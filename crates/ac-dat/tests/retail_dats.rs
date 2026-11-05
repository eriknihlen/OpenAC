//! Conformance against the retail dat files.
//!
//! Golden values read from the same dat files by an independent reader (a
//! throwaway console program). Floats are
//! compared by bit pattern. These tests need the dats: set `OPENAC_DAT_DIR`
//! or install them under `Documents\Asheron's Call`; run with
//! `cargo test -- --include-ignored` (the check scripts do this when the
//! directory exists).

use std::path::PathBuf;

use ac_dat::objects::{LandBlock, LandBlockInfo, Setup};
use ac_dat::{DatCollection, DatFile, DatFileType};

fn dat_dir() -> PathBuf {
    std::env::var_os("OPENAC_DAT_DIR")
        .map(PathBuf::from)
        .or_else(ac_dat::default_dat_dir)
        .expect("no dat directory: set OPENAC_DAT_DIR")
}

fn dats() -> DatCollection {
    DatCollection::open(dat_dir()).expect("open the retail dats")
}

struct HeaderGolden {
    block_size: i32,
    file_size: i32,
    file_type: DatFileType,
    subset: u32,
    first_free: i32,
    last_free: i32,
    free_count: i32,
    root: i32,
    master_map: i32,
    engine: i32,
    iteration: i32,
}

fn check_header(dat: &DatFile, g: &HeaderGolden) {
    let h = dat.header();
    assert_eq!(h.version, "");
    assert_eq!(h.block_size, g.block_size);
    assert_eq!(h.file_size, g.file_size);
    assert_eq!(h.file_type, g.file_type);
    assert_eq!(h.subset, g.subset);
    assert_eq!(h.first_free_block, g.first_free);
    assert_eq!(h.last_free_block, g.last_free);
    assert_eq!(h.free_block_count, g.free_count);
    assert_eq!(h.root_block, g.root);
    assert_eq!((h.new_lru, h.old_lru, h.use_lru), (0, 0, -842_150_656));
    assert_eq!(h.master_map_id, g.master_map);
    assert_eq!(h.engine_version, g.engine);
    assert_eq!(h.game_version, 0);
    assert_eq!(
        h.major_version_text(),
        "34a7d7d2-722f-4c46-8ab4-ef514f856ffd"
    );
    assert_eq!(h.minor_version, 6657);
    let it = dat.iteration();
    assert_eq!(it.current_iteration, g.iteration);
    assert_eq!(it.iterations, vec![(1, -g.iteration)]);
}

#[test]
#[ignore = "needs the retail dat files"]
fn headers_match_the_goldens() {
    let d = dats();
    check_header(
        &d.portal,
        &HeaderGolden {
            block_size: 1024,
            file_size: 926_941_184,
            file_type: DatFileType::Portal,
            subset: 0,
            first_free: 0x3738_6800,
            last_free: 0x3738_1C00,
            free_count: 2052,
            root: 0x0232_D400,
            master_map: 0x2500_0000,
            engine: 110,
            iteration: 2072,
        },
    );
    check_header(
        &d.cell,
        &HeaderGolden {
            block_size: 256,
            file_size: 348_127_232,
            file_type: DatFileType::Cell,
            subset: 1,
            first_free: 0x14B3_5A00,
            last_free: 0x14BF_FF00,
            free_count: 3238,
            root: 0x01B7_7400,
            master_map: 0,
            engine: 22,
            iteration: 982,
        },
    );
    check_header(
        &d.high_res,
        &HeaderGolden {
            block_size: 1024,
            file_size: 133_169_152,
            file_type: DatFileType::Portal,
            subset: 1_766_222_152,
            first_free: 0x07EA_C400,
            last_free: 0x07EA_BC00,
            free_count: 363,
            root: 0x0681_1800,
            master_map: 0,
            engine: 110,
            iteration: 497,
        },
    );
    check_header(
        &d.local,
        &HeaderGolden {
            block_size: 1024,
            file_size: 1_048_576,
            file_type: DatFileType::Local,
            subset: 1,
            first_free: 0x000F_DC00,
            last_free: 0x000B_DC00,
            free_count: 270,
            root: 0x0002_B000,
            master_map: 0,
            engine: 110,
            iteration: 994,
        },
    );
}

