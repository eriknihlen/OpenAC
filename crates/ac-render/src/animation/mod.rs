//! The window's animation: part poses for every drawn object.
//!
//! The session owns motion and moves bodies (root motion); the window owns
//! poses. For each server object with a motion table (or a Setup default
//! animation) the window keeps a [`LiveAnimation`]: its own motion
//! interpreter and sequencer, built from the object's Setup and motion
//! table when the object first shows up and driven by the motion state the
//! session publishes in the object's view. Statics of the landblocks and
//! cells around the player that have a default animation get a
//! [`StaticAnimation`]. Each frame the presenter advances them all, poses
//! their parts, and routes the hooks their animations fired (part fades
//! here, sounds to the window's audio, the rest to the window's effects
//! (`crate::effects`)). Everything here is dropped with the window and rebuilt on the
//! next attach (the cycles restart from their first frame).
//!
//! Held and wielded objects ([`attachment`]) are posed from their parent's
//! part poses when the frame is drawn.

pub mod attachment;
pub mod hooks;
pub mod live;
pub mod pose;
pub mod source;
pub mod statics;
#[cfg(test)]
pub(crate) mod test_support;
#[cfg(test)]
mod tests;

use std::collections::HashMap;

use ac_runtime::world::{AppliedMotion, ObjectView};
use glam::Vec3;

pub use hooks::{FiredHook, FrameHooks, HookFrameQueue, HookOwner, TranslucencyFades};
pub use live::LiveAnimation;
pub use pose::PartPose;
pub use source::AnimationSource;
pub use statics::{StaticAnimation, StaticKey};

/// A part at or above this translucency is not drawn.
pub const HIDDEN_PART_TRANSLUCENCY: f32 = 1.0;

/// What an object was when it was found not to animate, so it is not
/// looked at again until it changes.
type Refusal = (u16, Option<u32>, Option<u32>);

/// Every animation of one window. See the module documentation.
#[derive(Debug, Default)]
pub struct AnimationPresenter {
    live: HashMap<u32, LiveAnimation>,
    refused: HashMap<u32, Refusal>,
    statics: crate::IdMap<StaticKey, (StaticAnimation, Vec3)>,
    queue: HookFrameQueue,
    fades: TranslucencyFades,
    hooks: FrameHooks,
    /// Motion states objects took up, in order, for the next sync.
    motions: Vec<AppliedMotion>,
    /// The motion log (`OPENAC_MOTION_LOG`) is on.
    motion_log: bool,
    /// The objects the motion log follows (the player, the selection).
    watched: [Option<u32>; 2],
    /// For the motion log: the animation each watched object drew last
    /// (dat id, frame, last frame).
    drawn: HashMap<u32, (u32, f64, i32)>,
}

impl AnimationPresenter {
    /// Nothing animating.
    #[must_use]
    pub fn new() -> Self {
        Self {
            fades: TranslucencyFades::new(),
            ..Self::default()
        }
    }

    /// The motion states objects took up since the last frame, in order:
    /// the next [`sync`](Self::sync) takes each up before its object
    /// follows its latest view.
    pub fn queue_motions(&mut self, motions: impl IntoIterator<Item = AppliedMotion>) {
        self.motions.extend(motions);
    }

    /// Forget the queued motion states (no scene to follow them with).
    pub fn clear_motions(&mut self) {
        self.motions.clear();
    }

    /// Turn the motion log on or off.
    pub fn set_motion_log(&mut self, on: bool) {
        self.motion_log = on;
    }

    /// The objects the motion log follows this frame.
    pub fn watch(&mut self, guids: [Option<u32>; 2]) {
        self.watched = guids;
    }

