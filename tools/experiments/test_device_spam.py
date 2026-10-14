#!/usr/bin/env python3
"""检验「遥控器是不是在连发某个按键」以及「是不是在等主机回包」。

思路：设备→主机的原始 HID 报告全在 BthPort ETW 里。直接数——按住语音键的整段时间里，
usage 0x10（语音键）到底出现了几次：
  · 若 ≈ 按住秒数 × 31  → 设备在连发（那才需要换驱动/改固件）
  · 若只有个位数、且时间戳是秒级间隔 → 是 Windows 键盘自动重复合成，设备是干净的

顺带把 BIP_Type=1/2/4（链路层/控制信令）也摊开，用于判断"是否缺主机回包"。

用法: python test_device_spam.py <xml>
"""
import re
import sys
from collections import Counter, defaultdict

XML = sys.argv[1] if len(sys.argv) > 1 else r"F:\vibe\live2.xml"
VOICE_USAGE = 0x10          # 语音键的 HID usage
AUTO_REPEAT_HZ = 31         # Windows 默认键盘自动重复率

d = open(XML, encoding="utf-8", errors="replace").read()
ev = re.compile(
    r'SystemTime="([^"]+)".*?'
    r'<Data Name="BIP_Type">(\d+)</Data>\s*'
    r'<Data Name="BIP_DataLen">\s*(\d+)</Data>\s*'
    r'<Data Name="BIP_Data">0x([0-9A-Fa-f]*)</Data>', re.S)


def tsec(s):
    return (int(s[11:13]) * 3600 + int(s[14:16]) * 60 + int(s[17:19])
            + int(s[20:29]) / 1e9)


bytype = defaultdict(list)
for m in ev.finditer(d):
    bytype[int(m.group(2))].append((tsec(m.group(1)), int(m.group(3)),
                                    bytes.fromhex(m.group(4))))

print("=== BIP_Type 分布（3 = HCI 负载；1/2/4 = 链路层/控制信令）===")
for t in sorted(bytype):
    lens = Counter(len(x[2]) for x in bytype[t])
    print(f"  BIP_Type={t}: {len(bytype[t]):5d} 条  长度分布 {dict(sorted(lens.items())[:5])}")


def usages(raw: bytes):
    """从 ETW 负载里取出 HID 键盘报告的 6 个 usage 槽（CID 0x41，报告头 a1 01）"""
    acl = raw[4:]
    if len(acl) >= 12 and acl[2] == 0x41 and acl[4] == 0xA1 and acl[5] == 0x01:
        return list(acl[8:14])
    return None


# 音频帧 = 用户按住语音键的证据
audio = [x for x in bytype[3]
         if len(x[2]) >= 8
         and ((int.from_bytes(x[2][0:2], "little") >> 12) & 3) == 2
         and int.from_bytes(x[2][2:4], "little") == 344]

print("\n=== 按住语音键的时间窗口（按音频帧推断）===")
if audio:
    a0, a1 = audio[0][0], audio[-1][0]
    span = a1 - a0
    print(f"  {len(audio)} 个音频帧，窗口 {span:.1f} 秒")
else:
    a0 = a1 = 0
    span = 0
    print("  抓包里没有音频帧（这次没按语音键？）")

hits = []
for t, _dl, raw in bytype[3]:
    u = usages(raw)
    if u and VOICE_USAGE in u:
        hits.append((t, [hex(v) for v in u if v]))

print(f"\n=== 设备发出的语音键报告（usage 0x{VOICE_USAGE:02X}）===")
print(f"  整份抓包共 {len(hits)} 次")
for t, u in hits:
    print(f"    {t % 1000:9.3f}   usages={u}")
if len(hits) >= 2:
    gaps = [(hits[i + 1][0] - hits[i][0]) for i in range(len(hits) - 1)]
    print("  相邻间隔(s): " + ", ".join(f"{g:.2f}" for g in gaps))

expected = int(span * AUTO_REPEAT_HZ)
print(f"\n=== 判定 ===")
print(f"  若设备在连发：窗口 {span:.1f}s × {AUTO_REPEAT_HZ} Hz ≈ {expected} 次")
print(f"  实测：{len(hits)} 次")
if span > 3 and len(hits) < expected / 10:
    print("  ⇒ 设备**没有连发**；那 31 Hz 的重复是 Windows 键盘类驱动合成的。")
    print("     解法：① 内核钩子吞掉（现行）或 ② 把设备驱动换成 HID-compliant device（终态）")
else:
    print("  ⇒ 需要进一步确认（设备可能确实在重发）")

print("\n=== 所有人机交互 usage 统计 ===")
c = Counter()
for _t, _dl, raw in bytype[3]:
    u = usages(raw)
    if u:
        for v in u:
            if v:
                c[f"0x{v:02X}"] += 1
print("  ", dict(c.most_common(12)))

print("\n=== 链路层/控制信令样本（判断是否缺主机回包）===")
for t in (1, 2, 4):
    items = bytype.get(t, [])
    print(f"  BIP_Type={t}（{len(items)} 条）:")
    for _tt, dl, raw in items[:4]:
        print(f"    dlen={dl:4d}  {raw[:20].hex(' ')}")
print("\n  说明：蓝牙的可靠传输由 L2CAP/ACL **链路层自动重传**保证，")
print("        应用层不需要握手/回包。上面这些条目就是链路层信令。")
