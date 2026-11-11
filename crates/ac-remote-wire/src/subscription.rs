//! What a remote window asks its endpoint to send.

use ac_runtime::session::PresentationEvent;

/// Which objects a remote window wants (the endpoint filters by range; this
/// is only the request).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum Objects {
    /// None.
    None,
    /// Those within radar range.
    #[default]
    Radar,
    /// Every one the session knows.
    All,
}

/// What a remote window asks for: the kinds of event it can do without
/// (its world may come from elsewhere) and which objects.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Subscription {
    /// The motion states objects take up ([`PresentationEvent::Motions`]).
    pub motions: bool,
    /// Sounds ([`PresentationEvent::Sound`]).
    pub sound: bool,
    /// Scripted effects ([`PresentationEvent::PlayScript`],
    /// [`PresentationEvent::PlayScriptType`],
    /// [`PresentationEvent::DestroyParticles`]).
    pub effects: bool,
    /// What navigation draws ([`PresentationEvent::Navigation`]).
    pub navigation: bool,
    /// Which objects.
    pub objects: Objects,
}

impl Default for Subscription {
    /// A window whose world is drawn from elsewhere: no motions, sounds,
    /// effects or navigation; the objects within radar range.
    fn default() -> Self {
        Self {
            motions: false,
            sound: false,
            effects: false,
            navigation: false,
            objects: Objects::Radar,
        }
    }
}

impl Subscription {
    /// Whether the window wants `event`, by its kind alone (objects are
    /// filtered by range elsewhere: every other kind is wanted).
    #[must_use]
    pub fn wanted(&self, event: &PresentationEvent) -> bool {
        match event {
            PresentationEvent::Motions(_) => self.motions,
            PresentationEvent::Sound(_) => self.sound,
            PresentationEvent::PlayScript(_)
            | PresentationEvent::PlayScriptType(_)
            | PresentationEvent::DestroyParticles(_) => self.effects,
            PresentationEvent::Navigation(_) => self.navigation,
            // Listed, not a wildcard: a new kind of event must be placed.
            PresentationEvent::Snapshot(_)
            | PresentationEvent::Deltas(_)
            | PresentationEvent::GameTime(_)
            | PresentationEvent::AdminEnvirons(_)
            | PresentationEvent::Chat(_)
            | PresentationEvent::Player(_)
            | PresentationEvent::Interface(_)
            | PresentationEvent::PortalSpace(_)
            | PresentationEvent::PreGame(_)
            | PresentationEvent::StatusText(_)
            | PresentationEvent::WindowRequest(_)
            | PresentationEvent::Link(_)
            | PresentationEvent::SessionClock(_) => true,
        }
    }
}
