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
import re
import shutil
import subprocess
import sys
import time
import zipfile

# ★ Windows 上 Python 的 stdout 编码默认跟随控制台代码页：
#   中文机器是 cp936（能打中文）、而英文/西欧 runner 是 cp1252（打中文直接
#   UnicodeEncodeError 崩掉）。本脚本到处 print 中文，必须强制 UTF-8。
#   （CI 上就是栽在这里：本地复现不出来，因为本机是 cp936。）
try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

# 仓库根目录。默认是本地路径；CI 里用环境变量覆盖：
#   IPRC1000_ROOT=${{ github.workspace }} python tools/package.py
ROOT = os.environ.get("IPRC1000_ROOT") or r"F:\vibe"
PUB = os.path.join(ROOT, "iprc1000_gui_dotnet", "publish")


def read_version():
    """从 csproj 的 <Version> 读版本号 —— 发布包名、Release tag 都从这里取。

    单一来源，避免"csproj 写了 1.0.1 但包名还叫 v1.0"这种漂移。
    改版本只需改 csproj 一处。
    """
    p = os.path.join(ROOT, "iprc1000_gui_dotnet", "IPRC1000Adapter.csproj")
    try:
        m = re.search(r"<Version>\s*([^<\s]+)\s*</Version>", open(p, encoding="utf-8").read())
        return m.group(1) if m else "0.0.0"
    except OSError:
        return "0.0.0"


VERSION = read_version()
# ── 输出目录固定不带版本号 ──
#
# 用户的实际用法是：「一个固定文件夹 + 一次性建好的快捷方式，每次发版只更新里面的内容」。
# 所以**打包目录固定叫 IPRC1000_Adapter**，压缩包再出两份：
#   · IPRC1000_Adapter_v{版本}.zip  —— 带版本号，存档/回滚用
#   · IPRC1000_Adapter.zip          —— 固定名，解压出来就是 IPRC1000_Adapter\ 文件夹
# 后者配合 GitHub 的 /releases/latest/download/ 还能得到永久下载链接，
# 于是"升级"就等于「下载固定名包 → 解压覆盖同一个文件夹」，快捷方式永远不用重建。
PKG = os.path.join(ROOT, "IPRC1000_Adapter")
ZP = os.path.join(ROOT, f"IPRC1000_Adapter_v{VERSION}.zip")
FIXED_ZIP = os.path.join(ROOT, "IPRC1000_Adapter.zip")

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
    # 固定快捷方式相关（见「升级说明.txt」）
    ("快捷方式生成器",      "创建桌面快捷方式.cmd", "IPRC1000 遥控器"),
    ("快捷方式在 tools 里", "tools/创建桌面快捷方式.cmd", "管理员身份运行"),
    ("升级说明",           "升级说明.txt", "永久下载链接"),
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
    # 收到 CLEAR 就 ClearBuffer() 会丢掉已缓冲的真实音频 → 每次会话切换都断一下。
    # （会话是"松手 END → 再按 CLEAR"反复建立的，环里往往还压着上一次的真实音频）
    ("CLEAR 不再清缓冲", "iprc1000_gui_dotnet/CableRouter.cs", "ClearBuffer()"),
]

# 源码级「正向校验」：(标签, 相对仓库根的路径, 必须出现的子串)
SRC_REQUIRED = [
    ("注入走 Keymaps.Get()", "iprc1000_gui_dotnet/KeyFilter.cs", "Keymaps.Get(bid)"),
    ("映射自检入口 --map-dump", "iprc1000_gui_dotnet/Program.cs", "--map-dump"),
    ("缩进 JSON 走 Env.JsonPretty", "iprc1000_gui_dotnet/Env.cs", "JsonOptsPretty"),
    ("按组恢复出厂默认", "iprc1000_gui_dotnet/Keymaps.cs", "FactoryOf"),
    ("自检有硬看门狗", "iprc1000_gui_dotnet/MainForm.cs", "hard-timeout-35s"),
    ("自检有轨迹日志", "iprc1000_gui_dotnet/MainForm.cs", "trace.log"),
    ("界面显示版本号", "iprc1000_gui_dotnet/ui/app.js", "railVer"),
    # 「音频为什么断」的定位能力（2026-10-14 加，见 CHANGELOG 1.0.3）
    ("桥统计已重定向捕获", "iprc1000_gui_dotnet/Backend.cs", "RedirectStandardOutput = true"),
    ("应用读真实桥统计", "iprc1000_gui_dotnet/Backend.cs", "bridge-stats.json"),
    ("僵死桥会被重启", "iprc1000_gui_dotnet/Backend.cs", "BridgeZombie"),
    ("收到音频即算会话中", "iprc1000_gui_dotnet/CableRouter.cs", "_inSession = true;"),
    ("声卡侧有日志", "iprc1000_gui_dotnet/CableRouter.cs", "router.log"),
    ("桥侧写运行统计", "tools/etw_consumer/src/main.rs", "bridge-stats.json"),
    ("桥有单实例保护", "tools/etw_consumer/src/main.rs", "acquire_single_instance"),
    # 默认录音设备必须是"用 CoreAudio API 查真正的默认"，不是"查设备在不在"
    ("默认设备用官方 API 查", "iprc1000_gui_dotnet/Env.cs", "GetDefaultAudioEndpoint"),
    ("默认设备漂移会记日志", "iprc1000_gui_dotnet/Backend.cs", "[MIC]"),
    ("有录音设备自检入口", "iprc1000_gui_dotnet/Env.cs", "ProbeCaptureDevice"),
]

