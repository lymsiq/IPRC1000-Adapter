#!/usr/bin/env python3
r"""卡键守卫（Stuck Key Guard）—— 独立于主 App 的兜底。

解决什么问题
------------
遥控器只发来 M-down、没来得及发 M-up 就断开（拔电池/休眠/离开范围）时，
**Windows 会把 M 永远留在「按下」**，之后键盘类驱动按约 31 Hz 持续合成 ——
于是就算遥控器已经不在了，`m` 仍会一直往外刷屏。

主 App 里有兜底（设备移除即时解锁 + 800 ms 看门狗），但 **App 没运行时就没人管**。
本脚本就是补这个缺口：常驻后台，发现「M 仍按下但遥控器已不在」就补发一次 KEYUP。

用法
----
  python stuck_key_guard.py              # 前台运行（调试）
  python stuck_key_guard.py --install    # 加入开机自启（HKCU\...\Run）
  python stuck_key_guard.py --uninstall  # 取消开机自启
  python stuck_key_guard.py --once       # 只跑一次检查（自检用）

注意：SendInput 受 UIPI 限制，若前台是提权窗口可能被拒。守卫自身以管理员运行最稳
（随附的 Run-Guard.cmd 会自动提权）。
"""
import ctypes
import sys
import time

from ctypes import wintypes

# 需要盯防的键：
#   0x4D = M（遥控器语音键被 Windows 映射成的虚拟键）
#   0xA5 = 右 Alt（AltGr）—— 我们为「豆包长按说话」注入的键。
#          **它是更要命的一个**：AltGr 卡住会让字母键输出特殊字符、中文输入法失效。
# 其余是遥控器用到的各键，一并兜底。
WATCH_VKS = {
    0x4D: "M（语音键）",
    0xA5: "右 Alt / AltGr（长按说话注入键）",
    0x26: "↑", 0x28: "↓", 0x25: "←", 0x27: "→",
    0x0D: "Enter", 0x1B: "Esc", 0x5D: "菜单键", 0x5B: "Win",
    0xAD: "静音", 0xAE: "音量-", 0xAF: "音量+",
    0xB0: "上一曲", 0xB1: "下一曲", 0xB3: "播放/暂停",
    0x21: "PageUp", 0x22: "PageDown", 0x12: "Alt", 0x11: "Ctrl",
}
STUCK_MS = 600              # 持续按下多久算"卡住"
CHECK_MS = 150              # 轮询间隔
LOG = r"C:\Users\Administrator\AppData\Roaming\IPRC1000 Adapter\stuck-guard.log"
RUN_KEY = r"Software\Microsoft\Windows\CurrentVersion\Run"
RUN_NAME = "Iprc1000StuckKeyGuard"

u = ctypes.windll.user32
u.GetAsyncKeyState.argtypes = [ctypes.c_int]
u.GetAsyncKeyState.restype = ctypes.c_short


def down_vks():
    """当前处于「按下」状态的受监控键"""
    return [vk for vk in WATCH_VKS if (u.GetAsyncKeyState(vk) & 0x8000) != 0]


def remote_present():
    r"""遥控器是否还在系统里（注册表 Enum\HID 下有 0a5c/8502 节点）"""
    import winreg
    try:
        with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE,
                            r"SYSTEM\CurrentControlSet\Enum\HID", 0, winreg.KEY_READ) as k:
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


def send_keyup(vk):
    arr = (INPUT * 1)()
    arr[0].type = 1
    arr[0].ki = KEYBDINPUT(wVk=vk, wScan=0, dwFlags=0x0002, time=0, dwExtraInfo=None)
    return u.SendInput(1, arr, ctypes.sizeof(INPUT))


def release_all(why):
    """把受监控的、当前按下的键全部补一次 UP"""
    n = 0
    for vk in down_vks():
        send_keyup(vk)
        n += 1
        log(f"  ↳ 释放 0x{vk:02X} {WATCH_VKS[vk]}（{why}）")
    return n


def log(msg):
    line = f"{time.strftime('%H:%M:%S')}  {msg}"
    print(line, flush=True)
    try:
        import os
        os.makedirs(os.path.dirname(LOG), exist_ok=True)
        with open(LOG, "a", encoding="utf-8") as f:
            f.write(line + "\n")
    except Exception:
        pass


def install():
    import winreg
    exe = sys.executable
    script = __file__ if "__file__" in globals() else sys.argv[0]
    with winreg.OpenKey(winreg.HKEY_CURRENT_USER, RUN_KEY, 0, winreg.KEY_WRITE) as k:
        winreg.SetValueEx(k, RUN_NAME, 0, winreg.REG_SZ,
                          f'"{exe}" "{script}"')
    log(f"已加入开机自启：{RUN_NAME}")


def uninstall():
    import winreg
    with winreg.OpenKey(winreg.HKEY_CURRENT_USER, RUN_KEY, 0, winreg.KEY_WRITE) as k:
        try:
            winreg.DeleteValue(k, RUN_NAME)
            log("已取消开机自启")
        except FileNotFoundError:
            log("本来就没设置开机自启")


def check_once():
    """返回是否发生了解锁"""
    vks = down_vks()
    if not vks:
        return False
    if remote_present():
        return False
    log(f"检测到 {len(vks)} 个键按下但遥控器已不在 → 补发 KEYUP")
    release_all("单次检查")
    time.sleep(0.4)
    return not down_vks()


def main():
    if "--install" in sys.argv:
        install(); return
    if "--uninstall" in sys.argv:
        uninstall(); return
    if "--once" in sys.argv:
        log("单次检查：" + ("已解锁" if check_once() else "无需解锁"))
        return

    log("卡键守卫已启动（独立兜底，主 App 未运行也能救）")
    log("  盯防：" + "、".join(WATCH_VKS.values()))
    down_since = None
    fixed = 0
    while True:
        try:
            vks = down_vks()
            if vks:
                if down_since is None:
                    down_since = time.time()
                elif (time.time() - down_since) * 1000 > STUCK_MS:
                    # 遥控器在线时也可能卡（注入被强杀）→ 只对「遥控器不在」保守判定，
                    # 但若卡的是 AltGr/Ctrl/Alt/Shift 这类修饰键，则无论如何都要救（它们危害最大）。
                    mods = [v for v in vks if v in (0xA5, 0x12, 0x11, 0x10)]
                    if not remote_present() or mods:
                        fixed += 1
                        log(f"卡键救援 #{fixed}：{[hex(v) for v in vks]} 已按下 {(time.time()-down_since)*1000:.0f} ms"
                            f"（遥控器{'不在' if not remote_present() else '在线'}{'，含修饰键' if mods else ''}）")
                        release_all(f"救援 #{fixed}")
                        time.sleep(1.0)
                        down_since = None
            else:
                down_since = None
        except Exception as e:
            log(f"检查异常：{e}")
        time.sleep(CHECK_MS / 1000)


if __name__ == "__main__":
    main()
