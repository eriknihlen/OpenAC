//! The `/nav` and `/motor` chat commands: walks the client plans, and moves
//! it carries out, asked for by typing. Each answers in chat, and while
//! debug narration is on says how what it started ended. The words are
//! parsed by `ac_nav::commands`; what the commands ask goes through a
//! [`NavigationSurface`] (the runtime's walks and moves, or a recording in
//! tests), as OpenAC's commands go through its plugin navigation API.

use std::io::Write as _;
use std::path::PathBuf;

use ac_nav::automation::{MAXIMUM_GO_TO_ARRIVAL_METERS, MapPosition, NavCommandStatus};
use ac_nav::commands::{
    self, MOTOR_USAGE, MotorAction, MotorCommand, NAME_SEARCH_METERS, NAV_USAGE, NavGoal,
    NavTarget, describe_move, describe_walk,
};
use ac_nav::motor::{MoveChannel, MoveRequest, ScriptedMoveSnapshot, ScriptedMoveState};
use ac_nav::walk::{WalkReport, WalkState};

/// The walk command's verb.
pub const NAV_VERB: &str = "nav";
/// The move command's verb.
pub const MOTOR_VERB: &str = "motor";
/// What begins every line of a walk's narration written to chat.
pub const DEBUG_PREFIX: &str = "[nav] ";

/// What the commands ask of the client: its walks, its moves, and where it
/// and the objects it knows stand.
pub(crate) trait NavigationSurface {
    /// Walk to an object.
    fn go_to(&mut self, object_id: u32, arrival_meters: f32) -> NavCommandStatus;
    /// Walk to a place.
    fn go_to_place(&mut self, place: &MapPosition, arrival_meters: f32) -> NavCommandStatus;
    /// Walk onto an object's top.
    fn stand_on(&mut self, object_id: u32, arrival_meters: f32) -> NavCommandStatus;
    /// Follow a player.
    fn follow(&mut self, player_id: u32, buffer_meters: f32) -> NavCommandStatus;
    /// Stop the walk under way.
    fn stop_walk(&mut self) -> NavCommandStatus;
    /// Where the latest walk stands.
    fn walk_report(&self) -> WalkReport;
    /// Begin a scripted move.
    fn move_by(&mut self, request: MoveRequest) -> NavCommandStatus;
    /// Stop every scripted move, or one channel's.
    fn stop_moving(&mut self, channel: Option<MoveChannel>) -> NavCommandStatus;
    /// Charge and release a jump.
    fn jump(&mut self, power: f32) -> NavCommandStatus;
    /// Turn to a compass heading, degrees.
    fn face_heading(&mut self, heading: f32) -> NavCommandStatus;
    /// The latest move on each channel; all none while unavailable.
    fn move_report(&self) -> ScriptedMoveSnapshot;
    /// The selected object.
    fn selected(&self) -> Option<u32>;
    /// The cell the character stands in, while it is in the world.
    fn here_cell(&self) -> Option<u32>;
    /// Whether the character is in the world.
    fn is_available(&self) -> bool;
    /// The nearest object with this name (any case) within
    /// [`NAME_SEARCH_METERS`] of the character, with its name.
    fn find_object(&self, name: &str) -> Option<(u32, String)>;
    /// An object's name, when the client knows one.
    fn name_of(&self, object_id: u32) -> Option<String>;
    /// Whether walks are narrated at all.
    fn can_narrate(&self) -> bool;
    /// Turns the walks' full narration on or off.
    fn narrate(&mut self, on: bool);
    /// Whether a window draws the grid and routes.
    fn can_draw(&self) -> bool;
    /// Shows or hides the navigation grid; whether it is now shown.
    fn toggle_grid(&mut self) -> bool;
    /// Plans a route to an object without walking it and shows the grid
    /// with it; false when the character is not in the world.
    fn preview_route(&mut self, object_id: u32) -> bool;
}

/// What the commands remember between calls.
#[derive(Debug, Default)]
pub(super) struct ChatState {
    debug_to_chat: bool,
    debug_file: Option<PathBuf>,
    walk: Option<(i64, String)>,
    route: Option<(i64, String)>,
    moves: Vec<(MoveChannel, i64, String)>,
    replies: Vec<String>,
}

