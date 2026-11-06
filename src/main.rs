//! IPRC1000 原生内核（2026-10-31，egui 版）
//!
//! 线程模型（三线程，职责清晰、互不阻塞）：
//!   · **UI 线程**（主）：egui 即时渲染，每帧直接读 `settings` / `keymap` / `backend` 的内存状态；
//!   · **业务线程**：ETW 解码 + 按键注入 + 音频输出 + 看门狗（`backend::run_backend`，常驻）；
//!   · **托盘线程**：`tray-icon` 的消息循环 + 菜单事件。
//!
//! 为什么比 Tauri 稳：UI 与业务之间**没有 IPC**，就是共享内存 + 一次原子读。
//! WebView2 那套（JSON 序列化、渲染进程生命周期、后台节流）统统不存在，
//! 所以不会再出现"界面卡死 / 数据不更新 / 显示假数据"这一类故障。

#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

// Win32 版（feature = win32_only）不编译 egui/托盘那一套，因此有一批函数"没有调用者"。
// 逐个加 cfg 门控收益不大、还容易把完整版编坏，所以只在这一类构建下统一放开。
#![cfg_attr(feature = "win32_only", allow(dead_code))]

mod autostart;
mod backend;
mod cable_out;
mod commands;
mod device_probe;
mod dev_install;
mod elevate;
mod etw_registry;
mod health;
mod hid_reader;
mod hotkey;
mod installer;
mod keymap;
mod macro_player;
mod macro_store;
mod paths;
mod mouse_act;
mod mouse_move;
mod recorder;
mod settings;
mod shortcut_store;
mod ui_win32;
mod step;
mod text_slot;
mod winuhid;
#[cfg(feature = "gui")]
mod ui_edit;
mod ui_edit_win32;
#[cfg(feature = "gui")]
mod ui_egui;
#[cfg(feature = "gui")]
mod ui_insert;
mod uninstall;
#[cfg(feature = "gui")]
mod ui_pages;

use std::sync::atomic::Ordering;
use std::time::Duration;

// ── 给 UI 用的薄封装（UI 不直接碰 backend 的内部结构）──

/// 遥控器是否在线（30 秒内有过上报）
pub fn is_online() -> bool {
    backend::shared()
        .and_then(|sh| sh.lock().ok().map(|b| b.snapshot().online))
        .unwrap_or(false)
}

/// **按键级**在线：30 秒内真的收到过按键（ETW 或 HID 直读）。
///
/// 与 `is_online()` 的区别是这轮审查的核心结论：`is_online()` 只看"30 秒内有没有
/// 任意 ETW 事件"，L2CAP 信令保活 2 次/秒就能把它刷成"在线" ——
/// 于是"显示在线、按键全无"这类故障被掩盖。界面要显示的是这个。
pub fn is_key_online() -> bool {
    backend::shared()
        .and_then(|sh| sh.lock().ok().map(|b| b.snapshot().key_online))
        .unwrap_or(false)
}

/// 虚拟键盘是否就绪
pub fn is_inject_ready() -> bool {
    backend::shared()
        .and_then(|sh| sh.lock().ok().map(|b| b.snapshot().inject_ok))
        .unwrap_or(false)
}

/// 把"某个按键 → 某个值"落地（UI 的唯一写入口）
pub fn apply_binding(id: &str, value: &str) -> String {
    let Some(usage) = keymap::usage_of_id(id) else {
        return format!("未知按键：{id}");
    };
    let v = value.trim();
    if v.starts_with('-') {
        let Ok(act) = v.parse::<i64>() else {
            return format!("无法解析动作：{v}");
        };
        // 三类负数值，语义完全不同（这也是不能简单"负数=动作"的原因）：
        //   -1..-6        鼠标动作，交给 mouse_act
        //   -1001..-1010  文本槽 1..10
        //   -1101..-1999  宏
        if (-6..=-1).contains(&act) {
            settings::set_binding_in_group(id, v);
            keymap::set_action(usage, Some(act as i32));
            keymap::set_override(usage, Vec::new());
            return format!("{id} → {}（鼠标动作）", mouse_act::action_name(act as i32));
        }
        if (-1010..=-1001).contains(&act) {
            let slot = (-1000 - act) as u32;
            settings::set_binding_in_group(id, v);
            keymap::set_action(usage, None);
            keymap::set_override(usage, Vec::new());
            let n = text_slot::get(slot).map(|s| s.text.chars().count()).unwrap_or(0);
            return if n == 0 {
                format!("{id} → 文本槽 {slot}（槽是空的，先录一段文字）")
            } else {
                format!("{id} → 文本槽 {slot}（{n} 字）")
            };
        }
        // 「打开程序」：-2001..-2999 → 快捷方式编号 1..999
        if (-2999..=-2001).contains(&act) {
            let num = (-2000 - act) as u32;
            settings::set_binding_in_group(id, v);
            // ★ 必须写 Some(act)：后端的"打开程序"分派读的就是 action_of()。
            //   写成 None 会让绑定在界面上显示正常、实际按下去毫无反应（踩过）。
            keymap::set_action(usage, Some(act as i32));
            keymap::set_override(usage, Vec::new());
            return format!("{id} → {}", shortcut_store::label(num));
        }
        if (-1999..=-1101).contains(&act) {
            // 基准 -1100：宏 1 存成 -1101（与界面生成值一致）
            let num = (-1100 - act) as u32;
            settings::set_binding_in_group(id, v);
            keymap::set_action(usage, None);
            keymap::set_override(usage, Vec::new());
            return match macro_store::get(num) {
                Some(m) => format!("{id} → 宏 {}（{} 步）", num, m.steps.len()),
                None => format!("{id} → 宏 {num}（不存在，请先建宏）"),
            };
        }
        return String::from("无法识别的动作值");
    }
    keymap::set_action(usage, None);
    let vks: Vec<u16> = if v.is_empty() {
        Vec::new()
    } else {
        v.split(',').filter_map(|x| x.trim().parse::<u16>().ok()).collect()
    };
    if !v.is_empty() && vks.is_empty() {
        return format!("无法解析：{v}");
    }
    settings::set_binding_in_group(id, v);
    keymap::set_override(usage, vks.clone());
    if vks.is_empty() {
        format!("{id} 已清除（按下不注入任何键）")
    } else {
        let txt = vks.iter().map(|v| keymap::vk_name(*v)).collect::<Vec<_>>().join("+");
        format!("{id} → {txt}")
    }
}

/// 把 **stdout + stderr** 都重定向到 `<应用目录>\logs\native.log`。
///
/// 为什么必须这么做：之前日志靠调用方 `> file 2>&1`，用户自己双击 exe 时
/// stderr 直接丢弃 ⇒ 出问题只能靠猜（录制键盘不工作这事就因此查了很久）。
/// 改成进程内重定向后，**任何启动方式都有日志**。
/// 日志滚动：超过 `max_bytes` 时把 `name` → `name.1`，`name.1` → `name.2` …… 保留 `keep` 份。
fn rotate_log(path: &std::path::Path, keep: usize, max_bytes: u64) {
    let Ok(m) = std::fs::metadata(path) else { return };
    if m.len() <= max_bytes {
        return;
    }
    if keep == 0 {
        let _ = std::fs::remove_file(path);
        return;
    }
    // 从最旧的开始往后挪，避免覆盖；最老的一份直接丢掉
    let _ = std::fs::remove_file(std::path::PathBuf::from(format!("{}.{}", path.display(), keep)));
    for i in (1..keep).rev() {
        let from = std::path::PathBuf::from(format!("{}.{}", path.display(), i));
        let to = std::path::PathBuf::from(format!("{}.{}", path.display(), i + 1));
        let _ = std::fs::rename(&from, &to);
    }
    let _ = std::fs::rename(path, std::path::PathBuf::from(format!("{}.1", path.display())));
}

/// 日志总开关的运行时镜像（`diag::log` 每行都要判断，不能每行读 json）。
pub static LOGS_ON: std::sync::atomic::AtomicBool = std::sync::atomic::AtomicBool::new(true);

/// 当前是否写日志（供各模块查询）
// ── 「唤回界面」的命名事件 ──────────────────────────────────────────────
//
// 为什么需要：2026-11-01 真机——界面收进托盘后，用户双击 exe 想把它叫回来，
// 结果第二个实例只打印一行"已经有一个在运行"就静默退出，**屏幕上什么都没发生**
// 用户看到的就是"程序卡死了"。
// （另外：强杀进程后 Windows 会留一个僵尸托盘图标，点它同样没反应——
//   这两条路都断了，用户就真的没有任何入口。）
//
// 所以加一个**命名事件**：第一个实例创建它并轮询；后来者打开它、SetEvent，然后退出。
// 效果就是"再点一次图标 ⇒ 界面回来"，符合用户直觉，也不依赖托盘图标是否有效。

