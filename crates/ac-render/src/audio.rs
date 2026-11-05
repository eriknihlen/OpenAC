//! The window's sound: an [`AudioGraph`] built when the window attaches
//! and dropped when it detaches, fed each frame with the camera (the
//! listener), the player's cell (the region's ambient sounds), the scene's
//! sound tables and the server's sound events.
//!
//! A headless host has no window and so no sound.

use std::collections::HashMap;
use std::sync::Arc;
use std::time::Instant;

use ac_audio::ambient::{AmbientRegion, LandblockTerrain};
use ac_audio::gain::compass_heading_degrees;
use ac_audio::{
    AmbientListenerPose, AudioError, AudioGraph, AudioMixerOptions, AudioSettings, SoundDats,
};
use ac_dat::objects::{EnumIdMap, HookData, LandBlock, SoundTable, Wave};
use glam::Vec3;

use crate::content::{Content, ContentSlot};

pub use ac_audio::AudioLaunchOptions;
pub use ac_audio::options::mixer_command_arguments;

/// The sound's startup switches, read through `var` (the process
/// environment or a test map).
pub fn launch_options(var: impl Fn(&str) -> Option<String>) -> AudioLaunchOptions {
    AudioLaunchOptions::from_vars(var)
}

/// What the window's sound starts with.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct AudioOptions {
    /// The startup switches (`OPENAC_NO_AUDIO`, the probes).
    pub launch: AudioLaunchOptions,
    /// The player's volumes and switches.
    pub settings: AudioSettings,
    /// The voice pool.
    pub mixer: AudioMixerOptions,
}

/// The stored sound settings as the engine takes them.
#[must_use]
pub fn audio_settings_of(s: &ac_host::settings::AudioSettings) -> AudioSettings {
    AudioSettings {
        master: s.master,
        sfx: s.sfx,
        ambient: s.ambient,
        sound_features: s.sound_features,
        sfx_enabled: s.sfx_enabled,
        ambient_enabled: s.ambient_enabled,
        interface_enabled: s.interface_enabled,
        interface_volume: s.interface_volume,
        play_sound_only_when_active: s.play_sound_only_when_active,
    }
}

/// The stored mixer settings as the engine takes them.
#[must_use]
pub fn mixer_options_of(m: &ac_host::settings::AudioMixerSettings) -> AudioMixerOptions {
    AudioMixerOptions {
        retail_mixer: m.retail_mixer,
        voice_count: m.voice_count,
        use_authored_priority: m.use_authored_priority,
        max_voices_per_wave: m.max_voices_per_wave,
    }
    .normalized()
}

/// The process's dats as the sound cache reads them (it keeps its own
/// decoded waves, so it reads past the object caches).
struct ContentSounds(Arc<Content>);

impl SoundDats for ContentSounds {
    fn wave(&self, id: u32) -> Result<Option<Wave>, AudioError> {
        self.0.dats.uncached().wave(id)
    }
    fn sound_table(&self, id: u32) -> Result<Option<SoundTable>, AudioError> {
        self.0.dats.uncached().sound_table(id)
    }
    fn enum_id_map(&self, id: u32) -> Result<Option<EnumIdMap>, AudioError> {
        self.0.dats.uncached().enum_id_map(id)
    }
    fn master_map_id(&self) -> u32 {
        self.0.dats.uncached().master_map_id()
    }
}

/// The landblock terrain words the ambient walk reads (`0xXXYYFFFF`).
fn landblock_terrain(content: &Content, landblock_id: u32) -> Option<LandblockTerrain> {
    let block = content.dats.get::<LandBlock>(landblock_id).ok()??;
    Some(block.terrain.map(|t| t.0))
}

/// Where the player stands, for the ambient sounds.
#[derive(Debug, Clone, Copy, PartialEq)]
pub(crate) struct PlayerPlace {
    /// Full cell id.
    pub cell_id: u32,
    /// Position, drawing frame.
    pub position: Vec3,
    /// Position inside its landblock.
    pub local: Vec3,
    /// An interior cell that can see outside.
    pub seen_outside: bool,
}