# 包内「不应出现」的路径：(标签, 子串)。用来防体积膨胀与误打包。
ZIP_FORBIDDEN = [
    # 截图已在包顶层以中文名放了一份，文档目录里不该再有第二份
    ("截图未重复打包", "文档/screenshots/"),
    # 这些永远不该进发布包
    ("无工作记录目录", ".workbuddy/"),
    ("无构建中间产物", "obj/"),
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
              "一键部署.ps1", "SetHidDriver/publish/SetHidDriver.exe",
              "创建桌面快捷方式.cmd"):
        src = os.path.join(ROOT, "tools", f)
        if os.path.exists(src):
            shutil.copy2(src, os.path.join(target, "tools", os.path.basename(f)))
    # ★ 快捷方式生成器也放到包**顶层**：用户最自然的动作是"解压后一眼看到它"，
    #   埋在 tools/ 里容易被忽略。两处都放，成本只有一个几 KB 的文件。
    sc = os.path.join(ROOT, "tools", "创建桌面快捷方式.cmd")
    if os.path.exists(sc):
        shutil.copy2(sc, os.path.join(target, "创建桌面快捷方式.cmd"))
    docsrc = os.path.join(ROOT, "tools", "docs")
    if os.path.isdir(docsrc):
        # ★ 排除 screenshots/：它下面就是那 4 张界面截图，已经以中文名放到包顶层了
        #   （见下面 shots 那段）。不排除的话同一批图会存两份，白白多出近 2 MB。
        shutil.copytree(docsrc, os.path.join(target, "文档"), dirs_exist_ok=True,
                        ignore=shutil.ignore_patterns("screenshots"))
    # 界面截图：用 docs/screenshots 里由浏览器预览模式渲染的那几张
    # （原来读根目录的 _ui_kb.png —— 那是会过期的旧截图，曾把 mac 风格修饰键
    #   Control/Option/Command 发进包里，而程序早已改成 Ctrl/Alt/Shift/Win）
    shots = os.path.join(ROOT, "tools", "docs", "screenshots")
    for src, dst in (("keys.png", "界面截图-按键映射.png"),
                     ("modal.png", "界面截图-屏幕键盘.png"),
                     ("overview.png", "界面截图-功能总览.png"),
                     ("env.png", "界面截图-环境设置.png")):
        p = os.path.join(shots, src)
        if os.path.exists(p):
            shutil.copy2(p, os.path.join(target, dst))

    # ★ 第三方声明必须随包发出：包内内置了 WinUHid（MIT），
    #   MIT 要求"本许可声明须随所有副本一同提供"。漏了就是许可不合规。
    for n in ("LICENSE", "THIRD-PARTY-NOTICES.md"):
        p = os.path.join(ROOT, n)
        if os.path.exists(p):
            shutil.copy2(p, os.path.join(target, n))

    # ★ 固定快捷方式：告诉用户"解压到同一个目录"，这样桌面快捷方式永远不用重建
    with open(os.path.join(target, "升级说明.txt"), "w", encoding="utf-8") as f:
        f.write("IPRC1000 遥控器适配器 · 升级说明\n")
        f.write("=" * 46 + "\n\n")
        f.write("【一句话】程序就放在一个固定文件夹里，以后升级只覆盖里面的文件，\n")
        f.write("          位置不变 → 桌面快捷方式建一次就永远有效。\n\n")
        f.write("【这个文件夹叫什么】\n")
        f.write("  固定叫 IPRC1000_Adapter（不带版本号）。\n")
        f.write("  发布时除了带版本号的存档包，还会出一个**同名压缩包**：\n")
        f.write("      IPRC1000_Adapter.zip\n")
        f.write("  它的文件名不带版本号，所以解压出来的文件夹永远叫 IPRC1000_Adapter，\n")
        f.write("  不会每升一级就换目录。\n\n")
        f.write("【第一次】\n")
        f.write("  1) 下载 IPRC1000_Adapter.zip，解压到一个固定位置，例如 D:\\\n")
        f.write("     → 得到 D:\\IPRC1000_Adapter\\\n")
        f.write("  2) 双击里面的「创建桌面快捷方式.cmd」\n")
        f.write("     → 桌面出现「IPRC1000 遥控器」，已设为「以管理员身份运行」\n")
        f.write("  3) 首次部署环境：右键「以 PowerShell 运行」tools\\一键部署.ps1\n\n")
        f.write("【以后每次升级】两步，快捷方式不用动：\n")
        f.write("  1) 下载 IPRC1000_Adapter.zip（同一个链接，永远是最新版）\n")
        f.write("  2) 解压到 D:\\ ，选「全部覆盖」\n")
        f.write("     完事 —— 点桌面那个快捷方式就是新版。\n\n")
        f.write("【永久下载链接】永远指向最新版，不用再找 release 页面：\n")
        f.write("  https://github.com/lymsiq/IPRC1000-Adapter/releases/latest/download/IPRC1000_Adapter.zip\n\n")
        f.write("【小技巧】把这个链接存成浏览器书签，或加进下载工具的任务列表，\n")
        f.write("        升级就是「下载 → 覆盖解压」两步。\n")

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

    # ★ 再出一份**固定名**的包：IPRC1000_Adapter.zip
    #
    # 为什么需要：Windows 的解压向导会**按压缩包文件名建目录**，
    # 于是 `IPRC1000_Adapter_v1.0.4.zip` 解压出 `IPRC1000_Adapter_v1.0.4\`、
    # 下个版本又是新目录 —— 桌面快捷方式每升一次级就失效一次。
    # 固定名的包解压出来的目录名永远一样，覆盖解压即可，快捷方式一劳永逸。
    # 配合 GitHub 的 /releases/latest/download/ 还能得到**永久下载链接**：
    #   https://github.com/lymsiq/IPRC1000-Adapter/releases/latest/download/IPRC1000_Adapter.zip
    if os.path.exists(FIXED_ZIP):
        os.remove(FIXED_ZIP)
    shutil.copy2(ZP, FIXED_ZIP)
    return target


