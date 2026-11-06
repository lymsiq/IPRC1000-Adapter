//! 遥控器输入「消费者」—— **Raw Input 版**（2026-11-06 客户机根因修复）
//!
//! ## 为什么不再用 CreateFile 打开 HID 接口
//!
//! `enum_hid_paths()` 枚举到的是键盘顶层集合接口：
//! ```text
//! \\?\hid#{00001124-...}_vid&00020a5c_pid&8502#...#{4d1e55b2-...}\kbd
//! ```
//! Windows 自 Vista 起给**键盘/鼠标 HID 顶层集合**加了保护性 ACL：只允许 SYSTEM 打开。
//! 客户机实测 `CreateFileW` 恒定 `err=5`（ERROR_ACCESS_DENIED），累计 1044 次，
//! **管理员身份也无效**——这是操作系统安全设计，提权、改共享模式都解决不了。
//!
//! Raw Input 走窗口消息 `WM_INPUT`：由系统把报文投递到窗口所属桌面，
//! **不打开设备句柄**，因此完全绕开 ACL，且不需要管理员权限、不需要改造设备。
//! 交接文档 §3.3 的 Python 原型在客户机实测 20/20 按键全捕获、按下/抬起完整。
//!
//! ## 为什么仍不能「改造遥控器」
//!
//! 改绑成 `input.inf / HID_Raw_Inst.NT` 后 HID 接口不注册、PSM 0x0013 中断通道不建立，
//! 空口连 HIDP 报文都没有 ⇒ 按键与音频一起全断（`device_probe.rs:481-500`）。
//! 保持键盘模式，代价只是系统会同时打字母（已知边界）。
//!
//! ## 语义关键点（照交接文档 §5.3 逐条落实）
//!
//! 1. **按设备过滤** `is_remote_name()`：设备名含 `0A5C` + `8502`，否则物理键盘会被当遥控器。
//! 2. **快照语义**：`report::diff()` 要的是"当前按住哪些 usage"，不是增量事件。
//!    用 `HELD[256]` 维护，每次事件产出全量列表。
//! 3. **全松开必须 dispatch 空列表**：语音键松手只认空报文（`report::is_empty`），
//!    缺了这步长按语音永远不结束。
//! 4. **`RIDEV_INPUTSINK`**：窗口隐藏且不在前台也要收。
//! 5. **独立线程 + 独立隐藏窗口**，不与 egui / Win32 主循环耦合，窗口不 ShowWindow。
//!
//! ## VKey → usage 反查表为什么能成立
//!
//! 遥控器发的是**自己的 usage 编号**（`keymap.rs` 开头有真机结论），但系统按标准
//! 键盘页解释它们并给出 VKey。逐个反查可完整还原 23 键，例如：
//! `power=0x17`('t')→VK 0x54、`voice=0x10`('m')→VK 0x4D、`back=0x09`('f')→VK 0x46、
//! `rewind=0x36`(',')→VK_OEM_COMMA、`play_pause=0x31`('\\')→VK_OEM_5、
//! 方向键 `0x4F/0x50/0x51/0x52`→VK Right/Left/Down/Up。见 `vk_to_usage()`。

use std::ffi::c_void;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::Mutex;
use std::time::Duration;
use windows::core::w;
use windows::Win32::Foundation::{GetLastError, HANDLE, HINSTANCE, HWND, LPARAM, LRESULT, WPARAM};
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::UI::Input::{
    GetRawInputData, GetRawInputDeviceInfoW, GetRawInputDeviceList, RegisterRawInputDevices,
    HRAWINPUT, RAWINPUT, RAWINPUTDEVICE, RAWINPUTDEVICELIST, RAWINPUTHEADER, RIDEV_INPUTSINK,
    RIDI_DEVICENAME, RID_INPUT, RIM_TYPEKEYBOARD,
};
use windows::Win32::UI::WindowsAndMessaging::{
    CreateWindowExW, DefWindowProcW, DispatchMessageW, GetMessageW, RegisterClassW,
    TranslateMessage, MSG, WINDOW_EX_STYLE, WNDCLASSW, WS_OVERLAPPED,
};

