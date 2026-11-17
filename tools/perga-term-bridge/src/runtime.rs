//! Cross-platform PTY backend trait, optional Linux `unshare` containment with clean fallback,
//! 64KB ring buffer, and terminal session lifecycle manager.
//! Purified from `tools/rust-terminal/calm-terminal-runtime` with `portable-pty` backend support.

use crate::actions::{Encoded, encode_rpc_action};
use crate::view::{Frame, MAX_COLS, MAX_ROWS, TerminalView};
use crate::wait_plan::{WaitFor, WaitPlan, wait_for_change, wait_for_text};
use anyhow::{Context, Result, bail, ensure};
use bytes::Bytes;
use portable_pty::{CommandBuilder, MasterPty, PtySize, native_pty_system};
use serde_json::Value;
use std::collections::{BTreeMap, HashMap, VecDeque};
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::process::Command;
use std::sync::{Arc, Mutex};
use std::time::Duration;
use tokio::sync::{Mutex as AsyncMutex, broadcast, watch};
use tokio::time::Instant;

/// Exact capacity of the per-terminal history replay buffer pushed on WebSocket connect.
pub const RING_BUFFER_CAPACITY: usize = 64 * 1024;

/// Short gap between text and trailing CR on `submit` actions so TUI/readline readers
/// distinguish typed characters from the final Enter keystroke.
const SUBMIT_CR_GAP: Duration = Duration::from_millis(15);

/// Fixed-capacity byte ring buffer retaining the most recent `capacity` bytes.
#[derive(Debug)]
pub struct ByteRingBuffer {
    buf: VecDeque<u8>,
    capacity: usize,
}

impl ByteRingBuffer {
    pub fn new(capacity: usize) -> Self {
        Self {
            buf: VecDeque::with_capacity(capacity),
            capacity,
        }
    }

    pub fn len(&self) -> usize {
        self.buf.len()
    }

    pub fn is_empty(&self) -> bool {
        self.buf.is_empty()
    }

    pub fn push(&mut self, bytes: &[u8]) {
        if self.capacity == 0 || bytes.is_empty() {
            return;
        }
        if bytes.len() >= self.capacity {
            self.buf.clear();
            self.buf
                .extend(&bytes[bytes.len().saturating_sub(self.capacity)..]);
            return;
        }
        let overflow = (self.buf.len() + bytes.len()).saturating_sub(self.capacity);
        if overflow > 0 {
            self.buf.drain(..overflow);
        }
        self.buf.extend(bytes);
    }

    pub fn snapshot(&self) -> Vec<u8> {
        let (front, back) = self.buf.as_slices();
        let mut out = Vec::with_capacity(self.buf.len());
        out.extend_from_slice(front);
        out.extend_from_slice(back);
        out
    }
}

/// Optional process containment policy purified from `calm-terminal-runtime`.
/// On Linux, `UnshareIfAvailable` probes `unshare --user --map-root-user --pid --fork`
/// and cleanly falls back to a standard PTY subprocess when restricted (sandbox/containers/macOS/Windows).
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub enum ProcessContainment {
    #[default]
    StandardSubprocess,
    UnshareIfAvailable {
        unshare_bin: PathBuf,
    },
}

impl ProcessContainment {
    /// Wrap `(program, args)` with Linux `unshare` if requested and supported by the host kernel/sandbox;
    /// otherwise return `(program, args)` unchanged so macOS, Windows, and restricted Linux sandboxes
    /// always spawn cleanly.
    pub fn resolve_argv(&self, program: &str, args: &[String]) -> (String, Vec<String>) {
        match self {
            Self::StandardSubprocess => (program.to_owned(), args.to_vec()),
            Self::UnshareIfAvailable { unshare_bin } => {
                if Self::linux_unshare_supported(unshare_bin) {
                    let mut wrapped = vec![
                        "--user".to_owned(),
                        "--map-root-user".to_owned(),
                        "--pid".to_owned(),
                        "--fork".to_owned(),
                        "--kill-child=SIGKILL".to_owned(),
                        "--".to_owned(),
                        program.to_owned(),
                    ];
                    wrapped.extend_from_slice(args);
                    (unshare_bin.to_string_lossy().into_owned(), wrapped)
                } else {
                    (program.to_owned(), args.to_vec())
                }
            }
        }
    }

