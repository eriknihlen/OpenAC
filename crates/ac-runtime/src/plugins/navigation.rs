//! API group I: navigation (OpenAC `RuntimeNavigationAutomation` and its
//! ownership part) over the movement owner (the body, held keys, moves) and
//! the navigation owner (walks and who owns them). Walks and pauses carry
//! the calling plugin's id: a plugin cannot take or stop a walk another
//! plugin started, the player's commands always win, and a plugin that goes
//! away takes its walk and pauses with it.

use std::collections::HashMap;
use std::sync::{Arc, Mutex, PoisonError};

use ac_core::properties::{PropertyBool, PropertyInt};
use ac_motion::move_to_math::get_heading;
use ac_nav::automation::{
    MapPosition, NavCommandStatus, PauseId, validate_arrival, validate_place,
};
use ac_nav::motor::{self, ScriptedMoveSnapshot, ScriptedMoveState};
use ac_nav::walk::{NavPlan, NavReach, PlanStatus, PreviewTarget, WalkReport, WalkState};
use ac_physics::movement::MovementInput;
use ac_physics::placement_room::PlacementRoom;
use ac_plugin::abi::Event;
use ac_plugin::abi::navigation::{
    CellReach, FindObject, GoToPlace, GoToReport, GoToState, MoveChannel, MoveDirection, MovePace,
    MoveProgress, MoveReport, MoveRequest, MoveState, MoveUnit, MovementIntent, NavigationObject,
    NavigationPlan, NavigationPlanStatus, NavigationReach, NavigationSnapshot, NavigationStatus,
    PathPreviewRequest, PauseChange, ReachRequest, RoomAhead, RoomAheadStatus,
};
use ac_plugin::abi::world::Position;
use glam::Vec3;

use super::PluginBinding;
use super::world::position_of;
use crate::navigation::{Answered, NavigationSurface};
use crate::session::SessionState;
use crate::world::World;

/// The public flag of a door.
const DOOR_FLAG: u32 = 0x0000_1000;
/// The physics state bit of something that does not collide (an open door).
const ETHEREAL_STATE: u32 = 0x0000_0004;

/// One plugin's pause on walks: what it needs the character for now.
#[derive(Debug)]
struct Pause {
    owner: String,
    id: PauseId,
    reason: Arc<Mutex<String>>,
}

/// What navigation keeps for plugins: the snapshot and walk report as last
/// published, the server confirmations counted, and the pauses by handle.
#[derive(Debug, Default)]
pub struct NavigationWatch {
    published: Option<NavigationSnapshot>,
    snapshot_revision: u64,
    confirmed: Option<(ac_proto::Position, u16)>,
    confirmed_revision: u64,
    report_revision: i64,
    pauses: HashMap<u32, Pause>,
    next_pause: u32,
}

/// The character's movement now, revision stamped: the revision grows
/// whenever any other field changes (OpenAC `Snapshot` and
/// `PublishSnapshotChanged`). Default while not in the world.
fn snapshot(state: &mut SessionState, in_world: bool) -> NavigationSnapshot {
    let raw = raw_snapshot(state, in_world);
    let watch = &mut state.plugin_services.navigation;
    let changed = watch.published.as_ref().is_none_or(|last| {
        let mut last = last.clone();
        last.revision = 0;
        last != raw
    });
    if changed {
        watch.snapshot_revision += 1;
        let mut stamped = raw;
        stamped.revision = watch.snapshot_revision;
        watch.published = Some(stamped);
    }
    watch.published.clone().unwrap_or_default()
}

