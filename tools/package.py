#!/usr/bin/env python3
"""一键打包 + 校验（避免"改了代码忘了重打包"）。

做四件事：
  1. 结束占用（App / 语音桥 / ETW 会话）—— 否则 assets 里的 WinUHid.dll 被锁，删除会失败
  2. 把 `iprc1000_gui_dotnet/publish` 整个复制成发布目录，附上 tools/ 与截图、写版本.txt
  3. 压缩成 IPRC1000_Adapter_v1.0.zip
  4. **校验**：直接从 zip 里读出 ui/app.js、ui/index.html，检查关键功能关键字是否在包里
     —— 这一步是重点：以前出现过"代码更新了但 zip 是旧的"，靠人工记不住。

用法：python F:\\vibe\\tools\\package.py
"""
import datetime
import os
import shutil
import subprocess
import time
import zipfile

ROOT = r"F:\vibe"
PUB = os.path.join(ROOT, "iprc1000_gui_dotnet", "publish")
PKG = os.path.join(ROOT, "IPRC1000_Adapter_v1.0")
ZP = os.path.join(ROOT, "IPRC1000_Adapter_v1.0.zip")

# 包里必须存在的功能关键字（改完代码若这里 ✗，说明打包漏了新文件）
CHECKS = [
    ("屏幕键盘 KB_ROWS",    "ui/app.js",   "KB_ROWS"),
    ("键盘等分 flex",       "ui/style.css", "flex:1 1 0"),
    ("配置组 renderGroups", "ui/app.js",   "renderGroups"),
    ("搜索框",             "ui/app.js",   "gSearch"),
    ("导入导出",           "ui/app.js",   "exportGroup"),
    ("弹窗 comboMask",     "ui/index.html", "comboMask"),
    ("配置组 Tab gTabs",   "ui/index.html", "gTabs"),
    ("遥控器热区 keyhit",  "ui/style.css", ".keyhit"),
    ("缩略图 sprite",      "ui/style.css", ".keythumb"),
    # ★ 回归守卫：这两个曾经是 bug —— 弹窗关不掉（comboClose 未绑定）、按键名被挤没（keytext 缺 flex）
    ("弹窗可关闭 comboClose", "ui/app.js",  "comboClose"),
    ("按键名列 keytext",    "ui/style.css", ".keytext"),
    ("无重复 id comboCur", "ui/index.html", "comboSub"),
    ("一键部署脚本",       "tools/一键部署.ps1", "一键部署"),
    ("SOP 文档",          "文档/SOP-部署与使用.md", "SOP"),
    ("遥控器说明",         "文档/遥控器按键说明.md", "语音键"),
    # ★ 许可合规：包内内置 WinUHid(MIT)，MIT 要求许可声明随副本提供
    ("第三方声明",         "THIRD-PARTY-NOTICES.md", "WinUHid"),
    ("MIT 许可全文",       "THIRD-PARTY-NOTICES.md", "Cameron Gutman"),
    ("项目 LICENSE",      "LICENSE", "MIT License"),
]

# 源码级「反向校验」：(标签, 相对仓库根的路径, 禁止出现的子串)
# 这类检查针对**注入链路** —— 曾经出过的大 bug 都是"注入读了另一份数据源"。
# 放在源码上（而不是 zip 里）是因为 publish 只有 exe，没有 .cs。
SRC_FORBIDDEN = [
    # ★★ 最重要的一条：注入必须走 Keymaps.Get()，绝不能再用 Keys.Load()。
    #    Keys.Load() 读的是旧扁平格式，遇到多配置组的 keymap.json 会反序列化失败并
    #    **静默回落到出厂默认** → 界面显示一套、实际注入另一套，用户怎么改都没反应。
    ("注入未回退到 Keys.Load()", "iprc1000_gui_dotnet/KeyFilter.cs", "Keys.Load("),
    # 单文件发布下 `JsonNode.ToJsonString(new JsonSerializerOptions{...})` 会抛
    # InvalidOperationException（缺 TypeInfoResolver）→ 自检报告永远写不出来。
    # 必须用 Env.JsonPretty()（静态 options + JsonSerializer.Serialize）。
    ("未使用裸 ToJsonString(options)",
     "iprc1000_gui_dotnet/MainForm.cs", "ToJsonString(new JsonSerializerOptions"),
    # 默认值只能有一个来源：Defaults。Keys.Table 的 Btn 不许再带 Default 字段。
    ("Btn 未恢复 Default 字段", "iprc1000_gui_dotnet/Keys.cs", "string Hid, int[] Default"),
]

