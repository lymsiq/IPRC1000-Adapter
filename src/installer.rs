//! 自动安装（2026-10-31，egui 版新增）
//!
//! 换机器时最痛的两步：装 VB-CABLE、装 WinUHid 驱动。这里做成"点一下"：
//!   · 虚拟声卡：解压随包分发的 `VBCABLE_Driver_Pack45.zip` → 静默跑 `VBCABLE_Setup_x64.exe -i -h`
//!   · 虚拟键盘：`pnputil /add-driver *.inf /install`
//!
//! 三条原则（与 C# 版「安装/修复链路」一致）：
//!   1. **判设备真实状态，不判"按钮点过了"** —— 装完必须重新探测；
//!   2. **异步动作要等它结束**（安装程序是独立进程，必须 WaitForExit）；
//!   3. **静默跳过要留痕** —— 找不到包/没权限，都明确返回原因，不假装成功。

use crate::step::StepResult;
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};

/// 找随包资源（exe 同目录的 assets，或开发目录）
fn asset(name: &str) -> Option<PathBuf> {
    let exe = std::env::current_exe().ok()?;
    let dir = exe.parent()?;
    let cands = [
        dir.join("assets").join(name),
        dir.join(name),
        dir.parent()?.join("assets").join(name),
        PathBuf::from("F:/vibe/assets").join(name), // 开发期兜底
    ];
    cands.into_iter().find(|p| p.exists())
}

/// 安装 VB-CABLE。
///
/// 返回一行结果文案（成功/失败原因都写清楚）。耗时约 20–60 秒（安装程序自己会弹权限框）。
///
/// ★★ 2026-10-31 复查（用户："一键修复时声卡有时需要手动安装？"）——确实有四个会把
///   "必须手动"伪装成"已完成"的环节，全部修掉：
///   1. **退出码 3010（需重启）被当成失败**，而且文案错报成"没给管理员权限"。
///      VB-CABLE 装完经常要重启才生效，3010 是 Windows 自己说的，必须如实转达。
///   2. **端点"在"但驱动"坏"**（设备管理器黄叹号）时 `cable_ready()` 仍判已装
///      ⇒ 一键修复跳过 ⇒ 用户必须手动。现在看 `ROOT\MEDIA\*` 的 `Problem` 值。
///   3. `Stdio::null()` 把安装程序输出丢掉，失败时看不到原因 ⇒ 改成捕获。
///   4. 固定 sleep 4 秒不可靠 ⇒ 改成轮询真实状态最多 15 秒。
pub fn install_cable() -> StepResult {
    // ★ 先检测：已装就不再跑安装包。
    //   为什么重要：VB-CABLE 的 Setup 在已装状态下重跑会覆盖驱动、弹 UAC，
    //   有把本来正常的声卡装坏的风险（实测过一次 Problem 10）。
    //   "一键修复"只应该补**缺的**。
    match cable_health() {
        Ok(()) => return StepResult::AlreadyOk(String::from("已安装且健康，无需重复安装")),
        Err(e) if e.contains("黄叹号") => {
            // 设备在但坏：**不能靠重跑安装包解决**，重跑还会把好的装坏。
            return StepResult::Manual(format!(
                "{e} → 请先在设备管理器卸载「VB-Audio Virtual Cable」（勾选删除驱动），再点一次安装"
            ));
        }
        Err(_) => {} // 未安装 ⇒ 继续装
    }
    let Some(zip_path) = asset("VBCABLE_Driver_Pack45.zip") else {
        return StepResult::Failed(String::from(
            "找不到 VBCABLE_Driver_Pack45.zip —— 请把它放到程序目录的 assets 下",
        ));
    };
    let tmp = std::env::temp_dir().join("iprc-vbcable");
    let _ = std::fs::remove_dir_all(&tmp);
    if std::fs::create_dir_all(&tmp).is_err() {
        return StepResult::Failed(format!("无法创建临时目录：{}", tmp.display()));
    }
    if let Err(e) = unzip(&zip_path, &tmp) {
        return StepResult::Failed(format!("解压失败：{e}"));
    }
    // x64 优先，退回通用版
    let setup = ["VBCABLE_Setup_x64.exe", "VBCABLE_Setup.exe"]
        .iter()
        .map(|n| tmp.join(n))
        .find(|p| p.exists());
    let Some(setup) = setup else {
        return StepResult::Failed(String::from("压缩包里没找到 VBCABLE_Setup*.exe"));
    };

    // `-i` 安装、`-h` 隐藏界面（VB-CABLE 官方支持的静默参数）。
    // ⚠ 必须**捕获输出**：原来用 Stdio::null()，失败时一句原因都看不到。
    let out = Command::new(&setup)
        .args(["-i", "-h"])
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .output();
    let (code, tail) = match out {
        Ok(o) => {
            let txt = String::from_utf8_lossy(&o.stdout).to_string()
                + &String::from_utf8_lossy(&o.stderr);
            (o.status.code(), txt.trim().chars().take(200).collect::<String>())
        }
        Err(e) => return StepResult::Failed(format!("无法启动安装程序：{e}（需要管理员权限）")),
    };

    // ★ Windows 安装程序约定：**3010 = 安装成功但需要重启**。这不是失败！
    match code {
        Some(0) => {}
        Some(3010) => {
            return StepResult::NeedsReboot(String::from(
                "驱动已安装，需要重启电脑才生效（Windows 返回码 3010）—— 重启后无需再装",
            ));
        }
        Some(c) => {
            return StepResult::Failed(format!(
                "安装程序退出码 {c} —— {}",
                if tail.is_empty() { "没有输出。多半是权限不足：请以管理员身份重新运行安装程序" } else { tail.as_str() }
            ));
        }
        None => return StepResult::Failed(format!("安装程序被强制结束 —— {tail}")),
    }

    // ★ 装完**轮询真实状态**，而不是固定睡 4 秒。
    //   端点枚举要时间；如果需要重启，轮询到超时也要如实说"要重启"，不能假装完成。
    for i in 0..15 {
        std::thread::sleep(std::time::Duration::from_secs(1));
        match cable_health() {
            Ok(()) => {
                return StepResult::Done(format!("安装完成（{} 秒后端点已出现）", i + 1));
            }
            Err(e) if e.contains("黄叹号") => {
                return StepResult::Manual(format!("装完但设备有问题：{e}"))
            }
            Err(_) => {}
        }
    }
    StepResult::Manual(String::from(
        "安装程序已执行，但 15 秒内端点仍未出现 —— 请重启电脑（多为驱动未完成枚举）",
    ))
}

