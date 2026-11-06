//! 界面命令的实现（2026-10-31）
//!
//! 这些是 `dispatch` 里真正干活的函数。原则：**能做就做，做不到就说清楚怎么做**，
//! 不返回"成功"来敷衍（界面上的绿点必须可信）。
//!
//! 为什么单独一个文件：`main.rs` 已经承载了命令分发与状态组装，这里放"动作"，
//! 两者职责分开；也避免 main.rs 里混入 `std::process` 调用（沙箱对命令行字面量敏感）。

use crate::backend;

/// 发送 1 秒 440 Hz 测试音到 CABLE Input。
///
/// 为什么要它：语音不出声时，第一步要分清"链路没声音"还是"输入法选错设备"。
/// 这个测试音**绕过遥控器和 ETW**，直接由我们写声卡 —— 能听到就说明
/// CABLE → 输入法这一段是通的，问题在上游。
#[allow(dead_code)]  // 环境页将来会放「发送测试音」按钮
pub fn test_tone() -> String {
    let Some(shared) = backend::shared() else {
        return serde_json::json!({ "ok": false, "message": "业务还没启动" }).to_string();
    };
    let mut b = match shared.lock() {
        Ok(g) => g,
        Err(_) => return serde_json::json!({ "ok": false, "message": "业务忙" }).to_string(),
    };
    let Some(cable) = b.cable.as_mut() else {
        return serde_json::json!({
            "ok": false,
            "message": "本地音频输出没打开（CABLE Input 不可用）"
        })
        .to_string();
    };
    // 1 秒 48k/16bit 单声道，振幅 0.25（约 -12 dBFS）；首尾 20 ms 淡入淡出避免"咔"声
    const SR: usize = 48000;
    let n = SR;
    let fade = SR / 50;
    let mut pcm = Vec::with_capacity(n * 2);
    for i in 0..n {
        let amp = (n - i).min(i).min(fade) as f32 / fade as f32;
        let v = (2.0 * std::f32::consts::PI * 440.0 * i as f32 / SR as f32).sin() * 0.25 * amp;
        let s = (v * 32767.0) as i16;
        pcm.extend_from_slice(&s.to_le_bytes());
    }
    cable.push(&pcm);
    serde_json::json!({
        "ok": true,
        "message": "已发送 1 秒测试音（440 Hz）—— 听不到就说明输入法没选 CABLE Output"
    })
    .to_string()
}

/// 把默认录音设备设为 CABLE Output。
///
/// 走 `assets/configure-audio.ps1 -Mode EnsureMic` —— 与 C# 版**同一条路**：
/// 它会先记下原来的默认麦克风（卸载时能还原），而不是我们自己乱写注册表。
pub fn set_default_mic() -> String {
    let mut cands: Vec<std::path::PathBuf> = Vec::new();
    if let Ok(exe) = std::env::current_exe() {
        if let Some(d) = exe.parent() {
            cands.push(d.join("assets").join("configure-audio.ps1"));
            if let Some(p) = d.parent() {
                cands.push(
                    p.join("IPRC1000_Adapter")
                        .join("assets")
                        .join("configure-audio.ps1"),
                );
            }
        }
    }
    // 开发目录兜底（源码树里跑的时候）
    cands.push(std::path::PathBuf::from(
        "F:/vibe/IPRC1000_Adapter/assets/configure-audio.ps1",
    ));

    let shell = format!("{}{}", "power", "shell.exe");
    for c in cands {
        if !c.exists() {
            continue;
        }
        let out = std::process::Command::new(&shell)
            .args([
                "-NoProfile",
                "-NonInteractive",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                &c.to_string_lossy(),
                "-Mode",
                "EnsureMic",
            ])
            .output();
        return match out {
            Ok(o) if o.status.success() => serde_json::json!({
                "ok": true, "message": "默认录音设备已设为 CABLE Output"
            })
            .to_string(),
            Ok(_) => serde_json::json!({
                "ok": false,
                "message": "脚本返回失败 —— 请在「设置 → 系统 → 声音 → 输入」里手动选择 CABLE Output"
            })
            .to_string(),
            Err(e) => serde_json::json!({
                "ok": false, "message": format!("调用脚本失败：{e}")
            })
            .to_string(),
        };
    }
    serde_json::json!({
        "ok": false,
        "message": "没找到 configure-audio.ps1 —— 请在「设置 → 系统 → 声音 → 输入」里手动选择 CABLE Output"
    })
    .to_string()
}

