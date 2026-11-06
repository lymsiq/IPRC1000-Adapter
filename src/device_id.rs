//! 遥控器身份发现（2026-11-06）
//!
//! ## 为什么要有这一层
//!
//! 原来 `device_probe` / `hid_reader` 里把 `VID&00020A5C` / `PID&8502` 写死在 20 处。
//! 换一只同型号遥控器、或者换一个牌子（只要也是键盘类 HID），就得改代码重编译。
//!
//! ## 发现的链路
//!
//! ```text
//! 蓝牙设备名（"IPRC1000"）
//!       ↓ 枚举 BTHPORT\Parameters\Devices\*\Name
//!    设备 MAC（20c047f313d6）
//!       ↓ 同一条配对记录里的 VID / PID 值
//!   VID=0x0A5C  PID=0x8502
//!       ↓ 供 device_probe / hid_reader 拼匹配串
//! BTHENUM / HID 节点
//! ```
//!
//! **为什么必须"名称优先"**：`Enum\HID\...` 的实例名里**只有 VID/PID，没有设备名**。
//! 所以不能直接按名字找 HID 节点，只能先按名字找到"是哪一只"，再拿它的 VID/PID
//! 去匹配 HID —— 这是唯一可行的链式发现。
//!
//! ## 兜底
//!
//! 发现失败（注册表读不到 / 设备没配对）时返回 `None`，调用方沿用已知默认值，
//! **不改变现有行为**。所以这个模块上线后，开发机的表现应当完全不变。

use std::sync::Mutex;

#[derive(Clone)]
pub struct Identity {
    /// 配对记录键名，如 `20c047f313d6`
    pub mac: String,
    /// 蓝牙设备显示名，如 `IPRC1000`
    pub name: String,
    pub vid: u16,
    pub pid: u16,
}

/// 名称关键字不再写死在这里 —— 由配置的 `deviceNameHints` 提供
/// （默认 `["IPRC", "Broadcom Bluetooth Wireless"]`，见 settings::Default）。
/// **这是唯一需要"知道型号"的地方，且是配置不是代码。**

/// 兜底值：注册表读不到、也没学过时的已知型号（IPRC1000 / Broadcom）
pub const FALLBACK_VID: u16 = 0x0A5C;
pub const FALLBACK_PID: u16 = 0x8502;

static CACHE: Mutex<Option<Identity>> = Mutex::new(None);

/// 取当前遥控器身份。
///
/// 顺序（**这就是"不硬编码"的完整链路**）：
///   ① 配置里学过 → 直接用（不扫注册表，启动快）
///   ② 没学过 → 按名称关键字扫一遍
///   ③ 扫到了 → **写进配置**，以后走 ①
///
/// 换遥控器时 `invalidate()` 清一次，重新学习。
pub fn get() -> Option<Identity> {
    let mut g = CACHE.lock().unwrap_or_else(|e| e.into_inner());
    if let Some(id) = g.as_ref() {
        return Some(id.clone());
    }
    // ① 已学习
    if let Some(b) = crate::settings::device_binding() {
        let id = Identity {
            mac: b.mac,
            name: b.name,
            vid: b.vid,
            pid: b.pid,
        };
        *g = Some(id.clone());
        return Some(id);
    }
    // ② 扫描
    let found = discover()?;
    // ③ 落盘（下次不扫）
    crate::settings::set_device_binding(&crate::settings::DeviceBinding {
        name: found.name.clone(),
        mac: found.mac.clone(),
        vid: found.vid,
        pid: found.pid,
    });
    *g = Some(found.clone());
    Some(found)
}

/// 清缓存，下次重新发现（换了遥控器、或刚配对完）。
///
/// 同时清掉配置里的学习结果 —— 否则会一直用旧的那只。
pub fn invalidate() {
    if let Ok(mut g) = CACHE.lock() {
        *g = None;
    }
    crate::settings::clear_device_binding();
}

/// 当前用于匹配的 (VID, PID)。发现失败时用兜底值，行为与改造前一致。
pub fn vid_pid() -> (u16, u16) {
    match get() {
        Some(id) => (id.vid, id.pid),
        None => (FALLBACK_VID, FALLBACK_PID),
    }
}

