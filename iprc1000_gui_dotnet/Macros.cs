using System.Text.Json;

namespace Iprc1000Adapter;

/// <summary>
/// **统一的动作库**：文本组（固定 1–10 槽）+ 宏（多步序列）。
///
/// 形态：`%APPDATA%\IPRC1000 Adapter\macros.json`
/// <code>
/// {
///   "Ver": 1,
///   "Items": [
///     { "Num": 1,   "Kind": "text", "Name": "报价话术", "Text": "您好…" },
///     { "Num": 101, "Kind": "seq",  "Name": "全选复制", "Steps": [
///         { "Type": "keys",  "Vks": [17,65], "HoldMs": 30 },
///         { "Type": "wait",  "Ms": 80 },
///         { "Type": "paste", "Slot": 1 }
///     ] }
///   ]
/// }
/// </code>
///
/// ★ **为什么文本也放进来**：文本就是"只有一步 paste 的宏"。
///   一份存储 ⇒ 导出/导入一次带走；宏里能直接引用文本组（`{"Type":"paste","Slot":3}`）；
///   界面只是两个编辑视图，数据是同一个，不会出现"文本改了宏没改"。
///
/// ★ **绑定方式**：`keymap.json` 里那个键的值写成单个负数 `RefBase - Num`
///   （`RefBase = -1000` ⇒ 文本槽 1..10 = `-1001..-1010`，宏 = `-1101` 起）。
///   Num 全局唯一且**删除后不复用**，所以删了宏不会让别的键指向错的目标。
///   这条与 <see cref="KeyAct"/> 的鼠标动作哨兵（-1..-6）是两套区间，互不重叠。
/// </summary>
internal static class Macros
{
    /// <summary>文本槽数量（固定槽位：槽位为空 = 未设置）</summary>
    public const int TextSlots = 10;
    /// <summary>宏编号从这里开始分配（1..10 留给文本槽，避免两套东西撞号）</summary>
    public const int FirstMacroNum = 101;
    /// <summary>绑定值的基准：实际存的是 `RefBase - Num`</summary>
    public const int RefBase = -1000;

    public const string KindText = "text";
    public const string KindSeq = "seq";

    public const string StepKeys = "keys";
    public const string StepWait = "wait";
    public const string StepPaste = "paste";
    public const string StepClick = "click";
    /// <summary>把光标移到指定位置（不按键）</summary>
    public const string StepMove = "move";
    /// <summary>按下 → 移到终点 → 抬起（一步表达，避免中间状态被别的步骤插进来）</summary>
    public const string StepDrag = "drag";

