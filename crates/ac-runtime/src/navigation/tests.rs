//! The `/nav` and `/motor` commands against a recording of what they ask
//! (ported from OpenAC
//! `tests/AcDream.Runtime.Tests/Navigation/NavigationChatCommandsTests.cs`,
//! its harness and recording navigation), and the session's own wiring of
//! them (`tests/AcDream.Headless.Tests/HeadlessSessionNavigationTests.cs`,
//! `tests/AcDream.Runtime.Tests/Plugins/RuntimeNavigationCommandRegistrationTests.cs`).

use std::collections::HashMap;

use ac_nav::automation::{MapPosition, NavCommandStatus};
use ac_nav::motor::{
    MoveChannel, MoveChannelSnapshot, MoveDirection, MovePace, MoveRequest, MoveUnit,
    ScriptedMoveSnapshot, ScriptedMoveState,
};
use ac_nav::walk::{WalkReport, WalkState};

use super::chat::{ChatState, NavigationSurface};

/// What the commands asked for, and what they read back.
#[derive(Default)]
struct Recording {
    object_walks: Vec<(u32, f32)>,
    place_walks: Vec<(MapPosition, f32)>,
    stand_ons: Vec<(u32, f32)>,
    follows: Vec<(u32, f32)>,
    moves: Vec<MoveRequest>,
    stops: Vec<Option<MoveChannel>>,
    named: HashMap<String, u32>,
    report: WalkReport,
    move_report: ScriptedMoveSnapshot,
    selected: Option<u32>,
    narrated: bool,
    can_narrate: bool,
    listening: bool,
    drawable: bool,
    grid_shown: bool,
    previews: Vec<u32>,
}

const HERE_CELL: u32 = 0xA9B4_0032;

impl Recording {
    fn new() -> Self {
        Self {
            can_narrate: true,
            ..Self::default()
        }
    }

    fn planning(&mut self, object_id: u32) -> NavCommandStatus {
        self.report = WalkReport {
            sequence: self.report.sequence + 1,
            state: WalkState::Planning,
            object_id,
            remaining_meters: f32::NAN,
            replans: 0,
            reason: "planning".to_owned(),
            blocked_by_object_id: 0,
        };
        NavCommandStatus::Accepted
    }
}

impl NavigationSurface for Recording {
    fn go_to(&mut self, object_id: u32, arrival_meters: f32) -> NavCommandStatus {
        self.object_walks.push((object_id, arrival_meters));
        self.planning(object_id)
    }

    fn go_to_place(&mut self, place: &MapPosition, arrival_meters: f32) -> NavCommandStatus {
        self.place_walks.push((*place, arrival_meters));
        self.planning(0)
    }

    fn stand_on(&mut self, object_id: u32, arrival_meters: f32) -> NavCommandStatus {
        self.stand_ons.push((object_id, arrival_meters));
        self.planning(object_id)
    }

    fn follow(&mut self, player_id: u32, buffer_meters: f32) -> NavCommandStatus {
        self.follows.push((player_id, buffer_meters));
        self.planning(player_id)
    }

    fn stop_walk(&mut self) -> NavCommandStatus {
        NavCommandStatus::Accepted
    }

    fn walk_report(&self) -> WalkReport {
        self.report.clone()
    }

    fn move_by(&mut self, request: MoveRequest) -> NavCommandStatus {
        self.moves.push(request);
        let progress = MoveChannelSnapshot {
            sequence: self.moves.len() as i64,
            state: ScriptedMoveState::Moving,
            request,
            covered: 0.0,
            elapsed_seconds: 0.0,
        };
        match request.channel() {
            MoveChannel::Travel => self.move_report.travel = progress,
            MoveChannel::Strafe => self.move_report.strafe = progress,
            MoveChannel::Turn => self.move_report.turn = progress,
        }
        NavCommandStatus::Accepted
    }

