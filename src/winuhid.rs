//! WinUHid 虚拟 HID 键盘注入 —— **Rust 原生版**（与 `iprc1000_gui_dotnet/WinUhidInjector.cs` 等价）。
//!
//! 为什么要有这个文件（2026-10-30，原生内核第 0 步）：
//!   现在的按键链路是「ETW → 桥（Rust）→ UDP → C# → WinUHid」，中间多一次进程跳。
//!   第 0 步要验证的是：**桥自己直接把按键注入系统**（不发 UDP），
//!   这样既能量出"进程内直通"的真实延迟，又能证明原生内核的注入模块可行。
//!
//! API（与 C# 侧逐字对齐，改一处必须同步另一侧）：
//!   WinUHidGetDriverInterfaceVersion()            -> u32
//!   WinUHidCreateDevice(const config*)            -> handle（0 = 失败）
//!   WinUHidStartDevice(handle, null, null)        -> i32（非 0 成功）
//!   WinUHidSubmitInputReport(handle, u8*, u32)    -> i32（非 0 成功）
//!   WinUHidDestroyDevice(handle)
//!
//! ⚠ 三个不能想当然的地方（都是 C# 侧踩过的）：
//!   1. `DeviceConfig` 是 **Pack=1**，x64 上指针落在偏移 12 —— 不对齐。这里用原始字节
//!      缓冲手工按偏移写，避免 `#[repr(packed)]` 里取字段引用（E0793）带来的未定义行为。
//!   2. 报告是**整份 8 字节快照**（修饰键 1 + 保留 1 + 6 个 usage），不是"一个键一帧"；
//!      松开某个键必须重发"少一个键"的整份快照，否则会永久卡键。
//!   3. 修饰键 + 普通键同时按要**分两帧**（先只发修饰键，12ms 后再发主键），
//!      否则系统会漏掉修饰。

use std::ffi::c_void;

/// 标准键盘报告描述符（与 C# `KeyboardDescriptor` 逐字节一致：63 字节）
const KEYBOARD_DESCRIPTOR: [u8; 63] = [
    0x05, 0x01, 0x09, 0x06, 0xA1, 0x01, 0x05, 0x07, 0x19, 0xE0, 0x29, 0xE7, 0x15, 0x00, 0x25,
    0x01, 0x75, 0x01, 0x95, 0x08, 0x81, 0x02, 0x95, 0x01, 0x75, 0x08, 0x81, 0x01, 0x95, 0x05,
    0x75, 0x01, 0x05, 0x08, 0x19, 0x01, 0x29, 0x05, 0x91, 0x02, 0x95, 0x01, 0x75, 0x03, 0x91,
    0x01, 0x95, 0x06, 0x75, 0x08, 0x15, 0x00, 0x25, 0x73, 0x05, 0x07, 0x19, 0x00, 0x29, 0x73,
    0x81, 0x00, 0xC0,
];

/// DeviceConfig 的字节长度（Pack=1）：4 + 2*4 + 8 + 16 + 8 + 8 + 4 = 56
const CFG_LEN: usize = 56;

// 字段偏移（Pack=1，x64）
// 这两个字段我们始终写 0（supportedEvents=0 / containerId 全零让 Windows 分配），
// 常量留着是为了和 C# 的 `DeviceConfig` 逐字段对照 —— 以后要改布局时不会数错偏移。
#[allow(dead_code)]
const OFF_SUPPORTED_EVENTS: usize = 0;
const OFF_VENDOR_ID: usize = 4;
const OFF_PRODUCT_ID: usize = 6;
const OFF_VERSION: usize = 8;
const OFF_DESC_LEN: usize = 10;
const OFF_DESC_PTR: usize = 12;
#[allow(dead_code)]
const OFF_CONTAINER: usize = 20;
const OFF_INSTANCE_ID: usize = 36;
const OFF_HARDWARE_IDS: usize = 44;
const OFF_READ_PERIOD_US: usize = 52;

type FnGetVersion = unsafe extern "system" fn() -> u32;
type FnCreateDevice = unsafe extern "system" fn(*const u8) -> *mut c_void;
type FnStartDevice = unsafe extern "system" fn(*mut c_void, *mut c_void, *mut c_void) -> i32;
type FnSubmitReport = unsafe extern "system" fn(*mut c_void, *const u8, u32) -> i32;
type FnDestroyDevice = unsafe extern "system" fn(*mut c_void);

