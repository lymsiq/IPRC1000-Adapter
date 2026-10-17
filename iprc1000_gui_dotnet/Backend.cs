using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Iprc1000Adapter;

/// <summary>前端可调用的后端：与 Python 版保持同一套方法与返回（JSON 字符串）。</summary>
internal sealed class Backend : IDisposable
{
    private readonly CableRouter _router = new();
    private Process? _bridge;
    private DateTime _bridgeStart;
    private readonly object _lock = new();

    private DateTime _probeAt = DateTime.MinValue;
    private object? _cableCache;
    private bool _cableReady, _micOk, _uhidReady;

    /// <summary>启动后自动拉起语音桥（环境就绪时），省得每次手动点（持久化）</summary>
    public bool AutoStartBridge
    {
        get => Settings.AutoStartBridge;
        set { Settings.AutoStartBridge = value; if (value) MaybeAutoStartBridge(); }
    }
    private DateTime _lastAutoStart = DateTime.MinValue;
    private (bool online, bool paired, List<object> hid, List<object> bth) _diagCache;

    public string LastKey { get; private set; } = "";
    public int LastVk { get; private set; }
    public DateTime LastKeyAt { get; private set; } = DateTime.MinValue;
    public string LastButton { get; private set; } = "";
    public bool LastButtonPressed { get; private set; }

    private KeyFilter? _filter;
    private EtwKeySource? _etwKeys;
    private string _keySource = "hook";   // hook | etw

    /// <summary>来源自动选择/安全兜底触发时的说明（给界面显示）</summary>
    public string? _keySourceGuard;

    public Backend()
    {
        _router.Start();   // 常驻：随时接收语音桥推来的 PCM
    }

    /// <summary>鼠标模式引擎：方向键按住拖动 = 移动（自适应增速）｜确认 = 左键｜info = 右键</summary>
    private readonly MouseEngine _mouse = new();

    /// <summary>模式变化通知（MainForm 用它更新托盘提示与气泡）</summary>
    public event Action<string, bool>? MouseModeChanged;

    public void AttachFilter(KeyFilter f)
    {
        _filter = f;
        _filter.InjectEnabled = true;
        // ★ 鼠标模式路由：**只有四个方向键**由鼠标引擎接管（返回 true = 已消费，不走键盘映射）。
        //   挂在 ApplyButton 这个"两种按键来源共用的唯一漏斗"上，所以 ETW / 钩子两种模式都成立。
        //
        //   ⚠ v1.2.4 起**不再接管 确认 / info**：鼠标模式已合并成一个配置组，
        //   组里"除方向键外全部沿用默认组的映射"（用户明确要求），所以 ok/info
        //   应该走映射表（默认 enter / Ctrl+Enter），想当鼠标左右键就在弹窗里配成
        //   「鼠标左键 / 鼠标右键」—— 那样"按住方向 + 按住确认 = 拖拽"照样成立。
        _filter.MouseModeRouter = (bid, pressed) =>
            (bid is "up" or "down" or "left" or "right") && _mouse.HandleButton(bid, pressed);
        // 语音键按下 → 立刻预热音频链路（提前打开 CABLE Input）。
        // 见 KeyFilter.VoiceKeyDown 的说明：不预热会丢掉"按住后立刻说的第一个字"。
        KeyFilter.VoiceKeyDown -= WarmAudio;   // 防重复订阅
        KeyFilter.VoiceKeyDown += WarmAudio;
        // 动作值「切换配置组」（方块键的出厂值）→ 切组 + 同步鼠标模式
        KeyFilter.SwitchGroupRequested -= CycleGroup;
        KeyFilter.SwitchGroupRequested += CycleGroup;
        // ★ 卡键看门狗 v2 的音频判据：语音键按住期间蓝牙 SCO 一定在传帧，
        // 「按着 + 静默 >3s」= UP 丢了 → 看门狗会强制放开。
        // 只认当前这个桥进程写的统计（pid 匹配），避免读到上一个实例的残留。
        _filter.AudioSilenceMs = () =>
        {
            var st = ReadBridgeStats();
            if (st == null) return null;
            if ((st["pid"]?.GetValue<int>() ?? -1) != SafePid()) return null;
            long v;
            try { v = st["silentMs"]?.GetValue<long>() ?? -1; } catch { return null; }
            // -1 = 桥起来了但**一帧都没收到过**。这不是"有信号"，而是彻头彻尾的没信号，
            // 必须折算成一个超大的静默时长，否则 `>3000` 判不成立、语音键永远不会被放开。
            return v < 0 ? long.MaxValue / 4 : v;
        };
        // ETW 按键来源常驻监听（被动、无副作用）；是否真正生效由按键来源模式决定
        StartProbeThread();
        _etwKeys = new EtwKeySource(EtwKeySource.DefaultPort, OnUsage);
        _etwKeys.Start();

    }

    /// <summary>统一的 usage 事件入口（ETW 与 HID 直读两条路共用）</summary>
    private void OnUsage(byte usage, bool down)
    {
        // 幽灵报告（0x0C）不当"当前操作"显示，免得盖住真实按键
        if (usage == 0x0C) return;
        LastKey = EtwKeySource.UsageToButton.TryGetValue(usage, out var n)
            ? Keys.LabelOf(n) : $"0x{usage:X2}";
        LastKeyAt = DateTime.UtcNow;

        // ★★★ 只有「按键来源＝ETW」时才在这里注入 ★★★
        //
        // 钩子模式下按键由 Raw Input 路径（KeyFilter.HandleRawInput）注入，而 ETW 采集器
        // 是**常驻监听**的 —— 若这里不加判断，一次物理按键会被注入两次
        // （Raw Input 一次 + ETW 一次），表现就是「按一次触发两次」。
        if (_keySource != "hook" && EtwKeySource.UsageToButton.TryGetValue(usage, out var bid))
            _filter?.ApplyButton(bid, down, "etw");
    }

    /// <summary>
    /// 立即解锁卡键：清空 held，并给**所有遥控器键位**补发一次 KEYUP。
    /// 用于「系统里某个键被锁在按下、一直自动重复」的紧急处理。
    /// </summary>
    public string ForceUnlock()
    {
        var n = _filter?.ClearHeld("手动强制解锁") ?? 0;
        // 再把遥控器全部键位都补一次 UP（不管当时在不在 held 里）
        var sent = 0;
        if (_filter != null)
        {
            foreach (var vk in Keys.VkToButtonId.Keys)
            {
                try { KeyFilter.InjectUp(new[] { (int)vk }); sent++; } catch { }
            }
        }
        return Env.Json(new { ok = true,
            message = $"已强制解锁：清空 {n} 个按住键，并给 {sent} 个遥控器键位补发了 KEYUP。" });
    }

