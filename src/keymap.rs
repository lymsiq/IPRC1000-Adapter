//! 遥控器 usage → 注入键 的映射表（原生内核，2026-10-30）。
//!
//! 为什么必须有这张表：
//!   遥控器报文里的 usage 是**它自己的编号**，不是标准 HID 键盘 usage。
//!   2026-10-30 真机实测：`back` 发的是 `0x09`，而 `0x09` 在键盘页上就是字母 **f**
//!   （Backspace 是 `0x2A`）—— 直通注会打出 f。方向键能对只是**巧合**：
//!   `0x4F/0x50/0x51/0x52` 恰好等于键盘页的 → ← ↓ ↑ 和 Enter。
//!
//! 映射内容与 C# 的 `Keymaps.Defaults.Plain()` **逐个对齐**（那是唯一权威）。
//! 改这里必须同步改 `iprc1000_gui_dotnet/Keymaps.cs`，否则两版行为会漂移。

/// 遥控器 usage → 注入的虚拟键码（VK）。空数组 = 暂不支持（动作值/媒体键）。
///
/// 顺序与 `Keys.Table` 一致，便于人工对照。
pub const PLAIN: &[(u8, &[u16])] = &[
    (0x17, &[0x5B, 0x4C]),          // power    → Win+L 锁屏
    (0x09, &[0x08]),                // back     → Backspace
    (0x11, &[0x5D]),                // menu     → 菜单键（右键菜单）
    (0x07, &[0x1B]),                // exit     → Esc
    (0x0B, &[0x11, 0x0D]),          // info     → Ctrl+Enter
    (0x28, &[0x0D]),                // ok       → Enter
    (0x52, &[0x26]),                // up       → ↑
    (0x51, &[0x28]),                // down     → ↓
    (0x50, &[0x25]),                // left     → ←
    (0x4F, &[0x27]),                // right    → →
    (0x08, &[0x5B, 0x10, 0x53]),    // favorite → Win+Shift+S 区域截图
    (0x10, &[0xA5]),                // voice    → 右 Alt（豆包长按说话）
    (0x0D, &[]),                    // account  → 出厂不绑定（截图点击已移除入口）
    (0x31, &[]),                    // play_pause   → 媒体键，见下方说明
    (0x19, &[0x12, 0x1B]),          // fast_forward → Alt+Esc
    (0x36, &[0x5B, 0x44]),          // rewind       → Win+D 显示桌面
    (0x04, &[0x11, 0xDB]),          // channel_up   → Ctrl+[
    (0x05, &[0x11, 0xDD]),          // channel_down → Ctrl+]
    (0x1F, &[]),                    // volume_up    → 媒体键
    (0x20, &[]),                    // volume_down  → 媒体键
    (0x0A, &[]),                    // mute         → 媒体键
    (0x06, &[0x12, 0x11, 0x09]),    // last         → Ctrl+Alt+Tab
    (0x16, &[]),                    // square       → 动作值「切换配置组」
];

// ⚠ 媒体键（音量±/静音/播放暂停）暂时留空：它们是 **HID 消费者页**（0x0C）
//   的用法，而我们的报告描述符只声明了键盘页（0x07）—— 塞进键盘报告只会变成
//   "未知 usage"被系统丢弃。C# 版对这几个 VK 的 `Usage()` 也返回 null（同样不注入）。
//   原生内核要做它们，需要在同一台虚拟设备上加**第二个报告 ID / 消费者集合**，
//   属于下一步（先保证键盘页全对，避免又引入"看起来有、实际没发"的假象）。

/// 遥控器 usage 对应哪几个 VK
/// 用户自定义绑定（usage → VK 列表）。空表 = 全部用出厂默认。
///
/// 为什么要独立一层而不是改 `PLAIN`：出厂默认是"还原出厂设置"的依据，
/// 也是断线时的兜底；用户改的永远是可撤销的覆盖层。
static OVERRIDE: std::sync::OnceLock<std::sync::Mutex<std::collections::HashMap<u8, Vec<u16>>>> =
    std::sync::OnceLock::new();

fn ov() -> &'static std::sync::Mutex<std::collections::HashMap<u8, Vec<u16>>> {
    OVERRIDE.get_or_init(|| std::sync::Mutex::new(std::collections::HashMap::new()))
}

/// 设置/清除某个键的自定义绑定（`vks` 为空 = 该键禁用，按下不注入任何东西）
pub fn set_override(usage: u8, vks: Vec<u16>) {
    if let Ok(mut g) = ov().lock() {
        if vks.is_empty() {
            g.remove(&usage);
            g.insert(usage, Vec::new());   // 空表也要占位：表示"用户主动禁用"，区别于"没设置"
        } else {
            g.insert(usage, vks);
        }
    }
}

