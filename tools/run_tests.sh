#!/usr/bin/env bash
# IPRC1000 发布前测试套件
#
# 用法：bash F:/vibe/tools/run_tests.sh
#
# 覆盖四层：
#   ① 映射一致性（注入 == 界面）  ② 出厂默认冻结（当前映射 == Defaults）
#   ③ 注入通路（WinUHid + SendInput）④ 单键注入（系统是否真的收到）
#
# 不含 `--selftest`（全量自检）：那条要开 WebView2 界面，
# **必须在有交互桌面的会话里手动跑**，自动化/无桌面环境下 WebView2 起不来。
set -u
EXE="F:/vibe/iprc1000_gui_dotnet/publish/IPRC1000 Adapter.exe"
OUT="F:/vibe/_test_out"
mkdir -p "$OUT"
pass=0; fail=0
chk() { if [ "$2" = "0" ]; then echo "  ✓ $1"; pass=$((pass+1)); else echo "  ✗ $1"; fail=$((fail+1)); fi; }

if [ ! -f "$EXE" ]; then echo "找不到 $EXE —— 先在 iprc1000_gui_dotnet 里 dotnet publish"; exit 1; fi

for gi in 0 1; do
  name=$([ "$gi" = "0" ] && echo "默认配置组 1" || echo "WorkBuddy 快捷键")
  echo "════════ 映射自检 · 第 $gi 组（$name）════════"
  "$EXE" --map-dump "$OUT/map$gi.txt" $gi >/dev/null 2>&1; sleep 2
  grep -q "注入 == 界面 ✓" "$OUT/map$gi.txt" 2>/dev/null; chk "注入 == 界面" $?
  grep -q "== 其出厂默认 ✓" "$OUT/map$gi.txt" 2>/dev/null; chk "当前映射 == 出厂默认（冻结）" $?
  grep -q "★不一致★" "$OUT/map$gi.txt" 2>/dev/null; [ $? -ne 0 ]; chk "无「不一致」行" $?
done

echo "════════ 注入自检（右 Alt = 语音键，双通路）════════"
"$EXE" --inject-test "$OUT/inject.json" >/dev/null 2>&1; sleep 4
if [ -f "$OUT/inject.json" ]; then
  python3 - "$OUT/inject.json" <<'PY'
import json,sys
d=json.load(open(sys.argv[1],encoding='utf-8'))
vh=d.get("virtualHid",{}); si=d.get("sendInput",{})
print(f"    目标键 {d.get('vk')} ({d.get('name')})")
print(f"    虚拟HID(WinUHid) ready={vh.get('ready')} 按下={vh.get('whileHeld')} 松开={vh.get('afterRelease')}"
      f" -> {'OK' if vh.get('ok') else 'FAIL'}")
print(f"    SendInput                        按下={si.get('whileHeld')} 松开={si.get('afterRelease')}"
      f" -> {'OK' if si.get('ok') else 'FAIL'}")
sys.exit(0 if (vh.get('ok') or si.get('ok')) else 1)
PY
  chk "右 Alt 注入生效（至少一条通路）" $?
else
  chk "注入自检产出报告" 1
fi

echo "════════ 单键注入（菜单键 0x5D / 右 Alt 0xA5 / Ctrl 0x11 / Alt 0x12）════════"
for k in 5D A5 11 12; do
  "$EXE" --inject-verify $k "$OUT/v$k.txt" >/dev/null 2>&1; sleep 2
  held=$(grep "按住中" "$OUT/v$k.txt" 2>/dev/null | grep -o "0x[0-9A-F]*" | tail -1)
  echo "    VK 0x$k  按住中 GetAsyncKeyState=$held"
  # 高位为 1 表示系统已收到（0x8000/0x8001 → 8 或 9 开头），故匹配 0x[89A-F]
  case "$held" in 0x[89ABCDEF]*) chk "VK 0x$k 系统已收到（高位=1）" 0;; *) chk "VK 0x$k 系统已收到（高位=1）" 1;; esac
done

echo
echo "════════════ 结果：通过 $pass · 失败 $fail ════════════"
[ "$fail" = "0" ] && echo "⇒ 全部通过 ✓" || echo "⇒ 有失败项 ✗"
echo "提示：全量自检请在交互桌面手动跑 ——"
echo "  & \"IPRC1000 Adapter.exe\" --selftest \"\$env:TEMP\\selftest.json\""
exit $fail
