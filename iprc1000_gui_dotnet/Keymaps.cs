using System.Text.Json;
using System.Text.Json.Nodes;

namespace Iprc1000Adapter;

/// <summary>
/// 按键映射的**多组配置**存储。
///
/// 形态：`%APPDATA%\IPRC1000 Adapter\keymap.json`
/// <code>
/// {
///   "active": 0,
///   "groups": [
///     { "name": "默认配置组 1", "map": { "up": [38], "voice": [165], ... } },
///     { "name": "Codex 编程",   "map": { ... } }
///   ]
/// }
/// </code>
///
/// 兼容旧版：老的扁平 `keys.json`（直接是 id→vks）会在首次读取时**迁移成一组**。
/// 另有**方块键（square）固定用于切换配置组**，不参与映射（见 <see cref="Backend"/>）。
/// </summary>
internal static class Keymaps
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Env.AppName);

    private static readonly string File_ = Path.Combine(Dir, "keymap.json");
    private static readonly string LegacyFile = Path.Combine(Dir, "keys.json");

    public sealed class Group
    {
        public string Name { get; set; } = "配置组";

        /// <summary>
        /// 该组对应的**出厂映射**，决定「恢复默认」按钮恢复成什么：
        ///   "plain"     → <see cref="Defaults.Plain"/>     （Windows 日常）
        ///   "workbuddy" → <see cref="Defaults.WorkBuddy"/> （WorkBuddy 快捷键）
        ///   "mouse"     → <see cref="Defaults.Mouse"/>     （★ 鼠标模式：**这就是"模式"本身**）
        ///   "custom"    → 同 plain（用户自己复制/新建的组，没有专属出厂映射）
        ///
        /// ★★ `"mouse"` 是 v1.2.4 引入的关键一项：**鼠标模式不再是一个独立的开关，
        ///    而是"活动配置组是不是鼠标组"**。理由：模式与配置组各存一份状态时，
        ///    会出现"在鼠标模式组里，但模式还是键盘"这类谁都说不清的组合。
        ///    现在只有**一个**状态来源（活动组的 Kind），方块键切过去就自动开、
        ///    切走就自动关。
        ///
        /// 没有这个字段之前，`ResetCurrent()` 一律恢复成 Plain() ——
        /// 于是用户在 WorkBuddy 组点「恢复默认」会拿到 Windows 日常映射，明显不合预期。
        /// </summary>
        public string Kind { get; set; } = "plain";

        public Dictionary<string, int[]> Map { get; set; } = new();
    }

    /// <summary>
    /// 存档版本。每加一段迁移就 +1。
    ///   9 = **出厂映射定稿**：Defaults 已按用户逐键实测确认后的最终键位冻结。
