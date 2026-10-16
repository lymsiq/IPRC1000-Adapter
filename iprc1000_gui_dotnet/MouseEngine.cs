using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Iprc1000Adapter;

/// <summary>
/// 鼠标模式引擎：把遥控器的 **方向键按住拖动** 变成鼠标移动（带自适应增速），
/// **确认键 → 左键、info → 右键**。
///
/// ── 为什么需要"引擎"而不是"映射" ──
/// 现有的绑定模型是「按键 → 一串虚拟机码」，一次按下、一次抬起，没有时间维度。
/// 而"按住拖动"是**连续动作**：按住 100 ms 和按住 2 秒，走的路程完全不同。
/// 所以这里单开一条通道：ApplyButton 把方向键/确认/info 的路由交给本引擎
/// （见 <see cref="KeyFilter.MouseModeRouter"/>），引擎用自己的线程按 tick 下发位移。
///
/// ── 为什么位移要**按实际时间算** ──
/// 实测 SendInput 单次下单约 286 µs（本机），60 Hz 也就 1.7% CPU；但 `Thread.Sleep`
/// 的抖动是毫秒级的。所以速度定义成 **px/秒**，每帧用 `速度 × 实际间隔` 求位移 ——
/// 这样 tick 抖动只影响"帧间隔是否均匀"，不影响**走得多快**，不会出现"卡一下然后猛冲"。
///
/// ── 三道安全网（鼠标卡住比键盘卡住更妨碍使用）──
/// ① **按住超时**：方向键 8 秒、鼠标键 10 秒，到点无条件放开。
///    遥控器丢 UP 是既知会发生的（看门狗 v2 就是为它而建）。
/// ② **退出/切模式/清空** 一律把所有鼠标键抬起。
/// ③ **孤儿键回收**：定期问系统 `GetAsyncKeyState(VK_LBUTTON/VK_RBUTTON)`——
///    "系统说按着、而我们没持有"且持续够久 → 补一次抬起。**以系统状态为准**，
///    与键盘那边（孤儿键回收器）同一个思路。
/// </summary>
internal sealed class MouseEngine
{
    // ── 配置（落 mouse.json，界面可改）──
    public sealed class Config
    {
        /// <summary>tick 频率。实测 286 µs/次下单，100 Hz≈2.9% CPU；60 Hz 手感已够。</summary>
        public int TickHz { get; set; } = 60;
        /// <summary>起步速度（px/秒）—— 小到能精确点到按钮</summary>
        public double MinPxPerSec { get; set; } = 60;
        /// <summary>满速（px/秒）—— 大到能横跨屏幕（1920 宽约 2.1 秒走完）</summary>
        public double MaxPxPerSec { get; set; } = 900;
        /// <summary>增速斜坡时长（ms）：按住多久到满速</summary>
        public double RampMs { get; set; } = 700;
        /// <summary>增速曲线指数：1=线性，2=慢起快走（默认）</summary>
        public double RampExponent { get; set; } = 2.0;
        /// <summary>斜向归一化（不归一化的话斜着走快 41%）</summary>
        public bool NormalizeDiagonal { get; set; } = true;
        /// <summary>方向键按住超时（秒）—— 失控光标比失控按键更危险，所以不是 60 秒</summary>
        public double DirectionHoldTimeoutSec { get; set; } = 8;
        /// <summary>鼠标键按住超时（秒）—— 卡住的左键会让人点不了任何东西</summary>
        public double ButtonHoldTimeoutSec { get; set; } = 10;
        /// <summary>鼠标键孤儿回收的最低持续秒数（避免误伤用户真实拖拽）</summary>
        public double ButtonOrphanSec { get; set; } = 15;
        /// <summary>记住上次的模式（默认否：重启回键盘模式，否则用户会以为遥控器坏了）</summary>
        public bool RememberMode { get; set; }
        /// <summary>进入鼠标模式时自校准（实测"请求 px → 实际 px"的增益）</summary>
        public bool Calibrate { get; set; } = true;
    }

