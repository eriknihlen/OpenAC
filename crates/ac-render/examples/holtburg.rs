//! Draw the world around a made-up player in Holtburg, without a server, and save a screenshot.
//!
//! Opens a window fed by a hand-built presentation snapshot (one player object standing near the
//! Holtburg lifestone), waits for the loader to fill the area, writes a PNG and exits.
//!
//! Run: `cargo run -p ac-render --example holtburg -- [out.png] [seconds to wait] [zoom steps] [fly]`
//! (default `logs/holtburg.png`, 8 s; `fly` switches to the fly camera first). The dats come from
//! `OPENAC_DAT_DIR` or the usual place. `OPENAC_EXAMPLE_POSITION="<cell hex> <x> <y> <z> [heading]"`
//! puts the player elsewhere (an interior or dungeon cell, say); heading in degrees, 0 north.
//! `OPENAC_EXAMPLE_TRAVEL="<east m/s> <north m/s> [height m]"` moves the player that fast for the
//! whole wait (streaming check), saving a numbered screenshot with the renderer's counts every 10 s.
//! `OPENAC_EXAMPLE_MOTION=walk` (or `run`, `turn`, `idle`, the default) animates the player in
//! place. `OPENAC_EXAMPLE_PORTAL=<seconds>` puts the player into portal space that long before
//! the screenshot (the destination never arrives, so the tunnel holds).

use std::error::Error;
use std::path::PathBuf;
use std::sync::mpsc;
use std::thread;
use std::time::Duration;

use ac_proto::{
    AnimPartChange, ModelData, Position, Quaternion, SubPalette, TextureChange, Vector3,
};
use ac_render::{Color, HostEvent, HostOptions, WindowHost, WindowSpec};
use ac_runtime::session::PresentationEvent;
use ac_runtime::world::{ObjectDelta, ObjectKey, ObjectView, WorldSnapshot};