# 源码级「正向校验」：(标签, 相对仓库根的路径, 必须出现的子串)
SRC_REQUIRED = [
    ("注入走 Keymaps.Get()", "iprc1000_gui_dotnet/KeyFilter.cs", "Keymaps.Get(bid)"),
    ("映射自检入口 --map-dump", "iprc1000_gui_dotnet/Program.cs", "--map-dump"),
    ("缩进 JSON 走 Env.JsonPretty", "iprc1000_gui_dotnet/Env.cs", "JsonOptsPretty"),
    ("按组恢复出厂默认", "iprc1000_gui_dotnet/Keymaps.cs", "FactoryOf"),
    ("自检有硬看门狗", "iprc1000_gui_dotnet/MainForm.cs", "hard-timeout-35s"),
    ("自检有轨迹日志", "iprc1000_gui_dotnet/MainForm.cs", "trace.log"),
]


def kill_busy():
    out = subprocess.run(["tasklist", "/FO", "CSV", "/NH"],
                         capture_output=True, text=True, errors="replace").stdout
    for line in out.splitlines():
        if "IPRC1000" in line or "etw_consumer" in line:
            try:
                pid = line.split('","')[1]
                subprocess.run(["taskkill", "/PID", pid, "/F"], capture_output=True)
            except Exception:
                pass
    subprocess.run(["logman", "stop", "Iprc1000AudioBridge", "-ets"], capture_output=True)
    time.sleep(1.2)


def publish_fresh():
    """打包前强制重新编译发布 —— 否则可能把上一次的 publish 打进包。

    以前踩过两次坑：
      ① 改完代码直接跑 package.py，publish 还是旧的 → 用户拿到旧程序，"改了没效果"。
      ② **更隐蔽**：csproj 里 `ui\\**\\*` 是 CopyToOutputDirectory=PreserveNewest，
         输出目录已存在时 MSBuild 的增量判断会**整个跳过 ui 的复制** →
         publish\\ui 里只剩 icon.ico，界面文件全丢。
         所以这里先 `rmtree(PUB)` 再 publish，强制全量复制。
    """
    print("=== 重新编译发布 ===")
    # ★ 必须先清空：见上面 ②，否则 ui/ 会被增量逻辑跳过
    if os.path.exists(PUB):
        shutil.rmtree(PUB, ignore_errors=True)
    cmd = ["dotnet", "publish", "-c", "Release", "-r", "win-x64",
           "--self-contained", "false", "-p:PublishSingleFile=true",
           "-o", PUB, "--nologo", "-v", "quiet"]
    r = subprocess.run(cmd, cwd=os.path.join(ROOT, "iprc1000_gui_dotnet"),
                       capture_output=True, text=True, errors="replace")
    if r.returncode != 0:
        print("  ✗ 编译失败：")
        print("   " + (r.stdout or r.stderr)[-1500:].replace("\n", "\n   "))
        raise SystemExit(1)
    for f in os.listdir(PUB):
        if f.endswith(".xml"):
            try: os.remove(os.path.join(PUB, f))
            except OSError: pass
    exe = os.path.join(PUB, "IPRC1000 Adapter.exe")
    print(f"  ✓ {exe}  {os.path.getsize(exe):,} 字节  "
          f"{datetime.datetime.fromtimestamp(os.path.getmtime(exe)):%H:%M:%S}")
    # 立刻自检 ui 是否真的复制过来了（这是坑 ② 的哨兵）
    uip = os.path.join(PUB, "ui")
    need = ["app.js", "index.html", "style.css", "remote.png"]
    miss = [n for n in need if not os.path.exists(os.path.join(uip, n))]
    if miss:
        print(f"  ✗ publish\\ui 缺少 {miss} —— 增量复制又被跳过了")
        raise SystemExit(1)
    print(f"  ✓ publish\\ui 完整（{', '.join(need)}）")


