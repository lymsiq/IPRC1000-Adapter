#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""IPRC1000 · Linux 按键映射探针

读 /dev/input/eventX（需要 root），记录：
  · EV_MSC / MSC_SCAN  → **HID usage**（蓝牙 HID 设备会把原始 usage 报在这里）
  · EV_KEY              → Linux keycode（内核按标准 HID→keycode 表换算的结果）

输出「usage → keycode」对照表，用来验证项目里那张 23 键表在 Linux 上是否一致。

用法： sudo python3 keymap_probe.py [秒数]     默认 90 秒
"""
import glob
import re
import signal
import struct
import sys
import time

DEADLINE = None


def load_names():
    names = {}
    try:
        for ln in open("/usr/include/linux/input-event-codes.h", encoding="utf-8", errors="ignore"):
            m = re.match(r"#define\s+KEY_([A-Z0-9_]+)\s+(\d+)\s*$", ln.strip())
            if m:
                names.setdefault(int(m.group(2)), m.group(1))
    except OSError:
        pass
    return names


def find_device():
    for e in sorted(glob.glob("/sys/class/input/event*")):
        try:
            if open(e + "/device/name").read().strip() == "IPRC1000":
                return "/dev/input/" + e.rsplit("/", 1)[-1]
        except OSError:
            pass
    sys.exit("IPRC1000 not found (is it connected?)")


def main():
    secs = int(sys.argv[1]) if len(sys.argv) > 1 else 90
    names = load_names()
    dev = find_device()
    print(f"device   : {dev}", flush=True)
    print(f"duration : {secs}s   -- press the remote keys now", flush=True)

    signal.signal(signal.SIGALRM, lambda *_: sys.exit(0))
    signal.alarm(secs)

    pairs = {}           # usage -> {keycode: count}
    keycount = {}
    scan_pending = None
    t0 = time.time()
    try:
        with open(dev, "rb") as f:
            while True:
                b = f.read(24)
                if len(b) < 24:
                    break
                _s, _u, typ, code, val = struct.unpack("<qqHHi", b)
                if typ == 4 and code == 4:              # EV_MSC / MSC_SCAN
                    scan_pending = val
                elif typ == 1 and val == 1:             # EV_KEY press
                    keycount[code] = keycount.get(code, 0) + 1
                    if scan_pending is not None:
                        pairs.setdefault(scan_pending, {})
                        pairs[scan_pending][code] = pairs[scan_pending].get(code, 0) + 1
                        scan_pending = None
    except SystemExit:
        pass

    print(f"\ncaptured in {time.time() - t0:.0f}s\n")
    print("HID usage  ->  Linux keycode            (presses)")
    print("-" * 58)
    if not pairs:
        print("  (nothing captured -- were the keys pressed?)")
    for usage in sorted(pairs):
        for kc, c in sorted(pairs[usage].items(), key=lambda x: -x[1]):
            print(f"  0x{usage:02X}      ->  KEY_{names.get(kc, kc):<18s} ({kc:3d})  x{c}")
    print("-" * 58)
    print("keys seen:", len(keycount), " distinct codes")


if __name__ == "__main__":
    main()
