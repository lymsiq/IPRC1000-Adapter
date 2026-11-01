//! 文本槽与宏的编辑页（2026-10-31，egui 版）
//!
//! 两个页签解决两件不同的事：
//!   · **文本**：10 个槽，每槽一段话。绑到键上按下就输出（Unicode 注入，绕过输入法）。
//!   · **宏**：多条步骤序列，可新建 / 录制 / 逐步插入 / 试运行 / 删除。
//!
//! 步骤编辑的原则：**每一步都单独插入**（按键 / 延迟 / 鼠标点击），
//! 不做"必须录一段才能改"那种重模式 —— 录制是捷径，不是唯一路径。
//!
//! ⚠ 借用注意：egui 闭包里已经借了 `&mut PagesState`，所以按钮点击只把动作
//!   **记到队列**（`pending_step` / `pending_msg`），等所有闭包结束后统一执行，
//!   否则会二次借用冲突（编译器直接报错）。

use eframe::egui::{self, Color32, RichText};

use crate::macro_store;
use crate::text_slot;

pub struct PagesState {
    pub text_slot: u32,
    pub text_name: String,
    pub text_body: String,
    pub macro_sel: u32,
    pub recording: bool,
    pub insert: crate::ui_insert::InsertKeyState,
    /// 插入延迟用的毫秒数
    pub wait_ms: u64,
    /// 待插入的步骤（按钮只入队，末尾统一执行）
    pub pending_step: Option<serde_json::Value>,
    /// 待提示的消息
    pub pending_msg: Option<String>,
    /// 待删除的步骤序号（0 基）—— 按钮只入队，末尾统一执行
    pub pending_delete: Option<usize>,
    /// 当前选中的步骤行（插入操作以它为基准：插到它**下面**）
    pub sel_step: Option<usize>,
}

impl Default for PagesState {
    fn default() -> Self {
        // 默认选中槽 1，并且**把内容读进来** —— 否则界面上显示"1（4 字）"但文本框是空的
        let t1 = text_slot::get(1);
        Self {
            text_slot: 1,
            text_name: t1.as_ref().map(|x| x.name.clone()).unwrap_or_default(),
            text_body: t1.as_ref().map(|x| x.text.clone()).unwrap_or_default(),
            // 默认选中第一条宏 —— 否则一进来就是"未选中宏"，步骤区空白，
            // 用户会以为列表丢了（实测反馈）
            // 默认选中**最后一条**：通常是刚录/刚建的，比空列表里的第一条更有意义
            macro_sel: macro_store::all().last().map(|m| m.num).unwrap_or(0),
            recording: false,
            insert: Default::default(),
            wait_ms: 300,
            pending_step: None,
            pending_msg: None,
            pending_delete: None,
            sel_step: None,
        }
    }
}

impl PagesState {
    pub fn load_slot(&mut self, slot: u32) {
        self.text_slot = slot;
        if let Some(t) = text_slot::get(slot) {
            self.text_name = t.name;
            self.text_body = t.text;
        } else {
            self.text_name.clear();
            self.text_body.clear();
        }
    }
}

/// 取当前光标屏幕坐标（插入鼠标点击时用"现在的位置"）
fn cursor_pos() -> (i32, i32) {
    unsafe {
        let mut p = windows::Win32::Foundation::POINT::default();
        let _ = windows::Win32::UI::WindowsAndMessaging::GetCursorPos(&mut p);
        (p.x, p.y)
    }
}

/// 把一步（或由它展开的多步）追加到当前选中的宏。
///
/// "按下+弹起"会展开成 **3 步**（按下 → 等待 → 弹起），这样每一步都能单独删 ——
/// 用户明确要求"按下 / 弹起 各自可删"，合成一步就做不到。
pub fn append_step(st: &mut PagesState, step: serde_json::Value) -> String {
    insert_step(st, step, st.sel_step.map(|i| i + 1))
}

