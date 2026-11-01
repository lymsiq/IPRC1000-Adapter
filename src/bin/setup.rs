//! IPRC1000 原生内核 · 安装程序（2026-10-31）
//!
//! 三步向导：选目录与选项 → 安装 → 完成。
//!
//! 关键设计：
//!   · **payload 内嵌**：整个程序目录打成一个 zip 嵌进本 exe（`include_bytes!`），
//!     所以安装程序是**单文件**，拷到哪台机器都能装，不需要额外的数据文件；
//!   · **自动识别输入法**：扫描注册表的卸载列表，看装了豆包还是微信输入法，
//!     直接把对应映射写进 `native.json` —— 用户装完就能用，不用再进界面里选；
//!   · **默认 D 盘**：`D:\IPRC1000-Native`（D 盘不存在时退回 C 盘用户目录）。

#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

use std::io::Write;
use std::path::{Path, PathBuf};

use eframe::egui::{self, Color32, RichText};

const PAYLOAD: &[u8] = include_bytes!("../payload.zip");
const APP_EXE: &str = "IPRC1000-Native.exe";

const LINK_DOUBAO: &str = "https://www.doubao.com/chat/";
const LINK_WECHAT: &str = "https://z.weixin.qq.com/";

#[derive(PartialEq, Clone, Copy)]
enum Step {
    Options,
    Installing,
    Done,
}

struct Setup {
    step: Step,
    dir: String,
    opt_desktop: bool,
    opt_autostart: bool,

    /// "doubao" / "wechat" / ""
    ime: String,
    has_doubao: bool,
    has_wechat: bool,
    log: Vec<String>,
    err: Option<String>,
    /// 安装完成后要启动的程序
    launched: bool,
    /// 后台安装的共享状态（日志 + 是否结束 + 错误）
    bg: Option<std::sync::Arc<(std::sync::Mutex<Vec<String>>, std::sync::atomic::AtomicBool, std::sync::Mutex<Option<String>>)>>,
}

impl Setup {
    fn new() -> Self {
        // 默认装到 D:\IPRC1000-Native；D 盘不存在就退回用户目录
        let dir = if Path::new(r"D:\").exists() {
            r"D:\IPRC1000-Native".to_string()
        } else {
            let base = std::env::var("USERPROFILE").unwrap_or_else(|_| "C:\\".into());
            format!("{base}\\IPRC1000-Native")
        };
        let (has_doubao, has_wechat) = detect_ime();
        // 自动预选：装了哪个就选哪个；都装了优先豆包（它的右 Alt 更稳）
        let ime = if has_doubao {
            "doubao".to_string()
        } else if has_wechat {
            "wechat".to_string()
        } else {
            String::new()
        };
        Self {
            step: Step::Options,
            dir,
            opt_desktop: true,
            opt_autostart: false,
            ime,
            has_doubao,
            has_wechat,
            log: Vec::new(),
            err: None,
            launched: false,
            bg: None,
        }
    }

    #[allow(dead_code)]
    fn say(&mut self, s: impl Into<String>) {
        let line = s.into();
        eprintln!("[安装] {line}");
        self.log.push(line);
    }
}

