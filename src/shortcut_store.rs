//! 「按键 → 打开程序」的快捷方式表（2026-11-05）
//!
//! 需求：按遥控器上某个键直接打开一个程序（微信 / WorkBuddy / 任意 exe、文件夹、网址）。
//!
//! ## 为什么单独一张表，而不是把路径塞进绑定值
//!
//! 绑定值（`settings.groups[n].bindings`）现在是**纯数字编码**：
//!   · 正数/逗号分隔 = VK 序列
//!   · -1..-6        = 鼠标动作
//!   · -1001..-1010  = 文本槽
//!   · -1101..-1999  = 宏
//! 路径里带空格、中文、冒号，塞进这个字段会让两种界面（egui / Win32）的解析都要改，
//! 还会污染"值是否为数字"的既有判断。所以沿用**宏的做法**：
//! 建一张编号表，绑定里只存 `-2000 - 编号`。
//!
//! 存储：`%APPDATA%\IPRC1000 Adapter\shortcuts.json`
//! 结构：`[{"name":"微信","path":"C:\\...\\WeChat.exe","args":""}, ...]`
//! 编号 = 数组下标 + 1（与宏的 1 起编号一致）。
//!
//! 启动用 `ShellExecuteW("open")`：exe / 文件夹 / .lnk / 网址都能开，
//! 且比 CreateProcess 更贴近"用户双击"的行为（会走文件关联与提权提示）。

use serde::{Deserialize, Serialize};
use std::path::PathBuf;

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
pub struct Shortcut {
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub path: String,
    #[serde(default)]
    pub args: String,
}

fn file() -> PathBuf {
    let base = std::env::var("APPDATA").unwrap_or_default();
    PathBuf::from(base)
        .join("IPRC1000 Adapter")
        .join("shortcuts.json")
}

/// 全部条目（下标 0 = 编号 1）
pub fn list() -> Vec<Shortcut> {
    let p = file();
    let Ok(txt) = std::fs::read_to_string(&p) else {
        return Vec::new();
    };
    serde_json::from_str::<Vec<Shortcut>>(&txt).unwrap_or_default()
}

fn write(all: &[Shortcut]) -> Result<(), String> {
    let p = file();
    if let Some(d) = p.parent() {
        let _ = std::fs::create_dir_all(d);
    }
    let txt = serde_json::to_string_pretty(all).map_err(|e| e.to_string())?;
    std::fs::write(&p, txt).map_err(|e| e.to_string())
}

/// 编号（1 起）取条目
pub fn get(num: u32) -> Option<Shortcut> {
    if num == 0 {
        return None;
    }
    list().get((num - 1) as usize).cloned()
}

/// 存一条：`num == 0` 表示新增，返回实际编号
pub fn save(num: u32, name: &str, path: &str, args: &str) -> Result<u32, String> {
    let mut all = list();
    let item = Shortcut {
        name: name.trim().to_string(),
        path: path.trim().to_string(),
        args: args.trim().to_string(),
    };
    if item.path.is_empty() {
        return Err("路径不能为空".into());
    }
    if num == 0 || num as usize > all.len() {
        all.push(item);
        let n = all.len() as u32;
        write(&all)?;
        Ok(n)
    } else {
        all[(num - 1) as usize] = item;
        write(&all)?;
        Ok(num)
    }
}

pub fn delete(num: u32) -> Result<(), String> {
    let mut all = list();
    if num == 0 || num as usize > all.len() {
        return Err("编号不存在".into());
    }
    all.remove((num - 1) as usize);
    write(&all)
}

/// 按键动作值 → 编号（`-2001` ⇒ 1）。非本段返回 None。
pub fn num_of_action(act: i64) -> Option<u32> {
    if (-2999..=-2001).contains(&act) {
        Some((-2000 - act) as u32)
    } else {
        None
    }
}

/// 编号 → 动作值（1 ⇒ `-2001`）
pub fn action_of_num(num: u32) -> i64 {
    -2000 - num as i64
}

/// 启动。返回 Err 供界面显示（不 panic、不阻塞按键线程 —— 调用方应放子线程）。
pub fn launch(num: u32) -> Result<(), String> {
    use windows::core::{HSTRING, PCWSTR};
    use windows::Win32::UI::Shell::ShellExecuteW;
    use windows::Win32::UI::WindowsAndMessaging::SW_SHOWNORMAL;

    let Some(s) = get(num) else {
        return Err(format!("快捷方式 #{num} 不存在"));
    };
    if s.path.trim().is_empty() {
        return Err(format!("快捷方式 #{num} 没有路径"));
    }
    let verb = HSTRING::from("open");
    let path = HSTRING::from(s.path.as_str());
    let args = if s.args.trim().is_empty() {
        HSTRING::from("")
    } else {
        HSTRING::from(s.args.as_str())
    };
    unsafe {
        let h = ShellExecuteW(
            None,
            PCWSTR(verb.as_ptr()),
            PCWSTR(path.as_ptr()),
            PCWSTR(args.as_ptr()),
            PCWSTR::null(),
            SW_SHOWNORMAL,
        );
        // ShellExecuteW 返回值 > 32 才算成功（<=32 是错误码，历史遗留约定）
        let rc = h.0 as isize;
        if rc <= 32 {
            return Err(format!("打开失败（ShellExecute 返回 {rc}）：{}", s.path));
        }
    }
    Ok(())
}

/// 给界面用的一行描述
pub fn label(num: u32) -> String {
    match get(num) {
        Some(s) => {
            let n = if s.name.trim().is_empty() {
                std::path::Path::new(&s.path)
                    .file_name()
                    .map(|x| x.to_string_lossy().to_string())
                    .unwrap_or_else(|| s.path.clone())
            } else {
                s.name.clone()
            };
            format!("打开 {n}")
        }
        None => format!("打开程序 #{num}（未配置）"),
    }
}
