namespace Iprc1000Adapter;

/// <summary>
/// **动作值**：让任意按键直接执行一个"非键盘"动作 ——
/// 鼠标左键 / 右键 / 滚轮上 / 滚轮下 / 切换配置组。
///
/// ## 怎么存
///
/// 复用现有的映射格式 `int[] vks`（`keymap.json` 里每个按键一个整数数组），
/// 用**负数哨兵**表示鼠标动作：
///
/// | 值 | 动作 |
/// |---|---|
/// | `-1` | 鼠标左键 |
/// | `-2` | 鼠标右键 |
/// | `-3` | 滚轮上 |
/// | `-4` | 滚轮下 |
/// | `-5` | 切换配置组（方块键的出厂值）|
///
/// **为什么用负数**：虚拟键码（VK）的取值范围是 `0x00..0xFF`，负数**永远不可能**是一个
/// 真实按键。这样万一哪天漏了一处分派，它会立刻暴露成一个明确的越界值，
/// 而不是悄悄注入一个"看起来像键盘键"的错东西。
///
/// ## 语义
///
/// - **左键 / 右键是"按住—抬起"成对**的：按住方向 + 按住左键 = 拖拽，免费成立
///   （与鼠标模式里「确认=左键、info=右键」同一套语义）。
/// - **滚轮是"按下滚一格"**：按住不重复滚 —— 重复滚交给鼠标模式的滚轮/斜坡那套逻辑，
///   映射里只做一次，免得"按住不放"时滚到停不下来。
///
/// ⚠ 与鼠标模式（<see cref="MouseEngine"/>）互不冲突：那边管的是**方向键**（移动，
/// 由引擎线程按住加速），这边管的是**其余按键**在映射表里被配成动作的情况。
/// 鼠标类动作走 <see cref="MouseInjector"/>（SendInput），不经过虚拟 HID。
/// </summary>
internal static class KeyAct
{
    public const int Left = -1;
    public const int Right = -2;
    public const int WheelUp = -3;
    public const int WheelDown = -4;
    /// <summary>切换配置组（方块键的出厂值）。★ v1.2.4 起鼠标模式=一个配置组，
    /// 于是"切配置组"成了普通可绑定动作，方块键不再硬编码。</summary>
    public const int SwitchGroup = -5;
    /// <summary>截图点击：在目标窗口里找**采集过的图标**并单击（见 <see cref="TemplateClick"/>）。</summary>
    public const int ClickIcon = -6;

    /// <summary>一格滚轮（`WHEEL_DELTA`）</summary>
    public const int WheelStep = 120;

    public static readonly int[] All = { Left, Right, WheelUp, WheelDown, SwitchGroup, ClickIcon };
    /// <summary>只属于"鼠标"的那几个（滚轮/左右键）</summary>
    public static readonly int[] MouseOnes = { Left, Right, WheelUp, WheelDown };

    /// <summary>是不是动作值（负数哨兵只在 -5..-1 这一小段里算数）</summary>
    // ★ 2026-10-18 修：原来写 `vk >= SwitchGroup(-5) && vk <= Left(-1)`，
    //   新增 ClickIcon(-6) 后它被判"虚拟键码越界"（真机报错就是这句）。
    //   区间必须覆盖**最小的哨兵**，否则每加一个动作都要改这里。
    // ★ 2026-10-30：宏引用（`-1001` 起，见 <see cref="Macros"/>) 是**另一段不连续区间**，
    //   不能靠"扩大这个范围"来覆盖 —— 那会把 -7..-999 这些**无意义的空洞**也判成合法，
    //   于是一个手改错的值会被当成动作送进注入层，而不是被拒掉。
    public static bool Is(int vk) => (vk >= ClickIcon && vk <= Left) || Macros.IsRef(vk);

    /// <summary>是不是鼠标类动作</summary>
    public static bool IsMouse(int vk) => vk >= WheelDown && vk <= Left;

    /// <summary>宏引用在界面/日志里的显示名。目标被删了也要能看出来（不能显示成空白）</summary>
    private static string MacroLabel(int vk)
    {
        var it = Macros.FindByRef(vk);
        if (it is null) return "（目标已删除）";
        return (it.Kind == Macros.KindText ? "输入文本" : "执行宏") + "：" + it.Name;
    }

