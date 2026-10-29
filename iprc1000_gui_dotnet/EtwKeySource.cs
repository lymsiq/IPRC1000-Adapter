using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Iprc1000Adapter;

/// <summary>
/// 按键来源：**BthPort ETW**（配合"把设备驱动换成 HID-compliant device"）。
///
/// 为什么要有这条：把遥控器的驱动从 `HID Keyboard Device` 换成 `HID-compliant device` 后，
/// Windows **不再把它当键盘** → `m` 从根上消失；但 Raw Input / 键盘钩子也随之看不到它了。
/// 于是按键改从 ETW 里解析设备的原始 HID 报告（`a1 01 00 00 <usage> ...`），
/// 由语音桥（etw_consumer）解析后以 `KEY &lt;usage&gt; &lt;1|0&gt;` 发到本端口。
///
/// 优势：**不需要吞键、不需要 Raw Input、没有 held 集合、没有丢 UP 卡键的风险。**
/// </summary>
internal sealed class EtwKeySource : IDisposable
{
    // 2026-10-29：端口改为动态（可能被避让），不再硬编码 31681
    public static int DefaultPort => Env.KeyPort;

    /// <summary>HID usage → button id（与 src-tauri/.../iprc1000/bridge.rs 的 BUTTON_TABLE 注释一致）</summary>
    public static readonly Dictionary<byte, string> UsageToButton = new()
    {
        [0x17] = "power", [0x09] = "back", [0x11] = "menu", [0x07] = "exit", [0x0b] = "info",
        [0x28] = "ok", [0x52] = "up", [0x51] = "down", [0x50] = "left", [0x4f] = "right",
        [0x08] = "favorite", [0x10] = "voice", [0x0d] = "account", [0x31] = "play_pause",
        [0x19] = "fast_forward", [0x36] = "rewind", [0x04] = "channel_up", [0x05] = "channel_down",
        [0x1f] = "volume_up", [0x20] = "volume_down", [0x0a] = "mute", [0x06] = "last",
        [0x16] = "square",
        // 0x0C = 幽灵报告，故意不映射（会被接收但忽略）
    };

