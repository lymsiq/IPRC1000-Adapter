#!/usr/bin/env python3
"""用完整帧重建 IPRC1000 音频（重启 MaxEtwBytes 生效后）—— 零修补。

## 帧结构（本轮逐字节统计确认）
L2CAP 负载 653 字节：
    [0..12]   13 字节帧头      a1 f7 01 <seq> 00 81 00 <cnt> 00 00 00 40 01
    [13..652] 640 字节音频 = **320 个 16bit LE 样本**
ACL 上被拆成两个 HCI 分片：
    首片 dlen=344（PB=10）  ← 重启前只记 8 字节头，340 字节丢失；重启后完整
    续片 dlen=313（PB=01）  ← 一直完整
344 + 313 = 657 = L2CAP头(4) + 负载(653) ✓

## 回归结果（live2.xml，705 帧）
    帧边界 |Δ| 54  vs  帧内 |Δ| 55   → 比值 **0.98**（=1 即无缝）
    帧头序号 700 次步进全部恰好 +1   → **无丢帧、无重复**
⇒ 音频完整连续，**不再需要 PLC 修补**。

## 采样率
结构上每帧 320 样本；实测最长连续段 391 帧 / 7.325 s → 18.73 ms/帧 → 17.1 kHz。
但蓝牙 ACL 重传会**拉长**投递间隔，使推算值偏高，故真实速率 ≤17.1 kHz。
结合此前主观测试（"16K 就是我自己的声音"）→ **默认 16 kHz**（320 样本 = 20.0 ms）。
本脚本同时输出一组速率备选，便于试听确认。

用法: python etw_full_build.py <xml> [输出wav前缀]
"""
import os
import re
import struct
import sys
import wave

import numpy as np

XML = sys.argv[1] if len(sys.argv) > 1 else r"F:\vibe\live2.xml"
PREFIX = sys.argv[2] if len(sys.argv) > 2 else r"F:\vibe\clean_full"

HDR = 13                 # 帧头长度
AUDIO_BYTES = 653 - HDR  # 640
SAMPLES = AUDIO_BYTES // 2   # 320
RATES = [16000, 15500, 16500, 17100]

d = open(XML, encoding="utf-8", errors="replace").read()
ev = re.compile(
    r'SystemTime="([^"]+)".*?'
    r'<Data Name="BIP_Type">(\d+)</Data>\s*'
    r'<Data Name="BIP_DataLen">\s*(\d+)</Data>\s*'
    r'<Data Name="BIP_Data">0x([0-9A-Fa-f]*)</Data>', re.S)


def tsec(s):
    return (int(s[11:13]) * 3600 + int(s[14:16]) * 60 + int(s[17:19])
            + int(s[20:29]) / 1e9)


# 配帧：ACL 数据 = 首片 344 字节 + 续片 313 字节 = 657；去掉 4 字节 L2CAP 头 → 653 负载
frames = []
pending = None
for m in ev.finditer(d):
    if int(m.group(2)) != 3:
        continue
    raw = bytes.fromhex(m.group(4))
    if len(raw) < 4:
        continue
    dlen = int.from_bytes(raw[2:4], "little")
    flag = (int.from_bytes(raw[0:2], "little") >> 12) & 0x3
    if flag == 2 and dlen == 344:
        pending = (raw[4:], tsec(m.group(1)))
    elif flag == 1 and dlen == 313 and pending is not None:
        pdu = pending[0] + raw[4:]
        if len(pdu) == 657:
            frames.append((pdu[4:], pending[1]))
        pending = None

n = len(frames)
print(f"[i] 完整音频帧 {n} 个   每帧负载 {set(len(f[0]) for f in frames)} 字节")
if not n:
    print("[!] 未配出完整帧")
    sys.exit(1)

seq = [f[0][3] for f in frames]
times = [f[1] for f in frames]
jumps = sum(1 for i in range(1, n) if (seq[i] - seq[i - 1]) % 256 != 1)
print(f"[i] 帧头序号 {seq[0]}..{seq[-1]}，非连续跳变 {jumps} 处")
print(f"[i] 帧头示例: {frames[0][0][:HDR].hex(' ')}")

# 拼接音频。默认**不补静音**：把 705 帧语音直接接起来，得到一段连续语音，
# 最适合直接喂给豆包/试听判断音质。加 --gaps 则按时间戳补静音以保留真实停顿
# （但蓝牙 ACL 停顿也会被当成停顿，容易把文件拉得很长）。
KEEP_GAPS = "--gaps" in sys.argv
parts = []
for i, (p, t) in enumerate(frames):
    if KEEP_GAPS and i > 0:
        dt = t - times[i - 1]
        if dt > 0.05:
            gap = int(min(dt - 0.020, 5.0) * 16000)
            if gap > 0:
                parts.append(np.zeros(gap, dtype=np.float64))
    parts.append(np.frombuffer(p[HDR:HDR + AUDIO_BYTES], dtype="<i2").astype(np.float64))

s = np.concatenate(parts)
print(f"[i] 音频总样本 {len(s)}  纯语音 {n*SAMPLES} = {n*SAMPLES/16000:.2f}s @16k")


def metrics(x, per=SAMPLES):
    rms = float(np.sqrt(np.mean(x * x))) or 1.0
    d1 = float(np.mean(np.abs(np.diff(x)))) / rms
    bd = [abs(x[i * per] - x[i * per - 1]) for i in range(1, len(x) // per)]
    return rms, d1, (float(np.mean(bd)) / (d1 * rms) if bd else 0)


rms, d1, br = metrics(s)
print(f"[i] rms={rms:.0f}  平滑度={d1:.4f}  帧边界/帧内 |Δ| 比值={br:.2f}  (≈1 = 无缝)")

out = s - s.mean()
peak = float(np.max(np.abs(out))) or 1.0
out = np.clip(out * 0.70 * 32767.0 / peak, -32768, 32767).astype("<i2")

written = []
for R in RATES:
    path = f"{PREFIX}_{R//1000}k{R%1000//100}.wav" if R != 16000 else f"{PREFIX}_16k.wav"
    with wave.open(path, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(R)
        w.writeframes(out.tobytes())
    written.append((R, path, len(out) / R))
    print(f"[✓] {path}   {len(out)/R:.2f}s @ {R} Hz")

print("\n[i] 先用 16k 试听；若语速或音调不对，再试其余速率。")