    /// <summary>切换按键来源：hid = 直读 HID 设备（最低延迟）；etw = 从 BthPort ETW 读；hook = 内核钩子</summary>
    /// <summary>
    /// **按键来源自动选择 + 安全兜底**（每轮状态刷新都会跑）：
    ///
    /// - 设备**已不是键盘**（驱动换成 HID-compliant device）→ 自动切到 ETW 读键
    ///   （此时 Raw Input 根本收不到它，钩子模式等于聋了）
    /// - 设备**仍是键盘**却选了 ETW 模式 → 强制把钩子装回去
    ///   （否则遥控器按键会畅通无阻地打进前台窗口，就是"一直输出 m"）
    /// </summary>
    private void AutoSelectKeySource()
    {
        if (_filter == null) return;
        var stillKb = Env.RemoteStillKeyboard();

        if (!stillKb)
        {
            // 设备不是键盘 → 只能走 ETW（inbox 驱动下不存在可直读的 HID 接口）
            if (_keySource != "etw")
            {
                _keySource = "etw";
                _filter.SetSourceMode(true);
                _keySourceGuard = "设备已不是键盘 → 按键来源切到 ETW";
                DeviceLog("按键来源 → ETW（设备已不是键盘，延迟最低）");
            }
        }
        else if (stillKb && _keySource != "hook" && _filter.KeySourceIsEtw)
        {
            _keySource = "hook";
            _filter.SetSourceMode(false);
            _keySourceGuard = "设备仍是键盘（驱动未换）且模式是 ETW → 已自动启用钩子拦截，否则 m 会漏进应用";
            DeviceLog("按键来源 → 钩子拦截（设备仍是键盘，防止 m/i 漏进前台应用）");
        }
    }

    public string SetKeySource(string? mode)
    {
        var m = (mode ?? "hook").ToLowerInvariant();
        _keySource = m is "etw" ? m : "hook";
        _keySourceGuard = null;
        _filter?.SetSourceMode(_keySource != "hook");
        AutoSelectKeySource();
        var desc = _keySource switch
        {
            "etw" => "按键来源已切到 ETW（设备为非键盘时使用）。",
            _ => "按键来源已切回内核钩子（Raw Input + WH_KEYBOARD_LL 拦截）。",
        };
        return Env.Json(new { ok = true, message = desc });
    }

    public void NoteKey(int vk)
    {
        LastVk = vk;
        LastKey = Keys.VkToButton.TryGetValue(vk, out var n) ? n : Keys.VkLabel(vk);
        LastKeyAt = DateTime.UtcNow;
    }

    public void NoteButton(string bid, bool pressed)
    {
        // ★ v1.2.4 起这里**不再有方块键的特例**。
        //   以前方块键是"硬编码的特殊键"（行为由 `squareKeyAction` 决定，切组 or 切模式），
        //   现在它就是个**普通可绑定键**，出厂值 = 动作值「切换配置组」，
        //   由 KeyFilter 走到动作分派 → 回调 <see cref="CycleGroup"/>。
        //   好处：① 状态只有一个来源（活动组的 Kind）；② 用户想改绑就改绑。
        LastButton = bid;
        LastButtonPressed = pressed;
    }

    /// <summary>
    /// 切到下一个配置组，并**让鼠标模式跟着组走**。
    ///
    /// 为什么"切组"要顺带同步模式：v1.2.4 起鼠标模式 = 活动组是不是"鼠标组"，
    /// 所以切到鼠标组就自动开、切走就自动关。这是**唯一**的模式来源。
    /// **按下即生效**（不等抬起）—— 遥控器丢 UP 是既知会发生的。
    /// </summary>
    private void CycleGroup()
    {
        var g = Keymaps.NextGroup();
        SyncMouseWithGroup();
        _groupToast = _mouse.Enabled ? $"已切换到「{g}」（鼠标模式）" : $"已切换到配置组「{g}」";
        _groupToastAt = DateTime.UtcNow;
        MouseEngine.Log($"切配置组 → 「{g}」（鼠标模式={_mouse.Enabled}）");
    }

    /// <summary>
    /// **鼠标模式的唯一判据**：活动配置组的 Kind 是不是 "mouse"。
    ///
    /// 以前"模式"和"配置组"各存一份状态，于是会出现
    /// "在鼠标模式组里但模式还是键盘"这种谁都说不清的组合（用户在实测里明确要求合并）。
    /// </summary>
    private void SyncMouseWithGroup()
    {
        var want = Keymaps.MouseGroupActive;
        if (_mouse.Enabled == want) return;
        var msg = _mouse.SetEnabled(want);
        MouseEngine.Log($"鼠标模式跟随配置组 → {(want ? "开" : "关")}（组「{Keymaps.Current.Name}」）");
        try { MouseModeChanged?.Invoke(msg, _mouse.Enabled); } catch { }
    }

    /// <summary>
    /// 界面「实时校准区」按 ~20 Hz 轮询：返回**自上次调用以来的位移** + **光标真实位置** +
    /// 虚拟桌面尺寸。界面据此把光点映射到光标的**真实位置**（一台小地图），
    /// 而不是"从一个固定点开始累加位移" —— 后者会漂，而且和真实光标对不上。
    /// </summary>
    public string TakeMouseSample()
    {
        var s = _mouse.TakeSample();
        return Env.Json(new
        {
            dx = Math.Round(s.Dx, 1),
            dy = Math.Round(s.Dy, 1),
            px = Math.Round(s.Px, 1),
            pxPerSec = s.Sec > 0.001 ? Math.Round(s.Px / s.Sec) : 0,
            x = s.X,
            y = s.Y,
            vx = s.Vx,
            vy = s.Vy,
            vw = s.Vw,
            vh = s.Vh,
            mode = _mouse.Enabled ? "mouse" : "keyboard",
        });
    }

    /// <summary>界面上「重新自校准」按钮：量一次增益并回报结果</summary>
    public string CalibrateMouse()
    {
        _mouse.Calibrate();
        return _mouse.LastNote ?? "自校准完成。";
    }

    /// <summary>
    /// 切换/设置鼠标模式（界面与桥接调）。<paramref name="v"/>: "on"/"off"/"toggle"。
    ///
    /// ★ v1.2.4 起"模式"就是"活动配置组"，所以这个接口实际做的是**切组**：
    ///   开 = 切到「鼠标模式」组；关 = 切回上一个非鼠标组（记不住就回第 0 组）。
    ///   保留这个接口是为了不破坏既有调用方（界面按钮 / 桥接命令）。
    /// </summary>
    public string SetMouseMode(string? v)
    {
        var want = v?.Trim().ToLowerInvariant() switch
        {
            "on" or "true" or "1" => true,
            "off" or "false" or "0" => false,
            _ => !_mouse.Enabled,
        };
        var mi = Keymaps.MouseGroupIndex;
        if (mi < 0) return "找不到「鼠标模式」配置组。";
        if (want)
        {
            if (!Keymaps.MouseGroupActive) _lastNonMouseGroup = Keymaps.ActiveIndex;
            Keymaps.SwitchTo(mi);
        }
        else if (Keymaps.MouseGroupActive)
        {
            var back = _lastNonMouseGroup;
            if (back == mi || back < 0 || back >= Keymaps.Groups.Count) back = mi == 0 ? 1 : 0;
            Keymaps.SwitchTo(back);
        }
        SyncMouseWithGroup();
        var msg = _mouse.Enabled
            ? $"已切到配置组「{Keymaps.Current.Name}」—— 鼠标模式已开启。"
            : $"已切回配置组「{Keymaps.Current.Name}」—— 键盘模式。";
        _groupToast = msg;
        _groupToastAt = DateTime.UtcNow;
        return msg;
    }

    /// <summary>进鼠标组之前待在哪个组 —— "关掉鼠标模式"要能回到原处</summary>
    private int _lastNonMouseGroup = 0;


    /// <summary>应用鼠标参数（界面滑条调）。参数是一个 JSON 对象字符串。</summary>
    public string SetMouseConfig(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "没有收到参数。";
        try
        {
            using var doc = JsonDocument.Parse(json);
            var msg = _mouse.ApplyConfig(doc.RootElement);
            MouseEngine.Log("参数已更新：" + msg);
            return msg;
        }
        catch (Exception e) { return "参数解析失败：" + e.Message; }
    }

