# IPRC1000 probe tool
# Modes:
#   list   - find the IPRC1000 HID device interface path
#   keys   - low-level keyboard hook, log every keystroke (VK + scan code)
#   sniff  - Raw Input sniffer: per-device (VID_0A5C/PID_8502 only), logs VKey,
#            HID usage (MakeCode) and vendor HID reports with timestamps
#   pair   - Raw Input + low-level hook in ONE session, correlates the two streams
#            for our remote's keys (ordering + scan-code discriminability)
#
# Run:  python iprc1000_probe.py list
#       python iprc1000_probe.py keys
#       python iprc1000_probe.py sniff [seconds]
#       python iprc1000_probe.py pair [seconds]

import ctypes
import sys
import time
import datetime
from ctypes import wintypes
from ctypes import Structure, POINTER, byref, sizeof, create_unicode_buffer, c_byte, c_void_p, c_size_t

kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
setupapi = ctypes.WinDLL("setupapi", use_last_error=True)
user32 = ctypes.WinDLL("user32", use_last_error=True)

ULONG_PTR = c_size_t
LOG_DIR = r"F:\vibe"

# ---------- SetupAPI: find HID device path ----------
class GUID(Structure):
    _fields_ = [("Data1", wintypes.DWORD), ("Data2", wintypes.WORD),
                ("Data3", wintypes.WORD), ("Data4", c_byte * 8)]

GUID_DEVINTERFACE_HID = GUID(0x4D1E55B2, 0xF16F, 0x11CF, (0x88, 0xCB, 0x00, 0x11, 0x11, 0x00, 0x00, 0x30))
DIGCF_PRESENT = 0x00000002
DIGCF_DEVICEINTERFACE = 0x00000010
INVALID_HANDLE_VALUE = ctypes.c_void_p(-1).value

class SP_DEVICE_INTERFACE_DATA(Structure):
    _fields_ = [("cbSize", wintypes.DWORD), ("InterfaceClassGuid", GUID),
                ("Flags", wintypes.DWORD), ("Reserved", ULONG_PTR)]

setupapi.SetupDiGetClassDevsW.restype = wintypes.HANDLE
setupapi.SetupDiGetClassDevsW.argtypes = [POINTER(GUID), wintypes.LPCWSTR, wintypes.HWND, wintypes.DWORD]
setupapi.SetupDiEnumDeviceInterfaces.restype = wintypes.BOOL
setupapi.SetupDiEnumDeviceInterfaces.argtypes = [wintypes.HANDLE, POINTER(wintypes.DWORD),
    POINTER(GUID), wintypes.DWORD, POINTER(SP_DEVICE_INTERFACE_DATA)]
setupapi.SetupDiGetDeviceInterfaceDetailW.restype = wintypes.BOOL
setupapi.SetupDiGetDeviceInterfaceDetailW.argtypes = [wintypes.HANDLE, POINTER(SP_DEVICE_INTERFACE_DATA),
    wintypes.LPVOID, wintypes.DWORD, POINTER(wintypes.DWORD), POINTER(wintypes.DWORD)]
setupapi.SetupDiDestroyDeviceInfoList.argtypes = [wintypes.HANDLE]

def find_hid_paths(vid="0a5c", pid="8502"):
    hdev = setupapi.SetupDiGetClassDevsW(byref(GUID_DEVINTERFACE_HID), None, None,
                                         DIGCF_PRESENT | DIGCF_DEVICEINTERFACE)
    if hdev == INVALID_HANDLE_VALUE:
        raise OSError("SetupDiGetClassDevsW failed")
    paths = []
    idx = 0
    ifdata = SP_DEVICE_INTERFACE_DATA()
    ifdata.cbSize = sizeof(SP_DEVICE_INTERFACE_DATA)
    while setupapi.SetupDiEnumDeviceInterfaces(hdev, None, byref(GUID_DEVINTERFACE_HID), idx, byref(ifdata)):
        idx += 1
        need = wintypes.DWORD()
        setupapi.SetupDiGetDeviceInterfaceDetailW(hdev, byref(ifdata), None, 0, byref(need), None)
        buf = create_unicode_buffer(need.value)
        # cbSize must be 8 on x64 / 6 on x86 for validation; DevicePath follows the DWORD at offset 4
        ctypes.cast(buf, POINTER(wintypes.DWORD))[0] = 8 if sizeof(c_void_p) == 8 else 6
        if not setupapi.SetupDiGetDeviceInterfaceDetailW(hdev, byref(ifdata), buf, need.value, byref(need), None):
            continue
        path = ctypes.wstring_at(ctypes.cast(buf, c_void_p).value + 4)
        if vid in path.lower() and pid in path.lower():
            paths.append(path)
    setupapi.SetupDiDestroyDeviceInfoList(hdev)
    return paths

