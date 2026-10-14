using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Iprc1000Adapter;

/// <summary>前端可调用的后端：与 Python 版保持同一套方法与返回（JSON 字符串）。</summary>
internal sealed class Backend : IDisposable
{
    private readonly CableRouter _router = new();
    private Process? _bridge;
    private DateTime _bridgeStart;
    private readonly object _lock = new();

    private DateTime _probeAt = DateTime.MinValue;
    private object? _cableCache;
    private bool _cableReady, _micOk, _uhidReady;

    /// <summary>启动后自动拉起语音桥（环境就绪时），省得每次手动点（持久化）</summary>
    public bool AutoStartBridge
    {
        get => Settings.AutoStartBridge;
        set { Settings.AutoStartBridge = value; if (value) MaybeAutoStartBridge(); }
    }
    private DateTime _lastAutoStart = DateTime.MinValue;
    private (bool online, bool paired, List<object> hid, List<object> bth) _diagCache;

    public string LastKey { get; private set; } = "";
    public int LastVk { get; private set; }
    public DateTime LastKeyAt { get; private set; } = DateTime.MinValue;
    public string LastButton { get; private set; } = "";
    public bool LastButtonPressed { get; private set; }

    private KeyFilter? _filter;
    private EtwKeySource? _etwKeys;
    private string _keySource = "hook";   // hook | etw

    /// <summary>来源自动选择/安全兜底触发时的说明（给界面显示）</summary>
    public string? _keySourceGuard;

    public Backend()
    {
        _router.Start();   // 常驻：随时接收语音桥推来的 PCM
    }

    public void AttachFilter(KeyFilter f)
    {
        _filter = f;
        _filter.InjectEnabled = true;
        // ETW 按键来源常驻监听（被动、无副作用）；是否真正生效由按键来源模式决定
        StartProbeThread();
        _etwKeys = new EtwKeySource(EtwKeySource.DefaultPort, OnUsage);
        _etwKeys.Start();

    }

    /// <summary>统一的 usage 事件入口（ETW 与 HID 直读两条路共用）</summary>
    private void OnUsage(byte usage, bool down)
    {
        // 幽灵报告（0x0C）不当"当前操作"显示，免得盖住真实按键
        if (usage == 0x0C) return;
        LastKey = EtwKeySource.UsageToButton.TryGetValue(usage, out var n)
            ? Keys.LabelOf(n) : $"0x{usage:X2}";
        LastKeyAt = DateTime.UtcNow;

        // ★★★ 只有「按键来源＝ETW」时才在这里注入 ★★★
        //
        // 钩子模式下按键由 Raw Input 路径（KeyFilter.HandleRawInput）注入，而 ETW 采集器
        // 是**常驻监听**的 —— 若这里不加判断，一次物理按键会被注入两次
        // （Raw Input 一次 + ETW 一次），表现就是「按一次触发两次」。
        if (_keySource != "hook" && EtwKeySource.UsageToButton.TryGetValue(usage, out var bid))
            _filter?.ApplyButton(bid, down, "etw");
    }

    /// <summary>
    /// 立即解锁卡键：清空 held，并给**所有遥控器键位**补发一次 KEYUP。
    /// 用于「系统里某个键被锁在按下、一直自动重复」的紧急处理。
    /// </summary>
    public string ForceUnlock()
    {
        var n = _filter?.ClearHeld("手动强制解锁") ?? 0;
        // 再把遥控器全部键位都补一次 UP（不管当时在不在 held 里）
        var sent = 0;
        if (_filter != null)
        {
            foreach (var vk in Keys.VkToButtonId.Keys)
            {
                try { KeyFilter.InjectUp(new[] { (int)vk }); sent++; } catch { }
            }
        }
        return Env.Json(new { ok = true,
            message = $"已强制解锁：清空 {n} 个按住键，并给 {sent} 个遥控器键位补发了 KEYUP。" });
    }

