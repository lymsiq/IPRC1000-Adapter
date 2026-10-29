using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace Iprc1000Adapter;

/// <summary>
/// **语音悬浮框**（2026-10-29）：按住语音键时在输入光标附近显示波形 + 识别结果。
///
/// 三条硬性要求（任何一条做错都会毁掉体验）：
///   1. **不抢焦点**：一旦抢走焦点，目标输入框失焦 → 输入法结束会话、光标消失，
///      我们注入的文字也会打进别的地方。用 WS_EX_NOACTIVATE + ShowWithoutActivation。
///   2. **不挡字**：贴在光标**下方**，并且比光标低一点；空间不够就翻到上方。
///   3. **拿不到光标也要有地方去**：浏览器/UWP 内部输入框常常取不到 caret 矩形，
///      必须逐级回退（caret → 焦点窗口底部中央 → 屏幕右下），否则框会飘到莫名其妙的角落。
///
/// 自绘而不是嵌 WebView2：一个 300×90 的小条再拉一个 WebView2 要 300MB 内存，不值。
/// </summary>
internal static class AsrOverlay
{
    private static OverlayForm? _form;
    private static Thread? _uiThread;
    /// <summary>浮层线程的同步上下文：**所有 UI 操作都 Post 到这里**。
    /// 不能只靠 BeginInvoke —— 首次显示时窗口句柄还没创建，
    /// 那样会在调用线程（线程池）上直接改 Visible，跨线程操作会被静默吞掉，
    /// 表现就是"该弹的浮层不弹"。</summary>
    private static SynchronizationContext? _uiCtx;
    private static readonly object Gate = new();

    /// <summary>
    /// 浮层必须在**独立 STA 线程 + 自己的消息循环**里创建。
    /// 为什么：AsrRun 跑在线程池（MTA），在那里 new Form + Timer 没有消息泵，
    /// 定时器不会走 ⇒ 波形一帧都不刷新（而且跨线程访问控件会抛异常）。
    /// 不能用主窗口线程：主窗体在托盘时会被隐藏，且在别的线程上建窗容易踩焦点。
    /// </summary>
    private static void EnsureUi()
    {
        lock (Gate)
        {
            if (_uiThread != null) return;
            var ready = new ManualResetEventSlim();
            _uiThread = new Thread(() =>
            {
                try
                {
                    Application.EnableVisualStyles();
                    // Application.Run 会安装 WindowsFormsSynchronizationContext；先自己装一个保证可用
                    SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                    _uiCtx = SynchronizationContext.Current;
                    _form = new OverlayForm();
                    _form.CreateControl();          // 提前建句柄，后续 Invoke 才不会走错线程
                    ready.Set();
                    // 隐藏宿主窗体只是为了给消息循环一个宿主
                    using var host = new Form
                    {
                        FormBorderStyle = FormBorderStyle.None,
                        ShowInTaskbar = false,
                        Opacity = 0,
                        Size = new Size(1, 1),
                        StartPosition = FormStartPosition.Manual,
                        Location = new Point(-32000, -32000)
                    };
                    Application.Run(host);
                }
                catch (Exception e) { Safe.Note("浮层线程异常：" + e.Message); ready.Set(); }
            })
            { IsBackground = true, Name = "asr-overlay" };
            _uiThread.SetApartmentState(ApartmentState.STA);
            _uiThread.Start();
            ready.Wait(3000);
        }
    }

    /// <summary>所有 UI 操作都回到浮层线程执行。</summary>
    private static void Ui(Action a)
    {
        try
        {
            var ctx = _uiCtx;
            if (ctx != null) { ctx.Post(_ => { try { a(); } catch { } }, null); return; }
            // 上下文还没建好（极早期）：退回直接执行，至少不会丢
            var f = _form;
            if (f == null) { a(); return; }
            if (f.IsHandleCreated && f.InvokeRequired) f.BeginInvoke(a);
            else a();
        }
        catch { }
    }

    // 尺寸对齐输入法那种窄条：宽 300 / 高 64（原来 380×104 太大）
    public const int W = 300, H = 64;

    // ── 状态 ──
    private static string _text = "";
    private static string _hint = "正在听…";
    private static bool _listening;
    /// <summary>当前展示的是"未定稿"文字（流式 partial）⇒ 画下划线。</summary>
    private static bool _provisional;
    private static long _shownAt;

    public static bool Visible => _form is { Visible: true };

    public static void Show(string hint)
    {
        lock (Gate)
        {
            _hint = hint;
            _text = "";
            _listening = true;
            _provisional = false;
            _shownAt = Environment.TickCount64;
            try
            {
                EnsureUi();
                Ui(() =>
                {
                    if (_form == null) return;
                    PositionNearCaret(_form);
                    _form.Visible = true;
                    _form.TopMost = true;
                    _form.Invalidate();
                });
            }
            catch (Exception e) { Safe.Note("悬浮框显示失败：" + e.Message); }
        }
    }

    /// <summary>流式 partial：边识别边显示（引擎给的是全量假设，直接替换）。</summary>
    public static void ShowPartial(string text)
    {
        lock (Gate) { _listening = false; _hint = ""; _text = text ?? ""; _provisional = true; }
        Ui(() => _form?.Invalidate());
    }

    public static void Recognizing()
    {
        lock (Gate) { _listening = false; _hint = "识别中…"; }
        Ui(() => _form?.Invalidate());
    }