def mode_list():
    paths = find_hid_paths()
    print("IPRC1000 HID interface paths:", len(paths))
    for p in paths:
        print(" ", p)

# ---------- mode: keys (WH_KEYBOARD_LL) ----------
VK_NAMES = {
    0x08: "BACKSPACE", 0x09: "TAB", 0x0D: "ENTER", 0x10: "SHIFT", 0x11: "CTRL", 0x12: "ALT",
    0x1B: "ESC", 0x20: "SPACE", 0x21: "PGUP", 0x22: "PGDN", 0x23: "END", 0x24: "HOME",
    0x25: "LEFT", 0x26: "UP", 0x27: "RIGHT", 0x28: "DOWN", 0x2C: "PRTSC", 0x2D: "INS", 0x2E: "DEL",
    0x5B: "LWIN", 0x5C: "RWIN", 0x70: "F1", 0x71: "F2", 0x72: "F3", 0x73: "F4",
    0x74: "F5", 0x75: "F6", 0x76: "F7", 0x77: "F8", 0x78: "F9", 0x79: "F10",
    0x7A: "F11", 0x7B: "F12", 0xA0: "LSHIFT", 0xA1: "RSHIFT", 0xA2: "LCTRL",
    0xA3: "RCTRL", 0xA4: "LALT", 0xA5: "RALT", 0xBA: ";", 0xBB: "=", 0xBC: ",",
    0xBD: "-", 0xBE: ".", 0xBF: "/", 0xC0: "`", 0xDB: "[", 0xDC: "\\", 0xDD: "]",
    0xDE: "'", 0xAD: "VOLUME_MUTE", 0xAE: "VOLUME_DOWN", 0xAF: "VOLUME_UP",
    0xB0: "MEDIA_NEXT", 0xB1: "MEDIA_PREV", 0xB2: "MEDIA_STOP", 0xB3: "MEDIA_PLAYPAUSE",
}

def vk_name(vk):
    if vk in VK_NAMES:
        return VK_NAMES[vk]
    if 0x30 <= vk <= 0x39 or 0x41 <= vk <= 0x5A:
        return chr(vk)
    if 0x60 <= vk <= 0x69:
        return "NUM%d" % (vk - 0x60)
    return "VK_0x%02X" % vk

class KBDLLHOOKSTRUCT(Structure):
    _fields_ = [("vkCode", wintypes.DWORD), ("scanCode", wintypes.DWORD),
                ("flags", wintypes.DWORD), ("time", wintypes.DWORD),
                ("dwExtraInfo", c_void_p)]

HOOKPROC = ctypes.WINFUNCTYPE(wintypes.LPARAM, ctypes.c_int, wintypes.WPARAM, wintypes.LPARAM)
WH_KEYBOARD_LL = 13
WM_KEYDOWN = 0x0100
WM_KEYUP = 0x0101
WM_SYSKEYDOWN = 0x0104
WM_SYSKEYUP = 0x0105

