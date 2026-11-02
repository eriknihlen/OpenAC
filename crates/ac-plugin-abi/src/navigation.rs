//! Where the character and the objects around it stand, the movement keys a
//! plugin holds, moves the client carries out, and walks it plans (API
//! group I).

use crate::world::Position;
use crate::{wire_enum, wire_record};

wire_record! {
    /// The local character's movement, sampled once.
    pub struct NavigationSnapshot {
        /// The character is in the world and the other fields are real.
        pub is_available: bool,
        /// In portal space, between leaving one place and arriving.
        pub is_portal_space: bool,
        /// The character's object id.
        pub local_object_id: u32,
        /// Where the body stands now (predicted locally every pass).
        pub position: Position,
        /// Moving, or holding a movement input.
        pub is_moving: bool,
        /// No ground under foot.
        pub is_airborne: bool,
        /// The last position the server confirmed (`position` until one
        /// is).
        pub confirmed_position: Position,
        /// Grows with every confirmation; 0 before the first.
        pub confirmed_position_revision: u64,
        /// Grows whenever any other field of the snapshot changes.
        pub revision: u64,
    }
}

wire_record! {
    /// A world object as navigation sees it.
    pub struct NavigationObject {
        /// Its object id.
        pub object_id: u32,
        /// Its name, or empty before the client has one.
        pub name: String,
        /// Where it stands.
        pub position: Position,
        /// It is a door.
        pub is_door: bool,
        /// It stands open (a door: the world shows it passable).
        pub is_open: bool,
        /// It is locked.
        pub is_locked: bool,
        /// The open or locked state is known (not just "never reported").
        pub has_lock_state: bool,
        /// How hard its lock is to pick, 0 for none.
        pub lock_difficulty: i32,
    }
}

wire_record! {
    /// Find the object nearest a position by its exact name, any case.
    pub struct FindObject {
        /// The name.
        pub name: String,
        /// Where distances are measured from.
        pub near: Position,
        /// How far to search across the ground, metres.
        pub maximum_distance_meters: f64,
    }
}

wire_record! {
    /// The movement keys a plugin holds until it replaces or clears them.
    pub struct MovementIntent {
        /// Forward.
        pub forward: bool,
        /// Back up.
        pub backward: bool,
        /// Sidestep left.
        pub strafe_left: bool,
        /// Sidestep right.
        pub strafe_right: bool,
        /// Turn left.
        pub turn_left: bool,
        /// Turn right.
        pub turn_right: bool,
        /// Run rather than walk.
        pub run: bool,
        /// Jump.
        pub jump: bool,
    }
}

impl MovementIntent {
    /// No keys, running (OpenAC's default intent runs).
    #[must_use]
    pub fn running() -> Self {
        Self {
            run: true,
            ..Self::default()
        }
    }
}

wire_enum! {
    /// How a navigation command was answered.
    pub enum NavigationStatus: "navigation status" {
        /// No character in the world, or no navigation.
        Unavailable = 0,
        /// Taken; watch its report.
        Accepted = 1,
        /// The arguments are wrong, or the client refused it.
        Rejected = 2,
        /// Another owner's walk is under way.
        Held = 3,
    }
}

wire_enum! {
    /// Which way a move goes.
    pub enum MoveDirection: "move direction" {
        /// Forward.
        Forward = 0,
        /// Backward.
        Backward = 1,
        /// Sidestep left.
        StrafeLeft = 2,
        /// Sidestep right.
        StrafeRight = 3,
        /// Turn left on the spot.
        TurnLeft = 4,
        /// Turn right on the spot.
        TurnRight = 5,
    }
}

wire_enum! {
    /// How fast a move goes.
    pub enum MovePace: "move pace" {
        /// Walking.
        Walk = 0,
        /// Running.
        Run = 1,
    }
}

wire_enum! {
    /// What a move's amount counts.
    pub enum MoveUnit: "move unit" {
        /// Metres (degrees for a turn).
        MetersOrDegrees = 0,
        /// Seconds.
        Seconds = 1,
    }
}

wire_enum! {
    /// The parts of movement that combine like held keys; each carries one
    /// move.
    pub enum MoveChannel: "move channel" {
        /// Forward and backward.
        Travel = 0,
        /// Sidestepping.
        Strafe = 1,
        /// Turning.
        Turn = 2,
    }
}

