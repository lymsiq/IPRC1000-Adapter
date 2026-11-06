//! 环境体检（`IPRC1000-Native.exe --doctor [输出路径]`）
//!
//! ## 为什么需要它
//!
//! 2026-11-06 一整天里，最大的时间浪费不是环境本身，而是**程序说了结论但没说依据**：
//!
//! ```text
//! 环境检查：「已改绑 ⇒ 按键可读」   ← 假的，实际收不到
//! 状态栏：「按键通道未就绪」        ← 真的
//! ```
//!
//! 两句打架，人只能靠猜。所以这个命令**强制每一项都给三样**：
//!
//! ```text
//! 实测值   —— 到底探到了什么
//! 判据     —— 凭什么这么判断
//! 修法     —— 不符合时该做什么
//! ```
//!
//! ## 设计约束
//!
//! · **不启动 backend**：纯静态探测 + 读上次的 `bridge-stats.json`。
//!   这样它在任何状态下都能跑，不会因为"程序正在跑"而互相干扰。
//! · **必须能落盘**：release 是 `windows_subsystem = "windows"`，没有控制台，
//!   `println!` 全丢。所以默认写文件并弹窗告诉路径。
//! · **失败不退出**：某一项探测失败就记 `跳过`，继续跑下一项。

use std::fmt::Write as _;
use std::path::PathBuf;

#[derive(Clone, Copy, PartialEq)]
pub enum Verdict {
    Ok,
    Warn,
    Fail,
    Skip,
}

impl Verdict {
    fn tag(self) -> &'static str {
        match self {
            Verdict::Ok => "OK  ",
            Verdict::Warn => "警告",
            Verdict::Fail => "失败",
            Verdict::Skip => "跳过",
        }
    }
}

pub struct Item {
    pub no: usize,
    pub title: &'static str,
    /// 实测到的值（可以多行，用 `\n` 分隔）
    pub value: String,
    pub verdict: Verdict,
    /// 判据：凭什么这么判断
    pub note: &'static str,
    /// 不符时怎么修
    pub fix: &'static str,
}

fn item(no: usize, title: &'static str, value: String, note: &'static str, fix: &'static str) -> Item {
    Item { no, title, value, verdict: Verdict::Skip, note, fix }
}

/// 读 Windows build 号（判 Win10/Win11 用）
fn read_build() -> Option<u32> {
    use windows::core::PCWSTR;
    use windows::Win32::System::Registry::*;
    unsafe {
        let mut h = HKEY::default();
        let path: Vec<u16> = "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\0"
            .encode_utf16()
            .collect();
        if RegOpenKeyExW(HKEY_LOCAL_MACHINE, PCWSTR(path.as_ptr()), 0, KEY_READ, &mut h)
            != windows::Win32::Foundation::ERROR_SUCCESS
        {
            return None;
        }
        let name: Vec<u16> = "CurrentBuildNumber\0".encode_utf16().collect();
        let mut buf = [0u16; 64];
        let mut len = (buf.len() * 2) as u32;
        let mut ty = REG_NONE;
        let r = RegQueryValueExW(
            h,
            PCWSTR(name.as_ptr()),
            None,
            Some(&mut ty),
            Some(buf.as_mut_ptr() as *mut u8),
            Some(&mut len),
        );
        let _ = RegCloseKey(h);
        if r != windows::Win32::Foundation::ERROR_SUCCESS {
            return None;
        }
        let n = (len as usize / 2).saturating_sub(1);
        String::from_utf16_lossy(&buf[..n]).trim_matches('\0').parse().ok()
    }
}

/// 读上次运行写的 `bridge-stats.json`（backend 没跑过就没有）
fn read_stats() -> Option<serde_json::Value> {
    let p = crate::paths::logs_dir().join("bridge-stats.json");
    let txt = std::fs::read_to_string(p).ok()?;
    serde_json::from_str(&txt).ok()
}

