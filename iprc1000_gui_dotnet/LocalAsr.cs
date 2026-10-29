using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Iprc1000Adapter;

/// <summary>
/// **本地语音识别（CPU）**（2026-10-29）。
///
/// 链路：按住语音键 → 把已解码的 16k PCM 攒起来 → 松开 → 整段 POST 给本地引擎
///       → 拿 final 文本 → 用 Unicode 注入打进光标。
///
/// 为什么第一版用「整段」而不是 WebSocket 流式：
///   流式要处理 partial 的就地替换、断句、焦点保持，任何一处没做好都比"晚 0.5 秒出字"更糟。
///   先把「说出来 → 有字」这条最短路径跑通、量出真实延迟，再决定要不要上流式。
///
/// 引擎管理：懒启动 + 常驻；进程死了下次自动重来（模型加载 ~0.5s，可接受）。
/// </summary>
internal static class LocalAsr
{
    private static Process? _srv;
    private static int _port;
    /// <summary>WebSocket 端口（引擎独立分配，必须从 /health 读）。</summary>
    public static int WsPort { get; private set; }
    private static string _model = "";
    // ★★ 必须显式禁用代理：.NET 的 HttpClient 会读 HTTP_PROXY/HTTPS_PROXY 环境变量，
    //   而 **127.0.0.1 也会被代理**（我们实测：请求发到本地代理，代理回 404，
    //   表现为"引擎明明在跑、/health 也通，但 /v1/transcribe 一直 404"）。
    //   企业机器上常有代理环境变量 —— 所以这不是测试环境特例，是必须修的真 bug。
    private static readonly HttpClient Http = new(new HttpClientHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };
    private static readonly object Gate = new();

    public static bool Running => _srv is { HasExited: false };

    /// <summary>最近一次识别结果（给面板/诊断看，避免"到底跑没跑"靠猜）。</summary>
    public static string LastText = "";
    public static string LastErr = "";
    public static long LastMs;
    public static double LastSec;
    public static long LastAt;   // TickCount64
    public static long LastCaptureBytes;

    /// <summary>引擎可执行文件（随包在 assets\haolive）。</summary>
    public static string EnginePath()
    {
        var a = Path.Combine(AppContext.BaseDirectory, "assets", "haolive", "haolive-server.exe");
        if (File.Exists(a)) return a;
        var b = Path.Combine(Env.ConfigDir, "asr", "haolive-server.exe");
        return File.Exists(b) ? b : "";
    }