wire_enum! {
    /// Where a channel's latest move stands.
    pub enum MoveState: "move state" {
        /// No move asked for.
        None = 0,
        /// Under way.
        Moving = 1,
        /// It covered its amount.
        Completed = 2,
        /// A stop ended it.
        Stopped = 3,
        /// Time ran out.
        TimeLimit = 4,
        /// It stopped making progress.
        Blocked = 5,
        /// The player moved the character.
        Interrupted = 6,
        /// Portal space or leaving the world ended it.
        Lost = 7,
    }
}

wire_record! {
    /// A move the client carries out and ends by itself.
    pub struct MoveRequest {
        /// Which way.
        pub direction: MoveDirection,
        /// How fast.
        pub pace: MovePace,
        /// How much; 0 keeps going until stopped (at most 30 s).
        pub amount: f32,
        /// What the amount counts.
        pub unit: MoveUnit,
    }
}

wire_record! {
    /// One channel's latest move.
    pub struct MoveProgress {
        /// Grows by one for every move begun on any channel.
        pub sequence: i64,
        /// Where it stands.
        pub state: MoveState,
        /// Which way.
        pub direction: MoveDirection,
        /// How fast.
        pub pace: MovePace,
        /// How much was asked.
        pub amount: f32,
        /// What the amount counts.
        pub unit: MoveUnit,
        /// How far it got: metres, degrees for a turn.
        pub covered: f32,
        /// How long it has taken, seconds.
        pub elapsed_seconds: f32,
    }
}

wire_record! {
    /// The latest move on each channel, and the latest jump.
    pub struct MoveReport {
        /// Forward and backward.
        pub travel: MoveProgress,
        /// Sidestepping.
        pub strafe: MoveProgress,
        /// Turning.
        pub turn: MoveProgress,
        /// Grows by one for every jump begun.
        pub jump_sequence: i64,
        /// A jump is charging.
        pub jump_charging: bool,
    }
}

impl MoveReport {
    /// The latest move on one channel.
    #[must_use]
    pub fn channel(&self, channel: MoveChannel) -> &MoveProgress {
        match channel {
            MoveChannel::Travel => &self.travel,
            MoveChannel::Strafe => &self.strafe,
            MoveChannel::Turn => &self.turn,
        }
    }

    /// The channel a move in `direction` occupies.
    #[must_use]
    pub fn channel_of(direction: MoveDirection) -> MoveChannel {
        match direction {
            MoveDirection::Forward | MoveDirection::Backward => MoveChannel::Travel,
            MoveDirection::StrafeLeft | MoveDirection::StrafeRight => MoveChannel::Strafe,
            MoveDirection::TurnLeft | MoveDirection::TurnRight => MoveChannel::Turn,
        }
    }
}

wire_enum! {
    /// Where a walk the client plans stands.
    pub enum GoToState: "go-to state" {
        /// No walk asked for.
        None = 0,
        /// Building the grid or searching it for a route.
        Planning = 1,
        /// Walking the route.
        Walking = 2,
        /// Reached the goal within the distance asked.
        Arrived = 3,
        /// No route joins the character to the goal.
        NoRoute = 4,
        /// Stopped making progress, even after planning again.
        Blocked = 5,
        /// A stop, or a later walk, ended it.
        Stopped = 6,
        /// The player moved the character.
        Interrupted = 7,
        /// Portal space or leaving the world ended it.
        Lost = 8,
        /// Stopped where the character stands while something else needs
        /// it; plans on once nothing has for a moment.
        Waiting = 9,
        /// Ended at the nearest reachable spot, from which the goal cannot
        /// be seen. Not an arrival.
        ArrivedWithoutSight = 10,
    }
}

wire_record! {
    /// The latest walk the client planned.
    pub struct GoToReport {
        /// Grows by one for every walk asked for.
        pub sequence: i64,
        /// Where it stands.
        pub state: GoToState,
        /// The object walked to, 0 for a place.
        pub object_id: u32,
        /// The route left while walking; once ended, the straight distance
        /// to the object (NaN when unknown). Metres.
        pub remaining_meters: f32,
        /// How often it planned again.
        pub replans: i32,
        /// What it is doing, or why it ended.
        pub reason: Option<String>,
        /// On a blocked walk, the object (a door that would not open)
        /// beside the spot it stuck; else 0.
        pub blocked_by_object_id: u32,
        /// Grows whenever the reported walk or its state changes.
        pub revision: i64,
        /// Who asked for the walk under way: a plugin's id, or `player`;
        /// none once no walk is under way.
        pub owner: Option<String>,
    }
}

