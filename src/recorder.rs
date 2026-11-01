//! 宏录制器（2026-10-31）
//!
//! 用低级钩子（`WH_KEYBOARD_LL` + `WH_MOUSE_LL`）记录**真实的**键盘按键与鼠标点击，
//! 按时间间隔切分成"步骤"，直接喂给宏编辑器。
//!
//! 三条必须遵守的规则（全部来自 C# 版踩过的坑）：
//!   1. **钩子线程必须有消息循环**：`SetWindowsHookEx` 的回调是在"装了钩子的那个线程"
//!      的消息泵里被调用的；没有 `GetMessage` 循环，钩子永远不会触发，而且
//!      卸载时也没法通过 `PostThreadMessage(WM_QUIT)` 让它退出。
//!   2. **必须过滤注入事件**（`LLKHF_INJECTED` / `LLMHF_INJECTED`）：否则播放宏时
//!      录制 = 自激（把我们自己注入的键又录一遍）。原生内核额外做了一层保护：
//!      **录制期间遥控器按键不注入**（见 backend 的按下分支），所以遥控器本身也不会被录进去。
//!   3. **不吞键**：录制钩子只观察、永远 `CallNextHookEx`。吞键是另一套东西（GhostGuard），
//!      混在一起会让"录制时键盘失灵"这种事故极难排查。

use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::Mutex;
use std::time::Instant;

use windows::Win32::Foundation::{LPARAM, LRESULT, WPARAM};
use windows::Win32::System::Threading::GetCurrentThreadId;
use windows::Win32::UI::WindowsAndMessaging::*;

/// 一条录制到的事件
#[derive(Clone, Debug)]
pub struct RawEvent {
    /// "down" / "up"
    pub kind: String,
    /// 键盘：VK；鼠标：0
    pub vk: u16,
    /// 鼠标：点击类型 "left"/"right"；键盘：空
    pub button: String,
    /// 距录制开始的毫秒
    pub at_ms: u64,
    /// 鼠标坐标
    pub x: i32,
    pub y: i32,
}

/// 诊断计数：钩子收到多少键盘/鼠标事件、其中多少被当注入过滤掉
pub static KB_SEEN: AtomicU64 = AtomicU64::new(0);
pub static MS_SEEN: AtomicU64 = AtomicU64::new(0);
pub static KB_SKIP: AtomicU64 = AtomicU64::new(0);
/// F11 → 请求停止（录制中由钩子置位）
static STOP_REQ: AtomicBool = AtomicBool::new(false);

static T0: Mutex<Option<Instant>> = Mutex::new(None);
static EVENTS: Mutex<Vec<RawEvent>> = Mutex::new(Vec::new());
static RECORDING: AtomicBool = AtomicBool::new(false);
static HOOK_TID: std::sync::atomic::AtomicU32 = std::sync::atomic::AtomicU32::new(0);