    /// <summary>切换按键来源：hid = 直读 HID 设备（最低延迟）；etw = 从 BthPort ETW 读；hook = 内核钩子</summary>
    /// <summary>
    /// **按键来源自动选择 + 安全兜底**（每轮状态刷新都会跑）：
    ///
    /// - 设备**已不是键盘**（驱动换成 HID-compliant device）→ 自动切到 ETW 读键
    ///   （此时 Raw Input 根本收不到它，钩子模式等于聋了）
    /// - 设备**仍是键盘**却选了 ETW 模式 → 强制把钩子装回去
    ///   （否则遥控器按键会畅通无阻地打进前台窗口，就是"一直输出 m"）
    /// </summary>
    private void AutoSelectKeySource()
    {
        if (_filter == null) return;
        var stillKb = Env.RemoteStillKeyboard();

        if (!stillKb)
        {
            // 设备不是键盘 → 只能走 ETW（inbox 驱动下不存在可直读的 HID 接口）
            if (_keySource != "etw")
            {
                _keySource = "etw";
                _filter.SetSourceMode(true);
                _keySourceGuard = "设备已不是键盘 → 按键来源切到 ETW";
            }
        }
        else if (stillKb && _keySource != "hook" && _filter.KeySourceIsEtw)
        {
            _keySource = "hook";
            _filter.SetSourceMode(false);
            _keySourceGuard = "设备仍是键盘（驱动未换）且模式是 ETW → 已自动启用钩子拦截，否则 m 会漏进应用";
        }
    }

    public string SetKeySource(string? mode)
    {
        var m = (mode ?? "hook").ToLowerInvariant();
        _keySource = m is "etw" ? m : "hook";
        _keySourceGuard = null;
        _filter?.SetSourceMode(_keySource != "hook");
        AutoSelectKeySource();
        var desc = _keySource switch
        {
            "etw" => "按键来源已切到 ETW（设备为非键盘时使用）。",
            _ => "按键来源已切回内核钩子（Raw Input + WH_KEYBOARD_LL 拦截）。",
        };
        return Env.Json(new { ok = true, message = desc });
    }

    public void NoteKey(int vk)
    {
        LastVk = vk;
        LastKey = Keys.VkToButton.TryGetValue(vk, out var n) ? n : Keys.VkLabel(vk);
        LastKeyAt = DateTime.UtcNow;
    }

    public void NoteButton(string bid, bool pressed)
    {
        // ★ 遥控器最底部的「方块键」固定用于**切换到下一配置组**，循环切换、不注入任何按键。
        if (bid == "square" && pressed)
        {
            var g = Keymaps.NextGroup();
            _groupToast = $"已切换到配置组「{g}」";
            _groupToastAt = DateTime.UtcNow;
        }

        LastButton = bid;
        LastButtonPressed = pressed;
    }

    public string SetInject(string? on)
    {
        var want = on is "true" or "True" or "1" or "yes";
        if (_filter != null) _filter.InjectEnabled = want;
        return Env.Json(new { ok = true, message = want ? "已开启快捷键注入。" : "已关闭快捷键注入（仅拦截按键）。" });
    }

    // ───────── 状态 ─────────

    private volatile bool _probing;
    private Thread? _probeThread;
    private volatile bool _disposed;

    /// <summary>
    /// 起一个后台线程定时刷新探针。
    ///
    /// 原来探针是在 `GetState()` 里同步跑的，而 `GetState()` 由界面线程通过 JS 桥调用 ——
    /// 每轮轮询都要扫注册表（音频端点、Enum\HID、设备诊断），**界面会卡住**，
    /// 启动时尤其明显（一两秒的顿）。现在：界面只读缓存，后台线程负责刷新。
    /// </summary>
    private void StartProbeThread()
    {
        if (_probeThread != null) return;
        _probeThread = new Thread(() =>
        {
            while (!_disposed)
            {
                try { RefreshProbes(true); } catch { }
                for (var i = 0; i < 80 && !_disposed; i++) Thread.Sleep(100);   // 8 秒一轮
            }
        }) { IsBackground = true, Name = "iprc1000-probe", Priority = ThreadPriority.BelowNormal };
        _probeThread.Start();
    }