static SHOW_EV: std::sync::atomic::AtomicIsize = std::sync::atomic::AtomicIsize::new(0);

#[link(name = "kernel32")]
extern "system" {
    fn CreateEventW(attr: *const std::ffi::c_void, manual: i32, init: i32, name: *const u16) -> isize;
    fn OpenEventW(access: u32, inherit: i32, name: *const u16) -> isize;
    fn SetEvent(h: isize) -> i32;
    fn WaitForSingleObject(h: isize, ms: u32) -> u32;
    fn CloseHandle(h: isize) -> i32;
}
const EVENT_MODIFY_STATE: u32 = 0x0002;

fn show_event_name() -> Vec<u16> {
    use std::os::windows::ffi::OsStrExt;
    std::ffi::OsStr::new("Global\\Iprc1000_ShowWindow")
        .encode_wide()
        .chain(std::iter::once(0))
        .collect()
}

/// 主实例：创建（或打开已有）事件句柄，供后来者 SetEvent。
fn create_show_event() {
    let name = show_event_name();
    unsafe {
        // 自动重置：Wait 一次就清掉，不需要手动 ResetEvent
        let h = CreateEventW(std::ptr::null(), 0, 0, name.as_ptr());
        if h != 0 {
            SHOW_EV.store(h, std::sync::atomic::Ordering::SeqCst);
        }
    }
}

/// 后来者：请求已有实例把界面显示出来。
fn signal_show_event() -> bool {
    let name = show_event_name();
    unsafe {
        let h = OpenEventW(EVENT_MODIFY_STATE, 0, name.as_ptr());
        if h == 0 {
            return false;
        }
        let ok = SetEvent(h) != 0;
        let _ = CloseHandle(h);
        ok
    }
}

/// 主实例轮询：有没有人请求唤回（非阻塞）。
fn show_event_fired() -> bool {
    let h = SHOW_EV.load(std::sync::atomic::Ordering::SeqCst);
    if h == 0 {
        return false;
    }
    // WAIT_OBJECT_0 = 0
    unsafe { WaitForSingleObject(h, 0) == 0 }
}

pub fn logs_on() -> bool {
    LOGS_ON.load(std::sync::atomic::Ordering::Relaxed)
}

/// 快速判断"是否要写日志"—— **必须能在加载完整配置之前调用**
/// （`redirect_stderr_to_file` 是 main 的第一件事，那时 settings 还没 load）。
/// 只读 settings.json 里的一个字段，失败时按"开"处理。
fn logs_enabled_quick() -> bool {
    // 读的就是 settings 那份文件（native.json）。原来写的是 settings.json ——
    // 那个文件根本不存在，于是这里永远返回 true，"关日志"一直没生效。
    let p = crate::paths::data_dir().join("native.json");
    let Ok(txt) = std::fs::read_to_string(&p) else { return true };
    let Ok(v) = serde_json::from_str::<serde_json::Value>(&txt) else { return true };
    v.get("logEnabled").and_then(|x| x.as_bool()).unwrap_or(true)
}

/// 删除所有运行日志（关闭日志时调用一次）。
pub fn purge_logs() {
    let dir = crate::paths::logs_dir();
    for name in [
        "native.log", "bridge.log", "bridge-stats.json",
        "key-events.log", "router.log", "stuck-guard.log", "mouse.log",
    ] {
        for i in 0..4 {
            let f = if i == 0 {
                dir.join(name)
            } else {
                dir.join(format!("{name}.{i}"))
            };
            if f.exists() {
                let _ = std::fs::remove_file(&f);
            }
        }
    }
    eprintln!("[日志] 已删除所有运行日志文件");
}

/// 把 stdout/stderr 重定向到 NUL（关闭日志后用：所有 println/eprintln 直接被丢弃）
pub fn silence_stdio() {
    use std::os::windows::io::AsRawHandle;
    for which in [
        windows::Win32::System::Console::STD_OUTPUT_HANDLE,
        windows::Win32::System::Console::STD_ERROR_HANDLE,
    ] {
        // 每个句柄单独打开一次 NUL（File 不能同时给两个句柄用）
        if let Ok(f) = std::fs::OpenOptions::new().write(true).open("NUL") {
            unsafe {
                let h = f.as_raw_handle() as isize;
                let _ = windows::Win32::System::Console::SetStdHandle(
                    which,
                    windows::Win32::Foundation::HANDLE(h),
                );
                std::mem::forget(f);   // 句柄要活到进程结束
            }
        }
    }
}

fn redirect_stderr_to_file() {
    use std::os::windows::io::AsRawHandle;
    let dir = crate::paths::logs_dir();
    // ★ 用户关了日志：不写文件、直接丢弃（所有 println/eprintln 都进 NUL）
    if !logs_enabled_quick() {
        LOGS_ON.store(false, std::sync::atomic::Ordering::Relaxed);
        purge_logs();
        silence_stdio();
        return;
    }
    LOGS_ON.store(true, std::sync::atomic::Ordering::Relaxed);
    let path = dir.join("native.log");
    // 轮转：超过 2 MB 就滚动，**保留 3 份**（.1 最新 .3 最旧）。
    // 只留 1 份时，第二次轮转会覆盖掉唯一的备份 —— 想回溯两次启动前的日志就没了。
    rotate_log(&path, 3, 2 * 1024 * 1024);
    // bridge.log 同样处理（它每 30 秒写一行统计，长跑也会涨）
    rotate_log(&dir.join("bridge.log"), 3, 2 * 1024 * 1024);
    // ★ 两个都要接：业务日志用的是 eprintln!（stdout），错误用 eprintln!（stderr）。
    //   只接 stderr 的话，[KEY]/[INJ]/[宏] 这些关键行在双击启动时依然丢失。
    for which in [
        windows::Win32::System::Console::STD_OUTPUT_HANDLE,
        windows::Win32::System::Console::STD_ERROR_HANDLE,
    ] {
        if let Ok(f) = std::fs::OpenOptions::new().create(true).append(true).open(&path) {
            unsafe {
                let h = f.as_raw_handle() as isize;
                let _ = windows::Win32::System::Console::SetStdHandle(
                    which,
                    windows::Win32::Foundation::HANDLE(h),
                );
                std::mem::forget(f); // 句柄要活到进程结束
            }
        }
    }
}

