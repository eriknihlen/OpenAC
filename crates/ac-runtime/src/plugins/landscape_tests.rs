//! The landscape calls through the plugin binding: each landblock's terrain
//! vertices and the region's terrain colours, from the installed game files
//! where there are some, and the inert answer where there are none.

use std::time::Instant;

use ac_dat::objects::LandBlock;
use ac_physics::TerrainSurface;
use ac_plugin::abi::world::{
    LANDSCAPE_TERRAIN_MAX, LandblockTerrain, ROAD_TERRAIN_TYPE, TerrainColor,
};
use ac_plugin::abi::{Wire, calls};
use ac_plugin::{Caller, SessionApi};

use super::PluginBinding;
use super::tests::{in_world, with_binding};
use crate::navigation::walk_tests::{Session, content};
use crate::session::Outbox;

/// Holtburg's landblock, on dry land.
const HOLTBURG: u16 = 0xA9B4;
/// Open sea far west of Holtburg.
const OPEN_SEA: u16 = 0x01B4;
/// A landblock in the map grid's last column, which has no terrain file.
const NO_TERRAIN: u16 = 0xFF00;

fn call_as<C: ac_plugin::abi::Call>(
    binding: &mut PluginBinding<'_>,
    request: &C::Request,
) -> C::Reply {
    let bytes = binding
        .call(
            Caller {
                plugin_id: "a",
                api_version: ac_plugin::abi::API_CURRENT,
            },
            C::IMPORT,
            &request.to_bytes(),
        )
        .expect("a well-formed request");
    C::Reply::from_bytes(&bytes).expect("a well-formed reply")
}

fn terrain(session: &mut Session, landblocks: &[u16]) -> Vec<LandblockTerrain> {
    let Session { state, outbox, .. } = session;
    with_binding(state, outbox, |b| {
        call_as::<calls::LandscapeTerrain>(b, &landblocks.to_vec())
    })
}

fn palette(session: &mut Session) -> Vec<TerrainColor> {
    let Session { state, outbox, .. } = session;
    with_binding(state, outbox, |b| {
        call_as::<calls::LandscapeTerrainPalette>(b, &())
    })
}

// Without the game files there is nothing to read: both answer empty.
#[test]
fn without_the_game_files_the_landscape_reads_empty() {
    let mut state = in_world();
    let mut outbox = Outbox::new();
    with_binding(&mut state, &mut outbox, |b| {
        assert!(call_as::<calls::LandscapeTerrain>(b, &vec![HOLTBURG]).is_empty());
        assert!(call_as::<calls::LandscapeTerrainPalette>(b, &()).is_empty());
    });
}

/// Land, a landblock without terrain and sea, in the order asked, the one
/// without left out; each vertex at the height the physics ground has
/// there and water where the ground is.
#[test]
#[ignore = "needs the retail dat files"]
fn the_landblocks_asked_for_come_back_in_order_with_the_physics_heights() {
    let mut session = Session::at(60.0, 150.0);
    let answered = terrain(&mut session, &[HOLTBURG, NO_TERRAIN, OPEN_SEA]);
    let order: Vec<u16> = answered.iter().map(|t| t.landblock).collect();
    assert_eq!(order, [HOLTBURG, OPEN_SEA]);
    let world = content();
    for answer in &answered {
        assert_eq!(answer.vertices.len(), 81);
        let block = world
            .dats()
            .get::<LandBlock>(u32::from(answer.landblock) << 16 | 0xFFFF)
            .unwrap()
            .unwrap();
        let ground = TerrainSurface::from_landblock(&block, world.height_table());
        for x in 0..9 {
            for y in 0..9 {
                let vertex = answer.vertex(x, y).unwrap();
                assert_eq!(
                    vertex.height_meters,
                    TerrainSurface::vertex_height(&block.height, world.height_table(), x, y)
                );
                if x < 8 && y < 8 {
                    let at = ground.sample_z(24.0 * x as f32, 24.0 * y as f32);
                    assert!((vertex.height_meters - at).abs() < 1e-4, "({x}, {y})");
                }
            }
        }
    }
    let water = |t: &LandblockTerrain| {
        t.vertices
            .iter()
            .filter(|v| (0x10..=0x14).contains(&v.terrain_type))
            .count()
    };
    assert_eq!(water(&answered[0]), 0);
    assert_eq!(water(&answered[1]), 81);
}

/// A request longer than the cap is answered for its first 256 landblocks
/// only; how long a full request takes, cold and then from the cache, is
/// printed.
#[test]
#[ignore = "needs the retail dat files"]
fn a_request_is_answered_for_its_first_256_landblocks() {
    let mut session = Session::at(60.0, 150.0);
    // A 20 by 15 block of land and sea around Holtburg: 300 landblocks.
    let asked: Vec<u16> = (0xA0u16..0xB4)
        .flat_map(|x| (0xAAu16..0xB9).map(move |y| x << 8 | y))
        .collect();
    assert_eq!(asked.len(), 300);
    let started = Instant::now();
    let answered = terrain(&mut session, &asked);
    let cold = started.elapsed();
    let started = Instant::now();
    let again = terrain(&mut session, &asked);
    let warm = started.elapsed();
    println!(
        "{} landblocks: cold {:.2} ms, cached {:.2} ms",
        answered.len(),
        cold.as_secs_f64() * 1000.0,
        warm.as_secs_f64() * 1000.0
    );
    assert_eq!(answered.len(), LANDSCAPE_TERRAIN_MAX);
    let order: Vec<u16> = answered.iter().map(|t| t.landblock).collect();
    assert_eq!(order, asked[..LANDSCAPE_TERRAIN_MAX]);
    assert_eq!(again, answered);
}

/// One entry a terrain type, each with its texture and map colours and the
/// region's name, then the road's; worked out once and kept.
#[test]
#[ignore = "needs the retail dat files"]
fn every_terrain_type_and_the_road_have_their_colours_and_names() {
    let mut session = Session::at(60.0, 150.0);
    let started = Instant::now();
    let colours = palette(&mut session);
    let first = started.elapsed();
    let started = Instant::now();
    let again = palette(&mut session);
    let kept = started.elapsed();
    println!(
        "{} colours: first {:.1} ms, then {:.3} ms",
        colours.len(),
        first.as_secs_f64() * 1000.0,
        kept.as_secs_f64() * 1000.0
    );
    let region = content().region().expect("the region");
    assert_eq!(colours.len(), region.terrain_info.terrain_types.len() + 1);
    for (i, (colour, defined)) in colours
        .iter()
        .zip(&region.terrain_info.terrain_types)
        .enumerate()
    {
        assert_eq!(usize::from(colour.terrain_type), i);
        assert!(colour.rgb != 0 && colour.rgb <= 0xFF_FFFF, "{colour:?}");
        assert_eq!(colour.name.as_deref(), Some(defined.terrain_name.as_str()));
        let c = defined.terrain_color;
        let map = u32::from(c.red) << 16 | u32::from(c.green) << 8 | u32::from(c.blue);
        assert_eq!(colour.map_rgb, map, "{colour:?}");
    }
    let road = colours.last().unwrap();
    assert_eq!(
        (road.terrain_type, road.name.as_deref()),
        (ROAD_TERRAIN_TYPE, Some("Road"))
    );
    assert!(road.rgb != 0 && road.map_rgb == road.rgb, "{road:?}");
    assert_eq!(ROAD_TERRAIN_TYPE, ac_nav::landscape::ROAD_TERRAIN_TYPE);
    assert_eq!(again, colours);
}
