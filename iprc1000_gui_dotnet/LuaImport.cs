using System.Text;

namespace Iprc1000Adapter;

/// <summary>
/// **罗技 G HUB / LGS Lua 脚本导入**（子集提取，不做完整 Lua 解释器）。
///
/// ── 为什么只做子集 ─────────────────────────────────────────────────────
/// 罗技的 Lua 是"事件驱动 + 命令式代码"，不是录制的序列：
/// <code>
/// function OnEvent(event, arg)
///     if event == "MOUSE_BUTTON_PRESSED" and arg == 5 then
///         PressAndReleaseMouseButton(1)
///         Sleep(80)
///     end
/// end
/// </code>
/// 真正流行的脚本有一大半是 `while IsMouseButtonPressed(5) do ...`（按住连发）
/// 或 `repeat ... until not IsMouseButtonPressed(1)`（压枪）—— 那是"按住期间持续循环"，
/// 我们没有这个模型，而且属于挂机/游戏场景 —— **明确不支持，且必须报错说明原因**，
/// 不能静默导入出一个行为完全不同的宏。
///
/// ── 能导入什么 ───────────────────────────────────────────────────────
/// 线性序列：`PressKey`/`ReleaseKey`（配对成组合键）、`PressAndReleaseKey`、
/// `PressAndReleaseMouseButton`（当前位置点击）、`Sleep`（等待）。
/// 外壳（`function OnEvent` 和**一层** `if event == ...`）会被剥掉，
/// 因为触发条件在我们这里是"遥控器某个键"，不是罗技的 G 键号。
///
/// ── 明确报错（不静默）─────────────────────────────────────────────────
/// `while` / `repeat` / `for` / 嵌套 `if` / 赋值 / 函数定义 / `PlayMacro`。
/// 报错必须带**行号**：静默丢掉一半语句比拒绝导入更糟。
/// </summary>
internal static class LuaImport
{
    public sealed class Result
    {
        public readonly List<Macros.Step> Steps = new();
        public readonly List<string> Warnings = new();
        public string Error = "";
        public string TriggerHint = "";        // 被剥掉的 if 条件（提示用户"触发条件已忽略"）
        public bool Ok => Error.Length == 0 && Steps.Count > 0;
    }

    /// <summary>带原始行号的源码行（剥壳后仍能报准行号）</summary>
    private readonly record struct Line(string Text, int No);

