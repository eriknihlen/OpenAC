//! The position log (`OPENAC_POSITION_LOG=1`), to find where an object's
//! simulated body and the server's place for it part ways:
//!
//! - `update`: one line per server position for an object within
//!   [`NEAR_METRES`] of the character (any but the character itself and
//!   a projectile in flight, a creature or not), saying how it was handled
//!   and how far the body stood from the server's place before and after;
//! - `apart`: one line every [`APART_EVERY`] per object within
//!   [`NEAR_METRES`] whose body stands more than [`APART_METRES`] from the
//!   server's last place for it, with the time since its last update;
//! - `order`: one line per movement the server orders for the character,
//!   saying what it steers to.
//!
//! Off (the default), nothing is formatted or kept: every caller checks
//! [`PositionLog::is_on`] before it gathers anything.

use std::collections::HashMap;
use std::fmt;
use std::time::Duration;

use ac_physics::movement::RemotePositionOutcome;

use super::route::PositionDispositionRoute;

/// Objects farther than this from the character are not logged, metres.
pub const NEAR_METRES: f32 = 30.0;

/// A body farther than this from the server's place is reported as apart,
/// metres (a position update snaps a body on the ground that is farther).
pub const APART_METRES: f32 = 4.0;

/// How often a body that stays apart is reported again.
pub const APART_EVERY: Duration = Duration::from_secs(2);

/// How one server position for an object was handled.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Handling {
    /// Queued for the body to catch up to.
    Glide,
    /// The body was put at the server's place at once.
    Jump(&'static str),
    /// The body was placed by a collision sweep, or put there because it
    /// is too far from the character to be advanced.
    Placed(&'static str),
    /// Sent without the on-ground flag: the body was not moved. The body's
    /// cell before and after (they differ when the update wrote the
    /// server's cell onto the body).
    IgnoredNoGround {
        /// The body's cell before.
        cell_before: u32,
        /// The body's cell after.
        cell_after: u32,
    },
    /// Not applied: why.
    Rejected(&'static str),
    /// The object has no simulated body for the update to move: why.
    NoBody(&'static str),
}

impl fmt::Display for Handling {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Glide => f.write_str("glide"),
            Self::Jump(why) => write!(f, "jump ({why})"),
            Self::Placed(why) => write!(f, "placed ({why})"),
            Self::IgnoredNoGround {
                cell_before,
                cell_after,
            } if cell_before != cell_after => write!(
                f,
                "ignored, no ground flag, cell written 0x{cell_after:08X} over 0x{cell_before:08X}"
            ),
            Self::IgnoredNoGround { .. } => f.write_str("ignored, no ground flag, cell unchanged"),
            Self::Rejected(why) => write!(f, "rejected ({why})"),
            Self::NoBody(why) => write!(f, "no body ({why})"),
        }
    }
}

impl Handling {
    /// Whether the update moved the body (or queued it to move).
    #[must_use]
    pub fn moved_body(self) -> bool {
        matches!(self, Self::Glide | Self::Jump(_) | Self::Placed(_))
    }
}

/// How a server position that reached an object's body was handled, from
/// what the body made of it (`outcome`), the route it took, whether it
/// carried the on-ground flag, whether the body is near enough to the
/// character to be advanced, whether it was the body's first server
/// position, and the body's cell before and after.
#[must_use]
pub fn classify(
    outcome: RemotePositionOutcome,
    route: PositionDispositionRoute,
    advanced: bool,
    first_update: bool,
    cells: (u32, u32),
) -> Handling {
    match outcome {
        RemotePositionOutcome::Enqueued => Handling::Glide,
        RemotePositionOutcome::Snapped if first_update => Handling::Jump("first update"),
        RemotePositionOutcome::Snapped if !advanced => Handling::Placed("beyond 96 m"),
        RemotePositionOutcome::Snapped => Handling::Jump("more than 4 m"),
        RemotePositionOutcome::AirborneSnapped => Handling::Jump("body off the ground"),
        RemotePositionOutcome::PlacementRequired => {
            Handling::Placed(if route == PositionDispositionRoute::SetPosition {
                "teleport"
            } else {
                "far"
            })
        }
        RemotePositionOutcome::Nothing => match route {
            PositionDispositionRoute::RejectedAuthority => Handling::Rejected("not authoritative"),
            PositionDispositionRoute::RejectedData => Handling::Rejected("bad position"),
            PositionDispositionRoute::AwaitFreshPosition => Handling::Rejected("no position yet"),
            _ => Handling::IgnoredNoGround {
                cell_before: cells.0,
                cell_after: cells.1,
            },
        },
    }
}

/// A place as a cell and the offset in its landblock, metres.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Place {
    /// The full cell id.
    pub cell: u32,
    /// Landblock-local x, y, z.
    pub local: [f32; 3],
}

