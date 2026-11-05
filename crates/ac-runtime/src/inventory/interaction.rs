//! The pacing of uses and the one appraisal slot.
//!
//! Uses are throttled to one every 200 ms. Appraisals are asked one at a
//! time: the slot holds the object whose description is awaited; an answer
//! frees it, and a wait past ten asking turns (4990 ms) is given up so one
//! lost answer cannot silence every later question.

use ac_core::items::{InventoryTransactionState, TransactionEvent};

/// Minimum time between two uses, milliseconds.
pub const USE_THROTTLE_MS: i64 = 200;
/// The pace appraisals are asked at, milliseconds.
pub const APPRAISAL_REQUEST_INTERVAL_MS: i64 = 499;
/// How long an appraisal may hold the slot, milliseconds.
pub const APPRAISAL_REQUEST_TIMEOUT_MS: i64 = 10 * APPRAISAL_REQUEST_INTERVAL_MS;

/// Who asked for an appraisal.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum AppraisalOrigin {
    /// The player.
    #[default]
    User,
    /// Something acting for the player (a script); it never displaces the
    /// player's own question.
    Automation,
}

/// How an appraisal answer was taken.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct AppraisalAcceptance {
    /// The answer is about an object the slot asked for or shows.
    pub accepted: bool,
    /// It answers the awaited question (not a refresh of the shown one).
    pub first_response: bool,
    /// Who asked.
    pub origin: AppraisalOrigin,
    /// The examination window takes it: the player's own answer, or any
    /// answer about the object the window already shows (an automation
    /// answer about anything else only updates the object).
    pub present_in_ui: bool,
}

/// The use pacing and appraisal slot.
#[derive(Debug)]
pub struct Interaction {
    last_use_ms: i64,
    last_use_source: u32,
    last_use_target: u32,
    awaiting_use_completion: bool,
    awaiting_appraisal: u32,
    awaiting_origin: AppraisalOrigin,
    awaiting_since_ms: i64,
    current_appraisal: u32,
    last_completed_appraisal: u32,
    last_abandoned_appraisal: u32,
    appraisal_revision: u64,
}

impl Default for Interaction {
    fn default() -> Self {
        Self {
            last_use_ms: i64::MIN / 2,
            last_use_source: 0,
            last_use_target: 0,
            awaiting_use_completion: false,
            awaiting_appraisal: 0,
            awaiting_origin: AppraisalOrigin::User,
            awaiting_since_ms: 0,
            current_appraisal: 0,
            last_completed_appraisal: 0,
            last_abandoned_appraisal: 0,
            appraisal_revision: 0,
        }
    }
}

impl Interaction {
    /// Nothing used or asked.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Whether a use at `now_ms` would pass the 200 ms pacing (nothing is
    /// spent).
    #[must_use]
    pub fn is_use_throttle_ready(&self, now_ms: i64) -> bool {
        now_ms.saturating_sub(self.last_use_ms) >= USE_THROTTLE_MS
    }

    /// Spend the use stamp at `now_ms`; false while the last use is less
    /// than 200 ms ago.
    pub fn try_consume_use_throttle(&mut self, now_ms: i64) -> bool {
        if now_ms.saturating_sub(self.last_use_ms) < USE_THROTTLE_MS {
            return false;
        }
        self.last_use_ms = now_ms;
        true
    }

    /// A use (with or without a target) went out; its completion is
    /// awaited.
    pub fn note_use_dispatched(&mut self, source: u32, target: u32) {
        self.last_use_source = source;
        self.last_use_target = target;
        self.awaiting_use_completion = true;
    }

    /// The server finished a use: the busy count drops; returns the source
    /// and target of the use this awaited, if it awaited one. With only
    /// casts counted busy the answer is a cast's, and the awaited use
    /// waits on; with nothing counted, it is the use's.
    pub fn complete_use(
        &mut self,
        transactions: &mut InventoryTransactionState,
        events: &mut Vec<TransactionEvent>,
    ) -> Option<(u32, u32)> {
        let item_action_outstanding = transactions.item_busy_count() > 0;
        let no_counted_action = transactions.busy_count() == 0;
        transactions.complete_use(events);
        if !(item_action_outstanding || no_counted_action) || !self.awaiting_use_completion {
            return None;
        }
        self.awaiting_use_completion = false;
        Some((self.last_use_source, self.last_use_target))
    }

    /// The object whose description is awaited (0 for none).
    #[must_use]
    pub fn awaiting_appraisal(&self) -> u32 {
        self.awaiting_appraisal
    }

    /// The object the examination shows (0 for none).
    #[must_use]
    pub fn current_appraisal(&self) -> u32 {
        self.current_appraisal
    }