/// The verb and arguments of a typed line, when it is a command
/// (`/verb ...` or `@verb ...`).
#[must_use]
pub(super) fn split_command(line: &str) -> Option<(&str, &str)> {
    let trimmed = line.trim();
    let rest = trimmed.strip_prefix(['/', '@'])?;
    if rest.is_empty() {
        return None;
    }
    Some(match rest.find([' ', '\t']) {
        Some(at) => (&rest[..at], rest[at + 1..].trim()),
        None => (rest, ""),
    })
}

/// Whether a walk state is one a walk ends in.
fn is_finished(state: WalkState) -> bool {
    !matches!(
        state,
        WalkState::None | WalkState::Planning | WalkState::Walking | WalkState::Waiting
    )
}

/// UTC time as `yyyy-MM-ddTHH:mm:ss.fffZ`.
pub(super) fn utc_stamp(unix_ms: i64) -> String {
    let days = unix_ms.div_euclid(86_400_000);
    let ms_of_day = unix_ms.rem_euclid(86_400_000);
    // Days to a civil date (Howard Hinnant's algorithm).
    let z = days + 719_468;
    let era = z.div_euclid(146_097);
    let doe = z.rem_euclid(146_097);
    let yoe = (doe - doe / 1460 + doe / 36_524 - doe / 146_096) / 365;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let day = doy - (153 * mp + 2) / 5 + 1;
    let month = if mp < 10 { mp + 3 } else { mp - 9 };
    let year = yoe + era * 400 + i64::from(month <= 2);
    format!(
        "{year:04}-{month:02}-{day:02}T{:02}:{:02}:{:02}.{:03}Z",
        ms_of_day / 3_600_000,
        ms_of_day / 60_000 % 60,
        ms_of_day / 1000 % 60,
        ms_of_day % 1000
    )
}

/// Resolves a debug file's path, a leading `~` meaning the home folder, and
/// makes sure it can be written.
fn open_debug_file(path: &str) -> Result<PathBuf, String> {
    if path.trim().is_empty() {
        return Err("no path was given".to_owned());
    }
    let expanded = match path.strip_prefix("~/").or_else(|| path.strip_prefix("~\\")) {
        Some(rest) => std::env::var_os("USERPROFILE")
            .or_else(|| std::env::var_os("HOME"))
            .map(PathBuf::from)
            .ok_or_else(|| "the home folder is not known".to_owned())?
            .join(rest),
        None => PathBuf::from(path),
    };
    let full = std::path::absolute(&expanded).map_err(|e| e.to_string())?;
    if let Some(directory) = full.parent().filter(|d| !d.as_os_str().is_empty()) {
        std::fs::create_dir_all(directory).map_err(|e| e.to_string())?;
    }
    std::fs::OpenOptions::new()
        .create(true)
        .append(true)
        .open(&full)
        .map_err(|e| e.to_string())?;
    Ok(full)
}

impl ChatState {
    /// Take the lines the commands answered with.
    pub(super) fn drain_replies(&mut self) -> Vec<String> {
        std::mem::take(&mut self.replies)
    }

    fn reply(&mut self, line: impl Into<String>) {
        self.replies.push(line.into());
    }

    fn narrating(&self) -> bool {
        self.debug_to_chat || self.debug_file.is_some()
    }

    /// Says how a walk, route or move started and ended, only while debug
    /// narration is on.
    fn announce(&mut self, line: &str, wall_unix_ms: i64) {
        if self.narrating() {
            self.narrate(line, wall_unix_ms);
        }
    }

    /// A line of narration: to chat while it goes there, and to the debug
    /// file while there is one.
    pub(super) fn narrate(&mut self, line: &str, wall_unix_ms: i64) {
        if self.debug_to_chat {
            self.reply(format!("{DEBUG_PREFIX}{line}"));
        }
        let Some(file) = self.debug_file.clone() else {
            return;
        };
        let written = std::fs::OpenOptions::new()
            .create(true)
            .append(true)
            .open(&file)
            .and_then(|mut f| writeln!(f, "{} {line}", utc_stamp(wall_unix_ms)));
        if let Err(error) = written {
            self.debug_file = None;
            self.reply(format!(
                "Navigation: stopped writing debug narration to {}: {error}",
                file.display()
            ));
        }
    }

