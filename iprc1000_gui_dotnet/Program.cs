using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;

namespace Iprc1000Adapter;

internal static class Program
{
    /// <summary>
    /// 进程启动的**真实**时刻基准 —— 所有 `[T+xxx ms]` 打点都相对它。
    ///
    /// 为什么不用一个 `static readonly Stopwatch`：静态字段是**惰性初始化**的，
    /// 第一次被读到才启动。实测就踩到了 —— 首次打点显示 `T+0 ms`，
    /// 而那一刻进程其实已经跑了 40 多毫秒，"启动前 40 ms"被整段抹掉了。
    /// `Process.StartTime` 是内核给的进程创建时间，不受"我们何时开始计时"影响。
    /// 万一读不到（受限环境），退回一个进程内 Stopwatch。
    /// </summary>
    internal static long StartupMs
    {
        get
        {
            try
            {
                return (long)(DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
            }
            catch { return _fallbackSw.ElapsedMilliseconds; }
        }
    }

    private static readonly Stopwatch _fallbackSw = Stopwatch.StartNew();

    [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint ms);

    [STAThread]
    private static void Main(string[] args)
    {
        // 让静态基准立刻就位（懒初始化会推迟它的起点）
        _ = StartupMs;
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

        // 多键同时按住回归自检：--inject-multikey-test [输出文件]
        //   直接判「会不会卡键」—— 复现「按住A→再按B→先松A→再松B」那串，
        //   旧实现里 A 的键再也不会被释放。见 KeyFilter.InjectMultiKeyTest 的注释。
        if (args.Length > 0 && args[0] == "--inject-multikey-test")
        {
            var pm = args.Length > 1 ? args[1] : Path.Combine(Env.AppDir, "inject_multikey.txt");
            File.WriteAllText(pm, KeyFilter.InjectMultiKeyTest());
            return;
        }

        // 鼠标注入实测探针：--mouse-probe [输出文件]
        //   回答实现前必须先知道的三个数：系统指针加速是否开、相对移动的实测映射（1:1?）、
        //   SendInput 单次开销。测完光标会还原。
        if (args.Length > 0 && args[0] == "--mouse-probe")
        {
            var pa = args.Length > 1 ? args[1] : Path.Combine(Env.AppDir, "mouse_probe.txt");
            File.WriteAllText(pa, MouseInjector.Probe());
            return;
        }

        // 鼠标引擎自检：--mouse-engine-test [输出文件]
        //   不需要遥控器，直接驱动引擎并量真实光标：增速是否生效、松开是否停住、
        //   确认键是否真的按下系统左键、斜向是否归一化。
        if (args.Length > 0 && args[0] == "--mouse-engine-test")
        {
            var pe = args.Length > 1 ? args[1] : Path.Combine(Env.AppDir, "mouse_engine_test.txt");
            File.WriteAllText(pe, MouseEngine.SelfTest());
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

        // 映射一致性自检：--map-dump <输出文件> [组序号]
        //   把「界面显示的映射」「实际注入的映射」「出厂默认」三列并排打印。
        //   历史 bug：注入走 Keys.Load()（旧扁平格式 → 静默回落到出厂默认），与界面不一致。
        //   给组序号可单独自检某一组（如 --map-dump out.txt 1 看 WorkBuddy 组）。
        if (args.Length > 0 && args[0] == "--map-dump")
        {
            var pd = args.Length > 1 ? args[1] : Path.Combine(Env.AppDir, "map_dump.txt");
            var gi = args.Length > 2 && int.TryParse(args[2], out var g) ? g : Keymaps.ActiveIndex;
            File.WriteAllText(pd, DumpMaps(gi));
            return;
        }

        // ── 单实例保护 ──
        // 为什么必须有：**每份实例都会创建自己的虚拟键盘设备**。若其中一份在"按住某键"时
        // 被结束（任务管理器结束进程 / 崩溃），那个键就留在**它那台**设备的按下状态里，
        // 而新实例用的是**另一台**虚拟键盘，管不到它 → 系统层表现为"这个键卡住了"。
        // 第二份实例的行为：通知已在运行的那份把窗口显示出来，然后自己退出。
        // ⚠ 必须放在上面那些 CLI 分支**之后** —— --inject-test / --map-dump 这类诊断入口
        //    要能在程序正常运行期间照常跑（它们不该被"已在运行"挡掉）。
        Mutex? singleInstance = null;
        try
        {
            singleInstance = new Mutex(true, MainForm.SingleInstanceMutex, out var createdNew);
            if (!createdNew)
            {
                try { EventWaitHandle.OpenExisting(MainForm.ShowWindowEvent).Set(); }
                catch (Exception e)
                {
                    MessageBox.Show(
                        "IPRC1000 遥控器适配器已经在运行了。\n\n" +
                        "（想唤出它的窗口时失败了：" + e.Message + "）\n" +
                        "请到系统托盘双击图标打开。",
                        Env.AppName);
                }
                return;
            }
        }
        catch (Exception e)
        {
            // 拿不到互斥体不该阻止启动，但必须留痕（否则以后查不出"为什么开了两份"）
            MainForm.StartupMark("单实例保护不可用（已忽略）：" + e.Message);
        }

        // WebView2 需要显式 STA + 现代 WinForms 初始化
        ApplicationConfiguration.Initialize();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        MainForm.StartupMark("WinForms 初始化完成");

        // ★ 全局异常兜底：**selftest 模式也必须装**。
        //   以前 selftest 分支不装 —— 结果 InitWebView（async void 调用）一旦抛异常，
        //   进程直接以 0xE0434352 崩掉，报告、轨迹什么都没有，排查时完全无从下手。
        //   现在无论哪种模式，未捕获异常都会写成一份带堆栈的报告。
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            var txt = new System.Text.StringBuilder();
            txt.AppendLine("{");
            txt.AppendLine("  \"fatal\": true,");
            txt.AppendLine($"  \"type\": \"{ex?.GetType().Name ?? "Unknown"}\",");
            txt.AppendLine($"  \"message\": \"{(ex?.Message ?? "").Replace("\\", "/").Replace("\"", "'")}\",");
            txt.AppendLine($"  \"isTerminating\": {e.IsTerminating.ToString().ToLowerInvariant()},");
            txt.AppendLine("  \"stack\": \"" +
                (ex?.ToString() ?? "").Replace("\\", "/").Replace("\"", "'")
                    .Replace("\r", "").Replace("\n", " | ") + "\"");
            txt.AppendLine("}");
            var path = selftest
                ? (outPath ?? Path.Combine(Env.AppDir, "selftest.json"))
                : Path.Combine(Env.AppDir, "crash.json");
            try { File.WriteAllText(path, txt.ToString()); } catch { }
        };

