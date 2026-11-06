# Round 1 · 开发侧回应（2026-11-06）

状态：**接受 S2 根因，判据已按客户侧实测改写**

## 对 06-verdict.md 的确认

| 客户侧结论 | 开发侧处理 |
|---|---|
| `REFUSED_PSM` = 主机侧 PSM 0x0011 无监听者 | **采纳**。0.4.40 的 Raw Input 改造方向没错，但它只解决「`\kbd` 的 SYSTEM-only ACL」这一层；本次故障在更上游（SDP 未成功），Raw Input 列表里不可能有设备 |
| 断开原因码是 `0x13 REMOTE_USER_TERMINATED`，不是 `0x01` | **采纳**，我方 `parse_live.py` 解析有位移错误，已记 |
| 假绿灯来自 `Enum\HID` 残留 | **方向对，细节修正**：开发机实测 `Enum\HID` 下**一个残留都没有**，判据本身仍然错——它读的是枚举时缓存的 `HardwareID`（永远含 `HID_DEVICE_SYSTEM_KEYBOARD`），与「HID 服务是否绑定」无关。已改为读 BTHENUM 的 `Service` |
| `--restore-keyboard` 无效 | **采纳**。已从所有提示文案里撤掉对该命令的推荐；它只对「`Enum\HID` 有 raw 空壳残留」有效 |

## 两条硬要求

| # | 要求 | 状态 |
|---|---|---|
| 1 | `convert_to_hid_compliant()` 删除或默认关闭 | **已完成（0.4.40）**：函数恒返回 `Err`，命令层入口删除，界面按钮改为「恢复键盘模式」 |
| 2 | `remote_still_keyboard()` 改读 `Enum\BTHENUM\{00001124...}` 的 `Service` | **已完成**：新增 `bt_hid_bound()`，三态含义重定义，启动日志 / 一键修复 / egui / Win32 文案全部同步 |

## 三态语义（新）

| 返回值 | 含义 | 界面 | 修法 |
|---|---|---|---|
| `Some(true)` | `Service=HidBth`，主机有 PSM 0x0011 监听者 | 绿 | 无需处理 |
| `Some(false)` | 有 `{00001124}` 节点但未绑定 | 红 | 删除设备 → 重新配对 |
| `None` | 无 `{00001124}` 节点（SDP 未成功） | **红**（原来误显示绿色「未连接」，这是第二个假绿灯） | 删除设备 → 重新配对 |

## 顺带修掉的观测缺陷

1. **Raw Input 认领全落空却无痕**：启动/巡检现在打印真实设备名；`WM_INPUT` 到达但未认领时打印 VKey + 设备名；`--hid-probe` 列全部设备及认领原因；新增 `IPRC1000_RAWINPUT_ANY=1` 诊断开关。
2. **句柄泄漏**：客户机 `bridge.log` 540 秒 391 → 2098（3.2/s），只在 WinUHid 注入成功那轮出现。已实测排除 Raw Input 三个 API（300 轮 × 17 设备，全 +0），**尚未定位**，列 R2 待办。

## 待客户机验证

1. 删除 `IPRC1000` → 重新配对 → 跑 `tools\diag-bt.ps1`，要求 `{00001124}` 节点出现且 `Service=HidBth`
2. 新版启动日志应出现「蓝牙 HID 服务已绑定（Service=HidBth）」；若仍是 `REFUSED_PSM`，转硬件方案（USB dongle / Intel AX200）
3. 若服务记录仍 0 条 → SDP 在这块 Realtek 8821CE 上跑不起来，属适配器问题，不要再改软件

## 代码位置

- `src/device_probe.rs` — `bt_hid_bound()`
- `src/hid_reader.rs` — `start()` 三态日志 + Raw Input 认领可观测
- `src/commands.rs` — ④ 遥控器按键通道
- `src/ui_egui.rs` / `src/ui_win32.rs` — 通道状态文案
