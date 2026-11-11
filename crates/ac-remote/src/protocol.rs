//! The relay's control messages, endpoint side: what the endpoint sends as
//! JSON text ([`EndpointMessage`]) and what it reads ([`RelayMessage`]).
//!
//! Each message is one JSON object whose `type` member names it. Reading is
//! forward compatible: a message type this version does not know is
//! [`RelayMessage::Unknown`], and members a known message does not define
//! are ignored. Members it does define are checked strictly.

use ac_host_core::json::{self, JsonError, Member, Node, Value};

/// The version of the relay protocol this crate speaks.
pub const PROTOCOL_VERSION: u32 = 1;

/// Where the session is, as the relay and the browser see it.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SessionState {
    /// Logging in, character selection or creation.
    Pregame,
    /// In the world.
    InWorld,
    /// Between two places (portal space).
    PortalSpace,
    /// The game server was lost; logging in again.
    Reconnecting,
}

impl SessionState {
    /// The contract's word for it.
    #[must_use]
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Pregame => "pregame",
            Self::InWorld => "in_world",
            Self::PortalSpace => "portal_space",
            Self::Reconnecting => "reconnecting",
        }
    }
}

/// What the endpoint allows a browser to do.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Capability {
    /// Watch.
    View,
    /// Send input.
    Control,
    /// Log off and reconnect.
    Lifecycle,
}

impl Capability {
    /// The contract's word for it.
    #[must_use]
    pub fn as_str(self) -> &'static str {
        match self {
            Self::View => "view",
            Self::Control => "control",
            Self::Lifecycle => "lifecycle",
        }
    }
}

/// The endpoint's first message on every connection: who it is and where
/// the session is now. Never carries the account name, password or token.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Hello {
    /// The payload encoding's shape fingerprint, as hex.
    pub shape: String,
    /// The version of the browser UI interface.
    pub ui_interface: u32,
    /// The client's version.
    pub client_version: String,
    /// The client's commit, when the build knows it.
    pub client_commit: Option<String>,
    /// Random per process start (see [`new_instance_id`]).
    pub instance_id: String,
    /// The session's id in its session-config document.
    pub config_session_id: String,
    /// The session's login generation.
    pub generation: u64,
    /// The character's name; empty before login.
    pub character_name: String,
    /// The character's id; 0 before login.
    pub character_guid: u32,
    /// The game server, `host:port` as configured.
    pub server: String,
    /// Where the session is.
    pub state: SessionState,
    /// What the endpoint allows.
    pub capabilities: Vec<Capability>,
}

/// A change to the session's state; members left `None` keep their value.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct Status {
    /// The new login generation.
    pub generation: Option<u64>,
    /// The new character name.
    pub character_name: Option<String>,
    /// The new character id.
    pub character_guid: Option<u32>,
    /// The new state.
    pub state: Option<SessionState>,
}

impl Status {
    /// The members of `new` that differ from `old`.
    #[must_use]
    pub fn between(old: &Hello, new: &Hello) -> Self {
        Self {
            generation: (old.generation != new.generation).then_some(new.generation),
            character_name: (old.character_name != new.character_name)
                .then(|| new.character_name.clone()),
            character_guid: (old.character_guid != new.character_guid)
                .then_some(new.character_guid),
            state: (old.state != new.state).then_some(new.state),
        }
    }

    /// True when it changes nothing.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        *self == Self::default()
    }

    /// Writes its members into `hello`.
    pub fn apply_to(&self, hello: &mut Hello) {
        if let Some(generation) = self.generation {
            hello.generation = generation;
        }
        if let Some(name) = &self.character_name {
            hello.character_name.clone_from(name);
        }
        if let Some(guid) = self.character_guid {
            hello.character_guid = guid;
        }
        if let Some(state) = self.state {
            hello.state = state;
        }
    }
}

