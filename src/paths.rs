//! 统一的应用目录（2026-11-06）
//!
//! ## 为什么要有这个模块
//!
//! 客户反馈两条：
//! 1. **安装文件、配置、日志散落在不同文件夹**，卸载/搬家/发给别人排查都要到处找；
//! 2. `%LOCALAPPDATA%\2655AI\BridgeAudio\XiaomiRemoteBridge` 这种路径
//!    在资源管理器里看到完全不知道是什么（那是另一个项目的遗留名）。
//!
//! 现在所有写盘的点只认这里：
//!
//! ```text
//! <应用目录>\                 ← 安装器装的那一层（IPRC1000-Native.exe 所在）
//! ├── IPRC1000-Native.exe
//! ├── WinUHid.dll / assets\
//! ├── data\                  ← 配置（native.json / shortcuts.json / macros… / audio\）
//! └── logs\                  ← 日志（native.log / bridge.log …）
//! ```
//!
//! ## 位置优先级
//!
//! 1. 环境变量 `IPRC1000_HOME`（自测 / 多实例隔离）
//! 2. **exe 所在目录**（安装器默认装 `D:\IPRC1000-Native`，可写）
//! 3. `%APPDATA%\IPRC1000 Adapter`（装在 Program Files 等只读位置时回退）
//!
//! 判"能不能写"是**实际写一个探针文件**，不是看权限位 —— 权限位在 UAC 虚拟化
//! 和网络盘上不可靠。
//!
//! ## 老配置迁移
//!
//! `migrate_legacy()` 在启动早期调用一次：新位置没有 `data\native.json` 而旧的
//! `%APPDATA%\IPRC1000 Adapter\native.json` 有时，把配置**复制**过去（不删原件，
//! 万一用户要回退老版本）。日志不搬 —— 历史日志没有迁移价值。

use std::path::{Path, PathBuf};
use std::sync::OnceLock;

static HOME: OnceLock<PathBuf> = OnceLock::new();

/// 真正写一个文件来判断可写（权限位不可靠）
fn writable(dir: &Path) -> bool {
    if std::fs::create_dir_all(dir).is_err() {
        return false;
    }
    let probe = dir.join(".iprc-wtest");
    if std::fs::write(&probe, b"1").is_err() {
        return false;
    }
    let _ = std::fs::remove_file(&probe);
    true
}

/// 应用根目录（exe 所在层，或回退到 %APPDATA%）
pub fn home() -> PathBuf {
    HOME.get_or_init(|| {
        if let Ok(v) = std::env::var("IPRC1000_HOME") {
            let p = PathBuf::from(v);
            let _ = std::fs::create_dir_all(&p);
            return p;
        }
        if let Ok(exe) = std::env::current_exe() {
            if let Some(d) = exe.parent() {
                if writable(d) {
                    return d.to_path_buf();
                }
            }
        }
        let base = std::env::var("APPDATA").unwrap_or_else(|_| String::from("."));
        let p = PathBuf::from(base).join("IPRC1000 Adapter");
        let _ = std::fs::create_dir_all(&p);
        p
    })
    .clone()
}

/// 配置目录：`<home>\data`
pub fn data_dir() -> PathBuf {
    let d = home().join("data");
    let _ = std::fs::create_dir_all(&d);
    d
}

/// 日志目录：`<home>\logs`
pub fn logs_dir() -> PathBuf {
    let d = home().join("logs");
    let _ = std::fs::create_dir_all(&d);
    d
}

/// 老位置（仅用于迁移与"优先读旧配置"的兼容）
pub fn legacy_dir() -> PathBuf {
    let base = std::env::var("APPDATA").unwrap_or_else(|_| String::from("."));
    PathBuf::from(base).join("IPRC1000 Adapter")
}

/// 把老配置搬到新位置（只在"新位置还没有配置"时做一次）。
pub fn migrate_legacy() {
    let new_dir = data_dir();
    let old_dir = legacy_dir();
    if new_dir == old_dir {
        return; // 本来就是回退模式，没有搬家这回事
    }
    if new_dir.join("native.json").exists() {
        return; // 新位置已有配置，不覆盖
    }
    if !old_dir.join("native.json").exists() {
        return; // 全新机器，没什么可搬
    }
    // ★ 白名单，不是"所有 json"：旧目录里还躺着 C# 版的 settings.json、keymap.json，
    //   那是另一套程序的配置，搬过来只会让新装目录多出几个"不知道是什么"的文件
    //   （这正是客户抱怨的那类问题）。
    const KEEP: [&str; 5] = [
        "native.json",
        "native-macros.json",
        "native-text.json",
        "shortcuts.json",
        "mouse.json",
    ];
    let mut moved = 0usize;
    for name in KEEP {
        let src = old_dir.join(name);
        if !src.exists() {
            continue;
        }
        let dst = new_dir.join(name);
        if !dst.exists() && std::fs::copy(&src, &dst).is_ok() {
            moved += 1;
        }
    }
    if moved > 0 {
        eprintln!("[迁移] 已把 {moved} 个配置文件从 {} 搬到 {}", old_dir.display(), new_dir.display());
    }
}
