//! The session's navigation owner: walks the client plans and walks
//! (`/nav`), and moves it carries out (`/motor`), over `ac-nav`.
//!
//! One [`WalkController`] per session, ticked once per pass before the local
//! player's movement input is gathered: it samples the body, plans routes
//! over grids captured from the session's physics world (on worker threads,
//! whose results it takes up on a later pass; the session itself stays
//! single-threaded), and drives the body with scripted moves, which the
//! movement owner's input source carries out. Doors on the way are
//! appraised quietly and used through the inventory's use, as a player's
//! click would. A walk waits while the character attacks.
//!
//! Who drives is arbitrated by [`WalkOwnership`]; until plugins land only
//! the player (chat commands) does.

mod chat;
pub(crate) use chat::NavigationSurface;
#[cfg(test)]
mod place_floor_tests;
mod sources;
#[cfg(test)]
mod tests;
#[cfg(test)]
pub(crate) mod walk_tests;

use std::sync::{Arc, Mutex, PoisonError};
use std::time::Duration;

use ac_dat::CachedDats;
use ac_nav::automation::{
    MapPosition, NavCommandStatus, PLAYER_OWNER, PauseId, PauseNeed, WalkOwnership,
    validate_arrival, validate_place,
};
use ac_nav::commands::NAME_SEARCH_METERS;
use ac_nav::motor::{MoveChannel, MoveRequest, ScriptedMoveSnapshot, ScriptedMovement};
use ac_nav::walk::{
    DEFAULT_ARRIVAL_METERS, NavPlan, NavReach, PathPreview, PlanMode, PlanStatus, PreviewTarget,
    ReachSearch, WalkController, WalkWorld,
};
use glam::Vec3;

pub use ac_nav::commands::{describe_move, describe_walk};
pub use ac_nav::view::{NavLine, NavLineKind};
pub use ac_nav::walk::{WalkReport, WalkState};
pub use chat::{DEBUG_PREFIX, MOTOR_VERB, NAV_VERB};

use crate::inventory::Inventory;
use crate::movement::Movement;
use crate::session::Outbox;
use crate::world::World;

/// What navigation tells a host.
#[derive(Debug, Clone, PartialEq)]
pub enum NavigationEvent {
    /// The walk's report moved to another state, or a new walk began.
    Report(WalkReport),
    /// A line the walk says about its progress (its diagnostics).
    Said(String),
}

/// What the session lends navigation for one call.
pub(crate) struct NavigationContext<'a> {
    /// The objects the client knows.
    pub world: &'a World,
    /// The physics and the local player's body.
    pub movement: &'a mut Movement,
    /// Quiet appraisals of doors.
    pub inventory: &'a mut Inventory,
    /// Where appraisals go.
    pub outbox: &'a mut Outbox,
    /// The character is in the world.
    pub in_world: bool,
    /// The character is in portal space.
    pub in_portal_space: bool,
    /// The character is attacking (a walk waits).
    pub attacking: bool,
    /// The selected object.
    pub selected: Option<u32>,
    /// Session time.
    pub now: Duration,
    /// Wall clock, UTC milliseconds (debug narration's timestamps).
    pub wall_unix_ms: i64,
}

/// The session's walks and the moves asked for by chat.
pub struct Navigation {
    walk: WalkController,
    ownership: WalkOwnership,
    mode: PlanMode,
    said: Arc<Mutex<Vec<String>>>,
    narration: Arc<Mutex<Vec<String>>>,
    chat: chat::ChatState,
    last_tick: Option<Duration>,
    last_reported: (i64, WalkState),
    door_uses: Vec<u32>,
    events: Vec<NavigationEvent>,
    /// A window is attached to draw the grid and routes.
    drawable: bool,
    /// The grid is shown (`/nav grid`, or a route asked for alone).
    grid_shown: bool,
    view: NavigationView,
    /// Path previews and reach searches plugins asked for.
    previews: Previews,
}