    private const int VK_LBUTTON = 0x01, VK_RBUTTON = 0x02;
    private const string CfgFile = "mouse.json";

    private Config _cfg = Load();
    private readonly object _gate = new();

    /// <summary>方向键的按住状态（由 ApplyButton 写入，引擎线程读）</summary>
    private readonly HashSet<string> _dirHeld = new();
    /// <summary>方向集合第一次非空的时刻（增速斜坡的起点）</summary>
    private DateTime _dirSince = DateTime.MinValue;

    private bool _leftDown, _rightDown;
    private DateTime _leftAt = DateTime.MinValue, _rightAt = DateTime.MinValue;
    private readonly Dictionary<string, DateTime> _btnAt = new();

    private volatile bool _enabled;
    private volatile bool _stop;
    private Thread? _thread;
    private double _accX, _accY;
    private double _gain = 1.0;          // 实测增益：请求 1 px 实际走 _gain px
    private bool _accelSuspected;        // 增益随增量变化 → 系统加速开着

    // 统计（给界面 + 日志）
    public long Frames { get; private set; }
    public double PixelsMoved { get; private set; }
    public DateTime? LastReap { get; private set; }
    public string? LastNote { get; private set; }

    private DateTime _nextOrphanCheck = DateTime.MinValue;
    private int _organStreak, _orphanStreak;
    private int _dirTimeouts, _btnTimeouts, _orphanReaps;

    // ───────────────────────── 配置读写 ─────────────────────────

    private static string Path_ => Path.Combine(Env.ConfigDir, CfgFile);

    private static Config Load()
    {
        try
        {
            if (File.Exists(Path_))
                return JsonSerializer.Deserialize<Config>(File.ReadAllText(Path_)) ?? new Config();
        }
        catch { }
        return new Config();
    }

