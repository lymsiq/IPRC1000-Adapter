//! 音频调试录音 —— **异步写盘版**（0.4.46 重构）
//!
//! ## 开关
//!
//! ① 配置文件 `recordAudio`（界面复选框，写 `native.json`）
//! ② 环境变量 `IPRC1000_RECORD`：`0` 强制关、其它值强制开（调试用，优先级更高）
//!
//! 目录固定在**程序目录下的 `wav\`**，跟着程序走，用户直接能翻到。
//!
//! ## 两个文件，缺一不可
//!
//! ```text
//! capture-16k-<HHMMSS>.wav   收到的原始音频（16 kHz）
//! capture-48k-<HHMMSS>.wav   重采样后真正写进 CABLE 的（48 kHz）
//! ```
//!
//! 判读：
//!   · 16k 正常、48k 变调      ⇒ 重采样坏了
//!   · 16k 本身就小/糊/断续     ⇒ 接收或解码坏了
//!   · 两个都正常却没声音       ⇒ CABLE 环节（设备没打开 / 声道写错）
//!
//! ## 为什么重构成「通道 + 后台线程」
//!
//! 旧实现直接在 `push_payload` 里同步写文件，三个问题叠在热路径上：
//!
//! ```text
//! ① push_payload 跑在 ETW 回调线程，里面做文件 I/O 会堵住回调
//!    ⇒ ETW 缓冲溢出 ⇒ 丢事件（stats 里的 seqLost/oddFrag 会涨）
//! ② 每次写完 seek 回文件头改长度（每帧 2 次 seek + 2 次 write，
//!    20 ms 一帧 = 每秒 100 次随机写）
//! ③ 以上全发生在持有 Bridge 锁的临界区里 ⇒ 拖慢 UI 读快照与按键注入
//! ```
//!
//! 现在：
//!   · `push_*` 只做一次 `try_send`（队列满就丢，**绝不阻塞回调**）
//!   · 写盘、回写长度、分卷全部在独立线程
//!   · 长度回写从"每帧"降到"每 2 秒一次"，退出时补最后一次
//!
//! ## 分卷
//!
//! 单文件超 `MAX_FILE` 自动换下一个。WAV 的长度字段是 u32，本来也放不下 >4 GB。

use std::fs::File;
use std::io::{BufWriter, Seek, SeekFrom, Write};
use std::path::PathBuf;
use std::sync::mpsc::{sync_channel, Receiver, SyncSender};
use std::time::{Duration, Instant};

/// 单文件上限（超过就分卷）
const MAX_FILE: u64 = 400 * 1024 * 1024;
/// 长度回写周期（不是每帧）
const PATCH_EVERY: Duration = Duration::from_secs(2);
/// 队列深度：128 块 ≈ 2.5 秒音频，够吸收写盘抖动
const QUEUE: usize = 128;

/// 录音目标目录；未启用返回 None
fn target_dir() -> Option<PathBuf> {
    let enabled = match std::env::var("IPRC1000_RECORD").ok().as_deref() {
        Some("0") => false,
        Some(_) => true,
        None => crate::settings::record_audio(),
    };
    if !enabled {
        return None;
    }
    let base = match std::env::current_exe()
        .ok()
        .and_then(|p| p.parent().map(|d| d.to_path_buf()))
    {
        Some(b) => b,
        None => {
            eprintln!("[录音] 取程序目录失败，无法确定 wav 目录");
            return None;
        }
    };
    Some(base.join("wav"))
}

/// 句柄本身很轻：只有一条发送端。写盘全在后台线程。
pub struct Rec {
    tx: SyncSender<Vec<u8>>,
}

/// 后台写盘线程持有的状态
struct Sink {
    f: BufWriter<File>,
    path: PathBuf,
    /// 已写入的 PCM 字节数
    bytes: u64,
}

impl Sink {
    fn header(&mut self, rate: u32, channels: u16) -> std::io::Result<()> {
        let byte_rate = rate * channels as u32 * 2;
        let f = &mut self.f;
        f.write_all(b"RIFF")?;
        f.write_all(&0u32.to_le_bytes())?; // 长度稍后回写
        f.write_all(b"WAVEfmt ")?;
        f.write_all(&16u32.to_le_bytes())?; // fmt chunk size
        f.write_all(&1u16.to_le_bytes())?; // PCM
        f.write_all(&channels.to_le_bytes())?;
        f.write_all(&rate.to_le_bytes())?;
        f.write_all(&byte_rate.to_le_bytes())?;
        f.write_all(&(channels * 2).to_le_bytes())?; // block align
        f.write_all(&16u16.to_le_bytes())?; // bits per sample
        f.write_all(b"data")?;
        f.write_all(&0u32.to_le_bytes())?; // 长度稍后回写
        Ok(())
    }

