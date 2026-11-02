//! Win32 精简面板 —— **原型**（2026-11-02）
//!
//! 用途：显卡驱动缺失 / 远程桌面 / 老集显 时，OpenGL 界面起不来，用这个**纯 GDI** 面板兜底。
//! 目标：零图形依赖，能看状态、能改最常用的几项、能导出诊断信息。
//!
//! 这是原型：数据写死为样例，只用于评估布局与信息密度。
//! 构建：`cargo build --release --bin panel`
//! 截图：`shot.exe "IPRC1000 · 精简面板" panel.png`

use windows::core::PCWSTR;
use windows::Win32::Foundation::{COLORREF, HINSTANCE, HWND, LPARAM, LRESULT, RECT, WPARAM};
use windows::Win32::Graphics::Gdi::{
    CreateFontW, CreateSolidBrush, DeleteObject, FillRect, SetBkMode, SetTextColor, UpdateWindow,
    CLIP_DEFAULT_PRECIS, DEFAULT_CHARSET, DEFAULT_PITCH, FF_DONTCARE, FW_BOLD, FW_NORMAL, HBRUSH,
    HDC, HFONT, HGDIOBJ, OUT_DEFAULT_PRECIS, PROOF_QUALITY, TRANSPARENT,
};
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::UI::Controls::{LVCF_TEXT, LVCF_WIDTH, LVCOLUMNW, LVIF_TEXT, LVITEMW, LVS_REPORT};
use windows::Win32::UI::WindowsAndMessaging::HMENU;
use windows::Win32::UI::WindowsAndMessaging::*;

const W: i32 = 780;
const H: i32 = 604;

// 深色配色（COLORREF = 0x00BBGGRR）
const C_BG: u32 = 0x001E1E1E;
const C_TEXT: u32 = 0x00E6E6E6;
const C_DIM: u32 = 0x009A9A9A;
const C_OK: u32 = 0x0078C850;
const C_WARN: u32 = 0x0030A0E0;
const C_ACCENT: u32 = 0x00E0A050;

#[link(name = "uxtheme")]
extern "system" {
    /// 关掉控件主题：否则单选/复选框的文字颜色由主题决定（黑字），
    /// WM_CTLCOLORBTN 里设的颜色不生效，深色底上等于看不清。
    fn SetWindowTheme(hwnd: HWND, sub_app: PCWSTR, sub_id: PCWSTR) -> windows::core::HRESULT;
}

static mut BRUSH_BG: HBRUSH = HBRUSH(0);
static mut FONT_N: HFONT = HFONT(0);
static mut FONT_B: HFONT = HFONT(0);
static mut TEXT_COLORS: Vec<(isize, u32)> = Vec::new();

fn wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

fn set_color(h: HWND, c: u32) {
    unsafe { TEXT_COLORS.push((h.0, c)) };
}

fn color_of(h: HWND) -> Option<u32> {
    unsafe { TEXT_COLORS.iter().find(|(k, _)| *k == h.0).map(|(_, c)| *c) }
}

unsafe fn make_font(size: i32, weight: i32) -> HFONT {
    let face = wide("Microsoft YaHei UI");
    CreateFontW(
        size,
        0,
        0,
        0,
        weight,
        0,
        0,
        0,
        DEFAULT_CHARSET.0 as u32,
        OUT_DEFAULT_PRECIS.0 as u32,
        CLIP_DEFAULT_PRECIS.0 as u32,
        PROOF_QUALITY.0 as u32,
        u32::from(FF_DONTCARE.0) | u32::from(DEFAULT_PITCH.0),
        PCWSTR(face.as_ptr()),
    )
}

unsafe fn label(parent: HWND, text: &str, x: i32, y: i32, w: i32, h: i32, color: u32, bold: bool) -> HWND {
    let t = wide(text);
    let c = wide("STATIC");
    let inst = GetModuleHandleW(None).unwrap_or_default();
    let hw = CreateWindowExW(
        WINDOW_EX_STYLE(0),
        PCWSTR(c.as_ptr()),
        PCWSTR(t.as_ptr()),
        WS_CHILD | WS_VISIBLE,
        x,
        y,
        w,
        h,
        parent,
        HMENU(0),
        HINSTANCE(inst.0),
        None,
    );
    SendMessageW(
        hw,
        WM_SETFONT,
        WPARAM(if bold { FONT_B.0 as usize } else { FONT_N.0 as usize }),
        LPARAM(1),
    );
    set_color(hw, color);
    hw
}

