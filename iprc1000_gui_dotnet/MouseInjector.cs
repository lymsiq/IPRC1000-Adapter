using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Iprc1000Adapter;

/// <summary>
/// 鼠标注入：相对移动 / 按键 / 滚轮。
///
/// ★ 为什么用 SendInput，而**不用** WinUHid 虚拟 HID：
///   WinUHid 存在的唯一原因是**输入法会过滤注入的键盘事件**（它检测 KLLKHF_INJECTED 标志，
///   实测 SendInput 的右 Alt 进了输入管线但豆包不响应）。而**鼠标事件没有这层过滤** ——
///   浏览器 / Office / 输入法自己的窗口都正常接受 SendInput 的鼠标。
///   反过来，虚拟 HID 鼠标要多建一台设备、多一份驱动依赖，而 WinUHid 本身还有
///   "设备槽位被占 → WinUHidCreateDevice 失败 → 静默退化成 SendInput"的既知问题
///   （2026-10-15 实测踩到）。用 SendInput 直接绕开整整一类故障。
///   代价：需要 raw input 的游戏（FPS 之类）会忽略注入鼠标 —— 本场景不需要。
///
/// ⚠ 另一个既知约束：**UIPI**。若前台窗口比本进程权限更高，SendInput 会被静默丢弃。
///   本程序以管理员运行，正常；但探针会把返回值与 Win32 错误报出来，便于现场判断。
/// </summary>
internal static class MouseInjector
{
    // ── P/Invoke ──
    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out POINT p);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    /// <summary>DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2</summary>
    private static readonly IntPtr DpiPerMonitorV2 = new(-4);

    private const int SM_CXSCREEN = 0, SM_CYSCREEN = 1;
    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

    private const uint INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020, MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800, MOUSEEVENTF_HWHEEL = 0x1000;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

    /// <summary>最近一次注入是否被系统接受（false 多为 UIPI 拦截）</summary>
    public static string? LastError { get; private set; }

    /// <summary>复用的输入数组 —— 避免每帧分配（引擎会每秒调上百次）</summary>
    private static readonly INPUT[] One = new INPUT[1];

    private static bool Send(uint flags, int dx = 0, int dy = 0, uint data = 0)
    {
        One[0] = new INPUT
        {
            type = INPUT_MOUSE,
            mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = data, dwFlags = flags, dwExtraInfo = IntPtr.Zero },
        };
        var n = SendInput(1, One, Marshal.SizeOf<INPUT>());
        if (n == 1) { LastError = null; return true; }
        LastError = $"SendInput 返回 {n}，Win32 错误 {Marshal.GetLastWin32Error()}";
        return false;
    }

    /// <summary>相对移动（像素；受系统「指针精确度」+ 指针速度档位影响）</summary>
    public static bool Move(int dx, int dy) => Send(MOUSEEVENTF_MOVE, dx, dy);

    /// <summary>
    /// 一次调用灌 N 个相同的相对位移。
    /// 为什么需要它：`SendInput` 支持一次传多个 INPUT，由系统**在同一批**里处理 ——
    /// 比循环调用少一大截内核往返，引擎在高 tick 率下会明显更省（探针里给了实测对比）。
    /// </summary>
    public static bool MoveBatch(int dx, int dy, int times)
    {
        if (times <= 0) return true;
        var arr = new INPUT[times];
        for (var i = 0; i < times; i++)
            arr[i] = new INPUT
            {
                type = INPUT_MOUSE,
                mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = MOUSEEVENTF_MOVE },
            };
        var n = SendInput((uint)times, arr, Marshal.SizeOf<INPUT>());
        if (n == times) { LastError = null; return true; }
        LastError = $"SendInput 批 {times} 返回 {n}，Win32 错误 {Marshal.GetLastWin32Error()}";
        return false;
    }

    /// <summary>绝对定位（归一化 0..65535，映射到**整个虚拟桌面**；不走加速曲线）</summary>
    public static bool MoveAbsolute(int nx, int ny) => Send(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE, nx, ny);

    public static bool Left(bool down) => Send(down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP);
    public static bool Right(bool down) => Send(down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP);
    public static bool Middle(bool down) => Send(down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP);

    /// <summary>滚轮（正 = 上滚；一格 = 120）</summary>
    public static bool Wheel(int delta) => Send(MOUSEEVENTF_WHEEL, 0, 0, unchecked((uint)delta));

    public static bool WheelH(int delta) => Send(MOUSEEVENTF_HWHEEL, 0, 0, unchecked((uint)delta));

    public static (int X, int Y) CursorPos() => GetCursorPos(out var p) ? (p.X, p.Y) : (-1, -1);

    public static (int W, int H) ScreenSize() => (GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN));

    /// <summary>虚拟桌面（多显示器时比主屏宽）—— 绝对定位的归一化基准是**它**，不是主屏</summary>
    public static (int X, int Y, int W, int H) VirtualScreen() =>
        (GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
         GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN));

    /// <summary>
    /// 把光标放到**虚拟桌面**中心。
    /// ⚠ 必须用虚拟桌面：`MOUSEEVENTF_ABSOLUTE` 的 0..65535 映射到整个虚拟桌面，
    ///   按主屏宽度归一化会把光标放到主屏左侧而不是中心（本机主屏 2560 宽但光标在 x=3467，
    ///   说明有第二块显示器）。
    /// </summary>
    private static void CenterCursor()
    {
        var (vx, vy, vw, vh) = VirtualScreen();
        if (vw <= 0 || vh <= 0) { var (mw, mh) = ScreenSize(); vw = mw; vh = mh; vx = 0; vy = 0; }
        MoveAbsolute((vx + vw / 2) * 65535 / Math.Max(vw - 1, 1),
                     (vy + vh / 2) * 65535 / Math.Max(vh - 1, 1));
        WaitCursorSettled();
    }

    /// <summary>
    /// 等光标**真正停稳**再读位置。
    /// 为什么必须等：绝对定位之后系统还要做一轮指针处理，紧接着读会拿到过渡中的位置。
    /// 第一版探针就是因此把 1 px 的用例读成 5 px —— 那是**测量污染**，不是真加速。
    /// </summary>
    private static (int X, int Y) WaitCursorSettled(int maxMs = 400)
    {
        var last = CursorPos();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var stable = 0;
        while (sw.ElapsedMilliseconds < maxMs)
        {
            Thread.Sleep(12);
            var now = CursorPos();
            if (now == last) { if (++stable >= 2) return now; }
            else { stable = 0; last = now; }
        }
        return last;
    }

    /// <summary>
    /// 单次相对移动的**净位移**：归位 → 量基准 → 发 +d → 停稳读 → 发 −d → 停稳读，
    /// 取两个方向幅度的中位数（重复 reps 次），抵消方向性偏差与偶发抖动。
    /// </summary>
    private static (double fwd, double back) MoveOnceMeasured(int d, int reps = 5)
    {
        var fwd = new List<double>();
        var back = new List<double>();
        for (var i = 0; i < reps; i++)
        {
            CenterCursor();
            var (x0, _) = CursorPos();
            Move(d, 0);
            var (x1, _) = WaitCursorSettled();
            fwd.Add(x1 - x0);
            Move(-d, 0);
            var (x2, _) = WaitCursorSettled();
            // 从「+d 之后的位置」往回走了多少（不是相对基准 —— 那样量出来恒为 0，
            // 因为反向移动正确地把光标送回了原点，第一版就写错成 x0-x2 而误报 0）
            back.Add(x1 - x2);
        }
        fwd.Sort(); back.Sort();
        return (fwd[fwd.Count / 2], back[back.Count / 2]);
    }

    /// <summary>系统「指针精确度」设置（HKCU\Control Panel\Mouse）</summary>
    public static (string speed, string t1, string t2, bool on) AccelerationSetting()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Mouse");
            var s = k?.GetValue("MouseSpeed")?.ToString() ?? "?";
            var a = k?.GetValue("MouseThreshold1")?.ToString() ?? "?";
            var b = k?.GetValue("MouseThreshold2")?.ToString() ?? "?";
            return (s, a, b, s != "0");
        }
        catch { return ("?", "?", "?", false); }
    }

    /// <summary>`MouseSensitivity`（指针速度档位 1..20，默认 10）—— 对输入的**线性增益**，
    /// 与「指针精确度」的加速曲线是两回事，两者会叠加。</summary>
    public static string PointerSpeed()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Mouse");
            return k?.GetValue("MouseSensitivity")?.ToString() ?? "?";
        }
        catch { return "?"; }
    }

    /// <summary>
    /// **鼠标注入实测探针** —— 回答"实现之前必须先知道"的几个数：
    ///   ① 系统指针加速 / 指针速度档位（会不会与自研增速叠加）
    ///   ② 相对移动的实测净位移：请求 N px → 光标实际走多少 px（1:1 才可直接按像素写）
    ///   ③ 单次 / 批量 SendInput 的开销 → 决定 tick 频率与每帧增量上限
    /// 测完把光标**还原到原位置**。
    ///
    /// ⚠ 必须先把进程设成 **DPI 感知**再测：CLI 分支跑在 `ApplicationConfiguration.Initialize()`
    ///   之前，默认是 DPI 未感知的 —— 那种状态下 `GetCursorPos` 给的是**虚拟化坐标**，
    ///   测出来的映射必然是错的。
    /// </summary>
    public static string Probe()
    {
        var sb = new StringBuilder();
        sb.AppendLine("IPRC1000 鼠标注入实测");
        sb.AppendLine(new string('=', 58));

        var dpiOk = SetProcessDpiAwarenessContext(DpiPerMonitorV2);

        var (speed, t1, t2, accelOn) = AccelerationSetting();
        var ptr = PointerSpeed();
        var (vx, vy, vw, vh) = VirtualScreen();
        var (sw, sh) = ScreenSize();

        sb.AppendLine("① 环境");
        sb.AppendLine($"     进程 DPI 感知（本次设置）：{(dpiOk ? "已设置 PerMonitorV2" : "★ 设置失败/已被固定 —— 测量可能失真")}");
        sb.AppendLine($"     「指针精确度」MouseSpeed={speed}  Threshold1={t1}  Threshold2={t2}"
                      + (accelOn ? "   ⇒ **开启**（相对移动会被加速曲线处理）" : "   ⇒ 关闭"));
        sb.AppendLine($"     指针速度档位 MouseSensitivity={ptr}（1..20，默认 10；这是**线性增益**，与上面那个是两回事）");
        sb.AppendLine($"     主屏 {sw}×{sh} ｜ 虚拟桌面 {vw}×{vh} @ ({vx},{vy})"
                      + (vw > sw ? "   ⇒ 有第二块显示器" : ""));
        var start = CursorPos();
        sb.AppendLine($"     开始时光标位置：({start.X}, {start.Y})   ← 结束会还原");
        sb.AppendLine();

        sb.AppendLine("② 单次相对移动的净位移（归位→量基准→发±d→停稳→取 5 次中位数）");
        sb.AppendLine("     请求 px |    +d 实测 |    −d 实测 |  比值");
        foreach (var d in new[] { 1, 2, 4, 8, 16, 32, 64, 120, 240 })
        {
            var (f, b) = MoveOnceMeasured(d);
            sb.AppendLine($"     {d,7} | {f,10:F1} | {b,10:F1} | {f / d,6:F2}");
        }
        sb.AppendLine();

        sb.AppendLine("③ 高频工况：20 帧 × +4 px（就是引擎实际会做的事）");
        var totals = new List<double>();
        for (var i = 0; i < 5; i++)
        {
            CenterCursor();
            var (x0, _) = CursorPos();
            for (var k = 0; k < 20; k++) Move(4, 0);
            var (x1, _) = WaitCursorSettled();
            totals.Add(x1 - x0);
        }
        totals.Sort();
        var med = totals[totals.Count / 2];
        sb.AppendLine($"     请求合计 80 px ｜ 实测中位 {med:F1} px ｜ 比值 {med / 80:F2}");
        sb.AppendLine();

        sb.AppendLine("④ SendInput 开销（引擎每秒会调上百次，这个数决定 tick 频率上限）");
        CenterCursor();
        const int N = 300;
        var sw1 = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < N; i++) Move(1, 0);
        sw1.Stop();
        var per1 = sw1.Elapsed.TotalMilliseconds * 1000 / N;
        CenterCursor();
        var sw2 = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < N / 20; i++) MoveBatch(1, 0, 20);
        sw2.Stop();
        var perBatchEvent = sw2.Elapsed.TotalMilliseconds * 1000 / N;
        sb.AppendLine($"     逐次调用：{N} 次 {sw1.Elapsed.TotalMilliseconds,7:F1} ms ⇒ 单次 {per1,6:F1} µs");
        sb.AppendLine($"     批量调用：{N / 20} 次 × 20 个 = {N} 个事件 {sw2.Elapsed.TotalMilliseconds,6:F1} ms"
                      + $" ⇒ 每事件 {perBatchEvent,6:F1} µs（**快 {per1 / Math.Max(perBatchEvent, 0.01):F1} 倍**）");
        sb.AppendLine($"     ⇒ 按逐次调用算，100 Hz 的开销约 {per1 * 100 / 1000:F2} ms/s"
                      + $"（{per1 * 100 / 10000:F2}% CPU）");
        sb.AppendLine();

        sb.AppendLine("⑤ 结论（对实现的意义）");
        sb.AppendLine("     · 若②的「比值」列 ≈1.00 → 相对移动可预测，引擎直接按像素发增量即可。");
        sb.AppendLine("     · 若不等于 1 → **必须先按这张表做标定**（或改用绝对定位绕过加速曲线）；");
        sb.AppendLine("       门店机器的「指针精确度」多半是**开着**的，不能只按本机结论写死。");
        sb.AppendLine("     · ②里最大那个仍然 1:1 的 d，就是**单帧增量上限**（超过会跳格/丢精度）。");
        sb.AppendLine("     · ④若批量明显更快 → 引擎用批量下发（一帧里补齐多个位移），省内核往返。");

        // 还原光标
        if (start.X >= 0 && start.Y >= 0 && vw > 0 && vh > 0)
            MoveAbsolute((start.X - vx) * 65535 / Math.Max(vw - 1, 1),
                         (start.Y - vy) * 65535 / Math.Max(vh - 1, 1));
        return sb.ToString();
    }
}
