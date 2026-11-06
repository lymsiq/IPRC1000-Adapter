//! HID 报文的**纯解析与差分** —— 从 `handle_hid_report` 里剥出来的决策部分。
//!
//! ## 为什么只剥这几条
//!
//! `handle_hid_report` 有 200+ 行，但里面混着两类东西：
//!
//! | 类别 | 例子 | 能不能纯函数化 |
//! |---|---|---|
//! | **决策** | 这份报文算不算键盘快照、哪些键按下/抬起 | 能 —— 本模块 |
//! | **执行** | 注入、宏、鼠标、文本槽、日志 | 不能（有副作用，需要 fake sink） |
//!
//! 先把"决策"抽干净，执行留在 `backend`，这样最常出错、最难靠人眼验证的那部分
//! 就有了测试；执行那部分本来就是"照决策做",出错概率低得多。
//!
//! ## 语料来自真机
//!
//! `bridge.log` 里的 `[RAW]` 行就是这份语料（本轮有 1851 条）。真机模式：
//! ```text
//! [28 00 00 00 00 00]  按下
//! [00 00 00 00 00 00]  松开（空报文）
//! [0C 00 00 00 00 00]  心跳（按住期间会插）
//! [00 00 00 00 00 00]
//! [10 00 00 00 00 00]  语音键按下
//! ```

/// 语音键（右 Alt）的 usage
pub const VOICE: u8 = 0x10;

/// 真机上会单独出现的"幽灵"usage —— 它们不是键盘快照。
///
/// `0x01` 是错误回滚类，`0x0C` 是按住期间的心跳。只含它们的报文必须整份忽略：
/// 若当成快照处理，就会"按住语音键时插一条 0x0C ⇒ 判定松开 ⇒ 长按被截断"。
pub const GHOST: [u8; 2] = [0x01, 0x0C];

/// 报文里的按键集合（0 是填充，不是按键）。
pub fn key_set(usages: &[u8]) -> [bool; 256] {
    let mut now = [false; 256];
    for &u in usages {
        if u != 0 {
            now[u as usize] = true;
        }
    }
    now
}

/// 是不是"只含幽灵 usage"的报文（既不是空报文，也没有真实按键）。
///
/// 语义与原来的 `!has_real && !all_empty` 完全一致，独立出来是为了能测。
pub fn is_ghost_only(usages: &[u8]) -> bool {
    let has_real = usages.iter().any(|&u| u != 0 && !GHOST.contains(&u));
    let all_empty = usages.iter().all(|&u| u == 0);
    !has_real && !all_empty
}

/// 空报文 —— 真机上**只有它代表松手**。
pub fn is_empty(usages: &[u8]) -> bool {
    usages.iter().all(|&u| u == 0)
}

/// 差分的两部分：要抬起的键、要按下的键（都按 usage 升序，保证事件顺序确定）。
pub struct Diff {
    pub ups: Vec<u8>,
    pub downs: Vec<u8>,
}

