//! Region ambient sounds: what the land around the listener sounds like.
//!
//! The region data assigns ambient sounds to terrain and scenery. Around the
//! listener, the 64 land cells of each of the 3×3 nearest landblocks add a
//! weight (full inside 20 m, falling with the square of the distance to
//! nothing at 120 m) to each ambient sound their terrain names. A sound with
//! no chance value (`base_chance == 0`) is a continuous bed, heard from no
//! direction at a volume set by its share of all weight; any other is
//! intermittent, played now and then from one of the eight compass
//! directions it was heard from. Nothing loops: each sound is a one-shot
//! re-fired from a deadline queue.

use std::cmp::Ordering;
use std::collections::BinaryHeap;

use ac_dat::objects::{AmbientStbDesc, Region};
use glam::Vec3;

use crate::random::SoundRandom;

/// Within this distance a cell has full weight, metres.
pub const MIN_DISTANCE: f32 = 20.0;
/// [`MIN_DISTANCE`] squared.
pub const MIN_DISTANCE_SQ: f32 = 400.0;
/// Beyond this distance a cell has no weight, metres.
pub const MAX_DISTANCE: f32 = 120.0;
/// [`MAX_DISTANCE`] squared.
pub const MAX_DISTANCE_SQ: f32 = 14400.0;
/// A continuous bed quieter than this (linear) is not played.
pub const MIN_VOLUME: f32 = 0.03;
/// The bearing spread of an intermittent sound's direction, radians (22.5°).
pub const HEADING_SPREAD: f32 = std::f32::consts::FRAC_PI_8;
/// Inside this squared distance (≈14.14 m) a cell counts as all around.
pub const IN_VIEWER_BLOCK_DISTANCE_SQ: f32 = MIN_DISTANCE_SQ * 0.5;
/// Half the thickness of a direction's distance shell, metres.
pub const SHELL_HALF_THICKNESS: f32 = MIN_DISTANCE * 0.5;
/// Near bound of the all-around spread, metres.
pub const IN_BLOCK_NEAR_DISTANCE: f32 = 4.0;
/// Land cell side, metres.
pub const LAND_CELL_LENGTH: f32 = 24.0;
/// Land cells per landblock side.
pub const CELLS_PER_SIDE: usize = 8;
/// Terrain words per landblock side (a 9×9 vertex grid).
const VERTICES_PER_SIDE: usize = 9;
const NO_INDEX: u32 = 0xFFFF_FFFF;

/// Where an ambient contribution comes from.
#[allow(missing_docs)]
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum AmbientDirection {
    InViewerBlock,
    North,
    South,
    East,
    West,
    Northwest,
    Southwest,
    Northeast,
    Southeast,
}

impl AmbientDirection {
    /// Compass heading of the direction, radians (north 0, east π/2).
    #[must_use]
    pub fn heading(self) -> f32 {
        match self {
            Self::North | Self::InViewerBlock => 0.0,
            Self::South => std::f32::consts::PI,
            Self::East => std::f32::consts::FRAC_PI_2,
            Self::West => 4.712_389,
            Self::Northwest => 5.497_787,
            Self::Southwest => 3.926_990_7,
            Self::Northeast => std::f32::consts::FRAC_PI_4,
            Self::Southeast => 2.356_194_5,
        }
    }
}

/// A cell's weight at `offset` from the listener.
#[must_use]
pub fn calc_weight(offset: Vec3) -> f32 {
    let d2 = offset.length_squared();
    if d2 > MAX_DISTANCE_SQ {
        0.0
    } else if d2 < MIN_DISTANCE_SQ {
        1.0
    } else {
        MIN_DISTANCE_SQ / d2
    }
}

/// The direction of `offset`: all around inside ≈14 m; diagonal when both
/// axes are within a factor of two of each other; else the larger axis.
/// Height is ignored.
#[must_use]
pub fn calc_direction(offset: Vec3) -> AmbientDirection {
    let (x, y) = (offset.x, offset.y);
    if x * x + y * y < IN_VIEWER_BLOCK_DISTANCE_SQ {
        return AmbientDirection::InViewerBlock;
    }
    let (ax, ay) = (x.abs(), y.abs());
    const DIAGONAL_RATIO: f32 = 2.0;
    const EPSILON: f32 = 0.0002;
    let diagonal =
        ax > EPSILON && ay > EPSILON && ay / ax <= DIAGONAL_RATIO && ax / ay <= DIAGONAL_RATIO;
    if diagonal {
        return match (y >= 0.0, x >= 0.0) {
            (true, true) => AmbientDirection::Northeast,
            (true, false) => AmbientDirection::Northwest,
            (false, true) => AmbientDirection::Southeast,
            (false, false) => AmbientDirection::Southwest,
        };
    }
    if ay >= ax {
        if y >= 0.0 {
            AmbientDirection::North
        } else {
            AmbientDirection::South
        }
    } else if x >= 0.0 {
        AmbientDirection::East
    } else {
        AmbientDirection::West
    }
}

/// One authored ambient sound.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct AmbientSoundDescriptor {
    /// Sound type within the table.
    pub sound: u32,
    /// Volume.
    pub volume: f32,
    /// Chance per firing; 0 means continuous.
    pub base_chance: f32,
    /// Shortest interval, seconds (the only one for a continuous bed).
    pub min_rate: f32,
    /// Longest interval, seconds.
    pub max_rate: f32,
}

