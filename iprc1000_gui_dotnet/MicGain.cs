using System.Runtime.InteropServices;
using System.Text.Json;

namespace Iprc1000Adapter;

/// <summary>
/// **麦克风增益 + 实时电平表**（作用在写进 CABLE 之前的 48 kHz / 16-bit 采样上）。
///
/// ## 为什么放在这里，而不是语音桥
///
/// 1. **立刻生效** —— 滑块一拖就变。放桥里就得重启桥（丢 1 秒音频 + 重新订阅 ETW）。
/// 2. 增益是**线性运算**，放在重采样之前或之后**等价**，所以放后缀（桥）没有音质优势。
/// 3. 同一次遍历顺手把电平算出来 —— 电平表是滑块能不能用的前提（见下）。
///
/// ## 为什么"必须"配电平表
///
/// 只有一个滑块时，用户唯一的反馈是"戴着耳机听"，只能反复试。更糟的是：**一直往上拉**。
/// 增益过大会把波形削平（撞到 ±32767），声音发毛、识别率反而下降 —— 用户却以为"更响=更好"。
/// 所以这里同时给三样东西：**实时电平条**、**峰值 dBFS**、**削波计数**。
///
/// 线程模型：`Apply` 在路由线程（每包一次，~50 次/秒），`Set` 在界面线程。
/// 只用一个 `volatile int` 传 dB 值、每包重算一次倍数 —— 没有锁，也不会读到半截。
/// </summary>
internal sealed class MicGain
{
    public const int MinDb = -20;
    public const int MaxDb = 30;
    public const int DefaultDb = 0;

    /// <summary>电平条的下限（dBFS）。比这更小的声音一律显示为空。</summary>
    private const double FloorDb = -60.0;

    private volatile int _db = DefaultDb;
    private readonly string? _file;

    // ── 电平统计（只由路由线程写）──
    private volatile int _levelPermille;     // 0..1000，界面直接读
    private int _levelRaw;                   // 块间衰减用的内部值（0..1000）
    private volatile int _peakDbfs10;        // 峰值 dBFS × 10（整数便于跨线程读）
    private long _clipped;                   // 累计削波样本数
    private int _clipRecent;                 // 自上次界面取数以来的削波样本数

    public MicGain(int db = DefaultDb, string? file = null)
    {
        _file = file;
        if (file != null) db = LoadFrom(file, db);
        _db = Clamp(db);
        _peakDbfs10 = -600;
    }

    /// <summary>当前增益（整数 dB）</summary>
    public int Db => _db;
    /// <summary>电平表读数 0..1000（界面读这个）</summary>
    public int LevelPermille => _levelPermille;
    /// <summary>峰值保持（dBFS，负数；−60 以下当作静音）</summary>
    public double PeakDbfs => _peakDbfs10 / 10.0;
    /// <summary>累计削波样本数</summary>
    public long Clipped => _clipped;
    /// <summary>当前线性倍数（+6 dB ≈ ×2.0）</summary>
    public double Factor => Math.Pow(10, _db / 20.0);

    /// <summary>设增益（越界夹住）。返回是否发生了变化。</summary>
    public bool Set(int db)
    {
        db = Clamp(db);
        if (db == _db) return false;
        _db = db;
        if (_file != null) SaveTo(_file, db);
        return true;
    }

    private static int Clamp(int db) => Math.Clamp(db, MinDb, MaxDb);

    /// <summary>
    /// 就地缩放一段 16-bit LE PCM，并更新电平统计。
    ///
    /// **削波是"夹住"而不是"环绕"** —— 环绕会把正峰变成负谷，听起来是刺耳的爆音；
    /// 夹住只是波形变方（失真但可懂），而且能被计数报警。
    /// </summary>
    /// <returns>本次被削平的样本数</returns>
    public int Apply(byte[] buf, int offset, int count)
    {
        if (count < 2 || offset < 0 || offset + count > buf.Length) return 0;
        var db = _db;
        var factor = db == 0 ? 1.0 : Math.Pow(10, db / 20.0);

        var samples = MemoryMarshal.Cast<byte, short>(buf.AsSpan(offset, count & ~1));
        var clipped = 0;
        var peak = 0;
        double sumSq = 0;

        for (var i = 0; i < samples.Length; i++)
        {
            var v = samples[i];
            if (db != 0)
            {
                var scaled = v * factor;
                if (scaled > short.MaxValue) { scaled = short.MaxValue; clipped++; }
                else if (scaled < short.MinValue) { scaled = short.MinValue; clipped++; }
                v = (short)scaled;
                samples[i] = v;
            }
            var a = Math.Abs((int)v);
            if (a > peak) peak = a;
            sumSq += (double)v * v;
        }

        if (clipped > 0)
        {
            _clipped += clipped;
            _clipRecent += clipped;
        }
        UpdateLevel(peak, samples.Length == 0 ? 0 : Math.Sqrt(sumSq / samples.Length));
        return clipped;
    }