    public static Result Parse(string? lua)
    {
        var r = new Result();
        if (string.IsNullOrWhiteSpace(lua)) { r.Error = "脚本是空的"; return r; }

        // ★ 大小上限：`Parse` 在 UI 线程上同步跑（WebMessageReceived → Dispatch），
        //   几百 KB 的脚本会让窗口卡住。正常 Lua 脚本都是几 KB，512 KB 足够宽裕。
        const int MaxChars = 512 * 1024;
        if (lua.Length > MaxChars)
        {
            r.Error = $"脚本太大（{lua.Length / 1024} KB），上限 {MaxChars / 1024} KB —— "
                    + "这不是正常的宏脚本，请确认选错文件了没有。";
            return r;
        }

        var lines = StripComments(lua).Replace("\r\n", "\n").Replace('\r', '\n')
                                      .Split('\n')
                                      .Select((t, i) => new Line(t, i + 1))
                                      .Where(l => l.Text.Trim().Length > 0)
                                      .ToList();
        if (lines.Count == 0) { r.Error = "脚本里没有可执行语句（只有注释？）"; return r; }

        // ① 剥 OnEvent 外壳
        var body = StripOnEvent(lines, r);
        if (body is null) return r;                    // 错误已写入
        if (body.Count == 0) { r.Error = "OnEvent 里没有内容"; return r; }

        // ② 剥一层 if 触发条件（记录条件文本，提醒用户已被忽略）
        body = StripOneIf(body, r);

        // 官方文档：鼠标左键（arg == 1）**默认不上报**，要 EnablePrimaryMouseButtonEvents 才收得到。
        // 所以拿左键当触发条件的脚本，在原环境里可能本来就不触发 —— 提示一句，
        // 别让用户以为是导入错了。
        if (r.TriggerHint.Contains("arg == 1")
            && lua.IndexOf("EnablePrimaryMouseButtonEvents", StringComparison.Ordinal) < 0)
            r.Warnings.Add("触发条件用的是鼠标左键（arg == 1），而罗技默认**不上报**左键事件，"
                         + "这份脚本在 G HUB 里原本可能就不触发。已按遥控器触发导入。");

        // ③ 剩下的必须是平坦语句，出现任何块结构一律拒绝
        var bad = body.FirstOrDefault(l => HasBlockKeyword(l.Text));        if (!string.IsNullOrEmpty(bad.Text))
        {
            var kw = BlockKeywordOf(bad.Text);
            r.Error = $"第 {bad.No} 行有 `{kw}` 块结构 —— 这类脚本是「按住期间持续循环」"
                    + "（连点/压枪），本程序不支持导入。"
                    + (kw is "while" or "repeat" or "for" ? "建议改用「录制」重做一遍。" : "只支持线性序列。");
            return r;
        }

        // ④ 逐句翻译
        var pendingKeys = new List<int>();
        var combo = new List<int>();    // 按下顺序的完整组合（Ctrl down → C down ⇒ [Ctrl,C]）
        var pendingKeyAt = -1;
        var pendingHoldMs = 0;          // Press→Release 之间的 Sleep 累计 = 按住时长
        var pendingMouse = -1;

        foreach (var ln in body)
        {
            var t = ln.Text.Trim().TrimEnd(';').Trim();
            if (t.Length == 0) continue;

            var call = MatchCall(t);
            if (call is null)
            {
                r.Warnings.Add($"第 {ln.No} 行无法识别，已跳过：{Short(t)}");
                continue;
            }
            var (fn, args) = call.Value;

            switch (fn)
            {
                case "OutputLogMessage":
                case "OutputDebugMessage":
                case "ClearLog":
                case "EnablePrimaryMouseButtonEvents":
                    break;

                case "Sleep":
                    if (TryArg0(args, out var ms) && ms > 0)
                    {
                        // ★ 按住期间的 Sleep 不是"等待"，而是**按住多久**
                        //   （罗技脚本里 `PressKey / Sleep(50) / ReleaseKey` 就是"按住 50ms"）。
                        //   当成独立 wait 插进去会把顺序搞错：变成"先等 50ms 再按"。
                        if (pendingKeys.Count > 0) pendingHoldMs += ms;
                        else r.Steps.Add(new Macros.Step { Type = Macros.StepWait, Ms = Math.Clamp(ms, 0, 10000) });
                    }
                    else r.Warnings.Add($"第 {ln.No} 行 Sleep 参数不是数字，已跳过");
                    break;

                case "PressKey":
                case "PressAndReleaseKey":
                    {
                        var vks = ParseKeyArgs(args, r, ln.No);
                        if (vks.Count == 0) break;
                        if (fn == "PressAndReleaseKey")
                        {
                            r.Steps.Add(new Macros.Step { Type = Macros.StepKeys, Vks = vks.ToArray(), HoldMs = 30 });
                        }
                        else
                        {
                            foreach (var v in vks)
                            {
                                if (pendingKeys.Contains(v)) continue;
                                pendingKeys.Add(v);
                                combo.Add(v);        // 记下按下的顺序（Ctrl 先、C 后）
                            }
                            if (pendingKeyAt < 0) { pendingKeyAt = ln.No; pendingHoldMs = 0; }
                        }
                        break;
                    }

                case "ReleaseKey":
                    {
                        var vks = ParseKeyArgs(args, r, ln.No);
                        if (vks.Count == 0) break;
                        foreach (var v in vks) pendingKeys.Remove(v);
                        // ★ 只有**全部释放**时才闭合：Ctrl down → C down → C up → Ctrl up
                        //   是一个 "Ctrl+C"，中途就 emit 会多出一个单独的 Ctrl 步骤。
                        if (pendingKeys.Count == 0 && combo.Count > 0)
                        {
                            var hold = pendingHoldMs > 0 ? Math.Clamp(pendingHoldMs, 10, 2000) : 40;
                            r.Steps.Add(new Macros.Step
                            {
                                Type = Macros.StepKeys,
                                Vks = combo.OrderBy(v => IsModifier(v) ? 0 : 1).Distinct().ToArray(),
                                HoldMs = hold,
                            });
                            combo.Clear();
                            pendingKeyAt = -1;
                            pendingHoldMs = 0;
                        }
                        break;
                    }

                case "PressMouseButton":
                    if (TryArg0(args, out var pb)) pendingMouse = mb(pb);
                    break;

                case "ReleaseMouseButton":
                    if (pendingMouse >= 0)
                    {
                        r.Steps.Add(new Macros.Step
                        {
                            Type = Macros.StepClick,
                            Target = "cursor",           // 没有坐标 ⇒ 在当前光标位置点
                            Button = pendingMouse,
                        });
                        pendingMouse = -1;
                    }
                    else r.Warnings.Add($"第 {ln.No} 行 ReleaseMouseButton 前面没有对应的 Press，已跳过");
                    break;

                case "PressAndReleaseMouseButton":
                    if (TryArg0(args, out var pb2))
                        r.Steps.Add(new Macros.Step
                        {
                            Type = Macros.StepClick,
                            Target = "cursor",
                            Button = mb(pb2),
                        });
                    break;

                case "MoveMouseRelative":
                case "MoveMouseTo":
                case "MoveMouseWheel":
                case "MoveMouseToVirtual":
                    r.Warnings.Add($"第 {ln.No} 行 {fn} 已跳过：鼠标移动需要绝对坐标，"
                                 + "而脚本里的相对位移没有起点锚点。建议用「录制」重做这一步。");
                    break;

                case "PlayMacro":
                case "AbortMacro":
                case "SetMkeyState":
                    r.Warnings.Add($"第 {ln.No} 行 {fn} 是罗技专有操作，已跳过");
                    break;

                default:
                    r.Warnings.Add($"第 {ln.No} 行 {fn}() 不认识，已跳过：{Short(t)}");
                    break;
            }
        }

        // 收尾：还按着没松的键，补一个（否则导入出来的宏会"按着不放"）
        if (combo.Count > 0)
        {
            r.Steps.Add(new Macros.Step
            {
                Type = Macros.StepKeys,
                Vks = combo.OrderBy(v => IsModifier(v) ? 0 : 1).Distinct().ToArray(),
                HoldMs = 40,
            });
            r.Warnings.Add($"第 {pendingKeyAt} 行的 PressKey 没有对应的 ReleaseKey，已自动补上抬起");
        }

        if (r.Steps.Count == 0)
            r.Error = "没解析出任何可执行步骤" + (r.Warnings.Count > 0 ? "（可选项已跳过，见警告）" : "");
        return r;
    }

