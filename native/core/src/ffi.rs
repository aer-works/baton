use crate::{AerError, CancelHandle, Event, Task};
use std::ffi::{c_char, c_void, CStr, CString};
use std::panic::AssertUnwindSafe;
use std::time::Duration;

// Thread-local stores the last error message so callers can retrieve OS details
// (e.g. ENOENT vs EACCES on spawn failure) without widening the integer ABI.
thread_local! {
    static LAST_ERROR: std::cell::RefCell<Option<CString>> = const { std::cell::RefCell::new(None) };
}

fn set_last_error(msg: String) {
    LAST_ERROR.with(|e| {
        *e.borrow_mut() = CString::new(msg).ok();
    });
}

fn clear_last_error() {
    LAST_ERROR.with(|e| {
        *e.borrow_mut() = None;
    });
}

fn format_panic(payload: Box<dyn std::any::Any + Send>) -> String {
    if let Some(s) = payload.downcast_ref::<&str>() {
        format!("panic: {}", s)
    } else if let Some(s) = payload.downcast_ref::<String>() {
        format!("panic: {}", s)
    } else {
        "panic: unknown panic payload".to_string()
    }
}

fn map_aer_error(e: AerError) -> AerErrorCode {
    set_last_error(e.to_string());
    match e {
        AerError::SpawnFailed(_) => AerErrorCode::SpawnFailed,
        AerError::WaitFailed(_) => AerErrorCode::WaitFailed,
        AerError::InvalidStateTransition { .. } => AerErrorCode::InvalidStateTransition,
        AerError::TimedOut => AerErrorCode::TimedOut,
        AerError::KillFailed(_) => AerErrorCode::KillFailed,
        AerError::Cancelled => AerErrorCode::Cancelled,
    }
}

/// Error codes returned by FFI functions. Values are stable ABI.
#[repr(C)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AerErrorCode {
    Ok = 0,
    NullPointer = 1,
    SpawnFailed = 2,
    WaitFailed = 3,
    InvalidStateTransition = 4,
    TimedOut = 5,
    KillFailed = 6,
    /// Caller invoked aer_task_run a second time on the same handle.
    AlreadyRun = 7,
    /// An unexpected panic was caught at the FFI boundary.
    Panic = 8,
    /// The process was killed by an explicit cancel request.
    Cancelled = 9,
    /// A string argument failed validation (invalid UTF-8, empty key, or a
    /// key containing '=').
    InvalidArgument = 10,
}

/// Upper bound on `args_len` accepted by `aer_task_new`. No real invocation
/// needs anywhere near this many arguments; rejecting absurd values here
/// avoids handing an attacker-controlled `args_len` straight to
/// `Vec::with_capacity` before any pointer has been validated.
const MAX_ARGS_LEN: usize = 65_536;

/// C-compatible event delivered to AerEventCallback.
///
/// `kind` selects which fields are valid:
///   0 = Started  — `pid` is the process ID
///   1 = Exited   — `code` is the exit code (-1 if killed), `reason` is AerExitReason
///   2 = StdoutChunk — `seq`, `data`, `data_len` are valid; only when capture enabled
///   3 = StderrChunk — `seq`, `data`, `data_len` are valid; only when capture enabled
///
/// For chunk kinds, `data` points into Rust-owned memory that is only valid for
/// the duration of the callback. Copy the bytes before the callback returns.
///
/// Layout (64-bit targets):
///   offset  0: kind      u32
///   offset  4: pid       u32
///   offset  8: code      i32
///   offset 12: reason    u32   — AerExitReason when kind == 1, else 0
///   offset 16: seq       u64
///   offset 24: data      *const u8
///   offset 32: data_len  usize
///   total: 40 bytes
#[repr(C)]
#[derive(Clone, Copy)]
pub struct AerEvent {
    pub kind: u32,
    pub pid: u32,        // valid when kind == 0
    pub code: i32,       // valid when kind == 1
    pub reason: u32,     // AerExitReason when kind == 1; 0 otherwise
    pub seq: u64,        // valid when kind == 2 or 3; monotonically increasing within stream
    pub data: *const u8, // valid when kind == 2 or 3; only valid during the callback
    pub data_len: usize, // byte count for data
}