/// 已读到的按键事件总数（界面 / 统计可见）
pub static REPORTS: AtomicU64 = AtomicU64::new(0);
/// Raw Input 注册成功次数（原义"打开句柄次数"，语义对齐到新通道）
pub static REOPENED: AtomicU64 = AtomicU64::new(0);
/// 设备巡检里"没发现遥控器"的轮数（诊断：休眠 / 未配对 / 被改坏）
pub static MISS: AtomicU64 = AtomicU64::new(0);
/// 最近一次收到按键的时刻（Unix ms）。`is_key_online()` 靠它判三态。
pub static LAST_REPORT_MS: AtomicU64 = AtomicU64::new(0);
/// 解析失败 / 未映射 VKey 的累计次数
pub static FAILED: AtomicU64 = AtomicU64::new(0);
/// 当前系统 Raw Input 设备总数
pub static TOTAL_SEEN: AtomicU64 = AtomicU64::new(0);
/// 只含 0x0C 的快照数（交接文档 §6：需真机确认 0x0C 是心跳还是真实键）
pub static GHOST_SNAPSHOTS: AtomicU64 = AtomicU64::new(0);
/// WM_INPUT 已到达、但设备名没被认领而丢弃的次数（区分「系统没投递」与「过滤规则错」）
pub static FILTERED: AtomicU64 = AtomicU64::new(0);
/// RIDI_DEVICENAME 读取失败次数（名字读不出来 = 过滤必然全落空，必须留痕）
pub static NAME_FAIL: AtomicU64 = AtomicU64::new(0);
/// 诊断逃生舱：`IPRC1000_RAWINPUT_ANY=1` 时不按设备名过滤，用来确认 WM_INPUT 到底有没有到。
static FORCE_ANY: AtomicBool = AtomicBool::new(false);
/// 巡检里是否见过 VID/PID 精确匹配（见过就关掉蓝牙兜底，避免误认蓝牙物理键盘）。
static EXACT_SEEN: AtomicBool = AtomicBool::new(false);
/// 「已认领设备」只打一次日志，避免刷屏。
static CLAIM_LOGGED: AtomicBool = AtomicBool::new(false);

/// 当前按住的 usage 集合 —— 模拟 HID 报告的快照语义
static HELD: Mutex<[bool; 256]> = Mutex::new([false; 256]);

fn now_ms() -> u64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_millis() as u64)
        .unwrap_or(0)
}

/// 距上次收到按键多少毫秒（u64::MAX = 从未收到）
pub fn report_age_ms() -> u64 {
    let t = LAST_REPORT_MS.load(Ordering::Relaxed);
    if t == 0 {
        return u64::MAX;
    }
    now_ms().saturating_sub(t)
}

/// 精确认领：VID/PID 或设备名带 IPRC。
///
/// VID/PID 不再写死 —— 由 `device_id` 按**蓝牙设备名**发现后提供
/// （`BTHPORT\...\Devices\*\Name` 含 IPRC → 取它的 VID/PID）。
/// 这里用"裸十六进制片段"而不是 `VID&xxxx` 全串，是因为 Raw Input 的设备名有多种形态：
///   `\\?\HID#VID&00020a5c_PID&8502#...`、`\\?\BTHENUM#{00001124…}_DEV&00020A5C#...`
/// 只匹配 `VID&` 会漏掉 `DEV&` 那种。发现失败时 `device_id` 回落到 0x0A5C/0x8502，
/// 行为与改造前一致。
fn is_exact_remote(name: &str) -> bool {
    let u = name.to_uppercase();
    if u.contains("IPRC") {
        return true;
    }
    let (vid, pid) = crate::device_id::vid_pid();
    let v = format!("{vid:04X}");
    let p = format!("{pid:04X}");
    u.contains(&v) && (u.contains(&p) || u.contains("IPRC"))
}

