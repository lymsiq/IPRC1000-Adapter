//! 改键弹窗（2026-10-31，egui 版）
//!
//! 对应 C# 版那个「设置「↑」的快捷键」弹窗：全键盘点选 + 文本 / 宏两个页签 + 动作行。
//!
//! 三种改键方式，各有各的场合（这也是为什么不能只留一种）：
//!   · **点选键盘**：知道要什么键、想精确指定（比如 Ctrl+Shift+S），最常用；
//!   · **录键**（按遥控器）：手上正好拿着遥控器、懒得找键；
//!   · **文本 / 宏**：绑一段话或一串步骤，不属于"按键"范畴。
//!
//! ⚠ 重要概念澄清：这里改的是「**按下遥控器某个键 → 往系统发什么**」，
//!   **不是**修改 Windows 的快捷键。`Win+L` 只是电源键的默认值，改掉它不影响系统锁屏。

use eframe::egui::{self, Color32, RichText};

use crate::keymap;

#[derive(PartialEq, Clone, Copy)]
pub enum EditTab {
    Keys,
    Text,
    Macro,
}

/// 正在编辑的那个键
pub struct EditState {
    /// 按键 id（"up" / "voice" …）
    pub id: String,
    /// 遥控器丝印（"↑"）
    pub label: String,
    /// 当前选中的 VK 列表
    pub vks: Vec<u16>,
    /// 当前选中的动作（-1..-6），与 vks 互斥
    pub action: Option<i32>,
    pub tab: EditTab,
    /// 文本页签选中的槽（内容在「文本」页管理）
    pub text_slot: u32,
    /// 宏页签选中的宏编号
    pub macro_num: u32,
    /// 「全局」开关：所有配置组都生效（本版先只做展示，落盘逻辑在下一版）
    pub global: bool,
}

/// 弹窗的返回：None = 还开着；Some(Some(v)) = 确定（值是绑定字符串）；Some(None) = 取消
pub type EditResult = Option<Option<String>>;

impl EditState {
    pub fn new(id: &str, label: &str) -> Self {
        let usage = keymap::usage_of_id(id);
        let vks = usage.map(|u| keymap::vks_of(u)).unwrap_or_default();
        let action = usage.and_then(keymap::action_of);
        Self {
            id: id.to_string(),
            label: label.to_string(),
            vks,
            action,
            tab: EditTab::Keys,
            text_slot: 1,
            macro_num: 0,
            global: false,
        }
    }

    /// 当前选择的展示文案
    fn preview(&self) -> String {
        if let Some(a) = self.action {
            return crate::mouse_act::action_name(a).to_string();
        }
        if self.vks.is_empty() {
            return "（未设置）".to_string();
        }
        self.vks
            .iter()
            .map(|v| keymap::vk_name(*v))
            .collect::<Vec<_>>()
            .join("+")
    }

    /// 是否已选中某个键
    fn has(&self, vk: u16) -> bool {
        self.vks.contains(&vk)
    }

    /// 点一个普通键：**替换**（不是叠加）—— 用户点 "S" 就是想要 S，
    /// 不是想把 S 加到刚才的组合里。修饰键（Ctrl/Alt/Shift/Win）才叠加。
    fn pick_normal(&mut self, vk: u16) {
        self.action = None;
        self.vks = vec![vk];
    }

    /// 点修饰键：叠加/取消
    fn toggle_mod(&mut self, vk: u16) {
        self.action = None;
        if let Some(pos) = self.vks.iter().position(|v| *v == vk) {
            self.vks.remove(pos);
        } else {
            // 修饰键永远排前面（Win+Shift+S 这种顺序显示才正常）
            self.vks.insert(0, vk);
        }
    }

    fn pick_action(&mut self, act: i32) {
        self.action = Some(act);
        self.vks.clear();
    }
}

