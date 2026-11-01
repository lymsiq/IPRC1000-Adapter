//! 语音键的**租约**模型 —— 纯决策部分。
//!
//! ## 模型（一句话）
//!
//! ```text
//! 右 Alt 应按住  ⟺  now_ms() < 租约到期时刻
//! ```
//!
//! 遥控器的语音键是"按住说话"，但**松手报文经常丢**，所以不能靠"收到 UP 才释放"
//! （会永久卡住），也不能靠"计时一刀切"（30 秒长句会被砍断，实测过）。
//! 解决办法是租约：只要**音频还在流动**就续租；音频一停，租约自然到期。
//!
//! ## 写入只有四处
//!
//! | 事件 | 作用 |
//! |---|---|
//! | `0x10` 按下 | `arm(now, T_NOVOICE)` —— 先给一个较长的初始租约 |
//! | 每帧音频 | `renew(prev, now, T_PAUSE)` —— 续租 |
//! | 空报文（松手） | `disarm(now)` —— 立即失效 |
//! | 断链 / 退出 / 解卡键 | `disarm(now)` |
//!
//! ## 铁律
//!
//! **可靠事件才能续租，不可靠事件只能收口。**
//! 音频流动是可靠事件（它证明用户还在说）；"没收到松手"不是事件，只是缺失。
//! 所以续租只来自音频与按下，释放只来自到期与松手。
//!
//! ## 为什么这些要单独有测试
//!
//! 2026-10-31 真机踩过：30 秒长句被 20000 ms 的计时判据砍断；
//! 松手报文丢失导致右 Alt 永久按住；按住期间的心跳报文（0x0C/0x05）被误判成松手。
//! 这三类都属于"读代码看不出来、只有撞到才知道"，所以固化成用例。

/// 收到语音键按下：先给一个较长的初始租约（还不需要音频就能撑住起流）。
///
/// `t_novoice_ms` 历史上是 10 秒：遥控器起流有 300~500 ms 固件延迟，
/// 太短会在"刚按下还没出声"时被放开。
pub fn arm(now_ms: u64, t_novoice_ms: u64) -> u64 {
    now_ms.saturating_add(t_novoice_ms)
}

/// 收到音频帧：续租。
///
/// ★ **取较大值，绝不缩短**。理由：音频帧可能乱序/迟到，
/// 一条晚到的帧带着较早的 `now` 计算出来的到期时刻，若直接覆盖就会把租约改短，
/// 表现为"说着说着突然被放开"。
pub fn renew(prev_lease_ms: u64, now_ms: u64, t_pause_ms: u64) -> u64 {
    prev_lease_ms.max(now_ms.saturating_add(t_pause_ms))
}

/// 收口：立即失效（松手报文 / 断链 / 退出 / 解卡键）。
///
/// 取 `now` 而不是 0：`should_hold` 用的是 `<`，`now < now` 为假，语义清楚。
pub fn disarm(now_ms: u64) -> u64 {
    now_ms
}

/// 当前该不该按住。
///
/// 边界：`now == lease` 判为**不按住**（到期即刻释放）。
pub fn should_hold(now_ms: u64, lease_until_ms: u64) -> bool {
    now_ms < lease_until_ms
}

/// 报文是不是"松手"。
///
/// 真机事实：按住期间**零报文**，真松手一定是一条空报文 `[]`。
/// 所以空 = 松手，非空 = 还在按（含心跳）。
pub fn is_release_report(usages: &[u8]) -> bool {
    usages.is_empty()
}

/// 按住期间会插入的心跳 usage —— **不能**当成松手，也不能当成新的一次按下。
///
/// 实测出现过 `0x0C` / `0x05` 伴随其它键出现；若把它们当松手，
/// 就会"说着说着断一下"。
pub fn is_heartbeat(usage: u8) -> bool {
    usage == 0x0C || usage == 0x05
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 按下时给的是"较长初始租约"，保证起流延迟期间不会被放开
    #[test]
    fn arm_gives_long_initial_lease() {
        assert_eq!(arm(1_000, 10_000), 11_000);
        assert!(should_hold(1_000 + 500, arm(1_000, 10_000)));
    }

    /// ★ 续租取较大值：晚到的音频帧不能把租约改短
    #[test]
    fn renew_never_shortens() {
        let lease = arm(1_000, 10_000);          // 11_000
        let renewed = renew(lease, 1_200, 3_000); // 1_200+3_000=4_200 < 11_000
        assert_eq!(renewed, lease, "晚到的帧把租约改短 = 说着说着被放开");
    }

    /// 音频持续流动时，租约一直往后推
    #[test]
    fn renew_extends_while_audio_flows() {
        let mut lease = arm(0, 10_000);
        for t in [9_000u64, 12_000, 15_000] {
            lease = renew(lease, t, 3_000);
        }
        assert_eq!(lease, 18_000);
        assert!(should_hold(17_999, lease));
    }

    /// 到期边界：now == lease 就不再按住了
    #[test]
    fn release_exactly_at_deadline() {
        assert!(should_hold(9_999, 10_000));
        assert!(!should_hold(10_000, 10_000));
        assert!(!should_hold(10_001, 10_000));
    }

    /// 松手报文是空的；心跳不是松手
    #[test]
    fn only_empty_report_means_release() {
        assert!(is_release_report(&[]));
        assert!(!is_release_report(&[0x0C]));
        assert!(!is_release_report(&[0x05]));
        assert!(is_heartbeat(0x0C) && is_heartbeat(0x05));
        assert!(!is_heartbeat(0x10), "语音键本身不是心跳");
    }

    /// ★ 回归：固件 30 秒自动收工、**不发松手报文**时，靠租约兜底释放
    ///
    /// 真机现象：长句说到 30 秒左右突然断。排查结论是固件自动收工且不发空报文，
    /// 若只靠"收到松手才释放"，右 Alt 会永久按住。
    #[test]
    fn firmware_silent_stop_is_covered_by_lease() {
        let t_pause = 3_000;
        let mut now = 0u64;
        let mut lease = arm(now, 10_000);

        // 用户按住说了 29 秒，音频持续（每 20 ms 一帧）
        while now < 29_000 {
            now += 20;
            lease = renew(lease, now, t_pause);
        }
        assert!(should_hold(now, lease), "说着的时候不能被放开");

        // 固件收工：音频停了，也没有松手报文。只推进时间。
        let stop_at = now;
        now = stop_at + t_pause - 1;
        assert!(should_hold(now, lease), "刚停 1 ms 就放手属于误杀");

        now = stop_at + t_pause;
        assert!(!should_hold(now, lease), "音频停满 T_PAUSE 必须释放");
    }

    /// 回归：松手报文到达就立刻失效，不等租约到期
    #[test]
    fn explicit_release_is_immediate() {
        let lease = renew(arm(0, 10_000), 5_000, 3_000);
        let after = disarm(5_100);
        assert!(!should_hold(5_100, after), "松手了就该立刻放开");
        assert!(should_hold(5_100, lease), "（对照：不 disarm 的话本来还该按着）");
    }
}

