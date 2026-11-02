//! Reading one status line: the version-1 vocabulary `ac-host` writes.

use std::time::SystemTime;

use ac_host::json::{self as host_json, Node, Value};

use crate::time::parse_iso8601;

/// The envelope every status line carries.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct StatusEnvelope {
    /// Vocabulary version (`v`), 0 when unreadable.
    pub v: i32,
    /// Event name (`e`), empty when unreadable.
    pub e: String,
    /// UTC time (`t`); `None` when unreadable.
    pub t: Option<SystemTime>,
    /// The session (`sessionId`), empty when unreadable.
    pub session_id: String,
}

/// One character in a `characterList` event.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct StatusCharacterEntry {
    /// The character's object id.
    pub id: u32,
    /// The character's name.
    pub name: String,
    /// Seconds until a character pending deletion is gone.
    pub seconds_greyed_out: u32,
}

/// What a status line said.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum StatusEventKind {
    /// The host started the session.
    Started,
    /// The session connected.
    Connected,
    /// The server sent the character list.
    CharacterList {
        /// The account the server named.
        account_name: String,
        /// Character slots on the account.
        slot_count: i32,
        /// The characters, in server order.
        characters: Vec<StatusCharacterEntry>,
    },
    /// A character entered the world.
    EnteredWorld {
        /// Its object id.
        character_id: u32,
        /// Its name.
        character_name: String,
    },
    /// A character was created.
    CharacterCreated {
        /// Its object id.
        guid: u32,
        /// Its name.
        name: String,
    },
    /// Character creation failed.
    CreationFailed {
        /// The server's code.
        code: u32,
        /// The reason.
        reason: String,
        /// The name asked for.
        name: String,
    },
    /// A plugin loaded.
    PluginLoaded {
        /// Its id.
        plugin: String,
    },
    /// A plugin failed to load.
    PluginFailed {
        /// Its id.
        plugin: String,
        /// Why.
        error: String,
    },
    /// A login command failed.
    LoginCommandFailed {
        /// Its position, from 0.
        command_index: i32,
        /// The command.
        command: String,
        /// Why.
        error: String,
    },
    /// The session disconnected.
    Disconnected {
        /// Why.
        reason: String,
    },
    /// The host finished the session.
    Exited {
        /// The exit code.
        code: i32,
        /// Why.
        reason: String,
        /// The server's own words, when a refusal from it ended the session
        /// (`serverMessage`, an addition to OpenAC's vocabulary).
        server_message: Option<String>,
        /// That refusal only says the account, or one of its characters, is
        /// still logged on (`accountInUse`; absent means false).
        account_in_use: bool,
    },
    /// The client's session loop is making progress (`heartbeat`, an
    /// addition to OpenAC's vocabulary).
    Heartbeat,
    /// A game window opened on the session (an addition to OpenAC's
    /// vocabulary).
    WindowOpened,
    /// The session's game window closed.
    WindowClosed {
        /// `requested`, `closedByUser`, `renderFailed` or `shutdown`.
        reason: String,
    },
    /// A game window could not be opened on the session.
    WindowFailed {
        /// Why.
        error: String,
    },
    /// An event this launcher does not know, or a line that is not JSON.
    Unknown {
        /// The line as read.
        raw_json: String,
    },
    /// A known event missing or mistyping a field, or a non-object line.
    Malformed {
        /// What is wrong.
        error: String,
    },
}

/// One parsed status line.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct StatusEvent {
    /// The envelope.
    pub envelope: StatusEnvelope,
    /// The event.
    pub kind: StatusEventKind,
}

impl StatusEvent {
    /// The session the line is about.
    pub fn session_id(&self) -> &str {
        &self.envelope.session_id
    }
}

const KNOWN: [&str; 15] = [
    "started",
    "connected",
    "characterList",
    "enteredWorld",
    "pluginLoaded",
    "pluginFailed",
    "loginCommandFailed",
    "characterCreated",
    "creationFailed",
    "disconnected",
    "exited",
    "windowOpened",
    "windowClosed",
    "windowFailed",
    "heartbeat",
];

fn member<'a>(root: &'a Node, name: &str) -> Option<&'a Node> {
    match &root.value {
        // The last occurrence wins, as a lookup in OpenAC's document reader.
        Value::Object(members) => members
            .iter()
            .rev()
            .find(|member| member.name == name)
            .map(|member| &member.value),
        _ => None,
    }
}

