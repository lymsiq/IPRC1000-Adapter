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
    // client 模式不启音频（UDP 31680/31681 归业务进程独占，否则两进程抢端口）
    private readonly Backend _backend = new(startAudio: !ClientMode);
    /// <summary>`--ui-client`：宿主只做 UI，业务在独立进程（命名管道通信）</summary>
    internal static readonly bool ClientMode =
        Environment.GetCommandLineArgs().Any(a => a == "--ui-client");

    private readonly KeyFilter? _filter;
    private readonly NotifyIcon _tray;
    /// <summary>是否已经提示过"收进托盘"（每轮启动只提示一次）</summary>
    private bool _trayHintShown;
    private bool _reallyExit;
    private readonly bool _selftest;
    /// <summary>
    /// 端到端测试模式（`IPRC1000_WEBVIEW_DEBUG` 有值即开）。
    /// 作用：**不启动到托盘**，直接把界面建出来 —— 否则 WebView 不存在，
    /// Playwright 连不上 CDP，也测不了任何按钮。
    /// 生产环境默认关闭（环境变量不设就没有任何影响）。
    /// </summary>
    private readonly bool _e2e =
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("IPRC1000_WEBVIEW_DEBUG"));
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

    /// <summary>
    /// **安装/卸载前的"请退出"请求**（2026-10-29）。
    ///
    /// 为什么需要：升级时应用还在跑 ⇒ 文件被占用、复制失败；若强行 taskkill，
    /// **系统蓝牙栈会失去响应，只能重启电脑**（同类项目明确记录过这个事故）。
    /// 所以安装器改为：Set 这个事件 → 等最多 10 秒 → 超时也**不强杀**，提示用户手动退出。
    /// </summary>
    public const string ExitRequestEvent = @"Local\IPRC1000Adapter.ExitRequest";
    private EventWaitHandle? _exitEvent;
    private Thread? _exitThread;
    private EventWaitHandle? _showEvent;
    private Thread? _showThread;

    /// <summary>接住安装器的"请退出"请求：正常走退出流程（释放注入键、停桥），绝不强杀。</summary>
    private void StartExitRequestListener()
    {
        try
        {
            _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitRequestEvent);
            _exitThread = new Thread(() =>
            {
                while (!_reallyExit)
                {
                    try
                    {
                        if (!_exitEvent.WaitOne(1000)) continue;
                        if (IsDisposed) break;
                        Safe.Note("收到安装器的退出请求 → 正常退出（释放注入键、停桥）");
                        try
                        {
                            BeginInvoke(new Action(() =>
                            {
                                _reallyExit = true;
                                try { Safe.FlushNotes(); } catch { }
                                Close();
                                Application.Exit();
                            }));
                        }
                        catch { break; }
                    }
                    catch { break; }
                }
            })
            { IsBackground = true, Name = "iprc1000-exit-request" };
            _exitThread.Start();
        }
        catch (Exception e) { Safe.Note("退出请求监听启动失败：" + e.Message); }
    }

    /// <summary>接住"第二个实例要求显示窗口"的通知（后台线程等待，回 UI 线程执行）。</summary>
    private void StartShowWindowListener()
    {
        try
        {
            // 注：想给这个 named event 加 DACL（让普通权限的第二实例也能唤出管理员实例），
            // 但 `EventWaitHandle(…, EventWaitHandleSecurity)` 需要额外的 System.Threading.AccessControl 包，
            // 而"再启动一次"失败时 Program 已经会提示"请到托盘双击打开" —— 够用，故不引入新依赖。
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

        // ★ 2026-10-20（用户指出"托盘图标和程序图标不统一"）：
        //   exe 里有图标（<ApplicationIcon>），但 **icon.ico 没被打进发布目录**
        //   ⇒ `FindAsset` 返回 null ⇒ 托盘退回 `SystemIcons.Application`（通用"两个小窗口"）。
        //   所以这里补一条**不依赖外部文件**的兜底：直接从 exe 资源里取图标。
        var ico = Env.FindAsset("icon.ico");
        if (ico != null)
        {
            Icon = new Icon(ico);
        }
        else
        {
            try
            {
                var fromExe = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (fromExe != null) Icon = fromExe;
            }
            catch { }
        }

        // ── 系统托盘：关闭窗口 = 最小化到托盘，右键菜单可真正退出 ──
        _tray = new NotifyIcon
        {
            Icon = Icon ?? SystemIcons.Application,
            Text = Env.AppName,
            Visible = true,
        };
        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("显示主窗口", null, (_, _) => RestoreFromTray());
        // ★ 2026-10-23：`forceUnlock`（紧急释放卡住的键）**后端一直有、界面从来没入口** ——
        //   卡键时用户只能重启程序。接到托盘是最轻的补救（不必开面板）。
        trayMenu.Items.Add("紧急释放卡住的按键", null, (_, _) =>
        {
            try
            {
                var r = System.Text.Json.JsonDocument.Parse(_backend.ForceUnlock());
                var msg = r.RootElement.TryGetProperty("message", out var m) ? m.GetString() : null;
                _tray.ShowBalloonTip(3000, "IPRC1000", msg ?? "已执行", ToolTipIcon.Info);
            }
            catch { }
        });
        trayMenu.Items.Add("-");
        trayMenu.Items.Add("退出", null, (_, _) =>
        {
            _reallyExit = true;
            try { Safe.FlushNotes(); } catch { }   // 异步日志：退出前补写最后一段
            Application.Exit();
        });
        _tray.ContextMenuStrip = trayMenu;
        // 双击 = 打开并**直接切到设置页**（用户要求）；单击 = 只打开
        _tray.DoubleClick += (_, _) => RestoreFromTray(showSettings: true);
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) RestoreFromTray();
        };

        // 单实例：第二个实例会 Set 事件要求我们把窗口显示出来
        StartShowWindowListener();
        StartExitRequestListener();

        // 启动时最小化到托盘（默认开）；E2E 测试模式例外，必须把界面建出来
        if (Settings.StartMinimized && !_e2e)
        {
            WindowState = FormWindowState.Minimized;
            Load += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized) Hide();
            // F10 = 开始录制（常驻注册；F11 只在录制期间持有，见 SetStopHotkey）
            try
            {
                _recHotkeyStart = RegisterHotKey(Handle, HK_REC_START, 0, VK_F10);
                Safe.Note(_recHotkeyStart
                    ? "HOTKEY 录制热键已就绪：F10 开始录制（录制中 F11 结束）"
                    : "HOTKEY F10 注册失败（可能被别的程序占用）");
            }
            catch { }
        };
        }

        // ★★ 2026-10-29 进程拆分收尾：`--ui-client` 时宿主**只做 UI**，
        //    不建 KeyFilter、不监听 UDP、不注入、不启音频 —— 全部交给业务进程（--headless）。
        if (!ClientMode)
        {
            _filter = new KeyFilter();
            _filter.OnButton += (bid, pressed) => _backend.NoteButton(bid, pressed);
            _backend.AttachFilter(_filter);
        }

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

        // ★ 宏的开始/结束要有**即时反馈**。KeymouseGo 的 #124 就是抱怨
        //   "首行等待很长时，没法判断脚本到底起没起"——宏有长等待时同样有这个盲区。
        //   用托盘气泡而不是只靠界面 toast：用户跑宏时通常在别的程序里，看不到我们界面。
        //   Notify 在播放线程上触发，必须切回 UI 线程再碰托盘。
        MacroPlayer.Notify += (msg) =>
        {
            try
            {
                if (!IsHandleCreated) return;
                // 失败消息要显眼：日志默认是关的（LogEnabled=false），
                // 托盘气泡是用户**唯一**能看到"宏为什么没跑"的地方。
                var bad = msg.Contains("失败") || msg.Contains("没找到") || msg.Contains("中止")
                       || msg.Contains("中断") || msg.Contains("第 ");
                BeginInvoke(new Action(() =>
                {
                    try
                    {
                        _tray.ShowBalloonTip(bad ? 6000 : 2000, Env.AppName, msg,
                                             bad ? ToolTipIcon.Warning : ToolTipIcon.None);
                    }
                    catch { }
                }));
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
            // 2026-10-27：这里原来写"装 Raw Input + 键盘钩子" —— 两者都已删除
            //   （钩子 v1.2.63 移除、Raw Input v1.2.70 移除），日志文案会误导排查。
            _filter?.Start(Handle);   // client 模式下没有本地 KeyFilter（业务在别的进程）
            StartupMark("按键来源已启动（ETW；无钩子、无 Raw Input）");

            // ★ 启动兜底：清掉**上一个实例**被强杀时留在系统里的卡住修饰键
            //   （退出路径在 taskkill /F / 崩溃时跑不到，那台虚拟键盘是上一个进程的设备，
            //     我们的全零报告清不掉它 —— 只能补 SendInput 的 KEYUP）
            try
            {
                var stale = KeyFilter.ClearStaleKeysOnStart();
                if (stale != null)
                {
                    StartupMark($"启动兜底：清掉了残留的按键 {stale}");
                    // 让用户知道"上次卡住的键已经解开了"——否则他只会觉得"这次又卡了"
                    try
                    {
                        _tray.Visible = true;
                        _tray.ShowBalloonTip(5000, "已释放上次残留的按键",
                            $"检测到系统里还有按下的键（{stale}），已自动释放。\n" +
                            "通常是上次程序被强制结束时留下的。", ToolTipIcon.Info);
                    }
                    catch { }
                }
            }
            catch { }
            // ⚠ InitWebView 是 async void 语义：**必须自己 try/catch**，
            //   否则它内部任何异常都会掀翻进程（0xE0434352），且不留任何痕迹。
            //
            // ★ 只有窗口**真的可见**时才建界面。启动到托盘（StartMinimized，开机自启走这条）
            //   时不建 —— 省 341 ms + 356 MB。露出来时由 OnVisibleChanged 补建。
            //   `_selftest` 例外：自检要读界面，必须建。
            if (!Settings.StartMinimized || _selftest || _e2e)
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
        // client 模式下**不初始化本地业务**（UDP/音频/注入都在业务进程里）
        if (!ClientMode) Shown += (_, _) => Task.Run(() => _backend.InitAsync());

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
        // ★ 2026-10-27：**仅测试用**的远调试端口。设了 IPRC1000_WEBVIEW_DEBUG=9222
        //   才启用，默认完全关闭（端口会把页面暴露给本机任意进程，不能常开）。
        //   用途：Playwright 通过 CDP 连上**真实运行**的界面做点击验证
        //   （静态探针只测渲染，测不出"点了之后后端有没有真生效"）。
        var dbgPort = Environment.GetEnvironmentVariable("IPRC1000_WEBVIEW_DEBUG");
        var dbgArgs = string.IsNullOrWhiteSpace(dbgPort)
            ? null
            : $"--remote-debugging-port={dbgPort}";   // 也可用 1 走默认 9222
        if (dbgArgs != null && dbgPort == "1") dbgArgs = "--remote-debugging-port=9222";

        // ⚠ WebView2 的**环境按 UserDataFolder 缓存**：同一目录复用已有环境时，
        //   AdditionalBrowserArguments 会被忽略（调试端口因此不生效）。
        //   所以调试模式换个独立目录，强制新建环境；正常模式路径不变。
        web.CreationProperties = new CoreWebView2CreationProperties
        {
            UserDataFolder = dbgArgs != null ? dataDir + "-e2e" : dataDir,
            AdditionalBrowserArguments = dbgArgs,
        };
        await web.EnsureCoreWebView2Async();
        StartupMark("WebView2 环境就绪（Chromium 已起来，这段通常是启动里最大的一块）");

        web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        // ★★ 永远禁用 DevTools（2026-10-30 修）。
        //   原来写的是 `!_selftest`：自检时禁用、**正常运行时是开着的** ——
        //   而 F12 是 Chromium 的 DevTools 快捷键，用户按了 F12 就弹出开发者工具。
        //   （1.1.0 的审查结论就要求"DevTools 必须禁用"，当时没落到代码里。）
        //   自检时反而**要**打开：--macro-test 要看控制台输出。
        web.CoreWebView2.Settings.AreDevToolsEnabled = _selftest;
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

        // ★ 2026-10-29：命令分发已抽到 Backend.Dispatch（宿主与无界面进程共用同一份）。
        // client 模式：命令发到业务进程（命名管道），宿主自己什么都不执行
        string result = ClientMode
            ? ServiceClient.Call(method, Arg(0), Arg(1), 15000)
            : _backend.Dispatch(method, Arg(0), Arg(1));

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


    // ── 录制热键（2026-10-30）────────────────────────────────────────────
    // F10 = 开始录制，F11 = 结束录制。用户要求：录制时人在目标程序里，
    // 不可能回来点按钮。
    // ⚠ 全局热键是**独占**的：注册后其他程序收不到这个键。
    //   F10 影响小（浏览器按它只出菜单栏），所以常驻；
    //   F12 会直接弹出 DevTools（已禁用 DevTools，但 F11 更稳妥），改用 **F11**，
    //   也只在**录制期间**注册，停止就注销。
    private const int WM_HOTKEY = 0x0312;
    private const int HK_REC_START = 0xA001;
    private const int HK_REC_STOP = 0xA002;
    private const int VK_F10 = 0x79, VK_F11 = 0x7A;   // F11 = 结束录制（F12 是 DevTools/开发者工具的键）

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private bool _recHotkeyStart, _recHotkeyStop;

    /// <summary>注册/注销「结束录制」热键。只在录制期间持有 F11。</summary>
    private void SetStopHotkey(bool on)
    {
        try
        {
            if (on && !_recHotkeyStop) _recHotkeyStop = RegisterHotKey(Handle, HK_REC_STOP, 0, VK_F11);
            else if (!on && _recHotkeyStop) { UnregisterHotKey(Handle, HK_REC_STOP); _recHotkeyStop = false; }
        }
        catch { }
    }

    /// <summary>
    /// F10：开始录制。录制时用户人在目标程序里，不可能回来点按钮 —— 这就是加全局热键的原因。
    /// 启动后**立刻持有 F12**，这样用户录完直接按 F12 就能停，不用切窗口。
    /// </summary>
    private void StartRecordByHotkey()
    {
        if (Recorder.IsRecording || MacroPlayer.IsRunning) return;
        var msg = Recorder.Start();
        SetStopHotkey(Recorder.IsRecording);
        try { _tray.ShowBalloonTip(3000, Env.AppName, msg, ToolTipIcon.Info); } catch { }
    }

    /// <summary>
    /// F11：结束录制。**不自动把步骤塞进宏** —— 那需要知道"加到哪个键的宏里"，
    /// 而这时用户可能连弹窗都没开。步骤留在缓冲里，回界面点按钮取回。
    /// </summary>
    private void StopRecordByHotkey()
    {
        if (!Recorder.IsRecording) return;
        var msg = Recorder.Stop();
        SetStopHotkey(false);
        var tail = Recorder.EventCount > 0
            ? $"（{Recorder.EventCount} 个事件，回界面点「取回刚录的」）"
            : "";
        try { _tray.ShowBalloonTip(4000, Env.AppName, msg + tail, ToolTipIcon.Info); } catch { }
    }

    private const int WM_DEVICECHANGE = 0x0219;
    private const int DBT_DEVICEREMOVECOMPLETE = 0x0004;
    private const int DBT_DEVNODES_CHANGED = 0x0007;

    protected override void WndProc(ref Message m)
    {
        // 2026-10-27：`WM_INPUT` 分支删除 —— Raw Input 从未注册（RegisterRawInputDevices 从未调用），
        //   这条分支永远收不到消息；按键只走 ETW。
        if (m.Msg == WM_HOTKEY)
        {
            var id = m.WParam.ToInt32();
            try
            {
                if (id == HK_REC_START) StartRecordByHotkey();
                else if (id == HK_REC_STOP) StopRecordByHotkey();
            }
            catch { }
        }

        if (m.Msg == WM_DEVICECHANGE)
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

        // ★ 越界保护（2026-10-19 真机："窗口找不到了"）：
        //   最小化窗口的 rect 是 (-32000,-32000)；换显示器、拔掉副屏、分辨率变小之后，
        //   保存的位置也可能落在**任何屏幕之外** —— 这时"恢复成功"但用户什么都看不见。
        //   判据：与虚拟桌面无交集、或尺寸异常 ⇒ 挪回主屏中央。
        try
        {
            var vs = SystemInformation.VirtualScreen;
            if (!vs.IntersectsWith(Bounds) || Bounds.Width < 200 || Bounds.Height < 150)
            {
                var wa = Screen.PrimaryScreen?.WorkingArea ?? vs;
                var w = Math.Min(1200, Math.Max(640, wa.Width - 80));
                var h = Math.Min(800, Math.Max(480, wa.Height - 80));
                Bounds = new Rectangle(wa.Left + (wa.Width - w) / 2,
                                       wa.Top + (wa.Height - h) / 2, w, h);
                EtwKeySource.AppendLog($"DEV  窗口位置越界 ⇒ 已移回主屏中央（{Bounds}）");
            }
        }
        catch { }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        // 最小化 → 收进托盘（任务栏不再占位）
        if (WindowState == FormWindowState.Minimized)
        {
            Hide();
            // ★ 2026-10-19：真机出现过"关掉窗口后找不到程序了"。
            //   收进托盘时给一次气泡提示（每轮启动只提示一次，不烦人）。
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                try
                {
                    _tray.Visible = true;
                    _tray.ShowBalloonTip(4000, "已收进托盘",
                        "程序还在运行。要打开面板：双击右下角托盘里的这个图标。",
                        ToolTipIcon.Info);
                }
                catch { }
            }
        }
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
            _filter?.Dispose();
            _backend.Dispose();
        }
        base.Dispose(disposing);
    }
}