/// 认领规则：返回 Some(原因) 表示这是遥控器，原因写进日志便于回溯。
///
/// ★ 2026-11-06 客户机复盘：只认 `0A5C+8502` 太脆。hidbth 给蓝牙 HID 生成的
///   `RIDI_DEVICENAME` 可能不带 VID/PID（BTHENUM 路径里只有服务 GUID + 实例号），
///   硬编码会让 WM_INPUT 全部静默丢弃，而日志只显示「暂未发现设备」——
///   分不清「设备不在」「名字读不出来」「过滤规则不对」三种完全不同的故障。
fn remote_reason(name: &str) -> Option<&'static str> {
    if FORCE_ANY.load(Ordering::Relaxed) {
        return Some("强制放行(诊断模式)");
    }
    if is_exact_remote(name) {
        return Some("VID/PID 精确匹配");
    }
    // 兜底：蓝牙 HID 服务 GUID {00001124-0000-1000-8000-00805F9B34FB}。
    // 仅当系统里从没出现过精确匹配时生效，避免误认蓝牙物理键盘；
    // 实际按键另有 RIM_TYPEKEYBOARD 这道闸。
    if !EXACT_SEEN.load(Ordering::Relaxed) {
        let u = name.to_uppercase();
        if u.contains("BTHENUM") && u.contains("00001124") {
            return Some("蓝牙HID兜底(无精确匹配)");
        }
    }
    None
}

fn is_remote_name(name: &str) -> bool {
    remote_reason(name).is_some()
}

/// 读一个 Raw Input 设备的接口名（用于按设备过滤）。
///
/// ★ 2026-11-06 客户机复盘：原来「先问大小、再把同一个数值回填」的两段式调用有坑——
///   `GetRawInputDeviceInfoW(RIDI_DEVICENAME)` 的 `pcbSize` 文档写**字节数**，
///   实际对 Unicode 设备名常按**字符数**解释。回填值一旦被判成「缓冲区不足」就返回
///   `u32::MAX`，`device_name()` 静默返回 None ⇒ 设备名过滤永远不命中 ⇒ WM_INPUT 全丢。
///   改成一次性给足 512 WCHAR 的固定缓冲、按字节填 size、从第一个 NUL 截断：
///   两种语义下都成立，且不再依赖返回值的解释。
fn device_name(h: HANDLE) -> Option<String> {
    let mut buf = vec![0u16; 512];
    let mut size = (buf.len() * std::mem::size_of::<u16>()) as u32;
    let ret = unsafe {
        GetRawInputDeviceInfoW(
            h,
            RIDI_DEVICENAME,
            Some(buf.as_mut_ptr() as *mut c_void),
            &mut size,
        )
    };
    if ret == u32::MAX {
        return None;
    }
    let end = buf.iter().position(|&c| c == 0).unwrap_or(buf.len());
    if end == 0 {
        return None;
    }
    Some(String::from_utf16_lossy(&buf[..end]))
}

/// 列出全部 Raw Input 设备名（不打开句柄，因此不受 ACL 限制）。
fn list_devices() -> Vec<String> {
    let sz = std::mem::size_of::<RAWINPUTDEVICELIST>() as u32;
    let mut n = 0u32;
    unsafe { GetRawInputDeviceList(None, &mut n, sz); }
    if n == 0 {
        return Vec::new();
    }
    let mut devs = vec![RAWINPUTDEVICELIST::default(); n as usize];
    let got = unsafe { GetRawInputDeviceList(Some(devs.as_mut_ptr()), &mut n, sz) };
    if got == u32::MAX {
        return Vec::new();
    }
    devs[..(n as usize).min(devs.len())]
        .iter()
        .filter_map(|d| match device_name(d.hDevice) {
            Some(s) => Some(s),
            None => {
                let k = NAME_FAIL.fetch_add(1, Ordering::Relaxed) + 1;
                if k <= 3 || k % 100 == 0 {
                    let e = unsafe { GetLastError().0 };
                    eprintln!("[HID] RIDI_DEVICENAME 读取失败（累计 {k} 次）err={e}");
                }
                None
            }
        })
        .collect()
}