    /// <summary>按键事件日志（滚动保留最近 2000 行），方便"按一遍按键"后离线核对映射</summary>
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Env.AppName, "key-events.log");

    private void Log(string line)
    {
        try
        {
            var f = new FileInfo(LogPath);
            if (f.Exists && f.Length > 512 * 1024)
            {
                var keep = File.ReadAllLines(LogPath).TakeLast(500).ToArray();
                File.WriteAllLines(LogPath, keep);
            }
            File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff}  {line}\n");
        }
        catch { }
    }

    private readonly Action<byte, bool> _onKey;
    private Thread? _thread;
    private UdpClient? _udp;
    private volatile bool _running;

    public int Port { get; }

    /// <summary>按键日志文件路径（界面/诊断用）</summary>
    public static string EventsLogPath => LogPath;

    /// <summary>清空按键日志（诊断前先清一下，读起来干净）</summary>
    public static void ClearLog()
    {
        try { File.WriteAllText(LogPath, ""); } catch { }
    }

    /// <summary>把任意一行写进同一个按键日志（HID 直读那条路也复用）</summary>
    /// <summary>
    /// 写一行按键日志。
    ///
    /// ★ 只是**入队**（`KeyLog.Write`），真正的 open/write/close 在后台线程做。
    ///   原来这里直接 `File.AppendAllText`：本机实测每行 0.253 ms，一次按键 5~8 行
    ///   ⇒ **注入之前先等 1.3 ms**，而且文件越大、开着实时杀毒就越慢越抖。
    /// </summary>
    public static void AppendLog(string line) => KeyLog.Write(line);
    public bool Online => _running && _udp != null;

    /// <summary>累计按下次数（正常一次按键 = 1）</summary>
    public long KeyDowns { get; private set; }
    /// <summary>累计松开次数（正常一次按键 = 1）</summary>
    public long KeyUps { get; private set; }
    public long Events { get; private set; }
    public long Reports { get; private set; }
    public long UnknownUsage { get; private set; }
    public byte LastUsage { get; private set; }
    public string LastButton { get; private set; } = "";
    public bool LastPressed { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>实测 ETW 投递延迟（毫秒）：设备产生事件 → 我们收到</summary>
    public long MaxLatencyMs { get; private set; }
    public long AvgLatencyMs => LatencySamples == 0 ? -1 : SumLatencyMs / LatencySamples;
    private long SumLatencyMs, LatencySamples;
    public DateTime LastAt { get; private set; } = DateTime.MinValue;

    public EtwKeySource(int port, Action<byte, bool> onKey)
    {
        Port = port;
        _onKey = onKey;
    }

    public bool Start()
    {
        if (_running) return true;
        try
        {
            _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, Port));
            _udp.Client.ReceiveTimeout = 200;
        }
        catch (Exception e)
        {
            LastError = $"绑定 UDP {Port} 失败：{e.Message}";
            _udp = null;
            return false;
        }
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "etw-key-source" };
        _thread.Start();
        LastError = null;
        return true;
    }

    private void Loop()
    {
        var ep = new IPEndPoint(IPAddress.Any, 0);
        while (_running)
        {
            try
            {
                if (_udp == null) break;
                if (!_udp.Client.Poll(200_000, SelectMode.SelectRead)) continue;
                var data = _udp.Receive(ref ep);
                Handle(Encoding.ASCII.GetString(data));
            }
            catch (SocketException) { /* 超时 */ }
            catch (Exception e) { LastError = e.Message; }
        }
    }

    /// <summary>解析语音桥发来的 `KEY &lt;usage&gt; &lt;1|0&gt;`</summary>
    private void Handle(string msg)
    {
        var t = msg.Trim();
        if (!t.StartsWith("KEY ", StringComparison.Ordinal)) return;
        var parts = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return;
        if (!byte.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out var usage))
            return;
        var down = parts[2] == "1";

        // 第 4 段：ETW 事件时间戳（FILETIME）→ 算"设备产生 → 我们收到"的真实投递延迟
        long lat = -1;
        if (parts.Length >= 4 && long.TryParse(parts[3], out var ts) && ts > 0)
        {
            try
            {
                var evt = DateTime.FromFileTimeUtc(ts);
                lat = (long)(DateTime.UtcNow - evt).TotalMilliseconds;
                if (lat is >= 0 and < 60_000)
                {
                    if (lat > MaxLatencyMs) MaxLatencyMs = lat;
                    SumLatencyMs += lat;
                    LatencySamples++;
                }
            }
            catch { }
        }

        Events++;
        Reports++;
        // ★ 分开统计按下/松开：面板原来只说"48 次"，用户会读成"按了 48 下"。
        //   一次正常按键 = 1 DOWN + 1 UP = 2 个事件。若出现 4 个，说明**上报重复**
        //   （同一次物理按键产生了 2 组 DOWN/UP），那就得往桥/设备侧查，不能怪用户。
        if (down) KeyDowns++; else KeyUps++;
        LastAt = DateTime.UtcNow;

        if (!UsageToButton.TryGetValue(usage, out var bid))
        {
            UnknownUsage++;
            // ⚠ 幽灵帧（0x0C）**不更新 LastUsage**：
            //   以前这里先赋值再查表，于是界面会出现「voice（usage 0x0C）」这种
            //   按钮名和 usage 对不上的组合（2026-10-17 用户截图指出）。
            // ★ 日志关了就连字符串都不拼（`$"..."` 是先求值再传参，不判开关就是纯浪费）
            if (LogSwitch.Enabled)
                Log($"KEY  usage=0x{usage:X2}  (未知/幽灵)  {(down ? "DOWN" : "UP")}");
            _onKey(usage, down);   // 未知也上报，交给上层判断
            return;
        }
        // 只对**认得的按键**更新"最近按键"，所以两个字段永远自洽
        LastUsage = usage;
        LastButton = bid;
        LastPressed = down;
        if (LogSwitch.Enabled)
            Log($"KEY  usage=0x{usage:X2}  {bid}  {(down ? "DOWN" : "UP")}" + (lat >= 0 ? $"  投递延迟={lat}ms" : ""));
        _onKey(usage, down);
    }

    public object Status() => new
    {
        port = Port,
        maxLatencyMs = MaxLatencyMs,
        avgLatencyMs = AvgLatencyMs,
        latencySamples = LatencySamples,
        online = Online,
        events = Events,
        keyDowns = KeyDowns,
        keyUps = KeyUps,
        unknownUsage = UnknownUsage,
        lastUsage = $"0x{LastUsage:X2}",
        lastButton = LastButton,
        pressed = LastPressed,
        sinceLastMs = LastAt == DateTime.MinValue ? -1 : (long)(DateTime.UtcNow - LastAt).TotalMilliseconds,
        error = LastError,
    };

    public void Dispose()
    {
        _running = false;
        try { _thread?.Join(400); } catch { }
        try { _udp?.Dispose(); } catch { }
        _udp = null;
    }
}
