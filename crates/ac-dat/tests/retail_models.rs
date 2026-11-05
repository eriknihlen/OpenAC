//! Conformance of models, surfaces, palettes and clothing against the
//! retail dat files.
//!
//! Golden values read from the same dat files by an independent reader (a
//! throwaway console program). Floats are
//! compared by bit pattern. These tests need the dats: set `OPENAC_DAT_DIR`
//! or install them under `Documents\Asheron's Call`; run with
//! `cargo test -- --include-ignored` (the check scripts do this when the
//! directory exists).

use std::collections::BTreeMap;
use std::ops::RangeInclusive;
use std::path::PathBuf;

use ac_bits::Reader;
use ac_dat::objects::{
    BspNode, BspTree, ClothingTable, DatObject, GfxObj, GfxObjDegradeInfo, Palette, PaletteSet,
    RenderSurface, Surface, SurfaceBase, SurfaceTexture,
};
use ac_dat::types::Vector3;
use ac_dat::{DatCollection, DatFile};

fn dats() -> DatCollection {
    let dir = std::env::var_os("OPENAC_DAT_DIR")
        .map(PathBuf::from)
        .or_else(ac_dat::default_dat_dir)
        .expect("no dat directory: set OPENAC_DAT_DIR");
    DatCollection::open(dir).expect("open the retail dats")
}

fn bits(v: Vector3) -> (u32, u32, u32) {
    (v.x.to_bits(), v.y.to_bits(), v.z.to_bits())
}

/// Decode every object of a type in `range`, checking that each one
/// consumes its file exactly.
fn sweep<T: DatObject>(dat: &DatFile, range: RangeInclusive<u32>, mut f: impl FnMut(T)) -> u64 {
    let mut n = 0;
    for entry in dat.entries() {
        let id = entry.unwrap().id;
        if !range.contains(&id) {
            continue;
        }
        let bytes = dat.get_bytes(id).unwrap().unwrap();
        let mut r = Reader::new(&bytes);
        let mut object = T::unpack(&mut r).unwrap_or_else(|e| panic!("{id:#010x}: {e}"));
        assert_eq!(r.remaining(), 0, "{id:#010x} left bytes unread");
        object.set_id(id);
        f(object);
        n += 1;
    }
    n
}

/// Node counts of a tree keyed by node type name, plus
/// `nodes`, `polyrefs`, `portalrefs` and `solid`.
fn bsp_stats(tree: &BspTree, out: &mut BTreeMap<String, u64>) {
    let mut add = |key: &str, n: u64| *out.entry(key.to_owned()).or_default() += n;
    for node in &tree.nodes {
        add("nodes", 1);
        match node {
            BspNode::Split(s) => {
                add(std::str::from_utf8(&s.tag.text()).unwrap(), 1);
                add("polyrefs", s.polygons.len() as u64);
            }
            BspNode::Portal(p) => {
                add("Portal", 1);
                add("polyrefs", p.polygons.len() as u64);
                add("portalrefs", p.portals.len() as u64);
            }
            BspNode::Leaf(l) => {
                add("Leaf", 1);
                add("polyrefs", l.polygons.len() as u64);
                add("solid", u64::try_from(l.solid).unwrap());
            }
        }
    }
}

fn stats(tree: Option<&BspTree>) -> Vec<(String, u64)> {
    let mut out = BTreeMap::new();
    bsp_stats(tree.unwrap(), &mut out);
    out.into_iter().filter(|(_, n)| *n != 0).collect()
}

fn s(pairs: &[(&str, u64)]) -> Vec<(String, u64)> {
    let mut v: Vec<(String, u64)> = pairs.iter().map(|(k, n)| ((*k).to_owned(), *n)).collect();
    v.sort();
    v
}

