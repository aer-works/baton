use super::{
    spawn_drain_thread, ChunkMsg, KillHandle, OsHandle, OsProcess, OutputSinks, SpawnOptions,
};
use crate::AerError;
use std::ffi::c_void;
use std::io;
use std::mem::size_of;
use std::os::windows::io::AsRawHandle;
use std::process::{Command, Stdio};
use std::sync::Arc;
use std::time::Duration;
use windows_sys::Win32::Foundation::CloseHandle;
use windows_sys::Win32::System::JobObjects::{
    AssignProcessToJobObject, CreateJobObjectW, JobObjectBasicAccountingInformation,
    JobObjectExtendedLimitInformation, QueryInformationJobObject, SetInformationJobObject,
    TerminateJobObject, JOBOBJECT_BASIC_ACCOUNTING_INFORMATION,
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION, JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
};

/// RAII wrapper for a Windows Job Object handle.
/// Drop calls CloseHandle, which triggers KILL_ON_JOB_CLOSE for any surviving
/// descendants. Using Arc<JobHandle> ensures the handle stays alive as long as
/// any thread holds a KillHandle reference, preventing handle-value recycling.
pub(crate) struct JobHandle(*mut c_void);

// SAFETY: Windows HANDLEs are per-process, not per-thread. Passing the same
// HANDLE across threads within the same process is safe and is the documented
// usage pattern for job objects shared between the main and monitor threads.
unsafe impl Send for JobHandle {}
unsafe impl Sync for JobHandle {}

impl Drop for JobHandle {
    fn drop(&mut self) {
        unsafe { CloseHandle(self.0) };
    }
}

pub(crate) struct WindowsProcess;

impl OsProcess for WindowsProcess {
    fn spawn(
        program: &str,
        args: &[&str],
        options: SpawnOptions<'_>,
    ) -> Result<OsHandle, AerError> {
        // Create the job object first and wrap it immediately so all subsequent
        // error paths clean up via Drop — no manual CloseHandle calls needed.
        let raw_job = unsafe { CreateJobObjectW(std::ptr::null_mut(), std::ptr::null()) };
        if raw_job.is_null() {
            return Err(AerError::SpawnFailed(io::Error::last_os_error()));
        }
        let job = Arc::new(JobHandle(raw_job));

        // Configure kill-on-close: when the last handle to the job closes,
        // every process still in the job is terminated.
        let mut info: JOBOBJECT_EXTENDED_LIMIT_INFORMATION = unsafe { std::mem::zeroed() };
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if unsafe {
            SetInformationJobObject(
                job.0,
                JobObjectExtendedLimitInformation,
                &mut info as *mut _ as *mut _,
                size_of::<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>() as u32,
            )
        } == 0
        {
            return Err(AerError::SpawnFailed(io::Error::last_os_error()));
        }

        let mut command = Command::new(program);
        command
            .args(args)
            .stdin(Stdio::null())
            // Pipes are required even though output is not surfaced to callers.
            // Without draining, a child writing beyond the OS pipe buffer deadlocks
            // wait_with_output(). Never use Stdio::inherit here.
            .stdout(Stdio::piped())
            .stderr(Stdio::piped());

        // env_clear (if requested) must run before applying with_env entries,
        // otherwise it would wipe out the very vars we just set.
        if options.clear_env {
            command.env_clear();
        }
        for (key, value) in options.env {
            command.env(key, value);
        }
        if let Some(cwd) = options.cwd {
            command.current_dir(cwd);
        }

        let mut child = command.spawn().map_err(AerError::SpawnFailed)?;

        // Assign the child to the job. child.as_raw_handle() returns the process
        // HANDLE (*mut c_void), which AssignProcessToJobObject accepts directly.
        if unsafe { AssignProcessToJobObject(job.0, child.as_raw_handle()) } == 0 {
            let err = io::Error::last_os_error();
            // The child is already alive at this point (spawn succeeded) but never
            // made it into the job, so JobHandle's Drop/KILL_ON_JOB_CLOSE won't
            // reach it. The "no orphans" guarantee applies to spawn failures too,
            // not just to teardown after a successful spawn — kill it here before
            // returning the error so we don't leak a live, untracked process.
            let _ = child.kill();
            let _ = child.wait();
            return Err(AerError::SpawnFailed(err));
        }

        let pid = child.id();
        Ok(OsHandle {
            pid,
            child,
            kill: KillHandle { job },
        })
    }

    fn wait(handle: OsHandle, sinks: OutputSinks) -> Result<i32, AerError> {
        let OsHandle {
            mut child, kill, ..
        } = handle;

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

        // Wait for the root process only — NOT for grandchildren to close the pipe.
        let status = child.wait().map_err(AerError::WaitFailed)?;

        // Explicitly terminate the job now, independent of how many Arc<JobHandle>
        // clones are outstanding. task.rs's timeout monitor and CancelHandle both
        // hold their own clone of this KillHandle for longer than this function's
        // scope (the monitor for the full timeout duration; the cancel handle until
        // wait() returns), so this call — not Drop — is what guarantees every
        // surviving grandchild is killed and its inherited pipe handles closed here,
        // unblocking the drain threads below. The spec's guarantee is "nothing
        // survives run() returning", so terminating stragglers at root-exit is the
        // intended semantic regardless of who else is holding a reference.
        // Errors are ignored: the job may already be empty/terminated (e.g. the
        // timeout or cancel path already called TerminateJobObject).
        let _ = unsafe { TerminateJobObject(kill.job.0, 1) };

        // Now drop this thread's reference. Any clone still held by the monitor or
        // cancel handle is harmless: when it later drops, CloseHandle fires on an
        // already-empty, already-terminated job.
        drop(kill);

        // Drain threads unblock once all pipe write-ends are closed.
        if let Some(t) = stdout_drain {
            let _ = t.join();
        }
        if let Some(t) = stderr_drain {
            let _ = t.join();
        }

        Ok(status.code().unwrap_or(-1))
    }

    fn kill_escalating(kill: KillHandle, _grace: Duration) -> Result<(), AerError> {
        // TerminateJobObject kills every process in the job simultaneously.
        // This closes all inherited pipe handles, which unblocks wait_with_output()
        // on the main thread. On Windows there is no graceful kill; _grace is ignored.
        if unsafe { TerminateJobObject(kill.job.0, 1) } == 0 {
            return Err(AerError::KillFailed(io::Error::last_os_error()));
        }
        Ok(())
    }

    fn tree_alive(kill: &KillHandle) -> bool {
        let mut info: JOBOBJECT_BASIC_ACCOUNTING_INFORMATION = unsafe { std::mem::zeroed() };
        let mut returned: u32 = 0;
        let ok = unsafe {
            QueryInformationJobObject(
                kill.job.0,
                JobObjectBasicAccountingInformation,
                &mut info as *mut _ as *mut c_void,
                size_of::<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>() as u32,
                &mut returned,
            )
        };
        if ok == 0 {
            // Query failed: fail toward "alive" so callers still kill rather
            // than risk orphaning a live tree.
            return true;
        }
        info.ActiveProcesses > 0
    }

    fn reap_abandoned(_kill: &KillHandle) {
        // No-op: Windows has no zombie concept. Once TerminateJobObject has
        // killed the tree, dropping the Child's process handle releases
        // everything; liveness probes (GetExitCodeProcess) immediately see the
        // real exit code rather than STILL_ACTIVE.
    }
}
