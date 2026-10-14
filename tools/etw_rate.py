#!/usr/bin/env python3
"""判定真实采样率：看频谱带宽与基频 F0 的合理区间。"""
import re, sys, struct, math
XML = sys.argv[1] if len(sys.argv) > 1 else r"F:\vibe\bth2.xml"
d = open(XML, encoding="utf-8", errors="replace").read()
ev_re = re.compile(
    r'<Data Name="BIP_Type">(\d+)</Data>\s*'
    r'<Data Name="BIP_DataLen">\s*(\d+)</Data>\s*'
    r'<Data Name="BIP_Data">0x([0-9A-Fa-f]*)</Data>', re.S)
pay=[bytes.fromhex(m.group(3))[4:] for m in ev_re.finditer(d) if int(m.group(1))==3 and int(m.group(2))==317]
skip=1
flat=[]
for p in pay[:600]:
    b=p[skip:]
    flat += list(struct.unpack("<%dh"%(len(b)//2), b[:len(b)//2*2]))
N=len(flat)
print(f"样本 {N}")

# 分帧 FFT，求平均幅度谱
def fft(x):
    n=len(x)
    if n==1: return x
    e=fft(x[0::2]); o=fft(x[1::2])
    t=[complex(math.cos(-2*math.pi*k/n),math.sin(-2*math.pi*k/n))*o[k] for k in range(n//2)]
    return [e[k]+t[k] for k in range(n//2)]+[e[k]-t[k] for k in range(n//2)]

W=512
spec=[0.0]*(W//2)
win=[0.5-0.5*math.cos(2*math.pi*i/W) for i in range(W)]
frames=0
for st in range(0, min(N-W, 600*156), W):
    seg=flat[st:st+W]
    if max(abs(v) for v in seg)<800: continue
    x=[seg[i]*win[i] for i in range(W)]
    X=fft(x); frames+=1
    for k in range(W//2): spec[k]+=abs(X[k])
if frames==0:
    print("没有足够的有效帧"); sys.exit(0)
spec=[s/frames for s in spec]
tot=sum(spec)
print(f"有效帧 {frames}")
# 累计能量占比
for frac in (0.5,0.9,0.95,0.99):
    acc=0
    for k in range(W//2):
        acc+=spec[k]
        if acc>=tot*frac:
            print(f"  {int(frac*100)}% 能量低于 {k*16000/W:7.0f} Hz (@16k) / {k*8000/W:6.0f} Hz (@8k)")
            break
# 高频能量占比
hi4k=sum(spec[k] for k in range(W//2) if k*16000/W>=4000)
print(f"  >4kHz 能量占比 {hi4k/tot*100:.2f}% (@16k 标注)")
hi3k4=sum(spec[k] for k in range(W//2) if 3000<=k*16000/W<4000)
print(f"  3-4kHz 能量占比 {hi3k4/tot*100:.2f}%")

# F0 自相关（在能量最强的帧上）
best=None
for st in range(0, min(N-W,600*156), W):
    seg=flat[st:st+W]
    e=sum(v*v for v in seg)
    if best is None or e>best[0]: best=(e,seg)
seg=best[1]
m=sum(seg)/len(seg)
sd=[v-m for v in seg]
def acf(lag): return sum(sd[i]*sd[i+lag] for i in range(len(sd)-lag))
r0=acf(0)
print("\n最强帧的自相关峰（候选基音周期）:")
cand=[]
for lag in range(20,300):
    if acf(lag)>acf(lag-1) and acf(lag)>=acf(lag+1):
        cand.append((acf(lag)/r0, lag))
cand.sort(reverse=True)
for r,lag in cand[:5]:
    print(f"  R={r:+.3f} lag={lag} -> F0={16000/lag:7.1f} Hz(@16k)  /  {8000/lag:6.1f} Hz(@8k)")
