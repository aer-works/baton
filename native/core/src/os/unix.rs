use super::{
    spawn_drain_thread, ChunkMsg, KillHandle, OsHandle, OsProcess, OutputSinks, SpawnOptions,
};
use crate::AerError;
use std::io;
use std::os::unix::process::CommandExt;
use std::process::{Command, Stdio};
use std::thread;
use std::time::{Duration, Instant};

pub(crate) struct UnixProcess;

impl OsProcess for UnixProcess {
    fn spawn(
        program: &str,
        args: &[&str],
        options: SpawnOptions<'_>,
    ) -> Result<OsHandle, AerError> {
        let mut cmd = Command::new(program);
        cmd.args(args)
            .stdin(Stdio::null())
            // Pipes are required even though output is not surfaced to callers.
            // Without draining, a child writing beyond the OS pipe buffer deadlocks
            // child.wait(). Never use Stdio::inherit here.
            .stdout(Stdio::piped())
            .stderr(Stdio::piped());

        // env_clear (if requested) must run before applying with_env entries,
        // otherwise it would wipe out the very vars we just set.
        if options.clear_env {
            cmd.env_clear();
        }
        for (key, value) in options.env {
            cmd.env(key, value);
        }
        if let Some(cwd) = options.cwd {
            cmd.current_dir(cwd);
        }

        // SAFETY: The closure only calls setsid(), which is documented as
        // async-signal-safe — safe to call between fork and exec.
        let child = unsafe {
            cmd.pre_exec(|| {
                // setsid() makes the child the leader of a new session and process
                // group. After exec, child PID == PGID, so killpg(child_pid, sig)
                // broadcasts to the entire process tree rooted here.
                if libc::setsid() < 0 {
                    return Err(io::Error::last_os_error());
                }
                Ok(())
            })
        }
        .spawn()
        .map_err(AerError::SpawnFailed)?;

        let pid = child.id();
        Ok(OsHandle {
            pid,
            child,
            kill: KillHandle { pgid: pid },
        })
    }

    fn wait(handle: OsHandle, sinks: OutputSinks) -> Result<i32, AerError> {
        let OsHandle {
            mut child, kill, ..
        } = handle;
        let pgid = kill.pgid;

        // One drain thread per pipe so stdout and stderr are drained concurrently.
        // Sequential draining deadlocks if the child fills the stderr buffer while
        // the drain thread is still blocked on stdout (or vice versa). Both threads
        // must start before child.wait() is called.
        let stdout_drain = child
            .stdout
            .take()
            .map(|out| spawn_drain_thread(out, sinks.stdout, ChunkMsg::Stdout));
        let stderr_drain = child
            .stderr
            .take()
            .map(|err| spawn_drain_thread(err, sinks.stderr, ChunkMsg::Stderr));

        // Save result so cleanup always runs even if wait fails (e.g. ECHILD).
        // Skipping killpg on wait error would leave grandchildren as orphans.
        let wait_res = child.wait();

        // Kill the entire process group after root exits. On the timeout path,
        // kill_escalating already sent SIGKILL; ESRCH (empty group) is not an error.
        // On the natural-exit path, this terminates any grandchildren that inherited
        // stdout/stderr handles, unblocking the drain threads below.
        if unsafe { libc::killpg(pgid as i32, libc::SIGKILL) } != 0 {
            let e = io::Error::last_os_error();
            if e.raw_os_error() != Some(libc::ESRCH) {
                // Best-effort: do not lose the exit code over a cleanup failure.
            }
        }

        if let Some(t) = stdout_drain {
            let _ = t.join();
        }
        if let Some(t) = stderr_drain {
            let _ = t.join();
        }

        Ok(wait_res.map_err(AerError::WaitFailed)?.code().unwrap_or(-1))
    }

