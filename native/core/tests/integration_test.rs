use aer_core::{
    ffi::{self, AerErrorCode, AerEvent},
    AerError, CancelHandle, Event, ExitReason, Task,
};
use std::ffi::{c_void, CStr, CString};
use std::fs;
use std::thread;
use std::time::{Duration, Instant};

/// Returns a shell invocation that exits with the given code, cross-platform.
fn exit_cmd(code: i32) -> (String, Vec<String>) {
    #[cfg(target_os = "windows")]
    {
        ("cmd".into(), vec!["/c".into(), format!("exit {}", code)])
    }
    #[cfg(not(target_os = "windows"))]
    {
        ("sh".into(), vec!["-c".into(), format!("exit {}", code)])
    }
}

/// Returns a shell invocation that writes `msg` to stderr then exits 0.
fn stderr_cmd(msg: &str) -> (String, Vec<String>) {
    #[cfg(target_os = "windows")]
    {
        (
            "cmd".into(),
            vec!["/c".into(), format!("echo {} 1>&2", msg)],
        )
    }
    #[cfg(not(target_os = "windows"))]
    {
        ("sh".into(), vec!["-c".into(), format!("echo {} >&2", msg)])
    }
}

/// Returns a shell invocation that writes N lines to stdout.
fn noisy_cmd(lines: usize) -> (String, Vec<String>) {
    #[cfg(target_os = "windows")]
    {
        (
            "cmd".into(),
            vec![
                "/c".into(),
                format!("for /L %i in (1,1,{}) do @echo line %i", lines),
            ],
        )
    }
    #[cfg(not(target_os = "windows"))]
    {
        (
            "sh".into(),
            vec![
                "-c".into(),
                format!("seq 1 {} | while read i; do echo \"line $i\"; done", lines),
            ],
        )
    }
}

/// Returns a shell invocation that sleeps for N seconds, cross-platform.
///
/// On Windows, `timeout /t` exits immediately when stdin is not a console
/// (which it isn't when spawned with piped stdio). `ping -n (secs+1)` is the
/// standard workaround: each ping round-trip to 127.0.0.1 takes ~1 second.
fn sleep_cmd(secs: u64) -> (String, Vec<String>) {
    #[cfg(target_os = "windows")]
    {
        (
            "ping".into(),
            vec!["-n".into(), format!("{}", secs + 1), "127.0.0.1".into()],
        )
    }
    #[cfg(not(target_os = "windows"))]
    {
        ("sh".into(), vec!["-c".into(), format!("sleep {}", secs)])
    }
}

/// Returns a shell invocation that prints a line, flushes it, sleeps ~3s, then
/// exits 0. Used to verify that captured chunks are delivered while the
/// process is still alive rather than buffered until exit (#72).
fn live_output_cmd() -> (String, Vec<String>) {
    #[cfg(target_os = "windows")]
    {
        (
            "cmd".into(),
            vec!["/c".into(), "echo hello & ping -n 4 127.0.0.1 >nul".into()],
        )
    }
    #[cfg(not(target_os = "windows"))]
    {
        ("sh".into(), vec!["-c".into(), "echo hello; sleep 3".into()])
    }
}

/// Returns a shell invocation that echoes the value of a single named
/// environment variable, cross-platform. On Windows, cmd literally echoes
/// `%VAR%` back when the variable is unset, which is fine for assertions
/// that only check for the *presence* of an expected value.
fn echo_env_var_cmd(var: &str) -> (String, Vec<String>) {
    #[cfg(target_os = "windows")]
    {
        ("cmd".into(), vec!["/c".into(), format!("echo %{var}%")])
    }
    #[cfg(not(target_os = "windows"))]
    {
        ("sh".into(), vec!["-c".into(), format!("echo ${var}")])
    }
}

/// Returns a shell invocation that echoes two named environment variables on
/// separate lines, cross-platform.
fn echo_two_env_vars_cmd(var1: &str, var2: &str) -> (String, Vec<String>) {
    #[cfg(target_os = "windows")]
    {
        (
            "cmd".into(),
            vec!["/c".into(), format!("echo %{var1}% & echo %{var2}%")],
        )
    }
    #[cfg(not(target_os = "windows"))]
    {
        (
            "sh".into(),
            vec!["-c".into(), format!("echo ${var1} ; echo ${var2}")],
        )
    }
}

/// Returns a shell invocation that prints the process's current working
/// directory, cross-platform.
fn print_cwd_cmd() -> (String, Vec<String>) {
    #[cfg(target_os = "windows")]
    {
        ("cmd".into(), vec!["/c".into(), "cd".into()])
    }
    #[cfg(not(target_os = "windows"))]
    {
        ("sh".into(), vec!["-c".into(), "pwd".into()])
    }
}

/// Returns a shell invocation that echoes one environment variable then
/// prints the current working directory, cross-platform.
fn echo_env_var_and_cwd_cmd(var: &str) -> (String, Vec<String>) {
    #[cfg(target_os = "windows")]
    {
        (
            "cmd".into(),
            vec!["/c".into(), format!("echo %{var}% & cd")],
        )
    }
    #[cfg(not(target_os = "windows"))]
    {
        ("sh".into(), vec!["-c".into(), format!("echo ${var} ; pwd")])
    }
}

/// The shell's absolute path, cross-platform. Used specifically for
/// `with_clear_env` tests: after clearing the child's environment, relying
/// on PATH-based resolution of "cmd"/"sh" would be testing an unrelated
/// (and unspecified) resolution mechanism rather than clear-env behavior
/// itself, so those tests invoke the shell by its well-known absolute path.
#[cfg(target_os = "windows")]
fn shell_absolute_path() -> String {
    std::env::var("COMSPEC").unwrap_or_else(|_| r"C:\Windows\System32\cmd.exe".to_string())
}
#[cfg(not(target_os = "windows"))]
fn shell_absolute_path() -> String {
    "/bin/sh".to_string()
}

fn collect_events(program: &str, args: Vec<String>) -> (Result<(), AerError>, Vec<Event>) {
    let task = Task::new(program, args);
    let mut events = Vec::new();
    let result = task.run(|e| events.push(e));
    (result, events)
}

fn collect_events_with_timeout(
    program: &str,
    args: Vec<String>,
    timeout: Duration,
) -> (Result<(), AerError>, Vec<Event>) {
    let task = Task::new(program, args).with_timeout(timeout);
    let mut events = Vec::new();
    let result = task.run(|e| events.push(e));
    (result, events)
}

// --- Tests ---

#[test]
fn exit_zero_emits_started_then_exited() {
    let (prog, args) = exit_cmd(0);
    let (result, events) = collect_events(&prog, args);

    assert!(result.is_ok());
    assert_eq!(events.len(), 2);
    assert!(matches!(events[0], Event::Started { pid } if pid > 0));
    assert!(matches!(events[1], Event::Exited { code: 0, .. }));
}

#[test]
fn nonzero_exit_code_is_captured() {
    let (prog, args) = exit_cmd(42);
    let (result, events) = collect_events(&prog, args);

    assert!(result.is_ok());
    assert!(matches!(events[1], Event::Exited { code: 42, .. }));
}

#[test]
fn started_always_precedes_exited() {
    let (prog, args) = exit_cmd(0);
    let (_, events) = collect_events(&prog, args);

    let started_pos = events
        .iter()
        .position(|e| matches!(e, Event::Started { .. }));
    let exited_pos = events
        .iter()
        .position(|e| matches!(e, Event::Exited { .. }));

    assert!(started_pos.is_some() && exited_pos.is_some());
    assert!(started_pos.unwrap() < exited_pos.unwrap());
}

