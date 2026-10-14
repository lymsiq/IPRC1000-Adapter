#!/usr/bin/env python3
"""btsnoop_hci.log 分析器 —— 判定蓝牙遥控器语音音频走哪个通道

用法:
    python btsnoop_analyze.py <btsnoop_hci.log 或 bugreport.zip> [IPRC MAC 后4位]

输出:
  1. 文件概览（记录数、datalink 类型、时间跨度）
  2. L2CAP 信令通道建立请求（PSM / SCID / DCID）—— 找 RFCOMM(3)、AVDTP(25)、ATT、SMP
  3. 各动态通道的数据量与速率 —— 找"音频流"（大流量、稳定速率）
  4. LE 广播/连接事件 —— 判定遥控器是否自建 BLE 通道
  5. 每秒时间线（流量最大的通道）—— 看有无"按住语音键才开始"的数据流
"""
import sys
import struct
import zipfile
import os
import collections
import datetime

H4_CMD, H4_ACL, H4_SCO, H4_EVT, H4_ISO = 0x01, 0x02, 0x03, 0x04, 0x05

PSM_NAMES = {
    0x0001: "SDP", 0x0003: "RFCOMM", 0x0005: "TCS-BIN", 0x0007: "ATT",
    0x0009: "BNEP", 0x000F: "BNEP?", 0x0011: "HID_Control", 0x0013: "HID_Interrupt",
    0x0015: "UPnP", 0x0017: "AVCTP", 0x0019: "AVDTP", 0x001B: "AVCTP_Browsing",
    0x001D: "UDI_C-Plane", 0x001F: "ATT_BLE", 0x0021: "3DSP", 0x0025: "OTP",
    0x0101: "L2CAP", 0x1001: "MAP_Access", 0x1003: "MAP_Access?",
}


def load_btsnoop(path):
    """读取 btsnoop（支持直接给 bugreport.zip，自动从中提取）"""
    if path.lower().endswith(".zip"):
        with zipfile.ZipFile(path) as z:
            cands = [n for n in z.namelist() if n.lower().endswith("btsnoop_hci.log")]
            if not cands:
                raise SystemExit("zip 中没有找到 btsnoop_hci.log：\n  " + "\n  ".join(z.namelist()[:20]))
            cands.sort(key=len)
            print(f"[i] 从 zip 提取: {cands[0]}")
            return z.read(cands[0])
    with open(path, "rb") as f:
        return f.read()


def parse_records(data):
    if data[:8] != b"btsnoop\x00":
        raise SystemExit("不是 btsnoop 格式（magic 不匹配）")
    version, datalink = struct.unpack_from(">II", data, 8)
    kind = {
        1002: "HCI UART(H4)",
        1001: "HCI UART(H4)",
        2001: "HCI monitor(btmon)",
        2002: "HCI simulator",
        2004: "HCI Android",
    }.get(datalink, f"未封装/其它({datalink})")
    print(f"[i] btsnoop version={version} datalink={datalink} ({kind})")
    off = 16
    recs = []
    while off + 24 <= len(data):
        orig_len, inc_len, flags, drops, ts = struct.unpack_from(">IIIIq", data, off)
        off += 24
        payload = data[off:off + inc_len]
        off += inc_len

        if datalink == 2001 and len(payload) >= 4:
            # HCI monitor 帧：opcode(2 LE) + index(2 LE) + HCI 包（无 H4 类型字节）
            op, _idx = struct.unpack_from("<HH", payload, 0)
            body = payload[4:]
            h4 = {
                0x0004: H4_CMD,   # HCI Command
                0x0005: H4_EVT,   # HCI Event
                0x0006: H4_ACL,   # ACL RX
                0x0007: H4_ACL,   # ACL TX
                0x0008: H4_SCO,   # SCO RX
                0x0009: H4_SCO,   # SCO TX
                0x000A: H4_ISO,   # ISO RX
                0x000B: H4_ISO,   # ISO TX
            }.get(op)
            if h4 is None:
                continue  # mgmt / index 事件等，跳过
            recs.append((flags & 0x01, ts, bytes([h4]) + body))
        else:
            recs.append((flags & 0x01, ts, payload))  # (direction: 1=controller→host, ts, h4 frame)

    print(f"[i] 记录数={len(recs)}")
    return datalink, recs


