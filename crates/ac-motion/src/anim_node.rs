//! One entry of an animation sequence: an animation, the span of frames to
//! play and the playback rate.

use std::sync::Arc;

use ac_dat::objects::{AnimData, Animation, AnimationFrame};

use crate::frame::Frame;

/// Resolves animations by dat id (`0x03......`).
///
/// The sequence asks for an animation each time a motion appends one, so
/// implementations should cache; `None` means "no such animation", and the
/// entry is then dropped.
pub trait AnimationLoader {
    /// The animation with dat id `id`, or `None`.
    fn load_animation(&self, id: u32) -> Option<Arc<Animation>>;
}

/// One animation in a sequence, with its frame span and rate.
///
/// Frame numbers are whole keyframes; `low_frame..=high_frame` is the span
/// played. A negative framerate plays the span backwards.
#[derive(Debug, Clone, PartialEq)]
pub struct AnimSequenceNode {
    anim: Option<Arc<Animation>>,
    /// Keyframes per second; negative plays backwards.
    pub framerate: f32,
    /// First keyframe of the span.
    pub low_frame: i32,
    /// Last keyframe of the span.
    pub high_frame: i32,
}

impl Default for AnimSequenceNode {
    fn default() -> Self {
        Self {
            anim: None,
            framerate: 30.0,
            low_frame: -1,
            high_frame: -1,
        }
    }
}

impl AnimSequenceNode {
    /// A node for `anim_data`, its animation resolved through `loader` and
    /// its span clamped to the animation.
    #[must_use]
    pub fn from_anim_data(anim_data: &AnimData, loader: &dyn AnimationLoader) -> Self {
        let mut node = Self {
            anim: None,
            framerate: anim_data.framerate,
            low_frame: anim_data.low_frame,
            high_frame: anim_data.high_frame,
        };
        node.set_animation_id(anim_data.anim_id, loader);
        node
    }

    /// The resolved animation, if any.
    #[must_use]
    pub fn anim(&self) -> Option<&Arc<Animation>> {
        self.anim.as_ref()
    }

    /// True when an animation was resolved.
    #[must_use]
    pub fn has_anim(&self) -> bool {
        self.anim.is_some()
    }

    /// Resolve `anim_id` (0 means none) and clamp the span to it: a
    /// negative high frame means "to the end"; both ends are kept inside the
    /// animation and the high frame never sits below the low frame.
    pub fn set_animation_id(&mut self, anim_id: u32, loader: &dyn AnimationLoader) {
        self.anim = if anim_id == 0 {
            None
        } else {
            loader.load_animation(anim_id)
        };
        let Some(anim) = &self.anim else {
            return;
        };
        let num_frames = i32::try_from(anim.part_frames.len()).unwrap_or(i32::MAX);
        if self.high_frame < 0 {
            self.high_frame = num_frames - 1;
        }
        if self.low_frame >= num_frames {
            self.low_frame = num_frames - 1;
        }
        if self.high_frame >= num_frames {
            self.high_frame = num_frames - 1;
        }
        if self.low_frame > self.high_frame {
            self.high_frame = self.low_frame;
        }
    }

    /// Where playback starts: the low frame going forwards, one past the
    /// high frame going backwards.
    #[must_use]
    pub fn starting_frame(&self) -> i32 {
        if self.framerate < 0.0 {
            self.high_frame + 1
        } else {
            self.low_frame
        }
    }

    /// Where playback ends: one past the high frame going forwards, the low
    /// frame going backwards.
    #[must_use]
    pub fn ending_frame(&self) -> i32 {
        if self.framerate < 0.0 {
            self.low_frame
        } else {
            self.high_frame + 1
        }
    }

    /// Scale the rate; a negative factor also swaps the span ends.
    pub fn multiply_framerate(&mut self, factor: f32) {
        if factor < 0.0 {
            std::mem::swap(&mut self.low_frame, &mut self.high_frame);
        }
        self.framerate *= factor;
    }

    /// The whole-object movement of keyframe `index`, if the animation has
    /// one there.
    #[must_use]
    pub fn pos_frame(&self, index: i32) -> Option<Frame> {
        let anim = self.anim.as_ref()?;
        let i = usize::try_from(index).ok()?;
        if i >= anim.part_frames.len() {
            return None;
        }
        anim.pos_frames.get(i).map(Frame::from_dat)
    }

    /// [`AnimSequenceNode::pos_frame`] at the keyframe a fractional frame
    /// number falls in.
    #[must_use]
    pub fn pos_frame_at(&self, frame_number: f64) -> Option<Frame> {
        self.pos_frame(frame_number.floor() as i32)
    }

    /// The part placements and hooks of keyframe `index`.
    #[must_use]
    pub fn part_frame(&self, index: i32) -> Option<&AnimationFrame> {
        let anim = self.anim.as_ref()?;
        anim.part_frames.get(usize::try_from(index).ok()?)
    }
}

