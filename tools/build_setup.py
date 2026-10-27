#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
构建单文件安装程序：IPRC1000_Setup_v<版本>.exe

做什么：
  1. 从 IPRC1000Adapter.csproj 读版本号，写进引导程序（csproj + Program.cs）；
  2. 把**免运行时版** zip 作为载荷（payload.zip）嵌进去 —— 这样装完即用，不需要 .NET 8；
  3. dotnet build（net48：Win10 1903+ / Win11 自带运行时，用户端零依赖）；
  4. 产物复制到仓库根：IPRC1000_Setup_v<版本>.exe

前置：先跑 `python tools/package.py`（它会生成 IPRC1000_Adapter_full.zip）。

用法：
    python tools/build_setup.py            # 正常构建
    python tools/build_setup.py --no-build # 只同步版本与载荷，不编译
"""
import io
import os
import re
import shutil
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SETUP = os.path.join(ROOT, "tools", "setup")
CSPROJ_APP = os.path.join(ROOT, "iprc1000_gui_dotnet", "IPRC1000Adapter.csproj")
CSPROJ_SETUP = os.path.join(SETUP, "Setup.csproj")
PROGRAM = os.path.join(SETUP, "Program.cs")
PAYLOAD_SRC = os.path.join(ROOT, "IPRC1000_Adapter_full.zip")
PAYLOAD_DST = os.path.join(SETUP, "payload.zip")


def app_version() -> str:
    txt = io.open(CSPROJ_APP, encoding="utf-8").read()
    m = re.search(r"<Version>([0-9][0-9.]*)</Version>", txt)
    if not m:
        raise SystemExit("✗ 从 IPRC1000Adapter.csproj 读不到 <Version>")
    return m.group(1)


def sync_version(ver: str) -> None:
    # csproj 的 <Version>
    t = io.open(CSPROJ_SETUP, encoding="utf-8").read()
    t2, n = re.subn(r"<Version>[0-9][0-9.]*</Version>", f"<Version>{ver}</Version>", t)
    if n:
        io.open(CSPROJ_SETUP, "w", encoding="utf-8").write(t2)
    # Program.cs 里两处常量（Program.Version + Program2.Version）
    p = io.open(PROGRAM, encoding="utf-8").read()
    p2, n2 = re.subn(r'const string Version = "[0-9][0-9.]*";', f'const string Version = "{ver}";', p)
    io.open(PROGRAM, "w", encoding="utf-8").write(p2)
    print(f"  版本 → {ver}（{n + n2} 处已同步）")


def main() -> int:
    ver = app_version()
    print(f"=== 构建安装程序 v{ver} ===")
    sync_version(ver)

    if not os.path.exists(PAYLOAD_SRC):
        print(f"  ✗ 找不到 {PAYLOAD_SRC} —— 请先运行 python tools/package.py")
        return 1
    shutil.copy2(PAYLOAD_SRC, PAYLOAD_DST)
    print(f"  载荷：{os.path.getsize(PAYLOAD_DST) / 1048576:.1f} MB（免运行时版）")

    if "--no-build" in sys.argv:
        return 0

    r = subprocess.run(["dotnet", "build", "-c", "Release", "-v", "q", "--nologo"],
                       cwd=SETUP, capture_output=True, text=True, encoding="utf-8", errors="replace")
    # ⚠ 不能按"错误"两个字判断 —— 成功时 dotnet 会打印"0 个错误"，那也含"错误"。
    #   只认退出码 + `error CSxxxx` / `error MSBxxxx` 这种真实错误行。
    bad = [l for l in (r.stdout or "").splitlines()
           if re.search(r"\berror\s+(CS|MSB|NETSDK)\d+", l)]
    if r.returncode != 0 or bad:
        print("  ✗ 编译失败：")
        print("\n".join(bad[:8]) or (r.stdout or "")[-800:])
        return 1

    exe = os.path.join(SETUP, "bin", "Release", "net48", "IPRC1000_Setup.exe")
    if not os.path.exists(exe):
        print("  ✗ 没生成 IPRC1000_Setup.exe")
        return 1
    dst = os.path.join(ROOT, f"IPRC1000_Setup_v{ver}.exe")
    shutil.copy2(exe, dst)

    # 自检：必须是管理员清单（否则装不到 Program Files）
    raw = open(dst, "rb").read()
    has_admin = b"requireAdministrator" in raw
    has_payload = b"payload.zip" in raw
    size = len(raw) / 1048576
    print(f"  ✓ {os.path.basename(dst)}  {size:.1f} MB")
    print(f"    管理员清单: {'✓' if has_admin else '✗'}   内置载荷: {'✓' if has_payload else '✗'}")
    print(f"    自检: {'全部通过 ✓' if has_admin and has_payload and size > 50 else '★ 有异常'}")
    return 0 if (has_admin and has_payload and size > 50) else 1


if __name__ == "__main__":
    sys.exit(main())