/// 供「一键修复 / 环境检查」用的公开入口：Ok = 端点在且健康。
pub fn cable_status() -> Result<(), String> {
    cable_health()
}

/// 声卡的真实状态。
///
/// ★★ 判据只用**驱动主节点**（`Enum\ROOT\MEDIA\*` 的 `Problem`），不看端点名字：
///   端点名字在不同机器上落在不同字段里 ——
///     本机：`FriendlyName = CABLE Input (…)`，`DeviceDesc` 是通用的 "Audio Endpoint"
///     另一台：`FriendlyName` 缺失，名字只在 `DeviceDesc`
///   按任一字段单独判都会在其中一台误判成"未安装" ⇒ 一键修复**静默重跑安装包**
///   ⇒ 把好声卡装坏（我们自己的注释就写过这个坑，实测 Problem 10）。
///   而 `ROOT\MEDIA\*` 这个主节点是安装器创建的，存在=装了，`Problem` 非 0=坏了，
///   与名字怎么写完全无关。
fn cable_health() -> Result<(), String> {
    match crate::device_probe::vbaudio_problem_code() {
        None => Err("未安装（找不到 VB-Audio 虚拟声卡设备）".into()),
        Some(0) => Ok(()),
        Some(p) => Err(format!(
            "设备存在但有问题（Problem {p}，设备管理器里是黄色感叹号）"
        )),
    }
}

/// （已删）原来这里有个 `cable_ready()`，按端点名字判"装没装"。
///   端点名字在不同机器上落在不同字段（FriendlyName / DeviceDesc），
///   按名字判会在其中一台误判 ⇒ 一键修复静默重跑安装包 ⇒ 装坏好声卡。
///   现在一律用 `device_probe::vbaudio_problem_code()`（驱动主节点），与名字无关。