    /// <summary>
    /// 电平表：取本块的 RMS 与峰值中**较大**的那个（RMS 更能反映"有效响度"，
    /// 但短促的爆破音只有峰值抓得住），换算成 dBFS 后映射到 0..1000，
    /// 再按块做衰减 —— 这样界面 10 Hz 轮询也能看到平滑跳动的表。
    /// </summary>
    private void UpdateLevel(int peak, double rms)
    {
        var inst = DbToPermille(rms > 0 ? 20 * Math.Log10(rms / 32768.0) : -600);
        if (peak > 0)
        {
            var pk = DbToPermille(20 * Math.Log10(peak / 32768.0));
            if (pk > inst) inst = pk;
        }
        // 快起慢落：本块更大就直接吃进来，否则按 0.82 衰减（约 50 块/秒 → 每 100 ms 掉 1/3）
        _levelRaw = inst > _levelRaw ? inst : (int)(_levelRaw * 0.82);
        _levelPermille = _levelRaw;

        var peakDb = peak > 0 ? 20 * Math.Log10(peak / 32768.0) : -600;
        var cur = _peakDbfs10;
        // 峰值保持：涨就立刻跟，落得慢（每块最多掉 1 dB）
        var next = (int)Math.Round(peakDb * 10);
        _peakDbfs10 = next > cur ? next : Math.Max(next, cur - 10);
    }

    private static int DbToPermille(double dbfs)
    {
        if (dbfs <= FloorDb) return 0;
        if (dbfs >= 0) return 1000;
        return (int)Math.Round((dbfs - FloorDb) / (0 - FloorDb) * 1000);
    }

    /// <summary>界面取一次"自上次以来的削波数"并清零（用于闪烁提示）</summary>
    public int TakeClipRecent()
    {
        var n = _clipRecent;
        _clipRecent = 0;
        return n;
    }

    public object Status() => new
    {
        db = _db,
        minDb = MinDb,
        maxDb = MaxDb,
        factor = Math.Round(Factor, 2),
        level = LevelPermille,                // 0..1000
        peakDbfs = Math.Round(PeakDbfs, 1),
        clipped = Clipped,
    };