impl AmbientSoundDescriptor {
    /// A continuous bed (no chance value).
    #[must_use]
    pub fn is_continuous(&self) -> bool {
        self.base_chance == 0.0
    }
}

/// The distance range an intermittent sound is heard over in one direction.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct AmbientDirectionShell {
    /// The direction.
    pub direction: AmbientDirection,
    /// Nearest, metres.
    pub min_distance: f32,
    /// Farthest, metres.
    pub max_distance: f32,
}

/// One ambient sound being tracked around the listener.
#[derive(Debug, Clone, PartialEq)]
pub struct AmbientSoundInstance {
    descriptor: AmbientSoundDescriptor,
    sound_table_id: u32,
    sound_count: f32,
    current_volume: f32,
    play_chance: f32,
    on_queue: bool,
    directions: Vec<AmbientDirectionShell>,
}

impl AmbientSoundInstance {
    /// A new instance with no weight.
    #[must_use]
    pub fn new(descriptor: AmbientSoundDescriptor, sound_table_id: u32) -> Self {
        Self {
            descriptor,
            sound_table_id,
            sound_count: 0.0,
            current_volume: 0.0,
            play_chance: 0.0,
            on_queue: false,
            directions: Vec::new(),
        }
    }

    /// The authored sound.
    #[must_use]
    pub fn descriptor(&self) -> &AmbientSoundDescriptor {
        &self.descriptor
    }
    /// The sound table its slot is looked up in.
    #[must_use]
    pub fn sound_table_id(&self) -> u32 {
        self.sound_table_id
    }
    /// The weight gathered this rebuild.
    #[must_use]
    pub fn sound_count(&self) -> f32 {
        self.sound_count
    }
    /// A continuous bed's volume (its share of all weight).
    #[must_use]
    pub fn current_volume(&self) -> f32 {
        self.current_volume
    }
    /// An intermittent sound's chance per firing.
    #[must_use]
    pub fn play_chance(&self) -> f32 {
        self.play_chance
    }
    /// Holds a deadline in the queue.
    #[must_use]
    pub fn on_queue(&self) -> bool {
        self.on_queue
    }
    /// The directions it was heard from.
    #[must_use]
    pub fn directions(&self) -> &[AmbientDirectionShell] {
        &self.directions
    }

    /// Forget the last rebuild's weight and directions.
    pub fn reset_count(&mut self) {
        self.sound_count = 0.0;
        self.directions.clear();
        if !self.descriptor.is_continuous() {
            self.play_chance = 0.0;
        }
    }

    /// Add one cell's weight from `offset` in `direction`.
    pub fn add_to(&mut self, weight: f32, offset: Vec3, direction: AmbientDirection) {
        self.sound_count += weight;
        if self.descriptor.is_continuous() {
            return;
        }
        let distance = offset.length_squared().sqrt();
        let half = SHELL_HALF_THICKNESS;
        if direction != AmbientDirection::InViewerBlock {
            self.add_direction(direction, distance - half, distance + half);
            return;
        }
        for d in [
            AmbientDirection::North,
            AmbientDirection::South,
            AmbientDirection::East,
            AmbientDirection::West,
            AmbientDirection::Northwest,
            AmbientDirection::Southwest,
            AmbientDirection::Northeast,
            AmbientDirection::Southeast,
        ] {
            self.add_direction(d, IN_BLOCK_NEAR_DISTANCE, half);
        }
    }

    fn add_direction(&mut self, direction: AmbientDirection, min: f32, max: f32) {
        if let Some(s) = self
            .directions
            .iter_mut()
            .find(|s| s.direction == direction)
        {
            s.min_distance = s.min_distance.min(min);
            s.max_distance = s.max_distance.max(max);
            return;
        }
        if self.directions.len() >= 8 {
            return;
        }
        self.directions.push(AmbientDirectionShell {
            direction,
            min_distance: min,
            max_distance: max,
        });
    }

    /// Turn the gathered weight into a volume (bed) or a chance (one-shot).
    pub fn update_sound(&mut self, total_sound_count: f32) {
        if self.descriptor.is_continuous() {
            self.current_volume = if self.sound_count == 0.0 {
                0.0
            } else {
                self.descriptor.volume / total_sound_count * self.sound_count
            };
            return;
        }
        if self.sound_count <= 0.0 {
            return;
        }
        self.play_chance = self.descriptor.base_chance / total_sound_count * self.sound_count;
    }

    /// Loud enough (bed) or likely enough (one-shot) to schedule.
    #[must_use]
    pub fn can_hear(&self) -> bool {
        if self.descriptor.is_continuous() {
            self.current_volume >= MIN_VOLUME
        } else {
            self.play_chance > 0.0
        }
    }

    /// Whether this firing makes a sound: always for a bed; a one-shot rolls
    /// against its chance (inclusive).
    pub fn play_now(&self, rng: &mut dyn SoundRandom) -> bool {
        self.descriptor.is_continuous() || rng.next_variant_roll() <= self.play_chance
    }

    /// The volume a firing plays at.
    #[must_use]
    pub fn volume(&self) -> f32 {
        if self.descriptor.is_continuous() {
            self.current_volume
        } else {
            self.descriptor.volume
        }
    }

