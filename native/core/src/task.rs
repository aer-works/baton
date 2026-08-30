use crate::{
    event::{Event, ExitReason},
    machine::{State, StateMachine},
    os::{ChunkMsg, KillHandle, OsProcess, OutputSinks, PlatformProcess, SpawnOptions},
    AerError,
};
use std::io;
use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{mpsc, Arc, Mutex};
use std::thread;
use std::time::Duration;

const KILL_GRACE: Duration = Duration::from_secs(5);

/// Drop-based scope guard that kills the process tree if `run_impl` unwinds or
/// returns early before the process is known dead (e.g. the caller's event
/// callback panics, or a `?` on a transition/wait error fires). Armed right
/// after spawn and disarmed once `wait()` has successfully returned an exit
/// code, at which point the process is confirmed dead and there is nothing
/// left to clean up.
struct KillOnDropGuard {
    kill: KillHandle,
    armed: bool,
}

impl KillOnDropGuard {
    fn new(kill: KillHandle) -> Self {
        Self { kill, armed: true }
    }

    fn disarm(&mut self) {
        self.armed = false;
    }
}

impl Drop for KillOnDropGuard {
    fn drop(&mut self) {
        if self.armed {
            // Grace of zero is deliberate: on the panic/early-return path we are
            // abandoning the run, so immediate SIGKILL-equivalent semantics are
            // correct — there's no caller left to observe a graceful shutdown.
            // Errors are ignored: Drop must not panic, and there's no one left
            // to report a kill failure to.
            let _ = PlatformProcess::kill_escalating(self.kill.clone(), Duration::ZERO);
            // On this path the Child is dropped without wait(), so reap the
            // killed root explicitly — otherwise it lingers as a zombie in the
            // caller's process on Unix (no-op on Windows).
            PlatformProcess::reap_abandoned(&self.kill);
        }
    }
}

/// A handle that allows an external caller to cancel a running task.
///
/// Obtained before `run_with_cancel` is called. The handle can be cloned and
/// shared across threads. Calling `cancel()` is idempotent — only the first
/// call has effect. Calling `cancel()` before the process has started or after
/// it has already exited is safe and has no effect on the reported exit reason.
#[derive(Clone)]
pub struct CancelHandle {
    /// Set true the first time cancel() is called.
    cancelled: Arc<AtomicBool>,
    /// Set true only when cancel() actually fired a kill while the process was live.
    /// Used by run_impl to determine whether exit reason is CancelRequested.
    kill_fired: Arc<AtomicBool>,
    /// Non-None only between spawn and wait() returning. Mutex guards concurrent cancel().
    kill: Arc<Mutex<Option<KillHandle>>>,
}

impl Default for CancelHandle {
    fn default() -> Self {
        Self::new()
    }
}

impl CancelHandle {
    pub fn new() -> Self {
        Self {
            cancelled: Arc::new(AtomicBool::new(false)),
            kill_fired: Arc::new(AtomicBool::new(false)),
            kill: Arc::new(Mutex::new(None)),
        }
    }

    /// Request cancellation. If the process is running, it is killed immediately
    /// using the same escalation as a timeout kill. If the process has not started
    /// yet or has already exited, this is a no-op (the exit reason is unaffected).
    ///
    /// Note on the natural-exit race: the kill handle stays wired up until
    /// `run_impl` clears it just after `wait()` returns, so `cancel()` can still
    /// observe `Some(kill)` for a process that has, in fact, already exited. A
    /// `tree_alive` probe immediately before the kill narrows this window to the
    /// probe-then-kill gap itself — a process that dies in the instant between
    /// the probe returning "alive" and the kill actually landing is still
    /// (harmlessly) reported as cancelled, because kill_escalating on an
    /// already-exited tree is a no-op. What this eliminates is the systematic
    /// misreport where cancel() unconditionally fires on a long-dead process;
    /// it does not — cannot — close the microsecond-scale TOCTOU race inherent
    /// in probe-then-kill.
    ///
    /// Thread-safe. Only the first call has effect; subsequent calls are no-ops.
    pub fn cancel(&self) {
        if !self.cancelled.swap(true, Ordering::SeqCst) {
            if let Ok(guard) = self.kill.lock() {
                if let Some(kill) = guard.as_ref() {
                    if PlatformProcess::tree_alive(kill) {
                        self.kill_fired.store(true, Ordering::SeqCst);
                        let _ = PlatformProcess::kill_escalating(kill.clone(), KILL_GRACE);
                    }
                    // else: the tree already exited naturally (probed just now).
                    // kill_fired stays false so run_impl reports NaturalExit with
                    // the real exit code instead of a spurious CancelRequested.
                }
                // kill is None: called before spawn or after wait() returned.
                // kill_fired stays false; run_impl will not report CancelRequested.
            }
        }
    }

    /// Returns true if `cancel()` has been called at least once.
    pub fn is_cancelled(&self) -> bool {
        self.cancelled.load(Ordering::SeqCst)
    }
}

