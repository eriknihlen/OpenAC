//! The animation core against the retail dat files.
//!
//! Golden facts from OpenAC `HumanoidMotionTableRootMotionTests` and
//! `MotionCommandCatalogDatTests` (`tests/AcDream.Core.Tests/Physics/`),
//! plus a sweep that every motion table drives without failing. These tests
//! need the dats: set `OPENAC_DAT_DIR` or install them under
//! `Documents\Asheron's Call`; run with `cargo test -- --include-ignored`.

use std::collections::HashMap;
use std::path::PathBuf;
use std::sync::{Arc, Mutex};

use ac_dat::DatCollection;
use ac_dat::objects::{Animation, DatObject, MotionDataFlags, MotionTable, Setup};
use ac_motion::{
    AnimationLoader, AnimationSequencer, Frame, MotionTableRules, NoMotionDone, Sequence,
    motion_command,
};

const HUMANOID_MOTION_TABLE: u32 = 0x0900_0001;
const HUMANOID_SETUP: u32 = 0x0200_0001;
const NON_COMBAT: u32 = motion_command::NON_COMBAT;

fn dat_dir() -> PathBuf {
    std::env::var_os("OPENAC_DAT_DIR")
        .map(PathBuf::from)
        .or_else(ac_dat::default_dat_dir)
        .expect("no dat directory: set OPENAC_DAT_DIR")
}

/// Animations from the portal dat, cached.
struct DatLoader {
    dats: DatCollection,
    cache: Mutex<HashMap<u32, Option<Arc<Animation>>>>,
}

impl DatLoader {
    fn open() -> Arc<Self> {
        Arc::new(Self {
            dats: DatCollection::open(dat_dir()).expect("open the retail dats"),
            cache: Mutex::default(),
        })
    }

    fn get<T: DatObject>(&self, id: u32) -> T {
        self.dats
            .get::<T>(id)
            .unwrap_or_else(|e| panic!("{id:#010x}: {e}"))
            .unwrap_or_else(|| panic!("{id:#010x} missing"))
    }
}

impl AnimationLoader for DatLoader {
    fn load_animation(&self, id: u32) -> Option<Arc<Animation>> {
        let mut cache = self.cache.lock().expect("cache lock");
        cache
            .entry(id)
            .or_insert_with(|| {
                self.dats
                    .get::<Animation>(id)
                    .unwrap_or_else(|e| panic!("animation {id:#010x}: {e}"))
                    .map(Arc::new)
            })
            .clone()
    }
}

fn key(style: u32, motion: u32) -> u32 {
    (style << 16) | (motion & 0x00FF_FFFF)
}

#[test]
#[ignore = "needs the retail dat files"]
fn humanoid_walk_and_run_cycles_carry_no_literal_velocity() {
    let loader = DatLoader::open();
    let table: MotionTable = loader.get(HUMANOID_MOTION_TABLE);
    for motion in [0x4000_0005, 0x4000_0007] {
        let k = key(NON_COMBAT, motion);
        let (_, cycle) = table
            .cycles
            .iter()
            .find(|(key, _)| *key == k)
            .unwrap_or_else(|| panic!("cycle {k:#010x}"));
        assert_eq!(
            (cycle.velocity.x, cycle.velocity.y, cycle.velocity.z),
            (0.0, 0.0, 0.0)
        );
    }
}

#[test]
#[ignore = "needs the retail dat files"]
fn humanoid_turn_right_modifier_spins_at_minus_1_5_radians_per_second() {
    let loader = DatLoader::open();
    let rules = MotionTableRules::new(Arc::new(loader.get(HUMANOID_MOTION_TABLE)));
    let turn = motion_command::TURN_RIGHT & 0x00FF_FFFF;
    let modifier = rules
        .lookup_modifier(NON_COMBAT, turn, true)
        .or_else(|| rules.lookup_modifier(NON_COMBAT, turn, false))
        .expect("the TurnRight modifier");
    assert!(modifier.flags.contains(MotionDataFlags::HAS_OMEGA));
    assert_eq!((modifier.omega.x, modifier.omega.y), (0.0, 0.0));
    assert!((modifier.omega.z - -1.5).abs() < 1e-5);
}

