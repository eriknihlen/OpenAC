//! World transit: the player leaving the world (log out with its hold) and
//! passing through portal space (a teleport from the server's start message
//! through the destination, the reveal, readiness, materialization and
//! completion), plus the login reveal.
//!
//! Reveals are numbered by their own generation, which never repeats within
//! a process. Every acknowledgement names the generation (and for a portal
//! the teleport counter and destination cell) it is for; a stale one is
//! refused, a wrong one counts as an invariant failure.

use std::collections::BTreeMap;
use std::time::Duration;

use ac_core::physics::is_newer;
use ac_proto::Position;

/// How long a reveal may wait before the "please wait" cue shows.
pub const RETAIL_WAIT_CUE_DELAY: Duration = Duration::from_secs(5);
/// The log-out hold, seconds.
pub const RETAIL_LOGOUT_HOLD_SECONDS: f64 = 3.0;
/// The extra hold for a player killer, seconds.
pub const RETAIL_PLAYER_KILLER_ADDITIONAL_HOLD_SECONDS: f64 = 20.0;

/// Where a log out is.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum LogoutStage {
    /// No log out.
    #[default]
    None,
    /// Asked for; the hold runs (the server plays the log-out motion).
    Requested,
    /// The hold ended; the log-out presentation runs.
    PresentationActive,
    /// The server confirmed.
    Confirmed,
}

/// What a reveal is for.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum PortalKind {
    /// No reveal.
    #[default]
    None,
    /// Entering the world.
    Login,
    /// Arriving through a portal (or a recall, or a respawn).
    Portal,
}

/// What a host reports about the destination.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct DestinationReadiness {
    /// The reveal it is for.
    pub generation: u64,
    /// The destination cell.
    pub destination_cell: u32,
    /// The cell is inside a building or dungeon.
    pub is_indoor: bool,
    /// The destination cannot be loaded; it counts as ready.
    pub is_unhydratable: bool,
    /// The landblock radius the host streams around an outdoor destination
    /// (0 indoors).
    pub required_render_radius: i32,
    /// The surroundings are drawn.
    pub is_render_neighborhood_ready: bool,
    /// The composite textures are made.
    pub are_composite_textures_ready: bool,
    /// Collision is loaded.
    pub is_collision_ready: bool,
}

impl DestinationReadiness {
    /// There is a destination.
    #[must_use]
    pub fn has_destination(&self) -> bool {
        self.destination_cell != 0
    }

    /// The destination may be shown.
    #[must_use]
    pub fn is_ready(&self) -> bool {
        self.has_destination()
            && (self.is_unhydratable
                || (self.is_render_neighborhood_ready
                    && self.are_composite_textures_ready
                    && self.is_collision_ready))
    }

    /// Ready in every way, as a host with nothing to load reports it.
    #[must_use]
    pub fn immediate(generation: u64, destination_cell: u32) -> Self {
        let indoor = is_indoor(destination_cell);
        Self {
            generation,
            destination_cell,
            is_indoor: indoor,
            is_unhydratable: false,
            required_render_radius: i32::from(!indoor),
            is_render_neighborhood_ready: true,
            are_composite_textures_ready: true,
            is_collision_ready: true,
        }
    }
}

/// What a window hears of the player's portal space, to show it: the
/// tunnel from the start, the destination once the session has it, and the
/// arrival.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PortalSpaceEvent {
    /// The server put the player into portal space (its teleport start was
    /// new).
    Entered {
        /// The teleport counter.
        teleport_sequence: u16,
    },
    /// The session has the destination and holds the arrival for the
    /// attached window: it tells the server only once the window answers
    /// with [`DestinationShown`] for this reveal (or detaches).
    Destination {
        /// The reveal's generation.
        generation: u64,
        /// The teleport counter.
        teleport_sequence: u16,
        /// The destination cell.
        destination_cell: u32,
    },
    /// The player arrived: the session placed it at the destination and
    /// told the server.
    Arrived {
        /// The teleport counter.
        teleport_sequence: u16,
        /// The destination cell.
        destination_cell: u32,
    },
    /// The character's entry into the world began (the enter request): a
    /// window shows portal space from now until the login destination is
    /// shown.
    LoginEntering,
    /// The player's object is in the world at `destination_cell` and the
    /// session holds login complete for the attached window: it tells the
    /// server only once the window answers with
    /// `SessionCommand::LoginShown` for this reveal (or detaches).
    LoginDestination {
        /// The login reveal's generation.
        generation: u64,
        /// The player's cell.
        destination_cell: u32,
    },
    /// The entry went back to character selection before the player was in
    /// the world: the window leaves portal space.
    LoginWithdrawn,
    /// The log-out hold ended: the window warps the world away into portal
    /// space, which holds until the session leaves the world.
    LogoutStarted,
}

