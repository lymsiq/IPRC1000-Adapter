using System.Linq;
using NAudio.Wave;

namespace Iprc1000Adapter;

/// <summary>
/// **有界**抖动缓冲：<see cref="WaveFormat"/> 48 kHz / 16-bit / 单声道。
///
/// ## 为什么要有界（参数取自 macOS 版的实测）
///
/// macOS 版（`lymsiq/IPRC1000-Mac`，`BlackHoleOutput.swift`）实测了**帧到达间隔**：
/// **中位数 24 ms、最小 3 ms、最大 50 ms** —— 是**成簇到达**的，而虚拟声卡按稳定时钟拉取。
/// 所以必须有抖动缓冲，但**必须有界**：
/// 无上限的环形缓冲会把积压变成成百毫秒的延迟（"越说越慢"），
/// 有界则把"越来越慢"换成"偶尔丢一点"——这是**刻意的取舍**。
///
/// 我们这边原来是 `BufferedWaveProvider` + `BufferDuration = 1 秒`：
/// `DiscardOnBufferOverflow` 虽然开着，但**要攒到整整 1 秒才裁** ⇒ 最坏 1 秒延迟。
///
/// ## 四个参数（都可用环境变量调，见 <see cref="JitterOptions"/>）
///
/// | 参数 | 默认 | 作用 |
/// |---|---|---|
/// | 起播攒 | **40 ms** | 攒够这么多才出真实音频，防一上来就断音 |
/// | 积压上限 | **64 ms** | 超过就**丢最旧的**（60 ms 已经能盖住 50 ms 的抖动）|
/// | 判新会话 | **400 ms** | 距上次写入超过它 → 清空重来，不把上一段的尾巴粘过来 |
/// | 声卡 IO 缓冲 | **30 ms** | `WasapiOut` 的 desiredLatency（原来 50 ms）|
///
/// ⚠ **积压上限必须 ≥ 起播阈值**，否则永远攒不够 ⇒ 永久静音。<see cref="JitterOptions.Sanitize"/> 里夹住。
///
/// 线程模型：`AddSamples` 来自 UDP 接收线程，`Read` 来自声卡线程 —— 内部一把锁，
/// 两边的单次工作量都是"几十微秒级的 memcpy"，不会互相拖住。
/// </summary>
internal sealed class JitterBuffer : IWaveProvider
{
    // ── 参数 ──
    public sealed class JitterOptions
    {
        /// <summary>起播前先攒多少毫秒（防一上来就断音）</summary>
        public int PreRollMs = 40;
        /// <summary>积压上限（毫秒）；超过就丢最旧的</summary>
        public int CapMs = 64;
        /// <summary>距上次写入超过它 → 视为新会话，清空重来</summary>
        public int StaleMs = 400;
        /// <summary>声卡 IO 缓冲（毫秒），交给 WasapiOut 的 desiredLatency</summary>
        public int LatencyMs = 30;

        /// <summary>
        /// 唯一的校验闸 —— 环境变量与代码默认值都过这里。
        /// （项目约定：用户能手改的输入要和界面输入同等对待。）
        /// </summary>
        public JitterOptions Sanitize()
        {
            PreRollMs = Math.Clamp(PreRollMs, 0, 200);
            CapMs = Math.Clamp(CapMs, 20, 500);
            StaleMs = Math.Clamp(StaleMs, 50, 5000);
            LatencyMs = Math.Clamp(LatencyMs, 5, 200);
            // ★ 上限必须 ≥ 起播阈值：否则攒不到起播线，就**永远是静音**
            if (CapMs < PreRollMs) CapMs = PreRollMs;
            return this;
        }

        /// <summary>从环境变量读（`IPRC1000_JITTER_PREROLL_MS` 等），读不到或不合法就用默认值</summary>
        public static JitterOptions FromEnv()
        {
            var o = new JitterOptions();
            int Get(string name, int dflt)
            {
                var s = Environment.GetEnvironmentVariable(name);
                return int.TryParse(s, out var v) ? v : dflt;
            }
            o.PreRollMs = Get("IPRC1000_JITTER_PREROLL_MS", o.PreRollMs);
            o.CapMs = Get("IPRC1000_JITTER_CAP_MS", o.CapMs);
            o.StaleMs = Get("IPRC1000_JITTER_STALE_MS", o.StaleMs);
            o.LatencyMs = Get("IPRC1000_CABLE_LATENCY_MS", o.LatencyMs);
            return o.Sanitize();
        }
    }

    public WaveFormat WaveFormat { get; }

    private readonly JitterOptions _opt;
    /// <summary>每毫秒字节数（48 kHz × 16 bit × 1ch ÷ 1000 = 96）</summary>
    private readonly int _bytesPerMs;