/// Nullable C function pointer for receiving events. Pass NULL to ignore events.
pub type AerEventCallback = Option<unsafe extern "C" fn(*const AerEvent, *mut c_void)>;

/// Opaque handle to a Task. Heap-allocated; free with aer_task_free.
pub struct AerTask {
    // Option<Task> so we can call consuming builders:
    // take() → with_timeout() / with_capture_output() → put back.
    inner: Option<Task>,
    has_run: bool,
    // Populated by aer_task_make_cancel_handle; used by aer_task_run.
    cancel: Option<CancelHandle>,
}

/// Opaque cancellation handle. Create with aer_task_make_cancel_handle;
/// free with aer_cancel_free.
pub struct AerCancelHandle(CancelHandle);

/// Create a new task. Returns NULL on null/invalid-UTF-8 program or any null arg.
///
/// `args` may be NULL when `args_len` is 0.
/// All strings must be valid UTF-8 with no embedded NUL bytes.
/// `args_len` must not exceed `MAX_ARGS_LEN` (65_536); larger values return
/// NULL without allocating.
/// The returned pointer must be freed with aer_task_free.
///
/// # Safety
/// `program` must be a valid null-terminated C string or NULL.
/// `args` must point to an array of `args_len` valid null-terminated C strings, or NULL when `args_len` is 0.
#[no_mangle]
pub unsafe extern "C" fn aer_task_new(
    program: *const c_char,
    args: *const *const c_char,
    args_len: usize,
) -> *mut AerTask {
    match std::panic::catch_unwind(|| {
        if program.is_null() {
            return std::ptr::null_mut();
        }
        let program_str = match unsafe { CStr::from_ptr(program) }.to_str() {
            Ok(s) => s.to_owned(),
            Err(_) => return std::ptr::null_mut(),
        };

        // Validate args_len and the args pointer before allocating anything
        // sized by it — an absurd caller-supplied args_len must not reach
        // Vec::with_capacity, which would abort the process on allocation
        // failure instead of returning the NULL this function documents.
        if args_len > MAX_ARGS_LEN {
            return std::ptr::null_mut();
        }
        if args_len > 0 && args.is_null() {
            return std::ptr::null_mut();
        }

        let mut arg_strings: Vec<String> = Vec::with_capacity(args_len);
        for i in 0..args_len {
            let arg_ptr = unsafe { *args.add(i) };
            if arg_ptr.is_null() {
                return std::ptr::null_mut();
            }
            match unsafe { CStr::from_ptr(arg_ptr) }.to_str() {
                Ok(s) => arg_strings.push(s.to_owned()),
                Err(_) => return std::ptr::null_mut(),
            }
        }

        let task = Task::new(program_str, arg_strings);
        Box::into_raw(Box::new(AerTask {
            inner: Some(task),
            has_run: false,
            cancel: None,
        }))
    }) {
        Ok(ptr) => ptr,
        Err(e) => {
            set_last_error(format_panic(e));
            std::ptr::null_mut()
        }
    }
}

/// Set a timeout on the task. Must be called before aer_task_run.
///
/// # Safety
/// `task` must be a valid pointer returned by aer_task_new that has not been freed.
#[no_mangle]
pub unsafe extern "C" fn aer_task_with_timeout(
    task: *mut AerTask,
    timeout_ms: u64,
) -> AerErrorCode {
    match std::panic::catch_unwind(AssertUnwindSafe(|| {
        if task.is_null() {
            return AerErrorCode::NullPointer;
        }
        let t = unsafe { &mut *task };
        if t.has_run {
            return AerErrorCode::AlreadyRun;
        }
        match t.inner.take() {
            Some(inner) => {
                t.inner = Some(inner.with_timeout(Duration::from_millis(timeout_ms)));
                clear_last_error();
                AerErrorCode::Ok
            }
            None => AerErrorCode::AlreadyRun,
        }
    })) {
        Ok(code) => code,
        Err(e) => {
            set_last_error(format_panic(e));
            AerErrorCode::Panic
        }
    }
}

