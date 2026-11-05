//! Waiting on a blocking call (name resolution, a TCP connect) without
//! being stuck in it: the call runs on its own thread while the caller
//! watches its deadline and its cancellation.

use std::sync::mpsc::{self, RecvTimeoutError};
use std::time::{Duration, Instant};

use ac_launcher_core::cancel::CancelToken;

/// How often a waiting caller looks at its cancellation and deadline.
pub(crate) const POLL: Duration = Duration::from_millis(25);

/// Why a bounded wait gave up.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) enum Interrupted {
    /// The caller cancelled.
    Cancelled,
    /// The deadline passed.
    TimedOut,
}

/// Where a wait stands against its cancellation and deadline.
pub(crate) fn interrupted(cancel: &CancelToken, deadline: Option<Instant>) -> Option<Interrupted> {
    if cancel.is_cancelled() {
        Some(Interrupted::Cancelled)
    } else if deadline.is_some_and(|deadline| Instant::now() >= deadline) {
        Some(Interrupted::TimedOut)
    } else {
        None
    }
}

/// Runs `job` on a new thread and returns its result, unless the caller
/// cancels or the deadline passes first; the job is then left to finish on
/// its own and its result is dropped. `Err(None)` when the thread could not
/// be started or died.
pub(crate) fn run<T: Send + 'static>(
    name: &str,
    job: impl FnOnce() -> T + Send + 'static,
    deadline: Option<Instant>,
    cancel: &CancelToken,
) -> Result<T, Option<Interrupted>> {
    if let Some(reason) = interrupted(cancel, deadline) {
        return Err(Some(reason));
    }
    let (sender, receiver) = mpsc::channel();
    std::thread::Builder::new()
        .name(name.to_owned())
        .spawn(move || {
            // The receiver is gone when the caller gave up; nothing to do.
            let _ = sender.send(job());
        })
        .map_err(|_| None)?;
    loop {
        match receiver.recv_timeout(POLL) {
            Ok(value) => return Ok(value),
            Err(RecvTimeoutError::Disconnected) => return Err(None),
            Err(RecvTimeoutError::Timeout) => {
                if let Some(reason) = interrupted(cancel, deadline) {
                    return Err(Some(reason));
                }
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn returns_the_result_of_a_quick_job() {
        assert_eq!(run("t", || 7, None, &CancelToken::new()), Ok(7));
    }

    #[test]
    fn gives_up_at_the_deadline_and_on_cancellation() {
        let started = Instant::now();
        let slow = || std::thread::sleep(Duration::from_secs(2));
        let deadline = Some(Instant::now() + Duration::from_millis(100));
        assert_eq!(
            run("t", slow, deadline, &CancelToken::new()),
            Err(Some(Interrupted::TimedOut))
        );
        assert!(started.elapsed() < Duration::from_secs(1));

        let cancel = CancelToken::new();
        cancel.cancel();
        assert_eq!(
            run("t", slow, None, &cancel),
            Err(Some(Interrupted::Cancelled))
        );
    }
}
