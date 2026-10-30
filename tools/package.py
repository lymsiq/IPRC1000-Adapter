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
# ★ 2026-10-20：原来写死 `F://vibe` —— 换机器/换盘符就废。
# 现在默认 = **本脚本所在仓库根**（tools/package.py 的上一级），仍可用 IPRC1000_ROOT 覆盖。
ROOT = (os.environ.get("IPRC1000_ROOT")
        or os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
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
    ("换机部署入口",        "部署.cmd", "一键部署.ps1"),
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
    ("gbk", "部署.cmd",                   "一键部署.ps1"),
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
    # ★ v1.2.24 隐私护栏：**不许记录其它设备的标识**（用户明确要求）。
    #   曾经为了诊断"Raw Input 里有没有遥控器"，我把设备 VID/PID 存进一个字典并显示出来；
    #   那会把用户的物理键盘型号暴露在界面上。功能已完成使命，永久移除。
    ("不记录其它设备标识（隐私）", "iprc1000_gui_dotnet/KeyFilter.cs", "_rawByDevice"),
    # ★ v1.2.12：用户实测"贴嘴边小声说话输出正常"，增益本身没必要；
    #   而且 +1 dB 时界面那句"现在没有声音…"的提示会造成误解。整个功能已删。
    ("麦克风增益已移除（路由）", "iprc1000_gui_dotnet/CableRouter.cs", "MicGain"),
    ("麦克风增益已移除（命令）", "iprc1000_gui_dotnet/Backend.cs", "SetAudioGain"),
    ("麦克风增益已移除（界面）", "iprc1000_gui_dotnet/ui/index.html", "micGain"),
    # ★★ v1.2.8：数据 CID 是 L2CAP 动态分配的（0x40/0x41 都可能），不许再写死
    ("不许写死数据 CID", "tools/etw_consumer/src/main.rs", "CID_HID"),
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
    # 用 in_session 判新会话会跳过全部序号比较（回放里恒为 false）—— 自检 ② 抓到过
    ("不许用 in_session 判新会话", "tools/etw_consumer/src/main.rs", "let session_start = !self.in_session"),
    # ★ 解析器不得再写死音频分片长度（见上）—— 这两行是老 bug 的指纹
    ("音频解析不得写死首片长度", "tools/etw_consumer/src/main.rs", "dlen == 344"),
    ("音频解析不得写死续片长度", "tools/etw_consumer/src/main.rs", "dlen == 313"),
    ("鼠标模式不得把光标搬到固定点", "iprc1000_gui_dotnet/MouseEngine.cs", "CenterAndSettle"),
]

