//! What the client log needs to explain a character left at 0 health after
//! a death.
//!
//! On a death the server sets the character's health to 0, sends it to its
//! lifestone, and a few seconds into that teleport sends health, stamina
//! and mana back at 75% (ACE's `Player_Death.cs`). Login complete at the
//! arrival ends the server's portal space; until then it moves the
//! character nowhere (ACE's `Player_Location.cs`). A character still at 0
//! health long after dying either had its restore refused here, never
//! arrived here (so never sent login complete), or never had its portal
//! space ended there.
//!
//! So, cheaply enough for a bot that farms for days: every refused vital
//! update of the player's own is logged, other refused updates of its own
//! at most once per property every [`REFUSAL_LOG_INTERVAL`], and taken
//! vital updates only while a death is being watched (from the health
//! seen at 0 until it comes back, or until a warning below was written).
//! An arrival after the death that leaves the health at 0 for
//! [`STILL_DEAD_AFTER_ARRIVAL`] says so once; a death with no arrival at
//! all for [`STILL_DEAD_WITHOUT_ARRIVAL`] says so once too.

use std::collections::HashMap;
use std::time::Duration;

use ac_core::items::QualityKind;
use ac_core::player::{VitalKind, vital_id_to_kind};

use crate::world::{QualityNote, QualityOutcome};

/// How long after an arrival that followed a death the health may stay 0
/// before the log says so.
pub const STILL_DEAD_AFTER_ARRIVAL: Duration = Duration::from_secs(30);

/// How long the health may stay 0 with no arrival since the death before
/// the log says so.
pub const STILL_DEAD_WITHOUT_ARRIVAL: Duration = Duration::from_secs(60);

/// The shortest time between two lines about refused updates of the same
/// property other than a vital (a refused vital is always logged).
pub const REFUSAL_LOG_INTERVAL: Duration = Duration::from_secs(10);

/// The last health update of the player's own, as a warning names it.
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

/// The arrival warning waiting for its time: the first arrival after the
/// death sets when, the latest names the teleport.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
struct PendingArrival {
    due: Duration,
    teleport_sequence: u16,
    destination_cell: u32,
}

/// A death being watched: from the health seen at 0 until it comes back.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
struct Death {
    /// When the health was seen at 0.
    since: Duration,
    /// An arrival came since.
    arrived: bool,
    pending: Option<PendingArrival>,
    /// The arrival warning was written.
    arrival_warned: bool,
    /// The no-arrival warning was written.
    stray_warned: bool,
}

impl Death {
    fn new(since: Duration) -> Self {
        Self {
            since,
            arrived: false,
            pending: None,
            arrival_warned: false,
            stray_warned: false,
        }
    }
}

/// The session's watch over the player's vital updates and its deaths.
#[derive(Debug, Default)]
pub(crate) struct VitalWatch {
    last_health: Option<HealthUpdate>,
    refusals: HashMap<(QualityKind, u32), RefusalLog>,
    death: Option<Death>,
}

impl VitalWatch {
    /// Whether taken vital updates are logged now: a death is watched and
    /// no warning about it was written yet.
    fn logs_taken(&self) -> bool {
        self.death
            .is_some_and(|death| !death.arrival_warned && !death.stray_warned)
    }

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
        // The update that opens the watch and the one that closes it are
        // both logged.
        let logged_before = self.logs_taken();
        let kind = (note.kind == QualityKind::Vital)
            .then(|| vital_id_to_kind(note.property))
            .flatten();
        if kind == Some(VitalKind::Health) {
            self.last_health = Some(HealthUpdate {
                at,
                value: note.value,
                sequence: note.sequence,
                outcome: note.outcome,
            });
        }
        match health_now {
            Some(0) => {
                self.death.get_or_insert(Death::new(at));
            }
            // The health came back: the death and its warnings are over.
            Some(_) => self.death = None,
            None => {}
        }
        if note.kind != QualityKind::Vital {
            return self.refusal_line(note, at);
        }
        let logged = note.outcome != QualityOutcome::Taken || logged_before || self.logs_taken();
        logged.then(|| vital_line(note, kind, vital_now))
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