    /// Seconds until the next firing: a bed's `min_rate`; a one-shot rolls
    /// between its two rates.
    pub fn play_interval(&self, rng: &mut dyn SoundRandom) -> f32 {
        if self.descriptor.is_continuous() {
            self.descriptor.min_rate
        } else {
            roll_dice(self.descriptor.min_rate, self.descriptor.max_rate, rng)
        }
    }

    /// Where a one-shot plays from: a random shell, a bearing within ±11.25°
    /// of its direction, and a distance biased toward the near edge
    /// (`min + (max - min)·t²`); same height as the listener. `None` for a
    /// bed or a sound with no direction.
    pub fn sound_position(&self, listener: Vec3, rng: &mut dyn SoundRandom) -> Option<Vec3> {
        if self.descriptor.is_continuous() || self.directions.is_empty() {
            return None;
        }
        let count = self.directions.len();
        let index = ((rng.next_variant_roll() * count as f32).floor() as usize).min(count - 1);
        let shell = self.directions[index];
        let spread = HEADING_SPREAD;
        let angle = shell.direction.heading() + rng.next_variant_roll() * spread - spread * 0.5;
        let t = rng.next_variant_roll();
        let distance = shell.min_distance + (shell.max_distance - shell.min_distance) * t * t;
        Some(Vec3::new(
            listener.x + angle.sin() * distance,
            listener.y + angle.cos() * distance,
            listener.z,
        ))
    }
}

/// A value between `min` and `max` (either order); `min` when equal.
pub fn roll_dice(min: f32, max: f32, rng: &mut dyn SoundRandom) -> f32 {
    if min == max {
        return min;
    }
    let (lo, hi) = if max < min { (max, min) } else { (min, max) };
    lo + (hi - lo) * rng.next_variant_roll()
}

/// One ambient sound to play now.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct AmbientSoundFiring {
    /// Index of the instance in [`AmbientSoundScheduler::instances`].
    pub instance: usize,
    /// Volume to play at.
    pub volume: f32,
    /// Where it plays from; `None` for a bed (no direction).
    pub position: Option<Vec3>,
}

#[derive(Debug, Clone, Copy)]
struct Deadline {
    at: f64,
    seq: u64,
    instance: usize,
}

impl PartialEq for Deadline {
    fn eq(&self, other: &Self) -> bool {
        self.cmp(other) == Ordering::Equal
    }
}
impl Eq for Deadline {}
impl PartialOrd for Deadline {
    fn partial_cmp(&self, other: &Self) -> Option<Ordering> {
        Some(self.cmp(other))
    }
}
impl Ord for Deadline {
    // Reversed so the max-heap pops the earliest deadline, oldest first.
    fn cmp(&self, other: &Self) -> Ordering {
        other
            .at
            .total_cmp(&self.at)
            .then_with(|| other.seq.cmp(&self.seq))
    }
}

/// The tracked ambient sounds and their deadline queue.
#[derive(Debug, Default)]
pub struct AmbientSoundScheduler {
    instances: Vec<AmbientSoundInstance>,
    queue: BinaryHeap<Deadline>,
    seq: u64,
    total_sound_count: f32,
}

impl AmbientSoundScheduler {
    /// An empty scheduler.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Every live instance, in the order they were first tracked.
    #[must_use]
    pub fn instances(&self) -> &[AmbientSoundInstance] {
        &self.instances
    }

    /// Instances holding a deadline.
    #[must_use]
    pub fn queued_count(&self) -> usize {
        self.queue.len()
    }

    /// All weight gathered this rebuild (once per cell).
    #[must_use]
    pub fn total_sound_count(&self) -> f32 {
        self.total_sound_count
    }

    /// Start a rebuild: every instance forgets its weight.
    pub fn begin_rebuild(&mut self) {
        for i in &mut self.instances {
            i.reset_count();
        }
        self.total_sound_count = 0.0;
    }

    /// The instance for this sound and table, created on first sight.
    pub fn track(&mut self, descriptor: AmbientSoundDescriptor, sound_table_id: u32) -> usize {
        if let Some(i) = self
            .instances
            .iter()
            .position(|i| i.descriptor == descriptor && i.sound_table_id == sound_table_id)
        {
            return i;
        }
        self.instances
            .push(AmbientSoundInstance::new(descriptor, sound_table_id));
        self.instances.len() - 1
    }

    /// One land cell at `offset` whose terrain names `table`: its weight
    /// counts once toward the total, and goes to every sound in the table.
    pub fn contribute_cell(&mut self, offset: Vec3, table: &AmbientStbDesc) {
        let weight = calc_weight(offset);
        if weight <= 0.0 {
            return;
        }
        let direction = calc_direction(offset);
        self.total_sound_count += weight;
        for s in &table.ambient_sounds {
            let i = self.track(
                AmbientSoundDescriptor {
                    sound: s.sound_type,
                    volume: s.volume,
                    base_chance: s.base_chance,
                    min_rate: s.min_rate,
                    max_rate: s.max_rate,
                },
                table.stb_id,
            );
            self.instances[i].add_to(weight, offset, direction);
        }
    }

