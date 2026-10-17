#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""IPRC1000 · HCI 抓包分析（基于 btmon 的解码输出）

为什么不自己解析 btsnoop 二进制：
  `btmon -w` 写出来的是 **datalink=2001（BlueZ monitor 格式）**，不是 HCI H4(1002)；
  而 btmon 自己就能把它解码成人能读的文本（连 "Intel PTT Switch Notification" 这种
  厂商事件都认得出来）。自己反推二进制布局纯属重造轮子，还会把本机的 Intel 厂商事件
  误当成"遥控器发的厂商命令"——这是最容易得出的假阳性。

用法：
    python3 analyze.py capture.btsnoop [report.txt]
需要 btmon（bluez 自带）。读文件一般不需要 root；不行就自动加 sudo -n。
"""
import os
import re
import subprocess
import sys
from collections import Counter

# 蓝牙分配号里的常见 PSM（与"主机↔遥控器"相关的那些）
PSM_NAME = {
    0x0001: "SDP", 0x0003: "RFCOMM", 0x000F: "BNEP",
    0x0011: "HID-Control", 0x0013: "HID-Interrupt",
    0x0017: "AVCTP", 0x0019: "AVDTP", 0x001B: "AVCTP-Browsing",
}


def decode(path):
    """用 btmon 把 btsnoop 解成文本。"""
    for cmd in (["btmon", "-r", path], ["sudo", "-n", "btmon", "-r", path]):
        try:
            p = subprocess.run(cmd, capture_output=True, text=True, timeout=180)
            if p.stdout.strip():
                return p.stdout
        except (FileNotFoundError, subprocess.TimeoutExpired):
            continue
    raise SystemExit("btmon 解码失败：既没有 btmon，也没有 sudo -n 权限")


def main():
    if len(sys.argv) < 2:
        raise SystemExit(__doc__)
    path = sys.argv[1]
    if not os.path.exists(path):
        raise SystemExit("file not found: " + path)

    text = decode(path)
    lines = text.splitlines()

    # ── 采集 ──
    psm = Counter()
    l2ops = Counter()
    sco = 0
    vendor_cmds = Counter()
    vendor_evts = Counter()
    unknown = Counter()
    sdp_records = 0
    conn_events = []

    for i, ln in enumerate(lines):
        s = ln.strip()

        m = re.match(r"PSM:\s*(\d+)\s*\(0x([0-9a-fA-F]+)\)", s)
        if m:
            psm[int(m.group(2), 16)] += 1
            continue

        m = re.match(r"L2CAP:\s*([A-Za-z \-]+?)\s*\(0x([0-9a-fA-F]+)\)", s)
        if m:
            l2ops[(m.group(1).strip(), int(m.group(2), 16))] += 1
            continue

        if s.startswith("SCO Data"):
            sco += 1
            continue

        if s.startswith("SDP"):
            sdp_records += 1

        # 厂商命令/事件：btmon 会把下一行缩进的名称也打出来
        m = re.match(r"> HCI Command: (Vendor|Unknown)\s*\(0x([0-9a-fA-F]+)\)?", s)
        if m:
            name = lines[i + 1].strip() if i + 1 < len(lines) else ""
            if m.group(1) == "Vendor":
                vendor_cmds[(m.group(2), name)] += 1
            else:
                unknown[("cmd", m.group(2), name)] += 1
            continue

        m = re.match(r"> HCI Event: (Vendor|Unknown)\s*\(0x([0-9a-fA-F]+)\)?", s)
        if m:
            name = lines[i + 1].strip() if i + 1 < len(lines) else ""
            if m.group(1) == "Vendor":
                vendor_evts[(name, m.group(2))] += 1
            else:
                unknown[("evt", m.group(2), name)] += 1
            continue

        if "Connected" in s or "Role Change" in s or "Connection Complete" in s:
            conn_events.append(s)

    # ── 报告 ──
    out = []

    def w(x=""):
        out.append(x)

    w("=" * 68)
    w("IPRC1000  HCI probe report  (decoded by btmon)")
    w("=" * 68)
    w(f"file          : {path}")
    w(f"trace lines   : {len(lines)}")
    w(f"SDP records   : {sdp_records}")
    w(f"SCO packets   : {sco}" + ("   <== audio link UP" if sco else "   (no audio: voice key not pressed?)"))
    w("")
    w("-- L2CAP PSMs seen (a custom PSM = a private channel) " + "-" * 14)
    if not psm:
        w("  (none captured)")
    for p, c in psm.most_common():
        tag = PSM_NAME.get(p, "*** NON-STANDARD - INVESTIGATE ***")
        w(f"  PSM 0x{p:04X}  {tag:34s} x{c}")
    w("")
    w("-- L2CAP channel operations " + "-" * 39)
    for (nm, code), c in l2ops.most_common():
        w(f"  0x{code:02X} {nm:26s} x{c}")
    w("")
    w("-- Vendor HCI COMMANDS (host -> controller) " + "-" * 25)
    if not vendor_cmds:
        w("  (none)")
    for (op, nm), c in vendor_cmds.most_common():
        w(f"  0x{op}  {nm or '?'}  x{c}")
    w("")
    w("-- Vendor HCI EVENTS (controller -> host) " + "-" * 27)
    if not vendor_evts:
        w("  (none)")
    for (nm, op), c in vendor_evts.most_common():
        w(f"  0x{op}  {nm or '?'}  x{c}")
    w("    NOTE: Intel controllers emit these on their own (PTT, DDC, ...).")
    w("          Only events that appear *while pressing remote keys* matter.")
    w("")
    w("-- Unknown opcodes " + "-" * 49)
    if not unknown:
        w("  (none)")
    for (kind, op, nm), c in unknown.most_common(12):
        w(f"  {kind} 0x{op}  {nm or '?'}  x{c}")
    w("")
    w("-- verdict " + "-" * 57)
    weird = [p for p in psm if p not in PSM_NAME]
    if weird:
        w("  ** NON-STANDARD PSM FOUND -> a private L2CAP channel exists. **")
    elif psm:
        w("  Only standard PSMs (HID-Control / HID-Interrupt) -> NO private channel.")
        w("  => The remote exposes nothing but standard HID; IR/firmware cannot be")
        w("     driven from the host side over Bluetooth.")
    else:
        w("  No L2CAP traffic captured. Did the remote actually connect? Re-run.")
    w("=" * 68)

    txt = "\n".join(out)
    print(txt)
    if len(sys.argv) > 2:
        open(sys.argv[2], "w", encoding="utf-8").write(txt + "\n")


if __name__ == "__main__":
    main()