#[allow(dead_code)]  // 保留给「恢复出厂设置」（界面暂未接）
pub fn remove_override(usage: u8) {
    if let Ok(mut g) = ov().lock() {
        g.remove(&usage);
    }
}

#[allow(dead_code)]
pub fn overrides() -> std::collections::HashMap<u8, Vec<u16>> {
    ov().lock().map(|g| g.clone()).unwrap_or_default()
}

/// 鼠标动作绑定（usage → 动作码 -1..-6）。与 VK 覆盖表分开存：
/// `vks_of` 每帧都要问"这个键有动作吗"，绝不能去 clone 整个 bindings 表。
static ACTIONS: std::sync::OnceLock<std::sync::Mutex<std::collections::HashMap<u8, i32>>> =
    std::sync::OnceLock::new();

fn act_map() -> &'static std::sync::Mutex<std::collections::HashMap<u8, i32>> {
    ACTIONS.get_or_init(|| std::sync::Mutex::new(std::collections::HashMap::new()))
}

pub fn set_action(usage: u8, act: Option<i32>) {
    if let Ok(mut g) = act_map().lock() {
        match act {
            Some(a) => { g.insert(usage, a); }
            None => { g.remove(&usage); }
        }
    }
}

pub fn action_of(usage: u8) -> Option<i32> {
    act_map().lock().ok().and_then(|g| g.get(&usage).copied())
}

pub fn vks_of(remote_usage: u8) -> Vec<u16> {
    // ★ 2026-10-31：语音键（0x10）**按输入法动态返回**，不再是写死的右 Alt。
    //   · 豆包 = 右 Alt（0xA5）
    //   · 微信输入法 = Ctrl+Win（0x11 + 0x5B）—— 快捷键全局生效，不必切默认输入法
    //   读的是 settings 里的**原子量**（不是 Mutex）：按键路径每帧都要问一次，
    //   锁 + clone String 会明显拖慢注入。
    // ★ 鼠标模式（组 1）下，方向键用于移动光标 ⇒ 不发键盘键
    if crate::settings::is_mouse_active() && matches!(remote_usage, 0x52 | 0x51 | 0x50 | 0x4F) {
        return Vec::new();
    }
    // 绑了鼠标动作的键**绝不能**再发键盘键（否则会多打出一个字母）
    if action_of(remote_usage).is_some() {
        return Vec::new();
    }
    // ★★ 语音键（0x10）是**全局**的：永远按「输入法」页的选择走，
    //   **不受任何配置组的绑定影响**（用户要求：改哪个组，语音键都还是那个键）。
    //   所以这个判断必须在"用户覆盖"之前 —— 顺序错了就会出现
    //   "在 A 组改过语音键 → B 组也跟着变" 或者 "某组不再唤输入法"。
    if remote_usage == 0x10 {
        return match crate::settings::IME_IDX.load(std::sync::atomic::Ordering::Relaxed) {
            1 => vec![0x11, 0x5B],  // 微信输入法：Ctrl + Win
            _ => vec![0xA5],        // 豆包：右 Alt
        };
    }
    // 用户绑定（含"主动禁用"：查到空 Vec 就直接返回空）
    if let Ok(g) = ov().lock() {
        if let Some(v) = g.get(&remote_usage) {
            return v.clone();
        }
    }
    for (u, vks) in PLAIN {
        if *u == remote_usage {
            return vks.to_vec();
        }
    }
    Vec::new()
}

/// 遥控器 usage → (id, 丝印字符)
pub fn usage_label(u: u8) -> (&'static str, &'static str) {
    // id / 显示名 —— **与 C# 版 `Keys.Table` 逐条对齐**（遥控器上的实际丝印中文名）。
    // 之前用的是我自己编的简写（"pwr"/"↑"），与实际按键对不上，用户看不出是哪个键。
    match u {
        0x17 => ("power", "电源"),
        0x09 => ("back", "返回"),
        0x11 => ("menu", "菜单"),
        0x07 => ("exit", "退出"),
        0x0B => ("info", "信息"),
        0x28 => ("ok", "确认"),
        0x52 => ("up", "▲ 上"),
        0x51 => ("down", "▼ 下"),
        0x50 => ("left", "‹ 左"),
        0x4F => ("right", "› 右"),
        0x08 => ("favorite", "♡ 收藏"),
        0x10 => ("voice", "🎤 语音"),
        0x0D => ("account", "账户"),
        0x31 => ("play_pause", "播放/暂停"),
        0x19 => ("fast_forward", "快进"),
        0x36 => ("rewind", "快退"),
        0x04 => ("channel_up", "频道 +"),
        0x05 => ("channel_down", "频道 −"),
        0x1F => ("volume_up", "音量 +"),
        0x20 => ("volume_down", "音量 −"),
        0x0A => ("mute", "静音"),
        0x06 => ("last", "上一频道"),
        0x16 => ("square", "方块键"),
        _ => ("unknown", "?"),
    }
}

