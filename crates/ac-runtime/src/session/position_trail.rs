//! Where the client and the server last said the player was.
//!
//! A server that loses track of the player in its world answers every
//! spell the player casts at themselves as if the target were not there
//! (use-done error [`TARGET_NOT_ACQUIRED`]), and keeps doing so until the
//! character logs in again. The client cannot see why. So the session
//! keeps a short trail of every position it sent for the player and every
//! position the server sent back, and the first such refusal after a self
//! cast that went through hands the host that trail ([`PlayerNotFound`]),
//! once, so the episode explains itself in the log.
//!
//! A character that casts nothing at itself never gets that refusal, so the
//! trail is also handed over when the client has been sending the player's
//! position for [`UNANSWERED_POSITIONS_LIMIT`] and the server has sent none
//! back: the server answers every position it takes with the player's
//! position, so a long silence means it is no longer moving the player.

use std::collections::VecDeque;
use std::time::Duration;

use ac_proto::Position;

use crate::gameplay::SpellCastCompletion;

/// How far back the trail reaches.
pub const POSITION_TRAIL_WINDOW: Duration = Duration::from_secs(120);

/// How many entries the trail keeps at most, however recent.
pub const POSITION_TRAIL_CAP: usize = 512;

/// The use-done error the server answers when it cannot find the cast's
/// target where the caster is.
pub const TARGET_NOT_ACQUIRED: u32 = 0x042C;

/// A message that carries the player's position.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TrailMessage {
    /// Sent: the motion state.
    MoveToState,
    /// Sent: the position while the character moves on its own.
    AutonomousPosition,
    /// Sent: a jump.
    Jump,
    /// Received: the player moved.
    UpdatePosition,
    /// Received: the player's object was created.
    CreateObject,
    /// Received: the player's object was described again.
    UpdateObject,
}

/// What the client's world did with a position the server sent.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Acceptance {
    /// Its counters were taken (the player moved there, unless the world
    /// could not place it).
    Taken,
    /// It forced the player there.
    Forced,
    /// The world kept the position it had (counters not newer, or for
    /// another incarnation).
    Refused,
    /// The world does not know the player's object.
    NoObject,
}

/// A position as the trail keeps it: the full cell id and the offset in
/// its landblock, meters.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct TrailPosition {
    /// Full cell id.
    pub cell_id: u32,
    /// Offset inside the landblock, meters.
    pub origin: [f32; 3],
}

impl From<&Position> for TrailPosition {
    fn from(p: &Position) -> Self {
        Self {
            cell_id: p.cell_id,
            origin: [p.origin.x, p.origin.y, p.origin.z],
        }
    }
}

/// The sequence counters a position message carries (those it does not
/// carry are `None`).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct TrailStamps {
    /// The object's incarnation.
    pub instance: u16,
    /// Position updates (only the server's messages carry it).
    pub position: Option<u16>,
    /// The era of server-controlled movement (only the client's messages
    /// carry it).
    pub server_control: Option<u16>,
    /// Teleports.
    pub teleport: u16,
    /// Forced positions.
    pub force_position: u16,
}

/// One thing the trail remembers.
#[derive(Debug, Clone, Copy, PartialEq)]
pub enum TrailEvent {
    /// The client sent the player's position.
    Sent {
        /// Which message.
        message: TrailMessage,
        /// Its action number.
        action: u32,
        /// The position.
        position: TrailPosition,
        /// Its counters.
        stamps: TrailStamps,
        /// It said the player stands on the ground.
        contact: bool,
    },
    /// The server sent the player's position.
    Received {
        /// Which message.
        message: TrailMessage,
        /// The position.
        position: TrailPosition,
        /// Its counters.
        stamps: TrailStamps,
        /// What the client's world did with it.
        acceptance: Acceptance,
    },
    /// The server began a teleport.
    TeleportStarted {
        /// Its counter.
        teleport: u16,
    },
    /// A teleport's arrival completed (the server was told).
    TeleportArrived {
        /// Its counter.
        teleport: u16,
        /// Where it arrived.
        cell_id: u32,
    },
    /// The connection's generation ended.
    GenerationEnded,
}

/// One entry, with when it happened (session time).
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct TrailEntry {
    /// Session time.
    pub at: Duration,
    /// What happened.
    pub event: TrailEvent,
}

/// The latest entries, oldest first: those of the last
/// [`POSITION_TRAIL_WINDOW`], at most [`POSITION_TRAIL_CAP`] of them.
#[derive(Debug, Clone)]
pub(crate) struct PositionTrail {
    entries: VecDeque<TrailEntry>,
}

impl Default for PositionTrail {
    fn default() -> Self {
        Self {
            entries: VecDeque::with_capacity(POSITION_TRAIL_CAP),
        }
    }
}

