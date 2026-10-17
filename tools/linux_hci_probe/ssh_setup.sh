#!/usr/bin/env bash
# 在 Linux 那台机器的终端里粘贴运行（整段一次贴进去）。
# 作用：装 SSH + 蓝牙工具 → 把 Windows 这边的公钥加进去 → 打印用户名和 IP。
set -euo pipefail

echo "==> 1/4 安装 openssh-server + 蓝牙工具"
sudo apt-get update -qq
sudo apt-get install -y -qq openssh-server bluez bluez-tools python3

echo "==> 2/4 启动 SSH"
sudo systemctl enable --now ssh

echo "==> 3/4 解封蓝牙并拉起适配器"
sudo rfkill unblock bluetooth || true
sudo hciconfig hci0 up 2>/dev/null || echo "（hci0 起不来，等下我远程看）"

echo "==> 4/4 注入公钥"
mkdir -p ~/.ssh && chmod 700 ~/.ssh
echo 'ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIK0VPXLC4Sta1lhu0vTYZxEbOKe6bus+HTdyb3XOJ+iQ workbuddy-iprc1000-probe' >> ~/.ssh/authorized_keys
chmod 600 ~/.ssh/authorized_keys

echo
echo "=========== 把下面两行发给 WorkBuddy ==========="
whoami
hostname -I | awk '{print $1}'
echo "==============================================="
echo
echo "用完想收回访问权，在 Linux 上运行："
echo "  sed -i '/workbuddy-iprc1000-probe/d' ~/.ssh/authorized_keys"
