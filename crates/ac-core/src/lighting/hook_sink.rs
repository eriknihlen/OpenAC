//! Lights grouped by owner: switching an owner's lights on and off (the
//! animation "set light" hook) and moving lights that follow a moving
//! owner.

use std::collections::BTreeMap;

use glam::{Mat4, Vec3};

use super::manager::{LightId, LightManager, MissingRankingOrigin};
use super::source::LightSource;

/// Where a light's owner is this frame.
pub trait EntityPoseSource {
    /// The owner's root frame in world space (column-vector convention:
    /// `world = root * local`), if the owner is known.
    fn root_pose(&self, owner_id: u32) -> Option<Mat4>;

    /// The cell the owner is in, if known.
    fn cell_id(&self, _owner_id: u32) -> Option<u32> {
        None
    }
}

/// Owner bookkeeping for the lights in a [`LightManager`].
#[derive(Debug, Clone, Default)]
pub struct LightingHookSink {
    by_owner: BTreeMap<u32, Vec<LightId>>,
    tracked_by_owner: BTreeMap<u32, Vec<LightId>>,
    enabled_by_owner: BTreeMap<u32, bool>,
}

impl LightingHookSink {
    /// No owners.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Owners with registered lights.
    #[must_use]
    pub fn owned_light_owner_count(&self) -> usize {
        self.by_owner.len()
    }

    /// Owners with lights that follow their pose.
    #[must_use]
    pub fn pose_tracked_owner_count(&self) -> usize {
        self.tracked_by_owner.len()
    }

    /// Owners whose on/off state is remembered.
    #[must_use]
    pub fn retained_owner_state_count(&self) -> usize {
        self.enabled_by_owner.len()
    }

    /// Register a light under its owner. A remembered on/off state for the
    /// owner applies to it.
    ///
    /// # Errors
    /// [`MissingRankingOrigin`] for a point or spot light without one.
    pub fn register_owned_light(
        &mut self,
        lights: &mut LightManager,
        mut light: LightSource,
    ) -> Result<LightId, MissingRankingOrigin> {
        if let Some(&enabled) = self.enabled_by_owner.get(&light.owner_id) {
            light.is_lit = enabled;
        }
        let owner_id = light.owner_id;
        let tracks = light.tracks_owner_pose;
        let id = lights.register(light)?;
        self.by_owner.entry(owner_id).or_default().push(id);
        if tracks {
            self.tracked_by_owner.entry(owner_id).or_default().push(id);
        }
        Ok(id)
    }

    /// Drop every light of an owner (despawn, unload). With `forget_state`
    /// false the owner's on/off state is kept for when it comes back.
    pub fn unregister_owner(
        &mut self,
        lights: &mut LightManager,
        owner_id: u32,
        forget_state: bool,
    ) {
        if let Some(list) = self.by_owner.remove(&owner_id) {
            for id in list {
                lights.unregister(id);
            }
        }
        self.tracked_by_owner.remove(&owner_id);
        if forget_state {
            self.enabled_by_owner.remove(&owner_id);
        }
    }

    /// Remember an owner's initial on/off state unless one is known.
    pub fn initialize_owner_lighting(&mut self, owner_id: u32, enabled: bool) {
        self.enabled_by_owner.entry(owner_id).or_insert(enabled);
    }

    /// Whether an owner's lights are on (on unless switched off).
    #[must_use]
    pub fn is_owner_lighting_enabled(&self, owner_id: u32) -> bool {
        self.enabled_by_owner
            .get(&owner_id)
            .copied()
            .unwrap_or(true)
    }

    /// Switch an owner's lights on or off (the animation "set light"
    /// hook). Returns true when the state changed, so the caller can tell
    /// whoever listens.
    pub fn set_owner_lighting(
        &mut self,
        lights: &mut LightManager,
        owner_id: u32,
        enabled: bool,
    ) -> bool {
        if self.enabled_by_owner.get(&owner_id) == Some(&enabled) {
            return false;
        }
        self.enabled_by_owner.insert(owner_id, enabled);
        if let Some(list) = self.by_owner.get(&owner_id) {
            for &id in list {
                if let Some(light) = lights.get_mut(id) {
                    light.is_lit = enabled;
                }
            }
        }
        true
    }

    /// An owner's registered lights.
    #[must_use]
    pub fn owned_lights(&self, owner_id: u32) -> Option<&[LightId]> {
        self.by_owner.get(&owner_id).map(Vec::as_slice)
    }

    /// Recompute the world frame of every light that follows its owner:
    /// owner root times the light's local frame; the ranking point becomes
    /// the root position and the cell the owner's cell (0 when unknown).
    /// Owners without a pose are left alone.
    pub fn refresh_attached_lights(&self, lights: &mut LightManager, poses: &dyn EntityPoseSource) {
        for (&owner_id, ids) in &self.tracked_by_owner {
            let Some(root_world) = poses.root_pose(owner_id) else {
                continue;
            };
            let cell_id = poses.cell_id(owner_id).unwrap_or(0);
            for &id in ids {
                let Some(light) = lights.get_mut(id) else {
                    continue;
                };
                let light_world = root_world * light.local_pose;
                light.ranking_origin = Some(root_world.w_axis.truncate());
                light.world_position = light_world.w_axis.truncate();
                let forward = light_world.transform_vector3(Vec3::Y);
                if forward.length_squared() > 1e-8 {
                    light.world_forward = forward.normalize();
                }
                light.cell_id = cell_id;
            }
        }
    }
}

