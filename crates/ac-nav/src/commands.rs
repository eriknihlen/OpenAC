//! The words of `/nav` and `/motor`, parsed into what they ask for. The
//! runtime registers the commands, resolves a target or a name to an
//! object, hands the request to the walk or the motor, and says the
//! replies; how the typed words read is here.

use glam::Vec3;

use crate::automation::MapPosition;
use crate::motor::{
    MoveChannel, MoveDirection, MovePace, MoveRequest, MoveUnit, ScriptedMoveState,
};
use crate::order::fixed;
use crate::walk::{DEFAULT_ARRIVAL_METERS, DEFAULT_FOLLOW_METERS, WalkReport, WalkState};

/// How far around the character an object is looked for by name, metres.
pub const NAME_SEARCH_METERS: f64 = 1000.0;

/// What `/nav` prints when it is not understood.
pub const NAV_USAGE: &str = "Usage: /nav go to [target | 0xOBJECTID | name | CELL [X Y Z] | [X Y Z] | 24.3N 101.1W] [within METERS], /nav stand on [target | 0xOBJECTID | name] [within METERS], /nav follow [target | 0xPLAYERID | player name] [BUFFER METERS], /nav stop, /nav status, /nav grid, /nav route [target | 0xOBJECTID | name], /nav debug [on | off], /nav debug file PATH | off";

/// What `/motor` prints when it is not understood.
pub const MOTOR_USAGE: &str = "Usage: /motor walk|run [forward|backward] [AMOUNT], /motor strafe left|right [AMOUNT], /motor turn left|right [DEGREES], /motor turn to HEADING, /motor jump [POWER], /motor stop [travel|strafe|turn], /motor status; join moves with +, such as /motor run forward 20s + turn left 90. An AMOUNT is meters, degrees for a turn, or seconds written as 20s.";

/// The object a command names, for the runtime to resolve.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum NavTarget {
    /// The selected object (no words, `target`, `selected`, `selection`).
    Selected,
    /// An object id written `0x...`.
    Id(u32),
    /// The nearest object with this name within [`NAME_SEARCH_METERS`].
    Name(String),
}

/// Where `/nav go to` goes.
#[derive(Debug, Clone, PartialEq)]
pub enum NavGoal {
    /// A place, with how it is named.
    Place(MapPosition, String),
    /// An object.
    Object(NavTarget),
}

/// The words of a command line, split on spaces, tabs and commas.
#[must_use]
pub fn words(text: &str) -> Vec<String> {
    text.split([' ', '\t', ','])
        .map(str::trim)
        .filter(|word| !word.is_empty())
        .map(str::to_owned)
        .collect()
}

fn is(word: &str, expected: &str) -> bool {
    word.eq_ignore_ascii_case(expected)
}

/// A finite number, as the invariant culture reads one.
#[must_use]
pub fn parse_float(word: &str) -> Option<f32> {
    word.trim()
        .parse::<f32>()
        .ok()
        .filter(|value| value.is_finite())
}

/// A hexadecimal id, with `0x` (required when `require_prefix`) and at most
/// eight digits.
#[must_use]
pub fn parse_hex(word: &str, require_prefix: bool) -> Option<u32> {
    let digits = match word.get(..2) {
        Some(prefix) if prefix.eq_ignore_ascii_case("0x") => &word[2..],
        _ if require_prefix => return None,
        _ => word,
    };
    if digits.is_empty() || digits.len() > 8 || !digits.chars().all(|c| c.is_ascii_hexdigit()) {
        return None;
    }
    u32::from_str_radix(digits, 16).ok()
}

/// A map coordinate with its compass letter: `24.3N` is 24.3, `101.1W` is
/// -101.1.
#[must_use]
pub fn parse_compass(word: &str, positive: char, negative: char) -> Option<f64> {
    let mut chars = word.chars();
    let letter = chars.next_back()?.to_ascii_uppercase();
    let magnitude = chars.as_str();
    if magnitude.is_empty() || (letter != positive && letter != negative) {
        return None;
    }
    let value: f64 = magnitude.trim().parse().ok()?;
    if !value.is_finite() || value < 0.0 {
        return None;
    }
    Some(if letter == positive { value } else { -value })
}

