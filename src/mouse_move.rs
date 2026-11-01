//! 鼠标光标移动引擎（2026-10-31）
//!
//! 范围（用户明确划定）：**只有上下左右单轴移动**，不做斜向合成。
//! 这带来一个重要简化：任一时刻**最多只有一个轴在动**，不需要归一化对角线速度，
//! 也不会出现"斜着走比直着走快 41%"那类问题。
//!
//! 结构：
//!   · `backend` 在按下/松开方向键时置 [`DIR`] 的对应位（无锁原子量）；
//!   · 本模块一个 10 ms 的线程读它并按当前速度发 `MOUSEEVENTF_MOVE`。
//!
//! 为什么不用"每次按下移动一次"：按住要连续移动，节拍必须独立于按键事件
//! （遥控器按住期间一条报文都不发），所以必须要自己的定时器。
//! 为什么是 10 ms 而不是更小：`SendInput` 走的是系统输入队列，
//! 10 ms 已经是"看起来连续"的下限，再快只是白烧 CPU。

use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::time::{Duration, Instant};

use windows::Win32::UI::Input::KeyboardAndMouse::{
    SendInput, INPUT, INPUT_0, INPUT_MOUSE, MOUSEEVENTF_MOVE, MOUSEINPUT,
};

/// 方向位：0=上 1=下 2=左 3=右
pub static DIR: [AtomicBool; 4] = [
    AtomicBool::new(false),
    AtomicBool::new(false),
    AtomicBool::new(false),
    AtomicBool::new(false),
];

/// 按住起始时刻（毫秒，用于加速曲线）
static HOLD_SINCE: AtomicU64 = AtomicU64::new(0);

/// 基础速度（像素 / 10 ms）→ 600 px/s；按住 1 秒后升到 1200，2 秒后 1800
const BASE_PX: i32 = 6;

fn now_ms() -> u64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_millis() as u64)
        .unwrap_or(0)
}

/// 某个方向被按下/松开时由 backend 调用
pub fn set_dir(idx: usize, on: bool) {
    if idx >= 4 {
        return;
    }
    let any_before = DIR.iter().any(|d| d.load(Ordering::Relaxed));
    DIR[idx].store(on, Ordering::Relaxed);
    let any_after = DIR.iter().any(|d| d.load(Ordering::Relaxed));
    if !any_before && any_after {
        HOLD_SINCE.store(now_ms(), Ordering::Relaxed);
    }
    if !any_after {
        HOLD_SINCE.store(0, Ordering::Relaxed);
    }
}

pub fn clear_all() {
    for d in DIR.iter() {
        d.store(false, Ordering::Relaxed);
    }
    HOLD_SINCE.store(0, Ordering::Relaxed);
}

pub fn any_pressed() -> bool {
    DIR.iter().any(|d| d.load(Ordering::Relaxed))
}

/// 当前速度（像素 / 10 ms）。按住越久越快，最多 3 倍。
fn speed() -> i32 {
    let since = HOLD_SINCE.load(Ordering::Relaxed);
    if since == 0 {
        return BASE_PX;
    }
    let held_ms = now_ms().saturating_sub(since);
    let tier = (held_ms / 800).min(2) as i32; // 0 / 1 / 2
    BASE_PX * (1 + tier)
}

fn move_rel(dx: i32, dy: i32) {
    if dx == 0 && dy == 0 {
        return;
    }
    let inp = INPUT {
        r#type: INPUT_MOUSE,
        Anonymous: INPUT_0 {
            mi: MOUSEINPUT {
                dx,
                dy,
                mouseData: 0,
                dwFlags: MOUSEEVENTF_MOVE,
                time: 0,
                dwExtraInfo: 0,
            },
        },
    };
    unsafe {
        SendInput(&[inp], std::mem::size_of::<INPUT>() as i32);
    }
}

/// 启动移动线程（进程内只调一次；重复调用无害 —— 用 flag 守卫）
pub fn start() {
    static STARTED: AtomicBool = AtomicBool::new(false);
    if STARTED.swap(true, Ordering::SeqCst) {
        return;
    }
    std::thread::Builder::new()
        .name("mouse-move".into())
        .spawn(|| {
            let mut next = Instant::now();
            loop {
                next += Duration::from_millis(10);
                let now = Instant::now();
                if next > now {
                    std::thread::sleep(next - now);
                } else {
                    next = now; // 落后了就重新对齐，避免"补帧"造成跳跃
                }
                if !any_pressed() {
                    // ★ 空闲时放慢到 200 ms：用户可能几小时不碰鼠标模式。
                    //   一旦有方向键按下，下一轮（≤200ms 后）就回到 10 ms 节拍。
                    //   200ms 的"发现按下"延迟对鼠标模式无感（人按下到移动本来就有几十 ms）。
                    std::thread::sleep(Duration::from_millis(190));
                    continue;
                }
                let s = speed();
                // 上下左右各自独立，**不做斜向合成**（用户明确要求的范围）
                let mut dx = 0;
                let mut dy = 0;
                if DIR[0].load(Ordering::Relaxed) {
                    dy -= s;
                }
                if DIR[1].load(Ordering::Relaxed) {
                    dy += s;
                }
                if DIR[2].load(Ordering::Relaxed) {
                    dx -= s;
                }
                if DIR[3].load(Ordering::Relaxed) {
                    dx += s;
                }
                move_rel(dx, dy);
            }
        })
        .ok();
}
