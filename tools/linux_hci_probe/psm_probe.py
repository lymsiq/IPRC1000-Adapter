#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""IPRC1000 · L2CAP PSM 主动探测

有些设备的私有通道**不写进 SDP**，但会响应直连——SDP 干净不代表没有。
这个脚本对一串候选 PSM 逐个发起 L2CAP 连接，看哪些"意外地通了"。

判读：
  · 标准服务（SDP/HID/RFCOMM…）本来就该通 → 不算发现
  · **厂商 PSM 通了 = 有隐藏通道** → 这才是有价值的结果
  · 大部分应该报 "PSM not supported" 之类的拒绝

用法： sudo python3 psm_probe.py [对端MAC]
"""
import socket
import sys

MAC = sys.argv[1] if len(sys.argv) > 1 else "20:C0:47:0A:34:83"

# (PSM, 说明, 是否标准)
CANDIDATES = [
    (0x0001, "SDP",                      True),
    (0x0003, "RFCOMM",                   True),
    (0x000F, "BNEP",                     True),
    (0x0011, "HID Control",              True),
    (0x0013, "HID Interrupt",            True),
    (0x0017, "AVCTP",                    True),
    (0x0019, "AVDTP",                    True),
    (0x001B, "AVCTP Browsing",           True),
    # ↓ 以下都是"不该存在"的：通了就是发现
    (0x0081, "OBEX",                     False),
    (0x00FF, "generic vendor 0xFF",      False),
    (0x0101, "L2CAP test?",              False),
    (0x1001, "vendor 0x1001",            False),
    (0x1003, "vendor 0x1003",            False),
    (0x1005, "vendor 0x1005",            False),
    (0xA000, "vendor 0xA000",            False),
    (0xF000, "vendor 0xF000",            False),
    (0xF001, "vendor 0xF001",            False),
]


def try_connect(psm, timeout=4.0):
    s = socket.socket(socket.AF_BLUETOOTH, socket.SOCK_SEQPACKET, socket.BTPROTO_L2CAP)
    s.settimeout(timeout)
    try:
        s.connect((MAC, psm))
        return True, "CONNECTED"
    except OSError as e:
        return False, str(e)
    except Exception as e:                     # noqa: BLE001
        return False, f"{type(e).__name__}: {e}"
    finally:
        try:
            s.close()
        except OSError:
            pass


def main():
    print(f"target: {MAC}")
    print(f"{'PSM':>7}  {'name':20s} {'std':4s} result")
    print("-" * 72)
    hits = []
    for psm, name, std in CANDIDATES:
        ok, msg = try_connect(psm)
        flag = ""
        if ok and not std:
            flag = "   <== !!! UNEXPECTED CHANNEL"
            hits.append((psm, name))
        elif ok:
            flag = "   (expected)"
        print(f"  0x{psm:04X}  {name:20s} {'yes' if std else 'no ':4s} {msg}{flag}")
    print("-" * 72)
    if hits:
        print("\n>>> 发现非标准可达 PSM：")
        for psm, name in hits:
            print(f"    0x{psm:04X}  {name}")
        print("    => 这就是私有通道，值得进一步抓它的负载。")
    else:
        print("\n>>> 只有标准 PSM 可达 —— 与 SDP 结论一致，没有隐藏通道。")


if __name__ == "__main__":
    main()