#[test]
fn spawn_failure_returns_error_and_emits_no_events() {
    let (result, events) = collect_events("definitely_not_a_real_binary_xyzzy_aer", vec![]);

    assert!(matches!(result, Err(AerError::SpawnFailed(_))));
    assert!(
        events.is_empty(),
        "no events should be emitted when spawn fails"
    );
}

#[test]
fn large_output_does_not_deadlock() {
    // Verifies that wait_with_output() drains the pipe even for large output,
    // preventing the OS pipe-buffer deadlock. 1000 lines well exceeds typical
    // 64 KB pipe buffers when each line is ~10 bytes.
    let (prog, args) = noisy_cmd(1000);
    let (result, events) = collect_events(&prog, args);

    assert!(
        result.is_ok(),
        "large output caused deadlock or error: {:?}",
        result
    );
    assert!(matches!(events[1], Event::Exited { code: 0, .. }));
}

// --- M2: Timeout & Kill Escalation ---

#[test]
fn timeout_kills_slow_process() {
    let (prog, args) = sleep_cmd(60);
    let start = Instant::now();
    let (result, events) = collect_events_with_timeout(&prog, args, Duration::from_secs(1));
    let elapsed = start.elapsed();

    assert!(
        matches!(result, Err(AerError::TimedOut)),
        "expected TimedOut, got {:?}",
        result
    );
    // Regression guard for #76: a SIGTERM-responsive child must not cost the
    // full 5s kill grace on Unix — the grace window is polled, not slept out.
    // (Windows kills immediately, so this bound holds there trivially.)
    assert!(
        elapsed < Duration::from_secs(4),
        "run() took {elapsed:?} for a 1s timeout — the kill grace window is \
         being slept out unconditionally instead of polled"
    );
    assert_eq!(events.len(), 2, "expected Started + Exited even on timeout");
    assert!(matches!(events[0], Event::Started { pid } if pid > 0));
    assert!(matches!(events[1], Event::Exited { code: -1, .. }));
}

#[test]
fn no_timeout_set_runs_normally() {
    // Regression guard: Task::new() without with_timeout behaves identically to M1.
    let (prog, args) = exit_cmd(0);
    let (result, events) = collect_events(&prog, args);

    assert!(result.is_ok());
    assert_eq!(events.len(), 2);
    assert!(matches!(events[1], Event::Exited { code: 0, .. }));
}

#[test]
fn timeout_does_not_fire_for_fast_process() {
    // Process exits before the timeout — run() should return Ok, not TimedOut.
    let (prog, args) = exit_cmd(0);
    let (result, events) = collect_events_with_timeout(&prog, args, Duration::from_secs(30));

    assert!(
        result.is_ok(),
        "expected Ok for fast process with long timeout, got {:?}",
        result
    );
    assert!(matches!(events[1], Event::Exited { code: 0, .. }));
}

/// Regression coverage for #79: no existing test exercises a child that
/// survives SIGTERM, so `kill_escalating`'s SIGKILL escalation (the second
/// half of the grace-window logic in os/unix.rs) was never actually proven
/// to fire. `trap "" TERM` makes the shell ignore SIGTERM outright; only the
/// SIGKILL that follows the grace window can end it.
///
/// Unix-only: on Windows, `kill_escalating`'s `_grace` parameter is ignored
/// and the process is terminated immediately (see os/windows.rs) — there is
/// no graceful/SIGTERM phase to escalate past, so there is nothing this test
/// could exercise there.
///
/// Wall-clock cost: KILL_GRACE (task.rs) is 5s and `kill_escalating` always
/// sleeps out the full grace window after sending SIGTERM before sending
/// SIGKILL, regardless of whether the target already died. With a 1s timeout
/// this test costs ~6s wall time — acceptable per #79's review, and it
/// overlaps with other tests under cargo test's default parallelism.
#[cfg(not(target_os = "windows"))]
#[test]
fn sigterm_trapping_process_is_killed_by_sigkill_escalation() {
    let task = Task::new(
        "sh",
        vec!["-c".to_string(), "trap '' TERM; sleep 30".to_string()],
    )
    .with_timeout(Duration::from_secs(1));
    let mut events = Vec::new();
    let start = Instant::now();
    let result = task.run(|e| events.push(e));
    let elapsed = start.elapsed();

    assert!(
        matches!(result, Err(AerError::TimedOut)),
        "expected TimedOut, got {:?}",
        result
    );
    assert!(
        elapsed < Duration::from_secs(10),
        "run() took {elapsed:?} — SIGKILL escalation may not have fired against \
         the SIGTERM-trapping child"
    );
    assert_eq!(events.len(), 2, "expected Started + Exited even on timeout");
    assert!(
        matches!(
            events[1],
            Event::Exited {
                code: -1,
                reason: ExitReason::TimedOut
            }
        ),
        "expected TimedOut Exited, got {:?}",
        events[1]
    );
}

// --- M3: Process Tree Cleanup ---

/// Returns a command that spawns a long-lived grandchild, writes its PID to
/// `pid_file`, then exits immediately. Used to verify process tree cleanup.
fn orphan_cmd(pid_file: &str) -> (String, Vec<String>) {
    #[cfg(target_os = "windows")]
    {
        // Start-Process spawns ping as a child of PowerShell. PowerShell writes
        // ping's PID to the file, then exits. With M3, the job object kills ping
        // when PowerShell exits and the job handle is closed.
        (
            "powershell".into(),
            vec![
                "-NoProfile".into(),
                "-Command".into(),
                format!(
                    "$p = Start-Process -PassThru -NoNewWindow -FilePath ping \
                     -ArgumentList @('-n','9999','127.0.0.1'); \
                     $p.Id | Out-File -FilePath '{}' -Encoding ascii",
                    pid_file
                ),
            ],
        )
    }
    #[cfg(not(target_os = "windows"))]
    {
        // Background sleep inherits the session but not the process group after
        // setsid. $! captures its PID for verification.
        (
            "sh".into(),
            vec!["-c".into(), format!("sleep 9999 & echo $! > {pid_file}")],
        )
    }
}

/// Returns true if the process with the given PID is still running.
#[cfg(not(target_os = "windows"))]
fn process_is_alive(pid: u32) -> bool {
    unsafe { libc::kill(pid as i32, 0) == 0 }
}

/// Returns true if the process with the given PID is still running.
#[cfg(target_os = "windows")]
fn process_is_alive(pid: u32) -> bool {
    use windows_sys::Win32::Foundation::{CloseHandle, STILL_ACTIVE};
    use windows_sys::Win32::System::Threading::{
        GetExitCodeProcess, OpenProcess, PROCESS_QUERY_LIMITED_INFORMATION,
    };
    let handle = unsafe { OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, pid) };
    if handle.is_null() {
        return false;
    }
    let mut code: u32 = 0;
    let ok = unsafe { GetExitCodeProcess(handle, &mut code) };
    unsafe { CloseHandle(handle) };
    ok != 0 && code == STILL_ACTIVE as u32
}

#[test]
fn process_tree_is_cleaned_up() {
    let pid_file = std::env::temp_dir().join("aer_test_grandchild_pid.txt");
    let pid_file_str = pid_file.to_str().unwrap();

    let (prog, args) = orphan_cmd(pid_file_str);
    let task = Task::new(prog, args);
    let start = std::time::Instant::now();
    let result = task.run(|_| {});
    let elapsed = start.elapsed();
    assert!(result.is_ok(), "task failed unexpectedly: {:?}", result);
    assert!(
        elapsed < Duration::from_secs(10),
        "run() took {elapsed:?} — process tree cleanup is deadlocked (grandchild holds the pipe)"
    );

    // Brief pause: job object cleanup (Windows) or pipe-close propagation (Unix)
    // is synchronous within run(), but give the OS a moment to complete.
    thread::sleep(Duration::from_millis(200));

    let raw = fs::read_to_string(&pid_file)
        .expect("grandchild PID file not written — orphan_cmd may have failed");
    let grandchild_pid: u32 = raw
        .trim()
        .parse()
        .expect("grandchild PID file contains non-numeric data");

    assert!(
        !process_is_alive(grandchild_pid),
        "grandchild PID {grandchild_pid} is still alive — process tree cleanup failed"
    );

    let _ = fs::remove_file(&pid_file);
}