/// (entries, compressed, sum of ids, sum of sizes, min id, max id)
fn walk_summary(dat: &DatFile) -> (u64, u64, u64, u64, u32, u32) {
    let mut count = 0;
    let mut compressed = 0;
    let mut id_sum = 0u64;
    let mut size_sum = 0u64;
    let mut min = u32::MAX;
    let mut max = 0;
    let mut previous = None;
    for entry in dat.entries() {
        let e = entry.unwrap();
        if let Some(p) = previous {
            assert!(e.id > p, "walk out of order at {:#x}", e.id);
        }
        previous = Some(e.id);
        count += 1;
        compressed += u64::from(e.is_compressed());
        id_sum += u64::from(e.id);
        size_sum += u64::from(e.size);
        min = min.min(e.id);
        max = max.max(e.id);
    }
    (count, compressed, id_sum, size_sum, min, max)
}

#[test]
#[ignore = "needs the retail dat files"]
fn directory_walks_match_the_goldens() {
    let d = dats();
    assert_eq!(
        walk_summary(&d.portal),
        (
            79_694,
            0,
            12_954_366_271_963,
            852_004_981,
            0x0100_0001,
            0xFFFF_0001
        )
    );
    assert_eq!(
        walk_summary(&d.cell),
        (
            805_348,
            0,
            885_331_203_500_348,
            210_460_134,
            0x0000_FFFF,
            0xFFFF_0001
        )
    );
    assert_eq!(
        walk_summary(&d.high_res),
        (
            2_295,
            0,
            235_257_826_768,
            130_328_616,
            0x0600_3797,
            0xFFFF_0001
        )
    );
    assert_eq!(
        walk_summary(&d.local),
        (118, 0, 70_111_925_641, 700_113, 0x2100_0000, 0xFFFF_0001)
    );

    // First entries, field by field.
    let e = d.portal.entries().next().unwrap().unwrap();
    assert_eq!(
        (
            e.id,
            e.flags,
            e.version,
            e.offset,
            e.size,
            e.raw_date,
            e.iteration
        ),
        (0x0100_0001, 0, 3, 0x1DB5_9400, 5018, 1_123_748_804, 53)
    );
    let e = d.cell.entries().next().unwrap().unwrap();
    assert_eq!(
        (
            e.id,
            e.flags,
            e.version,
            e.offset,
            e.size,
            e.raw_date,
            e.iteration
        ),
        (0x0000_FFFF, 0, 2, 0x0000_0C00, 252, 1_117_233_940, 1)
    );
    assert_eq!(d.cell.find_entry(0x0001_0101).unwrap().unwrap().size, 248);
    assert_eq!(
        d.local.find_entry(0x2100_0001).unwrap().unwrap().iteration,
        875
    );
}

#[test]
#[ignore = "needs the retail dat files"]
fn holtburg_landblocks_match_the_goldens() {
    let d = dats();
    let lb: LandBlock = d.get(0xA9B4_FFFF).unwrap().unwrap();
    assert_eq!(lb.id, 0xA9B4_FFFF);
    assert!(lb.has_objects);
    let words: Vec<u16> = lb.terrain[..9].iter().map(|t| t.0).collect();
    assert_eq!(
        words,
        [
            0x0004, 0x7804, 0x000C, 0x000C, 0x780C, 0x000D, 0x780C, 0x0038, 0x0038
        ]
    );
    assert_eq!((lb.terrain[40].0, lb.terrain[80].0), (0x0038, 0x0024));
    assert_eq!(lb.height[..9], [45, 42, 39, 39, 35, 33, 29, 25, 20]);
    assert_eq!((lb.height[40], lb.height[80]), (40, 15));
    assert_eq!(lb.height.iter().map(|&h| u32::from(h)).sum::<u32>(), 2953);
    assert_eq!(
        lb.terrain.iter().map(|t| u32::from(t.0)).sum::<u32>(),
        1_175_405
    );

    let lb: LandBlock = d.get(0xA9B3_FFFF).unwrap().unwrap();
    assert!(lb.has_objects);
    let words: Vec<u16> = lb.terrain[..9].iter().map(|t| t.0).collect();
    assert_eq!(
        words,
        [
            0x600C, 0x600C, 0x6004, 0x7804, 0x7804, 0x7804, 0x7804, 0x0004, 0x0004
        ]
    );
    assert_eq!((lb.terrain[40].0, lb.terrain[80].0), (0x5004, 0x0004));
    assert_eq!(lb.height[..9], [43, 47, 47, 47, 47, 47, 47, 47, 45]);
    assert_eq!((lb.height[40], lb.height[80]), (53, 48));
    assert_eq!(lb.height.iter().map(|&h| u32::from(h)).sum::<u32>(), 4044);
    assert_eq!(
        lb.terrain.iter().map(|t| u32::from(t.0)).sum::<u32>(),
        1_653_184
    );
}