    /// The object of the last answered question (cleared by a new question
    /// about the same object).
    #[must_use]
    pub fn last_completed_appraisal(&self) -> u32 {
        self.last_completed_appraisal
    }

    /// The object whose question was last given up on.
    #[must_use]
    pub fn last_abandoned_appraisal(&self) -> u32 {
        self.last_abandoned_appraisal
    }

    /// Whether the awaited question has waited longer than the limit.
    #[must_use]
    pub fn is_awaiting_expired(&self, now_ms: i64) -> bool {
        self.awaiting_appraisal != 0
            && now_ms.saturating_sub(self.awaiting_since_ms) >= APPRAISAL_REQUEST_TIMEOUT_MS
    }

    /// Whether another question may be asked now.
    #[must_use]
    pub fn can_begin_appraisal(&self, now_ms: i64) -> bool {
        self.awaiting_appraisal == 0 || self.is_awaiting_expired(now_ms)
    }

    /// Ask about `object_id` through `send`. A question that waited too long
    /// is given up first; an automation question never displaces the
    /// player's. The first question in the slot takes an appraisal count.
    pub fn try_request_appraisal(
        &mut self,
        object_id: u32,
        origin: AppraisalOrigin,
        now_ms: i64,
        transactions: &mut InventoryTransactionState,
        events: &mut Vec<TransactionEvent>,
        send: impl FnOnce(u32),
    ) -> bool {
        if object_id == 0 {
            return false;
        }
        self.try_expire_awaiting(now_ms, transactions, events);
        if origin == AppraisalOrigin::Automation
            && self.awaiting_appraisal != 0
            && self.awaiting_origin == AppraisalOrigin::User
        {
            return false;
        }
        if object_id == self.last_completed_appraisal {
            self.last_completed_appraisal = 0;
        }
        if object_id == self.last_abandoned_appraisal {
            self.last_abandoned_appraisal = 0;
        }
        if self.awaiting_appraisal == 0 {
            transactions.increment_appraisal_count(events);
        }
        self.awaiting_appraisal = object_id;
        self.awaiting_origin = origin;
        self.awaiting_since_ms = now_ms;
        self.appraisal_revision += 1;
        send(object_id);
        true
    }

    /// Counts changes to the appraisal slot (a question asked, answered or
    /// given up on).
    #[must_use]
    pub fn appraisal_revision(&self) -> u64 {
        self.appraisal_revision
    }

    /// An answer about `object_id`. One about the awaited object completes
    /// the question (and, for the player's own question or the object
    /// already shown, becomes the shown one); one about the shown object is
    /// a refresh.
    pub fn accept_appraisal_response(
        &mut self,
        object_id: u32,
        transactions: &mut InventoryTransactionState,
        events: &mut Vec<TransactionEvent>,
    ) -> AppraisalAcceptance {
        if object_id == 0
            || (object_id != self.awaiting_appraisal && object_id != self.current_appraisal)
        {
            return AppraisalAcceptance::default();
        }
        let first_response = object_id == self.awaiting_appraisal;
        let origin = if first_response {
            self.awaiting_origin
        } else {
            AppraisalOrigin::User
        };
        let retargets = first_response
            && (origin == AppraisalOrigin::User || object_id == self.current_appraisal);
        if first_response {
            self.awaiting_appraisal = 0;
            self.awaiting_origin = AppraisalOrigin::User;
            self.last_completed_appraisal = object_id;
            if retargets {
                self.current_appraisal = object_id;
            }
            self.appraisal_revision += 1;
            transactions.complete_appraisal(events);
        }
        AppraisalAcceptance {
            accepted: true,
            first_response,
            origin,
            present_in_ui: !first_response || retargets,
        }
    }

    /// Give up on the awaited question once it waited too long; true when
    /// one was given up.
    pub fn try_expire_awaiting(
        &mut self,
        now_ms: i64,
        transactions: &mut InventoryTransactionState,
        events: &mut Vec<TransactionEvent>,
    ) -> bool {
        if !self.is_awaiting_expired(now_ms) {
            return false;
        }
        self.abandon_awaiting(transactions, events)
    }

    /// Give up on the awaited question now; false when none is awaited.
    pub fn abandon_awaiting(
        &mut self,
        transactions: &mut InventoryTransactionState,
        events: &mut Vec<TransactionEvent>,
    ) -> bool {
        if self.awaiting_appraisal == 0 {
            return false;
        }
        self.last_abandoned_appraisal = self.awaiting_appraisal;
        self.awaiting_appraisal = 0;
        self.awaiting_origin = AppraisalOrigin::User;
        self.awaiting_since_ms = 0;
        self.appraisal_revision += 1;
        transactions.complete_appraisal(events);
        true
    }

