#!/usr/bin/env bash
# IPRC1000 · Linux HCI 探测 · 抓包编排
# 用法：bash run.sh [秒数]        默认按键阶段 90 秒
# 产出：/tmp/iprc1000_hci/capture.btsnoop + report.txt
set -euo pipefail

SECS="${1:-90}"
OUT="/tmp/iprc1000_hci"
mkdir -p "$OUT"
CAP="$OUT/capture.btsnoop"
REP="$OUT/report.txt"
HERE="$(cd "$(dirname "$0")" && pwd)"

say() { printf '\n=== %s ===\n' "$*"; }

command -v btmon >/dev/null || { echo "btmon missing. run: bash install.sh"; exit 1; }

sudo rfkill unblock bluetooth || true
sudo hciconfig hci0 up 2>/dev/null || true
hciconfig -a 2>/dev/null | head -6 || true

say "STEP 1/4  start HCI capture (btsnoop)"
rm -f "$CAP"
sudo btmon -w "$CAP" >/dev/null 2>&1 &
BTMON=$!
trap 'sudo kill $BTMON 2>/dev/null || true' EXIT
sleep 1
[ -s "$CAP" ] || { echo "capture file not growing; is bluetooth blocked?"; exit 1; }
echo "capturing to $CAP (pid $BTMON)"

say "STEP 2/4  pair & connect the remote"
cat <<'TXT'
现在会打开 bluetoothctl。请依次输入（# 后面是说明，不要输）：

  scan on                       # 等它出现 IPRC1000
  pair   <MAC>                  # 例如 pair 20:C0:47:0A:34:83
  trust  <MAC>
  connect <MAC>                 # 连上；连上后试一下方向键/OK 是否有反应
  exit

配对键：长按遥控器右上角 PWR，等蓝灯闪烁。
恢复出厂配对：同时按 last + 下方向键。
TXT
bluetoothctl || true

say "STEP 3/4  press keys for ${SECS}s"
cat <<TXT
从现在起 ${SECS} 秒内，请把遥控器 23 个键**每个都按 3~5 次**，
并**长按语音键说话 10 秒**（这条最重要——要抓麦克风链路建立过程）。
看见下面这个倒计时归零再停。
TXT
for ((i=SECS; i>0; i--)); do printf '\r  %3ds left ...' "$i"; sleep 1; done
printf '\r  done.            \n'

say "STEP 4/4  stop capture + analyse"
sudo kill $BTMON 2>/dev/null || true
sleep 1
ls -l "$CAP"
python3 "$HERE/analyze.py" "$CAP" | tee "$REP"
cat <<TXT

报告已存：$REP
把这个文件发我（或 scp 回 Windows）：
  scp $REP <你的Windows用户名>@<WindowsIP>:/tmp/
TXT
