//! Routing of game events to handlers by event type.
//!
//! [`GameEventDispatcher`] holds at most one live handler per event type.
//! A plain registration replaces whatever was there; an owned registration
//! stacks on top of it and, when retired, gives the type back to the
//! nearest registration below it that is still live. Events with no handler
//! are counted per type.
//!
//! Handlers receive the dispatcher itself, the caller's context `C` (the
//! state they update) and the envelope. Through the dispatcher a handler
//! may retire its own registration or register others while it runs.
//!
//! [`SubscriptionSet`] collects release actions and runs them in reverse
//! order, for wiring that installs several owned registrations and must
//! take them all down together.

use std::collections::BTreeMap;

use crate::{GameEventEnvelope, GameEventType};

/// A game-event handler: gets the dispatcher, the context and the event.
pub type GameEventHandler<C> =
    Box<dyn FnMut(&mut GameEventDispatcher<C>, &mut C, &GameEventEnvelope<'_>)>;

struct Registration<C> {
    id: u64,
    /// `None` while the handler is running.
    handler: Option<GameEventHandler<C>>,
}

/// Proof of one owned registration, consumed by
/// [`GameEventDispatcher::retire`]. Dropping it without retiring leaves the
/// registration in place.
#[derive(Debug, PartialEq, Eq)]
#[must_use = "dropping the token makes the registration permanent"]
pub struct OwnedRegistration {
    event_type: GameEventType,
    id: u64,
}

impl OwnedRegistration {
    /// The event type the registration handles.
    #[must_use]
    pub fn event_type(&self) -> GameEventType {
        self.event_type
    }
}

/// What [`GameEventDispatcher::dispatch`] did with an event.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum DispatchOutcome {
    /// The type's handler ran.
    Handled,
    /// No handler for the type; the event was counted.
    Unhandled,
    /// The type's handler is already running further up the call stack
    /// (it dispatched an event of its own type); the event was dropped.
    HandlerBusy,
}

/// Routes game events to one handler per event type.
pub struct GameEventDispatcher<C> {
    /// Per type, registrations from oldest to newest; the last is live.
    /// A type with no registrations has no entry.
    handlers: BTreeMap<GameEventType, Vec<Registration<C>>>,
    unhandled: BTreeMap<GameEventType, u64>,
    next_id: u64,
}

impl<C> Default for GameEventDispatcher<C> {
    fn default() -> Self {
        Self {
            handlers: BTreeMap::new(),
            unhandled: BTreeMap::new(),
            next_id: 0,
        }
    }
}

impl<C> core::fmt::Debug for GameEventDispatcher<C> {
    fn fmt(&self, f: &mut core::fmt::Formatter<'_>) -> core::fmt::Result {
        f.debug_struct("GameEventDispatcher")
            .field("handled_types", &self.handlers.keys().collect::<Vec<_>>())
            .field("unhandled", &self.unhandled)
            .finish_non_exhaustive()
    }
}

