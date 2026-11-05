//! Everything a window needs to make the game's sounds: the engine, the
//! sound cache and the three sources the engine plays from (animation and
//! server sounds, interface sounds, region ambient sounds).

use std::collections::HashMap;
use std::sync::Arc;

use ac_dat::objects::{HookData, SoundTable};
use glam::Vec3;

use crate::ambient::{AmbientRegion, AmbientSoundFiring, AmbientSoundScheduler, LandblockTerrain};
use crate::cache::{SoundCache, SoundDats, resolve_ui_sound_table};
use crate::cookbook;
use crate::engine::AudioEngine;
use crate::environ::environ_sound;
use crate::error::AudioError;
use crate::options::AudioMixerOptions;
use crate::random::{SeededSoundRandom, SoundRandom};
use crate::settings::AudioLaunchOptions;
use crate::sound_id::SoundId;
use crate::tweaked::TweakedSoundHook;

/// Whether an animation hook is one of the three sound hooks.
#[must_use]
pub fn is_sound_hook(hook: &HookData) -> bool {
    matches!(
        hook,
        HookData::Sound { .. } | HookData::SoundTable { .. } | HookData::SoundTweaked { .. }
    )
}

/// Sounds from animation hooks and the server's sound event, played from
/// the entity's sound table.
pub struct HookSounds {
    rng: Box<dyn SoundRandom>,
    sound_tables: HashMap<u32, u32>,
    probe_wire: bool,
}

impl std::fmt::Debug for HookSounds {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("HookSounds")
            .field("sound_tables", &self.sound_tables.len())
            .finish_non_exhaustive()
    }
}

impl HookSounds {
    /// No entity tables yet.
    #[must_use]
    pub fn new(rng: Box<dyn SoundRandom>) -> Self {
        Self {
            rng,
            sound_tables: HashMap::new(),
            probe_wire: false,
        }
    }

    /// Record an entity's sound table (`0x20......`, from its create-object
    /// physics description).
    pub fn set_sound_table(&mut self, entity_id: u32, sound_table_id: u32) {
        self.sound_tables.insert(entity_id, sound_table_id);
    }

    /// Forget an entity.
    pub fn remove_entity(&mut self, entity_id: u32) {
        self.sound_tables.remove(&entity_id);
    }

    /// An entity's sound table, 0 when unknown.
    #[must_use]
    pub fn sound_table_of(&self, entity_id: u32) -> u32 {
        self.sound_tables.get(&entity_id).copied().unwrap_or(0)
    }

    fn table(
        &self,
        cache: &SoundCache,
        entity_id: u32,
    ) -> Result<Option<Arc<SoundTable>>, AudioError> {
        match self.sound_table_of(entity_id) {
            0 => Ok(None),
            id => cache.sound_table(id),
        }
    }

    /// A sound hook fired on an entity at a world position. A bare sound
    /// hook plays at volume 1 and priority 0; a table hook plays the
    /// entity's entry; a tweaked hook rolls its own odds first.
    pub fn on_hook(
        &mut self,
        engine: &mut AudioEngine,
        cache: &SoundCache,
        entity_id: u32,
        position: Vec3,
        hook: &HookData,
    ) -> Result<(), AudioError> {
        match *hook {
            HookData::Sound { id } => play_3d(engine, cache, entity_id, position, id, 1.0, 0.0),
            HookData::SoundTable { sound } => {
                let Some(table) = self.table(cache, entity_id)? else {
                    return Ok(());
                };
                let Some(entry) = cookbook::select_in_table(&table, sound, self.rng.as_mut())
                else {
                    return Ok(());
                };
                play_3d(
                    engine,
                    cache,
                    entity_id,
                    position,
                    entry.id,
                    entry.volume,
                    entry.priority,
                )
            }
            HookData::SoundTweaked { .. } => {
                let Some(t) = TweakedSoundHook::from_hook(hook) else {
                    return Ok(());
                };
                match t.try_roll(self.rng.as_mut()) {
                    Some((wave, volume)) => play_3d(
                        engine,
                        cache,
                        entity_id,
                        position,
                        wave,
                        volume,
                        t.authored_priority(),
                    ),
                    None => Ok(()),
                }
            }
            _ => Ok(()),
        }
    }

