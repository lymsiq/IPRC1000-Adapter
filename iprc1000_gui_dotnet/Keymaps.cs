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
        public Dictionary<string, int[]> Map { get; set; } = new();
    }

    /// <summary>存档版本：<2 表示还是旧的「Codex 编程」组，需要升级成 WorkBuddy 映射</summary>
    private const int Ver_ = 8;

    private sealed class Store
    {
        public int Ver { get; set; } = 1;
        public int Active { get; set; }
        public List<Group> Groups { get; set; } = new();
    }

    private static Store _s = Load();

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
                    return s;
                }
            }
        }
        catch { /* 损坏就用默认 */ }

        // 首次运行（或旧版迁移）：默认配置组 1 + Codex 编程
        var store = new Store
        {
            Ver = Ver_,
            Active = 0,
            Groups = new List<Group>
            {
                new() { Name = "默认配置组 1", Map = Defaults.Plain() },
                new() { Name = "WorkBuddy 快捷键", Map = Defaults.WorkBuddy() },
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
            System.IO.File.WriteAllText(File_, JsonSerializer.Serialize(s,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    // ─────────── 查询 ───────────
    public static int ActiveIndex => _s.Active;
    public static IReadOnlyList<Group> Groups => _s.Groups;
    public static Group Current => _s.Groups[_s.Active];

    /// <summary>取某个按键在当前配置组里的映射</summary>
    public static int[] Get(string id)
        => Current.Map.TryGetValue(id, out var v) ? v : Array.Empty<int>();

    /// <summary>设置某个按键在当前配置组里的映射（空数组 = 不执行）</summary>
    public static void Set(string id, int[] vks)
    {
        if (vks.Length == 0) Current.Map[id] = Array.Empty<int>();
        else Current.Map[id] = vks;
        Save();
    }

    // ─────────── 配置组操作 ───────────
    /// <summary>切到下一组（**方块键固定做这个**），循环</summary>
    public static string NextGroup()
    {
        _s.Active = (_s.Active + 1) % _s.Groups.Count;
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
        var g = new Group { Name = name, Map = new Dictionary<string, int[]>(src.Map) };
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
        _s.Groups.RemoveAt(idx);
        if (_s.Active >= _s.Groups.Count) _s.Active = _s.Groups.Count - 1;
        Save();
        return (true, "已删除该配置组。");
    }

    public static string ResetCurrent()
    {
        _s.Groups[_s.Active].Map = Defaults.Plain();
        Save();
        return _s.Groups[_s.Active].Name;
    }

    // ─────────── 导入导出（只作用于当前组）───────────
    public static string Export()
        => JsonSerializer.Serialize(new { name = Current.Name, map = Current.Map },
            new JsonSerializerOptions { WriteIndented = true });

    public static (bool ok, string msg) Import(string json)
    {
        try
        {
            var n = JsonNode.Parse(json) as JsonObject;
            if (n == null) return (false, "不是合法的 JSON。");
            var map = n["map"]?.AsObject();
            if (map == null) return (false, "缺少 map 字段。");
            var d = new Dictionary<string, int[]>();
            foreach (var kv in map)
            {
                var arr = kv.Value?.AsArray();
                if (arr == null) continue;
                d[kv.Key] = arr.Select(x => (int)(x?.GetValue<int>() ?? 0)).Where(x => x > 0).ToArray();
            }
            if (d.Count == 0) return (false, "map 里没有有效按键。");
            _s.Groups[_s.Active].Map = d;
            if (n["name"]?.GetValue<string>() is { Length: > 0 } nm)
                _s.Groups[_s.Active].Name = nm;
            Save();
            return (true, $"已导入 {d.Count} 个按键映射到当前配置组「{_s.Groups[_s.Active].Name}」。");
        }
        catch (Exception e) { return (false, "导入失败：" + e.Message); }
    }
}

/// <summary>两套出厂默认映射（按规格给出，键码为 Windows 虚拟键码）</summary>
internal static class Defaults
{
    // 虚拟键码速查：Ctrl=0x11 Alt=0x12 Shift=0x10 Win=0x5B
    //               ←=0x25 ↑=0x26 →=0x27 ↓=0x28 Return=0x0D Esc=0x1B Space=0x20 Tab=0x09
    //               媒体：下一曲=0xB0 上一曲=0xB1 播放暂停=0xB3 静音=0xAD 音量-=0xAE 音量+=0xAF
    //               右 Alt=0xA5（豆包长按说话）

    /// <summary>默认配置组：日常使用（Windows 化后的等价动作）</summary>
    public static Dictionary<string, int[]> Plain() => new()
    {
        // 遥控器按键        注入的键
        ["power"] = new[] { 0x5B, 0x4C },              // Win+L  锁屏（走虚拟 HID，系统热键能正常触发）
        ["back"] = new[] { 0x08 },                     // Backspace（浏览器/资源管理器=后退）
        ["menu"] = new[] { 0x5B, 0x58 },               // Win+X  快捷菜单（开始按钮右键菜单）
        ["exit"] = new[] { 0x1B },                     // Esc
        ["info"] = Array.Empty<int>(),                 // ★ 默认不映射（原 Alt+Enter / Ctrl+Enter 在多数场景无感，交给用户自定）
        ["ok"] = new[] { 0x0D },                       // Enter
        ["up"] = new[] { 0x26 },
        ["down"] = new[] { 0x28 },
        ["left"] = new[] { 0x25 },
        ["right"] = new[] { 0x27 },
        ["favorite"] = new[] { 0x5B, 0x10, 0x53 },     // Win+Shift+S  区域截图
        ["voice"] = new[] { 0xA5 },                    // ★ 右 Alt —— 豆包「长按模式」说话
        ["account"] = new[] { 0x5B, 0x45 },            // Win+E  资源管理器
        ["play_pause"] = new[] { 0xB3 },
        ["fast_forward"] = new[] { 0x12, 0x1B },       // Alt+Esc  按任务栏顺序切换窗口
        ["rewind"] = new[] { 0x5B, 0x44 },             // Win+D  显示桌面
        ["channel_up"] = new[] { 0x21 },               // PageUp
        ["channel_down"] = new[] { 0x22 },             // PageDown
        ["volume_up"] = new[] { 0xAF },                // 系统音量 +
        ["volume_down"] = new[] { 0xAE },              // 系统音量 -
        ["mute"] = new[] { 0xAD },                     // 系统静音
        ["last"] = new[] { 0x12, 0x09 },               // Alt+Tab 切换窗口
        ["square"] = Array.Empty<int>(),               // 固定用于切换配置组，不注入
    };

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
        ["info"]         = Array.Empty<int>(),           // ★ 默认不映射（/ 与 Ctrl+Enter 在 WorkBuddy 里都无感）
        ["ok"]           = new[] { 0x0D },               // Enter         发送消息
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
        ["square"]       = Array.Empty<int>(),           // 固定用于切换配置组
    };
}
