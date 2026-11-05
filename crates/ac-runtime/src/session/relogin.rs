//! Logging in again after the server fell silent.
//!
//! A session whose server stopped answering (the transport's silence rule)
//! has its connection ended like any other. What happens next is read at
//! that moment: a session with no window attached logs in again on a fixed
//! interval until one attempt gets back to where its start took it; a
//! session a window shows ends, and the window shows the loss. A failed
//! attempt waits another interval; only an account with no character to
//! enter ends the recovery.

use std::time::{Duration, Instant};

use super::SessionError;

/// How long a session that lost its server waits before each attempt to log
/// in again. It keeps trying at this pace until one attempt gets back in.
pub const LOST_CONNECTION_RETRY_INTERVAL: Duration = Duration::from_secs(30);

/// What a host hears of a session logging in again after its server fell
/// silent with no window attached.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Relogin {
    /// The server was found gone: the connection ended (the plugins heard
    /// the logoff) and the session logs in again `after` from now.
    Scheduled {
        /// The wait before the attempt.
        after: Duration,
    },
    /// An attempt to log in again failed; the next one is `after` from now.
    AttemptFailed {
        /// Why it failed.
        error: String,
        /// The wait before the next attempt.
        after: Duration,
    },
}

/// What a session does once its server fell silent.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) enum AfterLoss {
    /// Log in again after the interval.
    LogInAgain,
    /// End, reporting the loss.
    End,
}

/// What a session does after its server fell silent: it logs in again when
/// no window is attached at that moment, it plays (a probe's whole life is
/// its login) and nothing asked it to end; otherwise it ends.
pub(crate) fn after_loss(enters_world: bool, window_attached: bool, ending: bool) -> AfterLoss {
    if enters_world && !window_attached && !ending {
        AfterLoss::LogInAgain
    } else {
        AfterLoss::End
    }
}

/// Whether a failed attempt to log in again is tried again after the
/// interval: every failure is (the server still gone, not yet taking
/// logins, the account still in use), except an account with no character
/// to enter, which no later attempt changes.
pub(crate) fn retries_after(error: Option<&SessionError>) -> bool {
    !matches!(error, Some(SessionError::NoCharacter(_)))
}

/// One session's recovery from a lost server: whether it is recovering,
/// and when its next attempt is due.
#[derive(Debug, Clone)]
pub(crate) struct Recovery {
    interval: Duration,
    recovering: bool,
    deadline: Option<Instant>,
}

impl Recovery {
    /// No recovery under way; attempts `interval` apart once one starts.
    pub(crate) fn new(interval: Duration) -> Self {
        Self {
            interval,
            recovering: false,
            deadline: None,
        }
    }

    /// The server was found gone at `now`: the first attempt is one
    /// interval out.
    pub(crate) fn begin(&mut self, now: Instant) {
        self.recovering = true;
        self.schedule(now);
    }

    /// True from the moment the server is found gone until an attempt gets
    /// back in.
    pub(crate) fn is_recovering(&self) -> bool {
        self.recovering
    }

    /// When the next attempt is due, while one is scheduled.
    pub(crate) fn deadline(&self) -> Option<Instant> {
        self.deadline
    }

    /// The interval between attempts.
    pub(crate) fn interval(&self) -> Duration {
        self.interval
    }

    /// Whether the scheduled attempt is due at `now`; a due attempt is no
    /// longer scheduled (it is being made).
    pub(crate) fn take_due(&mut self, now: Instant) -> bool {
        match self.deadline {
            Some(deadline) if now >= deadline => {
                self.deadline = None;
                true
            }
            _ => false,
        }
    }

    /// The attempt failed at `now`: the next one waits another interval.
    pub(crate) fn attempt_failed(&mut self, now: Instant) {
        if self.recovering {
            self.schedule(now);
        }
    }

    /// An attempt got back in: the recovery is over.
    pub(crate) fn logged_in(&mut self) {
        self.recovering = false;
        self.deadline = None;
    }

    fn schedule(&mut self, now: Instant) {
        self.deadline = Some(now + self.interval);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // Source: OpenAC `HeadlessProcessSchedulerTests.LostServer_IsRetriedEvery30SecondsUntilALoginGetsBackIn`
    // (`20685205`): the default interval is 30 s; the loss schedules the
    // first attempt 30 s out; each failed attempt sets the next exactly
    // 30 s after it and none is made a tick early; a login that gets back
    // in ends the recovery.
    #[test]
    fn a_lost_server_is_retried_every_30_seconds_until_a_login_gets_back_in() {
        let retry = Duration::from_secs(30);
        assert_eq!(LOST_CONNECTION_RETRY_INTERVAL, retry);
        let mut recovery = Recovery::new(LOST_CONNECTION_RETRY_INTERVAL);
        let start = Instant::now();
        let mut now = start + Duration::from_millis(30);
        assert!(!recovery.is_recovering());
        assert!(
            !recovery.take_due(now),
            "nothing is scheduled before a loss"
        );

        recovery.begin(now);
        assert!(recovery.is_recovering());
        assert_eq!(recovery.deadline(), Some(now + retry));

        let mut attempts = 0;
        for _ in 1..=3 {
            now += retry - Duration::from_nanos(1);
            assert!(!recovery.take_due(now), "never early");
            now += Duration::from_nanos(1);
            assert!(recovery.take_due(now));
            attempts += 1;
            assert_eq!(recovery.deadline(), None, "the attempt is under way");
            recovery.attempt_failed(now);
            assert!(recovery.is_recovering());
            assert_eq!(recovery.deadline(), Some(now + retry));
        }
        assert_eq!(attempts, 3);

        now += retry;
        assert!(recovery.take_due(now));
        recovery.logged_in();
        assert!(!recovery.is_recovering());
        assert_eq!(recovery.deadline(), None);
        assert!(!recovery.take_due(now + retry), "play resumes");
    }

    // The windowless host logs
    // in again (OpenAC `HeadlessSessionHost.BeginLostConnectionRecovery`),
    // the window client ends (it shows the loss and stays there); whether a
    // window is attached is read when the server is found gone.
    #[test]
    fn only_a_playing_session_without_a_window_logs_in_again() {
        assert_eq!(after_loss(true, false, false), AfterLoss::LogInAgain);
        assert_eq!(after_loss(true, true, false), AfterLoss::End, "a window");
        assert_eq!(after_loss(true, false, true), AfterLoss::End, "ending");
        assert_eq!(after_loss(false, false, false), AfterLoss::End, "a probe");
    }

    // OpenAC `HeadlessSessionHost.CompletePendingReconnect`: a failed start
    // is deferred to the next interval; `HeadlessProcessScheduler`: any
    // other outcome (no character to enter) ends the session.
    #[test]
    fn every_failed_attempt_is_retried_but_an_account_with_no_character() {
        assert!(retries_after(None), "a connect that timed out");
        assert!(retries_after(Some(&SessionError::ConnectTimeout(
            "the connect request"
        ))));
        assert!(retries_after(Some(&SessionError::ServerEnded {
            code: 8,
            reason: "in use".into(),
        })));
        assert!(retries_after(Some(&SessionError::CharacterRejected(0x0B))));
        assert!(retries_after(Some(&SessionError::ConnectionLost)));
        assert!(!retries_after(Some(&SessionError::NoCharacter(
            "+Gone".into()
        ))));
    }
}
