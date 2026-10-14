#!/usr/bin/env python3
"""IPRC1000 音频链路一体自检。

逐段验证并打印结论：
  ① 虚拟声卡端点      CABLE Input(播放) / CABLE Output(录音) 是否存在且可用
  ② 默认录音设备      是不是 CABLE Output（豆包读的就是这个）
  ③ 写 CABLE Input    用 CableRouter（WASAPI）把 PCM 写进去
  ④ 读 CABLE Output   用 QAudioSource 真录回来，量 rms
  ⑤ 端到端            语音桥 → CABLE Input → CABLE Output，判 rms > 阈值

两种数据源：
  --replay（默认）  用之前抓好的完整帧离线回放（不用遥控器，可重复）
  --live            跑真实 ETW 语音桥，需要你按住遥控器语音键（需管理员）

用法:
  python audio_chain_check.py                  # 离线回放验证
  python audio_chain_check.py --live 15        # 真实链路，15 秒内按住语音键
"""
import array
import os
import socket
import subprocess
import sys
import time

sys.path.insert(0, r"F:\vibe\iprc1000_gui")

from PySide6.QtCore import QTimer                                   # noqa: E402
from PySide6.QtMultimedia import QAudioFormat, QAudioSource, QMediaDevices  # noqa: E402
from PySide6.QtWidgets import QApplication                          # noqa: E402

import app as gui                                                   # noqa: E402

BIN = r"F:\vibe\iprc1000_payloads.bin"
BRIDGE = r"F:\vibe\iprc1000_gui_dotnet\publish\assets\etw_consumer.exe"
LIVE = "--live" in sys.argv
SECS = 15
for i, a in enumerate(sys.argv):
    if a == "--live" and i + 1 < len(sys.argv) and sys.argv[i + 1].isdigit():
        SECS = int(sys.argv[i + 1])

OK, BAD, WARN = "[✓]", "[✗]", "[!]"
fails = []


def section(t):
    print(f"\n{'─'*66}\n{t}\n{'─'*66}", flush=True)


qapp = QApplication(sys.argv)

# ───────── ① 虚拟声卡端点 ─────────
section("① 虚拟声卡端点")
outs = [d.description() for d in QMediaDevices.audioOutputs()]
ins = [d.description() for d in QMediaDevices.audioInputs()]
cable_in = next((d for d in QMediaDevices.audioOutputs() if "cable input" in d.description().lower()), None)
cable_out = next((d for d in QMediaDevices.audioInputs() if "cable output" in d.description().lower()), None)
print(f"  播放设备 {len(outs)} 个：")
for d in outs:
    print(f"     {'→' if 'cable input' in d.lower() else ' '} {d}")
print(f"  录音设备 {len(ins)} 个：")
for d in ins:
    print(f"     {'→' if 'cable output' in d.lower() else ' '} {d}")
if cable_in and cable_out:
    print(f"  {OK} CABLE Input / CABLE Output 都在")
else:
    print(f"  {BAD} 虚拟声卡端点缺失")
    fails.append("虚拟声卡端点缺失")

# ───────── ② 默认录音设备 ─────────
section("② 默认录音设备（豆包读的就是这个）")
try:
    default_in = QMediaDevices.defaultAudioInput()
    print(f"  默认录音设备：{default_in.description()}")
    if "cable output" in default_in.description().lower():
        print(f"  {OK} 默认录音设备就是 CABLE Output —— 豆包能直接读到")
    else:
        print(f"  {WARN} 默认录音设备不是 CABLE Output。"
              f"豆包若跟随系统默认，就听不到语音；请在豆包设置里手动选 "
              f"『CABLE Output (VB-Audio Virtual Cable)』")
except Exception as e:
    print(f"  {WARN} 读不到默认设备：{e}")

if not cable_in or not cable_out:
    print("\n无法继续（缺端点）")
    sys.exit(1)

# ───────── ③④ 写入 + 回读 ─────────
section("③ 写 CABLE Input  " + ("（真实 ETW 语音桥）" if LIVE else "（离线回放）"))
ok = gui.ROUTER.start()
print(f"  CableRouter.start = {ok}  {gui.ROUTER.status()}")

fmt = QAudioFormat()
fmt.setSampleRate(48000)
fmt.setChannelCount(1)
fmt.setSampleFormat(QAudioFormat.Int16)
src = QAudioSource(cable_out, fmt)
io = src.start()
captured = bytearray()


def pump():
    d = io.readAll().data()
    if d:
        captured.extend(bytes(d))


t = QTimer()
t.timeout.connect(pump)
t.start(20)

print(f"\n{section('④ 从 CABLE Output 真录回来')}" if False else "")
section("④ 从 CABLE Output 真录回来")

if LIVE:
    if not gui.is_admin():
        print(f"  {BAD} 实时 ETW 需要管理员权限，请用管理员身份运行本脚本")
        fails.append("非管理员")
    print(f"  启动真实语音桥：{BRIDGE}")
    print(f"  ★ 请现在按住遥控器语音键说话，持续 {SECS} 秒…")
    proc = subprocess.Popen(
        [BRIDGE, "--udp", f"127.0.0.1:{gui.PCM_PORT}", "--hp", "70", "--seconds", str(SECS)],
        creationflags=0x08000000,
    )
else:
    print("  回放已知良好的 705 帧（14.1 秒语音）…")
    proc = subprocess.Popen(
        [BRIDGE if os.path.exists(BRIDGE)
         else r"F:\vibe\tools\etw_consumer\target\release\etw_consumer.exe",
         "--replay", BIN, "--udp", f"127.0.0.1:{gui.PCM_PORT}", "--hp", "70"],
        creationflags=0x08000000,
    )


def finish():
    t.stop()
    try:
        src.stop()
    except Exception:
        pass
    if proc.poll() is None:
        proc.terminate()
    r = gui.ROUTER.status()
    section("⑤ 结论")
    print(f"  语音桥 → 路由器：收到 {r['packets']} 包 / {r['bytes']} 字节"
          f"（设备 {r['device']}）")
    if r["packets"] == 0:
        print(f"  {BAD} 路由端一个包都没收到 —— 语音桥没推过来（没按语音键？没管理员？）")
        fails.append("路由器没收到包")

    n = len(captured) // 2
    if n:
        a = array.array("h")
        a.frombytes(bytes(captured[: n * 2]))
        rms = (sum(v * v for v in a) / len(a)) ** 0.5
        peak = max(abs(v) for v in a)
        print(f"  从 CABLE Output 录回 {n} 样本 = {n/48000:.2f}s  rms={rms:.0f}  峰值={peak}")
        if rms > 20:
            print(f"  {OK} 音频链路通：语音桥 → CABLE Input → CABLE Output，声音确实出来了")
        else:
            print(f"  {BAD} CABLE Output 录回来是静音 —— 虚拟声卡这一段不通")
            fails.append("CABLE Output 静音")
    else:
        print(f"  {BAD} 没录到任何数据")
        fails.append("没录到数据")

    print("\n" + "=" * 66)
    print("  整体判定：" + (f"{OK} 通过" if not fails else f"{BAD} 失败 —— " + "、".join(fails)))
    print("=" * 66)
    qapp.quit()


QTimer.singleShot((SECS + 8) * 1000, finish)
sys.exit(qapp.exec())
