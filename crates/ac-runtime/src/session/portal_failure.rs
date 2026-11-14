//! Portal space that does not end.
//!
//! The character's entry into the world and every teleport show portal
//! space until the client has what it needs to show the destination. Either
//! wait can be left without an end: the character's own object never
//! arrives, a packet the server will not send again carried it, or the
//! destination never loads. Nothing else ends such a session, because the
//! server keeps talking.
//!
//! So a session that has been in portal space for longer than
//! [`PORTAL_SPACE_LIMIT`] ends its connection like one whose server fell
//! silent, after telling the host what it knew at that moment (a
//! [`PortalFailure`]). The game itself waits without a limit; this is this
//! client's own rule.

use std::collections::{BTreeMap, VecDeque};
use std::time::Duration;

use ac_net::LinkSnapshot;

use super::Phase;
use crate::world::transit::PortalSnapshot;

/// How long a session may stay in portal space before it is ended.
pub const PORTAL_SPACE_LIMIT: Duration = Duration::from_secs(60);

/// How many of the latest inbound messages a session remembers.
pub const RECENT_INBOUND: usize = 64;

/// Which wait portal space is in.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PortalWait {
    /// The character is entering the world: login complete has not gone.
    Login,
    /// A teleport is under way: its arrival has not completed.
    Teleport,
}

/// Times an unbroken stay in portal space.
#[derive(Debug, Clone)]
pub(crate) struct PortalWatch {
    limit: Duration,
    since: Option<Duration>,
}

impl PortalWatch {
    /// A watch that allows `limit` of portal space.
    pub(crate) fn new(limit: Duration) -> Self {
        Self { limit, since: None }
    }

    /// The session is (`waiting`) or is not in portal space at `now`
    /// (session time): how long the stay has lasted, once that is strictly
    /// more than the limit. Leaving portal space starts the next stay from
    /// nothing; a login followed at once by a teleport is one stay.
    pub(crate) fn observe(&mut self, waiting: bool, now: Duration) -> Option<Duration> {
        if !waiting {
            self.since = None;
            return None;
        }
        let since = *self.since.get_or_insert(now);
        let waited = now.saturating_sub(since);
        (waited > self.limit).then_some(waited)
    }
}

/// One inbound message, as the session remembers it: what it was and when,
/// never what it said.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct InboundNote {
    /// When it was handled (session time).
    pub at: Duration,
    /// Its message type (0 when the body was too short to carry one).
    pub opcode: u32,
    /// The game event it carried, for a game-event message that parsed.
    pub game_event: Option<u32>,
    /// The body's length in bytes.
    pub bytes: usize,
    /// Whether it parsed.
    pub parsed: bool,
}

/// The latest inbound messages, oldest first.
#[derive(Debug, Clone, Default)]
pub(crate) struct RecentInbound {
    notes: VecDeque<InboundNote>,
}

impl RecentInbound {
    /// Remember `note`, forgetting the oldest beyond [`RECENT_INBOUND`].
    pub(crate) fn push(&mut self, note: InboundNote) {
        if self.notes.len() == RECENT_INBOUND {
            self.notes.pop_front();
        }
        self.notes.push_back(note);
    }

    /// The remembered messages, oldest first.
    pub(crate) fn to_vec(&self) -> Vec<InboundNote> {
        self.notes.iter().copied().collect()
    }
}

/// What the world and the arrival looked like when portal space ran out.
#[derive(Debug, Clone, PartialEq)]
pub struct PortalWorldState {
    /// The active teleport's counter (0 for a login).
    pub teleport_sequence: u16,
    /// The reveal under way (its kind, destination and readiness).
    pub reveal: PortalSnapshot,
    /// Acknowledgements the reveal refused as stale.
    pub stale_acknowledgements: u32,
    /// The character's object id, once the session knows it.
    pub player_guid: Option<u32>,
    /// The character's own object arrived.
    pub player_object: bool,
    /// The character's description (vitals, skills, inventory) arrived.
    pub player_described: bool,
    /// Objects the session knows.
    pub objects: usize,
    /// Messages that did not parse, by message type.
    pub parse_failures: BTreeMap<u32, u64>,
    /// A window is attached.
    pub window_attached: bool,
    /// The attached window is minimized (it holds nothing then).
    pub window_minimized: bool,
    /// The arrival (or the login) waits for the attached window to show
    /// the destination.
    pub held_for_window: bool,
    /// The arrival waits for the destination's collision to load (no
    /// window holds it).
    pub held_for_collision: bool,
    /// Whether that destination's collision is loaded, while a placement
    /// waits for it.
    pub collision_ready: Option<bool>,
    /// The session simulates the world (it has the game data).
    pub simulates_world: bool,
    /// The character is placed in the simulated world.
    pub player_placed: bool,
}

