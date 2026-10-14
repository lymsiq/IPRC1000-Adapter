#!/usr/bin/env python3
"""把 BthPort ETW 抓包（XML）导出成**带时间轴的回放文件**，供 etw_consumer --replay 使用。

导出**所有** ACL 分片（不只是音频帧），这样回放能同时验证：
  · 音频：首片 344 + 续片 313 → 653 字节负载
  · 按键：单分片的 HID 报告（CID 0x41，报告头 a1 01）

记录格式（小端）：
    u8  flag          分片标志（2=首片/独立包, 1=续片）
    u8  pad
    u16 dlen          ACL 头里声明的长度
    u32 dt_ms         相对上一条记录的毫秒间隔
    u16 acl_len       实际存下来的字节数
    u8  acl[acl_len]  ACL 数据（不含 4 字节 ACL 头）

用法: python dump_payloads.py <xml> <out.bin>
"""
import struct
import sys
import re

XML = sys.argv[1] if len(sys.argv) > 1 else r"F:\vibe\live2.xml"
OUT = sys.argv[2] if len(sys.argv) > 2 else r"F:\vibe\iprc1000_replay.bin"

d = open(XML, encoding="utf-8", errors="replace").read()
ev = re.compile(
    r'SystemTime="([^"]+)".*?'
    r'<Data Name="BIP_Type">(\d+)</Data>\s*'
    r'<Data Name="BIP_DataLen">\s*(\d+)</Data>\s*'
    r'<Data Name="BIP_Data">0x([0-9A-Fa-f]*)</Data>', re.S)


def tsec(s):
    return (int(s[11:13]) * 3600 + int(s[14:16]) * 60 + int(s[17:19])
            + int(s[20:29]) / 1e9)


recs = []
prev = None
for m in ev.finditer(d):
    if int(m.group(2)) != 3:
        continue
    raw = bytes.fromhex(m.group(4))
    if len(raw) < 4:
        continue
    dlen = int.from_bytes(raw[2:4], "little")
    flag = (int.from_bytes(raw[0:2], "little") >> 12) & 0x3
    if flag not in (1, 2) or dlen == 0 or dlen > 2048:
        continue
    t = tsec(m.group(1))
    dt = 0 if prev is None else max(0, int((t - prev) * 1000))
    prev = t
    recs.append((flag, dlen, dt, raw[4:]))

with open(OUT, "wb") as f:
    for flag, dlen, dt, acl in recs:
        f.write(struct.pack("<BBHIH", flag, 0, dlen, dt, len(acl)))
        f.write(acl)

hid = sum(1 for _, _, _, a in recs if len(a) >= 12 and a[2] == 0x41 and a[4] == 0xA1)
audio1 = sum(1 for f_, dl, _, _ in recs if f_ == 2 and dl == 344)
audio2 = sum(1 for f_, dl, _, _ in recs if f_ == 1 and dl == 313)
print(f"[✓] {len(recs)} 条记录 → {OUT}")
print(f"    音频首片 {audio1} · 音频续片 {audio2} → 完整帧 {min(audio1, audio2)}")
print(f"    HID 按键报告 {hid}")
