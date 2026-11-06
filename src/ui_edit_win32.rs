//! Win32 版的「设置快捷键」对话框（2026-11-06）
//!
//! ## 为什么需要
//!
//! Win32 版原来的「改映射」只有一条路：**直接按一个键**。能录单键和组合键，
//! 但看不到能选什么、也没法选鼠标动作/媒体键 —— 客户问"怎么设成鼠标右键"，
//! 只能回答"用完整版"。
//!
//! 这里做一个与完整版（egui）等价的可视化面板：修饰键勾选 + 键帽网格 +
//! 媒体键 + 导航键 + 动作按钮，底部 确定 / 清除 / 取消。
//!
//! ## 与 egui 版的关系
//!
//! 键位表照抄 `ui_edit.rs` 的同一份定义（VK 与排列一致），值编码也一致：
//!   · 按键 → `17,16,83`（修饰键在前，逗号分隔的 VK 十进制）
//!   · 动作 → `-1`..`-5`
//!   · 清除 → 空串
//! 所以两个界面改出来的配置可以互相打开、互相认。
//!
//! ## 实现取向
//!
//! 纯原生控件（BUTTON + AUTOCHECKBOX），自己跑一个模态消息循环。
//! 不用 DialogBoxIndirect —— 那种方式得把模板塞进资源，改个布局就要动资源；
//! 这里布局由键表算坐标，加键只改表。
//!
//! 全部界面状态只在对话框自己的消息循环线程里读写（与 ui_win32 同一套不变式）。

// 界面状态用 static mut 保存，但只在对话框自己的消息循环线程里读写
// （与 ui_win32 同一套不变式），跨线程的数据走 AtomicBool/Mutex。
#![allow(static_mut_refs)]

use std::sync::atomic::{AtomicBool, Ordering};
use windows::core::PCWSTR;
use windows::Win32::Foundation::{HINSTANCE, HWND, LPARAM, LRESULT, WPARAM};
use windows::Win32::Graphics::Gdi::HBRUSH;
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::UI::WindowsAndMessaging::*;
// 这两个反直觉地挂在 KeyboardAndMouse 下（windows crate 按头文件归属，不按语义）
use windows::Win32::UI::Input::KeyboardAndMouse::{EnableWindow, SetActiveWindow};

const CTRL: u16 = 0x11;
const ALT: u16 = 0x12;
const SHIFT: u16 = 0x10;
const WIN: u16 = 0x5B;

const ID_MOD_BASE: usize = 2000;
const ID_KEY_BASE: usize = 2100;
const ID_ACT_BASE: usize = 2400;
const ID_PREVIEW: usize = 2500;
const ID_OK: usize = 2501;
const ID_CLEAR: usize = 2502;
const ID_CANCEL: usize = 2503;

