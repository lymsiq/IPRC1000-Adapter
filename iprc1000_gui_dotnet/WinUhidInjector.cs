using System.Runtime.InteropServices;
using System.Text;

namespace Iprc1000Adapter;

/// <summary>
/// WinUHid 虚拟 HID 键盘注入（**优先于 SendInput**）。
///
/// 为什么需要它：豆包/千问等输入法会**主动过滤 SendInput 注入的按键**
/// （检测 `KLLKHF_INJECTED` 标志），实测 SendInput 的右 Alt 确实进了输入管线
/// 但输入法不响应。WinUHid 从驱动层创建**虚拟 HID 设备**，对上层完全等价于真实键盘，
/// 输入法无法区分。驱动是 UMDF（`.dll` 形式，跑在 wudfhost），**不受 HVCI 约束**。
///
/// 调用接口（与 src-tauri/.../xiaomi/hid_injector.rs 一致）：
///   WinUHidGetDriverInterfaceVersion() -> u32
///   WinUHidCreateDevice(const WinUHidDeviceConfig*) -> handle
///   WinUHidStartDevice(handle, null, null) -> i32（非 0 成功）
///   WinUHidSubmitInputReport(handle, const u8*, u32) -> i32
///   WinUHidDestroyDevice(handle)
/// </summary>
internal static class WinUhidInjector
{
    // 标准键盘报告描述符（修饰键 8bit + 保留 8bit + LED 5bit + 6 个键码）
    private static readonly byte[] KeyboardDescriptor =
    {
        0x05, 0x01, 0x09, 0x06, 0xA1, 0x01, 0x05, 0x07, 0x19, 0xE0, 0x29, 0xE7, 0x15, 0x00, 0x25,
        0x01, 0x75, 0x01, 0x95, 0x08, 0x81, 0x02, 0x95, 0x01, 0x75, 0x08, 0x81, 0x01, 0x95, 0x05,
        0x75, 0x01, 0x05, 0x08, 0x19, 0x01, 0x29, 0x05, 0x91, 0x02, 0x95, 0x01, 0x75, 0x03, 0x91,
        0x01, 0x95, 0x06, 0x75, 0x08, 0x15, 0x00, 0x25, 0x73, 0x05, 0x07, 0x19, 0x00, 0x29, 0x73,
        0x81, 0x00, 0xC0,
    };

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct DeviceConfig
    {
        public int supportedEvents;
        public ushort vendorId, productId, versionNumber, descriptorLength;
        public IntPtr descriptor;
        public unsafe fixed byte containerId[16];
        public IntPtr instanceId;
        public IntPtr hardwareIds;
        public uint readReportPeriodUs;
    }

    private delegate uint FnGetVersion();
    private delegate IntPtr FnCreateDevice(IntPtr config);
    private delegate int FnStartDevice(IntPtr dev, IntPtr a, IntPtr b);
    private delegate int FnSubmitReport(IntPtr dev, IntPtr report, uint len);
    private delegate void FnDestroyDevice(IntPtr dev);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryW(string path);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32.dll")] private static extern bool FreeLibrary(IntPtr module);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandleW(string? name);

    private static IntPtr _module = IntPtr.Zero;
    private static IntPtr _dev = IntPtr.Zero;
    private static FnSubmitReport? _submit;
    private static FnDestroyDevice? _destroy;
    private static readonly object Gate = new();

    /// <summary>最近一次失败原因（给界面显示）</summary>
    public static string? LastError { get; private set; }
    /// <summary>驱动接口版本（0 = 驱动不可用）</summary>
    public static uint DriverVersion { get; private set; }
    /// <summary>虚拟键盘是否已就绪</summary>
    public static bool Ready => _dev != IntPtr.Zero;
    /// <summary>当前是否在用虚拟键盘注入（否则回退 SendInput）</summary>
    public static bool InUse { get; private set; }