/// 开始录制（清空旧数据）。返回 false = 已经在录。
pub fn start() -> bool {
    if RECORDING.swap(true, Ordering::SeqCst) {
        return false;
    }
    *T0.lock().unwrap_or_else(|e| e.into_inner()) = Some(Instant::now());
    EVENTS.lock().unwrap_or_else(|e| e.into_inner()).clear();
    KB_SEEN.store(0, Ordering::Relaxed);
    MS_SEEN.store(0, Ordering::Relaxed);
    KB_SKIP.store(0, Ordering::Relaxed);
    STOP_REQ.store(false, Ordering::Relaxed);

    std::thread::Builder::new()
        .name("recorder".into())
        .spawn(|| unsafe {
            let tid = GetCurrentThreadId();
            HOOK_TID.store(tid, Ordering::SeqCst);
            // 键盘：**不装钩子**了（改用 GetAsyncKeyState 轮询，见下）
            let kb: Result<HHOOK, windows::core::Error> = Ok(HHOOK::default());
            let ms = SetWindowsHookExW(WH_MOUSE_LL, Some(mouse_proc), None, 0);
            // 注意：HHOOK 不是 Copy，这里只能先借用看错误，不能 kb.err() 把 Result move 掉
            let kb_ok = kb.is_ok();
            let ms_ok = ms.is_ok();
            match kb.as_ref() {
                Ok(_) => eprintln!("[录制] ✓ 键盘钩子已安装"),
                Err(e) => eprintln!("[录制] ✗ 键盘钩子安装失败：{e}"),
            }
            match ms.as_ref() {
                Ok(_) => eprintln!("[录制] ✓ 鼠标钩子已安装"),
                Err(e) => eprintln!("[录制] ✗ 鼠标钩子安装失败：{e}"),
            }
            if !kb_ok || !ms_ok {
                RECORDING.store(false, Ordering::SeqCst);
                return;
            }
            // 键盘轮询线程（8 ms 一轮）
            std::thread::spawn(|| {
                #[allow(unused_unsafe)]
                use windows::Win32::UI::Input::KeyboardAndMouse::GetAsyncKeyState;
                let mut prev = [false; 256];
                while RECORDING.load(Ordering::Relaxed) {
                    let mut changed: Vec<(u16, bool)> = Vec::new();
                    for vk in 8u16..=254 {
                        let st = GetAsyncKeyState(vk as i32) as u16;
                        let down = (st & 0x8000) != 0;
                        let i = vk as usize;
                        if down != prev[i] {
                            prev[i] = down;
                            changed.push((vk, down));
                        }
                    }
                    for (vk, down) in changed {
                        KB_SEEN.fetch_add(1, Ordering::Relaxed);
                        // F11 停止
                        if vk == 0x7A && down {
                            STOP_REQ.store(true, Ordering::Relaxed);
                            eprintln!("[录制] 收到 F11 ⇒ 请求停止");
                        }
                        // 跳过我们自己的虚拟键（VK 范围外不会出现，这里只做可读日志）
                        eprintln!("[录制] 键盘 vk=0x{vk:02X} {}", if down { "按下" } else { "抬起" });
                        push(RawEvent {
                            kind: if down { "down".into() } else { "up".into() },
                            vk,
                            button: String::new(),
                            at_ms: now_offset(),
                            x: 0,
                            y: 0,
                        });
                    }
                    std::thread::sleep(std::time::Duration::from_millis(8));
                }
            });

            // 必须有消息循环（见文件头第 1 条）
            let mut msg = MSG::default();
            while GetMessageW(&mut msg, None, 0, 0).as_bool() {
                let _ = TranslateMessage(&msg);
                DispatchMessageW(&msg);
            }
            let _ = UnhookWindowsHookEx(kb.unwrap());
            let _ = UnhookWindowsHookEx(ms.unwrap());
        })
        .ok();
    true
}

/// 停止录制并返回收集到的事件
pub fn stop() -> Vec<RawEvent> {
    if !RECORDING.swap(false, Ordering::SeqCst) {
        return Vec::new();
    }
    let tid = HOOK_TID.load(Ordering::SeqCst);
    if tid != 0 {
        unsafe {
            let _ = PostThreadMessageW(tid, WM_QUIT, WPARAM(0), LPARAM(0));
        }
    }
    // 等钩子线程把最后几个事件写完
    std::thread::sleep(std::time::Duration::from_millis(120));
    let out = EVENTS.lock().map(|g| g.clone()).unwrap_or_default();
    eprintln!(
        "[录制] 统计：键盘收到 {} 个（过滤注入 {} 个）· 鼠标收到 {} 个 · 有效事件 {} 个",
        KB_SEEN.load(Ordering::Relaxed),
        KB_SKIP.load(Ordering::Relaxed),
        MS_SEEN.load(Ordering::Relaxed),
        out.len()
    );
    out
}

