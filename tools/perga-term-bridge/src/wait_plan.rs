//! Validated waiting plans, text condition evaluation, and reactive revision wait loops.
//! Purified from `tools/rust-terminal/terminal_interaction_ref/{wait_plan.rs, text_conditions.rs, text_wait.rs, wait.rs}`
//! with zero domain/repository/fixture coupling.

use anyhow::{Result, ensure};
use serde::Deserialize;
use serde_json::{Value, json};
use std::time::Duration;
use tokio::sync::watch;
use tokio::time::Instant;

pub const WAIT_MS_MAX: u64 = 20_000;
pub const SETTLE_MS_MAX: u64 = 2_000;
pub const SETTLE_MS_DEFAULT: u64 = 150;
pub const CHANGE_WAIT_MS_DEFAULT: u64 = 2_000;
pub const SIGNAL_WAIT_MS_DEFAULT: u64 = 15_000;
pub const TEXT_WAIT_MS_DEFAULT: u64 = 15_000;
pub const REPAINT_MS_MAX: u64 = 5_000;
pub const REPAINT_MS_DEFAULT: u64 = 1_500;
pub const WAIT_TEXT_MAX_PATTERNS: usize = 8;
pub const WAIT_TEXT_MAX_BYTES: usize = 200;

pub const TERMINAL_SIGNAL_EVENTS: [&str; 4] = [
    "stop",
    "notification",
    "permission_request",
    "session_end",
];
pub const DEFAULT_SIGNAL_EVENTS: [&str; 4] = TERMINAL_SIGNAL_EVENTS;

#[derive(Clone, Copy, Debug, Default, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "lowercase")]
pub enum WaitFor {
    #[default]
    Elapsed,
    Change,
    Signal,
    Text,
}

impl WaitFor {
    pub fn name(self) -> &'static str {
        match self {
            Self::Elapsed => "elapsed",
            Self::Change => "change",
            Self::Signal => "signal",
            Self::Text => "text",
        }
    }
}

#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct TextConditions {
    pub present: Vec<String>,
    pub absent: Vec<String>,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct ConditionState {
    pub present: Option<bool>,
    pub absent: Option<bool>,
}

impl ConditionState {
    pub fn holds(self) -> bool {
        self.present != Some(false) && self.absent != Some(false)
    }

    pub fn to_json(self) -> Value {
        json!({ "present": self.present, "absent": self.absent })
    }
}

pub fn find_match(patterns: &[String], rows: &[String]) -> Option<(String, usize)> {
    patterns.iter().find_map(|pattern| {
        rows.iter()
            .position(|row| row.contains(pattern.as_str()))
            .map(|row| (pattern.clone(), row))
    })
}

impl TextConditions {
    pub fn is_empty(&self) -> bool {
        self.present.is_empty() && self.absent.is_empty()
    }

    pub fn test(&self, rows: &[String]) -> (Option<(String, usize)>, ConditionState) {
        let matched = find_match(&self.present, rows);
        let state = ConditionState {
            present: (!self.present.is_empty()).then_some(matched.is_some()),
            absent: (!self.absent.is_empty()).then(|| find_match(&self.absent, rows).is_none()),
        };
        (matched, state)
    }
}

#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct WaitPlan {
    pub mode: WaitFor,
    pub budget_ms: u64,
    pub settle_ms: u64,
    pub signal_events: Vec<String>,
    pub repaint_ms: u64,
    pub wait_text: Vec<String>,
    pub wait_text_absent: Vec<String>,
}

