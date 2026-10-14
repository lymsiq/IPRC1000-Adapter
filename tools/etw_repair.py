#!/usr/bin/env python3
"""把 ETW 抓到的 48% 音频「修复」成正确语速、正确音高的 WAV。

背景（已用原始 ETL 字节级证实）：
  遥控器每 20.4 ms 发一个 653 字节 L2CAP 音频帧，被控制器拆成两个 HCI ACL 分片：
    首片 dlen=344 / PB=10  —— BTHPORT 只记录 8 字节（ACL+L2CAP 头），340 字节负载丢失
    续片 dlen=313 / PB=01  —— 完整记录
  于是我们只拿到每帧尾部 313 字节（去掉 1 字节子报告头 = 156 个 16bit 样本），
  每 ~20 ms 一块 → 等效采样率 156/0.020 ≈ 7.8 kHz。
  直接按 16 kHz 播放 → 语速快 2.05 倍；丢块以 50 Hz 周期出现 → 「电流音」。

本脚本用 WSOLA（波形相似叠加，变速不变调）把 156 样本/块 拉伸到 ~320 样本/块，
使输出回到正确的 25.4 秒时长、正确音高；同时用互相关对齐消除块间硬跳变，
把「电流音」降为轻微的合成感。
"""
import re
import struct
import sys
import wave

import numpy as np

XML = sys.argv[1] if len(sys.argv) > 1 else r"F:\vibe\bth2.xml"
OUT = sys.argv[2] if len(sys.argv) > 2 else r"F:\vibe\repair_16k.wav"
SR = 16000

d = open(XML, encoding="utf-8", errors="replace").read()
ev = re.compile(
    r'SystemTime="([^"]+)".*?'
    r'<Data Name="BIP_Type">(\d+)</Data>\s*'
    r'<Data Name="BIP_DataLen">\s*(\d+)</Data>\s*'
    r'<Data Name="BIP_Data">0x([0-9A-Fa-f]*)</Data>', re.S)


def tsec(s):
    return (int(s[11:13]) * 3600 + int(s[14:16]) * 60 + int(s[17:19])
            + int(s[20:29]) / 1e9)


chunks, times = [], []
for m in ev.finditer(d):
    if int(m.group(2)) != 3 or int(m.group(3)) != 317:
        continue
    payload = bytes.fromhex(m.group(4))[4:]
    b = payload[1:]                      # 去掉 1 字节子报告头
    chunks.append(np.frombuffer(b[:len(b) // 2 * 2], dtype="<i2").astype(np.float32))
    times.append(tsec(m.group(1)))

n = len(chunks)
x = np.concatenate(chunks)
t0, t1 = times[0], times[-1]
span = t1 - t0
print(f"[i] 块数 {n}  每块 {len(chunks[0])} 样本  输入总样本 {len(x)} = {len(x)/SR:.2f}s @16k")
print(f"[i] 实际墙钟跨度 {span:.2f}s  →  等效采集率 {len(x)/span:.0f} Hz  →  需拉伸 {SR/(len(x)/span):.3f}×")

# 合成步长：用「墙钟跨度 / 块数」的平均值，而不用逐块时间戳。
# 原因：到达间隔抖动极大（3ms~25ms），逐块使用会把音频压成 3 倍 / 拉成 2.5 倍，产生明显颤动。
# L2CAP 帧结构上每帧覆盖 653 字节 = 326 个 16bit 样本（两段子报告各 163），
# 与「平均到达间隔 × 16k」相互印证。
Hs = int(round(span * SR / (n - 1)))
print(f"[i] 合成步长 {Hs} 样本 ({Hs/SR*1000:.2f} ms)  "
      f"[结构值 326 = {326/SR*1000:.2f} ms，偏差 {abs(Hs-326)/326*100:.1f}%]")
Hs_list = [Hs] * n

# ---- WSOLA ----
N = 512                       # 分析窗长
Ha = int(np.median([len(c) for c in chunks]))   # 分析步长 = 156
OV = N - Hs                   # 重叠长度
win = np.hanning(N).astype(np.float32)
delta = 128                   # 搜索半径

total_Hs = sum(Hs_list) + Hs
out = np.zeros(total_Hs + N + delta, dtype=np.float32)
wsum = np.zeros_like(out)

pos_in = 0
pos_out = 0
prev_tail = None
k = 0
while pos_in + N + delta < len(x) and pos_out + N < len(out):
    hs = Hs_list[k] if k < len(Hs_list) else Hs
    search_lo, search_hi = pos_in - delta, pos_in + delta
    best = pos_in
    if prev_tail is not None and OV > 8:
        # 与上一帧输出尾部做互相关，找最相似的起点
        best_score, best = -1e18, pos_in
        seg_end = min(search_hi + OV, len(x))
        for cand in range(max(0, search_lo), search_hi, 4):
            if cand + OV > seg_end:
                break
            a = x[cand:cand + OV]
            sc = float(np.dot(a, prev_tail))
            nrm = float(np.linalg.norm(a)) + 1e-6
            sc /= nrm
            if sc > best_score:
                best_score, best = sc, cand
    seg = x[best:best + N] * win
    out[pos_out:pos_out + N] += seg
    wsum[pos_out:pos_out + N] += win
    prev_tail = out[pos_out + hs:pos_out + hs + OV].copy()
    pos_out += hs
    pos_in += Ha
    k += 1

valid = wsum > 1e-3
out[valid] /= wsum[valid]
y = out[:pos_out].copy()

# 去直流 + 归一化到 -3dBFS
y -= float(np.mean(y))
peak = float(np.max(np.abs(y))) or 1.0
y *= 0.70 * 32767.0 / peak
y = np.clip(y, -32768, 32767).astype("<i2")

print(f"[✓] 输出 {len(y)} 样本 = {len(y)/SR:.2f}s  (原 {len(x)/SR:.2f}s)")
with wave.open(OUT, "wb") as w:
    w.setnchannels(1)
    w.setsampwidth(2)
    w.setframerate(SR)
    w.writeframes(y.tobytes())
print(f"[✓] 已写出 {OUT}")


def smooth(sig):
    s = sig.astype(np.float64)
    rms = float(np.sqrt(np.mean(s * s))) or 1.0
    return float(np.mean(np.abs(np.diff(s)))) / rms


# 同目录再写一份未修复版便于对比
raw = x - float(np.mean(x))
pk = float(np.max(np.abs(raw))) or 1.0
raw = np.clip(raw * 0.70 * 32767.0 / pk, -32768, 32767).astype("<i2")
cmp_path = OUT.replace(".wav", "_raw_fast.wav")
with wave.open(cmp_path, "wb") as w:
    w.setnchannels(1)
    w.setsampwidth(2)
    w.setframerate(SR)
    w.writeframes(raw.tobytes())
print(f"[✓] 对比(未修复/语速快2倍): {cmp_path}")
print(f"    平滑度 修复前 {smooth(x):.3f} -> 修复后 {smooth(y):.3f}")