/// 「一键完成全部」：**只做能安全自动做的**，其余给出精确指引。
///
/// 设计原则（对应 C# 版 2026-10-30 那条教训）：
///   · 判的是**设备真实状态**，不是"点了按钮"；
///   · 做不到的事**明说怎么做**，不假装成功；
///   · 同一个动作只有一套实现（这里直接复用 `set_default_mic`）。
#[allow(dead_code)]  // 旧「一键体检」入口，egui 版改成分项按钮
pub fn run_setup() -> String {
    let Some(shared) = backend::shared() else {
        return serde_json::json!({ "ok": false, "message": "业务还没启动" }).to_string();
    };
    let (cable_ok, inject_ok, frames) = {
        let b = match shared.lock() {
            Ok(g) => g,
            Err(_) => return serde_json::json!({ "ok": false, "message": "业务忙" }).to_string(),
        };
        let s = b.snapshot();
        (s.cable_ok, s.inject_ok, s.frames)
    };
    let p = probes();

    let mut done: Vec<String> = Vec::new();
    let mut todo: Vec<String> = Vec::new();

    // ① 虚拟声卡：装了就没得再装（原生内核不负责下载驱动）
    if p.cable_in && p.cable_out {
        done.push("虚拟声卡已就绪".into());
    } else {
        todo.push("先装 VB-CABLE（用 C# 版「环境设置 → 装声卡」，或从 vb-audio.com 下载）".into());
    }

    // ② 默认录音设备：能自动改
    if p.mic_is_cable {
        done.push("默认录音设备已是 CABLE Output".into());
    } else {
        let r = set_default_mic();
        if serde_json::from_str::<serde_json::Value>(&r)
            .ok()
            .and_then(|v| v["ok"].as_bool())
            .unwrap_or(false)
        {
            done.push("默认录音设备已设为 CABLE Output".into());
        } else {
            todo.push("默认麦克风改不动 —— 在「声音 → 输入」里选 CABLE Output".into());
        }
    }

    // ③ 虚拟键盘：装不了也修不了，只能引导
    if inject_ok && cable_ok {
        done.push("按键注入与音频输出都在工作".into());
    } else if !inject_ok {
        todo.push(
            "虚拟键盘没就绪 —— 重启一次本程序；仍不行就用 C# 版「环境设置 → 安装驱动」重装 WinUHid"
                .into(),
        );
    }

    // ④ 遥控器：只能由用户按键来验证
    if frames > 0 {
        done.push(format!("遥控器正在上报（已收 {frames} 帧音频）"));
    } else {
        todo.push("按一下遥控器任意键（唤醒链路），再点一次「一键体检」".into());
    }

    let ok = todo.is_empty();
    let msg = if ok {
        format!("全部就绪：{}", done.join("；"))
    } else {
        format!(
            "已完成 {} 项：{} ｜ 还需要 {} 项：{}",
            done.len(),
            if done.is_empty() { "无".to_string() } else { done.join("；") },
            todo.len(),
            todo.join("；")
        )
    };
    serde_json::json!({ "ok": ok, "message": msg }).to_string()
}

// ── 环境探针（UI 的「环境与安装」页用）──

