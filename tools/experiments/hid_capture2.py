#!/usr/bin/env python3
"""从 /dev/hidraw 读取 IPRC1000 的 F7 音频报告 → PCM → WAV（含自动重连）

用法: sudo python3 hid_capture2.py [秒数]
"""
import collections
import os
import subprocess
import sys
import time
import wave

MAC = "20:C0:47:0A:34:83"
DEV = "/dev/hidraw1"
SECONDS = float(sys.argv[1]) if len(sys.argv) > 1 else 25
WAV = "/tmp/iprc1000_mic.wav"
HDR = 13


def connected() -> bool:
    try:
        out = subprocess.run(["hcitool", "con"], capture_output=True, text=True, timeout=8).stdout
        return "34:83" in out.lower()
    except Exception:
        return False


def connect() -> bool:
    try:
        subprocess.run(["bluetoothctl", "connect", MAC], capture_output=True, text=True, timeout=20)
    except Exception:
        pass
    time.sleep(1)
    return connected()


counts = collections.Counter()
sizes = collections.Counter()
pcm = bytearray()
stamps = []
heads = []
reconnects = 0
errors = 0
f = None

t0 = time.time()
last_report = time.time()
while time.time() - t0 < SECONDS:
    if f is None:
        if not connected() and not connect():
            time.sleep(1)
            continue
        try:
            f = open(DEV, "rb", buffering=0)
            print(f"[i] +{time.time()-t0:.1f}s 已打开 {DEV}")
        except OSError as e:
            print(f"[!] 打开失败 {e}")
            time.sleep(1)
            continue
    try:
        data = f.read(2048)
    except OSError as e:
        errors += 1
        print(f"[!] +{time.time()-t0:.1f}s 读取错误 {e} → 重连")
        try:
            f.close()
        except Exception:
            pass
        f = None
        reconnects += 1
        time.sleep(1)
        continue
    if not data:
        continue
    rid = data[0]
    counts[rid] += 1
    if rid == 0xF7:
        sizes[len(data)] += 1
        stamps.append(time.time())
        last_report = time.time()
        if len(heads) < 3:
            heads.append(data[:20].hex(" "))
        if len(data) > 1 + HDR:
            pcm += data[1 + HDR:]
    if int(time.time() - t0) % 5 == 0 and counts[0xF7] and stamps and stamps[-1] == last_report:
        pass

if f:
    f.close()

dur = time.time() - t0
print(f"[i] 采集 {dur:.1f}s  重连 {reconnects} 次  读取错误 {errors} 次")
print(f"[i] report id 统计: {dict(counts)}")
print(f"[i] F7 长度分布: {dict(sizes)}")
for h in heads:
    print(f"[i] F7 样例: {h}")
n = len(stamps)
print(f"[i] F7 报告 {n} 个，PCM {len(pcm)} 字节 = {len(pcm)//2} 样本")
if n > 1:
    span = max(stamps[-1] - stamps[0], 0.001)
    print(f"[i] 报告速率 {n/span:.1f} 包/秒")
    if len(pcm):
        print(f"[i] 实测采样率 ≈ {len(pcm)/2/span:.0f} Hz")
if pcm:
    with wave.open(WAV, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(16000)
        w.writeframes(bytes(pcm))
    print(f"[✓] 已写入 {WAV} ({os.path.getsize(WAV)} 字节)")
else:
    print("[!] 未收到 F7 音频（需按住遥控器麦克风键）")
