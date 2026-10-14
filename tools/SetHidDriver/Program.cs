// 遥控器设备驱动切换工具。
//
// 三种模式：
//  ①（无参数）        按 INF 安装：input.inf → HID-compliant device（非键盘，零 m）
//  ② --revert         还原成 HID Keyboard Device（keyboard.inf）
//  ③ --model "<型号>"  绑定到**指定型号**（走 Device Manager「从列表中选取」的同一条路）
//
// ③的原理：SetupDiBuildDriverInfoList(SPDIT_CLASSDRIVER) → SetupDiEnumDriverInfo 找型号
//          → SetupDiSetSelectedDriver → SetupDiCallClassInstaller(DIF_INSTALLDEVICE)
// 与 UpdateDriverForPlugAndPlayDevices(FORCE) 的关键区别：**后者要求 INF 里存在匹配硬件 ID
// 的型号**；而 ③ 可以指定任意型号，从而装上硬件 ID 不匹配的驱动 —— 例如
// xinputhid.inf 的「Bluetooth XINPUT compatible input device」（其安装节是
// `AddService=,2 ; null function driver for raw PDO` + xinputhid 过滤器），
// 装上去后：集合照常启动、**不是键盘（零 m）**、HID 接口可被用户态直读（低延迟）。
//
// 用法：
//   SetHidDriver.exe                          # 换成 HID-compliant device
//   SetHidDriver.exe --revert                 # 还原成 HID Keyboard Device
//   SetHidDriver.exe --models                 # 列出 HIDClass 下可用型号
//   SetHidDriver.exe --model "蓝牙 XINPUT..." [INF路径]   # 绑定指定型号

using System.Runtime.InteropServices;
using System.Text;

namespace SetHidDriver;

