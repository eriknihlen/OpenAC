//! Audio: the game's sounds, mixed on the CPU and played on the default
//! output device.
//!
//! The pure parts (wave decoding, sound-table selection, the gain and pan
//! rules, voice allocation, ambient sounds) need no device. The device side
//! is an [`AudioEngine`] (inside an [`AudioGraph`] with the sound cache and
//! the three sound sources) that a window host creates and may drop at any
//! time; a headless host creates none. The mixer runs in the output
//! device's callback; the device is opened and closed on a dedicated
//! thread.

pub mod ambient;
pub mod cache;
pub mod cookbook;
mod engine;
pub mod environ;
mod error;
pub mod gain;
mod graph;
mod mixer;
pub mod options;
mod output;
pub mod random;
pub mod settings;
pub mod sound_id;
pub mod tweaked;
pub mod voices;
pub mod wave;

pub use cache::{SoundCache, SoundDats, resolve_ui_sound_table};
pub use engine::{AudioEngine, DETACHED_OUTPUT_RATE};
pub use error::AudioError;
pub use graph::{
    AmbientListenerPose, AmbientSounds, AudioGraph, HookSounds, UiSounds, is_sound_hook,
};
pub use mixer::{MIXER_SLOTS, Mixer, MixerStats};
pub use options::AudioMixerOptions;
pub use output::DeviceInfo;
pub use random::{SeededSoundRandom, SoundRandom};
pub use settings::{AudioLaunchOptions, AudioSettings};
pub use sound_id::SoundId;
pub use wave::WaveData;