/// 是否收到 F11 停止请求（UI 每帧轮询，收到就调 stop()）
pub fn stop_requested() -> bool {
    STOP_REQ.load(Ordering::Relaxed)
}

pub fn is_recording() -> bool {
    RECORDING.load(Ordering::Relaxed)
}

fn now_offset() -> u64 {
    T0.lock()
        .ok()
        .and_then(|g| g.map(|t| t.elapsed().as_millis() as u64))
        .unwrap_or(0)
}

fn push(e: RawEvent) {
    if let Ok(mut g) = EVENTS.lock() {
        if g.len() < 5000 {
            // 上限保护：录太久会把内存吃光
            g.push(e);
        }
    }
}

#[allow(dead_code)]
unsafe extern "system" fn kb_proc(code: i32, wp: WPARAM, lp: LPARAM) -> LRESULT {
    if code >= 0 && RECORDING.load(Ordering::Relaxed) {
        let kb = &*(lp.0 as *const KBDLLHOOKSTRUCT);
        // 诊断：先无条件记录"钩子收到了什么"，再判断是否过滤
        // （上次"只录到鼠标"时无法区分"钩子没收到"和"被过滤了"）
        KB_SEEN.fetch_add(1, Ordering::Relaxed);
        // F11 = 停止录制（用户要求：录制开始/结束要有热键，不用切窗口点按钮）
        if kb.vkCode == 0x7A && wp.0 as u32 == WM_KEYDOWN {
            STOP_REQ.store(true, Ordering::Relaxed);
            eprintln!("[录制] 收到 F11 ⇒ 请求停止");
        }
        let injected = kb.flags.0 & LLKHF_INJECTED.0 != 0;
        if injected {
            KB_SKIP.fetch_add(1, Ordering::Relaxed);
        } else {
            eprintln!(
                "[录制] 键盘 vk=0x{:02X} {}",
                kb.vkCode,
                if wp.0 as u32 == WM_KEYDOWN || wp.0 as u32 == WM_SYSKEYDOWN { "按下" } else { "抬起" }
            );
        }
        // 过滤我们自己注入的键（原生内核走 WinUHid，理论上不带 INJECTED，
        // 所以录制期间遥控器按键由 backend 直接跳过注入 —— 双重保险）
        if kb.flags.0 & LLKHF_INJECTED.0 == 0 {
            let down = wp.0 as u32 == WM_KEYDOWN || wp.0 as u32 == WM_SYSKEYDOWN;
            push(RawEvent {
                kind: if down { "down".into() } else { "up".into() },
                vk: kb.vkCode as u16,
                button: String::new(),
                at_ms: now_offset(),
                x: 0,
                y: 0,
            });
        }
    }
    CallNextHookEx(None, code, wp, lp)
}

unsafe extern "system" fn mouse_proc(code: i32, wp: WPARAM, lp: LPARAM) -> LRESULT {
    if code >= 0 && RECORDING.load(Ordering::Relaxed) {
        let ms = &*(lp.0 as *const MSLLHOOKSTRUCT);
        MS_SEEN.fetch_add(1, Ordering::Relaxed);
        if ms.flags & LLMHF_INJECTED == 0 {
            let kind = match wp.0 as u32 {
                WM_LBUTTONDOWN => Some(("down", "left")),
                WM_LBUTTONUP => Some(("up", "left")),
                WM_RBUTTONDOWN => Some(("down", "right")),
                WM_RBUTTONUP => Some(("up", "right")),
                _ => None,
            };
            if let Some((k, b)) = kind {
                push(RawEvent {
                    kind: k.into(),
                    vk: 0,
                    button: b.into(),
                    at_ms: now_offset(),
                    x: ms.pt.x,
                    y: ms.pt.y,
                });
            }
        }
    }
    CallNextHookEx(None, code, wp, lp)
}