    /// <summary>确保引擎在跑；模型路径变了就重启。</summary>
    public static (bool ok, string msg) Ensure(string modelPath, string backend = "cpu")
    {
        lock (Gate)
        {
            if (_srv is { HasExited: false } && _model == modelPath) return (true, "已运行");
            StopLocked();

            var exe = EnginePath();
            if (exe.Length == 0) return (false, "找不到引擎 haolive-server.exe");
            if (!File.Exists(modelPath)) return (false, "找不到模型文件：" + modelPath);

            _port = FreePort();
            var psi = new ProcessStartInfo(exe,
                $"--model \"{modelPath}\" --backend {backend} --port {_port} --threads 4")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(exe)!
            };
            try
            {
                _srv = Process.Start(psi);
                _model = modelPath;
                var srv = _srv;
                if (srv == null) { StopLocked(); return (false, "进程没起来"); }
                // 等 /health（模型加载约 0.5s，给 60s 余量）
                var t0 = DateTime.UtcNow;
                while ((DateTime.UtcNow - t0).TotalSeconds < 60)
                {
                    if (srv.HasExited) break;
                    try
                    {
                        using var r = Http.GetAsync($"http://127.0.0.1:{_port}/health").GetAwaiter().GetResult();
                        if (r.IsSuccessStatusCode)
                        {
                            // /health 里带 websocket_port（流式在**另一个端口**）
                            try
                            {
                                var hb = r.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                                using (var hd = JsonDocument.Parse(hb))
                                {
                                    if (hd.RootElement.TryGetProperty("websocket_port", out var wp))
                                        WsPort = wp.GetInt32();
                                }
                            }
                            catch { }
                            // 确认这个端口上真的是**加载了模型的**引擎（不是残留的别的实例）
                            try
                            {
                                using var m = Http.GetAsync($"http://127.0.0.1:{_port}/v1/models").GetAwaiter().GetResult();
                                var mb = m.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                                if (!mb.Contains("\"xasr\"") && !mb.Contains("\"data\""))
                                {
                                    LastErr = "引擎未加载模型：" + mb.Substring(0, Math.Min(120, mb.Length));
                                    StopLocked();
                                    return (false, LastErr);
                                }
                            }
                            catch { }
                            return (true, "已启动");
                        }
                    }
                    catch { }
                    Thread.Sleep(200);
                }
                var err = "";
                try { if (srv.HasExited) err = srv.StandardOutput.ReadToEnd(); } catch { }
                StopLocked();
                return (false, "引擎没起来" + (err.Length > 0 ? "：" + err.Substring(0, Math.Min(200, err.Length)) : ""));
            }
            catch (Exception e)
            {
                StopLocked();
                return (false, "启动失败：" + e.Message);
            }
        }
    }

    public static void Stop()
    {
        lock (Gate) StopLocked();
    }

    private static void StopLocked()
    {
        try
        {
            if (_srv is { HasExited: false }) { _srv.Kill(true); _srv.WaitForExit(2000); }
        }
        catch { }
        try { _srv?.Dispose(); } catch { }
        _srv = null;
        _model = "";
    }

    private static int FreePort()
    {
        for (var p = 9899; p < 9919; p++)
        {
            try
            {
                using var probe = new System.Net.Sockets.UdpClient(
                    new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, p));
                return p;
            }
            catch { }
        }
        return 9899;
    }

    /// <summary>整段转写。wav 必须是 16k / mono / 16bit。</summary>
    public static async Task<(bool ok, string text, int ms)> TranscribeAsync(byte[] wav)
    {
        if (!Running) return (false, "", 0);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var content = new ByteArrayContent(wav);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
            var url = $"http://127.0.0.1:{_port}/v1/transcribe";
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            req.Headers.ConnectionClose = true;          // 短连接，避免复用连接的兼容问题
            req.Headers.ExpectContinue = false;
            using var r = await Http.SendAsync(req);
            var body = await r.Content.ReadAsStringAsync();
            sw.Stop();
            if (!r.IsSuccessStatusCode)
            {
                LastErr = $"HTTP {(int)r.StatusCode} @ {url}（{wav.Length} 字节）：{body.Substring(0, Math.Min(120, body.Length))}";
                return (false, "", (int)sw.ElapsedMilliseconds);
            }
            using var doc = JsonDocument.Parse(body);
            var text = doc.RootElement.TryGetProperty("text", out var t) ? (t.GetString() ?? "") : "";
            return (true, text.Trim(), (int)sw.ElapsedMilliseconds);
        }
        catch (Exception e)
        {
            sw.Stop();
            LastErr = "异常：" + e.Message;
            Safe.Note("ASR 转写失败：" + e.Message);
            return (false, "", (int)sw.ElapsedMilliseconds);
        }
    }

    // ── 注入：Unicode 方式打字（中文只能这样，虚拟键盘的 HID 键码打不出汉字）──
    private const uint INPUT_KEYBOARD = 1, KEYEVENTF_UNICODE = 0x0004, KEYEVENTF_KEYUP = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public KEYBDINPUT ki;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint n, INPUT[] inputs, int size);

    /// <summary>把文本按 Unicode 单元打进当前焦点。返回是否全部发出。</summary>
    public static bool TypeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return true;
        var list = new List<INPUT>(text.Length * 2);
        foreach (var ch in text)
        {
            list.Add(new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE } });
            list.Add(new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } });
        }
        var arr = list.ToArray();
        var sent = SendInput((uint)arr.Length, arr, Marshal.SizeOf<INPUT>());
        return sent == arr.Length;
    }

    /// <summary>把 16k mono 16bit 的裸 PCM 包成 WAV（引擎也收裸 PCM，但 WAV 更直观、便于存证）。</summary>
    public static byte[] WrapWav(byte[] pcm)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + pcm.Length);
        w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        w.Write(16);            // fmt 块长度
        w.Write((short)1);      // PCM
        w.Write((short)1);      // 单声道
        w.Write(16000);         // 采样率
        w.Write(16000 * 2);     // 字节率
        w.Write((short)2);      // 块对齐
        w.Write((short)16);     // 位深
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(pcm.Length);
        w.Write(pcm);
        w.Flush();
        return ms.ToArray();
    }
}

