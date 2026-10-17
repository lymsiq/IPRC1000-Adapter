using System;
using System.Text;

/// <summary>
/// 「按键链路静默失效」探针。
///
/// 为什么需要它（2026-10-17 真实事故）：
///   v1.2.7 把 HID 数据 CID 写死成 0x41，某次重连后数据落到 0x40 ⇒ 桥**收到了按键报告**
///   却一个都解析不出来 ⇒ 用户"所有按键都没反应"，而他**过了 8 分钟**才发现。
///   那 8 分钟里桥的两个计数一直在：`hidReports` 在涨、`keyEvents` 恒为 0。
///
/// 判据（很关键，别写成"没有按键就报警"）：
///   · **只涨“收到的报告数”、不涨“产生的按键事件数” 持续 ≥10 s** → 异常
///   · **闲着没按键**（两个数都不涨）→ **正常**，不能报警
///   · 期间只要产生过一次按键事件 → 立即恢复正常
///
/// 三类状态必须分开（这一条今天已经踩两次坑：把"正常空态"显示成"故障"）。
/// </summary>
internal static class KeyPathWatch
{
    /// <summary>阈值：只涨报数不涨事件超过这么久就判异常</summary>
    public const int DefaultThresholdMs = 10_000;

    public sealed class State
    {
        public long LastHid;
        public long LastKey;
        /// <summary>本轮"只涨报数"的起点（毫秒）；-1 = 当前没在计数</summary>
        public long SinceMs = -1;
        public bool Stuck;
    }

    /// <summary>
    /// 喂一次快照。`hid` / `key` 是桥的**累计**计数，`nowMs` 用 `Environment.TickCount64`。
    /// 返回 true 表示判为"链路异常"。
    /// </summary>
    public static bool Step(State s, long hid, long key, long nowMs,
                            int thresholdMs = DefaultThresholdMs)
    {
        var newHid = hid > s.LastHid;
        var newKey = key > s.LastKey;
        s.LastHid = hid;
        s.LastKey = key;

        // ① 产生了按键事件 → 链路是通的，立即复位
        if (newKey) { s.SinceMs = -1; s.Stuck = false; return false; }

        // ② 也没有新的报告 → 用户压根没按键（正常空态）**不能报警**
        if (!newHid) { s.SinceMs = -1; s.Stuck = false; return false; }

        // ③ 有报告、没事件 → 开始/继续计时
        if (s.SinceMs < 0) s.SinceMs = nowMs;
        s.Stuck = nowMs - s.SinceMs >= thresholdMs;
        return s.Stuck;
    }

    /// <summary>自检（纯逻辑，不碰硬件）</summary>
    public static string SelfTest()
    {
        var sb = new StringBuilder();
        var ok = true;
        void Check(bool cond, string name, string detail)
        {
            ok &= cond;
            sb.AppendLine($"  {(cond ? "✓" : "✗")} {name}");
            sb.AppendLine($"       {detail}");
        }

        // ① 一开始什么都没收到：不能报警（"尚未收到"不是故障）
        var s1 = new State();
        var r1 = Step(s1, 0, 0, 1000);
        Check(!r1, "未收到任何报告时不报警", $"hid=0 key=0 → {r1}");

        // ② 闲着不动（两个数都不涨）：**绝对不能**报警
        var s2 = new State();
        Step(s2, 500, 400, 1000);                 // 先有历史
        var r2 = Step(s2, 500, 400, 60_000);      // 60 秒后仍是同样两个数
        Check(!r2, "空闲（两数都不涨）不报警", $"hid=500 key=400 停了 59 s → {r2}");

        // ③ 只涨报数、不涨事件，持续 11 秒 → 报警
        var s3 = new State();
        Step(s3, 1000, 900, 1000);
        var t = 1000;
        var hit = false;
        for (var i = 0; i < 12; i++)
        {
            t += 1000;
            hit = Step(s3, 1000 + i + 1, 900, t);   // 每秒多 1 个报告，事件不动
        }
        Check(hit, "只涨报告不涨事件 ≥10 s 报警", $"hid 持续 +1/s、key 不动，{t - 1000} ms 后 → {hit}");

        // ④ 边界：9 秒不报、11 秒报
        var s4 = new State();
        Step(s4, 0, 0, 0);
        Step(s4, 1, 0, 100);                       // 开始计时
        var at9 = Step(s4, 2, 0, 100 + 9000);
        var at11 = Step(s4, 3, 0, 100 + 11_000);
        Check(!at9 && at11, "阈值边界（9 s 不报 / 11 s 报）", $"9 s→{at9} · 11 s→{at11}");

        // ⑤ 恢复：期间产生过一次按键事件 → 立即复位
        var s5 = new State();
        Step(s5, 100, 50, 1000);
        Step(s5, 101, 50, 12_000);                 // 报警状态
        var recovered = Step(s5, 102, 51, 13_000); // 事件涨了
        Check(!recovered, "产生按键事件后立即恢复", $"key 50→51 → {recovered}");

        sb.AppendLine(ok ? "⇒ 全部通过 ✓" : "⇒ ★ 有断言失败");
        return sb.ToString();
    }
}
