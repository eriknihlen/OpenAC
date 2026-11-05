//! What the animations fire: the hooks each object's keyframes reach.
//!
//! The animation-done hook is taken at once, in the quantum that reached it
//! (it completes the motion the interpreter waits for). Everything else is
//! queued per owner and handed out once per frame, after the poses: part
//! fades go to the window's [`TranslucencyFades`], sounds to the window's
//! audio, and particle, script, light and the remaining visual hooks go to
//! the window's effects (see [`FrameHooks::effects`], `crate::effects`).

use std::collections::HashMap;

use ac_dat::objects::{AnimationHook, HookData};
use glam::Vec3;

/// Who fired a hook: a server object (by its local id) or a dat static.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub enum HookOwner {
    /// A server object, by presentation local id.
    Object(u32),
    /// A landblock or cell static.
    Static(super::statics::StaticKey),
}

/// One hook as fired, with where its owner stood.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct FiredHook {
    /// Who fired it.
    pub owner: HookOwner,
    /// The owner's server id (0 for dat statics).
    pub guid: u32,
    /// The owner's root, drawing frame.
    pub position: Vec3,
    /// The hook.
    pub hook: AnimationHook,
}

/// The hooks one frame produced, by destination.
#[derive(Debug, Default)]
pub struct FrameHooks {
    /// Sounds (sound, sound-table and tweaked-sound hooks), for the audio.
    pub sounds: Vec<FiredHook>,
    /// Particles, physics scripts, default scripts, lights, luminosity,
    /// diffuse, scale, texture scrolling, no-draw and whole-object fades:
    /// for the window's effects (`crate::effects`).
    pub effects: Vec<FiredHook>,
}

/// Hooks captured during the frame's advance, routed once per frame
/// (OpenAC `AnimationHookFrameQueue`).
#[derive(Debug, Default)]
pub struct HookFrameQueue {
    entries: Vec<(HookOwner, u32, Vec<AnimationHook>)>,
}

impl HookFrameQueue {
    /// Queue `hooks` fired by `owner` (`guid` 0 for statics).
    pub fn capture(&mut self, owner: HookOwner, guid: u32, hooks: Vec<AnimationHook>) {
        if !hooks.is_empty() {
            self.entries.push((owner, guid, hooks));
        }
    }

    /// How many captures are waiting.
    #[must_use]
    pub fn len(&self) -> usize {
        self.entries.len()
    }

    /// True with nothing waiting.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.entries.is_empty()
    }

    /// Route everything captured, in capture order: part fades start in
    /// `fades`; the rest is sorted into `out`. An owner `root` no longer
    /// knows (it went away during the frame) drops its hooks. The
    /// animation-done hook was taken at capture and is skipped here.
    pub fn drain(
        &mut self,
        root: impl Fn(HookOwner) -> Option<Vec3>,
        fades: &mut TranslucencyFades,
        out: &mut FrameHooks,
    ) {
        for (owner, guid, hooks) in self.entries.drain(..) {
            let Some(position) = root(owner) else {
                continue;
            };
            for hook in hooks {
                let fired = FiredHook {
                    owner,
                    guid,
                    position,
                    hook,
                };
                match hook.data {
                    HookData::TransparentPart {
                        part_index,
                        start,
                        end,
                        time,
                    } => fades.start_part_fade(owner, part_index, start, end, time),
                    HookData::Sound { .. }
                    | HookData::SoundTable { .. }
                    | HookData::SoundTweaked { .. } => out.sounds.push(fired),
                    HookData::NoOp
                    | HookData::AnimationDone
                    | HookData::Attack(_)
                    | HookData::ReplaceObject { .. }
                    | HookData::Ethereal { .. }
                    | HookData::SetOmega { .. } => {}
                    _ => out.effects.push(fired),
                }
            }
        }
    }

    /// Drop everything waiting.
    pub fn clear(&mut self) {
        self.entries.clear();
    }
}

/// Changes shorter than this are immediate, seconds.
#[allow(clippy::excessive_precision)]
pub const TRANSLUCENCY_INSTANT_EPSILON: f32 = 0.000_199_999_995;

#[derive(Debug, Clone, Copy, PartialEq)]
struct Fade {
    elapsed: f32,
    duration: f32,
    start: f32,
    end: f32,
}

