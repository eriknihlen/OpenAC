//! Audio against the retail dat files: every wave decodes, every sound table
//! resolves, the interface sound bank is found, the authored tweaked hooks
//! and ambient data read as OpenAC's installed-dat tests expect. These need
//! the dats: set `OPENAC_DAT_DIR` or install them under
//! `Documents\Asheron's Call`; run with `cargo test -- --include-ignored`.

use std::collections::BTreeMap;
use std::path::PathBuf;
use std::sync::Arc;

use ac_audio::ambient::{AmbientRegion, AmbientSoundScheduler, LandblockTerrain, rebuild_ambient};
use ac_audio::cache::resolve_ui_sound_table;
use ac_audio::tweaked::TweakedSoundHook;
use ac_audio::wave::{self, WaveFormatTag};
use ac_audio::{SeededSoundRandom, SoundCache, SoundId};
use ac_dat::DatCollection;
use ac_dat::objects::{EnvCell, LandBlock, PhysicsScript, Region, Setup, SoundTable, Wave};
use glam::Vec3;

fn dats() -> DatCollection {
    let dir = std::env::var_os("OPENAC_DAT_DIR")
        .map(PathBuf::from)
        .or_else(ac_dat::default_dat_dir)
        .expect("no dat directory: set OPENAC_DAT_DIR");
    DatCollection::open(dir).expect("open the retail dats")
}

fn ids_in(d: &DatCollection, lo: u32, hi: u32) -> Vec<u32> {
    d.portal
        .entries()
        .map(|e| e.unwrap().id)
        .filter(|id| (lo..=hi).contains(id))
        .collect()
}

/// Census measured on the retail portal dat: 786 waves, 785 PCM (8 or
/// 16 bit, mono or stereo) and one MP3 (`0x0A000393`, MPEG-2.5 layer 3,
/// mono 11025 Hz); no ADPCM. Every one decodes.
#[test]
#[ignore = "needs the retail dat files"]
fn every_wave_in_the_portal_dat_decodes() {
    let d = dats();
    let mut formats = BTreeMap::new();
    let ids = ids_in(&d, 0x0A00_0000, 0x0AFF_FFFF);
    for &id in &ids {
        let w: Wave = d.get(id).unwrap().unwrap();
        let tag = wave::peek_format(&w.header);
        *formats.entry(tag.0).or_insert(0) += 1;
        let decoded = wave::decode(&w.header, &w.data)
            .unwrap_or_else(|| panic!("{id:#010x} does not decode"));
        assert!(decoded.is_playable(), "{id:#010x} has an unplayable layout");
        assert!(decoded.frame_count() > 0, "{id:#010x} is empty");
    }
    assert_eq!(ids.len(), 786);
    assert_eq!(
        formats.into_iter().collect::<Vec<_>>(),
        [(WaveFormatTag::PCM.0, 785), (WaveFormatTag::MP3.0, 1)]
    );

    let mp3: Wave = d.get(0x0A00_0393).unwrap().unwrap();
    let decoded = wave::decode(&mp3.header, &mp3.data).unwrap();
    assert_eq!((decoded.channel_count, decoded.sample_rate), (1, 11025));
    let seconds = decoded.duration.as_secs_f64();
    // 39 frames of 576 samples: about two seconds, as measured in OpenAC.
    assert!((2.0..=2.1).contains(&seconds), "{seconds}");
}

/// Every sound table loads through the cache, and every wave its entries
/// name exists and decodes. Golden counts: 190 tables, 4184 sounds, 4185
/// entries (see the ac-dat sweep).
#[test]
#[ignore = "needs the retail dat files"]
fn every_sound_table_resolves_to_decodable_waves() {
    let d = Arc::new(dats());
    let cache = SoundCache::new(d.clone());
    let ids = ids_in(&d, 0x2000_0000, 0x2000_FFFF);
    let (mut sounds, mut entries) = (0usize, 0usize);
    for &id in &ids {
        let table = cache.sound_table(id).unwrap().unwrap();
        sounds += table.sounds.len();
        for (_, data) in &table.sounds {
            for e in &data.entries {
                entries += 1;
                assert!(
                    cache.wave(e.id).unwrap().is_some(),
                    "table {id:#010x} names wave {:#010x} which does not decode",
                    e.id
                );
            }
        }
    }
    assert_eq!((ids.len(), sounds, entries), (190, 4184, 4185));
    assert_eq!(cache.cached_sound_table_count(), 190);
}

