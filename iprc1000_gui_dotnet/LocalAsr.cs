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
        }
    }

    /// <summary>音频线程调用（每帧 316~640 字节），必须极轻。</summary>
    public static void Feed(byte[] b, int off, int len)
    {
        MemoryStream? buf;
        lock (Gate) { buf = _buf; if (buf == null) return; _bytes += len; }
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
        return (pcm.Length == 0 ? Array.Empty<byte>() : LocalAsr.WrapWav(pcm),
                pcm.Length / 2.0 / 16000.0);
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

    private static void Run()
    {
        var st = _stream;
        if (st != null)
        {
            _stream = null;
            AsrOverlay.Recognizing();
            try
            {
                var text = Finish(st.FinishAsync().GetAwaiter().GetResult());
                var sec = AsrCapture.End().seconds;      // 清空缓冲，顺便拿时长
                if (text.Length > 0)
                {
                    LocalAsr.LastText = text; LocalAsr.LastSec = sec;
                    LocalAsr.LastAt = Environment.TickCount64; LocalAsr.LastErr = "";
                    AsrOverlay.ShowResult(text);
                    Safe.Note($"ASR 流式成功（{st.Partials} 次 partial / {sec:F1}s）：「{text}」");
                    LocalAsr.TypeText(text);
                    return;
                }
                Safe.Note($"ASR 流式无结果（partial {st.Partials} 次），回落整段");
            }
            catch (Exception e) { Safe.Note("ASR 流式收尾异常：" + e.Message); }
        }
        RunBatch();
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
