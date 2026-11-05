//! The one inventory request in flight, the use requests outstanding (the
//! busy count) and the appraisals outstanding.
//!
//! One item request at a time: a pickup, move, wield, give, drop or shop
//! request is reserved, sent, and then completed by the server's answer
//! about that very object (its placement, its removal, its stack size, or
//! the shop listing) or failed by a refusal. Uses and appraisals are
//! counted separately so neither blocks the other.
//!
//! The runtime feeds the object table's events and the vendor transitions
//! in through [`InventoryTransactionState::observe_table_event`] and
//! [`InventoryTransactionState::observe_vendor_transition`].

use super::{
    ClientObjectTable, InventoryRequestKind, MoveOrigin, ObjectIdentity, TableEvent,
    VendorTransition, VendorTransitionKind,
};

/// The request in flight.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct PendingInventoryRequest {
    /// Never 0.
    pub token: u64,
    pub kind: InventoryRequestKind,
    /// The object (or, for a shop request, the vendor).
    pub item_id: u32,
    /// The record the request was made against; an answer about a later
    /// record of the same guid does not complete it.
    pub item_identity: Option<ObjectIdentity>,
    /// The request was sent.
    pub dispatched: bool,
}

/// What the transaction state did.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TransactionEvent {
    /// The pending request, busy count or appraisal count changed.
    StateChanged,
    /// The server answered the pending request.
    RequestCompleted(PendingInventoryRequest),
    /// The server refused the pending request.
    RequestFailed {
        request: PendingInventoryRequest,
        weenie_error: u32,
    },
    /// The object table was cleared (the projection owner rebuilds).
    ObjectTableCleared,
}

/// One use request counted as busy until it is sent (then the server's
/// "use done" releases it) or cancelled before sending. Resolving it a
/// second time does nothing, and a session reset makes it stale.
#[derive(Debug)]
pub struct UseReservation {
    generation: u64,
    resolved: bool,
}

/// See the module documentation.
#[derive(Debug, Default)]
pub struct InventoryTransactionState {
    next_request_token: u64,
    use_reservation_generation: u64,
    pending: Option<PendingInventoryRequest>,
    busy_count: u32,
    /// The part of `busy_count` taken by spell casts (the cursor's busy
    /// feedback), which does not hold up an item request.
    cast_busy_count: u32,
    appraisal_count: u32,
}

impl InventoryTransactionState {
    /// Nothing in flight.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Outstanding use requests, spell casts included.
    #[must_use]
    pub fn busy_count(&self) -> u32 {
        self.busy_count
    }

    /// Outstanding item actions: the busy count without the casts' part.
    #[must_use]
    pub fn item_busy_count(&self) -> u32 {
        self.busy_count - self.cast_busy_count
    }

    /// Outstanding appraisals; counted apart from the busy count, so a
    /// description in flight does not block an item action.
    #[must_use]
    pub fn appraisal_count(&self) -> u32 {
        self.appraisal_count
    }

    #[must_use]
    pub fn has_pending_request(&self) -> bool {
        self.pending.is_some()
    }

    /// No item action outstanding and no request pending (a cast's busy
    /// reference does not hold an item request up).
    #[must_use]
    pub fn can_begin_request(&self) -> bool {
        self.item_busy_count() == 0 && self.pending.is_none()
    }

    /// One appraisal at a time; item actions do not block it.
    #[must_use]
    pub fn can_begin_appraisal(&self) -> bool {
        self.appraisal_count == 0
    }

    /// The request in flight.
    #[must_use]
    pub fn pending(&self) -> Option<PendingInventoryRequest> {
        self.pending
    }

    fn next_token(&mut self) -> u64 {
        self.next_request_token = self.next_request_token.wrapping_add(1);
        if self.next_request_token == 0 {
            self.next_request_token = 1;
        }
        self.next_request_token
    }