/// Per-part translucency ramps started by part-fade hooks (OpenAC
/// `TranslucencyFadeManager`): 0 is opaque, 1 invisible. A part fades
/// linearly from its start value to its end value over the hook's time;
/// a part an object's idle cycle sets to 1 at once is hidden.
#[derive(Debug, Default)]
pub struct TranslucencyFades {
    active: HashMap<HookOwner, HashMap<u32, Fade>>,
    committed: HashMap<HookOwner, HashMap<u32, f32>>,
    revision: u64,
}

impl TranslucencyFades {
    /// No fades.
    #[must_use]
    pub fn new() -> Self {
        Self {
            revision: 1,
            ..Self::default()
        }
    }

    /// Changes every time a committed value changes.
    #[must_use]
    pub fn revision(&self) -> u64 {
        self.revision
    }

    /// Start a fade of `owner`'s part `part` from `start` to `end` over
    /// `time` seconds; a time at or under the instant epsilon commits the
    /// end value now. A second fade of the same part replaces the first.
    pub fn start_part_fade(
        &mut self,
        owner: HookOwner,
        part: u32,
        start: f32,
        end: f32,
        time: f32,
    ) {
        if time <= TRANSLUCENCY_INSTANT_EPSILON {
            if let Some(fades) = self.active.get_mut(&owner) {
                fades.remove(&part);
            }
            self.commit(owner, part, end);
            return;
        }
        self.active.entry(owner).or_default().insert(
            part,
            Fade {
                elapsed: 0.0,
                duration: time,
                start,
                end,
            },
        );
        self.commit(owner, part, start);
    }

    /// Advance every running fade by `dt` seconds.
    pub fn advance_all(&mut self, dt: f32) {
        if self.active.is_empty() {
            return;
        }
        let mut commits = Vec::new();
        for (owner, fades) in &mut self.active {
            fades.retain(|part, fade| {
                fade.elapsed += dt;
                let t = if fade.duration <= TRANSLUCENCY_INSTANT_EPSILON {
                    1.0
                } else {
                    (fade.elapsed / fade.duration).clamp(0.0, 1.0)
                };
                let value = if t >= 1.0 {
                    fade.end
                } else {
                    fade.start + (fade.end - fade.start) * t
                };
                commits.push((*owner, *part, value));
                t < 1.0
            });
        }
        self.active.retain(|_, fades| !fades.is_empty());
        for (owner, part, value) in commits {
            self.commit(owner, part, value);
        }
    }

    /// The part's current value, when a hook ever set it.
    #[must_use]
    pub fn current(&self, owner: HookOwner, part: u32) -> Option<f32> {
        self.committed.get(&owner)?.get(&part).copied()
    }

    /// Whether `owner` has any committed value (so its parts need looking
    /// at when drawn).
    #[must_use]
    pub fn has_owner(&self, owner: HookOwner) -> bool {
        self.committed.contains_key(&owner)
    }

    /// Forget an owner (it left).
    pub fn clear_owner(&mut self, owner: HookOwner) {
        let changed =
            self.active.remove(&owner).is_some() | self.committed.remove(&owner).is_some();
        if changed {
            self.revision += 1;
        }
    }

    fn commit(&mut self, owner: HookOwner, part: u32, value: f32) {
        let parts = self.committed.entry(owner).or_default();
        if parts
            .get(&part)
            .is_some_and(|p| p.to_bits() == value.to_bits())
        {
            return;
        }
        parts.insert(part, value);
        self.revision += 1;
    }
}

#[cfg(test)]
mod tests {
    //! Cases from OpenAC `TranslucencyFadeManagerTests` and
    //! `TranslucencyHookSinkTests`.
    use super::*;

    const E: HookOwner = HookOwner::Object(1);

    fn approx(a: Option<f32>, b: f32) -> bool {
        a.is_some_and(|a| (a - b).abs() < 1e-6)
    }

    #[test]
    fn an_instant_fade_commits_the_end_at_once() {
        let mut f = TranslucencyFades::new();
        f.start_part_fade(E, 3, 0.0, 1.0, TRANSLUCENCY_INSTANT_EPSILON);
        assert_eq!(f.current(E, 3), Some(1.0));
        f.start_part_fade(E, 4, 0.2, 0.7, 0.0);
        assert_eq!(f.current(E, 4), Some(0.7));
    }

