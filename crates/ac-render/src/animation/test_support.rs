//! Small dat records for the animation tests.

use std::collections::HashMap;
use std::sync::Arc;

use ac_dat::objects::{
    Animation, AnimationFlags, AnimationFrame, AnimationHook, HookData, Setup, SetupFlags,
};
use ac_dat::types::{Frame, Quaternion, Sphere, Vector3};
use ac_motion::AnimationLoader;
use glam::Vec3;

/// A Setup of `parts` parts (gfx ids `0x01000001..`), nothing else.
pub(crate) fn setup(parts: usize) -> Setup {
    Setup {
        id: 0x0200_0001,
        flags: SetupFlags(0),
        parts: (0..parts as u32).map(|i| 0x0100_0001 + i).collect(),
        parent_index: Vec::new(),
        default_scale: Vec::new(),
        holding_locations: Vec::new(),
        connection_points: Vec::new(),
        placement_frames: Vec::new(),
        cyl_spheres: Vec::new(),
        spheres: Vec::new(),
        height: 0.0,
        radius: 0.0,
        step_up_height: 0.0,
        step_down_height: 0.0,
        sorting_sphere: Sphere::default(),
        selection_sphere: Sphere::default(),
        lights: Vec::new(),
        default_animation: 0,
        default_script: 0,
        default_motion_table: 0,
        default_sound_table: 0,
        default_script_table: 0,
    }
}

fn frame_at(x: f32) -> Frame {
    Frame {
        origin: Vector3 { x, y: 0.0, z: 0.0 },
        orientation: Quaternion {
            w: 1.0,
            x: 0.0,
            y: 0.0,
            z: 0.0,
        },
    }
}

/// One part, keyframe 0 at the origin and keyframe 1 two metres along x.
pub(crate) fn two_frame_animation() -> Animation {
    Animation {
        id: 0x0300_0001,
        flags: AnimationFlags(0),
        num_parts: 1,
        pos_frames: Vec::new(),
        part_frames: vec![
            AnimationFrame {
                frames: vec![frame_at(0.0)],
                hooks: Vec::new(),
            },
            AnimationFrame {
                frames: vec![frame_at(2.0)],
                hooks: Vec::new(),
            },
        ],
    }
}

/// [`two_frame_animation`] with a spin hook on keyframe 0.
pub(crate) fn omega_animation(axis: Vec3) -> Animation {
    let mut a = two_frame_animation();
    a.part_frames[0].hooks.push(AnimationHook {
        direction: 0,
        data: HookData::SetOmega {
            axis: Vector3 {
                x: axis.x,
                y: axis.y,
                z: axis.z,
            },
        },
    });
    a
}

/// Animations from a map.
#[derive(Debug, Default)]
pub(crate) struct MapLoader(HashMap<u32, Arc<Animation>>);

impl MapLoader {
    pub(crate) fn add(&mut self, id: u32, animation: Animation) {
        self.0.insert(id, Arc::new(animation));
    }
}

impl AnimationLoader for MapLoader {
    fn load_animation(&self, id: u32) -> Option<Arc<Animation>> {
        self.0.get(&id).cloned()
    }
}