def mode_keys():
    logf = open(LOG_DIR + r"\iprc1000_keys.log", "a", encoding="utf-8", buffering=1)
    def ts():
        return datetime.datetime.now().strftime("%H:%M:%S.%f")[:-3]
    def write(line):
        print(line)
        logf.write(line + "\n")
    write("=== keys session start %s ===" % ts())
    write("hint: press every remote button; ESC quits")

    def hook_proc(nCode, wParam, lParam):
        if nCode >= 0 and wParam in (WM_KEYDOWN, WM_KEYUP, WM_SYSKEYDOWN, WM_SYSKEYUP):
            kb = ctypes.cast(lParam, POINTER(KBDLLHOOKSTRUCT))[0]
            vk = kb.vkCode
            kind = "DOWN" if wParam in (WM_KEYDOWN, WM_SYSKEYDOWN) else "UP"
            write("%s  %-4s  vk=0x%02X (%s)  scan=0x%03X  flags=0x%02X" %
                  (ts(), kind, vk, vk_name(vk), kb.scanCode, kb.flags))
            if vk == 0x1B:
                user32.PostQuitMessage(0)
        return user32.CallNextHookEx(None, nCode, wParam, lParam)

    proc = HOOKPROC(hook_proc)
    hhook = user32.SetWindowsHookExW(WH_KEYBOARD_LL, proc, None, 0)
    if not hhook:
        print("SetWindowsHookExW failed:", ctypes.get_last_error())
        sys.exit(1)
    msg = wintypes.MSG()
    while user32.GetMessageW(byref(msg), None, 0, 0):
        user32.TranslateMessage(byref(msg))
        user32.DispatchMessageW(byref(msg))
    user32.UnhookWindowsHookEx(hhook)
    write("=== keys session end %s ===" % ts())
    logf.close()

# ---------- mode: sniff (Raw Input, per-device) ----------
WNDPROC = ctypes.WINFUNCTYPE(wintypes.LPARAM, wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM)
WM_INPUT = 0x00FF
WM_TIMER = 0x0113
RID_INPUT = 0x10000003
RIDI_DEVICENAME = 0x20000007
RIDEV_INPUTSINK = 0x00000100
RIDEV_PAGEONLY = 0x00000020
RIM_TYPEMOUSE = 0
RIM_TYPEKEYBOARD = 1
RIM_TYPEHID = 2
HWND_MESSAGE = -3

class RAWINPUTDEVICE(Structure):
    _fields_ = [("usUsagePage", wintypes.USHORT), ("usUsage", wintypes.USHORT),
                ("dwFlags", wintypes.DWORD), ("hwndTarget", wintypes.HWND)]

class WNDCLASSW(Structure):
    _fields_ = [("style", wintypes.UINT), ("lpfnWndProc", WNDPROC), ("cbClsExtra", ctypes.c_int),
                ("cbWndExtra", ctypes.c_int), ("hInstance", wintypes.HINSTANCE), ("hIcon", wintypes.HICON),
                ("hCursor", wintypes.HANDLE), ("hbrBackground", wintypes.HBRUSH),
                ("lpszMenuName", wintypes.LPCWSTR), ("lpszClassName", wintypes.LPCWSTR)]

class RAWINPUTHEADER(Structure):
    _fields_ = [("dwType", wintypes.DWORD), ("dwSize", wintypes.DWORD),
                ("hDevice", wintypes.HANDLE), ("wParam", ULONG_PTR)]

user32.RegisterClassW.restype = wintypes.ATOM
user32.RegisterClassW.argtypes = [POINTER(WNDCLASSW)]
user32.CreateWindowExW.restype = wintypes.HWND
user32.CreateWindowExW.argtypes = [wintypes.DWORD, wintypes.LPCWSTR, wintypes.LPCWSTR, wintypes.DWORD,
    ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int, wintypes.HWND, wintypes.HMENU,
    wintypes.HINSTANCE, wintypes.LPVOID]
user32.DefWindowProcW.restype = wintypes.LPARAM
user32.DefWindowProcW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]
user32.RegisterRawInputDevices.restype = wintypes.BOOL
user32.RegisterRawInputDevices.argtypes = [POINTER(RAWINPUTDEVICE), wintypes.UINT, wintypes.UINT]
user32.GetRawInputData.restype = wintypes.UINT
user32.GetRawInputData.argtypes = [wintypes.HANDLE, wintypes.UINT, wintypes.LPVOID,
    POINTER(wintypes.UINT), wintypes.UINT]
user32.GetRawInputDeviceInfoW.restype = wintypes.UINT
user32.GetRawInputDeviceInfoW.argtypes = [wintypes.HANDLE, wintypes.UINT, wintypes.LPVOID,
    POINTER(wintypes.UINT)]
