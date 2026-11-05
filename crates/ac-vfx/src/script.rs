//! Physics scripts: timed lists of hooks (start a particle emitter, play a
//! sound, call another script, ...). Each owning object has one queue;
//! playing a script on an object that is already running one appends it,
//! to start when the last one ends. Different objects run independently.
//! Time is the presentation's game clock, in seconds.

use std::collections::{HashMap, HashSet, VecDeque};
use std::fmt;
use std::sync::Arc;

use ac_bits::Reader;
use ac_dat::DatObject;
use ac_dat::objects::{AnimationHook, PhysicsScript};
use glam::Vec3;

use crate::dats::VfxDats;
use crate::random::RandomSource;
use crate::script_table::is_physics_script_did;
use crate::slot_map::SlotMap;

/// A positive call delay below this plays at once.
pub const IMMEDIATE_CALL_PES_THRESHOLD_SECONDS: f32 = 0.0002;

/// A physics script ready to run: its hooks in start-time order.
#[derive(Debug, Clone, PartialEq)]
pub struct LoadedPhysicsScript {
    /// Script id.
    pub id: u32,
    /// (start time, hook), seconds after the script starts, in order.
    pub hooks: Vec<(f64, AnimationHook)>,
}

impl LoadedPhysicsScript {
    /// The script as stored, without reordering.
    #[must_use]
    pub fn from_dat(script: &PhysicsScript) -> Self {
        Self {
            id: script.id,
            hooks: script
                .script_data
                .iter()
                .map(|d| (d.start_time, d.hook))
                .collect(),
        }
    }
}

/// Why a physics script did not load.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ScriptLoadError(pub String);

impl fmt::Display for ScriptLoadError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.0)
    }
}

impl std::error::Error for ScriptLoadError {}

/// Read physics script `id` from the portal dat as the game does: an id that
/// is not a physics script id, or no such file, is `None`; a file that does
/// not decode to its last byte, stores another id or has a non-finite start
/// time is an error. The hooks are put in start-time order (stable).
pub fn load_physics_script(
    dats: &dyn VfxDats,
    id: u32,
) -> Result<Option<LoadedPhysicsScript>, ScriptLoadError> {
    if !is_physics_script_did(id) {
        return Ok(None);
    }
    let Some(bytes) = dats
        .portal_bytes(id)
        .map_err(|e| ScriptLoadError(e.to_string()))?
    else {
        return Ok(None);
    };
    parse_physics_script(id, &bytes).map(Some)
}

/// Decode a physics script file (see [`load_physics_script`]).
pub fn parse_physics_script(id: u32, bytes: &[u8]) -> Result<LoadedPhysicsScript, ScriptLoadError> {
    let mut r = Reader::new(bytes);
    let script = PhysicsScript::unpack(&mut r)
        .map_err(|e| ScriptLoadError(format!("PhysicsScript 0x{id:08X}: {e}")))?;
    if r.remaining() != 0 {
        return Err(ScriptLoadError(format!(
            "PhysicsScript 0x{id:08X} left {} of {} bytes unread.",
            r.remaining(),
            bytes.len()
        )));
    }
    if script.id != id {
        return Err(ScriptLoadError(format!(
            "PhysicsScript entry 0x{id:08X} contained id 0x{:08X}.",
            script.id
        )));
    }
    if let Some((i, d)) = script
        .script_data
        .iter()
        .enumerate()
        .find(|(_, d)| !d.start_time.is_finite())
    {
        return Err(ScriptLoadError(format!(
            "PhysicsScript 0x{id:08X} hook {i} has non-finite StartTime {}.",
            d.start_time
        )));
    }
    let mut loaded = LoadedPhysicsScript::from_dat(&script);
    loaded.hooks.sort_by(|a, b| a.0.total_cmp(&b.0));
    Ok(loaded)
}

/// Receives the hooks the runner fires. It gets the runner back so a hook
/// can play or call another script (or stop an owner) on the spot.
pub trait ScriptHookSink {
    /// `owner`'s script fired `hook`; `anchor` is the owner's anchor.
    fn on_script_hook(
        &mut self,
        runner: &mut PhysicsScriptRunner,
        owner: u32,
        anchor: Vec3,
        hook: &AnimationHook,
    );

    /// Whether `owner`'s scripts may advance this tick (an object outside
    /// any cell, or frozen, waits).
    fn can_advance_owner(&self, owner: u32) -> bool {
        let _ = owner;
        true
    }
}

/// The game clock went backwards, or is not finite.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct ScriptTimeError {
    /// The time given.
    pub given: f64,
    /// The runner's current time.
    pub current: f64,
}