    #[cfg(target_os = "linux")]
    fn linux_unshare_supported(unshare_bin: &Path) -> bool {
        if !unshare_bin.is_absolute() || !unshare_bin.exists() {
            return false;
        }
        Command::new(unshare_bin)
            .args([
                "--user",
                "--map-root-user",
                "--pid",
                "--fork",
                "--kill-child=SIGKILL",
                "--",
                "/bin/true",
            ])
            .stdin(std::process::Stdio::null())
            .stdout(std::process::Stdio::null())
            .stderr(std::process::Stdio::null())
            .status()
            .is_ok_and(|status| status.success())
    }

    #[cfg(not(target_os = "linux"))]
    fn linux_unshare_supported(_unshare_bin: &Path) -> bool {
        false
    }
}

/// Complete specification to spawn a PTY process.
#[derive(Clone, Debug)]
pub struct PtySpawnSpec {
    pub program: String,
    pub args: Vec<String>,
    pub cwd: PathBuf,
    pub env: BTreeMap<String, String>,
    pub cols: u16,
    pub rows: u16,
}

/// Handle to a spawned PTY session returned by [`PtyBackend`].
pub struct SpawnedPty {
    pub pid: Option<u32>,
    pub reader: Box<dyn Read + Send>,
    pub writer: Box<dyn Write + Send>,
    pub controller: Box<dyn PtyControl>,
    pub waiter: Box<dyn PtyChildWaiter>,
}

/// Resize and termination operations on a live PTY.
pub trait PtyControl: Send + Sync {
    fn resize(&self, cols: u16, rows: u16) -> Result<()>;
    fn graceful_terminate(&self, pid: Option<u32>);
    fn force_kill(&self, pid: Option<u32>) -> Result<()>;
}

/// Blocking wait handle for child process exit code collection.
pub trait PtyChildWaiter: Send {
    fn wait_exit(&mut self) -> Result<i32>;
}

/// Pluggable PTY backend abstraction (supports Unix PTY via `portable-pty`, Windows ConPTY, or test fakes).
pub trait PtyBackend: Send + Sync {
    fn spawn(&self, spec: &PtySpawnSpec) -> Result<SpawnedPty>;
}

/// Cross-platform PTY backend powered by `portable-pty` (Unix openpty + Windows ConPTY).
#[derive(Clone, Debug, Default)]
pub struct PortablePtyBackend {
    containment: ProcessContainment,
}

impl PortablePtyBackend {
    pub fn new(containment: ProcessContainment) -> Self {
        Self { containment }
    }
}

struct PortablePtyControl {
    master: Mutex<Box<dyn MasterPty + Send>>,
    killer: Mutex<Box<dyn portable_pty::ChildKiller + Send + Sync>>,
}

impl PtyControl for PortablePtyControl {
    fn resize(&self, cols: u16, rows: u16) -> Result<()> {
        let guard = self
            .master
            .lock()
            .map_err(|_| anyhow::anyhow!("pty master lock poisoned"))?;
        guard.resize(PtySize {
            rows,
            cols,
            pixel_width: 0,
            pixel_height: 0,
        })?;
        Ok(())
    }

    fn graceful_terminate(&self, pid: Option<u32>) {
        #[cfg(unix)]
        if let Some(raw_pid) = pid.and_then(|p| i32::try_from(p).ok()).filter(|&p| p > 0)
            && let Some(rustix_pid) = rustix::process::Pid::from_raw(raw_pid)
        {
            let _ = rustix::process::kill_process_group(rustix_pid, rustix::process::Signal::TERM);
            let _ = rustix::process::kill_process(rustix_pid, rustix::process::Signal::TERM);
            let _ = rustix::process::kill_process_group(rustix_pid, rustix::process::Signal::HUP);
        }
        #[cfg(not(unix))]
        let _ = pid;
    }

