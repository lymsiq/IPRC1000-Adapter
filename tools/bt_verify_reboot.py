#!/usr/bin/env python3
"""重启后一键验证：BthPort 的首片负载是否变完整了。

## 待验证的假设
实测（bth2_001.etl，从未重启过的机器）：
    ACL dlen=313 的事件 → BIP_Data 完整 313 字节
    ACL dlen=344 的事件 → BIP_Data **只有 8 字节**（ACL+L2CAP 头），340 字节丢失
⇒ 有效阈值 ∈ (317, 348]，**0x140 = 320** 正好落在区间内。

BthPort 的 `MaxEtwBytes` 默认值很可能就是 320，而它由 `bthport.sys` 在**驱动加载时**
读取一次。我们把 MaxEtwBytes 改成 8192 后**从未重启**，所以一直没生效。
若假设成立，重启后 348 字节的首片会被完整记录 → **每帧拿到全部 320 个样本，
不再需要 PLC 修补，声音直接就是清晰的。**

## 用法（管理员）
    python tools/bt_verify_reboot.py 30
30 秒内：按遥控器任意键唤醒 → 按住语音键说话 → 松开。
脚本会自己判断是否已重启、抓包、转换、并给出结论。
"""
import ctypes
import datetime as dt
import os
import re
import subprocess
import sys
import time
import winreg

DUR = int(sys.argv[1]) if len(sys.argv) > 1 else 30
WORK = r"F:\vibe"
ETL = os.path.join(WORK, "verify.etl")
XML = os.path.join(WORK, "verify.xml")
SESS = "Iprc1000Verify"
REG_PATH = r"SYSTEM\CurrentControlSet\Services\BthPort\Parameters"

PROVIDERS = ["Microsoft-Windows-BTH-BTHPORT"]


def run(cmd):
    return subprocess.run(cmd, shell=True, capture_output=True, text=True, errors="replace")


def is_admin():
    try:
        return bool(ctypes.windll.shell32.IsUserAnAdmin())
    except Exception:
        return False


def reg_state():
    """返回 (值字典, 键最后修改时间, 系统启动时间)。"""
    vals = {}
    last_write = None
    with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, REG_PATH, 0, winreg.KEY_READ) as k:
        n_sub, n_val, last_write = winreg.QueryInfoKey(k)
        for name in ("EtwDropLargeEvents", "EtwLogSensitiveData", "MaxEtwBytes"):
            try:
                vals[name] = winreg.QueryValueEx(k, name)[0]
            except FileNotFoundError:
                vals[name] = None
    boot = dt.datetime.fromtimestamp(time.time() - ctypes.windll.kernel32.GetTickCount64() / 1000.0)
    return vals, dt.datetime.fromtimestamp(last_write / 1e7 - 11644473600), boot


def main():
    if not is_admin():
        print("[!] 需要管理员权限运行")
        return 1

    vals, key_mtime, boot = reg_state()
    print("[i] 注册表:")
    for k, v in vals.items():
        print(f"      {k} = {v}")
    print(f"[i] 注册表最后修改: {key_mtime:%Y-%m-%d %H:%M:%S}")
    print(f"[i] 系统启动时间  : {boot:%Y-%m-%d %H:%M:%S}")
    if key_mtime > boot:
        print("\n[!] 注册表是在**本次启动之后**改的 —— bthport.sys 还没读到新值！")
        print("    → 请先**重启电脑**，然后重新运行本脚本。")
        return 2
    print("[✓] 注册表修改早于本次启动，配置应已生效，继续抓包。\n")

    run(f'logman stop "{SESS}" -ets')
    run(f'logman delete "{SESS}"')
    for f in (ETL, XML):
        if os.path.exists(f):
            os.remove(f)
    r = run(f'logman create trace -n "{SESS}" -o "{ETL}" -nb 128 640 -bs 128')
    if r.returncode != 0:
        print("[!] 创建会话失败:", r.stdout, r.stderr)
        return 1
    for p in PROVIDERS:
        run(f'logman update trace -n "{SESS}" -p "{p}" 0xffffffffffffffff 0xff')
    r = run(f'logman start -n "{SESS}"')
    if r.returncode != 0:
        print("[!] 启动失败:", r.stdout, r.stderr)
        return 1

    print(f"[●] 抓包 {DUR} 秒 —— 请按遥控器任意键唤醒，然后按住语音键说话…")
    for i in range(DUR, 0, -5):
        print(f"    剩余 {i}s", flush=True)
        time.sleep(5)
    run(f'logman stop -n "{SESS}" -ets')
    run(f'logman delete "{SESS}"')

    real = next((c for c in (ETL, ETL.replace(".etl", "_000001.etl"))
                 if os.path.exists(c) and os.path.getsize(c) > 0), None)
    if not real:
        print("[!] 未生成 ETL（可能没有蓝牙流量）")
        return 1
    print(f"[i] ETL {os.path.getsize(real)} 字节 → 转换…")
    run(f'tracerpt "{real}" -o "{XML}" -of XML -lr -y')

    d = open(XML, encoding="utf-8", errors="replace").read()
    ev = re.compile(
        r'<Data Name="BIP_Type">(\d+)</Data>\s*'
        r'<Data Name="BIP_DataLen">\s*(\d+)</Data>\s*'
        r'<Data Name="BIP_Data">0x([0-9A-Fa-f]*)</Data>', re.S)
    from collections import Counter
    lens = Counter()
    # 关键指纹：ACL dlen=344 的起始分片，看它带了多少负载
    first344 = Counter()      # payload_len -> 次数
    cont313 = Counter()       # 续片
    for m in ev.finditer(d):
        if int(m.group(1)) != 3:
            continue
        raw = bytes.fromhex(m.group(3))
        lens[len(raw)] += 1
        if len(raw) >= 4:
            dlen = int.from_bytes(raw[2:4], "little")
            flag = (int.from_bytes(raw[0:2], "little") >> 12) & 0x3
            carried = len(raw) - 4
            if flag == 2 and dlen == 344:      # 音频起始分片
                first344[carried] += 1
            if flag == 1 and dlen == 313:      # 音频续片
                cont313[carried] += 1

    print("\n[i] BIP_Type=3 事件负载长度分布:", dict(sorted(lens.items())))
    print(f"[i] 音频续片(dlen=313) 携带负载: {dict(cont313)}")
    print(f"[i] 音频起始分片(dlen=344) 携带负载: {dict(first344)}   ← 关键")

    if not first344 and not cont313:
        print("\n[!] 本轮**完全没有音频流量** —— 没有按语音键，或遥控器没连上。")
        print("    请：① 按遥控器任意键唤醒；② 按住语音键说话；③ 重跑本脚本。")
        return 4
    # 判据：dlen=344 的起始分片是否带够了负载（旧情况只有 4 字节 = 仅 ACL 头）
    best = max(first344) if first344 else 0
    if best >= 200:
        print(f"\n[✓✓✓] 首片负载已完整（携带 {best} 字节，应为 344）")
        print("      → 每帧 320 个样本全部到手，可以直接出干净音频，不再需要 PLC。")
        return 0
    if first344:
        print(f"\n[✗] 首片仍被截断（只携带 {best} 字节，应 344）")
        print("    → MaxEtwBytes 在重启后仍未生效，需走 USBPcap / Linux btmon 路线。")
        return 3
    print("\n[?] 只抓到续片，没抓到起始分片，样本不足以判定，请再抓一次。")
    return 5


if __name__ == "__main__":
    sys.exit(main())
