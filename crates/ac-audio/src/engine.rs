//! The audio engine: the voice pool, the listener, the volume knobs and the
//! play gates, in front of the CPU mixer on the device thread.

use std::sync::Arc;
use std::time::Instant;

use glam::Vec3;

use crate::error::AudioError;
use crate::gain::{self, VoiceMix};
use crate::mixer::{MIXER_SLOTS, Mixer, MixerLink, mixer_pair};
use crate::options::AudioMixerOptions;
use crate::output::{DeviceInfo, OutputDevice};
use crate::settings::AudioSettings;
use crate::voices::{ClaimRequest, VoicePool};
use crate::wave::WaveData;

/// Output rate of a detached engine's mixer (tests and offline use).
pub const DETACHED_OUTPUT_RATE: u32 = 44_100;

/// Plays sounds. A window host creates one (or none: headless, or
/// `OPENAC_NO_AUDIO`) and may drop it at any time; it holds nothing the
/// session needs.
///
/// Every sound is a one-shot whose level and pan are fixed when it starts
/// (see [`crate::gain`]); world sounds, ambient sounds and interface sounds
/// share one pool of voices.
#[derive(Debug)]
pub struct AudioEngine {
    voices: VoicePool,
    link: MixerLink,
    free_sources: Vec<u32>,
    device: Option<OutputDevice>,
    listener_position: Vec3,
    listener_heading_degrees: f32,
    master_volume: f32,
    sfx_volume: f32,
    ambient_volume: f32,
    muted: bool,
    focus_muted: bool,
    interface_enabled: bool,
    world_suspended: bool,
    clock: Instant,
    probe_voices: bool,
    last_probe_ms: Option<i64>,
    probe_plays: bool,
}

impl AudioEngine {
    /// Open the default output device on its own thread.
    pub fn open(mixer: AudioMixerOptions) -> Result<Self, AudioError> {
        let (link, sink) = mixer_pair(DETACHED_OUTPUT_RATE);
        let device = OutputDevice::open(sink)?;
        Ok(Self::with_link(mixer, link, Some(device)))
    }

    /// An engine with no device: the caller owns the [`Mixer`] and renders
    /// it (tests, offline rendering).
    #[must_use]
    pub fn detached(mixer: AudioMixerOptions) -> (Self, Mixer) {
        let (link, sink) = mixer_pair(DETACHED_OUTPUT_RATE);
        (Self::with_link(mixer, link, None), sink)
    }

    fn with_link(
        options: AudioMixerOptions,
        link: MixerLink,
        device: Option<OutputDevice>,
    ) -> Self {
        let mut voices = VoicePool::new(options);
        // Sources are mixer slots, numbered from 1 (0 means none).
        let mut free_sources: Vec<u32> = (1..=MIXER_SLOTS as u32).rev().collect();
        for v in voices.voices_mut() {
            v.source_id = free_sources.pop().unwrap_or(0);
        }
        Self {
            voices,
            link,
            free_sources,
            device,
            listener_position: Vec3::ZERO,
            listener_heading_degrees: 0.0,
            master_volume: 1.0,
            sfx_volume: 1.0,
            ambient_volume: 0.8,
            muted: false,
            focus_muted: false,
            interface_enabled: true,
            world_suspended: false,
            clock: Instant::now(),
            probe_voices: false,
            last_probe_ms: None,
            probe_plays: false,
        }
    }

    /// Turn the voice probe on or off (`OPENAC_PROBE_AUDIO_VOICES`).
    pub fn set_probe_voices(&mut self, on: bool) {
        self.probe_voices = on;
    }

    /// Turn the play probe on or off (`OPENAC_PROBE_AUDIO_PLAYS`).
    pub fn set_probe_plays(&mut self, on: bool) {
        self.probe_plays = on;
    }

    /// The device, when one is open.
    #[must_use]
    pub fn device_info(&self) -> Option<&DeviceInfo> {
        self.device.as_ref().map(OutputDevice::info)
    }

    /// The last error the device reported while running, or that the mixer
    /// thread has gone. `None` while all is well.
    #[must_use]
    pub fn device_error(&self) -> Option<String> {
        if let Some(e) = self.device.as_ref().and_then(OutputDevice::error) {
            return Some(e);
        }
        self.link
            .is_disconnected()
            .then(|| "the audio mixer has stopped".to_owned())
    }

