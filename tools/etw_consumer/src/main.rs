//! IPRC1000 语音桥：BthPort ETW → 完整帧解码 → UDP 推给 audio router → VB-CABLE → 输入法
//!
//! ## 帧结构（逐字节统计确认，`MaxEtwBytes` 生效后）
//! L2CAP 负载 653 字节：
//! ```text
//! [0..12]   13 字节帧头   a1 f7 01 <seq> 00 81 00 <cnt> 00 00 00 40 01
//! [13..652] 640 字节音频 = 320 个 16bit LE 样本 @16 kHz
//! ```
//! ACL 上拆两片：首片 `dlen=344`(PB=10) + 续片 `dlen=313`(PB=01) = 657 = L2CAP头(4)+负载(653)
//!
//! ⚠ 若注册表 `MaxEtwBytes` 未在**驱动加载时**生效（即改完没重启），
//! 首片只记 8 字节头 → 每帧只剩 156/320 样本。用 `bt_verify_reboot.py` 判定。
//!
//! ## 输出的 UDP 协议（与 `audio/pcm_router.rs` 对齐）
//! `CLEAR`（会话开始，router 开 CABLE 输出流）／ 48 kHz int16 LE PCM ／ `END`（会话结束）
//! 注意：**必须发 48 kHz** —— `voice_pcm::push_16k` 是 16k→48k 内插之后才发的。
//!
//! ## 用法
//! ```text
//! etw_consumer.exe --wav out.wav --seconds 30 --hp 70 --udp 127.0.0.1:31680
//! etw_consumer.exe --replay iprc1000_payloads.bin --udp 127.0.0.1:31680   # 离线回放
//! ```
//! 实时 ETW 模式需要**管理员**（`StartTrace` 要求 Administrators 或 Performance Log Users）。

use std::net::UdpSocket;
use std::sync::atomic::Ordering;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use ferrisetw::parser::Parser;
use ferrisetw::provider::Provider;
use ferrisetw::schema_locator::SchemaLocator;
use ferrisetw::trace::*;
use ferrisetw::EventRecord;

const BTHPORT_GUID: &str = "8a1f9517-3a8c-4a9e-a018-4f17a200f277";
const FRAME_LEN: usize = 653; // L2CAP 负载
const HDR: usize = 13; // 帧头
const SAMPLES: usize = 320; // 每帧样本（16 kHz → 320 样本 = 20 ms）
/// 无帧多久发 END。
///
/// ⚠ 曾经是 250 ms —— 蓝牙链路正常抖动（RF 干扰、SCO 重传）很容易超过它，
/// 于是**一次正常抖动就被判成"会话结束"**：桥发 END → 应用侧关掉 CABLE 流，
/// 紧接着下一帧又发 CLEAR 重新开流。开开关关既有断音，也容易把 WASAPI 设备搞到打不开。
/// 放宽到 600 ms：仍能在真正松手后 ~0.6 s 关流，但不会被抖动误触发。
const IDLE_END_MS: u64 = 600;
/// ── 强制 ETW 刷新的"填缓冲"机制 ──
///
/// 背景：ferrisetw 的 `flush_timer` 最小只能取整到 **1 秒**（API 限制），而空闲时蓝牙流量
/// 稀疏（~200 B/s）连 4 KB 缓冲都填不满 → 单个按键事件要干等最多 1 秒才投递
/// （实测平均 791 ms）。`ControlTrace(FLUSH)` 对实时会话返回 87，改不了。
///
/// 解法：**我们自己注册一个 ETW 提供者，定期往同一个会话里发一个 ~4KB 的空事件**。
/// 缓冲是按字节算的、且所有提供者共用 —— 一旦被这条大事件填满，ETW 就必须把缓冲区
/// **连同里面的按键事件一起刷出去**，于是按键延迟被压到"下一个填缓冲周期"以内。
/// 周期取 50 ms：延迟上限约 50 ms，而流量仅 ~80 KB/s（开销可忽略）。
/// 填缓冲的**三档自适应速率**（毫秒）：
///   · 热（最近 3 秒内有蓝牙事件，用户正在用）→ 10 ms  → 按键延迟 ≈20 ms
///   · 温（最近 60 秒内有事件）                → 50 ms  → 延迟 ≈50 ms
///   · 冷（一直没用）                          → 200 ms → 延迟 ≤200 ms，流量仅 ~10 KB/s
/// 遥控器**不在线**时完全停填（没必要为不存在的设备刷缓冲）。
const FLUSH_FEED_HOT_MS: u64 = 10;
const FLUSH_FEED_COLD_MS: u64 = 200;
/// 每次填的负载大小（2 KB —— 必须**小于缓冲** 4 KB，否则 EventWrite 返回 234 ERROR_MORE_DATA）
// 注意：负载必须**小于缓冲**（4KB），否则 EventWrite 返回 234（ERROR_MORE_DATA）。
// 取 2KB → 两个事件即可填满一页 4KB 缓冲，从而逼出一次刷新。
const FLUSH_FEED_BYTES: usize = 2048;
/// 我们自己提供者的 GUID（任意固定值，只要不与别人冲突）
const FLUSH_FEED_GUID: u128 = 0x4950524331303030_464C555348464545; // "IPRC1000"-"FLUSHFEED"

const KEY_PORT: u16 = 31681;  // 按键事件单独一个端口，避免和 48k PCM 混在一起


// ───────────────────────── ETW 侧的 HID 报告解析 ─────────────────────────
//
// 遥控器在 CID 0x41（HID 中断通道）上发两类东西：
//   音频帧：L2CAP 负载 653 字节（拆成 344 + 313 两个 ACL 分片）
//   按键报告：单分片，ACL dlen=14，L2CAP 负载 10 字节，形如
//       acl = 0a 00 | 41 00 | a1 01 00 00 10 00 00 00 00 00
//             └L2CAP┘  └CID┘  └报告头┘ mod  rsv └─ 6 个 usage 槽 ─┘
//   全零报告 = 松手。
//
// **这是"系统不再把它当键盘"之后唯一的按键来源**（把设备驱动从
// HID Keyboard Device 换成 HID-compliant device 后，Raw Input/键盘钩子就看不到它了）。