// ── 键位表（与 ui_edit.rs 一致）──
const ROW1: &[(&str, u16)] = &[
    ("Esc", 0x1B), ("F1", 0x70), ("F2", 0x71), ("F3", 0x72), ("F4", 0x73),
    ("F5", 0x74), ("F6", 0x75), ("F7", 0x76), ("F8", 0x77), ("F9", 0x78),
    ("F10", 0x79), ("F11", 0x7A), ("F12", 0x7B),
];
const ROW2: &[(&str, u16)] = &[
    ("`", 0xC0), ("1", 0x31), ("2", 0x32), ("3", 0x33), ("4", 0x34),
    ("5", 0x35), ("6", 0x36), ("7", 0x37), ("8", 0x38), ("9", 0x39),
    ("0", 0x30), ("-", 0xBD), ("=", 0xBB),
];
const ROW3: &[(&str, u16)] = &[
    ("Q", 0x51), ("W", 0x57), ("E", 0x45), ("R", 0x52), ("T", 0x54),
    ("Y", 0x59), ("U", 0x55), ("I", 0x49), ("O", 0x4F), ("P", 0x50),
    ("[", 0xDB), ("]", 0xDD),
];
const ROW4: &[(&str, u16)] = &[
    ("A", 0x41), ("S", 0x53), ("D", 0x44), ("F", 0x46), ("G", 0x47),
    ("H", 0x48), ("J", 0x4A), ("K", 0x4B), ("L", 0x4C), (";", 0xBA),
    ("'", 0xDE),
];
const ROW5: &[(&str, u16)] = &[
    ("Z", 0x5A), ("X", 0x58), ("C", 0x43), ("V", 0x56), ("B", 0x42),
    ("N", 0x4E), ("M", 0x4D), (",", 0xBC), (".", 0xBE), ("/", 0xBF),
];
const ROW_MEDIA: &[(&str, u16)] = &[
    ("播放/暂停", 0xB3), ("上一首", 0xB1), ("下一首", 0xB0),
    ("音量 -", 0xAE), ("音量 +", 0xAF), ("静音", 0xAD),
];
const ROW_NAV: &[(&str, u16)] = &[
    ("Up", 0x26), ("Down", 0x28), ("Left", 0x25), ("Right", 0x27),
    ("PgUp", 0x21), ("PgDn", 0x22),
];
const ROW_ACT: &[(&str, i32)] = &[
    ("切换配置组", -5), ("鼠标左键", -1), ("鼠标右键", -2),
    ("滚轮上", -3), ("滚轮下", -4),
];

