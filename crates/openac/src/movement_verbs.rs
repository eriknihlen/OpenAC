//! The console's movement verbs: walk, run, strafe, turn, jump, stop, the
//! run lock, postures and where the character is. Each becomes a movement
//! command for the session (the same commands a window's keys send).
//!
//! Amounts: a bare number is seconds for `/walk`, `/run` and `/strafe`
//! (`5m` is metres, `5s` seconds) and degrees for `/turn`; no amount keeps
//! going until `/stop` (at most 30 seconds).

use ac_runtime::movement::{
    MoveChannel, MoveDirection, MovePace, MoveRequest, MoveUnit, MovementCommand, MovementEvent,
    Posture, ScriptedMoveState,
};

/// Usage of the movement verbs.
pub const USAGE: &str = "usage: /walk|/run [back] [seconds|<n>m] | /strafe left|right [seconds|<n>m] | \
     /turn [left|right] <degrees> | /turn to <heading> | /jump [power 0..1] | /stop | /autorun | \
     /posture ready|sit|crouch|sleep | /where";

fn amount(word: Option<&str>, bare: MoveUnit) -> Option<(f32, MoveUnit)> {
    let Some(word) = word else {
        return Some((0.0, MoveUnit::MetersOrDegrees));
    };
    let (number, unit) = if let Some(n) = word.strip_suffix(['m', 'M']) {
        (n, MoveUnit::MetersOrDegrees)
    } else if let Some(n) = word.strip_suffix(['s', 'S']) {
        (n, MoveUnit::Seconds)
    } else {
        (word, bare)
    };
    let value: f32 = number.parse().ok()?;
    (value.is_finite() && value >= 0.0).then_some((value, unit))
}

fn request(direction: MoveDirection, pace: MovePace, amount: (f32, MoveUnit)) -> MovementCommand {
    MovementCommand::Move(MoveRequest {
        direction,
        pace,
        amount: amount.0,
        unit: amount.1,
    })
}

/// The movement commands a movement verb asks for: `None` for another
/// verb, `Err` with the usage for one used wrongly.
#[must_use]
pub fn parse(verb: &str, argument: &str) -> Option<Result<MovementCommand, &'static str>> {
    let verb = verb.to_ascii_lowercase();
    let words: Vec<String> = argument
        .split_whitespace()
        .map(str::to_ascii_lowercase)
        .collect();
    let w = |i: usize| words.get(i).map(String::as_str);
    let one = |c: MovementCommand| Some(Ok(c));
    let usage = Some(Err(USAGE));
    match verb.as_str() {
        "walk" | "run" => {
            let pace = if verb == "walk" {
                MovePace::Walk
            } else {
                MovePace::Run
            };
            let (direction, next) = match w(0) {
                Some("back" | "backward") => (MoveDirection::Backward, 1),
                Some("forward") => (MoveDirection::Forward, 1),
                _ => (MoveDirection::Forward, 0),
            };
            if words.len() > next + 1 {
                return usage;
            }
            match amount(w(next), MoveUnit::Seconds) {
                Some(a) => one(request(direction, pace, a)),
                None => usage,
            }
        }
        "strafe" => {
            let direction = match w(0) {
                Some("left") => MoveDirection::StrafeLeft,
                Some("right") => MoveDirection::StrafeRight,
                _ => return usage,
            };
            if words.len() > 2 {
                return usage;
            }
            match amount(w(1), MoveUnit::Seconds) {
                Some(a) => one(request(direction, MovePace::Run, a)),
                None => usage,
            }
        }
        "turn" => {
            if w(0) == Some("to") && words.len() == 2 {
                return match w(1).and_then(|h| h.parse::<f32>().ok()) {
                    Some(h) if h.is_finite() => one(MovementCommand::TurnToHeading(h)),
                    _ => usage,
                };
            }
            let (direction, degrees) = match (w(0), w(1)) {
                (Some("left"), d) => (MoveDirection::TurnLeft, d),
                (Some("right"), d) => (MoveDirection::TurnRight, d),
                (Some(d), None) => match d.parse::<f32>() {
                    Ok(v) if v < 0.0 => {
                        let a = (-v, MoveUnit::MetersOrDegrees);
                        return one(request(MoveDirection::TurnLeft, MovePace::Run, a));
                    }
                    Ok(_) => (MoveDirection::TurnRight, Some(d)),
                    Err(_) => return usage,
                },
                _ => return usage,
            };
            match amount(degrees, MoveUnit::MetersOrDegrees) {
                Some(a) => one(request(direction, MovePace::Run, a)),
                None => usage,
            }
        }
        "jump" => {
            let power = match w(0) {
                None => 1.0,
                Some(p) => match p.parse::<f32>() {
                    Ok(p) => p,
                    Err(_) => return usage,
                },
            };
            one(MovementCommand::Jump {
                power,
                leave_at: None,
            })
        }
        "stop" => one(MovementCommand::Stop),
        "autorun" => one(MovementCommand::ToggleRunLock),
        "where" => one(MovementCommand::Status),
        "posture" => match w(0) {
            Some("ready" | "stand") => one(MovementCommand::Posture(Posture::Ready)),
            Some("sit") => one(MovementCommand::Posture(Posture::Sit)),
            Some("crouch") => one(MovementCommand::Posture(Posture::Crouch)),
            Some("sleep") => one(MovementCommand::Posture(Posture::Sleep)),
            _ => usage,
        },
        _ => None,
    }
}

