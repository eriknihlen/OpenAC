//! In-memory dat fixtures for the unit tests: animations, motion data and
//! motion tables built in code, and a map-backed animation loader.

use std::collections::HashMap;
use std::sync::Arc;

use ac_dat::objects::{
    AnimData, Animation, AnimationFlags, AnimationFrame, AnimationHook, HookData, MotionData,
    MotionDataFlags, MotionTable,
};
use ac_dat::types::{Quaternion, Vector3};
use glam::{Quat, Vec3};

use crate::anim_node::AnimationLoader;
use crate::frame::Frame;

pub(crate) fn dat_vec(v: Vec3) -> Vector3 {
    Vector3 {
        x: v.x,
        y: v.y,
        z: v.z,
    }
}

pub(crate) fn dat_frame(f: Frame) -> ac_dat::types::Frame {
    ac_dat::types::Frame {
        origin: dat_vec(f.origin),
        orientation: Quaternion {
            w: f.orientation.w,
            x: f.orientation.x,
            y: f.orientation.y,
            z: f.orientation.z,
        },
    }
}

/// An animation of `frames` keyframes with `parts` parts each, placed by
/// `part(frame, part)`. No hooks, no whole-object frames.
pub(crate) fn make_anim(
    frames: usize,
    parts: usize,
    part: impl Fn(usize, usize) -> Frame,
) -> Animation {
    Animation {
        id: 0,
        flags: AnimationFlags(0),
        num_parts: parts as u32,
        pos_frames: Vec::new(),
        part_frames: (0..frames)
            .map(|f| AnimationFrame {
                frames: (0..parts).map(|p| dat_frame(part(f, p))).collect(),
                hooks: Vec::new(),
            })
            .collect(),
    }
}

/// [`make_anim`] with every part at the same placement.
pub(crate) fn uniform_anim(frames: usize, parts: usize, origin: Vec3, rot: Quat) -> Animation {
    make_anim(frames, parts, |_, _| Frame::new(origin, rot))
}

/// Give every keyframe a whole-object movement.
pub(crate) fn set_pos_frames(anim: &mut Animation, pos: impl Fn(usize) -> Frame) {
    anim.flags = AnimationFlags(AnimationFlags::POS_FRAMES);
    anim.pos_frames = (0..anim.part_frames.len())
        .map(|f| dat_frame(pos(f)))
        .collect();
}

/// A sound hook (type 1) used as a tagged marker.
pub(crate) fn sound_hook(id: u32, direction: u32) -> AnimationHook {
    AnimationHook {
        direction,
        data: HookData::Sound { id },
    }
}

/// Hook directions as stored.
pub(crate) const BOTH: u32 = 0;
pub(crate) const FORWARD: u32 = 1;
pub(crate) const BACKWARD: u32 = 0xFFFF_FFFF;

pub(crate) fn anim_data(anim_id: u32, low: i32, high: i32, framerate: f32) -> AnimData {
    AnimData {
        anim_id,
        low_frame: low,
        high_frame: high,
        framerate,
    }
}

/// A motion of one whole-animation span at `framerate`.
pub(crate) fn motion_data(anim_id: u32, framerate: f32) -> MotionData {
    MotionData {
        bitfield: 0,
        flags: MotionDataFlags(0),
        anims: vec![anim_data(anim_id, 0, -1, framerate)],
        velocity: Vector3::default(),
        omega: Vector3::default(),
    }
}

pub(crate) fn with_bitfield(mut md: MotionData, bitfield: u8) -> MotionData {
    md.bitfield = bitfield;
    md
}

pub(crate) fn with_velocity(mut md: MotionData, v: Vec3) -> MotionData {
    md.velocity = dat_vec(v);
    md.flags.0 |= MotionDataFlags::HAS_VELOCITY;
    md
}

pub(crate) fn with_omega(mut md: MotionData, w: Vec3) -> MotionData {
    md.omega = dat_vec(w);
    md.flags.0 |= MotionDataFlags::HAS_OMEGA;
    md
}

/// Animations by id; registering stamps the id into the animation.
#[derive(Debug, Default, Clone)]
pub(crate) struct MapLoader {
    map: HashMap<u32, Arc<Animation>>,
}

impl MapLoader {
    pub(crate) fn add(&mut self, id: u32, mut anim: Animation) {
        anim.id = id;
        self.map.insert(id, Arc::new(anim));
    }

    pub(crate) fn shared(self) -> Arc<Self> {
        Arc::new(self)
    }
}

impl AnimationLoader for MapLoader {
    fn load_animation(&self, id: u32) -> Option<Arc<Animation>> {
        self.map.get(&id).cloned()
    }
}

/// Builds a dat motion table; a later entry with the same key replaces the
/// earlier one, as a dictionary assignment would.
#[derive(Debug, Clone)]
pub(crate) struct TableBuilder {
    table: MotionTable,
}

pub(crate) fn cycle_key(style: u32, substate: u32) -> u32 {
    (style << 16) | (substate & 0x00FF_FFFF)
}

fn upsert<V>(list: &mut Vec<(u32, V)>, key: u32, value: V) {
    if let Some(entry) = list.iter_mut().find(|(k, _)| *k == key) {
        entry.1 = value;
    } else {
        list.push((key, value));
    }
}

impl TableBuilder {
    pub(crate) fn new(default_style: u32) -> Self {
        Self {
            table: MotionTable {
                id: 0x0900_0000,
                default_style,
                style_defaults: Vec::new(),
                cycles: Vec::new(),
                modifiers: Vec::new(),
                links: Vec::new(),
            },
        }
    }

    pub(crate) fn style_default(mut self, style: u32, substate: u32) -> Self {
        upsert(&mut self.table.style_defaults, style, substate);
        self
    }

    pub(crate) fn cycle(mut self, style: u32, substate: u32, md: MotionData) -> Self {
        upsert(&mut self.table.cycles, cycle_key(style, substate), md);
        self
    }

    /// A modifier under the style-specific key.
    pub(crate) fn modifier(mut self, style: u32, motion: u32, md: MotionData) -> Self {
        upsert(&mut self.table.modifiers, cycle_key(style, motion), md);
        self
    }

    /// A modifier under the style-independent key.
    pub(crate) fn global_modifier(mut self, motion: u32, md: MotionData) -> Self {
        upsert(&mut self.table.modifiers, motion & 0x00FF_FFFF, md);
        self
    }

    pub(crate) fn link(mut self, style: u32, from: u32, to: u32, md: MotionData) -> Self {
        let outer = cycle_key(style, from);
        if let Some((_, targets)) = self.table.links.iter_mut().find(|(k, _)| *k == outer) {
            upsert(targets, to, md);
        } else {
            self.table.links.push((outer, vec![(to, md)]));
        }
        self
    }

    pub(crate) fn build(self) -> MotionTable {
        self.table
    }
}
