#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""IPRC1000 · RFCOMM 服务通道扫描（1..30）

背景：2026-10-17 实测发现这台遥控器**接受 L2CAP PSM 0x0003 (RFCOMM)**，
但 SDP 里**没有广播任何 RFCOMM 服务**。这正是"隐藏串口服务"的典型形态
（调试口 / 固件通道常常不写进 SDP）。

RFCOMM 的空口连接成功 = L2CAP 那一层通了，但真正的服务在某个 **DLCI 服务通道**
（1..30）。逐个试：谁能连上，谁就是一个隐藏服务。

用法： sudo python3 rfcomm_scan.py [对端MAC]
"""
import socket
import sys

MAC = sys.argv[1] if len(sys.argv) > 1 else "20:C0:47:0A:34:83"


def probe(ch, timeout=1.5):
    s = socket.socket(socket.AF_BLUETOOTH, socket.SOCK_STREAM, socket.BTPROTO_RFCOMM)
    s.settimeout(timeout)
    try:
        s.connect((MAC, ch))
        return True, s
    except Exception as e:                                  # noqa: BLE001
        try:
            s.close()
        except OSError:
            pass
        return False, e


def main():
    print(f"target {MAC}   -- RFCOMM 服务通道 1..30\n")
    hits = []
    for ch in range(1, 31):
        ok, res = probe(ch)
        if ok:
            print(f"  ch {ch:2d}  CONNECTED  <== 有服务")
            # 试着读点东西（有些串口会主动吐 banner）
            try:
                res.settimeout(1.0)
                res.send(b"\r\n")
                data = res.recv(128)
                if data:
                    print(f"           读到 {len(data)} 字节: {data[:64]!r}")
                else:
                    print("           连接正常但无数据（可能是等待特定命令）")
            except Exception as e:                          # noqa: BLE001
                print(f"           (无数据: {type(e).__name__})")
            finally:
                try:
                    res.close()
                except OSError:
                    pass
            hits.append(ch)
        else:
            kind = type(res).__name__
            msg = str(res)
            # 只打非常见的（"拒绝"是预期，噪音太多）
            if "refused" not in msg.lower() and "111" not in msg:
                print(f"  ch {ch:2d}  {kind}: {msg}")

    print()
    if hits:
        print(f">>> 可达通道: {hits}")
        print("    => 这些是 SDP 里没广播的隐藏 RFCOMM 服务，值得逐个抓数据。")
    else:
        print(">>> 1~30 全部拒绝 —— RFCOMM 只在 L2CAP 层可达，没有打开任何服务通道。")
        print("    （这也说明 PSM 0x0003 的『连接成功』只是 L2CAP 层握手，没有上层服务）")


if __name__ == "__main__":
    main()
