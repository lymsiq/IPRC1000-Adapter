//! 设备节点探测（2026-10-31）
//!
//! 首页那张「遥控器」卡片要显示「HID 节点在线（N 个）· 蓝牙节点在线 · 配对已存在」，
//! 这些必须从**注册表真实枚举**得出 —— 不能像之前那样返回空数组冒充。
//!
//! 关键判据（与 C# `Env.RemoteStillKeyboard()` 同源）：
//!   HID 实例的 `Driver` 值如果指向 `kbdclass`（键盘类驱动），说明遥控器当前被系统当键盘，
//!   它的按键会漏出 `m` 这类字母；接好 WinUHid 之后该值为空（HID-compliant 设备）。
//!
//! 为什么不用 SetupAPI：注册表读取无副作用、不需要设备句柄、失败也只是返回空列表，
//! 比 SetupDiGetClassDevs 那一套轻得多；而我们要的信息（实例名 + Driver）注册表里全有。

#![allow(dead_code)]  // 整个模块：设备枚举供将来的界面用，当前 egui 版未接

use windows::core::PCWSTR;
use windows::Win32::System::Registry::*;

/// 一台设备的节点信息（界面只要 path 和 online）
#[derive(Clone)]
#[allow(dead_code)]
pub struct Node {
    pub path: String,
    pub online: bool,
}

fn wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

fn open_key(path: &str) -> Option<HKEY> {
    let p = wide(path);
    let mut h = HKEY::default();
    let rc = unsafe { RegOpenKeyExW(HKEY_LOCAL_MACHINE, PCWSTR(p.as_ptr()), 0, KEY_READ, &mut h) };
    if rc.is_ok() {
        Some(h)
    } else {
        None
    }
}

fn subkeys(h: HKEY) -> Vec<String> {
    let mut out = Vec::new();
    let mut i = 0u32;
    loop {
        let mut buf = [0u16; 512];
        let mut len = buf.len() as u32;
        let rc = unsafe {
            RegEnumKeyExW(
                h,
                i,
                windows::core::PWSTR(buf.as_mut_ptr()),
                &mut len,
                None,
                windows::core::PWSTR::null(),
                None,
                None,
            )
        };
        if rc.is_err() {
            break;
        }
        out.push(String::from_utf16_lossy(&buf[..len as usize]));
        i += 1;
        if i > 256 {
            break; // 防御：异常键不让它转圈
        }
    }
    out
}

/// 读一个字符串值（读不到返回 None）
fn value_string(h: HKEY, name: &str) -> Option<String> {
    let n = wide(name);
    let mut buf = [0u16; 1024];
    let mut size = (buf.len() * 2) as u32;
    let rc = unsafe {
        RegQueryValueExW(
            h,
            PCWSTR(n.as_ptr()),
            None,
            None,
            Some(buf.as_mut_ptr() as *mut u8),
            Some(&mut size),
        )
    };
    if rc.is_err() || size < 2 {
        return None;
    }
    let chars = (size as usize / 2).saturating_sub(1).min(buf.len());
    Some(String::from_utf16_lossy(&buf[..chars]))
}

/// 枚举 HID 节点。
///
/// 只扫 `HID\` 下**我们这个遥控器 VID/PID** 的分支 —— 系统里通常有十几个 HID 设备
/// （键盘、鼠标、耳机…），全列出来既慢又没意义；界面要的是"**这只遥控器**有几个节点、
/// 它们是不是还在线"。
pub fn hid_nodes() -> Vec<Node> {
    let mut out = Vec::new();
    let Some(root) = open_key(r"SYSTEM\CurrentControlSet\Enum\HID") else {
        return out;
    };
    for vidpid in subkeys(root) {
        // 我们的遥控器：VID 0A5C（Broadcom）/ PID 8502。大小写都可能，统一大写比较
        // ⚠ 实际键名里是 `VID&00020a5c`（& 而非 _），两种写法都要认 ——
        //   第一次只匹配 `VID_` 导致 HID 节点永远是空的（首页一直显示"不在线"）。
        let up = vidpid.to_uppercase();
        let has_vid = up.contains("VID_00020A5C") || up.contains("VID&00020A5C");
        let has_pid = up.contains("PID_8502") || up.contains("PID&8502");
        if !(has_vid || has_pid) {
            continue;
        }
        let path = format!(r"SYSTEM\CurrentControlSet\Enum\HID\{vidpid}");
        let Some(k) = open_key(&path) else { continue };
        for inst in subkeys(k) {
            let ipath = format!(r"{path}\{inst}");
            // ★ 判"是不是被当成键盘"用 is_keyboard_instance（ClassGUID/Service/DeviceDesc），
            //   不能用 Driver 字符串 —— 那是「类键 GUID\序号」形态，永远不含 "kbdclass"。
            let kb = open_key(&ipath).map(is_keyboard_instance).unwrap_or(false);
            out.push(Node {
                path: if kb { format!("{inst}（键盘）") } else { inst },
                online: true,
            });
        }
    }
    out
}