/// A window's answer to [`PortalSpaceEvent::Destination`]: it has shown the
/// destination (its presentation ended), with the readiness its reveal rule
/// found. The session then completes the arrival.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct DestinationShown {
    /// The teleport counter.
    pub teleport_sequence: u16,
    /// The destination's readiness as the window found it (its generation
    /// and cell name the reveal).
    pub readiness: DestinationReadiness,
}

/// The current reveal.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct PortalSnapshot {
    /// The reveal's generation (0: none yet).
    pub generation: u64,
    /// What it is for.
    pub kind: PortalKind,
    /// The destination's readiness.
    pub readiness: DestinationReadiness,
    /// The player materialized at the destination.
    pub materialized: bool,
    /// The reveal finished.
    pub completed: bool,
    /// The reveal was abandoned.
    pub cancelled: bool,
    /// The world viewport was seen again.
    pub world_viewport_observed: bool,
    /// The world simulation may run.
    pub world_simulation_available: bool,
    /// Acknowledgements that broke the order.
    pub invariant_failure_count: u32,
    /// The wait cue showed.
    pub wait_cue_shown: bool,
    /// Portal arrivals that materialized.
    pub portal_materialization_count: u32,
}

impl PortalSnapshot {
    /// No reveal: the world runs.
    pub const IDLE: Self = Self {
        generation: 0,
        kind: PortalKind::None,
        readiness: DestinationReadiness {
            generation: 0,
            destination_cell: 0,
            is_indoor: false,
            is_unhydratable: false,
            required_render_radius: 0,
            is_render_neighborhood_ready: false,
            are_composite_textures_ready: false,
            is_collision_ready: false,
        },
        materialized: false,
        completed: false,
        cancelled: false,
        world_viewport_observed: false,
        world_simulation_available: true,
        invariant_failure_count: 0,
        wait_cue_shown: false,
        portal_materialization_count: 0,
    };

    /// The destination cell.
    #[must_use]
    pub fn destination_cell(&self) -> u32 {
        self.readiness.destination_cell
    }

    /// The destination is ready.
    #[must_use]
    pub fn is_ready(&self) -> bool {
        self.readiness.is_ready()
    }
}

/// A position the server sent the player, offered as a teleport's
/// destination.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct TeleportDestination {
    /// The player.
    pub guid: u32,
    /// Instance counter.
    pub instance_sequence: u16,
    /// Position counter.
    pub position_sequence: u16,
    /// Teleport counter.
    pub teleport_sequence: u16,
    /// Forced-position counter.
    pub force_position_sequence: u16,
    /// Where.
    pub position: Position,
}

impl TeleportDestination {
    /// The destination cell.
    #[must_use]
    pub fn cell_id(&self) -> u32 {
        self.position.cell_id
    }
}

/// The transit owner.
#[derive(Debug)]
pub struct WorldTransit {
    buffered_destinations: BTreeMap<u16, TeleportDestination>,
    snapshot: PortalSnapshot,
    next_generation: u64,
    last_teleport_start: Option<u16>,
    pending_teleport_start: Option<u16>,
    active_teleport: Option<u16>,
    destination_accepted: bool,
    accepted_destination: Option<TeleportDestination>,
    logout_stage: LogoutStage,
    logout_hold_elapsed: f64,
    logout_hold_required: f64,
    rejections: u32,
}