    /// Reserves the request slot for `kind` on `item_id` (whose current
    /// record is `item_identity`, if known) before sending; none when an
    /// item id is 0 or another request or use is outstanding.
    pub fn try_reserve(
        &mut self,
        kind: InventoryRequestKind,
        item_id: u32,
        item_identity: Option<ObjectIdentity>,
        events: &mut Vec<TransactionEvent>,
    ) -> Option<PendingInventoryRequest> {
        if item_id == 0 || !self.can_begin_request() {
            return None;
        }
        let pending = PendingInventoryRequest {
            token: self.next_token(),
            kind,
            item_id,
            item_identity,
            dispatched: false,
        };
        self.pending = Some(pending);
        events.push(TransactionEvent::StateChanged);
        Some(pending)
    }

    /// The reserved request (matching token, kind and item) was sent.
    /// False when the reservation is not current or already sent.
    pub fn mark_dispatched(
        &mut self,
        token: u64,
        kind: InventoryRequestKind,
        item_id: u32,
        events: &mut Vec<TransactionEvent>,
    ) -> bool {
        match &mut self.pending {
            Some(current)
                if token != 0
                    && current.token == token
                    && current.item_id == item_id
                    && current.kind == kind
                    && !current.dispatched =>
            {
                current.dispatched = true;
                events.push(TransactionEvent::StateChanged);
                true
            }
            _ => false,
        }
    }

    /// Reserves, runs `dispatch` (the send, plus any local projection),
    /// and keeps the reservation only if it reports success.
    pub fn try_dispatch(
        &mut self,
        kind: InventoryRequestKind,
        item_id: u32,
        item_identity: Option<ObjectIdentity>,
        dispatch: impl FnOnce() -> bool,
        events: &mut Vec<TransactionEvent>,
    ) -> bool {
        let Some(reserved) = self.try_reserve(kind, item_id, item_identity, events) else {
            return false;
        };
        self.finish_dispatch(reserved, dispatch(), events)
    }

    /// Runs `dispatch` for a request reserved earlier (matching token, kind
    /// and item, not yet sent); a failed send releases it.
    pub fn try_dispatch_reserved(
        &mut self,
        token: u64,
        kind: InventoryRequestKind,
        item_id: u32,
        dispatch: impl FnOnce() -> bool,
        events: &mut Vec<TransactionEvent>,
    ) -> bool {
        let reserved = match self.pending {
            Some(current)
                if item_id != 0
                    && token != 0
                    && current.token == token
                    && current.item_id == item_id
                    && current.kind == kind
                    && !current.dispatched =>
            {
                current
            }
            _ => return false,
        };
        self.finish_dispatch(reserved, dispatch(), events)
    }

    fn finish_dispatch(
        &mut self,
        reserved: PendingInventoryRequest,
        dispatched: bool,
        events: &mut Vec<TransactionEvent>,
    ) -> bool {
        if !dispatched {
            self.clear_pending(reserved.token, events);
            return false;
        }
        self.mark_dispatched(reserved.token, reserved.kind, reserved.item_id, events);
        true
    }

    /// Releases a reservation that was never sent.
    pub fn cancel_before_dispatch(
        &mut self,
        token: u64,
        events: &mut Vec<TransactionEvent>,
    ) -> bool {
        match self.pending {
            Some(pending) if pending.token == token && !pending.dispatched => {
                self.pending = None;
                events.push(TransactionEvent::StateChanged);
                true
            }
            _ => false,
        }
    }

    fn clear_pending(&mut self, token: u64, events: &mut Vec<TransactionEvent>) {
        if self.pending.is_some_and(|pending| pending.token == token) {
            self.pending = None;
            events.push(TransactionEvent::StateChanged);
        }
    }

    /// Counts one more use outstanding.
    pub fn increment_busy_count(&mut self, events: &mut Vec<TransactionEvent>) {
        self.busy_count = self.busy_count.saturating_add(1);
        events.push(TransactionEvent::StateChanged);
    }

    /// Counts one more spell cast outstanding: busy for the cursor, but
    /// not for item requests.
    pub fn increment_cast_busy_count(&mut self, events: &mut Vec<TransactionEvent>) {
        if self.busy_count == u32::MAX {
            return;
        }
        self.busy_count += 1;
        self.cast_busy_count += 1;
        events.push(TransactionEvent::StateChanged);
    }

