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


def emit_frame(out, seq, split=None, truncate_cont=0, force_len=None, cid=0x0041):
    """把一帧写成回放格式的若干条 ACL 分片。

    回放格式：repeat { u8 flag; u8 pad; u16 dlen(LE); u32 dt_ms(LE); u16 acl_len(LE); u8 acl[] }
    `acl` 从 L2CAP 头开始（= 真机上 BIP_Data 去掉 4 字节 ACL 头之后的部分）。

    `split` 指定起始分片之后的切法，默认 `[313]`（一片续片）。
    ★ 真机实测见过 `[290, 23]` —— 同一个 657 字节 PDU 被拆成三片。
    """
    payload = build_frame(seq)
    l2cap_hdr = struct.pack("<HH", force_len if force_len is not None else PAYLOAD, cid)

    whole = l2cap_hdr + payload            # 657 字节
    split = split or [ACL_CONT]

    first = whole[:ACL_FIRST]
    assert len(first) == ACL_FIRST
    out += _rec(2, ACL_FIRST, first)

    rest = whole[ACL_FIRST:]
    for i, n in enumerate(split):
        chunk = rest[:n]
        rest = rest[n:]
        if truncate_cont and i == 0:
            chunk = chunk[:truncate_cont]
        out += _rec(1, n, chunk)
    assert not rest, f"切法没覆盖完（剩 {len(rest)} 字节）"
    return out


def _rec(flag, dlen, acl, dt_ms=0):
    return struct.pack("<BBHIH", flag, 0, dlen, dt_ms, len(acl)) + acl


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


def scenario_three_frag_split(n=50):
    """★ 同一个 657 字节 PDU 被拆成**三片**：344 + 290 + 23。

    这是 2026-10-16 13:22 那次"卡了一分钟、语音没电平"的真机指纹
    （日志里 `flag=1 dlen=290` 与 `flag=1 dlen=23` 各 512 次，而 `seq跳号 = 0`）。
    老解析器写死"续片必须 313"，于是**整段完好的音频被我们自己扔掉**。
    现在按 PB 标志 + L2CAP 长度组帧 ⇒ **一帧都不该丢**。
    """
    out = b""
    for i in range(n):
        out = emit_frame(out, i, split=[290, 23])
    return out


def emit_frame_at(out, seq, dt_ms=0, **kw):
    """同 emit_frame，但把间隔 dt_ms 写进**每一片**（回放按它判会话边界）。"""
    blob = emit_frame(b"", seq, **kw)
    out2, off = b"", 0
    while off + 10 <= len(blob):
        flag = blob[off]
        dlen = struct.unpack_from("<H", blob, off + 2)[0]
        acl_len = struct.unpack_from("<H", blob, off + 8)[0]
        out2 += struct.pack("<BBHIH", flag, 0, dlen, dt_ms, acl_len) + blob[off + 10:off + 10 + acl_len]
        off += 10 + acl_len
    return out + out2


def scenario_two_sessions(n=25, gap_ms=1000):
    """★ 两段会话，中间停 1 秒：**不许把会话边界记成"丢帧"**。

    真机实测：12 次会话 → 12~13 次「seq 跳号」，累计"跳过 1500 帧"，
    于是界面「丢失帧数」误报成上千帧 —— 而同期「组帧失败」一直是 0，音频一帧没丢。
    根因是"清序号基准"写在发 CLEAR 那一段（函数后半），而序号比较在函数**最前面**
    ⇒ 每个会话的第一帧都拿上一段的 seq 去比。这条用例就是钉住这个位置。
    """
    out = b""
    seq = 0
    for i in range(n):
        out = emit_frame_at(out, seq & 0xFF)
        seq += 1
    # 停 1 秒（> IDLE_END_MS）：遥控器这边的序号可能已经往前跑了一段，
    # 但那段时间**根本没在收音**，所以那不是丢帧。
    seq += 137
    for i in range(n):
        out = emit_frame_at(out, seq & 0xFF, dt_ms=gap_ms if i == 0 else 0)
        seq += 1
    return out


