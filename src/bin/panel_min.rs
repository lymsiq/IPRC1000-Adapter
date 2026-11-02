//! IPRC1000 的 Win32 原生界面（GDI，零显卡依赖）（2026-11-02）
//!
//! 覆盖遥控器全部 23 个预置按键，并支持：
//!   · **自定义按键**：遥控器上有、但预置列表里没有的键，可「添加」后再绑定；
//!     自定义行可删除，预置行不可删（避免客户误删默认键位）。
//!   · **输入法切换**：豆包（语音键 = 右 Alt）/ 微信输入法（语音键 = Ctrl+Win）。
//!     语音键是**全局**的，由输入法决定，所以该行只读、随选择变化。
//!
//! 单一映射、无鼠标模式、无配置组 —— 那是完整界面的职责。
//!
//! 构建：`cargo build --release --bin panel_min`

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
const H: i32 = 906;

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

/// 捕获状态机：0=空闲 1=等遥控器键（新增的第一步）2=等目标键
static mut CAP: u8 = 0;
static mut CAP_USAGE: u8 = 0;
/// 自定义行（可增删）
static mut CUSTOMS: Vec<(String, String, String)> = Vec::new();
/// 当前输入法：false=豆包（右 Alt） true=微信输入法（Ctrl+Win）
static mut IME_WECHAT: bool = false;

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
/// 托盘图标回调消息
const WM_TRAY: u32 = WM_APP + 1;
/// 托盘图标句柄（WM_DESTROY 时要用它删图标）
static mut TRAY_ICON: HICON = HICON(0);

#[derive(Clone, Copy, PartialEq)]
enum Kind {
    Key,
    /// 语音键：全局、由输入法决定，只读
    Voice,
    Text,
}

struct Row {
    name: &'static str,
    hid: &'static str,
    map: &'static str,
    kind: Kind,
}

const ROWS: [Row; 23] = [
    Row { name: "电源",      hid: "HID 17", map: "Win+L",           kind: Kind::Key },
    Row { name: "返回",      hid: "HID 09", map: "Backspace",       kind: Kind::Key },
    Row { name: "菜单",      hid: "HID 11", map: "Menu",            kind: Kind::Key },
    Row { name: "退出",      hid: "HID 07", map: "Esc",             kind: Kind::Key },
    Row { name: "信息",      hid: "HID 0B", map: "Ctrl+Enter",      kind: Kind::Key },
    Row { name: "确认",      hid: "HID 28", map: "Enter",           kind: Kind::Key },
    Row { name: "▲ 上",      hid: "HID 52", map: "↑",               kind: Kind::Key },
    Row { name: "▼ 下",      hid: "HID 51", map: "↓",               kind: Kind::Key },
    Row { name: "◀ 左",      hid: "HID 50", map: "←",               kind: Kind::Key },
    Row { name: "▶ 右",      hid: "HID 4F", map: "→",               kind: Kind::Key },
    Row { name: "收藏",      hid: "HID 08", map: "Win+Shift+S",     kind: Kind::Key },
    Row { name: "语音",      hid: "HID 10", map: "右 Alt",          kind: Kind::Voice },
    Row { name: "账户",      hid: "HID 00", map: "文本槽 1（4 字）", kind: Kind::Text },
    Row { name: "播放/暂停", hid: "HID 31", map: "未设置",           kind: Kind::Key },
    Row { name: "快进",      hid: "HID 19", map: "Alt+Esc",         kind: Kind::Key },
    Row { name: "快退",      hid: "HID 36", map: "未设置",           kind: Kind::Key },
    Row { name: "频道+",     hid: "HID 04", map: "Ctrl+[",          kind: Kind::Key },
    Row { name: "频道-",     hid: "HID 05", map: "Ctrl+]",          kind: Kind::Key },
    Row { name: "音量+",     hid: "HID 1F", map: "未设置",           kind: Kind::Key },
    Row { name: "音量-",     hid: "HID 20", map: "未设置",           kind: Kind::Key },
    Row { name: "静音",      hid: "HID 0A", map: "未设置",           kind: Kind::Key },
    Row { name: "上一频道",  hid: "HID 06", map: "Alt+Ctrl+Tab",    kind: Kind::Key },
    Row { name: "方块键",    hid: "HID 16", map: "未设置",           kind: Kind::Key },
];

fn wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