/// Regression test for #71: a generous timeout must not defeat process-tree
/// cleanup at root-exit. task.rs's timeout monitor thread holds its own clone of
/// the `KillHandle` (`kill_for_monitor`) for the full timeout duration, so if
/// `wait()` relied on Arc refcounting to force-close the job/process-group, the
/// drain threads would block until the deadline and this would be misreported as
/// `TimedOut` instead of `NaturalExit`.
#[test]
fn timeout_with_process_tree_reports_natural_exit() {
    let pid_file = std::env::temp_dir().join("aer_test_grandchild_pid_timeout.txt");
    let pid_file_str = pid_file.to_str().unwrap();

    let (prog, args) = orphan_cmd(pid_file_str);
    let task = Task::new(prog, args).with_timeout(Duration::from_secs(30));
    let mut events = Vec::new();
    let start = std::time::Instant::now();
    let result = task.run(|e| events.push(e));
    let elapsed = start.elapsed();

    assert!(result.is_ok(), "expected Ok, got {:?}", result);
    assert!(
        elapsed < Duration::from_secs(10),
        "run() took {elapsed:?} — the timeout monitor's KillHandle clone is blocking \
         cleanup until the 30s deadline instead of NaturalExit firing promptly"
    );
    assert_eq!(events.len(), 2);
    assert!(
        matches!(
            events[1],
            Event::Exited {
                code: 0,
                reason: ExitReason::NaturalExit
            }
        ),
        "expected Exited {{ code: 0, reason: NaturalExit }}, got {:?}",
        events[1]
    );

    let _ = fs::remove_file(&pid_file);
}

/// Regression test for #71: a `CancelHandle` that is never cancelled must not
/// defeat process-tree cleanup at root-exit. `run_with_cancel()` stores its own
/// clone of the `KillHandle` in the `CancelHandle` and only clears it AFTER
/// `wait()` returns (task.rs `run_impl`) — a circular wait if `wait()` relied on
/// Arc refcounting to force-close the job/process-group, which would hang
/// `run()` forever.
#[test]
fn cancel_handle_with_process_tree_returns_promptly() {
    let pid_file = std::env::temp_dir().join("aer_test_grandchild_pid_cancel.txt");
    let pid_file_str = pid_file.to_str().unwrap();

    let (prog, args) = orphan_cmd(pid_file_str);
    let task = Task::new(prog, args);
    let cancel = CancelHandle::new();
    let mut events = Vec::new();
    let start = std::time::Instant::now();
    let result = task.run_with_cancel(|e| events.push(e), &cancel);
    let elapsed = start.elapsed();

    assert!(result.is_ok(), "expected Ok, got {:?}", result);
    assert!(
        elapsed < Duration::from_secs(10),
        "run_with_cancel() took {elapsed:?} — the CancelHandle's KillHandle clone is \
         blocking cleanup (circular wait)"
    );
    assert_eq!(events.len(), 2);
    assert!(
        matches!(
            events[1],
            Event::Exited {
                code: 0,
                reason: ExitReason::NaturalExit
            }
        ),
        "expected Exited {{ code: 0, reason: NaturalExit }}, got {:?}",
        events[1]
    );

    let _ = fs::remove_file(&pid_file);
}

// --- M4: FFI Boundary ---

// Closures cannot be extern "C". State is passed through user_data instead.
unsafe extern "C" fn collect_ffi_events(event: *const AerEvent, user_data: *mut c_void) {
    let events = &mut *(user_data as *mut Vec<AerEvent>);
    events.push(*event);
}

/// Build CStrings from (program, args) and call aer_task_new.
/// Returns the task pointer; the CStrings must remain alive for the duration of the call.
fn ffi_new_task(program: &str, args: &[String]) -> *mut ffi::AerTask {
    let c_program = CString::new(program).unwrap();
    let c_args: Vec<CString> = args
        .iter()
        .map(|s| CString::new(s.as_str()).unwrap())
        .collect();
    let arg_ptrs: Vec<*const i8> = c_args.iter().map(|s| s.as_ptr()).collect();
    unsafe {
        ffi::aer_task_new(
            c_program.as_ptr(),
            if arg_ptrs.is_empty() {
                std::ptr::null()
            } else {
                arg_ptrs.as_ptr()
            },
            arg_ptrs.len(),
        )
    }
}

#[test]
fn ffi_null_program_returns_null() {
    let task = unsafe { ffi::aer_task_new(std::ptr::null(), std::ptr::null(), 0) };
    assert!(task.is_null(), "expected NULL for null program");
}

#[test]
fn ffi_basic_run_emits_events() {
    let (prog, args) = exit_cmd(0);
    let task = ffi_new_task(&prog, &args);
    assert!(!task.is_null());

    let mut events: Vec<AerEvent> = Vec::new();
    let code = unsafe {
        ffi::aer_task_run(
            task,
            Some(collect_ffi_events),
            &mut events as *mut _ as *mut c_void,
        )
    };
    unsafe { ffi::aer_task_free(task) };

    assert_eq!(code, AerErrorCode::Ok);
    assert_eq!(events.len(), 2);
    assert_eq!(events[0].kind, 0, "first event should be Started");
    assert!(events[0].pid > 0, "started pid should be > 0");
    assert_eq!(events[1].kind, 1, "second event should be Exited");
    assert_eq!(events[1].code, 0);
}

#[test]
fn ffi_null_callback_is_valid() {
    let (prog, args) = exit_cmd(0);
    let task = ffi_new_task(&prog, &args);
    assert!(!task.is_null());

    let code = unsafe { ffi::aer_task_run(task, None, std::ptr::null_mut()) };
    unsafe { ffi::aer_task_free(task) };

    assert_eq!(code, AerErrorCode::Ok);
}

#[test]
fn ffi_double_run_returns_already_run() {
    let (prog, args) = exit_cmd(0);
    let task = ffi_new_task(&prog, &args);
    assert!(!task.is_null());

    let first = unsafe { ffi::aer_task_run(task, None, std::ptr::null_mut()) };
    assert_eq!(first, AerErrorCode::Ok);

    let second = unsafe { ffi::aer_task_run(task, None, std::ptr::null_mut()) };
    assert_eq!(second, AerErrorCode::AlreadyRun);

    unsafe { ffi::aer_task_free(task) };
}

#[test]
fn ffi_null_task_run_returns_null_pointer() {
    let code = unsafe { ffi::aer_task_run(std::ptr::null_mut(), None, std::ptr::null_mut()) };
    assert_eq!(code, AerErrorCode::NullPointer);
}

#[test]
fn ffi_timeout_fires() {
    let (prog, args) = sleep_cmd(60);
    let task = ffi_new_task(&prog, &args);
    assert!(!task.is_null());

    let to_code = unsafe { ffi::aer_task_with_timeout(task, 1000) };
    assert_eq!(to_code, AerErrorCode::Ok);

    let mut events: Vec<AerEvent> = Vec::new();
    let run_code = unsafe {
        ffi::aer_task_run(
            task,
            Some(collect_ffi_events),
            &mut events as *mut _ as *mut c_void,
        )
    };
    unsafe { ffi::aer_task_free(task) };

    assert_eq!(run_code, AerErrorCode::TimedOut);
    assert_eq!(events.len(), 2, "expect Started + Exited even on timeout");
    assert_eq!(events[1].code, -1, "timed-out exit code must be -1");
}

