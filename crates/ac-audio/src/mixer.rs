//! The CPU mixer: what the audio device's thread runs.
//!
//! Like the game's sound device, every voice is a buffer played once from
//! start to end with a level and a pan fixed when it starts; the voices are
//! summed and the sum is clipped. The mixer owns no game state: it takes
//! commands from the engine through a channel and reports finished voices
//! through one atomic per slot, so the device callback never blocks.

use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::mpsc::{self, Receiver, Sender};

use crate::gain::pan_channel_gains;
use crate::options::MAXIMUM_VOICE_COUNT;
use crate::wave::WaveData;

/// Mixer slots: the most voices the pool can ever have.
pub const MIXER_SLOTS: usize = MAXIMUM_VOICE_COUNT as usize;

/// What the engine asks of the mixer.
#[derive(Debug)]
pub(crate) enum MixerCommand {
    /// Start `wave` in `slot`, replacing whatever the slot held.
    Play {
        slot: usize,
        generation: u64,
        wave: Arc<WaveData>,
        gain: f32,
        pan: i32,
    },
    /// Silence `slot`.
    Stop { slot: usize },
    /// Scale the whole mix (0 mutes).
    SetOutputGain(f32),
}

/// Per slot, the generation of the last sound that finished there.
#[derive(Debug)]
struct Finished {
    generation: [AtomicU64; MIXER_SLOTS],
    /// Output frames rendered so far.
    frames: AtomicU64,
}

/// The engine's end of the mixer: sends commands and knows which slots are
/// still sounding.
#[derive(Debug)]
pub(crate) struct MixerLink {
    tx: Sender<MixerCommand>,
    finished: Arc<Finished>,
    generation: [u64; MIXER_SLOTS],
    next_generation: u64,
    disconnected: bool,
}

impl MixerLink {
    /// Start a wave in a slot. False when the mixer is gone.
    pub(crate) fn play(&mut self, slot: usize, wave: Arc<WaveData>, gain: f32, pan: i32) -> bool {
        self.next_generation += 1;
        let generation = self.next_generation;
        self.generation[slot] = generation;
        self.send(MixerCommand::Play {
            slot,
            generation,
            wave,
            gain,
            pan,
        })
    }

    /// Silence a slot.
    pub(crate) fn stop(&mut self, slot: usize) {
        self.generation[slot] = 0;
        self.send(MixerCommand::Stop { slot });
    }

    /// Scale the whole mix.
    pub(crate) fn set_output_gain(&mut self, gain: f32) {
        self.send(MixerCommand::SetOutputGain(gain));
    }

    /// Whether the slot's current sound has not finished yet. A sound counts
    /// as playing from the moment it is sent.
    pub(crate) fn is_playing(&self, slot: usize) -> bool {
        let g = self.generation[slot];
        g != 0 && !self.disconnected && self.finished.generation[slot].load(Ordering::Acquire) != g
    }

    /// Output frames the mixer has rendered so far.
    pub(crate) fn frames_rendered(&self) -> u64 {
        self.finished.frames.load(Ordering::Relaxed)
    }

    /// The mixer has gone away (its device thread ended).
    pub(crate) fn is_disconnected(&self) -> bool {
        self.disconnected
    }

    fn send(&mut self, command: MixerCommand) -> bool {
        if self.disconnected {
            return false;
        }
        if self.tx.send(command).is_err() {
            self.disconnected = true;
        }
        !self.disconnected
    }
}

#[derive(Debug)]
struct MixVoice {
    wave: Arc<WaveData>,
    generation: u64,
    /// Position in source frames.
    position: f64,
    /// Source frames per output frame.
    step: f64,
    left: f32,
    right: f32,
}

/// Counters, for tests and diagnostics.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct MixerStats {
    /// Sounds started.
    pub started: u64,
    /// Sounds stopped while still sounding (a cut, not a natural end).
    pub cut: u64,
    /// Sounds that played to their end.
    pub finished: u64,
}

/// The mixing end. Owned by the device callback (or a test).
#[derive(Debug)]
pub struct Mixer {
    rx: Receiver<MixerCommand>,
    finished: Arc<Finished>,
    voices: Vec<Option<MixVoice>>,
    output_rate: u32,
    output_gain: f32,
    stats: MixerStats,
}

/// A connected engine end and mixer, the mixer producing at `output_rate`
/// frames per second.
pub(crate) fn mixer_pair(output_rate: u32) -> (MixerLink, Mixer) {
    let (tx, rx) = mpsc::channel();
    let finished = Arc::new(Finished {
        generation: std::array::from_fn(|_| AtomicU64::new(0)),
        frames: AtomicU64::new(0),
    });
    let link = MixerLink {
        tx,
        finished: finished.clone(),
        generation: [0; MIXER_SLOTS],
        next_generation: 0,
        disconnected: false,
    };
    let mixer = Mixer {
        rx,
        finished,
        voices: (0..MIXER_SLOTS).map(|_| None).collect(),
        output_rate: output_rate.max(1),
        output_gain: 1.0,
        stats: MixerStats::default(),
    };
    (link, mixer)
}