    /// <summary>
    /// 启动后的异步初始化：探针 → 自动切驱动 → 拉起语音桥。
    /// 全部放在后台线程，**界面先显示、不阻塞**（这是"启动后卡一两秒"的解法）。
    /// </summary>
    public void InitAsync()
    {
        try
        {
            // ★ 启动兜底：清掉上一次异常退出（例如被强杀）残留的「按着不放」的注入键。
            //   最要命的是右 Alt(AltGr) —— 卡住会让字母键输出特殊字符、中文输入法失效。
            try { KeyFilter.ReleaseAllInjected(); } catch { }
            RefreshProbes(true);
            AutoSwitchDriver();
            try { AutoSelectKeySource(); } catch { }
            try { MaybeAutoStartBridge(); } catch { }
        }
        catch { }
    }

    /// <summary>
    /// 启动时自动把遥控器驱动切成"非键盘"。
    ///
    /// 为什么：设备当键盘时，语音键会漏 `m`、幽灵报告会漏 `i`、拔电池还会把键锁在
    /// 「按下」持续刷屏；切成 HID-compliant device 后系统根本不产生这些 VK，从根上消失。
    /// 该操作约 1~2 秒，**必须在后台做**，否则界面会卡住。
    /// </summary>
    private void AutoSwitchDriver()
    {
        if (!Settings.AutoSwitchDriver) return;
        if (!Env.IsAdmin()) { _driverNote = "需要管理员权限才能自动切换驱动"; return; }
        if (!Env.RemoteStillKeyboard()) { _driverNote = null; return; }
        var (ok, msg) = Env.SetDeviceDriver(false);
        _driverNote = ok ? "启动时已自动把遥控器驱动切成「非键盘」（零 m）" : $"自动切驱动失败：{msg}";
        _probeAt = DateTime.MinValue;
        RefreshProbes(true);
    }

    /// <summary>自动切驱动的结果说明（给界面显示）</summary>
    private string? _driverNote;

    private void RefreshProbes(bool force)
    {
        if (!force && _cableCache != null && (DateTime.UtcNow - _probeAt).TotalSeconds < ProbeTtlSec) return;
        _cableCache = Env.CableState();
        var c = (dynamic)_cableCache;
        _cableReady = c.ready;
        _micOk = c.defaultMic;
        // ★ 不要在这里 EnsureReady()：它会创建虚拟 HID 设备，每轮轮询都做一次代价很大。
        //   只在驱动版本足够（>=1）时认为就绪，真正的设备在第一次注入时惰性创建。
        _uhidReady = WinUhidInjector.ProbeDriver();
        _diagCache = Env.RemoteDiag();
        _probeAt = DateTime.UtcNow;
    }

    private bool BridgeRunning => _bridge != null && !_bridge.HasExited;

    /// <summary>环境就绪就自动把语音桥拉起来（每 10 秒最多试一次）</summary>
    private void MaybeAutoStartBridge()
    {
        if (!AutoStartBridge || BridgeRunning) return;
        if (!Env.IsAdmin() || !_cableReady) return;
        if ((DateTime.UtcNow - _lastAutoStart).TotalSeconds < 10) return;
        _lastAutoStart = DateTime.UtcNow;
        StartBridge();
    }