    /// A sound hook from a scene with no real position (the portal tunnel):
    /// played centred, not as an interface sound.
    pub fn on_ui_hook(
        &mut self,
        engine: &mut AudioEngine,
        cache: &SoundCache,
        entity_id: u32,
        hook: &HookData,
    ) -> Result<(), AudioError> {
        let (wave, volume, priority) = match *hook {
            HookData::Sound { id } => (id, 1.0, 0.0),
            HookData::SoundTable { sound } => {
                let Some(table) = self.table(cache, entity_id)? else {
                    return Ok(());
                };
                let Some(e) = cookbook::select_in_table(&table, sound, self.rng.as_mut()) else {
                    return Ok(());
                };
                (e.id, e.volume, e.priority)
            }
            HookData::SoundTweaked { .. } => {
                let Some(t) = TweakedSoundHook::from_hook(hook) else {
                    return Ok(());
                };
                let Some((wave, volume)) = t.try_roll(self.rng.as_mut()) else {
                    return Ok(());
                };
                (wave, volume, t.authored_priority())
            }
            _ => return Ok(()),
        };
        if wave == 0 {
            return Ok(());
        }
        if let Some(w) = cache.wave(wave)? {
            engine.play_ui_wave(wave, &w, volume, priority, false);
        }
        Ok(())
    }

    /// The server's sound event: the entity plays `sound_type` from its
    /// table at the wire volume, from its position.
    pub fn play_server_sound(
        &mut self,
        engine: &mut AudioEngine,
        cache: &SoundCache,
        entity_id: u32,
        position: Vec3,
        sound_type: u32,
        wire_volume: f32,
    ) -> Result<(), AudioError> {
        let table_id = self.sound_table_of(entity_id);
        if table_id == 0 {
            self.wire_probe(entity_id, sound_type, "no-sound-table");
            return Ok(());
        }
        let Some(table) = cache.sound_table(table_id)? else {
            self.wire_probe(
                entity_id,
                sound_type,
                &format!("table-0x{table_id:08X}-unloadable"),
            );
            return Ok(());
        };
        let Some(entry) = cookbook::select_in_table(&table, sound_type, self.rng.as_mut()) else {
            self.wire_probe(entity_id, sound_type, "slot-missing-or-gate-silence");
            return Ok(());
        };
        self.wire_probe(
            entity_id,
            sound_type,
            &format!(
                "play wave=0x{:08X} pos=({:.0},{:.0},{:.0})",
                entry.id, position.x, position.y, position.z
            ),
        );
        play_3d(
            engine,
            cache,
            entity_id,
            position,
            entry.id,
            wire_volume,
            entry.priority,
        )
    }

    fn wire_probe(&self, entity_id: u32, sound_type: u32, outcome: &str) {
        if self.probe_wire {
            eprintln!("[sound-wire] local=0x{entity_id:08X} slot=0x{sound_type:02X} {outcome}");
        }
    }
}

fn play_3d(
    engine: &mut AudioEngine,
    cache: &SoundCache,
    entity_id: u32,
    position: Vec3,
    wave_id: u32,
    volume: f32,
    priority: f32,
) -> Result<(), AudioError> {
    if wave_id == 0 {
        return Ok(());
    }
    if let Some(w) = cache.wave(wave_id)? {
        engine.play_3d_wave(entity_id, wave_id, &w, position, volume, priority);
    }
    Ok(())
}

/// Interface sounds from the interface sound bank.
pub struct UiSounds {
    table_id: u32,
    table: Option<Arc<SoundTable>>,
    table_missing: bool,
    rng: Box<dyn SoundRandom>,
}