    fn stop_moving(&mut self, channel: Option<MoveChannel>) -> NavCommandStatus {
        self.stops.push(channel);
        NavCommandStatus::Accepted
    }

    fn jump(&mut self, _power: f32) -> NavCommandStatus {
        NavCommandStatus::Accepted
    }

    fn face_heading(&mut self, _heading: f32) -> NavCommandStatus {
        NavCommandStatus::Accepted
    }

    fn move_report(&self) -> ScriptedMoveSnapshot {
        self.move_report
    }

    fn selected(&self) -> Option<u32> {
        self.selected
    }

    fn here_cell(&self) -> Option<u32> {
        Some(HERE_CELL)
    }

    fn is_available(&self) -> bool {
        true
    }

    fn find_object(&self, name: &str) -> Option<(u32, String)> {
        self.named
            .iter()
            .find(|(n, _)| n.eq_ignore_ascii_case(name))
            .map(|(n, id)| (*id, n.clone()))
    }

    fn name_of(&self, _object_id: u32) -> Option<String> {
        None
    }

    fn can_narrate(&self) -> bool {
        self.can_narrate
    }

    fn narrate(&mut self, on: bool) {
        self.narrated = true;
        self.listening = on;
    }

    fn can_draw(&self) -> bool {
        self.drawable
    }

    fn toggle_grid(&mut self) -> bool {
        self.grid_shown = !self.grid_shown;
        self.grid_shown
    }

    fn preview_route(&mut self, object_id: u32) -> bool {
        self.grid_shown = true;
        self.previews.push(object_id);
        self.planning(object_id);
        true
    }
}

struct Harness {
    chat: ChatState,
    navigation: Recording,
    said: Vec<String>,
}

impl Harness {
    fn new() -> Self {
        Self {
            chat: ChatState::default(),
            navigation: Recording::new(),
            said: Vec::new(),
        }
    }

    fn selected(mut self, id: u32) -> Self {
        self.navigation.selected = Some(id);
        self
    }

    fn handle(&mut self, line: &str) -> bool {
        let handled = self.chat.handle(line, &mut self.navigation, 0);
        self.said.extend(self.chat.drain_replies());
        handled
    }

    fn tick(&mut self) {
        self.chat.tick(&self.navigation, 0);
        self.said.extend(self.chat.drain_replies());
    }

    fn listener(&mut self, line: &str) {
        assert!(self.navigation.listening, "the walk is narrated");
        self.chat.narrate(line, 0);
        self.said.extend(self.chat.drain_replies());
    }
}

fn close(actual: f32, expected: f32) {
    assert!(
        (actual - expected).abs() < 0.01,
        "{actual} is not {expected}"
    );
}

#[test]
fn go_to_a_cell_and_its_local_point_walks_to_that_place() {
    let mut harness = Harness::new();
    assert!(harness.handle("/nav go to 7E0307A3 310.353577 -312.687317 6.005"));
    let [(place, arrival)] = harness.navigation.place_walks[..] else {
        panic!("one walk to a place");
    };
    assert_eq!(place.cell_id, 0x7E03_07A3);
    let local = place.landblock_local();
    close(local.x, 310.353_58);
    close(local.y, -312.687_3);
    close(local.z, 6.005);
    assert_eq!(arrival, 2.5);
}

#[test]
fn go_to_takes_an_arrival_distance_and_a_prefixed_cell() {
    let mut harness = Harness::new();
    harness.handle("/nav go to 0x7E0307A3 310 -312 6 within 4");
    let [(place, arrival)] = harness.navigation.place_walks[..] else {
        panic!("one walk to a place");
    };
    assert_eq!(place.cell_id, 0x7E03_07A3);
    assert_eq!(arrival, 4.0);
}

