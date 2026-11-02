#!/usr/bin/env python3
"""打「Win32 版」独立包（不链接 OpenGL）。

## 为什么要单独出这个包

完整版用 egui + OpenGL 渲染界面。在没有显卡驱动的机器上（「Microsoft 基本显示适配器」、
远程桌面、老集显）GL 上下文建不出来，虽然程序会降级到 Win32 界面，但**二进制里仍然带着
整套 eframe/GL 代码**，体积 5.6 MB、启动要初始化渲染栈。

这个包用 `--no-default-features --features win32_only` 构建：
- **完全不链接 eframe / egui / OpenGL**（不是运行时跳过，是根本没有）
- 体积 1.2 MB（完整版 5.6 MB）
- 启动直接进 Win32 界面，不尝试创建 GL 上下文

## 包内容（缺一个就会出问题，别删）

| 文件 | 为什么必须有 |
|---|---|
| `IPRC1000.exe` | 主程序 |
| `WinUHid.dll` | 按键注入；缺了按键完全不生效（日志会报「未找到 WinUHid.dll」） |
| `icon.ico` | 窗口/托盘图标从同目录取，缺了会退回 exe 内嵌资源 |
| `assets/VBCABLE_Driver_Pack45.zip` | 语音要虚拟声卡，客户机没装时用这个装 |
| `使用说明.txt` | 客户向说明 |

用法：
    python tools/package_win32.py
"""

import hashlib
import io
import os
import shutil
import subprocess
import sys
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT_DIR = os.path.dirname(ROOT)  # F:\vibe

sys.path.insert(0, os.path.join(ROOT, "tools"))
from package import msvc_env  # 复用 MSVC 环境补全，避免 shell 环境不对就编不动


def version() -> str:
    with open(os.path.join(ROOT, "Cargo.toml"), encoding="utf-8") as f:
        for line in f:
            if line.startswith("version ="):
                return line.split('"')[1]
    raise SystemExit("读不到版本号")


def build() -> str:
    print("  编译 Win32 版（--no-default-features --features win32_only）…")
    r = subprocess.run(
        [
            "cargo", "build", "--release",
            "--no-default-features", "--features", "win32_only",
            "--bin", "iprc1000_native",
        ],
        cwd=ROOT, env=msvc_env(), capture_output=True, text=True,
        encoding="utf-8", errors="replace",
    )
    if r.returncode != 0:
        print(r.stderr[-3000:])
        raise SystemExit("编译失败")
    exe = os.path.join(ROOT, "target", "release", "iprc1000_native.exe")
    size = os.path.getsize(exe)
    print(f"  ✓ 编译完成  {size / 1048576:.2f} MB")

    # 防回归：Win32 版不该再引用 OpenGL/D3D 相关 DLL
    with open(exe, "rb") as f:
        blob = f.read().lower()
    for bad in (b"opengl32", b"glow", b"eframe"):
        if bad in blob:
            raise SystemExit(f"✗ 二进制里仍出现 `{bad.decode()}` —— feature 门控失效了")
    print("  ✓ 已确认不含 OpenGL / eframe 引用")
    return exe


def readme(ver: str) -> str:
    return f"""IPRC1000 · Win32 版  v{ver}
============================

这个版本用 Windows 自带的 GDI 绘制界面，**不需要显卡驱动**，也不需要 OpenGL。
适用于：显卡驱动未安装、远程桌面、较老的集成显卡等无法正常显示图形界面的机器。

与完整版的区别
--------------
完整版界面（egui）        本版界面（Win32）
· 宏（编辑、录制、播放）   ✗ 不提供（已配置的宏仍会正常执行）
· 文本槽 / 鼠标动作        ✗ 不能编辑（已配置的仍生效）
· 多配置组                ✗ 不支持（只用默认组）
· 按键映射                ✓ 全部 23 键 + 自定义键
· 语音（豆包 / 微信输入法）✓ 可切换
· 环境检查与一键修复       ✓
· 托盘、程序图标           ✓

按键注入、语音、音频这些核心能力与完整版完全一致 —— 界面只是换了一种画法。

安装
----
1. 把整个文件夹解压到任意位置（建议 D:\\IPRC1000-Win32）。
2. 双击 IPRC1000.exe。
3. 若按键无效：管理员身份运行一次，程序会把遥控器从「键盘」改成「HID 设备」
   （不改会打出字母）。界面顶部「一键修复」也会做这件事。
4. 若语音没声音：确认已安装 VB-CABLE 虚拟声卡（assets 目录里有安装包）。

本目录文件说明
--------------
IPRC1000.exe          主程序
WinUHid.dll           虚拟键盘驱动接口，**删了按键就不生效**
icon.ico              窗口/托盘图标
assets/               虚拟声卡安装包
native.json           首次运行的配置种子

常见问题
--------
Q: 打开后没有任何窗口？
A: 看托盘区（右下角）有没有 IPRC1000 图标，点它唤回；仍不行请看日志
   %APPDATA%\\IPRC1000 Adapter\\native.log

Q: 按键没反应？
A: 先看日志里有没有 INJ 行。没有则是虚拟键盘没建成（槽位被占用），重启电脑后立即再试。

Q: 我以后想要宏/文本功能？
A: 换用完整版（同一个 exe 名字 IPRC1000-Native.exe），两版共用同一份配置。
"""


def zip_it(ver: str, exe: str) -> str:
    stage = os.path.join(ROOT, "target", "win32_pkg", f"IPRC1000_Win32_v{ver}")
    if os.path.isdir(stage):
        shutil.rmtree(stage)
    os.makedirs(os.path.join(stage, "assets"))

    shutil.copy2(exe, os.path.join(stage, "IPRC1000.exe"))
    for name in ("WinUHid.dll", "icon.ico"):
        src = os.path.join(ROOT, name)
        if not os.path.exists(src):
            raise SystemExit(f"缺必需文件：{name}")
        shutil.copy2(src, os.path.join(stage, name))
    # 配置文件种子：没有它首次启动会用默认值（也能跑，但客户的麦克风/键位要重配）
    for name in ("native.json",):
        src = os.path.join(ROOT, name)
        if os.path.exists(src):
            shutil.copy2(src, os.path.join(stage, name))
    vbc = os.path.join(ROOT, "assets", "VBCABLE_Driver_Pack45.zip")
    if os.path.exists(vbc):
        shutil.copy2(vbc, os.path.join(stage, "assets", "VBCABLE_Driver_Pack45.zip"))

    with open(os.path.join(stage, "使用说明.txt"), "w", encoding="utf-8-sig") as f:
        f.write(readme(ver))

    out = os.path.join(OUT_DIR, f"IPRC1000_Win32_v{ver}.zip")
    if os.path.exists(out):
        os.remove(out)
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
        for base, _dirs, files in os.walk(stage):
            for fn in files:
                full = os.path.join(base, fn)
                rel = os.path.relpath(full, stage)
                z.write(full, os.path.join(f"IPRC1000_Win32_v{ver}", rel))
    return out


def main():
    ver = version()
    print(f"== 打 Win32 版  v{ver} ==")
    exe = build()
    out = zip_it(ver, exe)
    size = os.path.getsize(out)
    h = hashlib.sha256(open(out, "rb").read()).hexdigest()
    print(f"\n  ✓ {os.path.basename(out)}  {size / 1048576:.2f} MB")
    print(f"  sha256 {h}")
    print("\n  ⚠ 目标缓存被 Win32 版覆盖了：交付前请再跑一次 `python tools/package.py`")
    print("    重建完整版，否则下一次打完整包会误用这份 Win32 二进制。")


if __name__ == "__main__":
    main()
