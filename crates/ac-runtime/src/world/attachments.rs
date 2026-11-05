//! Which object hangs from which: the parent relations the server sent
//! (a sword in a hand, a shield on an arm) and the order a parent's
//! children were attached in.
//!
//! A relation can arrive before either side exists, or name a parent
//! incarnation that is not current yet. Parent events wait per child, in
//! arrival order, until the parent's named incarnation is the live one and
//! the child exists; one naming an older parent incarnation is dropped, one
//! naming a newer one keeps waiting. A relation a create carried waits for
//! its parent to appear and then binds to whatever incarnation of it is
//! current. Here an accepted relation is committed at once: there is no
//! presentation hand-off to wait for (a window rebuilds from the world).

use std::collections::{HashMap, VecDeque};

use ac_core::physics::is_newer;
use ac_proto::ParentEvent;

/// One parent relation: the child hangs from this incarnation of the
/// parent at this location.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ParentRelation {
    /// The parent.
    pub parent_guid: u32,
    /// The child.
    pub child_guid: u32,
    /// Where on the parent (a hand, a shield arm, ...).
    pub parent_location: u32,
    /// The placement frame the child takes.
    pub placement_id: u32,
    /// The parent incarnation the relation is about.
    pub parent_instance: u16,
    /// The child's position counter the relation carries.
    pub child_position_sequence: u16,
}

impl ParentRelation {
    /// The relation a parent event describes.
    #[must_use]
    pub fn of(event: &ParentEvent) -> Self {
        Self {
            parent_guid: event.parent_guid,
            child_guid: event.child_guid,
            parent_location: event.parent_location,
            placement_id: event.placement_id,
            parent_instance: event.parent_instance_sequence,
            child_position_sequence: event.child_position_sequence,
        }
    }
}

/// What a waiting relation is waiting for.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum WaitOwner {
    /// Not tried yet.
    Unknown,
    /// The parent (absent, or a newer incarnation than the live one).
    Parent,
    /// The child.
    Child,
}

/// A relation a create carried whose parent is not known yet; it binds to
/// the parent's incarnation when the parent appears.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct AwaitingParent {
    /// The parent.
    pub parent_guid: u32,
    /// The child.
    pub child_guid: u32,
    /// Where on the parent.
    pub parent_location: u32,
    /// The placement frame.
    pub placement_id: u32,
    /// The child's position counter.
    pub child_position_sequence: u16,
}

/// What resolving a child's waiting relations needs from the world.
pub trait ResolveWorld {
    /// Whether the object exists.
    fn is_known(&self, guid: u32) -> bool;
    /// The object's live incarnation, if it exists.
    fn instance_of(&self, guid: u32) -> Option<u16>;
    /// Offer the relation to the child's position counter; true when
    /// accepted (the counter advanced).
    fn accept(&mut self, relation: &ParentRelation) -> bool;
}

/// The parent relations of one session.
#[derive(Debug, Default)]
pub struct ParentAttachments {
    unresolved: HashMap<u32, VecDeque<(ParentRelation, WaitOwner)>>,
    awaiting_parent: HashMap<u32, AwaitingParent>,
    committed: HashMap<u32, ParentRelation>,
    children: HashMap<(u32, u16), Vec<u32>>,
}

impl ParentAttachments {
    /// Relations waiting, over every child.
    #[must_use]
    pub fn unresolved_count(&self) -> usize {
        self.unresolved.values().map(VecDeque::len).sum()
    }

    /// Children whose create-carried relation waits for its parent.
    #[must_use]
    pub fn awaiting_parent_count(&self) -> usize {
        self.awaiting_parent.len()
    }

    /// Children hanging from a parent.
    #[must_use]
    pub fn committed_count(&self) -> usize {
        self.committed.len()
    }

    /// A parent event: it waits behind the child's earlier ones.
    pub fn enqueue(&mut self, event: &ParentEvent) {
        self.unresolved
            .entry(event.child_guid)
            .or_default()
            .push_back((ParentRelation::of(event), WaitOwner::Unknown));
    }

