# IPRC1000 · Linux HCI 探测套件

**要回答的问题**：这颗遥控器除了标准 HID 通道，还有没有"厂商私有通道"？
如果有，红外/蜂鸣器/GPIO 这些被固件锁住的能力才有可能从主机侧驱动。

> 前提认知：博通手册说 *"PCM 接口可由主机通过 vendor-specific HCI 命令调整"*。
> 但在一只遥控器里，BCM20734 自己就是控制器，那些厂商命令走的是**芯片内部链路**，
> 我们在主机侧**本来就看不到**。所以真正有意义的信号是下面两个，脚本也主要抓这两个：
> 1. **非标准 L2CAP PSM** —— 遥控器主动开一条私有通道（这是最直接的证据）
> 2. **只在按键/说话时出现的厂商 HCI 流量** —— 与 Intel 控制器自身的基线命令区分开

---

## 文件

| 文件 | 作用 |
|---|---|
| `install.sh` | 一键装依赖（bluez / btmon / python3）+ 开 SSH + 检查适配器 |
| `run.sh` | 抓包编排：起 btmon → 引导你配对 → 倒计时按键 → 停止 → 自动分析 |
| `analyze.py` | 解析 btsnoop：OGF 直方图、厂商命令、L2CAP CID/PSM、给出结论 |

---

## 怎么送到 Linux 机器上

系统刚重装、SSH 还没开，所以**第一次必须在 Linux 那台机器的终端上操作**：

```bash
# 方式一：U 盘 / 共享文件夹把整个 linux_hci_probe 目录拷过去，然后
cd ~/linux_hci_probe && bash install.sh

# 方式二：如果 Windows 能看到 Linux 的目录，直接拷贝后执行 install.sh
```

`install.sh` 会顺手把 SSH 打开——装完之后就能从 Windows 连过去看结果了：

```powershell
ssh <用户名>@192.168.8.178
```

---

## 跑探测

```bash
bash run.sh              # 默认按键阶段 90 秒
bash run.sh 120          # 想按慢一点就给它 120 秒
```

脚本会分四步走，你只需要配合两件事：

**第二步 · 配对**（脚本会自动打开 `bluetoothctl`）
```
scan on
pair  20:C0:47:0A:34:83      # 换成你遥控器的 MAC（遥控器左下角印着）
trust 20:C0:47:0A:34:83
connect 20:C0:47:0A:34:83
exit
```
配对方法：**长按遥控器右上角 PWR**，等蓝灯闪烁。
恢复出厂配对：同时按 `last` + `下方向键`。

**第三步 · 按键**（倒计时期间）
- 23 个键**每个按 3~5 次**
- **长按语音键说话 10 秒** ← 这条最重要，要抓麦克风链路建立的过程

---

## 报告怎么读

跑完会在 `/tmp/iprc1000_hci/` 下生成 `capture.btsnoop` 和 `report.txt`。

| 看到什么 | 说明什么 |
|---|---|
| `SCO packets : 0` | **音频链路没建起来** —— 说明配对/连接没成功，或说话时段没覆盖到，重跑 |
| `SCO packets > 0` | 音频链路通了，**麦克风这条已经拿到证据** |
| `PSM 0x00XX *** NON-STANDARD ***` | **遥控器开了私有通道** —— 重大发现，红外/固件可能有戏 |
| 只有 `PSM 0x0011 / 0x0013`（HID）+ `0x0001`（SDP） | 没有私有 L2CAP 通道 —— 与我们在 Windows 侧的结论一致 |
| 厂商命令 `0xFCxx` 一堆 | 先别激动，**BlueZ 自己就会发 Intel 厂商命令**；要看的是"只在按键时出现"的那批 |

**结论分支**：
- 出现非标准 PSM → 私有通道存在 → 值得继续挖（下一步：对着那个 PSM 抓 L2CAP 负载）
- 只有标准 PSM → **PC 侧这条路基本走到头了**，安心卖现成功能

---

## 把结果发我

```bash
# 在 Linux 上
scp /tmp/iprc1000_hci/report.txt <你的Windows用户名>@<WindowsIP>:/tmp/

# 或者直接在 SSH 里贴出来
cat /tmp/iprc1000_hci/report.txt
```

`report.txt` 是纯文本，直接粘聊天里也行。

---

## 排障

| 现象 | 处理 |
|---|---|
| `btmon missing` | 先 `bash install.sh` |
| `hciconfig -a` 没输出 | `rfkill list` 看是否 blocked；`sudo rfkill unblock all`；BIOS 里确认蓝牙没关 |
| 抓不到包（文件不涨） | 蓝牙被 rfkill 挡了，或适配器没 up：`sudo hciconfig hci0 up` |
| 配对不上 | 长按 PWR 到蓝灯闪；或 `last + 下方向键` 恢复出厂配对后再来 |
| 报告说 "no connection requests" | 说明没连上就结束了，重跑并在 bluetoothctl 里确认 connect 成功 |