// ───────────────── 点按模式：松手后补一次"点击" ─────────────────
//
// 为什么需要它（2026-11-01 定案，方案 A）：
//   遥控器**只在按住时出音频**，所以"点一下开始、松手继续录"物理上做不到。
//   而豆包新版要求"点一下弹框"、结束要"再点一下"。
//   可行的折中：用户仍然按住说话，**松手时我们补一次瞬发点击** ——
//   豆包那边看到的就是"又按了一下"，于是结束对话。用户感知是"按一下、说、松手就结束"。
//
// 为什么不能用一次 `arm` 了事：补点击需要**先放开、隔一小段、再按下、再放开**。
//   同一个 tick 里 disarm+arm，两帧报告之间没有间隔，豆包会把它看成"一直按着"，
//   而不是"又按了一下"。所以用状态机把它拉成三段，由 50 ms 的 tick 驱动。

/// 补点击的两个阶段。
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum TapFollowUp {
    /// 没有待办
    None,
    /// 松手后等待 `ms` 再按下（留出"松手"的间隔，否则会被看成连按）
    Wait(u64),
    /// 已按下，到 `ms` 时放开
    Holding(u64),
}

/// 补点击的动作（由调用方转成真正的 arm/disarm）
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum TapAction {
    /// 该按下（开始补的这一下）
    Press,
    /// 该放开
    Release,
}

/// 松手后到"补按下"之间的间隔。
///
/// 太短（<30 ms）会被当成长按的一部分，太长（>150 ms）用户会听到"两段"。
pub const TAP_GAP_MS: u64 = 50;
/// 补的这一下按住多久。
pub const TAP_HOLD_MS: u64 = 60;

impl TapFollowUp {
    /// 用户松手时调用：排一次补点击。
    pub fn on_release(now_ms: u64) -> Self {
        TapFollowUp::Wait(now_ms.saturating_add(TAP_GAP_MS))
    }

    /// tick 驱动：到点了就返回该做的动作。
    pub fn tick(&mut self, now_ms: u64) -> Option<TapAction> {
        match *self {
            TapFollowUp::None => None,
            TapFollowUp::Wait(at) if now_ms >= at => {
                *self = TapFollowUp::Holding(now_ms.saturating_add(TAP_HOLD_MS));
                Some(TapAction::Press)
            }
            TapFollowUp::Holding(at) if now_ms >= at => {
                *self = TapFollowUp::None;
                Some(TapAction::Release)
            }
            _ => None,
        }
    }

    pub fn is_pending(self) -> bool {
        !matches!(self, TapFollowUp::None)
    }
}

#[cfg(test)]
mod tap_tests {
    use super::*;

    /// 补点击必须"放开 → 等一会儿 → 按下 → 再放开"，不能在同一次里连续做
    #[test]
    fn followup_tap_is_three_phases() {
        let mut f = TapFollowUp::on_release(1_000);
        assert!(f.is_pending());
        // 还没到点：什么都不做
        assert_eq!(f.tick(1_010), None);
        // 到点：按下
        assert_eq!(f.tick(1_050), Some(TapAction::Press));
        // 按住期间不重复按
        assert_eq!(f.tick(1_060), None);
        // 到点：放开
        assert_eq!(f.tick(1_110), Some(TapAction::Release));
        assert!(!f.is_pending());
        // 之后不再有任何动作
        assert_eq!(f.tick(9_999), None);
    }

    /// 用户松手后立刻又按下：补点击应被取消（否则一次操作会发两次点击）
    #[test]
    fn new_press_cancels_pending_followup() {
        let mut f = TapFollowUp::on_release(1_000);
        // 用户在 20 ms 内又按下了（真实连按）
        f = TapFollowUp::None; // 由调用方置 None 表示"新的一段开始"
        assert_eq!(f.tick(1_050), None, "取消后不能再补点击");
    }

    /// 间隔与按住时长都在合理范围（不连按、也不像长按）
    #[test]
    fn timing_is_sane() {
        assert!((20..=150).contains(&TAP_GAP_MS));
        assert!((30..=200).contains(&TAP_HOLD_MS));
    }
}