#[test]
fn ffi_spawn_failure_sets_error_message() {
    let task = ffi_new_task("definitely_not_a_real_binary_xyzzy_aer", &[]);
    assert!(!task.is_null());

    let code = unsafe { ffi::aer_task_run(task, None, std::ptr::null_mut()) };
    assert_eq!(code, AerErrorCode::SpawnFailed);

    let msg_ptr = ffi::aer_last_error_message();
    assert!(
        !msg_ptr.is_null(),
        "expected error message for spawn failure"
    );
    let msg = unsafe { CStr::from_ptr(msg_ptr) }.to_str().unwrap();
    assert!(!msg.is_empty(), "error message should not be empty");

    unsafe { ffi::aer_task_free(task) };
}

#[test]
fn ffi_free_null_is_noop() {
    unsafe { ffi::aer_task_free(std::ptr::null_mut()) };
}

#[test]
fn ffi_last_error_message_is_null_after_success() {
    let (prog, args) = exit_cmd(0);
    let task = ffi_new_task(&prog, &args);
    assert!(!task.is_null());

    let code = unsafe { ffi::aer_task_run(task, None, std::ptr::null_mut()) };
    assert_eq!(code, AerErrorCode::Ok);

    let msg_ptr = ffi::aer_last_error_message();
    assert!(
        msg_ptr.is_null(),
        "error message should be cleared on success"
    );

    unsafe { ffi::aer_task_free(task) };
}

// --- M4b: Observation Tier (Rust API) ---

#[test]
fn capture_output_collects_stdout_chunks() {
    let (prog, args) = noisy_cmd(50);
    let task = Task::new(prog, args).with_capture_output(true);
    let mut events = Vec::new();
    let result = task.run(|e| events.push(e));

    assert!(result.is_ok());
    assert!(matches!(events.first(), Some(Event::Started { .. })));
    assert!(matches!(events.last(), Some(Event::Exited { code: 0, .. })));

    let stdout_chunks: Vec<&Event> = events
        .iter()
        .filter(|e| matches!(e, Event::StdoutChunk { .. }))
        .collect();
    assert!(
        !stdout_chunks.is_empty(),
        "expected at least one StdoutChunk"
    );

    let all_bytes: Vec<u8> = stdout_chunks
        .iter()
        .flat_map(|e| {
            if let Event::StdoutChunk { bytes, .. } = e {
                bytes.clone()
            } else {
                vec![]
            }
        })
        .collect();
    let text = String::from_utf8_lossy(&all_bytes);
    assert!(text.contains('1'), "expected output to contain '1'");
    assert!(text.contains("50"), "expected output to contain '50'");

    // seq monotonically increasing within stdout stream
    let mut prev_seq: Option<u64> = None;
    for e in &stdout_chunks {
        if let Event::StdoutChunk { seq, .. } = e {
            if let Some(prev) = prev_seq {
                assert!(
                    *seq > prev,
                    "seq must be strictly increasing: got {} after {}",
                    seq,
                    prev
                );
            }
            prev_seq = Some(*seq);
        }
    }
}

#[test]
fn capture_output_off_emits_no_chunks() {
    let (prog, args) = noisy_cmd(100);
    let task = Task::new(prog, args); // no with_capture_output
    let mut events = Vec::new();
    let result = task.run(|e| events.push(e));

    assert!(result.is_ok());
    let has_chunks = events
        .iter()
        .any(|e| matches!(e, Event::StdoutChunk { .. } | Event::StderrChunk { .. }));
    assert!(
        !has_chunks,
        "chunks must not be emitted without capture enabled"
    );
    assert_eq!(events.len(), 2, "only Started and Exited without capture");
}

#[test]
fn capture_output_chunks_between_started_and_exited() {
    let (prog, args) = noisy_cmd(10);
    let task = Task::new(prog, args).with_capture_output(true);
    let mut events = Vec::new();
    let result = task.run(|e| events.push(e));

    assert!(result.is_ok());
    let started_pos = events
        .iter()
        .position(|e| matches!(e, Event::Started { .. }))
        .unwrap();
    let exited_pos = events
        .iter()
        .position(|e| matches!(e, Event::Exited { .. }))
        .unwrap();

    for (i, e) in events.iter().enumerate() {
        if matches!(e, Event::StdoutChunk { .. } | Event::StderrChunk { .. }) {
            assert!(
                i > started_pos,
                "chunk at index {} must follow Started at {}",
                i,
                started_pos
            );
            assert!(
                i < exited_pos,
                "chunk at index {} must precede Exited at {}",
                i,
                exited_pos
            );
        }
    }
}

#[test]
fn capture_output_collects_stderr_chunks() {
    let (prog, args) = stderr_cmd("hello_stderr");
    let task = Task::new(prog, args).with_capture_output(true);
    let mut events = Vec::new();
    let result = task.run(|e| events.push(e));

    assert!(result.is_ok());

    let stderr_chunks: Vec<&Event> = events
        .iter()
        .filter(|e| matches!(e, Event::StderrChunk { .. }))
        .collect();
    assert!(
        !stderr_chunks.is_empty(),
        "expected at least one StderrChunk"
    );

    let all_bytes: Vec<u8> = stderr_chunks
        .iter()
        .flat_map(|e| {
            if let Event::StderrChunk { bytes, .. } = e {
                bytes.clone()
            } else {
                vec![]
            }
        })
        .collect();
    let text = String::from_utf8_lossy(&all_bytes);
    assert!(
        text.contains("hello_stderr"),
        "expected 'hello_stderr' in stderr chunks, got: {:?}",
        text
    );
}

/// Regression test for #72: captured chunks must be delivered to the event
/// callback while the process is still alive, not buffered until it exits.
/// Before the fix, `run_impl` drained the chunk channels only after
/// `PlatformProcess::wait()` returned, so a slow process produced silence
/// followed by a burst of chunks right before `Exited`.
#[test]
fn capture_delivers_chunks_while_process_is_alive() {
    let (prog, args) = live_output_cmd();
    let task = Task::new(prog, args).with_capture_output(true);

    let start = Instant::now();
    let mut timestamps: Vec<(Instant, Event)> = Vec::new();
    let result = task.run(|e| timestamps.push((Instant::now(), e)));

    assert!(result.is_ok(), "expected Ok, got {:?}", result);

    let exited_at = timestamps
        .iter()
        .find(|(_, e)| matches!(e, Event::Exited { .. }))
        .map(|(t, _)| *t)
        .expect("expected an Exited event");

    let stdout_chunk_times: Vec<Instant> = timestamps
        .iter()
        .filter(|(_, e)| matches!(e, Event::StdoutChunk { .. }))
        .map(|(t, _)| *t)
        .collect();
    assert!(
        !stdout_chunk_times.is_empty(),
        "expected at least one StdoutChunk"
    );

    // Sanity: the run must actually have taken ~3s (the sleep), otherwise a
    // large first-chunk-to-Exited gap wouldn't prove anything about liveness.
    let total = exited_at.duration_since(start);
    assert!(
        total >= Duration::from_millis(2500),
        "process exited too quickly ({total:?}) for this test to be meaningful"
    );

    // The first stdout chunk must have arrived well before Exited — proving
    // it was delivered while the process was still sleeping, not flushed in
    // a burst after wait() returned.
    let first_chunk_at = stdout_chunk_times[0];
    let gap = exited_at.duration_since(first_chunk_at);
    assert!(
        gap >= Duration::from_millis(1500),
        "first stdout chunk arrived only {gap:?} before Exited — chunks are not \
         being delivered live"
    );

    // All chunks (either stream) must precede Exited.
    for (t, e) in &timestamps {
        if matches!(e, Event::StdoutChunk { .. } | Event::StderrChunk { .. }) {
            assert!(*t <= exited_at, "chunk timestamped after Exited: {:?}", e);
        }
    }
}