user32.SetTimer.restype = ctypes.c_void_p
user32.SetTimer.argtypes = [wintypes.HWND, ctypes.c_void_p, wintypes.UINT, wintypes.LPVOID]
user32.KillTimer.argtypes = [wintypes.HWND, ctypes.c_void_p]
user32.DispatchMessageW.argtypes = [POINTER(wintypes.MSG)]

def device_name(hDevice):
    size = wintypes.UINT(0)
    user32.GetRawInputDeviceInfoW(hDevice, RIDI_DEVICENAME, None, byref(size))
    if size.value == 0:
        return "?"
    buf = create_unicode_buffer(size.value)
    if user32.GetRawInputDeviceInfoW(hDevice, RIDI_DEVICENAME, buf, byref(size)) == 0xFFFFFFFF:
        return "?"
    return buf.value

def mode_sniff(seconds=60):
    logf = open(LOG_DIR + r"\iprc1000_sniff.log", "a", encoding="utf-8", buffering=1)
    state = {"start": time.time(), "end": time.time() + seconds,
             "ours": set(), "kbd_events": 0, "hid_reports": 0, "other": 0}

    def ts():
        return datetime.datetime.now().strftime("%H:%M:%S.%f")[:-3]
    def write(line):
        print(line)
        logf.write(line + "\n")

    def is_ours(name):
        return "0a5c" in name.lower() and "8502" in name.lower()

    def wndproc(hwnd, msg, wParam, lParam):
        if msg == WM_TIMER:
            user32.PostQuitMessage(0)
            return 0
        if msg == WM_INPUT:
            size = wintypes.UINT(0)
            user32.GetRawInputData(wintypes.HANDLE(lParam), RID_INPUT, None, byref(size),
                                   sizeof(RAWINPUTHEADER))
            buf = ctypes.create_string_buffer(size.value)
            if user32.GetRawInputData(wintypes.HANDLE(lParam), RID_INPUT, buf, byref(size),
                                      sizeof(RAWINPUTHEADER)) != size.value:
                return 0
            hdr = RAWINPUTHEADER.from_buffer_copy(buf.raw[:sizeof(RAWINPUTHEADER)])
            name = state["names"].get(hdr.hDevice)
            if name is None:
                name = device_name(hdr.hDevice)
                state["names"][hdr.hDevice] = name
                if is_ours(name):
                    state["ours"].add(hdr.hDevice)
                    write("%s  [device] OUR REMOTE: %s" % (ts(), name))
            if hdr.hDevice not in state["ours"]:
                state["other"] += 1
                return 0
            if hdr.dwType == RIM_TYPEKEYBOARD:
                state["kbd_events"] += 1
                k = ctypes.cast(ctypes.addressof(buf) + sizeof(RAWINPUTHEADER),
                                POINTER(RAWINPUTKEYBOARD_STRUCT)).contents
                kind = "UP" if (k.Flags & 0x01) else "DOWN"
                write("%s  KBD %-4s vkey=0x%02X (%s)  usage(MakeCode)=0x%02X" %
                      (ts(), kind, k.VKey, vk_name(k.VKey), k.MakeCode))
            elif hdr.dwType == RIM_TYPEHID:
                state["hid_reports"] += 1
                off = sizeof(RAWINPUTHEADER)
                sizeHid = wintypes.DWORD.from_buffer_copy(buf.raw[off:off + 4]).value
                count = wintypes.DWORD.from_buffer_copy(buf.raw[off + 4:off + 8]).value
                data = bytes(buf.raw[off + 8: off + 8 + sizeHid * count])
                write("%s  HID report sizeHid=%d count=%d  %s" % (ts(), sizeHid, count, data.hex(" ")))
            return 0
        return user32.DefWindowProcW(hwnd, msg, wParam, lParam)

    class RAWINPUTKEYBOARD_STRUCT(Structure):
        _fields_ = [("MakeCode", wintypes.USHORT), ("Flags", wintypes.USHORT),
                    ("Reserved", wintypes.USHORT), ("VKey", wintypes.USHORT),
                    ("Message", wintypes.UINT), ("ExtraInformation", ULONG_PTR)]

    state["names"] = {}
    proc = WNDPROC(wndproc)
    hinst = kernel32.GetModuleHandleW(None)
    wc = WNDCLASSW()
    wc.lpfnWndProc = proc
    wc.hInstance = hinst
    wc.lpszClassName = "IPRC1000SniffWnd"
    if not user32.RegisterClassW(byref(wc)):
        print("RegisterClassW failed:", ctypes.get_last_error())
        sys.exit(1)
    hwnd = user32.CreateWindowExW(0, "IPRC1000SniffWnd", None, 0, 0, 0, 0, 0,
                                  wintypes.HWND(HWND_MESSAGE), None, hinst, None)
    if not hwnd:
        print("CreateWindowExW failed:", ctypes.get_last_error())
        sys.exit(1)

    rid = (RAWINPUTDEVICE * 2)()
    rid[0].usUsagePage = 0x01   # generic desktop
    rid[0].usUsage = 0x06       # keyboard
    rid[0].dwFlags = RIDEV_INPUTSINK
    rid[0].hwndTarget = hwnd
    rid[1].usUsagePage = 0xFF00 # vendor-defined page
    rid[1].usUsage = 0x00
    rid[1].dwFlags = RIDEV_INPUTSINK | RIDEV_PAGEONLY
    rid[1].hwndTarget = hwnd
    if not user32.RegisterRawInputDevices(rid, 2, sizeof(RAWINPUTDEVICE)):
        print("RegisterRawInputDevices failed:", ctypes.get_last_error())
        sys.exit(1)

    user32.SetTimer(hwnd, 1, int(seconds * 1000), None)
    write("=== sniff session start %s, %ds, remote-only (VID_0A5C/PID_8502) ===" % (ts(), seconds))
    write("hint: press each remote button; then press and HOLD the MIC key ~5s")
    msg = wintypes.MSG()
    while user32.GetMessageW(byref(msg), None, 0, 0):
        user32.TranslateMessage(byref(msg))
        user32.DispatchMessageW(byref(msg))
    user32.KillTimer(hwnd, 1)
    summary = ("=== sniff session end %s: kbd=%d hid_reports=%d ignored_other_dev=%d ===" %
               (ts(), state["kbd_events"], state["hid_reports"], state["other"]))
    write(summary)
    logf.close()

