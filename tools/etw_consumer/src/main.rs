//! IPRC1000 语音桥：BthPort ETW → 完整帧解码 → UDP 推给 audio router → VB-CABLE → 输入法
//!
//! ## 帧结构（逐字节统计确认，`MaxEtwBytes` 生效后）
//! L2CAP 负载 653 字节：
//! ```text
//! [0..12]   13 字节帧头   A1 F7 01 <seq> 00 <roll> <n> <cnt> 00 00 00 40 01
//! [13..652] 640 字节音频 = 320 个 16bit LE 样本 @16 kHz
//! ```
//! ACL 上**常见**是拆两片：首片 `dlen=344`(PB=10) + 续片 `dlen=313`(PB=01) = 657 = L2CAP头(4)+负载(653)
//!
//! ⚠⚠ **但这个切法不是固定的**：2026-10-16 实测同一个 657 字节 PDU 被拆成了
//! **344 + 290 + 23 三片**（`flag=1 dlen=290` 与 `flag=1 dlen=23` 各 512 次）。老解析器
//! 写死"续片必须 313"，于是**把一整分钟完好的音频全扔了**（那段时间 `seq跳号 = 0`，
//! 说明上游一帧没丢 —— 是我们自己丢的）。现在**按 PB 标志 + L2CAP 声明长度组帧**：
//! 起始分片（PB=10）开一个新 PDU、后续分片（PB=01）无脑往上拼，拼到 `4 + L2CAP长度` 就交出去。
//! **分几片、每片多长，都不再影响正确性。**
//!
//! ⚠ **帧头不是常量**（2026-10-16 由 macOS 版实测纠正，见 `lymsiq/IPRC1000-Mac` 的
//! `docs/MACOS_VOICE.md`）：第 5 字节 `<roll>` **逐帧在变**（0x42/0x82/0xC2/0x02…每帧 +0x40），
//! 第 6 字节 `<n>` 也每帧 +9。早期笔记把它记成固定的 `00 81 00` —— **那是错的**，
//! 照它做内容匹配会一帧都命中不了（macOS 版就因此一直报「音频帧 0」）。
//! 真正恒定的是 `A1 F7 01` + `[4]==0` + 尾部 `00 00 00 40 01`。
//! 我们**按 PB 标志组帧**，所以不受影响；`HDR_SIG/HDR_TAIL` 只用来做**自检**。
//!
//! ★ 第 3 字节 `<seq>` 是**逐帧 +1（1 字节回绕）的序号** —— 这是把"丢帧"从猜测
//!   变成测量的关键：seq 连续就说明这期间一帧没丢，跳号才是真丢帧（见 `Stats::seq_*`）。
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
/// 帧头里**恒定**的两段（可变字节见文件头注释：第 5/6 字节逐帧在变）
const HDR_SIG: [u8; 3] = [0xA1, 0xF7, 0x01];
const HDR_TAIL: [u8; 2] = [0x40, 0x01];
/// 帧头 `seq` 所在字节（1 字节、逐帧 +1、到 255 回绕）
const HDR_SEQ_IDX: usize = 3;
/// L2CAP 头长度（帧内容以它开头：长度 2 + CID 2）
const L2CAP_HDR: usize = 4;
/// 一帧音频的 ACL 总长 = L2CAP 头(4) + 负载(653)
const AUDIO_TOTAL: usize = L2CAP_HDR + FRAME_LEN;
/// 遥控器把音频与按键都发在 L2CAP CID 0x41（HID 控制通道）上
/// HID 数据通道的 CID **不写死**（见 `is_data_cid` 的注释）。
///
/// 2026-10-17 实测踩到：L2CAP 的动态 CID 是**每次连接按升序分配**的。
/// Windows 的 HIDP 会开两条通道（Control / Interrupt）；**谁先开谁拿 0x0040**。
/// 曾经把数据 CID 定死成 `0x41`，某次重连后变得"按键全失效、音频一帧没有"，
/// 桥日志里全是 `未识别分片 dlen=14`，而 `首条未识别` 的 CID 是 **0x0040**。
/// （`package.py` 有两条反向断言盯着，别把那两种写法改回来。）
const CID_DYNAMIC_LO: u16 = 0x0040;
const CID_DYNAMIC_HI: u16 = 0x007F;

/// 是不是"动态分配的 HID 数据通道"CID。
///
/// 为什么用范围而不是等值：同一只遥控器不同次连接会落在 0x40/0x41 任一个上。
/// 真正区分"这是哪一路数据"的**不是 CID，而是 L2CAP 长度 + 报告头**（见下面的分派）。
fn is_data_cid(cid: u16) -> bool {
    (CID_DYNAMIC_LO..=CID_DYNAMIC_HI).contains(&cid)
}
/// L2CAP **信令**通道：建链/协商用的小包（`08 00 01 00 02 02 04 00` 这类），与音频/按键无关
const CID_SIG: u16 = 0x0001;
/// 按键报告（HID 输入报告）的 L2CAP 负载长度
const HID_PAYLOAD_LEN: usize = 10;
/// 无帧多久发 END。
///
/// ⚠ 曾经是 250 ms —— 蓝牙链路正常抖动（RF 干扰、ACL 重传）很容易超过它，
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
    /// 被当作"基线"的报文数（每段 1 条；若它带着键，说明挡掉了一次假按下）
    seed_reports: u64,
    /// 桥启动后收到的**第一个按键事件**：距启动多少毫秒 / 是哪个 usage / 是否被静默期吃掉。
    /// 用途很具体：下一次"启动就弹右键"发生时，直接看这三个数就知道
    /// "有没有按键、什么键、是启动瞬间还是几秒后"，不用再猜。
    /// u64::MAX = 还没有收到过任何带键的报文
    first_key_ms: u64,
    /// 是否收到过"带键"的报文（false ⇒ firstKeyMs 输出 -1）
    first_key_seen: bool,
    /// 被丢弃的"纯幽灵报文"数（只含 0x01/0x0C，不代表键盘状态）
    ghost_reports: u64,
    first_key_usage: u8,
    /// 实测按键报告走的是哪个 CID（第一次见到时记录，诊断用）
    key_cid: Option<u16>,
    /// 实测音频走的是哪个 CID
    audio_cid: Option<u16>,
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
    /// 首片完整、但配对的续片**根本没到**的次数（= 组装失败的帧数）
    lost_cont: u64,
    /// UDP 发送失败次数
    udp_err: u64,

    // ── 帧级丢失定位（2026-10-16 加，起因："连续语音之后按键没声音"）──
    //
    // 原来的盲区：只有 `lost_cont` 一个累计数，30 秒才打一行，
    // 既分不清"事件被 ETW 丢了"还是"分片长度变了"，也看不出哪一秒开始丢。
    // 现在：用**帧头 seq**（逐帧 +1）把真丢帧量出来，并给每条"被拒绝"的分片留痕。
    /// 帧头签名不符的帧数（按理应为 0；不为 0 说明帧格式与预期不同）
    hdr_odd: u64,
    /// 帧头 seq 跳号次数（= 真丢帧：遥控器发过、我们没收到）
    seq_jumps: u64,
    /// seq 累计跳过的帧数（估计丢失帧数）
    seq_lost: u64,
    /// 序号跳号里**属于静音期**的那部分（遥控器不说话时根本不发帧 ⇒ 序号自然跳过）。
    /// 这不是丢失 —— 单独统计，避免面板把"静音"误报成"丢帧"（2026-10-19 真机踩过）。
    seq_silence: u64,
    /// seq 重复 / 回退次数（重复帧或乱序）
    seq_dup: u64,
    /// 后续分片自身被截断（负载短于 ACL 头声明的长度）的次数
    trunc_cont: u64,
    /// 没被任何规则识别的 ACL 分片数
    odd_frag: u64,
    /// 上面那些分片的 `(flag, dlen)` 分布（最多记 8 类）—— 长度一变就能看出来
    odd_kinds: Vec<(u8, u16, u64)>,
    /// 第一条未识别分片的前 8 字节（十六进制）—— 光有长度不够，得看内容才能定性
    odd_sample: String,
    /// 没有起始分片就来了的后续分片（起始分片被 ETW 丢了）
    orphan_cont: u64,
    /// **后续分片的长度分布**（最多记 8 类）—— 2026-10-16 那次事故就是靠它定位的：
    /// 续片从「一片 313」变成了「290 + 23」，写死长度的解析器一片都收不下。
    cont_kinds: Vec<(u16, u64)>,
    /// 拼够长度但长度不是我们认识的音频帧（比如换了音频配置）
    short_pdu: u64,
    /// L2CAP 信令包（CID 0x0001）—— **单独计数，不算"未识别"**：
    /// 否则界面上会显示"长度不认识 N"，让人以为帧格式出了问题
    /// （实测 86 条/100 分钟，全是建链协商用的小包）
    sig_frag: u64,
    /// 上一次看到的 seq（None = 还没见过；用 Option 而不是哨兵值，
    /// 因为 `Stats` 是 `derive(Default)`，哨兵会被 0 顶掉）
    last_seq: Option<u8>,
}