/// Everything a session knew when it gave up on portal space.
#[derive(Debug, Clone, PartialEq)]
pub struct PortalFailure {
    /// Which wait it was.
    pub wait: PortalWait,
    /// How long portal space had lasted.
    pub waited: Duration,
    /// The limit it ran over.
    pub limit: Duration,
    /// The session time at that moment.
    pub at: Duration,
    /// Where the login was.
    pub phase: Phase,
    /// Login complete went out.
    pub login_complete_sent: bool,
    /// Login complete waits (for the window, or for the character to enter
    /// the simulated world).
    pub login_complete_held: bool,
    /// The world and the arrival.
    pub world: PortalWorldState,
    /// The transport.
    pub link: LinkSnapshot,
    /// How long ago the last valid server packet arrived.
    pub since_last_packet: Duration,
    /// Inbound messages received and not yet handled.
    pub unhandled_inbound: usize,
    /// The latest inbound messages, oldest first.
    pub recent: Vec<InboundNote>,
}

#[cfg(test)]
mod tests {
    use super::*;

    const fn seconds(seconds: u64) -> Duration {
        Duration::from_secs(seconds)
    }

    // This client's own rule (the game waits without a limit): a stay is
    // over the limit only when strictly longer, and is timed from the pass
    // that first saw it.
    #[test]
    fn a_stay_longer_than_the_limit_is_reported_with_its_length() {
        assert_eq!(PORTAL_SPACE_LIMIT, seconds(60));
        let mut watch = PortalWatch::new(PORTAL_SPACE_LIMIT);
        assert_eq!(
            watch.observe(false, seconds(5)),
            None,
            "not in portal space"
        );
        assert_eq!(watch.observe(true, seconds(10)), None, "the stay begins");
        assert_eq!(watch.observe(true, seconds(69)), None);
        assert_eq!(watch.observe(true, seconds(70)), None, "exactly the limit");
        assert_eq!(
            watch.observe(true, seconds(70) + Duration::from_nanos(1)),
            Some(seconds(60) + Duration::from_nanos(1))
        );
    }

    #[test]
    fn leaving_portal_space_starts_the_next_stay_from_nothing() {
        let mut watch = PortalWatch::new(seconds(60));
        assert_eq!(watch.observe(true, seconds(0)), None);
        assert_eq!(watch.observe(true, seconds(59)), None);
        assert_eq!(watch.observe(false, seconds(59)), None, "it ended in time");
        assert_eq!(watch.observe(true, seconds(100)), None, "a new stay");
        assert_eq!(watch.observe(true, seconds(160)), None);
        assert_eq!(watch.observe(true, seconds(161)), Some(seconds(61)));
    }

    #[test]
    fn only_the_latest_messages_are_remembered_oldest_first() {
        let mut recent = RecentInbound::default();
        for index in 0..RECENT_INBOUND + 3 {
            recent.push(InboundNote {
                at: Duration::from_millis(index as u64),
                opcode: index as u32,
                game_event: None,
                bytes: 4,
                parsed: true,
            });
        }
        let notes = recent.to_vec();
        assert_eq!(notes.len(), RECENT_INBOUND);
        assert_eq!(notes[0].opcode, 3, "the three oldest are forgotten");
        assert_eq!(notes[RECENT_INBOUND - 1].opcode, RECENT_INBOUND as u32 + 2);
    }
}
