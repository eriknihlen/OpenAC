//! The retail region's sky and a retail torch run through `ac-core`'s sky
//! and light rules, against values printed by OpenAC (`SkyDescLoader`,
//! `SkyStateProvider`, `SkyDayGroupSelector`, `LightInfoLoader`) reading
//! the same dat files.
//!
//! The two `to_*` functions are the mapping a caller makes from dat objects
//! to `ac-core`'s plain inputs. These tests need the dats: set
//! `OPENAC_DAT_DIR` or install them under `Documents\Asheron's Call`; run
//! with `cargo test -- --include-ignored`.

use std::path::PathBuf;

use ac_core::lighting::{LightInfoSource, LightKind, LightOwnerPlacement, load_lights};
use ac_core::sky::{
    DayGroupSource, LoadedSkyDesc, REGION_DAT_ID, SkyDatLookup, SkyDescSource,
    SkyObjectReplaceSource, SkyObjectSource, SkyStateProvider, SkyTimeSource, WorldEnvironment,
    WorldEnvironmentDefinition, load_sky_desc,
};
use ac_dat::DatCollection;
use ac_dat::objects::{GfxObj, Region, Setup, SkyDesc};
use ac_dat::types::ColorArgb;
use glam::{Quat, Vec3};

fn dats() -> DatCollection {
    let dir = std::env::var_os("OPENAC_DAT_DIR")
        .map(PathBuf::from)
        .or_else(ac_dat::default_dat_dir)
        .expect("no dat directory: set OPENAC_DAT_DIR");
    DatCollection::open(dir).expect("open the retail dats")
}

fn rgb(c: ColorArgb) -> [u8; 3] {
    [c.red, c.green, c.blue]
}

/// The region's sky as `ac-core` takes it: a field-for-field copy.
fn to_sky_source(sky: &SkyDesc) -> SkyDescSource {
    SkyDescSource {
        tick_size: sky.tick_size,
        light_tick_size: sky.light_tick_size,
        day_groups: sky
            .day_groups
            .iter()
            .map(|g| DayGroupSource {
                chance_of_occur: g.chance_of_occur,
                day_name: g.day_name.clone(),
                sky_objects: g
                    .sky_objects
                    .iter()
                    .map(|o| SkyObjectSource {
                        begin_time: o.begin_time,
                        end_time: o.end_time,
                        begin_angle: o.begin_angle,
                        end_angle: o.end_angle,
                        tex_velocity_x: o.tex_velocity_x,
                        tex_velocity_y: o.tex_velocity_y,
                        default_gfx_object_id: o.default_gfx_object_id,
                        default_pes_object_id: o.default_pes_object_id,
                        properties: o.properties,
                    })
                    .collect(),
                sky_time: g
                    .sky_time
                    .iter()
                    .map(|t| SkyTimeSource {
                        begin: t.begin,
                        dir_bright: t.dir_bright,
                        dir_heading: t.dir_heading,
                        dir_pitch: t.dir_pitch,
                        dir_color: rgb(t.dir_color),
                        amb_bright: t.amb_bright,
                        amb_color: rgb(t.amb_color),
                        min_world_fog: t.min_world_fog,
                        max_world_fog: t.max_world_fog,
                        world_fog_color: rgb(t.world_fog_color),
                        world_fog: t.world_fog,
                        sky_obj_replace: t
                            .sky_obj_replace
                            .iter()
                            .map(|r| SkyObjectReplaceSource {
                                object_index: r.object_index,
                                gfx_obj_id: r.gfx_obj_id,
                                rotate: r.rotate,
                                transparent: r.transparent,
                                luminosity: r.luminosity,
                                max_bright: r.max_bright,
                            })
                            .collect(),
                    })
                    .collect(),
            })
            .collect(),
    }
}

/// The two optional model lookups; a failed read is "unknown".
struct DatLookup<'a>(&'a DatCollection);

impl SkyDatLookup for DatLookup<'_> {
    fn gfx_obj_sort_center(&self, id: u32) -> Option<Vec3> {
        let g: GfxObj = self.0.get(id).ok()??;
        Some(Vec3::new(g.sort_center.x, g.sort_center.y, g.sort_center.z))
    }
    fn setup_default_script(&self, id: u32) -> Option<u32> {
        let s: Setup = self.0.get(id).ok()??;
        Some(s.default_script)
    }
}

/// A setup's lights as `ac-core` takes them, in stored order.
fn to_light_sources(setup: &Setup) -> Vec<LightInfoSource> {
    setup
        .lights
        .iter()
        .map(|(_, info)| {
            let f = &info.view_space_location;
            LightInfoSource {
                origin: Vec3::new(f.origin.x, f.origin.y, f.origin.z),
                orientation: Quat::from_xyzw(
                    f.orientation.x,
                    f.orientation.y,
                    f.orientation.z,
                    f.orientation.w,
                ),
                color: rgb(info.color),
                intensity: info.intensity,
                falloff: info.falloff,
                cone_angle: info.cone_angle,
            }
        })
        .collect()
}

