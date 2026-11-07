//! 启动期致命状态的**唯一出口**（2026-11-06）
//!
//! ## 为什么要有这个模块
//!
//! 「开发机正常 / 客户机全无反应」最难查的不是故障本身，而是**故障看不见**：
//! ETW 会话起不来时业务线程只 `eprintln!` 后 `return`，窗口、托盘、状态条照常在，
//! 用户和客服都以为"程序跑着就是好的"。
//!
//! 这里放一个进程级、跨线程可读的致命错误槽：后台线程负责写，UI 负责读并显示。
//! 判据沿用项目的老原则：**静默失败必须留痕，假仪表比没仪表更糟**。

use std::sync::Mutex;

static FATAL: Mutex<Option<String>> = Mutex::new(None);

/// 后台线程报告一个"功能整体不可用"的错误。只保留第一条（根因通常是最早那条）。
pub fn set_fatal(msg: impl Into<String>) {
    let msg = msg.into();
    eprintln!("[FATAL] {msg}");
    if let Ok(mut g) = FATAL.lock() {
        if g.is_none() {
            *g = Some(msg);
        }
    }
}

/// UI 每帧读一次；返回 None 表示没有致命错误。
pub fn fatal() -> Option<String> {
    FATAL.lock().ok().and_then(|g| g.clone())
}

/// 故障恢复后清掉（保留给显式恢复流程；当前 ETW 用 clear_matching 精确清）。
#[allow(dead_code)]
pub fn clear() {
    if let Ok(mut g) = FATAL.lock() {
        *g = None;
    }
}

/// 只清掉某一类故障（按消息前缀），避免误清掉其它仍然存在的致命错误。
/// 例：ETW 会话启动成功后只清「启动 ETW 会话失败」，不动 WinUHid 的注入故障。
pub fn clear_matching(prefix: &str) {
    if let Ok(mut g) = FATAL.lock() {
        if g.as_deref().map(|s| s.starts_with(prefix)).unwrap_or(false) {
            *g = None;
        }
    }
}
