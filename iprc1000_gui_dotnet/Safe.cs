using System.Diagnostics;
using System.Text;

namespace Iprc1000Adapter;

/// <summary>
/// **稳定性基础设施**：错误留痕 + 日志治理（2026-10-19）。
///
/// ## 为什么要有它
///
/// 全项目曾有 **97 处 `catch { }`** —— 故障被完全吞掉。真机排查时多次出现
/// "日志里什么都没有，只能靠猜"（分片被静默丢弃、配置解析失败回落默认值、注入失败退化）。
/// 这里提供一个**统一的"最小留痕"出口**：任何被 catch 的异常，至少写一行到 `errors.log`。
///
/// ## 日志治理策略（用户 2026-10-19 明确要求）
///
/// - **默认只保留当天的日志**：启动时 + 跨天时自动清理非今天的 `*.log`；
/// - **单文件上限 20 MB**：超过就截断（防一次异常风暴把盘写满）；
/// - `errors.log` **不受"诊断日志"开关影响**（它是故障记录，不是调试输出），
///   但同样受"只留当天 + 20MB"约束；
/// - 同类消息 **60 秒内去重**（只记一条 + 累计次数），避免刷屏。
/// </summary>
internal static class Safe
{
    private static readonly object Gate = new();
    private static string _lastKey = "";
    private static DateTime _lastAt = DateTime.MinValue;
    private static int _repeat;

    // 2026-10-29：从"20MB 截断保尾 1MB"改为"10MB 换新文件"。
    //   截断会把**旧现场**直接丢掉（跨天的问题无法回溯），而换新能把历史分段留下；
    //   并且**基础文件永远是当前文件** —— 报障只需要发这一个。
    private const long MaxBytes = 10L * 1024 * 1024;   // 10 MB
    private const int KeepFiles = 3;                   // 保留 .1/.2/.3 三份历史
    private static DateTime _purgedFor = DateTime.MinValue;

    private static string Dir => Env.ConfigDir;
    private static string ErrorFile => Path.Combine(Dir, "errors.log");

    /// <summary>记录一个被吞掉的异常。不会抛异常（自身失败也静默）。</summary>
    public static void Log(Exception? e, string what)
    {
        try
        {
            var msg = e == null ? "" : e.GetType().Name + ": " + e.Message;
            var key = what + "|" + msg;
            lock (Gate)
            {
                var now = DateTime.UtcNow;
                // 同类消息 60 秒内只写一条，其余累计计数（避免异常风暴刷屏）
                if (key == _lastKey && (now - _lastAt).TotalSeconds < 60)
                {
                    _repeat++;
                    return;
                }
                var body = new StringBuilder();
                body.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append("  ").Append(what);
                if (_repeat > 0) body.Append($"（上一条同类消息又发生 {_repeat} 次）");
                body.AppendLine();
                if (!string.IsNullOrEmpty(msg)) body.Append("    ").AppendLine(msg);
                if (e?.StackTrace is { Length: > 0 } st)
                {
                    var first = st.Split('\n');
                    if (first.Length > 0) body.Append("    ").AppendLine(first[0].Trim());
                }

                Directory.CreateDirectory(Dir);
                File.AppendAllText(ErrorFile, body.ToString(), new UTF8Encoding(false));
                _lastKey = key; _lastAt = now; _repeat = 0;
            }
            Cap(ErrorFile);
        }
        catch { /* 留痕本身失败就算了，绝不再抛 */ }
    }

    public static void Log(string what) => Log(null, what);

    /// <summary>
    /// **始终记录**一条关键事件（不做 60 秒去重）。
    ///
    /// 为什么需要：诊断日志默认是**关**的，而"语音键被谁放掉的"这类问题每次都因为
    /// "用户没开日志"而只能靠猜（2026-10-19 连续两次）。这类**归因必需**的事件
    /// 量小（每次按键 2 行），直接落到 errors.log，不依赖那个开关。
    /// </summary>
    /// <summary>事件日志总闸（由 Settings.KeepEventLog 控制）。关掉后 Note 变成空操作。</summary>
    public static volatile bool NotesEnabled = true;

