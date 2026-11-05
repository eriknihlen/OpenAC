//! Static objects that animate on their own: flags, windmills, swinging
//! signs, spinning crystals. A Setup with a default animation plays it in a
//! loop from the moment it is placed, with no motion table.
//!
//! Statics advance by the whole time since their last advance (no 30 Hz
//! object clock): a step under 0.2 ms is dropped rather than saved up, and
//! one over two seconds (the static was away) is dropped too. A spin hook
//! in the animation turns the whole object about its own place, at its
//! authored turn per 1/30 s, from the frame after the hook fired (OpenAC
//! `RetailStaticAnimatingObjectScheduler`).

use std::sync::Arc;

use ac_dat::objects::{AnimationHook, HookData, Setup};
use ac_motion::{AnimationLoader, AnimationSequencer, Frame, MotionTableRules};
use glam::{Quat, Vec3};

use super::hooks::{HookFrameQueue, HookOwner};
use super::pose::{PartPose, compose, rest_poses};
use crate::meshing::vec3;

/// A step this short is dropped, seconds.
pub const FRAME_EPSILON: f64 = 0.000_199_999_995;
/// A step this long is dropped, seconds.
pub const MAXIMUM_ELAPSED: f64 = 2.0;
/// The rate a static's spin hook is authored at, steps per second.
pub const OMEGA_REFERENCE_HZ: f64 = 30.0;

/// Which static: a landblock's (`cell` 0) or an interior cell's, by its
/// index in that list.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub struct StaticKey {
    /// The landblock id (`0xXXYY0000`).
    pub landblock: u32,
    /// The interior cell id, or 0 for the landblock's own statics.
    pub cell: u32,
    /// Index in the list.
    pub index: u32,
}

/// The whole-elapsed stepping and the spin of a static.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct StaticStep {
    pending: f64,
    /// Spin from the last spin hook, radians per 1/30 s about the world
    /// axes.
    pub omega: Vec3,
    /// The turn the spin has made so far (applied before the object's own
    /// orientation).
    pub spin: Quat,
}

impl Default for StaticStep {
    fn default() -> Self {
        Self {
            pending: 0.0,
            omega: Vec3::ZERO,
            spin: Quat::IDENTITY,
        }
    }
}

impl StaticStep {
    /// Add a frame's time; the step to take now, if any (see the module
    /// documentation).
    pub fn take(&mut self, elapsed: f64) -> Option<f64> {
        self.pending += elapsed;
        let step = std::mem::take(&mut self.pending);
        (step > FRAME_EPSILON && step <= MAXIMUM_ELAPSED).then_some(step)
    }

    /// Turn by the spin for a step of `elapsed` seconds.
    pub fn turn(&mut self, elapsed: f64) {
        if self.omega == Vec3::ZERO {
            return;
        }
        let mut frame = Frame::new(Vec3::ZERO, self.spin);
        frame.grotate(self.omega * (elapsed * OMEGA_REFERENCE_HZ) as f32);
        self.spin = frame.orientation;
    }

    /// Take the last spin hook among `hooks`, if any.
    pub fn apply_omega_hooks(&mut self, hooks: &[AnimationHook]) {
        if let Some(omega) = resolve_omega(hooks) {
            self.omega = omega;
        }
    }
}

/// The axis of the last spin hook in `hooks`.
#[must_use]
pub fn resolve_omega(hooks: &[AnimationHook]) -> Option<Vec3> {
    hooks.iter().rev().find_map(|h| match h.data {
        HookData::SetOmega { axis } => Some(vec3(axis)),
        _ => None,
    })
}

/// One animating static.
#[derive(Debug)]
pub struct StaticAnimation {
    /// Which static.
    pub key: StaticKey,
    /// Its Setup.
    pub setup: Arc<Setup>,
    sequencer: AnimationSequencer,
    /// Stepping and spin.
    pub step: StaticStep,
    /// Its parts' poses.
    pub poses: Vec<PartPose>,
    /// Advanced since the window last drew it.
    pub dirty: bool,
}

impl StaticAnimation {
    /// A static placed with `setup`, playing its default animation; `None`
    /// when the Setup has none or it does not resolve.
    #[must_use]
    pub fn new(
        key: StaticKey,
        setup: Arc<Setup>,
        table: Arc<MotionTableRules>,
        loader: Arc<dyn AnimationLoader + Send + Sync>,
    ) -> Option<Self> {
        if setup.default_animation == 0 {
            return None;
        }
        let sequencer = AnimationSequencer::from_setup(&setup, table, loader);
        if !sequencer.has_current_node() {
            return None;
        }
        Some(Self {
            key,
            poses: rest_poses(&setup, None, 1.0),
            setup,
            sequencer,
            step: StaticStep::default(),
            dirty: true,
        })
    }

    /// Advance by a frame of `elapsed` seconds: the pose, the spin (from
    /// the hooks of earlier steps), then this step's hooks into `queue`.
    /// Returns whether it advanced.
    pub fn tick(&mut self, elapsed: f64, queue: &mut HookFrameQueue) -> bool {
        let Some(step) = self.step.take(elapsed) else {
            return false;
        };
        self.dirty = true;
        let frames = self.sequencer.advance(step as f32, None).to_vec();
        compose(&self.setup, &frames, 1.0, &mut self.poses);
        self.step.turn(step);
        let hooks = self.sequencer.consume_pending_hooks();
        self.step.apply_omega_hooks(&hooks);
        let done = hooks
            .iter()
            .filter(|h| matches!(h.data, HookData::AnimationDone))
            .count();
        for _ in 0..done {
            self.sequencer
                .animation_done(true, &mut ac_motion::NoMotionDone);
        }
        queue.capture(HookOwner::Static(self.key), 0, hooks);
        true
    }
}