/// Why an attached request ended.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum DetachReason {
    /// The browser asked (it left, or the relay sent `detach`).
    Browser,
    /// The link to the relay was lost.
    Link,
    /// The endpoint's outbound queue overflowed.
    Overflow,
    /// The browser or relay broke the protocol.
    Protocol,
    /// The session ended.
    SessionEnded,
    /// A newer attach replaced it.
    Replaced,
}

impl DetachReason {
    /// The contract's word for it.
    #[must_use]
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Browser => "browser",
            Self::Link => "link",
            Self::Overflow => "overflow",
            Self::Protocol => "protocol",
            Self::SessionEnded => "session_ended",
            Self::Replaced => "replaced",
        }
    }
}

/// Why the endpoint dropped control by itself.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ControlReason {
    /// No input arrived for the heartbeat time.
    Heartbeat,
    /// Control was released.
    Released,
}

impl ControlReason {
    /// The contract's word for it.
    #[must_use]
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Heartbeat => "heartbeat",
            Self::Released => "released",
        }
    }
}

/// A control message from the endpoint to the relay.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum EndpointMessage {
    /// The first message on a connection.
    Hello(Hello),
    /// The session's state changed.
    Status(Status),
    /// The session accepted an attach; payload frames follow.
    Attached {
        /// The relay's request id.
        request_id: u64,
    },
    /// The session could not attach.
    AttachFailed {
        /// The relay's request id.
        request_id: u64,
        /// Why, for the browser to show.
        reason: String,
    },
    /// An attached request ended.
    Detached {
        /// The relay's request id.
        request_id: u64,
        /// Why.
        reason: DetachReason,
    },
    /// The endpoint dropped control by itself.
    ControlState {
        /// The relay's request id.
        request_id: u64,
        /// Whether the browser still controls (always false today).
        controlling: bool,
        /// Why.
        reason: ControlReason,
    },
}

impl EndpointMessage {
    /// The message as one line of JSON.
    #[must_use]
    pub fn to_json(&self) -> String {
        let mut out = ObjectWriter::new(self.type_name());
        match self {
            Self::Hello(hello) => {
                out.number("protocol", u64::from(PROTOCOL_VERSION));
                out.string("shape", &hello.shape);
                out.number("ui_interface", u64::from(hello.ui_interface));
                out.string("client_version", &hello.client_version);
                if let Some(commit) = &hello.client_commit {
                    out.string("client_commit", commit);
                }
                out.string("instance_id", &hello.instance_id);
                out.string("config_session_id", &hello.config_session_id);
                out.number("generation", hello.generation);
                out.string("character_name", &hello.character_name);
                out.number("character_guid", u64::from(hello.character_guid));
                out.string("server", &hello.server);
                out.string("state", hello.state.as_str());
                out.strings(
                    "capabilities",
                    hello.capabilities.iter().map(|c| c.as_str()),
                );
            }
            Self::Status(status) => {
                if let Some(generation) = status.generation {
                    out.number("generation", generation);
                }
                if let Some(name) = &status.character_name {
                    out.string("character_name", name);
                }
                if let Some(guid) = status.character_guid {
                    out.number("character_guid", u64::from(guid));
                }
                if let Some(state) = status.state {
                    out.string("state", state.as_str());
                }
            }
            Self::Attached { request_id } => out.number("request_id", *request_id),
            Self::AttachFailed { request_id, reason } => {
                out.number("request_id", *request_id);
                out.string("reason", reason);
            }
            Self::Detached { request_id, reason } => {
                out.number("request_id", *request_id);
                out.string("reason", reason.as_str());
            }
            Self::ControlState {
                request_id,
                controlling,
                reason,
            } => {
                out.number("request_id", *request_id);
                out.boolean("controlling", *controlling);
                out.string("reason", reason.as_str());
            }
        }
        out.finish()
    }

