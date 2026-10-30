using System.Diagnostics;
using System.Windows.Forms;

namespace Iprc1000Adapter;

/// <summary>
/// **宏播放器**：把一条 <see cref="Macros.Item"/> 的步骤序列执行出来（可多遍循环）。
///
/// 四条设计红线（都是踩过坑之后收的）：
///
/// ① **绝不自己造注入** —— 全部走 <see cref="KeyFilter.InjectTransient"/>，
///    这样"整份报告快照 / 孤儿键回收 / 800ms 看门狗"这三道保险才管得住宏发的键。
///    自己发键的话，一次异常就是**永久卡键**，而且只在用户机器上偶发。
///
/// ② **必须能被中断** —— 序列里有 wait 时用户按错了只能干等是灾难。
///    规则：**按任意遥控器键 = 停止**（见 <see cref="StopByKey"/>）。
///    不限于触发键：用户忘了绑哪个键时，只放行触发键等于没有出口。
///
/// ③ **必须有双重预算兜底** —— 单遍有预算（某步实现卡住要能停），
///    整体还有硬时长上限（无限循环忘了停不能一直占着注入层）。
///
/// ④ **必须留下可定位的痕迹** —— 第几遍、第几步、哪一步失败、为什么停，
///    全部落盘。KeymouseGo 的 issue 里一半是"为什么不动"，根因就是黑盒。
///
/// ★ 线程必须是 **STA**：`Clipboard` 在 MTA 线程上会直接抛
///   `ThreadStateException`（"当前线程必须为单线程单元"）。
/// </summary>
internal static class MacroPlayer
{
    /// <summary>按键步骤用的固定登记名。用固定名而不是每步不同，是为了兜底时一句就能清干净</summary>
    private const string BidKey = "__mac_k";
    /// <summary>粘贴步骤的 Ctrl+V 单独用一个登记名，方便中断时精确释放</summary>
    private const string BidPaste = "__mac_v";

    private static readonly object Gate = new();
    private static KeyFilter? _filter;
    private static Thread? _thread;
    private static volatile bool _cancel;
    private static int _runningNum;

    public static bool IsRunning { get; private set; }
    public static int RunningNum => _runningNum;

    /// <summary>当前第几遍（1 起）；只在运行中有意义</summary>
    public static int Round { get; private set; }
    /// <summary>总遍数：0 = 一直循环，N = N 遍，1 = 触发一次</summary>
    public static int TotalRounds { get; private set; }

    private static Stopwatch? _sw;
    /// <summary>
    /// 已运行时长。★ **没在跑就返回 0** —— 否则 `getState` 每 2 秒读到的会是
    /// 上一次宏"停在多少毫秒"，界面上看起来像还在跑。
    /// </summary>
    public static long ElapsedMs
    {
        get { try { return IsRunning ? (_sw?.ElapsedMilliseconds ?? 0) : 0; } catch { return 0; } }
    }

    /// <summary>停止原因（中断/超时/失败），给界面和日志用</summary>
    public static string StopReason { get; private set; } = "";

    /// <summary>开始/结束/中断的**一次性通知** —— 宿主订阅后弹托盘气泡。
    /// 不能只靠界面 toast：用户很多时候在别的程序里，根本看不到 WorkBuddy 的界面。</summary>
    public static event Action<string>? Notify;

    /// <summary>粘贴后要不要把剪贴板还原成原来的内容（默认要 —— 不还原等于污染用户剪贴板）</summary>
    public static bool RestoreClipboard { get; set; } = true;

    /// <summary>发出 Ctrl+V 之后等多久才还原剪贴板。太短会粘成旧内容（粘贴是异步的）</summary>
    public static int PasteDelayMs { get; set; } = 400;

    /// <summary>单遍预算余量：固定加这么多，覆盖启动开销和最后一步的收尾</summary>
    public static int BudgetPadMs { get; set; } = 4000;