/// <summary>
/// 录音缓冲：语音键按下开始攒，松开取走。
/// 只在 **AsrMode == "asr"** 时启用，避免影响现有的"交给输入法"路径。
/// </summary>
internal static class AsrCapture
{
    private static MemoryStream? _buf;
    private static readonly object Gate = new();
    private static long _bytes;

    // 波形用：最近 18 个 RMS 电平（0..1）。音频线程写入，UI 线程读取。
    private const int LevelSlots = 18;
    private static readonly float[] _levels = new float[LevelSlots];
    private static int _levelPos;

    /// <summary>仅供外观自检：直接塞一个电平值（不经过音频链）。</summary>
    public static void DebugFeedLevel(float v)
    {
        lock (Gate) { _levels[_levelPos] = Math.Clamp(v, 0, 1); _levelPos = (_levelPos + 1) % LevelSlots; }
    }

    public static float[] Levels()
    {
        lock (Gate)
        {
            var copy = new float[LevelSlots];
            for (var i = 0; i < LevelSlots; i++) copy[i] = _levels[(_levelPos + i) % LevelSlots];
            return copy;
        }
    }

    public static bool Active { get { lock (Gate) return _buf != null; } }
    /// <summary>实时字节数（诊断：能涨说明 PCM 真的进来了）。</summary>
    public static long LiveBytes { get { lock (Gate) return _bytes; } }

    public static void Begin()
    {
        lock (Gate)
        {
            _buf = new MemoryStream(16000 * 2 * 30);   // 预留 30 秒
            _bytes = 0;
            _probe.Clear(); _pending.Clear(); _ds2 = null;   // 每次重新判定上采样
        }
    }

    /// <summary>音频线程调用（每帧 316~640 字节），必须极轻。</summary>
    public static void Feed(byte[] b, int off, int len)
    {
        MemoryStream? buf;
        lock (Gate) { buf = _buf; if (buf == null) return; _bytes += len; }
        // ① 还没判定完：先攒着（一个字节都不往下走，避免前后倍率不一致）
        if (_ds2 == null)
        {
            for (var i = off; i < off + len; i++) _pending.Add(b[i]);
            ProbeAndDecide(b, off, len);
            if (_ds2 == null) return;                       // 还不够 4096 样本，继续攒

            // 判定出来了：把攒下的**全部**按统一倍率吐出
            var all = _pending.ToArray();
            _pending.Clear();
            var flushed = _ds2 == true ? Decimate2(all, 0, all.Length) : all;
            if (flushed.Length > 0)
            {
                try { buf.Write(flushed, 0, flushed.Length); } catch { }
                AsrRun.PushLive(flushed, 0, flushed.Length);
            }
            Safe.Note($"ASR 判定完成（{(_ds2 == true ? "32k→16k 抽取" : "16k 原样")}），补发 {flushed.Length / 32}ms");
            return;
        }

        // ② 已判定：按固定倍率处理
        if (_ds2 == true)
        {
            var dec = Decimate2(b, off, len);
            if (dec.Length == 0) return;
            try { buf.Write(dec, 0, dec.Length); } catch { }
            AsrRun.PushLive(dec, 0, dec.Length);
            return;
        }
        try { buf.Write(b, off, len); } catch { }
        AsrRun.PushLive(b, off, len);   // 流式已连上就同时推过去
        // RMS 单独算：不能锁着 Gate 做浮点循环（音频线程要快）
        try
        {
            double sum = 0; var n = len / 2;
            for (var i = 0; i + 1 < len; i += 2)
            {
                var v = (short)(b[off + i] | (b[off + i + 1] << 8));
                sum += (double)v * v;
            }
            var rms = n > 0 ? Math.Sqrt(sum / n) / 32768.0 : 0;
            // 人耳感知更接近对数：换算成 0..1 的显示值
            var lv = (float)Math.Clamp(Math.Log10(1 + rms * 60) / 1.6, 0, 1);
            lock (Gate) { _levels[_levelPos] = lv; _levelPos = (_levelPos + 1) % LevelSlots; }
        }
        catch { }
    }

