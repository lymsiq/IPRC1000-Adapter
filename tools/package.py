#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""IPRC1000 原生版打包 —— 唯一入口。

    python tools/package.py            # 打包当前版本
    python tools/package.py --check    # 只校验，不重建

## 为什么要有这个脚本（2026-11-01）

手工打包踩过三类事故，全部由"顺序靠人记"引起：

1. **包比代码旧**：`src/payload.zip` 是 `include_bytes!` 内嵌的，忘了重建就会
   打出"安装器装出旧版本"的包（真发生过两次，其中一次差点发出去）。
2. **构建顺序错**：必须先编主程序 → 再生成 payload → 再编 setup，
   顺序反了 setup 内嵌的还是上一版。
3. **文件锁**：`target/release/.fingerprint` 偶发被占（os error 5，杀软扫描），
   每次都要手工清目录。这里自动清一次并重试。

所以本脚本把顺序固定下来，并在每一步做**一致性校验**：
payload 里的 exe 与 target/release 的 exe 必须是同一个哈希，
否则直接失败——宁可不出包，也不出"看起来成功"的错包。
"""

import hashlib
import os
import shutil
import subprocess
import sys
import time
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DIST = os.path.dirname(ROOT)                       # F:\vibe
DEPLOY = r"D:\IPRC1000-Native"                     # 图标等静态资源的来源
TARGET = os.path.join(ROOT, "target", "release")
PAYLOAD = os.path.join(ROOT, "src", "payload.zip")

ICONS = ["icon-16.png", "icon-20.png", "icon-32.png", "icon-48.png",
         "icon-64.png", "icon.ico"]


def sha(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def version():
    with open(os.path.join(ROOT, "Cargo.toml"), encoding="utf-8") as f:
        for line in f:
            if line.startswith("version"):
                return line.split('"')[1]
    raise SystemExit("Cargo.toml 里找不到 version")


def clear_fingerprint():
    """清掉被占用的指纹目录（杀软扫描导致的 os error 5）。"""
    base = os.path.join(TARGET, ".fingerprint")
    if not os.path.isdir(base):
        return
    for name in os.listdir(base):
        if name.startswith("iprc1000_native-"):
            shutil.rmtree(os.path.join(base, name), ignore_errors=True)


def build(what="主程序"):
    """cargo build --release，遇到文件锁自动清一次重试。"""
    for attempt in (1, 2):
        print(f"  编译{what}（第 {attempt} 次）…")
        r = subprocess.run(["cargo", "build", "--release"], cwd=ROOT,
                           capture_output=True, text=True, encoding="utf-8", errors="replace")
        if r.returncode == 0:
            return
        if attempt == 1 and ("os error 5" in r.stderr or "拒绝访问" in r.stderr):
            print("  ⚠ 构建产物被占用（杀软扫描），清理指纹目录后重试")
            clear_fingerprint()
            time.sleep(1)
            continue
        print(r.stderr[-1500:])
        raise SystemExit(f"编译{what}失败")


def make_payload():
    """把程序目录打成 payload.zip（setup 用 include_bytes! 内嵌）。"""
    stg = os.path.join(ROOT, "_payload_stage")
    shutil.rmtree(stg, ignore_errors=True)
    os.makedirs(stg)
    shutil.copy2(os.path.join(TARGET, "iprc1000_native.exe"),
                 os.path.join(stg, "IPRC1000-Native.exe"))
    for n in ["WinUHid.dll"] + ICONS:
        shutil.copy2(os.path.join(DEPLOY, n), os.path.join(stg, n))
    shutil.copy2(os.path.join(ROOT, "使用说明.txt"), os.path.join(stg, "使用说明.txt"))
    shutil.copy2(os.path.join(ROOT, "一键安装.cmd"), os.path.join(stg, "一键安装.cmd"))
    shutil.copytree(os.path.join(DEPLOY, "assets"), os.path.join(stg, "assets"))

    if os.path.exists(PAYLOAD):
        os.remove(PAYLOAD)
    with zipfile.ZipFile(PAYLOAD, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for root, _, files in os.walk(stg):
            for f in sorted(files):
                p = os.path.join(root, f)
                z.write(p, os.path.relpath(p, stg))
    shutil.rmtree(stg, ignore_errors=True)


def verify_payload_matches_exe():
    """★ 防漂移核心：payload 里的 exe 必须与刚编译出来的 exe 完全一致。"""
    with zipfile.ZipFile(PAYLOAD) as z:
        data = z.read("IPRC1000-Native.exe")
    a = hashlib.sha256(data).hexdigest()
    b = sha(os.path.join(TARGET, "iprc1000_native.exe"))
    if a != b:
        raise SystemExit(
            "✗ payload 里的 exe 与刚编译出来的不是同一个！\n"
            f"    payload: {a[:16]}…\n"
            f"    target : {b[:16]}…\n"
            "  （说明 payload 没重建，或 setup 内嵌的是旧 payload）")
    return a


def make_dist(v):
    """生成两个分发件，返回各自的路径与哈希。"""
    stg = os.path.join(DIST, f"_pkg_native_v{v.replace('.', '')}")
    shutil.rmtree(stg, ignore_errors=True)
    os.makedirs(stg)
    shutil.copy2(os.path.join(TARGET, "iprc1000_native.exe"),
                 os.path.join(stg, "IPRC1000-Native.exe"))
    for n in ["WinUHid.dll"] + ICONS:
        shutil.copy2(os.path.join(DEPLOY, n), os.path.join(stg, n))
    shutil.copy2(os.path.join(ROOT, "使用说明.txt"), os.path.join(stg, "使用说明.txt"))
    shutil.copy2(os.path.join(ROOT, "一键安装.cmd"), os.path.join(stg, "一键安装.cmd"))
    shutil.copytree(os.path.join(DEPLOY, "assets"), os.path.join(stg, "assets"))

    zip_path = os.path.join(DIST, f"IPRC1000_Native_v{v}.zip")
    if os.path.exists(zip_path):
        os.remove(zip_path)
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for root, _, files in os.walk(stg):
            for f in sorted(files):
                p = os.path.join(root, f)
                z.write(p, os.path.relpath(p, stg))
    shutil.rmtree(stg, ignore_errors=True)

    setup_path = os.path.join(DIST, f"IPRC1000_Native_Setup_v{v}.exe")
    shutil.copy2(os.path.join(TARGET, "setup.exe"), setup_path)
    return zip_path, setup_path


def check_strings(path, expected):
    """发行件里应当能搜到这些标记串（漏了就说明打错包了）。"""
    data = open(path, "rb").read()
    missing = [s for s in expected if s.encode("utf-8") not in data]
    return missing


def main():
    v = version()
    print(f"IPRC1000 原生版打包 · 版本 {v}")
    print(f"  源码 {ROOT}")

    if "--check" in sys.argv:
        h = verify_payload_matches_exe()
        print(f"  ✓ payload 与 exe 一致（{h[:16]}…）")
        return

    # ① 主程序
    build("主程序")
    exe = os.path.join(TARGET, "iprc1000_native.exe")
    if not os.path.exists(exe):
        raise SystemExit("✗ 没找到编译好的 exe")
    exe_hash = sha(exe)
    print(f"  ✓ 主程序 {os.path.getsize(exe) / 1048576:.2f} MB  {exe_hash[:16]}…")

    # ② payload（必须先有主程序）
    make_payload()
    print(f"  ✓ payload {os.path.getsize(PAYLOAD) / 1048576:.2f} MB")

    # ③ setup（include_bytes! 内嵌 payload，必须重编）
    build("安装器")

    # ④ 校验：payload ↔ exe 一致（防"包比代码旧"）
    h = verify_payload_matches_exe()
    print(f"  ✓ 一致性校验通过（{h[:16]}…）")

    # ⑤ 分发件
    zip_path, setup_path = make_dist(v)
    print(f"  ✓ {os.path.basename(zip_path)}  {os.path.getsize(zip_path) / 1048576:.2f} MB")
    print(f"  ✓ {os.path.basename(setup_path)}  {os.path.getsize(setup_path) / 1048576:.2f} MB")

    # ⑥ 标记串校验：漏了说明打错包
    need = ["IPRC1000-Native", "一键安装"]
    miss = check_strings(setup_path, need)
    if miss:
        raise SystemExit(f"✗ Setup 里缺少标记串 {miss}")

    print()
    print(f"zip     sha256 {sha(zip_path)}")
    print(f"setup   sha256 {sha(setup_path)}")
    print(f"\n版本 {v} 打包完成。别忘了：git tag -a v{v}")


if __name__ == "__main__":
    main()
