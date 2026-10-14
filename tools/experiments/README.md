# 探索期脚本（experiments）

这里是从零摸清这台遥控器时写的一次性脚本，**不属于交付功能**，保留仅供追溯与复用。

它们记录了完整的逆向过程，遇到类似设备（蓝牙 HID / 双模遥控器 / BthPort 抓包）时可以照抄思路：

| 脚本 | 当时用来干什么 |
|---|---|
| `iprc1000_probe.py` | 枚举设备的 HID 接口与报告描述符 |
| `hid_capture.py` / `hid_capture2.py` | 直读 HID 报告，找"语音键"用的是哪个 usage |
| `raw_hid_test.py` | 测试能否绕过系统驱动直读原始报告（结论：键盘集合被系统禁止，见 README） |
| `btsnoop_analyze.py` / `btsnooz.py` | 解析 `btsnoop_hci.log`，从 HCI 层看设备发什么 |
| `hci_commands.py` / `le_adv_decode.py` | 解析 HCI 命令与 BLE 广播 |
| `sco_probe.py` / `sco_debug.py` / `audio_chain_check.py` | 摸 SCO/音频链路，确认麦克风声音从哪来 |
| `dump_payloads.py` / `live_read.py` | 把抓到的 HID 载荷 dump 出来逐字节比对 |
| `check_device_driver.py` / `pnp_watch.ps1` | 观察设备驱动绑定变化（`kbdhid` ↔ 普通 HID）|
| `watch_inject.py` / `test_device_spam.py` / `monitor_test.py` | 观察注入行为、复现"按键卡住一直输出 m" |
| `dual_capture.sh` / `adb.sh` / `msvc-env.cmd` | 抓包环境 / 安卓 / 编译环境的小工具 |

> 这些脚本依赖当时的具体环境（抓包文件、临时路径），**不保证可直接运行**。
> 正式交付用的工具在上一级 `tools/` 目录。
