//! egui 界面（2026-10-31，替代 Tauri/WebView2）
//!
//! 范围（用户明确划定）：**只做按键映射 + 输入法管理 + 自动安装**，其余功能全部砍掉。
//!
//! 为什么从 Tauri 换过来：连着一整天遇到的问题（界面卡死、数据不更新、显示假数据）
//! 根子都在 WebView2 那一层 —— IPC 序列化、渲染进程生命周期、后台节流。
//! egui 是纯 Rust 即时渲染，界面每帧直接调 `keymap::vks_of()` / `settings::ime()`，
//! **没有中间的 JSON 往返，也就不存在"界面和真实状态不一致"这种故障模式**。
//!
//! 代价：中文要自己加载系统字体（微软雅黑），常驻内存约 +20 MB —— 仍然远低于 WebView2 的 340 MB。

use std::time::Duration;

use eframe::egui;
use egui::{Color32, FontFamily, FontId, RichText};

use crate::{backend, keymap, macro_store, mouse_act, settings, text_slot};

#[derive(PartialEq, Clone, Copy)]
enum Tab {
    Keys,
    Ime,
    Text,
    Macro,
    Setup,
}

pub struct App {
    tab: Tab,
    /// 正在等待录键的按键 id（点「改键」后进入）
    capturing: Option<String>,
    /// 底部提示（保存结果之类）
    toast: String,
    toast_at: std::time::Instant,
    /// 首次运行时自动检测一次环境
    probed: bool,
    /// 自动安装的进度/结果
    install_msg: String,
    installing: bool,
    /// 正在打开的改键弹窗
    edit: Option<crate::ui_edit::EditState>,
    /// 卸载二次确认
    confirm_uninstall: bool,
    /// 窗口是否被收进托盘（egui 0.29 的 ViewportInfo 没有 visible 字段，自己记）
    hidden: bool,
    /// 收到关闭请求的下一帧才真正隐藏（见 update 里的说明）
    /// 当前是否应该占用 F10 热键（只有宏页面且窗口可见时需要）
    hotkey_want: bool,
    /// 文本/宏页的状态
    pages: crate::ui_pages::PagesState,
}

impl App {
    pub fn new(cc: &eframe::CreationContext<'_>) -> Self {
        install_cjk_font(&cc.egui_ctx);
        // 深色主题（与之前界面一致，也更适合长时间看）
        cc.egui_ctx.set_visuals(egui::Visuals::dark());
        // 字号整体放大一点（104 键/中文小字在高 DPI 下容易糊）
        let mut style = (*cc.egui_ctx.style()).clone();
        style.text_styles = [
            (egui::TextStyle::Heading, FontId::proportional(20.0)),
            (egui::TextStyle::Body, FontId::proportional(14.5)),
            (egui::TextStyle::Button, FontId::proportional(14.5)),
            (egui::TextStyle::Small, FontId::proportional(12.0)),
        ]
        .into();
        style.spacing.item_spacing = egui::vec2(8.0, 4.0);
        cc.egui_ctx.set_style(style);

        Self {
            tab: initial_tab(),
            capturing: None,
            toast: String::new(),
            toast_at: std::time::Instant::now(),
            probed: false,
            install_msg: String::new(),
            installing: false,
            edit: None,
            confirm_uninstall: false,
            hidden: false,
            hotkey_want: false,
            pages: crate::ui_pages::PagesState::default(),
        }
    }

    fn say(&mut self, s: impl Into<String>) {
        self.toast = s.into();
        self.toast_at = std::time::Instant::now();
    }
}