fn main() -> Result<(), Box<dyn Error>> {
    let mut args = std::env::args().skip(1);
    let out = PathBuf::from(args.next().unwrap_or_else(|| "logs/holtburg.png".into()));
    let wait = Duration::from_secs(args.next().and_then(|s| s.parse().ok()).unwrap_or(8));
    let zoom: f32 = args.next().and_then(|s| s.parse().ok()).unwrap_or(0.0);
    let fly = args.next().is_some_and(|a| a == "fly");
    let dat_dir = std::env::var_os("OPENAC_DAT_DIR").map(PathBuf::from);
    warn_if_outside_its_cell(dat_dir.as_deref(), &player());

    let mut options = HostOptions::with_overrides(dat_dir, None);
    // The frame diagnostics, as openac reads them (docs/launch-options.md).
    let on = |name: &str| std::env::var(name).is_ok_and(|v| v == "1");
    options.graphics.uncapped = on("OPENAC_UNCAPPED_RENDER");
    options.graphics.frame_profiler = on("OPENAC_FRAME_PROF");
    options.graphics.no_block_compression = on("OPENAC_NO_BLOCK_COMPRESSION");
    options.graphics.frame_history = std::env::var_os("OPENAC_FRAME_HISTORY").map(PathBuf::from);
    options.graphics.gpu_adapter = std::env::var("OPENAC_GPU_ADAPTER").ok();
    options.graphics.diagnostics_dir = Some(PathBuf::from("logs"));
    // The time of day, day group and sound switches, as openac reads them.
    options.sky = ac_render::environment::SkyOptions::from_vars(|k| std::env::var(k).ok());
    options.audio.launch = ac_audio::AudioLaunchOptions::from_env();
    // The atmospheric rendering at a preset, as the options window sets it.
    if let Ok(preset) = std::env::var("OPENAC_EXAMPLE_ATMOSPHERE")
        && preset != "off"
    {
        let mut selection = ac_host::settings::RenderPackSelection::atmospheric(&preset);
        // Setting values as the options window stores them: `id=value,...`.
        for pair in std::env::var("OPENAC_EXAMPLE_ATMOSPHERE_SET")
            .unwrap_or_default()
            .split(',')
            .filter_map(|p| p.split_once('='))
        {
            selection.set_user_value(pair.0.trim(), pair.1.trim());
        }
        options
            .settings
            .display
            .set_render_pack_selection(&selection);
    }
    // `OPENAC_EXAMPLE_LANDSCAPE_DETAIL=1|0`: the landscape detail textures
    // on or off, as the options window sets them.
    if let Ok(v) = std::env::var("OPENAC_EXAMPLE_LANDSCAPE_DETAIL") {
        options.settings.display.landscape_detail_textures = v == "1";
    }
    let (host, handle, events) = WindowHost::new(options)?;
    let driver = thread::spawn(move || -> Result<(), String> {
        let (tx, rx) = mpsc::channel();
        tx.send(PresentationEvent::Snapshot(WorldSnapshot {
            player_guid: Some(0x5000_0001),
            objects: vec![player()],
        }))
        .map_err(|e| e.to_string())?;
        let spec = WindowSpec {
            title: "openAC example: Holtburg".into(),
            width: 1280,
            height: 720,
            clear_color: Color {
                r: 0.45,
                g: 0.6,
                b: 0.85,
                a: 1.0,
            },
        };
        let result = (|| {
            handle
                .attach_to_session(spec, rx, std::sync::mpsc::channel().0)
                .map_err(|e| e.to_string())?;
            loop {
                match events.recv_timeout(Duration::from_secs(10)) {
                    Ok(HostEvent::Attached(adapter)) => {
                        println!("attached on {:?} ({})", adapter.backend, adapter.name);
                        break;
                    }
                    Ok(other) => println!("{other:?}"),
                    Err(e) => return Err(format!("no attach: {e}")),
                }
            }
            let attached_at = std::time::Instant::now();
            if fly {
                handle.toggle_fly().map_err(|e| e.to_string())?;
            }
            if zoom != 0.0 {
                handle.zoom(zoom).map_err(|e| e.to_string())?;
            }
            match std::env::var("OPENAC_EXAMPLE_TRAVEL")
                .ok()
                .and_then(|s| parse_travel(&s))
            {
                Some(travel) => travel_for(&tx, &handle, &events, wait, travel, &out),
                None => match portal_lead() {
                    Some(lead) => portal_space(&tx, wait, lead),
                    None => thread::sleep(wait),
                },
            }
            handle.screenshot(out.clone()).map_err(|e| e.to_string())?;
            loop {
                match events.recv_timeout(Duration::from_secs(10)) {
                    Ok(HostEvent::ScreenshotSaved { path, stats }) => {
                        println!("saved {}: {stats:?}", path.display());
                        break;
                    }
                    Ok(HostEvent::ScreenshotFailed(e)) => return Err(e.to_string()),
                    Ok(other) => println!("{other:?}"),
                    Err(e) => return Err(format!("no screenshot: {e}")),
                }
            }
            handle.detach().map_err(|e| e.to_string())?;
            loop {
                match events.recv_timeout(Duration::from_secs(10)) {
                    Ok(HostEvent::Detached {
                        frames_presented, ..
                    }) => {
                        let seconds = attached_at.elapsed().as_secs_f64();
                        println!(
                            "{frames_presented} frames in {seconds:.1} s ({:.1} per second)",
                            frames_presented as f64 / seconds
                        );
                        return Ok(());
                    }
                    Ok(other) => println!("{other:?}"),
                    Err(e) => return Err(format!("no detach: {e}")),
                }
            }
        })();
        // Keep the session side open until the end so the window does not detach early.
        drop(tx);
        let _ = handle.shutdown();
        result
    });
    host.run()?;
    driver.join().map_err(|_| "driver panicked")??;
    Ok(())
}

/// `OPENAC_EXAMPLE_PORTAL=<seconds>`: the player enters portal space that
/// long before the screenshot (and never arrives, so the tunnel holds).
fn portal_lead() -> Option<Duration> {
    let seconds: f64 = std::env::var("OPENAC_EXAMPLE_PORTAL").ok()?.parse().ok()?;
    Some(Duration::from_secs_f64(seconds.max(0.0)))
}