    /// <summary>显示结果，holdMs 后自动隐藏。</summary>
    public static void ShowResult(string text, int holdMs = 2200)
    {
        lock (Gate) { _listening = false; _hint = ""; _text = text ?? ""; _provisional = false; }
        Ui(() => _form?.Invalidate());
        Task.Delay(holdMs).ContinueWith(_ => Hide());
    }

    public static void Hide()
    {
        lock (Gate)
        {
            Ui(() => { try { if (_form != null) _form.Visible = false; } catch { } });
        }
    }

    // ── 位置：caret → 焦点窗口底部 → 屏幕右下 ──
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public RECT rcCaret;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO info);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref Point p);

    private static void PositionNearCaret(Form f)
    {
        try
        {
            var screen = Screen.FromPoint(Cursor.Position).WorkingArea;
            var hwnd = GetForegroundWindow();
            var x = screen.Right - W - 24;
            var y = screen.Bottom - H - 24;

            if (hwnd != IntPtr.Zero)
            {
                GetWindowThreadProcessId(hwnd, out _);
                var tid = GetWindowThreadProcessId(hwnd, out _);
                var gi = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
                if (GetGUIThreadInfo(tid, ref gi) && gi.hwndCaret != IntPtr.Zero && gi.rcCaret.Bottom > gi.rcCaret.Top)
                {
                    // ① 光标矩形（准确）
                    var p = new Point(gi.rcCaret.Left, gi.rcCaret.Bottom);
                    ClientToScreen(hwnd, ref p);
                    // 多屏：屏幕跟着光标走，否则副屏上会跑回主屏
                    screen = Screen.FromPoint(p).WorkingArea;
                    x = p.X - 16;
                    y = p.Y + 8;
                }
                else if (GetWindowRect(hwnd, out var wr))
                {
                    // ② 焦点窗口底部中央
                    x = (wr.Left + wr.Right) / 2 - W / 2;
                    y = wr.Bottom - H - 60;
                }
            }

            // 夹到工作区内（含上方翻转）
            if (y + H > screen.Bottom - 8) y = Math.Max(screen.Top + 8, y - H - 34);
            x = Math.Max(screen.Left + 8, Math.Min(x, screen.Right - W - 8));
            y = Math.Max(screen.Top + 8, Math.Min(y, screen.Bottom - H - 8));
            f.Location = new Point(x, y);
        }
        catch { }
    }

    // ── 窗口本体 ──
    private sealed class OverlayForm : Form
    {
        private readonly System.Windows.Forms.Timer _tick = new() { Interval = 40 };

        public OverlayForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(W, H);
            BackColor = Color.FromArgb(24, 26, 30);
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
            _tick.Tick += (_, _) => { if (Visible) Invalidate(); else _tick.Stop(); };
            _tick.Start();
        }

        /// <summary>关键：显示时不激活窗口（否则抢焦点，输入法会退出）。</summary>
        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                const int WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x00000080;
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            using (var path = Round(new Rectangle(0, 0, Width - 1, Height - 1), 11))
            using (var bg = new SolidBrush(Color.FromArgb(24, 26, 30)))
            using (var pen = new Pen(Color.FromArgb(58, 62, 70), 1))
            {
                g.FillPath(bg, path);
                g.DrawPath(pen, path);
            }

            // 波形（左侧 96px）：直接用录音缓冲的实时电平
            var bars = AsrCapture.Levels();
            var bw = 3; var gap = 2; var bx = 12; var baseY = Height / 2 + 4;
            const int showBars = 12;
            for (var i = 0; i < bars.Length && i < showBars; i++)
            {
                var v = Math.Clamp(bars[i], 0f, 1f);
                var h = 3 + (int)(v * 18);
                using var b = new SolidBrush(Color.FromArgb(_listening ? 90 : 60, 130, 200, 255));
                g.FillRectangle(b, bx + i * (bw + gap), baseY - h / 2,
                                bw, h);
            }

            // 右侧文字
            var textX = bx + showBars * (bw + gap) + 8;
            var rect = new Rectangle(textX, 8, Width - textX - 12, Height - 16);
            using var fHint = new Font("Microsoft YaHei UI", 8.5F);
            using var fText = new Font("Microsoft YaHei UI", 9.5F);
            // 垂直居中：提示一行、结果最多两行（超出用省略号，不撑破圆角）
            using var fmt = new StringFormat
            {
                Alignment = StringAlignment.Near,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.LineLimit
            };
            if (_text.Length == 0)
            {
                using var br = new SolidBrush(Color.FromArgb(165, 205, 215, 225));
                g.DrawString(_hint, fHint, br, rect, fmt);
            }
            else
            {
                using var br = new SolidBrush(Color.FromArgb(242, 246, 250));
                if (_provisional)
                {
                    // 未定稿：带下划线（"还在改"的视觉约定）；定稿后下划线消失
                    using var fU = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Underline);
                    g.DrawString(_text, fU, br, rect, fmt);
                }
                else g.DrawString(_text, fText, br, rect, fmt);
            }

            // 顶部细进度感（听 0~8s 的填充条，纯装饰，提示"在录"）
            if (_listening)
            {
                var sec = (Environment.TickCount64 - _shownAt) / 1000.0;
                var w = (int)Math.Min(Width - 24, (Width - 24) * Math.Min(1.0, sec / 8.0));
                using var b = new SolidBrush(Color.FromArgb(120, 90, 170, 255));
                g.FillRectangle(b, 12, Height - 5, w, 2);
            }
        }

        private static GraphicsPath Round(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            var d = radius * 2;
            p.AddArc(r.Left, r.Top, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