    public string SetInject(string? on)
    {
        var want = on is "true" or "True" or "1" or "yes";
        if (_filter != null) _filter.InjectEnabled = want;
        return Env.Json(new { ok = true, message = want ? "已开启快捷键注入。" : "已关闭快捷键注入（仅拦截按键）。" });
    }

    // ───────── 状态 ─────────

    private Thread? _probeThread;
    private volatile bool _disposed;

    /// <summary>
    /// 起一个后台线程定时刷新探针。
    ///
    /// 原来探针是在 `GetState()` 里同步跑的，而 `GetState()` 由界面线程通过 JS 桥调用 ——
    /// 每轮轮询都要扫注册表（音频端点、Enum\HID、设备诊断），**界面会卡住**，
    /// 启动时尤其明显（一两秒的顿）。现在：界面只读缓存，后台线程负责刷新。
    /// </summary>
    private void StartProbeThread()
    {
        if (_probeThread != null) return;
        _probeThread = new Thread(() =>
        {
            while (!_disposed)
            {
                try { RefreshProbes(true); } catch { }
                try { RecheckDeviceDriver(); } catch { }
                for (var i = 0; i < 80 && !_disposed; i++)     // 8 秒一轮
                {
                    Thread.Sleep(100);
                    // 设备变更事件把复查间隔压到 ≤500ms；否则每 2 秒兜底复查一次。
                    // ★ 这些都在**后台线程**上：SetDeviceDriver 要 1~2 秒，
                    //   放到 UI 线程（WndProc 会调 OnDeviceMayChanged）会把界面冻住。
                    if ((_deviceRecheckRequested && i % 5 == 4) || i % 20 == 19)
                    {
                        _deviceRecheckRequested = false;
                        try { RecheckDeviceDriver(); } catch { }
                    }
                }
            }
        }) { IsBackground = true, Name = "iprc1000-probe", Priority = ThreadPriority.BelowNormal };
        _probeThread.Start();
    }

    /// <summary>
    /// 启动后的异步初始化：探针 → 自动切驱动 → 拉起语音桥。
    /// 全部放在后台线程，**界面先显示、不阻塞**（这是"启动后卡一两秒"的解法）。
    /// </summary>
    public void InitAsync()
    {
        try
        {
            // ★ 启动兜底：清掉上一次异常退出（例如被强杀）残留的「按着不放」的注入键。
            //   最要命的是右 Alt(AltGr) —— 卡住会让字母键输出特殊字符、中文输入法失效。
            try { KeyFilter.ReleaseAllInjected(); } catch { }
            RefreshProbes(true);
            AutoSwitchDriver();
            try { AutoSelectKeySource(); } catch { }
            try { MaybeAutoStartBridge(); } catch { }
        }
        catch { }
    }

    /// <summary>
    /// 启动时自动把遥控器驱动切成"非键盘"。
    ///
    /// 为什么：设备当键盘时，语音键会漏 `m`、幽灵报告会漏 `i`、拔电池还会把键锁在
    /// 「按下」持续刷屏；切成 HID-compliant device 后系统根本不产生这些 VK，从根上消失。
    /// 该操作约 1~2 秒，**必须在后台做**，否则界面会卡住。
    /// </summary>
    private void AutoSwitchDriver()
    {
        if (!Settings.AutoSwitchDriver) return;
        if (!Env.IsAdmin()) { _driverNote = "需要管理员权限才能自动切换驱动"; return; }
        if (!Env.RemoteStillKeyboard())
        {
            _driverNote = null;
            DeviceLog("启动检查：遥控器已是非键盘设备 ✓");
            return;
        }
        var (ok, msg) = Env.SetDeviceDriver(false);
        _driverNote = ok ? "启动时已自动把遥控器驱动切成「非键盘」（零 m）" : $"自动切驱动失败：{msg}";
        DeviceLog(ok ? "✓ 启动时自动把遥控器驱动切成「非键盘」"
                     : $"★ 启动时自动切驱动失败：{msg}");
        _probeAt = DateTime.MinValue;
        RefreshProbes(true);
    }

    /// <summary>自动切驱动的结果说明（给界面显示）</summary>
    private string? _driverNote;

    /// <summary>设备变更事件请求"立刻复查"（WndProc → OnDeviceMayChanged 置位，探针线程消费）</summary>
    private volatile bool _deviceRecheckRequested;
    private DateTime _nextDriverCheck = DateTime.MinValue;
    private DateTime _driverBackoffUntil = DateTime.MinValue;
    private int _driverSwitchCount;              // 本次运行里"重连后自动切回"成功了几次

    /// <summary>
    /// 设备/驱动生命周期日志（写 key-events.log，前缀 `DEV`）。
    /// 以前驱动切换**完全静默** —— 事后根本查不出"什么时候变的、变了几次、为什么没生效"。
    /// </summary>
    private static void DeviceLog(string msg)
    {
        try { EtwKeySource.AppendLog("DEV  " + msg); } catch { }
    }

    /// <summary>
    /// 复查遥控器的驱动绑定 —— **蓝牙重连之后它会自己退回键盘，必须再看一遍**。
    ///
    /// 为什么必须复查（2026-10-16 排查确认）：
    ///   蓝牙重连时 Windows 会**销毁旧的 HID 子设备实例、建一个新的**
    ///   （注册表实例后缀 `&N&` 递增，实测 `&2&` → `&4&`）。
    ///   我们那次"换成非键盘"的驱动绑定挂在**旧实例**上，已随实例一起消失；
    ///   新实例重新做驱动匹配，可选只有 inbox INF → `keyboard.inf`
    ///   （靠兼容 ID `HID_DEVICE_SYSTEM_KEYBOARD`）胜出 → **又变回键盘**。
    ///   而按键来源此时仍是 ETW（ETW 模式下钩子不装），于是 m / i 会直接漏进前台应用
    ///   —— 老问题复发。以前**只能靠重启程序**或手点界面按钮才会好。
    ///
    /// 两步（顺序重要）：
    ///   ① 先调 AutoSelectKeySource() 兜住 —— 设备是键盘时它会自动切回钩子模式拦截。
    ///      这一步**不依赖驱动切换是否成功**，是立刻生效的保护。
    ///   ② 再把驱动切回非键盘；成功后 AutoSelectKeySource() 把来源切回 ETW（延迟更低）。
    ///
    /// 后退策略：切了但没生效（设备刚好睡着之类）→ 10 分钟内不再重试，
    /// 绝不反复重枚举把设备搞乱；这期间由钩子模式兜底，不会漏 m。
    /// ⚠ 本方法会阻塞 1~2 秒（真的重装驱动），**只能在探针线程上调**，不可在 UI 线程调。
    /// </summary>
    private void RecheckDeviceDriver()
    {
        if (_disposed) return;
        var now = DateTime.UtcNow;
        if (now < _nextDriverCheck) return;
        _nextDriverCheck = now.AddMilliseconds(500);
        if (!Env.RemotePresent()) return;               // 遥控器不在，什么都别做

        var stillKb = Env.RemoteStillKeyboard();
        AutoSelectKeySource();                          // ① 保护优先（幂等：只在需要时才真切）

        if (!stillKb) return;
        if (!Settings.AutoSwitchDriver) return;
        if (now < _driverBackoffUntil) return;
        if (!Env.IsAdmin())
        {
            _driverBackoffUntil = now.AddMinutes(10);
            DeviceLog("★ 遥控器退回键盘，但没有管理员权限、无法自动切回（请以管理员身份运行）");
            return;
        }

        var (ok, msg) = Env.SetDeviceDriver(false);
        _driverSwitchCount++;
        if (ok && !Env.RemoteStillKeyboard())
        {
            AutoSelectKeySource();                      // 成功 → 来源切回 ETW
            RefreshProbes(true);
            _driverNote = "检测到遥控器重连后退回键盘，已自动切回「非键盘」。";
            DeviceLog($"✓ 重连后自动切回非键盘驱动（本次运行第 {_driverSwitchCount} 次）");
            _driverBackoffUntil = now.AddSeconds(30);
        }
        else
        {
            _driverBackoffUntil = now.AddMinutes(10);
            _driverNote = "遥控器退回键盘且自动切换未生效，已启用钩子拦截兜底。";
            DeviceLog($"★ 重连后自动切驱动未生效（{msg}）—— 10 分钟内不再重试；" +
                      "当前由钩子模式兜底拦截（不会漏 m）");
        }
    }

