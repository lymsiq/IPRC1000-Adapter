using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace IprcSetup2
{
    internal static class Product
    {
        internal const string DisplayName = "IPRC1000 遥控器适配器";
        internal const string DirectoryName = "IPRC1000_Adapter";
        internal const string ExeName = "IPRC1000 Adapter.exe";
        internal static string StateDirectory { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "IPRC1000Adapter"); } }
        internal static string StatePath { get { return Path.Combine(StateDirectory, "setup-state.json"); } }
        internal const string RegistryKey = @"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters";
        internal const string SharedBluetoothMarkerKey = @"SOFTWARE\IPRC1000VoiceHub\Bluetooth";
        internal const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\IPRC1000Adapter";
        internal const string DoubaoUrl = "https://shurufa.doubao.com/pc";
        internal const string CableUrl = "https://vb-audio.com/Cable/";
        internal static string Version
        {
            get
            {
                var attribute = (System.Reflection.AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(
                    typeof(Product).Assembly, typeof(System.Reflection.AssemblyInformationalVersionAttribute));
                return attribute == null ? typeof(Product).Assembly.GetName().Version.ToString() : attribute.InformationalVersion.Split('+')[0];
            }
        }
    }

    public sealed class ManifestFile
    {
        public string Path { get; set; }
        public string Sha256 { get; set; }
        public long Size { get; set; }
    }
    public sealed class PackageManifest
    {
        public string Product { get; set; }
        public string Version { get; set; }
        public bool SelfContained { get; set; }
        public List<ManifestFile> Files { get; set; }
    }
    public sealed class RegistryOriginal
    {
        public string Name { get; set; }
        public bool Exists { get; set; }
        public string Kind { get; set; }
        public string Value { get; set; }
        public string[] Strings { get; set; }
    }
    public sealed class InstallState
    {
        public int Schema { get; set; }
        public string Version { get; set; }
        public string InstallDir { get; set; }
        public string Phase { get; set; }
        public string RegistryAppliedUtc { get; set; }
        public string ComponentRebootRequestedUtc { get; set; }
        public bool RestartRequired { get; set; }
        public bool ComponentsReady { get; set; }
        public bool DoubaoInstalled { get; set; }
        public string WinUhidConsentUtc { get; set; }
        public string WinUhidCertificateThumbprint { get; set; }
        public List<RegistryOriginal> RegistryBackup { get; set; }
        public List<string> PendingActions { get; set; }
        public string LastCheckedUtc { get; set; }
        public InstallState()
        {
            Schema = 1;
            RegistryBackup = new List<RegistryOriginal>();
            PendingActions = new List<string>();
        }
    }

    internal static class SetupCore
    {
        internal static string InstallationDirectory(string parent)
        {
            if (String.IsNullOrWhiteSpace(parent) || !Regex.IsMatch(parent, @"^[A-Za-z]:[\\/]") || parent.Contains('"'))
                throw new ArgumentException("请选择此电脑上的安装文件夹。");
            var path = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar);
            if (!String.Equals(Path.GetFileName(path), Product.DirectoryName, StringComparison.OrdinalIgnoreCase))
                path = Path.Combine(path + (path.EndsWith(":") ? "\\" : ""), Product.DirectoryName);
            return ValidateInstallationDirectory(path);
        }

        internal static string ValidateInstallationDirectory(string target)
        {
            if (String.IsNullOrWhiteSpace(target) || !Regex.IsMatch(target, @"^[A-Za-z]:[\\/]") || target.Contains('"'))
                throw new ArgumentException("请选择此电脑上的安装文件夹。");
            var path = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar);
            if (!String.Equals(Path.GetFileName(path), Product.DirectoryName, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("安装文件必须保存在独立的 " + Product.DirectoryName + " 文件夹中。");
            for (var directory = new DirectoryInfo(path); directory != null; directory = directory.Parent)
                if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("请选择普通文件夹，不能安装到目录链接。");
            if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
            {
                var manifestPath = Path.Combine(path, "package-manifest.json");
                if (File.Exists(manifestPath))
                {
                    var manifest = new JavaScriptSerializer().Deserialize<PackageManifest>(File.ReadAllText(manifestPath));
                    if (manifest == null || manifest.Product != Product.DirectoryName)
                        throw new IOException("这个文件夹属于其它程序，请选择其它安装位置。");
                }
                else
                {
                    var previous = LoadState(Product.StatePath);
                    if (!String.Equals(previous.InstallDir, path, StringComparison.OrdinalIgnoreCase)
                        || !File.Exists(Path.Combine(path, "IPRC1000AdapterSetup.exe")))
                        throw new IOException("这个文件夹已有其它文件。请选择其它位置，现有文件不会被覆盖。");
                }
            }
            return path;
        }

        internal static string[] InstallationResult(InstallState state)
        {
            if (state.ComponentsReady && state.DoubaoInstalled)
                return state.RestartRequired
                    ? new[] { "安装已完成，重启后见", "请保存工作并重启，重启后即可开始 AI 语音输入体验。\n请确认豆包已设置长按右 Alt，麦克风选择 CABLE Output。" }
                    : new[] { "已准备好，开始你的语音体验", "打开 IPRC1000，唤醒遥控器，在输入框中按住语音键说话。\n豆包需设置长按右 Alt，麦克风选择 CABLE Output。" };
            if (state.ComponentsReady)
                return new[] { "主程序已装好，还差豆包输入法", "请自行下载并配置豆包输入法：长按右 Alt，麦克风选择 CABLE Output。" + (state.RestartRequired ? "\n完成后请保存工作并重启，我们会继续检查。" : "\n配置好后，即可打开 IPRC1000 测试。") };
            return state.RestartRequired
                ? new[] { "还需要重启后检查", "请保存工作并重启，我们会自动继续检查。\n部分语音组件尚未就绪；如仍有问题，请重新运行安装。" }
                : new[] { "还有一步需要完成", "部分语音组件尚未准备好，请完成弹出的安装窗口后重试。\n需要帮助时，可展开下方的故障详情。" };
        }

        internal static string SafeDestination(string root, string relative)
        {
            if (String.IsNullOrEmpty(relative) || Path.IsPathRooted(relative) || relative.Contains(':'))
                throw new InvalidDataException("安装包包含非法路径：" + relative);
            var parts = relative.Replace('\\', '/').Split('/');
            foreach (var part in parts)
            {
                if (part == ".." || part == "." || part.EndsWith(" ") || part.EndsWith("."))
                    throw new InvalidDataException("安装包包含非法路径：" + relative);
                if (part.Length == 0) continue;
                if (part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                    Regex.IsMatch(part.Split('.')[0], @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase))
                    throw new InvalidDataException("安装包包含保留文件名：" + relative);
            }
            var basePath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var destination = Path.GetFullPath(Path.Combine(basePath, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("安装包路径超出安装目录。");
            return destination;
        }

        internal static PackageManifest ReadManifest(ZipArchive zip)
        {
            var entry = zip.GetEntry("package-manifest.json");
            if (entry == null || entry.Length > 4 * 1024 * 1024) throw new InvalidDataException("安装包缺少完整性清单。");
            using (var reader = new StreamReader(entry.Open()))
            {
                var serializer = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
                var manifest = serializer.Deserialize<PackageManifest>(reader.ReadToEnd());
                if (manifest == null || manifest.Product != Product.DirectoryName || !manifest.SelfContained || manifest.Files == null)
                    throw new InvalidDataException("安装包类型不匹配，必须使用IPRC1000 遥控器适配器自包含包。");
                if (String.IsNullOrEmpty(manifest.Version)) throw new InvalidDataException("安装包没有版本信息。");
                return manifest;
            }
        }

        internal static string Hash(Stream stream)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
        internal static string HashFile(string path)
        {
            using (var input = File.OpenRead(path)) return Hash(input);
        }

        internal static void ValidateArchive(ZipArchive zip, string root, PackageManifest manifest)
        {
            if (zip.Entries.Count > 20000) throw new InvalidDataException("安装包文件数异常。");
            var expected = new Dictionary<string, ManifestFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in manifest.Files)
            {
                if (file.Path == null || file.Path.Contains('\\') || file.Path.Contains("//")) throw new InvalidDataException("清单路径必须使用规范的 / 分隔符。");
                SafeDestination(root, file.Path);
                if (expected.ContainsKey(file.Path)) throw new InvalidDataException("清单含重复路径。");
                expected.Add(file.Path, file);
            }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.Contains('\\') || entry.FullName.Contains("//")) throw new InvalidDataException("安装包路径不规范。");
                SafeDestination(root, entry.FullName);
                if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new InvalidDataException("安装包不得包含符号链接。");
                if (!seen.Add(entry.FullName)) throw new InvalidDataException("安装包含重复路径。");
                if (String.IsNullOrEmpty(entry.Name)) continue;
                total += entry.Length;
                if (entry.Length > 2L * 1024 * 1024 * 1024 || total > 4L * 1024 * 1024 * 1024)
                    throw new InvalidDataException("安装包解压体积异常。");
                if (entry.FullName == "package-manifest.json") continue;
                ManifestFile file;
                if (!expected.TryGetValue(entry.FullName, out file) || file.Size != entry.Length)
                    throw new InvalidDataException("安装包清单不完整：" + entry.FullName);
                using (var input = entry.Open())
                    if (!String.Equals(Hash(input), file.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("安装包文件校验失败：" + entry.FullName);
            }
            if (expected.Keys.Any(path => !seen.Contains(path))) throw new InvalidDataException("安装包文件缺失。");
            if (!expected.ContainsKey(Product.ExeName) || !expected.ContainsKey("assets/etw_consumer.exe"))
                throw new InvalidDataException("安装包缺少主程序或蓝牙采集组件。");
        }

        internal static PackageManifest Extract(Stream payload, string destination, Action<int, string> progress)
        {
            using (var zip = new ZipArchive(payload, ZipArchiveMode.Read, true))
            {
                var manifest = ReadManifest(zip);
                ValidateArchive(zip, destination, manifest);
                foreach (var entry in zip.Entries)
                {
                    for (var parent = new DirectoryInfo(Path.GetDirectoryName(SafeDestination(destination, entry.FullName))); parent != null; parent = parent.Parent)
                        if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                            throw new InvalidDataException("解压目标不能包含目录链接或重解析点。");
                }
                Directory.CreateDirectory(destination);
                var files = zip.Entries.Where(e => !String.IsNullOrEmpty(e.Name)).ToArray();
                for (var i = 0; i < files.Length; i++)
                {
                    var entry = files[i];
                    var path = SafeDestination(destination, entry.FullName);
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    entry.ExtractToFile(path, true);
                    if (progress != null) progress((i + 1) * 100 / files.Length, entry.FullName);
                }
                return manifest;
            }
        }

        internal static void SaveState(string path, InstallState state)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temp = path + ".new";
            File.WriteAllText(temp, new JavaScriptSerializer().Serialize(state), new System.Text.UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temp, path, path + ".bak");
            else File.Move(temp, path);
        }
        internal static InstallState LoadState(string path)
        {
            if (!File.Exists(path)) return new InstallState();
            var state = new JavaScriptSerializer().Deserialize<InstallState>(File.ReadAllText(path));
            if (state == null || state.Schema != 1) throw new InvalidDataException("安装状态文件不兼容，已停止以保护原配置。");
            if (state.RegistryBackup == null) state.RegistryBackup = new List<RegistryOriginal>();
            if (state.PendingActions == null) state.PendingActions = new List<string>();
            return state;
        }
    }
}