    /// Says how each walk and move these commands started ended, once it
    /// has.
    pub(super) fn tick(&mut self, surface: &dyn NavigationSurface, wall_unix_ms: i64) {
        let report = surface.walk_report();
        if let Some((sequence, label)) = self.route.clone() {
            if report.sequence != sequence {
                self.route = None;
            } else if report.state != WalkState::Planning {
                // A route asked for alone that was found reports no walk
                // state of its own.
                self.route = None;
                let line = if matches!(report.state, WalkState::None | WalkState::Planned) {
                    let reason = if report.reason.trim().is_empty() {
                        "a route was found"
                    } else {
                        &report.reason
                    };
                    format!("{label}: {reason}")
                } else {
                    format!("{label}: {}", describe_walk(&report))
                };
                self.announce(&line, wall_unix_ms);
            }
        }
        if let Some((sequence, label)) = self.walk.clone() {
            if report.sequence != sequence {
                self.walk = None;
            } else if is_finished(report.state) {
                self.walk = None;
                self.announce(
                    &format!("{label}: {}", describe_walk(&report)),
                    wall_unix_ms,
                );
            }
        }
        let moves = surface.move_report();
        for index in (0..self.moves.len()).rev() {
            let (channel, sequence, label) = self.moves[index].clone();
            let progress = moves.channel(channel);
            if progress.sequence != sequence {
                self.moves.remove(index);
                continue;
            }
            if matches!(
                progress.state,
                ScriptedMoveState::None | ScriptedMoveState::Moving
            ) {
                continue;
            }
            self.moves.remove(index);
            self.announce(
                &format!("{label}: {}", describe_move(&progress)),
                wall_unix_ms,
            );
        }
    }

    /// Answers `/nav` and `/motor`; false for any other line (it goes on to
    /// the rest of the chat route).
    pub(super) fn handle(
        &mut self,
        line: &str,
        surface: &mut dyn NavigationSurface,
        wall_unix_ms: i64,
    ) -> bool {
        let Some((verb, arguments)) = split_command(line) else {
            return false;
        };
        if verb.eq_ignore_ascii_case(NAV_VERB) {
            self.nav(arguments, surface, wall_unix_ms);
        } else if verb.eq_ignore_ascii_case(MOTOR_VERB) {
            self.motor(arguments, surface, wall_unix_ms);
        } else {
            return false;
        }
        true
    }

    // ── /nav ────────────────────────────────────────────────────────────

    fn nav(&mut self, arguments: &str, surface: &mut dyn NavigationSurface, wall_unix_ms: i64) {
        let words = commands::words(arguments);
        let is = |index: usize, word: &str| {
            words
                .get(index)
                .is_some_and(|w| w.eq_ignore_ascii_case(word))
        };
        if words.is_empty() || is(0, "help") {
            self.reply(NAV_USAGE);
        } else if is(0, "go") && words.len() >= 2 && is(1, "to") {
            self.go_to(&words[2..], surface, wall_unix_ms);
        } else if is(0, "goto") {
            self.go_to(&words[1..], surface, wall_unix_ms);
        } else if is(0, "follow") {
            self.follow(&words[1..], surface, wall_unix_ms);
        } else if is(0, "stand") && words.len() >= 2 && is(1, "on") {
            self.stand_on(&words[2..], surface, wall_unix_ms);
        } else if is(0, "stop") && words.len() == 1 {
            let line = match surface.stop_walk() {
                NavCommandStatus::Accepted => "Navigation: stopped the walk",
                NavCommandStatus::Rejected | NavCommandStatus::Held => {
                    "Navigation: no walk is under way"
                }
                NavCommandStatus::Unavailable => "Navigation: the character is not in the world",
            };
            self.reply(line);
        } else if is(0, "status") && words.len() == 1 {
            let report = surface.walk_report();
            let line = if report.sequence == 0 {
                "Navigation: no walk has been asked for".to_owned()
            } else {
                format!("Navigation: {}", describe_walk(&report))
            };
            self.reply(line);
        } else if is(0, "grid") && words.len() == 1 {
            let line = if !surface.can_draw() {
                "Navigation: this client has nothing to draw the grid on"
            } else if surface.toggle_grid() {
                "Navigation: grid shown"
            } else {
                "Navigation: grid hidden"
            };
            self.reply(line);
        } else if is(0, "route") {
            self.route(&words[1..], surface, wall_unix_ms);
        } else if is(0, "debug") {
            self.debug(&words[1..], arguments, surface);
        } else {
            self.reply(NAV_USAGE);
        }
    }

