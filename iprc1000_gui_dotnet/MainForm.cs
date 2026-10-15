using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Iprc1000Adapter;

internal sealed class MainForm : Form
{
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly Backend _backend = new();
    private readonly KeyFilter _filter;
    private readonly NotifyIcon _tray;
    private bool _reallyExit;
    private readonly bool _selftest;
    private string? _selftestOut;

    public MainForm(bool selftest, string? selftestOut)
    {
        _selftest = selftest;
        _selftestOut = selftestOut;

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
        _tray.DoubleClick += (_, _) => RestoreFromTray();

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

        Controls.Add(_web);

        Load += (_, _) =>
        {
            // 句柄此刻已就绪，再装 Raw Input + 键盘钩子
            _filter.Start(Handle);
            // ⚠ InitWebView 是 async void 语义：**必须自己 try/catch**，
            //   否则它内部任何异常都会掀翻进程（0xE0434352），且不留任何痕迹。
            BeginInvoke(new Action(async () =>
            {
                try { await InitWebView(); }
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
        };

        // ★ 窗口显示之后再在后台做重活：探针 → 自动切驱动（约 1~2 秒）→ 拉起语音桥。
        //   原来这些都在界面线程上跑，所以启动后要卡一两秒；现在界面立即可用。
        Shown += (_, _) => Task.Run(() => _backend.InitAsync());

        // 进程退出兜底：无论如何都把注入的键放掉（正常关闭走 Dispose，异常退出走这里）
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { KeyFilter.ReleaseAllInjected(); } catch { }
        };
    }

    private async Task InitWebView()
    {
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
        _web.CreationProperties = new CoreWebView2CreationProperties { UserDataFolder = dataDir };
        await _web.EnsureCoreWebView2Async();

        _web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        _web.CoreWebView2.Settings.AreDevToolsEnabled = !_selftest;
        _web.CoreWebView2.Settings.IsStatusBarEnabled = false;
        _web.CoreWebView2.WebMessageReceived += OnWebMessage;

        var uiDir = Path.Combine(Env.AppDir, "ui");
        Directory.CreateDirectory(uiDir);
        // 用虚拟主机映射，页面有正常 origin，比 file:// 更稳
        _web.CoreWebView2.SetVirtualHostNameToFolderMapping(
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
        _web.CoreWebView2.NavigationStarting += (_, e) =>
        {
            if (IsLocalUi(e.Uri)) return;
            e.Cancel = true;
            Trace($"[nav-guard] 已拦截外部导航：{e.Uri}");
        };
        _web.CoreWebView2.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;                     // 一律不开新窗口（外链交给系统浏览器走 openUrl）
            Trace($"[nav-guard] 已拦截新窗口：{e.Uri}");
        };

        if (_selftest)
        {
            Trace($"selftest 块已进入；报告路径={SelfTestPath}；AppDir={Env.AppDir}");
            _web.CoreWebView2.NavigationCompleted += async (_, _) =>
            {
                Trace("NavigationCompleted 触发");
                // 启动即打开指定页（IPRC1000_START_PAGE=overview|keys|env）—— 便于验证与固化首页
                var startPage = Environment.GetEnvironmentVariable("IPRC1000_START_PAGE");
                if (!string.IsNullOrWhiteSpace(startPage))
                {
                    try
                    {
                        await _web.CoreWebView2.ExecuteScriptAsync(
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
        _web.CoreWebView2.Navigate("https://iprc1000.local/index.html");
    }

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
            "setAutoStart" => _backend.SetAutoStart(Arg(0)),
            "clearKeyLog" => _backend.ClearKeyLog(),
            "beginBind" => _backend.BeginBind(Arg(0)),
            "cancelBind" => _backend.CancelBind(),
            "setAutoRun" => _backend.SetAutoRun(Arg(0)),
            "setStartMinimized" => _backend.SetStartMinimized(Arg(0)),
            "setAutoSwitchDriver" => _backend.SetAutoSwitchDriver(Arg(0)),
            "setAutoKeepMic" => _backend.SetAutoKeepMic(Arg(0)),
            "checkCaptureDevice" => _backend.CheckCaptureDevice(),
            "installWinuhid" => _backend.InstallWinuhid(),
            "exportWinuhid" => _backend.ExportWinuhid(),
            _ => Env.Json(new { ok = false, message = $"未知方法 {method}" }),
        };

        var resp = new JsonObject { ["id"] = idNode, ["result"] = result };
        _web.CoreWebView2.PostWebMessageAsJson(resp.ToJsonString());
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
                var exec = _web.CoreWebView2.ExecuteScriptAsync(js);
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
                ["source"] = SafeNode("source", () => _web.CoreWebView2.Source),
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

    private void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
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
