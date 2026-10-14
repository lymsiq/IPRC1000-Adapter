#!/usr/bin/env python3
"""IPRC1000 麦克风激活探测（Linux 内核 SCO socket 方案）

比 raw HCI 更直接：内核会在 connect() 时自动向对端发起
HCI_Setup_Synchronous_Connection；若固件"有链路就推流"，读到的就是麦克风音频。

用法（需 root）:
    sudo python3 sco_probe.py 20:C0:47:0A:34:83                 # 默认探测 30 秒
    sudo python3 sco_probe.py 20:C0:47:0A:34:83 60              # 探测 60 秒
    sudo python3 sco_probe.py 20:C0:47:0A:34:83 30 --voice 0x0003

配套：另开一个终端跑 `sudo btmon -w iprc1000_seq.btsnoop` 抓全量 HCI 便于判定。

判定：
  · 能连上且读到周期性数据（尤其按住麦克风键时）→ 假设②成立
  · connect 报 errno 说明对端拒绝 SCO → 假设②排除
  · 连上但始终 0 字节 → 需要主机额外指令（厂商命令方向）
"""
import errno
import socket
import struct
import sys
import time

AF_BLUETOOTH = 31
BTPROTO_SCO = 2
SOL_BLUETOOTH = 274
BT_VOICE = 11          # 语音设置（host↔controller 编码/采样）
BT_VOICE_TRANSPARENT = 0x0003

ERR = {
    errno.EPERM: "权限不足（需要 root）",
    errno.EACCES: "被拒绝（检查蓝牙权限/rfkill）",
    errno.ENODEV: "无此设备（对端未连接？）",
    errno.ECONNREFUSED: "对端拒绝 SCO 连接（设备不支持同步链路）",
    errno.EHOSTDOWN: "对端不可达（ACL 未建立）",
    errno.EOPNOTSUPP: "不支持该操作（内核/控制器不支持 SCO）",
    errno.ETIMEDOUT: "连接超时（对端未接受 SCO 请求）",
}


def probe(mac: str, seconds: int, voice: int) -> int:
    print(f"[i] 目标 {mac}  探测 {seconds}s  voice_setting=0x{voice:04X}")

    try:
        sock = socket.socket(AF_BLUETOOTH, socket.SOCK_SEQPACKET, BTPROTO_SCO)
    except OSError as e:
        print(f"[!] 创建 SCO socket 失败: {e} ({ERR.get(e.errno, '')})")
        return 2

    try:
        sock.setsockopt(SOL_BLUETOOTH, BT_VOICE, struct.pack("<H", voice))
        print(f"[i] 已设置 voice setting 0x{voice:04X}")
    except OSError as e:
        print(f"[!] 设置 voice setting 失败（不致命）: {e}")

    try:
        sock.settimeout(10)
        sock.connect((mac,))
        print("[✓] SCO 连接已建立（对端接受了同步链路请求）")
    except OSError as e:
        print(f"[✗] SCO connect 失败: {e} ({ERR.get(e.errno, 'errno=%s' % e.errno)})")
        sock.close()
        return 3

    total = 0
    frames = 0
    t0 = time.time()
    timeline = []
    sock.settimeout(1.0)
    while time.time() - t0 < seconds:
        try:
            data = sock.recv(1024)
        except socket.timeout:
            continue
        except OSError as e:
            print(f"[!] 读取中断: {e}")
            break
        if not data:
            continue
        total += len(data)
        frames += 1
        sec = int(time.time() - t0)
        if len(timeline) <= sec:
            timeline.extend([0] * (sec + 1 - len(timeline)))
        timeline[sec] += len(data)

    dur = max(time.time() - t0, 0.001)
    print(f"\n[结果] 收到 {total} 字节 / {frames} 个包，平均 {total / dur:.0f} B/s")
    if timeline:
        print("[每秒字节]")
        for i, n in enumerate(timeline):
            bar = "#" * min(int(n / 20), 60)
            print(f"  +{i:3d}s {n:7d} {bar}")
    if total > 0:
        print("\n[结论] 存在数据流 → 假设②成立，可继续定位编码/帧率")
    else:
        print("\n[结论] 链路建立但无数据 → 需要主机额外指令（厂商命令方向）")
    sock.close()
    return 0


def main() -> int:
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    mac = sys.argv[1]
    seconds = int(sys.argv[2]) if len(sys.argv) > 2 else 30
    voice = 0x0060
    if "--voice" in sys.argv:
        voice = int(sys.argv[sys.argv.index("--voice") + 1], 0)
    return probe(mac, seconds, voice)


if __name__ == "__main__":
    sys.exit(main())
