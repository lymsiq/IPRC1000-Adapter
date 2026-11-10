//! 运行时文本轮盘（2026-11-10）
//!
//! ## 交互（定稿）
//!
//! ```text
//! 按映射键（绑「文本轮盘」）→ 圆盘弹出（跟随输入光标 / 鼠标位置）
//! ↑=1 号  →=2 号  ↓=3 号  ←=4 号   按下即输入
//! 返回键 = 取消
//! ```
//!
//! ## 为什么用 SetWindowRgn 裁圆
//!
//! `with_transparent(true)` 在 Windows + glow 后端的 secondary viewport 上**不生效**
//! （实测仍是深色方块底）。所以改成：窗口照常不透明，但用 `SetWindowRgn` +
//! `CreateEllipticRgn` 把窗口区裁成圆 —— 方块的四个角直接不存在，视觉上就是圆盘。
//!
//! 代价是没有外发光（被裁掉），窗口留 6px 边当描边。
//!
//! ## 另外两点
//!
//! * **不抢焦点**：`with_active(false)`。抢了焦点，随后注入的文字会打进轮盘窗口。
//! * **自己催帧**：主窗口可能收在托盘，刷新很慢；激活期间 `request_repaint_after(33ms)`，
//!   否则按键改了状态、画面却不动（用户说的"卡顿"）。

use eframe::egui;
use std::sync::atomic::{AtomicBool, AtomicI32, AtomicIsize, Ordering};

/// 窗口边长（也是裁圆的直径）。轮盘直径比它小 12px，留一圈边当描边。
pub const WINDOW_SIZE: f32 = 330.0;

static OPEN: AtomicBool = AtomicBool::new(false);
static CUT_DONE: AtomicBool = AtomicBool::new(false);
static TARGET: AtomicI32 = AtomicI32::new(0);
static FOUND: AtomicIsize = AtomicIsize::new(0);

pub fn show(ctx: &egui::Context) {
    let id = egui::ViewportId::from_hash_of("text_wheel");
    let active = crate::backend::text_wheel_active();

    if active.is_none() {
        if OPEN.swap(false, Ordering::Relaxed) {
            ctx.send_viewport_cmd_to(id, egui::ViewportCommand::Close);
            CUT_DONE.store(false, Ordering::Relaxed);
        }
        return;
    }
    OPEN.store(true, Ordering::Relaxed);
    // 窗口句柄是异步创建的：没裁成功就下一帧再试
    if !CUT_DONE.load(Ordering::Relaxed) {
        cut_round();
    }
    ctx.request_repaint_after(std::time::Duration::from_millis(33));

    // 文本只在打开时取一次，别在每帧绘制里反复加锁 + clone
    let labels: [String; 4] = std::array::from_fn(|i| match crate::text_slot::get(i as u32 + 1) {
        Some(t) if !t.text.is_empty() => {
            let total = t.text.chars().count();
            let mut s: String = t.text.chars().take(10).collect();
            if total > 10 {
                s.push('…');
            }
            s
        }
        _ => "未设置".to_string(),
    });
    let empty: [bool; 4] = std::array::from_fn(|i| {
        !crate::text_slot::get(i as u32 + 1)
            .map(|t| !t.text.is_empty())
            .unwrap_or(false)
    });

    let (x, y) = crate::backend::text_wheel_pos().unwrap_or_else(|| {
        let (cx, cy) = screen_center();
        (cx - WINDOW_SIZE / 2.0, cy - WINDOW_SIZE / 2.0)
    });

    ctx.show_viewport_deferred(
        id,
        egui::ViewportBuilder::default()
            .with_inner_size([WINDOW_SIZE, WINDOW_SIZE])
            .with_position([x, y])
            .with_decorations(false)
            .with_resizable(false)
            .with_always_on_top()
            .with_active(false)
            .with_taskbar(false),
        move |ctx, _class| {
            egui::CentralPanel::default()
                .frame(
                    egui::Frame::none()
                        .fill(egui::Color32::from_rgb(20, 22, 26))
                        .inner_margin(egui::Margin::same(0.0)),
                )
                .show(ctx, |ui| draw(ui, &labels, &empty));
        },
    );
}