///  10 = **鼠标模式变成一个配置组**（Kind="mouse"）；方块键从"硬编码的特殊键"
///       改为**普通可绑定键**，出厂值 = 动作值 `切换配置组`（`KeyAct.SwitchGroup`）。
    /// </summary>
    private const int Ver_ = 11;

    private sealed class Store
    {
        public int Ver { get; set; } = 1;
        public int Active { get; set; }
        public List<Group> Groups { get; set; } = new();

        /// <summary>
        /// **全局按键**（2026-10-30）：标记在这里的键，在所有配置组里用**同一份映射**，
        /// 不随配置组切换而变化。
        ///
        /// 为什么需要：语音键本质上是"输入法的属性"（豆包=右Alt、微信=Ctrl+Win），
        /// 它跟"当前在用哪套桌面键位"没有任何关系。原来每个配置组各存一份，
        /// 于是用户切到鼠标模式后语音键又变回右 Alt —— 表现为"输入法切换没生效"。
        ///
        /// 存放位置选在 **Store 顶层**而不是每个 Group 里，是因为"全局"的定义就是
        /// 不隶属于任何组；放组里就还得有个"哪一组的值才算数"的规则，又多一个歧义源。
        /// </summary>
        public Dictionary<string, int[]> Global { get; set; } = new();
    }

    private static Store _s = Load();

    /// <summary>
    /// 全局锁（2026-10-20 审查发现）：`_s` 由 **UI 线程** 改（Set/切组/增删组），
    /// 而 **ETW 按键线程**同时在 `Get()` 里读 `Dictionary` —— Dictionary 非线程安全，
    /// 并发"读 + 新增键"可能抛异常或读到半截状态，而且这条路径在按键热路径上。
    /// 加一把锁：热路径上无争用时开销约 20ns，可以忽略。
    /// </summary>
    private static readonly object Gate = new();

    // ─────────── 读写 ───────────
    private static Store Load()
    {
        try
        {
            if (System.IO.File.Exists(File_))
            {
                var s = JsonSerializer.Deserialize<Store>(System.IO.File.ReadAllText(File_));
                if (s is { Groups.Count: > 0 })
                {
                    if (s.Active < 0 || s.Active >= s.Groups.Count) s.Active = 0;
                    // 老文件没有这个字段（或显式写成 null）→ 归一化成空字典，
                    // 后面的 Get/Set 就能无条件用，不必每处判空
                    s.Global ??= new Dictionary<string, int[]>();
                    // 迁移：把旧的「Codex 编程」组升级成「WorkBuddy 快捷键」映射
                    if (s.Ver < 2)
                    {
                        foreach (var g in s.Groups)
                        {
                            if (g.Name.StartsWith("Codex")) { g.Name = "WorkBuddy 快捷键"; g.Map = Defaults.WorkBuddy(); }
                        }
                        s.Ver = 2;
                        Save(s);
                    }
                    if (s.Ver < 3)
                    {
                        // 用户指定：第一组的 back→Backspace、menu→Win+X、◀◀→Win+D、▶▶→Alt+Esc
                        // **只覆盖这 4 个键**，不碰该组里其它自定义
                        var g1 = s.Groups[0].Map;
                        g1["back"] = new[] { 0x08 };
                        g1["menu"] = new[] { 0x5B, 0x58 };
                        g1["fast_forward"] = new[] { 0x12, 0x1B };
                        g1["rewind"] = new[] { 0x5B, 0x44 };
                        s.Ver = 3;
                        Save(s);
                    }
                    if (s.Ver < 4)
                    {
                        // 空位补全：pwr→Win+L 锁屏、♡→Win+Shift+S 截图、👤→Win+E 资源管理器
                        var g1 = s.Groups[0].Map;
                        g1["power"] = new[] { 0x5B, 0x4C };
                        g1["favorite"] = new[] { 0x5B, 0x10, 0x53 };
                        g1["account"] = new[] { 0x5B, 0x45 };
                        s.Ver = 4;
                        Save(s);
                    }
                    if (s.Ver < 5)
                    {
                        // WorkBuddy 组：频道键改成 Ctrl+[ / Ctrl+]（上一个/下一个任务）
                        var g2 = s.Groups.Count > 1 ? s.Groups[1].Map : new Dictionary<string, int[]>();
                        if (g2.Count > 0)
                        {
                            g2["channel_up"] = new[] { 0x11, 0xDB };
                            g2["channel_down"] = new[] { 0x11, 0xDD };
                            // 任务切换交给频道键之后，方向键回归箭头，避免两者重复
                            g2["up"] = new[] { 0x26 };
                            g2["down"] = new[] { 0x28 };
                        }
                        s.Ver = 5;
                        Save(s);
                    }
                    if (s.Ver < 6)
                    {
                        // WorkBuddy 组：info 由 Enter 改为「/ 唤起斜杠命令」，避免与 ok(Enter) 重复
                        if (s.Groups.Count > 1) s.Groups[1].Map["info"] = new[] { 0xBF };
                        s.Ver = 6;
                        Save(s);
                    }
                    if (s.Ver < 7)
                    {
                        // WorkBuddy 组：▲▼◀▶ 四个方向键统一保持方向键（不做字号）
                        if (s.Groups.Count > 1)
                        {
                            s.Groups[1].Map["left"] = new[] { 0x25 };
                            s.Groups[1].Map["right"] = new[] { 0x27 };
                        }
                        s.Ver = 7;
                        Save(s);
                    }
                    if (s.Ver < 8)
                    {
                        // 用户反馈：info 的映射（Alt+Enter / Ctrl+Enter / 斜杠）都无感 → 两组的 info 一律清空。
                        // 同时修复「menu 与 info 撞成同一个组合键」的历史遗留：两者相同时把 menu 还原成 Win+X。
                        foreach (var g in s.Groups)
                        {
                            g.Map["info"] = Array.Empty<int>();
                            if (g.Map.TryGetValue("menu", out var mv) && mv.Length > 0 &&
                                mv.SequenceEqual(new[] { 0x11, 0x0D }))
                                g.Map["menu"] = new[] { 0x5B, 0x58 };
                        }
                        s.Ver = 8;
                        Save(s);
                    }
                    if (s.Ver < 9)
                    {
                        // Ver 9：出厂映射定稿（Defaults 冻结为实测确认的最终键位）+ 记录每组的 Kind。
                        // ★ 刻意**不动任何按键** —— 新默认与用户当前配置本就逐键一致，
                        //   若强写一遍反而会覆盖其它机器上的个性化设置。
                        //   冻结默认值只影响新装用户与「恢复默认」，存量映射原样保留。
                        foreach (var g in s.Groups)
                        {
                            if (g.Kind != "workbuddy" &&
                                g.Name.Contains("WorkBuddy", StringComparison.OrdinalIgnoreCase))
                                g.Kind = "workbuddy";
                        }
                        s.Ver = 9;
                        Save(s);
                    }
                    if (s.Ver < 10)
                    {
                        // Ver 10：鼠标模式变成一个配置组。
                        //  ① 补一个 Kind="mouse" 的「鼠标模式」组（放在第一个普通组之后，
                        //     让方块键"切下一组"的顺序是 默认 → 鼠标模式 → WorkBuddy）；
                        //  ② 方块键从"固定用于切组、不注入"改为**普通可绑定键**，
                        //     出厂值 = 动作值「切换配置组」。
                        //     ★ 只在该键**当前为空**时才写 —— 用户若已手动绑过别的，不覆盖。
                        if (!s.Groups.Any(g => g.Kind == "mouse"))
                        {
                            var at = s.Groups.FindIndex(g => g.Kind == "plain");
                            s.Groups.Insert(at < 0 ? s.Groups.Count : at + 1,
                                new Group { Name = MouseGroupName, Kind = "mouse", Map = Defaults.Mouse() });
                        }
                        foreach (var g in s.Groups)
                        {
                            if (!g.Map.TryGetValue("square", out var sq) || sq.Length == 0)
                                g.Map["square"] = new[] { KeyAct.SwitchGroup };
                        }
                        s.Ver = 10;
                        Save(s);
                    }

                    // ★ Ver 11（2026-10-30）：**语音键改为全局** ——
                    //   它本质是"输入法的属性"（豆包=右Alt / 微信=Ctrl+Win），跟当前用哪套
                    //   桌面键位无关。原来每组各存一份，切到鼠标模式就变回右 Alt，
                    //   用户看到的是"首页切了输入法却没生效"（实测就是这个 bug）。
                    //   取**活动组**的值当全局值：那才是用户当下有意的配置。
                    if (s.Ver < 11)
                    {
                        s.Global ??= new Dictionary<string, int[]>();
                        if (s.Groups.Count > 0)
                        {
                            var at = Math.Clamp(s.Active, 0, s.Groups.Count - 1);
                            if (s.Groups[at].Map.TryGetValue("voice", out var vv) && vv.Length > 0)
                                s.Global["voice"] = (int[])vv.Clone();
                        }
                        s.Ver = 11;
                        Save(s);
                    }
                    return s;
                }
            }
        }
        catch (Exception e)
        {
            Safe.Log(e, "读取 keymap.json 失败，本次已用出厂默认映射（文件保留，未覆盖）");
        }

        // 首次运行（或旧版迁移）：默认配置组 1 + Codex 编程
        var store = new Store
        {
            Ver = Ver_,
            Active = 0,
            Groups = new List<Group>
            {
                new() { Name = "默认配置组 1", Kind = "plain", Map = Defaults.Plain() },
                new() { Name = MouseGroupName, Kind = "mouse", Map = Defaults.Mouse() },
                new() { Name = "WorkBuddy 快捷键", Kind = "workbuddy", Map = Defaults.WorkBuddy() },
            },
        };

        // 旧版扁平 keys.json → 覆盖到第一组
        try
        {
            if (System.IO.File.Exists(LegacyFile))
            {
                var old = JsonSerializer.Deserialize<Dictionary<string, int[]>>(
                    System.IO.File.ReadAllText(LegacyFile));
                if (old is { Count: > 0 })
                {
                    foreach (var kv in old) store.Groups[0].Map[kv.Key] = kv.Value;
                    store.Groups[0].Name = "默认配置组 1（已迁移）";
                }
            }
        }
        catch { }

        return store;
    }

    private static void Save() => Save(_s);

    private static void Save(Store s)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            // ★ 原子写（同 Settings）：半截文件会让所有按键映射丢失
            var json = JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true });
            var tmp = File_ + ".tmp";
            System.IO.File.WriteAllText(tmp, json);
            if (System.IO.File.Exists(File_)) System.IO.File.Replace(tmp, File_, File_ + ".bak", true);
            else System.IO.File.Move(tmp, File_);
        }
        catch { }
    }

    // ─────────── 查询 ───────────
    /// <summary>「鼠标模式」组的固定名字（它是功能的入口，改名会让用户找不到）</summary>
    public const string MouseGroupName = "鼠标模式";

    /// <summary>活动组是不是"鼠标组" —— **鼠标模式的唯一判据**</summary>
    public static bool MouseGroupActive => Current.Kind == "mouse";

    /// <summary>鼠标组的下标（没有则 -1）</summary>
    public static int MouseGroupIndex => _s.Groups.FindIndex(g => g.Kind == "mouse");

    public static int ActiveIndex => _s.Active;
    public static IReadOnlyList<Group> Groups => _s.Groups;
    /// <summary>
    /// 当前配置组。
    /// ★ 2026-10-20：原实现 `_s.Groups[_s.Active]` **没有边界检查** —— 一旦 `Active`
    ///   非法（手改 keymap.json / 删组竞态），**每次按键都会抛** ArgumentOutOfRange，
    ///   而调用点（Get/Set）没有兜底 ⇒ 按键全失效。现在夹取到合法范围。
    /// </summary>
    public static Group Current
    {
        get
        {
            lock (Gate)
            {
                var g = _s.Groups;
                if (g.Count == 0) return DefaultGroup();          // 理论上不会，但绝不返回 null
                var i = _s.Active;
                if (i < 0 || i >= g.Count) i = 0;
                return g[i];
            }
        }
    }

    /// <summary>
    /// 兜底组：配置组列表异常为空时使用（**绝不返回 null**，也绝不让热路径抛异常）。
    /// 只在 `Load` 失败/文件被清空时才可能走到。
    /// </summary>
    private static Group DefaultGroup() => new()
    {
        Name = "默认配置组 1",
        Kind = "plain",
        Map = Defaults.Plain()
    };

    /// <summary>取某个按键在当前配置组里的映射</summary>
    public static int[] Get(string id)
    {
        lock (Gate)
        {
            // ★ 全局优先：语音键这类"跟配置组无关"的键走这里
            if (_s.Global.TryGetValue(id, out var g)) return g;
            return Current.Map.TryGetValue(id, out var v) ? v : Array.Empty<int>();
        }
    }

    /// <summary>这个键是不是全局的（界面要显示标记）</summary>
    public static bool IsGlobal(string id)
    {
        lock (Gate) return _s.Global.ContainsKey(id);
    }

    /// <summary>当前的全局键列表（自检/界面用）</summary>
    public static string[] GlobalIds()
    {
        lock (Gate) return _s.Global.Keys.ToArray();
    }

    /// <summary>
    /// 把某个键设为全局 / 取消全局。
    /// 设为全局时**用当前组的值**作为全局值；取消时把它写回当前组（否则用户会看到"值突然没了"）。
    /// </summary>
    public static string SetGlobal(string id, bool on)
    {
        var name = Keys.Table.FirstOrDefault(x => x.Id == id)?.Name ?? id;
        lock (Gate)
        {
            if (on)
            {
                var cur = Current.Map.TryGetValue(id, out var v) ? v : Array.Empty<int>();
                _s.Global[id] = (int[])cur.Clone();
            }
            else
            {
                if (!_s.Global.TryGetValue(id, out var g)) return $"「{name}」本来就不是全局的";
                Current.Map[id] = g;              // 值落回当前组，用户不会看到它"消失"
                _s.Global.Remove(id);
            }
        }
        Save();
        return on ? $"「{name}」已设为全局：所有配置组共用，切换配置组不再变化"
                  : $"「{name}」已取消全局：只跟随当前配置组";
    }

    /// <summary>
    /// 取指定组的**实际生效**映射（已合并全局键）。
    ///
    /// ⚠ 别直接返回 `Groups[idx].Map` —— 全局键的真实值在 Store 顶层，
    ///   不合并的话 `--map-dump` 和孤儿键回收看到的都是**组里的旧值**，
    ///   于是 dump 显示 RAlt、实际注入却是 Ctrl+Win（这种"仪表撒谎"比没有仪表更糟）。
    /// </summary>
    public static Dictionary<string, int[]> MapOf(int idx)
    {
        var g = idx >= 0 && idx < _s.Groups.Count ? _s.Groups[idx].Map : Current.Map;
        if (_s.Global.Count == 0) return g;
        var merged = new Dictionary<string, int[]>(g);
        foreach (var kv in _s.Global) merged[kv.Key] = kv.Value;
        return merged;
    }

    /// <summary>设置某个按键的映射（空数组 = 不执行）。全局键写进 Global，其余写当前组</summary>
    public static void Set(string id, int[] vks)
    {
        lock (Gate)
        {
            // ★ 全局键写到 Global —— 于是「首页切换输入法」这类入口一行都不用改：
            //   它调的仍是 Set("voice", ...)，效果自然覆盖所有配置组。
            if (_s.Global.ContainsKey(id)) _s.Global[id] = vks;
            else Current.Map[id] = vks;
        }
        Save();     // ★ 写盘放在锁外：磁盘写不该阻塞按键热路径
    }

    // ─────────── 配置组操作 ───────────
    /// <summary>切到下一组（**方块键固定做这个**），循环</summary>
    public static string NextGroup()
    {
        // ★ 2026-10-20：`% _s.Groups.Count` 在 Count==0 时会除零。正常路径有默认组兜底，
        //   但既然 `Current` 已加兜底组，这里也一并守住（方块键是高频操作，不能抛）。
        lock (Gate)
        {
            if (_s.Groups.Count == 0) return "默认配置组 1";
            _s.Active = (_s.Active + 1) % _s.Groups.Count;
        }
        Save();
        return _s.Groups[_s.Active].Name;
    }

    public static string SwitchTo(int idx)
    {
        if (idx < 0 || idx >= _s.Groups.Count) return _s.Groups[_s.Active].Name;
        _s.Active = idx; Save();
        return _s.Groups[_s.Active].Name;
    }

    /// <summary>复制当前组并新建一组（自动编号）</summary>
    public static string Duplicate()
    {
        var src = _s.Groups[_s.Active];
        var name = $"配置组 {_s.Groups.Count + 1}";
        // 复制出来的组没有专属出厂映射 → Kind=custom（「恢复默认」时按 plain 处理）
        var g = new Group { Name = name, Kind = "custom", Map = new Dictionary<string, int[]>(src.Map) };
        _s.Groups.Insert(_s.Active + 1, g);
        _s.Active += 1;
        Save();
        return name;
    }

    public static string Rename(int idx, string name)
    {
        if (idx < 0 || idx >= _s.Groups.Count || string.IsNullOrWhiteSpace(name))
            return _s.Groups[_s.Active].Name;
        _s.Groups[idx].Name = name.Trim();
        Save();
        return _s.Groups[idx].Name;
    }

    /// <summary>删除一组；至少保留一组</summary>
    public static (bool ok, string msg) Delete(int idx)
    {
        if (_s.Groups.Count <= 1) return (false, "至少要保留一组配置。");
        if (idx < 0 || idx >= _s.Groups.Count) return (false, "配置组不存在。");
        // ★ 鼠标组是"鼠标模式"这个功能的入口 —— 删了它就再也进不去鼠标模式了。
        //   与其留个死状态，不如直接挡住（要恢复用「恢复默认」）。
        if (_s.Groups[idx].Kind == "mouse")
            return (false, $"「{MouseGroupName}」是鼠标模式的入口，不能删除。");
        _s.Groups.RemoveAt(idx);
        if (_s.Active >= _s.Groups.Count) _s.Active = _s.Groups.Count - 1;
        Save();
        return (true, "已删除该配置组。");
    }

    /// <summary>某一组对应的出厂映射（由 <see cref="Group.Kind"/> 决定）</summary>
    public static Dictionary<string, int[]> FactoryOf(Group g)
        => g.Kind == "workbuddy" ? Defaults.WorkBuddy()
         : g.Kind == "mouse" ? Defaults.Mouse()
         : Defaults.Plain();

    /// <summary>指定组的出厂映射（自检用）</summary>
    public static Dictionary<string, int[]> FactoryOf(int idx)
        => FactoryOf(idx >= 0 && idx < _s.Groups.Count ? _s.Groups[idx] : Current);

    public static string ResetCurrent()
    {
        var g = _s.Groups[_s.Active];
        g.Map = FactoryOf(g);          // ★ 按该组自己的出厂映射恢复，而不是一律 Plain()
        Save();
        return g.Name;
    }

    // ─────────── 导入导出（只作用于当前组）───────────
    public static string Export()
        => JsonSerializer.Serialize(new { name = Current.Name, map = Current.Map },
            new JsonSerializerOptions { WriteIndented = true });

    /// <summary>按键 id → 中文名（提示与日志用；找不到就原样返回 id）</summary>
    private static string NameOfKey(string id)
        => Keys.Table.FirstOrDefault(x => x.Id == id)?.Name ?? id;

    public static (bool ok, string msg) Import(string json)
    {
        try
        {
            var n = JsonNode.Parse(json) as JsonObject;
            if (n == null) return (false, "不是合法的 JSON。");
            var map = n["map"]?.AsObject();
            if (map == null) return (false, "缺少 map 字段。");
            var d = new Dictionary<string, int[]>();
            var skipped = new List<string>();
            foreach (var kv in map)
            {
                // ★ 全局键的值**不能**写进组里：读的时候 `Get` 优先查 Global，
                //   写进去也白写 —— 但用户会看到"导入成功了，值却没变"，
                //   然后反复重试。明确跳过并告知，比静默吞掉好。
                if (_s.Global.ContainsKey(kv.Key)) { skipped.Add(NameOfKey(kv.Key)); continue; }

                var arr = kv.Value?.AsArray();
                if (arr == null) continue;
                d[kv.Key] = arr.Select(x => (int)(x?.GetValue<int>() ?? 0)).Where(x => x > 0).ToArray();
            }
            if (d.Count == 0 && skipped.Count == 0) return (false, "map 里没有有效按键。");

            // 全部被跳过时**不要**清空当前组 —— 否则导入一个"只有全局键"的文件会把用户配置抹掉
            if (d.Count > 0)
            {
                _s.Groups[_s.Active].Map = d;
                _s.Groups[_s.Active].Kind = "custom";   // 导入进来的不是出厂默认了
                if (n["name"]?.GetValue<string>() is { Length: > 0 } nm)
                    _s.Groups[_s.Active].Name = nm;
            }
            Save();

            var tail = skipped.Count > 0
                ? $"；{string.Join("、", skipped)} 是**全局键**，本次未应用 —— 它们不随配置组变化，请在按键弹窗里单独改"
                : "";
            return (true, $"已导入 {d.Count} 个按键映射到当前配置组「{_s.Groups[_s.Active].Name}」{tail}。");
        }
        catch (Exception e) { return (false, "导入失败：" + e.Message); }
    }
}

