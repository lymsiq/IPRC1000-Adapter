using System.Runtime.InteropServices;
using System.Text;

namespace Iprc1000Adapter;

/// <summary>
/// IPRC1000 按键拦截 + 快捷键注入（移植自 src-tauri/.../iprc1000/{bridge,swallow}.rs）。
///
/// 为什么必须拦截：遥控器在 Windows 上是**标准蓝牙键盘**，除方向键/OK 外都落进字母键区
/// （语音键 → `M`、返回 → `F`、方块键 → `S`）。不拦截就会打进焦点窗口。
///
/// ⚠ 关于「按住语音键一直输出 m」：那**不是遥控器在连发**，而是 Windows 的
/// **键盘自动重复**（按住任意键盘键都会以约 31 Hz 合成 DOWN，实测间隔 31.5 ms）。
/// 因此做法是：Raw Input 侧维护「遥控器当前按住的 VK 集合」，钩子里把**集合内的**
/// 物理事件一律吞掉 —— 自动重复的 DOWN 因为 VK 仍在集合里，同样被吞；松开才移除。
/// 注入事件（LLKHF_INJECTED）一律放行，否则会吞掉我们自己注入的快捷键。
/// </summary>
internal sealed class KeyFilter : IDisposable
{
    // ── 键盘钩子 ──

    // ── Raw Input ──

    /// <summary>
    /// Raw Input 收到的报文数（**自进程启动累计**）。
    ///
    /// 用途很具体：代码里一直有一句未经实测的断言 ——「设备切成 HID-compliant device 后
    /// `HandleRawInput` 根本不会被调用」。如果它其实**会**被调用，就能用 Raw Input 直读按键，
    /// 把投递延迟从 ETW 的 **5 ms 降到 ~1 ms**（省 4 ms/次按键）。
    ///
    /// 所以这个计数器不参与任何逻辑，只用来**证伪那句断言**：
    /// 设备非键盘状态下按几下遥控器，这里涨了 ⇒ 直读可用；一直是 0 ⇒ 断言成立。
    /// </summary>
    // `RawMessages` 只统计 WM_INPUT 次数，随 Raw Input 删除。

