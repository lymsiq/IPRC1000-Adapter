//! BthPort ETW 注册表项（2026-11-06 补进原生版）
//!
//! ## 为什么原生版必须自己写
//!
//! 音频帧是 653 B 的 ACL 大包。系统默认 `MaxEtwBytes` 太小，ETW 只把**首片 8 字节头**
//! 交出来，后续分片全丢 ⇒ `truncatedFirst` 暴涨、`frames=0` ⇒ 语音租约拿不到唯一续租点。
//! C# 版 `Env.ApplyRegistry()` 会写这三项，原生版此前**一处写入代码都没有**——
//! 开发机因为早被 C# 版改过并重启过，一直正常；全新客户机必然无声。
//!
//! 三个值（与 C# 版逐字一致，别改数值）：
//! | 名称 | 值 | 作用 |
//! |---|---|---|
//! | `EtwDropLargeEvents` | 0 | 不丢大事件 |
//! | `EtwLogSensitiveData` | 1 | 记录 BTHPORT 私有帧负载 |
//! | `MaxEtwBytes` | 8192 | 单事件上限覆盖 653 B 分片 |
//!
//! ⚠ **改完必须重启**：这三个值在 BthPort 驱动加载时读取，不是即时的。
//! 所以 `apply()` 成功后一律返回 `NeedsReboot`，不许报"已就绪"。

use windows::core::PCWSTR;
use windows::Win32::System::Registry::{
    RegCloseKey, RegOpenKeyExW, RegQueryValueExW, RegSetValueExW,
    HKEY, HKEY_LOCAL_MACHINE, KEY_READ, KEY_SET_VALUE, REG_DWORD,
};

const PATH: &str = r"SYSTEM\CurrentControlSet\Services\BthPort\Parameters";
const WANT: [(&str, u32); 3] = [
    ("EtwDropLargeEvents", 0),
    ("EtwLogSensitiveData", 1),
    ("MaxEtwBytes", 8192),
];

fn wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

/// 只读体检；不需要管理员。
pub struct Status {
    pub configured: bool,
    pub detail: String,
}

fn read_dword(h: HKEY, name: &str) -> Option<u32> {
    let n = wide(name);
    let mut data = [0u8; 4];
    let mut size = 4u32;
    let rc = unsafe {
        RegQueryValueExW(
            h,
            PCWSTR(n.as_ptr()),
            None,
            None,
            Some(data.as_mut_ptr()),
            Some(&mut size),
        )
    };
    if rc.is_err() || size != 4 {
        None
    } else {
        Some(u32::from_le_bytes(data))
    }
}

pub fn status() -> Status {
    let p = wide(PATH);
    let mut h = HKEY::default();
    if unsafe { RegOpenKeyExW(HKEY_LOCAL_MACHINE, PCWSTR(p.as_ptr()), 0, KEY_READ, &mut h) }.is_err() {
        return Status {
            configured: false,
            detail: String::from("读不到 BthPort\\Parameters（键不存在或权限不足）"),
        };
    }
    let mut bad = Vec::new();
    for (name, want) in WANT {
        match read_dword(h, name) {
            Some(v) if v == want => {}
            Some(v) => bad.push(format!("{name}={v}（应为 {want}）")),
            None => bad.push(format!("{name} 缺失（应为 {want}）")),
        }
    }
    unsafe { let _ = RegCloseKey(h); }
    Status {
        configured: bad.is_empty(),
        detail: if bad.is_empty() {
            String::from("三项已就绪（EtwDropLargeEvents=0 / EtwLogSensitiveData=1 / MaxEtwBytes=8192）")
        } else {
            bad.join("；")
        },
    }
}

/// 写三项。**需要管理员**（HKLM）。成功≠生效，调用方必须按"需重启"处理。
pub fn apply() -> Result<String, String> {
    let p = wide(PATH);
    let mut h = HKEY::default();
    unsafe {
        if RegOpenKeyExW(HKEY_LOCAL_MACHINE, PCWSTR(p.as_ptr()), 0, KEY_SET_VALUE, &mut h).is_err() {
            return Err(String::from(
                "打不开 HKLM BthPort\\Parameters —— 需要管理员权限，请用「一键安装.cmd」或右键以管理员身份运行",
            ));
        }
        for (name, want) in WANT {
            let n = wide(name);
            let bytes = want.to_le_bytes();
            let rc = RegSetValueExW(h, PCWSTR(n.as_ptr()), 0, REG_DWORD, Some(&bytes));
            if rc.is_err() {
                let _ = RegCloseKey(h);
                return Err(format!("写 {name} 失败：{rc:?}（需要管理员）"));
            }
        }
        let _ = RegCloseKey(h);
    }
    // 写完复读一次：判据是"值真的变了"，不是"API 没报错"
    let s = status();
    if !s.configured {
        return Err(format!("写入后复核仍未就绪：{}", s.detail));
    }
    Ok(String::from(
        "已写入 EtwDropLargeEvents=0 / EtwLogSensitiveData=1 / MaxEtwBytes=8192；**必须重启电脑**才生效",
    ))
}
