#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
状态字段层级检查（防止"字段塞错对象"这类静默 bug）。

## 为什么需要它

`Backend.GetState()` 返回的是**嵌套 JSON**（顶层的 groups / stuckGuard / keysource …），
而界面（app.js）按 `state.xxx` 读**顶层**字段。

如果某个字段被误加到子对象里（例如 `["keepEventLog"]` 落进了 `["stuckGuard"] = { … }`），
编译器不会报错、`ui_probe`（用 demo 快照）也发现不了 ——
界面只会静默拿到 `undefined`，表现为：

  · 开关点了没用、每 2 秒被刷新强制重置（2026-10-27 真实发生）
  · 提示条永远不显示
  · 输入框永远显示默认值

## 判据

顶层字段的缩进是 12 个空格（与 `["autoRun"]` 同级），子对象内是 16+。

## 用法

    python tools/check_state_fields.py        # 退出码 1 = 发现不匹配

发现不匹配 ⇒ 把对应字段**移到顶层**，或改界面按 `state.父对象.字段` 读。
"""
import io
import os
import re
import sys


def main() -> int:
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    cs_path = os.path.join(root, "iprc1000_gui_dotnet", "Backend.cs")
    js_path = os.path.join(root, "iprc1000_gui_dotnet", "ui", "app.js")
    cs = io.open(cs_path, encoding="utf-8", errors="replace").read()
    js = io.open(js_path, encoding="utf-8", errors="replace").read()

    i = cs.find("public string GetState()")
    if i < 0:
        print("✗ 没找到 Backend.GetState() —— 方法被改名了？请同步更新本脚本")
        return 2
    j = cs.find("\n    private ", i + 10)
    body = cs[i:j if j > 0 else i + 25000]

    top, nested = set(), {}
    for ln in body.splitlines():
        m = re.match(r'^(\s*)\["([A-Za-z_]\w*)"\]\s*=', ln)
        if not m:
            continue
        ind, key = len(m.group(1)), m.group(2)
        if ind <= 12:
            top.add(key)
        else:
            nested.setdefault(key, ind)

    # 界面按顶层读的字段（state.xxx / s.xxx）
    used = set(re.findall(r"\b(?:s|state)\.([A-Za-z_]\w*)\b", js))
    bad = {k: v for k, v in nested.items() if k in used}

    print(f"顶层字段 {len(top)} 个 · 嵌套字段 {len(nested)} 个")
    if not bad:
        print("✓ 没有「界面按顶层读、实际却嵌套」的字段")
        return 0

    print(f"✗ 发现 {len(bad)} 个层级错位的字段（界面会取到 undefined）：")
    for k, ind in sorted(bad.items()):
        print(f"    {k:<24} 缩进 {ind}（顶层应为 12）")
    print("\n处理：把字段移到 GetState() 的顶层对象里，或改界面按 state.父对象.字段 读。")
    return 1


if __name__ == "__main__":
    sys.exit(main())