#[test]
#[ignore = "needs the retail dat files"]
fn holtburg_landblock_info_matches_the_goldens() {
    let d = dats();
    let info: LandBlockInfo = d.get(0xA9B4_FFFE).unwrap().unwrap();
    assert_eq!(info.id, 0xA9B4_FFFE);
    assert_eq!(info.num_cells, 123);
    assert_eq!(info.objects.len(), 114);
    assert_eq!(info.buildings.len(), 12);
    assert!(info.restriction_table.is_empty());

    let o = &info.objects[2];
    assert_eq!(o.id, 0x0200_0279);
    let bits = |v: f32| v.to_bits();
    assert_eq!(
        (
            bits(o.frame.origin.x),
            bits(o.frame.origin.y),
            bits(o.frame.origin.z)
        ),
        (0x42BA_BC5D, 0x430B_D127, 0x4287_0000)
    );
    assert_eq!(
        (bits(o.frame.orientation.w), bits(o.frame.orientation.z)),
        (0x3F35_04F7, 0xBF35_04F7)
    );
    assert_eq!(info.objects[6].id, 0x0200_00A5);
    assert_eq!(bits(info.objects[6].frame.orientation.x), 0x35B9_6CAA);

    let b = &info.buildings[0];
    assert_eq!(
        (b.model_id, b.num_leaves, b.portals.len()),
        (0x0100_0C1E, 64, 10)
    );
    assert_eq!(bits(b.frame.origin.x), 0x42A8_2F91);
    let b = &info.buildings[6];
    assert_eq!(
        (b.model_id, b.num_leaves, b.portals.len()),
        (0x0100_0A2B, 29, 5)
    );
    let p = &b.portals[0];
    assert_eq!(
        (p.flags, p.other_cell_id, p.other_portal_id),
        (1, 0x0145, 0)
    );
    assert_eq!(p.stab_list.len(), 18);
    assert_eq!((p.stab_list[0], p.stab_list[17]), (0x013F, 0x0150));
    let b = &info.buildings[11];
    assert_eq!((b.model_id, b.num_leaves), (0x0100_0F69, 43));
    let p = &b.portals[0];
    assert_eq!(
        (p.flags, p.other_cell_id, p.other_portal_id),
        (3, 0x0179, 0)
    );
    assert_eq!(p.stab_list, vec![0x0179, 0x017A]);
}