/// A search a plugin asked for, under way.
#[derive(Debug)]
enum Pending {
    Plan(PathPreview),
    Reach(ReachSearch),
}

impl Pending {
    fn is_finished(&self) -> bool {
        match self {
            Self::Plan(preview) => preview.is_finished(),
            Self::Reach(search) => search.is_finished(),
        }
    }

    fn take(self) -> Answered {
        match self {
            Self::Plan(preview) => Answered::Plan(preview.take()),
            Self::Reach(search) => Answered::Reach(search.take()),
        }
    }
}

/// What a search a plugin asked for found.
#[derive(Debug, Clone, PartialEq)]
pub(crate) enum Answered {
    /// A path preview's plan.
    Plan(NavPlan),
    /// A reach search's cells.
    Reach(NavReach),
}

/// Path previews and reach searches plugins asked for, by owner and
/// handle, oldest first. Handles count up across both and are never 0.
#[derive(Debug, Default)]
pub(crate) struct Previews {
    items: Vec<(String, u32, Pending)>,
    next: u32,
}

impl Previews {
    /// Keep a search for `owner` until it ends; its handle (never 0).
    fn add(&mut self, owner: &str, pending: Pending) -> u32 {
        self.next = self.next.wrapping_add(1).max(1);
        self.items.push((owner.to_owned(), self.next, pending));
        self.next
    }
}

/// What an attached window draws of navigation: the grid around the
/// character and the route, as segments whose origin is the south-west
/// corner of the landblock `frame_center` (the physics frame's centre).
/// Empty when there is nothing to draw.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct NavigationView {
    /// The landblock id (`0xXXYYFFFF`) whose corner is the segments.
    /// origin; 0 with no segments.
    pub frame_center: u32,
    /// The segments, metres, z up.
    pub lines: Vec<NavLine>,
}

impl std::fmt::Debug for Navigation {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("Navigation")
            .field("report", self.walk.report())
            .field("mode", &self.mode)
            .finish_non_exhaustive()
    }
}

impl Default for Navigation {
    fn default() -> Self {
        Self::new(PlanMode::Threads)
    }
}

fn sink(lines: &Arc<Mutex<Vec<String>>>) -> ac_nav::walk::Say {
    let lines = Arc::clone(lines);
    Box::new(move |line: &str| {
        lines
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .push(line.to_owned());
    })
}

fn take(lines: &Arc<Mutex<Vec<String>>>) -> Vec<String> {
    std::mem::take(&mut *lines.lock().unwrap_or_else(PoisonError::into_inner))
}

/// Whether a cell belongs to a sealed dungeon, read through the shared dat
/// cache.
fn sealed(dats: &CachedDats, cell_id: u32) -> bool {
    ac_nav::dungeon::is_sealed_dungeon(dats, cell_id)
}

impl Navigation {
    /// No walk yet; grids are built and routes searched as `mode` says
    /// (worker threads in a session, inline in tests).
    #[must_use]
    pub fn new(mode: PlanMode) -> Self {
        let said = Arc::new(Mutex::new(Vec::new()));
        Self {
            walk: WalkController::new(mode, Some(sink(&said))),
            ownership: WalkOwnership::new(),
            mode,
            said,
            narration: Arc::new(Mutex::new(Vec::new())),
            chat: chat::ChatState::default(),
            last_tick: None,
            last_reported: (0, WalkState::None),
            door_uses: Vec::new(),
            events: Vec::new(),
            drawable: false,
            grid_shown: false,
            view: NavigationView::default(),
            previews: Previews::default(),
        }
    }

    /// A preview whose answer is known at once (no character, no world):
    /// it is told like any other, on the owner's next event pass.
    pub(crate) fn add_answered_preview(
        &mut self,
        owner: &str,
        status: PlanStatus,
        reason: &str,
    ) -> u32 {
        self.previews.add(
            owner,
            Pending::Plan(PathPreview::ready(NavPlan::failed(status, reason))),
        )
    }

