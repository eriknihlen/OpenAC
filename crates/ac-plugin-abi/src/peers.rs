//! The other clients on this computer (API group M): who they are and where
//! their characters stand, the casts they announce, the command lines they
//! broadcast, and a status board the session's plugins share. A plugin with
//! a transport of its own carries the same further: it reads what this
//! client tells its neighbours and imports what arrives from elsewhere,
//! which then reads back marked remote.

use crate::wire_record;
use crate::world::Position;

wire_record! {
    /// Another client running on this computer, as its last note says (or
    /// one a plugin imported).
    pub struct NetworkClient {
        /// The client's id (stable while it runs).
        pub client_id: u32,
        /// Its character's object id.
        pub player_id: u32,
        /// Its character's name.
        pub name: String,
        /// The world it plays in.
        pub world_name: String,
        /// Where its character stands.
        pub position: Position,
        /// The labels it answers to.
        pub tags: Vec<String>,
        /// Health now.
        pub current_health: u32,
        /// Mana now.
        pub current_mana: u32,
        /// Stamina now.
        pub current_stamina: u32,
        /// Health at most.
        pub max_health: u32,
        /// Mana at most.
        pub max_mana: u32,
        /// Stamina at most.
        pub max_stamina: u32,
        /// Its heading, degrees.
        pub heading: f32,
        /// A plugin handed it in (`peers_import_client`), typically a
        /// character on another computer; false for a client on this
        /// computer and for this client's own record. Ignored on an import.
        pub is_remote: bool,
    }
}

wire_record! {
    /// A cast this client tells the others about.
    pub struct CastAnnouncement {
        /// What it was cast at.
        pub target_id: u32,
        /// The spell.
        pub spell_id: u32,
        /// The skill it was cast with.
        pub effective_skill: i32,
        /// How long the effect lasts, seconds (0 for an attempt).
        pub duration_seconds: f64,
        /// It landed (a success), rather than being attempted.
        pub landed: bool,
    }
}

wire_record! {
    /// A cast another client announced (or this client's own, read back
    /// for a relay).
    pub struct PeerCast {
        /// This client's number for it; grows.
        pub sequence: i64,
        /// The client that announced it.
        pub client_id: u32,
        /// Its caster.
        pub caster_id: u32,
        /// Its target.
        pub target_id: u32,
        /// The spell.
        pub spell_id: u32,
        /// The skill it was cast with.
        pub effective_skill: i32,
        /// Seconds of the effect left now.
        pub seconds_remaining: f64,
        /// It landed.
        pub landed: bool,
        /// A plugin imported it rather than it being read from a client on
        /// this computer.
        pub is_remote: bool,
    }
}

wire_record! {
    /// Ask the other clients to run a command line.
    pub struct Broadcast {
        /// The line, as it would be typed.
        pub line: String,
        /// The labels it is aimed at; none aims it at every client.
        pub tags: Vec<String>,
        /// How far apart the recipients run it, milliseconds per place.
        pub delay_ms: i32,
    }
}

wire_record! {
    /// A command line another client broadcast (or this client's own, read
    /// back for a relay).
    pub struct PeerCommand {
        /// This client's number for it; grows.
        pub sequence: i64,
        /// The client that sent it.
        pub client_id: u32,
        /// Its sender's character.
        pub sender_id: u32,
        /// The labels it was aimed at.
        pub tags: Vec<String>,
        /// The line.
        pub line: String,
        /// When it was sent, UTC milliseconds since the Unix epoch.
        pub sent_unix_ms: i64,
        /// A plugin imported it rather than it being read from a client on
        /// this computer.
        pub is_remote: bool,
    }
}

wire_record! {
    /// A cast an imported peer said it made.
    pub struct RemoteCast {
        /// The casting character: a peer imported within the last 15 s.
        pub caster_id: u32,
        /// What it was cast at.
        pub target_id: u32,
        /// The spell (one this client's spell table knows).
        pub spell_id: u32,
        /// The skill, 0 when the caster did not say.
        pub effective_skill: i32,
        /// For a landed cast, seconds of the effect left at the import;
        /// ignored for an attempt.
        pub seconds_remaining: f64,
        /// It landed, rather than being attempted.
        pub landed: bool,
    }
}

wire_record! {
    /// A command line an imported peer asked the clients around it to run.
    pub struct RemoteBroadcast {
        /// The sending character: a peer imported within the last 15 s.
        pub sender_id: u32,
        /// The line, as the sender wrote it.
        pub line: String,
        /// The labels it is aimed at; none aims it at every client.
        pub tags: Vec<String>,
        /// The stagger the sender asked for, milliseconds per place.
        pub delay_ms: i32,
    }
}
