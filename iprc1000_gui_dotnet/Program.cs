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
}
