"""测试：用户态能否用 GENERIC_READ 读取遥控器的原始 HID 输入报告（含未声明的 0xF7）

若能读到 → 无需内核驱动，用户态直接取音频
用法: python raw_hid_test.py [秒数]
"""
import ctypes
import sys
import time
from ctypes import wintypes, byref, c_void_p

sys.path.insert(0, "F:\\vibe\\tools")
src = open("F:\\vibe\\tools\\iprc1000_probe.py", encoding="utf-8").read().split("# ---------- mode: keys")[0]
g = {}
exec(src, g)
k32 = g["kernel32"]

GENERIC_READ = 0x80000000
GENERIC_WRITE = 0x40000000
FILE_SHARE_ALL = 0x00000003
OPEN_EXISTING = 3
FILE_FLAG_OVERLAPPED = 0x40000000
INVALID_HANDLE = ctypes.c_void_p(-1).value


class OVERLAPPED(ctypes.Structure):
    _fields_ = [
        ("Internal", ctypes.c_void_p),
        ("InternalHigh", ctypes.c_void_p),
        ("Offset", wintypes.DWORD),
        ("OffsetHigh", wintypes.DWORD),
        ("hEvent", wintypes.HANDLE),
    ]


paths = g["find_hid_paths"]()
if not paths:
    print("[!] 未找到 IPRC1000 HID 设备（需已配对连接）")
    sys.exit(1)
path = paths[0]
print(f"HID 路径: {path}")

SECS = float(sys.argv[1]) if len(sys.argv) > 1 else 20

for access, desc in (
    (GENERIC_READ | GENERIC_WRITE, "GENERIC_READ|WRITE"),
    (GENERIC_READ, "GENERIC_READ"),
):
    ctypes.set_last_error(0)
    h = k32.CreateFileW(
        path, access, FILE_SHARE_ALL, None, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, None
    )
    ok = h != INVALID_HANDLE and h is not None and h != -1
    err = ctypes.get_last_error()
    print(f"\n=== 打开方式 {desc}: {'成功' if ok else f'失败 err={err}'} (handle={h}) ===")
    if not ok:
        continue

    BUF = 4096
    buf = ctypes.create_string_buffer(BUF)
    ev = k32.CreateEventW(None, True, False, None)
    ov = OVERLAPPED()
    ov.hEvent = ev

    print(f"开始 {SECS} 秒重叠读取（请按住遥控器麦克风键说话）…")
    reports = 0
    f7 = 0
    total = 0
    samples = []
    t0 = time.time()
    while time.time() - t0 < SECS:
        k32.ResetEvent(ev)
        ov.Internal = 0
        ov.InternalHigh = 0
        n = wintypes.DWORD(0)
        ctypes.set_last_error(0)
        r = k32.ReadFile(h, buf, BUF, byref(n), byref(ov))
        if not r:
            e = ctypes.get_last_error()
            if e != 997:  # ERROR_IO_PENDING
                print(f"[!] ReadFile 失败 err={e}")
                break
            k32.WaitForSingleObject(ev, 500)
            ok2 = k32.GetOverlappedResult(h, byref(ov), byref(n), False)
            if not ok2:
                e2 = ctypes.get_last_error()
                if e2 == 996:  # ERROR_IO_INCOMPLETE
                    continue
                print(f"[!] GetOverlappedResult 失败 err={e2}")
                break
        if n.value:
            reports += 1
            total += n.value
            data = bytes(buf.raw[: n.value])
            if reports <= 5:
                print(f"  报告 {reports}: {n.value} 字节  前16B: {data[:16].hex(' ')}")
            if b"\xf7" in data:
                f7 += data.count(b"\xf7")
                if len(samples) < 3:
                    i = data.find(b"\xa1\xf7")
                    samples.append(data[i : i + 24].hex(" ") if i >= 0 else "未找到 a1f7")

    k32.CloseHandle(h)
    k32.CloseHandle(ev)
    print(f"  结果：报告 {reports} 个，共 {total} 字节，F7 字节出现 {f7} 次")
    for s in samples:
        print(f"    F7 样例: {s}")
    if f7:
        print("  [✓] 用户态可读取 F7 音频 → 无需内核驱动！")
        break
    print("  [✗] 未读到 F7（可能被 hidclass 丢弃或读不到）")
