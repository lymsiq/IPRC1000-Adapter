//! 音频调试录音（2026-11-06）：把音频落盘，用来判断"声音到底哪一步坏的"。
//!
//! ## 开关
//!
//! 环境变量 `IPRC1000_RECORD`：
//!   · 未设置 / `0`  → 不录音（默认，零开销）
//!   · `1`            → 录到 `%APPDATA%\IPRC1000 Adapter\`
//!   · 其它值         → 当作目录
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
//! ## 为什么不用 hound
//!
//! `hound::WavWriter` 必须显式 `finalize()` 才把长度写回头部，而本程序退出走的是
//! `std::process::exit`（不跑 Drop）⇒ 文件头永远是 0 长度，播放器打不开。
//! 这里改成**每帧回写一次长度**（20 ms 一次、两次 seek + 8 字节），
//! 代价可忽略，好处是**任何时刻强杀进程，文件都是完整可播放的**。

use std::fs::{File, OpenOptions};
use std::io::{Seek, SeekFrom, Write};
use std::path::PathBuf;

/// 录音目标目录；未启用返回 None
fn target_dir() -> Option<PathBuf> {
    let v = std::env::var("IPRC1000_RECORD").ok()?;
    if v.is_empty() || v == "0" {
        return None;
    }
    if v == "1" {
        let appdata = std::env::var("APPDATA").ok()?;
        return Some(PathBuf::from(appdata).join("IPRC1000 Adapter"));
    }
    Some(PathBuf::from(v))
}

pub struct Rec {
    f: File,
    /// 已写入的 PCM 字节数
    bytes: u32,
}

impl Rec {
    /// 按 tag（`16k` / `48k`）与采样率开一个 16-bit 单声道 WAV。
    /// 未启用时返回 None，调用方用 Option 直接跳过。
    pub fn open(tag: &str, rate: u32) -> Option<Self> {
        let dir = target_dir()?;
        std::fs::create_dir_all(&dir).ok()?;
        let stamp = {
            let now = std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map(|d| d.as_secs())
                .unwrap_or(0);
            let secs = now % 86400;
            format!("{:02}{:02}{:02}", secs / 3600, (secs % 3600) / 60, secs % 60)
        };
        let path = dir.join(format!("capture-{tag}-{stamp}.wav"));
        let mut f = OpenOptions::new()
            .create(true)
            .write(true)
            .truncate(true)
            .open(&path)
            .ok()?;
        Self::write_header(&mut f, rate, 1).ok()?;
        eprintln!("[录音] {tag} -> {}", path.display());
        Some(Self { f, bytes: 0 })
    }

    fn write_header(f: &mut File, rate: u32, channels: u16) -> std::io::Result<()> {
        let byte_rate = rate * channels as u32 * 2;
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

    /// 回写 RIFF / data 长度，保证文件随时可播
    fn patch_size(&mut self) -> std::io::Result<()> {
        let riff = self.bytes.saturating_add(36);
        self.f.seek(SeekFrom::Start(4))?;
        self.f.write_all(&riff.to_le_bytes())?;
        self.f.seek(SeekFrom::Start(40))?;
        self.f.write_all(&self.bytes.to_le_bytes())?;
        self.f.seek(SeekFrom::End(0))?;
        Ok(())
    }

    /// 写一段 16-bit 小端单声道 PCM（长度必须是偶数）
    pub fn write_pcm16le(&mut self, pcm: &[u8]) {
        if pcm.is_empty() {
            return;
        }
        let n = pcm.len() & !1;
        if self.f.write_all(&pcm[..n]).is_err() {
            return;
        }
        self.bytes = self.bytes.saturating_add(n as u32);
        let _ = self.patch_size();
    }

    /// 写一串 i16 样本（48k 那路用）
    pub fn write_samples(&mut self, s: &[i16]) {
        if s.is_empty() {
            return;
        }
        let mut buf = Vec::with_capacity(s.len() * 2);
        for v in s {
            buf.extend_from_slice(&v.to_le_bytes());
        }
        self.write_pcm16le(&buf);
    }
}
