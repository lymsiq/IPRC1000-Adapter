using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Iprc1000Adapter;

/// <summary>
/// **截图点击**：把某个按钮的样子"学"下来，之后按遥控器键时在目标窗口里找它并点击。
///
/// ## 为什么不用 UI 自动化
///
/// 2026-10-18 实测：豆包的工具栏窗口类是 `OimeDirectUIWindow`（DirectUI 自绘），
/// UIA `TreeScope.Descendants` **返回 0 个元素** —— 它不对无障碍接口暴露任何按钮。
/// 所以"按控件名找按钮"这条路不存在。
///
/// ## 为什么不用固定坐标
///
/// 工具栏跟着输入框走，位置每次都变；固定坐标一点就废。
///
/// ## 本方案
///
/// 1. **采集**：用户把鼠标停在目标按钮上，点「采集图标」⇒ 截取光标周围 44×44 存成模板，
///    同时记下该点所属**顶层窗口的类名**（如 `OimeDirectUIWindow`）。
/// 2. **触发**：按类名找到那个窗口 → 截它的矩形 → 在窗口内做**灰度归一化互相关（NCC）**
///    找模板 → 置信度达标就把光标移过去单击。
///
/// 因为搜索范围只有那个小窗口、坐标由匹配结果算出来，所以**窗口移动/输入框位置变化都不影响**。
///
/// ## 已知边界
///
/// - 目标窗口必须可见（不能完全被其他窗口挡住，因为用的是屏幕截取）；
/// - 主题/缩放变化会让模板失效 —— 重新采集一次即可；
/// - 匹配阈值**可配置**（默认 **0.75**，见 `Settings.ClickMinScore`；SikuliX 默认 0.7，
///   其文档说"要防误点应 >0.85"）。低于阈值就不点 —— 宁可不动，也不要在错误位置乱点；
/// - 搜索范围**可配置**（`Settings.ClickSearchScope`：局部 / 窗口下部 / 全窗）。
/// </summary>
internal static class TemplateClick
{
    private const int LearnSize = 44;      // 采集边长
    /// <summary>
    /// 门槛**默认值**（唯一来源是 `Settings.ClickMinScore`，这里只作为它的兜底常量）。
    /// 运行时的判定一律用 `Settings.ClickMinScore` —— 本类内部**不得**再出现独立的门槛常量。
    /// </summary>
    private const double DefaultMinScore = 0.75;
    private const int MaxW = 3000, MaxH = 1600; // 窗口尺寸上限（防御异常窗口）
    /// <summary>在采集位置附近多大范围内找（±像素）。大窗口全遍历要几秒，收窄到这块足够快。</summary>
    private const int SearchRadius = 120;
    /// <summary>失败时先试这些缩放比（应对 DPI / 窗口缩放 / 主题字号变化）</summary>
    private static readonly double[] Rescales = { 0.80, 0.90, 1.10, 1.25 };
    private const int WideRadius = 300;   // 全缩放都失败后，再放宽一次搜索半径
    /// <summary>"still-there" 预检：上次命中点附近这个半径内先试一次（SikuliX 的核心加速手段）</summary>
    private const int StillThereRadius = 24;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string cls, string? name);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr h, System.Text.StringBuilder sb, int max);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder sb, int max);

    private const uint GA_ROOT = 2;
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    /// <summary>采集：把光标周围的图案存成模板，并记住所在顶层窗口的类名。</summary>
    public static string Learn()
    {
        if (!GetCursorPos(out var p)) return "采集失败：读不到光标位置。";
        var half = LearnSize / 2;
        var x = Math.Max(0, p.X - half);
        var y = Math.Max(0, p.Y - half);

        string cls;
        using (var bmp = new Bitmap(LearnSize, LearnSize, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(x, y, 0, 0, new Size(LearnSize, LearnSize), CopyPixelOperation.SourceCopy);

            // ★ 采集必须校验"这块地方真的有图案"。
            //   在纯色空白处采集，模板方差≈0 ⇒ 匹配永远返回 0 ⇒ 之后每次点都失败，
            //   而当时却提示"采集成功" —— 这是最误导人的一种失败。
            var gray = ToGray(bmp);
            double mean = 0;
            foreach (var v in gray) mean += v;
            mean /= gray.Length;
            double var2 = 0;
            foreach (var v in gray) { var d = v - mean; var2 += d * d; }
            var2 /= gray.Length;
            var sd = Math.Sqrt(var2);
            if (sd < 6.0)
                return $"采集失败：光标周围几乎是一片纯色（标准差仅 {sd:F1}）。" +
                       "请把鼠标**正对着图标的图案中心**再试一次。";

                Settings.ClickTemplate = ToBase64(bmp);
            Settings.ClickLastOk = false;      // 模板换了，旧的命中点作废
        }

        // 光标所在处 → 顶层窗口的类名（DirectUI 的子窗口可能不是它，所以取 root）
        var hwnd = WindowFromPoint(new POINT { X = p.X, Y = p.Y });
        var root = GetAncestor(hwnd, GA_ROOT);
        var sb = new System.Text.StringBuilder(256);
        GetClassNameW(root, sb, sb.Capacity);
        cls = sb.ToString();

        // 类名 `Chrome_WidgetWin_1` 这类很通用（所有 Electron 应用都用），
        // 所以还要记住**进程名**，触发时用它来确认是同一个程序的窗口。
        string procName = "";
        try
        {
            GetWindowThreadProcessId(root, out var pid);
            if (pid != 0) procName = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName;
        }
        catch { }

        // ★ 采集前必须排除"本程序自己"。
        //   真机出现过：用户在弹窗上点「采集图标」⇒ 记下了 IPRC1000 Adapter 自己的窗口，
        //   消息还显示"采集成功"（看着完全正常），之后每次触发都在自己窗口里找图标。
        try
        {
            var self = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
            if (!string.IsNullOrEmpty(procName) &&
                string.Equals(procName, self, StringComparison.OrdinalIgnoreCase))
                return $"采集失败：光标停在本程序自己的窗口上（进程 {procName}）。" +
                       "请把鼠标移到**目标程序**的那个按钮上再点「采集图标」。";
        }
        catch { }

        Settings.ClickProcess = procName;
        try
        {
            var sbT = new System.Text.StringBuilder(512);
            GetWindowTextW(root, sbT, sbT.Capacity);
            Settings.ClickTitle = sbT.ToString();
        }
        catch { }

        Settings.ClickWindowClass = cls;
        Settings.ClickTemplateX = p.X - x;   // 模板中心相对模板左上角（一般就是 half）
        Settings.ClickTemplateY = p.Y - y;

        // 记住"图标在窗口里的相对位置" —— 工具栏上可能有多个长得一样的图标，
        // 光靠模板会点错第一个，用它才能挑对。
        if (IsIconic(root)) return "采集失败：目标窗口处于最小化状态，请先恢复窗口。";
        if (GetWindowRect(root, out var wr))
        {
            Settings.ClickRelX = p.X - wr.L;
            Settings.ClickRelY = p.Y - wr.T;
            Settings.ClickRelBottom = wr.B - p.Y;   // ★ 贴底布局的关键锚点
        }
        return $"已采集 {LearnSize}×{LearnSize} 图标（进程 {procName} / 窗口 `{cls}`）。" +
               "请立刻点「试一下截图点击」验证：相似度 ≥0.90 很好；0.80~0.85 建议把鼠标对准图标更中心处重采。" +
               "（采集时若图标处于悬停高亮态，请挪开再回来重采 —— 高亮态和常态长得不一样）";
    }

    /// <summary>触发：在目标窗口里找模板并点击。返回给人看的说明。</summary>
    public static string TryClick()
    {
        var b64 = Settings.ClickTemplate;
        var cls = Settings.ClickWindowClass;
        if (string.IsNullOrEmpty(b64) || string.IsNullOrEmpty(cls))
            return "还没采集图标：先在「按键映射」页把鼠标停在目标按钮上，点「采集图标」。";

        // 先按**标题**精确找（同一进程常有多个同类名窗口：主窗、浮层、隐藏窗）。
        IntPtr hwnd = IntPtr.Zero;
        var title = Settings.ClickTitle;
        if (!string.IsNullOrEmpty(title))
        {
            try { hwnd = FindWindowW(cls, title); } catch { hwnd = IntPtr.Zero; }
        }
        if (hwnd == IntPtr.Zero)
        {
            try { hwnd = FindWindowW(cls, null); }
            catch { hwnd = IntPtr.Zero; }
        }

        // ★ `Chrome_WidgetWin_1` 是所有 Electron 应用共用的类名（Edge/VS Code/WorkBuddy…）。
        //   只按类名找会拿到**别的程序**的窗口 —— 必须核对进程名，不匹配就换一个。
        var wantProc = Settings.ClickProcess;
        if (hwnd != IntPtr.Zero && !string.IsNullOrEmpty(wantProc) && ProcOf(hwnd) != wantProc)
        {
            var alt = FindByClassAndProcess(cls, wantProc);
            if (alt != IntPtr.Zero) hwnd = alt;
        }
        if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd))
            return $"找不到可见的窗口 `{cls}`（目标界面没打开？）";
        if (IsIconic(hwnd))
            return $"窗口 `{cls}` 处于最小化状态，请先恢复窗口。";

        GetWindowRect(hwnd, out var r);
        var w = r.R - r.L;
        var h = r.B - r.T;
        if (w <= 0 || h <= 0 || w > MaxW || h > MaxH)
            return $"目标窗口尺寸异常（{w}×{h}），跳过。";

        using var tpl = FromBase64(b64);
        if (tpl == null) return "模板损坏，请重新采集。";
        if (tpl.Width >= w || tpl.Height >= h) return "目标窗口比模板还小，跳过。";

        using var shot = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(shot))
            g.CopyFromScreen(r.L, r.T, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);

        // ★ 只在"采集时的位置"附近找 —— 图标位置基本固定，没必要全窗遍历。
        //   WorkBuddy 那种 1200×800 的窗口，全窗 NCC 要 2~3 秒；±200px 只要 ~0.1 秒。
        // ★★★ 2026-10-19（真机"窗口从全屏变窗口后找不到"）：
        //   ✨ 是**贴底布局** —— 窗口变矮时它会上移。原来只按"距顶部"推算，
        //   全屏时存的 1341 在 1248 高的窗口里**直接越界** ⇒ 旧代码在这里 return
        //   "采集位置超出"，一次都没搜。
        //   现在：① 有底部锚点就用 `高度 - 距底距离` 重新推算；② 越界只做**夹取**，不放弃。
        var expectY = Settings.ClickRelBottom > 0
            ? h - Settings.ClickRelBottom          // 贴底布局：优先按底部锚点
            : Settings.ClickRelY;
        var expectX = Settings.ClickRelX;
        expectX = Math.Clamp(expectX, 0, Math.Max(0, w - 1));
        expectY = Math.Clamp(expectY, 0, Math.Max(0, h - 1));

        var roiX = Math.Clamp(expectX - SearchRadius, 0, Math.Max(0, w - tpl.Width));
        var roiY = Math.Clamp(expectY - SearchRadius, 0, Math.Max(0, h - tpl.Height));
        var roiW = Math.Min(w - roiX, SearchRadius * 2 + tpl.Width);
        var roiH = Math.Min(h - roiY, SearchRadius * 2 + tpl.Height);
        var roiUsable = roiW > tpl.Width && roiH > tpl.Height;

        // ★ 0) "still-there" 预检（SikuliX 的做法）：
        //   如果上次命中过，目标通常还在同一个位置 —— 先在那附近 ±24px 做一次**极小范围**
        //   匹配（几十个位置，约 1ms）。命中就直接用它，省掉整段搜索。
        double score = 0; int mx = 0, my = 0; double sx = 1.0, sy = 1.0;
        double second = 0;
        var minScore = Settings.ClickMinScore;   // 可配置（默认 0.75）
        var hadCache = Settings.ClickLastOk &&
                       Settings.ClickLastX > 0 && Settings.ClickLastY > 0;
        if (hadCache)
        {
            var cx0 = Math.Clamp(Settings.ClickLastX - Settings.ClickTemplateX, 0, Math.Max(0, w - tpl.Width));
            var cy0 = Math.Clamp(Settings.ClickLastY - Settings.ClickTemplateY, 0, Math.Max(0, h - tpl.Height));
            var px = Math.Max(0, cx0 - StillThereRadius);
            var py = Math.Max(0, cy0 - StillThereRadius);
            var pw = Math.Min(w - px, StillThereRadius * 2 + tpl.Width);
            var ph = Math.Min(h - py, StillThereRadius * 2 + tpl.Height);
            if (pw > tpl.Width && ph > tpl.Height)
            {
                var (sc0, x0, y0, sec0, _, _) = FindBest(shot, tpl, px, py, pw, ph, 1.0, expectX, expectY, minScore);
                if (sc0 >= minScore)
                {
                    score = sc0; mx = x0; my = y0;
                    roiX = px; roiY = py;          // 让后面的坐标换算用同一个原点
                    if (LogSwitch.Enabled)
                        Safe.Note($"CLICK still-there 命中（相似度 {sc0:F2}，省掉整段搜索）");
                }
            }
        }

        // 三段式：① 原尺寸、近处快查（~0.1s）；② 换几个尺度（DPI/缩放变化）；
        //         ③ 仍失败就放宽搜索半径（布局挪动）。三段都失败才放弃。
        if (score < minScore)
        {
        var (s1, x1, y1, sec1, sx1, sy1) = roiUsable
            ? FindBest(shot, tpl, roiX, roiY, roiW, roiH, 1.0, expectX, expectY, minScore)
            : (0.0, 0, 0, 0.0, 1.0, 1.0);
        score = s1; mx = x1; my = y1; sx = sx1; sy = sy1; second = sec1;
        }
        if (score < minScore)
        {
            foreach (var k in Rescales)
            {
                var (s2, x2, y2, sec2, _, _) = FindBest(shot, tpl, roiX, roiY, roiW, roiH, k, expectX, expectY, minScore);
                if (s2 > score) { score = s2; mx = x2; my = y2; second = sec2; sx = k; sy = k; }
                if (score >= minScore) break;   // ★ 与判定同源（原来写死 MinScore=0.80，门槛调到 0.9 时会提前停）
            }
        }
        if (score < minScore)
        {
            // 放宽半径再试一次（只按原尺寸，避免太慢）
            var wx = Math.Clamp(expectX - WideRadius, 0, Math.Max(0, w - tpl.Width));
            var wy = Math.Clamp(expectY - WideRadius, 0, Math.Max(0, h - tpl.Height));
            var ww = Math.Min(w - wx, WideRadius * 2 + tpl.Width);
            var wh = Math.Min(h - wy, WideRadius * 2 + tpl.Height);
            if (ww > tpl.Width && wh > tpl.Height)
            {
                var (s3, x3, y3, sec3, _, _) = FindBest(shot, tpl, wx, wy, ww, wh, 1.0, expectX, expectY, minScore);
                if (s3 > score) { score = s3; mx = x3; my = y3; second = sec3; roiX = wx; roiY = wy; }
            }
        }
        // ★ 第 4 段：全宽粗搜 + 精修（换个模型后 ✨ 会横向漂移，固定搜索窗装不下）
        if (score < minScore)
        {
            // 搜索范围由设置决定：0=不做粗搜（只信局部），1=窗口下部，2=整窗
        var scope = Settings.ClickSearchScope;
        var (cf, cfx, cfy) = scope == 0
            ? (0.0, 0, 0)
            : CoarseFind(shot, tpl, minScore, scope == 2 ? 0.0 : 0.55);
            if (cf >= minScore)
            {
                score = cf; mx = cfx; my = cfy;
                roiX = 0; roiY = 0; sx = 1.0; sy = 1.0;   // 已经是窗口绝对坐标
                if (LogSwitch.Enabled)
                    Safe.Note($"CLICK 粗搜命中（相似度 {cf:F2}）—— 目标换了位置（换了模型？）");
            }
            else if (cf > score) { score = cf; }
        }

        if (score < minScore)
            return $"没找到图标（最高相似度 {score:F2} < 门槛 {minScore:F2}" +
                   (second > 0 ? $"，次佳 {second:F2}" : "") +
                   "）。界面可能变了，重新采集一次；或把「匹配门槛」调低一档再试。";

        // ★ 唯一性校验（2026-10-19）：分数不高时必须**明显领先次佳**，否则很可能是
        //   "整片区域都像"（模板选得太泛，比如只截到背景）——这种情况下点下去就是误点。
        //   高分（≥0.85）不设此限，因为 0.85+ 本身已足够可信。
        if (score < 0.85 && second > 0 && score - second < 0.08)
            return $"找到相似区域但**不确定**（最高 {score:F2}、次佳 {second:F2}，差距 {score - second:F2} 太小）。" +
                   "像是模板选得太泛 —— 请把鼠标对准图标**更中心**处重新采集。";

        // 命中点 = 模板左上角 + 模板中心
        // 用"实际命中的模板"尺寸换算中心（缩放后模板边长会变）
        var cx = r.L + roiX + mx + (int)Math.Round(Settings.ClickTemplateX * sx);
        var cy = r.T + roiY + my + (int)Math.Round(Settings.ClickTemplateY * sy);
        // 记住"点之前光标在哪"（通常就是输入框 —— 点回去能把焦点还给它）
        var hadOrigin = GetCursorPos(out var before);

        if (!SetCursorPos(cx, cy))
            return $"找到了图标 ({cx},{cy})，但**移动光标失败**（被系统拒绝）。";

        System.Threading.Thread.Sleep(30);
        // ★ SendInput 的返回值必须检查：前台是**更高权限**窗口时（UIPI）会被静默丢弃，
        //   以前不看返回值 ⇒ 明明没点上也报"已单击"，是最坏的一种假成功。
        var down = MouseInjector.Left(true);
        System.Threading.Thread.Sleep(40);
        var up = MouseInjector.Left(false);
        if (!down || !up)
            return $"找到图标 ({cx},{cy})，但**点击被系统拒绝**（前台窗口权限更高）。" +
                   "请让目标程序与本源程序（或目标程序）以相同权限运行。";

        // 记住命中点（窗口内坐标）—— 下次先做 still-there 预检
        Settings.SetClickLast(cx - r.L, cy - r.T);   // 只落盘一次（原来 3 次）

        var scaleNote = Math.Abs(sx - 1.0) < 0.01 ? "" : $"，缩放 {sx:F2}×";
        var tail = "";

        // ★ 回到原位再点一下（把焦点还给输入框，否则之后的 Enter 发不出去）
        if (Settings.ClickReturnToOrigin && hadOrigin &&
            (Math.Abs(before.X - cx) > 4 || Math.Abs(before.Y - cy) > 4))
        {
            System.Threading.Thread.Sleep(120);          // 让刚才那一下先落到目标上
            SetCursorPos(before.X, before.Y);
            System.Threading.Thread.Sleep(60);
            var d2 = MouseInjector.Left(true);
            System.Threading.Thread.Sleep(30);
            var u2 = MouseInjector.Left(false);
            tail = (d2 && u2)
                ? $"，并已回到原位 ({before.X},{before.Y}) 点了一下（恢复焦点）"
                : "，但回原位点击被系统拒绝";
        }

        return $"已在 ({cx},{cy}) 单击（相似度 {score:F2}{scaleNote}）{tail}。";
    }

    private static string ProcOf(IntPtr h)
    {
        try
        {
            GetWindowThreadProcessId(h, out var pid);
            return pid == 0 ? "" : System.Diagnostics.Process.GetProcessById((int)pid).ProcessName;
        }
        catch { return ""; }
    }

    private static IntPtr FindByClassAndProcess(string cls, string procName)
    {
        var found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            var sb = new System.Text.StringBuilder(256);
            GetClassNameW(h, sb, sb.Capacity);
            if (sb.ToString() != cls) return true;
            if (!IsWindowVisible(h)) return true;
            if (ProcOf(h) == procName) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr l);

    // ── 灰度归一化互相关（NCC）──
    // 为什么不用 OpenCV：只需要一个 44×44 模板在一个小窗口里找，纯托管实现足够快（~50ms），
    // 少一个几十 MB 的依赖，也不会因为打包漏 DLL 而静默失效。
    /// <summary>
    /// **全宽粗搜 + 精修**（2026-10-19，为"模型名旁边的 ✨"而加）。
    ///
    /// ## 要解决的问题
    ///
    /// 那个「增强提示词」✨ 长在**模型名旁边**，而模型名会变（Deepseek / Claude / GPT…），
    /// 名字长短不同 ⇒ ✨ 的横向位置会**漂几十到几百像素**。只按"采集时的位置 ±120px"去搜，
    /// 换个模型就搜不到。
    ///
    /// ## 做法（金字塔）
    ///
    /// 1. 把窗口**下部**(倒数 45% 高度、**全宽**)缩到 **1/2**，模板也缩到 1/2；
    /// 2. 在这个缩小图上粗扫一遍（宽松门槛 0.55），拿到"大致在哪"；
    /// 3. 映射回原分辨率，在 ±16px 的小窗口里**全分辨率精修**，用正式门槛判定。
    ///
    /// 代价：粗扫约 100~200 ms —— 只在前面所有精搜都失败时才走这一段，
    /// 属于"换个模型/窗口刚动过"的兜底路径。
    /// </summary>
    private static (double score, int x, int y) CoarseFind(
        Bitmap shot, Bitmap tpl, double minScore, double bandTopRatio)
    {
        const double k = 0.5;                       // 降采样倍率
        var bandTop = Math.Max(0, (int)(shot.Height * bandTopRatio));   // 0.55 = 只看下部 45%；0 = 全窗
        var bw = shot.Width;
        var bh = shot.Height - bandTop;
        if (bh < tpl.Height * 2 || bw < tpl.Width * 2) return (0, 0, 0);

        using var band = shot.Clone(new Rectangle(0, bandTop, bw, bh), shot.PixelFormat);
        var sw = Math.Max(8, (int)(bw * k));
        var sh = Math.Max(8, (int)(bh * k));
        var tw = Math.Max(8, (int)(tpl.Width * k));
        var th = Math.Max(8, (int)(tpl.Height * k));

        using var small = new Bitmap(sw, sh, PixelFormat.Format32bppArgb);
        using var smallTpl = new Bitmap(tw, th, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            g.DrawImage(band, new Rectangle(0, 0, sw, sh));
        }
        using (var g = Graphics.FromImage(smallTpl))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            g.DrawImage(tpl, new Rectangle(0, 0, tw, th));
        }

        var (sc, sx, sy, _) = Match(small, smallTpl, -1, -1, 0.55);   // 粗扫放宽   // expX/Y = -1 ⇒ 不做距离偏置
        if (sc < 0.55) return (0, 0, 0);

        // 映射回原分辨率，±16px 精修
        var gx = (int)(sx / k);
        var gy = (int)(sy / k) + bandTop;
        var rx = Math.Max(0, gx - 16);
        var ry = Math.Max(0, gy - 16);
        var rw = Math.Min(shot.Width - rx, 32 + tpl.Width);
        var rh = Math.Min(shot.Height - ry, 32 + tpl.Height);
        if (rw <= tpl.Width || rh <= tpl.Height) return (0, 0, 0);

        var (fine, fx, fy, _, _, _) = FindBest(shot, tpl, rx, ry, rw, rh, 1.0, gx, gy, minScore);
        return fine >= minScore ? (fine, rx + fx, ry + fy) : (fine, 0, 0);
    }

    /// <summary>
    /// 在 <paramref name="roiX/Y/W/H"/> 指定的区域内、按 <paramref name="scale"/> 缩放模板后匹配一次。
    /// 返回的 (x,y) 是**相对 ROI** 的坐标；(sx,sy) 是该次实际使用的缩放比。
    /// </summary>
    private static (double score, int x, int y, double second, double sx, double sy)
        FindBest(Bitmap shot, Bitmap tpl, int roiX, int roiY, int roiW, int roiH, double scale,
                 int anchorX, int anchorY, double minScore)
    {
        Bitmap use = tpl;
        bool own = false;
        if (Math.Abs(scale - 1.0) > 0.01)
        {
            var nw = Math.Max(8, (int)Math.Round(tpl.Width * scale));
            var nh = Math.Max(8, (int)Math.Round(tpl.Height * scale));
            use = new Bitmap(nw, nh, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(use))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(tpl, new Rectangle(0, 0, nw, nh));
            }
            own = true;
        }
        if (use.Width > roiW || use.Height > roiH)
        {
            if (own) use.Dispose();
            return (0, 0, 0, 0, scale, scale);
        }
        using var roi = shot.Clone(new Rectangle(roiX, roiY, roiW, roiH), shot.PixelFormat);
        var expX = anchorX - (int)Math.Round(Settings.ClickTemplateX * scale) - roiX;
        var expY = anchorY - (int)Math.Round(Settings.ClickTemplateY * scale) - roiY;
        var (sc, x, y, sec) = Match(roi, use, expX, expY, minScore);
        if (own) use.Dispose();
        return (sc, x, y, sec, scale, scale);
    }

    private static (double score, int x, int y, double second) Match(
        Bitmap big, Bitmap tpl, int expX, int expY, double minScore)
    {
        var bw = big.Width; var bh = big.Height;
        var tw = tpl.Width; var th = tpl.Height;
        var bd = ToGray(big); var td = ToGray(tpl);

        // 模板均值/方差
        double tm = 0;
        for (int i = 0; i < td.Length; i++) tm += td[i];
        tm /= td.Length;
        double tv = 0;
        for (int i = 0; i < td.Length; i++) { var d = td[i] - tm; tv += d * d; }
        if (tv <= 1e-9) return (0, 0, 0, 0);   // 纯色模板没意义

        double best = double.NegativeInfinity; int bx = 0, by = 0;
        // 次佳：用于"唯一性校验" —— 分数不高但**明显领先第二名**时才算可信
        double second = double.NegativeInfinity;
        // ★ 同形图标消歧：工具栏上可能有多个长得一样的按钮（实测会把第一个当命中）。
        //   收集所有达标的候选，最后选"离采集时那个位置最近"的一个。
        var cands = new List<(double s, int x, int y)>();
        for (int y = 0; y <= bh - th; y++)
        {
            for (int x = 0; x <= bw - tw; x++)
            {
                double sum = 0, sum2 = 0, cross = 0;
                for (int ty = 0; ty < th; ty++)
                {
                    var bo = (y + ty) * bw + x;
                    var to = ty * tw;
                    for (int tx = 0; tx < tw; tx++)
                    {
                        var b = bd[bo + tx];
                        sum += b; sum2 += b * b;
                        cross += b * td[to + tx];
                    }
                }
                var n = tw * th;
                var mean = sum / n;
                var varB = sum2 - sum * mean;    // = Σ(b-mean)²
                if (varB <= 1e-9) continue;
                // NCC = (Σ b·t - n·mean·tm) / sqrt(varB · tv)
                var ncc = (cross - n * mean * tm) / Math.Sqrt(varB * tv);
                if (ncc > best) { second = best; best = ncc; bx = x; by = y; }
                else if (ncc > second && (Math.Abs(x - bx) > 8 || Math.Abs(y - by) > 8)) second = ncc;
                if (ncc >= minScore && cands.Count < 400) cands.Add((ncc, x, y));
            }
        }

        // 消歧：分数接近最高（差 ≤0.02）的候选里，挑离"采集时的位置"最近的那个。
        // ★ expX < 0 是"不偏置"哨兵（粗搜用）—— 否则会被误当成"偏向左上角"。
        if (cands.Count > 1 && expX >= 0)
        {
            var top = double.NegativeInfinity;
            foreach (var c in cands) if (c.s > top) top = c.s;
            var pick = cands[0];
            var bestDist = int.MaxValue;
            foreach (var c in cands)
            {
                if (c.s < top - 0.02) continue;
                var d = Math.Abs(c.x - expX) + Math.Abs(c.y - expY);
                if (d < bestDist) { bestDist = d; pick = c; }
            }
            return (pick.s, pick.x, pick.y, second);
        }
        return (best, bx, by, second);
    }

    private static float[] ToGray(Bitmap bmp)
    {
        var w = bmp.Width; var h = bmp.Height;
        var outA = new float[w * h];
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var buf = new byte[data.Stride * h];
            Marshal.Copy(data.Scan0, buf, 0, buf.Length);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    var o = y * data.Stride + x * 4;
                    // BT.601 亮度
                    outA[y * w + x] = 0.114f * buf[o] + 0.587f * buf[o + 1] + 0.299f * buf[o + 2];
                }
        }
        finally { bmp.UnlockBits(data); }
        return outA;
    }

    private static string ToBase64(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return Convert.ToBase64String(ms.ToArray());
    }

    private static Bitmap? FromBase64(string b64)
    {
        try
        {
            using var ms = new MemoryStream(Convert.FromBase64String(b64));
            return new Bitmap(ms);
        }
        catch { return null; }
    }
}