def verify():
    print("=== 校验包内内容 ===")
    ok_all = True
    with zipfile.ZipFile(ZP) as z:
        names = z.namelist()
        print(f"  {os.path.getsize(ZP)/1024/1024:.2f} MB · {len(names)} 个文件")
        for label, member, kw in CHECKS:
            try:
                raw = z.read(member)
                # ★ 包内文本有两种编码：多数是 UTF-8，而 .cmd 用 GBK
                #   （中文 Windows 的 cmd 控制台默认 cp936，UTF-8 的 .cmd 会乱码）。
                #   校验要两种都试，否则 GBK 文件会被按 UTF-8 解成乱码而误判为"缺内容"。
                txt = raw.decode("utf-8")
            except UnicodeDecodeError:
                try:
                    txt = raw.decode("gbk")
                except Exception:
                    txt = raw.decode("utf-8", "replace")
            except KeyError:
                txt = None
            ok = txt is not None and kw in txt
            ok_all &= ok
            print(f"  {'✓' if ok else '✗'} {label}")
        exe = z.getinfo("IPRC1000 Adapter.exe")
        print(f"  主程序 {exe.file_size:,} 字节 · 时间 {exe.date_time}")

        # ── 包内不应出现的路径（防止重复打包把体积撑大）──
        for label, bad in ZIP_FORBIDDEN:
            hit = [n for n in names if bad in n]
            ok = not hit
            ok_all &= ok
            print(f"  {'✓' if ok else '✗'} {label}" + ("" if ok else f"  ← {hit[:3]}"))

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
