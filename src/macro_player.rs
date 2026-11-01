//! 宏播放器（2026-10-31）
//!
//! 一条宏 = 一串步骤，按顺序执行；`loop` 决定重复几次（0 = 一直循环）。
//!
//! 三条硬约束（全部来自 C# 版的真机教训）：
//!   1. **必须有硬上限**：无限循环最多跑 10 分钟，否则用户按一下就去开会了；
//!   2. **播放中按遥控器任意键 = 停止**（否则只能杀进程）；
//!   3. **文本不走逐字符 SendInput**：那是"被输入法吃掉"的老坑。
//!      这里用 `KEYEVENTF_UNICODE`（硬件级 Unicode 注入，绕过输入法），
//!      比剪贴板方案简单，也不会污染用户剪贴板。
//!
//! 播放跑在独立线程：宏里有 `wait` 步骤，绝不能在 ETW 回调里 sleep。

use std::sync::atomic::{AtomicBool, Ordering};
use std::time::{Duration, Instant};

use windows::Win32::UI::Input::KeyboardAndMouse::{
    SendInput, INPUT, INPUT_0, INPUT_KEYBOARD, KEYBDINPUT, KEYEVENTF_KEYUP,
    KEYEVENTF_UNICODE,
};

/// 播放中标志（backend 的按下分支看到它就请求停止）
pub static PLAYING: AtomicBool = AtomicBool::new(false);
/// 停止请求
static STOP: AtomicBool = AtomicBool::new(false);

/// 无限循环的硬上限
const MAX_RUN: Duration = Duration::from_secs(600);

pub fn is_playing() -> bool {
    PLAYING.load(Ordering::Relaxed)
}

/// 请求停止（遥控器按键 / 界面点停止 都调它）
pub fn request_stop() {
    STOP.store(true, Ordering::Relaxed);
}

/// 供 backend 直接调用（文本槽绑定的键）
pub fn type_text_public(s: &str) {
    type_text(s);
}

/// 用 KEYEVENTF_UNICODE 直接打一串字符（绕过输入法）
fn type_text(s: &str) {
    for ch in s.encode_utf16() {
        let down = INPUT {
            r#type: INPUT_KEYBOARD,
            Anonymous: INPUT_0 {
                ki: KEYBDINPUT {
                    wVk: Default::default(),
                    wScan: ch,
                    dwFlags: KEYEVENTF_UNICODE,
                    time: 0,
                    dwExtraInfo: 0,
                },
            },
        };
        let mut up = down;
        up.Anonymous.ki.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;
        unsafe {
            SendInput(&[down, up], std::mem::size_of::<INPUT>() as i32);
        }
        std::thread::sleep(Duration::from_millis(6)); // 太快会把字符打乱
    }
}

/// 发送一组 VK：`press` = both / down / up
fn send_vks(vks: &[u16], press: &str, hold_ms: u64) {
    if vks.is_empty() {
        return;
    }
    let down = press == "both" || press == "down";
    let up = press == "both" || press == "up";
    if down {
        crate::winuhid::inject_vks(vks, true);
        if press == "both" && hold_ms > 0 {
            std::thread::sleep(Duration::from_millis(hold_ms));
        }
    }
    if up {
        crate::winuhid::inject_vks(vks, false);
    }
}

/// 播放一条宏。阻塞直到结束（调用方必须放在独立线程里）。
pub fn play(steps: &[serde_json::Value], loop_count: u32) -> String {
    STOP.store(false, Ordering::Relaxed);
    PLAYING.store(true, Ordering::Relaxed);
    let started = Instant::now();
    let mut rounds = 0u32;
    let mut stopped_by = "跑完";

    'outer: while loop_count == 0 || rounds < loop_count {
        rounds += 1;
        for st in steps {
            if STOP.load(Ordering::Relaxed) {
                stopped_by = "被按键打断";
                break 'outer;
            }
            if started.elapsed() > MAX_RUN {
                stopped_by = "达到 10 分钟上限";
                break 'outer;
            }
            let ty = st["type"].as_str().unwrap_or("");
            match ty {
                "keys" => {
                    let vks: Vec<u16> = st["vks"]
                        .as_array()
                        .map(|a| a.iter().filter_map(|v| v.as_u64().map(|x| x as u16)).collect())
                        .unwrap_or_default();
                    let press = st["press"].as_str().unwrap_or("both");
                    let hold = st["holdMs"].as_u64().unwrap_or(30);
                    send_vks(&vks, press, hold);
                }
                "wait" => {
                    let ms = st["ms"].as_u64().unwrap_or(300).min(60_000);
                    // 分片睡：不然"停止"要等到这一步睡完才生效
                    let mut left = ms;
                    while left > 0 {
                        if STOP.load(Ordering::Relaxed) {
                            break;
                        }
                        let slice = left.min(50);
                        std::thread::sleep(Duration::from_millis(slice));
                        left -= slice;
                    }
                }
                "text" => {
                    // 文本步骤：内容可能直接带 `text`，也可能引用文本槽（`slot`）
                    let body = if let Some(t) = st["text"].as_str() {
                        t.to_string()
                    } else {
                        let slot = st["slot"].as_u64().unwrap_or(1) as u32;
                        crate::text_slot::get(slot).map(|x| x.text).unwrap_or_default()
                    };
                    if !body.is_empty() {
                        type_text(&body);
                    }
                }
                // 鼠标点击（截图匹配）原生版还没实现 —— 跳过并记一笔，不静默
                "click" => {
                    eprintln!("[宏] 跳过 click 步骤（截图点击尚未接入原生内核）");
                }
                other => {
                    eprintln!("[宏] 跳过未知步骤类型：{other}");
                }
            }
        }
        if loop_count == 0 && rounds % 50 == 0 {
            // 无限循环时每 50 轮检查一次上限（上面的 elapsed 已覆盖，这里只是兜底节奏）
            std::thread::sleep(Duration::from_millis(1));
        }
    }
    finish(rounds, stopped_by)
}

fn finish(rounds: u32, why: &str) -> String {
    PLAYING.store(false, Ordering::Relaxed);
    format!("宏播放结束（{rounds} 轮，{why}）")
}
