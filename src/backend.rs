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

/// 业务实例（Tauri 命令通过 `shared()` 拿它）
static SHARED: std::sync::OnceLock<Arc<Mutex<Bridge>>> = std::sync::OnceLock::new();
/// 语音时序档位：0=低延迟 1=平衡 2=稳定（由界面写）
pub static PRESET: std::sync::atomic::AtomicU8 = std::sync::atomic::AtomicU8::new(1);

/// 供 Tauri 命令取业务句柄；业务线程还没起来时返回 None。
/// 最近一次遥控器按键的 usage。
///
/// 界面用它做"按键反应"回显 —— 客户最想确认的就是"我按了，机器到底收没收到"。
/// **只给 usage，不带时间**：显示"x 秒前"要么静止不动（没意义），
/// 要么就得每帧强制重绘（为一个装饰性数字持续唤醒 GPU，不值）。
pub fn last_key() -> Option<u8> {
    let sh = shared()?;
    let b = sh.lock().ok()?;
    let st = b.stats.lock().ok()?;
    if st.last_key_ms == 0 {
        None
    } else {
        Some(st.last_key_usage)
    }
}

pub fn shared() -> Option<&'static Arc<Mutex<Bridge>>> {
    SHARED.get()
}


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
/// 音频静默多久算"这段会话结束"（默认 600 ms）。
///
/// ★ 2026-10-30：从 const 改成可配置（`--idle-end-ms`），因为它是"两次说话之间隔多久
///   会被算成两段"的唯一判据，而这取决于用户的说话习惯：
///   · 调小（200~400）⇒ 快速连说变成两段，输入法能重新起录；代价是"说话中途想一下"被切断；
///   · 调大（1200+）⇒ 说完马上再说会被并成一段，输入法不会重新起录。
static IDLE_END_MS: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(600);
/// 尾音窗口（ms）：判定"说完了"之后再多等这么久才发 END。
///
/// 为什么要它：最后一段音频从遥控器到声卡有几十 ms 的链路延迟，
/// 一到阈值就发 END 会把最后一个字的尾巴切掉（豆包表现为"最后字没出来/少了半句"）。
/// 150 ms 是 C# 版实测的经验值；0 = 关闭（省 150 ms 但可能吞字）。
static TAIL_MS: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(150);

// ── 锁竞争观测（#3 的基线数据，本身不改变任何行为）──
/// 统计线程抢桥锁的最大等待（微秒）—— 越大说明热路径被挡得越厉害
pub static LOCK_WAIT_MAX_US: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);
/// 抢锁次数
pub static LOCK_WAIT_N: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);
/// 等待超过 5 ms 的次数（>5ms 就意味着一批 ETW 事件可能已积压）
pub static LOCK_WAIT_SLOW: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);

// ── 热路径（ETW 回调）自己的锁等待：决定"要不要无锁化"的那个数 ──
pub static HOT_LOCK_WAIT_MAX_US: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);
pub static HOT_LOCK_SLOW: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);
pub static HOT_LOCK_N: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);

#[inline]
fn idle_end_ms() -> u64 {
    IDLE_END_MS.load(Ordering::Relaxed)
}

// ───────────────── 语音租约（2026-10-31：结构性替换，非补丁）─────────────────
//
// **模型**：右 Alt 应该按住  ⟺  now_ms() < VOICE_LEASE_MS。
// 写入只有四处（arm / 每帧续租 / 松手收口 / 断链收口），读取只有 `reconcile_voice()`。
//
// **为什么用租约代替状态机**：语音键的"结束"没有可靠事件源 ——
//   · 按住期间遥控器**零报文**（真机抓包 156 条/52 次按键确认）；
//   · 固件 30 秒自动收工**不发**松开报文（2026-10-31 实测：20:59:19.5→20:59:29.8 无任何 [RAW]）；
//   · 断链则彻底没有事件。
// 所以"判断结束"的代码只能猜，每猜错一次就是一个 bug（判据①②③④ 都是这么长出来的）。
// 租约把"猜"换成"过期"：**没有续租就必然到期**，卡键在数学上不可能发生。
pub static VOICE_LEASE_MS: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);
/// 按下之后等用户开口的窗口：超过它还没收到一帧音频 ⇒ 这次按压无效，自动放开。
const T_NOVOICE_MS: u64 = 10_000;
/// 人说话的正常停顿容忍：每来一帧音频就续租这么多；断流超过它就结束。
/// ★ 这是整套语音逻辑里**唯一需要按体感调**的数字（调小 ⇒ 更容易切句，调大 ⇒ 说完后放手更慢）。
const T_PAUSE_MS: u64 = 3_000;
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
    /// 空档期间**链路层事件仍在到达**的次数。
    ///
    /// ★ 这是定位"语音中途断"的关键分水岭（2026-10-30 加）：
    ///   链路层事件（BIP_Type != 3）是**不依赖说话**持续流动的。
    ///   · 空档期间它们还在 ⇒ ETW 事件流正常，**是远端停发音频**（固件/蓝牙侧）
    ///   · 空档期间它们也没了 ⇒ **整个 BTHPORT 事件流都停了**（Windows/驱动侧）
    ///   两种情况的修法完全不同，必须靠这个数区分，不能靠猜。
    gaps_with_events: u64,
    /// 空档期间链路层事件也停了的次数（见 gaps_with_events 的说明）
    gaps_without_events: u64,
    /// **最近一次收到任何 ETW 事件**的时刻（Unix 毫秒）。
    /// ★ 这个数不动 = ETW 消费者线程停了（桥进程还活着、统计还在写，看门狗看不出来）——
    ///   实测踩到：语音连续说满约 30 秒后整条事件流冻结，而 ETW 会话本身却健康。
    last_event_ms: u64,
    /// 最近按下的遥控器 usage（界面左侧栏显示"已检测：xxx"用）
    last_key_usage: u8,
    /// 最近按键的时刻（ms，与 last_event_ms 同基准）
    last_key_ms: u64,
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
        // 用户关掉日志后，这里必须也停 —— bridge.log 是最大的一个文件
        if !crate::logs_on() {
            return;
        }
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
        if !crate::logs_on() {
            return;
        }
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
        // 用户关掉日志 ⇒ 统计文件也不写。放在函数入口最保险（不依赖调用点顺序）
        if !crate::logs_on() {
            return;
        }
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
            Some(Feed { reg, payload: vec![0xA5u8; super::FLUSH_FEED_BYTES] })
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

