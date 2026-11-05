//! What the open window knows that a crash report wants: the GPU, the
//! window size and where the player is. The window refreshes it twice a
//! second; the process's crash handler reads it.

use std::sync::{Arc, Mutex, PoisonError};

/// The facts at one moment.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct PresentationFacts {
    /// The adapter's name.
    pub device_name: String,
    /// The graphics API.
    pub backend: String,
    /// The driver's name and version.
    pub driver: String,
    /// Drawable width, pixels.
    pub width: u32,
    /// Drawable height, pixels.
    pub height: u32,
    /// The player's cell, when known.
    pub cell_id: Option<u32>,
    /// The player's position in that landblock, meters.
    pub position: Option<[f32; 3]>,
    /// The active camera.
    pub camera: String,
}

/// A handle shared between the window host and whoever reads the facts.
/// Two handles are equal when they share the same facts.
#[derive(Debug, Clone, Default)]
pub struct SharedFacts(Arc<Mutex<Option<PresentationFacts>>>);

impl PartialEq for SharedFacts {
    fn eq(&self, other: &Self) -> bool {
        Arc::ptr_eq(&self.0, &other.0)
    }
}

impl SharedFacts {
    /// Replace the facts (`None`: no window).
    pub fn set(&self, facts: Option<PresentationFacts>) {
        *self.0.lock().unwrap_or_else(PoisonError::into_inner) = facts;
    }

    /// The latest facts. Never blocks for long: the window holds the lock
    /// only to copy into it. A crash in the middle of an update still reads
    /// the last complete facts.
    #[must_use]
    pub fn get(&self) -> Option<PresentationFacts> {
        match self.0.try_lock() {
            Ok(guard) => guard.clone(),
            Err(std::sync::TryLockError::Poisoned(p)) => p.into_inner().clone(),
            Err(std::sync::TryLockError::WouldBlock) => None,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn handles_share_one_set_of_facts() {
        let a = SharedFacts::default();
        let b = a.clone();
        assert_eq!(a, b);
        assert_ne!(a, SharedFacts::default());
        assert_eq!(b.get(), None);
        a.set(Some(PresentationFacts {
            device_name: "GPU".into(),
            width: 1280,
            ..PresentationFacts::default()
        }));
        assert_eq!(b.get().map(|f| f.width), Some(1280));
        a.set(None);
        assert_eq!(b.get(), None);
    }
}