    public string GetState()
    {
        AutoSelectKeySource();     // 每轮状态刷新：自动选来源 + 安全兜底
        RefreshProbes(false);
        MaybeAutoStartBridge();
        // 映射来自当前配置组（Keymaps）
        var items = Keys.Table.Select(b =>
        {
            var vks = Keymaps.Get(b.Id);
            return new
            {
                id = b.Id,
                label = b.Label,          // 遥控器上印的字符/图标（界面主显示）
                name = b.Name,            // 中文说明（仅悬停提示）
                hid = b.Hid,
                cap = b.Label,
                vks,
                binding = Keys.Format(vks),
                sym = Keys.FormatSymbols(vks),                 // 符号化组合键（列表右侧显示）
                rect = Keys.Rects.TryGetValue(b.Id, out var rc)
                    ? new { cx = rc.Cx, cy = rc.Cy, w = rc.W, h = rc.H } : null,
                img = new { w = Keys.ImgW, h = Keys.ImgH },
            };
        }).ToList();

        bool fresh = (DateTime.UtcNow - LastKeyAt).TotalSeconds < 4;
        bool connected = _diagCache.online;

        var state = new JsonObject
        {
            ["admin"] = Env.IsAdmin(),
            ["device"] = new JsonObject
            {
                ["connected"] = connected,
                ["paired"] = _diagCache.paired,
                ["detail"] = new JsonObject
                {
                    ["hid"] = JsonSerializer.SerializeToNode(_diagCache.hid),
                    ["bthenum"] = JsonSerializer.SerializeToNode(_diagCache.bth),
                    ["paired"] = _diagCache.paired,
                    ["online"] = connected,
                },
                ["lastKey"] = fresh ? LastKey : (connected ? (LastKey.Length > 0 ? LastKey : "—") : null),
                ["lastState"] = fresh ? "已翻译" : (connected ? "已连接" : "待机"),
            },
            ["keys"] = new JsonObject
            {
                ["items"] = JsonSerializer.SerializeToNode(items),
                ["configured"] = items.Count(i => i.vks.Length > 0),
                ["total"] = items.Count,
            },
            ["reg"] = JsonSerializer.SerializeToNode(Env.RegState()),
            ["cable"] = JsonSerializer.SerializeToNode(_cableCache),
            ["bridge"] = new JsonObject
            {
                ["running"] = BridgeRunning,
                ["frames"] = 0,
                ["samples"] = 0,
                ["seconds"] = BridgeRunning ? (int)(DateTime.UtcNow - _bridgeStart).TotalSeconds : 0,
                ["hint"] = BridgeRunning ? "正在监听 BthPort ETW" : "待启动",
            },
            ["router"] = JsonSerializer.SerializeToNode(_router.Status()),
            ["inject"] = _filter?.InjectEnabled ?? false,
            ["winuhid"] = JsonSerializer.SerializeToNode(WinUhidInjector.Status()),
            ["setup"] = SetupStatus(),
            ["stuckGuard"] = new JsonObject
            {
                ["unlockCount"] = UnlockCount,
                ["lastUnlock"] = LastUnlock,
                ["watchdogMs"] = KeyFilter.HoldTimeoutMs,
            },
            ["bind"] = new JsonObject
            {
                ["active"] = _bindBid != null,
                ["bid"] = _bindBid,
                ["capturedVk"] = _filter?.CapturedVk ?? 0,
                ["capturedName"] = _filter is { CapturedVk: > 0 }
                    ? Keys.VkLabel(_filter.CapturedVk) : "",
            },
            ["autoRun"] = IsAutoRun(),
            ["startMinimized"] = Settings.StartMinimized,
            ["autoStart"] = AutoStartBridge,
            ["autoSwitchDriver"] = Settings.AutoSwitchDriver,
            ["groups"] = new JsonObject
            {
                ["active"] = Keymaps.ActiveIndex,
                ["items"] = new JsonArray(Keymaps.Groups.Select(g => (JsonNode)new JsonObject
                {
                    ["name"] = g.Name,
                    ["configured"] = g.Map.Count(kv => kv.Value.Length > 0),
                }).ToArray()),
            },
            ["groupToast"] = (DateTime.UtcNow - _groupToastAt).TotalSeconds < 4 ? _groupToast : null,
            ["driverNote"] = _driverNote,
            ["keysource"] = new JsonObject
            {
                ["mode"] = _keySource,
                ["stillKeyboard"] = Env.RemoteStillKeyboard(),
                ["guard"] = _keySourceGuard,
                ["etw"] = JsonSerializer.SerializeToNode(_etwKeys?.Status()),
                ["logPath"] = EtwKeySource.EventsLogPath,
                ["hookActive"] = _filter is { Running: true, KeySourceIsEtw: false },
            },
            ["swallow"] = new JsonObject
            {
                ["running"] = _filter?.Running ?? false,
                ["deviceSeen"] = _filter?.DeviceSeen ?? false,
                ["swallowed"] = _filter?.Swallowed ?? 0,
                ["passed"] = _filter?.Passed ?? 0,
                ["error"] = _filter?.LastError,
                ["sinceLastInputMs"] = _filter?.SinceLastInputMs ?? -1,
                ["spuriousLeaked"] = _filter?.SpuriousLeaked ?? 0,
                ["lastCleared"] = _filter?.LastCleared,
                ["lastButton"] = LastButton,
                ["pressed"] = LastButtonPressed,
            },
        };
        return state.ToJsonString();
    }