impl fmt::Display for ScriptTimeError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(
            f,
            "physics script time must be finite and monotonic: {} after {}",
            self.given, self.current
        )
    }
}

impl std::error::Error for ScriptTimeError {}

type ScriptLoader =
    Box<dyn Fn(u32) -> Result<Option<Arc<LoadedPhysicsScript>>, ScriptLoadError> + Send + Sync>;

#[derive(Debug, Clone)]
struct ScheduledScript {
    did: u32,
    script: Arc<LoadedPhysicsScript>,
    start: f64,
    duration: f64,
    immediate_ancestors: Option<HashSet<u32>>,
    earliest_tick_generation: i64,
    next_hook: usize,
}

#[derive(Debug, Default)]
struct OwnerQueue {
    serial: u64,
    scripts: VecDeque<ScheduledScript>,
}

#[derive(Debug, Clone, Copy)]
struct DelayedCallPes {
    owner: u32,
    script_did: u32,
    due: f64,
    earliest_tick_generation: i64,
}

#[derive(Debug, Clone)]
struct DispatchContext {
    owner: u32,
    script_did: u32,
    script_start: f64,
    immediate_ancestors: Option<HashSet<u32>>,
}

/// Runs every object's physics scripts against the game clock.
pub struct PhysicsScriptRunner {
    load: ScriptLoader,
    random: Box<dyn RandomSource>,
    cache: HashMap<u32, Option<Arc<LoadedPhysicsScript>>>,
    owners: SlotMap<u32, OwnerQueue>,
    anchors: HashMap<u32, Vec3>,
    delayed: Vec<DelayedCallPes>,
    next_owner_serial: u64,
    game_time: f64,
    tick_generation: i64,
    frame_time_published: bool,
    processing_timed_hooks: bool,
    dispatch: Option<DispatchContext>,
    diagnostics: Vec<String>,
}

impl fmt::Debug for PhysicsScriptRunner {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("PhysicsScriptRunner")
            .field("owners", &self.owners.len())
            .field("delayed", &self.delayed.len())
            .field("game_time", &self.game_time)
            .finish_non_exhaustive()
    }
}

impl PhysicsScriptRunner {
    /// A runner loading scripts through `load` and drawing call delays from
    /// `random`. Loads (hits, misses and failures) are cached.
    pub fn new(
        load: impl Fn(u32) -> Result<Option<Arc<LoadedPhysicsScript>>, ScriptLoadError>
        + Send
        + Sync
        + 'static,
        random: Box<dyn RandomSource>,
    ) -> Self {
        Self {
            load: Box::new(load),
            random,
            cache: HashMap::new(),
            owners: SlotMap::default(),
            anchors: HashMap::new(),
            delayed: Vec::new(),
            next_owner_serial: 0,
            game_time: 0.0,
            tick_generation: 0,
            frame_time_published: false,
            processing_timed_hooks: false,
            dispatch: None,
            diagnostics: Vec::new(),
        }
    }

    /// A runner loading scripts from the dats.
    #[must_use]
    pub fn with_dats(dats: Arc<dyn VfxDats>, random: Box<dyn RandomSource>) -> Self {
        Self::new(
            move |id| load_physics_script(dats.as_ref(), id).map(|s| s.map(Arc::new)),
            random,
        )
    }

    /// Scripts queued across every owner.
    #[must_use]
    pub fn active_script_count(&self) -> usize {
        self.owners.values().map(|o| o.scripts.len()).sum()
    }

    /// Owners with a queue.
    #[must_use]
    pub fn active_owner_count(&self) -> usize {
        self.owners.len()
    }

    /// Delayed script calls waiting.
    #[must_use]
    pub fn scheduled_call_pes_count(&self) -> usize {
        self.delayed.len()
    }

    /// Owners with an anchor.
    #[must_use]
    pub fn owner_anchor_count(&self) -> usize {
        self.anchors.len()
    }

    /// The runner's game clock.
    #[must_use]
    pub fn game_time(&self) -> f64 {
        self.game_time
    }

    /// Messages about scripts that could not play, since the last call.
    pub fn take_diagnostics(&mut self) -> Vec<String> {
        std::mem::take(&mut self.diagnostics)
    }

    /// Set the position `owner`'s hooks are reported at.
    pub fn set_owner_anchor(&mut self, owner: u32, world_position: Vec3) {
        if owner != 0 {
            self.anchors.insert(owner, world_position);
        }
    }

    /// The anchor `owner`'s hooks are reported at.
    #[must_use]
    pub fn owner_anchor(&self, owner: u32) -> Option<Vec3> {
        self.anchors.get(&owner).copied()
    }

