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
    /// 文本轮盘是否展开（动作行的「文本轮盘」打开）
    pub wheel_open: bool,
    /// 轮盘当前高亮方向：0=上(槽1) 1=右(槽2) 2=下(槽3) 3=左(槽4)
    pub wheel_sel: u8,
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
            wheel_open: false,
            wheel_sel: 0,
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
                        RichText::new("点 1–4 号格子：按下这个键就输出那段文字（内容在「文本」页编辑）")
                            .small()
                            .weak(),
                    );
                    ui.add_space(6.0);
                    // ★ 1–4 号大格子（2026-11-10）：文本回复最常用的就是前四句，
                    //   编号 + 摘要直接可见，比"槽 1（空）/ 槽 2（3 字）"更好认。
                    //   复用同一份 text_slot 数据，不新增存储、不新增概念。
                    ui.horizontal(|ui| {
                        for slot in 1..=4u32 {
                            let t = crate::text_slot::get(slot);
                            let chars = t.as_ref().map(|x| x.text.chars().count()).unwrap_or(0);
                            let sel = st.text_slot == slot;
                            let summary: String = if chars > 0 {
                                let mut s: String = t
                                    .as_ref()
                                    .map(|x| x.text.chars().take(7).collect())
                                    .unwrap_or_default();
                                if chars > 7 {
                                    s.push('…');
                                }
                                s
                            } else {
                                "未设置".to_string()
                            };
                            let fg = if sel {
                                Color32::WHITE
                            } else if chars > 0 {
                                Color32::from_gray(235)
                            } else {
                                Color32::from_gray(135)
                            };
                            let btn = egui::Button::new(
                                RichText::new(format!("{slot}\n{summary}")).size(13.0).color(fg),
                            )
                            .min_size(egui::vec2(104.0, 56.0))
                            .fill(if sel {
                                Color32::from_rgb(38, 110, 66)
                            } else {
                                Color32::from_rgb(38, 40, 45)
                            });
                            if ui.add(btn).clicked() {
                                st.text_slot = slot;
                            }
                        }
                    });
                    ui.add_space(6.0);
                    ui.collapsing("更多槽（5–10）", |ui| {
                        ui.horizontal_wrapped(|ui| {
                            for slot in 5..=10u32 {
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
    if st.wheel_open {
        wheel_overlay(ctx, st, &mut result);
    }
    if !open {
        return Some(None);
    }
    result
}

/// 4 方向文本轮盘（模态浮层）：↑ 槽1 / → 槽2 / ↓ 槽3 / ← 槽4，中心 = 直接输入提示。
///
/// 确认方式三选一：方向键 + Enter、鼠标点扇区、数字键 1–4。
/// 确认后直接把绑定值写回 `result`，不要求再点弹窗里的「确定」。
fn wheel_overlay(ctx: &egui::Context, st: &mut EditState, result: &mut EditResult) {
    use egui::{Align2, Color32, FontId, Key, Pos2, Sense, Shape, Stroke};

    let screen = ctx.screen_rect();
    let center = screen.center();
    let r_out = 168.0f32;
    let r_in = 74.0f32;

    // 键盘：方向键 / 数字 / Esc / Enter
    ctx.input(|i| {
        if i.key_pressed(Key::ArrowUp) || i.key_pressed(Key::Num1) { st.wheel_sel = 0; }
        if i.key_pressed(Key::ArrowRight) || i.key_pressed(Key::Num2) { st.wheel_sel = 1; }
        if i.key_pressed(Key::ArrowDown) || i.key_pressed(Key::Num3) { st.wheel_sel = 2; }
        if i.key_pressed(Key::ArrowLeft) || i.key_pressed(Key::Num4) { st.wheel_sel = 3; }
        if i.key_pressed(Key::Escape) { st.wheel_open = false; }
        if i.key_pressed(Key::Enter) {
            *result = Some(Some(format!("{}", -1000 - (st.wheel_sel as i64 + 1))));
            st.wheel_open = false;
        }
    });
    if !st.wheel_open {
        return;
    }

    let area = egui::Area::new(egui::Id::new("text_wheel"))
        .order(egui::Order::Foreground)
        .fixed_pos(screen.min);
    area.show(ctx, |ui| {
        let resp = ui.interact(screen, ui.id().with("wheel_bg"), Sense::click());
        let p = ui.painter();
        p.rect_filled(screen, 0.0, Color32::from_black_alpha(150));

        let labels = [(0u8, "↑"), (1, "→"), (2, "↓"), (3, "←")];
        for (idx, arrow) in labels {
            let a0 = (-135.0 + idx as f32 * 90.0).to_radians();
            let a1 = a0 + 90f32.to_radians();
            // 扇区多边形
            let mut pts: Vec<Pos2> = Vec::new();
            let steps = 20;
            for k in 0..=steps {
                let t = a0 + (a1 - a0) * k as f32 / steps as f32;
                pts.push(center + egui::vec2(t.cos() * r_out, t.sin() * r_out));
            }
            for k in (0..=steps).rev() {
                let t = a0 + (a1 - a0) * k as f32 / steps as f32;
                pts.push(center + egui::vec2(t.cos() * r_in, t.sin() * r_in));
            }
            let hot = st.wheel_sel == idx;
            let fill = if hot { Color32::from_rgb(28, 110, 68) } else { Color32::from_rgb(34, 36, 40) };
            let stroke = Stroke::new(2.0_f32, Color32::from_rgb(58, 62, 68));
            p.add(Shape::convex_polygon(pts, fill, stroke));

            // 文字：箭头 + 槽号 + 摘要
            let mid = (a0 + a1) / 2.0;
            let tx = center + egui::vec2(mid.cos() * (r_in + r_out) / 2.0, mid.sin() * (r_in + r_out) / 2.0);
            let slot = idx as u32 + 1;
            let t = crate::text_slot::get(slot);
            let chars = t.as_ref().map(|x| x.text.chars().count()).unwrap_or(0);
            let summary: String = if chars > 0 {
                let mut s: String = t.as_ref().map(|x| x.text.chars().take(8).collect()).unwrap_or_default();
                if chars > 8 { s.push('…'); }
                s
            } else { "未设置".to_string() };
            p.text(tx + egui::vec2(0.0, -16.0), Align2::CENTER_CENTER, format!("{arrow}  {slot} 号"),
                   FontId::proportional(17.0), if hot { Color32::WHITE } else { Color32::from_gray(200) });
            p.text(tx + egui::vec2(0.0, 8.0), Align2::CENTER_CENTER, summary,
                   FontId::proportional(14.0), if hot { Color32::WHITE } else { Color32::from_gray(150) });
        }

        // 中心
        p.circle_filled(center, r_in, Color32::from_rgb(24, 26, 30));
        p.circle_stroke(center, r_in, Stroke::new(2.0_f32, Color32::from_rgb(62, 66, 72)));
        p.text(center + egui::vec2(0.0, -22.0), Align2::CENTER_CENTER, format!("当前按键 · {}", st.label),
               FontId::proportional(13.0), Color32::from_gray(150));
        p.text(center + egui::vec2(0.0, 4.0), Align2::CENTER_CENTER, "↑↓←→ 选择",
               FontId::proportional(17.0), Color32::from_rgb(120, 200, 150));
        p.text(center + egui::vec2(0.0, 26.0), Align2::CENTER_CENTER, "Enter 确认 · Esc 取消",
               FontId::proportional(12.5), Color32::from_gray(140));

        // 鼠标命中
        if resp.clicked() {
            if let Some(pos) = resp.interact_pointer_pos() {
                let v = pos - center;
                let r = v.length();
                if r < r_in {
                    // 点中心：回「文本」页编辑（轮盘收起，不绑定）
                    st.wheel_open = false;
                    st.tab = EditTab::Text;
                } else if r <= r_out {
                    let mut a = v.y.atan2(v.x).to_degrees();
                    if a < -135.0 { a += 360.0; }
                    let idx = (((a + 135.0) / 90.0).floor() as i32).clamp(0, 3) as u8;
                    *result = Some(Some(format!("{}", -1000 - (idx as i64 + 1))));
                    st.wheel_open = false;
                } else {
                    st.wheel_open = false; // 点空白 = 取消
                }
            }
        }
    });
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

    // ★ 文本轮盘入口（2026-11-10）：与"动作"并列 —— 绑一段文字也是"这个键干什么"。
    //   点开后在界面上弹一个 4 方向轮盘，↑↓←→ 选、Enter 确认即完成绑定。
    ui.add_space(6.0);
    ui.horizontal(|ui| {
        if ui
            .add(egui::Button::new(RichText::new("◎ 文本轮盘").color(Color32::from_rgb(90, 190, 130)))
                .min_size(egui::vec2(bw * 3.6, bh)))
            .on_hover_text("上下左右选一条文本，确认后按下这个键就输出它")
            .clicked()
        {
            st.wheel_open = true;
            st.wheel_sel = 0;
        }
        ui.label(RichText::new("（1–4 号文本，内容在「文本」页编辑）").small().weak());
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
