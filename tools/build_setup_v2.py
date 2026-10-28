#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
用「新版安装器」（tools/setup_v2，移植自 DinggeVoiceHub 的 InstallEngine + 安装页面）
给 IPRC1000 Adapter 打单文件安装包。

与旧版 tools/build_setup.py 的区别：
  旧版：自己写的极简引导程序（解压 + 快捷方式 + 卸载项）
  新版：作者那套完整引擎 —— 可选安装位置、逐文件哈希校验、组件检测与按需安装、
        蓝牙参数备份/恢复、修复模式、安装状态机、卸载清单。

流程：免运行时版目录 → 生成 package-manifest.json → 打 payload.zip → 编译 net48 引导程序

用法：
    python tools/build_setup_v2.py                 # 用默认的 IPRC1000_Adapter_full 目录
    python tools/build_setup_v2.py --source <dir> --out <dir>
"""
import argparse
import hashlib
import io
import json
import os
import shutil
import subprocess
import sys
import tempfile
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SETUP = os.path.join(ROOT, "tools", "setup_v2")
CSPROJ_APP = os.path.join(ROOT, "iprc1000_gui_dotnet", "IPRC1000Adapter.csproj")
PRODUCT = "IPRC1000_Adapter"          # 必须等于 SetupCore.Product.DirectoryName
EXE_NAME = "IPRC1000 Adapter.exe"      # 必须等于 SetupCore.Product.ExeName


def app_version() -> str:
    txt = io.open(CSPROJ_APP, encoding="utf-8").read()
    import re
    m = re.search(r"<Version>([0-9][0-9A-Za-z.\-]*)</Version>", txt)
    if not m:
        raise SystemExit("✗ 读不到 <Version>")
    return m.group(1)


def sha256_file(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def build_payload(src: str, version: str, dest_zip: str) -> dict:
    """把 src 目录打成带 package-manifest.json 的 payload.zip。"""
    if not os.path.isdir(src):
        raise SystemExit(f"✗ 找不到源目录：{src}")
    if not os.path.isfile(os.path.join(src, EXE_NAME)):
        raise SystemExit(f"✗ 源目录里没有 {EXE_NAME}")
    if not os.path.isfile(os.path.join(src, "assets", "etw_consumer.exe")):
        raise SystemExit("✗ 源目录缺少 assets/etw_consumer.exe（安装器校验必需）")

    files = []
    for base, _, names in os.walk(src):
        for n in names:
            p = os.path.join(base, n)
            rel = os.path.relpath(p, src).replace(os.sep, "/")
            files.append({"Path": rel, "Size": os.path.getsize(p), "Sha256": sha256_file(p)})
    files.sort(key=lambda x: x["Path"])

    manifest = {"Product": PRODUCT, "Version": version,
                "SelfContained": True, "Files": files}

    with zipfile.ZipFile(dest_zip, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("package-manifest.json",
                   json.dumps(manifest, ensure_ascii=False, indent=2))
        for f in files:
            z.write(os.path.join(src, f["Path"].replace("/", os.sep)), f["Path"])
    return manifest


def main() -> int:
    ap = argparse.ArgumentParser()
    # 用**免运行时版**（自包含）：装机即用，不需要目标机预装 .NET 8
    ap.add_argument("--source", default=os.path.join(ROOT, PRODUCT + "_full"))
    ap.add_argument("--out", default=os.path.join(ROOT, "_setup_v2_out"))
    args = ap.parse_args()

    version = app_version()
    print(f"=== 用新版安装器构建 IPRC1000 Adapter v{version} ===")
    os.makedirs(args.out, exist_ok=True)

    payload = os.path.join(SETUP, "payload.zip")
    manifest = build_payload(args.source, version, payload)
    size_mb = os.path.getsize(payload) / 1048576
    print(f"  payload：{len(manifest['Files'])} 个文件 / {size_mb:.1f} MB（含清单）")

    r = subprocess.run(["dotnet", "build", "Setup.csproj", "-c", "Release", "-v", "q", "--nologo",
                        f"-p:AppVersion={version}"],
                       cwd=SETUP, capture_output=True, text=True, encoding="utf-8", errors="replace")
    import re
    errs = [l for l in (r.stdout or "").splitlines() if re.search(r"\berror\s+(CS|MSB|NETSDK)\d+", l)]
    if r.returncode != 0 or errs:
        print("  ✗ 编译失败：")
        print("\n".join(errs[:8]) or (r.stdout or "")[-600:])
        return 1

    built = os.path.join(SETUP, "bin", "Release", "net48", "IPRC1000AdapterSetup.exe")
    if not os.path.isfile(built):
        print("  ✗ 没生成 IPRC1000AdapterSetup.exe")
        return 1

    out_exe = os.path.join(args.out, f"IPRC1000_Setup_v{version}.exe")
    shutil.copy2(built, out_exe)
    os.remove(payload)

    h = sha256_file(out_exe)
    with open(out_exe + ".sha256", "w", encoding="utf-8") as f:
        f.write(f"{h}  {os.path.basename(out_exe)}\n")

    raw = open(out_exe, "rb").read()
    ok = {
        "管理员清单": b"requireAdministrator" in raw,
        "内置载荷": b"payload" in raw,
        "体积 >30MB": len(raw) > 30 * 1024 * 1024,
    }
    print(f"  ✓ {os.path.basename(out_exe)}  {len(raw)/1048576:.1f} MB")
    for k, v in ok.items():
        print(f"    {k}: {'✓' if v else '✗'}")
    print(f"    sha256: {h}")
    return 0 if all(ok.values()) else 1


if __name__ == "__main__":
    sys.exit(main())