#[link(name = "kernel32")]
extern "system" {
    fn LoadLibraryW(path: *const u16) -> *mut c_void;
    fn GetProcAddress(module: *mut c_void, name: *const u8) -> *mut c_void;
}

/// 一个已创建的虚拟键盘。Drop 时销毁设备（否则进程退出后设备槽位会被占住）。
pub struct Injector {
    device: *mut c_void,
    submit: FnSubmitReport,
    destroy: FnDestroyDevice,
    /// 已提交的帧数（自检/诊断用）
    pub frames: u64,
}

// 句柄本身是进程内资源；桥在单线程回调里用它，跨线程传递只是为了让 struct 能放进共享状态
unsafe impl Send for Injector {}

impl Drop for Injector {
    fn drop(&mut self) {
        unsafe { (self.destroy)(self.device) };
    }
}

impl Injector {
    /// 加载 DLL 并创建虚拟键盘。失败返回 Err（原因给日志用）。
    pub fn open() -> Result<Self, String> {
        let dll = find_dll().ok_or_else(|| "未找到 WinUHid.dll".to_string())?;
        let wide: Vec<u16> = dll
            .to_string_lossy()
            .encode_utf16()
            .chain(std::iter::once(0))
            .collect();
        let module = unsafe { LoadLibraryW(wide.as_ptr()) };
        if module.is_null() {
            return Err(format!("LoadLibraryW 失败：{}", dll.display()));
        }

        unsafe fn sym(module: *mut c_void, name: &str) -> *mut c_void {
            let mut c = Vec::with_capacity(name.len() + 1);
            c.extend_from_slice(name.as_bytes());
            c.push(0);
            GetProcAddress(module, c.as_ptr())
        }

        unsafe {
            let p_ver = sym(module, "WinUHidGetDriverInterfaceVersion");
            let p_new = sym(module, "WinUHidCreateDevice");
            let p_start = sym(module, "WinUHidStartDevice");
            let p_submit = sym(module, "WinUHidSubmitInputReport");
            let p_destroy = sym(module, "WinUHidDestroyDevice");
            if p_ver.is_null() || p_new.is_null() || p_start.is_null() || p_submit.is_null() || p_destroy.is_null() {
                return Err("WinUHid.dll 缺少导出函数（版本不匹配？）".to_string());
            }
            let ver: FnGetVersion = std::mem::transmute(p_ver);
            if ver() < 1 {
                return Err("WinUHid 驱动未安装或未就绪（interface version < 1）".to_string());
            }
            let create: FnCreateDevice = std::mem::transmute(p_new);
            let start: FnStartDevice = std::mem::transmute(p_start);
            let submit: FnSubmitReport = std::mem::transmute(p_submit);
            let destroy: FnDestroyDevice = std::mem::transmute(p_destroy);

            // ① 设备配置（手工按偏移写，见文件头说明）
            let mut cfg = [0u8; CFG_LEN];
            cfg[OFF_VENDOR_ID..OFF_VENDOR_ID + 2].copy_from_slice(&0u16.to_le_bytes());
            cfg[OFF_PRODUCT_ID..OFF_PRODUCT_ID + 2].copy_from_slice(&1u16.to_le_bytes());
            cfg[OFF_VERSION..OFF_VERSION + 2].copy_from_slice(&1u16.to_le_bytes());
            cfg[OFF_DESC_LEN..OFF_DESC_LEN + 2]
                .copy_from_slice(&(KEYBOARD_DESCRIPTOR.len() as u16).to_le_bytes());
            let desc_ptr = KEYBOARD_DESCRIPTOR.as_ptr() as usize;
            cfg[OFF_DESC_PTR..OFF_DESC_PTR + 8].copy_from_slice(&(desc_ptr as u64).to_le_bytes());
            // containerId 全零：让 Windows 分配
            // instanceId 用固定串，与 C# 侧一致 —— 两边同时跑会抢同一个设备，所以测试时要停掉 C#
            let instance: Vec<u16> = "Iprc1000AdapterKeyboard\0".encode_utf16().collect();
            let inst_ptr = instance.as_ptr() as usize;
            cfg[OFF_INSTANCE_ID..OFF_INSTANCE_ID + 8].copy_from_slice(&(inst_ptr as u64).to_le_bytes());
            cfg[OFF_HARDWARE_IDS..OFF_HARDWARE_IDS + 8].copy_from_slice(&0u64.to_le_bytes());
            cfg[OFF_READ_PERIOD_US..OFF_READ_PERIOD_US + 4].copy_from_slice(&0u32.to_le_bytes());

            let dev = create(cfg.as_ptr());
            if dev.is_null() {
                return Err("WinUHidCreateDevice 失败（设备槽位可能被占 / 驱动未生效）".to_string());
            }
            if start(dev, std::ptr::null_mut(), std::ptr::null_mut()) == 0 {
                destroy(dev);
                return Err("WinUHidStartDevice 失败".to_string());
            }
            Ok(Injector { device: dev, submit, destroy, frames: 0 })
        }
    }