    fn force_kill(&self, pid: Option<u32>) -> Result<()> {
        #[cfg(unix)]
        if let Some(raw_pid) = pid.and_then(|p| i32::try_from(p).ok()).filter(|&p| p > 0)
            && let Some(rustix_pid) = rustix::process::Pid::from_raw(raw_pid)
        {
            let _ = rustix::process::kill_process_group(rustix_pid, rustix::process::Signal::KILL);
            let _ = rustix::process::kill_process(rustix_pid, rustix::process::Signal::KILL);
        }
        #[cfg(not(unix))]
        let _ = pid;

        if let Ok(mut killer) = self.killer.lock() {
            let _ = killer.kill();
        }
        Ok(())
    }
}

struct PortableChildWaiter {
    child: Box<dyn portable_pty::Child + Send + Sync>,
}

impl PtyChildWaiter for PortableChildWaiter {
    fn wait_exit(&mut self) -> Result<i32> {
        let status = self.child.wait()?;
        let code = i32::try_from(status.exit_code()).unwrap_or(1);
        Ok(code)
    }
}

impl PtyBackend for PortablePtyBackend {
    fn spawn(&self, spec: &PtySpawnSpec) -> Result<SpawnedPty> {
        let pty_system = native_pty_system();
        let pair = pty_system.openpty(PtySize {
            rows: spec.rows,
            cols: spec.cols,
            pixel_width: 0,
            pixel_height: 0,
        })?;

        let (resolved_prog, resolved_args) =
            self.containment.resolve_argv(&spec.program, &spec.args);
        let mut cmd = CommandBuilder::new(&resolved_prog);
        for arg in &resolved_args {
            cmd.arg(arg);
        }
        cmd.cwd(&spec.cwd);
        cmd.env("TERM", "xterm-256color");
        cmd.env("COLORTERM", "truecolor");
        for (key, value) in &spec.env {
            cmd.env(key, value);
        }

        let child = pair
            .slave
            .spawn_command(cmd)
            .with_context(|| format!("failed to spawn PTY command {:?}", spec.program))?;
        drop(pair.slave);

        let pid = child.process_id();
        let killer = child.clone_killer();
        let reader = pair.master.try_clone_reader()?;
        let writer = pair.master.take_writer()?;

        Ok(SpawnedPty {
            pid,
            reader,
            writer,
            controller: Box::new(PortablePtyControl {
                master: Mutex::new(pair.master),
                killer: Mutex::new(killer),
            }),
            waiter: Box::new(PortableChildWaiter { child }),
        })
    }
}

/// Live broadcast event emitted to attached `/ws/{terminal_id}` subscribers.
#[derive(Clone, Debug)]
pub enum SessionBroadcast {
    Output { seq: u64, bytes: Bytes },
    Exited { exit_code: Option<i32> },
}

/// Snapshot returned when a WebSocket client attaches to a terminal session.
pub struct WsSubscription {
    pub history: Vec<u8>,
    pub snapshot_seq: u64,
    pub initial_exit: Option<Option<i32>>,
    pub rx: broadcast::Receiver<SessionBroadcast>,
}

struct SessionState {
    view: TerminalView,
    ring_buffer: ByteRingBuffer,
    revision: u64,
    ack_revision: u64,
    output_seq: u64,
    exited: bool,
    exit_code: Option<i32>,
}

pub struct ManagedTerminal {
    terminal_id: String,
    pid: i32,
    state: Mutex<SessionState>,
    writer: AsyncMutex<Option<Box<dyn Write + Send>>>,
    controller: Box<dyn PtyControl>,
    revisions_tx: watch::Sender<u64>,
    events_tx: watch::Sender<u64>,
    broadcast_tx: broadcast::Sender<SessionBroadcast>,
}

