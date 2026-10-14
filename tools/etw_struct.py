#!/usr/bin/env python3
"""分析 frag2（313B 续片）负载的内部结构：找 0xF7 分隔、判断冗余/连续性。"""
import re, sys, struct
from collections import Counter
XML = sys.argv[1] if len(sys.argv) > 1 else r"F:\vibe\bth2.xml"
d = open(XML, encoding="utf-8", errors="replace").read()
ev_re = re.compile(
    r'SystemTime="([^"]+)".*?'
    r'<Data Name="BIP_Type">(\d+)</Data>\s*'
    r'<Data Name="BIP_DataLen">\s*(\d+)</Data>\s*'
    r'<Data Name="BIP_Data">0x([0-9A-Fa-f]*)</Data>', re.S)
rows=[]
for m in ev_re.finditer(d):
    rows.append((int(m.group(2)), int(m.group(3)), bytes.fromhex(m.group(4))))

# 只保留完整的 317B 续片
cont = [b for t,l,b in rows if t==3 and l==317]
hdrs = [b for t,l,b in rows if t==3 and l==8]
print(f"续片 {len(cont)} 条，首片头 {len(hdrs)} 条")

pay = [c[4:] for c in cont]          # 去掉 4 字节 ACL 头 -> 313 B
print(f"每条负载长度 {set(len(p) for p in pay)}")

# 1) 0xF7 出现位置 —— 看是否每个子报告都以 0xF7 开头
pos = Counter()
for p in pay[:200]:
    for i,v in enumerate(p[:8]):
        if v == 0xF7: pos[i]+=1
print(f"\n[1] 前 8 字节中 0xF7 出现次数(前200条): {dict(pos)}")
allf7 = Counter()
for p in pay[:200]:
    c = Counter(i for i,v in enumerate(p) if v==0xF7)
    for i,n in c.items(): allf7[i]+=1
print(f"[1b] 全条 0xF7 位置 Top10: {allf7.most_common(10)}")

# 2) 相邻续片是否相同（冗余？）
same = sum(1 for i in range(len(pay)-1) if pay[i]==pay[i+1])
print(f"\n[2] 相邻续片完全相同: {same}/{len(pay)-1}")

# 3) 片段内是否有重复子块：把 313 分成两半比较
import math
dup=0
for p in pay[:300]:
    a,b = p[:156], p[156:312]
    if sum(x!=y for x,y in zip(a,b)) < 5: dup+=1
print(f"[3] 前156B == 后156B 的条数(前300): {dup}")

# 4) 直接看前 3 条负载的十六进制
print("\n[4] 前 3 条负载前 40 字节:")
for p in pay[:3]:
    print("   ", p[:40].hex(' '))
print("[4b] 前 3 条负载后 16 字节:")
for p in pay[:3]:
    print("   ", p[-16:].hex(' '))

# 5) 时间戳：续片之间的间隔
def pts(s):
    return int(s[11:13])*3600+int(s[14:16])*60+int(s[17:19])+int(s[20:29])/1e9
ts=[pts(m.group(1)) for m in ev_re.finditer(d) if int(m.group(2))==3 and int(m.group(3))==317]
g=[(ts[i+1]-ts[i])*1000 for i in range(len(ts)-1)]
print(f"\n[5] 续片间隔 平均 {sum(g)/len(g):.2f}ms 中位 {sorted(g)[len(g)//2]:.2f}ms")

# 6) 作为 16bit LE 样本，检查平滑度（跳过 1 字节 vs 不跳）
def sm(sk):
    buf=bytearray()
    for p in pay: buf+=p[sk:]
    b=bytes(buf); s=struct.unpack("<%dh"%(len(b)//2), b[:len(b)//2*2])
    rms=(sum(v*v for v in s)/len(s))**.5
    d1=sum(abs(s[i+1]-s[i]) for i in range(len(s)-1))/(len(s)-1)
    # 边界跳变比
    per = (313-sk)//2
    bd=[]; inr=[]
    for i in range(len(s)-1):
        if i % per == per-1: bd.append(abs(s[i+1]-s[i]))
        else: inr.append(abs(s[i+1]-s[i]))
    return len(s), rms, d1/max(rms,1), (sum(bd)/max(len(bd),1))/(sum(inr)/max(len(inr),1))
print("\n[6] skip  样本    rms    平滑度  边界比")
for sk in (0,1,2,3,4,5):
    n,r,sc,br = sm(sk)
    print(f"    {sk}   {n:7d}  {r:6.0f}  {sc:.3f}   {br:.2f}")
