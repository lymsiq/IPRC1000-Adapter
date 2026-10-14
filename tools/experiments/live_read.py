#!/usr/bin/env python3
"""增量读取 btmon 写的 btsnoop，提取 IPRC1000 的 F7 音频 → PCM → WAV

格式（datalink 2001 HCI monitor，已逆向验证）：
  btsnoop 记录: [orig(4)][incl(4)][flags(4)][drops(4)][ts(8)] + payload
  payload     : [opcode(2)][datalen(2 LE)] + [l2cap_len(2 LE)][cid(2 LE)] + 分片数据
  l2cap 载荷   : a1 (HIDP Input) + report_id + ...
  F7 报告      : report_id=0xF7, 其后 11 字节头（含 0x0140=320 样本数），再 640 字节 PCM

用法: sudo python3 live_read.py [秒数]
"""
import collections
import struct
import sys
import time
import wave

PATH = "/tmp/live.btsnoop"
SECS = float(sys.argv[1]) if len(sys.argv) > 1 else 30
RATE = int(sys.argv[2]) if len(sys.argv) > 2 else 16000
WAV = "/tmp/iprc1000_mic.wav"

# 已验证的 F7 载荷结构（653 字节，实测标定）：
#   [0..12]    a1 f7 + 11 字节头
#   [13..338]  PCM1 = 326 字节 = 163 样本
#   [339]      分隔/填充字节（丢弃）
#   [340..352] a1 f7 + 11 字节头
#   [353..652] PCM2 = 300 字节 = 150 样本（被截断，缺尾部 13 样本）
#
# 关键：设备每个载荷实发 326 样本（163+163），末尾 13 样本落到下一个载荷，
# 但下一个载荷以新子报告开头 → 这 13 样本被丢弃。实测边界跳变比 11.98 → 补齐后 0.86。
# 因此解码时须在每包尾部线性插值补 13 样本，音频才连续、语速才正确。
PAYLOAD_LEN = 653
BLOCK1 = (13, 339)
BLOCK2 = (353, 653)
GAP_SAMPLES = 13

frag = {}          # cid -> bytearray（分片重组）
last_block2 = None  # 上一包尾部字节，用于跨包补齐


def _fill_gap(tail_bytes: bytes, next_head: bytes) -> bytes:
    """在两包之间线性插值补 GAP_SAMPLES 个样本（16bit LE）"""
    if len(tail_bytes) < 2 or len(next_head) < 2:
        return b""
    last = struct.unpack("<h", tail_bytes[-2:])[0]
    nxt = struct.unpack("<h", next_head[:2])[0]
    out = bytearray()
    for k in range(1, GAP_SAMPLES + 1):
        out += struct.pack("<h", int(last + (nxt - last) * k / (GAP_SAMPLES + 1)))
    return bytes(out)
need = {}          # cid -> 期望的 L2CAP 总长
rssi_counts = collections.Counter()
rid_counts = collections.Counter()
pcm = bytearray()
first_struct = []
rate_stamps = []

fh = open(PATH, "rb", buffering=0)
data = bytearray()
fh.seek(0)
t0 = time.time()
header_done = False

try:
    while time.time() - t0 < SECS:
        chunk = fh.read(262144)
        if chunk:
            data += chunk
        else:
            time.sleep(0.005)

        # 只在首次校验并剥离 btsnoop 16 字节文件头
        if not header_done:
            if len(data) < 16:
                continue
            if data[:8] != b"btsnoop\x00":
                print(f"[!] 不是 btsnoop 数据，前 16 字节: {bytes(data[:16]).hex(' ')}")
                break
            data = data[16:]
            header_done = True

        off = 0
        consumed = 0
        while off + 24 <= len(data):
            ol, il, fl, dr, ts = struct.unpack_from(">IIIIq", data, off)
            if off + 24 + il > len(data):
                break
            pl = bytes(data[off + 24: off + 24 + il])
            off += 24 + il
            consumed = off
            # monitor 头 4 字节 + L2CAP 头
            if len(pl) < 8:
                continue
            opcode, dlen = struct.unpack_from("<HH", pl, 0)
            l2len, cid = struct.unpack_from("<HH", pl, 4)
            body = pl[8:]
            if l2len == 0 or l2len > 4096:
                continue
            if cid not in frag:
                frag[cid] = bytearray()
                need[cid] = l2len
            frag[cid] += body
            if len(frag[cid]) >= need[cid]:
                payload = bytes(frag[cid][:need[cid]])
                frag[cid] = bytearray()
                if len(payload) >= 2 and payload[0] == 0xA1:
                    rid = payload[1]
                    rid_counts[rid] += 1
                    if rid == 0xF7 and len(payload) == PAYLOAD_LEN:
                        rate_stamps.append(time.time())
                        if len(first_struct) < 2:
                            first_struct.append(
                                f"len={len(payload)} hdr={payload[:18].hex(' ')}")
                        # 双块结构：各取 PCM，丢弃块间分隔字节
                        chunk = payload[BLOCK1[0]:BLOCK1[1]] + payload[BLOCK2[0]:BLOCK2[1]]
                        # 记录原始块数据，供收尾补齐
                        if last_block2 is not None:
                            pcm += _fill_gap(last_block2, chunk[:2])
                        pcm += chunk
                        last_block2 = chunk[-GAP_SAMPLES * 2:]
        # 保留未解析尾部
        data = data[consumed:]
finally:
    fh.close()

dur = time.time() - t0
print(f"[i] 运行 {dur:.1f}s")
print(f"[i] report_id 统计: { {hex(k): v for k, v in rid_counts.items()} }")
for s in first_struct:
    print(f"[i] F7 载荷样例: {s}")
n = len(rate_stamps)
print(f"[i] F7 包 {n} 个，PCM {len(pcm)} 字节 = {len(pcm) // 2} 样本")
if n > 1:
    span = max(rate_stamps[-1] - rate_stamps[0], 0.001)
    print(f"[i] 包速率 {n / span:.1f} 包/秒")
    if pcm:
        print(f"[i] 实测采样率 ≈ {len(pcm) / 2 / span:.0f} Hz")
if pcm:
    with wave.open(WAV, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes(bytes(pcm))
    print(f"[✓] 已写入 {WAV}（{len(pcm)} 字节 PCM @ {RATE} Hz）")
else:
    print("[!] 未收到 F7 音频")
