#!/usr/bin/env python3
"""统计 BTHPORT ETW 中所有 EventID / BIP_Type / 方向 / 分片情况。"""
import re, sys
from collections import Counter
XML = sys.argv[1] if len(sys.argv) > 1 else r"F:\vibe\bth2.xml"
d = open(XML, encoding="utf-8", errors="replace").read()

blk = re.compile(r"<Event\b.*?</Event>", re.S)
eid_re = re.compile(r"<EventID>(\d+)</EventID>")
typ_re = re.compile(r'<Data Name="BIP_Type">(\d+)</Data>')
len_re = re.compile(r'<Data Name="BIP_DataLen">\s*(\d+)</Data>')
dat_re = re.compile(r'<Data Name="BIP_Data">0x([0-9A-Fa-f]*)</Data>')

eids = Counter(); types = Counter(); combo = Counter()
frames = []   # (type, decl_len, actual_len, first8)
for m in blk.finditer(d):
    t = m.group(0)
    eid = eid_re.search(t); typ = typ_re.search(t); ln = len_re.search(t); dt = dat_re.search(t)
    if not (eid and typ): continue
    eids[int(eid.group(1))] += 1
    ty = int(typ.group(1)); types[ty] += 1
    dl = int(ln.group(1)) if ln else -1
    raw = bytes.fromhex(dt.group(1)) if dt else b""
    combo[(int(eid.group(1)), ty, dl, len(raw))] += 1
    frames.append((ty, dl, len(raw), raw))

print("EventID 计数:", dict(eids))
print("BIP_Type 计数:", dict(types))
print("\n(EventID, BIP_Type, 声明长度, 实际长度) 最常见的 15 组:")
for k, v in combo.most_common(15):
    print(f"   {k}  ×{v}")

# 方向 / 分片标记：ACL handle(2) 的 bit14-13 = PB
pb = Counter()
for ty, dl, al, raw in frames:
    if ty == 3 and len(raw) >= 4:
        h = int.from_bytes(raw[0:2], 'little')
        handle = h & 0x0FFF
        flag = (h >> 12) & 0x3
        pb[(handle, flag)] += 1
print("\n(ACL handle, PB/BC flag) 计数:", dict(pb))

# 关键：ACL 声明长度 vs 实际携带数据长度
print("\nBIP_Type=3 的 ACL 声明 dlen vs 实际 EventData 字节:")
mism = Counter()
for ty, dl, al, raw in frames:
    if ty == 3 and len(raw) >= 4:
        dlen = int.from_bytes(raw[2:4], 'little')
        frm = int.from_bytes(raw[0:2], 'little')
        flag = (frm >> 12) & 0x3
        carried = len(raw) - 4
        mism[(flag, dlen, carried, "COMPLETE" if carried >= dlen else "TRUNCATED")] += 1
for k, v in sorted(mism.items(), key=lambda x: -x[1])[:12]:
    print(f"   PB={k[0]} 声明={k[1]:4d} 实际={k[2]:4d} {k[3]}  ×{v}")