    private static int mb(int n) => n switch { 2 => 1, 3 => 2, _ => 0 };   // 罗技 2=右 3=中 → 我们 0=左 1=右 2=中
    private static bool IsModifier(int vk) => vk is 0x11 or 0x10 or 0x12 or 0x5B;

    private static string Short(string s) => s.Length > 42 ? s[..42] + "…" : s;

    // ── 注释与外壳 ────────────────────────────────────────────────────────

    private static string StripComments(string s)
    {
        // 先去掉块注释 --[[ ... ]] / --[==[ ... ]==]
        var sb = new StringBuilder();
        var i = 0;
        while (i < s.Length)
        {
            if (s[i] == '-' && i + 1 < s.Length && s[i + 1] == '-')
            {
                if (i + 3 < s.Length && s[i + 2] == '[' && s[i + 3] == '[')
                {
                    var end = s.IndexOf("]]", i + 4, StringComparison.Ordinal);
                    i = end < 0 ? s.Length : end + 2;
                    continue;
                }
                // 单行注释到行尾
                while (i < s.Length && s[i] != '\n') i++;
                continue;
            }
            // 字符串里的 -- 不算注释（简化处理：只认双引号，够用）
            if (s[i] == '"')
            {
                sb.Append(s[i++]);
                while (i < s.Length && s[i] != '"') { if (s[i] == '\\' && i + 1 < s.Length) sb.Append(s[i++]); sb.Append(s[i++]); }
                if (i < s.Length) sb.Append(s[i++]);
                continue;
            }
            sb.Append(s[i++]);
        }
        return sb.ToString();
    }