/// 枚举蓝牙节点（BTHENUM 下这台遥控器的服务节点）
pub fn bt_nodes() -> Vec<Node> {
    let mut out = Vec::new();
    let Some(root) = open_key(r"SYSTEM\CurrentControlSet\Enum\BTHENUM") else {
        return out;
    };
    for svc in subkeys(root) {
        let path = format!(r"SYSTEM\CurrentControlSet\Enum\BTHENUM\{svc}");
        let Some(k) = open_key(&path) else { continue };
        // 服务分支名里带 VID/PID（如 {00001124-...}_VID&00020a5c_PID&8502）
        let up = svc.to_uppercase();
        if !(up.contains("VID&00020A5C") || up.contains("PID&8502")) {
            continue;
        }
        for inst in subkeys(k) {
            out.push(Node { path: inst, online: true });
        }
    }
    out
}



/// 带缓存的节点查询（15 秒）。
///
/// 为什么不每次现算：`getState` 每 2 秒一次，而枚举注册表要几十毫秒；
/// 设备热插拔的变化有 15 秒延迟完全可以接受（界面本来就不是实时监控）。
pub fn nodes_cached() -> (Vec<Node>, Vec<Node>, bool) {
    use std::sync::{Mutex, OnceLock};
    use std::time::Instant;
    static C: OnceLock<Mutex<Option<(Instant, Vec<Node>, Vec<Node>)>>> = OnceLock::new();
    let cell = C.get_or_init(|| Mutex::new(None));
    if let Ok(g) = cell.lock() {
        if let Some((t, h, b)) = g.as_ref() {
            if t.elapsed().as_secs() < 15 {
                let kb = h.iter().any(|n| n.path.contains("（键盘）"));
                return (h.clone(), b.clone(), kb);
            }
        }
    }
    let h = hid_nodes();
    let b = bt_nodes();
    let kb = h.iter().any(|n| n.path.contains("（键盘）"));
    if let Ok(mut g) = cell.lock() {
        *g = Some((Instant::now(), h.clone(), b.clone()));
    }
    (h, b, kb)
}

// ───────────────── 遥控器「还被当键盘吗」的判定 + 一键改造 ─────────────────
//
// 为什么要有这一段（2026-10-31，换机器/换遥控器复现）：
//   遥控器是标准蓝牙 HID 键盘。只要 Windows 配对成功，它的按键就会被输入栈当成
//   键盘输入打进焦点窗口 —— **不跑任何软件都会打出字母**。
//   正解不是"吞键"（低级钩子拿不到设备来源，还会误伤真键盘、有卡键风险），
//   而是**让它不再被当成键盘**：把 HID 接口的驱动从 `HID Keyboard Device` 换成
//   系统自带的 `HID-compliant device`（input.inf 的 HID_DEVICE 型号，null service）。
//   换完之后：
//     · 输入栈不再消费它的键 ⇒ 不打字母；
//     · 蓝牙 ACL 链路照常建立（音频也走它）⇒ 我们的 BthPort ETW 仍读得到原始报文。
//
// 10-13 真机已验证过这条路径（`tools/SetHidDriver`）：
//   切换前 Service=kbdhid / Desc=HID Keyboard Device
//   切换后 Service=(无)  / Desc=@input.inf,%hid_device%;HID-compliant device
//   而且改完后 56 次按键**一个字母都没产生**，按键与音频仍全部从 ETW 读到。
//
// ⚠ 三个不能想当然的点（都是当时踩过的）：
//   1. `hid.inf` 不存在；`HID-compliant device` 定义在 **input.inf** 里
//      （`input.inf:824` 的 `HID_DEVICE = "HID-compliant device"`，`:575` 是 null service 安装）。
//   2. HardwareID 必须**从注册表动态取**，不能写死 VID/PID（换遥控器/换机器后会变）。
//   3. `UpdateDriverForPlugAndPlayDevices(FORCE)` 只装 INF 的**默认型号** ——
//      对 input.inf + `HID_DEVICE` 兼容 ID 恰好就是我们要的那个型号，所以这条路走得通；
//      换成别的型号（如 XInput）会失败，不要照搬。

