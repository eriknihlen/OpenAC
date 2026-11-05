//! Per-object update timestamps: which server update about an object is new
//! enough to apply.
//!
//! Every object carries nine 16-bit counters, one per kind of update. The
//! server bumps a counter each time it sends that kind of update; the client
//! applies an update only if its counter is newer than the one it has, so
//! late or duplicated packets cannot move an object backwards. The instance
//! counter names the object's incarnation: a new one starts when the server
//! re-creates the object.

/// The nine counters, in the order the server sends them.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct PhysicsTimestamps {
    /// Position updates (also parent and pickup events).
    pub position: u16,
    /// Movement (motion) updates.
    pub movement: u16,
    /// Physics state updates.
    pub state: u16,
    /// Velocity updates.
    pub vector: u16,
    /// Teleports.
    pub teleport: u16,
    /// The era of server-controlled movement.
    pub server_controlled_move: u16,
    /// Forced positions of the local player.
    pub force_position: u16,
    /// Appearance updates.
    pub obj_desc: u16,
    /// The object's incarnation.
    pub instance: u16,
}

/// What a create-object message means for an object already known.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CreateDisposition {
    /// Older than the incarnation the client has; ignore it.
    StaleGeneration,
    /// The first create the client has seen for this object.
    InitialGeneration,
    /// Same incarnation; its parts go through the per-kind checks.
    ExistingGeneration,
    /// A newer incarnation; it replaces every counter.
    NewGeneration,
}

/// What a position update should do.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PositionDisposition {
    /// Not new enough; ignore it.
    Rejected,
    /// Apply it as an ordinary move.
    Apply,
    /// The server forces the local player here (jump straight to it).
    ForcePosition,
}

/// True if `new` is newer than `old`, treating the counters as wrapping:
/// when the two are more than half the range apart, the smaller number is
/// the newer one (it has wrapped past 65535). At exactly half the range the
/// smaller number counts as newer too.
#[must_use]
pub fn is_newer(old: u16, new: u16) -> bool {
    let distance = (i32::from(new) - i32::from(old)).abs();
    if distance > 0x7FFF {
        new < old
    } else {
        old < new
    }
}

/// The counters of one object, and the rules for advancing them.
#[derive(Debug, Clone, Default)]
pub struct PhysicsTimestampGate {
    stamps: PhysicsTimestamps,
    seeded: bool,
}

impl PhysicsTimestampGate {
    /// An object the client has not seen created yet; every update is
    /// refused until the first create.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// The current counters.
    #[must_use]
    pub fn stamps(&self) -> PhysicsTimestamps {
        self.stamps
    }

    /// Start processing a create-object message. A newer incarnation replaces
    /// every counter; the same incarnation leaves them alone so the message's
    /// parts can pass the same per-kind checks as standalone updates.
    pub fn seed_for_create_object(&mut self, incoming: PhysicsTimestamps) -> CreateDisposition {
        let disposition = self.preview_create_object(incoming.instance);
        if matches!(
            disposition,
            CreateDisposition::InitialGeneration | CreateDisposition::NewGeneration
        ) {
            self.stamps = incoming;
            self.seeded = true;
        }
        disposition
    }

    /// Start processing a re-sent description (an update-object message): it
    /// replaces the object outright, so every counter is taken from it,
    /// whatever its instance, and an object already seeded is a new
    /// generation of itself.
    pub fn seed_for_replacement(&mut self, incoming: PhysicsTimestamps) -> CreateDisposition {
        let disposition = if self.seeded {
            CreateDisposition::NewGeneration
        } else {
            CreateDisposition::InitialGeneration
        };
        self.stamps = incoming;
        self.seeded = true;
        disposition
    }

    /// Classify a create-object message without changing any counter.
    #[must_use]
    pub fn preview_create_object(&self, instance: u16) -> CreateDisposition {
        if !self.seeded {
            CreateDisposition::InitialGeneration
        } else if is_newer(self.stamps.instance, instance) {
            CreateDisposition::NewGeneration
        } else if is_newer(instance, self.stamps.instance) {
            CreateDisposition::StaleGeneration
        } else {
            CreateDisposition::ExistingGeneration
        }
    }