/// 加载系统里的中文字体，否则中文全是方框。
///
/// ★★ 2026-11-01（内存优化，实测有效）：原来固定读 `msyh.ttc` —— **整份 18.8 MB**
///   读进私有堆（`fs::read` → `FontData::from_owned`）。而且 TTC 是"字体集合"，
///   ab_glyph 只取第 0 个字面，却要把整包一直留在内存里做字形光栅化。
///
///   改为**按体积从小到大挑**，并在日志里写明选了哪个、多大 ——
///   否则"内存为什么降了/没降"永远说不清：
///     msyhl.ttc 11.6 MB（雅黑 Light，同一家族，视觉风险最低）
///     simhei.ttf 9.3 MB（黑体，字形更重，想再省 2.3 MB 可把它提到第一位）
///     msyh.ttc  18.8 MB（原来的默认）
///
///   ⚠ **不要用 `include_bytes!` 把系统字体打进 exe**：微软雅黑是微软的专有字体，
///     随包分发是侵权。系统字体只能就地读取。要内置必须换成 OFL 授权的开源字体。
fn install_cjk_font(ctx: &egui::Context) {
    let mut fonts = egui::FontDefinitions::default();
    const CANDIDATES: [&str; 3] = [
        "C:/Windows/Fonts/msyhl.ttc",  // 雅黑 Light 11.6 MB
        "C:/Windows/Fonts/simhei.ttf", // 黑体 9.3 MB
        "C:/Windows/Fonts/msyh.ttc",   // 雅黑 18.8 MB（兜底）
    ];
    for path in CANDIDATES {
        if let Ok(bytes) = std::fs::read(path) {
            let mb = bytes.len() as f64 / 1024.0 / 1024.0;
            eprintln!("[字体] 使用 {path}（{mb:.1} MB，占用私有内存）");
            fonts.font_data.insert(
                "cjk".to_owned(),
                egui::FontData::from_owned(bytes).into(),
            );
            // ★ 必须**追加到末尾**：中文字体里没有 ✓ ✗ ↑ 这类符号字形，
            //   插到最前面会让它们全部变成方框（实测踩到）。尾部追加 = 拉丁优先、
            //   中文靠 fallback，两边都对。
            for fam in [FontFamily::Proportional, FontFamily::Monospace] {
                fonts.families.entry(fam).or_default().push("cjk".to_owned());
            }
            break;
        }
    }
    ctx.set_fonts(fonts);
}