    private readonly byte[] _ring;
    private readonly int _mask;          // 容量为 2 的幂 → 用按位与代替取模
    private int _head;                   // 读位置
    private int _tail;                   // 写位置
    private int _avail;                  // 可读字节数
    private readonly object _lock = new();

    private bool _priming;               // true = 还在攒起播量（此时输出静音）
    private long _lastAddMs;             // 上次写入的墙钟毫秒（判新会话用）

    // ── 统计（给界面与日志用）──
    public long AddedBytes { get; private set; }
    public long ReadBytes { get; private set; }
    /// <summary>数据不够、补静音的**读取次数**（= 断音次数）</summary>
    public long Underruns { get; private set; }
    /// <summary>因积压超上限而丢掉的字节数（丢的是**最旧的**）</summary>
    public long DroppedBytes { get; private set; }
    /// <summary>峰值积压（毫秒）—— 长期贴近上限说明"偶尔丢一点"在起作用</summary>
    public long PeakBacklogMs { get; private set; }
    /// <summary>因为静默太久而清空重来的次数</summary>
    public long StaleResets { get; private set; }
    /// <summary>进入起播等待的次数</summary>
    public long Primings { get; private set; }

    public JitterOptions Options => _opt;

    public JitterBuffer(JitterOptions? opt = null)
    {
        _opt = (opt ?? JitterOptions.FromEnv()).Sanitize();
        WaveFormat = new WaveFormat(48000, 16, 1);
        _bytesPerMs = WaveFormat.AverageBytesPerSecond / 1000;   // 96

        // 容量：够放"上限 + 一个最大 UDP 包（1920 B）×2"再取 2 的幂。
        // 给得宽一点只花内存，不留余量反而会在写入时被迫裁掉刚进来的新数据。
        var need = _opt.CapMs * _bytesPerMs + 65536;
        var cap = 1;
        while (cap < need) cap <<= 1;
        _ring = new byte[cap];
        _mask = cap - 1;
        _priming = _opt.PreRollMs > 0;
        if (_priming) Primings = 1;
    }

    private int PreRollBytes => _opt.PreRollMs * _bytesPerMs;
    private int CapBytes => _opt.CapMs * _bytesPerMs;

    /// <summary>当前可读字节</summary>
    public int AvailableBytes { get { lock (_lock) return _avail; } }
    /// <summary>当前积压（毫秒）—— 这就是额外引入的延迟</summary>
    public double BacklogMs { get { lock (_lock) return (double)_avail / _bytesPerMs; } }
    /// <summary>是否已在出真实音频（false = 还在攒起播量）</summary>
    public bool Playing { get { lock (_lock) return !_priming; } }

    /// <summary>
    /// 写入一段 48 kHz/16-bit/mono PCM（来自 UDP）。
    /// 顺序很重要：**先判新会话（清旧尾巴），再裁积压，最后写入**。
    /// </summary>
    public void AddSamples(byte[] data, int offset, int count)
        => AddSamples(data, offset, count, Environment.TickCount64);

    /// <summary>
    /// 同上，但**时间由调用方给** —— 只为了让"静默 400 ms 判新会话"这条规则可被自检验证
    /// （`Environment.TickCount64` 没法在单测里拨动）。
    /// </summary>
    internal void AddSamples(byte[] data, int offset, int count, long nowMs)
    {
        if (count <= 0 || offset < 0 || offset + count > data.Length) return;
        var now = nowMs;
        lock (_lock)
        {
            // ① 距上次写入超过 StaleMs ⇒ 视为新会话：把上一段的残留丢掉
            if (_lastAddMs != 0 && now - _lastAddMs > _opt.StaleMs && _avail > 0)
            {
                _avail = 0; _head = 0; _tail = 0;
                StaleResets++;
                _priming = _opt.PreRollMs > 0;
                if (_priming) Primings++;
            }

            // ② 写入；超容量时（理论上不该发生，容量留了余量）从最旧的丢
            if (count > _ring.Length) { offset += count - _ring.Length; count = _ring.Length; }
            if (_avail + count > _ring.Length)
            {
                var over = _avail + count - _ring.Length;
                _head = (_head + over) & _mask;
                _avail -= over;
                DroppedBytes += over;
            }
            for (var i = 0; i < count; i++) _ring[(_tail + i) & _mask] = data[offset + i];
            _tail = (_tail + count) & _mask;
            _avail += count;
            AddedBytes += count;

            // ③ 积压超上限 ⇒ 丢**最旧的**（而不是不写新数据 —— 新数据才是用户刚说的）
            if (_avail > CapBytes)
            {
                var over = _avail - CapBytes;
                _head = (_head + over) & _mask;
                _avail -= over;
                DroppedBytes += over;
            }

            var backlogMs = _avail / _bytesPerMs;
            if (backlogMs > PeakBacklogMs) PeakBacklogMs = backlogMs;
            _lastAddMs = now;
        }
    }

