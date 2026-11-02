//! IPRC1000 的 Win32 原生界面（GDI，零显卡依赖）
//!
//! 为什么存在：界面用 eframe + glow（OpenGL）。在**没有显卡驱动**的机器上
//! （设备管理器显示「Microsoft 基本显示适配器」、远程桌面、老集显）GL 上下文创建不出来，
//! egui 界面永远打不开。这个界面用纯 GDI 绘制，只依赖系统核心组件，
//! 在这些机器上仍能改键位、切输入法、触发修复。
//!
//! ## 与完整界面的分工
//!
//! | 能力 | 完整界面（egui） | 本界面（Win32） |
//! |---|---|---|
//! | 按键映射 | 全部（含动作/宏/文本槽） | 键位映射 + 自定义键 |
//! | 语音 | 全部 | 输入法（豆包/微信）+ 触发方式 |
//! | 宏 / 文本槽 / 鼠标 / 配置组 | 有 | **无**（只读显示） |
//!
//! ## 数据来源（不维护第二份定义）
//!
//! · 按键表：`keymap::usage_label()`（与完整界面同一张表）
//! · 当前映射：`keymap::vks_of()` / `action_of()`
//! · 写入：`crate::apply_binding()`（与完整界面同一个写入口，含持久化）
//! · 设置：`settings::set_ime / set_trigger / set_log_enabled`
//! · 状态：`commands::probes()` / `device_probe::winuhid_driver_installed()`

use windows::core::PCWSTR;
use windows::Win32::Foundation::{COLORREF, HINSTANCE, HWND, LPARAM, LRESULT, RECT, WPARAM};
use windows::Win32::Graphics::Gdi::{
    CreateFontW, CreateSolidBrush, DeleteObject, FillRect, InvalidateRect, SetBkMode, SetTextColor,
    UpdateWindow, CLIP_DEFAULT_PRECIS, DEFAULT_CHARSET, DEFAULT_PITCH, FF_DONTCARE, FW_BOLD,
    FW_NORMAL, HBRUSH, HDC, HFONT, HGDIOBJ, OUT_DEFAULT_PRECIS, PROOF_QUALITY, TRANSPARENT,
};
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::UI::Controls::{
    InitCommonControlsEx, ICC_LISTVIEW_CLASSES, INITCOMMONCONTROLSEX, LVCF_TEXT, LVCF_WIDTH,
    LVCOLUMNW, LVIF_TEXT, LVITEMW, LVS_REPORT,
};
use windows::Win32::UI::Input::KeyboardAndMouse::GetKeyState;
use windows::Win32::UI::Shell::{
    Shell_NotifyIconW, NIF_ICON, NIF_MESSAGE, NIF_TIP, NIM_ADD, NIM_DELETE, NOTIFYICONDATAW,
};
use windows::Win32::UI::WindowsAndMessaging::{HICON, HMENU, *};

const W: i32 = 836;
const H: i32 = 936;

const C_BG: u32 = 0x001E1E1E;
const C_TEXT: u32 = 0x00E6E6E6;
const C_DIM: u32 = 0x009A9A9A;
const C_OK: u32 = 0x0078C850;
const C_WARN: u32 = 0x0030A0E0;
const C_HINT: u32 = 0x00D0A040;

static mut BRUSH_BG: HBRUSH = HBRUSH(0);
static mut FONT_N: HFONT = HFONT(0);
static mut FONT_B: HFONT = HFONT(0);
static mut TEXT_COLORS: Vec<(isize, u32)> = Vec::new();
static mut LIST: HWND = HWND(0);
static mut HINT: HWND = HWND(0);
static mut TRAY_ICON: HICON = HICON(0);
/// 状态条上的 4 个标签（修复完成或启动后刷新文字）
static mut STATUS_LABELS: Vec<HWND> = Vec::new();
/// 正在展示的行：usage 列表（按固定顺序 + 自定义键追加在后）
static mut ROWS: Vec<u8> = Vec::new();
/// 捕获状态：0=空闲 1=等遥控器键（新增）2=等目标键
static mut CAP: u8 = 0;
static mut CAP_USAGE: u8 = 0;
/// 修复线程的回报（None=未在跑）
static mut REPAIR_MSG: Option<String> = None;

