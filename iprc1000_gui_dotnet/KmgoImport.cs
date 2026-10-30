using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Iprc1000Adapter;

/// <summary>
/// **KeymouseGo 脚本导入**（`.txt`，Legacy 数组格式）。
///
/// ── 格式（源码 `Util/Parser.py` 的 LegacyParser 确认）────────────────────
/// 文件内容是一个数组，每行一个事件：
/// <code>
/// [113,"EM","mouse left up",[2996,206]],
/// [186,"EK","key down",[13,"Return",0]],
/// [132,"EK","key up",[13,"Return",0]],
/// [100,"EX","input","你好 world"],
/// </code>
/// 字段依次是 **延迟ms / 事件类型 / 动作 / 参数**，其中：
/// · `delay` 是**距上一个事件的间隔**（不是绝对时间戳）—— 要累加才是时间轴
/// · `EM` 参数 `[x, y]`：int = 绝对像素；float/`"0.052%"` = 屏幕比例；`[-1,-1]` = 原地不动
/// · `EK` 参数 `[vk, "名字", extended]`：**直接给虚拟键码**，不用查 scancode 表
/// · `EX` 只有 `input`：参数就是文本本身（原实现是复制到剪贴板再按 Ctrl+V）
///
/// ── 为什么能复用 <see cref="Recorder.BuildSteps"/> ─────────────────────
/// 这个格式的语义和"录制出来的原始事件流"完全一致（都是 按键/鼠标按下/抬起/移动 + 时间），
/// 所以先转成 <see cref="Recorder.RawEv"/>，再交给**已经过自检的**转换函数，
/// 组合键聚合、点击 vs 拖拽、间隔插 wait 全都不用重写 —— 少一份实现就少一处不一致。
///
/// ── 明确不支持 ────────────────────────────────────────────────────────
/// 新版 JSON5 的流程控制（`sequence` / `if` / `goto` / `label` / `subroutine`）
/// 以及滚轮：前者是"脚本语言"，后者我们没有对应步骤类型。**报错并给行号**，不静默丢。
/// </summary>
internal static class KmgoImport
{
    public sealed class Result
    {
        public readonly List<Macros.Step> Steps = new();
        public readonly List<string> Warnings = new();
        public string Error = "";
        public bool Ok => Error.Length == 0 && Steps.Count > 0;
    }

    /// <summary>`[113,"EM","mouse left up",[2996,206]],` 这种一行一条</summary>
    private static readonly Regex LineRe = new(
        @"^\s*\[\s*(-?\d+)\s*,\s*""([A-Za-z]{2})""\s*,\s*""([^""]*)""\s*,\s*(.+?)\s*\]\s*,?\s*$",
        RegexOptions.Compiled);

    /// <summary>
    /// 解析脚本。
    /// </summary>
    /// <param name="allocText">遇到 `EX input` 时用来"存一段文本并返回槽位号"；返回 0 表示存不下</param>
    public static Result Parse(string? txt, int screenW, int screenH, Func<string, int> allocText)
    {
        var r = new Result();
        if (string.IsNullOrWhiteSpace(txt)) { r.Error = "文件是空的"; return r; }

        // BOM 要去掉，否则第一行匹配不上（Windows 记事本存 UTF-8 默认带 BOM）
        txt = txt.TrimStart('\uFEFF');

        const int MaxChars = 4 * 1024 * 1024;
        if (txt.Length > MaxChars)
        {
            r.Error = $"脚本太大（{txt.Length / 1024} KB），上限 {MaxChars / 1024} KB";
            return r;
        }

        // 新版 JSON5 有 scripts 键 + 流程控制 —— 直接拒绝，别让人以为"导入成功了"
        if (Regex.IsMatch(txt, @"\bscripts\s*[=:]") || Regex.IsMatch(txt, @"""type""\s*:\s*""(if|goto|sequence|subroutine)"""))
        {
            r.Error = "这是 KeymouseGo 的**新版**脚本（带 scripts 与流程控制），本程序只支持旧版数组格式。\n"
                    + "在 KeymouseGo 里用旧版脚本，或把 action 序列单独整理成 `[延迟,\"EM\",\"动作\",参数],` 一行一条的形式。";
            return r;
        }

        // ── 逐行解析成原始事件 ──
        var evs = new List<Recorder.RawEv>();
        long t = 0;                       // 累计时间（delay 是间隔，要累加）
        var pendingText = new List<(int afterIdx, string text, int line)>();

        var lines = txt.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var parsed = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith("//") || line == "[" || line == "]") continue;

