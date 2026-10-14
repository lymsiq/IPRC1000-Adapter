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
            BeginInvoke(new Action(async () => await InitWebView()));
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

        // 允许外部注入 Chromium 参数（沙箱/无 GPU 环境调试用），WebView2 会读这个环境变量
        var extra = Environment.GetEnvironmentVariable("IPRC1000_WEBVIEW_ARGS");
        if (!string.IsNullOrWhiteSpace(extra))
            Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", extra);

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

        if (_selftest)
        {
            _web.CoreWebView2.NavigationCompleted += async (_, _) =>
            {
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
                if (await RunSelfTest()) Close();
            };
            // 兜底：页面没加载出来也要出报告，否则只能干等到超时
            var t = new System.Windows.Forms.Timer { Interval = 18000 };
            t.Tick += async (_, _) =>
            {
                t.Stop();
                if (await RunSelfTest(true)) Close();
            };
            t.Start();
        }
        _web.CoreWebView2.Navigate("https://iprc1000.local/index.html");
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
        if (_selfTestDone) return false;
        _selfTestDone = true;
        try
        {
            await Task.Delay(fallback ? 200 : 2500);
            var js = "JSON.stringify({title:document.title," +
                     "cards:document.querySelectorAll('.card').length," +
                     "keyrows:document.querySelectorAll('.keyrow').length," +
                     "panels:document.querySelectorAll('.panel').length," +
                     "navbtns:document.querySelectorAll('.nav-btn').length," +
                     // 验证键位标注渲染：读前 6 行与分组标题
                     "labels:[...document.querySelectorAll('.keyrow .keyname')].slice(0,6).map(e=>e.textContent)," +
                     "groups:[...document.querySelectorAll('.keygroup')].map(e=>e.textContent)," +
                     "caps:[...document.querySelectorAll('.keyrow .keycap')].slice(0,6).map(e=>e.textContent)," +
                     "backend:!!B," +
                     // 新增按键映射页的验证项：遥控器热区数 / 行内缩略图数 / 缩略图的 sprite 定位 / 组合键符号
                     "hits:document.querySelectorAll('#remoteStage .keyhit').length," +
                     "thumbs:document.querySelectorAll('.keythumb').length," +
                     "thumbBg:(document.querySelector('.keythumb')||{}).getAttribute?.('style')||''," +
                     "sym:(document.querySelector('.keybind-sym')||{}).textContent||''," +
                     "mask:!!document.querySelector('#comboMask')," +
                     "modal:!!document.querySelector('#comboMask .modal')," +
                     "rectOk:(()=>{const h=document.querySelector('#remoteStage .keyhit[data-id=ok]');" +
                     "return h?h.style.top+'/'+h.style.left:'(none)';})()})";
            var probe = "";
            try { probe = await _web.CoreWebView2.ExecuteScriptAsync(js); }
            catch (Exception e) { probe = $"\"<error: {e.Message}>\""; }
            var report = new JsonObject
            {
                ["dotnet"] = Environment.Version.ToString(),
                ["fallback"] = fallback,
                ["source"] = _web.CoreWebView2.Source,
                ["resources"] = new JsonObject
                {
                    ["VBCABLE_Driver_Pack45.zip"] = Env.FindAsset("VBCABLE_Driver_Pack45.zip") != null,
                    ["configure-xiaomi-audio.ps1"] = Env.FindAsset("configure-xiaomi-audio.ps1") != null,
                    ["etw_consumer.exe"] = Env.FindAsset("etw_consumer.exe") != null,
                    ["uiIndexHtml"] = Env.FindAsset("index.html") != null,
                },
                ["jsProbe"] = probe,
                ["state"] = JsonNode.Parse(_backend.GetState()),
            };
            File.WriteAllText(_selftestOut ?? Path.Combine(Env.AppDir, "selftest.json"),
                report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            try
            {
                File.WriteAllText(_selftestOut ?? "selftest.json",
                    $"{{\"error\":\"{ex.Message.Replace("\"", "'")}\"}}");
            }
            catch { }
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