    /// One contribution to one instance (tests and single sounds).
    pub fn contribute(&mut self, instance: usize, offset: Vec3) {
        let weight = calc_weight(offset);
        if weight <= 0.0 {
            return;
        }
        self.instances[instance].add_to(weight, offset, calc_direction(offset));
        self.total_sound_count += weight;
    }

    /// Finish a rebuild: volumes and chances from the shares; every audible
    /// instance not already queued fires now (into `firings`, if given) and
    /// is queued.
    pub fn end_rebuild(
        &mut self,
        now: f64,
        firings: Option<&mut Vec<AmbientSoundFiring>>,
        listener: Vec3,
        rng: &mut dyn SoundRandom,
    ) {
        let total = self.total_sound_count;
        for i in &mut self.instances {
            i.update_sound(total);
        }
        let mut firings = firings;
        for index in 0..self.instances.len() {
            let i = &self.instances[index];
            if i.on_queue || !i.can_hear() {
                continue;
            }
            self.fire(index, now, firings.as_deref_mut(), listener, rng);
        }
    }

    /// Fire every instance whose deadline is before `now`, re-queuing it.
    /// An instance that can no longer be heard leaves the queue.
    pub fn tick(
        &mut self,
        now: f64,
        firings: &mut Vec<AmbientSoundFiring>,
        listener: Vec3,
        rng: &mut dyn SoundRandom,
    ) {
        while self.queue.peek().is_some_and(|d| d.at < now) {
            let Some(d) = self.queue.pop() else { break };
            self.instances[d.instance].on_queue = false;
            if !self.instances[d.instance].can_hear() {
                continue;
            }
            self.fire(d.instance, now, Some(firings), listener, rng);
        }
    }

    /// Drop every instance and deadline.
    pub fn clear(&mut self) {
        self.instances.clear();
        self.queue.clear();
        self.total_sound_count = 0.0;
    }

    fn fire(
        &mut self,
        index: usize,
        now: f64,
        firings: Option<&mut Vec<AmbientSoundFiring>>,
        listener: Vec3,
        rng: &mut dyn SoundRandom,
    ) {
        if let Some(firings) = firings {
            let instance = &self.instances[index];
            if instance.play_now(rng) {
                let position = instance.sound_position(listener, rng);
                firings.push(AmbientSoundFiring {
                    instance: index,
                    volume: instance.volume(),
                    position,
                });
            }
        }
        let interval = self.instances[index].play_interval(rng);
        self.seq += 1;
        self.queue.push(Deadline {
            at: now + f64::from(interval),
            seq: self.seq,
            instance: index,
        });
        self.instances[index].on_queue = true;
    }
}

/// The parts of the region the ambient walk reads: terrain type → scene
/// type indices, scene type → sound group index, and the sound groups.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct AmbientRegion {
    /// Per terrain type, the scene type index for each scenery index.
    pub terrain_scene_types: Vec<Vec<u32>>,
    /// Per scene type, the index of its sound group (`0xFFFFFFFF` none).
    pub scene_stb_index: Vec<u32>,
    /// The sound groups.
    pub stb_desc: Vec<AmbientStbDesc>,
}

impl AmbientRegion {
    /// Extract the ambient parts of a region.
    #[must_use]
    pub fn from_region(region: &Region) -> Self {
        Self {
            terrain_scene_types: region
                .terrain_info
                .terrain_types
                .iter()
                .map(|t| t.scene_types.clone())
                .collect(),
            scene_stb_index: region
                .scene_info
                .as_ref()
                .map(|s| s.scene_types.iter().map(|t| t.stb_index).collect())
                .unwrap_or_default(),
            stb_desc: region
                .sound_info
                .as_ref()
                .map(|s| s.stb_desc.clone())
                .unwrap_or_default(),
        }
    }

    /// The sound group a terrain type and scenery index name, if any.
    #[must_use]
    pub fn stb_desc_for(&self, terrain_type: u32, scene_index: u32) -> Option<&AmbientStbDesc> {
        let scene_types = self.terrain_scene_types.get(terrain_type as usize)?;
        let scene_type = *scene_types.get(scene_index as usize)?;
        if scene_type == NO_INDEX {
            return None;
        }
        let stb_index = *self.scene_stb_index.get(scene_type as usize)?;
        if stb_index == NO_INDEX {
            return None;
        }
        let candidate = self.stb_desc.get(stb_index as usize)?;
        (candidate.stb_id != 0 && !candidate.ambient_sounds.is_empty()).then_some(candidate)
    }
}

/// Terrain words of one landblock (9×9, x-major).
pub type LandblockTerrain = [u16; VERTICES_PER_SIDE * VERTICES_PER_SIDE];

/// Rebuild the scheduler from the 3×3 landblocks around the viewer's.
/// `listener_local` is the listener's position inside its own landblock
/// (offsets are landblock-local, never absolute). `landblocks` returns a
/// loaded landblock's terrain words; a missing one contributes nothing.
pub fn rebuild_ambient(
    scheduler: &mut AmbientSoundScheduler,
    region: &AmbientRegion,
    viewer_landblock_id: u32,
    listener_local: Vec3,
    landblocks: &mut dyn FnMut(u32) -> Option<LandblockTerrain>,
    now: f64,
    rng: &mut dyn SoundRandom,
) {
    scheduler.begin_rebuild();
    let viewer_x = i64::from(viewer_landblock_id >> 24);
    let viewer_y = i64::from((viewer_landblock_id >> 16) & 0xFF);
    for dx in -1..=1i64 {
        for dy in -1..=1i64 {
            let (bx, by) = (viewer_x + dx, viewer_y + dy);
            if !(0..=0xFF).contains(&bx) || !(0..=0xFF).contains(&by) {
                continue;
            }
            let id = ((bx as u32) << 24) | ((by as u32) << 16) | 0xFFFF;
            let Some(terrain) = landblocks(id) else {
                continue;
            };
            contribute_landblock(scheduler, region, dx, dy, &terrain, listener_local);
        }
    }
    scheduler.end_rebuild(now, None, Vec3::ZERO, rng);
}

