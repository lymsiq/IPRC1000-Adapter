#!/bin/bash
# ADB 无线调试辅助：每次自动启动服务并重连（避免调用间连接丢失）
# 用法: bash tools/adb.sh <adb 子命令与参数...>
# 例:   bash tools/adb.sh shell getprop ro.build.version.release
#       bash tools/adb.sh pull /sdcard/btsnoop_hci.log F:/vibe/

ADB="/c/Users/Administrator/AppData/Local/Android/Sdk/platform-tools/adb.exe"
TARGET="${ADB_TARGET:-192.168.8.252:44001}"

"$ADB" start-server >/dev/null 2>&1
STATE=$("$ADB" -s "$TARGET" get-state 2>/dev/null)
if [ "$STATE" != "device" ]; then
  "$ADB" connect "$TARGET" >/dev/null 2>&1
  sleep 1
fi

"$ADB" -s "$TARGET" "$@"
