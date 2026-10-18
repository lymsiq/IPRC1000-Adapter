using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Iprc1000Adapter;

internal sealed class MainForm : Form
{
    /// <summary>
    /// WebView2 控件。**可空、可重建**。
    ///
    /// 窗口收进托盘一段时间后会主动释放它，把 Chromium 那 6 个进程 / 356 MB 还回去
    /// （见 <see cref="ReleaseWebView"/>）。用户再点开时重建 —— 重建只花几百毫秒，
    /// 换来的收益是**常驻内存从 457 MB 掉到约 100 MB**。
    /// </summary>
    private WebView2? _web;

    /// <summary>窗口隐藏多少秒之后释放界面进程（0 = 从不释放）。可用环境变量覆盖，便于实测对比。</summary>
    private static int ReleaseDelaySec
    {
        get
        {
            var v = Environment.GetEnvironmentVariable("IPRC1000_RELEASE_WEBVIEW_SEC");
            return int.TryParse(v, out var n) ? Math.Max(0, n) : 30;
        }
    }

    private readonly System.Windows.Forms.Timer _releaseTimer = new() { Interval = 1000 };
    private DateTime _hiddenSince = DateTime.MinValue;
    private string _lastUrl = "https://iprc1000.local/index.html";
    private bool _webInitBusy;
    private bool _releaseDisabled;      // 一旦重建失败就永久停用释放，避免反复出错
    private long _releaseCount;
    private readonly Backend _backend = new();
    private readonly KeyFilter _filter;
    private readonly NotifyIcon _tray;
    private bool _reallyExit;
    private readonly bool _selftest;
    private string? _selftestOut;

    /// <summary>
    /// 单实例用的名字：命名互斥体 + "请把窗口显示出来"事件。
    ///
    /// 为什么要单实例：**每份实例都会创建自己的虚拟键盘设备**。若其中一份在"按住某键"时
    /// 被结束（任务管理器结束进程 / 崩溃），那个键就留在**它那台**设备的按下状态里，
    /// 而新实例用的是**另一台**虚拟键盘，管不到它 → 系统层表现为"这个键卡住了"。
    /// 第二个实例改为：Set 下面这个事件让已在运行的那份把窗口显示出来，然后自己退出。
    /// </summary>
    public const string SingleInstanceMutex = @"Local\IPRC1000Adapter.SingleInstance";
    public const string ShowWindowEvent = @"Local\IPRC1000Adapter.ShowWindow";
    private EventWaitHandle? _showEvent;
    private Thread? _showThread;

