//! 强制重连已配对的蓝牙 HID 设备（2026-11-06）
//!
//! ## 问题
//!
//! Realtek 8821CE 上，遥控器 30 秒 idle 自己断开后，Windows **不主动重连**：
//!
//! ```text
//! 断开 → 手动点「连接」能连上 → 但不会自己连回去
//! ```
//!
//! 开发机的 Intel AX200 会自动重连。这不是配对记录丢失（手动能连），
//! 而是**驱动没有触发重连**。
//!
//! ## 解法：换个握手方式
//!
//! `BluetoothSetServiceState` 可以**禁用再启用某个服务的绑定**。对 HID 服务做一次
//! disable → enable，等于告诉蓝牙栈"这个服务重新协商一遍"，系统就会重新发起连接。
//!
//! **不删除配对记录**，所以不需要重配。代价是这 0.5 秒内按键不响应。
//!
//! ```text
//! BluetoothSetServiceState(radio, dev, {00001124}, DISABLE)
//!    ↓ 等 600 ms
//! BluetoothSetServiceState(radio, dev, {00001124}, ENABLE)
//! ```
//!
//! ## 为什么不用别的办法
//!
//! | 方案 | 问题 |
//! |---|---|
//! | 删设备重配对 | 要用户操作，且 30 秒后又失效 |
//! | 禁用/启用**适配器** | 会断开所有蓝牙设备（鼠标键盘一起掉） |
//! | `BluetoothRemoveDevice` | 同上，且丢配对 |
//! | 发送 HID 保活报文 | `\kbd` 被 RIM 独占，打不开 |
//!
//! 本模块只动**这一台设备的一个服务**，影响面最小。

use windows::core::GUID;
use windows::Win32::Devices::Bluetooth::*;
use windows::Win32::Foundation::{CloseHandle, HANDLE};

/// HID 服务类 GUID（`{00001124-0000-1000-8000-00805F9B34FB}`）
const HID_SERVICE: GUID = GUID::from_u128(0x0000_1124_0000_1000_8000_0080_5F9B_34FB);

pub struct Outcome {
    pub device: String,
    /// disable 是否成功
    pub disabled: bool,
    /// enable 是否成功
    pub enabled: bool,
    /// 重连后设备是否回到"已连接"
    pub connected: bool,
}

impl Outcome {
    pub fn summary(&self) -> String {
        if self.disabled && self.enabled {
            if self.connected {
                format!("{}：HID 服务已重新协商，设备已连接", self.device)
            } else {
                format!(
                    "{}：HID 服务已重新协商，但 1 秒内没等到设备上线 —— 可能设备还在休眠（按一下遥控器）",
                    self.device
                )
            }
        } else if !self.disabled {
            format!("{}：禁用 HID 服务失败（设备可能本来就没连）", self.device)
        } else {
            format!("{}：启用 HID 服务失败", self.device)
        }
    }
}

/// 对"名称含关键字"的那台蓝牙设备做一次 HID 服务重新协商。
///
/// 返回 `None` 表示没找到匹配的设备（未配对 / 适配器不在）。
pub fn renegotiate(name_hint: &str) -> Option<Outcome> {
    unsafe {
        // ① 找第一个蓝牙适配器
        let mut radio = HANDLE::default();
        let found = BluetoothFindFirstRadio(std::ptr::null(), &mut radio);
        let Ok(first) = found else {
            return None;
        };
        let hradio = radio;
        let _iter = first;

        let mut target: Option<BLUETOOTH_DEVICE_INFO> = None;

        // 遍历该适配器下的设备
        let params = BLUETOOTH_DEVICE_SEARCH_PARAMS {
            dwSize: std::mem::size_of::<BLUETOOTH_DEVICE_SEARCH_PARAMS>() as u32,
            fReturnAuthenticated: true.into(),
            fReturnRemembered: true.into(),
            fReturnUnknown: true.into(),
            fReturnConnected: true.into(),
            fIssueInquiry: false.into(),
            cTimeoutMultiplier: 0,
            hRadio: hradio,
        };

        if let Ok(hfind) = BluetoothFindFirstDevice(&params, &mut BLUETOOTH_DEVICE_INFO::default()) {
            loop {
                let mut info = BLUETOOTH_DEVICE_INFO::default();
                if BluetoothFindNextDevice(hfind, &mut info).is_err() {
                    break;
                }
                let name = String::from_utf16_lossy(
                    &info.szName.iter().take_while(|&&c| c != 0).copied().collect::<Vec<_>>(),
                );
                if name.to_uppercase().contains(&name_hint.to_uppercase()) {
                    target = Some(info);
                    break;
                }
            }
            let _ = BluetoothFindDeviceClose(hfind);
        }
        let _ = CloseHandle(hradio);

        let dev = target?;
        let dev_name = String::from_utf16_lossy(
            &dev.szName.iter().take_while(|&&c| c != 0).copied().collect::<Vec<_>>(),
        );

        // ② 禁用 HID 服务
        let rc_disable = BluetoothSetServiceState(
            hradio,
            &dev,
            &HID_SERVICE,
            BLUETOOTH_SERVICE_DISABLE,
        );
        let disabled = rc_disable == 0;
        if disabled {
            // 给蓝牙栈一点时间真正停下这条服务
            std::thread::sleep(std::time::Duration::from_millis(600));
        }

        // ③ 重新启用
        let rc_enable = BluetoothSetServiceState(
            hradio,
            &dev,
            &HID_SERVICE,
            BLUETOOTH_SERVICE_ENABLE,
        );
        let enabled = rc_enable == 0;

        // ④ 等最多 1 秒，看设备是否回到已连接
        let mut connected = false;
        for _ in 0..10 {
            std::thread::sleep(std::time::Duration::from_millis(100));
            let mut info = dev;
            if BluetoothGetDeviceInfo(hradio, &mut info) == 0 && info.fConnected.as_bool() {
                connected = true;
                break;
            }
        }

        Some(Outcome {
            device: dev_name,
            disabled,
            enabled,
            connected,
        })
    }
}
