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
import re
import shutil
import subprocess
import sys
import time
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DIST = os.path.dirname(ROOT)                       # F:\vibe
# ★ 2026-11-06：静态资源的**唯一来源**必须是源码树，不能是开发机部署目录。
#   原来指向 D:\IPRC1000-Native\assets：那台机器上的 configure-xiaomi-audio.ps1
#   是旧名（17092 B），而源码 commands.rs 只找 configure-audio.ps1（17957 B），
#   于是打出来的 Native zip / payload / Setup 全都装不上默认麦克风，且安装器仍报成功。
#   ROOT/assets 已由 git 跟踪（见 .gitignore 白名单），换机构建结果一致。
ASSETS = os.path.join(ROOT, "assets")
TARGET = os.path.join(ROOT, "target", "release")
PAYLOAD = os.path.join(ROOT, "src", "payload.zip")

ICONS = ["icon-16.png", "icon-20.png", "icon-32.png", "icon-48.png",
         "icon-64.png", "icon.ico"]

# 包内必须存在的文件（相对包根）。缺一个就不出包。
REQUIRED = [
    "IPRC1000-Native.exe",
    "WinUHid.dll",
    "使用说明.txt",
    "一键安装.cmd",
    "assets/configure-audio.ps1",
    "assets/VBCABLE_Driver_Pack45.zip",
    "assets/winuhid/WinUHid.dll",
    "assets/winuhid/WinUHidPublisher.cer",
    "assets/winuhid/driver/WinUHidDriver.inf",
    "assets/winuhid/driver/WinUHidDriver.cat",
    "assets/winuhid/driver/WinUHidDriver.dll",
] + ICONS

# 绝不能再混进发行件的历史文件名（与源码查找名不一致 = 客户机必失败）
FORBIDDEN = ["assets/configure-xiaomi-audio.ps1"]


def verify_stage(stg):
    """打包前的内容断言：包里有什么，比包叫什么名字重要得多。"""
    missing = [n for n in REQUIRED if not os.path.isfile(os.path.join(stg, n))]
    if missing:
        raise SystemExit(f"✗ 包内缺文件：{missing}\n  资源源 {ASSETS}（不要用开发机部署目录）")
    stale = [n for n in FORBIDDEN if os.path.exists(os.path.join(stg, n))]
    if stale:
        raise SystemExit(f"✗ 包内混进了旧文件：{stale}\n  请清理 {ASSETS} 后重打")

    # 脚本名必须与源码查找的名字一致 —— 单方面改任何一边都要在这里炸掉
    with open(os.path.join(ROOT, "src", "commands.rs"), encoding="utf-8") as f:
        cmd_src = f.read()
    names = set(re.findall(r'"(configure-[\w-]+\.ps1)"', cmd_src))
    if not names:
        raise SystemExit("✗ commands.rs 里找不到 configure-*.ps1 名字，断言失效")
    for n in names:
        if not os.path.isfile(os.path.join(stg, "assets", n)):
            raise SystemExit(f"✗ 源码要找 assets/{n}，包里却没有")

    # 安装脚本引用的 exe 必须在包里（Win32 变体曾因复用这份 cmd 直接断链）
    cmd_path = os.path.join(stg, "一键安装.cmd")
    with open(cmd_path, "rb") as f:
        raw = f.read()
    text = raw.decode("gbk", errors="replace")
    refs = set(re.findall(r'([\w-]+\.exe)\s+--install', text, re.I))
    if not refs:
        raise SystemExit("✗ 一键安装.cmd 里找不到 `--install` 调用，断言失效")
    for exe in refs:
        if not os.path.isfile(os.path.join(stg, exe)):
            raise SystemExit(f"✗ 一键安装.cmd 调 {exe}，包里却没有这个文件")
    print(f"  ✓ 内容断言通过（{len(REQUIRED)} 项必需 / 脚本 {sorted(names)} / 安装入口 {sorted(refs)}）")


def verify_dist(zip_path):
    """对已产出的 zip 做同样一套断言（--check 时验现有包，不重建）。"""
    import tempfile
    with tempfile.TemporaryDirectory() as tmp:
        with zipfile.ZipFile(zip_path) as z:
            z.extractall(tmp)
        verify_stage(tmp)


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