def emit_hid_report(out, usage=0x10):
    """一条按键报告：单分片，ACL 负载 14 字节（L2CAP 10 + 报告头 a1 01 + 6 个 usage 槽）。

    ★ 重写音频解析器（改成按 PB 标志组帧）之后，**必须**证明按键路径没被弄坏 ——
      音频与按键同走 L2CAP CID 0x41，区分只能靠 L2CAP 长度（653 vs 10）。
    """
    acl = struct.pack("<HH", 10, 0x0041) + bytes([0xA1, 0x01, 0x00, 0x00, usage, 0, 0, 0, 0, 0])
    assert len(acl) == 14
    return _rec(2, 14, acl)


def emit_hid_multi(out, usages, cid=0x0041, dt_ms=0):
    """一条按键报告，**6 个 usage 槽可填多个**（最多 6 个）。

    遥控器上同时按两个键时就是这个形状（实测 `back+menu` → `a1 01 00 00 09 11 ...`）。
    """
    slots = list(usages) + [0] * (6 - len(usages))
    acl = struct.pack("<HH", 10, cid) + bytes([0xA1, 0x01, 0x00, 0x00]) + bytes(slots)
    assert len(acl) == 14
    return _rec(2, 14, acl, dt_ms)


def scenario_alt_cid(n=3):
    """★★ 2026-10-17 线上事故的回归用例：**音频与按键走 CID 0x40**。

    事故经过：L2CAP 的动态 CID 是**每次连接按升序分配**的，Windows 的 HIDP
    开 Control/Interrupt 两条通道，**谁先开谁拿 0x40**。桥曾经写死
    `cid == 0x41` ⇒ 某次重连后数据落在 0x40，于是"按键整体无效、音频一帧没有"，
    日志里全是 `未识别分片 dlen=14`。

    判据必须是**L2CAP 长度 + 报告头**，而不是 CID 等值。
    """
    out = b""
    for i in range(n):
        out = emit_frame(out, i, cid=0x0040)         # 音频走 0x40
    # 真实流以"空报文"开头（桥会把每段第一条当基线，见 main.rs 的 seeded）
    out += emit_hid_multi(out, [], cid=0x0040)
    # ⚠ emit_hid_multi 只返回**新的一条**（与 emit_hid_report 同约定），必须 `+=`
    out += emit_hid_multi(out, [0x10], cid=0x0040)   # 语音键也走 0x40
    out += emit_hid_multi(out, [],     cid=0x0040)
    return out


def scenario_two_keys():
    """★★ 2026-10-17 真机实测发现的 bug 的回归用例。

    遥控器同时按 `back + menu` 时，HID 报告里**两个 usage 并存**：
        [09] → [09,11] → [11] → []
    老实现只取"第一个非零 usage"（`find(|u| u != 0)`）+ 单槽 `last_usage`，于是：
        [09]     → 09 DOWN
        [09,11]  → 第一个还是 09，**什么都不发（11 被整条丢掉）**
        [11]     → 09 UP + 11 DOWN（迟到）
        []       → 11 UP
    事件条数一样是 4 条，**顺序却错了** —— 所以这条用例必须断言**顺序**，
    只看计数会假通过。
    """
    out = b""
    # ★ 真实流一定从"空报文"开始；桥会把**每段第一条**当基线（不产生事件），
    #   所以用例必须先给一条空的，否则第一个按键会被基线吃掉。
    out += emit_hid_multi(out, [])
    for u in ([0x09], [0x09, 0x11], [0x11], []):
        out += emit_hid_multi(out, u)
    return out


def scenario_seed_baseline():
    """★★ 2026-10-18 真机事故的回归用例：**启动瞬间不得凭空触发一次按键**。

    现象：双击 exe 启动程序，光标处的文件**弹出右键菜单** —— 即菜单键(0x4E)被按了一次。

    根因：`held` 初值全 false，而程序刚起来时收到的**第一条报文里就带着那个键**
    （设备正抱着键，或蓝牙重连补发的一条"当前状态"），于是被当成"刚按下"。

    修法：每段的第一条报文只当基线，不发事件。

    断言：第一份报文就是 `[0x4E]`、第二份还是 `[0x4E]`（真的按住不放）⇒
    **一个按键事件都不许有**（老实现会先发 4E DOWN）。
    """
    out = b""
    out += emit_hid_multi(out, [0x4E])   # 程序刚起来，设备已经抱着"菜单"
    out += emit_hid_multi(out, [0x4E])   # 仍然抱着 —— 相对基线没有变化
    return out