/// Wait out `wait`, entering portal space `lead` before its end.
fn portal_space(tx: &mpsc::Sender<PresentationEvent>, wait: Duration, lead: Duration) {
    thread::sleep(wait.saturating_sub(lead));
    let _ = tx.send(PresentationEvent::PortalSpace(
        ac_runtime::world::transit::PortalSpaceEvent::Entered {
            teleport_sequence: 1,
        },
    ));
    thread::sleep(lead.min(wait));
}

/// A human standing near the Holtburg lifestone, facing north (or where
/// `OPENAC_EXAMPLE_POSITION` says).
fn player() -> ObjectView {
    let position = std::env::var("OPENAC_EXAMPLE_POSITION")
        .ok()
        .and_then(|s| parse_position(&s))
        .unwrap_or(Position {
            cell_id: 0xA9B4_0019,
            origin: Vector3 {
                x: 84.0,
                y: 7.1,
                z: 94.005,
            },
            orientation: Quaternion {
                w: 1.0,
                x: 0.0,
                y: 0.0,
                z: 0.0,
            },
        });
    ObjectView {
        key: ObjectKey {
            local_id: 1,
            incarnation: 1,
        },
        guid: 0x5000_0001,
        name: Some("Example".into()),
        setup_id: Some(0x0200_0001),
        position: Some(position),
        parent: None,
        placement: None,
        rest_placement: 0,
        scale: None,
        translucency: None,
        state: 0,
        velocity: None,
        model: clothed(),
        motion_table_id: Some(0x0900_0001),
        sound_table_id: None,
        radar: ac_runtime::world::RadarFacts::default(),
        motion: example_motion(),
        effects: ac_runtime::world::EffectFacts::default(),
        step_blend: None,
    }
}

/// A clothed human's appearance (palette ranges, texture swaps and part
/// swaps), as the local server sent it for a test character.
fn clothed() -> ModelData {
    const SUB_PALETTES: [(u32, u8, u8); 21] = [
        (0x04001FE3, 24, 8),
        (0x040002BA, 0, 24),
        (0x040002BC, 32, 8),
        (0x040005CB, 40, 24),
        (0x04000695, 92, 4),
        (0x040005F4, 152, 8),
        (0x04000699, 136, 16),
        (0x04001677, 136, 24),
        (0x04000436, 80, 12),
        (0x04000699, 92, 4),
        (0x04000694, 216, 24),
        (0x040005E0, 128, 8),
        (0x040005E0, 174, 12),
        (0x04000450, 96, 12),
        (0x04000450, 116, 12),
        (0x04000450, 186, 12),
        (0x04000450, 206, 10),
        (0x04000450, 108, 8),
        (0x0400168D, 240, 16),
        (0x040005AD, 168, 6),
        (0x040013F6, 160, 8),
    ];
    const TEXTURES: [(u8, u32, u32); 30] = [
        (16, 0x05000098, 0x050010B7),
        (16, 0x0500024C, 0x05001140),
        (16, 0x050002F5, 0x05001179),
        (16, 0x0500025C, 0x0500119D),
        (9, 0x050003D5, 0x0500025F),
        (9, 0x050003D4, 0x0500025E),
        (0, 0x05000BB0, 0x0500025D),
        (0, 0x05000CBE, 0x05000CEA),
        (10, 0x050002CC, 0x050002BE),
        (13, 0x050002CC, 0x050002BE),
        (5, 0x050003D8, 0x050002E4),
        (1, 0x050003D8, 0x050002E4),
        (6, 0x0500197A, 0x05002230),
        (6, 0x05001979, 0x05002235),
        (2, 0x05002230, 0x05002230),
        (2, 0x05002235, 0x05002235),
        (0, 0x05000BB0, 0x050002C8),
        (0, 0x05000CBE, 0x050002C8),
        (9, 0x050003D5, 0x05001897),
        (9, 0x050003D4, 0x05001898),
        (10, 0x0500187B, 0x05001894),
        (11, 0x0500187A, 0x05001893),
        (13, 0x0500187B, 0x05001894),
        (14, 0x0500187A, 0x05001893),
        (15, 0x050003D3, 0x05002041),
        (12, 0x050003D3, 0x05002041),
        (3, 0x05001FA8, 0x05001FA8),
        (7, 0x05001FA8, 0x05001FA8),
        (4, 0x05001FA8, 0x05001FA8),
        (8, 0x05001FA8, 0x05001FA8),
    ];
    const PARTS: [(u8, u32); 21] = [
        (16, 0x0100482F),
        (9, 0x01000054),
        (10, 0x01001222),
        (13, 0x01001220),
        (5, 0x01001217),
        (1, 0x01001218),
        (6, 0x01001CF4),
        (2, 0x01003088),
        (0, 0x0100120B),
        (9, 0x0100120D),
        (10, 0x010019F7),
        (11, 0x010019ED),
        (13, 0x010019FF),
        (14, 0x010019EF),
        (16, 0x01003090),
        (15, 0x01000077),
        (12, 0x01000076),
        (3, 0x01002A71),
        (7, 0x01002A72),
        (4, 0x01002A78),
        (8, 0x01002A79),
    ];
    ModelData {
        base_palette_id: Some(0x0400_007E),
        sub_palettes: SUB_PALETTES
            .iter()
            .map(|&(sub_palette_id, offset, length)| SubPalette {
                sub_palette_id,
                offset,
                length,
            })
            .collect(),
        texture_changes: TEXTURES
            .iter()
            .map(|&(part_index, old_texture, new_texture)| TextureChange {
                part_index,
                old_texture,
                new_texture,
            })
            .collect(),
        // The server also swaps parts 17..=33 for an empty model.
        anim_part_changes: PARTS
            .iter()
            .copied()
            .chain((17..=33).map(|part| (part, 0x0100_01EC)))
            .map(|(part_index, new_model_id)| AnimPartChange {
                part_index,
                new_model_id,
            })
            .collect(),
    }
}