impl Default for WorldTransit {
    fn default() -> Self {
        Self::new()
    }
}

impl WorldTransit {
    /// Idle.
    #[must_use]
    pub fn new() -> Self {
        Self {
            buffered_destinations: BTreeMap::new(),
            snapshot: PortalSnapshot::IDLE,
            next_generation: 0,
            last_teleport_start: None,
            pending_teleport_start: None,
            active_teleport: None,
            destination_accepted: false,
            accepted_destination: None,
            logout_stage: LogoutStage::None,
            logout_hold_elapsed: 0.0,
            logout_hold_required: 0.0,
            rejections: 0,
        }
    }

    /// The current reveal.
    #[must_use]
    pub fn snapshot(&self) -> PortalSnapshot {
        self.snapshot
    }

    /// The world simulation may run (no reveal holds it).
    #[must_use]
    pub fn is_world_simulation_available(&self) -> bool {
        self.snapshot.world_simulation_available
    }

    /// A teleport is under way.
    #[must_use]
    pub fn is_teleport_active(&self) -> bool {
        self.active_teleport.is_some()
    }

    /// The active teleport's counter (0 when none).
    #[must_use]
    pub fn active_teleport_sequence(&self) -> u16 {
        self.active_teleport.unwrap_or(0)
    }

    /// A teleport start waits to be activated.
    #[must_use]
    pub fn has_pending_teleport_start(&self) -> bool {
        self.pending_teleport_start.is_some()
    }

    /// The active teleport has its destination.
    #[must_use]
    pub fn has_accepted_teleport_destination(&self) -> bool {
        self.accepted_destination.is_some()
    }

    /// Destinations held for a teleport start that has not arrived.
    #[must_use]
    pub fn buffered_teleport_destination_count(&self) -> usize {
        self.buffered_destinations.len()
    }

    /// Acknowledgements refused as stale (diagnostics).
    #[must_use]
    pub fn rejection_count(&self) -> u32 {
        self.rejections
    }

    /// Nothing under way.
    #[must_use]
    pub fn is_idle(&self) -> bool {
        let reveal_active =
            self.snapshot.generation != 0 && !self.snapshot.completed && !self.snapshot.cancelled;
        self.buffered_destinations.is_empty()
            && self.pending_teleport_start.is_none()
            && self.active_teleport.is_none()
            && self.accepted_destination.is_none()
            && !reveal_active
            && self.logout_stage == LogoutStage::None
    }

    // -- Log out -----------------------------------------------------------

    /// Where the log out is.
    #[must_use]
    pub fn logout_stage(&self) -> LogoutStage {
        self.logout_stage
    }

    /// A log out is under way.
    #[must_use]
    pub fn is_logout_active(&self) -> bool {
        self.logout_stage != LogoutStage::None
    }

    /// Ask to log out: refused during another log out and during a
    /// teleport. The hold is 3 s, 23 s for a player killer.
    pub fn try_begin_logout_request(&mut self, is_player_killer: bool) -> bool {
        if self.logout_stage != LogoutStage::None
            || self.active_teleport.is_some()
            || self.pending_teleport_start.is_some()
        {
            self.rejections += 1;
            return false;
        }
        self.logout_stage = LogoutStage::Requested;
        self.logout_hold_elapsed = 0.0;
        self.logout_hold_required = RETAIL_LOGOUT_HOLD_SECONDS
            + if is_player_killer {
                RETAIL_PLAYER_KILLER_ADDITIONAL_HOLD_SECONDS
            } else {
                0.0
            };
        true
    }

    /// Withdraw a log out still in its hold.
    pub fn cancel_logout_request(&mut self) -> bool {
        if self.logout_stage != LogoutStage::Requested {
            return false;
        }
        self.logout_stage = LogoutStage::None;
        self.logout_hold_elapsed = 0.0;
        self.logout_hold_required = 0.0;
        true
    }

    /// Run the hold; true once, when it ends and the presentation begins.
    pub fn advance_logout_hold(&mut self, delta_seconds: f64) -> bool {
        if self.logout_stage != LogoutStage::Requested || delta_seconds < 0.0 {
            return false;
        }
        self.logout_hold_elapsed += delta_seconds;
        if self.logout_hold_elapsed < self.logout_hold_required {
            return false;
        }
        self.logout_stage = LogoutStage::PresentationActive;
        true
    }