#[test]
#[ignore = "needs the retail dat files"]
fn humanoid_walk_authors_root_travel() {
    let loader = DatLoader::open();
    let setup: Setup = loader.get(HUMANOID_SETUP);
    let rules = Arc::new(MotionTableRules::new(Arc::new(
        loader.get(HUMANOID_MOTION_TABLE),
    )));
    let mut seq = AnimationSequencer::from_setup(&setup, rules, loader.clone());
    seq.set_cycle(NON_COMBAT, motion_command::READY, 1.0)
        .expect("ready");
    seq.set_cycle(NON_COMBAT, motion_command::WALK_FORWARD, 1.0)
        .expect("walk forward");

    let mut distance = 0.0;
    for _ in 0..120 {
        let mut root = Frame::IDENTITY;
        seq.advance(1.0 / 60.0, Some(&mut root));
        distance += root.origin.length();
    }
    assert!(distance > 1.0, "walked {distance:.3} m in 2 s");
}

/// True when some motion table can animate `full_command`: as a link's
/// source (low 16 bits), as a link target, or as a modifier.
fn exists_in_any_motion_table(tables: &[MotionTable], full_command: u32) -> bool {
    let low16 = full_command & 0xFFFF;
    tables.iter().any(|mt| {
        mt.links.iter().any(|(k, _)| k & 0xFFFF == low16)
            || mt
                .links
                .iter()
                .any(|(_, targets)| targets.iter().any(|(t, _)| *t == full_command))
            || mt.modifiers.iter().any(|(k, _)| *k == full_command)
    })
}