    private void RefreshProbes(bool force)
    {
        if (!force && _cableCache != null && (DateTime.UtcNow - _probeAt).TotalSeconds < ProbeTtlSec) return;
        _cableCache = Env.CableState();
        var c = (dynamic)_cableCache;
        _cableReady = c.ready;
        _micOk = c.defaultMic;
        _micDefaultName = c.defaultMicName;
        WatchDefaultMic();
        // ★ 不要在这里 EnsureReady()：它会创建虚拟 HID 设备，每轮轮询都做一次代价很大。
        //   只在驱动版本足够（>=1）时认为就绪，真正的设备在第一次注入时惰性创建。
        _uhidReady = WinUhidInjector.ProbeDriver();
        _diagCache = Env.RemoteDiag();
        _probeAt = DateTime.UtcNow;
    }

    // ───────── 默认录音设备看门狗 ─────────
    //
    // 为什么需要：输入法/通话软件读的是**系统默认录音设备**。VB-CABLE 的 CABLE Output
    // 本来由部署脚本设成默认，但 Windows 会在**蓝牙耳机/耳麦连接或断开、设备热插拔**时
    // 自作主张把默认录音设备切走。一旦切走：
    //   · 输入法打开的是那个刚出现、还没就绪的设备 → 弹「录音启动失败」
    //   · 就算打开成功，读到的也不是我们的音频 → 通话波形没信号
    // 而本程序此前**既检测不到**（旧检测只判断"CABLE Output 是否存在"，不是"是否为默认"）
    // **也不会自动改回来**（SetDefaultMic 只在手动点「一键完成全部」时跑）。
    // 现在：发现漂移就记日志（把被切到哪个设备也记下来），并按 1 分钟一次的节奏改回来。

    private string? _micDefaultName;
    private DateTime _lastMicFix = DateTime.MinValue;

    /// <summary>漂移检测 + 自动恢复（每轮探针跑一次）</summary>
    private void WatchDefaultMic()
    {
        var now = _micDefaultName ?? "?";
        if (now == _lastMicSeen) return;
        _lastMicSeen = now;
        if (now.ToLowerInvariant().Contains("cable output"))
        {
            CableRouter.Log($"[MIC] 默认录音设备 = {now}（正确）");
            return;
        }
        CableRouter.Log($"[MIC] ⚠ 默认录音设备被改成了「{now}」，不是 CABLE Output —— " +
                        "输入法/通话软件会读错设备（表现为「录音启动失败」或通话无波形）");
        // 自动改回（每 60 秒最多一次，避免和系统反复拉锯）
        if (!Settings.AutoKeepDefaultMic) return;
        if (!Env.IsAdmin()) { CableRouter.Log("[MIC] 需要管理员权限才能改回，已跳过"); return; }
        if ((DateTime.UtcNow - _lastMicFix).TotalSeconds < 60) return;
        _lastMicFix = DateTime.UtcNow;
        var (ok, msg) = Env.SetDefaultMic();
        CableRouter.Log(ok ? "[MIC] 已自动改回 CABLE Output" : $"[MIC] 自动改回失败：{msg}");
    }

    private string? _lastMicSeen;

    private bool BridgeRunning => _bridge != null && !_bridge.HasExited;

    /// <summary>环境就绪就自动把语音桥拉起来（每 10 秒最多试一次）</summary>
    private void MaybeAutoStartBridge()
    {
        if (!AutoStartBridge) return;
        // ★ 僵死也要重启：进程还在但已停止上报（例如被另一个实例掐掉了 ETW 会话）。
        //   以前只判 "!HasExited"，这种"活着但聋了"的状态永远不会被拉起。
        if (BridgeRunning && !BridgeZombie) return;
        if (!Env.IsAdmin() || !_cableReady) return;
        if ((DateTime.UtcNow - _lastAutoStart).TotalSeconds < 10) return;
        _lastAutoStart = DateTime.UtcNow;
        if (BridgeRunning) StopBridge();      // 先清掉僵死的那个
        StartBridge();
    }

    /// <summary>语音键按下时预热声卡（由 KeyFilter.VoiceKeyDown 回调）</summary>
    private void WarmAudio()
    {
        try { _router.WarmUp(); } catch { }
    }

