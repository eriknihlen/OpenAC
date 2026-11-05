//! Sky object scripts: the day group's sky objects whose model is a Setup
//! with a default script (rain, snow, aurora, lightning) run that script on
//! a presentation-only owner that sits at the camera, turned like the sky
//! object, for as long as the object is up.

use std::collections::{BTreeSet, HashSet};

use ac_core::sky::{DayGroupData, SkyObjectData};
use glam::{Quat, Vec3};

use crate::controller::EntityEffectController;
use crate::hook_sink::ParticleHookSink;
use crate::particles::{ParticleRenderPass, ParticleSystem};
use crate::poses::{EffectPoseRegistry, root_matrix};
use crate::script::PhysicsScriptRunner;

/// What a sky update works on.
pub struct SkyTargets<'a> {
    /// The script runner.
    pub scripts: &'a mut PhysicsScriptRunner,
    /// The particle hook sink.
    pub sink: &'a mut ParticleHookSink,
    /// The particle system.
    pub particles: &'a mut ParticleSystem,
    /// Owners' poses.
    pub poses: &'a mut EffectPoseRegistry,
    /// The effect controller (registers sky owners as always advancing).
    pub effects: Option<&'a mut EntityEffectController>,
    /// Sky owners stopped (their sounds must stop too).
    pub stopped_owners: &'a mut Vec<u32>,
    /// Messages.
    pub diagnostics: &'a mut Vec<String>,
}

impl std::fmt::Debug for SkyTargets<'_> {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("SkyTargets")
            .field("scripts", &self.scripts)
            .finish_non_exhaustive()
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord)]
struct SkyPesKey {
    object_index: usize,
    gfx_obj_id: u32,
    properties: u32,
}

/// The local id of the owner that runs a sky object's script: `0xF0......`
/// with bit 27 for post-scene objects and the object's index below.
#[must_use]
pub fn sky_owner_id(object_index: usize, properties: u32) -> u32 {
    let post_scene = if properties & 0x01 != 0 {
        0x0800_0000
    } else {
        0
    };
    0xF000_0000 | post_scene | (object_index as u32 & 0x07FF_FFFF)
}

/// Starts and stops the sky objects' scripts as the day group and time of
/// day change.
#[derive(Debug, Default)]
pub struct SkyPesFrameController {
    active: BTreeSet<SkyPesKey>,
    missing: BTreeSet<SkyPesKey>,
    reported_mismatches: HashSet<u32>,
    seen: HashSet<SkyPesKey>,
}

impl SkyPesFrameController {
    /// No sky scripts running.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Sky scripts running.
    #[must_use]
    pub fn active_count(&self) -> usize {
        self.active.len()
    }

    /// Bring the running sky scripts in line with `day_group` at
    /// `day_fraction` (0..1 through the day): objects that are up and carry
    /// a script run it (once per object slot), the rest stop (particles
    /// fade). Without an active sky (indoors, portal space) everything
    /// stops. Weather objects stop while `disable_most_weather_effects`.
    pub fn update(
        &mut self,
        t: &mut SkyTargets<'_>,
        day_fraction: f32,
        day_group: Option<&DayGroupData>,
        camera_world_position: Vec3,
        sky_active: bool,
        disable_most_weather_effects: bool,
    ) {
        self.seen.clear();
        if !sky_active {
            self.stop_unseen(t, true);
            self.stop_unseen(t, false);
            return;
        }

        if let Some(group) = day_group {
            for (index, object) in group.sky_objects.iter().enumerate() {
                if self.plays(t, object, day_fraction, disable_most_weather_effects) {
                    self.seen.insert(key(index, object));
                }
            }
        }
        self.stop_unseen(t, true);
        self.stop_unseen(t, false);

        let Some(group) = day_group else {
            return;
        };
        for (index, object) in group.sky_objects.iter().enumerate() {
            if !self.plays(t, object, day_fraction, disable_most_weather_effects) {
                continue;
            }
            let script_id = object.default_script_id;
            let key = key(index, object);
            let owner = sky_owner_id(index, object.properties);
            let pass = if object.is_post_scene() {
                ParticleRenderPass::SkyPostScene
            } else {
                ParticleRenderPass::SkyPreScene
            };
            t.sink.set_entity_render_pass(t.particles, owner, pass);
            t.scripts.set_owner_anchor(owner, camera_world_position);
            let radians = object.current_angle(day_fraction).to_radians();
            let rotation = Quat::from_axis_angle(Vec3::Y, -radians);
            // One identity part: hooks on part 0 attach at the carrier.
            if let Err(error) = t.poses.publish(
                owner,
                root_matrix(camera_world_position, rotation),
                &[glam::Mat4::IDENTITY],
                0,
                None,
            ) {
                t.diagnostics.push(error.to_string());
            }

            if self.active.contains(&key) || self.missing.contains(&key) {
                continue;
            }
            if let Some(effects) = t.effects.as_deref_mut() {
                effects.register_synthetic_owner(owner);
            }
            if t.scripts.play(script_id, owner, camera_world_position) {
                self.active.insert(key);
            } else {
                self.missing.insert(key);
                if let Some(effects) = t.effects.as_deref_mut() {
                    effects.unregister_synthetic_owner(owner);
                }
                t.sink.clear_entity_render_pass(t.particles, owner);
                t.poses.remove(owner);
            }
        }
    }

    /// Whether the object runs a script now. The script is the carrier
    /// Setup's default script (the game's source); a different value in the
    /// sky object's own script column is reported once.
    fn plays(
        &mut self,
        t: &mut SkyTargets<'_>,
        object: &SkyObjectData,
        day_fraction: f32,
        disable_most_weather_effects: bool,
    ) -> bool {
        let script_id = object.default_script_id;
        if script_id != object.pes_object_id
            && object.gfx_obj_id != 0
            && self.reported_mismatches.insert(object.gfx_obj_id)
        {
            t.diagnostics.push(format!(
                "[sky-pes] carrier 0x{:08X}: Setup DefaultScript 0x{script_id:08X} != SkyObject \
                 pes_id 0x{:08X}; playing the DefaultScript (retail's source).",
                object.gfx_obj_id, object.pes_object_id
            ));
        }
        script_id != 0
            && object.is_visible(day_fraction)
            && !(object.is_weather() && disable_most_weather_effects)
    }

    fn stop_unseen(&mut self, t: &mut SkyTargets<'_>, active: bool) {
        let set = if active {
            &mut self.active
        } else {
            &mut self.missing
        };
        let stale: Vec<SkyPesKey> = set
            .iter()
            .filter(|k| !self.seen.contains(k))
            .copied()
            .collect();
        for key in stale {
            set.remove(&key);
            if active {
                let owner = sky_owner_id(key.object_index, key.properties);
                t.scripts.stop_all_for_entity(owner);
                if let Some(effects) = t.effects.as_deref_mut() {
                    effects.unregister_synthetic_owner(owner);
                }
                t.sink.stop_all_for_entity(t.particles, owner, true);
                t.poses.remove(owner);
                t.stopped_owners.push(owner);
            }
        }
    }
}

fn key(index: usize, object: &SkyObjectData) -> SkyPesKey {
    SkyPesKey {
        object_index: index,
        gfx_obj_id: object.gfx_obj_id,
        properties: object.properties,
    }
}

#[cfg(test)]
mod tests;