/// 这个 HID 实例是不是"被当成键盘"。
///
/// ★★ 2026-10-31（另一台机器的报告抓出来的真 bug）：
///   原来写的是 `Driver` 值字符串里找 `kbdclass` —— **永远是 false**。
///   `Driver` 的实际形态是「类键 GUID\序号」，例如
///     仍是键盘：`{4d36e96b-e325-11ce-bfc1-08002be10318}\0003` + `Service=kbdhid`
///     已改造后：`{745a17a0-74d3-11d0-b6fe-00a0c90f57da}\0075` + `Service=`（空）
///   所以 `contains("kbdclass")` 恒 false ⇒ 判据永远报"已改造" ⇒ 环境页假通过、
///   ④ 被整段跳过、`convert` 的 targets 为空还返回"无需改造"。**假仪表比没仪表更糟。**
///
/// 正确判据（按可靠性排序）：
///   1. `ClassGUID` == 键盘类 `{4d36e96b-e325-11ce-bfc1-08002be10318}`（改造后会变成 HIDClass）
///   2. `Service` ∈ {kbdhid, kbdclass}
///   3. `DeviceDesc` 含 "keyboard"
///
/// ⚠ **不要用 `HardwareID` 判**：转换成功后它里面仍然有 `HID_DEVICE_SYSTEM_KEYBOARD`，
///   一样会误判（报告里专门警告过）。
fn is_keyboard_instance(ik: HKEY) -> bool {
    const KEYBOARD_CLASS: &str = "4d36e96b-e325-11ce-bfc1-08002be10318";
    let class = value_string(ik, "ClassGUID").unwrap_or_default().to_lowercase();
    if class.contains(KEYBOARD_CLASS) {
        return true;
    }
    let svc = value_string(ik, "Service").unwrap_or_default().to_lowercase();
    if svc == "kbdhid" || svc == "kbdclass" {
        return true;
    }
    let desc = value_string(ik, "DeviceDesc").unwrap_or_default().to_lowercase();
    desc.contains("keyboard")
}

/// 一个遥控器 HID 实例的节点信息（判定 + 改造都要用）
pub struct HidInst {
    pub inst: String,
    /// `Driver` 值指向 kbdclass ⇒ 系统仍把它当键盘
    pub is_keyboard: bool,
    /// `HardwareID` 的第一项
    pub hwid: String,
    /// 完整**设备实例 ID**（`HID\{vidpid}\{inst}`），SetupAPI 用这个打开设备
    pub full_id: String,
}

/// 读 REG_MULTI_SZ 的第一项
fn value_multi_string(h: HKEY, name: &str) -> Vec<String> {
    let n = wide(name);
    let mut buf = [0u16; 2048];
    let mut size = (buf.len() * 2) as u32;
    let rc = unsafe {
        RegQueryValueExW(
            h,
            PCWSTR(n.as_ptr()),
            None,
            None,
            Some(buf.as_mut_ptr() as *mut u8),
            Some(&mut size),
        )
    };
    if rc.is_err() || size < 2 {
        return Vec::new();
    }
    let chars = (size as usize / 2).min(buf.len());
    String::from_utf16_lossy(&buf[..chars])
        .split('\0')
        .filter(|s| !s.is_empty())
        .map(|s| s.to_string())
        .collect()
}

/// 枚举这台遥控器的 HID 实例（带 Driver 判定 + HardwareID）
pub fn remote_hid_instances() -> Vec<HidInst> {
    let mut out = Vec::new();
    let Some(root) = open_key(r"SYSTEM\CurrentControlSet\Enum\HID") else {
        return out;
    };
    for vidpid in subkeys(root) {
        let up = vidpid.to_uppercase();
        // ⚠ 键名里是 `VID&00020a5c`（& 而非 _），两种写法都要认 —— 只匹配 `VID_`
        //   会让节点永远是空的（首页一直显示"不在线"）。
        let has_vid = up.contains("VID_00020A5C") || up.contains("VID&00020A5C");
        let has_pid = up.contains("PID_8502") || up.contains("PID&8502");
        if !(has_vid || has_pid) {
            continue;
        }
        let path = format!(r"SYSTEM\CurrentControlSet\Enum\HID\{vidpid}");
        let Some(k) = open_key(&path) else { continue };
        for inst in subkeys(k) {
            let ipath = format!(r"{path}\{inst}");
            let Some(ik) = open_key(&ipath) else { continue };
            let is_kb = is_keyboard_instance(ik);
            let hwid = value_multi_string(ik, "HardwareID")
                .into_iter()
                .next()
                .unwrap_or_default();
            let full_id = format!(r"HID\{vidpid}\{inst}");
            out.push(HidInst { inst, is_keyboard: is_kb, hwid, full_id });
        }
    }
    out
}