const ID_FIX: usize = 101;
const ID_ADD: usize = 403;
const ID_DEL: usize = 404;
const ID_REMAP: usize = 401;
const ID_DEFAULT: usize = 402;
const ID_HOLD: usize = 501;
const ID_TAP: usize = 502;
const ID_LOG: usize = 503;
const ID_IME_DOUBAO: usize = 511;
const ID_IME_WECHAT: usize = 512;
const ID_LOGDIR: usize = 601;
const ID_EXPORT: usize = 602;
const ID_TRAY: usize = 603;
const ID_MENU_OPEN: usize = 701;
const ID_MENU_QUIT: usize = 702;

const WM_TRAY: u32 = WM_APP + 1;
const WM_REPAIR_DONE: u32 = WM_APP + 2;

/// 界面上展示顺序（usage）。名字/说明一律从 keymap 取，这里只定顺序。
const ORDER: [u8; 23] = [
    0x17, 0x09, 0x11, 0x07, 0x0B, 0x28, 0x52, 0x51, 0x50, 0x4F, 0x08, 0x10, 0x0D, 0x31, 0x19,
    0x36, 0x04, 0x05, 0x1F, 0x20, 0x0A, 0x06, 0x16,
];

const VOICE: u8 = 0x10;
const ACCOUNT: u8 = 0x0D;

fn wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

fn set_color(h: HWND, c: u32) {
    unsafe { TEXT_COLORS.push((h.0, c)) };
}

fn color_of(h: HWND) -> Option<u32> {
    unsafe { TEXT_COLORS.iter().find(|(k, _)| *k == h.0).map(|(_, c)| *c) }
}

/// 该行能不能在本界面改
fn editable(u: u8) -> bool {
    u != VOICE && u != ACCOUNT
}

/// 说明列（只对不可改的行给出原因）
fn note_of(u: u8) -> &'static str {
    if u == VOICE {
        "由上方「唤醒」决定，全局不可单独改"
    } else if u == ACCOUNT {
        "文本输入，此界面不支持修改"
    } else {
        ""
    }
}

/// 当前映射的可读文本（动作 / 键序列 / 未设置）
fn map_text(u: u8) -> String {
    // 语音键由输入法决定，读 keymap（它是动态的）
    if u == VOICE {
        let vks = crate::keymap::vks_of(u);
        return vks
            .iter()
            .map(|v| crate::keymap::vk_name(*v))
            .collect::<Vec<_>>()
            .join("+");
    }
    // 其余一律以**配置里存的绑定字符串**为准 —— 与完整界面同一个数据源，
    // 否则「账户 = 文本槽 1」在这里会显示成"未设置"（实测过）。
    let b = crate::settings::binding_in_group(&crate::keymap::id_of(u));
    if !b.trim().is_empty() {
        if let Ok(v) = b.trim().parse::<i64>() {
            if v < 0 {
                return match v {
                    -6..=-1 => String::from("鼠标动作"),
                    -1010..=-1001 => format!("文本槽 {}", -1000 - v),
                    -1999..=-1101 => format!("宏 {}", -1100 - v),
                    _ => format!("动作 {v}"),
                };
            }
        }
        let vks: Vec<u16> = b
            .split(',')
            .filter_map(|x| x.trim().parse::<u16>().ok())
            .collect();
        if !vks.is_empty() {
            return vks
                .iter()
                .map(|v| crate::keymap::vk_name(*v))
                .collect::<Vec<_>>()
                .join("+");
        }
    }
    // 没绑定 ⇒ 显示出厂默认
    let vks = crate::keymap::vks_of(u);
    if vks.is_empty() {
        String::from("未设置")
    } else {
        vks.iter()
            .map(|v| crate::keymap::vk_name(*v))
            .collect::<Vec<_>>()
            .join("+")
    }
}

unsafe fn make_font(size: i32, weight: i32) -> HFONT {
    let face = wide("Microsoft YaHei UI");
    CreateFontW(
        size, 0, 0, 0, weight, 0, 0, 0,
        DEFAULT_CHARSET.0 as u32, OUT_DEFAULT_PRECIS.0 as u32,
        CLIP_DEFAULT_PRECIS.0 as u32, PROOF_QUALITY.0 as u32,
        u32::from(FF_DONTCARE.0) | u32::from(DEFAULT_PITCH.0),
        PCWSTR(face.as_ptr()),
    )
}