/// VK → HID 键盘页 usage（逐条对照 C# `WinUhidInjector.Usage`）
pub fn usage_of(vk: u16) -> Option<u8> {
    // A-Z
    if (0x41..=0x5A).contains(&vk) {
        return Some((vk - 0x41 + 0x04) as u8);
    }
    // 1-9
    if (0x31..=0x39).contains(&vk) {
        return Some((vk - 0x31 + 0x1E) as u8);
    }
    // F1-F12
    if (0x70..=0x7B).contains(&vk) {
        return Some((vk - 0x70 + 0x3A) as u8);
    }
    Some(match vk {
        0x30 => 0x27, // 0
        0x0D => 0x28, // Enter
        0x1B => 0x29, // Esc
        0x08 => 0x2A, // Backspace
        0x09 => 0x2B, // Tab
        0x20 => 0x2C, // 空格
        0x21 => 0x4B, // PageUp
        0x22 => 0x4E, // PageDown
        0x23 => 0x4D, // End
        0x24 => 0x4A, // Home
        0x25 => 0x50, // ←
        0x26 => 0x52, // ↑
        0x27 => 0x4F, // →
        0x28 => 0x51, // ↓
        0x2D => 0x49, // Insert
        0x2E => 0x4C, // Delete
        0x5D => 0x65, // 菜单键
        0xBD => 0x2D, // -
        0xBB => 0x2E, // =
        0xDB => 0x2F, // [
        0xDD => 0x30, // ]
        0xDC => 0x31, // \
        0xE2 => 0x64, // \（102 键盘）
        0xBA => 0x33, // ;
        0xDE => 0x34, // '
        0xC0 => 0x35, // `
        0xBC => 0x36, // ,
        0xBE => 0x37, // .
        0xBF => 0x38, // /
        _ => return None,
    })
}