    /// Counts a use as busy before it is sent.
    pub fn begin_use_request_reservation(
        &mut self,
        events: &mut Vec<TransactionEvent>,
    ) -> UseReservation {
        self.increment_busy_count(events);
        UseReservation {
            generation: self.use_reservation_generation,
            resolved: false,
        }
    }

    /// The use was sent: it stays busy until the server's "use done".
    pub fn mark_use_dispatched(&mut self, reservation: &mut UseReservation) {
        reservation.resolved = true;
    }

    /// The use was not sent: release its busy count (unless already
    /// resolved or made stale by a reset).
    pub fn cancel_use_before_dispatch(
        &mut self,
        reservation: &mut UseReservation,
        events: &mut Vec<TransactionEvent>,
    ) {
        if std::mem::replace(&mut reservation.resolved, true)
            || reservation.generation != self.use_reservation_generation
        {
            return;
        }
        if self.item_busy_count() > 0 {
            self.busy_count -= 1;
        }
        events.push(TransactionEvent::StateChanged);
    }

    /// The server finished a use. The answer does not say which action it
    /// ends, so with both kinds outstanding an item action's reference goes
    /// first and item requests keep moving.
    pub fn complete_use(&mut self, events: &mut Vec<TransactionEvent>) {
        if self.busy_count == 0 {
            return;
        }
        if self.item_busy_count() == 0 {
            self.cast_busy_count -= 1;
        }
        self.busy_count -= 1;
        events.push(TransactionEvent::StateChanged);
    }

    /// Counts one more appraisal outstanding.
    pub fn increment_appraisal_count(&mut self, events: &mut Vec<TransactionEvent>) {
        self.appraisal_count = self.appraisal_count.saturating_add(1);
        events.push(TransactionEvent::StateChanged);
    }

    /// An appraisal was answered.
    pub fn complete_appraisal(&mut self, events: &mut Vec<TransactionEvent>) {
        if self.appraisal_count == 0 {
            return;
        }
        self.appraisal_count -= 1;
        events.push(TransactionEvent::StateChanged);
    }

    /// Drops every outstanding use and appraisal; outstanding use
    /// reservations become stale.
    pub fn clear_busy(&mut self, events: &mut Vec<TransactionEvent>) {
        if self.busy_count == 0 && self.appraisal_count == 0 {
            return;
        }
        self.use_reservation_generation = self.use_reservation_generation.wrapping_add(1);
        self.busy_count = 0;
        self.cast_busy_count = 0;
        self.appraisal_count = 0;
        events.push(TransactionEvent::StateChanged);
    }

    /// Session teardown: drops the pending request, uses and appraisals.
    pub fn reset_session(&mut self, events: &mut Vec<TransactionEvent>) {
        let changed = self.pending.is_some() || self.busy_count != 0 || self.appraisal_count != 0;
        self.pending = None;
        self.use_reservation_generation = self.use_reservation_generation.wrapping_add(1);
        self.busy_count = 0;
        self.cast_busy_count = 0;
        self.appraisal_count = 0;
        if changed {
            events.push(TransactionEvent::StateChanged);
        }
    }

    /// Follows one object-table event: a server placement, removal or
    /// stack-size answer about the pending object completes it; a refused
    /// move fails it (whatever guid the refusal carries); a cleared table
    /// drops it.
    pub fn observe_table_event(
        &mut self,
        event: &TableEvent,
        objects: &ClientObjectTable,
        events: &mut Vec<TransactionEvent>,
    ) {
        match event {
            TableEvent::Moved(m) if m.origin == MoveOrigin::AuthoritativeResponse => {
                self.complete_inventory_response(m.item_id, m.identity, objects, events);
            }
            TableEvent::MoveRequestFailed(failure) => {
                self.on_move_failed(failure.item_id, failure.weenie_error, objects, events);
            }
            TableEvent::Removed { object, .. } => {
                self.complete_inventory_response(
                    object.object_id(),
                    Some(object.identity()),
                    objects,
                    events,
                );
            }
            TableEvent::StackSizeUpdated {
                object_id,
                identity,
            } => {
                self.complete_inventory_response(*object_id, Some(*identity), objects, events);
            }
            TableEvent::Cleared => {
                let changed = self.pending.take().is_some();
                events.push(TransactionEvent::ObjectTableCleared);
                if changed {
                    events.push(TransactionEvent::StateChanged);
                }
            }
            _ => {}
        }
    }