def scenario_long_hold_release():
    """★★★ 2026-10-18 真机反复卡键的回归用例：**长按后松开，UP 必须发出去**。

    真机现象：按住语音键说话（正常）、松开后 **右 Alt 一直按着不弹起**。

    根因：语音键按住期间遥控器**在按住期间不上报**（实测 30 秒内 0 次），
    松开时才发一条报文。它与上一条的间隔远超 600ms ⇒ 桥会把它当成"新的一段"、
    只取基线、**不发任何事件** ⇒ 那条"松开"被吞掉 ⇒ 永久卡键。

    断言：`[0x10]` 之后空档 700ms 再来一条 `[]` ⇒ 必须产生 `10 UP`。
    老实现只会给出 `10 DOWN`，没有 UP。

    ⚠ 必须按真实时间轴跑（realtime）：`--fast` 下 dt 被忽略、空档不存在 ⇒ 会假通过。
    """
    out = b""
    out += emit_hid_multi(out, [])                 # 段首基线
    out += emit_hid_multi(out, [0x10])             # 按下语音键
    # 长按期间设备不上报；这是"松开"那条，间隔 700ms 触发"空档重置基线"
    out += emit_hid_multi(out, [], dt_ms=700)
    return out


def scenario_ghost_between_hold():
    """★★★ 2026-10-18 真机"长按被截断在 200ms"的回归用例。

    真机日志（三组时间戳完全一致）：
        10:15:19.001  voice DOWN
        10:15:19.144  voice UP          ← 才 142ms
        10:15:19.144  0x0C (幽灵) DOWN   ← 同一毫秒
    遥控器在按住语音键期间**插了一条只带 0x0C 的报文**，我们按"整份快照"处理
    ⇒ voice 从集合里消失 ⇒ 判定松开 ⇒ 长按永远只有 ~150-210ms
    ⇒ 豆包「长按说话」（需 300~500ms）永不触发。

    断言：`[0x10]` → `[0x0C]` → **仍按着** `[0x10]` → `[]`
    正确结果：只有一条 `10 DOWN` 和一条 `10 UP`。
    老实现会在幽灵那条上误报 `10 UP`，随后把 `[0x10]` 又当成一次新按下
    ⇒ 变成 `DOWN / UP / DOWN / UP` 四条。
    """
    out = b""
    out += emit_hid_multi(out, [])          # 基线
    out += emit_hid_multi(out, [0x10])      # 按下语音键
    out += emit_hid_multi(out, [0x0C])      # 幽灵报文：不许影响按住状态
    out += emit_hid_multi(out, [0x10])      # 仍然按着
    out += emit_hid_multi(out, [])          # 真松开
    return out


def scenario_ghost_before_first_key():
    """★★★ 2026-10-18 真机"按下 0 / 松开 0，一个事件都没有"的回归用例。

    真机数据：`hidReports: 6` / `firstKeyUsage: 0x10` / **`keyEvents: 0`**
    —— 报告收到了，事件一个没发出。

    根因：幽灵报文过滤被放在"取基线"**之前**。遥控器空闲时反复发 0x0C 心跳，
    每条都走幽灵分支 return ⇒ `seeded` 永远 false ⇒ 第一份**真实**报文
    （语音键 0x10）被当成"段首基线"吃掉 ⇒ 永远不产生 DOWN。

    断言：`[0x0C]`（心跳）→ `[0x10]`（按下）→ `[]`（松开）
    正确结果：`10 DOWN` + `10 UP`。
    老实现：`0x0C` 被丢弃且不推进 seeded，`[0x10]` 被当基线 ⇒ **零事件**。
    """
    out = b""
    out += emit_hid_multi(out, [0x0C])   # 空闲心跳（幽灵）
    out += emit_hid_multi(out, [0x10])   # 按下语音键
    out += emit_hid_multi(out, [])       # 松开
    return out


def scenario_audio_with_keys(audio=20, keys=10):
    """音频与按键**交替**到达：音频一帧不丢、按键一条不落。"""
    out = b""
    for i in range(audio):
        out = emit_frame(out, i)
        if i < keys:
            out += emit_hid_report(out, 0x10)
    return out


