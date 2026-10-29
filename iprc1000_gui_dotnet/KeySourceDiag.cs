using System.Runtime.InteropServices;
using System.Text;

namespace Iprc1000Adapter;

/// <summary>
/// **键源归因诊断**（2026-10-29）。
///
/// 为什么需要：`WH_KEYBOARD_LL` 的钩子**拿不到"这个键来自哪个设备"** —— 只有一个 vkCode。
/// 所以遇到"键盘自己在冒 m"这类问题，只能一层层排除（拔硬件 / 停进程 / 禁设备），
/// 2026-10-29 那次就为此连错了两次方向（先怪遥控器、再怪我们的虚拟键盘）。
///
/// 做法：用 **Raw Input**（`RIDEV_INPUTSINK`）旁听所有键盘事件，每个事件带 `hDevice`，
/// 再反查设备名 ⇒ 得到「哪个设备发了什么键、多久一次」。
///
/// 注意：这是**只读旁听**，不拦截、不注入，所以对输入延迟零影响，
/// 也不会像钩子那样被反作弊盯上。
/// </summary>
internal static class KeySourceDiag
{
    private const int WM_INPUT = 0x00FF;
    private const int RIDEV_INPUTSINK = 0x00000100;
    private const int RID_INPUT = 0x10000003;
    private const int RIM_TYPEKEYBOARD = 1;
    private const uint RIDI_DEVICENAME = 0x20000007;

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWKEYBOARD
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VKey;
        public uint Message;
        public uint ExtraInformation;
    }

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterClassExW(ref WNDCLASSEX wc);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string cls, string name, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] d, uint n, uint size);
    [DllImport("user32.dll")] private static extern uint GetRawInputData(IntPtr h, uint cmd, IntPtr buf, ref uint size, uint headerSize);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetRawInputDeviceInfoW(IntPtr h, uint cmd, IntPtr data, ref uint size);
    [DllImport("user32.dll")] private static extern int GetMessageW(out MSG m, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref MSG m);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? n);

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEX
    {
        public uint cbSize; public uint style; public IntPtr lpfnWndProc;
        public int cbClsExtra; public int cbWndExtra; public IntPtr hInstance;
        public IntPtr hIcon; public IntPtr hCursor; public IntPtr hbrBackground;
        public string? lpszMenuName; public string lpszClassName; public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam;
        public uint time; public int ptX; public int ptY;
    }

    private static Thread? _thread;
    private static IntPtr _hwnd;
    private static WndProc? _proc;          // 必须保住引用，否则被 GC 回收 → 崩

    // ── 归因统计：设备名 → (次数, 最后一次 vk, 最后一次时间) ──
    private static readonly object _lock = new();
    private static readonly Dictionary<string, (long count, int lastVk, long lastAt)> _stats = new();
    private static readonly Dictionary<IntPtr, string> _names = new();

    public static bool Running => _hwnd != IntPtr.Zero;

    public static void Start(int timeoutMs = 1500)
    {
        if (_hwnd != IntPtr.Zero) return;
        _thread = new Thread(() =>
        {
            const string cls = "IPRC_RAWINPUT_DIAG";
            _proc = WndProcImpl;
            var wc = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
                hInstance = GetModuleHandleW(null),
                lpszClassName = cls
            };
            if (RegisterClassExW(ref wc) == IntPtr.Zero && Marshal.GetLastWin32Error() != 1410)
                return;                                        // 1410 = 类已存在
            // HWND_MESSAGE = (IntPtr)(-3)：消息专用窗，不显示
            _hwnd = CreateWindowExW(0, cls, "diag", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero,
                GetModuleHandleW(null), IntPtr.Zero);
            if (_hwnd == IntPtr.Zero) return;

            var dev = new[]
            {
                new RAWINPUTDEVICE
                {
                    usUsagePage = 0x01, usUsage = 0x06,   // Generic Desktop / Keyboard
                    dwFlags = RIDEV_INPUTSINK, hwndTarget = _hwnd
                }
            };
            RegisterRawInputDevices(dev, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());

            while (GetMessageW(out var m, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref m);
                DispatchMessageW(ref m);
            }
        })
        { IsBackground = true, Name = "rawinput-diag" };
        _thread.Start();
        for (var i = 0; i < timeoutMs / 10 && _hwnd == IntPtr.Zero; i++) Thread.Sleep(10);
    }

    private static IntPtr WndProcImpl(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (msg == WM_INPUT) OnRawInput(lParam);
        }
        catch { }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private static void OnRawInput(IntPtr lParam)
    {
        uint size = 0;
        GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref size, (uint)Marshal.SizeOf<RAWINPUTHEADER>());
        if (size == 0 || size > 64 * 1024) return;

        var buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(lParam, RID_INPUT, buf, ref size, (uint)Marshal.SizeOf<RAWINPUTHEADER>()) == uint.MaxValue)
                return;
            var hdr = Marshal.PtrToStructure<RAWINPUTHEADER>(buf);
            if (hdr.dwType != RIM_TYPEKEYBOARD) return;

            var kb = Marshal.PtrToStructure<RAWKEYBOARD>(IntPtr.Add(buf, Marshal.SizeOf<RAWINPUTHEADER>()));
            // 只统计**按下**：RI_KEY_BREAK(0x01) = 弹起；VKey 0 = 占位假键
            if (kb.VKey == 0 || (kb.Flags & 0x01) != 0) return;

            var name = DeviceName(hdr.hDevice);
            lock (_lock)
            {
                _stats.TryGetValue(name, out var s);
                _stats[name] = (s.count + 1, kb.VKey, Environment.TickCount64);
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static string DeviceName(IntPtr hDevice)
    {
        // hDevice == 0：SendInput/keybd_event 注入的事件没有设备（实测确认）
        if (hDevice == IntPtr.Zero) return "（软件注入，无设备）";
        lock (_lock) { if (_names.TryGetValue(hDevice, out var cached)) return cached; }

        uint size = 0;
        GetRawInputDeviceInfoW(hDevice, RIDI_DEVICENAME, IntPtr.Zero, ref size);
        string name;
        if (size == 0 || size > 4096) name = $"(未知 {hDevice})";
        else
        {
            var buf = Marshal.AllocHGlobal((int)size * 2 + 2);
            try
            {
                if (GetRawInputDeviceInfoW(hDevice, RIDI_DEVICENAME, buf, ref size) == uint.MaxValue)
                    name = $"(未知 {hDevice})";
                else name = Marshal.PtrToStringUni(buf) ?? $"(未知 {hDevice})";
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        lock (_lock) { _names[hDevice] = name; }
        return name;
    }

    /// <summary>当前统计快照（按次数降序），一行一个设备。</summary>
    public static string Snapshot()
    {
        if (!Running) return "（未启动）";
        (string name, long count, int vk, long at)[] rows;
        lock (_lock)
        {
            rows = _stats.Select(kv => (kv.Key, kv.Value.count, kv.Value.lastVk, kv.Value.lastAt))
                         .OrderByDescending(x => x.Item2).ToArray();
        }
        if (rows.Length == 0) return "（还没有按键）";

        var sb = new StringBuilder();
        sb.AppendLine("设备名 / 次数 / 最近按键 / 距今");
        foreach (var r in rows)
        {
            var ago = (Environment.TickCount64 - r.at) / 1000.0;
            var shortName = r.name.Length > 70 ? r.name[..70] + "…" : r.name;
            sb.AppendLine($"  {shortName}");
            sb.AppendLine($"      {r.count} 次   vk=0x{r.vk:X2}    {ago:F1}s 前");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>清空统计（每次诊断前调用，避免被历史数据干扰）</summary>
    public static void Reset() { lock (_lock) { _stats.Clear(); } }
}