    /// Turns the walks' full narration on or off, in chat and in a file.
    fn debug(&mut self, words: &[String], arguments: &str, surface: &mut dyn NavigationSurface) {
        if !surface.can_narrate() {
            self.reply("Navigation: this client does not narrate its walks");
            return;
        }
        let is = |index: usize, word: &str| {
            words
                .get(index)
                .is_some_and(|w| w.eq_ignore_ascii_case(word))
        };
        if is(0, "file") {
            if words.len() == 2 && is(1, "off") {
                self.debug_file = None;
                self.reply("Navigation: debug narration no longer goes to a file");
            } else if words.len() >= 2 {
                let lower = arguments.to_ascii_lowercase();
                let at = lower.find("file").map_or(0, |at| at + 4);
                let path = arguments[at..].trim();
                match open_debug_file(path) {
                    Ok(full) => {
                        self.reply(format!(
                            "Navigation: debug narration goes to {}",
                            full.display()
                        ));
                        self.debug_file = Some(full);
                    }
                    Err(problem) => {
                        self.reply(format!(
                            "Navigation: cannot write debug narration to {path}: {problem}"
                        ));
                        return;
                    }
                }
            } else {
                self.reply(NAV_USAGE);
                return;
            }
        } else if words.is_empty() || (words.len() == 1 && (is(0, "on") || is(0, "off"))) {
            self.debug_to_chat = if words.is_empty() {
                !self.debug_to_chat
            } else {
                is(0, "on")
            };
            self.reply(if self.debug_to_chat {
                "Navigation: debug narration on"
            } else {
                "Navigation: debug narration off"
            });
        } else {
            self.reply(NAV_USAGE);
            return;
        }
        surface.narrate(self.narrating());
    }

    fn go_to(&mut self, words: &[String], surface: &mut dyn NavigationSurface, wall: i64) {
        let (goal, arrival) = match commands::parse_go_to(words, surface.here_cell()) {
            Ok(parsed) => parsed,
            Err(problem) => {
                self.reply(format!("Navigation: {problem}"));
                return;
            }
        };
        match goal {
            NavGoal::Place(place, label) => {
                let status = surface.go_to_place(&place, arrival);
                self.started(status, &format!("Walk to {label}"), surface, wall);
            }
            NavGoal::Object(target) => match resolve(&target, surface) {
                Ok((object_id, label)) => {
                    let status = surface.go_to(object_id, arrival);
                    self.started(status, &format!("Walk to {label}"), surface, wall);
                }
                Err(problem) => self.reply(format!("Navigation: {problem}")),
            },
        }
    }

    /// Follows a player: the selected one, one by id, or one by name, with
    /// the buffer to keep behind them as a last number or after `within`.
    fn follow(&mut self, words: &[String], surface: &mut dyn NavigationSurface, wall: i64) {
        let (target, buffer) = match commands::parse_follow(words) {
            Ok(parsed) => parsed,
            Err(problem) => {
                self.reply(format!("Navigation: {problem}"));
                return;
            }
        };
        match resolve(&target, surface) {
            Ok((player_id, label)) => {
                let status = surface.follow(player_id, buffer);
                self.started(status, &format!("Follow {label}"), surface, wall);
            }
            Err(problem) => self.reply(format!("Navigation: {problem}")),
        }
    }

    fn stand_on(&mut self, words: &[String], surface: &mut dyn NavigationSurface, wall: i64) {
        let resolved = commands::parse_stand_on(words)
            .and_then(|(target, arrival)| resolve(&target, surface).map(|r| (r, arrival)));
        match resolved {
            Ok(((object_id, label), arrival)) => {
                let status = surface.stand_on(object_id, arrival);
                self.started(status, &format!("Stand on {label}"), surface, wall);
            }
            Err(problem) => self.reply(format!("Navigation: {problem}")),
        }
    }

    /// Plans and draws a route to an object without walking it.
    fn route(&mut self, words: &[String], surface: &mut dyn NavigationSurface, wall: i64) {
        if !surface.can_draw() {
            self.reply("Navigation: this client has nothing to draw a route on");
            return;
        }
        let (object_id, label) = match resolve(&commands::parse_target(words), surface) {
            Ok(resolved) => resolved,
            Err(problem) => {
                self.reply(format!("Navigation: {problem}"));
                return;
            }
        };
        if !surface.preview_route(object_id) {
            self.reply("Navigation: the character is not in the world");
            return;
        }
        self.route = Some((surface.walk_report().sequence, format!("Route to {label}")));
        self.announce(&format!("Navigation: planning a route to {label}"), wall);
    }

