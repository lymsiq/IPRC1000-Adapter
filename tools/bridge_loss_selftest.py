#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""语音桥「丢帧定位仪表」回归自检 —— 用**合成回放流**验证桥能不能分辨故障类型。

为什么要有它：2026-10-16 出现「连续语音之后按键没声音」，日志里只查到
`丢帧 511` 这一个**累计数**，既分不清是 ETW 丢了事件、还是音频分片长度变了，
也看不出哪一秒开始丢 —— 只能靠猜（两个假设都能解释 511）。

现在桥里加了三类互相独立的计数，这个脚本就负责证明**它们真的分得开**：
  · seq 跳号      → 真丢帧（遥控器发过、我们没收到）
  · 组帧失败      → 首片到了、配对的续片没到
  · 未识别分片    → 分片长度/标志变了（不是丢，是"变了"）—— 带 (flag, dlen) 分布
  · 续片被截断    → 分片在了但负载不足 313 B（老实现里这条路径没有任何计数）
  · 首片被截断    → `MaxEtwBytes` 没生效的老 bug（历史事故）

做法参考 macOS 版（`lymsiq/IPRC1000-Mac`）：他们的 `--follow-capture` 也是用
**合成抓包**做端到端自测（"写进去多少帧就解出多少帧"），而不是等真机复现。

用法：
    python tools/bridge_loss_selftest.py