fn contribute_landblock(
    scheduler: &mut AmbientSoundScheduler,
    region: &AmbientRegion,
    block_dx: i64,
    block_dy: i64,
    terrain: &LandblockTerrain,
    listener_local: Vec3,
) {
    let landblock_length = CELLS_PER_SIDE as f32 * LAND_CELL_LENGTH;
    let origin_x = block_dx as f32 * landblock_length;
    let origin_y = block_dy as f32 * landblock_length;
    for x in 0..CELLS_PER_SIDE {
        for y in 0..CELLS_PER_SIDE {
            let raw = terrain[x * VERTICES_PER_SIDE + y];
            let terrain_type = u32::from((raw >> 2) & 0x1F);
            let scene_index = u32::from((raw >> 11) & 0x1F);
            let Some(stb) = region.stb_desc_for(terrain_type, scene_index) else {
                continue;
            };
            let offset = Vec3::new(
                origin_x + x as f32 * LAND_CELL_LENGTH - listener_local.x,
                origin_y + y as f32 * LAND_CELL_LENGTH - listener_local.y,
                0.0,
            );
            if offset.length_squared() > MAX_DISTANCE_SQ {
                continue;
            }
            scheduler.contribute_cell(offset, stb);
        }
    }
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.Core.Tests/Audio/AmbientSoundTests.cs`.

    use ac_dat::objects::AmbientSoundDesc;

    use super::*;
    use crate::random::scripted::Scripted;

    fn rng(rolls: &[f32]) -> Scripted {
        Scripted::lenient(rolls)
    }

    fn continuous(volume: f32, min_rate: f32) -> AmbientSoundDescriptor {
        AmbientSoundDescriptor {
            sound: 0x46,
            volume,
            base_chance: 0.0,
            min_rate,
            max_rate: 0.0,
        }
    }

    fn intermittent(
        volume: f32,
        base_chance: f32,
        min_rate: f32,
        max_rate: f32,
    ) -> AmbientSoundDescriptor {
        AmbientSoundDescriptor {
            sound: 0x47,
            volume,
            base_chance,
            min_rate,
            max_rate,
        }
    }

    fn bed() -> AmbientSoundDescriptor {
        continuous(1.0, 5.0)
    }
    fn chirp() -> AmbientSoundDescriptor {
        intermittent(1.0, 0.5, 4.0, 10.0)
    }

    fn x(v: f32) -> Vec3 {
        Vec3::new(v, 0.0, 0.0)
    }

    fn close(a: f32, b: f32, places: i32) -> bool {
        (a - b).abs() <= 0.5 * 10f32.powi(-places)
    }

    #[test]
    fn base_chance_zero_means_continuous_not_intermittent() {
        assert!(bed().is_continuous());
        assert!(!chirp().is_continuous());
    }

    #[test]
    fn calc_weight_is_full_inside_twenty_metres_then_inverse_square() {
        for (d, expected) in [
            (0.0, 1.0),
            (19.9, 1.0),
            (20.0, 1.0),
            (40.0, 0.25),
            (120.0, 400.0 / 14400.0),
            (120.1, 0.0),
            (500.0, 0.0),
        ] {
            assert!(close(calc_weight(x(d)), expected, 4), "{d}");
        }
    }

    #[test]
    fn calc_direction_inside_fourteen_metres_is_in_viewer_block() {
        assert_eq!(calc_direction(x(14.1)), AmbientDirection::InViewerBlock);
        assert_ne!(calc_direction(x(14.2)), AmbientDirection::InViewerBlock);
    }

    #[test]
    fn calc_direction_sectors_match_retail() {
        use AmbientDirection::*;
        for (vx, vy, expected) in [
            (0.0, 50.0, North),
            (0.0, -50.0, South),
            (50.0, 0.0, East),
            (-50.0, 0.0, West),
            (40.0, 40.0, Northeast),
            (-40.0, 40.0, Northwest),
            (40.0, -40.0, Southeast),
            (-40.0, -40.0, Southwest),
        ] {
            assert_eq!(calc_direction(Vec3::new(vx, vy, 0.0)), expected);
        }
    }

    #[test]
    fn calc_direction_diagonal_needs_both_axes_within_two_times() {
        assert_eq!(
            calc_direction(Vec3::new(20.0, 50.0, 0.0)),
            AmbientDirection::North
        );
        assert_eq!(
            calc_direction(Vec3::new(30.0, 40.0, 0.0)),
            AmbientDirection::Northeast
        );
    }

    #[test]
    fn continuous_volume_is_its_share_of_the_total_weight() {
        let mut s = AmbientSoundScheduler::new();
        s.begin_rebuild();
        let grass = s.track(bed(), 0x2000_0001);
        let shore = s.track(bed(), 0x2000_0002);
        s.contribute(grass, x(1.0));
        s.contribute(grass, x(2.0));
        s.contribute(grass, x(3.0));
        s.contribute(shore, x(4.0));
        s.end_rebuild(0.0, None, Vec3::ZERO, &mut rng(&[0.0]));
        assert!(close(s.total_sound_count(), 4.0, 4));
        assert!(close(s.instances()[grass].current_volume(), 0.75, 4));
        assert!(close(s.instances()[shore].current_volume(), 0.25, 4));
    }

    #[test]
    fn continuous_bed_below_the_audibility_floor_cannot_be_heard() {
        let mut s = AmbientSoundScheduler::new();
        s.begin_rebuild();
        let faint = s.track(bed(), 0x2000_0001);
        let loud = s.track(bed(), 0x2000_0002);
        s.contribute(faint, x(119.0));
        for i in 0..5 {
            s.contribute(loud, x(i as f32));
        }
        s.end_rebuild(0.0, None, Vec3::ZERO, &mut rng(&[0.0]));
        assert!(s.instances()[faint].current_volume() < MIN_VOLUME);
        assert!(!s.instances()[faint].can_hear());
        assert!(s.instances()[loud].can_hear());
    }

    #[test]
    fn intermittent_play_chance_scales_with_its_share_and_volume_stays_authored() {
        let mut s = AmbientSoundScheduler::new();
        s.begin_rebuild();
        let a = s.track(intermittent(0.8, 0.6, 4.0, 10.0), 1);
        let b = s.track(intermittent(0.8, 0.6, 4.0, 10.0), 2);
        s.contribute(a, x(1.0));
        s.contribute(b, x(2.0));
        s.end_rebuild(0.0, None, Vec3::ZERO, &mut rng(&[0.0]));
        assert!(close(s.instances()[a].play_chance(), 0.3, 4));
        assert!(close(s.instances()[a].volume(), 0.8, 4));
    }

    #[test]
    fn intermittent_update_with_zero_weight_leaves_play_chance_alone() {
        let mut s = AmbientSoundScheduler::new();
        s.begin_rebuild();
        let i = s.track(intermittent(1.0, 0.6, 4.0, 10.0), 1);
        s.contribute(i, x(1.0));
        s.end_rebuild(0.0, None, Vec3::ZERO, &mut rng(&[0.0]));
        assert!(close(s.instances()[i].play_chance(), 0.6, 4));
        s.begin_rebuild();
        s.end_rebuild(0.0, None, Vec3::ZERO, &mut rng(&[0.0]));
        assert_eq!(s.instances()[i].play_chance(), 0.0);
        assert!(!s.instances()[i].can_hear());
    }

    #[test]
    fn rebuild_resets_stale_bearings() {
        let mut s = AmbientSoundScheduler::new();
        s.begin_rebuild();
        let i = s.track(chirp(), 1);
        s.contribute(i, Vec3::new(0.0, 60.0, 0.0));
        s.end_rebuild(0.0, None, Vec3::ZERO, &mut rng(&[0.0]));
        let has_north = |s: &AmbientSoundScheduler| {
            s.instances()[i]
                .directions()
                .iter()
                .any(|d| d.direction == AmbientDirection::North)
        };
        assert!(has_north(&s));
        s.begin_rebuild();
        s.contribute(i, Vec3::new(0.0, -60.0, 0.0));
        s.end_rebuild(0.0, None, Vec3::ZERO, &mut rng(&[0.0]));
        assert!(!has_north(&s));
    }

    #[test]
    fn in_viewer_block_contribution_spreads_across_all_eight_directions() {
        let mut s = AmbientSoundScheduler::new();
        s.begin_rebuild();
        let i = s.track(chirp(), 1);
        s.contribute(i, x(3.0));
        s.end_rebuild(0.0, None, Vec3::ZERO, &mut rng(&[0.0]));
        let dirs = s.instances()[i].directions();
        assert_eq!(dirs.len(), 8);
        for d in dirs {
            assert!(close(d.min_distance, IN_BLOCK_NEAR_DISTANCE, 3));
            assert!(close(d.max_distance, SHELL_HALF_THICKNESS, 3));
        }
    }

    #[test]
    fn continuous_play_now_is_always_true() {
        assert!(AmbientSoundInstance::new(bed(), 1).play_now(&mut rng(&[0.99])));
    }

    #[test]
    fn intermittent_play_now_rolls_against_play_chance() {
        for (roll, expected) in [(0.4, true), (0.5, true), (0.6, false)] {
            let mut s = AmbientSoundScheduler::new();
            s.begin_rebuild();
            let i = s.track(chirp(), 1);
            s.contribute(i, x(1.0));
            s.end_rebuild(0.0, None, Vec3::ZERO, &mut rng(&[0.0]));
            assert!(close(s.instances()[i].play_chance(), 0.5, 4));
            assert_eq!(s.instances()[i].play_now(&mut rng(&[roll])), expected);
        }
    }

    #[test]
    fn continuous_bed_has_no_position() {
        let i = AmbientSoundInstance::new(bed(), 1);
        assert_eq!(
            i.sound_position(Vec3::new(100.0, 200.0, 10.0), &mut rng(&[0.0])),
            None
        );
    }

    fn north_chirp() -> (AmbientSoundScheduler, usize) {
        let mut s = AmbientSoundScheduler::new();
        s.begin_rebuild();
        let i = s.track(chirp(), 1);
        s.contribute(i, Vec3::new(0.0, 60.0, 0.0));
        s.end_rebuild(0.0, None, Vec3::ZERO, &mut rng(&[0.0]));
        (s, i)
    }

    #[test]
    fn intermittent_position_offsets_the_listener_and_keeps_their_z() {
        let (s, i) = north_chirp();
        let listener = Vec3::new(100.0, 200.0, 37.0);
        let p = s.instances()[i]
            .sound_position(listener, &mut rng(&[0.0, 0.5, 1.0]))
            .unwrap();
        assert_eq!(p.z, 37.0);
        assert!(p.y > listener.y);
        assert!(close(p.x, 100.0, 1));
    }

    #[test]
    fn intermittent_distance_is_quadratically_biased_toward_min() {
        let (s, i) = north_chirp();
        let shell = s.instances()[i].directions()[0];
        let p = s.instances()[i]
            .sound_position(Vec3::ZERO, &mut rng(&[0.0, 0.5, 0.5]))
            .unwrap();
        let quadratic = shell.min_distance + (shell.max_distance - shell.min_distance) * 0.25;
        let linear = shell.min_distance + (shell.max_distance - shell.min_distance) * 0.5;
        assert!(close(p.length(), quadratic, 1));
        assert!(!close(p.length(), linear, 1));
    }

    #[test]
    fn continuous_interval_uses_min_rate_only() {
        let i = AmbientSoundInstance::new(continuous(1.0, 7.0), 1);
        assert_eq!(i.play_interval(&mut rng(&[0.9])), 7.0);
    }

    #[test]
    fn intermittent_interval_rolls_between_the_authored_rates() {
        let i = AmbientSoundInstance::new(intermittent(1.0, 0.5, 4.0, 10.0), 1);
        assert!(close(i.play_interval(&mut rng(&[0.5])), 7.0, 3));
    }

    #[test]
    fn roll_dice_swaps_an_inverted_range() {
        assert!(close(roll_dice(10.0, 4.0, &mut rng(&[0.5])), 7.0, 3));
    }

    #[test]
    fn scheduler_fires_at_the_deadline_and_re_arms() {
        let mut r = rng(&[]);
        let mut s = AmbientSoundScheduler::new();
        let mut firings = Vec::new();
        s.begin_rebuild();
        let b = s.track(bed(), 1);
        s.contribute(b, Vec3::ZERO);
        s.end_rebuild(0.0, Some(&mut firings), Vec3::ZERO, &mut r);
        firings.clear();
        s.tick(4.9, &mut firings, Vec3::ZERO, &mut r);
        assert!(firings.is_empty());
        s.tick(5.0, &mut firings, Vec3::ZERO, &mut r);
        assert!(firings.is_empty());
        s.tick(5.01, &mut firings, Vec3::ZERO, &mut r);
        assert_eq!(firings.len(), 1);
        assert!(s.instances()[b].on_queue());
        assert_eq!(s.queued_count(), 1);
        firings.clear();
        s.tick(10.1, &mut firings, Vec3::ZERO, &mut r);
        assert_eq!(firings.len(), 1);
    }

    #[test]
    fn scheduler_arming_plays_immediately_with_no_initial_delay() {
        let mut s = AmbientSoundScheduler::new();
        let mut firings = Vec::new();
        s.begin_rebuild();
        let b = s.track(continuous(1.0, 30.0), 1);
        s.contribute(b, Vec3::ZERO);
        s.end_rebuild(100.0, Some(&mut firings), Vec3::ZERO, &mut rng(&[]));
        assert_eq!(firings.len(), 1);
        assert!(s.instances()[b].on_queue());
    }

    #[test]
    fn scheduler_zero_play_interval_does_not_spin_forever() {
        let mut r = rng(&[]);
        let mut s = AmbientSoundScheduler::new();
        s.begin_rebuild();
        let b = s.track(continuous(1.0, 0.0), 1);
        s.contribute(b, Vec3::ZERO);
        s.end_rebuild(0.0, None, Vec3::ZERO, &mut r);
        let mut firings = Vec::new();
        s.tick(1.0, &mut firings, Vec3::ZERO, &mut r);
        assert_eq!(firings.len(), 1);
    }

    #[test]
    fn scheduler_does_not_re_arm_an_already_queued_instance_on_rebuild() {
        let mut r = rng(&[]);
        let mut s = AmbientSoundScheduler::new();
        s.begin_rebuild();
        let b = s.track(bed(), 1);
        s.contribute(b, Vec3::ZERO);
        s.end_rebuild(0.0, None, Vec3::ZERO, &mut r);
        assert_eq!(s.queued_count(), 1);
        for _ in 0..5 {
            s.begin_rebuild();
            s.contribute(b, Vec3::ZERO);
            s.end_rebuild(0.0, None, Vec3::ZERO, &mut r);
        }
        assert_eq!(s.queued_count(), 1);
    }

    #[test]
    fn scheduler_arms_an_instance_that_becomes_audible() {
        let mut r = rng(&[]);
        let mut s = AmbientSoundScheduler::new();
        s.begin_rebuild();
        let b = s.track(bed(), 1);
        s.end_rebuild(0.0, None, Vec3::ZERO, &mut r);
        assert_eq!(s.queued_count(), 0);
        s.begin_rebuild();
        s.contribute(b, Vec3::ZERO);
        s.end_rebuild(0.0, None, Vec3::ZERO, &mut r);
        assert_eq!(s.queued_count(), 1);
    }

    #[test]
    fn scheduler_drops_an_instance_that_went_inaudible() {
        let mut r = rng(&[]);
        let mut s = AmbientSoundScheduler::new();
        s.begin_rebuild();
        let b = s.track(bed(), 1);
        s.contribute(b, Vec3::ZERO);
        s.end_rebuild(0.0, None, Vec3::ZERO, &mut r);
        s.begin_rebuild();
        s.end_rebuild(0.0, None, Vec3::ZERO, &mut r);
        let mut firings = Vec::new();
        s.tick(99.0, &mut firings, Vec3::ZERO, &mut r);
        assert!(firings.is_empty());
        assert_eq!(s.queued_count(), 0);
        assert!(!s.instances()[b].on_queue());
    }

    #[test]
    fn scheduler_continuous_firing_has_no_position_intermittent_does() {
        let mut r = rng(&[]);
        let mut s = AmbientSoundScheduler::new();
        s.begin_rebuild();
        let b = s.track(continuous(1.0, 1.0), 1);
        let c = s.track(intermittent(1.0, 1.0, 1.0, 1.0), 2);
        s.contribute(b, Vec3::ZERO);
        s.contribute(c, Vec3::new(0.0, 60.0, 0.0));
        s.end_rebuild(0.0, None, Vec3::ZERO, &mut r);
        let mut firings = Vec::new();
        s.tick(1.01, &mut firings, Vec3::ZERO, &mut r);
        assert_eq!(firings.len(), 2);
        let of = |i| firings.iter().find(|f| f.instance == i).unwrap();
        assert_eq!(of(b).position, None);
        assert!(of(c).position.is_some());
    }

    #[test]
    fn scheduler_clear_drops_everything() {
        let mut s = AmbientSoundScheduler::new();
        s.begin_rebuild();
        let b = s.track(bed(), 1);
        s.contribute(b, Vec3::ZERO);
        s.end_rebuild(0.0, None, Vec3::ZERO, &mut rng(&[]));
        s.clear();
        assert_eq!(s.queued_count(), 0);
        assert!(s.instances().is_empty());
        assert_eq!(s.total_sound_count(), 0.0);
    }

    fn synthetic_region(entries: u32) -> AmbientRegion {
        AmbientRegion {
            terrain_scene_types: vec![vec![0]],
            scene_stb_index: vec![0],
            stb_desc: vec![AmbientStbDesc {
                stb_id: 0x2000_0001,
                ambient_sounds: (0..entries)
                    .map(|i| AmbientSoundDesc {
                        sound_type: 0x46 + i,
                        volume: 1.0,
                        base_chance: 0.0,
                        min_rate: 5.0,
                        max_rate: 0.0,
                    })
                    .collect(),
            }],
        }
    }

    fn uniform(_: u32) -> Option<LandblockTerrain> {
        Some([0; 81])
    }

    fn rebuild(
        region: &AmbientRegion,
        landblocks: &mut dyn FnMut(u32) -> Option<LandblockTerrain>,
    ) -> AmbientSoundScheduler {
        let mut s = AmbientSoundScheduler::new();
        rebuild_ambient(
            &mut s,
            region,
            0xA9B4_FFFF,
            Vec3::new(96.0, 96.0, 0.0),
            landblocks,
            0.0,
            &mut rng(&[]),
        );
        s
    }

    #[test]
    fn gatherer_listener_inside_the_block_accumulates_weight() {
        let s = rebuild(&synthetic_region(1), &mut uniform);
        assert!(s.total_sound_count() > 0.0);
        assert_eq!(s.instances().len(), 1);
        assert!(s.instances()[0].can_hear());
    }

    #[test]
    fn gatherer_total_weight_counts_cells_once_not_once_per_ambient_entry() {
        let single = rebuild(&synthetic_region(1), &mut uniform);
        let triple = rebuild(&synthetic_region(3), &mut uniform);
        assert!(close(
            single.total_sound_count(),
            triple.total_sound_count(),
            3
        ));
        assert_eq!(triple.instances().len(), 3);
        let one = single.instances()[0].current_volume();
        for b in triple.instances() {
            assert!(close(b.current_volume(), one, 3));
        }
    }

    #[test]
    fn gatherer_missing_neighbours_contribute_nothing() {
        let s = rebuild(&synthetic_region(1), &mut |id| {
            (id == 0xA9B4_FFFF).then_some([0; 81])
        });
        assert!(s.total_sound_count() > 0.0);
    }

    #[test]
    fn gatherer_unauthored_terrain_produces_no_ambients() {
        let mut region = synthetic_region(1);
        region.scene_stb_index[0] = 0xFFFF_FFFF;
        let s = rebuild(&region, &mut uniform);
        assert!(s.instances().is_empty());
        assert_eq!(s.total_sound_count(), 0.0);
    }
}
