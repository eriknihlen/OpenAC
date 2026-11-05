//! The retail dats, for the scenarios about a body that moves (OpenAC's
//! harness lays down a flat square of ground and a capsule for the
//! character; here the session loads its collision and the character's
//! shape and motion from the dats, as it does live). Set `OPENAC_DAT_DIR`
//! or install the dats under `Documents\Asheron's Call`; these scenarios
//! are ignored by default (`--include-ignored`).

use std::sync::{Arc, OnceLock};

use ac_physics::content::publish_landblock;
use ac_physics::{CollisionWorld, PhysicsEngine, Vec3};
use ac_runtime::gameplay::GameTables;
use ac_runtime::movement::WorldContent;

/// The human setup and motion table the character is created with.
pub const HUMAN_SETUP: u32 = 0x0200_0001;
pub const HUMAN_MOTION: u32 = 0x0900_0001;
/// Gravity and reporting collisions: a body that stands on the ground.
pub const BODY_STATE: u32 = 0x0000_0408;

fn directory() -> Option<std::path::PathBuf> {
    std::env::var_os("OPENAC_DAT_DIR").map(std::path::PathBuf::from)
}

/// The dats every dats scenario of the process shares.
pub fn content() -> Arc<WorldContent> {
    static CONTENT: OnceLock<Arc<WorldContent>> = OnceLock::new();
    CONTENT
        .get_or_init(|| {
            Arc::new(WorldContent::open(directory().as_deref()).expect("the retail dats"))
        })
        .clone()
}

/// The game tables read from the same dats.
pub fn tables() -> Arc<GameTables> {
    static TABLES: OnceLock<Arc<GameTables>> = OnceLock::new();
    TABLES
        .get_or_init(|| {
            Arc::new(GameTables::open(directory().as_deref()).expect("the retail tables"))
        })
        .clone()
}

/// The terrain's height at `(x, y)` inside `landblock` (its top 16 bits),
/// metres.
pub fn ground_z(landblock: u32, x: f32, y: f32) -> f32 {
    let content = content();
    let mut engine = PhysicsEngine::new();
    engine.attach_world(CollisionWorld::new());
    publish_landblock(
        &mut engine,
        content.dats(),
        content.height_table(),
        landblock | 0xFFFF,
        Vec3::ZERO,
    )
    .expect("the landblock loads");
    engine
        .sample_terrain_z(x, y)
        .expect("the spot is on the landblock")
}