    private static List<Line>? StripOnEvent(List<Line> lines, Result r)
    {
        var start = lines.FindIndex(l => l.Text.Contains("function", StringComparison.Ordinal)
                                      && l.Text.Contains("OnEvent", StringComparison.Ordinal));
        if (start < 0) return lines;                  // 没有外壳：整份当语句体（可能是代码片段）

        var body = new List<Line>();
        var depth = 0;
        for (var i = start + 1; i < lines.Count; i++)
        {
            var t = lines[i].Text.Trim();
            if (IsBlockOpen(t)) depth++;
            if (t.StartsWith("end", StringComparison.Ordinal))
            {
                if (depth == 0) return body;          // OnEvent 的 end
                depth--;
            }
            body.Add(lines[i]);
        }
        r.Error = "OnEvent 函数没有闭合的 end（脚本被截断了？）";
        return null;
    }

    /// <summary>剥掉最外层的一层 if（触发条件），返回块内内容。没有 if 就原样返回。</summary>
    private static List<Line> StripOneIf(List<Line> body, Result r)
    {
        var start = body.FindIndex(l => l.Text.TrimStart().StartsWith("if ", StringComparison.Ordinal));
        if (start < 0) return body;

        var cond = body[start].Text.Trim();
        r.TriggerHint = cond;
        r.Warnings.Add($"已忽略触发条件（我们由遥控器按键触发）：{Short(cond)}");

        var inner = new List<Line>();
        var depth = 1;
        for (var i = start + 1; i < body.Count; i++)
        {
            var t = body[i].Text.Trim();
            if (IsBlockOpen(t)) depth++;
            if (t.StartsWith("end", StringComparison.Ordinal))
            {
                depth--;
                if (depth == 0) return inner;
            }
            inner.Add(body[i]);
        }
        return inner;
    }

    private static bool IsBlockOpen(string t) =>
        t.StartsWith("if ", StringComparison.Ordinal) || t.StartsWith("function ", StringComparison.Ordinal)
        || t.StartsWith("while ", StringComparison.Ordinal) || t.StartsWith("for ", StringComparison.Ordinal)
        || t == "repeat" || t.StartsWith("repeat ", StringComparison.Ordinal)
        || t.StartsWith("do", StringComparison.Ordinal);

    private static bool HasBlockKeyword(string t)
    {
        var s = t.TrimStart();
        return s.StartsWith("while ", StringComparison.Ordinal) || s == "while"
            || s.StartsWith("repeat", StringComparison.Ordinal)
            || s.StartsWith("for ", StringComparison.Ordinal)
            || s.StartsWith("if ", StringComparison.Ordinal)
            || s.StartsWith("function ", StringComparison.Ordinal)
            || s.StartsWith("local ", StringComparison.Ordinal)
            || (s.Contains('=') && !s.Contains("==") && !s.Contains(")") && !s.Contains("(\""));
    }

    private static string BlockKeywordOf(string t)
    {
        var s = t.TrimStart();
        foreach (var k in new[] { "while", "repeat", "for", "if", "function", "local" })
            if (s.StartsWith(k, StringComparison.Ordinal)) return k;
        return "赋值";
    }

    // ── 语句识别 ──────────────────────────────────────────────────────────

