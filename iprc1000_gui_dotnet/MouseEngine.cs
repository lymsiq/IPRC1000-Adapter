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

        /// <summary>
        /// 鼠标模式下把 **音量± 当滚轮** 用（默认**关**）。
        /// 为什么默认关：有人还想在鼠标模式下调音量，默默地改掉这两个键的行为是越权。
        /// 打开后：音量+ = 上滚、音量− = 下滚，按住会加速重复（翻长页很实用）。
        /// </summary>
        public bool WheelOnVolumeKeys { get; set; }
        /// <summary>滚轮起步速度（格/秒）—— 每格 = 系统的一格（120）</summary>
        public double WheelMinPerSec { get; set; } = 3;
        /// <summary>滚轮满速（格/秒），与方向键共用同一个斜坡时长</summary>
        public double WheelMaxPerSec { get; set; } = 16;

        // ★ v1.2.4 起**没有** `squareKeyAction` 这个配置了：
        //   鼠标模式 = 活动配置组是不是"鼠标组"（Keymaps.MouseGroupActive），
        //   方块键则是一个普通可绑定键（出厂值 = 动作值「切换配置组」）。
        //   旧 mouse.json 里残留的这个字段会被静默忽略（System.Text.Json 默认行为）。
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

    // 滚轮（音量± 当滚轮用时）：按住会按斜坡加速重复
    private bool _wheelUp, _wheelDown;
    private DateTime _wheelSince = DateTime.MinValue;
    private double _wheelAcc;

    // 界面「实时校准区」用的位移增量（自上次被取走起累计）
    private double _deltaX, _deltaY;
    private DateTime _deltaSince = DateTime.MinValue;

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

    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        // ★ 名字大小写不敏感：程序自己写出的 mouse.json 是 PascalCase，
        //   但**手改配置**时很自然写成 camelCase（和界面发来的 payload 一致）。
        //   不设这个的话，手写的 camelCase 键会被**静默忽略** —— 实测踩到过。
        PropertyNameCaseInsensitive = true,
    };

    private static Config Load()
    {
        try
        {
            if (File.Exists(Path_))
            {
                var c = JsonSerializer.Deserialize<Config>(File.ReadAllText(Path_), ReadOpts) ?? new Config();
                Sanitize(c);      // ★ 文件里的非法值必须也在这一道闸里被夹住，不能静默生效
                return c;
            }
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
    /// <summary>
    /// 把配置夹到合法范围、校验枚举值。**文件路径与界面路径都必须过这一道** ——
    /// 否则手改 `mouse.json` 写进非法值会**静默生效**。
    /// 实测踩到过：文件里写 `squareKeyAction:"乱写"`，程序当成"切键鼠模式"
    /// （因为判定写的是"不等于 group 就算 mode"）。
    /// </summary>
    private static void Sanitize(Config c)
    {
        c.TickHz = Math.Clamp(c.TickHz, 20, 200);
        c.MinPxPerSec = Math.Clamp(c.MinPxPerSec, 10, 400);
        c.MaxPxPerSec = Math.Clamp(c.MaxPxPerSec, 100, 6000);
        if (c.MinPxPerSec > c.MaxPxPerSec) c.MinPxPerSec = c.MaxPxPerSec;
        c.RampMs = Math.Clamp(c.RampMs, 0, 4000);
        c.RampExponent = Math.Clamp(c.RampExponent, 1, 4);
        c.WheelMinPerSec = Math.Clamp(c.WheelMinPerSec, 1, 30);
        c.WheelMaxPerSec = Math.Clamp(c.WheelMaxPerSec, 2, 60);
        if (c.WheelMinPerSec > c.WheelMaxPerSec) c.WheelMinPerSec = c.WheelMaxPerSec;
        c.DirectionHoldTimeoutSec = Math.Clamp(c.DirectionHoldTimeoutSec, 2, 60);
        c.ButtonHoldTimeoutSec = Math.Clamp(c.ButtonHoldTimeoutSec, 2, 60);
    }

    public string ApplyConfig(JsonElement el)
    {
        var sb = new StringBuilder();
        lock (_gate)
        {
            double D(string k, double cur) =>
                el.TryGetProperty(k, out var v) && v.TryGetDouble(out var d) ? d : cur;
            bool B(string k, bool cur) =>
                el.TryGetProperty(k, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
                    ? v.GetBoolean() : cur;

            _cfg.TickHz = (int)D("tickHz", _cfg.TickHz);
            _cfg.MinPxPerSec = D("minPxPerSec", _cfg.MinPxPerSec);
            _cfg.MaxPxPerSec = D("maxPxPerSec", _cfg.MaxPxPerSec);
            _cfg.RampMs = D("rampMs", _cfg.RampMs);
            _cfg.RampExponent = D("rampExponent", _cfg.RampExponent);
            _cfg.NormalizeDiagonal = B("normalizeDiagonal", _cfg.NormalizeDiagonal);
            _cfg.DirectionHoldTimeoutSec = D("directionHoldTimeoutSec", _cfg.DirectionHoldTimeoutSec);
            _cfg.ButtonHoldTimeoutSec = D("buttonHoldTimeoutSec", _cfg.ButtonHoldTimeoutSec);
            _cfg.RememberMode = B("rememberMode", _cfg.RememberMode);
            _cfg.Calibrate = B("calibrate", _cfg.Calibrate);
            _cfg.WheelOnVolumeKeys = B("wheelOnVolumeKeys", _cfg.WheelOnVolumeKeys);
            _cfg.WheelMinPerSec = D("wheelMinPerSec", _cfg.WheelMinPerSec);
            _cfg.WheelMaxPerSec = D("wheelMaxPerSec", _cfg.WheelMaxPerSec);
            Sanitize(_cfg);     // 夹范围 + 校验枚举：与文件路径共用同一道闸
            sb.Append($"已应用：{_cfg.MinPxPerSec:F0}→{_cfg.MaxPxPerSec:F0} px/秒 · 斜坡 {_cfg.RampMs:F0} ms"
                      + (_cfg.NormalizeDiagonal ? " · 斜向归一化" : "")
                      + (_cfg.WheelOnVolumeKeys ? $" · 音量键当滚轮（{_cfg.WheelMinPerSec:F0}→{_cfg.WheelMaxPerSec:F0} 格/秒）" : "")
                      + $"（增益 {_gain:F2}）");
        }
        SaveConfig();
        return sb.ToString();
    }

    // ───────────────────────── 模式开关 ─────────────────────────

    public bool Enabled => _enabled;
    /// <summary>实测增益（请求 1 px 实际走多少 px）—— 界面显示用</summary>
    public double Gain => _gain;
    /// <summary>小/大增量比值不一致 → 系统加速开着，会与斜坡叠加</summary>
    public bool AccelSuspected => _accelSuspected;
    /// <summary>方块键是不是用来切键鼠模式（否则就是切配置组）</summary>
    /// <summary>开/关鼠标模式。开启时启动引擎线程（并在需要时做一次自校准）。</summary>
    public string SetEnabled(bool on)
    {
        if (_enabled == on) return $"已经是{(on ? "鼠标" : "键盘")}模式。";
        if (on)
        {
            _enabled = true;
            _stop = false;
            lock (_gate) { _dirHeld.Clear(); _dirSince = DateTime.MinValue; _accX = _accY = 0; }
            ResetUseCalibration();              // 重新开始被动标定
            _useRatios.Clear();
            // ★ 这里**不做**主动校准 —— 那会把光标搬到屏幕中心再弹回，用户看得见。
            //   增益改由 CalibrateFromUse() 在真实使用中学习（一次都不多动光标）。
            //   想立刻量一次，用界面的「重新自校准」（那种就地测法）。
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
    private static readonly HashSet<string> WheelKeyIds = new() { "volume_up", "volume_down" };

    /// <summary>鼠标模式下本引擎接管哪些按键（其余仍走配置组）。
    /// 音量键只有在"音量当滚轮"打开时才接管 —— 默认不碰它们。</summary>
    public bool Handles(string bid)
        => DirectionIds.Contains(bid) || MouseButtonIds.Contains(bid)
           || (_cfg.WheelOnVolumeKeys && WheelKeyIds.Contains(bid));

    /// <summary>
    /// 处理一个按键事件。<returns>true = 已消费（不要再走键盘映射）</returns>
    /// </summary>
    public bool HandleButton(string bid, bool pressed)
    {
        if (!_enabled || !Handles(bid)) return false;

        // 音量± → 滚轮（可选）
        if (WheelKeyIds.Contains(bid))
        {
            if (bid == "volume_up") _wheelUp = pressed; else _wheelDown = pressed;
            var any = _wheelUp || _wheelDown;
            if (any) _wheelSince = DateTime.UtcNow; else { _wheelSince = DateTime.MinValue; _wheelAcc = 0; }
            Log($"滚轮 {bid} {(pressed ? "按下" : "松开")}");
            return true;
        }

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
        // ⓪ 被动标定：**帧首**读光标。上一帧发出去的位移早在这 16 ms 里生效了，
        //    此时读到的才是最新的真实位置；放在 Move() 之后紧接着读会读到尚未生效的旧值。
        if (_cfg.Calibrate) CalibrateFromUse();

        string[] dirs;
        DateTime since;
        lock (_gate) { dirs = _dirHeld.ToArray(); since = _dirSince; }

        // ① 方向键按住超时（松开 UP 丢了也要停下来）
        if (dirs.Length > 0 && since != DateTime.MinValue
            && (DateTime.UtcNow - since).TotalSeconds > _cfg.DirectionHoldTimeoutSec)
        {
            lock (_gate) { _dirHeld.Clear(); _dirSince = DateTime.MinValue; _accX = _accY = 0; }
            _wantValid = false;
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
                lock (_gate) { _deltaX += sx; _deltaY += sy; }   // 供界面实时校准区取数
                _wantX = sx; _wantY = sy; _wantValid = true;      // 下一帧首去对账
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

        // ⑦ 滚轮（音量± 当滚轮用时）：按住按斜坡加速重复 —— 翻长页比一格一格点强得多
        if (_wheelUp || _wheelDown)
        {
            if ((DateTime.UtcNow - _wheelSince).TotalSeconds > _cfg.DirectionHoldTimeoutSec)
            {
                _wheelUp = _wheelDown = false; _wheelSince = DateTime.MinValue; _wheelAcc = 0;
                var msg = $"GUARD  滚轮键按住超时（>{_cfg.DirectionHoldTimeoutSec:F0}s）→ 已停止";
                Log(msg); GuardLog(msg); LastNote = msg; _dirTimeouts++;
            }
            else
            {
                var heldMs = _wheelSince == DateTime.MinValue ? 0 : (DateTime.UtcNow - _wheelSince).TotalMilliseconds;
                var t = _cfg.RampMs <= 0 ? 1.0 : Math.Min(heldMs / _cfg.RampMs, 1.0);
                var e = Math.Pow(t, Math.Max(_cfg.RampExponent, 0.1));
                var perSec = _cfg.WheelMinPerSec + (_cfg.WheelMaxPerSec - _cfg.WheelMinPerSec) * e;
                _wheelAcc += perSec * dt;
                var notches = (int)_wheelAcc;
                if (notches > 0)
                {
                    _wheelAcc -= notches;
                    MouseInjector.Wheel(_wheelUp ? 120 * notches : -120 * notches);
                    lock (_gate) _deltaY += _wheelUp ? -notches * 3 : notches * 3;  // 校准区里也体现出来
                }
            }
        }

        // ⑧ 孤儿键回收（每 1 秒一次）：以**系统状态**为准
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

    /// <summary>
    /// 取走"自上次调用以来累计的鼠标位移" + **光标当前的真实位置与虚拟桌面尺寸**
    /// （界面「实时校准区」按 ~20 Hz 轮询用）。
    ///
    /// 为什么要把光标位置也带上：校准区的光点应该**映射到光标的真实位置**（一台小地图），
    /// 而不是"从一个固定点开始累加位移" —— 后者会漂移，而且跟真实光标对不上。
    /// </summary>
    public Sample TakeSample()
    {
        var (x, y) = MouseInjector.CursorPos();
        var (vx, vy, vw, vh) = MouseInjector.VirtualScreen();
        lock (_gate)
        {
            var dx = _deltaX;
            var dy = _deltaY;
            var now = DateTime.UtcNow;
            var sec = _deltaSince == DateTime.MinValue ? 0 : (now - _deltaSince).TotalSeconds;
            _deltaX = _deltaY = 0;
            _deltaSince = now;
            return new Sample(dx, dy, Math.Sqrt(dx * dx + dy * dy), sec, x, y, vx, vy, vw, vh);
        }
    }

    /// <summary>界面校准区的一次采样：位移 + 光标真实位置 + 虚拟桌面</summary>
    public readonly record struct Sample(
        double Dx, double Dy, double Px, double Sec,
        int X, int Y, int Vx, int Vy, int Vw, int Vh);

    /// <summary>把所有鼠标键抬起并清空方向（退出 / 切模式 / 紧急复位时调）</summary>
    public void ReleaseAll(string why)
    {
        lock (_gate)
        {
            _dirHeld.Clear(); _dirSince = DateTime.MinValue; _accX = _accY = 0; _btnAt.Clear();
            _deltaX = _deltaY = 0;
        }
        _wheelUp = _wheelDown = false; _wheelSince = DateTime.MinValue; _wheelAcc = 0;
        ResetUseCalibration();              // 方向停了，下一次位移重新起算
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
    /// 主动自校准：实测"请求 1 px → 实际走多少 px"的增益。
    ///
    /// ★ **就在光标当前位置测，绝不把光标搬到屏幕中心。**
    ///   绝对定位会让用户看到光标猛地跳走再弹回（进鼠标模式那一刻尤其突兀），
    ///   多显示器下"虚拟桌面中心"还可能落在另一块屏上。
    ///   做法：只做 **+d / −d 成对移动，净位移为 0**；靠近屏幕边缘时某一侧会被裁，
    ///   就取**没被裁的那一侧**的幅度。
    ///
    /// ⚠ 这个函数只在**用户主动点「重新自校准」**时才跑（会有 ±24 px 的轻微抖动，用户预期之内）。
    ///   自动路径走 <see cref="CalibrateFromUse"/> —— 用用户自己的移动去学，**一次都不多动光标**。
    /// </summary>
    public void Calibrate()
    {
        try
        {
            const int D1 = 6, D2 = 24;

            // 返回 (+d 方向实际走了多少, 回到原位时实际走了多少)；被屏幕边缘裁掉的一侧为 0
            (double fwd, double back) MeasurePair(int d)
            {
                var (x0, _) = MouseInjector.CursorPos();
                MouseInjector.Move(d, 0);
                Thread.Sleep(20);
                var (x1, _) = MouseInjector.CursorPos();
                MouseInjector.Move(-d, 0);      // 立刻退回原位 → 净位移 0
                Thread.Sleep(20);
                var (x2, _) = MouseInjector.CursorPos();
                return (x1 - x0, x1 - x2);
            }

            double GainAt(int d)
            {
                var rs = new List<double>();
                for (var i = 0; i < 3; i++)
                {
                    var (f, b) = MeasurePair(d);
                    var got = Math.Max(Math.Abs(f), Math.Abs(b));   // 取没被裁的那一侧
                    if (got > 0) rs.Add(got / d);
                }
                if (rs.Count == 0) return 0;
                rs.Sort();
                return rs[rs.Count / 2];
            }

            var smallRatio = GainAt(D1);
            var bigRatio = GainAt(D2);
            if (bigRatio <= 0 && smallRatio <= 0)
            {
                LastNote = "自校准失败：两次测量都没测到位移（光标可能在屏幕边缘或注入被挡）。";
                Log(LastNote);
                return;
            }
            var gain = bigRatio > 0 ? bigRatio : smallRatio;
            _gain = Math.Clamp(gain, 0.2, 5.0);
            _accelSuspected = smallRatio > 0 && bigRatio > 0
                              && Math.Abs(smallRatio - bigRatio) / Math.Max(bigRatio, 0.01) > 0.25;

            Log($"主动自校准（就地，未搬动光标）：请求 {D1}px → ×{smallRatio:F2}｜"
                + $"请求 {D2}px → ×{bigRatio:F2} ⇒ 采用增益 {_gain:F2}"
                + (_accelSuspected ? "  ★ 两档比值不一致 → 系统「指针精确度」可能开着，速度会与你调的值有偏差" : ""));
            LastNote = $"自校准完成：实测增益 {_gain:F2}×"
                       + (_accelSuspected ? "（★ 两档比值不一致，系统「指针精确度」可能开着）" : "（相对移动精确可控）");
        }
        catch (Exception e)
        {
            Log("自校准失败（按当前增益继续）：" + e.Message);
            LastNote = "自校准失败（按当前增益继续）：" + e.Message;
        }
    }

    // ── 被动标定：用**用户自己的移动**去学增益 ──
    private (int X, int Y)? _lastCursor;
    private int _wantX, _wantY;
    private bool _wantValid;
    private readonly List<double> _useRatios = new();

    /// <summary>
    /// 从**真实使用**中学习增益 —— 我们本来就要知道光标实际走了多少，
    /// 顺手对一下"上一帧发出去的位移"与"光标实际位移"，就得到增益，**不需要额外动一下光标**。
    ///
    /// 这也是唯一能满足"绝不移动到固定位置"的做法：进鼠标模式时光标纹丝不动，
    /// 移动全都是 SendInput 的**相对位移**（MOUSEEVENTF_MOVE），起点永远是光标此刻在哪。
    ///
    /// 判据细节：
    /// · 样本必须**成对间隔一帧**（帧首发位移 → 下一帧首读结果），否则读到的是没生效的旧位置；
    /// · 靠近屏幕边缘且朝外移动时位移会被系统裁掉 → 这种样本直接作废；
    /// · 用户同时还在推自己的鼠标会污染样本 → 取 40 个样本的中位数，野值自然被压掉。
    /// </summary>
    private void CalibrateFromUse()
    {
        var (nx, ny) = MouseInjector.CursorPos();
        if (_wantValid && _lastCursor is { } last)
        {
            var want = Math.Abs(_wantX) + Math.Abs(_wantY);
            var got = Math.Abs(nx - last.X) + Math.Abs(ny - last.Y);
            // ★ 这一句**每帧**都跑（60 Hz）：改用带缓存的版本，
            //   否则光判断"光标是否贴边"就每秒 4×60 = 240 次系统调用。
            var (vx, vy, vw, vh) = MouseInjector.VirtualScreenCached();
            var clamped =
                (nx <= vx && _wantX < 0) || (nx >= vx + vw - 1 && _wantX > 0) ||
                (ny <= vy && _wantY < 0) || (ny >= vy + vh - 1 && _wantY > 0);
            if (!clamped && want > 0 && got > 0 && got < want * 4)
            {
                _useRatios.Add((double)got / want);
                if (_useRatios.Count >= 40)
                {
                    _useRatios.Sort();
                    var med = _useRatios[_useRatios.Count / 2];
                    _useRatios.Clear();
                    if (Math.Abs(med - _gain) > Math.Max(_gain, 1) * 0.05)
                    {
                        var old = _gain;
                        _gain = Math.Clamp(med, 0.2, 5.0);
                        Log($"被动标定：增益 {old:F2} → {_gain:F2}（用你自己的移动学的，全程没动光标）");
                    }
                }
            }
        }
        _lastCursor = (nx, ny);
        _wantValid = false;      // 这一帧还没发位移，下一帧首别拿它对账
    }

    /// <summary>位移断了（松手 / 复位）→ 对账链断掉，下一帧重新起算</summary>
    private void ResetUseCalibration()
    {
        _lastCursor = null;
        _wantValid = false;
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
            wheelOnVolumeKeys = _cfg.WheelOnVolumeKeys,
            wheelMinPerSec = _cfg.WheelMinPerSec,
            wheelMaxPerSec = _cfg.WheelMaxPerSec,
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
        if (!LogSwitch.Enabled) return;
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
    ///   ① 开引擎**一次移动注入都不发**（起点必须是"光标此刻在哪"，不许跳到屏幕中心之类的固定点）
    ///   ② 不开引擎时按住方向键，光标不动（路由没消费）
    ///   ③ 开引擎后按住 right 1.2 s，光标向右移动，且**后半段比前半段快**（自适应增速生效）
    ///   ④ 松开后**一次注入都不再发**（不漂移）
    ///   ⑤ 确认键 → 系统左键真的按下/抬起（`GetAsyncKeyState(VK_LBUTTON)`）
    ///   ⑥ 斜向不超速（同时按 right+down，合速度与单方向同量级）
    ///   ⑦ 收尾后没有卡住的按键
    /// </summary>
    public static string SelfTest()
    {
        var sb = new StringBuilder();
        var e = new MouseEngine();
        bool LeftSys() => (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;

        sb.AppendLine("IPRC1000 鼠标引擎自检");
        sb.AppendLine(new string('=', 58));
        sb.AppendLine($"活动配置组=「{Keymaps.Current.Name}」（Kind={Keymaps.Current.Kind}）"
                      + $" ⇒ 鼠标模式={Keymaps.MouseGroupActive}（引擎={e.Enabled}）"
                      + $" · 音量键当滚轮={(e.Cfg.WheelOnVolumeKeys ? "开" : "关")}");
        sb.AppendLine("跑这个自检会真的驱动光标，期间请**不要碰鼠标/触控板**：");
        sb.AppendLine("  · 「该不该动」的断言看的是**注入计数**（我方账本），人手干扰不影响；");
        sb.AppendLine("  · 「动了多少」的断言只能靠量真实光标，人手一动就被污染 → 会自动重试 3 次取最好的一次。");
        sb.AppendLine();
        var ok = true;
        void Check(bool pass, string what, string detail)
        {
            ok &= pass;
            sb.AppendLine($"  {(pass ? "✓" : "✗")} {what}");
            sb.AppendLine($"       {detail}");
        }

        /// <summary>
        /// 给"向右 / 向下"留出足够的活动空间 —— **只用相对位移**，绝不做绝对定位。
        /// （自检也不该把用户的光标搬到屏幕中心：多显示器下那儿是两块屏的接缝。）
        /// </summary>
        void EnsureRoom()
        {
            var (x, y) = MouseInjector.CursorPos();
            var (vx, vy, vw, vh) = MouseInjector.VirtualScreen();
            var roomRight = vx + vw - 1 - x;
            if (roomRight < 900) MouseInjector.Move(-(900 - roomRight + 200), 0);
            var roomDown = vy + vh - 1 - y;
            if (roomDown < 320) MouseInjector.Move(0, -(320 - roomDown + 120));
            if (y - vy < 320) MouseInjector.Move(0, 320 - (y - vy) + 120);
            MouseInjector.WaitCursorSettled();
        }

        /// <summary>按住某个方向集合 ms 毫秒，返回这段时间的位移（基准就地取，不归位）</summary>
        (double dist, double dx, double dy) Hold(string[] dirs, int ms)
        {
            EnsureRoom();
            var (x0, y0) = MouseInjector.CursorPos();
            foreach (var d in dirs) e.HandleButton(d, true);
            Thread.Sleep(ms);
            foreach (var d in dirs) e.HandleButton(d, false);
            Thread.Sleep(120);                       // 让引擎真的停下来再取终值
            var (x1, y1) = MouseInjector.CursorPos();
            return (Math.Sqrt(Math.Pow(x1 - x0, 2) + Math.Pow(y1 - y0, 2)), x1 - x0, y1 - y0);
        }

        /// <summary>同一段测量最多跑 3 次，取"位移最大"的那次（人手同时动鼠标会污染样本）</summary>
        T BestOf<T>(Func<T> f, Func<T, double> score, int tries = 3)
        {
            var best = f();
            var bs = score(best);
            for (var i = 1; i < tries && bs <= 0; i++)
            {
                var t = f();
                var s = score(t);
                if (s > bs) { best = t; bs = s; }
            }
            return best;
        }

        try
        {
            // ① 开启鼠标模式**不能发出任何移动注入** —— 起点永远是"光标此刻在哪"。
            //    曾经在 SetEnabled(true) 里做主动自校准（绝对定位到屏幕中心）→ 进模式那一刻光标猛跳。
            //    判据用注入计数（我方账本）而不是光标位移：人手同时在动鼠标也判得准。
            e.SetEnabled(false);
            Thread.Sleep(120);
            var (bx, by) = MouseInjector.CursorPos();
            var mv0 = MouseInjector.MoveEvents;
            e.SetEnabled(true);
            Thread.Sleep(250);
            var mvA = MouseInjector.MoveEvents;
            var (ax, ay) = MouseInjector.CursorPos();
            var jump = Math.Abs(ax - bx) + Math.Abs(ay - by);
            Check(mvA == mv0, "开启鼠标模式不发出任何移动注入（不会跳到固定点）",
                  $"开启期间新增移动注入 {mvA - mv0} 次（期望 0）；"
                  + $"光标 ({bx},{by}) → ({ax},{ay}) 位移 {jump} px"
                  + (jump > 2 && mvA == mv0 ? "（★ 这次位移不是本引擎干的 —— 期间有人动过鼠标）" : ""));
            e.SetEnabled(false);
            Thread.Sleep(120);

            // ② 未启用 → 不消费
            var consumed = e.HandleButton("right", true);
            Check(!consumed, "未开鼠标模式时方向键不被引擎消费", $"HandleButton(right) 返回 {consumed}（期望 false）");

            // ③ 启用后按住 right：分两段量，看增速
            e.SetEnabled(true);
            (int first, int second, int total) Ramp()
            {
                EnsureRoom();
                var (x0, _) = MouseInjector.CursorPos();
                e.HandleButton("right", true);
                Thread.Sleep(600);
                var (xm, _) = MouseInjector.CursorPos();
                Thread.Sleep(600);
                var (x2, _) = MouseInjector.CursorPos();
                e.HandleButton("right", false);
                Thread.Sleep(120);
                return (xm - x0, x2 - xm, x2 - x0);
            }
            var run = BestOf(Ramp, r => r.first > 0 && r.second > 0 ? r.total : 0);
            Check(run.first > 0 && run.second > 0, "按住 right 光标向右移动",
                  $"前 0.6s 走 {run.first} px，后 0.6s 走 {run.second} px（合计 {run.total} px）");
            Check(run.second > run.first, "自适应增速生效（后半段更快）",
                  $"前半 {run.first} px < 后半 {run.second} px（比值 {(double)run.second / Math.Max(run.first, 1):F2}）");

            // ④ 松开后引擎必须**一次都不再注入**（判据同样是账本，不受人手干扰）
            var mvB = MouseInjector.MoveEvents;
            var (x3, _) = MouseInjector.CursorPos();
            Thread.Sleep(400);
            var mvC = MouseInjector.MoveEvents;
            var (x4, _) = MouseInjector.CursorPos();
            Check(mvC == mvB, "松开后引擎立即停止注入（不再漂移）",
                  $"松开后 0.4 s 内新增移动注入 {mvC - mvB} 次（期望 0）"
                  + $"；光标 {(Math.Abs(x4 - x3) <= 2 ? "稳定" : $"又动了 {x4 - x3} px（★ 期间有人动过鼠标）")}");

            // ⑤ 确认键 → 左键
            e.HandleButton("ok", true);
            Thread.Sleep(80);
            var down = LeftSys();
            e.HandleButton("ok", false);
            Thread.Sleep(80);
            var up = LeftSys();
            Check(down && !up, "确认键 → 系统左键按下/抬起", $"按下时系统左键={down}，抬起后={up}");

            // ⑥ 斜向归一化：**同样时长**下比较单方向与斜向的位移
            //   （时长必须一样，否则斜坡进度不同，比出来没意义 —— 第一版就是这么比错的）
            const int T = 800;
            var single = Hold(new[] { "right" }, T);
            var diag = Hold(new[] { "right", "down" }, T);
            Check(diag.dist > 0 && diag.dist <= single.dist * 1.15,
                  "斜向不超速（已归一化）",
                  $"{T}ms 单方向 {single.dist:F0} px vs 斜向 {diag.dist:F0} px"
                  + $"（比值 {diag.dist / Math.Max(single.dist, 1):F2}，归一化后应 ≈1.00）");

            // ⑦ 全部抬起
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
