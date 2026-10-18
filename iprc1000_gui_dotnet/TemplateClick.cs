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
    private const int MaxW = 1200, MaxH = 600; // 搜索区域上限（防御异常窗口尺寸）

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
            Settings.ClickTemplate = ToBase64(bmp);
        }

        // 光标所在处 → 顶层窗口的类名（DirectUI 的子窗口可能不是它，所以取 root）
        var hwnd = WindowFromPoint(new POINT { X = p.X, Y = p.Y });
        var root = GetAncestor(hwnd, GA_ROOT);
        var sb = new System.Text.StringBuilder(256);
        GetClassNameW(root, sb, sb.Capacity);
        cls = sb.ToString();

        Settings.ClickWindowClass = cls;
        Settings.ClickTemplateX = p.X - x;   // 模板中心相对模板左上角（一般就是 half）
        Settings.ClickTemplateY = p.Y - y;

        // 记住"图标在窗口里的相对位置" —— 工具栏上可能有多个长得一样的图标，
        // 光靠模板会点错第一个，用它才能挑对。
        if (GetWindowRect(root, out var wr))
        {
            Settings.ClickRelX = p.X - wr.L;
            Settings.ClickRelY = p.Y - wr.T;
        }
        return $"已采集 {LearnSize}×{LearnSize} 图标（窗口类名 `{cls}`）。之后按动作键就会在它附近找并点击。";
    }

    /// <summary>触发：在目标窗口里找模板并点击。返回给人看的说明。</summary>
    public static string TryClick()
    {
        var b64 = Settings.ClickTemplate;
        var cls = Settings.ClickWindowClass;
        if (string.IsNullOrEmpty(b64) || string.IsNullOrEmpty(cls))
            return "还没采集图标：先在「按键映射」页把鼠标停在目标按钮上，点「采集图标」。";

        IntPtr hwnd;
        try { hwnd = FindWindowW(cls, null); }
        catch { hwnd = IntPtr.Zero; }
        if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd))
            return $"找不到可见的窗口 `{cls}`（目标界面没打开？）";

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

        var (score, mx, my) = Match(shot, tpl);
        if (score < MinScore)
            return $"没找到图标（最高相似度 {score:F2} < {MinScore:F2}）。界面可能变了，重新采集一次。";

        // 命中点 = 模板左上角 + 模板中心
        var cx = r.L + mx + Settings.ClickTemplateX;
        var cy = r.T + my + Settings.ClickTemplateY;
        SetCursorPos(cx, cy);
        System.Threading.Thread.Sleep(30);
        MouseInjector.Left(true);
        System.Threading.Thread.Sleep(40);
        MouseInjector.Left(false);
        return $"已在 ({cx},{cy}) 单击（相似度 {score:F2}）。";
    }

    // ── 灰度归一化互相关（NCC）──
    // 为什么不用 OpenCV：只需要一个 44×44 模板在一个小窗口里找，纯托管实现足够快（~50ms），
    // 少一个几十 MB 的依赖，也不会因为打包漏 DLL 而静默失效。
    private static (double score, int x, int y) Match(Bitmap big, Bitmap tpl)
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

        // 消歧：分数接近最高（差 ≤0.02）的候选里，挑离"采集时的相对位置"最近的那个。
        var expX = Settings.ClickRelX - Settings.ClickTemplateX;
        var expY = Settings.ClickRelY - Settings.ClickTemplateY;
        if (cands.Count > 1 && (Settings.ClickRelX > 0 || Settings.ClickRelY > 0))
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
