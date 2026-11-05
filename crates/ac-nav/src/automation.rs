//! Who drives the walk: one walk runs at a time, and it belongs to whoever
//! asked for it, a plugin by id or the player through a chat command.
//! Another plugin asking while that walk is under way is refused rather than
//! quietly taking the character; the player's own commands always win. A
//! plugin that goes away takes its walk and its pauses with it.
//!
//! Ownership is keyed on the walk's request sequence: the owner recorded
//! here holds the walk only while the walk's current request is the one it
//! started. A request that reached the walk any other way (a route preview
//! from chat, say) is the player's.
//!
//! Also here: how the runtime's positions read as map coordinates and back,
//! and which requests a walk accepts.

use glam::Vec3;

use crate::walk::WalkController;

impl OwnedWalk for WalkController {
    fn is_busy(&self) -> bool {
        WalkController::is_busy(self)
    }

    fn report_sequence(&self) -> i64 {
        self.report().sequence
    }

    fn stop(&mut self) {
        WalkController::stop(self);
    }
}

/// The owner of walks and pauses asked for through chat, which outrank
/// every plugin's.
pub const PLAYER_OWNER: &str = "player";

/// The farthest from its goal a walk may be asked to end, metres.
pub const MAXIMUM_GO_TO_ARRIVAL_METERS: f32 = 50.0;

/// How a navigation request was answered.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum NavCommandStatus {
    /// The request was taken; watch its report.
    Accepted,
    /// The arguments are wrong.
    Rejected,
    /// There is no character in the world, or no navigation.
    Unavailable,
    /// Another owner's walk is under way.
    Held,
}

/// What ownership needs of the walk it arbitrates.
pub trait OwnedWalk {
    /// Whether a request is waiting, being planned or being walked.
    fn is_busy(&self) -> bool;
    /// The sequence of the walk's latest request.
    fn report_sequence(&self) -> i64;
    /// Ends the request under way.
    fn stop(&mut self);
}

/// A pause a plugin or the player registered: while its need names a
/// reason, walks wait.
pub type PauseNeed = Box<dyn Fn() -> Option<String> + Send>;

/// The handle of a registered pause, which removes it.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct PauseId(u64);

struct Pause {
    id: PauseId,
    owner: String,
    need: PauseNeed,
}

/// Which owner holds the walk, and the pauses walks wait on. See the module
/// documentation.
#[derive(Default)]
pub struct WalkOwnership {
    walk_owner: Option<String>,
    walk_sequence: i64,
    report_revision: i64,
    last_reported_sequence: i64,
    /// The state last reported; 0 (no walk) before any report, as
    /// OpenAC starts from its `None` state.
    last_reported_state: u32,
    pauses: Vec<Pause>,
    next_pause: u64,
}

impl std::fmt::Debug for WalkOwnership {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("WalkOwnership")
            .field("walk_owner", &self.walk_owner)
            .field("walk_sequence", &self.walk_sequence)
            .field("report_revision", &self.report_revision)
            .field("pauses", &self.pauses.len())
            .finish()
    }
}

impl WalkOwnership {
    /// Nobody owns a walk; no pauses.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// The owner of the walk's current request: the recorded owner while the
    /// request is the one it started, the player for any other request,
    /// `None` while nothing runs.
    #[must_use]
    pub fn owner_of(&self, walk: &impl OwnedWalk) -> Option<&str> {
        if !walk.is_busy() {
            return None;
        }
        if walk.report_sequence() == self.walk_sequence {
            self.walk_owner.as_deref()
        } else {
            Some(PLAYER_OWNER)
        }
    }

    /// A walk request from an owner, refused while another owner's walk is
    /// under way unless the request is the player's. `validity` is the
    /// request's own check; `start` hands the request to the walk and returns
    /// its sequence. The caller answers [`NavCommandStatus::Unavailable`]
    /// itself while there is no walk or no character.
    pub fn begin_walk<W: OwnedWalk>(
        &mut self,
        owner: &str,
        walk: &mut W,
        validity: NavCommandStatus,
        start: impl FnOnce(&mut W) -> i64,
    ) -> NavCommandStatus {
        if validity != NavCommandStatus::Accepted {
            return validity;
        }
        if let Some(current) = self.owner_of(walk)
            && owner != PLAYER_OWNER
            && current != owner
        {
            return NavCommandStatus::Held;
        }
        self.walk_sequence = start(walk);
        self.walk_owner = Some(owner.to_owned());
        NavCommandStatus::Accepted
    }

    /// Stops the walk under way: a plugin stops only the walk it started;
    /// the player stops any.
    pub fn stop_walk(&mut self, owner: &str, walk: &mut impl OwnedWalk) -> NavCommandStatus {
        let Some(current) = self.owner_of(walk) else {
            return NavCommandStatus::Rejected;
        };
        if owner != PLAYER_OWNER && current != owner {
            return NavCommandStatus::Held;
        }
        walk.stop();
        self.walk_owner = None;
        NavCommandStatus::Accepted
    }

