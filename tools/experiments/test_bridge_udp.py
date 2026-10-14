#!/usr/bin/env python3
"""端到端验证语音桥的 UDP 协议（模拟 audio router 那一侧）。

启动一个 UDP 接收端，同时拉起 etw_consumer 的离线回放模式，
检查是否收到 CLEAR / 48k PCM / END，并把收到的 48k PCM 落盘成 WAV 供试听。

用法: python test_bridge_udp.py <payloads.bin> [exe]
"""
import socket
import struct
import subprocess
import sys
import threading
import time
import wave

BIN = sys.argv[1] if len(sys.argv) > 1 else r"F:\vibe\iprc1000_payloads.bin"
EXE = sys.argv[2] if len(sys.argv) > 2 else \
    r"F:\vibe\tools\etw_consumer\target\release\etw_consumer.exe"
OUT = r"F:\vibe\bridge_udp_out_48k.wav"
PORT = 31699  # 特意避开真实 router 的 31680

sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
sock.bind(("127.0.0.1", PORT))
sock.settimeout(0.3)

pcm = bytearray()
clears = ends = pcms = other = 0
stop = False


def reader():
    global clears, ends, pcms, other
    while not stop:
        try:
            d, _ = sock.recvfrom(65535)
        except socket.timeout:
            continue
        if d == b"CLEAR":
            clears += 1
        elif d == b"END":
            ends += 1
        elif d == b"PING":
            sock.sendto(b"PONG", ("127.0.0.1", PORT))
        elif len(d) % 2 == 0 and len(d) > 2:
            pcms += 1
            pcm.extend(d)
        else:
            other += 1


t = threading.Thread(target=reader, daemon=True)
t.start()

print(f"[i] 接收端 127.0.0.1:{PORT}，拉起回放…")
r = subprocess.run(
    [EXE, "--replay", BIN, "--udp", f"127.0.0.1:{PORT}", "--fast"],
    capture_output=True, text=True, errors="replace", timeout=120,
)
print("--- 桥输出 ---")
print(r.stdout.strip())
if r.stderr.strip():
    print("[stderr]", r.stderr.strip())

time.sleep(0.5)
stop = True
t.join(timeout=1)

print("\n--- 接收端统计 ---")
print(f"CLEAR={clears}  PCM包={pcms}  END={ends}  其他={other}")
print(f"PCM 字节 {len(pcm)} = {len(pcm)//2} 样本 @48k = {len(pcm)/2/48000:.2f}s")

ok = clears == 1 and ends >= 1 and pcms > 0
if ok:
    with wave.open(OUT, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(48000)
        w.writeframes(bytes(pcm))
    print(f"[✓] 已写出 {OUT}")
    print("[✓] 协议正确：CLEAR → 48k PCM 流 → END")
else:
    print("[✗] 协议异常")
sys.exit(0 if ok else 1)