    /// Try the child's waiting relations in arrival order; the first the
    /// world accepts is returned (the rest keep waiting). A relation whose
    /// parent is absent, or names a newer parent incarnation than the live
    /// one, waits; one naming an older incarnation is dropped; one whose
    /// child is absent waits; one the child's counter refuses is dropped.
    pub fn resolve(
        &mut self,
        child_guid: u32,
        world: &mut dyn ResolveWorld,
    ) -> Option<ParentRelation> {
        let queue = self.unresolved.get_mut(&child_guid)?;
        let mut accepted = None;
        for _ in 0..queue.len() {
            let Some((relation, _)) = queue.pop_front() else {
                break;
            };
            let Some(live) = world.instance_of(relation.parent_guid) else {
                queue.push_back((relation, WaitOwner::Parent));
                continue;
            };
            if live != relation.parent_instance {
                if !is_newer(relation.parent_instance, live) {
                    queue.push_back((relation, WaitOwner::Parent));
                }
                continue;
            }
            if !world.is_known(child_guid) {
                queue.push_back((relation, WaitOwner::Child));
                continue;
            }
            if world.accept(&relation) {
                accepted = Some(relation);
                break;
            }
        }
        if queue.is_empty() {
            self.unresolved.remove(&child_guid);
        }
        accepted
    }

    /// The relation is the child's from now on: it leaves its old parent's
    /// list (swapping the last child into its place) and goes to the end of
    /// the new one's.
    pub fn commit(&mut self, relation: ParentRelation) {
        self.remove_committed_child(relation.child_guid);
        self.committed.insert(relation.child_guid, relation);
        self.children
            .entry((relation.parent_guid, relation.parent_instance))
            .or_default()
            .push(relation.child_guid);
    }

    /// A relation a create carried, its parent known: it replaces any wait
    /// the child had for a parent and is committed.
    pub fn accept_create_relation(&mut self, relation: ParentRelation) {
        self.awaiting_parent.remove(&relation.child_guid);
        self.commit(relation);
    }

    /// A relation a create carried whose parent is not known yet waits for
    /// it.
    pub fn defer_until_parent_known(&mut self, request: AwaitingParent) {
        self.awaiting_parent.insert(request.child_guid, request);
    }

    /// The create-carried relations waiting for this parent, taken; a child
    /// already given a later word (a committed relation) is skipped.
    pub fn take_awaiting(&mut self, parent_guid: u32) -> Vec<AwaitingParent> {
        let mut children: Vec<u32> = self
            .awaiting_parent
            .values()
            .filter(|r| r.parent_guid == parent_guid)
            .map(|r| r.child_guid)
            .collect();
        children.sort_unstable();
        children
            .into_iter()
            .filter_map(|child| self.awaiting_parent.remove(&child))
            .filter(|r| !self.committed.contains_key(&r.child_guid))
            .collect()
    }

    /// The child's parent and its incarnation, when it hangs from one.
    #[must_use]
    pub fn committed_parent(&self, child_guid: u32) -> Option<(u32, u16)> {
        self.committed
            .get(&child_guid)
            .map(|r| (r.parent_guid, r.parent_instance))
    }

    /// Whether the child hangs from a parent.
    #[must_use]
    pub fn has_committed_parent(&self, child_guid: u32) -> bool {
        self.committed.contains_key(&child_guid)
    }

    /// The children hanging from this incarnation of the parent, in the
    /// order they were attached (a removal swaps the last into its place).
    #[must_use]
    pub fn children_attached_to(&self, parent_guid: u32, parent_instance: u16) -> &[u32] {
        self.children
            .get(&(parent_guid, parent_instance))
            .map_or(&[], Vec::as_slice)
    }

    /// The children with a waiting relation naming this parent, by id.
    #[must_use]
    pub fn children_unresolved_for(&self, parent_guid: u32) -> Vec<u32> {
        let mut children: Vec<u32> = self
            .unresolved
            .iter()
            .filter(|(_, q)| q.iter().any(|(r, _)| r.parent_guid == parent_guid))
            .map(|(child, _)| *child)
            .collect();
        children.sort_unstable();
        children
    }

    /// The children waiting for this parent or hanging from it, by id.
    #[must_use]
    pub fn children_waiting_for(&self, parent_guid: u32) -> Vec<u32> {
        let mut children = self.children_unresolved_for(parent_guid);
        children.extend(
            self.committed
                .values()
                .filter(|r| r.parent_guid == parent_guid)
                .map(|r| r.child_guid),
        );
        children.sort_unstable();
        children.dedup();
        children
    }

    /// The object is gone for good: every relation naming it, as child or
    /// as parent, goes.
    pub fn remove_object(&mut self, guid: u32) {
        self.awaiting_parent.retain(|_, r| r.parent_guid != guid);
        self.awaiting_parent.remove(&guid);
        self.remove_committed_child(guid);
        self.unresolved.remove(&guid);
        self.remove_committed_parent_references(guid);
        self.filter_parent_candidates(guid, |_| false);
    }