    /// Output frames the mixer has produced so far (the device is pulling
    /// when this grows).
    #[must_use]
    pub fn frames_rendered(&self) -> u64 {
        self.link.frames_rendered()
    }

    /// Voices whose sound is still sounding.
    #[must_use]
    pub fn playing_voice_count(&self) -> usize {
        self.voices
            .voices()
            .iter()
            .filter(|v| {
                v.in_use && v.source_id != 0 && self.link.is_playing(v.source_id as usize - 1)
            })
            .count()
    }

    // ── knobs ────────────────────────────────────────────────────────────

    /// Master volume (OpenAC's addition), multiplied into every sound.
    pub fn set_master_volume(&mut self, v: f32) {
        self.master_volume = v;
    }
    /// Effects volume (world and interface sounds).
    pub fn set_sfx_volume(&mut self, v: f32) {
        self.sfx_volume = v;
    }
    /// Ambient volume.
    pub fn set_ambient_volume(&mut self, v: f32) {
        self.ambient_volume = v;
    }
    /// Master volume.
    #[must_use]
    pub fn master_volume(&self) -> f32 {
        self.master_volume
    }
    /// Effects volume.
    #[must_use]
    pub fn sfx_volume(&self) -> f32 {
        self.sfx_volume
    }
    /// Ambient volume.
    #[must_use]
    pub fn ambient_volume(&self) -> f32 {
        self.ambient_volume
    }

    /// "Disable Interface Sound", gating only genuine interface sounds.
    pub fn set_interface_enabled(&mut self, on: bool) {
        self.interface_enabled = on;
    }

    /// The mute key: silences the whole output without stopping voices.
    pub fn set_muted(&mut self, muted: bool) {
        self.muted = muted;
        self.link.set_output_gain(if muted { 0.0 } else { 1.0 });
    }

    /// Whether the output is muted.
    #[must_use]
    pub fn muted(&self) -> bool {
        self.muted
    }

    /// "Play sound only when active": while set, no new sound starts (the
    /// game's gate). Becoming set also cuts every sound already playing
    /// (OpenAC's addition); clearing it starts nothing by itself.
    pub fn set_focus_muted(&mut self, muted: bool) {
        if muted && !self.focus_muted {
            for i in 0..self.voices.len() {
                if self.voices.voices()[i].in_use {
                    self.silence(i);
                }
            }
        }
        if self.probe_plays && muted != self.focus_muted {
            eprintln!(
                "[probe-audio] focus mute {}",
                if muted { "on: no sound starts" } else { "off" }
            );
        }
        self.focus_muted = muted;
    }

    /// Whether new sounds are refused for lack of focus.
    #[must_use]
    pub fn focus_muted(&self) -> bool {
        self.focus_muted
    }

    /// Apply the player's sound settings (volumes, category switches,
    /// interface switch).
    pub fn apply_settings(&mut self, settings: &AudioSettings) {
        self.master_volume = settings.master;
        let (sfx, ambient) = settings.effective_category_volumes();
        self.sfx_volume = sfx;
        self.ambient_volume = ambient;
        self.interface_enabled = settings.interface_enabled;
    }

    /// The listener: the camera's position and compass heading (degrees).
    pub fn set_listener(&mut self, position: Vec3, heading_degrees: f32) {
        self.listener_position = position;
        self.listener_heading_degrees = heading_degrees;
    }

    /// The listener's position.
    #[must_use]
    pub fn listener_position(&self) -> Vec3 {
        self.listener_position
    }

    // ── mixer settings ───────────────────────────────────────────────────

    /// The mixer settings in force.
    #[must_use]
    pub fn mixer_options(&self) -> &AudioMixerOptions {
        self.voices.options()
    }

    /// Put new mixer settings in force without restarting: voices beyond
    /// the new count are silenced and their slots freed. Returns true (a
    /// running engine always takes them).
    pub fn apply_mixer_options(&mut self, options: AudioMixerOptions) -> bool {
        let link = &mut self.link;
        let free = &mut self.free_sources;
        let mut retired = Vec::new();
        self.voices.apply_options(
            options,
            |v| retired.push(v.source_id),
            || free.pop().unwrap_or(0),
        );
        for source in retired {
            if source != 0 {
                link.stop(source as usize - 1);
                free.push(source);
            }
        }
        true
    }

