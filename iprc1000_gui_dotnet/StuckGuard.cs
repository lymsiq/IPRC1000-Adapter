namespace Iprc1000Adapter;

/// <summary>
/// 卡键判据 —— **纯函数，无任何外部依赖**，方便直接拿去跑单测。
///
/// 背景：v1 的看门狗盯的是「遥控器多久没上报」，而 ETW 模式下遥控器按住时
/// 根本不会持续上报（实测正常按住 30 秒，上报数 = 0），所以那条判据必然失效：
/// 丢一次 UP 就永久卡键，只能手动复位。
///
/// v2 改盯「我们注入了、但还没放开」的按钮，并用两条判据：
/// · 语音键 → **音频判据**。按住期间音频帧一直在传（**走 ACL 上的私有帧，不是 SCO** ——
///   2026-10-17 用 Linux btmon 实测过：全程 SCO 包 = 0），不说话时传的也是静音帧，
///   所以「按着 + 音频停了」= UP 丢了。
/// · 其它键 → 都是点按，按住超过 15 秒即视为卡死。
/// </summary>
internal static class StuckGuard
{
    /// <summary>
    /// 语音键宽限期：刚按下时 silentMs 还是上一会话的旧值，不能立刻判死。
    ///
    /// ★ 历史：60 秒 → **600 秒**（2026-10-30 按代码实际值订正注释，原来这里写着 60 秒）。
    ///   放宽的直接原因见 <see cref="VoiceSilentMs"/> 的说明。
    /// </summary>
    public const double VoiceGraceSec = 600.0;

    /// <summary>
    /// 语音键按住期间，音频静默超过这个毫秒数才判 UP 丢了。
    ///
    /// ★★ **这个值被放宽过三次，是"误杀正常长按"与"真丢 UP 后卡键"之间的拉锯**：
    ///   1. 3000 ms —— 真机 watchdog 日志抓到 `GUARD voice 强制释放 原因=语音键按着但音频已停 3004 ms`，
    ///      而系统侧实测用户按了 8192 / 6507 / 9347 ms ⇒ **每一次真实长按都在 3 秒被砍断**，
    ///      豆包只看到"3 秒就松手"，语音永不触发。
    ///   2. 30000 ms —— 仍然不够：用户反馈**说话超过 30 秒会被打断**。
    ///      原因同上：音频流本身会断流（真机 `bridge-stats` 出现过 `maxGapMs: 39635`，
    ///      中途断流 39.6 秒），30 秒与"正常一次长语音"的时长是重叠的。
    ///   3. **600_000 ms（10 分钟，现状）** —— 等于"只防彻底卡死，不防误杀"。
    ///
    /// ⚠ 现状的代价：真丢了 UP 要 10 分钟才自愈（用户按一下紧急复位更快）。
    ///   如果以后要收紧，**不能**只调小这个常数 —— 必须改成"静默 N 秒后**复查一次**，
    ///   仍然静默才释放"，否则断流型波动又会误杀。
    /// </summary>
    public const long VoiceSilentMs = 600_000;

    /// <summary>拿不到音频信号时（桥没运行），语音键的绝对兜底超时（秒）。取值同 VoiceSilentMs。</summary>
    public const double VoiceNoSignalSec = 600;

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