/// 蓝牙 HID 服务是否真的绑定了遥控器 —— **按键通道是否存在的唯一可靠判据**。
///
/// 2026-11-06 客户机 S2 实测链条：
/// ```text
/// SDP 从未成功（缓存服务记录 0 条）
///   → Enum\BTHENUM\{00001124-...}_VID&00020a5c_PID&8502 节点不存在
///   → HidBth 未绑定
///   → 主机 L2CAP 没有 PSM 0x0011 的监听者
///   → 遥控器主动开 HID-Control 被 REFUSED_PSM（先 PENDING 15 秒）
///   → 无 HID-Interrupt、无 HID 报告 → 按键全无（hidReports=0 / Raw Input 列表为空）
/// ```
///
/// 所以判据必须读 **`Enum\BTHENUM\{00001124-...}_VID&00020a5c_PID&8502\<inst>` 的 `Service`**，
/// 必须是 `HidBth`。
///
/// ⚠ **不能读 `Enum\HID` 下的节点**：那是设备被改造/换驱动后留下的空壳，
/// 它的 `HardwareID` 里永远有 `HID_DEVICE_SYSTEM_KEYBOARD`（枚举时缓存，不会被改写），
/// 会让界面报「键盘模式正确」的**假绿灯**——客户机就是这么被误导的。
///
/// 返回：
///   `Some(true)`  = Service=HidBth，主机侧有监听者，正常
///   `Some(false)` = 有 {00001124} 节点但 Service 为空 / 被禁用 ⇒ 故障
///   `None`        = 连 {00001124} 节点都没有 ⇒ SDP 没跑通 / 未配对
pub fn bt_hid_bound() -> Option<bool> {
    let Some(root) = open_key(r"SYSTEM\CurrentControlSet\Enum\BTHENUM") else {
        return None;
    };
    let mut any = false;
    let mut bound = false;
    for vidpid in subkeys(root) {
        let up = vidpid.to_uppercase();
        if !up.contains("00001124") {
            continue;
        }
        let ours = up.contains("VID&00020A5C")
            || up.contains("VID_00020A5C")
            || up.contains("PID&8502")
            || up.contains("PID_8502");
        if !ours {
            continue;
        }
        let path = format!(r"SYSTEM\CurrentControlSet\Enum\BTHENUM\{vidpid}");
        let Some(k) = open_key(&path) else { continue };
        for inst in subkeys(k) {
            any = true;
            let ipath = format!(r"{path}\{inst}");
            let Some(ik) = open_key(&ipath) else { continue };
            let svc = value_string(ik, "Service").unwrap_or_default();
            if svc.eq_ignore_ascii_case("HidBth") {
                bound = true;
            }
        }
    }
    if !any {
        None
    } else {
        Some(bound)
    }
}

/// HID 服务是否已绑定（保留旧函数名，调用点很多）。
///   Some(true)  = Service=HidBth ⇒ **正确状态**（Raw Input 读得到按键；副作用：系统打字母）
///   Some(false) = 有节点但没绑定 ⇒ **故障**：删除设备重新配对（`--restore-keyboard` 无效）
///   None        = 没有 {00001124} 节点 ⇒ SDP 没跑通，同样只能重新配对
pub fn remote_still_keyboard() -> Option<bool> {
    bt_hid_bound()
}