        if (!selftest)
        {
            Application.ThreadException += (_, e) => MessageBox.Show(e.Exception.Message, Env.AppName);
        }

        using var form = new MainForm(selftest, outPath);
        MainForm.StartupMark("主窗口构造完成");
        Application.Run(form);
        GC.KeepAlive(singleInstance);   // 互斥体必须活到进程结束，否则保护会提前失效
    }

    /// <summary>
    /// 映射自检：把「界面显示」「实际注入」「出厂默认」三条数据并排打印。
    ///
    /// 为什么要有这个：本项目曾经出过最严重的一类 bug —— **同一个东西有两个来源**。
    ///   · 界面走 Keymaps、注入走 Keys.Load()（旧扁平格式，静默回落硬编码默认）
    ///     → 用户改了映射，界面变了，实际注入却还是出厂值，怎么试都没反应。
    ///   · 默认值也曾同时写在 Keys.Table 和 Defaults 两处 → 必然漂移。
    /// 现在三者统一，本命令就是那道"必须一致"的闸门。
    /// </summary>
    private static string DumpMaps(int groupIdx)
    {
        if (groupIdx < 0 || groupIdx >= Keymaps.Groups.Count) groupIdx = Keymaps.ActiveIndex;
        var grp = Keymaps.Groups[groupIdx];
        var map = Keymaps.MapOf(groupIdx);
        var def = Keymaps.FactoryOf(groupIdx);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"IPRC1000 映射自检   {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"配置文件：%APPDATA%\\{Env.AppName}\\keymap.json");
        sb.AppendLine($"自检配置组：#{groupIdx} 「{grp.Name}」（Kind={grp.Kind}）" +
                      (groupIdx == Keymaps.ActiveIndex ? "  ← 当前生效" : ""));
        sb.AppendLine("共 " + Keymaps.Groups.Count + " 组：" +
            string.Join(" / ", Keymaps.Groups.Select((g, i) => $"#{i} {g.Name}[{g.Kind}]")));
        sb.AppendLine();
        sb.AppendLine($"{"按键",-14}{"HID",-7}{"实际注入",-18}{"界面显示",-18}{"出厂默认",-18}{"注入=界面",-10}{"=默认"}");
        sb.AppendLine(new string('-', 88));

        var bad = 0; var diffDef = 0;
        foreach (var b in Keys.Table)
        {
            // ★ 两条路径**分开写**：日后任何一方被改成别的数据源，这里立刻暴露
            var inject = map.TryGetValue(b.Id, out var iv) ? iv : Array.Empty<int>();   // ApplyButton 走这条
            var shown = map.TryGetValue(b.Id, out var sv) ? sv : Array.Empty<int>();    // GetState 走这条
            var dflt = def.TryGetValue(b.Id, out var dv) ? dv : Array.Empty<int>();
            var rawInj = Keys.FormatSymbols(inject);
            var rawSho = Keys.FormatSymbols(shown);
            var rawDef = Keys.FormatSymbols(dflt);
            // ★ 比较用**原始值**，展示才用占位文案 ——
            //   否则空映射时「不注入」vs「不执行」两个占位串不同，会误报不一致。
            var same = rawInj == rawSho;
            var eqDef = rawInj == rawDef;
            if (!same) bad++;
            if (!eqDef) diffDef++;
            sb.AppendLine($"{b.Id,-14}{b.Hid,-7}" +
                          $"{Or(rawInj, "（不注入）"),-18}{Or(rawSho, "（不执行）"),-18}" +
                          $"{Or(rawDef, "（不执行）"),-18}" +
                          $"{(same ? "OK" : "★不一致★"),-10}{(eqDef ? "OK" : "≠")}");
        }
        sb.AppendLine();
        sb.AppendLine(bad == 0
            ? "⇒ 注入 == 界面 ✓（注入用的就是界面显示的映射）"
            : $"⇒ ✗ 有 {bad} 处「注入 ≠ 界面」—— 两条路径读了不同的数据源！");
        sb.AppendLine(diffDef == 0
            ? $"⇒ 第 {groupIdx} 组 == 其出厂默认 ✓（已冻结为最终版）"
            : $"⇒ 第 {groupIdx} 组与出厂默认有 {diffDef} 处不同（用户自定义，属正常；" +
              "若想冻结为默认请更新 Defaults）");
        return sb.ToString();
    }

    private static string Or(string s, string fallback) => s.Length > 0 ? s : fallback;
}