/// 键盘布局（与 C# 版 `KB_ROWS` 一致）：(显示文字, VK, 宽度倍率)
const ROW1: &[(&str, u16, f32)] = &[
    ("Esc", 0x1B, 1.0), ("F1", 0x70, 1.0), ("F2", 0x71, 1.0), ("F3", 0x72, 1.0),
    ("F4", 0x73, 1.0), ("F5", 0x74, 1.0), ("F6", 0x75, 1.0), ("F7", 0x76, 1.0),
    ("F8", 0x77, 1.0), ("F9", 0x78, 1.0), ("F10", 0x79, 1.0), ("F11", 0x7A, 1.0),
    ("F12", 0x7B, 1.0),
];
const ROW2: &[(&str, u16, f32)] = &[
    ("`", 0xC0, 1.0), ("1", 0x31, 1.0), ("2", 0x32, 1.0), ("3", 0x33, 1.0),
    ("4", 0x34, 1.0), ("5", 0x35, 1.0), ("6", 0x36, 1.0), ("7", 0x37, 1.0),
    ("8", 0x38, 1.0), ("9", 0x39, 1.0), ("0", 0x30, 1.0), ("-", 0xBD, 1.0),
    ("=", 0xBB, 1.0), ("⌫", 0x08, 1.6),
];
const ROW3: &[(&str, u16, f32)] = &[
    ("Tab", 0x09, 1.4), ("Q", 0x51, 1.0), ("W", 0x57, 1.0), ("E", 0x45, 1.0),
    ("R", 0x52, 1.0), ("T", 0x54, 1.0), ("Y", 0x59, 1.0), ("U", 0x55, 1.0),
    ("I", 0x49, 1.0), ("O", 0x4F, 1.0), ("P", 0x50, 1.0), ("[", 0xDB, 1.0),
    ("]", 0xDD, 1.0), ("\\", 0xDC, 1.4),
];
const ROW4: &[(&str, u16, f32)] = &[
    ("Caps", 0x14, 1.8), ("A", 0x41, 1.0), ("S", 0x53, 1.0), ("D", 0x44, 1.0),
    ("F", 0x46, 1.0), ("G", 0x47, 1.0), ("H", 0x48, 1.0), ("J", 0x4A, 1.0),
    ("K", 0x4B, 1.0), ("L", 0x4C, 1.0), (";", 0xBA, 1.0), ("'", 0xDE, 1.0),
    ("enter", 0x0D, 2.2),
];
const ROW5: &[(&str, u16, f32)] = &[
    ("Shift", 0x10, 2.2), ("Z", 0x5A, 1.0), ("X", 0x58, 1.0), ("C", 0x43, 1.0),
    ("V", 0x56, 1.0), ("B", 0x42, 1.0), ("N", 0x4E, 1.0), ("M", 0x4D, 1.0),
    (",", 0xBC, 1.0), (".", 0xBE, 1.0), ("/", 0xBF, 1.0), ("Shift", 0x10, 2.2),
];
const ROW6: &[(&str, u16, f32)] = &[
    ("Ctrl", 0x11, 1.4), ("Win", 0x5B, 1.0), ("Alt", 0x12, 1.0),
    ("Space", 0x20, 6.0), ("Alt", 0x12, 1.0), ("Win", 0x5B, 1.0),
    ("Menu", 0x5D, 1.0), ("Ctrl", 0x11, 1.4),
];
const ROW_MEDIA: &[(&str, u16, f32)] = &[
    ("播放/暂停", 0xB3, 1.0), ("上一首", 0xB1, 1.0), ("下一首", 0xB0, 1.0),
    ("音量 −", 0xAE, 1.0), ("音量 +", 0xAF, 1.0), ("静音", 0xAD, 1.0),
];
const ROW_NAV: &[(&str, u16, f32)] = &[
    ("↑", 0x26, 1.0), ("↓", 0x28, 1.0), ("←", 0x25, 1.0), ("→", 0x27, 1.0),
    ("PgUp", 0x21, 1.0), ("PgDn", 0x22, 1.0),
];
/// 可绑定的动作。**不含"截图点击"**（-6）：模板匹配尚未实现，
/// 留着入口只会让用户绑了发现没反应。底层 `mouse_act::ACT_CLICK_ICON`
/// 仍保留，以便旧配置里的 -6 不会退化成键盘键。
const ROW_ACT: &[(&str, i32)] = &[
    ("切换配置组", -5), ("鼠标左键", -1), ("鼠标右键", -2),
    ("滚轮上", -3), ("滚轮下", -4),
];