unsafe fn push_button(parent: HWND, text: &str, id: usize, x: i32, y: i32, w: i32, h: i32) -> HWND {
    let t = wide(text);
    let c = wide("BUTTON");
    let inst = GetModuleHandleW(None).unwrap_or_default();
    let hw = CreateWindowExW(
        WINDOW_EX_STYLE(0),
        PCWSTR(c.as_ptr()),
        PCWSTR(t.as_ptr()),
        WS_CHILD | WS_VISIBLE | WS_TABSTOP,
        x,
        y,
        w,
        h,
        parent,
        HMENU(id as isize),
        HINSTANCE(inst.0),
        None,
    );
    SendMessageW(hw, WM_SETFONT, WPARAM(FONT_N.0 as usize), LPARAM(1));
    hw
}

unsafe fn toggle(parent: HWND, text: &str, id: usize, x: i32, y: i32, w: i32, kind: WINDOW_STYLE, checked: bool) -> HWND {
    let t = wide(text);
    let c = wide("BUTTON");
    let inst = GetModuleHandleW(None).unwrap_or_default();
    let hw = CreateWindowExW(
        WINDOW_EX_STYLE(0),
        PCWSTR(c.as_ptr()),
        PCWSTR(t.as_ptr()),
        WS_CHILD | WS_VISIBLE | WS_TABSTOP | kind,
        x,
        y,
        w,
        24,
        parent,
        HMENU(id as isize),
        HINSTANCE(inst.0),
        None,
    );
    SendMessageW(hw, WM_SETFONT, WPARAM(FONT_N.0 as usize), LPARAM(1));
    if checked {
        SendMessageW(hw, BM_SETCHECK, WPARAM(1), LPARAM(0));
    }
    // 关主题 ⇒ 文字颜色由我们接管（深底浅字）
    let _ = SetWindowTheme(hw, PCWSTR(wide("").as_ptr()), PCWSTR(wide("").as_ptr()));
    hw
}