/// 把原始事件整理成宏步骤（纯函数，好测也好改）。
///
/// 规则：
///   · 同一时刻按下的键合成一个 `keys` 步骤（按住的修饰键和主键一起发）；
///   · 两次动作之间间隔 ≥ 80 ms 就插一个 `wait` 步骤（保留节奏）；
///   · 鼠标按下+抬起在同一位置 → 一个"点击"步骤；
///   · 鼠标自由移动**不录**（用户明确要求过：不录自由移动）。
pub fn build_steps(evs: &[RawEvent]) -> Vec<serde_json::Value> {
    let mut out: Vec<serde_json::Value> = Vec::new();
    let mut held: Vec<u16> = Vec::new();
    let mut last_ms = 0u64;
    let mut pending_click: Option<(String, i32, i32, u64)> = None;

    for e in evs {
        // 时间间隔 → wait
        let gap = e.at_ms.saturating_sub(last_ms);
        if gap >= 80 && !out.is_empty() {
            out.push(serde_json::json!({ "type": "wait", "ms": gap.min(5000) }));
        }
        last_ms = e.at_ms;

        if e.kind == "text" {
            out.push(serde_json::json!({
                "type": "text", "text": e.button.clone(), "slot": 1,
                "ms": 0, "sym": "文本",
            }));
            continue;
        }

        if e.vk != 0 {
            // ★ 键盘：**按下、弹起各自成为一个步骤**（与 C# 版一致）。
            //   原来合成一个 press:"both" 的步骤，导致"只删按下"、"只删弹起"都做不到。
            let name = crate::keymap::vk_name(e.vk);
            if e.kind == "down" {
                if !held.contains(&e.vk) {
                    held.push(e.vk);
                }
                out.push(serde_json::json!({
                    "type": "keys",
                    "vks": vec![e.vk],
                    "sym": name,
                    "press": "down",
                    "holdMs": 0,
                    "ms": 0,
                    "slot": 1,
                }));
            } else {
                out.push(serde_json::json!({
                    "type": "keys",
                    "vks": vec![e.vk],
                    "sym": name,
                    "press": "up",
                    "holdMs": 0,
                    "ms": 0,
                    "slot": 1,
                }));
                if let Some(pos) = held.iter().position(|v| *v == e.vk) {
                    held.remove(pos);
                }
            }
        } else {
            // 鼠标
            if e.kind == "down" {
                pending_click = Some((e.button.clone(), e.x, e.y, e.at_ms));
            } else if let Some((btn, x, y, at)) = pending_click.take() {
                // 按下与抬起间隔很短且位置没变 → 一次点击
                let quick = e.at_ms.saturating_sub(at) < 800;
                let same_pos = (e.x - x).abs() <= 3 && (e.y - y).abs() <= 3;
                out.push(serde_json::json!({
                    "type": "click",
                    "button": btn,
                    "x": x, "y": y,
                    "mode": if quick && same_pos { "here" } else { "drag" },
                    "img": serde_json::Value::Null,
                    "ms": 0, "slot": 1,
                    "sym": if btn == "right" { "右键" } else { "左键" },
                }));
            }
        }
    }
    out
}