#[derive(Default)]
struct Stats {
    frames: u64,
    truncated_first: u64, // 只带 4 字节负载的首片 —— 出现即说明注册表没生效
    other: u64,
    key_events: u64,
    hid_reports: u64,
    /// 事件「产生 → 我们收到」的延迟统计（毫秒），用持续流动的链路层事件测
    lat_n: u64,
    lat_sum: u64,
    lat_max: u64,

    // ── 以下为"音频为什么断"定位用（2026-10-14 加）──
    /// 音频帧之间的空档（毫秒）：>IDLE_END_MS 记一次
    gaps: u64,
    /// 最长一次空档（毫秒）—— 长录音里"说着说着没声了"看这个
    max_gap_ms: u64,
    /// 最近一次收到音频帧的 Unix 毫秒（0=从未）
    last_frame_ms: u64,
    /// 会话计数：每次 CLEAR 前自增
    sessions: u64,
    /// 被 ETW 丢掉的续片数（= 丢失的音频帧数）—— 缓冲区不够时会出现
    lost_cont: u64,
    /// UDP 发送失败次数
    udp_err: u64,
}

// ───────────────────────── 运行统计落盘（定位"音频为什么断"） ─────────────────────────
//
// 背景：语音桥的输出以前直接丢弃（应用用 CreateNoWindow 启动且不重定向），
// 而统计只在**退出时**打印一次 —— 桥以 --seconds 86400 常驻，等于永远看不到。
// 结果："说着说着没声音了"到底断在 **ETW（帧根本没来）** 还是
// **声卡侧（帧来了但没进 CABLE）**，完全无法判断。
//
// 现在：每 2 秒把统计写进 %APPDATA%\IPRC1000 Adapter\bridge-stats.json，
// 关键事件（会话开始/结束、长空档）追加到 bridge.log。应用侧读这个文件显示真实帧数。

mod diag {
    use std::io::Write;
    use std::sync::{Mutex, OnceLock};

    static LOG: OnceLock<Mutex<Option<std::fs::File>>> = OnceLock::new();

    /// %APPDATA%\IPRC1000 Adapter\ —— 与应用配置同目录（应用读得到）
    pub fn app_dir() -> Option<std::path::PathBuf> {
        let base = std::env::var("APPDATA").ok()?;
        let d = std::path::PathBuf::from(base).join("IPRC1000 Adapter");
        let _ = std::fs::create_dir_all(&d);
        Some(d)
    }

    fn log_file() -> &'static Mutex<Option<std::fs::File>> {
        LOG.get_or_init(|| {
            let f = app_dir().and_then(|d| {
                std::fs::OpenOptions::new().create(true).append(true)
                    .open(d.join("bridge.log")).ok()
            });
            Mutex::new(f)
        })
    }

    /// 追加一行带时间戳的日志到 bridge.log
    pub fn log(msg: &str) {
        let ts = now_ms();
        if let Ok(mut g) = log_file().lock() {
            if let Some(f) = g.as_mut() {
                let _ = writeln!(f, "{}  {}", hms(ts), msg);
                let _ = f.flush();
            }
        }
    }

    /// 原子写统计文件（先写 .tmp 再 rename，读侧不会看到半个文件）
    pub fn write_stats(json: &str) {
        let Some(d) = app_dir() else { return };
        let tmp = d.join("bridge-stats.json.tmp");
        let dst = d.join("bridge-stats.json");
        if std::fs::write(&tmp, json).is_ok() {
            let _ = std::fs::rename(&tmp, &dst);
        }
    }

    pub fn now_ms() -> u64 {
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map(|d| d.as_millis() as u64)
            .unwrap_or(0)
    }

    fn hms(ms: u64) -> String {
        let s = (ms / 1000) % 86400;
        format!("{:02}:{:02}:{:02}.{:03}", s / 3600, (s / 60) % 60, s % 60, ms % 1000)
    }
}

// ───────────────────────── 一阶高通（级联 2 次 = 二阶） ─────────────────────────

#[derive(Clone, Copy, Default)]
struct Hpf {
    a: f32,
    prev_x: f32,
    prev_y: f32,
}

impl Hpf {
    fn new(fc: f32, sr: f32) -> Self {
        Self {
            a: (-2.0 * std::f32::consts::PI * fc / sr).exp(),
            prev_x: 0.0,
            prev_y: 0.0,
        }
    }
    fn process(&mut self, x: f32) -> f32 {
        let y = self.a * (self.prev_y + x - self.prev_x);
        self.prev_x = x;
        self.prev_y = y;
        y
    }
}

// ───────────────────────── UDP 输出（对齐 pcm_router 协议） ─────────────────────────

struct UdpSink {
    sock: UdpSocket,
    peer: String,
    sent: u64,
    pcm_bytes: u64,
}

impl UdpSink {
    fn new(addr: &str) -> Result<Self, String> {
        let sock = UdpSocket::bind("127.0.0.1:0").map_err(|e| e.to_string())?;
        Ok(Self {
            sock,
            peer: addr.to_string(),
            sent: 0,
            pcm_bytes: 0,
        })
    }
    fn cmd(&self, c: &[u8]) -> bool {
        self.sock.send_to(c, &self.peer).is_ok()
    }
    fn pcm(&mut self, bytes: &[u8]) -> bool {
        match self.sock.send_to(bytes, &self.peer) {
            Ok(_) => {
                self.sent += 1;
                self.pcm_bytes += bytes.len() as u64;
                true
            }
            Err(_) => false,
        }
    }
}