    /// A movement update. The movement counter is consumed before the
    /// server-control era is checked; an equal era passes.
    pub fn try_accept_movement(
        &mut self,
        instance: u16,
        movement: u16,
        server_controlled_move: u16,
    ) -> bool {
        if !self.is_current_instance(instance) || !advance(&mut self.stamps.movement, movement) {
            return false;
        }
        if is_newer(server_controlled_move, self.stamps.server_controlled_move) {
            return false;
        }
        self.stamps.server_controlled_move = server_controlled_move;
        true
    }

    /// A physics state update.
    pub fn try_accept_state(&mut self, instance: u16, state: u16) -> bool {
        self.is_current_instance(instance) && advance(&mut self.stamps.state, state)
    }

    /// A velocity update.
    pub fn try_accept_vector(&mut self, instance: u16, vector: u16) -> bool {
        self.is_current_instance(instance) && advance(&mut self.stamps.vector, vector)
    }

    /// An appearance update.
    pub fn try_accept_obj_desc(&mut self, instance: u16, obj_desc: u16) -> bool {
        self.is_current_instance(instance) && advance(&mut self.stamps.obj_desc, obj_desc)
    }

    /// A parent or pickup event; they share the position counter.
    pub fn try_accept_position_channel(&mut self, instance: u16, position: u16) -> bool {
        self.is_current_instance(instance) && advance(&mut self.stamps.position, position)
    }

    /// True if `instance` is the incarnation the client has.
    #[must_use]
    pub fn is_current_instance(&self, instance: u16) -> bool {
        self.seeded && instance == self.stamps.instance
    }

    /// A player-teleport start is admitted when its counter is equal to or
    /// newer than the current one. It does not advance the counter; the
    /// destination position update does.
    #[must_use]
    pub fn is_fresh_teleport_start(&self, teleport: u16) -> bool {
        self.seeded && !is_newer(teleport, self.stamps.teleport)
    }

    /// A delete affects only the exact current incarnation, and never the
    /// local player.
    #[must_use]
    pub fn try_accept_delete(&self, instance: u16, is_local_player: bool) -> bool {
        !is_local_player && self.seeded && instance == self.stamps.instance
    }

    /// A position update. The position counter is advanced tentatively and
    /// restored if a newer teleport is already known; a fresh teleport
    /// advances its own counter. The local player's forced-position counter
    /// is independent, and other objects never consume it.
    pub fn try_accept_position(
        &mut self,
        instance: u16,
        position: u16,
        teleport: u16,
        force_position: u16,
        is_local_player: bool,
    ) -> PositionDisposition {
        if !self.is_current_instance(instance) {
            return PositionDisposition::Rejected;
        }
        if is_local_player && is_newer(self.stamps.force_position, force_position) {
            self.stamps.force_position = force_position;
            if !is_newer(teleport, self.stamps.teleport) {
                self.stamps.position = position;
                return PositionDisposition::ForcePosition;
            }
        }
        let previous = self.stamps.position;
        if !advance(&mut self.stamps.position, position) {
            return PositionDisposition::Rejected;
        }
        if is_newer(teleport, self.stamps.teleport) {
            self.stamps.position = previous;
            return PositionDisposition::Rejected;
        }
        if is_newer(self.stamps.teleport, teleport) {
            self.stamps.teleport = teleport;
        }
        PositionDisposition::Apply
    }
}

