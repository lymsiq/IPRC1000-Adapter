#!/usr/bin/env python3
"""确认遥控器（IPRC1000）当前被绑成什么驱动，以及"是否还是键盘"。

用途：
  · 换驱动**之前**：确认你选中的确实是遥控器那个设备（对一下「设备实例路径」）
  · 换驱动**之后**：确认 Service 不再是 kbdhid（= 系统不再把它当键盘）

用法: python check_device_driver.py
"""
import winreg

VID_TOKEN, PID_TOKEN = "0a5c", "8502"
REMOTE_MAC = "20c0470a3483"

KEYS = [
    ("power", "0x17"), ("back", "0x09"), ("menu", "0x11"), ("exit", "0x07"),
    ("info", "0x0b"), ("ok", "0x28"), ("up", "0x52"), ("down", "0x51"),
    ("left", "0x50"), ("right", "0x4f"), ("favorite", "0x08"), ("voice", "0x10"),
]


def subkeys(root, path):
    out = []
    try:
        with winreg.OpenKey(root, path, 0, winreg.KEY_READ) as k:
            i = 0
            while True:
                try:
                    out.append(winreg.EnumKey(k, i))
                except OSError:
                    break
                i += 1
    except Exception:
        pass
    return out


def val(path, name):
    try:
        with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, path, 0, winreg.KEY_READ) as k:
            return winreg.QueryValueEx(k, name)[0]
    except Exception:
        return None


print("=" * 74)
print("  IPRC1000 遥控器 —— 驱动绑定检查")
print("=" * 74)

still_kb = False
found = []

print("\n【HID 枚举】Enum\\HID 下匹配 VID_0A5C / PID_8502 的节点")
for top in subkeys(winreg.HKEY_LOCAL_MACHINE, r"SYSTEM\CurrentControlSet\Enum\HID"):
    low = top.lower()
    if not (VID_TOKEN in low and PID_TOKEN in low):
        continue
    for inst in subkeys(winreg.HKEY_LOCAL_MACHINE, f"SYSTEM\\CurrentControlSet\\Enum\\HID\\{top}"):
        path = f"SYSTEM\\CurrentControlSet\\Enum\\HID\\{top}\\{inst}"
        svc = val(path, "Service") or "(无)"
        desc = val(path, "DeviceDesc") or ""
        hw = val(path, "HardwareID") or []
        cls = val(path, "ClassGUID") or ""
        found.append((path, svc))
        is_kbd = str(svc).lower() == "kbdhid"
        if is_kbd:
            still_kb = True
        print(f"\n  设备实例路径：")
        print(f"    HID\\{top}\\{inst}")
        print(f"  Service      ：{svc}   {'← ★仍是键盘驱动 kbdhid' if is_kbd else '← 已不是键盘 ✓'}")
        print(f"  DeviceDesc   ：{desc}")
        print(f"  HardwareID   ：{list(hw) if not isinstance(hw, str) else hw}")

print("\n【蓝牙侧】Enum\\BTHENUM 下匹配遥控器 MAC 的节点")
for top in subkeys(winreg.HKEY_LOCAL_MACHINE, r"SYSTEM\CurrentControlSet\Enum\BTHENUM"):
    if REMOTE_MAC not in top.lower():
        continue
    for inst in subkeys(winreg.HKEY_LOCAL_MACHINE, f"SYSTEM\\CurrentControlSet\\Enum\\BTHENUM\\{top}"):
        path = f"SYSTEM\\CurrentControlSet\\Enum\\BTHENUM\\{top}\\{inst}"
        print(f"  BTHENUM\\{top}\\{inst}")
        print(f"    Service = {val(path, 'Service') or '(无)'}")

print("\n" + "=" * 74)
print("  判定")
print("=" * 74)
if not found:
    print("  ⚠ 没找到遥控器的 HID 节点 —— 遥控器可能没连接（按任意键唤醒后再跑一次）")
elif still_kb:
    print("  ✗ 系统**仍然把它当键盘**（Service = kbdhid）")
    print()
    print("    所以按遥控器时：Windows 会合成 31 Hz 的按键重复，「m」会打进前台窗口。")
    print("    换驱动的做法（设备管理器 → 键盘 → HID Keyboard Device，别选成鼠标那类）：")
    print("      右键「更新驱动程序」→ 浏览我的电脑 → 让我从列表选取")
    print("      → 厂商 Microsoft / 型号 **HID-compliant device** → 下一步")
    print("    确认选的是遥控器：属性 → 详细信息 → 选「硬件 ID」，应含")
    print("      HID\\VID_0A5C&UP:0001_U:0006")
    print("    或选「设备实例路径」，应含 VID&00020a5c_PID&8502（见上面那行）")
    print("    成功后设备会从「键盘」移到「人体学输入设备」，再跑一次本脚本应显示「已不是键盘 ✓」")
else:
    print("  ✓ 遥控器已不再被当作键盘 —— 系统不会合成 31 Hz 重复，")
    print("    按遥控器不会再打出 m。此时可以在应用里点「切到 ETW 读键」。")
print()
