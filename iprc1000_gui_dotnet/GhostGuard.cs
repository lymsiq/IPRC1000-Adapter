using System.Runtime.InteropServices;

namespace Iprc1000Adapter;

/// <summary>
/// **无驱动模式**的幽灵键抑制（2026-10-29）。
///
/// 背景：遥控器是标准蓝牙键盘。按住语音键时，它会以约 31 Hz 连发 `m` ——
/// 装上 WinUHid 并把设备驱动切成「普通 HID 设备」后，系统不再把它当键盘，这个 `m` 就没了。
/// 但有些机器**装不了驱动**（无管理员、杀软拦截、受限镜像）。这个类是给那种情况的降级方案：
///
///   保持键盘 + 用低级键盘钩子**只在按住语音键期间**吞掉 `m`。
///
/// 为什么只在按住期间吞：
///   LL 钩子拿不到"这个键来自哪个设备"，所以不能长期吞 `m`（否则用户真打 m 就打不出来）。
///   而"按住语音键"是明确的、用户可见的状态，这个窗口内吞 `m` 在语义上是安全的
///   （人不可能一边按住语音键一边打 m）。
///
/// 已知代价（不掩饰）：
///   1. **输入法可能不认**：IME 会过滤 SendInput 注入的按键，所以无驱动模式下
///      "按住语音键触发输入法"这条链路**可能不生效**；按键映射到普通程序仍然可用。
///   2. 钩子本身可被反作弊检测 —— 所以本模式**默认关闭**，且提示"仅建议无联机游戏的机器"。
///   3. ETW 与 HID 之间有几十毫秒的先后差，第一两个 `m` 可能漏进去（见 Swallowed / 面板可观测）。
/// </summary>
internal static class GhostGuard
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int VK_M = 0x4D;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);
    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int ptX; public int ptY; }

    /// <summary>总开关：只有"无驱动模式"才置 true。</summary>
    public static volatile bool Enabled;

    private static volatile bool _armed;
    private static long _armedAt;

    // ── 连发特征判定（2026-10-29 补）────────────────────────────────
    //   ★ 用户实测的真相：**语音键在 HID 层就是 `m`(0x10)**，所以"按下语音键"和
    //     "第一个幽灵 m"是**同一个事件**。靠 ETW 收到 voice 再武装，永远慢一拍，
    //     而且一旦 ETW 没通（面板"0 个事件"那次），就完全武装不了 ⇒ m 一串全放行。
    //   兜底：**靠 m 自己的连发特征**判断 —— 遥控器按住时以约 31Hz（≈32ms）连发；
    //     人手打字两下 m 通常 ≥100ms。所以"两次 m 间隔 < 80ms"基本只能是自动重复。
    //   命中后进入"连发抑制"，200ms 内没有新的 m 才解除。
    private const int BurstGapMs = 80;      // 两次 m 间隔小于它 → 认为是连发
    private const int BurstHoldMs = 200;    // 抑制状态保持多久（无新 m）
    private static long _lastMDownAt;
    private static volatile bool _burst;
    private static long _burstAt;           // 进入连发抑制的时刻（硬性兜底用）
    private static volatile int _graceGen;

    private static IntPtr _hook;
    private static Thread? _thread;
    private static HookProc? _proc;   // 必须保住引用，否则会被 GC 回收 → 钩子崩

    /// <summary>被吞掉的 `m` 次数（面板可观测；0 且按住语音键有 m 漏出时说明窗口没抢到）。</summary>
    public static long Swallowed;

    public static bool Running => _hook != IntPtr.Zero;

    public static void Start()
    {
        if (_hook != IntPtr.Zero) return;
        _thread = new Thread(() =>
        {
            _proc = HookCallback;
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero)
            {
                Safe.Note($"GHOST 钩子安装失败（错误 {Marshal.GetLastWin32Error()}）");
                return;
            }
            Safe.Note("GHOST 幽灵键抑制已启用（仅按住语音键期间吞 m）");
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        })
        { IsBackground = true, Name = "ghost-guard" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        // ★ 2026-10-29：`SetWindowsHookEx` 在新线程里执行，这里必须**等它装完**再返回，
        //   否则调用方立刻查 `Running` 会拿到 false（自检就会误报"安装失败"）。
        for (var i = 0; i < 100 && _hook == IntPtr.Zero; i++) Thread.Sleep(10);
    }

    public static void Stop()
    {
        Enabled = false;
        _armed = false;
        _burst = false;
        _lastMDownAt = 0;
        if (_hook != IntPtr.Zero) { try { UnhookWindowsHookEx(_hook); } catch { } _hook = IntPtr.Zero; }
    }

    /// <summary>
    /// 主动补一个 `m` 的 KEYUP —— 用来打断"键一直被按住"的自动重复。
    /// 只在判定为连发（或语音键松开）时调用，不会影响用户正常打字。
    /// </summary>
    private static void ReleaseGhostKey()
    {
        try
        {
            keybd_event(VK_M, 0, 2 /*KEYEVENTF_KEYUP*/, UIntPtr.Zero);
            ForcedReleases++;
        }
        catch { }
    }

    /// <summary>已主动补发的 keyup 次数（面板可观测）</summary>
    public static long ForcedReleases;

    /// <summary>进入连发抑制的**次数**（= 遥控器按住过几次）。比"吞了多少下"更有意义：
    /// 按住 1 秒就会吞 31 下，面板显示"已吞 2640 次"会让用户以为出大问题。</summary>
    public static long Bursts;

    /// <summary>最近一次拦截的时刻（TickCount64）。拔掉遥控器/不按键时这个值**不再变化**，
    /// 界面用它回答"现在还发不发 m"，比累计数字有用得多。</summary>
    public static long LastSwallowAt;

    /// <summary>清零统计（切换模式时调用，避免累计数字被误读成"还在发"）</summary>
    public static void ResetStats() { Swallowed = 0; Bursts = 0; ForcedReleases = 0; LastSwallowAt = 0; }

    /// <summary>语音键按下 → 开始吞 m。</summary>
    public static void Arm(long nowMs)
    {
        if (!Enabled) return;
        _armedAt = nowMs;
        _armed = true;
        _graceGen++;      // 取消任何待执行的 Disarm
    }

    /// <summary>语音键松开 → 宽限一小段时间后停止（尾巴上的重复仍会来）。</summary>
    public static void Disarm()
    {
        if (!Enabled) return;
        // 语音键松开：若刚才在连发状态，补一次 keyup 确保键状态干净（不残留"按住"）
        if (_burst) { ReleaseGhostKey(); _burst = false; _lastMDownAt = 0; }
        var gen = ++_graceGen;
        Task.Delay(400).ContinueWith(_ =>
        {
            if (gen == _graceGen) _armed = false;   // 期间又按下过就不 disarm
        });
    }

    /// <summary>
    /// **自检**（不动设备驱动）：只启钩子 + 武装 3 秒，期间由调用方注入若干 `m`，
    /// 返回被吞掉的数量。用途：验证钩子本身能不能拦住 `m`（`--ghost-test`）。
    /// </summary>
    public static int SelfTest()
    {
        Enabled = true;
        Start();
        if (!Running) return -1;
        var before = Swallowed;
        _lastMDownAt = 0; _burst = false;   // 只靠"连发特征"判定，验证兜底路径
        // 连发 6 次（间隔 ~40ms，模拟遥控器 31Hz）；前 1 个应放行、其余应被吞
        // 连发 6 次、间隔 ~40ms（模拟遥控器 31Hz）。预期：第 1 个放行、后 5 个被吞。
        const int n = 6;
        for (var i = 0; i < n; i++)
        {
            keybd_event(0x4D, 0, 0, UIntPtr.Zero);
            keybd_event(0x4D, 0, 2 /*KEYEVENTF_KEYUP*/, UIntPtr.Zero);
            Thread.Sleep(40);
        }
        Thread.Sleep(200);
        _armed = false;
        Stop();
        return (int)(Swallowed - before);
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && Enabled)
            {
                // 安全阀：万一"松开"事件丢了（遥控器断连），15 秒后自动解除
                if (_armed && Environment.TickCount64 - _armedAt > 15000) _armed = false;

                var msg = wParam.ToInt32();
                if (msg is WM_KEYDOWN or WM_SYSKEYDOWN)
                {
                    var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                    if (info.vkCode == VK_M)
                    {
                        var now = Environment.TickCount64;

                        // ★★★ 顺序很重要（2026-10-29 真 bug，用户键盘 m 打不出来）：
                        //     必须先按"距上次的间隔"判过期，**再**更新 _lastMDownAt。
                        //     原来写成先更新再比较，差值恒为 0 ⇒ _burst 永远清不掉 ⇒
                        //     一整串吞到 1900 次，连物理键盘的 m 都被吃掉。
                        if (_burst && now - _lastMDownAt > BurstHoldMs) _burst = false;
                        // 硬性兜底：抑制最多持续 3 秒（防止任何时间戳异常把它钉死）
                        if (_burst && now - _burstAt > 3000) _burst = false;

                        // 连发特征：距上一次 m 按下 < 80ms ⇒ 是自动重复
                        if (_lastMDownAt != 0 && now - _lastMDownAt < BurstGapMs && !_burst)
                        {
                            _burst = true;
                            _burstAt = now;
                            Bursts++;
                            // ★★ 2026-10-29（用户提议，确实更彻底）：
                            //   光"吞 keydown"是拦不住自动重复的 —— 系统认为这个键**一直按着**，
                            //   于是按 31Hz 持续产生新的 keydown。**只有把键松开**才能终止重复。
                            //   所以这里主动补一个 m 的 KEYUP：打断重复链，之后不再刷屏。
                            ReleaseGhostKey();
                        }
                        _lastMDownAt = now;

                        if (_armed || _burst)
                        {
                            Swallowed++;
                            LastSwallowAt = now;
                            return (IntPtr)1;      // 吞掉：不传给后续（包括输入法）
                        }
                    }
                }
            }
        }
        catch { }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }
}
