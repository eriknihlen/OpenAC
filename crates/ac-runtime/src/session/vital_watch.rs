//! What the client log needs to explain a character left at 0 health after
//! a death.
//!
//! On a death the server sets the character's health to 0, sends it to its
//! lifestone, and a few seconds into that teleport sends health, stamina
//! and mana back at 75% (ACE `Player_Death.cs`, `ThreadSafeTeleportOnDeath`).
//! Login complete at the arrival ends the server's portal space; until then
//! it moves the character nowhere (ACE `Player_Location.cs`,
//! `OnTeleportComplete`). A character still at 0 health long after the
//! arrival either had its restore refused here or never had its portal
//! space ended there. So every vital update of the player's own is logged
//! with what the world did with it, other refused updates of the player's
//! own are logged at most once per property every
//! [`REFUSAL_LOG_INTERVAL`], and an arrival after a death that leaves the
//! health at 0 for [`STILL_DEAD_AFTER`] says so once.

use std::collections::HashMap;
use std::time::Duration;

use ac_core::items::QualityKind;
use ac_core::player::{VitalKind, vital_id_to_kind};

use crate::world::{QualityNote, QualityOutcome};

/// How long after an arrival that followed a death the health may stay 0
/// before the log says so.
pub const STILL_DEAD_AFTER: Duration = Duration::from_secs(30);

/// The shortest time between two lines about refused updates of the same
/// property (vitals are always logged).
pub const REFUSAL_LOG_INTERVAL: Duration = Duration::from_secs(10);

/// The last health update of the player's own, as the warning names it.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
struct HealthUpdate {
    at: Duration,
    value: Option<u32>,
    sequence: u8,
    outcome: QualityOutcome,
}

/// One property's refusal lines.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
struct RefusalLog {
    /// When its last line was written.
    logged_at: Duration,
    /// Refusals since that were not written.
    held_back: u32,
}

/// An arrival after a death, waiting to see the health come back.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
struct DeathArrival {
    due: Duration,
    teleport_sequence: u16,
    destination_cell: u32,
}

/// The session's watch over the player's vital updates and its death
/// teleports.
#[derive(Debug, Default)]
pub(crate) struct VitalWatch {
    last_health: Option<HealthUpdate>,
    refusals: HashMap<(QualityKind, u32), RefusalLog>,
    /// The health was seen at 0 since the last arrival.
    died: bool,
    death_arrival: Option<DeathArrival>,
}

impl VitalWatch {
    /// The world noted `note` at `at`; `vital_now` is the current value the
    /// client holds now for the note's vital and `health_now` the health's
    /// (`None` before the full record). The line to log, if any.
    pub(crate) fn note(
        &mut self,
        note: &QualityNote,
        vital_now: Option<u32>,
        health_now: Option<u32>,
        at: Duration,
    ) -> Option<String> {
        let line = if note.kind == QualityKind::Vital {
            let kind = vital_id_to_kind(note.property);
            if kind == Some(VitalKind::Health) {
                self.last_health = Some(HealthUpdate {
                    at,
                    value: note.value,
                    sequence: note.sequence,
                    outcome: note.outcome,
                });
            }
            Some(vital_line(note, kind, vital_now))
        } else {
            self.refusal_line(note, at)
        };
        match health_now {
            Some(0) => self.died = true,
            // The health came back: an arrival waiting for it has nothing
            // to say.
            Some(_) => self.death_arrival = None,
            None => {}
        }
        line
    }

    /// A refusal of a property other than a vital, unless one of the same
    /// property was logged less than [`REFUSAL_LOG_INTERVAL`] ago.
    fn refusal_line(&mut self, note: &QualityNote, at: Duration) -> Option<String> {
        let why = refusal(note.outcome, note.sequence)?;
        let key = (note.kind, note.property);
        if let Some(log) = self.refusals.get_mut(&key)
            && at.saturating_sub(log.logged_at) < REFUSAL_LOG_INTERVAL
        {
            log.held_back += 1;
            return None;
        }
        let held_back = self
            .refusals
            .insert(
                key,
                RefusalLog {
                    logged_at: at,
                    held_back: 0,
                },
            )
            .map_or(0, |log| log.held_back);
        let more = if held_back == 0 {
            String::new()
        } else {
            format!(" ({held_back} more refused since the last line)")
        };
        Some(format!(
            "quality: own {:?} property {} seq {}: refused, {why}{more}",
            note.kind, note.property, note.sequence
        ))
    }

