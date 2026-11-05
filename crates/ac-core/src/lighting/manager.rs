//! Every registered light, and the choices made from them each frame.
//!
//! Three choices are made:
//! - the **active set**: the sun in slot 0, then the point lights nearest
//!   the camera, eight slots in all (the fixed-function light count);
//! - the **point snapshot**: the lights the renderer may use this frame,
//!   up to 7 dynamic lights and 40 static ones, each list ranked by the
//!   distance from the light's owner to the player (not the camera);
//! - per object, the up to 8 snapshot lights nearest its centre whose reach
//!   touches it ([`select_for_object`]); per cell, the whole snapshot
//!   ([`select_for_cell`]).
//!
//! Lights live in the manager and are addressed by [`LightId`].

use std::fmt;

use glam::{Mat4, Vec3};

use super::source::{CellAmbientState, LightKind, LightSource};
use crate::sky::{SkyKeyframe, sun_direction};

/// Handle of a registered light.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct LightId(u64);

/// A point or spot light was registered without a ranking point.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct MissingRankingOrigin;

impl fmt::Display for MissingRankingOrigin {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str("a nondirectional light requires an explicit authored ranking origin")
    }
}

impl std::error::Error for MissingRankingOrigin {}

/// Lights per active set (slot 0 is the sun when there is one).
pub const MAX_ACTIVE_LIGHTS: usize = 8;
/// Lights one object is lit by.
pub const MAX_LIGHTS_PER_OBJECT: usize = 8;
/// Dynamic lights in the point snapshot.
pub const MAX_DYNAMIC_POINT_LIGHTS: usize = 7;
/// Static lights in the point snapshot.
pub const MAX_STATIC_POINT_LIGHTS: usize = 40;
/// Lights in the point snapshot.
pub const MAX_GLOBAL_LIGHTS: usize = MAX_DYNAMIC_POINT_LIGHTS + MAX_STATIC_POINT_LIGHTS;
/// Lights one cell is lit by: the whole snapshot.
pub const MAX_LIGHTS_PER_ENV_CELL: usize = MAX_GLOBAL_LIGHTS;

/// The player's own fill light: brightness.
pub const VIEWER_LIGHT_INTENSITY: f32 = 2.25;
/// The player's own fill light: falloff, metres (reach is 1.5 times this).
pub const VIEWER_LIGHT_FALLOFF: f32 = 10.0;
/// Owner id of the player's fill light; never an object id.
pub const VIEWER_LIGHT_OWNER_ID: u32 = 0xFFFF_FFFF;
/// Height of the player's fill light above the player, metres.
const VIEWER_LIGHT_HEIGHT: f32 = 2.0;

/// Ambient light level inside a sealed cell (no view of the sky).
pub const INDOOR_AMBIENT_LEVEL: f32 = 0.20;

#[derive(Debug, Clone)]
struct Entry {
    id: LightId,
    light: LightSource,
}

/// The light registry and its per-frame choices.
#[derive(Debug, Clone, Default)]
pub struct LightManager {
    /// Registration order; ids rise with it, so the list is sorted by id.
    all: Vec<Entry>,
    next_id: u64,
    sun: Option<LightSource>,
    /// The point lights of the active set, nearest first.
    active_points: Vec<LightId>,
    sun_active: bool,
    viewer_light: Option<LightId>,
    current_ambient: CellAmbientState,
    point_snapshot: Vec<LightId>,
    dynamic_point_snapshot: Vec<LightId>,
    static_point_snapshot: Vec<LightId>,
}

impl LightManager {
    /// An empty registry.
    #[must_use]
    pub fn new() -> Self {
        Self {
            active_points: Vec::with_capacity(MAX_ACTIVE_LIGHTS),
            dynamic_point_snapshot: Vec::with_capacity(MAX_DYNAMIC_POINT_LIGHTS + 1),
            static_point_snapshot: Vec::with_capacity(MAX_STATIC_POINT_LIGHTS + 1),
            point_snapshot: Vec::with_capacity(MAX_GLOBAL_LIGHTS),
            ..Self::default()
        }
    }

    /// Add a light. Point and spot lights need a ranking point.
    ///
    /// # Errors
    /// [`MissingRankingOrigin`].
    pub fn register(&mut self, light: LightSource) -> Result<LightId, MissingRankingOrigin> {
        if light.kind != LightKind::Directional && light.ranking_origin.is_none() {
            return Err(MissingRankingOrigin);
        }
        Ok(self.push(light))
    }

    fn push(&mut self, light: LightSource) -> LightId {
        let id = LightId(self.next_id);
        self.next_id += 1;
        self.all.push(Entry { id, light });
        id
    }

    /// Remove a light. Unknown ids are ignored.
    pub fn unregister(&mut self, id: LightId) {
        if let Some(index) = self.index_of(id) {
            self.all.remove(index);
            self.forget(|candidate| candidate == id);
        }
    }

    /// Remove every light owned by `owner_id`.
    pub fn unregister_by_owner(&mut self, owner_id: u32) {
        let mut removed = Vec::new();
        self.all.retain(|e| {
            let keep = e.light.owner_id != owner_id;
            if !keep {
                removed.push(e.id);
            }
            keep
        });
        if !removed.is_empty() {
            self.forget(|candidate| removed.contains(&candidate));
        }
    }

