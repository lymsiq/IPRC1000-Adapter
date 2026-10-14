#!/usr/bin/env python3
"""检查「一直输出 M」是不是「按下没弹起」——Windows 键盘状态 vs 设备是否还在。

原理
----
若遥控器只发来 M-down、没来得及发 M-up 就断开（拔电池/休眠/断连），
**Windows 的键盘状态会把 M 一直留在「按下」**，键盘类驱动随后按约 31 Hz
持续合成重复 —— 于是就算设备已经不在了，M 仍会一直往外冒。

这个脚本同时读两件事，一眼就能证实：
  ① 系统键盘状态：`GetAsyncKeyState(0x4D)` 的按下位
  ② 设备是否还在：注册表里遥控器的 HID 节点 + 蓝牙枚举

用法
----
  python check_stuck_key.py                 # 监控 30 秒，打印状态变化
  python check_stuck_key.py --seconds 60    # 自定义时长
  python check_stuck_key.py --fix           # 发现「设备不在但 M 仍按下」超过 3 秒，
                                             # 就自动补发一次 M 的 KEYUP 解锁（SendInput）
"""
import ctypes
import sys
import time

from ctypes import wintypes

VK_M = 0x4D          # 'M' —— 遥控器语音键被 Windows 映射成的虚拟键
STUCK_MS = 3000      # 判定「卡住」的持续按下时间
FIX = "--fix" in sys.argv
SECS = 30
for i, a in enumerate(sys.argv):
    if a == "--seconds" and i + 1 < len(sys.argv) and sys.argv[i + 1].isdigit():
        SECS = int(sys.argv[i + 1])

u = ctypes.windll.user32
u.GetAsyncKeyState.argtypes = [ctypes.c_int]
u.GetAsyncKeyState.restype = ctypes.c_short
u.GetKeyState.argtypes = [ctypes.c_int]
u.GetKeyState.restype = ctypes.c_short


def m_down():
    return (u.GetAsyncKeyState(VK_M) & 0x8000) != 0


def m_sync_down():
    """GetKeyState 反映的是「本线程消息队列看到的状态」，用于交叉验证"""
    return (u.GetKeyState(VK_M) & 0x8000) != 0


def device_present():
    """遥控器是否还在：注册表 Enum\\HID 下仍有 0a5c/8502 的节点"""
    import winreg
    base = r"SYSTEM\CurrentControlSet\Enum\HID"
    try:
        with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, base, 0, winreg.KEY_READ) as k:
            for i in range(256):
                try:
                    top = winreg.EnumKey(k, i)
                except OSError:
                    break
                low = top.lower()
                if "0a5c" in low and "8502" in low:
                    return True
    except Exception:
        pass
    return False


class KEYBDINPUT(ctypes.Structure):
    _fields_ = [("wVk", wintypes.WORD), ("wScan", wintypes.WORD),
                ("dwFlags", wintypes.DWORD), ("time", wintypes.DWORD),
                ("dwExtraInfo", ctypes.POINTER(ctypes.c_ulong))]


class INPUT(ctypes.Structure):
    class _I(ctypes.Union):
        _fields_ = [("ki", KEYBDINPUT), ("pad", ctypes.c_byte * 24)]

    _anonymous_ = ("i",)
    _fields_ = [("type", wintypes.DWORD), ("i", _I)]


def send_keyup():
    """补发一次 M 的 KEYUP，把 Windows 键盘状态里卡住的「按下」清掉"""
    arr = (INPUT * 1)()
    arr[0].type = 1
    arr[0].ki = KEYBDINPUT(wVk=VK_M, wScan=0, dwFlags=0x0002, time=0, dwExtraInfo=None)
    n = u.SendInput(1, arr, ctypes.sizeof(INPUT))
    return n


print("=" * 74)
print("  「一直输出 M」根因检查：Windows 键盘状态 vs 遥控器设备")
print("=" * 74)
print(f"  监控 {SECS} 秒；判定卡住阈值 {STUCK_MS} ms；自动修复 {'开' if FIX else '关'}")
print("  期间请：① 按几下语音键 ② 拔掉遥控器电池（或关掉蓝牙）观察 M 是否还在输出")
print("-" * 74)

t0 = time.time()
prev = None
down_since = None
fixed = 0
events = []

while time.time() - t0 < SECS:
    md = m_down()
    dev = device_present()
    state = (md, dev)
    if state != prev:
        now = time.strftime("%H:%M:%S")
        if md and not dev:
            tag = "  ← ★ M 仍按下，但设备已不在！"
        elif md and dev:
            tag = "  ← 正常按住"
        else:
            tag = "  ← 已弹起"
        print(f"  {now}  M={'按下' if md else '松开'}  设备={'在' if dev else '不在'}{tag}")
        events.append((now, md, dev))
        prev = state

    if md:
        if down_since is None:
            down_since = time.time()
        elif not dev and FIX and (time.time() - down_since) * 1000 > STUCK_MS:
            n = send_keyup()
            fixed += 1
            print(f"  {time.strftime('%H:%M:%S')}  → 已补发 M 的 KEYUP（SendInput 返回 {n}），尝试解锁")
            time.sleep(1.0)
            down_since = None
    else:
        down_since = None

    time.sleep(0.05)

print("-" * 74)
stuck = [e for e in events if e[1] and not e[2]]
print(f"  共 {len(events)} 次状态变化；其中「M 按下但设备不在」{len(stuck)} 次")
if stuck:
    print("  ⇒ **证实了你的判断**：M 是 Windows 因为「按下没弹起」而持续合成的，")
    print("     与遥控器是否在线无关（拔电池后仍会输出）。")
    print("  ⇒ 正确解法不是拦截输出，而是：**设备断开时把卡住的键补一次 UP**。")
elif events:
    print("  ⇒ 本次未复现「设备不在但 M 仍按下」。可在拔电池后再跑一次确认。")
else:
    print("  ⇒ 期间没有任何 M 状态变化（没按语音键？）")