#[cfg(test)]
mod tests {
    //! Cases from OpenAC `RetailStaticAnimatingObjectSchedulerTests`.
    use super::*;
    use crate::animation::source::empty_table;
    use crate::animation::test_support::{MapLoader, omega_animation, setup, two_frame_animation};

    const ANIMATION: u32 = 0x0300_0001;
    const KEY: StaticKey = StaticKey {
        landblock: 0xA9B4_0000,
        cell: 0,
        index: 0,
    };

    fn table() -> Arc<MotionTableRules> {
        Arc::new(MotionTableRules::new(Arc::new(empty_table())))
    }

    fn owner(animation: ac_dat::objects::Animation) -> StaticAnimation {
        let mut s = setup(1);
        s.default_animation = ANIMATION;
        let mut loader = MapLoader::default();
        loader.add(ANIMATION, animation);
        StaticAnimation::new(KEY, Arc::new(s), table(), Arc::new(loader)).unwrap()
    }

    fn x(p: &PartPose) -> f32 {
        p.visual.w_axis.x
    }

    #[test]
    fn the_default_animation_advances_by_whole_elapsed_and_drops_long_gaps() {
        let mut o = owner(two_frame_animation());
        let mut q = HookFrameQueue::default();
        assert!(o.tick(1.0 / 60.0, &mut q));
        assert!(x(&o.poses[0]) > 0.0);
        assert!(o.tick(2.0, &mut q));
        let before = o.poses[0];
        assert!(!o.tick(2.01, &mut q));
        assert_eq!(o.poses[0], before);
    }

    #[test]
    fn sub_epsilon_steps_are_dropped_not_saved_up() {
        let mut o = owner(two_frame_animation());
        let mut q = HookFrameQueue::default();
        assert!(!o.tick(0.0001, &mut q));
        assert!(!o.tick(0.0001, &mut q));
        assert!(o.tick(0.0003, &mut q));
    }

    #[test]
    fn a_setup_without_a_default_animation_does_not_animate() {
        let mut loader = MapLoader::default();
        loader.add(ANIMATION, two_frame_animation());
        assert!(StaticAnimation::new(KEY, Arc::new(setup(1)), table(), Arc::new(loader)).is_none());
    }

    #[test]
    fn a_spin_hook_turns_the_root_from_the_next_frame() {
        let mut o = owner(omega_animation(Vec3::new(0.0, 0.0, -0.027)));
        let mut q = HookFrameQueue::default();
        o.tick(1.0 / 30.0, &mut q);
        assert_eq!(o.step.spin, Quat::IDENTITY);
        o.tick(1.0 / 30.0, &mut q);
        assert_ne!(o.step.spin, Quat::IDENTITY);
        assert!(o.step.spin.x.abs() < 1e-5 && o.step.spin.y.abs() < 1e-5);
        let first = o.step.spin;
        o.tick(1.0 / 30.0, &mut q);
        assert_ne!(o.step.spin, first);
    }

    #[test]
    fn the_spin_is_independent_of_the_frame_rate() {
        for fps in [30, 60, 180] {
            let mut o = owner(omega_animation(Vec3::new(0.0, 0.0, -0.027)));
            let mut q = HookFrameQueue::default();
            o.tick(1.0 / 30.0, &mut q);
            for _ in 0..fps {
                o.tick(1.0 / f64::from(fps), &mut q);
            }
            let forward = o.step.spin * Vec3::X;
            let yaw = -0.027_f32 * 30.0;
            let expected = Vec3::new(yaw.cos(), yaw.sin(), 0.0);
            assert!((forward - expected).length() < 1e-4, "{fps}: {forward}");
        }
    }

    #[test]
    fn without_a_spin_hook_the_root_never_turns() {
        let mut o = owner(two_frame_animation());
        let mut q = HookFrameQueue::default();
        for _ in 0..4 {
            o.tick(1.0 / 30.0, &mut q);
        }
        assert_eq!(o.step.spin, Quat::IDENTITY);
    }

    // OpenAC `StaticAnimatingOmegaHookTests` (`TryResolveOmega`).
    fn hook(data: HookData) -> AnimationHook {
        AnimationHook { direction: 0, data }
    }

    fn omega(x: f32, y: f32, z: f32) -> AnimationHook {
        hook(HookData::SetOmega {
            axis: ac_dat::types::Vector3 { x, y, z },
        })
    }

    #[test]
    fn no_hooks_or_none_that_spin_leave_the_spin_alone() {
        assert_eq!(resolve_omega(&[]), None);
        assert_eq!(
            resolve_omega(&[hook(HookData::SoundTable { sound: 1 })]),
            None
        );
    }

    #[test]
    fn a_spin_hook_is_taken_verbatim_and_the_last_in_a_batch_wins() {
        assert_eq!(
            resolve_omega(&[omega(0.0, 0.0, -0.027)]),
            Some(Vec3::new(0.0, 0.0, -0.027))
        );
        assert_eq!(
            resolve_omega(&[omega(0.0, 0.0, -0.02), omega(0.0, 0.0, 0.05)]),
            Some(Vec3::new(0.0, 0.0, 0.05))
        );
    }
}