    /// Drop removed lights from the per-frame choices.
    fn forget(&mut self, gone: impl Fn(LightId) -> bool) {
        self.active_points.retain(|&id| !gone(id));
        self.point_snapshot.retain(|&id| !gone(id));
        self.dynamic_point_snapshot.retain(|&id| !gone(id));
        self.static_point_snapshot.retain(|&id| !gone(id));
        if self.viewer_light.is_some_and(&gone) {
            self.viewer_light = None;
        }
    }

    /// Remove everything, the player's fill light included.
    pub fn clear(&mut self) {
        self.all.clear();
        self.active_points.clear();
        self.sun_active = false;
        self.viewer_light = None;
        self.point_snapshot.clear();
        self.dynamic_point_snapshot.clear();
        self.static_point_snapshot.clear();
    }

    fn index_of(&self, id: LightId) -> Option<usize> {
        self.all.binary_search_by_key(&id, |e| e.id).ok()
    }

    /// A registered light.
    #[must_use]
    pub fn get(&self, id: LightId) -> Option<&LightSource> {
        self.index_of(id).map(|i| &self.all[i].light)
    }

    /// A registered light, to change.
    pub fn get_mut(&mut self, id: LightId) -> Option<&mut LightSource> {
        self.index_of(id).map(|i| &mut self.all[i].light)
    }

    /// Every registered light in registration order.
    pub fn iter(&self) -> impl ExactSizeIterator<Item = (LightId, &LightSource)> {
        self.all.iter().map(|e| (e.id, &e.light))
    }

    /// Number of registered lights (the player's fill light counts once
    /// it exists).
    #[must_use]
    pub fn registered_count(&self) -> usize {
        self.all.len()
    }

    /// The sun (not registered; it takes slot 0 of the active set).
    #[must_use]
    pub fn sun(&self) -> Option<&LightSource> {
        self.sun.as_ref()
    }

    /// Set or clear the sun.
    pub fn set_sun(&mut self, sun: Option<LightSource>) {
        self.sun = sun;
    }

    /// The ambient and sun state the scene uses.
    #[must_use]
    pub fn current_ambient(&self) -> CellAmbientState {
        self.current_ambient
    }

    /// Set the ambient and sun state.
    pub fn set_current_ambient(&mut self, ambient: CellAmbientState) {
        self.current_ambient = ambient;
    }

    /// Set the sun and ambient from the sky. Inside a sealed cell (one with
    /// no view outside) the sun is black and ambient is a flat 0.2 white;
    /// otherwise the sun has the keyframe's sun colour and the ambient its
    /// ambient colour. The sun's direction is from the sun towards the
    /// world either way.
    pub fn update_sun_from_sky(&mut self, keyframe: &SkyKeyframe, player_inside_cell: bool) {
        let sun_to_world = -sun_direction(keyframe);
        if player_inside_cell {
            self.sun = Some(LightSource {
                kind: LightKind::Directional,
                world_forward: sun_to_world,
                color_linear: Vec3::ZERO,
                intensity: 0.0,
                range: 1.0,
                ..LightSource::default()
            });
            self.current_ambient = CellAmbientState {
                ambient_color: Vec3::splat(INDOOR_AMBIENT_LEVEL),
                sun_color: Vec3::ZERO,
                sun_direction: sun_to_world,
            };
            return;
        }
        let sun_color = keyframe.sun_color();
        self.sun = Some(LightSource {
            kind: LightKind::Directional,
            world_forward: sun_to_world,
            color_linear: sun_color,
            intensity: 1.0,
            range: 1.0,
            ..LightSource::default()
        });
        self.current_ambient = CellAmbientState {
            ambient_color: keyframe.ambient_color(),
            sun_color,
            sun_direction: sun_to_world,
        };
    }

    /// Choose the active set: the sun (when set) in slot 0, then the lit
    /// point and spot lights nearest `viewer_world_pos` by their own
    /// position, filling the eight slots. Every candidate's `dist_sq` is
    /// updated.
    pub fn tick(&mut self, viewer_world_pos: Vec3) {
        self.active_points.clear();
        self.sun_active = self.sun.is_some();
        let base_slot = usize::from(self.sun_active);
        let max_point = MAX_ACTIVE_LIGHTS - base_slot;

        // Indices into `all`, nearest first.
        let mut chosen = [0usize; MAX_ACTIVE_LIGHTS];
        let mut filled = 0usize;
        for index in 0..self.all.len() {
            let light = &mut self.all[index].light;
            if !light.is_lit || light.kind == LightKind::Directional {
                continue;
            }
            let delta = light.world_position - viewer_world_pos;
            light.dist_sq = delta.length_squared();
            let dist_sq = light.dist_sq;

            if filled < max_point {
                let mut j = filled;
                while j > 0 && self.all[chosen[j - 1]].light.dist_sq > dist_sq {
                    chosen[j] = chosen[j - 1];
                    j -= 1;
                }
                chosen[j] = index;
                filled += 1;
            } else if dist_sq < self.all[chosen[max_point - 1]].light.dist_sq {
                let mut j = max_point - 1;
                while j > 0 && self.all[chosen[j - 1]].light.dist_sq > dist_sq {
                    chosen[j] = chosen[j - 1];
                    j -= 1;
                }
                chosen[j] = index;
            }
        }
        for &index in &chosen[..filled] {
            self.active_points.push(self.all[index].id);
        }
    }