    // ── 持久化（audio.json）──
    private static int LoadFrom(string file, int dflt)
    {
        try
        {
            if (!System.IO.File.Exists(file)) return dflt;
            using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(file));
            // ★ 与 mouse.json 同一套纪律：手改的文件也要过同一道校验闸
            if (doc.RootElement.TryGetProperty("gainDb", out var g) && g.TryGetInt32(out var v))
                return Clamp(v);
        }
        catch { /* 损坏就用默认值 */ }
        return dflt;
    }

    private static void SaveTo(string file, int db)
    {
        try { System.IO.File.WriteAllText(file, $"{{\n  \"gainDb\": {db}\n}}\n"); }
        catch { /* 写不进去不影响当前会话 */ }
    }

    // ───────────────────────── 自检（--micgain-test）─────────────────────────

    /// <summary>
    /// 纯逻辑自检：**不碰声卡、不碰网络**。
    ///
    /// 这类"就地改采样"的代码最容易出的三个静默故障：
    ///   ① 0 dB 时仍改动数据（多余的往返、可能引入量化误差）；
    ///   ② 溢出**环绕**而不是夹住（正峰翻成负谷 → 刺耳爆音）；
    ///   ③ 削波了却不计数（用户把增益拉到 +30 dB，声音已经废了却没有任何提示）。
    /// </summary>
    public static string SelfTest()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("IPRC1000 麦克风增益自检（纯逻辑，不需要声卡）");
        sb.AppendLine(new string('=', 58));
        var ok = true;
        void Check(bool pass, string what, string detail)
        {
            ok &= pass;
            sb.AppendLine($"  {(pass ? "✓" : "✗")} {what}");
            sb.AppendLine($"       {detail}");
        }

        static byte[] Make(params short[] v)
        {
            var b = new byte[v.Length * 2];
            for (var i = 0; i < v.Length; i++)
            {
                b[i * 2] = (byte)(v[i] & 0xFF);
                b[i * 2 + 1] = (byte)((v[i] >> 8) & 0xFF);
            }
            return b;
        }
        static short[] Read(byte[] b)
        {
            var v = new short[b.Length / 2];
            for (var i = 0; i < v.Length; i++) v[i] = (short)(b[i * 2] | (b[i * 2 + 1] << 8));
            return v;
        }

        // ① 0 dB 是恒等变换
        var g0 = new MicGain(0);
        var d0 = Make(1000, -1000, 32767, -32768, 0);
        var before = (byte[])d0.Clone();
        g0.Apply(d0, 0, d0.Length);
        Check(d0.SequenceEqual(before), "0 dB 不改动任何采样（逐字节相同）",
              $"因子={g0.Factor:F3}（应为 1.000）· 5 个采样原样保留");

        // ② +6 dB ≈ ×2
        var g6 = new MicGain(6);
        var d6 = Make(1000, -1000);
        g6.Apply(d6, 0, d6.Length);
        var r6 = Read(d6);
        Check(Math.Abs(g6.Factor - 1.995) < 0.01 && Math.Abs(r6[0] - 1995) <= 1,
              "+6 dB ≈ ×2（1000 → 1995 左右）",
              $"因子={g6.Factor:F3} · 输出=[{r6[0]}, {r6[1]}]");

        // ③ 溢出必须"夹住"而不是"环绕"
        var g12 = new MicGain(12);
        var d12 = Make(30000, -30000);
        var n12 = g12.Apply(d12, 0, d12.Length);
        var r12 = Read(d12);
        var clamped = r12[0] == short.MaxValue && r12[1] == short.MinValue;
        Check(clamped && n12 == 2,
              "溢出被夹在 ±32767/‑32768（**不环绕**），且计数 = 2",
              $"输出=[{r12[0]}, {r12[1]}]（环绕会变成负数/正数）· 削波计数={n12}");

        // ④ 负增益是衰减
        var gm = new MicGain(-20);
        var dm = Make(10000);
        gm.Apply(dm, 0, dm.Length);
        var rm = Read(dm);
        Check(Math.Abs(rm[0] - 1000) <= 2, "−20 dB 衰减到 ×0.1（10000 → 1000）",
              $"因子={gm.Factor:F3} · 输出=[{rm[0]}]");

        // ⑤ 电平：静音为空、满幅接近顶格
        var ge = new MicGain(0);
        var silent = Make(0, 0, 0, 0);
        ge.Apply(silent, 0, silent.Length);
        var lvSilent = ge.LevelPermille;
        var full = Make(new short[480].Select(_ => (short)32000).ToArray());
        ge.Apply(full, 0, full.Length);
        Check(lvSilent == 0 && ge.LevelPermille > 900,
              "电平表：静音 = 0，满幅 > 900（量程 0..1000）",
              $"静音 level={lvSilent} · 满幅 level={ge.LevelPermille} · 峰值={ge.PeakDbfs:F1} dBFS");

        // ⑥ 参数夹取（手改 audio.json 写了 1000 也不会炸）
        var gr = new MicGain(1000);
        var gr2 = new MicGain(-1000);
        Check(gr.Db == MaxDb && gr2.Db == MinDb, $"越界被夹到 [{MinDb}, {MaxDb}]",
              $"+1000 → {gr.Db} dB · −1000 → {gr2.Db} dB");

        // ⑦ 取削波计数会清零（界面闪烁提示靠它）
        var gc = new MicGain(20);
        var dc = Make(30000, 30000);
        gc.Apply(dc, 0, dc.Length);
        var first = gc.TakeClipRecent();
        var second = gc.TakeClipRecent();
        Check(first == 2 && second == 0, "削波计数取一次后清零（界面每次轮询只报新增）",
              $"第一次={first} · 第二次={second}");

        // ⑧ 普通长度/越界参数不崩
        var gs = new MicGain(6);
        var db0 = new byte[0];
        var r1 = gs.Apply(db0, 0, 0);
        var r2 = gs.Apply(new byte[10], 0, 99);
        Check(r1 == 0 && r2 == 0, "空块 / 越界长度直接安全返回 0",
              $"空块={r1} · 越界={r2}");

        sb.AppendLine();
        sb.AppendLine(ok ? "⇒ 全部通过 ✓" : "⇒ ★ 有断言失败");
        return sb.ToString();
    }
}
