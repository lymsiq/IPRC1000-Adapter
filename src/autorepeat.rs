//! 抑制 Windows 键盘自动重复（2026-11-06）
//!
//! ## 问题
//!
//! 遥控器是"键盘身份"（TLC 0x01/0x06），按住语音键时：
//!
//! ```text
//! 设备发一次 0x10 DOWN（按住期间零报文，实测）
//!   → Windows 的 kbdclass 认为「M 被按住」
//!   → 自动重复：500 ms 后每秒约 30 次
//!   → 焦点窗口里就是 mmmmmmmmmm…
//! ```
//!
//! **重复完全是系统打的**，不是设备发的，也不是我们注入的（我们的注入走
//! `report_of(&held)`，语音键映射成 RAlt 修饰位，不含字母）。
//!
//! 用户实测：按住 3 秒，浏览器地址栏被填满 `m`。
//!
//! ## 解法
//!
//! 收到**首次** DOWN 后立刻补一个 `KEYUP`：
//!
//! ```text
//! 系统状态：按下 → 立刻松开
//!   → 自动重复不启动
//!   → 最多只打 1 个字符
//! ```
//!
//! 设备按住期间零报文，所以不会再触发新的 DOWN ⇒ 不会每个重复周期都补一次。
//!
//! ## 只碰字母
//!
//! 只处理 HID usage `0x04..=0x1D`（A–Z）。方向键 / OK / 返回 / 音量**不产生可打印
//! 字符**，不碰——碰了反而会影响"长按方向连续移动"。
//!
//! ## 为什么不用钩子
//!
//! 钩子能彻底吞掉（0 个字符），但它是全键盘级拦截，写错会"全键盘失灵"。
//! 这里选**无钩子方案**：把 30 个压到 1 个，代价是多一个字符，风险为零。
//! 将来若要求 0 个字符，再在 `ports` 层接钩子实现，本模块不用改。

/// 标记我们自己注入的键（钩子方案接进来后，靠它区分"我们发的"和"用户按的"）
pub const MAGIC: usize = 0x4950_5243; // "IPRC"

/// 首次按下时调用：让系统认为键已松开，从而不启动自动重复。
///
/// 只对会产生可打印字符的 usage 生效（0x04..=0x1D）。
pub fn suppress(usage: u8) {
    // HID 键盘 usage 0x04..0x1D = 字母 A..Z
    if !(0x04..=0x1D).contains(&usage) {
        return;
    }
    // HID usage 0x04 → VK_A(0x41)，0x1D → VK_Z(0x5A)，线性对应
    let vk = 0x41u16 + (usage as u16 - 0x04);
    unsafe {
        use windows::Win32::UI::Input::KeyboardAndMouse::{
            SendInput, INPUT, INPUT_0, INPUT_KEYBOARD, KEYBDINPUT, KEYEVENTF_KEYUP, VIRTUAL_KEY,
        };
        let mut inp: INPUT = std::mem::zeroed();
        inp.r#type = INPUT_KEYBOARD;
        inp.Anonymous = INPUT_0 {
            ki: KEYBDINPUT {
                wVk: VIRTUAL_KEY(vk),
                wScan: 0,
                dwFlags: KEYEVENTF_KEYUP,
                time: 0,
                dwExtraInfo: MAGIC,
            },
        };
        // 失败也无所谓：最坏结果是"仍然连发"，不会造成按不动
        let _ = SendInput(&[inp], std::mem::size_of::<INPUT>() as i32);
    }
}
