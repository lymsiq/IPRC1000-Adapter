"""SCO socket 连接调试：确认地址格式与精确 errno"""
import socket

TARGET = "20:C0:47:0A:34:83"


def try_connect(label, addr):
    try:
        s = socket.socket(socket.AF_BLUETOOTH, socket.SOCK_SEQPACKET, socket.BTPROTO_SCO)
        s.settimeout(8)
        s.connect(addr)
        print(f"  OK   {label}: connect 成功 → {addr!r}")
        s.close()
        return True
    except Exception as e:  # noqa: BLE001
        print(f"  FAIL {label}: {type(e).__name__}: {e} | errno={getattr(e, 'errno', None)}")
        return False


print("BTPROTO_SCO =", getattr(socket, "BTPROTO_SCO", "缺失"))
print("AF_BLUETOOTH =", socket.AF_BLUETOOTH)

try_connect("str 元组", (TARGET,))
try_connect("bytes 元组", (bytes.fromhex(TARGET.replace(":", "")),))
try_connect("str+0 元组", (TARGET, 0))