    /// <summary>识别 `Name(args)`，返回函数名和参数串。不是函数调用返回 null</summary>
    private static (string fn, string args)? MatchCall(string t)
    {
        var p = t.IndexOf('(');
        if (p <= 0 || !t.EndsWith(")", StringComparison.Ordinal)) return null;
        var fn = t[..p].Trim();
        if (fn.Length == 0) return null;
        foreach (var ch in fn) if (!char.IsLetterOrDigit(ch) && ch != '_' && ch != '.') return null;
        return (fn, t[(p + 1)..^1]);
    }

    private static bool TryArg0(string args, out int v)
    {
        v = 0;
        var s = args.Trim();
        return s.Length > 0 && s.All(char.IsDigit) && int.TryParse(s, out v);
    }

    /// <summary>解析键参数：多个参数可以是键名或 scancode</summary>
    private static List<int> ParseKeyArgs(string args, Result r, int lineNo)
    {
        var outp = new List<int>();
        foreach (var raw in SplitArgs(args))
        {
            var a = raw.Trim();
            if (a.Length == 0) continue;
            if (a.StartsWith('"') && a.EndsWith('"') && a.Length >= 2)
            {
                var name = a[1..^1];
                var vk = VkOfKeyName(name);
                if (vk > 0) outp.Add(vk);
                else r.Warnings.Add($"第 {lineNo} 行键名 \"{name}\" 不认识，已跳过");
            }
            else if (int.TryParse(a, out var sc) && sc > 0)
            {
                var vk = VkOfScancode(sc);
                if (vk > 0) outp.Add(vk);
                else r.Warnings.Add($"第 {lineNo} 行 scancode {sc} 不在已知表里，已跳过");
            }
            else r.Warnings.Add($"第 {lineNo} 行参数看不懂：{Short(a)}");
        }
        return outp.Distinct().ToList();
    }

    private static IEnumerable<string> SplitArgs(string s)
    {
        var cur = new StringBuilder();
        var inStr = false;
        foreach (var c in s)
        {
            if (c == '"') inStr = !inStr;
            if (c == ',' && !inStr) { yield return cur.ToString(); cur.Clear(); continue; }
            cur.Append(c);
        }
        if (cur.Length > 0) yield return cur.ToString();
    }

    // ── 键名 / scancode → 虚拟键码 ────────────────────────────────────────

    // ── 键名 → 虚拟键码（官方 Appendix A 的 keyname 列 + 常见别名）──────────
    //
    // ★ 2026-10-30：按《罗技 G series Lua API》Appendix A 逐个核对过。
    //   官方名优先（tilde / lbracket / rbracket / quote / spacebar / appkey / num0-9…），
    //   同时保留社区写法作别名（grave / backtick / space / leftbracket / apostrophe），
    //   因为大量流传的脚本用的是别名。