    /// A portal arrival completed at `at`. One after a death (the health
    /// was seen at 0 since the last arrival, or is 0 now) waits
    /// [`STILL_DEAD_AFTER`] for the health to come back.
    pub(crate) fn arrived(
        &mut self,
        at: Duration,
        teleport_sequence: u16,
        destination_cell: u32,
        health_now: Option<u32>,
    ) {
        let after_death = std::mem::take(&mut self.died) || health_now == Some(0);
        self.death_arrival = after_death.then_some(DeathArrival {
            due: at + STILL_DEAD_AFTER,
            teleport_sequence,
            destination_cell,
        });
    }

    /// Once a pass: the warning, once, when an arrival after a death is
    /// [`STILL_DEAD_AFTER`] old and the health is still 0.
    pub(crate) fn tick(&mut self, at: Duration, health_now: Option<u32>) -> Option<String> {
        let arrival = self.death_arrival?;
        if at < arrival.due {
            return None;
        }
        self.death_arrival = None;
        if health_now != Some(0) {
            return None;
        }
        let last = match self.last_health {
            None => "none".to_owned(),
            Some(update) => format!(
                "s={:.3}, {}, {}",
                update.at.as_secs_f64(),
                value_text(update.value),
                verdict(update.outcome, update.sequence)
            ),
        };
        Some(format!(
            "still dead {} s after the death teleport {} to 0x{:08X} (last health update: {last})",
            STILL_DEAD_AFTER.as_secs(),
            arrival.teleport_sequence,
            arrival.destination_cell,
        ))
    }
}

/// Why an update with `sequence` was refused; `None` when it was taken.
fn refusal(outcome: QualityOutcome, sequence: u8) -> Option<String> {
    match outcome {
        QualityOutcome::Taken => None,
        QualityOutcome::NoPlayer => Some("no player guid".to_owned()),
        QualityOutcome::NotInItems => Some("not in items".to_owned()),
        QualityOutcome::Older { held } => Some(format!("sequence {sequence} vs held {held}")),
    }
}

/// `taken`, or `refused, ` and why.
fn verdict(outcome: QualityOutcome, sequence: u8) -> String {
    refusal(outcome, sequence).map_or_else(|| "taken".to_owned(), |why| format!("refused, {why}"))
}

fn value_text(value: Option<u32>) -> String {
    value.map_or_else(|| "?".to_owned(), |value| value.to_string())
}

/// One vital update: the vital, its value, the sequence, what the world did
/// with it and the value the client holds now.
fn vital_line(note: &QualityNote, kind: Option<VitalKind>, now: Option<u32>) -> String {
    let name = match kind {
        Some(VitalKind::Health) => "health",
        Some(VitalKind::Stamina) => "stamina",
        Some(VitalKind::Mana) => "mana",
        None => "vital",
    };
    let now = now.map_or_else(|| "unknown".to_owned(), |value| value.to_string());
    format!(
        "vital: {name} {} (id {}) seq {}: {}; {name} now {now}",
        value_text(note.value),
        note.property,
        note.sequence,
        verdict(note.outcome, note.sequence)
    )
}

#[cfg(test)]
mod tests {
    //! The lines the watch writes, and when it stays quiet.

    use super::*;

    const HEALTH: u32 = 2;

    fn health(value: u32, sequence: u8, outcome: QualityOutcome) -> QualityNote {
        QualityNote {
            kind: QualityKind::Vital,
            property: HEALTH,
            sequence,
            value: Some(value),
            outcome,
        }
    }

    fn refused_int(property: u32, sequence: u8) -> QualityNote {
        QualityNote {
            kind: QualityKind::Int,
            property,
            sequence,
            value: None,
            outcome: QualityOutcome::Older { held: 40 },
        }
    }

    fn secs(s: u64) -> Duration {
        Duration::from_secs(s)
    }