#[test]
#[ignore = "needs the retail dat files"]
fn gfx_objs_match_the_goldens() {
    let d = dats();

    // Drawing only, with a degrade table.
    let g: GfxObj = d.get(0x0100_0001).unwrap().unwrap();
    assert_eq!(g.flags.0, 0xA);
    assert_eq!(g.surfaces, vec![0x0800_120B]);
    assert_eq!(g.vertices.vertex_type, 1);
    assert_eq!(g.vertices.vertices.len(), 38);
    assert_eq!(bits(g.sort_center), (0xBC18_265D, 0xBC14_D21F, 0xBD3F_410E));
    assert_eq!(g.did_degrade, 0x1100_01FA);
    let (k, v) = &g.vertices.vertices[0];
    assert_eq!(*k, 0);
    assert_eq!(bits(v.origin), (0xBDB2_49A1, 0xBD6C_9C5B, 0x3E2B_4A23));
    assert_eq!(bits(v.normal), (0xBEC3_EAB3, 0xBF34_8C1E, 0x3F18_C89B));
    assert_eq!(v.uvs.len(), 1);
    assert_eq!(
        (v.uvs[0].u.to_bits(), v.uvs[0].v.to_bits()),
        (0x3F3A_ADD6, 0)
    );
    let (k, v) = &g.vertices.vertices[37];
    assert_eq!(*k, 37);
    assert_eq!(bits(v.origin), (0, 0x3E1F_65DC, 0x3DCD_013B));
    assert_eq!(v.uvs[0].v.to_bits(), 0x3DC3_CF70);
    assert_eq!((g.polygons.len(), g.physics_polygons.len()), (67, 0));
    let (k, p) = &g.polygons[0];
    assert_eq!(*k, 0);
    assert_eq!(
        (p.stippling.0, p.sides_type.0, p.pos_surface, p.neg_surface),
        (0, 0, 0, -1)
    );
    assert_eq!(p.vertex_ids, vec![0, 1, 2]);
    assert_eq!(p.pos_uv_indices, vec![0, 0, 0]);
    assert!(p.neg_uv_indices.is_empty());
    let (k, p) = &g.polygons[66];
    assert_eq!(*k, 66);
    assert_eq!(p.vertex_ids, vec![19, 22, 32]);
    assert!(g.physics_bsp.is_none());
    assert_eq!(
        stats(g.drawing_bsp.as_ref()),
        s(&[
            ("BpIn", 15),
            ("BPIN", 9),
            ("BPnN", 16),
            ("BPOL", 11),
            ("nodes", 51),
            ("polyrefs", 67)
        ])
    );
    let Some(BspNode::Split(root)) = g.drawing_bsp.as_ref().and_then(BspTree::root) else {
        panic!("drawing root is not a split")
    };
    assert_eq!(root.tag.0, 0x4250_6E4E);
    assert!(root.polygons.is_empty());
    assert_eq!(
        (root.sphere.origin.z.to_bits(), root.sphere.radius.to_bits()),
        (0xBD3F_410E, 0x3E8D_6CB5)
    );

    // Physics and drawing, with a degrade table.
    let g: GfxObj = d.get(0x0100_0439).unwrap().unwrap();
    assert_eq!(g.flags.0, 0xB);
    assert_eq!(g.surfaces, vec![0x0800_04A5, 0x0800_04A6]);
    assert_eq!(g.vertices.vertices.len(), 9);
    assert_eq!(bits(g.sort_center), (0xBEBC_1D4B, 0xBDBF_A8B5, 0x3FB0_81C3));
    assert_eq!(g.did_degrade, 0x1100_02ED);
    let v = &g.vertices.vertices[0].1;
    assert_eq!(bits(v.origin), (0xBF31_51F8, 0, 0x3F00_0000));
    let uvs: Vec<(u32, u32)> = v
        .uvs
        .iter()
        .map(|uv| (uv.u.to_bits(), uv.v.to_bits()))
        .collect();
    assert_eq!(uvs, [(0x3D3F_60D3, 0), (0x3CC2_CAB6, 0x3F80_0000)]);
    assert_eq!(
        bits(g.vertices.vertices[8].1.normal),
        (0xBF7D_F9CF, 0xBC50_6218, 0x3DFF_B481)
    );
    assert_eq!((g.polygons.len(), g.physics_polygons.len()), (12, 8));
    let p = &g.polygons[11].1;
    assert_eq!((p.stippling.0, p.pos_surface), (1, 1));
    assert_eq!(p.vertex_ids, vec![0, 8, 5]);
    assert_eq!(p.pos_uv_indices, vec![1, 0, 3]);
    let (k, p) = &g.physics_polygons[7];
    assert_eq!(*k, 7);
    assert_eq!((p.stippling.0, p.pos_surface, p.neg_surface), (4, -1, -1));
    assert_eq!(p.vertex_ids, vec![6, 7, 8]);
    assert!(p.pos_uv_indices.is_empty());
    assert_eq!(
        stats(g.physics_bsp.as_ref()),
        s(&[
            ("BPnN", 10),
            ("Leaf", 11),
            ("nodes", 21),
            ("polyrefs", 8),
            ("solid", 3)
        ])
    );
    let Some(BspNode::Split(root)) = g.physics_bsp.as_ref().and_then(BspTree::root) else {
        panic!("physics root is not a split")
    };
    assert_eq!(
        bits(root.plane.normal),
        (0xBF7D_2104, 0x0000_0000, 0xBE18_EDA2)
    );
    assert_eq!(root.plane.distance.to_bits(), 0x3C20_F58E);
    assert_eq!(
        (root.sphere.origin.z.to_bits(), root.sphere.radius.to_bits()),
        (0x3FAA_B1C4, 0x3FC5_081C)
    );
    assert_eq!(
        stats(g.drawing_bsp.as_ref()),
        s(&[("BpIn", 3), ("BPnN", 2), ("nodes", 5), ("polyrefs", 12)])
    );

    // Physics and drawing, no degrade table; a one-node drawing tree.
    let g: GfxObj = d.get(0x0100_00D6).unwrap().unwrap();
    assert_eq!(g.flags.0, 0x3);
    assert_eq!(g.did_degrade, 0);
    assert_eq!(bits(g.sort_center), (0xBE4C_CCCD, 0xC086_6666, 0x3F19_999A));
    assert_eq!(
        bits(g.vertices.vertices[1].1.normal),
        (0xBF35_04F7, 0x3ED9_3922, 0x3F10_D0BB)
    );
    assert_eq!(g.polygons[2].1.vertex_ids, vec![5, 2, 1, 3]);
    assert_eq!(g.polygons[2].1.pos_uv_indices, vec![1, 1, 1, 1]);
    assert_eq!(
        stats(g.physics_bsp.as_ref()),
        s(&[
            ("BPnN", 6),
            ("Leaf", 7),
            ("nodes", 13),
            ("polyrefs", 7),
            ("solid", 2)
        ])
    );
    let Some(BspNode::Split(root)) = g.drawing_bsp.as_ref().and_then(BspTree::root) else {
        panic!("drawing root is not a split")
    };
    assert_eq!(root.tag.0, 0x4270_496E);
    assert_eq!(root.polygons, vec![0, 1, 2]);
    assert_eq!(
        (root.sphere.origin.z.to_bits(), root.sphere.radius.to_bits()),
        (0x3FDC_FB01, 0x40CD_5365)
    );

    // A Holtburg static (setup 0x02000279's only part).
    let g: GfxObj = d.get(0x0100_0C22).unwrap().unwrap();
    assert_eq!((g.flags.0, g.did_degrade), (0xA, 0x1100_04EC));
    assert_eq!(g.surfaces, vec![0x0800_032A]);
    assert_eq!(g.vertices.vertices.len(), 8);
    assert_eq!(g.polygons[3].1.vertex_ids, vec![6, 7, 4, 5]);
    assert_eq!(g.polygons[3].1.pos_uv_indices, vec![0, 0, 1, 1]);
    assert_eq!(
        stats(g.drawing_bsp.as_ref()),
        s(&[("BpIn", 1), ("nodes", 1), ("polyrefs", 4)])
    );

    // A Holtburg building: portal nodes in the drawing tree.
    let g: GfxObj = d.get(0x0100_0C1E).unwrap().unwrap();
    assert_eq!((g.flags.0, g.did_degrade), (0xB, 0x1100_04EE));
    assert_eq!(g.surfaces.len(), 14);
    assert_eq!(g.surfaces[13], 0x0800_07EB);
    assert_eq!(g.vertices.vertices.len(), 103);
    assert_eq!((g.polygons.len(), g.physics_polygons.len()), (102, 96));
    assert_eq!(
        (
            g.polygons[0].1.stippling.0,
            g.polygons[0].1.vertex_ids.len()
        ),
        (4, 4)
    );
    assert_eq!(g.physics_polygons[95].1.vertex_ids, vec![33, 34, 31, 28]);
    assert_eq!(
        stats(g.physics_bsp.as_ref()),
        s(&[
            ("BPnN", 63),
            ("Leaf", 64),
            ("nodes", 127),
            ("polyrefs", 96),
            ("solid", 26)
        ])
    );
    assert_eq!(
        stats(g.drawing_bsp.as_ref()),
        s(&[
            ("BPIN", 43),
            ("BPnN", 2),
            ("Leaf", 53),
            ("nodes", 105),
            ("polyrefs", 92),
            ("Portal", 7),
            ("portalrefs", 10)
        ])
    );
    let Some(BspNode::Split(root)) = g.drawing_bsp.as_ref().and_then(BspTree::root) else {
        panic!("drawing root is not a split")
    };
    assert_eq!(&root.tag.text(), b"BPIN");
    assert_eq!(root.polygons, vec![46, 49, 51, 52]);
}