#[derive(Clone, Debug)]
pub struct EnsureSpec {
    pub terminal_id: String,
    pub program: String,
    pub args: Vec<String>,
    pub cwd: PathBuf,
    pub env: BTreeMap<String, String>,
    pub cols: u16,
    pub rows: u16,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct EnsureOutcome {
    pub terminal_id: String,
    pub pid: i32,
    pub created: bool,
}

#[derive(Clone, Debug)]
pub struct ObserveOutcome {
    pub revision: u64,
    pub frame: Frame,
    pub exited: bool,
    pub exit_code: Option<i32>,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct KillOutcome {
    pub exited: bool,
    pub exit_code: Option<i32>,
}

impl ManagedTerminal {
    fn start(terminal_id: String, cols: u16, rows: u16, spawned: SpawnedPty) -> Result<Arc<Self>> {
        let view = TerminalView::new(cols, rows, [220, 220, 220], [15, 20, 24])?;
        let pid = spawned
            .pid
            .and_then(|p| i32::try_from(p).ok())
            .unwrap_or(0);
        let (revisions_tx, _) = watch::channel(0u64);
        let (events_tx, _) = watch::channel(0u64);
        let (broadcast_tx, _) = broadcast::channel(1024);

        let session = Arc::new(Self {
            terminal_id,
            pid,
            state: Mutex::new(SessionState {
                view,
                ring_buffer: ByteRingBuffer::new(RING_BUFFER_CAPACITY),
                revision: 0,
                ack_revision: 0,
                output_seq: 0,
                exited: false,
                exit_code: None,
            }),
            writer: AsyncMutex::new(Some(spawned.writer)),
            controller: spawned.controller,
            revisions_tx,
            events_tx,
            broadcast_tx,
        });

        let reader_done = Arc::new(tokio::sync::Notify::new());
        {
            let session_ref = Arc::clone(&session);
            let done_notify = Arc::clone(&reader_done);
            let mut reader = spawned.reader;
            std::thread::Builder::new()
                .name(format!("perga-pty-read-{}", session.terminal_id))
                .spawn(move || {
                    let mut buf = [0u8; 8192];
                    loop {
                        match reader.read(&mut buf) {
                            Ok(0) => break,
                            Ok(n) => {
                                session_ref.ingest_pty_output(&buf[..n]);
                            }
                            Err(err) if err.kind() == std::io::ErrorKind::Interrupted => continue,
                            Err(_) => break,
                        }
                    }
                    done_notify.notify_waiters();
                })
                .context("failed to spawn PTY reader thread")?;
        }

        {
            let session_ref = Arc::clone(&session);
            let mut waiter = spawned.waiter;
            tokio::spawn(async move {
                let code = tokio::task::spawn_blocking(move || waiter.wait_exit())
                    .await
                    .ok()
                    .and_then(Result::ok);
                // Allow any trailing buffered PTY output to drain into TerminalView & ring buffer before marking exit.
                let _ = tokio::time::timeout(Duration::from_millis(80), reader_done.notified()).await;
                session_ref.record_exit(code);
            });
        }

        Ok(session)
    }

    pub fn pid(&self) -> i32 {
        self.pid
    }

    fn ingest_pty_output(&self, chunk: &[u8]) {
        if chunk.is_empty() {
            return;
        }
        let (next_rev, seq) = {
            let mut guard = self.state.lock().unwrap_or_else(|e| e.into_inner());
            guard.view.feed(chunk);
            guard.ring_buffer.push(chunk);
            guard.revision = guard.revision.saturating_add(1);
            guard.output_seq = guard.output_seq.saturating_add(1);
            (guard.revision, guard.output_seq)
        };
        let _ = self.revisions_tx.send_replace(next_rev);
        let _ = self.broadcast_tx.send(SessionBroadcast::Output {
            seq,
            bytes: Bytes::copy_from_slice(chunk),
        });
    }

