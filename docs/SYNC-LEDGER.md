> ⚠ 历史快照（2026-11-06）：本文关于「改绑 HID 会导致接口消失/按键全断」「按键只走 Raw Input」「重新配对是唯一解」的结论已于 2026-11-07 更正。当前口径见 VERSION.md 与 IPRC1000_Native_用户手册.html：改绑可选（HID_Inst 接口照常注册）；按键主路径 ETW BthPort ACL；休眠后全断的真因是 Realtek 组合卡蓝牙链路断，处理是禁用其蓝牙换独立适配器。

# SYNC-LEDGER — 轮次台账

> 唯一对话记录。聊天里说的不算，以本文件为准。
> 每条记录格式：`轮次 | 时间 | 方向 | 状态 | 结论 | 证据路径 | 待验证`
> 状态定义见《开发-测试同步协议.md》第 4 节。

---

## Round 1 — 2026-11-06

### 方向
客户机 → 开发机（本轮为结论回传 + 工具统一）

### 状态
**S2 根因确认**（根因层已锁定，等一次重新配对即可升 S4）

### 一句话结论

> 遥控器、链路、加密全部正常；**主机侧 SDP 服务发现从未成功执行**，
> 导致 `{00001124}` HID 服务节点不存在 → `HidBth` 未绑定 → 主机 L2CAP 无 `PSM 0x0011` 监听者
> → 遥控器主动开 HID-Control 被 `REFUSED_PSM` → 无 HID 报告 → Raw Input 列表永远为空。

### 证据

**① 主机侧零服务记录（决定性）**

```
HKLM\SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices\20c047f313d6\ServicesForc0e350ad95db
  SSP Paired = 1
  SSP MITM Protected = 0
  SSP Supported = 1
  AuthenticationRequirements = 5
  RemoteAuthenticationRequirements = 0
  IoCapability = 3
  BasebandSupport = 16384
  BRFlags = 0
  BRExtendedDeviceInfoFlags = 0
  → 以上全是"绑定元数据"，SDP 服务记录 0 条
```

配对成功（有元数据）但**服务发现没跑过**（无服务记录）—— 这是"绑上了但不知道对方能干什么"。

**② 服务节点树**

```
Enum\BTHENUM\Dev_20C047F313D6        Service = (空)
（无 {00001124-...} 子节点）
```

**③ HCI 实录（`tools\parse_hci.py` 输出，修正版）**

```
21:44:42.235  遥控器 Connection Request  from 20:C0:47:F3:13:D6  COD=0x20050C
21:44:42.235  HCI-CMD Accept Connection Request
21:44:42.414  Connection Complete   status=0x00 OK
21:44:42.509  Encryption Change     status=0x00 enable=1      ← 密钥正常
21:44:42.510    L2CAP ConnReq   PSM=HID-Control SCID=0x0040 ident=0x02
21:44:42.510    L2CAP ConnRsp   ... >> PENDING
21:44:57.516    L2CAP ConnRsp   ... >> REFUSED_PSM           ← 主机无监听者
21:44:57.677  Disconnection Complete reason=0x13 REMOTE_USER_TERMINATED
（每 ~16 秒循环一次）
```

### 本轮修正的三处偏差

| 项 | 旧说法 | 修正为 | 依据 |
|---|---|---|---|
| 断开原因码 | `0x01` | **`0x13` REMOTE_USER_TERMINATED** | 解析器位移错误：`params[3]`=`b[5]`，不是 `b[3]` |
| 假绿灯来源 | `Enum\HID` 有残留 | **本机 `Enum\HID` 无 A5C 残留** | `Get-ChildItem Enum\HID` 实测为空 |
| BD_ADDR | `0C:20:C0:47:F3:13` | **`20:C0:47:F3:13:D6`** | `b[2:8]` 反转，不是 `b[3:9]` |

→ 三处都指向同一件事：**必须用同一份解析器**。已统一到 `tools\parse_hci.py`。