// ───────────────────────── 桥核心：653 字节负载 → 16k 样本 → 48k UDP ─────────────────────────

/// 自我填缓冲：注册提供者 + 定期 EventWrite 一个 4KB 事件
mod flush_feed {
    use std::ffi::c_void;

    #[repr(C)]
    struct EventDescriptor {
        id: u16, version: u8, channel: u8, level: u8,
        opcode: u8, task: u16, keyword: u64,
    }
    #[repr(C)]
    struct EventDataDescriptor {
        ptr: u64, size: u32, reserved: u32,
    }
    #[repr(C)]
    struct Guid16([u8; 16]);

    #[link(name = "advapi32")]
    extern "system" {
        fn EventRegister(provider: *const Guid16, enable_cb: *const c_void,
                         ctx: *const c_void, reg: *mut u64) -> u32;
        fn EventWrite(reg: u64, desc: *const EventDescriptor,
                      count: u32, data: *const EventDataDescriptor) -> u32;
        fn EventUnregister(reg: u64) -> u32;
    }

    /// 直接用 ferrisetw::GUID 的原始 16 字节（它内部就是 Windows 的 GUID 布局）。
    /// 自己手搓字节序会因为字段大小端不一致而匹配不上，导致 EventWrite 的事件被丢弃。
    fn guid_bytes(g: ferrisetw::GUID) -> Guid16 {
        unsafe { std::mem::transmute::<ferrisetw::GUID, Guid16>(g) }
    }

    pub struct Feed { reg: u64, payload: Vec<u8> }

    impl Feed {
        pub fn register(guid: ferrisetw::GUID) -> Option<Feed> {
            let g = guid_bytes(guid);
            let mut reg: u64 = 0;
            let rc = unsafe { EventRegister(&g, std::ptr::null(), std::ptr::null(), &mut reg) };
            if rc != 0 || reg == 0 {
                eprintln!("[!] EventRegister rc={rc}");
                return None;
            }
            Some(Feed { reg, payload: vec![0xA5u8; crate::FLUSH_FEED_BYTES] })
        }

        /// 发一个 4KB 事件 —— 目的是把会话缓冲填满，逼 ETW 立刻刷新
        pub fn feed(&self) -> u32 {
            let desc = EventDescriptor {
                id: 1, version: 0, channel: 0, level: 5, opcode: 0, task: 0,
                keyword: u64::MAX,
            };
            let data = EventDataDescriptor {
                ptr: self.payload.as_ptr() as u64,
                size: self.payload.len() as u32,
                reserved: 0,
            };
            unsafe { EventWrite(self.reg, &desc, 1, &data) }
        }
    }

    impl Drop for Feed {
        fn drop(&mut self) { unsafe { EventUnregister(self.reg); } }
    }
}

/// 当前时间的 FILETIME（100ns 自 1601）—— 与 EventRecord::raw_timestamp 同一时基
#[cfg(target_os = "windows")]
fn now_filetime() -> u64 {
    #[repr(C)]
    struct FileTime { lo: u32, hi: u32 }
    #[link(name = "kernel32")]
    extern "system" { fn GetSystemTimeAsFileTime(t: *mut FileTime); }
    let mut t = FileTime { lo: 0, hi: 0 };
    unsafe { GetSystemTimeAsFileTime(&mut t); }
    ((t.hi as u64) << 32) | t.lo as u64
}
#[cfg(not(target_os = "windows"))]
fn now_filetime() -> u64 { 0 }

/// 填缓冲线程与回调之间共享的"活跃度"信息
struct Activity {
    /// 最近一次收到蓝牙事件的单调时刻（毫秒）
    last_event_ms: std::sync::atomic::AtomicU64,
    /// 遥测：各档填了多少次
    n_hot: std::sync::atomic::AtomicU64,
    n_skip: std::sync::atomic::AtomicU64,
}

impl Activity {
    fn new() -> Self {
        Self {
            last_event_ms: std::sync::atomic::AtomicU64::new(0),
            n_hot: std::sync::atomic::AtomicU64::new(0),
            n_skip: std::sync::atomic::AtomicU64::new(0),
        }
    }
}

/// 遥控器是否还在系统里（只看注册表，1 秒最多查一次）
#[cfg(target_os = "windows")]
fn remote_present() -> bool {
    use std::os::windows::ffi::OsStrExt;
    let path: Vec<u16> = std::ffi::OsStr::new("SYSTEM\\CurrentControlSet\\Enum\\HID")
        .encode_wide().chain(std::iter::once(0)).collect();
    #[link(name = "advapi32")]
    extern "system" {
        fn RegOpenKeyExW(h: usize, sub: *const u16, opt: u32, sam: u32, out: *mut usize) -> i32;
        fn RegEnumKeyExW(h: usize, idx: u32, name: *mut u16, len: *mut u32,
                         res: *mut u32, class: *mut u16, clen: *mut u32, ft: *mut u8) -> i32;
        fn RegCloseKey(h: usize) -> i32;
    }
    const HKEY_LOCAL_MACHINE: usize = 0x8000_0002;
    const KEY_READ: u32 = 0x2_0019;
    unsafe {
        let mut hk: usize = 0;
        if RegOpenKeyExW(HKEY_LOCAL_MACHINE, path.as_ptr(), 0, KEY_READ, &mut hk) != 0 { return false; }
        let mut buf = [0u16; 256];
        let mut i = 0u32;
        let mut found = false;
        loop {
            let mut len = buf.len() as u32;
            let rc = RegEnumKeyExW(hk, i, buf.as_mut_ptr(), &mut len,
                                   std::ptr::null_mut(), std::ptr::null_mut(),
                                   std::ptr::null_mut(), std::ptr::null_mut());
            if rc != 0 { break; }
            let name = String::from_utf16_lossy(&buf[..len as usize]).to_lowercase();
            if name.contains("0a5c") && name.contains("8502") { found = true; break; }
            i += 1;
        }
        RegCloseKey(hk);
        found
    }
}
#[cfg(not(target_os = "windows"))]
fn remote_present() -> bool { true }