    /// One incarnation of the object ended and `replacement` took its
    /// place. As child: what this incarnation carried goes, relations still
    /// waiting for their parent stay. As parent: its children are let go,
    /// and waiting relations stay only when they name the replacement or a
    /// later incarnation.
    pub fn end_generation(&mut self, guid: u32, replacement: u16) {
        self.awaiting_parent.remove(&guid);
        self.filter_child_candidates(guid, |owner| owner == WaitOwner::Parent);
        self.remove_committed_child(guid);
        self.remove_committed_parent_references(guid);
        self.filter_parent_candidates(guid, |r| {
            r.parent_instance == replacement || is_newer(replacement, r.parent_instance)
        });
    }

    /// The server deleted this incarnation. As child: like a generation
    /// end. As parent: waiting relations stay only when they name a strictly
    /// later incarnation.
    pub fn delete_generation(&mut self, guid: u32, deleted: u16) {
        self.awaiting_parent.remove(&guid);
        self.filter_child_candidates(guid, |owner| owner == WaitOwner::Parent);
        self.remove_committed_child(guid);
        self.remove_committed_parent_references(guid);
        self.filter_parent_candidates(guid, |r| is_newer(deleted, r.parent_instance));
    }

    /// The child left its parent (a move into the world, a pickup); its
    /// waiting relations stay.
    pub fn end_child(&mut self, child_guid: u32) {
        self.remove_committed_child(child_guid);
    }

    /// The child is gone: everything about it as a child goes.
    pub fn remove_child(&mut self, child_guid: u32) {
        self.awaiting_parent.remove(&child_guid);
        self.remove_committed_child(child_guid);
        self.unresolved.remove(&child_guid);
    }

    /// Forget everything (a new session generation).
    pub fn clear(&mut self) {
        *self = Self::default();
    }

    fn remove_committed_child(&mut self, child_guid: u32) {
        let Some(relation) = self.committed.remove(&child_guid) else {
            return;
        };
        let key = (relation.parent_guid, relation.parent_instance);
        if let Some(children) = self.children.get_mut(&key) {
            if let Some(index) = children.iter().position(|c| *c == child_guid) {
                children.swap_remove(index);
            }
            if children.is_empty() {
                self.children.remove(&key);
            }
        }
    }

    fn remove_committed_parent_references(&mut self, parent_guid: u32) {
        let children: Vec<u32> = self
            .committed
            .values()
            .filter(|r| r.parent_guid == parent_guid)
            .map(|r| r.child_guid)
            .collect();
        for child in children {
            self.remove_committed_child(child);
        }
    }

    fn filter_parent_candidates(
        &mut self,
        parent_guid: u32,
        keep: impl Fn(&ParentRelation) -> bool,
    ) {
        self.unresolved.retain(|_, queue| {
            queue.retain(|(r, _)| r.parent_guid != parent_guid || keep(r));
            !queue.is_empty()
        });
    }

