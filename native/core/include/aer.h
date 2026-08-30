#ifndef AER_H
#define AER_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* -------------------------------------------------------------------------
 * Error codes
 * All FFI functions that can fail return one of these values.
 * The integer values are stable ABI — never reorder or renumber them.
 * ------------------------------------------------------------------------- */
typedef enum {
    AER_OK = 0,
    AER_ERR_NULL_POINTER = 1,         /* a required pointer argument was NULL */
    AER_ERR_SPAWN_FAILED = 2,         /* OS refused to spawn the process */
    AER_ERR_WAIT_FAILED = 3,          /* OS error while waiting for exit */
    AER_ERR_INVALID_STATE_TRANSITION = 4,
    AER_ERR_TIMED_OUT = 5,            /* process killed because timeout elapsed */
    AER_ERR_KILL_FAILED = 6,          /* kill attempt itself failed */
    AER_ERR_ALREADY_RUN = 7,          /* aer_task_run called more than once */
    AER_ERR_PANIC = 8,                /* unexpected internal panic (see aer_last_error_message) */
    AER_ERR_CANCELLED = 9,            /* process was killed by an explicit cancel request */
    AER_ERR_INVALID_ARGUMENT = 10,    /* string argument failed validation (bad UTF-8, empty key, key contains '=') */
} AerErrorCode;

/* -------------------------------------------------------------------------
 * Event kinds
 * Matches the `kind` field in AerEvent.
 * ------------------------------------------------------------------------- */
#define AER_EVENT_STARTED       0u  /* pid field valid */
#define AER_EVENT_EXITED        1u  /* code and reason fields valid */
#define AER_EVENT_STDOUT_CHUNK  2u  /* seq, data, data_len valid; only with capture enabled */
#define AER_EVENT_STDERR_CHUNK  3u  /* seq, data, data_len valid; only with capture enabled */

/* -------------------------------------------------------------------------
 * Exit reason
 * Delivered in the `reason` field of an AER_EVENT_EXITED event.
 * The integer values are stable ABI — never reorder or renumber them.
 * ------------------------------------------------------------------------- */
typedef enum {
    AER_EXIT_NATURAL        = 0,  /* process exited on its own */
    AER_EXIT_TIMED_OUT      = 1,  /* killed because the configured timeout elapsed */
    AER_EXIT_CANCEL_REQUESTED = 2, /* killed by an explicit aer_cancel() call */
} AerExitReason;

/* -------------------------------------------------------------------------
 * Event
 * Delivered to AerEventCallback. Check `kind` to determine which fields apply.
 *
 * Layout (all targets, 64-bit assumed for pointer/size fields):
 *   offset  0: kind      uint32_t
 *   offset  4: pid       uint32_t
 *   offset  8: code      int32_t
 *   offset 12: reason    uint32_t   — AerExitReason when kind == AER_EVENT_EXITED, else 0
 *   offset 16: seq       uint64_t
 *   offset 24: data      const uint8_t *
 *   offset 32: data_len  size_t
 *   total: 40 bytes (64-bit)
 *
 * For chunk kinds (2, 3): `data` is only valid for the duration of the callback.
 * Copy the bytes if you need them after the callback returns.
 * ------------------------------------------------------------------------- */
typedef struct {
    uint32_t        kind;
    uint32_t        pid;       /* process ID; meaningful when kind == AER_EVENT_STARTED */
    int32_t         code;      /* exit code; meaningful when kind == AER_EVENT_EXITED; -1 on kill */
    uint32_t        reason;    /* AerExitReason; meaningful when kind == AER_EVENT_EXITED */
    uint64_t        seq;       /* chunk sequence number within stream; meaningful when kind == 2 or 3 */
    const uint8_t  *data;      /* chunk bytes; meaningful when kind == 2 or 3; valid only during callback */
    size_t          data_len;  /* byte count for data */
} AerEvent;

/* -------------------------------------------------------------------------
 * Task (opaque handle)
 * Allocate with aer_task_new; free with aer_task_free.
 * Do not share a single handle across threads without external synchronisation.
 * ------------------------------------------------------------------------- */
typedef struct AerTask AerTask;

/* -------------------------------------------------------------------------
 * Cancel handle (opaque handle)
 * Create with aer_task_make_cancel_handle; free with aer_cancel_free.
 * Thread-safe: may be called from any thread at any time.
 * ------------------------------------------------------------------------- */
typedef struct AerCancelHandle AerCancelHandle;

/* -------------------------------------------------------------------------
 * Event callback
 * Called synchronously from aer_task_run on the calling thread.
 * The `event` pointer is only valid for the duration of the callback.
 * `user_data` is whatever was passed to aer_task_run.
 * ------------------------------------------------------------------------- */
typedef void (*AerEventCallback)(const AerEvent *event, void *user_data);

/* -------------------------------------------------------------------------
 * API
 * ------------------------------------------------------------------------- */