/// 映射表（ListView 报表模式）
unsafe fn list_view(parent: HWND, x: i32, y: i32, w: i32, h: i32) -> HWND {
    let c = wide("SysListView32");
    let inst = GetModuleHandleW(None).unwrap_or_default();
    let hw = CreateWindowExW(
        WS_EX_CLIENTEDGE,
        PCWSTR(c.as_ptr()),
        PCWSTR::null(),
        WS_CHILD | WS_VISIBLE | WINDOW_STYLE(LVS_REPORT),
        x,
        y,
        w,
        h,
        parent,
        HMENU(0),
        HINSTANCE(inst.0),
        None,
    );
    SendMessageW(hw, WM_SETFONT, WPARAM(FONT_N.0 as usize), LPARAM(1));

    // 深色（ListView 支持直接设颜色，不需要自绘）
    const LVM_SETBKCOLOR: u32 = 0x1000 + 1;
    const LVM_SETTEXTCOLOR: u32 = 0x1000 + 36;
    const LVM_SETTEXTBKCOLOR: u32 = 0x1000 + 38;
    SendMessageW(hw, LVM_SETBKCOLOR, WPARAM(0), LPARAM(C_BG as isize));
    SendMessageW(hw, LVM_SETTEXTBKCOLOR, WPARAM(0), LPARAM(C_BG as isize));
    SendMessageW(hw, LVM_SETTEXTCOLOR, WPARAM(0), LPARAM(C_TEXT as isize));

    const LVM_INSERTCOLUMNW: u32 = 0x1000 + 97;
    const LVM_INSERTITEMW: u32 = 0x1000 + 77;
    const LVM_SETITEMTEXTW: u32 = 0x1000 + 116;

    let heads = [("按键", 140i32), ("动作", 220), ("说明", 340)];
    for (i, (t, wd)) in heads.iter().enumerate() {
        let mut txt = wide(t);
        let mut col = LVCOLUMNW {
            mask: LVCF_TEXT | LVCF_WIDTH,
            cx: *wd,
            pszText: windows::core::PWSTR(txt.as_mut_ptr()),
            ..Default::default()
        };
        SendMessageW(hw, LVM_INSERTCOLUMNW, WPARAM(i), LPARAM(&mut col as *mut _ as isize));
    }

    let rows = [
        ("语音键", "按住右 Alt", "按住说话 / 松手结束"),
        ("上 / 下", "方向键 ↑ / ↓", ""),
        ("左 / 右", "方向键 ← / →", ""),
        ("OK", "回车", ""),
        ("返回", "Esc", ""),
        ("音量 + / −", "音量加 / 减", "系统媒体键"),
        ("主页", "宏：打开豆包", "循环 1 次"),
    ];
    for (r, (a, b, c2)) in rows.iter().enumerate() {
        let mut ta = wide(a);
        let mut item = LVITEMW {
            mask: LVIF_TEXT,
            iItem: r as i32,
            iSubItem: 0,
            pszText: windows::core::PWSTR(ta.as_mut_ptr()),
            ..Default::default()
        };
        SendMessageW(hw, LVM_INSERTITEMW, WPARAM(0), LPARAM(&mut item as *mut _ as isize));
        for (sub, txt) in [(1i32, b), (2, c2)] {
            let mut t = wide(txt);
            let mut it = LVITEMW {
                mask: LVIF_TEXT,
                iItem: r as i32,
                iSubItem: sub,
                pszText: windows::core::PWSTR(t.as_mut_ptr()),
                ..Default::default()
            };
            SendMessageW(hw, LVM_SETITEMTEXTW, WPARAM(r), LPARAM(&mut it as *mut _ as isize));
        }
    }
    hw
}