unsafe fn label(parent: HWND, text: &str, x: i32, y: i32, w: i32, h: i32, color: u32, bold: bool) -> HWND {
    let t = wide(text);
    let c = wide("STATIC");
    let inst = GetModuleHandleW(None).unwrap_or_default();
    let hw = CreateWindowExW(
        WINDOW_EX_STYLE(0), PCWSTR(c.as_ptr()), PCWSTR(t.as_ptr()),
        WS_CHILD | WS_VISIBLE, x, y, w, h, parent, HMENU(0), HINSTANCE(inst.0), None,
    );
    SendMessageW(hw, WM_SETFONT,
        WPARAM(if bold { FONT_B.0 as usize } else { FONT_N.0 as usize }), LPARAM(1));
    set_color(hw, color);
    hw
}

unsafe fn push_button(parent: HWND, text: &str, id: usize, x: i32, y: i32, w: i32, h: i32) -> HWND {
    let t = wide(text);
    let c = wide("BUTTON");
    let inst = GetModuleHandleW(None).unwrap_or_default();
    let hw = CreateWindowExW(
        WINDOW_EX_STYLE(0), PCWSTR(c.as_ptr()), PCWSTR(t.as_ptr()),
        WS_CHILD | WS_VISIBLE | WS_TABSTOP, x, y, w, h,
        parent, HMENU(id as isize), HINSTANCE(inst.0), None,
    );
    SendMessageW(hw, WM_SETFONT, WPARAM(FONT_N.0 as usize), LPARAM(1));
    hw
}

#[link(name = "uxtheme")]
extern "system" {
    fn SetWindowTheme(hwnd: HWND, sub_app: PCWSTR, sub_id: PCWSTR) -> windows::core::HRESULT;
}

unsafe fn toggle(parent: HWND, text: &str, id: usize, x: i32, y: i32, w: i32, kind: WINDOW_STYLE, checked: bool) -> HWND {
    let t = wide(text);
    let c = wide("BUTTON");
    let inst = GetModuleHandleW(None).unwrap_or_default();
    let hw = CreateWindowExW(
        WINDOW_EX_STYLE(0), PCWSTR(c.as_ptr()), PCWSTR(t.as_ptr()),
        WS_CHILD | WS_VISIBLE | WS_TABSTOP | kind, x, y, w, 24,
        parent, HMENU(id as isize), HINSTANCE(inst.0), None,
    );
    SendMessageW(hw, WM_SETFONT, WPARAM(FONT_N.0 as usize), LPARAM(1));
    if checked {
        SendMessageW(hw, BM_SETCHECK, WPARAM(1), LPARAM(0));
    }
    let empty = wide("");
    let _ = SetWindowTheme(hw, PCWSTR(empty.as_ptr()), PCWSTR(empty.as_ptr()));
    hw
}

unsafe fn make_list(parent: HWND, x: i32, y: i32, w: i32, h: i32) -> HWND {
    let c = wide("SysListView32");
    let inst = GetModuleHandleW(None).unwrap_or_default();
    let hw = CreateWindowExW(
        WS_EX_CLIENTEDGE, PCWSTR(c.as_ptr()), PCWSTR::null(),
        WS_CHILD | WS_VISIBLE | WINDOW_STYLE(LVS_REPORT), x, y, w, h,
        parent, HMENU(0), HINSTANCE(inst.0), None,
    );
    SendMessageW(hw, WM_SETFONT, WPARAM(FONT_N.0 as usize), LPARAM(1));
    SendMessageW(hw, 0x1000 + 1, WPARAM(0), LPARAM(C_BG as isize));
    SendMessageW(hw, 0x1000 + 38, WPARAM(0), LPARAM(C_BG as isize));
    SendMessageW(hw, 0x1000 + 36, WPARAM(0), LPARAM(C_TEXT as isize));
    SendMessageW(hw, 0x1000 + 54, WPARAM(0), LPARAM(0x0000_0020));

    let heads = [("遥控器按键", 180i32), ("HID", 70), ("映射到", 220), ("说明", 300)];
    for (i, (t, wd)) in heads.iter().enumerate() {
        let mut txt = wide(t);
        let mut col = LVCOLUMNW {
            mask: LVCF_TEXT | LVCF_WIDTH,
            cx: *wd,
            pszText: windows::core::PWSTR(txt.as_mut_ptr()),
            ..Default::default()
        };
        SendMessageW(hw, 0x1000 + 97, WPARAM(i), LPARAM(&mut col as *mut _ as isize));
    }
    hw
}