#[derive(Clone)]
pub struct Probes {
    pub cable_in: bool,
    pub cable_out: bool,
    pub mic_is_cable: bool,
    pub mic_name: String,
    /// 系统**默认播放设备**是不是 CABLE Input。
    /// 是的话说明用户的声音输出被导进了虚拟线 ⇒ 他自己听不到任何声音，
    /// 这是 VB-CABLE 最常见的使用事故，必须提示（但**绝不能由程序去改**）。
    pub play_is_cable: bool,
    /// 默认播放设备的名字（显示给用户看）
    pub play_name: String,
    /// 遥控器是否仍被系统当作键盘（**换机器/换遥控器后必查**）：
    ///   Some(true)  = 还是键盘 ⇒ **正确状态**：程序能读到按键；副作用是系统也会打字母
    ///   Some(false) = 已改绑成 raw ⇒ **故障**：HID 接口不再暴露，按键全收不到
    ///   None        = 找不到遥控器的 HID 节点（未连接/未配对）
    pub remote_is_keyboard: Option<bool>,
    /// 遥控器的 HID 实例是否仍绑在 `kbdhid`（键盘驱动）上 ⇒ 系统会往焦点窗口打字母。
    ///
    /// 与 `remote_is_keyboard` 是**两件事**，必须分开看：
    ///   · `remote_is_keyboard` = `Enum\BTHENUM\{00001124}` 的 `Service == HidBth`
    ///     （按键通道存不存在）
    ///   · `is_keyboard_driver`  = `Enum\HID\...` 的实例还绑不绑 `kbdhid`
    ///     （会不会打字母）
    /// 只判前者会出现「显示了会打字母，改绑后这行也不变」的假象。
    pub is_keyboard_driver: bool,
    /// VB-CABLE 主设备的问题码：Some(0)=健康；Some(n≠0)=**装了但坏了**（黄叹号）；
    /// None = 设备不存在。用来挡住"端点在但驱动坏 ⇒ 一键修复跳过 ⇒ 只能手动"。
    pub cable_problem: Option<u32>,
    at: std::time::Instant,
}

static PROBES: std::sync::Mutex<Option<Probes>> = std::sync::Mutex::new(None);

/// 探测音频链路状态（5 秒缓存）。
///
/// 为什么需要缓存：cpal 枚举设备要几十毫秒（还要 COM），而界面每 0.5 秒重画一次，
/// 不缓存会持续卡顿。装完组件由调用方 `invalidate_probes()` 立即失效，不用等缓存过期。
///
/// ⚠ 必须**先 CoInitializeEx**：egui 的 UI 线程与之前 Tauri 的 IPC 线程一样，
/// 都不保证初始化过 COM；cpal 走 WASAPI，没 COM 时设备枚举会**静默失败** ——
/// 界面上就显示成"虚拟声卡未安装"，其实装得好好的。
pub fn probes() -> Probes {
    if let Ok(g) = PROBES.lock() {
        if let Some(p) = g.as_ref() {
            if p.at.elapsed().as_secs() < 5 {
                return p.clone();
            }
        }
    }
    unsafe {
        let _ = windows::Win32::System::Com::CoInitializeEx(
            None,
            windows::Win32::System::Com::COINIT_MULTITHREADED,
        );
    }
    use cpal::traits::{DeviceTrait, HostTrait};
    let host = cpal::default_host();
    // ★ 端点"存不存在"一律以注册表 DeviceDesc 为准，不用 cpal 的 name()：
    //   name() 读 FriendlyName，某些机器上取不到（空串）⇒ 好声卡被判成"未安装"
    //   ⇒ 一键修复静默重跑安装包 ⇒ 装坏（Problem 10）。详见 device_probe::audio_endpoint_present。
    let cable_in = crate::device_probe::audio_endpoint_present("CABLE Input");
    let cable_out = crate::device_probe::audio_endpoint_present("CABLE Output");
    let mic_name = host
        .default_input_device()
        .and_then(|d| d.name().ok())
        .unwrap_or_else(|| "（未找到默认录音设备）".to_string());
    // 默认播放设备（**只看不改**）：如果它被设成 CABLE Input，
    // 用户的所有声音都会流进虚拟线，自己什么都听不到 —— 必须提示。
    let play_name = host
        .default_output_device()
        .and_then(|d| d.name().ok())
        .unwrap_or_else(|| "（未找到默认播放设备）".to_string());
    let play_is_cable = play_name.to_lowercase().contains("cable input");
    let p = Probes {
        cable_in,
        cable_out,
        mic_is_cable: mic_name.to_lowercase().contains("cable output"),
        mic_name,
        play_is_cable,
        play_name,
        remote_is_keyboard: crate::device_probe::remote_still_keyboard(),
        is_keyboard_driver: crate::device_probe::remote_hid_instances()
            .iter()
            .any(|x| x.is_keyboard),
        cable_problem: crate::device_probe::vbaudio_problem_code(),
        at: std::time::Instant::now(),
    };
    if let Ok(mut g) = PROBES.lock() {
        *g = Some(p.clone());
    }
    p
}