wire_record! {
    /// Walk to a place.
    pub struct GoToPlace {
        /// Where (a cell of 0 places it by its map coordinates alone; an
        /// elevation of NaN stands it on the ground). A place in a dungeon
        /// or building cell within 0.5 m of a floor or of the top of an
        /// object such as a platform, over or under it, is placed on that
        /// surface; any other place in such a cell drops to the floor under
        /// it, or to the floor of a cell below where its own cell has none
        /// there.
        pub position: Position,
        /// End within this many metres (above 0, at most 50).
        pub arrival_meters: f32,
    }
}

wire_enum! {
    /// What a look for room ahead found.
    pub enum RoomAheadStatus: "room-ahead status" {
        /// The client could not look: no character in the world, no body
        /// or no known shape yet, the spot is not loaded, the distance is
        /// out of range, or the client does not offer the check.
        Unknown = 0,
        /// A body the size of the character fits at the spot.
        Clear = 1,
        /// Something solid is in the way at every height tried.
        Blocked = 2,
    }
}

wire_record! {
    /// Whether a body the size of the character fits straight ahead.
    pub struct RoomAhead {
        /// Whether there is room, or that the client could not tell.
        pub status: RoomAheadStatus,
        /// Where the body would stand when clear, facing the way the
        /// character faces; the default position otherwise.
        pub position: Position,
    }
}

impl RoomAhead {
    /// True when the check found room.
    #[must_use]
    pub fn is_clear(&self) -> bool {
        self.status == RoomAheadStatus::Clear
    }
}

wire_record! {
    /// A path to plan without walking it
    /// ([`crate::calls::NavigationPreviewPath`]): to an object (ending
    /// beside it) or, with a place, to that place (ending on its floor; an
    /// elevation of NaN takes the ground there), a place in a dungeon or
    /// building cell placed as [`GoToPlace`] places it.
    pub struct PathPreviewRequest {
        /// The object; ignored with a place.
        pub object_id: u32,
        /// The place, instead of an object.
        pub place: Option<Position>,
        /// End within this many metres (above 0, at most 50).
        pub arrival_meters: f32,
    }
}

wire_enum! {
    /// How a path preview ended (OpenAC `PluginNavigationPlanStatus`).
    pub enum NavigationPlanStatus: "navigation plan status" {
        /// A path to the destination was found.
        Routed = 0,
        /// No path to the destination could be planned.
        NoRoute = 1,
        /// The character or the navigation world is unavailable.
        Unavailable = 2,
        /// The destination or the arrival distance is invalid.
        InvalidTarget = 3,
        /// The grid build or the route search failed; the reason says how.
        Failed = 4,
    }
}

wire_record! {
    /// A previewed path ([`crate::Event::NavigationPlan`]; OpenAC
    /// `PluginNavigationPlan`): a snapshot of the collision loaded when it
    /// was asked for, which may go stale as the world changes.
    pub struct NavigationPlan {
        /// The handle [`crate::calls::NavigationPreviewPath`] returned.
        pub handle: u32,
        /// How it ended.
        pub status: NavigationPlanStatus,
        /// The route's points from the character toward the goal, each
        /// with its cell, map coordinates and elevation (heading 0); empty
        /// unless routed.
        pub path: Vec<Position>,
        /// Why, in words.
        pub reason: String,
        /// The route's length, metres.
        pub length_meters: f32,
        /// Whether the search ran over a grid already built (or being
        /// built) for a walk or an earlier preview, rather than one built
        /// for this preview alone.
        since (1, 5) pub grid_reused: bool,
        /// How long the client's worker spent on it, milliseconds: waiting
        /// for a grid under way, building one, and searching it. The time
        /// to answer the call itself is not in it.
        since (1, 5) pub took_ms: f32,
    }
}

wire_record! {
    /// A plugin's pause on walks: while its reason is not empty, walks wait.
    pub struct PauseChange {
        /// The handle [`crate::calls::NavigationPause`] returned.
        pub handle: u32,
        /// What the plugin needs the character for now; empty for nothing.
        pub reason: String,
    }
}