pub fn collect() -> Vec<Item> {
    let mut v: Vec<Item> = Vec::new();
    let mut no = 0usize;
    let mut next = || {
        no += 1;
        no
    };

    // ── 1. 系统 ──
    let build = read_build();
    let admin = crate::elevate::is_admin();
    let mut it = item(
        next(),
        "系统",
        format!(
            "架构 {} · build {} · 管理员 {}",
            std::env::consts::ARCH,
            build.map(|b| b.to_string()).unwrap_or_else(|| "未知".into()),
            admin
        ),
        "Win10 1809+ / Win11；ETW 与驱动安装需要管理员，按键走 Raw Input 不需要",
        "非管理员时：按键可用但没有语音；需要音频时右键「以管理员身份运行」",
    );
    it.verdict = if build.is_none() {
        Verdict::Warn
    } else if !admin {
        Verdict::Warn
    } else {
        Verdict::Ok
    };
    v.push(it);

    // ── 2. BTHENUM HID 服务节点 ──
    // ⚠ `bt_nodes()` 返回的 `path` 是**实例名**（如 `9&378511c7&3&0000`），
    //   服务 GUID 在父键里，所以不能拿它 contains("00001124") 判断。
    //   存在性改用 `bt_hid_bound()` —— 它就是专门查 {00001124} 的 Service 的。
    let bt = crate::device_probe::bt_nodes();
    let bound = crate::device_probe::bt_hid_bound();
    let has_hid_svc = bound.is_some();
    let mut it = item(
        next(),
        "HID 服务节点",
        if !has_hid_svc {
            "未找到 Enum\\BTHENUM\\{00001124…} 节点".to_string()
        } else if bt.is_empty() {
            "节点存在，但枚举不到实例".to_string()
        } else {
            format!(
                "{} 个服务实例（含 {{00001000}}/{{00001124}}/{{00001200}}）：\n{}",
                bt.len(),
                bt.iter()
                    .map(|n| format!("    {}", n.path))
                    .collect::<Vec<_>>()
                    .join("\n")
            )
        },
        "遥控器必须有一条 {00001124} HID 服务节点，否则主机不会建 HID 中断通道",
        "未配对或节点被删：设置→蓝牙→删除设备，再重新配对（配对全程按住按键）",
    );
    it.verdict = if has_hid_svc { Verdict::Ok } else { Verdict::Fail };
    v.push(it);

    // ── 3. HID 服务是否绑定 HidBth ──
    let mut it = item(
        next(),
        "HID 服务绑定",
        match bound {
            Some(true) => "Service = HidBth".to_string(),
            Some(false) => "节点存在但 Service 不是 HidBth".to_string(),
            None => "找不到 {00001124} 节点，无法判断".to_string(),
        },
        "只有 Service=HidBth 时，主机的 PSM 0x0011/0x0013 才有监听者",
        "Service 不对通常是驱动没绑上：重启一次蓝牙适配器（禁用→启用）",
    );
    it.verdict = match bound {
        Some(true) => Verdict::Ok,
        Some(false) => Verdict::Fail,
        None => Verdict::Fail,
    };
    v.push(it);

    // ── 4. HID 子节点（决定按键能不能读到） ──
    let insts = crate::device_probe::remote_hid_instances();
    let kb = insts.iter().filter(|i| i.is_keyboard).count();
    // 身份来源：现在按**蓝牙设备名**发现，不再依赖写死 VID/PID
    let who = match crate::device_id::get() {
        Some(id) => format!("按名称发现：{}（{}）VID=0x{:04X} PID=0x{:04X}", id.name, id.mac, id.vid, id.pid),
        None => format!(
            "未按名称发现（用兜底 VID=0x{:04X} PID=0x{:04X}），可能是设备未配对",
            crate::device_id::FALLBACK_VID,
            crate::device_id::FALLBACK_PID
        ),
    };
    let mut it = item(
        next(),
        "HID 子节点",
        if insts.is_empty() {
            format!("{who}\n0 个（遥控器未连接，或节点被删）")
        } else {
            format!(
                "{who}\n{} 个，其中绑键盘驱动 {} 个：\n{}",
                insts.len(),
                kb,
                insts
                    .iter()
                    .map(|i| format!(
                        "    {} {}",
                        if i.is_keyboard { "kbdhid  " } else { "非键盘 " },
                        i.full_id
                    ))
                    .collect::<Vec<_>>()
                    .join("\n")
            )
        },
        "绑 kbdhid ⇒ 按键能被读（代价：会打字符）；绑空/非键盘 ⇒ Raw Input 里不会有它",
        "0 个：按遥控器任意键唤醒；仍无则删除设备重新配对。有节点但非键盘：这就是改绑残留，必须重配对",
    );
    it.verdict = if insts.is_empty() {
        Verdict::Fail
    } else if kb == 0 {
        Verdict::Fail
    } else {
        Verdict::Ok
    };
    v.push(it);

    // ── 5. 按键通道（运行时，来自上次的 stats） ──
    let stats = read_stats();
    let (key_events, hid_miss, hid_reports) = match &stats {
        Some(s) => (
            s["keyEvents"].as_u64().unwrap_or(0),
            s["hidMiss"].as_u64().unwrap_or(0),
            s["hidReports"].as_u64().unwrap_or(0),
        ),
        None => (0, 0, 0),
    };
    let mut it = item(
        next(),
        "按键通道（上次运行）",
        match &stats {
            None => "没有 bridge-stats.json（程序还没跑过）".to_string(),
            Some(_) => format!(
                "keyEvents={key_events} · hidReports={hid_reports} · hidMiss={hid_miss}"
            ),
        },
        "keyEvents 随按键增长 = 通；hidMiss 一直涨 = 在反复打开不存在的接口",
        "keyEvents 为 0 且 hidMiss 在涨 ⇒ 问题在枚举/认领层，不是键表。看上面第 4 项",
    );
    it.verdict = match &stats {
        None => Verdict::Skip,
        Some(_) if key_events > 0 => Verdict::Ok,
        Some(_) if hid_miss > 0 => Verdict::Fail,
        Some(_) => Verdict::Warn,
    };
    v.push(it);

    // ── 6. 注入 ──
    let uh = crate::device_probe::winuhid_driver_installed();
    let mut it = item(
        next(),
        "按键注入",
        format!("SendInput 恒可用 · WinUHid 驱动{}", if uh { "已安装" } else { "未安装" }),
        "SendInput 零依赖、不需要驱动；WinUHid 只在需要「被识别为真实键盘」时才装",
        "不需要处理。WinUHid 未安装不影响按键（0.4.46 起已改为 SendInput 优先）",
    );
    it.verdict = Verdict::Ok;
    v.push(it);

    // ── 7. 音频端点 ──
    let cin = crate::device_probe::audio_endpoint_present("CABLE Input");
    let cout = crate::device_probe::audio_endpoint_present("CABLE Output");
    let mut it = item(
        next(),
        "VB-CABLE 端点",
        format!(
            "CABLE Input = {} · CABLE Output = {}",
            if cin { "在" } else { "缺" },
            if cout { "在" } else { "缺" }
        ),
        "两个都要在：程序写 CABLE Input，输入法读 CABLE Output。选反了会「有声音没文字」",
        "缺任一个：装 VB-CABLE（安装包 assets 里自带），装完重启电脑",
    );
    it.verdict = if cin && cout {
        Verdict::Ok
    } else if cin || cout {
        Verdict::Warn
    } else {
        Verdict::Fail
    };
    v.push(it);

    // ── 8. VB-CABLE 驱动健康 ──
    let code = crate::device_probe::vbaudio_problem_code();
    let mut it = item(
        next(),
        "VB-CABLE 驱动状态",
        match code {
            Some(0) => "问题码 0（健康）".to_string(),
            Some(n) => format!("问题码 {n}（装了但驱动有问题）"),
            None => "设备不存在".to_string(),
        },
        "问题码 0 = 正常；非 0 = 设备管理器里带黄色感叹号，端点会在但打不开",
        "非 0：设备管理器卸载 VB-CABLE → 重新安装 → 重启",
    );
    it.verdict = match code {
        Some(0) => Verdict::Ok,
        Some(_) => Verdict::Fail,
        None => Verdict::Fail,
    };
    v.push(it);

    // ── 9. 输入法配置 ──
    let ime = crate::settings::ime();
    let ime_label = match ime.as_str() {
        "doubao" => "豆包（RAlt 按住）",
        "wechat" => "微信（Ctrl+Win 按住）",
        other => other,
    };
    let mut it = item(
        next(),
        "输入法配置",
        format!("当前档位：{ime_label}"),
        "档位决定程序模拟哪个语音快捷键；快捷键随输入法版本可能变",
        "唤起异常时：先在记事本用物理键盘试该快捷键，能唤起才说明是程序侧问题",
    );
    it.verdict = Verdict::Ok;
    v.push(it);

    // ── 10. 蓝牙省电（换机器必查） ──
    let findings = crate::power::audit();
    let bad: Vec<_> = findings.iter().filter(|f| !f.ok()).collect();
    let mut it = item(
        next(),
        "蓝牙省电",
        if findings.is_empty() {
            "没找到蓝牙适配器 / HID 设备".to_string()
        } else if bad.is_empty() {
            format!("{} 项开关全为 0，空闲不会挂起", findings.len())
        } else {
            format!(
                "{} 项中 {} 项未关：\n{}",
                findings.len(),
                bad.len(),
                bad.iter()
                    .map(|f| format!("    {} / {} = {}", f.device, f.switch, f.describe()))
                    .collect::<Vec<_>>()
                    .join("\n")
            )
        },
        "空闲断链的根因：适配器 EnhancedPowerManagement / SelectiveSuspend 会挂起设备，蓝牙 HID 挂起后不保证自动重连",
        "管理员运行 `IPRC1000-Native.exe --repair` 会自动写成 0；或设备管理器里取消「允许计算机关闭此设备以节约电源」",
    );
    it.verdict = if findings.is_empty() {
        Verdict::Skip
    } else if bad.is_empty() {
        Verdict::Ok
    } else {
        Verdict::Warn
    };
    v.push(it);

    v
}

