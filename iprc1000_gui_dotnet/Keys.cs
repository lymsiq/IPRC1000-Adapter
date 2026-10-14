using System.Text.Json;

namespace Iprc1000Adapter;

/// <summary>按键表与映射存储（与 src-tauri/.../iprc1000/bridge.rs 的 BUTTON_TABLE 一致，已三源校验）。</summary>
internal static class Keys
{
    /// <summary>
/// 一个遥控器按键。
///   Id      — 内部标识
///   Label   — **遥控器上实际印的字符/图标**（界面按它显示，不用中文）
///   Name    — 中文说明（仅作悬停提示/无障碍用）
/// </summary>
public record Btn(string Id, string Label, string Name, string Hid, int[] Default);

    public static readonly Btn[] Table =
    {
        // ★ 推荐默认（依据 Windows Media Center 官方遥控器键位表 + 现代 Windows 习惯）：
        //   方向键→方向键 · 确认→Enter · 清除/退出→Esc · 返回→Alt+←（不比退格破坏文本）
        //   频道±→PageUp/PageDown（官方就是 PageUp/Down）· 音量/静音→系统媒体音量键
        //   播放/快进/快退→媒体键 · 收藏→Ctrl+D · 信息→（默认留空）· 上一频道→Alt+Tab
        //   菜单→菜单键 · 账户→Win · 方块键→F11(全屏) · 语音→右 Alt（豆包长按模式）
        new("power", "pwr", "电源", "0x17", Array.Empty<int>()),                 // 故意留空，避免误触关机
        new("back", "back", "返回", "0x09", new[] { 0x12, 0x25 }),                 // Alt+←
        new("menu", "menu", "菜单", "0x11", new[] { 0x5D }),                       // 菜单键（右键菜单）
        new("exit", "exit", "退出 / Home", "0x07", new[] { 0x1B }),                // Esc
        new("info", "info", "信息", "0x0b", Array.Empty<int>()),                 // 默认不映射（见 Defaults.Plain）
        new("ok", "ok", "确认", "0x28", new[] { 0x0D }),                         // Enter
        new("up", "▲", "上", "0x52", new[] { 0x26 }),
        new("down", "▼", "下", "0x51", new[] { 0x28 }),
        new("left", "‹", "左", "0x50", new[] { 0x25 }),
        new("right", "›", "右", "0x4f", new[] { 0x27 }),
        new("favorite", "♡", "收藏", "0x08", new[] { 0x11, 0x44 }),             // Ctrl+D
        new("voice", "🎤", "语音", "0x10", new[] { 0xA5 }),                      // 右 Alt
        new("account", "👤", "账户", "0x0d", new[] { 0x5B }),                    // Win
        new("play_pause", "⏯", "播放 / 暂停", "0x31", new[] { 0xB3 }),
        new("fast_forward", "▶▶", "快进", "0x19", new[] { 0xB0 }),               // 下一曲
        new("rewind", "◀◀", "快退", "0x36", new[] { 0xB1 }),                     // 上一曲
        new("channel_up", "ch▲", "频道 +", "0x04", new[] { 0x21 }),               // PageUp（修正原来错配的 PageDown）
        new("channel_down", "ch▼", "频道 -", "0x05", new[] { 0x22 }),             // PageDown
        new("volume_up", "vol+", "音量 +", "0x1f", new[] { 0xAF }),
        new("volume_down", "vol−", "音量 -", "0x20", new[] { 0xAE }),
        new("mute", "🔇", "静音", "0x0a", new[] { 0xAD }),
        new("last", "last", "上一频道", "0x06", new[] { 0x12, 0x09 }),             // Alt+Tab
        new("square", "▭", "方块键", "0x16", new[] { 0x7A }),                   // F11（全屏）
    };

    /// <summary>虚拟键码 → 显示名。也用于「当前操作」判断遥控器按键。</summary>
    public static readonly Dictionary<int, string> VkName = new()
    {
        [0x0D] = "enter", [0x1B] = "Esc", [0x20] = "空格", [0x09] = "Tab", [0x08] = "Backspace",
        [0x25] = "←", [0x26] = "↑", [0x27] = "→", [0x28] = "↓", [0x21] = "PgUp", [0x22] = "PgDn",
        [0x11] = "Ctrl", [0x10] = "Shift", [0x12] = "Alt", [0x5B] = "Win",
        [0xA0] = "LShift", [0xA1] = "RShift", [0xA2] = "LCtrl", [0xA3] = "RCtrl",
        [0xA4] = "左 Alt", [0xA5] = "右 Alt",
        [0xAD] = "静音", [0xAE] = "音量-", [0xAF] = "音量+", [0x5D] = "菜单键",
        [0xB0] = "下一曲", [0xB1] = "上一曲", [0xB2] = "停止", [0xB3] = "播放/暂停",
    };

    private static readonly HashSet<int> Mods = new() { 0x11, 0x10, 0x12, 0x5B, 0x5C, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5 };