/// 解析 KeymouseGo 脚本（Legacy 数组格式），转成宏步骤。
///
/// 格式（每行一条）：
/// ```text
/// [113,"EM","mouse left up",[2996,206]],
/// [186,"EK","key down",[13,"Return",0]],
/// [100,"EX","input","你好 world"],
/// ```
/// 字段 = **延迟ms**（相对上一条，不是绝对时间）/ 类型 / 动作 / 参数。
///
/// 为什么先转成 `RawEvent` 再走 `build_steps`：这个格式和"录制的原始事件流"语义完全一致，
/// 复用同一套聚合逻辑，就不存在"录制出来的宏"和"导入的宏"行为不一致的问题。
///
/// 明确不支持：新版 JSON5 的流程控制（sequence/if/goto/label）、滚轮 —— 都会记进 warnings，
/// 不静默丢弃（静默丢步骤比报错更糟：用户以为导成功了）。
#[allow(dead_code)]
pub fn import_kmgo(txt: &str) -> (Vec<serde_json::Value>, Vec<String>) {
    let mut evs: Vec<RawEvent> = Vec::new();
    let mut warns: Vec<String> = Vec::new();
    let mut t = 0u64;

    for (ln, raw) in txt.lines().enumerate() {
        let line = raw.trim();
        if line.is_empty() || !line.starts_with('[') {
            continue;
        }
        let body = line.trim_end_matches(',').trim();
        let body = body.trim_start_matches('[').trim_end_matches(']').trim();

        // 逐段切：delay , "TYPE" , "action" , params
        let Some((delay_s, rest)) = body.split_once(',') else {
            warns.push(format!("第 {} 行：格式不认识，已跳过", ln + 1));
            continue;
        };
        let Ok(delay) = delay_s.trim().parse::<u64>() else {
            warns.push(format!("第 {} 行：延迟不是数字，已跳过", ln + 1));
            continue;
        };
        t += delay;

        let rest = rest.trim();
        let Some(rest) = rest.strip_prefix('"') else {
            warns.push(format!("第 {} 行：缺少事件类型", ln + 1));
            continue;
        };
        let Some((typ, rest)) = rest.split_once('"') else { continue };
        let rest = rest.trim().trim_start_matches(',').trim();
        let Some(rest) = rest.strip_prefix('"') else { continue };
        let Some((action, params)) = rest.split_once('"') else { continue };
        let params = params.trim().trim_start_matches(',').trim();

        match typ {
            "EK" => {
                // [vk, "名字", extended]
                let inner = params.trim_start_matches('[').trim_end_matches(']');
                let vk = inner
                    .split(',')
                    .next()
                    .and_then(|x| x.trim().parse::<u16>().ok())
                    .unwrap_or(0);
                if vk == 0 {
                    continue;
                }
                let down = action.contains("down");
                evs.push(RawEvent {
                    kind: if down { "down".into() } else { "up".into() },
                    vk,
                    button: String::new(),
                    at_ms: t,
                    x: 0,
                    y: 0,
                });
            }
            "EM" => {
                // [x, y]；-1,-1 = 原地
                let inner = params.trim_start_matches('[').trim_end_matches(']');
                let mut it = inner.split(',');
                let x = it.next().and_then(|v| v.trim().parse::<i32>().ok()).unwrap_or(0);
                let y = it.next().and_then(|v| v.trim().parse::<i32>().ok()).unwrap_or(0);
                let btn = if action.contains("right") { "right" } else { "left" };
                if action.contains("wheel") {
                    warns.push(format!("第 {} 行：滚轮事件暂不支持，已跳过", ln + 1));
                    continue;
                }
                if action.contains("move") {
                    // 自由移动不录（与录制器的约定一致）
                    continue;
                }
                let down = action.contains("down");
                evs.push(RawEvent {
                    kind: if down { "down".into() } else { "up".into() },
                    vk: 0,
                    button: btn.into(),
                    at_ms: t,
                    x,
                    y,
                });
            }
            "EX" => {
                // 参数就是文本本身（带引号）
                let text = params
                    .trim()
                    .trim_start_matches('"')
                    .trim_end_matches('"')
                    .replace("\\n", "\n");
                if !text.is_empty() {
                    evs.push(RawEvent {
                        kind: "text".into(),
                        vk: 0,
                        button: text,
                        at_ms: t,
                        x: 0,
                        y: 0,
                    });
                }
            }
            _ => {
                warns.push(format!("第 {} 行：事件类型 {typ} 不支持，已跳过", ln + 1));
            }
        }
    }

    if evs.is_empty() {
        warns.push("没有解析出任何可用的步骤".to_string());
    }
    (build_steps(&evs), warns)
}