    /// The active set: the sun first when it took slot 0, then the point
    /// lights nearest first.
    pub fn active(&self) -> impl Iterator<Item = &LightSource> {
        let sun = if self.sun_active {
            self.sun.as_ref()
        } else {
            None
        };
        sun.into_iter()
            .chain(self.active_points.iter().filter_map(|&id| self.get(id)))
    }

    /// Size of the active set.
    #[must_use]
    pub fn active_count(&self) -> usize {
        usize::from(self.sun_active && self.sun.is_some()) + self.active_points.len()
    }

    /// Move the player's fill light (a white point light 2 m above the
    /// player), creating it the first time.
    pub fn update_viewer_light(&mut self, player_world_pos: Vec3) {
        let id = match self.viewer_light {
            Some(id) => id,
            None => {
                let id = self.push(LightSource {
                    kind: LightKind::Point,
                    local_pose: Mat4::from_translation(Vec3::new(0.0, 0.0, VIEWER_LIGHT_HEIGHT)),
                    color_linear: Vec3::ONE,
                    intensity: VIEWER_LIGHT_INTENSITY,
                    range: VIEWER_LIGHT_FALLOFF * 1.5,
                    owner_id: VIEWER_LIGHT_OWNER_ID,
                    is_lit: true,
                    is_dynamic: true,
                    ..LightSource::default()
                });
                self.viewer_light = Some(id);
                id
            }
        };
        if let Some(light) = self.get_mut(id) {
            light.ranking_origin = Some(player_world_pos);
            light.world_position = player_world_pos + Vec3::new(0.0, 0.0, VIEWER_LIGHT_HEIGHT);
        }
    }

    /// Build the point snapshot for this frame: the player's fill light
    /// first among the dynamic lights, then every other lit point or spot
    /// light into the dynamic (7) or static (40) list. Each list keeps the
    /// lights nearest the player, ranked by owner position (spot lights
    /// rank 0); a light only goes before ones strictly farther, so equal
    /// distances keep registration order. Dynamic lights come first.
    pub fn build_point_light_snapshot(&mut self, player_world_pos: Vec3) {
        self.point_snapshot.clear();
        let mut dynamics = std::mem::take(&mut self.dynamic_point_snapshot);
        let mut statics = std::mem::take(&mut self.static_point_snapshot);
        dynamics.clear();
        statics.clear();

        if let Some(viewer) = self.viewer_light
            && let Some(index) = self.index_of(viewer)
            && self.all[index].light.is_lit
        {
            self.insert_retained(
                index,
                player_world_pos,
                &mut dynamics,
                MAX_DYNAMIC_POINT_LIGHTS,
            );
        }

        for index in 0..self.all.len() {
            let entry = &self.all[index];
            if Some(entry.id) == self.viewer_light
                || !entry.light.is_lit
                || entry.light.kind == LightKind::Directional
            {
                continue;
            }
            if entry.light.is_dynamic {
                self.insert_retained(
                    index,
                    player_world_pos,
                    &mut dynamics,
                    MAX_DYNAMIC_POINT_LIGHTS,
                );
            } else {
                self.insert_retained(
                    index,
                    player_world_pos,
                    &mut statics,
                    MAX_STATIC_POINT_LIGHTS,
                );
            }
        }

        self.point_snapshot.extend_from_slice(&dynamics);
        self.point_snapshot.extend_from_slice(&statics);
        self.dynamic_point_snapshot = dynamics;
        self.static_point_snapshot = statics;
    }

    fn insert_retained(
        &mut self,
        index: usize,
        player_world_pos: Vec3,
        product: &mut Vec<LightId>,
        capacity: usize,
    ) {
        let light = &mut self.all[index].light;
        let rank = if light.kind == LightKind::Point {
            light.ranking_point().distance_squared(player_world_pos)
        } else {
            0.0
        };
        light.dist_sq = rank;
        let id = self.all[index].id;

        let mut at = 0;
        while at < product.len() {
            let other = self.get(product[at]).map_or(f32::NAN, |l| l.dist_sq);
            if rank < other {
                break;
            }
            at += 1;
        }
        if at >= capacity {
            return;
        }
        product.insert(at, id);
        if product.len() > capacity {
            product.truncate(capacity);
        }
    }

    /// This frame's point snapshot, dynamic lights first.
    #[must_use]
    pub fn point_snapshot(&self) -> &[LightId] {
        &self.point_snapshot
    }

    /// The point snapshot's lights, in order.
    pub fn point_snapshot_lights(&self) -> impl ExactSizeIterator<Item = &LightSource> {
        self.point_snapshot.iter().map(|&id| {
            self.get(id)
                .expect("snapshot ids are dropped when their light is removed")
        })
    }
}

