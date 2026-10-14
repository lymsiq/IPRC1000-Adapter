using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;
using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Iprc1000Adapter;

/// <summary>键盘 / 注册表 / 设备 等环境探测与操作（对齐 Python 版）。</summary>
internal static class Env
{
    public const string AppName = "IPRC1000 Adapter";
    public const string RegPath = @"SYSTEM\CurrentControlSet\Services\BthPort\Parameters";
    public const int PcmPort = 31680;
    public const string RemoteMac = "20c0470a3483";

    /// <summary>
    /// `%APPDATA%\IPRC1000 Adapter` —— 配置、日志、语音桥统计都放这里。
    /// 语音桥（Rust）也按同一个约定写 bridge.log / bridge-stats.json。
    /// </summary>
    public static string ConfigDir
    {
        get
        {
            var d = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);
            try { Directory.CreateDirectory(d); } catch { }
            return d;
        }
    }

    private const uint DN_STARTED = 0x0000_0008;

    [System.Runtime.InteropServices.DllImport("cfgmgr32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

    [System.Runtime.InteropServices.DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_DevNode_Status(out uint pulStatus, out uint pulProblemNumber,
        uint dnDevInst, uint ulFlags);

    public static string AppDir => AppContext.BaseDirectory;

    public static string? FindAsset(string name)
    {
        foreach (var root in new[] { AppDir, Path.Combine(AppDir, "assets"), Path.Combine(AppDir, "ui") })
        {
            var p = Path.Combine(root, name);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    // ───────────── 设备驱动替换（把遥控器从「键盘」改成通用 HID 设备） ─────────────

    [System.Runtime.InteropServices.DllImport("newdev.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool UpdateDriverForPlugAndPlayDevicesW(
        IntPtr hwndParent, string hardwareId, string fullInfPath,
        uint installFlags, out bool rebootRequired);

    private const string RemoteKeyboardHwid = @"HID\VID_0A5C&UP:0001_U:0006";
    private const uint INSTALLFLAG_FORCE = 0x00000001;
    private const uint INSTALLFLAG_NONINTERACTIVE = 0x00000004;

    /// <summary>
    /// 把遥控器的 HID 键盘集合换驱动：
    ///   · false（默认）→ `input.inf` 的 `HID_DEVICE` 型号 = 「HID-compliant device」（null service）
    ///     ⇒ 系统不再把它当键盘 ⇒ 不会再合成 31 Hz 的按键重复（`m` 从根上消失）
    ///   · true → `keyboard.inf`，还原成 HID Keyboard Device
    /// 调用的是设备管理器「更新驱动」用的同一个 API。
    /// </summary>
    public static (bool ok, string msg) SetDeviceDriver(bool revert)
    {
        if (!IsAdmin()) return (false, "需要管理员权限才能更换设备驱动。");
        var inf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                               "INF", revert ? "keyboard.inf" : "input.inf");
        if (!File.Exists(inf)) return (false, $"找不到 {inf}");
        try
        {
            var ok = UpdateDriverForPlugAndPlayDevicesW(IntPtr.Zero, RemoteKeyboardHwid, inf,
                       INSTALLFLAG_FORCE | INSTALLFLAG_NONINTERACTIVE, out var reboot);
            if (!ok)
                return (false, $"切换失败（Win32 错误 {Marshal.GetLastWin32Error()}）。" +
                               "若设备当前未连接，请按遥控器任意键唤醒后重试。");
            var kb = RemoteStillKeyboard();
            var msg = revert
                ? (kb ? "已还原为 HID Keyboard Device。" : "已调用还原，但设备仍不是键盘，请重试。")
                : (kb ? "已调用切换，但设备仍被当作键盘，请重试或改用设备管理器手动更换。"
                      : "已切换为 HID-compliant device —— 系统不再把它当键盘，按键重复从根上消失。");
            if (reboot) msg += "（系统提示需要重启才能完全生效）";
            return (!kb || revert, msg);
        }
        catch (Exception e) { return (false, $"调用失败：{e.Message}"); }
    }

    public static bool IsAdmin()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    // ───────────── 注册表 ─────────────

    public static Dictionary<string, object?> RegState()
    {
        var d = new Dictionary<string, object?>
        {
            ["EtwDropLargeEvents"] = null, ["EtwLogSensitiveData"] = null, ["MaxEtwBytes"] = null,
            ["boot"] = "—", ["regMtime"] = "—", ["ok"] = false, ["needReboot"] = false,
        };
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(RegPath, false);
            if (k != null)
            {
                foreach (var n in new[] { "EtwDropLargeEvents", "EtwLogSensitiveData", "MaxEtwBytes" })
                    d[n] = k.GetValue(n);
                // .NET 不暴露键的最后写入时间，这里用注册表文件时间近似不了 —— 改走「是否已应用」推断
            }
        }
        catch { /* ignore */ }

        var boot = DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);
        d["boot"] = boot.ToString("yyyy-MM-dd HH:mm");
        d["uptimeSec"] = (long)(Environment.TickCount64 / 1000);

        var applied = Equals(d["EtwDropLargeEvents"], 0)
                      && Equals(d["EtwLogSensitiveData"], 1)
                      && d["MaxEtwBytes"] is int m && m >= 1024;
        d["configured"] = applied;
        d["ok"] = applied;
        return d;
    }

    public static (bool ok, string msg) ApplyRegistry()
    {
        if (!IsAdmin())
            return (false, "需要管理员权限才能写入 HKLM，请先以管理员身份重启本程序。");
        try
        {
            using var k = Registry.LocalMachine.CreateSubKey(RegPath, true);
            if (k == null) return (false, "无法打开注册表键。");
            k.SetValue("EtwDropLargeEvents", 0, RegistryValueKind.DWord);
            k.SetValue("EtwLogSensitiveData", 1, RegistryValueKind.DWord);
            k.SetValue("MaxEtwBytes", 8192, RegistryValueKind.DWord);
            return (true, "已写入推荐值（EtwDropLargeEvents=0、EtwLogSensitiveData=1、MaxEtwBytes=8192）。请重启电脑后生效。");
        }
        catch (Exception e) { return (false, $"写入注册表失败：{e.Message}"); }
    }

    // ───────────── 设备 / 声卡 ─────────────

    private static IEnumerable<string> SubKeys(RegistryKey root, string path)
    {
        using var k = root.OpenSubKey(path, false);
        if (k == null) yield break;
        foreach (var s in k.GetSubKeyNames()) yield return s;
    }

    public static bool DevNodeStarted(string instance)
    {
        try
        {
            if (CM_Locate_DevNodeW(out var dev, instance, 0) != 0) return false;
            if (CM_Get_DevNode_Status(out var status, out var problem, dev, 0) != 0) return false;
            return problem == 0 && (status & DN_STARTED) != 0;
        }
        catch { return true; }   // 兜底
    }

    public static (bool online, bool paired, List<object> hid, List<object> bth) RemoteDiag()
    {
        var hid = new List<object>();
        var bth = new List<object>();
        foreach (var top in SubKeys(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Enum\HID"))
        {
            var low = top.ToLowerInvariant();
            if (!(low.Contains("0a5c") && low.Contains("8502"))) continue;
            foreach (var inst in SubKeys(Registry.LocalMachine, $@"SYSTEM\CurrentControlSet\Enum\HID\{top}"))
                hid.Add(new { id = $"HID\\{top}\\{inst}", online = DevNodeStarted($"HID\\{top}\\{inst}") });
        }
        foreach (var top in SubKeys(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Enum\BTHENUM"))
        {
            if (!top.ToLowerInvariant().Contains(RemoteMac)) continue;
            foreach (var inst in SubKeys(Registry.LocalMachine, $@"SYSTEM\CurrentControlSet\Enum\BTHENUM\{top}"))
                bth.Add(new { id = $"BTHENUM\\{top}\\{inst}", online = DevNodeStarted($"BTHENUM\\{top}\\{inst}") });
        }
        bool paired;
        using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices", false))
            paired = k?.GetSubKeyNames().Any(s => s.Equals(RemoteMac, StringComparison.OrdinalIgnoreCase)) ?? false;

        bool Online(List<object> list) => list.Any(o => (bool)o.GetType().GetProperty("online")!.GetValue(o)!);
        return (Online(hid) || Online(bth), paired, hid, bth);
    }

    private static bool MmDevicesHas(string flow, string needle)
    {
        var path = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\{flow}";
        foreach (var ep in SubKeys(Registry.LocalMachine, path))
        {
            using var props = Registry.LocalMachine.OpenSubKey($@"{path}\{ep}\Properties", false);
            if (props == null) continue;
            foreach (var pkey in new[]
                     {
                         "{a45c254e-df1c-4efd-8020-67d146a850e0},2",
                         "{b3f8fa53-0004-438e-9003-51a46e139bfc},6",
                     })
            {
                if (props.GetValue(pkey) is string s && s.ToLowerInvariant().Contains(needle)) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 当前**默认录音设备**的名字。用 CoreAudio 官方 API 取，不是猜注册表。
    /// 取不到时返回 `（取不到：…）`，调用方按"不是 CABLE"处理。
    /// </summary>
    public static string DefaultCaptureName()
    {
        try
        {
            using var en = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            return en.GetDefaultAudioEndpoint(
                NAudio.CoreAudioApi.DataFlow.Capture,
                NAudio.CoreAudioApi.Role.Communications).FriendlyName;
        }
        catch (Exception e) { return $"（取不到：{e.Message}）"; }
    }

    /// <summary>
    /// 默认录音设备**是不是** CABLE Output。
    ///
    /// ⚠ 与上面的 `DefaultMicIsCable()` 是两回事：那个只查"CABLE Output 是否存在且 ACTIVE"，
    /// 名字里带着 `cable output` 就返回 true —— **设备在 ≠ 它是默认设备**。
    /// 界面写着「默认录音设备」其实一直用的是存在性判断，所以默认被系统改走时
    /// 界面照样显示 ✔（这正是"有时按语音键没声音/输入法报录音启动失败"的可疑原因之一）。
    /// 这里用官方 API 取真正的默认设备来比对。
    /// </summary>
    public static bool DefaultCaptureIsCable()
        => DefaultCaptureName().ToLowerInvariant().Contains("cable output");

    /// <summary>
    /// 探测「输入法/通话软件要打开的那个麦克风」此刻能不能打开。
    ///
    /// 专治「录音启动失败」这类间歇性问题：报告默认设备是谁、是不是 CABLE Output、
    /// 现在能不能用**共享模式**打开，打不开就把 HRESULT 原样带回来。
    ///
    /// 共享模式打开失败基本只有几种可能：设备被别的程序**独占**占着（AUDCLNT_E_DEVICE_IN_USE）、
    /// 设备已失效（AUDCLNT_E_DEVICE_INVALIDATED）或设备不在。这些正是输入法报错的真实原因。
    /// </summary>
    public static object ProbeCaptureDevice()
    {
        var name = DefaultCaptureName();
        var isCable = name.ToLowerInvariant().Contains("cable output");
        var canOpen = false;
        string? err = null;
        try
        {
            using var en = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            var dev = en.GetDefaultAudioEndpoint(
                NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.Role.Communications);
            var cap = new NAudio.CoreAudioApi.WasapiCapture(dev, true, 50);
            cap.StartRecording();
            Thread.Sleep(120);
            cap.StopRecording();
            cap.Dispose();
            canOpen = true;
        }
        catch (Exception e)
        {
            err = $"{e.GetType().Name}(HRESULT=0x{(e.HResult & 0xFFFFFFFF):X8}): {e.Message}";
        }
        return new
        {
            defaultMicName = name,
            isCable,
            canOpen,
            error = err,
        };
    }

    /// <summary>
    /// 系统是否**仍然把这个遥控器当键盘**（HID 键盘集合还在）。
    ///
    /// 关键安全判据：按键来源切成 ETW 后钩子就不装了 —— 此时若设备还是键盘，
    /// `m` 会**畅通无阻**地打进前台应用。所以要能检测出来并自动兜底。
    /// </summary>
    public static bool RemoteStillKeyboard()
    {
        foreach (var top in SubKeys(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Enum\HID"))
        {
            var low = top.ToLowerInvariant();
            if (!(low.Contains("0a5c") && low.Contains("8502"))) continue;
            foreach (var inst in SubKeys(Registry.LocalMachine, $@"SYSTEM\CurrentControlSet\Enum\HID\{top}"))
            {
                using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\HID\{top}\{inst}", false);
                // Service=kbdhid 说明它仍被绑定成键盘
                if (k?.GetValue("Service") is string svc && svc.Equals("kbdhid", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    /// <summary>遥控器当前是否还在系统里（不论绑的是什么驱动）——用于「断开即解锁」</summary>
    public static bool RemotePresent()
    {
        foreach (var top in SubKeys(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Enum\HID"))
        {
            var low = top.ToLowerInvariant();
            if (low.Contains("0a5c") && low.Contains("8502")) return true;
        }
        return false;
    }

    public static object CableState()
    {
        var cin = MmDevicesHas("Render", "cable input");
        var cout = MmDevicesHas("Capture", "cable output");
        // 真正的默认设备名（CoreAudio API）—— "设备在不在" 和 "是不是默认" 必须分开报告
        var dmName = DefaultCaptureName();
        return new
        {
            input = cin,
            output = cout,
            ready = cin && cout,
            defaultMic = dmName.ToLowerInvariant().Contains("cable output"),
            defaultMicName = dmName,
            version = (cin || cout) ? "4.5（内置）" : "—",
        };
    }

    // ───────────── 动作 ─────────────

    public static (bool ok, string msg) InstallCable()
    {
        var zip = FindAsset("VBCABLE_Driver_Pack45.zip");
        if (zip == null) return (false, "未找到内置驱动包 VBCABLE_Driver_Pack45.zip。");
        var stage = Path.Combine(Path.GetTempPath(), "IPRC1000Adapter", "vb-cable-stage");
        try
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
            Directory.CreateDirectory(stage);
            System.IO.Compression.ZipFile.ExtractToDirectory(zip, stage, true);
        }
        catch (Exception e) { return (false, $"解压驱动包失败：{e.Message}"); }

        var setup = new[] { "VBCABLE_Setup_x64.exe", "VBCABLE_Setup.exe" }
            .Select(n => Path.Combine(stage, n)).FirstOrDefault(File.Exists);
        if (setup == null) return (false, "驱动包里没有找到官方安装程序。");

        var psi = new ProcessStartInfo(setup) { UseShellExecute = true, Verb = "runas", WorkingDirectory = stage };
        try { Process.Start(psi); }
        catch (Exception e) { return (false, $"启动安装程序被拒绝或失败：{e.Message}"); }
        return (true, "已启动官方安装程序（含 WHQL 签名，可在内核隔离下加载）。装完后请重启电脑，再点「设为默认麦克风」。");
    }

    /// <summary>以管理员身份运行 WinUHid 安装脚本（UAC 提权）</summary>
    public static (bool ok, string msg) InstallWinuhid()
    {
        var cmd = FindAsset("Run-Install.cmd");
        if (cmd == null) return (false, "未找到 WinUHid 安装脚本 assets/winuhid/Run-Install.cmd。");
        try
        {
            Process.Start(new ProcessStartInfo(cmd)
            {
                UseShellExecute = true, Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(cmd)!,
            });
            return (true, "已启动 WinUHid 安装脚本（UAC 请点「是」）。窗口会逐行输出 Phase，出现 Result: OK 即成功；装完回来点「测试注入右 Alt」。");
        }
        catch (Exception ex) { return (false, $"启动安装脚本失败：{ex.Message}"); }
    }

    /// <summary>把内嵌 WinUHid 资源导出到桌面，供手动安装/排查</summary>
    public static (bool ok, string msg) ExportWinuhid()
    {
        var cmd = FindAsset("Run-Install.cmd");
        var dir = cmd != null ? Path.GetDirectoryName(cmd) : null;
        if (dir == null) return (false, "未找到内嵌 WinUHid 资源。");
        var desk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                                "IPRC1000 WinUHid 手动安装");
        try
        {
            Directory.CreateDirectory(desk);
            foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(dir, f);
                var dst = Path.Combine(desk, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(f, dst, true);
            }
            Process.Start(new ProcessStartInfo(desk) { UseShellExecute = true });
            return (true, "已导出到桌面「IPRC1000 WinUHid 手动安装」并打开文件夹，双击 Run-Install.cmd 安装。");
        }
        catch (Exception ex) { return (false, $"导出失败：{ex.Message}"); }
    }

    public static (bool ok, string msg) SetDefaultMic()
    {
        var script = FindAsset("configure-xiaomi-audio.ps1");
        if (script == null)
            return (false, "未找到 configure-xiaomi-audio.ps1；请在 Windows 设置里手动把默认录音设备设为 CABLE Output。");
        try
        {
            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script,
                                      "-Mode", "EnsureMic", "-AppPath", AppDir })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var outp = p.StandardOutput.ReadToEnd();
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit(90_000);
            var line = outp.Split('\n').Select(l => l.Trim())
                .FirstOrDefault(l => l.StartsWith("Result:", StringComparison.OrdinalIgnoreCase))?[7..].Trim() ?? "";
            if (line.Contains("OK", StringComparison.OrdinalIgnoreCase) || (line.Length == 0 && p.ExitCode == 0))
                return (true, "默认麦克风已设为 CABLE Output。");
            var detail = line.Length > 0 ? line : (err.Trim().Length > 0 ? err.Trim() : $"退出码 {p.ExitCode}");
            return (false, $"设置默认麦克风未完成：{detail}");
        }
        catch (Exception e) { return (false, $"调用脚本失败：{e.Message}"); }
    }

    public static (bool ok, string msg) RelaunchAdmin()
    {
        if (IsAdmin()) return (true, "当前已是管理员权限。");
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
            Process.Start(psi);
            return (true, "已请求提权，本窗口即将关闭。");
        }
        catch (Exception e) { return (false, $"提权失败：{e.Message}"); }
    }

    public static string Json(object o) => JsonSerializer.Serialize(o, JsonOpts);
    public static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    // ───────── 版本信息（唯一来源是 csproj 的 <Version> 与 BuildStamp）─────────

    /// <summary>程序版本，如 "1.0.1"。取自程序集元数据（csproj 的 &lt;Version&gt;）。</summary>
    public static string AppVersion { get; } = ReadVersion();

    /// <summary>构建时间，如 "2026-10-14 15:40"。来自 csproj 注入的 AssemblyMetadata。</summary>
    public static string BuildStamp { get; } = ReadBuildStamp();

    /// <summary>"v1.0.1（构建于 2026-10-14 15:40）" —— 界面与日志直接用这个</summary>
    public static string VersionLine => $"v{AppVersion}" +
        (BuildStamp.Length > 0 ? $"（构建于 {BuildStamp}）" : "");

    private static string ReadVersion()
    {
        try
        {
            var asm = typeof(Env).Assembly;
            // 优先用 InformationalVersion（不含 SourceRevisionId 的干净版本号）
            var info = asm.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
                       ?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
                return info.Split('+')[0].Trim();       // 去掉 +<git hash> 后缀
            return asm.GetName().Version?.ToString(3) ?? "0.0.0";
        }
        catch { return "0.0.0"; }
    }

    private static string ReadBuildStamp()
    {
        try
        {
            return typeof(Env).Assembly
                .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "BuildStamp")?.Value ?? "";
        }
        catch { return ""; }
    }

    /// <summary>
    /// 缩进版 JSON（给自检报告这类需要人读的场合）。
    ///
    /// ⚠ 必须用**静态实例 + JsonSerializer.Serialize**，不要用
    /// `jsonNode.ToJsonString(new JsonSerializerOptions{...})` ——
    /// 后者在单文件发布下会抛
    /// `InvalidOperationException: JsonSerializerOptions instance must specify a
    /// TypeInfoResolver setting before being marked as read-only.`
    /// （曾导致自检报告永远写不出来，只剩一个空报告）。
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptsPretty = new() { WriteIndented = true };
    public static string JsonPretty(object o) => JsonSerializer.Serialize(o, JsonOptsPretty);
}