#[test]
fn go_to_the_target_walks_to_the_selected_object() {
    for line in ["/nav go to target", "/nav go to", "/nav go to selected"] {
        let mut harness = Harness::new().selected(0x7000_0123);
        harness.handle(line);
        assert_eq!(
            harness.navigation.object_walks,
            [(0x7000_0123, 2.5)],
            "{line}"
        );
    }
}

#[test]
fn go_to_the_target_with_nothing_selected_says_so_and_walks_nowhere() {
    let mut harness = Harness::new();
    harness.handle("/nav go to target");
    assert!(harness.navigation.object_walks.is_empty());
    assert!(
        harness
            .said
            .iter()
            .any(|line| line.contains("select something first"))
    );
}

#[test]
fn stand_on_walks_onto_the_object_and_never_beside_it() {
    for (line, object_id, arrival) in [
        ("/nav stand on target", 0x7000_0123, 2.5),
        ("/nav stand on", 0x7000_0123, 2.5),
        ("/nav stand on 0x70000456 within 1", 0x7000_0456, 1.0),
        ("/nav stand on Rock", 0x7000_0789, 2.5),
    ] {
        let mut harness = Harness::new().selected(0x7000_0123);
        harness.navigation.named.insert("Rock".into(), 0x7000_0789);
        harness.handle("/nav debug on");
        harness.handle(line);
        assert_eq!(
            harness.navigation.stand_ons,
            [(object_id, arrival)],
            "{line}"
        );
        assert!(harness.navigation.object_walks.is_empty());
        assert!(
            harness
                .said
                .iter()
                .any(|s| s.starts_with("[nav] Stand on ") && s.ends_with(": planning")),
            "{line}: {:?}",
            harness.said
        );
    }
}

#[test]
fn follow_takes_a_player_and_a_buffer_distance() {
    for (line, object_id, buffer) in [
        ("/nav follow Bob 5", 0x7000_0789, 5.0),
        ("/nav follow Bob", 0x7000_0789, 3.0),
        ("/nav follow target", 0x7000_0123, 3.0),
        ("/nav follow", 0x7000_0123, 3.0),
        ("/nav follow 8", 0x7000_0123, 8.0),
        ("/nav follow 0x70000456 within 4", 0x7000_0456, 4.0),
    ] {
        let mut harness = Harness::new().selected(0x7000_0123);
        harness.navigation.named.insert("Bob".into(), 0x7000_0789);
        harness.handle("/nav debug on");
        harness.handle(line);
        assert_eq!(harness.navigation.follows, [(object_id, buffer)], "{line}");
        assert!(
            harness
                .said
                .iter()
                .any(|s| s.starts_with("[nav] Follow ") && s.ends_with(": planning")),
            "{line}"
        );
    }
}

/// Walks, routes and moves say nothing in chat about how they start and end
/// unless debug narration is on; answers to what was typed always show.
#[test]
fn walks_and_moves_are_quiet_in_chat_unless_debug_is_on() {
    let mut harness = Harness::new();
    harness.handle("/nav go to target");
    harness.handle("/nav status");
    harness.handle("/motor run forward 5");
    assert_eq!(harness.said.len(), 2);
    assert!(harness.said[0].contains("select something first"));
    assert!(harness.said[1].starts_with("Navigation: "));

    harness.navigation.selected = Some(0x7000_0123);
    harness.said.clear();
    harness.handle("/nav follow target");
    let ours = harness.navigation.report.sequence;
    harness.navigation.report = WalkReport {
        sequence: ours,
        state: WalkState::Stopped,
        object_id: 0x7000_0123,
        remaining_meters: 1.0,
        replans: 0,
        reason: "stopped".into(),
        blocked_by_object_id: 0,
    };
    harness.tick();
    assert!(harness.said.is_empty());
}

#[test]
fn go_to_an_object_id_walks_to_that_object() {
    let mut harness = Harness::new();
    harness.handle("/nav go to 0x70000456 within 3");
    assert_eq!(harness.navigation.object_walks, [(0x7000_0456, 3.0)]);
}