/// The up to eight lights of `snapshot` that reach an object centred at
/// `center` with radius `radius`, nearest first by the light's position.
/// Writes positions within `snapshot` into `out_indices` and returns how
/// many. Depends only on the object, never on the camera.
pub fn select_for_object<'a>(
    snapshot: impl IntoIterator<Item = &'a LightSource>,
    center: Vec3,
    radius: f32,
    out_indices: &mut [usize],
) -> usize {
    let cap = out_indices.len().min(MAX_LIGHTS_PER_OBJECT);
    if cap == 0 {
        return 0;
    }
    let mut kept_dist_sq = [0.0f32; MAX_LIGHTS_PER_OBJECT];
    let mut count = 0usize;

    for (li, light) in snapshot.into_iter().enumerate() {
        let reach = light.range + radius;
        let dsq = (light.world_position - center).length_squared();
        if dsq >= reach * reach {
            continue;
        }
        if count < cap {
            let mut j = count;
            while j > 0 && kept_dist_sq[j - 1] > dsq {
                kept_dist_sq[j] = kept_dist_sq[j - 1];
                out_indices[j] = out_indices[j - 1];
                j -= 1;
            }
            kept_dist_sq[j] = dsq;
            out_indices[j] = li;
            count += 1;
        } else if dsq < kept_dist_sq[cap - 1] {
            let mut j = cap - 1;
            while j > 0 && kept_dist_sq[j - 1] > dsq {
                kept_dist_sq[j] = kept_dist_sq[j - 1];
                out_indices[j] = out_indices[j - 1];
                j -= 1;
            }
            kept_dist_sq[j] = dsq;
            out_indices[j] = li;
        }
    }
    count
}

/// The lights of a cell: the whole snapshot, in order, up to
/// [`MAX_LIGHTS_PER_ENV_CELL`] (the shader applies each light's reach).
/// Writes positions into `out_indices` and returns how many.
pub fn select_for_cell(snapshot_len: usize, out_indices: &mut [usize]) -> usize {
    let count = snapshot_len
        .min(out_indices.len())
        .min(MAX_LIGHTS_PER_ENV_CELL);
    for (index, slot) in out_indices.iter_mut().take(count).enumerate() {
        *slot = index;
    }
    count
}

#[cfg(test)]
mod tests {
    //! OpenAC `LightManagerTests`. Registering the same light twice cannot
    //! happen here (a light is moved in), and the zero-allocation checks
    //! are not ported (the lists keep their capacity between frames).
    use super::*;
    use crate::sky::DotNetRandom;

    fn point(pos: Vec3, range: f32, owner_id: u32, lit: bool, cell_id: u32) -> LightSource {
        LightSource {
            kind: LightKind::Point,
            world_position: pos,
            range,
            is_lit: lit,
            owner_id,
            cell_id,
            ..LightSource::default()
        }
        .with_ranking_origin(pos)
    }

    fn make_point(pos: Vec3, range: f32) -> LightSource {
        point(pos, range, 0, true, 0)
    }

    fn make_dynamic(pos: Vec3, range: f32) -> LightSource {
        LightSource {
            is_dynamic: true,
            ..make_point(pos, range)
        }
    }

    fn x(v: f32) -> Vec3 {
        Vec3::new(v, 0.0, 0.0)
    }

    fn snapshot(mgr: &LightManager) -> Vec<LightId> {
        mgr.point_snapshot().to_vec()
    }

    #[test]
    fn register_and_unregister_track_the_list() {
        let mut mgr = LightManager::new();
        let a = mgr.register(make_point(Vec3::ZERO, 5.0)).unwrap();
        mgr.register(make_point(x(10.0), 5.0)).unwrap();
        assert_eq!(mgr.registered_count(), 2);
        mgr.unregister(a);
        assert_eq!(mgr.registered_count(), 1);
    }

    #[test]
    fn a_point_light_without_a_ranking_origin_is_refused() {
        let mut mgr = LightManager::new();
        let light = LightSource {
            kind: LightKind::Point,
            world_position: Vec3::new(3.0, 4.0, 5.0),
            ..LightSource::default()
        };
        assert_eq!(mgr.register(light), Err(MissingRankingOrigin));
        assert!(MissingRankingOrigin.to_string().contains("ranking origin"));
        assert_eq!(mgr.registered_count(), 0);
    }

    #[test]
    fn tick_selects_the_nearest_eight() {
        let mut mgr = LightManager::new();
        for i in 0..12 {
            mgr.register(make_point(x(i as f32), 100.0)).unwrap();
        }
        mgr.tick(Vec3::ZERO);
        assert_eq!(mgr.active_count(), 8);
        assert!(mgr.active().all(|l| l.world_position.x <= 7.0));
    }

    #[test]
    fn tick_ignores_the_lights_reach() {
        let mut mgr = LightManager::new();
        mgr.register(make_point(x(20.0), 5.0)).unwrap();
        mgr.tick(Vec3::ZERO);
        assert_eq!(mgr.active_count(), 1);
        let mut mgr = LightManager::new();
        mgr.register(make_point(x(5.0), 5.0)).unwrap();
        mgr.tick(Vec3::ZERO);
        assert_eq!(mgr.active_count(), 1);
    }

