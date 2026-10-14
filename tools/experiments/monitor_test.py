"""测试 Python 能否绑定内核 HCI monitor 通道（实时获取原始 HCI 帧）"""
import socket
import struct

AF_BLUETOOTH = 31
BTPROTO_HCI = 1
HCI_CHANNEL_MONITOR = 3

s = socket.socket(AF_BLUETOOTH, socket.SOCK_RAW, BTPROTO_HCI)

bound = None
for form in ((0, HCI_CHANNEL_MONITOR), (0,)):
    try:
        s.bind(form)
        bound = form
        print(f"[✓] bind{form} 成功")
        break
    except Exception as e:  # noqa: BLE001
        print(f"[✗] bind{form} 失败: {type(e).__name__}: {e}")

if bound is None:
    raise SystemExit("无法绑定 monitor 通道")

s.settimeout(6)
seen = 0
while seen < 12:
    try:
        d = s.recv(4096)
    except socket.timeout:
        print("[i] 6 秒内无更多数据")
        break
    if not d:
        continue
    seen += 1
    op, idx = struct.unpack_from("<HH", d, 0)
    print(f"  帧 {len(d):5d}B  opcode=0x{op:04X}(LE)/0x{struct.unpack_from('>H', d, 0)[0]:04X}(BE)  index={idx}  数据前16B: {d[4:20].hex(' ')}")
print(f"[i] 共收到 {seen} 帧")