impl eframe::App for App {
    fn update(&mut self, ctx: &egui::Context, _frame: &mut eframe::Frame) {
        // 每帧确认"工具窗口"样式（winit 会覆盖，见 ensure_toolwindow 的说明）
        if !self.hidden {
            crate::ensure_toolwindow();
        }

        // ★ F10 录制热键只在「宏」页面 + 窗口可见 时占用。
        //   别的页面一直占着 F10 会跟用户其它软件抢按键（用户明确要求改掉）。
        //   隐藏到托盘时也释放 —— 那时看不到录制按钮，按 F10 没有意义。
        let want_hotkey = self.tab == Tab::Macro && !self.hidden;
        if want_hotkey != self.hotkey_want {
            self.hotkey_want = want_hotkey;
            crate::hotkey::set_enabled(want_hotkey);
        }

        // 托盘点了「打开界面」→ 由 UI 线程发指令（不能从托盘线程直接动 egui 窗口）
        // 只有"当前确实是隐藏状态"才处理唤回 —— 否则 SHOW_REQ 反复置位会刷屏（实测 12 次）
        if self.hidden && crate::SHOW_REQ.swap(false, std::sync::atomic::Ordering::Relaxed) {
            ctx.send_viewport_cmd(egui::ViewportCommand::OuterPosition(egui::pos2(80.0, 60.0)));
            ctx.send_viewport_cmd(egui::ViewportCommand::InnerSize(egui::vec2(900.0, 660.0)));
            crate::set_window_alpha(255);
            ctx.send_viewport_cmd(egui::ViewportCommand::Focus);
            self.hidden = false;
            eprintln!("[i] 已从托盘唤回界面");
        }

        // 关闭窗口 = 收进托盘，**不退出程序**
        //
        // ★★ 2026-11-01（内存优化）：这段原来是为了"留住窗口"才写的——帧 A 发 CancelClose、
        //   帧 B 缩成 1×1 + alpha 0，让 egui 继续跑 update()。代价是 **GL 上下文、字体 atlas、
        //   eframe 框架内存永远常驻**（实测：含同样字体的最小 eframe 程序就要 156 MB）。
        //   一个 95% 时间在后台的工具，不该背着整套 GPU 渲染栈。
        //
        //   现在反过来：**关窗就真的结束 UI 线程**，不 CancelClose。
        //   run_native 返回后主循环等 SHOW_REQ（托盘左键 / 菜单「打开界面」）再重建窗口；
        //   托盘线程是独立的（tray-icon 自有消息循环），不需要 egui 活着。
        //
        //   ⚠ 踩过的坑：**不要在这里再发 ViewportCommand::Close**。
        //     那样会与下面的 CancelClose 互相取消，变成死循环——
        //     实测日志里 "执行隐藏 → 收到关闭请求 → 执行隐藏 …" 无限重复，
        //     内存一点没降。让系统这次关闭正常完成即可。
        if ctx.input(|i| i.viewport().close_requested()) {
            eprintln!("[i] 收到关闭请求 ⇒ 收进托盘：结束 UI 线程以释放内存");
            crate::HIDE_REQUESTED.store(true, std::sync::atomic::Ordering::Relaxed);
        }

        // 首次运行做一次环境探测（会初始化 COM，之后 probes 有 15 秒缓存）
        if !self.probed {
            self.probed = true;
            let _ = crate::commands::probes();
        }
        // 录键中：轮询捕获结果
        if self.capturing.is_some() {
            if let Some(vks) = backend::poll_capture() {
                let id = self.capturing.take().unwrap_or_default();
                let val = vks
                    .iter()
                    .map(|v| v.to_string())
                    .collect::<Vec<_>>()
                    .join(",");
                let msg = crate::apply_binding(&id, &val);
                self.say(msg);
            }
            // 录像期间也要刷新界面（不然看不到"等待按键"的动效）
            ctx.request_repaint_after(Duration::from_millis(120));
        } else {
            // ★ 无条件请求重绘（参考 egui 托盘方案的标准做法）：
            //   窗口可见时按 500ms 节拍；**隐藏时用 request_repaint() 立即请求**，
            //   这样 update() 会持续被调用，托盘的唤回信号才能被处理。
            //   隐藏时虽然每帧都跑 update，但下面 `if self.hidden { return }` 会跳过
            //   整个 UI 构建，实际开销只有几十微秒。
            let interval = if self.hidden { 1000 } else { 500 };
            ctx.request_repaint_after(Duration::from_millis(interval));
        }

        egui::TopBottomPanel::top("top").show(ctx, |ui| {
            ui.add_space(6.0);
            ui.horizontal(|ui| {
                ui.label(RichText::new("IPRC1000").heading().strong());
                ui.label(RichText::new("原生内核").small().weak());
                ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                    let (txt, color) = if crate::is_online() {
                        ("● 遥控器在线", Color32::from_rgb(80, 200, 120))
                    } else {
                        ("○ 遥控器休眠（按任意键唤醒）", Color32::from_rgb(200, 170, 80))
                    };
                    ui.label(RichText::new(txt).color(color));
                });
            });
            ui.add_space(4.0);
            ui.horizontal(|ui| {
                ui.selectable_value(&mut self.tab, Tab::Keys, "按键映射");
                ui.selectable_value(&mut self.tab, Tab::Ime, "输入法");
                ui.selectable_value(&mut self.tab, Tab::Text, "文本");
                ui.selectable_value(&mut self.tab, Tab::Macro, "宏");
                ui.selectable_value(&mut self.tab, Tab::Setup, "环境与安装");
            });
            ui.add_space(4.0);
        });

        egui::TopBottomPanel::bottom("bottom").show(ctx, |ui| {
            ui.add_space(3.0);
            ui.horizontal(|ui| {
                if !self.toast.is_empty() && self.toast_at.elapsed().as_secs() < 8 {
                    ui.label(RichText::new(&self.toast).color(Color32::from_rgb(120, 200, 255)));
                } else {
                    ui.label(RichText::new("就绪").weak());
                }
                ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                    ui.label(RichText::new(format!("v{}", env!("CARGO_PKG_VERSION"))).small().weak());
                });
            });
            ui.add_space(3.0);
        });

        egui::CentralPanel::default().show(ctx, |ui| {
            match self.tab {
                Tab::Keys => self.keys_page(ui),
                Tab::Ime => self.ime_page(ui),
                Tab::Setup => self.setup_page(ui),
                // 文本/宏两页需要把 say 借出去（闭包），所以先把 toast 取出来
                Tab::Text | Tab::Macro => {}
            }
            if matches!(self.tab, Tab::Text | Tab::Macro) {
                let mut msg: Option<String> = None;
                {
                    let mut say = |m: String| msg = Some(m);
                    match self.tab {
                        Tab::Text => crate::ui_pages::text_page(ui, &mut self.pages, &mut say),
                        Tab::Macro => crate::ui_pages::macro_page(ui, &mut self.pages, &mut say),
                        _ => {}
                    }
                }
                if let Some(m) = msg {
                    self.say(m);
                }
            }
        });

        // 改键弹窗（模态浮层，最后画才会盖在上面）
        if let Some(st) = self.edit.as_mut() {
            if let Some(res) = crate::ui_edit::show(ctx, st) {
                let id = st.id.clone();
                self.edit = None;
                match res {
                    Some(val) => {
                        let msg = crate::apply_binding(&id, &val);
                        self.say(msg);
                    }
                    None => self.say("已取消"),
                }
            }
            ctx.request_repaint_after(Duration::from_millis(120));
        }
    }
}