    #[test]
    fn the_sun_keeps_slot_zero() {
        let mut mgr = LightManager::new();
        let sun = LightSource {
            kind: LightKind::Directional,
            world_forward: -Vec3::Z,
            ..LightSource::default()
        };
        mgr.set_sun(Some(sun));
        mgr.register(make_point(Vec3::ZERO, 100.0)).unwrap();
        mgr.tick(Vec3::ZERO);
        assert_eq!(mgr.active_count(), 2);
        assert_eq!(mgr.active().next(), Some(&sun));
    }

    #[test]
    fn an_unlit_light_is_left_out_until_lit() {
        let mut mgr = LightManager::new();
        let id = mgr.register(point(Vec3::ZERO, 100.0, 0, false, 0)).unwrap();
        mgr.tick(Vec3::ZERO);
        assert_eq!(mgr.active_count(), 0);
        mgr.get_mut(id).unwrap().is_lit = true;
        mgr.tick(Vec3::ZERO);
        assert_eq!(mgr.active_count(), 1);
    }

    #[test]
    fn unregister_by_owner_removes_the_owners_lights() {
        let mut mgr = LightManager::new();
        mgr.register(point(Vec3::ZERO, 5.0, 42, true, 0)).unwrap();
        mgr.register(point(x(1.0), 5.0, 42, true, 0)).unwrap();
        mgr.register(point(x(2.0), 5.0, 99, true, 0)).unwrap();
        mgr.unregister_by_owner(42);
        assert_eq!(mgr.registered_count(), 1);
    }

    #[test]
    fn dist_sq_is_updated_each_tick() {
        let mut mgr = LightManager::new();
        let id = mgr
            .register(make_point(Vec3::new(3.0, 0.0, 4.0), 10.0))
            .unwrap();
        mgr.tick(Vec3::ZERO);
        assert_eq!(mgr.get(id).unwrap().dist_sq, 25.0);
        mgr.tick(x(3.0));
        assert_eq!(mgr.get(id).unwrap().dist_sq, 16.0);
    }

    #[test]
    fn the_snapshot_leaves_out_directional_and_unlit_lights() {
        let mut mgr = LightManager::new();
        mgr.register(make_point(x(1.0), 5.0)).unwrap();
        mgr.register(point(x(2.0), 5.0, 0, false, 0)).unwrap();
        mgr.register(LightSource {
            kind: LightKind::Directional,
            ..LightSource::default()
        })
        .unwrap();
        mgr.build_point_light_snapshot(Vec3::ZERO);
        let lights: Vec<_> = mgr.point_snapshot_lights().collect();
        assert_eq!(lights.len(), 1);
        assert_eq!(lights[0].world_position.x, 1.0);
    }

    #[test]
    fn the_snapshot_sorts_by_root_distance() {
        let mut mgr = LightManager::new();
        mgr.register(make_point(x(100.0), 5.0)).unwrap();
        mgr.register(make_point(x(1.0), 5.0)).unwrap();
        mgr.build_point_light_snapshot(Vec3::ZERO);
        let xs: Vec<f32> = mgr
            .point_snapshot_lights()
            .map(|l| l.world_position.x)
            .collect();
        assert_eq!(xs, [1.0, 100.0]);
    }

    #[test]
    fn the_snapshot_ranks_by_root_not_by_the_offset_position() {
        let mut mgr = LightManager::new();
        let mut near_root_far_final = point(x(100.0), 20.0, 1, true, 0);
        near_root_far_final.ranking_origin = Some(x(1.0));
        let mut far_root_near_final = point(x(2.0), 20.0, 2, true, 0);
        far_root_near_final.ranking_origin = Some(x(50.0));
        let far = mgr.register(far_root_near_final).unwrap();
        let near = mgr.register(near_root_far_final).unwrap();
        mgr.build_point_light_snapshot(Vec3::ZERO);
        assert_eq!(snapshot(&mgr), [near, far]);
        assert_eq!(mgr.get(near).unwrap().dist_sq, 1.0);
    }

    #[test]
    fn strict_forward_insertion_keeps_equal_and_nan_order() {
        let mut mgr = LightManager::new();
        let mut nan = point(Vec3::ZERO, 20.0, 1, true, 0);
        nan.ranking_origin = Some(Vec3::new(f32::NAN, 0.0, 0.0));
        let mut equal_a = point(x(10.0), 20.0, 2, true, 0);
        equal_a.ranking_origin = Some(x(2.0));
        let mut equal_b = point(x(20.0), 20.0, 3, true, 0);
        equal_b.ranking_origin = Some(x(-2.0));
        let ids = [
            mgr.register(nan).unwrap(),
            mgr.register(equal_a).unwrap(),
            mgr.register(equal_b).unwrap(),
        ];
        mgr.build_point_light_snapshot(Vec3::ZERO);
        assert_eq!(snapshot(&mgr), ids);
    }