fn main() {
    // ★ 命令行子命令（--install / --repair / --diag …）**不要**吞掉输出：
    //   它们是给安装器/脚本/人看的，stdout+stderr 必须留在控制台（否则父进程拿到空输出）。
    //   只有正常启动（GUI 常驻）才把输出重定向进 native.log。
    let is_cli_tool = std::env::args().any(|a| {
        a == "--install"
            || a == "--repair"
            || a == "--diag"
            || a == "--audio"
            || a == "--launch-shortcut"
            || a == "--check-key"
            || a == "--bind-program"
            || a == "--hid-probe"
            || a == "--restore-keyboard"
    });
    if !is_cli_tool {
        redirect_stderr_to_file();
    }

    // ★ 2026-11-06：权限 / ETW 注册表**只读体检**，如实留痕，不强制提权。
    //   按键已改走 Raw Input（交接文档 §3 实测不需要管理员，且不碰 \kbd 的
    //   SYSTEM-only ACL）；仍需管理员的只剩语音的 ETW 会话与 --install 的系统动作。
    //   不自动 UAC：客户机每次开机弹一次提权框，对按键毫无帮助，反而添堵。
    let admin = elevate::is_admin();
    if !admin {
        eprintln!("[!] 当前非管理员：语音链路（ETW 会话）可能起不来；按键（Raw Input）不受影响。要用语音请右键「以管理员身份运行」。");
    }
    if !is_cli_tool {
        let reg = etw_registry::status();
        if !reg.configured {
            eprintln!("[!] BthPort ETW 注册表未就绪：{}", reg.detail);
            health::set_fatal(format!(
                "语音链路注册表未就绪（{}）。请运行「一键安装.cmd」写入后重启电脑；按键不受影响。",
                reg.detail
            ));
        }
    }

    // ★ 全局 panic 钩子：任何线程 panic 都留痕（默认只打印到 stderr 且格式难读）。
    //   没有它的话，后台线程 panic 后功能静默失效，日志里一个字都没有 —— 排查全靠猜。
    std::panic::set_hook(Box::new(|info| {
        use std::io::Write as _;
        let loc = info
            .location()
            .map(|l| format!("{}:{}", l.file(), l.line()))
            .unwrap_or_default();
        let msg = if let Some(s) = info.payload().downcast_ref::<&str>() {
            (*s).to_string()
        } else if let Some(s) = info.payload().downcast_ref::<String>() {
            s.clone()
        } else {
            String::from("<非字符串 payload>")
        };
        eprintln!("[PANIC] {msg}  @ {loc}");
        let _ = std::io::stderr().flush();
    }));

    // 资源诊断：`IPRC1000-Native.exe --diag`（无界面，打印模块/线程/内存后退出）
    // 诊断/自测：直接触发某个「打开程序」快捷方式（客户现场验证绑定是否有效）
    if let Some(i) = std::env::args().position(|a| a == "--launch-shortcut") {
        let num: u32 = std::env::args()
            .nth(i + 1)
            .and_then(|v| v.trim().parse().ok())
            .unwrap_or(0);
        match shortcut_store::launch(num) {
            Ok(()) => {
                println!("已启动快捷方式 #{num}");
                std::process::exit(0);
            }
            Err(e) => {
                println!("失败：{e}");
                std::process::exit(1);
            }
        }
    }

    // 恢复：`--restore-keyboard` 把遥控器从 raw 绑回键盘（改造后按键全无反应时的解药）
    if std::env::args().any(|a| a == "--restore-keyboard") {
        match device_probe::restore_keyboard() {
            Ok(m) => println!("{m}"),
            Err(e) => println!("失败：{e}"),
        }
        std::process::exit(0);
    }

    // 止字符：`--convert-hid` 把遥控器从 kbdhid 改绑到 mshidkmdf（HID-compliant device）。
    // 目标：系统不再往焦点窗口打字母，同时 HID 接口 / Raw Input / ETW 都保持不变。
    // 需要管理员；回退用 `--restore-keyboard`。
    if std::env::args().any(|a| a == "--convert-hid") {
        if !elevate::is_admin() {
            println!("失败：需要管理员权限（要改设备驱动绑定）。请以管理员身份重新运行本命令。");
            std::process::exit(4);
        }
        match device_probe::convert_to_hid_compliant() {
            Ok(m) => println!("{m}"),
            Err(e) => println!("失败：{e}"),
        }
        std::process::exit(0);
    }

    // 诊断：`--hid-probe` 列出 Raw Input 键盘设备并标出 0A5C+8502（判断"系统有没有把遥控器当键盘"）；
    //       不打开任何 \\kbd 接口（那个接口 ACL 是 SYSTEM-only，打开必然 err=5）
    if std::env::args().any(|a| a == "--hid-probe") {
        hid_reader::probe();
        std::process::exit(0);
    }

    // 诊断：`--check-key 08` 打印某个 usage 的绑定串 / action / vks，
    // 用来判断"界面显示有绑定、按下去却没反应"到底断在哪一层。
    if let Some(i) = std::env::args().position(|a| a == "--check-key") {
        settings::load();
        keymap::load_group(&settings::bindings_of(settings::active_group()));
        let raw = std::env::args().nth(i + 1).unwrap_or_default();
        let u = u8::from_str_radix(raw.trim().trim_start_matches("0x"), 16).unwrap_or(0xFF);
        let id = keymap::id_of(u);
        let act = keymap::action_of(u);
        println!("usage   = 0x{u:02X}");
        println!("id      = {id}   name = {}", keymap::name_of(u));
        println!("绑定串  = {:?}", settings::binding_in_group(&id));
        println!("action  = {act:?}   （后端「打开程序」读的就是这个）");
        println!("vks     = {:?}", keymap::vks_of(u));
        match act.and_then(|a| shortcut_store::num_of_action(a as i64)) {
            Some(n) => println!("=> 按下会执行：{}", shortcut_store::label(n)),
            None => println!("=> 不是「打开程序」动作"),
        }
        std::process::exit(0);
    }

    // 现场装配：`--bind-program favorite "D://...//WeChat.exe"`（等价于点「程序」按钮）
    if let Some(i) = std::env::args().position(|a| a == "--bind-program") {
        settings::load();
        keymap::load_group(&settings::bindings_of(settings::active_group()));
        let id = std::env::args().nth(i + 1).unwrap_or_default();
        let path = std::env::args().nth(i + 2).unwrap_or_default();
        let name = std::path::Path::new(&path)
            .file_stem()
            .map(|x| x.to_string_lossy().to_string())
            .unwrap_or_else(|| String::from("程序"));
        match shortcut_store::save(0, &name, &path, "") {
            Ok(num) => {
                let val = shortcut_store::action_of_num(num).to_string();
                println!("{}", apply_binding(&id, &val));
            }
            Err(e) => println!("失败：{e}"),
        }
        std::process::exit(0);
    }

    if std::env::args().any(|a| a == "--diag") {
        eprintln!("{}", diag_report());
        use std::io::Write as _;
        let _ = std::io::stderr().flush();
        std::process::exit(0);
    }

    // 一键修复（无界面）：`IPRC1000-Native.exe --repair`（供自测/批量部署）
    if std::env::args().any(|a| a == "--repair") {
        let r = commands::repair_all();
        eprintln!("{r}");
        use std::io::Write as _;
        let _ = std::io::stdout().flush();
        std::process::exit(0);
    }

    // ★ 一键安装（供 `一键安装.cmd` / 首次部署用）：一次提权做完四项并逐项复检。
    //   与界面的「⚙ 一键修复」走**同一个** `repair_all()` —— 同一个动作只能一套实现。
    //   退出码：0 = 全部就绪；2 = 已装但需重启；3 = 有必须手动处理的项。
    if std::env::args().any(|a| a == "--install") {
        // ★ 判真实权限，不判"点了按钮"：装驱动/写 HKLM 需要管理员，
        //   以前这里照样跑完并打"安装完成"，是客户机假成功的源头。
        if !admin {
            eprintln!("✗ 需要管理员权限：请右键「一键安装.cmd」→ 以管理员身份运行（或安装器右键同法）。");
            eprintln!("  按键走 Raw Input 不需要管理员；这一步是给声卡/驱动/注册表用的。");
            std::process::exit(4);
        }
        eprintln!("IPRC1000 一键安装 —— 声卡 / 键盘驱动 / 麦克风 / ETW 注册表 / 遥控器按键通道检查");
        eprintln!("（每一步装完都会立刻复检真实状态，不会只看退出码）");
        // ★ 结果类型化：退出码由结构化报告决定，**不再**靠 `msg.contains("重启")` 猜。
        //   那是同一类假绿灯（文案里出现"重启"两字就判需重启，与真实状态无关）。
        let rep = commands::repair_report();
        eprintln!("{}", rep.summary());
        if !rep.manual().is_empty() {
            eprintln!("\n★ 有组件需要你手动处理一步，请按上面给出的动作做完再运行一次");
        }
        use std::io::Write as _;
        let _ = std::io::stderr().flush();
        if !rep.manual().is_empty() {
            std::process::exit(3);
        }
        if rep.any_reboot() {
            eprintln!("\n★ Windows 说需要重启才生效，请重启一次（不用再装）");
            let _ = std::io::stderr().flush();
            std::process::exit(2);
        }
        std::process::exit(0);
    }

    // 创建桌面快捷方式：`IPRC1000-Native.exe --make-shortcut`
    if std::env::args().any(|a| a == "--make-shortcut") {
        match make_desktop_shortcut() {
            Ok(()) => std::process::exit(0),
            Err(e) => {
                eprintln!("[快捷方式] 失败：{e}");
                std::process::exit(1);
            }
        }
    }

    // 静默卸载：`IPRC1000-Native.exe --uninstall --yes`
    //
    // ★ 必须带 --yes：这个动作会**连带删除 VB-CABLE 声卡和 WinUHid 驱动**
    //   （按你的要求保持"全删"），误触发一次就得重装驱动+重启电脑。
    //   加一道显式确认，避免手滑/脚本误调。
    if std::env::args().any(|a| a == "--uninstall") {
        if !std::env::args().any(|a| a == "--yes") {
            eprintln!("[卸载] 已中止：这是危险操作，会删除");
            eprintln!("        · 程序目录、桌面快捷方式、开机自启项");
            eprintln!("        · VB-CABLE 虚拟声卡");
            eprintln!("        · WinUHid 虚拟键盘驱动（删掉后遥控器会变回普通键盘）");
            eprintln!();
            eprintln!("        确认请执行：IPRC1000-Native.exe --uninstall --yes");
            use std::io::Write as _;
            let _ = std::io::stderr().flush();
            std::process::exit(2);
        }
    }
    if std::env::args().any(|a| a == "--uninstall") {
        if let Some(dir) = uninstall::current_dir() {
            match uninstall::uninstall(&dir) {
                Ok(()) => {
                    eprintln!("[卸载] 已启动卸载脚本，2 秒后清理 {}", dir.display());
                    std::process::exit(0);
                }
                Err(e) => {
                    eprintln!("[卸载] 失败：{e}");
                    std::process::exit(1);
                }
            }
        }
    }
    // 音频设备诊断：`--audio`（无界面，打印默认录音/播放设备）
    if std::env::args().any(|a| a == "--audio") {
        let p = commands::probes();
        eprintln!("默认录音设备: {}", p.mic_name);
        eprintln!("  → 是 CABLE Output 吗? {}", if p.mic_is_cable { "是 ✓" } else { "**不是 ✗**（豆包会听到别的麦克风）" });
        eprintln!("默认播放设备: {}", p.play_name);
        eprintln!("  → 是 CABLE Input 吗? {}", if p.play_is_cable { "**是 ✗**（你的声音会进虚拟线，自己听不到）" } else { "不是 ✓" });
        eprintln!("CABLE Input 存在: {}", p.cable_in);
        eprintln!("CABLE Output 存在: {}", p.cable_out);
        use std::io::Write as _;
        let _ = std::io::stderr().flush();
        std::process::exit(0);
    }

    // ★ 单实例保护（必须在**任何** ETW 操作之前）。
    //   两个实例会互相停掉对方的 ETW 会话 ⇒ 老进程"活着但完全没反应"。
    // ★★ 2026-10-31（真机：点「重启程序」之后两个实例全没了）：
    //   托盘「重启程序」是先 spawn 新实例、再 sleep 300 ms 才 exit(0) ⇒ 新实例启动时
    //   老实例还占着互斥体 ⇒ 新实例直接打「已经有一个在运行」退出 ⇒ 结果**一个都不剩**。
    //   所以"由重启拉起"的实例要等老实例真正退出，而不是立刻放弃。
    let restarted = std::env::args().any(|a| a == "--restarted");
    let got_instance = if restarted {
        backend::acquire_single_instance_wait(6000)
    } else {
        backend::acquire_single_instance()
    };
    if !got_instance {
        // ★★ 关键：**先把已有实例的界面叫回来**，否则用户双击图标毫无反应 = "卡死"。
        if signal_show_event() {
            eprintln!("[i] 已有实例在运行，已请求它把界面显示出来（本进程退出）。");
        } else {
            eprintln!("[i] 已有实例在运行，但唤回信号没发出去（旧版本不支持）。");
        }
        eprintln!("[!] 已经有一个 IPRC1000 在运行，本次启动退出。");
        eprintln!("    原因：两个实例会互相停掉对方的 ETW 会话，表现为「进程在、但按键语音都没反应」。");
        eprintln!("    请用**托盘图标左键单击**打开已有实例（或托盘右键 →「打开界面」）。");
        use std::io::Write as _;
        let _ = std::io::stderr().flush();
        std::process::exit(0);
    }

    create_show_event();
    eprintln!("\n===== 启动 {} =====", chrono_like_now());
    if let Some((rss, h, t)) = self_stats() {
        eprintln!("[资源] 启动基线 RSS {rss} MB · 句柄 {h} · 线程 {t}");
    }

    // ★★ 启动第一件事：清理**上一次残留的 ETW 会话**。
    //   ETW 会话是系统级资源，进程被强杀/崩溃时它**不会自动停止**，
    //   之后每个新实例都能"启动成功"却收不到任何事件 —— 表现就是
    //   "按键无反应、语音没声音"，而且日志里一切正常，极难排查（今天踩了两次）。
    clean_stale_trace();

    // ① 先读配置（UI 一起来就要显示）
    //   ★ 读之前先看"新位置有没有配置"：没有就把老 %APPDATA% 的搬过来 ——
    //     否则升级到 0.4.34 的用户会发现键位/宏/话术全没了（其实是换了目录）。
    paths::migrate_legacy();
    settings::load();
    macro_store::load();
    macro_store::migrate_split_both();   // 旧数据：合并的按下+弹起拆成独立步骤
    text_slot::load();
    settings::ensure_mouse_defaults();
    backend::PRESET.store(settings::preset(), Ordering::Relaxed);
    keymap::load_group(&settings::bindings_of(settings::active_group()));

    // ② 光标移动引擎（鼠标模式）
    mouse_move::start();
    hotkey::start();   // F10 = 开始录制（F11 由录制钩子处理）

    // ③ 业务线程（ETW + 注入 + 音频，常驻不返回）
    std::thread::Builder::new()
        .name("iprc-backend".into())
        .spawn(backend::run_backend)
        .ok();

    // ③-2 按键输入消费者 = Raw Input（2026-11-06 客户机根因）
    //   旧版 CreateFileW 打开键盘顶层集合 \kbd 接口：该接口 ACL 是 SYSTEM-only，
    //   非 SYSTEM 进程恒定 err=5，管理员也打不开 ⇒ 客户机按键全无。
    //   现在独立线程建隐藏窗口，RegisterRawInputDevices(RIDEV_INPUTSINK) 收 WM_INPUT，
    //   按设备名 0A5C+8502 过滤，用 HELD[256] 还原 HID 报告快照；不打开任何设备接口。
    //   ⚠ 仍必须保持遥控器「键盘」身份：改绑成 input.inf/raw 后不注册 HID 设备接口、
    //   Windows 不建 HID 中断通道 ⇒ 按键与音频同时全断。见 device_probe.rs。
    //   语音音频仍走 ETW（那条通道本来就有数据）。
    hid_reader::start();

    // ④ 托盘线程（Win32 版由 ui_win32 自己建托盘，这里不再建，否则两个图标）
    #[cfg(feature = "gui")]
    {
        std::thread::Builder::new()
            .name("iprc-tray".into())
            .spawn(tray_thread)
            .ok();
    }

    // ⑤ 界面
    #[cfg(feature = "win32_only")]
    {
        eprintln!("[i] Win32 版：不创建 OpenGL 上下文，直接使用原生界面");
        ui_win32::run();
        eprintln!("[i] 界面已关闭 ⇒ 进程退出");
        std::process::exit(0);
    }

    #[cfg(feature = "gui")]
    {
    let opts = eframe::NativeOptions {
        viewport: egui::ViewportBuilder::default()
            .with_inner_size([900.0, 780.0])
            .with_min_inner_size([1.0, 1.0])   // 允许缩到 1×1：收进托盘就是靠"缩到看不见"实现的
            .with_title("IPRC1000 原生内核")
            // ⚠ 2026-10-31：**不要**用 with_visible(false) 起步。
            //   eframe 对一个"从未显示过"的窗口，事件循环与 close_requested 的处理都会走偏
            //   （实测：启动后几十秒内进程静默退出，日志无任何错误）。
            //   启动显示窗口、关窗时再隐藏到托盘 —— 体验一样，稳定性完全不同。
            .with_visible(true),
        ..Default::default()
    };
    // ★ 界面循环结束就**自动重建窗口**，进程永不退出。
    //   为什么需要这一层：只要 run_native 返回（窗口被真关闭、显存上下文丢失、
    //   系统主题切换等），如果直接往下走，main 结束 → 进程退出 → 托盘也没了。
    //   循环重建后，无论界面因为什么原因消失，程序都还在，托盘随时能唤回。
    let mut round = 0u32;
    // ★★ 2026-11-02（客户装机场景）：界面创建失败必须**有尽头、有说法**。
    //   原来无论什么原因失败都"2 秒后重建"，永不放弃 —— 在没有 OpenGL 的机器上
    //   （微软基本显示适配器 / 远程桌面 / 老集显）表现就是：
    //     托盘图标在、按键和语音都正常、**界面永远打不开**，日志里只有一行行"界面循环结束"。
    //   客户看到的是"这软件坏了"，而实际上核心功能是好的。
    //   现在：连续失败到阈值就切"仅托盘模式"，并弹一次说明清楚原因和办法。
    let mut gui_fail: u32 = 0;
    // ★ 自测钩子（不影响正常使用）：设了 `IPRC_TEST_WAKE_MS` 就在 N 毫秒后自动置
    //   SHOW_REQ，用来验证"收进托盘后能否重建界面"——这条路径只有点托盘才会走到，
    //   自动化测试里没法点托盘。与 `--tab`（自测截图用）同性质。
    if let Ok(v) = std::env::var("IPRC_TEST_WAKE_MS") {
        if let Ok(ms) = v.parse::<u64>() {
            eprintln!("[测试] {ms} ms 后自动发唤回信号");
            std::thread::spawn(move || {
                std::thread::sleep(std::time::Duration::from_millis(ms));
                SHOW_REQ.store(true, Ordering::SeqCst);
            });
        }
    }
    loop {
        round += 1;
        let opts2 = eframe::NativeOptions {
            viewport: egui::ViewportBuilder::default()
                .with_inner_size([900.0, 660.0])
                .with_min_inner_size([1.0, 1.0])   // 允许缩到 1×1：收进托盘就是靠"缩到看不见"实现的
                .with_title("IPRC1000 原生内核")
                // ★ 托盘应用不该占任务栏按钮。
                //   必须在 ViewportBuilder 里设：用 Win32 的 SetWindowLongPtr 改扩展样式
                //   会被 winit 在下一帧覆盖回去（实测 0x40110 又变回 WS_EX_APPWINDOW）。
                .with_taskbar(false),
            ..Default::default()
        };
        let _ = &opts;
        // 自测钩子：让本次界面创建必然失败，用来验证"失败封顶 + 仅托盘模式"这条路径
        // （真机上很难凑出无 OpenGL 的环境）。
        // ⚠ 用**哨兵文件**而不只用环境变量：经 explorer 代启时环境变量传不进来
        //   （这也是"部署后程序行为不对"的常见坑）。
        // ★ 哨兵文件**自动过期**（5 分钟）：这个钩子原本是"文件存在就必失败"，
        //   结果测试完忘了删，用户第二天启动发现界面打不开 —— 一个测试开关
        //   不该有能力永久把正常界面挡在门外。现在只在标记新鲜时才生效。
        let force_fail = std::env::var("IPRC_FORCE_GUI_FAIL").is_ok() || {
            let p = std::env::temp_dir().join("iprc-force-gui-fail");
            match std::fs::metadata(&p).and_then(|m| m.modified()) {
                Ok(t) => t.elapsed().map(|d| d < std::time::Duration::from_secs(300)).unwrap_or(false),
                Err(_) => false,
            }
        };
        let r: Result<(), String> = if force_fail {
            Err(String::from("模拟失败：OpenGL 上下文创建失败（IPRC_FORCE_GUI_FAIL）"))
        } else {
            eframe::run_native(
                "IPRC1000 原生内核",
                opts2,
                Box::new(|cc| Ok(Box::new(ui_egui::App::new(cc)))),
            )
            .map_err(|e| format!("{e}"))
        };
        // ★★ 2026-11-01：区分"用户主动收进托盘"与"意外结束"。
        //   主动隐藏 ⇒ **等**唤回信号，绝不立刻重建（否则等于没隐藏，
        //   内存也白释放——这正是改之前"缩成 1×1"的老问题）。
        //   托盘线程独立存活，左键/菜单「打开界面」会把 SHOW_REQ 置位。
        if r.is_ok() {
            gui_fail = 0;   // 成功显示过一次，失败计数归零
        }
        if HIDE_REQUESTED.swap(false, Ordering::SeqCst) {
            eprintln!("[i] 界面已收进托盘（UI 线程结束，GL 上下文与框架内存已交还系统）");
            // ★★ 2026-11-01（真机：点关闭按钮"无反应"）：
            //   `run_native` 返回了，但 **HWND 仍留在桌面上** —— 成了"僵尸窗口"：
            //   没有事件循环，点 X 没有任何人处理，用户看到的就是"关了没反应"。
            //   （我上一轮把"标题还在"误判成陈旧属性，实际是窗口真的还活着。）
            destroy_leftover_window();
            // ★ 同时丢弃隐藏之前积累的陈旧唤回信号：否则一关闭就立刻重建，
            //   表现成"点了关闭，窗口闪一下就回来了"。
            SHOW_REQ.store(false, Ordering::SeqCst);
            // 两条唤回入口都认：托盘图标（SHOW_REQ）与"再双击一次 exe"（命名事件）。
            // 后者在托盘图标失效（强杀留下的僵尸图标）时是唯一还活着的入口。
            while !SHOW_REQ.swap(false, Ordering::SeqCst) && !show_event_fired() {
                std::thread::sleep(std::time::Duration::from_millis(200));
            }
            eprintln!("[i] 收到唤回信号 ⇒ 重建界面");
            continue;
        }
        let err_txt = format!("{r:?}");
        eprintln!("[i] 界面循环结束（第 {round} 轮）：{err_txt}");
        gui_fail += 1;

        // 连续失败到阈值 ⇒ 判定"本机无法创建 OpenGL 界面"，转仅托盘模式。
        // 为什么保留进程：按键注入、语音、音频都不依赖 GL，关掉等于把能用的功能也停了。
        if gui_fail >= 2 {
            // ★ 2026-11-02：不再"仅托盘"（那样客户看不见任何东西、也改不了设置）。
            //   改用 Win32 原生界面：纯 GDI、零显卡依赖，能改键位/切换输入法/一键修复。
            eprintln!(
                "[!] 连续 {gui_fail} 次无法创建图形界面（{err_txt}）⇒ 改用 Win32 原生界面"
            );
            ui_win32::run();
            eprintln!("[i] Win32 界面已关闭 ⇒ 进程退出");
            std::process::exit(0);
        }
        std::thread::sleep(std::time::Duration::from_secs(2));
    }
    }
}