#[test]
#[ignore = "needs the retail dat files"]
fn every_gfx_obj_decodes_to_the_golden_totals() {
    let d = dats();
    let mut flags: BTreeMap<u32, u64> = BTreeMap::new();
    let (mut surfaces, mut surface_ids, mut vertices, mut uvs) = (0u64, 0u64, 0u64, 0u64);
    let (mut polys, mut phys_polys, mut poly_verts, mut pos_uv, mut neg_uv, mut degrade) =
        (0u64, 0u64, 0u64, 0u64, 0u64, 0u64);
    let (mut phys, mut draw) = (BTreeMap::new(), BTreeMap::new());
    let n = sweep(&d.portal, 0x0100_0000..=0x0100_FFFF, |g: GfxObj| {
        *flags.entry(g.flags.0).or_default() += 1;
        surfaces += g.surfaces.len() as u64;
        surface_ids += g.surfaces.iter().map(|&s| u64::from(s)).sum::<u64>();
        assert_eq!(g.vertices.vertex_type, 1);
        vertices += g.vertices.vertices.len() as u64;
        uvs += g
            .vertices
            .vertices
            .iter()
            .map(|(_, v)| v.uvs.len() as u64)
            .sum::<u64>();
        for (_, p) in g.polygons.iter().chain(&g.physics_polygons) {
            poly_verts += p.vertex_ids.len() as u64;
            pos_uv += p.pos_uv_indices.len() as u64;
            neg_uv += p.neg_uv_indices.len() as u64;
        }
        polys += g.polygons.len() as u64;
        phys_polys += g.physics_polygons.len() as u64;
        if let Some(t) = &g.physics_bsp {
            bsp_stats(t, &mut phys);
        }
        if let Some(t) = &g.drawing_bsp {
            bsp_stats(t, &mut draw);
        }
        degrade += u64::from(g.did_degrade != 0);
    });
    assert_eq!(n, 15_318);
    assert_eq!(
        flags.into_iter().collect::<Vec<_>>(),
        [(0x2, 10_546), (0x3, 641), (0xA, 3514), (0xB, 617)]
    );
    assert_eq!(
        (surfaces, surface_ids, vertices, uvs),
        (33_655, 4_517_189_281_772, 615_119, 1_077_669)
    );
    assert_eq!(
        (polys, phys_polys, poly_verts, pos_uv, neg_uv, degrade),
        (869_312, 115_159, 3_130_393, 2_734_851, 36, 4131)
    );
    let phys: Vec<(String, u64)> = phys.into_iter().filter(|(_, n)| *n != 0).collect();
    assert_eq!(
        phys,
        s(&[
            ("BPnN", 124_690),
            ("Leaf", 125_948),
            ("nodes", 250_638),
            ("polyrefs", 115_158),
            ("solid", 60_380)
        ])
    );
    let draw: Vec<(String, u64)> = draw.into_iter().filter(|(_, n)| *n != 0).collect();
    assert_eq!(
        draw,
        s(&[
            ("BpIn", 106_788),
            ("BpIN", 59_608),
            ("BPIn", 22_129),
            ("BPIN", 151_134),
            ("BPnN", 131_281),
            ("BPOL", 106_620),
            ("Leaf", 84_948),
            ("nodes", 663_131),
            ("polyrefs", 846_311),
            ("Portal", 623),
            ("portalrefs", 910)
        ])
    );
}