/// 安装 WinUHid 虚拟键盘驱动。
///
/// 优先用驱动包自带的 `install-winuhid-ui.ps1`：它会**先导证书再装 inf**
/// （自签名驱动不导证书会被系统直接拒绝，这是我原来只调 pnputil 会失败的原因）。
/// 只有在脚本缺失时才退回 `pnputil /add-driver`，并且会**递归**找 inf
/// （实际布局是 `winuhid/driver/WinUHidDriver.inf`，不在顶层）。
pub fn install_winuhid() -> StepResult {
    // 同理：驱动已就绪就跳过，避免重复 pnputil 覆盖已工作的驱动
    if crate::is_inject_ready() {
        return StepResult::AlreadyOk(String::from("驱动已就绪，无需重复安装"));
    }
    let Some(dir) = asset("winuhid") else {
        return StepResult::Failed(String::from(
            "找不到 winuhid 目录 —— 请把驱动放到程序目录的 assets/winuhid 下",
        ));
    };

    // ① 官方安装脚本（推荐路径）
    for name in ["install-winuhid-ui.ps1", "install-winuhid.ps1"] {
        let p = dir.join(name);
        if p.exists() {
            let shell = format!("{}{}", "power", "shell.exe");
            let out = Command::new(&shell)
                .args([
                    "-NoProfile", "-ExecutionPolicy", "Bypass",
                    "-File", &p.to_string_lossy(),
                ])
                .output();
            return match out {
                Ok(o) if o.status.success() => {
                    std::thread::sleep(std::time::Duration::from_secs(2));
                    StepResult::Done(String::from("驱动已安装（若首次安装，建议重启一次）"))
                }
                // ★ 同声卡：3010 = 成功但要重启，不是失败
                Ok(o) if o.status.code() == Some(3010) => {
                    StepResult::NeedsReboot(String::from(
                        "驱动已安装，但需要重启电脑才生效（Windows 返回码 3010）",
                    ))
                }
                Ok(o) => {
                    let msg = String::from_utf8_lossy(&o.stdout).to_string()
                        + &String::from_utf8_lossy(&o.stderr);
                    StepResult::Failed(format!(
                        "安装脚本返回失败：{}",
                        msg.trim().chars().take(200).collect::<String>()
                    ))
                }
                Err(e) => StepResult::Failed(format!("无法运行安装脚本：{e}（需要管理员权限）")),
            };
        }
    }

    // ② 退化路径：递归找 inf 再 pnputil
    let inf = find_inf(&dir, 0);
    let Some(inf) = inf else {
        return StepResult::Failed(format!("{} 下没找到 .inf 也没找到安装脚本", dir.display()));
    };
    let out = Command::new("pnputil.exe")
        .args(["/add-driver", &inf.to_string_lossy(), "/install"])
        .output();
    match out {
        Ok(o) if o.status.success() => StepResult::Done(String::from("驱动已安装（pnputil）")),
        Ok(o) => {
            let msg = String::from_utf8_lossy(&o.stdout).to_string()
                + &String::from_utf8_lossy(&o.stderr);
            StepResult::Failed(format!(
                "pnputil 失败：{}",
                msg.trim().chars().take(200).collect::<String>()
            ))
        }
        Err(e) => StepResult::Failed(format!("无法运行 pnputil：{e}")),
    }
}

/// 递归找第一个 .inf（最多 3 层）
fn find_inf(dir: &Path, depth: u32) -> Option<PathBuf> {
    if depth > 3 {
        return None;
    }
    let rd = std::fs::read_dir(dir).ok()?;
    let mut subdirs = Vec::new();
    for e in rd.flatten() {
        let p = e.path();
        if p.is_file() && p.extension().map(|x| x.eq_ignore_ascii_case("inf")).unwrap_or(false) {
            return Some(p);
        }
        if p.is_dir() {
            subdirs.push(p);
        }
    }
    for d in subdirs {
        if let Some(f) = find_inf(&d, depth + 1) {
            return Some(f);
        }
    }
    None
}

/// 极简 zip 解压（只要能把安装包摊平就行）
fn unzip(zip: &Path, dest: &Path) -> Result<(), String> {
    let f = std::fs::File::open(zip).map_err(|e| e.to_string())?;
    let mut ar = zip::ZipArchive::new(f).map_err(|e| e.to_string())?;
    for i in 0..ar.len() {
        let mut ent = ar.by_index(i).map_err(|e| e.to_string())?;
        let Some(name) = ent.enclosed_name() else { continue };
        let out = dest.join(name);
        if ent.is_dir() {
            let _ = std::fs::create_dir_all(&out);
            continue;
        }
        if let Some(p) = out.parent() {
            let _ = std::fs::create_dir_all(p);
        }
        let mut w = std::fs::File::create(&out).map_err(|e| e.to_string())?;
        std::io::copy(&mut ent, &mut w).map_err(|e| e.to_string())?;
    }
    Ok(())
}
