//! 卸载（2026-10-31）
//!
//! 绿色版没有安装程序，卸载由程序自己做。**不依赖任何外部脚本**。
//!
//! 核心思路：Windows 不允许删除**正在运行**的 exe，但**允许重命名它**。
//! 所以：
//!   1. 把 exe 重命名到**同一卷的父目录**（跨卷 rename 会失败，所以不能扔到别的盘）；
//!   2. 此时安装目录里已经没有 exe，`remove_dir_all` 就能删掉其余文件；
//!   3. 被挪走的那份用 `MoveFileEx(..., MOVEFILE_DELAY_UNTIL_REBOOT)` 标记"重启后删除"。
//!
//! 这条路走通之前试过并失败的方案（记录下来免得重犯）：
//!   · `.bat` + `%~1` 传路径 → `cmd /C start` 把参数吃掉，`rmdir` 空跑；
//!   · PowerShell `-File x.ps1 -Dir "路径"` → 参数没传进去；
//!   · PowerShell `-File` + 路径写进脚本 + `CREATE_NO_WINDOW` → 脚本压根没执行；
//!   · `.bat` 路径硬编码 + `rmdir` → 脚本自删了（说明跑到底），但 `rmdir` 从未生效、连日志都没写。
//!   外部脚本在"父进程即将退出"的时机下太不可控，纯 Rust 反而最稳。

use std::path::Path;

const APP_EXE: &str = "IPRC1000-Native.exe";

/// 执行卸载。成功返回后调用方应立刻 `std::process::exit(0)`。
pub fn uninstall(install_dir: &Path) -> Result<(), String> {
    let exe = install_dir.join(APP_EXE);

    // ⓪ 先把依赖组件卸掉（此时 assets 还在，正好用得上安装包）
    remove_cable(install_dir);
    remove_winuhid();

    // ① 把 exe 挪到父目录（同卷重命名，允许正在运行）
    let trashed = match install_dir.parent() {
        Some(p) => p.join("_IPRC1000_uninstalling.exe"),
        None => return Err(String::from("无法确定父目录")),
    };
    let _ = std::fs::remove_file(&trashed);
    if exe.exists() {
        std::fs::rename(&exe, &trashed).map_err(|e| format!("无法移动程序文件：{e}"))?;
    }

    // ② 删安装目录（此时里面已无 exe；可能还有被本进程加载的 DLL，允许部分失败）
    let mut last_err = String::new();
    for _ in 0..10 {
        if !install_dir.exists() {
            break;
        }
        match std::fs::remove_dir_all(install_dir) {
            Ok(()) => break,
            Err(e) => {
                last_err = e.to_string();
                std::thread::sleep(std::time::Duration::from_millis(300));
            }
        }
    }

    // ③ 桌面快捷方式（当前用户 + 公共）
    for env in ["USERPROFILE", "PUBLIC"] {
        if let Ok(base) = std::env::var(env) {
            let desk = Path::new(&base).join("Desktop");
            if let Ok(rd) = std::fs::read_dir(&desk) {
                for e in rd.flatten() {
                    let n = e.file_name().to_string_lossy().to_string();
                    if n.starts_with("IPRC1000") && n.ends_with(".lnk") {
                        let _ = std::fs::remove_file(e.path());
                    }
                }
            }
        }
    }

    // ④ 开机自启
    remove_autostart();

    // ⑤ 被挪走的 exe：标记为重启后删除（它自己还在运行，现在删不掉）
    schedule_delete_on_reboot(&trashed);

    if install_dir.exists() {
        // 目录没删干净（多半是 DLL 仍被本进程占用）→ 交给重启后清理
        schedule_delete_on_reboot(install_dir);
        return Err(format!(
            "安装目录未能完全删除（{last_err}）—— 已标记为重启后自动清理"
        ));
    }
    Ok(())
}

