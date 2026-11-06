# IPRC1000 原生内核（Rust）

遥控器适配器的 **Rust + egui 重写版**。与 C# 版是两个产品、两条版本线、两个仓库，
**不要混用版本号**。

## 两个版本的关系（重要）

| | 原生版（本仓） | C# 版 |
|---|---|---|
| 目录 | `F:\vibe\iprc1000_native` | `F:\vibe\iprc1000_gui_dotnet` |
| 仓库 | **本仓（独立 git）** | 父仓 `F:\vibe`（由 `.gitignore` 白名单跟踪） |
| 版本 | `Cargo.toml` 的 `version`，当前 **0.4.55** | `IPRC1000Adapter.csproj` 的 `<Version>`，当前 **1.2.164** |
| 界面 | egui（原生，无 WebView2） | WinForms + WebView2 |
| 发布包 | `IPRC1000_Native_v<版本>.zip` / `IPRC1000_Native_Setup_v<版本>.exe` | `IPRC1000_Adapter_v<版本>.zip` |
| 关系 | 逐步取代 C# 版 | 存量用户仍在用 |

**两者不能同时运行**：会抢同一个 WinUHid 虚拟键盘设备和同一个 ETW 会话
（症状是"进程活着但按键/语音全无反应"）。

## 关键约定

1. **版本号唯一来源**：本仓只认 `Cargo.toml` 的 `version`；界面显示、exe 属性、发布包名都跟它。
2. **构建顺序**（容易搞错）：
   ```
   ① cargo build --release          # 主程序
   ② 重新生成 src/payload.zip        # 安装器内嵌的程序本体
   ③ cargo build --release          # 再编 setup.exe（include_bytes! 内嵌上面那个 zip）
   ```
   顺序错了会打出"安装器装出旧版本"的包（真实踩过）。
3. **系统字体不可内置**：`msyh.ttc` 等是微软专有字体，只能运行时从系统读取；
   要内置必须换成 OFL 授权的开源字体。
4. **语音键是租约模型**：`右 Alt 应按住 ⟺ now_ms() < VOICE_LEASE_MS`。
   只有 `reconcile_voice()` 能改 `held[0x10]`；**可靠事件才能续租，不可靠事件只能收口**。
5. **遥控器不要改造**（2026-11-06 真机结论，推翻旧说明）：
   保持「键盘」身份时蓝牙 HID 中断通道才建立，程序才收得到按键。
   改绑成 `input.inf / raw` 后接口不注册 ⇒ 按键与音频同时全断。
   代价是系统也会打字母（已知边界）。误改造的机器用 `--restore-keyboard` 恢复。
6. **按键通道 = Raw Input**（0.4.40，客户机根因）：键盘顶层集合 `\kbd` 接口的
   ACL 是 SYSTEM-only，`CreateFileW` 必然 `err=5`，管理员也无效。
   `hid_reader` 改为独立线程 + 隐藏窗口收 `WM_INPUT`（`RIDEV_INPUTSINK`），
   按设备名 `0A5C+8502` 过滤，用 `HELD[256]` 还原 HID 报告快照。
   **按键不需要管理员**；仍需管理员的只有语音 ETW 会话与安装动作。
7. **Raw Input 认领必须可观测**（0.4.41，客户机复盘）：0.4.40 的设备过滤只认
   `0A5C+8502`，而 hidbth 生成的 `RIDI_DEVICENAME` 可能不带 VID/PID；再加上
   「先问大小再回填同一个 `pcbSize`」的字节/字符语义不一致，名字可能整个读不出来
   ⇒ WM_INPUT 全部静默丢弃，日志却只报「暂未发现设备」，分不清三种故障。
   现在：固定 512 WCHAR 缓冲 + 蓝牙 HID 服务 GUID 兜底认领 + 启动/巡检打印**真实设备名**
   + 记录「WM_INPUT 到达但未认领」；`--hid-probe` 列出全部设备及其认领原因。
   诊断开关 `IPRC1000_RAWINPUT_ANY=1` 可临时跳过过滤，用来确认消息到底有没有到。

## 日志位置

`%APPDATA%\IPRC1000 Adapter\native.log`（程序）、`bridge.log`（音频桥/周期资源采样）。