impl WaitPlan {
    pub fn new(
        wait_for: Option<WaitFor>,
        wait_ms: Option<u64>,
        settle_ms: Option<u64>,
        signal_events: Option<Vec<String>>,
        repaint_ms: Option<u64>,
        wait_text: Option<Vec<String>>,
        wait_text_absent: Option<Vec<String>>,
    ) -> Result<Self> {
        let mode = wait_for.unwrap_or_default();
        let wait_ms = wait_ms.unwrap_or(match mode {
            WaitFor::Change => CHANGE_WAIT_MS_DEFAULT,
            WaitFor::Signal => SIGNAL_WAIT_MS_DEFAULT,
            WaitFor::Text => TEXT_WAIT_MS_DEFAULT,
            WaitFor::Elapsed => 0,
        });
        ensure!(wait_ms <= WAIT_MS_MAX, "wait_ms must be 0..{WAIT_MS_MAX}");
        ensure!(
            settle_ms.is_none_or(|settle| settle <= SETTLE_MS_MAX),
            "settle_ms must be 0..{SETTLE_MS_MAX}"
        );
        ensure!(
            settle_ms.is_none_or(|settle| settle == SETTLE_MS_DEFAULT)
                || matches!(mode, WaitFor::Change | WaitFor::Signal | WaitFor::Text),
            "settle_ms requires wait_for=change, wait_for=signal or wait_for=text"
        );
        ensure!(
            signal_events.is_none() || mode == WaitFor::Signal,
            "signal_events requires wait_for=signal"
        );
        ensure!(
            repaint_ms.is_none_or(|repaint| repaint <= REPAINT_MS_MAX),
            "repaint_ms must be 0..{REPAINT_MS_MAX}"
        );
        ensure!(
            repaint_ms.is_none() || mode == WaitFor::Signal,
            "repaint_ms requires wait_for=signal"
        );
        ensure!(
            wait_text.is_none() || matches!(mode, WaitFor::Text | WaitFor::Signal),
            "wait_text requires wait_for=text or wait_for=signal"
        );
        ensure!(
            wait_text_absent.is_none() || matches!(mode, WaitFor::Text | WaitFor::Signal),
            "wait_text_absent requires wait_for=text or wait_for=signal"
        );
        let signal_events = match mode {
            WaitFor::Signal => {
                let events = signal_events.unwrap_or_else(|| {
                    DEFAULT_SIGNAL_EVENTS
                        .iter()
                        .map(|e| (*e).to_string())
                        .collect()
                });
                ensure!(
                    !events.is_empty(),
                    "signal_events must name at least one event"
                );
                for event in &events {
                    ensure!(
                        TERMINAL_SIGNAL_EVENTS.contains(&event.as_str()),
                        "unknown signal event {event:?}; expected one of {TERMINAL_SIGNAL_EVENTS:?}"
                    );
                }
                events
            }
            _ => Vec::new(),
        };
        ensure!(
            mode != WaitFor::Text || wait_text.is_some() || wait_text_absent.is_some(),
            "wait_for=text requires wait_text or wait_text_absent"
        );
        ensure!(
            mode != WaitFor::Signal
                || repaint_ms != Some(0)
                || (wait_text.is_none() && wait_text_absent.is_none()),
            "text conditions need a repaint window; repaint_ms must be > 0"
        );
        let patterns = |list: Option<Vec<String>>| -> Result<Vec<String>> {
            match list {
                Some(patterns) => {
                    validate_wait_text(&patterns)?;
                    Ok(patterns)
                }
                None => Ok(Vec::new()),
            }
        };
        Ok(Self {
            mode,
            budget_ms: wait_ms,
            settle_ms: settle_ms.unwrap_or(SETTLE_MS_DEFAULT),
            signal_events,
            repaint_ms: match mode {
                WaitFor::Signal => repaint_ms.unwrap_or(REPAINT_MS_DEFAULT),
                _ => 0,
            },
            wait_text: patterns(wait_text)?,
            wait_text_absent: patterns(wait_text_absent)?,
        })
    }