fn raw_snapshot(state: &mut SessionState, in_world: bool) -> NavigationSnapshot {
    let Some(player) = state.world.player_guid().filter(|_| in_world) else {
        return NavigationSnapshot::default();
    };
    let Some(controller) = state.movement.controller() else {
        return NavigationSnapshot::default();
    };
    let cell = controller.current_cell_position();
    let live = Position::from_cell(
        cell.obj_cell_id,
        cell.frame.origin.to_array(),
        get_heading(cell.frame.orientation),
    );
    let is_moving =
        controller.body_velocity().length_squared() > 0.0001 || state.movement.has_intent();
    let is_airborne = controller.is_airborne();
    let portal = state.transit.snapshot();
    let accepted = state
        .world
        .object(player)
        .and_then(|o| o.accepted_position().map(|p| (*p, o.timestamps().position)));
    let watch = &mut state.plugin_services.navigation;
    if let Some(now) = accepted
        && watch.confirmed != Some(now)
    {
        watch.confirmed = Some(now);
        watch.confirmed_revision += 1;
    }
    let confirmed = accepted.map_or_else(|| live.clone(), |(p, _)| position_of(&p));
    NavigationSnapshot {
        is_available: true,
        is_portal_space: portal.kind != crate::world::transit::PortalKind::None
            && !portal.completed
            && !portal.cancelled,
        local_object_id: player,
        position: live,
        is_moving,
        is_airborne,
        confirmed_position: confirmed,
        confirmed_position_revision: if accepted.is_some() {
            watch.confirmed_revision
        } else {
            0
        },
        revision: 0,
    }
}

/// The navigation events of this pass (OpenAC `OnPeerTick`): the snapshot
/// when it changed, then the walk report when its revision moved.
pub fn follow(state: &mut SessionState, in_world: bool) -> Vec<Event> {
    let mut out = Vec::new();
    let before = state.plugin_services.navigation.snapshot_revision;
    let now = snapshot(state, in_world);
    if now.revision != before {
        out.push(Event::NavigationSnapshotChanged(now));
    }
    let report = go_to_report(state, in_world);
    let watch = &mut state.plugin_services.navigation;
    if report.revision != 0 && report.revision != watch.report_revision {
        watch.report_revision = report.revision;
        out.push(Event::NavigationChanged(report));
    }
    out
}

/// The path previews and reach searches that ended this pass, each for the
/// plugin that asked (OpenAC: the plugin's `PreviewPathAsync` task
/// completes).
pub fn follow_previews(state: &mut SessionState) -> Vec<(String, Event)> {
    state
        .navigation
        .take_finished_previews()
        .into_iter()
        .map(|(owner, handle, answered)| {
            let event = match answered {
                Answered::Plan(plan) => Event::NavigationPlan(project_plan(handle, plan)),
                Answered::Reach(reach) => Event::NavigationReach(project_reach(handle, reach)),
            };
            (owner, event)
        })
        .collect()
}

/// A point as plugins read it: map coordinates and elevation from metres
/// measured from the corner of the first landblock, with its cell and no
/// heading.
fn map_position(cell_id: u32, global: Vec3) -> Position {
    Position {
        cell_id,
        east_west: (f64::from(global.x) - 24_468.0) / 240.0,
        north_south: (f64::from(global.y) - 24_468.0) / 240.0,
        elevation: f64::from(global.z) / 240.0,
        heading_degrees: 0.0,
        is_outdoor: (1..=0x40).contains(&(cell_id & 0xFFFF)),
    }
}

fn plan_status(status: PlanStatus) -> NavigationPlanStatus {
    match status {
        PlanStatus::Routed => NavigationPlanStatus::Routed,
        PlanStatus::NoRoute => NavigationPlanStatus::NoRoute,
        PlanStatus::Unavailable => NavigationPlanStatus::Unavailable,
        PlanStatus::InvalidTarget => NavigationPlanStatus::InvalidTarget,
        PlanStatus::Failed => NavigationPlanStatus::Failed,
    }
}

/// A reach search's answer as plugins read it: each cell's place as map
/// coordinates.
#[must_use]
pub fn project_reach(handle: u32, reach: NavReach) -> NavigationReach {
    NavigationReach {
        handle,
        status: plan_status(reach.status),
        cells: reach
            .cells
            .iter()
            .map(|cell| CellReach {
                cell_id: cell.cell_id,
                route_meters: cell.route_meters,
                place: map_position(cell.cell_id, cell.place),
                by_drop: cell.by_drop,
            })
            .collect(),
        reason: reach.reason,
        grid_reused: reach.grid_reused,
        took_ms: reach.took_ms,
    }
}