/// Enable stdout and stderr capture on the task. Must be called before aer_task_run.
///
/// When enabled, StdoutChunk (kind=2) and StderrChunk (kind=3) events are delivered
/// to the callback between Started and Exited. The `data` pointer in those events
/// is only valid for the duration of the callback; copy the bytes if needed after return.
///
/// # Safety
/// `task` must be a valid pointer returned by aer_task_new that has not been freed.
#[no_mangle]
pub unsafe extern "C" fn aer_task_with_capture_output(
    task: *mut AerTask,
    capture: bool,
) -> AerErrorCode {
    match std::panic::catch_unwind(AssertUnwindSafe(|| {
        if task.is_null() {
            return AerErrorCode::NullPointer;
        }
        let t = unsafe { &mut *task };
        if t.has_run {
            return AerErrorCode::AlreadyRun;
        }
        match t.inner.take() {
            Some(inner) => {
                t.inner = Some(inner.with_capture_output(capture));
                clear_last_error();
                AerErrorCode::Ok
            }
            None => AerErrorCode::AlreadyRun,
        }
    })) {
        Ok(code) => code,
        Err(e) => {
            set_last_error(format_panic(e));
            AerErrorCode::Panic
        }
    }
}

/// Set an environment variable for the child process. Must be called before
/// aer_task_run.
///
/// Repeatable: calling this again with the same `key` overrides the
/// previously set value. Returns `AER_ERR_INVALID_ARGUMENT` if `key` or
/// `value` is not valid UTF-8, if `key` is empty, or if `key` contains '='.
///
/// # Safety
/// `task` must be a valid pointer returned by aer_task_new that has not been freed.
/// `key` and `value` must be valid null-terminated UTF-8 C strings.
#[no_mangle]
pub unsafe extern "C" fn aer_task_set_env(
    task: *mut AerTask,
    key: *const c_char,
    value: *const c_char,
) -> AerErrorCode {
    match std::panic::catch_unwind(AssertUnwindSafe(|| {
        if task.is_null() || key.is_null() || value.is_null() {
            return AerErrorCode::NullPointer;
        }
        let t = unsafe { &mut *task };
        if t.has_run {
            return AerErrorCode::AlreadyRun;
        }
        let key_str = match unsafe { CStr::from_ptr(key) }.to_str() {
            Ok(s) => s,
            Err(_) => return AerErrorCode::InvalidArgument,
        };
        let value_str = match unsafe { CStr::from_ptr(value) }.to_str() {
            Ok(s) => s,
            Err(_) => return AerErrorCode::InvalidArgument,
        };
        if key_str.is_empty() || key_str.contains('=') {
            return AerErrorCode::InvalidArgument;
        }
        match t.inner.take() {
            Some(inner) => {
                t.inner = Some(inner.with_env(key_str, value_str));
                clear_last_error();
                AerErrorCode::Ok
            }
            None => AerErrorCode::AlreadyRun,
        }
    })) {
        Ok(code) => code,
        Err(e) => {
            set_last_error(format_panic(e));
            AerErrorCode::Panic
        }
    }
}

/// Set whether the child process inherits the parent's environment. Must be
/// called before aer_task_run.
///
/// When `clear` is true, the child inherits nothing from the parent
/// environment — only variables set via aer_task_set_env are present.
/// Default is false (inherit the full parent environment).
///
/// # Safety
/// `task` must be a valid pointer returned by aer_task_new that has not been freed.
#[no_mangle]
pub unsafe extern "C" fn aer_task_set_clear_env(task: *mut AerTask, clear: bool) -> AerErrorCode {
    match std::panic::catch_unwind(AssertUnwindSafe(|| {
        if task.is_null() {
            return AerErrorCode::NullPointer;
        }
        let t = unsafe { &mut *task };
        if t.has_run {
            return AerErrorCode::AlreadyRun;
        }
        match t.inner.take() {
            Some(inner) => {
                t.inner = Some(inner.with_clear_env(clear));
                clear_last_error();
                AerErrorCode::Ok
            }
            None => AerErrorCode::AlreadyRun,
        }
    })) {
        Ok(code) => code,
        Err(e) => {
            set_last_error(format_panic(e));
            AerErrorCode::Panic
        }
    }
}