    /// 回写 RIFF / data 长度，保证文件随时可播（强杀进程也不影响）
    fn patch(&mut self) -> std::io::Result<()> {
        let riff = (self.bytes.saturating_add(36)).min(u32::MAX as u64) as u32;
        let data = self.bytes.min(u32::MAX as u64) as u32;
        self.f.seek(SeekFrom::Start(4))?;
        self.f.write_all(&riff.to_le_bytes())?;
        self.f.seek(SeekFrom::Start(40))?;
        self.f.write_all(&data.to_le_bytes())?;
        self.f.seek(SeekFrom::End(0))?;
        Ok(())
    }
}

fn stamp_now() -> String {
    let now = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_secs())
        .unwrap_or(0);
    let secs = now % 86400;
    format!("{:02}{:02}{:02}", secs / 3600, (secs % 3600) / 60, secs % 60)
}

fn open_sink(dir: &PathBuf, tag: &str, rate: u32) -> Option<Sink> {
    let path = dir.join(format!("capture-{tag}-{}.wav", stamp_now()));
    let f = match File::create(&path) {
        Ok(f) => f,
        Err(e) => {
            eprintln!("[录音] {tag} 打开文件失败 {}：{e}", path.display());
            return None;
        }
    };
    let mut s = Sink { f: BufWriter::with_capacity(64 * 1024, f), path, bytes: 0 };
    if let Err(e) = s.header(rate, 1) {
        eprintln!("[录音] {tag} 写 WAV 头失败：{e}");
        return None;
    }
    // ★ 立刻把 44 字节头刷到磁盘。
    //   否则 BufWriter 会把头留在缓冲区，没数据时永远不 flush ⇒ 文件是 0 字节，
    //   看起来像"录音没工作"（2026-11-06 客户机实测：两个 wav 都是 0 字节）。
    let _ = s.f.flush();
    eprintln!("[录音] {tag} -> {}", s.path.display());
    Some(s)
}

fn writer_loop(
    rx: Receiver<Vec<u8>>,
    mut sink: Sink,
    dir: PathBuf,
    tag: String,
    rate: u32,
) {
    let mut last_patch = Instant::now();
    let mut lost: u64 = 0;
    while let Ok(buf) = rx.recv() {
        if sink.f.write_all(&buf).is_err() {
            lost += 1;
            continue;
        }
        sink.bytes = sink.bytes.saturating_add(buf.len() as u64);

        if last_patch.elapsed() >= PATCH_EVERY {
            let _ = sink.patch();
            last_patch = Instant::now();
        }
        if sink.bytes >= MAX_FILE {
            let _ = sink.patch();
            match open_sink(&dir, &tag, rate) {
                Some(n) => {
                    eprintln!("[录音] {tag} 分卷 -> {}", n.path.display());
                    sink = n;
                    last_patch = Instant::now();
                }
                None => break,
            }
        }
    }
    let _ = sink.patch();
    let _ = sink.f.flush();
    if lost > 0 {
        eprintln!("[录音] {tag} 写入失败 {lost} 块（磁盘满/权限？）");
    }
}

impl Rec {
    /// 按 tag（`16k` / `48k`）与采样率开一路录音。
    ///
    /// 返回 `Some` 时**文件已建好并写过 WAV 头**（能反映真实可写性），
    /// 之后所有写盘都在后台线程；调用方拿到的是一个只含发送端的轻句柄。
    pub fn open(tag: &str, rate: u32) -> Option<Self> {
        let Some(dir) = target_dir() else {
            // 必须留痕：旧版 `.ok()?` 静默返回，配置明明是 true 也查不到原因
            eprintln!(
                "[录音] {tag} 未启用：配置 recordAudio={} 环境变量 IPRC1000_RECORD={:?}",
                crate::settings::record_audio(),
                std::env::var("IPRC1000_RECORD").ok()
            );
            return None;
        };
        if let Err(e) = std::fs::create_dir_all(&dir) {
            eprintln!("[录音] {tag} 建目录失败 {}：{e}", dir.display());
            return None;
        }
        let sink = open_sink(&dir, tag, rate)?;
        let (tx, rx) = sync_channel::<Vec<u8>>(QUEUE);
        let tag_s = tag.to_string();
        let dir_c = dir.clone();
        if let Err(e) = std::thread::Builder::new()
            .name(format!("rec-{tag}"))
            .spawn(move || writer_loop(rx, sink, dir_c, tag_s, rate))
        {
            eprintln!("[录音] {tag} 起线程失败：{e}");
            return None;
        }
        Some(Self { tx })
    }

    /// 投递一段 16-bit 小端单声道 PCM。**非阻塞**：队列满就丢，绝不拖累调用方。
    pub fn write_pcm16le(&self, pcm: &[u8]) {
        if pcm.is_empty() {
            return;
        }
        let n = pcm.len() & !1;
        let _ = self.tx.try_send(pcm[..n].to_vec());
    }

    /// 投递一串 i16 样本（48k 那路用）
    pub fn write_samples(&self, s: &[i16]) {
        if s.is_empty() {
            return;
        }
        let mut buf = Vec::with_capacity(s.len() * 2);
        for v in s {
            buf.extend_from_slice(&v.to_le_bytes());
        }
        let _ = self.tx.try_send(buf);
    }
}