⚠ 自检会把 `APPDATA` 指到临时目录，**不污染真机的 bridge.log / bridge-stats.json**。
"""
import os
import re
import struct
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
EXE = os.path.join(HERE, "etw_consumer", "target", "release", "etw_consumer.exe")

FRAME_HDR = 13
PCM_BYTES = 640
PAYLOAD = FRAME_HDR + PCM_BYTES          # 653
ACL_FIRST = 344                          # 首片：L2CAP 头(4) + 340
ACL_CONT = PAYLOAD - (ACL_FIRST - 4)     # 313


def build_frame(seq, faulty_cont_len=None, truncate_cont=0):
    """造一帧的 653 字节 L2CAP 负载。

    帧头 `A1 F7 01 <seq> 00 <roll> <n> <cnt> 00 00 00 40 01`
    —— 第 5/6 字节逐帧在变（0x42,0x82,… / 0,9,18,…），照"固定 00 81 00"匹配会一帧都命不中。
    """
    roll = (0x42 + seq * 0x40) & 0xFF
    n = (seq * 9) & 0xFF
    hdr = bytes([0xA1, 0xF7, 0x01, seq & 0xFF, 0x00, roll, n, 0x00,
                 0x00, 0x00, 0x00, 0x40, 0x01])
    # 640 字节 PCM：随便给点内容，计数器不关心内容
    pcm = bytes(((i * 37 + seq * 11) & 0xFF) for i in range(PCM_BYTES))
    payload = hdr + pcm
    assert len(payload) == PAYLOAD
    return payload


def emit_frame(out, seq, faulty_cont_len=None, truncate_cont=0):
    """把一帧写成回放格式的两条 ACL 分片。

    回放格式：repeat { u8 flag; u8 pad; u16 dlen(LE); u32 dt_ms(LE); u16 acl_len(LE); u8 acl[] }
    `acl` 从 L2CAP 头开始（= 真机上 BIP_Data 去掉 4 字节 ACL 头之后的部分）。
    """
    payload = build_frame(seq)
    l2cap_hdr = struct.pack("<HH", PAYLOAD, 0x0041)   # 长度 653 / CID 0x41

    first = l2cap_hdr + payload[:ACL_FIRST - 4]
    assert len(first) == ACL_FIRST
    out += _rec(2, ACL_FIRST, first)

    cont = payload[ACL_FIRST - 4:]
    assert len(cont) == ACL_CONT
    dlen = faulty_cont_len if faulty_cont_len is not None else ACL_CONT
    if truncate_cont:
        cont = cont[:truncate_cont]
    out += _rec(1, dlen, cont)
    return out


def _rec(flag, dlen, acl):
    return struct.pack("<BBHIH", flag, 0, dlen, 0, len(acl)) + acl


def scenario_normal(n=50):
    out = b""
    for i in range(n):
        out = emit_frame(out, i)
    return out


def scenario_seq_jump(n=50, at=25, gap=5):
    """第 `at` 帧的 seq 直接跳到 `at+gap` —— 模拟"真丢了 gap 帧"。"""
    out = b""
    seq = 0
    for i in range(n):
        out = emit_frame(out, seq & 0xFF)
        seq += gap if i == at else 1
    return out


def scenario_cont_len_changed(n=50, lo=20, hi=40, bad_len=330):
    """续片长度变了（假设②）：首片仍 344，续片不是 313 → 组帧失败 + 未识别分片。

    这正是 2026-10-16 事故的指纹：`lost_cont` 一帧一帧涨，而 frames 不动。
    """
    out = b""
    for i in range(n):
        out = emit_frame(out, i, faulty_cont_len=bad_len if lo <= i < hi else None)
    return out


def scenario_cont_truncated(n=50, lo=10, hi=20, keep=200):
    """续片在了但被 ETW 截断（负载 < 313）—— 老实现里这条路径完全没有计数。"""
    out = b""
    for i in range(n):
        out = emit_frame(out, i, truncate_cont=keep if lo <= i < hi else 0)
    return out


def scenario_first_truncated(n=50, lo=10, hi=20):
    """首片被截断（`MaxEtwBytes` 没生效的历史 bug：只带 4 字节负载）。"""
    out = b""
    for i in range(n):
        payload = build_frame(i)
        if lo <= i < hi:
            out += _rec(2, ACL_FIRST, b"\x8d\x02\x41\x00")   # 只剩 L2CAP 头
        else:
            out = emit_frame(out, i)
    return out


def parse(text):
    """从桥的输出里抠出各类计数。"""
    g = {}
    m = re.search(r"音频帧 (\d+)\s+被截断首片 (\d+)", text)
    if m:
        g["frames"] = int(m.group(1))
        g["trunc_first"] = int(m.group(2))
    m = re.search(
        r"组帧失败\(续片没到\) (\d+) · seq跳号 (\d+) 次（跳过 (\d+) 帧）· seq重复 (\d+)"
        r"\s*· 续片被截断 (\d+) · 未识别分片 (\d+) · 帧头异常 (\d+)", text)
    if m:
        g.update(lost_cont=int(m.group(1)), seq_jumps=int(m.group(2)),
                 seq_lost=int(m.group(3)), seq_dup=int(m.group(4)),
                 trunc_cont=int(m.group(5)), odd_frag=int(m.group(6)),
                 hdr_odd=int(m.group(7)))
    m = re.search(r"未识别分片分布: (.+)", text)
    g["odd_kinds"] = m.group(1).strip() if m else ""
    return g


def run(name, blob, expect):
    tmp = tempfile.mkdtemp(prefix="iprc-selftest-")
    rp = os.path.join(tmp, "replay.bin")
    with open(rp, "wb") as f:
        f.write(blob)
    # ★ APPDATA 指到临时目录：自检不污染真机的 bridge.log / bridge-stats.json
    env = dict(os.environ)
    env["APPDATA"] = tmp
    p = subprocess.run(
        [EXE, "--replay", rp, "--no-feed", "--fast", "--key-udp", ""],
        capture_output=True, env=env, timeout=120)
    text = p.stdout.decode("utf-8", "replace") + p.stderr.decode("utf-8", "replace")
    got = parse(text)

    bad = []
    for k, want in expect.items():
        if k == "odd_kinds_has":
            if want not in got.get("odd_kinds", ""):
                bad.append(f"{k}: 期望包含 {want!r}，实际 {got.get('odd_kinds','')!r}")
            continue
        if got.get(k) != want:
            bad.append(f"{k}: 期望 {want}，实际 {got.get(k)}")
    status = "✓" if not bad else "✗"
    print(f"  {status} {name}")
    detail = " ".join(
        f"{k}={got.get(k)}" for k in
        ("frames", "lost_cont", "seq_jumps", "seq_lost", "trunc_cont",
         "odd_frag", "trunc_first", "hdr_odd") if k in got)
    print(f"       {detail}")
    if got.get("odd_kinds"):
        print(f"       未识别分片: {got['odd_kinds']}")
    for b in bad:
        print(f"       ★ {b}")
    return not bad


# 断言的语义（写清楚，免得下一个人把"期望"改错 —— 第一版就是这么改错的）：
#   · `seq_lost`  = **输出流里少了多少帧**（不论什么原因：真丢事件、组帧失败、被截断）
#                   seq 从 a 跳到 b ⇒ 中间少了 (b-a-1) 帧
#   · `lost_cont` = 首片到了、配对的续片没到（组装阶段）
#   · `odd_frag`  = 分片长度/标志不认识（**"变了"而不是"丢了"**，带 flag/dlen 分布）
#   · `trunc_cont`/`trunc_first` = 分片在了但负载被 ETW 截断
# 四者互相独立 ⇒ 一眼能判故障在哪一层（这就是这套仪表的意义）。
CASES = [
    ("① 正常 50 帧：一帧不丢", scenario_normal(50),
     dict(frames=50, lost_cont=0, seq_jumps=0, seq_lost=0, odd_frag=0, hdr_odd=0)),

    ("② seq 跳号：真丢帧（遥控器发过、我们没收到）", scenario_seq_jump(50, 25, 5),
     # 25→30 是 5 步，中间 **少了 4 帧**（26/27/28/29）
     dict(frames=50, lost_cont=0, seq_jumps=1, seq_lost=4, odd_frag=0)),

    ("③ 续片长度变了（今天事故的指纹）：组帧失败 + 未识别分片",
     scenario_cont_len_changed(50, 20, 40, 330),
     # 这 20 帧没能组出来 ⇒ 输出流里也少了 20 帧（seq 从 19 直接跳到 40）
     dict(frames=30, lost_cont=20, seq_jumps=1, seq_lost=20,
          odd_frag=20, odd_kinds_has="flag=1 dlen=330")),

    ("④ 续片被截断（负载不足）：以前完全没有计数",
     scenario_cont_truncated(50, 10, 20, 200),
     dict(frames=40, trunc_cont=10, lost_cont=0, odd_frag=0, seq_lost=10)),

    ("⑤ 首片被截断（MaxEtwBytes 没生效的老 bug）", scenario_first_truncated(50, 10, 20),
     dict(frames=40, trunc_first=10, odd_frag=0, lost_cont=0, seq_lost=10)),
]


def main():
    if not os.path.exists(EXE):
        print(f"★ 找不到 {EXE}，先 cargo build --release")
        return 2
    print("语音桥「丢帧定位仪表」回归自检")
    print("=" * 62)
    ok = True
    for name, blob, expect in CASES:
        ok &= run(name, blob, expect)
    print()
    print("⇒ " + ("全部通过 ✓ —— 三类故障确实分得开" if ok else "★ 有断言失败"))
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
