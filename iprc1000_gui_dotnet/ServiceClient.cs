using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Iprc1000Adapter;

/// <summary>
/// 业务服务的**客户端**（宿主用它把 47 条命令转发给业务进程）。
///
/// 协议（一行一条，UTF-8）：
///   请求  {"method":"getState","args":["",""]}
///   响应  Backend.Dispatch 的返回值（本身就是一个 JSON 对象）
///
/// 为什么不复用 HTTP：本机回环、单客户端、无跨机需求 —— 命名管道足够，
/// 而且**不需要开端口**（UDP 那两个端口是给语音桥用的，不该再引入新的监听面）。
/// </summary>
internal static class ServiceClient
{
    public const string PipeName = ServiceHost.PipeName;
    private static readonly JsonSerializerOptions Opt = new() { WriteIndented = false };

    /// <summary>同步调用；服务不在时返回一个可读的错误 JSON（宿主据此提示"业务未运行"）。</summary>
    public static string Call(string method, string a0 = "", string a1 = "", int timeoutMs = 3000)
    {
        NamedPipeClientStream? pipe = null;
        try
        {
            pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.None);
            pipe.Connect(timeoutMs);

            var request = new JsonObject { ["method"] = method, ["args"] = new JsonArray(a0, a1) }
                .ToJsonString(Opt);

            // ★★ 2026-10-29（实测踩到）：不能在超时后"用 using 去释放 reader" ——
            //   reader 里还有一个**阻塞中的 ReadLine**，Dispose 会卡在那次读上，整个调用就挂死了
            //   （表现：宿主界面完全冻住，日志里只剩一句 "Pipe is broken"）。
            //   正确做法：整个往返放进任务，超时后**从外部关闭管道**让阻塞的读抛异常。
            // ★ 2026-10-29：与服务端同一套**长度前缀帧**（[int32][utf8]），
            //   超时后从外部关闭管道解除阻塞（不能靠 using 释放 reader）。
            var sender = pipe;
            var work = Task.Run(() =>
            {
                var bytes = Encoding.UTF8.GetBytes(request);
                var br = new BinaryReader(sender, new UTF8Encoding(false));
                var bw = new BinaryWriter(sender, new UTF8Encoding(false));
                bw.Write(bytes.Length); bw.Write(bytes); bw.Flush();
                var len = br.ReadInt32();
                if (len <= 0 || len > 64 * 1024 * 1024) throw new IOException("帧长度异常");
                return Encoding.UTF8.GetString(br.ReadBytes(len));
            });

            if (!work.Wait(timeoutMs))
            {
                try { pipe.Dispose(); } catch { }
                return JsonSerializer.Serialize(new { ok = false, message = $"业务服务响应超时（{method}）" });
            }
            var line = work.Result;
            return string.IsNullOrWhiteSpace(line)
                ? "{\"ok\":false,\"message\":\"业务服务无响应\"}"
                : line;
        }
        catch (TimeoutException)
        {
            return "{\"ok\":false,\"message\":\"业务服务未运行（管道连接超时）\"}";
        }
        catch (Exception e)
        {
            return JsonSerializer.Serialize(new { ok = false, message = "管道调用失败：" + e.Message });
        }
        finally
        {
            try { pipe?.Dispose(); } catch { }
        }
    }

    /// <summary>服务是否在跑（0.3 s 探测，不发送命令）。</summary>
    public static bool IsRunning()
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.None);
            pipe.Connect(300);
            return true;
        }
        catch { return false; }
    }
}