    #[test]
    fn a_real_fade_reads_its_start_then_interpolates_linearly() {
        let mut f = TranslucencyFades::new();
        f.start_part_fade(E, 0, 0.0, 1.0, 2.0);
        assert_eq!(f.current(E, 0), Some(0.0));
        f.advance_all(1.0);
        assert!(approx(f.current(E, 0), 0.5));

        // Asymmetric range at a quarter of the time.
        f.start_part_fade(E, 1, 0.2, 0.6, 4.0);
        f.advance_all(1.0);
        assert!(approx(f.current(E, 1), 0.3));
    }

    #[test]
    fn overshoot_commits_the_exact_end_and_is_idempotent() {
        let mut f = TranslucencyFades::new();
        f.start_part_fade(E, 0, 0.0, 0.8, 1.0);
        f.advance_all(5.0);
        assert_eq!(f.current(E, 0), Some(0.8));
        let revision = f.revision();
        f.advance_all(5.0);
        assert_eq!(f.current(E, 0), Some(0.8));
        assert_eq!(f.revision(), revision);
    }

    #[test]
    fn clearing_an_owner_removes_running_and_committed_values() {
        let mut f = TranslucencyFades::new();
        f.start_part_fade(E, 0, 0.0, 1.0, 2.0);
        f.clear_owner(E);
        assert_eq!(f.current(E, 0), None);
        f.advance_all(1.0);
        assert_eq!(f.current(E, 0), None);
    }

    #[test]
    fn a_second_fade_replaces_the_first() {
        let mut f = TranslucencyFades::new();
        f.start_part_fade(E, 0, 0.0, 1.0, 2.0);
        f.start_part_fade(E, 0, 1.0, 0.0, 2.0);
        f.advance_all(1.0);
        assert!(approx(f.current(E, 0), 0.5));
        f.advance_all(1.0);
        assert_eq!(f.current(E, 0), Some(0.0));
    }

    #[test]
    fn unknown_parts_read_nothing_and_parts_advance_independently() {
        let mut f = TranslucencyFades::new();
        assert_eq!(f.current(E, 9), None);
        f.start_part_fade(E, 0, 0.0, 1.0, 1.0);
        f.start_part_fade(E, 1, 0.0, 1.0, 4.0);
        f.advance_all(1.0);
        assert_eq!(f.current(E, 0), Some(1.0));
        assert!(approx(f.current(E, 1), 0.25));
    }

    #[test]
    fn the_revision_moves_only_when_a_committed_value_changes() {
        let mut f = TranslucencyFades::new();
        let r0 = f.revision();
        f.start_part_fade(E, 0, 1.0, 1.0, 0.0);
        let r1 = f.revision();
        assert!(r1 > r0);
        f.start_part_fade(E, 0, 1.0, 1.0, 0.0);
        assert_eq!(f.revision(), r1);
    }

    fn hook(data: HookData) -> AnimationHook {
        AnimationHook { direction: 0, data }
    }

    #[test]
    fn the_queue_routes_part_fades_sounds_and_effects() {
        let mut q = HookFrameQueue::default();
        q.capture(
            E,
            0x5000_0001,
            vec![
                hook(HookData::TransparentPart {
                    part_index: 2,
                    start: 1.0,
                    end: 1.0,
                    time: 0.0,
                }),
                hook(HookData::Sound { id: 0x0A00_0001 }),
                hook(HookData::Ethereal { ethereal: true }),
                hook(HookData::Transparent {
                    start: 0.0,
                    end: 1.0,
                    time: 1.0,
                }),
                hook(HookData::AnimationDone),
            ],
        );
        q.capture(
            HookOwner::Object(2),
            0x5000_0002,
            vec![hook(HookData::Sound { id: 1 })],
        );
        let mut fades = TranslucencyFades::new();
        let mut out = FrameHooks::default();
        // Object 2 went away during the frame.
        q.drain(
            |o| (o == E).then_some(Vec3::new(1.0, 2.0, 3.0)),
            &mut fades,
            &mut out,
        );
        assert!(q.is_empty());
        // The part fade forwards its fields verbatim; the ethereal hook and
        // the whole-object fade do not touch part fades.
        assert_eq!(fades.current(E, 2), Some(1.0));
        assert_eq!(fades.current(E, 0), None);
        assert_eq!(out.sounds.len(), 1);
        assert_eq!(out.sounds[0].guid, 0x5000_0001);
        assert_eq!(out.sounds[0].position, Vec3::new(1.0, 2.0, 3.0));
        assert_eq!(out.effects.len(), 1);
    }
}