impl App {
    /// 按键映射：23 个键一行一个，右侧显示当前绑定 + 「改键」
    fn keys_page(&mut self, ui: &mut egui::Ui) {
        let mouse = settings::is_mouse_active();
        ui.horizontal(|ui| {
            ui.label(RichText::new("配置组").strong());
            let groups = settings::groups_ui();
            let active = settings::active_group() as usize;
            for (i, g) in groups.iter().enumerate() {
                let name = g["name"].as_str().unwrap_or("组");
                if ui
                    .selectable_label(active == i, format!("{name}（{}）", g["configured"]))
                    .clicked()
                    && active != i
                {
                    switch_group(i as u8);
                    self.say(format!("已切换到「{name}」"));
                }
            }
        });
        if mouse {
            ui.label(
                RichText::new("鼠标模式：方向键移动光标（单轴），OK=左键，返回=右键，频道±=滚轮")
                    .color(Color32::from_rgb(120, 200, 255))
                    .small(),
            );
        }
        ui.separator();

        egui::ScrollArea::vertical().show(ui, |ui| {
            for (usage, _default) in keymap::PLAIN {
                let (id, label) = keymap::usage_label(*usage);
                if id == "unknown" {
                    continue;
                }
                // ★ 显示"实际绑定"，必须区分四类：
                //   VK 列表 / 鼠标动作 / 文本槽 / 宏 —— 只看 vks_of 的话，
                //   后三类都会显示成"未设置"（用户以为没生效）。
                let bind_txt = binding_label(id, *usage, mouse);

                ui.horizontal(|ui| {
                    ui.add_sized([88.0, 20.0], egui::Label::new(RichText::new(label).strong()));
                    ui.add_sized(
                        [70.0, 20.0],
                        egui::Label::new(RichText::new(format!("HID {:02X}", usage)).weak().small()),
                    );
                    let w = 260.0;
                    if self.capturing.as_deref() == Some(id) {
                        ui.add_sized(
                            [w, 20.0],
                            egui::Label::new(
                                RichText::new("请按遥控器上的键…").color(Color32::YELLOW),
                            ),
                        );
                        if ui.button("取消").clicked() {
                            self.capturing = None;
                            backend::cancel_capture();
                        }
                    } else {
                        ui.add_sized([w, 20.0], egui::Label::new(RichText::new(&bind_txt)));
                        if *usage == 0x10 {
                            // ★ 语音键是全局的，绑定由「输入法」页决定 ——
                            //   这里不给改，否则用户会疑惑"我在 A 组改了为什么还这样"
                            ui.label(
                                RichText::new("由「输入法」页决定（全局）")
                                    .small()
                                    .color(Color32::from_rgb(200, 170, 80)),
                            );
                        } else {
                            if ui.button("改键").clicked() {
                                self.edit = Some(crate::ui_edit::EditState::new(id, label));
                            }
                            if ui.button("清除").clicked() {
                                let msg = crate::apply_binding(id, "");
                                self.say(msg);
                            }
                        }
                    }
                });
            }
        });
    }