/// `"<cell hex> <x> <y> <z> [heading degrees]"`.
/// A made-up interior position can lie in no cell at all (beyond a room's
/// wall): the game never puts a player there, and the window then shows
/// the void (the clear colour) where the floor would be. Say so before the
/// shot is mistaken for a rendering fault.
fn warn_if_outside_its_cell(dat_dir: Option<&std::path::Path>, player: &ObjectView) {
    use ac_render::camera_collision::build_neighbourhood;
    use ac_render::world_frame::LandblockCoord;
    let Some(position) = player.position.as_ref() else {
        return;
    };
    if position.cell_id & 0xFFFF < 0x0100 {
        return;
    }
    let Some(dir) = dat_dir.map(PathBuf::from).or_else(ac_dat::default_dat_dir) else {
        return;
    };
    let Ok(content) = ac_render::content::Content::open(&dir) else {
        return;
    };
    let Ok(Some(region)) = content
        .dats
        .get::<ac_dat::objects::Region>(ac_render::assets::REGION_ID)
    else {
        return;
    };
    let center = LandblockCoord::of_cell(position.cell_id);
    let Ok(engine) =
        build_neighbourhood(&content.dats, &region.land_defs.land_height_table, center)
    else {
        return;
    };
    let o = &position.origin;
    let (_, found) = engine.adjust_position(position.cell_id, glam::Vec3::new(o.x, o.y, o.z));
    if !found {
        println!(
            "warning: {} {} {} is in no cell reachable from {:08X}: the player stands outside the rooms",
            o.x, o.y, o.z, position.cell_id
        );
    }
}

fn parse_position(s: &str) -> Option<Position> {
    let mut parts = s.split_whitespace();
    let cell = u32::from_str_radix(parts.next()?.trim_start_matches("0x"), 16).ok()?;
    let mut num = || parts.next().and_then(|p| p.parse::<f32>().ok());
    let (x, y, z) = (num()?, num()?, num()?);
    // Heading clockwise from north: a turn of -heading about z.
    let half = -num().unwrap_or(0.0).to_radians() * 0.5;
    Some(Position {
        cell_id: cell,
        origin: Vector3 { x, y, z },
        orientation: Quaternion {
            w: half.cos(),
            x: 0.0,
            y: 0.0,
            z: half.sin(),
        },
    })
}

/// A straight run: metres per second east and north, and a height above
/// the start (the player floats; the camera follows it).
#[derive(Clone, Copy)]
struct Travel {
    east: f32,
    north: f32,
    height: f32,
}