/// 把步骤插到 `at` 位置（None = 末尾）。
pub fn insert_step(st: &mut PagesState, step: serde_json::Value, at: Option<usize>) -> String {
    if st.macro_sel == 0 {
        return String::from("先在上面选一条宏，再插入步骤");
    }
    let Some(m) = macro_store::get(st.macro_sel) else {
        return format!("宏 {} 不存在（可能已被删除）", st.macro_sel);
    };
    let mut steps = m.steps.clone();
    // 展开：按下+弹起 → [down, wait, up]
    let mut expanded: Vec<serde_json::Value> = Vec::new();
    if step["type"] == "keys" && step["press"].as_str().unwrap_or("") == "both" {
        let vks = step["vks"].clone();
        let sym = step["sym"].clone();
        let hold = step["holdMs"].as_u64().unwrap_or(50);
        expanded.push(serde_json::json!({
            "type": "keys", "vks": vks, "sym": sym, "press": "down", "holdMs": 0, "ms": 0, "slot": 1
        }));
        if hold > 0 {
            expanded.push(serde_json::json!({ "type": "wait", "ms": hold, "slot": 1, "sym": "" }));
        }
        expanded.push(serde_json::json!({
            "type": "keys", "vks": step["vks"].clone(), "sym": step["sym"].clone(),
            "press": "up", "holdMs": 0, "ms": 0, "slot": 1
        }));
    } else {
        expanded.push(step);
    }
    if steps.len() + expanded.len() > 50 {
        return String::from("一个宏最多 50 步");
    }
    let add = expanded.len();
    let pos = at.unwrap_or(steps.len()).min(steps.len());
    for (i, x) in expanded.into_iter().enumerate() {
        steps.insert(pos + i, x);
    }
    match macro_store::save(m.num, &m.name, steps, m.loop_count) {
        Ok(_) => {
            let n = macro_store::get(m.num).map(|x| x.steps.len()).unwrap_or(0);
            st.sel_step = Some((pos + add).saturating_sub(1).min(n.saturating_sub(1)));
            format!(
                "已插入 {} 步到第 {} 步之后（宏 {} 现在 {} 步）",
                add, if pos == 0 { 0 } else { pos }, m.num, n
            )
        }
        Err(e) => format!("插入失败：{e}"),
    }
}

/// 文本页
pub fn text_page(ui: &mut egui::Ui, st: &mut PagesState, toast: &mut dyn FnMut(String)) {
    ui.add_space(4.0);
    ui.label(RichText::new("文本槽（绑到键上，按下就输出这段文字）").strong());
    ui.add_space(4.0);

    ui.horizontal_wrapped(|ui| {
        for slot in 1..=10u32 {
            let has = text_slot::get(slot).map(|t| t.text.chars().count()).unwrap_or(0);
            let sel = st.text_slot == slot;
            let label = if has > 0 { format!("{slot}（{has} 字）") } else { format!("{slot}") };
            if ui.selectable_label(sel, label).clicked() && !sel {
                st.load_slot(slot);
            }
        }
    });
    ui.separator();

    ui.horizontal(|ui| {
        ui.label("名字：");
        ui.add(egui::TextEdit::singleline(&mut st.text_name).desired_width(240.0));
    });
    ui.add_space(4.0);
    ui.add(
        egui::TextEdit::multiline(&mut st.text_body)
            .desired_rows(10)
            .desired_width(f32::INFINITY)
            .hint_text("要输出的文字…（支持中文，输出走 Unicode 注入，不经输入法）"),
    );
    ui.add_space(6.0);
    ui.horizontal(|ui| {
        if ui.button("保存这个槽").clicked() {
            if st.text_body.trim().is_empty() {
                toast(String::from("文本是空的"));
            } else {
                text_slot::set(st.text_slot, &st.text_name, &st.text_body);
                toast(format!(
                    "文本槽 {} 已保存（{} 字）",
                    st.text_slot,
                    st.text_body.chars().count()
                ));
            }
        }
        if ui.button("清空").clicked() {
            st.text_body.clear();
            text_slot::set(st.text_slot, "", "");
            toast(format!("文本槽 {} 已清空", st.text_slot));
        }
        ui.label(
            RichText::new(format!("绑定值 {}", -1000 - st.text_slot as i64))
                .small()
                .weak(),
        );
    });
}