    public static string? DllPath()
    {
        foreach (var c in new[]
                 {
                     Path.Combine(Env.AppDir, "WinUHid.dll"),
                     Path.Combine(Env.AppDir, "assets", "winuhid", "WinUHid.dll"),
                 })
            if (File.Exists(c)) return c;
        return null;
    }

    /// <summary>初始化（幂等）。失败时 LastError 给出原因。</summary>
    /// <summary>
    /// 只探测驱动是否就绪（加载 DLL + 读接口版本），**不创建虚拟 HID 设备**。
    ///
    /// 与 `EnsureReady()` 的区别：后者会真的创建一个虚拟键盘设备，代价明显；
    /// 而环境探针每轮都要跑，绝不能在里面创建设备（实测会显著拉高常驻 CPU）。
    /// 真正的设备改为**第一次注入时惰性创建**。
    /// </summary>
    public static bool ProbeDriver()
    {
        lock (Gate)
        {
            if (DriverVersion >= 1) return true;
            var dll = DllPath();
            if (dll == null) { LastError = "未找到 WinUHid.dll"; return false; }
            if (_module == IntPtr.Zero)
            {
                _module = GetModuleHandleW("WinUHid.dll");
                if (_module == IntPtr.Zero) _module = LoadLibraryW(dll);
                if (_module == IntPtr.Zero)
                {
                    LastError = $"LoadLibraryW WinUHid.dll 失败 err={Marshal.GetLastWin32Error()}";
                    return false;
                }
            }
            var pVer = GetProcAddress(_module, "WinUHidGetDriverInterfaceVersion");
            if (pVer == IntPtr.Zero) { LastError = "WinUHid.dll 缺少版本查询导出函数"; return false; }
            DriverVersion = Marshal.GetDelegateForFunctionPointer<FnGetVersion>(pVer)();
            if (DriverVersion < 1)
            {
                LastError = $"WinUHid 驱动未安装或未就绪（interface version={DriverVersion}）";
                return false;
            }
            return true;
        }
    }

    public static bool EnsureReady()
    {
        lock (Gate)
        {
            if (_dev != IntPtr.Zero) return true;

            var dll = DllPath();
            if (dll == null)
            {
                LastError = "未找到 WinUHid.dll";
                EtwKeySource.AppendLog("INJ  ★ 虚拟 HID 不可用：未找到 WinUHid.dll（回退 SendInput，输入法可能过滤）");
                return false;
            }

            if (_module == IntPtr.Zero)
            {
                // 先看是否已被加载（例如同进程重复初始化）
                _module = GetModuleHandleW("WinUHid.dll");
                if (_module == IntPtr.Zero) _module = LoadLibraryW(dll);
                if (_module == IntPtr.Zero)
                {
                    LastError = $"LoadLibraryW WinUHid.dll 失败 err={Marshal.GetLastWin32Error()}";
                    return false;
                }
            }

            var pVer = GetProcAddress(_module, "WinUHidGetDriverInterfaceVersion");
            var pCreate = GetProcAddress(_module, "WinUHidCreateDevice");
            var pStart = GetProcAddress(_module, "WinUHidStartDevice");
            var pSubmit = GetProcAddress(_module, "WinUHidSubmitInputReport");
            var pDestroy = GetProcAddress(_module, "WinUHidDestroyDevice");
            if (pVer == IntPtr.Zero || pCreate == IntPtr.Zero || pStart == IntPtr.Zero
                || pSubmit == IntPtr.Zero || pDestroy == IntPtr.Zero)
            {
                LastError = "WinUHid.dll 缺少导出函数（版本不匹配？）";
                return false;
            }

            DriverVersion = Marshal.GetDelegateForFunctionPointer<FnGetVersion>(pVer)();
            if (DriverVersion < 1)
            {
                LastError = $"WinUHid 驱动未安装或未就绪（interface version={DriverVersion}），请先装驱动";
                EtwKeySource.AppendLog("INJ  ★ " + LastError + "（回退 SendInput，输入法可能过滤）");
                return false;
            }

            var create = Marshal.GetDelegateForFunctionPointer<FnCreateDevice>(pCreate);
            var start = Marshal.GetDelegateForFunctionPointer<FnStartDevice>(pStart);
            _submit = Marshal.GetDelegateForFunctionPointer<FnSubmitReport>(pSubmit);
            _destroy = Marshal.GetDelegateForFunctionPointer<FnDestroyDevice>(pDestroy);

            _dev = CreateKeyboard(create, start);
            if (_dev == IntPtr.Zero)
            {
                // ★ 设备创建失败必须留痕：此时会静默回退 SendInput，而 SendInput 会被
                //   输入法过滤 —— 现象是"遥控器按了没反应"，日志上却什么都看不到。
                //   （限流 5 秒一条：失败时每次注入都会重试，不限流会把日志刷爆）
                if ((DateTime.UtcNow - _lastCreateFailLog).TotalSeconds >= 5)
                {
                    _lastCreateFailLog = DateTime.UtcNow;
                    EtwKeySource.AppendLog(
                        $"INJ  ★ 虚拟 HID 设备创建失败：{LastError ?? "未知"}（回退 SendInput，输入法可能过滤）");
                }
                ScheduleBackgroundRetry();
                return false;
            }

            LastError = null;
            // ★ 设备生命周期留痕：卡键排查时要能回答"这台虚拟键盘是什么时候建的/销毁的"
            EtwKeySource.AppendLog($"INJ  虚拟 HID 设备已创建（驱动 interface version={DriverVersion}）");
            return true;
        }
    }