use windows::Win32::Foundation::{BOOL, HWND, LPARAM, RECT};

unsafe extern "system" fn enum_cb(hwnd: HWND, _l: LPARAM) -> BOOL {
    use windows::Win32::UI::WindowsAndMessaging::{GetWindowRect, GetWindowThreadProcessId};
    let mut pid = 0u32;
    let _ = GetWindowThreadProcessId(hwnd, Some(&mut pid));
    if pid != std::process::id() {
        return BOOL(1);
    }
    let mut r = RECT::default();
    if GetWindowRect(hwnd, &mut r).is_err() {
        return BOOL(1);
    }
    let s = TARGET.load(Ordering::Relaxed);
    if (r.right - r.left - s).abs() <= 3 && (r.bottom - r.top - s).abs() <= 3 {
        FOUND.store(hwnd.0 as isize, Ordering::Relaxed);
        return BOOL(0);
    }
    BOOL(1)
}

/// 把窗口区裁成圆。找不到窗口就下次再试。
fn cut_round() {
    use windows::Win32::Graphics::Gdi::{CreateEllipticRgn, SetWindowRgn};
    use windows::Win32::UI::WindowsAndMessaging::EnumWindows;

    let s = WINDOW_SIZE as i32;
    TARGET.store(s, Ordering::Relaxed);
    FOUND.store(0, Ordering::Relaxed);
    unsafe {
        let _ = EnumWindows(Some(enum_cb), LPARAM(0));
        let h = FOUND.load(Ordering::Relaxed);
        if h == 0 {
            return;
        }
        let rgn = CreateEllipticRgn(0, 0, s, s);
        if rgn.is_invalid() {
            return;
        }
        if SetWindowRgn(HWND(h), rgn, true) != 0 {
            CUT_DONE.store(true, Ordering::Relaxed);
        }
    }
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
    let r_out = 156.0f32;
    let r_in = 66.0f32;
    let p = ui.painter();

    p.circle_filled(center, r_out, Color32::from_rgb(30, 33, 38));
    p.circle_stroke(
        center,
        r_out,
        Stroke::new(1.0_f32, Color32::from_rgb(58, 64, 72)),
    );

    let arrows = ["↑", "→", "↓", "←"];
    for i in 0..4u8 {
        // 间隙 1.2°：再大就像四块拼图
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
        p.add(Shape::convex_polygon(
            pts,
            if empty[i as usize] {
                Color32::from_rgb(34, 37, 42)
            } else {
                Color32::from_rgb(44, 48, 55)
            },
            Stroke::new(1.0_f32, Color32::from_rgb(60, 66, 74)),
        ));

        let mid = (a0 + a1) / 2.0;
        let at = center
            + egui::vec2(mid.cos() * (r_in + r_out) / 2.0, mid.sin() * (r_in + r_out) / 2.0);
        p.text(
            at + egui::vec2(0.0, -18.0),
            Align2::CENTER_CENTER,
            format!("{}  {} 号", arrows[i as usize], i + 1),
            FontId::proportional(18.0),
            Color32::from_gray(220),
        );
        p.text(
            at + egui::vec2(0.0, 6.0),
            Align2::CENTER_CENTER,
            &labels[i as usize],
            FontId::proportional(13.5),
            if empty[i as usize] {
                Color32::from_gray(118)
            } else {
                Color32::from_gray(170)
            },
        );
    }

    p.circle_filled(center, r_in, Color32::from_rgb(24, 26, 31));
    p.circle_stroke(center, r_in, Stroke::new(1.0_f32, Color32::from_rgb(66, 72, 80)));
    p.text(
        center + egui::vec2(0.0, -12.0),
        Align2::CENTER_CENTER,
        "按方向键直接输入",
        FontId::proportional(12.0),
        Color32::from_gray(160),
    );
    p.text(
        center + egui::vec2(0.0, 10.0),
        Align2::CENTER_CENTER,
        "返回键取消",
        FontId::proportional(11.5),
        Color32::from_gray(130),
    );
}