    /// 输入法 + 触发方式 + 语音时序
    fn ime_page(&mut self, ui: &mut egui::Ui) {
        ui.add_space(6.0);
        ui.label(RichText::new("语音键唤醒哪个输入法").strong());
        ui.horizontal(|ui| {
            let cur = settings::ime();
            if ui.selectable_label(cur == "doubao", "豆包（右 Alt）").clicked() && cur != "doubao" {
                settings::set_ime("doubao");
                self.say("语音键已改为唤醒豆包（右 Alt）");
            }
            if ui.selectable_label(cur == "wechat", "微信输入法（Ctrl + Win）").clicked() && cur != "wechat" {
                settings::set_ime("wechat");
                self.say("语音键已改为微信输入法；若候选框不出现，请把系统默认输入法也切成微信");
            }
        });
        ui.add_space(10.0);

        ui.label(RichText::new("怎么触发").strong());
        ui.horizontal(|ui| {
            let cur = settings::trigger();
            if ui.selectable_label(cur == "hold", "按住说话（松开结束）").clicked() && cur != "hold" {
                settings::set_trigger("hold");
                if let Some(sh) = backend::shared() {
                    if let Ok(mut b) = sh.lock() {
                        b.reset_tap_latch();
                    }
                }
                self.say("已改为按住说话");
            }
            if ui.selectable_label(cur == "tap", "点按一次（再按一次结束）").clicked() && cur != "tap" {
                settings::set_trigger("tap");
                self.say("已改为点按一次");
            }
        });
        ui.add_space(10.0);

        ui.label(RichText::new("语音时序（调小更跟手，代价是可能断流）").strong());
        ui.horizontal(|ui| {
            let cur = settings::preset();
            for (i, (name, ms)) in [("低延迟", 400u64), ("平衡", 600), ("稳定", 900)].iter().enumerate() {
                if ui
                    .selectable_label(cur as usize == i, format!("{name}（{ms} ms）"))
                    .clicked()
                    && cur as usize != i
                {
                    settings::set_preset(i as u8);
                    backend::set_preset(i as u8);
                    self.say(format!("会话判定改为 {ms} ms"));
                }
            }
        });
    }