    fn type_name(&self) -> &'static str {
        match self {
            Self::Hello(_) => "hello",
            Self::Status(_) => "status",
            Self::Attached { .. } => "attached",
            Self::AttachFailed { .. } => "attach_failed",
            Self::Detached { .. } => "detached",
            Self::ControlState { .. } => "control_state",
        }
    }
}

/// Writes one JSON object, members in the order given. Strings are escaped
/// by the shared JSON writer: quotes, backslashes and control characters,
/// and everything outside printable ASCII as `\uXXXX`.
struct ObjectWriter {
    out: String,
}

impl ObjectWriter {
    fn new(type_name: &str) -> Self {
        let mut out = String::from("{\"type\":");
        json::write_string(&mut out, type_name);
        Self { out }
    }

    fn name(&mut self, name: &str) {
        self.out.push(',');
        json::write_string(&mut self.out, name);
        self.out.push(':');
    }

    fn string(&mut self, name: &str, value: &str) {
        self.name(name);
        json::write_string(&mut self.out, value);
    }

    fn number(&mut self, name: &str, value: u64) {
        self.name(name);
        self.out.push_str(&value.to_string());
    }

    fn boolean(&mut self, name: &str, value: bool) {
        self.name(name);
        self.out.push_str(if value { "true" } else { "false" });
    }

    fn strings<'a>(&mut self, name: &str, values: impl Iterator<Item = &'a str>) {
        self.name(name);
        self.out.push('[');
        for (index, value) in values.enumerate() {
            if index > 0 {
                self.out.push(',');
            }
            json::write_string(&mut self.out, value);
        }
        self.out.push(']');
    }

    fn finish(mut self) -> String {
        self.out.push('}');
        self.out
    }
}

/// Which objects the browser wants.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ObjectsFilter {
    /// None.
    None,
    /// Those within radar range.
    Radar,
    /// All the session knows.
    All,
}

/// What the browser wants streamed.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Subscription {
    /// Motion events.
    pub motions: bool,
    /// Sound events.
    pub sound: bool,
    /// Effect events.
    pub effects: bool,
    /// Navigation events.
    pub navigation: bool,
    /// Which objects.
    pub objects: ObjectsFilter,
}

/// A lifecycle command the browser may send.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum LifecyclePermission {
    /// Log the character off.
    LogOff,
    /// Reconnect to the game server.
    Reconnect,
}

/// What the browser may do. A lifecycle word this version does not know is
/// left out, so it is never granted.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Permissions {
    /// May take control.
    pub control: bool,
    /// The lifecycle commands it may send.
    pub lifecycle: Vec<LifecyclePermission>,
}

/// Why the relay refused the connection.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum RefuseReason {
    /// The protocol versions differ.
    Protocol,
    /// A newer connection with the same session and token replaced this one.
    Duplicate,
    /// The hello came too late.
    HelloTimeout,
    /// A reason this version does not know.
    Other(String),
}

/// A control message from the relay to the endpoint.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum RelayMessage {
    /// The relay accepted the hello.
    HelloOk {
        /// The relay's protocol version.
        protocol: u32,
        /// How often the relay pings, in milliseconds.
        ping_interval_ms: u64,
    },
    /// The relay refuses the connection; a close follows.
    Refused {
        /// Why.
        reason: RefuseReason,
    },
    /// A browser wants the session.
    Attach {
        /// The relay's request id.
        request_id: u64,
        /// What to stream.
        subscription: Subscription,
        /// What the browser may do.
        permissions: Permissions,
    },
    /// The browser left.
    Detach {
        /// The relay's request id.
        request_id: u64,
    },
    /// The browser took (`on`) or released control.
    Control {
        /// The relay's request id.
        request_id: u64,
        /// Taken or released.
        on: bool,
    },
    /// A message type this version does not know; ignore it.
    Unknown {
        /// Its `type`.
        type_name: String,
    },
}