#[test]
#[ignore = "needs the retail dat files"]
fn human_male_setup_matches_the_goldens() {
    let d = dats();
    let s: Setup = d.get(0x0200_0001).unwrap().unwrap();
    assert_eq!(s.id, 0x0200_0001);
    assert_eq!(s.flags.0, 0x5);
    assert_eq!(s.parts.len(), 34);
    let mut parts = vec![
        0x0100_004E,
        0x0100_004F,
        0x0100_004D,
        0x0100_004C,
        0x0100_004B,
        0x0100_0053,
        0x0100_0051,
        0x0100_0050,
        0x0100_0052,
        0x0100_0054,
        0x0100_0055,
        0x0100_0056,
        0x0100_0058,
        0x0100_0057,
        0x0100_0059,
        0x0100_005B,
        0x0100_005A,
    ];
    parts.extend(std::iter::repeat_n(0x0100_01EC, 17));
    assert_eq!(s.parts, parts);
    const NONE: u32 = 0xFFFF_FFFF;
    assert_eq!(
        s.parent_index,
        vec![
            NONE, NONE, 1, 2, 3, NONE, 5, 6, 7, 0, 9, 0xA, 0xB, 0xC, 0xD, 0xE, 0xF, 0, 0x11, 0x12,
            0x13, 0x10, 0x10, 9, 9, 2, 6, 0xA, 0xD, 9, 0x1D, 0x1E, 0x1F, 0x20
        ]
    );
    assert!(s.default_scale.is_empty());
    let holding: Vec<(i32, i32)> = s
        .holding_locations
        .iter()
        .map(|(k, v)| (*k, v.part_id))
        .collect();
    assert_eq!(
        holding,
        [
            (0, 9),
            (8, 12),
            (4, 0),
            (9, 12),
            (5, 9),
            (1, 15),
            (6, 9),
            (2, 12),
            (3, 11)
        ]
    );
    assert!(s.connection_points.is_empty());
    assert_eq!(s.placement_frames.len(), 1);
    let (key, pf) = &s.placement_frames[0];
    assert_eq!(*key, 0);
    assert_eq!(pf.frames.len(), 34);
    assert!(pf.hooks.is_empty());
    let f0 = pf.frames[0];
    assert_eq!(f0.origin.z.to_bits(), 0x3F76_6666);
    assert_eq!(f0.orientation.w.to_bits(), 0xB33B_BD31);
    assert_eq!(f0.orientation.z.to_bits(), 0xBF80_0000);
    assert!(s.cyl_spheres.is_empty());
    assert_eq!(s.spheres.len(), 2);
    assert_eq!(s.spheres[0].origin.z.to_bits(), 0x3EF3_3333);
    assert_eq!(s.spheres[1].origin.z.to_bits(), 0x3FAC_CCCD);
    assert_eq!(s.spheres[1].radius.to_bits(), 0x3EF5_C28F);
    assert_eq!(s.height.to_bits(), 0x3FEA_E148);
    assert_eq!(s.radius.to_bits(), 0x3F2D_C750);
    assert_eq!(s.step_up_height.to_bits(), 0x3F19_999A);
    assert_eq!(s.step_down_height.to_bits(), 0x3FC0_0000);
    assert_eq!(s.sorting_sphere.origin.z.to_bits(), 0x3F69_999A);
    assert_eq!(s.sorting_sphere.radius.to_bits(), 0x3F6A_E148);
    assert_eq!(s.selection_sphere.origin.z.to_bits(), 0x3F6A_E148);
    assert!(s.lights.is_empty());
    assert_eq!(
        (
            s.default_animation,
            s.default_script,
            s.default_motion_table,
            s.default_sound_table,
            s.default_script_table
        ),
        (0, 0, 0, 0, 0)
    );
}

#[test]
#[ignore = "needs the retail dat files"]
fn static_setups_match_the_goldens() {
    let d = dats();
    // A Holtburg static object (placed by LandBlockInfo 0xA9B4FFFE).
    let s: Setup = d.get(0x0200_0279).unwrap().unwrap();
    assert_eq!(s.flags.0, 0x4);
    assert_eq!(s.parts, vec![0x0100_0C22]);
    assert!(s.parent_index.is_empty());
    assert_eq!(s.placement_frames.len(), 1);
    assert_eq!(s.placement_frames[0].1.frames[0].orientation.w, 1.0);
    assert_eq!(s.cyl_spheres.len(), 1);
    let c = s.cyl_spheres[0];
    assert_eq!(
        (c.origin.z.to_bits(), c.radius.to_bits(), c.height.to_bits()),
        (0xBFC0_0000, 0x3E99_999A, 0x403F_7CEE)
    );
    assert!(s.spheres.is_empty());
    assert_eq!(s.height.to_bits(), 0x403F_7CEE);
    assert_eq!(s.radius.to_bits(), 0x3ED9_3924);
    assert_eq!(s.step_up_height.to_bits(), 0x3F9A_786D);
    assert_eq!(s.step_down_height.to_bits(), 0x3F9A_786D);
    assert_eq!(s.sorting_sphere.origin.z.to_bits(), 0xBB83_1203);
    assert_eq!(s.selection_sphere.radius.to_bits(), 0x3FBF_7CEE);

    // The first setup with lights and per-part scale.
    let s: Setup = d.get(0x0200_0EDF).unwrap().unwrap();
    assert_eq!(s.flags.0, 0x3);
    assert_eq!(s.parts, vec![0x0100_2D88]);
    assert_eq!(s.parent_index, vec![0xFFFF_FFFF]);
    assert_eq!(s.default_scale.len(), 1);
    assert_eq!(s.default_scale[0].x.to_bits(), 0x3E80_0000);
    let keys: Vec<u32> = s.placement_frames.iter().map(|(k, _)| *k).collect();
    assert_eq!(keys, [0, 121, 103, 101, 3, 1]);
    assert_eq!(s.spheres[0].origin.y.to_bits(), 0xB2FF_FFF3);
    assert_eq!(s.lights.len(), 1);
    let (key, light) = &s.lights[0];
    assert_eq!(*key, 0);
    assert_eq!(light.view_space_location.origin.z.to_bits(), 0x3E8F_5968);
    let c = light.color;
    assert_eq!((c.alpha, c.red, c.green, c.blue), (0xFF, 0x80, 0xFF, 0xFF));
    assert_eq!(light.intensity.to_bits(), 0x42C8_0000);
    assert_eq!(light.falloff.to_bits(), 0x4080_0000);
    assert_eq!(light.cone_angle.to_bits(), 0xCDCD_CDCD);
}