/// 托盘请求"显示窗口"（UI 线程每帧检查；不能从托盘线程直接动 egui 的窗口）
pub use hotkey::HIDE_REQUESTED;
pub use hotkey::SHOW_REQ;


fn chrono_like_now() -> String {
    let d = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap_or_default();
    format!("{}s", d.as_secs())
}

/// 输出本进程的模块清单与线程概况（`--diag`）。
///
/// 用途：搞清楚"40 个线程、600 个句柄"分别是谁的 —— 只看总数没法判断是否正常，
/// 必须知道每个模块贡献了多少线程（GPU 驱动/音频引擎/系统线程池都会自己建线程）。
fn diag_report() -> String {
    use windows::Win32::System::Diagnostics::ToolHelp::*;
    let mut out = String::new();
    let pid = unsafe { windows::Win32::System::Threading::GetCurrentProcessId() };
    if let Some((rss, h, t)) = self_stats() {
        out.push_str(&format!("进程: RSS {rss} MB · 句柄 {h} · 线程 {t}\n"));
    }
    unsafe {
        // 模块
        if let Ok(snap) = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE, pid) {
            let mut me = MODULEENTRY32W {
                dwSize: std::mem::size_of::<MODULEENTRY32W>() as u32,
                ..Default::default()
            };
            out.push_str("\n模块（按大小前 20）:\n");
            let mut mods: Vec<(String, u32, usize)> = Vec::new();
            if Module32FirstW(snap, &mut me).is_ok() {
                loop {
                    let name = String::from_utf16_lossy(
                        &me.szModule[..me.szModule.iter().position(|&c| c == 0).unwrap_or(0)],
                    );
                    mods.push((name, me.modBaseSize, me.modBaseAddr as usize));
                    if Module32NextW(snap, &mut me).is_err() {
                        break;
                    }
                }
            }
            let _ = windows::Win32::Foundation::CloseHandle(snap);
            mods.sort_by(|a, b| b.1.cmp(&a.1));
            for (n, sz, base) in mods.iter().take(20) {
                out.push_str(&format!("  {n:<34} {:>7} KB  base=0x{base:X}\n", sz / 1024));
            }
            out.push_str(&format!("  （共 {} 个模块）\n", mods.len()));
        }
        // 线程起始地址 → 归到模块
        if let Ok(snap) = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0) {
            let mut te = THREADENTRY32 {
                dwSize: std::mem::size_of::<THREADENTRY32>() as u32,
                ..Default::default()
            };
            out.push_str("\n线程起始地址分布:\n");
            let mut n = 0usize;
            if Thread32First(snap, &mut te).is_ok() {
                loop {
                    if te.th32OwnerProcessID == pid {
                        n += 1;
                    }
                    if Thread32Next(snap, &mut te).is_err() {
                        break;
                    }
                }
            }
            let _ = windows::Win32::Foundation::CloseHandle(snap);
            out.push_str(&format!("  本进程线程数: {n}\n"));
        }
    }
    out
}