    fn record_exit(&self, exit_code: Option<i32>) {
        let newly_exited = {
            let mut guard = self.state.lock().unwrap_or_else(|e| e.into_inner());
            if guard.exited {
                if guard.exit_code.is_none() {
                    guard.exit_code = exit_code;
                }
                false
            } else {
                guard.exited = true;
                guard.exit_code = exit_code;
                true
            }
        };
        if newly_exited {
            self.events_tx.send_modify(|v| *v = v.saturating_add(1));
            let _ = self.broadcast_tx.send(SessionBroadcast::Exited { exit_code });
        }
    }

    pub fn is_exited(&self) -> bool {
        self.state
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .exited
    }

    pub fn attach_ws(&self) -> WsSubscription {
        let guard = self.state.lock().unwrap_or_else(|e| e.into_inner());
        let rx = self.broadcast_tx.subscribe();
        let history = guard.ring_buffer.snapshot();
        let snapshot_seq = guard.output_seq;
        let initial_exit = guard.exited.then_some(guard.exit_code);
        WsSubscription {
            history,
            snapshot_seq,
            initial_exit,
            rx,
        }
    }

    /// Snapshot the current 64KB ring buffer and sequence (used to resync lagged WS subscribers).
    pub fn snapshot_history(&self) -> (Vec<u8>, u64) {
        let guard = self.state.lock().unwrap_or_else(|e| e.into_inner());
        (guard.ring_buffer.snapshot(), guard.output_seq)
    }