    #[test]
    fn strict_forward_insertion_advances_past_nan() {
        let mut mgr = LightManager::new();
        let far = mgr.register(point(x(10.0), 20.0, 1, true, 0)).unwrap();
        let mut nan_light = point(Vec3::ZERO, 20.0, 2, true, 0);
        nan_light.ranking_origin = Some(Vec3::new(f32::NAN, 0.0, 0.0));
        let nan = mgr.register(nan_light).unwrap();
        let near = mgr.register(point(Vec3::ONE, 20.0, 3, true, 0)).unwrap();
        mgr.build_point_light_snapshot(Vec3::ZERO);
        assert_eq!(snapshot(&mgr), [near, far, nan]);
        assert_eq!(mgr.get(near).unwrap().dist_sq, 3.0);
        assert_eq!(mgr.get(far).unwrap().dist_sq, 100.0);
        assert!(mgr.get(nan).unwrap().dist_sq.is_nan());
    }

    #[test]
    fn a_spot_ranks_zero_and_a_point_by_root_distance() {
        let mut mgr = LightManager::new();
        let p = mgr.register(point(x(1.0), 20.0, 1, true, 0)).unwrap();
        let spot = mgr
            .register(LightSource {
                kind: LightKind::Spot,
                ..point(x(100.0), 20.0, 2, true, 0)
            })
            .unwrap();
        mgr.build_point_light_snapshot(Vec3::ZERO);
        assert_eq!(snapshot(&mgr), [spot, p]);
        assert_eq!(mgr.get(spot).unwrap().dist_sq, 0.0);
    }

    #[test]
    fn the_seven_and_forty_lists_do_not_evict_each_other() {
        let mut mgr = LightManager::new();
        let dynamics: Vec<LightId> = (0..10)
            .map(|i| {
                let mut l = make_dynamic(x(100.0 + i as f32), 10.0);
                l.owner_id = 100 + i;
                mgr.register(l).unwrap()
            })
            .collect();
        let statics: Vec<LightId> = (0..50)
            .map(|i| {
                mgr.register(point(x(i as f32), 10.0, 200 + i, true, 0))
                    .unwrap()
            })
            .collect();
        mgr.build_point_light_snapshot(Vec3::ZERO);
        let snap = snapshot(&mgr);
        assert_eq!(snap.len(), MAX_GLOBAL_LIGHTS);
        assert_eq!(snap[..7], dynamics[..7]);
        assert_eq!(snap[7..], statics[..40]);
    }

    #[test]
    fn the_viewer_light_wins_an_equal_root_tie_though_registered_last() {
        let mut mgr = LightManager::new();
        let mut ordinary = make_dynamic(Vec3::new(0.0, 0.0, 2.0), 15.0);
        ordinary.ranking_origin = Some(Vec3::ZERO);
        let ordinary = mgr.register(ordinary).unwrap();
        mgr.update_viewer_light(Vec3::ZERO);
        mgr.build_point_light_snapshot(Vec3::ZERO);
        let snap = snapshot(&mgr);
        assert_eq!(snap.len(), 2);
        assert_ne!(snap[0], ordinary);
        let viewer = mgr.get(snap[0]).unwrap();
        assert_eq!(viewer.world_position, Vec3::new(0.0, 0.0, 2.0));
        assert_eq!(viewer.ranking_point(), Vec3::ZERO);
        assert_eq!(viewer.owner_id, VIEWER_LIGHT_OWNER_ID);
        assert_eq!(viewer.range, 15.0);
        assert_eq!(snap[1], ordinary);
    }

    #[test]
    fn clear_empties_the_snapshot() {
        let mut mgr = LightManager::new();
        mgr.register(make_point(Vec3::ONE, 5.0)).unwrap();
        mgr.build_point_light_snapshot(Vec3::ZERO);
        assert!(!mgr.point_snapshot().is_empty());
        mgr.clear();
        assert!(mgr.point_snapshot().is_empty());
        mgr.build_point_light_snapshot(Vec3::ZERO);
        assert!(mgr.point_snapshot().is_empty());
    }

    #[test]
    fn removing_a_light_drops_it_from_the_snapshot() {
        let mut mgr = LightManager::new();
        let a = mgr.register(make_point(Vec3::ONE, 5.0)).unwrap();
        mgr.build_point_light_snapshot(Vec3::ZERO);
        mgr.unregister(a);
        assert_eq!(mgr.point_snapshot_lights().len(), 0);
    }

    #[test]
    fn the_cell_tag_does_not_filter_the_snapshot() {
        let mut mgr = LightManager::new();
        mgr.register(point(x(1.0), 5.0, 0, true, 0xAAAA_0101))
            .unwrap();
        mgr.register(point(x(2.0), 5.0, 0, true, 0xAAAA_0102))
            .unwrap();
        mgr.register(point(x(3.0), 5.0, 0, true, 0)).unwrap();
        mgr.build_point_light_snapshot(Vec3::ZERO);
        assert_eq!(mgr.point_snapshot().len(), 3);
    }

