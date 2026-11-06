//! 原生音频输出（2026-10-31）：把 **48 kHz / 16-bit / 单声道** PCM 实时写进 `CABLE Input`。
//!
//! 为什么需要它：现在的语音链路是「桥 → UDP → CableRouter(C#/NAudio) → CABLE Input」，
//! 桥只管解码、C# 管出声。原生内核要独立工作，这一段必须自己在 Rust 里做完。
//!
//! 依赖选择：这一版用 `cpal`（Windows 后端就是 WASAPI），不是手写 COM。
//! 目的是**先把链路跑通并量化**（能不能出声、占多少 CPU、起播多快）；
//! 若最终要省依赖，可以照着这里的接口换裸 `IAudioClient`，上层代码不用动。
//!
//! 关键设计（全部对齐 C# 的 `JitterBuffer` 经验，那些参数是真机调出来的）：
//!   · **起播阈值 40 ms**：攒够才出声，否则一上来就欠载断续；
//!   · **积压上限 200 ms**：超了丢最旧（宁可丢一点，也不让延迟无限涨）；
//!   · **数据不足补静音**：绝不能阻塞声卡回调（那是爆音的源头）。

use cpal::traits::{DeviceTrait, HostTrait, StreamTrait};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};

/// VB-CABLE 输出端点的匹配关键字：**只认「名字含 CABLE」**。
///
/// 不同机器上实名不一致，实测出现过三种：
/// `CABLE Input` / `CABLE Input (VB-Audio Virtual Cable)` / `VB-Audio Virtual Cable`。
/// 精确匹配 `CABLE Input` 会在第三种机器上直接判"没装"，把音频整段丢掉。
const TARGET_KEYWORD: &str = "CABLE";
const SR: usize = 48000;
/// 环形缓冲容量（毫秒）—— 只影响"最多能积压多少"，不直接等于延迟
const RING_MS: usize = 200;
/// 起播阈值（毫秒）：攒够这么多才输出，之前一律静音
const PREROLL_MS: usize = 40;

/// 单声道环形缓冲（样本数固定，head/len 推进）
struct Ring {
    buf: Vec<i16>,
    head: usize,
    len: usize,
    /// 因积压超上限而被丢掉的最旧样本数（诊断用）
    dropped: u64,
}

impl Ring {
    fn new() -> Self {
        Self { buf: vec![0; SR * RING_MS / 1000], head: 0, len: 0, dropped: 0 }
    }

    fn push_one(&mut self, v: i16) {
        if self.len == self.buf.len() {
            // 满了：丢最旧的（前进 head），保证"最近的声音一定能进来"
            self.head = (self.head + 1) % self.buf.len();
            self.len -= 1;
            self.dropped += 1;
        }
        let tail = (self.head + self.len) % self.buf.len();
        self.buf[tail] = v;
        self.len += 1;
    }

    /// 取 `n` 个样本写入 `out`（f32，-1.0~1.0）；不足的部分补 0
    fn pop_into(&mut self, out: &mut [f32], primed: bool) -> usize {
        let mut got = 0;
        for o in out.iter_mut() {
            if !primed || self.len == 0 {
                *o = 0.0;
                continue;
            }
            *o = self.buf[self.head] as f32 / 32768.0;
            self.head = (self.head + 1) % self.buf.len();
            self.len -= 1;
            got += 1;
        }
        got
    }

    fn ms(&self) -> usize {
        self.len * 1000 / SR
    }
}

/// 一个已打开的 CABLE 输出。
///
/// ★ 2026-10-31：`cpal::Stream` **不是 `Send`**（编译期实测：`*mut ()` cannot be sent
/// between threads），而我们要把它放进 `Arc<Mutex<Bridge>>` 共享状态。
/// 解法：**流由内部线程持有**，本结构只暴露环形缓冲与控制标志 —— 于是 CableOut 是 Send，
/// 而流在它自己的线程里创建、播放、退出，生命周期由 Drop 收尾。
pub struct CableOut {
    ring: Arc<Mutex<Ring>>,
    stop: Arc<AtomicBool>,
    handle: Option<std::thread::JoinHandle<()>>,
    pub name: String,
    pub channels: u16,
    /// 已写入的 PCM 字节数（诊断/统计用）
    pub bytes: u64,
    /// 调试录音：重采样后真正送进设备的 48 kHz 音频
    rec48: Option<crate::capture::Rec>,
}

