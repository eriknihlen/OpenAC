//! Which landblocks a window keeps loaded around the player, in two tiers.
//!
//! The near tier (a square of `2 * near + 1` landblocks) is loaded in full:
//! terrain, static objects, buildings and their interior cells, scenery and
//! server objects. The far tier, out to `far`, is terrain only, so the
//! distance shows hills and coastlines without paying for every tree. As the
//! player moves, landblocks enter the far tier, are promoted to near, are
//! demoted back to far, and leave; a landblock only steps down once it is
//! two landblocks past its tier's edge, so walking back and forth along an
//! edge does not reload anything. Squares are by Chebyshev distance (the
//! larger of the x and y differences).

use std::collections::HashMap;

use crate::world_frame::LandblockCoord;

/// The changes a move asks for.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct TwoTierDiff {
    /// Entered the far tier from nothing: load terrain.
    pub load_far: Vec<LandblockCoord>,
    /// Entered the near tier from nothing: load everything.
    pub load_near: Vec<LandblockCoord>,
    /// Far became near: add objects, buildings and cells.
    pub promote: Vec<LandblockCoord>,
    /// Near became far: drop objects, buildings and cells.
    pub demote: Vec<LandblockCoord>,
    /// Left the far tier: drop everything.
    pub unload: Vec<LandblockCoord>,
}

impl TwoTierDiff {
    /// True when nothing changes.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.load_far.is_empty()
            && self.load_near.is_empty()
            && self.promote.is_empty()
            && self.demote.is_empty()
            && self.unload.is_empty()
    }
}

/// A landblock's tier.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Tier {
    /// Terrain only.
    Far,
    /// Everything.
    Near,
}

/// The two-tier window and what each resident landblock holds.
#[derive(Debug, Clone)]
pub struct StreamingRegion {
    center: (i32, i32),
    near_radius: i32,
    far_radius: i32,
    residence: HashMap<LandblockCoord, Tier>,
    bootstrapped: bool,
}

fn coord(x: i32, y: i32) -> Option<LandblockCoord> {
    ((0..=0xFF).contains(&x) && (0..=0xFF).contains(&y)).then_some(LandblockCoord {
        x: x as u8,
        y: y as u8,
    })
}

impl StreamingRegion {
    /// A window centred on (`cx`, `cy`); `far` is raised to `near` when
    /// smaller.
    #[must_use]
    pub fn new(cx: i32, cy: i32, near: u32, far: u32) -> Self {
        let near_radius = near as i32;
        Self {
            center: (cx, cy),
            near_radius,
            far_radius: (far as i32).max(near_radius),
            residence: HashMap::new(),
            bootstrapped: false,
        }
    }

    /// The centre landblock.
    #[must_use]
    pub fn center(&self) -> (i32, i32) {
        self.center
    }

    /// The near radius.
    #[must_use]
    pub fn near_radius(&self) -> u32 {
        self.near_radius as u32
    }

    /// The far radius.
    #[must_use]
    pub fn far_radius(&self) -> u32 {
        self.far_radius as u32
    }

    /// The tier a resident landblock is held at.
    #[must_use]
    pub fn tier(&self, block: LandblockCoord) -> Option<Tier> {
        self.residence.get(&block).copied()
    }

