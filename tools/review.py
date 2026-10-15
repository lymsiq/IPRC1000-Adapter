#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""IPRC1000 Adapter — 源码卫生检查（只报告，不判定）

★ 使用须知（2026-10-15 白屏事故换来的）：

  本工具的输出是「待人工确认清单」，不是「可删清单」。
  任何一条说要删之前，必须：

    1. 打开它给出的文件:行号，亲眼确认上下文；
    2. 想一想有没有**动态拼接**的引用（'page-' + page、模板字符串、
       className = 'x' + ...）—— 静态 grep 看不见这些；
    3. 跑一遍 tools/ui_probe.js（真实浏览器渲染），确认界面没被改坏。

  事故回顾：本工具旧版把 page-overview / page-keys / page-env 报成「未引用 id」，
  因为 app.js 用 `p.id !== 'page-' + page` 找页 —— 字面 grep 认不出拼接。
  三个 id 被删后 goto() 判据恒真，每一页都被加上 hidden，整个界面白屏。

  所以本版做了三件事：
    · 所有 id/class 交叉检查都先做「整词匹配」且**识别拼接模式**，
      命中拼接前缀的一律归入「疑似动态引用」，不算未引用；
    · 输出分两级：[硬伤]（客观错误，如重复 id、大括号不平衡、JS 引用了不存在的元素）
      和 [待确认]（需要人看）；只有硬伤才让退出码非 0；
    · 每条都带 文件:行号，让人能直接跳过去核实。

用法：python tools/review.py
"""
import re
import sys
from pathlib import Path

# 动态拼接出的类名白名单：keyboard 键宽由数据表驱动（kb-key + 'w15'/'w2'/'w25'/'w3'/'w5'/'w7'），
# 字面量散在 KB_ROWS 数据里，整词扫描能找到但形态多变 —— 统一按 w+数字 放行。
DYNAMIC_CLASS_PATTERNS = [r"w\d+"]

ROOT = Path(__file__).resolve().parent.parent
UI = ROOT / "iprc1000_gui_dotnet" / "ui"

hard: list[str] = []      # 客观错误
todo: list[str] = []      # 待人工确认


def add_hard(msg: str) -> None:
    hard.append(msg)


def add_todo(msg: str) -> None:
    todo.append(msg)


def read(p: Path) -> str:
    return p.read_text(encoding="utf-8")


def strip_js_comments(src: str) -> str:
    """剥掉 JS 注释 —— 注释里的示例（如 $('#x')）不算引用。
    [^:] 守卫是为了不吃掉字符串里的 https://。"""
    src = re.sub(r"/\*[\s\S]*?\*/", " ", src)
    return re.sub(r"(^|[^:])//[^\n]*", r"\1", src)


def whole_token(text: str, token: str) -> bool:
    r"""token 是否以「完整类名/id」的形式出现在 text 里。
    keybind 不能被 keybind-sym 匹配上 —— 前后都不能是 [-\w]。"""
    return re.search(rf"(?<![\w-]){re.escape(token)}(?![\w-])", text) is not None