    #[test]
    fn over_the_cap_nearer_statics_never_evict_dynamics() {
        let mut mgr = LightManager::new();
        for i in 0..MAX_GLOBAL_LIGHTS + 20 {
            mgr.register(point(x(i as f32 * 0.01), 5.0, i as u32 + 1, true, 0))
                .unwrap();
        }
        let dyns: Vec<LightId> = (0..7)
            .map(|i| mgr.register(make_dynamic(x(50.0 + i as f32), 9.0)).unwrap())
            .collect();
        mgr.build_point_light_snapshot(Vec3::ZERO);
        assert_eq!(mgr.point_snapshot().len(), MAX_GLOBAL_LIGHTS);
        for d in dyns {
            assert!(mgr.point_snapshot().contains(&d));
        }
    }

    #[test]
    fn over_the_cap_the_nearest_to_the_player_stay() {
        let mut mgr = LightManager::new();
        for i in 0..MAX_GLOBAL_LIGHTS + 50 {
            mgr.register(point(
                x(200.0 + i as f32 * 0.05),
                5.0,
                i as u32 + 1,
                true,
                0,
            ))
            .unwrap();
        }
        let torch = mgr.register(point(x(2.0), 15.0, 0xF00D, true, 0)).unwrap();
        mgr.build_point_light_snapshot(Vec3::ZERO);
        assert!(mgr.point_snapshot().contains(&torch));
    }

    /// Rank every lit point or spot light, then insert stably into the
    /// dynamic and static lists: the test's independent oracle.
    fn full_sort_oracle(mgr: &LightManager, player: Vec3) -> Vec<LightId> {
        let mut dynamics = Vec::new();
        let mut statics = Vec::new();
        for (id, light) in mgr.iter() {
            if !light.is_lit || light.kind == LightKind::Directional {
                continue;
            }
            let rank = if light.kind == LightKind::Point {
                light.ranking_point().distance_squared(player)
            } else {
                0.0
            };
            if light.is_dynamic {
                dynamics.push((id, rank));
            } else {
                statics.push((id, rank));
            }
        }
        fn stable(values: &[(LightId, f32)], cap: usize) -> Vec<LightId> {
            let mut selected: Vec<(LightId, f32)> = Vec::new();
            for &value in values {
                let mut at = 0;
                while at < selected.len() {
                    if value.1 < selected[at].1 {
                        break;
                    }
                    at += 1;
                }
                if at >= cap {
                    continue;
                }
                selected.insert(at, value);
                selected.truncate(cap);
            }
            selected.into_iter().map(|v| v.0).collect()
        }
        let mut out = stable(&dynamics, MAX_DYNAMIC_POINT_LIGHTS);
        out.extend(stable(&statics, MAX_STATIC_POINT_LIGHTS));
        out
    }

    #[test]
    fn over_the_cap_equal_distances_keep_registration_order() {
        let mut mgr = LightManager::new();
        for i in 0..MAX_GLOBAL_LIGHTS + 9 {
            mgr.register(point(Vec3::new(3.0, 4.0, 0.0), 10.0, i as u32 + 1, true, 0))
                .unwrap();
        }
        let expected = full_sort_oracle(&mgr, Vec3::ZERO);
        mgr.build_point_light_snapshot(Vec3::ZERO);
        assert_eq!(snapshot(&mgr), expected);
        mgr.build_point_light_snapshot(Vec3::ZERO);
        assert_eq!(snapshot(&mgr), expected);
    }

    #[test]
    fn the_bounded_selector_matches_a_complete_sort_on_random_scenes() {
        let mut random = DotNetRandom::new(0x0054_D1B0);
        let mut next = |lo: i32, hi: i32| lo + (random.next_double() * f64::from(hi - lo)) as i32;
        for _ in 0..64 {
            let mut mgr = LightManager::new();
            let count = 180 + next(0, 420);
            for index in 0..count {
                let pos = Vec3::new(
                    next(-12, 13) as f32,
                    next(-12, 13) as f32,
                    next(-3, 4) as f32,
                );
                let lit = next(0, 13) != 0;
                let cell = if next(0, 5) == 0 {
                    0
                } else {
                    0xAAAA_0100 + next(1, 4) as u32
                };
                let mut light = point(pos, 20.0, index as u32 + 1, lit, cell);
                light.is_dynamic = next(0, 7) == 0;
                if next(0, 17) == 0 {
                    light.kind = LightKind::Directional;
                }
                mgr.register(light).unwrap();
            }
            let player = Vec3::new(next(-4, 5) as f32, next(-4, 5) as f32, next(-2, 3) as f32);
            let expected = full_sort_oracle(&mgr, player);
            mgr.build_point_light_snapshot(player);
            assert_eq!(snapshot(&mgr), expected);
        }
    }

    #[test]
    fn town_network_scale_463_matches_a_complete_sort() {
        let mut mgr = LightManager::new();
        for index in 0..463u32 {
            let i = index as f32;
            let mut light = point(
                Vec3::new(i + 0.125, i * 0.001, i * 0.0001),
                15.0,
                index + 1,
                true,
                if index % 3 == 0 {
                    0x0007_0144
                } else {
                    0x0007_0145
                },
            );
            light.is_dynamic = index % 61 == 0;
            mgr.register(light).unwrap();
        }
        let player = Vec3::new(4.25, -1.5, 0.7);
        let expected = full_sort_oracle(&mgr, player);
        mgr.build_point_light_snapshot(player);
        assert_eq!(mgr.point_snapshot().len(), MAX_GLOBAL_LIGHTS);
        assert_eq!(snapshot(&mgr), expected);
    }

