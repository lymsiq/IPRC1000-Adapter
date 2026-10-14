using System.Net;
using System.Net.Sockets;
using System.Text;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Iprc1000Adapter;

/// <summary>
/// 监听 127.0.0.1:31680，把语音桥推来的 48 kHz PCM 写进 CABLE Input。
/// 协议（与项目里的 audio/pcm_router.rs 一致）：CLEAR / int16 LE 48k / END。
/// 用 NAudio 的 WASAPI 共享模式，靠系统 WebView2/音频栈，不额外打包任何引擎。
/// </summary>
internal sealed class CableRouter : IDisposable
{
    private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, Env.PcmPort));
    private WasapiOut? _out;
    private BufferedWaveProvider? _buf;
    private Thread? _thread;
    private volatile bool _running;
    private long _packets;
    private long _bytes;
    private volatile bool _inSession;
    private DateTime _lastEnd = DateTime.MinValue;

    // 声卡开关要加锁：预热是从**按键线程**调的，而正常开关在**路由线程**里，
    // 两个线程同时 EnsureSink 会各自 new 出一个 WasapiOut（后一个泄漏）。
    private readonly object _sinkLock = new();
    // 缓存 CABLE Input 设备对象：原来每轮 EnsureSink 都要重新枚举全部渲染端点（数十毫秒），
    // 而这段开销正好落在"按键→出声"的关键路径上。
    private MMDevice? _cable;
    // 语音键按下的时刻，用来量「按键 → 第一包音频到达」到底花了多久
    private DateTime _voiceDownAt = DateTime.MinValue;

    public string? Error { get; private set; }
    public string? Device { get; private set; }
    public bool Online => _running && Error == null;
    public long Packets => Interlocked.Read(ref _packets);
    public long Bytes => Interlocked.Read(ref _bytes);
    public bool Session => _inSession;

    // ── 声卡侧日志 ──
    // 整条链路里"最后一公里"就靠它：桥的统计（bridge.log / bridge-stats.json）能说明
    // "帧有没有来"，这里说明"来了有没有真的写进 CABLE"。缺了它，两端都正常但没声音时无从下嘴。
    private static readonly object _logLock = new();
    /// <summary>音频链路通用诊断日志（写 router.log）。同一份日志里既有声卡开关也有默认设备变化，便于对照。</summary>
    internal static void Log(string msg)
    {
        try
        {
            lock (_logLock)
            {
                var p = Path.Combine(Env.ConfigDir, "router.log");
                var body = $"{DateTime.Now:HH:mm:ss.fff}  {msg}\n";
                // 首次创建写 BOM：Windows 记事本 / Get-Content 否则会按 ANSI 解码而显示乱码
                if (!File.Exists(p)) File.WriteAllText(p, body, new UTF8Encoding(true));
                else File.AppendAllText(p, body, new UTF8Encoding(false));
            }
        }
        catch { }
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "cable-router" };
        _thread.Start();
    }

    private MMDevice? FindCable()
    {
        if (_cable != null) return _cable;          // 命中缓存，省掉整轮设备枚举
        try
        {
            using var en = new MMDeviceEnumerator();
            _cable = en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .FirstOrDefault(d => d.FriendlyName.ToLowerInvariant().Contains("cable input"));
            return _cable;
        }
        catch (Exception e) { Console.WriteLine(e.Message); return null; }
    }

    /// <summary>
    /// 预热：**语音键按下的瞬间**就把 CABLE Input 打开。
    ///
    /// 为什么必须做：打开声卡要「枚举设备 → new WasapiOut → Init → Play」，约 100~300 ms。
    /// 而原来的流程是"第一个音频包到了才去开声卡"，于是这段时间到达的音频被
    /// `if (EnsureSink()) _buf!.AddSamples(...)` 直接丢掉 ——
    /// 用户看到的现象就是**按住语音键立刻说话、第一个字识别不出来**。
    /// 现在把开声卡提前到按键时刻（音频还要 100~200 ms 才到），关键路径上就没有这段开销了。
    /// </summary>
    public void WarmUp()
    {
        _voiceDownAt = DateTime.UtcNow;
        if (!_running) return;
        var t0 = DateTime.UtcNow;
        if (EnsureSink())
        {
            var ms = (DateTime.UtcNow - t0).TotalMilliseconds;
            if (ms > 30) Log($"[LAT] 语音键预热：打开声卡用了 {ms:F0} ms");
        }
    }

    private bool EnsureSink()
    {
        lock (_sinkLock)
        {
            if (_out != null) return true;
            var dev = FindCable();
            if (dev == null)
            {
                Error = "未找到 CABLE Input 播放设备";
                Log("[!] " + Error + " —— 音频来了也写不进去");
                return false;
            }
            try
            {
                _buf = new BufferedWaveProvider(new WaveFormat(48000, 16, 1))
                {
                    DiscardOnBufferOverflow = true,
                    BufferDuration = TimeSpan.FromSeconds(1),
                    ReadFully = true,
                };
                _out = new WasapiOut(dev, AudioClientShareMode.Shared, false, 50);
                _out.Init(_buf);
                _out.Play();
                Device = dev.FriendlyName;
                Error = null;
                Log($"[+] 已打开输出设备：{Device}");
                return true;
            }
            catch (Exception e)
            {
                Error = $"打开 CABLE Input 失败：{e.Message}";
                Log("[!] " + Error);
                _cable = null;                  // 设备对象可能已失效，清掉缓存下次重枚举
                CloseSinkLocked();
                return false;
            }
        }
    }

    private void CloseSink()
    {
        lock (_sinkLock) CloseSinkLocked();
    }

    private void CloseSinkLocked()
    {
        if (_out == null) return;
        try { _out.Stop(); } catch { }
        _out?.Dispose();
        _out = null;
        _buf = null;
        Log($"[-] 已关闭输出设备（本次共写入 {Packets} 包 / {Bytes} 字节）");
    }

    private void Loop()
    {
        _udp.Client.ReceiveTimeout = 200;
        var asm = new byte[65536];
        while (_running)
        {
            try
            {
                if (_udp.Client.Poll(150_000, SelectMode.SelectRead))
                {
                    EndPoint ep = new IPEndPoint(IPAddress.Any, 0);
                    var n = _udp.Client.ReceiveFrom(asm, ref ep);
                    if (n == 4 && asm[0] == (byte)'P' && asm[1] == (byte)'I')
                        _udp.Send(new byte[] { (byte)'P', (byte)'O', (byte)'N', (byte)'G' }, 4,
                                  (IPEndPoint)ep);
                    else if (n == 5 && asm[0] == (byte)'C')      // CLEAR
                    {
                        // ★ 不要 ClearBuffer()。
                        //   以前这里清缓冲，但会话是「松手 END → 再按 CLEAR」反复建立的，
                        //   环里往往还压着上一次的真实音频 —— 一清就是一次可听的断音。
                        //   缓冲只有 1 秒、且 ReadFully=true 会在空时补静音，
                        //   不清也不会播出过期音频；而声卡重开时 EnsureSink 本来就换了新缓冲。
                        _inSession = true;
                        EnsureSink();
                        Log($"[CLEAR] 新会话开始（累计 {Packets} 包）");
                    }
                    else if (n == 3 && asm[0] == (byte)'E')      // END
                    {
                        _inSession = false;
                        _lastEnd = DateTime.UtcNow;
                        Log($"[END] 会话结束（累计 {Packets} 包 / {Bytes} 字节）");
                    }
                    else if (n >= 2 && n % 2 == 0)
                    {
                        Interlocked.Increment(ref _packets);
                        Interlocked.Add(ref _bytes, n);
                        // ★ 收到音频本身就说明"在会话中"。
                        //   UDP 不可靠，CLEAR 有可能丢包；若只认 CLEAR，丢一次就会被下面的
                        //   超时判断当成"会话已结束"而把声卡关掉 —— 表现为说话中途突然静音。
                        var first = !_inSession;
                        _inSession = true;
                        var ok = EnsureSink();
                        if (first && _voiceDownAt != DateTime.MinValue)
                        {
                            var ms = (DateTime.UtcNow - _voiceDownAt).TotalMilliseconds;
                            Log($"[LAT] 语音键按下 → 首包音频到达 {ms:F0} ms（声卡已就绪={ok}）");
                            _voiceDownAt = DateTime.MinValue;
                        }
                        if (ok) _buf!.AddSamples(asm, 0, n);
                    }
                }
            }
            catch (SocketException) { /* 超时 */ }
            catch (Exception e) { if (Error == null) Error = e.Message; }

            // 彻底静默一段时间后再关声卡。
            // 以前是 0.6 秒 → 后来 3 秒，都会被正常停顿触发，每次重开 WASAPI 又要 100~300 ms，
            // 而这段开销正好吃掉"按住后立刻说的第一个字"。放宽到 30 秒：
            // 相当于一直保持温热，语音交互几乎不会再碰到冷启动。
            if (!_inSession && _out != null && _lastEnd != DateTime.MinValue
                && (DateTime.UtcNow - _lastEnd).TotalSeconds > 30.0)
            {
                if (_buf == null || _buf.BufferedBytes == 0) CloseSink();
            }
        }
        CloseSink();
    }

    public object Status() => new
    {
        online = Online,
        packets = Packets,
        bytes = Bytes,
        error = Error,
        session = Session,
        device = Device,
    };

    public void Dispose()
    {
        _running = false;
        try { _thread?.Join(500); } catch { }
        CloseSink();
        _udp.Dispose();
    }
}
