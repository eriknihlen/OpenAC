//! In-memory motion tables and animations for the movement tests (the
//! shapes OpenAC's tests build).
#![allow(dead_code)] // a menu of builders; each suite uses some of them

use std::collections::HashMap;
use std::sync::Arc;

use ac_dat::objects::{
    AnimData, Animation, AnimationFlags, AnimationFrame, MotionData, MotionDataFlags, MotionTable,
};
use ac_dat::types::{Frame, Quaternion, Vector3};
use ac_motion::{AnimationLoader, AnimationSequencer, MotionTableRules};
use glam::Vec3;

/// Animations by id.
#[derive(Debug, Default, Clone)]
pub struct MapLoader {
    map: HashMap<u32, Arc<Animation>>,
}

impl MapLoader {
    /// Register `anim` under `id`.
    pub fn add(&mut self, id: u32, mut anim: Animation) {
        anim.id = id;
        self.map.insert(id, Arc::new(anim));
    }
}

impl AnimationLoader for MapLoader {
    fn load_animation(&self, id: u32) -> Option<Arc<Animation>> {
        self.map.get(&id).cloned()
    }
}

/// A loader that knows no animations.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullLoader;

impl AnimationLoader for NullLoader {
    fn load_animation(&self, _id: u32) -> Option<Arc<Animation>> {
        None
    }
}

fn identity_frame() -> Frame {
    Frame {
        origin: Vector3::default(),
        orientation: Quaternion {
            w: 1.0,
            x: 0.0,
            y: 0.0,
            z: 0.0,
        },
    }
}

/// An animation of `frames` keyframes with `parts` parts at rest.
pub fn rest_anim(frames: usize, parts: usize) -> Animation {
    Animation {
        id: 0,
        flags: AnimationFlags(0),
        num_parts: parts as u32,
        pos_frames: Vec::new(),
        part_frames: (0..frames)
            .map(|_| AnimationFrame {
                frames: vec![identity_frame(); parts],
                hooks: Vec::new(),
            })
            .collect(),
    }
}

/// [`rest_anim`] whose every keyframe moves the whole object by `step`.
pub fn moving_anim(frames: usize, parts: usize, step: Vec3) -> Animation {
    let mut anim = rest_anim(frames, parts);
    anim.flags = AnimationFlags(AnimationFlags::POS_FRAMES);
    anim.pos_frames = (0..frames)
        .map(|_| Frame {
            origin: dat_vec(step),
            orientation: identity_frame().orientation,
        })
        .collect();
    anim
}

/// A dat vector.
pub fn dat_vec(v: Vec3) -> Vector3 {
    Vector3 {
        x: v.x,
        y: v.y,
        z: v.z,
    }
}

/// One whole-animation span at `framerate`.
pub fn motion_data(anim_id: u32, framerate: f32) -> MotionData {
    MotionData {
        bitfield: 0,
        flags: MotionDataFlags(0),
        anims: vec![AnimData {
            anim_id,
            low_frame: 0,
            high_frame: -1,
            framerate,
        }],
        velocity: Vector3::default(),
        omega: Vector3::default(),
    }
}

/// With a velocity.
pub fn with_velocity(mut md: MotionData, v: Vec3) -> MotionData {
    md.velocity = dat_vec(v);
    md.flags.0 |= MotionDataFlags::HAS_VELOCITY;
    md
}

/// With a spin.
pub fn with_omega(mut md: MotionData, w: Vec3) -> MotionData {
    md.omega = dat_vec(w);
    md.flags.0 |= MotionDataFlags::HAS_OMEGA;
    md
}

/// The key of a cycle or style-specific modifier.
pub fn cycle_key(style: u32, substate: u32) -> u32 {
    (style << 16) | (substate & 0x00FF_FFFF)
}

/// Builds a dat motion table (a repeated key replaces the earlier entry).
#[derive(Debug, Clone)]
pub struct TableBuilder {
    table: MotionTable,
}

fn upsert<V>(list: &mut Vec<(u32, V)>, key: u32, value: V) {
    if let Some(entry) = list.iter_mut().find(|(k, _)| *k == key) {
        entry.1 = value;
    } else {
        list.push((key, value));
    }
}

impl TableBuilder {
    /// A table whose default stance is `default_style`.
    pub fn new(default_style: u32) -> Self {
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

    /// The stance's default substate.
    pub fn style_default(mut self, style: u32, substate: u32) -> Self {
        upsert(&mut self.table.style_defaults, style, substate);
        self
    }

    /// A cycle.
    pub fn cycle(mut self, style: u32, substate: u32, md: MotionData) -> Self {
        upsert(&mut self.table.cycles, cycle_key(style, substate), md);
        self
    }

    /// A style-specific modifier.
    pub fn modifier(mut self, style: u32, motion: u32, md: MotionData) -> Self {
        upsert(&mut self.table.modifiers, cycle_key(style, motion), md);
        self
    }

    /// A style-independent modifier.
    pub fn global_modifier(mut self, motion: u32, md: MotionData) -> Self {
        upsert(&mut self.table.modifiers, motion & 0x00FF_FFFF, md);
        self
    }

    /// A link from `from` to `to` in `style`.
    pub fn link(mut self, style: u32, from: u32, to: u32, md: MotionData) -> Self {
        let outer = cycle_key(style, from);
        if let Some((_, targets)) = self.table.links.iter_mut().find(|(k, _)| *k == outer) {
            upsert(targets, to, md);
        } else {
            self.table.links.push((outer, vec![(to, md)]));
        }
        self
    }

    /// The table's rules.
    pub fn rules(self) -> Arc<MotionTableRules> {
        Arc::new(MotionTableRules::new(Arc::new(self.table)))
    }
}

/// A one-part sequencer for `rules` and `loader`.
pub fn sequencer(rules: Arc<MotionTableRules>, loader: MapLoader) -> AnimationSequencer {
    AnimationSequencer::new(1, 0, rules, Arc::new(loader))
}
