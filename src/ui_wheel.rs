//! 运行时文本轮盘（2026-11-10）
//!
//! 与"绑定期的选择框"不是一回事：
//!
//! ```text
//! 绑定期：改键弹窗里选一条文本 → 这个键以后输出它
//! 运行时：按下这个键 → 屏幕上弹轮盘 → 遥控器方向键选 → OK 输出   ← 本模块
//! ```
//!
//! ## 为什么必须独立窗口
//!
//! 主窗口可能被收进托盘（不可见），而轮盘要盖在任何前台程序上。
//! 所以用 egui 的 deferred viewport：无边框、置顶、**不抢焦点**
//! （`with_active(false)` —— 抢焦点会把随后注入的文字打到轮盘窗口里）。
//!
//! ## 状态从哪来
//!
//! 全部读 `backend::text_wheel_active()`：按键拦截与选择状态都在 ETW 回调线程里改，
//! UI 只负责画。这样即使窗口没画出来（显卡异常），按键逻辑仍然正确。

use eframe::egui;

static OPEN: std::sync::atomic::AtomicBool = std::sync::atomic::AtomicBool::new(false);

pub fn show(ctx: &egui::Context) {
    use std::sync::atomic::Ordering;
    let id = egui::ViewportId::from_hash_of("text_wheel");
    let active = crate::backend::text_wheel_active();

    if active.is_none() {
        if OPEN.swap(false, Ordering::Relaxed) {
            ctx.send_viewport_cmd_to(id, egui::ViewportCommand::Close);
        }
        return;
    }
    let sel = active.unwrap_or(0);
    OPEN.store(true, Ordering::Relaxed);

    let (cx, cy) = screen_center();
    let (w, h) = (380.0f32, 380.0f32);
    ctx.show_viewport_deferred(
        id,
        egui::ViewportBuilder::default()
            .with_inner_size([w, h])
            .with_position([cx - w / 2.0, cy - h / 2.0])
            .with_decorations(false)
            .with_resizable(false)
            .with_always_on_top()
            .with_active(false)
            .with_taskbar(false),
        move |ctx, _class| {
            egui::CentralPanel::default()
                .frame(
                    egui::Frame::none()
                        .fill(egui::Color32::from_rgb(22, 24, 28))
                        .inner_margin(egui::Margin::same(0.0)),
                )
                .show(ctx, |ui| draw(ui, sel));
        },
    );
}

#[cfg(target_os = "windows")]
fn screen_center() -> (f32, f32) {
    unsafe {
        use windows::Win32::UI::WindowsAndMessaging::{GetSystemMetrics, SM_CXSCREEN, SM_CYSCREEN};
        (
            GetSystemMetrics(SM_CXSCREEN) as f32 / 2.0,
            GetSystemMetrics(SM_CYSCREEN) as f32 / 2.0,
        )
    }
}

#[cfg(not(target_os = "windows"))]
fn screen_center() -> (f32, f32) {
    (960.0, 540.0)
}

fn draw(ui: &mut egui::Ui, sel: u8) {
    use egui::{Align2, Color32, FontId, Pos2, Shape, Stroke};

    let center = ui.max_rect().center();
    let r_out = 152.0f32;
    let r_in = 62.0f32;
    let p = ui.painter();

    // 底圆（让轮盘在任何背景上都可读）
    p.circle_filled(center, r_out + 14.0, Color32::from_rgb(28, 30, 35));
    p.circle_stroke(
        center,
        r_out + 14.0,
        Stroke::new(1.0_f32, Color32::from_rgb(58, 62, 70)),
    );

    let arrows = ["↑", "→", "↓", "←"];
    for i in 0..4u8 {
        let a0 = (-135.0 + i as f32 * 90.0).to_radians();
        let a1 = a0 + 90f32.to_radians();
        let mut pts: Vec<Pos2> = Vec::new();
        let steps = 24;
        for k in 0..=steps {
            let t = a0 + (a1 - a0) * k as f32 / steps as f32;
            pts.push(center + egui::vec2(t.cos() * r_out, t.sin() * r_out));
        }
        for k in (0..=steps).rev() {
            let t = a0 + (a1 - a0) * k as f32 / steps as f32;
            pts.push(center + egui::vec2(t.cos() * r_in, t.sin() * r_in));
        }
        let hot = sel == i;
        p.add(Shape::convex_polygon(
            pts,
            if hot { Color32::from_rgb(30, 122, 74) } else { Color32::from_rgb(36, 39, 44) },
            Stroke::new(1.5_f32, Color32::from_rgb(56, 60, 68)),
        ));

        let mid = (a0 + a1) / 2.0;
        let at = center
            + egui::vec2(mid.cos() * (r_in + r_out) / 2.0, mid.sin() * (r_in + r_out) / 2.0);
        let slot = i as u32 + 1;
        let t = crate::text_slot::get(slot);
        let chars = t.as_ref().map(|x| x.text.chars().count()).unwrap_or(0);
        let summary: String = if chars > 0 {
            let mut s: String = t
                .as_ref()
                .map(|x| x.text.chars().take(9).collect())
                .unwrap_or_default();
            if chars > 9 {
                s.push('…');
            }
            s
        } else {
            "未设置".to_string()
        };
        let fg = if hot { Color32::WHITE } else { Color32::from_gray(200) };
        let fg2 = if hot { Color32::from_rgb(226, 245, 234) } else { Color32::from_gray(140) };
        p.text(
            at + egui::vec2(0.0, -18.0),
            Align2::CENTER_CENTER,
            format!("{}  {} 号", arrows[i as usize], slot),
            FontId::proportional(19.0),
            fg,
        );
        p.text(at + egui::vec2(0.0, 6.0), Align2::CENTER_CENTER, summary, FontId::proportional(14.5), fg2);
    }

    // 中心
    p.circle_filled(center, r_in, Color32::from_rgb(24, 26, 30));
    p.circle_stroke(center, r_in, Stroke::new(1.5_f32, Color32::from_rgb(60, 64, 72)));
    p.text(
        center + egui::vec2(0.0, -16.0),
        Align2::CENTER_CENTER,
        "OK 输入 · 返回取消",
        FontId::proportional(12.5),
        Color32::from_gray(150),
    );
    p.text(
        center + egui::vec2(0.0, 6.0),
        Align2::CENTER_CENTER,
        "方向键选择",
        FontId::proportional(16.0),
        Color32::from_rgb(120, 200, 150),
    );
}
