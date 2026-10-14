#!/usr/bin/env python3
"""深度分析 ETW 蓝牙抓包：确定音频投递是否完整、是否存在丢样本。

关注点（对应用户反馈「语速快 + 电流音」）：
  1. 每个 ACL 事件的 BIP_DataLen 分布 —— 是否稳定在 653，还是出现半长（~313）
  2. 事件的时间戳间隔 —— 是否等间隔（16 kHz 下每 653B ≈ 40.8 ms）
  3. 前缀（ACL 头）是否恒定 —— 若不是，FRAG_SKIP=5 会造成周期性错位
  4. 直接解码重建音频并评估平滑度
"""
import re
import struct
import sys
import wave

XML = sys.argv[1] if len(sys.argv) > 1 else r"F:\vibe\bth2.xml"
RATE = 16000

d = open(XML, encoding="utf-8", errors="replace").read()

# 每个 Event 块：抓 SystemTime + BIP_Type/DataLen/Data
ev_re = re.compile(
    r'SystemTime="([^"]+)".*?'
    r'<Data Name="BIP_Type">(\d+)</Data>\s*'
    r'<Data Name="BIP_DataLen">\s*(\d+)</Data>\s*'
    r'<Data Name="BIP_Data">0x([0-9A-Fa-f]*)</Data>',
    re.S,
)

rows = []
for m in ev_re.finditer(d):
    ts, typ, ln, hexd = m.group(1), int(m.group(2)), int(m.group(3)), m.group(4)
    rows.append((ts, typ, ln, bytes.fromhex(hexd)))

print(f"[i] 事件总数 {len(rows)}")

from collections import Counter
tc = Counter(r[1] for r in rows)
print(f"[i] BIP_Type 分布: {dict(tc)}")

audio = [r for r in rows if r[1] == 3]
print(f"[i] ACL 数据事件(BIP_Type=3) {len(audio)}")
if not audio:
    sys.exit(1)

lens = Counter(len(a[3]) for a in audio)
print(f"[i] 实际数据长度分布(字节): {dict(sorted(lens.items()))}")
dlens = Counter(a[2] for a in audio)
print(f"[i] 声明 BIP_DataLen 分布: {dict(sorted(dlens.items()))}")

# 时间间隔
def parse_ts(s):
    # 2026-10-13T14:52:33.060587400+07:59
    hh = int(s[11:13]); mm = int(s[14:16]); ss = int(s[17:19])
    frac = int(s[20:29]) / 1e9
    return hh * 3600 + mm * 60 + ss + frac

ts = [parse_ts(a[0]) for a in audio]
gaps = [ (ts[i+1]-ts[i])*1000 for i in range(len(ts)-1) ]
if gaps:
    gaps_sorted = sorted(gaps)
    avg = sum(gaps)/len(gaps)
    med = gaps_sorted[len(gaps_sorted)//2]
    print(f"[i] 事件间隔: 平均 {avg:.2f} ms  中位 {med:.2f} ms  最小 {min(gaps):.2f}  最大 {max(gaps):.2f}")
    # 出现最多的几个间隔
    buckets = Counter(round(g, 1) for g in gaps)
    print(f"[i] 最常见间隔: {buckets.most_common(8)}")

# 前缀分布（每事件前 8 字节）
pref = Counter(a[3][:8].hex() for a in audio)
print(f"[i] 前 8 字节前缀种类数 {len(pref)}，最常见: {pref.most_common(4)}")

# 长度 == 653 的完整事件占比
full = sum(1 for a in audio if len(a[3]) >= 649)
print(f"[i] 完整事件(>=649B) {full}/{len(audio)} = {full/len(audio)*100:.1f}%")

# 重建
def build(skip, goff=0):
    buf = bytearray()
    for a in audio:
        buf += a[3][skip:]
    b = bytes(buf[goff:])
    return struct.unpack("<%dh" % (len(b)//2), b[:len(b)//2*2])

def score(s):
    if len(s) < 100: return 1e9, 0
    rms = (sum(v*v for v in s)/len(s))**0.5
    d1 = sum(abs(s[i+1]-s[i]) for i in range(len(s)-1))/(len(s)-1)
    return d1/max(rms,1), rms

print("\n[i] 不同 FRAG_SKIP 的平滑度:")
for skip in (4, 5, 6, 7, 8):
    s = build(skip)
    sc, rms = score(s)
    print(f"    skip={skip}: 样本={len(s)} 时长={len(s)/RATE:.2f}s rms={rms:.0f} 平滑度={sc:.3f}")

best_skip = 5
s = build(best_skip)
out = r"F:\vibe\deep_16k.wav"
with wave.open(out, "wb") as w:
    w.setnchannels(1); w.setsampwidth(2); w.setframerate(RATE)
    w.writeframes(struct.pack("<%dh" % len(s), *s))
print(f"\n[✓] 写出 {out}")