    /// <summary>最近一次实际使用的增益倍数（1.0 = 未放大）。</summary>
    public static double LastGain = 1.0;

    /// <summary>
    /// **上采样检测**（2026-10-29，真机破案）。
    ///
    /// 现象：用户说一句约 4 秒的话，我们录到 8.7 秒；频谱能量全挤在 3~8kHz
    /// （正常语音该是 300~3400Hz）；把这堆样本每隔一个取一个（=时长减半）后，
    /// 引擎立刻认出「好多遍了」。⇒ 音频被**上采样了 2 倍**（样本数翻倍 + 线性插值），
    /// 引擎听到的是"拖慢一倍的糊音"，所以一个字都出不来。
    ///
    /// 判定依据：上采样产生的位置必然满足 `b ≈ (a + c) / 2`（a,b,c 为连续三个样本）。
    /// 抽若干组算命中率，>75% 就认定是 2× 上采样，之后按 2:1 抽取（带 3 点平均低通）。
    /// </summary>
    private static bool? _ds2;          // null = 还没判定
    private static readonly List<short> _probe = new(4096);
    /// <summary>
    /// 判定前暂存的**原始字节**。
    /// ★ 为什么必须缓存：如果在流中途才切换"是否降采样"，
    ///   前半段是一倍时长、后半段是减半时长 ⇒ 音频拼接错乱
    ///   ⇒ 表现为「卡顿」和「没说完就断」。所以判定完成前一个字节都不写进缓冲，
    ///   判定后用**同一个倍率**把暂存部分一次性吐出，保证音频连续。
    /// </summary>
    private static readonly List<byte> _pending = new(64 * 1024);
    public static bool Downsampled => _ds2 == true;

    private static void ProbeAndDecide(byte[] b, int off, int len)
    {
        if (_ds2 != null) return;
        for (var i = off; i + 1 < off + len && _probe.Count < 4096; i += 2)
            _probe.Add((short)(b[i] | (b[i + 1] << 8)));
        if (_probe.Count < 2048) return;

        var hit = 0; var tot = 0;
        for (var i = 2; i + 2 < _probe.Count; i += 2)
        {
            var a = _probe[i - 1]; var b2 = _probe[i]; var c = _probe[i + 1];
            var mid = (a + c) / 2.0;
            var scale = Math.Max(64.0, Math.Abs(a) * 0.02);
            tot++;
            if (Math.Abs(b2 - mid) <= scale) hit++;
        }
        var ratio = tot == 0 ? 0 : (double)hit / tot;
        _ds2 = ratio > 0.75;
        Safe.Note($"ASR 音频判定：插值命中率 {ratio:P0} ⇒ {(_ds2 == true ? "按 32k→16k 抽取" : "按 16k 原样使用")}");
    }