    /// 环境与自动安装
    fn setup_page(&mut self, ui: &mut egui::Ui) {
        let p = crate::commands::probes();
        ui.add_space(6.0);
        ui.label(RichText::new("环境检查").strong());
        // 只保留 3 项：CABLE Input/Output 是"虚拟声卡已安装"的子项，单独列属于重复信息
        // ★ 声卡要看**健康**，不能只看端点在不在：端点在但驱动坏掉（黄叹号）时，
        //   老判据会显示"已安装"，一键修复也跟着跳过 ⇒ 用户只能自己去设备管理器。
        if let Some(n) = p.cable_problem {
            if n != 0 {
                check_row(
                    ui,
                    false,
                    "虚拟声卡 VB-CABLE",
                    "装了但设备有问题（设备管理器黄叹号）—— 点「一键修复」看处理步骤",
                );
                let warn = Color32::from_rgb(255, 170, 80);
                ui.label(
                    RichText::new(format!(
                        "⚠ Problem {n}：设备存在但驱动不正常。重装不会修好它 —— 请到设备管理器卸载「VB-Audio Virtual Cable」（勾选删除驱动），再点「② 安装/修复虚拟声卡」。"
                    ))
                    .small()
                    .color(warn),
                );
                ui.add_space(4.0);
            } else {
                check_row(ui, true, "虚拟声卡 VB-CABLE", "已安装（输入 + 输出都在，设备健康）");
            }
        } else {
            check_row(
                ui,
                p.cable_in && p.cable_out,
                "虚拟声卡 VB-CABLE",
                match (p.cable_in, p.cable_out) {
                    (true, true) => "已安装（输入 + 输出都在）",
                    (true, false) => "只有 CABLE Input，缺 CABLE Output",
                    (false, true) => "只有 CABLE Output，缺 CABLE Input",
                    (false, false) => "未安装",
                },
            );
        }
        check_row(
            ui,
            p.mic_is_cable,
            "默认录音设备 = CABLE Output",
            if p.mic_is_cable { "已设置" } else { p.mic_name.as_str() },
        );
        // ⚠ 默认播放设备被设成 CABLE Input ⇒ 用户听不到任何声音。
        //   只提示、由用户自己在系统设置里改回（程序不碰播放设备）。
        if p.play_is_cable {
            let warn = Color32::from_rgb(255, 170, 80);
            ui.horizontal_wrapped(|ui| {
                ui.label(
                    RichText::new(format!(
                        "⚠ 默认播放设备是「{}」—— 电脑的声音会流进虚拟线，你自己听不到。\n                         请到 声音设置 → 输出，把默认播放设备改回扬声器/耳机。",
                        p.play_name
                    ))
                    .small()
                    .color(warn),
                );
            });
            ui.add_space(6.0);
        }

        let inject = crate::is_inject_ready();
        check_row(
            ui,
            inject,
            "虚拟键盘驱动 WinUHid",
            if inject { "就绪" } else { "未就绪（设备槽位被占，需重启电脑）" },
        );
        if !inject {
            ui.horizontal(|ui| {
                let warn = Color32::from_rgb(255, 170, 80);
                ui.label(
                    RichText::new("⚠ 按键不会注入：上次异常退出留下的虚拟键盘设备还占着槽位。")
                        .small()
                        .color(warn),
                );
                if ui.small_button("重启电脑").clicked() {
                    let _ = std::process::Command::new("shutdown").args(["/r", "/t", "3"]).spawn();
                }
            });
        }

        // ★ 遥控器是不是还被当成键盘（**换机器 / 换遥控器后必查**）：
        //   还是键盘 ⇒ 它的按键会被系统当键盘输入打进焦点窗口 ⇒ **打出字母**（不跑软件也会）。
        //   正解不是"吞键"，而是把 HID 接口改成 HID-compliant device（点下面「④」）。
        match p.remote_is_keyboard {
            Some(false) => check_row(
                ui,
                true,
                "遥控器不是键盘（HID-compliant）",
                "已改造 —— 按键不会打出字母",
            ),
            Some(true) => {
                check_row(
                    ui,
                    false,
                    "遥控器不是键盘（HID-compliant）",
                    "仍是键盘 —— 按键会打出字母，点「④ 改造遥控器」",
                );
                let warn = Color32::from_rgb(255, 170, 80);
                ui.label(
                    RichText::new("⚠ 这是换机器/换遥控器后的必做一步：不改的话，按遥控器会直接往光标处打字母。")
                        .small()
                        .color(warn),
                );
                ui.add_space(4.0);
            }
            None => check_row(
                ui,
                true,
                "遥控器不是键盘（HID-compliant）",
                "未连接（连上后再检查）",
            ),
        }

        ui.add_space(12.0);
        ui.label(RichText::new("一键处理").strong());
        // 卡键急救：任何键"粘住"（Ctrl 一直按住、全键盘变快捷键）点这里
        if ui
            .add(
                egui::Button::new(
                    RichText::new("⚠ 清除卡键（键粘住时点这里）")
                        .color(Color32::from_rgb(255, 180, 80)),
                )
                .min_size(egui::vec2(240.0, 28.0)),
            )
            .on_hover_text("向虚拟键盘发送\"全部松开\"，解除 Ctrl/Shift 等卡住的状态")
            .clicked()
        {
            if let Some(sh) = backend::shared() {
                if let Ok(mut b) = sh.lock() {
                    b.release_all_held();
                    // 再补一份全空报告，确保虚拟键盘里没有任何键按着
                    let _ = b.macro_send([0u8; 8]);
                }
            }
            self.say("已发送「全部松开」——若仍卡住，请按一下物理 Ctrl 键或重启程序");
        }
        ui.add_space(4.0);
        if ui
            .add(
                egui::Button::new(
                    RichText::new("⚙ 一键修复（缺什么补什么）")
                        .color(Color32::from_rgb(120, 200, 255)),
                )
                .min_size(egui::vec2(240.0, 28.0)),
            )
            .on_hover_text("检测 5 项，自动安装缺的组件（会弹 UAC 提权框）")
            .clicked()
        {
            self.say("开始一键修复…（可能弹 UAC 提权框，请允许）");
            let r = crate::commands::repair_all();
            let msg = parse_msg(&r);
            self.say(msg);
        }
        ui.add_space(6.0);
        ui.horizontal(|ui| {
            if ui
                .add_enabled(!self.installing, egui::Button::new("① 设为默认麦克风"))
                .clicked()
            {
                let r = crate::commands::set_default_mic();
                crate::commands::invalidate_probes();   // 立刻重新探测，不等缓存过期
                self.say(parse_msg(&r));
            }
            if ui
                .add_enabled(!self.installing, egui::Button::new("② 安装/修复虚拟声卡"))
                .clicked()
            {
                self.installing = true;
                self.install_msg = "正在安装 VB-CABLE…".into();
                let tx = self.install_msg.clone();
                let _ = tx;
                // 安装要下载+跑安装程序，放后台线程，别卡住界面
                self.say("开始安装 VB-CABLE（会弹安装向导，约 1 分钟）");
                std::thread::spawn(|| {
                    let r = crate::installer::install_cable();
                    eprintln!("[安装] {r}");
                    crate::commands::invalidate_probes();   // 装完立刻让界面重探
                });
            }
            if ui
                .add_enabled(!self.installing, egui::Button::new("③ 安装虚拟键盘驱动"))
                .clicked()
            {
                self.say("开始安装 WinUHid 驱动…");
                std::thread::spawn(|| {
                    let r = crate::installer::install_winuhid();
                    eprintln!("[安装] {r}");
                    crate::commands::invalidate_probes();
                });
            }
        });
        ui.add_space(4.0);
        // ④ 改造遥控器：换机器 / 换遥控器后必做（不做就按键出字母）。
        //   调系统自带的「更新驱动」把 HID 接口换成 HID-compliant device（null service），
        //   与设备管理器里"从列表选取"那一步等价。需要管理员权限（本程序本来就是管理员运行）。
        if ui
            .add_enabled(!self.installing, egui::Button::new("④ 改造遥控器（不再打字母）"))
            .on_hover_text(
                "把遥控器的 HID 接口从「键盘」换成 HID-compliant device：\n系统不再把它的按键当键盘输入（不打字母），\n而蓝牙链路照常建立，程序仍能读到原始报文。",
            )
            .clicked()
        {
            self.say("正在改造遥控器…（改完请再按一下任意键确认）");
            let r = crate::commands::convert_remote_hid();
            crate::commands::invalidate_probes();
            self.say(parse_msg(&r));
        }
        if self.installing {
            ui.add_space(6.0);
            ui.label(RichText::new(&self.install_msg).color(Color32::from_rgb(120, 200, 255)));
        }
        ui.add_space(10.0);
        ui.label(
            RichText::new(
                "说明：①②③ 都需要管理员权限。装完会立即重新检测；其余时间每 5 秒自动刷新一次（枚举音频设备较重，不做实时）。",
            )
            .small()
            .weak(),
        );

        ui.add_space(14.0);
        ui.separator();
        ui.add_space(4.0);
        // ── 运行日志开关 ──
        let mut logs = crate::settings::log_enabled();
        if ui
            .checkbox(&mut logs, "保存运行日志（排查问题时需要，平时可关）")
            .on_hover_text("关闭后会**立即删除**已有日志，之后不再写任何日志文件")
            .changed()
        {
            crate::settings::set_log_enabled(logs);
            if logs {
                crate::LOGS_ON.store(true, std::sync::atomic::Ordering::Relaxed);
                self.say("已开启日志。注意：本次运行仍不写（标准输出已指向空设备），**重启程序后**才会真正记录。");
            } else {
                crate::LOGS_ON.store(false, std::sync::atomic::Ordering::Relaxed);
                crate::purge_logs();
                crate::silence_stdio();
                self.say("已关闭日志，历史日志已删除，之后不再写任何日志文件。");
            }
        }

        ui.add_space(10.0);
        ui.separator();
        ui.add_space(4.0);
        let danger = Color32::from_rgb(230, 100, 100);
        if !self.confirm_uninstall {
            if ui
                .add(egui::Button::new(RichText::new("卸载本程序").color(danger)))
                .clicked()
            {
                self.confirm_uninstall = true;
            }
            ui.label(
                RichText::new("绿色版没有安装程序，卸载由程序自己完成（删目录 + 快捷方式 + 自启项）")
                    .small()
                    .weak(),
            );
        } else {
            ui.label(
                RichText::new("确认卸载？将删除整个程序目录、桌面快捷方式和开机自启项。")
                    .color(danger)
                    .strong(),
            );
            ui.horizontal(|ui| {
                if ui
                    .add(egui::Button::new(RichText::new("确认卸载").color(danger)).min_size(egui::vec2(96.0, 26.0)))
                    .clicked()
                {
                    if let Some(dir) = crate::uninstall::current_dir() {
                        match crate::uninstall::uninstall(&dir) {
                            Ok(()) => std::process::exit(0),
                            Err(e) => eprintln!("[卸载] {e}"),
                        }
                    }
                }
                if ui.button("取消").clicked() {
                    self.confirm_uninstall = false;
                }
            });
        }
    }
}