/// Regression coverage for #79: the capture path (`with_capture_output`) had
/// ~50 lines of test coverage against ~1000 for the discard path, and no test
/// combined capture with timeout, cancel, or a process tree. This proves
/// chunks emitted by a process are delivered live before the timeout kill's
/// `Exited { reason: TimedOut }` — the capture-path recv loop in
/// `run_impl` must drain the channel before observing the kill's effect, not
/// discard already-buffered chunks.
#[test]
fn capture_output_with_timeout_delivers_chunks_then_timed_out_exit() {
    let (prog, args) = live_output_cmd(); // prints, then sleeps ~3s
    let task = Task::new(prog, args)
        .with_capture_output(true)
        .with_timeout(Duration::from_millis(500));
    let mut events = Vec::new();
    let result = task.run(|e| events.push(e));

    assert!(
        matches!(result, Err(AerError::TimedOut)),
        "expected TimedOut, got {:?}",
        result
    );

    let exited_pos = events
        .iter()
        .position(|e| matches!(e, Event::Exited { .. }))
        .expect("expected an Exited event");
    assert!(
        matches!(
            events[exited_pos],
            Event::Exited {
                code: -1,
                reason: ExitReason::TimedOut
            }
        ),
        "expected TimedOut Exited, got {:?}",
        events[exited_pos]
    );

    let stdout_positions: Vec<usize> = events
        .iter()
        .enumerate()
        .filter(|(_, e)| matches!(e, Event::StdoutChunk { .. }))
        .map(|(i, _)| i)
        .collect();
    assert!(
        !stdout_positions.is_empty(),
        "expected at least one StdoutChunk before the timeout fired"
    );
    for i in stdout_positions {
        assert!(
            i < exited_pos,
            "chunk at index {i} must precede Exited at {exited_pos}"
        );
    }
}

/// Capture-path coverage for #79 (see doc comment on the timeout variant
/// above for context): proves chunks are delivered before the
/// `Exited { reason: CancelRequested }` event when a cancel fired from
/// another thread interrupts a still-emitting process.
#[test]
fn capture_output_with_cancel_delivers_chunks_then_cancel_requested_exit() {
    let (prog, args) = live_output_cmd(); // prints, then sleeps ~3s
    let task = Task::new(prog, args).with_capture_output(true);
    let cancel = CancelHandle::new();
    let cancel_clone = cancel.clone();

    let cancel_thread = thread::spawn(move || {
        thread::sleep(Duration::from_millis(300));
        cancel_clone.cancel();
    });

    let mut events = Vec::new();
    let result = task.run_with_cancel(|e| events.push(e), &cancel);
    cancel_thread.join().unwrap();

    assert!(
        matches!(result, Err(AerError::Cancelled)),
        "expected Cancelled, got {:?}",
        result
    );

    let exited_pos = events
        .iter()
        .position(|e| matches!(e, Event::Exited { .. }))
        .expect("expected an Exited event");
    assert!(
        matches!(
            events[exited_pos],
            Event::Exited {
                code: -1,
                reason: ExitReason::CancelRequested
            }
        ),
        "expected CancelRequested Exited, got {:?}",
        events[exited_pos]
    );

    let stdout_positions: Vec<usize> = events
        .iter()
        .enumerate()
        .filter(|(_, e)| matches!(e, Event::StdoutChunk { .. }))
        .map(|(i, _)| i)
        .collect();
    assert!(
        !stdout_positions.is_empty(),
        "expected at least one StdoutChunk before cancel fired"
    );
    for i in stdout_positions {
        assert!(
            i < exited_pos,
            "chunk at index {i} must precede Exited at {exited_pos}"
        );
    }
}

/// Capture-path coverage for #79: `process_tree_is_cleaned_up` and
/// `timeout_with_process_tree_reports_natural_exit` already prove the
/// *discard* path survives a grandchild that inherits stdout/stderr pipes
/// (root exit triggers a group-wide kill that unblocks the drain threads).
/// The capture live-delivery path (`run_impl`'s capture branch) has its own
/// recv loop that only terminates once both drain threads' `chunk_tx` clones
/// are dropped — nothing proved that path survives the same grandchild
/// scenario rather than hanging on a pipe the grandchild still holds open.
#[test]
fn capture_output_with_process_tree_returns_promptly_with_natural_exit() {
    let pid_file = std::env::temp_dir().join("aer_test_grandchild_pid_capture.txt");
    let pid_file_str = pid_file.to_str().unwrap();

    let (prog, args) = orphan_cmd(pid_file_str);
    let task = Task::new(prog, args).with_capture_output(true);
    let mut events = Vec::new();
    let start = Instant::now();
    let result = task.run(|e| events.push(e));
    let elapsed = start.elapsed();

    assert!(result.is_ok(), "expected Ok, got {:?}", result);
    assert!(
        elapsed < Duration::from_secs(10),
        "run() took {elapsed:?} — the capture path's recv loop may be \
         deadlocked on the grandchild holding stdout/stderr open"
    );
    assert!(
        matches!(
            events.last(),
            Some(Event::Exited {
                code: 0,
                reason: ExitReason::NaturalExit
            })
        ),
        "expected NaturalExit with code 0, got {:?}",
        events.last()
    );

    let exited_pos = events
        .iter()
        .position(|e| matches!(e, Event::Exited { .. }))
        .expect("expected an Exited event");
    for (i, e) in events.iter().enumerate() {
        if matches!(e, Event::StdoutChunk { .. } | Event::StderrChunk { .. }) {
            assert!(
                i < exited_pos,
                "chunk at index {i} must precede Exited at {exited_pos}"
            );
        }
    }

    let _ = fs::remove_file(&pid_file);
}

// --- M4b: Observation Tier (FFI) ---

/// Accumulates captured output bytes during the FFI callback (pointer is only
/// valid for the duration of the callback, so bytes must be copied immediately).
struct FfiCaptureData {
    started_pid: u32,
    exited_code: i32,
    stdout_chunks: Vec<Vec<u8>>,
    stderr_chunks: Vec<Vec<u8>>,
}

unsafe extern "C" fn collect_ffi_capture(event: *const AerEvent, user_data: *mut c_void) {
    let data = &mut *(user_data as *mut FfiCaptureData);
    let e = &*event;
    match e.kind {
        0 => data.started_pid = e.pid,
        1 => data.exited_code = e.code,
        // Copy bytes immediately — `data` pointer is only valid during the callback
        2 => data
            .stdout_chunks
            .push(std::slice::from_raw_parts(e.data, e.data_len).to_vec()),
        3 => data
            .stderr_chunks
            .push(std::slice::from_raw_parts(e.data, e.data_len).to_vec()),
        _ => {}
    }
}

#[test]
fn ffi_capture_output_collects_stdout() {
    let (prog, args) = noisy_cmd(50);
    let task = ffi_new_task(&prog, &args);
    assert!(!task.is_null());

    let cap_code = unsafe { ffi::aer_task_with_capture_output(task, true) };
    assert_eq!(cap_code, AerErrorCode::Ok);

    let mut data = FfiCaptureData {
        started_pid: 0,
        exited_code: -999,
        stdout_chunks: Vec::new(),
        stderr_chunks: Vec::new(),
    };
    let run_code = unsafe {
        ffi::aer_task_run(
            task,
            Some(collect_ffi_capture),
            &mut data as *mut _ as *mut c_void,
        )
    };
    unsafe { ffi::aer_task_free(task) };

    assert_eq!(run_code, AerErrorCode::Ok);
    assert!(data.started_pid > 0, "started_pid should be > 0");
    assert_eq!(data.exited_code, 0);
    assert!(
        !data.stdout_chunks.is_empty(),
        "expected stdout chunks via FFI"
    );

    let all: Vec<u8> = data.stdout_chunks.into_iter().flatten().collect();
    let text = String::from_utf8_lossy(&all);
    assert!(text.contains('1'), "expected output content in chunks");
}