/// <summary>
/// 两套**出厂默认**映射 —— 这是默认值的**唯一权威来源**（<see cref="Keys.Table"/> 不再存默认值）。
///
/// ★ 冻结版本（Ver 9）：以下键位是用户逐键实测确认后的**最终定稿**，
///   与 `%APPDATA%\IPRC1000 Adapter\keymap.json` 里当时的内容逐键一致。
///   改动前请先确认这是有意的，不要顺手"优化"。
///
/// ★★ 2026-10-30 二次定稿（v1.2.138）：按真机上正在使用的配置组重新冻结，
///   与用户当时的 `keymap.json`（Active=0「默认配置组 1」/第 1 组「鼠标模式」）逐键一致。
///   相对 Ver 9 的 6 处变化：
///     power         Win+L   → Ctrl+F4             （锁屏用不上，关标签更常用）
///     account       Win+E   → 截图点击（-6）        （Win+E 与 back 的资源管理器语义冲突）
///     channel_up    PageUp  → Ctrl+[              （跟 WorkBuddy 组的"上一个任务"对齐）
///     channel_down  PgDn    → Ctrl+]              （同上）
///     last          Alt+Tab → Ctrl+Alt+Tab        （粘滞式，松手不消失，选窗口更稳）
///     mouse.exit    Esc     → 鼠标左键（-1）       （鼠标模式没有窗口要退，左键是刚需）
///   ⚠ 只影响**新建/恢复默认**；已存在的 keymap.json **不会被覆盖**（不升 Ver、不触发迁移）。
/// </summary>
internal static class Defaults
{
    // 虚拟键码速查：Ctrl=0x11 Alt=0x12 Shift=0x10 Win=0x5B
    //               ←=0x25 ↑=0x26 →=0x27 ↓=0x28 Enter=0x0D Esc=0x1B Space=0x20 Tab=0x09
    //               菜单键=0x5D  PgUp=0x21 PgDn=0x22  Backspace=0x08
    //               [ =0xDB  ] =0xDD
    //               媒体：下一曲=0xB0 上一曲=0xB1 播放暂停=0xB3 静音=0xAD 音量-=0xAE 音量+=0xAF
    //               右 Alt=0xA5（豆包长按说话）