impl std::fmt::Debug for UiSounds {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("UiSounds")
            .field("table_id", &self.table_id)
            .finish_non_exhaustive()
    }
}

impl UiSounds {
    /// Interface sounds from bank `table_id` (0: none).
    #[must_use]
    pub fn new(table_id: u32, rng: Box<dyn SoundRandom>) -> Self {
        Self {
            table_id,
            table: None,
            table_missing: false,
            rng,
        }
    }

    /// The bank's sound table id.
    #[must_use]
    pub fn table_id(&self) -> u32 {
        self.table_id
    }

    /// Play one interface sound. `Ok(false)` when the bank is absent, the
    /// slot is unauthored, its play gate says silence, or no voice is free.
    pub fn play(
        &mut self,
        engine: &mut AudioEngine,
        cache: &SoundCache,
        sound: SoundId,
    ) -> Result<bool, AudioError> {
        if self.table_id == 0 || self.table_missing {
            return Ok(false);
        }
        if self.table.is_none() {
            self.table = cache.sound_table(self.table_id)?;
            if self.table.is_none() {
                self.table_missing = true;
                return Ok(false);
            }
        }
        let Some(table) = &self.table else {
            return Ok(false);
        };
        let Some(entry) = cookbook::select_in_table(table, sound.value(), self.rng.as_mut()) else {
            return Ok(false);
        };
        if entry.id == 0 {
            return Ok(false);
        }
        let (id, volume, priority) = (entry.id, entry.volume, entry.priority);
        let Some(w) = cache.wave(id)? else {
            return Ok(false);
        };
        Ok(engine.play_ui_wave(id, &w, volume, priority, true))
    }

    /// The sound for a server environment-change code, if it is a sound cue.
    pub fn play_environ_cue(
        &mut self,
        engine: &mut AudioEngine,
        cache: &SoundCache,
        change_type: u32,
    ) -> Result<bool, AudioError> {
        match environ_sound(change_type) {
            Some(sound) => self.play(engine, cache, sound),
            None => Ok(false),
        }
    }
}

/// Where the listener is, for the ambient walk.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct AmbientListenerPose {
    /// The cell the listener stands in.
    pub obj_cell_id: u32,
    /// World position (for one-shot placement).
    pub position: Vec3,
    /// Position inside the listener's landblock.
    pub landblock_local_position: Vec3,
    /// An indoor cell that can see outside (then ambients still play).
    pub seen_outside: bool,
}

/// The region's ambient sounds around the listener.
pub struct AmbientSounds {
    scheduler: AmbientSoundScheduler,
    rng: Box<dyn SoundRandom>,
    firings: Vec<AmbientSoundFiring>,
    region: Option<AmbientRegion>,
    landblocks: Box<dyn FnMut(u32) -> Option<LandblockTerrain>>,
    current_obj_cell: u32,
    listener_position: Vec3,
    clock: f64,
    suspended: bool,
}

impl std::fmt::Debug for AmbientSounds {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("AmbientSounds")
            .field("scheduler", &self.scheduler)
            .field("current_obj_cell", &self.current_obj_cell)
            .field("clock", &self.clock)
            .finish_non_exhaustive()
    }
}

impl AmbientSounds {
    /// No region yet: silent.
    #[must_use]
    pub fn new(rng: Box<dyn SoundRandom>) -> Self {
        Self {
            scheduler: AmbientSoundScheduler::new(),
            rng,
            firings: Vec::new(),
            region: None,
            landblocks: Box::new(|_| None),
            current_obj_cell: 0,
            listener_position: Vec3::ZERO,
            clock: 0.0,
            suspended: false,
        }
    }

    /// The scheduler (diagnostics).
    #[must_use]
    pub fn scheduler(&self) -> &AmbientSoundScheduler {
        &self.scheduler
    }