impl PositionTrail {
    /// Remember `event` at `at`, forgetting what fell out of the window
    /// and the oldest beyond the cap.
    pub(crate) fn push(&mut self, at: Duration, event: TrailEvent) {
        self.forget_before(at);
        if self.entries.len() == POSITION_TRAIL_CAP {
            self.entries.pop_front();
        }
        self.entries.push_back(TrailEntry { at, event });
    }

    /// The entries of the window ending at `now`, oldest first.
    pub(crate) fn snapshot(&self, now: Duration) -> Vec<TrailEntry> {
        let start = now.saturating_sub(POSITION_TRAIL_WINDOW);
        self.entries
            .iter()
            .filter(|entry| entry.at >= start)
            .copied()
            .collect()
    }

    fn forget_before(&mut self, now: Duration) {
        let start = now.saturating_sub(POSITION_TRAIL_WINDOW);
        while self.entries.front().is_some_and(|entry| entry.at < start) {
            self.entries.pop_front();
        }
    }
}

/// What the session knew when the server first could not find the player.
#[derive(Debug, Clone, PartialEq)]
pub struct PlayerNotFound {
    /// Session time of the report.
    pub at: Duration,
    /// What showed it.
    pub cause: NotFoundCause,
    /// The positions sent and received, oldest first.
    pub trail: Vec<TrailEntry>,
}

/// What showed that the server lost track of the player.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum NotFoundCause {
    /// A spell cast at the player came back [`TARGET_NOT_ACQUIRED`].
    SelfCastRefused {
        /// The spell cast.
        spell_id: u32,
        /// Its target (the player).
        target: u32,
        /// The server's answer.
        weenie_error: u32,
    },
    /// The server answered none of the positions sent since `since`
    /// (session time), at least [`UNANSWERED_POSITIONS_LIMIT`] ago.
    PositionsUnanswered {
        /// When the first unanswered position was sent.
        since: Duration,
    },
}

/// The shortest time between two reports, so a character whose self casts
/// keep alternating between going through and being refused does not write
/// the trail again and again.
pub const PLAYER_NOT_FOUND_REPORT_INTERVAL: Duration = Duration::from_secs(600);

/// Decides when a self cast's refusal opens an episode: the first
/// [`TARGET_NOT_ACQUIRED`] answer since a self cast went through (or since
/// the generation began); later ones wait until one goes through again. An
/// episode within [`PLAYER_NOT_FOUND_REPORT_INTERVAL`] of the last report is
/// not reported.
#[derive(Debug, Clone)]
pub(crate) struct SelfCastWatch {
    armed: bool,
    last_report: Option<Duration>,
}

impl Default for SelfCastWatch {
    fn default() -> Self {
        Self {
            armed: true,
            last_report: None,
        }
    }
}

impl SelfCastWatch {
    /// A cast's answer at session time `now`, `player` being the local
    /// player's id: true when it opens an episode to report.
    pub(crate) fn observe(
        &mut self,
        completion: &SpellCastCompletion,
        player: u32,
        now: Duration,
    ) -> bool {
        if player == 0 || completion.target_id != player {
            return false;
        }
        match completion.weenie_error {
            0 => {
                self.armed = true;
                false
            }
            TARGET_NOT_ACQUIRED if std::mem::replace(&mut self.armed, false) => {
                // A session clock that started again (a new connection)
                // counts as a long time.
                let due = self.last_report.is_none_or(|last| {
                    now.checked_sub(last)
                        .is_none_or(|since| since >= PLAYER_NOT_FOUND_REPORT_INTERVAL)
                });
                if due {
                    self.last_report = Some(now);
                }
                due
            }
            _ => false,
        }
    }

    /// A new generation began.
    pub(crate) fn rearm(&mut self) {
        self.armed = true;
    }
}

/// How long the client sends the player's position with no answer before
/// the trail is reported.
pub const UNANSWERED_POSITIONS_LIMIT: Duration = Duration::from_secs(30);

/// A pause in sending longer than this starts the wait over: the client
/// sends nothing while the player stands still, so one position the server
/// let pass before a long pause says nothing about the moves after it.
pub const UNANSWERED_SEND_GAP: Duration = Duration::from_secs(10);

/// Decides when unanswered positions open an episode: a position sent at
/// least [`UNANSWERED_POSITIONS_LIMIT`] after the first one the server has
/// not answered, with no pause of more than [`UNANSWERED_SEND_GAP`] between
/// them. Any position the server sends for the player, or a teleport, ends
/// it. An episode is reported once, and not before
/// [`PLAYER_NOT_FOUND_REPORT_INTERVAL`] has passed since the last report.
#[derive(Debug, Clone, Default)]
pub(crate) struct AnswerWatch {
    first_unanswered: Option<Duration>,
    last_sent: Option<Duration>,
    reported: bool,
    last_report: Option<Duration>,
}