    /// Follows the vendor: the listing of the shop a shop request named
    /// completes it.
    pub fn observe_vendor_transition(
        &mut self,
        transition: &VendorTransition,
        events: &mut Vec<TransactionEvent>,
    ) {
        if !matches!(
            transition.kind,
            VendorTransitionKind::Opened | VendorTransitionKind::Refreshed
        ) {
            return;
        }
        match self.pending {
            Some(pending)
                if pending.kind == InventoryRequestKind::Shop
                    && pending.item_id == transition.vendor_id =>
            {
                self.pending = None;
                events.push(TransactionEvent::RequestCompleted(pending));
                events.push(TransactionEvent::StateChanged);
            }
            _ => {}
        }
    }

    fn on_move_failed(
        &mut self,
        failed_item_id: u32,
        weenie_error: u32,
        objects: &ClientObjectTable,
        events: &mut Vec<TransactionEvent>,
    ) {
        if let Some(shop) = self.pending
            && shop.kind == InventoryRequestKind::Shop
        {
            self.pending = None;
            events.push(TransactionEvent::RequestFailed {
                request: shop,
                weenie_error,
            });
            events.push(TransactionEvent::StateChanged);
            return;
        }
        // While a request is pending, a refusal is about that request
        // whatever guid it carries (none, or a merge target's); the guid
        // only matters with nothing pending.
        let item_id = self.pending.map_or(failed_item_id, |p| p.item_id);
        let identity = objects.get(item_id).map(|item| item.identity());
        // The refusal ends the request as any answer does (completed, state
        // changed), then is announced as a failure.
        if let Some(failed) = self.complete_inventory_response(item_id, identity, objects, events) {
            events.push(TransactionEvent::RequestFailed {
                request: failed,
                weenie_error,
            });
        }
    }

    fn complete_inventory_response(
        &mut self,
        item_id: u32,
        identity: Option<ObjectIdentity>,
        objects: &ClientObjectTable,
        events: &mut Vec<TransactionEvent>,
    ) -> Option<PendingInventoryRequest> {
        let request = self.pending?;
        if request.kind == InventoryRequestKind::Shop
            || request.item_id != item_id
            || !matches_identity(request.item_identity, item_id, identity, objects)
        {
            return None;
        }
        self.pending = None;
        events.push(TransactionEvent::RequestCompleted(request));
        events.push(TransactionEvent::StateChanged);
        Some(request)
    }
}