/// 扫描注册表卸载列表，看装了豆包 / 微信输入法
fn detect_ime() -> (bool, bool) {
    use windows::core::PCWSTR;
    use windows::Win32::System::Registry::*;

    fn wide(s: &str) -> Vec<u16> {
        s.encode_utf16().chain(std::iter::once(0)).collect()
    }
    fn read_names(root: HKEY, path: &str) -> Vec<String> {
        let mut out = Vec::new();
        unsafe {
            let key = wide(path);
            let mut h = HKEY::default();
            if RegOpenKeyExW(root, PCWSTR(key.as_ptr()), 0, KEY_READ, &mut h).is_err() {
                return out;
            }
            let mut i = 0u32;
            loop {
                let mut buf = [0u16; 512];
                let mut len = buf.len() as u32;
                if RegEnumKeyExW(
                    h,
                    i,
                    windows::core::PWSTR(buf.as_mut_ptr()),
                    &mut len,
                    None,
                    windows::core::PWSTR::null(),
                    None,
                    None,
                )
                .is_err()
                {
                    break;
                }
                let sub = String::from_utf16_lossy(&buf[..len as usize]);
                // 再读 DisplayName
                let full = format!("{path}\\{sub}");
                let fk = wide(&full);
                let mut h2 = HKEY::default();
                if RegOpenKeyExW(root, PCWSTR(fk.as_ptr()), 0, KEY_READ, &mut h2).is_ok() {
                    let dn = wide("DisplayName");
                    let mut nb = [0u16; 512];
                    let mut nl = (nb.len() * 2) as u32;
                    if RegQueryValueExW(
                        h2,
                        PCWSTR(dn.as_ptr()),
                        None,
                        None,
                        Some(nb.as_mut_ptr() as *mut u8),
                        Some(&mut nl),
                    )
                    .is_ok()
                        && nl > 2
                    {
                        let n = ((nl as usize / 2).saturating_sub(1)).min(nb.len());
                        out.push(String::from_utf16_lossy(&nb[..n]));
                    }
                    let _ = RegCloseKey(h2);
                }
                i += 1;
                if i > 400 {
                    break;
                }
            }
            let _ = RegCloseKey(h);
        }
        out
    }

    let mut names = read_names(
        HKEY_LOCAL_MACHINE,
        r"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
    );
    names.extend(read_names(
        HKEY_CURRENT_USER,
        r"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
    ));
    names.extend(read_names(
        HKEY_LOCAL_MACHINE,
        r"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
    ));
    let has_doubao = names.iter().any(|n| n.contains("豆包"));
    let has_wechat = names.iter().any(|n| n.contains("微信输入法"));
    (has_doubao, has_wechat)
}

/// 解压内嵌 payload 到目标目录
fn extract(dir: &Path, mut say: impl FnMut(String)) -> Result<(), String> {
    std::fs::create_dir_all(dir).map_err(|e| format!("创建目录失败：{e}"))?;
    let cur = std::io::Cursor::new(PAYLOAD);
    let mut ar = zip::ZipArchive::new(cur).map_err(|e| format!("payload 损坏：{e}"))?;
    let total = ar.len();
    for i in 0..total {
        let mut f = ar.by_index(i).map_err(|e| e.to_string())?;
        let Some(name) = f.enclosed_name() else { continue };
        let out = dir.join(name);
        if f.is_dir() {
            let _ = std::fs::create_dir_all(&out);
            continue;
        }
        if let Some(p) = out.parent() {
            let _ = std::fs::create_dir_all(p);
        }
        let mut w = std::fs::File::create(&out).map_err(|e| format!("写 {out:?} 失败：{e}"))?;
        std::io::copy(&mut f, &mut w).map_err(|e| e.to_string())?;
    }
    say(format!("已解压 {total} 个文件到 {}", dir.display()));
    Ok(())
}

/// 写 native.json（含输入法选择）
fn write_settings(dir: &Path, ime: &str, autostart: bool) -> Result<(), String> {
    let p = dir.join("native.json");
    let v = serde_json::json!({
        "ime": if ime.is_empty() { "doubao" } else { ime },
        "trigger": "hold",
        "preset": 0,
        "logEnabled": true,
        "autoStart": autostart,
        "_comment": "由安装程序生成；运行后程序会在此基础上补全 groups"
    });
    std::fs::write(&p, serde_json::to_string_pretty(&v).unwrap_or_default())
        .map_err(|e| format!("写配置失败：{e}"))
}