    /// The previews and reach searches that ended, with their owners and
    /// handles, oldest first.
    pub(crate) fn take_finished_previews(&mut self) -> Vec<(String, u32, Answered)> {
        let (done, pending): (Vec<_>, Vec<_>) = std::mem::take(&mut self.previews.items)
            .into_iter()
            .partition(|(_, _, preview)| preview.is_finished());
        self.previews.items = pending;
        done.into_iter()
            .map(|(owner, handle, preview)| (owner, handle, preview.take()))
            .collect()
    }

    /// Whether a window is attached to draw the grid and routes; without
    /// one nothing is drawn and the walk keeps no grid for the view.
    pub(crate) fn set_drawable(&mut self, drawable: bool) {
        self.drawable = drawable;
        if !drawable {
            self.view = NavigationView::default();
        }
    }

    /// What the attached window draws now.
    #[must_use]
    pub fn view(&self) -> &NavigationView {
        &self.view
    }

    /// Where the latest walk stands.
    #[must_use]
    pub fn report(&self) -> &WalkReport {
        self.walk.report()
    }

    /// Whether a walk is waiting, planning or walking.
    #[must_use]
    pub fn is_busy(&self) -> bool {
        self.walk.is_busy()
    }

    /// The walk controller (the window's grid and route views read it).
    #[must_use]
    pub fn walk(&self) -> &WalkController {
        &self.walk
    }

    /// Take what the host should hear.
    pub fn drain_events(&mut self) -> Vec<NavigationEvent> {
        std::mem::take(&mut self.events)
    }

    /// Take the chat lines the commands answered with.
    pub(crate) fn drain_replies(&mut self) -> Vec<String> {
        self.chat.drain_replies()
    }

    /// Take the doors the walk asked to use, for the session to use them
    /// through the inventory.
    pub(crate) fn take_door_uses(&mut self) -> Vec<u32> {
        std::mem::take(&mut self.door_uses)
    }

    /// The character left the world (or the generation ended): the walk
    /// and its grid are dropped, and a fresh controller waits for the next.
    pub fn left_world(&mut self) {
        let narrating = self.walk.narration.is_some();
        self.walk = WalkController::new(self.mode, Some(sink(&self.said)));
        if narrating {
            self.walk.narration = Some(sink(&self.narration));
        }
        self.ownership.forget_walk();
        self.last_tick = None;
        self.door_uses.clear();
    }

    /// Advances the walk by one pass, before the player's movement input is
    /// read.
    pub(crate) fn tick(&mut self, cx: &mut NavigationContext<'_>) {
        let elapsed = self
            .last_tick
            .map_or(0.0, |last| cx.now.saturating_sub(last).as_secs_f64());
        self.last_tick = Some(cx.now);
        if let Some(content) = cx.movement.content().cloned() {
            let steering = cx.movement.intent_steers();
            let now_ms = i64::try_from(cx.now.as_millis()).unwrap_or(i64::MAX);
            let parts = cx.movement.navigation_parts();
            let goals = sources::Goals {
                engine: parts.engine,
                residency: parts.residency,
                world: cx.world,
                controller: parts.controller,
                bodies: &parts.remote_positions,
            };
            let mut body = sources::Body {
                controller: parts.controller,
                input: parts.input,
                auto_run: parts.auto_run,
                in_portal_space: cx.in_portal_space,
            };
            let inventory = &mut *cx.inventory;
            let outbox = &mut *cx.outbox;
            let active = cx.in_world;
            let mut appraise = |id: u32| inventory.appraise_quietly(id, now_ms, active, outbox);
            let mut doors = sources::Doors {
                goals: &goals,
                uses: &mut self.door_uses,
                appraise: &mut appraise,
            };
            let ownership = &self.ownership;
            let attacking = cx.attacking;
            let paused_by = || {
                ownership
                    .pause_reason()
                    .or_else(|| attacking.then(|| "the character is attacking".to_owned()))
                    .or_else(|| steering.then(|| "a plugin is steering the character".to_owned()))
            };
            let dungeon = |cell: u32| sealed(content.dats(), cell);
            let mut world = WalkWorld {
                physics: parts.engine,
                body: &mut body,
                goals: &goals,
                doors: Some(&mut doors),
                paused_by: Some(&paused_by),
                is_sealed_dungeon: Some(&dungeon),
            };
            // The walk keeps a grid built around the character while the
            // grid is drawn.
            self.walk.show_grid = self.drawable && self.grid_shown;
            self.walk.tick(&mut world, elapsed);
            self.view = view_of(
                &self.walk,
                &goals,
                goals.residency.frame_center(),
                self.drawable,
                self.grid_shown,
            );
        } else {
            self.view = NavigationView::default();
        }
        for line in take(&self.said) {
            self.events.push(NavigationEvent::Said(line));
        }
        for line in take(&self.narration) {
            self.chat.narrate(&line, cx.wall_unix_ms);
        }
        self.follow_report();
        let Self {
            walk,
            ownership,
            narration,
            chat,
            grid_shown,
            drawable,
            previews,
            ..
        } = self;
        let surface = RuntimeSurface {
            owner: PLAYER_OWNER,
            walk,
            ownership,
            narration,
            grid_shown,
            drawable: *drawable,
            previews,
            cx,
        };
        chat.tick(&surface, surface.cx.wall_unix_ms);
    }

