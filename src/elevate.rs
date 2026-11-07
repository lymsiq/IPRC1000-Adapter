//! 权限判定（2026-11-06）
//!
//! ## 为什么**不**自动提权
//!
//! 按键主路径是 ETW BthPort ACL（`backend::handle_fragment` 解析 a1 01 报告），
//! 需要管理员；0.4.40 起叠加的 Raw Input 是第二条路径，不需要管理员。
//! 仍不自动提权：主清单保持 asInvoker，由用户自己决定何时右键「以管理员身份运行」。
//!
//! 需要管理员的动作：ETW 会话（`UserTrace::start_and_process()` 要求
//! Administrators 或 Performance Log Users）、语音链路、`--install` 里的驱动/注册表动作。
//! 所以这里只做判定：非管理员时由 `health` 在界面上如实提示，
//! 由用户自己决定要不要右键「以管理员身份运行」，不替用户弹 UAC。

use std::ffi::c_void;
use windows::Win32::Foundation::{CloseHandle, HANDLE};
use windows::Win32::Security::{GetTokenInformation, TokenElevation, TOKEN_ELEVATION, TOKEN_QUERY};
use windows::Win32::System::Threading::{GetCurrentProcess, OpenProcessToken};

/// 当前进程是否 elevated。取不到令牌按"不是管理员"处理。
pub fn is_admin() -> bool {
    unsafe {
        let mut token = HANDLE::default();
        if OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &mut token).is_err() {
            return false;
        }
        let mut elev = TOKEN_ELEVATION::default();
        let mut len = 0u32;
        let rc = GetTokenInformation(
            token,
            TokenElevation,
            Some(&mut elev as *mut _ as *mut c_void),
            std::mem::size_of::<TOKEN_ELEVATION>() as u32,
            &mut len,
        );
        let _ = CloseHandle(token);
        rc.is_ok() && elev.TokenIsElevated != 0
    }
}
