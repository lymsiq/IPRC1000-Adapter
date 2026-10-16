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
import hashlib
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

# ── 免运行时版（自包含 + 单文件压缩）────────────────────────────────────────────
#
# 为什么要有第二个变体：主包只有 2.5 MB，前提是**机器上已经装了 .NET 桌面运行时**
# （约 170 MB）。门店机器经常是「无网 + 店员没有管理员权限」，装运行时这一步会卡死。
#
# 实测对比（同一份代码，只改发布参数）：
#   · 框架依赖      2.5 MB   启动 674 ms   需要 .NET + 管理员
#   · 自包含+压缩  ~64 MB    启动 840 ms   零依赖、零管理员、零联网
#   · 自包含      140 MB     启动 690 ms   同上
# 所以做**两个包让用户按机器情况选**，而不是换掉主包：
# 主包继续小（大多数机器本来就有 .NET），免运行时版给卡在部署那一步的机器兜底。
FULL_PUB = os.path.join(ROOT, "_full_publish")
FULL_PKG = os.path.join(ROOT, "IPRC1000_Adapter_full")
FULL_ZP = os.path.join(ROOT, f"IPRC1000_Adapter_full_v{VERSION}.zip")
FULL_FIXED_ZIP = os.path.join(ROOT, "IPRC1000_Adapter_full.zip")

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
    # ★ v1.1.1 启动引导：缺 .NET 桌面运行时时自动装。
    #   顶层与 tools/ 两处都要有 —— 快捷方式指向它，用户也习惯"解压后一眼看到"。
    ("启动引导在顶层",      "启动.cmd", "准备运行时"),
    ("启动引导在 tools 里", "tools/启动.cmd", "准备运行时"),
    ("运行时准备脚本",      "tools/准备运行时.ps1", "[switch]$Quiet"),
    # 快捷方式必须仍指向「启动.cmd」——若退回直接指 exe，缺 .NET 的机器就只剩英文提示
    ("快捷方式走启动引导",  "创建桌面快捷方式.cmd", "启动.cmd"),
]

# 包内文本的**编码约定**（踩过的坑，必须机器校验，否则只会在用户机器上炸）：
#   .cmd → GBK：中文 Windows 控制台代码页是 cp936，UTF-8 的中文会被逐字节解错。
#   .ps1 → UTF-8 **带 BOM**：PS 5.1 把无 BOM 的 UTF-8 当 ANSI 读，中文直接语法报错。
# 判据用「按该编码读出后中文关键字还在」—— 单看"能不能解码"不够：
# GBK 很宽容，UTF-8 的字节串按 GBK 也能解出来，只是变成乱码。
ENCODING = [
    # (期望编码, 包内路径, 该编码下必须仍然正确的中文关键字)
    ("gbk", "启动.cmd",                   "准备运行时"),
    ("gbk", "创建桌面快捷方式.cmd",        "快捷方式"),
    ("gbk", "tools/启动.cmd",             "准备运行时"),
    ("gbk", "tools/创建桌面快捷方式.cmd",  "快捷方式"),
    ("bom", "tools/准备运行时.ps1",        "运行时"),
    ("bom", "tools/一键部署.ps1",          "部署"),
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
    # ★★ 注入链路：单槽 `_injectedNow` 是卡键的根因，绝不能复活。
    #    旧的单槽实现会让"按住语音键(→右Alt) + 再按确认 + 先松语音"把右 Alt 永久卡住，
    #    而且会把它在 _heldButtons 里的登记一起删掉 —— 看门狗也救不回来。
    ("单槽 _injectedNow 不得复活", "iprc1000_gui_dotnet/KeyFilter.cs", "_injectedNow"),
    # ★★ 鼠标模式的根本约束：移动必须**从光标此刻所在处**起算（相对位移）。
    #    第一版 SetEnabled(true) 里做主动自校准 → 绝对定位到"虚拟桌面中心"
    #    ⇒ 进鼠标模式那一刻光标猛地跳走（多显示器下那儿还是两块屏的接缝）。
    #    用户当场指出："要先计算鼠标的当前位置，再在当前位置进行移动，而不是固定位置"。
    ("鼠标移动不得走绝对定位", "iprc1000_gui_dotnet/MouseEngine.cs", "MoveAbsolute"),
    # 方块键那行右侧以前**写死**成"切换配置组"，与设置里的实际值打架（2026-10-16 用户指出）
    ("方块键那行不得再写死行为", "iprc1000_gui_dotnet/ui/app.js", "it.id === 'square' ? '切换配置组'"),
    # 鼠标模式不再有独立开关（模式 = 活动组），旧配置项不得复活
    ("鼠标模式不得再有独立开关", "iprc1000_gui_dotnet/MouseEngine.cs", "SquareKeyAction"),
    # ★ 解析器不得再写死音频分片长度（见上）—— 这两行是老 bug 的指纹
    ("音频解析不得写死首片长度", "tools/etw_consumer/src/main.rs", "dlen == 344"),
    ("音频解析不得写死续片长度", "tools/etw_consumer/src/main.rs", "dlen == 313"),
    ("鼠标模式不得把光标搬到固定点", "iprc1000_gui_dotnet/MouseEngine.cs", "CenterAndSettle"),
]

