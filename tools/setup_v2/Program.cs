using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;
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
                // ── 静默安装（企业批量部署用）：`/S` 或 `--silent` ──────────────
                //   无界面、无 UAC（清单已要求提权）、退出码：0 成功 / 3010 需重启 / 1 失败。
                if (Array.IndexOf(args, "--silent") >= 0 || Array.IndexOf(args, "/S") >= 0)
                {
                    try
                    {
                        var engine = new InstallEngine(target, null);
                        // 静默卸载：与安装同一入口，便于批量/脚本化排查
                        if (Array.IndexOf(args, "--uninstall") >= 0)
                        {
                            engine.Uninstall();
                            Console.WriteLine("[silent] 已卸载：" + target);
                            return 0;
                        }
                        var st = engine.Install(new InstallOptions());
                        Console.WriteLine("[silent] 安装目录：" + st.InstallDir);
                        Console.WriteLine("[silent] 版本：" + st.Version + " 组件就绪：" + st.ComponentsReady);
                        Console.WriteLine("[silent] 日志：" + engine.LogPath);
                        return st.RestartRequired ? 3010 : (st.ComponentsReady ? 0 : 1);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine("[silent] 失败：" + e.Message);
                        return 1;
                    }
                }

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
        // 2026-10-29：安装时直接选输入法，装完就把「语音键」映射写好（省掉用户进应用再点一次）
        private readonly RadioButton _imeDoubao = new RadioButton(), _imeWechat = new RadioButton();

        // 卸载选项（2026-10-29）：默认全勾（自己人用，倾向"卸干净"）
        private readonly CheckBox _unCable = new CheckBox(), _unData = new CheckBox();
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
            // ★ 2026-10-29 换机风险：窗口固定 824×692 且 AutoScaleMode.Dpi，
            //   在 **1366×768 + 125%** 的笔记本上会超出屏幕、底部按钮点不到。
            //   这里按工作区收敛，并开滚动，保证任何屏幕上都能操作到"立即安装"。
            Load += (_, _) => ClampToWorkingArea();
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
            // 升级还是全新安装，一眼可见（2026-10-29）
            var installedVersion = "";
            try
            {
                var existingExe = Path.Combine(_target, "IPRC1000 Adapter.exe");
                if (File.Exists(existingExe))
                {
                    var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(existingExe);
                    // 优先 ProductVersion（带发布号）；FileVersion 在老包里可能是 1.2.0.0
                    installedVersion = (vi.ProductVersion ?? "").Split('+')[0].Trim();
                    if (string.IsNullOrEmpty(installedVersion)) installedVersion = vi.FileVersion ?? "";
                }
            }
            catch { }
            var versionHint = string.IsNullOrEmpty(installedVersion)
                ? $"全新安装 · 目标版本 {Product.Version}"
                : (installedVersion.StartsWith(Product.Version)
                    ? $"已安装 {installedVersion}（同版本，将覆盖修复）"
                    : $"检测到已安装 {installedVersion} → 将升级到 {Product.Version}");
            location.Controls.Add(Label(versionHint, 20, 78, 720, 20, 9F, Muted));
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

            var help = Card(30, 283, 764, 128);
            // ★ 2026-10-29 修：标题 Label 宽 560 会**盖住**右侧链接（Control 先加=在上层），
            //   于是"下载豆包"整个被遮住，只剩微信那个露在外面。现在标题只占 420，链接排在它右边。
            help.Controls.Add(Label("搭配语音输入法使用（豆包 / 微信输入法，任选其一）", 20, 15, 424, 25, 10.5F, Ink, true));
            var doubao = new LinkLabel { Text = "下载豆包  ↗", Bounds = new Rectangle(448, 17, 84, 24), LinkColor = Blue, ActiveLinkColor = Blue };
            doubao.LinkClicked += (s, e) => OpenLink(Product.DoubaoUrl);
            help.Controls.Add(doubao);
            var weixin = new LinkLabel { Text = "下载微信输入法  ↗", Bounds = new Rectangle(536, 17, 132, 24), LinkColor = Blue, ActiveLinkColor = Blue };
            weixin.LinkClicked += (s, e) => OpenLink(Product.WeixinImeUrl);
            help.Controls.Add(weixin);
            help.Controls.Add(Label("豆包长按右 Alt；微信输入法长按 Ctrl+Win。两者都要把「麦克风」选成 CABLE Output。", 20, 47, 730, 24, 9.5F, Muted));
            help.Controls.Add(Label("装好后在应用「环境设置」里选中你用的那个，语音键会自动改成对应组合键。", 20, 71, 730, 22, 9F, Muted));
            help.Controls.Add(Label("先连接遥控器：按住「下 + LAST」至闪灯 → Windows 蓝牙添加 IPRC1000", 20, 99, 730, 22, 9.5F, Muted));
            Controls.Add(help);

            // 语音输入法：装的时候直接选，装完即写好映射
            var imeCard = Card(30, 423, 764, 50);
            imeCard.Controls.Add(Label("语音输入法", 20, 13, 90, 22, 10F, Ink, true));
            _imeDoubao.Text = "豆包（长按右 Alt）";
            _imeDoubao.Bounds = new Rectangle(112, 11, 170, 24);
            _imeDoubao.Font = new Font("Microsoft YaHei UI", 9.5F);
            _imeDoubao.Checked = true;
            _imeWechat.Text = "微信输入法（长按 Ctrl+Win）";
            _imeWechat.Bounds = new Rectangle(292, 11, 240, 24);
            _imeWechat.Font = new Font("Microsoft YaHei UI", 9.5F);
            imeCard.Controls.Add(_imeDoubao);
            imeCard.Controls.Add(_imeWechat);
            imeCard.Controls.Add(Label("装完会自动把遥控器「语音键」映射成对应组合键；以后也能在应用里改。", 20, 30, 730, 18, 8.5F, Muted));
            Controls.Add(imeCard);

            // ★★ 2026-10-29 真机问题：`--uninstall` 时**页面还是安装页**（按钮写着「立即安装」），
            //   只靠一个 MessageBox 确认；用户点「否」（还被设成默认）就什么都不发生，
            //   点「立即安装」则变成**重装** ⇒ 表现就是"卸载点了没用、文件还在"。
            //   现在卸载/恢复模式**把页面本身改成确认页**，确认动作就是这个醒目按钮。
            var isUninstall = uninstall;
            var isRestore = restore;
            _install.Text = isUninstall ? "确认卸载" : isRestore ? "确认恢复配置" : "立即安装";
            _install.Bounds = new Rectangle(30, 481, 764, 49);
            StyleButton(_install, true);
            _install.Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold);
            _install.Click += async (s, e) =>
                await RunWork(isUninstall ? "uninstall" : isRestore ? "restore" : "install");

            if (isUninstall || isRestore)
            {
                // 卸载页不需要"装到哪""选输入法""搭配输入法"，只留一个明确的确认动作
                location.Visible = false;
                imeCard.Visible = false;
                help.Visible = false;
                Text = Product.DisplayName + (isUninstall ? " · 卸载" : " · 恢复配置");
                // 三张卡都隐藏后中间会空出一大块 —— 把主按钮与说明重排成"确认页"版式，
                // 否则用户看到的是"上面一块空白 + 下面一段字"，像页面没加载完。
                // ★ 必须在 Load 里重排：_phase / _result 是**本段之后**才创建的，
                //   在这里直接设 Bounds 会被它们自己的创建代码覆盖（实测：文字仍在底部）。
                // 两个清理选项：默认勾选（配置可先导出）
                _unCable.Text = "同时卸载虚拟声卡 VB-CABLE";
                _unCable.Bounds = new Rectangle(32, 494, 500, 24);
                _unCable.Font = new Font("Microsoft YaHei UI", 9.5F);
                _unCable.Checked = true;
                _unData.Text = "同时删除本机设置与按键映射（删除后需重新配置）";
                _unData.Bounds = new Rectangle(32, 520, 560, 24);
                _unData.Font = new Font("Microsoft YaHei UI", 9.5F);
                _unData.Checked = true;
                Controls.Add(_unCable);
                Controls.Add(_unData);
                Controls.Add(Label("想保留配置？先在应用「环境设置」点「导出配置」，文件会存到桌面。",
                    56, 546, 720, 20, 8.5F, Muted));

                Load += (_, _) =>
                {
                    _install.Bounds = new Rectangle(30, 210, 764, 56);
                    // ★ 说明文字在 _resultPanel 容器里，而**容器默认隐藏**（要等操作开始才显示）。
                    //   只设 _phase/_result 的 Visible 没用（实测截图仍是空的）—— 必须显示并移动容器。
                    _resultPanel.Visible = true;
                    _resultPanel.Bounds = new Rectangle(30, 292, 764, 190);
                };
                _phase.Text = isUninstall ? "卸载主程序" : "恢复安装前的蓝牙配置";
                _result.Text = isUninstall
                    ? "会删除程序文件，并移除「应用和功能」里的条目、ETW 注册表值、登录自启项。\n"
                      + "下面两项默认勾选：虚拟声卡（共享组件，其他软件可能在用）与本机配置（含按键映射）。\n"
                      + "取消勾选即可保留对应的东西。\n\n确认无误请点上面的「确认卸载」。"
                    : "会把蓝牙相关的注册表项恢复成安装前的值，恢复后需要重启。\n\n确认无误请点上面的「确认恢复配置」。";
                _result.ForeColor = Color.FromArgb(155, 101, 39);
                _install.Text = isUninstall ? "确认卸载（程序文件将被删除）" : "确认恢复配置";
            }
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
                // 卸载/恢复：确认已改成页面内的醒目按钮（见 ctor），这里不再弹 MessageBox。
                if (resume)
                {
                    await RunWork("resume");
                    if (_engine != null && _engine.State.ComponentsReady && _engine.State.DoubaoInstalled && !_engine.State.RestartRequired) OpenApplication();
                }
            };
        }

        /// <summary>把窗口收敛到当前屏幕工作区内；装不下就开滚动，不裁掉任何控件。</summary>
        private void ClampToWorkingArea()
        {
            try
            {
                var wa = Screen.FromControl(this).WorkingArea;
                var changed = false;
                if (Height > wa.Height - 16) { Height = Math.Max(480, wa.Height - 16); changed = true; }
                if (Width > wa.Width - 16) { Width = Math.Max(640, wa.Width - 16); changed = true; }
                if (changed)
                {
                    AutoScroll = true;              // 允许滚动到被挡住的部分
                    StartPosition = FormStartPosition.Manual;
                    Left = wa.Left + (wa.Width - Width) / 2;
                    Top = wa.Top + Math.Max(0, (wa.Height - Height) / 2);
                }
            }
            catch { }
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
                    if (operation == "uninstall")
                    {
                        _engine.Uninstall(_unCable.Checked, _unData.Checked);
                        return _engine.State;
                    }
                    if (operation == "restore") { _engine.RestoreRegistry(); return _engine.State; }
                    return _engine.Install(new InstallOptions());
                });
                var result = operation == "uninstall" || operation == "restore"
                    ? new[] { state.Phase, "操作已记录，可在需要时重新运行安装。" }
                    : SetupCore.InstallationResult(state);
                ApplyImeChoice();       // 把安装时选的输入法写进应用设置
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
                // 卸载/恢复失败时，原因必须**直接可见** —— 否则用户只知道"点了没用"
                _result.Text = (operation == "uninstall" || operation == "restore")
                    ? "没能完成：" + error.Message + "\n可以直接再点一次，或把下方详情发给我们。"
                    : "请检查网络连接，或完成弹出的安装窗口后重试。\n具体原因可在下方的故障详情中查看。";
                // ★ 2026-10-29：失败时**自动附上诊断建议**（复用 -Mode Status 的 Advice 段），
                //   让用户当场就知道下一步该点什么，而不是只看到一句英文异常。
                var advice = "";
                try
                {
                    if (_engine != null)
                    {
                        var (_, text, _) = _engine.CollectDiagnostics();
                        var lines = text.Replace("\r\n", "\n").Split('\n')
                                        .Where(l => l.Contains("Phase: Advice |"))
                                        .Select(l => l.Substring(l.IndexOf("|", StringComparison.Ordinal) + 1).Trim())
                                        .Where(l => l.Length > 0 && !l.StartsWith("----"))
                                        .ToArray();
                        if (lines.Length > 0) advice = "【建议】\r\n· " + String.Join("\r\n· ", lines) + "\r\n\r\n";
                    }
                }
                catch { }
                _details.Text = advice + error.Message + "\r\n\r\n日志：" + (_engine == null ? "尚未开始安装" : _engine.LogPath);
                _detailsLink.Visible = true;
                _logLink.Visible = _engine != null && File.Exists(_engine.LogPath);
                _install.Text = "重试安装";
            }
            finally { _running = false; _install.Enabled = _browse.Enabled = true; }
        }
        /// <summary>
        /// 把安装时选的输入法写进应用设置（`%APPDATA%\IPRC1000 Adapter\settings.json`）。
        ///
        /// 只改两个键（ImeProfile / ImeAutoPicked），**其余原样保留** —— 用户已有的按键映射、
        /// 鼠标参数等不能被安装器覆盖。应用下次启动会据此把「语音键」映射写好。
        /// </summary>
        private void ApplyImeChoice()
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                       "IPRC1000 Adapter");
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, "settings.json");

                // 用 net48 自带的 JavaScriptSerializer（本项目没有 System.Text.Json）
                var ser = new JavaScriptSerializer();
                var map = new Dictionary<string, object>();
                if (File.Exists(file))
                {
                    try
                    {
                        var parsed = ser.Deserialize<Dictionary<string, object>>(File.ReadAllText(file));
                        if (parsed != null) map = parsed;
                    }
                    catch { /* 坏了就重建，只影响这两个键 */ }
                }

                map["ImeProfile"] = _imeWechat.Checked ? "wechat" : "doubao";
                map["ImeAutoPicked"] = true;    // 安装时已选过，启动时不要再自动改
                File.WriteAllText(file, ser.Serialize(map), new System.Text.UTF8Encoding(true));
            }
            catch { /* 写不进去也不影响安装本身；应用里还能选 */ }
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
