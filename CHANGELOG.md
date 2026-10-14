# 变更记录

本文件记录本项目的所有重要变更。
格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，
版本号遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

---

## [1.0.2] - 2026-10-14

构建卫生修复。**功能与 1.0.1 完全一致**，只是把编译警告清零、发布包瘦身。

> 1.0.1 发布后 15 分钟内撤回（下载数 0）：那个提交在 CI 上跑不过 ——
> 见下方「修复」。CI 门禁本身是对的，它抓出了这个真实的构建问题。

### 修复

- **消除 22 条 `MSB3277` 编译警告**（WindowsBase 版本冲突）。
  根因：`Microsoft.Web.WebView2` 包的 `lib_manual` 目标是 `net5.0-windows10.0.17763.0`，
  与 `net8.0-windows` 匹配，于是**用不到的 WPF 版程序集也被引用进来**；
  它引用 WindowsBase 5.0，而 WinForms 用 4.0，两者冲突。
  本程序只用 WinForms 版，故在 `ResolveAssemblyReferences` **之前**把
  `Microsoft.Web.WebView2.Wpf` 从引用列表移除。
  警告数 **22 → 0**，发布包同时瘦身 **86 KB**（2,589,799 → 2,503,806 字节）。
- CI 的「零警告」门禁在 1.0.1 上确实失败了 —— 不是门禁误报，是真实问题，
  已按上述方式根治（而不是放宽门禁）。

---

## [1.0.1] - 2026-10-14

### 新增

- **界面显示版本号与构建时间**（左下角，如 `v1.0.1（构建于 2026-10-14 15:37）`）。
  版本号由 csproj 的 `<Version>` 经程序集元数据传入，构建时间由 MSBuild 注入 ——
  用来一眼确认"手上跑的是不是最新编译的那个"，本项目为此反复踩过坑。
- **浏览器预览模式**：直接用浏览器打开 `ui/index.html` 即可看界面（无需后端、自带示例数据），
  支持 `?page=overview|keys|env` 与 `?modal=<按键id>`。README 的截图就是用它自动生成的。
- **README 界面截图**（`tools/docs/screenshots/`，4 张）。
- **GitHub Actions CI**：编译 + 零警告检查 + 发布产物完整性 + 打包脚本 24 项校验。
- 本变更记录。

### 安全

- **WebView2 导航守卫**：拦截一切离开本地虚拟主机（`iprc1000.local`）的导航与新窗口。
  `OnWebMessage` 是 JS → 后端的桥，暴露了 40 多个方法，其中
  `reboot` / `installCable` / `installWinuhid` / `setDeviceDriver` / `relaunchAdmin` /
  `runSetup` / `applyRegistry` 等为**管理员级**动作，而本程序以管理员身份运行。
  界面目前全为本地文件、无外链，故现状不可利用；此改动是纵深防御。
  注意 `data:` 也在拦截之列（它本身就是一种注入途径）。

### 修复

- **`Backend.Dispose()` 未把 `_disposed` 置位**，导致探针后台线程（每 8 秒扫一次注册表）
  永远不会退出 —— 该线程以 `while (!_disposed)` 为退出条件。
  顺带删除从未使用的字段 `_probing`。编译警告由 2 条降为 **0 条**。

### 变更

- 发布包名随版本号自动生成（`IPRC1000_Adapter_v{版本}.zip`），由 `package.py` 读取
  csproj 的 `<Version>` —— 避免"csproj 写 1.0.1 而包名还叫 v1.0"这类漂移。
- 发布包内的界面截图改用 `tools/docs/screenshots/` 下的当前截图，
  不再使用根目录那张会过期的 `_ui_kb.png`（它把 mac 风格的
  `Control/Option/Command` 发进了包，而程序早已改为 `Ctrl/Alt/Shift/Win`）。

---

## [1.0.0] - 2026-10-14

首个公开发布。

### 功能

- **语音输入**：按住遥控器【语音键】→ 注入「右 Alt（按住不放）」→ 触发豆包输入法长按模式；松手即释放。
- **按键映射**：23 个按键全部可改，支持单键 / 组合键（Ctrl·Alt·Shift·Win）/ 媒体键 / 不执行。
- **两块界面**：左侧遥控器实体图（点键高亮），右侧每行按键缩略图 + 当前映射。
- **多组配置**：内置「Windows 日常」与「WorkBuddy 快捷键」，【方块键】循环切换；
  支持新建 / 重命名 / 删除 / 搜索 / 导出 / 导入。
- **环境一键就绪**：虚拟声卡、虚拟键盘驱动、设备驱动、默认麦克风，缺哪项点哪项。
- **卡键三层防护**：退出释放 + 启动自愈 + 独立守卫脚本。

### 解决的关键问题

| 问题 | 根因 | 解法 |
|---|---|---|
| 遥控器当键盘时一直输出 `m` | 设备只发 M-down、UP 丢失 | 设备驱动由 `kbdhid` 换成 `HID-compliant device` |
| 钩子吞掉 UP 导致键盘某键失效 | 拦截时把 keyup 也吞了 | 只吞 DOWN，UP 一律放行 |
| ETW 读按键要等 ~800 ms | 空闲时缓冲填不满，不刷新 | 自填缓冲，每 25 ms 写 2 KB ⇒ 实测 **791 ms → 13 ms** |
| 音量 / 静音 / 播放键无效 | Consumer Page 键，虚拟 HID 键盘表达不了 | 媒体键改走 `SendInput` |
| `Win+L` 等系统热键注入无效 | `SendInput` 会被系统拦掉 | 改走虚拟 HID（与真实键盘同一内核路径） |
| 退出时右 Alt 未释放 → 输入法失效 | 进程被杀后键仍为「按下」 | 三层防护 |
| **改按键映射完全没反应** | 注入路径读的是另一份旧格式配置 → 反序列化失败被静默吞掉 → 回落到出厂默认 | 注入与界面统一走同一数据源，并加 `--map-dump` 自检防复发 |

### 内置第三方组件

发布包内置 WinUHid（MIT）、VB-CABLE（VB-Audio Donationware）、语音桥 `etw_consumer.exe`。
版权与许可详见 [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md)。

---

[1.0.2]: https://github.com/lymsiq/IPRC1000-Adapter/releases/tag/v1.0.2
[1.0.1]: https://github.com/lymsiq/IPRC1000-Adapter/releases/tag/v1.0.1
[1.0.0]: https://github.com/lymsiq/IPRC1000-Adapter/releases/tag/v1.0.0