    /// <summary>第一组：默认配置组 1（Windows 日常）</summary>
    public static Dictionary<string, int[]> Plain() => new()
    {
        // 遥控器按键        注入的键
        ["power"] = new[] { 0x11, 0x73 },              // Ctrl+F4  关闭当前标签/文档
        ["back"] = new[] { 0x08 },                     // Backspace（浏览器/资源管理器=后退）
        ["menu"] = new[] { 0x5D },                     // 菜单键（右键菜单，多数程序都认）
        ["exit"] = new[] { 0x1B },                     // Esc
        ["info"] = new[] { 0x11, 0x0D },               // Ctrl+Enter
        ["ok"] = new[] { 0x0D },                       // enter 确认
        ["up"] = new[] { 0x26 },
        ["down"] = new[] { 0x28 },
        ["left"] = new[] { 0x25 },
        ["right"] = new[] { 0x27 },
        ["favorite"] = new[] { 0x5B, 0x10, 0x53 },     // Win+Shift+S  区域截图
        ["voice"] = new[] { 0xA5 },                    // ★ 右 Alt —— 豆包「长按模式」说话
        ["account"] = new[] { KeyAct.ClickIcon },      // 动作值：截图点击（找采集过的图标并单击）
        ["play_pause"] = new[] { 0xB3 },               // 播放/暂停
        ["fast_forward"] = new[] { 0x12, 0x1B },       // Alt+Esc  按任务栏顺序切换窗口
        ["rewind"] = new[] { 0x5B, 0x44 },             // Win+D  显示桌面
        ["channel_up"] = new[] { 0x11, 0xDB },         // Ctrl+[  上一个标签/任务
        ["channel_down"] = new[] { 0x11, 0xDD },       // Ctrl+]  下一个标签/任务
        ["volume_up"] = new[] { 0xAF },                // 系统音量 +
        ["volume_down"] = new[] { 0xAE },              // 系统音量 -
        ["mute"] = new[] { 0xAD },                     // 系统静音
        ["last"] = new[] { 0x12, 0x11, 0x09 },         // Ctrl+Alt+Tab  粘滞切换窗口（松手不消失）
        // ★ 方块键不再是"硬编码的特殊键"：它是一个**普通可绑定键**，
        //   出厂值 = 动作值「切换配置组」（负数哨兵，见 KeyAct）。用户可以改绑成任何键。
        ["square"] = new[] { KeyAct.SwitchGroup },
    };