    /// <summary>2:1 抽取 + 3 点平均（粗略低通，抑制抽取混叠）。</summary>
    private static byte[] Decimate2(byte[] src, int off, int len)
    {
        var n = len / 2;
        if (n < 4) return Array.Empty<byte>();
        var outLen = n / 2;
        var dst = new byte[outLen * 2];
        for (var k = 0; k < outLen; k++)
        {
            var i0 = off + ((k * 2) * 2);
            var i1 = i0 + 2;
            var i2 = i0 + 4;
            int a = (short)(src[i0] | (src[i0 + 1] << 8));
            var bb = (short)(src[i1] | (src[i1 + 1] << 8));
            var c = i2 + 1 < off + len ? (short)(src[i2] | (src[i2 + 1] << 8)) : bb;
            var v = Math.Clamp((a + 2 * bb + c) / 4, -32000, 32000);
            dst[k * 2] = (byte)(v & 0xFF);
            dst[k * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }
        return dst;
    }

    /// <summary>
    /// **安静时自动增益**：遥控器离嘴远、房间安静时，PCM 峰值可能只有几百，
    /// 引擎的语音前端会判成"没有人在说话" ⇒ 一次 partial 都不出（实测踩到：
    /// 同样的 17 秒，音量正常的音频 partials=921，音量偏低的 0）。
    ///
    /// 保守口径：
    ///   · 只在 peak &lt; 8000（约 -12 dBFS）时放大，最多 4 倍
    ///   · 放大后峰值不超过 24000（-3 dBFS），不会削顶
    ///   · peak 本来够大就**完全不动**（避免把噪声也放大）
    /// </summary>
    private static byte[] BoostIfQuiet(byte[] pcm)
    {
        LastGain = 1.0;
        try
        {
            var peak = 0;
            for (var i = 0; i + 1 < pcm.Length; i += 2)
            {
                var v = Math.Abs((int)(short)(pcm[i] | (pcm[i + 1] << 8)));
                if (v > peak) peak = v;
            }
            if (peak == 0 || peak >= 8000) return pcm;        // 够响或全静音：不动
            var gain = Math.Min(4.0, 24000.0 / peak);
            if (gain < 1.2) return pcm;
            var outPcm = new byte[pcm.Length];
            for (var i = 0; i + 1 < pcm.Length; i += 2)
            {
                var v = (short)(pcm[i] | (pcm[i + 1] << 8));
                var x = (int)Math.Clamp(v * gain, -32000, 32000);
                outPcm[i] = (byte)(x & 0xFF);
                outPcm[i + 1] = (byte)((x >> 8) & 0xFF);
            }
            LastGain = Math.Round(gain, 2);
            Safe.Note($"ASR 音量偏低（峰值 {peak}），已放大 {LastGain}×");
            return outPcm;
        }
        catch { return pcm; }
    }

    /// <summary>取当前已攒 PCM 的副本（流式连上后补推用），不清空缓冲。</summary>
    public static byte[] Snapshot()
    {
        lock (Gate)
        {
            try { return _buf?.ToArray() ?? Array.Empty<byte>(); }
            catch { return Array.Empty<byte>(); }
        }
    }

    public static (byte[] wav, double seconds) End()
    {
        byte[] pcm;
        long n;
        lock (Gate)
        {
            if (_buf == null) return (Array.Empty<byte>(), 0);
            pcm = _buf.ToArray();
            n = _bytes;
            _buf.Dispose();
            _buf = null;
            _bytes = 0;
        }
        if (pcm.Length == 0) return (Array.Empty<byte>(), 0);
        pcm = BoostIfQuiet(pcm);
        return (LocalAsr.WrapWav(pcm), pcm.Length / 2.0 / 16000.0);
    }
}

/// <summary>
/// 一次「按住 → 松开 → 出字」的编排。
/// 单独拆出来是因为 KeyFilter 里的钩子回调**必须极快返回**，任何识别逻辑都不能在里面跑。
/// </summary>
internal static class AsrRun
{
    private static int _busy;

    private static AsrStream? _stream;

    /// <summary>诊断用：最近一次 partial 文本 / 次数 / 是否正在流式。</summary>
    public static string LastPartial => _stream?.LastPartial ?? "";
    public static long PartialCount => _stream?.Partials ?? 0;
    public static bool Streaming => _stream != null;
    public static string WsState => _stream?.State ?? "";
    public static bool WsFinalSent => _stream?.FinalSent ?? false;
    public static bool WsFinalRecv => _stream?.FinalRecv ?? false;

