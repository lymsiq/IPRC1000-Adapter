using System.Runtime.InteropServices;

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
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    private const uint LLKHF_INJECTED_MASK = 0x10 | 0x02;

    // ── Raw Input ──
    private const int WM_INPUT = 0x00FF;
    private const uint RID_INPUT = 0x10000003, RIDI_DEVICENAME = 0x20000007;
    private const uint RIM_TYPEKEYBOARD = 1;
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const ushort RI_KEY_BREAK = 0x01;

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE { public ushort usUsagePage, usUsage; public uint dwFlags; public IntPtr hwndTarget; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER { public uint dwType, dwSize; public IntPtr hDevice, wParam; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWKEYBOARD { public ushort MakeCode, Flags, Reserved, VKey, Message; public uint ExtraInformation; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUT { public RAWINPUTHEADER header; public RAWKEYBOARD keyboard; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);
    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetRawInputDeviceInfoW(IntPtr hDevice, uint uiCommand, IntPtr pData, ref uint pcbSize);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref MSG lpMsg);
    [DllImport("user32.dll")] private static extern bool PostThreadMessageW(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int pt_x, pt_y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public KEYBDINPUT ki;
    }

    private const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint WM_QUIT = 0x0012;

    // ─────────────────────────────────────────

    private readonly HashSet<ushort> _held = new();
    private readonly object _heldLock = new();
    private readonly Dictionary<ulong, bool> _deviceCache = new();
    private IntPtr _hook = IntPtr.Zero;
    private HookProc? _proc;                 // 必须保活
    private Thread? _thread;
    private uint _threadId;
    private volatile bool _running;
    private IntPtr _hwnd;

    private long _swallowed, _passed;
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
    private const ushort SpuriousVk = 0x49;

    /// <summary>
    /// 幽灵键位的"武装窗口"时长。实测幽灵报告只比真实按键**晚 1~2 ms**，
    /// 所以窗口只要够覆盖这个间隔即可 —— 取 **40 ms**：
    ///   · 覆盖 2 ms 有 20 倍余量
    ///   · 误伤窗口极小（只有在"刚敲过遥控器用到的某个字母键"后 40 ms 内又敲 `i` 才会被吞，
    ///     而人手连续两键间隔通常 80~200 ms，几乎不会踩到）
    /// </summary>
    private const int SpuriousArmMs = 40;
    private DateTime _spuriousUntil = DateTime.MinValue;

    /// <summary>遥控器按键用到的 VK 集合（用于"这一下是不是遥控器的键"判断）</summary>
    private static readonly HashSet<ushort> RemoteVks =
        new(Keys.VkToButtonId.Keys.Select(v => (ushort)v));

    /// <summary>被放行出去的幽灵键次数（应当恒为 0；不为 0 就说明还在漏 i）</summary>
    public long SpuriousLeaked { get; private set; }

    /// <summary>钩子级诊断日志（写进同一个 key-events.log，前缀 HOOK ）</summary>
    private int _hookLogBudget = 1500;
    private readonly HashSet<ushort> _loggedSwallow = new();

    private void HookLog(string s)
    {
        if (_hookLogBudget-- <= 0) return;
        EtwKeySource.AppendLog("HOOK " + s);
    }

    /// <summary>供界面清空诊断日志</summary>
    public static void ResetHookLogBudget() { }
    private int[] _injectedNow = Array.Empty<int>();                // 当前已按下、尚未释放的注入键

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

    /// <summary>是否注入映射快捷键</summary>
    public bool InjectEnabled { get; set; } = true;

    // ── 「按一下绑定」：捕获下一次真实按键 ──
    /// <summary>是否正在等待捕获下一次按键</summary>
    public bool CaptureNext { get; set; }
    /// <summary>捕获到的 VK（0 = 还没有）</summary>
    public int CapturedVk { get; private set; }
    /// <summary>捕获成功时触发</summary>
    public event Action<int>? OnCaptured;
    /// <summary>遥控器按键按下/抬起回调 (buttonId, pressed)</summary>
    public event Action<string, bool>? OnButton;
    /// <summary>任意按键回调（用于「当前操作」显示）</summary>
    public event Action<ushort>? OnAnyKey;

    /// <summary>
    /// **语音键按下**时的回调（在注入按键之前触发）—— 用来预热音频链路。
    ///
    /// 开 CABLE Input 要 100~300 ms，而音频在按键后 100~200 ms 就到了；
    /// 不预热的话这段音频会被丢掉，用户看到的就是"按住后立刻说的第一个字识别不出来"。
    /// </summary>
    public static event Action? VoiceKeyDown;
    /// <summary>最近一次失败原因（Raw Input 注册 / 装钩子）</summary>
    public string? LastError { get; private set; }

    public long Swallowed => Interlocked.Read(ref _swallowed);
    public long Passed => Interlocked.Read(ref _passed);
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

        if (KeySourceIsEtw)
        {
            LastCleared = "按键来源＝ETW（不装钩子、不吞键）";
            return;   // ETW 模式：什么都不装
        }

        // Raw Input：只注册键盘（usage page 1 / usage 6），INPUTSINK 让后台窗口也能收到
        var rid = new[]
        {
            new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x06, dwFlags = RIDEV_INPUTSINK, hwndTarget = hwnd },
        };
        if (!RegisterRawInputDevices(rid, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
            LastError = $"RegisterRawInputDevices 失败，err={Marshal.GetLastWin32Error()}";

        _proc = HookCallback;
        _thread = new Thread(HookThread) { IsBackground = true, Name = "iprc1000-swallow" };
        _thread.Start();

        _watchdog = new System.Threading.Timer(_ => Watchdog(), null, 300, 300);
    }

    /// <summary>长时间没有遥控器上报 → 清空按住集合并释放可能卡住的注入键</summary>
    private void Watchdog()
    {
        try
        {
            bool empty;
            lock (_heldLock) empty = _held.Count == 0;
            if (empty) return;
            if ((DateTime.UtcNow - _lastRawInputAt).TotalMilliseconds < HoldTimeoutMs) return;
            var n = ClearHeld("遥控器静默超时（可能已休眠/断连，最后的 UP 丢了）");
            LastError = $"已自动释放 {n} 个卡住的按键（遥控器静默超时）";
        }
        catch { }
    }

    /// <summary>清空「按住」集合并释放所有已注入未释放的键。返回清掉的键数。</summary>
    public int ClearHeld(string reason = "手动清空")
    {
        ushort[] cleared;
        lock (_heldLock)
        {
            cleared = _held.ToArray();
            _held.Clear();
        }
        var n = cleared.Length;
        lock (_lastState) _lastState.Clear();

        // ★ 给系统补发这些键的 KEYUP。
        //   遥控器丢 UP 时，Windows 会一直认为该键「按住」（表现为真实键盘对应字母打不出来、
        //   修饰键卡住会让整个键盘行为异常）。补一次 UP 就能把系统侧状态清干净。
        foreach (var vk in cleared)
        {
            try { Send(new[] { (int)vk }, true); } catch { }
        }
        // 释放可能卡住的注入键（否则修饰键卡住会让整个键盘行为异常）
        if (_injectedNow.Length > 0)
        {
            if (_viaVirtualHid) WinUhidInjector.Release();
            else Send(_injectedNow.OrderByDescending(v => IsModifier(v) ? 0 : 1), true);
            _injectedNow = Array.Empty<int>();
            _viaVirtualHid = false;
        }
        if (n > 0) LastCleared = $"{reason}（释放 {n} 个）";
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
        if (hwnd != IntPtr.Zero) Start(hwnd);
    }

    public void Stop()
    {
        _running = false;
        _watchdog?.Dispose();
        _watchdog = null;
        ClearHeld("程序停止");              // 关键：退出前一定要把注入的键放掉，避免修饰键卡死
        if (_threadId != 0) PostThreadMessageW(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        try { _thread?.Join(1000); } catch { }
    }

    private void HookThread()
    {
        _threadId = GetCurrentThreadId();
        _hook = SetWindowsHookExW(WH_KEYBOARD_LL, _proc!, GetModuleHandleW(null), 0);
        if (_hook == IntPtr.Zero)
        {
            LastError = $"SetWindowsHookExW 失败，err={Marshal.GetLastWin32Error()}";
            _running = false;
            return;
        }
        while (_running && GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
        if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
    }

    // ── 钩子：吞掉遥控器当前按住的键（含系统自动重复） ──

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode == 0)
        {
            var m = (uint)wParam.ToInt64();
            if (m is WM_KEYDOWN or WM_KEYUP or WM_SYSKEYDOWN or WM_SYSKEYUP)
            {
                var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                var injected = (kb.flags & LLKHF_INJECTED_MASK) != 0;
                if (!injected)
                {
                    var vk = (ushort)kb.vkCode;
                    var isDown = m is WM_KEYDOWN or WM_SYSKEYDOWN;
                    bool held;
                    lock (_heldLock)
                    {
                        held = _held.Contains(vk)
                               // 幽灵键位：遥控器刚有动作的一小段窗口内也吞掉
                               || (vk == SpuriousVk && DateTime.UtcNow < _spuriousUntil);

                        // ★★ 关键：**在钩子里就武装幽灵窗口**。
                        //    不能只靠 Raw Input —— WM_INPUT 是队列消息，UI 线程一忙就会晚几十毫秒，
                        //    而幽灵报告只晚 1~2 ms，等 Raw Input 武装完 i 早漏出去了（实测踩到）。
                        //    这里只要看到"遥控器用到的键位"就立刻武装，与 Raw Input 时序无关。
                        if (isDown && RemoteVks.Contains(vk))
                            _spuriousUntil = DateTime.UtcNow.AddMilliseconds(SpuriousArmMs);
                    }
                    // ★★★ 只吞「按下」，**抬起事件一律放行** ★★★
                    //
                    // 这是「一直输出 m」的真正根因：遥控器的 UP 若被吞掉，Windows 就永远
                    // 收不到那次释放，会把该键锁在「按下」状态，之后键盘类驱动按约 31 Hz
                    // 持续合成重复 —— 于是就算遥控器早已松手，m 仍会不停往外冒。
                    // （此前我们把 UP 也吞了，导致这个 bug；DOWN 才需要吞，UP 放行反而
                    //   正好把系统状态清干净，且单独的 UP 不会产生任何字符。）
                    if (held && isDown)
                    {
                        Interlocked.Increment(ref _swallowed);
                        // 只记"意外"的：遥控器键集之外的 VK 被吞掉
                        if (!RemoteVks.Contains(vk))
                        {
                            lock (_loggedSwallow)
                            {
                                if (_loggedSwallow.Add(vk))
                                    HookLog($"SWALLOW vk=0x{vk:X2} scan=0x{kb.scanCode:X2} (不在遥控器键集内)");
                            }
                        }
                        return 1;
                    }
                    // 放行：**记录 vk/scan/是否遥控器键/武装剩余** —— 漏出去的到底是哪个键，一眼可见
                    HookLog($"PASS vk=0x{vk:X2} scan=0x{kb.scanCode:X2} down={isDown} " +
                            $"远程键={RemoteVks.Contains(vk)} 幽灵武装剩={(int)(_spuriousUntil - DateTime.UtcNow).TotalMilliseconds}ms " +
                            $"held={_held.Count}");
                    if (vk == SpuriousVk) SpuriousLeaked++;

                    // 「按一下绑定」：捕获这次物理按键（并吞掉，避免打出一个字符）
                    if (CaptureNext && isDown && vk != 0)
                    {
                        CaptureNext = false;
                        CapturedVk = vk;
                        Interlocked.Increment(ref _swallowed);
                        try { OnCaptured?.Invoke(vk); } catch { }
                        return 1;
                    }

                    Interlocked.Increment(ref _passed);
                }
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    // ── Raw Input：识别设备 + 维护 held 集合 + 触发映射 ──

    public void HandleRawInput(IntPtr lParam)
    {
        uint size = 0;
        GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref size, (uint)Marshal.SizeOf<RAWINPUTHEADER>());
        if (size == 0 || size > 1024) return;
        var buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(lParam, RID_INPUT, buf, ref size, (uint)Marshal.SizeOf<RAWINPUTHEADER>()) == uint.MaxValue) return;
            var raw = Marshal.PtrToStructure<RAWINPUT>(buf);
            if (raw.header.dwType != RIM_TYPEKEYBOARD) return;

            // 设备过滤：device_handle → 接口路径（结果缓存）
            if (!_deviceCache.TryGetValue((ulong)raw.header.hDevice, out var ours))
            {
                ours = DeviceName(raw.header.hDevice) is { } n && IsIprc1000(n);
                _deviceCache[(ulong)raw.header.hDevice] = ours;
            }
            if (!ours) return;
            DeviceSeen = true;
            _lastRawInputAt = DateTime.UtcNow;

            var vk = raw.keyboard.VKey;
            if (vk is 0 or 0xFF) return;
            var pressed = (raw.keyboard.Flags & RI_KEY_BREAK) == 0;

            lock (_heldLock)
            {
                if (pressed) _held.Add(vk); else _held.Remove(vk);
                // 遥控器有动作 → 顺手武装幽灵键位（0x0C → VK 0x49 'i'）
                _spuriousUntil = DateTime.UtcNow.AddMilliseconds(SpuriousArmMs);
            }
            OnAnyKey?.Invoke(vk);

            if (!Keys.VkToButtonId.TryGetValue(vk, out var bid)) return;

            // 去重：系统自动重复会一直产生 DOWN，只处理第一次
            lock (_lastState)
            {
                var changed = !_lastState.TryGetValue(vk, out var prev) || prev != pressed;
                _lastState[vk] = pressed;
                if (!changed) return;
            }

            ApplyButton(bid, pressed);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static string? DeviceName(IntPtr hDevice)
    {
        uint size = 0;
        GetRawInputDeviceInfoW(hDevice, RIDI_DEVICENAME, IntPtr.Zero, ref size);
        if (size == 0) return null;
        var buf = Marshal.AllocHGlobal((int)size * 2);
        try
        {
            if (GetRawInputDeviceInfoW(hDevice, RIDI_DEVICENAME, buf, ref size) == uint.MaxValue) return null;
            return Marshal.PtrToStringUni(buf);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static bool IsIprc1000(string path)
    {
        var p = path.ToLowerInvariant();
        return p.Contains("0a5c") && p.Contains("8502");
    }

    /// <summary>
    /// 处理一次按键事件（**Raw Input 与 ETW 两条来源共用**）：通知订阅者 + 按映射注入快捷键。
    /// </summary>
    public void ApplyButton(string bid, bool pressed, string src = "rawinput")
    {        try { OnButton?.Invoke(bid, pressed); } catch { }

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
        if (!InjectEnabled) return;

        EtwKeySource.AppendLog($"APPLY  {bid}  {(pressed ? "DOWN" : "UP")}  来源={src}" +
            $"  映射={Keys.Format(vks)}  组={Keymaps.Current.Name}");

        if (pressed)
        {
            // ★ 语音键按下 → 先预热音频链路（把 CABLE Input 提前打开），再注入按键。
            //   顺序很重要：开声卡要 100~300 ms，而音频随后就到 —— 要先开声卡再注入，
            //   否则那段时间的音频会被丢掉，表现为"按住后立刻说的第一个字识别不出来"。
            if (bid == "voice") { try { VoiceKeyDown?.Invoke(); } catch { } }
            InjectPress(vks);
            _injectedNow = vks;
        }
        else
        {
            // ★ 释放**按下时记录的那组键**，而不是重新读一遍配置 ——
            //   否则按住期间切了配置组/改了映射，就会放掉错的键，把原键卡在按下状态。
            var rel = _injectedNow is { Length: > 0 } ? _injectedNow : vks;
            InjectRelease(rel);
            _injectedNow = Array.Empty<int>();
        }
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
    public static void ReleaseAllInjected()
    {
        foreach (var vk in Keys.VkToButtonId.Keys)
        {
            try { InjectRelease(new[] { (int)vk }); } catch { }
        }
        // 额外把几个修饰键也放一遍（注入可能只按下了修饰键）
        foreach (var vk in new[] { 0xA5, 0xA4, 0x12, 0xA3, 0xA2, 0x11, 0xA1, 0xA0, 0x10, 0x5B, 0x5C })
        {
            try { InjectRelease(new[] { vk }); } catch { }
        }
    }

    public void Dispose()
    {
        try { ReleaseAllInjected(); } catch { }
        Stop();
        _proc = null;
    }
}
