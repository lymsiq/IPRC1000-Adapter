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
/// - 匹配阈值 0.72，低于它就不点（宁可不动，也不要在错误位置乱点）。
/// </summary>
internal static class TemplateClick
{
    private const int LearnSize = 44;      // 采集边长
    private const double MinScore = 0.72;  // 置信度门槛
    private const int MaxW = 3000, MaxH = 1600; // 窗口尺寸上限（防御异常窗口）
    /// <summary>在采集位置附近多大范围内找（±像素）。大窗口全遍历要几秒，收窄到这块足够快。</summary>
    private const int SearchRadius = 120;
    /// <summary>失败时先试这些缩放比（应对 DPI / 窗口缩放 / 主题字号变化）</summary>
    private static readonly double[] Rescales = { 0.80, 0.90, 1.10, 1.25 };
    private const int WideRadius = 300;   // 全缩放都失败后，再放宽一次搜索半径

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
        }
        return $"已采集 {LearnSize}×{LearnSize} 图标（进程 {procName} / 窗口 `{cls}`）。" +
               "请立刻点「试一下截图点击」验证：
" +
               "· 相似度 ≥0.90 → 很好；0.72~0.85 → 勉强可用，建议把鼠标对准图标更中心处重采；
" +
               "· 若采集时图标处于**悬停高亮**状态，请把鼠标挪开再回来重新采集（高亮态和常态长得不一样）。";
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
        var roiX = Math.Max(0, Settings.ClickRelX - SearchRadius);
        var roiY = Math.Max(0, Settings.ClickRelY - SearchRadius);
        var roiW = Math.Min(w - roiX, SearchRadius * 2 + tpl.Width);
        var roiH = Math.Min(h - roiY, SearchRadius * 2 + tpl.Height);
        if (roiW <= tpl.Width || roiH <= tpl.Height)
            return $"窗口太小或采集位置超出（{w}×{h}），请重新采集。";

        // 三段式：① 原尺寸、近处快查（~0.1s）；② 换几个尺度（DPI/缩放变化）；
        //         ③ 仍失败就放宽搜索半径（布局挪动）。三段都失败才放弃。
        var (score, mx, my, sx, sy, _) = FindBest(shot, tpl, roiX, roiY, roiW, roiH, 1.0);
        if (score < MinScore)
        {
            foreach (var k in Rescales)
            {
                var (s2, x2, y2, _, _, _) = FindBest(shot, tpl, roiX, roiY, roiW, roiH, k);
                if (s2 > score) { score = s2; mx = x2; my = y2; sx = k; sy = k; }
                if (score >= MinScore) break;
            }
        }
        if (score < MinScore)
        {
            // 放宽半径再试一次（只按原尺寸，避免太慢）
            var wx = Math.Max(0, Settings.ClickRelX - WideRadius);
            var wy = Math.Max(0, Settings.ClickRelY - WideRadius);
            var ww = Math.Min(w - wx, WideRadius * 2 + tpl.Width);
            var wh = Math.Min(h - wy, WideRadius * 2 + tpl.Height);
            if (ww > tpl.Width && wh > tpl.Height)
            {
                var (s3, x3, y3, _, _, _) = FindBest(shot, tpl, wx, wy, ww, wh, 1.0);
                if (s3 > score) { score = s3; mx = x3; my = y3; roiX = wx; roiY = wy; }
            }
        }
        if (score < MinScore)
            return $"没找到图标（最高相似度 {score:F2} < {MinScore:F2}）。界面可能变了，重新采集一次。";

        // 命中点 = 模板左上角 + 模板中心
        // 用"实际命中的模板"尺寸换算中心（缩放后模板边长会变）
        var cx = r.L + roiX + mx + (int)Math.Round(Settings.ClickTemplateX * sx);
        var cy = r.T + roiY + my + (int)Math.Round(Settings.ClickTemplateY * sy);
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

        var scaleNote = Math.Abs(sx - 1.0) < 0.01 ? "" : $"，缩放 {sx:F2}×";
        return $"已在 ({cx},{cy}) 单击（相似度 {score:F2}{scaleNote}）。";
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
    /// 在 <paramref name="roiX/Y/W/H"/> 指定的区域内、按 <paramref name="scale"/> 缩放模板后匹配一次。
    /// 返回的 (x,y) 是**相对 ROI** 的坐标；(sx,sy) 是该次实际使用的缩放比。
    /// </summary>
    private static (double score, int x, int y, double sx, double sy, Bitmap usedTpl)
        FindBest(Bitmap shot, Bitmap tpl, int roiX, int roiY, int roiW, int roiH, double scale)
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
            return (0, 0, 0, scale, scale, tpl);
        }
        using var roi = shot.Clone(new Rectangle(roiX, roiY, roiW, roiH), shot.PixelFormat);
        var expX = Settings.ClickRelX - (int)Math.Round(Settings.ClickTemplateX * scale) - roiX;
        var expY = Settings.ClickRelY - (int)Math.Round(Settings.ClickTemplateY * scale) - roiY;
        var (sc, x, y) = Match(roi, use, expX, expY);
        if (own) use.Dispose();
        return (sc, x, y, scale, scale, tpl);
    }

    private static (double score, int x, int y) Match(Bitmap big, Bitmap tpl, int expX, int expY)
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
        if (tv <= 1e-9) return (0, 0, 0);   // 纯色模板没意义

        double best = double.NegativeInfinity; int bx = 0, by = 0;
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
                if (ncc > best) { best = ncc; bx = x; by = y; }
                if (ncc >= MinScore && cands.Count < 400) cands.Add((ncc, x, y));
            }
        }

        // 消歧：分数接近最高（差 ≤0.02）的候选里，挑离"采集时的位置"最近的那个。
        if (cands.Count > 1)
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
            return pick;
        }
        return (best, bx, by);
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
