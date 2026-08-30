use crate::AerError;
use std::fmt;

/// States a task can occupy. Transitions are strictly one-directional.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum State {
    Created,
    Running,
    Exited,
}

impl fmt::Display for State {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            State::Created => write!(f, "Created"),
            State::Running => write!(f, "Running"),
            State::Exited => write!(f, "Exited"),
        }
    }
}

/// Enforces legal state transitions. Not part of the public API —
/// callers observe state only through emitted events.
pub(crate) struct StateMachine {
    state: State,
}

impl StateMachine {
    pub(crate) fn new() -> Self {
        Self {
            state: State::Created,
        }
    }

    /// Attempts to move to `next`. Returns an error if the transition is not legal.
    /// Legal edges: Created→Running, Running→Exited. All others are invalid.
    pub(crate) fn transition(&mut self, next: State) -> Result<(), AerError> {
        let legal = matches!(
            (&self.state, &next),
            (State::Created, State::Running) | (State::Running, State::Exited)
        );
        if legal {
            self.state = next;
            Ok(())
        } else {
            Err(AerError::InvalidStateTransition {
                from: self.state.clone(),
                to: next,
            })
        }
    }

    #[allow(dead_code)]
    pub(crate) fn current(&self) -> &State {
        &self.state
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn happy_path_transitions() {
        let mut m = StateMachine::new();
        assert_eq!(m.current(), &State::Created);
        m.transition(State::Running).unwrap();
        assert_eq!(m.current(), &State::Running);
        m.transition(State::Exited).unwrap();
        assert_eq!(m.current(), &State::Exited);
    }

    #[test]
    fn invalid_transition_is_error() {
        let mut m = StateMachine::new();
        assert!(m.transition(State::Exited).is_err());
    }

    #[test]
    fn terminal_state_rejects_all_transitions() {
        let mut m = StateMachine::new();
        m.transition(State::Running).unwrap();
        m.transition(State::Exited).unwrap();
        assert!(m.transition(State::Running).is_err());
        assert!(m.transition(State::Exited).is_err());
    }
}