    /// Install the region and the source of landblock terrain words.
    pub fn install_region(
        &mut self,
        region: AmbientRegion,
        landblocks: Box<dyn FnMut(u32) -> Option<LandblockTerrain>>,
    ) {
        self.region = Some(region);
        self.landblocks = landblocks;
        self.current_obj_cell = 0;
        self.scheduler.clear();
    }

    /// Follow the listener. The soundscape is rebuilt when the listener's
    /// cell changes; an indoor cell that cannot see outside is silent.
    pub fn observe_listener(&mut self, pose: AmbientListenerPose) {
        self.listener_position = pose.position;
        let Some(region) = &self.region else {
            return;
        };
        if pose.obj_cell_id == self.current_obj_cell {
            return;
        }
        self.current_obj_cell = pose.obj_cell_id;
        if pose.obj_cell_id & 0xFFFF >= 0x0100 && !pose.seen_outside {
            self.scheduler.clear();
            return;
        }
        crate::ambient::rebuild_ambient(
            &mut self.scheduler,
            region,
            (pose.obj_cell_id >> 16 << 16) | 0xFFFF,
            pose.landblock_local_position,
            &mut *self.landblocks,
            self.clock,
            self.rng.as_mut(),
        );
    }

    /// Advance the clock and play whatever is due.
    pub fn tick(
        &mut self,
        engine: &mut AudioEngine,
        cache: &SoundCache,
        delta_seconds: f64,
    ) -> Result<(), AudioError> {
        if delta_seconds > 0.0 {
            self.clock += delta_seconds;
        }
        if self.suspended || self.region.is_none() {
            return Ok(());
        }
        self.firings.clear();
        self.scheduler.tick(
            self.clock,
            &mut self.firings,
            self.listener_position,
            self.rng.as_mut(),
        );
        let firings = std::mem::take(&mut self.firings);
        let mut result = Ok(());
        for firing in &firings {
            if let Err(e) = self.play(engine, cache, firing) {
                result = Err(e);
                break;
            }
        }
        self.firings = firings;
        result
    }

    fn play(
        &mut self,
        engine: &mut AudioEngine,
        cache: &SoundCache,
        firing: &AmbientSoundFiring,
    ) -> Result<(), AudioError> {
        let instance = &self.scheduler.instances()[firing.instance];
        let (table_id, sound) = (instance.sound_table_id(), instance.descriptor().sound);
        let Some(table) = cache.sound_table(table_id)? else {
            return Ok(());
        };
        let Some(entry) = cookbook::select_in_table(&table, sound, self.rng.as_mut()) else {
            return Ok(());
        };
        if entry.id == 0 {
            return Ok(());
        }
        let Some(w) = cache.wave(entry.id)? else {
            return Ok(());
        };
        // The ambient volume is applied here and again by the engine, as
        // the game does on this path (as OpenAC does).
        let volume = firing.volume * engine.ambient_volume();
        match firing.position {
            Some(p) => engine.play_ambient_3d_wave(entry.id, &w, p, volume, entry.priority),
            None => engine.play_ambient_from_center(entry.id, &w, volume, entry.priority),
        };
        Ok(())
    }

    /// Stop scheduling (and forget the soundscape) until resumed.
    pub fn suspend(&mut self) {
        self.suspended = true;
        self.stop_all();
    }

    /// Schedule again.
    pub fn resume(&mut self) {
        self.suspended = false;
    }

    /// Drop every instance and deadline (world teardown).
    pub fn stop_all(&mut self) {
        self.scheduler.clear();
        self.current_obj_cell = 0;
    }
}

/// The audio a window owns: create on attach, drop on detach.
#[derive(Debug)]
pub struct AudioGraph {
    /// The engine (device, voices, knobs).
    pub engine: AudioEngine,
    /// Decoded waves and sound tables.
    pub cache: Arc<SoundCache>,
    /// Animation-hook and server sounds.
    pub hooks: HookSounds,
    /// Interface sounds.
    pub ui: UiSounds,
    /// Region ambient sounds.
    pub ambient: AmbientSounds,
    /// A character is in the session's world (the session gate).
    in_world: bool,
    /// A login or portal reveal holds the world back until its
    /// destination is shown (the world-generation quiescence).
    generation_quiesced: bool,
}