/// 取本进程的资源占用：(RSS MB, 句柄数, 线程数)。失败返回 None。
///
/// 用途：每 30 秒往日志写一行，长期运行时能看出"内存/句柄是否在涨"——
/// 外部监控工具在打包后的机器上没有，只能靠程序自己记。
pub fn self_stats() -> Option<(u64, u32, u32)> {
    use windows::Win32::System::Threading::{GetCurrentProcess, GetProcessHandleCount};
    unsafe {
        let h = GetCurrentProcess();
        // 内存
        let mut pmc = windows::Win32::System::ProcessStatus::PROCESS_MEMORY_COUNTERS::default();
        let ok = windows::Win32::System::ProcessStatus::GetProcessMemoryInfo(
            h,
            &mut pmc,
            std::mem::size_of::<windows::Win32::System::ProcessStatus::PROCESS_MEMORY_COUNTERS>() as u32,
        )
        .is_ok();
        if !ok {
            eprintln!("[!] self_stats: GetProcessMemoryInfo 失败");
            return None;
        }
        let rss = (pmc.WorkingSetSize / (1024 * 1024)) as u64;
        // 句柄
        let mut handles = 0u32;
        let _ = GetProcessHandleCount(h, &mut handles);
        // 线程数：遍历本进程的线程
        let snap = match windows::Win32::System::Diagnostics::ToolHelp::CreateToolhelp32Snapshot(
            windows::Win32::System::Diagnostics::ToolHelp::TH32CS_SNAPTHREAD,
            0,
        ) {
            Ok(h) => h,
            Err(e) => {
                eprintln!("[!] self_stats: CreateToolhelp32Snapshot 失败 {e:?}（句柄/线程仍有效）");
                return Some((rss, handles, 0));
            }
        };
        let mut te = windows::Win32::System::Diagnostics::ToolHelp::THREADENTRY32 {
            dwSize: std::mem::size_of::<windows::Win32::System::Diagnostics::ToolHelp::THREADENTRY32>() as u32,
            ..Default::default()
        };
        let pid = windows::Win32::System::Threading::GetCurrentProcessId();
        let mut threads = 0u32;
        if windows::Win32::System::Diagnostics::ToolHelp::Thread32First(snap, &mut te).is_ok() {
            loop {
                if te.th32OwnerProcessID == pid {
                    threads += 1;
                }
                if windows::Win32::System::Diagnostics::ToolHelp::Thread32Next(snap, &mut te).is_err() {
                    break;
                }
            }
        }
        let _ = windows::Win32::Foundation::CloseHandle(snap);
        Some((rss, handles, threads))
    }
}