/// VKey（系统对遥控器 usage 的标准键盘页解释）→ 遥控器原始 usage。
///
/// ★ 覆盖 `keymap::PLAIN` 的全部 23 键，不只是常用键：
///   OEM 键（0x2D..0x38）与数字键必须映射，否则 `rewind(0x36)`、
///   `play_pause(0x31)`、`volume_up(0x1F)` 会被静默丢掉。
fn vk_to_usage(vk: u16) -> Option<u8> {
    Some(match vk {
        // 字母 A-Z → 0x04..0x1D（含 voice=0x10 对应 'm'/VK 0x4D）
        0x41..=0x5A => (vk - 0x41) as u8 + 0x04,
        // 数字 1..9 / 0 → 0x1E..0x27
        0x31..=0x39 => (vk - 0x31) as u8 + 0x1E,
        0x30 => 0x27,
        // 控制键
        0x0D => 0x28, // Enter / OK
        0x1B => 0x29, // Esc
        0x08 => 0x2A, // Backspace
        0x09 => 0x2B, // Tab
        0x20 => 0x2C, // Space
        // 符号键（US 键盘页 0x2D..0x38）
        0xBD => 0x2D, // -        VK_OEM_MINUS
        0xBB => 0x2E, // =        VK_OEM_PLUS
        0xDB => 0x2F, // [        VK_OEM_4
        0xDD => 0x30, // ]        VK_OEM_6
        0xDC => 0x31, // \        VK_OEM_5   ← play_pause
        0xBA => 0x33, // ;        VK_OEM_1
        0xDE => 0x34, // '        VK_OEM_7
        0xC0 => 0x35, // `        VK_OEM_3
        0xBC => 0x36, // ,        VK_OEM_COMMA ← rewind
        0xBE => 0x37, // .        VK_OEM_PERIOD
        0xBF => 0x38, // /        VK_OEM_2
        // 编辑键
        0x2D => 0x49, // Insert
        0x24 => 0x4A, // Home
        0x21 => 0x4B, // PageUp
        0x2E => 0x4C, // Delete
        0x23 => 0x4D, // End
        0x22 => 0x4E, // PageDown
        // 方向键（遥控器 up/down/left/right）
        0x27 => 0x4F, // Right
        0x25 => 0x50, // Left
        0x28 => 0x51, // Down
        0x26 => 0x52, // Up
        // 功能键
        0x70..=0x7B => (vk - 0x70) as u8 + 0x3A, // F1..F12
        0x14 => 0x39,                            // CapsLock
        // 修饰键
        0xA0 | 0x10 => 0xE1, // LShift
        0xA1 => 0xE5,        // RShift
        0xA2 | 0x11 => 0xE0, // LCtrl
        0xA3 => 0xE4,        // RCtrl
        0xA4 | 0x12 => 0xE2, // LAlt
        0xA5 => 0xE6,        // RAlt
        0x5B => 0xE3,        // LWin
        0x5C => 0xE7,        // RWin
        _ => return None,
    })
}

fn dispatch(usages: &[u8]) {
    let Some(sh) = crate::backend::shared() else { return };
    let Ok(mut b) = sh.lock() else { return };
    b.handle_hid_report(usages);
}