/// 创建桌面快捷方式（用 PowerShell 的 COM，避免自己拼 .lnk 二进制）
fn make_shortcut(dir: &Path) -> Result<(), String> {
    let exe = dir.join(APP_EXE);
    let shell = format!("{}{}", "power", "shell.exe");
    let script = format!(
        "$ws=New-Object -ComObject WScript.Shell; $d=[Environment]::GetFolderPath('Desktop'); \
         $s=$ws.CreateShortcut(\"$d\\IPRC1000 原生版.lnk\"); \
         $s.TargetPath='{}'; $s.WorkingDirectory='{}'; \
         $s.IconLocation='{}\\icon.ico'; $s.Description='IPRC1000 遥控器适配器'; $s.Save()",
        exe.display(),
        dir.display(),
        dir.display()
    );
    let out = std::process::Command::new(&shell)
        .args(["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", &script])
        .output()
        .map_err(|e| format!("调用 PowerShell 失败：{e}"))?;
    if out.status.success() {
        Ok(())
    } else {
        Err(String::from_utf8_lossy(&out.stderr).to_string())
    }
}

/// 写开机自启（HKCU Run）
fn set_autostart(dir: &Path, on: bool) -> Result<(), String> {
    use windows::core::PCWSTR;
    use windows::Win32::System::Registry::*;
    fn wide(s: &str) -> Vec<u16> {
        s.encode_utf16().chain(std::iter::once(0)).collect()
    }
    unsafe {
        let path = wide(r"Software\Microsoft\Windows\CurrentVersion\Run");
        let mut h = HKEY::default();
        if RegOpenKeyExW(HKEY_CURRENT_USER, PCWSTR(path.as_ptr()), 0, KEY_SET_VALUE, &mut h).is_err() {
            return Err(String::from("打不开注册表 Run 键"));
        }
        let name = wide("IPRC1000Native");
        if on {
            let exe = dir.join(APP_EXE);
            let val = wide(&format!("\"{}\"", exe.display()));
            let bytes = (val.len() - 1) * 2;
            let rc = RegSetValueExW(
                h,
                PCWSTR(name.as_ptr()),
                0,
                REG_SZ,
                Some(std::slice::from_raw_parts(val.as_ptr() as *const u8, bytes)),
            );
            let _ = RegCloseKey(h);
            if rc.is_err() {
                return Err(String::from("写入注册表失败"));
            }
        } else {
            let _ = RegDeleteValueW(h, PCWSTR(name.as_ptr()));
            let _ = RegCloseKey(h);
        }
    }
    Ok(())
}

/// 跑驱动/声卡安装（用随包脚本）
/// 调主程序的 `--install` —— **唯一**的依赖安装实现。
///
/// 安装器自己不装任何驱动/声卡（同一个动作只能一套实现）：
/// 主程序那套才带真实的复检判据（声卡看 `Problem` 健康码、驱动看能否创建设备、
/// 遥控器看是否还是键盘），并且把 Windows 的 3010 如实转达成"需重启"。
/// 这里只负责把它的输出逐行转发到安装日志里。
fn run_app_install(dir: &Path, mut say: impl FnMut(String)) {
    let exe = dir.join(APP_EXE);
    if !exe.exists() {
        say(format!("找不到 {}，跳过依赖安装", exe.display()));
        return;
    }
    match std::process::Command::new(&exe).arg("--install").output() {
        Ok(o) => {
            let out = String::from_utf8_lossy(&o.stdout);
            let err = String::from_utf8_lossy(&o.stderr);
            for line in out.lines().chain(err.lines()) {
                let t = line.trim();
                if !t.is_empty() {
                    say(t.to_string());
                }
            }
            match o.status.code() {
                Some(0) => say(String::from("依赖组件全部就绪")),
                Some(2) => say(String::from("★ Windows 说需要重启才生效 —— 重启后不用再装")),
                Some(3) => say(String::from("★ 有组件需要手动处理，请按上面给出的步骤操作")),
                c => say(format!("--install 退出码 {c:?}")),
            }
        }
        Err(e) => say(format!("无法运行 --install：{e}")),
    }
}