    /// <summary>
    /// 声卡线程拉数据。
    /// **总是填满 <paramref name="count"/>**：数据不够就补静音 ——
    /// 这正是"CABLE Output 是一路常驻麦克风"的模型（安静时输出静音，不输出过期音频）。
    /// </summary>
    public int Read(byte[] buffer, int offset, int count)
    {
        lock (_lock)
        {
            // 还没攒够起播量（或刚从空转回来）→ 静音，继续攒
            if (_priming)
            {
                if (_avail < PreRollBytes) { Fill(buffer, offset, count, 0); return count; }
                _priming = false;
            }

            var n = Math.Min(_avail, count);
            if (n > 0)
            {
                for (var i = 0; i < n; i++) buffer[offset + i] = _ring[(_head + i) & _mask];
                _head = (_head + n) & _mask;
                _avail -= n;
                ReadBytes += n;
            }
            if (n < count)
            {
                Fill(buffer, offset + n, count - n, 0);
                Underruns++;
                // 读空了 → 下一段数据重新攒起播量（避免把两段之间接成一条）
                if (_avail == 0 && _opt.PreRollMs > 0)
                {
                    _priming = true;
                    Primings++;
                }
            }
            return count;
        }
    }

    private static void Fill(byte[] b, int offset, int count, byte v)
    {
        for (var i = 0; i < count; i++) b[offset + i] = v;
    }

    /// <summary>清空（显式复位用；平时靠 <see cref="JitterOptions.StaleMs"/> 自动判新会话）</summary>
    public void Reset()
    {
        lock (_lock) { _avail = 0; _head = 0; _tail = 0; _priming = _opt.PreRollMs > 0; }
    }

    // ───────────────────────── 自检（--jitter-test）─────────────────────────

    /// <summary>
    /// 有界抖动缓冲自检 —— **不需要声卡、不需要遥控器**，纯内存驱动。
    ///
    /// 抄 macOS 版的做法：他们用**合成抓包**做端到端自测（"写进去多少帧就解出多少帧"），
    /// 而不是等真机复现。抖动缓冲的参数全靠实测定，所以更要用合成数据把边界钉住。
    ///
    /// 断言：
    ///   ① 格式是 48 kHz / 16-bit / 单声道（写死错了整条链路都是噪音）
    ///   ② 起播阈值：没攒够 ~40 ms 之前**只输出静音**，攒够后立刻出真实数据
    ///   ③ 积压上限：一次灌 5 包（100 ms）→ 积压被裁到 ≤ 64 ms
    ///   ④ 丢的是**最旧的**：保留下来的是最新那几包（不是"不写新数据"）
    ///   ⑤ 空读补静音：返回长度恒等于请求长度（常驻麦克风模型），并记一次 underrun
    ///   ⑥ 静默 >400 ms 判新会话：旧尾巴被清掉，不粘到新一段上
    ///   ⑦ 参数越界被夹住（含"上限 < 起播阈值"这条会导致永久静音的坑）
    /// </summary>
    public static string SelfTest()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("IPRC1000 抖动缓冲自检（有界，不需要声卡）");
        sb.AppendLine(new string('=', 58));
        var ok = true;
        void Check(bool pass, string what, string detail)
        {
            ok &= pass;
            sb.AppendLine($"  {(pass ? "✓" : "✗")} {what}");
            sb.AppendLine($"       {detail}");
        }

        const int PKT = 1920;           // 桥每包 1920 字节 = 20 ms @48k/16bit/mono
        static byte[] Packet(byte v) { var a = new byte[PKT]; Array.Fill(a, v); return a; }

        // ① 格式
        var jb0 = new JitterBuffer(new JitterOptions { PreRollMs = 40, CapMs = 64 });
        Check(jb0.WaveFormat.SampleRate == 48000 && jb0.WaveFormat.BitsPerSample == 16
              && jb0.WaveFormat.Channels == 1,
              "格式 = 48 kHz / 16-bit / 单声道",
              $"{jb0.WaveFormat.SampleRate} Hz / {jb0.WaveFormat.BitsPerSample} bit / "
              + $"{jb0.WaveFormat.Channels} ch（每毫秒 {jb0.WaveFormat.AverageBytesPerSecond / 1000} 字节）");

        // ② 起播阈值
        var jb = new JitterBuffer(new JitterOptions { PreRollMs = 40, CapMs = 64 });
        var outBuf = new byte[PKT];
        jb.AddSamples(Packet(7), 0, PKT);                       // 20 ms，不够 40 ms
        jb.Read(outBuf, 0, PKT);
        var silent = outBuf.All(b => b == 0);
        jb.AddSamples(Packet(7), 0, PKT);                       // 再 20 ms → 够 40 ms
        jb.Read(outBuf, 0, PKT);
        var real = outBuf.Any(b => b == 7);
        Check(silent && real, "起播阈值 40 ms：没攒够只出静音，攒够立刻出真实数据",
              $"20 ms 时输出{(silent ? "静音" : "★ 真实数据（错）")}，40 ms 时输出"
              + $"{(real ? "真实数据" : "★ 仍是静音（错）")}");