#[test]
fn ffi_capture_output_off_emits_no_chunks() {
    let (prog, args) = noisy_cmd(50);
    let task = ffi_new_task(&prog, &args);
    assert!(!task.is_null());
    // No aer_task_with_capture_output — capture is off by default

    let mut events: Vec<AerEvent> = Vec::new();
    let run_code = unsafe {
        ffi::aer_task_run(
            task,
            Some(collect_ffi_events),
            &mut events as *mut _ as *mut c_void,
        )
    };
    unsafe { ffi::aer_task_free(task) };

    assert_eq!(run_code, AerErrorCode::Ok);
    let has_chunks = events.iter().any(|e| e.kind == 2 || e.kind == 3);
    assert!(
        !has_chunks,
        "no chunks should be emitted without capture enabled"
    );
    assert_eq!(events.len(), 2, "only Started and Exited without capture");
}

// --- M4c: Cancellation and ExitReason (Rust API) ---

#[test]
fn exit_reason_natural_on_clean_exit() {
    let (prog, args) = exit_cmd(0);
    let task = Task::new(prog, args);
    let mut events = Vec::new();
    let result = task.run(|e| events.push(e));

    assert!(result.is_ok());
    assert!(
        matches!(
            events.last(),
            Some(Event::Exited {
                reason: ExitReason::NaturalExit,
                ..
            })
        ),
        "expected NaturalExit, got {:?}",
        events.last()
    );
}

#[test]
fn exit_reason_timed_out_on_timeout() {
    let (prog, args) = sleep_cmd(60);
    let task = Task::new(prog, args).with_timeout(Duration::from_secs(1));
    let mut events = Vec::new();
    let result = task.run(|e| events.push(e));

    assert!(matches!(result, Err(AerError::TimedOut)));
    assert!(
        matches!(
            events.last(),
            Some(Event::Exited {
                reason: ExitReason::TimedOut,
                ..
            })
        ),
        "expected TimedOut reason, got {:?}",
        events.last()
    );
}

#[test]
fn cancel_kills_running_process() {
    let (prog, args) = sleep_cmd(60);
    let task = Task::new(prog, args);
    let cancel = CancelHandle::new();
    let cancel_clone = cancel.clone();

    let cancel_thread = thread::spawn(move || {
        thread::sleep(Duration::from_millis(300));
        cancel_clone.cancel();
    });

    let mut events = Vec::new();
    let result = task.run_with_cancel(|e| events.push(e), &cancel);
    cancel_thread.join().unwrap();

    assert!(
        matches!(result, Err(AerError::Cancelled)),
        "expected Cancelled, got {:?}",
        result
    );
    assert_eq!(events.len(), 2, "expected Started + Exited");
    assert!(matches!(events[0], Event::Started { .. }));
    assert!(
        matches!(
            events[1],
            Event::Exited {
                code: -1,
                reason: ExitReason::CancelRequested
            }
        ),
        "expected CancelRequested Exited, got {:?}",
        events[1]
    );
}

#[test]
fn cancel_after_exit_is_noop() {
    // Regression test for #73: cancel() called after the process has already
    // exited must not affect the exit reason or discard the real exit code —
    // the process should be reported as NaturalExit with code 0, and calling
    // cancel() after run_with_cancel() has already returned must change nothing.
    let (prog, args) = exit_cmd(0);
    let task = Task::new(prog, args);
    let cancel = CancelHandle::new();

    let mut events = Vec::new();
    let result = task.run_with_cancel(|e| events.push(e), &cancel);

    assert!(result.is_ok(), "expected Ok, got {:?}", result);
    assert!(
        matches!(
            events.last(),
            Some(Event::Exited {
                reason: ExitReason::NaturalExit,
                code: 0,
            })
        ),
        "expected NaturalExit with code 0, got {:?}",
        events.last()
    );

    // Process already exited (run_with_cancel returned); cancel now is a no-op
    // and must not retroactively change the reported outcome.
    cancel.cancel();
    assert!(
        matches!(
            events.last(),
            Some(Event::Exited {
                reason: ExitReason::NaturalExit,
                code: 0,
            })
        ),
        "cancel after exit must not change reason, got {:?}",
        events.last()
    );
}

#[test]
fn cancel_is_idempotent() {
    let (prog, args) = sleep_cmd(60);
    let task = Task::new(prog, args);
    let cancel = CancelHandle::new();
    let cancel_clone = cancel.clone();

    let cancel_thread = thread::spawn(move || {
        thread::sleep(Duration::from_millis(200));
        cancel_clone.cancel();
        cancel_clone.cancel(); // second call must not panic or error
        cancel_clone.cancel();
    });

    let result = task.run_with_cancel(|_| {}, &cancel);
    cancel_thread.join().unwrap();

    assert!(matches!(result, Err(AerError::Cancelled)));
}

/// Regression coverage for #79: only cancel-during-run
/// (`cancel_kills_running_process`) and cancel-after-exit
/// (`cancel_after_exit_is_noop`) were covered before this — nothing exercised
/// `cancel()` called before `run_with_cancel` is ever invoked. Per
/// `run_impl`'s cancel-wiring block in task.rs, a pre-spawn cancel leaves
/// `cancelled` true and `kill` unset; once the process actually spawns,
/// `run_impl` must notice the already-set flag and kill immediately instead
/// of waiting for a subsequent `cancel()` call that will never come.
#[test]
fn cancel_before_spawn_reports_cancel_requested() {
    let (prog, args) = sleep_cmd(30);
    let task = Task::new(prog, args);
    let cancel = CancelHandle::new();
    cancel.cancel(); // cancelled BEFORE run_with_cancel is ever called

    let mut events = Vec::new();
    let start = Instant::now();
    let result = task.run_with_cancel(|e| events.push(e), &cancel);
    let elapsed = start.elapsed();

    assert!(
        matches!(result, Err(AerError::Cancelled)),
        "expected Cancelled, got {:?}",
        result
    );
    assert!(
        elapsed < Duration::from_secs(15),
        "run_with_cancel() took {elapsed:?} — a pre-spawn cancel should kill \
         the process promptly rather than waiting out the sleep 30"
    );
    assert_eq!(events.len(), 2, "expected Started + Exited");
    assert!(matches!(events[0], Event::Started { .. }));
    assert!(
        matches!(
            events[1],
            Event::Exited {
                code: -1,
                reason: ExitReason::CancelRequested
            }
        ),
        "expected CancelRequested Exited, got {:?}",
        events[1]
    );
}

// --- M4c: Cancellation and ExitReason (FFI) ---

#[test]
fn ffi_exit_reason_natural_on_clean_exit() {
    let (prog, args) = exit_cmd(0);
    let task = ffi_new_task(&prog, &args);
    assert!(!task.is_null());

    let mut events: Vec<AerEvent> = Vec::new();
    let code = unsafe {
        ffi::aer_task_run(
            task,
            Some(collect_ffi_events),
            &mut events as *mut _ as *mut c_void,
        )
    };
    unsafe { ffi::aer_task_free(task) };

    assert_eq!(code, AerErrorCode::Ok);
    let exited = events.iter().find(|e| e.kind == 1).unwrap();
    assert_eq!(exited.reason, 0, "expected AER_EXIT_NATURAL (0)");
}

