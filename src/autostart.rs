//! 开机自启（2026-10-31）
//!
//! 写 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` —— 用户级，不需要管理员，
//! 也不污染 HKLM。值名固定为 `IPRC1000Native`，值是 exe 的完整路径（带引号，防空格）。
//!
//! 判据说明：**读注册表当前值**，而不是"记着我们点过勾"。
//! 用户可能用别的方式删掉它（任务管理器 → 启动项），界面要反映真实状态。

use windows::core::PCWSTR;
use windows::Win32::Foundation::ERROR_SUCCESS;
use windows::Win32::System::Registry::*;

const RUN_KEY: &str = r"Software\Microsoft\Windows\CurrentVersion\Run";
const VALUE_NAME: &str = "IPRC1000Native";

fn wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

/// 当前是否已设置开机自启（读注册表）
pub fn is_enabled() -> bool {
    unsafe {
        let mut h = HKEY::default();
        let key = wide(RUN_KEY);
        if RegOpenKeyExW(HKEY_CURRENT_USER, PCWSTR(key.as_ptr()), 0, KEY_READ, &mut h).is_err() {
            return false;
        }
        let name = wide(VALUE_NAME);
        let mut buf = [0u16; 1024];
        let mut size = (buf.len() * 2) as u32;
        let rc = RegQueryValueExW(
            h,
            PCWSTR(name.as_ptr()),
            None,
            None,
            Some(buf.as_mut_ptr() as *mut u8),
            Some(&mut size),
        );
        let _ = RegCloseKey(h);
        rc == ERROR_SUCCESS && size > 2
    }
}

/// 设置/取消开机自启。返回是否成功。
pub fn set_enabled(on: bool) -> bool {
    unsafe {
        let mut h = HKEY::default();
        let key = wide(RUN_KEY);
        // 写需要 KEY_SET_VALUE
        if RegOpenKeyExW(
            HKEY_CURRENT_USER,
            PCWSTR(key.as_ptr()),
            0,
            KEY_SET_VALUE | KEY_QUERY_VALUE,
            &mut h,
        )
        .is_err()
        {
            return false;
        }
        let name = wide(VALUE_NAME);
        if on {
            let Ok(exe) = std::env::current_exe() else {
                let _ = RegCloseKey(h);
                return false;
            };
            // 路径可能有空格，必须带引号
            let val = format!("\"{}\"", exe.display());
            let data = wide(&val);
            let bytes = (data.len() - 1) * 2; // 不含结尾 NUL
            let rc = RegSetValueExW(
                h,
                PCWSTR(name.as_ptr()),
                0,
                REG_SZ,
                Some(std::slice::from_raw_parts(data.as_ptr() as *const u8, bytes)),
            );
            let _ = RegCloseKey(h);
            rc == ERROR_SUCCESS
        } else {
            let rc = RegDeleteValueW(h, PCWSTR(name.as_ptr()));
            let _ = RegCloseKey(h);
            // 值本来就不存在也算成功
            rc == ERROR_SUCCESS || rc == windows::Win32::Foundation::ERROR_FILE_NOT_FOUND
        }
    }
}