/// 收到一个 Raw Input 键事件：更新 HELD，产出**当前全量快照**。
fn on_key(vk: u16, down: bool) {
    let Some(u) = vk_to_usage(vk) else {
        // 静默丢键是排查噩梦：留痕，方便真机补表（交接文档 §6 的确认流程）
        let n = FAILED.fetch_add(1, Ordering::Relaxed) + 1;
        if n <= 10 || n % 50 == 0 {
            eprintln!("[HID] 未映射 VKey=0x{vk:02X}（累计 {n} 次）—— 请把该键补进 vk_to_usage");
        }
        return;
    };
    let snapshot = {
        let Ok(mut g) = HELD.lock() else { return };
        g[u as usize] = down;
        (1..256usize).filter(|&i| g[i]).map(|i| i as u8).collect::<Vec<u8>>()
    };
    // 交接文档 §6：0x0C 既可能是"按住期间的心跳"，也可能是真实键（I）。
    // Raw Input 没有 HID 层心跳，但 backend 仍会把只含 0x0C 的快照判成幽灵报文丢掉。
    // 保留原行为，单独计数，等真机逐键确认后再决定是否放宽。
    if snapshot.len() == 1 && snapshot[0] == 0x0C {
        let n = GHOST_SNAPSHOTS.fetch_add(1, Ordering::Relaxed) + 1;
        if n == 1 {
            eprintln!("[HID] ⚠ 出现只含 0x0C 的快照（会被 report::GHOST 丢弃）；真机确认见交接文档 §6");
        }
    }
    REPORTS.fetch_add(1, Ordering::Relaxed);
    LAST_REPORT_MS.store(now_ms(), Ordering::Relaxed);
    // snapshot 为空 = 所有键松开 ⇒ `report::is_empty` 为 true ⇒ 语音长按正确结束
    dispatch(&snapshot);
}

extern "system" fn wndproc(hwnd: HWND, msg: u32, wparam: WPARAM, lparam: LPARAM) -> LRESULT {
    const WM_INPUT_MSG: u32 = 0x00FF;
    if msg == WM_INPUT_MSG {
        let mut size = 0u32;
        let hr = HRAWINPUT(lparam.0);
        unsafe {
            GetRawInputData(
                hr,
                RID_INPUT,
                None,
                &mut size,
                std::mem::size_of::<RAWINPUTHEADER>() as u32,
            );
        }
        if size > 0 && size <= 4096 {
            let mut buf = vec![0u8; size as usize];
            let got = unsafe {
                GetRawInputData(
                    hr,
                    RID_INPUT,
                    Some(buf.as_mut_ptr() as *mut c_void),
                    &mut size,
                    std::mem::size_of::<RAWINPUTHEADER>() as u32,
                )
            };
            if got == size {
                let ri = unsafe { &*(buf.as_ptr() as *const RAWINPUT) };
                if ri.header.dwType == RIM_TYPEKEYBOARD.0 {
                    let name = device_name(ri.header.hDevice).unwrap_or_default();
                    let kb = unsafe { ri.data.keyboard };
                    if let Some(reason) = remote_reason(&name) {
                        if is_exact_remote(&name) {
                            EXACT_SEEN.store(true, Ordering::Relaxed);
                        }
                        if !CLAIM_LOGGED.swap(true, Ordering::Relaxed) {
                            eprintln!("[HID] ✓ 已认领遥控器 Raw Input 设备（{reason}）：{name}");
                        }
                        // Raw Input 的 keyboard.Message 是 WM_KEYDOWN/UP（含 SYS 变体）
                        let down = matches!(kb.Message, 0x0100 | 0x0104);
                        let up = matches!(kb.Message, 0x0101 | 0x0105);
                        if down || up {
                            on_key(kb.VKey, down);
                        }
                    } else {
                        // ★ 关键可观测性：区分「系统根本没投递 WM_INPUT」与「名字没认上」。
                        //   客户机日志里长期只有「暂未发现设备」，就是缺了这一行。
                        let n = FILTERED.fetch_add(1, Ordering::Relaxed) + 1;
                        if n == 1 || n % 50 == 0 {
                            eprintln!(
                                "[HID] WM_INPUT 已到达但未认领（累计 {n} 次）VKey=0x{:02X} name={name:?}",
                                kb.VKey
                            );
                        }
                    }
                }
            }
        }
        return LRESULT(0);
    }
    unsafe { DefWindowProcW(hwnd, msg, wparam, lparam) }
}

