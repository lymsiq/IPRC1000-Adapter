using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace IprcSetup2
{
    internal sealed class InstallOptions
    {
        internal bool InstallWebView = true;
        internal bool InstallWinUhid = true;
        internal bool InstallCable = true;
        internal bool ResumeAfterLogin = true;
    }

    internal sealed class InstallEngine
    {
        internal readonly string StateDirectory = Product.StateDirectory;
        internal string StatePath { get { return Path.Combine(StateDirectory, "setup-state.json"); } }
        internal string LogPath { get; private set; }
        internal InstallState State { get; private set; }
        private readonly Action<int, string> _progress;
        private readonly string _target;
        private static readonly string[] RegistryNames = { "EtwDropLargeEvents", "EtwLogSensitiveData", "MaxEtwBytes" };
        private static readonly int[] RegistryValues = { 0, 1, 8192 };
        [DllImport("kernel32.dll")] private static extern ulong GetTickCount64();

        internal InstallEngine(string target, Action<int, string> progress)
        {
            _target = SetupCore.ValidateInstallationDirectory(target);
            _progress = progress;
            State = new InstallState();
        }
        /// <summary>日志是否已关闭（删日志目录之后不能再写）。</summary>
        private bool _logClosed;

        private void Report(int value, string message)
        {
            // ★★ 2026-10-29 自伤 bug：删完 %ProgramData% 目录后这里还会往 setup-*.log 追加，
            //   于是抛"未能找到路径…的一部分"，把**已经成功的卸载**报成失败。
            //   现在：日志写入永不抛异常；关日志后只报进度。
            try
            {
                if (!_logClosed && LogPath != null)
                    File.AppendAllText(LogPath, DateTime.UtcNow.ToString("o") + " " + message + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
            try { _progress?.Invoke(value, message); } catch { }
        }

        /// <summary>关闭日志（删日志目录前必须先调）。</summary>
        private void CloseLog() { _logClosed = true; }
        private void BeginLog(string operation)
        {
            Directory.CreateDirectory(StateDirectory);
            LogPath = Path.Combine(StateDirectory, "setup-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + ".log");
            State = SetupCore.LoadState(StatePath);
            if (!String.IsNullOrEmpty(State.InstallDir) && !String.Equals(Path.GetFullPath(State.InstallDir), _target, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("本机已有另一安装目录，请先使用原安装目录进行修复。");
            State.InstallDir = _target;
            State.Version = Product.Version;
            State.Phase = operation;
            State.PendingActions.Clear();
            State.ComponentsReady = false;
            State.DoubaoInstalled = false;
            Report(0, operation + " · " + Product.Version);
            SetupCore.SaveState(StatePath, State);
        }
        private void Save(string phase)
        {
            State.Phase = phase;
            State.LastCheckedUtc = DateTime.UtcNow.ToString("o");
            try { SetupCore.SaveState(StatePath, State); } catch { }   // 目录已删时不能抛
        }
        internal InstallState Install(InstallOptions options)
        {
            BeginLog("正在安装");
            try
            {
                if (!Environment.Is64BitOperatingSystem || Environment.OSVersion.Version.Major < 10)
                    throw new PlatformNotSupportedException("此安装包需要 Windows 10/11 x64。");
                EnsureAppClosed();
                InstallFiles();
                ApplyRegistry();
                Report(52, "正在检查界面运行时");
                if (!HasWebView2() && options.InstallWebView) InstallMicrosoftWebView();
                if (!HasWebView2()) State.PendingActions.Add("安装 Microsoft WebView2 运行时后重新运行安装器修复。");
                Report(64, "正在检查虚拟键盘驱动");
                if (!HasWinUhid() && options.InstallWinUhid) InstallWinUhid();
                if (!HasWinUhid()) State.PendingActions.Add("语音按键组件尚未就绪；请按提示重启后重新检查。");
                Report(76, "正在检查虚拟麦克风");
                if (!HasCable() && options.InstallCable) InstallCable();
                if (!HasCable()) State.PendingActions.Add("VB-CABLE 尚未就绪；从官方页面安装后重新运行修复。");
                State.DoubaoInstalled = HasDoubao();
                if (!State.DoubaoInstalled) State.PendingActions.Add("需要安装豆包输入法，启用长按右 Alt，并选择 CABLE Output 麦克风。");
                State.ComponentsReady = HasWebView2() && HasWinUhid() && HasCable();
                Report(88, "正在创建快捷方式与维护入口");
                RegisterApplication();
                State.RestartRequired = NeedsRestart();
                if (options.ResumeAfterLogin && State.RestartRequired) RegisterResumeTask();
                else RemoveResumeTask();
                if (State.RestartRequired) State.PendingActions.Insert(0, "请保存工作后重启 Windows；蓝牙采集配置在驱动加载时生效。");
                Save(State.RestartRequired ? "等待重启" : State.PendingActions.Count > 0 ? "需要完成组件配置" : "安装检查通过，待语音实测");
                Report(100, State.Phase);
                return State;
            }
            catch (Exception error)
            {
                State.PendingActions.Add(error.Message);
                Save("安装未完成");
                Report(100, "失败：" + error);
                throw;
            }
        }

        internal InstallState InspectAfterRestart()
        {
            BeginLog("重启后检查");
            if (!File.Exists(Path.Combine(_target, Product.ExeName))) State.PendingActions.Add("主程序文件缺失，请重新运行安装。");
            using (var key = Registry.LocalMachine.OpenSubKey(Product.RegistryKey, false))
                for (var i = 0; i < RegistryNames.Length; i++)
                    if (key == null || !Equals(key.GetValue(RegistryNames[i]), RegistryValues[i]))
                        State.PendingActions.Add("蓝牙配置缺失：" + RegistryNames[i] + "，请运行修复。");
            if (!HasWebView2()) State.PendingActions.Add("WebView2 运行时缺失。");
            if (!HasWinUhid()) State.PendingActions.Add("虚拟键盘驱动未就绪。");
            if (!HasCable()) State.PendingActions.Add("VB-CABLE 虚拟声卡未就绪。");
            State.ComponentsReady = State.PendingActions.Count == 0;
            State.DoubaoInstalled = HasDoubao();
            if (!State.DoubaoInstalled) State.PendingActions.Add("豆包输入法未安装。");
            State.RestartRequired = NeedsRestart();
            if (State.RestartRequired) State.PendingActions.Insert(0, "蓝牙配置写入后尚未重启 Windows。");
            Save(State.RestartRequired ? "等待重启" : State.PendingActions.Count > 0 ? "需要完成组件配置" : "安装检查通过，待语音实测");
            if (!State.RestartRequired) RemoveResumeTask();
            Report(100, State.Phase);
            return State;
        }

        /// <summary>
        /// **升级前请应用优雅退出**（2026-10-29）。
        ///
        /// 原则：**绝不强杀**。强杀会让系统蓝牙栈失去响应，只能重启电脑（同类项目记录过该事故）。
        /// 流程：Set 命名事件「请退出」→ 应用自己走正常退出（释放注入键、关蓝牙会话）→
        /// 最多等 10 秒；仍不退才提示用户手动从托盘退出。
        ///
        /// 旧版本应用没有这个监听 ⇒ OpenExisting 失败 ⇒ 直接落到"请手动退出"，行为与以前一致。
        /// </summary>
        private void EnsureAppClosed()
        {
            var processName = Path.GetFileNameWithoutExtension(Product.ExeName);
            var running = Process.GetProcessesByName(processName);
            if (running.Length == 0) return;
            foreach (var process in running) using (process) { }

            Report(2, "正在请已运行的程序退出…");
            var asked = false;
            try
            {
                using (var ev = EventWaitHandle.OpenExisting(@"Local\IPRC1000Adapter.ExitRequest"))
                {
                    ev.Set();
                    asked = true;
                }
            }
            catch { /* 应用不在运行、或旧版本没有该监听 */ }

            if (asked)
            {
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(300);
                    var still = Process.GetProcessesByName(processName);
                    foreach (var process in still) using (process) { }
                    if (still.Length == 0) return;
                }
            }

            throw new InvalidOperationException(
                "请先从托盘退出“" + Product.DisplayName + "”，再点击安装/修复。" +
                "安装器不会强制关闭正在运行的程序（强杀会让系统蓝牙栈失去响应，可能只能重启电脑）。");
        }
        private void InstallFiles()
        {
            Report(5, "校验安装包与文件完整性");
            var parent = Path.GetDirectoryName(_target);
            Directory.CreateDirectory(parent);
            var stage = Path.Combine(parent, ".IPRC1000Adapter-stage-" + Guid.NewGuid().ToString("N"));
            var backup = Path.Combine(StateDirectory, "file-backups", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
            var applied = new List<string>();
            var existed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"))
                {
                    if (payload == null) throw new InvalidDataException("安装包中没有内置载荷。");
                    var manifest = SetupCore.Extract(payload, stage, (p, name) => Report(5 + p * 25 / 100, "解压：" + name));
                    if (manifest.Version != Product.Version) throw new InvalidDataException("安装器与程序版本不一致。");
                }
                Directory.CreateDirectory(_target);
                foreach (var source in Directory.GetFiles(stage, "*", SearchOption.AllDirectories))
                {
                    var rel = source.Substring(stage.Length + 1);
                    var destination = SetupCore.SafeDestination(_target, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    if (File.Exists(destination))
                    {
                        var old = SetupCore.SafeDestination(backup, rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(old));
                        File.Copy(destination, old, false);
                        existed.Add(rel);
                    }
                    applied.Add(rel);
                    File.Copy(source, destination, true);
                }
                var maintenance = Path.Combine(_target, "IPRC1000AdapterSetup.exe");
                var sourceInstaller = Assembly.GetExecutingAssembly().Location;
                if (!String.Equals(Path.GetFullPath(sourceInstaller), Path.GetFullPath(maintenance), StringComparison.OrdinalIgnoreCase))
                    File.Copy(sourceInstaller, maintenance, true);
                Report(38, "程序文件已安装并校验");
            }
            catch
            {
                foreach (var rel in applied.AsEnumerable().Reverse())
                {
                    var destination = SetupCore.SafeDestination(_target, rel);
                    if (existed.Contains(rel)) File.Copy(SetupCore.SafeDestination(backup, rel), destination, true);
                    else if (File.Exists(destination)) File.Delete(destination);
                }
                throw;
            }
            finally
            {
                var safeParent = Path.GetFullPath(parent).TrimEnd('\\') + "\\";
                if (Path.GetFullPath(stage).StartsWith(safeParent, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(stage).StartsWith(".IPRC1000Adapter-stage-") && Directory.Exists(stage))
                    Directory.Delete(stage, true);
            }
        }

        private void ApplyRegistry()
        {
            Report(42, "保存原蓝牙配置并写入推荐值");
            using (var key = Registry.LocalMachine.CreateSubKey(Product.RegistryKey))
            {
                if (key == null) throw new UnauthorizedAccessException("无法写入蓝牙配置，请用管理员权限运行安装器。");
                if (State.RegistryBackup.Count == 0)
                {
                    foreach (var name in RegistryNames)
                    {
                        var exists = key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase);
                        var original = new RegistryOriginal { Name = name, Exists = exists };
                        if (exists)
                        {
                            original.Kind = key.GetValueKind(name).ToString();
                            var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                            if (value is string[]) original.Strings = (string[])value;
                            else if (value is byte[]) original.Value = Convert.ToBase64String((byte[])value);
                            else original.Value = Convert.ToString(value, CultureInfo.InvariantCulture);
                        }
                        State.RegistryBackup.Add(original);
                    }
                    Save("已备份蓝牙配置");
                }
                var changed = false;
                for (var i = 0; i < RegistryNames.Length; i++)
                {
                    var name = RegistryNames[i];
                    if (!Equals(key.GetValue(name), RegistryValues[i]) || key.GetValueKind(name) != RegistryValueKind.DWord)
                    {
                        key.SetValue(name, RegistryValues[i], RegistryValueKind.DWord);
                        changed = true;
                    }
                    if (!Equals(key.GetValue(name), RegistryValues[i])) throw new IOException("蓝牙配置写入验证失败：" + name);
                }
                key.Flush();
                if (changed)
                {
                    State.RegistryAppliedUtc = DateTime.UtcNow.ToString("o");
                    using (var marker = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\IPRC1000Adapter\Hub"))
                    {
                        marker.SetValue("BthPortConfiguredAtUtc", State.RegistryAppliedUtc, RegistryValueKind.String);
                        marker.Flush();
                    }
                    using (var shared = Registry.LocalMachine.CreateSubKey(Product.SharedBluetoothMarkerKey))
                    {
                        shared.SetValue("BthPortConfiguredAtUtc", State.RegistryAppliedUtc, RegistryValueKind.String);
                        shared.Flush();
                    }
                }
                State.RestartRequired = NeedsRestart();
                Save("蓝牙配置已验证");
            }
        }
        private bool NeedsRestart()
        {
            var boot = DateTime.UtcNow - TimeSpan.FromMilliseconds(GetTickCount64());
            DateTime configured;
            using (var shared = Registry.LocalMachine.OpenSubKey(Product.SharedBluetoothMarkerKey))
                if (shared != null && DateTime.TryParse(shared.GetValue("BthPortConfiguredAtUtc") as string, null, DateTimeStyles.RoundtripKind, out configured) && configured.ToUniversalTime() > boot) return true;
            using (var legacy = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\IPRC1000Adapter\Hub"))
                if (legacy != null && DateTime.TryParse(legacy.GetValue("BthPortConfiguredAtUtc") as string, null, DateTimeStyles.RoundtripKind, out configured) && configured.ToUniversalTime() > boot) return true;
            if (DateTime.TryParse(State.RegistryAppliedUtc, null, DateTimeStyles.RoundtripKind, out configured))
            {
                if (configured.ToUniversalTime() > boot) return true;
            }
            if (DateTime.TryParse(State.ComponentRebootRequestedUtc, null, DateTimeStyles.RoundtripKind, out configured) && configured.ToUniversalTime() > boot) return true;
            var flag = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"com.remote-bridge-hub.app\winuhid\reboot-required.flag");
            return File.Exists(flag) && File.GetLastWriteTimeUtc(flag) > boot;
        }
        internal void RestoreRegistry()
        {
            BeginLog("恢复原蓝牙配置");
            if (State.RegistryBackup.Count != 3) throw new InvalidOperationException("没有完整的原配置备份，未执行恢复。");
            using (var key = Registry.LocalMachine.CreateSubKey(Product.RegistryKey))
            {
                for (var i = 0; i < RegistryNames.Length; i++)
                {
                    var original = State.RegistryBackup.Single(x => x.Name == RegistryNames[i]);
                    if (!Equals(key.GetValue(original.Name), RegistryValues[i]))
                        throw new InvalidOperationException("配置在安装后已被其它程序修改，为避免覆盖用户改动，未恢复：" + original.Name);
                }
                foreach (var original in State.RegistryBackup)
                {
                    if (!original.Exists) { key.DeleteValue(original.Name, false); continue; }
                    var kind = (RegistryValueKind)Enum.Parse(typeof(RegistryValueKind), original.Kind);
                    object value = original.Value;
                    if (kind == RegistryValueKind.DWord) value = Int32.Parse(original.Value, CultureInfo.InvariantCulture);
                    else if (kind == RegistryValueKind.QWord) value = Int64.Parse(original.Value, CultureInfo.InvariantCulture);
                    else if (kind == RegistryValueKind.MultiString) value = original.Strings;
                    else if (kind == RegistryValueKind.Binary || kind == RegistryValueKind.None) value = Convert.FromBase64String(original.Value);
                    key.SetValue(original.Name, value, kind);
                }
                key.Flush();
            }
            State.RegistryAppliedUtc = DateTime.UtcNow.ToString("o");
            using (var marker = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\IPRC1000Adapter\Hub", true))
                if (marker != null) marker.DeleteValue("BthPortConfiguredAtUtc", false);
            using (var shared = Registry.LocalMachine.CreateSubKey(Product.SharedBluetoothMarkerKey))
            {
                shared.SetValue("BthPortConfiguredAtUtc", State.RegistryAppliedUtc, RegistryValueKind.String);
                shared.Flush();
            }
            State.RestartRequired = true;
            Save("原蓝牙配置已恢复，重启后生效");
            Report(100, State.Phase);
        }

        internal static bool HasWinUhid()
        {
            try { using (var stream = new FileStream(@"\\.\WinUHid", FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite)) return true; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
        internal static bool HasWebView2()
        {
            foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
                foreach (var root in new[] { @"SOFTWARE\Microsoft\EdgeUpdate\Clients", @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients" })
                    using (var key = hive.OpenSubKey(root))
                        if (key != null)
                            foreach (var sub in key.GetSubKeyNames())
                                using (var item = key.OpenSubKey(sub))
                                    if (item != null && Convert.ToString(item.GetValue("name")).IndexOf("WebView2", StringComparison.OrdinalIgnoreCase) >= 0 && !String.IsNullOrEmpty(Convert.ToString(item.GetValue("pv")))) return true;
            return false;
        }
        private static bool HasAudioEndpoint(string flow, string name)
        {
            using (var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\" + flow))
                if (root != null)
                    foreach (var id in root.GetSubKeyNames())
                        using (var endpoint = root.OpenSubKey(id))
                        using (var properties = root.OpenSubKey(id + @"\Properties"))
                        {
                            if (endpoint == null || properties == null || !Equals(endpoint.GetValue("DeviceState"), 1)) continue;
                            foreach (var value in properties.GetValueNames())
                                if (Convert.ToString(properties.GetValue(value)).IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                        }
            return false;
        }
        internal static bool HasCable() { return HasAudioEndpoint("Capture", "CABLE Output") && HasAudioEndpoint("Render", "CABLE Input"); }
        internal static bool HasDoubao()
        {
            foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
                foreach (var path in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
                    using (var root = hive.OpenSubKey(path))
                        if (root != null)
                            foreach (var sub in root.GetSubKeyNames())
                                using (var item = root.OpenSubKey(sub))
                                    if (item != null && (Convert.ToString(item.GetValue("DisplayName")).Contains("豆包输入法") || Convert.ToString(item.GetValue("InstallLocation")).IndexOf("DoubaoIME", StringComparison.OrdinalIgnoreCase) >= 0)) return true;
            return false;
        }

        /// <summary>运行并**捕获输出**（同时仍写入安装日志）。用于"修复/诊断"这类要把结果给用户看的操作。</summary>
        private (int code, string output) RunCapture(string file, string arguments)
        {
            var sb = new System.Text.StringBuilder();
            var start = new ProcessStartInfo(file, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = _target
            };
            using (var p = new Process { StartInfo = start })
            {
                p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(10 * 60 * 1000))
                    throw new TimeoutException("操作超时（10 分钟）。");
                p.WaitForExit();
                var text = sb.ToString();
                try { lock (this) File.AppendAllText(LogPath, text, Encoding.UTF8); } catch { }
                return (p.ExitCode, text);
            }
        }

        private static string Tail(string text, int lines)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var all = text.Replace("\r\n", "\n").Split('\n');
            var take = all.Skip(Math.Max(0, all.Length - lines)).Where(x => x.Trim().Length > 0);
            return string.Join("\n", take);
        }

        private string WinUhidScript()
        {
            var folder = Path.Combine(_target, "assets", "winuhid");
            var script = Path.Combine(folder, "install-winuhid.ps1");
            if (!File.Exists(script))
                throw new FileNotFoundException("安装目录不完整：找不到 assets\\winuhid\\install-winuhid.ps1");
            return script;
        }

        /// <summary>
        /// **修复安装**：复用驱动脚本的 -Force（清空所有 WinUHid 节点 → 重导证书 → 重建唯一节点）。
        /// 之所以能"修"，是因为脚本里先删节点再注册；不 -Force 时坏节点会让注册被跳过 ——
        /// 那正是"重装也修不好"的根因。
        /// </summary>
        public (bool ok, bool reboot, string text) RepairWinUhid()
        {
            var script = WinUhidScript();
            Report(66, "修复：重建虚拟键盘驱动（会清空旧节点）");
            var (code, output) = RunCapture("powershell.exe",
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + script + "\" -Mode InstallElevated -Force");
            var reboot = code == 3010;
            var ok = code == 0 || reboot;
            var head = ok
                ? (reboot ? "驱动已重建，需要重启后生效。" : "驱动已重建并通过检查。")
                : "修复未成功（退出码 " + code + "）。请点「复制诊断」把信息发给我们。";
            return (ok, reboot, head + (string.IsNullOrWhiteSpace(output) ? "" : "\n\n" + Tail(output, 10)));
        }

        /// <summary>
        /// **采集诊断**：跑只读的 -Mode Status，写成 txt 并返回文本（调用方负责复制到剪贴板）。
        /// </summary>
        public (bool ok, string text, string path) CollectDiagnostics()
        {
            var script = WinUhidScript();
            Report(66, "采集诊断信息（只读，不改系统）");
            var (code, output) = RunCapture("powershell.exe",
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + script + "\" -Mode Status");
            var body = new System.Text.StringBuilder();
            body.AppendLine("IPRC1000 Adapter — 诊断报告");
            body.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            body.AppendLine("安装目录: " + _target);
            body.AppendLine("Windows: " + Environment.OSVersion.VersionString);
            body.AppendLine("退出码: " + code);
            body.AppendLine(new string('-', 60));
            body.AppendLine(string.IsNullOrWhiteSpace(output) ? "(脚本无输出)" : output.Trim());
            var dir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var path = Path.Combine(dir, "IPRC1000-diag-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
            try { File.WriteAllText(path, body.ToString(), new UTF8Encoding(true)); } catch { path = ""; }
            return (code == 0, body.ToString(), path);
        }

        private int RunProcess(string file, string arguments, bool visible)
        {
            var start = new ProcessStartInfo(file, arguments) { UseShellExecute = false, CreateNoWindow = !visible, WorkingDirectory = Path.IsPathRooted(file) ? Path.GetDirectoryName(file) : _target };
            if (!visible) { start.RedirectStandardOutput = true; start.RedirectStandardError = true; }
            using (var process = new Process { StartInfo = start })
            {
                if (!visible)
                {
                    process.OutputDataReceived += (s, e) => { if (e.Data != null) lock (this) File.AppendAllText(LogPath, e.Data + Environment.NewLine, Encoding.UTF8); };
                    process.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (this) File.AppendAllText(LogPath, e.Data + Environment.NewLine, Encoding.UTF8); };
                }
                process.Start();
                if (!visible) { process.BeginOutputReadLine(); process.BeginErrorReadLine(); }
                if (!process.WaitForExit(visible ? 60 * 60 * 1000 : 15 * 60 * 1000))
                    throw new TimeoutException("组件安装或检查超时；该组件可能仍在运行，请查看其窗口与安装日志，完成或关闭后重试。");
                process.WaitForExit(); // Drain asynchronous redirected output.
                return process.ExitCode;
            }
        }
        private void Download(string url, string destination)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            using (var client = new TimeoutWebClient()) client.DownloadFile(new Uri(url), destination);
            if (new FileInfo(destination).Length == 0) throw new IOException("官方组件下载为空，请检查网络后重试。");
        }
        private void VerifySignature(string path, string publisherPattern)
        {
            var command = "$s=Get-AuthenticodeSignature -LiteralPath '" + path.Replace("'", "''") + "'; if($s.Status -ne 'Valid' -or $s.SignerCertificate.Subject -notmatch '" + publisherPattern.Replace("'", "''") + "'){Write-Error '官方组件签名验证失败';exit 1};exit 0";
            var code = RunProcess("powershell.exe", "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(command)), false);
            if (code != 0) throw new InvalidDataException("组件的 Authenticode 签名或发布者不符合预期，已停止运行。");
        }
        private void InstallMicrosoftWebView()
        {
            Report(53, "从微软下载并验证 WebView2 运行时");
            var path = Path.Combine(StateDirectory, "downloads", "MicrosoftEdgeWebView2Setup.exe");
            Download("https://go.microsoft.com/fwlink/p/?LinkId=2124703", path);
            VerifySignature(path, "Microsoft Corporation");
            var code = RunProcess(path, "/silent /install", false);
            if (code != 0 && code != 3010) throw new InvalidOperationException("WebView2 安装失败，退出码：" + code);
            if (code == 3010) State.ComponentRebootRequestedUtc = DateTime.UtcNow.ToString("o");
            if (!HasWebView2()) throw new InvalidOperationException("WebView2 安装器已退出，但运行时未通过检查。");
        }
        private void InstallWinUhid()
        {
            var folder = Path.Combine(_target, "assets", "winuhid");
            var script = Path.Combine(folder, "install-winuhid.ps1");
            var certPath = Path.Combine(folder, "WinUHidPublisher.cer");
            if (!File.Exists(certPath)) certPath = Path.Combine(folder, "driver", "WinUHidPublisher.cer");
            if (!File.Exists(script) || !File.Exists(certPath)) throw new FileNotFoundException("WinUHid 驱动包不完整。");
            using (var cert = new X509Certificate2(certPath))
            {
                State.WinUhidConsentUtc = DateTime.UtcNow.ToString("o");
                State.WinUhidCertificateThumbprint = cert.Thumbprint;
                Save("已记录立即安装操作包含必要语音驱动");
                Report(65, "安装已授权的 WinUHid 驱动；证书指纹 " + cert.Thumbprint);
            }
            var code = RunProcess("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + script + "\" -Mode InstallElevated", false);
            if (code != 0 && code != 3010) throw new InvalidOperationException("WinUHid 安装失败，退出码：" + code);
            if (code == 3010) State.ComponentRebootRequestedUtc = DateTime.UtcNow.ToString("o");
            if (!HasWinUhid() && !File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"com.remote-bridge-hub.app\winuhid\reboot-required.flag")))
                throw new InvalidOperationException("WinUHid 安装后设备仍不可用，请查看安装日志。");
        }
        private void InstallCable()
        {
            var download = Path.Combine(StateDirectory, "downloads", "VBCABLE_Driver_Pack45.zip");
            // ★ 2026-10-28：优先用**随包的官方原包**（我们发布包里自带 assets/VBCABLE_Driver_Pack45.zip）。
            //   原实现只会联网下载 ⇒ 内网/门店机器装不上；现在本地优先、缺失才联网。
            var bundled = Path.Combine(_target, "assets", "VBCABLE_Driver_Pack45.zip");
            if (File.Exists(bundled))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(download));
                File.Copy(bundled, download, true);
                Report(77, "使用随包的 VB-CABLE 原包（无需联网）");
            }
            else
            {
                Report(77, "从 VB-Audio 官方下载原始驱动包");
                Download("https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip", download);
            }
            var staging = Path.Combine(StateDirectory, "downloads", "VBCABLE-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            using (var zip = ZipFile.OpenRead(download))
            {
                if (zip.Entries.Count > 2000) throw new InvalidDataException("VB-CABLE 安装包文件数异常。");
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                long total = 0;
                foreach (var entry in zip.Entries)
                {
                    var target = SetupCore.SafeDestination(staging, entry.FullName);
                    if (!seen.Add(target) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                        throw new InvalidDataException("VB-CABLE 安装包包含重复路径或链接。");
                    if (String.IsNullOrEmpty(entry.Name)) continue;
                    total += entry.Length;
                    if (entry.Length > 128 * 1024 * 1024 || total > 512 * 1024 * 1024) throw new InvalidDataException("VB-CABLE 安装包内容异常。");
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    entry.ExtractToFile(target, true);
                }
            }
            var exe = Directory.GetFiles(staging, "VBCABLE_Setup_x64.exe", SearchOption.AllDirectories).Single();
            VerifySignature(exe, "BUREL VINCENT|Vincent Burel|VB-Audio");
            Report(80, "请在 VB-Audio 官方窗口完成 Install Driver，并关闭该窗口");
            var code = RunProcess(exe, "", true);
            if (code != 0 && code != 3010) throw new InvalidOperationException("VB-CABLE 安装器返回错误：" + code);
            if (code == 3010) State.ComponentRebootRequestedUtc = DateTime.UtcNow.ToString("o");
            if (!HasCable()) State.PendingActions.Add("VB-CABLE 安装器已退出，但音频端点未就绪；按官方提示重启后再检查。");
        }

        private void RegisterApplication()
        {
            var exe = Path.Combine(_target, Product.ExeName);
            var maintenance = Path.Combine(_target, "IPRC1000AdapterSetup.exe");
            using (var key = Registry.LocalMachine.CreateSubKey(Product.UninstallKey))
            {
                key.SetValue("DisplayName", Product.DisplayName);
                key.SetValue("DisplayVersion", Product.Version);
                key.SetValue("Publisher", "IPRC1000_Adapter contributors");
                key.SetValue("InstallLocation", _target);
                key.SetValue("DisplayIcon", exe);
                // ★★ 2026-10-28 修：原来只写 `--uninstall`，不带目录。
                //   卸载器于是按**默认目录**（%ProgramFiles%\…）找清单，而用户可能装在 D:// / F://，
                //   找不到 package-manifest.json 就抛异常 ⇒ 用户点"卸载"完全没反应（日志只留一行）。
                var dirArg = " --dir \"" + _target + "\"";
                key.SetValue("UninstallString", "\"" + maintenance + "\" --uninstall" + dirArg);
                key.SetValue("ModifyPath", "\"" + maintenance + "\" --resume" + dirArg);
                key.SetValue("NoRepair", 0, RegistryValueKind.DWord);
            }
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
            dynamic shortcut = shell.CreateShortcut(Path.Combine(desktop, Product.DisplayName + ".lnk"));
            shortcut.TargetPath = exe;
            shortcut.WorkingDirectory = _target;
            shortcut.Description = Product.DisplayName;
            shortcut.IconLocation = exe + ",0";
            shortcut.Save();
        }
        private static string ResumeTaskName()
        {
            return "IPRC1000Adapter.Resume." + WindowsIdentity.GetCurrent().User.Value;
        }
        private void RegisterResumeTask()
        {
            dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
            service.Connect();
            dynamic task = service.NewTask(0);
            task.RegistrationInfo.Description = "在安装账户下次登录时检查IPRC1000 遥控器适配器；检查后自动移除。";
            task.Principal.UserId = WindowsIdentity.GetCurrent().User.Value;
            task.Principal.LogonType = 3;
            task.Principal.RunLevel = 1;
            task.Settings.Enabled = true;
            task.Settings.AllowDemandStart = true;
            task.Settings.DisallowStartIfOnBatteries = false;
            task.Settings.StopIfGoingOnBatteries = false;
            dynamic trigger = task.Triggers.Create(9);
            trigger.UserId = WindowsIdentity.GetCurrent().User.Value;
            trigger.Delay = "PT10S";
            dynamic action = task.Actions.Create(0);
            action.Path = Path.Combine(_target, "IPRC1000AdapterSetup.exe");
            action.Arguments = "--resume";
            action.WorkingDirectory = _target;
            service.GetFolder("\\").RegisterTaskDefinition(ResumeTaskName(), task, 6, null, null, 3, null);
            Report(94, "已安排安装账户下次登录后继续检查，不会自动重启电脑");
        }
        private void RemoveResumeTask()
        {
            dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
            service.Connect();
            dynamic folder = service.GetFolder("\\");
            foreach (dynamic task in folder.GetTasks(0))
                if (String.Equals((string)task.Name, ResumeTaskName(), StringComparison.OrdinalIgnoreCase)) { folder.DeleteTask(ResumeTaskName(), 0); break; }
        }
        /// <summary>
        /// 卸载前停掉语音桥子进程（2026-10-29）。
        /// 桥是我们自己 spawn 的，杀掉**没有**蓝牙栈风险（风险在于强杀主程序）；
        /// 但不停它，`assets\etw_consumer.exe` 被占用 ⇒ 删不掉 ⇒ 用户觉得"卸载不了"。
        /// </summary>
        private static void StopBridgeProcess()
        {
            foreach (var p in Process.GetProcessesByName("etw_consumer"))
            {
                using (p)
                {
                    try { p.Kill(); p.WaitForExit(3000); } catch { }
                }
            }
        }

        /// <summary>删掉我们写进蓝牙配置键的 3 个 ETW 值（只删自己的，不动别人的）。</summary>
        private void RemoveEtwValues()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(Product.RegistryKey, true))
                {
                    if (key == null) return;
                    foreach (var name in RegistryNames)
                    {
                        try { key.DeleteValue(name, false); } catch { }
                    }
                }
                Report(90, "已清除 ETW 相关注册表值");
            }
            catch (Exception e) { Report(90, "清除注册表值失败（可忽略）：" + e.Message); }
        }

        /// <summary>删掉登录自启项（HKCU\...\Run 下名字含 IPRC1000 的值）。</summary>
        private void RemoveStartupEntry()
        {
            try
            {
                using (var run = Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (run == null) return;
                    foreach (var name in run.GetValueNames())
                    {
                        if (name.IndexOf("IPRC1000", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        try { run.DeleteValue(name, false); Report(91, "已删除自启项：" + name); } catch { }
                    }
                }
            }
            catch { }
        }

        /// <summary>尽力卸载 VB-CABLE（用官方安装器的 -u）。找不到或失败都只提示，不阻断卸载。</summary>
        private void UninstallCableBestEffort()
        {
            try
            {
                var baseDir = Path.Combine(StateDirectory, "downloads");
                if (!Directory.Exists(baseDir)) { Report(85, "未找到 VB-CABLE 安装包，跳过声卡卸载"); return; }
                var setup = Directory.GetFiles(baseDir, "VBCABLE_Setup_x64.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (setup == null) { Report(85, "未找到 VB-CABLE 卸载程序，请在设备管理器里手动卸载"); return; }
                Report(85, "正在卸载虚拟声卡 VB-CABLE（官方卸载器）…");
                var code = RunProcess(setup, "-u", false);
                Report(87, $"VB-CABLE 卸载返回 {code}（可能需要重启才完全移除）");
                State.RestartRequired = true;
            }
            catch (Exception e) { Report(87, "卸载 VB-CABLE 失败：" + e.Message); }
        }

        /// <summary>删除本机用户配置（%APPDATA%）与安装状态目录（%ProgramData%），尽力而为。</summary>
        private void RemoveUserDataBestEffort()
        {
            var dirs = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IPRC1000 Adapter"),
                StateDirectory
            };
            foreach (var dir in dirs)
            {
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    // 先删文件（日志可能被占用 → 单个失败不影响其他）
                    foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                    {
                        try { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); } catch { }
                    }
                    try { Directory.Delete(dir, true); } catch { }
                    Report(93, "已清除：" + dir);
                }
                catch (Exception e) { Report(93, "清除失败（可手动删除）：" + dir + " —— " + e.Message); }
            }
        }

        /// <summary>
        /// **恢复设备驱动**（2026-10-29）：让应用把遥控器 HID 节点改回 keyboard.inf。
        ///
        /// 为什么必须做、且必须在卸 WinUHid 之前：否则用户拿遥控器当键盘用时它就是"不认"的状态；
        /// 次序反了（先卸 WinUHid）更糟 —— 会留下"键盘 + 无注入"⇒ 幽灵 m 全量回归。
        /// 由应用执行，是因为 newdev.dll + HWID 那套逻辑已在 Env.SetDeviceDriver 验证过。
        /// </summary>
        private void TryRestoreDeviceDriver()
        {
            try
            {
                var exe = Path.Combine(_target, Product.ExeName);
                if (!File.Exists(exe)) { Report(84, "主程序不在，跳过设备驱动恢复"); return; }
                Report(84, "正在把遥控器驱动恢复为键盘…");
                var code = RunProcess(exe, "--restore-device", false);
                Report(84, "设备驱动恢复返回 " + code + (code == 0 ? "（已恢复）" : "（可能未连接遥控器，可忽略）"));
            }
            catch (Exception e) { Report(84, "恢复设备驱动失败：" + e.Message); }
        }

        /// <summary>
        /// **卸载 WinUHid 驱动 + 设备节点 + 自签名证书**（2026-10-29）。
        /// 必须趁安装目录还在时调用（脚本就在 assets\winuhid 里）；删完文件就跑不了了。
        /// </summary>
        private void RemoveWinUHid()
        {
            try
            {
                var script = Path.Combine(_target, "assets", "winuhid", "install-winuhid.ps1");
                if (!File.Exists(script)) { Report(88, "找不到驱动卸载脚本，已跳过（可手动清理）"); return; }
                Report(88, "正在卸载虚拟键盘驱动与证书…");
                var shell = Environment.GetEnvironmentVariable("SystemRoot") + "\\System32\\WindowsPowerShell\\v1.0\\powershell.exe";
                var code = RunProcess(shell,
                    "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + script + "\" -Mode Remove", false);
                Report(89, "驱动/证书清理返回 " + code + (code == 0 ? "" : "（详见日志）"));
                State.RestartRequired = true;
            }
            catch (Exception e) { Report(89, "卸载驱动失败：" + e.Message); }
        }

        internal void Uninstall(bool removeCable = false, bool removeUserData = false)
        {
            BeginLog("卸载主程序");
            // ★★ 2026-10-28 修：原来这里没有 try/catch —— 任何一步抛异常都会直接冒出去，
            //   日志只留下开头一行（实测 68 字节），用户完全看不到原因。
            try
            {
                EnsureAppClosed();
                StopBridgeProcess();
                // 次序很关键：先把遥控器驱动改回键盘，再卸 WinUHid
                // （脚本在安装目录里，必须在删文件之前跑）
                TryRestoreDeviceDriver();
                RemoveWinUHid();
                var manifestPath = Path.Combine(_target, "package-manifest.json");
                if (!File.Exists(manifestPath))
                {
                    // 程序文件或清单已经不在了（被手动删除 / 上次卸载中断）。
                    // 此时**不能抛异常**：那只会让"卸载"点了没反应。
                    // 兜底把卸载项、快捷方式、续装任务清掉，让系统回到"未安装"。
                    CleanResidual("程序文件或安装清单已不存在");
                    return;
                }
                var manifest = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<PackageManifest>(File.ReadAllText(manifestPath));
                if (manifest == null || manifest.Product != Product.DirectoryName || manifest.Files == null)
                    throw new InvalidDataException("安装清单无效，已停止卸载。");
                // ★ 2026-10-29：原来任何一个文件删不掉就抛异常 → 整个卸载中断，
                //   用户看到"卸载失败"却不知道是哪几个文件、为什么。现在**逐条删、逐条记**，
                //   最后一次性列出失败的（通常是被占用的 exe/dll）。
                var failed = new List<string>();
                var removed = 0;
                foreach (var file in manifest.Files)
                {
                    try
                    {
                        var path = SetupCore.SafeDestination(_target, file.Path);
                        if (!File.Exists(path)) continue;
                        File.Delete(path);
                        removed++;
                    }
                    catch (Exception e) { failed.Add(file.Path + "（" + e.GetType().Name + "）"); }
                }
                if (failed.Count > 0)
                    throw new IOException($"还有 {failed.Count} 个文件删不掉（可能仍在运行）：\n· "
                        + string.Join("\n· ", failed.Take(6))
                        + "\n\n请先退出程序（托盘右键 → 退出）后重试。");
                File.Delete(manifestPath);
                RemoveEtwValues();          // 我们写进蓝牙配置键的 ETW 值
                RemoveStartupEntry();       // 登录自启
                if (removeCable) UninstallCableBestEffort();
                var shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), Product.DisplayName + ".lnk");
                if (File.Exists(shortcut)) File.Delete(shortcut);
                Registry.LocalMachine.DeleteSubKeyTree(Product.UninstallKey, false);
                RemoveResumeTask();
                var extras = new List<string>();
                if (removeCable) extras.Add("虚拟声卡");
                if (removeUserData) extras.Add("本机配置");
                Save($"主程序已卸载（删除 {removed} 个文件"
                     + (extras.Count > 0 ? "；另清理：" + string.Join("、", extras) : "；用户数据已保留")
                     + "）");

                // ★ 删用户目录必须放在**最后**，而且要先关日志 ——
                //   否则后面任何一次 Report() 都会因为日志文件所在目录没了而抛异常。
                if (removeUserData)
                {
                    CloseLog();
                    RemoveUserDataBestEffort();
                }
                Report(100, State.Phase);
            }
            catch (Exception error)
            {
                // 失败也要**留痕**：写进日志与状态，否则用户只知道"点了没用"
                State.PendingActions.Add(error.Message);
                Save("卸载未完成：" + error.Message);
                Report(100, "失败：" + error.Message);
                throw;
            }
        }

        /// <summary>清理残留安装记录（卸载项 / 快捷方式 / 续装任务 / 空目录）。</summary>
        private void CleanResidual(string reason)
        {
            var shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), Product.DisplayName + ".lnk");
            if (File.Exists(shortcut)) { try { File.Delete(shortcut); } catch { } }
            try { Registry.LocalMachine.DeleteSubKeyTree(Product.UninstallKey, false); } catch { }
            try { RemoveResumeTask(); } catch { }
            try
            {
                // 只删**空目录**：目录里还有用户文件时绝不动
                if (Directory.Exists(_target) && !Directory.EnumerateFileSystemEntries(_target).Any())
                    Directory.Delete(_target);
            }
            catch { }
            Save("已清除残留安装记录（" + reason + "）");
            Report(100, State.Phase);
        }

        private sealed class TimeoutWebClient : WebClient
        {
            protected override WebRequest GetWebRequest(Uri address)
            {
                var request = base.GetWebRequest(address);
                request.Timeout = 120000;
                var http = request as HttpWebRequest;
                if (http != null) http.ReadWriteTimeout = 120000;
                return request;
            }
        }
    }
}