/**
 * Create a new task.
 *
 * `program`  — null-terminated, valid UTF-8, no embedded NUL bytes.
 * `args`     — array of `args_len` null-terminated strings; may be NULL when args_len == 0.
 * `args_len` — must not exceed 65536; larger values return NULL without
 *              allocating.
 * Returns NULL on any invalid input (NULL program, non-UTF-8 string, NULL
 * element, or args_len exceeding the 65536 cap).
 * The returned handle must be freed with aer_task_free.
 */
AerTask *aer_task_new(const char *program,
                      const char *const *args,
                      size_t args_len);

/**
 * Set a wall-clock timeout in milliseconds.
 *
 * Must be called before aer_task_run. If the process has not exited by the
 * deadline it is killed and aer_task_run returns AER_ERR_TIMED_OUT.
 */
AerErrorCode aer_task_with_timeout(AerTask *task, uint64_t timeout_ms);

/**
 * Enable stdout and stderr capture.
 *
 * Must be called before aer_task_run. When enabled, StdoutChunk (kind=2)
 * and StderrChunk (kind=3) events are delivered to the callback between
 * Started and Exited. The `data` pointer in those events is only valid
 * for the duration of the callback; copy the bytes if needed after return.
 */
AerErrorCode aer_task_with_capture_output(AerTask *task, bool capture);

/**
 * Set an environment variable for the child process.
 *
 * Must be called before aer_task_run. Repeatable: calling this again with
 * the same `key` overrides the previously set value. Variables set this way
 * are always visible to the child, regardless of aer_task_set_clear_env.
 *
 * `key` and `value` — null-terminated, valid UTF-8, no embedded NUL bytes.
 * Returns AER_ERR_INVALID_ARGUMENT if either is not valid UTF-8, if `key`
 * is empty, or if `key` contains '='.
 */
AerErrorCode aer_task_set_env(AerTask *task, const char *key, const char *value);

/**
 * Set whether the child process inherits the parent's environment.
 *
 * Must be called before aer_task_run. When `clear` is true, the child
 * inherits nothing from the parent environment — only variables set via
 * aer_task_set_env are present. Default is false (inherit the full parent
 * environment).
 */
AerErrorCode aer_task_set_clear_env(AerTask *task, bool clear);

/**
 * Set the child process's working directory.
 *
 * Must be called before aer_task_run. If the path does not exist or is not
 * a directory, this surfaces at aer_task_run time as AER_ERR_SPAWN_FAILED —
 * no events are emitted for that run.
 *
 * `path` — null-terminated, valid UTF-8, no embedded NUL bytes.
 * Returns AER_ERR_INVALID_ARGUMENT if `path` is not valid UTF-8 or is empty.
 */
AerErrorCode aer_task_set_cwd(AerTask *task, const char *path);

/**
 * Create a cancellation handle for this task.
 *
 * Must be called before aer_task_run. The returned handle is independent of
 * the task handle and must be freed separately with aer_cancel_free.
 * Pass the handle to aer_cancel() from any thread to cancel the execution.
 *
 * Returns NULL if task is NULL or on allocation failure.
 */
AerCancelHandle *aer_task_make_cancel_handle(AerTask *task);

/**
 * Spawn the process and block until it exits.
 *
 * `callback`  — called with Started, optional chunks, then Exited; may be NULL to ignore events.
 * `user_data` — passed through to callback unchanged; may be NULL.
 *               Must remain valid for the duration of this call.
 *
 * A given handle may only be run once; a second call returns AER_ERR_ALREADY_RUN.
 * Call aer_last_error_message() to get a human-readable description of any error.
 */
AerErrorCode aer_task_run(AerTask *task,
                           AerEventCallback callback,
                           void *user_data);

/**
 * Free a task handle. Safe to call with NULL (no-op).
 * Do not use the handle after this call.
 */
void aer_task_free(AerTask *task);

/**
 * Cancel a running task. Kills the process tree immediately.
 *
 * Safe to call before the process starts, after it exits, or from multiple
 * threads concurrently. Only the first call has effect; subsequent calls
 * return AER_OK without error.
 *
 * When the cancellation takes effect, aer_task_run returns AER_ERR_CANCELLED
 * and the Exited event has reason == AER_EXIT_CANCEL_REQUESTED.
 */
AerErrorCode aer_cancel(AerCancelHandle *cancel);

/**
 * Free a cancel handle. Safe to call with NULL (no-op).
 * Do not use the handle after this call.
 */
void aer_cancel_free(AerCancelHandle *cancel);

/**
 * Return the last error message for this thread as a null-terminated C string.
 * Returns NULL if no error has occurred since the last successful operation.
 *
 * The pointer is valid until the next FFI call on this thread. Do not free it.
 */
const char *aer_last_error_message(void);

#ifdef __cplusplus
} /* extern "C" */
#endif

#endif /* AER_H */
