#!/usr/bin/env python3
"""IPRC1000 音频修复：PLC 重建（好样本原样保留，只合成丢失的 164 个样本）。

## 一、帧结构（实测反推）
L2CAP 负载 653 B = 13 B 帧头 + 640 B = **320 个 16bit 样本 = 20.0 ms @16kHz**。
控制器把它拆成两个 HCI ACL 分片：
    首片 340 B —— BthPort 的 ETW 只记 8 字节头 → **整段丢失**
    续片 313 B —— 完整拿到 = 1 B 子块头 + 312 B = **156 个真实样本**
帧间隔实测 20.00 ms → 1271 帧 × 320 = 406 720 样本 = 25.42 s ✓ 与墙钟跨度 25.40 s 吻合。
**每帧 156 真 / 164 丢，丢样率 51.2%。**

## 二、两个听感问题与各自的根因
| 症状 | 根因 | 对策 |
|------|------|------|
| 语速快 2.05 倍 | 丢了 164/320 样本 | PLC 合成缺失样本，真实样本**不拉伸** |
| 「电流音」 | 填充用的基音周期**估错了** | 在 **300–2000 Hz 带通**信号上估基音 |

关于「电流音」的排查过程（避免重复踩坑）：
  * 早期版本用 WSOLA 把 156 真样本拉成 320 → 把 10 ms 内容重复播两遍 → 49 Hz 蜂鸣。**错误做法。**
  * 曾怀疑 <100 Hz 能量占 29% 是低频噪声，想高通削掉 —— **实测证伪**：
    安静片段 rms=23、最响片段 rms=21120，逐频段「语音/安静」比全都在 +52~+63 dB，
    说明**没有稳态噪声底，低频完全跟随语音** → 那是说话人自己的嗓音（F0≈103–139 Hz 的低音男声）。
    **高通会削掉人声基频，因此默认不做高通。**
  * 真凶：全带做归一化自相关时，低频人声使相关在最小 lag 处取到虚峰 → 估计器卡在 T=48
    （即 333 Hz）→ 填充每 3 ms 重复一次 → **333 Hz 蜂鸣 = 听到的「电流音」**。
    在 300–2000 Hz 带通上估计后，T 落在 115~155（F0 103~139 Hz），正常。

用法: python etw_plc.py <bth.xml> [输出.wav] [高通Hz=0]
"""
import os
import re
import sys
import wave

import numpy as np

XML = sys.argv[1] if len(sys.argv) > 1 else r"F:\vibe\bth2.xml"
OUT = sys.argv[2] if len(sys.argv) > 2 else r"F:\vibe\plc_16k.wav"
HP_FC = float(sys.argv[3]) if len(sys.argv) > 3 else 0.0   # 默认不做高通（低频是嗓音）

SR = 16000
FRAME = 320          # 每帧应有的样本数（20.0 ms @16k）
CHUNK = 156          # 每帧实际拿到的真实样本数
GAP = FRAME - CHUNK  # 需合成的样本数 = 164
PITCH_LO_HZ, PITCH_HI_HZ = 65.0, 330.0

# ───────────────────────── 读取 ─────────────────────────
d = open(XML, encoding="utf-8", errors="replace").read()
ev = re.compile(
    r'<Data Name="BIP_Type">(\d+)</Data>\s*'
    r'<Data Name="BIP_DataLen">\s*(\d+)</Data>\s*'
    r'<Data Name="BIP_Data">0x([0-9A-Fa-f]*)</Data>', re.S)