    /// Normalize `term.observe` RPC parameters (`"none" | "elapsed" | "change" | "text"`) into a `WaitPlan`.
    pub fn from_observe_rpc(
        wait_for: Option<&str>,
        wait_ms: Option<u64>,
        settle_ms: Option<u64>,
        wait_text: Option<Vec<String>>,
        wait_text_absent: Option<Vec<String>>,
    ) -> Result<Option<Self>> {
        let clean_patterns = |list: Option<Vec<String>>| list.filter(|items| !items.is_empty());
        let wait_text = clean_patterns(wait_text);
        let wait_text_absent = clean_patterns(wait_text_absent);
        match wait_for.unwrap_or("none") {
            "none" => Ok(None),
            "elapsed" => {
                let budget = wait_ms.unwrap_or(0);
                ensure!(budget <= WAIT_MS_MAX, "wait_ms must be 0..{WAIT_MS_MAX}");
                if let Some(settle) = settle_ms {
                    ensure!(
                        settle <= SETTLE_MS_MAX,
                        "settle_ms must be 0..{SETTLE_MS_MAX}"
                    );
                }
                Ok(Some(Self {
                    mode: WaitFor::Elapsed,
                    budget_ms: budget,
                    settle_ms: settle_ms.unwrap_or(SETTLE_MS_DEFAULT),
                    signal_events: Vec::new(),
                    repaint_ms: 0,
                    wait_text: Vec::new(),
                    wait_text_absent: Vec::new(),
                }))
            }
            "change" => Self::new(
                Some(WaitFor::Change),
                wait_ms,
                settle_ms,
                None,
                None,
                None,
                None,
            )
            .map(Some),
            "text" => Self::new(
                Some(WaitFor::Text),
                wait_ms,
                settle_ms,
                None,
                None,
                wait_text,
                wait_text_absent,
            )
            .map(Some),
            other => anyhow::bail!(
                "unsupported wait_for mode {other:?}; expected none, elapsed, change, or text"
            ),
        }
    }

    pub fn conditions(&self) -> TextConditions {
        TextConditions {
            present: self.wait_text.clone(),
            absent: self.wait_text_absent.clone(),
        }
    }

    pub fn tests_text(&self) -> bool {
        match self.mode {
            WaitFor::Text => true,
            WaitFor::Signal => !self.wait_text.is_empty() || !self.wait_text_absent.is_empty(),
            _ => false,
        }
    }

    pub fn validate(&self) -> Result<()> {
        ensure!(
            self.budget_ms <= WAIT_MS_MAX
                && self.settle_ms <= SETTLE_MS_MAX
                && self.repaint_ms <= REPAINT_MS_MAX,
            "observation wait exceeds limits"
        );
        ensure!(
            self.mode != WaitFor::Signal || !self.signal_events.is_empty(),
            "signal wait without events"
        );
        for list in [&self.wait_text, &self.wait_text_absent] {
            match self.mode {
                WaitFor::Text | WaitFor::Signal => {
                    if !list.is_empty() {
                        validate_wait_text(list)?;
                    }
                }
                _ => ensure!(
                    list.is_empty(),
                    "text conditions outside text or signal mode"
                ),
            }
        }
        ensure!(
            self.mode != WaitFor::Text || !self.conditions().is_empty(),
            "wait_for=text without text conditions"
        );
        ensure!(
            self.mode != WaitFor::Signal || self.repaint_ms > 0 || self.conditions().is_empty(),
            "text conditions need a repaint window; repaint_ms must be > 0"
        );
        Ok(())
    }
}

