# IPRC1000 遥控器适配器

把 **Bluetooth 语音遥控器**（IPRC1000 / BCM20734）变成 Windows 的**语音输入 + 快捷键盘**。

- 按住遥控器【语音键】说话 → 自动注入 **右 Alt（按住不放）** → 豆包输入法的「长按模式」开始收音
- 方向 / 确认 / 返回 / 音量 等键 → 注入对应快捷键（可自由映射，支持组合键、媒体键）
- 最底部【方块键】→ **循环切换配置组**；内置「Windows 日常」与「WorkBuddy 快捷键」两组

---

## 亮点 / 踩过的坑

这个项目解决了一串 Windows 上很隐蔽的问题，记录下来供参考：

| 问题 | 根因 | 解法 |
|---|---|---|
| 遥控器当键盘时一直输出 `m` | 设备只发来 M-down、UP 丢失 → Windows 把 M 锁在「按下」持续合成 | 把设备驱动从 `kbdhid` 换成 `HID-compliant device`（普通 HID），系统不再当键盘 |
| 钩子吞掉 UP 导致键盘某键失效 | 拦截时把 keyup 也吞了 → 系统永远等不到释放 | **只吞 DOWN，UP 一律放行** |
| 幽灵报告变成字母 `i` | 设备每按一次键都附发一个 usage `0x0C` 的"幽灵报告" | 见上：非键盘模式下系统根本不产生 VK |
| ETW 读按键要等 ~800 ms | ferrisetw 的 `flush_timer` 最小只能取整到 **1 秒**，空闲时蓝牙流量填不满缓冲 | **自填缓冲**：自己注册一个 ETW 提供者，每 25 ms 写一个 2 KB 事件把缓冲填满逼它刷新 ⇒ 实测 **791 ms → 13 ms** |
| 音量 / 静音 / 播放键无效 | 这些是 **Consumer Page** 键，WinUHid 的键盘设备表达不了 | 媒体键改走 `SendInput` |
| `Win+L` 等系统热键注入无效 | 普通 `SendInput` 会被系统拦掉 | 走 **WinUHid 虚拟 HID 设备**（与真实键盘同一条内核路径）|
| 退出时注入的键没释放 → 输入法失效 | 按住注入的右 Alt 在进程被杀后仍为"按下" | 三层防护：退出释放 + 启动自愈 + 独立卡键守卫 |

---

## 目录结构

```
iprc1000_gui_dotnet/       主程序（.NET 8 + WinForms + WebView2 界面）
  ├─ Backend / KeyFilter / Keymaps / Keys / Env / WinUhidInjector / EtwKeySource ...
  ├─ ui/                   界面（HTML/CSS/JS + 遥控器图片）
  └─ assets/               运行期资源（第三方二进制见下方"准备"）
tools/
  ├─ etw_consumer/         语音桥（Rust）：从 BthPort ETW 读按键与音频
  ├─ SetHidDriver/         设备驱动切换工具（键盘 ↔ 普通 HID）
  ├─ 一键部署.ps1           首次部署：驱动 + 设备绑定 + 声卡 + 启动
  ├─ stuck_key_guard.py    独立卡键守卫（App 没跑也能救）
  ├─ check_stuck_key.py    卡键根因检查
  ├─ package.py            一键打包 + 包内容校验
  └─ docs/                 SOP 与遥控器说明书
```

---

## 准备

### 运行依赖

- Windows 10/11 x64
- **.NET 8 Desktop Runtime**
- **WebView2 Runtime**（Win10/11 一般自带）
- 管理员权限（ETW 抓包 + 驱动安装）

### 需要自行获取的第三方组件

仓库**不包含**以下二进制（体积大 / 许可限制），请自行下载放到 `iprc1000_gui_dotnet/assets/`：

| 组件 | 用途 | 从哪来 |
|---|---|---|
| **WinUHid** | 虚拟 HID 键盘（注入右 Alt 说话、系统热键必需）| WinUHid 项目发布页 |
| **VB-CABLE** | 虚拟声卡（音频回环）| VB-Audio 官网 |
| `etw_consumer.exe` | 语音桥 | 本项目自建：`cd tools/etw_consumer && cargo build --release` |

> 官方的**发布包 zip** 里已内置这些组件，`tools/一键部署.ps1` 会自动安装它们。

---

## 构建

```powershell
# 语音桥（Rust）
cd tools/etw_consumer
cargo build --release
copy target\release\etw_consumer.exe ..\..\iprc1000_gui_dotnet\assets\

# 主程序（.NET 8）
cd iprc1000_gui_dotnet
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

## 部署

```powershell
# 右键「以 PowerShell 运行」（会自己提权）
tools\一键部署.ps1
```

脚本依次完成：① WinUHid 驱动 ② 设备驱动绑定（kbdhid → HID-compliant device）
③ VB-CABLE 声卡 ④ 启动主程序（其余自动化由程序完成）

详细步骤与排障见 **[`tools/docs/SOP-部署与使用.md`](tools/docs/SOP-部署与使用.md)**，
键位表见 **[`tools/docs/遥控器按键说明.md`](tools/docs/遥控器按键说明.md)**。

---

## 使用

| 操作 | 效果 |
|---|---|
| 按住 🎤 | 语音输入（豆包长按模式）|
| 按 ▭ | 循环切换配置组（默认组 ↔ WorkBuddy 快捷键）|
| 「按键映射」页 | 左侧遥控器高亮 + 右侧每行按键截图；点一行弹出**屏幕键盘**改键 |
| 配置组 Tab | `+` 复制新建 · 双击重命名 · `−` 删除 · 搜索框过滤 · 导出 / 导入 |

改键支持：单键、组合键（Ctrl/Alt/Shift/Win）、媒体键、以及「不执行」。
语音键另有 **豆包（右 Alt）** 与 **微信输入法（Ctrl+Win）** 两个一键预设。

---

## 已知限制

- **ETW 模式**下按键有约 10~200 ms 延迟（活跃时 ~13 ms，空闲 ~200 ms）—— 这是 Windows ETW 刷新周期的硬限制，
  已用"自填缓冲"压到现有水平。要彻底消除只能自写 UMDF 过滤驱动。
- 仓库不含第三方驱动/声卡二进制，首次部署需按上文准备。

## License

MIT