    pub async fn observe(
        &self,
        scroll_offset: usize,
        wait_plan: Option<WaitPlan>,
    ) -> Result<ObserveOutcome> {
        let baseline = {
            let guard = self.state.lock().unwrap_or_else(|e| e.into_inner());
            guard.revision
        };

        if let Some(plan) = wait_plan {
            let started = Instant::now();
            let budget = Duration::from_millis(plan.budget_ms);
            let deadline = started + budget;
            let settle = Duration::from_millis(plan.settle_ms);
            match plan.mode {
                WaitFor::Elapsed => {
                    if plan.budget_ms > 0 {
                        let mut events = self.events_tx.subscribe();
                        let sleep = tokio::time::sleep(budget);
                        tokio::pin!(sleep);
                        loop {
                            if self.is_exited() {
                                break;
                            }
                            tokio::select! {
                                _ = &mut sleep => break,
                                res = events.changed() => {
                                    if res.is_err() || self.is_exited() {
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }
                WaitFor::Change => {
                    let revisions_rx = self.revisions_tx.subscribe();
                    let events_rx = self.events_tx.subscribe();
                    let _ = wait_for_change(
                        revisions_rx,
                        events_rx,
                        || self.is_exited(),
                        baseline,
                        deadline,
                        settle,
                    )
                    .await;
                }
                WaitFor::Text => {
                    let revisions_rx = self.revisions_tx.subscribe();
                    let events_rx = self.events_tx.subscribe();
                    let conditions = plan.conditions();
                    let _ = wait_for_text(
                        revisions_rx,
                        events_rx,
                        || self.is_exited(),
                        || {
                            let guard = self.state.lock().unwrap_or_else(|e| e.into_inner());
                            guard.view.frame(0).ok().map(|f| (f.text, guard.revision))
                        },
                        &conditions,
                        started,
                        deadline,
                        settle,
                    )
                    .await;
                }
                WaitFor::Signal => {
                    if plan.budget_ms > 0 {
                        tokio::time::sleep(budget).await;
                    }
                }
            }
        }

        let guard = self.state.lock().unwrap_or_else(|e| e.into_inner());
        let frame = guard.view.frame(scroll_offset)?;
        Ok(ObserveOutcome {
            revision: guard.revision,
            frame,
            exited: guard.exited,
            exit_code: guard.exit_code,
        })
    }

    pub async fn send_action(&self, kind: &str, payload: &Value) -> Result<u64> {
        let mut writer_guard = self.writer.lock().await;
        let (surface, exited) = {
            let guard = self.state.lock().unwrap_or_else(|e| e.into_inner());
            (guard.view.input_surface(), guard.exited)
        };
        ensure!(
            !exited,
            "terminal {:?} has already exited",
            self.terminal_id
        );
        let encoded = encode_rpc_action(kind, payload, &surface)?;
        let writer = writer_guard
            .as_mut()
            .ok_or_else(|| anyhow::anyhow!("terminal {:?} stdin is closed", self.terminal_id))?;

        match encoded {
            Encoded::Bytes(bytes) => {
                if !bytes.is_empty() {
                    writer.write_all(&bytes)?;
                    writer.flush()?;
                }
            }
            Encoded::Submit(bytes) => {
                if bytes.len() > 1 {
                    let text_part = &bytes[..bytes.len() - 1];
                    writer.write_all(text_part)?;
                    writer.flush()?;
                    tokio::time::sleep(SUBMIT_CR_GAP).await;
                }
                writer.write_all(b"\r")?;
                writer.flush()?;
            }
        }

        let ack = {
            let mut guard = self.state.lock().unwrap_or_else(|e| e.into_inner());
            let next = guard.ack_revision.saturating_add(1).max(guard.revision);
            guard.ack_revision = next;
            next
        };
        Ok(ack)
    }

    pub async fn write_raw(&self, bytes: &[u8]) -> Result<()> {
        if bytes.is_empty() {
            return Ok(());
        }
        let mut writer_guard = self.writer.lock().await;
        if self.is_exited() {
            bail!("terminal {:?} has already exited", self.terminal_id);
        }
        let writer = writer_guard
            .as_mut()
            .ok_or_else(|| anyhow::anyhow!("terminal {:?} stdin is closed", self.terminal_id))?;
        writer.write_all(bytes)?;
        writer.flush()?;
        Ok(())
    }

    pub fn resize(&self, cols: u16, rows: u16) -> Result<()> {
        ensure!(
            (1..=MAX_COLS).contains(&cols) && (1..=MAX_ROWS).contains(&rows),
            "terminal geometry ({cols}x{rows}) out of supported range"
        );
        if !self.is_exited() {
            self.controller.resize(cols, rows)?;
        }
        let next_rev = {
            let mut guard = self.state.lock().unwrap_or_else(|e| e.into_inner());
            guard.view.resize(cols, rows)?;
            guard.revision = guard.revision.saturating_add(1);
            guard.revision
        };
        let _ = self.revisions_tx.send_replace(next_rev);
        Ok(())
    }

    pub async fn kill(&self, grace_ms: u64) -> Result<KillOutcome> {
        {
            let guard = self.state.lock().unwrap_or_else(|e| e.into_inner());
            if guard.exited {
                return Ok(KillOutcome {
                    exited: true,
                    exit_code: guard.exit_code,
                });
            }
        }

        let pid = u32::try_from(self.pid).ok().filter(|&p| p > 0);
        // Close stdin writer first so processes waiting on stdin EOF can exit cleanly during grace window.
        {
            let mut writer_guard = self.writer.lock().await;
            *writer_guard = None;
        }

        let mut events_rx = self.events_tx.subscribe();
        if grace_ms > 0 {
            self.controller.graceful_terminate(pid);
            let grace = Duration::from_millis(grace_ms.min(30_000));
            let _ = tokio::time::timeout(grace, async {
                while !self.is_exited() {
                    if events_rx.changed().await.is_err() {
                        break;
                    }
                }
            })
            .await;
        }

        if !self.is_exited() {
            let _ = self.controller.force_kill(pid);
            let _ = tokio::time::timeout(Duration::from_secs(3), async {
                while !self.is_exited() {
                    if events_rx.changed().await.is_err() {
                        break;
                    }
                }
            })
            .await;
        }

        let mut guard = self.state.lock().unwrap_or_else(|e| e.into_inner());
        if !guard.exited {
            guard.exited = true;
            if guard.exit_code.is_none() {
                guard.exit_code = Some(137);
            }
            let code = guard.exit_code;
            drop(guard);
            self.events_tx.send_modify(|v| *v = v.saturating_add(1));
            let _ = self
                .broadcast_tx
                .send(SessionBroadcast::Exited { exit_code: code });
            return Ok(KillOutcome {
                exited: true,
                exit_code: code,
            });
        }
        Ok(KillOutcome {
            exited: true,
            exit_code: guard.exit_code,
        })
    }
}

/// Thread-safe registry of live and retained terminal sessions.
pub struct SessionManager {
    backend: Arc<dyn PtyBackend>,
    sessions: AsyncMutex<HashMap<String, Arc<ManagedTerminal>>>,
}

impl Default for SessionManager {
    fn default() -> Self {
        Self::new(Arc::new(PortablePtyBackend::default()))
    }
}

impl SessionManager {
    pub fn new(backend: Arc<dyn PtyBackend>) -> Self {
        Self {
            backend,
            sessions: AsyncMutex::new(HashMap::new()),
        }
    }

    pub async fn get(&self, terminal_id: &str) -> Option<Arc<ManagedTerminal>> {
        let guard = self.sessions.lock().await;
        guard.get(terminal_id).cloned()
    }

    pub async fn ensure(&self, spec: EnsureSpec) -> Result<EnsureOutcome> {
        let mut guard = self.sessions.lock().await;
        if let Some(existing) = guard.get(&spec.terminal_id) {
            return Ok(EnsureOutcome {
                terminal_id: spec.terminal_id,
                pid: existing.pid(),
                created: false,
            });
        }

        ensure!(!spec.terminal_id.trim().is_empty(), "terminal_id is required");
        ensure!(
            !spec.program.trim().is_empty() && !spec.program.contains('\0'),
            "program must be a non-empty string without NUL bytes"
        );
        ensure!(
            !spec.args.iter().any(|a| a.contains('\0')),
            "args must not contain NUL bytes"
        );
        ensure!(
            (1..=MAX_COLS).contains(&spec.cols) && (1..=MAX_ROWS).contains(&spec.rows),
            "cols/rows out of supported range"
        );
        ensure!(
            spec.cwd.is_dir(),
            "cwd does not exist or is not a directory: {}",
            spec.cwd.display()
        );
        for (key, value) in &spec.env {
            ensure!(
                !key.is_empty() && !key.contains('=') && !key.contains('\0') && !value.contains('\0'),
                "invalid environment variable assignment for {key:?}"
            );
        }

        let spawn_spec = PtySpawnSpec {
            program: spec.program,
            args: spec.args,
            cwd: spec.cwd,
            env: spec.env,
            cols: spec.cols,
            rows: spec.rows,
        };
        let spawned = self.backend.spawn(&spawn_spec)?;
        let managed =
            ManagedTerminal::start(spec.terminal_id.clone(), spec.cols, spec.rows, spawned)?;
        let pid = managed.pid();
        guard.insert(spec.terminal_id.clone(), managed);
        Ok(EnsureOutcome {
            terminal_id: spec.terminal_id,
            pid,
            created: true,
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ring_buffer_retains_exact_tail_64kb() {
        let mut ring = ByteRingBuffer::new(RING_BUFFER_CAPACITY);
        assert!(ring.is_empty());

        let total = RING_BUFFER_CAPACITY + 20_000;
        let payload: Vec<u8> = (0..total).map(|i| (i % 251) as u8).collect();
        for chunk in payload.chunks(4096) {
            ring.push(chunk);
        }
        assert_eq!(ring.len(), RING_BUFFER_CAPACITY);
        assert_eq!(
            ring.snapshot(),
            payload[total - RING_BUFFER_CAPACITY..].to_vec()
        );
    }

    #[test]
    fn optional_unshare_containment_falls_back_cleanly_when_unavailable() {
        let containment = ProcessContainment::UnshareIfAvailable {
            unshare_bin: PathBuf::from("/nonexistent/unshare-binary"),
        };
        let (prog, args) = containment.resolve_argv("/bin/sh", &["-c".into(), "echo ok".into()]);
        assert_eq!(prog, "/bin/sh");
        assert_eq!(args, vec!["-c", "echo ok"]);
    }
}