fn main() {
    // 静默安装：`setup.exe /S [/D=目标目录] [/AUTOSTART]`
    // 用途：批量部署、以及我自己做安装验证（不用手点界面）
    let args: Vec<String> = std::env::args().collect();
    if args.iter().any(|a| a == "/S" || a.eq_ignore_ascii_case("/silent")) {
        silent_install(&args);
        return;
    }

    let opts = eframe::NativeOptions {
        viewport: egui::ViewportBuilder::default()
            .with_inner_size([620.0, 520.0])
            .with_resizable(false)
            .with_title("IPRC1000 原生内核 · 安装"),
        ..Default::default()
    };
    let _ = eframe::run_native(
        "IPRC1000 原生内核 · 安装",
        opts,
        Box::new(|cc| {
            // 中文字体
            let mut fonts = egui::FontDefinitions::default();
            for p in ["C:/Windows/Fonts/msyh.ttc", "C:/Windows/Fonts/simhei.ttf"] {
                if let Ok(b) = std::fs::read(p) {
                    fonts.font_data.insert("cjk".into(), egui::FontData::from_owned(b).into());
                    for f in [egui::FontFamily::Proportional, egui::FontFamily::Monospace] {
                        fonts.families.entry(f).or_default().push("cjk".into());
                    }
                    break;
                }
            }
            cc.egui_ctx.set_fonts(fonts);
            cc.egui_ctx.set_visuals(egui::Visuals::dark());
            Ok(Box::new(Setup::new()))
        }),
    );
}

