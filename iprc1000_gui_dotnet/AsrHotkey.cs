using System.Runtime.InteropServices;

namespace Iprc1000Adapter;

/// <summary>
/// **本地识别快捷键**（2026-10-29）：Ctrl+Alt+V（VK 可配）。
///
/// 为什么单独一套钩子，而不是复用 GhostGuard 里的那个：
///   GhostGuard 只在"需要抑制幽灵 m"时才启用，headless / 某些状态下不装钩子，
///   于是快捷键会**随幽灵键抑制的启停而失效** —— 这类"有时能用有时不能"最难查。
///   这里用**自己的线程 + 自己的低级钩子**，只要总开关开着就常驻。
///
/// 三个必须做对的点：
///   1. **不滤 INJECTED**：用户会把遥控器语音键映射成这个组合键，那条路径正是我们自己注入的。
///   2. **只旁听**：永远 CallNextHookEx，绝不吞键（否则 Ctrl+Alt+V 在别的程序里就废了）。
///   3. **按下即开录、松开才收尾**：中间不重置，避免长句被切断。
/// </summary>
internal static class AsrHotkey
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    private const int VK_CONTROL = 0x11, VK_MENU = 0x12;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public int vkCode, scanCode, flags, time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam, lParam;
        public uint time;
        public int ptX, ptY;
    }

    private static Thread? _thread;
    private static IntPtr _hook = IntPtr.Zero;
    private static HookProc? _proc;
    private static volatile bool _down;         // 当前是否处于"按住"状态
    private static long _downAt;

    public static bool Ready => _hook != IntPtr.Zero;

    /// <summary>钩子收到的按键总数（诊断用：能涨说明钩子在，没涨说明钩子收不到事件）。</summary>
    public static long Seen;
    /// <summary>命中快捷键的次数。</summary>
    public static long Hits;
    public static string Describe => Settings.AsrHotkeyVk == 0
        ? "(未设置)"
        : "Ctrl+Alt+" + (char)Settings.AsrHotkeyVk;

    public static void Start()
    {
        if (_thread != null) return;
        _proc = Callback;
        _thread = new Thread(() =>
        {
            try
            {
                _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
                if (_hook == IntPtr.Zero)
                {
                    Safe.Note($"ASR 快捷键钩子安装失败（错误 {Marshal.GetLastWin32Error()}）");
                    return;
                }
                Safe.Note("ASR 快捷键已就绪：" + Describe);
                while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
            }
            catch (Exception e) { Safe.Note("ASR 快捷键线程异常：" + e.Message); }
        })
        { IsBackground = true, Name = "asr-hotkey" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        for (var i = 0; i < 100 && _hook == IntPtr.Zero; i++) Thread.Sleep(10);
    }

    public static void Stop()
    {
        if (_hook != IntPtr.Zero) { try { UnhookWindowsHookEx(_hook); } catch { } _hook = IntPtr.Zero; }
        _thread = null;
        _down = false;
    }

    /// <summary>总开关/键位变化时调用：该开就开、该关就关。</summary>
    public static void Sync()
    {
        if (Settings.AsrEnabled && Settings.AsrHotkeyVk != 0) Start();
        else Stop();
    }

    private static IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && Settings.AsrEnabled && Settings.AsrHotkeyVk != 0)
            {
                var m = wParam.ToInt32();
                if (m is WM_KEYDOWN or WM_SYSKEYDOWN or WM_KEYUP or WM_SYSKEYUP)
                {
                    var ki = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                    Seen++;
                    if (ki.vkCode == Settings.AsrHotkeyVk)
                    {
                        var ctrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
                        var alt = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
                        if (ctrl && alt)
                        {
                            var isDown = m is WM_KEYDOWN or WM_SYSKEYDOWN;
                            Hits++;
                            if (isDown && !_down)
                            {
                                _down = true; _downAt = Environment.TickCount64;
                                AsrRun.Begin();
                            }
                            else if (!isDown && _down)
                            {
                                _down = false;
                                AsrRun.Schedule(190);      // 与遥控器同一条收尾路径（含尾音窗口）
                            }
                        }
                    }
                }
            }
        }
        catch { }
        return CallNextHookEx(_hook, nCode, wParam, lParam);   // ★ 永不吞键
    }
}