/// 把遥控器从 `kbdhid`（键盘）改绑到 `mshidkmdf`（**HID-compliant device**）。
///
/// 目的：让系统**不再往焦点窗口打字母**，同时按键照常可读：
///   · 不再绑 `kbdhid` ⇒ 不打字符
///   · 绑 `mshidkmdf`，仍属 HIDClass ⇒ HID 接口照常注册
///   · TLC 仍是 `0x01/0x06` ⇒ Raw Input 的 `WM_INPUT` 照常投递
///   · ETW 读 BthPort 的 ACL 包，与 HID 驱动绑定无关 ⇒ 照常有数据
///
/// 安装细节在 `dev_install::install_hid_compliant`（SetupAPI 按型号强制安装）。
/// 旧实现用 `UpdateDriverForPlugAndPlayDevicesW` 按硬件 ID 匹配，而 `input.inf`
/// 只匹配 `HID_DEVICE`，设备是 `HID_DEVICE_SYSTEM_KEYBOARD` ⇒ 必然失败
/// （`GetLastError=0`，开发机日志实证）。
///
/// 回退：`--restore-keyboard`（删 devnode，让 PnP 按硬件 ID 重新选回 `keyboard.inf`）。
pub fn convert_to_hid_compliant() -> Result<String, String> {
    const INF: &str = r"C:\Windows\INF\input.inf";

    let insts = remote_hid_instances();
    if insts.is_empty() {
        return Err("找不到遥控器的 HID 节点 —— 先按一下遥控器唤醒，确认设备管理器里能看到它".into());
    }
    let targets: Vec<&HidInst> = insts.iter().filter(|x| x.is_keyboard).collect();
    if targets.is_empty() {
        return Ok("遥控器已经是 HID-compliant device，无需改造（按键不会打出字母）".into());
    }

    let mut done = 0usize;
    let mut errs: Vec<String> = Vec::new();
    for t in &targets {
        match crate::dev_install::install_hid_compliant(&t.full_id, INF) {
            Ok(()) => {
                done += 1;
                eprintln!("[设备] 已改绑为 HID-compliant device：{}", t.full_id);
            }
            Err(e) => {
                eprintln!("[设备] ★改绑失败：{} → {e}", t.full_id);
                errs.push(format!("{}：{e}", t.inst));
            }
        }
    }

    if done == 0 {
        Err(format!("改造失败：{}", errs.join("；")))
    } else if errs.is_empty() {
        Ok(format!(
            "已改造 {done} 个节点 ⇒ 不再打字母，但 Raw Input 里不再有这台设备，按键收不到。\
             必须删除设备重新配对才能恢复（--restore-keyboard 只删节点，不够）。"
        ))
    } else {
        Ok(format!("部分成功：{done} 个已改造；问题：{}", errs.join("；")))
    }
}

/// VB-CABLE 主设备的问题码（`ROOT\MEDIA\*` 的 `Problem` 值）。
///   None = 找不到设备（未安装）或设备健康
///   Some(0) = 设备在且健康
///   Some(n) = 设备在但**坏了**（设备管理器黄叹号，n = CM_PROB_*）
///
/// 为什么要它：`cable_ready()` 只看"端点叫不叫 CABLE"，**端点在但驱动坏掉时照样返回 true**
/// ⇒ 一键修复认为"已装"直接跳过 ⇒ 用户必须手动去设备管理器处理。
/// 而这恰恰是"装完还得手动"最常见的那个环节。
pub fn vbaudio_problem_code() -> Option<u32> {
    let root = r"SYSTEM\CurrentControlSet\Enum\ROOT\MEDIA";
    let Some(rk) = open_key(root) else { return None };
    for inst in subkeys(rk) {
        let path = format!(r"{root}\{inst}");
        let Some(k) = open_key(&path) else { continue };
        let hwid = value_multi_string(k, "HardwareID")
            .into_iter()
            .next()
            .unwrap_or_default()
            .to_uppercase();
        let desc = value_string(k, "DeviceDesc").unwrap_or_default().to_uppercase();
        if !(hwid.contains("VBAUDIO") || desc.contains("VB-AUDIO")) {
            continue;
        }
        // Problem 是 REG_DWORD：存在且非 0 ⇒ 设备有问题
        let name = wide("Problem");
        let mut val = 0u32;
        let mut size = 4u32;
        let rc = unsafe {
            RegQueryValueExW(
                k,
                PCWSTR(name.as_ptr()),
                None,
                None,
                Some(&mut val as *mut u32 as *mut u8),
                Some(&mut size),
            )
        };
        return Some(if rc.is_ok() { val } else { 0 });
    }
    None
}