/// 启动消费链路（幂等，只会起一次）。
pub fn start() {
    static STARTED: std::sync::Once = std::sync::Once::new();
    STARTED.call_once(|| {
        let force = std::env::var("IPRC1000_RAWINPUT_ANY")
            .map(|v| v == "1")
            .unwrap_or(false);
        FORCE_ANY.store(force, Ordering::Relaxed);
        if force {
            eprintln!("[HID] ⚠ 诊断模式 IPRC1000_RAWINPUT_ANY=1：不按设备名过滤，物理键盘也会触发动作");
        }

        // 判据 = Enum\BTHENUM\{00001124-...} 的 Service 是否为 HidBth（客户机 S2 实测）。
        // 读 Enum\HID 残留节点会得到假绿灯：HardwareID 里永远有 HID_DEVICE_SYSTEM_KEYBOARD。
        match crate::device_probe::bt_hid_bound() {
            Some(true) => eprintln!(
                "[HID] 蓝牙 HID 服务已绑定（Service=HidBth），按键通道正常。★ 不要改造遥控器。"
            ),
            Some(false) => eprintln!(
                "[HID] ⚠ HID 服务节点在但没有绑定 HidBth ⇒ 主机侧没有 PSM 0x0011 监听者，\
                 遥控器连接会被 REFUSED_PSM、一个 HID 报告都收不到。\
                 修法：删除 IPRC1000 后重新配对（--restore-keyboard 对这种情况无效）。"
            ),
            None => eprintln!(
                "[HID] ⚠ 没有 Enum\\BTHENUM\\{{00001124-...}} 节点 ⇒ SDP 未成功 / 未配对，\
                 主机侧不会建 HID 通道。修法：删除设备重新配对（--restore-keyboard 无效）。"
            ),
        }

        // ★ 启动时就把 Raw Input 的**真实设备名**打出来。客户机日志里长期是
        //   「键盘模式正确」+「暂未发现设备」并存，却看不到任何设备名，
        //   导致分不清「设备不在 / 名字读不出来 / 过滤规则不对」。
        {
            let names = list_devices();
            TOTAL_SEEN.store(names.len() as u64, Ordering::Relaxed);
            let exact = names.iter().filter(|n| is_exact_remote(n)).count();
            if exact > 0 {
                EXACT_SEEN.store(true, Ordering::Relaxed);
            }
            eprintln!("[HID] 启动枚举 Raw Input：{total} 个设备，VID/PID 精确匹配 {exact} 个", total = names.len());
            // ★ 必须打**全部**：之前 take(12) 把真正的遥控器截掉了，
            //   现场只能看到一堆鼠标键盘，反而更迷惑。
            for (i, n) in names.iter().enumerate() {
                eprintln!(
                    "[HID]   {i:2}. [{}] {n}",
                    remote_reason(n).unwrap_or("未认领")
                );
            }
            if names.is_empty() {
                eprintln!("[HID]   · 列表为空：遥控器休眠/蓝牙断链，或 RIDI_DEVICENAME 读取失败");
            }
        }

        // 轻量设备巡检：保留 MISS / TOTAL_SEEN 的可观测性（区分"没唤醒"和"读不到"）
        std::thread::Builder::new()
            .name("hid-device-watch".into())
            .spawn(|| loop {
                let names = list_devices();
                TOTAL_SEEN.store(names.len() as u64, Ordering::Relaxed);
                if names.iter().any(|n| is_exact_remote(n)) {
                    EXACT_SEEN.store(true, Ordering::Relaxed);
                }
                let remote = names.iter().filter(|n| is_remote_name(n)).count();
                if remote == 0 {
                    let n = MISS.fetch_add(1, Ordering::Relaxed) + 1;
                    if n == 1 || n % 15 == 0 {
                        eprintln!(
                            "[HID] 暂未认领遥控器 Raw Input 设备（累计 {n} 轮；枚举 {} 个；WM_INPUT 到达但被过滤 {} 次；名字读取失败 {} 次）",
                            names.len(),
                            FILTERED.load(Ordering::Relaxed),
                            NAME_FAIL.load(Ordering::Relaxed),
                        );
                        for (i, s) in names.iter().enumerate() {
                            eprintln!("[HID]   {i:2}. {s}");
                        }
                    }
                }
                std::thread::sleep(Duration::from_secs(2));
            })
            .ok();

        // Raw Input 消息线程：独立隐藏窗口 + 自己的消息队列
        std::thread::Builder::new()
            .name("hid-rawinput".into())
            .spawn(|| unsafe {
                let hmod = GetModuleHandleW(None).unwrap_or_default();
                let hinst = HINSTANCE(hmod.0);
                let cls = w!("IPRC1000RawInput");
                let wc = WNDCLASSW {
                    lpfnWndProc: Some(wndproc),
                    hInstance: hinst,
                    lpszClassName: cls,
                    ..Default::default()
                };
                if RegisterClassW(&wc) == 0 {
                    let e = GetLastError().0;
                    if e != 1410 {
                        // 1410 = ERROR_CLASS_ALREADY_EXISTS（多次启动同一进程内不重复注册）
                        eprintln!("[HID] RegisterClassW 失败 err={e}");
                        return;
                    }
                }
                let hwnd = CreateWindowExW(
                    WINDOW_EX_STYLE(0),
                    cls,
                    w!("iprc-rawinput"),
                    WS_OVERLAPPED,
                    0, 0, 0, 0,
                    None, None, hinst, None,
                );
                if hwnd.0 == 0 {
                    eprintln!("[HID] CreateWindowExW 失败 err={}", GetLastError().0);
                    return;
                }
                let rid = RAWINPUTDEVICE {
                    usUsagePage: 0x01, // Generic Desktop
                    usUsage: 0x06,     // Keyboard
                    dwFlags: RIDEV_INPUTSINK, // 窗口不在前台也收
                    hwndTarget: hwnd,
                };
                if let Err(e) = RegisterRawInputDevices(
                    &[rid],
                    std::mem::size_of::<RAWINPUTDEVICE>() as u32,
                ) {
                    eprintln!("[HID] RegisterRawInputDevices 失败 {e}");
                    return;
                }
                REOPENED.fetch_add(1, Ordering::Relaxed);
                eprintln!("[HID] Raw Input 已注册（不用打开 \\kbd，绕开 ACL）");

                let mut msg = MSG::default();
                while GetMessageW(&mut msg, None, 0, 0).as_bool() {
                    let _ = TranslateMessage(&msg);
                    DispatchMessageW(&msg);
                }
            })
            .ok();
    });
}

