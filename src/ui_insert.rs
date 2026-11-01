//! 插入按键弹框（2026-10-31，egui 版）
//!
//! 对应 C# 版宏编辑器里的「+ 按键」：按下要录的键 → 显示当前组合 → 选按下/弹起/双向
//! → 设按住时长（默认 50 ms）→ 插入为一步宏。
//!
//! 为什么用 egui 的输入事件而不是低级钩子：
//!   · 这只是"在弹框里录一个键"，窗口有焦点就够了；
//!   · 钩子要独立消息循环、要处理注入过滤、卸载时机也麻烦 —— 为录一个键不值得。
//!   （录制整段宏仍然用钩子，那是另一回事。）

use eframe::egui::{self, Color32, RichText};

use crate::keymap;

pub struct InsertKeyState {
    pub open: bool,
    /// 已录到的组合（有序：修饰键在前）
    pub vks: Vec<u16>,
    pub waiting: bool,
    /// 0 = 按下+弹起，1 = 只按下，2 = 只弹起
    pub mode: u8,
    /// 按住时长（ms）
    pub hold_ms: u64,
}

impl Default for InsertKeyState {
    fn default() -> Self {
        Self { open: false, vks: Vec::new(), waiting: true, mode: 0, hold_ms: 50 }
    }
}

/// egui 的按键 → Windows VK
fn key_to_vk(k: egui::Key) -> Option<u16> {
    use egui::Key as K;
    Some(match k {
        // 字母
        K::A => 0x41, K::B => 0x42, K::C => 0x43, K::D => 0x44, K::E => 0x45,
        K::F => 0x46, K::G => 0x47, K::H => 0x48, K::I => 0x49, K::J => 0x4A,
        K::K => 0x4B, K::L => 0x4C, K::M => 0x4D, K::N => 0x4E, K::O => 0x4F,
        K::P => 0x50, K::Q => 0x51, K::R => 0x52, K::S => 0x53, K::T => 0x54,
        K::U => 0x55, K::V => 0x56, K::W => 0x57, K::X => 0x58, K::Y => 0x59,
        K::Z => 0x5A,
        // 数字（主键盘区）
        K::Num0 => 0x30, K::Num1 => 0x31, K::Num2 => 0x32, K::Num3 => 0x33,
        K::Num4 => 0x34, K::Num5 => 0x35, K::Num6 => 0x36, K::Num7 => 0x37,
        K::Num8 => 0x38, K::Num9 => 0x39,
        // 功能键
        K::F1 => 0x70, K::F2 => 0x71, K::F3 => 0x72, K::F4 => 0x73,
        K::F5 => 0x74, K::F6 => 0x75, K::F7 => 0x76, K::F8 => 0x77,
        K::F9 => 0x78, K::F10 => 0x79, K::F11 => 0x7A, K::F12 => 0x7B,
        // 控制键
        K::Escape => 0x1B, K::Enter => 0x0D, K::Tab => 0x09, K::Backspace => 0x08,
        K::Space => 0x20, K::Delete => 0x2E, K::Insert => 0x2D,
        K::Home => 0x24, K::End => 0x23, K::PageUp => 0x21, K::PageDown => 0x22,
        K::ArrowUp => 0x26, K::ArrowDown => 0x28, K::ArrowLeft => 0x25, K::ArrowRight => 0x27,
        _ => return None,
    })
}

/// 渲染弹框。返回 `Some(步骤 JSON)` 表示要插入。
pub fn show(ctx: &egui::Context, st: &mut InsertKeyState) -> Option<serde_json::Value> {
    if !st.open {
        return None;
    }
    let mut result = None;
    let mut keep_open = st.open;

    // 等待按键期间：从 egui 的输入事件里抓
    if st.waiting {
        let mut got: Option<(u16, bool, bool, bool)> = None; // (vk, ctrl, alt, shift)
        ctx.input(|i| {
            let m = i.modifiers;
            for ev in &i.events {
                if let egui::Event::Key { key, pressed: true, .. } = ev {
                    // 单独按修饰键也允许（有些人就想绑 Ctrl）
                    let vk = match key {
                        egui::Key::Space if m.ctrl || m.alt || m.shift => continue,
                        k => match key_to_vk(*k) {
                            Some(v) => v,
                            None => continue,
                        },
                    };
                    got = Some((vk, m.ctrl, m.alt, m.shift));
                    break;
                }
            }
        });
        if let Some((vk, ctrl, alt, shift)) = got {
            st.vks.clear();
            if ctrl { st.vks.push(0x11); }
            if alt { st.vks.push(0x12); }
            if shift { st.vks.push(0x10); }
            st.vks.push(vk);
            st.waiting = false;
        }
    }

    egui::Window::new("插入按键")
        .open(&mut keep_open)
        .collapsible(false)
        .resizable(false)
        .default_width(360.0)
        .anchor(egui::Align2::CENTER_CENTER, [0.0, 0.0])
        .show(ctx, |ui| {
            ui.horizontal(|ui| {
                ui.label(RichText::new("当前按键：").weak());
                if st.waiting {
                    ui.label(RichText::new("请按下要录的键…").color(Color32::YELLOW));
                } else if st.vks.is_empty() {
                    ui.label(RichText::new("（还没录到）").weak());
                } else {
                    let txt = st.vks.iter().map(|v| keymap::vk_name(*v)).collect::<Vec<_>>().join("+");
                    ui.label(RichText::new(txt).strong().color(Color32::from_rgb(120, 200, 255)));
                }
                if ui.button("重录").clicked() {
                    st.waiting = true;
                    st.vks.clear();
                }
            });
            ui.add_space(8.0);

            ui.label(RichText::new("动作").strong());
            ui.horizontal(|ui| {
                ui.selectable_value(&mut st.mode, 0, "按下 + 弹起");
                ui.selectable_value(&mut st.mode, 1, "只按下");
                ui.selectable_value(&mut st.mode, 2, "只弹起");
            });
            ui.add_space(6.0);

            ui.horizontal(|ui| {
                ui.label("按住时长：");
                let mut v = st.hold_ms as i32;
                if ui.add(egui::DragValue::new(&mut v).range(10..=5000).suffix(" ms")).changed() {
                    st.hold_ms = v.max(10) as u64;
                }
                ui.label(RichText::new("（按下后等这么久再弹起）").small().weak());
            });
            ui.add_space(8.0);

            ui.horizontal(|ui| {
                let can = !st.vks.is_empty();
                if ui.add_enabled(can, egui::Button::new("插入这一步")).clicked() {
                    let (press, hold) = match st.mode {
                        1 => ("down", 0),
                        2 => ("up", 0),
                        _ => ("both", st.hold_ms),
                    };
                    let sym = st.vks.iter().map(|v| keymap::vk_name(*v)).collect::<Vec<_>>().join("+");
                    result = Some(serde_json::json!({
                        "type": "keys",
                        "vks": st.vks,
                        "sym": sym,
                        "press": press,
                        "holdMs": hold,
                        "ms": 0,
                        "slot": 1,
                    }));
                    st.open = false;
                }
                if ui.button("取消").clicked() {
                    st.open = false;
                }
            });
        });

    if !keep_open {
        st.open = false;
    }
    result
}