    /// The last use that went out: (source, target, its completion is
    /// awaited).
    #[must_use]
    pub fn last_use(&self) -> (u32, u32, bool) {
        (
            self.last_use_source,
            self.last_use_target,
            self.awaiting_use_completion,
        )
    }

    /// Session end: nothing used or asked.
    pub fn reset_session(&mut self) {
        *self = Self::default();
    }
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC
    //! `tests/AcDream.Runtime.Tests/Gameplay/RuntimeAppraisalChannelExpiryTests.cs`,
    //! `RuntimeAppraisalChannelClockTests.cs` and the throttle and appraisal
    //! cases of `RuntimeInteractionTransactionStateTests.cs`.

    use super::*;

    struct Fixture {
        interaction: Interaction,
        transactions: InventoryTransactionState,
        events: Vec<TransactionEvent>,
        sent: Vec<u32>,
    }

    impl Fixture {
        fn new() -> Self {
            Self {
                interaction: Interaction::new(),
                transactions: InventoryTransactionState::new(),
                events: Vec::new(),
                sent: Vec::new(),
            }
        }

        fn ask(&mut self, object: u32, origin: AppraisalOrigin, now_ms: i64) -> bool {
            let sent = &mut self.sent;
            self.interaction.try_request_appraisal(
                object,
                origin,
                now_ms,
                &mut self.transactions,
                &mut self.events,
                |id| sent.push(id),
            )
        }

        fn answer(&mut self, object: u32) -> AppraisalAcceptance {
            self.interaction.accept_appraisal_response(
                object,
                &mut self.transactions,
                &mut self.events,
            )
        }
    }

    #[test]
    fn uses_are_paced_200_ms_apart() {
        let mut interaction = Interaction::new();
        assert!(interaction.try_consume_use_throttle(1_000));
        assert!(!interaction.try_consume_use_throttle(1_199));
        assert!(interaction.try_consume_use_throttle(1_200));
    }

    #[test]
    fn one_question_at_a_time_and_its_answer_frees_the_slot() {
        let mut f = Fixture::new();
        assert!(f.ask(0x10, AppraisalOrigin::User, 0));
        assert_eq!(f.transactions.appraisal_count(), 1);
        assert!(!f.interaction.can_begin_appraisal(100));
        let acceptance = f.answer(0x10);
        assert!(acceptance.accepted && acceptance.first_response);
        assert_eq!(f.transactions.appraisal_count(), 0);
        assert_eq!(f.interaction.current_appraisal(), 0x10);
        assert_eq!(f.interaction.last_completed_appraisal(), 0x10);
        assert!(f.interaction.can_begin_appraisal(100));
        // A refresh of the shown object is accepted but is not a first answer.
        let refresh = f.answer(0x10);
        assert!(refresh.accepted && !refresh.first_response);
        assert_eq!(f.sent, [0x10]);
    }

    #[test]
    fn an_unrelated_answer_is_not_taken() {
        let mut f = Fixture::new();
        f.ask(0x10, AppraisalOrigin::User, 0);
        assert!(!f.answer(0x11).accepted);
        assert_eq!(f.interaction.awaiting_appraisal(), 0x10);
    }

    #[test]
    fn automation_never_displaces_the_players_question() {
        let mut f = Fixture::new();
        f.ask(0x10, AppraisalOrigin::User, 0);
        assert!(!f.ask(0x11, AppraisalOrigin::Automation, 10));
        assert_eq!(f.interaction.awaiting_appraisal(), 0x10);
    }

    #[test]
    fn an_automation_answer_does_not_retarget_the_shown_object() {
        let mut f = Fixture::new();
        f.ask(0x10, AppraisalOrigin::User, 0);
        f.answer(0x10);
        f.ask(0x20, AppraisalOrigin::Automation, 10);
        let acceptance = f.answer(0x20);
        assert_eq!(acceptance.origin, AppraisalOrigin::Automation);
        assert!(!acceptance.present_in_ui);
        assert_eq!(f.interaction.current_appraisal(), 0x10);
        assert_eq!(f.interaction.last_completed_appraisal(), 0x20);
        // An automation answer about the shown object refreshes the window.
        f.ask(0x10, AppraisalOrigin::Automation, 20);
        let same = f.answer(0x10);
        assert!(same.first_response && same.present_in_ui);
        assert_eq!(same.origin, AppraisalOrigin::Automation);
    }

