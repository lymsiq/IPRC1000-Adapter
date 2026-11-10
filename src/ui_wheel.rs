//! 运行时文本轮盘（2026-11-10）
//!
//! 与"绑定期的选择框"不是一回事：
//!
//! ```text
//! 绑定期：改键弹窗里选一条文本 → 这个键以后输出它
//! 运行时：按下绑了「文本轮盘」的键 → 屏幕上弹圆盘 → 方向键选 → 输入
//! ```
//!
//! ## 三个必须做对的地方
//!
//! 1. **不抢焦点**：`with_active(false)`。抢了焦点，随后注入的文字会打进轮盘窗口。
//! 2. **透明**：`with_transparent(true)` + 面板填透明 —— 只留圆盘，不要深色方块底。
//! 3. **自己驱动重绘**：主窗口可能收在托盘，刷新很慢。激活期间主动
//!    `request_repaint_after(33ms)`，否则按键改了高亮、画面却不动（用户说"卡顿"）。
//!
//! 状态全部来自 `backend::text_wheel_active()`：按键拦截在 ETW 回调线程里做，
//! 这里只负责画，窗口画不出来也不影响按键逻辑。

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
    let _sel = active.unwrap_or(0);   // 按下即输入，不画高亮；保留读取以免状态未消费
    OPEN.store(true, Ordering::Relaxed);
    // 轮盘自己催帧：主窗口隐藏时也按 30fps 刷新
    ctx.request_repaint_after(std::time::Duration::from_millis(33));

    // 文本只在弹窗打开时取一次，别在每帧的绘制里反复加锁 + clone
    let labels: [String; 4] = std::array::from_fn(|i| {
        let slot = i as u32 + 1;
        match crate::text_slot::get(slot) {
            Some(t) if !t.text.is_empty() => {
                let total = t.text.chars().count();
                let mut s: String = t.text.chars().take(10).collect();
                if total > 10 {
                    s.push('…');
                }
                s
            }
            _ => "未设置".to_string(),
        }
    });
    let empty: [bool; 4] = std::array::from_fn(|i| !crate::text_slot::get(i as u32 + 1)
        .map(|t| !t.text.is_empty())
        .unwrap_or(false));

    let (cx, cy) = screen_center();
    let d = 372.0f32;
    ctx.show_viewport_deferred(
        id,
        egui::ViewportBuilder::default()
            .with_inner_size([d, d])
            .with_position([cx - d / 2.0, cy - d / 2.0])
            .with_decorations(false)
            .with_resizable(false)
            .with_always_on_top()
            .with_active(false)
            .with_taskbar(false)
            .with_transparent(true),
        move |ctx, _class| {
            egui::CentralPanel::default()
                .frame(egui::Frame::none().fill(egui::Color32::TRANSPARENT))
                .show(ctx, |ui| draw(ui, &labels, &empty));
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

fn draw(ui: &mut egui::Ui, labels: &[String; 4], empty: &[bool; 4]) {
    use egui::{Align2, Color32, FontId, Pos2, Shape, Stroke};

    let center = ui.max_rect().center();
    let r_out = 150.0f32;
    let r_in = 64.0f32;
    let p = ui.painter();

    // 柔和外光（多层半透明圆，代替方形底）
    for (i, a) in [(18.0f32, 10u8), (10.0, 16), (4.0, 22)] {
        p.circle_filled(center, r_out + i, Color32::from_black_alpha(a));
    }
    p.circle_filled(center, r_out, Color32::from_rgba_unmultiplied(26, 29, 34, 246));
    p.circle_stroke(
        center,
        r_out,
        Stroke::new(1.0_f32, Color32::from_rgba_unmultiplied(70, 76, 84, 200)),
    );

    let arrows = ["↑", "→", "↓", "←"];
    for i in 0..4u8 {
        // 间隙只留 1.2°：之前 3.6° 的缝把圆切成了三块，看着像"带黑框"
        let a0 = (-135.0 + i as f32 * 90.0 + 0.6).to_radians();
        let a1 = a0 + (90.0 - 1.2f32).to_radians();
        let mut pts: Vec<Pos2> = Vec::new();
        let steps = 26;
        for k in 0..=steps {
            let t = a0 + (a1 - a0) * k as f32 / steps as f32;
            pts.push(center + egui::vec2(t.cos() * r_out, t.sin() * r_out));
        }
        for k in (0..=steps).rev() {
            let t = a0 + (a1 - a0) * k as f32 / steps as f32;
            pts.push(center + egui::vec2(t.cos() * r_in, t.sin() * r_in));
        }
        // 按下即输入，所以没有"高亮"状态；只把空槽画暗一点
        p.add(Shape::convex_polygon(
            pts,
            if empty[i as usize] {
                Color32::from_rgb(32, 35, 40)
            } else {
                Color32::from_rgb(42, 46, 52)
            },
            Stroke::new(1.0_f32, Color32::from_rgb(62, 68, 76)),
        ));

        let mid = (a0 + a1) / 2.0;
        let at = center
            + egui::vec2(mid.cos() * (r_in + r_out) / 2.0, mid.sin() * (r_in + r_out) / 2.0);
        let slot = i as u32 + 1;
        let fg = Color32::from_gray(215);
        let fg2 = if empty[i as usize] {
            Color32::from_gray(118)
        } else {
            Color32::from_gray(170)
        };
        p.text(
            at + egui::vec2(0.0, -17.0),
            Align2::CENTER_CENTER,
            format!("{}  {} 号", arrows[i as usize], slot),
            FontId::proportional(18.0),
            fg,
        );
        p.text(
            at + egui::vec2(0.0, 6.0),
            Align2::CENTER_CENTER,
            &labels[i as usize],
            FontId::proportional(13.5),
            fg2,
        );
    }

    // 中心
    p.circle_filled(center, r_in, Color32::from_rgb(22, 24, 28));
    p.circle_stroke(
        center,
        r_in,
        Stroke::new(1.0_f32, Color32::from_rgba_unmultiplied(70, 76, 84, 180)),
    );
    p.text(
        center + egui::vec2(0.0, -13.0),
        Align2::CENTER_CENTER,
        "按方向键直接输入",
        FontId::proportional(11.5),
        Color32::from_gray(150),
    );
    p.text(
        center + egui::vec2(0.0, 8.0),
        Align2::CENTER_CENTER,
        "返回键取消",
        FontId::proportional(11.5),
        Color32::from_gray(130),
    );
}