    #[test]
    fn select_for_object_basics() {
        let mut idx = [0usize; 8];
        assert_eq!(select_for_object([], Vec3::ZERO, 1.0, &mut idx), 0);

        let near = [make_point(x(3.0), 5.0)];
        assert_eq!(select_for_object(&near, Vec3::ZERO, 0.0, &mut idx), 1);
        assert_eq!(idx[0], 0);

        let far = [make_point(x(10.0), 5.0)];
        assert_eq!(select_for_object(&far, Vec3::ZERO, 0.0, &mut idx), 0);

        let edge = [make_point(x(7.0), 5.0)];
        assert_eq!(select_for_object(&edge, Vec3::ZERO, 0.0, &mut idx), 0);
        assert_eq!(
            select_for_object(&edge, Vec3::ZERO, 3.0, &mut idx),
            1,
            "radius extends reach"
        );
    }

    #[test]
    fn select_for_object_keeps_the_nearest_eight() {
        let snap: Vec<LightSource> = (0..10)
            .map(|i| make_point(x(i as f32 + 1.0), 100.0))
            .collect();
        let mut idx = [0usize; 8];
        assert_eq!(select_for_object(&snap, Vec3::ZERO, 0.0, &mut idx), 8);
        assert_eq!(idx, [0, 1, 2, 3, 4, 5, 6, 7]);
    }

    #[test]
    fn select_for_object_depends_only_on_the_object() {
        let snap = [make_point(x(2.0), 10.0), make_point(x(20.0), 10.0)];
        let (mut a, mut b) = ([0usize; 8], [0usize; 8]);
        let na = select_for_object(&snap, Vec3::ZERO, 1.0, &mut a);
        let nb = select_for_object(&snap, Vec3::ZERO, 1.0, &mut b);
        assert_eq!((na, nb), (1, 1));
        assert_eq!(a[0], b[0]);
    }

    #[test]
    fn select_for_cell_takes_the_whole_snapshot() {
        let mut sel = [0usize; MAX_LIGHTS_PER_ENV_CELL];
        assert_eq!(select_for_cell(4, &mut sel), 4);
        assert_eq!(sel[..4], [0, 1, 2, 3]);

        let snap: Vec<LightSource> = (0..MAX_DYNAMIC_POINT_LIGHTS)
            .map(|i| make_dynamic(x(i as f32), 100.0))
            .chain((0..MAX_STATIC_POINT_LIGHTS).map(|i| make_point(x(i as f32 + 10.0), 100.0)))
            .collect();
        assert_eq!(select_for_cell(snap.len(), &mut sel), 47);
        assert!(sel.iter().enumerate().all(|(i, &v)| i == v));
        let mut obj = [0usize; MAX_LIGHTS_PER_ENV_CELL];
        assert_eq!(select_for_object(&snap, Vec3::ZERO, 100.0, &mut obj), 8);
    }

    #[test]
    fn a_hub_sized_scene_keeps_the_torch_beside_the_player() {
        let mut mgr = LightManager::new();
        for i in 0..400u32 {
            mgr.register(point(
                x(200.0 + i as f32 * 0.05),
                5.0,
                i + 1,
                true,
                0xAAAA_0102,
            ))
            .unwrap();
        }
        let torch = mgr
            .register(point(x(2.0), 15.0, 0xF00D_F00D, true, 0xAAAA_0101))
            .unwrap();
        let mut sel = [0usize; MAX_LIGHTS_PER_OBJECT];
        for _ in 0..2 {
            mgr.build_point_light_snapshot(Vec3::ZERO);
            let snap: Vec<&LightSource> = mgr.point_snapshot_lights().collect();
            let n = select_for_object(snap.iter().copied(), Vec3::ZERO, 6.0, &mut sel);
            assert!(sel[..n].iter().any(|&i| mgr.point_snapshot()[i] == torch));
        }
        assert_eq!(mgr.point_snapshot().len(), MAX_STATIC_POINT_LIGHTS);
    }

    #[test]
    fn sun_and_ambient_follow_the_sky_outdoors_and_go_flat_indoors() {
        let sky = crate::sky::SkyStateProvider::default_sky().interpolate(0.5);
        let mut mgr = LightManager::new();
        mgr.update_sun_from_sky(&sky, false);
        let sun = *mgr.sun().unwrap();
        assert_eq!(sun.kind, LightKind::Directional);
        assert_eq!(sun.color_linear, sky.sun_color());
        assert_eq!(sun.intensity, 1.0);
        assert_eq!(sun.world_forward, -sun_direction(&sky));
        assert_eq!(mgr.current_ambient().ambient_color, sky.ambient_color());

        mgr.update_sun_from_sky(&sky, true);
        let sun = *mgr.sun().unwrap();
        assert_eq!((sun.color_linear, sun.intensity), (Vec3::ZERO, 0.0));
        assert_eq!(mgr.current_ambient().ambient_color, Vec3::splat(0.2));
        assert_eq!(mgr.current_ambient().sun_color, Vec3::ZERO);
    }
}