    /// OpenAC `RuntimeInteractionTransactionState.AcceptAppraisalResponse`'s
    /// `PresentInUi`: the player's answer and a refresh of the shown object
    /// reach the examination window.
    #[test]
    fn the_players_answer_and_a_refresh_are_presented() {
        let mut f = Fixture::new();
        f.ask(0x10, AppraisalOrigin::User, 0);
        assert!(f.answer(0x10).present_in_ui);
        assert!(f.answer(0x10).present_in_ui);
    }

    #[test]
    fn a_lost_answer_is_given_up_after_ten_turns() {
        // RuntimeAppraisalChannelExpiryTests: the slot frees itself at
        // 4990 ms so the next question goes out.
        let mut f = Fixture::new();
        f.ask(0x10, AppraisalOrigin::User, 1_000);
        assert!(
            !f.interaction
                .can_begin_appraisal(1_000 + APPRAISAL_REQUEST_TIMEOUT_MS - 1)
        );
        assert!(
            f.interaction
                .can_begin_appraisal(1_000 + APPRAISAL_REQUEST_TIMEOUT_MS)
        );
        assert!(f.ask(
            0x11,
            AppraisalOrigin::User,
            1_000 + APPRAISAL_REQUEST_TIMEOUT_MS
        ));
        assert_eq!(f.interaction.last_abandoned_appraisal(), 0x10);
        assert_eq!(f.interaction.awaiting_appraisal(), 0x11);
        // The given-up question released its count; the new one holds one.
        assert_eq!(f.transactions.appraisal_count(), 1);
    }

    #[test]
    fn a_completed_use_reports_what_it_awaited_once() {
        let mut interaction = Interaction::new();
        let mut transactions = InventoryTransactionState::new();
        let mut events = Vec::new();
        transactions.increment_busy_count(&mut events);
        interaction.note_use_dispatched(0x10, 0x20);
        assert_eq!(
            interaction.complete_use(&mut transactions, &mut events),
            Some((0x10, 0x20))
        );
        assert_eq!(transactions.busy_count(), 0);
        assert_eq!(
            interaction.complete_use(&mut transactions, &mut events),
            None
        );
    }

    // OpenAC `RuntimeInteractionTransactionStateTests.AStaleCastCursorReferenceDoesNotHoldAnItemUse`.
    #[test]
    fn a_stale_cast_cursor_reference_does_not_hold_an_item_use() {
        let mut interaction = Interaction::new();
        let mut transactions = InventoryTransactionState::new();
        let mut events = Vec::new();
        transactions.increment_cast_busy_count(&mut events);
        assert!(transactions.can_begin_request());

        transactions.increment_busy_count(&mut events);
        interaction.note_use_dispatched(0x10, 0x20);
        assert_eq!(transactions.item_busy_count(), 1);
        assert_eq!(
            interaction.complete_use(&mut transactions, &mut events),
            Some((0x10, 0x20))
        );
        assert_eq!(transactions.item_busy_count(), 0);
        assert_eq!(transactions.busy_count(), 1);

        assert_eq!(
            interaction.complete_use(&mut transactions, &mut events),
            None
        );
        assert_eq!(transactions.busy_count(), 0);
    }

    // OpenAC `RuntimeInteractionTransactionStateTests.UncountedItemUseStillPublishesItsReceiptWhenNoOtherActionIsPending`.
    #[test]
    fn an_uncounted_item_use_still_gets_its_receipt_with_nothing_else_pending() {
        let mut interaction = Interaction::new();
        let mut transactions = InventoryTransactionState::new();
        let mut events = Vec::new();
        interaction.note_use_dispatched(0x10, 0x20);
        assert_eq!(transactions.busy_count(), 0);
        assert_eq!(
            interaction.complete_use(&mut transactions, &mut events),
            Some((0x10, 0x20))
        );
    }

    // A cast's use-done while an uncounted item use waits: the answer is
    // the cast's, and the item use keeps waiting (OpenAC
    // `RuntimeInteractionTransactionState.CompleteUse`).
    #[test]
    fn a_cast_receipt_does_not_answer_an_uncounted_item_use() {
        let mut interaction = Interaction::new();
        let mut transactions = InventoryTransactionState::new();
        let mut events = Vec::new();
        transactions.increment_cast_busy_count(&mut events);
        interaction.note_use_dispatched(0x10, 0x20);
        assert_eq!(
            interaction.complete_use(&mut transactions, &mut events),
            None
        );
        assert_eq!(transactions.busy_count(), 0);
        assert_eq!(
            interaction.complete_use(&mut transactions, &mut events),
            Some((0x10, 0x20))
        );
    }
}