#[test]
#[ignore = "needs the retail dat files"]
fn every_surface_and_surface_texture_decodes_to_the_golden_totals() {
    let d = dats();
    let (mut type_sum, mut tex_sum, mut pal_sum, mut color_sum) = (0u64, 0u64, 0u64, 0u64);
    let (mut tr, mut lu, mut di) = (0u64, 0u64, 0u64);
    let n = sweep(&d.portal, 0x0800_0000..=0x0800_FFFF, |s: Surface| {
        type_sum += u64::from(s.surface_type.0);
        match s.base {
            SurfaceBase::Texture { texture, palette } => {
                tex_sum += u64::from(texture);
                pal_sum += u64::from(palette);
            }
            SurfaceBase::Solid(c) => {
                color_sum += u64::from(u32::from_le_bytes([c.blue, c.green, c.red, c.alpha]));
            }
        }
        tr += u64::from(s.translucency.to_bits());
        lu += u64::from(s.luminosity.to_bits());
        di += u64::from(s.diffuse.to_bits());
    });
    assert_eq!(n, 6152);
    assert_eq!(
        (type_sum, tex_sum, pal_sum, color_sum),
        (13_386_345, 503_286_005_590, 0, 655_685_763_174)
    );
    assert_eq!(
        (tr, lu, di),
        (275_483_055_869, 807_011_010_504, 6_549_966_507_245)
    );

    let (mut type_sum, mut count, mut tex_sum, mut cat_sum) = (0u64, 0u64, 0u64, 0u64);
    let n = sweep(&d.portal, 0x0500_0000..=0x05FF_FFFF, |t: SurfaceTexture| {
        type_sum += u64::from(t.texture_type.0);
        count += t.textures.len() as u64;
        tex_sum += t.textures.iter().map(|&x| u64::from(x)).sum::<u64>();
        cat_sum += u64::from(t.data_category);
    });
    assert_eq!(
        (n, type_sum, count, tex_sum, cat_sum),
        (7221, 14_442, 10_245, 1_031_505_938_734, 0)
    );
}

