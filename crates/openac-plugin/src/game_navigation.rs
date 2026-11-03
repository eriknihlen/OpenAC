//! Typed [`Host`] methods for API group I: navigation.

use ac_plugin_abi::calls;
use ac_plugin_abi::navigation::{
    FindObject, GoToPlace, GoToReport, MoveChannel, MoveDirection, MovePace, MoveReport,
    MoveRequest, MoveUnit, MovementIntent, NavigationObject, NavigationSnapshot, NavigationStatus,
    PathPreviewRequest, PauseChange, ReachRequest, RoomAhead,
};
use ac_plugin_abi::world::Position;

use crate::Host;

/// A pause on the client's walks: while its reason is not empty, walks
/// stop where the character stands and wait. Release it with
/// [`Host::navigation_release_pause`] (the client releases it when the
/// plugin stops).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct WalkPause(pub u32);

/// I. Navigation.
impl Host<'_> {
    /// The character's movement now.
    pub fn navigation(&mut self) -> NavigationSnapshot {
        self.call::<calls::NavigationSnapshot>(&())
    }

    /// One object by id, with what is known of its door and lock.
    pub fn navigation_object(&mut self, object_id: u32) -> Option<NavigationObject> {
        self.call::<calls::NavigationObject>(&object_id)
    }

    /// The object nearest `near` named exactly `name` (any case), within
    /// `maximum_distance_meters` across the ground.
    pub fn navigation_find(
        &mut self,
        name: &str,
        near: Position,
        maximum_distance_meters: f64,
    ) -> Option<NavigationObject> {
        self.call::<calls::NavigationFind>(&FindObject {
            name: name.to_owned(),
            near,
            maximum_distance_meters,
        })
    }

    /// Every object whose position is known, by id.
    pub fn navigation_objects(&mut self) -> Vec<NavigationObject> {
        self.call::<calls::NavigationObjects>(&())
    }

    /// Looks for room to set a body the size of the character down
    /// `distance_meters` (above 0, at most 10; a summoned pet's usual
    /// distance is 3) straight ahead of where it faces: the room a pet
    /// needs before it is summoned. The client asks its own collision the
    /// way it places any object entering the world, without letting the
    /// body slide aside, so a wall, a building, rising terrain or a door in
    /// that spot blocks it; creatures and players standing there do not.
    /// Ground up to 70 cm above and about 66 cm below the feet is tried
    /// too. Nothing moves.
    /// [`RoomAheadStatus::Unknown`](ac_plugin_abi::navigation::RoomAheadStatus::Unknown)
    /// when the client
    /// cannot look (out of the world, the spot not loaded, the distance out
    /// of range); a plugin that must not stall can treat that as room.
    pub fn check_room_ahead(&mut self, distance_meters: f32) -> RoomAhead {
        self.call::<calls::NavigationRoomAhead>(&distance_meters)
    }

    /// Hold movement keys until replaced or cleared.
    pub fn set_movement_intent(&mut self, intent: MovementIntent) -> NavigationStatus {
        self.call::<calls::NavigationSetIntent>(&intent)
    }

    /// Release the held movement keys.
    pub fn clear_movement_intent(&mut self) -> NavigationStatus {
        self.call::<calls::NavigationClearIntent>(&())
    }

    /// Turn on the spot to a compass heading (degrees clockwise from north).
    pub fn face_heading(&mut self, heading_degrees: f32) -> NavigationStatus {
        self.call::<calls::NavigationFace>(&heading_degrees)
    }

    /// Begin a move the client carries out: `amount` counts `unit`; 0
    /// keeps going until stopped (at most 30 s).
    pub fn move_by(
        &mut self,
        direction: MoveDirection,
        pace: MovePace,
        amount: f32,
        unit: MoveUnit,
    ) -> NavigationStatus {
        self.call::<calls::NavigationMove>(&MoveRequest {
            direction,
            pace,
            amount,
            unit,
        })
    }

    /// End every move under way.
    pub fn stop_moving(&mut self) -> NavigationStatus {
        self.call::<calls::NavigationStopMoving>(&None)
    }

    /// End the move on one channel.
    pub fn stop_moving_channel(&mut self, channel: MoveChannel) -> NavigationStatus {
        self.call::<calls::NavigationStopMoving>(&Some(channel))
    }

    /// Jump with `power` of a full charge (above 0, at most 1).
    pub fn jump(&mut self, power: f32) -> NavigationStatus {
        self.call::<calls::NavigationJump>(&power)
    }

    /// The latest move on each channel and the latest jump.
    pub fn move_report(&mut self) -> MoveReport {
        self.call::<calls::NavigationMoveReport>(&())
    }

    /// Walk to an object along a planned route, ending within
    /// `arrival_meters` (above 0, at most 50) facing it.
    pub fn go_to(&mut self, object_id: u32, arrival_meters: f32) -> NavigationStatus {
        self.call::<calls::NavigationGoTo>(&(object_id, arrival_meters))
    }

    /// Walk to a place, ending on the floor it stands on. A place in a
    /// dungeon or building cell within 0.5 m of a floor or of the top of an
    /// object such as a platform, over or under it, is placed on that
    /// surface; any other place in such a cell drops to the floor under it,
    /// or to the floor of a cell below where its own cell has none there
    /// (the upper half of a tall room).
    pub fn go_to_place(&mut self, position: Position, arrival_meters: f32) -> NavigationStatus {
        self.call::<calls::NavigationGoToPlace>(&GoToPlace {
            position,
            arrival_meters,
        })
    }

    /// Walk onto an object's top.
    pub fn stand_on(&mut self, object_id: u32, arrival_meters: f32) -> NavigationStatus {
        self.call::<calls::NavigationStandOn>(&(object_id, arrival_meters))
    }

    /// Follow a player, keeping within `buffer_meters` behind them.
    pub fn follow(&mut self, player_id: u32, buffer_meters: f32) -> NavigationStatus {
        self.call::<calls::NavigationFollow>(&(player_id, buffer_meters))
    }

    /// End the walk this plugin started.
    pub fn stop_go_to(&mut self) -> NavigationStatus {
        self.call::<calls::NavigationStopGoTo>(&())
    }

    /// The latest walk.
    pub fn go_to_report(&mut self) -> GoToReport {
        self.call::<calls::NavigationGoToReport>(&())
    }

    /// Plan a path to an object (ending within `arrival_meters` of it)
    /// without moving the character, owning navigation or touching a walk
    /// under way. Returns the preview's handle; the plan arrives as
    /// `Event::NavigationPlan` with that handle on a later event pass. The
    /// collision loaded now is what is searched, so a plan may go stale.
    pub fn preview_path(&mut self, object_id: u32, arrival_meters: f32) -> u32 {
        self.call::<calls::NavigationPreviewPath>(&PathPreviewRequest {
            object_id,
            place: None,
            arrival_meters,
        })
    }

    /// [`Host::preview_path`] to a place (an elevation of NaN takes the
    /// ground there), ending on the floor the place stands on. A place in a
    /// dungeon or building cell is placed as [`Self::go_to_place`] places
    /// it, so a dungeon cell's centre may be given as it is.
    pub fn preview_path_to_place(&mut self, place: Position, arrival_meters: f32) -> u32 {
        self.call::<calls::NavigationPreviewPath>(&PathPreviewRequest {
            object_id: 0,
            place: Some(place),
            arrival_meters,
        })
    }

    /// Every cell of the sealed dungeon the character stands in that it can
    /// walk to along routes of at most `max_meters` (`f32::INFINITY` for
    /// all of them), each with its route's length, from one search (API
    /// 1.5). Returns the search's handle, or 0 when nothing can be searched
    /// from (out of the world, in portal space or the air, not in a sealed
    /// dungeon, or before the game data walks need is loaded) or
    /// `max_meters` is not above 0. The answer arrives as `Event::NavigationReach` with
    /// that handle on a later event pass, nearest by route first; a cell it
    /// leaves out cannot be walked to from here now. The search runs on the
    /// client's worker over the grid walks and previews use.
    pub fn reachable_cells(&mut self, max_meters: f32) -> u32 {
        self.call::<calls::NavigationReachableCells>(&ReachRequest { max_meters })
    }

    /// Have walks wait while this plugin needs the character: `reason`
    /// (empty for nothing yet) says for what. None when refused.
    pub fn navigation_pause(&mut self, reason: &str) -> Option<WalkPause> {
        let handle = self.call::<calls::NavigationPause>(&reason.to_owned());
        (handle != 0).then_some(WalkPause(handle))
    }

    /// Change what a pause needs the character for (empty: nothing now).
    pub fn navigation_set_pause(&mut self, pause: WalkPause, reason: &str) -> bool {
        self.call::<calls::NavigationPauseSet>(&PauseChange {
            handle: pause.0,
            reason: reason.to_owned(),
        })
    }

    /// Remove a pause.
    pub fn navigation_release_pause(&mut self, pause: WalkPause) -> bool {
        self.call::<calls::NavigationPauseRelease>(&pause.0)
    }
}