    fn kill_escalating(kill: KillHandle, grace: Duration) -> Result<(), AerError> {
        // killpg broadcasts to the entire process group. After setsid, the child's
        // PGID == its PID, so kill.pgid == the pid passed to spawn.
        //
        // Pre-setsid race: setsid() runs in the child *after* fork (pre_exec), so
        // a kill arriving in the fork-to-setsid window finds no process group yet
        // and killpg fails with ESRCH even though the child is alive. On ESRCH we
        // therefore fall back to signaling the pid directly (on Unix the pgid
        // value IS the root pid). A pre-exec child cannot have spawned
        // grandchildren, so the direct signal is complete coverage; post-setsid,
        // killpg succeeds and the fallback never fires. The fallback's own result
        // is ignored: ESRCH there means the process is genuinely gone.
        //
        // SIGTERM: polite request; gives the group a chance to clean up.
        if unsafe { libc::killpg(kill.pgid as i32, libc::SIGTERM) } != 0 {
            let e = io::Error::last_os_error();
            if e.raw_os_error() == Some(libc::ESRCH) {
                let _ = unsafe { libc::kill(kill.pgid as i32, libc::SIGTERM) };
            } else {
                return Err(AerError::KillFailed(e));
            }
        }
        // Poll for group death during the grace window instead of sleeping it out
        // unconditionally (#76): a child that exits promptly on SIGTERM must not
        // cost the caller (timeout monitor, cancel thread, ~6 tests) the full
        // grace period. tree_alive is the same probe the cancel/timeout paths
        // use; the concurrent wait() reaps the dead root promptly, so the probe
        // converges to "dead" as soon as the tree is gone — at which point there
        // is nothing left to SIGKILL and we are done.
        let deadline = Instant::now() + grace;
        loop {
            if !Self::tree_alive(&kill) {
                return Ok(());
            }
            let now = Instant::now();
            if now >= deadline {
                break;
            }
            thread::sleep(Duration::from_millis(25).min(deadline - now));
        }
        // SIGKILL: cannot be caught or ignored. ESRCH here usually means the group
        // is already gone (responded to SIGTERM) — not an error — but it can also
        // be the pre-setsid window again, so send the direct-pid fallback too.
        if unsafe { libc::killpg(kill.pgid as i32, libc::SIGKILL) } != 0 {
            let e = io::Error::last_os_error();
            if e.raw_os_error() == Some(libc::ESRCH) {
                let _ = unsafe { libc::kill(kill.pgid as i32, libc::SIGKILL) };
            } else {
                return Err(AerError::KillFailed(e));
            }
        }
        Ok(())
    }

    fn tree_alive(kill: &KillHandle) -> bool {
        // Signal 0 sends nothing but still performs existence/permission checks.
        // ESRCH means no process in the group exists; any other outcome
        // (success, or a permission-style error) is treated as "still alive"
        // so callers fail toward killing rather than orphaning.
        if unsafe { libc::killpg(kill.pgid as i32, 0) } == 0 {
            return true;
        }
        if io::Error::last_os_error().raw_os_error() != Some(libc::ESRCH) {
            return true;
        }
        // ESRCH on the group can also mean the child is in the fork-to-setsid
        // window where the group does not exist yet but the process does (see
        // kill_escalating). Probe the pid directly before concluding the tree is
        // dead — otherwise a cancel() landing in that window skips its kill
        // entirely and orphans the child.
        if unsafe { libc::kill(kill.pgid as i32, 0) } == 0 {
            return true;
        }
        io::Error::last_os_error().raw_os_error() != Some(libc::ESRCH)
    }

    fn reap_abandoned(kill: &KillHandle) {
        // The abandoned root (pgid value == root pid) was just SIGKILLed but its
        // `Child` gets dropped without wait() on the unwind path, which would
        // leave a zombie in the caller's process — one that still answers
        // kill(pid, 0), so "is it alive" probes never see it die. A blocking
        // waitpid is safe here: SIGKILL cannot be blocked, so exit is imminent.
        // ECHILD (already reaped — e.g. the detached capture-path wait thread
        // got there first, or the pid is not our child) is ignored.
        let _ = unsafe { libc::waitpid(kill.pgid as i32, std::ptr::null_mut(), 0) };
    }
}