#[test]
#[ignore = "needs the retail dat files"]
fn every_render_surface_decodes_to_the_golden_totals() {
    let d = dats();
    // (count, width, height, format, data length, palette, category, byte sum)
    let summary = |dat: &DatFile| {
        let mut t = [0u64; 8];
        t[0] = sweep(dat, 0x0600_0000..=0x07FF_FFFF, |s: RenderSurface| {
            t[1] += u64::from(s.width);
            t[2] += u64::from(s.height);
            t[3] += u64::from(s.format.0);
            t[4] += s.data.len() as u64;
            t[5] += u64::from(s.default_palette_id);
            t[6] += u64::from(s.data_category);
            t[7] += s.data.iter().map(|&b| u64::from(b)).sum::<u64>();
        });
        t
    };
    assert_eq!(
        summary(&d.portal),
        [
            20_684,
            1_699_194,
            1_710_713,
            1_749_157_732_944,
            605_978_157,
            281_062_801_779,
            123_637,
            52_329_243_894
        ]
    );
    assert_eq!(
        summary(&d.high_res),
        [
            2294,
            469_120,
            516_736,
            816_919_499_070,
            130_268_416,
            86_105_499_606,
            15_918,
            8_766_013_741
        ]
    );
}

#[test]
#[ignore = "needs the retail dat files"]
fn every_palette_and_palette_set_decodes_to_the_golden_totals() {
    let d = dats();
    let (mut colors, mut sum) = (0u64, 0u64);
    let n = sweep(&d.portal, 0x0400_0000..=0x0400_FFFF, |p: Palette| {
        colors += p.colors.len() as u64;
        sum += p
            .colors
            .iter()
            .map(|c| u64::from(u32::from_le_bytes([c.blue, c.green, c.red, c.alpha])))
            .sum::<u64>();
    });
    assert_eq!((n, colors, sum), (4521, 9_259_008, 39_652_977_767_096_732));

    let (mut count, mut sum) = (0u64, 0u64);
    let n = sweep(&d.portal, 0x0F00_0000..=0x0F00_FFFF, |p: PaletteSet| {
        count += p.palettes.len() as u64;
        sum += p.palettes.iter().map(|&x| u64::from(x)).sum::<u64>();
    });
    assert_eq!((n, count, sum), (2681, 5038, 338_124_116_919));
}