    private static DateTime _lastCreateFailLog = DateTime.MinValue;
    private static int _retrying;          // 0 = 没有正在重试的后台线程

    /// <summary>
    /// 创建失败后**后台自愈重试**。
    ///
    /// 为什么需要：实测（2026-10-15）—— 上一次实例被 `taskkill /F` 强杀后，
    /// WinUHid 的设备槽位会被**短暂占住**，紧接着的新进程 `WinUHidCreateDevice` 直接失败，
    /// 于是整条注入链路静默退化成 SendInput（输入法会过滤它）→ 表现为"遥控器按了没反应"，
    /// 而几秒之后自己就好了。这种"过一会儿就能成功"的失败，重试是最好的解药。
    ///
    /// 为什么放后台线程：`EnsureReady()` 在注入热路径上（第一次按键时会调），
    /// 在里面 sleep 重试会把按键延迟拉长几百毫秒 —— 那正是我们一直想避免的。
    /// 后台重试不阻塞任何按键；这期间按键走 SendInput 兜底，成功率低但不至于完全没反应。
    /// </summary>
    private static void ScheduleBackgroundRetry()
    {
        if (Interlocked.CompareExchange(ref _retrying, 1, 0) != 0) return;
        var t = new Thread(() =>
        {
            try
            {
                for (var i = 1; i <= 6; i++)
                {
                    Thread.Sleep(600 * i);              // 0.6 / 1.2 / 1.8 … 秒，逐步拉开
                    if (_dev != IntPtr.Zero) break;
                    if (EnsureReady())
                    {
                        EtwKeySource.AppendLog($"INJ  ✓ 虚拟 HID 后台重试成功（第 {i} 次，累计约 {600 * i * (i + 1) / 2} ms）");
                        break;
                    }
                    if (i == 6)
                        EtwKeySource.AppendLog(
                            "INJ  ★ 虚拟 HID 重试 6 次仍失败 —— 注入会一直走 SendInput，" +
                            "输入法很可能过滤它；请检查 WinUHid 驱动是否正常（界面「环境设置」可装）");
                }
            }
            catch { }
            finally { Volatile.Write(ref _retrying, 0); }
        })
        { IsBackground = true, Name = "iprc1000-uhid-retry" };
        t.Start();
    }