// （已删）`trim_working_set()`：收进托盘不再"缩窗口 + 换出工作集"，而是
//   直接结束 UI 事件循环（GL 上下文与字体 atlas 随之销毁，内存真正交还系统）。
//   换工作集只是把物理页挪到页面文件，**私有提交量不变**，任务管理器里看不出改善。

/// 启动参数 `--tab keys|ime|text|macro|setup` 指定初始页（自测截图用）
fn initial_tab() -> Tab {
    for (i, a) in std::env::args().enumerate() {
        if a == "--tab" {
            if let Some(v) = std::env::args().nth(i + 1) {
                return match v.as_str() {
                    "ime" => Tab::Ime,
                    "text" => Tab::Text,
                    "macro" => Tab::Macro,
                    "setup" => Tab::Setup,
                    _ => Tab::Keys,
                };
            }
        }
    }
    Tab::Keys
}

fn check_row(ui: &mut egui::Ui, ok: bool, name: &str, detail: &str) {
    ui.horizontal(|ui| {
        // ⚠ 不能用 ✓(U+2713) / ✗(U+2717)：微软雅黑里没有这两个字形，会渲染成方框。
        //   √(U+221A) 和 ×(U+00D7) 在 GB2312 里，稳定显示。
        let (mark, color) = if ok {
            ("√", Color32::from_rgb(80, 200, 120))
        } else {
            ("×", Color32::from_rgb(230, 100, 100))
        };
        ui.label(RichText::new(mark).color(color).strong());
        ui.add_sized([230.0, 18.0], egui::Label::new(name));
        ui.label(RichText::new(detail).weak());
    });
}

