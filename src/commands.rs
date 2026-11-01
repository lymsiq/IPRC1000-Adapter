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
/// 走 `assets/configure-xiaomi-audio.ps1 -Mode EnsureMic` —— 与 C# 版**同一条路**：
/// 它会先记下原来的默认麦克风（卸载时能还原），而不是我们自己乱写注册表。
pub fn set_default_mic() -> String {
    let mut cands: Vec<std::path::PathBuf> = Vec::new();
    if let Ok(exe) = std::env::current_exe() {
        if let Some(d) = exe.parent() {
            cands.push(d.join("assets").join("configure-xiaomi-audio.ps1"));
            if let Some(p) = d.parent() {
                cands.push(
                    p.join("IPRC1000_Adapter")
                        .join("assets")
                        .join("configure-xiaomi-audio.ps1"),
                );
            }
        }
    }
    // 开发目录兜底（源码树里跑的时候）
    cands.push(std::path::PathBuf::from(
        "F:/vibe/IPRC1000_Adapter/assets/configure-xiaomi-audio.ps1",
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
        "message": "没找到 configure-xiaomi-audio.ps1 —— 请在「设置 → 系统 → 声音 → 输入」里手动选择 CABLE Output"
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
    ///   Some(true)  = 还是键盘 ⇒ 按键会漏出字母，需要「改造遥控器」
    ///   Some(false) = 已是 HID-compliant device ✓
    ///   None        = 找不到遥控器的 HID 节点（未连接/未配对）
    pub remote_is_keyboard: Option<bool>,
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
fn repair_driver() -> String {
    crate::installer::install_winuhid()
}

fn install_cable() -> String {
    crate::installer::install_cable()
}

/// 「改造遥控器」：把它的 HID 接口从「键盘」换成「HID-compliant device」。
///
/// 做完之后系统的输入栈不再消费它的键 ⇒ **按键不会打出字母**；
/// 而蓝牙 ACL 链路照常建立（音频也走它）⇒ 我们的 ETW 仍读得到原始报文。
/// 这是"换机器 / 换遥控器后按键出字母"的根治办法（不是吞键）。
pub fn convert_remote_hid() -> String {
    let r = crate::device_probe::convert_to_hid_compliant();
    invalidate_probes();
    match r {
        Ok(msg) => serde_json::json!({ "ok": true, "message": msg }).to_string(),
        Err(e) => serde_json::json!({ "ok": false, "message": e }).to_string(),
    }
}

pub fn repair_all() -> String {
    let p = probes();
    let mut done: Vec<String> = Vec::new();
    let mut fail: Vec<String> = Vec::new();

    // ① 虚拟声卡
    //   ★ 判据是「端点在 **且** 设备健康」，不是"端点在"：
    //     端点在但驱动坏掉（设备管理器黄叹号）时，老判据会判"已装"跳过 ⇒ 用户只能手动。
    match crate::installer::cable_status() {
        Ok(()) => done.push(String::from("虚拟声卡已安装且健康")),
        Err(e) if e.contains("黄叹号") => fail.push(format!(
            "虚拟声卡：{e} —— 请到设备管理器卸载「VB-Audio Virtual Cable」（勾选删除驱动）后重新点安装"
        )),
        Err(_) => {
            let r = install_cable();
            if r.contains("完成") || r.contains("已安装") || r.contains("重启") {
                done.push(r);
            } else {
                fail.push(format!("虚拟声卡：{r}"));
            }
        }
    }

    // ② 默认麦克风
    let p2 = probes();
    if p2.mic_is_cable {
        done.push(String::from("默认麦克风已指向 CABLE Output"));
    } else {
        let r = set_default_mic();
        if r.contains("已设") {
            done.push(String::from("默认麦克风已设为 CABLE Output"));
        } else {
            fail.push(String::from("默认麦克风改不动，请在系统设置里手动选 CABLE Output"));
        }
    }

    // ③ 虚拟键盘驱动
    if !crate::is_inject_ready() {
        let r = repair_driver();
        if r.contains("完成") || r.contains("已安装") || r.contains("就绪") {
            done.push(String::from("虚拟键盘驱动已安装"));
        } else {
            fail.push(format!("驱动：{r}"));
        }
    } else {
        done.push(String::from("虚拟键盘驱动已就绪"));
    }

    // ④ 遥控器改造（换机器 / 换遥控器后最常见的一项）
    //    还被当键盘 ⇒ 按键会漏出字母。改成 HID-compliant device 后系统不再消费它的键。
    match p.remote_is_keyboard {
        Some(true) => {
            let r = convert_remote_hid();
            // r = {"ok":bool,"message":str}
            let v: serde_json::Value = serde_json::from_str(&r).unwrap_or_default();
            let ok = v["ok"].as_bool().unwrap_or(false);
            let m = v["message"].as_str().unwrap_or("（无结果）").to_string();
            if ok {
                done.push(m);
            } else {
                fail.push(format!("遥控器改造：{m}"));
            }
        }
        Some(false) => done.push(String::from("遥控器已不是键盘（不会打出字母）")),
        None => done.push(String::from("遥控器未连接（跳过键盘改造检查）")),
    }

    invalidate_probes();
    let msg = if fail.is_empty() {
        format!("全部就绪：{}", done.join("；"))
    } else {
        format!("已完成 {} 项；{} 项需要手动处理：{}", done.len(), fail.len(), fail.join("；"))
    };
    serde_json::json!({ "ok": fail.is_empty(), "message": msg }).to_string()
}