    /// Every resident landblock and its tier.
    pub fn resident(&self) -> impl Iterator<Item = (LandblockCoord, Tier)> + '_ {
        self.residence.iter().map(|(c, t)| (*c, *t))
    }

    /// The whole window as loads, near rings first.
    #[must_use]
    pub fn first_tick_diff(&self) -> TwoTierDiff {
        let mut diff = TwoTierDiff::default();
        for ring in 0..=self.far_radius {
            for (x, y) in ring_cells(self.center, ring) {
                let Some(c) = coord(x, y) else {
                    continue;
                };
                if ring <= self.near_radius {
                    diff.load_near.push(c);
                } else {
                    diff.load_far.push(c);
                }
            }
        }
        diff
    }

    /// Record the first tick's loads as resident (once).
    pub fn mark_resident_from_bootstrap(&mut self) {
        if self.bootstrapped {
            return;
        }
        self.residence.clear();
        let d = self.first_tick_diff();
        for c in d.load_near {
            self.residence.insert(c, Tier::Near);
        }
        for c in d.load_far {
            self.residence.insert(c, Tier::Far);
        }
        self.bootstrapped = true;
    }

    /// Move the window to (`cx`, `cy`) and say what changes. Before the
    /// bootstrap this bootstraps at the new centre.
    pub fn recenter_to(&mut self, cx: i32, cy: i32) -> TwoTierDiff {
        if !self.bootstrapped {
            self.center = (cx, cy);
            let d = self.first_tick_diff();
            self.mark_resident_from_bootstrap();
            return d;
        }
        let near_unload = self.near_radius + 2;
        let far_unload = self.far_radius + 2;
        let mut diff = TwoTierDiff::default();
        let mut in_window = std::collections::HashSet::new();
        for ring in 0..=self.far_radius {
            for (x, y) in ring_cells((cx, cy), ring) {
                let Some(c) = coord(x, y) else {
                    continue;
                };
                in_window.insert(c);
                let in_near = ring <= self.near_radius;
                match self.residence.get(&c) {
                    None => {
                        if in_near {
                            diff.load_near.push(c);
                        } else {
                            diff.load_far.push(c);
                        }
                        self.residence
                            .insert(c, if in_near { Tier::Near } else { Tier::Far });
                    }
                    Some(Tier::Far) if in_near => {
                        diff.promote.push(c);
                        self.residence.insert(c, Tier::Near);
                    }
                    _ => {}
                }
            }
        }
        let mut resident: Vec<(LandblockCoord, Tier)> =
            self.residence.iter().map(|(c, t)| (*c, *t)).collect();
        resident.sort_by_key(|(c, _)| (c.x, c.y));
        for (c, tier) in resident {
            let dx = (i32::from(c.x) - cx).abs();
            let dy = (i32::from(c.y) - cy).abs();
            let distance = dx.max(dy);
            if in_window.contains(&c) {
                if tier == Tier::Near && distance > self.near_radius && distance > near_unload {
                    diff.demote.push(c);
                    self.residence.insert(c, Tier::Far);
                }
                continue;
            }
            match tier {
                Tier::Near => {
                    if distance > near_unload {
                        diff.demote.push(c);
                        self.residence.insert(c, Tier::Far);
                        if distance > far_unload {
                            diff.unload.push(c);
                            self.residence.remove(&c);
                        }
                    }
                }
                Tier::Far => {
                    if distance > far_unload {
                        diff.unload.push(c);
                        self.residence.remove(&c);
                    }
                }
            }
        }
        self.center = (cx, cy);
        diff
    }

    /// Change the radii in place (the centre stays): every resident
    /// landblock outside the new window unloads, a near one now in the far
    /// tier is demoted, a far one now in the near tier promoted, and what
    /// the new window lacks loads. `far` is raised to `near` when smaller.
    /// Before the bootstrap only the radii change.
    pub fn reconfigure_radii(&mut self, near: u32, far: u32) -> TwoTierDiff {
        let rebuilt = Self::new(self.center.0, self.center.1, near, far);
        let mut diff = TwoTierDiff::default();
        if !self.bootstrapped
            || (rebuilt.near_radius, rebuilt.far_radius) == (self.near_radius, self.far_radius)
        {
            self.near_radius = rebuilt.near_radius;
            self.far_radius = rebuilt.far_radius;
            return diff;
        }
        let desired = rebuilt.first_tick_diff();
        let near_set: std::collections::HashSet<LandblockCoord> =
            desired.load_near.iter().copied().collect();
        let far_set: std::collections::HashSet<LandblockCoord> =
            desired.load_far.iter().copied().collect();
        let mut resident: Vec<(LandblockCoord, Tier)> = self.resident().collect();
        resident.sort_by_key(|(c, _)| (c.x, c.y));
        for (c, tier) in resident {
            if near_set.contains(&c) {
                if tier == Tier::Far {
                    diff.promote.push(c);
                    self.residence.insert(c, Tier::Near);
                }
            } else if far_set.contains(&c) {
                if tier == Tier::Near {
                    diff.demote.push(c);
                    self.residence.insert(c, Tier::Far);
                }
            } else {
                diff.unload.push(c);
                self.residence.remove(&c);
            }
        }
        for c in desired.load_near {
            if let std::collections::hash_map::Entry::Vacant(e) = self.residence.entry(c) {
                e.insert(Tier::Near);
                diff.load_near.push(c);
            }
        }
        for c in desired.load_far {
            if let std::collections::hash_map::Entry::Vacant(e) = self.residence.entry(c) {
                e.insert(Tier::Far);
                diff.load_far.push(c);
            }
        }
        self.near_radius = rebuilt.near_radius;
        self.far_radius = rebuilt.far_radius;
        diff
    }

    /// Drop every landblock (a teleport rebuilds the window from nothing)
    /// and say which were resident.
    pub fn reset(&mut self) -> Vec<LandblockCoord> {
        self.bootstrapped = false;
        self.residence.drain().map(|(c, _)| c).collect()
    }
}