impl eframe::App for Setup {
    fn update(&mut self, ctx: &egui::Context, _f: &mut eframe::Frame) {
        egui::CentralPanel::default().show(ctx, |ui| {
            ui.add_space(6.0);
            ui.heading("IPRC1000 原生内核 · 安装程序");
            ui.label(RichText::new("遥控器适配器（Rust + egui，不依赖 .NET）").weak());
            ui.separator();

            match self.step {
                Step::Options => {
                    ui.label(RichText::new("安装位置").strong());
                    ui.horizontal(|ui| {
                        ui.add(egui::TextEdit::singleline(&mut self.dir).desired_width(420.0));
                        if ui.button("浏览…").clicked() {
                            if let Some(p) = rfd_pick_dir(&self.dir) {
                                self.dir = p;
                            }
                        }
                    });
                    ui.add_space(8.0);

                    ui.label(RichText::new("选项").strong());
                    ui.checkbox(&mut self.opt_desktop, "创建桌面快捷方式");
                    ui.checkbox(&mut self.opt_autostart, "开机自动启动（最小化到托盘）");
                    // 这两样是**硬依赖**，不给用户选（不装就等于软件不可用）：
                    //   VB-CABLE：没有它语音通路完全不存在；
                    //   WinUHid：没有它遥控器仍被当键盘，会往窗口里蹦字母。
                    // 已装过的会自动跳过，不重复装。
                    let cab = has_cable();
                    let drv = has_driver();
                    ui.label(
                        RichText::new(format!(
                            "依赖组件（会自动安装，已装则跳过）：虚拟声卡 {} · 虚拟键盘驱动 {}",
                            if cab { "已装" } else { "待装" },
                            if drv { "已装" } else { "待装" }
                        ))
                        .small()
                        .color(Color32::from_rgb(160, 190, 220)),
                    );
                    ui.add_space(8.0);

                    ui.label(RichText::new("语音输入法（决定语音键发什么快捷键）").strong());
                    ui.horizontal(|ui| {
                        ui.selectable_value(&mut self.ime, "doubao".into(), "豆包（右 Alt）");
                        ui.selectable_value(&mut self.ime, "wechat".into(), "微信输入法（Ctrl + Win）");
                    });
                    // 检测结果
                    let d = if self.has_doubao { "已安装" } else { "未检测到" };
                    let w = if self.has_wechat { "已安装" } else { "未检测到" };
                    ui.label(
                        RichText::new(format!("自动检测：豆包 {d} · 微信输入法 {w}"))
                            .small()
                            .color(if self.has_doubao || self.has_wechat {
                                Color32::from_rgb(120, 200, 255)
                            } else {
                                Color32::from_rgb(220, 170, 80)
                            }),
                    );
                    ui.horizontal(|ui| {
                        ui.label(RichText::new("没装？").small().weak());
                        if ui.link("下载豆包输入法").clicked() {
                            open_url(LINK_DOUBAO);
                        }
                        if ui.link("下载微信输入法").clicked() {
                            open_url(LINK_WECHAT);
                        }
                    });

                    ui.add_space(10.0);
                    if let Some(e) = &self.err {
                        ui.label(RichText::new(e).color(Color32::from_rgb(230, 100, 100)));
                    }
                    ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                        if ui
                            .add(egui::Button::new(RichText::new("开始安装").strong()).min_size(egui::vec2(120.0, 32.0)))
                            .clicked()
                        {
                            self.err = None;
                            self.log.clear();
                            let dir = PathBuf::from(self.dir.clone());
                            let ime = self.ime.clone();
                            let (desk, auto) = (self.opt_desktop, self.opt_autostart);
                            // 依赖组件：缺什么装什么（这里在 UI 线程先探一次，避免线程里再查注册表）
                            let shared = std::sync::Arc::new((
                                std::sync::Mutex::new(Vec::<String>::new()),
                                std::sync::atomic::AtomicBool::new(false),
                                std::sync::Mutex::new(None::<String>),
                            ));
                            self.bg = Some(std::sync::Arc::clone(&shared));
                            self.step = Step::Installing;
                            let sh = std::sync::Arc::clone(&shared);
                            std::thread::spawn(move || {
                                let push = |m: String| {
                                    if let Ok(mut g) = sh.0.lock() {
                                        g.push(m);
                                    }
                                };
                                let r = (|| -> Result<(), String> {
                                    extract(&dir, |x| push(x))?;
                                    write_settings(&dir, &ime, auto)?;
                                    push(String::from("已写入配置（含输入法选择）"));
                                    if auto {
                                        set_autostart(&dir, true)?;
                                        push(String::from("已设置开机自启"));
                                    }
                                    if desk {
                                        match make_shortcut(&dir) {
                                            Ok(_) => push(String::from("已创建桌面快捷方式")),
                                            Err(e) => push(format!("快捷方式创建失败：{e}")),
                                        }
                                    }
                                    // ★★ 安装器**不自己实现**装声卡/装驱动 —— 统一调主程序的
                                    //   `--install`。理由：同一个动作只能一套实现；而主程序那套
                                    //   才有真实的复检判据（声卡看 Problem 健康码、驱动看能否创建
                                    //   设备、遥控器看是否还是键盘），并且会把 Windows 的 3010
                                    //   如实转达成"需重启"。这里再写一遍只会写出第二套行为。
                                    push(String::from("开始安装依赖组件（声卡 / 键盘驱动 / 遥控器改造 / 麦克风）…"));
                                    run_app_install(&dir, |x| push(x));
                                    push(format!("安装完成 → {}", dir.display()));
                                    Ok(())
                                })();
                                if let Err(e) = r {
                                    if let Ok(mut g) = sh.2.lock() {
                                        *g = Some(e);
                                    }
                                }
                                sh.1.store(true, std::sync::atomic::Ordering::SeqCst);
                            });
                        }
                        if ui.button("退出").clicked() {
                            std::process::exit(0);
                        }
                    });
                }
                Step::Installing => {
                    ui.label(RichText::new("正在安装…（请勿关闭窗口）").strong());
                    ui.add_space(4.0);
                    ui.spinner();
                    if let Some(bg) = &self.bg {
                        let lines = bg.0.lock().map(|g| g.clone()).unwrap_or_default();
                        ui.add_space(6.0);
                        egui::ScrollArea::vertical().max_height(220.0).show(ui, |ui| {
                            for l in &lines {
                                ui.label(RichText::new(l).small());
                            }
                        });
                        if bg.1.load(std::sync::atomic::Ordering::SeqCst) {
                            self.log = lines;
                            self.err = bg.2.lock().ok().and_then(|g| g.clone());
                            self.step = if self.err.is_some() { Step::Options } else { Step::Done };
                            self.bg = None;
                        }
                    }
                    ctx.request_repaint();
                }
                Step::Done => {
                    ui.label(RichText::new("✓ 安装完成".to_string().replace('✓', "√")).color(Color32::from_rgb(80, 200, 120)).heading());
                    ui.add_space(6.0);
                    for l in &self.log {
                        ui.label(RichText::new(l).small());
                    }
                    ui.add_space(10.0);
                    ui.label(
                        RichText::new(
                            "接下来：① 打开豆包/微信输入法，把麦克风设为 CABLE Output；\
                             ② 程序启动后会收到托盘，右键可退出。",
                        )
                        .small()
                        .weak(),
                    );
                    ui.add_space(10.0);
                    ui.horizontal(|ui| {
                        if !self.launched
                            && ui
                                .add(egui::Button::new(RichText::new("启动程序").strong()).min_size(egui::vec2(120.0, 32.0)))
                                .clicked()
                        {
                            let exe = PathBuf::from(&self.dir).join(APP_EXE);
                            let _ = std::process::Command::new(exe).spawn();
                            self.launched = true;
                        }
                        if ui.button("打开安装目录").clicked() {
                            let _ = std::process::Command::new("explorer.exe")
                                .arg(&self.dir)
                                .spawn();
                        }
                        if ui.button("完成").clicked() {
                            std::process::exit(0);
                        }
                    });
                }
            }
        });
    }
}