    /// <summary>
    /// 整体硬时长上限。**无限循环的唯一刹车** ——
    /// 用户设了"一直循环"然后走开，没有这条就会一直占着注入层（遥控器全部失灵）。
    /// </summary>
    public static int MaxTotalMs { get; set; } = 600_000;   // 10 分钟

    /// <summary>每轮之间的间隔。避免"零延迟空转"把 CPU 和注入层打满</summary>
    public static int LoopGapMs { get; set; } = 300;

    public static void Attach(KeyFilter f) => _filter = f;

    /// <summary>
    /// 这个按键是不是「正在播放的那个宏」的触发键。
    /// 仅用于日志区分"按的是触发键还是别的键"，**不再作为放行条件**（任意键都能中断）。
    /// </summary>
    public static bool IsTrigger(string bid)
    {
        try
        {
            var mv = Keymaps.Get(bid);
            if (mv.Length != 1 || !Macros.IsRef(mv[0])) return false;
            return Macros.RefBase - mv[0] == _runningNum;
        }
        catch { return false; }
    }

    /// <summary>
    /// 按下触发键时调用：没在跑就开跑；正在跑同一个（或别的）就停。
    /// 注意这里**只处理按下**（抬起什么都不做）—— 宏是"点一下开始"，不是"按住才跑"。
    /// </summary>
    public static bool Toggle(int num)
    {
        if (IsRunning)
        {
            // 正常路径下 ApplyButton 的守卫已经把它转成 StopByKey 了；
            // 走到这里说明从别的入口进来的，保守按中断处理。
            _cancel = true;
            StopReason = "已中断";
            return true;
        }
        return Start(num);
    }

    /// <summary>
    /// **宏播放期间按任意遥控器键 = 停止**。
    ///
    /// 为什么不是"只放行触发键"：用户配完宏很可能忘了绑在哪个键上，
    /// 那时候唯一的出口就应该是"随便按一个"。按键没反应比中断更容易被当成故障。
    /// </summary>
    public static void StopByKey(string bid)
    {
        if (!IsRunning) return;
        _cancel = true;
        var label = SafeLabel(bid);
        StopReason = $"已被「{label}」打断";
        Log($"MACRO {StopReason}");
    }

    private static string SafeLabel(string bid)
    {
        try { return Keys.LabelOf(bid); } catch { return bid; }
    }

