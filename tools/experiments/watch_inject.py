#!/usr/bin/env python3
"""独立验证：我们 SendInput 注入的右 Alt 是否真的进入了输入管线。

用独立低级键盘钩子旁观，记录事件的 vk / flags（尤其 LLKHF_INJECTED），
同时拉起 IPRC1000 Adapter.exe --inject-test 作为注入源。

判据：
  · 看到带 INJECTED 的 0xA5  → 注入已送达（豆包不响应 = 它自己过滤了注入键）
  · 完全看不到 0xA5          → 注入没出去 / 被我们自己的钩子吃掉了
  · 看到不带 INJECTED 的 0xA5 → 被当成物理键（最理想）
"""
import ctypes
import subprocess
import sys
import time
from ctypes import wintypes

EXE = r"F:\vibe\iprc1000_gui_dotnet\publish\IPRC1000 Adapter.exe"
OUT = r"F:\vibe\iprc1000_gui_dotnet\inject_watch.json"

WH_KEYBOARD_LL = 13
WM_KEYDOWN, WM_KEYUP, WM_SYSKEYDOWN, WM_SYSKEYUP = 0x0100, 0x0101, 0x0104, 0x0105
LLKHF_INJECTED = 0x10
LLKHF_LOWER_IL_INJECTED = 0x02
PM_REMOVE = 1


class KBDLLHOOKSTRUCT(ctypes.Structure):
    _fields_ = [("vkCode", wintypes.DWORD), ("scanCode", wintypes.DWORD),
                ("flags", wintypes.DWORD), ("time", wintypes.DWORD),
                ("dwExtraInfo", ctypes.c_void_p)]


events = []
HOOKPROC = ctypes.WINFUNCTYPE(ctypes.c_int, ctypes.c_int, wintypes.WPARAM, wintypes.LPARAM)


def on_key(ncode, wparam, lparam):
    if ncode == 0 and int(wparam) in (WM_KEYDOWN, WM_KEYUP, WM_SYSKEYDOWN, WM_SYSKEYUP):
        kb = ctypes.cast(lparam, ctypes.POINTER(KBDLLHOOKSTRUCT)).contents
        kind = "DOWN" if int(wparam) in (WM_KEYDOWN, WM_SYSKEYDOWN) else "UP  "
        inj = "INJECTED" if (kb.flags & (LLKHF_INJECTED | LLKHF_LOWER_IL_INJECTED)) else "physical"
        events.append((time.perf_counter(), kind, kb.vkCode, kb.scanCode, inj))
    return ctypes.windll.user32.CallNextHookEx(None, ncode, wparam, lparam)


user32 = ctypes.windll.user32
user32.SetWindowsHookExW.restype = ctypes.c_void_p
user32.SetWindowsHookExW.argtypes = [ctypes.c_int, HOOKPROC, ctypes.c_void_p, wintypes.DWORD]
user32.CallNextHookEx.restype = ctypes.c_void_p
user32.CallNextHookEx.argtypes = [ctypes.c_void_p, ctypes.c_int, wintypes.WPARAM, wintypes.LPARAM]

proc = HOOKPROC(on_key)          # 必须保活
hook = user32.SetWindowsHookExW(WH_KEYBOARD_LL, proc, None, 0)
print(f"[i] 钩子 handle={hook}  装={'成功' if hook else '失败'}", flush=True)
if not hook:
    print("[!] 装钩子失败", ctypes.get_last_error(), flush=True)
    sys.exit(1)

print("[i] 1 秒后触发注入…", flush=True)
time.sleep(1.0)
subprocess.Popen([EXE, "--inject-test", OUT], creationflags=0x08000000)

deadline = time.time() + 3.5
msg = wintypes.MSG()
while time.time() < deadline:
    while user32.PeekMessageW(ctypes.byref(msg), None, 0, 0, PM_REMOVE):
        user32.TranslateMessage(ctypes.byref(msg))
        user32.DispatchMessageW(ctypes.byref(msg))
    time.sleep(0.01)

print(f"\n=== 旁观到 {len(events)} 个键盘事件 ===", flush=True)
t0 = events[0][0] if events else 0
for t, kind, vk, sc, inj in events[:40]:
    print(f"  +{(t-t0)*1000:7.1f} ms  {kind}  vk=0x{vk:02X}  scan=0x{sc:02X}  {inj}", flush=True)

alts = [e for e in events if e[2] in (0xA5, 0xA4, 0x12)]
print(f"\nAlt 类事件 {len(alts)} 条：", flush=True)
for t, kind, vk, sc, inj in alts:
    print(f"    {kind} vk=0x{vk:02X} scan=0x{sc:02X} {inj}", flush=True)
if not alts:
    print("    （没有 Alt 事件 —— 注入没送达，或被钩子吃掉了）", flush=True)