    /// <summary>Begin 被调用的次数（诊断用：按语音键/快捷键后这个数涨不涨，一眼判断闸门通不通）。</summary>
    public static long BeginCount;
    /// <summary>语音键闸门命中的次数（含按下/松开）。</summary>
    public static long VoiceGateHits;

    public static void Begin()
    {
        Interlocked.Increment(ref BeginCount);
        try
        {
            AsrCapture.Begin();
            AsrOverlay.Show("正在听…");
            _stream = null;
            // ★ 必须先打开音频会话：快捷键不是遥控器事件，不预热的话
            //   CableRouter 不会把 PCM 送进缓冲（录到 0 字节，表现为"没声音/没字"）
            KeyFilter.RaiseVoiceKeyDown();
            WatchSilence();
            // 后台把引擎和流拉起来：音频先进本地缓冲，连上后把已攒的部分补推过去
            Task.Run(async () =>
            {
                try
                {
                    if (Settings.AsrModel.Length == 0) { Safe.Note("ASR 未选择模型"); return; }
                    var model = System.IO.Path.Combine(Env.ConfigDir, "asr", "models", Settings.AsrModel + ".gguf");
                    var (ok, msg) = LocalAsr.Ensure(model, Settings.AsrBackend);
                    if (!ok) { Safe.Note("ASR 引擎不可用：" + msg); return; }
                    var st = await AsrStream.OpenAsync(LocalAsr.WsPort);
                    if (st == null) { Safe.Note("ASR 流式不可用，松手后走整段识别"); return; }
                    st.OnPartial += txt => AsrOverlay.ShowPartial(txt);
                    _stream = st;
                    // 补推已经攒下的音频
                    var pre = AsrCapture.Snapshot();
                    if (pre.Length > 0) st.Push(pre, pre.Length);
                    Safe.Note($"ASR 流式已连接（ws {LocalAsr.WsPort}，补推 {pre.Length / 32}ms）");
                }
                catch (Exception e) { Safe.Note("ASR 流式启动失败：" + e.Message); }
            });
            Safe.Note("ASR 开始录音");
        }
        catch (Exception e) { Safe.Note("ASR 录音开始失败：" + e.Message); }
    }

    /// <summary>音频线程调用：流已连上就实时推（未连上时靠 Snapshot 补推）。</summary>

    /// <summary>
    /// 说话期间"一点音频都没进来"的看门狗：1.5 秒还没有任何字节，
    /// 直接在浮层上写原因（否则用户只看到波形不动、以为卡住）。
    /// 常见原因：遥控器没按住 / 桥没在跑 / CABLE 没被占用。
    /// </summary>
    private static void WatchSilence()
    {
        Task.Delay(1500).ContinueWith(_ =>
        {
            try
            {
                if (!AsrCapture.Active) return;             // 已经收尾了
                if (AsrCapture.LiveBytes > 0) return;       // 有音频，正常
                AsrOverlay.ShowError("没收到音频：检查遥控器是否按住 / 语音桥是否在跑");
                Safe.Note("ASR 看门狗：1.5s 内没有音频进来");
                DumpDiag("silence", null, 0, 0);
            }
            catch { }
        });
    }
    public static void PushLive(byte[] b, int off, int len)
    {
        var st = _stream;
        st?.Push(b, off, len);
    }

    /// <summary>松开后延迟一点再取音频（等尾音进缓冲），然后在后台线程识别 + 注入。</summary>
    public static void Schedule(int delayMs)
    {
        Task.Delay(delayMs).ContinueWith(_ => Run());
    }

    /// <summary>松手后的收尾：有流就走流式（等 final），没有就回落整段。</summary>
    /// <summary>识别结果统一出口：术语纠正 → 规则润色。</summary>
    private static string Finish(string? raw)
    {
        var src = raw ?? "";
        var t = Glossary.Fix(src);
        if (Settings.PolishRules) t = TextPolish.Apply(t);
        if (t != src && src.Length > 0) Safe.Note($"润色：{src} → {t}");
        return t;
    }

