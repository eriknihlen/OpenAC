//! The attach/detach state machine of the window host.
//!
//! Pure logic: it decides what the host does (open a window, close it, stop the event loop) from
//! what happened (a command from another thread, the user closing the window, a GPU failure). The
//! host performs the actions; this module never touches a window or a GPU, so it is tested on its
//! own.

/// Whether a window is attached.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AttachState {
    /// No window. The event loop keeps running and waits for commands.
    Detached,
    /// A window is open and presenting frames.
    Attached,
    /// Shutdown was requested; the event loop is stopping and every input is ignored.
    Exited,
}

/// Something that happened, fed to [`AttachMachine::step`].
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AttachInput {
    /// The owner asked for a window.
    AttachRequested,
    /// The owner asked to close the window.
    DetachRequested,
    /// The user closed the window (title bar close button, Alt+F4, ...).
    WindowClosedByUser,
    /// Opening the window or its GPU surface failed; nothing is open.
    AttachFailed,
    /// Presenting a frame failed in a way the window cannot recover from.
    RenderFailed,
    /// The owner asked the host to stop.
    ShutdownRequested,
}

/// Why a window closed.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum DetachReason {
    /// The owner asked for it.
    Requested,
    /// The user closed the window.
    ClosedByUser,
    /// The host is shutting down.
    Shutdown,
    /// Rendering failed and the window was dropped.
    RenderFailed,
}

/// What the host must do next.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AttachAction {
    /// Create the window and its GPU surface.
    OpenWindow,
    /// Drop the window and everything built for it.
    CloseWindow(DetachReason),
    /// An attach was asked for while a window is already open; tell the owner it was ignored.
    RejectAttach,
    /// Stop the event loop.
    ExitLoop,
}

/// The state machine. Starts [`AttachState::Detached`].
#[derive(Debug, Clone)]
pub struct AttachMachine {
    state: AttachState,
}

impl Default for AttachMachine {
    fn default() -> Self {
        Self::new()
    }
}

impl AttachMachine {
    /// A machine with no window.
    pub fn new() -> Self {
        Self {
            state: AttachState::Detached,
        }
    }

    /// The current state.
    pub fn state(&self) -> AttachState {
        self.state
    }