    /// <summary>中文名（界面与日志用）；不是鼠标动作返回 null</summary>
    public static string? NameOf(int vk) => vk switch
    {
        Left => "鼠标左键",
        Right => "鼠标右键",
        WheelUp => "滚轮上",
        WheelDown => "滚轮下",
        SwitchGroup => "切换配置组",
        ClickIcon => "截图点击",
        _ when Macros.IsRef(vk) => MacroLabel(vk),
        _ => null,
    };

    /// <summary>中文名 → 哨兵值；不认识返回 null。
    /// 让「导出→导入」和手工编辑 `keymap.json` 都能用可读的名字。</summary>
    public static int? FromName(string? s) => (s ?? "").Trim() switch
    {
        "鼠标左键" or "左键" or "mouseleft" or "left" => Left,
        "鼠标右键" or "右键" or "mouseright" or "right" => Right,
        "滚轮上" or "滚轮向上" or "wheelup" => WheelUp,
        "滚轮下" or "滚轮向下" or "wheeldown" => WheelDown,
        "切换配置组" or "nextgroup" or "group" => SwitchGroup,
        "截图点击" or "clickicon" or "iconshot" => ClickIcon,
        _ => null,
    };

    /// <summary>
    /// 真正下发。`down` = 按下还是抬起。
    ///
    /// 滚轮忽略 `down`（只在按下时滚一格），见类注释里的理由。
    /// 返回 false 表示 SendInput 被拒（多为 UIPI：前台窗口权限更高）。
    /// </summary>
    public static bool Apply(int vk, bool down) => vk switch
    {
        Left => MouseInjector.Left(down),
        Right => MouseInjector.Right(down),
        WheelUp => down && MouseInjector.Wheel(WheelStep),
        WheelDown => down && MouseInjector.Wheel(-WheelStep),
        // 切配置组**只在按下时**做一次（等抬起就等于"丢一次 UP 这个键就失灵"）
        SwitchGroup => true,
        // 截图点击**只在按下时**做一次（它是一次性动作，抬起无需处理）
        ClickIcon => down && ClickIconNow(),
        // ★ 宏引用：按下 = 开始播放（正在播同一个则=中断），抬起什么都不做。
        //   一次性语义和「切换配置组」一致 —— 等抬起就等于"丢一次 UP 这个键就失灵"。
        _ when Macros.IsRef(vk) => down && MacroPlayer.Toggle(Macros.RefBase - vk),
        _ => false,
    };

    /// <summary>截图点击：找图标并单击。结果写进日志（失败原因要能看出来）。
    /// 2026-10-30 由 private 改 public：宏里的「点击」步骤要复用同一套实现，
    /// **不要**在宏里另写一份截图匹配 —— 两份实现迟早行为不一致。</summary>
    public static bool ClickIconNow()
    {
        try
        {
            var msg = TemplateClick.TryClick();
            EtwKeySource.AppendLog($"APPLY  截图点击  {msg}");
            return true;
        }
        catch (Exception e)
        {
            EtwKeySource.AppendLog($"APPLY  截图点击  异常：{e.Message}");
            return false;
        }
    }

    /// <summary>
    /// 映射值是不是"单个动作" —— 动作**不能和其它键组合**
    /// （Ctrl+鼠标左键 / Ctrl+切配置组 在遥控器上都没法表达，也没有实际用例）。
    /// </summary>
    public static bool IsSingleAction(int[] vks) => vks.Length == 1 && Is(vks[0]);

    /// <summary>
    /// 映射层自检（`--mouseact-test`）：**纯逻辑，不注入任何东西**。
    ///
    /// 为什么要有它：鼠标动作是塞进 `int[] vks` 的负数哨兵，最容易出的错是
    /// 「`Parse` 不认 → 存不进去」和「`Format` 显示成 -1 → 界面看不懂」这两个**静默**故障，
    /// 而且越界值还可能被当成真实 VK 送进键盘注入。这里把这几条边界钉住。
    /// </summary>
    public static string SelfTest()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("IPRC1000 动作值映射自检（纯逻辑，不注入）");
        sb.AppendLine(new string('=', 58));
        var ok = true;
        void Check(bool pass, string what, string detail)
        {
            ok &= pass;
            sb.AppendLine($"  {(pass ? "✓" : "✗")} {what}");
            sb.AppendLine($"       {detail}");
        }