/// 现场诊断：`--hid-probe`。列 Raw Input 设备，**不再尝试打开 \kbd**。
pub fn probe() {
    FORCE_ANY.store(
        std::env::var("IPRC1000_RAWINPUT_ANY").map(|v| v == "1").unwrap_or(false),
        Ordering::Relaxed,
    );
    let names = list_devices();
    TOTAL_SEEN.store(names.len() as u64, Ordering::Relaxed);
    println!("全系统 Raw Input 设备：{} 个", names.len());
    // ★ 列**全部**设备名而非只列匹配的：客户机定位靠的正是这份清单。
    for n in &names {
        println!("  [{}] {n}", remote_reason(n).unwrap_or("未认领"));
    }
    let remote: Vec<&String> = names.iter().filter(|n| is_remote_name(n)).collect();
    println!("其中认领为遥控器：{} 个", remote.len());
    println!("按键通道：Raw Input（WM_INPUT），不打开 \\kbd，绕开 SYSTEM-only ACL，无需管理员");
    println!(
        "运行时统计：WM_INPUT 到达但被过滤 {} 次 / RIDI_DEVICENAME 失败 {} 次 / 已收按键 {} 条",
        FILTERED.load(Ordering::Relaxed),
        NAME_FAIL.load(Ordering::Relaxed),
        REPORTS.load(Ordering::Relaxed),
    );
    if remote.is_empty() {
        println!("★ 没认领到遥控器 —— 把上面这份设备清单发回来即可精确适配过滤规则。");
        println!("  临时验证：设环境变量 IPRC1000_RAWINPUT_ANY=1 后重跑主程序，若按键生效即证明是过滤规则问题。");
        println!("  若清单为空：按一下唤醒 / 重新配对；被改造过则先 --restore-keyboard 并重启蓝牙适配器。");
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 遥控器 23 键：系统按标准键盘页解释出来的 VKey，必须能反查回原始 usage。
    /// 少了任何一条，那个键就会在 Raw Input 通道上静默失效（且界面毫无提示）。
    #[test]
    fn all_remote_usages_round_trip() {
        // (遥控器 usage, 系统 VKey) —— 与 keymap::PLAIN 逐行对齐
        let cases: &[(u8, u16)] = &[
            (0x17, 0x54), // power   't'
            (0x09, 0x46), // back    'f'
            (0x11, 0x4E), // menu    'n'
            (0x07, 0x44), // exit    'd'
            (0x0B, 0x48), // info    'h'
            (0x28, 0x0D), // ok      Enter
            (0x52, 0x26), // up
            (0x51, 0x28), // down
            (0x50, 0x25), // left
            (0x4F, 0x27), // right
            (0x08, 0x45), // favorite 'e'
            (0x10, 0x4D), // voice   'm'  ← 语音键，丢了就没有语音
            (0x0D, 0x4A), // account 'j'
            (0x31, 0xDC), // play_pause '\'
            (0x19, 0x56), // fast_forward 'v'
            (0x36, 0xBC), // rewind  ','
            (0x04, 0x41), // channel_up   'a'
            (0x05, 0x42), // channel_down 'b'
            (0x1F, 0x32), // volume_up  '2'
            (0x20, 0x33), // volume_down '3'
            (0x0A, 0x47), // mute    'g'
            (0x06, 0x43), // last    'c'
            (0x16, 0x53), // square  's'
        ];
        assert_eq!(cases.len(), 23);
        for (usage, vk) in cases {
            assert_eq!(vk_to_usage(*vk), Some(*usage), "VKey 0x{vk:02X} 应还原为 usage 0x{usage:02X}");
        }
    }

    /// 全松开必须产出空列表：`report::is_empty(&[])` 是语音长按结束的唯一判据。
    #[test]
    fn release_produces_empty_snapshot() {
        assert!(iprc1000_native::report::is_empty(&[]));
        assert!(!iprc1000_native::report::is_ghost_only(&[]));
    }

    /// 设备过滤：物理键盘 / 别的 HID 不能被当成遥控器。
    #[test]
    fn device_filter_requires_both_ids() {
        assert!(is_remote_name(r"\\?\HID#VID&00020a5c_PID&8502#8&1&0000#{884b96c3-...}"));
        assert!(!is_remote_name(r"\\?\HID#VID&00020460_PID&8502#..."));
        assert!(!is_remote_name(r"\\?\ACPI#StandardPS2Keyboard#..."));
    }

    /// 客户机复盘（2026-11-06）：hidbth 给蓝牙 HID 生成的 RIDI_DEVICENAME
    /// 可能不带 VID/PID，必须有兜底；但一旦出现精确匹配就关掉兜底，
    /// 否则同一台机器上的蓝牙物理键盘也会被当成遥控器。
    #[test]
    fn device_claim_rules() {
        EXACT_SEEN.store(false, Ordering::Relaxed);
        let bt = r"\\?\BTHENUM#{00001124-0000-1000-8000-00805f9b34fb}_DEV&00020A5C#8&1&0000#{884b96c3-...}";
        assert_eq!(remote_reason(bt), Some("蓝牙HID兜底(无精确匹配)"));
        assert_eq!(remote_reason(r"\\?\HID#VID_046D_PID_C52B#..."), None);

        EXACT_SEEN.store(true, Ordering::Relaxed);
        assert_eq!(remote_reason(bt), None, "有精确匹配时不能再兜底认领蓝牙物理键盘");
        assert!(remote_reason(r"\\?\HID#VID&00020a5c_PID&8502#...").is_some());
        EXACT_SEEN.store(false, Ordering::Relaxed);
    }
}