fn validate_wait_text(patterns: &[String]) -> Result<()> {
    ensure!(
        (1..=WAIT_TEXT_MAX_PATTERNS).contains(&patterns.len()),
        "wait_text must list 1..{WAIT_TEXT_MAX_PATTERNS} patterns"
    );
    for pattern in patterns {
        ensure!(
            !pattern.is_empty()
                && pattern.len() <= WAIT_TEXT_MAX_BYTES
                && !pattern.chars().any(char::is_control),
            "each wait_text pattern must be 1..{WAIT_TEXT_MAX_BYTES} bytes of printable text"
        );
    }
    Ok(())
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct TextMatch {
    pub pattern: String,
    pub row: usize,
    pub revision: u64,
    pub already: bool,
}

impl TextMatch {
    pub fn to_json(&self) -> Value {
        json!({
            "pattern": self.pattern,
            "row": self.row,
            "revision": self.revision.to_string(),
            "already": self.already
        })
    }
}

#[derive(Debug, PartialEq, Eq)]
pub struct TextWait {
    pub matched: Option<TextMatch>,
    pub holds: bool,
    pub conditions: ConditionState,
    pub settled: bool,
    pub exited: bool,
}

#[allow(clippy::too_many_arguments)]
pub async fn wait_for_text(
    mut revisions: watch::Receiver<u64>,
    mut events: watch::Receiver<u64>,
    stopped: impl Fn() -> bool,
    capture: impl Fn() -> Option<(Vec<String>, u64)>,
    conditions: &TextConditions,
    started: Instant,
    deadline: Instant,
    settle: Duration,
) -> TextWait {
    let mut seen: Option<u64> = None;
    let mut matched: Option<TextMatch> = None;
    let mut state = ConditionState::default();
    let mut holds = false;
    let mut continuous = true;
    let mut last_change = started;
    let verdict =
        |matched: Option<TextMatch>, state: ConditionState, settled: bool, exited: bool| TextWait {
            matched,
            holds: state.holds(),
            conditions: state,
            settled,
            exited,
        };
    loop {
        events.borrow_and_update();
        let current = *revisions.borrow_and_update();
        let now = Instant::now();
        if seen != Some(current) {
            let first = seen.is_none();
            seen = Some(current);
            if !first {
                last_change = now;
            }
            let tested = capture().map(|(rows, revision)| (conditions.test(&rows), revision));
            let found = tested
                .as_ref()
                .and_then(|((found, _), revision)| found.clone().map(|(p, r)| (p, r, *revision)));
            state = tested
                .as_ref()
                .map(|((_, state), _)| *state)
                .unwrap_or_default();
            holds = tested.is_some() && state.holds();
            continuous = continuous && found.is_some();
            matched = found.map(|(pattern, row, revision)| TextMatch {
                pattern,
                row,
                revision,
                already: continuous,
            });
        }
        let quiet_until = last_change + settle;
        if stopped() {
            return verdict(matched, state, false, true);
        }
        if holds && now >= quiet_until && quiet_until < deadline {
            return verdict(matched, state, true, false);
        }
        if now >= deadline {
            return verdict(matched, state, false, false);
        }
        let timer = if holds {
            quiet_until.min(deadline)
        } else {
            deadline
        };
        tokio::select! {
            result = revisions.changed() => {
                if result.is_err() {
                    return verdict(matched, state, false, stopped());
                }
            }
            result = events.changed() => {
                if result.is_err() {
                    return verdict(matched, state, false, stopped());
                }
            }
            _ = tokio::time::sleep_until(timer) => {
                if Some(*revisions.borrow_and_update()) != seen {
                    continue;
                }
                if stopped() {
                    return verdict(matched, state, false, true);
                }
                if holds && timer < deadline {
                    return verdict(matched, state, true, false);
                }
                return verdict(matched, state, false, false);
            }
        }
    }
}

#[derive(Debug, PartialEq, Eq)]
pub struct ChangeProgress {
    pub changed: bool,
    pub settled: bool,
    pub exited: bool,
}

pub async fn wait_for_change(
    mut revisions: watch::Receiver<u64>,
    mut events: watch::Receiver<u64>,
    stopped: impl Fn() -> bool,
    baseline: u64,
    deadline: Instant,
    settle: Duration,
) -> ChangeProgress {
    let mut progress = ChangeProgress {
        changed: false,
        settled: false,
        exited: false,
    };
    let mut seen = baseline;
    let mut quiet_until = deadline;
    loop {
        events.borrow_and_update();
        let current = *revisions.borrow_and_update();
        let now = Instant::now();
        if current != seen {
            seen = current;
            progress.changed = true;
            quiet_until = (now + settle).min(deadline);
        }
        if stopped() {
            progress.exited = true;
            break;
        }
        if now >= deadline {
            break;
        }
        tokio::select! {
            result = revisions.changed() => {
                if result.is_err() {
                    break;
                }
            }
            result = events.changed() => {
                if result.is_err() {
                    break;
                }
            }
            _ = tokio::time::sleep_until(quiet_until) => {
                if *revisions.borrow_and_update() != seen {
                    continue;
                }
                if stopped() {
                    progress.exited = true;
                    break;
                }
                if progress.changed && quiet_until < deadline {
                    progress.settled = true;
                }
                break;
            }
        }
    }
    progress
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
    use std::sync::{Arc, Mutex};

    #[test]
    fn omitted_wait_ms_and_rpc_observe_normalization() {
        let plan = WaitPlan::new(Some(WaitFor::Change), None, None, None, None, None, None).unwrap();
        assert_eq!(plan.budget_ms, CHANGE_WAIT_MS_DEFAULT);
        assert_eq!(plan.settle_ms, SETTLE_MS_DEFAULT);

        // `DESIGN.md` example passes empty `wait_text: []`, `wait_text_absent: []` even for non-text waits.
        assert!(
            WaitPlan::from_observe_rpc(Some("none"), Some(0), Some(150), Some(vec![]), Some(vec![]))
                .unwrap()
                .is_none()
        );
        let text_plan = WaitPlan::from_observe_rpc(
            Some("text"),
            Some(2000),
            Some(150),
            Some(vec!["READY".into()]),
            Some(vec![]),
        )
        .unwrap()
        .unwrap();
        assert_eq!(text_plan.mode, WaitFor::Text);
        assert_eq!(text_plan.wait_text, vec!["READY"]);
        assert!(text_plan.wait_text_absent.is_empty());
    }

    #[tokio::test(start_paused = true)]
    async fn wait_for_text_settles_after_quiet_window() {
        let (revisions, revisions_rx) = watch::channel(0u64);
        let (_events, events_rx) = watch::channel(0u64);
        let stopped = Arc::new(AtomicBool::new(false));
        let rows = Arc::new(Mutex::new(vec!["$ ".to_string()]));
        let rev = Arc::new(AtomicU64::new(0));
        let conditions = TextConditions {
            present: vec!["READY".into()],
            absent: vec!["BUSY".into()],
        };
        let started = Instant::now();
        let flag = stopped.clone();
        let screen = rows.clone();
        let current = rev.clone();
        let handle = tokio::spawn(async move {
            wait_for_text(
                revisions_rx,
                events_rx,
                move || flag.load(Ordering::SeqCst),
                move || {
                    Some((
                        screen.lock().unwrap().clone(),
                        current.load(Ordering::SeqCst),
                    ))
                },
                &conditions,
                started,
                started + Duration::from_millis(5000),
                Duration::from_millis(150),
            )
            .await
        });

        tokio::task::yield_now().await;
        tokio::time::advance(Duration::from_millis(100)).await;
        *rows.lock().unwrap() = vec!["READY BUSY".into()];
        rev.store(1, Ordering::SeqCst);
        revisions.send_replace(1);
        tokio::task::yield_now().await;
        tokio::time::advance(Duration::from_millis(200)).await;
        tokio::task::yield_now().await;
        assert!(!handle.is_finished(), "must not settle while BUSY is shown");

        *rows.lock().unwrap() = vec!["READY".into()];
        rev.store(2, Ordering::SeqCst);
        revisions.send_replace(2);
        tokio::task::yield_now().await;
        tokio::time::advance(Duration::from_millis(150)).await;
        let verdict = handle.await.unwrap();
        assert!(verdict.holds && verdict.settled && !verdict.exited);
    }
}