/// The landblocks at exactly `ring` (Chebyshev) from `center`.
fn ring_cells(center: (i32, i32), ring: i32) -> Vec<(i32, i32)> {
    let mut out = Vec::new();
    for dy in -ring..=ring {
        for dx in -ring..=ring {
            if dx.abs().max(dy.abs()) == ring {
                out.push((center.0 + dx, center.1 + dy));
            }
        }
    }
    out
}

/// Whether the player is inside a sealed dungeon, where only that
/// landblock's cells are loaded (no terrain around it).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct DungeonGate {
    /// Inside a sealed dungeon.
    pub inside_dungeon: bool,
    /// The landblock (`cell >> 16`) the window collapses onto.
    pub observer_landblock: Option<u32>,
}

/// The dungeon gate: a sealed dungeon cell (interior, no view outside)
/// collapses streaming onto its landblock, except while a teleport is
/// being held (the current cell is stale then).
#[must_use]
pub fn dungeon_gate(
    teleport_hold: bool,
    cell_is_sealed_dungeon: bool,
    cell_id: u32,
) -> DungeonGate {
    if !teleport_hold && cell_is_sealed_dungeon {
        DungeonGate {
            inside_dungeon: true,
            observer_landblock: Some(cell_id >> 16),
        }
    } else {
        DungeonGate {
            inside_dungeon: false,
            observer_landblock: None,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn c(x: u8, y: u8) -> LandblockCoord {
        LandblockCoord { x, y }
    }

    // Ported from OpenAC tests/AcDream.Core.Tests/Streaming/StreamingRegionTwoTierTests.cs.

    #[test]
    fn two_radii_are_kept() {
        let r = StreamingRegion::new(100, 100, 4, 12);
        assert_eq!(
            (r.near_radius(), r.far_radius(), r.center()),
            (4, 12, (100, 100))
        );
    }

    #[test]
    fn first_tick_splits_into_near_and_far() {
        let r = StreamingRegion::new(100, 100, 1, 3);
        let d = r.first_tick_diff();
        assert_eq!(d.load_near.len(), 9);
        assert_eq!(d.load_far.len(), 40);
        assert!(d.promote.is_empty() && d.demote.is_empty() && d.unload.is_empty());
    }

    fn bootstrapped(cx: i32, cy: i32, near: u32, far: u32) -> StreamingRegion {
        let mut r = StreamingRegion::new(cx, cy, near, far);
        let _ = r.first_tick_diff();
        r.mark_resident_from_bootstrap();
        r
    }

    #[test]
    fn walking_brings_a_column_into_the_far_tier() {
        let mut r = bootstrapped(100, 100, 1, 3);
        let d = r.recenter_to(101, 100);
        for y in 97..=103 {
            assert!(d.load_far.contains(&c(104, y)));
        }
        assert!(d.load_near.is_empty());
        assert_eq!(d.promote.len(), 3);
        assert!(d.unload.is_empty());
    }

    #[test]
    fn far_landblocks_are_promoted_not_reloaded() {
        let mut r = bootstrapped(100, 100, 1, 3);
        let d = r.recenter_to(102, 100);
        assert!(d.promote.contains(&c(102, 100)));
        assert!(!d.load_near.contains(&c(102, 100)));
        assert!(!d.load_far.contains(&c(102, 100)));
    }

    #[test]
    fn a_teleport_loads_a_fresh_window() {
        let mut r = bootstrapped(100, 100, 1, 3);
        let d = r.recenter_to(200, 200);
        assert_eq!(d.load_near.len(), 9);
        assert_eq!(d.load_far.len(), 40);
        assert!(d.promote.is_empty());
    }

    #[test]
    fn demotion_waits_for_the_hysteresis() {
        let mut r = bootstrapped(100, 100, 2, 4);
        let d1 = r.recenter_to(103, 100);
        assert!(!d1.demote.contains(&c(100, 100)));
        let d2 = r.recenter_to(105, 100);
        assert!(d2.demote.contains(&c(100, 100)));
    }

    #[test]
    fn unloading_waits_for_the_hysteresis() {
        let mut r = bootstrapped(100, 100, 1, 3);
        let d1 = r.recenter_to(101, 100);
        assert!(!d1.unload.contains(&c(97, 100)));
        let d2 = r.recenter_to(103, 100);
        assert!(d2.unload.contains(&c(97, 100)));
    }

    #[test]
    fn oscillating_on_an_edge_does_not_thrash() {
        let mut r = bootstrapped(103, 100, 2, 4);
        let (mut demotes, mut promotes) = (0, 0);
        for _ in 0..5 {
            let d1 = r.recenter_to(102, 100);
            demotes += d1.demote.len();
            promotes += d1.promote.len();
            let d2 = r.recenter_to(103, 100);
            demotes += d2.demote.len();
            promotes += d2.promote.len();
        }
        assert_eq!(demotes, 0);
        assert!(promotes <= 5, "{promotes}");
    }

    // OpenAC tests/AcDream.Core.Tests/Streaming/StreamingControllerTests.cs:
    // ReconfigureRadii_SmallerWindowUnloadsOnlyOutsideResidents,
    // _LargerWindowLoadsOnlyMissingResidents, _UnchangedWindowDoesNotClearOrRebootstrap.

    #[test]
    fn a_smaller_window_unloads_only_the_residents_outside_it() {
        let mut r = bootstrapped(50, 50, 2, 2);
        let d = r.reconfigure_radii(0, 0);
        assert!(d.load_near.is_empty() && d.load_far.is_empty());
        assert_eq!(d.unload.len(), 24);
        assert!(!d.unload.contains(&c(50, 50)));
        assert_eq!((r.near_radius(), r.far_radius()), (0, 0));
        assert_eq!(r.resident().count(), 1);
    }

    #[test]
    fn a_larger_window_loads_only_the_missing_residents() {
        let mut r = bootstrapped(50, 50, 0, 0);
        let d = r.reconfigure_radii(1, 1);
        assert_eq!(d.load_near.len(), 8);
        assert!(!d.load_near.contains(&c(50, 50)));
        assert!(d.unload.is_empty() && d.load_far.is_empty());
    }

    #[test]
    fn an_unchanged_window_changes_nothing_and_tiers_follow_the_radii() {
        let mut r = bootstrapped(50, 50, 1, 2);
        assert!(r.reconfigure_radii(1, 2).is_empty());
        let d = r.reconfigure_radii(2, 2);
        assert_eq!(d.promote.len(), 16, "the far ring becomes near");
        let d = r.reconfigure_radii(0, 2);
        assert_eq!(d.demote.len(), 24);
        assert_eq!(r.tier(c(51, 51)), Some(Tier::Far));
    }

    // Ported from OpenAC tests/AcDream.Core.Tests/Streaming/DungeonStreamingGateTests.cs.

    const DUNGEON_CELL: u32 = 0x0007_0145;
    const OUTDOOR_CELL: u32 = 0xAB34_0001;

    #[test]
    fn a_sealed_dungeon_collapses_onto_its_landblock() {
        let g = dungeon_gate(false, true, DUNGEON_CELL);
        assert!(g.inside_dungeon);
        assert_eq!(g.observer_landblock, Some(0x0007));
    }

    #[test]
    fn outdoors_and_teleport_holds_do_not_collapse() {
        for (hold, sealed, cell) in [
            (false, false, OUTDOOR_CELL),
            (true, true, DUNGEON_CELL),
            (true, false, OUTDOOR_CELL),
        ] {
            let g = dungeon_gate(hold, sealed, cell);
            assert!(!g.inside_dungeon);
            assert_eq!(g.observer_landblock, None);
        }
    }
}