    /// Answers `/nav` and `/motor`; false for any other line.
    pub(crate) fn chat_command(&mut self, line: &str, cx: &mut NavigationContext<'_>) -> bool {
        let wall = cx.wall_unix_ms;
        let Self {
            walk,
            ownership,
            narration,
            chat,
            grid_shown,
            drawable,
            previews,
            ..
        } = self;
        let mut surface = RuntimeSurface {
            owner: PLAYER_OWNER,
            walk,
            ownership,
            narration,
            grid_shown,
            drawable: *drawable,
            previews,
            cx,
        };
        let handled = chat.handle(line, &mut surface, wall);
        self.follow_report();
        handled
    }

    /// Runs `act` over the walks and moves as `owner` asks for them (a
    /// plugin's id): a walk another owner started is held from it.
    pub(crate) fn as_owner<R>(
        &mut self,
        owner: &str,
        cx: &mut NavigationContext<'_>,
        act: impl FnOnce(&mut RuntimeSurface<'_, '_>) -> R,
    ) -> R {
        let Self {
            walk,
            ownership,
            narration,
            grid_shown,
            drawable,
            previews,
            ..
        } = self;
        let mut surface = RuntimeSurface {
            owner,
            walk,
            ownership,
            narration,
            grid_shown,
            drawable: *drawable,
            previews,
            cx,
        };
        let answer = act(&mut surface);
        self.follow_report();
        answer
    }

    /// The latest walk with who owns it and the report stream's revision,
    /// which grows whenever the walk or `state` (the state as the reader
    /// distinguishes states) changes.
    pub(crate) fn owned_report(&mut self, state: u32) -> (WalkReport, Option<String>, i64) {
        let (owner, revision) = self.ownership.report(&self.walk, state);
        (self.walk.report().clone(), owner, revision)
    }

    /// Has walks wait while `need` names something `owner` needs the
    /// character for.
    pub(crate) fn add_pause(&mut self, owner: &str, need: PauseNeed) -> PauseId {
        self.ownership.add_pause(owner, need)
    }

    /// Removes a pause; false when it was already gone.
    pub(crate) fn remove_pause(&mut self, id: PauseId) -> bool {
        self.ownership.remove_pause(id)
    }

    /// What a registered pause needs the character for now.
    #[cfg(test)]
    pub(crate) fn pause_reason(&self) -> Option<String> {
        self.ownership.pause_reason()
    }

    /// An owner went away: its pauses go and the walk it owns stops.
    pub(crate) fn release_owner(&mut self, owner: &str) {
        self.ownership.release(owner, Some(&mut self.walk));
        // Its previews are never told (a search under way runs out).
        self.previews.items.retain(|(o, _, _)| o != owner);
        self.follow_report();
    }