    // ───────── 动作 ─────────

    public string ApplyRegistry()
    {
        var (ok, msg) = Env.ApplyRegistry();
        return Env.Json(new { ok, message = msg });
    }

    public string Reboot()
    {
        try
        {
            Process.Start(new ProcessStartInfo("shutdown", "/r /t 8 /c \"IPRC1000 Adapter: 重启以加载蓝牙 ETW 配置\"")
            { UseShellExecute = false, CreateNoWindow = true });
            return Env.Json(new { ok = true, message = "系统将在 8 秒后重启…" });
        }
        catch (Exception e) { return Env.Json(new { ok = false, message = $"重启失败：{e.Message}" }); }
    }

    public string InstallCable()
    {
        var (ok, msg) = Env.InstallCable();
        _probeAt = DateTime.MinValue;
        return Env.Json(new { ok, message = msg });
    }

    public string SetDefaultMic()
    {
        var (ok, msg) = Env.SetDefaultMic();
        _probeAt = DateTime.MinValue;
        return Env.Json(new { ok, message = msg });
    }

    // ─────────── 配置组 ───────────
    public string SwitchGroup(string? idx)
    {
        var name = Keymaps.SwitchTo(int.TryParse(idx, out var i) ? i : 0);
        return Env.Json(new { ok = true, message = $"已切换到配置组「{name}」。" });
    }

    public string NewGroup()
    {
        var name = Keymaps.Duplicate();
        return Env.Json(new { ok = true, message = $"已复制当前映射并新建配置组「{name}」。" });
    }

    public string RenameGroup(string? idx, string? name)
    {
        var n = Keymaps.Rename(int.TryParse(idx, out var i) ? i : 0, name ?? "");
        return Env.Json(new { ok = true, message = $"配置组已重命名为「{n}」。" });
    }

    public string DeleteGroup(string? idx)
    {
        var (ok, msg) = Keymaps.Delete(int.TryParse(idx, out var i) ? i : 0);
        return Env.Json(new { ok, message = msg });
    }

    /// <summary>导出当前配置组（返回 JSON 文本，由界面存文件）</summary>
    public string ExportGroup() => Keymaps.Export();

    public string ImportGroup(string? json)
    {
        var (ok, msg) = Keymaps.Import(json ?? "");
        return Env.Json(new { ok, message = msg });
    }

    public string SaveKey(string? bid, string? text)
    {
        if (string.IsNullOrEmpty(bid)) return Env.Json(new { ok = false, message = "缺少按键 id" });
        if (!Keys.Table.Any(x => x.Id == bid))
            return Env.Json(new { ok = false, message = $"未知按键 {bid}" });
        int[] vks;
        try { vks = Keys.Parse(text ?? ""); }
        catch (ArgumentException e) { return Env.Json(new { ok = false, message = e.Message }); }
        Keymaps.Set(bid, vks);                       // 写进**当前配置组**
        var name = Keys.Table.FirstOrDefault(x => x.Id == bid)?.Name ?? bid;
        var shown = Keys.FormatSymbols(vks);
        return Env.Json(new { ok = true,
            message = $"「{name}」已设为 {(shown.Length > 0 ? shown : "不执行")}（配置组「{Keymaps.Current.Name}」）" });
    }

    public string ResetKeys()
    {
        var n = Keymaps.ResetCurrent();
        return Env.Json(new { ok = true, message = $"已把配置组「{n}」恢复为默认映射。" });
    }

