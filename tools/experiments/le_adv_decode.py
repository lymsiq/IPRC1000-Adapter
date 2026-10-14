"""LE Extended Advertising Report 解码 —— 判断遥控器是否在 BLE 侧广播

BT 5.0 LE Meta 子事件 0x0D（LE Extended Advertising Report）结构：
  [0] subevent(0x0D) [1] num_reports
  每条报告：
    [+0..1]  event_type       [+2]    address_type
    [+3..8]  address(6B, 小端) [+9]    primary_phy
    [+10]    secondary_phy    [+11]   advertising_sid
    [+12]    tx_power         [+13]   rssi
    [+14..15] periodic_adv_interval
    [+16]    direct_addr_type [+17..22] direct_address
    [+23]    data_length      [+24..]  data
"""
import struct
import sys
import collections

# 兼容 0x02 传统广播报告：subevent(1) num(1) event_type(1) addr_type(1) addr(6) data_len(1) data


def parse(path, mac_suffix="34:83"):
    d = open(path, "rb").read()
    off = 16
    recs = []
    while off + 24 <= len(d):
        ol, il, fl, dr, ts = struct.unpack_from(">IIIIq", d, off)
        off += 24
        recs.append((fl, ts, d[off:off + il]))
        off += il

    adv = collections.Counter()
    adv_data = {}
    ext_count = legacy_count = 0
    t0 = recs[0][1] if recs else 0

    for fl, ts, f in recs:
        if not f or f[0] != 0x04:
            continue
        body = f[1:]
        # HCI 事件: [0]=事件码(0x3E) [1]=参数总长 [2]=LE 子事件 [3..]=子事件参数
        if len(body) < 4 or body[0] != 0x3E:
            continue
        sub = body[2]
        if sub == 0x0D:  # LE Extended Advertising Report
            ext_count += 1
            num = body[3] if len(body) > 3 else 0
            p = 4
            for _ in range(max(num, 0)):
                if p + 24 > len(body):
                    break
                # [+0..1] event_type [+2] addr_type [+3..8] address
                addr = body[p + 3:p + 9][::-1].hex(":")
                dlen = body[p + 23] if p + 23 < len(body) else 0
                data = body[p + 24:p + 24 + dlen]
                adv[addr] += 1
                if addr not in adv_data and data:
                    adv_data[addr] = (data.hex(" "), (ts - t0) / 1e6)
                p += 24 + dlen
        elif sub == 0x02:  # legacy LE Advertising Report
            legacy_count += 1
            num = body[3]
            p = 4
            for _ in range(num):
                if p + 9 > len(body):
                    break
                addr = body[p + 1:p + 7][::-1].hex(":")
                dlen = body[p + 7]
                data = body[p + 8:p + 8 + dlen]
                adv[addr] += 1
                if addr not in adv_data and data:
                    adv_data[addr] = (data.hex(" "), (ts - t0) / 1e6)
                p += 9 + dlen

    print(f"LE 扩展广播报告: {ext_count} 个事件 | 传统广播报告: {legacy_count} 个事件")
    print(f"去重后广播源地址数: {len(adv)}")
    print()
    print(f"{'地址':<20}{'次数':>6}   广告数据（前 12 字节）")
    hits = []
    for a, c in adv.most_common():
        data, t = adv_data.get(a, ("", 0))
        mark = ""
        if mac_suffix and a.lower().endswith(mac_suffix):
            mark = "  <== 遥控器地址匹配!"
            hits.append((a, c, data, t))
        print(f"{a:<20}{c:>6}   {data[:36]}{mark}")
    print()
    if hits:
        print("发现遥控器 BLE 广播:", hits)
    else:
        print(f"未发现地址以 {mac_suffix} 结尾的广播源 → 遥控器不在 BLE 侧广播")
    print()
    print("参考：遥控器 BR/EDR 地址为 20:c0:47:0a:34:83（BLE 地址通常相同或为随机静态地址）")


if __name__ == "__main__":
    parse(sys.argv[1] if len(sys.argv) > 1 else "btsnoop_hci.log",
          sys.argv[2] if len(sys.argv) > 2 else "34:83")