/// Why a control message could not be read. No variant carries its text.
#[derive(Debug, Clone, PartialEq, Eq, thiserror::Error)]
pub enum MessageError {
    /// Not JSON.
    #[error("the message is not JSON: {0}")]
    Json(#[from] JsonError),
    /// JSON, but not an object.
    #[error("the message is not a JSON object")]
    NotAnObject,
    /// No `type` string.
    #[error("the message has no type")]
    NoType,
    /// A known message lacks a member it needs.
    #[error("a '{message}' message has no '{member}'")]
    Missing {
        /// The message type.
        message: &'static str,
        /// The member.
        member: &'static str,
    },
    /// A known member has the wrong type or value.
    #[error("a '{message}' message has a bad '{member}'")]
    Bad {
        /// The message type.
        message: &'static str,
        /// The member.
        member: &'static str,
    },
}

impl RelayMessage {
    /// Reads one control message.
    ///
    /// # Errors
    /// The text is not a JSON object with a `type`, or a known message's
    /// members are missing or of the wrong type.
    pub fn parse(text: &str) -> Result<Self, MessageError> {
        let node = json::parse(text.as_bytes())?;
        let Value::Object(members) = &node.value else {
            return Err(MessageError::NotAnObject);
        };
        let Some(Value::String(type_name)) = member(members, "type").map(|n| &n.value) else {
            return Err(MessageError::NoType);
        };
        let message = match type_name.as_str() {
            "hello_ok" => {
                let read = Fields::new(members, "hello_ok");
                Self::HelloOk {
                    protocol: read.u32("protocol")?,
                    ping_interval_ms: read.u64("ping_interval_ms")?,
                }
            }
            "refused" => {
                let read = Fields::new(members, "refused");
                let reason = match read.string("reason")? {
                    "protocol" => RefuseReason::Protocol,
                    "duplicate" => RefuseReason::Duplicate,
                    "hello_timeout" => RefuseReason::HelloTimeout,
                    other => RefuseReason::Other(other.to_owned()),
                };
                Self::Refused { reason }
            }
            "attach" => {
                let read = Fields::new(members, "attach");
                let request_id = read.u64("request_id")?;
                let wanted = read.object("subscription")?;
                let subscription = Subscription {
                    motions: wanted.boolean("motions")?,
                    sound: wanted.boolean("sound")?,
                    effects: wanted.boolean("effects")?,
                    navigation: wanted.boolean("navigation")?,
                    objects: match wanted.string("objects")? {
                        "none" => ObjectsFilter::None,
                        "radar" => ObjectsFilter::Radar,
                        "all" => ObjectsFilter::All,
                        _ => return Err(wanted.bad("objects")),
                    },
                };
                let permissions = read.object("permissions")?;
                let control = permissions.boolean("control")?;
                let mut lifecycle = Vec::new();
                for item in permissions.array("lifecycle")? {
                    let Value::String(word) = &item.value else {
                        return Err(permissions.bad("lifecycle"));
                    };
                    let granted = match word.as_str() {
                        "log_off" => LifecyclePermission::LogOff,
                        "reconnect" => LifecyclePermission::Reconnect,
                        // Not known here, so not granted.
                        _ => continue,
                    };
                    if !lifecycle.contains(&granted) {
                        lifecycle.push(granted);
                    }
                }
                Self::Attach {
                    request_id,
                    subscription,
                    permissions: Permissions { control, lifecycle },
                }
            }
            "detach" => Self::Detach {
                request_id: Fields::new(members, "detach").u64("request_id")?,
            },
            "control" => {
                let read = Fields::new(members, "control");
                Self::Control {
                    request_id: read.u64("request_id")?,
                    on: read.boolean("on")?,
                }
            }
            other => Self::Unknown {
                type_name: other.to_owned(),
            },
        };
        Ok(message)
    }
}

fn member<'a>(members: &'a [Member], name: &str) -> Option<&'a Node> {
    members.iter().find(|m| m.name == name).map(|m| &m.value)
}

