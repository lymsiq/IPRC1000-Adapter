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

## Round 2 — 待开始

触发条件：重新配对完成后，或开发侧交付新构建（需附 build hash）。