/// 取真实桌面目录（走 Shell 的 Known Folder，兼容 OneDrive / 自定义重定向）
fn desktop_dir() -> std::path::PathBuf {
    use windows::Win32::UI::Shell::{SHGetKnownFolderPath, FOLDERID_Desktop, KNOWN_FOLDER_FLAG};
    unsafe {
        if let Ok(p) = SHGetKnownFolderPath(&FOLDERID_Desktop, KNOWN_FOLDER_FLAG(0), None) {
            let s = p.to_string().unwrap_or_default();
            // SHGetKnownFolderPath 分配的内存要用 CoTaskMemFree 释放
            windows::Win32::System::Com::CoTaskMemFree(Some(p.0 as *const _));
            if !s.is_empty() {
                return std::path::PathBuf::from(s);
            }
        }
    }
    // 退路：用户目录下的 Desktop
    std::path::PathBuf::from(std::env::var("USERPROFILE").unwrap_or_default()).join("Desktop")
}

/// 在桌面创建指向本程序的快捷方式（`--make-shortcut`）。
///
/// 为什么放在程序里：外部 shell 脚本调 COM 建 .lnk 会被安全策略拦，
/// 装 pywin32 又增加依赖。程序自己就在用 windows crate，直接调 IShellLink 最省事，
/// 用户重装/换机后也能让程序自己重建快捷方式。
fn make_desktop_shortcut() -> Result<(), String> {
    use windows::core::{Interface, PCWSTR};
    use windows::Win32::System::Com::{
        CoCreateInstance, CoInitializeEx, IPersistFile, CLSCTX_INPROC_SERVER,
        COINIT_APARTMENTTHREADED,
    };
    use windows::Win32::UI::Shell::{IShellLinkW, ShellLink};

    let exe = std::env::current_exe().map_err(|e| e.to_string())?;
    let dir = exe.parent().map(|p| p.to_path_buf()).unwrap_or_default();
    // ⚠ 不能拼 %USERPROFILE%\Desktop：本机桌面被重定向到 D://Document//Desktop，
    //   拼出来是"不存在的路径" ⇒ Save 报 0x80070003。
    let desk = desktop_dir();
    let lnk = desk.join("IPRC1000 原生版.lnk");

    fn wide(s: &str) -> Vec<u16> {
        s.encode_utf16().chain(std::iter::once(0)).collect()
    }
    let w_exe = wide(&exe.display().to_string());
    let w_dir = wide(&dir.display().to_string());
    let w_ico = wide(&dir.join("icon.ico").display().to_string());
    let w_desc = wide("IPRC1000 遥控器适配器（原生版）");
    let w_lnk = wide(&lnk.display().to_string());

    unsafe {
        let _ = CoInitializeEx(None, COINIT_APARTMENTTHREADED);
        let link: IShellLinkW =
            CoCreateInstance(&ShellLink, None, CLSCTX_INPROC_SERVER).map_err(|e| e.to_string())?;
        link.SetPath(PCWSTR(w_exe.as_ptr())).map_err(|e| e.to_string())?;
        let _ = link.SetWorkingDirectory(PCWSTR(w_dir.as_ptr()));
        let _ = link.SetIconLocation(PCWSTR(w_ico.as_ptr()), 0);
        let _ = link.SetDescription(PCWSTR(w_desc.as_ptr()));
        let pf: IPersistFile = Interface::cast(&link).map_err(|e| e.to_string())?;
        pf.Save(PCWSTR(w_lnk.as_ptr()), true).map_err(|e| e.to_string())?;
    }
    eprintln!("[快捷方式] 已创建 {}", lnk.display());
    Ok(())
}

/// 确保主窗口是"工具窗口"样式（不占任务栏 / 不进 Alt+Tab）。
///
/// ⚠ 为什么需要**反复**设置：
///   · egui 的 `ViewportBuilder::with_taskbar(false)` 在 eframe 0.29 的 glow 后端**不生效**
///     （实测扩展样式始终是 0x40110 = WS_EX_APPWINDOW）；
///   · 用 Win32 改一次也不行 —— winit 在窗口状态变化时会用自己的样式覆盖回去。
///   所以做成"每帧检查一次，不对就修"，代价是两次 GetWindowLong（微秒级）。
///
/// 用 `SWP_FRAMECHANGED` 而不是 ShowWindow(HIDE/SHOW)：后者会让窗口闪一下。
pub fn ensure_toolwindow() {
    unsafe {
        use windows::Win32::UI::WindowsAndMessaging::*;
        const WS_EX_TOOLWINDOW: isize = 0x80;
        const WS_EX_APPWINDOW: isize = 0x40000;
        let title: Vec<u16> = "IPRC1000 原生内核"
            .encode_utf16()
            .chain(std::iter::once(0))
            .collect();
        let h = FindWindowW(None, windows::core::PCWSTR(title.as_ptr()));
        if h.0 == 0 {
            return;
        }
        let cur = GetWindowLongPtrW(h, GWL_EXSTYLE);
        let want = (cur | WS_EX_TOOLWINDOW) & !WS_EX_APPWINDOW;
        if cur != want {
            let _ = SetWindowLongPtrW(h, GWL_EXSTYLE, want);
            // 不隐藏窗口，只让系统重算非客户区（任务栏按钮会随之更新）
            let _ = SetWindowPos(
                h,
                None,
                0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED,
            );
        }
    }
}