def build():
    target = PKG
    if os.path.exists(target):
        try:
            shutil.rmtree(target)
        except PermissionError:
            target = os.path.join(ROOT, "_pkg_new")
            shutil.rmtree(target, ignore_errors=True)
    os.makedirs(target, exist_ok=True)
    for n in os.listdir(PUB):
        s, d = os.path.join(PUB, n), os.path.join(target, n)
        shutil.copytree(s, d) if os.path.isdir(s) else shutil.copy2(s, d)

    os.makedirs(os.path.join(target, "tools"), exist_ok=True)
    for f in ("check_stuck_key.py", "stuck_key_guard.py", "Run-Guard.cmd",
              "一键部署.ps1", "SetHidDriver/publish/SetHidDriver.exe"):
        src = os.path.join(ROOT, "tools", f)
        if os.path.exists(src):
            shutil.copy2(src, os.path.join(target, "tools", os.path.basename(f)))
    docsrc = os.path.join(ROOT, "tools", "docs")
    if os.path.isdir(docsrc):
        shutil.copytree(docsrc, os.path.join(target, "文档"), dirs_exist_ok=True)
    shot = os.path.join(ROOT, "_ui_kb.png")
    if os.path.exists(shot):
        shutil.copy2(shot, os.path.join(target, "界面截图-按键映射.png"))

    # ★ 第三方声明必须随包发出：包内内置了 WinUHid（MIT），
    #   MIT 要求"本许可声明须随所有副本一同提供"。漏了就是许可不合规。
    for n in ("LICENSE", "THIRD-PARTY-NOTICES.md"):
        p = os.path.join(ROOT, n)
        if os.path.exists(p):
            shutil.copy2(p, os.path.join(target, n))

    with open(os.path.join(target, "版本.txt"), "w", encoding="utf-8") as f:
        f.write("IPRC1000 Adapter\n")
        f.write("打包时间：" + datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S") + "\n")
        f.write("包含：\n"
                "  · 按键映射页：遥控器实体图（选中高亮）+ 每行按键截图 + 屏幕键盘选择器\n"
                "  · 多组配置 Tab（+/双击重命名/−）、搜索、导出、导入\n"
                "  · 方块键固定切换下一配置组\n"
                "  · ETW 自适应填缓冲（按键延迟实测 ~13 ms）\n"
                "  · 卡键三层防护（设备移除即时 / 800ms 看门狗 / 独立守卫）\n"
                "  · 媒体键走 SendInput、启动自动切非键盘驱动\n"
                "\n首次部署：右键「以 PowerShell 运行」tools\\一键部署.ps1\n"
                "文档：文档\\SOP-部署与使用.md · 文档\\遥控器按键说明.md\n"
                "许可：LICENSE（MIT）· THIRD-PARTY-NOTICES.md（内置第三方组件声明）\n")

    if os.path.exists(ZP):
        os.remove(ZP)
    with zipfile.ZipFile(ZP, "w", zipfile.ZIP_DEFLATED) as z:
        for root, _, files in os.walk(target):
            for f in files:
                p = os.path.join(root, f)
                z.write(p, os.path.relpath(p, target))
    return target


def verify():
    print("=== 校验包内内容 ===")
    ok_all = True
    with zipfile.ZipFile(ZP) as z:
        names = z.namelist()
        print(f"  {os.path.getsize(ZP)/1024/1024:.2f} MB · {len(names)} 个文件")
        for label, member, kw in CHECKS:
            try:
                txt = z.read(member).decode("utf-8", "replace")
                ok = kw in txt
            except KeyError:
                ok = False
            ok_all &= ok
            print(f"  {'✓' if ok else '✗'} {label}")
        exe = z.getinfo("IPRC1000 Adapter.exe")
        print(f"  主程序 {exe.file_size:,} 字节 · 时间 {exe.date_time}")

    # ── 源码级正/反向校验（针对注入链路，防回归）──
    print("  ── 源码检查 ──")
    for label, rel, bad in SRC_FORBIDDEN:
        p = os.path.join(ROOT, rel)
        try:
            txt = open(p, encoding="utf-8", errors="replace").read()
        except OSError:
            txt = ""
        # 注释里提到是允许的（本文件顶部就有一段说明），只看非注释行
        hit = any(bad in ln and not ln.strip().startswith(("//", "*", "/*"))
                  for ln in txt.splitlines())
        ok = not hit
        ok_all &= ok
        print(f"  {'✓' if ok else '✗'} {label}" + ("" if ok else f"  ← 仍含 {bad}"))

    for label, rel, need in SRC_REQUIRED:
        p = os.path.join(ROOT, rel)
        try:
            txt = open(p, encoding="utf-8", errors="replace").read()
        except OSError:
            txt = ""
        ok = need in txt
        ok_all &= ok
        print(f"  {'✓' if ok else '✗'} {label}")

    print(f"  ZIP 时间 {datetime.datetime.fromtimestamp(os.path.getmtime(ZP)):%Y-%m-%d %H:%M:%S}")
    print("  ⇒ " + ("全部通过，包是新的 ✓" if ok_all else "★ 有检查未通过 ✗"))
    return ok_all


if __name__ == "__main__":
    kill_busy()
    publish_fresh()          # ★ 打包前先重新编译，杜绝"包里是旧程序"
    tgt = build()
    verify()
    print(f"\n包目录：{tgt}\n压缩包：{ZP}")