/// Store `incoming` if it is strictly newer than `current`.
fn advance(current: &mut u16, incoming: u16) -> bool {
    if is_newer(*current, incoming) {
        *current = incoming;
        true
    } else {
        false
    }
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.Core.Tests/Physics/MotionSequenceGateTests.cs`.

    use super::*;

    #[allow(clippy::too_many_arguments)]
    fn stamps(
        position: u16,
        movement: u16,
        state: u16,
        vector: u16,
        teleport: u16,
        server_controlled_move: u16,
        force_position: u16,
        obj_desc: u16,
        instance: u16,
    ) -> PhysicsTimestamps {
        PhysicsTimestamps {
            position,
            movement,
            state,
            vector,
            teleport,
            server_controlled_move,
            force_position,
            obj_desc,
            instance,
        }
    }

    fn seed_movement(gate: &mut PhysicsTimestampGate, instance: u16, movement: u16, sc: u16) {
        gate.seed_for_create_object(stamps(0, movement, 0, 0, 0, sc, 0, 0, instance));
    }

    #[test]
    fn is_newer_matches_the_wraparound_compare() {
        for (old, new, expected) in [
            (0, 1, true),
            (1, 0, false),
            (5, 5, false),
            (0, 0x7FFF, true),
            (0, 0x8000, false),
            (0xFFFF, 0, true),
            (0xFFFE, 2, true),
            (2, 0xFFFE, false),
        ] {
            assert_eq!(is_newer(old, new), expected, "{old:#x} -> {new:#x}");
        }
    }

    /// OpenAC `PhysicsTimestampGateTests.Replacement_TakesEveryCounterWhateverItsInstance`.
    #[test]
    fn a_replacement_takes_every_counter_whatever_its_instance() {
        let mut gate = PhysicsTimestampGate::new();
        assert_eq!(
            gate.seed_for_replacement(stamps(1, 1, 1, 1, 1, 1, 1, 1, 5)),
            CreateDisposition::InitialGeneration
        );
        // The same instance and an older one both replace the object.
        assert_eq!(
            gate.seed_for_replacement(stamps(9, 9, 9, 9, 9, 9, 9, 9, 5)),
            CreateDisposition::NewGeneration
        );
        assert_eq!(
            gate.seed_for_replacement(stamps(2, 3, 4, 5, 6, 7, 8, 1, 4)),
            CreateDisposition::NewGeneration
        );
        assert_eq!(gate.stamps(), stamps(2, 3, 4, 5, 6, 7, 8, 1, 4));
        assert!(gate.try_accept_state(4, 5));
    }

    #[test]
    fn events_before_the_first_create_are_rejected() {
        let mut gate = PhysicsTimestampGate::new();
        assert!(!gate.try_accept_movement(0, 1, 0));
        assert!(!gate.try_accept_state(0, 1));
        assert!(!gate.try_accept_vector(0, 1));
        assert!(!gate.try_accept_obj_desc(0, 1));
    }

    #[test]
    fn duplicate_and_stale_movement_is_dropped() {
        let mut gate = PhysicsTimestampGate::new();
        seed_movement(&mut gate, 0, 0, 0);
        assert!(gate.try_accept_movement(0, 5, 0));
        assert!(!gate.try_accept_movement(0, 5, 0));
        assert!(!gate.try_accept_movement(0, 4, 0));
        assert!(gate.try_accept_movement(0, 6, 0));
    }

    #[test]
    fn movement_wraps_around_the_u16_seam() {
        let mut gate = PhysicsTimestampGate::new();
        seed_movement(&mut gate, 0, 0xFFFD, 0);
        assert!(gate.try_accept_movement(0, 0xFFFE, 0));
        assert!(gate.try_accept_movement(0, 2, 0));
        assert!(!gate.try_accept_movement(0, 0xFFFE, 0));
    }

    #[test]
    fn seeded_gate_accepts_next_rejects_the_past() {
        let mut gate = PhysicsTimestampGate::new();
        seed_movement(&mut gate, 3, 0x9000, 2);
        assert!(gate.try_accept_movement(3, 0x9001, 2));
        assert!(!gate.try_accept_movement(3, 0x8FFF, 2));
    }

    #[test]
    fn same_generation_create_does_not_consume_channels() {
        let mut gate = PhysicsTimestampGate::new();
        seed_movement(&mut gate, 3, 0x9000, 2);
        assert!(gate.try_accept_movement(3, 0x9001, 2));
        assert_eq!(
            gate.seed_for_create_object(stamps(0, 0x9002, 0, 0, 0, 2, 0, 0, 3)),
            CreateDisposition::ExistingGeneration
        );
        assert!(gate.try_accept_movement(3, 0x9002, 2));
        seed_movement(&mut gate, 4, 0x9010, 2);
        assert!(!gate.try_accept_movement(3, 0x9011, 2));
        assert!(gate.try_accept_movement(4, 0x9011, 2));
    }

    #[test]
    fn preview_matches_seed_without_mutation() {
        let mut gate = PhysicsTimestampGate::new();
        assert_eq!(
            gate.preview_create_object(7),
            CreateDisposition::InitialGeneration
        );
        assert_eq!(
            gate.seed_for_create_object(stamps(10, 20, 30, 40, 50, 60, 70, 80, 7)),
            CreateDisposition::InitialGeneration
        );
        assert_eq!(
            gate.preview_create_object(7),
            CreateDisposition::ExistingGeneration
        );
        assert_eq!(
            gate.preview_create_object(8),
            CreateDisposition::NewGeneration
        );
        assert_eq!(
            gate.preview_create_object(6),
            CreateDisposition::StaleGeneration
        );
        assert!(gate.try_accept_movement(7, 21, 60));
        assert!(!gate.try_accept_movement(8, 22, 60));
    }

    #[test]
    fn same_generation_create_channels_accept_independently() {
        let mut gate = PhysicsTimestampGate::new();
        gate.seed_for_create_object(stamps(10, 10, 10, 10, 20, 5, 0, 10, 3));
        assert_eq!(
            gate.seed_for_create_object(stamps(9, 11, 11, 9, 20, 5, 0, 11, 3)),
            CreateDisposition::ExistingGeneration
        );
        assert!(!gate.try_accept_position_channel(3, 9));
        assert!(gate.try_accept_movement(3, 11, 5));
        assert!(gate.try_accept_state(3, 11));
        assert!(!gate.try_accept_vector(3, 9));
        assert!(gate.try_accept_obj_desc(3, 11));
    }

    #[test]
    fn stale_server_control_drops_but_movement_still_advances() {
        let mut gate = PhysicsTimestampGate::new();
        seed_movement(&mut gate, 0, 0, 0);
        assert!(gate.try_accept_movement(0, 1, 5));
        assert!(!gate.try_accept_movement(0, 2, 4));
        assert!(!gate.try_accept_movement(0, 2, 5));
        assert!(gate.try_accept_movement(0, 3, 5));
    }

    #[test]
    fn equal_server_control_passes() {
        let mut gate = PhysicsTimestampGate::new();
        seed_movement(&mut gate, 0, 0, 0);
        assert!(gate.try_accept_movement(0, 1, 7));
        assert!(gate.try_accept_movement(0, 2, 7));
    }

    #[test]
    fn other_instances_are_dropped_without_consuming_movement() {
        let mut gate = PhysicsTimestampGate::new();
        seed_movement(&mut gate, 2, 0, 0);
        assert!(gate.try_accept_movement(2, 1, 0));
        assert!(!gate.try_accept_movement(1, 2, 0));
        assert!(gate.try_accept_movement(2, 2, 0));

        let mut gate = PhysicsTimestampGate::new();
        seed_movement(&mut gate, 1, 0, 0);
        assert!(gate.try_accept_movement(1, 1, 0));
        assert!(!gate.try_accept_movement(2, 2, 0));
        assert!(gate.try_accept_movement(1, 2, 0));
    }

    #[test]
    fn instance_compare_wraps_around_the_seam() {
        let mut gate = PhysicsTimestampGate::new();
        seed_movement(&mut gate, 0xFFFE, 0, 0);
        assert!(gate.try_accept_movement(0xFFFE, 1, 0));
        assert!(!gate.try_accept_movement(2, 2, 0));
        assert_eq!(
            gate.seed_for_create_object(stamps(0, 1, 0, 0, 0, 0, 0, 0, 2)),
            CreateDisposition::NewGeneration
        );
        assert!(gate.try_accept_movement(2, 2, 0));
    }

    #[test]
    fn state_and_vector_advance_independently() {
        let mut gate = PhysicsTimestampGate::new();
        gate.seed_for_create_object(stamps(10, 20, 30, 40, 50, 60, 70, 80, 90));
        assert!(gate.try_accept_state(90, 31));
        assert!(!gate.try_accept_state(90, 31));
        assert!(gate.try_accept_vector(90, 41));
        assert!(!gate.try_accept_vector(90, 40));
    }

    #[test]
    fn position_rolls_back_when_a_newer_teleport_exists() {
        let mut gate = PhysicsTimestampGate::new();
        gate.seed_for_create_object(stamps(10, 0, 0, 0, 20, 0, 0, 0, 1));
        assert_eq!(
            gate.try_accept_position(1, 11, 19, 0, false),
            PositionDisposition::Rejected
        );
        assert_eq!(
            gate.try_accept_position(1, 11, 20, 0, false),
            PositionDisposition::Apply
        );
    }

    #[test]
    fn force_position_advances_its_own_channel() {
        let mut gate = PhysicsTimestampGate::new();
        gate.seed_for_create_object(stamps(10, 0, 0, 0, 20, 0, 30, 0, 1));
        assert_eq!(
            gate.try_accept_position(1, 9, 20, 31, true),
            PositionDisposition::ForcePosition
        );
        assert_eq!(gate.stamps().position, 9);
        assert_eq!(gate.stamps().teleport, 20);
        assert_eq!(
            gate.try_accept_position(1, 9, 20, 31, true),
            PositionDisposition::Rejected
        );
    }

    #[test]
    fn force_with_newer_teleport_does_not_consume_teleport() {
        for teleport_seed in [20, u16::MAX] {
            let incoming_teleport = teleport_seed.wrapping_add(1);
            let mut gate = PhysicsTimestampGate::new();
            gate.seed_for_create_object(stamps(10, 0, 0, 0, teleport_seed, 0, 30, 0, 1));
            assert_eq!(
                gate.try_accept_position(1, 11, incoming_teleport, 31, true),
                PositionDisposition::ForcePosition
            );
            assert_eq!(gate.stamps().position, 11);
            assert_eq!(gate.stamps().teleport, teleport_seed);
            assert_eq!(gate.stamps().force_position, 31);
        }
    }

    #[test]
    fn fresh_force_is_consumed_when_an_older_teleport_rejects_the_pose() {
        let mut gate = PhysicsTimestampGate::new();
        gate.seed_for_create_object(stamps(10, 0, 0, 0, 20, 0, 30, 0, 1));
        assert_eq!(
            gate.try_accept_position(1, 11, 19, 31, true),
            PositionDisposition::Rejected
        );
        assert_eq!(gate.stamps().position, 10);
        assert_eq!(gate.stamps().teleport, 20);
        assert_eq!(gate.stamps().force_position, 31);
    }

    #[test]
    fn parent_pickup_and_position_share_one_channel() {
        let mut gate = PhysicsTimestampGate::new();
        gate.seed_for_create_object(stamps(10, 0, 0, 0, 20, 0, 0, 0, 1));
        assert!(gate.try_accept_position_channel(1, 11));
        assert_eq!(
            gate.try_accept_position(1, 11, 20, 0, false),
            PositionDisposition::Rejected
        );
        assert!(!gate.try_accept_position_channel(1, 10));
        assert!(gate.try_accept_position_channel(1, 12));
    }

    #[test]
    fn new_generation_replaces_lower_stamps_wholesale() {
        let mut gate = PhysicsTimestampGate::new();
        gate.seed_for_create_object(stamps(1000, 1000, 1000, 1000, 1000, 1000, 1000, 1000, 1));
        assert_eq!(
            gate.seed_for_create_object(stamps(1, 1, 1, 1, 1, 1, 1, 1, 2)),
            CreateDisposition::NewGeneration
        );
        assert!(gate.try_accept_state(2, 2));
        assert!(gate.try_accept_vector(2, 2));
        assert_eq!(
            gate.try_accept_position(2, 2, 1, 1, false),
            PositionDisposition::Apply
        );
    }

    #[test]
    fn stale_create_and_delete_cannot_touch_the_current_generation() {
        let mut gate = PhysicsTimestampGate::new();
        gate.seed_for_create_object(stamps(1, 1, 1, 1, 1, 1, 1, 1, 2));
        assert_eq!(
            gate.seed_for_create_object(stamps(500, 500, 500, 500, 500, 500, 500, 500, 1)),
            CreateDisposition::StaleGeneration
        );
        assert!(!gate.try_accept_delete(1, false));
        assert!(gate.try_accept_delete(2, false));
        assert!(!gate.try_accept_delete(2, true));
    }

    #[test]
    fn obj_desc_needs_the_current_instance_and_a_newer_stamp() {
        let mut gate = PhysicsTimestampGate::new();
        gate.seed_for_create_object(stamps(0, 0, 0, 0, 0, 0, 0, 0xFFFE, 7));
        assert!(!gate.try_accept_obj_desc(6, 0xFFFF));
        assert!(gate.try_accept_obj_desc(7, 0xFFFF));
        assert!(gate.try_accept_obj_desc(7, 0));
        assert!(!gate.try_accept_obj_desc(7, 0));
    }

    #[test]
    fn teleport_start_checks_but_does_not_advance() {
        let mut gate = PhysicsTimestampGate::new();
        gate.seed_for_create_object(stamps(0, 0, 0, 0, 10, 0, 0, 0, 1));
        assert!(gate.is_fresh_teleport_start(10));
        assert!(!gate.is_fresh_teleport_start(9));
        assert!(gate.is_fresh_teleport_start(11));
        assert_eq!(gate.stamps().teleport, 10);
    }
}