    fn follow_report(&mut self) {
        let report = self.walk.report();
        let now = (report.sequence, report.state);
        if now != self.last_reported && report.sequence != 0 {
            self.last_reported = now;
            self.events.push(NavigationEvent::Report(report.clone()));
        }
    }
}

/// The player's walks and moves over the runtime, as the commands see them
/// (OpenAC's `RuntimeNavigationAutomation` for the player owner).
pub(crate) struct RuntimeSurface<'a, 'b> {
    owner: &'a str,
    walk: &'a mut WalkController,
    ownership: &'a mut WalkOwnership,
    narration: &'a Arc<Mutex<Vec<String>>>,
    grid_shown: &'a mut bool,
    drawable: bool,
    previews: &'a mut Previews,
    cx: &'a mut NavigationContext<'b>,
}

impl RuntimeSurface<'_, '_> {
    /// Walks are possible: the dats are there and the character is in the
    /// world.
    fn can_walk(&self) -> bool {
        self.cx.in_world && self.cx.movement.content().is_some()
    }

    fn begin_walk(
        &mut self,
        validity: NavCommandStatus,
        start: impl FnOnce(&mut WalkController) -> i64,
    ) -> NavCommandStatus {
        if !self.can_walk() {
            return NavCommandStatus::Unavailable;
        }
        self.ownership
            .begin_walk(self.owner, self.walk, validity, start)
    }

    /// Start a path preview for the surface's owner (OpenAC
    /// `RuntimeNavigationAutomation.PreviewPathAsync`): the target and the
    /// arrival distance checked, the collision captured now, the search on
    /// the walk's planner. The handle the plan will carry.
    pub(crate) fn preview_path(
        &mut self,
        target: PreviewTarget,
        arrival_meters: f32,
        valid: bool,
    ) -> u32 {
        let preview = if !valid {
            PathPreview::ready(NavPlan::failed(
                PlanStatus::InvalidTarget,
                "invalid destination or arrival distance",
            ))
        } else if let Some(preview) = self.capture_preview(target, arrival_meters) {
            preview
        } else {
            PathPreview::ready(NavPlan::failed(
                PlanStatus::Unavailable,
                "navigation is unavailable",
            ))
        };
        self.previews.add(self.owner, Pending::Plan(preview))
    }

    /// Start a reach search for the surface's owner: every cell of the
    /// sealed dungeon the character stands in that it can walk to within
    /// `max_meters`, the search on the walk's planner. The handle the
    /// answer will carry, or 0 when nothing can be searched from: out of
    /// the world, in portal space or the air, or not in a sealed dungeon.
    pub(crate) fn reachable_cells(&mut self, max_meters: f32) -> u32 {
        match self.with_walk_world(|walk, world| walk.reachable_cells(world, max_meters)) {
            Some(Some(search)) => self.previews.add(self.owner, Pending::Reach(search)),
            _ => 0,
        }
    }

    fn capture_preview(
        &mut self,
        target: PreviewTarget,
        arrival_meters: f32,
    ) -> Option<PathPreview> {
        self.with_walk_world(|walk, world| walk.preview_path(world, target, arrival_meters))
    }