/// 设置主窗口的整体透明度（0=全透明，255=不透明）。
///
/// 为什么用这个来"收进托盘"：egui/eframe 0.29 在窗口 **不可见** 时不会调用 update()，
/// 于是托盘/F9 的唤回信号永远没人处理（试过 Visible(false)、Minimized、缩到 1×1 都不行）。
/// 而"透明"的窗口在 Windows 看来仍然是**可见窗口**，update 正常跑，唤回只改 alpha 即可。
///
/// 配合 `WS_EX_LAYERED`（必须置位，否则 SetLayeredWindowAttributes 无效），
/// 透明后同时把窗口缩到 1×1 挪到角落 —— 既不挡点击，也不占屏幕。
pub fn set_window_alpha(alpha: u8) {
    unsafe {
        use windows::Win32::UI::WindowsAndMessaging::*;
        const WS_EX_LAYERED: isize = 0x80000;
        let title: Vec<u16> = "IPRC1000 原生内核"
            .encode_utf16()
            .chain(std::iter::once(0))
            .collect();
        let h = FindWindowW(None, windows::core::PCWSTR(title.as_ptr()));
        if h.0 == 0 {
            return;
        }
        let cur = GetWindowLongPtrW(h, GWL_EXSTYLE);
        if cur & WS_EX_LAYERED == 0 {
            let _ = SetWindowLongPtrW(h, GWL_EXSTYLE, cur | WS_EX_LAYERED);
        }
        let _ = SetLayeredWindowAttributes(h, windows::Win32::Foundation::COLORREF(0), alpha, LWA_ALPHA);
    }
}

/// 把主窗口**最小化**（不是隐藏）。
///
/// 为什么要最小化而不是 `ViewportCommand::Visible(false)`：
/// egui 的 run_native 在**没有任何可见窗口**时会返回 → main 结束 → 进程退出。
/// 早先"关窗后程序直接消失"就是这个原因（隐藏 = 让 eframe 认为窗口没了）。
/// 最小化后窗口仍然存在（只是不可见），egui 会继续跑、托盘也能唤回。
#[allow(dead_code)]  // 已被 ViewportCommand::Minimized 取代（Win32 版会被 winit 还原）
pub fn minimize_window() {
    unsafe {
        use windows::Win32::UI::WindowsAndMessaging::*;
        let title: Vec<u16> = "IPRC1000 原生内核"
            .encode_utf16()
            .chain(std::iter::once(0))
            .collect();
        let h = FindWindowW(None, windows::core::PCWSTR(title.as_ptr()));
        if h.0 != 0 {
            // ★ 移到**屏幕外**，而不是最小化 / 隐藏：
            //   egui 的 run_native 在"没有可见窗口"时会返回 → 进程退出。
            //   最小化实测也会退（35 秒后就没了）；移到屏幕外则 visible 仍为 true，
            //   eframe 认为窗口还在，程序继续跑，用户也看不到它。
            let _ = SetWindowPos(
                h,
                HWND_TOP,
                -32000,
                -32000,
                900,
                660,
                SWP_NOACTIVATE | SWP_NOZORDER,
            );
        }
    }
}

// （已删）`restore_window()`：那套是"把窗口缩成 1×1"时代的唤回方式。
//   现在收进托盘会结束 UI 线程（没有窗口可恢复），唤回统一走 SHOW_REQ → 主循环重建窗口。


/// 托盘：`tray-icon` 自带消息循环，跑在独立线程里即可，不干扰 egui
fn tray_thread() {
    use tray_icon::menu::{Menu, MenuItem, PredefinedMenuItem};
    use tray_icon::{Icon, TrayIconBuilder};

    let menu = Menu::new();
    let mi_show = MenuItem::new("打开界面", true, None);
    // ★ 勾选状态取自**注册表的真实值**，不是"我们记着点过"
    //   （用户可能从任务管理器 → 启动项 里禁掉，菜单要跟着反映）
    let mi_auto = tray_icon::menu::CheckMenuItem::new(
        "开机自启",
        true,
        autostart::is_enabled(),
        None,
    );
    let mi_cable = MenuItem::new("安装 / 修复虚拟声卡", true, None);
    let mi_driver = MenuItem::new("安装虚拟键盘驱动", true, None);
    // ★ "重启程序"：ETW 的 ACL 音频通道跑久了会停止投递（按键还正常），
    //   重启是当前唯一的恢复手段。放在菜单里，用户不用去找快捷方式。
    let mi_restart = MenuItem::new("重启程序（音频通道失效时用）", true, None);
    let mi_quit = MenuItem::new("退出", true, None);
    let _ = menu.append(&mi_show);
    let _ = menu.append(&PredefinedMenuItem::separator());
    let _ = menu.append(&mi_auto);
    let _ = menu.append(&PredefinedMenuItem::separator());
    let _ = menu.append(&mi_cable);
    let _ = menu.append(&mi_driver);
    let _ = menu.append(&PredefinedMenuItem::separator());
    let _ = menu.append(&mi_restart);
    let _ = menu.append(&mi_quit);

    let icon = load_icon().unwrap_or_else(|| {
        let mut rgba = Vec::with_capacity(16 * 16 * 4);
        for _ in 0..(16 * 16) {
            rgba.extend_from_slice(&[60, 140, 240, 255]);
        }
        Icon::from_rgba(rgba, 16, 16).expect("内置图标")
    });

    let Ok(_tray) = TrayIconBuilder::new()
        .with_menu(Box::new(menu))
        // ★ 左键不弹菜单（默认是"左右键都弹"）：左键留给"唤出主界面"，
        //   右键才是菜单 —— 这是 Windows 托盘图标的通行约定。
        .with_menu_on_left_click(false)
        .with_tooltip("IPRC1000 原生内核")
        .with_icon(icon)
        .build()
    else {
        return;
    };

    // ★ 托盘右键菜单要弹出，必须有**消息循环**：
    //   TrackPopupMenu 是在托盘窗口的消息处理里跑的，光靠 channel 收不到。
    //   之前只有 recv_timeout，所以菜单点了没反应。
    unsafe {
        let _ = windows::Win32::System::Com::CoInitializeEx(
            None,
            windows::Win32::System::Com::COINIT_APARTMENTTHREADED,
        );
    }
    // ★ 托盘图标点击：左键抬起 = 唤出主界面（右键由系统弹菜单，不在这里处理）
    {
        use tray_icon::{MouseButton, MouseButtonState, TrayIconEvent};
        std::thread::Builder::new()
            .name("tray-click".into())
            .spawn(move || {
                for ev in TrayIconEvent::receiver() {
                    if let TrayIconEvent::Click {
                        button: MouseButton::Left,
                        button_state: MouseButtonState::Up,
                        ..
                    } = ev
                    {
                        SHOW_REQ.store(true, Ordering::Relaxed);
                    }
                }
            })
            .ok();
    }

    let rx = tray_icon::menu::MenuEvent::receiver();
    loop {
        // 泵一轮窗口消息（托盘窗口的消息都在这里）
        unsafe {
            let mut msg = windows::Win32::UI::WindowsAndMessaging::MSG::default();
            while windows::Win32::UI::WindowsAndMessaging::PeekMessageW(
                &mut msg,
                None,
                0,
                0,
                windows::Win32::UI::WindowsAndMessaging::PM_REMOVE,
            )
            .as_bool()
            {
                let _ = windows::Win32::UI::WindowsAndMessaging::TranslateMessage(&msg);
                windows::Win32::UI::WindowsAndMessaging::DispatchMessageW(&msg);
            }
        }
        // 100 ms 足够：菜单点击的体感阈值在 150 ms 左右，而且这里只是收 channel，
        // 真正弹出菜单是系统在消息循环里做的（下面的 PeekMessage）。
        if let Ok(ev) = rx.recv_timeout(Duration::from_millis(100)) {
            if ev.id == mi_restart.id() {
                // 用当前 exe 路径重新拉起自己，然后退出。参数不带（保持默认页面）
                // ★ 必须带 --restarted：让新实例"等老实例退出"而不是抢锁失败自杀，
                //   否则用户点一次重启会得到"两个都没了"（20:53 实测）。
                if let Ok(exe) = std::env::current_exe() {
                    let _ = std::process::Command::new(exe).arg("--restarted").spawn();
                }
                std::thread::sleep(std::time::Duration::from_millis(300));
                use std::io::Write as _;
                eprintln!("[托盘] 用户点「重启程序」");
                let _ = std::io::stderr().flush();
                std::process::exit(0);
            }
            if ev.id == mi_quit.id() {
                // exit() 不刷缓冲 ⇒ 日志最后几行会丢，排查时会误判成"莫名退出"
                use std::io::Write as _;
                eprintln!("[托盘] 用户点「退出」");
                let _ = std::io::stderr().flush();
                std::process::exit(0);
            }
            if ev.id == mi_show.id() {
                // ★ 2026-11-01：收进托盘后 UI 线程已经结束，没有窗口可以"恢复"了
                //   （原来这里还要调 restore_window()，那套是"缩成 1×1"时代的做法）。
                //   现在只需要置信号，主循环会在 200 ms 内重建窗口。
                SHOW_REQ.store(true, Ordering::Relaxed);
            }
            if ev.id == mi_auto.id() {
                let want = !autostart::is_enabled();
                let ok = autostart::set_enabled(want);
                mi_auto.set_checked(if ok { want } else { autostart::is_enabled() });
                eprintln!(
                    "[托盘] 开机自启 → {}",
                    if ok {
                        if want { "已开启" } else { "已关闭" }
                    } else {
                        "设置失败（注册表不可写）"
                    }
                );
            }
            if ev.id == mi_cable.id() {
                std::thread::spawn(|| {
                    let r = installer::install_cable();
                    eprintln!("[托盘] 安装虚拟声卡：{r}");
                });
            }
            if ev.id == mi_driver.id() {
                std::thread::spawn(|| {
                    let r = installer::install_winuhid();
                    eprintln!("[托盘] 安装驱动：{r}");
                });
            }
        }
    }
    #[allow(unreachable_code)]
    {
        drop(_tray);
    }
}