/// 音频端点是否真的存在（按注册表 `DeviceDesc` 匹配）。
///
/// ★ 2026-10-31（另一台机器的报告）：cpal 的 `name()` 读的是 **FriendlyName**（PKEY,14），
///   而那台机器的端点只有 `DeviceDesc`（PKEY,2）⇒ `name()` 返回空串 ⇒
///   `cable_ready()` 按名字判永远"未安装" ⇒ **一键修复每次都静默重跑 `VBCABLE_Setup -i -h`**
///   —— 而我们自己的注释就写过：好声卡被重装会坏（实测 Problem 10）。
///
/// 所以端点存在性一律以注册表 `DeviceDesc` 为准（FriendlyName 只用来显示）。
pub fn audio_endpoint_present(kind: &str) -> bool {
    let root = r"SYSTEM\CurrentControlSet\Enum\SWD\MMDEVAPI";
    let Some(rk) = open_key(root) else { return false };
    for inst in subkeys(rk) {
        let path = format!(r"{root}\{inst}");
        let Some(k) = open_key(&path) else { continue };
        // ★ 名字可能在 FriendlyName，也可能在 DeviceDesc —— 两台机器各缺一半，
        //   只查一个字段就会在另一台误判成"未安装"。**两个都查**。
        let a = value_string(k, "DeviceDesc").unwrap_or_default();
        let b = value_string(k, "FriendlyName").unwrap_or_default();
        let k_up = kind.to_uppercase();
        if a.to_uppercase().contains(&k_up) || b.to_uppercase().contains(&k_up) {
            return true;
        }
    }
    false
}

/// WinUHid 驱动**是否已安装**（与"此刻能不能创建设备"是两件事）。
///
/// 为什么必须分开：`is_inject_ready()` 判的是"能创建虚拟键盘设备"，
/// 而**主程序自己正占着设备槽位时它必然返回 false**。于是"程序在跑的时候点一键安装"
/// 会被判成"驱动缺失"，白跑一遍安装脚本，并显示"驱动已安装（若首次安装建议重启）"
/// —— 用户看到的是莫名的一次驱动安装。
///
/// 判据与声卡同源：`ROOT\WINUHID_VIRTUAL_HID_ENUMERATOR` 这个设备节点存在 = 装了。
pub fn winuhid_driver_installed() -> bool {
    let root = r"SYSTEM\CurrentControlSet\Enum\ROOT\WINUHID_VIRTUAL_HID_ENUMERATOR";
    let Some(k) = open_key(root) else { return false };
    // 至少有一个实例（0000/0001…）
    !subkeys(k).is_empty()
}