#[test]
fn ffi_exit_reason_timed_out() {
    let (prog, args) = sleep_cmd(60);
    let task = ffi_new_task(&prog, &args);
    assert!(!task.is_null());

    let to_code = unsafe { ffi::aer_task_with_timeout(task, 1000) };
    assert_eq!(to_code, AerErrorCode::Ok);

    let mut events: Vec<AerEvent> = Vec::new();
    let run_code = unsafe {
        ffi::aer_task_run(
            task,
            Some(collect_ffi_events),
            &mut events as *mut _ as *mut c_void,
        )
    };
    unsafe { ffi::aer_task_free(task) };

    assert_eq!(run_code, AerErrorCode::TimedOut);
    let exited = events.iter().find(|e| e.kind == 1).unwrap();
    assert_eq!(exited.reason, 1, "expected AER_EXIT_TIMED_OUT (1)");
    assert_eq!(exited.code, -1);
}

#[test]
fn ffi_cancel_kills_process() {
    let (prog, args) = sleep_cmd(60);
    let task = ffi_new_task(&prog, &args);
    assert!(!task.is_null());

    let cancel = unsafe { ffi::aer_task_make_cancel_handle(task) };
    assert!(!cancel.is_null());

    // Cancel from a separate thread after a brief delay
    let cancel_copy = cancel as usize; // raw pointer as usize for Send
    let cancel_thread = thread::spawn(move || {
        thread::sleep(Duration::from_millis(300));
        let cancel_ptr = cancel_copy as *mut ffi::AerCancelHandle;
        unsafe { ffi::aer_cancel(cancel_ptr) }
    });

    let mut events: Vec<AerEvent> = Vec::new();
    let run_code = unsafe {
        ffi::aer_task_run(
            task,
            Some(collect_ffi_events),
            &mut events as *mut _ as *mut c_void,
        )
    };
    let cancel_result = cancel_thread.join().unwrap();
    unsafe { ffi::aer_cancel_free(cancel) };
    unsafe { ffi::aer_task_free(task) };

    assert_eq!(cancel_result, AerErrorCode::Ok);
    assert_eq!(run_code, AerErrorCode::Cancelled);
    let exited = events.iter().find(|e| e.kind == 1).unwrap();
    assert_eq!(exited.reason, 2, "expected AER_EXIT_CANCEL_REQUESTED (2)");
    assert_eq!(exited.code, -1);
}

#[test]
fn ffi_cancel_null_returns_null_pointer() {
    let code = unsafe { ffi::aer_cancel(std::ptr::null_mut()) };
    assert_eq!(code, AerErrorCode::NullPointer);
}

#[test]
fn ffi_cancel_free_null_is_noop() {
    unsafe { ffi::aer_cancel_free(std::ptr::null_mut()) };
}

// --- #75: Panic Safety (Rust API) ---

/// Regression test for #75: before the fix, a panic inside the caller's
/// `on_event` callback unwound out of `run()` with the process tree still
/// armed and no cleanup — Rust callers (not shielded by the FFI boundary's
/// `catch_unwind`) leaked the child. `KillOnDropGuard` in task.rs must kill
/// the tree from `Drop` when the guard is still armed at unwind time.
#[test]
fn panicking_callback_does_not_orphan_the_process() {
    let (prog, args) = sleep_cmd(30);
    let task = Task::new(prog, args);

    let mut recorded_pid: u32 = 0;
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        task.run(|e| {
            if let Event::Started { pid } = e {
                recorded_pid = pid;
                panic!("simulated callback panic (#75 regression test)");
            }
        })
    }));

    assert!(
        result.is_err(),
        "expected the callback panic to propagate out of run()"
    );
    assert!(recorded_pid > 0, "pid was not recorded before the panic");

    // The guard's kill is dispatched synchronously as part of unwinding
    // (Drop runs on the way out of run_impl), but the OS tearing the process
    // down is not instantaneous; poll briefly rather than asserting instantly.
    let start = Instant::now();
    while process_is_alive(recorded_pid) && start.elapsed() < Duration::from_secs(5) {
        thread::sleep(Duration::from_millis(100));
    }
    assert!(
        !process_is_alive(recorded_pid),
        "process {recorded_pid} is still alive after the callback panicked — \
         the process tree was orphaned"
    );
}

// --- #77: Environment and Working-Directory Control (Rust API) ---

/// Collects all captured stdout bytes from a run's events into one buffer.
fn stdout_text(events: &[Event]) -> String {
    let bytes: Vec<u8> = events
        .iter()
        .filter_map(|e| match e {
            Event::StdoutChunk { bytes, .. } => Some(bytes.clone()),
            _ => None,
        })
        .flatten()
        .collect();
    String::from_utf8_lossy(&bytes).into_owned()
}

#[test]
fn with_env_var_is_visible_to_child() {
    let (prog, args) = echo_env_var_cmd("AER_TEST_VAR");
    let task = Task::new(prog, args)
        .with_env("AER_TEST_VAR", "hello_from_aer")
        .with_capture_output(true);
    let mut events = Vec::new();
    let result = task.run(|e| events.push(e));

    assert!(result.is_ok(), "expected Ok, got {:?}", result);
    let text = stdout_text(&events);
    assert!(
        text.contains("hello_from_aer"),
        "expected env var value in child output, got: {:?}",
        text
    );
}

#[test]
fn with_env_repeated_call_same_key_overrides_earlier_value() {
    let (prog, args) = echo_env_var_cmd("AER_TEST_VAR");
    let task = Task::new(prog, args)
        .with_env("AER_TEST_VAR", "first_value")
        .with_env("AER_TEST_VAR", "second_value")
        .with_capture_output(true);
    let mut events = Vec::new();
    let result = task.run(|e| events.push(e));

    assert!(result.is_ok(), "expected Ok, got {:?}", result);
    let text = stdout_text(&events);
    assert!(
        text.contains("second_value"),
        "expected the later with_env call to win, got: {:?}",
        text
    );
    assert!(
        !text.contains("first_value"),
        "the earlier with_env value leaked through, got: {:?}",
        text
    );
}

#[test]
fn with_clear_env_removes_inherited_var_but_keeps_explicit_ones() {
    // SAFETY: no other thread in this test binary reads this uniquely-named
    // var concurrently; set/remove bracket the run tightly.
    unsafe {
        std::env::set_var("AER_INHERITED_TEST_VAR", "should_not_be_inherited");
    }

    // Program resolved via shell_absolute_path() (see its doc comment for why),
    // not the "cmd"/"sh" from echo_two_env_vars_cmd — only its args are reused.
    let (_, args) = echo_two_env_vars_cmd("AER_INHERITED_TEST_VAR", "AER_EXPLICIT_TEST_VAR");
    let task = Task::new(shell_absolute_path(), args)
        .with_clear_env(true)
        .with_env("AER_EXPLICIT_TEST_VAR", "should_be_present")
        .with_capture_output(true);
    let mut events = Vec::new();
    let result = task.run(|e| events.push(e));

    unsafe {
        std::env::remove_var("AER_INHERITED_TEST_VAR");
    }

    assert!(result.is_ok(), "expected Ok, got {:?}", result);
    let text = stdout_text(&events);
    assert!(
        !text.contains("should_not_be_inherited"),
        "inherited var leaked through despite with_clear_env(true), got: {:?}",
        text
    );
    assert!(
        text.contains("should_be_present"),
        "explicit with_env var missing after with_clear_env(true), got: {:?}",
        text
    );
}

#[test]
fn with_cwd_changes_child_working_directory() {
    let target_dir = std::env::temp_dir();
    let expected = fs::canonicalize(&target_dir).expect("canonicalize temp dir");

    let (prog, args) = print_cwd_cmd();
    let task = Task::new(prog, args)
        .with_cwd(&target_dir)
        .with_capture_output(true);
    let mut events = Vec::new();
    let result = task.run(|e| events.push(e));

    assert!(result.is_ok(), "expected Ok, got {:?}", result);
    let printed = stdout_text(&events);
    let printed_line = printed
        .lines()
        .find(|l| !l.trim().is_empty())
        .expect("expected a non-empty line of output containing the cwd");
    let actual = fs::canonicalize(printed_line.trim())
        .unwrap_or_else(|e| panic!("failed to canonicalize printed cwd {printed_line:?}: {e}"));

    assert_eq!(actual, expected, "child did not run in the configured cwd");
}