internal static class Program
{
    // ─────────── ① 按 INF 安装（UpdateDriverForPlugAndPlayDevices） ───────────
    [DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UpdateDriverForPlugAndPlayDevicesW(
        IntPtr hwndParent, string hardwareId, string fullInfPath,
        uint installFlags, out bool rebootRequired);

    private const string RemoteKeyboardHwid = @"HID\VID_0A5C&UP:0001_U:0006";
    private const uint INSTALLFLAG_FORCE = 0x00000001;
    private const uint INSTALLFLAG_NONINTERACTIVE = 0x00000004;

    // ─────────── ③ SetupAPI：按「指定型号」安装 ───────────
    private const int DIGCF_PRESENT = 0x02;
    private const int DIGCF_DEVICEINTERFACE = 0x10;
    private const int SPDIT_CLASSDRIVER = 1;
    private const int DIF_INSTALLDEVICE = 0x19;
    private const int ERROR_NO_MORE_ITEMS = 259;
    private const int ERROR_INVALID_DATA = 13;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SP_DRVINFO_DATA
    {
        public uint cbSize;
        public uint DriverType;
        public IntPtr Reserved;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Description;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string MfgName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string ProviderName;
        public System.Runtime.InteropServices.ComTypes.FILETIME DriverDate;
        public long DriverVersion;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator,
        IntPtr hwndParent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref SP_DEVINFO_DATA data,
        StringBuilder buf, uint bufSize, out uint required);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiBuildDriverInfoList(IntPtr set, ref SP_DEVINFO_DATA data, int driverType);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDriverInfoList(IntPtr set, ref SP_DEVINFO_DATA data, int driverType);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiEnumDriverInfoW(IntPtr set, ref SP_DEVINFO_DATA devInfo,
        int driverType, uint index, ref SP_DRVINFO_DATA drvInfo);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiSetSelectedDriverW(IntPtr set, ref SP_DEVINFO_DATA data,
        ref SP_DRVINFO_DATA drvInfo);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiCallClassInstaller(int installFunction, IntPtr set,
        ref SP_DEVINFO_DATA data);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    private const int DIF_SELECTDEVICE = 0x1B;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SP_CLASSINSTALL_HEADER
    {
        public uint cbSize;
        public int InstallFunction;
    }

    /// <summary>
    /// Device Manager 在列出「全部类驱动」前会先设这个参数（DIF_SELECTDEVICE）。
    /// 不设的话 `SetupDiBuildDriverInfoList(SPDIT_CLASSDRIVER)` 会返回 0 个型号。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SP_SELECTDEVICE_PARAMS
    {
        public SP_CLASSINSTALL_HEADER ClassInstallHeader;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 60)] public string Title;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 255)] public string Instructions;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 30)] public string ListLabel;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string SubTitle;
        public uint Reserved0;
        public uint Reserved1;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiSetClassInstallParamsW(IntPtr set, ref SP_DEVINFO_DATA devInfo,
        ref SP_SELECTDEVICE_PARAMS classInstallParams, uint classInstallParamsSize);

    private static void EnableClassDriverList(IntPtr set, ref SP_DEVINFO_DATA dd)
    {
        var p = new SP_SELECTDEVICE_PARAMS
        {
            ClassInstallHeader = new SP_CLASSINSTALL_HEADER
            { cbSize = (uint)Marshal.SizeOf<SP_CLASSINSTALL_HEADER>(), InstallFunction = DIF_SELECTDEVICE },
            Title = "选择驱动程序",
            Instructions = "",
            ListLabel = "型号",
            SubTitle = "",
            Reserved0 = 0,
            Reserved1 = 0,
        };
        if (!SetupDiSetClassInstallParamsW(set, ref dd, ref p, (uint)Marshal.SizeOf<SP_SELECTDEVICE_PARAMS>()))
            Console.WriteLine($"   SetClassInstallParams 失败 err={Marshal.GetLastWin32Error()}");
    }

    private static Guid GuidDevclassHidclass = new("745a17a0-74d3-11d0-b6fe-00a0c90f57da");

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var mode = args.Length > 0 ? args[0] : "";

        if (mode == "--models") { ListModels(); return 0; }

        if (mode == "--model" && args.Length > 1)
        {
            var want = args[1];
            if (args.Length > 2) StageInf(args[2]);
            return InstallModel(want);
        }

        var revert = mode == "--revert";
        var infPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "INF", revert ? "keyboard.inf" : "input.inf");
        Console.WriteLine($"目标硬件 ID : {RemoteKeyboardHwid}");
        Console.WriteLine($"INF          : {infPath}");
        var rb = false;
        var ok = UpdateDriverForPlugAndPlayDevicesW(IntPtr.Zero, RemoteKeyboardHwid, infPath,
            INSTALLFLAG_FORCE | INSTALLFLAG_NONINTERACTIVE, out rb);
        Console.WriteLine(ok ? "[✓] API 调用成功" : $"[X] 失败 err={Marshal.GetLastWin32Error()}");
        if (rb) Console.WriteLine("    系统提示需要重启");
        return ok ? 0 : 3;
    }

    private static void StageInf(string inf)
    {
        try
        {
            var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "System32", "pnputil.exe"),
                $"/add-driver \"{inf}\" /install")
            { UseShellExecute = true, Verb = "runas" });
            p?.WaitForExit(90_000);
            Console.WriteLine($"[i] 已暂存 INF：{inf}");
        }
        catch (Exception e) { Console.WriteLine($"[!] pnputil 暂存失败：{e.Message}"); }
    }

    private static void ListModels()
    {
        var set = SetupDiGetClassDevsW(ref GuidDevclassHidclass, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT);
        if (set == (IntPtr)(-1)) { Console.WriteLine("SetupDiGetClassDevs 失败"); return; }
        try
        {
            for (uint i = 0; ; i++)
            {
                var dd = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
                if (!SetupDiEnumDeviceInfo(set, i, ref dd)) break;
                var sb = new StringBuilder(512);
                SetupDiGetDeviceInstanceIdW(set, ref dd, sb, (uint)sb.Capacity, out _);
                var iid = sb.ToString();
                var shortId = iid.Length > 62 ? iid[..62] : iid;

                // 对每个设备分别试 CLASSDRIVER(1) 和 COMPATDRIVER(0)
                foreach (var (t, name) in new[] { (SPDIT_CLASSDRIVER, "CLASS"), (2, "COMPAT") })
                {
                    if (t == SPDIT_CLASSDRIVER) EnableClassDriverList(set, ref dd);
                    if (!SetupDiBuildDriverInfoList(set, ref dd, t))
                    {
                        Console.WriteLine($"{shortId}\n   [{name}] BuildDriverInfoList 失败 err={Marshal.GetLastWin32Error()}");
                        continue;
                    }
                    var n = 0;
                    var first = "";
                    var firstErr = 0;
                    for (uint j = 0; ; j++)
                    {
                        var drv = new SP_DRVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DRVINFO_DATA>() };
                        if (!SetupDiEnumDriverInfoW(set, ref dd, t, j, ref drv))
                        {
                            firstErr = n == 0 ? Marshal.GetLastWin32Error() : firstErr;
                            break;
                        }
                        if (n == 0) first = drv.Description;
                        n++;
                    }
                    if (n == 0 && firstErr != 0 && firstErr != 259)
                        Console.WriteLine($"   [{name}] EnumDriverInfo 失败 err={firstErr}");
                    SetupDiDestroyDriverInfoList(set, ref dd, t);
                    var mark = iid.ToLowerInvariant().Contains("0a5c") ? "  ★遥控器" : "";
                    Console.WriteLine($"{shortId}{mark}\n   [{name}] {n} 个型号" + (n > 0 ? $"（首个: {first}）" : ""));
                }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    private static int InstallModel(string want)
    {
        var set = SetupDiGetClassDevsW(ref GuidDevclassHidclass, IntPtr.Zero, IntPtr.Zero,
            DIGCF_PRESENT);
        if (set == (IntPtr)(-1))
        {
            Console.WriteLine($"[X] SetupDiGetClassDevs 失败 err={Marshal.GetLastWin32Error()}");
            return 2;
        }
        try
        {
            for (uint i = 0; ; i++)
            {
                var dd = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
                if (!SetupDiEnumDeviceInfo(set, i, ref dd)) break;
                var sb = new StringBuilder(512);
                SetupDiGetDeviceInstanceIdW(set, ref dd, sb, (uint)sb.Capacity, out _);
                if (!sb.ToString().ToLowerInvariant().Contains("0a5c")) continue;

                Console.WriteLine($"设备：{sb}");
                if (!SetupDiBuildDriverInfoList(set, ref dd, SPDIT_CLASSDRIVER))
                {
                    Console.WriteLine($"[X] BuildDriverInfoList 失败 err={Marshal.GetLastWin32Error()}");
                    return 2;
                }
                SP_DRVINFO_DATA? hit = null;
                var count = 0;
                for (uint j = 0; ; j++)
                {
                    var drv = new SP_DRVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DRVINFO_DATA>() };
                    if (!SetupDiEnumDriverInfoW(set, ref dd, SPDIT_CLASSDRIVER, j, ref drv)) break;
                    count++;
                    if (drv.Description.Equals(want, StringComparison.OrdinalIgnoreCase)) hit = drv;
                }
                Console.WriteLine($"  HIDClass 下共 {count} 个可用型号");
                if (hit == null)
                {
                    Console.WriteLine($"[X] 没找到型号「{want}」。先跑 --models 看有哪些；" +
                                      "若列表里没有，用 --model 带上 INF 路径暂存后再试。");
                    return 4;
                }

                var sel = hit.Value;
                if (!SetupDiSetSelectedDriverW(set, ref dd, ref sel))
                {
                    Console.WriteLine($"[X] SetSelectedDriver 失败 err={Marshal.GetLastWin32Error()}");
                    return 5;
                }
                if (!SetupDiCallClassInstaller(DIF_INSTALLDEVICE, set, ref dd))
                {
                    var e = Marshal.GetLastWin32Error();
                    Console.WriteLine($"[X] DIF_INSTALLDEVICE 失败 err={e}" +
                        (e == ERROR_NO_MORE_ITEMS ? "（无更多项）" : "") +
                        (e == ERROR_INVALID_DATA ? "（无效数据：该型号不接受此设备）" : ""));
                    return 6;
                }
                Console.WriteLine($"[✓] 已绑定到型号「{want}」");
                SetupDiDestroyDriverInfoList(set, ref dd, SPDIT_CLASSDRIVER);
                return 0;
            }
            Console.WriteLine("[X] 没找到遥控器设备");
            return 7;
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }
}