def analyze(recs, mac_hint=None):
    # 统计容器
    acl = []                      # (ts, from_ctrl, handle, l2cap_cid, payload_len, l2cap_payload)
    l2cap_conn = []               # (ts, psm, scid, dcid)
    dyn_bytes = collections.Counter()
    dyn_first, dyn_last = {}, {}
    dyn_l2 = {}                   # cid -> [l2cap payload]
    le_adv = collections.Counter()
    le_conn = []
    hci_cmd = collections.Counter()
    evt = collections.Counter()
    h4_count = collections.Counter()
    sco_bytes = collections.Counter()   # 可选的同步链路（音频常走这里）
    iso_bytes = collections.Counter()
    t0 = tN = None

    for from_ctrl, ts, frame in recs:
        if not frame:
            continue
        if t0 is None:
            t0 = ts
        tN = ts
        h4 = frame[0]
        body = frame[1:]
        h4_count[h4] += 1
        if h4 == H4_SCO:
            sco_bytes[int(ts // 1e6)] += len(frame)
            continue
        if h4 == H4_ISO:
            iso_bytes[int(ts // 1e6)] += len(frame)
            continue
        if h4 == H4_CMD:
            if len(body) >= 2:
                hci_cmd[struct.unpack_from("<H", body, 0)[0]] += 1
        elif h4 == H4_EVT:
            if len(body) >= 1:
                code = body[0]
                evt[code] += 1
                # LE Meta (0x3E) → 子事件
                if code == 0x3E and len(body) >= 3:
                    sub = body[2]
                    evt[f"0x3E/{sub:#04x}"] += 1
                    if sub == 0x02 and len(body) >= 12:  # LE Advertising Report
                        n = body[3]
                        addr = body[6:12][::-1].hex(":")
                        le_adv[addr] += 1
                    if sub == 0x01 and len(body) >= 10:  # LE Connection Complete
                        addr = body[8:14][::-1].hex(":")
                        le_conn.append((ts, addr))
        elif h4 == H4_ACL:
            if len(body) < 4:
                continue
            handle_flags, dlen = struct.unpack_from("<HH", body, 0)
            handle = handle_flags & 0x0FFF
            pb = (handle_flags >> 12) & 0x3
            l2 = body[4:4 + dlen]
            # 仅统计 L2CAP 起始分片（PB=0b10 表示首片/完整包）
            if pb in (0b00, 0b10) and len(l2) >= 4:
                ln, cid = struct.unpack_from("<HH", l2, 0)
                payload = l2[4:4 + ln]
                acl.append((ts, from_ctrl, handle, cid, len(payload)))
                dyn_bytes[cid] += len(payload)
                dyn_first.setdefault(cid, ts)
                dyn_last[cid] = ts
                dyn_l2.setdefault(cid, []).append(payload)
                # L2CAP 信令通道 0x0001：解析连接请求
                if cid == 0x0001 and len(payload) >= 4:
                    code = payload[0]
                    if code == 0x02 and len(payload) >= 8:  # Connection Request
                        psm, scid = struct.unpack_from("<HH", payload, 4)
                        l2cap_conn.append((ts, psm, scid, None))
                    if code == 0x03 and len(payload) >= 8:  # Connection Response
                        dcid, scid, result, status = struct.unpack_from("<HHHH", payload, 4)
                        l2cap_conn.append((ts, None, scid, dcid))

    span = (tN - t0) / 1e6 if t0 and tN else 0
    print(f"[i] 时间跨度: {span:.1f} 秒")
    h4names = {H4_CMD: "命令", H4_ACL: "ACL", H4_SCO: "SCO(同步)", H4_EVT: "事件", H4_ISO: "ISO"}
    print("[i] HCI 包类型统计: " + ", ".join(
        f"{h4names.get(k, hex(k))}={v}" for k, v in sorted(h4_count.items())))

    if sco_bytes or iso_bytes:
        print("\n=== 0) 同步链路（SCO/ISO）—— 音频常经此传输 ===")
        for name, data in (("SCO", sco_bytes), ("ISO", iso_bytes)):
            if data:
                total = sum(data.values())
                dur = (max(data) - min(data) + 1) if data else 1
                print(f"  {name}: 合计 {total} 字节，约 {total / dur:.0f} B/s，"
                      f"出现时段 +{min(data) - int(t0 // 1e6)}s .. +{max(data) - int(t0 // 1e6)}s")
    else:
        print("[i] 无 SCO/ISO 同步链路数据")
    print()

    print("=== 1) L2CAP 通道建立请求 ===")
    if l2cap_conn:
        for ts, psm, scid, dcid in l2cap_conn[:40]:
            rel = (ts - t0) / 1e6
            if psm is not None:
                print(f"  +{rel:7.2f}s  REQ  PSM 0x{psm:04X} ({PSM_NAMES.get(psm, '未知')})  SCID=0x{scid:04X}")
            else:
                print(f"  +{rel:7.2f}s  RSP  DCID=0x{dcid:04X}  SCID=0x{scid:04X}")
    else:
        print("  （无）")

    print("\n=== 2) 各通道数据量（按字节排序）===")
    known = {0x0001: "信令", 0x0004: "ATT", 0x0005: "LE信令", 0x0006: "SMP", 0x0007: "BR/EDR-SMP"}
    rows = []
    for cid, n in dyn_bytes.most_common(15):
        dur = max((dyn_last[cid] - dyn_first[cid]) / 1e6, 0.001)
        rows.append((cid, n, n / dur))
    for cid, n, rate in rows:
        name = known.get(cid, "动态" if cid >= 0x0040 else "固定")
        print(f"  CID 0x{cid:04X} [{name:6s}]  {n:8d} 字节   平均 {rate:9.0f} B/s")
    if not rows:
        print("  （无 ACL 数据）")

    print("\n=== 3) LE 广播 / 连接 ===")
    if le_adv:
        print(f"  广播报告 {sum(le_adv.values())} 条，来源地址:")
        for a, c in le_adv.most_common(10):
            mark = "  <== 疑似遥控器" if mac_hint and a.lower().endswith(mac_hint.lower()) else ""
            print(f"    {a}  x{c}{mark}")
    else:
        print("  （无 LE 广播）")
    for ts, a in le_conn[:10]:
        print(f"    LE 连接建立 +{(ts - t0) / 1e6:7.2f}s  {a}")

    print("\n=== 4) 高频 HCI 事件（前 12）===")
    names = {0x0E: "Command Complete", 0x05: "Disconn Complete", 0x3E: "LE Meta",
             0xFF: "厂商自定义", 0x13: "Num Completed Packets", 0x10: "Conn Complete",
             0x0C: "Conn Complete?", 0x1B: "Max Slots Change", 0x03: "Conn Request",
             0x08: "Encryption Change", 0x30: "Encryption Key Refresh"}
    for k, v in evt.most_common(12):
        print(f"  {k!s:12s} {names.get(k if isinstance(k, int) else -1, ''):22s} x{v}")

    print("\n=== 5) 最忙通道的每秒时间线 ===")
    if acl:
        # 选总字节最大的非信令通道
        cand = [(cid, n) for cid, n in dyn_bytes.items() if cid not in (0x0001, 0x0005)]
        if cand:
            top_cid = max(cand, key=lambda x: x[1])[0]
            per_sec = collections.Counter()
            for ts, from_ctrl, handle, cid, plen in acl:
                if cid == top_cid:
                    per_sec[int((ts - t0) // 1e6)] += plen
            print(f"  通道 CID 0x{top_cid:04X}（每秒字节数，仅显示非零）:")
            for s in sorted(per_sec):
                bar = "#" * min(int(per_sec[s] / 20), 60)
                print(f"    +{s:4d}s  {per_sec[s]:7d}  {bar}")

    return {"span": span, "dyn_bytes": dyn_bytes, "l2cap_conn": l2cap_conn}


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print(__doc__)
        raise SystemExit(2)
    path = sys.argv[1]
    mac_hint = sys.argv[2] if len(sys.argv) > 2 else "34:83"
    raw = load_btsnoop(path)
    datalink, recs = parse_records(raw)
    analyze(recs, mac_hint)