def scenario_unknown_pdu(n=10):
    """L2CAP 长度不是我们认识的 653（比如换了音频配置）→ 必须**留痕**（未识别），
    而且不能当成音频帧交出去（宁可不响，也不要放出解错的数据）。"""
    out = b""
    for i in range(n):
        out = emit_frame(out, i, force_len=700)   # 声明的 L2CAP 长度不是 653
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
    m = re.search(r"音频帧 (\d+)\s+被截断首片 (\d+)\s+HID报告 (\d+)", text)
    if m:
        g["frames"] = int(m.group(1))
        g["trunc_first"] = int(m.group(2))
        g["hid_reports"] = int(m.group(3))
    m = re.search(
        r"组帧失败\(续片没到\) (\d+) · seq跳号 (\d+) 次（跳过 (\d+) 帧）· seq重复 (\d+)"
        r"\s*· 续片被截断 (\d+) · 未识别分片 (\d+) · 帧头异常 (\d+)"
        r"(?: · 孤续片 (\d+) · 怪长帧 (\d+))?", text)
    if m:
        g.update(lost_cont=int(m.group(1)), seq_jumps=int(m.group(2)),
                 seq_lost=int(m.group(3)), seq_dup=int(m.group(4)),
                 trunc_cont=int(m.group(5)), odd_frag=int(m.group(6)),
                 hdr_odd=int(m.group(7)),
                 orphan_cont=int(m.group(8) or 0), short_pdu=int(m.group(9) or 0))
    m = re.search(r"未识别分片分布: (.+)", text)
    g["odd_kinds"] = m.group(1).strip() if m else ""
    m = re.search(r"续片长度分布: (.+)", text)
    g["cont_kinds"] = m.group(1).strip() if m else ""
    return g