unsafe fn insert_row(hw: HWND, row: usize, first: &str) {
    let mut t = wide(first);
    let mut it = LVITEMW {
        mask: LVIF_TEXT, iItem: row as i32, iSubItem: 0,
        pszText: windows::core::PWSTR(t.as_mut_ptr()), ..Default::default()
    };
    SendMessageW(hw, 0x1000 + 77, WPARAM(0), LPARAM(&mut it as *mut _ as isize));
}

unsafe fn set_cell(hw: HWND, row: usize, sub: i32, text: &str) {
    let mut t = wide(text);
    let mut it = LVITEMW {
        mask: LVIF_TEXT, iItem: row as i32, iSubItem: sub,
        pszText: windows::core::PWSTR(t.as_mut_ptr()), ..Default::default()
    };
    SendMessageW(hw, 0x1000 + 116, WPARAM(row), LPARAM(&mut it as *mut _ as isize));
}

unsafe fn set_hint(text: &str, color: u32) {
    let h = HINT;
    let t = wide(text);
    SetWindowTextW(h, PCWSTR(t.as_ptr()));
    TEXT_COLORS.retain(|(k, _)| *k != h.0);
    set_color(h, color);
    let _ = InvalidateRect(h, None, true);
}

/// 重建"要显示哪些键"：内置 23 键 + 用户加过覆盖的自定义键
unsafe fn refresh_rows() {
    let mut v: Vec<u8> = ORDER.to_vec();
    for u in 0u16..256 {
        let u = u as u8;
        if ORDER.contains(&u) {
            continue;
        }
        let known = crate::keymap::usage_label(u).0 != "unknown";
        let touched = crate::keymap::action_of(u).is_some()
            || crate::keymap::overrides().contains_key(&u);
        if (!known && touched) || (known && touched) {
            v.push(u);
        }
    }
    ROWS = v;
}

unsafe fn rebuild_list() {
    refresh_rows();
    SendMessageW(LIST, 0x1000 + 9, WPARAM(0), LPARAM(0));
    let rows = ROWS.clone();
    for (i, u) in rows.iter().enumerate() {
        let u = *u;
        insert_row(LIST, i, &crate::keymap::name_of(u));
        set_cell(LIST, i, 1, &format!("HID {:02X}", u));
        set_cell(LIST, i, 2, &map_text(u));
        set_cell(LIST, i, 3, note_of(u));
    }
}

unsafe fn selected_usage() -> Option<u8> {
    let sel = SendMessageW(LIST, 0x1000 + 12, WPARAM(usize::MAX), LPARAM(2)).0;
    if sel < 0 {
        None
    } else {
        ROWS.get(sel as usize).copied()
    }
}

/// 状态条：声卡 / 麦克风 / 键盘驱动 / 遥控器
unsafe fn refresh_status() {
    let p = crate::commands::probes();
    // ★ 只看"驱动装了没有"（读注册表，无副作用）。
    //   绝不能用 `is_inject_ready()` —— 它会**尝试创建虚拟键盘**，
    //   与后台注入线程抢同一个设备槽位。实测：WinUHid.dll 缺失时在 WM_CREATE 里
    //   直接 0xC0000005 崩溃；即使不崩，也会干扰正在工作的注入。
    let drv = crate::device_probe::winuhid_driver_installed();
    let items: [(&str, bool); 4] = [
        ("声卡", p.cable_in && p.cable_out),
        ("麦克风", p.mic_is_cable),
        ("键盘驱动", drv),
        ("遥控器", p.remote_is_keyboard == Some(false)),
    ];
    for (i, (name, ok)) in items.iter().enumerate() {
        if let Some(h) = STATUS_LABELS.get(i) {
            let txt = format!("{} {}", name, if *ok { "✓" } else { "⚠" });
            let t = wide(&txt);
            SetWindowTextW(*h, PCWSTR(t.as_ptr()));
            TEXT_COLORS.retain(|(k, _)| *k != h.0);
            set_color(*h, if *ok { C_OK } else { C_WARN });
        }
    }
}