    private static bool Start(int num)
    {
        var it0 = Macros.FindByNum(num);
        if (it0 is null) { Log($"MACRO {num} 找不到（可能已被删除）"); return false; }

        // ★★ 2026-10-30 修：**文本项被当成"没有步骤"拒绝了**。
        //   文本的设计是"只有一步 paste 的宏"，但它的内容在 `Text` 字段，`Steps` 是空的。
        //   而绑定值 `-1001..-1010`（文本）和 `-1101..`（宏）走的是**同一条**动作分支
        //   ⇒ 一个绑了文本的按键按下去，Start() 判 Steps==0 直接 return false，
        //   表现为"按了完全没反应"（日志默认还关着）。
        //   这里把文本项**转成一个真正的单步宏**再走后面的流程，其余逻辑零改动。
        var it = it0;
        if (it0.Kind == Macros.KindText)
        {
            if (string.IsNullOrEmpty(it0.Text))
            {
                Log($"TEXT {num} 内容是空的");
                Notify?.Invoke($"文本 {num} 是空的 —— 先在「文本」页签里填内容");
                return false;
            }
            it = new Macros.Item
            {
                Num = it0.Num,
                Name = it0.Name,
                Kind = Macros.KindSeq,      // 对播放器来说它就是一步宏
                Loop = 1,
                Steps = new List<Macros.Step>
                {
                    new() { Type = Macros.StepPaste, Slot = it0.Num, OnFail = "stop" },
                },
            };
        }
        if (it.Steps.Count == 0) { Log($"MACRO {num} 没有步骤"); return false; }

        lock (Gate)
        {
            _cancel = false;
            _runningNum = num;
            Round = 0;
            TotalRounds = it.Loop;
            StopReason = "";
            _sw = Stopwatch.StartNew();
            IsRunning = true;
        }

        var loopTxt = it.Loop == 1 ? "执行一次"
                    : it.Loop == 0 ? "一直循环（按任意遥控器键停）"
                    : $"循环 {it.Loop} 遍";
        Log($"MACRO {num}「{it.Name}」开始：{it.Steps.Count} 步 · {loopTxt}");
        Notify?.Invoke($"宏「{it.Name}」开始（{it.Steps.Count} 步 · {loopTxt}）");

        _thread = new Thread(() =>
        {
            string result;
            try { result = RunCore(it); }
            catch (Exception e)
            {
                result = "异常：" + e.Message;
                Safe.Log(e, $"宏 {num} 执行异常");
            }
            finally
            {
                ReleaseAll();
                IsRunning = false;
            }
            Log($"MACRO {num}「{it.Name}」{result}");
            Notify?.Invoke($"宏「{it.Name}」{result}");
        })
        {
            IsBackground = true,
            Name = "MacroPlayer",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        return true;
    }

    /// <summary>
    /// 同步跑一遍（界面「试运一遍」用）。
    /// ★ **强制只跑一遍**：试运行如果遵守 Loop=0 就会一直转，
    ///   用户在界面上点"试一下"结果停不下来是最坏的体验。
    /// </summary>
    public static string RunSync(int num)
    {
        var it = Macros.FindByNum(num);
        if (it is null) return "找不到这个宏";
        if (it.Steps.Count == 0) return "这个宏没有步骤";
        if (IsRunning) return "正在播放另一个宏，等它结束再试";

        var once = new Macros.Item { Num = it.Num, Name = it.Name, Loop = 1, Steps = it.Steps };

        string result = "";
        var t = new Thread(() =>
        {
            try { result = RunCore(once); }
            catch (Exception e) { result = "异常：" + e.Message; }
            finally { ReleaseAll(); }
        })
        {
            IsBackground = true,
            Name = "MacroTest",
        };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join(BudgetOf(once) + 2000);
        if (t.IsAlive) { _cancel = true; ReleaseAll(); return "试运行超时（已强制释放所有键）"; }
        return "试运行：" + result;
    }

    /// <summary>单独试一次「粘贴某文本组」（界面上的“试一下”）。同样走 STA 线程</summary>
    public static string PasteTest(int slot)
    {
        if (string.IsNullOrEmpty(Macros.TextOfSlot(slot))) return $"文本 {slot} 是空的";
        var ok = false;
        var why = "";
        var t = new Thread(() => { try { ok = DoPaste(slot, new[] { 0x11, 0x56 }, out why); } catch (Exception e) { why = e.Message; } })
        {
            IsBackground = true,
            Name = "MacroPasteTest",
        };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join(PasteDelayMs + 2000);
        if (t.IsAlive) return "粘贴超时（剪贴板被别的程序占用？）";
        return ok ? $"已把文本 {slot} 粘贴到当前光标处" : $"文本 {slot} 粘贴失败：{why}";
    }

    private static void ReleaseAll()
    {
        try { _filter?.InjectTransient(BidKey, Array.Empty<int>(), false); } catch { }
        try { _filter?.InjectTransient(BidPaste, Array.Empty<int>(), false); } catch { }
    }

    // ── 执行 ───────────────────────────────────────────────────────────────

    private static string RunCore(Macros.Item it)
    {
        // ★ 计时器必须在**这里**建，不能依赖 Start() 里那个 ——
        //   RunSync（界面「试运一遍」）根本不走 Start，`_sw` 会是 null，
        //   后面那句 `_sw!.ElapsedMilliseconds` 就是空引用。
        var sw = Stopwatch.StartNew();
        _sw = sw;
        StopReason = "";

        var rounds = it.Loop <= 0 ? int.MaxValue : it.Loop;
        var totalCap = it.Loop == 0
            ? MaxTotalMs
            : Math.Min((long)BudgetOf(it) * Math.Max(it.Loop, 1) + 5000, MaxTotalMs);

        var done = 0;
        for (var r = 1; r <= rounds; r++)
        {
            if (_cancel) break;
            if (sw.ElapsedMilliseconds > totalCap) return $"超过总时长上限 {totalCap / 1000}s，已中止（完成 {done} 遍）";

            Round = r;
            // 第 1 遍逐步留痕；之后只记轮次 —— 否则循环 100 遍会产生几千行日志，反而没法看
            var verbose = r == 1;
            if (rounds > 1)
                Log($"MACRO 第 {r} 遍" + (rounds == int.MaxValue ? "（循环中，按任意遥控器键停）" : $"/{rounds}"));

            var fail = RunOnce(it, verbose);
            if (fail != null)
            {
                StopReason = fail;
                return done > 0 ? $"{fail}（完成 {done} 遍）" : fail;
            }
            done++;

            if (r < rounds && !SleepCancellable(LoopGapMs))
            {
                StopReason = "已中断";
                break;
            }
        }

        if (_cancel && StopReason.Length == 0) StopReason = "已中断";
        var tail = rounds == int.MaxValue ? $"，共 {done} 遍" : "";
        var ms = sw.ElapsedMilliseconds;
        return StopReason.Length > 0
            ? $"{StopReason}（完成 {done} 遍，{ms}ms）"
            : $"完成{tail}（{done} 遍 / {ms}ms）";
    }

    /// <summary>跑一遍。返回 null = 成功；否则是失败原因（要能指名到第几步）</summary>
    private static string? RunOnce(Macros.Item it, bool verbose)
    {
        var budget = BudgetOf(it);
        var sw = Stopwatch.StartNew();

        for (var i = 0; i < it.Steps.Count; i++)
        {
            var st = it.Steps[i];
            var no = i + 1;
            if (_cancel) return "已中断";
            if (sw.ElapsedMilliseconds > budget) return $"第 {no} 步之后超过单遍预算 {budget}ms";

            string? err = null;
            var why = "";
            switch ((st.Type ?? "").ToLowerInvariant())
            {
                case Macros.StepKeys:
                    if (verbose) Log($"MACRO {no}/{it.Steps.Count} 按键 {DescribeVks(st.Vks)}（按住 {st.HoldMs}ms）");
                    if (!DoKeys(st)) err = $"按键 {DescribeVks(st.Vks)} 注入失败";
                    break;

                case Macros.StepWait:
                    // 长等待要专门说一句，否则用户以为卡死了（KeymouseGo #124 就是这个抱怨）
                    if (verbose && st.Ms >= 800)
                        Log($"MACRO {no}/{it.Steps.Count} 等待 {st.Ms}ms（这段时间按任意遥控器键可中断）");
                    if (!SleepCancellable(Scaled(st.Ms))) return "已中断";
                    break;

                case Macros.StepPaste:
                    if (verbose) Log($"MACRO {no}/{it.Steps.Count} 粘贴文本 {st.Slot}"
                                   + (LooksCtrlV(st.PasteKey) ? "" : $"（粘贴键 {DescribeVks(st.PasteKey)}）"));
                    if (!DoPaste(st.Slot, st.PasteKey, out why)) err = why;
                    break;

                case Macros.StepClick:
                    if (verbose) Log($"MACRO {no}/{it.Steps.Count} 点击"
                                   + (st.Target == "coord" ? $"坐标 {st.X},{st.Y}" : "（图标）"));
                    if (!DoClick(st, out why)) err = why;
                    break;

                case Macros.StepMove:
                    if (verbose) Log($"MACRO {no}/{it.Steps.Count} 移动到 {st.X},{st.Y}");
                    if (!DoMove(st, out why)) err = why;
                    break;

                case Macros.StepDrag:
                    if (verbose) Log($"MACRO {no}/{it.Steps.Count} 拖拽 {st.X},{st.Y} → {st.X2},{st.Y2}（{st.Ms}ms）");
                    if (!DoDrag(st, out why)) err = why;
                    break;

                default:
                    // 不认识的步骤类型：**跳过并记日志**，不要静默 —— 静默丢步会让用户
                    // 以为"宏配错了"，却看不出是哪一步丢的。
                    Log($"MACRO 跳过未知步骤类型「{st.Type}」（第 {no} 步）");
                    break;
            }

            if (err != null)
            {
                // 失败策略：skip 要**明确记日志**，否则就是"跑完了但结果不对"，更难查
                if ((st.OnFail ?? "").Trim().ToLowerInvariant() == "skip")
                {
                    Log($"MACRO 第 {no} 步失败（已设置为跳过继续）：{err}");
                    continue;
                }
                return $"第 {no} 步 {err}";
            }
        }
        return null;
    }

    private static bool LooksCtrlV(int[] pk) =>
        pk is { Length: 2 } && pk[0] == 0x11 && pk[1] == 0x56;

    /// <summary>键盘组合键 → 可读文字（日志用）。不依赖界面那一套，避免日志里出现 0x11</summary>
    private static string DescribeVks(int[] vks)
    {
        if (vks is null || vks.Length == 0) return "（空）";
        try
        {
            var s = Keys.FormatSymbols(vks);
            return s.Length > 0 ? s : "[" + string.Join(",", vks) + "]";
        }
        catch { return "[" + string.Join(",", vks) + "]"; }
    }

    private static bool DoKeys(Macros.Step st)
    {
        var f = _filter;
        if (f is null) return false;
        // 按下 / 抬起可以分开录（对齐 KeymouseGo 的 `key down` / `key up`）
        var press = st.Press ?? "both";
        if (press == "up")
        {
            // 只抬起：把这一槽里按住的键全部放下（宏里的键共用 BidKey 一个槽）
            f.InjectTransient(BidKey, Array.Empty<int>(), false);
            return true;
        }
        if (press == "down")
        {
            // 只按下：登记并保持，等到后续 `up` 步骤或宏结束时的 ReleaseAll
            f.InjectTransient(BidKey, st.Vks, true);
            return true;
        }
        f.InjectTransient(BidKey, st.Vks, true);
        var held = SleepCancellable(Scaled(st.HoldMs));
        f.InjectTransient(BidKey, Array.Empty<int>(), false);
        return held;
    }

    // ── 鼠标类步骤（2026-10-30 录制功能加入）──────────────────────────────
    //
    // ★ 这里会**主动移动光标**。这与 MouseEngine 的"只用相对位移、绝不搬光标"不冲突：
    //   那条约束是给"鼠标模式"（连续跟手）立的，防止光标跳；
    //   宏里"移到指定位置"是用户**明确要求**的行为，必须用绝对定位。

    private static bool ClickButton(int btn, bool down) => btn switch
    {
        1 => MouseInjector.Right(down),
        2 => MouseInjector.Middle(down),
        _ => MouseInjector.Left(down),
    };

    /// <summary>
    /// 坐标换算：录制尺寸与实际屏幕一致时**原样使用**（零误差，最常见的同机场景）；
    /// 不一致才按比例缩放 —— 这时是近似值，所以「点击」默认走图标而不是坐标。
    /// </summary>
    private static (int x, int y) ScaleTo(Macros.Step st, bool endpoint)
    {
        var (_, _, vw, vh) = MouseInjector.VirtualScreen();
        var rawX = endpoint ? st.X2 : st.X;
        var rawY = endpoint ? st.Y2 : st.Y;
        if (st.Sw <= 0 || st.Sh <= 0 || (st.Sw == vw && st.Sh == vh)) return (rawX, rawY);
        return ((int)Math.Round(rawX * (double)vw / st.Sw),
                (int)Math.Round(rawY * (double)vh / st.Sh));
    }

    /// <summary>播放前的窗口校验：录制时在前台的那个窗口不在前台就停，避免点到别处</summary>
    private static string? WrongWindow(Macros.Step st)
    {
        if (string.IsNullOrEmpty(st.WinTitle)) return null;
        var cur = Recorder.ForegroundTitle();
        if (cur.Length == 0) return null;                 // 拿不到标题就不拦
        if (cur.Contains(st.WinTitle) || st.WinTitle.Contains(cur)) return null;
        return $"前台窗口是「{cur}」，不是录制时的「{st.WinTitle}」";
    }

    private static bool DoClick(Macros.Step st, out string why)
    {
        why = "";
        var target = st.Target ?? "icon";

        if (target == "icon")
        {
            // 图标匹配：找不到就停，**不退回坐标** —— 坐标点空会点到别的按钮上。
            // ★ 提示必须说清"这是什么、该怎么办"：实测用户加了「点击（图标）」却从没采集过图标，
            //   宏在第 1 步就停，日志默认又是关的 ⇒ 体感就是"按了完全没反应"。
            if (!KeyAct.ClickIconNow())
            {
                why = "没找到图标 —— 这是「截图匹配」，需要先在按键页采集图标；"
                    + "只想在鼠标当前位置点一下的话，把这一步的目标改成「在光标位置点一下」";
                return false;
            }
            return true;
        }

        if (target == "cursor")
        {
            // 当前位置点击：罗技 Lua 的 PressAndReleaseMouseButton 没有坐标参数，
            // 语义就是"点现在光标所在处"。不移动光标。
            if (!ClickButton(st.Button, true)) { why = "按下失败"; return false; }
            SleepCancellable(40);
            ClickButton(st.Button, false);
            return true;
        }

        if (WrongWindow(st) is { } bad) { why = bad; return false; }
        var (x, y) = ScaleTo(st, false);
        if (!MouseInjector.MoveToScreen(x, y)) { why = "移动光标失败"; return false; }
        SleepCancellable(30);
        if (!ClickButton(st.Button, true)) { why = "按下失败"; return false; }
        SleepCancellable(40);
        // ★ 抬起必须无条件执行：按下没抬起 = 系统里留着一个按住的键，
        //   用户接下来点什么都是拖拽。这一条比"这一步成功没有"重要得多。
        ClickButton(st.Button, false);
        return true;
    }

    private static bool DoMove(Macros.Step st, out string why)
    {
        why = "";
        var (x, y) = ScaleTo(st, false);
        if (!MouseInjector.MoveToScreen(x, y)) { why = "移动光标失败"; return false; }
        return true;
    }

    private static bool DoDrag(Macros.Step st, out string why)
    {
        why = "";
        if (WrongWindow(st) is { } bad) { why = bad; return false; }

        var (x1, y1) = ScaleTo(st, false);
        var (x2, y2) = ScaleTo(st, true);
        if (!MouseInjector.MoveToScreen(x1, y1)) { why = "移动光标失败"; return false; }
        SleepCancellable(40);
        if (!ClickButton(st.Button, true)) { why = "按下失败"; return false; }

        try
        {
            // 分步移动而不是瞬移：很多程序（尤其 Electron/WPF 的拖拽）只认
            // "按下期间发生过移动"，一次瞬移可能被当成没拖。
            var totalMs = Scaled(st.Ms);
            var n = Math.Clamp(totalMs / 15, 3, 40);
            for (var i = 1; i <= n; i++)
            {
                if (_cancel) { why = "已中断"; return false; }
                var t = (double)i / n;
                MouseInjector.MoveToScreen(
                    (int)Math.Round(x1 + (x2 - x1) * t),
                    (int)Math.Round(y1 + (y2 - y1) * t));
                SleepCancellable(Math.Max(1, totalMs / n));
            }
            return true;
        }
        finally
        {
            // 无论中断/异常/超时，都必须抬起 —— 否则会留一个永远按着的键
            ClickButton(st.Button, false);
        }
    }

    private static bool DoPaste(int slot, int[] pasteKey, out string why)
    {
        why = "";
        var text = Macros.TextOfSlot(slot);
        if (string.IsNullOrEmpty(text))
        {
            why = $"文本 {slot} 是空的";
            Log($"MACRO {why}，跳过粘贴");
            return false;
        }

        var f = _filter;
        if (f is null) { why = "注入器未就绪"; return false; }

        // ── 保存原剪贴板 ──
        string? old = null;
        var hadOld = false;
        try
        {
            if (Clipboard.ContainsText()) { old = Clipboard.GetText(); hadOld = true; }
        }
        catch { /* 剪贴板被别的进程占着：还原不了就算了，粘贴本身不受影响 */ }

        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception e)
        {
            why = "写剪贴板失败：" + e.Message;
            Log("MACRO " + why);
            return false;
        }

        // ── 发粘贴键（默认 Ctrl+V；终端里用户会改成 Ctrl+Shift+V）──
        var pk = pasteKey is { Length: > 0 } ? pasteKey : new[] { 0x11, 0x56 };
        f.InjectTransient(BidPaste, pk, true);
        SleepCancellable(60);
        f.InjectTransient(BidPaste, Array.Empty<int>(), false);
        SleepCancellable(PasteDelayMs);       // 等目标程序真正把内容读走

        // ── 还原剪贴板 ──
        // ★ 时序上必须放在"等"之后：设置剪贴板和读取剪贴板都是异步的，
        //   发完 Ctrl+V 立刻还原，很多程序（浏览器/Electron）会粘到旧内容。
        if (RestoreClipboard && hadOld)
        {
            try
            {
                if (string.IsNullOrEmpty(old)) Clipboard.Clear();
                else Clipboard.SetText(old!);
            }
            catch { }
        }
        return true;
    }