fn all_motion_tables(loader: &DatLoader) -> Vec<MotionTable> {
    let portal = &loader.dats.portal;
    let mut tables = Vec::new();
    for entry in portal.entries() {
        let entry = entry.expect("dat entry");
        if entry.id >> 24 == 0x09 {
            tables.push(loader.get::<MotionTable>(entry.id));
        }
    }
    tables
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_dat_motion_tables_use_the_server_command_numbering() {
    let loader = DatLoader::open();
    let tables = all_motion_tables(&loader);
    for present in [
        motion_command::LIFESTONE_RECALL,
        motion_command::HOUSE_RECALL,
        0x1000_0137,
        0x1000_0166,
        0x1000_0171,
        0x1000_0173,
    ] {
        assert!(
            exists_in_any_motion_table(&tables, present),
            "{present:#010x}"
        );
    }
    // The older client's numbers for the same commands are absent.
    for absent in [0x1000_0150, 0x1000_0163, 0x1000_016E, 0x1000_0170] {
        assert!(
            !exists_in_any_motion_table(&tables, absent),
            "{absent:#010x}"
        );
    }
}

#[test]
#[ignore = "needs the retail dat files"]
fn every_motion_table_enters_its_default_state() {
    let loader = DatLoader::open();
    let tables = all_motion_tables(&loader);
    assert!(tables.len() > 100, "{} tables", tables.len());
    let mut entered = 0;
    for table in tables {
        let id = table.id;
        let rules = MotionTableRules::new(Arc::new(table));
        let mut sequence = Sequence::new(loader.clone());
        let mut manager = ac_motion::MotionTableManager::new(Some(Arc::new(rules)));
        manager.initialize_state(&mut sequence);
        if manager.state().style != 0 {
            entered += 1;
            assert!(
                sequence.has_anims(),
                "{id:#010x}: default state plays nothing"
            );
        }
        // Run the default cycle for a second and drain, as a despawn would.
        let mut hooks = Vec::new();
        for _ in 0..30 {
            let mut root = Frame::IDENTITY;
            sequence.update(1.0 / 30.0, Some(&mut root), Some(&mut hooks));
        }
        manager.handle_exit_world(&mut NoMotionDone);
        assert_eq!(manager.pending_animations().len(), 0, "{id:#010x}");
    }
    assert!(entered > 100, "{entered} tables entered a default state");
}

/// No retail cycle or link is without animations, so the wrapped tick
/// count of an empty cycle never arises from the dats.
#[test]
#[ignore = "needs the retail dat files"]
fn no_motion_table_holds_a_cycle_or_link_without_animations() {
    let loader = DatLoader::open();
    let tables = all_motion_tables(&loader);
    let mut cycles = 0;
    for t in &tables {
        for (k, c) in &t.cycles {
            cycles += 1;
            assert!(!c.anims.is_empty(), "{:#010x} cycle {k:#010x}", t.id);
        }
        for (k, targets) in &t.links {
            for (to, l) in targets {
                assert!(
                    !l.anims.is_empty(),
                    "{:#010x} link {k:#010x}->{to:#010x}",
                    t.id
                );
            }
        }
    }
    // Counted on the local dats: 436 tables, 18,451 cycles.
    assert_eq!((tables.len(), cycles), (436, 18_451));
}

/// Whether the sequence's cursor is in its cyclic tail.
fn in_cycle(seq: &AnimationSequencer) -> bool {
    let s = seq.sequence();
    match (s.curr_anim_index(), s.first_cyclic_index()) {
        (Some(current), Some(first)) => current >= first,
        _ => false,
    }
}

/// OpenAC `RecallTeleportAnimationTests.InstalledRecall_HiddenEnterWorldPublishesFinishedCyclicPoseWithoutAdvancingTime`:
/// at the moment the server teleports a recalling character (the recall
/// link's authored length, as the server times it) the recall is still the
/// current animation; entering the world drops the link, so the character
/// arrives standing in its cycle, in a pose other than the recall's last.
#[test]
#[ignore = "needs the retail dat files"]
fn a_recall_interrupted_by_its_teleport_arrives_in_the_cycle() {
    let loader = DatLoader::open();
    let setup: Setup = loader.get(HUMANOID_SETUP);
    let table: MotionTable = loader.get(HUMANOID_MOTION_TABLE);
    let ready_key = key(NON_COMBAT, motion_command::READY);
    let (_, targets) = table
        .links
        .iter()
        .find(|(k, _)| *k == ready_key)
        .expect("the ready links");
    let (_, recall) = targets
        .iter()
        .find(|(m, _)| *m == motion_command::LIFESTONE_RECALL)
        .expect("the recall link");
    let mut server_duration = 0.0f64;
    for anim in &recall.anims {
        let loaded: Animation = loader.get(anim.anim_id);
        let high = if anim.high_frame == -1 {
            loaded.part_frames.len() as i32
        } else {
            anim.high_frame
        };
        server_duration += f64::from(high - anim.low_frame) / f64::from(anim.framerate.abs());
    }

    let rules = Arc::new(MotionTableRules::new(Arc::new(table)));
    let mut seq = AnimationSequencer::from_setup(&setup, rules, loader.clone());
    seq.set_cycle(NON_COMBAT, motion_command::READY, 1.0)
        .expect("ready");
    seq.play_action(motion_command::LIFESTONE_RECALL, 1.0)
        .expect("the recall");
    let recall_tail = seq.advance(server_duration as f32, None).to_vec();
    assert!(
        !in_cycle(&seq),
        "at the server's teleport the recall link is still playing"
    );

    seq.handle_enter_world(&mut NoMotionDone);
    let finished = seq.sample_current_pose().to_vec();
    assert!(in_cycle(&seq), "entering the world leaves the cycle");
    assert_eq!(recall_tail.len(), finished.len());
    let moved = recall_tail.iter().zip(&finished).any(|(a, b)| {
        a.origin.distance_squared(b.origin) > 1e-6
            || a.orientation
                .normalize()
                .dot(b.orientation.normalize())
                .abs()
                < 0.999_999
    });
    assert!(moved, "the cycle's pose, not the recall's last");
}