fn load(d: &DatCollection) -> (Region, LoadedSkyDesc) {
    let region: Region = d.get(REGION_DAT_ID).unwrap().unwrap();
    let loaded = load_sky_desc(
        &to_sky_source(region.sky_info.as_ref().unwrap()),
        Some(&DatLookup(d)),
    );
    (region, loaded)
}

fn b(v: Vec3) -> [u32; 3] {
    v.to_array().map(f32::to_bits)
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_retail_sky_loads_like_openac() {
    let d = dats();
    let (region, loaded) = load(&d);

    assert_eq!(loaded.tick_size, 0.800000011920929);
    assert_eq!(loaded.light_tick_size, 15.0);
    assert_eq!(region.game_time.zero_time_of_year, 3600.0);
    let names: Vec<&str> = loaded.day_groups.iter().map(|g| g.name.as_str()).collect();
    assert_eq!(
        names,
        [
            "Sunny", "Sunny", "Sunny", "Rainy", "Sunny", "Sunny", "Sunny", "Rainy", "Clear",
            "Rainy", "Clear", "Clear", "Cloudy", "Cloudy", "Cloudy", "Rainy", "Rainy", "Rainy",
            "Rainy", "Rainy"
        ]
    );
    let shape: Vec<(usize, usize, usize)> = loaded
        .day_groups
        .iter()
        .map(|g| {
            (
                g.sky_objects.len(),
                g.sky_times.len(),
                g.sky_times.iter().map(|t| t.replaces.len()).sum(),
            )
        })
        .collect();
    let sunny = (7, 11, 33);
    let rainy = (19, 13, 65);
    assert_eq!(
        shape,
        [
            sunny,
            sunny,
            sunny,
            rainy,
            sunny,
            sunny,
            sunny,
            rainy,
            (7, 11, 34),
            rainy,
            sunny,
            sunny,
            sunny,
            sunny,
            sunny,
            rainy,
            rainy,
            rainy,
            (17, 11, 55),
            (17, 11, 55)
        ]
    );
    assert!(
        loaded
            .day_groups
            .iter()
            .all(|g| g.chance_of_occur.to_bits() == 0x40A0_0000)
    );

    // Every keyframe and override value, and every sky object's lookups,
    // summed by bit pattern (OpenAC prints the same sums).
    let mut kf_sum = 0u64;
    let mut kf_count = 0;
    let mut obj_sum = 0u64;
    let mut obj_count = 0;
    let add = |sum: &mut u64, values: &[f32]| {
        for v in values {
            *sum += u64::from(v.to_bits());
        }
    };
    for group in &loaded.day_groups {
        for st in &group.sky_times {
            let k = st.keyframe;
            kf_count += 1;
            add(
                &mut kf_sum,
                &[
                    k.begin,
                    k.sun_heading_deg,
                    k.sun_pitch_deg,
                    k.dir_color.x,
                    k.dir_color.y,
                    k.dir_color.z,
                    k.dir_bright,
                    k.amb_color.x,
                    k.amb_color.y,
                    k.amb_color.z,
                    k.amb_bright,
                    k.fog_color.x,
                    k.fog_color.y,
                    k.fog_color.z,
                    k.fog_start,
                    k.fog_end,
                ],
            );
            for r in &st.replaces {
                let c = r.authored_sort_center;
                add(
                    &mut kf_sum,
                    &[
                        r.rotate,
                        r.transparent,
                        r.luminosity,
                        r.max_bright,
                        c.x,
                        c.y,
                        c.z,
                    ],
                );
            }
        }
        for o in &group.sky_objects {
            obj_count += 1;
            obj_sum += u64::from(o.default_script_id);
            let c = o.authored_sort_center;
            add(&mut obj_sum, &[c.x, c.y, c.z]);
        }
    }
    assert_eq!((kf_count, kf_sum), (232, 6_063_247_535_021));
    assert_eq!((obj_count, obj_sum), (232, 530_365_037_534));
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_clear_day_matches_openac_value_for_value() {
    let d = dats();
    let (_, loaded) = load(&d);
    let clear = &loaded.day_groups[8];

    let objects: Vec<(u32, u32, u32, u32, [u32; 3])> = clear
        .sky_objects
        .iter()
        .map(|o| {
            (
                o.gfx_obj_id,
                o.pes_object_id,
                o.properties,
                o.default_script_id,
                b(o.authored_sort_center),
            )
        })
        .collect();
    assert_eq!(objects[0], (0x0100_15F1, 0, 0, 0, [0, 0, 0x4389_8000]));
    assert_eq!(
        objects[2],
        (
            0x0100_1F67,
            0,
            0,
            0,
            [0x44EE_AEB8, 0x44EA_58F6, 0xB784_1BA5]
        )
    );
    assert_eq!(objects[4], (0x0100_4C39, 0, 2, 0, [0, 0, 0x443B_8000]));
    assert_eq!(
        objects[6],
        (0x0200_0714, 0x3300_07DB, 0, 0x3300_07DB, [0, 0, 0])
    );
    let o2 = &clear.sky_objects[2];
    assert_eq!(
        [o2.begin_time, o2.end_time, o2.begin_angle, o2.end_angle].map(f32::to_bits),
        [0x3D23_D70A, 0x3E57_0A3D, 0xC1A0_0000, 0x433E_0000]
    );

    // Keyframe 0 (midnight) and keyframe 4.
    let k0 = clear.sky_times[0].keyframe;
    assert_eq!(k0.begin, 0.0);
    assert_eq!(k0.sun_heading_deg.to_bits(), 0x42B4_0000);
    assert_eq!(k0.sun_pitch_deg.to_bits(), 0x3F66_6666);
    assert_eq!(b(k0.dir_color), [0x3F61_E1E2, 0x3F4C_CCCD, 0x3F20_A0A1]);
    assert_eq!(k0.dir_bright.to_bits(), 0x3E80_0000);
    assert_eq!(b(k0.amb_color), [0x3F48_C8C9, 0x3EC8_C8C9, 0x3F80_0000]);
    assert_eq!(k0.amb_bright.to_bits(), 0x3ECC_CCCD);
    assert_eq!(b(k0.fog_color), [0x3DB8_B8B9, 0x3DB8_B8B9, 0x3E20_A0A1]);
    assert_eq!((k0.fog_start, k0.fog_end.to_bits()), (0.0, 0x43C8_0000));
    let r0 = clear.sky_times[0].replaces[0];
    assert_eq!(
        [r0.rotate, r0.transparent, r0.luminosity, r0.max_bright].map(f32::to_bits),
        [0x4387_0000, 0xBC23_D70A, 0x3DE1_47AE, 0x3DE1_47AE]
    );
    let k4 = clear.sky_times[4].keyframe;
    assert_eq!(k4.begin.to_bits(), 0x3E8A_3D71);
    assert_eq!(b(k4.amb_color), [0x3F66_E6E7, 0x3F66_E6E7, 0x3F80_0000]);
    assert_eq!(b(k4.fog_color), [0x3F43_C3C4, 0x3F48_C8C9, 0x3F5C_DCDD]);
    assert_eq!(
        (k4.fog_start.to_bits(), k4.fog_end.to_bits()),
        (0x4316_0000, 0x4516_0000)
    );
    assert_eq!(clear.sky_times[9].replaces.len(), 4);

    // OpenAC Interpolate over the Clear day.
    let sky = SkyStateProvider::new(clear.sky_times.iter().map(|s| s.keyframe).collect()).unwrap();
    let k = sky.interpolate(0.3);
    assert_eq!(k.sun_pitch_deg.to_bits(), 0x41D1_696A);
    assert_eq!(k.dir_bright.to_bits(), 0x3F35_7575);
    assert_eq!(b(k.fog_color), [0x3F43_C3C4, 0x3F48_C8C9, 0x3F5C_DCDD]);
    let k = sky.interpolate(0.5);
    assert_eq!(k.sun_heading_deg.to_bits(), 0x42B4_0000);
    assert_eq!(k.sun_pitch_deg.to_bits(), 0x4286_B4B4);
    assert_eq!(k.dir_bright.to_bits(), 0x3F44_8485);
    let k = sky.interpolate(0.97);
    assert_eq!(k.sun_heading_deg.to_bits(), 0x4387_0000);
    assert_eq!(k.dir_bright.to_bits(), 0x3EA6_125D);
    assert_eq!(b(k.fog_color), [0x3DB8_B8B9, 0x3DB8_B8B9, 0x3E0C_8C8D]);
    assert_eq!((k.fog_start, k.fog_end.to_bits()), (0.0, 0x43C8_0000));
}

#[test]
#[ignore = "needs the retail dat files"]
fn day_groups_are_chosen_like_openac() {
    let d = dats();
    let (region, loaded) = load(&d);
    for (year, day, expected) in [(10, 0, 16), (116, 83, 18), (312, 312, 7), (127, 359, 4)] {
        assert_eq!(
            loaded.select_day_group_index(year, 360, day, None),
            expected
        );
    }

    // The world environment built from the region: year 10, day 0 at tick 0.
    let definition = WorldEnvironmentDefinition::from_loaded_sky(
        Some(&loaded),
        Some(region.game_time.zero_time_of_year),
        None,
    )
    .unwrap();
    let (env, change) = WorldEnvironment::new(definition, 0.0).unwrap();
    let change = change.unwrap();
    assert_eq!((change.absolute_year, change.day_of_year), (10, 0));
    assert_eq!(env.active_day_group_index(), Some(16));
    assert_eq!(env.sky().keyframe_count(), 13);
}

#[test]
#[ignore = "needs the retail dat files"]
fn a_holtburg_torch_lights_like_openac() {
    let d = dats();
    let setup: Setup = d.get(0x0200_05D8).unwrap().unwrap();
    let infos = to_light_sources(&setup);
    assert_eq!(infos.len(), 1);
    assert_eq!(infos[0].color, [255, 150, 80]);
    assert_eq!(infos[0].falloff, 6.0);

    let rotation = Quat::from_xyzw(
        0.0,
        0.0,
        f32::from_bits(0x3EAF_904D),
        f32::from_bits(0x3F70_7ABB),
    );
    let lights = load_lights(
        &infos,
        &LightOwnerPlacement {
            owner_id: 0x1234,
            position: Vec3::new(100.0, 50.0, 20.0),
            rotation,
            is_dynamic: false,
            cell_id: 0xA9B4_0017,
            tracks_owner_pose: false,
        },
    );
    let l = lights[0];
    assert_eq!(
        l.kind,
        LightKind::Point,
        "a negative cone angle is a point light"
    );
    assert_eq!(l.cone_angle.to_bits(), 0xCDCD_CDCD);
    assert_eq!(l.range.to_bits(), 0x40F9_9999);
    assert_eq!(l.intensity, 100.0);
    assert_eq!(b(l.color_linear), [0x3F80_0000, 0x3F16_9697, 0x3EA0_A0A1]);
    let close =
        |a: Vec3, bits: [u32; 3]| (a - Vec3::from(bits.map(f32::from_bits))).length() < 1e-4;
    assert!(close(
        l.world_position,
        [0x42C7_847B, 0x423C_BA3F, 0x41B0_0000]
    ));
    assert!(close(
        l.world_forward,
        [0x3D8F_1815, 0x3F50_F04D, 0xBF12_D5E9]
    ));
    assert_eq!(l.ranking_point(), Vec3::new(100.0, 50.0, 20.0));
    assert_eq!(l.cell_id, 0xA9B4_0017);
}

/// OpenAC `TownNetworkFountainRoomLightInspectionTests.TownCeilingFixture_AuthoredTypeKeyAndRootRankOrigin_ArePinnedFromInstalledDat`:
/// the Town Network ceiling fixture (setup 0x02000365 in cell 0x00070144)
/// has one point light, key 0, hung at its light frame inside the fixture
/// and ranked by the fixture's root.
#[test]
#[ignore = "needs the retail dat files"]
fn the_town_ceiling_fixture_ranks_by_its_root() {
    let d = dats();
    let cell: ac_dat::objects::EnvCell = d.get(0x0007_0144).unwrap().unwrap();
    let fixture = cell
        .static_objects
        .iter()
        .find(|s| s.id == 0x0200_0365)
        .expect("the fixture stands in the cell");
    let setup: Setup = d.get(0x0200_0365).unwrap().unwrap();
    assert_eq!(setup.lights.len(), 1);
    let (key, authored) = &setup.lights[0];
    assert_eq!(*key, 0);
    assert_eq!(authored.cone_angle.to_bits(), 0xCDCD_CDCD);
    let local = &authored.view_space_location;
    assert_eq!(
        (local.origin.x, local.origin.y, local.origin.z),
        (0.000_759_337, 0.006_751_48, 0.0277)
    );

    let o = &fixture.frame.origin;
    let q = &fixture.frame.orientation;
    let root = Vec3::new(o.x, o.y, o.z);
    let rotation = Quat::from_xyzw(q.x, q.y, q.z, q.w);
    assert_eq!(root, Vec3::new(69.875, -69.916, 5.005));
    assert_eq!(rotation, Quat::from_xyzw(0.0, 0.0, -0.94372, 0.330_745));

    let lights = load_lights(
        &to_light_sources(&setup),
        &LightOwnerPlacement {
            owner_id: 0x4000_712F,
            position: root,
            rotation,
            is_dynamic: false,
            cell_id: 0x0007_0144,
            tracks_owner_pose: false,
        },
    );
    assert_eq!(lights.len(), 1);
    let light = lights[0];
    assert_eq!(light.ranking_origin, Some(root));
    assert_eq!(light.kind, LightKind::Point);
    assert_ne!(light.world_position, root);
    let local_offset = Vec3::new(local.origin.x, local.origin.y, local.origin.z);
    let expected = root + rotation * local_offset;
    assert!((light.world_position - expected).length() < 1e-5);
}
