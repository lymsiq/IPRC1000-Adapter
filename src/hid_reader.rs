//! 遥控器 HID 输入「消费者」（2026-11-06）
//!
//! ## 为什么必须要有这个东西
//!
//! 「④ 改造遥控器」把 HID 集合节点从键盘改绑成 raw（`input.inf / HID_Raw_Inst.NT`），
//! 目的是让系统不再把按键当键盘（不打字母）。但真机抓包（`diag-keys.txt`）证明还有下半段后果：
//!
//! ```text
//! 10:05:39.564  TX SIG ConnReq  PSM=HID-Control(0x0011)   ← 控制通道建了
//! 10:05:39.571  TX SIG CfgRsp                              ← 配置完成
//!               ← 此后 3.6 秒，空口零业务数据 →
//! 10:05:43.190  EVT DiscComplete reason=0x08               ← 监督超时踢掉
//! ```
//!
//! 整个抓包里 **PSM 0x0013（HID Interrupt）一次都没被请求**。
//! 原因是 Windows 蓝牙 HID 的主机侧是「按需建中断通道」：**没有客户端打开输入报告管道，
//! 就不发这个连接请求**；遥控器没地方投递按键，固件干脆不发报文，4 秒后链路超时断开重连，
//! 无限循环（`ConnectionCount=33`）。
//!
//! 所以 raw 绑定本身没错，缺的是**消费者**。本模块就是那个消费者：
//! 打开 HID 集合 → 持续 `ReadFile` → 把 8 字节键盘报告解成 usage 列表 → 交给后端状态机。
//!
//! ## 为什么不再依赖 ETW 抓按键
//!
//! ETW 只能看到**空口上真有的报文**。中断通道不建立时，空口上本来就没有按键报文，
//! ETW 再准也抓不到（实测 `hidReports=0` 而 `otherEvents=1049`）。
//! 现在按键走 HID 直读（有数据就一定读得到），ETW 继续只为**音频**服务（语音键的 ACL 音频帧）。
//!
//! ## 与 kbdhid 的关系
//!
//! · 已改造（raw）：系统没有客户端，只有我们读 → 不打字母、单触发。
//! · 未改造（键盘）：kbdhid 也在读，HID 允许多个句柄共享打开（`FILE_SHARE_READ|WRITE`），
//!   我们会**同时**收到按键 → 此时会出现"打字母 + 注入"双触发。
//!   所以本模块启动时会检查设备是不是还挂在键盘类下，是的话只打一行提醒，不做额外动作
//!   （改造与否仍由用户决定，和原来一样）。

use std::sync::atomic::{AtomicU64, Ordering};
use std::time::Duration;
use windows::core::PCWSTR;
use windows::Win32::Foundation::{CloseHandle, GENERIC_READ, HANDLE};
use windows::Win32::Storage::FileSystem::{
    CreateFileW, FILE_ATTRIBUTE_NORMAL, FILE_SHARE_READ, FILE_SHARE_WRITE, OPEN_EXISTING,
};

// 直接用 kernel32 的 ReadFile：windows crate 里它的签名带 OVERLAPPED 参数，
// 需要额外开 Win32_System_IO feature 才导出 —— 为一个阻塞读多开一个 feature 不值。
#[link(name = "kernel32")]
extern "system" {
    fn ReadFile(
        hfile: HANDLE,
        lpbuffer: *mut core::ffi::c_void,
        nbytestoread: u32,
        lpnumberofbytesread: *mut u32,
        lpoverlapped: *mut core::ffi::c_void,
    ) -> i32;
}

/// HID 设备接口类 GUID（`{4d1e55b2-f16f-11cf-88cb-001111000030}`）
const HID_INTERFACE_GUID: &str = "{4d1e55b2-f16f-11cf-88cb-001111000030}";

/// 已读到的报告总数（诊断用，界面/日志可看）
pub static REPORTS: AtomicU64 = AtomicU64::new(0);
/// 已成功打开过设备几次（掉线重连会 +1）
pub static REOPENED: AtomicU64 = AtomicU64::new(0);
/// 枚举不到接口的轮数（诊断：用来区分"遥控器休眠"和"改造后 phantom"）
pub static MISS: AtomicU64 = AtomicU64::new(0);

fn wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