chunks = []
for m in ev.finditer(d):
    if int(m.group(1)) != 3 or int(m.group(2)) != 317:   # 只取完整续片
        continue
    b = bytes.fromhex(m.group(3))[5:]                   # 4B ACL 头 + 1B 子块头
    chunks.append(np.frombuffer(b[:len(b) // 2 * 2], dtype="<i2").astype(np.float64))

n = len(chunks)
raw = np.concatenate(chunks)
print(f"[i] 帧数 {n}  每帧真实 {CHUNK}  合成 {GAP}  丢样率 {GAP/FRAME*100:.1f}%")
print(f"[i] 输出 {n*FRAME} 样本 = {n*FRAME/SR:.2f}s   源 rms={np.sqrt(np.mean(raw**2)):.0f}")


# ───────────────────────── 滤波器 ─────────────────────────
def biquad(x, kind, f0, Q=0.707, sr=SR):
    w0 = 2 * np.pi * f0 / sr
    al = np.sin(w0) / (2 * Q)
    c = np.cos(w0)
    if kind == "bp":
        b0, b1, b2 = al, 0.0, -al
    elif kind == "hp":
        b0, b1, b2 = (1 + c) / 2, -(1 + c), (1 + c) / 2
    else:  # lp
        b0, b1, b2 = (1 - c) / 2, 1 - c, (1 - c) / 2
    a0, a1, a2 = 1 + al, -2 * c, 1 - al
    b0, b1, b2, a1, a2 = b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0
    y = np.zeros_like(x)
    x1 = x2 = y1 = y2 = 0.0
    for i, v in enumerate(x):
        o = b0 * v + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2
        x2, x1 = x1, v
        y2, y1 = y1, o
        y[i] = o
    return y


def bandpass(x, lo, hi):
    """级联 2×2 阶带通（用于基音估计）。"""
    return biquad(biquad(biquad(biquad(x, "bp", lo), "bp", lo), "lp", hi), "lp", hi)


def band_pct(y, sr=SR):
    N = 1
    while N * 2 <= len(y):
        N *= 2
    S = np.abs(np.fft.rfft(y[:N] * np.hanning(N)))
    f = np.fft.rfftfreq(N, 1 / sr)
    g = lambda a, b: S[(f >= a) & (f < b)].sum() / S.sum() * 100
    return g(0, 100), g(300, 3400), g(4000, 8000)


sig = biquad(biquad(raw, "hp", HP_FC), "hp", HP_FC) if HP_FC > 0 else raw
if HP_FC > 0:
    print(f"[i] 已高通 {HP_FC:.0f}Hz（注意：低频含人声基频，通常不应开启）")

# 基音估计专用通道：300–2000 Hz 带通，避免低频嗓音把相关峰拉到最小 lag
pitch_ch = bandpass(sig, 300.0, 2000.0)


# ─────────────────── 基音估计（在带通信道上） ───────────────────
def est_pitch(bp_hist):
    """在带通历史上做归一化自相关；返回 (周期T, 周期性R)。"""
    seg = bp_hist[-768:]
    lo, hi = int(SR / PITCH_HI_HZ), int(SR / PITCH_LO_HZ)
    if len(seg) < hi * 2:
        return None, 0.0
    s = seg - seg.mean()
    if np.dot(s, s) < 1e-9:
        return None, 0.0
    best_t, best_r = 0, -1.0
    for t in range(lo, hi):
        a, b = s[:-t], s[t:]
        den = np.sqrt(np.dot(a, a) * np.dot(b, b))
        r = np.dot(a, b) / den if den > 1e-9 else 0.0
        if r > best_r:
            best_r, best_t = r, t
    return (best_t or None), float(best_r)


# ─────────────────── PLC 重建 ───────────────────
def fill_periodic(hist, gap, T):
    """周期同步叠加：把一个基音周期按 T 步长无缝铺满 gap。
    平铺源必须正好是一个周期 `hist[-T:]`，这样 out[0] 恰好是 hist[-1] 的周期延拓。"""
    if not T or T <= 0 or len(hist) < T:
        return np.zeros(gap)
    rep = np.tile(hist[-T:], gap // T + 6)
    h = np.hanning(2 * T)
    acc = np.zeros(len(rep) + 2 * T)
    wsum = np.zeros_like(acc)
    pos = 0
    while pos < gap + T:
        m = min(2 * T, len(acc) - pos)
        acc[pos:pos + m] += rep[pos:pos + m] * h[:m]
        wsum[pos:pos + m] += h[:m]
        pos += T
    return acc[:gap] / np.maximum(wsum[:gap], 1e-6)


def fill_interp(hist, fut, gap):
    """清音/噪声：前后段交叉淡化插值。"""
    a = np.tile(hist[-max(gap, 64):], 2)[-gap:] if len(hist) else np.zeros(gap)
    b = np.tile(fut[:max(gap, 64)], 2)[:gap] if len(fut) else a.copy()
    w = np.linspace(1, 0, gap)
    return a * w + b * (1 - w)


good = [sig[i * CHUNK:(i + 1) * CHUNK] for i in range(n)]
g_bp = [pitch_ch[i * CHUNK:(i + 1) * CHUNK] for i in range(n)]

# 填充段的频谱配平开关：周期重复会抹掉非周期（噪声）成分，
# 使填充比真实段更"闷"（<300Hz 52% vs 真实 38%），每 20ms 在闷/亮之间摆动
# —— 这就是残余「电流音」。按每帧把填充的低/高频能量比配到真实段上。
TILT_MATCH = os.environ.get("TILT_MATCH", "1") != "0"


def _lp2(x, fc):
    return biquad(biquad(x, "lp", fc), "lp", fc)


def match_tilt(fill, ref):
    """把 fill 的低/高频能量比配到 ref 上，同时保持总能量不变。"""
    if len(ref) < 256 or len(fill) < 64:
        return fill
    lf_f = _lp2(fill, 500.0)
    hf_f = fill - lf_f
    lf_r = _lp2(ref[-1024:], 500.0)
    hf_r = ref[-1024:] - lf_r
    A = float(np.dot(lf_f, lf_f))
    B = float(np.dot(hf_f, hf_f))
    Ar = float(np.dot(lf_r, lf_r))
    Br = float(np.dot(hf_r, hf_r))
    if A < 1e-6 or B < 1e-6 or Br < 1e-6:
        return fill
    R = max(min(Ar / Br, 50.0), 0.02)      # 参考段的高低比，限幅防爆
    tot = A + B
    g_hf = np.sqrt(tot / (B * (R + 1.0)))
    g_lf = np.sqrt(R * tot / (A * (R + 1.0)))
    # 增益限幅，避免极端帧产生突变
    g_hf = min(max(g_hf, 0.35), 3.0)
    g_lf = min(max(g_lf, 0.35), 3.0)
    return lf_f * g_lf + hf_f * g_hf


out = np.zeros(n * FRAME)     # 全带（输出用）
obp = np.zeros(n * FRAME)     # 带通（仅用于逐帧基音估计）
n_per = n_int = 0
T_used = []

for i in range(n):
    base = i * FRAME
    out[base:base + CHUNK] = good[i]
    obp[base:base + CHUNK] = g_bp[i]
    if i == n - 1:
        break
    hbp = obp[max(0, base - 2048):base + CHUNK]
    hist = out[max(0, base - 2048):base + CHUNK]
    T, R = est_pitch(hbp)
    if T and R >= 0.30:
        g = fill_periodic(hist, GAP, T)
        gb = fill_periodic(hbp, GAP, T)
        n_per += 1
        T_used.append(T)
    else:
        g = fill_interp(hist, good[i + 1][:GAP], GAP)
        gb = fill_interp(hbp, g_bp[i + 1][:GAP], GAP)
        n_int += 1
    # 起点：线性延拓平滑接入（消除 T 的 1~2 样本误差造成的咔哒）
    k = 16
    if len(hist) > k and len(hbp) > k:
        for arr, h in ((g, hist), (gb, hbp)):
            tail = h[-k:]
            ext = tail[-1] + (tail[-1] - tail[0]) / k * np.arange(1, k + 1)
            w = np.linspace(0, 1, k)
            arr[:k] = ext * (1 - w) + arr[:k] * w
    # 终点：与下一帧真实样本交叉淡化
    xf = 24
    w = np.linspace(0, 1, xf)
    g[-xf:] = g[-xf:] * (1 - w) + good[i + 1][:xf] * w
    gb[-xf:] = gb[-xf:] * (1 - w) + g_bp[i + 1][:xf] * w
    # 频谱配平：消除「周期重复 → 偏闷 → 每 20ms 闷/亮交替」的电流音
    if TILT_MATCH and len(hist) >= 256:
        g = match_tilt(g, hist)
    out[base + CHUNK:base + FRAME] = g
    obp[base + CHUNK:base + FRAME] = gb

T_used = np.array(T_used) if T_used else np.array([0])
print(f"[i] 填充: 周期同步 {n_per} 帧 / 插值 {n_int} 帧 "
      f"({n_per/max(n_per+n_int,1)*100:.0f}% 有声)")
print(f"[i] 基音周期 T 中位 {np.median(T_used):.0f} → F0 中位 {SR/np.median(T_used):.0f} Hz"
      f"  [5%,95%] = {SR/np.percentile(T_used,95):.0f}~{SR/np.percentile(T_used,5):.0f} Hz")

out -= out.mean()
peak = float(np.max(np.abs(out))) or 1.0
out = np.clip(out * 0.70 * 32767.0 / peak, -32768, 32767).astype("<i2")

with wave.open(OUT, "wb") as w:
    w.setnchannels(1)
    w.setsampwidth(2)
    w.setframerate(SR)
    w.writeframes(out.tobytes())
print(f"[✓] 已写出 {OUT}  ({len(out)} 样本 = {len(out)/SR:.2f}s)")


# ─────────────────── 指标 ───────────────────
def report(name, y):
    s = y.astype(np.float64)
    rms = float(np.sqrt(np.mean(s * s))) or 1.0
    bd = [abs(s[i * FRAME + CHUNK] - s[i * FRAME + CHUNK - 1])
          for i in range(len(s) // FRAME - 1)]
    seam = float(np.mean(bd)) / rms
    lo, mid, hi = band_pct(s)
    # 333Hz 蜂鸣残留（旧 bug 的指纹）：300-360Hz 相对 200-300 / 360-450 平均
    N = 1
    while N * 2 <= len(s):
        N *= 2
    S = np.abs(np.fft.rfft(s[:N] * np.hanning(N)))
    f = np.fft.rfftfreq(N, 1 / SR)
    tone = S[(f >= 300) & (f <= 360)].mean() / max(
        (S[(f >= 200) & (f < 300)].mean() + S[(f > 360) & (f <= 450)].mean()) / 2, 1e-9)
    print(f"{name:18s} 接缝Δ/rms {seam:.3f}  <100Hz {lo:5.2f}%  "
          f"300-3400Hz {mid:5.2f}%  >4kHz {hi:5.2f}%  333Hz残留 {20*np.log10(tone):+5.1f}dB")


import os
print("\n对照:")
for nm, p in (("未修复(快2倍)", r"F:\vibe\repair_16k_raw_fast.wav"),
              ("WSOLA 旧版", r"F:\vibe\repair_16k.wav"),
              ("PLC + 带通估基音", OUT)):
    if os.path.exists(p):
        with wave.open(p) as w:
            report(nm, np.frombuffer(w.readframes(w.getnframes()), dtype="<i2"))
