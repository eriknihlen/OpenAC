//! A development aid: the stream and the commands as JSON, for trying a
//! remote window before its interface is built for the browser. A small
//! program (the `remote_codec` example) reads stream frames and JSON
//! commands on its input and answers each with one line of JSON.
//!
//! The summary carries only what a first test shows (chat, status text,
//! the vitals) and counts the rest. It is not the wire encoding and is
//! never sent to a relay.

use ac_host_core::json::{self, Member, Node, Value};
use ac_proto::Position;
use ac_runtime::movement::{MovementCommand, MovementKeys};
use ac_runtime::session::{Generation, LifecycleCommand, PresentationEvent, SessionCommand};
use ac_runtime::world::{ObjectDelta, VitalReading};

/// Follows one stream and says what its events hold, with where the
/// player stands whenever that changed enough to show on a map.
#[derive(Debug, Default)]
pub struct Summarizer {
    /// The player's object, from the snapshot.
    player: Option<u32>,
    /// The place last told.
    told: Option<Position>,
}

impl Summarizer {
    /// A summarizer for a stream from its start.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// One line of JSON saying what `events` hold.
    pub fn summarize(&mut self, events: &[PresentationEvent]) -> String {
        // Where the batch leaves the player, if it says.
        let mut place = None;
        for event in events {
            match event {
                PresentationEvent::Snapshot(snapshot) => {
                    self.player = snapshot.player_guid;
                    self.told = None;
                    place = snapshot
                        .objects
                        .iter()
                        .find(|object| Some(object.guid) == self.player)
                        .and_then(|object| object.position);
                }
                PresentationEvent::Deltas(deltas) => {
                    for delta in deltas {
                        if let ObjectDelta::Upsert(object) = delta
                            && Some(object.guid) == self.player
                            && object.position.is_some()
                        {
                            place = object.position;
                        }
                    }
                }
                _ => {}
            }
        }
        let tail = place
            .filter(|place| self.told.is_none_or(|told| shows(&told, place)))
            .map(|place| {
                self.told = Some(place);
                format!(
                    r#"{{"kind":"position","cell":{},"x":{:.3},"y":{:.3},"z":{:.3},"heading":{:.1}}}"#,
                    place.cell_id,
                    place.origin.x,
                    place.origin.y,
                    place.origin.z,
                    heading(&place)
                )
            });
        render(events, tail)
    }
}

/// Metres the player moves before a new place is told.
const TOLD_DISTANCE: f32 = 0.5;
/// Degrees the player turns before a new place is told.
const TOLD_TURN: f32 = 5.0;

/// Compass degrees a position faces: north 0, east 90.
fn heading(place: &Position) -> f32 {
    let q = place.orientation;
    ac_motion::get_heading(glam::Quat::from_xyzw(q.x, q.y, q.z, q.w))
}

/// Whether `now` is far enough from `told` to show on a map.
fn shows(told: &Position, now: &Position) -> bool {
    let (a, b) = (told.origin, now.origin);
    let moved = ((a.x - b.x).powi(2) + (a.y - b.y).powi(2) + (a.z - b.z).powi(2)).sqrt();
    let turned = (heading(told) - heading(now)).abs();
    told.cell_id != now.cell_id || moved >= TOLD_DISTANCE || turned.min(360.0 - turned) >= TOLD_TURN
}

/// One line of JSON saying what `events` hold, without following the
/// player.
#[must_use]
pub fn summarize(events: &[PresentationEvent]) -> String {
    render(events, None)
}