    /// <summary>
    /// **鼠标模式组**（Kind = "mouse"）—— 这一组**就是鼠标模式本身**。
    ///
    /// 默认映射 = <see cref="Plain"/> 原样，只做两处覆盖：
    ///   ① 四个方向键**清空** —— 由 <see cref="MouseEngine"/> 接管（按住加速移动），走不了普通映射；
    ///   ② `exit` 改成**鼠标左键** —— 鼠标模式下没有窗口焦点要退，左键才是刚需。
    /// 其余按键（含确认、info、语音…）**全部沿用默认组**，
    /// 用户可以随时在弹窗里改成任何东西（比如把确认改成「鼠标左键」就能恢复
    /// "按住方向 + 按住确认 = 拖拽"）。
    /// </summary>
    public static Dictionary<string, int[]> Mouse()
    {
        var m = Plain();
        foreach (var d in new[] { "up", "down", "left", "right" })
            m[d] = Array.Empty<int>();       // 交给引擎，不注入按键

        // ★ 2026-10-30 按真机实测配置冻结：鼠标模式下 Esc 没有意义（没有窗口焦点要退），
        //   改成**鼠标左键**才是刚需 —— 配合方向键移动就能完成"移动→点击"，
        //   不需要再回到默认组。这一处是**唯一**偏离 Plain 的键（方向键除外）。
        m["exit"] = new[] { KeyAct.Left };
        return m;
    }

