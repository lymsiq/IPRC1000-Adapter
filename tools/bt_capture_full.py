#!/usr/bin/env python3
"""一次到位：配置注册表 → 多提供程序抓包 → 转 XML → 判定首片负载是否完整。

为什么要多提供程序：
  BthPort 的 ETW 对「L2CAP 起始分片」只记 8 字节（ACL+L2CAP 头），340 字节负载永久丢失，
  这是 BthPort 自身行为（已在 ETL 原始字节中证实）。所以向上游/下游找完整帧：
    - BTH-BTHUSB  : USB 传输层（本机蓝牙是 USB\\VID_8087&PID_0029，HCI 走 USB bulk）
    - Bthmini     : 蓝牙微型端口驱动
    - bthhid      : 蓝牙 HID 传输驱动（在 HID 层，可能直接拿到 0xF7 报告）
    - BTH-BTHPORT : 基线对照

用法（管理员）:
    python bt_capture_full.py 45          # 抓 45 秒
抓包期间请：按遥控器任意键唤醒 → 按住语音键说话 → 松开。
"""
import os
import re
import subprocess
import sys
import time

DUR = int(sys.argv[1]) if len(sys.argv) > 1 else 45
WORK = r"F:\vibe"
ETL = os.path.join(WORK, "full.etl")
SESS = "Iprc1000Full"

# 提供程序: 关键字掩码(全部) 级别
PROVIDERS = [
    ("Microsoft-Windows-BTH-BTHPORT", "0xffffffffffffffff"),
    ("Microsoft-Windows-BTH-BTHUSB", "0xffffffffffffffff"),
    ("Microsoft-Windows-Bluetooth-Bthmini", "0xffffffffffffffff"),
    ("{5794AE18-A6E8-4348-A3FB-0EB1C0AD14B0}", "0xffffffffffffffff"),  # bthhid
    ("{DDB6DA39-08A7-4579-8D0C-68011146E205}", "0xffffffffffffffff"),  # btha2dp
]


def run(cmd, **kw):
    return subprocess.run(cmd, shell=True, capture_output=True, text=True,
                          errors="replace", **kw)


def is_admin():
    try:
        import ctypes
        return bool(ctypes.windll.shell32.IsUserAnAdmin())
    except Exception:
        return False


def main():
    if not is_admin():
        print("[!] 需要管理员权限运行")
        return 1

    # 1) 注册表
    reg = r"HKLM\SYSTEM\CurrentControlSet\Services\BthPort\Parameters"
    for name, val in (("EtwDropLargeEvents", 0), ("EtwLogSensitiveData", 1),
                      ("MaxEtwBytes", 1024)):
        run(f'reg add "{reg}" /v {name} /t REG_DWORD /d {val} /f')
    print("[i] 注册表已设置: EtwDropLargeEvents=0 EtwLogSensitiveData=1 MaxEtwBytes=1024")
    print("    注意: MaxEtwBytes 由 bthport.sys 在驱动加载时读取，")
    print("    若从未在设置后重启过，请重启一次再抓包（这是当前最可疑的修复点）。")

    run(f'logman stop "{SESS}" -ets')
    run(f'logman delete "{SESS}"')

    if os.path.exists(ETL):
        os.remove(ETL)
    r = run(f'logman create trace "{SESS}" -o "{ETL}" -nb 128 640 -bs 128 -f bincirc')
    if r.returncode != 0:
        print("[!] 创建会话失败:", r.stdout, r.stderr)
        return 1
    for prov, kw in PROVIDERS:
        rr = run(f'logman update trace "{SESS}" -p "{prov}" {kw} 0xff')
        print(f"    + {prov}: {'OK' if rr.returncode == 0 else rr.stdout.strip()[:80]}")
    r = run(f'logman start "{SESS}"')
    if r.returncode != 0:
        print("[!] 启动失败:", r.stdout, r.stderr)
        return 1

    print(f"\n[●] 开始抓包 {DUR} 秒 —— 请立刻按遥控器任意键唤醒，然后按住语音键说话 …")
    for i in range(DUR, 0, -5):
        print(f"    剩余 {i}s", flush=True)
        time.sleep(5)
    run(f'logman stop "{SESS}" -ets')
    run(f'logman delete "{SESS}"')

    real = None
    for cand in (ETL, ETL.replace(".etl", "_000001.etl")):
        if os.path.exists(cand) and os.path.getsize(cand) > 0:
            real = cand
            break
    if not real:
        print("[!] 未生成 ETL")
        return 1
    print(f"[i] ETL: {real}  {os.path.getsize(real)} 字节")

    xml = os.path.join(WORK, "full.xml")
    run(f'tracerpt "{real}" -o "{xml}" -of XML -lr -y')
    if not os.path.exists(xml):
        print("[!] tracerpt 转换失败")
        return 1

    # 3) 判定
    d = open(xml, encoding="utf-8", errors="replace").read()
    ev = re.compile(
        r'<Provider Name="([^"]*)".*?<EventID>(\d+)</EventID>.*?'
        r'<Data Name="BIP_DataLen">\s*(\d+)</Data>\s*'
        r'<Data Name="BIP_Data">0x([0-9A-Fa-f]*)</Data>', re.S)
    from collections import Counter
    per_prov = Counter()
    lens = Counter()
    maxlen = 0
    for m in ev.finditer(d):
        per_prov[m.group(1)] += 1
        n = len(m.group(4)) // 2
        lens[(m.group(1), n)] += 1
        maxlen = max(maxlen, n)
    print("\n[i] 各提供程序事件数:", dict(per_prov))
    print("[i] (提供程序, 负载字节) Top12:")
    for (p, n), c in lens.most_common(12):
        print(f"     {p}  {n:5d} B  ×{c}")

    if maxlen and maxlen > 317:
        print(f"\n[✓✓] 发现 {maxlen} 字节负载 —— 上游提供程序给出了完整帧！")
    else:
        print(f"\n[✗] 最大负载仍为 {maxlen} 字节（313 音频）—— 该层也没拿到完整帧")
    return 0


if __name__ == "__main__":
    sys.exit(main())