/// The window's audio. See the module documentation.
pub(crate) struct WindowAudio {
    graph: AudioGraph,
    content: Arc<Content>,
    settings: AudioSettings,
    /// Server id to the sound table registered for it.
    tables: HashMap<u32, u32>,
    muted: bool,
    focused: bool,
    last_tick: Option<Instant>,
}

impl WindowAudio {
    /// Open the default output device. `Ok(None)` when sound is switched
    /// off; an error when the device or the dats cannot be opened (the
    /// window then runs silent).
    pub(crate) fn open(
        content: &ContentSlot,
        options: &AudioOptions,
    ) -> Result<Option<Self>, String> {
        if options.launch.no_audio {
            return Ok(None);
        }
        let content = content.get()?;
        let dats: Arc<dyn SoundDats> = Arc::new(ContentSounds(Arc::clone(&content)));
        let Some(mut graph) =
            AudioGraph::create(dats, options.mixer, options.launch).map_err(|e| e.to_string())?
        else {
            return Ok(None);
        };
        graph.engine.apply_settings(&options.settings);
        graph
            .engine
            .set_focus_muted(options.settings.focus_muted(true));
        Ok(Some(Self {
            graph,
            content,
            settings: options.settings,
            tables: HashMap::new(),
            muted: false,
            focused: true,
            last_tick: None,
        }))
    }

    /// The output device's description, for the report.
    pub(crate) fn device(&self) -> String {
        self.graph
            .engine
            .device_info()
            .map_or_else(|| "no output device".to_owned(), |d| format!("{d:?}"))
    }

    /// The player changed the sound settings (the options window): the
    /// volumes and switches at once, the focus mute re-judged.
    pub(crate) fn apply_settings(&mut self, settings: AudioSettings) {
        self.settings = settings;
        self.graph.engine.apply_settings(&settings);
        self.graph
            .engine
            .set_focus_muted(settings.focus_muted(self.focused));
    }

    /// The player changed the mixer settings.
    pub(crate) fn apply_mixer(&mut self, mixer: AudioMixerOptions) {
        self.graph.engine.apply_mixer_options(mixer);
    }

    /// The session's world changed: world sounds play only while a
    /// character is in it (OpenAC's world-audio session gate).
    pub(crate) fn set_in_world(&mut self, in_world: bool) {
        self.graph.set_in_world(in_world);
    }

    /// A login or portal reveal holds the world back (from its start until
    /// the destination is shown): the world is quiet meanwhile (OpenAC's
    /// world-generation quiescence).
    pub(crate) fn set_world_held(&mut self, held: bool) {
        self.graph.set_world_generation_quiesced(held);
    }

    /// Ctrl+M: silence everything or sound again. Returns the new state
    /// (true: muted).
    pub(crate) fn toggle_mute(&mut self) -> bool {
        self.muted = !self.muted;
        self.graph.engine.set_muted(self.muted);
        self.muted
    }

    /// The window gained or lost the focus: with "play sound only when
    /// active" on, an unfocused window is silent.
    pub(crate) fn focus_changed(&mut self, focused: bool) {
        self.focused = focused;
        self.graph
            .engine
            .set_focus_muted(self.settings.focus_muted(focused));
    }

    /// Install the region's ambient sounds.
    pub(crate) fn install_region(&mut self, region: AmbientRegion) {
        let content = Arc::clone(&self.content);
        self.graph
            .ambient
            .install_region(region, Box::new(move |id| landblock_terrain(&content, id)));
    }