impl Place {
    /// The place a wire position names.
    #[must_use]
    pub fn of(p: &ac_proto::Position) -> Self {
        Self {
            cell: p.cell_id,
            local: [p.origin.x, p.origin.y, p.origin.z],
        }
    }
}

impl fmt::Display for Place {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        let [x, y, z] = self.local;
        write!(f, "0x{:08X} ({x:.2}, {y:.2}, {z:.2})", self.cell)
    }
}

fn place_or_none(place: Option<Place>) -> String {
    place.map_or_else(|| "none".to_owned(), |p| p.to_string())
}

fn metres_or_none(metres: Option<f32>) -> String {
    metres.map_or_else(|| "?".to_owned(), |m| format!("{m:.2}"))
}

/// One server position for an object, as logged.
#[derive(Debug, Clone, PartialEq)]
pub struct UpdateLine {
    /// The object.
    pub guid: u32,
    /// Its name.
    pub name: String,
    /// The update's instance, position, teleport and forced-position
    /// counters.
    pub sequences: [u16; 4],
    /// The update carried the on-ground flag.
    pub grounded: bool,
    /// How it was handled.
    pub handling: Handling,
    /// The server's place.
    pub server: Place,
    /// The body's place before the update (none without a body).
    pub body_before: Option<Place>,
    /// The body's place after.
    pub body_after: Option<Place>,
    /// Metres from the body to the server's place, before.
    pub gap_before: Option<f32>,
    /// Metres from the body to the server's place, after.
    pub gap_after: Option<f32>,
    /// Metres from the character to the server's place.
    pub from_player: f32,
}

impl fmt::Display for UpdateLine {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        let [instance, position, teleport, force] = self.sequences;
        write!(
            f,
            "update 0x{:08X} \"{}\" seq={instance}/{position}/{teleport}/{force} ground={} {} \
             server={} body={} -> {} gap={} -> {} m player={:.2} m",
            self.guid,
            self.name,
            if self.grounded { "yes" } else { "no" },
            self.handling,
            self.server,
            place_or_none(self.body_before),
            place_or_none(self.body_after),
            metres_or_none(self.gap_before),
            metres_or_none(self.gap_after),
            self.from_player,
        )
    }
}

/// An object's body that stays apart from the server's place, as logged.
#[derive(Debug, Clone, PartialEq)]
pub struct ApartLine {
    /// The object.
    pub guid: u32,
    /// Its name.
    pub name: String,
    /// Metres from the body to the server's place.
    pub gap: f32,
    /// The server's last place.
    pub server: Place,
    /// The body's place.
    pub body: Place,
    /// Seconds since its last server position (none logged yet).
    pub since_update: Option<f32>,
    /// Seconds since a server position last moved its body (none logged
    /// yet).
    pub since_moved: Option<f32>,
    /// Metres from the character to the body.
    pub from_player: f32,
}

impl fmt::Display for ApartLine {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(
            f,
            "apart 0x{:08X} \"{}\" gap={:.2} m server={} body={} since update={} s \
             since moved={} s player={:.2} m",
            self.guid,
            self.name,
            self.gap,
            self.server,
            self.body,
            metres_or_none(self.since_update),
            metres_or_none(self.since_moved),
            self.from_player,
        )
    }
}

/// What a server order for the character steers to.
#[derive(Debug, Clone, PartialEq)]
pub enum Steer {
    /// The target's simulated body: where it stands, and how far it stands
    /// from the server's last place for the target.
    Body {
        /// The body's place.
        place: Option<Place>,
        /// Metres from the body to the server's place.
        gap: Option<f32>,
    },
    /// A stand-in for a target nothing simulates: where it stands still.
    StandIn(Option<Place>),
    /// The place the order names, and why (a target with no body here, or
    /// an order that names only a place).
    OrderPlace {
        /// The order's place.
        place: Place,
        /// Why.
        why: &'static str,
    },
    /// The heading the order names for a target with no body here.
    OrderHeading {
        /// Degrees.
        heading: f32,
        /// Why.
        why: &'static str,
    },
    /// A heading only.
    Heading(f32),
}