    public string GetState()
    {
        AutoSelectKeySource();     // 每轮状态刷新：自动选来源 + 安全兜底
        RefreshProbes(false);
        MaybeAutoStartBridge();
        // 映射来自当前配置组（Keymaps）
        var items = Keys.Table.Select(b =>
        {
            var vks = Keymaps.Get(b.Id);
            return new
            {
                id = b.Id,
                label = b.Label,          // 遥控器上印的字符/图标（界面主显示）
                name = b.Name,            // 中文说明（仅悬停提示）
                hid = b.Hid,
                cap = b.Label,
                vks,
                binding = Keys.Format(vks),
                sym = Keys.FormatSymbols(vks),                 // 符号化组合键（列表右侧显示）
                rect = Keys.Rects.TryGetValue(b.Id, out var rc)
                    ? new { cx = rc.Cx, cy = rc.Cy, w = rc.W, h = rc.H } : null,
                img = new { w = Keys.ImgW, h = Keys.ImgH },
            };
        }).ToList();

        bool fresh = (DateTime.UtcNow - LastKeyAt).TotalSeconds < 4;
        bool connected = _diagCache.online;

        var state = new JsonObject
        {
            ["admin"] = Env.IsAdmin(),
            // 版本与构建时间：界面右下角显示，用来一眼确认"手上跑的是哪一版"
            ["version"] = Env.AppVersion,
            ["buildStamp"] = Env.BuildStamp,
            ["versionLine"] = Env.VersionLine,
            ["device"] = new JsonObject
            {
                ["connected"] = connected,
                ["paired"] = _diagCache.paired,
                ["detail"] = new JsonObject
                {
                    ["hid"] = JsonSerializer.SerializeToNode(_diagCache.hid),
                    ["bthenum"] = JsonSerializer.SerializeToNode(_diagCache.bth),
                    ["paired"] = _diagCache.paired,
                    ["online"] = connected,
                },
                ["lastKey"] = fresh ? LastKey : (connected ? (LastKey.Length > 0 ? LastKey : "—") : null),
                ["lastState"] = fresh ? "已翻译" : (connected ? "已连接" : "待机"),
            },
            ["keys"] = new JsonObject
            {
                ["items"] = JsonSerializer.SerializeToNode(items),
                ["configured"] = items.Count(i => i.vks.Length > 0),
                ["total"] = items.Count,
            },
            ["reg"] = JsonSerializer.SerializeToNode(Env.RegState()),
            ["cable"] = JsonSerializer.SerializeToNode(_cableCache),
            ["bridge"] = BridgeStatus(),
            ["router"] = JsonSerializer.SerializeToNode(_router.Status()),
            ["inject"] = _filter?.InjectEnabled ?? false,
            ["winuhid"] = JsonSerializer.SerializeToNode(WinUhidInjector.Status()),
            ["setup"] = SetupStatus(),
            ["stuckGuard"] = new JsonObject
            {
                ["unlockCount"] = UnlockCount,
                ["lastUnlock"] = LastUnlock,
                ["watchdogMs"] = KeyFilter.HoldTimeoutMs,
            },
            ["bind"] = new JsonObject
            {
                ["active"] = _bindBid != null,
                ["bid"] = _bindBid,
                ["capturedVk"] = _filter?.CapturedVk ?? 0,
                ["capturedName"] = _filter is { CapturedVk: > 0 }
                    ? Keys.VkLabel(_filter.CapturedVk) : "",
            },
            ["autoRun"] = IsAutoRun(),
            ["startMinimized"] = Settings.StartMinimized,
            ["autoStart"] = AutoStartBridge,
            ["autoSwitchDriver"] = Settings.AutoSwitchDriver,
            ["autoKeepMic"] = Settings.AutoKeepDefaultMic,
            // 当前真正的默认录音设备（用来一眼看出有没有被系统改走）
            ["defaultMicName"] = _micDefaultName ?? "?",
            ["groups"] = new JsonObject
            {
                ["active"] = Keymaps.ActiveIndex,
                ["items"] = new JsonArray(Keymaps.Groups.Select(g => (JsonNode)new JsonObject
                {
                    ["name"] = g.Name,
                    // ★ kind 必须给界面：鼠标模式 = "活动组是不是鼠标组"，
                    //   界面要靠它显示"鼠标移动"、拼按钮文案（v1.2.4）
                    ["kind"] = g.Kind,
                    ["configured"] = g.Map.Count(kv => kv.Value.Length > 0),
                }).ToArray()),
            },
            ["groupToast"] = (DateTime.UtcNow - _groupToastAt).TotalSeconds < 4 ? _groupToast : null,
            ["driverNote"] = _driverNote,
            ["mouse"] = JsonSerializer.SerializeToNode(_mouse.Status()),
            ["keysource"] = new JsonObject
            {
                ["mode"] = _keySource,
                ["stillKeyboard"] = Env.RemoteStillKeyboard(),
                ["guard"] = _keySourceGuard,
                ["etw"] = JsonSerializer.SerializeToNode(_etwKeys?.Status()),
                ["logPath"] = EtwKeySource.EventsLogPath,
                ["hookActive"] = _filter is { Running: true, KeySourceIsEtw: false },
            },
            ["swallow"] = new JsonObject
            {
                ["running"] = _filter?.Running ?? false,
                ["deviceSeen"] = _filter?.DeviceSeen ?? false,
                ["swallowed"] = _filter?.Swallowed ?? 0,
                ["passed"] = _filter?.Passed ?? 0,
                ["error"] = _filter?.LastError,
                ["sinceLastInputMs"] = _filter?.SinceLastInputMs ?? -1,
                ["spuriousLeaked"] = _filter?.SpuriousLeaked ?? 0,
                ["lastCleared"] = _filter?.LastCleared,
                ["lastButton"] = LastButton,
                ["pressed"] = LastButtonPressed,
            },
        };
        return state.ToJsonString();
    }

    // ───────── 动作 ─────────

    public string ApplyRegistry()
    {
        var (ok, msg) = Env.ApplyRegistry();
        return Env.Json(new { ok, message = msg });
    }

    public string Reboot()
    {
        try
        {
            Process.Start(new ProcessStartInfo("shutdown", "/r /t 8 /c \"IPRC1000 Adapter: 重启以加载蓝牙 ETW 配置\"")
            { UseShellExecute = false, CreateNoWindow = true });
            return Env.Json(new { ok = true, message = "系统将在 8 秒后重启…" });
        }
        catch (Exception e) { return Env.Json(new { ok = false, message = $"重启失败：{e.Message}" }); }
    }

    public string InstallCable()
    {
        var (ok, msg) = Env.InstallCable();
        _probeAt = DateTime.MinValue;
        return Env.Json(new { ok, message = msg });
    }

    public string SetDefaultMic()
    {
        var (ok, msg) = Env.SetDefaultMic();
        _probeAt = DateTime.MinValue;
        return Env.Json(new { ok, message = msg });
    }

    // ─────────── 配置组 ───────────
    public string SwitchGroup(string? idx)
    {
        var name = Keymaps.SwitchTo(int.TryParse(idx, out var i) ? i : 0);
        SyncMouseWithGroup();
        return Env.Json(new { ok = true, message = $"已切换到配置组「{name}」"
            + (Keymaps.MouseGroupActive ? "（鼠标模式已开启）" : "") + "。" });
    }

    public string NewGroup()
    {
        var name = Keymaps.Duplicate();
        SyncMouseWithGroup();
        return Env.Json(new { ok = true, message = $"已复制当前映射并新建配置组「{name}」。" });
    }

    public string RenameGroup(string? idx, string? name)
    {
        var n = Keymaps.Rename(int.TryParse(idx, out var i) ? i : 0, name ?? "");
        return Env.Json(new { ok = true, message = $"配置组已重命名为「{n}」。" });
    }

    public string DeleteGroup(string? idx)
    {
        var (ok, msg) = Keymaps.Delete(int.TryParse(idx, out var i) ? i : 0);
        return Env.Json(new { ok, message = msg });
    }

    /// <summary>导出当前配置组（返回 JSON 文本，由界面存文件）</summary>
    public string ExportGroup() => Keymaps.Export();

    public string ImportGroup(string? json)
    {
        var (ok, msg) = Keymaps.Import(json ?? "");
        return Env.Json(new { ok, message = msg });
    }

    public string SaveKey(string? bid, string? text)
    {
        if (string.IsNullOrEmpty(bid)) return Env.Json(new { ok = false, message = "缺少按键 id" });
        if (!Keys.Table.Any(x => x.Id == bid))
            return Env.Json(new { ok = false, message = $"未知按键 {bid}" });
        int[] vks;
        try { vks = Keys.Parse(text ?? ""); }
        catch (ArgumentException e) { return Env.Json(new { ok = false, message = e.Message }); }
        Keymaps.Set(bid, vks);                       // 写进**当前配置组**
        var name = Keys.Table.FirstOrDefault(x => x.Id == bid)?.Name ?? bid;
        var shown = Keys.FormatSymbols(vks);
        return Env.Json(new { ok = true,
            message = $"「{name}」已设为 {(shown.Length > 0 ? shown : "不执行")}（配置组「{Keymaps.Current.Name}」）" });
    }

    public string ResetKeys()
    {
        var n = Keymaps.ResetCurrent();
        return Env.Json(new { ok = true, message = $"已把配置组「{n}」恢复为默认映射。" });
    }