/// How the console words a movement event.
#[must_use]
pub fn describe(event: &MovementEvent) -> String {
    match event {
        MovementEvent::MoveEnded {
            channel,
            state,
            covered,
            elapsed_seconds,
        } => {
            let what = match channel {
                MoveChannel::Travel => "move",
                MoveChannel::Strafe => "strafe",
                MoveChannel::Turn => "turn",
            };
            let unit = if *channel == MoveChannel::Turn {
                "degrees"
            } else {
                "m"
            };
            let how = match state {
                ScriptedMoveState::Completed => "completed",
                ScriptedMoveState::Stopped => "stopped",
                ScriptedMoveState::TimeLimit => "ran out of time",
                ScriptedMoveState::Blocked => "blocked",
                ScriptedMoveState::Interrupted => "interrupted",
                ScriptedMoveState::Lost => "lost",
                ScriptedMoveState::None | ScriptedMoveState::Moving => "ended",
            };
            format!("{what} {how}: {covered:.2} {unit} in {elapsed_seconds:.2} s")
        }
        MovementEvent::Refused(text) => format!("movement: {text}"),
        MovementEvent::Status(None) => "where: the character is not placed".to_owned(),
        MovementEvent::Status(Some(s)) => {
            let o = s.position.origin;
            format!(
                "where: cell 0x{:08X} ({:.3}, {:.3}, {:.3}) heading {:.1} deg, {}, speed {:.2} m/s, {} landblocks resident{}",
                s.position.cell_id,
                o.x,
                o.y,
                o.z,
                s.heading_degrees,
                if s.on_ground {
                    "on the ground"
                } else {
                    "in the air"
                },
                (s.velocity[0].powi(2) + s.velocity[1].powi(2)).sqrt(),
                s.resident_landblocks,
                if s.auto_run { ", autorun" } else { "" }
            )
        }
        MovementEvent::Residency { loaded, dropped } => format!(
            "collision: loaded {} dropped {}",
            hex_list(loaded),
            hex_list(dropped)
        ),
    }
}

fn hex_list(ids: &[u32]) -> String {
    if ids.is_empty() {
        return "none".to_owned();
    }
    ids.iter()
        .map(|id| format!("{:04X}", id >> 16))
        .collect::<Vec<_>>()
        .join(",")
}

#[cfg(test)]
mod tests {
    use super::*;

    fn commands(verb: &str, argument: &str) -> Vec<MovementCommand> {
        vec![parse(verb, argument).unwrap().unwrap()]
    }

    #[test]
    fn walk_and_run_count_seconds_by_default_and_metres_with_m() {
        assert_eq!(
            commands("walk", "3"),
            [MovementCommand::Move(MoveRequest {
                direction: MoveDirection::Forward,
                pace: MovePace::Walk,
                amount: 3.0,
                unit: MoveUnit::Seconds,
            })]
        );
        assert_eq!(
            commands("run", "back 10m"),
            [MovementCommand::Move(MoveRequest {
                direction: MoveDirection::Backward,
                pace: MovePace::Run,
                amount: 10.0,
                unit: MoveUnit::MetersOrDegrees,
            })]
        );
        // No amount: until stopped.
        assert!(matches!(
            commands("run", "")[0],
            MovementCommand::Move(MoveRequest { amount: 0.0, .. })
        ));
    }

    #[test]
    fn turn_counts_degrees_and_a_negative_turn_goes_left() {
        assert!(matches!(
            commands("turn", "90")[0],
            MovementCommand::Move(MoveRequest {
                direction: MoveDirection::TurnRight,
                amount: 90.0,
                unit: MoveUnit::MetersOrDegrees,
                ..
            })
        ));
        assert!(matches!(
            commands("turn", "-45")[0],
            MovementCommand::Move(MoveRequest {
                direction: MoveDirection::TurnLeft,
                amount: 45.0,
                ..
            })
        ));
        assert_eq!(
            commands("turn", "to 180"),
            [MovementCommand::TurnToHeading(180.0)]
        );
    }

    #[test]
    fn jump_stop_and_the_rest() {
        assert_eq!(
            commands("jump", ""),
            [MovementCommand::Jump {
                power: 1.0,
                leave_at: None
            }]
        );
        assert_eq!(commands("stop", ""), [MovementCommand::Stop]);
        assert_eq!(commands("autorun", ""), [MovementCommand::ToggleRunLock]);
        assert_eq!(commands("where", ""), [MovementCommand::Status]);
        assert!(parse("say", "hello").is_none());
        assert_eq!(parse("strafe", "up"), Some(Err(USAGE)));
        assert_eq!(parse("walk", "x"), Some(Err(USAGE)));
    }
}