impl Drop for CableOut {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::Relaxed);
        if let Some(h) = self.handle.take() {
            let _ = h.join();
        }
    }
}

impl CableOut {
    /// 找 `CABLE Input` 并打开共享模式输出流（在独立线程里持有）。
    pub fn open() -> Result<Self, String> {
        let ring = Arc::new(Mutex::new(Ring::new()));
        let stop = Arc::new(AtomicBool::new(false));
        let ring_t = Arc::clone(&ring);
        let stop_t = Arc::clone(&stop);
        let (tx, rx) = std::sync::mpsc::channel::<Result<(String, u16), String>>();

        let handle = std::thread::Builder::new()
            .name("cable-out".into())
            .spawn(move || {
                match build_stream(Arc::clone(&ring_t)) {
                    Ok((stream, name, channels)) => {
                        if let Err(e) = stream.play() {
                            let _ = tx.send(Err(format!("启动输出流失败：{e}")));
                            return;
                        }
                        let _ = tx.send(Ok((name, channels)));
                        // 保住 stream 直到被要求停止（它一 drop，回调立刻停）
                        while !stop_t.load(Ordering::Relaxed) {
                            std::thread::sleep(std::time::Duration::from_millis(50));
                        }
                        drop(stream);
                    }
                    Err(e) => {
                        let _ = tx.send(Err(e));
                    }
                }
            })
            .map_err(|e| format!("创建音频线程失败：{e}"))?;

        match rx.recv_timeout(std::time::Duration::from_secs(8)) {
            Ok(Ok((name, channels))) => Ok(Self {
                ring,
                stop,
                handle: Some(handle),
                name,
                channels,
                bytes: 0,
                rec48: crate::capture::Rec::open("48k", SR as u32),
            }),
            Ok(Err(e)) => Err(e),
            Err(_) => Err("打开 CABLE Input 超时（8 秒）".to_string()),
        }
    }

    /// 清空环形缓冲（**新会话开始时必须调**）。
    ///
    /// 为什么：上一句说完后，Ring 里可能还压着最多 200 ms 的尾巴。不清的话，
    /// 下一句的音频会接在旧尾巴后面，豆包听到的是「半句话 + 新句子」——
    /// 实测表现就是"有波形但识别不出字"（用户报告，约十几次出现一次）。
    pub fn clear(&mut self) {
        if let Ok(mut r) = self.ring.lock() {
            r.head = 0;
            r.len = 0;
        }
    }

    /// 写入一帧 **16 kHz** / 16-bit / 单声道 PCM（小端字节）。
    ///
    /// ★ 必须重采样到 48 kHz（2026-11-06 客户机实测）：
    ///   上游每帧只有 320 个样本（16 kHz / 20 ms），而输出流是 48 kHz。
    ///   原来直接 `push_one` 塞进去 ⇒ 320/48000 = 6.67 ms 当 20 ms 播，
    ///   **整段语音 3 倍速、音调变尖、时长剩 1/3**，表现为"声音很小、识别不出字"。
    ///   这里按 3:1 做线性插值上采样，每个输入样本补两个中间样本。
    pub fn push(&mut self, pcm: &[u8]) {
        const RATIO: i32 = (SR / 16000) as i32; // 3
        let mut buf: Vec<i16> = Vec::with_capacity(pcm.len() * 3 / 2 + 8);
        {
            let Ok(mut r) = self.ring.lock() else { return };
            let mut prev: i32 = 0;
            for c in pcm.chunks_exact(2) {
                let cur = i16::from_le_bytes([c[0], c[1]]) as i32;
                for k in 1..=RATIO {
                    let v = (prev + (cur - prev) * k / RATIO) as i16;
                    r.push_one(v);
                    buf.push(v);
                }
                prev = cur;
            }
        }
        self.bytes += pcm.len() as u64;
        // 调试录音：这一路才是真正送进 CABLE 的音频
        if let Some(rec) = self.rec48.as_mut() {
            rec.write_samples(&buf);
        }
    }