#[test]
fn go_to_a_name_walks_to_the_nearest_object_called_that() {
    let mut harness = Harness::new();
    harness
        .navigation
        .named
        .insert("Town Network Portal".into(), 0x7000_0789);
    harness.handle("/nav go to Town Network Portal");
    assert_eq!(harness.navigation.object_walks, [(0x7000_0789, 2.5)]);
}

#[test]
fn a_quoted_name_keeps_its_quotes() {
    // OpenAC NavigationChatCommands takes the words as typed (split on
    // space, tab and comma only), so a name is written bare.
    let mut harness = Harness::new();
    harness
        .navigation
        .named
        .insert("Town Network Portal".into(), 0x7000_0789);
    harness.handle("/nav go to \"Town Network Portal\"");
    assert!(harness.navigation.object_walks.is_empty());
    assert!(
        harness
            .said
            .iter()
            .any(|line| line.contains("nothing named \"\"Town Network Portal\"\"")),
        "{:?}",
        harness.said
    );
}

#[test]
fn go_to_map_coordinates_stands_on_the_ground_there() {
    let mut harness = Harness::new();
    harness.handle("/nav go to 24.3N 101.1W");
    let [(place, _)] = harness.navigation.place_walks[..] else {
        panic!("one walk to a place");
    };
    assert_eq!(place.cell_id, 0);
    assert!((place.north_south - 24.3).abs() < 1e-5);
    assert!((place.east_west + 101.1).abs() < 1e-5);
    assert!(place.elevation.is_nan());
}

/// The line `/loc` prints is taken as it is: a cell, a bracketed point, and
/// the heading that follows.
#[test]
fn go_to_takes_the_line_loc_prints() {
    for line in [
        "/nav go to 0x7E0307A3 [310.353577 -312.687317 6.005000] 1.000000 0.000000 0.000000 0.000000",
        "/nav go to Your location is: 0x7E0307A3 [310.353577 -312.687317 6.005000] 0.707107 0.000000 0.000000 -0.707107",
        "/nav go to 7E0307A3 [310.353577 -312.687317 6.005000]",
    ] {
        let mut harness = Harness::new();
        harness.handle(line);
        let [(place, arrival)] = harness.navigation.place_walks[..] else {
            panic!("{line}: one walk to a place");
        };
        assert_eq!(place.cell_id, 0x7E03_07A3);
        let local = place.landblock_local();
        close(local.x, 310.353_58);
        close(local.y, -312.687_3);
        close(local.z, 6.005);
        assert_eq!(arrival, 2.5);
    }
}

/// A bracketed point alone lies in the cell the character stands in.
#[test]
fn go_to_a_bracketed_point_alone_is_in_the_characters_own_cell() {
    let mut harness = Harness::new();
    harness.handle("/nav go to [68.145149 -60.000000 0.005000] within 3");
    let [(place, arrival)] = harness.navigation.place_walks[..] else {
        panic!("one walk to a place");
    };
    assert_eq!(place.cell_id, HERE_CELL);
    let local = place.landblock_local();
    close(local.x, 68.145_15);
    close(local.y, -60.0);
    assert_eq!(arrival, 3.0);
}

#[test]
fn a_walk_says_once_how_it_ended_and_not_about_a_walk_that_replaced_it() {
    let mut harness = Harness::new().selected(0x7000_0123);
    harness.handle("/nav debug on");
    harness.handle("/nav go to target");
    let ours = harness.navigation.report.sequence;
    harness.said.clear();

    harness.tick();
    assert!(harness.said.is_empty());

    harness.navigation.report = WalkReport {
        sequence: ours,
        state: WalkState::Arrived,
        object_id: 0x7000_0123,
        remaining_meters: 1.2,
        replans: 0,
        reason: "arrived".into(),
        blocked_by_object_id: 0,
    };
    harness.tick();
    harness.tick();
    let [said] = &harness.said[..] else {
        panic!("one line: {:?}", harness.said);
    };
    assert!(said.contains("arrived"));

    harness.handle("/nav go to target");
    harness.navigation.report = WalkReport {
        sequence: ours + 5,
        state: WalkState::Stopped,
        object_id: 0,
        remaining_meters: f32::NAN,
        replans: 0,
        reason: "a newer request replaced it".into(),
        blocked_by_object_id: 0,
    };
    harness.said.clear();
    harness.tick();
    assert!(harness.said.is_empty());
}