    #[test]
    fn every_vital_update_is_a_line_with_what_was_done_with_it() {
        let mut watch = VitalWatch::default();
        assert_eq!(
            watch
                .note(
                    &health(147, 18, QualityOutcome::Taken),
                    Some(147),
                    Some(147),
                    secs(1)
                )
                .as_deref(),
            Some("vital: health 147 (id 2) seq 18: taken; health now 147")
        );
        assert_eq!(
            watch
                .note(
                    &health(147, 17, QualityOutcome::Older { held: 18 }),
                    Some(147),
                    Some(147),
                    secs(1)
                )
                .as_deref(),
            Some(
                "vital: health 147 (id 2) seq 17: refused, sequence 17 vs held 18; health now 147"
            )
        );
        assert_eq!(
            watch
                .note(
                    &health(147, 3, QualityOutcome::NotInItems),
                    Some(0),
                    Some(0),
                    secs(1)
                )
                .as_deref(),
            Some("vital: health 147 (id 2) seq 3: refused, not in items; health now 0")
        );
        let stamina = QualityNote {
            property: 4,
            ..health(90, 5, QualityOutcome::NoPlayer)
        };
        assert_eq!(
            watch.note(&stamina, None, None, secs(1)).as_deref(),
            Some("vital: stamina 90 (id 4) seq 5: refused, no player guid; stamina now unknown")
        );
    }

    #[test]
    fn a_refused_update_of_another_kind_is_logged_once_per_property_every_ten_seconds() {
        let mut watch = VitalWatch::default();
        let first = watch.note(&refused_int(25, 9), None, None, secs(100));
        assert_eq!(
            first.as_deref(),
            Some("quality: own Int property 25 seq 9: refused, sequence 9 vs held 40")
        );
        // The same property again within 10 s: held back; another
        // property has its own limit.
        assert_eq!(watch.note(&refused_int(25, 9), None, None, secs(105)), None);
        assert_eq!(watch.note(&refused_int(25, 9), None, None, secs(109)), None);
        assert!(
            watch
                .note(&refused_int(26, 9), None, None, secs(105))
                .is_some()
        );
        assert_eq!(
            watch
                .note(&refused_int(25, 9), None, None, secs(110))
                .as_deref(),
            Some(
                "quality: own Int property 25 seq 9: refused, sequence 9 vs held 40 \
                 (2 more refused since the last line)"
            )
        );
        // A taken update of another kind is no line at all.
        let taken = QualityNote {
            outcome: QualityOutcome::Taken,
            ..refused_int(25, 41)
        };
        assert_eq!(watch.note(&taken, None, None, secs(130)), None);
    }

    #[test]
    fn an_arrival_after_a_death_still_at_zero_health_warns_once() {
        let mut watch = VitalWatch::default();
        watch.note(
            &health(0, 10, QualityOutcome::Taken),
            Some(0),
            Some(0),
            secs(50),
        );
        watch.note(
            &health(147, 11, QualityOutcome::Older { held: 12 }),
            Some(0),
            Some(0),
            secs(53),
        );
        watch.arrived(secs(60), 3, 0x8164_0007, Some(0));
        assert_eq!(watch.tick(secs(89), Some(0)), None, "not yet 30 s");
        assert_eq!(
            watch.tick(secs(90), Some(0)).as_deref(),
            Some(
                "still dead 30 s after the death teleport 3 to 0x81640007 \
                 (last health update: s=53.000, 147, refused, sequence 11 vs held 12)"
            )
        );
        assert_eq!(watch.tick(secs(200), Some(0)), None, "once");
    }

    #[test]
    fn no_warning_when_the_health_came_back_or_there_was_no_death() {
        // Restored after the arrival.
        let mut watch = VitalWatch::default();
        watch.note(
            &health(0, 10, QualityOutcome::Taken),
            Some(0),
            Some(0),
            secs(50),
        );
        watch.arrived(secs(60), 3, 0x8164_0007, Some(0));
        watch.note(
            &health(147, 11, QualityOutcome::Taken),
            Some(147),
            Some(147),
            secs(63),
        );
        assert_eq!(watch.tick(secs(95), Some(147)), None);
        // Restored before the arrival (in portal space, as the server
        // sends it): the arrival still follows the death, and the health
        // is back when the time comes.
        let mut watch = VitalWatch::default();
        watch.note(
            &health(0, 10, QualityOutcome::Taken),
            Some(0),
            Some(0),
            secs(50),
        );
        watch.note(
            &health(147, 11, QualityOutcome::Taken),
            Some(147),
            Some(147),
            secs(53),
        );
        watch.arrived(secs(60), 3, 0x8164_0007, Some(147));
        assert_eq!(watch.tick(secs(95), Some(147)), None);
        // An arrival with no death before it waits for nothing.
        let mut watch = VitalWatch::default();
        watch.arrived(secs(60), 4, 0x7D64_0020, Some(147));
        assert_eq!(watch.tick(secs(95), Some(0)), None);
    }
}