    private static IntPtr CreateKeyboard(FnCreateDevice create, FnStartDevice start)
    {
        var instance = Encoding.Unicode.GetBytes("Iprc1000AdapterKeyboard\0");
        var cfg = Marshal.AllocHGlobal(Marshal.SizeOf<DeviceConfig>());
        var desc = Marshal.AllocHGlobal(KeyboardDescriptor.Length);
        var inst = Marshal.AllocHGlobal(instance.Length);
        try
        {
            Marshal.Copy(KeyboardDescriptor, 0, desc, KeyboardDescriptor.Length);
            Marshal.Copy(instance, 0, inst, instance.Length);

            var c = new DeviceConfig
            {
                supportedEvents = 0,
                vendorId = 0,
                productId = 1,
                versionNumber = 1,
                descriptorLength = (ushort)KeyboardDescriptor.Length,
                descriptor = desc,
                instanceId = inst,
                hardwareIds = IntPtr.Zero,
                readReportPeriodUs = 0,
            };
            Marshal.StructureToPtr(c, cfg, false);

            var dev = create(cfg);
            if (dev == IntPtr.Zero) { LastError = "WinUHidCreateDevice 失败（驱动没装好？）"; return IntPtr.Zero; }
            if (start(dev, IntPtr.Zero, IntPtr.Zero) == 0)
            {
                LastError = "WinUHidStartDevice 失败";
                return IntPtr.Zero;
            }
            return dev;
        }
        finally
        {
            Marshal.FreeHGlobal(cfg);
            Marshal.FreeHGlobal(desc);
            Marshal.FreeHGlobal(inst);
        }
    }

    // ── 键码换算 ──

    private static byte? ModifierBit(int vk) => vk switch
    {
        0x11 or 0xA2 => 0x01,   // Ctrl
        0x10 or 0xA0 => 0x02,   // Shift
        0x12 or 0xA4 => 0x04,   // Alt
        0x5B => 0x08,           // LWin
        0xA3 => 0x10,           // RCtrl
        0xA1 => 0x20,           // RShift
        0xA5 => 0x40,           // ★ 右 Alt
        0x5C => 0x80,           // RWin
        _ => null,
    };

    private static byte? Usage(int vk)
    {
        if (vk >= 0x41 && vk <= 0x5A) return (byte)(vk - 0x41 + 0x04);          // A-Z
        if (vk >= 0x31 && vk <= 0x39) return (byte)(vk - 0x31 + 0x1E);          // 1-9
        if (vk >= 0x70 && vk <= 0x7B) return (byte)(vk - 0x70 + 0x3A);          // F1-F12
        return vk switch
        {
            0x30 => 0x27,   // 0
            0x0D => 0x28,   // Enter
            0x1B => 0x29,   // Esc
            0x08 => 0x2A,   // Backspace
            0x09 => 0x2B,   // Tab
            0x20 => 0x2C,   // 空格
            0x21 => 0x4B,   // PageUp
            0x22 => 0x4E,   // PageDown
            0x23 => 0x4D,   // End
            0x24 => 0x4A,   // Home
            0x25 => 0x50,   // ←
            0x26 => 0x52,   // ↑
            0x27 => 0x4F,   // →
            0x28 => 0x51,   // ↓
            0x2D => 0x49,   // Insert
            0x2E => 0x4C,   // Delete
            0x5D => 0x65,   // 菜单键
            // ★ OEM 符号键（之前整组缺失 —— 导致 Ctrl+[ / Ctrl+] / / 等被静默丢弃，
            //   表现为"按下去没反应"。HID usage 见 HID Usage Tables 0x2D~0x38）
            0xBD => 0x2D,   // -
            0xBB => 0x2E,   // =
            0xDB => 0x2F,   // [
            0xDD => 0x30,   // ]
            0xDC => 0x31,   // \
            0xE2 => 0x64,   // \ (102 键盘）
            0xBA => 0x33,   // ;
            0xDE => 0x34,   // '
            0xC0 => 0x35,   // `
            0xBC => 0x36,   // ,
            0xBE => 0x37,   // .
            0xBF => 0x38,   // /
            _ => null,
        };
    }