/// 本地时间 "HH:MM:SS.mmm"（日志用，判断"按住多久"靠它）
fn hms_ms() -> String {
    use windows::Win32::System::SystemInformation::GetLocalTime;
    unsafe {
        let t = GetLocalTime();
        format!(
            "{:02}:{:02}:{:02}.{:03}",
            t.wHour, t.wMinute, t.wSecond, t.wMilliseconds
        )
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

/// 蓝牙设备名里是否含 "IPRC"（与 C# 侧 RemoteNameHint 一致）
///
/// 读 `HKLM\SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices\<MAC>\Name`，
/// 值是 UTF-8 字节串（可能有结尾 NUL）。这是"配对过的蓝牙设备名"的权威来源。
#[cfg(target_os = "windows")]
fn bth_device_name_present() -> bool {
    use std::os::windows::ffi::OsStrExt;
    #[link(name = "advapi32")]
    extern "system" {
        fn RegOpenKeyExW(h: usize, sub: *const u16, opt: u32, sam: u32, out: *mut usize) -> i32;
        fn RegEnumKeyExW(h: usize, idx: u32, name: *mut u16, len: *mut u32,
                         res: *mut u32, class: *mut u16, clen: *mut u32, ft: *mut u8) -> i32;
        fn RegQueryValueExW(h: usize, name: *const u16, res: *mut u32, ty: *mut u32,
                            data: *mut u8, len: *mut u32) -> i32;
        fn RegCloseKey(h: usize) -> i32;
    }
    const HKEY_LOCAL_MACHINE: usize = 0x8000_0002;
    const KEY_READ: u32 = 0x2_0019;
    let root: Vec<u16> = std::ffi::OsStr::new("SYSTEM\\CurrentControlSet\\Services\\BTHPORT\\Parameters\\Devices")
        .encode_wide().chain(std::iter::once(0)).collect();
    let val: Vec<u16> = std::ffi::OsStr::new("Name").encode_wide().chain(std::iter::once(0)).collect();
    unsafe {
        let mut hk: usize = 0;
        if RegOpenKeyExW(HKEY_LOCAL_MACHINE, root.as_ptr(), 0, KEY_READ, &mut hk) != 0 { return false; }
        let mut buf = [0u16; 256];
        let mut i = 0u32;
        let mut found = false;
        loop {
            let mut len = buf.len() as u32;
            let rc = RegEnumKeyExW(hk, i, buf.as_mut_ptr(), &mut len,
                                   std::ptr::null_mut(), std::ptr::null_mut(),
                                   std::ptr::null_mut(), std::ptr::null_mut());
            if rc != 0 { break; }
            let mac: Vec<u16> = buf[..len as usize].to_vec();
            let mut sub: usize = 0;
            if RegOpenKeyExW(hk, mac.as_ptr(), 0, KEY_READ, &mut sub) == 0 {
                let mut data = [0u8; 128];
                let mut dlen: u32 = data.len() as u32;
                let mut ty: u32 = 0;
                if RegQueryValueExW(sub, val.as_ptr(), std::ptr::null_mut(), &mut ty,
                                    data.as_mut_ptr(), &mut dlen) == 0 && dlen > 0 {
                    let text = String::from_utf8_lossy(&data[..dlen as usize]).to_lowercase();
                    if text.contains("iprc") { found = true; }
                }
                RegCloseKey(sub);
            }
            if found { break; }
            i += 1;
        }
        RegCloseKey(hk);
        found
    }
}

#[cfg(not(target_os = "windows"))]
fn bth_device_name_present() -> bool { true }

/// 遥控器是否还在系统里（只看注册表，1 秒最多查一次）
#[cfg(target_os = "windows")]
fn remote_present() -> bool {
    use std::os::windows::ffi::OsStrExt;

    // ★★ 2026-10-29：**按蓝牙名称识别**（用户确认：设备名固定为 IPRC1000）。
    //   原来的判据是 `Enum\HID` 里出现 `0a5c` && `8502` —— 那是 Broadcom 的 VID/PID，
    //   换一只不同批次/不同芯片的遥控器就会**误判成"未连接"**。
    //   名称在 `Services\BTHPORT\Parameters\Devices\<MAC>\Name`（UTF-8 字节串），
    //   与 C# 侧 Env.PairedRemoteMacs() 用的是同一个来源，两边判据保持一致。
    if bth_device_name_present() { return true; }

    // 兜底：老逻辑（有些堆栈不写 BTHPORT\Name）
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

/// ## 锁序铁律（改这块之前必读）
///
/// 本结构体同时被四条线程访问：ETW 回调（热路径）、统计/落盘线程、UI 线程、托盘线程。
/// 涉及**两把锁**时，顺序**永远**是：
///
/// ```text
///   桥锁（Arc<Mutex<Bridge>>）  →  stats 锁（Bridge::stats）
/// ```
///
/// 反过来的写法（先 stats 再桥锁）与热路径形成**锁序反转**：
/// 热路径持桥锁等 stats，另一边持 stats 等桥锁 ⇒ 双方永久互等。
/// 症状是"进程活着、界面和按键全停"，且取决于两线程时序、极难复现。
/// 2026-11-01 在统计线程里发现并修正过一处（原来就是 st→br）。
///
/// 另：**热路径里不要做 I/O**（写文件、弹框、等子进程）。一次慢盘就顶住 ETW 回调，
/// 缓冲溢出后表现为"丢事件"，而且日志里看不出因果。
pub struct Bridge {
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
    /// 语音键"连续多少帧没出现"—— 用来判断真正的松手。
    /// 只认"空报文"会永久卡住（松手时遥控器常发带数据的报文）。
    voice_missing: u8,
    /// 最近一次收到 HID 报文的时刻（用来判断"报文流停了"= 遥控器确实松手了）
    last_report_at: Option<Instant>,
    /// 音频通道健康检测：上一次看到音频帧的时刻（用于判断 ACL 通道是否死了）
    last_frame_seen: Option<Instant>,
    /// 上次告警时刻（避免刷屏）
    audio_dead_warned: Option<Instant>,
    /// 注入器失效、需要重建（`submit_report` 返回 false 时置位）。
    ///
    /// 为什么需要：Windows 会重置 HID 设备（USB 电源管理、驱动更新、设备管理器刷新），
    /// 长时间运行后 WinUHid 句柄会失效——此后所有注入静默失败，程序以为发了 UP，
    /// 系统却还按着 Alt ⇒ **运行一段时间后开始"卡键"**（用户实测：一开始正常，后面不行）。
    inline_dirty: bool,
    /// 上次自检时 `held[0x10]` 的值（只在语音键状态变化时才反查系统，避免频繁调 API）
    last_ralt_checked: bool,
    /// 右 Alt 状态核对的时间点（延后 120 ms，避开系统更新延迟）
    ralt_check_at: Option<Instant>,
    /// 语音租约的"对账"节流：每 500 ms 最多问一次系统真实状态（成本≈0）
    last_voice_verify: Option<Instant>,
    /// 上次尝试重建注入器的时间（避免每 tick 都 open，失败时很浪费）
    last_inject_retry: Option<Instant>,
    /// 音频流正在后台重开时的接收端。
    /// 为什么不直接在回调里 open：`CableOut::open` 内部有 recv_timeout(8s)，
    /// 在 ETW 回调线程上阻塞几百毫秒就会让 ETW 缓冲溢出、大量丢事件。
    audio_reopen_rx: Option<std::sync::mpsc::Receiver<Result<crate::cable_out::CableOut, String>>>,
    /// 语音键"第一次发现不在报文里"的时刻 —— 用**时间**而不是帧数判松手。
    /// 为什么不用帧数：遥控器按住时会周期性插心跳报文，偶尔连续 3 个心跳就被
    /// 误判成松手（实测 10 次里约 1 次，表现是右 Alt 被"点"了一下 → 豆包走内容整理）。
    voice_missing_since: Option<Instant>,
    /// 本次按住语音键期间**是否听到过音频**。
    /// 判"音频停了"之前必须先有过音频，否则"按下还没开口"会被当成"说完了"。
    voice_had_audio: bool,
    /// 点按模式的锁存状态：true = 语音键"已按下并保持"，等下一次按下时释放。
    /// 只在 `trigger == tap` 时有意义（2026-10-31）。
    tap_followup: iprc1000_native::voice::TapFollowUp,
    /// 点按模式：第一下已发出去、还等松手那一拍。
    /// 为什么需要它：点按模式下 `held[0x10]` 在 60 ms 后就变回 false，
    /// 而"松手"分支挂在 `held[u] && !now[u]` 里 ⇒ 根本进不去，第二下永远发不出来。
    /// 而且**不能**无条件对每个空报文都补一下：空闲时按别的键也会产生空报文。
    tap_awaiting: bool,
    /// 点按模式：第一下发出的时刻。用来判断"这次按压之后音频来过没有"——
    /// 静默兜底不能用 `voice_had_audio`，那个只在 held[0x10] 为真时置位，
    /// 而点按模式下 held 只有 60 ms（说话期间是 false）。
    tap_started_ms: u64,
    /// 最近一次**真正注入**语音键（0x10）的时刻。
    /// 用来给"对账"一个宽限：刚注入完就立刻查 GetAsyncKeyState，
    /// 系统还没来得及反映 ⇒ 会误判成"没按"并补发一份重复的 DOWN。
    /// 60 ms 的点按几乎每次都撞上（真机日志里能直接看到）。
    last_voice_inject_ms: u64,
    /// 每个键**按下的时刻**（看门狗用）。UP 丢了时靠它兜底释放，
    /// 否则那个键会永久留在"按下"状态（遥控器按住期间不上报，没人能纠正）。
    /// 每个键**最迟放手时刻**（毫秒时间戳；0 = 没按住）。
    ///
    /// ★★ 2026-11-01（#2 统一每键 deadline）：原来这里是 `[Option<Instant>; 256]`，
    ///   与看门狗里的 `max_hold_ms` 合起来表达同一件事，但分成两半。
    ///   现在合并成一个数：**按下即写好到期时刻**，看门狗只做"到点没到点"的比较。
    ///   顺带满足 #3 的前置条件——无锁状态需要原子 u64，`Option<Instant>` 不行。
    deadline_ms: [u64; 256],
    /// 语音键本次按下的时刻（0 = 未按）。租约只回答"还该按多久"，
    /// 回答不了"按了多久"——会话静默守卫需要后者。
    voice_pressed_at_ms: u64,
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
    /// 最后一个音频帧到达时的**链路层事件计数**（用来算空档期间还有没有事件在来）
    other_at_frame: u64,
    prev: i16, // 48k 内插用的上一个样本
    have_prev: bool,
    samples16: u64,
    /// 丢掉最前面几帧（高通瞬态 + 首个不完整段）。
    ///
    /// ★ 2026-10-17 从 2 帧降到 1 帧：每帧 20 ms，少丢一帧 = **首字早 20 ms**。
    ///   70 Hz 二阶高通的瞬态在 20 ms 内基本衰减完；多丢的那一帧纯粹是在拖慢首字。
    warmup: u32,
    pending: Option<Vec<u8>>, // ETW 回调里暂存的首片 ACL 数据
    /// `--cable` 用的本地音频输出（None = 只发 UDP，行为与以前完全一致）
    pub cable: Option<crate::cable_out::CableOut>,
    /// `--inline` 用的虚拟键盘（`None` = 按键照旧走 UDP 给 C#，行为与以前完全一致）
    inline: Option<crate::winuhid::Injector>,
    /// 上一次真正提交给系统的整份报告 —— 内容没变就不重复提交
    inline_last: [u8; 8],
}

impl Bridge {
    fn new(
        hp: f32,
        udp: Option<UdpSink>,
        wav: Option<hound::WavWriter<std::io::BufWriter<std::fs::File>>>,
        key_sink: Option<UdpSink>,
        stats: Arc<Mutex<Stats>>,
        inline: Option<crate::winuhid::Injector>,
        cable: Option<crate::cable_out::CableOut>,
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
            deadline_ms: [0; 256],
            voice_pressed_at_ms: 0,
            voice_missing: 0,
            last_report_at: None,
            last_frame_seen: None,
            audio_dead_warned: None,
            inline_dirty: false,
            last_ralt_checked: false,
            ralt_check_at: None,
            last_voice_verify: None,
            last_inject_retry: None,
            audio_reopen_rx: None,
            voice_missing_since: None,
            voice_had_audio: false,
            tap_followup: iprc1000_native::voice::TapFollowUp::None,
            tap_awaiting: false,
            tap_started_ms: 0,
            last_voice_inject_ms: 0,
            last_ts: 0,
            stats,
            wav,
            in_session: false,
            last_frame_at: None,
            other_at_frame: 0,
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
            inline,
            inline_last: [0u8; 8],
            cable,
        }
    }

    /// 把"当前按住集合"作为**整份快照**提交给虚拟键盘。
    ///
    /// 只在 `--inline` 时生效；内容与上次相同就不重复提交（遥控器只在状态变化时上报，
    /// 但空报文/幽灵报文也会走到这里）。顺带算一次**端到端延迟**：
    /// ETW 报告里的 FILETIME（设备产生事件）→ 注入完成，这正好是原生内核要证明的那个数。
    fn flush_inline(&mut self) {
        let Some(inj) = self.inline.as_mut() else { return };
        let report = crate::winuhid::report_of(&self.held);
        if report == self.inline_last {
            return;
        }
        // ★★ 2026-10-31（组合键分帧）：同时含修饰键和普通键时，**先发一帧只有修饰键**，
        //   12 ms 后再发完整帧。与 C# `WinUHidInjector.Send` 的做法一致。
        //   不分帧的后果（C# 侧实测过）：系统按"同一份报告"处理，Ctrl+Enter 里的 Ctrl
        //   有时不被认作"已按下"，于是变成光秃秃一个 Enter（Ctrl+[ / Alt+Esc 同理）。
        //   ⚠ 这 12 ms 是在 ETW 回调线程上 sleep，会让 ETW 缓冲多积 12 ms 事件；
        //     只发生在"按下组合键"这一瞬间，实测丢帧为 0，可以接受。
        if report[0] != 0 && report[2..8].iter().any(|&b| b != 0) {
            let mut mod_only = [0u8; 8];
            mod_only[0] = report[0];
            inj.submit_report(&mod_only);
            if !diag::QUIET.load(Ordering::Relaxed) {
                // 单独记一行：以后排查"组合键没生效"时，先看这一帧在不在 ——
                // 不在 = 分帧没走；在但主键帧没生效 = 12 ms 不够，要加大。
                eprintln!("[INJ] mod-first={:02X?}（分帧，等 12 ms）", mod_only);
            }
            std::thread::sleep(Duration::from_millis(12));
        }
        let ok = inj.submit_report(&report);
        if !ok {
            // 标记重建，tick 里会重新 open 并**重发当前状态**
            self.inline_dirty = true;
        }
        self.inline_last = report;
        if !diag::QUIET.load(Ordering::Relaxed) {
            // FILETIME(1601, 100ns) → 距今多少 ms
            let lat = if self.last_ts > 0 {
                const EPOCH_DIFF: i128 = 116_444_736_000_000_000;
                let ticks = self.last_ts as i128 - EPOCH_DIFF;
                let evt = std::time::UNIX_EPOCH + Duration::from_nanos((ticks * 100) as u64);
                match std::time::SystemTime::now().duration_since(evt) {
                    Ok(d) => format!("{:.1} ms", d.as_secs_f64() * 1000.0),
                    Err(_) => "-".to_string(),
                }
            } else {
                "-".to_string()
            };
            eprintln!(
                "[INJ] report={:02X?} 延迟={} {}",
                report,
                lat,
                if ok { "ok" } else { "★驱动拒绝" }
            );
        }
        // ★ 自检：注入了不等于系统认了。直接问系统"右 Alt 现在是否按着"，
        //   和我们的预期比对 —— 这是唯一能区分"我们的问题"和"下游的问题"的办法。
        //   GetAsyncKeyState 对虚拟 HID 设备有效（驱动层看它就是真实键盘）。
        // ★ 不要在这里立即查：注入到系统状态更新有几十毫秒延迟，
        //   立即查必然读到"上一个状态"，产生大量假告警（实测每次 DOWN/UP 都会报不一致）。
        //   改为 120 ms 后再核对。
        if self.held[0x10] != self.last_ralt_checked {
            self.ralt_check_at = Some(Instant::now() + Duration::from_millis(120));
            self.last_ralt_checked = self.held[0x10];
        }
    }

    /// 音频通道健康检测。
    ///
    /// 背景（2026-10-31 实测）：程序运行一段时间后，**按键（HID）还能收到，
    /// 但音频（ACL）事件完全停止投递** —— 帧数冻结、`距上次音频` 一路涨到几分钟，
    /// 重启程序立刻恢复。此时遥控器亮绿灯、按下也有日志，但豆包没有波形。
    ///
    /// 判据：**最近 60 秒收到过按键事件，但音频帧 0 增长** ⇒ 音频通道死了。
    /// 这条只做告警（不自动重启，避免重启循环）；用户看到提示后重启即可。
    fn check_audio_health(&mut self) {
        const SILENT_MS: u128 = 60_000;
        const WARN_INTERVAL_MS: u128 = 120_000;
        let Some(last_frame) = self.last_frame_seen else { return };
        // 音频刚来过就不管
        if last_frame.elapsed().as_millis() < SILENT_MS {
            return;
        }
        // 60 秒内有没有按键事件？没有就可能是单纯没用遥控器
        let (recent_key, last_key_ms) = {
            let st = self.stats.lock().unwrap_or_else(|e| e.into_inner());
            (st.last_key_ms != 0, st.last_key_ms)
        };
        if !recent_key {
            return;
        }
        let key_age = diag::now_ms().saturating_sub(last_key_ms);
        if key_age as u128 > SILENT_MS {
            // 按键也很久没来 ⇒ 只是没在用，不算异常
            return;
        }
        // 都满足：按键在来、音频不来 ⇒ 音频通道死了
        if let Some(w) = self.audio_dead_warned {
            if w.elapsed().as_millis() < WARN_INTERVAL_MS {
                return;
            }
        }
        self.audio_dead_warned = Some(Instant::now());
        eprintln!(
            "[!] ★音频通道失效：按键正常（{} ms 前有按键），但音频已 {} 秒没有新帧。",
            key_age,
            last_frame.elapsed().as_secs()
        );
        eprintln!("    ETW 的 ACL 音频事件停止投递（重启程序即可恢复；这是已知问题）");
        diag::log("[!] 音频通道失效（按键正常但无音频帧）—— 需重启程序");
    }

    /// 到点了就核对右 Alt 在系统里的真实状态（由 tick 调用）。
    fn poll_ralt_check(&mut self) {
        let Some(at) = self.ralt_check_at else { return };
        if Instant::now() < at {
            return;
        }
        self.ralt_check_at = None;
        self.check_ralt_state();
    }

    /// 反查系统侧右 Alt 的真实状态，与 `held[0x10]` 比对；不一致就记录并重发。
    fn check_ralt_state(&mut self) {
        const VK_RMENU: i32 = 0xA5;
        let actual = unsafe {
            (windows::Win32::UI::Input::KeyboardAndMouse::GetAsyncKeyState(VK_RMENU) as u16) & 0x8000 != 0
        };
        let expected = self.held[0x10];
        self.last_ralt_checked = expected;
        if actual != expected {
            eprintln!(
                "[INJ] ★右Alt状态不一致：程序期望 {expected}，系统实际 {actual} ⇒ 强制重发一次"
            );
            // 强制重发：清掉 inline_last 让它不被"状态没变"短路掉
            self.inline_last = [0u8; 8];
            self.flush_inline();
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
                            let mut st = self.stats.lock().unwrap_or_else(|e| e.into_inner());
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
                    self.stats.lock().unwrap_or_else(|e| e.into_inner()).audio_cid.get_or_insert(cid);
                    if truncated {
                        // 首片只带回了 L2CAP 头 → 这一帧不可能补齐
                        self.pending = None;
                        self.stats.lock().unwrap_or_else(|e| e.into_inner()).truncated_first += 1;
                        return;
                    }
                    if self.pending.is_some() {
                        // 上一个 PDU 还没凑齐，新的起始分片就来了 ⇒ 那个 PDU 确实没收全
                        self.stats.lock().unwrap_or_else(|e| e.into_inner()).lost_cont += 1;
                    }
                    self.pending = Some(acl.to_vec());
                    self.try_complete();
                    return;
                }

                // L2CAP 信令通道：建链/协商用的小包，不是音频也不是按键。
                // ★ 单独计数而不是丢进"未识别" —— 那会让界面显示"长度不认识 N"，
                //   看起来像帧格式出了问题，实际完全无害。
                if cid == CID_SIG {
                    self.stats.lock().unwrap_or_else(|e| e.into_inner()).sig_frag += 1;
                    return;
                }

                let mut s = self.stats.lock().unwrap_or_else(|e| e.into_inner());
                note_odd(&mut s, flag, dlen, acl);
            }

            // ── 后续分片（PB=01）：长度随便多少都收 ──
            1 => {
                if self.pending.is_none() {
                    // 起始分片被丢了，续片成了孤儿
                    self.stats.lock().unwrap_or_else(|e| e.into_inner()).orphan_cont += 1;
                    return;
                }
                {
                    let (mut s, p) = (self.stats.lock().unwrap_or_else(|e| e.into_inner()), self.pending.as_mut().unwrap());
                    if truncated { s.trunc_cont += 1; }
                    bump_kind(&mut s.cont_kinds, acl.len() as u16);
                    p.extend_from_slice(acl);
                }
                self.try_complete();
            }

            _ => {
                let mut s = self.stats.lock().unwrap_or_else(|e| e.into_inner());
                note_odd(&mut s, flag, dlen, acl);
            }
        }
    }

    /// 攒够一整帧就交出去（`pending` 里是 L2CAP 头 + 负载）
    fn try_complete(&mut self) {
        if self.pending.as_ref().map(|p| p.len()).unwrap_or(0) < AUDIO_TOTAL {
            return;
        }
        let Some(p) = self.pending.take() else { return };
        if p.len() != AUDIO_TOTAL {
            // 拼是拼够了，但长度不是我们认识的那一种（比如换了音频配置）——
            // ★ 必须留痕：这正是"看着有声、其实解不出来"的那类情况。
            self.stats.lock().unwrap_or_else(|e| e.into_inner()).short_pdu += 1;
            return;
        }
        self.push_payload(&p[L2CAP_HDR..]);
        self.stats.lock().unwrap_or_else(|e| e.into_inner()).frames += 1;
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

        // 纯函数：报文 → 按键集合（见 report::key_set，有单测）
        let now = iprc1000_native::report::key_set(usages);


        // ⓪ 本段的第一条报文：只当基线、**不发任何按键事件**。
        //
        //   ★ 2026-10-18 用户实测：双击 exe 启动程序 → 光标处的文件**弹出右键菜单**，
        //     即"菜单键(0x4E)被按下了一次"。原因就是 `held` 初值全 false，
        //     而程序刚起来时收到的第一条报文里**已经带着那个键**（设备正抱着键 / 重连补发），
        //     于是被当成"刚按下"。
        //   ⇒ 每条（段）会话的第一条报文只记账：早已按着的键不算"刚按下"，
        //     用户真正需要的按键事件从第二条报文开始，代价是 20 ms。
        if !self.seeded {
            // ★★ 2026-10-30（用户实测：启动后第一次按键必须"点一下"才生效）：
            //   **空报文立即结束基线窗口**。
            //
            //   基线的作用只有一个：防止"进程启动瞬间设备正抱着键"被误判成一次按下
            //   （历史事故：双击 exe，光标处的文件弹出右键菜单）。
            //   但遥控器**只在状态变化时上报** ⇒ 桥起来时若遥控器静置，
            //   第一份报文必然是空报文 —— 它已经证明"没有任何键被抱着"，
            //   此时再拿 1.5 秒的时间窗去吞掉用户的第一下真实按键，纯属自伤。
            //   真机症状就是：启动后第一次按语音键完全没反应（被当基线吞了），第二次正常。
            //
            //   所以：空报文（或只含幽灵 usage 的心跳报文）⇒ 立刻关窗，
            //   之后的任何按键都按"真实按下"处理。
            //   只有"启动 1.5 秒内、且报文里已经带着键"才继续走基线逻辑。
            let no_real_key = !usages.iter().any(|&u| u != 0 && u != 0x01 && u != 0x0C);
            if no_real_key {
                self.seeded = true;
                self.held = [false; 256];        // 等价于"没有任何键按着"
                self.stats.lock().unwrap_or_else(|e| e.into_inner()).seed_reports += 1;
                return;
            }
            // ★ 基线规则只在**启动后 1.5 秒内**生效。
            //   超过这个时间还出现"首份报文"，说明是用户真的按下了（不是启动时设备就已抱着键），
            //   必须正常对账、发出 DOWN —— 否则"启动后第一次按下被吞"这个 bug 会一直存在。
            if self.started.elapsed().as_millis() > 1500 {
                self.seeded = true;
                self.stats.lock().unwrap_or_else(|e| e.into_inner()).seed_reports += 0;   // 不计入"基线吞掉"
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
            self.stats.lock().unwrap_or_else(|e| e.into_inner()).seed_reports += 1;
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
        // 报文流时间戳：只要遥控器还在发（哪怕只有心跳），这里就更新。
        // ⚠ 2026-10-31 租约模型之后，"报文间隔"**不再参与松手判断**（那是在猜），
        //   这里只保留时间戳给诊断/统计用。
        self.last_report_at = Some(Instant::now());

        // 纯函数：只含幽灵 usage（0x01 / 0x0C）的报文不是键盘快照（见 report::is_ghost_only）
        if iprc1000_native::report::is_ghost_only(usages) {
            self.stats.lock().unwrap_or_else(|e| e.into_inner()).ghost_reports += 1;
            return;
        }

        // ⓪-2 记录"启动后第一个带键的报文"（**纯诊断，不参与逻辑**）。
        //   真机出现过"启动瞬间弹出右键菜单"，最后查明是**上位机自己**发的全零报告
        //   （见 v1.2.33），与这里无关；但留下这两个数，下次再有类似现象能直接定性。
        if !self.first_key_done {
            if let Some(u) = now.iter().position(|&b| b) {
                self.first_key_done = true;
                let mut st = self.stats.lock().unwrap_or_else(|e| e.into_inner());
                st.first_key_seen = true;
                st.first_key_ms = self.started.elapsed().as_millis() as u64;
                st.first_key_usage = u as u8;
            }
        }

        // ★ 报文里**又出现**了语音键 ⇒ 说明还按着，清掉"缺失计时"。
        //   不清的话：中间插了一帧心跳，下一次无语音键会接着老时间算 ⇒ 立刻判超时。
        if now[0x10] {
            self.voice_missing_since = None;
        }

        // ★★ 点按模式：松手（空报文）⇒ 发第二下。必须放在 UP 循环**之前**：
        //   点按模式下按下那一下 60 ms 就已弹起，`held[0x10]` 是 false，
        //   UP 循环根本不会进语音分支，第二下永远发不出来（实测会缺一次）。
        //   用 `tap_awaiting` 而不是"每个空报文都补"：空闲时按别的键也会产生空报文，
        //   无条件补就会凭空多出一次语音点击。
        if crate::settings::TRIGGER_IDX.load(Ordering::Relaxed) == 1
            && self.tap_awaiting
            && iprc1000_native::report::is_empty(usages)
        {
            self.tap_awaiting = false;
            self.tap_followup = iprc1000_native::voice::TapFollowUp::schedule(diag::now_ms());
            eprintln!("[点按] 松手 ⇒ {} ms 后发第二下（豆包结束对话）",
                iprc1000_native::voice::TAP_GAP_MS);
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
                if u == VOICE_USAGE {
                    // ★ 2026-11-01：点按模式不再锁存，所以这里没有"锁着就不放"的特例了。
                    //   （语义改成方案 A：松手就结束，只是事后补一次点击。）
                    // ★★ 租约模型（2026-10-31 结构替换）：这里**不再判断"是否松手"**，
                    //   只提供一个"提前收口"的提示。判断松手是以前所有 bug 的来源：
                    //     · 按住期间遥控器零报文；
                    //     · 固件 30 秒自动收工不发松开报文；
                    //     · 按住时还会插 0x05/0x0C 心跳（也不含 0x10）。
                    //   三者长得一模一样，靠"间隔 >100 ms""连续 3 帧"去区分，本质都在猜。
                    //
                    //   现在只认**空报文**（真机反复验证：真正的松手一定是 `[]`）：
                    //     · 空报文   ⇒ 把租约收到 now（更快的放手，0.5~10 ms）
                    //     · 非空报文 ⇒ **完全不动租约**，让它按 T_PAUSE 自己到期
                    if iprc1000_native::report::is_empty(usages) {
                        self.voice_disarm("松手（空报文）");
                        // （点按模式的"第二下"在 UP 循环之前处理，见 tap_awaiting）
                    }
                    continue;   // held[0x10] 只能由 reconcile_voice 改
                }
                self.held[u] = false;
                self.deadline_ms[u] = 0;
                if crate::settings::is_mouse_active() {
                    if let Some(slot) = dir_slot(u as u8) {
                        crate::mouse_move::set_dir(slot, false);
                        continue;
                    }
                }
                if let Some(act) = crate::keymap::action_of(u as u8) {
                    crate::mouse_act::fire(act, false);
                    if !diag::QUIET.load(Ordering::Relaxed) {
                        eprintln!("[ACT] usage=0x{u:02X} {} UP（鼠标动作）", crate::mouse_act::action_name(act));
                    }
                    continue;
                }
                if self.seeded_held[u] {
                    // 这个键是"基线里就已经按着"的，我们从未发过它的 DOWN ⇒ UP 也不发
                    self.seeded_held[u] = false;
                    continue;
                }
                self.emit_key(Some(u as u8), false);
            }
        }
        // ★★ 语音键的"重按"必须在这里处理（2026-10-31 审查发现）。
        //
        //   为什么不能放进下面的 ② 循环：那个循环的条件是 `now[u] && !held[u]`，
        //   只认"新出现的键"；而重按恰恰是"now 有、held 也有"，条件为假 ——
        //   所以原来的写法是**死代码，永远跑不到**。
        //
        //   什么时候会出现"now 有、held 也有"：固件 30 秒自动收工那次松手**不发**报文，
        //   我们漏掉了那次松开；用户再按下时又来一条 0x10。
        //   （按住期间遥控器零报文 ⇒ 又来一条 0x10 在物理上只可能是"松开又按下"）
        //
        //   这里只续租、不重发报告：避免 [00]→[40] 的瞬间抖动被豆包当成点按。
        if now[0x10] && self.held[0x10] {
            eprintln!("[语音 {}] 按住中又收到 0x10（松开又按下）⇒ 续租", hms_ms());
            self.voice_arm(T_NOVOICE_MS);
        }

        // ② 再按下新出现的
        for u in 0..256usize {
            if now[u] && !self.held[u] {
                // 绑到文本槽的键：直接把这串字打出去（Unicode 注入，绕过输入法）
                if let Some(slot) = text_slot_of(u as u8) {
                    if let Some(t) = crate::text_slot::get(slot) {
                        if !t.text.is_empty() {
                            crate::macro_player::type_text_public(&t.text);
                            eprintln!("[文本] 槽 {} 已输出 {} 字", slot, t.text.chars().count());
                        }
                    }
                    self.held[u] = true;
                    self.arm_deadline(u);
                    continue;
                }
                // ★ 录制中：遥控器按键**不注入**，避免录进去、也避免打到前台窗口
                if crate::recorder::is_recording() {
                    continue;
                }
                // ★ 鼠标模式（组 1）：方向键驱动光标移动，不注入键盘
                if crate::settings::is_mouse_active() {
                    if let Some(slot) = dir_slot(u as u8) {
                        crate::mouse_move::set_dir(slot, true);
                        self.held[u] = true;
                        self.arm_deadline(u);
                        {
                            let mut st = self.stats.lock().unwrap_or_else(|e| e.into_inner());
                            st.last_key_usage = u as u8;
                            st.last_key_ms = diag::now_ms();
                        }
                        continue;
                    }
                }
                // ★ 录键模式：收到第一个键就录下并**不注入**（否则会打到前台窗口）
                if CAPTURING.load(Ordering::Relaxed) {
                    let vks = crate::keymap::vks_of(u as u8);
                    if let Ok(mut g) = captured().lock() {
                        *g = Some(vks);
                    }
                    CAPTURING.store(false, Ordering::Relaxed);
                    eprintln!("[录键] 捕获 usage=0x{u:02X}");
                    continue;
                }
                // ★ 播放中任意键 = 停止（C# 版同约定）。这一下**不注入**，
                //   否则"用来停止的那个键"还会在目标窗口打出一个字。
                if crate::macro_player::is_playing() {
                    crate::macro_player::request_stop();
                    if !diag::QUIET.load(Ordering::Relaxed) {
                        eprintln!("[宏] 收到按键 usage=0x{u:02X} ⇒ 请求停止播放");
                    }
                    continue;
                }
                // 绑到宏的键：起播（放独立线程，宏里有 wait 步骤）
                if let Some(num) = macro_ref_of(u as u8) {
                    if let Some(m) = crate::macro_store::get(num) {
                        let steps = m.steps.clone();
                        let n_steps = steps.len();
                        let lp = m.loop_count;
                        std::thread::spawn(move || {
                            let r = crate::macro_player::play(&steps, lp);
                            eprintln!("[宏] {r}");
                        });
                        self.held[u] = true;
                        self.arm_deadline(u);
                        if !diag::QUIET.load(Ordering::Relaxed) {
                            eprintln!("[宏] 起播 #{num}（{n_steps} 步，循环 {lp}）");
                        }
                    } else {
                        eprintln!("[宏] 绑定的宏 #{num} 不存在 —— 请在界面里重新保存一次");
                    }
                    continue;
                }
                // 绑了鼠标动作的键：直接执行动作，不进键盘报告
                if let Some(act) = crate::keymap::action_of(u as u8) {
                    // ★ "切换配置组"必须在这里真正执行 —— `mouse_act::fire` 只负责
                    //   "认领"（返回 true 防止漏成键盘键），它不知道有哪些组。
                    if act == crate::mouse_act::ACT_SWITCH_GROUP {
                        let n = crate::settings::groups_ui().len() as u8;
                        if n > 1 {
                            let next = (crate::settings::active_group() + 1) % n;
                            self.release_all_held();
                            crate::settings::set_active_group(next);
                            crate::keymap::load_group(&crate::settings::bindings_of(next));
                            let name = crate::settings::groups_ui()
                                .get(next as usize)
                                .and_then(|g| g["name"].as_str().map(|s| s.to_string()))
                                .unwrap_or_default();
                            eprintln!("[组] 已切换到 {next}「{name}」");
                        }
                        self.held[u] = true;
                        self.arm_deadline(u);
                        continue;
                    }
                    // 「打开程序」：单独一段动作值，启动放子线程（ShellExecute 会阻塞）
                    if let Some(num) = crate::shortcut_store::num_of_action(act as i64) {
                        std::thread::spawn(move || {
                            if let Err(e) = crate::shortcut_store::launch(num) {
                                eprintln!("[打开] {e}");
                            }
                        });
                        self.held[u] = true;
                        self.arm_deadline(u);
                        {
                            let mut st = self.stats.lock().unwrap_or_else(|e| e.into_inner());
                            st.last_key_usage = u as u8;
                            st.last_key_ms = diag::now_ms();
                        }
                        if !diag::QUIET.load(Ordering::Relaxed) {
                            eprintln!("[ACT] usage=0x{u:02X} 打开程序 #{num}");
                        }
                        continue;
                    }
                    crate::mouse_act::fire(act, true);
                    self.held[u] = true;
                    self.arm_deadline(u);
                    {
                        let mut st = self.stats.lock().unwrap_or_else(|e| e.into_inner());
                        st.last_key_usage = u as u8;
                        st.last_key_ms = diag::now_ms();
                    }
                    if !diag::QUIET.load(Ordering::Relaxed) {
                        eprintln!("[ACT] usage=0x{u:02X} {} DOWN（鼠标动作）", crate::mouse_act::action_name(act));
                    }
                    continue;
                }
                if u == 0x10 {
                    // ★★ 租约模型：语音键的所有语义都在这里，且只写租约、不写 held。
                    //
                    // ★★ 2026-11-01（点按模式，两次修正后的最终语义）：
                    //   点按模式 = **两次独立的"点按"**：
                    //     按下语音键 ⇒ 发一次点按（按下→弹起）⇒ 豆包弹框、开始录音
                    //     松手       ⇒ 再发一次点按            ⇒ 豆包结束对话
                    //   中间说话期间**不保持右 Alt** —— 音频走 CABLE 虚拟麦克风，
                    //   与键盘无关；压着右 Alt 反而会被豆包看成"一次长按"。
                    //
                    //   （第一版曾写成"按住整段 + 松手补一次"，实测豆包看到的是长按，已废弃。）
                    if crate::settings::TRIGGER_IDX.load(Ordering::Relaxed) == 1 {
                        // ★★ 点按模式（2026-11-01 二次修正）：**按下这一下本身就是一次点按**
                        //   —— 按下→弹起，模拟一次键盘按键，让豆包弹出语音框开始录音。
                        //   这段时间**不保持右 Alt**：音频走 CABLE 虚拟麦克风，与键盘无关，
                        //   遥控器按住只是在传音频。压着反而会被豆包看成"长按"。
                        // ★ 按下这一下**立刻发**：间隔（TAP_GAP_MS）只用于"松手后那一下"，
                        //   给第一下也加间隔等于白送 50 ms 延迟。
                        self.voice_arm(iprc1000_native::voice::TAP_HOLD_MS);
                        self.tap_awaiting = true;   // 等松手那一拍
                        self.tap_started_ms = diag::now_ms();
                        eprintln!("[点按] 按下 ⇒ 立即发一次点按（{} ms 后由 tick 弹起）",
                            iprc1000_native::voice::TAP_HOLD_MS);
                    } else if self.held[0x10] {
                        // 按住期间又收到 0x10 —— 物理上只可能是"松开又按下"：
                        // 按住期间遥控器零报文；而固件 30 秒自动收工那次松手**不发**报文，
                        // 所以我们漏掉了那次松开。这里只续租、不重发报告（避免
                        // [00]→[40] 的瞬间抖动被豆包当成点按）。
                        eprintln!("[语音 {}] 按住中又收到 0x10（松开又按下）⇒ 续租", hms_ms());
                        self.voice_arm(T_NOVOICE_MS);
                    } else {
                        // 无条件记录：以后出问题能直接看出"有没有走到这个分支"
                        eprintln!(
                            "[语音 {}] DOWN 按下（in_session={} 模式={}）",
                            hms_ms(),
                            self.in_session,
                            if crate::settings::TRIGGER_IDX.load(Ordering::Relaxed) == 1 {
                                "点按"
                            } else {
                                "按住"
                            }
                        );
                        self.voice_arm(T_NOVOICE_MS);
                    }
                    {
                        // 记录"最近按了什么"，界面左侧栏靠它显示"已检测：↑ 上"
                        let mut st = self.stats.lock().unwrap_or_else(|e| e.into_inner());
                        st.last_key_usage = u as u8;
                        st.last_key_ms = diag::now_ms();
                    }
                    continue;   // held[0x10] 只能由 reconcile_voice 改
                }
                self.held[u] = true;
                self.arm_deadline(u);
                {
                    // 记录"最近按了什么"，界面左侧栏靠它显示"已检测：↑ 上"
                    let mut st = self.stats.lock().unwrap_or_else(|e| e.into_inner());
                    st.last_key_usage = u as u8;
                    st.last_key_ms = diag::now_ms();
                }
                self.emit_key(Some(u as u8), true);
            }
        }
        // ★ 2026-10-30：整份快照模型下，一次对账结束后就是"系统的真实状态"。
        //   在这里提交一次，天然满足"松开要重发少一个键的整份报告"。
        self.flush_inline();
    }

    /// 距上一次遥控器事件的毫秒数（u64::MAX = 从未有过）
    pub fn idle_age_ms(&self) -> u64 {
        let st = match self.stats.lock() {
            Ok(g) => g,
            Err(_) => return u64::MAX,
        };
        if st.last_event_ms == 0 {
            return u64::MAX;
        }
        diag::now_ms().saturating_sub(st.last_event_ms)
    }

    /// 宏播放专用：直接提交一份报告（**不动 held 状态**，也不更新 inline_last）。
    ///
    /// 为什么不复用 flush_inline：那个是"把当前按住的键同步给系统"，
    /// 宏是"额外按一下就走"，如果混进去会把遥控器物理按键的状态覆盖掉。
    pub fn macro_send(&mut self, report: [u8; 8]) -> bool {
        match self.inline.as_mut() {
            Some(inj) => inj.submit_report(&report),
            None => false,
        }
    }

    // ══════════════ 语音租约：唯一的语音状态写入通道 ══════════════
    //
    // 规则（改这块之前先读这三条）：
    //   1. **任何代码都不许直接改 `held[0x10]`** —— 只有 `reconcile_voice` 能改；
    //   2. 业务侧只描述"右 Alt 还应该按多久"，用 arm/disarm 表达，不表达"结束"；
    //   3. 可靠事件（音频帧）可以续租；**不可靠事件（松开报文、断链）只能收口**，
    //      绝不能延长 —— 这样它丢了最坏只是多按一个 T_PAUSE，而不是永久卡键。

    /// 续租：从现在起 `ms` 毫秒内右 Alt 应该按住。
    fn voice_arm(&mut self, ms: u64) {
        let now = diag::now_ms();
        VOICE_LEASE_MS.store(iprc1000_native::voice::arm(now, ms), Ordering::Relaxed);
        self.reconcile_voice("arm");
    }

    /// 收口：让租约立刻到期（松手 / 断链 / 退出 / 解卡键）。
    fn voice_disarm(&mut self, why: &str) {
        VOICE_LEASE_MS.store(iprc1000_native::voice::disarm(diag::now_ms()), Ordering::Relaxed);
        self.reconcile_voice(why);
    }

    /// **唯一**把 `held[0x10]` 与租约对齐的地方，并顺带向系统对账。
    ///
    /// 它被三处调用：租约写入后立即对账（保证松手仍是 0.5~10 ms）、
    /// tick 每 50 ms（保证"没有事件也会到期"）、以及退出/解卡键。
    fn reconcile_voice(&mut self, why: &str) {
        let want = iprc1000_native::voice::should_hold(
            diag::now_ms(),
            VOICE_LEASE_MS.load(Ordering::Relaxed),
        );
        if want == self.held[0x10] {
            // 状态已经对齐 —— 但还要问一句系统：注入真的生效了吗？
            if want {
                self.verify_voice_injected();
            }
            return;
        }
        self.held[0x10] = want;
        self.deadline_ms[0x10] = if want { VOICE_LEASE_MS.load(Ordering::Relaxed) } else { 0 };
        // 语音键"刚按下多久"由这个字段回答（会话静默守卫要用，看不出租约）
        self.voice_pressed_at_ms = if want { diag::now_ms() } else { 0 };
        if want {
            self.seeded_held[0x10] = false;   // 从这一刻起归我们管，基线记账作废
            self.voice_missing_since = None;
            self.voice_had_audio = false;   // 新一段开始，"听到过音频"重新计
        } else {
            self.voice_missing = 0;
            self.voice_missing_since = None;
            // ★ 启动基线里"早已按着"的语音键：我们从没发过它的 DOWN，
            //   这里就不能发 UP（否则面板出现"松开比按下多"——项目里踩过）。
            if self.seeded_held[0x10] {
                self.seeded_held[0x10] = false;
                return;
            }
        }
        if !diag::QUIET.load(Ordering::Relaxed) {
            if want {
                eprintln!("[语音 {}] 租约续上 ⇒ 按住右Alt（{why}）", hms_ms());
            } else {
                // 放开时把"这一段到底有没有听到声音"一起写出来：
                //   had_audio=false ⇒ 按下后一直没开口（或链路/麦克风有问题）
                //
                // ★ 2026-11-01：原来固定写"租约到期"，但触发放开的可能是
                //   "松手（空报文）"这种**收口**事件 —— 两者语义不同，
                //   写在一起会误导排障（真机日志里就出现过
                //   "租约到期 ⇒ 放开（松手（空报文））"这种自相矛盾的一句）。
                //   现在按原因分档：松手/断链/退出是"收口"，其余才是"到期"。
                let how = if why.contains("松手") || why.contains("断链") || why.contains("退出")
                    || why.contains("解卡")
                {
                    "收口"
                } else {
                    "租约到期"
                };
                eprintln!(
                    "[语音 {}] {how} ⇒ 放开右Alt（{why}，had_audio={}）",
                    hms_ms(),
                    self.voice_had_audio
                );
            }
        }
        self.emit_key(Some(0x10), want);
        self.flush_inline();
        self.last_voice_inject_ms = diag::now_ms();
    }

    /// 对账：租约说"按着"，去问系统（GetAsyncKeyState）是否真的按着；不是就补发一次整份报告。
    ///
    /// 成本：每 500 ms 一次 `GetAsyncKeyState`（微秒级）—— 比任何超时判据都便宜，
    /// 而且它直接读系统的真相，不是我们的账本。
    fn verify_voice_injected(&mut self) {
        // ★ 宽限：刚注入过语音键（150 ms 内）就先别对账 —— 系统还没来得及反映，
        //   这时候查 GetAsyncKeyState 必然"说没按"，于是补发一份重复的 DOWN。
        //   点按模式只有 60~100 ms，几乎每次都会撞上（真机日志可见）。
        if self.last_voice_inject_ms != 0
            && diag::now_ms().saturating_sub(self.last_voice_inject_ms) < 150
        {
            return;
        }
        let now = Instant::now();
        if let Some(t) = self.last_voice_verify {
            if now.duration_since(t).as_millis() < 500 {
                return;
            }
        }
        self.last_voice_verify = Some(now);
        let actual = unsafe {
            (windows::Win32::UI::Input::KeyboardAndMouse::GetAsyncKeyState(0xA5) as u16) & 0x8000 != 0
        };
        if !actual {
            eprintln!("[语音] ★对账：租约说按着，系统说没按 ⇒ 补发一份报告");
            self.inline_last = [0u8; 8];   // 清掉"状态没变"的短路，强制重发
            self.flush_inline();
        }
    }

    /// 切换「按住 / 点按」时清掉锁存 —— 否则上一次还锁着，切回按住模式的第一次按会没反应。
    pub fn reset_tap_latch(&mut self) {
        // 切换触发方式时清掉待办的点按与"等第二下"标记，
        // 否则切回按住模式还可能莫名多发一下。
        self.tap_awaiting = false;
        self.tap_started_ms = 0;
        if self.tap_followup.is_pending() {
            self.tap_followup = iprc1000_native::voice::TapFollowUp::None;
            self.voice_disarm("切换触发方式");
        }
    }

    /// **按住超时兜底**（2026-10-31，原生内核）。
    ///
    /// 为什么必须有：遥控器**按住期间一条报文都不发**，松开时才发一条空报文。
    /// 那条 UP 一旦在 ETW 里丢掉（缓冲溢出 / 链路抖动 / 设备刚好重连），我们就会永远
    /// 认为键还按着 —— 系统侧表现为那个键**一直按住**（写代码时是灾难性故障）。
    ///
    /// 与原版（C# 的 StuckGuard）的差别：那边注入在 A 进程、接收在 B 进程，
    /// 所以还要防"接收方死了、按键还在按着"；这里注入与接收**同进程**，
    /// 进程一没设备就销毁，只需防"进程还活着但 UP 丢了"这一种。
    ///
    /// 阈值取 **60 秒**（保守）：真人按住方向键拖鼠标远不到 60 秒；
    /// 而语音键有更灵敏的判据 —— 会话静默 600 ms 时 `tick` 会把所有键都放掉。
    /// 语音键兜底：**音频流停了**就认为松手。
    ///
    /// 判据选择过程（都试过）：
    ///   · "报文流停止" ❌　按住语音键期间遥控器不发 HID 报文（音频走独立链路），
    ///     一按下就判"流停了"⇒ 右 Alt 立刻弹起 ⇒ 完全说不了话（实测踩过）；
    ///   · "音频流停止" ✅　按住说话时音频帧持续到达（10~20 ms 一帧），
    ///     松手后音频停 ⇒ 这是唯一可靠的正向信号。
    ///
    /// 两个阈值都要保守：
    ///   · 按住不足 2 秒不判（刚按下、还没开口时本来就没音频）；
    ///   · 音频停 3 秒才判（人说话换气/停顿很少超过 3 秒，太短会把句子切断）。
    /// 音频流空闲释放：超时没收到音频帧就把 WASAPI 输出流关掉。
    ///
    /// 省的是：3~4 个线程 + 一批 WASAPI 句柄 + 一块环形缓冲。
    /// 代价：下次说话要重开（100~300 ms），期间前几帧丢弃 —— 所以阈值取 3 分钟，
    ///       只针对"真的很久没用语音"的场景，正常会话间隔（几秒~几十秒）不受影响。
    /// 注入器自愈：失效后重新打开，并把**当前所有按住的键重新下发一遍**。
    ///
    /// 关键点：重建后必须清 `inline_last` 再 flush，否则"当前状态没变"会被跳过，
    /// 系统侧仍停留在旧状态（这正是卡键的来源）。
    fn heal_injector(&mut self) {
        if !self.inline_dirty {
            return;
        }
        // 别每 tick 都试（重建失败时可能很频繁），大约每 2 秒试一次
        let Some(last) = self.last_inject_retry else {
            self.last_inject_retry = Some(Instant::now());
            return;
        };
        if last.elapsed() < Duration::from_secs(2) {
            return;
        }
        self.last_inject_retry = Some(Instant::now());
        match crate::winuhid::Injector::open() {
            Ok(inj) => {
                eprintln!("[INJ] 注入器已失效 ⇒ 已重建，并重发当前按键状态");
                self.inline = Some(inj);
                self.inline_last = [0u8; 8];   // 强制重发
                self.inline_dirty = false;
                self.last_inject_retry = None;
                self.flush_inline();
            }
            Err(e) => {
                eprintln!("[!] 注入器重建失败：{e}（2 秒后重试）");
            }
        }
    }

    /// 检查后台重开的音频流是否就绪（在 tick 里调，不阻塞）
    fn poll_audio_reopen(&mut self) {
        let Some(rx) = self.audio_reopen_rx.as_ref() else { return };
        match rx.try_recv() {
            Ok(Ok(c)) => {
                eprintln!("[音频] 输出流已重开：{}（{} 声道）", c.name, c.channels);
                self.cable = Some(c);
                self.audio_reopen_rx = None;
            }
            Ok(Err(e)) => {
                eprintln!("[!] 重开音频流失败：{e}（下次说话再试）");
                self.audio_reopen_rx = None;
            }
            Err(std::sync::mpsc::TryRecvError::Empty) => {}   // 还在开
            Err(std::sync::mpsc::TryRecvError::Disconnected) => {
                eprintln!("[!] 重开音频流的线程异常退出");
                self.audio_reopen_rx = None;
            }
        }
    }

    fn release_audio_if_idle(&mut self) {
        const IDLE: u128 = 180_000;
        if self.cable.is_none() {
            return;
        }
        // 遥控器还在发音频帧就绝不能关
        if let Some(t) = self.last_frame_at {
            if t.elapsed().as_millis() < IDLE {
                return;
            }
        } else {
            return;
        }
        self.cable = None;   // Drop 会停掉音频线程、关掉流
        eprintln!("[音频] 空闲 180 秒，已关闭输出流（省 3~4 个线程；下次说话自动重开）");
    }

    /// 某个键"最迟放手时刻"的分档上限（毫秒）。语音键走租约，这里只是兜底值。
    fn max_hold_ms(u: usize) -> u64 {
        match u {
            0x10 => 20_000,
            // 修饰键粘住最致命（Ctrl 粘住 = 全键盘变快捷键），15 秒就该放
            0x11 | 0x12 | 0x5B | 0xA0 | 0xA1 | 0xA2 | 0xA3 | 0xA4 | 0xA5 => 15_000,
            // 方向键在鼠标模式下本来就要长按
            0x4F | 0x50 | 0x51 | 0x52 => 60_000,
            _ => 20_000,
        }
    }

    /// 按下某个键时记下它的到期时刻（`deadline_ms`，0 = 未按住）。
    ///
    /// **单一表示**：不再有"held_at 时刻 + 阈值表"两半，也没有第二处阈值。
    fn arm_deadline(&mut self, u: usize) {
        self.deadline_ms[u] = diag::now_ms().saturating_add(Self::max_hold_ms(u));
    }

    fn watchdog_held(&mut self) {
        // ★ 阈值按"键的性质"分档（照 C# 版经验）：
        //   修饰键卡住最致命（Ctrl 粘住 = 全键盘变快捷键），15 秒就该放；
        //   方向键在鼠标模式下本来就要长按，给 60 秒；
        //   其余 20 秒。
        //   原来统一 60 秒 —— 用户体感就是"卡死了"。
        //   ⚠ 语音键（0x10）在循环里被**整条跳过**：它的松手由判据①②和"10 s 无音频"负责，
        //     计时判据只会误杀长句（见循环内说明）。所以这里的 0x10 分支不再使用。
        let mut released = false;
        for u in 0..256usize {
            if !self.held[u] {
                continue;
            }
            // ★★ 2026-10-31（按用户要求：**移除语音键的计时看门狗**）
            //
            //   为什么可以不要：语音键的"松手"有比计时准确得多的三条判据，计时只是猜。
            //     · ① 报文中断后恢复 = 松手（每次正常松手都必经，实测 0.5~10 ms 放出）
            //     · ② 音频停 3 s 且已按住 >2 s（松手后音频必停；UP 丢了也照样命中）
            //     · 「按住 10 s 一帧音频都没收到」= 无效按压（断链 / 没开麦）
            //   真机证据：30 秒长句在音频持续流动的情况下被 20000 ms 砍断 ——
            //   计时判据无法区分"用户在说长句"和"UP 丢了"，所以它只能误杀。
            //
            //   ⚠ 只移除语音键。**其它键的看门狗必须留着**：方向键卡住会让光标一直飘，
            //     Ctrl/Shift 卡住会让全键盘变快捷键，而那些键没有任何音频判据兜底。
            if u == 0x10 {
                continue;
            }
            // ★ 统一 deadline：按下时就写好了到期时刻，这里只做一次比较。
            //   语音键在上面已跳过，由租约（reconcile_voice）管。
            let now = diag::now_ms();
            if self.deadline_ms[u] == 0 {
                self.arm_deadline(u);   // 老状态没记时刻：补一个，下一轮再判
                continue;
            }
            if now < self.deadline_ms[u] {
                continue;
            }
            let limit = Self::max_hold_ms(u);
            self.held[u] = false;
            self.deadline_ms[u] = 0;
            self.seeded_held[u] = false;
            self.emit_key(Some(u as u8), false);
            released = true;
            diag::log_async(&format!(
                "GUARD usage=0x{u:02X} 按住超过 {limit} ms 仍未收到 UP ⇒ 强制释放（UP 丢了）"
            ));
            // ★ 同时打到主日志：排查时用户只看 native.log，只写 bridge.log 会"看不见"
            eprintln!("[语音] UP 判据③看门狗：usage=0x{u:02X} 按住超 {limit} ms 强制释放");
        }
        if released {
            self.flush_inline();   // 把系统侧的状态同步成"已放开"
        }
    }

    /// 会话结束 / 程序退出 / 界面点「解卡键」：把还按着的键全部放掉
    pub fn release_all_held(&mut self) {
        self.release_all_held_with(false);
    }

    /// `keep_voice=true`：**会话结束路径专用**，不动语音键。
    ///
    /// ★★ 2026-10-31 真机实测（20 次按压里 3 次被豆包当成「内容整理」）：
    ///   会话结束这一刀原来是"无条件全放"，包括用户**刚刚按下**的语音键。
    ///   而遥控器从按下列首帧音频有 300~500 ms 固件延迟 ⇒ 上一段的 END 计时器
    ///   （最后音频帧 + 600 静默 + 150 尾音 = 750 ms）经常在新按压之后才到期：
    ///
    /// ```text
    /// 20:38:20.208  [RAW][10]        用户按下（上一段还没结束）
    /// 20:38:20.673  会话结束(END)    上一段的计时器到期
    /// 20:38:20.681  [00…]            把刚按下 473 ms 的右 Alt 放掉 ← 就是这一刀
    /// 20:38:20.731  会话开始(CLEAR)  新一段音频 58 ms 后才开始
    /// ```
    ///   豆包收到的是"短按 0.47 s 且没有音频" ⇒ 走「内容整理」。
    ///
    ///   语音键本来就有自己的三条判据（①报文中断恢复 / ②音频停 3 s / ③看门狗 20 s，
    ///   外加"按住 10 s 无音频"），不需要用"上一段的计时器"去放它 —— **永远不要动它**。
    fn release_all_held_with(&mut self, keep_voice: bool) {
        // 点按模式锁存中的语音键**不清**：它代表"用户按了一下、还没按第二下结束"，
        // 中途的会话结束（静音超时）不代表用户想结束。
        // ★ 2026-11-01：点按模式不再"锁存语音键"，所以这里也不保留它了。
        //   （原来保它是为了让用户按第二下，代价是松手后右 Alt 一直按着。）
        let keep_voice = keep_voice;
        if !keep_voice {
            // ★ 语音键走租约：收口即放（reconcile 会负责发报告与记账）。
            //   放在循环之前，循环里就不会再碰到 held[0x10]。
            self.voice_disarm("release_all_held");
        }
        // 移动引擎是独立线程，必须显式停 —— 否则"卡住的方向键"会让光标一直飘
        crate::mouse_move::clear_all();
        const VOICE: usize = 0x10;
        for u in 0..256usize {
            if keep_voice && u == VOICE {
                continue;   // 点按锁存中的语音键保留（等用户按第二下）
            }
            if self.held[u] {
                self.held[u] = false;
                self.deadline_ms[u] = 0;
                // 基线里"早已按着"的键，我们从没发过它的 DOWN ⇒ 这里也不发 UP，
                // 否则面板会出现"松开比按下多"（用户实测 17 下 / 28 松）。
                if self.seeded_held[u] {
                    self.seeded_held[u] = false;
                    continue;
                }
                self.emit_key(Some(u as u8), false);
            }
        }
        // 退出/会话结束时也要把系统侧的状态同步成"全部放开"，否则会留下卡键
        self.flush_inline();
    }

    fn emit_key(&self, usage: Option<u8>, down: bool) {
        let Some(u) = usage else { return };
        self.stats.lock().unwrap_or_else(|e| e.into_inner()).key_events += 1;
        // 附带 ETW 时间戳，应用侧可算"设备产生事件 → 我们收到"的真实延迟
        let msg = format!("KEY {u:02X} {} {}\n", if down { 1 } else { 0 }, self.last_ts);
        if let Some(k) = self.key_sink.as_ref() {
            let _ = k.cmd(msg.as_bytes());
        }
        if !diag::QUIET.load(std::sync::atomic::Ordering::Relaxed) {
            eprintln!("[KEY] usage=0x{u:02X}  {}", if down { "DOWN" } else { "UP" });
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
            .map(|t| t.elapsed() >= Duration::from_millis(idle_end_ms()))
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
            let mut s = self.stats.lock().unwrap_or_else(|e| e.into_inner());
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
                        // ★★ 2026-10-30 修正「总账变分账」：
                        //   原来这两行是**互斥**的 —— 静音期的跳号只进 seq_silence，
                        //   不再进 seq_lost。于是"少了 4 帧"这个**客观事实**在总账里消失了，
                        //   只剩"其中 4 帧归因于静音"。自检的断言（按注释语义期望 seq_lost=4）
                        //   因此永远红着 —— 而真正的问题是指标语义被悄悄改了。
                        //
                        //   现在：seq_lost = 总缺失（不论原因）；seq_silence = 其中静音造成的。
                        //   真丢 = seq_lost - seq_silence。两个数一起看才有意义。
                        s.seq_lost += n - 1;
                        if gap_ms + 150 >= expected_ms {
                            s.seq_silence += n - 1;
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
            let mut during = 0u64;
            {
                let mut s = self.stats.lock().unwrap_or_else(|e| e.into_inner());
                if gap > s.max_gap_ms { s.max_gap_ms = gap; }
                if gap >= idle_end_ms() {
                    s.gaps += 1;
                    is_gap = true;
                    // ★ 空档期间链路层事件还在不在？——一句话分清"远端停发"与"事件流全停"
                    during = s.other.saturating_sub(self.other_at_frame);
                    if during > 0 { s.gaps_with_events += 1; } else { s.gaps_without_events += 1; }
                }
            }
            if is_gap {
                // ★ 新会话：先清掉上一句残留在缓冲里的尾巴（最多 200 ms）。
                //   不清的话新句会接在旧尾巴后面 ⇒ 豆包识别混乱（波形有、没字）。
                if let Some(c) = self.cable.as_mut() {
                    c.clear();
                }
                diag::log_async(&format!(
                    "音频空档 {gap} ms（>= IDLE_END_MS {}），期间链路层事件 {during} 个 ⇒ {}，会触发 END→CLEAR",
                    idle_end_ms(),
                    if during > 0 { "ETW 还在收，是远端停发音频" } else { "整个事件流都停了（Windows/驱动侧）" }
                ));
            }
        }
        // 记下"这一帧时刻的链路层事件计数"，下次空档时用它算期间还有没有事件
        {
            let s = self.stats.lock().unwrap_or_else(|e| e.into_inner());
            self.other_at_frame = s.other;
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
        self.last_frame_seen = Some(Instant::now());
        // ★ 按住语音键期间收到音频帧 ⇒ 记下"这次按住确实听到声音了"。
        //   判据②（音频停 3 秒）必须先有这个前提，否则"按下还没开口"会被误判成说完。
        if self.held[0x10] {
            self.voice_had_audio = true;
        }
            // 预热帧也是真实音频 → 说明用户确实开口了
            if self.held[0x10] {
                self.voice_had_audio = true;
            }
            return;
        }

        if let Some(w) = self.wav.as_mut() {
            for v in &s16 {
                let _ = w.write_sample(*v);
            }
        }

        // ★★ 2026-10-31：**会话管理必须独立于 udp**。
        //   原来整段（CLEAR + 上采样 + 发送）都包在 `if let Some(u) = self.udp` 里，
        //   而原生内核是 `cable-only`（没有 UDP 接收方）⇒ `in_session` 永远是 false
        //   ⇒ `tick()` 里的 END 检测和 **release_all_held 全部失效**（语音键会卡住）。
        if !self.in_session {
            self.in_session = true;
            self.have_prev = false;
            if let Some(u) = self.udp.as_mut() {
                u.cmd(b"CLEAR");
            }
            let n = {
                let mut st = self.stats.lock().unwrap_or_else(|e| e.into_inner());
                st.sessions += 1;
                st.sessions
            };
            diag::log_async("会话开始（CLEAR）");
            eprintln!("[会话] 第 {n} 段开始（上段空档触发新一轮音频）");
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

        // ① 本地声卡（原生内核：直接写 CABLE Input）
        //    ★ 空闲时流会被关掉（省 WASAPI 线程/句柄），这里按需重开。
        //      重开约 100~300 ms，在此期间**丢弃前几帧**是必要的取舍：
        //      "3 分钟没说话后第一句丢掉开头 0.2 秒" 比 "一直占着 4 个线程" 更划算。
        // ★ 流被空闲释放后要重开，但**绝不能在 ETW 回调线程里 open**
        //   （里面有 recv_timeout(8s)，会把 ETW 缓冲堵爆 ⇒ 大量丢事件）。
        //   这里只起一个后台线程去开，开好了由 tick 取回。
        if self.cable.is_none() && self.audio_reopen_rx.is_none() {
            let (tx, rx) = std::sync::mpsc::channel();
            std::thread::spawn(move || {
                let _ = tx.send(crate::cable_out::CableOut::open());
            });
            self.audio_reopen_rx = Some(rx);
            eprintln!("[音频] 空闲后首次使用 ⇒ 后台重开输出流（本帧及后续几帧丢弃）");
        }
        if let Some(c) = self.cable.as_mut() {
            c.push(&out);
        }
        // ② UDP（C# 版走这条；两者可同时开，用于对比）
        if let Some(u) = self.udp.as_mut() {
            if !u.pcm(&out) {
                self.stats.lock().unwrap_or_else(|e| e.into_inner()).udp_err += 1;
            }
        }

        self.samples16 += SAMPLES as u64;
        self.last_frame_at = Some(Instant::now());
        self.last_frame_seen = Some(Instant::now());
        // ★★ 语音租约的唯一"续租"点（2026-10-31）：
        //   每一帧音频都在证明"用户还在说"，于是把租约推后 T_PAUSE。
        //   **这是唯一能延长租约的事件**。
        //
        //   ⚠ 必须以 `held[0x10]`（= 会话中）为条件。写成 `lease != 0` 是错的：
        //     disarm 只是把 lease 收到 now（非零），于是**放开之后的音频尾巴**
        //     会把一个已经结束的会话又续上 —— 21:28 实测：
        //       21:28:36.092 放开（松手（空报文））
        //       21:28:36.118 tick 又把它按上（26 ms 的假按下，豆包看到一次短按）
        // ★★ 点按模式**不续租**：那里的"按住"只有 60 ms（一次模拟点按），
        //   若让音频帧续租，这一下点按会被拉成整段长按 —— 豆包看到的就不是"点两下"了。
        let tap_mode_now = crate::settings::TRIGGER_IDX.load(Ordering::Relaxed) == 1;
        if self.held[0x10] && !tap_mode_now {
            let now = diag::now_ms();
            // ★ 用 renew 而不是直接 now+T_PAUSE：音频帧可能乱序/迟到，
            //   直接覆盖会把租约改短（表现为"说着说着被放开"）。见 voice::renew 的说明。
            let prev = VOICE_LEASE_MS.load(Ordering::Relaxed);
            VOICE_LEASE_MS.store(
                iprc1000_native::voice::renew(prev, now, T_PAUSE_MS),
                Ordering::Relaxed,
            );
        }
        if self.held[0x10] {
            self.voice_had_audio = true;   // 仅诊断用：放开时写进日志
        }
        self.stats.lock().unwrap_or_else(|e| e.into_inner()).last_frame_ms = diag::now_ms();
    }

    /// 空闲检测：超过 IDLE_END_MS 没有帧就发 END，让 router 关掉 CABLE 流。
    ///
    /// ⚠ 它是**持有 bridge 互斥体**被调的（`bridge.lock().unwrap_or_else(|e| e.into_inner()).tick()`），
    /// 而 ETW 回调要抢同一个锁 —— 所以这里**绝不能做文件 I/O**：一次慢盘就等于
    /// 把回调卡住，ETW 缓冲跟着溢出。文案一律走 `log_async`。
    fn tick(&mut self) {
        // ★ 2026-10-31：**先跑按住超时兜底**，再判会话 —— 这个检查不能依赖 `in_session`
        //   （先按方向键、一个音频帧都没有时 in_session 一直是 false，老写法会直接 return）。
        // 语音键松手的三条判据（按可靠性排序）：
        //   ① 报文里连续 3 帧没有语音键（在 on_report 里判）
        //   ② **音频流停了 3 秒**且已按住超过 2 秒（下面这条）—— 比"报文中断"可靠，
        //      因为按住说话时音频一定在传，而 HID 报文在按住期间根本不发
        //   ③ 看门狗 15 秒硬上限
        // ★★ 语音键由**租约**驱动（2026-10-31 结构替换）：
        //   没有事件也会到期 —— 这是"事件驱动"做不到的部分，必须由这个 50 ms 的节拍兜住。
        //   原来这里放的是判据②（音频停 3 s），现在它只是"续租的副作用"，不再单独判断。
        // ★★ 点按模式的"补一次点击"：由这个 50 ms 的节拍驱动三段时序
        //   （松手等待 → 按下 → 放开）。放在 reconcile 之前，保证同一拍里
        //   "先做补点击的动作、再统一对账"，避免出现两帧相反的指令。
        {
            let now = diag::now_ms();
            use iprc1000_native::voice::{TapAction, TapFollowUp};
            match self.tap_followup.tick(now) {
                Some(TapAction::Press) => {
                    eprintln!("[点按] 松手后那一下 ⇒ 按下右Alt");
                    self.voice_arm(iprc1000_native::voice::TAP_HOLD_MS);
                }
                Some(TapAction::Release) => {
                    eprintln!("[点按] ⇒ 弹起右Alt（这一段结束）");
                    self.voice_disarm("点按结束");
                }
                None => {}
            }
            let _ = TapFollowUp::None;   // 保持 use 不产生告警
        }

        // ★★ 点按模式的静默兜底：第一下发出去之后，如果音频停了很久还没等到"松手"，
        //   就判松手报文丢了，自动补第二下 —— 否则豆包会一直停在录音状态。
        //   判据要两个条件同时成立：
        //     ① 这次按压之后**确实来过音频**（last_frame_ms > tap_started_ms），
        //        否则"刚按下还没开口"也会被误判成说完；
        //     ② 距最后一帧音频已超过 TAP_AUTO_END_SILENCE_MS。
        if crate::settings::TRIGGER_IDX.load(Ordering::Relaxed) == 1 && self.tap_awaiting {
            let now = diag::now_ms();
            let last = self
                .stats
                .lock()
                .map(|s| s.last_frame_ms)
                .unwrap_or(0);
            if last > self.tap_started_ms
                && now.saturating_sub(last) > iprc1000_native::voice::TAP_AUTO_END_SILENCE_MS
            {
                eprintln!(
                    "[点按] 音频已静默 {} ms 仍无松手报文 ⇒ 判定松手丢失，自动补第二下",
                    now.saturating_sub(last)
                );
                self.tap_awaiting = false;
                self.tap_followup = iprc1000_native::voice::TapFollowUp::schedule(now);
            }
        }

        // ★★ 30 秒无音频强制收口（用户已定：要）。
        //   为什么必须有：遥控器按住期间**一条报文都不发**，如果松手那条 UP 在 ETW 里丢了
        //   （缓冲溢出/链路抖动/设备刚好重连），我们就会一直认为"还按着"。
        //   判据：按住超过 30 秒，且这一整段**一帧音频都没收到**。
        //   —— 真人说话不可能 30 秒不出一点声音；不出声只可能是断链或松手丢了。
        if self.held[0x10] && !self.voice_had_audio && self.voice_pressed_at_ms != 0 {
            let held = diag::now_ms().saturating_sub(self.voice_pressed_at_ms);
            if held > 30_000 {
                eprintln!("[语音 {}] 按住 {held} ms 且全程无音频 ⇒ 判定松手丢失，强制收口", hms_ms());
                self.voice_disarm("30 秒无音频");
            }
        }

        self.reconcile_voice("tick");
        self.check_audio_health();
        self.poll_ralt_check();
        self.heal_injector();
        self.poll_audio_reopen();
        self.release_audio_if_idle();
        self.watchdog_held();
        if !self.in_session {
            return;
        }
        if let Some(t) = self.last_frame_at {
            let end_after = idle_end_ms() + TAIL_MS.load(Ordering::Relaxed);
            if t.elapsed() >= Duration::from_millis(end_after) {
                // ★★ 语音键刚刚按下 ⇒ 这是**新的一段开始**，不能把上一段收尾。
                //   原因见 `release_all_held_with` 的说明：遥控器起流有 300~500 ms 固件延迟，
                //   这个计时器经常在新按压之后才到期；此时若发 END+释放，就变成"按下 0.5 秒
                //   被放掉 ⇒ 豆包走内容整理"（真机 20 次里 3 次）。
                //   推迟一整轮即可：音频一到 `last_frame_at` 就会刷新，条件自然失效。
                const VOICE_FRESH_MS: u128 = 1_500;
                let voice_held_ms: u128 = if self.held[0x10] && self.voice_pressed_at_ms != 0 {
                    diag::now_ms().saturating_sub(self.voice_pressed_at_ms) as u128
                } else {
                    u128::MAX
                };
                if self.held[0x10] && voice_held_ms < VOICE_FRESH_MS {
                    if !diag::QUIET.load(Ordering::Relaxed) {
                        eprintln!(
                            "[会话] 静默 {} ms 本应结束，但语音键刚按下 {} ms ⇒ 推迟结束（否则会被当成短按）",
                            t.elapsed().as_millis(),
                            voice_held_ms
                        );
                    }
                    return;
                }
                if let Some(u) = self.udp.as_ref() {
                    u.cmd(b"END");
                }
                // ★ 会话结束：把缓冲里的残余音频清掉。
                //   不清的话，残余（最多 200ms）会在下一句开始时被一起推给豆包，
                //   表现为"上一句的尾巴 + 新句子"⇒ 识别混乱。
                if let Some(c) = self.cable.as_mut() {
                    c.clear();
                }
                // 会话断开时可能还有键按着（链路掉/遥控器走远）—— 必须放掉，
                // 否则上位机以为键一直按着（卡键）。
                // ⚠ 但**语音键不放**：它有自己的三条判据，放掉它正是"内容整理"的根因。
                self.release_all_held_with(true);
                self.in_session = false;
                let secs = {
                    let st = self.stats.lock().unwrap_or_else(|e| e.into_inner());
                    st.sessions
                };
                diag::log_async(&format!(
                    "会话结束（END）—— 已静默 {} ms",
                    t.elapsed().as_millis()
                ));
                eprintln!(
                    "[会话] 第 {secs} 段结束（静默 {} ms，缓冲已清空）",
                    t.elapsed().as_millis()
                );
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
    /// `--idle-end-ms`：音频静默多久算会话结束（默认 600）。
    idle_end_ms: u64,
    /// `--tray`：显示系统托盘（原生内核常驻用）
    tray: bool,
    /// `--cable`：音频**在本进程内直接写 CABLE Input**（不经 UDP→C#），原生内核用。
    cable: bool,
    /// `--inline`：按键**在本进程内直接注入**（WinUHid），不发 UDP。
    /// 原生内核第 0 步的验证开关；同时开着 C# 版会双注入，测试时必须停掉 C#。
    inline: bool,
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
        idle_end_ms: 600,
        tray: false,
        cable: false,
        inline: false,
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
            "--inline" => { out.inline = true; consumed = 1; }
            "--cable" => { out.cable = true; consumed = 1; }
            "--tray" => { out.tray = true; consumed = 1; }
            "--no-key-udp" => {
                out.key_udp = Some(String::new());
                consumed = 1;
            }
            "--hp" => out.hp = val.and_then(|s| s.parse().ok()).unwrap_or(0.0),
            "--seconds" => out.seconds = val.and_then(|s| s.parse().ok()).unwrap_or(30),
            "--idle-end-ms" => out.idle_end_ms = val.and_then(|s| s.parse().ok()).unwrap_or(600),
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
            eprintln!("[i] 同时写出 WAV: {path}");
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
/// 单实例守卫：**必须在 main 最开头调用**。
///
/// 不启用它的后果（注释里原来就写了，但一直没接上）：
/// 第二个实例启动时会无条件执行 `logman stop Iprc1000AudioBridge -ets`，
/// 把第一个实例的 ETW 会话停掉 —— 老进程从此收不到任何事件，
/// 表现为「进程活着、界面正常、但按键和语音全部没反应」，极难排查。
pub fn acquire_single_instance() -> bool {
    use std::os::windows::ffi::OsStrExt;
    #[link(name = "kernel32")]
    extern "system" {
        fn CreateMutexW(attr: *const std::ffi::c_void, owner: i32, name: *const u16) -> isize;
        fn GetLastError() -> u32;
        fn CloseHandle(h: isize) -> i32;
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
        if GetLastError() == ERROR_ALREADY_EXISTS {
            // ★★ 2026-10-31（真机：点「重启程序」后两个实例全没了）：
            //   **抢不到必须关句柄**。原来这里只 return false、把句柄泄漏掉，
            //   而那个句柄会让互斥体对象在**老进程退出后依然存活** ⇒ 谁再 CreateMutexW
            //   都拿到 ERROR_ALREADY_EXISTS ⇒ 重启后的新实例永远起不来。
            //   真机日志（20:53:24）：
            //     [!] 已经有一个 IPRC1000 在运行，本次启动退出。   ← 新实例
            //     [托盘] 用户点「重启程序」                       ← 老实例这时才退
            CloseHandle(h);
            return false;
        }
        // 拿到了：句柄故意泄漏，它要活到进程结束，才能一直占住这个互斥体
        true
    }
}

/// 带等待的单实例检查 —— **「重启程序」专用**。
///
/// 老实例收到重启请求后要先 `exit(0)`（代码里还有 300 ms 的 sleep），
/// 新实例如果立刻检查，必然撞上"已存在"。这里最多等 `ms` 毫秒轮询：
/// 一旦老实例真的退出（互斥体对象随之销毁），立刻就能拿到。
pub fn acquire_single_instance_wait(ms: u64) -> bool {
    let t0 = std::time::Instant::now();
    loop {
        if acquire_single_instance() {
            return true;
        }
        if t0.elapsed().as_millis() as u64 >= ms {
            return false;
        }
        std::thread::sleep(Duration::from_millis(150));
    }
}
#[cfg(not(target_os = "windows"))]
fn acquire_single_instance() -> bool {
    true
}
#[cfg(not(target_os = "windows"))]
pub fn acquire_single_instance_wait(_ms: u64) -> bool {
    true
}

/// 启动业务（ETW 解码 + 按键注入 + 音频输出 + 看门狗）。
///
/// ★ 2026-10-31：由 Tauri 在**独立线程**里调用，因此这里是常驻循环、不返回。
///   注入与本地音频输出固定开启（不再有 `--inline` / `--cable` 开关），
///   托盘交给 Tauri 自带的那套（本文件不再创建 Win32 托盘）。
pub fn run_backend() {
    let mut args = parse_args();
    args.inline = true;
    args.cable = true;
    args.seconds = u64::MAX / 4;      // 常驻
    args.tray = false;
    // 会话判定阈值：用户可在界面「高级设置」里调（200~3000 ms），这里只做钳制。
    IDLE_END_MS.store(args.idle_end_ms.clamp(200, 3000), Ordering::Relaxed);
    let stats = Arc::new(Mutex::new(Stats::default()));

    // ★ 2026-10-31（Tauri 版）：**去掉单实例守卫**。
    //   原来它用命名互斥体判断，但失败分支的句柄没关 ⇒ 第一次"检测到已存在"就自己占住了
    //   互斥体，之后每个新实例都被自己挡住（表现：日志说"已有桥在运行"，系统里却一个业务
    //   进程都没有）。Tauri 宿主本身就是单窗口应用，不需要这层守卫。

    let udp = match args.udp.as_deref() {
        Some(addr) => match UdpSink::new(addr) {
            Ok(u) => {
                eprintln!("[i] UDP → {addr}（协议 CLEAR / 48k PCM / END）");
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
                eprintln!("[i] 按键事件 → {key_addr}（格式: \"KEY <usage> <1|0>\"）");
                Some(k)
            }
            Err(e) => {
                eprintln!("[!] 按键 UDP 初始化失败: {e}");
                None
            }
        }
    };

    let wav = args.wav.as_deref().and_then(|p| make_wav(p, args.rate));

    // ★ 2026-10-30（原生内核第 0 步）：`--inline` 时按键**在本进程内直通注入**。
    //   成功就把按键 UDP 关掉 —— 否则 C# 版和本进程会同时注入，一次按键变两次。
    //   失败则原样回退到 UDP（不改变旧行为）。
    let (inline, key_sink) = if args.inline {
        // ★ 2026-10-31：**重试**。WinUHidCreateDevice 常见失败原因是"上一个进程的虚拟键盘
        //   设备还没被驱动销毁"（强杀/快速重启时尤其明显），过几百毫秒自己就好了。
        //   C# 版有后台自愈重试，原生版必须也有 —— 否则一次失败就永久退化成"不注入"。
        let mut inj = None;
        let mut last_err = String::new();
        for i in 0..6 {
            match crate::winuhid::Injector::open() {
                Ok(j) => { inj = Some(j); break; }
                Err(e) => {
                    last_err = e;
                    if i < 5 {
                        eprintln!("[!] 虚拟键盘创建失败（第 {} 次）：{} —— {} ms 后重试", i + 1, last_err, 400 * (i + 1));
                        std::thread::sleep(Duration::from_millis(400 * (i + 1) as u64));
                    }
                }
            }
        }
        match inj {
            Some(j) => {
                eprintln!("[i] 原生直通：按键由本进程注入（WinUHid），按键 UDP 已关闭");
                (Some(j), None)
            }
            None => {
                eprintln!("[!] 虚拟键盘重试 6 次仍失败：{last_err} —— 按键暂不注入。**设备槽位被上次异常退出的进程占着，必须重启电脑才能释放**\
（重启前按遥控器不会注入，且可能残留某个键按住）");
                (None, key_sink)
            }
        }
    } else {
        (None, key_sink)
    };

    // ★ 2026-10-31：`--cable` = 原生音频输出（本进程直接写 CABLE Input）
    let cable = if args.cable {
        match crate::cable_out::CableOut::open() {
            Ok(c) => {
                eprintln!("[i] 原生音频输出：{}（48000 Hz / {} 声道）", c.name, c.channels);
                Some(c)
            }
            Err(e) => {
                eprintln!("[!] 打开 CABLE Input 失败：{e} —— 音频仍走 UDP");
                None
            }
        }
    } else {
        None
    };

    let bridge = Arc::new(Mutex::new(Bridge::new(
        args.hp, udp, wav, key_sink, Arc::clone(&stats), inline, cable,
    )));

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
            // 每 30 秒记一次自身资源占用：内存涨/句柄涨/线程涨 = 有泄漏，
            // 这是唯一能在"用户正常使用"时观察长期趋势的办法（外部工具测不到）。
            if n % 15 == 0 {
                match crate::self_stats() {
                    // 用同步 log（log_async 是入内存队列，靠另外的地方 drain；
                    // 实测在原生模式下没有周期性 drain，异步写会一直积压不落盘）
                    Some((rss, handles, threads)) => diag::log(&format!(
                        "[资源] RSS {rss} MB · 句柄 {handles} · 线程 {threads} · 运行 {} 秒 · 锁等待max {} µs（>5ms {} 次 / 共 {} 次）· 热路径等待max {} µs（>5ms {} 次 / 共 {} 次）",
                        n * 2,
                        LOCK_WAIT_MAX_US.load(Ordering::Relaxed),
                        LOCK_WAIT_SLOW.load(Ordering::Relaxed),
                        LOCK_WAIT_N.load(Ordering::Relaxed),
                        HOT_LOCK_WAIT_MAX_US.load(Ordering::Relaxed),
                        HOT_LOCK_SLOW.load(Ordering::Relaxed),
                        HOT_LOCK_N.load(Ordering::Relaxed)
                    )),
                    // 打出来而不是静默：否则"统计没出现"分不清是代码没跑还是 API 失败
                    None => diag::log("[!] 资源统计不可用（self_stats 返回 None）"),
                }
            }
            let (json, stat_line, blocked) = {
                // ★★ 锁序铁律：**永远先 br 后 st**（热路径就是 br→st）。
                //   这里原来是 st→br，与热路径相反 ⇒ 经典锁序反转：
                //     热路径持有 br、等 st；统计线程持有 st、等 br ⇒ 双方永久互等。
                //   症状是"进程活着但界面/按键全停"，而且取决于两线程时序、极难复现。
                //   2026-11-01 修正：改成 br→st，与热路径同序（st 的获取移到下面）。
                // ★★ 2026-11-01（#3 热路径无锁化的**第一步**：先测量，不猜）：
                //   统计线程每 2 秒抢一次同一把桥锁，测量"从想拿到真拿到"的等待时间。
                //   这个数就是热路径被 UI/统计/录制挡住的程度 —— 一旦出现 >20ms 的等待，
                //   就证明"单锁串行化"确实在顶住 ETW 回调（缓冲溢出→丢事件的根源），
                //   无锁化才有依据、也才有验收标准。
                let lock_t0 = Instant::now();
                let Ok(g) = br.lock() else { continue };
                let Ok(s) = st.lock() else { continue };   // ★ br 之后再拿 st
                {
                    let wait_us = lock_t0.elapsed().as_micros() as u64;
                    let prev = LOCK_WAIT_MAX_US.load(Ordering::Relaxed);
                    if wait_us > prev {
                        LOCK_WAIT_MAX_US.store(wait_us, Ordering::Relaxed);
                    }
                    if wait_us >= 5_000 {
                        LOCK_WAIT_SLOW.store(LOCK_WAIT_SLOW.load(Ordering::Relaxed) + 1, Ordering::Relaxed);
                    }
                    LOCK_WAIT_N.store(LOCK_WAIT_N.load(Ordering::Relaxed) + 1, Ordering::Relaxed);
                }
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
                        "\"gapWithEvents\":{},\"gapNoEvents\":{},\"lastEventMs\":{},",
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
                    s.gaps_with_events,
                    s.gaps_without_events,
                    s.last_event_ms,
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
                    let s2 = st.lock().unwrap_or_else(|e| e.into_inner());
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
            // ★ 用户关掉日志 ⇒ 统计文件也不写（它每 2 秒一次，是持续 IO）
            if !crate::logs_on() {
                prev_blocked = blocked;
                continue;
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
        eprintln!("[i] 实时 ETW 模式（需要管理员权限）");
    }
    if args.hp > 0.0 {
        eprintln!("[i] 高通 {:.0} Hz", args.hp);
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
                // ★☆ panic 防护：这是从 ETW 消费者线程**穿过 FFI 边界**调进来的回调，
                //   Rust 的 panic 一旦越过这条边界就是 UB（可能直接崩进程，且没有任何日志）。
                //   实测过多起"进程突然消失、run_native 都没返回"——这里兜住最保险。
                let r = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
                // ★ 先记"消费者被调用了"（在任何解析之前）——
                //   这个数冻结 = ETW 回调**根本不再被调用**（消费者线程死了），
                //   而不是"事件到了但解析不出来"。两者要分清，见 last_event_ms。
                {
                    let now_unix = std::time::SystemTime::now()
                        .duration_since(std::time::UNIX_EPOCH)
                        .map(|d| d.as_millis() as u64).unwrap_or(0);
                    let mut s = stats_cb.lock().unwrap_or_else(|e| e.into_inner());
                    s.last_event_ms = now_unix;
                }
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
                    // ★★ 2026-11-01（#3 的关键指标）：这里测的是**热路径自己的锁等待**。
                    //   统计线程那侧测出来恒为 0（它 2 秒才抢一次，不说明问题）；
                    //   真正该问的是"ETW 回调有没有被 UI/统计/录制挡住"——
                    //   一旦这里出现 >5 ms，就说明单锁确实在顶住事件流（丢事件的根源），
                    //   无锁化才有依据；否则这项重构不值得它的风险。
                    let hot_t0 = Instant::now();
                    let mut g = bridge_cb.lock().unwrap_or_else(|e| e.into_inner());
                    {
                        let w = hot_t0.elapsed().as_micros() as u64;
                        if w > HOT_LOCK_WAIT_MAX_US.load(Ordering::Relaxed) {
                            HOT_LOCK_WAIT_MAX_US.store(w, Ordering::Relaxed);
                        }
                        if w >= 5_000 {
                            HOT_LOCK_SLOW.store(HOT_LOCK_SLOW.load(Ordering::Relaxed) + 1, Ordering::Relaxed);
                        }
                        HOT_LOCK_N.store(HOT_LOCK_N.load(Ordering::Relaxed) + 1, Ordering::Relaxed);
                    }
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
                }));
                if r.is_err() {
                    // 只记一次类型，避免刷屏；真正的排查靠 panic hook 打的栈
                    static ONCE: std::sync::atomic::AtomicBool = std::sync::atomic::AtomicBool::new(false);
                    if !ONCE.swap(true, Ordering::Relaxed) {
                        eprintln!("[!] ETW 回调内发生 panic（已拦截，未崩溃；后续同类不再重复提示）");
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

    let _trace = match UserTrace::new()
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
        eprintln!("[i] 填缓冲自检：EventWrite rc={rc}（0=成功）");
    }
    if !feed_enabled {
        eprintln!("[i] 填缓冲已禁用（--no-feed，用于 A/B 对比）");
    } else {
        match feed.as_ref() {
            Some(_) => eprintln!(
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

    // ★ 把 bridge 注册到全局，供 Tauri 的命令（getState / setVoiceTiming …）访问
    let _ = SHARED.set(Arc::clone(&bridge));

    let mut applied_preset = u8::MAX;
    loop {
        // ★ 节拍按需：遥控器 30 秒内有事件（可能随时按键/说话）→ 50 ms；
        //   休眠/不在线 → 150 ms。看门狗最严的阈值是 15 s，150 ms 完全够。
        //   这一步不影响按键延迟（按键由 ETW 事件驱动，不靠这个循环）。
        let idle = bridge.lock().map(|b| b.idle_age_ms()).unwrap_or(u64::MAX);
        let interval = if idle < 30_000 { 50 } else { 150 };
        std::thread::sleep(Duration::from_millis(interval));
        bridge.lock().unwrap_or_else(|e| e.into_inner()).tick();

        // 界面改的「语音时序」由 Tauri 命令写进 PRESET，这里落地
        let pre = PRESET.load(Ordering::Relaxed);
        if pre != applied_preset {
            applied_preset = pre;
            let ms = [400u64, 600, 900][(pre as usize).min(2)];
            IDLE_END_MS.store(ms, Ordering::Relaxed);
            eprintln!("[i] 语音时序档位 {pre} ⇒ 会话判定 {ms} ms");
        }
    }
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
    eprintln!(
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
        bridge.lock().unwrap_or_else(|e| e.into_inner()).handle_fragment(flag, dlen, &acl);
        n += 1;
        // ★ 回放也要**按时间轴判会话**：真实流里"松手 → 再按"之间隔着几百毫秒到几秒，
        //   桥会发 END 并结束会话；回放若不补这一步，整段回放会被当成**一个**会话，
        //   于是"跨会话的假跳号"这类问题在回放里永远复现不出来。
        //   （自检 `scenario_two_sessions` 就靠这条。）
        if dt >= idle_end_ms() as u32 {
            bridge.lock().unwrap_or_else(|e| e.into_inner()).tick();
        }
        if realtime {
            acc += Duration::from_millis(dt as u64);
            let got = t0.elapsed();
            if acc > got {
                std::thread::sleep(acc - got);
            }
        }
    }
    eprintln!("[i] 回放记录 {n} 条");
    // 收尾：先跑几轮 tick，再**显式 finish()** 把 END 发出去。
    // 以前只靠 tick 的 12×50ms=600ms 等空闲超时，而 IDLE_END_MS 从 250 提到 600 之后
    // 正好卡在边界上 —— END 可能发不出去，下游就一直保持会话不关。
    for _ in 0..6 {
        std::thread::sleep(Duration::from_millis(50));
        bridge.lock().unwrap_or_else(|e| e.into_inner()).tick();
    }
    bridge.lock().unwrap_or_else(|e| e.into_inner()).finish();
    diag::drain();      // 回放很短，统计线程（2 s 一轮）可能来不及落盘 —— 这里补一次
    report(&stats, &bridge);
}


/// 供 UI（Tauri 命令）读取的只读快照 —— 只暴露界面真正需要的几项。
/// 开始录键（清掉上一次的结果）。egui 版改用弹框内直接读键盘，这里保留给旧调用方。
#[allow(dead_code)]
pub fn begin_capture() {
    if let Ok(mut g) = captured().lock() {
        *g = None;
    }
    CAPTURING.store(true, std::sync::atomic::Ordering::Relaxed);
}

/// UI 用：非阻塞取录键结果（`None` = 还没按）
pub fn poll_capture() -> Option<Vec<u16>> {
    captured().lock().ok().and_then(|mut g| g.take())
}

/// UI 用：取消录键
pub fn cancel_capture() {
    CAPTURING.store(false, std::sync::atomic::Ordering::Relaxed);
    if let Ok(mut g) = captured().lock() {
        *g = None;
    }
}

/// UI 用：设置语音时序档位（会话判定 400/600/900 ms）
pub fn set_preset(i: u8) {
    let ms = [400u64, 600, 900][(i as usize).min(2)];
    IDLE_END_MS.store(ms, std::sync::atomic::Ordering::Relaxed);
}

/// 取录键结果：
///   · 录到了   → `{ok:true, vks:[...], label:"Ctrl+A"}`
///   · 还在等   → `{ok:false, capturing:true}`（前端每 100 ms 轮询一次）
///   · 没在录   → `{ok:false, capturing:false}`
#[allow(dead_code)]
pub fn take_capture() -> String {
    if let Ok(mut g) = captured().lock() {
        if let Some(vks) = g.take() {
            CAPTURING.store(false, std::sync::atomic::Ordering::Relaxed);
            let label: Vec<String> = vks
                .iter()
                .map(|vk| crate::keymap::vk_name(*vk))
                .collect();
            return serde_json::json!({
                "ok": true, "vks": vks, "label": label.join("+")
            })
            .to_string();
        }
    }
    let capturing = CAPTURING.load(std::sync::atomic::Ordering::Relaxed);
    serde_json::json!({ "ok": false, "capturing": capturing }).to_string()
}

/// 某个遥控器键是否绑到了宏（引用值 -1101..-1999 → 宏编号 = -1000 - 值）
///
/// 只在"按下"那一刻查一次，所以这里直接读 settings 的 map 是可以接受的
/// （不能放进 `vks_of` —— 那里每帧都问，clone 整个 map 会拖慢注入）。
fn macro_ref_of(usage: u8) -> Option<u32> {
    let (id, _) = crate::keymap::usage_label(usage);
    if id == "unknown" {
        return None;
    }
    let map = crate::settings::bindings();
    let v = map.get(id)?.trim().to_string();
    let n: i64 = v.parse().ok()?;
    if (-1999..=-1101).contains(&n) {
        // ⚠ 基准是 **-1100**（宏 1 → -1101），不是 -1000。
        //   用 -1000 算会把宏 1 解成 101，正好撞到文本槽的邻居区间。
        Some((-1100 - n) as u32)
    } else {
        None
    }
}

/// 录键状态（2026-10-31）：界面点「+ 按键」后进入捕获，按下遥控器任意键即录到。
///
/// 为什么用 ETW 而不是低级键盘钩子：原生内核本来就直读 BthPort ETW，
/// 遥控器的键天然从这里过；再装一个键盘钩子既多一套生命周期，又要处理"钩子线程
/// 必须有消息循环"的坑（C# 版为此踩过整个键盘失灵）。
/// 代价：只能录**遥控器上的键**，录键盘上的任意键请用屏幕键盘。
pub static CAPTURING: std::sync::atomic::AtomicBool = std::sync::atomic::AtomicBool::new(false);
static CAPTURED: std::sync::OnceLock<Mutex<Option<Vec<u16>>>> = std::sync::OnceLock::new();

fn captured() -> &'static Mutex<Option<Vec<u16>>> {
    CAPTURED.get_or_init(|| Mutex::new(None))
}

/// 方向键 → 移动引擎的槽位（上/下/左/右）
/// 某个键是否绑到了文本槽（-1001..-1010 → 槽 1..10）
fn text_slot_of(usage: u8) -> Option<u32> {
    let (id, _) = crate::keymap::usage_label(usage);
    if id == "unknown" {
        return None;
    }
    let map = crate::settings::bindings();
    let v = map.get(id)?.trim().to_string();
    let n: i64 = v.parse().ok()?;
    if (-1010..=-1001).contains(&n) {
        Some((-1000 - n) as u32)
    } else {
        None
    }
}

fn dir_slot(usage: u8) -> Option<usize> {
    match usage {
        0x52 => Some(0), // up
        0x51 => Some(1), // down
        0x50 => Some(2), // left
        0x4F => Some(3), // right
        _ => None,
    }
}

#[allow(dead_code)]  // 字段多，不同 UI 版本按需读
pub struct Snapshot {
    pub frames: u64,
    pub sessions: u64,
    pub inject_ok: bool,
    pub cable_ok: bool,
    pub cable_name: String,
    /// 最近按下的 usage（0 = 还没按过）
    pub last_key: u8,
    /// 最近按键距现在多少毫秒（u64::MAX = 从未按过）
    pub last_key_age_ms: u64,
    /// 最近一次 ETW 事件距现在多少毫秒（u64::MAX = 从未有过）
    pub last_event_age_ms: u64,
    /// 在线 = 30 秒内有过事件（遥控器会休眠，不能只看"曾经连过"）
    pub online: bool,
}

impl Bridge {
    pub fn snapshot(&self) -> Snapshot {
        let (frames, sessions) = {
            let s = self.stats.lock().unwrap_or_else(|e| e.into_inner());
            (s.frames, s.sessions)
        };
        let (last_key, last_key_ms, last_event_ms) = {
            let s = self.stats.lock().unwrap_or_else(|e| e.into_inner());
            (s.last_key_usage, s.last_key_ms, s.last_event_ms)
        };
        let now = diag::now_ms();
        let key_age = if last_key_ms == 0 { u64::MAX } else { now.saturating_sub(last_key_ms) };
        let evt_age = if last_event_ms == 0 { u64::MAX } else { now.saturating_sub(last_event_ms) };
        Snapshot {
            frames,
            sessions,
            inject_ok: self.inline.is_some(),
            cable_ok: self.cable.is_some(),
            cable_name: self.cable.as_ref().map(|c| c.name.clone()).unwrap_or_default(),
            last_key,
            last_key_age_ms: key_age,
            last_event_age_ms: evt_age,
            online: evt_age != u64::MAX && evt_age < 30_000,
        }
    }
}

fn report(stats: &Arc<Mutex<Stats>>, bridge: &Arc<Mutex<Bridge>>) {
    let st = stats.lock().unwrap_or_else(|e| e.into_inner());
    let mut g = bridge.lock().unwrap_or_else(|e| e.into_inner());
    // 原生音频输出（`--cable`）的收尾统计：积压剩多少、有没有因超上限丢样本
    if let Some(c) = g.cable.as_ref() {
        eprintln!(
            "[i] 原生音频输出 {}：写入 {} 字节，结束时积压 {} ms，累计丢最旧 {} 样本",
            c.name, c.bytes, c.backlog_ms(), c.dropped()
        );
    }
    let (sent, bytes) = g
        .udp
        .as_ref()
        .map(|u| (u.sent, u.pcm_bytes))
        .unwrap_or((0, 0));
    eprintln!(
        "\n[i] 音频帧 {}  被截断首片 {}  HID报告 {}  按键事件 {}  其他事件 {}",
        st.frames, st.truncated_first, st.hid_reports, st.key_events, st.other
    );
    eprintln!(
        "[i] 丢帧定位: 组帧失败(续片没到) {} · seq跳号 {} 次（跳过 {} 帧）· seq重复 {} \
         · 续片被截断 {} · 未识别分片 {} · 帧头异常 {} · 孤续片 {} · 怪长帧 {} · L2CAP信令 {}",
        st.lost_cont, st.seq_jumps, st.seq_lost, st.seq_dup,
        st.trunc_cont, st.odd_frag, st.hdr_odd, st.orphan_cont, st.short_pdu, st.sig_frag
    );
    if !st.cont_kinds.is_empty() {
        let mut k: Vec<String> = st.cont_kinds.iter().map(|(l, c)| format!("{l}B×{c}")).collect();
        k.sort();
        eprintln!("[i] 续片长度分布: {}", k.join(", "));
    }
    if !st.odd_sample.is_empty() {
        eprintln!("[i] 首条未识别分片前 8 字节: {}", st.odd_sample);
    }
    if !st.odd_kinds.is_empty() {
        let mut k: Vec<String> = st.odd_kinds.iter()
            .map(|(f, l, c)| format!("flag={f} dlen={l}×{c}")).collect();
        k.sort();
        eprintln!("[i] 未识别分片分布: {}", k.join(", "));
    }
    if st.lat_n > 0 {
        eprintln!(
            "[i] ETW 投递延迟（链路层事件实测）: 平均 {} ms  最大 {} ms  （{} 次采样）",
            st.lat_sum / st.lat_n, st.lat_max, st.lat_n
        );
    }
    eprintln!(
        "[i] 16k 样本 {} = {:.2}s    UDP 包 {}  共 {} 字节 @48k",
        g.samples16,
        g.samples16 as f64 / 16000.0,
        sent,
        bytes
    );
    if st.truncated_first > 0 && st.frames == 0 {
        eprintln!("[!] 只收到被截断的首片 → MaxEtwBytes 未生效（需重启），首片只有 8 字节头");
    }
    g.finish();
}

