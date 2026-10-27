// IPRC1000 遥控器适配器 —— 单文件安装程序（.NET Framework 4.8，Win10/11 自带）
//
// 做四件事：解压（内置免运行时版整包）→ 桌面快捷方式 → 注册卸载项 → 引导跑一键部署。
//
// 为什么用 net48 而不是 net8：安装程序**本身不能依赖 .NET 8**（那正是要装的东西）。
// Win10 1903+ / Win11 都自带 .NET Framework 4.8，所以这个 Setup.exe 用户端零依赖。
//
// 测试用参数：
//   --extract <dir>   只解压到指定目录（不建快捷方式、不写注册表、不跑部署）
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

namespace IprcSetup
{
    internal static class Program
    {
        private const string AppName = "IPRC1000 Adapter";
        private const string DisplayName = "IPRC1000 遥控器适配器";
        private const string Version = "1.2.79";
        private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\IPRC1000Adapter";

        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // ── 测试模式：只解压 ──
            var ei = Array.IndexOf(args, "--extract");
            if (ei >= 0 && ei + 1 < args.Length)
            {
                var dir = args[ei + 1];
                Directory.CreateDirectory(dir);
                Extract(dir, null);
                Console.WriteLine("extracted to " + dir);
                return;
            }

            // 目标目录：默认 Program Files；可用 --dir 指定（装到 D 盘 / 便携测试）
            var di = Array.IndexOf(args, "--dir");
            var target = (di >= 0 && di + 1 < args.Length && args[di + 1].Length > 0)
                ? Path.GetFullPath(args[di + 1])
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppName);