struct Bridge {
    hpf: Option<[Hpf; 2]>,
    udp: Option<UdpSink>,
    key_sink: Option<UdpSink>,
    last_usage: Option<u8>,
    /// 最近一个事件的 ETW 时间戳（100ns 自 1601，即 FILETIME）—— 用来实测投递延迟
    last_ts: u64,
    stats: Arc<Mutex<Stats>>,
    wav: Option<hound::WavWriter<std::io::BufWriter<std::fs::File>>>,
    in_session: bool,
    last_frame_at: Option<Instant>,
    prev: i16, // 48k 内插用的上一个样本
    have_prev: bool,
    samples16: u64,
    warmup: u32, // 丢掉最前面几帧（高通瞬态 + 首个不完整段）
    pending: Option<Vec<u8>>, // ETW 回调里暂存的首片 ACL 数据
}

impl Bridge {
    fn new(
        hp: f32,
        udp: Option<UdpSink>,
        wav: Option<hound::WavWriter<std::io::BufWriter<std::fs::File>>>,
        key_sink: Option<UdpSink>,
        stats: Arc<Mutex<Stats>>,
    ) -> Self {
        Self {
            hpf: if hp > 0.0 {
                Some([Hpf::new(hp, 16000.0), Hpf::new(hp, 16000.0)])
            } else {
                None
            },
            udp,
            key_sink,
            last_usage: None,
            last_ts: 0,
            stats,
            wav,
            in_session: false,
            last_frame_at: None,
            prev: 0,
            have_prev: false,
            samples16: 0,
            warmup: 0,
            pending: None,
        }
    }

    /// 处理一个 ACL 分片（`acl` = 去掉 4 字节 ACL 头之后的数据）—— **实时与回放共用同一条路**
    fn handle_fragment(&mut self, flag: u8, dlen: usize, acl: &[u8]) {
        // ── 音频：首片(344) + 续片(313) ──
        if flag == 2 && dlen == 344 {
            if acl.len() >= 344 {
                // ★ 上一个首片还没等到续片，新首片就来了 → 那条续片被 ETW 丢了
                //   （缓冲区不够大 / 消费跟不上时 ETW 会直接丢事件）
                //   这就是"音频丢帧"的直接量化指标。
                if self.pending.is_some() {
                    self.stats.lock().unwrap().lost_cont += 1;
                }
                self.pending = Some(acl[..344].to_vec());
            } else {
                self.pending = None;
                self.stats.lock().unwrap().truncated_first += 1;
            }
            return;
        }
        if flag == 1 && dlen == 313 {
            if let Some(mut head) = self.pending.take() {
                let n = acl.len().min(313);
                head.extend_from_slice(&acl[..n]);
                if head.len() == FRAME_LEN + 4 {
                    self.push_payload(&head[4..]);
                    self.stats.lock().unwrap().frames += 1;
                }
            }
            return;
        }
        // ── 按键：单分片 HID 报告（L2CAP CID 0x41，报告头 a1 01） ──
        if flag == 2 && acl.len() >= 12 && acl[2] == 0x41 && acl[4] == 0xa1 && acl[5] == 0x01 {
            self.stats.lock().unwrap().hid_reports += 1;
            let end = acl.len().min(14);
            let usages: Vec<u8> = acl[8..end].to_vec();
            self.handle_hid_report(&usages);
        }
    }

    /// HID 键盘报告 → 按下 / 松开事件（同一 usage 重复上报会被去重）
    fn handle_hid_report(&mut self, usages: &[u8]) {
        match usages.iter().copied().find(|&u| u != 0) {
            Some(u) => {
                if self.last_usage != Some(u) {
                    let old = self.last_usage;
                    self.emit_key(old, false); // 先放掉上一个
                    self.last_usage = Some(u);
                    self.emit_key(Some(u), true);
                }
            }
            None => {
                if let Some(u) = self.last_usage.take() {
                    self.emit_key(Some(u), false);
                }
            }
        }
    }

    fn emit_key(&self, usage: Option<u8>, down: bool) {
        let Some(u) = usage else { return };
        self.stats.lock().unwrap().key_events += 1;
        // 附带 ETW 时间戳，应用侧可算"设备产生事件 → 我们收到"的真实延迟
        let msg = format!("KEY {u:02X} {} {}\n", if down { 1 } else { 0 }, self.last_ts);
        if let Some(k) = self.key_sink.as_ref() {
            let _ = k.cmd(msg.as_bytes());
        }
        println!("[KEY] usage=0x{u:02X}  {}", if down { "DOWN" } else { "UP" });
    }