/// 卸载 VB-CABLE 虚拟声卡（用安装包自带的卸载参数 `-u`）
fn remove_cable(install_dir: &Path) {
    let zip_p = install_dir.join("assets").join("VBCABLE_Driver_Pack45.zip");
    if !zip_p.exists() {
        return;
    }
    let tmp = std::env::temp_dir().join("iprc-vbcable-un");
    let _ = std::fs::remove_dir_all(&tmp);
    let _ = std::fs::create_dir_all(&tmp);
    if let Ok(bytes) = std::fs::read(&zip_p) {
        if let Ok(mut ar) = zip::ZipArchive::new(std::io::Cursor::new(bytes)) {
            for i in 0..ar.len() {
                if let Ok(mut f) = ar.by_index(i) {
                    if let Some(n) = f.enclosed_name() {
                        let out = tmp.join(n);
                        if let Some(p) = out.parent() {
                            let _ = std::fs::create_dir_all(p);
                        }
                        if let Ok(mut w) = std::fs::File::create(&out) {
                            let _ = std::io::copy(&mut f, &mut w);
                        }
                    }
                }
            }
        }
    }
    for exe in ["VBCABLE_Setup_x64.exe", "VBCABLE_Setup.exe"] {
        let p = tmp.join(exe);
        if p.exists() {
            // `-u` 卸载、`-h` 隐藏界面
            let _ = std::process::Command::new(&p).args(["-u", "-h"]).status();
            eprintln!("[卸载] 已卸载 VB-CABLE 虚拟声卡");
            break;
        }
    }
    let _ = std::fs::remove_dir_all(&tmp);
}

/// 卸载 WinUHid 驱动（pnputil 找出它的 oem 编号再删）
fn remove_winuhid() {
    let out = std::process::Command::new("pnputil.exe")
        .args(["/enum-drivers"])
        .output();
    let Ok(o) = out else { return };
    let text = String::from_utf8_lossy(&o.stdout).to_string();
    // pnputil 输出按块分隔，每块有 "发布名称: oemXX.inf" 和 "原始名称: ...WinUHid..."
    let mut current: Option<String> = None;
    let mut targets: Vec<String> = Vec::new();
    for line in text.lines() {
        let l = line.trim();
        if let Some(rest) = l.split(":").nth(1) {
            if l.contains("发布名称") || l.to_lowercase().contains("published name") {
                current = Some(rest.trim().to_string());
            }
        }
        if l.to_lowercase().contains("winuhid") {
            if let Some(c) = &current {
                if c.to_lowercase().ends_with(".inf") {
                    targets.push(c.clone());
                }
            }
        }
    }
    targets.sort();
    targets.dedup();
    for t in &targets {
        let r = std::process::Command::new("pnputil.exe")
            .args(["/delete-driver", t, "/uninstall", "/force"])
            .output();
        match r {
            Ok(x) if x.status.success() => eprintln!("[卸载] 已删除驱动包 {t}"),
            _ => eprintln!("[卸载] 删除驱动包 {t} 失败（可能没管理员权限）"),
        }
    }
    if targets.is_empty() {
        eprintln!("[卸载] 未发现 WinUHid 驱动包（可能本来就没装）");
    }
}

/// 删掉 HKCU Run 里的自启项
fn remove_autostart() {
    use windows::core::PCWSTR;
    use windows::Win32::System::Registry::*;
    fn wide(s: &str) -> Vec<u16> {
        s.encode_utf16().chain(std::iter::once(0)).collect()
    }
    unsafe {
        let path = wide(r"Software\Microsoft\Windows\CurrentVersion\Run");
        let mut h = HKEY::default();
        if RegOpenKeyExW(HKEY_CURRENT_USER, PCWSTR(path.as_ptr()), 0, KEY_SET_VALUE, &mut h).is_ok() {
            let name = wide("IPRC1000Native");
            let _ = RegDeleteValueW(h, PCWSTR(name.as_ptr()));
            let _ = RegCloseKey(h);
        }
    }
}

/// 标记"重启时删除"（文件正被占用时唯一的干净办法）
fn schedule_delete_on_reboot(p: &Path) {
    use windows::core::PCWSTR;
    use windows::Win32::Storage::FileSystem::{MoveFileExW, MOVEFILE_DELAY_UNTIL_REBOOT};
    if !p.exists() {
        return;
    }
    let w: Vec<u16> = p
        .display()
        .to_string()
        .encode_utf16()
        .chain(std::iter::once(0))
        .collect();
    unsafe {
        let _ = MoveFileExW(PCWSTR(w.as_ptr()), PCWSTR::null(), MOVEFILE_DELAY_UNTIL_REBOOT);
    }
}

/// 当前程序所在目录（卸载的目标）
pub fn current_dir() -> Option<std::path::PathBuf> {
    std::env::current_exe().ok()?.parent().map(|p| p.to_path_buf())
}