/// Reads the members of one object of a known message; members it is not
/// asked for are ignored.
struct Fields<'a> {
    members: &'a [Member],
    message: &'static str,
}

impl<'a> Fields<'a> {
    fn new(members: &'a [Member], message: &'static str) -> Self {
        Self { members, message }
    }

    fn bad(&self, member: &'static str) -> MessageError {
        MessageError::Bad {
            message: self.message,
            member,
        }
    }

    fn required(&self, name: &'static str) -> Result<&'a Value, MessageError> {
        member(self.members, name)
            .map(|node| &node.value)
            .ok_or(MessageError::Missing {
                message: self.message,
                member: name,
            })
    }

    fn u64(&self, name: &'static str) -> Result<u64, MessageError> {
        match self.required(name)? {
            // Digits only: no sign, fraction or exponent.
            Value::Number(text) if text.bytes().all(|b| b.is_ascii_digit()) => {
                text.parse().map_err(|_| self.bad(name))
            }
            _ => Err(self.bad(name)),
        }
    }

    fn u32(&self, name: &'static str) -> Result<u32, MessageError> {
        u32::try_from(self.u64(name)?).map_err(|_| self.bad(name))
    }

    fn boolean(&self, name: &'static str) -> Result<bool, MessageError> {
        match self.required(name)? {
            Value::Bool(value) => Ok(*value),
            _ => Err(self.bad(name)),
        }
    }

    fn string(&self, name: &'static str) -> Result<&'a str, MessageError> {
        match self.required(name)? {
            Value::String(text) => Ok(text),
            _ => Err(self.bad(name)),
        }
    }

    fn object(&self, name: &'static str) -> Result<Fields<'a>, MessageError> {
        match self.required(name)? {
            Value::Object(members) => Ok(Fields::new(members, self.message)),
            _ => Err(self.bad(name)),
        }
    }

    fn array(&self, name: &'static str) -> Result<&'a [Node], MessageError> {
        match self.required(name)? {
            Value::Array(items) => Ok(items),
            _ => Err(self.bad(name)),
        }
    }
}

/// A fresh instance id: 32 hex digits, different on every call. Not a
/// secret; it tells a reconnect apart from a new process.
#[must_use]
pub fn new_instance_id() -> String {
    format!("{:016x}{:016x}", random_u64(), random_u64())
}

/// 64 bits that differ on every call: each `RandomState` carries keys
/// seeded from the operating system's random source (and stepped on every
/// call), here hashing the time. Unpredictable enough for ids and the
/// WebSocket key and masks; not for secrets.
pub(crate) fn random_u64() -> u64 {
    use std::hash::{BuildHasher, Hasher};
    let mut hasher = std::collections::hash_map::RandomState::new().build_hasher();
    let nanos = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|since| since.as_nanos())
        .unwrap_or_default();
    hasher.write_u128(nanos);
    hasher.finish()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn hello() -> Hello {
        Hello {
            shape: "9f3a".into(),
            ui_interface: 1,
            client_version: "0.2.7".into(),
            client_commit: Some("afa5c5b2".into()),
            instance_id: "5d2c9e".into(),
            config_session_id: "main".into(),
            generation: 3,
            character_name: "Zaphod Mossbeard".into(),
            character_guid: 1_342_177_281,
            server: "game.example:9000".into(),
            state: SessionState::InWorld,
            capabilities: vec![Capability::View, Capability::Control, Capability::Lifecycle],
        }
    }

    #[test]
    fn hello_is_written_as_the_contract_shows() {
        assert_eq!(
            EndpointMessage::Hello(hello()).to_json(),
            concat!(
                r#"{"type":"hello","protocol":1,"shape":"9f3a","ui_interface":1,"#,
                r#""client_version":"0.2.7","client_commit":"afa5c5b2","#,
                r#""instance_id":"5d2c9e","config_session_id":"main","#,
                r#""generation":3,"character_name":"Zaphod Mossbeard","character_guid":1342177281,"#,
                r#""server":"game.example:9000","state":"in_world","#,
                r#""capabilities":["view","control","lifecycle"]}"#
            )
        );
        let mut bare = hello();
        bare.client_commit = None;
        bare.capabilities.clear();
        bare.state = SessionState::Pregame;
        let text = EndpointMessage::Hello(bare).to_json();
        assert!(!text.contains("client_commit"), "{text}");
        assert!(text.contains(r#""state":"pregame""#), "{text}");
        assert!(text.ends_with(r#""capabilities":[]}"#), "{text}");
    }

    #[test]
    fn every_endpoint_message_is_valid_json() {
        for message in [
            EndpointMessage::Hello(hello()),
            EndpointMessage::Status(Status::default()),
            EndpointMessage::Attached { request_id: 7 },
            EndpointMessage::AttachFailed {
                request_id: 7,
                reason: "not in world".into(),
            },
            EndpointMessage::Detached {
                request_id: 7,
                reason: DetachReason::SessionEnded,
            },
            EndpointMessage::ControlState {
                request_id: 7,
                controlling: false,
                reason: ControlReason::Heartbeat,
            },
        ] {
            let text = message.to_json();
            assert!(json::parse(text.as_bytes()).is_ok(), "{text}");
        }
    }

    #[test]
    fn replies_are_written_as_the_contract_shows() {
        let cases = [
            (
                EndpointMessage::Status(Status {
                    generation: Some(4),
                    character_name: Some("A".into()),
                    character_guid: Some(5),
                    state: Some(SessionState::PortalSpace),
                }),
                r#"{"type":"status","generation":4,"character_name":"A","character_guid":5,"state":"portal_space"}"#,
            ),
            (
                EndpointMessage::Status(Status {
                    state: Some(SessionState::Reconnecting),
                    ..Status::default()
                }),
                r#"{"type":"status","state":"reconnecting"}"#,
            ),
            (
                EndpointMessage::Attached { request_id: 7 },
                r#"{"type":"attached","request_id":7}"#,
            ),
            (
                EndpointMessage::AttachFailed {
                    request_id: 7,
                    reason: "busy".into(),
                },
                r#"{"type":"attach_failed","request_id":7,"reason":"busy"}"#,
            ),
            (
                EndpointMessage::ControlState {
                    request_id: 7,
                    controlling: false,
                    reason: ControlReason::Released,
                },
                r#"{"type":"control_state","request_id":7,"controlling":false,"reason":"released"}"#,
            ),
        ];
        for (message, expected) in cases {
            assert_eq!(message.to_json(), expected);
        }
    }

    #[test]
    fn detach_and_control_reasons_use_the_contracts_words() {
        let words: Vec<_> = [
            DetachReason::Browser,
            DetachReason::Link,
            DetachReason::Overflow,
            DetachReason::Protocol,
            DetachReason::SessionEnded,
            DetachReason::Replaced,
        ]
        .iter()
        .map(|reason| {
            EndpointMessage::Detached {
                request_id: 1,
                reason: *reason,
            }
            .to_json()
        })
        .collect();
        for (text, word) in words.iter().zip([
            "browser",
            "link",
            "overflow",
            "protocol",
            "session_ended",
            "replaced",
        ]) {
            assert_eq!(
                text,
                &format!(r#"{{"type":"detached","request_id":1,"reason":"{word}"}}"#)
            );
        }
        assert_eq!(ControlReason::Heartbeat.as_str(), "heartbeat");
        assert_eq!(ControlReason::Released.as_str(), "released");
    }

    #[test]
    fn strings_are_escaped() {
        let mut odd = hello();
        odd.character_name = "Quote\" Back\\ Tab\t Nul\u{0} Bell\u{7} \u{e5}sa \u{1F600}".into();
        let text = EndpointMessage::Hello(odd.clone()).to_json();
        assert!(text.is_ascii(), "{text}");
        // Non-ASCII goes out as UTF-16 escapes: U+00E5, then U+1F600 as a
        // surrogate pair.
        let escaped = format!(
            r#""character_name":"Quote\" Back\\ Tab\t Nul{u}0000 Bell{u}0007 {u}00E5sa {u}D83D{u}DE00""#,
            u = r"\u"
        );
        assert!(text.contains(&escaped), "{text}");
        // It reads back as the same name.
        let node = json::parse(text.as_bytes()).unwrap();
        let name = json::object_members(&node)
            .unwrap()
            .iter()
            .find(|m| m.name == "character_name")
            .map(|m| m.value.value.clone());
        assert_eq!(name, Some(Value::String(odd.character_name)));
    }

    #[test]
    fn status_between_names_only_what_changed() {
        let old = hello();
        let mut new = hello();
        assert!(Status::between(&old, &new).is_empty());
        new.state = SessionState::PortalSpace;
        new.generation = 4;
        let status = Status::between(&old, &new);
        assert_eq!(
            status,
            Status {
                generation: Some(4),
                state: Some(SessionState::PortalSpace),
                ..Status::default()
            }
        );
        let mut applied = old.clone();
        status.apply_to(&mut applied);
        assert_eq!(applied, new);
    }

    #[test]
    fn reads_the_relays_messages() {
        assert_eq!(
            RelayMessage::parse(r#"{"type":"hello_ok","protocol":1,"ping_interval_ms":5000}"#),
            Ok(RelayMessage::HelloOk {
                protocol: 1,
                ping_interval_ms: 5000
            })
        );
        for (word, reason) in [
            ("protocol", RefuseReason::Protocol),
            ("duplicate", RefuseReason::Duplicate),
            ("hello_timeout", RefuseReason::HelloTimeout),
            ("later", RefuseReason::Other("later".into())),
        ] {
            assert_eq!(
                RelayMessage::parse(&format!(r#"{{"type":"refused","reason":"{word}"}}"#)),
                Ok(RelayMessage::Refused { reason })
            );
        }
        assert_eq!(
            RelayMessage::parse(concat!(
                r#"{"type":"attach","request_id":7,"subscription":{"motions":false,"sound":true,"#,
                r#""effects":false,"navigation":true,"objects":"radar"},"#,
                r#""permissions":{"control":true,"lifecycle":["log_off","reconnect"]}}"#
            )),
            Ok(RelayMessage::Attach {
                request_id: 7,
                subscription: Subscription {
                    motions: false,
                    sound: true,
                    effects: false,
                    navigation: true,
                    objects: ObjectsFilter::Radar,
                },
                permissions: Permissions {
                    control: true,
                    lifecycle: vec![LifecyclePermission::LogOff, LifecyclePermission::Reconnect],
                },
            })
        );
        assert_eq!(
            RelayMessage::parse(r#"{"type":"detach","request_id":7}"#),
            Ok(RelayMessage::Detach { request_id: 7 })
        );
        assert_eq!(
            RelayMessage::parse(r#"{"type":"control","request_id":7,"on":true}"#),
            Ok(RelayMessage::Control {
                request_id: 7,
                on: true
            })
        );
    }

    #[test]
    fn objects_filter_words() {
        for (word, filter) in [
            ("none", ObjectsFilter::None),
            ("radar", ObjectsFilter::Radar),
            ("all", ObjectsFilter::All),
        ] {
            let text = format!(
                r#"{{"type":"attach","request_id":1,"subscription":{{"motions":false,"sound":false,"effects":false,"navigation":false,"objects":"{word}"}},"permissions":{{"control":false,"lifecycle":[]}}}}"#
            );
            let Ok(RelayMessage::Attach { subscription, .. }) = RelayMessage::parse(&text) else {
                panic!("{text}");
            };
            assert_eq!(subscription.objects, filter);
        }
    }

    #[test]
    fn unknown_types_and_members_are_tolerated() {
        assert_eq!(
            RelayMessage::parse(r#"{"type":"weather","rain":true}"#),
            Ok(RelayMessage::Unknown {
                type_name: "weather".into()
            })
        );
        assert_eq!(
            RelayMessage::parse(
                r#"{"request_id":7,"extra":{"a":[1,2]},"type":"detach","later":null}"#
            ),
            Ok(RelayMessage::Detach { request_id: 7 })
        );
        // An unknown lifecycle word is not granted; delete in particular.
        let Ok(RelayMessage::Attach { permissions, .. }) = RelayMessage::parse(concat!(
            r#"{"type":"attach","request_id":1,"subscription":{"motions":false,"sound":false,"#,
            r#""effects":false,"navigation":false,"objects":"none","fov":90},"#,
            r#""permissions":{"control":false,"lifecycle":["delete","reconnect"],"admin":true}}"#
        )) else {
            panic!("attach");
        };
        assert_eq!(permissions.lifecycle, vec![LifecyclePermission::Reconnect]);
    }

    #[test]
    fn malformed_messages_are_typed_errors() {
        let error = |text: &str| RelayMessage::parse(text).unwrap_err();
        assert!(matches!(error("not json"), MessageError::Json(_)));
        assert!(matches!(error(""), MessageError::Json(_)));
        assert_eq!(error("[1]"), MessageError::NotAnObject);
        assert_eq!(error(r#"{"a":1}"#), MessageError::NoType);
        assert_eq!(error(r#"{"type":5}"#), MessageError::NoType);
        assert_eq!(
            error(r#"{"type":"detach"}"#),
            MessageError::Missing {
                message: "detach",
                member: "request_id"
            }
        );
        for bad in [r#""7""#, "-1", "7.5", "1e3", "18446744073709551616"] {
            assert_eq!(
                error(&format!(r#"{{"type":"detach","request_id":{bad}}}"#)),
                MessageError::Bad {
                    message: "detach",
                    member: "request_id"
                },
                "{bad}"
            );
        }
        assert_eq!(
            error(r#"{"type":"control","request_id":1,"on":"yes"}"#),
            MessageError::Bad {
                message: "control",
                member: "on"
            }
        );
        assert_eq!(
            error(r#"{"type":"hello_ok","protocol":4294967296,"ping_interval_ms":1}"#),
            MessageError::Bad {
                message: "hello_ok",
                member: "protocol"
            }
        );
        assert_eq!(
            error(
                r#"{"type":"attach","request_id":1,"subscription":{"motions":false,"sound":false,"effects":false,"navigation":false,"objects":"some"},"permissions":{"control":false,"lifecycle":[]}}"#
            ),
            MessageError::Bad {
                message: "attach",
                member: "objects"
            }
        );
        assert_eq!(
            error(
                r#"{"type":"attach","request_id":1,"subscription":{"sound":false,"effects":false,"navigation":false,"objects":"all"},"permissions":{"control":false,"lifecycle":[]}}"#
            ),
            MessageError::Missing {
                message: "attach",
                member: "motions"
            }
        );
        assert_eq!(
            error(
                r#"{"type":"attach","request_id":1,"subscription":{"motions":false,"sound":false,"effects":false,"navigation":false,"objects":"all"},"permissions":{"control":false,"lifecycle":[3]}}"#
            ),
            MessageError::Bad {
                message: "attach",
                member: "lifecycle"
            }
        );
        // Strict JSON: a repeated member is refused rather than guessed at.
        assert!(matches!(
            error(r#"{"type":"detach","request_id":1,"request_id":2}"#),
            MessageError::Json(_)
        ));
    }

    #[test]
    fn instance_ids_are_hex_and_distinct() {
        let first = new_instance_id();
        let second = new_instance_id();
        assert_eq!(first.len(), 32);
        assert!(first.bytes().all(|b| b.is_ascii_hexdigit()));
        assert_ne!(first, second);
    }
}