/// 枚举所有已配对的蓝牙设备，按名称关键字挑出遥控器。
fn discover() -> Option<Identity> {
    use windows::core::PCWSTR;
    use windows::Win32::Foundation::ERROR_SUCCESS;
    use windows::Win32::System::Registry::*;

    let root_path: Vec<u16> = "SYSTEM\\CurrentControlSet\\Services\\BTHPORT\\Parameters\\Devices\0"
        .encode_utf16()
        .collect();

    unsafe {
        let mut root = HKEY::default();
        if RegOpenKeyExW(
            HKEY_LOCAL_MACHINE,
            PCWSTR(root_path.as_ptr()),
            0,
            KEY_READ,
            &mut root,
        ) != ERROR_SUCCESS
        {
            return None;
        }

        for i in 0..64u32 {
            let mut name = [0u16; 128];
            let mut nlen = name.len() as u32;
            if RegEnumKeyExW(
                root,
                i,
                windows::core::PWSTR(name.as_mut_ptr()),
                &mut nlen,
                None,
                windows::core::PWSTR::null(),
                None,
                None,
            ) != ERROR_SUCCESS
            {
                break;
            }
            let mac = String::from_utf16_lossy(&name[..nlen as usize]);
            if mac.is_empty() {
                continue;
            }
            let sub: Vec<u16> = format!("{mac}\0").encode_utf16().collect();
            let mut k = HKEY::default();
            if RegOpenKeyExW(root, PCWSTR(sub.as_ptr()), 0, KEY_READ, &mut k) != ERROR_SUCCESS {
                continue;
            }
            let name_bytes = query_binary(k, "Name").unwrap_or_default();
            let dev_name = String::from_utf8_lossy(&name_bytes)
                .trim_matches('\0')
                .trim()
                .to_string();
            let vid = query_dword(k, "VID").unwrap_or(0) as u16;
            let pid = query_dword(k, "PID").unwrap_or(0) as u16;
            let _ = RegCloseKey(k);

            if dev_name.is_empty() {
                continue;
            }
            let up = dev_name.to_uppercase();
            let hit = crate::settings::device_name_hints()
                .iter()
                .any(|h| !h.is_empty() && up.contains(&h.to_uppercase()));
            if !hit {
                continue;
            }
            if vid == 0 || pid == 0 {
                // 名字对上了但取不到 IDs：用兜底值，至少名称链路是通的
                eprintln!("[设备] 发现 {dev_name}（{mac}）但 VID/PID 缺失，用兜底值");
                let _ = RegCloseKey(root);
                return Some(Identity {
                    mac,
                    name: dev_name,
                    vid: FALLBACK_VID,
                    pid: FALLBACK_PID,
                });
            }
            eprintln!(
                "[设备] 按名称发现遥控器：{dev_name}（{mac}）VID=0x{vid:04X} PID=0x{pid:04X}"
            );
            let _ = RegCloseKey(root);
            return Some(Identity {
                mac,
                name: dev_name,
                vid,
                pid,
            });
        }
        let _ = RegCloseKey(root);
    }
    None
}

unsafe fn query_binary(
    k: windows::Win32::System::Registry::HKEY,
    name: &str,
) -> Option<Vec<u8>> {
    use windows::core::PCWSTR;
    use windows::Win32::Foundation::ERROR_SUCCESS;
    use windows::Win32::System::Registry::*;
    let n: Vec<u16> = format!("{name}\0").encode_utf16().collect();
    let mut ty = REG_NONE;
    let mut len = 0u32;
    if RegQueryValueExW(k, PCWSTR(n.as_ptr()), None, Some(&mut ty), None, Some(&mut len))
        != ERROR_SUCCESS
        || len == 0
    {
        return None;
    }
    let mut buf = vec![0u8; len as usize];
    if RegQueryValueExW(
        k,
        PCWSTR(n.as_ptr()),
        None,
        Some(&mut ty),
        Some(buf.as_mut_ptr()),
        Some(&mut len),
    ) != ERROR_SUCCESS
    {
        return None;
    }
    buf.truncate(len as usize);
    Some(buf)
}

unsafe fn query_dword(
    k: windows::Win32::System::Registry::HKEY,
    name: &str,
) -> Option<u32> {
    let b = query_binary(k, name)?;
    if b.len() >= 4 {
        Some(u32::from_le_bytes([b[0], b[1], b[2], b[3]]))
    } else {
        None
    }
}

/// 当前 VID/PID 的**所有写法**（注册表键名里两种都有：`VID&00020a5c` / `VID_0A5C`）。
///
/// 返回 (VID 变体, PID 变体)，调用方用 `any(|t| s.contains(t))` 判断。
pub fn vid_tokens() -> Vec<String> {
    let (vid, _) = vid_pid();
    vec![format!("VID&{vid:08X}"), format!("VID_{vid:04X}")]
}

pub fn pid_tokens() -> Vec<String> {
    let (_, pid) = vid_pid();
    vec![format!("PID&{pid:04X}"), format!("PID_{pid:04X}")]
}

/// 某串是否指向当前遥控器（大小写不敏感）
pub fn matches(s: &str) -> bool {
    let up = s.to_uppercase();
    vid_tokens().iter().any(|t| up.contains(&t.to_uppercase()))
        && pid_tokens().iter().any(|t| up.contains(&t.to_uppercase()))
}