fn switch_group(id: u8) {
    if let Some(sh) = backend::shared() {
        if let Ok(mut b) = sh.lock() {
            b.release_all_held();
        }
    }
    settings::set_active_group(id);
    keymap::load_group(&settings::bindings_of(id));
}

fn parse_msg(json: &str) -> String {
    serde_json::from_str::<serde_json::Value>(json)
        .ok()
        .and_then(|v| v["message"].as_str().map(|s| s.to_string()))
        .unwrap_or_else(|| "完成".to_string())
}

/// 某个键当前绑定的可读文案（含四类：VK / 鼠标动作 / 文本槽 / 宏）
fn binding_label(id: &str, usage: u8, mouse_group: bool) -> String {
    // 鼠标模式下的方向键：不绑也有固定语义
    if mouse_group && matches!(usage, 0x52 | 0x51 | 0x50 | 0x4F) {
        let raw = settings::bindings().get(id).cloned().unwrap_or_default();
        if raw.trim().is_empty() {
            return "移动光标".to_string();
        }
    }
    // 先看"有没有自定义绑定"（负数 = 动作/文本/宏；正数串 = VK 列表）
    if let Some(raw) = settings::bindings().get(id) {
        let v = raw.trim();
        if let Ok(n) = v.parse::<i64>() {
            if n < 0 {
                if (-6..=-1).contains(&n) {
                    return format!("{}（动作）", mouse_act::action_name(n as i32));
                }
                if (-1010..=-1001).contains(&n) {
                    let slot = (-1000 - n) as u32;
                    let chars = text_slot::get(slot).map(|t| t.text.chars().count()).unwrap_or(0);
                    return if chars == 0 {
                        format!("文本槽 {slot}（空）")
                    } else {
                        format!("文本槽 {slot}（{chars} 字）")
                    };
                }
                if (-1999..=-1101).contains(&n) {
                    let num = (-1100 - n) as u32;
                    return match macro_store::get(num) {
                        Some(m) => format!("宏 {}（{} 步）", num, m.steps.len()),
                        None => format!("宏 {num}（不存在）"),
                    };
                }
            }
        }
    }
    // 否则按 VK 列表（用户覆盖 > 出厂默认）
    let vks = keymap::vks_of(usage);
    if vks.is_empty() {
        "未设置".to_string()
    } else {
        vks.iter().map(|v| keymap::vk_name(*v)).collect::<Vec<_>>().join("+")
    }
}