/// 把主窗口显示出来。**已被 SHOW_REQ 机制取代**（eframe 不认外部 ShowWindow），保留作参考。
#[allow(dead_code)]
fn show_window() {
    unsafe {
        use windows::Win32::UI::WindowsAndMessaging::*;
        let title: Vec<u16> = "IPRC1000 原生内核"
            .encode_utf16()
            .chain(std::iter::once(0))
            .collect();
        let h = FindWindowW(None, windows::core::PCWSTR(title.as_ptr()));
        if h.0 != 0 {
            {
                let _ = ShowWindow(h, SW_SHOW);
                let _ = SetForegroundWindow(h);
            }
        }
    }
}

fn load_icon() -> Option<tray_icon::Icon> {
    let exe = std::env::current_exe().ok()?;
    let dir = exe.parent()?;
    // ★ 顺序很重要：32/48 的 PNG 最稳。
    //   原来只给 icon.ico，而它是 256×256 单帧 —— tray-icon 加载失败后
    //   静默退回"纯蓝色方块"（这就是你看到那个方块的原因）。
    for p in [
        dir.join("icon-32.png"),
        dir.join("icon-48.png"),
        dir.join("assets").join("icon-32.png"),
        dir.join("icon.ico"),
    ] {
        if let Ok(img) = image::open(&p) {
            let rgba = img.to_rgba8();
            let (w, h) = rgba.dimensions();
            return tray_icon::Icon::from_rgba(rgba.into_raw(), w, h).ok();
        }
    }
    None
}

/// 清掉上一次残留的 ETW 会话（**启动第一件事**）。
///
/// 为什么必须在最前面：ETW 会话是系统级资源，进程被强杀/崩溃时**不会自动停止**。
/// 残留会话会让新实例"启动成功却收不到任何事件"——按键无反应、语音没声音，
/// 而日志里一切正常，极难排查（真机踩过两次）。
///
/// ⚠ 2026-11-01：这个函数在一次批量删代码中误删过（`iprc1000_native` 没有版本控制，
///   所以无法从 git 恢复）。语义与 `backend::cleanup_stale_session()` 一致：
///   `logman stop <会话名> -ets`，同名会话停一次即可。
fn clean_stale_trace() {
    use std::os::windows::process::CommandExt;
    const CREATE_NO_WINDOW: u32 = 0x0800_0000;
    // 失败是常态（本来就没有残留会话）⇒ 不打印，避免每次启动刷一行无关信息
    let _ = std::process::Command::new("logman")
        .args(["stop", "Iprc1000AudioBridge", "-ets"])
        .stdout(std::process::Stdio::null())
        .stderr(std::process::Stdio::null())
        .creation_flags(CREATE_NO_WINDOW)
        .status();
}

/// 销毁 `run_native` 返回后可能残留的窗口句柄。
///
/// 为什么需要：eframe 事件循环退出后，HWND 不一定会被销毁。留下的是"僵尸窗口"——
/// 看得见、点关闭没反应（没有消息循环在跑），还会挡住下面的窗口。
/// 补一刀：先 WM_CLOSE 让窗口走正常销毁流程，再 DestroyWindow 兜底。
fn destroy_leftover_window() {
    use windows::Win32::Foundation::{BOOL, HWND, LPARAM, RECT, WPARAM};
    use windows::Win32::UI::WindowsAndMessaging::*;
    use windows::Win32::System::Threading::GetCurrentProcessId;

    // 枚举本进程遗留的"辅助窗口"（回调里收集句柄）。
    unsafe extern "system" fn collect(h: HWND, lp: LPARAM) -> BOOL {
        let out = &mut *(lp.0 as *mut Vec<HWND>);
        let mut pid = 0u32;
        GetWindowThreadProcessId(h, Some(&mut pid));
        if pid == GetCurrentProcessId() && IsWindowVisible(h).as_bool() {
            // ★ 两个过滤条件缺一不可：
            //   · 只收**可见**的：真正造成"左上角小方块"的是可见窗口；
            //     不可见的那些是 winit 内部对象，跨线程 DestroyWindow 必然失败（Win32 限制），
            //     去碰它们只会刷出无意义的失败日志。
            //   · 只收"小窗口"：主窗口是几百像素，不会误伤。
            let mut rc = RECT::default();
            let _ = GetWindowRect(h, &mut rc);
            let small = (rc.right - rc.left) <= 40 && (rc.bottom - rc.top) <= 40;
            if small {
                let mut cls = [0u16; 128];
                let n = GetClassNameW(h, &mut cls);
                let name = String::from_utf16_lossy(&cls[..n.max(0) as usize]);
                // winit 的事件循环会建一个 "Thread Event Target" 窗口；
                // 事件循环一结束，它就没有消息循环了 ⇒ Windows 标"未响应"，
                // 表现为**屏幕左上角一个十几像素的小方块**（真机 2026-11-02 确认）。
                if name.contains("Thread Event Target") || rc.right - rc.left == 0 {
                    out.push(h);
                }
            }
        }
        BOOL(1)
    }

    unsafe {
        // ① 按标题找主窗口（原来就有的那条）
        let title: Vec<u16> = "IPRC1000 原生内核"
            .encode_utf16()
            .chain(std::iter::once(0))
            .collect();
        let h = FindWindowW(None, windows::core::PCWSTR(title.as_ptr()));
        if h.0 != 0 {
            let _ = PostMessageW(h, WM_CLOSE, WPARAM(0), LPARAM(0));
            let _ = DestroyWindow(h);
        }

        // ② 扫本进程留下的辅助窗口并销毁。
        //    这是"左上角 10 像素未响应方块"的根因：它没有标题，按标题永远找不到。
        let mut victims: Vec<HWND> = Vec::new();
        let _ = EnumWindows(
            Some(collect),
            LPARAM(&mut victims as *mut Vec<HWND> as isize),
        );
        for v in &victims {
            // ★★ 关键修正（2026-11-02 实测）：这里**只能隐藏，不能销毁**。
            //   winit 把 "Thread Event Target" 窗口**按线程缓存**（thread-local），一个线程只建一次。
            //   销毁它之后，下一轮事件循环以为它还在、不会重建 ⇒ 新界面缺少事件目标窗口，
            //   跨线程唤醒（托盘/热键触发的重绘）会往已销毁的句柄发消息，静默失效。
            //   实测对比：全新启动 10 个窗口（含事件目标）；销毁后重建只剩 9 个（缺事件目标）。
            //
            //   隐藏即可消除视觉问题：不可见的窗口不会被画出"未响应"幽灵方块，
            //   而句柄仍然有效，winit 复用它，事件循环照常工作。
            let _ = ShowWindow(*v, SW_HIDE);
        }
        if !victims.is_empty() {
            eprintln!("[i] 已隐藏 {} 个遗留辅助窗口（winit 事件目标）", victims.len());
        }
    }
}
