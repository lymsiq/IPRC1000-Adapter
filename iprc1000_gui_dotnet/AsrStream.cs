using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Iprc1000Adapter;

/// <summary>
/// **流式识别**（2026-10-29）：走引擎的 `/v1/realtime` WebSocket。
///
/// 和"整段识别"的区别：
///   · 整段：松开后一次性 POST，1.2 秒后整句出现。
///   · 流式：边说边推 PCM，引擎持续回 `partial`（**全量假设，不是增量**），
///     松手发 `{"event":"final"}`，等 `type:"final"` 拿到定稿。
///
/// 两个协议要点（读 docs/protocol.md 得来，猜错就通不了）：
///   1. WebSocket 用**独立端口**，必须从 `/health` 的 `websocket_port` 读，不是 HTTP 端口。
///   2. `text` 永远是**完整当前假设** ⇒ 显示时直接替换整行，不要拼接。
///
/// 线程模型：Push 由音频线程调用（只做入队），发送由独立发送循环做
/// —— 绝不能在音频回调里 await 网络。
/// </summary>
internal sealed class AsrStream : IDisposable
{
    private readonly ClientWebSocket _ws = new();
    private readonly CancellationTokenSource _cts = new();
    /// <summary>发送队列：每项都带"是不是文本帧"。
    /// ★ 协议要求控制消息（{"event":"final"}）必须是 **Text 帧**，音频必须是 Binary；
    ///   之前全部按 Binary 发，引擎把 final 当音频吃掉了 —— 永远不回 type:"final"，
    ///   于是收尾干等、界面卡在「识别中…」（实测踩到）。</summary>
    private readonly BlockingCollection<(byte[] data, bool text)> _sendQueue = new(2000);
    private readonly TaskCompletionSource<string> _final = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _closed, _dropped;

    public string SessionId { get; } = Guid.NewGuid().ToString("N").Substring(0, 16);
    public string LastPartial { get; private set; } = "";
    public long Partials { get; private set; }
    public bool FinalSent { get; private set; }
    public bool FinalRecv { get; private set; }
    public string State => _ws.State.ToString();
    public int Dropped => _dropped;

    public event Action<string>? OnPartial;

    public static async Task<AsrStream?> OpenAsync(int wsPort, int timeoutMs = 8000)
    {
        var s = new AsrStream();
        try
        {
            s._ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            s._ws.Options.SetRequestHeader("X-Session-Id", s.SessionId);
            using var cts = new CancellationTokenSource(timeoutMs);
            await s._ws.ConnectAsync(new Uri($"ws://127.0.0.1:{wsPort}/v1/realtime"), cts.Token);
            _ = Task.Run(s.SendLoop);
            _ = Task.Run(s.RecvLoop);
            return s;
        }
        catch (Exception e)
        {
            Safe.Note("ASR 流式连接失败：" + e.Message);
            s.Dispose();
            return null;
        }
    }

    /// <summary>音频线程调用：只入队，不做 IO。</summary>
    public void Push(byte[] pcm, int len) => Push(pcm, 0, len);

    public void Push(byte[] pcm, int off, int len)
    {
        if (_closed != 0 || len <= 0) return;
        var copy = new byte[len];
        Buffer.BlockCopy(pcm, off, copy, 0, len);
        if (!_sendQueue.TryAdd((copy, false))) Interlocked.Increment(ref _dropped);   // 队列满就丢，不阻塞音频
    }

    /// <summary>松手：发 final，等定稿（超时返回已有 partial）。</summary>
    public async Task<string> FinishAsync(int timeoutMs = 5000)
    {
        try
        {
            var msg = Encoding.UTF8.GetBytes("{\"event\":\"final\"}");
            // 必须排在所有音频之后，且**作为文本帧**发出
            _sendQueue.Add((msg, true));
            FinalSent = true;
            var done = await Task.WhenAny(_final.Task, Task.Delay(timeoutMs));
            if (done == _final.Task) return await _final.Task;
            Safe.Note($"ASR 流式收尾超时（{timeoutMs}ms），用已有 partial");
            return LastPartial;
        }
        catch (Exception e)
        {
            Safe.Note("ASR 流式收尾异常：" + e.Message);
            return LastPartial;
        }
        finally { Dispose(); }
    }

    private async Task SendLoop()
    {
        try
        {
            foreach (var (buf, isText) in _sendQueue.GetConsumingEnumerable(_cts.Token))
            {
                if (_ws.State != WebSocketState.Open) break;
                await _ws.SendAsync(new ArraySegment<byte>(buf),
                    isText ? WebSocketMessageType.Text : WebSocketMessageType.Binary, true, _cts.Token);
            }
        }
        catch { }
    }

    private async Task RecvLoop()
    {
        var buf = new byte[8192];
        var acc = new MemoryStream();
        try
        {
            while (_ws.State == WebSocketState.Open)
            {
                var r = await _ws.ReceiveAsync(new ArraySegment<byte>(buf), _cts.Token);
                if (r.MessageType == WebSocketMessageType.Close) break;
                acc.Write(buf, 0, r.Count);
                if (!r.EndOfMessage) continue;

                var json = Encoding.UTF8.GetString(acc.ToArray());
                acc.SetLength(0);
                Handle(json);
            }
        }
        catch { }
        finally
        {
            try { _final.TrySetResult(LastPartial); } catch { }
        }
    }

    private void Handle(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var ev = root.TryGetProperty("event", out var e) ? e.GetString() : null;
            if (ev == "error")
            {
                Safe.Note("ASR 流式错误：" + json.Substring(0, Math.Min(160, json.Length)));
                return;
            }
            if (ev == "ready") return;

            var text = root.TryGetProperty("text", out var t) ? (t.GetString() ?? "") : "";
            var type = root.TryGetProperty("type", out var ty) ? ty.GetString() : "";
            if (type == "final")
            {
                FinalRecv = true;
                LastPartial = text;
                _final.TrySetResult(text);
                return;
            }
            // partial：text 是全量假设 ⇒ 直接替换
            if (text.Length == 0) return;
            Partials++;
            LastPartial = text;
            try { OnPartial?.Invoke(text); } catch { }
        }
        catch { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        try { _sendQueue.CompleteAdding(); } catch { }
        try { _cts.Cancel(); } catch { }
        try { if (_ws.State == WebSocketState.Open) _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).Wait(500); } catch { }
        try { _ws.Dispose(); } catch { }
        try { _cts.Dispose(); } catch { }
    }
}
