mod event;
pub mod ffi;
mod machine;
mod os;
mod task;

pub use event::{Event, ExitReason};
pub use machine::State;
pub use task::{CancelHandle, Task};

use machine::State as MachineState;
use std::fmt;
use std::io;

/// All errors produced by aer-core. Every variant maps to a defined failure mode;
/// no errors are swallowed or converted to strings inside the library.
#[derive(Debug)]
pub enum AerError {
    /// The OS refused to spawn the process.
    SpawnFailed(io::Error),
    /// The OS returned an error while waiting for the process to exit.
    WaitFailed(io::Error),
    /// A state transition was attempted that is not permitted by the machine.
    InvalidStateTransition {
        from: MachineState,
        to: MachineState,
    },
    /// The process was killed because the configured timeout elapsed.
    TimedOut,
    /// The kill attempt itself failed (e.g. the process already exited by the time kill was sent).
    KillFailed(io::Error),
    /// The process was killed by an explicit cancel request.
    Cancelled,
}

impl fmt::Display for AerError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            AerError::SpawnFailed(e) => write!(f, "process spawn failed: {}", e),
            AerError::WaitFailed(e) => write!(f, "process wait failed: {}", e),
            AerError::InvalidStateTransition { from, to } => {
                write!(f, "invalid state transition: {} → {}", from, to)
            }
            AerError::TimedOut => write!(f, "process timed out and was killed"),
            AerError::KillFailed(e) => write!(f, "kill attempt failed: {}", e),
            AerError::Cancelled => write!(f, "process was cancelled"),
        }
    }
}

impl std::error::Error for AerError {
    fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
        match self {
            AerError::SpawnFailed(e) | AerError::WaitFailed(e) | AerError::KillFailed(e) => Some(e),
            AerError::InvalidStateTransition { .. } | AerError::TimedOut | AerError::Cancelled => {
                None
            }
        }
    }
}