impl<C> GameEventDispatcher<C> {
    /// A dispatcher with no handlers.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    fn registration(&mut self, handler: GameEventHandler<C>) -> Registration<C> {
        let id = self.next_id;
        self.next_id += 1;
        Registration {
            id,
            handler: Some(handler),
        }
    }

    /// Make `handler` the type's handler, dropping every registration the
    /// type had. Owned registrations dropped this way retire as no-ops.
    pub fn register(
        &mut self,
        event_type: GameEventType,
        handler: impl FnMut(&mut Self, &mut C, &GameEventEnvelope<'_>) + 'static,
    ) {
        let registration = self.registration(Box::new(handler));
        self.handlers.insert(event_type, vec![registration]);
    }

    /// Make `handler` the type's handler until the returned token is
    /// retired; then the nearest earlier registration still live takes
    /// over, or the type becomes unhandled.
    pub fn register_owned(
        &mut self,
        event_type: GameEventType,
        handler: impl FnMut(&mut Self, &mut C, &GameEventEnvelope<'_>) + 'static,
    ) -> OwnedRegistration {
        let registration = self.registration(Box::new(handler));
        let id = registration.id;
        self.handlers
            .entry(event_type)
            .or_default()
            .push(registration);
        OwnedRegistration { event_type, id }
    }

    /// Drop every registration for the type; it becomes unhandled.
    pub fn unregister(&mut self, event_type: GameEventType) {
        self.handlers.remove(&event_type);
    }

    /// Retire an owned registration. If it is the live one, the nearest
    /// earlier live registration takes over; if it is buried under a newer
    /// one it is just removed; if a plain registration or
    /// [`GameEventDispatcher::unregister`] already dropped it, nothing
    /// changes.
    pub fn retire(&mut self, registration: OwnedRegistration) {
        let Some(stack) = self.handlers.get_mut(&registration.event_type) else {
            return;
        };
        stack.retain(|r| r.id != registration.id);
        if stack.is_empty() {
            self.handlers.remove(&registration.event_type);
        }
    }

    /// Run the type's live handler, or count the event as unhandled.
    pub fn dispatch(
        &mut self,
        context: &mut C,
        envelope: &GameEventEnvelope<'_>,
    ) -> DispatchOutcome {
        let event_type = envelope.event_type;
        let Some(live) = self
            .handlers
            .get_mut(&event_type)
            .and_then(|s| s.last_mut())
        else {
            let count = self.unhandled.entry(event_type).or_insert(0);
            *count = count.saturating_add(1);
            return DispatchOutcome::Unhandled;
        };
        let id = live.id;
        let Some(mut handler) = live.handler.take() else {
            return DispatchOutcome::HandlerBusy;
        };
        handler(self, context, envelope);
        // Put the handler back unless its registration went away while it
        // ran.
        if let Some(slot) = self
            .handlers
            .get_mut(&event_type)
            .and_then(|s| s.iter_mut().find(|r| r.id == id))
        {
            slot.handler = Some(handler);
        }
        DispatchOutcome::Handled
    }

    /// How many events of the type arrived with no handler.
    #[must_use]
    pub fn unhandled_count(&self, event_type: GameEventType) -> u64 {
        self.unhandled.get(&event_type).copied().unwrap_or(0)
    }

    /// Every type seen without a handler, with its count.
    #[must_use]
    pub fn unhandled_counts(&self) -> &BTreeMap<GameEventType, u64> {
        &self.unhandled
    }

    /// Forget the unhandled counts (after a log-off, say).
    pub fn reset_unhandled_counts(&mut self) {
        self.unhandled.clear();
    }

    /// How many event types have a handler.
    #[must_use]
    pub fn registered_handler_count(&self) -> usize {
        self.handlers.len()
    }
}

/// One release action of a [`SubscriptionSet`].
type Release<T> = Box<dyn FnOnce(&mut T)>;

/// Release actions run together, newest first. Each runs exactly once:
/// [`SubscriptionSet::release`] consumes the set.
#[must_use = "dropping the set without releasing leaves every subscription in place"]
pub struct SubscriptionSet<T> {
    releases: Vec<Release<T>>,
}

impl<T> Default for SubscriptionSet<T> {
    fn default() -> Self {
        Self {
            releases: Vec::new(),
        }
    }
}

impl<T> core::fmt::Debug for SubscriptionSet<T> {
    fn fmt(&self, f: &mut core::fmt::Formatter<'_>) -> core::fmt::Result {
        f.debug_struct("SubscriptionSet")
            .field("len", &self.releases.len())
            .finish()
    }
}

impl<T> SubscriptionSet<T> {
    /// An empty set.
    pub fn new() -> Self {
        Self::default()
    }

    /// Add a release action.
    pub fn add(&mut self, release: impl FnOnce(&mut T) + 'static) {
        self.releases.push(Box::new(release));
    }

    /// Number of release actions held.
    #[must_use]
    pub fn len(&self) -> usize {
        self.releases.len()
    }

    /// True when the set holds nothing.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.releases.is_empty()
    }

    /// Run every release action against `target`, newest first.
    pub fn release(self, target: &mut T) {
        for release in self.releases.into_iter().rev() {
            release(target);
        }
    }
}

impl<C> SubscriptionSet<GameEventDispatcher<C>> {
    /// Add an owned registration; releasing the set retires it.
    pub fn add_registration(&mut self, registration: OwnedRegistration) {
        self.add(move |dispatcher| dispatcher.retire(registration));
    }
}

#[cfg(test)]
mod tests {
    //! Cases ported from OpenAC
    //! `tests/AcDream.Core.Net.Tests/Messages/GameEventDispatcherTests.cs`
    //! (dispatch and registration ownership) and
    //! `tests/AcDream.Core.Net.Tests/SubscriptionSetTests.cs`.
    //!
    //! Not ported, because Rust makes them unrepresentable: disposing a
    //! token twice or from many threads at once (`retire` consumes the
    //! token), releasing a set twice (`release` consumes the set), and
    //! release actions that fail and are retried (release actions return
    //! nothing). A handler that panics is a bug and is not isolated.