fn parse_travel(s: &str) -> Option<Travel> {
    let mut parts = s.split_whitespace().map(|p| p.parse::<f32>().ok());
    Some(Travel {
        east: parts.next()??,
        north: parts.next()??,
        height: parts.next().flatten().unwrap_or(0.0),
    })
}

/// Move the player along `travel` for `wait`, ten times a second, saving a
/// screenshot next to `out` every 10 s.
fn travel_for(
    tx: &mpsc::Sender<PresentationEvent>,
    handle: &ac_render::HostHandle,
    events: &mpsc::Receiver<HostEvent>,
    wait: Duration,
    travel: Travel,
    out: &std::path::Path,
) {
    let start = player();
    let Some(p0) = start.position else {
        return;
    };
    // Global metres: landblock x/y * 192 + local.
    let gx = f64::from(p0.cell_id >> 24) * 192.0 + f64::from(p0.origin.x);
    let gy = f64::from((p0.cell_id >> 16) & 0xFF) * 192.0 + f64::from(p0.origin.y);
    let began = std::time::Instant::now();
    let mut next_shot = 10.0;
    let mut shot = 0;
    while began.elapsed() < wait {
        let t = began.elapsed().as_secs_f64();
        let x = gx + t * f64::from(travel.east);
        let y = gy + t * f64::from(travel.north);
        let (bx, by) = ((x / 192.0).floor(), (y / 192.0).floor());
        let (lx, ly) = (x - bx * 192.0, y - by * 192.0);
        let cell = ((bx as u32) << 24)
            | ((by as u32) << 16)
            | ((lx / 24.0) as u32 * 8 + (ly / 24.0) as u32 + 1);
        let mut view = start.clone();
        view.position = Some(Position {
            cell_id: cell,
            origin: Vector3 {
                x: lx as f32,
                y: ly as f32,
                z: p0.origin.z + travel.height,
            },
            orientation: p0.orientation,
        });
        if tx
            .send(PresentationEvent::Deltas(vec![ObjectDelta::Upsert(
                Box::new(view),
            )]))
            .is_err()
        {
            return;
        }
        if t >= next_shot {
            next_shot += 10.0;
            shot += 1;
            let stem = out.file_stem().and_then(|s| s.to_str()).unwrap_or("travel");
            let path = out.with_file_name(format!("{stem}-{shot}.png"));
            if handle.screenshot(path).is_ok() {
                while let Ok(event) = events.recv_timeout(Duration::from_secs(10)) {
                    if let HostEvent::ScreenshotSaved { path, stats } = event {
                        println!("{t:.0} s at cell {cell:08X}: {}: {stats:?}", path.display());
                        break;
                    }
                }
            }
        }
        thread::sleep(Duration::from_millis(100));
    }
}

/// The made-up player's motion: `OPENAC_EXAMPLE_MOTION` = `walk`, `run`,
/// `turn` or `idle` (the default), as the session would publish it.
fn example_motion() -> Option<ac_runtime::world::MotionView> {
    use ac_motion::motion_command::{NON_COMBAT, READY, RUN_FORWARD, TURN_RIGHT, WALK_FORWARD};
    let (forward, speed, turn) = match std::env::var("OPENAC_EXAMPLE_MOTION").as_deref() {
        Ok("walk") => (WALK_FORWARD, 1.0, 0),
        Ok("run") => (RUN_FORWARD, 2.5, 0),
        Ok("turn") => (READY, 1.0, TURN_RIGHT),
        _ => (READY, 1.0, 0),
    };
    Some(ac_runtime::world::MotionView {
        state: ac_physics::movement::InboundInterpretedState {
            current_style: NON_COMBAT,
            forward_command: forward,
            forward_speed: speed,
            side_step_command: 0,
            side_step_speed: 1.0,
            turn_command: turn,
            turn_speed: 1.0,
            actions: None,
        },
        serial: 0,
        grounded: true,
        standing_long_jump: false,
        // A body the session's physics moves, standing on flat ground: the
        // chase camera takes over only once the controller is in charge.
        body: Some(ac_runtime::world::BodyFacts {
            velocity: [0.0; 3],
            in_contact: true,
            contact_normal: [0.0, 0.0, 1.0],
        }),
    })
}
