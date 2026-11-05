//! The status box: short client-side messages (text class `ClientLocal`)
//! shown at the top of the view for a few seconds, newest on top, at most
//! four at a time.

use std::collections::VecDeque;

/// How many messages show at once.
pub const MAX_CONCURRENT_ITEMS: usize = 4;

/// How long a message shows, in seconds.
pub const DEFAULT_LIFETIME_SECONDS: f64 = 5.0;

/// A message on show.
#[derive(Debug, Clone, PartialEq)]
pub struct SpewBoxEntry {
    /// The text.
    pub text: String,
    /// When it goes away, on the caller's clock (seconds).
    pub expires_at_seconds: f64,
}

/// The status box's queue and the messages on show.
#[derive(Debug, Clone, Default)]
pub struct SpewBoxState {
    pending: VecDeque<String>,
    visible: Vec<SpewBoxEntry>,
    revision: u64,
}

impl SpewBoxState {
    /// Nothing queued, nothing showing.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Advances whenever what shows changes.
    #[must_use]
    pub fn revision(&self) -> u64 {
        self.revision
    }

    /// How many messages show.
    #[must_use]
    pub fn len(&self) -> usize {
        self.visible.len()
    }

    /// True when nothing shows.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.visible.is_empty()
    }

    /// How many messages wait for the next [`Self::tick`].
    #[must_use]
    pub fn pending_count(&self) -> usize {
        self.pending.len()
    }

    /// Queues a message; it shows from the next [`Self::tick`]. The queue
    /// never holds more than [`MAX_CONCURRENT_ITEMS`], so a session nothing
    /// ticks does not keep every message of its life. Nothing a tick would
    /// show is lost: a message equal to the one queued just before it would
    /// only replace it with the same expiry, and of the rest a tick keeps
    /// the newest four, the ones kept here.
    pub fn enqueue(&mut self, text: &str) {
        if self.pending.back().is_some_and(|last| last == text) {
            return;
        }
        self.pending.push_back(text.to_owned());
        while self.pending.len() > MAX_CONCURRENT_ITEMS {
            self.pending.pop_front();
        }
    }

    /// Moves queued messages on show (newest first) and drops expired ones.
    /// A message identical to the newest one on show replaces it, with a
    /// fresh lifetime, instead of stacking; beyond four, the oldest go.
    pub fn tick(&mut self, now_seconds: f64) {
        let mut changed = !self.pending.is_empty();
        while let Some(text) = self.pending.pop_front() {
            if self
                .visible
                .first()
                .is_some_and(|newest| newest.text == text)
            {
                self.visible.remove(0);
            }
            self.visible.insert(
                0,
                SpewBoxEntry {
                    text,
                    expires_at_seconds: now_seconds + DEFAULT_LIFETIME_SECONDS,
                },
            );
            self.visible.truncate(MAX_CONCURRENT_ITEMS);
        }
        let before = self.visible.len();
        self.visible
            .retain(|entry| entry.expires_at_seconds > now_seconds);
        changed |= self.visible.len() != before;
        if changed {
            self.revision += 1;
        }
    }

    /// The messages on show, newest first.
    #[must_use]
    pub fn visible(&self) -> &[SpewBoxEntry] {
        &self.visible
    }

    /// Drops everything queued and showing.
    pub fn reset(&mut self) {
        self.pending.clear();
        self.visible.clear();
        self.revision += 1;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn texts(state: &SpewBoxState) -> Vec<&str> {
        state.visible().iter().map(|e| e.text.as_str()).collect()
    }

    // Golden values: OpenAC tests/AcDream.Core.Tests/Chat/SpewBoxStateTests.cs
    #[test]
    fn enqueue_does_not_show_until_tick() {
        let mut state = SpewBoxState::new();
        state.enqueue("You can't jump while in the air");
        assert_eq!(state.len(), 0);
        assert!(state.visible().is_empty());
    }

    #[test]
    fn tick_drains_pending_into_visible() {
        let mut state = SpewBoxState::new();
        state.enqueue("You can't jump while in the air");
        state.tick(0.0);
        assert_eq!(texts(&state), ["You can't jump while in the air"]);
    }

    #[test]
    fn tick_inserts_newest_first() {
        let mut state = SpewBoxState::new();
        state.enqueue("first");
        state.tick(0.0);
        state.enqueue("second");
        state.tick(0.0);
        assert_eq!(texts(&state), ["second", "first"]);
    }

    #[test]
    fn identical_repeat_refreshes_in_place() {
        let mut state = SpewBoxState::new();
        state.enqueue("You are too encumbered to carry that!");
        state.tick(0.0);
        state.enqueue("You are too encumbered to carry that!");
        state.tick(1.0);
        assert_eq!(state.len(), 1);
        assert_eq!(
            state.visible()[0].text,
            "You are too encumbered to carry that!"
        );
        assert_eq!(
            state.visible()[0].expires_at_seconds,
            1.0 + DEFAULT_LIFETIME_SECONDS
        );
    }

    #[test]
    fn different_text_does_not_dedupe() {
        let mut state = SpewBoxState::new();
        state.enqueue("first message");
        state.tick(0.0);
        state.enqueue("second message");
        state.tick(0.0);
        assert_eq!(texts(&state), ["second message", "first message"]);
    }

    #[test]
    fn overflow_drops_the_oldest() {
        let mut state = SpewBoxState::new();
        assert_eq!(MAX_CONCURRENT_ITEMS, 4);
        for i in 0..=MAX_CONCURRENT_ITEMS {
            state.enqueue(&format!("line {i}"));
            state.tick(0.0);
        }
        assert_eq!(texts(&state), ["line 4", "line 3", "line 2", "line 1"]);
    }

    #[test]
    fn tick_prunes_expired_entries() {
        let mut state = SpewBoxState::new();
        state.enqueue("fading message");
        state.tick(0.0);
        assert_eq!(state.len(), 1);
        state.tick(DEFAULT_LIFETIME_SECONDS + 0.001);
        assert!(state.is_empty());
    }

    #[test]
    fn without_a_tick_the_queue_stays_bounded() {
        // Enqueue_WithoutTick_PendingStaysBounded.
        let mut state = SpewBoxState::new();
        for i in 0..10_000 {
            state.enqueue(&format!("line {}", i % 7));
        }
        assert!(state.pending_count() <= MAX_CONCURRENT_ITEMS);
        state.tick(0.0);
        assert_eq!(texts(&state), ["line 3", "line 2", "line 1", "line 0"]);
    }

    #[test]
    fn the_bound_shows_exactly_what_an_unbounded_queue_would() {
        // Enqueue_Bounded_TickShowsExactlyWhatAnUnboundedQueueWould: for any
        // burst between two ticks, from any visible set, the visible lines
        // and their expiries equal an unbounded queue's. (A small LCG stands
        // in for the test's seeded `Random`.)
        let mut seed: u64 = 4891;
        let mut next = |bound: u64| {
            seed = seed
                .wrapping_mul(6_364_136_223_846_793_005)
                .wrapping_add(1_442_695_040_888_963_407);
            (seed >> 33) % bound
        };
        let alphabet = ["a", "b", "c", "d", "e", "f"];
        for _ in 0..2_000 {
            let mut state = SpewBoxState::new();
            let mut model_pending: Vec<&str> = Vec::new();
            let mut model: Vec<SpewBoxEntry> = Vec::new();
            let mut now = 0.0;
            for _ in 0..(1 + next(4)) {
                for _ in 0..next(12) {
                    let text = alphabet[usize::try_from(next(6)).unwrap()];
                    state.enqueue(text);
                    model_pending.push(text);
                }
                now += f64::from(u32::try_from(next(3)).unwrap());
                state.tick(now);
                for text in model_pending.drain(..) {
                    if model.first().is_some_and(|e| e.text == text) {
                        model.remove(0);
                    }
                    model.insert(
                        0,
                        SpewBoxEntry {
                            text: text.to_owned(),
                            expires_at_seconds: now + DEFAULT_LIFETIME_SECONDS,
                        },
                    );
                    model.truncate(MAX_CONCURRENT_ITEMS);
                }
                model.retain(|e| e.expires_at_seconds > now);
                assert_eq!(state.visible(), model.as_slice());
            }
        }
    }

    #[test]
    fn a_quiet_tick_leaves_the_revision_alone() {
        let mut state = SpewBoxState::new();
        state.enqueue("stays visible a while");
        state.tick(0.0);
        let revision = state.revision();
        state.tick(0.5);
        assert_eq!(state.revision(), revision);
    }

    #[test]
    fn reset_clears_pending_and_visible() {
        let mut state = SpewBoxState::new();
        state.enqueue("pending, never ticked");
        state.enqueue("about to be visible");
        state.tick(0.0);
        assert!(!state.is_empty());
        state.reset();
        assert!(state.is_empty());
        state.tick(1.0);
        assert!(state.is_empty());
    }

    #[test]
    fn revision_advances_when_what_shows_changes() {
        let mut state = SpewBoxState::new();
        let initial = state.revision();
        state.enqueue("a line");
        state.tick(0.0);
        assert!(state.revision() > initial);
    }
}
