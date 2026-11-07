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
        // ① 找第一个蓝牙适配器。注意 BluetoothFindFirstRadio 返回的是
        //    HBLUETOOTH_RADIO_FIND（查找句柄），radio 才是 HANDLE，两者要分别关闭。
        let mut radio = HANDLE::default();
        let radio_find = BluetoothFindFirstRadio(std::ptr::null(), &mut radio).ok()?;
        let hradio = radio;
        let hint = name_hint.to_uppercase();

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
        // ★ BLUETOOTH_DEVICE_INFO::default() 是 zeroed，dwSize=0 会让所有
        //   Bluetooth* API 返回 ERROR_INVALID_PARAMETER，必须显式填。
        let fresh = || BLUETOOTH_DEVICE_INFO {
            dwSize: std::mem::size_of::<BLUETOOTH_DEVICE_INFO>() as u32,
            ..Default::default()
        };
        let name_of = |d: &BLUETOOTH_DEVICE_INFO| {
            String::from_utf16_lossy(
                &d.szName.iter().take_while(|&&c| c != 0).copied().collect::<Vec<_>>(),
            )
        };

        // ② 枚举设备：FindFirstDevice 会填好**第一个**，不能丢掉再从 Next 开始。
        let mut target: Option<BLUETOOTH_DEVICE_INFO> = None;
        let mut dev0 = fresh();
        if let Ok(hfind) = BluetoothFindFirstDevice(&params, &mut dev0) {
            if name_of(&dev0).to_uppercase().contains(&hint) {
                target = Some(dev0);
            }
            if target.is_none() {
                loop {
                    let mut next = fresh();
                    if BluetoothFindNextDevice(hfind, &mut next).is_err() {
                        break;
                    }
                    if name_of(&next).to_uppercase().contains(&hint) {
                        target = Some(next);
                        break;
                    }
                }
            }
            let _ = BluetoothFindDeviceClose(hfind);
        }
        // 查找句柄用完即关；hradio 后面还要传给 SetServiceState/GetDeviceInfo
        let _ = BluetoothFindRadioClose(radio_find);

        let dev = match target {
            Some(d) => d,
            None => {
                let _ = CloseHandle(hradio);
                return None;
            }
        };
        let dev_name = name_of(&dev);

        // ③ 禁用 HID 服务（此时 hradio 仍然有效）
        let disabled = BluetoothSetServiceState(
            hradio,
            &dev,
            &HID_SERVICE,
            BLUETOOTH_SERVICE_DISABLE,
        ) == 0;
        if disabled {
            std::thread::sleep(std::time::Duration::from_millis(600));
        }

        // ④ 重新启用
        let enabled = BluetoothSetServiceState(
            hradio,
            &dev,
            &HID_SERVICE,
            BLUETOOTH_SERVICE_ENABLE,
        ) == 0;

        // ⑤ 等最多 1 秒，看设备是否回到已连接
        let mut connected = false;
        for _ in 0..10 {
            std::thread::sleep(std::time::Duration::from_millis(100));
            let mut info = dev; // Copy，dwSize 已填好
            if BluetoothGetDeviceInfo(hradio, &mut info) == 0 && info.fConnected.as_bool() {
                connected = true;
                break;
            }
        }

        // ★ 所有 API 调用结束后才关闭 radio 句柄（旧版在这里之前就 Close 了）
        let _ = CloseHandle(hradio);

        Some(Outcome {
            device: dev_name,
            disabled,
            enabled,
            connected,
        })
    }
}