/// A plan as plugins read it: each point as map coordinates with its cell
/// and no heading (OpenAC `RuntimeNavigationProjection.GlobalPosition`).
#[must_use]
pub fn project_plan(handle: u32, plan: NavPlan) -> NavigationPlan {
    NavigationPlan {
        handle,
        status: plan_status(plan.status),
        path: plan
            .path
            .iter()
            .map(|point| map_position(point.cell_id, point.global))
            .collect(),
        reason: plan.reason,
        length_meters: plan.length_meters,
        grid_reused: plan.grid_reused,
        took_ms: plan.took_ms,
    }
}

/// The latest walk with its owner and revision; default while not in the
/// world (OpenAC `GoToReportFor`).
fn go_to_report(state: &mut SessionState, in_world: bool) -> GoToReport {
    if !in_world || state.world.player_guid().is_none() {
        return GoToReport::default();
    }
    let projected = go_to_state(state.navigation.report().state);
    let (report, owner, revision) = state.navigation.owned_report(u32::from(projected.tag()));
    project_report(&report, projected, owner, revision)
}

pub(super) fn project_report(
    report: &WalkReport,
    state: GoToState,
    owner: Option<String>,
    revision: i64,
) -> GoToReport {
    GoToReport {
        sequence: report.sequence,
        state,
        object_id: report.object_id,
        remaining_meters: report.remaining_meters,
        replans: report.replans,
        reason: Some(report.reason.clone()).filter(|r| !r.is_empty()),
        blocked_by_object_id: report.blocked_by_object_id,
        revision,
        owner,
    }
}

pub(super) fn go_to_state(state: WalkState) -> GoToState {
    match state {
        WalkState::Planning => GoToState::Planning,
        WalkState::Walking => GoToState::Walking,
        WalkState::Waiting => GoToState::Waiting,
        WalkState::Arrived => GoToState::Arrived,
        WalkState::ArrivedWithoutSight => GoToState::ArrivedWithoutSight,
        WalkState::NoRoute => GoToState::NoRoute,
        WalkState::Blocked => GoToState::Blocked,
        WalkState::Stopped => GoToState::Stopped,
        WalkState::Interrupted => GoToState::Interrupted,
        WalkState::Lost => GoToState::Lost,
        WalkState::None | WalkState::Planned => GoToState::None,
    }
}

fn status(status: NavCommandStatus) -> NavigationStatus {
    match status {
        NavCommandStatus::Accepted => NavigationStatus::Accepted,
        NavCommandStatus::Rejected => NavigationStatus::Rejected,
        NavCommandStatus::Unavailable => NavigationStatus::Unavailable,
        NavCommandStatus::Held => NavigationStatus::Held,
    }
}

fn accepted(ok: bool) -> NavigationStatus {
    if ok {
        NavigationStatus::Accepted
    } else {
        NavigationStatus::Rejected
    }
}

/// An object's name: its item's, else its description's.
fn name_of(world: &World, object_id: u32) -> Option<String> {
    world
        .item(object_id)
        .map(|i| i.name.clone())
        .filter(|n| !n.is_empty())
        .or_else(|| world.name(object_id).map(str::to_owned))
}

/// An object with what the client knows of the door it is (OpenAC
/// `RuntimeNavigationProjection.Enrich`): a door stands open while the
/// world shows it passable (its Open property comes with an appraisal and
/// does not follow it afterwards); its lock is known once appraised.
fn enrich(world: &World, value: NavigationObject) -> NavigationObject {
    let item = world.item(value.object_id);
    let state = world.object(value.object_id).map(|o| o.physics().state);
    enrich_with(value, item, state)
}