    /// <summary>单条宏最多几步 —— 防止导入一个畸形文件把播放器拖死</summary>
    public const int MaxSteps = 50;
    /// <summary>单条文本上限（char）。8000 已远超任何"常用话术"</summary>
    public const int MaxTextLen = 8000;

    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Env.AppName);

    private static readonly string File_ = Path.Combine(Dir, "macros.json");

    private static readonly object Gate = new();
    private static Doc _s = null!;

    // ── 数据模型 ───────────────────────────────────────────────────────────

    public sealed class Step
    {
        /// <summary>keys / wait / paste / click / move / drag</summary>
        public string Type { get; set; } = StepWait;
        /// <summary>Type=keys：要按下的虚拟键码（组合键就是多个，如 [17,65] = Ctrl+A）</summary>
        public int[] Vks { get; set; } = Array.Empty<int>();
        /// <summary>Type=keys：按下后停留多久再抬起（毫秒）。太短目标程序会漏掉</summary>
        public int HoldMs { get; set; } = 30;
        /// <summary>Type=wait：等多久（毫秒）</summary>
        public int Ms { get; set; } = 80;
        /// <summary>Type=paste：引用哪个文本槽（1..10）</summary>
        public int Slot { get; set; } = 1;

        // ── 鼠标类步骤（2026-10-30 录制功能加入）──────────────────────────

        /// <summary>
        /// Type=click 的目标：`icon` = 找采集过的图标（**推荐**）；`coord` = 按坐标点（会点空，谨慎）。
        /// 默认 icon —— 坐标点击是"用户明确要求才用"的降级手段。
        /// </summary>
        public string Target { get; set; } = "icon";
        /// <summary>Type=click/move/drag 的屏幕坐标（物理像素）。drag 时这是**起点**</summary>
        public int X { get; set; }
        public int Y { get; set; }
        /// <summary>Type=drag 的**终点**（move 不用）。一步表达"按下→拖到→抬起"</summary>
        public int X2 { get; set; }
        public int Y2 { get; set; }
        /// <summary>录制时的屏幕尺寸。播放时按比例缩放，换分辨率/DPI 不至于整体偏移</summary>
        public int Sw { get; set; }
        public int Sh { get; set; }
        /// <summary>
        /// 录制时的前台窗口标题。播放前校验：**不在前台就停**，避免点到别的窗口上。
        /// 空串 = 不校验（手写的步骤没有这个信息）。
        /// </summary>
        public string WinTitle { get; set; } = "";
        /// <summary>click/drag 用哪个键：0 = 左 1 = 右 2 = 中</summary>
        public int Button { get; set; }

        // ── 可配置项（2026-10-30）─────────────────────────────────────────

        /// <summary>
        /// Type=paste 的粘贴键（VK 列表）。默认 `[0x11,0x56]` = Ctrl+V；
        /// 终端/部分 IDE 里是 Ctrl+Shift+V ⇒ `[0x11,0x10,0x56]`。
        /// 写死 Ctrl+V 的话，那些程序里会"跑完了但没内容"，属于最难查的一类。
        /// </summary>
        public int[] PasteKey { get; set; } = { 0x11, 0x56 };

        /// <summary>
        /// 这一步失败怎么办：`stop` = 整条宏停下（默认）；`skip` = 跳过继续。
        /// ★ 默认必须是 stop —— 静默跳过会产生"跑完了但结果不对"，比直接停更难查。
        /// </summary>
        public string OnFail { get; set; } = "stop";
    }

    public sealed class Item
    {
        /// <summary>全局唯一编号：文本槽 1..10，宏从 101 起</summary>
        public int Num { get; set; }
        /// <summary>text / seq</summary>
        public string Kind { get; set; } = KindSeq;
        public string Name { get; set; } = "";
        public string Text { get; set; } = "";
        public List<Step> Steps { get; set; } = new();
        /// <summary>
        /// 执行遍数：**1 = 触发一次（默认）**；N = 循环 N 遍；**0 = 一直循环，直到按任意遥控器键停止**。
        ///
        /// ★ 0 这个语义容易误设，所以界面必须把「0 = 一直循环」写在输入框旁边，
        ///   不能只靠用户猜。另外无限循环有**硬时长上限**（见 MacroPlayer），
        ///   否则用户忘了停就是一直占着注入层。
        /// </summary>
        public int Loop { get; set; } = 1;
    }

    private sealed class Doc
    {
        public int Ver { get; set; } = 1;
        public List<Item> Items { get; set; } = new();
    }

    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    static Macros()
    {
        try { Load(); } catch { _s = new Doc(); }
    }

    // ── 读 / 写 ────────────────────────────────────────────────────────────

    private static void Load()
    {
        try
        {
            if (File.Exists(File_))
            {
                var txt = File.ReadAllText(File_);
                // ★ 空文件/半截 JSON 都走 catch —— 和 Keymaps 一样：**保留原文件、不覆盖**，
                //   否则一次异常就把用户所有动作库清空了。
                var d = JsonSerializer.Deserialize<Doc>(txt, ReadOpts);
                if (d is { Items: not null })
                {
                    Sanitize(d);
                    _s = d;
                    return;
                }
            }
        }
        catch (Exception e)
        {
            Safe.Log(e, "读取 macros.json 失败，本次用空白动作库（文件保留，未覆盖）");
        }
        _s = new Doc();
    }

    /// <summary>
    /// 夹到合法范围 + 去掉非法条目。
    /// ★ 文件被人手改过也必须过这一道 —— 否则一个 `HoldMs = 999999` 会让播放器卡死。
    /// </summary>
    private static void Sanitize(Doc d)
    {
        var seen = new HashSet<int>();
        var keep = new List<Item>();

        foreach (var it in d.Items)
        {
            if (it is null) continue;
            it.Kind = (it.Kind ?? "").Trim().ToLowerInvariant();
            if (it.Kind != KindText && it.Kind != KindSeq) continue;

            if (it.Num <= 0) continue;
            if (!seen.Add(it.Num)) continue;                 // 编号重复：留第一个

            it.Name = (it.Name ?? "").Trim();
            if (it.Name.Length > 40) it.Name = it.Name[..40];

            if (it.Kind == KindText)
            {
                if (it.Num > TextSlots) continue;            // 文本槽只有 1..10
                it.Text ??= "";
                if (it.Text.Length > MaxTextLen) it.Text = it.Text[..MaxTextLen];
                it.Steps = new List<Step>();
            }
            else
            {
                if (it.Num <= TextSlots) continue;           // 宏编号从 101 起，不能占文本槽号
                it.Text = "";
                it.Steps ??= new List<Step>();
                if (it.Steps.Count > MaxSteps) it.Steps = it.Steps.Take(MaxSteps).ToList();
                foreach (var st in it.Steps) SanitizeStep(st);
                // ★ 负数（手改错/旧格式缺字段）**必须回落到 1**，不能夹到 0 ——
                //   0 是"一直循环"，把坏数据变成无限循环是最糟的失败方向。
                it.Loop = it.Loop < 0 ? 1 : Math.Min(it.Loop, 999);
                // 清洗后一个都不剩的宏直接丢掉 —— 留着它只会让用户"按了没反应"
                it.Steps.RemoveAll(st => st is null);
                if (it.Steps.Count == 0 && it.Kind == KindSeq && string.IsNullOrEmpty(it.Name))
                    continue;
            }

            if (it.Name.Length == 0)
                it.Name = it.Kind == KindText ? $"文本 {it.Num}" : $"宏 {it.Num}";
            keep.Add(it);
        }

        d.Items = keep;
        d.Ver = 1;
    }

    private static void SanitizeStep(Step? st)
    {
        if (st is null) return;
        st.Type = (st.Type ?? "").Trim().ToLowerInvariant();
        st.HoldMs = Math.Clamp(st.HoldMs, 10, 2000);
        st.Ms = Math.Clamp(st.Ms, 0, 10000);
        st.Slot = Math.Clamp(st.Slot, 1, TextSlots);

        // 鼠标类：白名单式取值，认不出来的一律回默认（不是"保留原值"——
        // 一个手改的 "COORD" 或空串会让播放器走进未定义分支）
        st.Target = (st.Target ?? "").Trim().ToLowerInvariant() == "coord" ? "coord" : "icon";
        st.OnFail = (st.OnFail ?? "").Trim().ToLowerInvariant() == "skip" ? "skip" : "stop";
        st.Button = Math.Clamp(st.Button, 0, 2);
        st.X = Math.Clamp(st.X, -32768, 32767);
        st.Y = Math.Clamp(st.Y, -32768, 32767);
        st.X2 = Math.Clamp(st.X2, -32768, 32767);
        st.Y2 = Math.Clamp(st.Y2, -32768, 32767);
        st.Sw = Math.Clamp(st.Sw, 0, 20000);
        st.Sh = Math.Clamp(st.Sh, 0, 20000);
        st.WinTitle = (st.WinTitle ?? "").Trim();
        if (st.WinTitle.Length > 80) st.WinTitle = st.WinTitle[..80];

        st.PasteKey ??= new[] { 0x11, 0x56 };
        st.PasteKey = st.PasteKey.Where(v => v > 0 && v <= 0xFF).Distinct().Take(3).ToArray();
        if (st.PasteKey.Length == 0) st.PasteKey = new[] { 0x11, 0x56 };

        st.Vks ??= Array.Empty<int>();
        if (st.Type != StepKeys) return;
        // 只认真实虚拟键码：负数（鼠标动作）在宏里一律不收 —— 宏是"键盘序列"，混进鼠标动作
        // 会让播放器的"整份报告"语义和鼠标注入互相干扰，属于自找麻烦。
        st.Vks = st.Vks.Where(v => v > 0 && v <= 0xFF).Distinct().Take(4).ToArray();
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var json = JsonSerializer.Serialize(_s, WriteOpts);
            var tmp = File_ + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(File_)) File.Replace(tmp, File_, File_ + ".bak", true);
            else File.Move(tmp, File_);
        }
        catch (Exception e)
        {
            Safe.Log(e, "写入 macros.json 失败（本次修改只存在内存里）");
        }
    }

    // ── 查询 ───────────────────────────────────────────────────────────────

    public static List<Item> All { get { lock (Gate) return _s.Items.Select(Clone).ToList(); } }

    public static bool IsRef(int vk) => vk < RefBase && vk >= RefBase - 999;

    public static int RefOf(int num) => RefBase - num;

    /// <summary>绑定值 → 目标条目。不是宏引用 / 引用的条目已删 → null</summary>
    public static Item? FindByRef(int vk)
    {
        if (!IsRef(vk)) return null;
        var num = RefBase - vk;
        lock (Gate) return _s.Items.FirstOrDefault(x => x.Num == num) is { } it ? Clone(it) : null;
    }

    public static Item? FindByNum(int num)
    {
        lock (Gate) return _s.Items.FirstOrDefault(x => x.Num == num) is { } it ? Clone(it) : null;
    }

    /// <summary>文本槽内容（未设置返回空串）</summary>
    public static string TextOfSlot(int slot)
    {
        lock (Gate)
            return _s.Items.FirstOrDefault(x => x.Kind == KindText && x.Num == slot)?.Text ?? "";
    }

    /// <summary>
    /// 宏里**所有可能出现**的虚拟键码 —— 给 <see cref="KeyFilter"/> 的孤儿键回收用。
    /// ★ 漏了这个的后果：宏发出去的键不在"可能注入集合"里，看门狗就不认它，
    ///   一旦宏中途崩溃，那个键会**永久按住**（用户只能拔键盘）。
    /// </summary>
    public static int[] AllInjectableVks()
    {
        var set = new SortedSet<int>();
        lock (Gate)
        {
            foreach (var it in _s.Items)
            {
                if (it.Kind != KindSeq) continue;
                foreach (var st in it.Steps)
                {
                    if (st.Type != StepKeys) continue;
                    foreach (var v in st.Vks) set.Add(v);
                }
            }
        }
        return set.ToArray();
    }

    // ── 修改 ───────────────────────────────────────────────────────────────

    /// <summary>保存文本槽。slot 1..10；name 为空自动叫「文本 N」</summary>
    public static string SaveText(int slot, string name, string text)
    {
        if (slot < 1 || slot > TextSlots) return $"槽位必须在 1..{TextSlots}";
        if ((text ?? "").Length > MaxTextLen) return $"文本太长（上限 {MaxTextLen} 字符）";
        lock (Gate)
        {
            var it = _s.Items.FirstOrDefault(x => x.Kind == KindText && x.Num == slot);
            if (it is null)
            {
                it = new Item { Num = slot, Kind = KindText };
                _s.Items.Add(it);
            }
            it.Name = string.IsNullOrWhiteSpace(name) ? $"文本 {slot}" : name.Trim()[..Math.Min(name.Trim().Length, 40)];
            it.Text = text ?? "";
            Sanitize(_s);
            Save();
        }
        return TextOfSlot(slot).Length == 0 ? $"文本 {slot} 已清空" : $"文本 {slot} 已保存（{TextOfSlot(slot).Length} 字）";
    }

    /// <summary>保存宏。num = 0 表示新建（自动分配编号）</summary>
    public static string SaveMacro(int num, string name, List<Step> steps, int loop, out int savedNum)
    {
        savedNum = 0;
        steps ??= new List<Step>();
        if (steps.Count == 0) return "宏至少要有一" + "步";
        if (steps.Count > MaxSteps) return $"宏最多 {MaxSteps} 步";
        foreach (var st in steps) SanitizeStep(st);
        steps.RemoveAll(st => st is null);
        if (steps.Count == 0) return "宏至少要有一" + "步（清洗后为空）";

        lock (Gate)
        {
            Item? it;
            if (num <= 0)
            {
                var next = FirstMacroNum;
                if (_s.Items.Count > 0) next = Math.Max(FirstMacroNum, _s.Items.Max(x => x.Num) + 1);
                it = new Item { Num = next, Kind = KindSeq };
                _s.Items.Add(it);
            }
            else
            {
                it = _s.Items.FirstOrDefault(x => x.Num == num && x.Kind == KindSeq);
                if (it is null) return $"找不到编号 {num} 的宏";
            }

            it.Name = string.IsNullOrWhiteSpace(name) ? $"宏 {it.Num}" : name.Trim()[..Math.Min(name.Trim().Length, 40)];
            it.Steps = steps;
            it.Kind = KindSeq;
            it.Text = "";
            it.Loop = loop;
            Sanitize(_s);
            Save();
            savedNum = it.Num;
        }
        var loopTxt = LoopTextOf(savedNum);
        return $"宏已保存（{steps.Count} 步{loopTxt}，编号 {savedNum}）";
    }

    /// <summary>保存后的"循环遍数"描述。无限循环必须在提示里说出来，否则用户不知道自己在配什么</summary>
    private static string LoopTextOf(int num)
    {
        var it = FindByNum(num);
        if (it is null || it.Loop == 1) return "";
        return it.Loop == 0 ? "，一直循环" : $"，循环 {it.Loop} 遍";
    }

    public static string Delete(int num)
    {
        lock (Gate)
        {
            var it = _s.Items.FirstOrDefault(x => x.Num == num);
            if (it is null) return "找不到这一条";
            var isText = it.Kind == KindText;
            if (isText)
            {
                // ★ 清空文本槽前先数一下有多少宏在引用它 —— 那些宏会在播放时
                //   报「文本 N 是空的」，用户如果不知道有引用，会以为是宏坏了。
                var users = _s.Items.Count(x => x.Kind == KindSeq
                    && x.Steps.Any(s => s.Type == StepPaste && s.Slot == num));
                it.Text = "";
                it.Name = $"文本 {num}";
                Save();
                return users > 0
                    ? $"文本 {num} 已清空。注意：有 {users} 个宏引用了它，那些宏执行到粘贴时会失败。"
                    : $"文本 {num} 已清空";
            }
            _s.Items.Remove(it);
            Save();
            return $"宏 {num} 已删除";
        }
    }

    /// <summary>
    /// 找第一个能用的文本槽写入，返回槽号；1–10 全满返回 0。
    ///
    /// 给「导入 KeymouseGo 脚本」用：那边的 `EX input` 是**内联文本**，
    /// 而我们的 paste 步骤只认槽位号，所以导入时把文本落进空槽再引用。
    /// </summary>
    public static int AllocTextSlot(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        lock (Gate)
        {
            for (var n = 1; n <= TextSlots; n++)
            {
                var it = _s.Items.FirstOrDefault(x => x.Kind == KindText && x.Num == n);
                if (it is null)
                {
                    _s.Items.Add(new Item { Num = n, Kind = KindText, Name = $"文本 {n}", Text = text });
                    Save();
                    return n;
                }
                if (string.IsNullOrEmpty(it.Text))
                {
                    it.Text = text;
                    Save();
                    return n;
                }
            }
            return 0;
        }
    }

    // ── 给界面的 JSON ──────────────────────────────────────────────────────

    /// <summary>把字符串转义成可安全塞进 JSON 字面量的内容（不含首尾引号）</summary>
    private static string JsonEscape(string s)
        => string.IsNullOrEmpty(s) ? "" : JsonSerializer.Serialize(s).Trim('"');

    /// <summary>摘要（不带正文）—— 每 2 秒的状态轮询只拿这个，避免几 KB 文本反复过管道</summary>
    public static string StateJson()
    {
        var arr = new System.Text.StringBuilder();
        arr.Append('[');
        lock (Gate)
        {
            var first = true;
            foreach (var it in _s.Items.OrderBy(x => x.Num))
            {
                if (!first) arr.Append(',');
                first = false;
                arr.Append('{');
                arr.Append("\"num\":").Append(it.Num).Append(',');
                arr.Append("\"kind\":\"").Append(it.Kind).Append("\",");
                arr.Append("\"name\":\"").Append(JsonEscape(it.Name)).Append('"');
                if (it.Kind == KindText)
                    arr.Append(",\"len\":").Append(it.Text.Length);
                else
                    arr.Append(",\"steps\":").Append(it.Steps.Count).Append(",\"loop\":").Append(it.Loop);
                arr.Append('}');
            }
        }
        arr.Append(']');
        return arr.ToString();
    }

    /// <summary>单条全文（界面点开某条时才取）</summary>
    public static string ItemJson(int num)
    {
        var it = FindByNum(num);
        if (it is null) return "{}";
        return JsonSerializer.Serialize(new
        {
            num = it.Num,
            kind = it.Kind,
            name = it.Name,
            text = it.Text,
            loop = it.Loop,
            steps = it.Steps.Select(s => new
            {
                type = s.Type,
                vks = s.Vks,
                holdMs = s.HoldMs,
                ms = s.Ms,
                slot = s.Slot,
                target = s.Target,
                x = s.X,
                y = s.Y,
                x2 = s.X2,
                y2 = s.Y2,
                sw = s.Sw,
                sh = s.Sh,
                winTitle = s.WinTitle,
                button = s.Button,
                pasteKey = s.PasteKey,
                onFail = s.OnFail,
            }).ToArray(),
        }, WriteOpts);
    }

    /// <summary>步骤列表 → 界面直接能用的 JSON（camelCase，与 ItemJson 的 steps 字段一致）</summary>
    public static string StepsToJson(List<Step> steps)
    {
        var arr = (steps ?? new List<Step>()).Select(s => new
        {
            type = s.Type,
            vks = s.Vks,
            holdMs = s.HoldMs,
            ms = s.Ms,
            slot = s.Slot,
            target = s.Target,
            x = s.X,
            y = s.Y,
            x2 = s.X2,
            y2 = s.Y2,
            sw = s.Sw,
            sh = s.Sh,
            winTitle = s.WinTitle,
            button = s.Button,
            pasteKey = s.PasteKey,
            onFail = s.OnFail,
        }).ToArray();
        return JsonSerializer.Serialize(arr, WriteOpts);
    }

    private static Item Clone(Item it) => new()
    {
        Num = it.Num,
        Kind = it.Kind,
        Name = it.Name,
        Text = it.Text,
        Loop = it.Loop,
        Steps = it.Steps.Select(s => new Step
        {
            Type = s.Type,
            Vks = (int[])s.Vks.Clone(),
            HoldMs = s.HoldMs,
            Ms = s.Ms,
            Slot = s.Slot,
            Target = s.Target,
            X = s.X,
            Y = s.Y,
            X2 = s.X2,
            Y2 = s.Y2,
            Sw = s.Sw,
            Sh = s.Sh,
            WinTitle = s.WinTitle,
            Button = s.Button,
            PasteKey = (int[])s.PasteKey.Clone(),
            OnFail = s.OnFail,
        }).ToList(),
    };

    // ── 自检（纯逻辑，不注入任何东西）──────────────────────────────────────

    /// <summary>
    /// `--macro-test`：钉住几条**静默出错**的边界。
    /// 这些都是"不会崩、但用户按了没反应 / 按错东西"的类型，只能靠断言兜。
    /// </summary>
    public static string SelfTest()
    {
        var sb = new System.Text.StringBuilder();
        var ok = true;
        void Check(bool cond, string name, string detail)
        {
            if (!cond) ok = false;
            sb.AppendLine($"  {(cond ? "✓" : "✗")} {name}   {detail}");
        }

        // ① 编号 ↔ 绑定值 往返（错一个就会"绑到别的宏上"）
        Check(RefOf(1) == -1001 && RefOf(101) == -1101, "编号→绑定值",
              "文本1=-1001 · 宏101=-1101");
        Check(FindByRef(RefOf(101)) is null, "未存在的编号返回 null（不是抛异常）",
              $"RefOf(101)={RefOf(101)}");

        // ② 区间判定：鼠标动作（-1..-6）不能被当成宏引用，反之亦然
        var mouseOk = !IsRef(-1) && !IsRef(-6) && !IsRef(0);
        var refOk = IsRef(-1001) && IsRef(-1101) && !IsRef(-1000);
        Check(mouseOk && refOk, "区间不重叠", "鼠标 -1..-6 不算宏 · -1000 本身不算 · -1001 算");

        // ③ 步骤参数夹紧（HoldMs=999999 会让播放器卡死）
        var st = new Step { Type = StepKeys, Vks = new[] { 17, 65 }, HoldMs = 999999, Ms = -50, Slot = 99 };
        SanitizeStep(st);
        Check(st.HoldMs == 2000 && st.Ms == 0 && st.Slot == TextSlots, "参数夹紧",
              $"HoldMs 999999→{st.HoldMs} · Ms -50→{st.Ms} · Slot 99→{st.Slot}");

        // ④ 宏里不收负数（鼠标动作）和越界 VK
        var st2 = new Step { Type = StepKeys, Vks = new[] { 17, -1, 300, 65 } };
        SanitizeStep(st2);
        Check(st2.Vks.Length == 2 && st2.Vks.SequenceEqual(new[] { 17, 65 }), "过滤负数/越界 VK",
              "[-1,300] 被剔除，只剩 [17,65]");

        // ⑤ 序列化往返（字段名写错会静默丢参数）
        var doc = new Doc();
        doc.Items.Add(new Item
        {
            Num = 101, Kind = KindSeq, Name = "测试",
            Steps = new List<Step>
            {
                new() { Type = StepKeys, Vks = new[] { 17, 65 }, HoldMs = 30 },
                new() { Type = StepWait, Ms = 80 },
                new() { Type = StepPaste, Slot = 2 },
            },
        });
        var json = JsonSerializer.Serialize(doc, WriteOpts);
        var back = JsonSerializer.Deserialize<Doc>(json, ReadOpts);
        var b0 = back?.Items.FirstOrDefault();
        Check(b0 is { Steps.Count: 3 } && b0.Steps[2].Slot == 2 && b0.Steps[0].Vks.Length == 2,
              "序列化往返", "3 步且参数不丢");

        // ⑥ 重复编号去重 + 文本槽编号上限（手改文件最容易出这两种）
        var d2 = new Doc();
        d2.Items.Add(new Item { Num = 101, Kind = KindSeq, Name = "A", Steps = { new Step { Type = StepWait, Ms = 10 } } });
        d2.Items.Add(new Item { Num = 101, Kind = KindSeq, Name = "B", Steps = { new Step { Type = StepWait, Ms = 20 } } });
        d2.Items.Add(new Item { Num = 11, Kind = KindText, Name = "越界文本槽", Text = "x" });
        Sanitize(d2);
        Check(d2.Items.Count == 1 && d2.Items[0].Name == "A", "去重 + 文本槽 ≤10",
              $"101 重复留第一个 · Num=11 的 text 被剔除 → 剩 {d2.Items.Count} 条");

        // ⑧ 循环遍数：默认 1；**负数必须回落 1**（绝不能变成 0 = 无限循环）
        //    这是最糟的失败方向：一个手改错的 -1 会让宏在用户走开后一直转。
        var li = new Item { Num = 101, Kind = KindSeq, Name = "L" };
        li.Steps.Add(new Step { Type = StepWait, Ms = 10 });
        var defLoop = li.Loop;
        li.Loop = -5;
        var d3 = new Doc();
        d3.Items.Add(li);
        Sanitize(d3);
        Check(defLoop == 1 && d3.Items[0].Loop == 1, "循环遍数默认 1、负数回落 1",
              $"默认={defLoop} · -5→{d3.Items[0].Loop}（不能变 0=无限循环）");

        var l0 = new Item { Num = 102, Kind = KindSeq, Name = "L0", Loop = 0 };
        l0.Steps.Add(new Step { Type = StepWait, Ms = 10 });
        var d4 = new Doc();
        d4.Items.Add(l0);
        Sanitize(d4);
        Check(d4.Items[0].Loop == 0, "0 = 一直循环要原样保留", "界面必须写明 0 是无限，不能靠用户猜");

        // ⑨ 录制转换（纯函数，不用真按键）：组合键 / 点击 / 拖拽 / 手抖
        var ev1 = new List<Recorder.RawEv>
        {
            new() { Kind = "kd", Vk = 0x11, T = 0 },                  // Ctrl 按下
            new() { Kind = "kd", Vk = 0x41, T = 50 },                 // A 按下
            new() { Kind = "ku", Vk = 0x41, T = 120 },                // A 抬起（按住 70ms）
            new() { Kind = "ku", Vk = 0x11, T = 150 },                // Ctrl 抬起
            new() { Kind = "md", X = 100, Y = 200, T = 650, Button = 0, Win = "记事本" },
            new() { Kind = "mu", X = 101, Y = 201, T = 700, Button = 0 },
        };
        var b1 = Recorder.BuildSteps(ev1, 1920, 1080);
        Check(b1.Count == 3
              && b1[0].Type == StepKeys && b1[0].Vks.Contains(0x11) && b1[0].Vks.Contains(0x41)
              && b1[0].HoldMs == 70
              && b1[1].Type == StepWait && b1[1].Ms is >= 400 and <= 600
              && b1[2].Type == StepClick && b1[2].Target == "coord" && b1[2].X == 100 && b1[2].Y == 200,
              "录制转换：Ctrl+A + 停顿 + 点击",
              $"步数={b1.Count}(期望3) · 首步={b1.FirstOrDefault()?.Type}·hold={b1.FirstOrDefault()?.HoldMs}"
              + $" · 末步={(b1.Count > 0 ? b1[^1].Type : "-")}");

        var ev2 = new List<Recorder.RawEv>
        {
            new() { Kind = "md", X = 100, Y = 100, T = 0, Button = 0, Win = "画图" },
            new() { Kind = "mm", X = 200, Y = 200, T = 100 },
            new() { Kind = "mm", X = 300, Y = 300, T = 200 },
            new() { Kind = "mu", X = 300, Y = 300, T = 250, Button = 0 },
        };
        var b2 = Recorder.BuildSteps(ev2, 1920, 1080);
        Check(b2.Count == 1 && b2[0].Type == StepDrag && b2[0].X == 100 && b2[0].X2 == 300,
              "录制转换：拖拽（位移 >8px）",
              $"步数={b2.Count} 类型={b2.FirstOrDefault()?.Type} 终点X={b2.FirstOrDefault()?.X2}");

        var ev3 = new List<Recorder.RawEv>
        {
            new() { Kind = "md", X = 500, Y = 500, T = 0, Button = 0, Win = "X" },
            new() { Kind = "mm", X = 503, Y = 502, T = 50 },          // 手抖 3px
            new() { Kind = "mu", X = 503, Y = 502, T = 90, Button = 0 },
        };
        var b3 = Recorder.BuildSteps(ev3, 1920, 1080);
        Check(b3.Count == 1 && b3[0].Type == StepClick, "录制转换：手抖 3px 仍算点击（不是拖拽）",
              $"步数={b3.Count} 类型={b3.FirstOrDefault()?.Type}");

        // ⑩ Lua 导入（罗技）：能剥壳、能转组合键、按住循环必须报错并指行号
        var lua1 = "function OnEvent(event, arg)\n"
                 + "  if event == \"MOUSE_BUTTON_PRESSED\" and arg == 5 then\n"
                 + "    PressAndReleaseMouseButton(1)\n"
                 + "    Sleep(80)\n"
                 + "  end\n"
                 + "end";
        var lr1 = LuaImport.Parse(lua1);
        Check(lr1.Ok && lr1.Steps.Count == 2
              && lr1.Steps[0].Type == StepClick && lr1.Steps[0].Target == "cursor"
              && lr1.Steps[1].Type == StepWait && lr1.Steps[1].Ms == 80,
              "Lua 导入：剥掉 OnEvent + if 外壳",
              $"步数={lr1.Steps.Count}(期望2) · 首步={lr1.Steps.FirstOrDefault()?.Type}"
              + $" · 次步={lr1.Steps.Skip(1).FirstOrDefault()?.Type}");

        var lua2 = "function OnEvent(event, arg)\n"
                 + "  if event == \"MOUSE_BUTTON_PRESSED\" and arg == 5 then\n"
                 + "    while IsMouseButtonPressed(5) do\n"
                 + "      PressAndReleaseMouseButton(1)\n"
                 + "      Sleep(60)\n"
                 + "    end\n"
                 + "  end\n"
                 + "end";
        var lr2 = LuaImport.Parse(lua2);
        Check(!lr2.Ok && lr2.Error.Contains("while") && lr2.Error.Contains("第"),
              "Lua 导入：按住循环必须报错并指行号（不能静默导入）",
              lr2.Ok ? "竟然导入成功了（危险）" : lr2.Error[..Math.Min(lr2.Error.Length, 38)]);

        var lua3 = "PressKey(29)\nPressKey(\"c\")\nSleep(50)\nReleaseKey(\"c\")\nReleaseKey(29)";
        var lr3 = LuaImport.Parse(lua3);
        Check(lr3.Ok && lr3.Steps.Count == 1 && lr3.Steps[0].Type == StepKeys
              && lr3.Steps[0].Vks.Contains(0x11) && lr3.Steps[0].Vks.Contains(0x43)
              && lr3.Steps[0].HoldMs == 50,
              "Lua 导入：scancode 29=Ctrl，组合只闭合一步、Sleep 变按住时长",
              $"步数={lr3.Steps.Count}(期望1) · hold={lr3.Steps.FirstOrDefault()?.HoldMs}(期望50)");

        // ⑪ Lua 键码表必须与**官方 Appendix A** 一致。
        //    这几个锚点是专门挑出来的：`0x47`(num7) 曾经被错映射成 Home、
        //    方向键（0x148 等）整段缺失 —— 这类错误导入后表现为"按键不对"，
        //    用户根本不会想到是映射表的问题，所以必须钉死。
        var anchors = new (int sc, int vk, string note)[]
        {
            (0x01, 0x1B, "escape"),
            (0x1e, 0x41, "a"),
            (0x39, 0x20, "spacebar"),
            (0x29, 0xC0, "tilde"),
            (0x2a, 0x10, "lshift"),
            (0x47, 0x67, "num7（**不是** Home）"),
            (0x52, 0x60, "num0（**不是** Delete）"),
            (0x148, 0x26, "up（扩展键）"),
            (0x153, 0x2E, "delete（扩展键）"),
            (0x15b, 0x5B, "lgui（扩展键）"),
        };
        var keyOk = true;
        var keyBad = "八个锚点全部一致";
        foreach (var (sc, vk, note) in anchors)
        {
            var got = LuaImport.VkOfScancode(sc);
            if (got != vk) { keyOk = false; keyBad = $"{note}: 期望 0x{vk:X2} 实得 0x{got:X2}"; break; }
        }
        // 键名同样抽查几个官方写法（"spacebar"/"tilde"/"num7" 都是社区脚本不这么写的）
        var nameOk = LuaImport.VkOfKeyName("spacebar") == 0x20
                     && LuaImport.VkOfKeyName("tilde") == 0xC0
                     && LuaImport.VkOfKeyName("num7") == 0x67
                     && LuaImport.VkOfKeyName("lbracket") == 0xDB
                     && LuaImport.VkOfKeyName("space") == 0x20;      // 别名也要认
        Check(keyOk && nameOk, "Lua 键码表与官方 Appendix A 一致",
              keyOk && nameOk ? "scancode 10 锚点 + 键名 5 项（含别名）" : (keyOk ? "键名不符" : keyBad));

        // ⑫ KeymouseGo 脚本导入 —— 用**用户真实脚本的片段**做断言（不是编的数据）
        //    真实片段看着别扭但很有代表性：第一条是 mouse up（没有配对的 down）、
        //    键盘是 13=Return、坐标是绝对像素 2996,206。
        var km1 = "[\n"
                + "[113,\"EM\",\"mouse left up\",[2996,206]],\n"
                + "[186,\"EK\",\"key down\",[13,\"Return\",0]],\n"
                + "[132,\"EK\",\"key up\",[13,\"Return\",0]],\n"
                + "[94,\"EM\",\"mouse left down\",[2996,206]],\n"
                + "[126,\"EM\",\"mouse left up\",[2996,206]],\n"
                + "]";
        var kr1 = KmgoImport.Parse(km1, 3840, 2160, _ => 1);
        Check(kr1.Ok && kr1.Steps.Count == 3
              && kr1.Steps[0].Type == StepKeys && kr1.Steps[0].Vks.Contains(0x0D)
              && kr1.Steps[0].HoldMs == 132
              && kr1.Steps[1].Type == StepWait
              && kr1.Steps[2].Type == StepClick && kr1.Steps[2].X == 2996,
              "KeymouseGo 导入：回车 + 点击（真实脚本片段）",
              $"步数={kr1.Steps.Count}(期望3) · 首步={kr1.Steps.FirstOrDefault()?.Type}"
              + $" · 键={kr1.Steps.FirstOrDefault()?.Vks?.Length}");

        // EX input 要变成"引用文本槽的 paste 步骤"
        var gotText = "";
        var km2 = "[100,\"EX\",\"input\",\"你好 world\"],\n"
                + "[50,\"EK\",\"key down\",[13,\"Return\",0]],\n"
                + "[50,\"EK\",\"key up\",[13,\"Return\",0]]";
        var kr2 = KmgoImport.Parse(km2, 1920, 1080, s => { gotText = s; return 3; });
        Check(kr2.Ok && kr2.Steps.Any(s => s.Type == StepPaste && s.Slot == 3) && gotText == "你好 world",
              "KeymouseGo 导入：EX input 落进文本槽",
              $"文本=「{gotText}」· 槽位={kr2.Steps.FirstOrDefault(s => s.Type == StepPaste)?.Slot}");

        // 新版（带 scripts / 流程控制）必须**明确拒绝**，不能假装成功
        var km3 = "{ scripts: [ { type: 'if', do: [], else: [] } ] }";
        var kr3 = KmgoImport.Parse(km3, 1920, 1080, _ => 1);
        Check(!kr3.Ok && kr3.Error.Contains("新版"), "KeymouseGo 导入：新版流程控制要明确拒绝",
              kr3.Ok ? "竟然导入成功了" : kr3.Error[..Math.Min(kr3.Error.Length, 30)]);

        // ⑬ StateJson 必须是**合法 JSON**：它被直接拼进 getState 返回给界面，
        //   格式错一个字符就是整个界面白屏（和 v1.1.0 那次同级别的故障）。
        var sj = StateJson();
        var sjOk = true;
        try { sjOk = System.Text.Json.Nodes.JsonNode.Parse(sj) is System.Text.Json.Nodes.JsonArray; }
        catch { sjOk = false; }
        Check(sjOk, "StateJson 是合法 JSON 数组", "getState 直接拼接它，格式错了界面全白");

        sb.AppendLine(ok ? "⇒ 全部通过 ✓" : "⇒ 有失败项 ✗");
        return sb.ToString();
    }
}