    /// 一个完整音频帧的 653 字节负载
    fn push_payload(&mut self, p: &[u8]) {
        if p.len() != FRAME_LEN {
            return;
        }
        // ── 帧间空档统计：长录音里"说着说着没声"就是靠这个定位 ──
        if let Some(t) = self.last_frame_at {
            let gap = t.elapsed().as_millis() as u64;
            {
                let mut s = self.stats.lock().unwrap();
                if gap > s.max_gap_ms { s.max_gap_ms = gap; }
                if gap >= IDLE_END_MS {
                    s.gaps += 1;
                    diag::log(&format!("音频空档 {gap} ms（>= IDLE_END_MS {IDLE_END_MS}），会触发 END→CLEAR"));
                }
            }
        }
        let body = &p[HDR..HDR + SAMPLES * 2];
        let mut s16 = [0i16; SAMPLES];
        for (i, c) in body.chunks_exact(2).enumerate() {
            let mut v = i16::from_le_bytes([c[0], c[1]]) as f32;
            if let Some(f) = self.hpf.as_mut() {
                let once = f[1].process(v);
                v = f[0].process(once);
            }
            s16[i] = v.clamp(-32768.0, 32767.0) as i16;
        }

        // 丢掉最前面几帧：高通初始瞬态
        if self.warmup < 2 {
            self.warmup += 1;
            self.last_frame_at = Some(Instant::now());
            return;
        }

        if let Some(w) = self.wav.as_mut() {
            for v in &s16 {
                let _ = w.write_sample(*v);
            }
        }

        if let Some(u) = self.udp.as_mut() {
            if !self.in_session {
                u.cmd(b"CLEAR");
                self.in_session = true;
                self.have_prev = false;
                self.stats.lock().unwrap().sessions += 1;
                diag::log("会话开始（CLEAR）");
            }
            // 16k → 48k：与 voice_pcm::push_16k 一致的三点线性内插
            let mut prev = if self.have_prev { self.prev } else { s16[0] };
            let mut out = Vec::with_capacity(SAMPLES * 3 * 2);
            for &cur in s16.iter() {
                let d = cur as i32 - prev as i32;
                for v in [
                    (prev as i32 + d / 3) as i16,
                    (prev as i32 + d * 2 / 3) as i16,
                    cur,
                ] {
                    out.extend_from_slice(&v.to_le_bytes());
                }
                prev = cur;
            }
            self.prev = s16[SAMPLES - 1];
            self.have_prev = true;
            if !u.pcm(&out) {
                self.stats.lock().unwrap().udp_err += 1;
            }
        }

        self.samples16 += SAMPLES as u64;
        self.last_frame_at = Some(Instant::now());
        self.stats.lock().unwrap().last_frame_ms = diag::now_ms();
    }

    /// 空闲检测：超过 IDLE_END_MS 没有帧就发 END，让 router 关掉 CABLE 流
    fn tick(&mut self) {
        if !self.in_session {
            return;
        }
        if let Some(t) = self.last_frame_at {
            if t.elapsed() >= Duration::from_millis(IDLE_END_MS) {
                if let Some(u) = self.udp.as_ref() {
                    u.cmd(b"END");
                }
                self.in_session = false;
                diag::log(&format!(
                    "会话结束（END）—— 已静默 {} ms",
                    t.elapsed().as_millis()
                ));
            }
        }
    }

    fn finish(&mut self) {
        if self.in_session {
            if let Some(u) = self.udp.as_ref() {
                u.cmd(b"END");
            }
            self.in_session = false;
        }
        if let Some(w) = self.wav.take() {
            let _ = w.finalize();
        }
    }
}

// ───────────────────────── main ─────────────────────────

struct Args {
    wav: Option<String>,
    replay: Option<String>,
    udp: Option<String>,
    key_udp: Option<String>,
    no_feed: bool,
    hp: f32,
    seconds: u64,
    realtime: bool,
    rate: u32,
}

fn parse_args() -> Args {
    let a: Vec<String> = std::env::args().collect();
    let mut out = Args {
        wav: None,
        replay: None,
        udp: None,
        key_udp: None,
        no_feed: false,
        hp: 0.0,
        seconds: 30,
        realtime: true,
        rate: 16000,
    };
    let mut i = 1;
    while i < a.len() {
        let key = a[i].as_str();
        let val = a.get(i + 1).cloned();
        let mut consumed = 2;
        match key {
            "--wav" => out.wav = val,
            "--replay" => out.replay = val,
            "--udp" => out.udp = val,
            "--key-udp" => out.key_udp = val,
            "--no-feed" => { out.no_feed = true; consumed = 1; }
            "--no-key-udp" => {
                out.key_udp = Some(String::new());
                consumed = 1;
            }
            "--hp" => out.hp = val.and_then(|s| s.parse().ok()).unwrap_or(0.0),
            "--seconds" => out.seconds = val.and_then(|s| s.parse().ok()).unwrap_or(30),
            "--rate" => out.rate = val.and_then(|s| s.parse().ok()).unwrap_or(16000),
            "--fast" => {
                out.realtime = false;
                consumed = 1;
            }
            _ => {
                i += 1;
                continue;
            }
        }
        i += consumed;
    }
    out
}

fn make_wav(path: &str, rate: u32) -> Option<hound::WavWriter<std::io::BufWriter<std::fs::File>>> {
    let spec = hound::WavSpec {
        channels: 1,
        sample_rate: rate,
        bits_per_sample: 16,
        sample_format: hound::SampleFormat::Int,
    };
    match hound::WavWriter::create(path, spec) {
        Ok(w) => {
            println!("[i] 同时写出 WAV: {path}");
            Some(w)
        }
        Err(e) => {
            eprintln!("[!] 建 WAV 失败 {path}: {e}");
            None
        }
    }
}

const TRACE_NAME: &str = "Iprc1000AudioBridge";

/// 清掉同名残留 ETW 会话。
///
/// **必须做**：上一次进程被强杀（任务管理器结束 / 父进程带崩）时，ETW 会话不会自动注销，
/// 下次 `StartTrace` 会直接报 `AlreadyExist` —— 用户看到的现象就是"语音桥启动失败/没声音、
/// 而且没有任何提示"。这里先无条件 stop 一次同名会话。
fn cleanup_stale_session() {
    #[cfg(target_os = "windows")]
    {
        use std::os::windows::process::CommandExt;
        const CREATE_NO_WINDOW: u32 = 0x0800_0000;
        let _ = std::process::Command::new("logman")
            .args(["stop", TRACE_NAME, "-ets"])
            .stdout(std::process::Stdio::null())
            .stderr(std::process::Stdio::null())
            .creation_flags(CREATE_NO_WINDOW)
            .status();
    }
}

