# IPRC1000 原生内核（Rust）

遥控器适配器的 **Rust + egui 重写版**。与 C# 版是两个产品、两条版本线、两个仓库，
**不要混用版本号**。

## 两个版本的关系（重要）

| | 原生版（本仓） | C# 版 |
|---|---|---|
| 目录 | `F:\vibe\iprc1000_native` | `F:\vibe\iprc1000_gui_dotnet` |
| 仓库 | **本仓（独立 git）** | 父仓 `F:\vibe`（由 `.gitignore` 白名单跟踪） |
| 版本 | `Cargo.toml` 的 `version`，当前 **0.4.11** | `IPRC1000Adapter.csproj` 的 `<Version>`，当前 **1.2.164** |
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
5. **设备改造**：遥控器默认是蓝牙 HID 键盘（会往焦点窗口打字母）。
   正解是把它 HID 接口驱动换成 `HID-compliant device`（环境页「④ 改造遥控器」），
   **不是吞键**——低级钩子拿不到设备来源，会误伤真键盘。

## 日志位置

`%APPDATA%\IPRC1000 Adapter\native.log`（程序）、`bridge.log`（音频桥/周期资源采样）。
