//! 用 SetupAPI 把某个设备实例**强制改绑**到 `input.inf` 的「HID-compliant device」。
//!
//! ## 为什么不用 `windows` crate 的 setupapi 绑定
//!
//! 它把 `HDEVINFO` 当作 COM 风格的 `Interface` 类型：`SetupDiCreateDeviceInfoList`
//! 返回 `Result<HDEVINFO, Error>`，而 `DiInstallDevice` / `SetupDiDestroyDeviceInfoList`
//! 要求 `Param<HDEVINFO, CopyType>` —— 类型系统直接对不上（实测 47 个编译错误）。
//! 这里改成 `LoadLibraryW("setupapi.dll")` + `GetProcAddress`，ABI 自己声明，
//! 与 `winuhid.rs` 的做法一致，也不给链接器加依赖。
//!
//! ## 为什么不能用 `UpdateDriverForPlugAndPlayDevicesW`
//!
//! 它按**硬件 ID** 去 INF 的 models 段找匹配项。`input.inf` 只有：
//! ```text
//! %HID_Device%=HID_Inst,     HID_DEVICE
//! %HID_Raw_Device%=HID_Raw_Inst, HID_DEVICE
//! ```
//! 匹配键是 `HID_DEVICE`，而遥控器的硬件 ID 是 `HID_DEVICE_SYSTEM_KEYBOARD`
//! ⇒ 永远匹配不上 ⇒ 返回 FALSE 且 `GetLastError=0`（开发机日志实证）。
//! 设备管理器手动能改成功，是因为它走「按 INF + 型号强制安装」，
//! 也就是本模块做的这条路。

use std::ffi::c_void;

const SPDIT_CLASSDRIVER: u32 = 2;
const MAX_PATH: usize = 260;

#[repr(C)]
#[derive(Clone, Copy, Default)]
struct Guid {
    data1: u32,
    data2: u16,
    data3: u16,
    data4: [u8; 8],
}

#[repr(C)]
struct SpDevInfoData {
    cb_size: u32,
    class_guid: Guid,
    dev_inst: u32,
    reserved: usize,
}

#[repr(C)]
#[derive(Clone, Copy)]
struct FileTime {
    low: u32,
    high: u32,
}

/// `SP_DRVINFO_DATA_V2_W`
#[repr(C)]
#[derive(Clone, Copy)]
struct SpDrvInfoDataW {
    cb_size: u32,
    driver_type: u32,
    reserved: usize,
    description: [u16; 256],
    mfg_name: [u16; 256],
    provider_name: [u16; 256],
    driver_date: FileTime,
    driver_version: u64,
}

impl Default for SpDrvInfoDataW {
    fn default() -> Self {
        Self {
            cb_size: 0,
            driver_type: 0,
            reserved: 0,
            description: [0; 256],
            mfg_name: [0; 256],
            provider_name: [0; 256],
            driver_date: FileTime { low: 0, high: 0 },
            driver_version: 0,
        }
    }
}

/// `SP_DEVINSTALL_PARAMS_W`
#[repr(C)]
#[derive(Clone, Copy)]
struct SpDevInstallParamsW {
    cb_size: u32,
    flags: u32,
    flags_ex: u32,
    hwnd_parent: isize,
    install_msg_handler: usize,
    install_msg_handler_context: usize,
    file_queue: isize,
    reserved: usize,
    class_install_reserved: u32,
    driver_path: [u16; MAX_PATH],
}

impl Default for SpDevInstallParamsW {
    fn default() -> Self {
        Self {
            cb_size: 0,
            flags: 0,
            flags_ex: 0,
            hwnd_parent: 0,
            install_msg_handler: 0,
            install_msg_handler_context: 0,
            file_queue: 0,
            reserved: 0,
            class_install_reserved: 0,
            driver_path: [0; MAX_PATH],
        }
    }
}

// BOOL SetupDiGetINFClassW(PCWSTR InfName, LPGUID ClassGuid, PWSTR ClassName,
//                          DWORD ClassNameSize, PDWORD RequiredSize);
// ★ 五个参数，别漏 ClassNameSize —— 漏了会参数错位，返回 err=87 (INVALID_PARAMETER)。
type FnGetInfClass =
    unsafe extern "system" fn(*const u16, *mut Guid, *mut u16, u32, *mut u32) -> i32;
type FnCreateList = unsafe extern "system" fn(*const Guid, isize) -> isize;
type FnOpenDevInfo =
    unsafe extern "system" fn(isize, *const u16, isize, u32, *mut SpDevInfoData) -> i32;
type FnSetSelDevice = unsafe extern "system" fn(isize, *mut SpDevInfoData) -> i32;
type FnGetInstallParams =
    unsafe extern "system" fn(isize, *mut SpDevInfoData, *mut SpDevInstallParamsW) -> i32;
type FnSetInstallParams =
    unsafe extern "system" fn(isize, *mut SpDevInfoData, *mut SpDevInstallParamsW) -> i32;