/// 枚举本机所有 HID 接口路径（SetupAPI）。
///
/// ★ 必须走「设备接口」而不是「设备实例」：CreateFile 打开 HID 集合要的是
///   `\\?\HID#<硬件ID>#<实例>#{接口GUID}` 这种**接口路径**，
///   用注册表 Enum\HID 的键名拼出来的是设备实例路径 —— 实测打不开（"找不到文件"）。
fn enum_hid_paths() -> Vec<String> {
    use windows::Win32::Devices::DeviceAndDriverInstallation::{
        SetupDiDestroyDeviceInfoList, SetupDiEnumDeviceInterfaces, SetupDiGetClassDevsW,
        SetupDiGetDeviceInterfaceDetailW, DIGCF_DEVICEINTERFACE, DIGCF_PRESENT,
        SP_DEVICE_INTERFACE_DATA, SP_DEVICE_INTERFACE_DETAIL_DATA_W,
    };
    use windows::Win32::Devices::HumanInterfaceDevice::HidD_GetHidGuid;

    let mut out = Vec::new();
    unsafe {
        let guid = HidD_GetHidGuid();
        let set_res = SetupDiGetClassDevsW(
            Some(&guid),
            None,
            None,
            DIGCF_PRESENT | DIGCF_DEVICEINTERFACE,
        );
        let Ok(set) = set_res else {
            eprintln!("[HID] SetupDiGetClassDevs 失败 err={}", windows::Win32::Foundation::GetLastError().0);
            return out;
        };
        let mut idx = 0u32;
        loop {
            let mut did = SP_DEVICE_INTERFACE_DATA {
                cbSize: std::mem::size_of::<SP_DEVICE_INTERFACE_DATA>() as u32,
                ..Default::default()
            };
            if SetupDiEnumDeviceInterfaces(set, None, &guid, idx, &mut did).is_err() {
                break;
            }
            idx += 1;
            let mut need = 0u32;
            let _ = SetupDiGetDeviceInterfaceDetailW(set, &did, None, 0, Some(&mut need), None);
            if need == 0 {
                continue;
            }
            let mut buf = vec![0u8; need as usize];
            let det = buf.as_mut_ptr() as *mut SP_DEVICE_INTERFACE_DETAIL_DATA_W;
            (*det).cbSize = std::mem::size_of::<SP_DEVICE_INTERFACE_DETAIL_DATA_W>() as u32;
            if SetupDiGetDeviceInterfaceDetailW(set, &did, Some(det), need, None, None).is_err() {
                continue;
            }
            // DevicePath 是 [u16;1] 的变长尾巴，从首地址读到 0 为止
            let base = (*det).DevicePath.as_ptr();
            let mut n = 0usize;
            while *base.add(n) != 0 {
                n += 1;
            }
            let path = String::from_utf16_lossy(std::slice::from_raw_parts(base, n));
            if path.to_uppercase().contains("VID&00020A5C") || path.to_uppercase().contains("VID_00020A5C") {
                out.push(path);
            }
        }
        let _ = SetupDiDestroyDeviceInfoList(set);
    }
    out
}

/// 把 8 字节标准键盘报告解成 usage 列表。
///
/// 报告格式（见 SDP 里的 48 字节描述符）：`[8 位修饰键][保留 1 字节][6 个 usage]`。
/// 有的栈会给缓冲区加上 1 字节 Report ID（本设备是 1），所以先按长度判断一下。
fn decode(buf: &[u8]) -> Vec<u8> {
    let b = if buf.len() >= 9 && buf[0] == 1 { &buf[1..] } else { buf };
    if b.len() < 8 {
        return Vec::new();
    }
    let mut v = Vec::with_capacity(14);
    for i in 0..8 {
        if b[0] & (1 << i) != 0 {
            v.push(0xE0 + i as u8); // 左Ctrl..右GUI
        }
    }
    for u in &b[2..8] {
        if *u != 0 {
            v.push(*u);
        }
    }
    v
}

fn dispatch(usages: &[u8]) {
    let Some(sh) = crate::backend::shared() else { return };
    let Ok(mut b) = sh.lock() else { return };
    b.handle_hid_report(usages);
}