#[test]
fn with_cwd_invalid_path_returns_spawn_failed_and_emits_no_events() {
    let (prog, args) = exit_cmd(0);
    let task = Task::new(prog, args).with_cwd("definitely_not_a_real_directory_xyzzy_aer");
    let mut events = Vec::new();
    let result = task.run(|e| events.push(e));

    assert!(
        matches!(result, Err(AerError::SpawnFailed(_))),
        "expected SpawnFailed, got {:?}",
        result
    );
    assert!(
        events.is_empty(),
        "no events should be emitted when spawn fails due to invalid cwd"
    );
}

// --- #77: Environment and Working-Directory Control (FFI) ---

#[test]
fn ffi_set_env_and_cwd_visible_in_child() {
    let target_dir = std::env::temp_dir();
    let expected = fs::canonicalize(&target_dir).expect("canonicalize temp dir");

    let (prog, args) = echo_env_var_and_cwd_cmd("AER_FFI_TEST_VAR");
    let task = ffi_new_task(&prog, &args);
    assert!(!task.is_null());

    let key = CString::new("AER_FFI_TEST_VAR").unwrap();
    let value = CString::new("ffi_env_value").unwrap();
    let env_code = unsafe { ffi::aer_task_set_env(task, key.as_ptr(), value.as_ptr()) };
    assert_eq!(env_code, AerErrorCode::Ok);

    let cwd_cstring = CString::new(target_dir.to_str().unwrap()).unwrap();
    let cwd_code = unsafe { ffi::aer_task_set_cwd(task, cwd_cstring.as_ptr()) };
    assert_eq!(cwd_code, AerErrorCode::Ok);

    let cap_code = unsafe { ffi::aer_task_with_capture_output(task, true) };
    assert_eq!(cap_code, AerErrorCode::Ok);

    let mut data = FfiCaptureData {
        started_pid: 0,
        exited_code: -999,
        stdout_chunks: Vec::new(),
        stderr_chunks: Vec::new(),
    };
    let run_code = unsafe {
        ffi::aer_task_run(
            task,
            Some(collect_ffi_capture),
            &mut data as *mut _ as *mut c_void,
        )
    };
    unsafe { ffi::aer_task_free(task) };

    assert_eq!(run_code, AerErrorCode::Ok);
    let all: Vec<u8> = data.stdout_chunks.into_iter().flatten().collect();
    let text = String::from_utf8_lossy(&all);
    assert!(
        text.contains("ffi_env_value"),
        "expected env var value in child output, got: {:?}",
        text
    );

    let printed_cwd_line = text
        .lines()
        .rev()
        .find(|l| !l.trim().is_empty())
        .expect("expected a non-empty trailing line containing the cwd");
    let actual = fs::canonicalize(printed_cwd_line.trim())
        .unwrap_or_else(|e| panic!("failed to canonicalize printed cwd {printed_cwd_line:?}: {e}"));
    assert_eq!(actual, expected, "child did not run in the configured cwd");
}

#[test]
fn ffi_set_clear_env_removes_inherited_var() {
    unsafe {
        std::env::set_var("AER_FFI_INHERITED_VAR", "should_not_be_inherited_ffi");
    }

    // Program resolved via shell_absolute_path() (see its doc comment for why),
    // not the "cmd"/"sh" from echo_env_var_cmd — only its args are reused.
    let (_, args) = echo_env_var_cmd("AER_FFI_INHERITED_VAR");
    let task = ffi_new_task(&shell_absolute_path(), &args);
    assert!(!task.is_null());

    let clear_code = unsafe { ffi::aer_task_set_clear_env(task, true) };
    assert_eq!(clear_code, AerErrorCode::Ok);

    let cap_code = unsafe { ffi::aer_task_with_capture_output(task, true) };
    assert_eq!(cap_code, AerErrorCode::Ok);

    let mut data = FfiCaptureData {
        started_pid: 0,
        exited_code: -999,
        stdout_chunks: Vec::new(),
        stderr_chunks: Vec::new(),
    };
    let run_code = unsafe {
        ffi::aer_task_run(
            task,
            Some(collect_ffi_capture),
            &mut data as *mut _ as *mut c_void,
        )
    };
    unsafe { ffi::aer_task_free(task) };
    unsafe {
        std::env::remove_var("AER_FFI_INHERITED_VAR");
    }

    assert_eq!(run_code, AerErrorCode::Ok);
    let all: Vec<u8> = data.stdout_chunks.into_iter().flatten().collect();
    let text = String::from_utf8_lossy(&all);
    assert!(
        !text.contains("should_not_be_inherited_ffi"),
        "inherited var leaked through despite aer_task_set_clear_env(true), got: {:?}",
        text
    );
}

#[test]
fn ffi_set_env_null_task_returns_null_pointer() {
    let key = CString::new("K").unwrap();
    let value = CString::new("V").unwrap();
    let code = unsafe { ffi::aer_task_set_env(std::ptr::null_mut(), key.as_ptr(), value.as_ptr()) };
    assert_eq!(code, AerErrorCode::NullPointer);
}

#[test]
fn ffi_set_env_empty_key_returns_invalid_argument() {
    let (prog, args) = exit_cmd(0);
    let task = ffi_new_task(&prog, &args);
    assert!(!task.is_null());

    let key = CString::new("").unwrap();
    let value = CString::new("V").unwrap();
    let code = unsafe { ffi::aer_task_set_env(task, key.as_ptr(), value.as_ptr()) };
    unsafe { ffi::aer_task_free(task) };

    assert_eq!(code, AerErrorCode::InvalidArgument);
}

#[test]
fn ffi_set_env_key_containing_equals_returns_invalid_argument() {
    let (prog, args) = exit_cmd(0);
    let task = ffi_new_task(&prog, &args);
    assert!(!task.is_null());

    let key = CString::new("BAD=KEY").unwrap();
    let value = CString::new("V").unwrap();
    let code = unsafe { ffi::aer_task_set_env(task, key.as_ptr(), value.as_ptr()) };
    unsafe { ffi::aer_task_free(task) };

    assert_eq!(code, AerErrorCode::InvalidArgument);
}

#[test]
fn ffi_set_cwd_empty_returns_invalid_argument() {
    let (prog, args) = exit_cmd(0);
    let task = ffi_new_task(&prog, &args);
    assert!(!task.is_null());

    let path = CString::new("").unwrap();
    let code = unsafe { ffi::aer_task_set_cwd(task, path.as_ptr()) };
    unsafe { ffi::aer_task_free(task) };

    assert_eq!(code, AerErrorCode::InvalidArgument);
}

#[test]
fn ffi_set_cwd_invalid_path_causes_spawn_failed_on_run() {
    let (prog, args) = exit_cmd(0);
    let task = ffi_new_task(&prog, &args);
    assert!(!task.is_null());

    let path = CString::new("definitely_not_a_real_directory_xyzzy_aer").unwrap();
    let cwd_code = unsafe { ffi::aer_task_set_cwd(task, path.as_ptr()) };
    assert_eq!(cwd_code, AerErrorCode::Ok);

    let mut events: Vec<AerEvent> = Vec::new();
    let run_code = unsafe {
        ffi::aer_task_run(
            task,
            Some(collect_ffi_events),
            &mut events as *mut _ as *mut c_void,
        )
    };
    unsafe { ffi::aer_task_free(task) };

    assert_eq!(run_code, AerErrorCode::SpawnFailed);
    assert!(
        events.is_empty(),
        "no events should be emitted when spawn fails due to invalid cwd"
    );
}
