//! 原始帧导出（`IPRC1000_DUMP_RAW=<目录>`，2026-11-06）
//!
//! ## 为什么需要
//!
//! "音频不对"时，`capture-16k.wav` 只能证明"解出来是什么"，不能证明
//! "原始字节长什么样"。一旦怀疑**头长度/分片拼法**不对（换适配器后
//! CSR 与 Intel 的 ACL 分片方式不同），就必须拿到那份 653 字节的原始负载，
//! 才能离线试 `HDR=11/12/13/14/15` 去找正确偏移。
//!
//! ## 格式
//!
//! 无头、定长：每帧固定写 `FRAME_LEN`(653) 字节，按到达顺序拼接。
//! 离线时按 653 切分即可，第 N 帧就是 `data[N*653 .. (N+1)*653]`。
//!
//! 不写时间戳是故意的：定长才能用 `filesize / 653` 直接算出帧数，
//! 也不会因为时间戳字段而引入"这 8 字节算不算偏移"的歧义。
//!
//! ## 线程模型
//!
//! 调用方在 **ETW 回调线程**上，绝不能做文件 I/O。这里只做一次
//! `try_send`（队列满就丢并计数），写盘全在后台线程。

use std::io::Write;
use std::path::Path;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::Arc;

/// 帧长（与 `backend::FRAME_LEN` 一致；这里复制一份避免循环依赖）
const FRAME: usize = 653;

pub struct RawDump {
    tx: std::sync::mpsc::SyncSender<Vec<u8>>,
    lost: Arc<AtomicU64>,
}

impl RawDump {
    /// 环境变量 `IPRC1000_DUMP_RAW` 指向目录时启用；否则返回 `None`（零开销）。
    pub fn from_env() -> Option<Self> {
        let dir = std::env::var_os("IPRC1000_DUMP_RAW")?;
        let dir = std::path::PathBuf::from(dir);
        Self::open(&dir)
    }

    pub fn open(dir: &Path) -> Option<Self> {
        if std::fs::create_dir_all(dir).is_err() {
            eprintln!("[录音] 原始帧导出目录创建失败：{}", dir.display());
            return None;
        }
        let path = dir.join("raw-frames.bin");
        let f = std::fs::File::create(&path).ok()?;
        let (tx, rx) = std::sync::mpsc::sync_channel::<Vec<u8>>(256);
        let lost = Arc::new(AtomicU64::new(0));
        let lost_bg = Arc::clone(&lost);

        std::thread::Builder::new()
            .name("raw-dump".into())
            .spawn(move || {
                let mut w = std::io::BufWriter::with_capacity(64 * 1024, f);
                let mut n = 0u64;
                while let Ok(buf) = rx.recv() {
                    if w.write_all(buf.as_slice()).is_err() {
                        break;
                    }
                    n += 1;
                    // 每 64 帧（约 1.3 秒）落一次盘，避免崩溃时全丢
                    if n % 64 == 0 {
                        let _ = w.flush();
                    }
                }
                let _ = w.flush();
                let n = lost_bg.load(Ordering::Relaxed);
                if n > 0 {
                    eprintln!("[录音] ⚠ 原始帧队列满，丢弃 {n} 帧（诊断数据不完整）");
                }
            })
            .ok()?;

        eprintln!("[录音] 原始帧导出 -> {}", path.display());
        Some(Self { tx, lost })
    }

    /// 投递一帧原始负载（653 字节）。**非阻塞**。
    pub fn write(&self, p: &[u8]) {
        if p.len() != FRAME {
            return;
        }
        if self.tx.try_send(p.to_vec()).is_err() {
            self.lost.fetch_add(1, Ordering::Relaxed);
        }
    }
}
