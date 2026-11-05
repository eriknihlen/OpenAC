//! Landblocks leaving the window: released over frames, never shown with
//! the window that replaced them, and released whole before they load
//! again.

use std::path::PathBuf;
use std::sync::Arc;
use std::time::{Duration, Instant};

use ac_host::settings::DisplaySettings;

use super::*;
use crate::camera::FlyInput;
use crate::content::ContentSlot;

/// A resident whose landblock the cell dat lacks: it counts as arrived,
/// and it needs no GPU.
fn missing() -> Resident {
    Resident {
        tier: Tier::Near,
        tile: None,
        missing: true,
        near: None,
        near_requested: false,
    }
}

#[test]
fn a_destination_is_not_ready_while_the_last_window_is_being_released() {
    let block = LandblockCoord { x: 0xA9, y: 0xB4 };
    let residents = HashMap::from([(block, missing())]);
    let models = crate::IdMap::default();
    let walk = FrameWalk::new();
    let facts = |retiring| RevealState {
        residents: &residents,
        models: &models,
        walk: &walk,
        server_models_ready: true,
        retiring,
        unavailable: false,
        window: RevealWindow {
            near_radius: 0,
            far_radius: 0,
        },
    };
    let cell = 0xA9B4_0001;
    assert!(facts(false).render_neighborhood_ready(cell, 0, 0));
    assert!(
        !facts(true).render_neighborhood_ready(cell, 0, 0),
        "what the window left behind is released before the destination shows"
    );
}

fn dat_dir() -> PathBuf {
    std::env::var_os("OPENAC_DAT_DIR")
        .map(PathBuf::from)
        .or_else(ac_dat::default_dat_dir)
        .expect("no dat directory: set OPENAC_DAT_DIR")
}

fn renderer(device: &wgpu::Device, queue: &wgpu::Queue) -> WorldRenderer {
    renderer_with_near(device, queue, 0)
}

fn renderer_with_near(device: &wgpu::Device, queue: &wgpu::Queue, near: u32) -> WorldRenderer {
    let content = Arc::new(ContentSlot::new(dat_dir()));
    content.get().expect("open the retail dats");
    let settings = WorldSettings {
        content,
        quality_overrides: crate::quality::QualityOverrides {
            near_radius: Some(near),
            far_radius: Some(near),
            msaa_samples: Some(0),
            ..Default::default()
        },
        residency: Default::default(),
        orbit: Default::default(),
        reveal_radius: None,
        sky: Default::default(),
        audio: Default::default(),
    };
    WorldRenderer::new(
        device,
        queue,
        wgpu::TextureFormat::Rgba8Unorm,
        96,
        64,
        &settings,
        &DisplaySettings::default(),
        &[1],
    )
}

/// Prepare frames until `done` holds (at most a minute).
fn frames_until(
    world: &mut WorldRenderer,
    device: &wgpu::Device,
    queue: &wgpu::Queue,
    what: &str,
    mut done: impl FnMut(&mut WorldRenderer) -> bool,
) {
    let started = Instant::now();
    loop {
        world.prepare(device, queue, None, FlyInput::default());
        if done(world) {
            return;
        }
        assert!(
            started.elapsed() < Duration::from_secs(60),
            "never: {what} ({:?})",
            world.failure()
        );
        std::thread::sleep(Duration::from_millis(10));
    }
}

/// Holtburg: terrain, statics and buildings with interior cells.
const HOLTBURG: LandblockCoord = LandblockCoord { x: 0xA9, y: 0xB4 };

/// Load Holtburg near and wait until all of it has arrived.
fn load_holtburg(world: &mut WorldRenderer, device: &wgpu::Device, queue: &wgpu::Queue) {
    let _ = world
        .origin
        .try_initialize(i32::from(HOLTBURG.x), i32::from(HOLTBURG.y));
    world.load(HOLTBURG, Tier::Near);
    frames_until(world, device, queue, "Holtburg arrives", |w| {
        w.residents
            .get(&HOLTBURG)
            .is_some_and(|r| r.tile.is_some() && r.near.is_some())
    });
}

fn holds_holtburg(world: &WorldRenderer) -> (bool, bool) {
    let cell = HOLTBURG.terrain_id() | 0x0001;
    (
        world.collision.terrain_origin(cell).is_some(),
        !world.walk.is_empty(),
    )
}

#[test]
#[ignore = "needs the retail dat files"]
fn a_landblock_leaving_the_window_goes_at_once_and_is_released_on_a_later_frame() {
    let Some((device, queue)) = crate::test_gpu::device() else {
        eprintln!("no GPU adapter on this machine");
        return;
    };
    let mut world = renderer(&device, &queue);
    load_holtburg(&mut world, &device, &queue);
    assert_eq!(holds_holtburg(&world), (true, true), "terrain and cells in");

    world.unload(HOLTBURG);
    // Out of the window at once; its collision and cells wait their turn.
    assert!(!world.residents.contains_key(&HOLTBURG));
    assert_eq!(world.retiring.len(), 1);
    assert_eq!(holds_holtburg(&world), (true, true));

    world.prepare(&device, &queue, None, FlyInput::default());
    assert!(
        world.retiring.is_empty(),
        "released within a frame's budget"
    );
    assert_eq!(holds_holtburg(&world), (false, false), "released whole");
}