/// 对话框状态（仅对话框线程访问）
struct State {
    mods: Vec<u16>,
    main: u16,
    action: i32,
    /// 当前绑定不属于本面板可编辑的范围时，用它原样显示（如"打开 WorkBuddy"）
    cur_note: Option<String>,
    result: Option<String>,
}
static mut ST: State = State { mods: Vec::new(), main: 0, action: 0, cur_note: None, result: None };
/// 所有键帽的登记表：(控件 id, VK, 显示名)。
///
/// ★ 必须统一登记：之前"创建时按钮序号"和"点击时按 ROW 数组推算序号"是两套算法，
///   行尾插了 Tab/Backspace/Enter 之后两边就错位了 —— 点 S 实际落到 H 上，
///   而界面上看不出任何异常（只是标错了键）。现在两边只查这一张表。
static mut KEYS: Vec<(usize, u16, &'static str)> = Vec::new();
static OPEN: AtomicBool = AtomicBool::new(false);

fn wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

unsafe fn mk(parent: HWND, class: &str, text: &str, id: usize,
            x: i32, y: i32, w: i32, h: i32, style: u32) {
    let c = wide(class);
    let t = wide(text);
    let inst = GetModuleHandleW(None).unwrap_or_default();
    let _ = CreateWindowExW(
        WINDOW_EX_STYLE(0),
        PCWSTR(c.as_ptr()),
        PCWSTR(t.as_ptr()),
        WS_CHILD | WS_VISIBLE | WINDOW_STYLE(style),
        x, y, w, h,
        parent,
        HMENU(id as isize),
        HINSTANCE(inst.0),
        None,
    );
}

unsafe fn btn(parent: HWND, text: &str, id: usize, x: i32, y: i32, w: i32, h: i32) {
    mk(parent, "BUTTON", text, id, x, y, w, h, BS_PUSHBUTTON as u32);
}

/// 创建一个"键帽"并登记到 KEYS（查找/高亮都靠这张表）
unsafe fn key_btn(parent: HWND, name: &'static str, vk: u16, id: usize,
                  x: i32, y: i32, w: i32, h: i32) {
    mk(parent, "BUTTON", name, id, x, y, w, h, BS_PUSHBUTTON as u32);
    KEYS.push((id, vk, name));
}

unsafe fn set_text(h: HWND, s: &str) {
    let t = wide(s);
    let _ = SetWindowTextW(h, PCWSTR(t.as_ptr()));
}

/// 现场从 4 个复选框读修饰键。
///
/// ★ 不能缓存：`CheckDlgButton` 会向父窗口发 WM_COMMAND，回填勾选的过程中
///   就会被自己的处理函数读到"还没勾完"的中间状态，把缓存清空 ——
///   现象是勾着 Ctrl/Alt 却显示"当前：W"（用户截图里就是这样）。
unsafe fn mods_now(_dlg: HWND) -> Vec<u16> {
    ST.mods.clone()
}

/// 点修饰键：在内存里叠加/取消，然后刷新显示
unsafe fn toggle_mod(dlg: HWND, i: usize) {
    let vk = [CTRL, ALT, SHIFT, WIN][i];
    if let Some(p) = ST.mods.iter().position(|v| *v == vk) {
        ST.mods.remove(p);
    } else {
        ST.mods.insert(0, vk); // 修饰键永远排前面，Ctrl+Alt+W 这种顺序才对
    }
    refresh(dlg);
}

unsafe fn preview_text(dlg: HWND) -> String {
    if ST.action != 0 {
        return ROW_ACT
            .iter()
            .find(|(_, a)| *a == ST.action)
            .map(|(n, _)| n.to_string())
            .unwrap_or_else(|| format!("动作 {}", ST.action));
    }
    let mut v = mods_now(dlg);
    if ST.main != 0 {
        v.push(ST.main);
    }
    if v.is_empty() {
        return ST.cur_note.clone().unwrap_or_else(|| String::from("未设置"));
    }
    v.iter()
        .map(|vk| crate::keymap::vk_name(*vk))
        .collect::<Vec<_>>()
        .join("+")
}

unsafe fn refresh(dlg: HWND) {
    let pv = GetDlgItem(dlg, ID_PREVIEW as i32);
    if pv.0 != 0 {
        set_text(pv, &preview_text(dlg));
    }
    // 键帽：选中的加中括号（比自绘背景省事，也不会因 DPI 走样）
    for (id, vk, name) in KEYS.iter() {
        let h = GetDlgItem(dlg, *id as i32);
        if h.0 != 0 {
            let t = if ST.main == *vk { format!("[{name}]") } else { name.to_string() };
            set_text(h, &t);
        }
    }
    for (i, name) in ["Ctrl", "Alt", "Shift", "Win"].iter().enumerate() {
        let h = GetDlgItem(dlg, (ID_MOD_BASE + i) as i32);
        if h.0 != 0 {
            let on = ST.mods.contains(&[CTRL, ALT, SHIFT, WIN][i]);
            let t = if on { format!("[{name}]") } else { (*name).to_string() };
            set_text(h, &t);
        }
    }
    let mut ai = 0usize;
    for (name, act) in ROW_ACT {
        let h = GetDlgItem(dlg, (ID_ACT_BASE + ai) as i32);
        if h.0 != 0 {
            let t = if ST.action == *act { format!("[{name}]") } else { name.to_string() };
            set_text(h, &t);
        }
        ai += 1;
    }
}

/// 打开对话框。`current` 为当前值（回填用）。
/// 返回 `Some(值)`（空串=清除）；取消返回 `None`。
pub unsafe fn show(parent: HWND, key_label: &str, current: &str) -> Option<String> {
    if OPEN.swap(true, Ordering::SeqCst) {
        return None;
    }
    ST = State { mods: Vec::new(), main: 0, action: 0, cur_note: None, result: None };
    KEYS.clear();

    // 回填
    let cur = current.trim();
    if let Ok(n) = cur.parse::<i32>() {
        if (-6..=-1).contains(&n) {
            ST.action = n;
        } else if (-2999..=-2001).contains(&n) {
            // 「打开程序」绑定：本面板不编辑它，但要让用户知道当前是什么
            ST.cur_note = Some(crate::shortcut_store::label((-2000 - n) as u32));
        } else if (-1010..=-1001).contains(&n) || (-1999..=-1101).contains(&n) {
            ST.cur_note = Some(String::from("文本 / 宏（请在完整版界面改）"));
        } else if (0..=0xFFFF).contains(&n) {
            // ★ 单个 VK（"87" 这种只绑一个键、没有逗号的）也必须回填成主键。
            //   漏了这一支 ⇒ 打开对话框显示"未设置"，用户以为键没绑。
            ST.main = n as u16;
        }
    } else if !cur.is_empty() {
        let mut vks: Vec<u16> = cur
            .split(',')
            .filter_map(|x| x.trim().parse::<u16>().ok())
            .collect();
        let mut mods = Vec::new();
        for m in [CTRL, ALT, SHIFT, WIN] {
            if let Some(p) = vks.iter().position(|v| *v == m) {
                mods.push(m);
                vks.remove(p);
            }
        }
        ST.mods = mods;
        ST.main = vks.first().copied().unwrap_or(0);
    }

    let cls = wide("IprcKeyEditDlg");
    let hinst = GetModuleHandleW(None).ok();
    let wc = WNDCLASSW {
        style: CS_HREDRAW | CS_VREDRAW,
        lpfnWndProc: Some(wndproc),
        hInstance: hinst.map(|h| h.into()).unwrap_or_default(),
        hCursor: LoadCursorW(None, IDC_ARROW).unwrap_or_default(),
        hbrBackground: HBRUSH(6), // COLOR_WINDOW+1
        lpszClassName: PCWSTR(cls.as_ptr()),
        ..Default::default()
    };
    let _ = RegisterClassW(&wc);

    let (cw, ch) = (800i32, 505i32);
    let (mut px, mut py) = (240, 160);
    let mut pr = windows::Win32::Foundation::RECT::default();
    if GetWindowRect(parent, &mut pr).is_ok() {
        px = pr.left + ((pr.right - pr.left) - cw) / 2;
        py = pr.top + ((pr.bottom - pr.top) - ch) / 2;
    }
    let title = wide(&format!("设置「{key_label}」的快捷键"));
    let dlg = CreateWindowExW(
        WS_EX_DLGMODALFRAME,
        PCWSTR(cls.as_ptr()),
        PCWSTR(title.as_ptr()),
        WS_POPUP | WS_CAPTION | WS_SYSMENU | WS_VISIBLE,
        px, py, cw, ch,
        parent,
        HMENU(0),
        HINSTANCE(hinst.map(|h| h.0).unwrap_or(0)),
        None,
    );
    if dlg.0 == 0 {
        OPEN.store(false, Ordering::SeqCst);
        return None;
    };

    // 字体：中文必须显式给，否则控件用系统默认宋体，字小而糊
    let face = wide("Microsoft YaHei UI");
    let font = windows::Win32::Graphics::Gdi::CreateFontW(
        -15, 0, 0, 0, 400, 0, 0, 0, 134, 0, 0, 0, 0,
        PCWSTR(face.as_ptr()),
    );

    // ── 布局 ──
    let pad = 18i32;
    let (kw, kh, gap) = (46i32, 28i32, 4i32);
    let mut y = 14i32;

    mk(dlg, "STATIC", "当前：", 0, pad, y + 4, 58, 20, 0);
    mk(dlg, "STATIC", "", ID_PREVIEW, pad + 58, y + 2, 460, 22, 0);
    y += 30;

    mk(dlg, "STATIC", "修饰：", 0, pad, y + 6, 58, 20, 0);
    let mut mx = pad + 58;
    for (i, name) in ["Ctrl", "Alt", "Shift", "Win"].iter().enumerate() {
        // 用普通按钮而不是复选框：这个窗口是自己 CreateWindow 的，不是对话框管理器创建的，
        // 复选标记（BM_SETCHECK）刷不出来。用文字 [Ctrl] 表示已选，状态只存在内存里，必定一致。
        btn(dlg, name, ID_MOD_BASE + i, mx, y, 78, kh);
        mx += 82;
    }
    y += kh + 8;

    let mut idx = 0usize;
    let mut x;

    // 行1：Esc + F1..F12
    x = pad;
    for (name, vk) in ROW1 {
        key_btn(dlg, name, *vk, ID_KEY_BASE + idx, x, y, kw, kh);
        idx += 1;
        x += kw + gap;
    }
    y += kh + gap;

    // 行2：` 1..0 - = + Backspace
    x = pad;
    for (name, vk) in ROW2 {
        key_btn(dlg, name, *vk, ID_KEY_BASE + idx, x, y, kw, kh);
        idx += 1;
        x += kw + gap;
    }
    key_btn(dlg, "Backspace", 0x08, ID_KEY_BASE + idx, x, y, 86, kh);
    idx += 1;
    y += kh + gap;

    // 行3：Tab + Q..] + \
    x = pad;
    key_btn(dlg, "Tab", 0x09, ID_KEY_BASE + idx, x, y, 56, kh);
    idx += 1;
    x += 56 + gap;
    for (name, vk) in ROW3 {
        key_btn(dlg, name, *vk, ID_KEY_BASE + idx, x, y, kw, kh);
        idx += 1;
        x += kw + gap;
    }
    key_btn(dlg, "\\", 0xDC, ID_KEY_BASE + idx, x, y, 56, kh);
    idx += 1;
    y += kh + gap;

    // 行4：Caps + A..' + Enter
    x = pad;
    key_btn(dlg, "Caps", 0x14, ID_KEY_BASE + idx, x, y, 64, kh);
    idx += 1;
    x += 64 + gap;
    for (name, vk) in ROW4 {
        key_btn(dlg, name, *vk, ID_KEY_BASE + idx, x, y, kw, kh);
        idx += 1;
        x += kw + gap;
    }
    key_btn(dlg, "Enter", 0x0D, ID_KEY_BASE + idx, x, y, 78, kh);
    idx += 1;
    y += kh + gap;

    // 行5：Shift + Z../ + Shift
    x = pad;
    key_btn(dlg, "Shift", 0x10, ID_KEY_BASE + idx, x, y, 64, kh);
    idx += 1;
    x += 64 + gap;
    for (name, vk) in ROW5 {
        key_btn(dlg, name, *vk, ID_KEY_BASE + idx, x, y, kw, kh);
        idx += 1;
        x += kw + gap;
    }
    key_btn(dlg, "Shift", 0x10, ID_KEY_BASE + idx, x, y, 78, kh);
    idx += 1;
    y += kh + gap;

    // 行6：Ctrl Win Alt Space Alt Win Menu Ctrl
    x = pad;
    for (name, w, vk) in [("Ctrl", 62, 0x11), ("Win", 52, 0x5B), ("Alt", 52, 0x12),
                          ("Space", 246, 0x20), ("Alt", 52, 0x12), ("Win", 52, 0x5B),
                          ("Menu", 56, 0x5D), ("Ctrl", 62, 0x11)] {
        key_btn(dlg, name, vk, ID_KEY_BASE + idx, x, y, w, kh);
        idx += 1;
        x += w + gap;
    }
    y += kh + 10;

    // 媒体键
    x = pad;
    for (name, vk) in ROW_MEDIA {
        key_btn(dlg, name, *vk, ID_KEY_BASE + idx, x, y, 78, kh);
        idx += 1;
        x += 82 + gap;
    }
    y += kh + gap;

    // 导航键
    x = pad;
    for (name, vk) in ROW_NAV {
        key_btn(dlg, name, *vk, ID_KEY_BASE + idx, x, y, 78, kh);
        idx += 1;
        x += 82 + gap;
    }
    y += kh + 12;

    // 动作
    mk(dlg, "STATIC", "动作：", 0, pad, y + 6, 58, 20, 0);
    x = pad + 58;
    let mut ai = 0usize;
    for (name, _act) in ROW_ACT {
        btn(dlg, name, ID_ACT_BASE + ai, x, y, 118, kh);
        ai += 1;
        x += 122;
    }
    y += kh + 16;

    // 底部
    btn(dlg, "确定", ID_OK, pad, y, 96, 32);
    btn(dlg, "清除", ID_CLEAR, pad + 106, y, 96, 32);
    btn(dlg, "取消", ID_CANCEL, pad + 212, y, 96, 32);

    // 应用字体（创建完统一刷一遍）
    if !font.0 == 0 {
        let mut child = GetWindow(dlg, GW_CHILD);
        while !child.0 == 0 {
            SendMessageW(child, WM_SETFONT, WPARAM(font.0 as usize), LPARAM(1));
            child = GetWindow(child, GW_HWNDNEXT);
        }
    }

    // 回填勾选与高亮
    for (i, vk) in [CTRL, ALT, SHIFT, WIN].iter().enumerate() {
        if ST.mods.contains(vk) {
            let h = GetDlgItem(dlg, (ID_MOD_BASE + i) as i32);
            if !h.0 == 0 {
                SendMessageW(h, BM_SETCHECK, WPARAM(1), LPARAM(0));
            }
        }
    }
    refresh(dlg);

    // 模态
    let _ = EnableWindow(parent, false);
    let mut msg = MSG::default();
    while GetMessageW(&mut msg, None, 0, 0).as_bool() {
        let _ = TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }
    let _ = EnableWindow(parent, true);
    let _ = SetActiveWindow(parent);
    let _ = windows::Win32::Graphics::Gdi::DeleteObject(font);
    OPEN.store(false, Ordering::SeqCst);
    ST.result.take()
}

unsafe extern "system" fn wndproc(h: HWND, msg: u32, wp: WPARAM, lp: LPARAM) -> LRESULT {
    match msg {
        WM_COMMAND => {
            let id = (wp.0 & 0xFFFF) as usize;
            match id {
                ID_OK => {
                    let val = if ST.action != 0 {
                        ST.action.to_string()
                    } else {
                        let mut v = mods_now(h);
                        if ST.main != 0 {
                            v.push(ST.main);
                        }
                        v.iter().map(|x| x.to_string()).collect::<Vec<_>>().join(",")
                    };
                    ST.result = Some(val);
                    let _ = PostMessageW(h, WM_CLOSE, WPARAM(0), LPARAM(0));
                }
                ID_CLEAR => {
                    ST.result = Some(String::new());
                    let _ = PostMessageW(h, WM_CLOSE, WPARAM(0), LPARAM(0));
                }
                ID_CANCEL => {
                    ST.result = None;
                    let _ = PostMessageW(h, WM_CLOSE, WPARAM(0), LPARAM(0));
                }
                _ if (ID_MOD_BASE..ID_KEY_BASE).contains(&id) => {
                    toggle_mod(h, id - ID_MOD_BASE);
                }
                _ if (ID_KEY_BASE..ID_ACT_BASE).contains(&id) => {
                    if let Some((_, vk, _)) = KEYS.iter().find(|(k, _, _)| *k == id) {
                        ST.main = *vk;
                        ST.action = 0;
                    }
                    refresh(h);
                }
                _ if (ID_ACT_BASE..ID_ACT_BASE + 100).contains(&id) => {
                    if let Some((_, act)) = ROW_ACT.get(id - ID_ACT_BASE) {
                        ST.action = *act;
                        ST.main = 0;
                        refresh(h);
                    }
                }
                _ => {}
            }
            LRESULT(0)
        }
        WM_CLOSE => {
            let _ = DestroyWindow(h);
            LRESULT(0)
        }
        WM_DESTROY => {
            PostQuitMessage(0);
            LRESULT(0)
        }
        _ => DefWindowProcW(h, msg, wp, lp),
    }
}