/// 渲染弹窗。返回 `Some(Some(值))` = 用户点了确定。
pub fn show(ctx: &egui::Context, st: &mut EditState) -> EditResult {
    let mut result: EditResult = None;
    let mut open = true;
    egui::Window::new(format!("设置「{}」的快捷键", st.label))
        .open(&mut open)
        .collapsible(false)
        .resizable(false)
        .default_width(520.0)
        .anchor(egui::Align2::CENTER_CENTER, [0.0, 0.0])
        .show(ctx, |ui| {
            // 顶部：当前值 + 全局
            ui.horizontal(|ui| {
                ui.label(RichText::new("当前：").weak());
                ui.label(RichText::new(st.preview()).strong());
                ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                    ui.checkbox(&mut st.global, "全局");
                });
            });
            ui.add_space(6.0);

            // 页签
            ui.horizontal(|ui| {
                ui.selectable_value(&mut st.tab, EditTab::Keys, "按键");
                ui.selectable_value(&mut st.tab, EditTab::Text, "文本");
                ui.selectable_value(&mut st.tab, EditTab::Macro, "宏");
            });
            ui.separator();

            match st.tab {
                EditTab::Keys => keys_pane(ui, st),
                EditTab::Text => {
                    // ★ 这里**不再直接编辑文本**：文本内容统一在「文本」页管理，
                    //   同一份数据两个编辑入口必然打架（改了一处另一处看不见）。
                    //   这里只负责"选哪个槽"。
                    ui.label(
                        RichText::new("选一个文本槽绑给这个键（内容去「文本」页编辑）")
                            .small()
                            .weak(),
                    );
                    ui.add_space(4.0);
                    ui.horizontal_wrapped(|ui| {
                        for slot in 1..=10u32 {
                            let t = crate::text_slot::get(slot);
                            let chars = t.as_ref().map(|x| x.text.chars().count()).unwrap_or(0);
                            let sel = st.text_slot == slot;
                            let label = if chars > 0 {
                                format!("槽 {slot}（{chars} 字）")
                            } else {
                                format!("槽 {slot}（空）")
                            };
                            if ui.selectable_label(sel, label).clicked() {
                                st.text_slot = slot;
                            }
                        }
                    });
                    if let Some(t) = crate::text_slot::get(st.text_slot) {
                        if !t.text.is_empty() {
                            ui.add_space(4.0);
                            ui.label(RichText::new("预览：").small().weak());
                            let preview: String = t.text.chars().take(120).collect();
                            ui.label(RichText::new(preview).small());
                        }
                    }
                }
                EditTab::Macro => {
                    ui.label(RichText::new("绑一条宏：按下这个键就播放它").small().weak());
                    let list = crate::macro_store::all();
                    if list.is_empty() {
                        ui.label(RichText::new("还没有宏（先到宏编辑器里建一条）").weak());
                    }
                    for m in list {
                        let sel = st.macro_num == m.num;
                        let name = if m.name.is_empty() {
                            format!("宏 {}", m.num)
                        } else {
                            format!("{}（{} 步）", m.name, m.steps.len())
                        };
                        if ui.selectable_label(sel, name).clicked() {
                            st.macro_num = m.num;
                        }
                    }
                }
            }

            ui.add_space(8.0);
            ui.separator();
            ui.horizontal(|ui| {
                if ui.button("确定").clicked() {
                    let val = match st.tab {
                        EditTab::Keys => {
                            if let Some(a) = st.action {
                                a.to_string()
                            } else {
                                st.vks.iter().map(|v| v.to_string()).collect::<Vec<_>>().join(",")
                            }
                        }
                        EditTab::Text => {
                            let slot = st.text_slot.clamp(1, 10) as i64;
                            format!("{}", -1000 - slot)
                        }
                        EditTab::Macro => {
                            if st.macro_num == 0 {
                                String::new()
                            } else {
                                format!("{}", -1100 - st.macro_num as i64)
                            }
                        }
                    };
                    result = Some(Some(val));
                }
                if ui.button("清除").clicked() {
                    result = Some(Some(String::new()));
                }
                if ui.button("取消").clicked() {
                    result = Some(None);
                }
            });
        });
    if !open {
        return Some(None);
    }
    result
}