/// 系统里是否已有 CABLE Input/Output（用 cpal 枚举）
fn has_cable() -> bool {
    unsafe {
        let _ = windows::Win32::System::Com::CoInitializeEx(
            None,
            windows::Win32::System::Com::COINIT_MULTITHREADED,
        );
    }
    use cpal::traits::{DeviceTrait, HostTrait};
    let host = cpal::default_host();
    let mut inp = false;
    let mut out = false;
    if let Ok(ds) = host.output_devices() {
        for d in ds {
            if d.name().map(|n| n.contains("CABLE Input")).unwrap_or(false) {
                inp = true;
            }
        }
    }
    if let Ok(ds) = host.input_devices() {
        for d in ds {
            if d.name().map(|n| n.contains("CABLE Output")).unwrap_or(false) {
                out = true;
            }
        }
    }
    inp && out
}

/// 虚拟键盘驱动是否已安装（查 ROOT 枚举节点）
fn has_driver() -> bool {
    use windows::core::PCWSTR;
    use windows::Win32::System::Registry::*;
    fn wide(s: &str) -> Vec<u16> {
        s.encode_utf16().chain(std::iter::once(0)).collect()
    }
    unsafe {
        let path = wide(r"SYSTEM\CurrentControlSet\Enum\ROOT");
        let mut h = HKEY::default();
        if RegOpenKeyExW(HKEY_LOCAL_MACHINE, PCWSTR(path.as_ptr()), 0, KEY_READ, &mut h).is_err() {
            return false;
        }
        let mut i = 0u32;
        let mut found = false;
        loop {
            let mut buf = [0u16; 256];
            let mut len = buf.len() as u32;
            if RegEnumKeyExW(
                h,
                i,
                windows::core::PWSTR(buf.as_mut_ptr()),
                &mut len,
                None,
                windows::core::PWSTR::null(),
                None,
                None,
            )
            .is_err()
            {
                break;
            }
            let name = String::from_utf16_lossy(&buf[..len as usize]).to_uppercase();
            if name.contains("WINUHID") {
                found = true;
                break;
            }
            i += 1;
            if i > 300 {
                break;
            }
        }
        let _ = RegCloseKey(h);
        found
    }
}