    /// <summary>
    /// 上面那个计数里，**确实来自遥控器**的条数（过了 `IsIprc1000` 设备过滤之后的）。
    ///
    /// 为什么要分两个：`RawMessages` 是所有设备的（含物理键盘、含我们自己注入的回流），
    /// 用它判断"直读可用"会得出错误结论。**只有这个 > 0 才说明真的能直读。**
    /// </summary>
    public static long RawOurs { get; private set; }






    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);


    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public KEYBDINPUT ki;
    }

    private const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_EXTENDEDKEY = 0x0001;

    // ─────────────────────────────────────────

    // 2026-10-27：`_held`（Raw Input 的按键集合）随整条 Raw Input 路径一起删除。
    //   注意：热路径用的是 `_heldButtons`（注入的按钮状态），两者不是一回事。
    private readonly object _heldLock = new();

    // ── 卡键看门狗 v2 ──────────────────────────────────────────────────────────
    //
    // ★ 背景：看门狗 v1 只盯 Raw Input 的 _held 集合，而按键来源切到 ETW 之后
    //   HandleRawInput 根本不会被调用（驱动已切成非键盘）→ _held 恒为空 →
    //   看门狗每次都在第一行就 return。**丢一次 UP 就永久卡键**，只能手动紧急复位。
    //   （2026-10-15 实录：语音键 DOWN 后 UP 丢失，注入的右 Alt 卡了 61 秒。）
    //
    // v2 改盯「**注入了还没放开**的按钮」，在 ApplyButton 里维护 ——
    // 这条路径 Raw Input / ETW 两种来源都走，与按键来源无关。
    //
    // ★ 判据（实测得出）：语音键按住期间蓝牙 SCO 一定在传音频帧
    //   （不说话也传静音帧；实测"语音键按下 → 首包音频 0~528ms"），
    //   所以「语音键按着 + 音频停了 3 秒」= 遥控器早已松开、UP 丢了 → 强制放开。
    //   注意**不能**用"遥控器静默"做判据 —— 实测正常按住 30 秒期间事件数也是 0。
    private readonly Dictionary<string, (int[] vks, DateTime at)> _heldButtons = new();

    /// <summary>
    /// 音频静默毫秒数提供者（由 Backend 注入，读语音桥统计里的 silentMs）。
    /// 语音键按住期间 SCO 链路一定在传帧，所以静默几秒就说明键早松了。
    /// 返回 null 表示拿不到信号（桥没跑）——此时退化为绝对超时。
    /// </summary>
    public Func<long?>? AudioSilenceMs { get; set; }
    // 句柄 → 是不是遥控器。**只存一个 bool，不存别的设备的任何信息**
    // （2026-10-18 用户明确要求：别人的键盘鼠标不要留痕）。
    // `_deviceCache`（句柄→是否遥控器）只服务 Raw Input 路径，已删除。
    private volatile bool _running;
    private IntPtr _hwnd;

    // 2026-10-20：`_swallowed/_passed` 原本只在已删除的低级钩子里自增 ⇒ 恒为 0，
    // 而界面却拿它显示"已拦截/放行"（假仪表）。一并删除。
    private readonly Dictionary<ushort, bool> _lastState = new();   // 去重：按住的自动重复只处理第一次
    private DateTime _lastRawInputAt = DateTime.MinValue;

    /// <summary>
    /// 遥控器的「幽灵报告」keyboard usage = **0x0C**，Windows 把它映射成 VK **0x49 = 字母 `i`**。
    ///
    /// 它**紧跟**在每次真实按键之后约 2 ms 出现（实测：每个键都伴随一个幽灵报告）。
    /// 由于低级钩子比 Raw Input 先拿到事件，幽灵报告的第一个 DOWN 到达时 `held` 里
    /// 还没有 0x49 → 会漏出去打出一个 `i`（输入框里不断插 i，输入法跟着抖，表现就是"卡顿"）。
    ///
    /// 修法：**只要看到遥控器任何按键，就把幽灵键位一并武装一小段时间**（幽灵报告只会在
    /// 这之后极短时间内出现），这样它的 DOWN 也会被吞掉。窗口很短，对真实键盘影响可忽略。
    /// </summary>

    /// <summary>
    /// 幽灵键位的"武装窗口"时长。实测幽灵报告只比真实按键**晚 1~2 ms**，
    /// 所以窗口只要够覆盖这个间隔即可 —— 取 **40 ms**：
    ///   · 覆盖 2 ms 有 20 倍余量
    ///   · 误伤窗口极小（只有在"刚敲过遥控器用到的某个字母键"后 40 ms 内又敲 `i` 才会被吞，
    ///     而人手连续两键间隔通常 80~200 ms，几乎不会踩到）
    /// </summary>
    // 幽灵键位"武装窗口"随 Raw Input / 钩子路径一起删除（ETW 侧由桥的 ghost 过滤负责）。

    /// <summary>被放行出去的幽灵键次数（应当恒为 0；不为 0 就说明还在漏 i）</summary>
    // `SpuriousLeaked` 从未被递增、也无人消费，删除。

    /// <summary>
    /// 当前**虚拟键盘整份报告**里按着的键 = 所有「按住中」按钮的键并集。
    ///
    /// ★★ 为什么必须是"全集"而不是"某一个按钮的键"：
    ///   虚拟 HID 是**整份报告快照**模型 —— `WinUhidInjector.Send(keys)` 每次下发的都是
    ///   "此刻按下的全集"，只发单个按钮的键会把**别的**按住中的键从报告里挤掉
    ///   （系统立刻认为那个键抬起了）。
    ///
    /// 历史 bug（2026-10-15 线上排查确认）：这里原名 `_injectedNow`，是**单个全局槽**，
    /// 只记「最后一个按下的按钮」。于是「按住语音键(→右 Alt) → 再按确认 → 先松语音」时：
    ///   按语音 → 槽={0xA5}；按确认 → 槽={0x0D}（0xA5 被覆盖）
    ///   松语音 → 释放的是 {0x0D}（错的键），并把语音在 `_heldButtons` 里的登记一起删掉
    ///   ⇒ **看门狗再也看不到语音键，右 Alt 永久卡住**（实测症状：字母打不出、输入法失效）。
    /// </summary>
    private static int[] _injectedSet = Array.Empty<int>();
    private static readonly object _injLock = new();

    /// <summary>按 `_heldButtons` 重算"应该按着"的全集（整份报告的内容）。</summary>
    private int[] ComputeIntended()
    {
        lock (_heldLock)
        {
            var set = new SortedSet<int>();
            foreach (var e in _heldButtons.Values)
                foreach (var v in e.vks) set.Add(v);
            return set.ToArray();
        }
    }

    /// <summary>
    /// **宏播放器专用**：把一个组合键按下/抬起，复用现有的「整份报告快照」下发路径。
    ///
    /// 为什么不自己造一套注入：卡键防护（整份报告重算 / 孤儿键回收 / 看门狗）
    /// 全都挂在 <see cref="_heldButtons"/> + <see cref="InjectSet"/> 上。
    /// 宏如果绕过它们单独发键，一旦中途异常就是**永久卡键**，
    /// 而这个 bug 只在用户机器上偶发、几乎无法复现。
    ///
    /// `bid` 用 `__mac` 前缀，不会与任何遥控器按键 id 冲突，也不会去查映射表。
    /// </summary>
    public void InjectTransient(string bid, int[] vks, bool pressed)
    {
        // 释放时允许空 vks：那是"把这个 bid 全部放掉"的兜底用法
        if (pressed && (vks is null || vks.Length == 0)) return;
        if (pressed)
        {
            // 同名 bid 已按住 → 先放掉，避免"两次按下一次抬起"留下残影
            if (_heldButtons.ContainsKey(bid)) ForceReleaseButton(bid, "宏重复按下，先放掉上一次");
            lock (_heldLock) _heldButtons[bid] = (vks, DateTime.UtcNow);
            InjectSet(ComputeIntended());
        }
        else
        {
            bool had;
            lock (_heldLock) had = _heldButtons.Remove(bid);
            if (had) InjectSet(ComputeIntended());
        }
    }

    /// <summary>
    /// 本程序**可能**注入的全部 VK（所有配置组的映射值 + 全部修饰键）。
    ///
    /// 孤儿键回收器只在这个集合里找、绝不碰别的键 —— 所以连**上一个实例被强杀时
    /// 留下的卡住键**也能被下一个实例兜住（新实例的 `_heldButtons` 是空的，
    /// 但那台虚拟键盘可能还停在"按着"状态）。
    /// 每次 tick 重算（一秒一次，几十个元素，开销可忽略），这样改了映射也不用清缓存。
    /// </summary>
    private static int[] InjectableVks()
    {
        var set = new SortedSet<int>();
        try
        {
            for (var g = 0; g < Keymaps.Groups.Count; g++)
                foreach (var vks in Keymaps.MapOf(g).Values)
                    foreach (var v in vks) set.Add(v);
            for (var g = 0; g < Keymaps.Groups.Count; g++)
                foreach (var vks in Keymaps.FactoryOf(g).Values)
                    foreach (var v in vks) set.Add(v);
        }
        catch { }
        // ★ 宏里出现的键也必须算进来 —— 否则孤儿键回收器不认它们：
        //   宏播放中途崩溃留下的键会被当成"不是我们按的"而永远不释放。
        try { foreach (var v in Macros.AllInjectableVks()) set.Add(v); } catch { }
        foreach (var m in new[] { 0xA5, 0xA4, 0x12, 0xA3, 0xA2, 0x11, 0xA1, 0xA0, 0x10, 0x5B, 0x5C })
            set.Add(m);
        return set.ToArray();
    }

    /// <summary>
    /// 遥控器多久没有上报就认为「所有键都已松开」。
    ///
    /// **这是必须的保护**：遥控器在做时按住语音键会以 ~96 Hz 持续上报 DOWN，
    /// 但一旦它**休眠/断连/信号丢失**，最后一个 UP 就永远不会到达 ——
    /// `held` 集合会一直留着那个 VK，导致**真实键盘对应的字母被永久吞掉**
    /// （实测踩过：M 卡住，键盘打不出 m），甚至修饰键卡住会让整个键盘行为异常。
    /// 因为遥控器只要还在按住就一定会持续上报，所以「静默超时」是安全的判据。
    /// </summary>
    public const int HoldTimeoutMs = 800;
    private System.Threading.Timer? _watchdog;

    /// <summary>
    /// 是否注入映射快捷键。**变化必须留痕** —— 注入关掉时按下的键不会被登记进按住集合，
    /// 看门狗也就看不到（历史隐患来源）；以前这里是静默属性，事后完全查不出来。
    /// </summary>
    /// <summary>
    /// 鼠标模式路由钩子：`(buttonId, pressed) => 是否已消费`。
    /// 由 Backend 接到 <see cref="MouseEngine.HandleButton"/> 上。
    /// 用委托而不是直接引用 MouseEngine，是为了让 KeyFilter 不必知道鼠标这回事。
    /// </summary>
    public Func<string, bool, bool>? MouseModeRouter { get; set; }

    private volatile bool _injectEnabled = true;
    public bool InjectEnabled
    {
        get => _injectEnabled;
        set
        {
            if (_injectEnabled == value) return;
            _injectEnabled = value;
            try
            {
                EtwKeySource.AppendLog("INJ  InjectEnabled = " + (value ? "true" : "false")
                    + (value ? "" : "（注入已关闭：此后按下的键不注入、也不登记）"));
            }
            catch { }
        }
    }

    // ── 「按一下绑定」：捕获下一次真实按键 ──
    /// <summary>是否正在等待捕获下一次按键</summary>
    // 2026-10-27：`CaptureNext` / `CapturedVk` 是"按一下键来绑定"用的，但**从没有任何代码读取它们**
    //   （绑定走的是屏幕键盘 → saveKey）⇒ 连同后端 BeginBind/CancelBind 一起删除。
    /// <summary>捕获到的 VK（0 = 还没有）</summary>
    public int CapturedVk { get; private set; }
    /// <summary>捕获成功时触发</summary>
    // 2026-10-20：`OnCaptured` 从未被触发、也从未有人订阅 → 已删除（审查发现的死事件）。
    /// <summary>遥控器按键按下/抬起回调 (buttonId, pressed)</summary>
    public event Action<string, bool>? OnButton;
    /// <summary>任意按键回调（用于「当前操作」显示）</summary>
    // 2026-10-27：`OnAnyKey` 删除 —— 它只在已删的 Raw Input 路径里触发；
    //   "最近按键"由 ETW 路径（Backend 里 UsageToButton）维护，不依赖它。

    /// <summary>
    /// **语音键按下**时的回调（在注入按键之前触发）—— 用来预热音频链路。
    ///
    /// 开 CABLE Input 要 100~300 ms，而音频在按键后 100~200 ms 就到了；
    /// 不预热的话这段音频会被丢掉，用户看到的就是"按住后立刻说的第一个字识别不出来"。
    /// </summary>
    public static event Action? VoiceKeyDown;

    /// <summary>
    /// 按下绑定了动作值「切换配置组」的按键时触发（方块键的出厂值）。
    /// 由 <c>Backend</c> 接上：切到下一组 + 同步鼠标模式。
    /// 放在事件里而不是直接调 Keymaps 的原因：切组要顺带更新托盘提示与鼠标模式，
    /// 那些状态都在 Backend 手上。
    /// </summary>
    public static event Action? SwitchGroupRequested;
    /// <summary>最近一次失败原因（Raw Input 注册 / 装钩子）</summary>
    public string? LastError { get; private set; }

    // 2026-10-20：`Swallowed` / `Passed` 随计数器一起删除。
    public bool Running => _running;
    public bool DeviceSeen { get; private set; }

    // ── 启动 / 停止 ──
    // 注意：必须在窗体句柄创建完成之后再调用 Start —— 构造函数里访问 Form.Handle 会同步
    // 触发 OnHandleCreated，而此时字段尚未赋值，容易踩空引用。

    /// <summary>
    /// true = 按键来自 BthPort ETW（设备已被改成 HID-compliant device，系统不再当它键盘）。
    /// 此时**不注册 Raw Input、不装键盘钩子** —— 也就没有吞键、没有 held 集合、没有卡键风险。
    /// </summary>
    public bool KeySourceIsEtw { get; set; }

    public void Start(IntPtr hwnd)
    {
        if (_running) return;
        _hwnd = hwnd;
        _running = true;
        LastError = null;
        _lastRawInputAt = DateTime.UtcNow;

        // ★ 2026-10-18：**只有 ETW 这一种来源**。这里无条件返回 ——
        //   不再装低级钩子、也不再注册 Raw Input（Raw Input 在非键盘设备下收不到遥控器报文，
        //   实测 `遥控器 0 条`；而钩子会吞物理键盘的键，属于"键盘模式"遗留）。
        LastCleared = "按键来源＝ETW（全程不装钩子、不注册 Raw Input）";

        // ★ 卡键看门狗：**必须起**，而且与"装不装钩子"无关。
        //
        //   ⚠⚠ 2026-10-18 回归修复：v1.2.26 删钩子安装时，把这一行定时器**一起删了**，
        //      于是 `Watchdog()` 再也没有调用者 ⇒
        //        · WatchdogV2()（盯"注入了还没放开"，语音 3s/方向 60s/其它 15s）**从不运行**
        //        · ReapOrphanKeys()（以 GetAsyncKeyState 为准的孤儿键回收）**从不运行**
        //      两条卡键防线同时失效 —— 而 ETW 模式下它们本来是**唯一**的防线。
        //      （钩子可以删，看门狗不能删：它防的是"UP 丢了 → 永久卡键"，与按键来源无关。）
        _watchdog?.Dispose();
        _watchdog = new System.Threading.Timer(_ => Watchdog(), null, 300, 300);
    }

    /// <summary>长时间没有遥控器上报 → 清空按住集合并释放可能卡住的注入键</summary>
    // ── 语音键"抖动对"去抖（2026-10-19 全键抓包后确认）──
    //   实测报文：DOWN → UP(3/18/34 ms) → 41~163 ms 后真正的 DOWN
    //   ⇒ 输入法见到那个假 UP 就判"说完了"，表现为"还没说话就弹出已识别"。
    // ★★ 2026-10-20（第三遍按键检查·受控实验）：抖动对**不只出现在语音键**。
    //   用 UDP 复现 `DOWN → UP(5ms) → DOWN(41ms)` 实测：
    //     语音键 → INJECT 1 次（已去抖）
    //     OK 键 → INJECT **2 次** ⇒ 映射成 Enter 就会**发两次**
    //   所以去抖改为**全键生效**；状态按 bid 独立（否则两个键会互相取消挂起的释放）。
    /// <summary>
    /// 抖动对的判据：按住短于它才算"可能是假松开"。
    ///
    /// **取值依据（2026-10-20 重新核对抓包）**：
    /// · 真实抖动对：`DOWN → UP(3ms) → 41ms 后 DOWN` ← **3ms 是物理上按不出来的**，必是固件抖动；
    /// · 34ms 那一例后面隔了 **163ms** 才重按 —— 那是用户有意的"短按 + 长按"两次，**不是抖动**。
    /// ⇒ 阈值取 **25ms**：覆盖 3/18ms 的假松开，又不误伤 34ms 以上的真短按。
    /// </summary>
    private const double DebounceMs = 25;
    /// <summary>
    /// 挂起多久没再按下，才确认是真松开。实测抖动对的重按间隔 **41ms**，
    /// 取 **120ms**（≈3 倍余量），同时保证真轻点最多只延后 120ms 松开。
    /// </summary>
    private const int DebounceWindowMs = 120;

    /// <summary>
    /// **尾音窗口**（2026-10-29）：语音键松开后，晚这么久再发"松开"给输入法。
    ///
    /// 为什么需要：输入法收到 UP 就立刻收尾出字，而音频链本身有几十毫秒延迟
    /// （抖缓 + 声卡缓冲）。实测症状就是"最后一个字被吃掉"。
    /// 150 ms 足以覆盖，用户几乎无感（字晚 0.15 秒出现）。0 = 关闭。
    /// </summary>
    private const int VoiceTailMs = 150;
    private readonly Dictionary<string, int> _upToken = new();
    private readonly HashSet<string> _upPending = new();
    private readonly object _upLock = new();

    // 每个键上次"系统侧缺失"的补发时间（500ms 内不重复补，避免刷屏）
    private readonly Dictionary<int, DateTime> _lastReinject = new();
    private DateTime _lastHealthNote = DateTime.MinValue;

    /// <summary>
    /// **按键健康检查**（2026-10-19，对应"按住语音键、右 Alt 自己弹起"）。
    ///
    /// 背景：真机抓包证明报文是干净的（长按期间遥控器一条都不发，松手才发空报文），
    /// 程序也按 2.5 秒完整按住、准时松开。那么"自己弹起"只可能来自**系统侧把我们注入的
    /// 按住悄悄丢了** —— 虚拟 HID 设备被重枚举、系统电源管理、驱动重绑定都会这样。
    /// （与同类项目的"音频假正常"是同一类问题：**我们以为按着，系统里其实已经抬起**。）
    ///
    /// 判据：程序认为按着的每一个 VK，系统 `GetAsyncKeyState` 必须也是按下。
    /// 不是 ⇒ 记一条 `KEY HEALTH` 并**重新下发整份报告**把按住补回去。
    /// </summary>
    private void CheckHeldHealth()
    {
        try
        {
            List<(string bid, int[] vks)> snapshot;
            lock (_heldLock) snapshot = _heldButtons.Select(kv => (kv.Key, kv.Value.vks)).ToList();
            if (snapshot.Count == 0) return;

            var needFix = false;
            foreach (var (bid, vks) in snapshot)
                foreach (var vk in vks)
                {
                    if ((GetAsyncKeyState(vk) & 0x8000) != 0) continue;   // 系统里是按下的 ✓
                    needFix = true;
                    var now = DateTime.UtcNow;
                    if (_lastReinject.TryGetValue(vk, out var last) && (now - last).TotalMilliseconds < 500)
                        continue;
                    _lastReinject[vk] = now;
                    if ((now - _lastHealthNote).TotalMilliseconds > 1000)
                    {
                        _lastHealthNote = now;
                        Safe.Note($"KEY HEALTH  {bid}(0x{vk:X2}) 程序认为按着，但系统里已抬起 —— 重新注入");
                    }
                }

            if (needFix) InjectSet(ComputeIntended());   // 按整份报告重发，把按住的键补回去
        }
        catch (Exception e) { Safe.Log(e, "按键健康检查失败"); }
    }

    private void Watchdog()
    {
        try
        {
            CheckHeldHealth();
            // ★ 2026-10-27：删掉 v1（"遥控器静默超时"）—— 它依赖 Raw Input 的 `_held` 集合，
            //   而 ETW 是唯一来源后 `_held` **永远是空的** ⇒ 这个分支一次都不会命中（纯死代码）。
            //   真正的卡键防线是下面的 v2（盯"注入了还没放开"）与 v3（以系统状态为准）。
        }
        catch { }

        // v2 是真正管用的那条（ETW 模式下唯一有效的判据），必须无条件执行
        try { WatchdogV2(); }
        catch { }

        // v3：以**系统实际状态**为准的孤儿键回收 —— 当 v2 赖以判断的"账"本身被写错时，
        //     这是最后一道（实测确认过的失守场景见 ReapOrphanKeys 的注释）。
        try { ReapOrphanKeys(); }
        catch { }
    }

    /// <summary>
    /// 卡键看门狗 v2：盯「注入了还没放开」的按钮。
    ///
    /// v1 只在 Raw Input 模式有效（_held 只在 HandleRawInput 里维护），
    /// 而按键来源切到 ETW 后那条路径根本不跑 —— **丢一次 UP 就永久卡键**。
    /// v2 在 ApplyButton（两种来源共用）里登记，再按下面的判据强制放开：
    ///
    /// · 语音键：音频判据。按住期间蓝牙 SCO 一定在传音频帧（实测按下 → 首包 0~528 ms，
    ///   不说话也传静音帧），所以「按着 + 音频停了 &gt; 3 秒」= UP 丢了。
    ///   拿不到音频信号时退化为绝对超时（120 秒）。
    ///   ⚠ 不能用"遥控器静默"判 —— 实测正常按住 30 秒期间上报数也是 0。
    /// · 其它键：都是点按，按住超过 15 秒即视为卡死。
    /// </summary>
    private void WatchdogV2()
    {
        List<KeyValuePair<string, (int[] vks, DateTime at)>> held;
        lock (_heldLock) held = _heldButtons.ToList();
        if (held.Count == 0) return;

        foreach (var kv in held)
        {
            var (vks, at) = kv.Value;
            var heldSec = (DateTime.UtcNow - at).TotalSeconds;

            // 判据集中在 StuckGuard（纯函数，有单测覆盖），这里只负责取证据 + 执行。
            var why = StuckGuard.Decide(kv.Key, heldSec, AudioSilenceMs?.Invoke());
            if (why != null) ForceReleaseButton(kv.Key, why);
        }
    }

    /// <summary>强制放开一个卡住的按钮（补发 KEYUP + 释放虚拟 HID），并记日志</summary>
    private void ForceReleaseButton(string bid, string why)
    {
        int[] vks;
        lock (_heldLock)
        {
            if (!_heldButtons.Remove(bid, out var e)) return;
            vks = e.vks;
        }
        try
        {
            // ★ 把登记表删掉之后**重算整份报告**再下发（而不是只放这个按钮的键）——
            //   虚拟 HID 是快照模型，只有整份下发才能保证"报告的 == 我们以为按着的"。
            //   （历史 bug：这里原来只 InjectRelease(vks)，而 `_injectedNow` 并未被清，
            //     导致看门狗释放之后还留着一组旧键，之后任意一次松手都会去放那组旧键。）
            InjectSet(ComputeIntended());
        }
        catch { }
        var msg = $"GUARD  {bid}  强制释放  原因={why}  键={Keys.Format(vks)}";
        EtwKeySource.AppendLog(msg);
        GuardLog(msg);
        LastError = $"已自动释放卡住的 {bid}（{why}）";
    }

    /// <summary>卡键看门狗的动作日志（与 key-events.log 分开，方便一眼扫）</summary>
    private static readonly object _guardLogLock = new();
    private static void GuardLog(string msg)
    {
        if (!LogSwitch.Enabled) return;
        try
        {
            var p = Path.Combine(Env.ConfigDir, "stuck-guard.log");
            var body = $"{DateTime.Now:HH:mm:ss.fff}  {msg}\n";
            lock (_guardLogLock)
            {
                // ★ BOM 只能写一次。UTF8Encoding(true) 是"每次都写 BOM"，
                //   追加几次日志里就会夹进一堆 ï»¿。（router.log 那边是同样的处理）
                if (!File.Exists(p)) File.WriteAllText(p, body, new UTF8Encoding(true));
                else File.AppendAllText(p, body, new UTF8Encoding(false));
            }
        }
        catch { }
    }

    /// <summary>这个按钮已经按住了多少毫秒（没按着返回 -1）。</summary>
    public double HeldMs(string bid)
    {
        lock (_heldLock)
            return _heldButtons.TryGetValue(bid, out var e)
                ? (DateTime.UtcNow - e.at).TotalMilliseconds
                : -1;
    }

    /// <summary>是否**只有**这个按钮按着（用于"音频结束就释放语音键"的保守判断）。</summary>
    public bool IsOnlyHeld(string bid)
    {
        lock (_heldLock) return _heldButtons.Count == 1 && _heldButtons.ContainsKey(bid);
    }

    /// <summary>某个按钮现在是否真的在「按住」集合里（界面据此显示，而不是靠最后一个事件的方向）。</summary>
    public bool IsHeld(string bid)
    {
        lock (_heldLock) return _heldButtons.ContainsKey(bid);
    }

    /// <summary>清空「按住」集合并释放所有已注入未释放的键。返回清掉的键数。</summary>
    public int ClearHeld(string reason = "手动清空")
    {
        // ★ v2 看门狗的登记集合也要一起清，否则手动复位之后
        //   看门狗还会拿着一条已经不存在的"按住"记录去重复释放。
        List<int[]> heldButtons;
        lock (_heldLock)
        {
            heldButtons = _heldButtons.Values.Select(e => e.vks).ToList();
            _heldButtons.Clear();
        }

        // ★ 2026-10-18 修：原来这里用 `cleared.Length`（**Raw Input 的 _held**）当"释放了几个键"，
        //   而 ETW 模式下 _held 恒为空 ⇒ n 永远是 0 ⇒ **明明放掉了注入的键，却既不记日志、
        //   也不更新 LastCleared**。真机表现就是"按语音键后右 Alt 过了好几秒才自己弹起，
        //   而日志里什么都看不到"（排查时完全无法定位是谁放的）。
        //   现在用**真正放掉的按钮数**（heldButtons）来计数并留痕。
        var n = heldButtons.Count;
        lock (_lastState) _lastState.Clear();

        // ★ 归因必需：谁放掉的、为什么 —— 始终记录（不受诊断日志开关影响）
        if (n > 0) Safe.Note($"CLEARHELD  {reason}  释放 {n} 个：{string.Join(",", heldButtons.Select(v => Keys.Format(v)))}");

        if (n > 0)
        {
            var msg = $"GUARD  清空按住集合  {reason}  释放 {n} 个按键"
                      + $"（{string.Join(" ", heldButtons.Select(v => Keys.Format(v)))}）";
            EtwKeySource.AppendLog(msg);
            GuardLog(msg);
            LastCleared = $"{reason}（释放 {n} 个）";
        }

        foreach (var vks in heldButtons)
        {
            try { InjectRelease(vks); } catch { }
        }

        // ★ 给系统补发这些键的 KEYUP。
        //   遥控器丢 UP 时，Windows 会一直认为该键「按住」（表现为真实键盘对应字母打不出来、
        //   修饰键卡住会让整个键盘行为异常）。补一次 UP 就能把系统侧状态清干净。
        // 释放可能卡住的注入键（否则修饰键卡住会让整个键盘行为异常）
        // ★ 登记表已清空 → 整份报告重算出来就是空集 → 下发全零报告。
        if (_injectedSet.Length > 0 || _viaVirtualHid)
        {
            try { InjectSet(ComputeIntended()); } catch { }
            _viaVirtualHid = false;
        }
        return n;
    }

    /// <summary>最近一次清空的原因（给界面显示）</summary>
    public string? LastCleared { get; private set; }

    /// <summary>距离上一次遥控器上报的毫秒数；从未收到过返回 -1</summary>
    public long SinceLastInputMs =>
        _lastRawInputAt == DateTime.MinValue ? -1 : (long)(DateTime.UtcNow - _lastRawInputAt).TotalMilliseconds;

    /// <summary>切换按键来源（会重启钩子）</summary>
    public void SetSourceMode(bool etw)
    {
        if (KeySourceIsEtw == etw) return;
        var hwnd = _hwnd;
        Stop();
        KeySourceIsEtw = etw;
        // ★ 2026-10-29：原来 `if (hwnd != IntPtr.Zero) Start(hwnd)` —— 无界面模式（--headless）
        //   没有窗口句柄，于是看门狗（卡键兜底）**永远不启动**。现在无条件启动：
        //   hwnd 为 0 只是"收不到设备变更窗口消息"，看门狗与注入不受影响。
        Start(hwnd);
    }

    public void Stop()
    {
        _running = false;
        _watchdog?.Dispose();
        _watchdog = null;
        ClearHeld("程序停止");              // 关键：退出前一定要把注入的键放掉，避免修饰键卡死
    }


        // 2026-10-27：`HandleRawInput` 整块删除 —— WM_INPUT 从未被注册
    //   （`RegisterRawInputDevices` 从未调用），整条路径不可达；ETW 是唯一按键来源。

            /// <summary>
    /// 处理一次按键事件（**Raw Input 与 ETW 两条来源共用**）：通知订阅者 + 按映射注入快捷键。
    /// </summary>
    public void ApplyButton(string bid, bool pressed, string src = "rawinput")
    {        try { OnButton?.Invoke(bid, pressed); } catch { }

        // ★★ 宏播放期间**按任意遥控器键 = 停止**（2026-10-30 由"只放行触发键"改成这样）。
        //   理由：用户配完宏很可能忘了绑在哪个键上，那时唯一出口就应该是"随便按一个"；
        //   "按了没反应"比"被打断"更像故障。抬起不处理 —— 抬起也算中断会让一次按键触发两次停止。
        //
        //   为什么必须在 ApplyButton（而不是 KeyAct）里拦：宏发的键和用户按的键共用
        //   _heldButtons + 整份报告，两边同时改就会串键（宏按 Ctrl+A 的过程里插进一个方向键，
        //   结果不可预测）。
        if (MacroPlayer.IsRunning)
        {
            if (pressed) MacroPlayer.StopByKey(bid);
            EtwKeySource.AppendLog(
                $"APPLY  {bid}  {(pressed ? "DOWN" : "UP")}  来源={src}  宏播放中"
                + (pressed ? $"（{(MacroPlayer.IsTrigger(bid) ? "触发键" : "其他键")} ⇒ 停止）" : "，抬起忽略"));
            return;
        }

        // ★ 2026-10-29 无驱动模式：按住语音键期间吞掉遥控器自带的 31 Hz 幽灵 `m`。
        //   必须在这里（两种按键来源共用的漏斗）而不是注入路径 —— 幽灵键是**遥控器自己发的**，
        //   跟我们注入了什么无关。
        GhostGuard.NoteRemoteActivity();      // 遥控器活跃 → 由 ETW 时间戳驱动"先吞后补"的窗口
        if (bid == "voice")
        {
            if (pressed) GhostGuard.Arm(Environment.TickCount64);
            else GhostGuard.Disarm();
        }

        // ★ 鼠标模式：方向键 / 确认 / info 交给 MouseEngine 接管（它有自己的线程按 tick 下发位移）。
        //   必须在键盘映射**之前**分流，而且**不能**登记进 _heldButtons ——
        //   那会把鼠标动作塞进键盘的「整份报告」，反而发出真的按键。
        //   返回 true = 已消费，本函数直接结束。
        if (MouseModeRouter is { } router)
        {
            try
            {
                if (router(bid, pressed))
                {
                    EtwKeySource.AppendLog(
                        $"APPLY  {bid}  {(pressed ? "DOWN" : "UP")}  来源={src}  映射=（鼠标模式）");
                    return;
                }
            }
            catch { }
        }

        // ★ 映射成**动作值**的按键（鼠标左键/右键/滚轮上/滚轮下、切换配置组）。
        //   鼠标类走 SendInput（MouseInjector）；切组走事件回调。
        //   **绝不登记进 _heldButtons** —— 那会把它们塞进键盘的「整份报告」，
        //   反而发出一个真的键盘按键。与上面鼠标模式的方向键分流是同一套做法。
        //   放在这里（鼠标模式分流之后、键盘映射之前）的原因：鼠标模式里的方向键
        //   优先级更高，其余按键才轮到映射表。
        {
            var mv = Keymaps.Get(bid);
            if (KeyAct.IsSingleAction(mv))
            {
                var dv = mv[0];
                var name = KeyAct.NameOf(dv) ?? "?";

                // 「切换配置组」：按下即生效（等抬起 = 丢一次 UP 这个键就失灵）
                if (dv == KeyAct.SwitchGroup)
                {
                    EtwKeySource.AppendLog(
                        $"APPLY  {bid}  {(pressed ? "DOWN" : "UP")}  来源={src}  映射={name}（动作）");
                    if (pressed) { try { SwitchGroupRequested?.Invoke(); } catch { } }
                    return;
                }

                EtwKeySource.AppendLog(
                    $"APPLY  {bid}  {(pressed ? "DOWN" : "UP")}  来源={src}  映射={name}（动作）");
                if (pressed && !InjectEnabled)
                {
                    EtwKeySource.AppendLog($"INJ  {bid} 未注入：InjectEnabled=false（映射={name}）");
                    return;
                }
                // 滚轮的抬起什么都不做（只在按下时滚一格，见 KeyAct 的注释）
                if (pressed || dv == KeyAct.Left || dv == KeyAct.Right)
                {
                    if (!KeyAct.Apply(dv, pressed))
                        EtwKeySource.AppendLog(
                            $"INJ  {bid} 鼠标动作未生效：{MouseInjector.LastError ?? "SendInput 被拒（前台窗口权限更高？）"}");
                }
                return;
            }
        }

        // ★★ 关键修复：注入必须用**当前配置组**（Keymaps），不能用 Keys.Load()。
        //
        // 历史 bug：这里原来调的是 `Keys.Load()` —— 那是**旧版扁平格式**的读取器，
        // 它把 keymap.json 里的多配置组结构 {Ver,Active,Groups:[…]} 反序列化成
        // Dictionary<string,int[]> 会抛异常，然后 catch 里**静默回落到 Keys.Table 的出厂默认**。
        // 结果：界面（走 Keymaps）显示的是用户配置，实际注入（走 Keys）却是硬编码默认，
        // 两套数据源共用同一个文件路径却各读各的 → 用户怎么改都不生效。
        // 现象：back 配 Backspace 却注入 Alt+←、menu 配 Win+X 却注入菜单键、info 配 Ctrl+Enter 却注入 Alt+Enter。
        var vks = Keymaps.Get(bid);
        if (vks.Length == 0)
        {
            // 记录"按下了但没映射"，否则日志里这一键完全消失，看不出是没配置还是没识别
            if (pressed) EtwKeySource.AppendLog($"APPLY  {bid}  DOWN  来源={src}  映射=（未设置）");
            return;
        }

        // ★ 2026-10-18：把"这一按持续了多久"写进日志。
        //   真机排查时最有用的一条：豆包「长按说话」要求 ≥300~500ms，
        //   而实测有些按下只有 213ms（等于点击）—— 光看"按了没反应"根本分不出
        //   是程序没注入、还是这一按本身太短。
        double holdMs = -1;
        if (!pressed)
        {
            lock (_heldLock)
                if (_heldButtons.TryGetValue(bid, out var e0))
                    holdMs = (DateTime.UtcNow - e0.at).TotalMilliseconds;
        }
        // 语音键是核心功能，它的每次按下/抬起都始终留痕（含按住时长），便于事后归因
        if (bid == "voice")
            Safe.Note($"VOICE {(pressed ? "DOWN" : "UP")}  来源={src}" + (holdMs >= 0 ? $"  按住 {holdMs:F0} ms" : ""));

        // ★★★ 语音键"抖动对"去抖（2026-10-19 全键抓包确认的真实报文形态）：
        //   遥控器在**按住开始**时会先发一个极短的 DOWN→UP（实测 3 / 18 / 34 ms），
        //   41~163 ms 之后才是真正持续的 DOWN。
        //   输入法见到那个假 UP 就认为"这次说话结束了"⇒ 用户看到"还没说话就弹出已识别"。
        //   处理：**超短 UP 不立即释放**，挂起 100 ms；
        //   · 期间来了 DOWN ⇒ 判为抖动对，**完全不发 UP**（当作同一次按住，豆包毫无感知）；
        //   · 100 ms 内没有 DOWN ⇒ 说明是真的轻点，这时才释放（最多延迟 100 ms）。
        if (!pressed && src == "etw" && holdMs >= 0 && holdMs < DebounceMs)
        {
            int tok;
            lock (_upLock)
            {
                _upToken.TryGetValue(bid, out var prev);
                tok = prev + 1;
                _upToken[bid] = tok;
                _upPending.Add(bid);
            }
            if (bid == "voice")
                Safe.Note($"VOICE UP 去抖挂起（按住 {holdMs:F0} ms < {DebounceMs}ms，疑似抖动对）");
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                await System.Threading.Tasks.Task.Delay(DebounceWindowMs);
                lock (_upLock)
                {
                    if (!_upToken.TryGetValue(bid, out var cur) || cur != tok) return;   // 期间又按下了
                    _upPending.Remove(bid);
                }
                try { ForceReleaseButton(bid, $"去抖 {DebounceWindowMs}ms 后仍无按下 ⇒ 确认松开"); } catch { }
            });
            return;
        }

        // ★ 尾音窗口：真正的松开也晚一点发，避免最后一点音频被切断
        if (!pressed && bid == "voice" && src == "etw" && VoiceTailMs > 0)
        {
            int tok2;
            lock (_upLock)
            {
                _upToken.TryGetValue(bid, out var prev2);
                tok2 = prev2 + 1;
                _upToken[bid] = tok2;
                _upPending.Add(bid);
            }
            Safe.Note($"VOICE UP 进入尾音窗口 {VoiceTailMs}ms（推迟释放，避免吞掉最后一个字）");
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                await System.Threading.Tasks.Task.Delay(VoiceTailMs);
                lock (_upLock)
                {
                    if (!_upToken.TryGetValue(bid, out var cur2) || cur2 != tok2) return;
                    _upPending.Remove(bid);
                }
                try { ForceReleaseButton(bid, $"尾音 {VoiceTailMs}ms 已过 ⇒ 释放"); } catch { }
            });
            return;
        }

        EtwKeySource.AppendLog($"APPLY  {bid}  {(pressed ? "DOWN" : "UP")}  来源={src}" +
            $"  映射={Keys.Format(vks)}  组={Keymaps.Current.Name}" +
            (holdMs >= 0 ? $"  按住 {holdMs:F0} ms" : ""));

        if (pressed)
        {
            // ★ 注入被关掉时**不能**登记进按住集合 —— 登记了但没真注入，看门狗会去释放
            //   一个根本没按下过的键。这里显式留痕（以前是静默 return，事后查不出来）。
            if (!InjectEnabled)
            {
                EtwKeySource.AppendLog($"INJ  {bid} 未注入：InjectEnabled=false（映射={Keys.Format(vks)}）");
                return;
            }

            // ★ 语音键按下 → 先预热音频链路（把 CABLE Input 提前打开），再注入按键。
            //   顺序很重要：开声卡要 100~300 ms，而音频随后就到 —— 要先开声卡再注入，
            //   否则那段时间的音频会被丢掉，表现为"按住后立刻说的第一个字识别不出来"。
            // 抖动对：刚才那次"松开"是假的 ⇒ 取消挂起的释放，保持按住（对外只看到一次连续按住）
            {
                bool merged;
                lock (_upLock)
                {
                    merged = _upPending.Remove(bid);
                    if (merged) _upToken[bid] = (_upToken.TryGetValue(bid, out var t) ? t : 0) + 1;
                }
                if (merged)
                {
                    if (bid == "voice") Safe.Note("VOICE DOWN 命中抖动对 → 已合并为同一次按住（不发 UP）");
                    lock (_heldLock) _heldButtons[bid] = (vks, DateTime.UtcNow);
                    InjectSet(ComputeIntended());
                    return;
                }
            }
            if (bid == "voice") { try { VoiceKeyDown?.Invoke(); } catch { } }

            bool dup; double dupAgeMs = 0;
            lock (_heldLock)
            {
                dup = _heldButtons.TryGetValue(bid, out var dupEnt);
                if (dup) dupAgeMs = (DateTime.UtcNow - dupEnt.at).TotalMilliseconds;
            }
            if (dup)
            {
                // ★★ 2026-10-18（真机"第一次正常、第二次唤不醒"）：
                //   这里原来**无条件忽略**重复 DOWN。只要有一次 UP 丢了，
                //   `_heldButtons` 里就一直留着这个键 ⇒ **之后每一次按下都被当成"抖动"忽略**
                //   ⇒ 这个键永久失效（豆包再也唤不醒），只能靠紧急复位救回来。
                //
                //   正确判据：**离上次按下有多久**。
                //     · <300 ms → 真的是遥控器抖动/自动重复 → 忽略（保留原意）；
                //     · ≥300 ms → 上一次的 UP 丢了 → **先强制释放，再正常注入这一次**。
                // ★ ETW 来源**不会产生"自动重复的 DOWN"**：桥已经做过状态差分，
                //   只有"状态真的从抬起变成按下"才会发 DOWN。所以 ETW 下的重复 DOWN
                //   **必然是上一次的 UP 丢了** ⇒ 无条件自愈（门槛只会让"快速按第二次"漏掉）。
                //   只有 Raw Input 来源（钩子直读）才需要 300ms 门槛挡自动重复。
                if (src != "etw" && dupAgeMs < 300)
                {
                    EtwKeySource.AppendLog($"INJ  {bid} 重复 DOWN（距上次 {dupAgeMs:F0} ms，判为抖动）—— 已忽略");
                    return;
                }
                ForceReleaseButton(bid, $"再次按下但距上次 {dupAgeMs:F0} ms —— 上一次的 UP 丢了");
            }

            // 记进"按住未放"登记表 —— 看门狗 v2 靠它发现丢 UP 的卡键（两种按键来源都走这里）
            lock (_heldLock) _heldButtons[bid] = (vks, DateTime.UtcNow);
            InjectSet(ComputeIntended());          // 按"全集"下发，不挤掉别的按住中的键
        }
        else
        {
            bool had;
            lock (_heldLock) had = _heldButtons.Remove(bid);
            if (!had)
            {
                // 没登记过就收到 UP：可能是看门狗/紧急复位已经替我们放过了，
                // 也可能是遥控器发了个孤立 UP。
                // ★ 这里**刻意不再补发释放** —— 补发会去动一个我们从未按下过的键，
                //   万一用户此刻正按住真实键盘的同名键就会被误放。
                //   真的还有残留的话，由孤儿键回收器（以系统实际状态为准）兜住。
                EtwKeySource.AppendLog($"INJ  {bid} UP 到达但按住集合里没有它（可能已被看门狗/紧急复位释放）—— 不补发");
                return;
            }
            // ★ 释放的是"移掉这个按钮之后剩下的全集"，而不是这个按钮自己的键 ——
            //   否则会把别的按住中的键一起放掉（虚拟 HID 是整份报告快照模型）。
            InjectSet(ComputeIntended());
        }
    }

    /// <summary>
    /// 把虚拟键盘的报告**整份**设为 <paramref name="intended"/>（= 当前所有「按住中」按钮的键并集）。
    ///
    /// 这是修掉"卡键"的核心：任何一次变化都重算全集并整份下发，
    /// 所以**永远不会出现"我们以为按着、报告里其实没有"或反过来**。
    /// 日志按**变化量**分别记 DOWN/UP，格式与历史一致（分析脚本可继续用）。
    /// </summary>
    private static DateTime _lastHidFailLog = DateTime.MinValue;

    private static void InjectSet(int[] intended)
    {
        int[] before;
        lock (_injLock) { before = _injectedSet; _injectedSet = intended; }

        var added = intended.Where(v => !before.Contains(v)).ToArray();
        var removed = before.Where(v => !intended.Contains(v)).ToArray();
        if (added.Length == 0 && removed.Length == 0) return;   // 报告没变，不必下发

        // ── 媒体键：走 SendInput —— WinUHid 键盘设备表达不了 Consumer Page ──
        //    它们是点按语义、不参与"整份报告"，所以按变化量单独发。
        var addMedia = added.Where(IsMedia).ToArray();
        var remMedia = removed.Where(IsMedia).ToArray();
        if (addMedia.Length > 0) { Send(addMedia.OrderBy(v => IsModifier(v) ? 0 : 1), false); InjectLog(false, addMedia, false); }
        if (remMedia.Length > 0) { Send(remMedia.OrderByDescending(v => IsModifier(v) ? 0 : 1), true); InjectLog(true, remMedia, false); }

        // ── 其余键：整份报告下发（虚拟 HID 是快照模型）──
        var report = intended.Where(v => !IsMedia(v)).ToArray();
        var beforeK = before.Where(v => !IsMedia(v)).ToArray();
        var addK = added.Where(v => !IsMedia(v)).ToArray();
        var remK = removed.Where(v => !IsMedia(v)).ToArray();
        // 只有媒体键变化时没有"报告"要下发，别白建一台虚拟键盘
        if (report.Length == 0 && beforeK.Length == 0) return;

        if (WinUhidInjector.Send(report))
        {
            _viaVirtualHid = true;
            if (addK.Length > 0) InjectLog(false, addK, true);
            if (remK.Length > 0) InjectLog(true, remK, true);
            return;
        }

        // ★ 虚拟 HID 没就绪 / 下发失败：**必须留痕**，否则就是
        //   "我们以为按着、系统其实没收到"，事后完全查不出来。
        //   （限流 5 秒一条，免得整条链路失效时把日志刷爆）
        if ((DateTime.UtcNow - _lastHidFailLog).TotalSeconds >= 5)
        {
            _lastHidFailLog = DateTime.UtcNow;
            EtwKeySource.AppendLog($"INJ  ★ 虚拟 HID 下发失败（{WinUhidInjector.LastError ?? "未知"}）—— 回退 SendInput");
        }
        _viaVirtualHid = false;
        if (addK.Length > 0) { Send(addK.OrderBy(v => IsModifier(v) ? 0 : 1), false); InjectLog(false, addK, false); }
        if (remK.Length > 0) { Send(remK.OrderByDescending(v => IsModifier(v) ? 0 : 1), true); InjectLog(true, remK, false); }
    }

    // ── 注入（按住语义：按下注入，松手释放） ──

    private static void Send(IEnumerable<int> vks, bool up)
    {
        var list = vks.ToList();
        if (list.Count == 0) return;
        var inputs = new INPUT[list.Count];
        for (var i = 0; i < list.Count; i++)
        {
            var vk = (ushort)list[i];
            uint flags = up ? KEYEVENTF_KEYUP : 0;
            // 扩展键：右 Alt/Ctrl、媒体键等必须带 EXTENDEDKEY
            if (vk is 0xA5 or 0xA3 or 0x5B or 0x5C or 0xAD or 0xAE or 0xAF or 0xB0 or 0xB1 or 0xB3
                or 0x21 or 0x22 or 0x25 or 0x26 or 0x27 or 0x28 or 0x2C or 0x2D or 0x2E or 0x6F or 0xDC)
                flags |= KEYEVENTF_EXTENDEDKEY;
            inputs[i] = new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = vk, dwFlags = flags } };
        }
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    /// <summary>本次按下是否走的 WinUHid 虚拟键盘（释放要走同一条路）</summary>
    private static bool _viaVirtualHid;


    /// <summary>
    /// 连发循环：**按绝对时间轴排程**，不是"每次睡一个间隔"。
    ///
    /// 为什么不用 `System.Threading.Timer`：Windows 默认定时器精度约 **15.6 ms**，
    /// 40 ms 的间隔实际会抖动到 40~60 ms 且误差累积 —— 这就是"有时候会顿"的来源。
    /// 这里配合 `timeBeginPeriod(1)` 用 Stopwatch 对齐每个时间点，
    /// 并且"落后就立刻补偿"（不叠加、不漂移）。
    /// </summary>


    /// <summary>按下：**优先 WinUHid 虚拟 HID**（输入法不过滤），失败才回退 SendInput。</summary>
    /// <summary>
    /// Consumer Page（usage page 0x0C）的媒体键：静音 / 音量± / 播放暂停 / 上一曲下一曲 …
    /// 这些**不是键盘页的 usage**，WinUHid 的键盘设备表达不了（Usage() 里没有映射，
    /// 会被静默丢弃）—— 所以必须改走 SendInput，它原生支持媒体键。
    /// 音量/播放对延迟不敏感，走 SendInput 完全没问题。
    /// </summary>
    private static readonly HashSet<int> MediaVks = new()
    {
        0xAD, 0xAE, 0xAF,          // 静音 / 音量- / 音量+
        0xB0, 0xB1, 0xB2, 0xB3,    // 下一曲 / 上一曲 / 停止 / 播放暂停
        0xB4, 0xB5, 0xB6, 0xB7,    // 邮件 / 媒体选择 / …
    };

    private static bool IsMedia(int vk) => MediaVks.Contains(vk);

    /// <summary>注入侧日志：我们到底往系统里发了什么键（钩子侧已证明没漏 i，只剩这条路没记录）</summary>
    private static void InjectLog(bool up, int[] vks, bool uhid)
    {
        if (vks.Length == 0) return;
        EtwKeySource.AppendLog($"INJECT {(up ? "UP  " : "DOWN")} " +
            string.Join("+", vks.Select(v => $"0x{v:X2}")) +
            $" via={(uhid ? "winuhid" : "sendinput")}");
    }

    private static void InjectPress(int[] vks)
    {
        // 媒体键（音量/静音/播放…）走 SendInput —— WinUHid 键盘设备表达不了 Consumer Page
        if (vks.Length > 0 && vks.Any(IsMedia))
        {
            Send(vks.OrderBy(v => IsModifier(v) ? 0 : 1), false);
            InjectLog(false, vks, false);
            _viaVirtualHid = false;
            return;
        }
        if (WinUhidInjector.Send(vks)) { _viaVirtualHid = true; InjectLog(false, vks, true); return; }
        _viaVirtualHid = false;
        Send(vks.OrderBy(v => IsModifier(v) ? 0 : 1), false);
        InjectLog(false, vks, false);
    }

    /// <summary>释放：与按下走同一条路（虚拟键盘发全零报告）</summary>
    public static void InjectUp(int[] vks) => InjectRelease(vks);

    /// <summary>查询系统当前是否认为该键处于「按下」（高位置 1）—— 用于验证注入是否被系统接受</summary>
    public static short AsyncState(int vk) => GetAsyncKeyState(vk);

    /// <summary>
    /// 注入验证：按下 → 用 GetAsyncKeyState 看系统是否真的认为它按下了 → 释放。
    ///
    /// 这是排查「某些键注入无效」的关键手段：如果按住时高位置 1，说明**系统收到了**，
    /// 问题在应用层（热键判定/时序）；如果一直不置位，说明**注入链路本身**没送出去。
    /// </summary>
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr h, System.Text.StringBuilder t, int n);

    private static string ForeTitle()
    {
        var sb = new System.Text.StringBuilder(256);
        GetWindowText(GetForegroundWindow(), sb, 256);
        return sb.ToString();
    }

    /// <summary>组合键验证：按住组合键一段时间再释放，并报告前台窗口标题变化（用于验证 Win+R 之类的系统热键）。</summary>
    public static string InjectComboVerify(IEnumerable<int> vks)
    {
        var list = vks.ToArray();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("组合键 VK = " + string.Join(",", list.Select(v => $"0x{v:X2}")));
        sb.AppendLine("注入前前台窗口: " + ForeTitle());
        try { InjectPress(list); } catch (Exception e) { sb.AppendLine("异常: " + e.Message); }
        Thread.Sleep(600);
        sb.AppendLine("按住中前台窗口: " + ForeTitle());
        try { InjectRelease(list); } catch { }
        Thread.Sleep(700);
        sb.AppendLine("释放后前台窗口: " + ForeTitle() + "   ← 若弹出了系统窗口说明组合键生效");
        return sb.ToString();
    }

    public static string InjectVerify(int vk)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"目标 VK = 0x{vk:X2}");
        sb.AppendLine($"注入前   GetAsyncKeyState = 0x{AsyncState(vk):X4}");
        try { InjectPress(new[] { vk }); } catch (Exception e) { sb.AppendLine("注入异常: " + e.Message); }
        Thread.Sleep(400);
        sb.AppendLine($"按住中   GetAsyncKeyState = 0x{AsyncState(vk):X4}   ← 高位=1 表示系统已收到");
        try { InjectRelease(new[] { vk }); } catch { }
        Thread.Sleep(300);
        sb.AppendLine($"释放后   GetAsyncKeyState = 0x{AsyncState(vk):X4}");
        return sb.ToString();
    }


    /// <summary>
    /// **多键同时按住**的回归自检 —— 对应的正是 2026-10-15 查出的卡键缺陷。
    ///
    /// 复现序列：「按住 A → 再按 B → 先松 A → 再松 B」。
    /// 旧实现（单槽 `_injectedNow`）在这一串里 **A 的键再也不会被释放**：
    ///   按 A → 槽={A}；按 B → 槽={B}（A 被覆盖）；松 A → 释放的是 {B}，
    ///   并把 A 在 `_heldButtons` 里的登记一起删掉 ⇒ 看门狗也看不到 A → 永久卡键。
    ///
    /// 断言全部用 `GetAsyncKeyState` 读**系统真实状态**，不看我们自己的账：
    ///   ① 按 A 后          → A 按下、B 未按
    ///   ② 再按 B 后        → **A 仍按下**（整份报告快照；旧的单键实现会把 A 挤掉）
    ///   ③ 松 A 后          → A 抬起、**B 仍按下**
    ///   ④ 松 B 后          → **两者都抬起** ← 这一条就是卡键的直接判据
    /// </summary>
    public static string InjectMultiKeyTest(int vkA = 0xA5, int vkB = 0x0D)
    {
        bool Read(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("IPRC1000 多键同时按住回归自检");
        sb.AppendLine($"A = 0x{vkA:X2}   B = 0x{vkB:X2}");
        sb.AppendLine("判据 = 虚拟 HID 的「整份报告快照」语义：只发单键会把它从报告里挤掉。");
        sb.AppendLine();

        var ok = true;
        void Step(string name, int[] set, bool expectA, bool expectB, string expect)
        {
            try { InjectSet(set); }
            catch (Exception e) { sb.AppendLine($"  ✗ {name}  注入异常：{e.Message}"); ok = false; return; }
            Thread.Sleep(130);
            var a = Read(vkA);
            var b = Read(vkB);
            var pass = a == expectA && b == expectB;
            ok &= pass;
            sb.AppendLine($"  {(pass ? "✓" : "✗")} {name}");
            sb.AppendLine($"      期望 {expect}｜实测 A={(a ? "按下" : "抬起")} B={(b ? "按下" : "抬起")}");
        }

        Step("① 按下 A", new[] { vkA }, true, false, "A 按下、B 未按");
        Step("② 再按下 B", new[] { vkA, vkB }, true, true, "A、B 都按下（A 不能被挤掉）");
        Step("③ 松开 A", new[] { vkB }, false, true, "A 抬起、B 仍按下");
        Step("④ 松开 B ★ 卡键判据", Array.Empty<int>(), false, false, "A、B 都抬起");

        sb.AppendLine();
        sb.AppendLine(ok ? "⇒ 全部通过 ✓ 多键同时按住不会卡键" : "⇒ ★ 有断言失败 —— 会出现卡键！");

        // 无论如何先确保都放掉，别把自检本身变成一次卡键
        try { InjectSet(Array.Empty<int>()); } catch { }
        try { WinUhidInjector.Release(); } catch { }
        return sb.ToString();
    }

    private static void InjectRelease(int[] vks)
    {
        if (vks.Length > 0 && vks.Any(IsMedia))
        {
            Send(vks.OrderByDescending(v => IsModifier(v) ? 0 : 1), true);
            InjectLog(true, vks, false);
            return;
        }
        if (_viaVirtualHid && WinUhidInjector.Release()) { InjectLog(true, vks, true); return; }
        Send(vks.OrderByDescending(v => IsModifier(v) ? 0 : 1), true);
        InjectLog(true, vks, false);
    }

    private static bool IsModifier(int vk) =>
        vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);

    /// <summary>
    /// 注入自检：真的按下 / 松开一次右 Alt，并用 GetAsyncKeyState 读回状态。
    /// 用来证明「遥控器语音键 → 右Alt 按住 → 松手释放」这条注入链路可用。
    /// </summary>
    public static string InjectSelfTest(int vk = 0xA5)
    {
        bool Read() => (GetAsyncKeyState(vk) & 0x8000) != 0;

        // ① 虚拟 HID（WinUHid）—— 输入法不会过滤
        var uhidReady = WinUhidInjector.Ready || WinUhidInjector.EnsureReady();
        var b1 = Read();
        var ok1 = false; var mid1 = false; var end1 = false;
        if (uhidReady)
        {
            WinUhidInjector.Send(new[] { vk }); Thread.Sleep(150); mid1 = Read();
            WinUhidInjector.Release();          Thread.Sleep(150); end1 = Read();
            ok1 = !b1 && mid1 && !end1;
        }

        // ② SendInput 兜底
        var b2 = Read();
        Send(new[] { vk }, false); Thread.Sleep(150);
        var mid2 = Read();
        Send(new[] { vk }, true);  Thread.Sleep(150);
        var end2 = Read();

        return System.Text.Json.JsonSerializer.Serialize(new
        {
            vk = $"0x{vk:X2}",
            name = VkLabel(vk),
            virtualHid = new { ready = uhidReady, driverVersion = WinUhidInjector.DriverVersion,
                               error = WinUhidInjector.LastError,
                               beforePress = b1, whileHeld = mid1, afterRelease = end1, ok = ok1 },
            sendInput = new { beforePress = b2, whileHeld = mid2, afterRelease = end2,
                              ok = !b2 && mid2 && !end2 },
            ok = ok1,
        });
    }

    private static string VkLabel(int vk) => vk switch
    {
        0xA5 => "右 Alt", 0xA4 => "左 Alt", 0xA3 => "右 Ctrl", 0xA2 => "左 Ctrl",
        0x5B => "左 Win", 0x20 => "空格", 0x0D => "回车", _ => $"VK_0x{vk:X2}",
    };

    /// <summary>
    /// 连发注入精度自测：按 `intervalMs` 做 `n` 次"松开+按下"点按，
    /// 报告**每个时间点的计划 vs 实际**（我们的循环精度）以及耗时分布（含注入投递）。
    /// </summary>

    /// <summary>
    /// 释放所有可能残留的注入键。
    ///
    /// 为什么必须有：为「豆包长按说话」注入的**右 Alt(AltGr)** 是按住不放的；
    /// 若 App 在按住期间退出/被强杀，系统里就会留下一个「按着不放的 AltGr」——
    /// **后果是字母键输出特殊字符、中文输入法直接失效**（用户实测踩到）。
    /// 退出路径能跑到就调这里；跑不到（taskkill /F）则由启动时的兜底清理补上。
    /// </summary>
    /// <summary>
    /// **清理上一次异常退出残留的任意卡键**（2026-10-29 真机事故）。
    ///
    /// 事故：用户键盘的 `m` 打不出来，且拔掉遥控器电池也照样按 31Hz 冒 `m`。
    /// 用低级钩子观察者抓到铁证：`scan=0x32 flags=0x00 async=0x8001` ——
    /// 是**系统认为 m 一直按住**在自动重复（不是遥控器、也不是注入）。
    /// 源头是虚拟键盘设备里残留的一份"m 按下"报告（进程被强杀/崩溃时没发释放）。
    ///
    /// 为什么原来清不掉：`ReleaseAllInjected` 只在**本进程**持有键时才发全零；
    /// 残留来自**上一个进程**，`_injectedSet` 是空的 ⇒ 跳过；而 SendInput 兜底只补修饰键。
    ///
    /// 现在的做法：启动时扫一遍**可能被注入过的键**，只要发现"系统认为还按着"的，
    /// 就发一次全零报告 + 针对性 KEYUP。正常启动（没人按着）不会触发，
    /// 所以不会重演"每次启动弹右键菜单"那个老坑。
    /// </summary>
    public static int ReleaseStuckKeys()
    {
        try
        {
            // 只看"我们会注入的键"：字母、数字、常用功能键、修饰键。范围收窄是有意的 ——
            // 避免误释放用户启动时正按着的无关键。
            var watch = new List<int>();
            for (var vk = 0x08; vk <= 0xFE; vk++) watch.Add(vk);

            var stuck = new List<int>();
            foreach (var vk in watch)
            {
                // 0x8000 = 当前按下。启动瞬间若真有键按着，多半是残留。
                if ((GetAsyncKeyState(vk) & 0x8000) != 0) stuck.Add(vk);
            }
            if (stuck.Count == 0) return 0;

            // ① 虚拟键盘：发全零报告（这是清"上一个进程设备"最有效的手段）
            try { WinUhidInjector.Release(); } catch { }
            // ② 系统侧：针对性补 KEYUP（只对确实按着的键）
            foreach (var vk in stuck)
            {
                try { Send(new[] { vk }, true); } catch { }
            }
            Safe.Note($"GHOST 启动清理卡键：{string.Join(",", stuck.Select(v => "0x" + v.ToString("X2")))}");
            return stuck.Count;
        }
        catch { return 0; }
    }

    public static void ReleaseAllInjected()
    {
        int[] held;
        lock (_injLock) held = _injectedSet;

        // ① 虚拟 HID：**只有我们真的持有键时**才发全零报告。
        //
        //   ★★ 2026-10-17 真机事故（本次用低级钩子观察者抓到，铁证）：
        //      无条件发这一帧 ⇒ Windows 给**所有键**补发 KEYUP（新虚拟键盘的首帧就是这样初始化的），
        //      其中包含 **Apps 键 0x5D** ⇒ Explorer 收到"Apps 抬起"就弹出右键菜单。
        //      表现：**每次启动程序都弹一次右键菜单**，而且与鼠标/键盘怎么启动无关。
        //      观察者原始记录：`001172 KEY vk=0x5D UP flags=144`（144 = 已注入），
        //      同一时刻我们的日志是 `UHID frame 00-00-00-00-00-00-00-00`。
        if (held.Length > 0)
        {
            try { WinUhidInjector.Release(); } catch { }
        }

        // ② SendInput 兜底：**只对系统里确实还按着的键**补 KEYUP。
        //    （原来是无条件逐个补，等于每次启动都往系统里灌一遍"全键抬起"——
        //      同样是弹菜单的来源。干净启动时一个键都不会发。）
        //    这样"上一个实例被强杀留下卡住的 AltGr"照样能清掉（系统会报它按着）。
        foreach (var vk in InjectableVks())
        {
            if ((GetAsyncKeyState(vk) & 0x8000) == 0) continue;
            try { Send(new[] { vk }, true); } catch { }
        }
        lock (_injLock) _injectedSet = Array.Empty<int>();
    }

    /// <summary>
    /// **启动兜底**：清掉上一个实例被强杀时留在系统里的卡住修饰键。
    ///
    /// 为什么需要：退出路径（<see cref="ReleaseAllInjected"/>）在 `taskkill /F` 或崩溃时
    /// **跑不到**，系统里就会留下一个"按着不放的右 Alt" —— 后果是字母键输出特殊字符、
    /// 中文输入法直接失效（线上实测踩到）。那台虚拟键盘是**上一个进程**的设备，
    /// 我们的全零报告清不掉它，只能补一次 SendInput 的 KEYUP 把系统侧的合并状态压回去。
    ///
    /// 为什么安全：只动**修饰键**，而且只动 `GetAsyncKeyState` **确实报"按着"**的那些 ——
    /// 刚启动这一刻用户不会正按着修饰键；每次动作都写 stuck-guard.log，事后可查。
    /// 想关掉：设 `IPRC1000_NO_START_CLEAN=1`。
    /// </summary>
    /// <summary>
    /// 系统当前认为"被按住"的键（只扫我们可能注入的那批）。
    /// 用途：面板诊断用 —— 卡键时一眼看出是哪个键没放开，
    /// 而不是只能描述"打字一直重复"。
    /// </summary>
    public static string HeldKeysNow()
    {
        var held = new List<int>();
        try
        {
            foreach (var vk in StartupCleanVks().Distinct())
                if ((GetAsyncKeyState(vk) & 0x8000) != 0) held.Add(vk);
        }
        catch { }
        return held.Count == 0 ? "" :
            string.Join(" ", held.Select(v => $"{Keys.VkLabel(v)}(0x{v:X2})"));
    }

    /// <summary>
    /// 启动清理要检查的 VK 全集。
    ///
    /// ★ 不能只用 `Keys.VkToButtonId`（那是**遥控器按钮**的键，映射目标可以是别的键），
    ///   也不能只列修饰键 —— 实测：只列 "遥控器键 + 修饰键" 时，一个卡住的 F24 完全查不到。
    ///   这里直接给**我们可能注入过的全部键**：字母/数字/F1-F24/导航/编辑/媒体/修饰。
    ///   约 120 个，一次 GetAsyncKeyState 循环是微秒级，启动时做一次无感。
    /// </summary>
    private static IEnumerable<int> StartupCleanVks()
    {
        foreach (var vk in new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5 })
            yield return vk;                                  // 修饰键
        for (var vk = 0x30; vk <= 0x39; vk++) yield return vk; // 0-9
        for (var vk = 0x41; vk <= 0x5A; vk++) yield return vk; // A-Z
        for (var vk = 0x70; vk <= 0x87; vk++) yield return vk; // F1-F24
        foreach (var vk in new[]
                 {
                     0x08, 0x09, 0x0D, 0x1B, 0x20,             // 退格/Tab/回车/Esc/空格
                     0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, // PgUp/PgDn/End/Home/方向
                     0x2C, 0x2D, 0x2E, 0x2F, 0x90, 0x91,       // PrtSc/Ins/Del/Help/NumLock/ScrLk
                     0xA6, 0xA7, 0xA8, 0xA9, 0xAA, 0xAB, 0xAC, 0xAD, // 浏览器键
                     0xAE, 0xAF, 0xB0, 0xB1, 0xB2, 0xB3,       // 音量/媒体
                 })
            yield return vk;
        foreach (var vk in Keys.VkToButtonId.Keys)
            if (vk > 0) yield return vk;                      // 遥控器自身用到的键（兜底）
    }

    /// <summary>
    /// 启动兜底：把上一次实例被强杀时残留在系统里的**按键**补发 KEYUP。
    ///
    /// ★★ 2026-10-27 真机反馈"换台机器还是卡键"：
    ///   原实现只检查 **11 个修饰键**（Alt/Ctrl/Shift/Win），
    ///   而遥控器能映射的普通键（字母 / F 键 / 媒体键 / 方向键）**一个都不在名单里** ——
    ///   上一个实例若在按住这类键时被 taskkill /崩溃，重启后系统仍认为它按着，
    ///   表现为"一直在重复输入 / 菜单自己滚动"，且**重启程序清不掉**（只能重启系统）。
    ///   现在改为：**所有遥控器可映射的 VK + 全部修饰键**。
    ///
    /// 副作用评估：启动瞬间若用户**正用手按着**其中某个键，会被这一次 KEYUP 打断 ——
    ///   与原来修饰键的行为一致，且只发生在启动那一下，可接受。
    ///   需要排查时可用环境变量 `IPRC1000_NO_START_CLEAN=1` 关掉。
    /// </summary>
    public static string? ClearStaleKeysOnStart()
    {
        if (Environment.GetEnvironmentVariable("IPRC1000_NO_START_CLEAN") == "1") return null;

        var stuck = new List<int>();
        foreach (var vk in StartupCleanVks().Distinct())
            if ((GetAsyncKeyState(vk) & 0x8000) != 0) stuck.Add(vk);
        if (stuck.Count == 0) return null;

        foreach (var vk in stuck)
        {
            try { Send(new[] { vk }, true); } catch { }
        }
        var names = string.Join(" ", stuck.Select(v => $"{Keys.VkLabel(v)}(0x{v:X2})"));
        var msg = $"GUARD  启动兜底  系统里残留着按下的键 → 补发 KEYUP  共 {stuck.Count} 个：{names}" +
                  "（多为上一次实例被强杀留下的）";
        // 这条用 Safe.Note 记一份：即使诊断日志关着，也能在 errors.log 里看到
        try { Safe.Note($"GUARD 启动兜底 释放残留按键 {stuck.Count} 个：{names}"); } catch { }
        EtwKeySource.AppendLog(msg);
        GuardLog(msg);
        return names;
    }

    // ── 孤儿键回收器 ──
    private DateTime _lastReapAt = DateTime.MinValue;
    private readonly Dictionary<int, int> _orphanStreak = new();   // vk → 连续观察到"系统说按着"的次数
    private readonly HashSet<int> _orphanLogged = new();

    /// <summary>
    /// 孤儿键回收 —— **以系统实际状态为准，不用我们自己的账**。
    ///
    /// 为什么必须有它：卡键的根因类别是"**我们自己的账不可靠**"。只要 `_heldButtons`
    /// 被写错（历史上真发生过：单槽 `_injectedNow` 被覆盖时把登记表里的记录一起删掉），
    /// 任何基于它的看门狗都必然失灵、救不回来。所以这里改问系统。
    ///
    /// 判据：`GetAsyncKeyState(vk)` 说这个键还按着，而**我们已经不持有任何键** →
    /// 说明我们的虚拟键盘还剩着一份没收回的报告 → 补发全零报告。
    ///
    /// 为什么安全（三条）：
    ///   ① 只在**我们没持有任何键**时才动手 —— 此时下发全零报告不可能碰到合法按住的键；
    ///   ② 只在 `InjectableVks()`（本程序可能注入的键）里找，绝不碰别的键；
    ///   ③ 全零报告只作用于**我们自己的虚拟键盘设备**；系统按键状态是分设备合并的，
    ///      所以物理键盘上按着的键**不受影响**。
    /// 另外要求连续 3 次（约 3 秒）都成立才动手，避免瞬时抖动误判。
    /// 若注入其实走的是 SendInput 路径（`_viaVirtualHid == false`），只告警不动手 ——
    /// 那条路径上没有"虚拟设备残留"这回事，而 SendInput 补发有可能误伤物理按键。
    /// </summary>
    private void ReapOrphanKeys()
    {
        if ((DateTime.UtcNow - _lastReapAt).TotalSeconds < 1.0) return;
        _lastReapAt = DateTime.UtcNow;

        // ① 我们正持有键 → 不介入（此时全零报告会误伤）
        if (ComputeIntended().Length != 0) { _orphanStreak.Clear(); return; }

        var candidates = InjectableVks();
        if (candidates.Length == 0) return;

        var orphans = new List<int>();
        var suspects = new List<int>();
        foreach (var vk in candidates)
        {
            if ((GetAsyncKeyState(vk) & 0x8000) == 0)
            {
                _orphanStreak.Remove(vk);
                _orphanLogged.Remove(vk);
                continue;
            }
            _orphanStreak[vk] = _orphanStreak.TryGetValue(vk, out var n) ? n + 1 : 1;
            if (_orphanStreak[vk] < 3) continue;
            (_viaVirtualHid ? orphans : suspects).Add(vk);
            if (!_orphanLogged.Add(vk)) continue;      // 同一轮只记一次，避免刷屏
        }

        if (orphans.Count > 0)
        {
            var names = string.Join(" ", orphans.Select(v => $"0x{v:X2}"));
            try { WinUhidInjector.Release(); } catch { }   // 全零报告：只作用于我们的虚拟键盘
            lock (_injLock) _injectedSet = Array.Empty<int>();
            var msg = $"GUARD  孤儿键回收  系统仍认为按着、但已不在我们的按住集合里 → 补发全零报告  键={names}";
            EtwKeySource.AppendLog(msg);
            GuardLog(msg);
            LastError = "已回收孤儿键：" + names;
        }
        if (suspects.Count > 0)
        {
            // SendInput 路径下不敢自动补发（可能误伤物理按键），只告警让人知道
            EtwKeySource.AppendLog(
                $"INJ  ★ 疑似孤儿键 {string.Join(" ", suspects.Select(v => $"0x{v:X2}"))}" +
                "（系统报按着、我们不持有）—— 当前走 SendInput，未自动补发，请手动紧急复位");
        }
    }

    public void Dispose()
    {
        try { ReleaseAllInjected(); } catch { }
        Stop();
    }
}