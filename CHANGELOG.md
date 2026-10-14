# 变更记录

格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，
版本号遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

---

## [1.0.2] - 2026-10-14

**功能与 1.0.0 一致，仅构建与文档调整。**

### 修复

- 消除 22 条 `MSB3277` 编译警告（去掉用不到的 WebView2 WPF 程序集），exe 瘦身 86 KB。
- `tools/package.py` 在英文/西欧语系系统上因输出编码为 cp1252 而崩溃，已强制 UTF-8。

### 变更

- 发布包名随版本号自动生成。
- 文档精简：只保留功能介绍与部署使用说明。

> v1.0.1 已撤回（发布后 15 分钟内发现 CI 未通过，下载数 0）。

---

## [1.0.1] - 2026-10-14

### 新增

- 界面左下角显示**程序版本号与构建时间**。
- README 与发布包内加入**界面截图**。
- **GitHub Actions CI**：编译 + 零警告门禁 + 发布产物完整性 + 打包校验。
- 浏览器预览模式支持 `?page=overview|keys|env` 与 `?modal=<按键id>`。

### 修复

- `Backend.Dispose()` 未置 `_disposed`，探针后台线程不会退出。

### 安全

- 新增 **WebView2 导航守卫**，拦截离开本地页面的导航与新窗口。

---

## [1.0.0] - 2026-10-14

首个公开发布。

### 功能

- **语音输入**：按住【语音键】注入右 Alt（按住不放），触发豆包输入法长按模式。
- **按键映射**：23 个按键可改，支持单键 / 组合键 / 媒体键 / 不执行。
- **界面**：左侧遥控器实体图（点键高亮），右侧每行按键缩略图 + 当前映射；点行弹出屏幕键盘改键。
- **多组配置**：内置「Windows 日常」与「WorkBuddy 快捷键」，【方块键】循环切换。
- **环境一键就绪**：虚拟声卡、虚拟键盘驱动、设备驱动、默认麦克风。
- **卡键三层防护**：退出释放 + 启动自愈 + 独立守卫脚本。
- 修复按键映射修改后不生效的问题（注入与界面统一走同一数据源）。

### 内置第三方组件

WinUHid（MIT）、VB-CABLE（VB-Audio Donationware）、语音桥 `etw_consumer.exe`。
详见 [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md)。

---

[1.0.2]: https://github.com/lymsiq/IPRC1000-Adapter/releases/tag/v1.0.2
[1.0.0]: https://github.com/lymsiq/IPRC1000-Adapter/releases/tag/v1.0.0