    /// Applies one input and returns the actions to perform, in order.
    pub fn step(&mut self, input: AttachInput) -> Vec<AttachAction> {
        use AttachAction as A;
        use AttachInput as I;
        use AttachState as S;

        let (next, actions) = match (self.state, input) {
            (S::Exited, _) => (S::Exited, vec![]),

            (S::Detached, I::AttachRequested) => (S::Attached, vec![A::OpenWindow]),
            // Nothing is open, so there is nothing to close; a late close event from a window
            // that is already gone lands here too.
            (
                S::Detached,
                I::DetachRequested | I::WindowClosedByUser | I::AttachFailed | I::RenderFailed,
            ) => (S::Detached, vec![]),
            (S::Detached, I::ShutdownRequested) => (S::Exited, vec![A::ExitLoop]),

            (S::Attached, I::AttachRequested) => (S::Attached, vec![A::RejectAttach]),
            (S::Attached, I::DetachRequested) => {
                (S::Detached, vec![A::CloseWindow(DetachReason::Requested)])
            }
            (S::Attached, I::WindowClosedByUser) => (
                S::Detached,
                vec![A::CloseWindow(DetachReason::ClosedByUser)],
            ),
            // The open failed, so no window exists to close.
            (S::Attached, I::AttachFailed) => (S::Detached, vec![]),
            (S::Attached, I::RenderFailed) => (
                S::Detached,
                vec![A::CloseWindow(DetachReason::RenderFailed)],
            ),
            (S::Attached, I::ShutdownRequested) => (
                S::Exited,
                vec![A::CloseWindow(DetachReason::Shutdown), A::ExitLoop],
            ),
        };
        self.state = next;
        actions
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use AttachAction as A;
    use AttachInput as I;

    #[test]
    fn starts_detached() {
        assert_eq!(AttachMachine::new().state(), AttachState::Detached);
    }

    #[test]
    fn attach_then_detach_then_attach_again() {
        let mut m = AttachMachine::new();
        assert_eq!(m.step(I::AttachRequested), [A::OpenWindow]);
        assert_eq!(m.state(), AttachState::Attached);
        assert_eq!(
            m.step(I::DetachRequested),
            [A::CloseWindow(DetachReason::Requested)]
        );
        assert_eq!(m.state(), AttachState::Detached);
        assert_eq!(m.step(I::AttachRequested), [A::OpenWindow]);
        assert_eq!(m.state(), AttachState::Attached);
    }

    #[test]
    fn user_close_detaches_but_keeps_the_loop_running() {
        let mut m = AttachMachine::new();
        m.step(I::AttachRequested);
        assert_eq!(
            m.step(I::WindowClosedByUser),
            [A::CloseWindow(DetachReason::ClosedByUser)]
        );
        assert_eq!(m.state(), AttachState::Detached);
        // The owner's later detach finds nothing to do.
        assert_eq!(m.step(I::DetachRequested), []);
        // And a new window can still be opened.
        assert_eq!(m.step(I::AttachRequested), [A::OpenWindow]);
    }

    #[test]
    fn second_attach_while_attached_is_rejected() {
        let mut m = AttachMachine::new();
        m.step(I::AttachRequested);
        assert_eq!(m.step(I::AttachRequested), [A::RejectAttach]);
        assert_eq!(m.state(), AttachState::Attached);
    }

    #[test]
    fn failed_attach_returns_to_detached_without_closing() {
        let mut m = AttachMachine::new();
        m.step(I::AttachRequested);
        assert_eq!(m.step(I::AttachFailed), []);
        assert_eq!(m.state(), AttachState::Detached);
        assert_eq!(m.step(I::AttachRequested), [A::OpenWindow]);
    }

    #[test]
    fn render_failure_closes_the_window() {
        let mut m = AttachMachine::new();
        m.step(I::AttachRequested);
        assert_eq!(
            m.step(I::RenderFailed),
            [A::CloseWindow(DetachReason::RenderFailed)]
        );
        assert_eq!(m.state(), AttachState::Detached);
    }

    #[test]
    fn stray_inputs_while_detached_do_nothing() {
        let mut m = AttachMachine::new();
        for input in [
            I::DetachRequested,
            I::WindowClosedByUser,
            I::AttachFailed,
            I::RenderFailed,
        ] {
            assert_eq!(m.step(input), [], "{input:?}");
            assert_eq!(m.state(), AttachState::Detached);
        }
    }

    #[test]
    fn shutdown_while_detached_exits() {
        let mut m = AttachMachine::new();
        assert_eq!(m.step(I::ShutdownRequested), [A::ExitLoop]);
        assert_eq!(m.state(), AttachState::Exited);
    }

    #[test]
    fn shutdown_while_attached_closes_then_exits() {
        let mut m = AttachMachine::new();
        m.step(I::AttachRequested);
        assert_eq!(
            m.step(I::ShutdownRequested),
            [A::CloseWindow(DetachReason::Shutdown), A::ExitLoop]
        );
        assert_eq!(m.state(), AttachState::Exited);
    }

    #[test]
    fn everything_is_ignored_after_exit() {
        let mut m = AttachMachine::new();
        m.step(I::ShutdownRequested);
        for input in [
            I::AttachRequested,
            I::DetachRequested,
            I::WindowClosedByUser,
            I::AttachFailed,
            I::RenderFailed,
            I::ShutdownRequested,
        ] {
            assert_eq!(m.step(input), [], "{input:?}");
            assert_eq!(m.state(), AttachState::Exited);
        }
    }
}