        // ① 哨兵值都不在 VK 范围里
        var allNeg = All.All(v => v < 0);
        Check(allNeg, "五个哨兵都是负数（VK 范围 0x00..0xFF 之外）",
              "值 = " + string.Join(" ", All));

        // ② 名字 ↔ 哨兵 互查
        var nameRound = All.All(v => FromName(NameOf(v)) == v);
        Check(nameRound, "中文名 → 哨兵值 可往返",
              string.Join(" · ", All.Select(v => $"{NameOf(v)}={v}")));

        // ③ Parse → Format 往返（这是"存得进、看得懂"的关键）
        var cases = new[] { "鼠标左键", "鼠标右键", "滚轮上", "滚轮下", "切换配置组", "截图点击" };
        var bad = cases.Where(c => Keys.Format(Keys.Parse(c)) != c).ToArray();
        Check(bad.Length == 0, "Keys.Parse → Keys.Format 往返（导出/导入与手改 JSON 都靠它）",
              bad.Length == 0
                ? string.Join(" / ", cases.Select(c => $"\"{c}\"→[{Keys.Parse(c)[0]}]→\"{Keys.Format(Keys.Parse(c))}\""))
                : "对不上的：" + string.Join(" ", bad));

        // ④ 负数数字也能解析（界面保存时传的就是 "-1" 这种）
        var byNum = Keys.Parse("-1").Length == 1 && Keys.Parse("-1")[0] == Left
                    && Keys.Parse("-4")[0] == WheelDown
                    && Keys.Parse("-6").Length == 1 && Keys.Parse("-6")[0] == ClickIcon;
        Check(byNum, "Parse 认识 \"-1\"/\"-4\"/\"-6\"（界面保存走的就是这种字符串）",
              $"Parse(\"-1\")=[{Keys.Parse("-1")[0]}] · Parse(\"-4\")=[{Keys.Parse("-4")[0]}] · Parse(\"-6\")=[{Keys.Parse("-6")[0]}]");

        // ⑤ 越界负数必须被拒（不能让它变成某个"真实 VK"）
        var rejected = false;
        // ★ 2026-10-18：新增 ClickIcon(-6) 后，-6 已是合法动作 —— 越界样例改用 -7。
        foreach (var badVal in new[] { "-7", "-99", "-0x1", "0xEE" })
        {
            try { Keys.Parse(badVal); } catch { rejected = true; }
            if (!rejected) { sb.AppendLine($"        ★ {badVal} 竟然被接受了"); break; }
            rejected = true;
        }
        Check(rejected, "越界负数被 Parse 拒绝（不会漏成真实 VK）",
              "试过 -7 / -99 / -0x1，全部抛异常（0xEE 是合法 VK，不受影响）");

        // ⑥ 组合语义：鼠标动作只能单独出现
        var combo = IsSingleAction(new[] { Left }) && !IsSingleAction(new[] { 0x11, Left })
                    && !IsSingleAction(new[] { 0x11 });
        Check(combo, "动作只认「单独一个」（Ctrl+鼠标左键 这种无法表达）",
              "[-1]=单独✓ · [Ctrl,-1]=组合✗ · [Ctrl]=非鼠标✗");

        // ⑦ 普通键盘映射不受影响
        var normal = Keys.Format(Keys.Parse("17,65")) == "Ctrl+A";
        Check(normal, "普通组合键路径未被改动",
              $"Parse(\"17,65\") → \"{Keys.Format(Keys.Parse("17,65"))}\"");

        sb.AppendLine();
        sb.AppendLine(ok ? "⇒ 全部通过 ✓" : "⇒ ★ 有断言失败");
        return sb.ToString();
    }
}