impl AnswerWatch {
    /// A position was sent at session time `now`: when it opens an
    /// episode to report, the time of the first unanswered one.
    pub(crate) fn sent(&mut self, now: Duration) -> Option<Duration> {
        // A session clock that started again (a new connection) counts as
        // a long pause.
        let paused = self.last_sent.is_some_and(|last| {
            now.checked_sub(last)
                .is_none_or(|gap| gap > UNANSWERED_SEND_GAP)
        });
        self.last_sent = Some(now);
        if paused {
            self.answered();
        }
        let since = *self.first_unanswered.get_or_insert(now);
        if self.reported
            || now
                .checked_sub(since)
                .is_none_or(|waited| waited < UNANSWERED_POSITIONS_LIMIT)
        {
            return None;
        }
        // Too soon after the last report: the episode is told once the
        // interval has passed, if it still lasts.
        let due = self.last_report.is_none_or(|last| {
            now.checked_sub(last)
                .is_none_or(|elapsed| elapsed >= PLAYER_NOT_FOUND_REPORT_INTERVAL)
        });
        if !due {
            return None;
        }
        self.reported = true;
        self.last_report = Some(now);
        Some(since)
    }

    /// The server sent the player's position (or moved the player itself),
    /// or a new generation began: the wait starts over.
    pub(crate) fn answered(&mut self) {
        self.first_unanswered = None;
        self.reported = false;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const PLAYER: u32 = 0x5000_0001;

    fn at(millis: u64) -> Duration {
        Duration::from_millis(millis)
    }

    fn marker(teleport: u16) -> TrailEvent {
        TrailEvent::TeleportStarted { teleport }
    }

    fn teleports(entries: &[TrailEntry]) -> Vec<u16> {
        entries
            .iter()
            .map(|entry| match entry.event {
                TrailEvent::TeleportStarted { teleport } => teleport,
                _ => unreachable!(),
            })
            .collect()
    }

    fn answer(target: u32, weenie_error: u32) -> SpellCastCompletion {
        SpellCastCompletion {
            revision: 1,
            spell_id: 48,
            target_id: target,
            weenie_error,
        }
    }

    #[test]
    fn the_trail_keeps_only_the_last_two_minutes() {
        let mut trail = PositionTrail::default();
        trail.push(at(0), marker(1));
        trail.push(at(10_000), marker(2));
        trail.push(at(125_000), marker(3));
        // At 125 s the window starts at 5 s: the entry at 0 s is gone.
        assert_eq!(teleports(&trail.snapshot(at(125_000))), [2, 3]);
        // A snapshot later still leaves out what fell out since.
        assert_eq!(teleports(&trail.snapshot(at(131_000))), [3]);
        // Exactly at the window's start is kept.
        assert_eq!(teleports(&trail.snapshot(at(130_000))), [2, 3]);
    }

    #[test]
    fn the_trail_keeps_at_most_its_cap_oldest_first() {
        let mut trail = PositionTrail::default();
        for index in 0..POSITION_TRAIL_CAP + 3 {
            trail.push(at(index as u64), marker(index as u16));
        }
        let entries = trail.snapshot(at(POSITION_TRAIL_CAP as u64 + 3));
        assert_eq!(entries.len(), POSITION_TRAIL_CAP);
        assert_eq!(teleports(&entries)[0], 3, "the three oldest are forgotten");
        assert_eq!(
            teleports(&entries)[POSITION_TRAIL_CAP - 1],
            POSITION_TRAIL_CAP as u16 + 2
        );
    }

    #[test]
    fn the_first_refusal_of_a_self_cast_opens_an_episode_once() {
        let mut watch = SelfCastWatch::default();
        assert!(watch.observe(&answer(PLAYER, TARGET_NOT_ACQUIRED), PLAYER, at(0)));
        assert!(!watch.observe(&answer(PLAYER, TARGET_NOT_ACQUIRED), PLAYER, at(0)));
        // Another error does not end the episode.
        assert!(!watch.observe(&answer(PLAYER, 0x0400), PLAYER, at(0)));
        assert!(!watch.observe(&answer(PLAYER, TARGET_NOT_ACQUIRED), PLAYER, at(0)));
    }

    #[test]
    fn a_self_cast_that_goes_through_ends_the_episode() {
        let mut watch = SelfCastWatch::default();
        assert!(watch.observe(&answer(PLAYER, TARGET_NOT_ACQUIRED), PLAYER, at(0)));
        assert!(!watch.observe(&answer(PLAYER, 0), PLAYER, at(1_000)));
        assert!(watch.observe(&answer(PLAYER, TARGET_NOT_ACQUIRED), PLAYER, at(600_000)));
    }

    #[test]
    fn episodes_are_reported_at_most_once_per_interval() {
        let mut watch = SelfCastWatch::default();
        assert!(watch.observe(&answer(PLAYER, TARGET_NOT_ACQUIRED), PLAYER, at(0)));
        assert!(!watch.observe(&answer(PLAYER, 0), PLAYER, at(1_000)));
        // A second episode inside the interval is not reported...
        assert!(!watch.observe(&answer(PLAYER, TARGET_NOT_ACQUIRED), PLAYER, at(2_000)));
        assert!(!watch.observe(&answer(PLAYER, 0), PLAYER, at(3_000)));
        // ...the next one after it is.
        assert!(watch.observe(&answer(PLAYER, TARGET_NOT_ACQUIRED), PLAYER, at(600_000)));
        // A session clock that started again counts as long enough.
        assert!(!watch.observe(&answer(PLAYER, 0), PLAYER, at(600_500)));
        assert!(watch.observe(&answer(PLAYER, TARGET_NOT_ACQUIRED), PLAYER, at(5_000)));
    }

    #[test]
    fn a_new_generation_ends_the_episode() {
        let mut watch = SelfCastWatch::default();
        assert!(watch.observe(&answer(PLAYER, TARGET_NOT_ACQUIRED), PLAYER, at(0)));
        watch.rearm();
        assert!(watch.observe(&answer(PLAYER, TARGET_NOT_ACQUIRED), PLAYER, at(600_000)));
    }

    #[test]
    fn positions_unanswered_for_the_limit_open_an_episode_once() {
        let mut watch = AnswerWatch::default();
        // A position a second, none answered.
        assert_eq!(sending(&mut watch, 1_000, 30_000), []);
        assert_eq!(
            watch.sent(at(30_999)),
            None,
            "29.999 s is not yet long enough"
        );
        assert_eq!(watch.sent(at(31_000)), Some(at(1_000)));
        assert_eq!(
            sending(&mut watch, 32_000, 90_000),
            [],
            "the episode is reported once"
        );
    }

    /// Positions sent once a second from `from` to `to` milliseconds
    /// (both included): the reports they opened, as (sent, since) in ms.
    fn sending(watch: &mut AnswerWatch, from: u64, to: u64) -> Vec<(u64, u64)> {
        (from..=to)
            .step_by(1_000)
            .filter_map(|millis| {
                watch
                    .sent(at(millis))
                    .map(|since| (millis, since.as_millis() as u64))
            })
            .collect()
    }

    #[test]
    fn an_answer_starts_the_wait_over() {
        let mut watch = AnswerWatch::default();
        assert_eq!(sending(&mut watch, 0, 20_000), []);
        watch.answered();
        assert_eq!(sending(&mut watch, 21_000, 50_000), [], "counted from 21 s");
        assert_eq!(watch.sent(at(51_000)), Some(at(21_000)));
    }

    #[test]
    fn a_pause_in_sending_starts_the_wait_over() {
        let mut watch = AnswerWatch::default();
        assert_eq!(watch.sent(at(0)), None);
        // Standing still sends nothing; the moves minutes later are timed
        // from their own start.
        assert_eq!(watch.sent(at(10_000)), None, "10 s is no pause yet");
        assert_eq!(sending(&mut watch, 200_000, 229_000), []);
        assert_eq!(watch.sent(at(230_000)), Some(at(200_000)));
    }

    #[test]
    fn an_episode_inside_the_interval_is_reported_once_it_has_passed() {
        let mut watch = AnswerWatch::default();
        assert_eq!(sending(&mut watch, 0, 30_000), [(30_000, 0)]);
        watch.answered();
        // A second episode inside the interval is held back...
        assert_eq!(sending(&mut watch, 100_000, 629_000), []);
        // ...and told when the interval since the report at 30 s has passed,
        // if it still lasts.
        assert_eq!(sending(&mut watch, 630_000, 700_000), [(630_000, 100_000)]);
        // A session clock that started again counts as long enough.
        watch.answered();
        assert_eq!(sending(&mut watch, 1_000, 31_000), [(31_000, 1_000)]);
    }

    #[test]
    fn a_cast_at_someone_else_neither_opens_nor_ends_an_episode() {
        let mut watch = SelfCastWatch::default();
        assert!(!watch.observe(&answer(0x8000_0002, TARGET_NOT_ACQUIRED), PLAYER, at(0)));
        assert!(!watch.observe(&answer(PLAYER, TARGET_NOT_ACQUIRED), 0, at(0)));
        assert!(watch.observe(&answer(PLAYER, TARGET_NOT_ACQUIRED), PLAYER, at(0)));
        assert!(!watch.observe(&answer(0x8000_0002, 0), PLAYER, at(0)));
        assert!(!watch.observe(&answer(PLAYER, TARGET_NOT_ACQUIRED), PLAYER, at(0)));
    }
}