impl AudioGraph {
    /// Open the default device and build the graph. `Ok(None)` when audio
    /// is switched off (`OPENAC_NO_AUDIO=1`). An error when the device cannot
    /// be opened or the dats cannot be read; the caller runs without sound.
    pub fn create(
        dats: Arc<dyn SoundDats>,
        mixer: AudioMixerOptions,
        launch: AudioLaunchOptions,
    ) -> Result<Option<Self>, AudioError> {
        if launch.no_audio {
            return Ok(None);
        }
        let mut engine = AudioEngine::open(mixer)?;
        engine.set_probe_voices(launch.probe_voices);
        engine.set_probe_plays(launch.probe_plays);
        let mut graph = Self::with_engine(engine, Arc::new(SoundCache::new(dats)))?;
        graph.hooks.probe_wire = launch.probe_wire_sounds;
        Ok(Some(graph))
    }

    /// A graph around an existing engine and cache.
    pub fn with_engine(engine: AudioEngine, cache: Arc<SoundCache>) -> Result<Self, AudioError> {
        let ui_table = resolve_ui_sound_table(cache.dats().as_ref())?;
        let rng = || -> Box<dyn SoundRandom> { Box::new(SeededSoundRandom::from_entropy()) };
        Ok(Self {
            engine,
            cache,
            hooks: HookSounds::new(rng()),
            ui: UiSounds::new(ui_table, rng()),
            ambient: AmbientSounds::new(rng()),
            in_world: true,
            generation_quiesced: false,
        })
    }

    /// An animation hook on an entity at a world position.
    pub fn on_hook(
        &mut self,
        entity_id: u32,
        position: Vec3,
        hook: &HookData,
    ) -> Result<(), AudioError> {
        self.hooks
            .on_hook(&mut self.engine, &self.cache, entity_id, position, hook)
    }

    /// A sound hook from a scene with no position (the portal tunnel).
    pub fn on_ui_hook(&mut self, entity_id: u32, hook: &HookData) -> Result<(), AudioError> {
        self.hooks
            .on_ui_hook(&mut self.engine, &self.cache, entity_id, hook)
    }

    /// The server's sound event (`0xF750`).
    pub fn play_server_sound(
        &mut self,
        entity_id: u32,
        position: Vec3,
        sound_type: u32,
        volume: f32,
    ) -> Result<(), AudioError> {
        self.hooks.play_server_sound(
            &mut self.engine,
            &self.cache,
            entity_id,
            position,
            sound_type,
            volume,
        )
    }

    /// An interface sound.
    pub fn play_ui(&mut self, sound: SoundId) -> Result<bool, AudioError> {
        self.ui.play(&mut self.engine, &self.cache, sound)
    }

    /// The server's environment change (`AdminEnvirons`), when it is a sound.
    pub fn play_environ_cue(&mut self, change_type: u32) -> Result<bool, AudioError> {
        self.ui
            .play_environ_cue(&mut self.engine, &self.cache, change_type)
    }

    /// Per frame: the listener (the camera) and the ambient clock.
    pub fn tick(
        &mut self,
        listener_position: Vec3,
        listener_heading_degrees: f32,
        ambient_pose: Option<AmbientListenerPose>,
        delta_seconds: f64,
    ) -> Result<(), AudioError> {
        self.engine
            .set_listener(listener_position, listener_heading_degrees);
        if let Some(pose) = ambient_pose {
            self.ambient.observe_listener(pose);
        }
        self.ambient
            .tick(&mut self.engine, &self.cache, delta_seconds)
    }

