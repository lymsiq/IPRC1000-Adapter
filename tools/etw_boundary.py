#!/usr/bin/env python3
"""决定性判定：块边界到底是「内容丢失」还是「连续」。
  - 打印边界附近真实样本值
  - 块间互相关（找重叠/重复）
  - 自相关找真实基频周期
"""
import re, sys, struct
XML = sys.argv[1] if len(sys.argv) > 1 else r"F:\vibe\bth2.xml"
d = open(XML, encoding="utf-8", errors="replace").read()
ev_re = re.compile(
    r'SystemTime="([^"]+)".*?'
    r'<Data Name="BIP_Type">(\d+)</Data>\s*'
    r'<Data Name="BIP_DataLen">\s*(\d+)</Data>\s*'
    r'<Data Name="BIP_Data">0x([0-9A-Fa-f]*)</Data>', re.S)
rows=[(m.group(1),int(m.group(2)),int(m.group(3)),bytes.fromhex(m.group(4)))
      for m in ev_re.finditer(d)]
cont=[r[3] for r in rows if r[1]==3 and r[2]==317]
pay=[c[4:] for c in cont]

def to_i16(b): return struct.unpack("<%dh"%(len(b)//2), b[:len(b)//2*2])
chunks=[to_i16(p[1:]) for p in pay]     # skip 1 -> 156 samples
print(f"块数 {len(chunks)}，每块 {len(chunks[0])} 样本")

print("\n=== 前两块边界处真实样本 (块0 末 8 + 块1 首 8) ===")
c0,c1=chunks[0],chunks[1]
print("  块0 末:", c0[-8:])
print("  块1 首:", c1[:8])
print(f"  边界跳变 Δ={abs(c1[0]-c0[-1])}   块内平均|Δ|={sum(abs(c0[i+1]-c0[i]) for i in range(len(c0)-1))/(len(c0)-1):.1f}")

print("\n=== 块3 末 / 块4 首 ===")
c3,c4=chunks[3],chunks[4]
print("  块3 末:", c3[-8:])
print("  块4 首:", c4[:8])
print(f"  边界跳变 Δ={abs(c4[0]-c3[-1])}")

# 块间互相关：块N 尾部与块N+1 头部，寻找最佳重叠 lag
def best_overlap(a, b, maxlag=60):
    best=(None, 1e18)
    for lag in range(1, maxlag):
        # a[-lag:] 对 b[:lag]
        s=sum((a[-lag+i]-b[i])**2 for i in range(lag))/lag
        if s<best[1]: best=(lag,s)
    return best

errs=[best_overlap(chunks[i],chunks[i+1]) for i in range(min(300,len(chunks)-1))]
from collections import Counter
print("\n=== 块间最佳重叠 lag 分布（前300对）===")
print(Counter(l for l,_ in errs).most_common(8))

# 相邻块直接差值（同长度）
diffs=[]
for i in range(min(300,len(chunks)-1)):
    a,b=chunks[i],chunks[i+1]
    diffs.append(sum((x-y)**2 for x,y in zip(a,b))/len(a))
rms=sum(x*x for x in chunks[0])/len(chunks[0])
print(f"\n相邻块同位置均方误差 {sum(diffs)/len(diffs):.0f}  vs  块内均方值 rms^2={rms:.0f}")

# 自相关：拼接流找主周期
flat=[s for c in chunks[:400] for s in c]
N=len(flat); mean=sum(flat)/N
ac=[]
for lag in range(1,400):
    v=sum((flat[i]-mean)*(flat[i+lag]-mean) for i in range(N-lag))/(N-lag)
    ac.append(v)
# 每 156 样本处的自相关（块周期）
print("\n=== 拼接流自相关（归一化，看块周期与基音周期）===")
v0=sum((x-mean)**2 for x in flat)/N
for lag in (78,156,312,468):
    if lag<len(ac): print(f"  lag={lag:4d} ({lag/16000*1000:.2f}ms@16k / {lag/8000*1000:.2f}ms@8k)  R={ac[lag-1]/v0:+.3f}")
# 找峰值
peaks=sorted(range(1,400), key=lambda i:-ac[i-1])[:6]
print("  自相关最强 lag:", [(p, round(ac[p-1]/v0,3)) for p in sorted(peaks)])