    /// <summary>
    /// 按「执行速度」缩放一个等待时长。100% = 原速；200% = 快一倍（时长减半）。
    ///
    /// ★ 只用于**步骤级**的等待（wait 的毫秒 / 按键按住时长 / 拖拽时长）。
    ///   内部固定延时（粘贴后等目标程序读剪贴板那 400ms）**不能**跟着缩，
    ///   否则会粘成旧内容 —— 那是个时序依赖，不是"快慢"问题。
    /// </summary>
    private static int Scaled(int ms)
    {
        if (ms <= 0) return 0;
        var pct = Math.Clamp(Settings.MacroSpeedPct, 10, 400);
        return (int)Math.Clamp(ms * 100L / pct, 1, 600_000);
    }

    /// <summary>可中断的等待：每 20ms 检查一次取消标志，被取消后立刻返回 false</summary>
    private static bool SleepCancellable(int ms)
    {
        if (ms <= 0) return !_cancel;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (_cancel) return false;
            var left = ms - (int)sw.ElapsedMilliseconds;
            try { Thread.Sleep(Math.Clamp(left, 1, 20)); } catch { }
        }
        return !_cancel;
    }

    /// <summary>
    /// 这条宏**跑一遍**正常该多久。超了就是卡住了，直接中止。
    /// 每步的估价故意放宽（截图匹配可能要一两秒），宁可多给时间，不要误杀正常执行。
    /// </summary>
    private static int BudgetOf(Macros.Item it)
    {
        var sum = 0L;
        foreach (var st in it.Steps)
        {
            sum += (st.Type ?? "").ToLowerInvariant() switch
            {
                Macros.StepKeys => st.HoldMs + 40,
                Macros.StepWait => st.Ms,
                Macros.StepPaste => 80 + PasteDelayMs,
                // 图标匹配可能要一两秒；坐标点击只是移动+按下
                Macros.StepClick => (st.Target ?? "icon") == "coord" ? 300 : 3000,
                Macros.StepMove => 80,
                Macros.StepDrag => st.Ms + 300,
                _ => 0,
            };
        }
        // ★ 预算必须跟着「执行速度」走：用户设 50%（慢一倍）时实际耗时会翻倍，
        //   预算不放大就会出现"正常执行却被判超时中止"。
        var pct = Math.Clamp(Settings.MacroSpeedPct, 10, 400);
        var total = (sum + BudgetPadMs) * 100L / pct;
        return (int)Math.Clamp(total, 1000, 600_000);
    }

    private static void Log(string msg)
    {
        try { EtwKeySource.AppendLog(msg); } catch { }
    }
}