/// The summary line, with `tail` (one more event, already JSON) after the
/// events' own.
fn render(events: &[PresentationEvent], tail: Option<String>) -> String {
    let mut out = String::from(r#"{"type":"events","events":["#);
    let mut first = true;
    let mut next = |out: &mut String| {
        if !std::mem::take(&mut first) {
            out.push(',');
        }
    };
    for event in events {
        match event {
            PresentationEvent::Chat(lines) => {
                for line in lines {
                    next(&mut out);
                    out.push_str(r#"{"kind":"chat","text":"#);
                    json::write_string(&mut out, &line.text);
                    out.push_str(&format!(r#","class":{}}}"#, line.log_text_type));
                }
            }
            PresentationEvent::StatusText(text) => {
                next(&mut out);
                out.push_str(r#"{"kind":"status","text":"#);
                json::write_string(&mut out, text);
                out.push('}');
            }
            PresentationEvent::Player(view) => {
                next(&mut out);
                out.push_str(r#"{"kind":"vitals","health":"#);
                vital(&mut out, &view.health);
                out.push_str(r#","stamina":"#);
                vital(&mut out, &view.stamina);
                out.push_str(r#","mana":"#);
                vital(&mut out, &view.mana);
                out.push('}');
            }
            PresentationEvent::Snapshot(snapshot) => {
                next(&mut out);
                out.push_str(&format!(
                    r#"{{"kind":"snapshot","objects":{}}}"#,
                    snapshot.objects.len()
                ));
            }
            other => {
                next(&mut out);
                let (name, count) = counted(other);
                out.push_str(&format!(
                    r#"{{"kind":"other","name":"{name}","count":{count}}}"#
                ));
            }
        }
    }
    if let Some(tail) = tail {
        next(&mut out);
        out.push_str(&tail);
    }
    out.push_str("]}");
    out
}

fn vital(out: &mut String, reading: &VitalReading) {
    let number = |value: Option<u32>| value.map_or_else(|| "null".to_owned(), |v| v.to_string());
    out.push_str(&format!(
        r#"{{"current":{},"max":{}}}"#,
        number(reading.current),
        number(reading.max)
    ));
}

/// The name of an event the summary only counts, and how many things it
/// holds.
fn counted(event: &PresentationEvent) -> (&'static str, usize) {
    match event {
        PresentationEvent::Snapshot(snapshot) => ("Snapshot", snapshot.objects.len()),
        PresentationEvent::Deltas(deltas) => ("Deltas", deltas.len()),
        PresentationEvent::Motions(motions) => ("Motions", motions.len()),
        PresentationEvent::Chat(lines) => ("Chat", lines.len()),
        PresentationEvent::GameTime(_) => ("GameTime", 1),
        PresentationEvent::Sound(_) => ("Sound", 1),
        PresentationEvent::PlayScript(_) => ("PlayScript", 1),
        PresentationEvent::PlayScriptType(_) => ("PlayScriptType", 1),
        PresentationEvent::DestroyParticles(_) => ("DestroyParticles", 1),
        PresentationEvent::AdminEnvirons(_) => ("AdminEnvirons", 1),
        PresentationEvent::Player(_) => ("Player", 1),
        PresentationEvent::Interface(_) => ("Interface", 1),
        PresentationEvent::PortalSpace(_) => ("PortalSpace", 1),
        PresentationEvent::PreGame(_) => ("PreGame", 1),
        PresentationEvent::StatusText(_) => ("StatusText", 1),
        PresentationEvent::WindowRequest(_) => ("WindowRequest", 1),
        PresentationEvent::Link(_) => ("Link", 1),
        PresentationEvent::Navigation(_) => ("Navigation", 1),
        PresentationEvent::SessionClock(_) => ("SessionClock", 1),
    }
}

/// The command a JSON request asks for, stamped with its generation, and
/// the request's id; or the id (when it could be read) and what is wrong.
pub fn command_from_json(text: &str) -> Result<(u64, SessionCommand), (Option<u64>, String)> {
    let node = json::parse(text.as_bytes()).map_err(|error| (None, error.to_string()))?;
    let members =
        json::object_members(&node).map_err(|_| (None, "not a JSON object".to_owned()))?;
    let id = number(members, "id").ok_or((None, "no numeric \"id\"".to_owned()))?;
    let fail = |why: String| (Some(id), why);
    let generation = number(members, "generation")
        .ok_or_else(|| fail("no numeric \"generation\"".to_owned()))?;
    let name = match find(members, "cmd") {
        Some(Value::String(name)) => name.as_str(),
        _ => return Err(fail("no \"cmd\"".to_owned())),
    };
    let command = match name {
        "keys" => {
            let key = |name: &str| match find(members, name) {
                None => Ok(false),
                Some(Value::Bool(held)) => Ok(*held),
                Some(_) => Err(fail(format!("\"{name}\" is not true or false"))),
            };
            SessionCommand::Movement(MovementCommand::Keys(MovementKeys {
                forward: key("forward")?,
                backward: key("backward")?,
                strafe_left: key("strafe_left")?,
                strafe_right: key("strafe_right")?,
                turn_left: key("turn_left")?,
                turn_right: key("turn_right")?,
                walk: key("walk")?,
                jump: key("jump")?,
            }))
        }
        "release_keys" => SessionCommand::Movement(MovementCommand::ReleaseKeys),
        "chat" => match find(members, "text") {
            Some(Value::String(text)) => SessionCommand::Chat(text.clone()),
            _ => return Err(fail("\"chat\" needs a \"text\"".to_owned())),
        },
        "log_off" => SessionCommand::Lifecycle(LifecycleCommand::EndCharacterSession),
        "reconnect" => SessionCommand::Lifecycle(LifecycleCommand::Reconnect),
        other => return Err(fail(format!("unknown \"cmd\": {other}"))),
    };
    Ok((
        id,
        SessionCommand::AtGeneration {
            generation: Generation(generation),
            command: Box::new(command),
        },
    ))
}

fn find<'a>(members: &'a [Member], name: &str) -> Option<&'a Value> {
    members
        .iter()
        .find(|member| member.name == name)
        .map(|member: &Member| -> &Node { &member.value })
        .map(|node| &node.value)
}

fn number(members: &[Member], name: &str) -> Option<u64> {
    match find(members, name) {
        Some(Value::Number(digits)) => digits.parse().ok(),
        _ => None,
    }
}

/// The reply to a command request: its bytes, as hex.
#[must_use]
pub fn command_reply(id: u64, bytes: &[u8]) -> String {
    let hex: String = bytes.iter().map(|byte| format!("{byte:02x}")).collect();
    format!(r#"{{"type":"command","id":{id},"hex":"{hex}"}}"#)
}

/// The reply to a record that could not be used.
#[must_use]
pub fn error_reply(id: Option<u64>, message: &str) -> String {
    let mut out = String::from(r#"{"type":"error","#);
    if let Some(id) = id {
        out.push_str(&format!(r#""id":{id},"#));
    }
    out.push_str(r#""message":"#);
    json::write_string(&mut out, message);
    out.push('}');
    out
}

#[cfg(test)]
mod tests {
    use ac_core::chat::ChatKind;
    use ac_runtime::communication::ChatLine;
    use ac_runtime::world::PlayerView;

    use super::*;

    fn parsed(line: &str) -> Vec<Member> {
        assert!(!line.contains('\n'), "one line: {line:?}");
        let node = json::parse(line.as_bytes()).unwrap_or_else(|e| panic!("{e}: {line}"));
        json::object_members(&node).unwrap().to_vec()
    }

    fn member<'a>(members: &'a [Member], name: &str) -> &'a Value {
        &members
            .iter()
            .find(|m| m.name == name)
            .unwrap_or_else(|| panic!("no member {name}"))
            .value
            .value
    }

    fn text_of(value: &Value) -> &str {
        match value {
            Value::String(text) | Value::Number(text) => text,
            other => panic!("{other:?}"),
        }
    }

    fn events_of(line: &str) -> Vec<Vec<Member>> {
        let members = parsed(line);
        assert_eq!(text_of(member(&members, "type")), "events");
        match member(&members, "events") {
            Value::Array(items) => items
                .iter()
                .map(|item| json::object_members(item).unwrap().to_vec())
                .collect(),
            other => panic!("{other:?}"),
        }
    }

    fn chat_line(text: &str, class: u32) -> ChatLine {
        ChatLine {
            sequence: 1,
            kind: ChatKind::LocalSpeech,
            log_text_type: class,
            combat_kind: None,
            received_unix_ms: 0,
            text: text.to_owned(),
            channel_name: String::new(),
            sender: String::new(),
            sender_guid: 0,
            words: String::new(),
            display_text: String::new(),
            channel_id: 0,
            spans: None,
        }
    }

    #[test]
    fn chat_status_and_vitals_are_shown_and_the_rest_counted() {
        let view = PlayerView {
            health: VitalReading {
                current: Some(5),
                max: Some(10),
                percent: Some(0.5),
            },
            ..PlayerView::default()
        };
        let line = summarize(&[
            PresentationEvent::StatusText("Busy \"now\"".to_owned()),
            PresentationEvent::Chat(vec![chat_line("You say, \"hi\"", 2), chat_line("b", 0)]),
            PresentationEvent::Player(view),
            PresentationEvent::GameTime(1.0),
            PresentationEvent::Deltas(Vec::new()),
        ]);
        let events = events_of(&line);
        let kinds: Vec<&str> = events.iter().map(|e| text_of(member(e, "kind"))).collect();
        assert_eq!(
            kinds,
            ["status", "chat", "chat", "vitals", "other", "other"]
        );
        assert_eq!(text_of(member(&events[0], "text")), "Busy \"now\"");
        assert_eq!(text_of(member(&events[1], "text")), "You say, \"hi\"");
        assert_eq!(text_of(member(&events[1], "class")), "2");
        let Value::Object(health) = member(&events[3], "health") else {
            panic!("health");
        };
        assert_eq!(text_of(member(health, "current")), "5");
        assert_eq!(text_of(member(health, "max")), "10");
        // A vital not known yet is null, not zero.
        let Value::Object(mana) = member(&events[3], "mana") else {
            panic!("mana");
        };
        assert_eq!(member(mana, "current"), &Value::Null);
        assert_eq!(text_of(member(&events[4], "name")), "GameTime");
        assert_eq!(text_of(member(&events[5], "name")), "Deltas");
        assert_eq!(text_of(member(&events[5], "count")), "0");
    }

    use ac_proto::{Quaternion, Vector3};
    use ac_runtime::world::{ObjectDelta, WorldSnapshot};

    const PLAYER: u32 = 0x5000_000A;

    /// The player at (`x`, 80, 42) in one cell, facing north or east.
    fn player_at(x: f32, east: bool) -> ac_runtime::world::ObjectView {
        let half = std::f32::consts::FRAC_1_SQRT_2;
        let mut view = crate::tests::object_view(1);
        view.guid = PLAYER;
        view.position = Some(Position {
            cell_id: 0xC6A9_001C,
            origin: Vector3 {
                x,
                y: 80.0,
                z: 42.0,
            },
            orientation: if east {
                Quaternion {
                    w: half,
                    x: 0.0,
                    y: 0.0,
                    z: -half,
                }
            } else {
                Quaternion {
                    w: 1.0,
                    x: 0.0,
                    y: 0.0,
                    z: 0.0,
                }
            },
        });
        view
    }

    fn moved(view: ac_runtime::world::ObjectView) -> PresentationEvent {
        PresentationEvent::Deltas(vec![ObjectDelta::Upsert(Box::new(view))])
    }

    fn positions(line: &str) -> Vec<Vec<Member>> {
        events_of(line)
            .into_iter()
            .filter(|e| text_of(member(e, "kind")) == "position")
            .collect()
    }

    fn number_of(members: &[Member], name: &str) -> f64 {
        text_of(member(members, name)).parse().unwrap()
    }

    #[test]
    fn the_snapshot_says_where_the_player_stands() {
        let mut summary = Summarizer::new();
        let snapshot = PresentationEvent::Snapshot(WorldSnapshot {
            player_guid: Some(PLAYER),
            objects: vec![crate::tests::object_view(2), player_at(77.5, true)],
        });
        let line = summary.summarize(&[snapshot]);
        let told = positions(&line);
        assert_eq!(told.len(), 1, "{line}");
        assert_eq!(number_of(&told[0], "cell"), f64::from(0xC6A9_001C_u32));
        assert_eq!(number_of(&told[0], "x"), 77.5);
        assert_eq!(number_of(&told[0], "y"), 80.0);
        assert_eq!(number_of(&told[0], "z"), 42.0);
        // Compass degrees: north 0, east 90.
        assert!(
            (number_of(&told[0], "heading") - 90.0).abs() < 0.01,
            "{line}"
        );
    }

    #[test]
    fn the_player_moving_or_turning_is_told_and_a_shuffle_or_another_object_is_not() {
        let mut summary = Summarizer::new();
        let snapshot = PresentationEvent::Snapshot(WorldSnapshot {
            player_guid: Some(PLAYER),
            objects: vec![player_at(77.5, false)],
        });
        assert_eq!(positions(&summary.summarize(&[snapshot])).len(), 1);
        // A few centimetres: not worth a line.
        assert!(positions(&summary.summarize(&[moved(player_at(77.6, false))])).is_empty());
        // Another object moving is not the player.
        let mut other = player_at(90.0, false);
        other.guid = 0x8000_0001;
        assert!(positions(&summary.summarize(&[moved(other)])).is_empty());
        // Far enough.
        let told = positions(&summary.summarize(&[moved(player_at(78.2, false))]));
        assert_eq!(told.len(), 1);
        assert_eq!(number_of(&told[0], "x"), 78.2);
        assert!(number_of(&told[0], "heading").abs() < 0.01);
        // A turn on the spot.
        let told = positions(&summary.summarize(&[moved(player_at(78.2, true))]));
        assert_eq!(told.len(), 1);
        // Two moves in one batch: the last place, once.
        let both = PresentationEvent::Deltas(vec![
            ObjectDelta::Upsert(Box::new(player_at(80.0, true))),
            ObjectDelta::Upsert(Box::new(player_at(82.0, true))),
        ]);
        let told = positions(&summary.summarize(&[both]));
        assert_eq!(told.len(), 1);
        assert_eq!(number_of(&told[0], "x"), 82.0);
    }

    #[test]
    fn no_events_is_still_one_line() {
        assert!(events_of(&summarize(&[])).is_empty());
    }

    fn stamped(text: &str) -> (u64, Generation, SessionCommand) {
        match command_from_json(text) {
            Ok((
                id,
                SessionCommand::AtGeneration {
                    generation,
                    command,
                },
            )) => (id, generation, *command),
            other => panic!("{other:?}"),
        }
    }

    #[test]
    fn held_keys_are_the_full_set_stamped_with_the_generation() {
        let (id, generation, command) = stamped(
            r#"{"cmd":"keys","id":9,"generation":4,"forward":true,"turn_left":true,"walk":true}"#,
        );
        assert_eq!((id, generation), (9, Generation(4)));
        let expected = MovementKeys {
            forward: true,
            turn_left: true,
            walk: true,
            ..MovementKeys::default()
        };
        assert!(
            matches!(command, SessionCommand::Movement(MovementCommand::Keys(keys)) if keys == expected),
            "{command:?}"
        );
    }

    #[test]
    fn the_other_commands() {
        let (_, _, command) = stamped(r#"{"cmd":"release_keys","id":1,"generation":2}"#);
        assert!(matches!(
            command,
            SessionCommand::Movement(MovementCommand::ReleaseKeys)
        ));
        let (_, _, command) =
            stamped(r#"{"cmd":"chat","id":1,"generation":2,"text":"/t Bob, hi"}"#);
        assert!(matches!(command, SessionCommand::Chat(text) if text == "/t Bob, hi"));
        let (_, _, command) = stamped(r#"{"cmd":"log_off","id":1,"generation":2}"#);
        assert!(matches!(
            command,
            SessionCommand::Lifecycle(LifecycleCommand::EndCharacterSession)
        ));
        let (_, _, command) = stamped(r#"{"cmd":"reconnect","id":1,"generation":2}"#);
        assert!(matches!(
            command,
            SessionCommand::Lifecycle(LifecycleCommand::Reconnect)
        ));
    }

    #[test]
    fn a_request_that_cannot_be_used_says_why_with_its_id_when_it_has_one() {
        let (id, why) = command_from_json(r#"{"cmd":"dance","id":3,"generation":1}"#).unwrap_err();
        assert_eq!(id, Some(3));
        assert!(why.contains("dance"), "{why}");
        let (id, _) = command_from_json(r#"{"cmd":"keys","id":4}"#).unwrap_err();
        assert_eq!(id, Some(4), "no generation");
        let (id, _) = command_from_json(r#"{"cmd":"chat","id":5,"generation":1}"#).unwrap_err();
        assert_eq!(id, Some(5), "no text");
        let (id, _) = command_from_json(r#"{"cmd":"keys","generation":1}"#).unwrap_err();
        assert_eq!(id, None);
        assert_eq!(command_from_json("{ nope").unwrap_err().0, None);
        let (id, _) = command_from_json(r#"{"cmd":"keys","id":6,"generation":1,"forward":"yes"}"#)
            .unwrap_err();
        assert_eq!(id, Some(6));
    }

    #[test]
    fn replies_are_one_line_of_json() {
        let reply = parsed(&command_reply(7, &[0x00, 0xAB, 0xFF]));
        assert_eq!(text_of(member(&reply, "type")), "command");
        assert_eq!(text_of(member(&reply, "id")), "7");
        assert_eq!(text_of(member(&reply, "hex")), "00abff");
        let reply = parsed(&error_reply(Some(7), "bad\nthing"));
        assert_eq!(text_of(member(&reply, "type")), "error");
        assert_eq!(text_of(member(&reply, "id")), "7");
        assert_eq!(text_of(member(&reply, "message")), "bad\nthing");
        let reply = parsed(&error_reply(None, "x"));
        assert!(reply.iter().all(|m| m.name != "id"));
    }
}