class RAWINPUTKEYBOARD(Structure):
    _fields_ = [("MakeCode", wintypes.USHORT), ("Flags", wintypes.USHORT),
                ("Reserved", wintypes.USHORT), ("VKey", wintypes.USHORT),
                ("Message", wintypes.UINT), ("ExtraInformation", ULONG_PTR)]

LL_FLAGS = {0x01: "EXTENDED", 0x02: "LL_FLAGS_DESC?", 0x04: "VIRTUALVK", 0x08: "PROCESSKEYDOWN",
            0x10: "INJECTED", 0x20: "ALTDOWN", 0x40: "DOWN", 0x80: "UP"}

def ll_flag_names(flags):
    names = [n for bit, n in sorted(LL_FLAGS.items()) if flags & bit]
    return "|".join(names) if names else "0"

def mode_pair(seconds=90):
    """One session, two streams (Raw Input + WH_KEYBOARD_LL), correlated.

    Answers the three questions the IPRC1000 bridge design hinges on:
      1. which VK / MakeCode each physical button really produces;
      2. does the LL hook see the event before or after WM_INPUT arrives;
      3. is scanCode a device-safe discriminator (remote 'T' vs real keyboard 'T').
    """
    log_path = LOG_DIR + r"\iprc1000_pair.log"
    logf = open(log_path, "w", encoding="utf-8", buffering=1)
    t0 = time.perf_counter()
    state = {"names": {}, "ours": set(), "end": seconds, "half_shown": False,
             "raw_n": 0, "ll_n": 0, "hid_n": 0,
             "raw_pending": [], "ll_pending": []}

    def now():
        return (time.perf_counter() - t0) * 1000.0

    def ts():
        return datetime.datetime.now().strftime("%H:%M:%S.%f")[:-3]

    def write(line):
        print(line, flush=True)
        logf.write(line + "\n")

    def is_ours(name):
        return "0a5c" in name.lower() and "8502" in name.lower()

    def pair_with_raw(ll, is_down):
        """Find an unmatched remote raw event for this hook event; report delta."""
        for r in state["raw_pending"]:
            if r["matched"] or r["vk"] != ll["vk"] or r["down"] != is_down:
                continue
            if abs(r["t"] - ll["t"]) > 1500.0:
                continue
            r["matched"] = True
            d = ll["t"] - r["t"]
            who = "RAW-FIRST" if d > 0 else "HOOK-FIRST"
            write("%s  >>> PAIR %s  |Δ|=%6.1fms  vk=0x%02X (%s) scan=0x%03X make=0x%03X" %
                  (ts(), who, abs(d), ll["vk"], vk_name(ll["vk"]), ll["scan"], r["make"]))
            return True
        return False

    # ---- low-level hook ----
    def hook_proc(nCode, wParam, lParam):
        if nCode >= 0 and wParam in (WM_KEYDOWN, WM_KEYUP, WM_SYSKEYDOWN, WM_SYSKEYUP):
            kb = ctypes.cast(lParam, POINTER(KBDLLHOOKSTRUCT))[0]
            is_down = wParam in (WM_KEYDOWN, WM_SYSKEYDOWN)
            state["ll_n"] += 1
            item = {"t": now(), "vk": kb.vkCode, "scan": kb.scanCode,
                    "flags": kb.flags, "extra": kb.dwExtraInfo or 0, "matched": False}
            write("%s  HOOK %-4s vk=0x%02X (%s) scan=0x%03X flags=0x%02X(%s) extra=0x%X" %
                  (ts(), "DOWN" if is_down else "UP", kb.vkCode, vk_name(kb.vkCode),
                   kb.scanCode, kb.flags, ll_flag_names(kb.flags), item["extra"]))
            if not pair_with_raw(item, is_down):
                state["ll_pending"].append(item)
            del state["ll_pending"][:max(0, len(state["ll_pending"]) - 40)]
        return user32.CallNextHookEx(None, nCode, wParam, lParam)

    proc = HOOKPROC(hook_proc)

    # ---- raw input window ----
    def wndproc(hwnd, msg, wParam, lParam):
        if msg == WM_TIMER:
            if wParam == 2 and not state["half_shown"]:
                state["half_shown"] = True
                write("%s  ### PHASE B NOW: type on the REAL keyboard: t f n d h e m j c a b 2 3 g v i and arrows/enter ###" % ts())
                return 0
            user32.PostQuitMessage(0)
            return 0
        if msg != WM_INPUT:
            return user32.DefWindowProcW(hwnd, msg, wParam, lParam)

        size = wintypes.UINT(0)
        user32.GetRawInputData(wintypes.HANDLE(lParam), RID_INPUT, None, byref(size),
                               sizeof(RAWINPUTHEADER))
        buf = ctypes.create_string_buffer(size.value)
        if user32.GetRawInputData(wintypes.HANDLE(lParam), RID_INPUT, buf, byref(size),
                                  sizeof(RAWINPUTHEADER)) != size.value:
            return 0
        hdr = RAWINPUTHEADER.from_buffer_copy(buf.raw[:sizeof(RAWINPUTHEADER)])
        name = state["names"].get(hdr.hDevice)
        if name is None:
            name = device_name(hdr.hDevice)
            state["names"][hdr.hDevice] = name
            write("%s  [device] %s  ours=%s" % (ts(), name, is_ours(name)))
        if not is_ours(name):
            return 0

        if hdr.dwType == RIM_TYPEKEYBOARD:
            k = RAWINPUTKEYBOARD.from_buffer_copy(
                buf.raw[sizeof(RAWINPUTHEADER):sizeof(RAWINPUTHEADER) + sizeof(RAWINPUTKEYBOARD)])
            is_down = (k.Flags & 0x01) == 0
            state["raw_n"] += 1
            write("%s  RAW  %-4s vk=0x%02X (%s) make=0x%03X flags=0x%02X extra=0x%X  [%s]" %
                  (ts(), "DOWN" if is_down else "UP", k.VKey, vk_name(k.VKey),
                   k.MakeCode, k.Flags, k.ExtraInformation, name.split("#")[1][:28]))
            item = {"t": now(), "vk": k.VKey, "make": k.MakeCode, "down": is_down,
                    "matched": False}
            if not pair_with_raw_ll(item):
                state["raw_pending"].append(item)
            del state["raw_pending"][:max(0, len(state["raw_pending"]) - 40)]
        elif hdr.dwType == RIM_TYPEHID:
            off = sizeof(RAWINPUTHEADER)
            sizeHid = wintypes.DWORD.from_buffer_copy(buf.raw[off:off + 4]).value
            count = wintypes.DWORD.from_buffer_copy(buf.raw[off + 4:off + 8]).value
            data = bytes(buf.raw[off + 8: off + 8 + sizeHid * count])
            state["hid_n"] += 1
            write("%s  HID  sizeHid=%d count=%d  %s" % (ts(), sizeHid, count, data.hex(" ")))
        return 0

    def pair_with_raw_ll(raw):
        for ll in state["ll_pending"]:
            if ll["matched"] or ll["vk"] != raw["vk"] or ((ll["flags"] & 0x80) == 0) != raw["down"]:
                continue
            if abs(ll["t"] - raw["t"]) > 1500.0:
                continue
            ll["matched"] = True
            d = raw["t"] - ll["t"]
            who = "HOOK-FIRST" if d > 0 else "RAW-FIRST"
            write("%s  >>> PAIR %s  |Δ|=%6.1fms  vk=0x%02X (%s) scan=0x%03X make=0x%03X" %
                  (ts(), who, abs(d), raw["vk"], vk_name(raw["vk"]), ll["scan"], raw["make"]))
            return True
        return False

    hinst = kernel32.GetModuleHandleW(None)
    wc = WNDCLASSW()
    wc.lpfnWndProc = wndproc
    wc.hInstance = hinst
    wc.lpszClassName = "IPRC1000PairWnd"
    if not user32.RegisterClassW(byref(wc)):
        print("RegisterClassW failed:", ctypes.get_last_error())
        sys.exit(1)
    hwnd = user32.CreateWindowExW(0, "IPRC1000PairWnd", None, 0, 0, 0, 0, 0,
                                  wintypes.HWND(HWND_MESSAGE), None, hinst, None)
    if not hwnd:
        print("CreateWindowExW failed:", ctypes.get_last_error())
        sys.exit(1)

    rid = (RAWINPUTDEVICE * 3)()
    specs = [(0x01, 0x06, RIDEV_INPUTSINK),        # keyboard collection
             (0x0C, 0x01, RIDEV_INPUTSINK),        # consumer control
             (0xFF00, 0x00, RIDEV_INPUTSINK | RIDEV_PAGEONLY)]  # vendor collections
    for i, (page, usage, flags) in enumerate(specs):
        rid[i].usUsagePage, rid[i].usUsage, rid[i].dwFlags, rid[i].hwndTarget = page, usage, flags, hwnd
    if not user32.RegisterRawInputDevices(rid, 3, sizeof(RAWINPUTDEVICE)):
        print("RegisterRawInputDevices failed:", ctypes.get_last_error())
        sys.exit(1)

    hhook = user32.SetWindowsHookExW(WH_KEYBOARD_LL, proc, hinst, 0)
    if not hhook:
        print("SetWindowsHookExW failed:", ctypes.get_last_error())
        sys.exit(1)

    user32.SetTimer(hwnd, 1, int(seconds * 1000), None)
    user32.SetTimer(hwnd, 2, int(seconds * 500), None)
    write("=== pair session start %s (%ds) ===" % (ts(), seconds))
    write("### PHASE A NOW: press EVERY remote button once, with a ~0.5s gap. "
          "Then HOLD the mic/voice key ~5s. Then click Start and type on the REAL keyboard. ###")
    msg = wintypes.MSG()
    while user32.GetMessageW(byref(msg), None, 0, 0):
        user32.TranslateMessage(byref(msg))
        user32.DispatchMessageW(byref(msg))
    user32.KillTimer(hwnd, 1)
    user32.KillTimer(hwnd, 2)
    user32.UnhookWindowsHookEx(hhook)
    write("=== pair session end %s: raw=%d hook=%d hid=%d devices=%d ===" %
          (ts(), state["raw_n"], state["ll_n"], state["hid_n"], len(state["names"])))
    write("log: %s" % log_path)
    logf.close()