    /// The server confirmed (legal during the hold too).
    pub fn acknowledge_logout_confirmed(&mut self) -> bool {
        if !matches!(
            self.logout_stage,
            LogoutStage::Requested | LogoutStage::PresentationActive
        ) {
            return false;
        }
        self.logout_stage = LogoutStage::Confirmed;
        true
    }

    /// The log out finished.
    pub fn complete_logout(&mut self) -> bool {
        if self.logout_stage != LogoutStage::Confirmed {
            return false;
        }
        self.logout_stage = LogoutStage::None;
        self.logout_hold_elapsed = 0.0;
        self.logout_hold_required = 0.0;
        true
    }

    // -- Reveals -----------------------------------------------------------

    /// Begin the login reveal into `destination_cell`. Returns its
    /// generation.
    pub fn begin_login_reveal(&mut self, destination_cell: u32) -> u64 {
        self.begin_reveal(PortalKind::Login, destination_cell)
    }

    /// True when a portal reveal may begin for this teleport and cell.
    #[must_use]
    pub fn can_begin_portal_reveal(&self, teleport_sequence: u16, destination_cell: u32) -> bool {
        self.active_teleport == Some(teleport_sequence)
            && self.accepted_destination.is_some_and(|d| {
                d.teleport_sequence == teleport_sequence
                    && d.cell_id() != 0
                    && d.cell_id() == destination_cell
            })
    }

    /// Begin the portal reveal of the accepted destination. Returns its
    /// generation; the destination is consumed.
    pub fn try_begin_portal_reveal(
        &mut self,
        teleport_sequence: u16,
        destination_cell: u32,
    ) -> Option<u64> {
        if !self.can_begin_portal_reveal(teleport_sequence, destination_cell) {
            self.rejections += 1;
            return None;
        }
        let generation = self.begin_reveal(PortalKind::Portal, destination_cell);
        self.accepted_destination = None;
        Some(generation)
    }

    /// True when the host may place the player at this portal destination.
    #[must_use]
    pub fn can_place_portal_destination(
        &self,
        generation: u64,
        teleport_sequence: u16,
        destination_cell: u32,
    ) -> bool {
        self.is_current_portal_destination(generation, teleport_sequence, destination_cell)
    }

    fn begin_reveal(&mut self, kind: PortalKind, destination_cell: u32) -> u64 {
        self.next_generation += 1;
        let generation = self.next_generation;
        self.snapshot = PortalSnapshot {
            generation,
            kind,
            readiness: DestinationReadiness {
                generation,
                destination_cell,
                is_indoor: is_indoor(destination_cell),
                ..DestinationReadiness::default()
            },
            materialized: false,
            completed: false,
            cancelled: false,
            world_viewport_observed: false,
            world_simulation_available: false,
            wait_cue_shown: false,
            ..self.snapshot
        };
        generation
    }

    // -- Teleport correlation ----------------------------------------------

    /// True when a start with this counter is new.
    #[must_use]
    pub fn can_queue_teleport_start(&self, sequence: u16) -> bool {
        self.last_teleport_start
            .is_none_or(|last| is_newer(last, sequence))
    }

    /// The server's teleport start: held until the host can enter portal
    /// space. Older buffered destinations are dropped.
    pub fn try_queue_teleport_start(&mut self, sequence: u16) -> bool {
        if !self.can_queue_teleport_start(sequence) {
            return false;
        }
        self.prune_destinations_older_than(sequence);
        self.active_teleport = None;
        self.pending_teleport_start = Some(sequence);
        self.last_teleport_start = Some(sequence);
        self.destination_accepted = false;
        self.accepted_destination = None;
        true
    }

