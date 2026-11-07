> ⚠ 历史快照（2026-11-06）：本文关于「改绑 HID 会导致接口消失/按键全断」「按键只走 Raw Input」「重新配对是唯一解」的结论已于 2026-11-07 更正。当前口径见 VERSION.md 与 IPRC1000_Native_用户手册.html：改绑可选（HID_Inst 接口照常注册）；按键主路径 ETW BthPort ACL；休眠后全断的真因是 Realtek 组合卡蓝牙链路断，处理是禁用其蓝牙换独立适配器。

# Round 1 结论（客户机侧）

**状态：S2 根因确认**

## 结论

遥控器、ACL 链路、加密全部正常。故障在**主机侧服务发现**：

```
SDP 从未成功执行（缓存服务记录 = 0 条）
  → 无 Enum\BTHENUM\{00001124-...} HID 服务节点
  → HidBth 未绑定该设备
  → 主机 L2CAP 没有 PSM 0x0011 的监听者
  → 遥控器主动开 HID-Control 被 REFUSED_PSM（先 PENDING 15 秒）
  → 无 HID-Interrupt、无 HID 报告
  → Raw Input 设备列表永远为空 → 按键无反应 / hidReports=0 / keyEvents=0
```

## 与开发侧结论的异同

| 项 | 开发侧 | 客户侧实测 | 判定 |
|---|---|---|---|
| HID-Control 被 REFUSED_PSM = 主机侧无监听者 | 是 | **是** | 一致，开发侧正确 |
| 断开原因码 | `0x01` | **`0x13` REMOTE_USER_TERMINATED** | 开发侧解析器有位移错误 |
| 假绿灯来源 `Enum\HID` 残留 | 有残留 | **本机无残留** | 开发侧按开发机推断，需按 R2 换判据 |
| 修法 `--restore-keyboard` | 建议 | **无效**（无节点可恢复） | 必须重新配对 |

## 修法（唯一可行）

1. 设置 → 蓝牙和其他设备 → 删除 `IPRC1000`
2. 遥控器进配对模式（指示灯快闪）
3. 添加设备 → 蓝牙 → 选中并配对
4. 跑 `tools\diag-bt.ps1`，要求 C2/C3 转 OK

这一步会强制 Windows 在**新建链路上重跑 SDP**，从而建出 `{00001124}` 节点并让 HidBth 绑定。

## 若重新配对后 C3 仍为 0 条

说明 SDP 本身在这块适配器上跑不起来（Realtek 8821CE 组合卡），转硬件方案：
USB 蓝牙 dongle（CSR8510 / RTL8761B）或换 Intel AX200/AX210。

## 给开发的两条硬要求

1. `commands.rs` 的 `convert_to_hid_compliant()` 必须删除或默认关闭 —— 它会把设备改成
   `HID-compliant device`、摘掉 `kbdhid`，按键直接死。
2. `remote_still_keyboard()` 的判据改为读 `Enum\BTHENUM\{00001124...}` 的 `Service`，
   不得读 `Enum\HID` 残留。