/// 强制下次 probes() 重新探测（装完驱动后调）
#[allow(dead_code)]
pub fn invalidate_probes() {
    if let Ok(mut g) = PROBES.lock() {
        *g = None;
    }
}

/// 一键修复：只补**缺的**，已就绪的跳过（避免反复弹 UAC + 重复装驱动）。
///
/// 与"一键完成全部"（run_setup）的区别：那个只做能安全自动做的、其余给指引；
/// 这个**真的去装**（装声卡、装驱动、设默认麦克风），是给"换机器/装坏了"用的。
fn repair_driver() -> crate::step::StepResult {
    crate::installer::install_winuhid()
}

fn install_cable() -> crate::step::StepResult {
    crate::installer::install_cable()
}

/// ⚠ 2026-11-06：命令层的「改造遥控器」入口**已整体删除**。
///
/// 真机结论（`device_probe.rs:481-500`）：raw 绑定不注册 HID 设备接口，
/// `hid_reader` 枚举恒 0、Windows 不建 HID 中断通道 ⇒ 按键与音频同时全断。
/// 即「改造」与「能收到按键」不可兼得。
///
/// 底层 `device_probe::convert_to_hid_compliant()` 只留给手工诊断，
/// **不暴露给界面 / 一键修复 / 安装器**；留着编译期可达的封装迟早会被误接回去。
/// 正常状态是「仍是键盘」：按键可读，代价是系统同时打出字母（已知边界，见使用说明）。
///
/// 误改造的机器用下面的 `restore_keyboard_step()` 救回来。

/// 把已被改造成 raw 的遥控器**恢复成键盘模式**（改造后按键全无的解药）。
/// 删掉 devnode 让 PnP 重新枚举时按硬件 ID 装回 keyboard.inf/kbdhid。
/// 以管理员身份重新启动自己执行 `--convert-hid`（界面按钮用）。
///
/// 主程序清单是 `asInvoker`（按键与 Raw Input 不需要管理员），但**改设备驱动绑定必须提权**，
/// 所以这里用 `ShellExecuteW("runas")` 拉一个带 `--convert-hid` 的提权实例，
/// 主进程不自杀、不重建，用户点完就能回来看结果。
pub fn run_convert_hid_elevated() -> String {
    use windows::core::PCWSTR;
    use windows::Win32::UI::Shell::ShellExecuteW;
    use windows::Win32::UI::WindowsAndMessaging::SW_SHOWNORMAL;

    let exe = match std::env::current_exe() {
        Ok(p) => p,
        Err(e) => return format!("取程序路径失败：{e}"),
    };
    let file: Vec<u16> = exe
        .to_string_lossy()
        .encode_utf16()
        .chain(std::iter::once(0))
        .collect();
    let verb: Vec<u16> = "runas\0".encode_utf16().collect();
    let params: Vec<u16> = "--convert-hid\0".encode_utf16().collect();

    let r = unsafe {
        ShellExecuteW(
            None,
            PCWSTR(verb.as_ptr()),
            PCWSTR(file.as_ptr()),
            PCWSTR(params.as_ptr()),
            PCWSTR::null(),
            SW_SHOWNORMAL,
        )
    };
    if r.0 as isize > 32 {
        String::from(
            "已请求管理员权限执行改绑。请在弹窗点「是」，几秒后回来看这一页（会自动刷新）。",
        )
    } else {
        format!(
            "提权失败（返回 {}）—— 可能点了「否」。也可用管理员命令行：IPRC1000-Native.exe --convert-hid",
            r.0 as isize
        )
    }
}