    /// The host entered portal space: the queued start becomes active, with
    /// the destination if it arrived first.
    pub fn activate_queued_teleport(&mut self) -> bool {
        let Some(sequence) = self.pending_teleport_start.take() else {
            return false;
        };
        self.active_teleport = Some(sequence);
        self.destination_accepted = false;
        self.accepted_destination = None;
        if let Some(buffered) = self.buffered_destinations.remove(&sequence) {
            self.destination_accepted = true;
            self.accepted_destination = Some(buffered);
        }
        true
    }

    /// A position for the player. It becomes the active teleport's
    /// destination (the first one only), or is held for a start that has
    /// not come yet when its teleport counter advanced.
    pub fn offer_teleport_destination(
        &mut self,
        destination: TeleportDestination,
        teleport_timestamp_advanced: bool,
    ) -> bool {
        let sequence = destination.teleport_sequence;
        if destination.cell_id() == 0 {
            return false;
        }
        if teleport_timestamp_advanced {
            self.prune_destinations_older_than(sequence);
        }
        if self.active_teleport == Some(sequence) {
            if self.destination_accepted {
                return false;
            }
            self.destination_accepted = true;
            self.accepted_destination = Some(destination);
            return true;
        }
        if self.pending_teleport_start == Some(sequence) {
            self.buffered_destinations
                .entry(sequence)
                .or_insert(destination);
            return false;
        }
        let correlation = self
            .pending_teleport_start
            .unwrap_or(self.active_teleport.unwrap_or(0));
        let may_belong_to_future_start = (self.active_teleport.is_none()
            && self.pending_teleport_start.is_none())
            || is_newer(correlation, sequence);
        if !teleport_timestamp_advanced || !may_belong_to_future_start {
            return false;
        }
        self.buffered_destinations
            .entry(sequence)
            .or_insert(destination);
        false
    }

    /// The active teleport's accepted destination.
    #[must_use]
    pub fn accepted_teleport_destination(&self) -> Option<TeleportDestination> {
        self.active_teleport.and(self.accepted_destination)
    }

    /// The teleport is over.
    pub fn end_teleport(&mut self) {
        self.active_teleport = None;
        self.pending_teleport_start = None;
        self.destination_accepted = false;
        self.accepted_destination = None;
    }

    // -- Reveal acknowledgements ---------------------------------------------

    /// The host's readiness for the current reveal. Accepted once; the
    /// indoor flag and render radius must match the cell.
    pub fn acknowledge_destination_readiness(&mut self, readiness: DestinationReadiness) -> bool {
        if !self.validate_active(readiness.generation, readiness.destination_cell) {
            return false;
        }
        if self.snapshot.readiness.is_ready() {
            return false;
        }
        let indoor = is_indoor(readiness.destination_cell);
        let radius_valid = if indoor {
            readiness.required_render_radius == 0
        } else {
            readiness.required_render_radius >= 1
        };
        if readiness.is_indoor != indoor || !radius_valid {
            self.fail_invariant();
            return false;
        }
        if self.snapshot.readiness == readiness {
            return false;
        }
        self.snapshot.readiness = readiness;
        true
    }

    /// The player materialized at the portal destination: the world
    /// simulation runs again.
    pub fn acknowledge_portal_materialized(
        &mut self,
        generation: u64,
        teleport_sequence: u16,
        destination_cell: u32,
    ) -> bool {
        if !self.is_current_portal_destination(generation, teleport_sequence, destination_cell) {
            self.rejections += 1;
            return false;
        }
        if !self.validate_active(generation, destination_cell) || self.snapshot.materialized {
            return false;
        }
        if !self.snapshot.readiness.is_ready() {
            self.fail_invariant();
            return false;
        }
        if self.snapshot.kind == PortalKind::Portal {
            self.snapshot.portal_materialization_count += 1;
        }
        self.snapshot.materialized = true;
        self.snapshot.world_simulation_available = true;
        true
    }

    /// The player materialized at the login destination.
    pub fn acknowledge_login_materialized(&mut self, generation: u64) -> bool {
        if generation == 0
            || generation != self.snapshot.generation
            || self.snapshot.kind != PortalKind::Login
        {
            self.rejections += 1;
            return false;
        }
        if !self.validate_active(generation, self.snapshot.destination_cell())
            || self.snapshot.materialized
        {
            return false;
        }
        if !self.snapshot.readiness.is_ready() {
            self.fail_invariant();
            return false;
        }
        self.snapshot.materialized = true;
        self.snapshot.world_simulation_available = true;
        true
    }

