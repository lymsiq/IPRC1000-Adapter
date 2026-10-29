using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;

namespace Iprc1000Adapter;

/// <summary>
/// **无界面服务模式**（2026-10-29 拆进程的第一步）。
///
/// 目的：让「业务」与「UI 宿主」可以分开跑。
///   · 业务 = Backend + KeyFilter + 注入 + 音频路由 + 探针（**零 WinForms 依赖**，实测可独立）
///   · UI   = 窗口 / 托盘 / WebView2（崩溃不该影响按键与语音）
///
/// 当前阶段：**业务先独立成服务**（本类），UI 仍在同一个 exe 里跑；
/// 下一步把 UI 的 47 个命令转发到这个管道上，那时宿主就只剩 1.3k 行、随时可换语言重写。
///
/// 协议：每行一个 JSON 请求 `{"method":"getState","args":["",""]}`，
///        每行一个 JSON 响应（即 Backend.Dispatch 的返回值），一行一条、顺序处理。
/// 管道名：`IPRC1000Adapter.Service`（本机会话内可见）。
///
/// 与 UI 模式的一个已知差异：**没有窗口句柄** ⇒ 收不到 WM_DEVICECHANGE
/// （"拔掉遥控器/蓝牙断开"这类即时事件）。卡键兜底仍由看门狗负责
/// （StuckGuard 语音 600s / 其它 15s + CheckHeldHealth 300ms）。
/// </summary>
internal static class ServiceHost
{
    public const string PipeName = "IPRC1000Adapter.Service";

    public static void Run()
    {
        using var mutex = new Mutex(true, @"Global\IPRC1000Adapter.Service", out var created);
        if (!created)
        {
            Console.WriteLine("[service] 已有实例在运行，退出。");
            return;
        }

        var backend = new Backend();
        var filter = new KeyFilter();
        filter.OnButton += (bid, pressed) => backend.NoteButton(bid, pressed);
        backend.AttachFilter(filter);
        filter.Start(IntPtr.Zero);              // 起看门狗（无窗口）
        _ = Task.Run(() =>
        {
            // InitAsync 是 void + 内部 try/catch（异常会被吞），这里补一层留痕，
            // 否则"无界面模式下按键没反应"只能靠猜。
            try { Safe.Note("INIT 开始"); backend.InitAsync(); Safe.Note("INIT 结束"); }
            catch (Exception e) { Safe.Note($"INIT 失败 {e.GetType().Name}: {e.Message}"); }
        });

        Console.WriteLine($"[service] 业务服务已启动，管道名 {PipeName}");
        Console.WriteLine($"[service] 接收端口：音频 UDP {Env.PcmPort} / 按键 UDP {Env.KeyPort}（被占用时自动避让）");
        Console.WriteLine("[service] 用 --headless 启动后，UI（若有）通过命名管道调用；Ctrl+C 退出。");

        while (true)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 4,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                server.WaitForConnection();
                // ★ 2026-10-29：协议用**二进制长度前缀**，不用"一行 JSON"。
                //   文本协议在两个独立实现之间容易出歧义（BOM、\r\n、编码探测），实测就卡在 ReadLine 上。
                //   帧格式：[int32 长度][utf8 JSON]，请求/响应同构。
                using var br = new BinaryReader(server, new UTF8Encoding(false));
                using var bw = new BinaryWriter(server, new UTF8Encoding(false));
                while (true)
                {
                    string line;
                    try { line = ReadFrame(br); }
                    catch (EndOfStreamException) { break; }
                    if (string.IsNullOrEmpty(line)) break;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var resp = HandleOne(line, backend);
                    Safe.Note($"SERVICE {line[..Math.Min(60, line.Length)]} → {resp.Length}B / {sw.ElapsedMilliseconds}ms");
                    WriteFrame(bw, resp);
                }
            }
            catch (Exception e)
            {
                // 单个客户端出问题不影响服务：记一笔，继续等下一个
                Safe.Note($"SERVICE 客户端异常 {e.GetType().Name}: {e.Message}");
            }
            finally
            {
                try { server?.Dispose(); } catch { }
            }
        }
    }

    /// <summary>读一帧：int32 长度 + utf8 字节。</summary>
    private static string ReadFrame(BinaryReader br)
    {
        var len = br.ReadInt32();
        if (len <= 0 || len > 64 * 1024 * 1024) throw new EndOfStreamException();
        return Encoding.UTF8.GetString(br.ReadBytes(len));
    }

    private static void WriteFrame(BinaryWriter bw, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        bw.Write(bytes.Length);
        bw.Write(bytes);
        bw.Flush();
    }

    private static string HandleOne(string line, Backend backend)
    {
        try
        {
            var node = JsonNode.Parse(line)?.AsObject();
            if (node == null) return "{\"ok\":false,\"message\":\"不是合法 JSON\"}";
            var method = node["method"]?.GetValue<string>() ?? "";
            var args = node["args"] as JsonArray;
            string A(int i) => args != null && args.Count > i ? (args[i]?.ToString() ?? "") : "";
            return backend.Dispatch(method, A(0), A(1));
        }
        catch (Exception e)
        {
            return $"{{\"ok\":false,\"message\":\"{System.Text.Json.JsonSerializer.Serialize(e.Message)}\"}}";
        }
    }
}