/// 单实例保护。
///
/// 为什么需要：`cleanup_stale_session()` 在启动时无条件执行
/// `logman stop Iprc1000AudioBridge -ets`。若已有桥在跑，第二个实例会把它的
/// ETW 会话停掉 —— **老进程从此收不到任何事件：进程活着、看起来一切正常，但永久静音**。
/// 应用侧靠"进程是否存活"判断桥的状态，完全看不出这种僵死。
#[cfg(target_os = "windows")]
fn acquire_single_instance() -> bool {
    use std::os::windows::ffi::OsStrExt;
    #[link(name = "kernel32")]
    extern "system" {
        fn CreateMutexW(attr: *const std::ffi::c_void, owner: i32, name: *const u16) -> isize;
        fn GetLastError() -> u32;
    }
    const ERROR_ALREADY_EXISTS: u32 = 183;
    unsafe {
        let name: Vec<u16> = std::ffi::OsStr::new("Global\\Iprc1000AudioBridge_SingleInstance")
            .encode_wide()
            .chain(std::iter::once(0))
            .collect();
        let h = CreateMutexW(std::ptr::null(), 0, name.as_ptr());
        if h == 0 {
            return true; // 建不出来就不挡路
        }
        // 句柄故意泄漏：它要活到进程结束，才能一直占住这个互斥体
        GetLastError() != ERROR_ALREADY_EXISTS
    }
}
#[cfg(not(target_os = "windows"))]
fn acquire_single_instance() -> bool {
    true
}