fn set_color(h: HWND, c: u32) {
    unsafe { TEXT_COLORS.push((h.0, c)) };
}

fn color_of(h: HWND) -> Option<u32> {
    unsafe { TEXT_COLORS.iter().find(|(k, _)| *k == h.0).map(|(_, c)| *c) }
}

fn ime_voice_map() -> &'static str {
    unsafe {
        if IME_WECHAT {
            "Ctrl+Win（微信输入法）"
        } else {
            "右 Alt（豆包）"
        }
    }
}

fn note_of(r: &Row) -> String {
    match r.kind {
        Kind::Key => String::new(),
        Kind::Voice => "由上方「唤醒」决定，全局不可单独改".into(),
        Kind::Text => "文本输入，此界面不支持修改".into(),
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

    SendMessageW(hw, 0x1000 + 1, WPARAM(0), LPARAM(C_BG as isize)); // SETBKCOLOR
    SendMessageW(hw, 0x1000 + 38, WPARAM(0), LPARAM(C_BG as isize)); // SETTEXTBKCOLOR
    SendMessageW(hw, 0x1000 + 36, WPARAM(0), LPARAM(C_TEXT as isize)); // SETTEXTCOLOR
    SendMessageW(hw, 0x1000 + 54, WPARAM(0), LPARAM(0x0000_0020)); // FULLROWSELECT

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

/// 整表重建（增删行后调用，避免索引错乱）
unsafe fn rebuild_list() {
    SendMessageW(LIST, 0x1000 + 9, WPARAM(0), LPARAM(0)); // DELETEALLITEMS
    for (i, r) in ROWS.iter().enumerate() {
        insert_row(LIST, i, r.name);
        set_cell(LIST, i, 1, r.hid);
        let map = if r.kind == Kind::Voice { ime_voice_map() } else { r.map };
        set_cell(LIST, i, 2, map);
        set_cell(LIST, i, 3, &note_of(r));
    }
    for (k, (n, h, m)) in CUSTOMS.iter().enumerate() {
        let i = ROWS.len() + k;
        insert_row(LIST, i, n);
        set_cell(LIST, i, 1, h);
        set_cell(LIST, i, 2, m);
        set_cell(LIST, i, 3, "自定义（可删除）");
    }
}

unsafe fn selected_row() -> Option<usize> {
    let sel = SendMessageW(LIST, 0x1000 + 12, WPARAM(usize::MAX), LPARAM(2)).0;
    if sel < 0 { None } else { Some(sel as usize) }
}

fn vk_name_owned(vk: u32) -> String {
    let table: &[(u32, &str)] = &[
        (0x08, "Backspace"), (0x09, "Tab"), (0x0D, "Enter"), (0x1B, "Esc"), (0x20, "Space"),
        (0x21, "PageUp"), (0x22, "PageDown"), (0x23, "End"), (0x24, "Home"),
        (0x25, "←"), (0x26, "↑"), (0x27, "→"), (0x28, "↓"),
        (0x2D, "Insert"), (0x2E, "Delete"),
        (0xBA, ";"), (0xBB, "="), (0xBC, ","), (0xBD, "-"), (0xBE, "."), (0xBF, "/"),
        (0xDB, "["), (0xDC, "\\"), (0xDD, "]"), (0xDE, "'"), (0xC0, "`"),
    ];
    for (k, n) in table {
        if *k == vk {
            return n.to_string();
        }
    }
    if (0x30..=0x39).contains(&vk) || (0x41..=0x5A).contains(&vk) {
        return (vk as u8 as char).to_string();
    }
    if (0x70..=0x87).contains(&vk) {
        return format!("F{}", vk - 0x6F);
    }
    format!("VK_{:02X}", vk)
}

/// 加载程序图标：优先 exe 同目录的 icon.ico，其次用 exe 内嵌资源（app.rc 里 ID 1）
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

/// 放进托盘：左键唤回窗口，右键弹菜单（打开界面 / 退出）
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
    // 只有失败才留痕：正常路径零开销，出问题时也能查（托盘加不上是"看起来没运行"的常见原因）
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

unsafe extern "system" fn wndproc(hwnd: HWND, msg: u32, wp: WPARAM, lp: LPARAM) -> LRESULT {
    match msg {
        WM_CREATE => {
            BRUSH_BG = CreateSolidBrush(COLORREF(C_BG));
            FONT_N = make_font(-14, FW_NORMAL.0 as i32);
            FONT_B = make_font(-16, FW_BOLD.0 as i32);

            // 状态条
            label(hwnd, "声卡 ✓", 24, 16, 70, 22, C_OK, false);
            label(hwnd, "麦克风 ✓", 100, 16, 84, 22, C_OK, false);
            label(hwnd, "键盘驱动 ⚠", 190, 16, 104, 22, C_WARN, false);
            label(hwnd, "遥控器 ✓", 300, 16, 90, 22, C_OK, false);
            push_button(hwnd, "一键修复", ID_FIX, 696, 12, 116, 30);

            // 工具栏
            label(hwnd, "按键映射 · 单模式", 24, 62, 300, 24, C_TEXT, true);
            push_button(hwnd, "+ 添加按键", ID_ADD, 424, 58, 112, 30);
            push_button(hwnd, "删除", ID_DEL, 542, 58, 76, 30);
            push_button(hwnd, "改映射", ID_REMAP, 624, 58, 92, 30);
            push_button(hwnd, "恢复默认", ID_DEFAULT, 722, 58, 90, 30);

            LIST = make_list(hwnd, 24, 98, 788, 560);
            rebuild_list();

            HINT = label(hwnd,
                "选中一行 → 「改映射」按目标键；「+ 添加按键」用于列表里没有的键",
                24, 666, 788, 22, C_DIM, false);

            // 唤醒哪个输入法（决定语音键发什么）
            label(hwnd, "唤醒", 28, 704, 60, 24, C_TEXT, true);
            toggle(hwnd, "豆包（右 Alt）", ID_IME_DOUBAO, 88, 704, 140,
                   WS_CHILD | WINDOW_STYLE(0x0000_0009), true);
            toggle(hwnd, "微信输入法（Ctrl+Win）", ID_IME_WECHAT, 234, 704, 220,
                   WS_CHILD | WINDOW_STYLE(0x0000_0009), false);

            // 触发方式
            label(hwnd, "触发", 28, 738, 60, 24, C_TEXT, true);
            toggle(hwnd, "按住说话（松开结束）", ID_HOLD, 88, 738, 200,
                   WS_CHILD | WINDOW_STYLE(0x0000_0009), true);
            toggle(hwnd, "点按一次（松手自动结束）", ID_TAP, 292, 738, 240,
                   WS_CHILD | WINDOW_STYLE(0x0000_0009), false);
            toggle(hwnd, "保存运行日志", ID_LOG, 560, 738, 200,
                   WS_CHILD | WINDOW_STYLE(0x0000_0003), true);

            // 底部
            push_button(hwnd, "打开日志目录", ID_LOGDIR, 24, 786, 132, 32);
            push_button(hwnd, "导出配置", ID_EXPORT, 168, 786, 112, 32);
            push_button(hwnd, "收进托盘", ID_TRAY, 680, 786, 132, 32);

            // 图标：标题栏 / 任务栏 / Alt+Tab + 托盘
            let icon = load_app_icon();
            TRAY_ICON = icon;
            if icon.0 != 0 {
                SendMessageW(hwnd, WM_SETICON, WPARAM(ICON_BIG as usize), LPARAM(icon.0));
                SendMessageW(hwnd, WM_SETICON, WPARAM(ICON_SMALL as usize), LPARAM(icon.0));
            }
            add_tray(hwnd, icon);
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
            for top in [50i32, 690, 770] {
                let line = RECT { left: 24, top, right: W - 28, bottom: top + 1 };
                FillRect(hdc, &line, lb);
            }
            let _ = DeleteObject(HGDIOBJ(lb.0));
            LRESULT(1)
        }
        WM_COMMAND => {
            let id = (wp.0 & 0xffff) as usize;
            match id {
                ID_ADD => {
                    CAP = 1;
                    set_hint("① 请按遥控器上要新增的那个键…（原型用键盘按键模拟）", C_HINT);
                    let _ = windows::Win32::UI::Input::KeyboardAndMouse::SetFocus(hwnd);
                }
                ID_DEL => match selected_row() {
                    None => set_hint("请先选中要删除的自定义行", C_HINT),
                    Some(i) if i < ROWS.len() => {
                        set_hint("预置按键不能删除（只能改映射）；自定义行才可删", C_HINT)
                    }
                    Some(i) => {
                        CUSTOMS.remove(i - ROWS.len());
                        rebuild_list();
                        set_hint("已删除该自定义按键", C_OK);
                    }
                },
                ID_REMAP => match selected_row() {
                    None => set_hint("请先在表格里选中一行，再点「改映射」", C_HINT),
                    Some(i) => {
                        let kind = if i < ROWS.len() { ROWS[i].kind } else { Kind::Key };
                        match kind {
                            Kind::Key => {
                                CAP = 2;
                                set_hint("请按目标键（可先按住 Ctrl/Alt/Shift/Win）…", C_HINT);
                                let _ = windows::Win32::UI::Input::KeyboardAndMouse::SetFocus(hwnd);
                            }
                            _ => set_hint(
                                "这一项由完整界面或输入法设置决定，此界面不支持单独修改", C_HINT),
                        }
                    }
                },
                ID_IME_DOUBAO => {
                    IME_WECHAT = false;
                    rebuild_list();
                    set_hint("语音键已改为「右 Alt」（豆包）", C_OK);
                }
                ID_IME_WECHAT => {
                    IME_WECHAT = true;
                    rebuild_list();
                    set_hint(
                        "语音键已改为「Ctrl + Win」（微信输入法）；若候选框不出现，请把系统默认输入法也切成微信",
                        C_OK,
                    );
                }
                ID_TRAY => {
                    let _ = ShowWindow(hwnd, SW_HIDE);
                }
                _ => {}
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
                return LRESULT(0); // 修饰键不算
            }

            if state == 1 {
                // 第一步：记录"遥控器键"。真机接入后这里来自 ETW 上报，
                // 原型用键盘按键模拟，界面上标注"模拟"以免误解。
                CAP_USAGE = (vk & 0xFF) as u8;
                CAP = 2;
                let msg = format!("已记录 HID {:02X}（模拟）—— ② 请按要映射到的目标键…", CAP_USAGE);
                set_hint(&msg, C_HINT);
                return LRESULT(0);
            }

            // 第二步：目标键 → 生成组合键字符串并追加自定义行
            let st = |k: i32| unsafe { GetKeyState(k) } < 0;
            let mut parts: Vec<&str> = Vec::new();
            if st(0x11) { parts.push("Ctrl"); }
            if st(0x12) { parts.push("Alt"); }
            if st(0x10) { parts.push("Shift"); }
            if st(0x5B) || st(0x5C) { parts.push("Win"); }
            let main = vk_name_owned(vk);
            let combo = if parts.is_empty() { main } else { format!("{}+{}", parts.join("+"), main) };

            unsafe {
                match selected_row() {
                    // 改已有行
                    Some(i) if CAP_USAGE == 0 => {
                        if i < ROWS.len() {
                            set_cell(LIST, i, 2, &combo);
                        } else {
                            let k = i - ROWS.len();
                            if let Some(c) = CUSTOMS.get_mut(k) {
                                c.2 = combo.clone();
                            }
                            rebuild_list();
                        }
                        set_hint(&format!("已改为 {combo}"), C_OK);
                    }
                    // 新增行
                    _ => {
                        let name = format!("HID {:02X}", CAP_USAGE);
                        let hid = format!("HID {:02X}", CAP_USAGE);
                        CUSTOMS.push((name, hid, combo.clone()));
                        CAP_USAGE = 0;
                        rebuild_list();
                        // 滚到新增那一行：否则它落在可视区外，用户以为"没加成"（实测如此）
                        let idx = ROWS.len() + CUSTOMS.len() - 1;
                        SendMessageW(LIST, 0x1000 + 19, WPARAM(idx), LPARAM(0));
                        set_hint(&format!("已新增按键并绑定 {combo}（在表格末尾）"), C_OK);
                    }
                }
            }
            CAP = 0;
            LRESULT(0)
        }
        // 点 X 不退出，收进托盘（与主程序行为一致）
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
                    // 不先抢前台，菜单点"外面"不会消失（Win32 老毛病）
                    let _ = SetForegroundWindow(hwnd);
                    let cmd = TrackPopupMenu(
                        menu,
                        TPM_RETURNCMD | TPM_RIGHTBUTTON,
                        pt.x, pt.y, 0, hwnd, None,
                    )
                    .0 as usize;
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

fn main() {
    unsafe {
        // 必须先初始化通用控件，否则 ListView 的扩展调用会访问未初始化结构（实测崩溃）
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
