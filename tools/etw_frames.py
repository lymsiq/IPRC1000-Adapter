#!/usr/bin/env python3
"""打印连续事件的原始 ACL/L2CAP 帧头，判定真实分段方式。"""
import re, sys
XML = sys.argv[1] if len(sys.argv) > 1 else r"F:\vibe\bth2.xml"
N = int(sys.argv[2]) if len(sys.argv) > 2 else 30
d = open(XML, encoding="utf-8", errors="replace").read()
ev_re = re.compile(
    r'SystemTime="([^"]+)".*?'
    r'<Data Name="BIP_Type">(\d+)</Data>\s*'
    r'<Data Name="BIP_DataLen">\s*(\d+)</Data>\s*'
    r'<Data Name="BIP_Data">0x([0-9A-Fa-f]*)</Data>', re.S)
rows=[]
for m in ev_re.finditer(d):
    rows.append((m.group(1), int(m.group(2)), int(m.group(3)), bytes.fromhex(m.group(4))))
audio=[r for r in rows if r[1]==3]
print(f"# BIP_Type=3 共 {len(audio)} 条，打印前 {N} 条")
for i,(ts,typ,ln,b) in enumerate(audio[:N]):
    if len(b) >= 8:
        acl_h = int.from_bytes(b[0:2],'little'); acl_len = int.from_bytes(b[2:4],'little')
        l2_len = int.from_bytes(b[4:6],'little'); l2_cid = int.from_bytes(b[6:8],'little')
        t = ts[11:23]
        print(f"{i:3d} t={t} evt={ln:4d} | ACL h=0x{acl_h:04x} len={acl_len:4d} | L2CAP len={l2_len:4d} cid={l2_cid:5d} | hdr={b[:12].hex()} tail={b[-6:].hex()}")
    else:
        print(f"{i:3d} t={ts[11:23]} evt={ln:4d} | raw={b.hex()}")
