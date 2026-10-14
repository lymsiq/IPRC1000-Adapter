#!/bin/bash
# IPRC1000 SCO 激活探测（在 Linux 侧以 sudo 运行）
# 流程：抓包 → 建 ACL → 立刻发 HCI_Setup_Synchronous_Connection → 保持采集
MAC=20:C0:47:0A:34:83
VOICE=${1:-0060}

echo "===== 开始 $(date +%H:%M:%S)  voice_setting=0x$VOICE ====="

echo "[1] 启动 btmon 抓包"
sudo pkill btmon 2>/dev/null
sudo rm -f /tmp/sco_run.btsnoop
sudo nohup btmon -w /tmp/sco_run.btsnoop >/dev/null 2>&1 &
sleep 1.5

echo "[2] 建立 ACL（最多 4 次尝试）"
HD=""
for i in 1 2 3 4; do
  timeout 15 bluetoothctl connect "$MAC" >/dev/null 2>&1
  sleep 1
  HD=$(hcitool con 2>/dev/null | grep -i "34:83" | awk '{print $4}' | head -1)
  if [ -n "$HD" ]; then echo "    第 $i 次成功，handle=$HD"; break; fi
  echo "    第 $i 次未成，重试…"; sleep 3
done

if [ -z "$HD" ]; then
  echo "[!] ACL 未能建立 → 遥控器可能仍被其它主机占用"
  sudo pkill btmon
  exit 1
fi

L=$(printf "%02x" $((HD & 255)))
U=$(printf "%02x" $(((HD >> 8) & 255)))
echo "[3] 发送 HCI_Setup_Synchronous_Connection  handle=0x$(printf %04X "$HD")  字节 $L $U"
sudo hcitool cmd 0x01 0x0028 "$L" "$U" 00 00 00 00 00 00 00 00 0A 00 "$VOICE" 00 FF 00 00 2>&1 | sed 's/^/    /'
sleep 2

echo "[4] 连接列表（若出现 SCO 行说明同步链路已建立）"
hcitool con 2>&1 | sed 's/^/    /'

echo "[5] 保持 40 秒采集（请在此期间按住遥控器麦克风键说话）"
for i in $(seq 1 8); do
  sleep 5
  N=$(sudo ls -l /tmp/sco_run.btsnoop 2>/dev/null | awk '{print $5}')
  echo "    +$((i*5))s  抓包大小=$N"
done

echo "[6] 收尾"
sudo pkill btmon
ls -l /tmp/sco_run.btsnoop
echo "===== 结束 $(date +%H:%M:%S) ====="