/// A single-shot task: one command, one execution, deterministic lifecycle.
pub struct Task {
    program: String,
    args: Vec<String>,
    timeout: Option<Duration>,
    capture_output: bool,
    env: Vec<(String, String)>,
    clear_env: bool,
    cwd: Option<PathBuf>,
}

impl Task {
    pub fn new(program: impl Into<String>, args: Vec<impl Into<String>>) -> Self {
        Self {
            program: program.into(),
            args: args.into_iter().map(Into::into).collect(),
            timeout: None,
            capture_output: false,
            env: Vec::new(),
            clear_env: false,
            cwd: None,
        }
    }

    /// Sets a maximum wall-clock duration for the process. If the process has not
    /// exited by the deadline, it is killed and `run()` returns `Err(AerError::TimedOut)`.
    pub fn with_timeout(mut self, timeout: Duration) -> Self {
        self.timeout = Some(timeout);
        self
    }

    /// Enables stdout and stderr capture. When true, `StdoutChunk` and
    /// `StderrChunk` events are emitted between `Started` and `Exited`.
    pub fn with_capture_output(mut self, capture: bool) -> Self {
        self.capture_output = capture;
        self
    }

    /// Sets an environment variable for the child process. Repeatable — if
    /// called more than once with the same key, the last call before `run()`
    /// wins (later calls override earlier ones for that key). Variables set
    /// this way are always visible to the child, regardless of
    /// `with_clear_env`.
    pub fn with_env(mut self, key: impl Into<String>, value: impl Into<String>) -> Self {
        let key = key.into();
        let value = value.into();
        match self.env.iter_mut().find(|(k, _)| *k == key) {
            Some(entry) => entry.1 = value,
            None => self.env.push((key, value)),
        }
        self
    }

    /// When `true`, the child inherits nothing from the parent process's
    /// environment — only variables set via `with_env` are present. Default
    /// `false`, in which case the child inherits the full parent environment
    /// (unchanged behavior from earlier milestones).
    pub fn with_clear_env(mut self, clear: bool) -> Self {
        self.clear_env = clear;
        self
    }

    /// Sets the child process's working directory. If the path does not exist
    /// or is not a directory, this surfaces at spawn time the same way any
    /// other OS-level spawn rejection does: `run()` returns
    /// `Err(AerError::SpawnFailed)` and no events are emitted (std's
    /// `Command::current_dir` behavior).
    pub fn with_cwd(mut self, path: impl Into<PathBuf>) -> Self {
        self.cwd = Some(path.into());
        self
    }

    /// Spawns the process and blocks until it exits.
    ///
    /// Events emitted: `Started`, optional chunks (when capture enabled), then `Exited`.
    /// If spawning fails, `on_event` is never called and `SpawnFailed` is returned.
    pub fn run(&self, on_event: impl FnMut(Event)) -> Result<(), AerError> {
        self.run_impl(on_event, None)
    }

    /// Like `run`, but wires up a `CancelHandle` so an external caller can cancel
    /// the execution mid-flight. If cancelled, `Exited { reason: CancelRequested,
    /// code: -1 }` is emitted and `Err(AerError::Cancelled)` is returned.
    pub fn run_with_cancel<F: FnMut(Event)>(
        &self,
        on_event: F,
        cancel: &CancelHandle,
    ) -> Result<(), AerError> {
        self.run_impl(on_event, Some(cancel))
    }