fn int32_or_default(root: &Node, name: &str) -> i32 {
    member(root, name)
        .and_then(|node| host_json::int32(node).ok())
        .unwrap_or_default()
}

fn string_or_default(root: &Node, name: &str) -> String {
    match member(root, name).map(|node| &node.value) {
        Some(Value::String(text)) => text.clone(),
        _ => String::new(),
    }
}

fn utc_time_or_default(root: &Node, name: &str) -> Option<SystemTime> {
    match member(root, name).map(|node| &node.value) {
        Some(Value::String(text)) => parse_iso8601(text)
            .filter(|parsed| parsed.offset_minutes == Some(0))
            .map(|parsed| parsed.instant),
        _ => None,
    }
}

fn require<'a>(root: &'a Node, name: &str) -> Result<&'a Node, String> {
    member(root, name).ok_or_else(|| format!("status event is missing '{name}'."))
}

fn require_string(root: &Node, name: &str) -> Result<String, String> {
    match &require(root, name)?.value {
        Value::String(text) => Ok(text.clone()),
        _ => Err(format!("status event field '{name}' is not a string.")),
    }
}

fn optional_string(root: &Node, name: &str) -> Result<Option<String>, String> {
    match member(root, name).map(|node| &node.value) {
        None | Some(Value::Null) => Ok(None),
        Some(Value::String(text)) => Ok(Some(text.clone())),
        Some(_) => Err(format!("status event field '{name}' is not a string.")),
    }
}

fn optional_bool(root: &Node, name: &str) -> Result<bool, String> {
    match member(root, name).map(|node| &node.value) {
        None | Some(Value::Null) => Ok(false),
        Some(Value::Bool(value)) => Ok(*value),
        Some(_) => Err(format!("status event field '{name}' is not a boolean.")),
    }
}

fn require_int32(root: &Node, name: &str) -> Result<i32, String> {
    host_json::int32(require(root, name)?)
        .map_err(|_| format!("status event field '{name}' is not an integer."))
}

fn require_uint32(root: &Node, name: &str) -> Result<u32, String> {
    host_json::uint32(require(root, name)?)
        .map_err(|_| format!("status event field '{name}' is not an unsigned integer."))
}

fn require_utc_timestamp(root: &Node, name: &str) -> Result<SystemTime, String> {
    let Value::String(text) = &require(root, name)?.value else {
        return Err(format!(
            "status event field '{name}' is not an ISO-8601 UTC string."
        ));
    };
    let explicit_utc = text.ends_with('Z') || text.ends_with("+00:00");
    match parse_iso8601(text) {
        Some(parsed) if explicit_utc && parsed.offset_minutes == Some(0) => Ok(parsed.instant),
        _ => Err(format!(
            "status event field '{name}' is not an ISO-8601 UTC timestamp."
        )),
    }
}