    /// The session is leaving the world (logout, character select, portal):
    /// world sounds stop, interface sounds finish.
    pub fn suspend_for_session_reset(&mut self) {
        self.engine.suspend_world_audio();
        self.ambient.stop_all();
    }

    /// The session entered the world again. World sounds stay quiet while
    /// a reveal holds the world back.
    pub fn resume_for_world_entry(&mut self) {
        if !self.generation_quiesced {
            self.engine.resume_world_audio();
        }
    }

    /// Follow the session's world: a character in it resumes world sounds,
    /// none (character selection, after a log off, a new generation)
    /// suspends them. Interface sounds play either way.
    pub fn set_in_world(&mut self, in_world: bool) {
        self.in_world = in_world;
        if in_world {
            self.resume_for_world_entry();
        } else {
            self.suspend_for_session_reset();
        }
    }

    /// Follow the world reveal: from the start of a login or portal reveal
    /// until its destination is shown the world is quiet (world voices
    /// stop and none starts). Interface sounds, the portal cues among
    /// them, play throughout. Only edges act: a reveal that supersedes
    /// another suspends nothing more, and the end of the last one resumes
    /// once.
    pub fn set_world_generation_quiesced(&mut self, quiesced: bool) {
        if self.generation_quiesced == quiesced {
            return;
        }
        self.generation_quiesced = quiesced;
        if quiesced {
            self.engine.suspend_world_audio();
        } else if self.in_world {
            self.engine.resume_world_audio();
        }
    }
}

#[cfg(test)]
mod tests {
    use ac_dat::objects::{SoundData, SoundEntry};

    use super::*;
    use crate::cache::fake::{FakeDats, pcm_wave};
    use crate::mixer::Mixer;
    use crate::random::scripted::Scripted;

    const TABLE: u32 = 0x2000_0001;
    const WAVE: u32 = 0x0A00_0001;

    fn dats() -> FakeDats {
        let entry = SoundEntry {
            id: WAVE,
            priority: 0.5,
            probability: 1.0,
            volume: 0.7,
        };
        FakeDats {
            waves: [(WAVE, pcm_wave(WAVE, 64))].into_iter().collect(),
            tables: [(
                TABLE,
                SoundTable {
                    id: TABLE,
                    hash_key: 0,
                    hashes: Vec::new(),
                    sounds: vec![
                        (
                            SoundId::Footstep1.value(),
                            SoundData {
                                entries: vec![entry],
                                unknown: 0,
                            },
                        ),
                        (
                            SoundId::UiEnterPortal.value(),
                            SoundData {
                                entries: vec![entry],
                                unknown: 0,
                            },
                        ),
                    ],
                },
            )]
            .into_iter()
            .collect(),
            ..FakeDats::default()
        }
    }

    fn graph() -> (AudioGraph, Mixer) {
        let (engine, mixer) = AudioEngine::detached(AudioMixerOptions::default());
        let cache = Arc::new(SoundCache::new(Arc::new(dats())));
        let mut g = AudioGraph::with_engine(engine, cache).unwrap();
        g.hooks = HookSounds::new(Box::new(Scripted::lenient(&[])));
        g.ui = UiSounds::new(TABLE, Box::new(Scripted::lenient(&[])));
        (g, mixer)
    }

    fn playing(m: &mut Mixer) -> usize {
        m.apply_commands();
        m.active_voices()
    }

    #[test]
    fn a_sound_table_hook_plays_the_entitys_entry() {
        let (mut g, mut m) = graph();
        let hook = HookData::SoundTable {
            sound: SoundId::Footstep1.value(),
        };
        g.on_hook(7, Vec3::ZERO, &hook).unwrap();
        assert_eq!(playing(&mut m), 0, "no table known for the entity yet");
        g.hooks.set_sound_table(7, TABLE);
        g.on_hook(7, Vec3::ZERO, &hook).unwrap();
        assert_eq!(playing(&mut m), 1);
    }