fn keys_pane(ui: &mut egui::Ui, st: &mut EditState) {
    let bw = 30.0;   // 单键基准宽度
    let bh = 24.0;
    let mut clicked: Option<(u16, bool)> = None; // (vk, 是修饰键)
    let mut act_clicked: Option<i32> = None;

    // 修饰键行（单独一行，醒目且好点）
    ui.horizontal(|ui| {
        for (name, vk) in [("Ctrl", 0x11u16), ("Alt", 0x12), ("Shift", 0x10), ("Win（开始键）", 0x5B)] {
            let on = st.has(vk);
            let btn = egui::Button::new(RichText::new(name).color(if on {
                Color32::from_rgb(60, 140, 240)
            } else {
                Color32::GRAY
            }))
            .min_size(egui::vec2(bw * 3.2, bh));
            if ui.add(btn).clicked() {
                clicked = Some((vk, true));
            }
        }
    });
    ui.add_space(4.0);

    let row = |ui: &mut egui::Ui, keys: &[(&str, u16, f32)], st: &EditState, clicked: &mut Option<(u16, bool)>| {
        ui.horizontal(|ui| {
            for (name, vk, w) in keys {
                // 修饰键（出现多处的 Shift/Ctrl/Alt/Win）走叠加逻辑
                let is_mod = matches!(*vk, 0x10 | 0x11 | 0x12 | 0x5B);
                let on = st.has(*vk);
                let b = egui::Button::new(RichText::new(*name).color(if on {
                    Color32::from_rgb(60, 140, 240)
                } else {
                    Color32::from_gray(210)
                }))
                .min_size(egui::vec2(bw * w, bh));
                if ui.add(b).clicked() {
                    *clicked = Some((*vk, is_mod));
                }
            }
        });
    };

    for keys in [ROW1, ROW2, ROW3, ROW4, ROW5, ROW6] {
        row(ui, keys, st, &mut clicked);
    }
    ui.add_space(6.0);
    row(ui, ROW_MEDIA, st, &mut clicked);
    row(ui, ROW_NAV, st, &mut clicked);

    ui.add_space(6.0);
    ui.horizontal(|ui| {
        ui.label(RichText::new("动作：").small().weak());
        for (name, act) in ROW_ACT {
            let on = st.action == Some(*act);
            if ui
                .add(
                    egui::Button::new(RichText::new(*name).color(if on {
                        Color32::from_rgb(60, 140, 240)
                    } else {
                        Color32::from_gray(200)
                    }))
                    .min_size(egui::vec2(bw * 2.6, bh)),
                )
                .clicked()
            {
                act_clicked = Some(*act);
            }
        }
    });

    // 延迟处理点击（避免在闭包里同时借用 st）
    if let Some((vk, is_mod)) = clicked {
        if is_mod {
            st.toggle_mod(vk);
        } else {
            st.pick_normal(vk);
        }
    }
    if let Some(a) = act_clicked {
        st.pick_action(a);
    }
}