    /// Follow the scene's objects' sound tables (by server id).
    pub(crate) fn sync_sound_tables<'a>(
        &mut self,
        objects: impl Iterator<Item = (u32, Option<u32>)> + 'a,
    ) {
        let mut seen = HashMap::new();
        for (guid, table) in objects {
            let Some(table) = table.filter(|&t| t != 0) else {
                continue;
            };
            seen.insert(guid, table);
            if self.tables.get(&guid) != Some(&table) {
                self.graph.hooks.set_sound_table(guid, table);
            }
        }
        for guid in self.tables.keys() {
            if !seen.contains_key(guid) {
                self.graph.hooks.remove_entity(*guid);
            }
        }
        self.tables = seen;
    }

    /// The server's sound event from object `guid` at `position`.
    pub(crate) fn play_server_sound(
        &mut self,
        guid: u32,
        position: Vec3,
        sound_type: u32,
        volume: f32,
    ) {
        if let Err(error) = self
            .graph
            .play_server_sound(guid, position, sound_type, volume)
        {
            eprintln!("audio: server sound {sound_type:#x}: {error}");
        }
    }

    /// A sound an animation fired: object `guid` (its sound table picks a
    /// sound-table hook's sound) at `position`, drawing frame.
    pub(crate) fn play_hook(&mut self, guid: u32, position: Vec3, hook: &HookData) {
        if let Err(error) = self.graph.on_hook(guid, position, hook) {
            eprintln!("audio: animation sound: {error}");
        }
    }

    /// An interface sound (the portal-space cues), centred.
    pub(crate) fn play_ui(&mut self, sound: ac_audio::SoundId) {
        if let Err(error) = self.graph.play_ui(sound) {
            eprintln!("audio: interface sound {sound:?}: {error}");
        }
    }

    /// A sound hook of a scene with no place in the world (the portal
    /// tunnel's animation), centred.
    pub(crate) fn play_ui_hook(&mut self, hook: &ac_dat::objects::HookData) {
        /// The tunnel's owner id (it is no world object).
        const TUNNEL_OWNER: u32 = 0xFFFF_FF01;
        if let Err(error) = self.graph.on_ui_hook(TUNNEL_OWNER, hook) {
            eprintln!("audio: portal tunnel sound: {error}");
        }
    }

    /// The server's environment change, when it is a sound cue.
    pub(crate) fn play_environ_cue(&mut self, change_type: u32) {
        if let Err(error) = self.graph.play_environ_cue(change_type) {
            eprintln!("audio: environment cue {change_type:#x}: {error}");
        }
    }

    /// Per frame: the listener is the camera (`eye`, looking along
    /// `look`), the ambient follows the player.
    pub(crate) fn tick(
        &mut self,
        camera: Option<(Vec3, Vec3)>,
        player: Option<PlayerPlace>,
        now: Instant,
    ) {
        let dt = self
            .last_tick
            .replace(now)
            .map_or(0.0, |t| now.saturating_duration_since(t).as_secs_f64());
        let (eye, heading) = match camera {
            Some((eye, look)) => {
                let flat = Vec3::new(look.x, look.y, 0.0);
                let heading = if flat.length_squared() > 1e-8 {
                    compass_heading_degrees(eye, eye + flat)
                } else {
                    0.0
                };
                (eye, heading)
            }
            None => (Vec3::ZERO, 0.0),
        };
        let pose = player.map(|p| AmbientListenerPose {
            obj_cell_id: p.cell_id,
            position: p.position,
            landblock_local_position: p.local,
            seen_outside: p.seen_outside,
        });
        if let Err(error) = self.graph.tick(eye, heading, pose, dt) {
            eprintln!("audio: {error}");
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn sound_is_off_when_switched_off() {
        let options = AudioOptions {
            launch: AudioLaunchOptions {
                no_audio: true,
                ..AudioLaunchOptions::default()
            },
            ..AudioOptions::default()
        };
        // The content is never opened when sound is off.
        let slot = ContentSlot::new(std::path::PathBuf::from("no such directory"));
        assert!(matches!(WindowAudio::open(&slot, &options), Ok(None)));
        assert!(slot.get_if_open().is_none());
    }
}