#[cfg(test)]
mod tests {
    //! OpenAC `LightingHookSinkTests`.
    use std::cell::Cell;
    use std::collections::HashMap;

    use super::*;
    use crate::lighting::LightKind;

    #[derive(Default)]
    struct Poses {
        poses: HashMap<u32, (Mat4, u32)>,
        root_queries: Cell<u32>,
    }

    impl EntityPoseSource for Poses {
        fn root_pose(&self, owner_id: u32) -> Option<Mat4> {
            self.root_queries.set(self.root_queries.get() + 1);
            self.poses.get(&owner_id).map(|p| p.0)
        }
        fn cell_id(&self, owner_id: u32) -> Option<u32> {
            self.poses.get(&owner_id).map(|p| p.1)
        }
    }

    fn owned(owner_id: u32) -> LightSource {
        LightSource {
            kind: LightKind::Point,
            owner_id,
            is_lit: true,
            ..LightSource::default()
        }
        .with_ranking_origin(Vec3::ZERO)
    }

    #[test]
    fn set_light_flips_only_the_owners_lights() {
        let mut mgr = LightManager::new();
        let mut sink = LightingHookSink::new();
        let a = sink.register_owned_light(&mut mgr, owned(42)).unwrap();
        let b = sink.register_owned_light(&mut mgr, owned(42)).unwrap();
        let other = sink.register_owned_light(&mut mgr, owned(99)).unwrap();

        assert!(sink.set_owner_lighting(&mut mgr, 42, false));

        assert!(!mgr.get(a).unwrap().is_lit);
        assert!(!mgr.get(b).unwrap().is_lit);
        assert!(mgr.get(other).unwrap().is_lit);
        assert!(
            !sink.set_owner_lighting(&mut mgr, 42, false),
            "no change, no notice"
        );
    }

    #[test]
    fn unregister_owner_removes_all_owned_lights() {
        let mut mgr = LightManager::new();
        let mut sink = LightingHookSink::new();
        sink.register_owned_light(&mut mgr, owned(7)).unwrap();
        sink.register_owned_light(&mut mgr, owned(7)).unwrap();
        assert_eq!(mgr.registered_count(), 2);
        sink.unregister_owner(&mut mgr, 7, true);
        assert_eq!(mgr.registered_count(), 0);
    }

    #[test]
    fn refresh_composes_the_local_frame_with_the_current_root_and_cell() {
        let mut mgr = LightManager::new();
        let mut poses = Poses::default();
        let root = Mat4::from_translation(Vec3::new(10.0, 20.0, 30.0))
            * Mat4::from_rotation_z(std::f32::consts::FRAC_PI_2);
        poses.poses.insert(42, (root, 0x0101_0002));
        let mut sink = LightingHookSink::new();
        let id = sink
            .register_owned_light(
                &mut mgr,
                LightSource {
                    owner_id: 42,
                    local_pose: Mat4::from_translation(Vec3::new(1.0, 0.0, 2.0)),
                    tracks_owner_pose: true,
                    ..LightSource::default()
                }
                .with_ranking_origin(Vec3::ZERO),
            )
            .unwrap();

        sink.refresh_attached_lights(&mut mgr, &poses);

        let light = mgr.get(id).unwrap();
        assert!((light.world_position - Vec3::new(10.0, 21.0, 32.0)).length() < 0.01);
        assert_eq!(light.ranking_point(), Vec3::new(10.0, 20.0, 30.0));
        assert_eq!(light.cell_id, 0x0101_0002);
    }

    #[test]
    fn refresh_leaves_a_dat_static_light_alone() {
        let mut mgr = LightManager::new();
        let mut poses = Poses::default();
        poses
            .poses
            .insert(42, (Mat4::from_translation(Vec3::new(100.0, 0.0, 0.0)), 0));
        let mut sink = LightingHookSink::new();
        let id = sink
            .register_owned_light(
                &mut mgr,
                LightSource {
                    owner_id: 42,
                    world_position: Vec3::new(7.0, 8.0, 9.0),
                    local_pose: Mat4::from_translation(Vec3::X),
                    tracks_owner_pose: false,
                    ..LightSource::default()
                }
                .with_ranking_origin(Vec3::new(7.0, 8.0, 9.0)),
            )
            .unwrap();

        sink.refresh_attached_lights(&mut mgr, &poses);

        assert_eq!(
            mgr.get(id).unwrap().world_position,
            Vec3::new(7.0, 8.0, 9.0)
        );
        assert_eq!(poses.root_queries.get(), 0);
    }

    #[test]
    fn unregistering_a_presentation_keeps_the_set_light_state() {
        let mut mgr = LightManager::new();
        let mut sink = LightingHookSink::new();
        sink.initialize_owner_lighting(7, true);
        sink.set_owner_lighting(&mut mgr, 7, false);
        sink.unregister_owner(&mut mgr, 7, false);

        let id = sink.register_owned_light(&mut mgr, owned(7)).unwrap();

        assert!(!mgr.get(id).unwrap().is_lit);
    }
}
