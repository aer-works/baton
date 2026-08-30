use std::fmt;

/// Why a process execution ended.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u32)]
pub enum ExitReason {
    /// The process exited on its own before any timeout or cancellation.
    NaturalExit = 0,
    /// The configured timeout elapsed and the process was killed.
    TimedOut = 1,
    /// An explicit cancel request killed the process.
    CancelRequested = 2,
}

/// Events emitted during a single task execution.
/// `Started` always precedes `Exited`; no other ordering is valid.
/// `StdoutChunk`/`StderrChunk` are emitted only when `CaptureOutput` is enabled
/// and always appear strictly between `Started` and `Exited`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Event {
    /// Emitted immediately after the OS confirms process spawn.
    Started { pid: u32 },
    /// Emitted after the process exits. `code` is -1 if the OS provides no exit code
    /// (e.g. signal-killed or killed by timeout/cancel). `reason` explains why it stopped.
    Exited { code: i32, reason: ExitReason },
    /// A chunk of bytes from the process's stdout pipe. `seq` is monotonically
    /// increasing within the stdout stream only; no cross-stream ordering is implied.
    /// Only emitted when `Task::with_capture_output(true)` is set.
    StdoutChunk { seq: u64, bytes: Vec<u8> },
    /// A chunk of bytes from the process's stderr pipe. `seq` is monotonically
    /// increasing within the stderr stream only; no cross-stream ordering is implied.
    /// Only emitted when `Task::with_capture_output(true)` is set.
    StderrChunk { seq: u64, bytes: Vec<u8> },
}

impl fmt::Display for Event {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Event::Started { pid } => write!(f, "Started(pid={})", pid),
            Event::Exited { code, reason } => {
                write!(f, "Exited(code={}, reason={:?})", code, reason)
            }
            Event::StdoutChunk { seq, bytes } => {
                write!(f, "StdoutChunk(seq={}, len={})", seq, bytes.len())
            }
            Event::StderrChunk { seq, bytes } => {
                write!(f, "StderrChunk(seq={}, len={})", seq, bytes.len())
            }
        }
    }
}