    /// Runs `ask` against the walk with the world as it stands, for a search
    /// that moves nothing (no doors, no pause); `None` while walks are not
    /// possible.
    fn with_walk_world<R>(
        &mut self,
        ask: impl FnOnce(&mut WalkController, &WalkWorld<'_>) -> R,
    ) -> Option<R> {
        if !self.can_walk() {
            return None;
        }
        let content = self.cx.movement.content().cloned()?;
        let in_portal_space = self.cx.in_portal_space;
        let parts = self.cx.movement.navigation_parts();
        let goals = sources::Goals {
            engine: parts.engine,
            residency: parts.residency,
            world: self.cx.world,
            controller: parts.controller,
            bodies: &parts.remote_positions,
        };
        let mut body = sources::Body {
            controller: parts.controller,
            input: parts.input,
            auto_run: parts.auto_run,
            in_portal_space,
        };
        let dungeon = |cell: u32| sealed(content.dats(), cell);
        let world = WalkWorld {
            physics: parts.engine,
            body: &mut body,
            goals: &goals,
            doors: None,
            paused_by: None,
            is_sealed_dungeon: Some(&dungeon),
        };
        Some(ask(self.walk, &world))
    }

    /// Where the character stands, in metres from the corner of the first
    /// landblock.
    fn here_position(&self) -> Option<(f64, f64)> {
        if !self.cx.in_world {
            return None;
        }
        let cell = self.cx.movement.controller()?.current_cell_position();
        (cell.obj_cell_id != 0).then(|| global_xy(cell.obj_cell_id, cell.frame.origin))
    }
}

impl chat::NavigationSurface for RuntimeSurface<'_, '_> {
    fn go_to(&mut self, object_id: u32, arrival_meters: f32) -> NavCommandStatus {
        let validity = validate_arrival(object_id, arrival_meters);
        self.begin_walk(validity, |walk| walk.walk_to(object_id, arrival_meters))
    }

    fn go_to_place(&mut self, place: &MapPosition, arrival_meters: f32) -> NavCommandStatus {
        let validity = validate_place(place, arrival_meters);
        let (cell, local) = (place.cell_id, place.landblock_local());
        self.begin_walk(validity, |walk| {
            walk.walk_to_place(cell, local, arrival_meters)
        })
    }

    fn stand_on(&mut self, object_id: u32, arrival_meters: f32) -> NavCommandStatus {
        let validity = validate_arrival(object_id, arrival_meters);
        self.begin_walk(validity, |walk| walk.stand_on(object_id, arrival_meters))
    }

    fn follow(&mut self, player_id: u32, buffer_meters: f32) -> NavCommandStatus {
        let validity = validate_arrival(player_id, buffer_meters);
        self.begin_walk(validity, |walk| walk.follow(player_id, buffer_meters))
    }

    fn stop_walk(&mut self) -> NavCommandStatus {
        if !self.can_walk() {
            return NavCommandStatus::Unavailable;
        }
        self.ownership.stop_walk(self.owner, self.walk)
    }

    fn walk_report(&self) -> WalkReport {
        self.walk.report().clone()
    }

    fn move_by(&mut self, request: MoveRequest) -> NavCommandStatus {
        if !self.cx.in_world {
            return NavCommandStatus::Unavailable;
        }
        if !ScriptedMovement::is_valid(&request) {
            return NavCommandStatus::Rejected;
        }
        status(self.cx.movement.begin_move(request))
    }

    fn stop_moving(&mut self, channel: Option<MoveChannel>) -> NavCommandStatus {
        if !self.cx.in_world {
            return NavCommandStatus::Unavailable;
        }
        match channel {
            None => {
                self.cx.movement.stop_moves();
                NavCommandStatus::Accepted
            }
            Some(channel) => status(self.cx.movement.stop_channel(channel)),
        }
    }

    fn jump(&mut self, power: f32) -> NavCommandStatus {
        if !self.cx.in_world {
            return NavCommandStatus::Unavailable;
        }
        status(self.cx.movement.begin_jump(power, None))
    }

    fn face_heading(&mut self, heading: f32) -> NavCommandStatus {
        if !self.cx.in_world {
            return NavCommandStatus::Unavailable;
        }
        status(self.cx.movement.turn_to_heading(heading))
    }

    fn move_report(&self) -> ScriptedMoveSnapshot {
        if self.cx.in_world {
            self.cx.movement.scripted_snapshot()
        } else {
            ScriptedMoveSnapshot::default()
        }
    }

    fn selected(&self) -> Option<u32> {
        self.cx.selected
    }

    fn here_cell(&self) -> Option<u32> {
        if !self.cx.in_world {
            return None;
        }
        self.cx
            .movement
            .controller()
            .map(|c| c.current_cell_position().obj_cell_id)
            .filter(|cell| *cell != 0)
    }