def run(name, blob, expect, realtime=False):
    tmp = tempfile.mkdtemp(prefix="iprc-selftest-")
    rp = os.path.join(tmp, "replay.bin")
    with open(rp, "wb") as f:
        f.write(blob)
    # ★ APPDATA 指到临时目录：自检不污染真机的 bridge.log / bridge-stats.json
    env = dict(os.environ)
    env["APPDATA"] = tmp
    cmd = [EXE, "--replay", rp, "--no-feed", "--key-udp", ""]
    if not realtime:
        cmd.append("--fast")
    p = subprocess.run(cmd, capture_output=True, env=env, timeout=180)
    text = p.stdout.decode("utf-8", "replace") + p.stderr.decode("utf-8", "replace")
    got = parse(text)

    bad = []

    # ★ 2026-10-18：`key_seq` 是**子序列**匹配，`key_seq=[]` 永远成立（空断言！）。
    #   「不许有任何按键事件」这种断言必须用它：整段序列完全相等。
    if "key_seq_exact" in expect:
        import re as _re2
        seq_x = [f"{m.group(1)} {m.group(2)}"
                 for m in _re2.finditer(r"usage=0x([0-9A-F]{2})\s+(DOWN|UP)", text)]
        if seq_x != list(expect["key_seq_exact"]):
            bad.append(f"key_seq_exact: 期望 {list(expect['key_seq_exact'])}，实际 {seq_x}")

    # 「不许凭空触发一次按下」的专用断言：整段里**一个 DOWN 都不许有**。
    # （不能要求序列为空 —— 退出时 release_all_held 会给"基线里按着的键"补一个 UP，
    #   那是正确行为，不是假按下。）
    if expect.get("no_key_down"):
        import re as _re3
        downs = [m.group(1) for m in _re3.finditer(r"usage=0x([0-9A-F]{2})\s+DOWN", text)]
        if downs:
            bad.append(f"no_key_down: 不许有任何 DOWN，实际出现 {downs}")

    if "key_seq" in expect:
        # 从 stdout 里按出现顺序抽出 [usage DIR]
        import re as _re
        seq = [f"{m.group(1)} {m.group(2)}"
               for m in _re.finditer(r"usage=0x([0-9A-F]{2})\s+(DOWN|UP)", text)]
        want_seq = list(expect["key_seq"])
        # 允许前后有别的键（真机回放里可能有），只要求 want_seq 是**连续子序列**
        ok = any(seq[i:i + len(want_seq)] == want_seq for i in range(len(seq) - len(want_seq) + 1))
        if not ok:
            bad.append(f"key_seq: 期望 {want_seq}，实际 {seq}")
    for k, want in expect.items():
        if k in ("key_seq", "key_seq_exact", "no_key_down"):
            continue
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
         "odd_frag", "trunc_first", "hdr_odd", "orphan_cont", "short_pdu") if k in got)
    print(f"       {detail}")
    if got.get("odd_kinds"):
        print(f"       未识别分片: {got['odd_kinds']}")
    if got.get("cont_kinds"):
        print(f"       续片长度分布: {got['cont_kinds']}")
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

    ("③ 同一个 PDU 被拆成三片（344+290+23）—— 今天事故的真机指纹，修复后必须一帧不丢",
     scenario_three_frag_split(50),
     dict(frames=50, lost_cont=0, seq_jumps=0, seq_lost=0, odd_frag=0,
          orphan_cont=0, short_pdu=0)),

    ("⑥ L2CAP 长度不认识（换了配置）→ 留痕、不当音频交出去",
     scenario_unknown_pdu(10),
     # 10 条起始分片的 L2CAP 长度都是 700 ≠ 653 ⇒ 全部落到"未识别"，
     # 且**一帧都不许交出去**（宁可不响，也别放出解错的数据）
     dict(frames=0, odd_frag=10, short_pdu=0, lost_cont=0)),

    ("⑦ 音频与按键交替到达：音频一帧不丢、按键一条不落（重写解析器后的回归保护）",
     scenario_audio_with_keys(20, 10),
     dict(frames=20, hid_reports=10, lost_cont=0, odd_frag=0, orphan_cont=0)),

    # ⚠ 这条**必须按真实时间轴跑**（第 4 个元素 = realtime）：`--fast` 下 dt_ms 被忽略，
    #   "停了 1 秒"在墙钟上不存在 ⇒ 桥判不出新会话 ⇒ 用例会**假通过**。
    ("⑧ 跨会话的序号跳变**不算丢帧**（假仪表比没仪表更糟）",
     scenario_two_sessions(25, 1000),
     dict(frames=50, seq_jumps=0, seq_lost=0, lost_cont=0), True),

    ("⑨ 两键同按（back+menu）：**不许丢第二个键**，且事件顺序正确",
     scenario_two_keys(),
     dict(frames=0, key_seq=["09 DOWN", "11 DOWN", "09 UP", "11 UP"])),

    ("⑪ 启动瞬间第一份报文带着按键 —— **不许凭空触发**（真机表现：弹出右键菜单）",
     scenario_seed_baseline(),
     dict(frames=0, no_key_down=True)),

    ("⑫ 长按后空档 700ms 才松开 —— **UP 必须发出去**（否则右 Alt 永久卡键）",
     scenario_long_hold_release(),
     dict(frames=0, key_seq=["10 DOWN", "10 UP"]), True),

    ("⑭ 先来一条幽灵心跳、再按语音键 —— **不许把真实按下当成基线吃掉**",
     scenario_ghost_before_first_key(),
     dict(frames=0, key_seq=["10 DOWN", "10 UP"])),

    ("⑬ 按住期间夹一条幽灵报文(0x0C) —— **不许把长按截断**（豆包需 300~500ms）",
     scenario_ghost_between_hold(),
     dict(frames=0, key_seq=["10 DOWN", "10 UP"])),

    ("⑩ 音频/按键走 CID 0x40（动态分配另一种落法）—— 不许整路失效",
     scenario_alt_cid(3),
     dict(frames=3, key_seq=["10 DOWN", "10 UP"])),

    ("④ 续片被截断（负载不足）：以前完全没有计数",
     scenario_cont_truncated(50, 10, 20, 200),
     # 续片短了 ⇒ 这一帧拼不齐：`trunc_cont` 说明**为什么**（被截断），
     # `lost_cont` 说明**结果**（下一帧的首片来时它还挂着 ⇒ 这一帧没交出去）。
     # 老代码在这条路上连计数都没有（`take()` 一清就当无事发生）。
     dict(frames=40, trunc_cont=10, lost_cont=10, odd_frag=0, seq_lost=10)),

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
    for case in CASES:
        name, blob, expect = case[0], case[1], case[2]
        rt = case[3] if len(case) > 3 else False
        ok &= run(name, blob, expect, realtime=rt)
    print()
    print("⇒ " + ("全部通过 ✓ —— 三类故障确实分得开" if ok else "★ 有断言失败"))
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