/// A number with up to `decimals` decimals, trailing zeros dropped (the
/// `0.###` patterns).
fn trimmed(value: f64, decimals: u32) -> String {
    let text = fixed(value as f32, decimals);
    if text.contains('.') {
        text.trim_end_matches('0').trim_end_matches('.').to_owned()
    } else {
        text
    }
}

/// Splits a trailing `within METERS` off the words: the goal words and the
/// arrival distance (2.5 m without one).
///
/// # Errors
///
/// When `within` is not followed by exactly one number.
pub fn split_within(words: &[String]) -> Result<(Vec<String>, f32), String> {
    let Some(within) = words.iter().rposition(|word| is(word, "within")) else {
        return Ok((words.to_vec(), DEFAULT_ARRIVAL_METERS));
    };
    let arrival = (within + 2 == words.len())
        .then(|| parse_float(&words[words.len() - 1]))
        .flatten();
    match arrival {
        Some(arrival) => Ok((words[..within].to_vec(), arrival)),
        None => Err("within takes a distance in meters, such as within 3".to_owned()),
    }
}

/// A place: a cell and a point in its landblock's own frame, as /loc prints
/// them, with or without 0x, brackets, the "Your location is:" before them
/// and the heading after them; a bracketed point alone, in `here_cell`, the
/// character's own cell; or map coordinates with compass letters, which
/// stand on the ground there. Gives the place and how it is named.
#[must_use]
pub fn parse_place(words: &[String], here_cell: Option<u32>) -> Option<(MapPosition, String)> {
    let joined = words.join(" ");
    let bracketed = joined.contains('[');
    const LOC: &str = "Your location is:";
    let text = match joined.get(..LOC.len()) {
        Some(head) if head.eq_ignore_ascii_case(LOC) => &joined[LOC.len()..],
        _ => joined.as_str(),
    };
    let cleaned = text.replace(['[', ']'], " ");
    let parts: Vec<&str> = cleaned
        .split(' ')
        .map(str::trim)
        .filter(|p| !p.is_empty())
        .collect();
    if parts.len() == 4 || (bracketed && parts.len() == 8) {
        let cell = parse_hex(parts[0], false);
        let x = parse_float(parts[1]);
        let y = parse_float(parts[2]);
        let z = parse_float(parts[3]);
        if let (Some(cell), Some(x), Some(y), Some(z)) = (cell, x, y, z)
            && parts[4..].iter().all(|part| parse_float(part).is_some())
        {
            return Some((
                MapPosition::of(cell, Vec3::new(x, y, z)),
                format!(
                    "0x{cell:08X} [{} {} {}]",
                    trimmed(f64::from(x), 3),
                    trimmed(f64::from(y), 3),
                    trimmed(f64::from(z), 3)
                ),
            ));
        }
    }
    if bracketed
        && parts.len() == 3
        && let (Some(x), Some(y), Some(z), Some(here)) = (
            parse_float(parts[0]),
            parse_float(parts[1]),
            parse_float(parts[2]),
            here_cell,
        )
    {
        return Some((
            MapPosition::of(here, Vec3::new(x, y, z)),
            format!(
                "0x{here:08X} [{} {} {}]",
                trimmed(f64::from(x), 3),
                trimmed(f64::from(y), 3),
                trimmed(f64::from(z), 3)
            ),
        ));
    }
    if words.len() == 2
        && let (Some(north_south), Some(east_west)) = (
            parse_compass(&words[0], 'N', 'S'),
            parse_compass(&words[1], 'E', 'W'),
        )
    {
        return Some((
            MapPosition {
                cell_id: 0,
                east_west,
                north_south,
                elevation: f64::NAN,
            },
            format!(
                "{}{} {}{}",
                trimmed(north_south.abs(), 3),
                if north_south < 0.0 { 'S' } else { 'N' },
                trimmed(east_west.abs(), 3),
                if east_west < 0.0 { 'W' } else { 'E' }
            ),
        ));
    }
    None
}