    /// The world viewport was seen again.
    pub fn acknowledge_world_viewport_visible(&mut self, generation: u64) -> bool {
        if !self.validate_generation(generation, true) || self.snapshot.world_viewport_observed {
            return false;
        }
        if !self.snapshot.readiness.is_ready() {
            self.fail_invariant();
            return false;
        }
        self.snapshot.world_viewport_observed = true;
        true
    }

    /// The reveal has waited `elapsed`; true from the 5 s cue on.
    pub fn observe_wait(&mut self, generation: u64, elapsed: Duration) -> bool {
        if generation == 0
            || generation != self.snapshot.generation
            || self.snapshot.cancelled
            || self.snapshot.completed
            || elapsed < RETAIL_WAIT_CUE_DELAY
        {
            return false;
        }
        self.snapshot.wait_cue_shown = true;
        true
    }

    /// The reveal finished: needs readiness, and a portal reveal needs its
    /// materialization first.
    pub fn complete(&mut self, generation: u64) -> bool {
        if !self.validate_generation(generation, true) || self.snapshot.completed {
            return false;
        }
        if !self.snapshot.readiness.is_ready()
            || (self.snapshot.kind == PortalKind::Portal && !self.snapshot.materialized)
        {
            self.fail_invariant();
            return false;
        }
        self.snapshot.completed = true;
        self.snapshot.world_simulation_available = true;
        true
    }

    /// Abandon the reveal; the world runs again.
    pub fn cancel(&mut self, generation: u64) -> bool {
        if generation == 0
            || generation != self.snapshot.generation
            || self.snapshot.cancelled
            || self.snapshot.completed
        {
            return false;
        }
        self.snapshot.cancelled = true;
        self.snapshot.world_simulation_available = true;
        true
    }

    /// The session ended: everything clears, the reveal generation keeps
    /// counting.
    pub fn reset_session(&mut self) {
        self.snapshot = PortalSnapshot::IDLE;
        self.end_teleport();
        self.last_teleport_start = None;
        self.buffered_destinations.clear();
        self.logout_stage = LogoutStage::None;
        self.logout_hold_elapsed = 0.0;
        self.logout_hold_required = 0.0;
    }

    fn is_current_portal_destination(
        &self,
        generation: u64,
        teleport_sequence: u16,
        destination_cell: u32,
    ) -> bool {
        self.active_teleport == Some(teleport_sequence)
            && generation != 0
            && generation == self.snapshot.generation
            && self.snapshot.kind == PortalKind::Portal
            && !self.snapshot.cancelled
            && !self.snapshot.completed
            && destination_cell != 0
            && destination_cell == self.snapshot.destination_cell()
    }

    fn validate_active(&mut self, generation: u64, destination_cell: u32) -> bool {
        if !self.validate_generation(generation, false) {
            return false;
        }
        if destination_cell == 0 || destination_cell != self.snapshot.destination_cell() {
            self.fail_invariant();
            return false;
        }
        true
    }

    fn validate_generation(&mut self, generation: u64, allow_completed: bool) -> bool {
        if generation == 0
            || generation != self.snapshot.generation
            || self.snapshot.cancelled
            || (!allow_completed && self.snapshot.completed)
        {
            self.rejections += 1;
            return false;
        }
        true
    }

    fn fail_invariant(&mut self) {
        self.snapshot.invariant_failure_count += 1;
    }

    fn prune_destinations_older_than(&mut self, sequence: u16) {
        self.buffered_destinations
            .retain(|&buffered, _| !is_newer(buffered, sequence));
    }
}

/// An indoor cell (a building interior or dungeon): its low word is 0x100
/// or more.
#[must_use]
pub fn is_indoor(cell_id: u32) -> bool {
    cell_id & 0xFFFF >= 0x0100
}

#[cfg(test)]
#[path = "transit_tests.rs"]
mod tests;