    /// A portal arrival completed at `at`. During a watched death (or with
    /// the health 0 now) the first arrival starts the
    /// [`STILL_DEAD_AFTER_ARRIVAL`] wait; a later one keeps that time and
    /// only changes which teleport the warning names.
    pub(crate) fn arrived(
        &mut self,
        at: Duration,
        teleport_sequence: u16,
        destination_cell: u32,
        health_now: Option<u32>,
    ) {
        if health_now == Some(0) {
            self.death.get_or_insert(Death::new(at));
        }
        let Some(death) = self.death.as_mut() else {
            return;
        };
        death.arrived = true;
        if death.arrival_warned {
            return;
        }
        let due = death
            .pending
            .map_or(at + STILL_DEAD_AFTER_ARRIVAL, |pending| pending.due);
        death.pending = Some(PendingArrival {
            due,
            teleport_sequence,
            destination_cell,
        });
    }

    /// Once a pass: a warning, once per death, when the health is still 0
    /// [`STILL_DEAD_AFTER_ARRIVAL`] after an arrival, or
    /// [`STILL_DEAD_WITHOUT_ARRIVAL`] after dying with no arrival since.
    pub(crate) fn tick(&mut self, at: Duration, health_now: Option<u32>) -> Option<String> {
        if health_now != Some(0) {
            return None;
        }
        let death = self.death.as_mut()?;
        if let Some(pending) = death.pending
            && at >= pending.due
        {
            death.pending = None;
            death.arrival_warned = true;
            return Some(format!(
                "still dead {} s after the death teleport {} to 0x{:08X} \
                 (last health update: {})",
                STILL_DEAD_AFTER_ARRIVAL.as_secs(),
                pending.teleport_sequence,
                pending.destination_cell,
                self.last_health_text(),
            ));
        }
        if !death.arrived && !death.stray_warned && at >= death.since + STILL_DEAD_WITHOUT_ARRIVAL {
            death.stray_warned = true;
            return Some(format!(
                "still dead {} s after dying with no teleport arrival (last health update: {})",
                STILL_DEAD_WITHOUT_ARRIVAL.as_secs(),
                self.last_health_text(),
            ));
        }
        None
    }