/// 小直方图 +1（最多记 8 类）—— 用于"长度分布"这类要比对形状的计数
fn bump_kind(v: &mut Vec<(u16, u64)>, k: u16) {
    if let Some(e) = v.iter_mut().find(|e| e.0 == k) {
        e.1 += 1;
    } else if v.len() < 8 {
        v.push((k, 1));
    }
}

/// 记一条"谁都不认识"的 ACL 分片。
///
/// ★ 以前这里是"什么都不做" —— 于是"音频为什么没声音"最直接的一类证据被**静默吞掉**。
/// 现在既记 `(flag, dlen)` 分布，也留**前 8 字节十六进制**：只看长度定不了性，
/// 看内容才知道它是什么（比如 L2CAP 长度/CID/报告头分别是多少）。
fn note_odd(s: &mut Stats, flag: u8, dlen: usize, acl: &[u8]) {
    s.odd_frag += 1;
    if let Some(e) = s.odd_kinds.iter_mut().find(|e| e.0 == flag && e.1 == dlen as u16) {
        e.2 += 1;
    } else if s.odd_kinds.len() < 8 {
        s.odd_kinds.push((flag, dlen as u16, 1));
    }
    if s.odd_sample.is_empty() {
        let n = acl.len().min(8);
        s.odd_sample = acl[..n].iter().map(|b| format!("{b:02X}")).collect::<Vec<_>>().join(" ");
    }
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
    ///
    /// ⚠ **不要在 ETW 回调里直接调它** —— 这里面是**文件写 + flush**，
    /// 一次慢盘就能把回调卡住几十~几百毫秒，而回调一卡 ETW 缓冲就溢出、**整段音频被丢**。
    /// 回调路径请用 [`log_async`]（只入内存队列，由统计线程落盘）。
    /// 静默开关（`--quiet` 置位）。发布版默认静默：**不写日志文件**。
    /// 排查时去掉 `--quiet` 即可恢复全部日志。
    pub static QUIET: std::sync::atomic::AtomicBool = std::sync::atomic::AtomicBool::new(false);

    pub fn log(msg: &str) {
        if QUIET.load(std::sync::atomic::Ordering::Relaxed) { return; }
        if let Ok(mut g) = log_file().lock() {
            if let Some(f) = g.as_mut() {
                let _ = writeln!(f, "{}  {}", local_hms(), msg);
                let _ = f.flush();
            }
        }
    }

    // ── 异步日志队列：把文件 I/O 彻底移出 ETW 回调路径 ──
    //
    // 这条改动抄 macOS 版的教训：他们把收包回调从 `DispatchQueue.main`
    // （还跑着 UI + 0.2s tick + 2s 落盘）挪到独立队列后，到达延迟峰值 71→56 ms。
    // 我们这边的等价物就是"回调里做文件写" —— 回调只入队（一次短锁 + 一次 push），
    // 统计线程每 2 秒 drain 一次落盘。
    static QUEUE: OnceLock<Mutex<std::collections::VecDeque<String>>> = OnceLock::new();

    /// 队列上限：统计线程若卡死，宁可丢日志也不要无限吃内存。
    const QUEUE_MAX: usize = 512;

    fn queue() -> &'static Mutex<std::collections::VecDeque<String>> {
        QUEUE.get_or_init(|| Mutex::new(std::collections::VecDeque::new()))
    }

    /// **回调路径专用**：只入队，不做任何 I/O。由统计线程 drain 后写文件。
    pub fn log_async(msg: &str) {
        if let Ok(mut q) = queue().lock() {
            if q.len() >= QUEUE_MAX {
                q.pop_front();
            }
            q.push_back(format!("{}  {}", local_hms(), msg));
        }
    }

    /// 统计线程调用：把队列里的行一次性落盘。
    /// 返回落盘的行数（顺便当"回调到底有没有被卡住"的旁证）。
    pub fn drain() -> usize {
        let mut lines: Vec<String> = Vec::new();
        if let Ok(mut q) = queue().lock() {
            while let Some(l) = q.pop_front() {
                lines.push(l);
            }
        }
        if lines.is_empty() {
            return 0;
        }
        let n = lines.len();
        if let Ok(mut g) = log_file().lock() {
            if let Some(f) = g.as_mut() {
                for l in &lines {
                    let _ = writeln!(f, "{l}");
                }
                let _ = f.flush();
            }
        }
        n
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

    #[cfg(not(target_os = "windows"))]
    fn hms(ms: u64) -> String {
        let s = (ms / 1000) % 86400;
        format!("{:02}:{:02}:{:02}.{:03}", s / 3600, (s / 60) % 60, s % 60, ms % 1000)
    }

    /// 本地时间 `HH:MM:SS.mmm`。
    ///
    /// ⚠ 原来用 `hms(now_ms())`，那是**UTC** —— 于是同一个 bridge.log 里
    /// 应用写的行是本地时间、桥写的行是 UTC，差 8 小时，读日志时极易误判。
    #[cfg(target_os = "windows")]
    pub fn local_hms() -> String {
        #[repr(C)]
        struct SystemTime {
            year: u16, month: u16, dow: u16, day: u16,
            hour: u16, minute: u16, second: u16, millis: u16,
        }
        #[link(name = "kernel32")]
        extern "system" {
            fn GetLocalTime(out: *mut SystemTime);
        }
        let mut st = SystemTime {
            year: 0, month: 0, dow: 0, day: 0, hour: 0, minute: 0, second: 0, millis: 0,
        };
        unsafe { GetLocalTime(&mut st) };
        format!("{:02}:{:02}:{:02}.{:03}", st.hour, st.minute, st.second, st.millis)
    }
    #[cfg(not(target_os = "windows"))]
    pub fn local_hms() -> String {
        hms(now_ms())
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
    /// 发送缓冲区满（接收端一时没跟上）导致**丢弃**的包数
    ///
    /// ★ 为什么套接字要设成非阻塞：阻塞式 `send_to` 在接收端不读的时候会**堵住调用线程**，
    /// 而这个线程就是 **ETW 回调线程** —— 一堵，ETW 缓冲就溢出，丢的不是一帧而是**一整段**
    /// （2026-10-16 那次 15 秒 / 511 帧的事故，量级就是这个）。
    /// 改成非阻塞后：接收端跟不上 → 丢这一个包并计数，回调**绝不阻塞**。
    blocked: u64,
}

impl UdpSink {
    fn new(addr: &str) -> Result<Self, String> {
        let sock = UdpSocket::bind("127.0.0.1:0").map_err(|e| e.to_string())?;
        // 非阻塞：宁可丢包也不让 ETW 回调卡住（见 blocked 的注释）
        sock.set_nonblocking(true).map_err(|e| e.to_string())?;
        Ok(Self {
            sock,
            peer: addr.to_string(),
            sent: 0,
            pcm_bytes: 0,
            blocked: 0,
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
            Err(e) if e.kind() == std::io::ErrorKind::WouldBlock => {
                self.blocked += 1;
                false
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
    /// ★ **当前按住的 usage 集合**（整份报告快照）。
    ///
    /// 以前这里是 `last_usage: Option<u8>`（只记一个键），而 HID 键盘报告的 6 个槽
    /// **可以同时有多个 usage** —— 实测遥控器上 `back + menu` 同时按就是这个样子：
    ///   `a1 01 00 00 09 11 00 00 00 00`
    /// 只取第一个非零 usage 会**把第二个键整条丢掉**（2026-10-17 实测发现）。
    ///
    /// 现在与注入器的模型对齐：把 6 个槽解析成"当前集合"，**与上一份做差集**，
    /// 只对"新按下 / 刚松开"发事件。这样多键同按、先后松开都不会丢。
    held: [bool; 256],
    /// 本段会话是否已经用第一条报文"记过基线"（见下方 handle_hid_report 的说明）
    seeded: bool,
    /// 基线里"已经按着"的键（只用于记账，**它的 UP 不发**，否则会出现"无对应的松开"）
    seeded_held: [bool; 256],
    /// 桥的启动时刻（用于记录"第一个按键距启动多久"）
    started: std::time::Instant,
    /// 是否已经记录过"启动后第一个带键的报文"
    first_key_done: bool,
    /// 上一条按键报文到达的时刻（用来判"新一段"：空档 ≥600 ms 就重新取基线）
    last_report: Option<std::time::Instant>,
    /// 最近一个事件的 ETW 时间戳（100ns 自 1601，即 FILETIME）—— 用来实测投递延迟
    last_ts: u64,
    stats: Arc<Mutex<Stats>>,
    wav: Option<hound::WavWriter<std::io::BufWriter<std::fs::File>>>,
    in_session: bool,
    last_frame_at: Option<Instant>,
    prev: i16, // 48k 内插用的上一个样本
    have_prev: bool,
    samples16: u64,
    /// 丢掉最前面几帧（高通瞬态 + 首个不完整段）。
    ///
    /// ★ 2026-10-17 从 2 帧降到 1 帧：每帧 20 ms，少丢一帧 = **首字早 20 ms**。
    ///   70 Hz 二阶高通的瞬态在 20 ms 内基本衰减完；多丢的那一帧纯粹是在拖慢首字。
    warmup: u32,
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
            held: [false; 256],
            last_ts: 0,
            stats,
            wav,
            in_session: false,
            last_frame_at: None,
            prev: 0,
            have_prev: false,
            samples16: 0,
            warmup: 0,
            seeded: false,
            seeded_held: [false; 256],
            started: std::time::Instant::now(),
            first_key_done: false,
            last_report: None,
            pending: None,
        }
    }

    /// # ★★ 遥控器按键报文模型（2026-10-19 全键抓包确认，156 条报文 / 52 次按键 / 19 个键）
    ///
    /// **所有按键的形态完全一致**：
    ///
    /// ```text
    /// 短按：  [键] → +41ms → [] → +10ms → [0x0C] → []
    /// 长按：  [键] → ★按住期间零报文★ → [] → [0x0C] → []
    /// ```
    ///
    /// 由此确定的三条事实（此前长期靠猜）：
    /// 1. **长按期间遥控器一条报文都不发** ⇒ 不存在"按住被别的报文挤掉"；
    /// 2. **`0x0C` 是厂商附加事件**：每次抬起后 10ms 必发一次，39/39 全部单独出现，
    ///    从不与其他键同现 ⇒ **不是按键**（当作幽灵报文丢弃）；
    /// 3. **`0x05` 是真键（CH-）**，不是幽灵（全轮仅 2 次，对应 CH- 的短按+长按）。
    ///
    /// ## 由此确定的状态机（本文件的差分逻辑）
    ///
    /// - **按下**：`now[u] && !held[u]` ⇒ 发 DOWN（与来源有关；启动基线除外）；
    /// - **抬起**：`held[u] && !now[u]` ⇒ 发 UP；
    ///   · **语音键（0x10）例外**：只有 **空报文** 才算松手（非空报文里没有它不算）——
    ///     因为曾经出现"按住期间来了别的键的报文 ⇒ 语音键被误判松开 ⇒ 右 Alt 自己弹起"；
    ///   · 幽灵报文（只含 0x0C/0x01）**整条丢弃**，不参与差分。
    ///
    /// ## 已知的固件抖动（在**应用侧**去抖，不在本文件）
    ///
    /// 真机另见：按住开始的瞬间，固件有时会先发一个 **DOWN → UP(3~34ms) → DOWN(41~163ms 后)**
    /// 的抖动对。桥如实上报（这是事实），由 `KeyFilter` 的语音键去抖把这一对合并，
    /// 否则输入法会看到 3ms 的假松开、以为"说完了"。
    ///
    /// 处理一个 ACL 分片（`acl` = 去掉 4 字节 ACL 头之后的数据）—— **实时与回放共用同一条路**
    ///
    /// ## ★ 2026-10-16：不要写死分片长度
    ///
    /// 老实现写死了"首片必须 `flag==2 && dlen==344`、续片必须 `flag==1 && dlen==313`"。
    /// 实测（13:22 那次"卡了 1 分钟、语音没电平"）同一个 657 字节的 L2CAP PDU
    /// **被拆成了三片：344 + 290 + 23** —— 续片长度不是 313，于是一片都匹配不上，
    /// `pending` 永远凑不满，**整整一分钟我们主动把完好的音频全扔了**。
    /// 而同一时间 `seqJumps = 0`（帧序号连续），说明**上游一帧都没丢**。
    ///
    /// 现在改成**按 PB 标志 + L2CAP 声明长度组帧**：起始分片（PB=10）开一个新 PDU，
    /// 后续分片（PB=01）无脑往上拼，拼到 `4 + L2CAP长度` 就交出去。
    /// 分几片、每片多长，都不再影响正确性 —— 而且长度不认识的会**留痕**，不再静默。
    fn handle_fragment(&mut self, flag: u8, dlen: usize, acl: &[u8]) {
        // `acl.len() < dlen` ⇒ 这条分片的负载被 ETW 截断（MaxEtwBytes 没生效时的老 bug）
        let truncated = acl.len() < dlen;

        match flag {
            // ── 起始分片（PB=10）──
            2 => {
                // 用 L2CAP 头（acl 的前 4 字节）判断这是哪一路数据，比猜长度可靠
                let l2cap_len = if acl.len() >= 2 {
                    u16::from_le_bytes([acl[0], acl[1]]) as usize
                } else { 0 };
                let cid = if acl.len() >= 4 {
                    u16::from_le_bytes([acl[2], acl[3]])
                } else { 0 };

                // ① 按键：单分片 HID 报告（L2CAP 负载 10 字节，报告头 a1 01）
                //    ★ 判据是**长度 + 报告头**，CID 只要求落在动态范围（0x40~0x7F）——
                //      因为 Control/Interrupt 谁先连谁拿 0x40，写死会整路失效。
                if is_data_cid(cid) && l2cap_len == HID_PAYLOAD_LEN {
                    if !truncated && acl.len() >= 6 && acl[4] == 0xa1 && acl[5] == 0x01 {
                        {
                            let mut st = self.stats.lock().unwrap();
                            st.hid_reports += 1;
                            st.key_cid.get_or_insert(cid);
                        }
                        let end = acl.len().min(14);
                        let usages: Vec<u8> = acl[8..end].to_vec();
                        self.handle_hid_report(&usages);
                    }
                    return;
                }

                // ② 音频起始分片（同样只看长度，不认死 CID）
                if is_data_cid(cid) && l2cap_len == FRAME_LEN {
                    self.stats.lock().unwrap().audio_cid.get_or_insert(cid);
                    if truncated {
                        // 首片只带回了 L2CAP 头 → 这一帧不可能补齐
                        self.pending = None;
                        self.stats.lock().unwrap().truncated_first += 1;
                        return;
                    }
                    if self.pending.is_some() {
                        // 上一个 PDU 还没凑齐，新的起始分片就来了 ⇒ 那个 PDU 确实没收全
                        self.stats.lock().unwrap().lost_cont += 1;
                    }
                    self.pending = Some(acl.to_vec());
                    self.try_complete();
                    return;
                }

                // L2CAP 信令通道：建链/协商用的小包，不是音频也不是按键。
                // ★ 单独计数而不是丢进"未识别" —— 那会让界面显示"长度不认识 N"，
                //   看起来像帧格式出了问题，实际完全无害。
                if cid == CID_SIG {
                    self.stats.lock().unwrap().sig_frag += 1;
                    return;
                }

                let mut s = self.stats.lock().unwrap();
                note_odd(&mut s, flag, dlen, acl);
            }

            // ── 后续分片（PB=01）：长度随便多少都收 ──
            1 => {
                if self.pending.is_none() {
                    // 起始分片被丢了，续片成了孤儿
                    self.stats.lock().unwrap().orphan_cont += 1;
                    return;
                }
                {
                    let (mut s, p) = (self.stats.lock().unwrap(), self.pending.as_mut().unwrap());
                    if truncated { s.trunc_cont += 1; }
                    bump_kind(&mut s.cont_kinds, acl.len() as u16);
                    p.extend_from_slice(acl);
                }
                self.try_complete();
            }

            _ => {
                let mut s = self.stats.lock().unwrap();
                note_odd(&mut s, flag, dlen, acl);
            }
        }
    }

    /// 攒够一整帧就交出去（`pending` 里是 L2CAP 头 + 负载）
    fn try_complete(&mut self) {
        if self.pending.as_ref().map(|p| p.len()).unwrap_or(0) < AUDIO_TOTAL {
            return;
        }
        let p = self.pending.take().unwrap();
        if p.len() != AUDIO_TOTAL {
            // 拼是拼够了，但长度不是我们认识的那一种（比如换了音频配置）——
            // ★ 必须留痕：这正是"看着有声、其实解不出来"的那类情况。
            self.stats.lock().unwrap().short_pdu += 1;
            return;
        }
        self.push_payload(&p[L2CAP_HDR..]);
        self.stats.lock().unwrap().frames += 1;
    }

    /// HID 键盘报告 → 按下 / 松开事件
    ///
    /// **整份报告快照模型**：6 个槽里所有非零 usage 构成"当前按住集合"，
    /// 与上一份逐项比较，只对变化发事件。
    ///
    /// 为什么必须这样（2026-10-17 实测）：
    ///   · 遥控器同时按 `back + menu` → 报告里**两个 usage 并存**（`09 11`）；
    ///   · 松开其中一个 → 报告变成 `11`，此时才算 menu "新按下"（如果之前只用单槽模型）；
    ///   · 幽灵帧 0x0C 还会**与正在按住的键并存**（`11 0c`）。
    /// 单槽（`Option<u8>`）模型在上述三种情况下都会丢键或错时报文。
    fn handle_hid_report(&mut self, usages: &[u8]) {
        // ★★ 2026-10-18：把**原始报文**打出来（排查"按了语音键却零事件"）。
        //   之前只能看到解析后的 KEY 行，无法判断"遥控器到底发了什么"。
        //   这条只在日志开启（非 --quiet）时输出，发布版默认静默。
        if !diag::QUIET.load(std::sync::atomic::Ordering::Relaxed) {
            let hex: Vec<String> = usages.iter().map(|u| format!("{:02X}", u)).collect();
            diag::log(&format!("[RAW] len={} usages=[{}]", usages.len(), hex.join(" ")));
        }

        // ★★★ 2026-10-18（真机铁证）：**取消"空档 ≥600ms 就重取基线"**。
        //
        //   原始报文（bridge.log [RAW]）：
        //     20.842 [10] 按下 → 无事件
        //     22.332 [00] 松开 → 无事件
        //     23.208 [10] 按下 → 无事件   ← 距上条 876ms
        //     26.228 [10] 按下 → 无事件   ← 距上条 1.3s
        //     30.430 [10] 按下 → ★有 DOWN ← 距上条仅 525ms
        //
        //   遥控器**只在状态变化时**发报文，所以两次按键间隔通常 1~3 秒。
        //   而"新一段里出现的键只当基线、不发 DOWN"这条防误触规则，
        //   在正常使用中会**吞掉几乎每一次按下** —— 只有间隔 <600ms 的才侥幸生效。
        //
        //   现在：基线**只在进程启动后的第一份报文取一次**（防"启动瞬间设备已抱着键"），
        //   之后所有报文一律正常对账。蓝牙重连时若设备补发"当前状态"，按 DOWN 处理是正确的
        //   （那个键确实被按着），其后的 UP 也会正常发出。
        let t = std::time::Instant::now();
        self.last_report = Some(t);

        let mut now = [false; 256];
        for &u in usages {
            if u != 0 {
                now[u as usize] = true;
            }
        }


        // ⓪ 本段的第一条报文：只当基线、**不发任何按键事件**。
        //
        //   ★ 2026-10-18 用户实测：双击 exe 启动程序 → 光标处的文件**弹出右键菜单**，
        //     即"菜单键(0x4E)被按下了一次"。原因就是 `held` 初值全 false，
        //     而程序刚起来时收到的第一条报文里**已经带着那个键**（设备正抱着键 / 重连补发），
        //     于是被当成"刚按下"。
        //   ⇒ 每条（段）会话的第一条报文只记账：早已按着的键不算"刚按下"，
        //     用户真正需要的按键事件从第二条报文开始，代价是 20 ms。
        if !self.seeded {
            // ★ 基线规则只在**启动后 1.5 秒内**生效。
            //   超过这个时间还出现"首份报文"，说明是用户真的按下了（不是启动时设备就已抱着键），
            //   必须正常对账、发出 DOWN —— 否则"启动后第一次按下被吞"这个 bug 会一直存在。
            if self.started.elapsed().as_millis() > 1500 {
                self.seeded = true;
                self.stats.lock().unwrap().seed_reports += 0;   // 不计入"基线吞掉"
                // 落到下面的正常差分逻辑（held 初始为空 ⇒ 会正常发 DOWN）
            } else {
            self.seeded = true;

            // ★★★ 2026-10-18（真机卡键，反复出现）：空档期间**被松开的键必须补发 UP**。
            //
            //   为什么会长按必卡：语音键按住时遥控器**在按住期间不上报**
            //   （实测按住 30 秒内上报 0 次），松开时才发一条。这条报文与上一条的间隔
            //   远超 600 ms ⇒ 一定会走到这里。而这里原来直接 `self.held = now; return;`，
            //   于是那条"松开"被当成新基线**吞掉** ⇒ 右 Alt 永远等不到 UP ⇒ 永久卡键。
            //
            //   正确规则：
            //     · 消失了（held 有、now 没有）→ **发 UP**（本段基线里"早已按着"的除外，
            //       那种键我们从没发过 DOWN）；
            //     · 新出现（now 有、held 没有）→ 只记账、**不发 DOWN**（防启动/重连时误触发）。
            for u in 0..256usize {
                if self.held[u] && !now[u] {
                    if !self.seeded_held[u] {
                        self.emit_key(Some(u as u8), false);
                    }
                    self.seeded_held[u] = false;
                }
            }
            for u in 0..256usize {
                if now[u] && !self.held[u] {
                    self.seeded_held[u] = true;   // 新出现的键按"基线"处理：不发 DOWN
                }
            }
            self.held = now;
            self.stats.lock().unwrap().seed_reports += 1;
            return;
            }
        }
        // ★★★ 2026-10-18（真机日志铁证）：**只含"幽灵 usage"的报文不是键盘快照**。
        //
        //   遥控器按住语音键时会插一条只带 0x0C 的报文；按"整份快照"处理会误判松开，
        //   把长按截断在 142~213 ms（豆包门槛 300~500ms）。
        //
        //   ⚠ 位置很关键：这段**必须排在"取基线"之后**。
        //     之前放在前面 ⇒ 空闲时的 0x0C 心跳一直走这条 return ⇒ `seeded` 永远是 false
        //     ⇒ 第一份**真实**报文被当成"段首基线"吃掉 ⇒ `keyEvents` 恒为 0
        //     （真机症状：音频正常、按键面板"按下 0 / 松开 0"、豆包完全不响应）。
        const GHOST: [u8; 2] = [0x01, 0x0C];
        let has_real = usages.iter().any(|&u| u != 0 && !GHOST.contains(&u));
        let all_empty = usages.iter().all(|&u| u == 0);
        if !has_real && !all_empty {
            self.stats.lock().unwrap().ghost_reports += 1;
            return;
        }

        // ⓪-2 记录"启动后第一个带键的报文"（**纯诊断，不参与逻辑**）。
        //   真机出现过"启动瞬间弹出右键菜单"，最后查明是**上位机自己**发的全零报告
        //   （见 v1.2.33），与这里无关；但留下这两个数，下次再有类似现象能直接定性。
        if !self.first_key_done {
            if let Some(u) = now.iter().position(|&b| b) {
                self.first_key_done = true;
                let mut st = self.stats.lock().unwrap();
                st.first_key_seen = true;
                st.first_key_ms = self.started.elapsed().as_millis() as u64;
                st.first_key_usage = u as u8;
            }
        }

        // ① 先放掉不再按住的（升序，保证事件顺序确定）
        for u in 0..256usize {
            if self.held[u] && !now[u] {
                // ★★★ 2026-10-19（参考同类项目后的结构性修正）：
                //   **语音键只认"空报文"为松手**。
                //
                //   为什么：遥控器按住语音键期间会插入别的报文（0x05 / 0x0C …）。
                //   我们按"整份快照"对账 ⇒ 那条报文里没有 0x10 ⇒ 判定"松开" ⇒
                //   注入的右 Alt 被放掉 ⇒ 用户看到"按住没说话，Alt 自己弹起来了"。
                //   这类误释放已经反复出现，而真正的松手**一定是空报文 `[]`**（真机多次验证）。
                //   代价：若"松开语音键的同时按下别的键"，这一条会漏（极少见），
                //   由 StuckGuard 兜底。**宁可晚放，也不能乱放。**
                const VOICE_USAGE: usize = 0x10;
                if u == VOICE_USAGE && !all_empty {
                    continue;   // 非空报文里"没有语音键"不算松手
                }
                self.held[u] = false;
                if self.seeded_held[u] {
                    // 这个键是"基线里就已经按着"的，我们从未发过它的 DOWN ⇒ UP 也不发
                    self.seeded_held[u] = false;
                    continue;
                }
                self.emit_key(Some(u as u8), false);
            }
        }
        // ② 再按下新出现的
        for u in 0..256usize {
            if now[u] && !self.held[u] {
                self.held[u] = true;
                self.emit_key(Some(u as u8), true);
            }
        }
    }

    /// 会话结束 / 程序退出：把还按着的键全部放掉（否则上位机会以为键一直按着）
    fn release_all_held(&mut self) {
        for u in 0..256usize {
            if self.held[u] {
                self.held[u] = false;
                // 基线里"早已按着"的键，我们从没发过它的 DOWN ⇒ 这里也不发 UP，
                // 否则面板会出现"松开比按下多"（用户实测 17 下 / 28 松）。
                if self.seeded_held[u] {
                    self.seeded_held[u] = false;
                    continue;
                }
                self.emit_key(Some(u as u8), false);
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
        if !diag::QUIET.load(std::sync::atomic::Ordering::Relaxed) {
            println!("[KEY] usage=0x{u:02X}  {}", if down { "DOWN" } else { "UP" });
        }
    }

    /// 一个完整音频帧的 653 字节负载
    fn push_payload(&mut self, p: &[u8]) {
        if p.len() != FRAME_LEN {
            return;
        }

        // ★ 本帧是不是"新会话的第一帧"。
        //
        //   为什么要在**这里**判：发 CLEAR 在本函数后半段，而序号比较在最前面。
        //   以前把"清序号基准"写在发 CLEAR 那一段 ⇒ 每个会话的**第一帧**仍会拿
        //   上一段会话的 seq 去比，白记一次"跳号"。
        //   实测（v1.2.4，100 分钟）：12 次会话 → 12~13 次跳号、累计"跳过 1500 帧"，
        //   于是界面上的「丢失帧数」**误报**成上千帧 —— 而同期 `组帧失败` 一直是 0，
        //   音频一帧没丢。假仪表比没仪表更糟。
        //
        //   ⚠ **判据必须是"距上一帧的空档"，不能用 `self.in_session`**：
        //   `in_session` 只在写了 UDP 接收端时才被维护（在发 CLEAR 那一段里），
        //   回放（不带 `--udp`）时它恒为 false ⇒ 每一帧都被当成新会话 ⇒ 序号比较全被跳过，
        //   连"真跳号"都查不出来（这条被自检 ② 抓了个正着）。
        //   用空档判则与 CLEAR/END 同一套语义，且与 UDP 无关。
        let session_start = self.last_frame_at
            .map(|t| t.elapsed() >= Duration::from_millis(IDLE_END_MS))
            .unwrap_or(true);

        // ── 帧头自检 + seq 跟踪 ──
        //
        // 帧头 = `A1 F7 01 <seq> 00 <roll> <n> <cnt> 00 00 00 40 01`，
        // 其中 `<seq>` 逐帧 +1（1 字节，到 255 回绕）。**这是唯一能证明"真丢了多少帧"的判据**：
        //   · seq 连续        → 这段时间一帧没丢（哪怕桥因为别的原因没出声）
        //   · seq 跳号 n       → 真丢了 n-1 帧（遥控器发了、我们没收到）
        //   · 帧头签名不符     → 帧格式与预期不同（那就不是"丢"，是"变了"）
        // 有了它 + `lost_cont`（组装失败）+ `odd_frag`（被拒绝的分片），
        // 三者一对就能判定故障在哪一层，不用再靠猜。
        {
            let sig_ok = p[0] == HDR_SIG[0]
                && p[1] == HDR_SIG[1]
                && p[2] == HDR_SIG[2]
                && p[11] == HDR_TAIL[0]
                && p[12] == HDR_TAIL[1];
            let seq = p[HDR_SEQ_IDX];
            let mut s = self.stats.lock().unwrap();
            if !sig_ok {
                s.hdr_odd += 1;
            } else if !session_start {
                if let Some(prev) = s.last_seq {
                match (seq.wrapping_sub(prev)) as u64 {
                    1 => {}                       // 正常
                    0 => s.seq_dup += 1,          // 重复帧
                    n => {
                        // ★ 2026-10-19：跳号**不等于**丢帧。
                        //   遥控器静音时**根本不发帧**，恢复后序号自然跳过 N 帧。
                        //   判别：跳过 N 帧理论上需要 N×20ms 真实时间；
                        //     · 实际空档 ≥ 该值（留 150ms 余量）⇒ 静音期未发送，**不是丢**；
                        //     · 实际空档明显更小 ⇒ 这段时间里本该有的帧真的没了 ⇒ 才算丢。
                        let gap_ms = self.last_frame_at
                            .map(|t| t.elapsed().as_millis() as u64)
                            .unwrap_or(0);
                        let expected_ms = (n as u64) * 20;
                        s.seq_jumps += 1;
                        if gap_ms + 150 >= expected_ms {
                            s.seq_silence += n - 1;
                        } else {
                            s.seq_lost += n - 1;
                        }
                    }
                    }
                }
            }
            s.last_seq = Some(seq);
        }
        // ── 帧间空档统计：长录音里"说着说着没声"就是靠这个定位 ──
        //
        // ⚠ 这里**不能在锁内写文件**：`diag::log` 是文件写 + flush，慢盘一次几十~几百毫秒，
        //   而这条路径就是 ETW 回调本身 —— 一卡 ETW 缓冲就溢出。现在锁内只改计数，
        //   文案走 `log_async`（只入内存队列，由统计线程落盘）。
        if let Some(t) = self.last_frame_at {
            let gap = t.elapsed().as_millis() as u64;
            let mut is_gap = false;
            {
                let mut s = self.stats.lock().unwrap();
                if gap > s.max_gap_ms { s.max_gap_ms = gap; }
                if gap >= IDLE_END_MS {
                    s.gaps += 1;
                    is_gap = true;
                }
            }
            if is_gap {
                diag::log_async(&format!(
                    "音频空档 {gap} ms（>= IDLE_END_MS {IDLE_END_MS}），会触发 END→CLEAR"));
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
        if self.warmup < 1 {
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
                {
                    let mut s = self.stats.lock().unwrap();
                    s.sessions += 1;
                    // ★ 序号基准已在**本函数开头**按 `session_start` 跳过比较，
                    //   这里不必再清（清了反而会少比一帧）。见那段的注释。
                }
                // 回调路径 → 异步日志（见 diag::log 的警告）
                diag::log_async("会话开始（CLEAR）");
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

    /// 空闲检测：超过 IDLE_END_MS 没有帧就发 END，让 router 关掉 CABLE 流。
    ///
    /// ⚠ 它是**持有 bridge 互斥体**被调的（`bridge.lock().unwrap().tick()`），
    /// 而 ETW 回调要抢同一个锁 —— 所以这里**绝不能做文件 I/O**：一次慢盘就等于
    /// 把回调卡住，ETW 缓冲跟着溢出。文案一律走 `log_async`。
    fn tick(&mut self) {
        if !self.in_session {
            return;
        }
        if let Some(t) = self.last_frame_at {
            if t.elapsed() >= Duration::from_millis(IDLE_END_MS) {
                if let Some(u) = self.udp.as_ref() {
                    u.cmd(b"END");
                }
                // 会话断开时可能还有键按着（链路掉/遥控器走远）—— 必须放掉，
                // 否则上位机以为键一直按着（卡键）。
                self.release_all_held();
                self.in_session = false;
                diag::log_async(&format!(
                    "会话结束（END）—— 已静默 {} ms",
                    t.elapsed().as_millis()
                ));
            }
        }
    }

    fn finish(&mut self) {
        self.release_all_held();     // 退出前把按着的键放掉（防卡键）
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
    /// 静默：不写 bridge.log、也不打 [KEY] 行（发布版默认走这个）。
    /// ★ 按键 stdout 让 C# 读到就丢是纯浪费 —— 一次管道写系统调用 ≈2~5 µs，
    ///   而且它就压在"ETW 收到按键 → 发 UDP"这一步上。
    quiet: bool,
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
        quiet: false,
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
            "--quiet" => out.quiet = true,
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
        std::thread::spawn(move || {
            let mut n = 0u64;
            // ★ 2 秒粒度的"丢失增量"快照。原来只有一个 30 秒粒度的累计数 ——
            //   "哪一秒开始丢、丢的是哪一类"完全看不见（2026-10-16 排查时就吃了这个亏：
            //   511 这个数字查到了，但判断不出是 ETW 丢事件还是分片长度变了）。
            let mut prev_loss = (0u64, 0u64, 0u64, 0u64, 0u64, 0u64,
                                 (0u64, 0u64, 0u64, 0u64, 0u64));
            let mut prev_blocked = 0u64;
            loop {
            std::thread::sleep(Duration::from_millis(2000));
            n += 1;
            let (json, stat_line, blocked) = {
                let Ok(s) = st.lock() else { continue };
                let Ok(g) = br.lock() else { continue };
            let (sent, bytes) = g
                .udp
                .as_ref()
                .map(|u| (u.sent, u.pcm_bytes))
                .unwrap_or((0, 0));
            let blocked = g.udp.as_ref().map(|u| u.blocked).unwrap_or(0);
                let now = diag::now_ms();
                let silent: i64 = if s.last_frame_ms == 0 {
                    -1 // 从未收到过音频帧
                } else {
                    (now.saturating_sub(s.last_frame_ms)) as i64
                };
                let json = format!(
                    concat!(
                        "{{\"pid\":{},\"uptimeSec\":{},\"heartbeatMs\":{},",
                        "\"frames\":{},\"samples16\":{},\"seconds16\":{:.2},",
                        "\"udpPackets\":{},\"udpBytes\":{},\"udpBlocked\":{},",
                        "\"sessions\":{},\"gaps\":{},\"maxGapMs\":{},",
                        "\"lostCont\":{},\"truncatedFirst\":{},\"udpErr\":{},",
                        "\"seqJumps\":{},\"seqLost\":{},\"seqSilence\":{},\"seqDup\":{},",
                        "\"truncCont\":{},\"oddFrag\":{},\"hdrOdd\":{},",
                        "\"orphanCont\":{},\"shortPdu\":{},\"sigFrag\":{},",
                        "\"hidReports\":{},\"keyEvents\":{},\"otherEvents\":{},",
                        // 启动诊断：第一个带键的报文（-1 = 至今没收到过任何带键报文）
                        "\"firstKeyMs\":{},\"firstKeyUsage\":\"0x{:02X}\",",
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
                    blocked,
                    s.sessions,
                    s.gaps,
                    s.max_gap_ms,
                    s.lost_cont,
                    s.truncated_first,
                    s.udp_err,
                    s.seq_jumps,
                    s.seq_lost,
                    s.seq_silence,
                    s.seq_dup,
                    s.trunc_cont,
                    s.odd_frag,
                    s.hdr_odd,
                    s.orphan_cont,
                    s.short_pdu,
                    s.sig_frag,
                    s.hid_reports,
                    s.key_events,
                    s.other,
                    if s.first_key_seen { s.first_key_ms as i64 } else { -1i64 },
                    s.first_key_usage,
                    silent,
                    if g.in_session { 1 } else { 0 }
                );
                let stat_line = format!(
                    "[STAT] 运行 {}s | 会话 {} 空档 {} 最长 {}ms | 帧 {} 丢帧 {}（seq跳 {} 帧，其中静音 {} 帧）被截首片 {} \
                     · 截续片 {} · 未识别片 {} · 帧头异常 {} · 孤续片 {} · 怪长帧 {} \
                     | UDP {} 包 失败 {} 缓冲满丢 {} | 距上次音频 {} | 会话中={}",
                    t0.elapsed().as_secs(),
                    s.sessions, s.gaps, s.max_gap_ms,
                    s.frames, s.lost_cont, s.seq_jumps, s.seq_silence, s.truncated_first,
                    s.trunc_cont, s.odd_frag, s.hdr_odd, s.orphan_cont, s.short_pdu,
                    sent, s.udp_err, blocked,
                    if silent < 0 { "从未".to_string() } else { format!("{silent}ms") },
                    if g.in_session { "是" } else { "否" }
                );
                (json, stat_line, blocked)
            };
            diag::write_stats(&json);
            // ★ 把回调**异步**写下的日志行落盘（文件 I/O 只在统计线程做，见 diag::log 的警告）
            diag::drain();
            // ★ 丢失增量日志（2 秒一行，**只在有变化时**打）：
            //   把"哪一类在涨"直接写进日志，下一次复现不用再翻累计数。
            {
                let cur = match st.lock() {
                    Ok(s) => (
                        s.lost_cont, s.seq_jumps, s.seq_lost,
                        s.trunc_cont, s.odd_frag, s.hdr_odd,
                        (s.odd_kinds.clone(), s.odd_sample.clone(),
                         s.cont_kinds.clone(), s.orphan_cont, s.short_pdu),
                    ),
                    Err(_) => continue,
                };
                let d = (
                    cur.0.wrapping_sub(prev_loss.0),
                    cur.1.wrapping_sub(prev_loss.1),
                    cur.2.wrapping_sub(prev_loss.2),
                    cur.3.wrapping_sub(prev_loss.3),
                    cur.4.wrapping_sub(prev_loss.4),
                    cur.5.wrapping_sub(prev_loss.5),
                );
                // 「发送缓冲满丢包」也算一类丢失（接收端一时没跟上 → 那一包没了）
                let dblk = blocked.wrapping_sub(prev_blocked);
                let dorphan = cur.6.3.wrapping_sub(prev_loss.6.3);
                let dshort = cur.6.4.wrapping_sub(prev_loss.6.4);
                // 实测的 HID 数据 CID（动态分配，0x40 或 0x41 —— 见 is_data_cid 的注释）
                let (aud, kcid) = {
                    let s2 = st.lock().unwrap();
                    (s2.audio_cid, s2.key_cid)
                };
                let cidtxt = |c: Option<u16>| match c {
                    Some(v) => format!("0x{v:04X}"),
                    None => "未出现".to_string(),
                };
                if d != (0, 0, 0, 0, 0, 0) || dblk > 0 || dorphan > 0 || dshort > 0 {
                    let (odd, sample, conts, orphan, short_pdu) = &cur.6;
                    let mut kinds: Vec<String> = odd.iter()
                        .map(|(f, l, c)| format!("flag={f} dlen={l}×{c}"))
                        .collect();
                    kinds.sort();
                    // ★ 续片长度分布 —— 2026-10-16 那次"卡一分钟"就是靠它定性的：
                    //   续片从「一片 313」变成了「290 + 23」，而解析器写死了 313。
                    let mut cl: Vec<String> = conts.iter()
                        .map(|(l, c)| format!("{l}B×{c}")).collect();
                    cl.sort();
                    diag::log(&format!(
                        "★ 音频丢失明细: 组帧失败(续片没到) +{} · seq跳号 +{}（跳过 {} 帧）\
                         · 续片被截断 +{} · 未识别分片 +{} [{}] · 帧头异常 +{} · \
                         孤续片 +{} · 怪长帧 +{} · 续片长度分布 [{}]{} · UDP缓冲满丢 +{} · \
                         实际 CID 音频={} 按键={}",
                        d.0, d.1, d.2, d.3, d.4,
                        if kinds.is_empty() { "无".to_string() } else { kinds.join(", ") },
                        d.5,
                        orphan.wrapping_sub(prev_loss.6.3),
                        short_pdu.wrapping_sub(prev_loss.6.4),
                        if cl.is_empty() { "无".to_string() } else { cl.join(", ") },
                        if sample.is_empty() { String::new() } else { format!(" · 首条未识别 {sample}") },
                        dblk,
                        cidtxt(aud), cidtxt(kcid)
                    ));
                }
                prev_loss = (cur.0, cur.1, cur.2, cur.3, cur.4, cur.5,
                             (cur.6.0.len() as u64, 0, 0, cur.6.3, cur.6.4));
                prev_blocked = blocked;
            }
            // ★ 每 30 秒往 bridge.log 追加一行统计。
            //   bridge-stats.json 只保留"当前值"，看不出走势；
            //   而用户报的是「**多次对话后**才出现没波形」——正是需要看趋势的场景。
            //   有了这条时间线，"丢帧是不是越攒越多""空档是不是越来越长"一眼可判。
            if n % 15 == 0 {
                diag::log(&stat_line);
            }
        }
        });
    }

    if let Some(path) = args.replay.clone() {
        replay_mode(&path, Arc::clone(&bridge), args.realtime, Arc::clone(&stats));
        return;
    }

    if args.quiet {
        diag::QUIET.store(true, std::sync::atomic::Ordering::Relaxed);
    }
    if !args.quiet {
        println!("[i] 实时 ETW 模式（需要管理员权限）");
    }
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
        // ★ 缓冲数量 2/4 → 8/32。
        //
        // 原来最多只有 4 个 4KB 缓冲（共 16KB），而这条链路上的流量是：
        //   真实音频 ≈ 33 KB/s（50 帧/秒 × 657 字节）
        //   + 填缓冲 ≈ 200 KB/s（热档每 10ms 一个 2048 字节事件）
        // 消费端（ferrisetw 的处理线程）只要停顿几十毫秒，缓冲就满、ETW 直接丢事件。
        // 实测一台机器跑 20 分钟丢了 512 个音频续片（= 512 帧 × 20ms ≈ 10 秒音频），
        // 表现就是音频断续、久了就像没声音。
        //
        // 关键：**buffer_size 不动**。刷新节奏由"填满一个缓冲"和 flush_timer 决定，
        // 4KB 一页 + 2048 字节的事件 → 每 2 个事件刷一次，延迟不变。
        // 而把缓冲**个数**放大 8 倍，只是给消费端留出积压空间，延迟不受影响。
        min_buffer: 8,
        max_buffer: 32,
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
        // ★ 回放也要**按时间轴判会话**：真实流里"松手 → 再按"之间隔着几百毫秒到几秒，
        //   桥会发 END 并结束会话；回放若不补这一步，整段回放会被当成**一个**会话，
        //   于是"跨会话的假跳号"这类问题在回放里永远复现不出来。
        //   （自检 `scenario_two_sessions` 就靠这条。）
        if dt >= IDLE_END_MS as u32 {
            bridge.lock().unwrap().tick();
        }
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
    diag::drain();      // 回放很短，统计线程（2 s 一轮）可能来不及落盘 —— 这里补一次
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
    println!(
        "[i] 丢帧定位: 组帧失败(续片没到) {} · seq跳号 {} 次（跳过 {} 帧）· seq重复 {} \
         · 续片被截断 {} · 未识别分片 {} · 帧头异常 {} · 孤续片 {} · 怪长帧 {} · L2CAP信令 {}",
        st.lost_cont, st.seq_jumps, st.seq_lost, st.seq_dup,
        st.trunc_cont, st.odd_frag, st.hdr_odd, st.orphan_cont, st.short_pdu, st.sig_frag
    );
    if !st.cont_kinds.is_empty() {
        let mut k: Vec<String> = st.cont_kinds.iter().map(|(l, c)| format!("{l}B×{c}")).collect();
        k.sort();
        println!("[i] 续片长度分布: {}", k.join(", "));
    }
    if !st.odd_sample.is_empty() {
        println!("[i] 首条未识别分片前 8 字节: {}", st.odd_sample);
    }
    if !st.odd_kinds.is_empty() {
        let mut k: Vec<String> = st.odd_kinds.iter()
            .map(|(f, l, c)| format!("flag={f} dlen={l}×{c}")).collect();
        k.sort();
        println!("[i] 未识别分片分布: {}", k.join(", "));
    }
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