    /// Voices in the pool (for diagnostics).
    #[must_use]
    pub fn voice_count(&self) -> usize {
        self.voices.len()
    }

    // ── playing ──────────────────────────────────────────────────────────

    fn effect_master(&self) -> f32 {
        self.master_volume * self.sfx_volume
    }

    fn ambient_master(&self) -> f32 {
        self.master_volume * self.ambient_volume
    }

    /// A sound at a world position belonging to `owner_id`. False when it
    /// is gated, too quiet, unplayable or every voice is busy.
    pub fn play_3d_wave(
        &mut self,
        owner_id: u32,
        wave_id: u32,
        wave: &Arc<WaveData>,
        position: Vec3,
        volume: f32,
        priority: f32,
    ) -> bool {
        if self.world_suspended || self.focus_muted {
            return false;
        }
        let mix = self.mix_at(position, volume, self.effect_master());
        self.play_mixed(mix, owner_id, false, wave_id, wave, priority, "world")
    }

    /// A centred sound with no falloff, sharing the same voices. With
    /// `is_interface_sound` it obeys "Disable Interface Sound" and survives
    /// a world change; without (the portal tunnel's cues) it does neither.
    /// Interface sounds follow the effects volume, as the game's do.
    pub fn play_ui_wave(
        &mut self,
        wave_id: u32,
        wave: &Arc<WaveData>,
        volume: f32,
        priority: f32,
        is_interface_sound: bool,
    ) -> bool {
        if self.focus_muted || (is_interface_sound && !self.interface_enabled) {
            return false;
        }
        let mix = centred(volume, self.effect_master());
        self.play_mixed(mix, 0, true, wave_id, wave, priority, "ui")
    }

    /// An intermittent ambient sound from a position.
    pub fn play_ambient_3d_wave(
        &mut self,
        wave_id: u32,
        wave: &Arc<WaveData>,
        position: Vec3,
        volume: f32,
        priority: f32,
    ) -> bool {
        if self.world_suspended || self.focus_muted {
            return false;
        }
        let mix = self.mix_at(position, volume, self.ambient_master());
        self.play_mixed(mix, 0, false, wave_id, wave, priority, "ambient")
    }

    /// A continuous ambient bed, centred.
    pub fn play_ambient_from_center(
        &mut self,
        wave_id: u32,
        wave: &Arc<WaveData>,
        volume: f32,
        priority: f32,
    ) -> bool {
        if self.world_suspended || self.focus_muted {
            return false;
        }
        let mix = centred(volume, self.ambient_master());
        self.play_mixed(mix, 0, false, wave_id, wave, priority, "ambient-center")
    }

    fn mix_at(&self, position: Vec3, volume: f32, master: f32) -> VoiceMix {
        gain::mix(
            self.listener_position,
            self.listener_heading_degrees,
            position,
            volume,
            master,
            true,
        )
    }

    #[allow(clippy::too_many_arguments)]
    fn play_mixed(
        &mut self,
        mix: VoiceMix,
        owner_id: u32,
        is_interface: bool,
        wave_id: u32,
        wave: &Arc<WaveData>,
        priority: f32,
        what: &str,
    ) -> bool {
        if !mix.play || !wave.is_playable() || self.link.is_disconnected() {
            return false;
        }
        let now_ms = self.clock.elapsed().as_millis() as i64;
        let link = &self.link;
        let claim = self.voices.claim(
            |source| source != 0 && link.is_playing(source as usize - 1),
            ClaimRequest {
                owner_id,
                is_interface,
                authored_priority: priority,
                wave_id,
                now_ms,
            },
        );
        let Some(claim) = claim else {
            self.probe(&format!("dropped {what}"), wave_id, owner_id, now_ms);
            return false;
        };
        if claim.took_playing_voice {
            self.probe(&format!("stole for {what}"), wave_id, owner_id, now_ms);
        }
        let source = self.voices.voices()[claim.slot].source_id;
        if source == 0 {
            return false;
        }
        let linear = gain::linear_gain(mix.decibels);
        let started = self
            .link
            .play(source as usize - 1, wave.clone(), linear, mix.pan);
        if self.probe_plays {
            self.probe_play(what, wave_id, owner_id, claim.slot, mix, linear, started);
        }
        started
    }

