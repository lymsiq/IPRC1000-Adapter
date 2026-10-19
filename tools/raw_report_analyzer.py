#!/usr/bin/env python3
"""
遥控器原始报文分析器 —— 用于判定"哪些 usage 是真按键、哪些是幽灵"。

输入：`%APPDATA%\\IPRC1000 Adapter\\bridge.log`（需开启诊断日志，桥才会输出 `[RAW]` 行）
输出：按时间分组的报文表 + 每个 usage 的出现统计 + 疑似幽灵键结论。

用法：
    python tools/raw_report_analyzer.py                # 分析最近 10 分钟
    python tools/raw_report_analyzer.py --since 13:00  # 指定起始时间
    python tools/raw_report_analyzer.py --all          # 全部
"""
import argparse
import os
import re
import sys
from collections import Counter, defaultdict

RAW_RE = re.compile(r"^(\d{2}:\d{2}:\d{2}\.\d{3})\s+\[RAW\] len=(\d+) usages=\[([^\]]*)\]")

# 已知的键名（与 Keys.cs 对应），仅用于显示
NAMES = {
    0x04: "CH+", 0x05: "CH-", 0x07: "EXIT/HOME", 0x08: "收藏", 0x09: "BACK",
    0x0B: "INFO", 0x0C: "?幽灵?", 0x0D: "账户", 0x10: "voice/语音", 0x11: "MENU",
    0x16: "方块", 0x17: "PWR", 0x19: "快进", 0x28: "OK", 0x31: "播放暂停",
    0x36: "快退", 0x4F: "RIGHT", 0x50: "LEFT", 0x51: "DOWN", 0x52: "UP",
    0x01: "?01?",
}


def parse(path, since=None):
    rows = []
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            m = RAW_RE.match(line)
            if not m:
                continue
            t = m.group(1)
            if since and t < since:
                continue
            usages = [int(x, 16) for x in m.group(3).split() if x]
            rows.append((t, usages))
    return rows


def label(u):
    return NAMES.get(u, f"0x{u:02X}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--log", default=None)
    ap.add_argument("--since", default=None, help="只分析 HH:MM:SS 之后的记录")
    ap.add_argument("--all", action="store_true")
    ap.add_argument("--gap", type=float, default=0.35, help="超过多少秒视为新的一次按键（默认 0.35s）")
    a = ap.parse_args()

    log = a.log or os.path.join(os.path.expandvars(r"%APPDATA%\IPRC1000 Adapter"), "bridge.log")
    if not os.path.exists(log):
        print(f"找不到 {log}\n（桥没开诊断日志时不会写 [RAW]）")
        return 1

    rows = parse(log, None if a.all else a.since)
    if not rows:
        print("没有 [RAW] 记录。请确认：① 诊断日志已开启；② 期间按过遥控器。")
        return 1

    # 按时间间隔切分成"一次按键会话"
    def secs(t):
        h, m, s = t.split(":")
        return int(h) * 3600 + int(m) * 60 + float(s)

    groups, cur = [], [rows[0]]
    for prev, nxt in zip(rows, rows[1:]):
        if secs(nxt[0]) - secs(prev[0]) > a.gap:
            groups.append(cur)
            cur = [nxt]
        else:
            cur.append(nxt)
    groups.append(cur)

    print(f"共 {len(rows)} 条 [RAW]，切成 {len(groups)} 组（相邻报文间隔 > {a.gap}s 视为另一次按键）\n")

    # 逐组打印
    print("=" * 78)
    print("每组时序（空 8 位报文按什么都不按 = 全部抬起）")
    print("=" * 78)
    for i, g in enumerate(groups, 1):
        t0 = g[0][0]
        seq = []
        for t, us in g:
            if not us:
                seq.append("空")
            else:
                seq.append("+".join(label(u) for u in us))
        # 压缩连续重复
        comp, last, n = [], None, 0
        for s in seq:
            if s == last:
                n += 1
            else:
                if last is not None:
                    comp.append(last if n == 1 else f"{last}×{n}")
                last, n = s, 1
        comp.append(last if n == 1 else f"{last}×{n}")
        print(f"[{i:02d}] {t0}  ({len(g)} 条)  " + " → ".join(comp[:14]) + (" …" if len(comp) > 14 else ""))

    # usage 统计
    cnt = Counter()
    for _, us in rows:
        for u in us:
            cnt[u] += 1
    print("\n" + "=" * 78)
    print("各 usage 出现次数（越少越可疑；只在按住期间出现的多半是幽灵）")
    print("=" * 78)
    for u, n in cnt.most_common():
        print(f"  0x{u:02X}  {n:5d} 次   {label(u)}")

    # 幽灵候选：出现在"有真实按键的报文"里、且从未单独出现过
    alone = defaultdict(int)
    together = defaultdict(int)
    for _, us in rows:
        real = [u for u in us if u not in (0x0C, 0x01)]
        for u in us:
            if len(us) == 1:
                alone[u] += 1
            elif u in (0x0C, 0x01):
                together[u] += 1
    print("\n" + "=" * 78)
    print("幽灵判定")
    print("=" * 78)
    for u in (0x0C, 0x01, 0x05):
        if cnt.get(u):
            verdict = ("疑似幽灵（常与其他键同现，几乎不单独出现）"
                       if alone.get(u, 0) <= max(1, together.get(u, 0) // 10)
                       else "看起来是**真实按键**（会单独出现）")
            print(f"  0x{u:02X}: 总 {cnt[u]} 次 · 单独 {alone.get(u,0)} 次 · 与其他键同现 {together.get(u,0)} 次"
                  f"  ⇒ {verdict}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