    fn is_available(&self) -> bool {
        self.here_cell().is_some()
    }

    fn find_object(&self, name: &str) -> Option<(u32, String)> {
        let here = self.here_position()?;
        let world = self.cx.world;
        let mut nearest = NAME_SEARCH_METERS;
        let mut found = None;
        for object in world.objects() {
            let guid = object.guid();
            let candidate = name_of(world, guid).unwrap_or_default();
            if !candidate.eq_ignore_ascii_case(name) {
                continue;
            }
            let Some(position) = object.position() else {
                continue;
            };
            let (x, y) = global_xy(
                position.cell_id,
                Vec3::new(position.origin.x, position.origin.y, position.origin.z),
            );
            let distance = ((x - here.0).powi(2) + (y - here.1).powi(2)).sqrt();
            if distance > nearest {
                continue;
            }
            nearest = distance;
            found = Some((guid, candidate));
        }
        found
    }

    fn name_of(&self, object_id: u32) -> Option<String> {
        if !self.cx.in_world || self.cx.world.object(object_id).is_none() {
            return None;
        }
        name_of(self.cx.world, object_id)
    }

    fn can_narrate(&self) -> bool {
        self.cx.movement.content().is_some()
    }

    fn narrate(&mut self, on: bool) {
        self.walk.narration = on.then(|| sink(self.narration));
    }

    fn can_draw(&self) -> bool {
        self.drawable
    }

    fn toggle_grid(&mut self) -> bool {
        *self.grid_shown = !*self.grid_shown;
        *self.grid_shown
    }

    fn preview_route(&mut self, object_id: u32) -> bool {
        // As OpenAC's window: asking for a route shows the grid with it.
        *self.grid_shown = true;
        self.walk.route_to(object_id, DEFAULT_ARRIVAL_METERS);
        true
    }
}

/// What the window draws: the grid while it is shown, and the route of a
/// walk under way or of a route asked for alone (OpenAC's
/// `WorldSceneDiagnosticsController` draws the navigation view when either
/// is there to show).
fn view_of(
    walk: &WalkController,
    goals: &sources::Goals<'_>,
    frame_center: Option<u32>,
    drawable: bool,
    grid_shown: bool,
) -> NavigationView {
    let Some(frame_center) = frame_center.filter(|_| drawable) else {
        return NavigationView::default();
    };
    if !grid_shown && !ac_nav::view::has_route_to_show(walk) {
        return NavigationView::default();
    }
    let character = goals
        .controller
        .filter(|c| c.cell_id() != 0)
        .map(|c| c.position());
    let portals = match character {
        Some(at) if grid_shown => walk.portals_near(goals, at, ac_nav::view::DRAW_RADIUS),
        _ => Vec::new(),
    };
    let lines = ac_nav::view::lines(
        &ac_nav::view::NavDrawState::of(walk),
        character,
        &portals,
        grid_shown,
    );
    if lines.is_empty() {
        return NavigationView::default();
    }
    NavigationView {
        frame_center,
        lines,
    }
}

/// An object's name: its item's, else the one its description gave.
fn name_of(world: &World, object_id: u32) -> Option<String> {
    world
        .item(object_id)
        .map(|i| i.name.clone())
        .filter(|n| !n.is_empty())
        .or_else(|| world.name(object_id).map(str::to_owned))
}

fn status(accepted: bool) -> NavCommandStatus {
    if accepted {
        NavCommandStatus::Accepted
    } else {
        NavCommandStatus::Rejected
    }
}

/// A point's position measured from the corner of the first landblock,
/// metres (east, north).
fn global_xy(cell_id: u32, local: Vec3) -> (f64, f64) {
    (
        f64::from((cell_id >> 24) & 0xFF) * 192.0 + f64::from(local.x),
        f64::from((cell_id >> 16) & 0xFF) * 192.0 + f64::from(local.y),
    )
}