/// 「取消改造」：把遥控器的 HID 节点从 raw 绑回键盘。
///
/// ## 为什么需要（2026-11-06 真机结论）
///
/// 改造（`input.inf / HID_Raw_Inst.NT`）的注释写着"系统不再消费按键，但程序仍能读到报文"——
/// 前半句对，**后半句错**。真机实测（`hid-window*.csv` + `--hid-probe`）：
///
/// ```text
/// 11:00:47  HID 节点 present=True / Status=OK / CM_PROB_NONE     ← 设备在
///           全系统 HID 接口 9 个，匹配遥控器 0 个                  ← 但没有它的接口
/// ```
///
/// raw 绑定**不注册 HID 设备接口** ⇒
///   · 程序无法 CreateFile 当消费者（`hidOpen` 永远 0）
///   · Windows 也就不建 HID 中断通道（PSM 0x0013）
///   · 遥控器没地方投递按键 ⇒ 4 秒监督超时断开重连
///   · ETW 只剩信令可看（`otherEvents` 涨、`keyEvents` 恒 0）
///
/// 也就是说：**改造与"能收到按键"不可兼得**。这只遥控器只有一个 Keyboard 集合，
/// 系统里也没有第二个客户端能替它消费。所以正解是"不改造 + 钩子吞键"，本函数就是回到起点。
pub fn restore_keyboard() -> Result<String, String> {
    // 枚举注册表里的遥控器节点（含 phantom —— 正是它们需要被删掉）。
    // 实例 ID 形如 HID\{00001124-...}_VID&00020a5c_PID&8502\9&378511c7&1&0000
    let mut targets: Vec<String> = Vec::new();
    unsafe {
        use windows::Win32::System::Registry::*;
        let mut root = HKEY::default();
        let path: Vec<u16> = r"SYSTEM\CurrentControlSet\Enum\HID\0"
            .replace(r"\0", "")
            .encode_utf16()
            .chain(std::iter::once(0))
            .collect();
        if RegOpenKeyExW(HKEY_LOCAL_MACHINE, windows::core::PCWSTR(path.as_ptr()), 0, KEY_READ, &mut root).is_err() {
            return Err("打不开 HID 枚举键".into());
        }
        let mut i = 0u32;
        loop {
            let mut buf = [0u16; 256];
            let mut len = buf.len() as u32;
            if RegEnumKeyExW(root, i, windows::core::PWSTR(buf.as_mut_ptr()), &mut len, None,
                             windows::core::PWSTR::null(), None, None).is_err() { break; }
            i += 1;
            let key = String::from_utf16_lossy(&buf[..len as usize]);
            let up = key.to_uppercase();
            if !(up.contains("0A5C") && up.contains("8502")) { continue; }
            let mut sub = HKEY::default();
            let sp: Vec<u16> = format!(r"SYSTEM\CurrentControlSet\Enum\HID\{key}")
                .encode_utf16().chain(std::iter::once(0)).collect();
            if RegOpenKeyExW(HKEY_LOCAL_MACHINE, windows::core::PCWSTR(sp.as_ptr()), 0, KEY_READ, &mut sub).is_err() { continue; }
            let mut j = 0u32;
            loop {
                let mut b2 = [0u16; 256];
                let mut l2 = b2.len() as u32;
                if RegEnumKeyExW(sub, j, windows::core::PWSTR(b2.as_mut_ptr()), &mut l2, None,
                                 windows::core::PWSTR::null(), None, None).is_err() { break; }
                j += 1;
                let inst = String::from_utf16_lossy(&b2[..l2 as usize]);
                targets.push(format!(r"HID\{key}\{inst}"));
            }
            let _ = RegCloseKey(sub);
        }
        let _ = RegCloseKey(root);
    }

    if targets.is_empty() {
        return Err("找不到遥控器的 HID 节点 —— 请先按一下遥控器唤醒后重试".into());
    }

    // 用 pnputil 删除节点，让 Windows 重新枚举时按硬件 ID 重新排序装回 keyboard.inf/kbdhid。
    //
    // ⚠ 更正一条曾经写在这里的错误结论（2026-11-06 11:1x 实测）：
    //   原文写「raw 绑定后设备不再声明 TLC，keyboard.inf 的匹配项对不上」——**这是错的**。
    //   删除前读 `Enum\HID\{00001124-...}\<inst>` 的 HardwareID，仍然是多值，且明确包含：
    //       HID\VID_0A5C&UP:0001_U:0006
    //       HID_DEVICE_SYSTEM_KEYBOARD      ← 键盘匹配项一直都在
    //       HID_DEVICE
    //   HardwareID 是枚举时由 hidbth 依据报告描述符生成的、缓存在 Enum 键里，raw 绑定不会改写它。
    //   所以 keyboard.inf 在原理上能匹配上；之前 UpdateDriver(keyboard.inf) 「rc 非 0 但没变化」
    //   另有原因（用的是设备实例 ID 而非硬件 ID、或 INF 未进 DriverStore 等），**不要据此否定这条路**。
    //
    //   之所以仍选 remove-device：它把 devnode 整个删掉，强制 PnP 从零重新排序，
    //   不受上一次强制安装残留影响，比"再 UpdateDriver 一次"确定性强。

    let mut ok = 0usize;
    let mut err: Vec<String> = Vec::new();
    for inst in &targets {
        let out = std::process::Command::new(r"C://Windows//System32//pnputil.exe")
            .args(["/remove-device", inst])
            .output();
        match out {
            Ok(o) if o.status.success() => {
                ok += 1;
                eprintln!("[设备] 已删除节点（下次连接会自动重新识别）：{inst}");
            }
            Ok(o) => {
                let msg = String::from_utf8_lossy(&o.stderr).trim().to_string();
                err.push(format!("{inst}：{msg}"));
            }
            Err(e) => err.push(format!("{inst}：pnputil 启动失败 {e}")),
        }
    }

    if ok > 0 {
        Ok(format!(
            "已删除 {ok} 个旧节点。\n现在按一下遥控器唤醒它 —— Windows 会重新识别并装回键盘驱动，\
             按键链路就会恢复。\n⚠ 恢复后系统会重新消费按键（可能打出字母），\
             在程序侧用钩子吞掉之前属于正常现象。"
        ))
    } else {
        Err(format!("删除失败（需要以管理员身份运行）：{}", err.join("；")))
    }
}