    use super::*;

    const TELL: GameEventType = GameEventType::TELL;

    type Calls = Vec<&'static str>;

    fn event(event_type: GameEventType) -> GameEventEnvelope<'static> {
        GameEventEnvelope {
            player_guid: 0x1234_5678,
            sequence: 0,
            event_type,
            payload: &[],
        }
    }

    fn record(
        name: &'static str,
    ) -> impl FnMut(&mut GameEventDispatcher<Calls>, &mut Calls, &GameEventEnvelope<'_>) {
        move |_, calls, _| calls.push(name)
    }

    #[test]
    fn calls_the_handler_for_its_type() {
        let mut d = GameEventDispatcher::<Vec<(GameEventType, usize)>>::new();
        d.register(TELL, |_, seen, e| {
            seen.push((e.event_type, e.payload.len()))
        });
        let payload = [0u8; 4];
        let env = GameEventEnvelope {
            payload: &payload,
            ..event(TELL)
        };
        let mut seen = Vec::new();
        assert_eq!(d.dispatch(&mut seen, &env), DispatchOutcome::Handled);
        assert_eq!(seen, [(TELL, 4)]);
    }

    #[test]
    fn unhandled_events_are_counted() {
        let mut d = GameEventDispatcher::<()>::new();
        let house = GameEventType::HOUSE_PROFILE;
        assert_eq!(
            d.dispatch(&mut (), &event(house)),
            DispatchOutcome::Unhandled
        );
        assert_eq!(
            d.dispatch(&mut (), &event(house)),
            DispatchOutcome::Unhandled
        );
        assert_eq!(d.unhandled_count(house), 2);
        assert_eq!(d.unhandled_count(TELL), 0);
        assert_eq!(d.unhandled_counts().len(), 1);
        d.reset_unhandled_counts();
        assert_eq!(d.unhandled_count(house), 0);
    }

    #[test]
    fn unregister_removes_the_handler_and_moves_to_unhandled() {
        let mut d = GameEventDispatcher::new();
        let mut calls = Calls::new();
        d.register(TELL, record("tell"));
        d.unregister(TELL);
        d.dispatch(&mut calls, &event(TELL));
        assert!(calls.is_empty());
        assert_eq!(d.unhandled_count(TELL), 1);
    }

    #[test]
    fn retiring_an_owned_registration_restores_the_plain_predecessor() {
        let mut d = GameEventDispatcher::new();
        let mut calls = Calls::new();
        d.register(TELL, record("baseline"));
        let owned = d.register_owned(TELL, record("owned"));
        d.dispatch(&mut calls, &event(TELL));
        d.retire(owned);
        d.dispatch(&mut calls, &event(TELL));
        assert_eq!(calls, ["owned", "baseline"]);
        assert_eq!(d.registered_handler_count(), 1);
    }

    #[test]
    fn nested_owners_retired_older_first_skip_the_retired_predecessor() {
        let mut d = GameEventDispatcher::new();
        let mut calls = Calls::new();
        d.register(TELL, record("baseline"));
        let a = d.register_owned(TELL, record("A"));
        let b = d.register_owned(TELL, record("B"));
        d.retire(a);
        d.dispatch(&mut calls, &event(TELL));
        d.retire(b);
        d.dispatch(&mut calls, &event(TELL));
        assert_eq!(calls, ["B", "baseline"]);
    }

    #[test]
    fn nested_owners_retired_newest_first_restore_the_live_older_owner() {
        let mut d = GameEventDispatcher::new();
        let mut calls = Calls::new();
        d.register(TELL, record("baseline"));
        let a = d.register_owned(TELL, record("A"));
        let b = d.register_owned(TELL, record("B"));
        d.retire(b);
        d.dispatch(&mut calls, &event(TELL));
        d.retire(a);
        d.dispatch(&mut calls, &event(TELL));
        assert_eq!(calls, ["A", "baseline"]);
    }

    #[test]
    fn a_later_plain_replacement_survives_retiring_the_owned_token() {
        let mut d = GameEventDispatcher::new();
        let mut calls = Calls::new();
        let owned = d.register_owned(TELL, record("owned"));
        d.register(TELL, record("replacement"));
        d.retire(owned);
        d.dispatch(&mut calls, &event(TELL));
        assert_eq!(calls, ["replacement"]);
    }

    #[test]
    fn owned_registration_without_predecessor_becomes_unhandled_when_retired() {
        let mut d = GameEventDispatcher::new();
        let owned = d.register_owned(TELL, record("owned"));
        assert_eq!(owned.event_type(), TELL);
        d.retire(owned);
        let mut calls = Calls::new();
        d.dispatch(&mut calls, &event(TELL));
        assert_eq!(d.registered_handler_count(), 0);
        assert_eq!(d.unhandled_count(TELL), 1);
        assert!(calls.is_empty());
    }

    #[test]
    fn explicit_unregister_retires_the_chain_without_resurrection() {
        let mut d = GameEventDispatcher::new();
        d.register(TELL, record("baseline"));
        let a = d.register_owned(TELL, record("A"));
        let b = d.register_owned(TELL, record("B"));
        d.unregister(TELL);
        d.retire(a);
        d.retire(b);
        let mut calls = Calls::new();
        d.dispatch(&mut calls, &event(TELL));
        assert_eq!(d.registered_handler_count(), 0);
        assert_eq!(d.unhandled_count(TELL), 1);
        assert!(calls.is_empty());
    }

    #[test]
    fn handler_can_retire_itself_and_install_a_plain_replacement_while_running() {
        let mut d = GameEventDispatcher::<Calls>::new();
        // The handler needs its own token; it is handed over through a
        // shared slot filled right after registering.
        let token: std::rc::Rc<std::cell::Cell<Option<OwnedRegistration>>> = Default::default();
        let slot = std::rc::Rc::clone(&token);
        let owned = d.register_owned(TELL, move |d, calls, _| {
            calls.push("owned");
            if let Some(me) = slot.take() {
                d.retire(me);
            }
            d.register(TELL, record("replacement"));
        });
        token.set(Some(owned));
        let mut calls = Calls::new();
        d.dispatch(&mut calls, &event(TELL));
        d.dispatch(&mut calls, &event(TELL));
        assert_eq!(calls, ["owned", "replacement"]);
        assert_eq!(d.registered_handler_count(), 1);
    }

    #[test]
    fn plain_replacement_after_nested_owners_survives_their_retirement() {
        let mut d = GameEventDispatcher::new();
        let a = d.register_owned(TELL, record("A"));
        let b = d.register_owned(TELL, record("B"));
        d.register(TELL, record("raw"));
        d.retire(a);
        d.retire(b);
        let mut calls = Calls::new();
        d.dispatch(&mut calls, &event(TELL));
        assert_eq!(calls, ["raw"]);
    }

    #[test]
    fn a_handler_dispatching_its_own_type_is_reported_busy() {
        let mut d = GameEventDispatcher::<Vec<DispatchOutcome>>::new();
        d.register(TELL, |d, outcomes, e| {
            let inner = d.dispatch(outcomes, e);
            outcomes.push(inner);
        });
        let mut outcomes = Vec::new();
        let outer = d.dispatch(&mut outcomes, &event(TELL));
        assert_eq!(outcomes, [DispatchOutcome::HandlerBusy]);
        assert_eq!(outer, DispatchOutcome::Handled);
        // The handler is back in place afterwards.
        d.dispatch(&mut outcomes, &event(TELL));
        assert_eq!(outcomes.len(), 2);
        assert_eq!(d.unhandled_count(TELL), 0);
    }

    #[test]
    fn subscription_set_releases_newest_first() {
        let mut set = SubscriptionSet::<Vec<i32>>::new();
        set.add(|order| order.push(1));
        set.add(|order| order.push(2));
        set.add(|order| order.push(3));
        assert_eq!(set.len(), 3);
        let mut order = Vec::new();
        set.release(&mut order);
        assert_eq!(order, [3, 2, 1]);
    }

    #[test]
    fn subscription_set_retires_dispatcher_registrations() {
        let mut d = GameEventDispatcher::new();
        d.register(TELL, record("baseline"));
        let mut set = SubscriptionSet::new();
        assert!(set.is_empty());
        set.add_registration(d.register_owned(TELL, record("A")));
        set.add_registration(d.register_owned(GameEventType::POPUP_STRING, record("popup")));
        set.add_registration(d.register_owned(TELL, record("B")));
        let mut calls = Calls::new();
        d.dispatch(&mut calls, &event(TELL));
        set.release(&mut d);
        d.dispatch(&mut calls, &event(TELL));
        d.dispatch(&mut calls, &event(GameEventType::POPUP_STRING));
        assert_eq!(calls, ["B", "baseline"]);
        assert_eq!(d.registered_handler_count(), 1);
        assert_eq!(d.unhandled_count(GameEventType::POPUP_STRING), 1);
    }
}
