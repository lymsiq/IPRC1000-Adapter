using System;

namespace Iprc1000Adapter;

/// <summary>
/// 日志总开关（发布版**默认关闭**）。
///
/// 为什么默认关（用户明确要求"发包程序不保留日志，速度只为快和稳定"）：
///   本程序一度在关键路径上同步写 `key-events.log`（每行 0.253 ms，一次按键 5~8 行，
///   **在注入之前**）。现在虽然改成了异步队列，但"完全不写"才是最省的做法 ——
///   文件读写会随文件变大、杀毒实时扫描而变慢变抖。
///
/// 关的是什么（**文本日志**）：
///   key-events.log · bridge.log · router.log · webview.log · stuck-guard.log · mouse.log
///
/// **不关**的是什么（这些不是日志，是运行需要的东西）：
///   `keymap.json` / `settings.json` / `mouse.json` —— 配置
///   `bridge-stats.json` —— 界面「语音桥」面板与"按键链路探针"要读它；
///     它由桥的**后台统计线程每 2 秒写一次**，不在按键/音频路径上，实测开销约 0.03% CPU。
///   ⚠ 它也是今天那次"按键全失效"能被 10 秒内发现的前提 —— 删了面板和探针一起瞎。
///
/// 想排查时怎么打开：
///   ① 环境设置页勾上「写入诊断日志」；或 ② 设环境变量 `IPRC1000_LOG=on`。
/// </summary>
internal static class LogSwitch
{
    private static bool? _enabled;

    /// <summary>是否写文本日志。默认 false（发布版性能优先）。</summary>
    public static bool Enabled
    {
        get
        {
            if (_enabled.HasValue) return _enabled.Value;
            // 环境变量优先级最高（临时排查用，不动配置）
            var env = Environment.GetEnvironmentVariable("IPRC1000_LOG");
            if (!string.IsNullOrWhiteSpace(env))
            {
                _enabled = env.Equals("on", StringComparison.OrdinalIgnoreCase)
                           || env.Equals("1", StringComparison.OrdinalIgnoreCase);
                return _enabled.Value;
            }
            try { _enabled = Settings.LogEnabled; }
            catch { _enabled = false; }
            return _enabled.Value;
        }
        set => _enabled = value;
    }

    /// <summary>界面开关用（写进 settings.json）</summary>
    public static void Set(bool on)
    {
        _enabled = on;
        Settings.LogEnabled = on;
    }
}