# 源码级「正向校验」：(标签, 相对仓库根的路径, 必须出现的子串)
SRC_REQUIRED = [
    # ★ v1.2.46：纯幽灵报文不参与状态对账（否则长按被截断在 ~200ms）
    ("纯幽灵报文不参与对账", "tools/etw_consumer/src/main.rs", "const GHOST: [u8; 2] = [0x01, 0x0C]"),
    ("语音键随音频结束释放", "iprc1000_gui_dotnet/Backend.cs", "AudioDrivenVoiceRelease"),
    ("幽灵过滤在取基线之后", "tools/etw_consumer/src/main.rs", "必须排在\"取基线\"之后"),
    ("幽灵心跳回归用例", "tools/bridge_loss_selftest.py", "scenario_ghost_before_first_key"),
    ("桥输出原始报文", "tools/etw_consumer/src/main.rs", '"[RAW] len={} usages=[{}]"'),
    ("只取一次基线（不按空档重取）", "tools/etw_consumer/src/main.rs", "取消\"空档 ≥600ms 就重取基线\""),
    ("音频释放有 2s 宽限", "iprc1000_gui_dotnet/Backend.cs", "heldMs < 2000"),
    ("音频释放只在长时间静默后兜底", "iprc1000_gui_dotnet/Backend.cs", "silent < 5000"),
    ("映射页说明鼠标左键用法", "iprc1000_gui_dotnet/ui/app.js", "在<b>光标当前位置</b>单击一次"),
    ("异常留痕与日志治理", "iprc1000_gui_dotnet/Safe.cs", "只保留当天的日志"),
    ("遥控器按名字动态识别", "iprc1000_gui_dotnet/Env.cs", "PairedRemoteMacs"),
    ("启动预创建虚拟 HID", "iprc1000_gui_dotnet/Backend.cs", "虚拟 HID 设备已预创建"),
    ("配置原子写", "iprc1000_gui_dotnet/Settings.cs", "File.Replace(tmp, File_, File_ + \".bak\""),
    ("截图点击：模板匹配实现", "iprc1000_gui_dotnet/TemplateClick.cs", "归一化互相关"),
    ("截图点击：动作哨兵 -6", "iprc1000_gui_dotnet/KeyAct.cs", "ClickIcon = -6"),
    ("截图点击：采集入口", "iprc1000_gui_dotnet/ui/app.js", "learnClickIcon"),
    ("WinUHid 状态与探针同判据", "iprc1000_gui_dotnet/WinUhidInjector.cs", "inUse = Ready"),
    ("幽灵报文回归用例", "tools/bridge_loss_selftest.py", "scenario_ghost_between_hold"),
    # ★ v1.2.44：重复 DOWN 要按时间区分"抖动"与"丢 UP"（否则第二次按下永久失效）
    ("重复DOWN按时间判据", "iprc1000_gui_dotnet/KeyFilter.cs", "上一次的 UP 丢了"),
    ("ETW 下重复DOWN无条件自愈", "iprc1000_gui_dotnet/KeyFilter.cs", 'src != "etw" && dupAgeMs < 300'),
    # ★ v1.2.43：空档之后必须补发被松开的键的 UP（否则长按必卡）
    ("空档后补发 UP", "tools/etw_consumer/src/main.rs", "空档期间**被松开的键必须补发 UP**"),
    ("长按松开回归用例", "tools/bridge_loss_selftest.py", "scenario_long_hold_release"),
    # ★ v1.2.42：语音键的音频判据不能误杀正常长按（3s→30s）
    ("语音键看门狗放宽到 600s", "iprc1000_gui_dotnet/StuckGuard.cs", "VoiceSilentMs = 600_000"),
    # ★ v1.2.41：日志要记录"这一按持续多久"（长按类功能的关键判据）
    ("日志记录按住时长", "iprc1000_gui_dotnet/KeyFilter.cs", '按住 {holdMs:F0} ms'),
    # ★ v1.2.40：桥重启要清按住集合；面板"按住"要反映真实状态
    ("桥重启清按住集合", "iprc1000_gui_dotnet/Backend.cs", 'ClearHeld("语音桥重启，之前的 UP 不会再来")'),
    # ★ v1.2.139：宏/文本动作库（2026-10-30）
    #   这四条是"被删掉就出大事"的实现 —— 宏一旦绕过现有注入路径，就是永久卡键。
    ("宏复用现有注入路径", "iprc1000_gui_dotnet/MacroPlayer.cs", "KeyFilter.InjectTransient"),
    ("宏的键进孤儿回收", "iprc1000_gui_dotnet/KeyFilter.cs", "Macros.AllInjectableVks()"),
    ("宏播放中放行中断键", "iprc1000_gui_dotnet/KeyFilter.cs", "MacroPlayer.IsTrigger(bid)"),
    ("宏有总预算兜底", "iprc1000_gui_dotnet/MacroPlayer.cs", "超过总时长上限"),
    ("粘贴等够再还原剪贴板", "iprc1000_gui_dotnet/MacroPlayer.cs", "SleepCancellable(PasteDelayMs)"),
    ("宏引用区间不与鼠标动作重叠", "iprc1000_gui_dotnet/KeyAct.cs", "Macros.IsRef(vk)"),
    ("macros.json 原子写", "iprc1000_gui_dotnet/Macros.cs", "File.Replace(tmp, File_, File_ + \".bak\""),
    ("宏界面三页签", "iprc1000_gui_dotnet/ui/index.html", 'id="comboTabs"'),
    # ★ v1.2.140：循环 + 可观测性（对着 KeymouseGo 的 issues 收的）
    #   这四条防的是"静默失控"：循环停不下来、无限循环没有刹车、失败查不出第几步。
    ("任意键都能打断宏", "iprc1000_gui_dotnet/KeyFilter.cs", "MacroPlayer.StopByKey(bid)"),
    ("无限循环有硬时长上限", "iprc1000_gui_dotnet/MacroPlayer.cs", "MaxTotalMs { get; set; } = 600_000"),
    ("坏数据不能变成无限循环", "iprc1000_gui_dotnet/Macros.cs", "it.Loop < 0 ? 1 :"),
    ("试运行强制只跑一遍", "iprc1000_gui_dotnet/MacroPlayer.cs", "Loop = 1, Steps = it.Steps"),
    ("每步留痕（第几步失败）", "iprc1000_gui_dotnet/MacroPlayer.cs", "MACRO {no}/{it.Steps.Count}"),
    ("宏起止弹托盘气泡", "iprc1000_gui_dotnet/MainForm.cs", "MacroPlayer.Notify +="),
    # ★ v1.2.141：录制 + 鼠标步骤 + 可配项（2026-10-30）
    #   每条都对应一个"删掉就会出事"的机制，不是装饰性断言。
    ("录制滤掉自己的注入", "iprc1000_gui_dotnet/Recorder.cs", "(info.flags & LLMHF_INJECTED) == 0"),
    ("录制不记自由移动", "iprc1000_gui_dotnet/Recorder.cs", "if (_recMouseDown) Add("),
    ("拖拽一步表达（起点+终点）", "iprc1000_gui_dotnet/Recorder.cs", "X2 = e.X, Y2 = e.Y"),
    ("拖拽抬起在 finally", "iprc1000_gui_dotnet/MacroPlayer.cs", "ClickButton(st.Button, false);"),
    ("绝对定位要带 VIRTUALDESK", "iprc1000_gui_dotnet/MouseInjector.cs", "MOUSEEVENTF_VIRTUALDESK"),
    ("坐标点击前校验窗口", "iprc1000_gui_dotnet/MacroPlayer.cs", "WrongWindow(st)"),
    ("粘贴键可配（终端场景）", "iprc1000_gui_dotnet/MacroPlayer.cs", "DoPaste(st.Slot, st.PasteKey"),
    ("失败策略 skip 要留痕", "iprc1000_gui_dotnet/MacroPlayer.cs", "已设置为跳过继续"),
    # ★ v1.2.148：点击默认走"光标位置"（用户加"+ 点击"的本意）
    #   实测踩过：用户加了「点击（图标）」但没采集图标 → 宏第 1 步就停，
    #   而日志默认关着 ⇒ 体感"按了完全没反应"。
    ("点击默认走光标位", "iprc1000_gui_dotnet/ui/app.js", "target: t === 'click' ? 'cursor' : 'icon'"),
    ("光标位点击的分支存在", "iprc1000_gui_dotnet/MacroPlayer.cs", 'if (target == "cursor")'),
    ("宏失败要用警告气泡", "iprc1000_gui_dotnet/MainForm.cs", "ToolTipIcon.Warning"),
    # ★ v1.2.144：全局按键（语音键跨配置组统一）
    #   这四条防的是"语音键随配置组漂移"——实测过：首页切成微信输入法，
    #   切到鼠标模式又变回右 Alt，用户报的就是这个。
    ("全局键读取优先", "iprc1000_gui_dotnet/Keymaps.cs", "if (_s.Global.TryGetValue(id, out var g)) return g;"),
    ("写入按是否全局分流", "iprc1000_gui_dotnet/Keymaps.cs", "if (_s.Global.ContainsKey(id)) _s.Global[id] = vks;"),
    ("dump/回收要合并全局", "iprc1000_gui_dotnet/Keymaps.cs", "foreach (var kv in _s.Global) merged[kv.Key] = kv.Value;"),
    ("语音键默认设为全局", "iprc1000_gui_dotnet/Keymaps.cs", 's.Global["voice"] = (int[])vv.Clone();'),
    ("按住状态取真实集合", "iprc1000_gui_dotnet/Backend.cs", "PatchEtwPressed"),
    # ★ v1.2.39：探针提示要说清"要重启"（原来只显示"驱动未安装"）
    ("驱动探针说明降级与重启", "iprc1000_gui_dotnet/Backend.cs", "当前走 SendInput 降级"),
    # ★ v1.2.38：把"驱动未生效需重启"和"槽位被占"分开提示
    ("驱动未生效要提示重启", "iprc1000_gui_dotnet/WinUhidInjector.cs", "ControlDeviceOpenable"),
    # ★ v1.2.37：ClearHeld 必须按"真正放掉的按钮"计数并留痕（原来用 Raw Input 集合，恒为 0）
    ("清空按住集合要留痕", "iprc1000_gui_dotnet/KeyFilter.cs", 'GUARD  清空按住集合'),
    # ★ v1.2.36：卡键防线（看门狗定时器必须真的起；切组/改映射前先放掉按住的键）
    ("卡键看门狗定时器必须启动", "iprc1000_gui_dotnet/KeyFilter.cs", "new System.Threading.Timer(_ => Watchdog()"),
    ("切组前释放按住的键", "iprc1000_gui_dotnet/Backend.cs", 'ClearHeld("切换配置组，先放掉按住的键")'),
    ("改映射前释放按住的键", "iprc1000_gui_dotnet/Backend.cs", 'ClearHeld($"修改「{bid}」映射，先放掉按住的键")'),
    # ★ v1.2.35：注入测试按钮/说明跟着当前映射走（不再写死"右 Alt"）
    ("测试按钮动态文案", "iprc1000_gui_dotnet/ui/app.js", "测试注入 ${voiceBind}"),
    # ★ v1.2.30：清理遗留桥（旧桥会挡掉新桥，导致"改了没生效"）
    ("清理遗留语音桥", "iprc1000_gui_dotnet/Backend.cs", "ReapStaleBridge"),
    # ★ v1.2.29：启动诊断字段（第一个带键报文的时间/键值/是否被静默期吃掉）
    ("首键诊断进 stats", "tools/etw_consumer/src/main.rs", "first_key_seen"),
    # ★ v1.2.28：启动静默期 + 基线键不发 UP（修"启动弹右键"与"松开比按下多"）
    ('会话基线保留（防"早已按着"被当成新按下）', "tools/etw_consumer/src/main.rs", "if !self.seeded {"),
    ("基线键不发 UP", "tools/etw_consumer/src/main.rs", "self.seeded_held[u] = false"),
    # ★ v1.2.26：调试工具只在开发版；发包版（DevTools=false）里根本不编译
    ("调试入口有编译开关", "iprc1000_gui_dotnet/Program.cs", "#if IPRC_DEV"),
    ("打包时关闭调试入口", "tools/package.py", "-p:DevTools=false"),
    ("按键来源只有 ETW（不再回退钩子）", "iprc1000_gui_dotnet/Backend.cs", "唯一来源"),
    ("ETW 下不装钩子/不注册 Raw Input", "iprc1000_gui_dotnet/KeyFilter.cs", "全程不装钩子、不注册 Raw Input"),
    # ★ v1.2.25：日志总开关（界面可开可关）+ 桥侧也受控
    ("桥支持 --quiet", "tools/etw_consumer/src/main.rs", '"--quiet" => out.quiet = true'),
    ("启桥按开关决定 --quiet", "iprc1000_gui_dotnet/Backend.cs", '+ (LogSwitch.Enabled ? "" : " --quiet")'),
    ("界面日志开关", "iprc1000_gui_dotnet/ui/index.html", 'id="logToggle"'),
    ("开关命令", "iprc1000_gui_dotnet/Backend.cs", "public string SetLog(string? on)"),
    # ★ v1.2.23：每段会话第一条报文只当基线（修"启动瞬间凭空触发一次按键"）
    ("会话基线（防启动假按键）", "tools/etw_consumer/src/main.rs", "if !self.seeded {"),
    # ★ v1.2.21：ETW 模式下 Raw Input 不许再处理一次（注册没撤掉，会双处理）
    # ★ 2026-10-27：随 Raw Input 整条路径删除，原 4 条断言（ETW 挡重复处理 / 过滤后计数 /
    #   Raw Input 报文计数 / 报文数暴露给界面）已无对应行为，一并移除。
    # ★ v1.2.20：被动标定每帧查虚拟桌面 → 改带缓存（原每帧 4 次 GetSystemMetrics）
    ("每帧查询走缓存", "iprc1000_gui_dotnet/MouseEngine.cs", "VirtualScreenCached()"),
    # ★ v1.2.19：热路径不再拼日志字符串 + Raw Input 直读可行性探针
    ("日志关掉后不拼字符串", "iprc1000_gui_dotnet/EtwKeySource.cs", "if (LogSwitch.Enabled)"),
    # ★ v1.2.18：按键行右侧快捷键与"名字"同基线（曾低 8px，看着像错位）
    ("右值与名字同基线", "iprc1000_gui_dotnet/ui/style.css", "align-self:flex-start"),
    # ★ v1.2.18：pwr 热点校正（8 倍放大原图确认圆心 (141,38)，原写 (140,47)）
    ("pwr 热点已校正", "iprc1000_gui_dotnet/Keys.cs", '["power"]        = (141,  38, 44, 46)'),
    # ★ v1.2.17：日志关掉后，前端不许再留任何"日志"字样（用户截图指出）
    ("日志卡片按开关显示", "iprc1000_gui_dotnet/ui/app.js", "logOn && ks.logPath"),
    ("清空日志按钮默认隐藏", "iprc1000_gui_dotnet/ui/index.html", 'id="btnClearLog" hidden'),
    ("状态带 logEnabled", "iprc1000_gui_dotnet/Backend.cs", '["logEnabled"] = LogSwitch.Enabled'),
    # ★ v1.2.17：幽灵帧 0x0C 不许污染"最近按键"（曾显示成「voice（usage 0x0C）」）
    ("幽灵帧不更新最近按键", "iprc1000_gui_dotnet/EtwKeySource.cs", "只对**认得的按键**更新"),
    # ★ v1.2.16：发布版默认不写任何文本日志（用户要求"速度只为快和稳定"）
    ("日志总开关存在（默认关）", "iprc1000_gui_dotnet/LogSwitch.cs", "public static bool Enabled"),
    ("按键日志受开关控制", "iprc1000_gui_dotnet/KeyLog.cs", "!LogSwitch.Enabled"),
    ("路由日志受开关控制", "iprc1000_gui_dotnet/CableRouter.cs", "if (!LogSwitch.Enabled) return;"),
    ("鼠标日志受开关控制", "iprc1000_gui_dotnet/MouseEngine.cs", "if (!LogSwitch.Enabled) return;"),
    ("界面日志受开关控制", "iprc1000_gui_dotnet/MainForm.cs", "发布版默认不写 webview.log"),
    ("桥的 stdout 仍被读走（防阻塞）", "iprc1000_gui_dotnet/Backend.cs", "if (!LogSwitch.Enabled) continue;"),
    # ★ v1.2.15：默认启动进托盘（原来 StartMinimized 是 bool 无初始值 = false）
    ("默认启动进托盘", "iprc1000_gui_dotnet/Settings.cs", "public bool StartMinimized { get; set; } = true;"),
    ("老配置一次性迁移", "iprc1000_gui_dotnet/Settings.cs", "if (_data.SchemaVersion < 1)"),
    ("托盘单击也能打开", "iprc1000_gui_dotnet/MainForm.cs", "_tray.MouseClick"),
    ("启动瞬时可见不建界面", "iprc1000_gui_dotnet/MainForm.cs", "WindowState != FormWindowState.Minimized"),
    # ★ v1.2.15：按键日志异步（同步写盘每行 0.253 ms，一次按键 5~8 行，在注入之前）
    ("按键日志异步写入", "iprc1000_gui_dotnet/KeyLog.cs", "BlockingCollection"),
    ("热路径只入队", "iprc1000_gui_dotnet/EtwKeySource.cs", "KeyLog.Write(line)"),
    ("日志轮转（防越写越慢）", "iprc1000_gui_dotnet/KeyLog.cs", "RotateBytes"),
    ("日志自检入口", "iprc1000_gui_dotnet/Program.cs", "--keylog-test"),
    # ★ v1.2.14：静默失效探针（今天那次"按键全失效过了 8 分钟才发现"的兜底）
    ("按键链路探针（纯函数）", "iprc1000_gui_dotnet/KeyPathWatch.cs", "public static bool Step"),
    ("探针接进桥状态", "iprc1000_gui_dotnet/Backend.cs", "KeyPathWatch.Step("),
    ("探针自检入口", "iprc1000_gui_dotnet/Program.cs", "--keypath-test"),
    ("界面显示探针提示", "iprc1000_gui_dotnet/ui/index.html", "keyPathNote"),
    # ★ v1.2.14：WebView2 延迟到"真正需要界面"时再创建（启动到托盘时省 341 ms + 356 MB）
    # 2026-10-27：加了 `_e2e`（端到端测试模式）例外 —— 断言改为盯住核心判据
    # `!Settings.StartMinimized`（托起时不建界面），而不是整行字面量，
    # 这样以后再加开关不会误报，但**删掉这条判据**仍会失败。
    # 2026-10-27：启动清卡键**必须覆盖所有可注入键**，不能只清修饰键
    #   （真机"换机器还是卡键"的根因：普通键/F 键卡住后重启也清不掉）
    ("启动清理覆盖全部可注入键", "iprc1000_gui_dotnet/KeyFilter.cs", "private static IEnumerable<int> StartupCleanVks()"),
    ("启动清理含 F 键区间", "iprc1000_gui_dotnet/KeyFilter.cs", "for (var vk = 0x70; vk <= 0x87; vk++)"),
    ("启动清理含字母区间", "iprc1000_gui_dotnet/KeyFilter.cs", "for (var vk = 0x41; vk <= 0x5A; vk++)"),
    # 2026-10-27 真机：语音键预设只存后端、不同步弹窗本地状态 ⇒ 用户再点"保存映射"会把旧值写回去
    ("语音预设同步本地状态", "iprc1000_gui_dotnet/ui/app.js", "kbKeyLabel = NM[vk]"),
    ("WebView2 按需创建（托盘启动不建）", "iprc1000_gui_dotnet/MainForm.cs", "if (!Settings.StartMinimized || _selftest"),
    ("托盘启动不建界面（判定存在）", "iprc1000_gui_dotnet/MainForm.cs", "if (Settings.StartMinimized && !_e2e)"),
    # ★ v1.2.9 延迟优化：起播/声卡缓冲都降过，且自适应必须存在（否则降不下去只能手工调）
    ("抖动缓冲起播 24 ms", "iprc1000_gui_dotnet/JitterBuffer.cs", "public int PreRollMs = 24;"),
    ("自适应天花板给上限留余量", "iprc1000_gui_dotnet/JitterBuffer.cs", "CapHeadroomMs"),
    ("声卡缓冲 20 ms", "iprc1000_gui_dotnet/JitterBuffer.cs", "public int LatencyMs = 20;"),
    ("自适应 pre-roll 结算", "iprc1000_gui_dotnet/JitterBuffer.cs", "private void SettleSession()"),
    ("生效起播进 Status", "iprc1000_gui_dotnet/JitterBuffer.cs", "effPreRollMs = _effPreRollMs"),
    ("桥只丢 1 帧 warmup（首字早 20 ms）", "tools/etw_consumer/src/main.rs", "if self.warmup < 1 {"),
    # ★ v1.2.10：把"打开声卡 125 ms"从第一次说话挪到启动；且同一次按键两次回调不许重置计时
    ("启动预热声卡", "iprc1000_gui_dotnet/CableRouter.cs", "PrewarmInBackground"),
    ("WarmUp 幂等（400 ms 内不重置计时）", "iprc1000_gui_dotnet/CableRouter.cs", "TotalMilliseconds < 400"),
    # ★★ v1.2.8 事故：HID 数据 CID 曾被写死 0x0041，某次重连落到 0x40 → 按键全失效、音频没声
    ("HID 数据 CID 用动态范围判定", "tools/etw_consumer/src/main.rs", "fn is_data_cid"),
    ("实测 CID 写进日志（日志自证）", "tools/etw_consumer/src/main.rs", "实际 CID 音频="),
    # ★★ 注入链路的三道保险（2026-10-15 线上排查后加，缺任一道都可能再卡键）
    ("注入按整份报告下发", "iprc1000_gui_dotnet/KeyFilter.cs", "_injectedSet"),
    ("孤儿键回收器存在", "iprc1000_gui_dotnet/KeyFilter.cs", "ReapOrphanKeys"),
    ("孤儿键回收以系统状态为准", "iprc1000_gui_dotnet/KeyFilter.cs", "GetAsyncKeyState"),
    # 2026-10-27：函数已扩展为"清所有可注入键"，改名后同步（旧名会误报）
    ("启动兜底清残留键", "iprc1000_gui_dotnet/KeyFilter.cs", "ClearStaleKeysOnStart"),
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
    # 2026-10-29：升级时桥会占着 assets\etw_consumer.exe 导致覆盖失败
    ("安装流程会先停语音桥", "tools/setup_v2/InstallEngine.cs", "StopBridgeProcess();\n                InstallFiles();"),
    ("卸载流程会先停语音桥", "tools/setup_v2/InstallEngine.cs", "StopBridgeProcess();\n                // 次序很关键"),
    ("覆盖文件带重试", "tools/setup_v2/InstallEngine.cs", "CopyWithRetry"),
    ("鼠标路由挂在 ApplyButton", "iprc1000_gui_dotnet/KeyFilter.cs", "MouseModeRouter"),
    ("鼠标模式有引擎自检", "iprc1000_gui_dotnet/MouseEngine.cs", "static string SelfTest"),
    ("引擎自检有 CLI 入口", "iprc1000_gui_dotnet/Program.cs", "--mouse-engine-test"),
    # ★ 鼠标模式页（阶段 2）
    # 2026-10-29：鼠标参数已并入环境设置，原来的独立页删除 -> 改断言 #mouseParams 容器
    ("鼠标参数已并入环境设置", "iprc1000_gui_dotnet/ui/index.html", 'id="mouseParams"'),
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
    # 2026-10-29：命令分发已从 MainForm 搬到 Backend.Dispatch，断言跟着搬
    ("界面取数用 mouseSample", "iprc1000_gui_dotnet/Backend.cs", "\"mouseSample\""),
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
    ("新会话按空档判、并跳过序号比较", "tools/etw_consumer/src/main.rs", "let session_start = self.last_frame_at"),
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
    # 2026-10-17 v1.2.9：30 → 20（再省 10 ms）；这条断言的价值是"别被人改回去"
    ("声卡 IO 缓冲降到 20 ms", "iprc1000_gui_dotnet/JitterBuffer.cs", "LatencyMs = 20"),
    ("上限必须 ≥ 起播阈值（否则永久静音）", "iprc1000_gui_dotnet/JitterBuffer.cs", "if (CapMs < PreRollMs) CapMs = PreRollMs"),
    ("抖动缓冲有自检", "iprc1000_gui_dotnet/JitterBuffer.cs", "public static string SelfTest"),
    ("抖动自检有 CLI 入口", "iprc1000_gui_dotnet/Program.cs", "--jitter-test"),
    ("路由改用有界缓冲（不再用 1 秒 BufferedWaveProvider）", "iprc1000_gui_dotnet/CableRouter.cs", "new JitterBuffer()"),
    ("界面能看到抖动缓冲", "iprc1000_gui_dotnet/ui/app.js", "抖动缓冲"),
    # ★★ 鼠标动作映射（2026-10-16 用户要求）：把任意按键配成 左键/右键/滚轮上/滚轮下
    ("映射支持动作值", "iprc1000_gui_dotnet/KeyAct.cs", "public const int Left = -1"),
    ("动作值走 SendInput（不进虚拟键盘）", "iprc1000_gui_dotnet/KeyFilter.cs", "KeyAct.IsSingleAction"),
    ("鼠标动作有日志留痕", "iprc1000_gui_dotnet/KeyFilter.cs", "映射={name}（动作）"),
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
           "-p:PublishSingleFile=true",
           # ★ 发包版**不带任何调试入口**（--selftest / --*-test 分支根本不编译）。
           #   开发时直接 dotnet build/publish（不加这个参数）仍旧带，自检照跑。
           "-p:DevTools=false"]
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
    # ★ 2026-10-20：`dotnet publish` 有时**不带** ui\icon.ico（bin 里有、publish 里没有），
    #   而托盘图标会因此退回系统通用图标（用户看到的"两个小窗口"）。这里显式补一份。
    src_ico = os.path.join(ROOT, "iprc1000_gui_dotnet", "ui", "icon.ico")
    uip_early = os.path.join(out, "ui")
    if os.path.exists(src_ico):
        os.makedirs(uip_early, exist_ok=True)
        shutil.copy2(src_ico, os.path.join(uip_early, "icon.ico"))

    # 立刻自检 ui 是否真的复制过来了（这是坑 ② 的哨兵）
    uip = os.path.join(out, "ui")
    need = ["app.js", "index.html", "style.css", "remote.png", "icon.ico"]
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
    for n in ("创建桌面快捷方式.cmd", "启动.cmd", "部署.cmd"):
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
        # ★ 2026-10-27：改为指向双击入口。右键运行**不是管理员**，用户最容易卡在这一步。
        f.write("  3) 首次部署环境：**双击包里的「部署.cmd」**（会自动请求管理员权限，\n")
        f.write("     依次：装虚拟键盘驱动 → 切设备驱动 → 装虚拟声卡 → 启动程序）。\n")
        f.write("     若习惯手动：也可在管理员 PowerShell 里运行 tools\\一键部署.ps1\n\n")
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
        # ⚠ 这段**依赖仓库是公开的**：改成 private 后该链接对外实测 404
        #   （2026-10-17 验证过），届时要把这里换成"找我要最新安装包"，
        #   并同步改 CHECKS 里那条断言的关键字。见运维手册 §1.5。


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
                "  · 方块键（出厂值 = 动作值「切换配置组」，可改绑成任意键/鼠标动作）\n"
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

    # ── 体检修复按钮 ↔ Dispatch 命令 一致性（2026-10-30 新增）──
    #   踩过的坑：Doctor 给出 action="installUhid"，Dispatch 却没有这个命令；
    #   前端是 B[action]() **动态调用** ⇒ 用户点体检里的「一键安装驱动」只得到
    #   「未知方法 installUhid」，按钮是死的，而且编译期、ui_probe 都发现不了。
    try:
        btxt = open(os.path.join(ROOT, "iprc1000_gui_dotnet", "Backend.cs"),
                    encoding="utf-8", errors="replace").read()
        disp = set(re.findall(r'"([A-Za-z][A-Za-z0-9_]*)"\s*=>', btxt))
        # 界面上会按 action 名动态调用的命令（新增时**必须**同步加进 Dispatch）
        doc_actions = ["installCable", "installUhid", "relaunchAdmin", "setDefaultMic",
                       "startBridge", "releaseAllKeys", "resetKeys", "repairDriver",
                       "exportDriverDiag", "exportWinuhid", "setDeviceDriver", "runSetup", "importKmgo", "setMacroSpeed"]
        miss = [a for a in doc_actions if a not in disp]
        ok = not miss
        ok_all &= ok
        print(f"  {'✓' if ok else '✗'} 界面按 action 动态调用的命令都存在"
              + ("" if ok else f"  ← Dispatch 缺：{', '.join(miss)}（点了会报「未知方法」）"))
    except Exception as e:
        print(f"  ⚠ 体检动作一致性检查没跑成：{e}")

    # ── 状态字段层级检查（2026-10-27 新增）──
    #   症状：字段被误加进子对象 ⇒ 界面读 state.xxx 得到 undefined ⇒
    #   开关点了没用 / 提示不显示 / 输入框永远默认值。编译器与 ui_probe 都发现不了。
    try:
        r = subprocess.run([sys.executable, os.path.join(ROOT, "tools", "check_state_fields.py")],
                           capture_output=True, text=True, encoding="utf-8", errors="replace")
        ok = (r.returncode == 0)
        ok_all &= ok
        print(f"  {'✓' if ok else '✗'} 状态字段层级（GetState 顶层 vs 界面读取）")
        if not ok:
            for line in (r.stdout or "").splitlines()[:8]:
                print("      " + line)
    except Exception as e:
        print(f"  ⚠ 状态字段层级检查没跑成：{e}")

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
    ok_main = verify()

    # ── ② 免运行时版（自包含 + 压缩，约 64 MB）──
    #    需求来源：门店机器常年「无网 + 店员没有管理员权限」，
    #    而 .NET 桌面运行时安装包约 55 MB、装完占约 170 MB —— 这一步经常卡死。
    #    自包含版把这些全省掉，代价是包变大 + 每次启动多约 165 ms 的解压。
    print()
    publish_fresh(out=FULL_PUB, self_contained=True, compressed=True)
    full_tgt = build(pub=FULL_PUB, target=FULL_PKG, variant="full")
    ok_full = verify(zp=FULL_ZP, title="免运行时版", self_contained=True, source_checks=False)

    # ── 单文件安装包（IPRC1000_Setup_v<版本>.exe）──
    #    载荷 = 刚打好的免运行时版（含 .NET 8）；引导程序本身用 net48（Win10/11 自带）。
    #    失败只警告、不阻断 —— 安装包是加分项，zip 包才是主线。
    print()
    try:
        # ★ 2026-10-28：改用**新版安装器**（tools/setup_v2，移植自 DinggeVoiceHub 的
        #   InstallEngine + 安装页面）：可选安装位置、逐文件哈希校验、组件按需安装、
        #   蓝牙参数备份/修复模式/卸载清单。旧的极简引导程序保留在 build_setup.py 备查。
        r = subprocess.run([sys.executable, os.path.join(ROOT, "tools", "build_setup_v2.py")],
                           capture_output=True, text=True, encoding="utf-8", errors="replace")
        for line in (r.stdout or "").splitlines()[-4:]:
            if line.strip():
                print(line)
        if r.returncode != 0:
            print("  ⚠ 安装包构建失败（不影响上面的 zip 包）")
    except Exception as e:
        print(f"  ⚠ 安装包构建没跑成：{e}")

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

    # ★★ 2026-10-30：检查未通过必须让**退出码非 0**。
    #   之前只打印「★ 有检查未通过 ✗」但 exit 0 —— 外层脚本/CI 根本看不出失败，
    #   实测就这样带着一条红断言把包打了出去。
    if not (ok_main and ok_full):
        print("\n★ 检查有未通过项 —— 包已生成，但**不要发版**（先修断言或代码）")
        sys.exit(1)
