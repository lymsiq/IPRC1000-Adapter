using System.Net;
using System.Net.Sockets;
using System.Text;
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
    // ★★ 2026-10-29：**不要**在字段初始化里绑端口 —— 那样只要 new 了 CableRouter 就占住 UDP 31680，
    //   宿主进程（--ui-client）虽然不 Start，也会被"端口已占用"直接崩掉（实测 10048）。
    //   改为 Start() 里绑：不 Start 就不占端口。
    private UdpClient? _udp;
    private WasapiOut? _out;
    /// <summary>有界抖动缓冲（替代原来的 BufferedWaveProvider）</summary>
    private JitterBuffer? _jit;
    /// <summary>输出设备"不在播放"的起始时刻（用于 500ms 宽限，避免抖动导致反复重建）</summary>
    private DateTime _notPlayingSince = DateTime.MinValue;

    // ── 包间隔统计（每段会话一份）──
    // ★ 为什么需要它：某次实测 62 秒丢了 375 帧（12%），但"丢帧分类"全是 0
    //   —— 说明不是分片层面的问题，而是**帧干脆没到**。
    //   光看累计数分不清"均匀地丢了 12%"还是"某一串全丢了又补回来"，
    //   所以直接量**相邻音频包的到达间隔**：均匀抖动 vs 成串到达，一眼可辨。
    private readonly List<int> _gaps = new(4096);
    private long _lastPktMs;

    private Thread? _thread;
    private volatile bool _running;
    private long _packets;
    private long _bytes;
    private volatile bool _inSession;

    // 声卡开关要加锁：预热是从**按键线程**调的，而正常开关在**路由线程**里，
    // 两个线程同时 EnsureSink 会各自 new 出一个 WasapiOut（后一个泄漏）。
    private readonly object _sinkLock = new();
    // 缓存 CABLE Input 设备对象：原来每轮 EnsureSink 都要重新枚举全部渲染端点（数十毫秒），
    // 而这段开销正好落在"按键→出声"的关键路径上。
    private MMDevice? _cable;
    // 语音键按下的时刻，用来量「按键 → 第一包音频到达」到底花了多久
    private DateTime _voiceDownAt = DateTime.MinValue;

    public string? Error { get; private set; }
    public string? Device { get; private set; }
    public bool Online => _running && Error == null;
    public long Packets => Interlocked.Read(ref _packets);
    public long Bytes => Interlocked.Read(ref _bytes);
    public bool Session => _inSession;

    // ── 声卡侧日志 ──
    // 整条链路里"最后一公里"就靠它：桥的统计（bridge.log / bridge-stats.json）能说明
    // "帧有没有来"，这里说明"来了有没有真的写进 CABLE"。缺了它，两端都正常但没声音时无从下嘴。
    private static readonly object _logLock = new();
    /// <summary>音频链路通用诊断日志（写 router.log）。同一份日志里既有声卡开关也有默认设备变化，便于对照。</summary>
    internal static void Log(string msg)
    {
        if (!LogSwitch.Enabled) return;
        try
        {
            lock (_logLock)
            {
                var p = Path.Combine(Env.ConfigDir, "router.log");
                var body = $"{DateTime.Now:HH:mm:ss.fff}  {msg}\n";
                // 首次创建写 BOM：Windows 记事本 / Get-Content 否则会按 ANSI 解码而显示乱码
                if (!File.Exists(p)) File.WriteAllText(p, body, new UTF8Encoding(true));
                else File.AppendAllText(p, body, new UTF8Encoding(false));
            }
        }
        catch { }
    }

    public void Start()
    {
        if (_running) return;
        _udp ??= new UdpClient(new IPEndPoint(IPAddress.Loopback, Env.PcmPort));
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "cable-router" };
        _thread.Start();
        PrewarmInBackground();     // 把"打开声卡"的 100~300 ms 挪到启动阶段（不阻塞 UI）
    }

    /// <summary>
    /// 本段会话的包间隔概况 —— 用来区分"均匀抖动"和"成串到达"。
    /// 正常应该是：中位 ≈20 ms、几乎没有 >40 ms。
    /// 若 >40 ms 的次数很多而总量又对得上 → 音频是**成串**来的（ETW/UDP 侧问题）；
    /// 若中位明显 >20 ms → 帧**真的少了**（链路丢帧）。
    /// </summary>
    private string GapSummary()
    {
        if (_gaps.Count < 10) return "";
        var sorted = _gaps.ToArray();
        Array.Sort(sorted);
        var med = sorted[sorted.Length / 2];
        var over40 = sorted.Count(g => g > 40);
        var over100 = sorted.Count(g => g > 100);
        var p95 = sorted[(int)(sorted.Length * 0.95)];
        return $" · 包间隔 中位 {med} ms / p95 {p95} ms / >40ms {over40} 次 / >100ms {over100} 次（{_gaps.Count} 个间隔）";
    }

    private MMDevice? FindCable()
    {
        if (_cable != null) return _cable;          // 命中缓存，省掉整轮设备枚举
        try
        {
            using var en = new MMDeviceEnumerator();
            _cable = en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .FirstOrDefault(d => d.FriendlyName.ToLowerInvariant().Contains("cable input"));
            return _cable;
        }
        catch (Exception e) { Console.WriteLine(e.Message); return null; }
    }

    /// <summary>
    /// 预热：**语音键按下的瞬间**就把 CABLE Input 打开。
    ///
    /// 为什么必须做：打开声卡要「枚举设备 → new WasapiOut → Init → Play」，约 100~300 ms。
    /// 而原来的流程是"第一个音频包到了才去开声卡"，于是这段时间到达的音频被
    /// `if (EnsureSink()) _buf!.AddSamples(...)` 直接丢掉 ——
    /// 用户看到的现象就是**按住语音键立刻说话、第一个字识别不出来**。
    /// 现在把开声卡提前到按键时刻（音频还要 100~200 ms 才到），关键路径上就没有这段开销了。
    /// </summary>
    /// <summary>
    /// **丢弃当前声卡句柄**（2026-10-29）。下一轮循环的 EnsureSink 会重新枚举并重建。
    ///
    /// 用途：休眠唤醒 / 切换用户 / 远程桌面回来后，原来的 WasapiOut 句柄可能已失效
    /// （表现为"有包但没声音"）。这类情况**只能关掉重建**，不能靠重试恢复。
    /// </summary>
    public void DropSink()
    {
        lock (_sinkLock)
        {
            try { _out?.Dispose(); } catch { }
            _out = null;
            _cable = null;
            Error = null;
        }
    }

    public void WarmUp()
    {
        // ★ 幂等：同一次按下可能从**两条来源**各来一次（低级钩子 + ETW/RawInput），
        //   以前第二次会把 `_voiceDownAt` 重置 ⇒ 日志里"首包延迟"少算了整段开声卡时间
        //   （实测：真实 133 ms，日志只写 8 ms）。400 ms 内重复调用直接忽略。
        lock (_sinkLock)
        {
            if (_voiceDownAt != DateTime.MinValue &&
                (DateTime.UtcNow - _voiceDownAt).TotalMilliseconds < 400)
                return;
            _voiceDownAt = DateTime.UtcNow;

            // ★★ 2026-10-19（真机"麦克风图标出现但音量指示不动"）：
            //   图标 = 输入法收到了"右 Alt 按下"这**个键盘事件**（延迟 ~10ms）；
            //   电平 = 输入法从**默认录音设备**收到 PCM（首包实测双峰：10~30ms 或 468~538ms）。
            //   若按住时间短于首包延迟（常见 500ms 左右），就会"图标闪一下、电平一格都没动"。
            //   这里在按下 1200ms 后仍无音频时留一条明确诊断（首包到达会把 _voiceDownAt 清掉）。
            var stamp = _voiceDownAt;
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                await System.Threading.Tasks.Task.Delay(1200);
                if (_voiceDownAt == stamp)
                    Log("[LAT] ⚠ 按下 1200 ms 仍无音频 —— 遥控器还没开始出流" +
                        "（短按 < 首包延迟 / 信号弱 / 遥控器刚被唤醒）");
            });
        }
        if (!_running) return;
        var t0 = DateTime.UtcNow;
        if (EnsureSink())
        {
            var ms = (DateTime.UtcNow - t0).TotalMilliseconds;
            // 这条留着：它单独说明"开声卡花了多久"，是首字延迟的一大块
            if (ms > 30) Log($"[LAT] 语音键预热：打开声卡用了 {ms:F0} ms（含在下面的首包时间里）");
        }
    }

    /// <summary>
    /// **启动时就预热声卡** —— 把 100~300 ms 的"打开声卡"从**第一次说话**挪到启动阶段。
    ///
    /// 实测（2026-10-17）：每次程序启动后第一次按语音键，日志会先打
    /// `[LAT] 语音键预热：打开声卡用了 125 ms` —— 这 125 ms 直接加在首字上。
    /// 声卡本来就设计成常驻（打开后不再关），所以提前开没有任何副作用。
    /// </summary>
    public void PrewarmInBackground()
    {
        if (!_running) return;
        new Thread(() =>
        {
            var t0 = DateTime.UtcNow;
            try
            {
                if (EnsureSink())
                {
                    var ms = (DateTime.UtcNow - t0).TotalMilliseconds;
                    Log($"[LAT] 启动预热：声卡已提前打开（{ms:F0} ms，第一次说话不用再付）");
                }
            }
            catch (Exception e) { Log($"[LAT] 启动预热失败（不影响使用）：{e.Message}"); }
        }) { IsBackground = true, Name = "cable-prewarm" }.Start();
    }

    /// <summary>
    /// **发送 1 秒测试音**（440 Hz 正弦，48k/16bit/单声道）到 CABLE Input。
    ///
    /// 用途（参考无线麦 SayAll 的"发送 1 秒测试音"）：一键区分两种故障 ——
    ///   · 测试音**有电平** ⇒ 音频链路正常，问题在"采不到遥控器音频"；
    ///   · 测试音**也没电平** ⇒ 输入法选错了录音设备（没选 CABLE Output）。
    /// 比"按住说话猜一下"高效得多。
    /// </summary>
    public string PlayTestTone(int ms = 1000)
    {
        if (!_running) return "音频路由未启动（请先启动语音桥）。";
        if (!EnsureSink()) return "打不开 CABLE Input —— 虚拟声卡没装好？";
        const int SR = 48000;
        var n = SR * ms / 1000;
        var buf = new byte[n * 2];
        for (var i = 0; i < n; i++)
        {
            var v = (short)(Math.Sin(2 * Math.PI * 440 * i / SR) * 12000);
            buf[i * 2] = (byte)(v & 0xFF);
            buf[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }
        try
        {
            _jit!.AddSamples(buf, 0, buf.Length);
            Log($"[TEST] 已向 CABLE Input 写入 {ms} ms 测试音（440Hz）");
            return $"已发送 {ms} 毫秒测试音（440Hz）。请在输入法/录音软件里选 CABLE Output，看电平是否跳动。";
        }
        catch (Exception e)
        {
            return "写入测试音失败：" + e.Message;
        }
    }

    private bool EnsureSink()
    {
        lock (_sinkLock)
        {
            // ★★★ 2026-10-19（参考同类项目的"假正常"复盘，对应真机"有图标、电平不动"）：
            //   原来只判断 `_out != null` —— 对象在就当作健康。
            //   但**睡眠唤醒 / 设备重枚举 / WASAPI 异常**之后，对象还在、`PlaybackState` 已经
            //   不是 Playing：这时 `AddSamples` 照样"成功"，而 CABLE Output **一点电平都没有**
            //   ⇒ 输入法显示麦克风图标但音量指示永远不动。
            //   判据改为"**真的在播放**"才算健康；不在播放就重建输出。
            if (_out != null)
            {
                if (_out.PlaybackState == NAudio.Wave.PlaybackState.Playing)
                {
                    _notPlayingSince = DateTime.MinValue;
                    return true;
                }
                // 给 500ms 宽限：避免瞬时抖动导致反复重建（重建会产生一次断音）
                if (_notPlayingSince == DateTime.MinValue) _notPlayingSince = DateTime.UtcNow;
                if ((DateTime.UtcNow - _notPlayingSince).TotalMilliseconds < 500) return true;

                Log($"[AUDIO HEALTH stale] 输出设备状态={_out.PlaybackState}（不是 Playing）—— 重建输出通道");
                CloseSinkLocked();
                _out = null;
                _notPlayingSince = DateTime.MinValue;
            }
            var dev = FindCable();
            if (dev == null)
            {
                Error = "未找到 CABLE Input 播放设备";
                Log("[!] " + Error + " —— 音频来了也写不进去");
                return false;
            }
            try
            {
                // ★ 有界抖动缓冲（替换原来的 BufferedWaveProvider：`BufferDuration = 1 秒` +
                //   `DiscardOnBufferOverflow` 要攒满整整 1 秒才裁 ⇒ 最坏 1 秒延迟）。
                //   参数与理由见 JitterBuffer.cs 的类注释（起播 40 ms / 上限 64 ms /
                //   静默 400 ms 判新会话 / 声卡 IO 缓冲 30 ms，均可由环境变量覆盖）。
                _jit = new JitterBuffer();
                _out = new WasapiOut(dev, AudioClientShareMode.Shared, false, _jit.Options.LatencyMs);
                _out.Init(_jit);
                _out.Play();
                Device = dev.FriendlyName;
                Error = null;
                Log($"[+] 已打开输出设备：{Device} · 抖动缓冲 起播 {_jit.Options.PreRollMs} ms / "
                    + $"上限 {_jit.Options.CapMs} ms / 判新会话 {_jit.Options.StaleMs} ms / "
                    + $"声卡缓冲 {_jit.Options.LatencyMs} ms");
                return true;
            }
            catch (Exception e)
            {
                Error = $"打开 CABLE Input 失败：{e.Message}";
                Log("[!] " + Error);
                _cable = null;                  // 设备对象可能已失效，清掉缓存下次重枚举
                CloseSinkLocked();
                return false;
            }
        }
    }

    private void CloseSink()
    {
        lock (_sinkLock) CloseSinkLocked();
    }

    private void CloseSinkLocked()
    {
        if (_out == null) return;
        try { _out.Stop(); } catch { }
        _out?.Dispose();
        _out = null;
        _jit = null;
        Log($"[-] 已关闭输出设备（本次共写入 {Packets} 包 / {Bytes} 字节）");
    }

    private void Loop()
    {
        var udp = _udp;
        if (udp == null) return;
        udp.Client.ReceiveTimeout = 200;
        var asm = new byte[65536];
        while (_running)
        {
            try
            {
                if (udp.Client.Poll(150_000, SelectMode.SelectRead))
                {
                    EndPoint ep = new IPEndPoint(IPAddress.Any, 0);
                    var n = udp.Client.ReceiveFrom(asm, ref ep);
                    if (n == 4 && asm[0] == (byte)'P' && asm[1] == (byte)'I')
                        udp.Send(new byte[] { (byte)'P', (byte)'O', (byte)'N', (byte)'G' }, 4,
                                  (IPEndPoint)ep);
                    else if (n == 5 && asm[0] == (byte)'C')      // CLEAR
                    {
                        // ★ 仍然**不在这里无条件清缓冲**（以前清过，会话是「松手 END → 再按
                        //   CLEAR」反复建立的，环里往往还压着上一次的真实音频，一清就是一次断音）。
                        //   现在改为**按静默时长判**：JitterBuffer 自己看"距上次写入 > 400 ms"
                        //   才判定新会话并清空 —— 紧接上一次的 CLEAR（数据还新鲜）不会清，
                        //   真隔了一段才按的则清掉旧尾巴。两边都占。
                        _inSession = true;
                        EnsureSink();
                        _gaps.Clear();
                        _lastPktMs = 0;
                        Log($"[CLEAR] 新会话开始（累计 {Packets} 包）");
                    }
                    else if (n == 3 && asm[0] == (byte)'E')      // END
                    {
                        _inSession = false;
                        Log($"[END] 会话结束（累计 {Packets} 包 / {Bytes} 字节）{GapSummary()}");
                    }
                    else if (n >= 2 && n % 2 == 0)
                    {
                        Interlocked.Increment(ref _packets);
                        Interlocked.Add(ref _bytes, n);
                        // ★ 收到音频本身就说明"在会话中"。
                        //   UDP 不可靠，CLEAR 有可能丢包；只认 CLEAR 的话丢一次状态就错了。
                        _inSession = true;
                        var ok = EnsureSink();
                        // 延迟埋点：从「语音键按下」到「第一包音频到达」。
                        // ⚠ 原来写的是 `if (first && …)`，其中 first = !_inSession ——
                        //   但 CLEAR 总是先于音频包到达、已把 _inSession 置 true，
                        //   于是这个条件**永远不成立，这行日志从来没打印过**。
                        //   现在只看「按键后是否已量过」，与 _inSession 无关。
                        if (_voiceDownAt != DateTime.MinValue)
                        {
                            var ms = (DateTime.UtcNow - _voiceDownAt).TotalMilliseconds;
                            if (ms < 5000)   // 超过 5 秒说明是后面某次音频，不是这次按键的
                                Log($"[LAT] 语音键按下 → 首包音频到达 {ms:F0} ms（声卡已就绪={ok}）");
                            _voiceDownAt = DateTime.MinValue;
                        }
                        // 包间隔（同一次会话内相邻音频包的到达间隔）
                        var nowMs = Environment.TickCount64;
                        if (_lastPktMs != 0 && _gaps.Count < 20000)
                        {
                            var g = (int)(nowMs - _lastPktMs);
                            if (g >= 0 && g < 5000) _gaps.Add(g);
                        }
                        _lastPktMs = nowMs;
                        if (ok) _jit!.AddSamples(asm, 0, n);
                    }
                }
            }
            catch (SocketException) { /* 超时 */ }
            catch (Exception e) { if (Error == null) Error = e.Message; }

            // ── 声卡**一直开着**，不再按静默时长自动关 ──
            //
            // 走过的弯路：0.6 秒 → 3 秒 → 30 秒，每一档都会被正常停顿触发，
            // 而每次重开 WASAPI 都要 40~120 ms，这段开销正好吃掉"按住后立刻说的第一个字"。
            // 更要命的是：语音键**预热**刚打开声卡，下一轮循环就会因为
            // "距上次 END 已超时 + 缓冲为空"把它立刻关掉 —— 开→关→再开，纯粹是抖动。
            //
            // 本产品的前提就是「CABLE Output 是一路常驻的麦克风」，
            // 所以让 CABLE Input 也常驻才对：ReadFully=true 会在没数据时补静音，
            // 就像一个正常麦克风在安静环境里输出静音一样，不会播出过期音频。
            // 只在出错或退出时关闭（见 EnsureSink 的 catch 与 Dispose）。
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
        jitter = _jit?.Status(),
    };

    public void Dispose()
    {
        _running = false;
        try { _thread?.Join(500); } catch { }
        CloseSink();
        _udp?.Dispose();
        _udp = null;
    }
}