#[test]
#[ignore = "needs the retail dat files"]
fn every_setup_decodes_to_the_golden_totals() {
    let d = dats();
    let (mut n, mut parts, mut placements, mut hooks, mut lights) = (0, 0, 0, 0, 0);
    for entry in d.portal.entries() {
        let id = entry.unwrap().id;
        if !(0x0200_0000..=0x0200_FFFF).contains(&id) {
            continue;
        }
        let s: Setup = d.portal.get(id).unwrap().unwrap();
        n += 1;
        parts += s.parts.len();
        placements += s.placement_frames.len();
        hooks += s
            .placement_frames
            .iter()
            .map(|(_, f)| f.hooks.len())
            .sum::<usize>();
        lights += s.lights.len();
    }
    assert_eq!(
        (n, parts, placements, hooks, lights),
        (5935, 22_960, 19_466, 0, 608)
    );
}

#[test]
#[ignore = "needs the retail dat files"]
fn every_landblock_and_info_decodes_to_the_golden_totals() {
    let d = dats();
    let (mut n, mut with_objects, mut height_sum, mut terrain_sum) = (0u64, 0u64, 0u64, 0u64);
    let (mut infos, mut cells, mut objects, mut buildings, mut portals, mut restrictions) =
        (0u64, 0u64, 0u64, 0u64, 0u64, 0u64);
    for entry in d.cell.entries() {
        let id = entry.unwrap().id;
        match id & 0xFFFF {
            0xFFFF => {
                let lb: LandBlock = d.cell.get(id).unwrap().unwrap();
                n += 1;
                with_objects += u64::from(lb.has_objects);
                height_sum += lb.height.iter().map(|&h| u64::from(h)).sum::<u64>();
                terrain_sum += lb.terrain.iter().map(|t| u64::from(t.0)).sum::<u64>();
            }
            0xFFFE => {
                let li: LandBlockInfo = d.cell.get(id).unwrap().unwrap();
                infos += 1;
                cells += u64::from(li.num_cells);
                objects += li.objects.len() as u64;
                buildings += li.buildings.len() as u64;
                portals += li
                    .buildings
                    .iter()
                    .map(|b| b.portals.len() as u64)
                    .sum::<u64>();
                restrictions += li.restriction_table.len() as u64;
            }
            _ => {}
        }
    }
    assert_eq!(
        (n, with_objects, height_sum, terrain_sum),
        (65_025, 5346, 121_748_928, 31_651_457_476)
    );
    assert_eq!(
        (infos, cells, objects, buildings, portals, restrictions),
        (5346, 729_888, 42_942, 6979, 16_937, 7140)
    );
}

