#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把 btmon 解码文本里 L2CAP 的「Connection Request(PSM) ↔ Response(Result)」
按 ident 配成一张表 —— 这是"哪些 PSM 被接受/被拒"的最硬证据。

用法： python3 psm_results.py trace.txt
"""
import re
import sys

REQ = re.compile(r"L2CAP: Connection Request \(0x02\) ident (\d+)")
PSM = re.compile(r"PSM: (\d+) \(0x([0-9a-fA-F]+)\)")
RSP = re.compile(r"L2CAP: Connection Response \(0x03\) ident (\d+)")
RES = re.compile(r"Result: (.+?) \(0x([0-9a-fA-F]+)\)")

RESULT_NAME = {
    0x0000: "Connection successful",
    0x0001: "Connection pending",
    0x0002: "PSM not supported  ← 明确拒绝",
    0x0003: "Security block",
    0x0004: "No resources (多为该通道已被占用)",
}


def main():
    lines = open(sys.argv[1], encoding="utf-8", errors="ignore").read().splitlines()
    req, rsp = {}, {}
    need_psm = None
    for ln in lines:
        m = REQ.search(ln)
        if m:
            need_psm = int(m.group(1))
            continue
        if need_psm is not None:
            m = PSM.search(ln)
            if m:
                req[need_psm] = int(m.group(2), 16)
                need_psm = None
                continue
        m = RSP.search(ln)
        if m:
            cur = int(m.group(1))
            continue
        if "Result:" in ln:
            m = RES.search(ln)
            if m and "cur" in dir():
                rsp[cur] = int(m.group(2), 16)

    print(f"{'PSM':>7}  {'结果码':>6}  说明")
    print("-" * 62)
    for ident in sorted(req):
        psm = req[ident]
        code = rsp.get(ident)
        if code is None:
            txt = "（没抓到响应）"
            print(f"  0x{psm:04X}       -    {txt}")
        else:
            print(f"  0x{psm:04X}    0x{code:04X}  {RESULT_NAME.get(code, '?')}")
    print("-" * 62)
    ok = [p for i, p in req.items() if rsp.get(i) == 0]
    printf_ok = ", ".join(f"0x{p:04X}" for p in sorted(ok))
    print(f"被接受的 PSM: {printf_ok or '无'}")


if __name__ == "__main__":
    main()
