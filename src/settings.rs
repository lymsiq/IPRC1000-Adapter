//! 原生内核的设置（2026-10-31）
//!
//! 与 C# 版的 `settings.json` **分文件**（`native.json`）：两边字段完全不同，
//! 共用一个文件会互相覆盖。放同一个目录（`%APPDATA%\IPRC1000 Adapter`）。
//!
//! 配置组模型：
//!   · `groups[0]` = 默认配置组（**不可删**，删了就没有兜底映射了）；
//!   · 任何一个 `kind == 1` 的组 = 鼠标模式（方向键交给移动引擎，不可删）；
//!   · 其余是用户自建组，可增可删可改名。
//!   出厂固定这两组，是为了让"鼠标模式"有一个稳定的语义锚点（`is_mouse_active()`）。

use std::collections::HashMap;
use std::path::PathBuf;
use std::sync::atomic::{AtomicU8, Ordering};
use std::sync::{Mutex, OnceLock};

/// 免锁副本：按键处理每帧都要问"现在该发什么键"，走 Mutex 会拖慢注入路径。
/// 0 = 豆包, 1 = 微信
pub static IME_IDX: AtomicU8 = AtomicU8::new(0);
/// 0 = 按住, 1 = 点按
pub static TRIGGER_IDX: AtomicU8 = AtomicU8::new(0);

/// 一个配置组
#[derive(Clone, Debug)]
pub struct Group {
    pub name: String,
    /// 按键 id → 绑定值（"38" / "17,91" / "-1" 鼠标动作 / 空串 = 禁用）
    pub bindings: HashMap<String, String>,
    /// 0 = 普通组，1 = 鼠标模式（方向键驱动光标）
    pub kind: u8,
}

impl Group {
    fn normal(name: &str) -> Self {
        Self { name: name.to_string(), bindings: HashMap::new(), kind: 0 }
    }
}

#[derive(Clone, Debug)]
pub struct Data {
    pub ime: String,
    pub trigger: String,
    pub preset: u8,
    pub log_enabled: bool,
    pub active_group: u8,
    pub groups: Vec<Group>,
}

impl Default for Data {
    fn default() -> Self {
        Self {
            ime: "doubao".into(),
            trigger: "hold".into(),
            preset: 1,
            log_enabled: true,
            active_group: 0,
            groups: vec![
                Group::normal("默认配置组 1"),
                Group { name: "鼠标模式".into(), bindings: HashMap::new(), kind: 1 },
            ],
        }
    }
}

fn store() -> &'static Mutex<Data> {
    static S: OnceLock<Mutex<Data>> = OnceLock::new();
    S.get_or_init(|| Mutex::new(Data::default()))
}

fn path() -> PathBuf {
    let base = std::env::var("APPDATA").unwrap_or_else(|_| ".".to_string());
    PathBuf::from(base).join("IPRC1000 Adapter").join("native.json")
}

fn parse_map(v: &serde_json::Value) -> HashMap<String, String> {
    let mut m = HashMap::new();
    if let Some(obj) = v.as_object() {
        for (k, val) in obj {
            if let Some(s) = val.as_str() {
                m.insert(k.clone(), s.to_string());
            }
        }
    }
    m
}