/// 对比"上一份状态"与"这一份报文"，得出要发的 UP / DOWN。
///
/// 注意：**语音键（0x10）不在这里判定**。它的松手只认空报文，且受租约与点按锁存影响，
/// 规则比"状态差分"复杂得多，见 `voice` 模块。
pub fn diff(held: &[bool; 256], now: &[bool; 256]) -> Diff {
    let mut ups = Vec::new();
    let mut downs = Vec::new();
    for u in 0..256usize {
        if u == VOICE as usize {
            continue;
        }
        if held[u] && !now[u] {
            ups.push(u as u8);
        } else if now[u] && !held[u] {
            downs.push(u as u8);
        }
    }
    Diff { ups, downs }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 真机报文：0x28 按下 → 空 → 心跳 → 空
    /// 心跳**不能**把 0x28 判成松开。
    #[test]
    fn heartbeat_does_not_release_held_key() {
        let mut held = [false; 256];
        // [28 ...] 按下
        let now = key_set(&[0x28, 0, 0, 0, 0, 0]);
        let d = diff(&held, &now);
        assert_eq!(d.downs, vec![0x28]);
        assert!(d.ups.is_empty());
        held = now;

        // [00 ...] 空报文 ⇒ 真的松开
        let now = key_set(&[0, 0, 0, 0, 0, 0]);
        let d = diff(&held, &now);
        assert_eq!(d.ups, vec![0x28]);
        held = now;

        // [0C ...] 心跳：是幽灵报文，整份忽略 ⇒ 不产生任何变化
        let ghost = [0x0C, 0, 0, 0, 0, 0];
        assert!(is_ghost_only(&ghost), "只含 0x0C 必须被认成幽灵报文");
        assert!(!is_ghost_only(&[0x28, 0, 0, 0, 0, 0]));
        assert!(!is_ghost_only(&[0, 0, 0, 0, 0, 0]), "空报文不是幽灵报文");
        let _ = held;
    }

    /// 空报文只能由 `is_empty` 认出来，不能与"幽灵"混淆
    #[test]
    fn empty_and_ghost_are_different() {
        assert!(is_empty(&[0, 0, 0, 0, 0, 0]));
        assert!(!is_empty(&[0x0C, 0, 0, 0, 0, 0]));
        assert!(!is_empty(&[0x28, 0, 0, 0, 0, 0]));
        // 二者互斥
        for case in [
            vec![0u8, 0, 0, 0, 0, 0],
            vec![0x0C, 0, 0, 0, 0, 0],
            vec![0x01, 0, 0, 0, 0, 0],
            vec![0x28, 0, 0, 0, 0, 0],
        ] {
            assert!(!(is_empty(&case) && is_ghost_only(&case)));
        }
    }

    /// 多个键同按：差分给出确定的升序事件序列
    #[test]
    fn multiple_keys_are_ordered() {
        let held = [false; 256];
        let now = key_set(&[0x52, 0x28, 0, 0, 0, 0]); // 上方向 + 回车
        let d = diff(&held, &now);
        assert_eq!(d.downs, vec![0x28, 0x52], "必须升序，否则事件顺序不稳定");
    }

    /// 语音键不进 `diff` —— 它的释放规则由 voice 模块单独管
    #[test]
    fn voice_key_is_not_part_of_diff() {
        let mut held = [false; 256];
        held[VOICE as usize] = true;
        let now = key_set(&[0, 0, 0, 0, 0, 0]); // 空报文
        let d = diff(&held, &now);
        assert!(
            !d.ups.contains(&VOICE),
            "语音键若在这里被抬起，就绕过了租约与点按锁存"
        );
    }

    /// 同一份状态对比自己：不产生任何事件（幂等）
    #[test]
    fn same_state_yields_nothing() {
        let now = key_set(&[0x28, 0x52, 0, 0, 0, 0]);
        let d = diff(&now, &now);
        assert!(d.ups.is_empty() && d.downs.is_empty());
    }

    /// 真机语料逐条过一遍：任何一条都不应"既判空又判幽灵"，
    /// 且幽灵报文集合只有 0x01 / 0x0C 两种。
    #[test]
    fn real_corpus_spot_check() {
        // 取自 bridge.log 的真实行（13:33:18 – 13:33:27）
        let corpus: [&[u8]; 6] = [
            &[0x28, 0, 0, 0, 0, 0],
            &[0, 0, 0, 0, 0, 0],
            &[0x0C, 0, 0, 0, 0, 0],
            &[0, 0, 0, 0, 0, 0],
            &[0x10, 0, 0, 0, 0, 0],
            &[0x28, 0, 0, 0, 0, 0],
        ];
        for r in corpus {
            assert!(!(is_empty(r) && is_ghost_only(r)), "{r:?} 不能同时成立");
            if is_ghost_only(r) {
                let keys: Vec<u8> = r.iter().copied().filter(|&x| x != 0).collect();
                assert!(
                    keys.iter().all(|k| GHOST.contains(k)),
                    "{r:?} 被判幽灵，但含非幽灵键 {keys:?}"
                );
            }
        }
    }
}

/// 8 字节标准键盘报告 → usage 列表。
///
/// ★ 两条路径（ETW 与 HID 直读）必须共用这里的解析，否则同一台设备会产出
///   语义不同的两套 usage：
///   · ETW 路径的输入是 `acl[8..14]`（6 个 usage 字节），**从不含修饰键字节**；
///   · HID 直读拿到的是完整 8 字节 `[modifier][reserved][6×usage]`。
///
/// ★ **不展开修饰键**（这里刻意只取 b[2..8]）：
///   展开会让"真实键盘上按着 Ctrl"变成遥控器的一次按键事件（误触发），
///   而且 ETW 路径本来就不产生 0xE0..0xE7 —— 两条路必须同语义。
///   遥控器自己的按键（含语音键 0x10、方向键、音量键）都在这 6 个字节里，不需要修饰位。
pub fn from_report_bytes(buf: &[u8]) -> Vec<u8> {
    // 有的栈会在缓冲区前置 1 字节 Report ID（本设备是 1）
    let b = if buf.len() >= 9 && buf[0] == 1 { &buf[1..] } else { buf };
    if b.len() < 8 {
        return Vec::new();
    }
    b[2..8].iter().copied().filter(|u| *u != 0).collect()
}