/// [`enrich`] over an item and the physics state its record shows.
pub(super) fn enrich_with(
    mut value: NavigationObject,
    item: Option<&ac_core::items::ClientObject>,
    state: Option<u32>,
) -> NavigationObject {
    let Some(item) = item else {
        return value;
    };
    let flags = item.public_weenie_bitfield.map_or(0, |f| f.bits());
    let door = flags & DOOR_FLAG != 0;
    let bools = &item.properties.bools;
    let open = bools.get(&PropertyBool::OPEN.0).copied();
    let locked = bools.get(&PropertyBool::LOCKED.0).copied();
    value.is_door = door;
    value.is_open = match state {
        Some(state) if door => state & ETHEREAL_STATE != 0,
        _ => open == Some(true),
    };
    value.is_locked = locked == Some(true);
    value.has_lock_state = open.is_some() || locked.is_some() || item.last_appraisal_time_ms > 0;
    value.lock_difficulty = item.properties.get_int(PropertyInt::RESIST_LOCKPICK.0, 0);
    value
}

fn move_request(request: &MoveRequest) -> motor::MoveRequest {
    let direction = match request.direction {
        MoveDirection::Forward => motor::MoveDirection::Forward,
        MoveDirection::Backward => motor::MoveDirection::Backward,
        MoveDirection::StrafeLeft => motor::MoveDirection::StrafeLeft,
        MoveDirection::StrafeRight => motor::MoveDirection::StrafeRight,
        MoveDirection::TurnLeft => motor::MoveDirection::TurnLeft,
        MoveDirection::TurnRight => motor::MoveDirection::TurnRight,
    };
    motor::MoveRequest {
        direction,
        pace: match request.pace {
            MovePace::Walk => motor::MovePace::Walk,
            MovePace::Run => motor::MovePace::Run,
        },
        amount: request.amount,
        unit: match request.unit {
            MoveUnit::MetersOrDegrees => motor::MoveUnit::MetersOrDegrees,
            MoveUnit::Seconds => motor::MoveUnit::Seconds,
        },
    }
}

fn move_channel(channel: MoveChannel) -> motor::MoveChannel {
    match channel {
        MoveChannel::Travel => motor::MoveChannel::Travel,
        MoveChannel::Strafe => motor::MoveChannel::Strafe,
        MoveChannel::Turn => motor::MoveChannel::Turn,
    }
}

fn move_report(snapshot: &ScriptedMoveSnapshot) -> MoveReport {
    let progress = |c: motor::MoveChannelSnapshot| MoveProgress {
        sequence: c.sequence,
        state: match c.state {
            ScriptedMoveState::None => MoveState::None,
            ScriptedMoveState::Moving => MoveState::Moving,
            ScriptedMoveState::Completed => MoveState::Completed,
            ScriptedMoveState::Stopped => MoveState::Stopped,
            ScriptedMoveState::TimeLimit => MoveState::TimeLimit,
            ScriptedMoveState::Blocked => MoveState::Blocked,
            ScriptedMoveState::Interrupted => MoveState::Interrupted,
            ScriptedMoveState::Lost => MoveState::Lost,
        },
        direction: match c.request.direction {
            motor::MoveDirection::Forward => MoveDirection::Forward,
            motor::MoveDirection::Backward => MoveDirection::Backward,
            motor::MoveDirection::StrafeLeft => MoveDirection::StrafeLeft,
            motor::MoveDirection::StrafeRight => MoveDirection::StrafeRight,
            motor::MoveDirection::TurnLeft => MoveDirection::TurnLeft,
            motor::MoveDirection::TurnRight => MoveDirection::TurnRight,
        },
        pace: match c.request.pace {
            motor::MovePace::Walk => MovePace::Walk,
            motor::MovePace::Run => MovePace::Run,
        },
        amount: c.request.amount,
        unit: match c.request.unit {
            motor::MoveUnit::MetersOrDegrees => MoveUnit::MetersOrDegrees,
            motor::MoveUnit::Seconds => MoveUnit::Seconds,
        },
        covered: c.covered,
        elapsed_seconds: c.elapsed_seconds,
    };
    MoveReport {
        travel: progress(snapshot.travel),
        strafe: progress(snapshot.strafe),
        turn: progress(snapshot.turn),
        jump_sequence: snapshot.jump_sequence,
        jump_charging: snapshot.jump_charging,
    }
}