    public string StartBridge()
    {
        lock (_lock)
        {
            if (BridgeRunning) return Env.Json(new { ok = false, message = "语音桥已在运行。" });
            if (!Env.IsAdmin())
                return Env.Json(new { ok = false, message = "语音桥需要管理员权限（ETW StartTrace）。请用管理员身份重启本程序。" });
            var exe = Env.FindAsset("etw_consumer.exe");
            if (exe == null)
                return Env.Json(new { ok = false, message = "未找到 etw_consumer.exe。" });
            try
            {
                var psi = new ProcessStartInfo(exe)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    Arguments = $"--udp 127.0.0.1:{Env.PcmPort} --hp 70 --seconds 86400",
                };
                _bridge = Process.Start(psi);
                _bridgeStart = DateTime.UtcNow;
                return Env.Json(new { ok = true, message = "语音桥已启动。按住遥控器语音键说话即可。" });
            }
            catch (Exception e)
            {
                _bridge = null;
                return Env.Json(new { ok = false, message = $"启动语音桥失败：{e.Message}" });
            }
        }
    }

    public string StopBridge()
    {
        lock (_lock)
        {
            try { if (_bridge is { HasExited: false }) { _bridge.Kill(true); _bridge.WaitForExit(3000); } }
            catch { }
            _bridge = null;
            return Env.Json(new { ok = true, message = "语音桥已停止。" });
        }
    }

    /// <summary>真的按一次右 Alt 并读回状态，验证注入链路</summary>
    public string TestInject() => KeyFilter.InjectSelfTest();

    /// <summary>清空卡住的按键状态（释放 held 集合 + 放掉注入键）</summary>
    public string ClearKeys()
    {
        var n = _filter?.ClearHeld("手动清空") ?? 0;
        return Env.Json(new { ok = true, message = n > 0
            ? $"已清空 {n} 个卡住的按键，并释放了注入的快捷键。"
            : "当前没有卡住的按键。" });
    }

    /// <summary>环境自检清单（安装/就绪向导用）</summary>
    private JsonObject SetupStatus()
    {
        // 「按键受保护」= 要么设备已不是键盘，要么钩子正在拦截（两者都不会漏 m / 漏键）
        var devOk = !Env.RemoteStillKeyboard() || (_filter is { Running: true, KeySourceIsEtw: false });
        return new JsonObject
        {
            ["cable"] = new JsonObject { ["ok"] = _cableReady, ["hint"] = "虚拟声卡 VB-CABLE" },
            ["mic"] = new JsonObject { ["ok"] = _micOk, ["hint"] = "默认录音设备 = CABLE Output" },
            ["winuhid"] = new JsonObject { ["ok"] = _uhidReady,
                ["hint"] = WinUhidInjector.DriverVersion >= 1
                    ? $"虚拟键盘驱动已就绪（接口 v{WinUhidInjector.DriverVersion}）"
                    : "WinUHid 驱动未安装" },
            ["device"] = new JsonObject { ["ok"] = devOk,
                ["hint"] = !Env.RemoteStillKeyboard() ? "遥控器已不是键盘（零 m）"
                       : (devOk ? "键盘模式 + 钩子拦截中（低延迟）" : "遥控器仍在输出 m") },
            ["bridge"] = new JsonObject { ["ok"] = BridgeRunning, ["hint"] = "语音桥运行中" },
            ["ready"] = _cableReady && _micOk && _uhidReady && devOk,
        };
    }

    /// <summary>「一键完成全部」：把还缺的步骤按顺序跑一遍</summary>
    public string RunSetup()
    {
        RefreshProbes(true);
        var done = new List<string>();
        var todo = new List<string>();

        if (!_cableReady)
        {
            var (ok, m) = Env.InstallCable();
            todo.Add($"虚拟声卡：{m}");
            _ = ok;
        }
        else done.Add("虚拟声卡已就绪");

        if (!_uhidReady)
        {
            WinUhidInjector.EnsureReady();
            if (WinUhidInjector.DriverVersion < 1)
            {
                var (ok, m) = Env.InstallWinuhid();
                todo.Add($"虚拟键盘驱动：{m}");
                _ = ok;
            }
            else done.Add("虚拟键盘驱动已就绪");
        }
        else done.Add("虚拟键盘驱动已就绪");

        if (Env.RemoteStillKeyboard())
        {
            var (ok, m) = Env.SetDeviceDriver(false);
            if (ok) done.Add("设备驱动：已改为 HID-compliant device");
            else todo.Add($"设备驱动：{m}");
        }
        else done.Add("设备驱动已是 HID-compliant device");

        if (!_micOk)
        {
            var (ok, m) = Env.SetDefaultMic();
            if (ok) done.Add("默认麦克风：已设为 CABLE Output");
            else todo.Add($"默认麦克风：{m}");
        }
        else done.Add("默认麦克风已是 CABLE Output");

        RefreshProbes(true);
        MaybeAutoStartBridge();

        var msg = (done.Count > 0 ? "已完成：" + string.Join("；", done) + "。" : "")
                + (todo.Count > 0 ? "\n需要你操作：" + string.Join("；", todo) : "");
        return Env.Json(new { ok = todo.Count == 0, message = msg.Trim() });
    }

    public string SetDeviceDriver(string? revert)
    {
        var (ok, msg) = Env.SetDeviceDriver(revert is "true" or "1" or "True");
        _probeAt = DateTime.MinValue;
        AutoSelectKeySource();
        return Env.Json(new { ok, message = msg });
    }

    private string? _bindBid;
    private string? _groupToast;
    private DateTime _groupToastAt = DateTime.MinValue;
    private DateTime _lastDevCheck = DateTime.MinValue;

    /// <summary>环境探针缓存时长（秒）。这些探针要扫注册表，拉长可显著降低常驻 CPU。</summary>
    private const int ProbeTtlSec = 20;

    /// <summary>最近一次卡键解锁的原因（给界面显示）</summary>
    public string? LastUnlock { get; private set; }
    /// <summary>累计解锁次数</summary>
    public long UnlockCount { get; private set; }

    /// <summary>
    /// 设备可能发生变化（插拔/蓝牙断开/拔电池）。若遥控器已不在，**立即**清掉卡住的键。
    ///
    /// 为什么必须即时：遥控器只发 M-down 没来得及发 M-up 就断开时，Windows 会把 M 永远
    /// 留在「按下」，之后按 31 Hz 持续合成 —— 也就是「拔电池还一直输出 m」。
    /// 只靠静默超时要等 1.5 秒，这期间用户已经在挨刷屏了。
    /// </summary>
    public void OnDeviceMayChanged()
    {
        // 事件可能很频繁（DBT_DEVNODES_CHANGED），限流 200 ms
        if ((DateTime.UtcNow - _lastDevCheck).TotalMilliseconds < 200) return;
        _lastDevCheck = DateTime.UtcNow;

        if (Env.RemotePresent()) return;          // 设备还在，不动
        var n = _filter?.ClearHeld("遥控器已断开（设备移除）") ?? 0;
        if (n > 0)
        {
            UnlockCount++;
            LastUnlock = $"{DateTime.Now:HH:mm:ss} 遥控器断开 → 立即释放 {n} 个卡住的键";
        }
    }

    /// <summary>开始「按一下绑定」：下一次真实按键的 VK 会被捕获（并吞掉，不会打出一个字符）</summary>
    public string BeginBind(string? bid)
    {
        if (_filter == null) return Env.Json(new { ok = false, message = "按键过滤器未就绪。" });
        _bindBid = bid;
        _filter.CaptureNext = true;
        return Env.Json(new { ok = true, message = "请在键盘上按一下要绑定的按键（Esc 可取消）。" });
    }

    /// <summary>捕获到按键：直接写进该键的映射（单键）</summary>
    public void OnBindCaptured(int vk)
    {
        var bid = _bindBid;
        if (bid == null || vk <= 0) return;
        if (Keys.Table.Any(x => x.Id == bid)) Keymaps.Set(bid, new[] { vk });
        _bindBid = null;
    }

    public string CancelBind()
    {
        if (_filter != null) _filter.CaptureNext = false;
        _bindBid = null;
        return Env.Json(new { ok = true, message = "已取消绑定。" });
    }

    /// <summary>开机自启（写入 HKCU\...\Run）</summary>
    public string SetAutoRun(string? on)
    {
        var want = on is "true" or "1" or "True";
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (k == null) return Env.Json(new { ok = false, message = "打不开 Run 注册表项。" });
            if (want)
            {
                var exe = Environment.ProcessPath!;
                k.SetValue(Env.AppName, $"\"{exe}\"");
            }
            else k.DeleteValue(Env.AppName, false);
            Settings.AutoRun = want;
            return Env.Json(new { ok = true, autoRun = want,
                message = want ? "已设置开机自启（登录 Windows 后自动启动）。" : "已取消开机自启。" });
        }
        catch (Exception e) { return Env.Json(new { ok = false, message = $"设置失败：{e.Message}" }); }
    }

    public bool IsAutoRun()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", false);
            return k?.GetValue(Env.AppName) != null;
        }
        catch { return false; }
    }

    /// <summary>启动时最小化到托盘</summary>
    public string SetStartMinimized(string? on)
    {
        var want = on is "true" or "1" or "True";
        Settings.StartMinimized = want;
        return Env.Json(new { ok = true, startMinimized = want,
            message = want ? "已开启：启动后直接进托盘（双击托盘图标可打开）。"
                           : "已关闭：启动后显示主窗口。" });
    }

    /// <summary>清空按键日志</summary>
    public string ClearKeyLog()
    {
        EtwKeySource.ClearLog();
        return Env.Json(new { ok = true, message = "按键日志已清空。" });
    }


    /// <summary>开关「启动后自动拉起语音桥」</summary>
    public string SetAutoStart(string? on)
    {
        AutoStartBridge = on is "true" or "1" or "True";
        if (AutoStartBridge) MaybeAutoStartBridge();
        return Env.Json(new { ok = true, autoStart = AutoStartBridge,
            message = AutoStartBridge ? "已开启：启动本程序后自动拉起语音桥。" : "已关闭自动启动语音桥。" });
    }

    public string InstallWinuhid()
    {
        var (ok, msg) = Env.InstallWinuhid();
        return Env.Json(new { ok, message = msg });
    }

    public string ExportWinuhid()
    {
        var (ok, msg) = Env.ExportWinuhid();
        return Env.Json(new { ok, message = msg });
    }

    public string RelaunchAdmin()
    {
        var (ok, msg) = Env.RelaunchAdmin();
        return Env.Json(new { ok, message = msg });
    }

    public string OpenUrl(string? url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url ?? "https://vb-audio.com/Cable/") { UseShellExecute = true });
            return Env.Json(new { ok = true, message = "已在浏览器打开。" });
        }
        catch (Exception e) { return Env.Json(new { ok = false, message = e.Message }); }
    }

    /// <summary>开关「启动时自动把驱动切成非键盘」</summary>
    public string SetAutoSwitchDriver(string? on)
    {
        var want = on is "true" or "1" or "True";
        Settings.AutoSwitchDriver = want;
        return Env.Json(new { ok = true, autoSwitchDriver = want,
            message = want ? "已开启：每次启动会把遥控器驱动自动切成非键盘（零 m、无卡键）。"
                           : "已关闭：不再自动改驱动。" });
    }

    /// <summary>手动「释放所有注入键」——用于键盘手感不对时一键复位</summary>
    public string ReleaseAllKeys()
    {
        KeyFilter.ReleaseAllInjected();
        _filter?.ClearHeld("手动释放全部注入键");
        return Env.Json(new { ok = true, message = "已释放全部注入键（含右 Alt / 方向键 / 媒体键）。" });
    }

    public void Dispose()
    {
        StopBridge();
        _router.Dispose();
    }
}