#[cfg(test)]
mod tests {
    //! Golden cases from OpenAC `AnimSequenceNodeTests`.
    use super::*;
    use crate::test_support::{MapLoader, anim_data, make_anim, set_pos_frames};
    use glam::{Quat, Vec3};

    fn loader_with(frames: usize, pos_frames: bool) -> MapLoader {
        let mut anim = make_anim(frames, 1, |f, _| {
            Frame::new(Vec3::new(f as f32, 0.0, 0.0), Quat::IDENTITY)
        });
        if pos_frames {
            set_pos_frames(&mut anim, |f| {
                Frame::new(Vec3::new(0.0, f as f32, 0.0), Quat::IDENTITY)
            });
        }
        let mut loader = MapLoader::default();
        loader.add(0x0300_ABCD, anim);
        loader
    }

    #[test]
    fn default_node_has_the_retail_defaults() {
        let n = AnimSequenceNode::default();
        assert_eq!(n.framerate, 30.0);
        assert_eq!((n.low_frame, n.high_frame), (-1, -1));
        assert!(!n.has_anim());
    }

    #[test]
    fn zero_id_leaves_no_anim_and_frames_unclamped() {
        let mut n = AnimSequenceNode::default();
        n.set_animation_id(0, &loader_with(10, false));
        assert!(!n.has_anim());
        assert_eq!((n.low_frame, n.high_frame), (-1, -1));
    }

    #[test]
    fn clamp_order() {
        // (low, high, frames) -> (low, high).
        for (low, high, frames, exp_low, exp_high) in [
            (0, -1, 10, 0, 9),
            (12, -1, 10, 9, 9),
            (3, 15, 10, 3, 9),
            (15, 15, 10, 9, 9),
            (5, 2, 10, 5, 5),
            (2, 7, 10, 2, 7),
        ] {
            let mut n = AnimSequenceNode {
                low_frame: low,
                high_frame: high,
                ..AnimSequenceNode::default()
            };
            n.set_animation_id(0x0300_ABCD, &loader_with(frames, false));
            assert!(n.has_anim());
            assert_eq!(
                (n.low_frame, n.high_frame),
                (exp_low, exp_high),
                "{low} {high}"
            );
        }
    }

    #[test]
    fn boundary_pair_is_direction_aware_bare_ints() {
        for (framerate, low, high, start, end) in [
            (30.0, 2, 7, 2, 8),
            (-30.0, 2, 7, 8, 2),
            (0.0, 2, 7, 2, 8),
            (30.0, 0, 0, 0, 1),
            (-30.0, 0, 0, 1, 0),
        ] {
            let n = AnimSequenceNode {
                framerate,
                low_frame: low,
                high_frame: high,
                ..AnimSequenceNode::default()
            };
            assert_eq!((n.starting_frame(), n.ending_frame()), (start, end));
        }
    }

    #[test]
    fn multiply_framerate_swaps_on_negative_and_round_trips() {
        let mut n = AnimSequenceNode {
            framerate: 30.0,
            low_frame: 2,
            high_frame: 7,
            ..AnimSequenceNode::default()
        };
        n.multiply_framerate(2.0);
        assert_eq!((n.framerate, n.low_frame, n.high_frame), (60.0, 2, 7));
        n.multiply_framerate(0.5);
        n.multiply_framerate(-1.0);
        assert_eq!((n.framerate, n.low_frame, n.high_frame), (-30.0, 7, 2));
        assert_eq!((n.starting_frame(), n.ending_frame()), (3, 7));
        n.multiply_framerate(-1.0);
        assert_eq!((n.framerate, n.low_frame, n.high_frame), (30.0, 2, 7));
    }

    #[test]
    fn pos_frame_bounds() {
        assert!(AnimSequenceNode::default().pos_frame(0).is_none());
        let mut n = AnimSequenceNode::default();
        n.set_animation_id(0x0300_ABCD, &loader_with(3, true));
        assert!(n.pos_frame(-1).is_none());
        assert!(n.pos_frame(3).is_none());
        assert!(n.pos_frame(2).is_some());
        // The fractional overload floors: 1.99 is keyframe 1.
        assert_eq!(n.pos_frame_at(1.99).unwrap().origin.y, 1.0);

        let mut n = AnimSequenceNode::default();
        n.set_animation_id(0x0300_ABCD, &loader_with(3, false));
        assert!(n.pos_frame(0).is_none());
    }

    #[test]
    fn part_frame_bounds_and_value() {
        let mut n = AnimSequenceNode::default();
        n.set_animation_id(0x0300_ABCD, &loader_with(3, false));
        assert!(n.part_frame(-1).is_none());
        assert!(n.part_frame(3).is_none());
        assert_eq!(n.part_frame(2).unwrap().frames[0].origin.x, 2.0);
    }

    #[test]
    fn from_anim_data_copies_then_clamps() {
        let n = AnimSequenceNode::from_anim_data(
            &anim_data(0x0300_ABCD, 0, -1, 15.0),
            &loader_with(5, false),
        );
        assert_eq!((n.framerate, n.low_frame, n.high_frame), (15.0, 0, 4));
        assert!(n.has_anim());
    }
}