    /// <summary>
    /// 把"这一次识别"的现场写成 json（成功/失败都写）。
    /// 为什么需要：带界面的实例没有测试管道，日志又被"记录事件日志"开关挡着，
    /// 出问题只能靠猜 —— 这个文件绕过两者，任何一次识别都能事后回看。
    /// </summary>
    /// <summary>最近一次录音的电平统计（判断"是不是声音太小导致引擎不出字"）。</summary>
    private static (double rms, int peak, double clipRatio) AudioStats(byte[] wav)
    {
        try
        {
            var start = 44;                       // 跳过 WAV 头
            if (wav.Length <= start) return (0, 0, 0);
            double sum = 0; var n = 0; var peak = 0; var clip = 0;
            for (var i = start; i + 1 < wav.Length; i += 2)
            {
                var v = Math.Abs((int)(short)(wav[i] | (wav[i + 1] << 8)));
                if (v > peak) peak = v;
                if (v >= 32000) clip++;
                sum += (double)v * v; n++;
            }
            return (n == 0 ? 0 : Math.Sqrt(sum / n), peak, n == 0 ? 0 : (double)clip / n);
        }
        catch { return (0, 0, 0); }
    }

    private static void DumpDiag(string phase, AsrStream? st, int wavBytes, double sec, string extra = "", byte[]? wav = null)
    {
        try
        {
            var (rms, peak, clip) = wav != null ? AudioStats(wav) : (0.0, 0, 0.0);
            if (wav != null && wav.Length > 44)
            {
                try
                {
                    System.IO.File.WriteAllBytes(
                        System.IO.Path.Combine(Env.ConfigDir, "asr-last.wav"), wav);
                }
                catch { }
            }
            var obj = new
            {
                at = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                phase,
                streaming = st != null,
                partials = st?.Partials ?? 0,
                wsState = st?.State ?? "",
                finalSent = st?.FinalSent ?? false,
                finalRecv = st?.FinalRecv ?? false,
                wavBytes, sec = Math.Round(sec, 2),
                bytesPerSec = sec > 0.1 ? (int)(wavBytes / sec) : 0,
                rms = Math.Round(rms, 1), peak, clipRatio = Math.Round(clip, 4), gain = AsrCapture.LastGain,
                lastPartial = st?.LastPartial ?? "",
                lastErr = LocalAsr.LastErr,
                extra,
            };
            var path = System.IO.Path.Combine(Env.ConfigDir, "asr-last.json");
            System.IO.File.AppendAllText(path,
                System.Text.Json.JsonSerializer.Serialize(obj) + Environment.NewLine,
                new System.Text.UTF8Encoding(true));
        }
        catch { }
    }

    private static void Run()
    {
        var st = _stream;
        if (st != null)
        {
            _stream = null;
            AsrOverlay.Recognizing();
            try
            {
                // ★ 先取音频并**保住**：流式失败时还要用它走整段兜底。
                //   上一版是先 FinishAsync 再 End()，失败后音频已丢 ⇒ 兜底必然 0 字节 ⇒ 卡住。
                var (wav, sec) = AsrCapture.End();
                var raw = st.FinishAsync().GetAwaiter().GetResult();
                DumpDiag("stream-finish", st, wav.Length, sec, "raw=" + (raw ?? ""), wav);
                var text = Finish(raw);
                if (text.Length > 0)
                {
                    LocalAsr.LastText = text; LocalAsr.LastSec = sec;
                    LocalAsr.LastAt = Environment.TickCount64; LocalAsr.LastErr = "";
                    AsrOverlay.ShowResult(text);
                    Safe.Note($"ASR 流式成功（{st.Partials} 次 partial / {sec:F1}s）：「{text}」");
                    LocalAsr.TypeText(text);
                    return;
                }
                // 流式没拿到最终文本 ⇒ 用保住的音频走整段
                if (wav.Length > 0)
                {
                    Safe.Note($"ASR 流式空结果（partial {st.Partials}，ws={st.State}），改用整段识别兜底");
                    RunBatchWith(wav, sec);
                    return;
                }
                DumpDiag("stream-empty-noaudio", st, wav.Length, sec, "", wav);
                AsrOverlay.ShowError("没录到音频");
                Safe.Note("ASR 流式空结果且无音频");
            }
            catch (Exception e)
            {
                AsrOverlay.ShowError("流式收尾异常：" + e.Message);
                Safe.Note("ASR 流式收尾异常：" + e.Message);
            }
            return;    // 走到这里说明已有结论，不要再落到 RunBatch（音频已被取走）
        }
        DumpDiag("batch-enter", null, 0, 0);
        RunBatch();
    }

