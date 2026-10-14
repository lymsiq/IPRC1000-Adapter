#!/usr/bin/env python3
"""从 /dev/hidraw 读取 IPRC1000 的 F7 音频报告 → PCM → WAV

用法: sudo python3 hid_capture.py [/dev/hidrawN] [秒数]
输出: /tmp/iprc1000_mic.wav  + 统计信息（报告速率 / 采样率 / 头部分析）
"""
import collections
import sys
import time
import wave

DEV = sys.argv[1] if len(sys.argv) > 1 else "/dev/hidraw1"
SECONDS = float(sys.argv[2]) if len(sys.argv) > 2 else 20
WAV = "/tmp/iprc1000_mic.wav"
HDR = 13  # 报告头长度（不含 report ID）

counts = collections.Counter()
sizes = collections.Counter()
pcm = bytearray()
stamps = []
heads = []

f = open(DEV, "rb", buffering=0)
t0 = time.time()
try:
    while time.time() - t0 < SECONDS:
        data = f.read(2048)
        if not data:
            continue
        rid = data[0]
        counts[rid] += 1
        if rid == 0xF7:
            sizes[len(data)] += 1
            stamps.append(time.time())
            if len(heads) < 3:
                heads.append(data[:20].hex(" "))
            if len(data) > 1 + HDR:
                pcm += data[1 + HDR:]
except KeyboardInterrupt:
    pass
finally:
    f.close()

dur = time.time() - t0
print(f"[i] 采集 {dur:.1f}s")
print(f"[i] report id 统计: {dict(counts)}")
print(f"[i] F7 报告长度分布: {dict(sizes)}")
for h in heads:
    print(f"[i] F7 头部样例: {h}")
n_f7 = len(stamps)
print(f"[i] F7 报告 {n_f7} 个，PCM {len(pcm)} 字节 = {len(pcm) // 2} 样本")
if n_f7 > 1:
    span = max(stamps[-1] - stamps[0], 0.001)
    print(f"[i] 报告速率 {n_f7 / span:.1f} 包/秒")
    if len(pcm):
        print(f"[i] 实测采样率 ≈ {len(pcm) / 2 / span:.0f} Hz")
if pcm:
    with wave.open(WAV, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(16000)
        w.writeframes(bytes(pcm))
    print(f"[✓] 已写入 {WAV}（按 16 kHz 打包，若实测采样率不同请据上面数值换算）")
else:
    print("[!] 未收到 F7 音频数据（确认按住遥控器麦克风键）")