fn main() {
    let args = parse_args();
    let stats = Arc::new(Mutex::new(Stats::default()));

    if !args.replay.is_some() && !acquire_single_instance() {
        eprintln!("[!] 已有语音桥在运行，本实例直接退出（避免掐掉正在用的 ETW 会话）。");
        return;
    }

    let udp = match args.udp.as_deref() {
        Some(addr) => match UdpSink::new(addr) {
            Ok(u) => {
                println!("[i] UDP → {addr}（协议 CLEAR / 48k PCM / END）");
                Some(u)
            }
            Err(e) => {
                eprintln!("[!] UDP 初始化失败: {e}");
                None
            }
        },
        None => None,
    };
    // 按键事件通道（默认 127.0.0.1:31681；用 --no-key-udp 关掉）
    let key_addr = args.key_udp.clone().unwrap_or_else(|| format!("127.0.0.1:{KEY_PORT}"));
    let key_sink = if key_addr.is_empty() {
        None
    } else {
        match UdpSink::new(&key_addr) {
            Ok(k) => {
                println!("[i] 按键事件 → {key_addr}（格式: \"KEY <usage> <1|0>\"）");
                Some(k)
            }
            Err(e) => {
                eprintln!("[!] 按键 UDP 初始化失败: {e}");
                None
            }
        }
    };

    let wav = args.wav.as_deref().and_then(|p| make_wav(p, args.rate));
    let bridge = Arc::new(Mutex::new(Bridge::new(args.hp, udp, wav, key_sink, Arc::clone(&stats))));

    // ★ 每 2 秒把统计写盘。
    //
    // 桥以 --seconds 86400 常驻，而统计原来**只在退出时**打印一次 —— 等于永远看不到。
    // 应用侧读这个文件显示真实的「音频帧」，我们排查"说着说着没声了"也靠它区分：
    //   frames 不涨      → 帧根本没来（遥控器/BT/ETW 丢事件）→ 看 lostCont / gaps / silentMs
    //   frames 在涨      → 帧来了，问题在 UDP / CABLE 路由 / WASAPI 这一侧
    // 注意**无论有没有变化都要写**（内含 heartbeat），否则"活着但聋了"和"死了"分不出来。
    {
        let st = Arc::clone(&stats);
        let br = Arc::clone(&bridge);
        let t0 = Instant::now();
        std::thread::spawn(move || loop {
            std::thread::sleep(Duration::from_millis(2000));
            let json = {
                let Ok(s) = st.lock() else { continue };
                let Ok(g) = br.lock() else { continue };
                let (sent, bytes) = g
                    .udp
                    .as_ref()
                    .map(|u| (u.sent, u.pcm_bytes))
                    .unwrap_or((0, 0));
                let now = diag::now_ms();
                let silent: i64 = if s.last_frame_ms == 0 {
                    -1 // 从未收到过音频帧
                } else {
                    (now.saturating_sub(s.last_frame_ms)) as i64
                };
                format!(
                    concat!(
                        "{{\"pid\":{},\"uptimeSec\":{},\"heartbeatMs\":{},",
                        "\"frames\":{},\"samples16\":{},\"seconds16\":{:.2},",
                        "\"udpPackets\":{},\"udpBytes\":{},",
                        "\"sessions\":{},\"gaps\":{},\"maxGapMs\":{},",
                        "\"lostCont\":{},\"truncatedFirst\":{},\"udpErr\":{},",
                        "\"hidReports\":{},\"keyEvents\":{},\"otherEvents\":{},",
                        "\"silentMs\":{},\"inSession\":{}}}"
                    ),
                    std::process::id(),
                    t0.elapsed().as_secs(),
                    now,
                    s.frames,
                    g.samples16,
                    g.samples16 as f64 / 16000.0,
                    sent,
                    bytes,
                    s.sessions,
                    s.gaps,
                    s.max_gap_ms,
                    s.lost_cont,
                    s.truncated_first,
                    s.udp_err,
                    s.hid_reports,
                    s.key_events,
                    s.other,
                    silent,
                    if g.in_session { 1 } else { 0 }
                )
            };
            diag::write_stats(&json);
        });
    }

    if let Some(path) = args.replay.clone() {
        replay_mode(&path, Arc::clone(&bridge), args.realtime, Arc::clone(&stats));
        return;
    }

    println!("[i] 实时 ETW 模式（需要管理员权限）");
    if args.hp > 0.0 {
        println!("[i] 高通 {:.0} Hz", args.hp);
    }
    let bridge_cb = Arc::clone(&bridge);
    let stats_cb = Arc::clone(&stats);
    // 活跃度共享：回调更新、填缓冲线程按它选档（热/温/冷/设备不在）
    let activity = std::sync::Arc::new(Activity::new());
    let act_cb = std::sync::Arc::clone(&activity);

    let feed_guid = ferrisetw::GUID::from_u128(FLUSH_FEED_GUID);
    let provider = Provider::by_guid(BTHPORT_GUID)
        .level(5)
        .any(u64::MAX) // ★ 关键字掩码默认 0 会收不到任何事件
        .add_callback(
            move |record: &EventRecord, schema_locator: &SchemaLocator| {
                let Ok(schema) = schema_locator.event_schema(record) else {
                    return;
                };
                let parser = Parser::create(record, &schema);
                let bip_type: u32 = parser
                    .try_parse::<u8>("BIP_Type")
                    .map(u32::from)
                    .or_else(|_| parser.try_parse::<u16>("BIP_Type").map(u32::from))
                    .unwrap_or(0);
                if bip_type != 3 {
                    // 链路层事件是持续流动的（不需要按遥控器），用它来测「产生→收到」的延迟
                    if let Ok(mut s) = stats_cb.lock() {
                        s.other += 1;
                        let now = now_filetime();
                        let ts = record.raw_timestamp() as u64;
                        if ts > 0 && now > ts {
                            let ms = (now - ts) / 10_000;   // 100ns -> ms
                            if ms < 10_000 {
                                s.lat_n += 1;
                                s.lat_sum += ms;
                                if ms > s.lat_max { s.lat_max = ms; }
                            }
                        }
                    }
                    return;
                }
                let Ok(data) = parser.try_parse::<Vec<u8>>("BIP_Data") else {
                    return;
                };
                if data.len() < 4 {
                    return;
                }
                let dlen = u16::from_le_bytes([data[2], data[3]]) as usize;
                let flag = ((u16::from_le_bytes([data[0], data[1]]) >> 12) & 0x3) as u8;
                let acl = data[4..].to_vec();
                {
                    let mut g = bridge_cb.lock().unwrap();
                    g.last_ts = record.raw_timestamp() as u64;
                    g.handle_fragment(flag, dlen, &acl);
                    drop(g);
                    // ★ 更新活跃度：填缓冲线程据此选档（热 10ms / 温 50ms / 冷 200ms）
                    {
                        let now = std::time::SystemTime::now()
                            .duration_since(std::time::UNIX_EPOCH)
                            .map(|d| d.as_millis() as u64).unwrap_or(0);
                        act_cb.last_event_ms.store(now, Ordering::Relaxed);
                    }
                }
            },
        )
        .build();

    cleanup_stale_session();

    // ★ 把 ETW 缓冲调到最小（1 页 = 4 KB）。
    //
    // ferrisetw 的 `flush_timer` 最小只能取整到 **1 秒** —— 也就是说空闲时（没有音频流），
    // 单个按键事件要等最多 1 秒才被投递，这正是"上下左右不精确/一顿一顿"的根因
    // （音频以 50 Hz 持续来，很快填满缓冲所以很顺；按键不行）。
    //
    // 补救办法：**把缓冲设到最小**。缓冲越小越快被填满 → 越快刷新 → 越快投递。
    // 蓝牙链路本身有持续的控制/信令流量，所以小缓冲也能被反复填满，延迟从"最多 1 秒"
    // 降到几十毫秒量级。
    let props = ferrisetw::trace::TraceProperties {
        buffer_size: 4,                 // KB，1 页
        min_buffer: 2,
        max_buffer: 4,
        flush_timer: Duration::from_secs(1),   // API 只能到秒
        ..Default::default()
    };
    // ★ 把我们自己的"填缓冲"提供者挂到同一个会话上，这样 EventWrite 的事件才会进这块缓冲
    let feed_provider = ferrisetw::provider::Provider::by_guid(feed_guid)
        .level(5)
        .any(u64::MAX)
        .add_callback(|_rec: &EventRecord, _sl: &SchemaLocator| { /* 只为自己产事件，不做处理 */ })
        .build();

    let trace = match UserTrace::new()
        .set_trace_properties(props)
        .named(TRACE_NAME.to_string())
        .enable(provider)
        .enable(feed_provider)
        .start_and_process()
    {
        Ok(t) => t,
        Err(e) => {
            eprintln!("[!] 启动 ETW 会话失败: {e:?}（请以管理员运行）");
            return;
        }
    };

    // ★ 填缓冲线程：每 50 ms 发一个 4KB 事件 -> 逼 ETW 立刻刷新缓冲 -> 按键延迟降到 ~50ms
    let feed_enabled = !args.no_feed;
    let feed = flush_feed::Feed::register(feed_guid);
    if let Some(fd) = feed.as_ref() {
        let rc = fd.feed();
        println!("[i] 填缓冲自检：EventWrite rc={rc}（0=成功）");
    }
    if !feed_enabled {
        println!("[i] 填缓冲已禁用（--no-feed，用于 A/B 对比）");
    } else {
        match feed.as_ref() {
            Some(_) => println!(
                "[i] 填缓冲：遥控器在线时恒定 {FLUSH_FEED_HOT_MS}ms 一格，每次 {FLUSH_FEED_BYTES} 字节；不在线时停填"),
            None => eprintln!("[!] 填缓冲提供者注册失败（按键延迟会维持在 ~1 秒）"),
        }
    }
    let feed_handle = std::sync::Arc::new(feed);
    {
        let f = std::sync::Arc::clone(&feed_handle);
        let act = std::sync::Arc::clone(&activity);
        if feed_enabled {
            std::thread::spawn(move || {
                let mut present_checked = 0u64;
                let mut present = true;
                loop {
                    let now = std::time::SystemTime::now()
                        .duration_since(std::time::UNIX_EPOCH)
                        .map(|d| d.as_millis() as u64).unwrap_or(0);
                    let last = act.last_event_ms.load(Ordering::Relaxed);

                    // 遥控器在线性：每秒最多查一次注册表
                    if now.saturating_sub(present_checked) > 1000 {
                        present = remote_present();
                        present_checked = now;
                    }

                    let idle = if last == 0 { u64::MAX } else { now.saturating_sub(last) };
                    // ★ 遥控器在线时**始终用热档（10 ms）**，不再按空闲时长降档。
                    //
                    // 原因：ETW 缓冲只有在被填满或到 flush_timer 时才刷新，而降档到"冷"
                    // （200 ms）后，用户按下语音键产生的**头几批音频事件最多要等 200 ms**
                    // 才被投递出去 —— 这正好落在"按住键立刻说话"的第一个字上。
                    // 遥控器在线就说明用户随时可能开口，用 10 ms 换来"按键即出声"值得；
                    // 设备不在线时仍然基本停填（那时也不会有音频）。
                    let interval = if !present {
                        act.n_skip.fetch_add(1, Ordering::Relaxed);
                        FLUSH_FEED_COLD_MS * 5          // 设备不在：基本停填
                    } else {
                        act.n_hot.fetch_add(1, Ordering::Relaxed);
                        FLUSH_FEED_HOT_MS               // 在线：恒定 10 ms
                    };
                    let _ = idle;                       // 保留统计，不再用于选档

                    if present {
                        if let Some(fd) = f.as_ref() { fd.feed(); }
                    }
                    std::thread::sleep(Duration::from_millis(interval));
                }
            });
        }
    }

    let deadline = Instant::now() + Duration::from_secs(args.seconds.max(1));
    while Instant::now() < deadline {
        std::thread::sleep(Duration::from_millis(50));
        bridge.lock().unwrap().tick();
    }
    let _ = trace.stop();
    report(&stats, &bridge);
}