    fn run_impl<F: FnMut(Event)>(
        &self,
        mut on_event: F,
        cancel: Option<&CancelHandle>,
    ) -> Result<(), AerError> {
        let mut machine = StateMachine::new();

        let str_args: Vec<&str> = self.args.iter().map(String::as_str).collect();
        let spawn_options = SpawnOptions {
            env: &self.env,
            clear_env: self.clear_env,
            cwd: self.cwd.as_deref(),
        };
        let handle = PlatformProcess::spawn(&self.program, &str_args, spawn_options)?;

        // Armed immediately after spawn so any early return or panic below —
        // including one raised by the caller's `on_event` callback — kills the
        // process tree instead of leaking it. Disarmed only once `wait()` has
        // successfully returned an exit code, in both the capture and
        // no-capture paths below.
        let mut kill_guard = KillOnDropGuard::new(handle.kill.clone());

        let pid = handle.pid;
        machine.transition(State::Running)?;
        on_event(Event::Started { pid });

        // Wire the cancel handle to the live kill handle so cancel() can reach the process.
        // Also handle the case where cancel() was called before spawn completed:
        // in that case the flag is set but kill_fired is false; do the kill now
        // (unless the process already raced to natural exit — same probe as cancel()).
        if let Some(ch) = cancel {
            // The guarded data is a plain Option<KillHandle>, always left in a
            // consistent state, so recovering from a poisoned lock is safe.
            *ch.kill.lock().unwrap_or_else(|e| e.into_inner()) = Some(handle.kill.clone());
            if ch.cancelled.load(Ordering::SeqCst)
                && !ch.kill_fired.load(Ordering::SeqCst)
                && PlatformProcess::tree_alive(&handle.kill)
            {
                ch.kill_fired.store(true, Ordering::SeqCst);
                let _ = PlatformProcess::kill_escalating(handle.kill.clone(), KILL_GRACE);
            }
        }

        // Timeout monitor thread: kills the process tree after the deadline.
        let timed_out = Arc::new(AtomicBool::new(false));
        let (cancel_tx, cancel_rx) = mpsc::channel::<()>();

        if let Some(timeout) = self.timeout {
            let kill_for_monitor = handle.kill.clone();
            let timed_out_clone = Arc::clone(&timed_out);
            thread::spawn(move || {
                if let Err(mpsc::RecvTimeoutError::Timeout) = cancel_rx.recv_timeout(timeout) {
                    // Probe before killing: the deadline can fire in the same race
                    // window as cancel() — the process may have exited naturally a
                    // moment earlier. See CancelHandle::cancel()'s doc comment for
                    // the full TOCTOU discussion; the same residual microsecond-scale
                    // race applies here.
                    if PlatformProcess::tree_alive(&kill_for_monitor) {
                        timed_out_clone.store(true, Ordering::SeqCst);
                        let _ = PlatformProcess::kill_escalating(kill_for_monitor, KILL_GRACE);
                    }
                }
            });
        }

        // Capture-enabled path: wait() runs on a spawned thread so this (caller)
        // thread is free to pump the chunk channel and invoke on_event as bytes
        // arrive, instead of blocking until the process exits. The event callback
        // itself never leaves the caller thread — only PlatformProcess::wait runs
        // elsewhere.
        let os_code: i32 = if self.capture_output {
            let (chunk_tx, chunk_rx) = mpsc::channel::<ChunkMsg>();
            let sinks = OutputSinks {
                stdout: Some(chunk_tx.clone()),
                stderr: Some(chunk_tx.clone()),
            };
            // Drop our clone so the channel closes once both drain threads (inside
            // wait()) finish and drop their clones — that's the recv() loop's exit
            // signal below.
            drop(chunk_tx);

            let (wait_tx, wait_rx) = mpsc::channel::<Result<i32, AerError>>();
            let wait_thread = thread::spawn(move || {
                let result = PlatformProcess::wait(handle, sinks);
                let _ = wait_tx.send(result);
            });

            // Live delivery: emit each chunk as it arrives. Ends when every sender
            // has dropped, i.e. once wait()'s drain threads have finished (which
            // happens after wait() terminates the process tree and its pipes close).
            while let Ok(msg) = chunk_rx.recv() {
                match msg {
                    ChunkMsg::Stdout(seq, bytes) => on_event(Event::StdoutChunk { seq, bytes }),
                    ChunkMsg::Stderr(seq, bytes) => on_event(Event::StderrChunk { seq, bytes }),
                }
            }

            // The chunk channel closing means the drain threads are done, so wait()
            // is at most moments from returning; this recv() blocks until it does.
            let result: Result<i32, AerError> = wait_rx.recv().map_err(|_| {
                AerError::WaitFailed(io::Error::other(
                    "wait thread ended without sending a result",
                ))
            })?;
            let _ = wait_thread.join();
            result?
        } else {
            let sinks = OutputSinks {
                stdout: None,
                stderr: None,
            };
            PlatformProcess::wait(handle, sinks)?
        };

        // The process is confirmed dead now that an exit code was obtained
        // (both the capture and no-capture paths above route through the `?`
        // that would have left this guard armed on failure). Disarm so a
        // later panic in the caller's Exited callback doesn't fire a
        // redundant kill against an already-reaped tree.
        kill_guard.disarm();

        // Disconnect the kill handle: process is dead, cancel() is now a no-op.
        if let Some(ch) = cancel {
            // Same poisoning rationale as the wiring lock() above.
            *ch.kill.lock().unwrap_or_else(|e| e.into_inner()) = None;
        }

        let _ = cancel_tx.send(());

        let was_timed_out = timed_out.load(Ordering::SeqCst);
        // kill_fired is true only if cancel() actually killed the process while live.
        let was_cancelled = cancel
            .map(|ch| ch.kill_fired.load(Ordering::SeqCst))
            .unwrap_or(false);

        let reason = if was_timed_out {
            ExitReason::TimedOut
        } else if was_cancelled {
            ExitReason::CancelRequested
        } else {
            ExitReason::NaturalExit
        };

        let code = if was_timed_out || was_cancelled {
            -1
        } else {
            os_code
        };

        // Captured chunks (if any) were already delivered live, above, strictly
        // between Started and this Exited transition.
        machine.transition(State::Exited)?;
        on_event(Event::Exited { code, reason });

        if was_timed_out {
            return Err(AerError::TimedOut);
        }
        if was_cancelled {
            return Err(AerError::Cancelled);
        }

        Ok(())
    }
}
