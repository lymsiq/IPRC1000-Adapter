//! 安装/环境动作的结果类型（2026-11-01）
//!
//! ## 为什么要有这个模块
//!
//! 原来所有安装动作返回 `String`，调用方靠**中文文案**猜语义：
//!
//! ```ignore
//! if r.contains("完成") || r.contains("已安装") { 成功 } else { 失败 }
//! ```
//!
//! 这已经真实误判过：`m.contains("已改造")` 把"已经是 HID-compliant，无需改造"
//! 判成了"改造成功"，于是界面上是绿的、实际什么都没做——**假绿灯**。
//! 而客户看到绿灯就不会再排查，比直接报错更糟。
//!
//! 所以：**成败由类型决定，文案只负责显示**。加一个状态不需要改任何 `contains`。

/// 一个安装/修复动作的结果。
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum StepResult {
    /// 做成了
    Done(String),
    /// 本来就是好的，不需要做
    AlreadyOk(String),
    /// 做成了，但 Windows 要求重启才生效（退出码 3010）
    NeedsReboot(String),
    /// 需要人工处理一步（附**唯一**动作，不要给一堆可能性）
    Manual(String),
    /// 失败（附原因）
    Failed(String),
}

impl StepResult {
    /// 算不算"已经到位"（含"需要重启"——重启后会到位）
    pub fn is_ok(&self) -> bool {
        matches!(
            self,
            StepResult::Done(_) | StepResult::AlreadyOk(_) | StepResult::NeedsReboot(_)
        )
    }

    /// 需要人工处理
    pub fn is_manual(&self) -> bool {
        matches!(self, StepResult::Manual(_) | StepResult::Failed(_))
    }

    pub fn needs_reboot(&self) -> bool {
        matches!(self, StepResult::NeedsReboot(_))
    }

    /// 给人看的一句话（界面上原样显示）
    pub fn message(&self) -> &str {
        match self {
            StepResult::Done(s)
            | StepResult::AlreadyOk(s)
            | StepResult::NeedsReboot(s)
            | StepResult::Manual(s)
            | StepResult::Failed(s) => s,
        }
    }

    /// 结构化标签，进日志/诊断包用（不含中文，便于脚本判断）
    pub fn tag(&self) -> &'static str {
        match self {
            StepResult::Done(_) => "done",
            StepResult::AlreadyOk(_) => "already_ok",
            StepResult::NeedsReboot(_) => "needs_reboot",
            StepResult::Manual(_) => "manual",
            StepResult::Failed(_) => "failed",
        }
    }
}

/// 一次"一键修复"的整体结论。
#[derive(Default)]
pub struct Report {
    pub items: Vec<(String, StepResult)>,
}

impl Report {
    pub fn push(&mut self, name: &str, r: StepResult) {
        self.items.push((name.to_string(), r));
    }

    /// 全部到位（含"待重启"）
    pub fn all_ok(&self) -> bool {
        self.items.iter().all(|(_, r)| r.is_ok())
    }

    pub fn any_reboot(&self) -> bool {
        self.items.iter().any(|(_, r)| r.needs_reboot())
    }

    /// 必须人工处理的项（客户需要看到"我现在该做什么"）
    pub fn manual(&self) -> Vec<&(String, StepResult)> {
        self.items.iter().filter(|(_, r)| r.is_manual()).collect()
    }

    /// 一行总结
    pub fn summary(&self) -> String {
        if self.items.is_empty() {
            return String::from("没有需要处理的项");
        }
        let ok = self.items.iter().filter(|(_, r)| r.is_ok()).count();
        let reboot = self.any_reboot();
        let manual = self.manual().len();
        let mut s = format!("{ok}/{} 项到位", self.items.len());
        if reboot {
            s.push_str("；有组件需重启电脑才生效");
        }
        if manual > 0 {
            s.push_str(&format!("；{manual} 项需要手动处理"));
        }
        let detail: Vec<String> = self
            .items
            .iter()
            .map(|(n, r)| format!("{n}：{}", r.message()))
            .collect();
        format!("{s}\n{}", detail.join("\n"))
    }
}

impl std::fmt::Display for StepResult {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{}", self.message())
    }
}