    /// <summary>接住"第二个实例要求显示窗口"的通知（后台线程等待，回 UI 线程执行）。</summary>
    private void StartShowWindowListener()
    {
        try
        {
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEvent);
            _showThread = new Thread(() =>
            {
                while (!_reallyExit)
                {
                    try
                    {
                        if (!_showEvent.WaitOne(1000)) continue;
                        if (IsDisposed) break;
                        try { BeginInvoke(new Action(() => RestoreFromTray())); } catch { break; }
                    }
                    catch { break; }
                }
            })
            { IsBackground = true, Name = "iprc1000-show-window" };
            _showThread.Start();
        }
        catch (Exception e)
        {
            StartupMark("窗口唤起监听未启动（已忽略）：" + e.Message);
        }
    }

    public MainForm(bool selftest, string? selftestOut)
    {
        _selftest = selftest;
        _selftestOut = selftestOut;
        StartupMark("窗口构造开始");

        Text = Env.AppName;
        ClientSize = new Size(1180, 760);
        MinimumSize = new Size(980, 680);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(0xEA, 0xF2, 0xFD);   // 与页面底色一致，避免白屏闪烁

        var ico = Env.FindAsset("icon.ico");
        if (ico != null) Icon = new Icon(ico);

        // ── 系统托盘：关闭窗口 = 最小化到托盘，右键菜单可真正退出 ──
        _tray = new NotifyIcon
        {
            Icon = Icon ?? SystemIcons.Application,
            Text = Env.AppName,
            Visible = true,
        };
        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("显示主窗口", null, (_, _) => RestoreFromTray());
        trayMenu.Items.Add("-");
        trayMenu.Items.Add("退出", null, (_, _) => { _reallyExit = true; Application.Exit(); });
        _tray.ContextMenuStrip = trayMenu;
        // 双击 = 打开并**直接切到设置页**（用户要求）；单击 = 只打开
        _tray.DoubleClick += (_, _) => RestoreFromTray(showSettings: true);
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) RestoreFromTray();
        };

        // 单实例：第二个实例会 Set 事件要求我们把窗口显示出来
        StartShowWindowListener();

        // 启动时最小化到托盘（默认开）
        if (Settings.StartMinimized)
        {
            WindowState = FormWindowState.Minimized;
            Load += (_, _) => { if (WindowState == FormWindowState.Minimized) Hide(); };
        }

        _filter = new KeyFilter();
        _filter.OnAnyKey += vk => _backend.NoteKey(vk);
        _filter.OnButton += (bid, pressed) => _backend.NoteButton(bid, pressed);
        _backend.AttachFilter(_filter);

        // ★ 模式指示必须显眼：托盘提示 + 气泡。否则用户按到方块键切了模式却不知道，
        //   会以为遥控器坏了（方向键不动了 / 反过来光标乱跑）。
        _backend.MouseModeChanged += (msg, mouse) =>
        {
            try
            {
                var t = $"{Env.AppName} · {(mouse ? "鼠标模式" : "键盘模式")}";
                _tray.Text = t.Length > 63 ? t[..63] : t;      // NotifyIcon.Text 上限 63 字符
                _tray.ShowBalloonTip(2500, Env.AppName, msg,
                    mouse ? ToolTipIcon.Info : ToolTipIcon.None);
            }
            catch { }
        };

        // ★ 不再在构造函数里建 WebView2 控件。
        //   开机自启走的是 `StartMinimized`（默认开）—— 那种情况下用户根本看不到界面，
        //   却要先付 **341 ms（启动开销的 51%）+ 356 MB（常驻内存的 78%）**。
        //   现在改成"真正露出来时才建"：可见启动走下面 Load 那条路，
        //   托盘启动走 `OnVisibleChanged` 那条路（那条本来就有，用于释放后重建）。
        StartupMark("窗口构造完成（托盘/钩子对象已建；WebView2 延迟到需要时）");

        // 隐藏计时：收进托盘 N 秒后把界面进程放掉
        _releaseTimer.Tick += (_, _) => ReleaseTick();
        _releaseTimer.Start();

        Load += (_, _) =>
        {
            StartupMark("Load 触发（窗口句柄就绪）");
            // 句柄此刻已就绪，再装 Raw Input + 键盘钩子
            _filter.Start(Handle);
            StartupMark("按键钩子 + RAW INPUT 已装");

            // ★ 启动兜底：清掉**上一个实例**被强杀时留在系统里的卡住修饰键
            //   （退出路径在 taskkill /F / 崩溃时跑不到，那台虚拟键盘是上一个进程的设备，
            //     我们的全零报告清不掉它 —— 只能补 SendInput 的 KEYUP）
            try
            {
                var stale = KeyFilter.ClearStaleModifiersOnStart();
                if (stale != null) StartupMark($"启动兜底：清掉了残留的修饰键 {stale}");
            }
            catch { }
            // ⚠ InitWebView 是 async void 语义：**必须自己 try/catch**，
            //   否则它内部任何异常都会掀翻进程（0xE0434352），且不留任何痕迹。
            //
            // ★ 只有窗口**真的可见**时才建界面。启动到托盘（StartMinimized，开机自启走这条）
            //   时不建 —— 省 341 ms + 356 MB。露出来时由 OnVisibleChanged 补建。
            //   `_selftest` 例外：自检要读界面，必须建。
            if (!Settings.StartMinimized || _selftest)
            BeginInvoke(new Action(async () =>
            {
                try { await EnsureWebViewAsync(); }
                catch (Exception e)
                {
                    Trace($"InitWebView 异常 {e.GetType().Name}: {e.Message}");
                    if (_selftest)
                    {
                        try
                        {
                            File.WriteAllText(SelfTestPath, Env.JsonPretty(new JsonObject
                            {
                                ["error"] = "WebView2 初始化失败：" + e.Message,
                                ["type"] = e.GetType().Name,
                                ["detail"] = e.ToString().Replace("\r", "").Replace("\n", " | "),
                            }));
                        }
                        catch { }
                        FinishSelfTest();
                    }
                    else
                    {
                        MessageBox.Show($"界面初始化失败：{e.Message}", Env.AppName);
                    }
                }
            }));
            else StartupMark("启动到托盘：界面暂不创建（首次打开窗口时由 OnVisibleChanged 再建，省 341 ms + 356 MB）");
        };

        // ★ 窗口显示之后再在后台做重活：探针 → 自动切驱动（约 1~2 秒）→ 拉起语音桥。
        //   原来这些都在界面线程上跑，所以启动后要卡一两秒；现在界面立即可用。
        Shown += (_, _) => Task.Run(() => _backend.InitAsync());

        // 进程退出兜底：无论如何都把注入的键放掉（正常关闭走 Dispose，异常退出走这里）
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { KeyFilter.ReleaseAllInjected(); } catch { }
            try { KeyLog.Flush(); } catch { }
            try { _showEvent?.Dispose(); } catch { }
        };
    }

    private async Task InitWebView()
    {
        var web = _web ?? throw new InvalidOperationException("WebView2 控件尚未创建");
        StartupMark("开始初始化 WebView2（创建 Chromium 环境）");
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Env.AppName, "WebView2");
        Directory.CreateDirectory(dataDir);

        // ★ 省资源：默认关掉对「本地静态 UI」完全没用的 Chromium 子系统。
        //
        //   本程序常驻后台，UI 只是几页本地 HTML/CSS —— 但默认会拉起一整套浏览器：
        //   实测 **13 个进程 / 724 MB**，其中属于我们的就有 **6 个进程 / 356 MB**
        //   （GPU 进程 84 MB + crashpad 12 MB 是纯浪费）。
        //   走官方环境变量注入，不动 EnsureCoreWebView2Async 那条敏感路径。
        //
        //   刻意**没有**加 --disable-renderer-backgrounding /
        //   --disable-backgrounding-occluded-windows：那两个会让后台渲染进程不降频，
        //   对本常驻托盘应用是反效果（我们要的就是窗口藏起来时它自己降下去）。
        //
        // ⚠ 关于 `--disable-gpu`（本可再省 ~84 MB 的 GPU 进程）：
        //   **实测没能验证它是否会让界面白屏**，所以默认不加。
        //   原因：自动化环境里启动的实例窗口不显示，WebView2 压根不初始化
        //   （连 `--selftest` 都因此走 18 秒兜底，这是既有问题），拿不到对照组。
        //   想试的话自己加：设环境变量 IPRC1000_WEBVIEW_ARGS="--disable-gpu ..."，
        //   界面白就去掉它。
        var extra = Environment.GetEnvironmentVariable("IPRC1000_WEBVIEW_ARGS");
        if (string.IsNullOrWhiteSpace(extra))
            extra = "--disable-crash-reporter --no-crash-upload "  // 省 crashpad（~12 MB）
                  + "--disable-background-networking "
                  + "--disable-sync --disable-extensions "
                  + "--no-first-run --no-default-browser-check "
                  + "--mute-audio "
                  + "--renderer-process-limit=1 "          // 我们只有一页，不需要多渲染进程
                  + "--disable-features=Translate,OptimizationHints,MediaRouter,"
                  + "CalculateNativeWinOcclusion";
        Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", extra);
        Trace($"[webview] 额外参数：{extra}");

        // ★ 用 CreationProperties + 无参 EnsureCoreWebView2Async：
        //   它内部会自行 marshal 到 UI 线程；显式 CreateAsync 后 await 的续体可能落到
        //   线程池，再碰 CoreWebView2 就会抛「只能从 UI 线程访问」。
        web.CreationProperties = new CoreWebView2CreationProperties { UserDataFolder = dataDir };
        await web.EnsureCoreWebView2Async();
        StartupMark("WebView2 环境就绪（Chromium 已起来，这段通常是启动里最大的一块）");

        web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        web.CoreWebView2.Settings.AreDevToolsEnabled = !_selftest;
        web.CoreWebView2.Settings.IsStatusBarEnabled = false;
        web.CoreWebView2.WebMessageReceived += OnWebMessage;

        // 记下当前地址：释放后重建时用它回到原来的页面（否则用户每次被扔回首页）
        web.CoreWebView2.SourceChanged += (_, _) =>
        {
            try
            {
                var u = web.CoreWebView2.Source;
                if (!string.IsNullOrEmpty(u) && u.StartsWith("https://iprc1000.local", StringComparison.Ordinal))
                    _lastUrl = u;
            }
            catch { }
        };

        var uiDir = Path.Combine(Env.AppDir, "ui");
        Directory.CreateDirectory(uiDir);
        // 用虚拟主机映射，页面有正常 origin，比 file:// 更稳
        web.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "iprc1000.local", uiDir, CoreWebView2HostResourceAccessKind.Allow);

        // ★★ 导航守卫（纵深防御）
        //
        // 为什么必要：OnWebMessage 是 **JS → 后端** 的桥，暴露了 40 多个方法，
        // 其中含 reboot / installCable / installWinuhid / setDeviceDriver /
        // relaunchAdmin / runSetup / applyRegistry 等**管理员级**动作，
        // 而本程序是**以管理员身份运行**的。
        // 界面目前全部是本地文件、没有任何外链，所以现状不可利用；
        // 但只要页面被导航到外部 URL（加个外链、内容被注入），外部内容就能调到这些方法。
        // 这里把"导航离开本地虚拟主机"和"开新窗口"一律拦掉，成本极低。
        web.CoreWebView2.NavigationStarting += (_, e) =>
        {
            if (IsLocalUi(e.Uri)) return;
            e.Cancel = true;
            Trace($"[nav-guard] 已拦截外部导航：{e.Uri}");
        };
        web.CoreWebView2.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;                     // 一律不开新窗口（外链交给系统浏览器走 openUrl）
            Trace($"[nav-guard] 已拦截新窗口：{e.Uri}");
        };

        // ★ 常开：记录「页面真的渲染出来」的时刻。
        //   这一条和「WebView2 环境就绪」是**两件事** —— `Navigate()` 只是**发起**导航，
        //   它返回时页面还没渲染，用户看到的还是空窗口。
        //   以前只有 selftest 才订阅这个事件，于是生产环境里
        //   "页面到底什么时候出来的"完全看不见 —— 这正是「启动有点慢」说不清的原因。
        web.CoreWebView2.NavigationCompleted += (_, e) =>
        {
            StartupMark(e.IsSuccess
                ? "页面渲染完成 ← 用户此时才真正看到内容"
                : $"页面渲染失败：{e.WebErrorStatus}");
            // 托盘双击要求"打开就是设置页"：界面首次建好时在这里补执行（页面此刻才存在 goto）
            if (e.IsSuccess && _pendingPage != null)
            {
                var pg = _pendingPage;
                _pendingPage = null;
                try { _ = web.CoreWebView2.ExecuteScriptAsync($"goto('{pg}')"); } catch { }
            }
        };

        if (_selftest)
        {
            Trace($"selftest 块已进入；报告路径={SelfTestPath}；AppDir={Env.AppDir}");
            web.CoreWebView2.NavigationCompleted += async (_, _) =>
            {
                Trace("NavigationCompleted 触发");
                // 启动即打开指定页（IPRC1000_START_PAGE=overview|keys|env）—— 便于验证与固化首页
                var startPage = Environment.GetEnvironmentVariable("IPRC1000_START_PAGE");
                if (!string.IsNullOrWhiteSpace(startPage))
                {
                    try
                    {
                        await web.CoreWebView2.ExecuteScriptAsync(
                            $"document.querySelector('[data-page=\"{startPage}\"]')?.click()");
                    }
                    catch { }
                }
                var ok = await RunSelfTest();
                Trace($"RunSelfTest 返回 {ok}");
                if (ok) FinishSelfTest();
            };
            // 兜底①：页面没加载出来也要出报告（18 s）
            var t = new System.Windows.Forms.Timer { Interval = 18000 };
            t.Tick += async (_, _) =>
            {
                t.Stop();
                Trace("兜底计时器①(18s)触发");
                var ok = await RunSelfTest(true);
                Trace($"RunSelfTest(fallback) 返回 {ok}");
                if (ok) FinishSelfTest();
            };
            t.Start();

            // 兜底②：**硬看门狗**（35 s）。上面两步都可能被卡住（例如
            //   ExecuteScriptAsync 永不返回、或 NavigationCompleted 压根没触发），
            //   这里保证无论如何都出一份报告并退出，不会静默挂死。
            var hard = new System.Windows.Forms.Timer { Interval = 35000 };
            hard.Tick += (_, _) =>
            {
                hard.Stop();
                Trace("硬看门狗(35s)触发");
                EnsureReportWritten("hard-timeout-35s");
                FinishSelfTest();
            };
            hard.Start();
        }
        web.CoreWebView2.Navigate(_lastUrl);
        StartupMark("导航已发起");

    }

    // ───────── 界面进程的释放与重建 ─────────
    //
    // 动因（实测）：本程序常驻托盘，而 WebView2 会常年占着 **6 个进程 / 356 MB**。
    // 窗口收进托盘后，这部分内存**完全没人用**。所以：
    //   隐藏 ≥ N 秒 → Dispose 掉 WebView2（Chromium 进程随之退出）
    //   用户点开托盘 → 重建（几百毫秒），并回到原来的页面
    // 收益：常驻内存 457 MB → 约 100 MB，且**界面观感零损失**（重建的是同一个页面）。

    /// <summary>
    /// CoreWebView2 是否已初始化并完成首次导航。
    /// ★ 必须和「控件是否存在」(<see cref="_web"/>) 分开记 ——
    ///   构造函数就会创建控件（让窗口有内容可停靠），但初始化要等到 Load 之后。
    ///   如果拿「控件是否为 null」当"已初始化"用，首次启动会在
    ///   <see cref="EnsureWebViewAsync"/> 的守卫处直接返回，InitWebView 永远不执行
    ///   → 没有 CoreWebView2、也没有导航 → **整窗白屏**（v1.1.0 首发的实际事故）。
    /// </summary>
    private bool _webReady;

    private void AttachWebView()
    {
        var w = new WebView2 { Dock = DockStyle.Fill };
        _web = w;
        Controls.Add(w);
        w.BringToFront();
    }

    /// <summary>确保 WebView2 已创建并初始化（首次与重建共用这一条路径）。</summary>
    private async Task EnsureWebViewAsync()
    {
        if (_webReady || _webInitBusy || IsDisposed) return;
        _webInitBusy = true;
        try
        {
            var sw = Stopwatch.StartNew();
            if (_web == null) AttachWebView();      // 释放后重建；首次启动控件已由构造函数建好
            await InitWebView();
            _webReady = true;
            // ⚠ 措辞要准：这里只代表「Chromium 环境起来了 + 导航已发起」，
            //   **页面还在渲染**，用户看到的仍是那个底色空窗口。
            //   "用户真正看到内容"的时刻由上面的 NavigationCompleted 打点（T+ 行）。
            WebViewLog($"WebView2 环境就绪 + 导航已发起，耗时 {sw.ElapsedMilliseconds} ms" +
                       $"（累计释放 {_releaseCount} 次）");
        }
        catch (Exception e)
        {
            WebViewLog($"★ 界面初始化失败：{e.GetType().Name}: {e.Message}");
            throw;
        }
        finally { _webInitBusy = false; }
    }

    private void ReleaseTick()
    {
        if (Visible || _releaseDisabled || _web == null || _webInitBusy) return;
        if (_hiddenSince == DateTime.MinValue) { _hiddenSince = DateTime.Now; return; }

        var hideSec = (DateTime.Now - _hiddenSince).TotalSeconds;
        var delay = ReleaseDelaySec;
        if (delay <= 0 || hideSec < delay) return;      // 0 = 用户显式要求不要释放

        ReleaseWebView(hideSec);
    }

    private void ReleaseWebView(double hideSec)
    {
        var w = _web;
        if (w == null) return;
        _web = null;
        _webReady = false;
        // 重建时重新开一段时间轴（否则重建的打点会混在上一轮里，看不出是哪一次）
        _startupMarked = false;
        _firstStateLogged = false;

        var sw = Stopwatch.StartNew();
        try { w.Dispose(); } catch (Exception e) { WebViewLog($"释放时出异常（已忽略）：{e.Message}"); }
        _releaseCount++;
        WebViewLog($"窗口已隐藏 {hideSec:F0} s → 释放界面进程（Dispose 耗时 {sw.ElapsedMilliseconds} ms），" +
                   $"当前页面已记住={_lastUrl}");
    }

    /// <summary>
    /// 启动时间轴埋点：把「启动有点慢」从一个体感拆成 `T+xxx ms` 的若干段，
    /// 和界面生命周期日志写在同一个文件里 —— 于是"环境就绪 vs 页面渲染"
    /// 各占多少毫秒是一眼可见的事实，不用猜。
    /// </summary>
    private static bool _startupMarked;
    internal static void StartupMark(string what)
    {
        var body = $"[T+{Program.StartupMs,5} ms] {what}";
        if (!_startupMarked)
        {
            _startupMarked = true;
            body = "──── 时间轴（本轮启动或重建）────\n" + body;
        }
        WebViewLog(body);
    }

    /// <summary>本轮是否已经记过"前端 JS 已就绪"（重建时重置，方便再看一次）。</summary>
    private bool _firstStateLogged;

    /// <summary>界面进程的生命周期日志（与 key-events.log 分开，方便单独看内存策略是否生效）</summary>
    private static void WebViewLog(string msg)
    {
        // 发布版默认不写 webview.log（启动时间轴的用途是排障，不是运行需要）
        if (!LogSwitch.Enabled) return;
        try
        {
            var p = Path.Combine(Env.ConfigDir, "webview.log");
            var body = $"{DateTime.Now:HH:mm:ss.fff}  {msg}\n";
            lock (WebViewLogLock)
            {
                if (!File.Exists(p)) File.WriteAllText(p, body, new UTF8Encoding(true));
                else File.AppendAllText(p, body, new UTF8Encoding(false));
            }
        }
        catch { }
    }

    private static readonly object WebViewLogLock = new();

    /// <summary>自检报告路径（未指定就落在程序目录）</summary>
    private string SelfTestPath => _selftestOut ?? Path.Combine(Env.AppDir, "selftest.json");

    /// <summary>
    /// 是否是本程序的本地界面。
    /// 只放行虚拟主机 `iprc1000.local` 与初始的 about:blank；
    /// 其余（http/https 外站、file://、data: 等）一律视为外部导航而拦截
    /// —— 尤其不放行 `data:`，它本身就是一种注入途径。
    /// </summary>
    private static bool IsLocalUi(string uri)
        => uri.StartsWith("https://iprc1000.local/", StringComparison.OrdinalIgnoreCase)
           || uri.Equals("about:blank", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 自检轨迹。写到 `&lt;报告&gt;.trace.log` —— 报告本身出不来时，
    /// 靠它才能知道到底走到了哪一步（自检是"出了事没处问"的场景，必须有轨迹）。
    /// </summary>
    private void Trace(string msg)
    {
        if (!_selftest) return;
        try
        {
            File.AppendAllText(SelfTestPath + ".trace.log",
                $"{DateTime.Now:HH:mm:ss.fff}  {msg}\n");
        }
        catch { }
    }

    /// <summary>报告还没落盘就补一份"为什么没写出来"的最小报告。</summary>
    private void EnsureReportWritten(string reason)
    {
        try
        {
            if (File.Exists(SelfTestPath)) return;
            File.WriteAllText(SelfTestPath,
                $"{{\"error\":\"selftest 未在预期时间内完成\",\"reason\":\"{reason}\"," +
                $"\"time\":\"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\"}}");
        }
        catch { }
    }

    /// <summary>收尾：关窗 + 兜底强退（WebView2 的宿主进程有时会把 Application.Run 拖住）。</summary>
    private void FinishSelfTest()
    {
        try { Close(); } catch { }
        // Close() 后消息循环若仍不退出（WebView2 组合进程常见），直接结束进程 ——
        // 自检是一次性任务，报告已经落盘，强退无副作用。
        var t = new System.Windows.Forms.Timer { Interval = 1200 };
        t.Tick += (_, _) => { t.Stop(); Environment.Exit(0); };
        t.Start();
    }

    private bool _selfTestDone;

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonNode? idNode;
        string method;
        JsonNode? args = null;
        try
        {
            var node = JsonNode.Parse(e.WebMessageAsJson)!.AsObject();
            // ★ id 必须原样保留类型：JS 侧是数字，若按 string 读会抛异常 → 不回包 → 前端永远挂起
            idNode = node["id"]?.DeepClone();
            method = node["method"]?.GetValue<string>() ?? "";
            args = node["args"];
        }
        catch { return; }

        string Arg(int i) => args is JsonArray a && a.Count > i ? (a[i]?.ToString() ?? "") : "";

        // 前端 boot() 一开始就 bindUI() + refresh()，所以首个 getState 到达
        // 等价于「JS 跑起来了、开始要数据了」—— 用它给时间轴收尾。
        if (!_firstStateLogged && method == "getState")
        {
            _firstStateLogged = true;
            StartupMark("前端 JS 已就绪（首次向宿主取状态）");
        }

        string result = method switch
        {
            "getState" => _backend.GetState(),
            "refreshAll" => _backend.GetState(),
            "applyRegistry" => _backend.ApplyRegistry(),
            "reboot" => _backend.Reboot(),
            "installCable" => _backend.InstallCable(),
            "setDefaultMic" => _backend.SetDefaultMic(),
            "saveKey" => _backend.SaveKey(Arg(0), Arg(1)),
            "switchGroup" => _backend.SwitchGroup(Arg(0)),
            "newGroup" => _backend.NewGroup(),
            "renameGroup" => _backend.RenameGroup(Arg(0), Arg(1)),
            "deleteGroup" => _backend.DeleteGroup(Arg(0)),
            "exportGroup" => _backend.ExportGroup(),
            "importGroup" => _backend.ImportGroup(Arg(0)),
            "resetKeys" => _backend.ResetKeys(),
            "startBridge" => _backend.StartBridge(),
            "stopBridge" => _backend.StopBridge(),
            "relaunchAdmin" => _backend.RelaunchAdmin(),
            "openUrl" => _backend.OpenUrl(Arg(0)),
            "setInject" => _backend.SetInject(Arg(0)),
            "testInject" => _backend.TestInject(),
            "clearKeys" => _backend.ClearKeys(),
            "forceUnlock" => _backend.ForceUnlock(),
            "releaseAllKeys" => _backend.ReleaseAllKeys(),
            "setKeySource" => _backend.SetKeySource(Arg(0)),
            "runSetup" => _backend.RunSetup(),
            "setDeviceDriver" => _backend.SetDeviceDriver(Arg(0)),
            // 鼠标模式（2026-10-16）：方块键切模式；参数与状态见 Backend.Mouse
            "setMouseMode" => _backend.SetMouseMode(Arg(0)),
            "setMouseConfig" => _backend.SetMouseConfig(Arg(0)),
            "mouseSample" => _backend.TakeMouseSample(),
            "mouseCalibrate" => _backend.CalibrateMouse(),
            "setAutoStart" => _backend.SetAutoStart(Arg(0)),
            "clearKeyLog" => _backend.ClearKeyLog(),
            "beginBind" => _backend.BeginBind(Arg(0)),
            "cancelBind" => _backend.CancelBind(),
            "setAutoRun" => _backend.SetAutoRun(Arg(0)),
            "setStartMinimized" => _backend.SetStartMinimized(Arg(0)),
            "setLog" => _backend.SetLog(Arg(0)),
            "setAutoSwitchDriver" => _backend.SetAutoSwitchDriver(Arg(0)),
            "setAutoKeepMic" => _backend.SetAutoKeepMic(Arg(0)),
            "checkCaptureDevice" => _backend.CheckCaptureDevice(),
            "learnClickIcon" => _backend.LearnClickIcon(),
            "installWinuhid" => _backend.InstallWinuhid(),
            "exportWinuhid" => _backend.ExportWinuhid(),
            _ => Env.Json(new { ok = false, message = $"未知方法 {method}" }),
        };

        var resp = new JsonObject { ["id"] = idNode, ["result"] = result };
        // 界面进程可能已被释放（窗口在托盘里）—— 这时候没有页面在等回包，直接丢弃
        _web?.CoreWebView2?.PostWebMessageAsJson(resp.ToJsonString());
    }

    // ───────── 自检 ─────────

    private async Task<bool> RunSelfTest(bool fallback = false)
    {
        Trace($"RunSelfTest 进入（fallback={fallback}, done={_selfTestDone}）");
        if (_selfTestDone) return false;
        _selfTestDone = true;
        var errs = new JsonArray();

        // ⚠ 每段各自 try/catch：任何一段出问题都**照常出报告**并把出错段落记进去。
        //   以前整个方法共用一个 try —— 一个 NRE 就让报告只剩 {"error": "..."}，
        //   连"是哪个环节"都看不出来，等于白跑。
        try
        {
            await Task.Delay(fallback ? 200 : 2500);
            Trace("延迟结束，开始采集");

            var js = "JSON.stringify({title:document.title," +
                     "cards:document.querySelectorAll('.card').length," +
                     "keyrows:document.querySelectorAll('.keyrow').length," +
                     "panels:document.querySelectorAll('.panel').length," +
                     "navbtns:document.querySelectorAll('.nav-btn').length," +
                     "labels:[...document.querySelectorAll('.keyrow .keyname')].slice(0,6).map(e=>e.textContent)," +
                     "groups:[...document.querySelectorAll('.keygroup')].map(e=>e.textContent)," +
                     "backend:!!B," +
                     "hits:document.querySelectorAll('#remoteStage .keyhit').length," +
                     "thumbs:document.querySelectorAll('.keythumb').length," +
                     "thumbBg:(document.querySelector('.keythumb')||{}).getAttribute?.('style')||''," +
                     "sym:(document.querySelector('.keybind-sym')||{}).textContent||''," +
                     "mask:!!document.querySelector('#comboMask')," +
                     "modal:!!document.querySelector('#comboMask .modal')," +
                     "rectOk:(()=>{const h=document.querySelector('#remoteStage .keyhit[data-id=ok]');" +
                     "return h?h.style.top+'/'+h.style.left:'(none)';})()})";

            // ★ 这一步是 async，不能套进 SafeNode（那会拿到 Task 而不是字符串），
            //   而且 **ExecuteScriptAsync 有可能永远不返回**（页面正在导航/被对话框挡住时），
            //   所以必须加超时 —— 否则整个自检会静默卡死，连报告都出不来。
            var probe = "";
            try
            {
                var core = _web?.CoreWebView2 ?? throw new InvalidOperationException("WebView2 未就绪");
                var exec = core.ExecuteScriptAsync(js);
                var done = await Task.WhenAny(exec, Task.Delay(6000));
                probe = done == exec
                    ? await exec
                    : "\"<timeout: ExecuteScriptAsync 6s 未返回>\"";
            }
            catch (Exception e) { probe = $"\"<error: {e.GetType().Name}: {e.Message}>\""; }

            JsonNode SafeNode(string name, Func<JsonNode> f)
            {
                try { return f(); }
                catch (Exception e)
                {
                    var where = new System.Diagnostics.StackTrace(e, false).GetFrames()
                        ?.Select(fr => fr.GetMethod()?.Name)
                        .FirstOrDefault(n => n is not null && n != "SafeNode") ?? "?";
                    errs.Add($"{name}: {e.GetType().Name} @{where}() — {e.Message}");
                    return new JsonObject { ["error"] = e.Message };
                }
            }

            var report = new JsonObject
            {
                ["dotnet"] = Environment.Version.ToString(),
                ["fallback"] = fallback,
                ["source"] = SafeNode("source", () => _web?.CoreWebView2?.Source ?? "(界面进程已释放)"),
                ["resources"] = SafeNode("resources", () => new JsonObject
                {
                    ["VBCABLE_Driver_Pack45.zip"] = Env.FindAsset("VBCABLE_Driver_Pack45.zip") != null,
                    ["configure-xiaomi-audio.ps1"] = Env.FindAsset("configure-xiaomi-audio.ps1") != null,
                    ["etw_consumer.exe"] = Env.FindAsset("etw_consumer.exe") != null,
                    ["uiIndexHtml"] = Env.FindAsset("index.html") != null,
                }),
                ["jsProbe"] = probe,
                ["state"] = SafeNode("getState",
                    () => JsonNode.Parse(_backend.GetState()) ?? new JsonObject()),
                ["errors"] = errs,
            };
            File.WriteAllText(SelfTestPath, Env.JsonPretty(report));
            Trace($"报告已写入 {SelfTestPath}（{new FileInfo(SelfTestPath).Length} 字节）");
        }
        catch (Exception ex)
        {
            Trace($"采集异常 {ex.GetType().Name}: {ex.Message}");
            try
            {
                // ⚠ errs 已经挂在 report 上了（JsonNode 有父指针），
                //   直接复用会抛「The node already has a parent」→ 错误报告也写不出去，
                //   最后连"为什么失败"都看不到。所以这里必须**深拷贝**一份。
                var errs2 = new JsonArray();
                foreach (var n in errs) errs2.Add(n?.DeepClone());

                File.WriteAllText(SelfTestPath, Env.JsonPretty(new JsonObject
                {
                    ["error"] = ex.Message,
                    ["type"] = ex.GetType().Name,
                    ["errors"] = errs2,
                    ["detail"] = ex.ToString().Replace("\r", "").Replace("\n", " | "),
                }));
                Trace("错误报告已写入");
            }
            catch (Exception e2)
            {
                // 连 JSON 都写不出来时，退化成纯文本 —— 无论如何都要留下线索
                Trace($"错误报告也写不进去: {e2.GetType().Name}: {e2.Message}");
                try
                {
                    File.WriteAllText(SelfTestPath,
                        "{\"error\":\"" + ex.Message.Replace("\"", "'") + "\"}");
                    Trace("已退化为纯文本报告");
                }
                catch { }
            }
        }
        return true;
    }

    // ── Raw Input（WM_INPUT）+ 拦截器生命周期 ──

    private const int WM_INPUT = 0x00FF;

    private const int WM_DEVICECHANGE = 0x0219;
    private const int DBT_DEVICEREMOVECOMPLETE = 0x0004;
    private const int DBT_DEVNODES_CHANGED = 0x0007;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_INPUT)
        {
            try { _filter.HandleRawInput(m.LParam); } catch { /* 不打断消息循环 */ }
        }
        else if (m.Msg == WM_DEVICECHANGE)
        {
            // 设备插拔事件（蓝牙断开/拔电池都会来）→ 若遥控器已不在，立即解锁卡住的键
            var ev = m.WParam.ToInt32() & 0xFFFF;
            if (ev is DBT_DEVICEREMOVECOMPLETE or DBT_DEVNODES_CHANGED)
            {
                try { _backend.OnDeviceMayChanged(); } catch { }
            }
        }
        base.WndProc(ref m);
    }

    /// <summary>界面还没建好时，先记下"打开后要跳到哪一页"，等渲染完成再执行</summary>
    private string? _pendingPage;

    /// <summary>
    /// 让前端切页。界面已就绪就直接执行；还没建好就挂起，
    /// 等 `NavigationCompleted`（页面渲染完成）再执行 —— 否则脚本会打在空页面上。
    /// </summary>
    private void ShowPage(string page)
    {
        if (_webReady && _web?.CoreWebView2 != null)
        {
            try { _ = _web.CoreWebView2.ExecuteScriptAsync($"goto('{page}')"); } catch { }
        }
        else _pendingPage = page;
    }

    private void RestoreFromTray(bool showSettings = false)
    {
        if (showSettings) ShowPage("env");     // 设置页 = page-env
        // ★ 顺序：**先**把 WindowState 改回 Normal，**再** Show。
        //   反过来的话 `Show()` 触发的 OnVisibleChanged 里拿到的还是 Minimized，
        //   会被下面那条"最小化时不建界面"的守卫挡掉，界面就永远建不出来。
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Show();
        Activate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        // 最小化 → 收进托盘（任务栏不再占位）
        if (WindowState == FormWindowState.Minimized) Hide();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        // ★ 窗口收进托盘后，前端每 2 秒一次的 getState() + 三次 DOM 重绘纯属浪费
        //   （本程序绝大部分时间都待在托盘里）。让它停掉；重新露出时再补刷。
        //   两个入口（最小化 / 点 X 收托盘）都走 Hide()，这里一处就够。
        SetUiVisible(Visible);

        // ⚠ 这里必须排除 `Minimized`：启动到托盘时框架会**先让窗口可见、再 Hide()**
        //   （2026-10-17 实测：靠这条时序绕过了构造函数里的延迟，WebView2 照样在 293 ms 被建起来）。
        //   只有"真的在屏幕上（Normal/Maximized）"才补建。
        if (Visible && WindowState != FormWindowState.Minimized)
        {
            // 露出来了：取消隐藏计时；若界面进程已被释放（或还没初始化成功），就地补建
            _hiddenSince = DateTime.MinValue;
            if (!_webReady && !_releaseDisabled)
            {
                BeginInvoke(new Action(async () =>
                {
                    try { await EnsureWebViewAsync(); }
                    catch (Exception ex)
                    {
                        // 重建失败 → 永久停用「自动释放」，避免每开一次窗就报一次错
                        _releaseDisabled = true;
                        WebViewLog($"★ 重建失败，已停用自动释放：{ex.GetType().Name}: {ex.Message}");
                        MessageBox.Show(
                            $"界面重建失败，已停止自动释放界面进程（本次重启内不再释放）。\n\n{ex.Message}",
                            Env.AppName);
                    }
                }));
            }
        }
        else if (_hiddenSince == DateTime.MinValue)
        {
            _hiddenSince = DateTime.Now;      // 开始计时
        }
    }

    private async void SetUiVisible(bool v)
    {
        try
        {
            if (_web?.CoreWebView2 == null) return;
            await _web.CoreWebView2.ExecuteScriptAsync(
                $"window.setUiVisible({(v ? "true" : "false")})");
        }
        catch { }   // WebView2 尚未就绪时忽略，下次可见性变化会再试
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_reallyExit)
        {
            // 点右上角 X = 最小化到托盘，程序继续在后台跑（语音桥不中断）
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _filter.Dispose();
            _backend.Dispose();
        }
        base.Dispose(disposing);
    }
}