    public void SaveConfig()
    {
        try
        {
            File.WriteAllText(Path_, JsonSerializer.Serialize(_cfg,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public Config Cfg { get { lock (_gate) return _cfg; } }

    /// <summary>改配置（界面调）—— 只接受合法范围，避免把速度设成 0 或几十万</summary>
    public string ApplyConfig(JsonElement el)
    {
        var sb = new StringBuilder();
        lock (_gate)
        {
            double D(string k, double cur, double lo, double hi) =>
                el.TryGetProperty(k, out var v) && v.TryGetDouble(out var d) ? Math.Clamp(d, lo, hi) : cur;
            bool B(string k, bool cur) =>
                el.TryGetProperty(k, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
                    ? v.GetBoolean() : cur;

            _cfg.TickHz = (int)D("tickHz", _cfg.TickHz, 20, 200);
            _cfg.MinPxPerSec = D("minPxPerSec", _cfg.MinPxPerSec, 10, 400);
            _cfg.MaxPxPerSec = D("maxPxPerSec", _cfg.MaxPxPerSec, 100, 6000);
            _cfg.RampMs = D("rampMs", _cfg.RampMs, 0, 4000);
            _cfg.RampExponent = D("rampExponent", _cfg.RampExponent, 1, 4);
            _cfg.NormalizeDiagonal = B("normalizeDiagonal", _cfg.NormalizeDiagonal);
            _cfg.DirectionHoldTimeoutSec = D("directionHoldTimeoutSec", _cfg.DirectionHoldTimeoutSec, 2, 60);
            _cfg.ButtonHoldTimeoutSec = D("buttonHoldTimeoutSec", _cfg.ButtonHoldTimeoutSec, 2, 60);
            _cfg.RememberMode = B("rememberMode", _cfg.RememberMode);
            _cfg.Calibrate = B("calibrate", _cfg.Calibrate);
            if (_cfg.MinPxPerSec > _cfg.MaxPxPerSec) _cfg.MinPxPerSec = _cfg.MaxPxPerSec;
            sb.Append($"已应用：{_cfg.MinPxPerSec:F0}→{_cfg.MaxPxPerSec:F0} px/秒 · 斜坡 {_cfg.RampMs:F0} ms"
                      + (_cfg.NormalizeDiagonal ? " · 斜向归一化" : "")
                      + $"（增益 {_gain:F2}）");
        }
        SaveConfig();
        return sb.ToString();
    }

    // ───────────────────────── 模式开关 ─────────────────────────

    public bool Enabled => _enabled;

    /// <summary>开/关鼠标模式。开启时启动引擎线程（并在需要时做一次自校准）。</summary>
    public string SetEnabled(bool on)
    {
        if (_enabled == on) return $"已经是{(on ? "鼠标" : "键盘")}模式。";
        if (on)
        {
            _enabled = true;
            _stop = false;
            lock (_gate) { _dirHeld.Clear(); _dirSince = DateTime.MinValue; _accX = _accY = 0; }
            if (_cfg.Calibrate) Calibrate();
            if (_thread == null || !_thread.IsAlive)
            {
                _thread = new Thread(Loop) { IsBackground = true, Name = "iprc1000-mouse", Priority = ThreadPriority.AboveNormal };
                _thread.Start();
            }
            var msg = "已切到鼠标模式：方向键=移动（按住会加速）｜确认=左键｜info=右键。"
                      + $"再按一次方块键切回键盘模式。" + (_gain != 1.0 ? $"（已按实测增益 {_gain:F2} 校正）" : "");
            Log($"模式 → 鼠标（速度 {_cfg.MinPxPerSec:F0}→{_cfg.MaxPxPerSec:F0} px/s，斜坡 {_cfg.RampMs:F0}ms，"
                + $"tick {_cfg.TickHz}Hz，增益 {_gain:F2}{(_accelSuspected ? "，★ 疑似系统加速开启" : "")}）");
            LastNote = msg;
            return msg;
        }

        _enabled = false;
        ReleaseAll("切回键盘模式");
        // 关掉就把线程收掉：常驻托盘应用不该留着 60 Hz 的空转线程。
        // （键盘模式下本引擎也不碰鼠标，所以没有"孤儿键要回收"这回事）
        _stop = true;
        try { _thread?.Join(500); } catch { }
        _thread = null;
        Log("模式 → 键盘（引擎线程已停）");
        LastNote = "已切回键盘模式（全部按键恢复原映射）。";
        return LastNote;
    }

    public string Toggle()
    {
        var msg = SetEnabled(!_enabled);
        if (!_enabled && _cfg.RememberMode) SaveMode(false);
        else if (_enabled && _cfg.RememberMode) SaveMode(true);
        return msg;
    }

    private void SaveMode(bool mouse)
    {
        try
        {
            var p = Path.Combine(Env.ConfigDir, "mouse-mode.txt");
            File.WriteAllText(p, mouse ? "mouse" : "keyboard");
        }
        catch { }
    }

    /// <summary>上次退出时是不是鼠标模式（只有 RememberMode 打开才生效）</summary>
    public static bool WasMouseModeLastTime()
    {
        try
        {
            var p = Path.Combine(Env.ConfigDir, "mouse-mode.txt");
            return File.Exists(p) && File.ReadAllText(p).Trim() == "mouse";
        }
        catch { return false; }
    }

    // ───────────────────────── 按键路由（由 ApplyButton 调）─────────────────────────

    private static readonly HashSet<string> DirectionIds = new() { "up", "down", "left", "right" };
    private static readonly HashSet<string> MouseButtonIds = new() { "ok", "info" };

    /// <summary>鼠标模式下本引擎接管哪些按键（其余仍走配置组）</summary>
    public static bool Handles(string bid) => DirectionIds.Contains(bid) || MouseButtonIds.Contains(bid);

    /// <summary>
    /// 处理一个按键事件。<returns>true = 已消费（不要再走键盘映射）</returns>
    /// </summary>
    public bool HandleButton(string bid, bool pressed)
    {
        if (!_enabled || !Handles(bid)) return false;

        if (DirectionIds.Contains(bid))
        {
            lock (_gate)
            {
                if (pressed)
                {
                    if (_dirHeld.Count == 0) _dirSince = DateTime.UtcNow;   // 斜坡起点
                    _dirHeld.Add(bid);
                }
                else
                {
                    _dirHeld.Remove(bid);
                    if (_dirHeld.Count == 0) { _dirSince = DateTime.MinValue; _accX = _accY = 0; }
                }
                Log($"方向 {bid} {(pressed ? "按下" : "松开")} → 当前 {string.Join("+", _dirHeld)}");
            }
            return true;
        }

        // ok → 左键，info → 右键（**按住/抬起成对**：这样"按住方向 + 按住确认 = 拖拽"免费成立）
        var isLeft = bid == "ok";
        if (pressed)
        {
            if (isLeft) { _leftDown = true; _leftAt = DateTime.UtcNow; MouseInjector.Left(true); }
            else { _rightDown = true; _rightAt = DateTime.UtcNow; MouseInjector.Right(true); }
            lock (_gate) _btnAt[bid] = DateTime.UtcNow;
            Log($"鼠标 {(isLeft ? "左键" : "右键")} 按下");
        }
        else
        {
            if (isLeft) { _leftDown = false; MouseInjector.Left(false); }
            else { _rightDown = false; MouseInjector.Right(false); }
            lock (_gate) _btnAt.Remove(bid);
            Log($"鼠标 {(isLeft ? "左键" : "右键")} 抬起");
        }
        return true;
    }

    // ───────────────────────── 引擎线程 ─────────────────────────

    private void Loop()
    {
        var sw = Stopwatch.StartNew();
        var last = sw.Elapsed;
        var next = sw.Elapsed;
        while (!_stop)
        {
            var hz = Math.Clamp(_cfg.TickHz, 20, 200);
            next += TimeSpan.FromSeconds(1.0 / hz);
            var wait = next - sw.Elapsed;
            if (wait > TimeSpan.Zero) Thread.Sleep(wait);      // 1ms 粒度：Main 里已 timeBeginPeriod(1)
            else next = sw.Elapsed;                            // 落后了就重排，避免追赶风暴

            var now = sw.Elapsed;
            var dt = (now - last).TotalSeconds;
            last = now;
            if (dt <= 0) continue;
            if (dt > 0.25) dt = 0.25;                          // 系统休眠回来时不要一次灌进几十秒的位移

            try { Step(dt); } catch { }
        }
    }

    private void Step(double dt)
    {
        string[] dirs;
        DateTime since;
        lock (_gate) { dirs = _dirHeld.ToArray(); since = _dirSince; }

        // ① 方向键按住超时（松开 UP 丢了也要停下来）
        if (dirs.Length > 0 && since != DateTime.MinValue
            && (DateTime.UtcNow - since).TotalSeconds > _cfg.DirectionHoldTimeoutSec)
        {
            lock (_gate) { _dirHeld.Clear(); _dirSince = DateTime.MinValue; _accX = _accY = 0; }
            _dirTimeouts++;
            var msg = $"GUARD  鼠标方向键按住超时（>{_cfg.DirectionHoldTimeoutSec:F0}s）→ 已停止移动"
                      + $"（之前是 {string.Join("+", dirs)}；多为遥控器 UP 丢失）";
            Log(msg);
            GuardLog(msg);
            LastNote = msg;
            dirs = Array.Empty<string>();
        }

        // ② 方向向量
        double vx = 0, vy = 0;
        foreach (var d in dirs)
        {
            switch (d)
            {
                case "left": vx -= 1; break;
                case "right": vx += 1; break;
                case "up": vy -= 1; break;
                case "down": vy += 1; break;
            }
        }

        if (vx != 0 || vy != 0)
        {
            // ③ 自适应增速：按"方向集合持续按住多久"算，按住越久越快
            var heldMs = since == DateTime.MinValue ? 0 : (DateTime.UtcNow - since).TotalMilliseconds;
            var t = _cfg.RampMs <= 0 ? 1.0 : Math.Min(heldMs / _cfg.RampMs, 1.0);
            var e = Math.Pow(t, Math.Max(_cfg.RampExponent, 0.1));
            var speed = _cfg.MinPxPerSec + (_cfg.MaxPxPerSec - _cfg.MinPxPerSec) * e;

            // ④ 斜向归一化（不然斜着走快 41%）
            if (vx != 0 && vy != 0 && _cfg.NormalizeDiagonal)
            {
                var inv = 1.0 / Math.Sqrt(2);
                vx *= inv; vy *= inv;
            }

            // ⑤ 浮点累加：速度可能是 1.3 px/帧，直接取整会"走走停停"
            _accX += vx * speed / _gain * dt;
            _accY += vy * speed / _gain * dt;
            var sx = (int)Math.Truncate(_accX);
            var sy = (int)Math.Truncate(_accY);
            if (sx != 0 || sy != 0)
            {
                _accX -= sx; _accY -= sy;
                MouseInjector.Move(sx, sy);
                PixelsMoved += Math.Sqrt((double)sx * sx + (double)sy * sy);
            }
            Frames++;
        }
        else
        {
            _accX = _accY = 0;
        }

        // ⑥ 鼠标键按住超时（卡住的左键会让人点不了任何东西）
        if (_leftDown && (DateTime.UtcNow - _leftAt).TotalSeconds > _cfg.ButtonHoldTimeoutSec)
        {
            _leftDown = false; MouseInjector.Left(false); _btnTimeouts++;
            var msg = $"GUARD  鼠标左键按住超时（>{_cfg.ButtonHoldTimeoutSec:F0}s）→ 已强制抬起";
            Log(msg); GuardLog(msg); LastNote = msg;
        }
        if (_rightDown && (DateTime.UtcNow - _rightAt).TotalSeconds > _cfg.ButtonHoldTimeoutSec)
        {
            _rightDown = false; MouseInjector.Right(false); _btnTimeouts++;
            var msg = $"GUARD  鼠标右键按住超时（>{_cfg.ButtonHoldTimeoutSec:F0}s）→ 已强制抬起";
            Log(msg); GuardLog(msg); LastNote = msg;
        }

        // ⑦ 孤儿键回收（每 1 秒一次）：以**系统状态**为准
        if (DateTime.UtcNow >= _nextOrphanCheck)
        {
            _nextOrphanCheck = DateTime.UtcNow.AddSeconds(1);
            ReapOrphanButtons();
        }
    }

    /// <summary>
    /// 鼠标键的孤儿回收 —— 与键盘那边同一个思路：**不问我们自己的账，问系统**。
    /// `GetAsyncKeyState(VK_LBUTTON)` 反映的是**合并后**的按键状态。
    ///
    /// ⚠ 误伤边界：用户**物理**按住左键拖窗口时，系统也会报按下。所以要求
    ///   ① 我们此刻不持有该键（`_leftDown == false`）；
    ///   ② 连续 ≥ <see cref="Config.ButtonOrphanSec"/> 秒都这样（真人拖拽很少超过 15 秒）；
    ///   ③ 只在鼠标模式下做（键盘模式下本引擎不碰鼠标）。
    /// 三条同时满足才补一次抬起，并且每次都会写日志 —— 事后可查是不是误伤。
    /// </summary>
    private void ReapOrphanButtons()
    {
        if (!_enabled) { _organStreak = _orphanStreak = 0; return; }

        var sysLeft = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
        var sysRight = (GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0;
        var needed = (int)Math.Max(_cfg.ButtonOrphanSec, 2);

        _organStreak = (sysLeft && !_leftDown) ? _organStreak + 1 : 0;
        _orphanStreak = (sysRight && !_rightDown) ? _orphanStreak + 1 : 0;

        if (_organStreak >= needed)
        {
            _organStreak = 0;
            MouseInjector.Left(false);
            _orphanReaps++;
            LastReap = DateTime.Now;
            var msg = $"GUARD  鼠标左键孤儿回收：系统仍认为按着、但我们没持有它"
                      + $"（连续 {needed} 秒）→ 已补一次抬起";
            Log(msg); GuardLog(msg); LastNote = msg;
        }
        if (_orphanStreak >= needed)
        {
            _orphanStreak = 0;
            MouseInjector.Right(false);
            _orphanReaps++;
            LastReap = DateTime.Now;
            var msg = $"GUARD  鼠标右键孤儿回收：系统仍认为按着、但我们没持有它"
                      + $"（连续 {needed} 秒）→ 已补一次抬起";
            Log(msg); GuardLog(msg); LastNote = msg;
        }
    }

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);

    // ───────────────────────── 收尾 ─────────────────────────

    /// <summary>把所有鼠标键抬起并清空方向（退出 / 切模式 / 紧急复位时调）</summary>
    public void ReleaseAll(string why)
    {
        lock (_gate) { _dirHeld.Clear(); _dirSince = DateTime.MinValue; _accX = _accY = 0; _btnAt.Clear(); }
        var had = _leftDown || _rightDown;
        if (_leftDown) { _leftDown = false; try { MouseInjector.Left(false); } catch { } }
        if (_rightDown) { _rightDown = false; try { MouseInjector.Right(false); } catch { } }
        if (had) Log($"ReleaseAll（{why}）");
    }

    public void Stop()
    {
        _stop = true;
        ReleaseAll("引擎停止");
        if (_cfg.RememberMode) SaveMode(_enabled);
        try { _thread?.Join(500); } catch { }
        _thread = null;
    }

    // ───────────────────────── 自校准 ─────────────────────────

    /// <summary>
    /// 实测"请求 1 px → 实际走多少 px"的增益。
    ///
    /// 为什么必须做：阶段 0 实测本机是**严格 1:1**，但那是在「指针精确度关 + 指针速度默认档」
    /// 的前提下；门店机器这两项都可能不同，写死像素就会走得太快或太慢。
    /// 另外如果**小增量和大增量的比值不一致**，说明系统加速开着（我们的斜坡会与之叠加），
    /// 这时把 `_accelSuspected` 置起来，界面上会提示用户。
    ///
    /// 做法：把光标挪到虚拟桌面中心 → 发 ±d 各测几次取中位数 → 还原光标。约 0.3 秒。
    /// </summary>
    public void Calibrate()
    {
        try
        {
            var (vx, vy, vw, vh) = MouseInjector.VirtualScreen();
            if (vw <= 0 || vh <= 0) return;
            var start = MouseInjector.CursorPos();

            double Measure(int d)
            {
                var got = new List<double>();
                for (var i = 0; i < 3; i++)
                {
                    MouseInjector.MoveAbsolute((vx + vw / 2) * 65535 / Math.Max(vw - 1, 1),
                                               (vy + vh / 2) * 65535 / Math.Max(vh - 1, 1));
                    Thread.Sleep(25);
                    var (x0, _) = MouseInjector.CursorPos();
                    MouseInjector.Move(d, 0);
                    Thread.Sleep(20);
                    var (x1, _) = MouseInjector.CursorPos();
                    got.Add(x1 - x0);
                }
                got.Sort();
                return got[got.Count / 2];
            }

            var small = Measure(6);
            var big = Measure(48);
            var gain = big <= 0 ? 1.0 : big / 48.0;
            _gain = Math.Clamp(gain, 0.2, 5.0);
            // 小增量与大增量的比值差得多 → 增益不是常数 → 系统加速在起作用
            var smallRatio = small / 6.0;
            _accelSuspected = Math.Abs(smallRatio - gain) / Math.Max(gain, 0.01) > 0.25;

            if (start.X >= 0 && start.Y >= 0)
                MouseInjector.MoveAbsolute((start.X - vx) * 65535 / Math.Max(vw - 1, 1),
                                           (start.Y - vy) * 65535 / Math.Max(vh - 1, 1));

            Log($"自校准：请求 6px→实际 {small:F0}px（比值 {smallRatio:F2}）｜"
                + $"请求 48px→实际 {big:F0}px（比值 {gain:F2}）⇒ 采用增益 {_gain:F2}"
                + (_accelSuspected ? "  ★ 两档比值不一致 → 系统「指针精确度」可能开着，速度会与你调的值有偏差" : ""));
        }
        catch (Exception e) { Log("自校准失败（按增益 1.0 继续）：" + e.Message); }
    }

    // ───────────────────────── 状态 / 日志 ─────────────────────────

    public object Status()
    {
        string[] dirs;
        lock (_gate) dirs = _dirHeld.ToArray();
        return new
        {
            mode = _enabled ? "mouse" : "keyboard",
            enabled = _enabled,
            held = dirs,
            gain = Math.Round(_gain, 3),
            accelSuspected = _accelSuspected,
            tickHz = _cfg.TickHz,
            minPxPerSec = _cfg.MinPxPerSec,
            maxPxPerSec = _cfg.MaxPxPerSec,
            rampMs = _cfg.RampMs,
            rampExponent = _cfg.RampExponent,
            normalizeDiagonal = _cfg.NormalizeDiagonal,
            directionHoldTimeoutSec = _cfg.DirectionHoldTimeoutSec,
            buttonHoldTimeoutSec = _cfg.ButtonHoldTimeoutSec,
            rememberMode = _cfg.RememberMode,
            calibrate = _cfg.Calibrate,
            frames = Frames,
            pixelsMoved = Math.Round(PixelsMoved),
            dirTimeouts = _dirTimeouts,
            btnTimeouts = _btnTimeouts,
            orphanReaps = _orphanReaps,
            lastReap = LastReap?.ToString("HH:mm:ss"),
            note = LastNote,
        };
    }

    /// <summary>鼠标日志（独立一份，和 router.log / stuck-guard.log 同样的风格与 BOM 约定）</summary>
    private static readonly object _logLock = new();
    public static void Log(string msg)
    {
        try
        {
            var p = Path.Combine(Env.ConfigDir, "mouse.log");
            var body = $"{DateTime.Now:HH:mm:ss.fff}  {msg}\n";
            lock (_logLock)
            {
                if (!File.Exists(p)) File.WriteAllText(p, body, new UTF8Encoding(true));
                else File.AppendAllText(p, body, new UTF8Encoding(false));
            }
        }
        catch { }
    }

    private static void GuardLog(string msg)
    {
        try
        {
            var p = Path.Combine(Env.ConfigDir, "stuck-guard.log");
            var body = $"{DateTime.Now:HH:mm:ss.fff}  {msg}\n";
            lock (typeof(MouseEngine))
            {
                if (!File.Exists(p)) File.WriteAllText(p, body, new UTF8Encoding(true));
                else File.AppendAllText(p, body, new UTF8Encoding(false));
            }
        }
        catch { }
    }

    // ───────────────────────── 自检（--mouse-engine-test）─────────────────────────

    /// <summary>
    /// 引擎端到端自检 —— **不需要遥控器**，直接驱动引擎并量真实光标。
    /// 断言：
    ///   ① 不开引擎时按住方向键，光标不动（路由没消费）
    ///   ② 开引擎后按住 right 1.2 s，光标向右移动，且**后半段比前半段快**（自适应增速生效）
    ///   ③ 松开后**立即停住**（不漂移）
    ///   ④ 确认键 → 系统左键真的按下/抬起（`GetAsyncKeyState(VK_LBUTTON)`）
    ///   ⑤ 斜向不超速（同时按 right+down，合速度与单方向同量级）
    /// </summary>
    public static string SelfTest()
    {
        var sb = new StringBuilder();
        var e = new MouseEngine();
        bool LeftSys() => (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;

        sb.AppendLine("IPRC1000 鼠标引擎自检");
        sb.AppendLine(new string('=', 58));
        var ok = true;
        void Check(bool pass, string what, string detail)
        {
            ok &= pass;
            sb.AppendLine($"  {(pass ? "✓" : "✗")} {what}");
            sb.AppendLine($"       {detail}");
        }

        /// <summary>按住某个方向集合 ms 毫秒，返回这段时间的位移（基准用"归位并停稳"取）</summary>
        (double dist, double dx, double dy) Hold(string[] dirs, int ms)
        {
            var (x0, y0) = MouseInjector.CenterAndSettle();
            foreach (var d in dirs) e.HandleButton(d, true);
            Thread.Sleep(ms);
            (int X, int Y) p = MouseInjector.CursorPos();
            foreach (var d in dirs) e.HandleButton(d, false);
            Thread.Sleep(120);                       // 让引擎真的停下来再取终值
            var (x1, y1) = MouseInjector.CursorPos();
            return (Math.Sqrt(Math.Pow(x1 - x0, 2) + Math.Pow(y1 - y0, 2)), x1 - x0, y1 - y0);
        }

        try
        {
            // ① 未启用 → 不消费
            e.SetEnabled(false);
            var consumed = e.HandleButton("right", true);
            Check(!consumed, "未开鼠标模式时方向键不被引擎消费", $"HandleButton(right) 返回 {consumed}（期望 false）");

            // ② 启用后按住 right：分两段量，看增速
            e.SetEnabled(true);
            var (x0, _) = MouseInjector.CenterAndSettle();
            e.HandleButton("right", true);
            Thread.Sleep(600);
            var (xm, _) = MouseInjector.CursorPos();
            Thread.Sleep(600);
            var (x2, _) = MouseInjector.CursorPos();
            e.HandleButton("right", false);
            var first = xm - x0;
            var second = x2 - xm;
            Check(first > 0 && second > 0, "按住 right 光标向右移动",
                  $"前 0.6s 走 {first} px，后 0.6s 走 {second} px（合计 {x2 - x0} px）");
            Check(second > first, "自适应增速生效（后半段更快）",
                  $"前半 {first} px < 后半 {second} px（比值 {(double)second / Math.Max(first, 1):F2}）");

            // ③ 松开后停住
            Thread.Sleep(400);
            var (x3, _) = MouseInjector.CursorPos();
            Check(Math.Abs(x3 - x2) <= 2, "松开后立即停住（无漂移）", $"松开后 0.4 s 又动了 {x3 - x2} px");

            // ④ 确认键 → 左键
            e.HandleButton("ok", true);
            Thread.Sleep(80);
            var down = LeftSys();
            e.HandleButton("ok", false);
            Thread.Sleep(80);
            var up = LeftSys();
            Check(down && !up, "确认键 → 系统左键按下/抬起", $"按下时系统左键={down}，抬起后={up}");

            // ⑤ 斜向归一化：**同样时长**下比较单方向与斜向的位移
            //   （时长必须一样，否则斜坡进度不同，比出来没意义 —— 第一版就是这么比错的）
            const int T = 800;
            var single = Hold(new[] { "right" }, T);
            var diag = Hold(new[] { "right", "down" }, T);
            Check(diag.dist > 0 && diag.dist <= single.dist * 1.15,
                  "斜向不超速（已归一化）",
                  $"{T}ms 单方向 {single.dist:F0} px vs 斜向 {diag.dist:F0} px"
                  + $"（比值 {diag.dist / Math.Max(single.dist, 1):F2}，归一化后应 ≈1.00）");

            // ⑥ 全部抬起
            e.ReleaseAll("自检收尾");
            Thread.Sleep(80);
            Check(!LeftSys(), "收尾后系统左键已抬起", $"系统左键={LeftSys()}");
        }
        catch (Exception ex)
        {
            ok = false;
            sb.AppendLine("  ✗ 自检异常：" + ex.Message);
        }
        finally { e.Stop(); }

        sb.AppendLine();
        sb.AppendLine(ok ? "⇒ 全部通过 ✓" : "⇒ ★ 有断言失败");
        return sb.ToString();
    }
}