/// 启动消费者线程（幂等：只会起一次）
pub fn start() {
    static STARTED: std::sync::Once = std::sync::Once::new();
    STARTED.call_once(|| {
        eprintln!("[HID] 消费者线程启动中…");
        std::thread::Builder::new()
            .name("hid-consumer".into())
            .spawn(|| {
                loop {
                    let paths = enum_hid_paths();
                    if !paths.is_empty() {
                        eprintln!("[HID] 枚举到 {} 个遥控器接口", paths.len());
                    }
                    if paths.is_empty() {
                        // 未发现接口：可能是遥控器休眠，或改造成 raw 后节点处于 phantom
                        // （只在短窗口里出现）。500ms 轮一次，尽快抢到窗口。
                        MISS.fetch_add(1, Ordering::Relaxed);
                        if MISS.load(Ordering::Relaxed) % 60 == 1 {
                            eprintln!("[HID] 暂未发现遥控器接口（正常：休眠或 phantom 窗口之间）");
                        }
                        std::thread::sleep(Duration::from_millis(500));
                        continue;
                    }
                    let mut opened = false;
                    for p in paths {
                        let w = wide(&p);
                        let h = unsafe {
                            CreateFileW(
                                PCWSTR(w.as_ptr()),
                                GENERIC_READ.0,
                                FILE_SHARE_READ | FILE_SHARE_WRITE,
                                None,
                                OPEN_EXISTING,
                                FILE_ATTRIBUTE_NORMAL,
                                None,
                            )
                        };
                        let err = unsafe { windows::Win32::Foundation::GetLastError().0 };
                        let Ok(h) = h else {
                            eprintln!("[HID] 打开失败 err={err} path={p}");
                            continue;
                        };
                        if h.is_invalid() {
                            eprintln!("[HID] 句柄无效 err={err} path={p}");
                            continue;
                        }
                        opened = true;
                        REOPENED.fetch_add(1, Ordering::Relaxed);
                        eprintln!("[HID] 已打开遥控器输入管道（成为 HID 消费者，中断通道应随即建立）");
                        // 阻塞读；设备掉线时 ReadFile 返回失败，退出内层循环去重连
                        let mut buf = [0u8; 64];
                        loop {
                            let mut n = 0u32;
                            let ok = unsafe {
                                ReadFile(
                                    h,
                                    buf.as_mut_ptr() as *mut core::ffi::c_void,
                                    buf.len() as u32,
                                    &mut n,
                                    std::ptr::null_mut(),
                                )
                            };
                            if ok == 0 {
                                break;
                            }
                            if n == 0 {
                                continue;
                            }
                            let usages = decode(&buf[..n as usize]);
                            REPORTS.fetch_add(1, Ordering::Relaxed);
                            dispatch(&usages);
                        }
                        unsafe { let _ = CloseHandle(h); }
                        eprintln!("[HID] 设备断开，准备重连");
                        break;
                    }
                    if !opened {
                        std::thread::sleep(Duration::from_millis(500));
                    } else {
                        std::thread::sleep(Duration::from_millis(300));
                    }
                }
            })
            .ok();
    });
}

/// 现场诊断：`--hid-probe`。列出所有含 0a5c 的 HID 接口、尝试打开、报告结果。
///
/// 为什么需要：客户机器上"按键完全没反应"可能是两回事 ——
///   ① 接口存在但没人读（本模块就是来解决的）
///   ② 接口根本不存在（raw 绑定后节点 phantom，或遥控器没连）
/// 一条命令就能分清，不用再猜。
pub fn probe() {
    let paths = enum_hid_paths();
    println!("HID 接口总数（含遥控器的）：{}", paths.len());
    if paths.is_empty() {
        println!("★ 一个都没找到 —— 遥控器未连接，或改造成 raw 后接口不暴露");
        return;
    }
    for p in &paths {
        let w = wide(p);
        let h = unsafe {
            CreateFileW(
                PCWSTR(w.as_ptr()),
                GENERIC_READ.0,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                None,
                OPEN_EXISTING,
                FILE_ATTRIBUTE_NORMAL,
                None,
            )
        };
        match h {
            Ok(hh) if !hh.is_invalid() => {
                println!("可打开 ✓  {p}");
                unsafe { let _ = CloseHandle(hh); }
            }
            _ => {
                let e = unsafe { windows::Win32::Foundation::GetLastError().0 };
                println!("打不开 ✗  err={e}  {p}");
            }
        }
    }
}
