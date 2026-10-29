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

    /// <summary>「只涨报告不涨事件」探针（见 KeyPathWatch 的说明）</summary>
    private readonly KeyPathWatch.State _keyPath = new();

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

    public Backend(bool startAudio = true)
    {
        // ★ 2026-10-27：事件日志总闸跟随设置（默认开）。关掉后 Safe.Note 变成空操作。
        Safe.NotesEnabled = Settings.KeepEventLog;
        // ★★ 2026-10-29：**必须先选端口再绑**。原来把 ResolvePorts 放在 InitAsync 里，
        //   而 CableRouter.Start() 在构造函数里就绑 31680 ⇒ 端口被占时直接抛 SocketException 崩溃。
        if (startAudio) Env.ResolvePortsOnce();
        // client 模式（宿主只做 UI）不启音频路由：UDP 31680 由业务进程独占
        if (startAudio) _router.Start();   // 常驻：随时接收语音桥推来的 PCM
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
    private int _firstKeyRecheck;      // 0 = 还没做过"首键复查驱动"

    private void OnUsage(byte usage, bool down)
    {
        // 幽灵报告（0x0C）不当"当前操作"显示，免得盖住真实按键
        if (usage == 0x0C) return;

        // ★★ 2026-10-27（真机"换机器后按键随机打出字母"）：
        //   `RemoteStillKeyboard()` 读的是注册表 —— 遥控器**在休眠/还没配对**时注册表里
        //   没有它的条目，函数会返回 false ⇒ 启动时误判"已是非键盘"，于是**不再切驱动**。
        //   等用户真正按下第一个键，才会暴露"它其实还是键盘"（字符已经漏进窗口了）。
        //   这里在**收到第一个按键**时再复查一次，把误判窗口缩到最短。
        if (down && Interlocked.Exchange(ref _firstKeyRecheck, 1) == 0)
        {
            try
            {
                if (Env.RemoteStillKeyboard())
                {
                    _deviceRecheckRequested = true;   // 探针线程 ≤500ms 内复查并尝试切驱动
                    DeviceLog("DEV  首键到达：遥控器仍是键盘（启动时可能在休眠）→ 已请求立即复查驱动");
                }
            }
            catch { }
        }
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
        // ★ 2026-10-18 用户要求：**只保留 ETW 一种按键来源**，不再有"键盘模式/钩子回退"。
        //   设备仍是键盘时，ETW 照样能读到按键（BthPort 层），但系统也会把它当键盘 →
        //   按遥控器会**同时打进前台应用**（m/i 漏出）。这种情况不再偷偷切钩子，
        //   而是**明确提示用户去换驱动**（界面按钮「安装虚拟键盘驱动」）。
        var stillKb = Env.RemoteStillKeyboard();

        if (_keySource != "etw")
        {
            _keySource = "etw";
            _filter.SetSourceMode(true);
            DeviceLog("按键来源 → ETW（唯一来源）");
        }

        _keySourceGuard = Settings.DriverFreeMode
            // 无驱动模式：遥控器**就该**是键盘，不能再引导用户去装驱动（那会推翻本模式）
            ? "无驱动模式：遥控器保持键盘，它自带的 m 由钩子按「连发特征」吞掉"
              + $"（已吞 {GhostGuard.Swallowed} 次；开头第 1 个 m 可能漏，属正常）"
            : stillKb
              ? "设备仍是键盘：ETW 能读到按键，但系统也会把它当键盘，按键会漏成字符 —— "
                + "请点「安装虚拟键盘驱动」把驱动换成 HID-compliant device"
              : "设备已不是键盘 ✓ 按键走 BthPort ETW（不装钩子、不吞键、无卡键风险）";
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
        // ★ 切组前先放掉正按住的键（2026-10-18 卡键排查）：
        //   旧组里按着的键（如语音键→右 Alt）在**新组**里可能没有映射，
        //   之后那次"松开"就找不到该释放谁 ⇒ 修饰键永久卡住。
        //   必须在 Keymaps.NextGroup() **之前**释放，那时用的还是旧映射。
        try { _filter?.ClearHeld("切换配置组，先放掉按住的键"); } catch { }
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
            try { _filter?.ClearHeld("方块键切回键盘模式，先放掉按住的键"); } catch { }
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
    /// <summary>
    /// **结构性改造（2026-10-18）：语音键的释放以「音频会话结束」为准，不再只依赖遥控器的 UP。**
    ///
    /// 为什么必须这样：真机反复出现"按住能说话，松开后右 Alt 不弹 / 第二次唤不醒"。
    /// 日志证明遥控器的 UP 与后续报文都不可靠（长按 5.7s 后，语音键连一条报文都不再发）。
    /// 而"音频停了"是**可靠信号** —— 用户松手后音频必然停、桥的 inSession 会归 0。
    ///
    /// 规则（全部满足才动手，保守）：我们正按着且**只有** voice 一个键 + 桥确实收到过会话
    /// + 当前不在会话中 + 音频静默 > 400ms ⇒ 强制释放。
    /// </summary>
    private void AudioDrivenVoiceRelease()
    {
        // ★★ 2026-10-19（真机"还没说话就自动弹起"）：默认**不做**这件事。
        //   遥控器不说话时会停止出流 ⇒ "按住想一下"被误判成静默超时 ⇒ 主动放掉右 Alt。
        //   UP 路径已可靠（v1.2.50），这条只作可选的兜底。
        if (!Settings.VoiceAudioRelease) return;
        if (_filter is not { } f) return;
        if (!f.IsOnlyHeld("voice")) return;

        // ★★ 2026-10-18（真机"短按后语音没发出就弹起"）：
        //   按下后 **2 秒内绝不动手**。语音桥的首包要 300~530ms，这段时间
        //   `inSession` 还是 0、`silentMs` 已经很大 ⇒ 不加宽限就会在音频起来之前
        //   把 Alt 放掉（豆包还没开始录音就结束）。
        var heldMs = f.HeldMs("voice");
        if (heldMs < 0 || heldMs < 2000) return;

        var st = ReadBridgeStats();
        if (st == null) return;
        if ((st["pid"]?.GetValue<int>() ?? -1) != SafePid()) return;
        if ((st["sessions"]?.GetValue<long>() ?? 0) <= 0) return;   // 从没收到过音频：别乱动
        if ((st["inSession"]?.GetValue<int>() ?? 0) != 0) return;   // 还在会话中：正常
        long silent;
        try { silent = st["silentMs"]?.GetValue<long>() ?? -1; } catch { return; }
        // ★★ 2026-10-18 二次确认（时序常量审计）：
        //   桥的 IDLE_END_MS = 600ms —— 也就是说**说话中途停顿 0.6 秒**就会把会话判结束。
        //   原来这里只要静默 > 400ms 就放键 ⇒ 用户"想一想再说"会被硬生生切断。
        //   改成 **5 秒**：只作为"UP 彻底失效"的兜底，不会打断正常停顿。
        if (silent < 5000) return;
        f.ClearHeld($"音频会话已结束（静默 {silent} ms）—— 释放语音键");
    }

    private void StartProbeThread()
    {
        if (_probeThread != null) return;
        _probeThread = new Thread(() =>
        {
            while (!_disposed)
            {
                // ★ 2026-10-19：原来这里每 8 秒 `RefreshProbes(true)` —— 会**强制**重新枚举
                //   注册表 + 声卡端点（CableState/RemoteDiag），长期运行的固定开销。
                //   面板读的是同一个缓存（ProbeTtlSec=20），强制刷新纯属多余。
                //   现在：只有"设备变更过"才强制；其它时候尊重 20 秒 TTL。
                var force = _deviceRecheckRequested;
                _deviceRecheckRequested = false;
                try { RefreshProbes(force); } catch { }
                try { RecheckDeviceDriver(); } catch { }
                for (var i = 0; i < 80 && !_disposed; i++)     // 8 秒一轮
                {
                    Thread.Sleep(100);
                    // 设备变更事件把复查间隔压到 ≤500ms；否则每 2 秒兜底复查一次。
                    // ★ 这些都在**后台线程**上：SetDeviceDriver 要 1~2 秒，
                    //   放到 UI 线程（WndProc 会调 OnDeviceMayChanged）会把界面冻住。
                    // 每 ~500ms 检查一次"语音键是否该随音频结束而释放"（结构性改造）
                    if (i % 5 == 0) { try { AudioDrivenVoiceRelease(); } catch { } }
                    // 每 8 秒一次：跨天就清理旧日志（内部只比较日期，不枚举目录）
                    if (i == 0) { try { Safe.Housekeeping(); } catch { } }
                    // 每 32 秒一次：检测反作弊进程（只用于界面提示，不改变注入行为）
                    if (i == 0 && Environment.TickCount64 - _antiCheatAt > 32000)
                    {
                        _antiCheatAt = Environment.TickCount64;
                        try { _antiCheat = Env.DetectAntiCheat(); } catch { }
                    }
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
            // 端口已在构造函数里解析（见 Backend ctor），这里只保证后续读到的是一致值
            Env.ResolvePortsOnce();

            // 键源归因：旁听所有键盘事件（只读、不拦截）。出问题时一条命令就能看出"谁在发键"。
            try { KeySourceDiag.Start(); } catch { }

            // 先按**已保存的档位**补应用映射（安装器可能在安装时就选好了输入法）
            EnsureImeProfileApplied();
            // 再考虑"首次启动自动预选"（只做一次）
            AutoPickIme();

            // 无驱动模式：恢复幽灵键抑制（钩子不随进程持久，重启后要重新装）
            if (Settings.DriverFreeMode)
            {
                try { GhostGuard.Enabled = true; GhostGuard.Start(); } catch { }
                GhostGuard.HoldFirstKey = Settings.ZeroGhost;
            }

            // ★★ 2026-10-29：先清**上一次进程残留的任意卡键**（真机事故：用户键盘 m 打不出来，
            //   系统按 31Hz 自动重复它，拔遥控器电池也停不了）。只在真有键按着时才动手。
            try { var stuck = KeyFilter.ReleaseStuckKeys(); if (stuck > 0) DeviceLog($"启动清理卡键 {stuck} 个"); } catch { }

            // ★ 启动兜底：清掉上一次异常退出（例如被强杀）残留的「按着不放」的注入键。
            //   最要命的是右 Alt(AltGr) —— 卡住会让字母键输出特殊字符、中文输入法失效。
            try { KeyFilter.ReleaseAllInjected(); } catch { }

            RefreshProbes(true);          // ← 必须先跑：它才会设置 _cableReady（起桥的前置条件）

            // ★★ 2026-10-19（真机"启动后 3~5 秒内按语音键无响应"）：
            //   原来顺序是 RefreshProbes → AutoSwitchDriver → AutoSelectKeySource → 起桥，
            //   而切驱动实测要 1~2 秒、选来源也要时间 ⇒ 桥要 2.5 秒后才开始监听 ETW，
            //   这段时间遥控器的按键**根本没有接收者**（界面表现为完全无响应）。
            //   桥只依赖 ETW + 音频设备，**不依赖驱动切换** ⇒ 提到切驱动之前。
            //   ⚠ 不能提到 RefreshProbes 之前：_cableReady 还没设，MaybeAutoStartBridge 会直接 return
            //     （真机 A/B 实测：那样桥 20 秒都不启动）。
            try { MaybeAutoStartBridge(); } catch { }

            AutoSwitchDriver();
            try { AutoSelectKeySource(); } catch { }

            // ★★★ 2026-10-19（真机三段式：第1次无反应 / 第2次有图标但不录 / 第3次正常）
            //   虚拟 HID 设备原本是**首次按键时才创建**（日志里 `虚拟 HID 设备已创建` 出现在第一次注入前）。
            //   而设备刚被创建时 Windows 正在**枚举这个新键盘**：
            //     · 第 1 次按下 → 系统还没把新设备接上 ⇒ 按键被丢（完全无反应）
            //     · 第 2 次按下 → 输入法刚发现"多了个键盘"，只显示麦克风图标、不进入录音
            //     · 第 3 次 → 一切就绪 ⇒ 正常
            //   启动时就把它建好，让枚举在用户按键之前完成。
            //   ⚠ 只建**设备**、**绝不发报告**：启动时发空报告会让 Windows 给所有键补发
            //     KEYUP（含 Apps 键），历史上表现为"开机自动弹出右键菜单"。
            try
            {
                if (WinUhidInjector.EnsureReady())
                    EtwKeySource.AppendLog("INJ  虚拟 HID 设备已预创建（启动预热，避免首次按键被吞）");
                else
                    EtwKeySource.AppendLog($"INJ  虚拟 HID 预创建未成功：{WinUhidInjector.LastError ?? "未知"}（首次按键时再试）");
            }
            catch (Exception e) { Safe.Log(e, "启动预创建虚拟 HID 失败"); }
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
        // ★★ 2026-10-29（用户实测踩到）：无驱动模式要求**遥控器保持键盘**，
        //   而这里会在每次启动时强制把它切成"非键盘" —— 等于把用户的设置推翻。
        //   面板里会显示"遥控器已被系统当作普通 HID 设备"，用户以为开关没生效。
        if (Settings.DriverFreeMode)
        {
            _driverNote = "无驱动模式：已跳过自动切换，遥控器保持键盘（幽灵 m 由钩子吞掉）";
            DeviceLog("无驱动模式：跳过自动切驱动");
            return;
        }
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
        // ★★ 2026-10-29：无驱动模式要**保持键盘** —— 这里原来一看到"仍是键盘"就切回非键盘，
        //   重启后会把用户的设置推翻（实测：开了无驱动、重启又变回 HID-compliant）。
        if (Settings.DriverFreeMode)
        {
            _driverNote = "无驱动模式：遥控器保持键盘（重连后也不切换）";
            return;
        }
        if (!Settings.AutoSwitchDriver) return;
        if (now < _driverBackoffUntil) return;
        if (!Env.IsAdmin())
        {
            _driverBackoffUntil = now.AddMinutes(10);
            _driverNote = "遥控器仍是键盘，但当前没有管理员权限 —— 无法自动切驱动（按键会漏成字符）";
            DeviceLog("★ 遥控器仍是键盘，但没有管理员权限、无法自动切回（请以管理员身份运行）");
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
            _driverNote = "遥控器仍是键盘且自动切换未生效 —— 按键会漏成字符，请手动「切换设备驱动」。";
            // ★ 2026-10-27：原来这里写"已启用钩子拦截兜底（不会漏 m）" ——
            //   钩子在 v1.2.63 就删了，这话是**假的**，会让人误以为受保护。
            DeviceLog($"★ 遥控器仍是键盘且自动切驱动未生效（{msg}）—— 10 分钟内不再重试；" +
                      "**当前没有任何拦截，按键会漏成字符**，请在环境设置里手动切换设备驱动");
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
    /// <summary>检测到的反作弊名称（仅用于界面提示）；每 32 秒刷新一次</summary>
    private string? _antiCheat;
    /// <summary>已安装输入法检测结果（缓存 60 s，避免每 2 秒刷界面都扫注册表）</summary>
    private (bool doubao, bool wechat) _imeDetected;
    private long _imeDetectedAt;

    private (bool doubao, bool wechat) ImeDetected()
    {
        if (Environment.TickCount64 - _imeDetectedAt > 60000)
        {
            _imeDetectedAt = Environment.TickCount64;
            try { _imeDetected = Env.DetectVoiceImes(); } catch { }
        }
        return _imeDetected;
    }

    /// <summary>
    /// **首次启动自动预选**输入法档位（只做一次，之后不再自动改）。
    ///
    /// 规则：只装了微信 → 选微信；其余（都装 / 只装豆包 / 都没装）→ 保持豆包默认。
    /// 为什么"都装"时不选微信：豆包是本产品长期调过的默认路径，微信的 Ctrl+Win 是
    /// 全局快捷键、可能与其他软件冲突 —— 两个都在时把选择权留给用户更稳。
    /// </summary>
    /// <summary>
    /// 把**已保存的档位**落到「语音键」映射上（幂等）。
    ///
    /// 场景：安装器里选了"微信输入法" → 写 settings.json 的 ImeProfile=wechat + ImeAutoPicked=true；
    /// 应用启动时如果不看这个档位，映射还是旧的右 Alt ⇒ 用户在安装界面的选择等于白选。
    /// 规则：只在当前映射仍是**某个档位的预设值**时才改；用户自定义过就不动。
    /// </summary>
    private void EnsureImeProfileApplied()
    {
        try
        {
            var prof = Settings.ImeProfile;
            if (prof is not ("doubao" or "wechat")) return;
            var doubao = new[] { 0xA5 };
            var wechat = new[] { 0xA2, 0x5B };
            var want = prof == "doubao" ? doubao : wechat;
            var other = prof == "doubao" ? wechat : doubao;
            var cur = Keymaps.Get("voice") ?? Array.Empty<int>();
            if (cur.SequenceEqual(want)) return;              // 已经是目标
            if (cur.Length > 0 && !cur.SequenceEqual(other)) return;   // 用户自定义，不动
            Keymaps.Set("voice", want);
            Safe.Note($"IME 按已保存档位应用映射：{prof} → {Keys.FormatSymbols(want)}");
        }
        catch (Exception e) { Safe.Note($"IME 应用档位失败 {e.GetType().Name}: {e.Message}"); }
    }

    private void AutoPickIme()
    {
        if (Settings.ImeAutoPicked) return;
        try
        {
            var (doubao, wechat) = ImeDetected();
            var pick = (!doubao && wechat) ? "wechat" : Settings.ImeProfile;
            SetImeProfile(pick);
            Settings.ImeAutoPicked = true;
            Safe.Note($"IME 自动预选：豆包={doubao} 微信={wechat} → {pick}");
        }
        catch (Exception e) { Safe.Note($"IME 自动预选失败 {e.GetType().Name}: {e.Message}"); }
    }
    private long _antiCheatAt;

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

        // ★★ 关键（2026-10-18）：清掉**不属于本实例的遗留桥**。
        //
        //   桥自身有"单实例守卫"：如果已经有一把在跑，**新拉起来的那把会立刻退出**。
        //   于是上一个实例被强杀/崩溃后留下的旧桥会一直霸占着 —— 用户装上新版本，
        //   实际跑的却是**旧桥的代码**，表现为"改了没生效"（这次排查就撞上了：
        //   stats 里的 pid 一直是旧的那个）。
        //
        //   这里按 **PID 精确**清（只认进程名 etw_consumer，绝不按映像名批量杀 ——
        //   那会连本程序自己的桥一起杀掉）。清理后本实例拉起的才真正生效。
        ReapStaleBridge();

        // ★★ 2026-10-18（真机卡键）：**桥一换，按住的键就永远等不到 UP 了**。
        //   旧桥被杀 / 僵死重启时，它没来得及发的 UP 不会再有第二个人补发 ⇒
        //   程序会一直认为那个键「按着」⇒ 注入的修饰键（右 Alt）永久卡住，
        //   而且之后按同一个键也"没反应"（状态已经在按住里，不会重复注入）。
        //   所以启新桥之前必须把按住集合清空并释放。
        try { _filter?.ClearHeld("语音桥重启，之前的 UP 不会再来"); } catch { }

        StartBridge();
    }

    /// <summary>杀掉"不是本实例拉起"的遗留语音桥（按 stats 里的 pid 精确定位）。</summary>
    private void ReapStaleBridge()
    {
        try
        {
            var st = ReadBridgeStats();
            var pid = st?["pid"]?.GetValue<int>() ?? -1;
            if (pid <= 0 || pid == SafePid()) return;          // 没有 / 就是我们自己的
            var p = System.Diagnostics.Process.GetProcessById(pid);
            if (p == null || p.HasExited) return;
            // 只认 etw_consumer —— 万一 pid 被系统复用，也不会误杀别的程序
            if (!string.Equals(p.ProcessName, "etw_consumer", StringComparison.OrdinalIgnoreCase)) return;
            p.Kill(true);
            p.WaitForExit(2000);
            DeviceLog($"清理遗留语音桥 pid={pid}（上一实例留下的，会挡住新桥）");
        }
        catch { /* 拿不到就跳过，不影响正常启动 */ }
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
            // 发布版默认不写日志（LogSwitch 默认 false）—— 界面据此隐藏"按键日志"与"清空日志"
            ["logEnabled"] = LogSwitch.Enabled,
            // 非键盘设备下 Raw Input 到底收不收得到报文 —— 用来判断能不能把按键延迟从 5 ms 降到 ~1 ms
            // 过滤后（只算遥控器）的条数 —— 这个 > 0 才说明 Raw Input 直读真的可用
            ["rawOurs"] = KeyFilter.RawOurs,
            ["winuhid"] = JsonSerializer.SerializeToNode(WinUhidInjector.Status()),
            ["setup"] = SetupStatus(),
            ["stuckGuard"] = new JsonObject
            {
                ["unlockCount"] = UnlockCount,
                ["lastUnlock"] = LastUnlock,
                ["watchdogMs"] = KeyFilter.HoldTimeoutMs,
                // ★ 面板上要显示的是**语音键**的判据（音频静默阈值），不是 Raw Input 的静默超时
                ["voiceSilentMs"] = StuckGuard.VoiceSilentMs,
                ["voiceAudioRelease"] = Settings.VoiceAudioRelease,
                ["voiceGraceSec"] = (long)StuckGuard.VoiceGraceSec,
            },
            // ★★ 2026-10-27：这几项原来被我误加到 `stuckGuard` 子对象里，而界面读的是**顶层**
            //   `state.xxx` ⇒ 全部取到 undefined：
            //     · keepEventLog → 开关每 2 秒被强制勾回（用户报"点了没反应、一直打钩"）
            //     · antiCheat    → 反作弊提示永远不显示
            //     · clickMinScore / clickSearchScope / clickReturnToOrigin → 界面永远显示默认值
            //   放在这一层（与 autoRun / autoKeepMic 同级）才对。
            ["antiCheat"] = _antiCheat,
            ["remoteKb"] = Env.RemoteKb().ToString(),   // Absent / Keyboard / NotKeyboard
            ["heldNow"] = KeyFilter.HeldKeysNow(),
            ["keepEventLog"] = Settings.KeepEventLog,
            ["imeProfile"] = Settings.ImeProfile,
            ["driverFree"] = Settings.DriverFreeMode,
            ["ports"] = new JsonObject { ["pcm"] = Env.PcmPort, ["key"] = Env.KeyPort },
            ["driverVersion"] = WinUhidInjector.DriverVersion,
            ["uhidReady"] = _uhidReady,
            ["lastRepair"] = _lastRepair.at == default ? null : new JsonObject
            {
                ["at"] = _lastRepair.at.ToString("HH:mm:ss"),
                ["ok"] = _lastRepair.ok,
                ["msg"] = _lastRepair.msg,
            },
            ["ghostSwallowed"] = GhostGuard.Swallowed,
            ["ghostReleased"] = GhostGuard.ForcedReleases,
            ["ghostBursts"] = GhostGuard.Bursts,
            ["ghostGate"] = new JsonObject { ["enabled"] = GhostGuard.AttributeEnabled, ["remotes"] = GhostGuard.RemoteCount },
            ["ghostReplayed"] = GhostGuard.Replayed,
            ["zeroGhost"] = Settings.ZeroGhost,
            // -1 = 从未拦过；否则=距最近一次拦截的秒数（不变化 = 现在已经不发 m）
            ["ghostLastAgo"] = GhostGuard.LastSwallowAt == 0 ? -1
                : (int)((Environment.TickCount64 - GhostGuard.LastSwallowAt) / 1000),
            ["imeVoice"] = Keys.FormatSymbols(Keymaps.Get("voice") ?? Array.Empty<int>()),
            ["imeDetected"] = new JsonObject
            {
                ["doubao"] = ImeDetected().doubao,
                ["wechat"] = ImeDetected().wechat,
            },
            ["clickMinScore"] = Settings.ClickMinScore,
            ["clickSearchScope"] = Settings.ClickSearchScope,
            ["clickReturnToOrigin"] = Settings.ClickReturnToOrigin,
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
                // ★ 2026-10-18：`pressed` 以前取"最后一个事件是不是 DOWN"——
                //   一旦 UP 丢了，面板就永远显示「按住」，看起来像卡键（真机被误判过）。
                //   现在直接查**按住集合**：键真被释放了，显示也就跟着变。
                ["etw"] = PatchEtwPressed(JsonSerializer.SerializeToNode(_etwKeys?.Status())),
                ["logPath"] = EtwKeySource.EventsLogPath,
                ["hookActive"] = _filter is { Running: true, KeySourceIsEtw: false },
            },
            ["swallow"] = new JsonObject
            {
                ["running"] = _filter?.Running ?? false,
                ["deviceSeen"] = _filter?.DeviceSeen ?? false,
                // 2026-10-20：`swallowed`/`passed` 恒为 0（钩子已删）→ 不再上报
                ["error"] = _filter?.LastError,
                ["sinceLastInputMs"] = _filter?.SinceLastInputMs ?? -1,
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
        try { _filter?.ClearHeld("切换配置组，先放掉按住的键"); } catch { }   // 见 CycleGroup 的说明
        var name = Keymaps.SwitchTo(int.TryParse(idx, out var i) ? i : 0);
        SyncMouseWithGroup();
        return Env.Json(new { ok = true, message = $"已切换到配置组「{name}」"
            + (Keymaps.MouseGroupActive ? "（鼠标模式已开启）" : "") + "。" });
    }

    public string NewGroup()
    {
        try { _filter?.ClearHeld("新建配置组，先放掉按住的键"); } catch { }
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
        // ★ 改当前组的映射前也先放掉按住的键：正在按的这个键如果被改掉/清空，
        //   接下来那次"松开"就不知道该释放谁（修饰键会永久卡住）。
        try { _filter?.ClearHeld($"修改「{bid}」映射，先放掉按住的键"); } catch { }
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
                    // 日志关着就给桥加 --quiet：桥不写 bridge.log、也不打 [KEY] 行
                    // （那行是每条按键一次管道写，C# 读到也是丢）
                    Arguments = $"--udp 127.0.0.1:{Env.PcmPort} --key-udp 127.0.0.1:{Env.KeyPort} --hp 70 --seconds 86400"
                                + (LogSwitch.Enabled ? "" : " --quiet"),
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
                    // ★ 即使不写日志也**必须继续读**：桥（Rust）把日志打到 stdout，
                    //   没人读的话管道写满，桥会**阻塞在 write 上** —— 那才是真正拖慢速度。
                    if (!LogSwitch.Enabled) continue;
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

    /// <summary>把 etw 状态里的 pressed 改成"真实按住集合"里有没有这个按钮。</summary>
    private JsonNode? PatchEtwPressed(JsonNode? node)
    {
        try
        {
            if (node is JsonObject o && _filter is { } f)
            {
                var bid = o["lastButton"]?.GetValue<string>();
                o["pressed"] = !string.IsNullOrEmpty(bid) && f.IsHeld(bid!);
            }
        }
        catch { }
        return node;
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

        // ★ 静默失效探针：只在"报告在涨、事件不涨"时才算异常（空闲不报，见 KeyPathWatch）
        var hidCount = L("hidReports");
        var keyCount = L("keyEvents");
        KeyPathWatch.Step(_keyPath, hidCount, keyCount, Environment.TickCount64);
        return new JsonObject
        {
            ["running"] = running,
            // ★ 前端靠它区分"闲着没说话"和"正在说话却断流" —— 否则界面一空闲就标红，
            //   看着像故障（2026-10-17 用户被这条误导过一次）。
            ["inSession"] = L("inSession") != 0,
            ["keyPathStuck"] = _keyPath.Stuck,
            ["keyPathHint"] = _keyPath.Stuck
                ? $"已收到 {hidCount} 个按键报告，但 {KeyPathWatch.DefaultThresholdMs / 1000} 秒内一次按键事件都没产生"
                  + " —— 解析链路异常（看 bridge.log 的「未识别分片」与 CID）"
                : "",
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
        /// <summary>环境自检清单（安装/就绪向导用）</summary>
    private JsonObject SetupStatus()
    {
        // 「按键受保护」= 要么设备已不是键盘，要么钩子正在拦截，要么是**无驱动模式**（钩子按连发特征吞 m）
        //   ★ 2026-10-29：原来只认前两种 ⇒ 无驱动模式下遥控器是键盘，这项一直报"✗ 遥控器不是键盘"，
        //     还引导用户去"安装虚拟键盘驱动"—— 恰好会把无驱动模式推翻。
        var driverFreeOn = Settings.DriverFreeMode && GhostGuard.Running;
        var devOk = driverFreeOn || !Env.RemoteStillKeyboard() || (_filter is { Running: true, KeySourceIsEtw: false });
        CheckGhostAutoFallback();   // 失控自愈：无驱动模式卡住时自动切回驱动模式
        if (!Settings.DriverFreeMode) Task.Run(CheckDriverAutoHeal);   // 驱动失败自愈（不阻塞界面）

        return new JsonObject
        {
            ["cable"] = new JsonObject { ["ok"] = _cableReady, ["hint"] = "虚拟声卡 VB-CABLE" },
            ["mic"] = new JsonObject { ["ok"] = _micOk, ["hint"] = "默认录音设备 = CABLE Output" },
            ["winuhid"] = new JsonObject { ["ok"] = _uhidReady,
                // ★ 2026-10-18：DriverVersion==0 的**真正原因**通常是"驱动装好了但没重启"
                //   （控制设备 \.\WinUHid 不存在），以前只显示"驱动未安装"，
                //   用户照着字面意思反复去点安装，白绕好几圈。现在直接说要重启。
                ["hint"] = _uhidReady
                    ? $"虚拟键盘驱动已就绪（接口 v{WinUhidInjector.DriverVersion}）"
                    : WinUhidInjector.ControlDeviceOpenable()
                        ? "WinUHid 设备槽位被占，稍后会自愈重试"
                        : "WinUHid 驱动未生效 —— 请重启 Windows（不重启注入只能走 SendInput，豆包会过滤）" },
            ["device"] = new JsonObject { ["ok"] = devOk,
                ["hint"] = driverFreeOn ? $"无驱动模式：键盘 + 钩子吞 m（已吞 {GhostGuard.Swallowed}）"
                       : !Env.RemoteStillKeyboard() ? "遥控器已不是键盘（零 m）"
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
        // ★★★ 2026-10-19（真机"按住语音键、还没说话就自动弹起"）：
        //   `RemotePresent()` 查的是 HID 里有没有 `VID_0A5C` 节点，但**按住语音键流音频时**，
        //   系统会临时挂起/重建这个 HID 节点（我们早已观测到：遥控器休眠时 HID 里根本没有该节点）。
        //   于是 DBT_DEVNODES_CHANGED 一到、RemotePresent()=false ⇒ 立刻 ClearHeld
        //   ⇒ 右 Alt 被放掉 ⇒ 用户以为"按键自己弹起来了"。
        //
        //   判据修正：**音频还在流 ⇒ 遥控器肯定活着**，一律不允许当作"已断开"。
        //   只有"既看不到 HID 节点、又没有音频"才认为真断开。
        try
        {
            var st0 = ReadBridgeStats();
            if (st0 != null)
            {
                long silent = -1;
                try { silent = st0["silentMs"]?.GetValue<long>() ?? -1; } catch { }
                if (silent >= 0 && silent < 5000)
                {
                    _deviceRecheckRequested = true;   // 只复查驱动，不动按住状态
                    return;
                }
            }
        }
        catch { }

        var n = _filter?.ClearHeld("遥控器已断开（设备移除）") ?? 0;
        if (n > 0) EtwKeySource.AppendLog($"DEV  设备节点变化且 5 秒内无音频 ⇒ 判定遥控器已断开，释放 {n} 个按住键");
        if (n > 0)
        {
            UnlockCount++;
            LastUnlock = $"{DateTime.Now:HH:mm:ss} 遥控器断开 → 立即释放 {n} 个卡住的键";
        }
    }

    /// <summary>开始「按一下绑定」：下一次真实按键的 VK 会被捕获（并吞掉，不会打出一个字符）</summary>
        /// <summary>捕获到按键：直接写进该键的映射（单键）</summary>
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
    /// <summary>界面上的「写入诊断日志」开关。改完要重启桥才生效（桥的 --quiet 是启动参数）。</summary>
    public string SetLog(string? on)
    {
        var want = on == "1";
        var before = LogSwitch.Enabled;
        LogSwitch.Set(want);
        if (want != before && BridgeRunning) { StopBridge(); StartBridge(); }
        return Env.Json(new { ok = true, logEnabled = LogSwitch.Enabled });
    }

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

    /// <summary>采集「截图点击」的图标：取光标周围 44×44 存成模板（用户把鼠标停在目标按钮上再点）。</summary>
    public string LearnClickIcon()
    {
        try
        {
            var msg = TemplateClick.Learn();
            EtwKeySource.AppendLog($"APPLY  采集图标  {msg}");
            return Env.Json(new { ok = true, message = msg });
        }
        catch (Exception e)
        {
            return Env.Json(new { ok = false, message = "采集失败：" + e.Message });
        }
    }

    /// <summary>
    /// **无驱动（降级）模式**开关（2026-10-29）。
    ///
    ///   开：遥控器保持键盘 + 启钩子吞幽灵 m（给装不了 WinUHid 的机器）
    ///   关：切回"普通 HID 设备" + 停钩子（恢复标准路径）
    ///
    /// 切换会改动设备驱动，**建议切完重启程序**（设备在重新枚举，注入目标会短暂失效）。
    /// 已知代价（如实告知，不掩饰）：
    ///   · 输入法（豆包/微信）可能不认 SendInput 注入 ⇒ 语音键触发可能不生效；
    ///   · 按键映射到普通程序仍可用；音频链路不受影响；
    ///   · 钩子可被反作弊检测 —— 仅建议无联机游戏的机器使用。
    /// </summary>
    public string SetDriverFreeMode(string? on)
    {
        var want = on is "true" or "1" or "True";
        try { _filter?.ClearHeld("切换驱动模式，先放掉按住的键"); } catch { }

        Settings.DriverFreeMode = want;
        GhostGuard.ResetStats();          // 切换即清零：避免旧累计数字被误读成"还在发"
        if (want) { GhostGuard.Enabled = true; GhostGuard.Start(); }
        else GhostGuard.Stop();

        var (ok, msg) = Env.SetDeviceDriver(want);   // true=还原键盘，false=普通 HID
        // 结果同时写进 _driverNote：toast 一闪就没了，而"到底切没切成功"必须能回头看
        _driverNote = (want ? "无驱动模式：" : "标准模式：") + msg;
        DeviceLog((ok ? "✓ " : "★ ") + _driverNote);
        _probeAt = DateTime.MinValue;
        return Env.Json(new { ok, driverFree = want,
            message = (want
                ? "已切到无驱动模式：遥控器保持键盘，按住语音键期间会吞掉它自带的 m。"
                : "已切回标准模式：遥控器重新设为普通 HID 设备。")
                + $" 设备驱动：{msg} 建议重启程序后使用。"
                + (want ? " 注意：输入法可能不认此模式下的按键注入（语音键可能无法触发输入法）。" : "") });
    }

    /// <summary>
    /// 切换**语音输入法档位**：把「语音键」的映射改成该输入法要求的组合键。
    ///
    ///   豆包：长按 **右 Alt**（VK_RMENU = 0xA5）
    ///   微信输入法：长按 **Ctrl+Win**（0x11 + 0x5B）—— 官方快捷键**全局生效**，
    ///               不需要把默认输入法切换成微信输入法
    ///   自定义：不改映射
    ///
    /// 只在"当前映射还是某个档位的默认值"时才自动改写 —— 用户手工改过就不动。
    /// </summary>
    /// <summary>
    /// **失控自动降级**：无驱动模式的幽灵键抑制如果连续吞了很多次却一直没见到弹起，
    /// 说明这个键卡住了（用户整套键盘会打不了字）。此时**自动切回驱动模式** ——
    /// 驱动模式下系统层面就没有 m，最稳。宁可功能降级，也不能把用户的键盘搞坏。
    /// </summary>
    private static volatile bool _fallbackRunning;

    private void CheckGhostAutoFallback()
    {
        if (!Settings.DriverFreeMode || !GhostGuard.AutoDisabled || _fallbackRunning) return;
        _fallbackRunning = true;
        Safe.Note("GHOST 失控（连续吞咽未见弹起）→ 自动切回驱动模式，恢复键盘");
        Task.Run(() =>
        {
            try { KeyFilter.ReleaseAllInjected(); } catch { }
            try { SetDriverFreeMode("0"); } catch { }
            _driverNote = "无驱动模式失控（遥控器的 m 卡住），已自动切回驱动模式";
        });
    }

    // ── 虚拟键盘驱动：一键修复 / 诊断 / 自愈（2026-10-29）─────────────
    private (DateTime at, bool ok, string msg) _lastRepair;
    private int _autoRepairCount;
    private DateTime _lastAutoRepairAt = DateTime.MinValue;

    /// <summary>
    /// **一键安装 / 修复虚拟键盘驱动**（界面按钮直接调）。
    /// 用 -Force：先清空所有 WinUHid 节点再重建 —— 这是"重装也修不好"的解药。
    /// </summary>
    public string RepairDriver(string? forceArg)
    {
        var force = forceArg is not ("0" or "false" or "False");
        var (ok, reboot, text) = Env.RepairWinuhid(force);
        _lastRepair = (DateTime.Now, ok, text.Split('\n')[0]);
        // 重建后重新探测：ProbeDriver 决定注入是否可用、面板是否显示"已就绪"
        try { WinUhidInjector.EnsureReady(); } catch { }
        RefreshProbes(true);
        DeviceLog((ok ? "✓ " : "★ ") + "一键修复驱动：" + _lastRepair.msg);
        return Env.Json(new { ok, reboot, needRestart = reboot,
            message = text, uhidReady = _uhidReady,
            driverVersion = WinUhidInjector.DriverVersion });
    }

    /// <summary>**一键诊断**：跑只读 Status，写到桌面并返回全文（界面显示 + 可复制）。</summary>
    public string ExportDriverDiag()
    {
        var (ok, text) = Env.RunWinuhidStatus();
        var advice = text.Replace("\r\n", "\n").Split('\n')
                         .Where(l => l.Contains("Phase: Advice |"))
                         .Select(l => l[(l.IndexOf('|') + 1)..].Trim())
                         .Where(l => l.Length > 0 && !l.StartsWith("----")).ToArray();
        var body = "IPRC1000 Adapter — 驱动诊断\n时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                 + "\n版本: " + Env.AppVersion + "\n" + new string('-', 50) + "\n" + text.Trim();
        var path = "";
        try
        {
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "IPRC1000-driver-diag-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
            File.WriteAllText(path, body, new System.Text.UTF8Encoding(true));
        }
        catch { }
        return Env.Json(new { ok, path, text = body, advice });
    }

    /// <summary>
    /// **失败自愈**：驱动"装了但不可用"持续一段时间 ⇒ 自动跑一次 -Force 修复。
    /// 本程序以管理员运行，子进程继承提权，**不会再弹 UAC**。
    /// 上限：本会话最多 2 次、间隔 ≥5 分钟（避免反复折腾）。
    /// </summary>
    private DateTime _uhidDownSince = DateTime.MinValue;

    private void CheckDriverAutoHeal()
    {
        if (_uhidReady) { _uhidDownSince = DateTime.MinValue; return; }
        if (_uhidDownSince == DateTime.MinValue) { _uhidDownSince = DateTime.Now; return; }
        if ((DateTime.Now - _uhidDownSince).TotalSeconds < 20) return;          // 给它 20 秒自愈窗口
        if (_autoRepairCount >= 2) return;
        if ((DateTime.Now - _lastAutoRepairAt).TotalMinutes < 5) return;
        _autoRepairCount++;
        _lastAutoRepairAt = DateTime.Now;
        Safe.Note($"驱动不可用超过 20 秒 → 自动修复（第 {_autoRepairCount} 次）");
        var (ok, reboot, text) = Env.RepairWinuhid(force: true);
        _lastRepair = (DateTime.Now, ok, (reboot ? "需要重启：" : "") + text.Split('\n')[0]);
        try { WinUhidInjector.EnsureReady(); } catch { }
        _probeAt = DateTime.MinValue;   // 下一轮立刻重新探测
        DeviceLog((ok ? "✓ " : "★ ") + "驱动自愈：" + _lastRepair.msg);
    }

    /// <summary>键源归因报告：`rawInput` / `rawInput reset`</summary>
    private static string RawInputReport(string? arg)
    {
        if (arg is "reset" or "clear")
        {
            KeySourceDiag.Reset();
            return Env.Json(new { ok = true, message = "已清空键源统计" });
        }
        return Env.Json(new { ok = true, running = KeySourceDiag.Running, text = KeySourceDiag.Snapshot() });
    }

    /// <summary>无驱动模式的「零漏」开关：遥控器活跃时 m 先吞后补，代价是 60ms 延迟。</summary>
    public string SetZeroGhost(string? on)
    {
        var want = on is "true" or "1" or "True";
        Settings.ZeroGhost = want;
        GhostGuard.HoldFirstKey = want;
        return Env.Json(new { ok = true, zeroGhost = want,
            message = want
                ? "已开启「零漏」：遥控器活跃的 8 秒内，第一个 m 会被先吞再补（打字时若刚用过遥控器，单个 m 会晚 60ms 出现）。"
                : "已关闭「零漏」：每次按住语音键可能漏 1 个 m。" });
    }

    public string SetImeProfile(string? p)
    {
        var profile = (p ?? "").Trim().ToLowerInvariant();
        if (profile is not ("doubao" or "wechat" or "custom"))
            return Env.Json(new { ok = false, message = $"未知输入法：{p}" });

        var doubao = new[] { 0xA5 };
        // ★ 2026-10-29：**左** Ctrl + **左** Win（0xA2 + 0x5B），不是通用 VK_CONTROL(0x11)。
        //   微信输入法官方给的就是"左 Ctrl + 左 Win"；用通用 Ctrl 时系统按当前键盘布局
        //   可能解析成右 Ctrl，组合键就失效（同类项目 SayAll 也是这么写的）。
        var wechat = new[] { 0xA2, 0x5B };
        var target = profile switch { "doubao" => doubao, "wechat" => wechat, _ => Array.Empty<int>() };
        var cur = Keymaps.Get("voice") ?? Array.Empty<int>();
        var curShown = Keys.FormatSymbols(cur);
        var isPreset = cur.Length == 0 || cur.SequenceEqual(doubao) || cur.SequenceEqual(wechat);

        if (profile != "custom" && !isPreset)
        {
            Settings.ImeProfile = profile;
            return Env.Json(new { ok = true, applied = false, imeProfile = profile, voiceNow = curShown,
                message = $"已切换为「{ImeName(profile)}」，但「语音」当前是自定义映射（{curShown}），未改动。" });
        }

        if (profile != "custom")
        {
            try { _filter?.ClearHeld("切换输入法档位，先放掉按住的键"); } catch { }
            Keymaps.Set("voice", target);
        }
        Settings.ImeProfile = profile;
        return Env.Json(new { ok = true, applied = profile != "custom", imeProfile = profile,
            voiceNow = Keys.FormatSymbols(Keymaps.Get("voice") ?? Array.Empty<int>()),
            message = profile switch
            {
                "doubao" => "已切到豆包：语音键 = 长按右 Alt。",
                "wechat" => "已切到微信输入法：语音键 = 长按 Ctrl+Win（全局生效，无需切换默认输入法）。另请在微信输入法设置里把「麦克风」选成 CABLE Output。",
                _ => "已切到自定义：语音键映射不再自动改动。",
            } });
    }

    private static string ImeName(string p) => p switch
    {
        "doubao" => "豆包输入法", "wechat" => "微信输入法", _ => "自定义"
    };

    /// <summary>事件日志开关（errors.log 的 VOICE/CLEARHELD 等）。关掉 = 完全不写日志。</summary>
    public string SetKeepEventLog(string? on)
    {
        var want = on is "true" or "1" or "True";
        Settings.KeepEventLog = want;          // setter 里同时改 Safe.NotesEnabled
        return Env.Json(new { ok = true, keepEventLog = want,
            message = want ? "已开启事件日志（每天几 KB，出问题能归因）"
                           : "已关闭事件日志 —— 现在**完全不写任何日志**" });
    }

    /// <summary>设置截图点击的搜索范围（0=只局部 / 1=窗口下部 / 2=整个窗口）。</summary>
    public string SetClickSearchScope(string v)
    {
        if (!int.TryParse(v, out var n) || n < 0 || n > 2)
            return Env.Json(new { ok = false, message = "搜索范围只能是 0/1/2" });
        Settings.ClickSearchScope = n;
        var name = n switch { 0 => "只搜采集位置附近", 2 => "失败后再搜整个窗口", _ => "失败后再搜窗口下部" };
        return Env.Json(new { ok = true, scope = Settings.ClickSearchScope, message = "搜索范围：" + name });
    }

    /// <summary>设置截图点击的相似度门槛（0.50~0.98）。</summary>
    public string SetClickMinScore(string v)
    {
        if (!double.TryParse(v, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d))
            return Env.Json(new { ok = false, message = "门槛必须是数字（例如 0.75）" });
        Settings.ClickMinScore = d;
        return Env.Json(new { ok = true, score = Settings.ClickMinScore,
            message = $"匹配门槛已设为 {Settings.ClickMinScore:F2}。越低越容易点中，也越容易点错。" });
    }

    /// <summary>发送 1 秒测试音，用于验证"CABLE Output 有没有声音"。</summary>
    public string PlayTestTone()
    {
        try
        {
            var msg = _router.PlayTestTone(1000);
            return Env.Json(new { ok = true, message = msg });
        }
        catch (Exception e)
        {
            return Env.Json(new { ok = false, message = "发送测试音失败：" + e.Message });
        }
    }

    /// <summary>「截图点击」的试运行：就地执行一次，把结果（相似度/失败原因）回给界面。</summary>
    /// <summary>截图点击后是否"回到原位再点一下"（恢复输入焦点）。</summary>
    public string SetClickReturnOrigin(string v)
    {
        var on = v == "1";
        Settings.ClickReturnToOrigin = on;
        return Env.Json(new
        {
            ok = true,
            message = on
                ? "已开启：点完目标会回到原来的位置再点一下（恢复输入焦点）"
                : "已关闭：只点目标，不回到原位"
        });
    }

    public string TestClickIcon()
    {
        try
        {
            var msg = TemplateClick.TryClick();
            EtwKeySource.AppendLog($"APPLY  截图点击(测试)  {msg}");
            return Env.Json(new { ok = true, message = msg });
        }
        catch (Exception e)
        {
            return Env.Json(new { ok = false, message = "执行异常：" + e.Message });
        }
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

    /// <summary>
    /// **统一命令分发**（2026-10-29 拆进程用）。
    ///
    /// 原来这套 switch 在 MainForm 里 —— 那是"宿主"代码，无界面模式（--headless）就用不到。
    /// 抽到这里之后：UI 宿主（走 WebView 消息）与无界面服务（走命名管道）**调的是同一份**，
    /// 命令语义必然一致，不会出现"界面能转、服务不能转"的分裂。
    ///
    /// 参数用两个字符串（原来 JS 侧最多用到 args[0]/args[1]）。
    /// </summary>
    public string Dispatch(string method, string a0 = "", string a1 = "")
    {
        return method switch
        {
            "getState" => GetState(),
            "refreshAll" => GetState(),
            "applyRegistry" => ApplyRegistry(),
            "reboot" => Reboot(),
            "installCable" => InstallCable(),
            "setDefaultMic" => SetDefaultMic(),
            "saveKey" => SaveKey(a0, a1),
            "switchGroup" => SwitchGroup(a0),
            "newGroup" => NewGroup(),
            "renameGroup" => RenameGroup(a0, a1),
            "deleteGroup" => DeleteGroup(a0),
            "exportGroup" => ExportGroup(),
            "importGroup" => ImportGroup(a0),
            "resetKeys" => ResetKeys(),
            "startBridge" => StartBridge(),
            "stopBridge" => StopBridge(),
            "relaunchAdmin" => RelaunchAdmin(),
            "openUrl" => OpenUrl(a0),
            "setInject" => SetInject(a0),
            "testInject" => TestInject(),
            // forceUnlock=托盘"紧急释放"；releaseAllKeys=面板"释放全部注入键"（语义不同，都保留）
            "forceUnlock" => ForceUnlock(),
            "releaseAllKeys" => ReleaseAllKeys(),
            "runSetup" => RunSetup(),
            "setDeviceDriver" => SetDeviceDriver(a0),
            // 鼠标模式（2026-10-16）：方块键切模式；参数与状态见 Backend.Mouse
            "setMouseMode" => SetMouseMode(a0),
            "setMouseConfig" => SetMouseConfig(a0),
            "mouseSample" => TakeMouseSample(),
            "mouseCalibrate" => CalibrateMouse(),
            "setAutoStart" => SetAutoStart(a0),
            "clearKeyLog" => ClearKeyLog(),
            "setAutoRun" => SetAutoRun(a0),
            "setStartMinimized" => SetStartMinimized(a0),
            "setLog" => SetLog(a0),
            "setKeepEventLog" => SetKeepEventLog(a0),
            "setImeProfile" => SetImeProfile(a0),
            "setDriverFree" => SetDriverFreeMode(a0),
            "setZeroGhost" => SetZeroGhost(a0),
            // 键源归因：返回"哪个设备发了什么键"。调试用，界面上没有入口。
            "rawInput" => RawInputReport(a0),
            "repairDriver" => RepairDriver(a0),
            "exportDriverDiag" => ExportDriverDiag(),
            "setAutoSwitchDriver" => SetAutoSwitchDriver(a0),
            "setAutoKeepMic" => SetAutoKeepMic(a0),
            "checkCaptureDevice" => CheckCaptureDevice(),
            "learnClickIcon" => LearnClickIcon(),
            "testClickIcon" => TestClickIcon(),
            "testTone" => PlayTestTone(),
            "setClickMinScore" => SetClickMinScore(a0),
            "setClickSearchScope" => SetClickSearchScope(a0),
            "setClickReturnOrigin" => SetClickReturnOrigin(a0),
            "installWinuhid" => InstallWinuhid(),
            "exportWinuhid" => ExportWinuhid(),
            _ => Env.Json(new { ok = false, message = $"未知方法 {method}" }),
        };
    }
}
