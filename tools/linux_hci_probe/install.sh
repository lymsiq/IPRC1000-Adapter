#!/usr/bin/env bash
# IPRC1000 · Linux HCI 探测 · 一键安装
# 作用：装好 SSH + 蓝牙抓包工具，并检查适配器是否就绪。
# 用法：bash install.sh        （会提示输入 sudo 密码）
set -euo pipefail

say() { printf '\n=== %s ===\n' "$*"; }

say "1/5 apt update"
sudo apt-get update -y

say "2/5 install packages"
# bluez: 蓝牙协议栈（btmon 就在里面）；bluez-tools: 旧版工具；python3: 分析器
sudo apt-get install -y bluez bluez-tools python3

say "3/5 enable bluetooth + ssh"
sudo systemctl enable --now bluetooth
# SSH 可选：装上方便从 Windows 远程看结果；不想开就注释掉下面两行
sudo apt-get install -y openssh-server
sudo systemctl enable --now ssh

say "4/5 unblock radio + bring adapter up"
sudo rfkill unblock bluetooth || true
sudo hciconfig hci0 up 2>/dev/null || true

say "5/5 checks"
printf 'btmon      : '; btmon --version 2>/dev/null | head -1 || echo "MISSING"
printf 'ssh listen : '; ss -tlnp 2>/dev/null | grep -q ':22 ' && echo OK || echo "not listening"
echo "--- adapters ---"
hciconfig -a 2>/dev/null | head -8 || echo "no hci config output"
echo "--- this machine IP ---"
ip -4 addr show | grep -o 'inet [0-9.]*' | grep -v '127.0.0.1' || true

cat <<'TXT'

下一步：
  1) 把遥控器靠近这台机器
  2) 运行：  bash run.sh
  3) 按脚本提示配对、按键，等它自己出报告
TXT
