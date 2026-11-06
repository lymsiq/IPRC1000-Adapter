//! 蓝牙省电开关（2026-11-06）
//!
//! ## 为什么需要它
//!
//! 换机器后的典型症状：
//!
//! ```text
//! 断开重连 → 正常
//! 空闲一会儿 → 断链
//! 按遥控器 → 不自动重连，界面一直"按键通道未就绪"
//! ```
//!
//! 根因是三个省电开关（适配器 + HID 设备各一组）：空闲时 Windows 把设备挂起，
//! 而蓝牙 HID 被挂起后**不保证自动重连**。
//!
//! 《测试协议》§3 的 B5 早就列了这一项，但代码里从没实现过（`grep MSPower` 零结果）。
//! 每次换机器都会踩，所以做成可检查 + 可修复。
//!
//! ## 检查项
//!
//! | 位置 | 键 |
//! |---|---|
//! | `Enum\USB\VID_*&PID_*\*`，仅 `Class=Bluetooth` | `EnhancedPowerManagementEnabled` / `SelectiveSuspendEnabled` / `AllowIdleIrpInD3` |
//! | `Enum\HID\*`（遥控器实例） | `EnhancedPowerManagementEnabled` |
//!
//! 目标值一律为 **0**（= 不允许省电挂起）。

use std::ffi::OsStr;
use std::os::windows::ffi::OsStrExt;
use windows::core::PCWSTR;
use windows::Win32::Foundation::ERROR_SUCCESS;
use windows::Win32::System::Registry::*;

/// 适配器上要关的三个开关
pub const ADAPTER_SWITCHES: &[&str] = &[
    "EnhancedPowerManagementEnabled",
    "SelectiveSuspendEnabled",
    "AllowIdleIrpInD3",
];
/// HID 设备上要关的一个开关
pub const HID_SWITCH: &str = "EnhancedPowerManagementEnabled";

pub struct Finding {
    /// 形如 `USB\VID_0BDA&PID_C821\00E04C000001`
    pub device: String,
    pub switch: &'static str,
    /// `None` = 该键没有这个值（多数情况等于"未设置"，效果上已经是关的）
    pub value: Option<u32>,
}

impl Finding {
    pub fn ok(&self) -> bool {
        // ★ 只有显式 0 才算已关。`None` = 值不存在 = **系统默认允许省电**，
        //   这正是空闲断链的来源（实测：Intel 适配器三个开关全都不存在）。
        self.value == Some(0)
    }
    pub fn describe(&self) -> String {
        match self.value {
            Some(0) => "0（已关）".into(),
            Some(v) => format!("{v}（**未关**）"),
            None => "未设置（默认允许省电，**需写成 0**）".into(),
        }
    }
}

// ── 注册表小工具 ──

fn wide(s: &str) -> Vec<u16> {
    OsStr::new(s).encode_wide().chain(std::iter::once(0)).collect()
}

unsafe fn open(path: &str, write: bool) -> Option<HKEY> {
    let p = wide(path);
    let mut h = HKEY::default();
    let access = if write { KEY_READ | KEY_WRITE } else { KEY_READ };
    if RegOpenKeyExW(HKEY_LOCAL_MACHINE, PCWSTR(p.as_ptr()), 0, access, &mut h) == ERROR_SUCCESS {
        Some(h)
    } else {
        None
    }
}

unsafe fn subkeys(h: HKEY) -> Vec<String> {
    let mut out = Vec::new();
    for i in 0..256u32 {
        let mut buf = [0u16; 256];
        let mut len = buf.len() as u32;
        if RegEnumKeyExW(
            h,
            i,
            windows::core::PWSTR(buf.as_mut_ptr()),
            &mut len,
            None,
            windows::core::PWSTR::null(),
            None,
            None,
        ) != ERROR_SUCCESS
        {
            break;
        }
        out.push(String::from_utf16_lossy(&buf[..len as usize]));
    }
    out
}

unsafe fn get_string(h: HKEY, name: &str) -> Option<String> {
    let n = wide(name);
    let mut ty = REG_NONE;
    let mut len = 0u32;
    if RegQueryValueExW(h, PCWSTR(n.as_ptr()), None, Some(&mut ty), None, Some(&mut len)) != ERROR_SUCCESS || len == 0 {
        return None;
    }
    let mut buf = vec![0u8; len as usize];
    if RegQueryValueExW(h, PCWSTR(n.as_ptr()), None, Some(&mut ty), Some(buf.as_mut_ptr()), Some(&mut len)) != ERROR_SUCCESS {
        return None;
    }
    buf.truncate(len as usize);
    Some(String::from_utf16_lossy(
        &buf.chunks_exact(2)
            .map(|c| u16::from_le_bytes([c[0], c[1]]))
            .take_while(|&c| c != 0)
            .collect::<Vec<_>>(),
    ))
}

unsafe fn get_dword(h: HKEY, name: &str) -> Option<u32> {
    let n = wide(name);
    let mut v: u32 = 0;
    let mut len = 4u32;
    let mut ty = REG_NONE;
    if RegQueryValueExW(
        h,
        PCWSTR(n.as_ptr()),
        None,
        Some(&mut ty),
        Some(&mut v as *mut u32 as *mut u8),
        Some(&mut len),
    ) == ERROR_SUCCESS
    {
        Some(v)
    } else {
        None
    }
}