    /// <summary>
    /// 提交一组虚拟键码（修饰键 + 最多 6 个键）。
    ///
    /// ★ 组合键要**分两帧发**：先只发修饰键，再发"修饰键 + 主键"。
    ///
    /// 为什么：`Win+X`、`Win+L` 这类**系统热键**由 Explorer/系统注册，判定条件是
    /// "主键按下时修饰键**已经处于按下状态**"。如果把修饰键和主键塞进同一帧 HID 报告，
    /// 系统在**同一时刻**看到两者，热键会漏判 —— 表现就是这个键"没反应"
    /// （而 Ctrl/Alt 类的普通组合键在同一帧里通常也能工作，所以以前没暴露）。
    /// </summary>
    public static bool Send(IReadOnlyList<int> vks)
    {
        if (!EnsureReady()) return false;

        // ① 若含修饰键且不止一个键：先单独发一帧"只有修饰键"的报告
        var modFrame = new byte[8];
        var hasMod = false;
        foreach (var vk in vks)
        {
            var bit = ModifierBit(vk);
            if (bit.HasValue) { modFrame[0] |= bit.Value; hasMod = true; }
        }
        if (hasMod && vks.Count > 1)
        {
            SubmitFrame(modFrame);
            Thread.Sleep(12);        // 让系统先"看到"修饰键按下，再收主键（12ms 用户无感）
        }

        var report = new byte[8];
        var keys = new List<byte>();
        foreach (var vk in vks)
        {
            var bit = ModifierBit(vk);
            if (bit.HasValue) { report[0] |= bit.Value; continue; }
            var u = Usage(vk);
            if (u.HasValue && !keys.Contains(u.Value) && keys.Count < 6) keys.Add(u.Value);
        }
        for (var i = 0; i < keys.Count; i++) report[2 + i] = keys[i];

        return SubmitFrame(report);
    }

    /// <summary>提交一帧 8 字节键盘报告</summary>
    private static bool SubmitFrame(byte[] report)
    {
        // ★ 排查"启动瞬间有按键注入"（2026-10-18）：每提交一帧都记 hex，
        //   这样观察者看到注入事件时，能立刻对上是不是我们发的、发的是什么。
        EtwKeySource.AppendLog("UHID frame " + BitConverter.ToString(report));
        return SubmitFrameInner(report);
    }

    private static bool SubmitFrameInner(byte[] report)
    {
        var buf = Marshal.AllocHGlobal(8);
        try
        {
            Marshal.Copy(report, 0, buf, 8);
            var rc = _submit!(_dev, buf, 8);
            if (rc == 0) { LastError = "WinUHidSubmitInputReport 失败"; return false; }
            InUse = true;
            LastError = null;
            return true;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>全部松开（空报告）。</summary>
    public static bool Release() => Send(Array.Empty<int>());

    public static object Status() => new
    {
        dllFound = DllPath() != null,
        driverVersion = DriverVersion,
        ready = Ready,
        error = LastError,
    };

    public static void Shutdown()
    {
        lock (Gate)
        {
            var had = _dev != IntPtr.Zero;
            try { if (had) _destroy?.Invoke(_dev); } catch { }
            _dev = IntPtr.Zero;
            InUse = false;
            // ★ 销毁也留痕：配合"创建"那条，就能看出虚拟键盘是不是被反复重建
            //   （反复重建意味着某次残留的按下状态可能被留在系统里）
            if (had) EtwKeySource.AppendLog("INJ  虚拟 HID 设备已销毁");
        }
    }
}