    fn last_health_text(&self) -> String {
        match self.last_health {
            None => "none".to_owned(),
            Some(update) => format!(
                "s={:.3}, {}, {}",
                update.at.as_secs_f64(),
                value_text(update.value),
                verdict(update.outcome, update.sequence)
            ),
        }
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

    fn taken(value: u32, sequence: u8) -> QualityNote {
        health(value, sequence, QualityOutcome::Taken)
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

    /// The health is `value` after the note (the update was taken).
    fn set(watch: &mut VitalWatch, value: u32, sequence: u8, at: u64) -> Option<String> {
        watch.note(&taken(value, sequence), Some(value), Some(value), secs(at))
    }

    #[test]
    fn a_refused_vital_update_is_always_a_line_with_why() {
        let mut watch = VitalWatch::default();
        let older = health(140, 17, QualityOutcome::Older { held: 18 });
        assert_eq!(
            watch.note(&older, Some(147), Some(147), secs(1)).as_deref(),
            Some(
                "vital: health 140 (id 2) seq 17: refused, sequence 17 vs held 18; health now 147"
            )
        );
        let missing = health(147, 3, QualityOutcome::NotInItems);
        assert_eq!(
            watch.note(&missing, None, None, secs(1)).as_deref(),
            Some("vital: health 147 (id 2) seq 3: refused, not in items; health now unknown")
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
    fn a_taken_vital_update_is_a_line_only_while_a_death_is_watched() {
        let mut watch = VitalWatch::default();
        // Farming: regeneration and hits write nothing.
        assert_eq!(set(&mut watch, 147, 18, 1), None);
        assert_eq!(set(&mut watch, 120, 19, 2), None);
        // The death opens the watch, with its own line.
        assert_eq!(
            set(&mut watch, 0, 20, 3).as_deref(),
            Some("vital: health 0 (id 2) seq 20: taken; health now 0")
        );
        let mana = QualityNote {
            property: 6,
            ..taken(0, 4)
        };
        assert_eq!(
            watch.note(&mana, Some(0), Some(0), secs(4)).as_deref(),
            Some("vital: mana 0 (id 6) seq 4: taken; mana now 0")
        );
        // The restore closes it, with its own line; after it, nothing.
        assert_eq!(
            set(&mut watch, 110, 21, 7).as_deref(),
            Some("vital: health 110 (id 2) seq 21: taken; health now 110")
        );
        assert_eq!(set(&mut watch, 115, 22, 8), None);
    }

    #[test]
    fn taken_vital_updates_stop_being_logged_once_a_warning_was_written() {
        let mut watch = VitalWatch::default();
        set(&mut watch, 0, 10, 50);
        watch.arrived(secs(60), 3, 0x8164_0007, Some(0));
        assert!(watch.tick(secs(90), Some(0)).is_some());
        assert_eq!(set(&mut watch, 0, 11, 95), None);
        // Refusals still are.
        let older = health(0, 9, QualityOutcome::Older { held: 11 });
        assert!(watch.note(&older, Some(0), Some(0), secs(96)).is_some());
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
        set(&mut watch, 0, 10, 50);
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
        watch.arrived(secs(210), 4, 0x8164_0007, Some(0));
        assert_eq!(watch.tick(secs(300), Some(0)), None, "once per death");
    }

    #[test]
    fn later_arrivals_keep_the_first_arrivals_deadline() {
        // A bot recalling every 15 s while dead still hears the warning,
        // 30 s after its first arrival, naming the latest teleport.
        let mut watch = VitalWatch::default();
        set(&mut watch, 0, 10, 50);
        watch.arrived(secs(60), 3, 0x8164_0007, Some(0));
        watch.arrived(secs(75), 4, 0x7D64_0020, Some(0));
        watch.arrived(secs(89), 5, 0x8164_0007, Some(0));
        assert_eq!(
            watch.tick(secs(90), Some(0)).as_deref(),
            Some(
                "still dead 30 s after the death teleport 5 to 0x81640007 \
                 (last health update: s=50.000, 0, taken)"
            )
        );
    }

    #[test]
    fn a_death_with_no_arrival_warns_once_after_sixty_seconds() {
        let mut watch = VitalWatch::default();
        set(&mut watch, 0, 10, 50);
        assert_eq!(watch.tick(secs(109), Some(0)), None, "not yet 60 s");
        assert_eq!(
            watch.tick(secs(110), Some(0)).as_deref(),
            Some(
                "still dead 60 s after dying with no teleport arrival \
                 (last health update: s=50.000, 0, taken)"
            )
        );
        assert_eq!(watch.tick(secs(200), Some(0)), None, "once");
        // An arrival has its own warning instead.
        let mut watch = VitalWatch::default();
        set(&mut watch, 0, 10, 50);
        watch.arrived(secs(55), 3, 0x8164_0007, Some(0));
        assert!(watch.tick(secs(85), Some(0)).is_some(), "the arrival's");
        assert_eq!(watch.tick(secs(110), Some(0)), None, "not this one");
    }

    #[test]
    fn no_warning_when_the_health_came_back_or_there_was_no_death() {
        // Restored after the arrival.
        let mut watch = VitalWatch::default();
        set(&mut watch, 0, 10, 50);
        watch.arrived(secs(60), 3, 0x8164_0007, Some(0));
        set(&mut watch, 147, 11, 63);
        assert_eq!(watch.tick(secs(95), Some(147)), None);
        // Restored before the arrival (in portal space, as the server
        // sends it): the arrival waits for nothing.
        let mut watch = VitalWatch::default();
        set(&mut watch, 0, 10, 50);
        set(&mut watch, 147, 11, 53);
        watch.arrived(secs(60), 3, 0x8164_0007, Some(147));
        assert_eq!(watch.tick(secs(95), Some(147)), None);
        assert_eq!(watch.tick(secs(200), Some(147)), None);
        // An arrival with no death before it waits for nothing.
        let mut watch = VitalWatch::default();
        watch.arrived(secs(60), 4, 0x7D64_0020, Some(147));
        assert_eq!(watch.tick(secs(95), Some(0)), None);
    }

    #[test]
    fn the_health_coming_back_ends_the_death_and_its_pending_warning() {
        // Restored, then dead again: the first death's arrival warning is
        // gone, and the new death waits its own 60 s from its own start.
        let mut watch = VitalWatch::default();
        set(&mut watch, 0, 10, 50);
        watch.arrived(secs(60), 3, 0x8164_0007, Some(0));
        set(&mut watch, 147, 11, 63);
        set(&mut watch, 0, 12, 70);
        assert_eq!(watch.tick(secs(90), Some(0)), None);
        assert_eq!(watch.tick(secs(129), Some(0)), None);
        assert_eq!(
            watch.tick(secs(130), Some(0)).as_deref(),
            Some(
                "still dead 60 s after dying with no teleport arrival \
                 (last health update: s=70.000, 0, taken)"
            )
        );
    }
}