    /// Forgets an owner that went away: its pauses are dropped and the walk
    /// it owns is stopped.
    pub fn release(&mut self, owner: &str, walk: Option<&mut impl OwnedWalk>) {
        self.pauses.retain(|pause| pause.owner != owner);
        if let Some(walk) = walk
            && self.owner_of(walk) == Some(owner)
        {
            walk.stop();
            self.walk_owner = None;
        }
    }

    /// The owner and revision a walk report carries: who owns the walk under
    /// way (none when none is), and a revision that grows whenever the
    /// reported sequence or state (any value the caller distinguishes
    /// states by, 0 being no walk) changes.
    pub fn report(&mut self, walk: &impl OwnedWalk, state: u32) -> (Option<String>, i64) {
        let sequence = walk.report_sequence();
        if sequence != self.last_reported_sequence || state != self.last_reported_state {
            self.last_reported_sequence = sequence;
            self.last_reported_state = state;
            self.report_revision += 1;
        }
        let owner = if walk.is_busy() {
            Some(if sequence == self.walk_sequence {
                self.walk_owner.clone().unwrap_or_default()
            } else {
                PLAYER_OWNER.to_owned()
            })
        } else {
            None
        };
        (owner, self.report_revision)
    }

    /// The walk was replaced by a fresh one (the character left the
    /// world): who owned it and the reports are forgotten, the pauses
    /// stay (their owners still need the character when it returns).
    pub fn forget_walk(&mut self) {
        self.walk_owner = None;
        self.walk_sequence = 0;
        self.last_reported_sequence = 0;
        self.last_reported_state = 0;
    }

    /// Registers a pause for an owner.
    pub fn add_pause(&mut self, owner: &str, need: PauseNeed) -> PauseId {
        self.next_pause += 1;
        let id = PauseId(self.next_pause);
        self.pauses.push(Pause {
            id,
            owner: owner.to_owned(),
            need,
        });
        id
    }

    /// Removes a pause; false when it was already gone.
    pub fn remove_pause(&mut self, id: PauseId) -> bool {
        let before = self.pauses.len();
        self.pauses.retain(|pause| pause.id != id);
        self.pauses.len() != before
    }

    /// What a registered pause needs the character for now, the first that
    /// names something, trimmed; `None` when none does. The runtime adds its
    /// own reasons after these (an attack under way, a held movement
    /// intent).
    #[must_use]
    pub fn pause_reason(&self) -> Option<String> {
        self.pauses.iter().find_map(|pause| {
            (pause.need)()
                .map(|need| need.trim().to_owned())
                .filter(|need| !need.is_empty())
        })
    }
}

/// Whether a walk to an object (or a follow) may be asked for: an object
/// id, and an arrival distance above zero and at most
/// [`MAXIMUM_GO_TO_ARRIVAL_METERS`].
#[must_use]
pub fn validate_arrival(object_id: u32, arrival_meters: f32) -> NavCommandStatus {
    if object_id == 0 || !(arrival_meters > 0.0) || arrival_meters > MAXIMUM_GO_TO_ARRIVAL_METERS {
        NavCommandStatus::Rejected
    } else {
        NavCommandStatus::Accepted
    }
}

/// Whether a walk to a place may be asked for: finite map coordinates, an
/// elevation that is not infinite (NaN stands the place on the ground), and
/// an arrival distance as [`validate_arrival`] takes it.
#[must_use]
pub fn validate_place(position: &MapPosition, arrival_meters: f32) -> NavCommandStatus {
    if !position.east_west.is_finite()
        || !position.north_south.is_finite()
        || position.elevation.is_infinite()
        || !(arrival_meters > 0.0)
        || arrival_meters > MAXIMUM_GO_TO_ARRIVAL_METERS
    {
        NavCommandStatus::Rejected
    } else {
        NavCommandStatus::Accepted
    }
}

/// A position as map coordinates: 240 m to a unit, from the middle of the
/// map; east and north positive.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct MapPosition {
    /// The cell, or 0 for none.
    pub cell_id: u32,
    /// East (positive) or west.
    pub east_west: f64,
    /// North (positive) or south.
    pub north_south: f64,
    /// Height, in the same units.
    pub elevation: f64,
}

impl MapPosition {
    /// A cell and a point in its landblock's own frame, as map coordinates.
    #[must_use]
    pub fn of(cell_id: u32, local: Vec3) -> Self {
        let block_x = f64::from((cell_id >> 24) & 0xFF);
        let block_y = f64::from((cell_id >> 16) & 0xFF);
        Self {
            cell_id,
            east_west: ((block_x - 127.0) * 192.0 + f64::from(local.x) - 84.0) / 240.0,
            north_south: ((block_y - 127.0) * 192.0 + f64::from(local.y) - 84.0) / 240.0,
            elevation: f64::from(local.z) / 240.0,
        }
    }

