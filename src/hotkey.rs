//! 全局热键（2026-10-31）
//!
//! 用户要求：录制宏用 **F10 开始 / F11 结束**，不用切窗口点按钮。
//!
//! 分工：
//!   · **F11 停止**：录制期间本来就挂着低级键盘钩子，直接在钩子里检测（见 recorder.rs）；
//!   · **F10 开始**：录制前没有钩子，所以用 `RegisterHotKey` + 一个独立的消息循环线程。
//!
//! 为什么不用"常驻键盘钩子检测 F10"：钩子会拦到所有按键（多一份被 AV 误报、
//! 出 bug 就全键盘失灵的风险），而 `RegisterHotKey` 是系统级热键，只回报这一个键。

use std::sync::atomic::{AtomicBool, Ordering};

/// F10 被按下（UI 每帧轮询，处理完清掉）
pub static F10_HIT: AtomicBool = AtomicBool::new(false);
/// 请求显示界面（托盘左键 / 菜单「打开界面」共用的信号）
pub static SHOW_REQ: AtomicBool = AtomicBool::new(false);
/// **用户主动把界面收进托盘**（区别于"意外结束"）。
///
/// ★ 2026-11-01（内存优化）：收进托盘不再是把窗口缩成 1×1，而是**结束 UI 事件循环**
///   —— 这样 GL 上下文、字体 atlas、eframe 框架内存才会真正交还系统。
///   主循环靠这个标志区分两种 `run_native` 返回：
///     true  ⇒ 用户主动隐藏，**等** SHOW_REQ 再重建（否则会立刻重建，等于没隐藏）
///     false ⇒ 意外结束（GL 丢失/主题切换），2 秒后自动重建（保留原有健壮性）
pub static HIDE_REQUESTED: AtomicBool = AtomicBool::new(false);

/// 是否**需要** F10 热键（只有宏页面开着时才需要）。
/// 热键线程收到 `WM_APP_HOTKEY` 后按这个标志重新注册/注销。
static WANT: AtomicBool = AtomicBool::new(false);
/// 热键线程的 TID（用来 PostThreadMessage 唤醒它）
static TID: std::sync::atomic::AtomicU32 = std::sync::atomic::AtomicU32::new(0);
/// 自定义消息：请求"按 WANT 重新配置热键"
const WM_APP_HOTKEY: u32 = 0x8000 + 1;

/// 启动 F10 全局热键监听（进程内只调一次）
pub fn start() {
    use windows::Win32::UI::Input::KeyboardAndMouse::{RegisterHotKey, MOD_NOREPEAT, VK_F10};
    use windows::Win32::UI::WindowsAndMessaging::{GetMessageW, MSG, WM_HOTKEY};

    static STARTED: AtomicBool = AtomicBool::new(false);
    if STARTED.swap(true, Ordering::SeqCst) {
        return;
    }
    std::thread::Builder::new()
        .name("hotkey".into())
        .spawn(|| unsafe {
            use windows::Win32::UI::Input::KeyboardAndMouse::UnregisterHotKey;
            use windows::Win32::System::Threading::GetCurrentThreadId;

            TID.store(GetCurrentThreadId(), Ordering::SeqCst);
            eprintln!(
                "[热键] F10/F11 = 录制宏（**只在宏页面生效**，切走后自动释放按键占位）\
                 · 唤回界面点托盘图标左键"
            );
            let mut registered = false;
            let mut msg = MSG::default();
            while GetMessageW(&mut msg, None, 0, 0).as_bool() {
                if msg.message == WM_HOTKEY {
                    if msg.wParam.0 == 1 {
                        F10_HIT.store(true, Ordering::Relaxed);
                    }
                } else if msg.message == WM_APP_HOTKEY {
                    // 页面切换：按 WANT 决定注册还是注销
                    let want = WANT.load(Ordering::Relaxed);
                    if want && !registered {
                        match RegisterHotKey(None, 1, MOD_NOREPEAT, VK_F10.0 as u32) {
                            Ok(()) => {
                                registered = true;
                                eprintln!("[热键] 进入宏页面 ⇒ F10 已注册");
                            }
                            Err(_) => eprintln!(
                                "[热键] F10 注册失败（可能被其他程序占用），请用界面按钮开始录制"
                            ),
                        }
                    } else if !want && registered {
                        let _ = UnregisterHotKey(None, 1);
                        registered = false;
                        eprintln!("[热键] 离开宏页面 ⇒ F10 已释放（不再占用系统按键）");
                    }
                }
            }
        })
        .ok();
}

/// 通知热键线程：现在是否需要 F10（宏页面开着 = 需要）。
///
/// 用 PostThreadMessage 唤醒阻塞在 GetMessageW 的线程 —— RegisterHotKey/UnregisterHotKey
/// 必须**在注册它的那个线程**上调用，不能从 UI 线程直接改。
pub fn set_enabled(on: bool) {
    use windows::Win32::UI::WindowsAndMessaging::PostThreadMessageW;
    if WANT.swap(on, Ordering::SeqCst) == on {
        return; // 状态没变，不用折腾
    }
    let tid = TID.load(Ordering::SeqCst);
    if tid != 0 {
        unsafe {
            let _ = PostThreadMessageW(tid, WM_APP_HOTKEY, windows::Win32::Foundation::WPARAM(0), windows::Win32::Foundation::LPARAM(0));
        }
    }
}

/// 取一次"F10 是否被按过"（取走即清）
pub fn take_f10() -> bool {
    F10_HIT.swap(false, Ordering::Relaxed)
}