    /// Queue script `script_did` on `owner`: it starts now, or when the
    /// owner's last queued script ends. False (nothing queued) for a zero
    /// id, a missing or empty script, a non-finite start, or a zero-time
    /// call chain that would never yield.
    pub fn play_direct(&mut self, owner: u32, script_did: u32) -> bool {
        if owner == 0 || script_did == 0 {
            return false;
        }
        let Some(script) = self.resolve(script_did).filter(|s| !s.hooks.is_empty()) else {
            self.diagnostics.push(format!(
                "PhysicsScript 0x{script_did:08X} for owner 0x{owner:08X} is missing or empty."
            ));
            return false;
        };
        if script.hooks.iter().any(|(t, _)| !t.is_finite()) {
            self.diagnostics.push(format!(
                "PhysicsScript 0x{script_did:08X} for owner 0x{owner:08X} has a non-finite hook time."
            ));
            return false;
        }

        let start = match self.owners.get(&owner).and_then(|o| o.scripts.back()) {
            Some(last) => last.start + last.duration,
            None => self.game_time,
        };
        if !start.is_finite() {
            self.diagnostics.push(format!(
                "PhysicsScript 0x{script_did:08X} for owner 0x{owner:08X} produced a non-finite start time."
            ));
            return false;
        }

        let mut immediate_ancestors = None;
        if let Some(dispatch) = &self.dispatch
            && dispatch.owner == owner
            && start <= dispatch.script_start
        {
            let mut ancestors = dispatch.immediate_ancestors.clone().unwrap_or_default();
            ancestors.insert(dispatch.script_did);
            if ancestors.contains(&script_did) {
                self.diagnostics.push(format!(
                    "Rejected zero-time recursive PhysicsScript 0x{script_did:08X} for owner \
                     0x{owner:08X}; DAT CallPES chain would never yield."
                ));
                return false;
            }
            immediate_ancestors = Some(ancestors);
        }

        let earliest_tick_generation = self.earliest_script_tick_generation(owner);
        if !self.owners.contains_key(&owner) {
            self.next_owner_serial += 1;
            self.owners.insert(
                owner,
                OwnerQueue {
                    serial: self.next_owner_serial,
                    scripts: VecDeque::new(),
                },
            );
        }
        let duration = script.hooks.last().map_or(0.0, |(t, _)| *t);
        if let Some(queue) = self.owners.get_mut(&owner) {
            queue.scripts.push_back(ScheduledScript {
                did: script_did,
                script,
                start,
                duration,
                immediate_ancestors,
                earliest_tick_generation,
                next_hook: 0,
            });
        }
        true
    }

    /// Set `owner`'s anchor and queue the script.
    pub fn play(&mut self, script_did: u32, owner: u32, anchor: Vec3) -> bool {
        self.set_owner_anchor(owner, anchor);
        self.play_direct(owner, script_did)
    }

    /// A script call from a hook: a pause below
    /// [`IMMEDIATE_CALL_PES_THRESHOLD_SECONDS`] queues the script at once;
    /// a longer one waits a uniformly random part of the pause first.
    pub fn schedule_call_pes(&mut self, owner: u32, script_did: u32, maximum_pause: f32) -> bool {
        if owner == 0 || script_did == 0 || !maximum_pause.is_finite() {
            return false;
        }
        if maximum_pause < IMMEDIATE_CALL_PES_THRESHOLD_SECONDS {
            return self.play_direct(owner, script_did);
        }
        let sample = self.random.next_unit();
        if !sample.is_finite() {
            return false;
        }
        let sample = sample.clamp(0.0, 1.0);
        let due = self.game_time + sample * f64::from(maximum_pause);
        let earliest_tick_generation = self.earliest_tick_generation_for_new_timed_hook();
        self.delayed.push(DelayedCallPes {
            owner,
            script_did,
            due,
            earliest_tick_generation,
        });
        true
    }

    /// Publish the frame's clock before animation hooks are delivered,
    /// without running the script pass.
    pub fn publish_time(&mut self, game_time: f64) -> Result<(), ScriptTimeError> {
        self.validate_time(game_time)?;
        self.game_time = game_time;
        self.frame_time_published = true;
        Ok(())
    }

    /// Run every due delayed call and every due hook up to `game_time`
    /// (absolute seconds).
    pub fn tick(
        &mut self,
        game_time: f64,
        sink: &mut dyn ScriptHookSink,
    ) -> Result<(), ScriptTimeError> {
        self.validate_time(game_time)?;
        self.game_time = game_time;
        self.frame_time_published = false;
        self.tick_generation += 1;

        self.drain_due_call_pes(game_time, sink);

        let owners: Vec<u32> = self.owners.keys().collect();
        for owner in owners {
            let Some(serial) = self.owners.get(&owner).map(|o| o.serial) else {
                continue;
            };
            if !sink.can_advance_owner(owner) {
                continue;
            }
            self.drain_owner(owner, serial, game_time, sink);
        }
        Ok(())
    }