# ---------- mode: hidout (HID Output 报告扫描) ----------
# 假设①：主机写 HID Output 报告可作为"开始语音"开关。
# 该遥控器描述符定义了 1 字节 Output 报告（参考 wangchll/IPRC1000：
# Input/Output/Feature = 9/1/1）。本模式尝试打开设备并扫描输出值。
# 注意：键盘集合常被 kbdclass 独占，可能全部失败（会明确报告错误码）。
# 用法: python iprc1000_probe.py hidout [起始] [结束] [间隔ms]
GENERIC_WRITE = 0x40000000
FILE_SHARE_ALL = 0x00000003
_HID_DLL = ctypes.WinDLL("hid.dll", use_last_error=True)


def mode_hidout(start=0x00, end=0xFF, delay_ms=200):
    paths = find_hid_paths()
    if not paths:
        print("未找到 IPRC1000 HID 设备（是否已与电脑配对？）")
        sys.exit(1)
    path = paths[0]
    print("设备路径:", path)
    logf = open(LOG_DIR + r"\iprc1000_hidout.log", "a", encoding="utf-8", buffering=1)

    def ts():
        return datetime.datetime.now().strftime("%H:%M:%S.%f")[:-3]

    logf.write("=== hidout start %s range=%02X-%02X ===\n" % (ts(), start, end))
    print("=== 扫描 output 报告 %02X-%02X 间隔 %dms ===" % (start, end, delay_ms))

    handle, mode_desc = None, None
    for access, desc in ((GENERIC_WRITE, "GENERIC_WRITE"), (0, "0权限")):
        h = kernel32.CreateFileW(path, access, FILE_SHARE_ALL, None, 3, 0, None)
        if h != INVALID_HANDLE_VALUE:
            handle, mode_desc = h, desc
            print("打开成功: %s" % desc)
            break
        print("打开失败(%s) err=%d" % (desc, ctypes.get_last_error()))
    if handle is None:
        msg = "两种方式都无法打开设备 → Windows 无法写 Output 报告（键盘集合被独占）"
        print(msg)
        logf.write(msg + "\n")
        logf.close()
        return

    ok = fail = 0
    first_err = None
    try:
        for v in range(start, end + 1):
            buf = ctypes.create_string_buffer(bytes([v]), 1)
            r = _HID_DLL.HidD_SetOutputReport(handle, buf, 1)
            if not r:
                n = wintypes.DWORD(0)
                r2 = kernel32.WriteFile(handle, buf, 1, byref(n), None)
                if not r2:
                    fail += 1
                    err = ctypes.get_last_error()
                    if first_err is None:
                        first_err = err
                        print("写入失败 err=%d %s" % (err, "(AccessDenied 权限/独占)" if err == 5 else ""))
                else:
                    ok += 1
                    logf.write("%s out=%02X ok(WriteFile)\n" % (ts(), v))
            else:
                ok += 1
                logf.write("%s out=%02X ok(HidD)\n" % (ts(), v))
            time.sleep(delay_ms / 1000.0)
    except KeyboardInterrupt:
        print("\n中断")
    finally:
        kernel32.CloseHandle(handle)
        line = "=== hidout end %s: 成功 %d / 失败 %d (首错 %s) ===" % (ts(), ok, fail, first_err)
        print(line)
        logf.write(line + "\n")
        logf.close()


# ---------- main ----------
if __name__ == "__main__":
    mode = sys.argv[1] if len(sys.argv) > 1 else "list"
    if mode == "list":
        mode_list()
    elif mode == "keys":
        mode_keys()
    elif mode == "sniff":
        secs = int(sys.argv[2]) if len(sys.argv) > 2 else 60
        mode_sniff(secs)
    elif mode == "pair":
        secs = int(sys.argv[2]) if len(sys.argv) > 2 else 90
        mode_pair(secs)
    elif mode == "hidout":
        a = int(sys.argv[2], 0) if len(sys.argv) > 2 else 0x00
        b = int(sys.argv[3], 0) if len(sys.argv) > 3 else 0xFF
        d = int(sys.argv[4]) if len(sys.argv) > 4 else 200
        mode_hidout(a, b, d)
    else:
        print("unknown mode:", mode)
        sys.exit(2)