type FnBuildDrvList = unsafe extern "system" fn(isize, *mut SpDevInfoData, u32) -> i32;
type FnEnumDrvInfo =
    unsafe extern "system" fn(isize, *mut SpDevInfoData, u32, u32, *mut SpDrvInfoDataW) -> i32;
type FnSetSelDriver =
    unsafe extern "system" fn(isize, *mut SpDevInfoData, *mut SpDrvInfoDataW) -> i32;
type FnDiInstall =
    unsafe extern "system" fn(isize, isize, *mut SpDevInfoData, *mut SpDrvInfoDataW, u32, *mut i32)
        -> i32;
type FnDestroyList = unsafe extern "system" fn(isize) -> i32;

#[link(name = "kernel32")]
extern "system" {
    fn LoadLibraryW(path: *const u16) -> *mut c_void;
    fn GetProcAddress(module: *mut c_void, name: *const u8) -> *mut c_void;
    fn GetLastError() -> u32;
}

fn wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

unsafe fn sym(module: *mut c_void, name: &str) -> *mut c_void {
    let mut n = name.as_bytes().to_vec();
    n.push(0);
    unsafe { GetProcAddress(module, n.as_ptr()) }
}

/// 把 `instance_id`（形如 `HID\{00001124-…}_VID&00020a5c_PID&8502\9&…`）
/// 强制改绑到 `input.inf` 的「HID-compliant device」。
///
/// 选型严格：型号名必须含 `hid-compliant device` 且**不含 `raw`** ——
/// 选成 raw device 会让 HID 接口彻底消失、按键与音频全断。
pub fn install_hid_compliant(instance_id: &str, inf_path: &str) -> Result<(), String> {
    unsafe {
        let dll = wide("setupapi.dll");
        let m = LoadLibraryW(dll.as_ptr());
        if m.is_null() {
            return Err(String::from("加载 setupapi.dll 失败"));
        }

        let p_inf_class = sym(m, "SetupDiGetINFClassW");
        let p_create = sym(m, "SetupDiCreateDeviceInfoList");
        let p_open = sym(m, "SetupDiOpenDeviceInfoW");
        let p_set_dev = sym(m, "SetupDiSetSelectedDevice");
        let p_get_ip = sym(m, "SetupDiGetDeviceInstallParamsW");
        let p_set_ip = sym(m, "SetupDiSetDeviceInstallParamsW");
        let p_build = sym(m, "SetupDiBuildDriverInfoList");
        let p_enum = sym(m, "SetupDiEnumDriverInfoW");
        let p_set_drv = sym(m, "SetupDiSetSelectedDriverW");
        let p_destroy = sym(m, "SetupDiDestroyDeviceInfoList");
        if p_inf_class.is_null()
            || p_create.is_null()
            || p_open.is_null()
            || p_set_dev.is_null()
            || p_get_ip.is_null()
            || p_set_ip.is_null()
            || p_build.is_null()
            || p_enum.is_null()
            || p_set_drv.is_null()
            || p_destroy.is_null()
        {
            return Err(String::from("setupapi.dll 缺少导出函数"));
        }
        let f_inf_class: FnGetInfClass = std::mem::transmute(p_inf_class);
        let f_create: FnCreateList = std::mem::transmute(p_create);
        let f_open: FnOpenDevInfo = std::mem::transmute(p_open);
        let f_set_dev: FnSetSelDevice = std::mem::transmute(p_set_dev);
        let f_get_ip: FnGetInstallParams = std::mem::transmute(p_get_ip);
        let f_set_ip: FnSetInstallParams = std::mem::transmute(p_set_ip);
        let f_build: FnBuildDrvList = std::mem::transmute(p_build);
        let f_enum: FnEnumDrvInfo = std::mem::transmute(p_enum);
        let f_set_drv: FnSetSelDriver = std::mem::transmute(p_set_drv);
        let f_destroy: FnDestroyList = std::mem::transmute(p_destroy);

        // ① 拿 input.inf 的类 GUID（HIDClass）
        let inf_w = wide(inf_path);
        let mut class_guid = Guid::default();
        let mut class_name = [0u16; 256];
        let mut need = 0u32;
        if f_inf_class(
            inf_w.as_ptr(),
            &mut class_guid,
            class_name.as_mut_ptr(),
            class_name.len() as u32,
            &mut need,
        ) == 0
        {
            let e = GetLastError();
            return Err(format!(
                "SetupDiGetINFClassW 失败（err={e}）—— 检查 {inf_path} 是否存在、是否可读"
            ));
        }

        // ② 建 device info list（HIDClass 类）
        let list = f_create(&class_guid, 0);
        if list == -1 || list == 0 {
            return Err(format!(
                "SetupDiCreateDeviceInfoList 失败（err={}）—— 需要管理员权限",
                GetLastError()
            ));
        }
        let result = run(
            list,
            instance_id,
            inf_path,
            &f_open,
            &f_set_dev,
            &f_get_ip,
            &f_set_ip,
            &f_build,
            &f_enum,
            &f_set_drv,
        );
        f_destroy(list);
        result
    }
}

