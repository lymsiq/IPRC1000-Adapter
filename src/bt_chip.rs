//! 蓝牙芯片能力判定（2026-11-06）
//!
//! ## 结论来源
//!
//! Realtek 的 Wi-Fi+BT 组合卡（RTL8821CE / 8822CE / 8723DE / 8822BE）蓝牙部分
//! **不提供链接密钥的非易失存储**，系统日志里表现为 `BTHUSB 事件 18`
//! （"Windows 无法在本地适配器上存储蓝牙认证码"）。
//!
//! 后果不是"配对失败"，而是**休眠后无法重建 HID 通道**：
//!
//! ```text
//! 遥控器醒着、密钥还在内存 → 第一次重连通
//! 30 秒 idle 休眠 → 密钥没了 → Windows 仍显示"已连接"
//!   → 按键通道不建 → 必须删设备重配对 → 30 秒后又失效
//! ```
//!
//! 重装 `RtkBtfilter`、换新版瑞昱驱动都**不能**给芯片加上存储能力。
//!
//! ## 为什么不做"自动修复"
//!
//! 曾经试过改绑、`--restore-keyboard`、重配对 —— 在这块芯片上全是徒劳，
//! 只会让用户反复折腾。正确做法是**一眼看出该换蓝牙**，不做无效尝试。
//!
//! ## 判定条件
//!
//! `Enum\USB\VID_0BDA&PID_*` 且 `Service=BTHUSB`（= 它是这台机器的蓝牙适配器）。
//! 不看 `Class`（实测 `Enum\USB` 下没有这个值）。

use windows::core::PCWSTR;
use windows::Win32::Foundation::ERROR_SUCCESS;
use windows::Win32::System::Registry::*;

pub struct Chip {
    /// `USB\VID_0BDA&PID_C821\...`
    pub instance: String,
    pub vid_pid: String,
    pub desc: String,
    /// 厂商（`Mfg` 值；`Driver` 里是类 GUID，不适合展示）
    pub vendor: String,
}

impl Chip {
    /// 是否是已知"存不住密钥"的 Realtek 组合卡蓝牙
    pub fn is_known_bad(&self) -> bool {
        self.vid_pid.to_uppercase().contains("VID_0BDA")
    }
}

fn wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

unsafe fn open(path: &str) -> Option<HKEY> {
    let p = wide(path);
    let mut h = HKEY::default();
    if RegOpenKeyExW(HKEY_LOCAL_MACHINE, PCWSTR(p.as_ptr()), 0, KEY_READ, &mut h) == ERROR_SUCCESS {
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

unsafe fn get_sz(h: HKEY, name: &str) -> Option<String> {
    let n = wide(name);
    let mut ty = REG_NONE;
    let mut len = 0u32;
    if RegQueryValueExW(h, PCWSTR(n.as_ptr()), None, Some(&mut ty), None, Some(&mut len)) != ERROR_SUCCESS
        || len == 0
    {
        return None;
    }
    let mut buf = vec![0u8; len as usize];
    if RegQueryValueExW(h, PCWSTR(n.as_ptr()), None, Some(&mut ty), Some(buf.as_mut_ptr()), Some(&mut len))
        != ERROR_SUCCESS
    {
        return None;
    }
    let units: Vec<u16> = buf
        .chunks_exact(2)
        .map(|c| u16::from_le_bytes([c[0], c[1]]))
        .take_while(|&c| c != 0)
        .collect();
    Some(String::from_utf16_lossy(&units))
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

/// 找出这台机器上的蓝牙适配器（`Service=BTHUSB` 的那个）。
pub fn adapter() -> Option<Chip> {
    unsafe {
        let root = open("SYSTEM\\CurrentControlSet\\Enum\\USB")?;
        for vid in subkeys(root) {
            if !vid.to_uppercase().starts_with("VID_") {
                continue;
            }
            let Some(vk) = open(&format!("SYSTEM\\CurrentControlSet\\Enum\\USB\\{vid}")) else {
                continue;
            };
            for inst in subkeys(vk) {
                let path = format!("SYSTEM\\CurrentControlSet\\Enum\\USB\\{vid}\\{inst}");
                let Some(k) = open(&path) else { continue };
                let svc = get_sz(k, "Service").unwrap_or_default();
                // 跳过幽灵设备（拔掉的 dongle 会留在注册表里）
                let flags = get_dword(k, "ConfigFlags").unwrap_or(0);
                let desc = get_sz(k, "DeviceDesc").unwrap_or_default();
                let vendor = get_sz(k, "Mfg")
                    .map(|v| v.trim_start_matches('@').to_string())
                    .unwrap_or_default();
                let _ = RegCloseKey(k);
                if !svc.eq_ignore_ascii_case("BTHUSB") || flags & 0x20 != 0 {
                    continue;
                }
                let _ = RegCloseKey(vk);
                let _ = RegCloseKey(root);
                return Some(Chip {
                    instance: format!(r"USB\{vid}\{inst}"),
                    vid_pid: vid.clone(),
                    desc,
                    vendor,
                });
            }
            let _ = RegCloseKey(vk);
        }
        let _ = RegCloseKey(root);
    }
    None
}
