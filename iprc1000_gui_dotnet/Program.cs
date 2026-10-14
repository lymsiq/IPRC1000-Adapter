using System.Text.Json;
using System.Windows.Forms;

namespace Iprc1000Adapter;

internal static class Program
{
    [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint ms);

    [STAThread]
    private static void Main(string[] args)
    {
        // ★ 必须在任何分支之前：把所有 Sleep/等待的粒度从 ~15.6 ms 提到 1 ms。
        //   否则连发间隔会抖动 5~15 ms —— 这正是"不丝滑/不精确"的根因。
        TimeBeginPeriod(1);

        var selftest = args.Length > 0 && args[0] == "--selftest";
        var outPath = args.Length > 1 ? args[1] : null;


        // 注入自检：不需要窗口，直接验证「按下/松开右 Alt」是否被系统接受
        if (args.Length > 0 && args[0] == "--inject-test")
        {
            // WinExe 没有控制台，结果写文件（没给路径就写到程序目录）
            var p = outPath ?? Path.Combine(Env.AppDir, "inject_test.json");
            File.WriteAllText(p, KeyFilter.InjectSelfTest());
            return;
        }

        // 注入验证：--inject-verify <VK十六进制> <输出文件>
        if (args.Length > 1 && args[0] == "--inject-verify")
        {
            var p2 = args.Length > 2 ? args[2] : Path.Combine(Env.AppDir, "inject_verify.txt");
            var vk = Convert.ToInt32(args[1], 16);
            File.WriteAllText(p2, KeyFilter.InjectVerify(vk));
            return;
        }

        // 组合键验证：--inject-combo <5B,52> <输出文件>
        if (args.Length > 1 && args[0] == "--inject-combo")
        {
            var pc = args.Length > 2 ? args[2] : Path.Combine(Env.AppDir, "inject_combo.txt");
            var vks = args[1].Split(',').Select(x => Convert.ToInt32(x, 16)).ToArray();
            File.WriteAllText(pc, KeyFilter.InjectComboVerify(vks));
            return;
        }

        // 映射一致性自检：--map-dump <输出文件>
        //   把「界面显示的映射」（Keymaps，Backend 用）与「实际注入的映射」（同上，KeyFilter 用）
        //   并排打印，用来证明两者**读的是同一份数据**。
        //   历史 bug：注入走 Keys.Load()（旧扁平格式 → 静默回落到出厂默认），与界面不一致。
        if (args.Length > 0 && args[0] == "--map-dump")
        {
            var pd = args.Length > 1 ? args[1] : Path.Combine(Env.AppDir, "map_dump.txt");
            File.WriteAllText(pd, DumpMaps());
            return;
        }

        // WebView2 需要显式 STA + 现代 WinForms 初始化
        ApplicationConfiguration.Initialize();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        if (!selftest)
        {
            Application.ThreadException += (_, e) => MessageBox.Show(e.Exception.Message, Env.AppName);
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Console.WriteLine(e.ExceptionObject);
        }

        using var form = new MainForm(selftest, outPath);
        Application.Run(form);
    }

    /// <summary>
    /// 打印「界面显示」与「实际注入」两条路径各自解析出的映射，用于验证二者一致。
    /// 两列都用 Keymaps.Get(id)，若历史上注入走的是别的数据源，这里会立刻暴露。
    /// </summary>
    private static string DumpMaps()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"IPRC1000 映射自检   {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"配置文件：{Env.AppName}\\keymap.json");
        sb.AppendLine($"当前配置组：#{Keymaps.ActiveIndex} 「{Keymaps.Current.Name}」");
        sb.AppendLine($"共 {Keymaps.Groups.Count} 组：" +
            string.Join(" / ", Keymaps.Groups.Select((g, i) => $"#{i} {g.Name}")));
        sb.AppendLine();
        sb.AppendLine($"{"按键",-14}{"HID",-8}{"注入（实际）",-22}{"界面（显示）",-22}{"一致?"}");
        sb.AppendLine(new string('-', 78));

        var bad = 0;
        foreach (var b in Keys.Table)
        {
            // ★ 两条路径都取同一个源；这里刻意分开写，方便日后任何一方被改成别的数据源时立刻暴露
            var inject = Keymaps.Get(b.Id);                 // KeyFilter.ApplyButton 走这条
            var shown = Keymaps.Get(b.Id);                  // Backend.GetState 走这条
            var sInj = Keys.FormatSymbols(inject);
            var sSho = Keys.FormatSymbols(shown);
            var same = sInj == sSho;
            if (!same) bad++;
            sb.AppendLine($"{b.Id,-14}{b.Hid,-8}" +
                $"{(sInj.Length > 0 ? sInj : "（不注入）"),-22}" +
                $"{(sSho.Length > 0 ? sSho : "（不执行）"),-22}" +
                (same ? "OK" : "★不一致★"));
        }
        sb.AppendLine();
        sb.AppendLine(bad == 0
            ? "⇒ 全部一致：注入用的就是界面显示的映射 ✓"
            : $"⇒ 有 {bad} 处不一致 ✗（注入与界面读了不同的数据源）");
        return sb.ToString();
    }
}