impl Mixer {
    /// The output rate, frames per second.
    #[must_use]
    pub fn output_rate(&self) -> u32 {
        self.output_rate
    }

    /// Change the output rate (before the device starts pulling).
    pub fn set_output_rate(&mut self, rate: u32) {
        self.output_rate = rate.max(1);
    }

    /// Apply every command the engine has sent so far.
    pub fn apply_commands(&mut self) {
        // Empty or disconnected both mean nothing more to apply; a
        // disconnected engine just leaves the voices to finish.
        while let Ok(command) = self.rx.try_recv() {
            self.apply(command);
        }
    }

    fn apply(&mut self, command: MixerCommand) {
        match command {
            MixerCommand::Play {
                slot,
                generation,
                wave,
                gain,
                pan,
            } => {
                let (l, r) = pan_channel_gains(pan);
                let step = f64::from(wave.sample_rate) / f64::from(self.output_rate);
                if self.voices[slot].take().is_some() {
                    self.stats.cut += 1;
                }
                self.voices[slot] = Some(MixVoice {
                    wave,
                    generation,
                    position: 0.0,
                    step,
                    left: gain * l,
                    right: gain * r,
                });
                self.stats.started += 1;
            }
            MixerCommand::Stop { slot } => {
                if self.voices[slot].take().is_some() {
                    self.stats.cut += 1;
                }
            }
            MixerCommand::SetOutputGain(g) => self.output_gain = g,
        }
    }

    /// Voices currently sounding.
    #[must_use]
    pub fn active_voices(&self) -> usize {
        self.voices.iter().filter(|v| v.is_some()).count()
    }

    /// Counters.
    #[must_use]
    pub fn stats(&self) -> MixerStats {
        self.stats
    }

    /// Apply pending commands, then mix into `out`: interleaved stereo
    /// (left, right) frames, overwritten, every sample clipped to `[-1, 1]`.
    pub fn render(&mut self, out: &mut [f32]) {
        self.apply_commands();
        out.fill(0.0);
        let frames = out.len() / 2;
        self.finished
            .frames
            .fetch_add(frames as u64, Ordering::Relaxed);
        for slot in 0..self.voices.len() {
            let Some(voice) = self.voices[slot].as_mut() else {
                continue;
            };
            let done = mix_voice(voice, &mut out[..frames * 2]);
            if done {
                let generation = voice.generation;
                self.voices[slot] = None;
                self.finished.generation[slot].store(generation, Ordering::Release);
                self.stats.finished += 1;
            }
        }
        let gain = self.output_gain;
        for s in out.iter_mut() {
            *s = (*s * gain).clamp(-1.0, 1.0);
        }
    }
}

/// One source frame as (left, right) in `[-1, 1)`; past the end is silence.
fn frame_at(wave: &WaveData, frame: usize) -> (f32, f32) {
    let bytes = wave.frame_bytes();
    let start = frame * bytes;
    let Some(data) = wave.pcm_bytes.get(start..start + bytes) else {
        return (0.0, 0.0);
    };
    let sample = |i: usize| -> f32 {
        if wave.bits_per_sample == 8 {
            (f32::from(data[i]) - 128.0) / 128.0
        } else {
            f32::from(i16::from_le_bytes([data[i * 2], data[i * 2 + 1]])) / 32768.0
        }
    };
    if wave.channel_count == 1 {
        let s = sample(0);
        (s, s)
    } else {
        (sample(0), sample(1))
    }
}

/// Add a voice into `out`; true when it reached its end.
fn mix_voice(voice: &mut MixVoice, out: &mut [f32]) -> bool {
    let frame_count = voice.wave.frame_count();
    for pair in out.chunks_exact_mut(2) {
        if voice.position >= frame_count as f64 {
            return true;
        }
        // Linear interpolation between the two source frames around the
        // position; the frame after the last is silence.
        let i = voice.position as usize;
        let t = (voice.position - i as f64) as f32;
        let (l0, r0) = frame_at(&voice.wave, i);
        let (l1, r1) = frame_at(&voice.wave, i + 1);
        pair[0] += (l0 + (l1 - l0) * t) * voice.left;
        pair[1] += (r0 + (r1 - r0) * t) * voice.right;
        voice.position += voice.step;
    }
    voice.position >= frame_count as f64
}

#[cfg(test)]
mod tests {
    use std::time::Duration;

    use super::*;

    fn wave(rate: u32, channels: u16, bits: u16, pcm: Vec<u8>) -> Arc<WaveData> {
        Arc::new(WaveData {
            channel_count: channels,
            sample_rate: rate,
            bits_per_sample: bits,
            pcm_bytes: pcm,
            duration: Duration::ZERO,
        })
    }