# 源码级「正向校验」：(标签, 相对仓库根的路径, 必须出现的子串)
SRC_REQUIRED = [
    # ★★ 注入链路的三道保险（2026-10-15 线上排查后加，缺任一道都可能再卡键）
    ("注入按整份报告下发", "iprc1000_gui_dotnet/KeyFilter.cs", "_injectedSet"),
    ("孤儿键回收器存在", "iprc1000_gui_dotnet/KeyFilter.cs", "ReapOrphanKeys"),
    ("孤儿键回收以系统状态为准", "iprc1000_gui_dotnet/KeyFilter.cs", "GetAsyncKeyState"),
    ("启动兜底清残留修饰键", "iprc1000_gui_dotnet/KeyFilter.cs", "ClearStaleModifiersOnStart"),
    ("多键同时按住回归自检", "iprc1000_gui_dotnet/KeyFilter.cs", "InjectMultiKeyTest"),
    ("多键自检有 CLI 入口", "iprc1000_gui_dotnet/Program.cs", "--inject-multikey-test"),
    ("虚拟 HID 创建失败会自愈重试", "iprc1000_gui_dotnet/WinUhidInjector.cs", "ScheduleBackgroundRetry"),
    ("单实例保护存在", "iprc1000_gui_dotnet/Program.cs", "MainForm.SingleInstanceMutex"),
    ("单实例有唤起窗口的监听", "iprc1000_gui_dotnet/MainForm.cs", "StartShowWindowListener"),
    # ★★ 重连后退回键盘的自愈（2026-10-16）。蓝牙重连会换掉设备实例、驱动绑定随之消失，
    #    退回键盘后 m/i 会漏进前台应用；以前只能重启程序。
    ("重连后退回键盘会自动切回", "iprc1000_gui_dotnet/Backend.cs", "RecheckDeviceDriver"),
    ("设备变更事件会触发驱动复查", "iprc1000_gui_dotnet/Backend.cs", "_deviceRecheckRequested"),
    ("驱动/按键来源变化有日志", "iprc1000_gui_dotnet/Backend.cs", "\"DEV  \""),
    ("手动还原键盘会关掉自动切换", "iprc1000_gui_dotnet/Backend.cs", "已同时关闭"),
    # ★ 鼠标注入（阶段 0）：传输层用 SendInput（不是虚拟 HID），且必须能用探针现场取数
    ("鼠标注入走 SendInput", "iprc1000_gui_dotnet/MouseInjector.cs", "MOUSEEVENTF_MOVE"),
    ("鼠标实测探针存在", "iprc1000_gui_dotnet/MouseInjector.cs", "public static string Probe"),
    ("鼠标探针有 CLI 入口", "iprc1000_gui_dotnet/Program.cs", "--mouse-probe"),
    # 探针必须先把进程设成 DPI 感知 —— 否则 GetCursorPos 返回虚拟化坐标，
    # 实测会把 1:1 的映射读成 5×（本机就这么误测过一轮，差点按错误的加速模型去设计）
    ("鼠标探针先设 DPI 感知", "iprc1000_gui_dotnet/MouseInjector.cs", "SetProcessDpiAwarenessContext"),
    # ★ 鼠标模式引擎（阶段 1）：方向键拖动=移动+自适应增速，确认/info=左右键
    ("鼠标引擎存在", "iprc1000_gui_dotnet/MouseEngine.cs", "internal sealed class MouseEngine"),
    ("鼠标引擎按时间算位移", "iprc1000_gui_dotnet/MouseEngine.cs", "_cfg.TickHz"),
    ("鼠标引擎有自适应增速", "iprc1000_gui_dotnet/MouseEngine.cs", "RampExponent"),
    ("鼠标引擎有方向按住超时", "iprc1000_gui_dotnet/MouseEngine.cs", "DirectionHoldTimeoutSec"),
    ("鼠标键孤儿回收存在", "iprc1000_gui_dotnet/MouseEngine.cs", "ReapOrphanButtons"),
    ("鼠标路由挂在 ApplyButton", "iprc1000_gui_dotnet/KeyFilter.cs", "MouseModeRouter"),
    ("鼠标模式有引擎自检", "iprc1000_gui_dotnet/MouseEngine.cs", "static string SelfTest"),
    ("引擎自检有 CLI 入口", "iprc1000_gui_dotnet/Program.cs", "--mouse-engine-test"),
    # ★ 鼠标模式页（阶段 2）
    ("鼠标模式页在包内", "iprc1000_gui_dotnet/ui/index.html", "page-mouse"),
    ("鼠标页有实时校准区", "iprc1000_gui_dotnet/ui/index.html", "mousePad"),
    ("鼠标页渲染函数存在", "iprc1000_gui_dotnet/ui/app.js", "function renderMouse"),
    ("预览分支也渲染鼠标页", "iprc1000_gui_dotnet/ui/app.js", "renderEnv(demo); renderMouse(demo)"),
    ("空 note 不显示", "iprc1000_gui_dotnet/ui/style.css", ".note:empty"),
    # ★ 方块键的作用：两个互斥选项（切配置组 / 切键鼠模式）
    # ★★ 鼠标模式变成一个配置组（v1.2.4）：模式与配置组合并成**一个状态来源**，
    #    "方块键的作用"那个二选一设置随之取消，方块键回归普通可绑定键。
    ("鼠标模式是一个配置组", "iprc1000_gui_dotnet/Keymaps.cs", "Kind = \"mouse\""),
    ("模式的唯一判据是活动组", "iprc1000_gui_dotnet/Keymaps.cs", "public static bool MouseGroupActive"),
    ("切组会同步鼠标模式", "iprc1000_gui_dotnet/Backend.cs", "SyncMouseWithGroup"),
    ("方块键是普通可绑定键", "iprc1000_gui_dotnet/Keymaps.cs", "KeyAct.SwitchGroup"),
    ("鼠标组默认清空方向键（交给引擎）", "iprc1000_gui_dotnet/Keymaps.cs", "交给引擎，不注入按键"),
    ("鼠标组不许删除", "iprc1000_gui_dotnet/Keymaps.cs", "是鼠标模式的入口，不能删除"),
    ("旧配置会迁移出鼠标组", "iprc1000_gui_dotnet/Keymaps.cs", "if (s.Ver < 10)"),
    ("配置组状态带 kind", "iprc1000_gui_dotnet/Backend.cs", "[\"kind\"] = g.Kind"),
    ("界面认得出鼠标组的方向键", "iprc1000_gui_dotnet/ui/app.js", "鼠标移动"),
    ("弹窗能选「切换配置组」", "iprc1000_gui_dotnet/ui/app.js", "['切换配置组',-5]"),
    # ★★ 假仪表修（2026-10-16 晚）：每个会话的第一帧曾被算成"跳号"，界面误报丢失上千帧
    ("按空档判新会话（不再用 in_session）", "tools/etw_consumer/src/main.rs", "距上一帧的空档"),
    ("回放也按时间轴判会话", "tools/etw_consumer/src/main.rs", "回放也要**按时间轴判会话**"),
    ("L2CAP 信令单独计数", "tools/etw_consumer/src/main.rs", "sig_frag"),
    ("自检覆盖跨会话假跳号", "tools/bridge_loss_selftest.py", "scenario_two_sessions"),
    # ★ 移动必须从光标当前位置起算（2026-10-16 用户指正后重做）
    ("被动标定用真实使用学增益", "iprc1000_gui_dotnet/MouseEngine.cs", "CalibrateFromUse"),
    ("开模式不再触发主动校准", "iprc1000_gui_dotnet/MouseEngine.cs", "这里**不做**主动校准"),
    ("移动注入有计数（判据不受人手干扰）", "iprc1000_gui_dotnet/MouseInjector.cs", "public static long MoveEvents"),
    ("自检断言「开模式不发移动注入」", "iprc1000_gui_dotnet/MouseEngine.cs", "开启鼠标模式不发出任何移动注入"),
    ("自检断言「松开后不再注入」", "iprc1000_gui_dotnet/MouseEngine.cs", "松开后引擎立即停止注入"),
    ("校准区光点=光标真实位置", "iprc1000_gui_dotnet/ui/app.js", "(d.x - d.vx)"),
    ("界面取数用 mouseSample", "iprc1000_gui_dotnet/MainForm.cs", "\"mouseSample\""),
    # 诊断盲区：这三个"静默降级"以前完全查不出来
    ("注入开关变化会留痕", "iprc1000_gui_dotnet/KeyFilter.cs", "InjectEnabled"),
    ("虚拟 HID 失败会留痕", "iprc1000_gui_dotnet/KeyFilter.cs", "虚拟 HID 下发失败"),
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
    # v1.0.6：声卡常驻（不再按静默自动关，避免"开→关→再开"抖动）
    ("声卡常驻不再自动关", "iprc1000_gui_dotnet/CableRouter.cs", "声卡**一直开着**"),
    # v1.0.6：桥的输出是 UTF-8，必须显式指定，否则中文乱码
    ("桥输出按 UTF-8 解码", "iprc1000_gui_dotnet/Backend.cs", "StandardOutputEncoding = Encoding.UTF8"),
    # v1.0.6：ETW 缓冲加大，防丢音频帧
    ("ETW 缓冲已加大", "tools/etw_consumer/src/main.rs", "max_buffer: 32"),
    ("桥日志用本地时间", "tools/etw_consumer/src/main.rs", "local_hms"),
    # ★★ 丢帧定位仪表（2026-10-16 加，起因「连续语音之后按键没声音」）。
    #    以前只有 `lost_cont` 一个 30 秒粒度的累计数 —— 既分不清"ETW 丢事件"还是
    #    "分片长度变了"，也看不出哪一秒开始丢。这四类计数互相独立，缺一个就退回去靠猜。
    ("帧头 seq 用来量真丢帧", "tools/etw_consumer/src/main.rs", "seq_lost"),
    ("seq 跳号有独立计数", "tools/etw_consumer/src/main.rs", "seq_jumps"),
    ("未识别分片留痕（不再静默吞掉）", "tools/etw_consumer/src/main.rs", "odd_kinds"),
    ("续片被截断有计数（老代码没有）", "tools/etw_consumer/src/main.rs", "trunc_cont"),
    ("帧头签名自检", "tools/etw_consumer/src/main.rs", "HDR_SIG"),
    ("丢失明细按 2 秒粒度落日志", "tools/etw_consumer/src/main.rs", "音频丢失明细"),
    ("仪表有合成回放自检", "tools/bridge_loss_selftest.py", "scenario_normal"),
    # 帧头**不是**常量：第 5/6 字节逐帧在变。早期笔记记成固定 `00 81 00` 是错的，
    # 照它做内容匹配会一帧都命不中（macOS 版就因此一直报「音频帧 0」）。
    ("帧头可变字节已标注", "tools/etw_consumer/src/main.rs", "帧头不是常量"),
    ("界面能看到丢帧分类", "iprc1000_gui_dotnet/ui/app.js", "丢帧分类"),
    # ★★ 2026-10-16：解析器不得再写死分片长度。真机实测同一帧被拆成 344+290+23 三片，
    #    写死"续片必须 313"的老代码把**一整分钟完好的音频全扔了**（同期 seq 跳号 = 0）。
    ("音频按 PB 标志组帧", "tools/etw_consumer/src/main.rs", "按 PB 标志 + L2CAP 声明长度组帧"),
    ("后续分片长度不写死", "tools/etw_consumer/src/main.rs", "长度随便多少都收"),
    ("续片长度分布有统计", "tools/etw_consumer/src/main.rs", "cont_kinds"),
    ("孤续片有计数", "tools/etw_consumer/src/main.rs", "orphan_cont"),
    ("怪长帧有计数", "tools/etw_consumer/src/main.rs", "short_pdu"),
    ("未识别分片留内容样本", "tools/etw_consumer/src/main.rs", "odd_sample"),
    ("新会话重置帧序号基准", "tools/etw_consumer/src/main.rs", "s.last_seq = None"),
    ("自检覆盖三片拆分", "tools/bridge_loss_selftest.py", "scenario_three_frag_split"),
    ("自检有按键回归保护", "tools/bridge_loss_selftest.py", "scenario_audio_with_keys"),
    # ★★ 回调隔离（2026-10-16）：把文件 I/O 移出 ETW 回调路径。
    #    教训来自 macOS 版：他们把收包回调从主队列（还跑着 UI + tick + 落盘）挪走后，
    #    到达延迟峰值 71→56 ms。我们这边的等价物就是"回调里做文件写"。
    ("回调只入队不写文件", "tools/etw_consumer/src/main.rs", "pub fn log_async"),
    ("异步队列由统计线程落盘", "tools/etw_consumer/src/main.rs", "pub fn drain"),
    ("回调路径不再直接写文件", "tools/etw_consumer/src/main.rs", "diag::log_async(\"会话开始"),
    ("tick 不再在锁内写文件", "tools/etw_consumer/src/main.rs", "diag::log_async(&format!(\n"),
    # 阻塞式 send_to 会堵住 ETW 回调线程 → 缓冲溢出丢一整段；改非阻塞，宁可丢一包
    ("UDP 发送非阻塞（不再堵住回调）", "tools/etw_consumer/src/main.rs", "set_nonblocking(true)"),
    ("发送缓冲满有计数", "tools/etw_consumer/src/main.rs", "udpBlocked"),
    # ★★ 有界抖动缓冲（2026-10-16）：顺手参数抄 macOS 版的实测定值
    ("有界抖动缓冲存在", "iprc1000_gui_dotnet/JitterBuffer.cs", "internal sealed class JitterBuffer"),
    ("起播攒 40 ms", "iprc1000_gui_dotnet/JitterBuffer.cs", "PreRollMs = 40"),
    ("积压上限 64 ms", "iprc1000_gui_dotnet/JitterBuffer.cs", "CapMs = 64"),
    ("静默 400 ms 判新会话", "iprc1000_gui_dotnet/JitterBuffer.cs", "StaleMs = 400"),
    ("声卡 IO 缓冲降到 30 ms", "iprc1000_gui_dotnet/JitterBuffer.cs", "LatencyMs = 30"),
    ("上限必须 ≥ 起播阈值（否则永久静音）", "iprc1000_gui_dotnet/JitterBuffer.cs", "if (CapMs < PreRollMs) CapMs = PreRollMs"),
    ("抖动缓冲有自检", "iprc1000_gui_dotnet/JitterBuffer.cs", "public static string SelfTest"),
    ("抖动自检有 CLI 入口", "iprc1000_gui_dotnet/Program.cs", "--jitter-test"),
    ("路由改用有界缓冲（不再用 1 秒 BufferedWaveProvider）", "iprc1000_gui_dotnet/CableRouter.cs", "new JitterBuffer()"),
    ("界面能看到抖动缓冲", "iprc1000_gui_dotnet/ui/app.js", "抖动缓冲"),
    # ★★ 鼠标动作映射（2026-10-16 用户要求）：把任意按键配成 左键/右键/滚轮上/滚轮下
    ("映射支持动作值", "iprc1000_gui_dotnet/KeyAct.cs", "public const int Left = -1"),
    ("动作值走 SendInput（不进虚拟键盘）", "iprc1000_gui_dotnet/KeyFilter.cs", "KeyAct.IsSingleAction"),
    ("鼠标动作有日志留痕", "iprc1000_gui_dotnet/KeyFilter.cs", "（鼠标动作）"),
    ("解析认识动作名", "iprc1000_gui_dotnet/Keys.cs", "KeyAct.FromName"),
    ("动作值有纯逻辑自检", "iprc1000_gui_dotnet/KeyAct.cs", "public static string SelfTest"),
    ("鼠标动作自检有 CLI 入口", "iprc1000_gui_dotnet/Program.cs", "--mouseact-test"),
    ("方块键那行显示实际行为", "iprc1000_gui_dotnet/ui/app.js", "function bindSym"),
    ("弹窗有鼠标动作行", "iprc1000_gui_dotnet/ui/index.html", "kbMouse"),
    # v1.0.7：卡键看门狗 v2。v1 盯「遥控器静默」，而 ETW 模式下遥控器按住时
    # 根本不上报（实测按住 30 秒上报 0 次）→ 丢一次 UP 就永久卡键。
    ("看门狗 v2 有独立登记集合", "iprc1000_gui_dotnet/KeyFilter.cs", "_heldButtons"),
    ("看门狗 v2 会被定时器调用", "iprc1000_gui_dotnet/KeyFilter.cs", "WatchdogV2()"),
    ("判据抽成可单测的纯函数", "iprc1000_gui_dotnet/StuckGuard.cs", "public static string? Decide"),
    ("看门狗用音频做判据", "iprc1000_gui_dotnet/KeyFilter.cs", "StuckGuard.Decide(kv.Key, heldSec"),
    ("后端挂上了音频证据", "iprc1000_gui_dotnet/Backend.cs", "AudioSilenceMs"),
    ("强制释放有动作日志", "iprc1000_gui_dotnet/KeyFilter.cs", "stuck-guard.log"),
    # 紧急复位必须连 v2 的集合一起清，否则看门狗会拿着已失效的记录重复释放
    ("紧急复位会清 v2 集合", "iprc1000_gui_dotnet/KeyFilter.cs", "_heldButtons.Clear()"),
    # silentMs = -1 表示桥起来了但一帧都没收到，必须折算成大值，否则永不判卡键
    ("从未收帧按超大静默处理", "iprc1000_gui_dotnet/Backend.cs", "long.MaxValue / 4"),
    # v1.0.7 资源 review：
    # ① 看门狗曾经被 `if (empty) return;` 挡住（ETW 模式下 v1 集合恒为空）→ v2 一次都没跑过
    ("v1 判据不成立不许提前 return", "iprc1000_gui_dotnet/KeyFilter.cs", "if (!empty &&"),
    # ② 窗口藏进托盘后停掉前端 2 秒轮询（跨进程 getState + 三次 DOM 重绘没人看）
    ("托盘时停前端轮询", "iprc1000_gui_dotnet/ui/app.js", "window.setUiVisible"),
    ("可见性变化会通知前端", "iprc1000_gui_dotnet/MainForm.cs", "OnVisibleChanged"),
    # ③ WebView2 默认关掉没用的 Chromium 子系统（省 crashpad 等）
    ("WebView2 有省资源参数", "iprc1000_gui_dotnet/MainForm.cs", "IPRC1000_WEBVIEW_ARGS"),
    # ★★ v1.1.1 白屏事故回归守卫（v1.1.0 首发整窗白屏，界面完全打不开）。
    #    根因：把「控件是否存在」(_web) 当成了"已初始化"用，而构造函数就会建控件
    #    → 首次启动在守卫处直接 return → InitWebView 永不执行 → 无 CoreWebView2、无导航。
    #    铁证：白屏机器上 webview.log **根本不存在**（日志是由 InitWebView 那条路径写的）。
    #    下面四条缺任何一条，白屏就可能复发：
    ("就绪标志与控件标志分离", "iprc1000_gui_dotnet/MainForm.cs", "private bool _webReady;"),
    ("初始化守卫用就绪标志", "iprc1000_gui_dotnet/MainForm.cs",
     "if (_webReady || _webInitBusy || IsDisposed) return;"),
    ("初始化成功才置就绪", "iprc1000_gui_dotnet/MainForm.cs", "_webReady = true;"),
    ("释放时清掉就绪标志", "iprc1000_gui_dotnet/MainForm.cs", "_webReady = false;"),
    # 出"白屏/界面没起来"时必须有日志可看（v1.1.0 期间正是因为无日志只能靠猜）
    ("界面生命周期有日志", "iprc1000_gui_dotnet/MainForm.cs", "webview.log"),
    ("初始化成败都记日志", "iprc1000_gui_dotnet/MainForm.cs", "界面初始化失败"),
    # ★ v1.1.2 启动时间轴：让"启动慢"变成可查的事实，而不是体感。
    #   基准必须是**进程真实创建时刻** —— 用进程内 Stopwatch 会因静态字段惰性初始化
    #   而把开头几十毫秒整段抹掉（实测首行显示 T+0 ms，那时进程已跑了 135 ms）。
    ("有启动时间轴埋点", "iprc1000_gui_dotnet/MainForm.cs", "StartupMark"),
    ("时间轴基准是进程创建时刻", "iprc1000_gui_dotnet/Program.cs",
     "Process.GetCurrentProcess().StartTime"),
    # 「环境就绪」≠「页面渲染完成」：Navigate() 只是发起导航。
    # 以前只有 selftest 订阅 NavigationCompleted，生产环境看不见页面何时出来。
    ("页面渲染完成常开打点", "iprc1000_gui_dotnet/MainForm.cs", "页面渲染完成"),
    # 免运行时版要靠 exe 体积认出自己，别让它在缺 .NET 的机器上白装一个运行时
    ("运行时脚本认得出免运行时版", "tools/准备运行时.ps1", "-gt 40"),
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


# 刚编译出来的语音桥 sha256 —— `build_bridge()` 填，`verify()` 用来断言包内就是它
BRIDGE_SHA = None
BRIDGE_SRC = os.path.join(ROOT, "tools", "etw_consumer", "target", "release", "etw_consumer.exe")
BRIDGE_DST = os.path.join(ROOT, "iprc1000_gui_dotnet", "assets", "etw_consumer.exe")
BRIDGE_RS = os.path.join(ROOT, "tools", "etw_consumer", "src", "main.rs")


def build_bridge():
    """**重新编译 Rust 语音桥**并刷新到 `iprc1000_gui_dotnet/assets/`。

    ★★ 为什么必须做（2026-10-16 实测踩到）：

      包里的 `assets/etw_consumer.exe` 是一把**签入仓库的二进制**，而 `dotnet publish`
      只负责把它**复制**过去 —— 于是**改了桥的 Rust 源码、包里的桥还是旧的**。
      这次给桥加了丢帧定位仪表与回调隔离，打完包一比对才发现包里那把还是两天前的，
      发出去就是"改了没效果"，而且**从包外完全看不出来**。

    同类坑以前也吃过（publish 增量跳过 ui/），所以这里同样是"先构建、再断言"。
    """
    global BRIDGE_SHA
    print("=== 重新编译语音桥（Rust）===")
    cargo = shutil.which("cargo")
    if cargo is None:
        # 没有 Rust 工具链时不直接放行 —— 只有"assets 里的桥比源码新"才允许继续，
        # 否则就是在**静默发一个旧桥**（这正是本函数要防的事）。
        if os.path.exists(BRIDGE_DST) and os.path.getmtime(BRIDGE_DST) > os.path.getmtime(BRIDGE_RS):
            print("  ⚠ 没找到 cargo，但 assets 里的桥比源码新，按现状继续（未经重新编译）")
        else:
            print("  ✗ 没找到 cargo，而 assets\\etw_consumer.exe 比 main.rs 旧 —— "
                  "继续下去就会发一个旧桥。请先装 Rust 工具链，或手工刷新 assets 里的桥。")
            raise SystemExit(1)
    else:
        r = subprocess.run([cargo, "build", "--release"],
                           cwd=os.path.join(ROOT, "tools", "etw_consumer"),
                           capture_output=True, text=True, errors="replace")
        if r.returncode != 0:
            print("  ✗ 桥编译失败：")
            print("   " + (r.stderr or r.stdout)[-1500:].replace("\n", "\n   "))
            raise SystemExit(1)
        # 编译警告也算不合格（项目基线：Rust 0 警告）
        warn = [l for l in (r.stderr or "").splitlines() if l.strip().startswith("warning")]
        for l in warn[:10]:
            print("  ⚠ " + l.strip())
        if warn:
            print(f"  ✗ 桥编译有 {len(warn)} 条警告（基线是 0 警告）")
            raise SystemExit(1)

    if not os.path.exists(BRIDGE_SRC):
        print(f"  ✗ 找不到编译产物 {BRIDGE_SRC}")
        raise SystemExit(1)
    shutil.copy2(BRIDGE_SRC, BRIDGE_DST)
    BRIDGE_SHA = hashlib.sha256(open(BRIDGE_DST, "rb").read()).hexdigest()
    st = os.stat(BRIDGE_DST)
    print(f"  ✓ {BRIDGE_DST}  {st.st_size:,} 字节  "
          f"{datetime.datetime.fromtimestamp(st.st_mtime):%H:%M:%S}  sha256 {BRIDGE_SHA[:16]}…")


def publish_fresh(out=None, self_contained=False, compressed=False):
    """打包前强制重新编译发布 —— 否则可能把上一次的 publish 打进包。

    以前踩过两次坑：
      ① 改完代码直接跑 package.py，publish 还是旧的 → 用户拿到旧程序，"改了没效果"。
      ② **更隐蔽**：csproj 里 `ui\\**\\*` 是 CopyToOutputDirectory=PreserveNewest，
         输出目录已存在时 MSBuild 的增量判断会**整个跳过 ui 的复制** →
         publish\\ui 里只剩 icon.ico，界面文件全丢。
         所以这里先 `rmtree(out)` 再 publish，强制全量复制。

    参数：
      self_contained=True → 自带 .NET 运行时（免运行时版用）
      compressed=True     → 压缩单文件（体积 ~64MB；**代价是每次启动多约 165 ms**，
                            因为是"每次启动解压"而不是只解一次）
    """
    out = out or PUB
    print("=== 重新编译发布" + ("（免运行时版）" if self_contained else "") + " ===")
    # ★ 必须先清空：见上面 ②，否则 ui/ 会被增量逻辑跳过
    if os.path.exists(out):
        shutil.rmtree(out, ignore_errors=True)
    cmd = ["dotnet", "publish", "-c", "Release", "-r", "win-x64",
           "--self-contained", "true" if self_contained else "false",
           "-p:PublishSingleFile=true"]
    if compressed:
        cmd.append("-p:EnableCompressionInSingleFile=true")
    cmd += ["-o", out, "--nologo", "-v", "quiet"]
    r = subprocess.run(cmd, cwd=os.path.join(ROOT, "iprc1000_gui_dotnet"),
                       capture_output=True, text=True, errors="replace")
    if r.returncode != 0:
        print("  ✗ 编译失败：")
        print("   " + (r.stdout or r.stderr)[-1500:].replace("\n", "\n   "))
        raise SystemExit(1)
    for f in os.listdir(out):
        if f.endswith(".xml"):
            try: os.remove(os.path.join(out, f))
            except OSError: pass
    exe = os.path.join(out, "IPRC1000 Adapter.exe")
    print(f"  ✓ {exe}  {os.path.getsize(exe):,} 字节  "
          f"{datetime.datetime.fromtimestamp(os.path.getmtime(exe)):%H:%M:%S}")
    # 立刻自检 ui 是否真的复制过来了（这是坑 ② 的哨兵）
    uip = os.path.join(out, "ui")
    need = ["app.js", "index.html", "style.css", "remote.png"]
    miss = [n for n in need if not os.path.exists(os.path.join(uip, n))]
    if miss:
        print(f"  ✗ publish\\ui 缺少 {miss} —— 增量复制又被跳过了")
        raise SystemExit(1)
    print(f"  ✓ publish\\ui 完整（{', '.join(need)}）")
    size_mb = os.path.getsize(exe) / 1024 / 1024
    if self_contained and size_mb < 40:
        print(f"  ✗ 自包含版 exe 只有 {size_mb:.1f} MB —— 看起来没带上运行时")
        raise SystemExit(1)
    if (not self_contained) and size_mb > 20:
        print(f"  ✗ 框架依赖版 exe 竟有 {size_mb:.1f} MB —— 发布参数不对")
        raise SystemExit(1)


def build(pub=None, target=None, variant="main"):
    """把 publish 输出 + 仓库附属文件装成一个发布目录，并出两份压缩包。

    variant="main" → 主包（框架依赖，约 2.5 MB，**需要机器已装 .NET**）
    variant="full" → 免运行时版（自包含 + 单文件压缩，约 64 MB，**零依赖**）

    两个变体共用这一套逻辑：附属文件、文档、许可证、编码校验完全一致，
    差别只有「exe 来自哪次 publish」和「说明文件多一段话」——
    这样不会出现"主包有文档、免运行时版漏了许可证"这种漂移。
    """
    pub = pub or PUB
    target = target or PKG
    if os.path.exists(target):
        try:
            shutil.rmtree(target)
        except PermissionError:
            target = os.path.join(ROOT, "_pkg_new")
            shutil.rmtree(target, ignore_errors=True)
    os.makedirs(target, exist_ok=True)
    for n in os.listdir(pub):
        s, d = os.path.join(pub, n), os.path.join(target, n)
        shutil.copytree(s, d) if os.path.isdir(s) else shutil.copy2(s, d)

    os.makedirs(os.path.join(target, "tools"), exist_ok=True)
    for f in ("check_stuck_key.py", "stuck_key_guard.py", "Run-Guard.cmd",
              "一键部署.ps1", "SetHidDriver/publish/SetHidDriver.exe",
              "创建桌面快捷方式.cmd", "准备运行时.ps1", "启动.cmd"):
        src = os.path.join(ROOT, "tools", f)
        if os.path.exists(src):
            shutil.copy2(src, os.path.join(target, "tools", os.path.basename(f)))
    # ★ 启动引导也放到包**顶层**：桌面快捷方式直接指向它，而用户最自然的动作是
    #   "解压后一眼看到它"，埋在 tools/ 里容易被忽略。两处都放，成本只有几 KB。
    #   （「启动.cmd」自己会先试同目录、再试 ..\，所以两种位置都能跑）
    for n in ("创建桌面快捷方式.cmd", "启动.cmd"):
        p = os.path.join(ROOT, "tools", n)
        if os.path.exists(p):
            shutil.copy2(p, os.path.join(target, n))
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
        if variant == "full":
            f.write("【★ 这一份是「免运行时版」】\n")
            f.write("  主程序自带 .NET 运行时（所以包大，约 64 MB），但换来：\n")
            f.write("    · 机器上不用装 .NET 桌面运行时\n")
            f.write("    · 不需要管理员权限\n")
            f.write("    · 不需要联网\n")
            f.write("  适合：门店机器没网 / 装不上运行时 / 店员没有管理员权限。\n")
            f.write("  （另一份 IPRC1000_Adapter.zip 只有约 2.5 MB，前提是机器已经装好 .NET。）\n\n")
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
        f.write("【以后都从「启动.cmd」进】\n")
        f.write("  也可以直接双击程序目录里的「启动.cmd」（桌面快捷方式指的就是它）。\n")
        f.write("  它会先确认 .NET 桌面运行时到位：缺了就自动装（优先用包内离线安装器，\n")
        f.write("  否则联网下载官方安装包），装好再拉起主程序 —— 不会再弹英文的\n")
        f.write("  「需要下载 .NET」对话框让你自己找下载地址。\n")
        f.write("  ★ 主程序支持 .NET 8/9/10/11 任一桌面运行时，不是只认 8。\n\n")
        f.write("【以后每次升级】两步，快捷方式不用动：\n")
        f.write("  1) 下载 IPRC1000_Adapter.zip（同一个链接，永远是最新版）\n")
        f.write("  2) 解压到 D:\\ ，选「全部覆盖」\n")
        f.write("     完事 —— 点桌面那个快捷方式就是新版。\n\n")
        f.write("【永久下载链接】永远指向最新版，不用再找 release 页面：\n")
        f.write("  https://github.com/lymsiq/IPRC1000-Adapter/releases/latest/download/IPRC1000_Adapter.zip\n\n")
        f.write("【小技巧】把这个链接存成浏览器书签，或加进下载工具的任务列表，\n")
        f.write("        升级就是「下载 → 覆盖解压」两步。\n")

    with open(os.path.join(target, "版本.txt"), "w", encoding="utf-8") as f:
        f.write("IPRC1000 Adapter" + ("（免运行时版）" if variant == "full" else "") + "\n")
        f.write("打包时间：" + datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S") + "\n")
        if variant == "full":
            f.write("运行要求：无（主程序自带 .NET 运行时，不需要管理员、不需要联网）\n")
        else:
            f.write("运行要求：.NET 桌面运行时 8/9/10/11 任一（缺了「启动.cmd」会自动装）\n")
        f.write("包含：\n"
                "  · 按键映射页：遥控器实体图（选中高亮）+ 每行按键截图 + 屏幕键盘选择器\n"
                "  · 多组配置 Tab（+/双击重命名/−）、搜索、导出、导入\n"
                "  · 方块键固定切换下一配置组\n"
                "  · ETW 自适应填缓冲（按键延迟实测 ~13 ms）\n"
                "  · 卡键三层防护（设备移除即时 / 800ms 看门狗 / 独立守卫）\n"
                "  · 媒体键走 SendInput、启动自动切非键盘驱动\n"
                "\n启动：双击「启动.cmd」（自动确认 / 安装 .NET 桌面运行时后拉起主程序）\n"
                "首次部署：右键「以 PowerShell 运行」tools\\一键部署.ps1\n"
                "文档：文档\\SOP-部署与使用.md · 文档\\遥控器按键说明.md\n"
                "许可：LICENSE（MIT）· THIRD-PARTY-NOTICES.md（内置第三方组件声明）\n")

    zp, fixed = (FULL_ZP, FULL_FIXED_ZIP) if variant == "full" else (ZP, FIXED_ZIP)
    if os.path.exists(zp):
        os.remove(zp)
    with zipfile.ZipFile(zp, "w", zipfile.ZIP_DEFLATED) as z:
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
    # 配合 GitHub 的 /releases/latest/download/ 还能得到**永久下载链接**。
    # ⚠ v1.0.7 / v1.1.0 都只传了带版本号那个 → 永久链接实测 404，别再漏。
    if os.path.exists(fixed):
        os.remove(fixed)
    shutil.copy2(zp, fixed)
    return target


def verify(zp=None, title="主包", self_contained=False, source_checks=True):
    """校验一个发布包的内容。两个变体共用 —— 断言必须两边都过。"""
    zp = zp or ZP
    print(f"=== 校验包内内容（{title}）===")
    ok_all = True
    with zipfile.ZipFile(zp) as z:
        names = z.namelist()
        print(f"  {os.path.getsize(zp)/1024/1024:.2f} MB · {len(names)} 个文件")
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
        # ★★ 包里的语音桥必须**就是刚编译出来的那把** ——
        #    防"改了桥的 Rust 源码、包里的桥还是旧的"（2026-10-16 实测踩到：
        #    桥上加了丢帧定位仪表，打完包才发现包里还是两天前的二进制）。
        #    比 sha256 是唯一靠得住的判据：mtime 会被复制保留、大小可能一样。
        if BRIDGE_SHA:
            member = next((n for n in names if n.replace("\\", "/").endswith("assets/etw_consumer.exe")), None)
            if member is None:
                ok_all = False
                print("  ✗ 包里没有 assets/etw_consumer.exe（语音桥缺失）")
            else:
                got = hashlib.sha256(z.read(member)).hexdigest()
                ok = got == BRIDGE_SHA
                ok_all &= ok
                print(f"  {'✓' if ok else '✗'} 语音桥与源码同批"
                      f"（sha256 {got[:16]}…）"
                      + ("" if ok else f"  ← 期望 {BRIDGE_SHA[:16]}…（包里是旧的！）"))
        # ★ 变体自证：主包必须很小（说明是框架依赖），免运行时版必须很大（说明真带上了运行时）。
        #   这条防的是"打错发布参数"——比如免运行时版其实还是框架依赖，
        #   那用户在门店机器上就会遇到"装了却打不开"，而包里什么都看不出来。
        mb = exe.file_size / 1024 / 1024
        ok = mb > 40 if self_contained else mb < 20
        ok_all &= ok
        print(f"  {'✓' if ok else '✗'} 体积符合{('免运行时版' if self_contained else '框架依赖版')}"
              f"预期（{mb:.1f} MB）")

        # ── 包内不应出现的路径（防止重复打包把体积撑大）──
        for label, bad in ZIP_FORBIDDEN:
            hit = [n for n in names if bad in n]
            ok = not hit
            ok_all &= ok
            print(f"  {'✓' if ok else '✗'} {label}" + ("" if ok else f"  ← {hit[:3]}"))

        # ── 文本编码约定（.cmd=GBK / .ps1=带 BOM 的 UTF-8）──
        for want, member, kw in ENCODING:
            if member not in names:
                ok_all = False
                print(f"  ✗ 缺 {member}（编码无法校验）")
                continue
            raw = z.read(member)
            if want == "gbk":
                # UTF-8 的字节串按 GBK 几乎总能"解得出来"，只是变成乱码 ——
                # 所以判据不是能解码，而是**中文关键字还原正确**。
                try:
                    txt = raw.decode("gbk")
                except UnicodeDecodeError:
                    txt = ""
                ok = kw in txt and not raw.startswith(b"\xef\xbb\xbf")
                why = "" if ok else f"  ← 按 GBK 读不出「{kw}」（是不是存成了 UTF-8？）"
            else:  # bom
                ok = raw.startswith(b"\xef\xbb\xbf")
                if ok:
                    try:
                        txt = raw.decode("utf-8-sig")
                    except UnicodeDecodeError:
                        txt, ok = "", False
                    ok = ok and kw in txt
                why = "" if ok else f"  ← 应为「带 BOM 的 UTF-8」且含「{kw}」"
            ok_all &= ok
            print(f"  {'✓' if ok else '✗'} {member} 编码为 {want.upper()}{why}")

    # ── 源码级正/反向校验（针对注入链路，防回归）──
    #    只在主包那一次跑：它检查的是仓库源码，跟打哪个变体无关。
    if not source_checks:
        print(f"  ⇒ {title}：" + ("全部通过，包是新的 ✓" if ok_all else "★ 有检查未通过 ✗"))
        return ok_all
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

    print(f"  ZIP 时间 {datetime.datetime.fromtimestamp(os.path.getmtime(zp)):%Y-%m-%d %H:%M:%S}")
    print(f"  ⇒ {title}：" + ("全部通过，包是新的 ✓" if ok_all else "★ 有检查未通过 ✗"))
    return ok_all


if __name__ == "__main__":
    kill_busy()
    # ── ⓪ 先重编译语音桥（Rust）并刷新到 assets/ ──
    #    ★ 必须排在 dotnet publish 之前：publish 只是把 assets 里的二进制**复制**进包，
    #      不重编译就等于发旧桥（2026-10-16 实测踩到）。
    build_bridge()
    print()
    # ── ① 主包（框架依赖，约 2.5 MB）──
    publish_fresh()
    tgt = build()
    verify()

    # ── ② 免运行时版（自包含 + 压缩，约 64 MB）──
    #    需求来源：门店机器常年「无网 + 店员没有管理员权限」，
    #    而 .NET 桌面运行时安装包约 55 MB、装完占约 170 MB —— 这一步经常卡死。
    #    自包含版把这些全省掉，代价是包变大 + 每次启动多约 165 ms 的解压。
    print()
    publish_fresh(out=FULL_PUB, self_contained=True, compressed=True)
    full_tgt = build(pub=FULL_PUB, target=FULL_PKG, variant="full")
    verify(zp=FULL_ZP, title="免运行时版", self_contained=True, source_checks=False)

    print(f"\n主包目录：{tgt}\n主包压缩包：{ZP}")
    print(f"免运行时版目录：{full_tgt}\n免运行时版压缩包：{FULL_ZP}")

    # ★★ 发布清单 —— GitHub Release 必须传**固定名**附件。
    #    包内「升级说明.txt」向用户承诺了一个永久下载链接，它指向**固定名**那个包；
    #    只传带版本号的包 → 那个链接就是 404。
    #    v1.0.7 / v1.1.0 都这么漏过（固定名附件从 v1.0.5 起再没上传过），
    #    直到 v1.1.1 实测链接 404 才发现 —— 文档写了、流程没做，用户照样拿不到。
    print("\n★ 上传 GitHub Release 时**固定名附件是必须的**，只传带版本号的那个永久链接就是 404：")
    print(f"   {os.path.basename(FIXED_ZIP):38s} ← 固定名·主包（永久链接靠它）")
    print(f"   {os.path.basename(ZP):38s} ← 带版本号存档")
    print(f"   {os.path.basename(FULL_FIXED_ZIP):38s} ← 固定名·免运行时版")
    print(f"   {os.path.basename(FULL_ZP):38s} ← 带版本号存档")
    print("   永久链接（发布后务必自测一次，记得带 Cache-Control: no-cache —— CDN 会缓存）：")
    print("   https://github.com/lymsiq/IPRC1000-Adapter/releases/latest/download/IPRC1000_Adapter.zip")
    print("   https://github.com/lymsiq/IPRC1000-Adapter/releases/latest/download/IPRC1000_Adapter_full.zip")
    for p in (ZP, FIXED_ZIP, FULL_ZP, FULL_FIXED_ZIP):
        h = hashlib.sha256(open(p, "rb").read()).hexdigest()
        mb = os.path.getsize(p) / 1024 / 1024
        print(f"   sha256 {os.path.basename(p):40s} {mb:7.1f} MB  {h}")