    /// <summary>遥控器各按键的虚拟键码 → button id（拦截与注入都用它）。</summary>
    public static readonly Dictionary<ushort, string> VkToButtonId = new()
    {
        [0x54] = "power", [0x46] = "back", [0x4E] = "menu", [0x44] = "exit", [0x48] = "info",
        [0x0D] = "ok", [0x26] = "up", [0x28] = "down", [0x25] = "left", [0x27] = "right",
        [0x45] = "favorite", [0x4D] = "voice", [0x4A] = "account", [0xDC] = "play_pause",
        [0x56] = "fast_forward", [0xBC] = "rewind", [0x41] = "channel_up", [0x42] = "channel_down",
        [0x32] = "volume_up", [0x33] = "volume_down", [0x47] = "mute", [0x43] = "last", [0x53] = "square",
    };

    /// <summary>遥控器各按键对应的虚拟键码（HID usage 换算所得）。</summary>
    public static readonly Dictionary<int, string> VkToButton = new()
    {
        [0x54] = "电源", [0x46] = "返回", [0x4E] = "菜单", [0x44] = "退出 / Home", [0x48] = "信息",
        [0x0D] = "确认", [0x26] = "上", [0x28] = "下", [0x25] = "左", [0x27] = "右",
        [0x45] = "收藏", [0x4D] = "语音", [0x4A] = "账户", [0xDC] = "播放 / 暂停",
        [0x56] = "快进", [0xBC] = "快退", [0x41] = "频道 +", [0x42] = "频道 -",
        [0x32] = "音量 +", [0x33] = "音量 -", [0x47] = "静音", [0x43] = "上一频道", [0x53] = "方块键",
    };

    public static string VkLabel(int vk)
    {
        if (VkName.TryGetValue(vk, out var s)) return s;
        if ((vk >= 0x30 && vk <= 0x39) || (vk >= 0x41 && vk <= 0x5A)) return ((char)vk).ToString();
        if (vk >= 0x70 && vk <= 0x87) return $"F{vk - 0x6F}";
        return $"0x{vk:X2}";
    }

    public static string Format(int[] vks)
    {
        var mods = vks.Where(v => Mods.Contains(v)).Select(VkLabel);
        var rest = vks.Where(v => !Mods.Contains(v)).Select(VkLabel);
        return string.Join("+", mods.Concat(rest));
    }

    public static int[] Parse(string text)
    {
        var t = (text ?? "").Replace('，', ',').Replace('＋', '+').Trim();
        if (t.Length == 0) return Array.Empty<int>();
        var parts = t.Split(new[] { '+', ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
        var outp = new List<int>();
        foreach (var raw in parts)
        {
            var p = raw.Trim();
            int v;
            if (p.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(p[2..], System.Globalization.NumberStyles.HexNumber, null, out v))
                    throw new ArgumentException($"无法解析：{p}");
            }
            else if (!int.TryParse(p, out v)) throw new ArgumentException($"无法解析：{p}");
            if (v < 0 || v > 0xFE) throw new ArgumentException($"虚拟键码越界：{p}");
            outp.Add(v);
        }
        return outp.ToArray();
    }

    // ───────── 存储 ─────────

    private static string Dir
    {
        get
        {
            var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Env.AppName);
            Directory.CreateDirectory(d);
            return d;
        }
    }

    /// <summary>
    /// 每个按键在 `remote.png`（179×663）里的位置（中心点 + 裁剪尺寸）。
    ///
    /// 用途有两个，都靠这张表：
    ///   ① 列表里每行的**按键缩略图** —— 用 CSS sprite 从 remote.png 裁出这一小块；
    ///   ② 遥控器图上选中时**高亮**对应的键 —— 按这张表叠一层高亮框。
    ///
    /// 坐标是从"叠加坐标网格的 remote.png"上逐个读出来的（遥控器是规则三列布局，
    /// 列心 x≈40 / 90 / 138，行心见下）。
    /// </summary>
    public static readonly Dictionary<string, (int Cx, int Cy, int W, int H)> Rects = new()
    {
        //            id            中心x  中心y   宽  高
        ["power"]        = (140,  47, 44, 46),
        ["back"]         = ( 39, 147, 44, 44),
        ["up"]           = ( 91, 147, 48, 44),
        ["menu"]         = (139, 147, 44, 44),
        ["left"]         = ( 39, 200, 44, 44),
        ["ok"]           = ( 91, 200, 48, 44),
        ["right"]        = (139, 200, 44, 44),
        ["exit"]         = ( 39, 252, 44, 44),
        ["down"]         = ( 91, 252, 48, 44),
        ["info"]         = (139, 252, 44, 44),
        ["favorite"]     = ( 39, 318, 44, 44),
        ["voice"]        = ( 91, 318, 48, 44),
        ["account"]      = (139, 318, 44, 44),
        ["rewind"]       = ( 39, 372, 44, 44),
        ["play_pause"]   = ( 91, 372, 48, 44),
        ["fast_forward"] = (139, 372, 44, 44),
        ["volume_up"]    = ( 39, 428, 46, 30),
        ["mute"]         = ( 91, 428, 42, 42),
        ["channel_up"]   = (142, 428, 46, 30),
        ["volume_down"]  = ( 39, 478, 46, 30),
        ["last"]         = (105, 478, 48, 42),
        ["channel_down"] = (142, 478, 46, 30),
        ["square"]       = ( 90, 617, 46, 36),
    };