    public string StartBridge()
    {
        lock (_lock)
        {
            if (BridgeRunning) return Env.Json(new { ok = false, message = "语音桥已在运行。" });
            if (!Env.IsAdmin())
                return Env.Json(new { ok = false, message = "语音桥需要管理员权限（ETW StartTrace）。请用管理员身份重启本程序。" });
            var exe = Env.FindAsset("etw_consumer.exe");
            if (exe == null)
                return Env.Json(new { ok = false, message = "未找到 etw_consumer.exe。" });
            try
            {
                var psi = new ProcessStartInfo(exe)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    // ★ 必须重定向并主动读走：否则桥的输出被丢弃（CreateNoWindow 下没有控制台），
                    //   而"音频为什么断"的线索全在桥的 stdout 里。顺带避免子进程写满管道被阻塞。
                    // ⚠ 也必须显式指定 UTF-8：桥（Rust）写的是 UTF-8，而 .NET 默认按
                    //   控制台代码页（中文机 cp936）解码 → 中文全变成乱码
                    //   （"UDP → 127.0.0.1" 会变成 "UDP 鈫?127.0.0.1"）。
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                    Arguments = $"--udp 127.0.0.1:{Env.PcmPort} --hp 70 --seconds 86400",
                };
                var proc = Process.Start(psi);
                if (proc == null)
                {
                    _bridge = null;
                    return Env.Json(new { ok = false, message = "启动语音桥失败：进程未创建。" });
                }
                _bridge = proc;
                _bridgeStart = DateTime.UtcNow;
                PumpToLog(proc.StandardOutput, "out");
                PumpToLog(proc.StandardError, "err");
                return Env.Json(new { ok = true, message = "语音桥已启动。按住遥控器语音键说话即可。" });
            }
            catch (Exception e)
            {
                _bridge = null;
                return Env.Json(new { ok = false, message = $"启动语音桥失败：{e.Message}" });
            }
        }
    }

    public string StopBridge()
    {
        lock (_lock)
        {
            try { if (_bridge is { HasExited: false }) { _bridge.Kill(true); _bridge.WaitForExit(3000); } }
            catch { }
            _bridge = null;
            return Env.Json(new { ok = true, message = "语音桥已停止。" });
        }
    }

    /// <summary>把语音桥的 stdout/stderr 追加到 bridge.log（后台线程读到底）。</summary>
    private static void PumpToLog(StreamReader? r, string tag)
    {
        if (r == null) return;
        var path = Path.Combine(Env.ConfigDir, "bridge.log");
        new Thread(() =>
        {
            try
            {
                string? line;
                while ((line = r.ReadLine()) != null)
                {
                    try
                    {
                        // 桥（Rust）输出是 UTF-8。Windows 记事本/PowerShell 的 Get-Content
                        // 默认按 ANSI 代码页解，会显示成乱码 —— 所以**首次创建时写一个 BOM**，
                        // 让系统按 UTF-8 识别（后续追加不能再写 BOM，否则文件中间会插空字符）。
                        var isNew = !File.Exists(path);
                        var body = $"{DateTime.Now:HH:mm:ss.fff} [{tag}] {line}\n";
                        if (isNew) File.WriteAllText(path, body, new UTF8Encoding(true));
                        else File.AppendAllText(path, body, new UTF8Encoding(false));
                    }
                    catch { }
                }
            }
            catch { }
        })
        { IsBackground = true, Name = "bridge-log" }.Start();
    }

    /// <summary>读语音桥每 2 秒刷新的 bridge-stats.json。读不到返回 null。</summary>
    private static JsonObject? ReadBridgeStats()
    {
        try
        {
            var p = Path.Combine(Env.ConfigDir, "bridge-stats.json");
            if (!File.Exists(p)) return null;
            return JsonNode.Parse(File.ReadAllText(p)) as JsonObject;
        }
        catch { return null; }
    }

    /// <summary>
    /// 语音桥是否"僵死"：**进程还在，但已不再上报统计**。
    ///
    /// 这是最危险的一种故障 —— 靠"进程是否存活"完全看不出来，界面显示一切正常，
    /// 实际永久静音。典型成因：另一个桥实例启动时 `logman stop` 掐掉了它的 ETW 会话。
    /// 判定：统计文件超过 10 秒没刷新（正常 2 秒一刷）。
    /// </summary>
    private bool BridgeZombie
    {
        get
        {
            if (!BridgeRunning) return false;
            if ((DateTime.UtcNow - _bridgeStart).TotalSeconds < 10) return false; // 刚启动，还没写第一份
            var st = ReadBridgeStats();
            if (st == null) return false;                                        // 老版本桥没有统计文件
            var hb = st["heartbeatMs"]?.GetValue<long>() ?? 0;
            if (hb <= 0) return false;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            // 统计文件里的 pid 必须是当前这个桥的，否则是上一个实例留下的
            if ((st["pid"]?.GetValue<int>() ?? -1) != SafePid()) return false;
            return now - hb > 10_000;
        }
    }

    private int SafePid()
    {
        try { return _bridge?.Id ?? -1; } catch { return -1; }
    }

    /// <summary>语音桥状态（真实统计来自 bridge-stats.json，以前这里是硬编码 0）</summary>
    private JsonObject BridgeStatus()
    {
        var st = ReadBridgeStats();
        var running = BridgeRunning;
        var zombie = BridgeZombie;
        // 统计文件里带 pid，只认当前这个桥的，避免读到上一个实例的残留
        if (st != null && (st["pid"]?.GetValue<int>() ?? -1) != SafePid()) st = null;
        long L(string k) { try { return st?[k]?.GetValue<long>() ?? 0; } catch { return 0; } }
        double D(string k) { try { return st?[k]?.GetValue<double>() ?? 0; } catch { return 0; } }
        var silentMs = st == null ? -1 : L("silentMs");
        return new JsonObject
        {
            ["running"] = running,
            ["zombie"] = zombie,
            ["frames"] = L("frames"),
            ["samples"] = L("samples16"),
            ["seconds"] = Math.Round(D("seconds16"), 1),
            ["uptimeSec"] = L("uptimeSec"),
            ["sessions"] = L("sessions"),
            ["gaps"] = L("gaps"),
            ["maxGapMs"] = L("maxGapMs"),
            ["silentMs"] = silentMs,
            ["lostCont"] = L("lostCont"),
            ["truncatedFirst"] = L("truncatedFirst"),
            // ★ 丢帧定位（2026-10-16 加）：光有"丢了 511 帧"没法定位是哪一层的错。
            //   四级互相独立 ⇒ 一眼判定（判据见 tools/bridge_loss_selftest.py）：
            //     seqLost  = 输出流里**总共**少了几帧（任何原因）
            //     seqJumps = 序号跳了几次（跳号才是"遥控器发过、我们没收到"的真丢帧）
            //     lostCont = 首片到了、配对的续片没到（组装失败）
            //     oddFrag  = 分片长度/标志不认识 —— **"变了"而不是"丢了"**
            //     truncCont/truncatedFirst = 分片在了但负载被 ETW 截断
            ["seqJumps"] = L("seqJumps"),
            ["seqLost"] = L("seqLost"),
            ["seqDup"] = L("seqDup"),
            ["truncCont"] = L("truncCont"),
            ["oddFrag"] = L("oddFrag"),
            ["hdrOdd"] = L("hdrOdd"),
            ["orphanCont"] = L("orphanCont"),
            ["sigFrag"] = L("sigFrag"),
            ["shortPdu"] = L("shortPdu"),
            ["udpPackets"] = L("udpPackets"),
            ["udpBytes"] = L("udpBytes"),
            ["hint"] = !running ? "待启动"
                : zombie ? "语音桥僵死：进程在但已停止上报 —— 会自动重启"
                : st == null ? "正在监听 BthPort ETW（统计文件还没生成）"
                : L("seqLost") > 0 || L("lostCont") > 0 || L("oddFrag") > 0
                    ? $"⚠ 收到音频，但已丢 {L("seqLost")} 帧"
                      + (L("oddFrag") > 0 ? "，且有分片长度不认识（帧格式可能变了）" : "")
                      + " —— 明细见 bridge.log 的「音频丢失明细」"
                : silentMs < 0 ? "正在监听 BthPort ETW（尚未收到音频）"
                : $"正在监听 BthPort ETW（{silentMs} ms 前收到音频）",
        };
    }

    /// <summary>真的按一次右 Alt 并读回状态，验证注入链路</summary>
    public string TestInject() => KeyFilter.InjectSelfTest();
    /// <summary>清空卡住的按键状态（释放 held 集合 + 放掉注入键）</summary>
    public string ClearKeys()
    {
        var n = _filter?.ClearHeld("手动清空") ?? 0;
        return Env.Json(new { ok = true, message = n > 0
            ? $"已清空 {n} 个卡住的按键，并释放了注入的快捷键。"
            : "当前没有卡住的按键。" });
    }

    /// <summary>环境自检清单（安装/就绪向导用）</summary>
    private JsonObject SetupStatus()
    {
        // 「按键受保护」= 要么设备已不是键盘，要么钩子正在拦截（两者都不会漏 m / 漏键）
        var devOk = !Env.RemoteStillKeyboard() || (_filter is { Running: true, KeySourceIsEtw: false });
        return new JsonObject
        {
            ["cable"] = new JsonObject { ["ok"] = _cableReady, ["hint"] = "虚拟声卡 VB-CABLE" },
            ["mic"] = new JsonObject { ["ok"] = _micOk, ["hint"] = "默认录音设备 = CABLE Output" },
            ["winuhid"] = new JsonObject { ["ok"] = _uhidReady,
                ["hint"] = WinUhidInjector.DriverVersion >= 1
                    ? $"虚拟键盘驱动已就绪（接口 v{WinUhidInjector.DriverVersion}）"
                    : "WinUHid 驱动未安装" },
            ["device"] = new JsonObject { ["ok"] = devOk,
                ["hint"] = !Env.RemoteStillKeyboard() ? "遥控器已不是键盘（零 m）"
                       : (devOk ? "键盘模式 + 钩子拦截中（低延迟）" : "遥控器仍在输出 m") },
            ["bridge"] = new JsonObject { ["ok"] = BridgeRunning, ["hint"] = "语音桥运行中" },
            ["ready"] = _cableReady && _micOk && _uhidReady && devOk,
        };
    }

    /// <summary>「一键完成全部」：把还缺的步骤按顺序跑一遍</summary>
    public string RunSetup()
    {
        RefreshProbes(true);
        var done = new List<string>();
        var todo = new List<string>();

        if (!_cableReady)
        {
            var (ok, m) = Env.InstallCable();
            todo.Add($"虚拟声卡：{m}");
            _ = ok;
        }
        else done.Add("虚拟声卡已就绪");

        if (!_uhidReady)
        {
            WinUhidInjector.EnsureReady();
            if (WinUhidInjector.DriverVersion < 1)
            {
                var (ok, m) = Env.InstallWinuhid();
                todo.Add($"虚拟键盘驱动：{m}");
                _ = ok;
            }
            else done.Add("虚拟键盘驱动已就绪");
        }
        else done.Add("虚拟键盘驱动已就绪");

        if (Env.RemoteStillKeyboard())
        {
            var (ok, m) = Env.SetDeviceDriver(false);
            if (ok) done.Add("设备驱动：已改为 HID-compliant device");
            else todo.Add($"设备驱动：{m}");
        }
        else done.Add("设备驱动已是 HID-compliant device");

        if (!_micOk)
        {
            var (ok, m) = Env.SetDefaultMic();
            if (ok) done.Add("默认麦克风：已设为 CABLE Output");
            else todo.Add($"默认麦克风：{m}");
        }
        else done.Add("默认麦克风已是 CABLE Output");

        RefreshProbes(true);
        MaybeAutoStartBridge();

        var msg = (done.Count > 0 ? "已完成：" + string.Join("；", done) + "。" : "")
                + (todo.Count > 0 ? "\n需要你操作：" + string.Join("；", todo) : "");
        return Env.Json(new { ok = todo.Count == 0, message = msg.Trim() });
    }

    public string SetDeviceDriver(string? revert)
    {
        var wantKeyboard = revert is "true" or "1" or "True";
        var (ok, msg) = Env.SetDeviceDriver(wantKeyboard);
        var extra = "";
        if (ok && wantKeyboard && Settings.AutoSwitchDriver)
        {
            // ★ 用户手动还原成键盘 → 必须**同时关掉**"自动切回非键盘"，
            //   否则 2 秒后的周期复查会立刻把这次操作覆盖掉（按钮等于没用）。
            Settings.AutoSwitchDriver = false;
            extra = "（已同时关闭「自动切回非键盘」—— 否则周期复查会立刻把它改回去）";
        }
        // 手动操作后给一个短暂的静默期，避免立刻被复查逻辑打扰
        _driverBackoffUntil = DateTime.UtcNow.AddSeconds(10);
        DeviceLog($"手动{(wantKeyboard ? "还原为键盘" : "切为非键盘")}：" +
                  $"{(ok ? "成功" : "失败")} {msg}{extra}");
        _probeAt = DateTime.MinValue;
        AutoSelectKeySource();
        return Env.Json(new { ok, message = msg + extra });
    }

    private string? _bindBid;
    private string? _groupToast;
    private DateTime _groupToastAt = DateTime.MinValue;
    private DateTime _lastDevCheck = DateTime.MinValue;

    /// <summary>环境探针缓存时长（秒）。这些探针要扫注册表，拉长可显著降低常驻 CPU。</summary>
    private const int ProbeTtlSec = 20;

    /// <summary>最近一次卡键解锁的原因（给界面显示）</summary>
    public string? LastUnlock { get; private set; }
    /// <summary>累计解锁次数</summary>
    public long UnlockCount { get; private set; }

    /// <summary>
    /// 设备可能发生变化（插拔/蓝牙断开/拔电池）。若遥控器已不在，**立即**清掉卡住的键。
    ///
    /// 为什么必须即时：遥控器只发 M-down 没来得及发 M-up 就断开时，Windows 会把 M 永远
    /// 留在「按下」，之后按 31 Hz 持续合成 —— 也就是「拔电池还一直输出 m」。
    /// 只靠静默超时要等 1.5 秒，这期间用户已经在挨刷屏了。
    /// </summary>
    public void OnDeviceMayChanged()
    {
        // 事件可能很频繁（DBT_DEVNODES_CHANGED），限流 200 ms
        if ((DateTime.UtcNow - _lastDevCheck).TotalMilliseconds < 200) return;
        _lastDevCheck = DateTime.UtcNow;

        if (Env.RemotePresent())
        {
            // ★ 「设备还在」不等于「没变」：蓝牙重连会销毁旧的 HID 子设备实例、建一个新的，
            //   我们设的驱动绑定随旧实例一起消失 → 退回键盘。
            //   这里只**请求**探针线程复查 —— 本方法是 WndProc（UI 线程）调的，
            //   而驱动切换要 1~2 秒，在这里做会把界面冻住。
            _deviceRecheckRequested = true;
            return;
        }
        var n = _filter?.ClearHeld("遥控器已断开（设备移除）") ?? 0;
        if (n > 0)
        {
            UnlockCount++;
            LastUnlock = $"{DateTime.Now:HH:mm:ss} 遥控器断开 → 立即释放 {n} 个卡住的键";
        }
    }

    /// <summary>开始「按一下绑定」：下一次真实按键的 VK 会被捕获（并吞掉，不会打出一个字符）</summary>
    public string BeginBind(string? bid)
    {
        if (_filter == null) return Env.Json(new { ok = false, message = "按键过滤器未就绪。" });
        _bindBid = bid;
        _filter.CaptureNext = true;
        return Env.Json(new { ok = true, message = "请在键盘上按一下要绑定的按键（Esc 可取消）。" });
    }

    /// <summary>捕获到按键：直接写进该键的映射（单键）</summary>
    public void OnBindCaptured(int vk)
    {
        var bid = _bindBid;
        if (bid == null || vk <= 0) return;
        if (Keys.Table.Any(x => x.Id == bid)) Keymaps.Set(bid, new[] { vk });
        _bindBid = null;
    }

    public string CancelBind()
    {
        if (_filter != null) _filter.CaptureNext = false;
        _bindBid = null;
        return Env.Json(new { ok = true, message = "已取消绑定。" });
    }

    /// <summary>开机自启（写入 HKCU\...\Run）</summary>
    public string SetAutoRun(string? on)
    {
        var want = on is "true" or "1" or "True";
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (k == null) return Env.Json(new { ok = false, message = "打不开 Run 注册表项。" });
            if (want)
            {
                var exe = Environment.ProcessPath!;
                k.SetValue(Env.AppName, $"\"{exe}\"");
            }
            else k.DeleteValue(Env.AppName, false);
            Settings.AutoRun = want;
            return Env.Json(new { ok = true, autoRun = want,
                message = want ? "已设置开机自启（登录 Windows 后自动启动）。" : "已取消开机自启。" });
        }
        catch (Exception e) { return Env.Json(new { ok = false, message = $"设置失败：{e.Message}" }); }
    }

    public bool IsAutoRun()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", false);
            return k?.GetValue(Env.AppName) != null;
        }
        catch { return false; }
    }

    /// <summary>启动时最小化到托盘</summary>
    public string SetStartMinimized(string? on)
    {
        var want = on is "true" or "1" or "True";
        Settings.StartMinimized = want;
        return Env.Json(new { ok = true, startMinimized = want,
            message = want ? "已开启：启动后直接进托盘（双击托盘图标可打开）。"
                           : "已关闭：启动后显示主窗口。" });
    }

    /// <summary>清空按键日志</summary>
    public string ClearKeyLog()
    {
        EtwKeySource.ClearLog();
        return Env.Json(new { ok = true, message = "按键日志已清空。" });
    }


    /// <summary>开关「启动后自动拉起语音桥」</summary>
    public string SetAutoStart(string? on)
    {
        AutoStartBridge = on is "true" or "1" or "True";
        if (AutoStartBridge) MaybeAutoStartBridge();
        return Env.Json(new { ok = true, autoStart = AutoStartBridge,
            message = AutoStartBridge ? "已开启：启动本程序后自动拉起语音桥。" : "已关闭自动启动语音桥。" });
    }

    public string InstallWinuhid()
    {
        var (ok, msg) = Env.InstallWinuhid();
        return Env.Json(new { ok, message = msg });
    }

    public string ExportWinuhid()
    {
        var (ok, msg) = Env.ExportWinuhid();
        return Env.Json(new { ok, message = msg });
    }

    public string RelaunchAdmin()
    {
        var (ok, msg) = Env.RelaunchAdmin();
        return Env.Json(new { ok, message = msg });
    }

    public string OpenUrl(string? url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url ?? "https://vb-audio.com/Cable/") { UseShellExecute = true });
            return Env.Json(new { ok = true, message = "已在浏览器打开。" });
        }
        catch (Exception e) { return Env.Json(new { ok = false, message = e.Message }); }
    }

    /// <summary>开关「启动时自动把驱动切成非键盘」</summary>
    public string SetAutoSwitchDriver(string? on)
    {
        var want = on is "true" or "1" or "True";
        Settings.AutoSwitchDriver = want;
        return Env.Json(new { ok = true, autoSwitchDriver = want,
            message = want ? "已开启：每次启动会把遥控器驱动自动切成非键盘（零 m、无卡键）。"
                           : "已关闭：不再自动改驱动。" });
    }

    public string SetAutoKeepMic(string? on)
    {
        var want = on is "true" or "1" or "True";
        Settings.AutoKeepDefaultMic = want;
        return Env.Json(new { ok = true, autoKeepMic = want,
            message = want ? "已开启：默认录音设备被 Windows 改走时（蓝牙耳机接入/断开等）会自动改回 CABLE Output。"
                           : "已关闭：不再自动改回默认录音设备（输入法可能读到别的设备）。" });
    }

    /// <summary>
    /// 「检查录音设备」：输入法/通话软件要打开的那个麦克风，此刻到底能不能开。
    /// 专治间歇性的「录音启动失败」—— 失败时它会指出是"默认设备被改走"还是"设备被占用"。
    /// </summary>
    public string CheckCaptureDevice()
    {
        var o = (dynamic)Env.ProbeCaptureDevice();
        var ok = (bool)o.canOpen;
        var isCable = (bool)o.isCable;
        var name = (string)o.defaultMicName;
        var err = (string?)o.error;

        CableRouter.Log($"[MIC-CHECK] 默认录音设备={name} 是CABLE={isCable} 可打开={ok}" +
                        (err is null ? "" : $" 错误={err}"));

        string msg;
        if (ok && isCable)
            msg = $"正常。默认录音设备是「{name}」，现在可以正常打开。";
        else if (!isCable)
            msg = $"默认录音设备是「{name}」，**不是 CABLE Output** —— 输入法/通话软件会读错设备，" +
                  "这就是「录音启动失败」或通话没波形的原因。点「一键完成全部」可改回。";
        else
            msg = $"CABLE Output 是默认设备，但**现在打不开**：{err}\n" +
                  "常见原因是别的程序正以独占模式占用它（同时开着通话软件和输入法时最容易出现）。" +
                  "关掉其中一个再试。";
        return Env.Json(new { ok, isCable, canOpen = ok, name, error = err, message = msg });
    }

    /// <summary>手动「释放所有注入键」——用于键盘手感不对时一键复位</summary>
    public string ReleaseAllKeys()
    {
        KeyFilter.ReleaseAllInjected();
        _filter?.ClearHeld("手动释放全部注入键");
        return Env.Json(new { ok = true, message = "已释放全部注入键（含右 Alt / 方向键 / 媒体键）。" });
    }

    public void Dispose()
    {
        // ★ 必须先置位：探测线程用 `while (!_disposed)` 做退出条件，
        //   漏了这一步线程永远不会停（每 8 秒扫一次注册表），
        //   也正是编译警告 CS0649「从未对 _disposed 赋值」的根源。
        _disposed = true;
        try { _probeThread?.Join(600); } catch { }      // 给它一点时间自己退出
        try { _mouse.Stop(); } catch { }                // 退出前把鼠标键全部抬起（卡住左键会点不了东西）
        StopBridge();
        _router.Dispose();
    }
}