            // 用 --dir 指定目录 ⇒ 便携模式：只解压，不建快捷方式、不写注册表
            using (var f = new InstallForm(target, di >= 0))
            {
                Application.Run(f);
            }
        }

        /// <summary>把内置的 payload.zip 解到 dir（进度回调 0~1）。</summary>
        internal static void Extract(string dir, Action<double, string> progress)
        {
            var asm = Assembly.GetExecutingAssembly();
            using (var s = asm.GetManifestResourceStream("payload.zip"))
            {
                if (s == null) throw new InvalidOperationException("安装包损坏：找不到内置载荷 payload.zip");
                using (var zip = new ZipArchive(s, ZipArchiveMode.Read))
                {
                    var total = zip.Entries.Count;
                    var i = 0;
                    foreach (var e in zip.Entries)
                    {
                        i++;
                        var rel = e.FullName.Replace('/', Path.DirectorySeparatorChar);
                        var dest = Path.Combine(dir, rel);
                        if (string.IsNullOrEmpty(e.Name))           // 目录项
                        {
                            Directory.CreateDirectory(dest);
                        }
                        else
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(dest));
                            e.ExtractToFile(dest, true);
                        }
                        if (progress != null) progress((double)i / total, e.Name);
                    }
                }
            }
        }

        internal static void CreateShortcut(string installDir)
        {
            // 复用包内已调好的脚本：它会建带"以管理员身份运行"标志的快捷方式
            var cmd = Path.Combine(installDir, "创建桌面快捷方式.cmd");
            if (!File.Exists(cmd)) return;
            var psi = new ProcessStartInfo("cmd.exe", "/c \"\"" + cmd + "\"\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = installDir,
            };
            try { Process.Start(psi).WaitForExit(8000); } catch { }
        }

        internal static void WriteUninstall(string installDir)
        {
            var uninstall = Path.Combine(installDir, "卸载.cmd");
            var lines = new[]
            {
                "@echo off",
                "chcp 936 >nul",
                "echo 正在卸载 " + DisplayName + " ...",
                "taskkill /F /IM \"IPRC1000 Adapter.exe\" >nul 2>&1",
                "reg delete \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run\" /v IPRC1000Adapter /f >nul 2>&1",
                "del /q \"%USERPROFILE%\\Desktop\\" + DisplayName + ".lnk\" >nul 2>&1",
                "echo 提示：虚拟键盘驱动与虚拟声卡不会自动卸载，需要时请用包内的还原脚本。",
                "start \"\" cmd /c timeout /t 2 >nul & rmdir /s /q \"" + installDir + "\"",
            };
            File.WriteAllText(uninstall, string.Join("\r\n", lines), System.Text.Encoding.GetEncoding(936));

            try
            {
                using (var k = Registry.LocalMachine.CreateSubKey(UninstallKey))
                {
                    if (k == null) return;
                    k.SetValue("DisplayName", DisplayName + " v" + Version);
                    k.SetValue("DisplayVersion", Version);
                    k.SetValue("Publisher", "Voice VibeCoding");
                    k.SetValue("InstallLocation", installDir);
                    k.SetValue("DisplayIcon", Path.Combine(installDir, "IPRC1000 Adapter.exe"));
                    k.SetValue("UninstallString", "\"" + uninstall + "\"");
                    k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                }
            }
            catch { }
        }
    }

    /// <summary>一个极简安装窗口：进度条 + 状态文字 + 结束后的两个按钮。</summary>
    internal sealed class InstallForm : Form
    {
        private readonly string _dir;
        private readonly bool _portable;
        private readonly Label _status = new Label();
        private readonly ProgressBar _bar = new ProgressBar();
        private readonly Button _close = new Button();

        public InstallForm(string dir, bool portable)
        {
            _dir = dir;
            _portable = portable;
            Text = "安装 " + Program2.DisplayName;
            ClientSize = new Size(520, 170);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9f);

            var title = new Label
            {
                Text = "正在安装 " + Program2.DisplayName + " v" + Program2.Version + "（含 .NET 运行时，约 64 MB）",
                AutoSize = false, Bounds = new Rectangle(16, 14, 490, 22),
            };
            _status.Text = "准备中…";
            _status.AutoSize = false;
            _status.Bounds = new Rectangle(16, 42, 490, 20);
            _bar.Bounds = new Rectangle(16, 68, 488, 18);
            _bar.Minimum = 0; _bar.Maximum = 100;

            _close.Text = "关闭";
            _close.Bounds = new Rectangle(424, 118, 80, 30);
            _close.Enabled = false;
            _close.Click += (s, e) => Close();

            Controls.AddRange(new Control[] { title, _status, _bar, _close });
            Shown += (s, e) => Run();
        }

        private void Run()
        {
            try
            {
                if (Directory.Exists(_dir) && Directory.EnumerateFileSystemEntries(_dir).GetEnumerator().MoveNext())
                {
                    var r = MessageBox.Show(
                        "目标目录已存在：\n" + _dir + "\n\n继续会**覆盖**同名文件（设置与日志在 %APPDATA%，不受影响）。是否继续？",
                        "已安装", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (r != DialogResult.Yes) { Close(); return; }
                }
                Directory.CreateDirectory(_dir);

                _status.Text = "正在解压…";
                Program.Extract(_dir, (p, name) =>
                {
                    _bar.Value = Math.Min(99, (int)(p * 90));
                    if ((DateTime.Now.Millisecond % 7) == 0) _status.Text = "正在解压：" + name;
                    Application.DoEvents();
                });

                // ★ 顺序很重要：**先**写卸载信息（删不干净比没快捷方式严重得多），
                //   再做快捷方式 —— 后者要跑一个 .cmd，慢或被安全软件拦时不能拖累前面的步骤。
                _status.Text = "正在登记卸载信息…"; Application.DoEvents();
                Program.WriteUninstall(_dir);

                if (!_portable)
                {
                    _status.Text = "正在创建桌面快捷方式…"; Application.DoEvents();
                    Program.CreateShortcut(_dir);
                }

                _bar.Value = 100;
                _status.Text = "安装完成。是否现在执行环境部署（装驱动/声卡）？";
                var go = MessageBox.Show(
                    "文件已安装到：\n" + _dir + "\n\n" +
                    "是否**现在**执行一键部署？\n" +
                    "（会安装虚拟键盘驱动、切换遥控器驱动、安装虚拟声卡，需要几分钟，可能提示重启）",
                    "安装完成", MessageBoxButtons.YesNo, MessageBoxIcon.Information);

                if (go == DialogResult.Yes)
                {
                    var deploy = Path.Combine(_dir, "部署.cmd");
                    if (File.Exists(deploy))
                        Process.Start(new ProcessStartInfo(deploy) { UseShellExecute = true, WorkingDirectory = _dir });
                    _status.Text = "已启动部署脚本（按提示点 UAC）。可关闭本窗口。";
                }
                else
                {
                    _status.Text = _portable
                        ? "已装到指定目录（便携模式：未建快捷方式、未写注册表）。双击目录里的「部署.cmd」即可部署。"
                        : "已跳过部署。以后可双击安装目录里的「部署.cmd」或桌面快捷方式。";
                }
                _close.Enabled = true;
            }
            catch (Exception ex)
            {
                _status.Text = "安装失败：" + ex.Message;
                MessageBox.Show(ex.ToString(), "安装失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                _close.Enabled = true;
            }
        }
    }

    /// <summary>给窗体用的常量副本（避免窗体里再引用 Program 的私有成员）。</summary>
    internal static class Program2
    {
        public const string DisplayName = "IPRC1000 遥控器适配器";
        public const string Version = "1.2.79";
    }
}