fn parse_known(root: &Node, e: &str) -> Result<(StatusEnvelope, StatusEventKind), String> {
    let v = require_int32(root, "v")?;
    if v != 1 {
        return Err(format!("status event version is {v}; expected 1."));
    }
    let t = require_utc_timestamp(root, "t")?;
    let session_id = require_string(root, "sessionId")?;
    if session_id.trim().is_empty() {
        return Err("status event field 'sessionId' is empty.".into());
    }
    let kind = match e {
        "started" => StatusEventKind::Started,
        "connected" => StatusEventKind::Connected,
        "characterList" => {
            let account_name = require_string(root, "accountName")?;
            let slot_count = require_int32(root, "slotCount")?;
            let items = match &require(root, "characters")?.value {
                Value::Array(items) => items,
                _ => return Err("status event field 'characters' is not an array.".into()),
            };
            let characters = items
                .iter()
                .map(|item| {
                    Ok(StatusCharacterEntry {
                        id: require_uint32(item, "id")?,
                        name: require_string(item, "name")?,
                        seconds_greyed_out: require_uint32(item, "secondsGreyedOut")?,
                    })
                })
                .collect::<Result<Vec<_>, String>>()?;
            StatusEventKind::CharacterList {
                account_name,
                slot_count,
                characters,
            }
        }
        "enteredWorld" => StatusEventKind::EnteredWorld {
            character_id: require_uint32(root, "characterId")?,
            character_name: require_string(root, "characterName")?,
        },
        "pluginLoaded" => StatusEventKind::PluginLoaded {
            plugin: require_string(root, "plugin")?,
        },
        "pluginFailed" => StatusEventKind::PluginFailed {
            plugin: require_string(root, "plugin")?,
            error: require_string(root, "error")?,
        },
        "loginCommandFailed" => {
            let command_index = require_int32(root, "commandIndex")?;
            if command_index < 0 {
                return Err("status event field 'commandIndex' is negative.".into());
            }
            StatusEventKind::LoginCommandFailed {
                command_index,
                command: require_string(root, "command")?,
                error: require_string(root, "error")?,
            }
        }
        "characterCreated" => StatusEventKind::CharacterCreated {
            guid: require_uint32(root, "guid")?,
            name: require_string(root, "name")?,
        },
        "creationFailed" => StatusEventKind::CreationFailed {
            code: require_uint32(root, "code")?,
            reason: require_string(root, "reason")?,
            name: require_string(root, "name")?,
        },
        "disconnected" => StatusEventKind::Disconnected {
            reason: require_string(root, "reason")?,
        },
        "exited" => StatusEventKind::Exited {
            code: require_int32(root, "code")?,
            reason: require_string(root, "reason")?,
            server_message: optional_string(root, "serverMessage")?,
            account_in_use: optional_bool(root, "accountInUse")?,
        },
        "heartbeat" => StatusEventKind::Heartbeat,
        "windowOpened" => StatusEventKind::WindowOpened,
        "windowClosed" => StatusEventKind::WindowClosed {
            reason: require_string(root, "reason")?,
        },
        "windowFailed" => StatusEventKind::WindowFailed {
            error: require_string(root, "error")?,
        },
        _ => return Err("known event dispatch is incomplete.".into()),
    };
    Ok((
        StatusEnvelope {
            v,
            e: e.to_owned(),
            t: Some(t),
            session_id,
        },
        kind,
    ))
}

fn envelope_or_defaults(root: &Node, e: &str) -> StatusEnvelope {
    StatusEnvelope {
        v: int32_or_default(root, "v"),
        e: e.to_owned(),
        t: utc_time_or_default(root, "t"),
        session_id: string_or_default(root, "sessionId"),
    }
}

fn unknown_line(raw: &str) -> StatusEvent {
    StatusEvent {
        envelope: StatusEnvelope {
            v: 0,
            e: String::new(),
            t: None,
            session_id: String::new(),
        },
        kind: StatusEventKind::Unknown {
            raw_json: raw.to_owned(),
        },
    }
}

/// Reads one status line; never fails. Text that is not JSON (or is blank)
/// is [`StatusEventKind::Unknown`]; JSON that is not an object, lacks a
/// usable `e`, or is a known event with a missing or mistyped field (the
/// version-1 envelope included) is [`StatusEventKind::Malformed`]; an event
/// name this launcher does not know is `Unknown`, keeping the line.
pub fn parse_status_line(line: &str) -> StatusEvent {
    if line.trim().is_empty() {
        return unknown_line(line);
    }
    let Ok(root) = host_json::parse(line.as_bytes()) else {
        return unknown_line(line);
    };
    if !matches!(root.value, Value::Object(_)) {
        return StatusEvent {
            envelope: StatusEnvelope {
                v: 0,
                e: String::new(),
                t: None,
                session_id: String::new(),
            },
            kind: StatusEventKind::Malformed {
                error: "status event root is not a JSON object.".into(),
            },
        };
    }
    let e = match member(&root, "e").map(|node| &node.value) {
        None => Err("status event is missing 'e'."),
        Some(Value::String(text)) if text.trim().is_empty() => {
            Err("status event field 'e' is empty.")
        }
        Some(Value::String(text)) => Ok(text.clone()),
        Some(_) => Err("status event field 'e' is not a string."),
    };
    let e = match e {
        Ok(e) => e,
        Err(error) => {
            return StatusEvent {
                envelope: envelope_or_defaults(&root, ""),
                kind: StatusEventKind::Malformed {
                    error: error.into(),
                },
            };
        }
    };
    if !KNOWN.contains(&e.as_str()) {
        return StatusEvent {
            envelope: envelope_or_defaults(&root, &e),
            kind: StatusEventKind::Unknown {
                raw_json: line.to_owned(),
            },
        };
    }
    match parse_known(&root, &e) {
        Ok((envelope, kind)) => StatusEvent { envelope, kind },
        Err(error) => StatusEvent {
            envelope: envelope_or_defaults(&root, &e),
            kind: StatusEventKind::Malformed { error },
        },
    }
}