    /// The point in its landblock's own frame, the inverse of [`Self::of`];
    /// for a position with no cell, measured from the corner of the first
    /// landblock.
    #[must_use]
    pub fn landblock_local(&self) -> Vec3 {
        let block_x = f64::from((self.cell_id >> 24) & 0xFF);
        let block_y = f64::from((self.cell_id >> 16) & 0xFF);
        Vec3::new(
            (self.east_west * 240.0 + 84.0 - (block_x - 127.0) * 192.0) as f32,
            (self.north_south * 240.0 + 84.0 - (block_y - 127.0) * 192.0) as f32,
            (self.elevation * 240.0) as f32,
        )
    }

    /// Whether the cell is an outdoor one.
    #[must_use]
    pub fn is_outdoors(&self) -> bool {
        (1..=0x40).contains(&(self.cell_id & 0xFFFF))
    }
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC
    //! `tests/AcDream.Runtime.Tests/Navigation/RuntimeNavigationOwnershipTests.cs`
    //! (the arbitration; the plugin-host plumbing around it is the runtime's).

    use super::*;

    /// A walk that takes requests and reports the latest.
    #[derive(Default)]
    struct FakeWalk {
        sequence: i64,
        busy: bool,
        stops: i32,
    }

    impl OwnedWalk for FakeWalk {
        fn is_busy(&self) -> bool {
            self.busy
        }
        fn report_sequence(&self) -> i64 {
            self.sequence
        }
        fn stop(&mut self) {
            self.busy = false;
            self.stops += 1;
        }
    }

    impl FakeWalk {
        fn request(&mut self) -> i64 {
            self.sequence += 1;
            self.busy = true;
            self.sequence
        }
    }

    fn go(ownership: &mut WalkOwnership, owner: &str, walk: &mut FakeWalk) -> NavCommandStatus {
        ownership.begin_walk(owner, walk, NavCommandStatus::Accepted, FakeWalk::request)
    }

    #[test]
    fn a_plugin_cannot_take_a_walk_another_plugin_started() {
        let mut ownership = WalkOwnership::new();
        let mut walk = FakeWalk::default();

        assert_eq!(
            go(&mut ownership, "first", &mut walk),
            NavCommandStatus::Accepted
        );
        assert_eq!(
            go(&mut ownership, "second", &mut walk),
            NavCommandStatus::Held
        );
        assert_eq!(ownership.owner_of(&walk), Some("first"));
        assert_eq!(
            go(&mut ownership, "first", &mut walk),
            NavCommandStatus::Accepted
        );
    }

    #[test]
    fn the_player_takes_any_walk() {
        let mut ownership = WalkOwnership::new();
        let mut walk = FakeWalk::default();
        go(&mut ownership, "plugin", &mut walk);

        assert_eq!(
            go(&mut ownership, PLAYER_OWNER, &mut walk),
            NavCommandStatus::Accepted
        );
        assert_eq!(ownership.owner_of(&walk), Some(PLAYER_OWNER));
        assert_eq!(
            go(&mut ownership, "plugin", &mut walk),
            NavCommandStatus::Held
        );
    }

    #[test]
    fn a_plugin_stops_only_its_own_walk_and_the_player_stops_any() {
        let mut ownership = WalkOwnership::new();
        let mut walk = FakeWalk::default();
        go(&mut ownership, "first", &mut walk);

        assert_eq!(
            ownership.stop_walk("second", &mut walk),
            NavCommandStatus::Held
        );
        assert_eq!(walk.stops, 0);
        assert_eq!(
            ownership.stop_walk(PLAYER_OWNER, &mut walk),
            NavCommandStatus::Accepted
        );
        assert_eq!(walk.stops, 1);
        assert_eq!(
            ownership.stop_walk(PLAYER_OWNER, &mut walk),
            NavCommandStatus::Rejected
        );
    }

    #[test]
    fn a_request_that_reached_the_walk_another_way_is_the_players() {
        let mut ownership = WalkOwnership::new();
        let mut walk = FakeWalk::default();
        go(&mut ownership, "plugin", &mut walk);

        walk.request();

        assert_eq!(ownership.owner_of(&walk), Some(PLAYER_OWNER));
        assert_eq!(
            go(&mut ownership, "plugin", &mut walk),
            NavCommandStatus::Held
        );
    }