unsafe fn load_app_icon() -> HICON {
    use std::os::windows::ffi::OsStrExt;
    if let Ok(exe) = std::env::current_exe() {
        if let Some(dir) = exe.parent() {
            let p = dir.join("icon.ico");
            if p.exists() {
                let w: Vec<u16> = p.as_os_str().encode_wide().chain(std::iter::once(0)).collect();
                if let Ok(h) = LoadImageW(
                    None, PCWSTR(w.as_ptr()), IMAGE_ICON, 0, 0,
                    LR_LOADFROMFILE | LR_DEFAULTSIZE,
                ) {
                    if h.0 != 0 {
                        return HICON(h.0);
                    }
                }
            }
        }
    }
    let inst = GetModuleHandleW(None).unwrap_or_default();
    LoadIconW(HINSTANCE(inst.0), PCWSTR(1 as *const u16)).unwrap_or_default()
}

unsafe fn add_tray(hwnd: HWND, icon: HICON) {
    let mut nid = NOTIFYICONDATAW {
        cbSize: std::mem::size_of::<NOTIFYICONDATAW>() as u32,
        hWnd: hwnd,
        uID: 1,
        uFlags: NIF_ICON | NIF_MESSAGE | NIF_TIP,
        uCallbackMessage: WM_TRAY,
        hIcon: icon,
        ..Default::default()
    };
    for (i, c) in "IPRC1000".encode_utf16().take(127).enumerate() {
        nid.szTip[i] = c;
    }
    if !Shell_NotifyIconW(NIM_ADD, &nid).as_bool() {
        let _ = std::fs::write(
            std::env::temp_dir().join("iprc-tray-failed.txt"),
            b"NIM_ADD returned FALSE",
        );
    }
}

unsafe fn remove_tray(hwnd: HWND) {
    let nid = NOTIFYICONDATAW {
        cbSize: std::mem::size_of::<NOTIFYICONDATAW>() as u32,
        hWnd: hwnd,
        uID: 1,
        ..Default::default()
    };
    let _ = Shell_NotifyIconW(NIM_DELETE, &nid);
}

/// 把捕获到的组合键变成 `apply_binding` 需要的 "17,65" 形式
fn combo_vks(vk: u32) -> Vec<u16> {
    let st = |k: i32| unsafe { GetKeyState(k) } < 0;
    let mut v: Vec<u16> = Vec::new();
    if st(0x11) { v.push(0x11); }
    if st(0x12) { v.push(0x12); }
    if st(0x10) { v.push(0x10); }
    if st(0x5B) || st(0x5C) { v.push(0x5B); }
    v.push(vk as u16);
    v
}