impl PluginBinding<'_> {
    /// Runs `act` over the walks and moves as `owner`; unavailable with no
    /// connection.
    fn navigate(
        &mut self,
        owner: &str,
        act: impl FnOnce(&mut crate::navigation::RuntimeSurface<'_, '_>) -> NavCommandStatus,
    ) -> NavigationStatus {
        let Some(link) = self.link.as_mut() else {
            return NavigationStatus::Unavailable;
        };
        if !self.facts.in_world {
            return NavigationStatus::Unavailable;
        }
        status(
            self.state
                .navigate(owner, link.outbox, link.active, link.clocks, act),
        )
    }

    pub(super) fn navigation_snapshot(&mut self) -> NavigationSnapshot {
        let in_world = self.in_world();
        snapshot(self.state, in_world)
    }

    pub(super) fn navigation_object(&self, object_id: u32) -> Option<NavigationObject> {
        if !self.in_world() || object_id == 0 {
            return None;
        }
        let world = &self.state.world;
        if Some(object_id) == world.player_guid() {
            let controller = self.state.movement.controller()?;
            let cell = controller.current_cell_position();
            return Some(NavigationObject {
                object_id,
                name: world
                    .item(object_id)
                    .map(|i| i.name.clone())
                    .unwrap_or_default(),
                position: Position::from_cell(
                    cell.obj_cell_id,
                    cell.frame.origin.to_array(),
                    get_heading(cell.frame.orientation),
                ),
                ..NavigationObject::default()
            });
        }
        let position = world.object(object_id)?.position()?;
        Some(enrich(
            world,
            NavigationObject {
                object_id,
                name: name_of(world, object_id).unwrap_or_else(|| format!("0x{object_id:08X}")),
                position: position_of(position),
                ..NavigationObject::default()
            },
        ))
    }

    pub(super) fn navigation_find(&self, request: &FindObject) -> Option<NavigationObject> {
        if !self.in_world()
            || request.name.trim().is_empty()
            || !request.maximum_distance_meters.is_finite()
            || request.maximum_distance_meters < 0.0
        {
            return None;
        }
        let world = &self.state.world;
        let mut nearest_distance = request.maximum_distance_meters;
        let mut nearest = None;
        for object in world.objects() {
            let object_id = object.guid();
            let name = name_of(world, object_id).unwrap_or_default();
            if !name.eq_ignore_ascii_case(&request.name) {
                continue;
            }
            let Some(position) = object.position() else {
                continue;
            };
            let candidate = position_of(position);
            let distance = request.near.horizontal_distance_metres(&candidate);
            if distance > nearest_distance {
                continue;
            }
            nearest_distance = distance;
            nearest = Some(enrich(
                world,
                NavigationObject {
                    object_id,
                    name,
                    position: candidate,
                    ..NavigationObject::default()
                },
            ));
        }
        nearest
    }

    pub(super) fn navigation_objects(&self) -> Vec<NavigationObject> {
        if !self.in_world() {
            return Vec::new();
        }
        let world = &self.state.world;
        let mut out: Vec<NavigationObject> = world
            .objects()
            .filter_map(|object| {
                let object_id = object.guid();
                let position = object.position()?;
                Some(enrich(
                    world,
                    NavigationObject {
                        object_id,
                        name: name_of(world, object_id)
                            .unwrap_or_else(|| format!("0x{object_id:08X}")),
                        position: position_of(position),
                        ..NavigationObject::default()
                    },
                ))
            })
            .collect();
        out.sort_by_key(|o| o.object_id);
        out
    }

    /// Room for a body the size of the character ahead of it, asked of the
    /// physics world the runtime owns, so every host answers from the same
    /// collision (OpenAC `CheckRoomAhead`). Unknown out of the world.
    pub(super) fn navigation_room_ahead(&mut self, distance_meters: f32) -> RoomAhead {
        if !self.in_world() {
            return RoomAhead::default();
        }
        let (result, orientation) = self.state.movement.room_ahead(distance_meters);
        match result.room {
            PlacementRoom::Clear => RoomAhead {
                status: RoomAheadStatus::Clear,
                position: Position::from_cell(
                    result.cell_id,
                    result.cell_local_position.to_array(),
                    get_heading(orientation),
                ),
            },
            PlacementRoom::Blocked => RoomAhead {
                status: RoomAheadStatus::Blocked,
                position: Position::default(),
            },
            PlacementRoom::Unknown => RoomAhead::default(),
        }
    }

    pub(super) fn navigation_set_intent(&mut self, intent: &MovementIntent) -> NavigationStatus {
        if !self.in_world() {
            return NavigationStatus::Unavailable;
        }
        accepted(self.state.movement.set_intent(MovementInput {
            forward: intent.forward,
            backward: intent.backward,
            strafe_left: intent.strafe_left,
            strafe_right: intent.strafe_right,
            turn_left: intent.turn_left,
            turn_right: intent.turn_right,
            run: intent.run,
            jump: intent.jump,
            ..MovementInput::default()
        }))
    }

    pub(super) fn navigation_clear_intent(&mut self) -> NavigationStatus {
        if !self.in_world() {
            return NavigationStatus::Unavailable;
        }
        // Clearing nothing is still taken, as OpenAC's command accepts it.
        self.state.movement.clear_intent();
        NavigationStatus::Accepted
    }

    pub(super) fn navigation_face(&mut self, owner: &str, heading: f32) -> NavigationStatus {
        self.navigate(owner, |s| s.face_heading(heading))
    }

    pub(super) fn navigation_move(
        &mut self,
        owner: &str,
        request: &MoveRequest,
    ) -> NavigationStatus {
        let request = move_request(request);
        self.navigate(owner, |s| s.move_by(request))
    }

    pub(super) fn navigation_stop_moving(
        &mut self,
        owner: &str,
        channel: Option<MoveChannel>,
    ) -> NavigationStatus {
        let channel = channel.map(move_channel);
        self.navigate(owner, |s| s.stop_moving(channel))
    }

    pub(super) fn navigation_jump(&mut self, owner: &str, power: f32) -> NavigationStatus {
        self.navigate(owner, |s| s.jump(power))
    }

    pub(super) fn navigation_move_report(&self) -> MoveReport {
        if !self.in_world() {
            return MoveReport::default();
        }
        move_report(&self.state.movement.scripted_snapshot())
    }

    pub(super) fn navigation_go_to(
        &mut self,
        owner: &str,
        (object_id, arrival): (u32, f32),
    ) -> NavigationStatus {
        self.navigate(owner, |s| s.go_to(object_id, arrival))
    }

    pub(super) fn navigation_go_to_place(
        &mut self,
        owner: &str,
        request: &GoToPlace,
    ) -> NavigationStatus {
        let p = &request.position;
        let place = MapPosition {
            cell_id: p.cell_id,
            east_west: p.east_west,
            north_south: p.north_south,
            elevation: p.elevation,
        };
        let arrival = request.arrival_meters;
        self.navigate(owner, |s| s.go_to_place(&place, arrival))
    }

    pub(super) fn navigation_stand_on(
        &mut self,
        owner: &str,
        (object_id, arrival): (u32, f32),
    ) -> NavigationStatus {
        self.navigate(owner, |s| s.stand_on(object_id, arrival))
    }

    pub(super) fn navigation_follow(
        &mut self,
        owner: &str,
        (player_id, buffer): (u32, f32),
    ) -> NavigationStatus {
        self.navigate(owner, |s| s.follow(player_id, buffer))
    }

    pub(super) fn navigation_stop_go_to(&mut self, owner: &str) -> NavigationStatus {
        self.navigate(owner, |s| s.stop_walk())
    }

    /// A path preview (OpenAC `PreviewPathAsync`): the handle the plan
    /// will carry; the plan reaches only this plugin.
    pub(super) fn navigation_preview_path(
        &mut self,
        owner: &str,
        request: &PathPreviewRequest,
    ) -> u32 {
        let arrival = request.arrival_meters;
        let (target, valid) = match &request.place {
            Some(p) => {
                let place = MapPosition {
                    cell_id: p.cell_id,
                    east_west: p.east_west,
                    north_south: p.north_south,
                    elevation: p.elevation,
                };
                let valid = validate_place(&place, arrival) == NavCommandStatus::Accepted;
                (
                    PreviewTarget::Place {
                        cell_id: place.cell_id,
                        local: place.landblock_local(),
                    },
                    valid,
                )
            }
            None => (
                PreviewTarget::Object(request.object_id),
                validate_arrival(request.object_id, arrival) == NavCommandStatus::Accepted,
            ),
        };
        let Some(link) = self.link.as_mut() else {
            return self.state.navigation.add_answered_preview(
                owner,
                PlanStatus::Unavailable,
                "navigation is unavailable",
            );
        };
        if !self.facts.in_world && valid {
            return self.state.navigation.add_answered_preview(
                owner,
                PlanStatus::Unavailable,
                "navigation is unavailable",
            );
        }
        self.state
            .navigate(owner, link.outbox, link.active, link.clocks, |s| {
                s.preview_path(target, arrival, valid)
            })
    }

    /// A reach search: the handle the answer will carry, or 0 when nothing
    /// can be searched from (no session, out of the world, in portal space
    /// or the air, not in a sealed dungeon, or before the game data walks
    /// need is loaded) or the distance is not above 0. The answer reaches
    /// only this plugin.
    pub(super) fn navigation_reachable_cells(
        &mut self,
        owner: &str,
        request: &ReachRequest,
    ) -> u32 {
        let above_zero = request.max_meters > 0.0;
        if !above_zero || !self.facts.in_world {
            return 0;
        }
        let Some(link) = self.link.as_mut() else {
            return 0;
        };
        let max_meters = request.max_meters;
        self.state
            .navigate(owner, link.outbox, link.active, link.clocks, |s| {
                s.reachable_cells(max_meters)
            })
    }

    pub(super) fn navigation_go_to_report(&mut self) -> GoToReport {
        let in_world = self.in_world();
        go_to_report(self.state, in_world)
    }

    /// A pause on walks for `owner`, needing the character for `reason`
    /// (empty: nothing yet). Its handle; 0 never names one.
    pub(super) fn navigation_pause(&mut self, owner: &str, reason: String) -> u32 {
        let shared = Arc::new(Mutex::new(reason));
        let read = Arc::clone(&shared);
        let id = self.state.navigation.add_pause(
            owner,
            Box::new(move || {
                let reason = read.lock().unwrap_or_else(PoisonError::into_inner);
                (!reason.trim().is_empty()).then(|| reason.clone())
            }),
        );
        let watch = &mut self.state.plugin_services.navigation;
        watch.next_pause = watch.next_pause.wrapping_add(1).max(1);
        let handle = watch.next_pause;
        watch.pauses.insert(
            handle,
            Pause {
                owner: owner.to_owned(),
                id,
                reason: shared,
            },
        );
        handle
    }

    pub(super) fn navigation_pause_set(&mut self, owner: &str, change: PauseChange) -> bool {
        let watch = &self.state.plugin_services.navigation;
        let Some(pause) = watch
            .pauses
            .get(&change.handle)
            .filter(|p| p.owner == owner)
        else {
            return false;
        };
        *pause.reason.lock().unwrap_or_else(PoisonError::into_inner) = change.reason;
        true
    }

    pub(super) fn navigation_pause_release(&mut self, owner: &str, handle: u32) -> bool {
        let watch = &mut self.state.plugin_services.navigation;
        if watch.pauses.get(&handle).is_none_or(|p| p.owner != owner) {
            return false;
        }
        let Some(pause) = watch.pauses.remove(&handle) else {
            return false;
        };
        self.state.navigation.remove_pause(pause.id)
    }

    /// A plugin went away: its walk stops and its pauses go.
    pub(super) fn navigation_release(&mut self, owner: &str) {
        self.state
            .plugin_services
            .navigation
            .pauses
            .retain(|_, p| p.owner != owner);
        self.state.navigation.release_owner(owner);
    }
}

/// Where the character stands now, when it is in the world (the
/// snapshot's live position).
pub(super) fn live_position(state: &mut SessionState, in_world: bool) -> Option<Position> {
    let raw = raw_snapshot(state, in_world);
    raw.is_available.then_some(raw.position)
}