    // Diagnostic probe: one line per sound handed to a voice, with its
    // level, pan and the knobs it was mixed with, so a run shows what the
    // speakers were given without anyone listening.
    #[allow(clippy::too_many_arguments)]
    fn probe_play(
        &self,
        what: &str,
        wave_id: u32,
        owner_id: u32,
        slot: usize,
        mix: VoiceMix,
        linear: f32,
        started: bool,
    ) {
        let options = self.voices.options();
        eprintln!(
            "[probe-audio] {} {what} wave=0x{wave_id:08X} owner=0x{owner_id:08X} voice={slot} \
             gain={}dB ({linear:.3}) pan={}; sounding {}/{} voices; master {:.2} sfx {:.2} \
             ambient {:.2}; muted {}; retail mixer {}, per-wave cap {}; frames {}",
            if started { "play" } else { "refused" },
            mix.decibels,
            mix.pan,
            self.playing_voice_count(),
            self.voices.len(),
            self.master_volume,
            self.sfx_volume,
            self.ambient_volume,
            self.muted,
            if options.retail_mixer { "on" } else { "off" },
            options.effective_max_voices_per_wave(),
            self.link.frames_rendered(),
        );
    }

    /// Stop the world: world voices go quiet and no world sound starts
    /// until [`Self::resume_world_audio`]. Interface sounds keep playing
    /// (the portal cue sounds at exactly this moment).
    pub fn suspend_world_audio(&mut self) {
        if self.probe_plays && !self.world_suspended {
            eprintln!("[probe-audio] world sounds suspended");
        }
        self.world_suspended = true;
        let world: Vec<usize> = self.voices.silenced_by_world_change().collect();
        for i in world {
            self.silence(i);
        }
    }

    /// Let world sounds start again.
    pub fn resume_world_audio(&mut self) {
        if self.probe_plays && self.world_suspended {
            eprintln!("[probe-audio] world sounds resumed");
        }
        self.world_suspended = false;
    }

    /// Silence every sound belonging to `owner_id` (an object that left).
    pub fn stop_all_for_owner(&mut self, owner_id: u32) {
        if owner_id == 0 {
            return;
        }
        for i in 0..self.voices.len() {
            let v = self.voices.voices()[i];
            if v.in_use && v.owner_id == owner_id {
                self.silence(i);
            }
        }
    }

    fn silence(&mut self, index: usize) {
        let source = self.voices.voices()[index].source_id;
        if source != 0 {
            self.link.stop(source as usize - 1);
        }
        self.voices.voices_mut()[index].vacate();
    }

    // Diagnostic probe: what every voice holds when a sound is dropped or
    // takes a playing voice, at most twice a second.
    fn probe(&mut self, what: &str, wave_id: u32, owner_id: u32, now_ms: i64) {
        if !self.probe_voices || self.last_probe_ms.is_some_and(|t| now_ms - t < 500) {
            return;
        }
        self.last_probe_ms = Some(now_ms);
        let mut line =
            format!("[probe-voices] {what} wave=0x{wave_id:08X} owner=0x{owner_id:08X}; voices:");
        for (i, v) in self.voices.voices().iter().enumerate() {
            let playing = v.source_id != 0 && self.link.is_playing(v.source_id as usize - 1);
            line.push_str(&format!(
                " [{i}] wave=0x{:08X} owner=0x{:08X}{} prio={:.2} state={} age={}ms",
                v.wave_id,
                v.owner_id,
                if v.is_interface { " ui" } else { "" },
                v.priority,
                if playing { "Playing" } else { "Stopped" },
                now_ms - v.spoke_at_ms,
            ));
        }
        eprintln!("{line}");
    }
}