/// The object words name: the selected one when no words or a target is
/// given, an object id written `0x...`, or an object by name.
#[must_use]
pub fn parse_target(words: &[String]) -> NavTarget {
    if words.is_empty()
        || (words.len() == 1
            && (is(&words[0], "target") || is(&words[0], "selected") || is(&words[0], "selection")))
    {
        return NavTarget::Selected;
    }
    if words.len() == 1
        && let Some(id) = parse_hex(&words[0], true)
    {
        return NavTarget::Id(id);
    }
    NavTarget::Name(words.join(" "))
}

/// `/nav go to ...`: a place or an object, and the arrival distance.
///
/// # Errors
///
/// When `within` is malformed.
pub fn parse_go_to(words: &[String], here_cell: Option<u32>) -> Result<(NavGoal, f32), String> {
    let (goal, arrival) = split_within(words)?;
    if let Some((place, label)) = parse_place(&goal, here_cell) {
        return Ok((NavGoal::Place(place, label), arrival));
    }
    Ok((NavGoal::Object(parse_target(&goal)), arrival))
}

/// `/nav stand on ...`: an object and the arrival distance.
///
/// # Errors
///
/// When `within` is malformed.
pub fn parse_stand_on(words: &[String]) -> Result<(NavTarget, f32), String> {
    let (goal, arrival) = split_within(words)?;
    Ok((parse_target(&goal), arrival))
}

/// `/nav follow ...`: a player, and the buffer to keep behind them as a last
/// number or after `within` (3 m without one).
///
/// # Errors
///
/// When `within` is malformed.
pub fn parse_follow(words: &[String]) -> Result<(NavTarget, f32), String> {
    let (mut goal, mut buffer) = split_within(words)?;
    if goal.len() == words.len() {
        buffer = DEFAULT_FOLLOW_METERS;
        if let Some(last) = goal.last()
            && !last.get(..2).is_some_and(|p| p.eq_ignore_ascii_case("0x"))
            && let Some(trailing) = parse_float(last)
        {
            buffer = trailing;
            goal.pop();
        }
    }
    Ok((parse_target(&goal), buffer))
}

/// What one `/motor` move does once started.
#[derive(Debug, Clone, Copy, PartialEq)]
pub enum MotorAction {
    /// A scripted move.
    Move(MoveRequest),
    /// Turn to a compass heading, degrees.
    FaceHeading(f32),
    /// Charge and release a jump at a power.
    Jump(f32),
}

/// A move to start: the channel it occupies, if any, so two on one channel
/// are refused; whether it is a scripted move whose end the move report
/// says; how it is named; and what it does.
#[derive(Debug, Clone, PartialEq)]
pub struct MotorMove {
    /// The channel it runs on.
    pub channel: Option<MoveChannel>,
    /// Whether the move report says how it ends.
    pub reported: bool,
    /// How it is named in chat.
    pub label: String,
    /// What it does.
    pub action: MotorAction,
}

/// What a `/motor` line asks for.
#[derive(Debug, Clone, PartialEq)]
pub enum MotorCommand {
    /// Print [`MOTOR_USAGE`].
    Help,
    /// Say the latest move on each channel.
    Status,
    /// Stop every move, or one channel's.
    Stop(Option<MoveChannel>),
    /// Start these moves together, in the order written.
    Moves(Vec<MotorMove>),
}

fn amount_label(amount: f32, unit: MoveUnit, distance_unit: &str) -> String {
    if amount == 0.0 {
        " until stopped".to_owned()
    } else if unit == MoveUnit::Seconds {
        format!(" for {} s", trimmed(f64::from(amount), 2))
    } else {
        format!(" {} {distance_unit}", trimmed(f64::from(amount), 2))
    }
}

fn parse_amount(words: &[String]) -> Option<(f32, MoveUnit)> {
    match words {
        [] => Some((0.0, MoveUnit::MetersOrDegrees)),
        [word] => {
            let (number, unit) = match word.strip_suffix(['s', 'S']) {
                Some(number) => (number, MoveUnit::Seconds),
                None => (word.as_str(), MoveUnit::MetersOrDegrees),
            };
            parse_float(number).map(|amount| (amount, unit))
        }
        _ => None,
    }
}

fn parse_channel(word: &str) -> Option<MoveChannel> {
    if is(word, "travel") {
        Some(MoveChannel::Travel)
    } else if is(word, "strafe") {
        Some(MoveChannel::Strafe)
    } else if is(word, "turn") {
        Some(MoveChannel::Turn)
    } else {
        None
    }
}