/// 宏页
pub fn macro_page(ui: &mut egui::Ui, st: &mut PagesState, toast: &mut dyn FnMut(String)) {
    // 编辑页：任何改动都要立刻可见，不能等 App 层 500 ms 定时刷新
    ui.ctx().request_repaint();

    // ── 全局热键：F10 开始、F11 停止 ──
    if crate::hotkey::take_f10() && !st.recording {
        if crate::recorder::start() {
            st.recording = true;
            st.pending_msg = Some(String::from("F10 已触发：开始录制（按 F11 结束）"));
        }
    }
    if st.recording && crate::recorder::stop_requested() {
        let evs = crate::recorder::stop();
        st.recording = false;
        let steps = crate::recorder::build_steps(&evs);
        if steps.is_empty() {
            st.pending_msg = Some(String::from("F11 已停止：没录到任何操作"));
        } else {
            let n = steps.len();
            match macro_store::save(0, "录制的宏", steps, 1) {
                Ok(num) => {
                    st.macro_sel = num;
                    st.pending_msg = Some(format!("F11 已停止：已保存为宏 {num}（{n} 步）"));
                }
                Err(e) => st.pending_msg = Some(format!("保存失败：{e}")),
            }
        }
    }

    // ── 顶部工具条 ──
    ui.horizontal(|ui| {
        ui.label(RichText::new("宏列表").strong());
        if ui.button("+ 新建宏").clicked() {
            match macro_store::save(0, "", Vec::new(), 1) {
                Ok(num) => {
                    // 先给个默认名（列表里不至于显示成"宏 7"），用户可立刻在右侧改
                    let _ = macro_store::save(num, &format!("宏 {num}"), Vec::new(), 1);
                    st.macro_sel = num;
                    st.pending_msg = Some(format!("已新建「宏 {num}」—— 右侧可改名字"));
                }
                Err(e) => st.pending_msg = Some(format!("新建失败：{e}")),
            }
        }
        if !st.recording {
            if ui.button("● 开始录制").clicked() {
                if crate::recorder::start() {
                    st.recording = true;
                    st.pending_msg = Some(String::from("录制中：在键盘/鼠标上操作，完成后点「停止录制」"));
                } else {
                    st.pending_msg = Some(String::from("已经在录制了"));
                }
            }
        } else if ui
            .add(egui::Button::new(RichText::new("■ 停止录制").color(Color32::from_rgb(230, 120, 120))))
            .clicked()
        {
            let evs = crate::recorder::stop();
            st.recording = false;
            let steps = crate::recorder::build_steps(&evs);
            if steps.is_empty() {
                st.pending_msg = Some(String::from("没录到任何操作（遥控器按键不会被录）"));
            } else {
                let n = steps.len();
                match macro_store::save(0, "录制的宏", steps, 1) {
                    Ok(num) => {
                        st.macro_sel = num;
                        st.pending_msg = Some(format!("已录制并保存为宏 {num}（{n} 步）"));
                    }
                    Err(e) => st.pending_msg = Some(format!("保存失败：{e}")),
                }
            }
        }
    });
    ui.separator();

    // ── 中部：左=宏列表，右=详情与插入工具 ──
    // ★ 必须用 allocate_ui 把这块**限死在 160 高**：
    //   之前直接用 horizontal_top，它会把剩余高度全占满，导致下面的"步骤明细"高度为 0
    //   —— 界面上表现就是"步骤列表整个不见了"。
    let mid_w = ui.available_width();
    ui.allocate_ui(egui::vec2(mid_w, 160.0), |ui| {
    ui.horizontal_top(|ui| {
        ui.vertical(|ui| {
            ui.set_width(220.0);
            ui.label(RichText::new("宏").small().weak());
            egui::ScrollArea::vertical()
                .id_salt("macro_list")
                .max_height(150.0)
                .auto_shrink([false, false])
                .show(ui, |ui| {
                    let list = macro_store::all();
                    if list.is_empty() {
                        ui.label(RichText::new("（空）").weak());
                    }
                    for m in &list {
                        let sel = st.macro_sel == m.num;
                        let name = if m.name.is_empty() { format!("宏 {}", m.num) } else { m.name.clone() };
                        if ui.selectable_label(sel, format!("{name}　{} 步", m.steps.len())).clicked() {
                            st.macro_sel = m.num;
                        }
                    }
                });
        });
        ui.separator();

        ui.vertical(|ui| {
            let Some(m) = macro_store::get(st.macro_sel) else {
                ui.label(RichText::new("← 选一条宏，或先「+ 新建宏」").weak());
                return;
            };
            let mut name_edit: Option<String> = None;
            let mut loop_edit: Option<u32> = None;
            // 名字（可改）+ 循环次数
            ui.horizontal(|ui| {
                ui.label(RichText::new("名字：").small().weak());
                let mut name = if m.name.is_empty() {
                    format!("宏 {}", m.num)
                } else {
                    m.name.clone()
                };
                if ui
                    .add(
                        egui::TextEdit::singleline(&mut name)
                            .desired_width(200.0)
                            .hint_text("给这条宏起个名字"),
                    )
                    .changed()
                {
                    name_edit = Some(name.clone());
                }
                ui.label(RichText::new("循环").small().weak());
                let mut lp = m.loop_count as i32;
                if ui
                    .add(egui::DragValue::new(&mut lp).range(0..=999).suffix(" 次"))
                    .on_hover_text("0 = 一直循环（10 分钟上限）")
                    .changed()
                {
                    loop_edit = Some(lp.max(0) as u32);
                }
            });
            ui.add_space(2.0);
            ui.horizontal(|ui| {
                ui.label(RichText::new(format!("宏 {} · {} 步", m.num, m.steps.len())).strong());
                if ui.button("试运行").clicked() {
                    let num = m.num;
                    st.pending_msg = Some(format!("宏 {num} 开始试运行（按任意遥控器键可中断）"));
                    std::thread::spawn(move || {
                        if let Some(x) = macro_store::get(num) {
                            let r = crate::macro_player::play(&x.steps, 1);
                            eprintln!("[宏] 试运行：{r}");
                        }
                    });
                }
                if ui.button("清空步骤").clicked() {
                    let (num, name, lp) = (m.num, m.name.clone(), m.loop_count);
                    let _ = macro_store::save(num, &name, Vec::new(), lp);
                    st.pending_msg = Some(format!("宏 {num} 已清空"));
                }
                if ui.button("删除宏").clicked() {
                    let num = m.num;
                    macro_store::delete(num);
                    st.macro_sel = 0;
                    st.pending_msg = Some(format!("宏 {num} 已删除"));
                }
            });
            ui.add_space(4.0);

            // 名字 / 循环次数改动 → 立即落盘
            if name_edit.is_some() || loop_edit.is_some() {
                let nm = name_edit.clone().unwrap_or_else(|| m.name.clone());
                let lp = loop_edit.unwrap_or(m.loop_count);
                let _ = macro_store::save(m.num, &nm, m.steps.clone(), lp);
            }

            // 插入位置提示（选中某行就插到它下面）
            ui.horizontal(|ui| {
                let pos_txt = match st.sel_step {
                    Some(i) if i < m.steps.len() => format!("将插入到第 {} 步之后", i + 1),
                    _ => String::from("将追加到末尾（点某一步可选择插入位置）"),
                };
                ui.label(RichText::new(pos_txt).small().color(Color32::from_rgb(120, 200, 255)));
                if st.sel_step.is_some() && ui.small_button("取消选择").clicked() {
                    st.sel_step = None;
                }
            });
            // 插入工具：按键 / 延迟 / 鼠标点击（点一下就插，不需要再点确认）
            ui.horizontal_wrapped(|ui| {
                if ui.button("+ 按键").clicked() {
                    st.insert.open = true;
                    st.insert.waiting = true;
                    st.insert.vks.clear();
                }
                let mut ms = st.wait_ms as i32;
                if ui
                    .add(egui::DragValue::new(&mut ms).range(10..=60000).suffix(" ms"))
                    .changed()
                {
                    st.wait_ms = ms.max(10) as u64;
                }
                if ui.button("+ 延迟").clicked() {
                    st.pending_step = Some(serde_json::json!({
                        "type": "wait", "ms": st.wait_ms, "slot": 1, "sym": ""
                    }));
                }
                ui.separator();
                ui.label(RichText::new("点击：").small().weak());
                // ★ 点左/右/中键**直接插入**（用当前光标位置）—— 之前要先选再点"+点击"，两步反人类
                for (name, key) in [("左键", "left"), ("右键", "right"), ("中键", "middle")] {
                    if ui.button(name).clicked() {
                        let (x, y) = cursor_pos();
                        let sym = if key == "right" { "右键" } else if key == "middle" { "中键" } else { "左键" };
                        st.pending_step = Some(serde_json::json!({
                            "type": "click", "button": key, "x": x, "y": y,
                            "mode": "here", "img": null, "ms": 0, "slot": 1, "sym": sym
                        }));
                    }
                }
                ui.label(RichText::new("（先停鼠标，点按钮=在当前位置插一次点击）").small().weak());
            });
        });
    });
    });
    ui.separator();

    // ── 底部：步骤明细（**全宽**，占满剩余高度；放进 horizontal 里算不出高度）──
    let Some(m) = macro_store::get(st.macro_sel) else {
        ui.label(RichText::new("（未选中宏）").weak());
        finish_pending(st, toast);
        return;
    };
    ui.horizontal(|ui| {
        ui.label(RichText::new("步骤明细").strong());
        ui.label(RichText::new(format!("共 {} 步", m.steps.len())).small().weak());
    });
    egui::ScrollArea::vertical()
        .id_salt("macro_steps")
        .auto_shrink([false, false])
        .show(ui, |ui| {
            if m.steps.is_empty() {
                ui.label(RichText::new("（还没有步骤）").weak());
            }
            let mut del: Option<usize> = None;
            let mut wait_edit: Option<(usize, u64)> = None;
            // ★ 用 Grid（表格）而不是手算宽度：
            //   `allocate_ui_with_layout` 的宽度只是"建议值"，内容会把它撑开，
            //   导致"等待"行的 × 比"按键"行右移几十像素。Grid 天然按列对齐。
            egui::Grid::new("macro_steps_grid")
                .num_columns(3)
                .spacing([10.0, 4.0])
                .min_col_width(80.0)
                .show(ui, |ui| {
                    for (idx, s) in m.steps.iter().enumerate() {
                        let n = idx + 1;
                        let ty = s["type"].as_str().unwrap_or("?");
                        let selected = st.sel_step == Some(idx);

                        // 第 1 列：步骤描述（可点击选中）
                        let (text, is_wait) = match ty {
                            "keys" => {
                                let sym = s["sym"].as_str().unwrap_or("");
                                let arrow = match s["press"].as_str().unwrap_or("both") {
                                    "down" => "↓ 按下",
                                    "up" => "↑ 弹起",
                                    _ => "⇅",
                                };
                                (format!("{n}. {arrow} {sym}"), false)
                            }
                            "wait" => (format!("{n}. 等待"), true),
                            "click" => (
                                format!(
                                    "{n}. 点击 {} ({}, {})",
                                    s["sym"].as_str().unwrap_or(""),
                                    s["x"],
                                    s["y"]
                                ),
                                false,
                            ),
                            "text" => (
                                format!(
                                    "{n}. 文本「{}」",
                                    s["text"].as_str().unwrap_or("").chars().take(20).collect::<String>()
                                ),
                                false,
                            ),
                            other => (format!("{n}. {other}"), false),
                        };
                        if ui
                            .add(egui::SelectableLabel::new(selected, RichText::new(text)))
                            .clicked()
                        {
                            st.sel_step = Some(idx);
                        }

                        // 第 2 列：延迟的毫秒数（可改）；其它步骤留空
                        if is_wait {
                            let mut ms = s["ms"].as_u64().unwrap_or(0) as i32;
                            if ui
                                .add_sized(
                                    [96.0, 20.0],
                                    egui::DragValue::new(&mut ms).range(10..=60000).suffix(" ms"),
                                )
                                .on_hover_text("拖动或双击修改")
                                .changed()
                            {
                                wait_edit = Some((idx, ms.max(10) as u64));
                            }
                        } else {
                            ui.label("");
                        }

                        // 第 3 列：删除（固定列）
                        if ui
                            .add(
                                egui::Button::new(
                                    RichText::new("×").color(Color32::from_rgb(230, 120, 120)),
                                )
                                .small(),
                            )
                            .on_hover_text("删除这一步")
                            .clicked()
                        {
                            del = Some(idx);
                        }
                        ui.end_row();
                    }
                });
            if let Some((idx, ms)) = wait_edit {
                if let Some(m3) = macro_store::get(st.macro_sel) {
                    let mut steps = m3.steps.clone();
                    if let Some(x) = steps.get_mut(idx) {
                        x["ms"] = serde_json::json!(ms);
                        let _ = macro_store::save(m3.num, &m3.name, steps, m3.loop_count);
                        st.pending_msg = Some(format!("第 {} 步延迟改为 {} ms", idx + 1, ms));
                    }
                }
            }
            if let Some(i) = del {
                st.pending_delete = Some(i);
            }
        });

    // 单步删除（按钮只入队，这里统一执行）
    if let Some(idx) = st.pending_delete.take() {
        if let Some(m2) = macro_store::get(st.macro_sel) {
            let mut steps = m2.steps.clone();
            if idx < steps.len() {
                steps.remove(idx);
                match macro_store::save(m2.num, &m2.name, steps, m2.loop_count) {
                    Ok(_) => toast(format!("已删除第 {} 步（剩 {} 步）", idx + 1,
                        macro_store::get(m2.num).map(|x| x.steps.len()).unwrap_or(0))),
                    Err(e) => toast(format!("删除失败：{e}")),
                }
            }
        }
    }

    finish_pending(st, toast);

    // 插入按键弹框
    if st.insert.open {
        if let Some(step) = crate::ui_insert::show(ui.ctx(), &mut st.insert) {
            let msg = append_step(st, step);
            toast(msg);
        }
    }
}

/// 统一处理"待插入步骤"和"待提示消息"
fn finish_pending(st: &mut PagesState, toast: &mut dyn FnMut(String)) {
    if let Some(step) = st.pending_step.take() {
        st.pending_msg = Some(append_step(st, step));
    }
    if let Some(m) = st.pending_msg.take() {
        toast(m);
    }
}