/// 回放格式（由 tools/dump_payloads.py 生成）：
///   repeat { u8 flag; u8 pad; u16 dlen(LE); u32 dt_ms(LE); u16 acl_len(LE); u8 acl[acl_len] }
///   `dlen` 是 ACL 头里声明的长度（首片被截断时会是 344 而实际只有几字节），
///   `acl_len` 是实际存下来的字节数；`dt_ms` 相对上一条的毫秒间隔，用于精确节奏。
fn replay_mode(path: &str, bridge: Arc<Mutex<Bridge>>, realtime: bool, stats: Arc<Mutex<Stats>>) {
    let data = match std::fs::read(path) {
        Ok(d) => d,
        Err(e) => {
            eprintln!("[!] 读回放文件失败 {path}: {e}");
            return;
        }
    };
    println!(
        "[i] 回放 {path}: {} 字节{}",
        data.len(),
        if realtime { "，按原始时间轴节奏" } else { "，全速" }
    );
    let mut off = 0usize;
    let mut n = 0usize;
    let mut acc = Duration::ZERO;
    let t0 = Instant::now();
    while off + 10 <= data.len() {
        let flag = data[off];
        let dlen = u16::from_le_bytes([data[off + 2], data[off + 3]]) as usize;
        let dt = u32::from_le_bytes([data[off + 4], data[off + 5], data[off + 6], data[off + 7]]);
        let acl_len = u16::from_le_bytes([data[off + 8], data[off + 9]]) as usize;
        off += 10;
        if off + acl_len > data.len() || acl_len == 0 {
            break;
        }
        let acl = data[off..off + acl_len].to_vec();
        off += acl_len;
        bridge.lock().unwrap().handle_fragment(flag, dlen, &acl);
        n += 1;
        if realtime {
            acc += Duration::from_millis(dt as u64);
            let got = t0.elapsed();
            if acc > got {
                std::thread::sleep(acc - got);
            }
        }
    }
    println!("[i] 回放记录 {n} 条");
    // 收尾：先跑几轮 tick，再**显式 finish()** 把 END 发出去。
    // 以前只靠 tick 的 12×50ms=600ms 等空闲超时，而 IDLE_END_MS 从 250 提到 600 之后
    // 正好卡在边界上 —— END 可能发不出去，下游就一直保持会话不关。
    for _ in 0..6 {
        std::thread::sleep(Duration::from_millis(50));
        bridge.lock().unwrap().tick();
    }
    bridge.lock().unwrap().finish();
    report(&stats, &bridge);
}

fn report(stats: &Arc<Mutex<Stats>>, bridge: &Arc<Mutex<Bridge>>) {
    let st = stats.lock().unwrap();
    let mut g = bridge.lock().unwrap();
    let (sent, bytes) = g
        .udp
        .as_ref()
        .map(|u| (u.sent, u.pcm_bytes))
        .unwrap_or((0, 0));
    println!(
        "\n[i] 音频帧 {}  被截断首片 {}  HID报告 {}  按键事件 {}  其他事件 {}",
        st.frames, st.truncated_first, st.hid_reports, st.key_events, st.other
    );
    if st.lat_n > 0 {
        println!(
            "[i] ETW 投递延迟（链路层事件实测）: 平均 {} ms  最大 {} ms  （{} 次采样）",
            st.lat_sum / st.lat_n, st.lat_max, st.lat_n
        );
    }
    println!(
        "[i] 16k 样本 {} = {:.2}s    UDP 包 {}  共 {} 字节 @48k",
        g.samples16,
        g.samples16 as f64 / 16000.0,
        sent,
        bytes
    );
    if st.truncated_first > 0 && st.frames == 0 {
        println!("[!] 只收到被截断的首片 → MaxEtwBytes 未生效（需重启），首片只有 8 字节头");
    }
    g.finish();
}