/// Set the child process's working directory. Must be called before
/// aer_task_run.
///
/// If the path does not exist or is not a directory, this surfaces at
/// aer_task_run time as AER_ERR_SPAWN_FAILED (std's Command::current_dir
/// behavior) — no events are emitted for that run.
///
/// Returns `AER_ERR_INVALID_ARGUMENT` if `path` is not valid UTF-8 or is empty.
///
/// # Safety
/// `task` must be a valid pointer returned by aer_task_new that has not been freed.
/// `path` must be a valid null-terminated UTF-8 C string.
#[no_mangle]
pub unsafe extern "C" fn aer_task_set_cwd(task: *mut AerTask, path: *const c_char) -> AerErrorCode {
    match std::panic::catch_unwind(AssertUnwindSafe(|| {
        if task.is_null() || path.is_null() {
            return AerErrorCode::NullPointer;
        }
        let t = unsafe { &mut *task };
        if t.has_run {
            return AerErrorCode::AlreadyRun;
        }
        let path_str = match unsafe { CStr::from_ptr(path) }.to_str() {
            Ok(s) => s,
            Err(_) => return AerErrorCode::InvalidArgument,
        };
        if path_str.is_empty() {
            return AerErrorCode::InvalidArgument;
        }
        match t.inner.take() {
            Some(inner) => {
                t.inner = Some(inner.with_cwd(path_str));
                clear_last_error();
                AerErrorCode::Ok
            }
            None => AerErrorCode::AlreadyRun,
        }
    })) {
        Ok(code) => code,
        Err(e) => {
            set_last_error(format_panic(e));
            AerErrorCode::Panic
        }
    }
}

/// Create a cancellation handle for this task.
///
/// Must be called before aer_task_run. The returned handle can be passed to
/// aer_cancel() from any thread at any time to cancel the running execution.
/// The handle is independent of the task handle — free it with aer_cancel_free
/// after the task has completed (or whenever it's no longer needed).
///
/// Returns NULL on memory allocation failure or if `task` is NULL.
///
/// # Safety
/// `task` must be a valid pointer returned by aer_task_new that has not been freed.
#[no_mangle]
pub unsafe extern "C" fn aer_task_make_cancel_handle(task: *mut AerTask) -> *mut AerCancelHandle {
    match std::panic::catch_unwind(AssertUnwindSafe(|| {
        if task.is_null() {
            return std::ptr::null_mut();
        }
        let t = unsafe { &mut *task };
        let cancel = CancelHandle::new();
        // Clone into the task so aer_task_run can call run_with_cancel
        t.cancel = Some(cancel.clone());
        Box::into_raw(Box::new(AerCancelHandle(cancel)))
    })) {
        Ok(ptr) => ptr,
        Err(e) => {
            set_last_error(format_panic(e));
            std::ptr::null_mut()
        }
    }
}

/// Cancel a running task. Kills the process tree immediately.
///
/// Safe to call before the process starts, after it exits, or concurrently
/// from multiple threads. Only the first call has effect; subsequent calls
/// are no-ops and return AER_OK.
///
/// # Safety
/// `cancel` must be a valid pointer returned by aer_task_make_cancel_handle, or NULL.
#[no_mangle]
pub unsafe extern "C" fn aer_cancel(cancel: *mut AerCancelHandle) -> AerErrorCode {
    match std::panic::catch_unwind(AssertUnwindSafe(|| {
        if cancel.is_null() {
            return AerErrorCode::NullPointer;
        }
        let c = unsafe { &*cancel };
        c.0.cancel();
        clear_last_error();
        AerErrorCode::Ok
    })) {
        Ok(code) => code,
        Err(e) => {
            set_last_error(format_panic(e));
            AerErrorCode::Panic
        }
    }
}

/// Free a cancel handle. Safe to call with NULL (no-op).
///
/// # Safety
/// `cancel` must be a valid pointer returned by aer_task_make_cancel_handle, or NULL.
/// Do not use the handle after this call.
#[no_mangle]
pub unsafe extern "C" fn aer_cancel_free(cancel: *mut AerCancelHandle) {
    if cancel.is_null() {
        return;
    }
    let _ = std::panic::catch_unwind(|| {
        drop(unsafe { Box::from_raw(cancel) });
    });
}