    fn started(
        &mut self,
        status: NavCommandStatus,
        label: &str,
        surface: &dyn NavigationSurface,
        wall_unix_ms: i64,
    ) {
        match status {
            NavCommandStatus::Accepted => {
                self.walk = Some((surface.walk_report().sequence, label.to_owned()));
                self.announce(&format!("{label}: planning"), wall_unix_ms);
            }
            NavCommandStatus::Rejected => self.reply(format!(
                "{label}: refused; the arrival distance must be above 0 and at most \
                 {MAXIMUM_GO_TO_ARRIVAL_METERS:.0} m"
            )),
            NavCommandStatus::Unavailable | NavCommandStatus::Held => {
                self.reply(format!("{label}: this client cannot walk now"));
            }
        }
    }

    // ── /motor ──────────────────────────────────────────────────────────

    fn motor(&mut self, arguments: &str, surface: &mut dyn NavigationSurface, wall: i64) {
        let command = match commands::parse_motor(arguments) {
            Ok(command) => command,
            Err(reply) => {
                self.reply(reply);
                return;
            }
        };
        match command {
            MotorCommand::Help => self.reply(MOTOR_USAGE),
            MotorCommand::Status => {
                let moves = surface.move_report();
                self.reply(format!(
                    "Motor: travel {}; strafe {}; turn {}",
                    describe_move(&moves.travel),
                    describe_move(&moves.strafe),
                    describe_move(&moves.turn)
                ));
            }
            MotorCommand::Stop(channel) => {
                let line = match surface.stop_moving(channel) {
                    NavCommandStatus::Accepted => "Motor: stopped",
                    NavCommandStatus::Rejected | NavCommandStatus::Held => "Motor: nothing to stop",
                    NavCommandStatus::Unavailable => "Motor: the character is not in the world",
                };
                self.reply(line);
            }
            MotorCommand::Moves(moves) => {
                for one in moves {
                    let status = match one.action {
                        MotorAction::Move(request) => surface.move_by(request),
                        MotorAction::FaceHeading(heading) => surface.face_heading(heading),
                        MotorAction::Jump(power) => surface.jump(power),
                    };
                    match status {
                        NavCommandStatus::Accepted => {}
                        NavCommandStatus::Rejected => {
                            self.reply(format!(
                                "{}: refused; it is past what the client carries out",
                                one.label
                            ));
                            continue;
                        }
                        NavCommandStatus::Unavailable | NavCommandStatus::Held => {
                            self.reply(format!(
                                "{}: this client cannot move the character now",
                                one.label
                            ));
                            continue;
                        }
                    }
                    match one.channel.filter(|_| one.reported) {
                        Some(channel) => {
                            let sequence = surface.move_report().channel(channel).sequence;
                            self.moves.push((channel, sequence, one.label));
                        }
                        None => self.announce(&format!("{}: sent", one.label), wall),
                    }
                }
            }
        }
    }
}

/// The object a target names: the selected one, one by id, or the nearest
/// one with that name, with how it is named.
fn resolve(target: &NavTarget, surface: &dyn NavigationSurface) -> Result<(u32, String), String> {
    let label = |id: u32| match surface.name_of(id).filter(|n| !n.is_empty()) {
        Some(name) => format!("{name} (0x{id:08X})"),
        None => format!("0x{id:08X}"),
    };
    match target {
        NavTarget::Selected => match surface.selected().filter(|id| *id != 0) {
            Some(id) => Ok((id, label(id))),
            None => Err("select something first".to_owned()),
        },
        NavTarget::Id(id) => Ok((*id, label(*id))),
        NavTarget::Name(name) => {
            if !surface.is_available() {
                return Err("the character is not in the world".to_owned());
            }
            surface
                .find_object(name)
                .map(|(id, found)| (id, format!("{found} (0x{id:08X})")))
                .ok_or_else(|| {
                    format!("nothing named \"{name}\" is within {NAME_SEARCH_METERS:.0} m")
                })
        }
    }
}