/// 启动时读磁盘（兼容旧的"两个字段"格式）
pub fn load() {
    let Ok(txt) = std::fs::read_to_string(path()) else { return };
    let Ok(v) = serde_json::from_str::<serde_json::Value>(&txt) else { return };
    // 配置格式版本（0 = 老文件，没有这个键）
    let schema = v.get("schemaVersion").and_then(|x| x.as_u64()).unwrap_or(0) as u32;
    if schema > 2 {
        eprintln!(
            "[!] 配置文件版本 {schema} 高于本程序支持的 2 —— 可能来自更新版本，未知字段会被忽略"
        );
    }
    let mut d = match store().lock() {
        Ok(g) => g,
        Err(_) => return,
    };
    if let Some(s) = v["ime"].as_str() {
        if s == "doubao" || s == "wechat" {
            d.ime = s.to_string();
        }
    }
    if let Some(s) = v["trigger"].as_str() {
        if s == "hold" || s == "tap" {
            d.trigger = s.to_string();
        }
    }
    if let Some(n) = v["preset"].as_u64() {
        d.preset = n.min(2) as u8;
    }
    if let Some(b) = v["logEnabled"].as_bool() {
        d.log_enabled = b;
    }
    if let Some(arr) = v["groups"].as_array() {
        if !arr.is_empty() {
            let mut gs: Vec<Group> = arr
                .iter()
                .map(|g| Group {
                    name: g["name"].as_str().unwrap_or("配置组").to_string(),
                    bindings: parse_map(&g["bindings"]),
                    kind: g["kind"].as_u64().unwrap_or(0) as u8,
                })
                .collect();
            // 保证永远有一组普通组可兜底
            if !gs.iter().any(|g| g.kind == 0) {
                gs.insert(0, Group::normal("默认配置组 1"));
            }
            d.groups = gs;
        }
    } else {
        // 旧格式迁移：bindings（组 0）+ bindingsG1（组 1）
        d.groups[0].bindings = parse_map(&v["bindings"]);
        d.groups[1].bindings = parse_map(&v["bindingsG1"]);
    }
    if let Some(n) = v["activeGroup"].as_u64() {
        let n = n as usize;
        d.active_group = if n < d.groups.len() { n as u8 } else { 0 };
    }
    IME_IDX.store(if d.ime == "wechat" { 1 } else { 0 }, Ordering::Relaxed);
    TRIGGER_IDX.store(if d.trigger == "tap" { 1 } else { 0 }, Ordering::Relaxed);
}

fn save(d: &Data) {
    let p = path();
    if let Some(dir) = p.parent() {
        let _ = std::fs::create_dir_all(dir);
    }
    let groups: Vec<serde_json::Value> = d
        .groups
        .iter()
        .map(|g| serde_json::json!({
            "name": g.name, "bindings": g.bindings, "kind": g.kind
        }))
        .collect();
    let v = serde_json::json!({
        // ★ 格式版本：将来字段语义变更时靠它判断要不要迁移。
        //   老文件没有这个键 ⇒ 读出来是 null ⇒ 视为 v1（当前读法兼容 v1，无需迁移）。
        "schemaVersion": 2,
        "ime": d.ime, "trigger": d.trigger, "preset": d.preset,
        "logEnabled": d.log_enabled,
        "activeGroup": d.active_group,
        "groups": groups,
        "_comment": "IPRC1000 原生内核设置（与 C# 版 settings.json 互不影响）"
    });
    let _ = std::fs::write(&p, serde_json::to_string_pretty(&v).unwrap_or_default());
}

pub fn ime() -> String {
    store().lock().map(|d| d.ime.clone()).unwrap_or_else(|_| "doubao".into())
}

pub fn set_ime(v: &str) -> bool {
    if v != "doubao" && v != "wechat" {
        return false;
    }
    if let Ok(mut d) = store().lock() {
        d.ime = v.to_string();
        IME_IDX.store(if v == "wechat" { 1 } else { 0 }, Ordering::Relaxed);
        save(&d);
        return true;
    }
    false
}

pub fn trigger() -> String {
    store().lock().map(|d| d.trigger.clone()).unwrap_or_else(|_| "hold".into())
}

pub fn set_trigger(v: &str) -> bool {
    if v != "hold" && v != "tap" {
        return false;
    }
    if let Ok(mut d) = store().lock() {
        d.trigger = v.to_string();
        TRIGGER_IDX.store(if v == "tap" { 1 } else { 0 }, Ordering::Relaxed);
        save(&d);
        return true;
    }
    false
}

pub fn preset() -> u8 {
    store().lock().map(|d| d.preset).unwrap_or(1)
}

pub fn set_preset(v: u8) {
    if let Ok(mut d) = store().lock() {
        d.preset = v.min(2);
        save(&d);
    }
}

#[allow(dead_code)]
pub fn log_enabled() -> bool {
    store().lock().map(|d| d.log_enabled).unwrap_or(true)
}

#[allow(dead_code)]
pub fn set_log_enabled(v: bool) {
    if let Ok(mut d) = store().lock() {
        d.log_enabled = v;
        save(&d);
    }
}

// ── 配置组 ──

pub fn active_group() -> u8 {
    store().lock().map(|d| d.active_group).unwrap_or(0)
}

pub fn set_active_group(g: u8) {
    if let Ok(mut d) = store().lock() {
        d.active_group = if (g as usize) < d.groups.len() { g } else { 0 };
        save(&d);
    }
}

/// 当前生效的组是不是"鼠标模式"
pub fn is_mouse_active() -> bool {
    store()
        .lock()
        .map(|d| {
            d.groups
                .get(d.active_group as usize)
                .map(|g| g.kind == 1)
                .unwrap_or(false)
        })
        .unwrap_or(false)
}