    internal static int VkOfKeyName(string name) => name.ToLowerInvariant() switch
    {
        // 字母
        "a" => 0x41, "b" => 0x42, "c" => 0x43, "d" => 0x44, "e" => 0x45, "f" => 0x46,
        "g" => 0x47, "h" => 0x48, "i" => 0x49, "j" => 0x4A, "k" => 0x4B, "l" => 0x4C,
        "m" => 0x4D, "n" => 0x4E, "o" => 0x4F, "p" => 0x50, "q" => 0x51, "r" => 0x52,
        "s" => 0x53, "t" => 0x54, "u" => 0x55, "v" => 0x56, "w" => 0x57, "x" => 0x58,
        "y" => 0x59, "z" => 0x5A,
        // 数字行
        "0" => 0x30, "1" => 0x31, "2" => 0x32, "3" => 0x33, "4" => 0x34,
        "5" => 0x35, "6" => 0x36, "7" => 0x37, "8" => 0x38, "9" => 0x39,
        // 官方名
        "escape" => 0x1B, "spacebar" => 0x20, "enter" => 0x0D, "tab" => 0x09,
        "backspace" => 0x08, "capslock" => 0x14, "tilde" => 0xC0,
        "minus" => 0xBD, "equal" => 0xBB, "comma" => 0xBC, "period" => 0xBE,
        "slash" => 0xBF, "semicolon" => 0xBA, "quote" => 0xDE, "backslash" => 0xDC,
        "lbracket" => 0xDB, "rbracket" => 0xDD, "non_us_slash" => 0xE2,
        "lshift" => 0x10, "rshift" => 0x10, "lctrl" => 0x11, "rctrl" => 0x11,
        "lalt" => 0x12, "ralt" => 0x12, "lgui" => 0x5B, "rgui" => 0x5C, "appkey" => 0x5D,
        // 导航区
        "up" => 0x26, "down" => 0x28, "left" => 0x25, "right" => 0x27,
        "home" => 0x24, "end" => 0x23, "insert" => 0x2D, "delete" => 0x2E,
        "pageup" => 0x21, "pagedown" => 0x22,
        "printscreen" => 0x2C, "scrolllock" => 0x91, "pause" => 0x13,
        // 小键盘
        "numlock" => 0x90, "numslash" => 0x6F, "nummultiply" or "numstar" => 0x6A,
        "numminus" => 0x6D, "numplus" => 0x6B, "numenter" => 0x0D, "numperiod" => 0x6E,
        "num0" => 0x60, "num1" => 0x61, "num2" => 0x62, "num3" => 0x63, "num4" => 0x64,
        "num5" => 0x65, "num6" => 0x66, "num7" => 0x67, "num8" => 0x68, "num9" => 0x69,
        // 社区别名（官方文档里不这么写，但流传的脚本大量在用）
        "esc" => 0x1B, "space" => 0x20, "return" => 0x0D, "del" => 0x2E,
        "grave" or "backtick" => 0xC0, "apostrophe" => 0xDE,
        "leftbracket" => 0xDB, "rightbracket" => 0xDD,
        "leftshift" => 0x10, "rightshift" => 0x10, "ctrl" => 0x11, "alt" => 0x12,
        "shift" => 0x10, "win" or "lwin" => 0x5B, "rwin" => 0x5C,
        "pgup" => 0x21, "pgdn" => 0x22, "arrowup" => 0x26, "arrowdown" => 0x28,
        "arrowleft" => 0x25, "arrowright" => 0x27,
        // 功能键
        "f1" => 0x70, "f2" => 0x71, "f3" => 0x72, "f4" => 0x73, "f5" => 0x74, "f6" => 0x75,
        "f7" => 0x76, "f8" => 0x77, "f9" => 0x78, "f10" => 0x79, "f11" => 0x7A, "f12" => 0x7B,
        "f13" => 0x7C, "f14" => 0x7D, "f15" => 0x7E, "f16" => 0x7F, "f17" => 0x80,
        "f18" => 0x81, "f19" => 0x82, "f20" => 0x83, "f21" => 0x84, "f22" => 0x85,
        "f23" => 0x86, "f24" => 0x87,
        _ => 0,
    };