impl fmt::Display for Steer {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Body { place, gap } => write!(
                f,
                "via=body steer={} target gap={} m",
                place_or_none(*place),
                metres_or_none(*gap)
            ),
            Self::StandIn(place) => write!(f, "via=stand-in steer={}", place_or_none(*place)),
            Self::OrderPlace { place, why } => {
                write!(f, "via=order place ({why}) steer={place}")
            }
            Self::OrderHeading { heading, why } => {
                write!(f, "via=order heading ({why}) heading={heading:.1}")
            }
            Self::Heading(heading) => write!(f, "heading={heading:.1}"),
        }
    }
}

/// A movement the server ordered for the character, as logged.
#[derive(Debug, Clone, PartialEq)]
pub struct OrderLine {
    /// The movement type's name.
    pub kind: &'static str,
    /// The target, 0 for none.
    pub target: u32,
    /// The target's name (empty for none).
    pub name: String,
    /// What it steers to.
    pub steer: Steer,
    /// The order's distance and speed fields, as `name=value` pairs.
    pub fields: Vec<(&'static str, f32)>,
}

impl fmt::Display for OrderLine {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "order {}", self.kind)?;
        if self.target != 0 {
            write!(f, " target=0x{:08X} \"{}\"", self.target, self.name)?;
        }
        write!(f, " {}", self.steer)?;
        for (name, value) in &self.fields {
            write!(f, " {name}={value:.2}")?;
        }
        Ok(())
    }
}

/// What the log remembers of one object.
#[derive(Debug, Clone, Copy, Default)]
struct Seen {
    last_update: Option<Duration>,
    last_moved: Option<Duration>,
    last_apart: Option<Duration>,
}

/// The position log: off by default; on, it keeps the lines until the
/// session prints them.
#[derive(Debug, Default)]
pub struct PositionLog {
    on: bool,
    seen: HashMap<u32, Seen>,
    lines: Vec<String>,
}

/// The line's head: the wall clock (as the motion log's) and the
/// session's clock, seconds.
fn stamp(now: Duration) -> String {
    format!(
        "[position] {} s={:.3}",
        crate::world::motion_log_stamp(),
        now.as_secs_f64()
    )
}

impl PositionLog {
    /// Turn the log on.
    pub fn enable(&mut self) {
        self.on = true;
    }

    /// Whether the log is on.
    #[must_use]
    pub fn is_on(&self) -> bool {
        self.on
    }

    /// The lines kept since the last call, oldest first.
    pub fn take_lines(&mut self) -> Vec<String> {
        std::mem::take(&mut self.lines)
    }

    /// Log an object's server position.
    pub fn update(&mut self, now: Duration, line: &UpdateLine) {
        if !self.on {
            return;
        }
        let seen = self.seen.entry(line.guid).or_default();
        seen.last_update = Some(now);
        if line.handling.moved_body() {
            seen.last_moved = Some(now);
        }
        self.lines.push(format!("{} {line}", stamp(now)));
    }

    /// Whether a body found apart at `now` is due a line (none for this
    /// object yet, or the last one [`APART_EVERY`] ago).
    #[must_use]
    pub fn apart_due(&self, guid: u32, now: Duration) -> bool {
        self.on
            && self
                .seen
                .get(&guid)
                .and_then(|s| s.last_apart)
                .is_none_or(|at| now.saturating_sub(at) >= APART_EVERY)
    }

    /// Log a body apart from the server's place: the times since its last
    /// update and last move are filled in here.
    pub fn apart(&mut self, now: Duration, mut line: ApartLine) {
        if !self.on {
            return;
        }
        let seen = self.seen.entry(line.guid).or_default();
        seen.last_apart = Some(now);
        let since = |at: Option<Duration>| at.map(|t| now.saturating_sub(t).as_secs_f32());
        line.since_update = since(seen.last_update);
        line.since_moved = since(seen.last_moved);
        self.lines.push(format!("{} {line}", stamp(now)));
    }

    /// Log a server order for the character.
    pub fn order(&mut self, now: Duration, line: &OrderLine) {
        if !self.on {
            return;
        }
        self.lines.push(format!("{} {line}", stamp(now)));
    }