fn channel_name(channel: MoveChannel) -> &'static str {
    match channel {
        MoveChannel::Travel => "travel",
        MoveChannel::Strafe => "strafe",
        MoveChannel::Turn => "turn",
    }
}

fn capitalize(word: &str) -> String {
    let mut chars = word.chars();
    chars.next().map_or_else(String::new, |first| {
        first.to_uppercase().collect::<String>() + chars.as_str()
    })
}

fn parse_move(words: &[String]) -> Result<MotorMove, String> {
    let Some(first) = words.first() else {
        return Err("an empty move".to_owned());
    };
    let verb = first.to_lowercase();
    match verb.as_str() {
        "walk" | "run" => {
            let pace = if verb == "walk" {
                MovePace::Walk
            } else {
                MovePace::Run
            };
            let mut direction = MoveDirection::Forward;
            let mut next = 1;
            if words.len() > 1 && is(&words[1], "backward") {
                direction = MoveDirection::Backward;
                next = 2;
            } else if words.len() > 1 && is(&words[1], "forward") {
                next = 2;
            }
            let Some((amount, unit)) = parse_amount(&words[next..]) else {
                return Err(format!("{verb} takes an amount such as 10 or 20s"));
            };
            let label = format!(
                "{} {}{}",
                capitalize(&verb),
                if direction == MoveDirection::Backward {
                    "backward"
                } else {
                    "forward"
                },
                amount_label(amount, unit, "m")
            );
            Ok(MotorMove {
                channel: Some(MoveChannel::Travel),
                reported: true,
                label,
                action: MotorAction::Move(MoveRequest {
                    direction,
                    pace,
                    amount,
                    unit,
                }),
            })
        }
        "strafe" => {
            if words.len() < 2 || !(is(&words[1], "left") || is(&words[1], "right")) {
                return Err("strafe takes left or right".to_owned());
            }
            let direction = if is(&words[1], "left") {
                MoveDirection::StrafeLeft
            } else {
                MoveDirection::StrafeRight
            };
            let Some((amount, unit)) = parse_amount(&words[2..]) else {
                return Err("strafe takes an amount such as 2 or 3s".to_owned());
            };
            Ok(MotorMove {
                channel: Some(MoveChannel::Strafe),
                reported: true,
                label: format!(
                    "Strafe {}{}",
                    words[1].to_lowercase(),
                    amount_label(amount, unit, "m")
                ),
                action: MotorAction::Move(MoveRequest {
                    direction,
                    pace: MovePace::Run,
                    amount,
                    unit,
                }),
            })
        }
        "turn" => {
            if words.len() == 3
                && is(&words[1], "to")
                && let Some(heading) = parse_float(&words[2])
            {
                return Ok(MotorMove {
                    channel: Some(MoveChannel::Turn),
                    reported: false,
                    label: format!("Turn to {}°", trimmed(f64::from(heading), 1)),
                    action: MotorAction::FaceHeading(heading),
                });
            }
            if words.len() < 2 || !(is(&words[1], "left") || is(&words[1], "right")) {
                return Err("turn takes left, right, or to a heading".to_owned());
            }
            let direction = if is(&words[1], "left") {
                MoveDirection::TurnLeft
            } else {
                MoveDirection::TurnRight
            };
            let Some((amount, unit)) = parse_amount(&words[2..]) else {
                return Err("turn takes degrees such as 90, or seconds such as 2s".to_owned());
            };
            Ok(MotorMove {
                channel: Some(MoveChannel::Turn),
                reported: true,
                label: format!(
                    "Turn {}{}",
                    words[1].to_lowercase(),
                    amount_label(amount, unit, "°")
                ),
                action: MotorAction::Move(MoveRequest {
                    direction,
                    pace: MovePace::Run,
                    amount,
                    unit,
                }),
            })
        }
        "jump" => {
            let power = match words {
                [_] => Some(1.0),
                [_, power] => parse_float(power),
                _ => None,
            };
            let Some(power) = power else {
                return Err("jump takes a power from above 0 to 1".to_owned());
            };
            Ok(MotorMove {
                channel: None,
                reported: false,
                label: format!("Jump at {}", trimmed(f64::from(power), 2)),
                action: MotorAction::Jump(power),
            })
        }
        _ => Err(format!("{verb} is not a move")),
    }
}