    public static void Note(string what)
    {
        if (!NotesEnabled) return;
        // ★★★ 2026-10-20（第四轮审查·资源/延迟）：
        //   `Note` 跑在**按键线程**（ETW 回调/注入路径）上，原来是**同步写盘** ——
        //   慢盘/杀软扫描时一次 AppendAllText 可能几十毫秒，直接把按键延迟顶上去。
        //   改成**入队 + 后台落盘**（与桥的 log_async 同一思路）：热路径只做一次入队（~百纳秒）。
        try
        {
            EnsureWriter();
            _q.Enqueue($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {what}");
            if (_q.Count > 4000) _q.TryDequeue(out _);      // 极端的洪水保护
        }
        catch { }
    }

    private static readonly System.Collections.Concurrent.ConcurrentQueue<string> _q = new();
    private static int _writerStarted;

    /// <summary>后台落盘线程：每 200ms 或攒够 64 条就刷一次。首次 Note 时惰性启动。</summary>
    private static void EnsureWriter()
    {
        if (Interlocked.Exchange(ref _writerStarted, 1) != 0) return;
        Task.Factory.StartNew(() =>
        {
            var sb = new System.Text.StringBuilder();
            while (true)
            {
                try
                {
                    Thread.Sleep(200);
                    sb.Clear();
                    var n = 0;
                    while (n < 512 && _q.TryDequeue(out var line)) { sb.Append(line).Append('\n'); n++; }
                    if (n == 0) continue;
                    lock (Gate)
                    {
                        Directory.CreateDirectory(Dir);
                        File.AppendAllText(ErrorFile, sb.ToString(), new UTF8Encoding(false));
                    }
                    Cap(ErrorFile);
                }
                catch { }
            }
        }, TaskCreationOptions.LongRunning);
    }

    /// <summary>把队列里剩下的立刻写完（崩溃/退出前调用）。</summary>
    public static void FlushNotes()
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            while (_q.TryDequeue(out var line)) sb.Append(line).Append('\n');
            if (sb.Length == 0) return;
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                File.AppendAllText(ErrorFile, sb.ToString(), new UTF8Encoding(false));
            }
            Cap(ErrorFile);
        }
        catch { }
    }

    /// <summary>
    /// 清理日志：**保留今天的，删除更早的**；并对超过上限的文件做截断。
    /// 启动时调一次，之后由后台线程在**跨天**时调一次（很便宜：一次目录枚举）。
    /// </summary>
    public static void Housekeeping(bool force = false)
    {
        try
        {
            var today = DateTime.Today;
            lock (Gate)
            {
                if (!force && _purgedFor == today) return;
                _purgedFor = today;
            }
            if (!Directory.Exists(Dir)) return;

            foreach (var f in Directory.EnumerateFiles(Dir, "*.log"))
            {
                try
                {
                    var fi = new FileInfo(f);
                    // 只保留当天；更早的直接删（用户要求"默认只保留当天日志"）
                    if (fi.LastWriteTime.Date < today) { fi.Delete(); continue; }
                    Cap(f);
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>
    /// 超过 10MB 就**换新文件**：把当前文件改名为 `name.1.log`，历史依次后移，最多留 3 份。
    ///
    /// 与旧的"截断保尾 1MB"相比：
    ///   · 旧现场不再丢失（截断是不可恢复的）；
    ///   · **基础文件名永远是当前文件** —— 用户报障只需发 `errors.log` 这一个；
    ///   · 目录总量有上界（4 × 10MB = 40MB），不会无限增长。
    ///
    /// 为什么安全：写入走 `File.AppendAllText`（每次开-写-关，不长期持有句柄），
    /// 所以这里改名不会撞上自己的写锁；失败的每一步都单独 try，最坏情况是"这次不换"。
    /// </summary>
    private static void Cap(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length <= MaxBytes) return;

            var dir = fi.DirectoryName ?? Dir;
            var name = Path.GetFileNameWithoutExtension(path);   // errors
            var ext = Path.GetExtension(path);                   // .log
            string Part(int i) => Path.Combine(dir, name + "." + i + ext);

            // ① 删最老的一份
            try { if (File.Exists(Part(KeepFiles))) File.Delete(Part(KeepFiles)); } catch { }
            // ② 依次后移：.2→.3、.1→.2
            for (var i = KeepFiles - 1; i >= 1; i--)
            {
                try
                {
                    var from = Part(i);
                    if (File.Exists(from)) File.Move(from, Part(i + 1), true);
                }
                catch { }
            }
            // ③ 当前 → .1
            try
            {
                if (File.Exists(Part(1))) File.Delete(Part(1));
                File.Move(path, Part(1), true);
            }
            catch { return; }   // 改不动就放弃（下次再试），绝不能把当前日志搞丢

            Log($"日志超过 {MaxBytes / 1024 / 1024}MB，已换新文件（保留 {KeepFiles} 份历史）：{Path.GetFileName(path)}");
        }
        catch { }
    }

    /// <summary>供 UI 显示：当前日志目录占用（字节）。</summary>
    public static long LogBytes()
    {
        try
        {
            if (!Directory.Exists(Dir)) return 0;
            long sum = 0;
            foreach (var f in Directory.EnumerateFiles(Dir, "*.log"))
                try { sum += new FileInfo(f).Length; } catch { }
            return sum;
        }
        catch { return 0; }
    }
}