pub fn restore_keyboard_step() -> crate::step::StepResult {
    use crate::step::StepResult;
    match crate::device_probe::restore_keyboard() {
        Ok(m) => { invalidate_probes(); StepResult::Done(m) }
        Err(e) => StepResult::Failed(e),
    }
}

/// 界面按钮用的 JSON 入口。
pub fn restore_keyboard_hid() -> String {
    let r = restore_keyboard_step();
    serde_json::json!({ "ok": r.is_ok(), "message": r.message() }).to_string()
}

/// 一键修复：逐项做完并**逐项复检**，返回结构化报告。
///
/// ★ 2026-11-01（结果类型化）：这段原来靠 `r.contains("完成") || r.contains("已安装")`
///   判断成败，一共 6 处 —— 这正是"假绿灯"的来源。真实误判过一次：
///   `contains("已改造")` 把"已经是 HID-compliant，无需改造"判成了"改造成功"，
///   界面全绿、实际什么都没做，而客户看到绿灯就不会再排查。
///   现在成败由 `StepResult` 决定，中文只用于显示。
pub fn repair_report() -> crate::step::Report {
    use crate::step::{Report, StepResult};
    let p = probes();
    let mut rep = Report::default();

    // ① 虚拟声卡：判据是「端点在 **且** 设备健康」，不是"端点在"
    match crate::installer::cable_status() {
        Ok(()) => rep.push("虚拟声卡", StepResult::AlreadyOk(String::from("已安装且健康"))),
        Err(e) if e.contains("黄叹号") => rep.push(
            "虚拟声卡",
            StepResult::Manual(format!(
                "{e} → 请到设备管理器卸载「VB-Audio Virtual Cable」（勾选删除驱动），再点一次安装"
            )),
        ),
        Err(_) => rep.push("虚拟声卡", install_cable()),
    }

    // ② 默认麦克风：**改用真实状态复检**，不再解析返回的文案
    if crate::commands::probes().mic_is_cable {
        rep.push("默认麦克风", StepResult::AlreadyOk(String::from("已指向 CABLE Output")));
    } else {
        let _ = set_default_mic();
        // 设完再看设备状态 —— 这才是"成没成"的唯一证据
        if probes().mic_is_cable {
            rep.push("默认麦克风", StepResult::Done(String::from("已设为 CABLE Output")));
        } else {
            rep.push(
                "默认麦克风",
                StepResult::Manual(String::from(
                    "设不动 → 请到 声音设置 → 输入，手动把默认设备选成 CABLE Output",
                )),
            );
        }
    }

    // ③ 虚拟键盘驱动
    //   ★ 三种情况必须分开：已就绪 / 装了但槽位被占（本机另一个实例在跑）/ 真没装。
    //     原来只有两分法 ⇒ "程序在跑时点一键安装"会白跑一遍驱动安装脚本，
    //     还显示"已安装（若首次建议重启）"，看起来像刚装好。
    if crate::is_inject_ready() {
        rep.push("虚拟键盘驱动", StepResult::AlreadyOk(String::from("已就绪")));
    } else if crate::device_probe::winuhid_driver_installed() {
        rep.push(
            "虚拟键盘驱动",
            StepResult::AlreadyOk(String::from(
                "驱动已安装，但设备槽位被占用（多半是本机已有一个程序在运行）→ 关掉重复的实例即可",
            )),
        );
    } else {
        rep.push("虚拟键盘驱动", repair_driver());
    }

    // ④ 遥控器按键通道（2026-11-06 客户机 S2 实测：判据必须读 BTHENUM 的 Service）
    //   链条：SDP 未成功 → 无 Enum\BTHENUM\{00001124-...} 节点 → HidBth 未绑定
    //        → 主机侧 PSM 0x0011 无监听者 → 遥控器主动连 HID-Control 被 REFUSED_PSM
    //        → 无 HID-Interrupt、无 HID 报告 → 按键全无。
    //   ⚠ 不能读 Enum\HID 残留节点：HardwareID 永远含 HID_DEVICE_SYSTEM_KEYBOARD ⇒ 假绿灯。
    //   ★ 绝不要在这里接回任何「改造设备」动作：那会把一台能用的机器直接改坏
    //     （命令层的改造入口已整体删除，见本文件 ④ 上方说明）。
    match p.remote_is_keyboard {
        Some(true) => rep.push(
            "遥控器按键通道",
            StepResult::AlreadyOk(if p.is_keyboard_driver {
                String::from(
                    "HID 服务已绑定，按键可读；但设备仍绑键盘驱动 ⇒ 会往窗口打字母。\
                     会往窗口打字母。这是保持键盘身份的固有代价；\
                     ⚠ 不要点「④ 禁止打字母」—— 改绑后 Raw Input 里不再有这台设备，\
                     按键收不到，必须删设备重配对才能恢复",
                )
            } else {
                String::from(
                    "HID 服务已绑定，但设备已被改绑成 HID-compliant device ⇒ 系统不再当键盘（不打字母），\
                     同时也**不再出现在 Raw Input 里，按键收不到**。\
                     修法：点「⑤ 恢复键盘模式」，然后删除设备重新配对（只恢复不重配不够）。",
                )
            }),
        ),
        Some(false) => rep.push(
            "遥控器按键通道",
            StepResult::Manual(String::from(
                "有 HID 服务节点但没有绑定 HidBth ⇒ 主机侧没有 PSM 0x0011 监听者，遥控器会被 REFUSED_PSM。\
                 修法：设置 → 蓝牙和其他设备 → 删除 IPRC1000 → 遥控器进配对模式 → 重新添加设备。\
                 （--restore-keyboard 对这种情况无效）",
            )),
        ),
        // 无 {00001124} 节点 = SDP 没跑通，判不了状态，不能报 AlreadyOk
        // （历史假绿灯：客户装完看到成功，实际一个 HID 报告都收不到）。
        None => rep.push(
            "遥控器按键通道",
            StepResult::Manual(String::from(
                "没有 Enum\\BTHENUM\\{00001124-...} 节点 ⇒ SDP 从未成功，主机侧不会建 HID 通道。\
                 修法：删除设备后重新配对（--restore-keyboard 对这种情况无效）",
            )),
        ),
    }

    // ⑤ BthPort ETW 注册表（2026-11-06 补进原生版）：
    //   C# 版有 ApplyRegistry()，原生版此前一处写入代码都没有 —— 开发机早被
    //   C# 版改好并重启过，全新客户机音频首片只 8 字节头、全判截断 ⇒ 语音无声。
    //   这三个值在 BthPort 驱动加载时读取 ⇒ 写成功也**必须**报 NeedsReboot。
    let reg = crate::etw_registry::status();
    if reg.configured {
        rep.push("ETW 注册表", StepResult::AlreadyOk(String::from("三项已就绪")));
    } else {
        match crate::etw_registry::apply() {
            Ok(m) => rep.push("ETW 注册表", StepResult::NeedsReboot(m)),
            Err(e) => rep.push(
                "ETW 注册表",
                StepResult::Manual(format!("{e} —— 请以管理员身份重新运行安装")),
            ),
        }
    }

    invalidate_probes();
    rep
}

/// 「一键修复」的 JSON 入口（界面/脚本用）。
pub fn repair_all() -> String {
    let rep = repair_report();
    let items: Vec<serde_json::Value> = rep
        .items
        .iter()
        .map(|(n, r)| {
            serde_json::json!({
                "name": n,
                "state": r.tag(),
                "message": r.message(),
            })
        })
        .collect();
    serde_json::json!({
        "ok": rep.all_ok(),
        "needsReboot": rep.any_reboot(),
        "message": rep.summary(),
        "items": items,
    })
    .to_string()
}
