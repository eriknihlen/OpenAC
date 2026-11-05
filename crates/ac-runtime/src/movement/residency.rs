//! Which landblocks' collision a session holds: the landblock the player is
//! in and its eight neighbours (a 3 x 3 square of 192 m landblocks), loaded
//! from the shared dats into the session's physics engine.
//!
//! The engine measures the world from one landblock's corner, the frame
//! centre: a landblock's corner sits at its offset from the centre in whole
//! 192 m steps. Placing the player (entering the world, a teleport, a
//! forced position) centres the frame on its landblock and loads the square
//! afresh, as OpenAC's headless collision neighbourhood does. Walking keeps
//! the frame and loads the new neighbours as the player crosses into a new
//! landblock, dropping those that left the 3 x 3 window.
//!
//! A portal arrival in a session no window holds loads its new square over
//! the session's passes instead ([`Residency::request_center`] and
//! [`Residency::advance`]): the arrival waits until the square is loaded,
//! as OpenAC's headless host waits for its collision neighbourhood.

use std::collections::{BTreeMap, BTreeSet, VecDeque};

use ac_physics::content::{ContentError, PlacedStatic, landblock_origin, publish_landblock};
use ac_physics::{CollisionWorld, PhysicsEngine, Vec3};

use super::content::WorldContent;

/// Landblocks within this many of the player's are loaded; the others are
/// dropped.
pub const NEAR_RADIUS: i32 = 1;

/// True when `landblock` is in the 3 x 3 window around `center` (OpenAC
/// `HeadlessCollisionNeighborhood.IsInWindow`).
#[must_use]
pub fn is_in_window(center: u32, landblock: u32) -> bool {
    chebyshev(center, landblock) <= NEAR_RADIUS
}

/// What a window around `center` loads that is not `resident` yet, centre
/// first, each at its corner measured from the `frame` landblock (OpenAC
/// `HeadlessCollisionNeighborhood.BuildPublicationPlan`).
#[must_use]
pub fn window_plan(center: u32, frame: u32, resident: &BTreeSet<u32>) -> Vec<(u32, Vec3)> {
    square(center, NEAR_RADIUS)
        .into_iter()
        .filter(|id| !resident.contains(id))
        .map(|id| (id, landblock_origin(frame, id)))
        .collect()
}

/// The canonical id (`0xXXYYFFFF`) of the landblock holding `cell_id`.
#[must_use]
pub fn canonical_landblock(cell_id: u32) -> u32 {
    (cell_id & 0xFFFF_0000) | 0xFFFF
}

fn coords(landblock: u32) -> (i32, i32) {
    (
        ((landblock >> 24) & 0xFF) as i32,
        ((landblock >> 16) & 0xFF) as i32,
    )
}

fn chebyshev(a: u32, b: u32) -> i32 {
    let (ax, ay) = coords(a);
    let (bx, by) = coords(b);
    (ax - bx).abs().max((ay - by).abs())
}

/// The landblocks within `radius` of `center`, on the map, centre first.
fn square(center: u32, radius: i32) -> Vec<u32> {
    let (cx, cy) = coords(center);
    let mut ids = vec![canonical_landblock(center)];
    for dx in -radius..=radius {
        for dy in -radius..=radius {
            if dx == 0 && dy == 0 {
                continue;
            }
            let (x, y) = (cx + dx, cy + dy);
            if !(0..=0xFF).contains(&x) || !(0..=0xFF).contains(&y) {
                continue;
            }
            ids.push(((x as u32) << 24) | ((y as u32) << 16) | 0xFFFF);
        }
    }
    ids
}

/// A new frame the server asked for, loading over the passes (OpenAC
/// `HeadlessCollisionNeighborhood`'s requested centre, reset and
/// publication queue).
#[derive(Debug)]
struct Recentre {
    /// The landblock the new square is built around.
    center: u32,
    /// The cell the player arrives in.
    cell_id: u32,
    /// Everything resident is still to be dropped and the frame moved.
    reset: bool,
    /// The landblocks still to load, centre first (built after the reset).
    plan: Option<VecDeque<(u32, Vec3)>>,
}

/// What one step of a recentre did.
#[derive(Debug, Default, Clone, Copy, PartialEq, Eq)]
pub struct RecentreStep {
    /// Everything resident was dropped and the frame moved to the new
    /// centre.
    pub reset: bool,
    /// The landblock this step loaded.
    pub loaded: Option<u32>,
    /// The square is loaded (or nothing was asked for).
    pub done: bool,
}

/// The session's resident collision.
#[derive(Debug, Default)]
pub struct Residency {
    /// The landblock whose corner is the engine's origin (0: no frame).
    frame_center: u32,
    /// The landblock the square is built around (0 while a new square
    /// loads).
    focus: u32,
    resident: BTreeSet<u32>,
    /// The static objects each resident landblock placed.
    placed: BTreeMap<u32, Vec<PlacedStatic>>,
    /// A new square loading over the passes.
    recentre: Option<Recentre>,
}