    /// <summary>remote.png 的自然尺寸（sprite 定位要用）</summary>
    public const int ImgW = 179;
    public const int ImgH = 663;

    /// <summary>
    /// 把按键映射格式化成**符号化组合键**（和业界习惯一致）：
    ///   Ctrl→`Ctrl` Alt→`Alt` Shift→`Shift` Win→`Win`；普通键用短名（↑ ↓ ← → Return Esc PgUp …）
    /// 例：`[0x11,0x41]` → `Ctrl+A`；`[0xA5]` → `RAlt`
    /// </summary>
    public static string FormatSymbols(int[] vks)
    {
        if (vks == null || vks.Length == 0) return "";
        var mods = new List<string>();
        var keys = new List<string>();
        foreach (var vk in vks)
        {
            // 用 **Windows 习惯的文字前缀**（不是 mac 那套符号）
            switch (vk)
            {
                case 0x11 or 0xA2 or 0xA3: mods.Add("Ctrl"); continue;   // Ctrl
                case 0x12 or 0xA4: mods.Add("Alt"); continue;            // Alt
                case 0x10 or 0xA0 or 0xA1: mods.Add("Shift"); continue;  // Shift
                case 0x5B or 0x5C: mods.Add("Win"); continue;            // Win（开始键）
                case 0xA5: keys.Add("RAlt"); continue;                   // 右 Alt（豆包说话）单独显示
            }
            keys.Add(VkLabel(vk));
        }
        return string.Join("+", mods.Concat(keys));   // 例：Ctrl+Shift+Q
    }

    /// <summary>按键 id → 遥控器上的标注（界面显示用）</summary>
    public static string LabelOf(string? id)
        => Table.FirstOrDefault(b => b.Id == id)?.Label ?? (id ?? "");

    private static string File_ => Path.Combine(Dir, "keymap.json");

    /// <summary>
    /// 推荐默认的版本。变更默认键位时 +1；老配置（版本更低或缺失）会在下次加载时
    /// **自动升级到最新推荐默认**（用户的旧自定义会被覆盖，故升版本要谨慎）。
    /// </summary>
    public const int DefaultsVersion = 2;

    private static string VersionFile => Path.Combine(Dir, "keymap.version");

    private static int ReadVersion()
    {
        try { return File.Exists(VersionFile) ? int.Parse(File.ReadAllText(VersionFile).Trim()) : 0; }
        catch { return 0; }
    }

    private static void WriteVersion(int v)
    {
        try { File.WriteAllText(VersionFile, v.ToString()); } catch { }
    }

    /// <summary>是否刚刚从旧版本升级到了推荐默认（界面提示用）</summary>
    public static bool JustMigrated { get; private set; }

    /// <summary>
    /// 内存缓存。**按键路径上不要读磁盘** —— ApplyButton 每次按键都会取映射，
    /// 原来每按一次就读一次 keymap.json，连按时会带来可见的延迟。
    /// 任何写入都会清缓存。
    /// </summary>
    private static Dictionary<string, int[]>? _cache;

    public static void InvalidateCache() => _cache = null;

    public static Dictionary<string, int[]> Load()
    {
        if (_cache != null) return _cache;

        // 老配置 → 升级到最新推荐默认
        if (ReadVersion() < DefaultsVersion)
        {
            var fresh = Table.ToDictionary(b => b.Id, b => (int[])b.Default.Clone());
            try
            {
                File.WriteAllText(File_, JsonSerializer.Serialize(fresh,
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
            WriteVersion(DefaultsVersion);
            JustMigrated = ReadVersion() > 0;
            return _cache = fresh;
        }
        return _cache = LoadFromDisk();
    }

    private static Dictionary<string, int[]> LoadFromDisk()
    {
        var map = Table.ToDictionary(b => b.Id, b => (int[])b.Default.Clone());
        try
        {
            if (File.Exists(File_))
            {
                var saved = JsonSerializer.Deserialize<Dictionary<string, int[]>>(File.ReadAllText(File_));
                if (saved != null)
                    foreach (var (k, v) in saved)
                        if (map.ContainsKey(k)) map[k] = v;
            }
        }
        catch { /* 忽略损坏的配置 */ }
        return map;
    }

    public static void Save(Dictionary<string, int[]> map)
    {
        File.WriteAllText(File_, JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }));
        WriteVersion(DefaultsVersion);
        InvalidateCache();
    }

    public static void Reset()
    {
        File.WriteAllText(File_,
            JsonSerializer.Serialize(Table.ToDictionary(b => b.Id, b => b.Default),
                new JsonSerializerOptions { WriteIndented = true }));
        InvalidateCache();
    }
}