    #[test]
    fn a_bare_sound_hook_plays_its_wave() {
        let (mut g, mut m) = graph();
        g.on_hook(7, Vec3::ZERO, &HookData::Sound { id: WAVE })
            .unwrap();
        assert_eq!(playing(&mut m), 1);
    }

    #[test]
    fn a_server_sound_needs_the_entitys_table() {
        let (mut g, mut m) = graph();
        g.play_server_sound(7, Vec3::ZERO, SoundId::Footstep1.value(), 1.0)
            .unwrap();
        assert_eq!(playing(&mut m), 0);
        g.hooks.set_sound_table(7, TABLE);
        g.play_server_sound(7, Vec3::ZERO, SoundId::Footstep1.value(), 1.0)
            .unwrap();
        assert_eq!(playing(&mut m), 1);
    }

    #[test]
    fn interface_sounds_play_from_the_bank() {
        let (mut g, mut m) = graph();
        assert!(g.play_ui(SoundId::UiEnterPortal).unwrap());
        assert!(!g.play_ui(SoundId::UiExitPortal).unwrap());
        assert_eq!(playing(&mut m), 1);
    }

    #[test]
    fn non_sound_hooks_are_ignored() {
        let (mut g, mut m) = graph();
        g.on_hook(7, Vec3::ZERO, &HookData::AnimationDone).unwrap();
        assert!(!is_sound_hook(&HookData::AnimationDone));
        assert!(is_sound_hook(&HookData::Sound { id: 1 }));
        assert_eq!(playing(&mut m), 0);
    }

    #[test]
    fn a_session_reset_silences_the_world_and_spares_the_interface() {
        let (mut g, mut m) = graph();
        g.on_hook(7, Vec3::ZERO, &HookData::Sound { id: WAVE })
            .unwrap();
        assert!(g.play_ui(SoundId::UiEnterPortal).unwrap());
        g.suspend_for_session_reset();
        assert_eq!(playing(&mut m), 1);
        g.on_hook(7, Vec3::ZERO, &HookData::Sound { id: WAVE })
            .unwrap();
        assert_eq!(playing(&mut m), 1);
        g.resume_for_world_entry();
        g.on_hook(7, Vec3::ZERO, &HookData::Sound { id: WAVE })
            .unwrap();
        assert_eq!(playing(&mut m), 2);
    }

    // OpenAC WorldAudioSessionGate: no world sounds at character selection
    // or after a log off; the ambient soundscape is dropped and rebuilt on
    // the next cell once a character is in the world again.
    #[test]
    fn world_sounds_play_only_while_a_character_is_in_the_world() {
        let (mut g, mut m) = graph();
        g.set_in_world(false);
        g.on_hook(7, Vec3::ZERO, &HookData::Sound { id: WAVE })
            .unwrap();
        assert_eq!(playing(&mut m), 0, "no world sound outside the world");
        assert!(g.play_ui(SoundId::UiEnterPortal).unwrap());
        assert_eq!(playing(&mut m), 1, "interface sounds still play");
        g.set_in_world(true);
        g.on_hook(7, Vec3::ZERO, &HookData::Sound { id: WAVE })
            .unwrap();
        assert_eq!(playing(&mut m), 2);
        g.set_in_world(false);
        assert_eq!(playing(&mut m), 1, "leaving the world silences its sounds");
        assert_eq!(g.ambient.scheduler().queued_count(), 0);
    }