#[allow(clippy::too_many_arguments)]
unsafe fn run(
    list: isize,
    instance_id: &str,
    inf_path: &str,
    f_open: &FnOpenDevInfo,
    f_set_dev: &FnSetSelDevice,
    f_get_ip: &FnGetInstallParams,
    f_set_ip: &FnSetInstallParams,
    f_build: &FnBuildDrvList,
    f_enum: &FnEnumDrvInfo,
    f_set_drv: &FnSetSelDriver,
) -> Result<(), String> {
    unsafe {
        // ③ 打开目标设备实例
        let id = wide(instance_id);
        let mut dev = SpDevInfoData {
            cb_size: std::mem::size_of::<SpDevInfoData>() as u32,
            class_guid: Guid::default(),
            dev_inst: 0,
            reserved: 0,
        };
        if f_open(list, id.as_ptr(), 0, 0, &mut dev) == 0 {
            return Err(format!("SetupDiOpenDeviceInfo 失败（err={}）", GetLastError()));
        }
        if f_set_dev(list, &mut dev) == 0 {
            return Err(format!("SetupDiSetSelectedDevice 失败（err={}）", GetLastError()));
        }

        // ④ 把驱动搜索目录指到 INF 所在目录，否则 CLASSDRIVER 列表是空的
        let mut ip = SpDevInstallParamsW {
            cb_size: std::mem::size_of::<SpDevInstallParamsW>() as u32,
            ..Default::default()
        };
        if f_get_ip(list, &mut dev, &mut ip) == 0 {
            return Err(format!("SetupDiGetDeviceInstallParams 失败（err={}）", GetLastError()));
        }
        let inf_dir = std::path::Path::new(inf_path)
            .parent()
            .map(|p| p.to_string_lossy().to_string())
            .unwrap_or_else(|| String::from(r"C:\Windows\INF"));
        let dir = wide(&inf_dir);
        let n = dir.len().min(ip.driver_path.len());
        ip.driver_path[..n].copy_from_slice(&dir[..n]);
        ip.cb_size = std::mem::size_of::<SpDevInstallParamsW>() as u32;
        if f_set_ip(list, &mut dev, &mut ip) == 0 {
            return Err(format!("SetupDiSetDeviceInstallParams 失败（err={}）", GetLastError()));
        }

        // ⑤ 枚举可用驱动型号
        if f_build(list, &mut dev, SPDIT_CLASSDRIVER) == 0 {
            return Err(format!(
                "SetupDiBuildDriverInfoList 失败（err={}）—— 可能没有管理员权限",
                GetLastError()
            ));
        }

        // ⑥ 挑「HID-compliant device」，**严格排除 raw**
        let mut picked: Option<SpDrvInfoDataW> = None;
        let mut idx = 0u32;
        let mut seen: Vec<String> = Vec::new();
        loop {
            let mut di = SpDrvInfoDataW {
                cb_size: std::mem::size_of::<SpDrvInfoDataW>() as u32,
                ..Default::default()
            };
            if f_enum(list, &mut dev, SPDIT_CLASSDRIVER, idx, &mut di) == 0 {
                break;
            }
            idx += 1;
            let end = di.description.iter().position(|&c| c == 0).unwrap_or(256);
            let desc = String::from_utf16_lossy(&di.description[..end]);
            let low = desc.to_lowercase();
            seen.push(desc.clone());
            if low.contains("hid-compliant device") && !low.contains("raw") {
                picked = Some(di);
                break;
            }
        }
        let mut picked = picked.ok_or_else(|| {
            format!(
                "input.inf 里没找到「HID-compliant device」型号。候选：{}",
                if seen.is_empty() {
                    String::from("（空列表）")
                } else {
                    seen.join(" / ")
                }
            )
        })?;

        // ⑦ 指定型号并强制安装
        if f_set_drv(list, &mut dev, &mut picked) == 0 {
            return Err(format!("SetupDiSetSelectedDriver 失败（err={}）", GetLastError()));
        }

        let dll = wide("newdev.dll");
        let m2 = LoadLibraryW(dll.as_ptr());
        if m2.is_null() {
            return Err(String::from("加载 newdev.dll 失败"));
        }
        let p_di = sym(m2, "DiInstallDevice");
        if p_di.is_null() {
            return Err(String::from("newdev.dll 缺少 DiInstallDevice"));
        }
        let f_di: FnDiInstall = std::mem::transmute(p_di);
        let mut reboot = 0i32;
        if f_di(0, list, &mut dev, &mut picked, 0, &mut reboot) == 0 {
            return Err(format!("DiInstallDevice 失败（err={}）", GetLastError()));
        }
        Ok(())
    }
}