#[test]
#[ignore = "needs the retail dat files"]
fn every_clothing_table_decodes_to_the_golden_totals() {
    let d = dats();
    // base, obj, tex, sub, subPal, range, keys, models, texs, icons, ranges, sets, buckets
    let mut t = [0u64; 13];
    let n = sweep(&d.portal, 0x1000_0000..=0x1000_FFFF, |c: ClothingTable| {
        t[12] += u64::from(c.base_effects_buckets) + u64::from(c.sub_pal_effects_buckets);
        for (key, effect) in &c.base_effects {
            t[0] += 1;
            t[6] += u64::from(*key);
            for o in &effect.object_effects {
                t[1] += 1;
                t[7] += u64::from(o.model_id) + u64::from(o.index);
                for e in &o.texture_effects {
                    t[2] += 1;
                    t[8] += u64::from(e.old_texture) + u64::from(e.new_texture);
                }
            }
        }
        for (key, scheme) in &c.sub_pal_effects {
            t[3] += 1;
            t[6] += u64::from(*key);
            t[9] += u64::from(scheme.icon);
            for sp in &scheme.sub_palettes {
                t[4] += 1;
                t[11] += u64::from(sp.palette_set);
                for r in &sp.ranges {
                    t[5] += 1;
                    t[10] += u64::from(r.offset) * 3 + u64::from(r.num_colors);
                }
            }
        }
    });
    assert_eq!(n, 1917);
    assert_eq!(
        t,
        [
            15_803,
            46_221,
            40_823,
            18_742,
            25_689,
            31_510,
            530_341_845_481,
            775_928_287_477,
            6_849_456_218_977,
            1_251_706_742_759,
            98_242_984,
            6_464_871_470_424,
            76_680
        ]
    );
}

#[test]
#[ignore = "needs the retail dat files"]
fn every_degrade_table_decodes_to_the_golden_totals() {
    let d = dats();
    let (mut count, mut ids, mut modes, mut floats) = (0u64, 0u64, 0u64, 0u64);
    let n = sweep(
        &d.portal,
        0x1100_0000..=0x1100_FFFF,
        |g: GfxObjDegradeInfo| {
            for x in &g.degrades {
                count += 1;
                ids += u64::from(x.id);
                modes += u64::from(x.degrade_mode);
                // The golden program summed the three bit patterns as u32
                // (wrapping) before widening.
                floats += u64::from(
                    x.min_dist
                        .to_bits()
                        .wrapping_add(x.ideal_dist.to_bits())
                        .wrapping_add(x.max_dist.to_bits()),
                );
            }
        },
    );
    assert_eq!(
        (n, count, ids, modes, floats),
        (4131, 14_344, 171_435_610_537, 16_928, 40_875_820_483_274)
    );
}