impl Residency {
    /// No frame, nothing resident.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// The landblock at the engine's origin, once there is a frame.
    #[must_use]
    pub fn frame_center(&self) -> Option<u32> {
        (self.frame_center != 0).then_some(self.frame_center)
    }

    /// The landblocks resident now.
    pub fn resident(&self) -> impl Iterator<Item = u32> + '_ {
        self.resident.iter().copied()
    }

    /// The corner of the landblock holding `cell_id` in the engine's frame.
    #[must_use]
    pub fn origin_of(&self, cell_id: u32) -> Option<Vec3> {
        self.frame_center()
            .map(|center| landblock_origin(center, canonical_landblock(cell_id)))
    }

    /// True when the landblock holding `cell_id` is resident.
    #[must_use]
    pub fn is_resident(&self, cell_id: u32) -> bool {
        self.resident.contains(&canonical_landblock(cell_id))
    }

    /// Centre the frame on the landblock holding `cell_id` and load the
    /// square around it afresh (everything resident is dropped first, and
    /// the engine starts empty with a new collision world), all at once.
    ///
    /// # Errors
    /// The centre landblock could not be loaded (a missing neighbour is
    /// skipped, as OpenAC skips one).
    pub fn center_on(
        &mut self,
        engine: &mut PhysicsEngine,
        content: &WorldContent,
        cell_id: u32,
    ) -> Result<(), ContentError> {
        self.recentre = Some(Recentre {
            center: canonical_landblock(cell_id),
            cell_id,
            reset: true,
            plan: None,
        });
        while !self.advance(engine, content)?.done {}
        Ok(())
    }

    /// Ask for the square around the landblock holding `cell_id`, loaded
    /// over the following [`Residency::advance`] steps (OpenAC
    /// `HeadlessCollisionNeighborhood.CenterOn`). The landblock the square
    /// is already built around only moves the player's cell; the one
    /// already asked for only updates the cell; any other starts a new
    /// frame.
    pub fn request_center(&mut self, engine: &mut PhysicsEngine, cell_id: u32) {
        let center = canonical_landblock(cell_id);
        if let Some(recentre) = self.recentre.as_mut()
            && recentre.center == center
        {
            recentre.cell_id = cell_id;
            return;
        }
        if self.recentre.is_none()
            && self.focus == center
            && self.resident.contains(&center)
            && engine.is_landblock_terrain_resident(center)
        {
            engine.update_player_curr_cell(cell_id);
            return;
        }
        self.recentre = Some(Recentre {
            center,
            cell_id,
            reset: true,
            plan: None,
        });
    }

    /// One step of the square asked for (OpenAC
    /// `HeadlessCollisionNeighborhood.AdvanceWork`): the first drops
    /// everything resident, moves the frame to the new centre and loads
    /// the centre; each later one loads the next neighbour; the square is
    /// built around the new centre once the last is in.
    ///
    /// # Errors
    /// The centre landblock could not be loaded; the request is dropped.
    pub fn advance(
        &mut self,
        engine: &mut PhysicsEngine,
        content: &WorldContent,
    ) -> Result<RecentreStep, ContentError> {
        let Some(mut recentre) = self.recentre.take() else {
            return Ok(RecentreStep {
                done: true,
                ..RecentreStep::default()
            });
        };
        let mut step = RecentreStep::default();
        if recentre.reset {
            engine.clear();
            engine.attach_world(CollisionWorld::new());
            self.placed.clear();
            self.resident.clear();
            self.frame_center = recentre.center;
            self.focus = 0;
            recentre.reset = false;
            step.reset = true;
        }
        let frame = self.frame_center;
        let plan = recentre.plan.get_or_insert_with(|| {
            window_plan(recentre.center, frame, &self.resident)
                .into_iter()
                .collect()
        });
        if let Some((id, origin)) = plan.pop_front() {
            match publish_landblock(engine, content.dats(), content.height_table(), id, origin)? {
                Some(publication) => {
                    self.resident.insert(id);
                    self.placed.insert(id, publication.placed);
                    step.loaded = Some(id);
                }
                None if id == recentre.center => {
                    return Err(ContentError::MissingLandblock(id));
                }
                None => {}
            }
        }
        if plan.is_empty() {
            self.focus = recentre.center;
            engine.update_player_curr_cell(recentre.cell_id);
            step.done = true;
        } else {
            self.recentre = Some(recentre);
        }
        Ok(step)
    }

    /// True while a square asked for is still loading.
    #[must_use]
    pub fn is_recentring(&self) -> bool {
        self.recentre.is_some()
    }

    /// The collision at `cell_id` is there to arrive on: the square is
    /// built around its landblock, which is loaded, and an interior cell
    /// is published (OpenAC `HeadlessCollisionNeighborhood.IsReady`).
    #[must_use]
    pub fn is_ready(&self, engine: &PhysicsEngine, cell_id: u32) -> bool {
        let center = canonical_landblock(cell_id);
        self.recentre.is_none()
            && self.focus == center
            && self.resident.contains(&center)
            && engine.is_landblock_terrain_resident(center)
            && (cell_id & 0xFFFF < 0x0100
                || engine.world().is_some_and(|w| w.cell(cell_id).is_some()))
    }

    /// The player is in `cell_id`: when that is a new landblock, load the
    /// neighbours not yet resident and drop those too far away. The frame
    /// stays. Returns the landblocks loaded and dropped.
    ///
    /// # Errors
    /// A landblock could not be loaded.
    pub fn follow(
        &mut self,
        engine: &mut PhysicsEngine,
        content: &WorldContent,
        cell_id: u32,
    ) -> Result<(Vec<u32>, Vec<u32>), ContentError> {
        let landblock = canonical_landblock(cell_id);
        // A new frame the server asked for finishes before the window
        // moves inside it (OpenAC `Follow`).
        if self.frame_center == 0
            || self.recentre.is_some()
            || landblock == self.focus
            || cell_id & 0xFFFF == 0
        {
            return Ok((Vec::new(), Vec::new()));
        }
        self.focus = landblock;
        // What left the window goes first, then the new landblocks come in
        // at their place in the same frame.
        let dropped: Vec<u32> = self
            .resident
            .iter()
            .copied()
            .filter(|&id| !is_in_window(landblock, id))
            .collect();
        for &id in &dropped {
            engine.remove_landblock(id);
            self.resident.remove(&id);
            self.placed.remove(&id);
        }
        let mut loaded = Vec::new();
        for (id, origin) in window_plan(landblock, self.frame_center, &self.resident) {
            if let Some(publication) =
                publish_landblock(engine, content.dats(), content.height_table(), id, origin)?
            {
                self.resident.insert(id);
                self.placed.insert(id, publication.placed);
                loaded.push(id);
            }
        }
        engine.update_player_curr_cell(cell_id);
        Ok((loaded, dropped))
    }

    /// The static objects of the resident landblocks: by landblock, each
    /// in placement order.
    pub fn placed(&self) -> impl Iterator<Item = &PlacedStatic> + '_ {
        self.placed.values().flatten()
    }

    /// Forget everything (the character left the world).
    pub fn reset(&mut self, engine: &mut PhysicsEngine) {
        engine.clear();
        self.resident.clear();
        self.placed.clear();
        self.frame_center = 0;
        self.focus = 0;
        self.recentre = None;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_square_is_the_landblock_and_its_neighbours_on_the_map() {
        let s = square(0xA9B4_0001, 1);
        assert_eq!(s.len(), 9);
        assert_eq!(s[0], 0xA9B4_FFFF);
        assert!(s.contains(&0xA8B3_FFFF) && s.contains(&0xAAB5_FFFF));
        // At the map's corner only four landblocks exist.
        assert_eq!(square(0x0000_0001, 1).len(), 4);
        assert_eq!(square(0xFFFF_0001, 1).len(), 4);
    }

    #[test]
    fn a_walk_north_loads_only_the_new_row_measured_from_the_same_frame() {
        // HeadlessCollisionNeighborhoodServiceWindowTests.
        // AWalkNorthPublishesOnlyTheNewRowMeasuredFromTheSameFrame:
        // arrived in 0x282A, the window is 0x27..0x29 by 0x29..0x2B; the
        // player walks into 0x282B, so the row y = 0x2C is new.
        let frame = 0x282A_FFFF;
        let mut resident = BTreeSet::new();
        for x in 0x27..=0x29u32 {
            for y in 0x29..=0x2Bu32 {
                resident.insert((x << 24) | (y << 16) | 0xFFFF);
            }
        }
        let mut plan = window_plan(0x282B_FFFF, frame, &resident);
        plan.sort_by_key(|(id, _)| *id);
        assert_eq!(
            plan,
            [
                (0x272C_FFFF, Vec3::new(-192.0, 384.0, 0.0)),
                (0x282C_FFFF, Vec3::new(0.0, 384.0, 0.0)),
                (0x292C_FFFF, Vec3::new(192.0, 384.0, 0.0)),
            ]
        );
        assert!(!is_in_window(0x282B_FFFF, 0x2829_FFFF));
        assert!(is_in_window(0x282B_FFFF, 0x292A_FFFF));
    }

    #[test]
    fn a_fresh_frame_loads_the_centre_first_at_the_worlds_zero() {
        // AFreshFramePublishesTheCentreFirstAtTheWorldsZero.
        let plan = window_plan(0x282A_FFFF, 0x282A_FFFF, &BTreeSet::new());
        assert_eq!(plan[0], (0x282A_FFFF, Vec3::ZERO));
        assert_eq!(plan.len(), 9);
    }

    #[test]
    fn distances_are_counted_in_whole_landblocks_along_the_longer_axis() {
        assert_eq!(chebyshev(0xA9B4_FFFF, 0xABB5_FFFF), 2);
        assert_eq!(chebyshev(0xA9B4_FFFF, 0xA9B4_0101), 0);
        assert_eq!(chebyshev(0xA9B4_FFFF, 0xA6B4_FFFF), 3);
    }
}