        // ③④ 积压上限 + 丢最旧
        var jb2 = new JitterBuffer(new JitterOptions { PreRollMs = 0, CapMs = 64 });
        for (byte i = 1; i <= 5; i++) jb2.AddSamples(Packet(i), 0, PKT);   // 共 100 ms
        var avail = jb2.AvailableBytes;
        var capB = 64 * 96;                                     // 6144 字节
        var buf = new byte[avail];
        jb2.Read(buf, 0, avail);
        var firstVal = buf[0];
        var lastVal = buf[buf.Length - 1];
        var nFirst = buf.Count(b => b == firstVal);
        Check(avail <= capB, "积压被裁到上限以内（64 ms）",
              $"灌 5 包 = {5 * PKT} 字节（100 ms），实际留下 {avail} 字节（{avail / 96.0:F1} ms）");
        // 保留的应是"最新"那段：末尾是第 5 包；开头落在第 2 包（3456 = 1920 + 1536）
        Check(lastVal == 5 && firstVal == 2 && nFirst == 384,
              "丢的是**最旧的**（保留最新数据，不是丢新数据）",
              $"首字节值 {firstVal}（期望 2）· 末字节值 {lastVal}（期望 5）· "
              + $"首值连续 {nFirst} 字节（期望 384 = 第 2 包被裁剩的部分）");

        // ⑤ 空读补静音
        var jb3 = new JitterBuffer(new JitterOptions { PreRollMs = 0, CapMs = 64 });
        var b3 = new byte[960];
        var r3 = jb3.Read(b3, 0, 960);
        Check(r3 == 960 && b3.All(b => b == 0) && jb3.Underruns == 1,
              "空读补静音：返回长度 = 请求长度，并记一次 underrun",
              $"Read 返回 {r3}（期望 960）· 全是静音 · underruns={jb3.Underruns}（期望 1）");

        // ⑥ 静默 > 400 ms 判新会话
        var jb4 = new JitterBuffer(new JitterOptions { PreRollMs = 0, CapMs = 64, StaleMs = 400 });
        jb4.AddSamples(Packet(1), 0, PKT, 1000);                // 旧一段
        jb4.AddSamples(Packet(2), 0, PKT, 2000);                // 1 秒后 = 新一段
        var b4 = new byte[jb4.AvailableBytes];
        jb4.Read(b4, 0, b4.Length);
        Check(b4.Length == PKT && b4.All(b => b == 2) && jb4.StaleResets == 1,
              "静默 > 400 ms 判新会话：旧尾巴被清掉",
              $"留下 {b4.Length} 字节（期望 {PKT}，即只剩新一段）· 全为新数据 · "
              + $"staleResets={jb4.StaleResets}（期望 1）");

        // ⑦ 参数夹取（含"上限 < 起播阈值 ⇒ 永久静音"这个坑）
        var bad = new JitterOptions { PreRollMs = 500, CapMs = 5, StaleMs = 1, LatencyMs = 9999 };
        bad.Sanitize();
        Check(bad.PreRollMs == 200 && bad.CapMs >= bad.PreRollMs && bad.StaleMs == 50
              && bad.LatencyMs == 200,
              "参数越界被夹住，且上限 ≥ 起播阈值",
              $"起播 {bad.PreRollMs}（夹到 200）· 上限 {bad.CapMs}（夹到 200，**必须 ≥ 起播**）"
              + $" · 新会话 {bad.StaleMs}（夹到 50）· 声卡缓冲 {bad.LatencyMs}（夹到 200）");

        sb.AppendLine();
        sb.AppendLine($"当前生效参数（环境变量可调）：起播 {jb0.Options.PreRollMs} ms · "
                      + $"上限 {jb0.Options.CapMs} ms · 判新会话 {jb0.Options.StaleMs} ms · "
                      + $"声卡缓冲 {jb0.Options.LatencyMs} ms");
        sb.AppendLine(ok ? "⇒ 全部通过 ✓" : "⇒ ★ 有断言失败");
        return sb.ToString();
    }

    public object Status() => new
    {
        preRollMs = _opt.PreRollMs,
        capMs = _opt.CapMs,
        staleMs = _opt.StaleMs,
        latencyMs = _opt.LatencyMs,
        backlogMs = Math.Round(BacklogMs, 1),
        peakBacklogMs = PeakBacklogMs,
        playing = Playing,
        underruns = Underruns,
        droppedBytes = DroppedBytes,
        staleResets = StaleResets,
        primings = Primings,
    };
}