/// VK → 报告 byte0 里的修饰键位（逐条对照 C# `ModifierBit`）
pub fn modifier_bit(vk: u16) -> Option<u8> {
    Some(match vk {
        0x11 | 0xA2 => 0x01, // Ctrl
        0x10 | 0xA0 => 0x02, // Shift
        0x12 | 0xA4 => 0x04, // Alt
        0x5B => 0x08,        // LWin
        0xA3 => 0x10,        // RCtrl
        0xA1 => 0x20,        // RShift
        0xA5 => 0x40,        // 右 Alt（豆包长按说话）
        0x5C => 0x80,        // RWin
        _ => return None,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 2026-10-30 真机事故的回归锚点：back 曾经直通 `0x09` ⇒ 在记事本里打出字母 `f`。
    #[test]
    fn back_must_be_backspace_not_letter_f() {
        assert_eq!(vks_of(0x09), &[0x08], "遥控器 back 应映射到 VK_BACK(0x08)");
        assert_eq!(usage_of(0x08), Some(0x2A), "VK_BACK → HID 0x2A");
        assert_ne!(usage_of(0x08), Some(0x09), "绝不能落到字母 f 的 usage 上");
    }

    /// 方向键：这些是**巧合对上**的键，同样要用测试钉住，避免以后改表改错。
    #[test]
    fn arrows_and_enter() {
        assert_eq!(usage_of(vks_of(0x50)[0]), Some(0x50)); // left
        assert_eq!(usage_of(vks_of(0x52)[0]), Some(0x52)); // up
        assert_eq!(usage_of(vks_of(0x4F)[0]), Some(0x4F)); // right
        assert_eq!(usage_of(vks_of(0x51)[0]), Some(0x51)); // down
        assert_eq!(usage_of(vks_of(0x28)[0]), Some(0x28)); // ok → Enter
    }

    /// 映射表里每个 VK 都必须"真的能发出去"：要么是修饰键，要么能转成 HID usage。
    /// 否则就是"界面有、按下去没反应"的静默失败（本项目最忌讳的那种）。
    #[test]
    fn every_mapped_vk_is_emittable() {
        for (u, vks) in PLAIN {
            for &vk in *vks {
                assert!(
                    modifier_bit(vk).is_some() || usage_of(vk).is_some(),
                    "遥控器 usage 0x{u:02X} 映射的 VK 0x{vk:02X} 既不是修饰键、也转不出 HID usage"
                );
            }
        }
    }

    /// 组合键里的修饰键必须能被识别（否则 Ctrl+[ 会变成单独一个 [）
    #[test]
    fn combos_have_modifiers() {
        assert_eq!(modifier_bit(vks_of(0x0B)[0]), Some(0x01)); // info = Ctrl+Enter
        assert_eq!(modifier_bit(vks_of(0x19)[0]), Some(0x04)); // fast_forward = Alt+Esc
        assert_eq!(modifier_bit(vks_of(0x10)[0]), Some(0x40)); // voice = 右 Alt
    }
}


/// 单个 VK → 可读名（界面显示与录键回显共用）
pub fn vk_name(vk: u16) -> String {
    match vk {
        0x11 => "Ctrl".to_string(),
        0x12 => "Alt".to_string(),
        0x10 => "Shift".to_string(),
        0x5B => "Win".to_string(),
        0xA5 => "右Alt".to_string(),
        0xA3 => "右Ctrl".to_string(),
        0x0D => "Enter".to_string(),
        0x08 => "Backspace".to_string(),
        0x1B => "Esc".to_string(),
        0x09 => "Tab".to_string(),
        0x20 => "Space".to_string(),
        0x5D => "Menu".to_string(),
        // 媒体键（HID 消费者页对应的 VK）：不写名字界面会显示 0xAF 这种，
        // 客户看不出是"音量+"。两个界面都用这个函数，所以在这里补齐。
        0xAD => "静音".to_string(),
        0xAE => "音量−".to_string(),
        0xAF => "音量+".to_string(),
        0xB0 => "下一曲".to_string(),
        0xB1 => "上一曲".to_string(),
        0xB2 => "停止".to_string(),
        0xB3 => "播放/暂停".to_string(),
        0x25 => "←".to_string(),
        0x26 => "↑".to_string(),
        0x27 => "→".to_string(),
        0x28 => "↓".to_string(),
        0xDB => "[".to_string(),
        0xDD => "]".to_string(),
        0x2E => "Del".to_string(),
        0x24 => "Home".to_string(),
        0x23 => "End".to_string(),
        0x21 => "PgUp".to_string(),
        0x22 => "PgDn".to_string(),
        0x14 => "Caps".to_string(),
        // A-Z / 0-9 直接给字符
        0x41..=0x5A => char::from_u32(vk as u32).map(|c| c.to_string()).unwrap_or_default(),
        0x30..=0x39 => char::from_u32(vk as u32).map(|c| c.to_string()).unwrap_or_default(),
        // F1-F24
        0x70..=0x87 => format!("F{}", vk - 0x6F),
        v => format!("0x{v:02X}"),
    }
}

/// 按组整体重载覆盖层与动作表（切换配置组时调）。
///
/// 为什么要"清空再重载"而不是合并：两组是**互斥**的两套映射，
/// 合并会让"鼠标模式里删掉的键"还留着默认组的绑定。
pub fn load_group(map: &std::collections::HashMap<String, String>) {
    if let Ok(mut g) = ov().lock() {
        g.clear();
    }
    if let Ok(mut a) = act_map().lock() {
        a.clear();
    }
    for (id, val) in map {
        let Some(u) = usage_of_id(id) else { continue };
        let v = val.trim();
        if let Ok(act) = v.parse::<i32>() {
            if (-6..=-1).contains(&act) {
                set_action(u, Some(act));
            }
            continue;
        }
        let vks: Vec<u16> = v.split(',').filter_map(|x| x.trim().parse::<u16>().ok()).collect();
        set_override(u, vks);
    }
}

/// 按键 id → 遥控器 usage（界面与后端共用的唯一一份映射）
pub fn usage_of_id(id: &str) -> Option<u8> {
    for u in 0u16..256 {
        let u = u as u8;
        let (i, _) = usage_label(u);
        if i == id && i != "unknown" {
            return Some(u);
        }
    }
    // ★ 2026-11-02：用户自定义的按键（遥控器上有、但内置表里没有的 usage）。
    //   统一编码成 "hidXX"（十六进制），这样它能和内置键一样被持久化到
    //   `set_binding_in_group(id, value)` —— 否则"新增按键"只能活在内存里，重启就丢。
    let hex = id.strip_prefix("hid")?;
    u8::from_str_radix(hex, 16).ok()
}

/// usage 的稳定 id：内置键用 `usage_label` 的 id，其余统一 `hidXX`
pub fn id_of(u: u8) -> String {
    let (id, _) = usage_label(u);
    if id == "unknown" {
        format!("hid{:02X}", u)
    } else {
        id.to_string()
    }
}

/// 给 Win32 界面用的：某个 usage 的显示名（未知键显示 `HID xx`）
pub fn name_of(u: u8) -> String {
    let (id, name) = usage_label(u);
    if id == "unknown" {
        format!("HID {:02X}", u)
    } else {
        name.to_string()
    }
}