#[test]
fn moves_joined_with_plus_start_together_in_the_order_written() {
    let mut harness = Harness::new();
    harness.handle("/motor run forward 20s + turn left 90");
    assert_eq!(
        harness.navigation.moves,
        [
            MoveRequest {
                direction: MoveDirection::Forward,
                pace: MovePace::Run,
                amount: 20.0,
                unit: MoveUnit::Seconds
            },
            MoveRequest {
                direction: MoveDirection::TurnLeft,
                pace: MovePace::Run,
                amount: 90.0,
                unit: MoveUnit::MetersOrDegrees
            },
        ]
    );
}

#[test]
fn two_moves_on_one_channel_are_refused_and_nothing_starts() {
    let mut harness = Harness::new();
    harness.handle("/motor run forward + walk backward 3");
    assert!(harness.navigation.moves.is_empty());
    assert!(
        harness
            .said
            .iter()
            .any(|line| line.contains("travel channel"))
    );
}

#[test]
fn a_move_says_how_it_ended_once_it_has() {
    let mut harness = Harness::new();
    harness.handle("/nav debug on");
    harness.handle("/motor walk backward 3");
    harness.said.clear();
    let sequence = harness.navigation.move_report.travel.sequence;
    harness.navigation.move_report.travel = MoveChannelSnapshot {
        sequence,
        state: ScriptedMoveState::Blocked,
        request: MoveRequest::new(MoveDirection::Backward, MovePace::Walk, 3.0),
        covered: 1.5,
        elapsed_seconds: 1.6,
    };
    harness.tick();
    harness.tick();
    let [said] = &harness.said[..] else {
        panic!("one line: {:?}", harness.said);
    };
    assert!(said.contains("blocked"));
}

#[test]
fn stop_ends_one_channel_or_every_move() {
    let mut harness = Harness::new();
    harness.handle("/motor stop turn");
    harness.handle("/motor stop");
    assert_eq!(harness.navigation.stops, [Some(MoveChannel::Turn), None]);
}

#[test]
fn the_grid_and_route_say_there_is_nothing_to_draw_on_where_nothing_is_drawn() {
    let mut harness = Harness::new().selected(0x7000_0123);
    harness.handle("/nav grid");
    harness.handle("/nav route target");
    assert_eq!(
        harness
            .said
            .iter()
            .filter(|line| line.contains("nothing to draw"))
            .count(),
        2
    );
    assert!(harness.navigation.previews.is_empty());
    assert!(!harness.navigation.grid_shown);
}

/// As OpenAC's window: `/nav grid` toggles the grid and says which way.
#[test]
fn the_grid_is_shown_and_hidden_where_a_window_draws_it() {
    let mut harness = Harness::new();
    harness.navigation.drawable = true;
    harness.handle("/nav grid");
    assert!(harness.navigation.grid_shown);
    harness.handle("/nav grid");
    assert!(!harness.navigation.grid_shown);
    assert_eq!(
        harness.said,
        ["Navigation: grid shown", "Navigation: grid hidden"]
    );
}

