//! 权限判定（2026-11-06）
//!
//! ## 为什么**不**自动提权
//!
//! 交接文档 §3 已在客户机证实：按键通道的根因是 `\kbd` 接口的 SYSTEM-only ACL，
//! 改成 Raw Input 后**不需要管理员**（原型实测 20/20 按键全捕获）。
//! 此时若给主程序加 `requireAdministrator` 或启动时 `runas` 重启，只会让客户机
//! 每次开机多一个 UAC 弹窗，对按键毫无帮助。
//!
//! 仍然需要管理员的只剩**语音链路**（`UserTrace::start_and_process()` 要求
//! Administrators 或 Performance Log Users）与 `--install` 里的驱动/注册表动作。
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
