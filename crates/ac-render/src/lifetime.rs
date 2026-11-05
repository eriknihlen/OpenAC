//! An orderly end for a window: input stops being accepted first, then the
//! parts are released in a fixed order, each only after the ones that
//! depend on it.

use std::cell::Cell;

/// Once teardown starts, callbacks from the platform (input, focus) are no
/// longer delivered. Every callback of the window host runs on the event
/// loop's thread, so the gate is a plain flag; a callback may close the
/// gate itself.
#[derive(Debug)]
pub struct QuiescenceGate {
    accepting: Cell<bool>,
}

impl Default for QuiescenceGate {
    fn default() -> Self {
        Self::new()
    }
}

impl QuiescenceGate {
    /// An open gate.
    #[must_use]
    pub fn new() -> Self {
        Self {
            accepting: Cell::new(true),
        }
    }

    /// Whether callbacks are still delivered.
    #[must_use]
    pub fn is_accepting(&self) -> bool {
        self.accepting.get()
    }

    /// Stop delivering callbacks, for good.
    pub fn stop_accepting(&self) {
        self.accepting.set(false);
    }

    /// Run `callback` if the gate is open; its result, or `None`.
    pub fn invoke<T>(&self, callback: impl FnOnce() -> T) -> Option<T> {
        self.accepting.get().then(callback)
    }
}

/// A stage of a teardown: releases one thing, or says why it could not.
pub type TeardownStage<'a> = Box<dyn FnMut() -> Result<(), String> + 'a>;

/// Stages run in order; a stage that fails stops the ones after it (they
/// depend on it), and the next [`OrderedTeardown::advance`] retries exactly
/// the failed stage. (A stage cannot call back into `advance`: it would
/// need the teardown it is part of, which the borrow rules refuse.)
pub struct OrderedTeardown<'a> {
    stages: Vec<TeardownStage<'a>>,
    next: usize,
}

impl std::fmt::Debug for OrderedTeardown<'_> {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("OrderedTeardown")
            .field("stages", &self.stages.len())
            .field("next", &self.next)
            .finish()
    }
}

impl<'a> OrderedTeardown<'a> {
    /// A teardown of `stages`, first to last.
    #[must_use]
    pub fn new(stages: Vec<TeardownStage<'a>>) -> Self {
        Self { stages, next: 0 }
    }

    /// Every stage has run.
    #[must_use]
    pub fn is_complete(&self) -> bool {
        self.next == self.stages.len()
    }

    /// The stage that runs next.
    #[must_use]
    pub fn next_stage(&self) -> usize {
        self.next
    }

    /// Run the remaining stages in order until one fails.
    pub fn advance(&mut self) -> Result<(), String> {
        while self.next < self.stages.len() {
            (self.stages[self.next])()?;
            self.next += 1;
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    //! Cases from OpenAC `tests/AcDream.App.Tests/Rendering/OrderedResourceTeardownTests.cs`
    //! and `HostQuiescenceGateTests.cs`.
    use super::*;
    use std::cell::RefCell;

    #[test]
    fn a_failure_stops_dependents_and_a_retry_resumes_at_that_stage() {
        let calls = RefCell::new(Vec::new());
        let attempts = Cell::new(0);
        let mut teardown = OrderedTeardown::new(vec![
            Box::new(|| {
                calls.borrow_mut().push(1);
                Ok(())
            }),
            Box::new(|| {
                calls.borrow_mut().push(2);
                attempts.set(attempts.get() + 1);
                if attempts.get() == 1 {
                    Err("synthetic stage failure".into())
                } else {
                    Ok(())
                }
            }),
            Box::new(|| {
                calls.borrow_mut().push(3);
                Ok(())
            }),
        ]);
        assert!(teardown.advance().is_err());
        assert_eq!(*calls.borrow(), [1, 2]);
        assert_eq!(teardown.next_stage(), 1);
        teardown.advance().unwrap();
        assert_eq!(*calls.borrow(), [1, 2, 2, 3]);
        assert!(teardown.is_complete());
        // Complete: further calls do nothing.
        teardown.advance().unwrap();
        assert_eq!(calls.borrow().len(), 4);
    }

    #[test]
    fn a_callback_can_stop_the_gate_reentrantly() {
        let gate = QuiescenceGate::new();
        let calls = Cell::new(0);
        gate.invoke(|| {
            calls.set(calls.get() + 1);
            gate.stop_accepting();
        });
        assert_eq!(gate.invoke(|| calls.set(calls.get() + 1)), None);
        assert_eq!(calls.get(), 1);
        assert!(!gate.is_accepting());
    }
}
