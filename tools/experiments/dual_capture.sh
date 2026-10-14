#!/usr/bin/env bash
# 双通道采集：hidraw 读取 + btmon 原始抓包，用于区分「流没发生」与「hidraw 丢弃」
set -u
DEV=/dev/hidraw1
SECS=${1:-30}

echo "===== 双通道采集开始 $(date +%H:%M:%S)  时长 ${SECS}s ====="

# 确保连接
hcitool con 2>/dev/null | grep -q 34:83 || timeout 15 bluetoothctl connect 20:C0:47:0A:34:83 >/dev/null 2>&1
sleep 1
echo "[i] 连接状态: $(hcitool con 2>/dev/null | grep -c 34:83) 条 ACL"

# 通道 A：btmon 原始抓包
sudo pkill btmon 2>/dev/null
sudo rm -f /tmp/dual.btsnoop
sudo nohup btmon -w /tmp/dual.btsnoop >/dev/null 2>&1 &
sleep 1

# 通道 B：hidraw 读取（统计 report id）
sudo python3 - "$DEV" "$SECS" <<'PY' &
import collections, sys, time
dev, secs = sys.argv[1], float(sys.argv[2])
counts = collections.Counter()
sizes = collections.Counter()
f = open(dev, "rb", buffering=0)
t0 = time.time()
while time.time() - t0 < secs:
    try:
        d = f.read(2048)
    except OSError as e:
        print(f"[hidraw] 读取错误 {e}")
        break
    if d:
        counts[d[0]] += 1
        if d[0] == 0xF7:
            sizes[len(d)] += 1
f.close()
print(f"[hidraw] 统计 report_id->次数: {dict(counts)}")
print(f"[hidraw] F7 长度分布: {dict(sizes)}")
PY
HPID=$!

echo "[i] 采集中（请按住麦克风键说话）…"
for i in $(seq 1 "$SECS"); do
  sleep 1
  [ $((i % 10)) -eq 0 ] && echo "    +${i}s 抓包=$(sudo stat -c%s /tmp/dual.btsnoop 2>/dev/null)"
done

kill $HPID 2>/dev/null
sudo pkill btmon
sleep 1

echo "===== 通道 A：btmon 统计 ====="
sudo btmon -r /tmp/dual.btsnoop > /tmp/dual.txt 2>&1
echo "  总行数: $(wc -l < /tmp/dual.txt)"
echo "  ACL Data RX 包数: $(grep -c 'ACL Data RX' /tmp/dual.txt)"
echo "  CID 65 通道行数: $(grep -c 'Channel: 65' /tmp/dual.txt)"
echo "  F7 头部出现次数: $(grep -c 'a1 f7' /tmp/dual.txt 2>/dev/null || echo 0)"
echo "===== 通道 B：hidraw 结果见上方 ====="