/// `/nav route` plans a route to the target without walking it, shows the
/// grid, and narrates (only while debug is on) how the search ended.
#[test]
fn a_route_is_planned_to_the_target_without_walking_and_shown_with_the_grid() {
    let mut harness = Harness::new().selected(0x7000_0123);
    harness.navigation.drawable = true;
    harness.handle("/nav route");
    harness.handle("/nav route 0x70000456");
    assert_eq!(harness.navigation.previews, [0x7000_0123, 0x7000_0456]);
    assert!(harness.navigation.grid_shown);
    assert!(harness.navigation.object_walks.is_empty());
    assert!(
        harness.said.is_empty(),
        "quiet unless debug: {:?}",
        harness.said
    );

    harness.handle("/nav debug");
    harness.handle("/nav route target");
    assert!(
        harness
            .said
            .iter()
            .any(|l| l == "[nav] Navigation: planning a route to 0x70000123"),
        "{:?}",
        harness.said
    );
    harness.navigation.report.state = WalkState::Planned;
    harness.navigation.report.reason = "3 legs, 12.0 m".to_owned();
    harness.tick();
    assert!(
        harness
            .said
            .iter()
            .any(|l| l == "[nav] Route to 0x70000123: 3 legs, 12.0 m"),
        "{:?}",
        harness.said
    );
}

#[test]
fn a_route_with_nothing_selected_says_so() {
    let mut harness = Harness::new();
    harness.navigation.drawable = true;
    harness.handle("/nav route");
    assert_eq!(harness.said, ["Navigation: select something first"]);
    assert!(harness.navigation.previews.is_empty());
}

#[test]
fn debug_sends_the_walks_narration_to_chat_until_it_is_turned_off() {
    let mut harness = Harness::new();
    assert!(!harness.navigation.listening);
    harness.handle("/nav debug");
    harness.listener("Route: 3 legs, 40.0 m");
    assert!(
        harness
            .said
            .iter()
            .any(|line| line == "[nav] Route: 3 legs, 40.0 m")
    );
    harness.handle("/nav debug off");
    assert!(!harness.navigation.listening);
}

#[test]
fn debug_writes_the_walks_narration_to_a_file_as_well_when_asked_to() {
    let directory = std::env::temp_dir().join(format!(
        "nav-debug-{}-{}",
        std::process::id(),
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map_or(0, |d| d.as_nanos())
    ));
    let path = directory.join("walks.log");
    let mut harness = Harness::new();
    harness.handle(&format!("/nav debug file {}", path.display()));
    harness.listener("Walk to 0x70000001: arrived");
    let text = std::fs::read_to_string(&path).expect("the narration file");
    let lines: Vec<&str> = text.lines().collect();
    let [line] = lines[..] else {
        panic!("one line: {lines:?}");
    };
    assert!(line.ends_with(" Walk to 0x70000001: arrived"));
    assert!(
        !harness
            .said
            .iter()
            .any(|l| l.starts_with(super::DEBUG_PREFIX))
    );
    harness.handle("/nav debug file off");
    assert!(!harness.navigation.listening);
    let _ = std::fs::remove_dir_all(&directory);
}

#[test]
fn debug_says_so_where_walks_are_not_narrated() {
    let mut harness = Harness::new();
    harness.navigation.can_narrate = false;
    harness.handle("/nav debug");
    assert!(harness.said.iter().any(|l| l.contains("does not narrate")));
}

#[test]
fn only_the_two_verbs_are_answered() {
    let mut harness = Harness::new();
    assert!(harness.handle("/NAV status"));
    assert!(harness.handle("@motor status"));
    assert!(!harness.handle("/navigate"));
    assert!(!harness.handle("nav status"));
    assert!(!harness.handle("/"));
}

#[test]
fn the_utc_stamp_reads_as_dot_net_writes_it() {
    // 2026-10-12T08:09:10.123Z
    assert_eq!(
        super::chat::utc_stamp(1_791_792_550_123),
        "2026-10-12T08:09:10.123Z"
    );
}

// ── the session's wiring ───────────────────────────────────────────────