    #[test]
    fn a_plugin_that_goes_away_takes_its_walk_and_pauses_with_it() {
        let mut ownership = WalkOwnership::new();
        let mut walk = FakeWalk::default();
        go(&mut ownership, "plugin", &mut walk);
        ownership.add_pause("plugin", Box::new(|| Some("fighting a monster".to_owned())));
        ownership.add_pause("other", Box::new(|| None));
        assert_eq!(
            ownership.pause_reason().as_deref(),
            Some("fighting a monster")
        );

        ownership.release("plugin", Some(&mut walk));

        assert_eq!(walk.stops, 1);
        assert!(ownership.owner_of(&walk).is_none());
        assert!(ownership.pause_reason().is_none());
    }

    #[test]
    fn releasing_a_plugin_leaves_another_owners_walk_alone() {
        let mut ownership = WalkOwnership::new();
        let mut walk = FakeWalk::default();
        go(&mut ownership, "plugin", &mut walk);

        ownership.release("other", Some(&mut walk));

        assert_eq!(walk.stops, 0);
        assert_eq!(ownership.owner_of(&walk), Some("plugin"));
    }

    #[test]
    fn a_pause_names_its_need_trimmed_and_a_blank_one_needs_nothing() {
        let mut ownership = WalkOwnership::new();
        let blank = ownership.add_pause("a", Box::new(|| Some("   ".to_owned())));
        assert!(ownership.pause_reason().is_none());
        ownership.add_pause("b", Box::new(|| Some("  buffing ".to_owned())));
        assert_eq!(ownership.pause_reason().as_deref(), Some("buffing"));
        assert!(ownership.remove_pause(blank));
        assert!(!ownership.remove_pause(blank));
    }

    #[test]
    fn a_report_names_its_owner_and_counts_its_changes() {
        let mut ownership = WalkOwnership::new();
        let mut walk = FakeWalk::default();
        go(&mut ownership, "plugin", &mut walk);

        let (owner, first) = ownership.report(&walk, 1);
        let (_, same) = ownership.report(&walk, 1);
        let (_, changed) = ownership.report(&walk, 2);
        walk.busy = false;
        let (gone, _) = ownership.report(&walk, 2);

        assert_eq!(owner.as_deref(), Some("plugin"));
        assert_eq!(first, same);
        assert_eq!(changed, first + 1);
        assert!(gone.is_none());
    }

    #[test]
    fn a_walk_that_ended_by_itself_leaves_no_owner() {
        let mut ownership = WalkOwnership::new();
        let mut walk = FakeWalk::default();
        go(&mut ownership, "first", &mut walk);

        walk.busy = false;

        assert!(ownership.owner_of(&walk).is_none());
        assert_eq!(
            ownership.stop_walk("second", &mut walk),
            NavCommandStatus::Rejected
        );
        assert_eq!(
            go(&mut ownership, "second", &mut walk),
            NavCommandStatus::Accepted
        );
        assert_eq!(ownership.owner_of(&walk), Some("second"));
    }

    #[test]
    fn arguments_are_still_checked_before_ownership() {
        let mut ownership = WalkOwnership::new();
        let mut walk = FakeWalk::default();
        go(&mut ownership, "first", &mut walk);

        let refused = ownership.begin_walk(
            "second",
            &mut walk,
            validate_arrival(0, 2.0),
            FakeWalk::request,
        );

        assert_eq!(refused, NavCommandStatus::Rejected);
        assert_eq!(ownership.owner_of(&walk), Some("first"));
    }

    #[test]
    fn arrival_and_places_are_checked() {
        assert_eq!(validate_arrival(0, 2.5), NavCommandStatus::Rejected);
        assert_eq!(validate_arrival(7, 0.0), NavCommandStatus::Rejected);
        assert_eq!(validate_arrival(7, 50.5), NavCommandStatus::Rejected);
        assert_eq!(validate_arrival(7, f32::NAN), NavCommandStatus::Rejected);
        assert_eq!(validate_arrival(7, 50.0), NavCommandStatus::Accepted);
        let ground = MapPosition {
            elevation: f64::NAN,
            ..MapPosition::default()
        };
        assert_eq!(validate_place(&ground, 2.5), NavCommandStatus::Accepted);
        let infinite = MapPosition {
            elevation: f64::INFINITY,
            ..MapPosition::default()
        };
        assert_eq!(validate_place(&infinite, 2.5), NavCommandStatus::Rejected);
    }

    #[test]
    fn a_position_reads_as_map_coordinates_and_back() {
        // Holtburg's 0xA9B4 landblock: x 0xA9 = 169, y 0xB4 = 180.
        let local = Vec3::new(84.0, 84.0, 48.0);
        let map = MapPosition::of(0xA9B4_0021, local);

        assert!((map.east_west - (169.0 - 127.0) * 192.0 / 240.0).abs() < 1e-9);
        assert!((map.north_south - (180.0 - 127.0) * 192.0 / 240.0).abs() < 1e-9);
        assert!((map.elevation - 0.2).abs() < 1e-9);
        assert!(map.is_outdoors());
        assert!((map.landblock_local() - local).length() < 1e-4);
    }
}