fn matches_identity(
    expected: Option<ObjectIdentity>,
    item_id: u32,
    actual: Option<ObjectIdentity>,
    objects: &ClientObjectTable,
) -> bool {
    let Some(expected) = expected else {
        return true;
    };
    if let Some(actual) = actual {
        return actual == expected;
    }
    objects
        .get(item_id)
        .is_none_or(|current| current.identity() == expected)
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.Core.Tests/Items/InventoryTransactionStateTests.cs`.
    //! OpenAC's tests of re-entrant dispatch from inside a send or an
    //! observer, of exceptions thrown by a send or observer, and of
    //! disposal have no counterpart: the state is borrowed mutably while a
    //! send runs, observers do not exist, and dropping the state detaches
    //! nothing. Where they pin an ordering, the follow-up is applied after
    //! the call.

    use super::*;
    use crate::items::{
        ClientObject, ClientObjectPlacement, EquipMask, ItemType, VendorShopProfile, VendorState,
    };

    const PLAYER: u32 = 0x5000_0001;
    const PACK: u32 = 0x5000_0002;
    const FIRST: u32 = 0x6000_0001;
    const SECOND: u32 = 0x6000_0002;

    struct Fixture {
        objects: ClientObjectTable,
        state: InventoryTransactionState,
        table_events: Vec<TableEvent>,
        events: Vec<TransactionEvent>,
    }

    impl Fixture {
        fn new() -> Self {
            let mut table_events = Vec::new();
            let mut objects = ClientObjectTable::new();
            for (id, name) in [(FIRST, "first"), (SECOND, "second")] {
                let mut item = ClientObject::new(id);
                item.name = name.into();
                item.item_type = ItemType::MISC;
                objects.add_or_update(item, &mut table_events);
            }
            objects.move_item(FIRST, PACK, 0, EquipMask::NONE, None, &mut table_events);
            objects.move_item(SECOND, PACK, 1, EquipMask::NONE, None, &mut table_events);
            Self {
                objects,
                state: InventoryTransactionState::new(),
                table_events: Vec::new(),
                events: Vec::new(),
            }
        }

        fn identity(&self, id: u32) -> Option<ObjectIdentity> {
            self.objects.get(id).map(ClientObject::identity)
        }

        fn dispatch(&mut self, kind: InventoryRequestKind, id: u32) -> bool {
            let identity = self.identity(id);
            self.state
                .try_dispatch(kind, id, identity, || true, &mut self.events)
        }

        /// Feeds the table events produced so far into the state.
        fn pump(&mut self) {
            for event in std::mem::take(&mut self.table_events) {
                self.state
                    .observe_table_event(&event, &self.objects, &mut self.events);
            }
        }

        fn confirmed_move(&mut self, id: u32, container: u32, slot: i32) -> bool {
            let moved = self.objects.apply_confirmed_server_move(
                id,
                ClientObjectPlacement::in_container(container, slot),
                None,
                &mut self.table_events,
            );
            self.pump();
            moved
        }

        fn reject(&mut self, id: u32, error: u32) {
            self.objects.reject_move(id, error, &mut self.table_events);
            self.pump();
        }

        fn failures(&self) -> Vec<(PendingInventoryRequest, u32)> {
            self.events
                .iter()
                .filter_map(|event| match event {
                    TransactionEvent::RequestFailed {
                        request,
                        weenie_error,
                    } => Some((*request, *weenie_error)),
                    _ => None,
                })
                .collect()
        }

        fn completed(&self) -> Vec<PendingInventoryRequest> {
            self.events
                .iter()
                .filter_map(|event| match event {
                    TransactionEvent::RequestCompleted(request) => Some(*request),
                    _ => None,
                })
                .collect()
        }
    }

    // OpenAC `InventoryTransactionStateTests.CursorBusyCountDoesNotBlockAnInventoryRequest`.
    #[test]
    fn cursor_busy_count_does_not_block_an_inventory_request() {
        let mut f = Fixture::new();
        f.state.increment_cast_busy_count(&mut f.events);
        assert!(f.state.can_begin_request());
        assert!(f.dispatch(InventoryRequestKind::PutInContainer, FIRST));
        assert_eq!(f.state.busy_count(), 1);
        assert!(!f.state.can_begin_request());
    }

    // OpenAC `InventoryTransactionStateTests.ItemBusyReferenceStillBlocksWhileCastReferencesRemain`.
    #[test]
    fn item_busy_reference_still_blocks_while_cast_references_remain() {
        let mut f = Fixture::new();
        f.state.increment_cast_busy_count(&mut f.events);
        f.state.increment_busy_count(&mut f.events);
        assert_eq!(f.state.busy_count(), 2);
        assert_eq!(f.state.item_busy_count(), 1);
        assert!(!f.state.can_begin_request());

        f.state.complete_use(&mut f.events);
        assert_eq!(f.state.busy_count(), 1);
        assert_eq!(f.state.item_busy_count(), 0);
        assert!(f.state.can_begin_request());

        f.state.complete_use(&mut f.events);
        assert_eq!(f.state.busy_count(), 0);
    }

    #[test]
    fn shop_request_waits_for_matching_vendor_response_or_failure() {
        let mut f = Fixture::new();
        let mut vendor = VendorState::new();
        let (first_vendor, second_vendor) = (0x4000_1000, 0x4000_2000);

        assert!(f.dispatch(InventoryRequestKind::Shop, first_vendor));
        assert_eq!(f.state.busy_count(), 0);
        assert!(f.state.has_pending_request());
        f.state.complete_use(&mut f.events);
        assert!(f.confirmed_move(FIRST, PLAYER, 0));
        let transition = vendor
            .apply(second_vendor, VendorShopProfile::default(), Vec::new())
            .expect("opened");
        f.state
            .observe_vendor_transition(&transition, &mut f.events);
        assert!(f.state.has_pending_request());

        let transition = vendor
            .apply(first_vendor, VendorShopProfile::default(), Vec::new())
            .expect("opened");
        f.state
            .observe_vendor_transition(&transition, &mut f.events);
        assert!(!f.state.has_pending_request());

        assert!(f.dispatch(InventoryRequestKind::Shop, first_vendor));
        f.reject(FIRST, 3);
        assert!(!f.state.has_pending_request());
    }

    #[test]
    fn reserve_closes_the_window_for_a_second_request_and_a_send_commits() {
        let mut f = Fixture::new();
        let identity = f.identity(FIRST);
        let reserved = f
            .state
            .try_reserve(
                InventoryRequestKind::PutInContainer,
                FIRST,
                identity,
                &mut f.events,
            )
            .expect("reserved");
        assert!(!f.dispatch(InventoryRequestKind::PutInContainer, SECOND));
        assert!(f.state.try_dispatch_reserved(
            reserved.token,
            InventoryRequestKind::PutInContainer,
            FIRST,
            || true,
            &mut f.events
        ));
        let pending = f.state.pending().expect("pending");
        assert_eq!(pending.item_id, FIRST);
        assert!(pending.dispatched);
    }

    #[test]
    fn dispatch_failure_releases_the_exact_provisional_owner() {
        let mut f = Fixture::new();
        assert!(!f.state.try_dispatch(
            InventoryRequestKind::PutInContainer,
            FIRST,
            None,
            || false,
            &mut f.events
        ));
        assert!(!f.state.has_pending_request());
        assert!(f.state.can_begin_request());
    }

    #[test]
    fn cancelled_reservation_rolls_back_only_itself() {
        let mut f = Fixture::new();
        let reserved = f
            .state
            .try_reserve(InventoryRequestKind::Pickup, FIRST, None, &mut f.events)
            .expect("reserved");
        assert!(
            !f.state
                .cancel_before_dispatch(reserved.token + 1, &mut f.events)
        );
        assert!(
            f.state
                .cancel_before_dispatch(reserved.token, &mut f.events)
        );
        assert!(!f.state.has_pending_request());
        assert!(f.state.can_begin_request());
    }

    #[test]
    fn authoritative_response_clears_before_a_follow_up_request() {
        let mut f = Fixture::new();
        assert!(f.dispatch(InventoryRequestKind::PutInContainer, FIRST));
        assert!(f.confirmed_move(FIRST, PLAYER, 0));
        let completed = f.completed();
        assert_eq!(completed.len(), 1);
        assert_eq!(completed[0].item_id, FIRST);
        assert!(!f.state.has_pending_request());
        assert!(f.dispatch(InventoryRequestKind::PutInContainer, SECOND));
        assert_eq!(f.state.pending().map(|p| p.item_id), Some(SECOND));
    }

    #[test]
    fn optimistic_move_and_unrelated_responses_do_not_complete_request() {
        let mut f = Fixture::new();
        let identity = f.identity(FIRST);
        let objects = &mut f.objects;
        let table_events = &mut f.table_events;
        assert!(f.state.try_dispatch(
            InventoryRequestKind::PutInContainer,
            FIRST,
            identity,
            || objects.move_item_optimistic(FIRST, PLAYER, 0, table_events),
            &mut f.events
        ));
        f.pump();
        assert!(f.objects.apply_confirmed_server_move(
            SECOND,
            ClientObjectPlacement::in_container(PLAYER, 1),
            None,
            &mut f.table_events
        ));
        f.pump();
        assert!(f.state.has_pending_request());

        assert!(f.confirmed_move(FIRST, PLAYER, 0));
        assert!(!f.state.has_pending_request());
    }

    #[test]
    fn reused_guid_response_cannot_complete_prior_object_identity() {
        let mut f = Fixture::new();
        let original = f.identity(FIRST);
        assert!(f.dispatch(InventoryRequestKind::PutInContainer, FIRST));

        let mut replacement = ClientObject::new(FIRST);
        replacement.name = "replacement".into();
        replacement.item_type = ItemType::MISC;
        let new_identity = f.objects.add_or_update(replacement, &mut f.table_events);
        f.objects
            .move_item(FIRST, PACK, 0, EquipMask::NONE, None, &mut f.table_events);
        f.pump();
        assert_ne!(original, Some(new_identity));

        assert!(f.confirmed_move(FIRST, PLAYER, 0));
        assert!(f.state.has_pending_request());
    }

    #[test]
    fn use_reservation_transfers_to_use_done_or_cancels_before_dispatch() {
        let mut f = Fixture::new();
        let mut cancelled = f.state.begin_use_request_reservation(&mut f.events);
        assert_eq!(f.state.busy_count(), 1);
        f.state
            .cancel_use_before_dispatch(&mut cancelled, &mut f.events);
        f.state
            .cancel_use_before_dispatch(&mut cancelled, &mut f.events);
        assert_eq!(f.state.busy_count(), 0);

        let mut dispatched = f.state.begin_use_request_reservation(&mut f.events);
        f.state.mark_use_dispatched(&mut dispatched);
        f.state
            .cancel_use_before_dispatch(&mut dispatched, &mut f.events);
        assert_eq!(f.state.busy_count(), 1);
        f.state.complete_use(&mut f.events);
        assert_eq!(f.state.busy_count(), 0);
    }

    #[test]
    fn reset_invalidates_late_use_reservation_and_clears_pending_request() {
        let mut f = Fixture::new();
        assert!(
            f.state
                .try_reserve(InventoryRequestKind::Pickup, FIRST, None, &mut f.events)
                .is_some()
        );
        let mut stale = f.state.begin_use_request_reservation(&mut f.events);
        f.state.reset_session(&mut f.events);
        f.state
            .cancel_use_before_dispatch(&mut stale, &mut f.events);
        assert_eq!(f.state.busy_count(), 0);
        assert!(!f.state.has_pending_request());
        assert!(f.state.can_begin_request());
    }

    #[test]
    fn object_table_clear_releases_request_and_notifies_projection_owner() {
        let mut f = Fixture::new();
        assert!(f.dispatch(InventoryRequestKind::PutInContainer, FIRST));
        f.objects.clear(&mut f.table_events);
        f.pump();
        let cleared = f
            .events
            .iter()
            .filter(|event| **event == TransactionEvent::ObjectTableCleared)
            .count();
        assert_eq!(cleared, 1);
        assert!(!f.state.has_pending_request());
    }

    #[test]
    fn reject_move_fails_with_latched_kind_and_wire_error() {
        let mut f = Fixture::new();
        assert!(f.dispatch(InventoryRequestKind::DropToWorld, FIRST));
        f.reject(FIRST, 0x426);
        let failures = f.failures();
        assert_eq!(failures.len(), 1);
        assert_eq!(failures[0].0.kind, InventoryRequestKind::DropToWorld);
        assert_eq!(failures[0].0.item_id, FIRST);
        assert_eq!(failures[0].1, 0x426);
        assert!(!f.state.has_pending_request());
    }

    #[test]
    fn request_failed_resolves_the_latched_request_whatever_guid_the_wire_carries() {
        let mut f = Fixture::new();
        assert!(f.dispatch(InventoryRequestKind::DropToWorld, FIRST));
        f.reject(SECOND, 0x426);
        let failures = f.failures();
        assert_eq!(failures.len(), 1);
        assert_eq!(failures[0].0.item_id, FIRST);
        assert_eq!(failures[0].1, 0x426);
        assert!(!f.state.has_pending_request());

        f.reject(FIRST, 0x1D);
        assert_eq!(f.failures().len(), 1);
        assert!(!f.state.has_pending_request());
    }

    #[test]
    fn reject_move_with_no_guid_resolves_the_sole_outstanding_request() {
        let mut f = Fixture::new();
        assert!(f.dispatch(InventoryRequestKind::Pickup, FIRST));
        f.reject(0, 0x43E);
        let failures = f.failures();
        assert_eq!(failures.len(), 1);
        assert_eq!(failures[0].0.item_id, FIRST);
        assert_eq!(failures[0].1, 0x43E);
        assert!(!f.state.has_pending_request());
        assert!(f.dispatch(InventoryRequestKind::Pickup, SECOND));
    }
}
