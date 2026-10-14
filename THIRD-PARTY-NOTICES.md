# 第三方组件声明 / Third-Party Notices

本项目（IPRC1000 Adapter）自身以 **MIT** 许可发布，见 [`LICENSE`](LICENSE)。

发布包（`IPRC1000_Adapter_v1.0.zip`）**内置**下列第三方组件，各自的版权与许可如下。
其中 WinUHid 为 MIT 许可，按该许可要求在此**随附完整许可证文本**。

---

## 1. WinUHid —— 虚拟 HID 设备仿真框架

- 用途：虚拟键盘驱动。注入的按键与真实键盘走同一条内核路径，
  输入法不会过滤，且 `Win+L` 等系统热键能够生效。
- 上游：<https://github.com/cgutman/WinUHid>
- 许可：**MIT**
- 包内位置：`assets/winuhid/`

```
MIT License

Copyright (c) 2025 Cameron Gutman

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

> 注意：本包内的驱动与安装脚本来自上游，**请勿用其它来源的版本替换**
> （安装脚本做了一些针对性适配，见 `assets/winuhid/安装说明.md`）。

---

## 2. VB-CABLE (VB-Audio Virtual Cable) —— 虚拟声卡

- 用途：把遥控器语音从 `CABLE Input` 回环到 `CABLE Output`，供输入法取用。
- 上游：<https://vb-audio.com/Cable/>
- 许可：**Donationware**（VB-Audio 自有条款）
- 包内位置：`assets/VBCABLE_Driver_Pack45.zip`

本包内置的是 VB-Audio 官方发布的**原始未修改**驱动包，仅作原样再分发：

- 未做任何修改或重打包；
- 不单独销售、不以此为收费项；
- 若你从本项目受益，建议到 <https://vb-audio.com/Cable/> 向作者捐赠。

---

## 3. ferrisetw —— ETW 事件消费库（Rust）

- 用途：语音桥读取 BthPort ETW 事件（按键与音频）。
- 上游：<https://crates.io/crates/ferrisetw>（作者 n4r1b）
- 许可：**MIT OR Apache-2.0**
- 形态：静态链接进 `assets/etw_consumer.exe`

---

## 4. hound —— WAV 编解码（Rust）

- 用途：语音桥导出/处理 WAV。
- 上游：<https://crates.io/crates/hound>
- 作者：Ruud van Asseldonk
- 许可：**Apache-2.0**
- 形态：静态链接进 `assets/etw_consumer.exe`

---

## 5. 运行期依赖（由用户环境提供，本项目不打包）

| 组件 | 用途 | 许可 |
|---|---|---|
| .NET 8 Desktop Runtime | 主程序运行时 | MIT（Microsoft）|
| Microsoft WebView2 Runtime | 界面渲染（用系统 Edge）| 微软专有条款 |
| NAudio | 写 `CABLE Input`（WASAPI）| MIT |

---

## 许可证全文

- MIT 全文已在上文第 1 节给出；亦可参见 <https://opensource.org/license/mit>。
- Apache-2.0 全文：<https://www.apache.org/licenses/LICENSE-2.0>。