#[test]
#[ignore = "needs the retail dat files"]
fn many_threads_read_one_dat() {
    let d = dats();
    let ids = [0xA9B4_FFFFu32, 0xA9B3_FFFF, 0xA9B4_FFFE, 0x0000_FFFF];
    let expected: Vec<Vec<u8>> = ids
        .iter()
        .map(|&id| d.cell.get_bytes(id).unwrap().unwrap())
        .collect();
    std::thread::scope(|scope| {
        for t in 0..8 {
            let (d, ids, expected) = (&d, &ids, &expected);
            scope.spawn(move || {
                for round in 0..200 {
                    let i = (t + round) % ids.len();
                    assert_eq!(d.cell.get_bytes(ids[i]).unwrap().unwrap(), expected[i]);
                }
            });
        }
    });
}

/// Directory lookups through the node cache, from several threads at once,
/// find exactly what an uncached walk of the directory lists, and the
/// cache never holds more nodes than its limit.
#[test]
#[ignore = "needs the retail dat files"]
fn cached_lookups_from_many_threads_match_the_directory_walk() {
    let path = dat_dir().join(ac_dat::CELL_DAT);
    let walk = DatFile::open_with_node_cache(&path, 0).unwrap();
    // Every 97th entry of the cell dat: spread over the whole tree.
    let sample: Vec<ac_dat::DatEntry> = walk.entries().map(|e| e.unwrap()).step_by(97).collect();
    let cached = DatFile::open_with_node_cache(&path, 64).unwrap();
    std::thread::scope(|scope| {
        for t in 0..8 {
            let (cached, sample) = (&cached, &sample);
            scope.spawn(move || {
                for entry in sample.iter().skip(t).step_by(3) {
                    assert_eq!(cached.find_entry(entry.id).unwrap(), Some(*entry));
                    assert!(cached.cached_nodes() <= 64);
                }
            });
        }
    });
    let stats = cached.node_cache_stats();
    assert!(stats.hits > 0 && stats.evictions > 0, "{stats:?}");
}

/// A file's bytes gathered one block at a time, following the next
/// pointers, with one read per block: the plain reading of the format.
fn read_block_by_block(
    file: &std::fs::File,
    block_size: usize,
    entry: &ac_dat::DatEntry,
) -> Vec<u8> {
    use std::io::{Read, Seek, SeekFrom};
    let mut file = file;
    let mut out = Vec::with_capacity(entry.size as usize);
    let mut block = entry.offset;
    let mut buf = vec![0u8; block_size];
    while out.len() < entry.size as usize {
        assert_ne!(block, 0, "chain of {:08X} ends early", entry.id);
        let take = (block_size - 4).min(entry.size as usize - out.len());
        file.seek(SeekFrom::Start(block as u64)).unwrap();
        file.read_exact(&mut buf[..4 + take]).unwrap();
        out.extend_from_slice(&buf[4..4 + take]);
        block = i32::from_le_bytes([buf[0], buf[1], buf[2], buf[3]]);
    }
    out
}

/// Files are read as runs of blocks; every file whose chain leaves its run
/// (and a spread of the others) reads the same bytes as the plain
/// block-by-block walk, in all three dats.
#[test]
#[ignore = "needs the retail dat files"]
fn run_reads_match_block_by_block_reads() {
    for name in [ac_dat::PORTAL_DAT, ac_dat::CELL_DAT, ac_dat::HIGHRES_DAT] {
        let path = dat_dir().join(name);
        let dat = DatFile::open(&path).unwrap();
        let file = std::fs::File::open(&path).unwrap();
        let block_size = dat.header().block_size as usize;
        let (mut checked, mut multi_block) = (0, 0);
        for (i, entry) in dat.entries().map(|e| e.unwrap()).enumerate() {
            // Only files spanning several blocks can leave their run.
            let spans_blocks = entry.size as usize > block_size - 4;
            if i % 53 != 0 && !spans_blocks {
                continue;
            }
            let expected = read_block_by_block(&file, block_size, &entry);
            let got = dat.read_entry(&entry).unwrap();
            assert!(got == expected, "{name} {:08X}", entry.id);
            checked += 1;
            if spans_blocks {
                multi_block += 1;
            }
        }
        assert!(
            checked > 0 && multi_block > 0,
            "{name}: {checked} {multi_block}"
        );
    }
}