pub fn render(items: &[Item]) -> String {
    let mut s = String::new();
    let now = {
        // 只取本地时分，够用
        let t = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map(|d| d.as_secs())
            .unwrap_or(0);
        let secs = (t + 8 * 3600) % 86400; // CST，仅用于显示
        format!("{:02}:{:02}:{:02}", secs / 3600, (secs % 3600) / 60, secs % 60)
    };
    let _ = writeln!(s, "=== IPRC1000 环境体检（{now}）===\n");

    for it in items {
        let _ = writeln!(s, "[{}] {}  —— {}", it.no, it.title, it.verdict.tag());
        for line in it.value.lines() {
            let _ = writeln!(s, "    {line}");
        }
        let _ = writeln!(s, "    判据：{}", it.note);
        if it.verdict != Verdict::Ok && !it.fix.is_empty() {
            let _ = writeln!(s, "    修法：{}", it.fix);
        }
        s.push('\n');
    }

    let ok = items.iter().filter(|i| i.verdict == Verdict::Ok).count();
    let warn = items.iter().filter(|i| i.verdict == Verdict::Warn).count();
    let fail = items.iter().filter(|i| i.verdict == Verdict::Fail).count();
    let skip = items.iter().filter(|i| i.verdict == Verdict::Skip).count();
    let _ = writeln!(
        s,
        "=== 汇总：{ok} 通过 / {warn} 警告 / {fail} 失败 / {skip} 跳过 ==="
    );
    if fail == 0 && warn == 0 {
        let _ = writeln!(s, "环境就绪。");
    } else {
        let _ = writeln!(s, "按上面各项的「修法」逐条处理。");
    }
    s
}

/// 跑体检并落盘。返回 (报告正文, 落盘路径)。
pub fn run(out: Option<&str>) -> (String, PathBuf) {
    let items = collect();
    let text = render(&items);
    let path = match out {
        Some(p) => PathBuf::from(p),
        None => crate::paths::logs_dir().join("doctor.txt"),
    };
    if let Some(dir) = path.parent() {
        let _ = std::fs::create_dir_all(dir);
    }
    if let Err(e) = std::fs::write(&path, text.as_bytes()) {
        eprintln!("[doctor] 写文件失败 {}：{e}", path.display());
    }
    (text, path)
}
