using System.Text;
using System.Text.RegularExpressions;

namespace Iprc1000Adapter;

/// <summary>
/// **规则层润色**（2026-10-29）。
///
/// 定位：零依赖、零延迟（毫秒级）、可预测。只做"机械重复"类的清理，
/// 任何可能改变语义的操作都**不做**（不重写句子、不补主语、不改语序）。
///
/// 为什么保守：ASR 出来的文本一旦被"善意改写"，用户就失去对"我到底说了什么"的判断依据。
/// 规则层只该去掉**没有信息量的东西**；真正要"把口语变书面语"，那是 LLM 层的事。
///
/// 处理项（按顺序）：
///   1. 填充词：嗯/呃/啊/唉/哦（仅当它们被逗号/句号包住或独立成段时）
///   2. 口头禅：那个/然后/就是/就是说/这个（仅当其后面紧跟标点，说明是插入语）
///   3. 连续重复字：我我我 → 我、就就就是 → 就是（限 2 字以上的同字连写）
///   4. 标点：连续标点归一、中英标点统一、去句首标点
///   5. 收尾：去掉末尾多余空格；若整句没有结束标点则补句号
/// </summary>
internal static class TextPolish
{
    // 只在这些"被标点包住"的位置删填充词，避免动到正文
    private static readonly string[] Fillers = { "嗯", "呃", "啊", "唉", "哦", "诶", "嘛" };

    // 口头禅：后面必须跟标点（说明是插入语，不是句子成分）
    private static readonly string[] Tics = { "那个", "然后", "就是说", "就是", "这个", "你看", "对吧" };

    public static string Apply(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var s = text.Trim();

        // 1) 填充词：句首 或 紧跟在标点后，且自身后面也是标点
        foreach (var f in Fillers)
        {
            s = Regex.Replace(s, $@"(^|[，。、；：！？,\s]){f}(?=[，。、；：！？,\s]|$)", "$1");
        }

        // 2) 口头禅：**后面紧跟标点**才算插入语（"然后我们"不会被删）
        foreach (var t in Tics)
        {
            s = Regex.Replace(s, $@"(^|[，。、；：！？,\s]){t}(?=[，。、；：！？,\s]|$)", "$1");
        }

        // 3) 同字连写 ≥3 次 → 1 次（"我我我" → "我"）；2 次不动（"看看""试试"是正常词）
        s = Regex.Replace(s, @"([\u4e00-\u9fa5])\1{2,}", "$1");

        // 4) 标点规整
        s = s.Replace(",", "，").Replace(";", "；").Replace(":", "：").Replace("?", "？").Replace("!", "！");
        s = Regex.Replace(s, @"[，、；：]{2,}", "，");           // 连续逗号类归一
        s = Regex.Replace(s, @"。{2,}", "。");
        s = Regex.Replace(s, @"([，。、；：！？])\s+", "$1");     // 标点后的空格
        s = Regex.Replace(s, @"^[，。、；：！？\s]+", "");         // 句首标点
        s = Regex.Replace(s, @"\s{2,}", " ");

        // 5) 收尾
        s = s.Trim();
        if (s.Length > 0 && !"。！？…".Contains(s[^1])) s += "。";

        return s;
    }
}

/// <summary>
/// **术语表 / 别名纠正**（2026-10-29 第一层）。
///
/// 文件：`%APPDATA%\IPRC1000 Adapter\asr\terms.txt`，一行一条：
///     正确词                  ← 仅列出（当前引擎不支持把词喂给模型做偏置）
///     正确词 = 错法1,错法2     ← 识别出现错法时替换成正确词
///
/// 为什么第一版用"错法替换"而不是"喂给 ASR 做偏置"：
///   前置偏置效果更好（让模型直接认对），但需要引擎支持热词/上下文参数
///   —— HaoLive 的 CLI 目前没有这个参数。所以先做**可控的事后纠正**，
///   等引擎支持偏置时再把 terms 接进去（那时这一层仍可保留，兜底）。
/// </summary>
internal static class Glossary
{
    public static string FilePath => Path.Combine(Env.ConfigDir, "asr", "terms.txt");

    private static DateTime _stamp = DateTime.MinValue;
    private static List<(string right, string[] wrong)> _rules = new();

    public static int Count => Load().Count;

    private static List<(string right, string[] wrong)> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return _rules = new List<(string, string[])>();
            var t = File.GetLastWriteTimeUtc(FilePath);
            if (t == _stamp && _rules.Count > 0) return _rules;
            _stamp = t;

            var list = new List<(string, string[])>();
            foreach (var raw in File.ReadAllLines(FilePath))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                var eq = line.IndexOf('=');
                if (eq < 0) continue;                                  // 只列不纠
                var right = line.Substring(0, eq).Trim();
                var wrong = line.Substring(eq + 1)
                                .Split(new[] { ',', '，', '|' }, StringSplitOptions.RemoveEmptyEntries)
                                .Select(x => x.Trim()).Where(x => x.Length > 0 && x != right).ToArray();
                if (right.Length > 0 && wrong.Length > 0) list.Add((right, wrong));
            }
            return _rules = list;
        }
        catch (Exception e)
        {
            Safe.Note("术语表读取失败：" + e.Message);
            return _rules;
        }
    }

    /// <summary>把识别结果里的"错法"替换成正确词。</summary>
    public static string Fix(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var rules = Load();
        if (rules.Count == 0) return text;
        var s = text;
        foreach (var (right, wrong) in rules)
            foreach (var w in wrong)
                if (s.Contains(w)) s = s.Replace(w, right);
        return s;
    }

    /// <summary>确保术语文件存在（首次给个带注释的空模板，别让用户猜格式）。</summary>
    public static void EnsureFile()
    {
        try
        {
            if (File.Exists(FilePath)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath,
                "# 术语表：一行一条\n" +
                "# 只纠正写法，用 = 分隔，左边是正确写法，右边是常见的错误识别（逗号分隔）：\n" +
                "#   IPRC1000 = IPRC一千,IP二C1000\n" +
                "#   WinUHid  = win u hid,温优海德\n" +
                "# 只列不纠也可以（当前版本还不会把这些词喂给识别模型做偏置）：\n" +
                "#   KDS\n",
                new UTF8Encoding(true));
        }
        catch { }
    }
}
