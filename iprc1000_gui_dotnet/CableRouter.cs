using System.Net;
using System.Net.Sockets;
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

    public string? Error { get; private set; }
    public string? Device { get; private set; }
    public bool Online => _running && Error == null;
    public long Packets => Interlocked.Read(ref _packets);
    public long Bytes => Interlocked.Read(ref _bytes);
    public bool Session => _inSession;

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "cable-router" };
        _thread.Start();
    }

    private static MMDevice? FindCable()
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            return en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .FirstOrDefault(d => d.FriendlyName.ToLowerInvariant().Contains("cable input"));
        }
        catch (Exception e) { Console.WriteLine(e.Message); return null; }
    }

    private bool EnsureSink()
    {
        if (_out != null) return true;
        var dev = FindCable();
        if (dev == null) { Error = "未找到 CABLE Input 播放设备"; return false; }
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
            return true;
        }
        catch (Exception e) { Error = $"打开 CABLE Input 失败：{e.Message}"; CloseSink(); return false; }
    }

    private void CloseSink()
    {
        try { _out?.Stop(); } catch { }
        _out?.Dispose();
        _out = null;
        _buf = null;
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
                        _buf?.ClearBuffer();
                        _inSession = true;
                        EnsureSink();
                    }
                    else if (n == 3 && asm[0] == (byte)'E')      // END
                    {
                        _inSession = false;
                        _lastEnd = DateTime.UtcNow;
                    }
                    else if (n >= 2 && n % 2 == 0)
                    {
                        Interlocked.Increment(ref _packets);
                        Interlocked.Add(ref _bytes, n);
                        if (EnsureSink()) _buf!.AddSamples(asm, 0, n);
                    }
                }
            }
            catch (SocketException) { /* 超时 */ }
            catch (Exception e) { if (Error == null) Error = e.Message; }

            if (!_inSession && _out != null && _lastEnd != DateTime.MinValue
                && (DateTime.UtcNow - _lastEnd).TotalSeconds > 0.6)
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