    /// 当前积压（毫秒）—— 用于界面"延迟"显示
    pub fn backlog_ms(&self) -> usize {
        self.ring.lock().map(|r| r.ms()).unwrap_or(0)
    }

    /// 累计被丢弃的样本数（积压超上限时丢最旧）
    pub fn dropped(&self) -> u64 {
        self.ring.lock().map(|r| r.dropped).unwrap_or(0)
    }
}

/// 在音频线程里真正打开设备并建流（返回 stream + 设备名 + 声道数）
fn build_stream(ring: Arc<Mutex<Ring>>) -> Result<(cpal::Stream, String, u16), String> {
    let host = cpal::default_host();
    let mut picked = None;
    let mut seen: Vec<String> = Vec::new();
    for d in host
        .output_devices()
        .map_err(|e| format!("枚举输出设备失败：{e}"))?
    {
        let n = d.name().unwrap_or_default();
        seen.push(n.clone());
        // ★ 名字放宽到「含 CABLE」：不同机器上端点实名不一致，
        //   `CABLE Input` / `CABLE Input (VB-Audio Virtual Cable)` /
        //   `VB-Audio Virtual Cable` 都出现过（客户机实测第三种）。
        //   只认精确的 `CABLE Input` 会直接判"没装"，把音频整段丢掉。
        if picked.is_none() && n.to_uppercase().contains(TARGET_KEYWORD) {
            picked = Some((d, n));
        }
    }
    let (device, name) = picked.ok_or_else(|| {
        format!(
            "没有找到 VB-CABLE 输出端点（按名字含 CABLE 匹配）。当前输出设备：{}",
            if seen.is_empty() {
                String::from("（枚举结果为空）")
            } else {
                seen.join(" | ")
            }
        )
    })?;

    let supported = device
        .default_output_config()
        .map_err(|e| format!("读设备默认格式失败：{e}"))?;
    let cfg = supported.config();
    if cfg.sample_rate.0 as usize != SR {
        // 我们的 PCM 固定 48k。VB-CABLE 默认就是 48k；不是的话说明用户改过设备格式，
        // 这里直接报错而不是偷偷重采样 —— 悄悄变调比报错更难查。
        return Err(format!(
            "CABLE Input 的格式是 {} Hz，本内核只发 48000 Hz —— 请在声音设置里改回 48 kHz",
            cfg.sample_rate.0
        ));
    }
    let channels = cfg.channels;
    if supported.sample_format() != cpal::SampleFormat::F32 {
        return Err(format!(
            "CABLE Input 的采样格式是 {:?}，本内核只支持 F32",
            supported.sample_format()
        ));
    }

    let ch = channels as usize;
    // ★ 回调里绝不加锁做重活：只做"取样本 + 转 f32 + 填声道"，
    //   锁内是 memcpy 级操作（C# 侧同样结论：回调一慢就爆音）。
    let mut mono = vec![0f32; 0];
    let stream = device
        .build_output_stream(
            &cfg,
            move |data: &mut [f32], _| {
                let frames = data.len() / ch;
                let mut r = match ring.lock() {
                    Ok(g) => g,
                    Err(_) => {
                        for v in data.iter_mut() { *v = 0.0; }
                        return;
                    }
                };
                // 起播：不够 PREROLL 就先吐静音，避免一上来就断续
                let primed = r.ms() >= PREROLL_MS;
                if mono.len() < frames {
                    mono.resize(frames, 0.0);
                }
                let slice = &mut mono[..frames];
                r.pop_into(slice, primed);
                for (i, v) in slice.iter().enumerate() {
                    for c in 0..ch {
                        data[i * ch + c] = *v;
                    }
                }
            },
            |e| eprintln!("[!] 音频回调错误：{e}"),
            None,
        )
        .map_err(|e| format!("打开输出流失败：{e}"))?;

    Ok((stream, name, channels))
}