mod session {
    //! As OpenAC's `HeadlessSessionNavigationTests` and
    //! `RuntimeNavigationCommandRegistrationTests`: the verbs answer in the
    //! chat log, never as on-screen notices, in a session with or without
    //! the dats.

    use std::time::Duration;

    use crate::communication::CommunicationPaths;
    use crate::session::{Clocks, HostEvent, Outbox, SessionCommand, SessionState};

    const CLOCKS: Clocks = Clocks {
        wall_unix_ms: 1_700_000_000_000,
        session: Duration::from_secs(10),
    };

    fn typed(state: &mut SessionState, line: &str) -> Vec<HostEvent> {
        let mut outbox = Outbox::new();
        state.command(
            SessionCommand::Chat(line.to_owned()),
            &mut outbox,
            true,
            CLOCKS,
        );
        assert!(
            outbox.drain().next().is_none(),
            "{line} sent nothing to the server"
        );
        state.drain_host_events()
    }

    fn chat(events: &[HostEvent]) -> Vec<String> {
        events
            .iter()
            .filter_map(|e| match e {
                HostEvent::Chat(line) => Some(line.text.clone()),
                _ => None,
            })
            .collect()
    }

    #[test]
    fn navigation_commands_answer_in_the_chat_log() {
        let mut state = SessionState::new(CommunicationPaths::default());
        let events = typed(&mut state, "/nav status");
        assert!(
            chat(&events)
                .iter()
                .any(|l| l.contains("Navigation: no walk has been asked for")),
            "{events:?}"
        );
        assert!(
            !events
                .iter()
                .any(|e| matches!(e, HostEvent::InterfaceText(t) if t.contains("Navigation:")))
        );
        let events = typed(&mut state, "/motor status");
        assert!(
            chat(&events)
                .iter()
                .any(|l| l.starts_with("Motor: travel none"))
        );
    }

    #[test]
    fn the_grid_and_the_route_answer_in_plain_words_without_a_window() {
        let mut state = SessionState::new(CommunicationPaths::default());
        let mut lines = chat(&typed(&mut state, "/nav grid"));
        lines.extend(chat(&typed(&mut state, "/nav route 0x80000001")));
        assert!(
            lines
                .iter()
                .any(|l| l.contains("Navigation: this client has nothing to draw the grid on"))
        );
        assert!(
            lines
                .iter()
                .any(|l| l.contains("Navigation: this client has nothing to draw a route on"))
        );
    }

    /// With a window attached the grid is toggled; the view stays empty
    /// until there is a grid and a character to draw around.
    #[test]
    fn with_a_window_attached_the_grid_is_toggled() {
        let mut state = SessionState::new(CommunicationPaths::default());
        state.set_window_attached(true, &mut Outbox::new());
        let mut lines = chat(&typed(&mut state, "/nav grid"));
        lines.extend(chat(&typed(&mut state, "/nav grid")));
        assert!(
            lines.iter().any(|l| l == "Navigation: grid shown"),
            "{lines:?}"
        );
        assert!(
            lines.iter().any(|l| l == "Navigation: grid hidden"),
            "{lines:?}"
        );
        assert_eq!(
            *state.navigation.view(),
            crate::navigation::NavigationView::default()
        );
        state.set_window_attached(false, &mut Outbox::new());
        let lines = chat(&typed(&mut state, "/nav grid"));
        assert!(
            lines
                .iter()
                .any(|l| l.contains("Navigation: this client has nothing to draw the grid on"))
        );
    }

    /// A session that loaded no game data has nothing to plan walks over,
    /// so it says so.
    #[test]
    fn walks_are_unavailable_in_a_session_without_game_data() {
        let mut state = SessionState::new(CommunicationPaths::default());
        let lines = chat(&typed(&mut state, "/nav go to 0x70000001"));
        assert!(
            lines
                .iter()
                .any(|l| l.contains("Walk to 0x70000001: this client cannot walk now")),
            "{lines:?}"
        );
    }
}
