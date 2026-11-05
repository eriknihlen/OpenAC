//! Cooperative cancellation: a flag a long operation checks between steps.

use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};

/// A shared cancellation flag. Clones see the same flag.
#[derive(Debug, Clone, Default)]
pub struct CancelToken(Arc<AtomicBool>);

/// The error a cancelled operation returns.
#[derive(Debug, Clone, Copy, PartialEq, Eq, thiserror::Error)]
#[error("The operation was cancelled.")]
pub struct Cancelled;

impl CancelToken {
    /// A token nobody has cancelled.
    pub fn new() -> Self {
        Self::default()
    }

    /// A token that never cancels.
    pub fn none() -> Self {
        Self::default()
    }

    /// Asks every holder to stop.
    pub fn cancel(&self) {
        self.0.store(true, Ordering::SeqCst);
    }

    /// Whether cancellation was asked for.
    pub fn is_cancelled(&self) -> bool {
        self.0.load(Ordering::SeqCst)
    }

    /// `Err(Cancelled)` once cancellation was asked for.
    pub fn check(&self) -> Result<(), Cancelled> {
        if self.is_cancelled() {
            Err(Cancelled)
        } else {
            Ok(())
        }
    }

    /// A token that is cancelled when this one is, or when cancelled itself
    /// (OpenAC's linked token source).
    pub fn child(&self) -> LinkedToken {
        LinkedToken {
            parent: self.clone(),
            own: Self::new(),
        }
    }
}

/// A token cancelled by its parent or by itself.
#[derive(Debug, Clone)]
pub struct LinkedToken {
    parent: CancelToken,
    own: CancelToken,
}

impl LinkedToken {
    /// Cancels this token only.
    pub fn cancel(&self) {
        self.own.cancel();
    }

    /// Whether this token or its parent was cancelled.
    pub fn is_cancelled(&self) -> bool {
        self.own.is_cancelled() || self.parent.is_cancelled()
    }

    /// `Err(Cancelled)` once either was cancelled.
    pub fn check(&self) -> Result<(), Cancelled> {
        if self.is_cancelled() {
            Err(Cancelled)
        } else {
            Ok(())
        }
    }
}