/// What a `/motor` line asks for.
///
/// # Errors
///
/// With the reply to give: a move not understood (with [`MOTOR_USAGE`]),
/// two moves on one channel, or a stop that names no channel.
pub fn parse_motor(arguments: &str) -> Result<MotorCommand, String> {
    let parts: Vec<&str> = arguments.split('+').map(str::trim).collect();
    if parts.iter().all(|part| part.is_empty()) || is(arguments.trim(), "help") {
        return Ok(MotorCommand::Help);
    }
    let first = words(parts[0]);
    if parts.len() == 1 && first.first().is_some_and(|word| is(word, "status")) {
        return Ok(MotorCommand::Status);
    }
    if parts.len() == 1 && first.first().is_some_and(|word| is(word, "stop")) {
        return match &first[1..] {
            [] => Ok(MotorCommand::Stop(None)),
            [channel] if parse_channel(channel).is_some() => {
                Ok(MotorCommand::Stop(parse_channel(channel)))
            }
            _ => Err(format!(
                "Motor: stop takes travel, strafe or turn. {MOTOR_USAGE}"
            )),
        };
    }
    let mut moves = Vec::with_capacity(parts.len());
    let mut channels = Vec::new();
    for part in parts {
        let parsed = parse_move(&words(part))
            .map_err(|problem| format!("Motor: {problem}. {MOTOR_USAGE}"))?;
        if let Some(channel) = parsed.channel {
            if channels.contains(&channel) {
                return Err(format!(
                    "Motor: two moves on the {} channel cannot run together",
                    channel_name(channel)
                ));
            }
            channels.push(channel);
        }
        moves.push(parsed);
    }
    Ok(MotorCommand::Moves(moves))
}

/// How a walk report reads in chat (`/nav status`).
#[must_use]
pub fn describe_walk(report: &WalkReport) -> String {
    let state = match report.state {
        WalkState::Planning => "planning",
        WalkState::Walking => "walking",
        WalkState::Waiting => "waiting",
        WalkState::Arrived => "arrived",
        WalkState::ArrivedWithoutSight => "arrived without a line of sight",
        WalkState::NoRoute => "no route",
        WalkState::Blocked => "blocked",
        WalkState::Stopped => "stopped",
        WalkState::Interrupted => "interrupted",
        WalkState::Lost => "lost",
        WalkState::None | WalkState::Planned => "idle",
    };
    let mut text = if report.reason.trim().is_empty() || report.reason == state {
        state.to_owned()
    } else {
        format!("{state} ({})", report.reason)
    };
    if report.blocked_by_object_id != 0 {
        text.push_str(&format!(
            ", blocked by 0x{:08X}",
            report.blocked_by_object_id
        ));
    }
    if report.remaining_meters.is_finite() {
        text.push_str(&format!(", {} m left", fixed(report.remaining_meters, 1)));
    }
    text
}

/// How one channel's latest move reads in chat (`/motor status`).
#[must_use]
pub fn describe_move(progress: &crate::motor::MoveChannelSnapshot) -> String {
    if progress.sequence == 0 {
        return "none".to_owned();
    }
    let state = match progress.state {
        ScriptedMoveState::Moving => "moving",
        ScriptedMoveState::Completed => "completed",
        ScriptedMoveState::Stopped => "stopped",
        ScriptedMoveState::TimeLimit => "ran out of time",
        ScriptedMoveState::Blocked => "blocked",
        ScriptedMoveState::Interrupted => "interrupted by the player",
        ScriptedMoveState::Lost => "lost",
        ScriptedMoveState::None => "none",
    };
    let unit = if matches!(
        progress.request.direction,
        MoveDirection::TurnLeft | MoveDirection::TurnRight
    ) {
        "°"
    } else {
        " m"
    };
    format!(
        "{state} after {}{unit} in {} s",
        trimmed(f64::from(progress.covered), 2),
        trimmed(f64::from(progress.elapsed_seconds), 1)
    )
}