            var m = LineRe.Match(line);
            if (!m.Success)
            {
                // 带括号的行不像事件就报错；纯注释/空行上面已经放过
                if (line.Contains('[') && line.Contains('"'))
                    r.Warnings.Add($"第 {i + 1} 行格式不认识，已跳过：{Short(line)}");
                continue;
            }

            parsed++;
            var delay = int.TryParse(m.Groups[1].Value, out var d) ? Math.Clamp(d, 0, 3_600_000) : 0;
            var type = m.Groups[2].Value.ToUpperInvariant();
            var action = m.Groups[3].Value.Trim().ToLowerInvariant();
            var args = m.Groups[4].Value.Trim();
            t += delay;

            switch (type)
            {
                case "EM":
                    {
                        if (action.Contains("wheel"))
                        {
                            r.Warnings.Add($"第 {i + 1} 行是滚轮（{action}），本程序没有滚轮步骤，已跳过");
                            break;
                        }
                        if (!TryParsePoint(args, screenW, screenH, out var px, out var py))
                        {
                            r.Warnings.Add($"第 {i + 1} 行鼠标坐标看不懂，已跳过：{Short(args)}");
                            break;
                        }

                        var btn = action.Contains("right") ? 1 : action.Contains("middle") ? 2 : 0;
                        if (action.EndsWith("down"))
                            evs.Add(new Recorder.RawEv { Kind = "md", X = px, Y = py, T = t, Button = btn });
                        else if (action.EndsWith("up"))
                            evs.Add(new Recorder.RawEv { Kind = "mu", X = px, Y = py, T = t, Button = btn });
                        else if (action == "mouse move")
                            evs.Add(new Recorder.RawEv { Kind = "mm", X = px, Y = py, T = t });
                        else
                            r.Warnings.Add($"第 {i + 1} 行鼠标动作不认识（{action}），已跳过");
                        break;
                    }

                case "EK":
                    {
                        var ka = ParseKeyArgs(args);
                        if (ka.vk <= 0)
                        {
                            r.Warnings.Add($"第 {i + 1} 行键码看不懂，已跳过：{Short(args)}");
                            break;
                        }
                        // extended 标记在我们这边不需要：注入用的是 VK，系统自己判断
                        var kind = action.StartsWith("key up") ? "ku" : "kd";
                        evs.Add(new Recorder.RawEv { Kind = kind, Vk = ka.vk, T = t });
                        break;
                    }

                case "EX":
                    // 文本要落到文本槽里，而槽位分配在 Backend（这里不碰存储）。
                    // 记下"它出现在第几个事件之后"，等 BuildSteps 跑完再按位置插入。
                    if (action == "input")
                    {
                        var text = Unquote(args);
                        pendingText.Add((evs.Count, text, i + 1));
                        t += 0;      // 文本本身不占时间
                    }
                    else r.Warnings.Add($"第 {i + 1} 行 EX 动作不认识（{action}），已跳过");
                    break;

                default:
                    r.Warnings.Add($"第 {i + 1} 行事件类型「{type}」不认识，已跳过");
                    break;
            }
        }

        if (parsed == 0)
        {
            r.Error = "一行事件都没解析出来 —— 确认这是 KeymouseGo 的 .txt 脚本（每行形如 `[113,\"EM\",\"mouse left up\",[2996,206]],`）";
            return r;
        }

        // ── 事件 → 步骤：复用录制器的转换（组合键聚合 / 点击与拖拽判定 / 插 wait）──
        //    分段处理：EX 文本会把事件流切断，因为 BuildSteps 不认文本
        var cuts = pendingText.Select(p => p.afterIdx).Distinct().OrderBy(x => x).ToList();
        var prev = 0;
        foreach (var cut in cuts.Append(evs.Count))
        {
            if (cut > prev)
            {
                var seg = evs.GetRange(prev, cut - prev);
                if (seg.Count > 0) r.Steps.AddRange(Recorder.BuildSteps(seg, screenW, screenH));
            }
            prev = cut;

            // 这个位置上的文本（可能有多条）
            foreach (var pt in pendingText.Where(p => p.afterIdx == cut))
            {
                if (string.IsNullOrEmpty(pt.text)) continue;
                var slot = allocText(pt.text);
                if (slot > 0)
                    r.Steps.Add(new Macros.Step { Type = Macros.StepPaste, Slot = slot });
                else
                    r.Warnings.Add($"第 {pt.line} 行的文本没有空余槽位可放（1–10 已用完），已跳过");
            }
        }

        if (r.Steps.Count == 0) r.Error = "解析出的事件里没有可执行的动作（可能全是滚轮/不支持的项，见警告）";
        return r;
    }

    // ── 参数解析 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 坐标。KeymouseGo 允许三种写法：
    /// `[2996,206]` 绝对像素 / `[0.52,0.31]` 屏幕比例 / `["0.05208%","0.1852%"]` 旧版百分比。
    /// `[-1,-1]` 表示"原地不动"，调用方按 0,0 处理前应先判掉。
    /// </summary>
    private static bool TryParsePoint(string args, int sw, int sh, out int x, out int y)
    {
        x = y = 0;
        var s = args.Trim();
        if (!s.StartsWith('[') || !s.EndsWith(']')) return false;
        var body = s[1..^1];
        var parts = SplitTop(body);
        if (parts.Count != 2) return false;

        if (!TryScalar(parts[0], out var a, out var aIsRatio)) return false;
        if (!TryScalar(parts[1], out var b, out var bIsRatio)) return false;

        // [-1,-1] = 保持原位：交给调用方判断，这里原样返回 -1
        if (a == -1 && b == -1) { x = y = -1; return true; }

        // 比例型（float 或带 % ）→ 乘屏幕尺寸；int → 绝对像素
        x = aIsRatio ? (int)Math.Round(a * Math.Max(sw, 1)) : (int)Math.Round(a);
        y = bIsRatio ? (int)Math.Round(b * Math.Max(sh, 1)) : (int)Math.Round(b);
        return true;
    }

    private static bool TryScalar(string raw, out double val, out bool isRatio)
    {
        val = 0;
        isRatio = false;
        var s = raw.Trim();
        if (s.StartsWith('"') && s.EndsWith('"') && s.Length >= 2) s = s[1..^1].Trim();

        var pct = s.EndsWith('%');
        if (pct) s = s[..^1].Trim();

        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out val)) return false;

        // 带 % 或落在 0..1 之间的小数 = 比例；整数 = 绝对像素
        isRatio = pct || (Math.Abs(val) <= 1.0 && s.Contains('.'));
        return true;
    }

    private static (int vk, int extended) ParseKeyArgs(string args)
    {
        var s = args.Trim();
        if (!s.StartsWith('[') || !s.EndsWith(']')) return (0, 0);
        var parts = SplitTop(s[1..^1]);
        if (parts.Count == 0) return (0, 0);

        var vk = 0;
        if (int.TryParse(parts[0].Trim(), out var v)) vk = v;
        var ext = parts.Count >= 3 && int.TryParse(parts[2].Trim(), out var e) ? e : 0;
        return (vk is > 0 and <= 0xFF ? vk : 0, ext);
    }

    private static string Unquote(string args)
    {
        var s = args.Trim();
        if (s.Length >= 2 && s.StartsWith('"') && s.EndsWith('"'))
        {
            try { return JsonSerializer.Deserialize<string>(s) ?? ""; } catch { return s[1..^1]; }
        }
        return s;
    }

    /// <summary>按顶层逗号切（方括号/花括号/引号里的逗号不算）</summary>
    private static List<string> SplitTop(string s)
    {
        var outp = new List<string>();
        var depth = 0;
        var inStr = false;
        var cur = new System.Text.StringBuilder();
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (inStr)
            {
                if (c == '\\' && i + 1 < s.Length) { cur.Append(c).Append(s[++i]); continue; }
                if (c == '"') inStr = false;
                cur.Append(c);
                continue;
            }
            if (c == '"') { inStr = true; cur.Append(c); continue; }
            if (c is '[' or '{') depth++;
            if (c is ']' or '}') depth--;
            if (c == ',' && depth == 0) { outp.Add(cur.ToString()); cur.Clear(); continue; }
            cur.Append(c);
        }
        if (cur.Length > 0) outp.Add(cur.ToString());
        return outp;
    }

    private static string Short(string s) => s.Length > 40 ? s[..40] + "…" : s;
}