unsafe fn set_dword(h: HKEY, name: &str, v: u32) -> bool {
    let n = wide(name);
    RegSetValueExW(h, PCWSTR(n.as_ptr()), 0, REG_DWORD, Some(&v.to_le_bytes())) == ERROR_SUCCESS
}

// ── 枚举 ──

/// 所有 `Class=Bluetooth` 的 USB 适配器实例（形如 `USB\VID_...\...`）
fn bluetooth_adapters() -> Vec<String> {
    let mut out = Vec::new();
    unsafe {
        let Some(root) = open("SYSTEM\\CurrentControlSet\\Enum\\USB", false) else {
            return out;
        };
        for vid in subkeys(root) {
            if !vid.to_uppercase().starts_with("VID_") {
                continue;
            }
            let Some(vk) = open(&format!("SYSTEM\\CurrentControlSet\\Enum\\USB\\{vid}"), false) else {
                continue;
            };
            for inst in subkeys(vk) {
                let path = format!("SYSTEM\\CurrentControlSet\\Enum\\USB\\{vid}\\{inst}");
                let Some(k) = open(&path, false) else { continue };
                // ★ 不能用 `Class` 判断 —— 实测这个值在 `Enum\USB` 下**根本不存在**
                //   （读它返回 ERROR_FILE_NOT_FOUND）。要用 `ClassGUID` 或 `Service`。
                let guid = get_string(k, "ClassGUID").unwrap_or_default();
                let svc = get_string(k, "Service").unwrap_or_default();
                let _ = RegCloseKey(k);
                let is_bt = guid.eq_ignore_ascii_case("{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}")
                    || svc.eq_ignore_ascii_case("BTHUSB")
                    || svc.eq_ignore_ascii_case("bthenum");
                if is_bt {
                    out.push(format!(r"USB\{vid}\{inst}"));
                }
            }
            let _ = RegCloseKey(vk);
        }
        let _ = RegCloseKey(root);
    }
    out
}

/// 所有 HID 设备实例（形如 `HID\VID_...\...`）
fn hid_instances() -> Vec<String> {
    let mut out = Vec::new();
    unsafe {
        let Some(root) = open("SYSTEM\\CurrentControlSet\\Enum\\HID", false) else {
            return out;
        };
        for parent in subkeys(root) {
            // 只关心遥控器自己的实例。否则会把键盘/鼠标/虚拟设备全列进来，
            // --doctor 那一栏会变成几十行噪声。
            if !crate::device_id::matches(&parent) {
                continue;
            }
            let Some(pk) = open(&format!("SYSTEM\\CurrentControlSet\\Enum\\HID\\{parent}"), false) else {
                continue;
            };
            for inst in subkeys(pk) {
                out.push(format!(r"HID\{parent}\{inst}"));
            }
            let _ = RegCloseKey(pk);
        }
        let _ = RegCloseKey(root);
    }
    out
}

/// 只读检查
pub fn audit() -> Vec<Finding> {
    let mut out = Vec::new();
    for dev in bluetooth_adapters() {
        let dp = format!("SYSTEM\\CurrentControlSet\\Enum\\{dev}\\Device Parameters");
        unsafe {
            let Some(k) = open(&dp, false) else { continue };
            for sw in ADAPTER_SWITCHES {
                out.push(Finding {
                    device: dev.clone(),
                    switch: sw,
                    value: get_dword(k, sw),
                });
            }
            let _ = RegCloseKey(k);
        }
    }
    for dev in hid_instances() {
        let dp = format!("SYSTEM\\CurrentControlSet\\Enum\\{dev}\\Device Parameters");
        unsafe {
            let Some(k) = open(&dp, false) else { continue };
            if let Some(v) = get_dword(k, HID_SWITCH) {
                out.push(Finding {
                    device: dev.clone(),
                    switch: HID_SWITCH,
                    value: Some(v),
                });
            }
            let _ = RegCloseKey(k);
        }
    }
    out
}

/// 把不合格的项写成 0。返回 (检查项数, 实际写入数)。
///
/// 需要管理员（写 HKLM\SYSTEM）。
pub fn apply() -> Result<(usize, usize), String> {
    let mut checked = 0usize;
    let mut fixed = 0usize;

    for dev in bluetooth_adapters() {
        let dp = format!("SYSTEM\\CurrentControlSet\\Enum\\{dev}\\Device Parameters");
        unsafe {
            let Some(k) = open(&dp, true) else { continue };
            for sw in ADAPTER_SWITCHES {
                checked += 1;
                let cur = get_dword(k, sw);
                if cur.map(|v| v != 0).unwrap_or(false) {
                    if set_dword(k, sw, 0) {
                        fixed += 1;
                        eprintln!("  [省电] {dev} / {sw}: {cur:?} → 0");
                    }
                }
            }
            let _ = RegCloseKey(k);
        }
    }

    for dev in hid_instances() {
        let dp = format!("SYSTEM\\CurrentControlSet\\Enum\\{dev}\\Device Parameters");
        unsafe {
            let Some(k) = open(&dp, true) else { continue };
            let cur = get_dword(k, HID_SWITCH);
            if cur.map(|v| v != 0).unwrap_or(false) {
                checked += 1;
                if set_dword(k, HID_SWITCH, 0) {
                    fixed += 1;
                    eprintln!("  [省电] {dev} / {HID_SWITCH}: {cur:?} → 0");
                }
            }
            let _ = RegCloseKey(k);
        }
    }

    if checked == 0 {
        Ok((0, 0))
    } else {
        Ok((checked, fixed))
    }
}