#[cfg(test)]
mod tests {
    //! The parsing cases of OpenAC
    //! `tests/AcDream.Runtime.Tests/Navigation/NavigationChatCommandsTests.cs`;
    //! the replies, registry and debug narration around them are the
    //! runtime's.

    use super::*;

    fn line(text: &str) -> Vec<String> {
        words(text)
    }

    fn place(text: &str, here: Option<u32>) -> (MapPosition, f32) {
        match parse_go_to(&line(text), here).expect("parses") {
            (NavGoal::Place(place, _), arrival) => (place, arrival),
            other => panic!("not a place: {other:?}"),
        }
    }

    fn close(actual: f32, expected: f32) {
        assert!(
            (actual - expected).abs() < 0.005,
            "{actual} is not {expected}"
        );
    }

    #[test]
    fn go_to_a_cell_and_its_local_point_walks_to_that_place() {
        let (place, arrival) = place("7E0307A3 310.353577 -312.687317 6.005", None);

        assert_eq!(place.cell_id, 0x7E03_07A3);
        let local = place.landblock_local();
        close(local.x, 310.353_58);
        close(local.y, -312.687_3);
        close(local.z, 6.005);
        assert_eq!(arrival, 2.5);
    }

    #[test]
    fn go_to_takes_an_arrival_distance_and_a_prefixed_cell() {
        let (place, arrival) = place("0x7E0307A3 310 -312 6 within 4", None);

        assert_eq!(place.cell_id, 0x7E03_07A3);
        assert_eq!(arrival, 4.0);
    }

    #[test]
    fn go_to_the_target_walks_to_the_selected_object() {
        for text in ["target", "", "selected"] {
            assert_eq!(
                parse_go_to(&line(text), None).unwrap(),
                (NavGoal::Object(NavTarget::Selected), 2.5)
            );
        }
    }

    #[test]
    fn stand_on_takes_a_target_an_id_or_a_name() {
        assert_eq!(
            parse_stand_on(&line("target")).unwrap(),
            (NavTarget::Selected, 2.5)
        );
        assert_eq!(
            parse_stand_on(&line("")).unwrap(),
            (NavTarget::Selected, 2.5)
        );
        assert_eq!(
            parse_stand_on(&line("0x70000456 within 1")).unwrap(),
            (NavTarget::Id(0x7000_0456), 1.0)
        );
        assert_eq!(
            parse_stand_on(&line("Rock")).unwrap(),
            (NavTarget::Name("Rock".to_owned()), 2.5)
        );
    }

    #[test]
    fn follow_takes_a_player_and_a_buffer_distance() {
        let cases = [
            ("Bob 5", NavTarget::Name("Bob".to_owned()), 5.0),
            ("Bob", NavTarget::Name("Bob".to_owned()), 3.0),
            ("target", NavTarget::Selected, 3.0),
            ("", NavTarget::Selected, 3.0),
            ("8", NavTarget::Selected, 8.0),
            ("0x70000456 within 4", NavTarget::Id(0x7000_0456), 4.0),
        ];
        for (text, target, buffer) in cases {
            assert_eq!(
                parse_follow(&line(text)).unwrap(),
                (target, buffer),
                "{text}"
            );
        }
    }

    #[test]
    fn go_to_an_object_id_or_a_name() {
        assert_eq!(
            parse_go_to(&line("0x70000456 within 3"), None).unwrap(),
            (NavGoal::Object(NavTarget::Id(0x7000_0456)), 3.0)
        );
        assert_eq!(
            parse_go_to(&line("Town Network Portal"), None).unwrap(),
            (
                NavGoal::Object(NavTarget::Name("Town Network Portal".to_owned())),
                2.5
            )
        );
    }

    #[test]
    fn go_to_map_coordinates_stands_on_the_ground_there() {
        let (place, _) = place("24.3N 101.1W", None);

        assert_eq!(place.cell_id, 0);
        assert!((place.north_south - 24.3).abs() < 1e-5);
        assert!((place.east_west + 101.1).abs() < 1e-5);
        assert!(place.elevation.is_nan());
    }