pub fn bindings() -> HashMap<String, String> {
    bindings_of(active_group())
}

pub fn bindings_of(group: u8) -> HashMap<String, String> {
    match store().lock() {
        Ok(d) => d
            .groups
            .get(group as usize)
            .map(|g| g.bindings.clone())
            .unwrap_or_default(),
        Err(_) => HashMap::new(),
    }
}

/// 往**当前组**写一个绑定并落盘
pub fn set_binding_in_group(id: &str, value: &str) {
    if let Ok(mut d) = store().lock() {
        let idx = d.active_group as usize;
        if let Some(g) = d.groups.get_mut(idx) {
            g.bindings.insert(id.to_string(), value.to_string());
            save(&d);
        }
    }
}

/// 界面用的组列表
pub fn groups_ui() -> Vec<serde_json::Value> {
    let d = match store().lock() {
        Ok(g) => g,
        Err(_) => return Vec::new(),
    };
    d.groups
        .iter()
        .enumerate()
        .map(|(i, g)| {
            serde_json::json!({
                "id": i,
                "name": g.name,
                "count": if g.kind == 1 { 19 } else { crate::keymap::PLAIN.len() },
                "kind": g.kind,
                "configured": g.bindings.values().filter(|v| !v.is_empty()).count(),
            })
        })
        .collect()
}

/// 新建一个普通组，返回新组 id
#[allow(dead_code)]
pub fn new_group(name: &str) -> Result<u8, String> {
    let mut d = store().lock().map_err(|_| "存储忙".to_string())?;
    if d.groups.len() >= 16 {
        return Err(String::from("最多 16 个配置组"));
    }
    let nm = if name.trim().is_empty() {
        format!("配置组 {}", d.groups.len() + 1)
    } else {
        name.trim().to_string()
    };
    d.groups.push(Group::normal(&nm));
    let id = (d.groups.len() - 1) as u8;
    save(&d);
    Ok(id)
}

/// 删除一个组（默认组与鼠标模式组不可删）
#[allow(dead_code)]
pub fn delete_group(id: u8) -> Result<(), String> {
    let mut d = store().lock().map_err(|_| "存储忙".to_string())?;
    let idx = id as usize;
    let Some(g) = d.groups.get(idx) else { return Err(String::from("组不存在")) };
    if g.kind == 1 {
        return Err(String::from("鼠标模式组是固定的，不能删除"));
    }
    if idx == 0 {
        return Err(String::from("默认配置组不能删除（它是兜底映射）"));
    }
    d.groups.remove(idx);
    if d.active_group as usize >= d.groups.len() {
        d.active_group = 0;
    }
    save(&d);
    Ok(())
}

#[allow(dead_code)]
pub fn rename_group(id: u8, name: &str) -> Result<(), String> {
    let mut d = store().lock().map_err(|_| "存储忙".to_string())?;
    let idx = id as usize;
    let Some(g) = d.groups.get_mut(idx) else { return Err(String::from("组不存在")) };
    if name.trim().is_empty() {
        return Err(String::from("名字不能为空"));
    }
    g.name = name.trim().to_string();
    save(&d);
    Ok(())
}

/// 鼠标模式组的出厂默认：OK=左键、返回=右键、频道±=滚轮。
///
/// 方向键**不绑** —— 它们由移动引擎直接接管（`is_mouse_active()` 为真时，
/// `keymap::vks_of` 对上下左右直接返回空）。
pub fn ensure_mouse_defaults() {
    let need = {
        let d = match store().lock() {
            Ok(g) => g,
            Err(_) => return,
        };
        d.groups
            .iter()
            .find(|g| g.kind == 1)
            .map(|g| g.bindings.is_empty())
            .unwrap_or(false)
    };
    if !need {
        return;
    }
    if let Ok(mut d) = store().lock() {
        if let Some(g) = d.groups.iter_mut().find(|g| g.kind == 1) {
            g.bindings.insert("ok".into(), "-1".into());
            g.bindings.insert("back".into(), "-2".into());
            g.bindings.insert("channel_up".into(), "-3".into());
            g.bindings.insert("channel_down".into(), "-4".into());
        }
        save(&d);
        eprintln!("[i] 鼠标模式组已填入出厂默认（OK=左键 / 返回=右键 / 频道±=滚轮）");
    }
}