unsafe extern "system" fn wndproc(hwnd: HWND, msg: u32, wp: WPARAM, lp: LPARAM) -> LRESULT {
    match msg {
        WM_CREATE => {
            BRUSH_BG = CreateSolidBrush(COLORREF(C_BG));
            FONT_N = make_font(-14, FW_NORMAL.0 as i32);
            FONT_B = make_font(-16, FW_BOLD.0 as i32);

            // ── 环境状态 ──
            label(hwnd, "环境状态", 24, 18, 200, 24, C_TEXT, true);
            push_button(hwnd, "一键修复", 101, 632, 14, 120, 30);

            label(hwnd, "✓  虚拟声卡", 28, 60, 130, 22, C_OK, false);
            label(hwnd, "已安装且健康", 170, 60, 560, 22, C_DIM, false);
            label(hwnd, "✓  默认麦克风", 28, 84, 130, 22, C_OK, false);
            label(hwnd, "已指向 CABLE Output", 170, 84, 560, 22, C_DIM, false);
            label(hwnd, "⚠  虚拟键盘驱动", 28, 108, 130, 22, C_WARN, false);
            label(hwnd, "驱动已装，但设备槽位被占用（关掉重复实例即可）", 170, 108, 580, 22, C_DIM, false);
            label(hwnd, "✓  遥控器", 28, 132, 130, 22, C_OK, false);
            label(hwnd, "已不是键盘（不会打出字母）", 170, 132, 560, 22, C_DIM, false);

            // ── 语音 ──
            label(hwnd, "语音触发", 24, 176, 200, 22, C_TEXT, true);
            toggle(hwnd, "按住说话（松开结束）", 201, 28, 200, 210, WS_CHILD | WINDOW_STYLE(0x0000_0009), true);
            toggle(hwnd, "点按一次（松手自动结束）", 202, 244, 200, 260, WS_CHILD | WINDOW_STYLE(0x0000_0009), false);
            toggle(hwnd, "保存运行日志（排查问题时需要）", 203, 28, 236, 400, WS_CHILD | WINDOW_STYLE(0x0000_0003), true);

            // ── 映射表 ──
            label(hwnd, "按键映射（只读预览）", 24, 276, 260, 22, C_TEXT, true);
            label(hwnd, "改映射请在有显卡的机器上用完整界面", 320, 276, 420, 22, C_DIM, false);
            list_view(hwnd, 24, 304, 728, 208);

            // ── 底部 ──
            push_button(hwnd, "打开日志目录", 301, 24, 522, 132, 32);
            push_button(hwnd, "导出配置", 302, 168, 522, 112, 32);
            push_button(hwnd, "复制诊断信息", 303, 292, 522, 132, 32);
            push_button(hwnd, "收进托盘", 304, 620, 522, 132, 32);

            label(
                hwnd,
                "本机无法创建 OpenGL 图形界面，已切换到精简面板 —— 按键、语音、音频仍正常工作。",
                24,
                566,
                728,
                20,
                C_DIM,
                false,
            );
            LRESULT(0)
        }
        WM_CTLCOLORSTATIC => {
            let hctl = HWND(lp.0);
            let hdc = HDC(wp.0 as isize);
            SetBkMode(hdc, TRANSPARENT);
            SetTextColor(hdc, COLORREF(color_of(hctl).unwrap_or(C_TEXT)));
            LRESULT(BRUSH_BG.0 as isize)
        }
        WM_CTLCOLORBTN => {
            let hdc = HDC(wp.0 as isize);
            SetBkMode(hdc, TRANSPARENT);
            SetTextColor(hdc, COLORREF(C_TEXT));
            LRESULT(BRUSH_BG.0 as isize)
        }
        WM_ERASEBKGND => {
            let hdc = HDC(wp.0 as isize);
            let mut rc = RECT::default();
            let _ = GetClientRect(hwnd, &mut rc);
            FillRect(hdc, &rc, BRUSH_BG);
            let line = RECT { left: 24, top: 164, right: W - 40, bottom: 165 };
            let lb = CreateSolidBrush(COLORREF(0x00484848));
            FillRect(hdc, &line, lb);
            let _ = DeleteObject(HGDIOBJ(lb.0));
            LRESULT(1)
        }
        WM_COMMAND => {
            let id = (wp.0 & 0xffff) as usize;
            if id == 304 {
                let _ = ShowWindow(hwnd, SW_HIDE);
            }
            LRESULT(0)
        }
        WM_DESTROY => {
            let _ = DeleteObject(HGDIOBJ(BRUSH_BG.0));
            let _ = DeleteObject(HGDIOBJ(FONT_N.0));
            let _ = DeleteObject(HGDIOBJ(FONT_B.0));
            PostQuitMessage(0);
            LRESULT(0)
        }
        _ => DefWindowProcW(hwnd, msg, wp, lp),
    }
}

fn main() {
    unsafe {
        let inst = GetModuleHandleW(None).unwrap_or_default();
        let cls = wide("IprcPanel");
        let wc = WNDCLASSW {
            style: CS_HREDRAW | CS_VREDRAW,
            lpfnWndProc: Some(wndproc),
            hInstance: HINSTANCE(inst.0),
            hCursor: LoadCursorW(HINSTANCE(0), IDC_ARROW).unwrap_or_default(),
            hbrBackground: HBRUSH(0),
            lpszClassName: PCWSTR(cls.as_ptr()),
            ..Default::default()
        };
        RegisterClassW(&wc);

        let title = wide("IPRC1000 · 精简面板");
        let hwnd = CreateWindowExW(
            WINDOW_EX_STYLE(0),
            PCWSTR(cls.as_ptr()),
            PCWSTR(title.as_ptr()),
            WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX,
            CW_USEDEFAULT,
            CW_USEDEFAULT,
            W,
            H,
            HWND(0),
            HMENU(0),
            HINSTANCE(inst.0),
            None,
        );
        if hwnd.0 == 0 {
            eprintln!("创建窗口失败");
            return;
        }
        let _ = ShowWindow(hwnd, SW_SHOW);
        let _ = UpdateWindow(hwnd);

        let mut msg = MSG::default();
        while GetMessageW(&mut msg, HWND(0), 0, 0).as_bool() {
            let _ = TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
    }
}