    /// <summary>
    /// **scancode → 虚拟键码**，严格按官方 Appendix A（表里是十六进制，脚本里传十进制）。
    ///
    /// ⚠ 两段编号是**不连续**的，这是之前出错的地方：
    ///   · 基础键：1..88（escape=1、a=30、spacebar=57…）
    ///   · **扩展键：0x1xx**（方向键 0x148、Home 0x147、Delete 0x153、lgui 0x15b…）
    ///   小键盘 71..83 是 `num7/num8/num9/num4…`，**不是方向键** ——
    ///   曾经按"连续编号"推成方向键，结果导入带小键盘的脚本会按出方向键。
    /// </summary>
    internal static int VkOfScancode(int sc) => sc switch
    {
        // ── 基础键 ──
        0x01 => 0x1B,                                                    // escape
        0x02 => 0x31, 0x03 => 0x32, 0x04 => 0x33, 0x05 => 0x34, 0x06 => 0x35,
        0x07 => 0x36, 0x08 => 0x37, 0x09 => 0x38, 0x0a => 0x39, 0x0b => 0x30,
        0x0c => 0xBD, 0x0d => 0xBB, 0x0e => 0x08, 0x0f => 0x09,           // - = BS Tab
        0x10 => 0x51, 0x11 => 0x57, 0x12 => 0x45, 0x13 => 0x52, 0x14 => 0x54,
        0x15 => 0x59, 0x16 => 0x55, 0x17 => 0x49, 0x18 => 0x4F, 0x19 => 0x50,  // q..p
        0x1a => 0xDB, 0x1b => 0xDD, 0x1c => 0x0D, 0x1d => 0x11,           // [ ] Enter LCtrl
        0x1e => 0x41, 0x1f => 0x53, 0x20 => 0x44, 0x21 => 0x46, 0x22 => 0x47,
        0x23 => 0x48, 0x24 => 0x4A, 0x25 => 0x4B, 0x26 => 0x4C,           // a..l
        0x27 => 0xBA, 0x28 => 0xDE, 0x29 => 0xC0, 0x2a => 0x10, 0x2b => 0xDC,  // ; ' ` LShift \
        0x2c => 0x5A, 0x2d => 0x58, 0x2e => 0x43, 0x2f => 0x56, 0x30 => 0x42,
        0x31 => 0x4E, 0x32 => 0x4D,                                        // z..m
        0x33 => 0xBC, 0x34 => 0xBE, 0x35 => 0xBF, 0x36 => 0x10, 0x38 => 0x12,
        0x39 => 0x20, 0x3a => 0x14,                                        // , . / RShift LAlt Space Caps
        0x3b => 0x70, 0x3c => 0x71, 0x3d => 0x72, 0x3e => 0x73, 0x3f => 0x74,
        0x40 => 0x75, 0x41 => 0x76, 0x42 => 0x77, 0x43 => 0x78, 0x44 => 0x79,   // F1..F10
        0x45 => 0x90, 0x46 => 0x91,                                        // NumLock ScrLock
        0x47 => 0x67, 0x48 => 0x68, 0x49 => 0x69, 0x4a => 0x6D,           // num7 8 9 -
        0x4b => 0x64, 0x4c => 0x65, 0x4d => 0x66, 0x4e => 0x6B,           // num4 5 6 +
        0x4f => 0x61, 0x50 => 0x62, 0x51 => 0x63, 0x52 => 0x60, 0x53 => 0x6E,  // num1 2 3 0 .
        0x56 => 0xE2,                                                      // non_us_slash
        0x57 => 0x7A, 0x58 => 0x7B,                                        // F11 F12
        0x64 => 0x7C, 0x65 => 0x7D, 0x66 => 0x7E, 0x67 => 0x7F, 0x68 => 0x80,
        0x69 => 0x81, 0x6a => 0x82, 0x6b => 0x83, 0x6c => 0x84, 0x6d => 0x85,
        0x6e => 0x86, 0x76 => 0x87,                                        // F13..F24

        // ── 扩展键（0x1xx）—— ★ 这一段以前整段缺失 ──
        0x11c => 0x0D,                                                     // numenter
        0x11d => 0xA3,                                                     // rctrl
        0x135 => 0x6F,                                                     // numslash
        0x137 => 0x2C,                                                     // printscreen
        0x138 => 0xA5,                                                     // ralt
        0x146 => 0x13,                                                     // pause
        0x147 => 0x24, 0x148 => 0x26, 0x149 => 0x21,                       // home up pgup
        0x14b => 0x25, 0x14d => 0x27,                                      // left right
        0x14f => 0x23, 0x150 => 0x28, 0x151 => 0x22,                       // end down pgdn
        0x152 => 0x2D, 0x153 => 0x2E,                                      // insert delete
        0x15b => 0x5B, 0x15c => 0x5C, 0x15d => 0x5D,                       // lgui rgui appkey
        _ => 0,
    };
}