    #[test]
    fn go_to_takes_the_line_loc_prints() {
        for text in [
            "0x7E0307A3 [310.353577 -312.687317 6.005000] 1.000000 0.000000 0.000000 0.000000",
            "Your location is: 0x7E0307A3 [310.353577 -312.687317 6.005000] 0.707107 0.000000 0.000000 -0.707107",
            "7E0307A3 [310.353577 -312.687317 6.005000]",
        ] {
            let (place, arrival) = place(text, None);
            assert_eq!(place.cell_id, 0x7E03_07A3, "{text}");
            let local = place.landblock_local();
            close(local.x, 310.353_58);
            close(local.y, -312.687_3);
            close(local.z, 6.005);
            assert_eq!(arrival, 2.5);
        }
    }

    #[test]
    fn go_to_a_bracketed_point_alone_is_in_the_characters_own_cell() {
        let (place, arrival) = place(
            "[68.145149 -60.000000 0.005000] within 3",
            Some(0xA9B4_0021),
        );

        assert_eq!(place.cell_id, 0xA9B4_0021);
        let local = place.landblock_local();
        close(local.x, 68.145_15);
        close(local.y, -60.0);
        assert_eq!(arrival, 3.0);
    }

    #[test]
    fn a_malformed_within_is_refused() {
        assert_eq!(
            parse_go_to(&line("target within"), None).unwrap_err(),
            "within takes a distance in meters, such as within 3"
        );
    }

    #[test]
    fn moves_joined_with_plus_start_together_in_the_order_written() {
        let MotorCommand::Moves(moves) = parse_motor("run forward 20s + turn left 90").unwrap()
        else {
            panic!("not moves");
        };

        assert_eq!(moves.len(), 2);
        assert_eq!(moves[0].label, "Run forward for 20 s");
        assert_eq!(
            moves[0].action,
            MotorAction::Move(MoveRequest::for_seconds(
                MoveDirection::Forward,
                MovePace::Run,
                20.0
            ))
        );
        assert_eq!(moves[1].label, "Turn left 90 °");
        assert_eq!(moves[1].channel, Some(MoveChannel::Turn));
    }

    #[test]
    fn two_moves_on_one_channel_are_refused_and_nothing_starts() {
        assert_eq!(
            parse_motor("run forward + walk backward 2").unwrap_err(),
            "Motor: two moves on the travel channel cannot run together"
        );
    }

    #[test]
    fn stop_ends_one_channel_or_every_move() {
        assert_eq!(parse_motor("stop").unwrap(), MotorCommand::Stop(None));
        assert_eq!(
            parse_motor("stop strafe").unwrap(),
            MotorCommand::Stop(Some(MoveChannel::Strafe))
        );
        assert!(parse_motor("stop sideways").is_err());
        assert_eq!(parse_motor("status").unwrap(), MotorCommand::Status);
        assert_eq!(parse_motor("").unwrap(), MotorCommand::Help);
    }

    #[test]
    fn a_jump_takes_a_power_and_a_turn_takes_a_heading() {
        let MotorCommand::Moves(moves) = parse_motor("jump 0.5 + turn to 90").unwrap() else {
            panic!("not moves");
        };
        assert_eq!(moves[0].action, MotorAction::Jump(0.5));
        assert_eq!(moves[0].label, "Jump at 0.5");
        assert_eq!(moves[1].action, MotorAction::FaceHeading(90.0));
        assert_eq!(moves[1].label, "Turn to 90°");
        assert!(
            parse_motor("fly 3")
                .unwrap_err()
                .starts_with("Motor: fly is not a move.")
        );
    }

    #[test]
    fn a_walk_report_reads_with_its_reason_blocker_and_distance() {
        let report = WalkReport {
            sequence: 3,
            state: WalkState::Blocked,
            object_id: 0x7000_0456,
            remaining_meters: 4.26,
            replans: 6,
            reason: "a door".to_owned(),
            blocked_by_object_id: 0x7A00_0001,
        };

        assert_eq!(
            describe_walk(&report),
            "blocked (a door), blocked by 0x7A000001, 4.3 m left"
        );
        assert_eq!(
            describe_walk(&WalkReport {
                state: WalkState::Planned,
                remaining_meters: f32::NAN,
                reason: "a route was found".to_owned(),
                blocked_by_object_id: 0,
                ..report
            }),
            "idle (a route was found)"
        );
    }
}