    fn filter_child_candidates(&mut self, child_guid: u32, keep: impl Fn(WaitOwner) -> bool) {
        if let Some(queue) = self.unresolved.get_mut(&child_guid) {
            queue.retain(|(_, owner)| keep(*owner));
            if queue.is_empty() {
                self.unresolved.remove(&child_guid);
            }
        }
    }
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.Runtime.Tests/Entities/ParentAttachmentStateTests.cs`
    //! (the ledger cases; the staged/recovery hand-off to presentation has no
    //! counterpart, an accepted relation commits at once). The cases that
    //! need objects are in `world::attachment_tests`.

    use super::*;
    use ac_core::physics::{PhysicsTimestampGate, PhysicsTimestamps};

    /// A world of known objects and their incarnations, with one child's
    /// position counter.
    struct Fixture {
        instances: HashMap<u32, u16>,
        child_gate: PhysicsTimestampGate,
        child_instance: u16,
        offered: usize,
    }

    impl Fixture {
        /// The child's counter seeded at position `position`, incarnation
        /// `instance` (`SeedForCreateObject(position, 0, ..., instance)`).
        fn new(position: u16, instance: u16) -> Self {
            let mut child_gate = PhysicsTimestampGate::new();
            child_gate.seed_for_create_object(PhysicsTimestamps {
                position,
                instance,
                ..PhysicsTimestamps::default()
            });
            Self {
                instances: HashMap::new(),
                child_gate,
                child_instance: instance,
                offered: 0,
            }
        }

        fn with(mut self, guid: u32, instance: u16) -> Self {
            self.instances.insert(guid, instance);
            self
        }
    }

    impl ResolveWorld for Fixture {
        fn is_known(&self, _guid: u32) -> bool {
            true
        }
        fn instance_of(&self, guid: u32) -> Option<u16> {
            self.instances.get(&guid).copied()
        }
        fn accept(&mut self, relation: &ParentRelation) -> bool {
            self.offered += 1;
            self.child_gate
                .try_accept_position_channel(self.child_instance, relation.child_position_sequence)
        }
    }

    fn event(
        parent: u32,
        child: u32,
        location: u32,
        placement: u32,
        parent_instance: u16,
        position: u16,
    ) -> ParentEvent {
        ParentEvent {
            parent_guid: parent,
            child_guid: child,
            parent_location: location,
            placement_id: placement,
            parent_instance_sequence: parent_instance,
            child_position_sequence: position,
        }
    }

    fn relation(parent: u32, child: u32, parent_instance: u16) -> ParentRelation {
        ParentRelation {
            parent_guid: parent,
            child_guid: child,
            parent_location: 1,
            placement_id: 2,
            parent_instance,
            child_position_sequence: 1,
        }
    }

    #[test]
    fn child_removal_zeroes_every_table() {
        // LedgerConvergence_ChildRemoval_ZeroesEveryTable (both rows).
        for (parent, instance) in [(0x5000_0900, 3), (0x7000_0901, 0)] {
            let child = 0x7000_0902;
            let mut ledger = ParentAttachments::default();
            ledger.commit(relation(parent, child, instance));
            assert_eq!(ledger.committed_count(), 1);
            assert!(!ledger.children_attached_to(parent, instance).is_empty());

            ledger.remove_child(child);

            assert_eq!(ledger.committed_count(), 0);
            assert_eq!(ledger.unresolved_count(), 0);
            assert!(ledger.children_attached_to(parent, instance).is_empty());
            assert_eq!(ledger.committed_parent(child), None);
            assert!(!ledger.has_committed_parent(child));
        }
    }

    #[test]
    fn parent_removal_zeroes_every_table() {
        // LedgerConvergence_ParentRemoval_ZeroesEveryTable.
        for (parent, instance) in [(0x5000_0920, 2), (0x7000_0921, 0)] {
            let child = 0x7000_0922;
            let mut ledger = ParentAttachments::default();
            ledger.commit(relation(parent, child, instance));

            ledger.remove_object(parent);

            assert_eq!(ledger.committed_count(), 0);
            assert!(ledger.children_attached_to(parent, instance).is_empty());
            assert!(!ledger.has_committed_parent(child));
        }
    }

    #[test]
    fn teardown_zeroes_every_table_with_mixed_pending_state() {
        // LedgerConvergence_Teardown_ZeroesEveryTableWithMixedPendingState.
        for (parent, instance) in [(0x5000_0930, 9), (0x7000_0931, 0)] {
            let (committed_child, unresolved_child) = (0x7000_0932, 0x7000_0933);
            let mut ledger = ParentAttachments::default();
            ledger.commit(relation(parent, committed_child, instance));
            ledger.enqueue(&event(parent, unresolved_child, 0, 0, instance, 1));
            assert!(ledger.committed_count() > 0);
            assert!(ledger.unresolved_count() > 0);

            ledger.clear();

            assert_eq!(ledger.committed_count(), 0);
            assert_eq!(ledger.unresolved_count(), 0);
            assert!(!ledger.has_committed_parent(committed_child));
            assert!(ledger.children_attached_to(parent, instance).is_empty());
        }
    }

    #[test]
    fn queued_relations_stay_ordered_and_the_newest_accepted_wins() {
        // MultipleQueuedRelationsRemainOrderedAndNewestAcceptedWins.
        let (parent, child) = (0x7000_0110, 0x7000_0111);
        let mut world = Fixture::new(4, 3).with(parent, 9);
        let mut ledger = ParentAttachments::default();
        ledger.enqueue(&event(parent, child, 1, 2, 9, 5));
        ledger.enqueue(&event(parent, child, 1, 3, 9, 6));

        let first = ledger.resolve(child, &mut world).unwrap();
        assert_eq!((first.child_position_sequence, first.placement_id), (5, 2));
        ledger.commit(first);
        let second = ledger.resolve(child, &mut world).unwrap();
        assert_eq!(
            (second.child_position_sequence, second.placement_id),
            (6, 3)
        );
        ledger.commit(second);
        assert_eq!(ledger.committed_parent(child), Some((parent, 9)));
        assert_eq!(ledger.children_attached_to(parent, 9), [child]);
        assert_eq!(ledger.unresolved_count(), 0);
    }

    #[test]
    fn a_stale_relation_cannot_replace_the_accepted_create_relation() {
        // RejectedStaleRelationCannotReplaceAcceptedCreateRelation.
        let child = 0x7000_0121;
        let mut world = Fixture::new(10, 3).with(0x7000_0122, 8);
        let mut ledger = ParentAttachments::default();
        ledger.commit(ParentRelation {
            child_position_sequence: 10,
            ..relation(0x7000_0120, child, 0)
        });
        ledger.enqueue(&event(0x7000_0122, child, 1, 4, 8, 9));

        assert_eq!(ledger.resolve(child, &mut world), None);
        assert_eq!(ledger.committed_parent(child), Some((0x7000_0120, 0)));
    }

    #[test]
    fn a_future_parent_generation_does_not_block_the_current_relation_behind_it() {
        // FutureParentGenerationDoesNotBlockCurrentRelationBehindIt.
        let (parent, child) = (0x7000_0140, 0x7000_0141);
        let mut world = Fixture::new(4, 3).with(parent, 9);
        let mut ledger = ParentAttachments::default();
        ledger.enqueue(&event(parent, child, 1, 2, 10, 6));
        ledger.enqueue(&event(parent, child, 1, 3, 9, 5));

        let accepted = ledger.resolve(child, &mut world).unwrap();
        assert_eq!(
            (accepted.child_position_sequence, accepted.placement_id),
            (5, 3)
        );
        assert!(ledger.children_waiting_for(parent).contains(&child));
        assert_eq!(ledger.unresolved_count(), 1, "the future one waits");
    }

    #[test]
    fn a_future_parent_relation_survives_the_childs_generation_replacement() {
        // FutureParentRelationSurvivesChildGenerationReplacement.
        let (parent, child) = (0x7000_0180, 0x7000_0181);
        let mut ledger = ParentAttachments::default();
        ledger.enqueue(&event(parent, child, 1, 3, 10, 5));

        let mut before = Fixture::new(0, 0).with(parent, 9);
        assert_eq!(ledger.resolve(child, &mut before), None);
        assert_eq!(before.offered, 0, "a future parent is never offered");
        ledger.end_generation(child, 4);

        let mut after = Fixture::new(4, 4).with(parent, 10);
        let accepted = ledger.resolve(child, &mut after).unwrap();
        assert_eq!(
            (accepted.parent_instance, accepted.child_position_sequence),
            (10, 5)
        );
    }

    #[test]
    fn remove_and_clear_discard_pending_and_committed_state() {
        // RemoveAndClearDiscardPendingAndRollbackState.
        let (parent, child) = (0x7000_0130, 0x7000_0131);
        let mut ledger = ParentAttachments::default();
        ledger.commit(ParentRelation {
            child_position_sequence: 4,
            ..relation(parent, child, 0)
        });
        ledger.enqueue(&event(parent, child, 1, 3, 9, 5));

        ledger.remove_object(parent);
        assert_eq!(ledger.committed_parent(child), None);
        assert!(ledger.children_waiting_for(parent).is_empty());
        assert_eq!(ledger.unresolved_count(), 0);

        ledger.commit(relation(parent, child, 0));
        ledger.clear();
        assert_eq!(ledger.committed_parent(child), None);
    }

    #[test]
    fn leaving_the_parent_keeps_a_fresher_waiting_parent_event() {
        // UnparentProjectionRetainsFresherPendingParentEvent.
        let (parent, child) = (0x7000_0160, 0x7000_0161);
        let mut world = Fixture::new(4, 3).with(parent, 9);
        let mut ledger = ParentAttachments::default();
        ledger.commit(ParentRelation {
            child_position_sequence: 4,
            ..relation(0x7000_0162, child, 0)
        });
        ledger.enqueue(&event(parent, child, 1, 3, 9, 5));

        ledger.end_child(child);
        assert_eq!(ledger.committed_parent(child), None);
        let accepted = ledger.resolve(child, &mut world).unwrap();
        assert_eq!(accepted.parent_guid, parent);
        assert_eq!(accepted.child_position_sequence, 5);
    }

    #[test]
    fn an_exact_parent_delete_keeps_only_strictly_later_generations() {
        // ExactParentDeleteRetainsOnlyStrictlyFutureGenerationRelation.
        let (parent, child) = (0x7000_0170, 0x7000_0171);
        let mut ledger = ParentAttachments::default();
        ledger.enqueue(&event(parent, child, 1, 2, 9, 5));
        ledger.enqueue(&event(parent, child, 1, 3, 10, 6));

        ledger.delete_generation(parent, 9);
        let mut world = Fixture::new(4, 3).with(parent, 10);
        let accepted = ledger.resolve(child, &mut world).unwrap();
        assert_eq!(
            (accepted.parent_instance, accepted.child_position_sequence),
            (10, 6)
        );
    }

    #[test]
    fn committed_children_append_in_commit_order() {
        // CommittedChildrenAppendInCommitOrder.
        let parent = 0x7000_0200;
        let mut ledger = ParentAttachments::default();
        for child in [0x7000_0201, 0x7000_0202, 0x7000_0203] {
            ledger.commit(relation(parent, child, 9));
        }
        assert_eq!(
            ledger.children_attached_to(parent, 9),
            [0x7000_0201, 0x7000_0202, 0x7000_0203]
        );
    }

    #[test]
    fn a_child_leaving_swaps_the_last_child_into_its_place() {
        // EndingChildProjectionSwapRemovesCommittedChild.
        let parent = 0x7000_0210;
        let mut ledger = ParentAttachments::default();
        for child in [0x7000_0211, 0x7000_0212, 0x7000_0213] {
            ledger.commit(relation(parent, child, 9));
        }
        ledger.end_child(0x7000_0211);
        assert_eq!(
            ledger.children_attached_to(parent, 9),
            [0x7000_0213, 0x7000_0212]
        );
    }

    #[test]
    fn a_reparent_swaps_out_the_old_entry_and_appends_the_new_one() {
        // ReparentSwapRemovesOldEntryAndAppendsNewEntry.
        let (old_parent, new_parent, child) = (0x7000_0220, 0x7000_0221, 0x7000_0222);
        let mut ledger = ParentAttachments::default();
        ledger.commit(relation(old_parent, child, 9));
        ledger.commit(relation(old_parent, 0x7000_0223, 9));
        ledger.commit(relation(old_parent, 0x7000_0224, 9));
        ledger.commit(relation(new_parent, 0x7000_0225, 4));

        ledger.commit(relation(new_parent, child, 4));

        assert_eq!(
            ledger.children_attached_to(old_parent, 9),
            [0x7000_0224, 0x7000_0223]
        );
        assert_eq!(
            ledger.children_attached_to(new_parent, 4),
            [0x7000_0225, child]
        );
    }

    #[test]
    fn committed_children_are_kept_apart_by_parent_and_incarnation() {
        // CommittedChildrenAreIsolatedByParentGuidAndIncarnation.
        let (parent, other) = (0x7000_0230, 0x7000_0231);
        let mut ledger = ParentAttachments::default();
        ledger.commit(relation(parent, 0x7000_0232, 9));
        ledger.commit(relation(parent, 0x7000_0233, 10));
        ledger.commit(relation(other, 0x7000_0234, 9));

        assert_eq!(ledger.children_attached_to(parent, 9), [0x7000_0232]);
        assert_eq!(ledger.children_attached_to(parent, 10), [0x7000_0233]);
        assert_eq!(ledger.children_attached_to(other, 9), [0x7000_0234]);
        assert!(ledger.children_attached_to(other, 10).is_empty());
    }

    #[test]
    fn a_create_relation_waits_for_its_parent_unless_a_later_word_arrived() {
        // EquippedChildRenderController.AdmitRelationsAwaitingParent and
        // ParentAttachmentState.HasLaterWordOnChild.
        let parent = 0x7000_0240;
        let request = |child| AwaitingParent {
            parent_guid: parent,
            child_guid: child,
            parent_location: 1,
            placement_id: 2,
            child_position_sequence: 1,
        };
        let mut ledger = ParentAttachments::default();
        ledger.defer_until_parent_known(request(0x7000_0241));
        ledger.defer_until_parent_known(request(0x7000_0242));
        // The second child was hung elsewhere meanwhile.
        ledger.commit(relation(0x7000_0249, 0x7000_0242, 1));
        assert_eq!(ledger.awaiting_parent_count(), 2);

        let admitted = ledger.take_awaiting(parent);
        assert_eq!(admitted, [request(0x7000_0241)]);
        assert_eq!(ledger.awaiting_parent_count(), 0);
    }
}
