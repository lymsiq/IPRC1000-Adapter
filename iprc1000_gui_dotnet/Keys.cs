namespace Iprc1000Adapter;

/// <summary>
/// 按键表：**只描述按键本身**（id / 遥控器上印的字符 / 中文说明 / HID usage）。
///
/// ★ 这里**刻意不再保存"默认映射"**。曾经 `Btn` 上有个 `Default` 字段，
///   和 <see cref="Defaults"/> 里的出厂映射各写一份 —— 两份数据一定会漂移，
///   而"同一个东西有多个来源"正是本程序历史上最难查的那类 bug 的成因。
///   现在出厂映射**只有 <see cref="Defaults"/> 一个权威来源**。
/// </summary>
internal static class Keys
{
    /// <summary>
    /// 一个遥控器按键。
    ///   Id      — 内部标识
    ///   Label   — **遥控器上实际印的字符/图标**（界面按它显示，不用中文）
    ///   Name    — 中文说明（仅作悬停提示/无障碍用）
    ///   Hid     — HID usage（ETW 上报的原始值，排查用）
    /// </summary>
    public record Btn(string Id, string Label, string Name, string Hid);

    public static readonly Btn[] Table =
    {
        // 出厂默认映射见 Defaults.Plain() / Defaults.WorkBuddy() —— 本表不再重复记录。
        new("power", "pwr", "电源", "0x17"),
        new("back", "back", "返回", "0x09"),
        new("menu", "menu", "菜单", "0x11"),
        new("exit", "exit", "退出 / Home", "0x07"),
        new("info", "info", "信息", "0x0b"),
        new("ok", "ok", "确认", "0x28"),
        new("up", "▲", "上", "0x52"),
        new("down", "▼", "下", "0x51"),
        new("left", "‹", "左", "0x50"),
        new("right", "›", "右", "0x4f"),
        new("favorite", "♡", "收藏", "0x08"),
        new("voice", "🎤", "语音", "0x10"),
        new("account", "👤", "账户", "0x0d"),
        new("play_pause", "⏯", "播放 / 暂停", "0x31"),
        new("fast_forward", "▶▶", "快进", "0x19"),
        new("rewind", "◀◀", "快退", "0x36"),
        new("channel_up", "ch▲", "频道 +", "0x04"),
        new("channel_down", "ch▼", "频道 -", "0x05"),
        new("volume_up", "vol+", "音量 +", "0x1f"),
        new("volume_down", "vol−", "音量 -", "0x20"),
        new("mute", "🔇", "静音", "0x0a"),
        new("last", "last", "上一频道", "0x06"),
        new("square", "▭", "方块键", "0x16"),
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

    // ───────── 显示 / 解析辅助 ─────────

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

    // ═══════════════════════════════════════════════════════════════════════
    //  ★★ 注意：这里**曾经**有一套「扁平 keymap.json」的读写实现（Load/Save/Reset）。
    //
    //  它和 Keymaps（多配置组）**写同一个文件** %APPDATA%\IPRC1000 Adapter\keymap.json，
    //  但格式不同：本类要 Dictionary<string,int[]>，Keymaps 写的是 {Ver,Active,Groups:[…]}。
    //  于是 JsonSerializer.Deserialize 必然抛异常，被 catch 吞掉后**静默回落到出厂默认** ——
    //  界面显示用户配置、实际注入硬编码默认，改什么都不生效（back/menu/info 全中招）。
    //
    //  该实现已**删除**，配置一律走 Keymaps（唯一数据源）。下面的 Load() 仅作兼容转发。
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 【已废弃】请改用 <see cref="Keymaps.Get"/>。
    ///
    /// 保留此方法只是为了让老调用点编译通过，并**保证不再返回错误数据**：
    /// 它现在直接转发到当前配置组，返回值一定与界面显示一致。
    /// </summary>
    [Obsolete("请改用 Keymaps.Get(id) —— 本方法读的是已废弃的扁平格式，曾导致注入用错映射。")]
    public static Dictionary<string, int[]> Load()
        => Table.ToDictionary(b => b.Id, b => Keymaps.Get(b.Id));

    /// <summary>【已废弃】改用 <see cref="Keymaps.ResetCurrent"/>。</summary>
    [Obsolete("请改用 Keymaps.ResetCurrent()。")]
    public static void Reset() => Keymaps.ResetCurrent();
}
