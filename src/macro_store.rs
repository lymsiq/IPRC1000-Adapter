//! 宏与文本槽的存储（2026-10-31）
//!
//! 存独立文件 `native-macros.json`（与 C# 版 `macros.json` 分开）：
//! 两边的字段语义有差异，共用一个文件会互相覆盖。
//!
//! **步骤结构刻意不做强类型**：前端已经在维护 `type/vks/sym/holdMs/press/ms/slot/...`
//! 这一套（还有录制、导入、截图点击等分支），后端如果也定义一遍结构，两边一改就漂移。
//! 这里只保证"存进去什么，取出来还是什么"，字段语义由前端负责；播放器按 `type` 解释。

use std::path::PathBuf;
use std::sync::{Mutex, OnceLock};

use serde_json::{json, Value};

/// 一条宏
#[derive(Clone)]
pub struct Macro {
    pub num: u32,
    pub name: String,
    pub steps: Vec<Value>,
    /// 0 = 一直循环（播放器有 10 分钟硬上限）
    pub loop_count: u32,
}

fn path() -> PathBuf {
    let base = std::env::var("APPDATA").unwrap_or_else(|_| ".".to_string());
    PathBuf::from(base).join("IPRC1000 Adapter").join("native-macros.json")
}

fn store() -> &'static Mutex<Vec<Macro>> {
    static M: OnceLock<Mutex<Vec<Macro>>> = OnceLock::new();
    M.get_or_init(|| Mutex::new(Vec::new()))
}

/// 启动时读一次磁盘
pub fn load() {
    let Ok(txt) = std::fs::read_to_string(path()) else { return };
    let Ok(v) = serde_json::from_str::<Value>(&txt) else { return };
    let Some(arr) = v["macros"].as_array() else { return };
    let mut list = Vec::new();
    for m in arr {
        let num = m["num"].as_u64().unwrap_or(0) as u32;
        if num == 0 {
            continue;
        }
        list.push(Macro {
            num,
            name: m["name"].as_str().unwrap_or("").to_string(),
            steps: m["steps"].as_array().cloned().unwrap_or_default(),
            loop_count: m["loop"].as_u64().unwrap_or(1) as u32,
        });
    }
    if let Ok(mut g) = store().lock() {
        *g = list;
    }
}

fn flush(list: &[Macro]) {
    let p = path();
    if let Some(d) = p.parent() {
        let _ = std::fs::create_dir_all(d);
    }
    let arr: Vec<Value> = list
        .iter()
        .map(|m| {
            json!({
                "num": m.num, "name": m.name,
                "steps": m.steps, "loop": m.loop_count,
            })
        })
        .collect();
    let _ = std::fs::write(&p, serde_json::to_string_pretty(&json!({ "macros": arr })).unwrap_or_default());
}

pub fn all() -> Vec<Macro> {
    store().lock().map(|g| g.clone()).unwrap_or_default()
}

pub fn get(num: u32) -> Option<Macro> {
    store().lock().ok().and_then(|g| g.iter().find(|m| m.num == num).cloned())
}

/// 保存：`num == 0` 表示新建（分配 min(1..999) 里第一个空号）。
/// 删除不复用编号 —— 与 C# 版一致，避免"删了再加"把旧绑定指到新宏上。
pub fn save(num: u32, name: &str, steps: Vec<Value>, loop_count: u32) -> Result<u32, String> {
    // ★ 允许 0 步：先建空宏、再一条条插入，比"必须录制一段才能建"顺手得多。
    //   （旧限制是照搬 C# 的保存校验，但那边有完整编辑器可以先加步骤再存。）
    if steps.len() > 50 {
        return Err("一个宏最多 50 步".to_string());
    }
    let mut g = store().lock().map_err(|_| "存储忙".to_string())?;
    let final_num = if num == 0 {
        let used: std::collections::HashSet<u32> = g.iter().map(|m| m.num).collect();
        (1u32..=999).find(|n| !used.contains(n)).ok_or("宏数量已达上限 999")?
    } else {
        num
    };
    if let Some(m) = g.iter_mut().find(|m| m.num == final_num) {
        m.name = name.to_string();
        m.steps = steps;
        m.loop_count = loop_count;
    } else {
        g.push(Macro { num: final_num, name: name.to_string(), steps, loop_count });
    }
    g.sort_by_key(|m| m.num);
    flush(&g);
    Ok(final_num)
}

#[allow(dead_code)]  // 界面暂无"删除宏"入口，保留
pub fn delete(num: u32) {
    if let Ok(mut g) = store().lock() {
        g.retain(|m| m.num != num);
        flush(&g);
    }
}

/// 界面用的列表（`state.macros`）：只给概要，不带 steps（避免每 2 秒传一大坨）
#[allow(dead_code)]
pub fn list_for_ui() -> Vec<Value> {
    all()
        .iter()
        .map(|m| {
            json!({
                "num": m.num,
                "kind": "macro",
                "name": m.name,
                "len": m.steps.len(),
                "loop": m.loop_count,
                // 引用值：绑定里用 -1101-... 指向它（与 C# Macros.RefBase 一致）
                "ref": -1100 - m.num as i64,
            })
        })
        .collect()
}

/// 取一条宏给编辑界面（带 steps）
#[allow(dead_code)]
pub fn to_ui(m: &Macro) -> Value {
    json!({
        "ok": true,
        "num": m.num,
        "name": m.name,
        "steps": m.steps,
        "loop": m.loop_count,
    })
}

/// 把旧格式里 `press:"both"` 的按键步骤拆成 按下 / 等待 / 弹起 三步。
///
/// 为什么要迁移：以前"按下+弹起"是一个步骤（只能整体删），现在改成各自独立成步，
/// 旧数据如果不动，界面上会显示成一个 "⇅" 的怪步骤，也没法单独删按下或弹起。
pub fn migrate_split_both() {
    let mut changed_nums: Vec<u32> = Vec::new();
    for m in all() {
        let mut out: Vec<serde_json::Value> = Vec::new();
        let mut changed = false;
        for st in m.steps {
            if st["type"].as_str() == Some("keys") && st["press"].as_str() == Some("both") {
                changed = true;
                let vks = st["vks"].clone();
                let sym = st["sym"].clone();
                let hold = st["holdMs"].as_u64().unwrap_or(50);
                out.push(serde_json::json!({
                    "type": "keys", "vks": vks, "sym": sym, "press": "down", "holdMs": 0, "ms": 0, "slot": 1
                }));
                if hold > 0 {
                    out.push(serde_json::json!({ "type": "wait", "ms": hold, "slot": 1, "sym": "" }));
                }
                out.push(serde_json::json!({
                    "type": "keys", "vks": st["vks"].clone(), "sym": st["sym"].clone(),
                    "press": "up", "holdMs": 0, "ms": 0, "slot": 1
                }));
            } else {
                out.push(st);
            }
        }
        if changed {
            let _ = save(m.num, &m.name, out, m.loop_count);
            changed_nums.push(m.num);
        }
    }
    if !changed_nums.is_empty() {
        eprintln!("[迁移] 已把宏 {:?} 的合并按键步骤拆成按下/弹起", changed_nums);
    }
}
