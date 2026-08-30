use crate::AerError;
use std::io::{self, Read};
use std::path::Path;
use std::process::Child;
use std::sync::mpsc::{self, Sender};
use std::thread::{self, JoinHandle};
use std::time::Duration;

/// Lightweight handle that the timeout monitor thread uses to kill the process tree.
///
/// Windows: wraps an `Arc<JobHandle>` so the OS handle stays alive until all
/// threads are done with it, preventing handle-value recycling races.
/// Unix: wraps the pgid (== pid after setsid); no heap allocation needed.
#[derive(Clone)]
pub(crate) struct KillHandle {
    #[cfg(windows)]
    pub(crate) job: std::sync::Arc<windows::JobHandle>,
    #[cfg(not(windows))]
    pub(crate) pgid: u32,
}

/// Handle to a live OS process.
pub(crate) struct OsHandle {
    pub(crate) pid: u32,
    pub(crate) child: Child,
    pub(crate) kill: KillHandle,
}

/// A single captured chunk, tagged with the stream it came from. Sent over one
/// shared channel so chunks are delivered to the caller in true arrival order
/// while per-stream `seq` ordering (each drain thread's sends are FIFO on its
/// own sender clone) is preserved automatically.
pub(crate) enum ChunkMsg {
    Stdout(u64, Vec<u8>),
    Stderr(u64, Vec<u8>),
}

/// Senders for captured stdout/stderr chunks — clones of the same channel,
/// distinguished by which `ChunkMsg` variant each drain thread sends.
/// `None` means the stream should be silently drained.
pub(crate) struct OutputSinks {
    pub(crate) stdout: Option<mpsc::Sender<ChunkMsg>>,
    pub(crate) stderr: Option<mpsc::Sender<ChunkMsg>>,
}

/// Pre-spawn configuration for a child process's environment and working
/// directory. Applied identically on both platforms via std's `Command`
/// (`env` / `env_clear` / `current_dir`) — no OS-specific logic needed.
pub(crate) struct SpawnOptions<'a> {
    /// Environment variables to set on the child. Applied in order, so a
    /// later entry for the same key overrides an earlier one (mirrors
    /// `Task::with_env`'s override-on-repeat documented behavior).
    pub(crate) env: &'a [(String, String)],
    /// When true, the child does not inherit the parent's environment at
    /// all — only `env` entries above are present.
    pub(crate) clear_env: bool,
    /// Working directory for the child. `None` means inherit the parent's
    /// current working directory (the default, unchanged behavior).
    pub(crate) cwd: Option<&'a Path>,
}

/// Platform abstraction for spawning, waiting on, and killing a process.
/// Implementations must not leak OS-specific behavior into callers.
pub(crate) trait OsProcess {
    fn spawn(program: &str, args: &[&str], options: SpawnOptions<'_>)
        -> Result<OsHandle, AerError>;
    /// Blocks until the process exits. Returns the exit code.
    /// Returns -1 if the OS provides no exit code (e.g. signal-killed on Unix).
    /// When `sinks` contains `Some` senders, drain threads forward captured bytes
    /// to them; `None` sinks are silently discarded.
    fn wait(handle: OsHandle, sinks: OutputSinks) -> Result<i32, AerError>;
    /// Kills the entire process tree. On Unix: SIGTERM → sleep(grace) → SIGKILL
    /// to the process group. On Windows: TerminateJobObject immediately.
    fn kill_escalating(kill: KillHandle, grace: Duration) -> Result<(), AerError>;
    /// Probes whether any process in the tree is still alive. Used by task.rs
    /// to decide whether a cancel/timeout kill would actually be acting on a
    /// live process, or is arriving after the process already exited naturally.
    /// A grandchild that is still alive after the root has exited counts as
    /// "alive" — the whole tree, not just the root, must be gone.
    /// On query failure, implementations fail toward "alive" (killing an
    /// already-dead tree is harmless; skipping a kill on a live one is not).
    fn tree_alive(kill: &KillHandle) -> bool;
    /// Reaps the root process of an abandoned run (panic/early-error path,
    /// where `wait()` was never reached and the `Child` is dropped without
    /// being waited on). Without this, the killed root lingers as a zombie in
    /// the caller's process on Unix — still answering `kill(pid, 0)` probes.
    /// Windows has no zombie concept; that implementation is a no-op.
    /// Must only be called after the tree has been killed.
    fn reap_abandoned(kill: &KillHandle);
}

/// Spawns a thread that drains `reader` in 8192-byte chunks until EOF or error.
///
/// When `tx` is `Some`, each successful read is wrapped via `make_msg` (with a
/// per-thread, monotonically increasing `seq` starting at 0) and sent; a send
/// failure (receiver dropped) is treated the same as any other loop exit — the
/// thread stops draining. When `tx` is `None`, bytes are silently discarded via
/// `io::copy` into `io::sink()` (still required so the child cannot deadlock on
/// a full pipe buffer). Shared by both platform backends so stdout/stderr
/// draining behavior — buffer size, EOF/error handling, seq numbering — stays
/// identical in one place instead of four.
pub(crate) fn spawn_drain_thread(
    mut reader: impl Read + Send + 'static,
    tx: Option<Sender<ChunkMsg>>,
    make_msg: fn(u64, Vec<u8>) -> ChunkMsg,
) -> JoinHandle<()> {
    thread::spawn(move || {
        if let Some(tx) = tx {
            let mut seq = 0u64;
            let mut buf = vec![0u8; 8192];
            loop {
                match reader.read(&mut buf) {
                    Ok(0) | Err(_) => break,
                    Ok(n) => {
                        let _ = tx.send(make_msg(seq, buf[..n].to_vec()));
                        seq += 1;
                    }
                }
            }
        } else {
            let _ = io::copy(&mut reader, &mut io::sink());
        }
    })
}

#[cfg(not(target_os = "windows"))]
mod unix;
#[cfg(target_os = "windows")]
pub(crate) mod windows;

#[cfg(not(target_os = "windows"))]
pub(crate) use unix::UnixProcess as PlatformProcess;
#[cfg(target_os = "windows")]
pub(crate) use windows::WindowsProcess as PlatformProcess;
