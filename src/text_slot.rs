//! 文本槽（1..10）：一段文字，绑到某个遥控器键上，按下就粘贴/打出。
//!
//! 与 C# 版同语义：槽 1..10 → 绑定引用值 -1001..-1010。
//! 存 `native-text.json`，段落内容不进 getState（每 2 秒传一遍没意义）。

use std::path::PathBuf;
use std::sync::{Mutex, OnceLock};

#[derive(Clone, Default)]
pub struct Slot {
    pub name: String,
    pub text: String,
}

fn path() -> PathBuf {
    crate::paths::data_dir().join("native-text.json")
}

fn store() -> &'static Mutex<std::collections::HashMap<u32, Slot>> {
    static S: OnceLock<Mutex<std::collections::HashMap<u32, Slot>>> = OnceLock::new();
    S.get_or_init(|| Mutex::new(std::collections::HashMap::new()))
}

pub fn load() {
    let Ok(txt) = std::fs::read_to_string(path()) else { return };
    let Ok(v) = serde_json::from_str::<serde_json::Value>(&txt) else { return };
    let Some(obj) = v["slots"].as_object() else { return };
    let mut g = match store().lock() { Ok(g) => g, Err(_) => return };
    g.clear();
    for (k, val) in obj {
        if let Ok(n) = k.parse::<u32>() {
            g.insert(n, Slot {
                name: val["name"].as_str().unwrap_or("").to_string(),
                text: val["text"].as_str().unwrap_or("").to_string(),
            });
        }
    }
}

fn flush(g: &std::collections::HashMap<u32, Slot>) {
    let p = path();
    if let Some(d) = p.parent() { let _ = std::fs::create_dir_all(d); }
    let mut obj = serde_json::Map::new();
    for (k, v) in g {
        obj.insert(k.to_string(), serde_json::json!({ "name": v.name, "text": v.text }));
    }
    let _ = std::fs::write(&p, serde_json::to_string_pretty(&serde_json::json!({ "slots": obj })).unwrap_or_default());
}

pub fn get(slot: u32) -> Option<Slot> {
    store().lock().ok().and_then(|g| g.get(&slot).cloned())
}

pub fn set(slot: u32, name: &str, text: &str) {
    if let Ok(mut g) = store().lock() {
        g.insert(slot, Slot { name: name.to_string(), text: text.to_string() });
        flush(&g);
    }
}

/// 界面列表用（哪个槽有内容）
#[allow(dead_code)] // 未接 UI：列表渲染入口
pub fn list_for_ui() -> Vec<serde_json::Value> {
    let g = match store().lock() { Ok(g) => g, Err(_) => return Vec::new() };
    let mut out: Vec<serde_json::Value> = g
        .iter()
        .filter(|(_, s)| !s.text.is_empty())
        .map(|(n, s)| serde_json::json!({
            "num": n, "kind": "text", "name": s.name, "len": s.text.chars().count(),
            "ref": -1000 - *n as i64,
        }))
        .collect();
    out.sort_by_key(|v| v["num"].as_u64().unwrap_or(0));
    out
}