unsafe extern "system" fn wndproc(hwnd: HWND, msg: u32, wp: WPARAM, lp: LPARAM) -> LRESULT {
    match msg {
        WM_CREATE => {
            BRUSH_BG = CreateSolidBrush(COLORREF(C_BG));
            FONT_N = make_font(-14, FW_NORMAL.0 as i32);
            FONT_B = make_font(-16, FW_BOLD.0 as i32);

            // 状态条（一行）
            STATUS_LABELS.clear();
            for (i, name) in ["声卡", "麦克风", "键盘驱动", "遥控器"].iter().enumerate() {
                let x = 24 + i as i32 * 92;
                let h = label(hwnd, &format!("{name} …"), x, 16, 90, 22, C_DIM, false);
                STATUS_LABELS.push(h);
            }
            push_button(hwnd, "一键修复", ID_FIX, 696, 12, 116, 30);

            // 工具栏
            label(hwnd, "按键映射 · 单模式", 24, 62, 300, 24, C_TEXT, true);
            push_button(hwnd, "+ 添加按键", ID_ADD, 424, 58, 112, 30);
            push_button(hwnd, "删除", ID_DEL, 542, 58, 76, 30);
            push_button(hwnd, "改映射", ID_REMAP, 624, 58, 92, 30);
            push_button(hwnd, "恢复默认", ID_DEFAULT, 722, 58, 90, 30);

            LIST = make_list(hwnd, 24, 98, 788, 590);
            rebuild_list();

            HINT = label(
                hwnd,
                "选中一行 → 「改映射」按目标键；「+ 添加按键」用于列表里没有的键",
                24, 696, 788, 22, C_DIM, false,
            );

            // 输入法
            let ime = crate::settings::ime();
            label(hwnd, "唤醒", 28, 734, 60, 24, C_TEXT, true);
            toggle(hwnd, "豆包（右 Alt）", ID_IME_DOUBAO, 88, 734, 140,
                   WS_CHILD | WINDOW_STYLE(0x0000_0009), ime != "wechat");
            toggle(hwnd, "微信输入法（Ctrl+Win）", ID_IME_WECHAT, 234, 734, 220,
                   WS_CHILD | WINDOW_STYLE(0x0000_0009), ime == "wechat");

            // 触发方式
            let tap = crate::settings::trigger() == "tap";
            label(hwnd, "触发", 28, 768, 60, 24, C_TEXT, true);
            toggle(hwnd, "按住说话（松开结束）", ID_HOLD, 88, 768, 200,
                   WS_CHILD | WINDOW_STYLE(0x0000_0009), !tap);
            toggle(hwnd, "点按一次（松手自动结束）", ID_TAP, 292, 768, 240,
                   WS_CHILD | WINDOW_STYLE(0x0000_0009), tap);
            toggle(hwnd, "保存运行日志", ID_LOG, 560, 768, 200,
                   WS_CHILD | WINDOW_STYLE(0x0000_0003), crate::settings::log_enabled());

            // 底部
            push_button(hwnd, "打开日志目录", ID_LOGDIR, 24, 816, 132, 32);
            push_button(hwnd, "导出配置", ID_EXPORT, 168, 816, 112, 32);
            push_button(hwnd, "收进托盘", ID_TRAY, 680, 816, 132, 32);

            // 图标 + 托盘
            let icon = load_app_icon();
            TRAY_ICON = icon;
            if icon.0 != 0 {
                SendMessageW(hwnd, WM_SETICON, WPARAM(ICON_BIG as usize), LPARAM(icon.0));
                SendMessageW(hwnd, WM_SETICON, WPARAM(ICON_SMALL as usize), LPARAM(icon.0));
            }
            add_tray(hwnd, icon);
            refresh_status();
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
            let lb = CreateSolidBrush(COLORREF(0x00484848));
            for top in [50i32, 720, 800] {
                let line = RECT { left: 24, top, right: W - 28, bottom: top + 1 };
                FillRect(hdc, &line, lb);
            }
            let _ = DeleteObject(HGDIOBJ(lb.0));
            LRESULT(1)
        }
        WM_COMMAND => {
            let id = (wp.0 & 0xffff) as usize;
            match id {
                ID_FIX => {
                    set_hint("正在逐项检查并修复，请稍候…", C_HINT);
                    std::thread::spawn(move || {
                        let r = crate::commands::repair_all();
                        let v: serde_json::Value = serde_json::from_str(&r).unwrap_or_default();
                        let msg = v["message"].as_str().unwrap_or("修复完成").to_string();
                        unsafe { REPAIR_MSG = Some(msg); }
                        let h = FindWindowW(None, PCWSTR(wide("IPRC1000").as_ptr()));
                        if h.0 != 0 {
                            let _ = PostMessageW(h, WM_REPAIR_DONE, WPARAM(0), LPARAM(0));
                        }
                    });
                }
                ID_ADD => {
                    CAP = 1;
                    set_hint("① 请按遥控器上要新增的那个键…", C_HINT);
                    let _ = windows::Win32::UI::Input::KeyboardAndMouse::SetFocus(hwnd);
                }
                ID_DEL => match selected_usage() {
                    None => set_hint("请先选中要删除的自定义键", C_HINT),
                    Some(u) if ORDER.contains(&u) => {
                        set_hint("内置按键不能删除（只能改映射）；自定义键才可删", C_HINT)
                    }
                    Some(u) => {
                        let id = crate::keymap::id_of(u);
                        crate::settings::set_binding_in_group(&id, "");
                        crate::keymap::remove_override(u);
                        rebuild_list();
                        set_hint("已删除该自定义按键", C_OK);
                    }
                },
                ID_REMAP => match selected_usage() {
                    None => set_hint("请先在表格里选中一行，再点「改映射」", C_HINT),
                    Some(u) if !editable(u) => set_hint(
                        "这一项由「唤醒」或完整界面决定，此处不支持单独修改", C_HINT),
                    Some(_) => {
                        CAP = 2;
                        set_hint("请按目标键（可先按住 Ctrl/Alt/Shift/Win）…", C_HINT);
                        let _ = windows::Win32::UI::Input::KeyboardAndMouse::SetFocus(hwnd);
                    }
                },
                ID_DEFAULT => {
                    for u in ORDER.iter() {
                        let id = crate::keymap::id_of(*u);
                        crate::settings::set_binding_in_group(&id, "");
                        crate::keymap::remove_override(*u);
                    }
                    rebuild_list();
                    set_hint("已恢复出厂默认键位", C_OK);
                }
                ID_IME_DOUBAO => {
                    crate::settings::set_ime("doubao");
                    rebuild_list();
                    set_hint("语音键已改为「右 Alt」（豆包）", C_OK);
                }
                ID_IME_WECHAT => {
                    crate::settings::set_ime("wechat");
                    rebuild_list();
                    set_hint("语音键已改为「Ctrl + Win」（微信输入法）", C_OK);
                }
                ID_HOLD => {
                    crate::settings::set_trigger("hold");
                    set_hint("已改为「按住说话」：按下开始，松手结束", C_OK);
                }
                ID_TAP => {
                    crate::settings::set_trigger("tap");
                    set_hint("已改为「点按一次」：松手后自动补一次点击", C_OK);
                }
                ID_LOG => {
                    let on = SendMessageW(HWND(lp.0), BM_GETCHECK, WPARAM(0), LPARAM(0)).0 != 0;
                    crate::settings::set_log_enabled(on);
                    set_hint(if on { "已开启日志（重启程序后开始记录）" } else { "已关闭日志" }, C_OK);
                }
                ID_LOGDIR => {
                    let base = std::env::var("APPDATA").unwrap_or_default();
                    let dir = std::path::PathBuf::from(base).join("IPRC1000 Adapter");
                    let _ = std::process::Command::new("explorer").arg(&dir).spawn();
                    set_hint(&format!("已打开 {}", dir.display()), C_OK);
                }
                ID_EXPORT => {
                    let base = std::env::var("APPDATA").unwrap_or_default();
                    let src = std::path::PathBuf::from(&base).join("IPRC1000 Adapter").join("native.json");
                    let dst = std::path::PathBuf::from(&base).join("IPRC1000 配置备份.json");
                    let msg = match std::fs::copy(&src, &dst) {
                        Ok(_) => format!("配置已导出到 {}", dst.display()),
                        Err(e) => format!("导出失败：{e}"),
                    };
                    set_hint(&msg, C_OK);
                }
                ID_TRAY => {
                    let _ = ShowWindow(hwnd, SW_HIDE);
                }
                _ => {}
            }
            LRESULT(0)
        }
        WM_REPAIR_DONE => {
            let msg = unsafe { REPAIR_MSG.take() }.unwrap_or_else(|| String::from("修复完成"));
            refresh_status();
            rebuild_list();
            set_hint(&msg, C_OK);
            LRESULT(0)
        }
        WM_CLOSE => {
            let _ = ShowWindow(hwnd, SW_HIDE);
            LRESULT(0)
        }
        WM_TRAY => {
            let ev = (lp.0 & 0xFFFF) as u32;
            if ev == WM_LBUTTONUP || ev == WM_LBUTTONDBLCLK {
                let _ = ShowWindow(hwnd, SW_SHOW);
                let _ = SetForegroundWindow(hwnd);
            } else if ev == WM_RBUTTONUP {
                if let Ok(menu) = CreatePopupMenu() {
                    let t1 = wide("打开界面");
                    let t2 = wide("退出");
                    let _ = AppendMenuW(menu, MF_STRING, ID_MENU_OPEN, PCWSTR(t1.as_ptr()));
                    let _ = AppendMenuW(menu, MF_STRING, ID_MENU_QUIT, PCWSTR(t2.as_ptr()));
                    let mut pt = windows::Win32::Foundation::POINT::default();
                    let _ = GetCursorPos(&mut pt);
                    let _ = SetForegroundWindow(hwnd);
                    let cmd = TrackPopupMenu(
                        menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.x, pt.y, 0, hwnd, None,
                    ).0 as usize;
                    let _ = DestroyMenu(menu);
                    let _ = PostMessageW(hwnd, WM_NULL, WPARAM(0), LPARAM(0));
                    if cmd == ID_MENU_OPEN {
                        let _ = ShowWindow(hwnd, SW_SHOW);
                        let _ = SetForegroundWindow(hwnd);
                    } else if cmd == ID_MENU_QUIT {
                        let _ = DestroyWindow(hwnd);
                    }
                }
            }
            LRESULT(0)
        }
        WM_KEYDOWN | WM_SYSKEYDOWN => {
            let state = CAP;
            if state == 0 {
                return DefWindowProcW(hwnd, msg, wp, lp);
            }
            let vk = wp.0 as u32;
            if matches!(vk, 0x10 | 0x11 | 0x12 | 0x5B | 0x5C) {
                return LRESULT(0);
            }
            if state == 1 {
                CAP_USAGE = (vk & 0xFF) as u8;
                CAP = 2;
                set_hint(
                    &format!("已记录 HID {:02X} —— ② 请按要映射到的目标键…", CAP_USAGE),
                    C_HINT,
                );
                return LRESULT(0);
            }
            let vks = combo_vks(vk);
            let csv = vks.iter().map(|v| v.to_string()).collect::<Vec<_>>().join(",");
            let usage = CAP_USAGE;
            CAP_USAGE = 0;
            CAP = 0;
            unsafe {
                if let Some(sel) = selected_usage() {
                    let id = crate::keymap::id_of(sel);
                    let r = crate::apply_binding(&id, &csv);
                    rebuild_list();
                    set_hint(&r, C_OK);
                } else if state == 1 || usage != 0 {
                    let id = crate::keymap::id_of(usage);
                    let r = crate::apply_binding(&id, &csv);
                    rebuild_list();
                    // 新增的自定义键滚到可见
                    let idx = ROWS.len().saturating_sub(1);
                    SendMessageW(LIST, 0x1000 + 19, WPARAM(idx), LPARAM(0));
                    set_hint(&format!("{r}（已新增）"), C_OK);
                }
            }
            LRESULT(0)
        }
        WM_DESTROY => {
            remove_tray(hwnd);
            let _ = DeleteObject(HGDIOBJ(BRUSH_BG.0));
            let _ = DeleteObject(HGDIOBJ(FONT_N.0));
            let _ = DeleteObject(HGDIOBJ(FONT_B.0));
            PostQuitMessage(0);
            LRESULT(0)
        }
        _ => DefWindowProcW(hwnd, msg, wp, lp),
    }
}

/// 打开 Win32 界面并进入消息循环（阻塞直到用户「退出」）。
///
/// 返回后调用方应退出进程：这个界面就是 UI，它关了说明用户要收工。
pub fn run() {
    unsafe {
        let icc = INITCOMMONCONTROLSEX {
            dwSize: std::mem::size_of::<INITCOMMONCONTROLSEX>() as u32,
            dwICC: ICC_LISTVIEW_CLASSES,
        };
        let _ = InitCommonControlsEx(&icc);

        let inst = GetModuleHandleW(None).unwrap_or_default();
        let cls = wide("Iprc1000Panel");
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

        let title = wide("IPRC1000");
        let hwnd = CreateWindowExW(
            WINDOW_EX_STYLE(0), PCWSTR(cls.as_ptr()), PCWSTR(title.as_ptr()),
            WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX,
            CW_USEDEFAULT, CW_USEDEFAULT, W, H,
            HWND(0), HMENU(0), HINSTANCE(inst.0), None,
        );
        if hwnd.0 == 0 {
            eprintln!("[!] Win32 界面创建失败");
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