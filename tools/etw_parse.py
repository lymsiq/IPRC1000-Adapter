#!/usr/bin/env python3
"""从 Windows ETW 蓝牙抓包中提取 IPRC1000 麦克风音频（已验证方案）

背景：Windows 的 hidclass 会丢弃报告描述符未声明的 Report ID（0xF7 音频），
用户态也无法读取（kbdclass 独占）。但 **ETW 的 BTH-BTHPORT 提供程序** 能在
HCI/ACL 层拿到原始数据 —— 无需任何驱动。

关键前提（注册表，需管理员，设完建议禁用/启用一次蓝牙适配器）：
  HKLM\\SYSTEM\\CurrentControlSet\\Services\\BthPort\\Parameters
    EtwDropLargeEvents = 0   ← 默认 1 会丢弃我们的大包（这是关键）
    EtwLogSensitiveData = 1  ← 默认 0 不记录数据负载
    MaxEtwBytes = 4096

抓包：
  logman create trace -n BthTrace -o F:\\vibe\\bth.etl -nb 128 640 -bs 128
  logman update trace -n BthTrace -p Microsoft-Windows-BTH-BTHPORT
  logman update trace -n BthTrace -p Microsoft-Windows-BTH-BUSB      # 实际名称 BTHUSB
  logman start -n BthTrace     … 操作遥控器 …   logman stop -n BthTrace
  注意：logman 会在文件名后自动加 _000001 后缀
  tracerpt bth_000001.etl -o bth.xml -of XML -lr -y

解析（本脚本）：
  1. 取 BIP_Type=3 且长度 >=300 的事件（= ACL 数据/音频），其余为键盘报告与命令
  2. 每个事件：跳过 4 字节 ACL 头 + 1 字节，其余进流
  3. 全局偏移 +2 字节
  4. 结果：16 kHz / 16-bit LE / 单声道，平滑度 0.038、边界比 0.89（完全无缝）

用法: python etw_parse.py <bth_lr.xml> [输出wav]
"""
import re
import struct
import sys
import wave

XML = sys.argv[1] if len(sys.argv) > 1 else r"F:\vibe\bth_lr.xml"
OUT = sys.argv[2] if len(sys.argv) > 2 else r"F:\vibe\etw_mic.wav"
RATE = 16000
FRAG_SKIP = 5   # 4 字节 ACL 头 + 1 字节
GLOBAL_OFF = 2  # 实测最优


def main() -> int:
    d = open(XML, encoding="utf-8", errors="replace").read()
    evs = re.findall(
        r'<Data Name="BIP_Type">3</Data>\s*'
        r'<Data Name="BIP_DataLen">\s*(\d+)</Data>\s*'
        r'<Data Name="BIP_Data">0x([0-9A-Fa-f]*)</Data>',
        d,
    )
    audio = [bytes.fromhex(h) for l, h in evs if int(l) >= 300]
    print(f"[i] 音频事件 {len(audio)} 个（全部长度 {sorted(set(len(a) for a in audio))}）")
    if not audio:
        print("[!] 未找到音频事件：确认 EtwDropLargeEvents=0 且抓包时按住了语音键")
        return 1

    buf = bytearray()
    for a in audio:
        buf += a[FRAG_SKIP:]
    b = bytes(buf[GLOBAL_OFF:])
    s = struct.unpack("<%dh" % (len(b) // 2), b[: len(b) // 2 * 2])

    rms = (sum(v * v for v in s) / len(s)) ** 0.5
    d1 = sum(abs(s[i + 1] - s[i]) for i in range(len(s) - 1)) / (len(s) - 1)
    print(f"[i] 样本 {len(s)} = {len(s)/RATE:.2f}s @ {RATE}Hz   rms={rms:.0f}   平滑度={d1/max(rms,1):.3f}")

    with wave.open(OUT, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes(struct.pack("<%dh" % len(s), *s))
    print(f"[✓] 已写出 {OUT}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