    // OpenAC WorldGenerationQuiescence (the start of a reveal suspends the
    // world's audio, its release resumes it) and
    // OpenAlAudioEngine.SuspendWorldAudio (the portal-enter cue sounds at
    // that moment and finishes).
    #[test]
    fn a_reveal_quiets_the_world_until_its_destination_is_shown_and_spares_the_interface() {
        let (mut g, mut m) = graph();
        g.set_in_world(true);
        g.on_hook(7, Vec3::ZERO, &HookData::Sound { id: WAVE })
            .unwrap();
        assert_eq!(playing(&mut m), 1);
        g.set_world_generation_quiesced(true);
        assert_eq!(playing(&mut m), 0, "the world's voices stop at the start");
        assert!(g.play_ui(SoundId::UiEnterPortal).unwrap());
        g.on_hook(7, Vec3::ZERO, &HookData::Sound { id: WAVE })
            .unwrap();
        assert_eq!(playing(&mut m), 1, "only the portal cue sounds");
        g.set_world_generation_quiesced(false);
        g.on_hook(7, Vec3::ZERO, &HookData::Sound { id: WAVE })
            .unwrap();
        assert_eq!(playing(&mut m), 2, "the world sounds once it is shown");
    }

    // OpenAC WorldGenerationQuiescenceTests
    // .SupersededReveal_StaysSilentAndOnlyLatestRuntimeEdgeResumesAudio.
    #[test]
    fn a_superseded_reveal_stays_quiet_until_the_latest_one_ends() {
        let (mut g, mut m) = graph();
        g.set_world_generation_quiesced(true);
        // A portal begins while the login reveal still holds the world.
        g.set_world_generation_quiesced(true);
        g.on_hook(7, Vec3::ZERO, &HookData::Sound { id: WAVE })
            .unwrap();
        assert_eq!(playing(&mut m), 0);
        g.set_world_generation_quiesced(false);
        g.on_hook(7, Vec3::ZERO, &HookData::Sound { id: WAVE })
            .unwrap();
        assert_eq!(playing(&mut m), 1);
    }

    // The two gates together: entering the world during a reveal does not
    // end the reveal's quiet, and the end of a reveal with no character in
    // the world does not bring the world back.
    #[test]
    fn the_reveal_and_the_session_gate_both_hold_the_world_quiet() {
        let (mut g, mut m) = graph();
        let world_sound = |g: &mut AudioGraph| {
            g.on_hook(7, Vec3::ZERO, &HookData::Sound { id: WAVE })
                .unwrap();
        };
        g.set_world_generation_quiesced(true);
        g.set_in_world(true);
        world_sound(&mut g);
        assert_eq!(playing(&mut m), 0, "a reveal still holds the world");
        g.set_in_world(false);
        g.set_world_generation_quiesced(false);
        world_sound(&mut g);
        assert_eq!(playing(&mut m), 0, "no character in the world");
        g.set_in_world(true);
        world_sound(&mut g);
        assert_eq!(playing(&mut m), 1);
    }

    #[test]
    fn indoors_is_silent_unless_it_sees_outside() {
        let mut a = AmbientSounds::new(Box::new(Scripted::lenient(&[])));
        a.install_region(
            AmbientRegion {
                terrain_scene_types: vec![vec![0]],
                scene_stb_index: vec![0],
                stb_desc: vec![ac_dat::objects::AmbientStbDesc {
                    stb_id: TABLE,
                    ambient_sounds: vec![ac_dat::objects::AmbientSoundDesc {
                        sound_type: SoundId::Footstep1.value(),
                        volume: 1.0,
                        base_chance: 0.0,
                        min_rate: 5.0,
                        max_rate: 0.0,
                    }],
                }],
            },
            Box::new(|_| Some([0; 81])),
        );
        let pose = |cell: u32, seen_outside| AmbientListenerPose {
            obj_cell_id: cell,
            position: Vec3::ZERO,
            landblock_local_position: Vec3::new(96.0, 96.0, 0.0),
            seen_outside,
        };
        a.observe_listener(pose(0xA9B4_0031, false));
        assert_eq!(a.scheduler().queued_count(), 1);
        a.observe_listener(pose(0xA9B4_0105, false));
        assert_eq!(a.scheduler().queued_count(), 0);
        a.observe_listener(pose(0xA9B4_0106, true));
        assert_eq!(a.scheduler().queued_count(), 1);
    }
}