/// 静默安装的日志（windows_subsystem="windows" 下控制台输出全部丢失，必须落文件）
fn slog(msg: &str) {
    use std::io::Write as _;
    let p = std::env::temp_dir().join("iprc-setup.log");
    if let Ok(mut f) = std::fs::OpenOptions::new().create(true).append(true).open(p) {
        let _ = writeln!(f, "{msg}");
    }
}

/// 静默安装（无界面）
fn silent_install(args: &[String]) {
    let st = Setup::new();
    let mut dir = PathBuf::from(st.dir.clone());
    let mut autostart = false;
    let mut desk = true;
    for a in args {
        if let Some(d) = a.strip_prefix("/D=") {
            dir = PathBuf::from(d);
        }
        if a.eq_ignore_ascii_case("/AUTOSTART") {
            autostart = true;
        }
        if a.eq_ignore_ascii_case("/NODESKTOP") {
            desk = false;
        }
    }
    slog(&format!("[安装] 静默安装 → {}", dir.display()));
    let r = (|| -> Result<(), String> {
        extract(&dir, |x| slog(&x))?;
        write_settings(&dir, &st.ime, autostart)?;
        slog(&format!(
            "[安装] 已写入配置（输入法：{}）",
            if st.ime.is_empty() { "默认豆包" } else { st.ime.as_str() }
        ));
        if autostart {
            set_autostart(&dir, true)?;
            slog("[安装] 已设置开机自启");
        }
        if desk {
            match make_shortcut(&dir) {
                Ok(_) => slog("[安装] 已创建桌面快捷方式"),
                Err(e) => slog(&format!("[安装] 快捷方式失败：{e}")),
            }
        }
        // ★★ 与界面路径同一套：依赖组件统一交给主程序的 `--install`
        //   （同一个动作只能一套实现）。它做 4 项并逐项复检：
        //   声卡（含 Problem 健康码）/ 键盘驱动 / 遥控器改造 / 默认麦克风。
        run_app_install(&dir, |x| slog(&x));
        slog("[安装] 完成。");
        Ok(())
    })();
    match r {
        Ok(()) => {
            let exe = dir.join(APP_EXE);
            if exe.exists() {
                let _ = std::process::Command::new(exe).spawn();
                slog("[安装] 已启动程序");
            }
            std::process::exit(0);
        }
        Err(e) => {
            slog(&format!("[安装] 失败：{e}"));
            std::process::exit(1);
        }
    }
}

/// 用系统对话框选目录（用 PowerShell 的 FolderBrowserDialog，省得引 rfd 依赖）
fn rfd_pick_dir(cur: &str) -> Option<String> {
    let shell = format!("{}{}", "power", "shell.exe");
    let script = format!(
        "Add-Type -AssemblyName System.Windows.Forms; \
         $d=New-Object System.Windows.Forms.FolderBrowserDialog; \
         $d.SelectedPath='{}'; \
         if($d.ShowDialog() -eq 'OK'){{ Write-Output $d.SelectedPath }}",
        cur.replace("'", "''")
    );
    let out = std::process::Command::new(&shell)
        .args(["-NoProfile", "-STA", "-Command", &script])
        .output()
        .ok()?;
    let s = String::from_utf8_lossy(&out.stdout).trim().to_string();
    (!s.is_empty()).then_some(s)
}

fn open_url(u: &str) {
    let _ = std::process::Command::new("cmd")
        .args(["/C", "start", "", u])
        .spawn();
}

/// 让"未使用"的告警安静：Write 目前没用上，但保留便于将来写日志
#[allow(dead_code)]
fn _unused(mut w: impl Write) {
    let _ = w.flush();
}