def msvc_env():
    """补齐 MSVC 的编译/链接环境（PATH + LIB + INCLUDE）。

    ★ 2026-11-01 踩坑：删掉 target/ 之后全量重编，build script 需要在**宿主机**链接，
      这时才暴露出环境依赖：
        · 在 Git Bash 里跑 → link.exe 解析到 /usr/bin/link（GNU 的），报
          "link: extra operand ... Try 'link --help'"；
        · 换成另一个 shell 跑 → 干脆找不到 link.exe。
      根因是 VS 的 VC 工具目录不在 PATH 里、LIB/INCLUDE 也没设。
      本函数按 vswhere 的安装位置推导这些变量，让打包不再依赖"当前 shell 恰好是对的"。
    """
    env = os.environ.copy()
    vswhere = r"C://Program Files (x86)//Microsoft Visual Studio//Installer//vswhere.exe"
    install = None
    if os.path.exists(vswhere):
        try:
            r = subprocess.run(
                [vswhere, "-latest", "-products", "*",
                 "-requires", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64",
                 "-property", "installationPath"],
                capture_output=True, text=True)
            install = (r.stdout or "").strip() or None
        except Exception:
            install = None
    if not install:
        for cand in (r"D://BuildTools//VS2022", r"C://BuildTools//VS2022",
                     r"C://Program Files//Microsoft Visual Studio//2022//BuildTools"):
            if os.path.isdir(os.path.join(cand, "VC", "Tools", "MSVC")):
                install = cand
                break
    if not install:
        print("  ! 没找到 Visual Studio/BuildTools，沿用当前环境")
        return env

    msvc_root = os.path.join(install, "VC", "Tools", "MSVC")
    vers = sorted(os.listdir(msvc_root)) if os.path.isdir(msvc_root) else []
    if not vers:
        return env
    vc = os.path.join(msvc_root, vers[-1])

    sdk_root = r"C://Program Files (x86)//Windows Kits//10"
    sdk_lib = os.path.join(sdk_root, "Lib")
    sdk_vers = sorted(os.listdir(sdk_lib)) if os.path.isdir(sdk_lib) else []
    sdk = sdk_vers[-1] if sdk_vers else None

    path = [os.path.join(vc, "bin", "Hostx64", "x64")]
    lib = [os.path.join(vc, "lib", "x64")]
    inc = [os.path.join(vc, "include")]
    if sdk:
        path.append(os.path.join(sdk_root, "bin", sdk, "x64"))
        lib += [os.path.join(sdk_root, "Lib", sdk, "ucrt", "x64"),
                os.path.join(sdk_root, "Lib", sdk, "um", "x64")]
        inc += [os.path.join(sdk_root, "Include", sdk, "ucrt"),
                os.path.join(sdk_root, "Include", sdk, "um"),
                os.path.join(sdk_root, "Include", sdk, "shared"),
                os.path.join(sdk_root, "Include", sdk, "winrt")]

    env["PATH"] = os.pathsep.join(path + [env.get("PATH", "")])
    env["LIB"] = os.pathsep.join(lib)
    env["INCLUDE"] = os.pathsep.join(inc)
    return env


def build(what="主程序"):
    """cargo build --release，遇到文件锁自动清一次重试。"""
    # 实测：杀软扫描可能持续几次重试的时间，所以按 1s/3s/8s 递增等待，最多 4 次。
    # 只清指纹目录通常不够——被占的往往是 target/release 下的产物本身，只能等它扫完。
    delays = [1, 3, 8]
    for attempt in range(1, len(delays) + 2):
        print(f"  编译{what}（第 {attempt} 次）…")
        r = subprocess.run(["cargo", "build", "--release"], cwd=ROOT, env=msvc_env(),
                           capture_output=True, text=True, encoding="utf-8", errors="replace")
        if r.returncode == 0:
            return
        locked = ("os error 5" in r.stderr) or ("拒绝访问" in r.stderr)
        if locked and attempt <= len(delays):
            wait = delays[attempt - 1]
            print(f"  ⚠ 构建产物被占用（杀软扫描中），清理指纹后等 {wait}s 重试")
            clear_fingerprint()
            time.sleep(wait)
            continue
        print(r.stderr[-1500:])
        hint = "（反复被占用：建议把 target 目录加入杀软排除项）" if locked else ""
        raise SystemExit(f"编译{what}失败{hint}")


def make_payload():
    """把程序目录打成 payload.zip（setup 用 include_bytes! 内嵌）。"""
    stg = os.path.join(ROOT, "_payload_stage")
    shutil.rmtree(stg, ignore_errors=True)
    os.makedirs(stg)
    shutil.copy2(os.path.join(TARGET, "iprc1000_native.exe"),
                 os.path.join(stg, "IPRC1000-Native.exe"))
    for n in ["WinUHid.dll"] + ICONS:
        shutil.copy2(os.path.join(ROOT, n), os.path.join(stg, n))
    shutil.copy2(os.path.join(ROOT, "使用说明.txt"), os.path.join(stg, "使用说明.txt"))
    shutil.copy2(os.path.join(ROOT, "一键安装.cmd"), os.path.join(stg, "一键安装.cmd"))
    shutil.copytree(ASSETS, os.path.join(stg, "assets"))
    verify_stage(stg)

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
        shutil.copy2(os.path.join(ROOT, n), os.path.join(stg, n))
    shutil.copy2(os.path.join(ROOT, "使用说明.txt"), os.path.join(stg, "使用说明.txt"))
    shutil.copy2(os.path.join(ROOT, "一键安装.cmd"), os.path.join(stg, "一键安装.cmd"))
    shutil.copytree(ASSETS, os.path.join(stg, "assets"))
    verify_stage(stg)

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
        zp = os.path.join(DIST, f"IPRC1000_Native_v{v}.zip")
        if os.path.isfile(zp):
            print(f"  ✓ 校验现有发行包 {os.path.basename(zp)}")
            verify_dist(zp)
        else:
            print("  · 尚无发行 zip（只校验了 payload）")
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
    print("本脚本只产 Native 版 zip + Setup；Win32 变体与其它版本暂停发包。")


if __name__ == "__main__":
    main()
