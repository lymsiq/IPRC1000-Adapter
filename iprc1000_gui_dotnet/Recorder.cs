using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Iprc1000Adapter;

/// <summary>
/// **录制器**：把用户"做一遍"的过程录成宏步骤（键盘 + 鼠标点击 / 移动 / 拖拽）。
///
/// 三条设计要点：
///
/// ① **只装钩子，不做注入** —— 录制期间完全不碰 <see cref="KeyFilter"/>。
///    钩子回调里靠 `LLKHF_INJECTED` / `LLMHF_INJECTED` 把我们自己注入的事件滤掉，
///    否则"播放宏的时候顺手按了录制"会把宏自己发的键又录一遍（自激）。
///
/// ② **鼠标轨迹不录** —— 只在"按住期间"记录移动（为了还原拖拽）。自由移动录下来
///    既没有回放价值，又会让步骤数量爆炸（KeymouseGo #48 的用户就是抱怨这个）。
///
/// ③ **事件 → 步骤是纯函数**（<see cref="BuildSteps"/>），不依赖钩子状态。
///    这样 `--macro-test` 能直接喂人造事件断言转换质量，不用真的按键。
///
/// ⚠ 坐标是**屏幕物理像素 + 录制时的屏幕尺寸**，播放时按比例缩放。
///   跨分辨率/跨 DPI 缩放仍可能偏（这是所有坐标方案的固有缺陷），
///   所以「点击」步骤优先用图标匹配，坐标只是用户明确选择时才用。
/// </summary>
internal static class Recorder
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;

    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205;
    private const int WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208;

    private const uint LLKHF_INJECTED = 0x10;
    private const uint LLMHF_INJECTED = 0x01;

    /// <summary>拖拽判定：移动超过这么多像素才算拖拽（否则算点击。手抖 3~5px 很常见）</summary>
    private const int DragThresholdPx = 8;
    /// <summary>按下超过这么久才算拖拽（长按不放也算）</summary>
    private const int DragThresholdMs = 600;
    /// <summary>两个步骤之间小于这个间隔就不插 wait（避免一堆 10ms 的碎等待）</summary>
    private const int MinWaitMs = 70;
    /// <summary>单个 wait 上限（防止录进一个"离开 5 分钟"）</summary>
    private const int MaxWaitMs = 10000;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT { public int ptX, ptY; public uint mouseData, flags, time; public IntPtr dwExtraInfo; }

    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int ptX, ptY; }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? lpModuleName);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    private const uint WM_QUIT_ = 0x0012;

    // ── 录制事件（给 BuildSteps 的输入，pure data）────────────────────────

    /// <summary>一条原始事件。`Kind`：kd/ku（键按下/抬起）、md/mu（鼠标按下/抬起）、mm（鼠标移动）</summary>
    public struct RawEv
    {
        public string Kind;
        public int Vk;
        public int X, Y;
        public long T;        // 相对录制开始的毫秒
        public int Button;    // 0=左 1=右 2=中
        public string Win;    // 鼠标按下时的前台窗口标题
    }

    private static readonly object Gate = new();
    private static readonly List<RawEv> _evs = new();
    private static HookProc? _kbProc, _msProc;
    private static IntPtr _kbHook, _msHook;
    private static Thread? _thread;
    private static uint _threadId;
    private static Stopwatch? _sw;

    public static bool IsRecording { get; private set; }
    public static int EventCount { get { lock (Gate) return _evs.Count; } }
    public static long ElapsedMs { get { try { return IsRecording ? (_sw?.ElapsedMilliseconds ?? 0) : 0; } catch { return 0; } } }

    // ── 开始 / 停止 ────────────────────────────────────────────────────────

    public static string Start()
    {
        if (IsRecording) return "已经在录制了";
        if (MacroPlayer.IsRunning) return "宏正在播放，先等它结束（或者按任意遥控器键停掉）";

        lock (Gate) _evs.Clear();
        _sw = Stopwatch.StartNew();
        IsRecording = true;

        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            _kbProc = KeyboardProc;
            _msProc = MouseProc;
            var mod = GetModuleHandle(null);
            _kbHook = SetWindowsHookEx(WH_KEYBOARD_LL, _kbProc, mod, 0);
            _msHook = SetWindowsHookEx(WH_MOUSE_LL, _msProc, mod, 0);

            if (_kbHook == IntPtr.Zero && _msHook == IntPtr.Zero)
            {
                Safe.Note($"REC 钩子都装不上（错误 {Marshal.GetLastWin32Error()}）");
                IsRecording = false;
                return;
            }
            Safe.Note($"REC 录制开始（键盘钩子={( _kbHook != IntPtr.Zero ? "OK" : "失败")}，鼠标钩子={(_msHook != IntPtr.Zero ? "OK" : "失败")}）");

            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        })
        { IsBackground = true, Name = "recorder" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        // 等钩子装完再返回 —— 否则用户"点完就开始操作"，头几个事件会丢
        for (var i = 0; i < 150 && _kbHook == IntPtr.Zero && _msHook == IntPtr.Zero && IsRecording; i++)
            Thread.Sleep(10);

        if (!IsRecording) return "录制启动失败（钩子装不上，可能有安全软件拦截）";
        return "已开始录制，做一遍你的操作，做完点「停止录制」";
    }

    public static string Stop()
    {
        if (!IsRecording) return "当前没有在录制";
        IsRecording = false;

        try { if (_kbHook != IntPtr.Zero) UnhookWindowsHookEx(_kbHook); } catch { }
        try { if (_msHook != IntPtr.Zero) UnhookWindowsHookEx(_msHook); } catch { }
        _kbHook = _msHook = IntPtr.Zero;

        // 让消息泵退出（不然那个线程会一直挂着）
        try { if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT_, IntPtr.Zero, IntPtr.Zero); } catch { }
        try { _thread?.Join(400); } catch { }
        _thread = null;

        var n = EventCount;
        Safe.Note($"REC 录制结束，共 {n} 个事件");
        return n == 0 ? "没录到任何操作（钩子可能被拦截，或你什么都没做）" : $"已录到 {n} 个事件";
    }

    public static void Cancel()
    {
        if (IsRecording) Stop();
        lock (Gate) _evs.Clear();
    }

    /// <summary>录到的事件数 + 大致能转成多少步（界面显示用）</summary>
    public static string Summary()
    {
        List<RawEv> copy;
        lock (Gate) copy = _evs.ToList();
        var (_, _, w, h) = MouseInjector.VirtualScreen();
        var steps = BuildSteps(copy, w, h);
        return $"事件 {copy.Count} 个 → 步骤 {steps.Count} 步";
    }

    /// <summary>把录到的事件转成步骤（调用方负责存进宏）。空录制返回空列表</summary>
    public static List<Macros.Step> TakeSteps(out string summary)
    {
        List<RawEv> copy;
        lock (Gate) copy = _evs.ToList();
        var (_, _, w, h) = MouseInjector.VirtualScreen();
        var steps = BuildSteps(copy, w, h);

        var keys = steps.Count(s => s.Type == Macros.StepKeys);
        var clicks = steps.Count(s => s.Type == Macros.StepClick);
        var drags = steps.Count(s => s.Type == Macros.StepDrag);
        var moves = steps.Count(s => s.Type == Macros.StepMove);
        var waits = steps.Count(s => s.Type == Macros.StepWait);
        summary = $"录制完成：{steps.Count} 步（按键 {keys} · 点击 {clicks}"
                + (drags > 0 ? $" · 拖拽 {drags}" : "")
                + (moves > 0 ? $" · 移动 {moves}" : "")
                + $" · 等待 {waits}）";
        return steps;
    }

    // ── 钩子回调 ──────────────────────────────────────────────────────────

    private static IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && IsRecording)
        {
            try
            {
                var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                // ★ 必须滤掉注入事件：否则播放宏的时候自己发的键会被录进来（自激）
                if ((info.flags & LLKHF_INJECTED) == 0)
                {
                    var msg = (int)wParam;
                    if (msg is WM_KEYDOWN or WM_SYSKEYDOWN or WM_KEYUP or WM_SYSKEYUP)
                    {
                        Add(new RawEv
                        {
                            Kind = (msg is WM_KEYDOWN or WM_SYSKEYDOWN) ? "kd" : "ku",
                            Vk = (int)info.vkCode,
                            T = ElapsedMs,
                        });
                    }
                }
            }
            catch { }
        }
        return CallNextHookEx(_kbHook, nCode, wParam, lParam);
    }

    private static IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && IsRecording)
        {
            try
            {
                var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                if ((info.flags & LLMHF_INJECTED) == 0)
                {
                    var msg = (int)wParam;
                    var t = ElapsedMs;
                    switch (msg)
                    {
                        case WM_MOUSEMOVE:
                            // ★ 自由移动一律不记：数据量最大、回放价值最小（KeymouseGo #48 的抱怨）。
                            //   只有"按住期间"的移动才有意义 —— 那是拖拽。
                            if (_recMouseDown) Add(new RawEv { Kind = "mm", X = info.ptX, Y = info.ptY, T = t });
                            break;
                        case WM_LBUTTONDOWN:
                        case WM_RBUTTONDOWN:
                        case WM_MBUTTONDOWN:
                            _recMouseDown = true;
                            Add(new RawEv
                            {
                                Kind = "md", X = info.ptX, Y = info.ptY, T = t,
                                Button = msg == WM_LBUTTONDOWN ? 0 : msg == WM_RBUTTONDOWN ? 1 : 2,
                                Win = ForegroundTitle(),
                            });
                            break;
                        case WM_LBUTTONUP:
                        case WM_RBUTTONUP:
                        case WM_MBUTTONUP:
                            _recMouseDown = false;
                            Add(new RawEv
                            {
                                Kind = "mu", X = info.ptX, Y = info.ptY, T = t,
                                Button = msg == WM_LBUTTONUP ? 0 : msg == WM_RBUTTONUP ? 1 : 2,
                            });
                            break;
                    }
                }
            }
            catch { }
        }
        return CallNextHookEx(_msHook, nCode, wParam, lParam);
    }

    private static void Add(RawEv e)
    {
        lock (Gate) _evs.Add(e);
    }

    /// <summary>鼠标当前是否处于按下状态 —— 只有按住期间才记移动（为了还原拖拽）</summary>
    private static volatile bool _recMouseDown;

    /// <summary>当前前台窗口标题（录制时记下来，播放前用来校验"还是那个窗口吗"）</summary>
    public static string ForegroundTitle()
    {
        try
        {
            var h = GetForegroundWindow();
            if (h == IntPtr.Zero) return "";
            var sb = new StringBuilder(256);
            var n = GetWindowText(h, sb, sb.Capacity);
            return n > 0 ? sb.ToString(0, Math.Min(n, 80)) : "";
        }
        catch { return ""; }
    }

    // ── 事件 → 步骤（纯函数，可自检）──────────────────────────────────────

    private static readonly HashSet<int> Mods = new()
    {
        0x11, 0xA2, 0xA3,      // Ctrl
        0x10, 0xA0, 0xA1,      // Shift
        0x12, 0xA4, 0xA5,      // Alt（含右 Alt）
        0x5B, 0x5C,            // Win
    };

    /// <summary>修饰键归一：左右 Ctrl/Shift 在回放时没必要区分（注入的是通用修饰键也行）</summary>
    private static int NormMod(int vk) => vk switch
    {
        0xA2 or 0xA3 => 0x11,
        0xA0 or 0xA1 => 0x10,
        0xA4 or 0xA5 => 0x12,
        0x5C => 0x5B,
        _ => vk,
    };

    /// <summary>
    /// 把原始事件翻译成宏步骤。**纯函数**：不读任何全局状态，便于自检。
    ///
    /// 规则：
    ///   · 修饰键按下 → 记进组合；普通键按下→抬起 → 一个 keys 步骤（组合键 + 按住时长）
    ///   · 鼠标按下→抬起：位移 ≤8px 且 ≤600ms ⇒ click；否则 ⇒ drag（起点→终点）
    ///   · 步骤之间间隔 ≥70ms 插 wait（上限 10s，用户离开一会儿不该录成 5 分钟等待）
    /// </summary>
    public static List<Macros.Step> BuildSteps(List<RawEv> events, int screenW, int screenH)
    {
        var outp = new List<Macros.Step>();
        if (events is null || events.Count == 0) return outp;

        var mods = new HashSet<int>();
        var pendingKey = -1;
        long pendingAt = 0;
        var pendingWin = "";

        var mDown = false;
        long mDownAt = 0;
        int mDownX = 0, mDownY = 0, mBtn = 0;
        var mWin = "";
        var maxDist = 0;
        var lastX = 0; var lastY = 0;

        // 中间结果：at = 开始时刻、dur = 持续时长。最后用它算 wait。
        var timed = new List<(Macros.Step s, long at, long dur)>();

        foreach (var e in events)
        {
            switch (e.Kind)
            {
                case "kd":
                    if (Mods.Contains(e.Vk))
                    {
                        mods.Add(NormMod(e.Vk));
                        if (pendingKey < 0) { pendingAt = e.T; pendingWin = e.Win ?? ""; }
                    }
                    else if (pendingKey < 0)     // 按住不放会有重复 DOWN，只认第一次
                    {
                        pendingKey = e.Vk;
                        pendingAt = e.T;
                        pendingWin = e.Win ?? "";
                    }
                    break;

                case "ku":
                    if (Mods.Contains(e.Vk))
                    {
                        mods.Remove(NormMod(e.Vk));
                    }
                    else if (pendingKey >= 0 && (e.Vk == pendingKey || mods.Count > 0))
                    {
                        var combo = mods.Append(pendingKey).Distinct().OrderBy(v => Mods.Contains(v) ? 0 : 1).ToArray();
                        var hold = (int)Math.Clamp(e.T - pendingAt, 10, 2000);
                        timed.Add((new Macros.Step
                        {
                            Type = Macros.StepKeys,
                            Vks = combo,
                            HoldMs = hold,
                            WinTitle = pendingWin,
                        }, pendingAt, hold));
                        pendingKey = -1;
                    }
                    break;

                case "md":
                    mDown = true;
                    mDownAt = e.T;
                    mDownX = e.X; mDownY = e.Y;
                    lastX = e.X; lastY = e.Y;
                    maxDist = 0;
                    mBtn = e.Button;
                    mWin = e.Win ?? "";
                    break;

                case "mm":
                    if (mDown)
                    {
                        lastX = e.X; lastY = e.Y;
                        var d = Math.Max(Math.Abs(e.X - mDownX), Math.Abs(e.Y - mDownY));
                        if (d > maxDist) maxDist = d;
                    }
                    break;

                case "mu":
                    if (mDown && e.Button == mBtn)
                    {
                        var dur = e.T - mDownAt;
                        if (maxDist <= DragThresholdPx && dur <= DragThresholdMs)
                        {
                            timed.Add((new Macros.Step
                            {
                                Type = Macros.StepClick,
                                Target = "coord",            // 录到的就是位置；图标匹配由用户手动改
                                X = mDownX, Y = mDownY,
                                Sw = screenW, Sh = screenH,
                                Button = mBtn,
                                WinTitle = mWin,
                            }, mDownAt, 60));
                        }
                        else
                        {
                            // ★ 拖拽用**一个**步骤表达（起点 X,Y → 终点 X2,Y2）。
                            //   拆成 drag+move 两步的话，中间可能有别的步骤插进来，
                            //   而那时鼠标还按着 —— 结果就是"按着左键去按键"，非常难查。
                            var dragMs = (int)Math.Clamp(dur, 60, 10000);
                            timed.Add((new Macros.Step
                            {
                                Type = Macros.StepDrag,
                                X = mDownX, Y = mDownY,
                                X2 = e.X, Y2 = e.Y,
                                Sw = screenW, Sh = screenH,
                                Button = mBtn,
                                WinTitle = mWin,
                                Ms = dragMs,          // 拖拽耗时：回放时按这个节奏移动
                            }, mDownAt, dragMs));
                        }
                        mDown = false;
                    }
                    break;
            }
        }

        // 收尾：还按着没抬的键（录音中途停止）补一个
        if (pendingKey >= 0)
        {
            timed.Add((new Macros.Step
            {
                Type = Macros.StepKeys,
                Vks = mods.Append(pendingKey).Distinct().ToArray(),
                HoldMs = 40,
                WinTitle = pendingWin,
            }, pendingAt, 40));
        }

        // 排序 + 插 wait
        timed.Sort((a, b) => a.at.CompareTo(b.at));
        long prevEnd = -1;
        foreach (var (s, at, dur) in timed)
        {
            if (prevEnd >= 0)
            {
                var gap = at - prevEnd;
                if (gap >= MinWaitMs)
                    outp.Add(new Macros.Step { Type = Macros.StepWait, Ms = (int)Math.Clamp(gap, 0, MaxWaitMs) });
            }
            outp.Add(s);
            prevEnd = at + dur;
        }

        return outp;
    }
}