/// Golden: OpenAC `UiSoundTableResolutionTests` (`0x2000004B`, slots in
/// `0x6A..=0x8A` including the portal and Roar/Thunder6 cues).
#[test]
#[ignore = "needs the retail dat files"]
fn the_interface_sound_bank_resolves_through_the_enum_id_map_chain() {
    let d = dats();
    let did = resolve_ui_sound_table(&d).unwrap();
    assert_eq!(did, 0x2000_004B);
    let table: SoundTable = d.get(did).unwrap().unwrap();
    assert!(!table.sounds.is_empty());
    for (key, _) in &table.sounds {
        assert!((0x6A..=0x8A).contains(key), "{key:#x}");
    }
    let has = |s: SoundId| table.sounds.iter().any(|(k, _)| *k == s.value());
    assert!(has(SoundId::UiEnterPortal));
    assert!(has(SoundId::UiExitPortal));
    assert!(has(SoundId::UiRoar));
    assert!(has(SoundId::UiThunder6));
}

/// Golden: OpenAC `TweakedSoundHookLiveDatTests` (the thunder script
/// `0x33000453`: eight claps, play probabilities 0.2..0.5).
#[test]
#[ignore = "needs the retail dat files"]
fn the_thunder_sequence_is_authored_to_stay_silent_most_times_round() {
    let d = dats();
    let script: PhysicsScript = d.get(0x3300_0453).unwrap().unwrap();
    let thunder: Vec<TweakedSoundHook> = script
        .script_data
        .iter()
        .filter_map(|x| TweakedSoundHook::from_hook(&x.hook.data))
        .collect();
    assert_eq!(
        thunder.iter().map(|h| h.sound_id).collect::<Vec<_>>(),
        [
            0x0A00_04D1,
            0x0A00_04D0,
            0x0A00_04D1,
            0x0A00_04D0,
            0x0A00_04D2,
            0x0A00_04D0,
            0x0A00_04D2,
            0x0A00_04D0
        ]
    );
    assert_eq!(
        thunder
            .iter()
            .map(|h| h.play_probability())
            .collect::<Vec<_>>(),
        [0.2, 0.5, 0.4, 0.3, 0.2, 0.2, 0.2, 0.2]
    );
}

/// Golden: OpenAC `TweakedSoundHookLiveDatTests` (every authored play
/// probability is in `(0, 1]`).
#[test]
#[ignore = "needs the retail dat files"]
fn every_authored_tweaked_hook_carries_a_usable_probability() {
    let d = dats();
    let mut probabilities = Vec::new();
    for id in ids_in(&d, 0x3300_0000, 0x33FF_FFFF) {
        let script: PhysicsScript = d.get(id).unwrap().unwrap();
        probabilities.extend(
            script
                .script_data
                .iter()
                .filter_map(|x| TweakedSoundHook::from_hook(&x.hook.data))
                .map(|h| h.play_probability()),
        );
    }
    assert_eq!(probabilities.len(), 368);
    assert!(probabilities.iter().all(|p| *p > 0.0 && *p <= 1.0));
}

/// Golden: OpenAC `EnvCellSoundEmitterInventoryTests` (no Holtburg interior
/// places an object whose sound table has an ambient slot `0x46..=0x4E`):
/// indoors is silent by design.
#[test]
#[ignore = "needs the retail dat files"]
fn holtburg_interiors_carry_no_dat_authored_ambient_emitters() {
    let d = dats();
    let mut emitters = Vec::new();
    for cell_id in 0xA9B4_0100u32..=0xA9B4_01FF {
        let Some(cell) = d.get::<EnvCell>(cell_id).unwrap() else {
            continue;
        };
        for stab in &cell.static_objects {
            if stab.id & 0xFF00_0000 != 0x0200_0000 {
                continue;
            }
            let setup: Setup = d.get(stab.id).unwrap().unwrap();
            if setup.default_sound_table == 0 {
                continue;
            }
            let Some(table) = d.get::<SoundTable>(setup.default_sound_table).unwrap() else {
                continue;
            };
            if table.sounds.iter().any(|(k, _)| (0x46..=0x4E).contains(k)) {
                emitters.push((cell_id, stab.id));
            }
        }
    }
    assert!(emitters.is_empty(), "{emitters:x?}");
}

/// The retail region's ambient data around Holtburg produces audible beds
/// over the 3×3 landblocks.
#[test]
#[ignore = "needs the retail dat files"]
fn holtburg_outdoors_has_ambient_sounds() {
    let d = dats();
    let region: Region = d.get(0x1300_0000).unwrap().unwrap();
    let ambient = AmbientRegion::from_region(&region);
    let mut scheduler = AmbientSoundScheduler::new();
    let mut landblocks = |id: u32| -> Option<LandblockTerrain> {
        let lb: LandBlock = d.get(id).ok()??;
        Some(std::array::from_fn(|i| lb.terrain[i].0))
    };
    rebuild_ambient(
        &mut scheduler,
        &ambient,
        0xA9B4_FFFF,
        Vec3::new(96.0, 96.0, 0.0),
        &mut landblocks,
        0.0,
        &mut SeededSoundRandom::new(1),
    );
    assert!(scheduler.total_sound_count() > 0.0);
    assert!(scheduler.instances().iter().any(|i| i.can_hear()));
    assert!(scheduler.queued_count() > 0);
}