    /// <summary>用给定的 wav 走整段识别（流式兜底用）。</summary>
    private static void RunBatchWith(byte[] wav, double sec)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var model = System.IO.Path.Combine(Env.ConfigDir, "asr", "models", Settings.AsrModel + ".gguf");
            var (ok, msg) = LocalAsr.Ensure(model, Settings.AsrBackend);
            if (!ok) { AsrOverlay.ShowError("引擎不可用：" + msg); return; }
            var (tok, raw, ms) = LocalAsr.TranscribeAsync(wav).GetAwaiter().GetResult();
            var text = Finish(raw);
            if (!tok || text.Length == 0)
            {
                var (r2, p2, _) = AudioStats(wav);
                AsrOverlay.ShowError(LocalAsr.LastErr.Length > 0
                    ? LocalAsr.LastErr
                    : $"识别无结果（音量 {p2}，{(p2 < 800 ? "偏小，请靠近麦克风" : "正常")}）");
                return;
            }
            LocalAsr.LastText = text; LocalAsr.LastSec = sec;
            LocalAsr.LastAt = Environment.TickCount64; LocalAsr.LastErr = "";
            AsrOverlay.ShowResult(text);
            Safe.Note($"ASR 整段兜底成功（{sec:F1}s / {ms}ms）：「{text}」");
            LocalAsr.TypeText(text);
        }
        catch (Exception e) { AsrOverlay.ShowError("兜底异常：" + e.Message); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    private static void RunBatch()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;   // 上一句还在识别，直接跳过
        try
        {
            AsrOverlay.Recognizing();
            var (wav, sec) = AsrCapture.End();
            if (wav.Length == 0) { Safe.Note("ASR 没录到音频（检查音频链是否在跑）"); return; }
            if (sec < 0.25) { Safe.Note($"ASR 跳过：太短（{sec:F2}s）"); return; }

            if (Settings.AsrModel.Length == 0)
            {
                Safe.Note("ASR 未选择模型 —— 环境设置 → 本地语音识别 里下载/选用一个");
                return;
            }
            var model = Path.Combine(Env.ConfigDir, "asr", "models", Settings.AsrModel + ".gguf");
            var (ok, msg) = LocalAsr.Ensure(model, Settings.AsrBackend);
            if (!ok) { Safe.Note("ASR 引擎不可用：" + msg); return; }

            LocalAsr.LastCaptureBytes = wav.Length;
            var (tok, rawText, ms) = LocalAsr.TranscribeAsync(wav).GetAwaiter().GetResult();
            var text = Finish(rawText);
            if (!tok) { AsrOverlay.Hide(); Safe.Note($"ASR 转写失败（{sec:F1}s / {wav.Length} 字节）：{LocalAsr.LastErr}"); return; }
            if (text.Length == 0) { LocalAsr.LastErr = "空结果"; Safe.Note($"ASR 空结果（{sec:F1}s 音频 / {ms}ms）"); return; }

            AsrOverlay.ShowResult(text);
            LocalAsr.LastText = text; LocalAsr.LastMs = ms; LocalAsr.LastSec = sec;
            LocalAsr.LastAt = Environment.TickCount64; LocalAsr.LastErr = "";
            Safe.Note($"ASR 成功：{sec:F1}s 音频 → {ms}ms，「{text}」");
            if (!LocalAsr.TypeText(text)) Safe.Note("ASR 文本注入可能未全部生效（目标程序可能拒绝 Unicode 注入）");
        }
        catch (Exception e) { Safe.Note("ASR 执行异常：" + e.Message); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }
}