    /// Forget an object that left.
    pub fn forget(&mut self, guid: u32) {
        self.seen.remove(&guid);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const R: PositionDispositionRoute = PositionDispositionRoute::Interpolate;

    #[test]
    fn each_outcome_is_named() {
        let c = |o, route, advanced, first| classify(o, route, advanced, first, (1, 1));
        assert_eq!(
            c(RemotePositionOutcome::Enqueued, R, true, false),
            Handling::Glide
        );
        assert_eq!(
            c(RemotePositionOutcome::Snapped, R, true, false),
            Handling::Jump("more than 4 m")
        );
        assert_eq!(
            c(RemotePositionOutcome::Snapped, R, true, true),
            Handling::Jump("first update")
        );
        assert_eq!(
            c(RemotePositionOutcome::Snapped, R, false, false),
            Handling::Placed("beyond 96 m")
        );
        assert_eq!(
            c(RemotePositionOutcome::AirborneSnapped, R, true, false),
            Handling::Jump("body off the ground")
        );
        assert_eq!(
            c(
                RemotePositionOutcome::PlacementRequired,
                PositionDispositionRoute::SetPosition,
                true,
                false
            ),
            Handling::Placed("teleport")
        );
        assert_eq!(
            c(
                RemotePositionOutcome::PlacementRequired,
                PositionDispositionRoute::SetPositionSimple,
                true,
                false
            ),
            Handling::Placed("far")
        );
        assert_eq!(
            c(
                RemotePositionOutcome::Nothing,
                PositionDispositionRoute::RejectedAuthority,
                true,
                false
            ),
            Handling::Rejected("not authoritative")
        );
        assert_eq!(
            c(RemotePositionOutcome::Nothing, R, true, false),
            Handling::IgnoredNoGround {
                cell_before: 1,
                cell_after: 1
            }
        );
    }

    #[test]
    fn an_ignored_update_says_whether_it_wrote_the_cell() {
        let written = Handling::IgnoredNoGround {
            cell_before: 0x0032_01DE,
            cell_after: 0x0032_01D9,
        };
        assert_eq!(
            written.to_string(),
            "ignored, no ground flag, cell written 0x003201D9 over 0x003201DE"
        );
        let kept = Handling::IgnoredNoGround {
            cell_before: 0x0032_01DE,
            cell_after: 0x0032_01DE,
        };
        assert_eq!(kept.to_string(), "ignored, no ground flag, cell unchanged");
        assert!(!written.moved_body());
    }

    #[test]
    fn nothing_is_kept_while_off() {
        let mut log = PositionLog::default();
        log.order(
            Duration::ZERO,
            &OrderLine {
                kind: "MoveToPosition",
                target: 0,
                name: String::new(),
                steer: Steer::Heading(0.0),
                fields: Vec::new(),
            },
        );
        assert!(!log.apart_due(1, Duration::ZERO));
        assert!(log.take_lines().is_empty());
    }

    #[test]
    fn an_apart_body_is_reported_every_two_seconds_with_its_update_age() {
        let mut log = PositionLog::default();
        log.enable();
        let place = Place {
            cell: 0x0032_01D9,
            local: [87.35, -10.2, 18.01],
        };
        log.update(
            Duration::from_secs(10),
            &UpdateLine {
                guid: 7,
                name: "Bull".into(),
                sequences: [1, 2, 3, 0],
                grounded: true,
                handling: Handling::Glide,
                server: place,
                body_before: Some(place),
                body_after: Some(place),
                gap_before: Some(0.0),
                gap_after: Some(0.0),
                from_player: 2.0,
            },
        );
        let apart = ApartLine {
            guid: 7,
            name: "Bull".into(),
            gap: 9.5,
            server: place,
            body: place,
            since_update: None,
            since_moved: None,
            from_player: 3.0,
        };
        let at = Duration::from_millis(13_500);
        assert!(log.apart_due(7, at));
        log.apart(at, apart.clone());
        assert!(!log.apart_due(7, at + Duration::from_secs(1)));
        assert!(log.apart_due(7, at + APART_EVERY));
        let lines = log.take_lines();
        assert_eq!(lines.len(), 2);
        assert!(lines[0].contains("update 0x00000007 \"Bull\" seq=1/2/3/0 ground=yes glide"));
        assert!(
            lines[1].ends_with(
                "apart 0x00000007 \"Bull\" gap=9.50 m server=0x003201D9 (87.35, -10.20, 18.01) \
                 body=0x003201D9 (87.35, -10.20, 18.01) since update=3.50 s since moved=3.50 s \
                 player=3.00 m"
            ),
            "{}",
            lines[1]
        );
    }
}