    /// <summary>
    /// 第二组：**WorkBuddy 快捷键**（照 WorkBuddy 的快捷键表映射；mac 的 Cmd 在 Windows 上对应 Ctrl）。
    ///
    /// 语音键（voice）**保持不动 = 右 Alt**，这是豆包「长按说话」的核心，
    /// 换配置组时也不应改变行为。
    /// </summary>
    public static Dictionary<string, int[]> WorkBuddy() => new()
    {
        //  遥控器键        注入                      WorkBuddy 动作
        ["power"]        = new[] { 0x11, 0x10, 0x57 },   // Ctrl+Shift+W  关闭当前页
        ["back"]         = new[] { 0x11, 0x50 },         // Ctrl+P        打开文件搜索
        ["menu"]         = new[] { 0x11, 0x4F },         // Ctrl+O        全局搜索
        ["exit"]         = new[] { 0x1B },               // Esc           停止生成
        ["info"]         = Array.Empty<int>(),           // 不映射
        ["ok"]           = new[] { 0x0D },               // enter         发送消息
        ["up"]           = new[] { 0x26 },               // ↑             光标上移
        ["down"]         = new[] { 0x28 },               // ↓             光标下移
        ["left"]         = new[] { 0x25 },               // ←             光标左移
        ["right"]        = new[] { 0x27 },               // →             光标右移
        ["favorite"]     = new[] { 0x11, 0x49 },         // Ctrl+I        打开计划面板
        ["voice"]        = new[] { 0xA5 },               // ★ 右 Alt —— 语音输入，两组都**不动**
        ["account"]      = new[] { 0x11, 0x52 },         // Ctrl+R        聚焦区域
        ["play_pause"]   = new[] { 0x7B },               // F12           进入终端会话
        ["fast_forward"] = new[] { 0x11, 0x42 },         // Ctrl+B        切换到面板
        ["rewind"]       = new[] { 0x11, 0x10, 0x42 },   // Ctrl+Shift+B  切换右面板
        ["channel_up"]   = new[] { 0x11, 0xDB },         // Ctrl+[        上一个任务
        ["channel_down"] = new[] { 0x11, 0xDD },         // Ctrl+]        下一个任务
        ["volume_up"]    = new[] { 0xAF },               // 系统音量 +
        ["volume_down"]  = new[] { 0xAE },               // 系统音量 -
        ["mute"]         = new[] { 0xAD },               // 系统静音
        ["last"]         = new[] { 0x10, 0x11, 0x57 },   // Shift+Ctrl+W  唤起快速输入窗口
        ["square"]       = new[] { KeyAct.SwitchGroup },  // 动作值：切换配置组
    };
}
