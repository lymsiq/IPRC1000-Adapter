using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace IprcSetup2
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                var extract = Array.IndexOf(args, "--extract");
                if (extract >= 0)
                {
                    if (extract + 1 >= args.Length) throw new ArgumentException("--extract 需要目标目录。");
                    using (var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"))
                        SetupCore.Extract(payload, Path.GetFullPath(args[extract + 1]), null);
                    return 0;
                }
                var defaultDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Product.DirectoryName);
                var selfDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if (String.Equals(Path.GetFileName(selfDirectory), Product.DirectoryName, StringComparison.OrdinalIgnoreCase)
                    && File.Exists(Path.Combine(selfDirectory, "package-manifest.json")))
                    defaultDirectory = selfDirectory;
                else if (File.Exists(Product.StatePath))
                {
                    var previous = SetupCore.LoadState(Product.StatePath);
                    if (!String.IsNullOrEmpty(previous.InstallDir)) defaultDirectory = previous.InstallDir;
                }
                var di = Array.IndexOf(args, "--dir");
                var target = di >= 0 && di + 1 < args.Length ? args[di + 1] : defaultDirectory;
                using (var form = new InstallForm(target, Array.IndexOf(args, "--resume") >= 0,
                    Array.IndexOf(args, "--uninstall") >= 0, Array.IndexOf(args, "--restore-registry") >= 0))
                    Application.Run(form);
                return 0;
            }
            catch (Exception error)
            {
                MessageBox.Show(error.Message, Product.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 1;
            }
        }
    }

    internal sealed class InstallForm : Form
    {
        private InstallEngine _engine;
        private string _target;
        private bool _running;
        private readonly TextBox _path = new TextBox(), _details = new TextBox();
        private readonly Button _browse = new Button(), _install = new Button(), _open = new Button();
        private readonly Label _phase = new Label(), _result = new Label();
        private readonly ProgressBar _progress = new ProgressBar();
        private readonly LinkLabel _detailsLink = new LinkLabel(), _logLink = new LinkLabel();
        // 2026-10-29：维护入口 —— 修复安装 / 复制诊断（复用驱动脚本已验证的 -Force 与 -Mode Status）
        private readonly LinkLabel _repairLink = new LinkLabel(), _diagLink = new LinkLabel();
        private readonly Panel _resultPanel = new Panel();
        private static readonly Color Ink = Color.FromArgb(27, 48, 76);
        private static readonly Color Muted = Color.FromArgb(94, 115, 137);
        private static readonly Color Blue = Color.FromArgb(35, 111, 225);

        internal InstallForm(string target, bool resume, bool uninstall, bool restore)
        {
            _target = target;
            Text = Product.DisplayName + " · 安装";
            ClientSize = new Size(824, 692);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Color.FromArgb(245, 249, 254);
            ForeColor = Ink;
            Font = new Font("Microsoft YaHei UI", 9.5F);
            DoubleBuffered = true;

            var header = new Panel { Bounds = new Rectangle(0, 0, 824, 145) };
            header.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var fill = new LinearGradientBrush(header.ClientRectangle, Color.FromArgb(230, 242, 255), Color.FromArgb(225, 251, 247), 12F))
                    e.Graphics.FillRectangle(fill, header.ClientRectangle);
                using (var glow = new SolidBrush(Color.FromArgb(78, Color.White)))
                { e.Graphics.FillEllipse(glow, 640, -88, 240, 240); e.Graphics.FillEllipse(glow, 573, 58, 104, 104); }
            };
            header.Controls.Add(Label(Product.DirectoryName.ToUpperInvariant(), 34, 22, 740, 19, 9F, Blue, true));
            header.Controls.Add(Label(Product.DisplayName, 31, 51, 762, 46, 23F, Ink, true));
            header.Controls.Add(Label("让灵感，从开口的一刻开始。", 34, 108, 725, 23, 10.5F, Muted));
            Controls.Add(header);

            var location = Card(30, 166, 764, 103);
            location.Controls.Add(Label("安装位置", 20, 14, 500, 23, 10F, Ink, true));
            _path.Bounds = new Rectangle(22, 48, 602, 28);
            _path.ReadOnly = true;
            _path.TabStop = false;
            _path.BorderStyle = BorderStyle.None;
            _path.BackColor = Color.White;
            _path.ForeColor = Muted;
            _path.Text = _target;
            _browse.Text = "更改位置";
            _browse.Bounds = new Rectangle(632, 41, 110, 37);
            StyleButton(_browse, false);
            _browse.Click += (s, e) => ChooseDirectory();
            location.Controls.AddRange(new Control[] { _path, _browse });
            Controls.Add(location);

            var help = Card(30, 283, 764, 180);
            help.Controls.Add(Label("搭配豆包输入法使用", 20, 15, 550, 25, 10.5F, Ink, true));
            help.Controls.Add(Label("请自行下载豆包输入法，设置长按右 Alt，麦克风选择 CABLE Output。", 20, 47, 720, 24, 9.5F, Muted));
            var doubao = new LinkLabel { Text = "下载豆包输入法  ↗", Bounds = new Rectangle(583, 17, 158, 24), LinkColor = Blue, ActiveLinkColor = Blue };
            doubao.LinkClicked += (s, e) => OpenLink(Product.DoubaoUrl);
            help.Controls.Add(doubao);
            help.Controls.Add(Label("先连接遥控器", 20, 85, 600, 24, 10.5F, Ink, true));
            help.Controls.Add(Label("① 按住「下 + LAST」至闪灯     ② Windows 蓝牙添加 IPRC1000", 20, 117, 720, 24, 9.5F, Muted));
            help.Controls.Add(Label("③ 配对后按一下 OK 唤醒，按住语音键即可开始说话。", 20, 144, 720, 24, 9.5F, Muted));
            Controls.Add(help);

            _install.Text = "立即安装";
            _install.Bounds = new Rectangle(30, 481, 764, 49);
            StyleButton(_install, true);
            _install.Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold);
            _install.Click += async (s, e) => await RunWork("install");
            Controls.Add(_install);
            _progress.Bounds = new Rectangle(31, 542, 762, 6);
            _progress.Visible = false;
            Controls.Add(_progress);

            _resultPanel.Bounds = new Rectangle(30, 558, 764, 103);
            _phase.Bounds = new Rectangle(0, 0, 580, 26);
            _phase.Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold);
            _result.Bounds = new Rectangle(0, 32, 764, 57);
            _result.ForeColor = Muted;
            _open.Text = "打开 IPRC1000";
            _open.Bounds = new Rectangle(615, 0, 149, 34);
            StyleButton(_open, false);
            _open.Visible = false;
            _open.Click += (s, e) => OpenApplication();
            _resultPanel.Controls.AddRange(new Control[] { _phase, _result, _open });
            Controls.Add(_resultPanel);

            // 维护入口：始终可见（装坏了也能自救）
            _repairLink.Text = "修复安装";
            _repairLink.Bounds = new Rectangle(330, 662, 90, 22);
            _repairLink.LinkColor = Muted;
            _repairLink.LinkClicked += async (s, e) => await RepairAsync();
            Controls.Add(_repairLink);
            _diagLink.Text = "复制诊断";
            _diagLink.Bounds = new Rectangle(432, 662, 90, 22);
            _diagLink.LinkColor = Muted;
            _diagLink.LinkClicked += (s, e) => CopyDiagnostics();
            Controls.Add(_diagLink);

            _detailsLink.Text = "展开故障详情";
            _detailsLink.Bounds = new Rectangle(30, 662, 150, 22);
            _detailsLink.LinkColor = Muted;
            _detailsLink.Visible = false;
            _detailsLink.LinkClicked += (s, e) => ToggleDetails();
            _logLink.Text = "查看安装日志";
            _logLink.Bounds = new Rectangle(174, 662, 150, 22);
            _logLink.LinkColor = Muted;
            _logLink.Visible = false;
            _logLink.LinkClicked += (s, e) => { if (_engine != null && File.Exists(_engine.LogPath)) OpenLink(_engine.LogPath); };
            _details.Bounds = new Rectangle(30, 696, 764, 119);
            _details.Multiline = true;
            _details.ReadOnly = true;
            _details.ScrollBars = ScrollBars.Vertical;
            _details.BackColor = Color.White;
            _details.Visible = false;
            Controls.AddRange(new Control[] { _detailsLink, _logLink, _details });
            Controls.Add(Label("Windows x64  ·  " + Product.Version, 520, 666, 273, 20, 8F, Color.FromArgb(135, 153, 172)));

            FormClosing += (s, e) => { if (_running) e.Cancel = true; };
            Shown += async (s, e) =>
            {
                if (uninstall || restore)
                {
                    var action = uninstall ? "卸载主程序，保留用户数据和共享组件？" : "恢复安装前的蓝牙配置？恢复后需要重启。";
                    if (MessageBox.Show(action, Product.DisplayName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                        await RunWork(uninstall ? "uninstall" : "restore");
                }
                else if (resume)
                {
                    await RunWork("resume");
                    if (_engine != null && _engine.State.ComponentsReady && _engine.State.DoubaoInstalled && !_engine.State.RestartRequired) OpenApplication();
                }
            };
        }

        private static Label Label(string text, int x, int y, int width, int height, float size, Color color, bool bold = false)
        { return new Label { Text = text, Bounds = new Rectangle(x, y, width, height), Font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular), ForeColor = color, BackColor = Color.Transparent }; }
        private static Panel Card(int x, int y, int width, int height)
        { return new Panel { Bounds = new Rectangle(x, y, width, height), BackColor = Color.White, Padding = new Padding(1) }; }
        private static void StyleButton(Button button, bool primary)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = primary ? 0 : 1;
            button.FlatAppearance.BorderColor = Color.FromArgb(212, 225, 239);
            button.BackColor = primary ? Blue : Color.FromArgb(245, 249, 255);
            button.ForeColor = primary ? Color.White : Blue;
            button.Cursor = Cursors.Hand;
        }
        private void ChooseDirectory()
        {
            using (var picker = new FolderBrowserDialog { Description = "选择安装所在的磁盘或文件夹，将自动创建 " + Product.DirectoryName + " 文件夹。", ShowNewFolderButton = true })
            {
                picker.SelectedPath = Path.GetDirectoryName(_target);
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                try { _target = SetupCore.InstallationDirectory(picker.SelectedPath); _path.Text = _target; }
                catch (Exception error) { _phase.Text = "请换一个安装位置"; _result.Text = error.Message; }
            }
        }
        private async Task RunWork(string operation)
        {
            if (_running) return;
            _running = true;
            _install.Enabled = _browse.Enabled = false;
            _open.Visible = _detailsLink.Visible = _logLink.Visible = _details.Visible = false;
            ClientSize = new Size(824, 692);
            _progress.Visible = true;
            _progress.Value = 0;
            _phase.ForeColor = Ink;
            _phase.Text = operation == "resume" ? "正在检查，一会儿就好" : "正在为你准备语音体验";
            _result.Text = "如出现系统或语音组件安装窗口，请按提示完成。";
            try
            {
                _target = SetupCore.ValidateInstallationDirectory(_target);
                _path.Text = _target;
                _engine = new InstallEngine(_target, OnProgress);
                var state = await Task.Run(() =>
                {
                    if (operation == "resume") return _engine.InspectAfterRestart();
                    if (operation == "uninstall") { _engine.Uninstall(); return _engine.State; }
                    if (operation == "restore") { _engine.RestoreRegistry(); return _engine.State; }
                    return _engine.Install(new InstallOptions());
                });
                var result = operation == "uninstall" || operation == "restore"
                    ? new[] { state.Phase, "操作已记录，可在需要时重新运行安装。" }
                    : SetupCore.InstallationResult(state);
                _phase.Text = result[0];
                _result.Text = result[1];
                _phase.ForeColor = state.ComponentsReady && state.DoubaoInstalled && !state.RestartRequired ? Color.FromArgb(17, 121, 103) : Ink;
                _details.Text = String.Join(Environment.NewLine, state.PendingActions.ToArray()) + "\r\n\r\n日志：" + _engine.LogPath;
                _detailsLink.Visible = !state.ComponentsReady && operation != "uninstall" && operation != "restore";
                _logLink.Visible = _detailsLink.Visible;
                _open.Visible = state.ComponentsReady && !state.RestartRequired && File.Exists(Path.Combine(_target, Product.ExeName));
                _install.Text = state.ComponentsReady ? "重新检查 / 修复" : "重新安装";
            }
            catch (Exception error)
            {
                _phase.Text = "这次还没装好，我们帮你继续";
                _phase.ForeColor = Color.FromArgb(155, 101, 39);
                _result.Text = "请检查网络连接，或完成弹出的安装窗口后重试。\n具体原因可在下方的故障详情中查看。";
                _details.Text = error.Message + "\r\n\r\n日志：" + (_engine == null ? "尚未开始安装" : _engine.LogPath);
                _detailsLink.Visible = true;
                _logLink.Visible = _engine != null && File.Exists(_engine.LogPath);
                _install.Text = "重试安装";
            }
            finally { _running = false; _install.Enabled = _browse.Enabled = true; }
        }
        /// <summary>修复安装：重建虚拟键盘驱动（-Force 会先清旧节点，这是"重装修不好"的解药）。</summary>
        private async Task RepairAsync()
        {
            if (_running) return;
            if (_engine == null) { _phase.Text = "请先完成一次安装"; return; }
            _running = true;
            _repairLink.Enabled = _diagLink.Enabled = _install.Enabled = _browse.Enabled = false;
            _phase.ForeColor = Ink;
            _phase.Text = "正在修复虚拟键盘驱动（会清空旧节点后重建）…";
            _result.Text = "期间请勿关闭本窗口。";
            try
            {
                var (ok, reboot, text) = await Task.Run(() => _engine.RepairWinUhid());
                _phase.Text = reboot ? "已修复，需要重启计算机后生效" : (ok ? "修复完成" : "修复未成功");
                _phase.ForeColor = ok ? Color.FromArgb(17, 121, 103) : Color.FromArgb(155, 101, 39);
                _result.Text = ok
                    ? "可以直接点「打开 IPRC1000」验证；若提示未就绪，请重启后再试。"
                    : "请点「复制诊断」把信息发给我们，便于定位。";
                _details.Text = text;
                _detailsLink.Visible = true;
                _logLink.Visible = _engine != null && File.Exists(_engine.LogPath);
            }
            catch (Exception error)
            {
                _phase.Text = "修复未完成";
                _phase.ForeColor = Color.FromArgb(155, 101, 39);
                _result.Text = error.Message;
                _details.Text = error.Message;
                _detailsLink.Visible = true;
            }
            finally
            {
                _running = false;
                _repairLink.Enabled = _diagLink.Enabled = _install.Enabled = _browse.Enabled = true;
            }
        }

        /// <summary>复制诊断：跑只读的 -Mode Status，复制到剪贴板并落盘到桌面。</summary>
        private void CopyDiagnostics()
        {
            if (_engine == null) { _phase.Text = "请先完成一次安装"; return; }
            try
            {
                var (ok, text, path) = _engine.CollectDiagnostics();
                var copied = false;
                try { Clipboard.SetText(text); copied = true; } catch { }
                _phase.ForeColor = ok ? Color.FromArgb(17, 121, 103) : Color.FromArgb(155, 101, 39);
                _phase.Text = copied ? "诊断信息已复制到剪贴板" : "诊断已生成（剪贴板不可用）";
                _result.Text = path.Length > 0
                    ? "同时已保存到桌面：" + Path.GetFileName(path) + "\n直接把内容发给我们即可。"
                    : "未能写入桌面文件，可从下方「展开故障详情」里复制。";
                _details.Text = text;
                _details.Visible = true;
                _detailsLink.Visible = true;
            }
            catch (Exception error)
            {
                _phase.Text = "诊断采集失败";
                _phase.ForeColor = Color.FromArgb(155, 101, 39);
                _result.Text = error.Message;
                _details.Text = error.Message;
                _detailsLink.Visible = true;
            }
        }

        private void OnProgress(int progress, string message)
        {
            if (IsDisposed) return;
            BeginInvoke((Action)(() =>
            {
                _progress.Value = Math.Max(0, Math.Min(100, progress));
                if (progress >= 100) return;
                _phase.Text = progress < 40 ? "正在安装 IPRC1000" : progress < 52 ? "正在连接语音能力" : progress < 80 ? "正在准备必要组件" : progress < 88 ? "请完成弹出的语音组件安装窗口" : "马上就好，正在完成最后检查";
            }));
        }
        private void ToggleDetails()
        {
            _details.Visible = !_details.Visible;
            _detailsLink.Text = _details.Visible ? "收起故障详情" : "展开故障详情";
            ClientSize = new Size(824, _details.Visible ? 833 : 692);
        }
        private void OpenLink(string path)
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception error) { _phase.Text = "暂时无法打开"; _result.Text = error.Message; }
        }
        private void OpenApplication()
        {
            try { Process.Start(new ProcessStartInfo(Path.Combine(_target, Product.ExeName)) { UseShellExecute = true, WorkingDirectory = _target }); }
            catch (Exception error) { _phase.Text = "暂时无法打开 IPRC1000"; _result.Text = error.Message; }
        }
    }
}
