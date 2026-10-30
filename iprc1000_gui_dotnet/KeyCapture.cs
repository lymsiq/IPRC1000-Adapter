using System.Runtime.InteropServices;

namespace Iprc1000Adapter;

/// <summary>
/// **单次按键捕获**：让用户"按一下"就把按键录进宏步骤，而不是手填 `17,65`。
///
/// 为什么需要：宏步骤里的键是**虚拟键码**（`17,65` = Ctrl+A），
/// 普通用户不可能记得住，也查不到 —— 这个输入框等于废的。
///
/// 行为：
/// · 只捕获**一次**（一个组合），拿到就自动卸钩子
/// · **修饰键会被吞掉并累积**：按住 Ctrl 再按 A，得到 `[Ctrl, A]`
/// · 捕获到的那个主键**也会被吞**，不会打到当前窗口 —— 否则用户为了录一个 Ctrl+S，
///   会把文件保存一遍
/// · 5 秒没按任何键自动放弃（避免钩子一直挂着）
/// </summary>
internal static class KeyCapture
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    private const uint LLKHF_INJECTED = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int ptX, ptY; }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? lpModuleName);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMin, uint wMax);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    private static IntPtr _hook;
    private static HookProc? _proc;
    private static Thread? _thread;
    private static uint _tid;
    private static readonly object Gate = new();

    private static readonly List<int> _mods = new();
    private static int[]? _result;
    private static DateTime _began;

    public static bool IsCapturing { get; private set; }

    /// <summary>开始捕获。返回 false = 钩子装不上（安全软件拦截）。</summary>
    public static bool Begin()
    {
        lock (Gate)
        {
            if (IsCapturing) return true;
            _mods.Clear();
            _result = null;
            _began = DateTime.UtcNow;
            IsCapturing = true;
        }

        _thread = new Thread(() =>
        {
            _tid = GetCurrentThreadId();
            _proc = Callback;
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero) { IsCapturing = false; return; }
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        })
        { IsBackground = true, Name = "key-capture" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        for (var i = 0; i < 100 && _hook == IntPtr.Zero && IsCapturing; i++) Thread.Sleep(5);
        return _hook != IntPtr.Zero;
    }

    /// <summary>取结果并停止。返回 null = 用户还没按（继续等）或已超时。</summary>
    public static int[]? Take()
    {
        int[]? r;
        lock (Gate)
        {
            if ((DateTime.UtcNow - _began).TotalSeconds > 5) { Stop(); return null; }
            r = _result;
        }
        if (r != null) Stop();
        return r;
    }

    public static void Stop()
    {
        IsCapturing = false;
        lock (Gate) { _result = null; _mods.Clear(); }
        try { if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook); } catch { }
        _hook = IntPtr.Zero;
        try { if (_tid != 0) PostThreadMessage(_tid, 0x0012, IntPtr.Zero, IntPtr.Zero); } catch { }
        try { _thread?.Join(200); } catch { }
        _thread = null;
    }

    public static void Cancel() => Stop();

    private static bool IsModifier(int vk) => vk is 0x11 or 0xA2 or 0xA3     // Ctrl
                                                  or 0x10 or 0xA0 or 0xA1    // Shift
                                                  or 0x12 or 0xA4 or 0xA5    // Alt
                                                  or 0x5B or 0x5C;           // Win

    /// <summary>修饰键归一（左/右 Ctrl 都存成通用 Ctrl，注入时更好用）</summary>
    private static int NormMod(int vk) => vk switch
    {
        0xA2 or 0xA3 => 0x11,
        0xA0 or 0xA1 => 0x10,
        0xA4 or 0xA5 => 0x12,
        0x5C => 0x5B,
        _ => vk,
    };

    private static IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && IsCapturing)
        {
            try
            {
                var msg = (int)wParam;
                if (msg is WM_KEYDOWN or WM_SYSKEYDOWN or WM_KEYUP or WM_SYSKEYUP)
                {
                    var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                    if ((info.flags & LLKHF_INJECTED) == 0)
                    {
                        var vk = (int)info.vkCode;
                        var down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;

                        if (down)
                        {
                            if (IsModifier(vk))
                            {
                                lock (Gate)
                                {
                                    var n = NormMod(vk);
                                    if (!_mods.Contains(n)) _mods.Add(n);
                                }
                            }
                            else
                            {
                                lock (Gate)
                                {
                                    // 组合 = 已按住的修饰键 + 这一下主键
                                    _result = _mods.Append(vk).Distinct().ToArray();
                                }
                            }
                        }
                        // ★ 捕获期间**全部吞掉**：不吞的话，用户为了录 Ctrl+S 会真的保存一次文件
                        return (IntPtr)1;
                    }
                }
            }
            catch { }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }
}