    /// Follow the objects `views` lists (top-level objects with a Setup):
    /// new ones that animate get their animation, changed ones take up the
    /// queued motion states (in order) and follow their view, ones that
    /// went away are dropped.
    pub fn sync<'a>(
        &mut self,
        views: impl Iterator<Item = &'a ObjectView>,
        source: &mut dyn AnimationSource,
    ) {
        let mut queued: HashMap<u32, Vec<AppliedMotion>> = HashMap::new();
        for applied in self.motions.drain(..) {
            queued
                .entry(applied.key.local_id)
                .or_default()
                .push(applied);
        }
        let mut seen = std::collections::HashSet::new();
        for view in views {
            let (Some(setup_id), Some(_), None) = (view.setup_id, view.position, view.parent)
            else {
                continue;
            };
            let local = view.key.local_id;
            seen.insert(local);
            let table = view.motion_table_id.filter(|&t| t != 0);
            if let Some(live) = self.live.get_mut(&local) {
                if live.key == view.key
                    && live.setup_id == setup_id
                    && live.motion_table_id.filter(|&t| t != 0) == table
                {
                    let log = self.motion_log && self.watched.contains(&Some(view.guid));
                    live.set_motion_log(log);
                    for applied in queued.remove(&local).unwrap_or_default() {
                        if applied.key != view.key {
                            continue;
                        }
                        // A server state is taken up even when it reads
                        // the same; what the body passed through on the
                        // way (its move-to's turn stopped) is followed.
                        let (done, did) = if applied.taken_up {
                            (live.take_up(&applied.motion), "took up")
                        } else {
                            (live.follow_motion(&applied.motion), "followed between")
                        };
                        if done && log {
                            eprintln!("{}", live.motion_log_line(did, &applied.motion));
                        }
                    }
                    if live.follow(view)
                        && log
                        && let Some(motion) = view.motion.as_ref()
                    {
                        eprintln!("{}", live.motion_log_line("followed", motion));
                    }
                    continue;
                }
                self.live.remove(&local);
                self.drawn.remove(&local);
                self.fades.clear_owner(HookOwner::Object(local));
            }
            let refusal = (view.key.incarnation, Some(setup_id), table);
            if self.refused.get(&local) == Some(&refusal) {
                continue;
            }
            match Self::build(view, setup_id, table, source) {
                Some(mut live) => {
                    if self.motion_log && self.watched.contains(&Some(view.guid)) {
                        live.set_motion_log(true);
                        let dropped = queued.get(&local).map_or(0, Vec::len);
                        eprintln!(
                            "[motion] {} window 0x{:08X} built from its view ({dropped} queued \
                             states dropped)",
                            ac_runtime::world::motion_log_stamp(),
                            view.guid
                        );
                    }
                    self.refused.remove(&local);
                    self.live.insert(local, live);
                }
                None => {
                    self.refused.insert(local, refusal);
                }
            }
        }
        self.refused.retain(|local, _| seen.contains(local));
        let gone: Vec<u32> = self
            .live
            .keys()
            .copied()
            .filter(|local| !seen.contains(local))
            .collect();
        for local in gone {
            if let Some(live) = self.live.remove(&local)
                && self.motion_log
                && self.watched.contains(&Some(live.guid))
            {
                eprintln!(
                    "[motion] {} window 0x{:08X} dropped (its view went away)",
                    ac_runtime::world::motion_log_stamp(),
                    live.guid
                );
            }
            self.drawn.remove(&local);
            self.fades.clear_owner(HookOwner::Object(local));
        }
    }

    fn build(
        view: &ObjectView,
        setup_id: u32,
        table: Option<u32>,
        source: &mut dyn AnimationSource,
    ) -> Option<LiveAnimation> {
        let setup = source.setup(setup_id)?;
        if setup.parts.is_empty() {
            return None;
        }
        let (rules, interpreted) = match table.and_then(|t| source.motion_table(t)) {
            Some(rules) => (rules, true),
            None if setup.default_animation != 0 => (source.empty_motion_table(), false),
            None => return None,
        };
        Some(LiveAnimation::new(
            view,
            setup,
            rules,
            interpreted,
            source.animations(),
        ))
    }

    /// Start animating the static `key` placed with `setup_id` at
    /// `landblock_local` (metres in its landblock), when its Setup has a
    /// default animation. Returns whether it animates.
    pub fn register_static(
        &mut self,
        key: StaticKey,
        setup_id: u32,
        landblock_local: Vec3,
        source: &mut dyn AnimationSource,
    ) -> bool {
        if self.statics.contains_key(&key) {
            return true;
        }
        let Some(setup) = source.setup(setup_id) else {
            return false;
        };
        if setup.default_animation == 0 {
            return false;
        }
        let table = source.empty_motion_table();
        match StaticAnimation::new(key, setup, table, source.animations()) {
            Some(animation) => {
                self.statics.insert(key, (animation, landblock_local));
                true
            }
            None => false,
        }
    }

    /// Stop animating every static of `landblock` (`0xXXYY0000`).
    pub fn drop_statics(&mut self, landblock: u32) {
        let gone: Vec<StaticKey> = self
            .statics
            .keys()
            .copied()
            .filter(|k| k.landblock == landblock)
            .collect();
        for key in gone {
            self.statics.remove(&key);
            self.fades.clear_owner(HookOwner::Static(key));
        }
    }

    /// Advance everything by a frame of `elapsed` seconds. `root` places a
    /// server object's root in the drawing frame (by local id) and
    /// `static_root` a static's (by its key and landblock-local place);
    /// `player` is the player's root.
    pub fn tick(
        &mut self,
        elapsed: f64,
        player: Option<Vec3>,
        root: impl Fn(u32) -> Option<Vec3>,
        static_root: impl Fn(StaticKey, Vec3) -> Option<Vec3>,
    ) {
        if !(elapsed.is_finite() && elapsed > 0.0) {
            return;
        }
        for (local, live) in &mut self.live {
            if let Some(position) = root(*local) {
                live.tick(elapsed, position, player, &mut self.queue);
            }
            if self.motion_log && self.watched.contains(&Some(live.guid)) {
                Self::log_drawing(&mut self.drawn, *local, live);
            }
        }
        for (animation, _) in self.statics.values_mut() {
            animation.tick(elapsed, &mut self.queue);
        }
        self.fades.advance_all(elapsed as f32);
        let statics = &self.statics;
        self.hooks = FrameHooks::default();
        self.queue.drain(
            |owner| match owner {
                HookOwner::Object(local) => root(local),
                HookOwner::Static(key) => statics.get(&key).and_then(|(_, p)| static_root(key, *p)),
            },
            &mut self.fades,
            &mut self.hooks,
        );
    }

    /// The motion log's line when a watched object's drawn animation
    /// changes: the animation it draws now, and where the one before was
    /// left (an action left before its last frame was cut short); and,
    /// the first time a watched object is seen, what it draws then.
    fn log_drawing(drawn: &mut HashMap<u32, (u32, f64, i32)>, local: u32, live: &LiveAnimation) {
        let Some(now) = live.drawing() else {
            return;
        };
        let before = drawn.insert(local, now);
        let Some((anim, frame, last)) = before else {
            // First seen (just built, or just watched): what it draws now.
            eprintln!(
                "[motion] {} window 0x{:08X} draws 0x{:08X} from frame {:.1} to {}",
                ac_runtime::world::motion_log_stamp(),
                live.guid,
                now.0,
                now.1,
                now.2
            );
            return;
        };
        if anim == now.0 {
            return;
        }
        eprintln!(
            "[motion] {} window 0x{:08X} draws 0x{:08X} from frame {:.1} to {}; left 0x{anim:08X} \
             at frame {frame:.1} of {last}",
            ac_runtime::world::motion_log_stamp(),
            live.guid,
            now.0,
            now.1,
            now.2
        );
    }

    /// The animation of the server object with local id `local`.
    #[must_use]
    pub fn object(&self, local: u32) -> Option<&LiveAnimation> {
        self.live.get(&local)
    }

    /// The animation of the static `key`.
    #[must_use]
    pub fn static_animation(&self, key: StaticKey) -> Option<&StaticAnimation> {
        self.statics.get(&key).map(|(a, _)| a)
    }

    /// How many server objects and statics animate.
    #[must_use]
    pub fn counts(&self) -> (usize, usize) {
        (self.live.len(), self.statics.len())
    }

    /// `owner`'s part `part`'s translucency from part-fade hooks (0 opaque,
    /// 1 invisible), if a hook set one.
    #[must_use]
    pub fn part_translucency(&self, owner: HookOwner, part: u32) -> Option<f32> {
        self.fades.current(owner, part)
    }

    /// `parts` placed by `place` (part index, part) and each one's opacity
    /// at the object's `object_opacity` (0..=1) times its part fade: parts
    /// a fade made invisible are left out. The opacities are `None` when
    /// every part is solid.
    #[must_use]
    pub fn faded_parts(
        &self,
        owner: HookOwner,
        object_opacity: f32,
        parts: &[crate::meshing::MeshRef],
        place: impl Fn(usize, &crate::meshing::MeshRef) -> glam::Mat4,
    ) -> (Vec<crate::meshing::MeshRef>, Option<Vec<f32>>) {
        let fades = self.has_fades(owner);
        let mut out = Vec::with_capacity(parts.len());
        let mut opacities = Vec::with_capacity(parts.len());
        for (i, part) in parts.iter().enumerate() {
            let translucency = if fades {
                self.part_translucency(owner, i as u32).unwrap_or(0.0)
            } else {
                0.0
            };
            if translucency >= HIDDEN_PART_TRANSLUCENCY {
                continue;
            }
            out.push(crate::meshing::MeshRef {
                gfx_obj_id: part.gfx_obj_id,
                transform: place(i, part),
                variant: part.variant,
            });
            opacities.push(object_opacity * (1.0 - translucency));
        }
        let solid = opacities.iter().all(|o| *o >= 1.0);
        (out, (!solid).then_some(opacities))
    }

    /// Whether `owner`'s part `part` is hidden by a part-fade hook.
    #[must_use]
    pub fn part_hidden(&self, owner: HookOwner, part: u32) -> bool {
        self.fades
            .current(owner, part)
            .is_some_and(|t| t >= HIDDEN_PART_TRANSLUCENCY)
    }

    /// Start a part fade a physics script asked for (as a part-fade hook
    /// would).
    pub fn start_part_fade(
        &mut self,
        owner: HookOwner,
        part: u32,
        start: f32,
        end: f32,
        time: f32,
    ) {
        self.fades.start_part_fade(owner, part, start, end, time);
    }

    /// Whether any of `owner`'s parts has a fade value.
    #[must_use]
    pub fn has_fades(&self, owner: HookOwner) -> bool {
        self.fades.has_owner(owner)
    }

    /// Take the sounds the last tick's animations fired.
    pub fn take_sounds(&mut self) -> Vec<FiredHook> {
        std::mem::take(&mut self.hooks.sounds)
    }

    /// The effect hooks the last tick's animations fired (particles,
    /// scripts, lights, ...), for the window's effects. They are
    /// replaced on the next tick whether taken or not.
    pub fn take_effects(&mut self) -> Vec<FiredHook> {
        std::mem::take(&mut self.hooks.effects)
    }
}
