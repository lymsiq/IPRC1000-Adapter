namespace Iprc1000Adapter;

/// <summary>
/// 卡键判据 —— **纯函数，无任何外部依赖**，方便直接拿去跑单测。
///
/// 背景：v1 的看门狗盯的是「遥控器多久没上报」，而 ETW 模式下遥控器按住时
/// 根本不会持续上报（实测正常按住 30 秒，上报数 = 0），所以那条判据必然失效：
/// 丢一次 UP 就永久卡键，只能手动复位。
///
/// v2 改盯「我们注入了、但还没放开」的按钮，并用两条判据：
/// · 语音键 → **音频判据**。按住期间蓝牙 SCO 一定在传音频帧（实测按下 → 首包
///   0~528 ms，不说话也传静音帧），所以「按着 + 音频停了 &gt; 3 秒」= UP 丢了。
///   拿不到音频信号（桥没跑）时退化为绝对超时。
/// · 其它键 → 都是点按，按住超过 15 秒即视为卡死。
/// </summary>
internal static class StuckGuard
{
    /// <summary>
    /// 语音键宽限期：刚按下时 silentMs 还是上一会话的旧值，不能立刻判死。
    /// ★ 2026-10-18 用户要求再放宽到 **60 秒**实测（配合音频驱动释放 5 秒兜底，不影响卡键自愈）。
    /// </summary>
    public const double VoiceGraceSec = 60.0;

    /// <summary>
    /// 语音键按住期间，音频静默超过这个毫秒数才判 UP 丢了。
    ///
    /// ★★ 2026-10-18 真机修正：原来是 3000 ms，**误杀真实长按**。
    ///   实测音频流并不稳定（真机 bridge-stats 里出现过 `maxGapMs: 39635`，
    ///   即中途断流 39.6 秒）。断流一旦超过 3 秒，看门狗就把用户**正按着的**
    ///   右 Alt 强制放掉 ⇒ "长按"被打断成"点击" ⇒ 语音功能不触发。
    ///   ⇒ 2026-10-18 第二次修正（拿到铁证后）：真机 watchdog 日志显示
    ///     `GUARD voice 强制释放 原因=语音键按着但音频已停 3004 ms`，
    ///     而系统侧实测用户按了 **8192 / 6507 / 9347 ms** —— 也就是说
    ///     **每一次真实长按都在 3 秒时被误杀**，豆包只看到"3 秒就松手"，语音永不触发。
    ///   ⇒ 阈值改为 **30 秒**（宽限与静默同步）。取舍：宁可"真丢了 UP 多卡 30 秒"
    ///     （用户按一下紧急复位即可），也不能"每次都把正常长按砍断" ——
    ///     后者是**核心功能不可用**，前者只是偶发不适。
    /// </summary>
    public const long VoiceSilentMs = 60_000;

    /// <summary>拿不到音频信号时（桥没运行），语音键的绝对兜底超时（秒）。</summary>
    public const double VoiceNoSignalSec = 120;

    /// <summary>普通键（点按类）的绝对超时（秒）。</summary>
    public const double OtherSec = 15;

    /// <summary>
    /// 方向键的绝对超时（秒）。**必须比普通键长** —— 长按方向键连续滚菜单是真实用法
    /// （遥控器侧有键盘自动重复、但 ETW 只报一次 DOWN，所以这里只能靠绝对时间兜底）。
    /// </summary>
    public const double DirectionalSec = 60;

    /// <summary>长按合法的方向键。</summary>
    private static readonly HashSet<string> Directional = new() { "up", "down", "left", "right" };

    /// <summary>
    /// 判定某个「按住中」的按钮是否已经卡死。
    /// </summary>
    /// <param name="bid">按钮 id（"voice" / "back" / ...）</param>
    /// <param name="heldSec">已经按住多少秒</param>
    /// <param name="silentMs">音频静默多少毫秒；null = 拿不到音频信号</param>
    /// <returns>卡死原因；null = 正常，不要动它</returns>
    public static string? Decide(string bid, double heldSec, long? silentMs)
    {
        if (bid == "voice")
        {
            // ★ 先给音频起流留宽限。不留的话刚按下就会被旧 silentMs 误判成立刻放开。
            if (heldSec <= VoiceGraceSec) return null;

            if (silentMs is > VoiceSilentMs)
                return $"语音键按着但音频已停 {silentMs} ms —— 遥控器早已松开、UP 丢了";

            if (silentMs is null && heldSec > VoiceNoSignalSec)
                return $"语音键按住 {heldSec:F0} 秒且拿不到音频信号（桥未运行）";

            return null;
        }

        if (Directional.Contains(bid))
        {
            if (heldSec > DirectionalSec)
                return $"{bid} 按住 {heldSec:F0} 秒（方向键长按滚菜单也不可能按这么久）";
            return null;
        }

        if (heldSec > OtherSec)
            return $"{bid} 按住 {heldSec:F0} 秒（普通键是点按，不可能按这么久）";

        return null;
    }
}