fn centred(volume: f32, master: f32) -> VoiceMix {
    match gain::attenuation(0.0, volume, master) {
        Some(decibels) => VoiceMix {
            play: true,
            decibels,
            pan: 0,
        },
        None => VoiceMix {
            play: false,
            decibels: gain::VOL_MIN_DECIBELS,
            pan: 0,
        },
    }
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.App.Tests/Audio/OpenAlAudioEnginePlaybackGatesTests.cs`
    //! and the engine half of `AudioMixerCommandBindingTests.cs`. The
    //! detached mixer never renders, so a started sound stays playing (the
    //! "device that never finishes a sound" of the C# tests).

    use std::time::Duration;

    use super::*;
    use crate::options::NO_PER_WAVE_CAP;

    fn tone() -> Arc<WaveData> {
        Arc::new(WaveData {
            channel_count: 1,
            sample_rate: 8000,
            bits_per_sample: 8,
            pcm_bytes: vec![128, 160, 192, 160, 128, 96, 64, 96],
            duration: Duration::from_millis(1),
        })
    }

    fn engine() -> (AudioEngine, Mixer) {
        AudioEngine::detached(AudioMixerOptions::default())
    }

    fn playing(mixer: &mut Mixer) -> usize {
        mixer.apply_commands();
        mixer.active_voices()
    }

    #[test]
    fn interface_sounds_follow_the_effect_volume() {
        let (mut e, mut m) = engine();
        e.set_sfx_volume(0.0);
        assert!(!e.play_ui_wave(1, &tone(), 1.0, 1.0, true));
        assert_eq!(playing(&mut m), 0);
        e.set_sfx_volume(1.0);
        assert!(e.play_ui_wave(2, &tone(), 1.0, 1.0, true));
        assert_eq!(playing(&mut m), 1);
    }

    #[test]
    fn focus_muted_refuses_new_sounds_without_touching_the_output_gain() {
        let (mut e, mut m) = engine();
        e.set_focus_muted(true);
        assert!(!e.play_3d_wave(1, 1, &tone(), Vec3::ZERO, 1.0, 1.0));
        assert!(!e.play_ui_wave(2, &tone(), 1.0, 1.0, true));
        assert!(!e.play_ambient_from_center(3, &tone(), 1.0, 1.0));
        assert_eq!(playing(&mut m), 0);
        e.set_focus_muted(false);
        assert!(e.play_3d_wave(4, 4, &tone(), Vec3::ZERO, 1.0, 1.0));
        assert_eq!(playing(&mut m), 1);
        assert!(!e.muted());
    }

    #[test]
    fn focus_muted_cuts_sounds_already_playing() {
        let (mut e, mut m) = AudioEngine::detached(AudioMixerOptions {
            use_authored_priority: false,
            max_voices_per_wave: NO_PER_WAVE_CAP,
            ..AudioMixerOptions::default()
        });
        let t = tone();
        for i in 0..32 {
            assert!(e.play_3d_wave(i + 1, i + 1, &t, Vec3::ZERO, 1.0, 1.0));
        }
        assert_eq!(playing(&mut m), 32);
        assert!(!e.play_3d_wave(900, 900, &t, Vec3::ZERO, 1.0, 1.0));
        let cuts_before = m.stats().cut;
        e.set_focus_muted(true);
        assert_eq!(playing(&mut m), 0);
        let cuts_after = m.stats().cut;
        assert!(cuts_after > cuts_before);
        e.set_focus_muted(true);
        m.apply_commands();
        assert_eq!(m.stats().cut, cuts_after);
        e.set_focus_muted(false);
        assert_eq!(playing(&mut m), 0);
        for i in 0..32 {
            assert!(e.play_3d_wave(1000 + i, 1000 + i, &t, Vec3::ZERO, 1.0, 1.0));
        }
        assert_eq!(playing(&mut m), 32);
    }

    #[test]
    fn disabling_interface_sound_leaves_the_non_interface_caller_alone() {
        let (mut e, mut m) = engine();
        e.set_interface_enabled(false);
        assert!(!e.play_ui_wave(1, &tone(), 1.0, 1.0, true));
        assert_eq!(playing(&mut m), 0);
        assert!(e.play_ui_wave(2, &tone(), 1.0, 1.0, false));
        assert_eq!(playing(&mut m), 1);
    }

    #[test]
    fn a_sound_beyond_the_cutoff_gets_no_voice() {
        let (mut e, mut m) = engine();
        assert!(!e.play_3d_wave(1, 1, &tone(), Vec3::new(0.0, 200.0, 0.0), 1.0, 1.0));
        assert_eq!(playing(&mut m), 0);
    }

    #[test]
    fn a_world_change_silences_world_sounds_and_spares_interface_sounds() {
        let (mut e, mut m) = engine();
        assert!(e.play_3d_wave(7, 1, &tone(), Vec3::ZERO, 1.0, 0.5));
        assert!(e.play_ui_wave(2, &tone(), 1.0, 0.5, true));
        e.suspend_world_audio();
        assert_eq!(playing(&mut m), 1);
        assert!(!e.play_3d_wave(7, 3, &tone(), Vec3::ZERO, 1.0, 0.5));
        assert!(!e.play_ambient_from_center(4, &tone(), 1.0, 0.5));
        assert!(e.play_ui_wave(5, &tone(), 1.0, 0.5, true));
        e.resume_world_audio();
        assert!(e.play_3d_wave(7, 6, &tone(), Vec3::ZERO, 1.0, 0.5));
    }

    #[test]
    fn stop_all_for_owner_silences_only_that_owner() {
        let (mut e, mut m) = engine();
        assert!(e.play_3d_wave(7, 1, &tone(), Vec3::ZERO, 1.0, 0.5));
        assert!(e.play_3d_wave(8, 2, &tone(), Vec3::ZERO, 1.0, 0.5));
        e.stop_all_for_owner(7);
        assert_eq!(playing(&mut m), 1);
    }

    #[test]
    fn a_finished_sound_frees_its_voice() {
        let (mut e, mut m) = AudioEngine::detached(AudioMixerOptions {
            retail_mixer: true,
            ..AudioMixerOptions::default()
        });
        for i in 0..16 {
            assert!(e.play_3d_wave(i + 1, i + 1, &tone(), Vec3::ZERO, 1.0, 0.0));
        }
        assert!(!e.play_3d_wave(99, 99, &tone(), Vec3::ZERO, 1.0, 0.0));
        let mut out = vec![0.0; 2 * 4096];
        m.render(&mut out);
        assert_eq!(m.active_voices(), 0);
        assert!(e.play_3d_wave(99, 99, &tone(), Vec3::ZERO, 1.0, 0.0));
    }

    #[test]
    fn mute_sets_the_output_gain_and_keeps_voices() {
        let (mut e, mut m) = engine();
        assert!(e.play_ui_wave(1, &tone(), 1.0, 1.0, true));
        e.set_muted(true);
        let mut out = vec![1.0; 8];
        m.render(&mut out);
        assert!(out.iter().all(|s| *s == 0.0));
        assert_eq!(m.active_voices(), 1);
    }

    #[test]
    fn changing_the_voice_count_resizes_the_live_mixer() {
        let (mut e, _m) = engine();
        assert_eq!(e.voice_count(), 32);
        assert!(e.apply_mixer_options(AudioMixerOptions {
            voice_count: 40,
            ..AudioMixerOptions::default()
        }));
        assert_eq!(e.voice_count(), 40);
        let sources: std::collections::HashSet<u32> =
            e.voices.voices().iter().map(|v| v.source_id).collect();
        assert_eq!(sources.len(), 40);
        assert!(!sources.contains(&0));
    }

    #[test]
    fn turning_the_retail_mixer_on_releases_the_voices_it_no_longer_has() {
        let (mut e, mut m) = engine();
        for i in 0..32 {
            assert!(e.play_3d_wave(i + 1, i + 1, &tone(), Vec3::ZERO, 1.0, 0.0));
        }
        e.apply_mixer_options(AudioMixerOptions {
            retail_mixer: true,
            ..AudioMixerOptions::default()
        });
        assert_eq!(e.voice_count(), 16);
        assert_eq!(playing(&mut m), 16);
        e.apply_mixer_options(AudioMixerOptions::default());
        assert_eq!(e.voice_count(), 32);
        assert_eq!(e.free_sources.len(), MIXER_SLOTS - 32);
    }

    #[test]
    fn settings_apply_volumes_and_switches() {
        let (mut e, _m) = engine();
        e.apply_settings(&AudioSettings {
            master: 0.5,
            sfx: 0.7,
            ambient: 0.3,
            ambient_enabled: false,
            interface_enabled: false,
            ..AudioSettings::default()
        });
        assert_eq!(
            (e.master_volume(), e.sfx_volume(), e.ambient_volume()),
            (0.5, 0.7, 0.0)
        );
        assert!(!e.play_ui_wave(1, &tone(), 1.0, 1.0, true));
    }

    #[test]
    fn an_unplayable_layout_is_refused() {
        let (mut e, _m) = engine();
        let w = Arc::new(WaveData {
            bits_per_sample: 24,
            ..(*tone()).clone()
        });
        assert!(!e.play_ui_wave(1, &w, 1.0, 1.0, false));
    }
}
