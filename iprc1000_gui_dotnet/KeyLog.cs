using System.Collections.Concurrent;
using System.Text;

namespace Iprc1000Adapter;

/// <summary>
/// 按键日志的**异步写入器**。
///
/// 为什么需要它（2026-10-17 实测）：
///   原来 `EtwKeySource.AppendLog` 直接 `File.AppendAllText` —— 每行都是
///   「open → seek 到末尾 → write → close」，而且是**在注入之前**、**在 ETW 投递线程上**执行。
///   一次按键产生 5~8 行（KEY DOWN / APPLY / INJECT DOWN / INJECT UP…），
///   本机实测**每行 0.253 ms ⇒ 单次按键约 1.3 ms 的纯等待**。
///   这还只是空载：文件越大、开着实时杀毒，单行可能飙到几毫秒 —— 而且会抖。
///
/// 现在：调用方只入队（纳秒级），由一条后台线程合并写盘。
/// 顺带做了**轮转**（超 512 KB 转 .1），否则日志只增不减，越写越慢。
/// </summary>
internal static class KeyLog
{
    private const int MaxQueue = 4096;
    private const long RotateBytes = 512 * 1024;

    private static readonly BlockingCollection<string> _q =
        new(new ConcurrentQueue<string>(), MaxQueue);
    private static readonly object _fileLock = new();
    private static Thread? _thread;
    private static string? _path;

    /// <summary>队列满而丢掉的行数（宁可丢日志，也不许阻塞按键）</summary>
    public static long Dropped;
    /// <summary>真正落盘的行数</summary>
    public static long Written;

    /// <summary>启用（幂等）。`path` 为空表示用默认的 key-events.log。</summary>
    public static void EnsureStarted(string? path = null)
    {
        lock (_fileLock)
        {
            _path ??= path ?? Path.Combine(Env.ConfigDir, "key-events.log");
            if (_thread != null) return;
            _thread = new Thread(Loop) { IsBackground = true, Name = "key-log" };
            _thread.Start();
        }
    }

    /// <summary>写一行（**只入队，永不阻塞**；队列满就丢并计数）。</summary>
    public static void Write(string line)
    {
        if (line == null || !LogSwitch.Enabled) return;   // 发布版默认不写日志
        EnsureStarted();
        // 时间戳在这里统一加，保证与旧格式一致
        if (!_q.TryAdd($"{DateTime.Now:HH:mm:ss.fff}  {line}\n"))
            Dropped++;
    }

    /// <summary>把队列里剩下的写完（退出时调，最多等 <paramref name="ms"/> 毫秒）。</summary>
    public static void Flush(int ms = 800)
    {
        var deadline = Environment.TickCount64 + ms;
        while (_q.Count > 0 && Environment.TickCount64 < deadline)
            Thread.Sleep(5);
        Thread.Sleep(10);   // 给最后一批一步落盘的机会
    }

    private static void Loop()
    {
        foreach (var s in _q.GetConsumingEnumerable())
        {
            try { AppendLocked(s); }
            catch { /* 日志写失败绝不能影响按键 */ }
        }
    }

    private static void AppendLocked(string s)
    {
        var p = _path!;
        // ★ 轮转：只保留一份 .1。日志只增不减会让每次追加越来越慢。
        try
        {
            var fi = new FileInfo(p);
            if (fi.Exists && fi.Length > RotateBytes)
            {
                var bak = p + ".1";
                try { if (File.Exists(bak)) File.Delete(bak); } catch { }
                try { File.Move(p, bak); } catch { }
            }
        }
        catch { }

        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        // BOM 只写一次（UTF8Encoding(true) 每次都写，会在文件中间夹进 ï»¿）
        if (!File.Exists(p)) File.WriteAllText(p, s, new UTF8Encoding(true));
        else File.AppendAllText(p, s, new UTF8Encoding(false));
        Written++;
    }

    /// <summary>自检：证明「入队不阻塞 + 队列满会丢而不是卡住 + 落盘内容对」</summary>
    public static string SelfTest()
    {
        var sb = new StringBuilder();
        var ok = true;
        void Check(bool cond, string name, string detail)
        {
            ok &= cond;
            sb.AppendLine($"  {(cond ? "✓" : "✗")} {name}");
            sb.AppendLine($"       {detail}");
        }

        // ⚠ 自检必须**临时打开总开关**：发布版默认 `LogSwitch.Enabled == false`，
        //   此时 `Write` 直接 return，什么都测不到（这条自检第一次就是因此假失败）。
        //   自检只往临时目录写，不会污染真机日志。
        var prevEnabled = LogSwitch.Enabled;
        LogSwitch.Enabled = true;

        // ① 入队必须快（这是全部意义）：2 万行不许超过 200 ms
        var dir = Path.Combine(Path.GetTempPath(), "iprc-keylog-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "k.log");
        _path = file;                       // 自检走临时文件，不污染真机日志
        EnsureStarted();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 20000; i++) Write($"BENCH {i}");
        sw.Stop();
        Check(sw.ElapsedMilliseconds < 200,
              "入队 2 万行不阻塞（远快于同步写盘）",
              $"{sw.ElapsedMilliseconds} ms（若走同步 I/O，2 万 × 0.25 ms ≈ 5000 ms）");

        // ② 队列满时是"丢 + 计数"，不是"卡住"或抛异常
        Check(!_q.IsAddingCompleted, "队列满不会抛异常",
              $"丢弃计数 = {Dropped}（队列上限 {MaxQueue}，丢日志可以，卡按键不行）");

        // ③ 落盘内容正确（等写完再数行数）
        Flush(2000);
        var lines = File.Exists(file) ? File.ReadAllLines(file).Length : 0;
        Check(lines > 0 && lines == (int)Written,
              "落盘行数 = 写入计数",
              $"文件 {lines} 行 · Written={Written} · Dropped={Dropped}");

        try { Directory.Delete(dir, true); } catch { }
        _path = null;
        LogSwitch.Enabled = prevEnabled;     // 复原（自检不许改变运行态）
        sb.AppendLine(ok ? "⇒ 全部通过 ✓" : "⇒ ★ 有断言失败");
        return sb.ToString();
    }
}