### 关键推论：`--restore-keyboard` 对本故障无效

该命令作用于 `Enum\HID` 下的 HID 子节点；本机**根本不存在这个节点**，
没有可"恢复"的对象。同理 `ConfigFlags`、`hidserv` 也都不是原因（服务全 Running）。

**唯一可行修法**：删除配对 → 遥控器进配对模式 → 重新配对，强制 Windows 重跑 SDP。

### 待验证（下一步动作）

| # | 动作 | 期望 | 负责 |
|---|---|---|---|
| 1 | 设置 → 蓝牙 → 删除 `IPRC1000` | 配对记录消失 | 测试 |
| 2 | 遥控器进配对模式并重新配对 | 配对完成 | 测试 |
| 3 | 跑 `tools\diag-bt.ps1` | C2=`[OK] Service = HidBth`，C3=记录数>0 | 测试 |
| 4 | 按任意键 | 7 步判定链第 5-7 步 PASS | 双方 |

若第 3 步重新配对后**仍然 0 条服务记录**，则 SDP 本身建不起来，判定为适配器/固件层问题
（Realtek 8821CE 组合卡），转硬件方案（USB dongle CSR8510/RTL8761B 或换 AX200/AX210），
届时开 Round 2 记录。

### 本轮新增文件

| 文件 | 说明 |
|---|---|
| `tools\diag-bt.ps1` | 只读诊断，7 检查点，双机同跑 |
| `tools\parse_hci.py` | HCI/L2CAP 唯一解析器（修正 3 处位移） |
| `docs\开发-测试同步协议.md` | 本协议 v1 |
| `docs\SYNC-LEDGER.md` | 本台账 |
| `handoff\20261106-round1\01-diag-PC-20260728LQXO-*.txt` | 客户机诊断原始输出 |

---

## Round 2 — 2026-11-06 14:0x

### 方向
开发机 → 客户机（交付执行单 + 新构建）

### 状态
**S3 已修待验**（修法为操作层：重配强制重跑 SDP；软件侧判据已修）

### 交付物

| 项 | 路径 / 值 |
|---|---|
| 客户机执行单 | `handoff\20261106-round2\执行单-客户机.md` |
| 主程序 | `IPRC1000_Native_v0.4.41.zip` 4.02 MB · sha256 `cbd2f345f2fef5c16d362866ba6f92e06e30ef8e3cd794e845ad82f21bbc56fb` |
| 安装器 | `IPRC1000_Native_Setup_v0.4.41.exe` 8.28 MB · sha256 `3a99aedef14100ded83c3c983b48d440ccabb2e7c83b9e02de67936cca28f8a0` |
| exe | sha256 `eec3dcb1a3498d74…`（payload↔target 一致） |
| 项目留言（已 @丁浩） | `msg_79e8dab6b0424ba9920dc146cf1dbcf7` |
| 跟踪待办 | `rmxurh` · 进行中 · 指派丁浩 · 截止 2026-11-07 |

0.4.41 相对 0.4.40：`bt_hid_bound()` 三态判据（读 `Enum\BTHENUM\{00001124}` 的 `Service`，
不再读 `Enum\HID` 缓存 HardwareID）+ Raw Input 认领可观测（`--hid-probe`、`IPRC1000_RAWINPUT_ANY=1`、
未认领打印 VKey+设备名）。⚠ 0.4.40 包打于 13:24，**不含**判据修复（commit 1e49dc0 在 14:01），
客户机不要用 0.4.40 验判据。

### 待客户机验证

按执行单 §1–§5：重配后 `C2 = HidBth`、`C3 记录数 > 0`；
再装 0.4.41 看环境页绿 + `native.log` 有 `[KEY]`；7 步判定链全 PASS 才升 S4。
重配 2 次仍 0 条 → 执行单 §6 硬件分支（更新驱动 → 禁用启用适配器 → RTL8761B/CSR8510 dongle 或 AX200）。