/// Run the task. May only be called once per handle.
///
/// `callback` may be NULL to ignore events.
/// `user_data` is passed through to the callback unchanged; may be NULL.
/// `user_data` must remain valid for the duration of this call.
///
/// # Safety
/// `task` must be a valid pointer returned by aer_task_new that has not been freed.
/// If `callback` is non-null, it must be a valid function pointer.
/// `user_data` must satisfy any aliasing requirements expected by `callback`.
#[no_mangle]
pub unsafe extern "C" fn aer_task_run(
    task: *mut AerTask,
    callback: AerEventCallback,
    user_data: *mut c_void,
) -> AerErrorCode {
    match std::panic::catch_unwind(AssertUnwindSafe(|| {
        if task.is_null() {
            return AerErrorCode::NullPointer;
        }
        let t = unsafe { &mut *task };
        if t.has_run {
            return AerErrorCode::AlreadyRun;
        }
        t.has_run = true;

        let inner = match t.inner.as_ref() {
            Some(task) => task,
            None => return AerErrorCode::AlreadyRun,
        };

        let on_event = |event: Event| {
            if let Some(cb) = callback {
                match event {
                    Event::Started { pid } => {
                        let c_event = AerEvent {
                            kind: 0,
                            pid,
                            code: 0,
                            reason: 0,
                            seq: 0,
                            data: std::ptr::null(),
                            data_len: 0,
                        };
                        unsafe { cb(&c_event as *const AerEvent, user_data) };
                    }
                    Event::Exited { code, reason } => {
                        let c_event = AerEvent {
                            kind: 1,
                            pid: 0,
                            code,
                            reason: reason as u32,
                            seq: 0,
                            data: std::ptr::null(),
                            data_len: 0,
                        };
                        unsafe { cb(&c_event as *const AerEvent, user_data) };
                    }
                    Event::StdoutChunk { seq, bytes } => {
                        let c_event = AerEvent {
                            kind: 2,
                            pid: 0,
                            code: 0,
                            reason: 0,
                            seq,
                            data: bytes.as_ptr(),
                            data_len: bytes.len(),
                        };
                        // bytes is alive here; pointer valid during cb
                        unsafe { cb(&c_event as *const AerEvent, user_data) };
                    }
                    Event::StderrChunk { seq, bytes } => {
                        let c_event = AerEvent {
                            kind: 3,
                            pid: 0,
                            code: 0,
                            reason: 0,
                            seq,
                            data: bytes.as_ptr(),
                            data_len: bytes.len(),
                        };
                        unsafe { cb(&c_event as *const AerEvent, user_data) };
                    }
                }
            }
        };

        let result = if let Some(ref cancel) = t.cancel {
            inner.run_with_cancel(on_event, cancel)
        } else {
            inner.run(on_event)
        };

        match result {
            Ok(()) => {
                clear_last_error();
                AerErrorCode::Ok
            }
            Err(e) => map_aer_error(e),
        }
    })) {
        Ok(code) => code,
        Err(e) => {
            set_last_error(format_panic(e));
            AerErrorCode::Panic
        }
    }
}

/// Free a task handle. Safe to call with NULL (no-op).
///
/// # Safety
/// `task` must be a valid pointer returned by aer_task_new, or NULL.
/// Do not use the handle after this call.
#[no_mangle]
pub unsafe extern "C" fn aer_task_free(task: *mut AerTask) {
    if task.is_null() {
        return;
    }
    let _ = std::panic::catch_unwind(|| {
        drop(unsafe { Box::from_raw(task) });
    });
}

/// Returns the last error message as a null-terminated C string, or NULL if none.
///
/// The pointer is valid until the next FFI call on this thread. Do not free it.
#[no_mangle]
pub extern "C" fn aer_last_error_message() -> *const c_char {
    LAST_ERROR.with(|e| match &*e.borrow() {
        Some(s) => s.as_ptr(),
        None => std::ptr::null(),
    })
}
