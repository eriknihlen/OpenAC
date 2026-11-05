//! Play one dat wave through the default output device.
//!
//! `cargo run -p ac-audio --example play_wave -- <wave id> [pan]`
//!
//! The wave plays centred (or at `pan`, whole decibels −15..15, positive to
//! the right) at full volume for one second (a shorter wave plays to its end
//! and must have finished). The dats come from `OPENAC_DAT_DIR` or
//! `Documents\Asheron's Call`.

use std::path::PathBuf;
use std::process::ExitCode;
use std::sync::Arc;
use std::time::Duration;

use ac_audio::{AudioEngine, AudioMixerOptions, SoundCache};
use ac_dat::DatCollection;
use glam::Vec3;

fn parse_id(s: &str) -> Option<u32> {
    let hex = s.trim_start_matches("0x").trim_start_matches("0X");
    u32::from_str_radix(hex, 16).ok()
}

fn main() -> ExitCode {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let Some(id) = args.first().and_then(|s| parse_id(s)) else {
        eprintln!("usage: play_wave <hex wave id> [pan -15..15]");
        return ExitCode::from(2);
    };
    let pan: i32 = match args.get(1).map(|s| s.parse()) {
        None => 0,
        Some(Ok(p)) if (-15..=15).contains(&p) => p,
        Some(_) => {
            eprintln!("pan must be a whole number from -15 to 15");
            return ExitCode::from(2);
        }
    };
    let dir = std::env::var_os("OPENAC_DAT_DIR")
        .map(PathBuf::from)
        .or_else(ac_dat::default_dat_dir);
    let Some(dir) = dir else {
        eprintln!("no dat directory: set OPENAC_DAT_DIR");
        return ExitCode::from(2);
    };
    let dats = match DatCollection::open(&dir) {
        Ok(d) => Arc::new(d),
        Err(e) => {
            eprintln!("opening the dats in {}: {e}", dir.display());
            return ExitCode::FAILURE;
        }
    };
    let cache = SoundCache::new(dats);
    let wave = match cache.wave(id) {
        Ok(Some(w)) => w,
        Ok(None) => {
            eprintln!("wave {id:#010x} does not exist or does not decode");
            return ExitCode::FAILURE;
        }
        Err(e) => {
            eprintln!("{e}");
            return ExitCode::FAILURE;
        }
    };
    println!(
        "wave {id:#010x}: {} channel(s), {} Hz, {} bits, {:.3} s",
        wave.channel_count,
        wave.sample_rate,
        wave.bits_per_sample,
        wave.duration.as_secs_f64()
    );
    let mut engine = match AudioEngine::open(AudioMixerOptions::default()) {
        Ok(e) => e,
        Err(e) => {
            eprintln!("{e}");
            return ExitCode::FAILURE;
        }
    };
    if let Some(info) = engine.device_info() {
        println!(
            "device: {} ({} Hz, {} channel(s), {})",
            info.name, info.sample_rate, info.channels, info.sample_format
        );
    }
    // A source due east or west of a north-facing listener at 5 m gives
    // full pan; scale the bearing to the asked pan.
    let played = if pan == 0 {
        engine.play_ui_wave(id, &wave, 1.0, 1.0, false)
    } else {
        let angle = (pan as f32 / -15.0).clamp(-1.0, 1.0).asin();
        engine.set_listener(Vec3::ZERO, 0.0);
        let source = Vec3::new(-angle.sin() * 5.0, -angle.cos() * 5.0, 0.0);
        engine.play_3d_wave(1, id, &wave, source, 1.0, 1.0)
    };
    if !played {
        eprintln!("the engine refused the sound");
        return ExitCode::FAILURE;
    }
    let short = wave.duration < Duration::from_secs(1);
    std::thread::sleep(wave.duration.min(Duration::from_secs(1)) + Duration::from_millis(100));
    if let Some(e) = engine.device_error() {
        eprintln!("device reported: {e}");
        return ExitCode::FAILURE;
    }
    let (frames, still) = (engine.frames_rendered(), engine.playing_voice_count());
    drop(engine);
    println!("done: device pulled {frames} frames; voices still sounding: {still}");
    if frames == 0 || (short && still != 0) {
        eprintln!("the sound did not play through");
        return ExitCode::FAILURE;
    }
    ExitCode::SUCCESS
}