    fn mono16(rate: u32, samples: &[i16]) -> Arc<WaveData> {
        wave(
            rate,
            1,
            16,
            samples.iter().flat_map(|s| s.to_le_bytes()).collect(),
        )
    }

    #[test]
    fn a_centred_mono_voice_plays_at_full_level_on_both_speakers() {
        let (mut link, mut mixer) = mixer_pair(8000);
        assert!(link.play(0, mono16(8000, &[16384, 16384]), 1.0, 0));
        let mut out = [0.0; 4];
        mixer.render(&mut out);
        assert_eq!(out[0], 0.5);
        assert_eq!(out[1], 0.5);
    }

    #[test]
    fn pan_turns_down_the_far_speaker_only() {
        let (mut link, mut mixer) = mixer_pair(8000);
        link.play(0, mono16(8000, &[16384; 4]), 1.0, 6);
        let mut out = [0.0; 2];
        mixer.render(&mut out);
        assert_eq!(out[1], 0.5);
        assert!((out[0] - 0.5 * 10f32.powf(-6.0 / 20.0)).abs() < 1e-6);
    }

    #[test]
    fn eight_bit_samples_are_unsigned_around_128() {
        let (mut link, mut mixer) = mixer_pair(8000);
        link.play(0, wave(8000, 1, 8, vec![192, 64]), 1.0, 0);
        let mut out = [0.0; 4];
        mixer.render(&mut out);
        assert_eq!(&out, &[0.5, 0.5, -0.5, -0.5]);
    }

    #[test]
    fn stereo_keeps_its_channels() {
        let (mut link, mut mixer) = mixer_pair(8000);
        let pcm = [8192i16, -8192]
            .iter()
            .flat_map(|s| s.to_le_bytes())
            .collect();
        link.play(0, wave(8000, 2, 16, pcm), 1.0, 0);
        let mut out = [0.0; 2];
        mixer.render(&mut out);
        assert_eq!(out, [0.25, -0.25]);
    }

    #[test]
    fn the_sum_is_clipped_not_limited() {
        let (mut link, mut mixer) = mixer_pair(8000);
        for slot in 0..3 {
            link.play(slot, mono16(8000, &[16384; 2]), 1.0, 0);
        }
        link.play(3, mono16(8000, &[-1000; 2]), 0.0, 0);
        let mut out = [0.0; 2];
        mixer.render(&mut out);
        assert_eq!(out, [1.0, 1.0]);
    }

    #[test]
    fn a_voice_finishes_at_its_end_and_reports_it() {
        let (mut link, mut mixer) = mixer_pair(8000);
        link.play(5, mono16(8000, &[1; 3]), 1.0, 0);
        assert!(link.is_playing(5));
        let mut out = [0.0; 4];
        mixer.render(&mut out);
        assert!(link.is_playing(5));
        mixer.render(&mut out);
        assert!(!link.is_playing(5));
        assert_eq!(mixer.active_voices(), 0);
        assert_eq!(mixer.stats().finished, 1);
    }

    #[test]
    fn a_replaced_voice_does_not_report_the_new_sound_finished() {
        let (mut link, mut mixer) = mixer_pair(8000);
        link.play(0, mono16(8000, &[1; 1]), 1.0, 0);
        let mut out = [0.0; 4];
        mixer.render(&mut out);
        link.play(0, mono16(8000, &[1; 100]), 1.0, 0);
        assert!(link.is_playing(0));
        mixer.render(&mut out);
        assert!(link.is_playing(0));
    }

    #[test]
    fn resampling_steps_through_the_source_at_its_own_rate() {
        // A 4000 Hz source on an 8000 Hz output: every source frame lasts two
        // output frames, the second halfway to the next.
        let (mut link, mut mixer) = mixer_pair(8000);
        link.play(0, mono16(4000, &[0, 16384]), 1.0, 0);
        let mut out = [0.0; 8];
        mixer.render(&mut out);
        let left: Vec<f32> = out.chunks(2).map(|p| p[0]).collect();
        assert_eq!(left, [0.0, 0.25, 0.5, 0.25]);
    }

    #[test]
    fn stop_and_mute() {
        let (mut link, mut mixer) = mixer_pair(8000);
        link.play(0, mono16(8000, &[16384; 8]), 1.0, 0);
        link.set_output_gain(0.0);
        let mut out = [1.0; 2];
        mixer.render(&mut out);
        assert_eq!(out, [0.0, 0.0]);
        link.stop(0);
        assert!(!link.is_playing(0));
        mixer.apply_commands();
        assert_eq!(mixer.active_voices(), 0);
        assert_eq!(mixer.stats().cut, 1);
    }

    #[test]
    fn a_dropped_mixer_disconnects_the_link() {
        let (mut link, mixer) = mixer_pair(8000);
        drop(mixer);
        assert!(!link.play(0, mono16(8000, &[1]), 1.0, 0));
        assert!(link.is_disconnected());
        assert!(!link.is_playing(0));
    }
}