    /// [`Self::tick`] by a delta; a negative delta counts as zero.
    pub fn advance(
        &mut self,
        delta_seconds: f32,
        sink: &mut dyn ScriptHookSink,
    ) -> Result<(), ScriptTimeError> {
        let delta = if delta_seconds < 0.0 {
            0.0
        } else {
            f64::from(delta_seconds)
        };
        self.tick(self.game_time + delta, sink)
    }

    /// Drop `owner`'s scripts, anchor and delayed calls.
    pub fn stop_all_for_entity(&mut self, owner: u32) {
        self.owners.remove(&owner);
        self.anchors.remove(&owner);
        self.delayed.retain(|call| call.owner != owner);
    }

    /// Drop everything.
    pub fn clear(&mut self) {
        self.owners.clear();
        self.anchors.clear();
        self.delayed.clear();
    }

    fn drain_due_call_pes(&mut self, game_time: f64, sink: &dyn ScriptHookSink) {
        self.processing_timed_hooks = true;
        // Newest first, as the game walks its timed hooks.
        let mut i = self.delayed.len();
        while i > 0 {
            i -= 1;
            let Some(call) = self.delayed.get(i).copied() else {
                continue;
            };
            if call.due > game_time
                || call.earliest_tick_generation > self.tick_generation
                || !sink.can_advance_owner(call.owner)
            {
                continue;
            }
            self.delayed.remove(i);
            self.play_direct(call.owner, call.script_did);
        }
        self.processing_timed_hooks = false;
    }

    fn drain_owner(
        &mut self,
        owner: u32,
        serial: u64,
        game_time: f64,
        sink: &mut dyn ScriptHookSink,
    ) {
        loop {
            // A hook may have deleted (or replaced) this owner's queue.
            let Some(queue) = self.owners.get_mut(&owner).filter(|q| q.serial == serial) else {
                return;
            };
            let Some(current) = queue.scripts.front_mut() else {
                self.owners.remove(&owner);
                return;
            };
            if current.earliest_tick_generation > self.tick_generation {
                return;
            }
            let Some(&(time, hook)) = current.script.hooks.get(current.next_hook) else {
                queue.scripts.pop_front();
                continue;
            };
            if current.start + time > game_time {
                return;
            }
            current.next_hook += 1;
            let context = DispatchContext {
                owner,
                script_did: current.did,
                script_start: current.start,
                immediate_ancestors: current.immediate_ancestors.clone(),
            };
            self.dispatch_hook(owner, context, &hook, sink);
        }
    }

    fn dispatch_hook(
        &mut self,
        owner: u32,
        context: DispatchContext,
        hook: &AnimationHook,
        sink: &mut dyn ScriptHookSink,
    ) {
        let anchor = self.anchors.get(&owner).copied().unwrap_or(Vec3::ZERO);
        let previous = self.dispatch.replace(context);
        sink.on_script_hook(self, owner, anchor, hook);
        self.dispatch = previous;
    }

    fn resolve(&mut self, id: u32) -> Option<Arc<LoadedPhysicsScript>> {
        if let Some(cached) = self.cache.get(&id) {
            return cached.clone();
        }
        let script = match (self.load)(id) {
            Ok(script) => script,
            Err(e) => {
                self.diagnostics
                    .push(format!("Failed to load PhysicsScript 0x{id:08X}: {e}"));
                None
            }
        };
        self.cache.insert(id, script.clone());
        script
    }

    fn earliest_tick_generation_for_new_timed_hook(&self) -> i64 {
        // A timed call added during the animation phase is not seen by the
        // pass that is about to run.
        if self.frame_time_published {
            self.tick_generation + 2
        } else {
            self.tick_generation + 1
        }
    }

    fn earliest_script_tick_generation(&self, owner: u32) -> i64 {
        if self.processing_timed_hooks || self.dispatch.as_ref().is_some_and(|d| d.owner == owner) {
            self.tick_generation
        } else {
            self.tick_generation + 1
        }
    }

    fn validate_time(&self, game_time: f64) -> Result<(), ScriptTimeError> {
        if !game_time.is_finite() || game_time < self.game_time {
            return Err(ScriptTimeError {
                given: game_time,
                current: self.game_time,
            });
        }
        Ok(())
    }
}

#[cfg(test)]
pub(crate) mod tests;