    /// 提交一帧 8 字节键盘报告。rc=0 表示驱动拒绝。
    pub fn submit_report(&mut self, report: &[u8; 8]) -> bool {
        let rc = unsafe { (self.submit)(self.device, report.as_ptr(), 8) };
        if rc != 0 {
            self.frames += 1;
            true
        } else {
            false
        }
    }
}

/// 把"当前按住集合"编成一份 HID 报告（整份快照）。
///
/// ★ 2026-10-30（真机实测修正）：**必须按映射表展开，不能把遥控器 usage 直通**。
///   遥控器 `back` 发的是 `0x09`，而 `0x09` 在键盘页上就是字母 `f`（Backspace 是 `0x2A`）。
///   方向键能对只是巧合。映射见 `keymap::PLAIN`（与 C# 的 `Defaults.Plain()` 一致）。
pub fn report_of(held: &[bool; 256]) -> [u8; 8] {
    let mut r = [0u8; 8];
    let mut slot = 2;
    for u in 0..256usize {
        if !held[u] {
            continue;
        }
        for vk in crate::keymap::vks_of(u as u8) {
            if let Some(bit) = crate::keymap::modifier_bit(vk) {
                r[0] |= bit; // 修饰键进 byte0 位图
                continue;
            }
            if let Some(hu) = crate::keymap::usage_of(vk) {
                if !r[2..8].contains(&hu) && slot < 8 {
                    r[slot] = hu;
                    slot += 1;
                }
            }
        }
    }
    r
}

/// 依次尝试常见位置找 WinUHid.dll（与 C# `DllPath()` 的顺序一致）。
/// 环境变量 `WINUHID_DLL` 优先 —— 开发期指针向 target 目录时不用来回拷文件。
fn find_dll() -> Option<std::path::PathBuf> {
    if let Ok(p) = std::env::var("WINUHID_DLL") {
        let pb = std::path::PathBuf::from(p);
        if pb.exists() {
            return Some(pb);
        }
    }
    let exe = std::env::current_exe().ok()?;
    let dir = exe.parent()?;
    let cands = [
        dir.join("WinUHid.dll"),
        dir.join("assets").join("winuhid").join("WinUHid.dll"),
        dir.parent()?.join("assets").join("winuhid").join("WinUHid.dll"),
    ];
    cands.into_iter().find(|p| p.exists())
}

/// 由一组 VK 构造 8 字节键盘报告（`down=false` 时返回全零 = 全放开）。
///
/// 与 `report_of(held)` 的区别：那个是从"当前按住的键"反推报告，这个直接按给定
/// 组合构造 —— 宏播放要用它，因为它不该改动 `held`（那是遥控器物理键的状态）。
pub fn report_for(vks: &[u16], down: bool) -> [u8; 8] {
    let mut r = [0u8; 8];
    if !down {
        return r;
    }
    let mut slot = 2;
    for &vk in vks {
        if let Some(bit) = crate::keymap::modifier_bit(vk) {
            r[0] |= bit;
        } else if let Some(u) = crate::keymap::usage_of(vk) {
            if slot < 8 {
                r[slot] = u;
                slot += 1;
            }
        }
    }
    r
}

/// 向当前虚拟键盘提交一组 VK（宏播放入口）。
pub fn inject_vks(vks: &[u16], down: bool) {
    let Some(shared) = crate::backend::shared() else { return };
    if let Ok(mut b) = shared.lock() {
        let r = report_for(vks, down);
        b.macro_send(r);
    }
}