#[test]
#[ignore = "needs the retail dat files"]
fn a_landblock_wanted_again_before_its_release_is_released_whole_first() {
    let Some((device, queue)) = crate::test_gpu::device() else {
        eprintln!("no GPU adapter on this machine");
        return;
    };
    let mut world = renderer(&device, &queue);
    load_holtburg(&mut world, &device, &queue);

    // A short hop away and back: Holtburg leaves the window and is asked
    // for again before any frame released it.
    world.unload(HOLTBURG);
    world.load(HOLTBURG, Tier::Near);
    assert!(world.retiring.is_empty(), "no old copy is left to release");
    assert_eq!(
        holds_holtburg(&world),
        (false, false),
        "the old copy is gone before the new one arrives"
    );
    let fresh = &world.residents[&HOLTBURG];
    assert!(fresh.tile.is_none() && fresh.near.is_none() && fresh.near_requested);

    // The new copy arrives whole, and later frames release nothing of it.
    load_holtburg(&mut world, &device, &queue);
    assert_eq!(holds_holtburg(&world), (true, true));
    world.prepare(&device, &queue, None, FlyInput::default());
    assert_eq!(holds_holtburg(&world), (true, true));
}

/// A server object standing at the destination, its model not here yet.
fn object_at_holtburg(local_id: u32) -> ac_runtime::world::ObjectView {
    use ac_runtime::world::{EffectFacts, ObjectKey, ObjectView, RadarFacts};
    ObjectView {
        key: ObjectKey {
            local_id,
            incarnation: 1,
        },
        guid: 0x8000_0000 | local_id,
        name: None,
        setup_id: Some(0x0200_0001),
        position: Some(ac_proto::Position {
            cell_id: HOLTBURG.terrain_id() | 0x0019,
            origin: ac_proto::Vector3 {
                x: 10.0,
                y: 20.0,
                z: 30.0,
            },
            orientation: ac_proto::Quaternion::IDENTITY,
        }),
        parent: None,
        placement: None,
        rest_placement: 0,
        scale: None,
        translucency: None,
        state: 0,
        velocity: None,
        model: ac_proto::ModelData::default(),
        motion_table_id: None,
        sound_table_id: None,
        radar: RadarFacts::default(),
        motion: None,
        effects: EffectFacts::default(),
        step_blend: None,
    }
}

fn scene_of(objects: Vec<ac_runtime::world::ObjectView>) -> Scene {
    let mut scene = Scene::default();
    scene.apply(ac_runtime::session::PresentationEvent::Snapshot(
        ac_runtime::world::WorldSnapshot {
            player_guid: None,
            objects,
        },
    ));
    scene
}

/// OpenAC `WbDrawDispatcherCompositeWarmupTests.AnEntityThatLeavesTheWorldIsNoLongerWaitedFor`:
/// the reveal waits for the models of the server objects in the world now,
/// so one that leaves while its model is still loading holds it no longer.
#[test]
#[ignore = "needs the retail dat files"]
fn a_server_object_that_leaves_the_world_no_longer_holds_the_reveal() {
    let Some((device, queue)) = crate::test_gpu::device() else {
        eprintln!("no GPU adapter on this machine");
        return;
    };
    let mut world = renderer(&device, &queue);

    world.request_missing_models(Some(&scene_of(vec![object_at_holtburg(7)])));
    assert!(
        !world.server_models_ready,
        "an object whose model has not arrived holds the reveal"
    );

    // It leaves the world (killed, decayed, logged out) before its model
    // arrives.
    world.request_missing_models(Some(&scene_of(Vec::new())));
    assert!(
        world.server_models_ready,
        "an object that left holds the reveal no longer"
    );
}

/// OpenAC `RetailPViewRenderer` (`ObjectRingLimit = ctx.RenderRadius`):
/// each frame walks with the object range set to the near radius (the walk
/// itself stops at ring 4).
#[test]
#[ignore = "needs the retail dat files"]
fn the_frame_walks_with_the_near_radius_as_its_object_range() {
    let Some((device, queue)) = crate::test_gpu::device() else {
        eprintln!("no GPU adapter on this machine");
        return;
    };
    for near in [2, 5] {
        let mut world = renderer_with_near(&device, &queue, near);
        load_holtburg(&mut world, &device, &queue);
        frames_until(&mut world, &device, &queue, "the terrain is bound", |w| {
            w.terrain_group.is_some()
        });
        world.prepare(&device, &queue, None, FlyInput::default());
        assert_eq!(world.walk.object_ring_limit, near);
    }
}