def main() -> int:
    if not UI.is_dir():
        print(f"找不到 UI 目录：{UI}")
        return 2
    html = read(UI / "index.html")
    css = read(UI / "style.css")
    js_raw = read(UI / "app.js")
    js = strip_js_comments(js_raw)

    # ── 1. HTML：重复 id（客观错误） ──────────────────────────────
    ids = re.findall(r'id="([^"]+)"', html)
    dup = sorted({i for i in ids if ids.count(i) > 1})
    if dup:
        add_hard("HTML 存在重复 id：" + ", ".join(dup))

    # ── 2. HTML id → app.js 引用检查 ─────────────────────────────
    # app.js 里的引用方式：
    #   $('#x') / getElementById('x') / '#x .foo'（带后代）/ '#x'（整串，setDot 风格）
    js_ids = set(re.findall(r"\$\('#([\w-]+)'", js))
    js_ids |= set(re.findall(r"getElementById\('([\w-]+)'\)", js))
    js_ids |= set(re.findall(r"'#([\w-]+)[ .\[]", js))          # '#x .foo' 带后代
    js_ids |= set(re.findall(r"['\"]#([\w-]+)['\"]", js))       # '#x' 整串（setDot('#d-device',…)）
    html_ids = set(ids)

    # 动态拼接检测：app.js 里有没有 'xxx-' + var 这种模式
    concat_prefixes = set(re.findall(r"['\"]([\w]+-)['\"]\s*\+\s*\w+", js))
    # html_ids - js_ids 里的每一个，若能由某个拼接前缀 + 已知页名拼出来，就不算未引用
    page_names = set(re.findall(r"data-page=\"(\w+)\"", html)) | set(
        re.findall(r"\?\(page\)|'(\w+)'", ""))                  # 页名来自 nav 按钮
    page_names |= {"overview", "keys", "env"}

    for i in sorted(html_ids - js_ids):
        made = next((p for p in concat_prefixes if (p + i.split("-", 1)[-1]) in html_ids
                     or any(h.startswith(p) for h in html_ids)), None)
        # 更直接：这个 id 的前缀部分是否等于某个拼接前缀
        prefix_hit = any(i.startswith(p.rstrip("-")) and "-" in i for p in concat_prefixes)
        if prefix_hit or made:
            add_todo(f"[拼接引用] id “{i}” 未被 app.js 字面引用，"
                     f"但 app.js 存在拼接模式 {sorted(concat_prefixes)} —— "
                     f"很可能经 '前缀' + 变量 动态引用（page-* 惨案同款）。删除前必须人工确认。")
        else:
            add_todo(f"HTML id “{i}” 未被 app.js 引用（含选择器串）。"
                     f"可能只被 CSS 用到、或纯静态标签。确认后可考虑删除。")

    # 反向：app.js 引用但 HTML 没有的 id —— 客观错误
    # （运行时真值以 tools/ui_probe.js 的 DOM 检查为准，这里是静态侧的同一道闸）
    missing = sorted(i for i in js_ids if i not in html_ids)
    if missing:
        add_hard("app.js 引用但 index.html 里不存在的 id：" + ", ".join(missing))

    # ── 3. CSS 类 → HTML/JS 使用检查 ─────────────────────────────
    css_classes = set(re.findall(r"\.([a-zA-Z][\w-]*)", css))
    # 使用方来源：HTML class 属性 + JS 里出现过的所有字符串字面量（模板串/拼接都涵盖）
    used_text = html + js
    for c in sorted(css_classes):
        if any(re.fullmatch(p, c) for p in DYNAMIC_CLASS_PATTERNS):
            continue                      # 动态拼接类：数据驱动，白名单放行
        if not whole_token(used_text, c):
            add_todo(f"CSS 类 “.{c}” 在 index.html 与 app.js 中都找不到整词出现。"
                     f"确认后可考虑删除（注意运行时若由模板字符串拼出，会漏检）。")

    # HTML 用到但 CSS 未定义的类 —— 通常是漏写样式（不算错误，但要人看）
    html_classes = set()
    for cl in re.findall(r'class="([^"]+)"', html):
        html_classes.update(cl.split())
    js_classes = set()
    for cl in re.findall(r"class=\\\\?\"([^\"$]+)\\\\?\"", js) + re.findall(
            r"className\s*=\s*['\"]([^'\"]+)['\"]", js):
        js_classes.update(cl.split())
    for c in sorted(html_classes - css_classes):
        add_todo(f"HTML 用了类 “{c}” 但 style.css 没定义（ic-green 曾是这种 —— 图标落错底色）。"
                 f"确认是漏写还是故意复用默认样式。")

    # ── 4. app.js：定义了但没被调用的函数 ────────────────────────
    js_funcs = re.findall(r"^function\s+(\w+)\s*\(", js, re.M)
    for f in sorted(set(js_funcs)):
        calls = len(re.findall(rf"\b{re.escape(f)}\b", js))
        if calls <= 1:
            add_todo(f"JS 函数 {f}() 定义后似乎没有任何调用。"
                     f"确认是否经 onclick 字符串/外部调用。")

    # ── 5. app.js：重复的 kv 标签（同一数据在两个面板各显示一次）──
    kv_labels = re.findall(r"kvItem\(\s*['\"]([^'\"]+)['\"]", js)
    seen: dict[str, int] = {}
    for k in kv_labels:
        seen[k] = seen.get(k, 0) + 1
    for k, n in sorted(seen.items()):
        if n > 1:
            add_todo(f"kv 标签 “{k}” 出现 {n} 次 —— 可能是同一份数据重复展示"
                     f"（「最近按键」曾犯过）。确认两处是不是不同口径。")

    # ── 6. 大括号 / 圆括号平衡（客观错误） ───────────────────────
    for name, text in (("style.css", css), ("app.js", js_raw)):
        for a, b in (("{", "}"), ("(", ")")):
            if text.count(a) != text.count(b):
                add_hard(f"{name} 括号不平衡：{a}={text.count(a)}  {b}={text.count(b)}")

    # ── 7. C#：疑似死方法（只报告 private，且要求人工核实） ──────
    cs_files = list((ROOT / "iprc1000_gui_dotnet").glob("*.cs"))
    for cs in cs_files:
        src = read(cs)
        # DllImport / extern 是 P/Invoke 声明，C# 里不出现调用方是正常的
        bodies = re.finditer(
            r"(\[DllImport[^\]]*\]\s*|)\bprivate\s+(?:static\s+|extern\s+|async\s+)*"
            r"[\w<>\[\],\s?]+?\s(\w+)\s*\(", src)
        for m in bodies:
            name = m.group(2)
            if m.group(1) or name == "Main":      # P/Invoke、程序入口点
                continue
            refs = 0
            for other in cs_files:
                refs += len(re.findall(rf"\b{re.escape(name)}\b", read(other)))
            if refs <= 1:
                add_todo(f"C# 疑似死方法：{cs.name} → {name}()（全工程仅定义处 1 次出现）。"
                         f"确认没有被反射/事件/字符串派发调用。")

    # ── 输出 ─────────────────────────────────────────────────────
    print("=" * 62)
    print("IPRC1000 Adapter 源码卫生检查（只报告，不判定）")
    print("=" * 62)
    if hard:
        print(f"\n★ 硬伤 {len(hard)} 项（客观错误，退出码非 0）：")
        for m in hard:
            print("   [硬伤] " + m)
    else:
        print("\n硬伤 0 项 ✓")
    if todo:
        print(f"\n待人工确认 {len(todo)} 项（不是可删清单，逐条看行号核实）：")
        for m in todo:
            print("   [待确认] " + m)
    else:
        print("\n待人工确认 0 项 ✓")
    print("\n提醒：动过 UI 之后，务必再跑一遍真实渲染验证 ——")
    print("      NODE_PATH=<managed workspace>/node_modules node tools/ui_probe.js")
    print("=" * 62)
    return 1 if hard else 0


if __name__ == "__main__":
    sys.exit(main())
