import struct
import collections
import sys

OPCODES = {
    0x0405: "Create Connection", 0x0406: "Disconnect", 0x0408: "Add SCO Connection",
    0x0409: "Cancel Create Connection", 0x040B: "Accept Connection Request",
    0x040C: "Reject Connection Request", 0x041B: "Authentication Requested",
    0x041D: "Set Connection Encryption", 0x0428: "Setup Synchronous Connection",
    0x0429: "Accept Synchronous Connection Request",
    0x042A: "Reject Synchronous Connection Request",
    0x043D: "Enhanced Setup Synchronous Connection",
    0x043E: "Enhanced Accept Synchronous Connection Request",
    0x0401: "Inquiry", 0x0402: "Inquiry Cancel", 0x0403: "Periodic Inquiry Mode",
    0x0C01: "Set Event Mask", 0x0C03: "Reset", 0x0C05: "Set Event Filter",
    0x0C24: "Set Event Filter(v2)", 0x0C33: "Host Buffer Size",
    0x0C52: "Set Event Mask Page 2", 0x0C56: "Set Event Mask Page 2?",
    0x1001: "Read Local Version", 0x1002: "Read Local Supported Commands",
    0x1005: "Read Buffer Size", 0x1009: "Read BD_ADDR",
    0x2002: "Read Local Supported Features", 0x2005: "Read Local Name",
    0x200E: "Read Local Extended Features", 0x2013: "Write Simple Pairing Mode",
    0x2016: "Write LE Host Support", 0x201C: "Read Local Extended Features?",
    0x3F001: "LE Set Event Mask",
}

SYNC_OPS = {0x0408, 0x0428, 0x0429, 0x042A, 0x043D, 0x043E}


def main(path):
    d = open(path, "rb").read()
    off = 16
    recs = []
    while off + 24 <= len(d):
        ol, il, fl, dr, ts = struct.unpack_from(">IIIIq", d, off)
        off += 24
        recs.append((fl, ts, d[off:off + il]))
        off += il

    cmds = collections.Counter()
    sync_hits = []
    t0 = recs[0][1] if recs else 0
    for fl, ts, f in recs:
        if f and f[0] == 0x01 and len(f) >= 3:
            op = struct.unpack_from("<H", f, 1)[0]
            cmds[op] += 1
            if op in SYNC_OPS:
                sync_hits.append((op, (ts - t0) / 1e6))

    print("=== 主机发出的 HCI 命令 ===")
    for op, n in cmds.most_common(40):
        ogf, ocf = (op >> 10) & 0x3F, op & 0x3FF
        name = OPCODES.get(op, f"OGF=0x{ogf:02X} OCF=0x{ocf:03X}")
        print(f"  0x{op:04X}  {name:42s} x{n}")

    print()
    print("=== 同步（SCO/eSCO）音频链路建立命令 ===")
    if sync_hits:
        for op, t in sync_hits:
            print(f"  +{t:.2f}s  0x{op:04X} {OPCODES.get(op, '')}")
    else:
        print("  无 —— 主机从未建立任何同步音频链路")


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "btsnoop_hci.log")
