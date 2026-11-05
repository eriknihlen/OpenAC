//! Conformance of the animation, motion, cell, region, sound, effect and
//! table objects against the retail dat files.
//!
//! Golden values read from the same dat files by an independent reader (a
//! throwaway console program). Floats are
//! compared by bit pattern; "float sums" add each float's bit pattern as a
//! u64. Every sweep also proves each object decodes to the last byte. These
//! tests need the dats: set `OPENAC_DAT_DIR` or install them under
//! `Documents\Asheron's Call`; run with `cargo test -- --include-ignored`.

use std::collections::BTreeMap;
use std::path::PathBuf;

use ac_bits::Reader;
use ac_dat::objects::{
    Animation, AnimationHook, AnimationPartChange, BspNode, BspTag, BspTree, CHAR_GEN_ID, CharGen,
    CombatManeuver, CombatTable, DatObject, DualEnumIdMap, EXPERIENCE_TABLE_ID, EnumIdMap, EnvCell,
    EnvCellFlags, Environment, ExperienceTable, GearCg, HookData, MotionData, MotionDataFlags,
    MotionTable, ObjDesc, ParticleEmitter, PhysicsScript, PhysicsScriptTable, Region,
    SKILL_TABLE_ID, SPELL_COMPONENT_TABLE_ID, SPELL_TABLE_ID, Scene, SkillCg, SkillTable,
    SoundTable, SpellComponentTable, SpellTable, StringTable, StringTableString, Wave,
};
use ac_dat::types::ColorArgb;
use ac_dat::{DatCollection, DatFile};

fn dat_dir() -> PathBuf {
    std::env::var_os("OPENAC_DAT_DIR")
        .map(PathBuf::from)
        .or_else(ac_dat::default_dat_dir)
        .expect("no dat directory: set OPENAC_DAT_DIR")
}

fn dats() -> DatCollection {
    DatCollection::open(dat_dir()).expect("open the retail dats")
}

fn bits(v: f32) -> u64 {
    u64::from(v.to_bits())
}

fn dbits(v: f64) -> u64 {
    v.to_bits()
}

/// Decode every file whose id is in `lo..=hi`, checking that each one
/// decodes to its last byte.
fn sweep<T: DatObject>(dat: &DatFile, lo: u32, hi: u32, each: impl FnMut(u32, T)) -> usize {
    sweep_where(dat, |id| (lo..=hi).contains(&id), each)
}

/// Decode every file whose id passes `wanted`, checking that each one
/// decodes to its last byte.
fn sweep_where<T: DatObject>(
    dat: &DatFile,
    wanted: impl Fn(u32) -> bool,
    mut each: impl FnMut(u32, T),
) -> usize {
    let mut n = 0;
    for entry in dat.entries() {
        let entry = entry.unwrap();
        let id = entry.id;
        if !wanted(id) {
            continue;
        }
        let bytes = dat.read_entry(&entry).unwrap();
        let r = &mut Reader::new(&bytes);
        let object = T::unpack(r).unwrap_or_else(|e| panic!("{id:#010x}: {e}"));
        assert_eq!(r.remaining(), 0, "{id:#010x} has trailing bytes");
        each(id, object);
        n += 1;
    }
    n
}

#[test]
#[ignore = "needs the retail dat files"]
fn every_animation_decodes_to_the_golden_totals() {
    let d = dats();
    let (mut parts, mut pos, mut part_frames, mut frames, mut hooks) =
        (0u64, 0u64, 0u64, 0u64, 0u64);
    let mut fsum = 0u64;
    let mut flags = BTreeMap::new();
    let mut types = BTreeMap::new();
    let n = sweep::<Animation>(&d.portal, 0x0300_0000, 0x0300_FFFF, |_, a| {
        parts += u64::from(a.num_parts);
        pos += a.pos_frames.len() as u64;
        part_frames += a.part_frames.len() as u64;
        *flags.entry(a.flags.0).or_insert(0) += 1;
        for p in &a.pos_frames {
            fsum += bits(p.origin.x) + bits(p.orientation.w);
        }
        for f in &a.part_frames {
            frames += f.frames.len() as u64;
            for fr in &f.frames {
                fsum += bits(fr.origin.z) + bits(fr.orientation.x);
            }
            for h in &f.hooks {
                hooks += 1;
                *types.entry(hook_type(&h.data)).or_insert(0) += 1;
            }
        }
    });
    assert_eq!(
        (n, parts, pos, part_frames, frames, hooks),
        (2066, 44_142, 9878, 95_006, 1_975_959, 6419)
    );
    assert_eq!(fsum, 6_050_853_782_724_954);
    assert_eq!(
        flags.into_iter().collect::<Vec<_>>(),
        [(0, 519), (1, 183), (2, 1190), (3, 174)]
    );
    // No hook of an unknown type, no no-op hook.
    assert_eq!(
        types.into_iter().collect::<Vec<_>>(),
        [
            (1, 10),
            (2, 3228),
            (3, 1053),
            (5, 2),
            (6, 86),
            (7, 1062),
            (13, 533),
            (15, 128),
            (17, 23),
            (19, 104),
            (20, 9),
            (21, 173),
            (22, 8)
        ]
    );
}

fn hook_type(h: &HookData) -> u32 {
    match h {
        HookData::NoOp => 0,
        HookData::Sound { .. } => 1,
        HookData::SoundTable { .. } => 2,
        HookData::Attack(_) => 3,
        HookData::AnimationDone => 4,
        HookData::ReplaceObject { .. } => 5,
        HookData::Ethereal { .. } => 6,
        HookData::TransparentPart { .. } => 7,
        HookData::Luminous { .. } => 8,
        HookData::LuminousPart { .. } => 9,
        HookData::Diffuse { .. } => 10,
        HookData::DiffusePart { .. } => 11,
        HookData::Scale { .. } => 12,
        HookData::CreateParticle { .. } => 13,
        HookData::DestroyParticle { .. } => 14,
        HookData::StopParticle { .. } => 15,
        HookData::NoDraw { .. } => 16,
        HookData::DefaultScript => 17,
        HookData::DefaultScriptPart { .. } => 18,
        HookData::CallPes { .. } => 19,
        HookData::Transparent { .. } => 20,
        HookData::SoundTweaked { .. } => 21,
        HookData::SetOmega { .. } => 22,
        HookData::TextureVelocity { .. } => 23,
        HookData::TextureVelocityPart { .. } => 24,
        HookData::SetLight { .. } => 25,
        HookData::CreateBlockingParticle { .. } => 26,
        HookData::Unknown { hook_type } => *hook_type,
    }
}

#[test]
#[ignore = "needs the retail dat files"]
fn animations_match_the_goldens() {
    let d = dats();
    let a: Animation = d.get(0x0300_0001).unwrap().unwrap();
    assert_eq!((a.id, a.flags.0, a.num_parts), (0x0300_0001, 2, 34));
    assert!(a.pos_frames.is_empty());
    assert_eq!(a.part_frames.len(), 33);
    let f = a.part_frames[0].frames[0];
    assert_eq!(
        (
            f.origin.x.to_bits(),
            f.origin.y.to_bits(),
            f.origin.z.to_bits()
        ),
        (0, 0, 0x3F76_6666)
    );
    assert_eq!(
        (
            f.orientation.w.to_bits(),
            f.orientation.x.to_bits(),
            f.orientation.y.to_bits(),
            f.orientation.z.to_bits()
        ),
        (0xBCA4_62B6, 0x3C96_1657, 0xB9C0_D2BB, 0xBF7F_E7D1)
    );
    let h = a.part_frames[0].hooks[0];
    assert_eq!(
        (h.direction, h.data),
        (1, HookData::SoundTable { sound: 1 })
    );
    let last = a.part_frames[32].frames[33];
    assert_eq!(
        (
            last.origin.x.to_bits(),
            last.origin.y.to_bits(),
            last.origin.z.to_bits(),
            last.orientation.w.to_bits()
        ),
        (0xBBD5_80BB, 0xBEC8_91C1, 0x3F19_3F3A, 0x3CAF_4568)
    );

    // The first hook of several types, field by field.
    let first = |id: u32, frame: usize, kind: u32| {
        let a: Animation = d.get(id).unwrap().unwrap();
        *a.part_frames[frame]
            .hooks
            .iter()
            .find(|h| hook_type(&h.data) == kind)
            .unwrap()
    };
    let h = first(0x0300_000E, 6, 3);
    let HookData::Attack(c) = h.data else {
        panic!("attack")
    };
    assert_eq!(
        (
            h.direction,
            c.part_index,
            c.left_x.to_bits(),
            c.radius.to_bits(),
            c.height.to_bits()
        ),
        (0, 5, 0xBF00_0000, 0x4000_0000, 0x3F80_0000)
    );
    let h = first(0x0300_0098, 0, 21);
    let HookData::SoundTweaked {
        sound_id,
        priority,
        probability,
        volume,
    } = h.data
    else {
        panic!("sound tweaked")
    };
    assert_eq!(
        (
            sound_id,
            priority.to_bits(),
            probability.to_bits(),
            volume.to_bits()
        ),
        (0x0A00_0243, 0x3DCC_CCCD, 0x3E99_999A, 0x3E99_999A)
    );
    let h = first(0x0300_03C2, 0, 13);
    let HookData::CreateParticle {
        emitter_info_id,
        part_index,
        offset,
        emitter_id,
    } = h.data
    else {
        panic!("create particle")
    };
    assert_eq!(
        (
            emitter_info_id,
            part_index,
            offset.origin.z.to_bits(),
            emitter_id
        ),
        (0x3200_0029, 12, 0xBDCC_CCCD, 1)
    );
    let h = first(0x0300_06B4, 0, 22);
    let HookData::SetOmega { axis } = h.data else {
        panic!("set omega")
    };
    assert_eq!(
        (axis.x.to_bits(), axis.y.to_bits(), axis.z.to_bits()),
        (0, 0x305B_4A01, 0xBCBE_EE96)
    );
    assert_eq!(
        first(0x0300_079E, 0, 19).data,
        HookData::CallPes {
            pes: 0x3300_07CB,
            pause: 0.0
        }
    );
    assert_eq!(
        first(0x0300_0559, 1, 6),
        AnimationHook {
            direction: 1,
            data: HookData::Ethereal { ethereal: true }
        }
    );

    // The only two part-swap hooks, read with a one-byte part index as the
    // retail client does. A reader that takes a two-byte index here
    // reports part 0xB401 with model 0x0100000B.
    let a: Animation = d.get(0x0300_055B).unwrap().unwrap();
    let swaps: Vec<_> = a.part_frames[0]
        .hooks
        .iter()
        .map(|h| (h.direction, h.data))
        .collect();
    assert_eq!(
        swaps,
        [
            (
                0,
                HookData::ReplaceObject {
                    part_index: 1,
                    part_id: 0x0100_0BB4
                }
            ),
            (
                0xFFFF_FFFF,
                HookData::ReplaceObject {
                    part_index: 1,
                    part_id: 0x0100_0BB5
                }
            )
        ]
    );
}

#[test]
#[ignore = "needs the retail dat files"]
fn every_motion_table_decodes_to_the_golden_totals() {
    let d = dats();
    let (mut style_defaults, mut cycles, mut modifiers, mut links, mut link_targets) =
        (0u64, 0u64, 0u64, 0u64, 0u64);
    let (mut anims, mut velocity, mut omega, mut fsum, mut keysum) = (0u64, 0u64, 0u64, 0u64, 0u64);
    let mut motion = |m: &MotionData, keysum: &mut u64| {
        anims += m.anims.len() as u64;
        for a in &m.anims {
            fsum += bits(a.framerate);
            // The golden program adds these three as wrapping u32s.
            *keysum += u64::from(
                a.anim_id
                    .wrapping_add(a.low_frame as u32)
                    .wrapping_add(a.high_frame as u32),
            );
        }
        if m.flags.contains(MotionDataFlags::HAS_VELOCITY) {
            velocity += 1;
            fsum += bits(m.velocity.y);
        }
        if m.flags.contains(MotionDataFlags::HAS_OMEGA) {
            omega += 1;
            fsum += bits(m.omega.z);
        }
        *keysum += u64::from(m.bitfield);
    };
    let n = sweep::<MotionTable>(&d.portal, 0x0900_0000, 0x0900_FFFF, |_, t| {
        style_defaults += t.style_defaults.len() as u64;
        cycles += t.cycles.len() as u64;
        modifiers += t.modifiers.len() as u64;
        links += t.links.len() as u64;
        keysum += u64::from(t.default_style);
        for (k, m) in t.cycles.iter().chain(&t.modifiers) {
            keysum += u64::from(*k);
            motion(m, &mut keysum);
        }
        for (k, targets) in &t.links {
            keysum += u64::from(*k);
            link_targets += targets.len() as u64;
            for (k, m) in targets {
                keysum += u64::from(*k);
                motion(m, &mut keysum);
            }
        }
    });
    assert_eq!(
        (n, style_defaults, cycles, modifiers, links, link_targets),
        (436, 1287, 18_451, 1222, 14_481, 42_537)
    );
    assert_eq!((anims, velocity, omega), (79_162, 441, 1843));
    assert_eq!((fsum, keysum), (117_832_409_979_700, 44_382_335_134_662));
}

#[test]
#[ignore = "needs the retail dat files"]
fn human_motion_table_matches_the_goldens() {
    let d = dats();
    let t: MotionTable = d.get(0x0900_0001).unwrap().unwrap();
    assert_eq!(t.default_style, 0x8000_003D);
    assert_eq!(
        (
            t.style_defaults.len(),
            t.cycles.len(),
            t.modifiers.len(),
            t.links.len()
        ),
        (14, 366, 8, 318)
    );
    assert_eq!(t.style_defaults[0], (0x8000_0040, 0x4100_0003));
    assert_eq!(t.style_defaults[13], (0x8000_003F, 0x4100_0003));
    let keys: Vec<u32> = t.cycles[..6].iter().map(|(k, _)| *k).collect();
    assert_eq!(
        keys,
        [
            0x003D_0141,
            0x003D_0140,
            0x003D_0143,
            0x0046_0003,
            0x0044_0003,
            0x00E9_0003
        ]
    );
    let c = &t.cycles[0].1;
    assert_eq!((c.bitfield, c.flags.0, c.anims.len()), (0, 0, 1));
    let a = c.anims[0];
    assert_eq!(
        (a.anim_id, a.low_frame, a.high_frame, a.framerate.to_bits()),
        (0x0300_08EA, 15, -1, 0)
    );
    let (key, m) = &t.modifiers[0];
    assert_eq!((*key, m.flags.0, m.anims.len()), (0x0046_000D, 2, 0));
    assert_eq!(
        (
            m.omega.x.to_bits(),
            m.omega.y.to_bits(),
            m.omega.z.to_bits()
        ),
        (0, 0, 0xBFC0_0000)
    );
    let (key, targets) = &t.links[0];
    assert_eq!((*key, targets.len()), (0x003D_0141, 1));
    assert_eq!(targets[0].0, 0x4100_0003);
    assert_eq!(targets[0].1.anims[0].anim_id, 0x0300_08EA);
}

fn is_env_cell(id: u32) -> bool {
    (0x0100..=0xFFFD).contains(&(id & 0xFFFF))
}

#[test]
#[ignore = "needs the retail dat files"]
fn every_env_cell_decodes_to_the_golden_totals() {
    let d = dats();
    let (mut surfaces, mut portals, mut visible, mut stabs, mut restricted, mut seen_outside) =
        (0u64, 0u64, 0u64, 0u64, 0u64, 0u64);
    let (mut fsum, mut isum) = (0u64, 0u64);
    let n = sweep_where::<EnvCell>(&d.cell, is_env_cell, |id, c| {
        assert_eq!(c.cell_id, id);
        surfaces += c.surfaces.len() as u64;
        portals += c.cell_portals.len() as u64;
        visible += c.visible_cells.len() as u64;
        stabs += c.static_objects.len() as u64;
        restricted += u64::from(c.flags.contains(EnvCellFlags::HAS_RESTRICTION_OBJ));
        seen_outside += u64::from(c.flags.contains(EnvCellFlags::SEEN_OUTSIDE));
        fsum += bits(c.position.origin.x) + bits(c.position.orientation.w);
        // The golden program adds each group of fields as wrapping u32s.
        isum += u64::from(
            c.flags
                .0
                .wrapping_add(u32::from(c.environment_id))
                .wrapping_add(u32::from(c.cell_structure))
                .wrapping_add(c.restriction_obj),
        );
        isum += c.surfaces.iter().map(|&s| u64::from(s)).sum::<u64>();
        for p in &c.cell_portals {
            isum += u64::from(p.flags)
                + u64::from(p.polygon_id)
                + u64::from(p.other_cell_id)
                + u64::from(p.other_portal_id);
        }
        isum += c.visible_cells.iter().map(|&v| u64::from(v)).sum::<u64>();
        for s in &c.static_objects {
            isum += u64::from(s.id);
            fsum += bits(s.frame.origin.z);
        }
    });
    assert_eq!(
        (
            n,
            surfaces,
            portals,
            visible,
            stabs,
            restricted,
            seen_outside
        ),
        (
            734_976, 3_881_969, 1_867_699, 56_600_648, 597_863, 103_766, 61_702
        )
    );
    assert_eq!((fsum, isum), (3_350_201_757_555_273, 223_602_620_109_315));
}

#[test]
#[ignore = "needs the retail dat files"]
fn holtburg_env_cells_match_the_goldens() {
    let d = dats();
    let c: EnvCell = d.get(0xA9B4_0100).unwrap().unwrap();
    assert_eq!((c.id, c.flags.0, c.cell_id), (0xA9B4_0100, 3, 0xA9B4_0100));
    assert_eq!((c.environment_id, c.cell_structure), (0x034F, 0));
    assert_eq!(c.environment_file_id(), 0x0D00_034F);
    assert_eq!(c.restriction_obj, 0);
    let p = c.position;
    assert_eq!(
        (
            p.origin.x.to_bits(),
            p.origin.y.to_bits(),
            p.origin.z.to_bits(),
            p.orientation.w.to_bits(),
            p.orientation.z.to_bits()
        ),
        (
            0x42A8_2F91,
            0x4303_8AC1,
            0x4284_0000,
            0x3F35_04F7,
            0xBF35_04F7
        )
    );
    assert_eq!(
        c.surfaces,
        [
            0x0371, 0x0372, 0x07E4, 0x07E6, 0x07E7, 0x05AB, 0x032A, 0x07E2, 0x07E3, 0x074E, 0x06E8
        ]
    );
    let portals: Vec<_> = c
        .cell_portals
        .iter()
        .map(|p| (p.flags, p.polygon_id, p.other_cell_id, p.other_portal_id))
        .collect();
    assert_eq!(
        portals,
        [
            (7, 25, 0xFFFF, 0xFFFF),
            (7, 26, 0xFFFF, 0xFFFF),
            (1, 30, 0x0110, 0),
            (1, 28, 0x0102, 0),
            (1, 29, 0x0103, 1),
            (7, 27, 0xFFFF, 0xFFFF)
        ]
    );
    assert_eq!(c.visible_cells, (0x0101..=0x0110).collect::<Vec<u16>>());
    assert_eq!(c.static_objects.len(), 22);
    assert_eq!(
        (
            c.static_objects[0].id,
            c.static_objects[0].frame.origin.x.to_bits()
        ),
        (0x0100_0C20, 0x42B8_2F91)
    );
    assert_eq!(
        (
            c.static_objects[21].id,
            c.static_objects[21].frame.origin.x.to_bits()
        ),
        (0x0200_0162, 0x42A8_492A)
    );

    let c: EnvCell = d.get(0xA9B4_0105).unwrap().unwrap();
    assert_eq!(c.cell_structure, 5);
    let portals: Vec<_> = c
        .cell_portals
        .iter()
        .map(|p| (p.flags, p.polygon_id, p.other_cell_id, p.other_portal_id))
        .collect();
    assert_eq!(
        portals,
        [(1, 9, 0x0106, 1), (1, 10, 0x010F, 0), (1, 11, 0x0101, 2)]
    );
    assert_eq!(c.static_objects.len(), 6);
    assert_eq!(c.static_objects[5].id, 0x0200_00AC);

    // A dungeon cell without static objects.
    let c: EnvCell = d.get(0x01D9_0108).unwrap().unwrap();
    assert_eq!((c.flags.0, c.environment_id), (0, 0x00F0));
    assert_eq!(c.surfaces, [0x04C3, 0x04C1, 0x04C0, 0x04C4, 0x0034]);
    assert_eq!(c.cell_portals.len(), 1);
    assert_eq!(c.visible_cells.len(), 27);
    assert!(c.static_objects.is_empty());
    assert_eq!(c.position.orientation.w.to_bits(), 0xB33B_BD31);
}

#[derive(Default)]
struct BspStats {
    nodes: [u64; 3],
    leaves: u64,
    portal_nodes: u64,
    poly_refs: u64,
    portal_refs: u64,
    max_depth: usize,
    fsum: u64,
}

impl BspStats {
    /// Visit every node from the root, front child first, tracking depth
    /// (the root is at depth 1).
    fn walk(&mut self, t: &BspTree, tree: usize) {
        let mut stack = vec![(0usize, 1usize)];
        let mut visited = 0;
        while let Some((index, depth)) = stack.pop() {
            let node = &t.nodes[index];
            visited += 1;
            self.max_depth = self.max_depth.max(depth);
            self.nodes[tree] += 1;
            match node {
                BspNode::Leaf(l) => {
                    self.leaves += 1;
                    self.poly_refs += l.polygons.len() as u64;
                    self.fsum += bits(l.sphere.radius);
                }
                BspNode::Portal(p) => {
                    self.portal_nodes += 1;
                    self.poly_refs += p.polygons.len() as u64;
                    self.portal_refs += p.portals.len() as u64;
                    self.fsum += bits(p.plane.distance) + bits(p.sphere.radius);
                }
                BspNode::Split(s) => {
                    self.poly_refs += s.polygons.len() as u64;
                    self.fsum += bits(s.plane.distance) + bits(s.sphere.radius);
                }
            }
            let children: Vec<usize> = node.children().collect();
            stack.extend(children.into_iter().rev().map(|c| (c, depth + 1)));
        }
        // Every node is reached from the root exactly once.
        assert_eq!(visited, t.nodes.len());
    }
}

#[test]
#[ignore = "needs the retail dat files"]
fn every_environment_decodes_to_the_golden_totals() {
    let d = dats();
    let (mut cells, mut verts, mut uvs, mut polys, mut phys_polys, mut portals, mut drawing) =
        (0u64, 0u64, 0u64, 0u64, 0u64, 0u64, 0u64);
    let (mut fsum, mut isum) = (0u64, 0u64);
    let mut bsp = BspStats::default();
    let n = sweep::<Environment>(&d.portal, 0x0D00_0000, 0x0D00_FFFF, |_, e| {
        cells += e.cells.len() as u64;
        for (key, c) in &e.cells {
            isum += u64::from(key.wrapping_add(c.vertex_array.vertex_type as u32));
            verts += c.vertex_array.vertices.len() as u64;
            for (vkey, v) in &c.vertex_array.vertices {
                uvs += v.uvs.len() as u64;
                fsum += bits(v.origin.x) + bits(v.normal.z);
                isum += u64::from(*vkey);
                fsum += v.uvs.iter().map(|uv| bits(uv.u)).sum::<u64>();
            }
            polys += c.polygons.len() as u64;
            phys_polys += c.physics_polygons.len() as u64;
            portals += c.portals.len() as u64;
            for (pkey, p) in c.polygons.iter().chain(&c.physics_polygons) {
                isum += u64::from(
                    u32::from(*pkey)
                        .wrapping_add(u32::from(p.stippling.0))
                        .wrapping_add(p.sides_type.0 as u32)
                        .wrapping_add(u32::from(p.pos_surface as u16))
                        .wrapping_add(u32::from(p.neg_surface as u16))
                        .wrapping_add(p.vertex_ids.len() as u32)
                        .wrapping_add(p.pos_uv_indices.len() as u32)
                        .wrapping_add(p.neg_uv_indices.len() as u32),
                );
                isum += p.vertex_ids.iter().map(|&v| u64::from(v)).sum::<u64>();
            }
            isum += c.portals.iter().map(|&p| u64::from(p)).sum::<u64>();
            bsp.walk(&c.cell_bsp, 0);
            bsp.walk(&c.physics_bsp, 1);
            if let Some(t) = &c.drawing_bsp {
                drawing += 1;
                bsp.walk(t, 2);
            }
        }
    });
    assert_eq!(
        (n, cells, verts, uvs, polys, phys_polys, portals, drawing),
        (772, 3168, 45_800, 78_548, 38_189, 30_726, 7997, 3165)
    );
    assert_eq!((fsum, isum), (242_294_764_889_394, 6_511_912_983));
    assert_eq!(bsp.nodes, [21_919, 47_894, 16_643]);
    assert_eq!(
        (bsp.leaves, bsp.portal_nodes, bsp.poly_refs, bsp.portal_refs),
        (25_531, 0, 61_107, 0)
    );
    assert_eq!((bsp.max_depth, bsp.fsum), (63, 191_569_902_605_785));
}

#[test]
#[ignore = "needs the retail dat files"]
fn environment_matches_the_goldens() {
    let d = dats();
    let e: Environment = d.get(0x0D00_00CA).unwrap().unwrap();
    assert_eq!(e.cells.len(), 1);
    let c = e.cell(0).unwrap();
    assert_eq!(
        (
            c.vertex_array.vertices.len(),
            c.polygons.len(),
            c.physics_polygons.len()
        ),
        (8, 1, 6)
    );
    assert_eq!(c.portals, [0]);
    let (key, v) = &c.vertex_array.vertices[0];
    assert_eq!(*key, 0);
    assert_eq!(
        (
            v.origin.x.to_bits(),
            v.origin.y.to_bits(),
            v.origin.z.to_bits(),
            v.normal.z.to_bits()
        ),
        (0xC0A0_0000, 0xC0A0_0000, 0, 0xBF80_0000)
    );
    assert_eq!(v.uvs.len(), 1);
    assert_eq!(
        (v.uvs[0].u.to_bits(), v.uvs[0].v.to_bits()),
        (0, 0x4040_0000)
    );
    let (key, p) = &c.polygons[0];
    assert_eq!(*key, 0);
    assert_eq!(
        (p.stippling.0, p.sides_type.0, p.pos_surface, p.neg_surface),
        (1, 0, 0, -1)
    );
    assert_eq!(p.vertex_ids, [0, 1, 2, 3]);
    assert_eq!(p.pos_uv_indices, [0, 0, 0, 0]);
    assert!(p.neg_uv_indices.is_empty());
    let Some(BspNode::Split(s)) = c.cell_bsp.root() else {
        panic!("cell tree root is a split")
    };
    assert_eq!(s.tag, BspTag::from_text(*b"BPnn"));
    assert_eq!(s.tag.0, 0x4250_6E6E);
    assert_eq!(
        (s.plane.normal.z.to_bits(), s.plane.distance.to_bits()),
        (0x3F80_0000, 0)
    );
    let Some(BspNode::Split(s)) = c.physics_bsp.root() else {
        panic!("physics tree root is a split")
    };
    assert_eq!(s.tag, BspTag::from_text(*b"BPnN"));
    assert_eq!(s.tag.0, 0x4250_6E4E);
    assert_eq!(s.sphere.radius.to_bits(), 0x40E2_9821);
    assert!(s.polygons.is_empty());
    let Some(BspNode::Split(s)) = c.drawing_bsp.as_ref().and_then(BspTree::root) else {
        panic!("drawing tree root is a split")
    };
    // A childless split; its plane holds the fill pattern.
    assert_eq!(s.tag, BspTag::from_text(*b"BPOL"));
    assert_eq!(s.tag.0, 0x4250_4F4C);
    assert_eq!(s.plane.distance.to_bits(), 0xCDCD_CDCD);
    assert_eq!(s.sphere.radius.to_bits(), 0x40E2_9821);
    assert_eq!(s.polygons, [0]);
    assert!(s.positive.is_none() && s.negative.is_none());
}

#[test]
#[ignore = "needs the retail dat files"]
fn region_matches_the_goldens() {
    let d = dats();
    let ids: Vec<u32> = d
        .portal
        .entries()
        .map(|e| e.unwrap().id)
        .filter(|id| (0x1300_0000..=0x1300_FFFF).contains(id))
        .collect();
    assert_eq!(ids, [0x1300_0000]);
    sweep::<Region>(&d.portal, 0x1300_0000, 0x1300_FFFF, |_, _| {});
    let g: Region = d.get(0x1300_0000).unwrap().unwrap();
    assert_eq!(
        (g.region_number, g.version, g.name.as_str(), g.parts_mask.0),
        (1, 3, "Dereth", 0x21F)
    );
    let l = &g.land_defs;
    assert_eq!(
        (
            l.num_block_length,
            l.num_block_width,
            l.square_length.to_bits(),
            l.lblock_length,
            l.vertex_per_cell
        ),
        (255, 255, 0x41C0_0000, 8, 1)
    );
    assert_eq!(
        (
            l.max_obj_height.to_bits(),
            l.sky_height.to_bits(),
            l.road_width.to_bits()
        ),
        (0x4348_0000, 0x447A_0000, 0x40A0_0000)
    );
    let h = &l.land_height_table;
    assert_eq!(
        (
            h[0].to_bits(),
            h[1].to_bits(),
            h[100].to_bits(),
            h[255].to_bits()
        ),
        (0, 0x4000_0000, 0x4348_0000, 0x442F_0000)
    );

    let t = &g.game_time;
    assert_eq!(t.zero_time_of_year.to_bits(), 0x40AC_2000_0000_0000);
    assert_eq!(
        (t.zero_year, t.day_length.to_bits(), t.days_per_year),
        (10, 0x45EE_2000, 360)
    );
    assert_eq!(t.year_spec, "P.Y.");
    assert_eq!(t.times_of_day.len(), 16);
    let tod: Vec<_> = t
        .times_of_day
        .iter()
        .map(|t| (t.start.to_bits(), t.is_night, t.name.as_str()))
        .collect();
    assert_eq!(tod[0], (0, true, "Darktide"));
    assert_eq!(tod[4], (0x3E80_0000, false, "Dawnsong"));
    assert_eq!(tod[15], (0x3F70_0000, true, "Gloaming-and-Half"));
    assert_eq!(
        t.days_of_week,
        [
            "FirstDay", "StarDay", "EarthDay", "MoonsDay", "ElderDay", "Freeday"
        ]
    );
    let seasons: Vec<_> = t
        .seasons
        .iter()
        .map(|s| (s.start, s.name.as_str()))
        .collect();
    assert_eq!(seasons.len(), 12);
    assert_eq!(seasons[0], (0, "Morningthaw"));
    assert_eq!(seasons[6], (180, "HarvestGain"));
    assert_eq!(seasons[11], (330, "Wintersebb"));

    let sky = g.sky_info.as_ref().unwrap();
    assert_eq!(
        (
            sky.tick_size.to_bits(),
            sky.light_tick_size.to_bits(),
            sky.day_groups.len()
        ),
        (0x3FE9_9999_A000_0000, 0x402E_0000_0000_0000, 20)
    );
    let (mut objects, mut times, mut replaces, mut fsum) = (0, 0, 0, 0u64);
    for group in &sky.day_groups {
        objects += group.sky_objects.len();
        times += group.sky_time.len();
        for t in &group.sky_time {
            replaces += t.sky_obj_replace.len();
            fsum += bits(t.dir_bright) + bits(t.max_world_fog);
        }
        for o in &group.sky_objects {
            fsum += bits(o.begin_angle) + u64::from(o.default_gfx_object_id);
        }
    }
    assert_eq!(
        (objects, times, replaces, fsum),
        (232, 232, 897, 710_966_160_244)
    );
    let g0 = &sky.day_groups[0];
    assert_eq!(
        (
            g0.chance_of_occur.to_bits(),
            g0.day_name.as_str(),
            g0.sky_objects.len(),
            g0.sky_time.len()
        ),
        (0x40A0_0000, "Sunny", 7, 11)
    );
    let o = g0.sky_objects[0];
    assert_eq!(
        (o.begin_time, o.end_time, o.begin_angle, o.end_angle),
        (0.0, 0.0, 0.0, 0.0)
    );
    assert_eq!(
        (
            o.default_gfx_object_id,
            o.default_pes_object_id,
            o.properties
        ),
        (0x0100_15EE, 0, 0)
    );
    let s = &g0.sky_time[0];
    assert_eq!(
        (
            s.begin.to_bits(),
            s.dir_bright.to_bits(),
            s.dir_heading.to_bits(),
            s.dir_pitch.to_bits()
        ),
        (0, 0x3E80_0000, 0x42B4_0000, 0x3F66_6666)
    );
    let argb = |c: ColorArgb| u32::from_be_bytes([c.alpha, c.red, c.green, c.blue]);
    assert_eq!(
        (
            argb(s.dir_color),
            argb(s.amb_color),
            argb(s.world_fog_color)
        ),
        (0xFFDC_DCDC, 0xFFC8_64FF, 0xFF17_1728)
    );
    assert_eq!(
        (
            s.amb_bright.to_bits(),
            s.min_world_fog.to_bits(),
            s.max_world_fog.to_bits(),
            s.world_fog,
            s.sky_obj_replace.len()
        ),
        (0x3ECC_CCCD, 0, 0x43C8_0000, 1, 3)
    );
    let rep = s.sky_obj_replace[0];
    assert_eq!(
        (
            rep.object_index,
            rep.gfx_obj_id,
            rep.rotate.to_bits(),
            rep.transparent.to_bits(),
            rep.luminosity.to_bits(),
            rep.max_bright.to_bits()
        ),
        (0, 0, 0x4387_0000, 0xBF80_0000, 0x4130_0000, 0x4130_0000)
    );

    let sound = g.sound_info.as_ref().unwrap();
    let ambient: usize = sound.stb_desc.iter().map(|s| s.ambient_sounds.len()).sum();
    assert_eq!((sound.stb_desc.len(), ambient), (37, 383));
    let stb = &sound.stb_desc[0];
    assert_eq!((stb.stb_id, stb.ambient_sounds.len()), (0x2000_0017, 9));
    let a = stb.ambient_sounds[0];
    assert_eq!(
        (
            a.sound_type,
            a.volume.to_bits(),
            a.base_chance.to_bits(),
            a.min_rate.to_bits(),
            a.max_rate.to_bits()
        ),
        (70, 0x3F19_999A, 0x3F00_0000, 0x3FB3_3333, 0x4120_0000)
    );

    let scene = g.scene_info.as_ref().unwrap();
    let scenes: usize = scene.scene_types.iter().map(|t| t.scenes.len()).sum();
    assert_eq!((scene.scene_types.len(), scenes), (89, 169));
    let t0 = &scene.scene_types[0];
    assert_eq!(
        (t0.stb_index, t0.scenes.len(), t0.scenes[0]),
        (8, 4, 0x1200_0076)
    );
    assert_eq!(scene.scene_types[1].scenes[0], 0x1200_00A5);

    let terrain = &g.terrain_info;
    let refs: usize = terrain
        .terrain_types
        .iter()
        .map(|t| t.scene_types.len())
        .sum();
    assert_eq!((terrain.terrain_types.len(), refs), (32, 1024));
    let names: Vec<&str> = terrain
        .terrain_types
        .iter()
        .map(|t| t.terrain_name.as_str())
        .collect();
    assert_eq!(names[..4], ["BarrenRock", "Grassland", "Ice", "LushGrass"]);
    assert_eq!(names[10], "sand-yellow");
    assert_eq!(names[31], "DesolateLands");
    let t0 = &terrain.terrain_types[0];
    assert_eq!(argb(t0.terrain_color), 0xFF64_9664);
    assert_eq!(
        t0.scene_types,
        [
            1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 1, 1, 1, 1, 29, 1, 86, 85, 84, 1, 1, 1, 1, 1, 1, 1, 1,
            1, 71, 1, 1
        ]
    );
    let ls = &terrain.land_surfaces;
    let tm = &ls.tex_merge;
    assert_eq!(
        (
            ls.surf_type,
            tm.base_tex_size,
            tm.corner_terrain_maps.len(),
            tm.side_terrain_maps.len(),
            tm.road_maps.len(),
            tm.terrain_desc.len()
        ),
        (0, 1024, 4, 1, 3, 33)
    );
    assert_eq!(
        (
            tm.corner_terrain_maps[0].t_code,
            tm.corner_terrain_maps[0].texture_id
        ),
        (8, 0x0500_1371)
    );
    assert_eq!(
        (
            tm.side_terrain_maps[0].t_code,
            tm.side_terrain_maps[0].texture_id
        ),
        (9, 0x0500_1441)
    );
    assert_eq!(
        (tm.road_maps[0].r_code, tm.road_maps[0].texture_id),
        (9, 0x0500_168E)
    );
    let d0 = tm.terrain_desc[0];
    let x = d0.terrain_tex;
    assert_eq!(
        (
            d0.terrain_type,
            x.texture_id,
            x.tex_tiling,
            x.max_vert_bright,
            x.min_vert_bright,
            x.max_vert_saturate,
            x.min_vert_saturate,
            x.max_vert_hue,
            x.min_vert_hue,
            x.detail_tex_tiling,
            x.detail_texture_id
        ),
        (0, 0x0500_145C, 2, 100, 90, 90, 70, 150, 90, 4, 0x0500_1786)
    );
    let last = tm.terrain_desc[32];
    assert_eq!(
        (
            last.terrain_type,
            last.terrain_tex.texture_id,
            last.terrain_tex.detail_texture_id
        ),
        (32, 0x0500_1458, 0x0500_12AF)
    );
    let m = g.region_misc.unwrap();
    assert_eq!(
        (
            m.version,
            m.game_map_id,
            m.autotest_map_id,
            m.autotest_map_size,
            m.clear_cell_id,
            m.clear_monster_id
        ),
        (1, 0x0600_127D, 0x0600_0261, 4, 0x0100_0FDE, 0x0100_1612)
    );
}

#[test]
#[ignore = "needs the retail dat files"]
fn scenes_decode_to_the_golden_totals() {
    let d = dats();
    let (mut objects, mut fsum, mut isum) = (0u64, 0u64, 0u64);
    let mut first = None;
    let n = sweep::<Scene>(&d.portal, 0x1200_0000, 0x1200_FFFF, |_, s| {
        objects += s.objects.len() as u64;
        for o in &s.objects {
            fsum += bits(o.frequency) + bits(o.base_loc.origin.x) + bits(o.max_slope);
            isum += u64::from(
                o.object_id
                    .wrapping_add(o.align as u32)
                    .wrapping_add(o.orient as u32)
                    .wrapping_add(o.weenie_obj),
            );
        }
        first.get_or_insert(s);
    });
    assert_eq!((n, objects), (179, 1167));
    assert_eq!((fsum, isum), (4_959_599_954_196, 32_969_515_353));
    let s = first.unwrap();
    assert_eq!((s.id, s.objects.len()), (0x1200_0009, 8));
    let o = s.objects[0];
    assert_eq!(o.object_id, 0x0200_02D3);
    assert_eq!(
        (
            o.base_loc.origin.x.to_bits(),
            o.base_loc.origin.y.to_bits(),
            o.base_loc.orientation.w.to_bits()
        ),
        (0x3FB8_51EC, 0xC12E_B852, 0x3F80_0000)
    );
    assert_eq!(
        [
            o.frequency,
            o.displace_x,
            o.displace_y,
            o.min_scale,
            o.max_scale,
            o.max_rotation,
            o.min_slope,
            o.max_slope
        ]
        .map(f32::to_bits),
        [
            0x3F3A_5E35,
            0x4108_51F6,
            0x400A_3D47,
            0x3E99_999A,
            0x3F4C_CCCD,
            0x43A2_0CCD,
            0x3F48_11BA,
            0x3F80_0000
        ]
    );
    assert_eq!((o.align, o.orient, o.weenie_obj), (0, 0, 0));
}

#[test]
#[ignore = "needs the retail dat files"]
fn sound_tables_and_waves_decode_to_the_golden_totals() {
    let d = dats();
    let (mut hashes, mut sounds, mut entries, mut fsum, mut isum) = (0u64, 0u64, 0u64, 0u64, 0u64);
    let n = sweep::<SoundTable>(&d.portal, 0x2000_0000, 0x2000_FFFF, |_, t| {
        hashes += t.hashes.len() as u64;
        sounds += t.sounds.len() as u64;
        isum += u64::from(t.hash_key as u32);
        for (key, h) in &t.hashes {
            isum += u64::from(*key);
            fsum += bits(h.volume);
        }
        for (key, s) in &t.sounds {
            isum += u64::from(key.wrapping_add(s.unknown as u32));
            entries += s.entries.len() as u64;
            for e in &s.entries {
                isum += u64::from(e.id);
                fsum += bits(e.priority) + bits(e.probability) + bits(e.volume);
            }
        }
    });
    assert_eq!((n, hashes, sounds, entries), (190, 190, 4184, 4185));
    assert_eq!((fsum, isum), (13_376_753_677_244, 702_129_851_850));

    let t: SoundTable = d.get(0x2000_0001).unwrap().unwrap();
    assert_eq!((t.hash_key, t.hashes.len(), t.sounds.len()), (0, 1, 52));
    let keys: Vec<u32> = t.sounds[..5].iter().map(|(k, _)| *k).collect();
    assert_eq!(keys, [88, 154, 143, 44, 89]);
    let s = &t.sounds[0].1;
    assert_eq!((s.entries.len(), s.unknown), (1, 0));
    let e = s.entries[0];
    assert_eq!(
        (
            e.id,
            e.priority.to_bits(),
            e.probability.to_bits(),
            e.volume.to_bits()
        ),
        (0x0A00_0262, 0x3F33_3333, 0x3F80_0000, 0x3F80_0000)
    );
    let (key, h) = t.hashes[0];
    assert_eq!(
        (key, h.priority, h.probability, h.volume),
        (0, 0.0, 1.0, 1.0)
    );

    let (mut header, mut data, mut header_bytes) = (0u64, 0u64, 0u64);
    let n = sweep::<Wave>(&d.portal, 0x0A00_0000, 0x0A00_FFFF, |id, w| {
        header += w.header.len() as u64;
        data += w.data.len() as u64;
        header_bytes += w.header.iter().map(|&b| u64::from(b)).sum::<u64>();
        w.format()
            .unwrap_or_else(|e| panic!("{id:#010x} format: {e}"));
    });
    assert_eq!(
        (n, header, data, header_bytes),
        (786, 14_160, 52_397_474, 214_484)
    );
}

/// The enum id maps, and the chain the interface sound bank is found
/// through: master map (portal header) → client slot 7 → type key
/// `0x10000003`. Golden: OpenAC `UiSoundTableResolutionTests`
/// (`ExpectedUiSoundTableDid = 0x2000004B`).
#[test]
#[ignore = "needs the retail dat files"]
fn enum_id_maps_decode_and_name_the_interface_sound_bank() {
    let d = dats();
    let n = sweep::<EnumIdMap>(&d.portal, 0x2500_0000, 0x25FF_FFFF, |_, _| {});
    assert!(n > 0);
    let master_id = d.portal.header().master_map_id as u32;
    let master: EnumIdMap = d.get(master_id).unwrap().unwrap();
    let slot = master.client_id(7).unwrap();
    let per_slot: EnumIdMap = d.get(slot).unwrap().unwrap();
    assert_eq!(per_slot.client_id(0x1000_0003), Some(0x2000_004B));
}

/// The dual enum maps, and the material names they hold: master map →
/// client enum 0x10000001 → slot 1 (OpenAC
/// `RetailAppraisalNameResolver.Load`).
#[test]
#[ignore = "needs the retail dat files"]
fn dual_enum_id_maps_decode_and_name_the_materials() {
    let d = dats();
    let n = sweep::<DualEnumIdMap>(&d.portal, 0x2700_0000, 0x27FF_FFFF, |_, _| {});
    assert!(n > 0);
    let master_id = d.portal.header().master_map_id as u32;
    let master: EnumIdMap = d.get(master_id).unwrap().unwrap();
    let root: EnumIdMap = d
        .get(master.client_id(0x1000_0001).unwrap())
        .unwrap()
        .unwrap();
    let materials: DualEnumIdMap = d.get(root.client_id(1).unwrap()).unwrap().unwrap();
    assert_eq!(materials.client_name(62), Some("Pyreal"));
    assert_eq!(materials.client_name(63), Some("Silver"));
}

#[test]
#[ignore = "needs the retail dat files"]
fn physics_scripts_decode_to_the_golden_totals() {
    let d = dats();
    let (mut data, mut dsum) = (0u64, 0u64);
    let mut types = BTreeMap::new();
    let mut first = None;
    let n = sweep::<PhysicsScript>(&d.portal, 0x3300_0000, 0x3300_FFFF, |_, s| {
        data += s.script_data.len() as u64;
        for x in &s.script_data {
            dsum = dsum.wrapping_add(dbits(x.start_time));
            *types.entry(hook_type(&x.hook.data)).or_insert(0) += 1;
        }
        first.get_or_insert(s);
    });
    assert_eq!((n, data, dsum), (4248, 12_268, 9_743_704_462_169_473_024));
    assert_eq!(
        types.into_iter().collect::<Vec<_>>(),
        [
            (2, 626),
            (12, 122),
            (13, 10_743),
            (14, 1),
            (15, 15),
            (16, 2),
            (19, 354),
            (20, 10),
            (21, 368),
            (23, 11),
            (25, 1),
            (26, 15)
        ]
    );
    let s = first.unwrap();
    assert_eq!((s.id, s.script_data.len()), (0x3300_0007, 1));
    let x = s.script_data[0];
    assert_eq!(x.start_time, 0.0);
    let HookData::CreateParticle {
        emitter_info_id,
        part_index,
        offset,
        emitter_id,
    } = x.hook.data
    else {
        panic!("create particle")
    };
    assert_eq!(
        (
            x.hook.direction,
            emitter_info_id,
            part_index,
            offset.origin.z.to_bits(),
            emitter_id
        ),
        (0, 0x3200_000C, 0, 0x3F19_999A, 0)
    );

    // A blocking-particle hook carries the particle hook's fields, as in
    // the retail client; a reader that reads none leaves these 40 bytes
    // unread.
    let s: PhysicsScript = d.get(0x3300_0AEA).unwrap().unwrap();
    assert_eq!(s.script_data.len(), 1);
    let HookData::CreateBlockingParticle {
        emitter_info_id,
        part_index,
        offset,
        emitter_id,
    } = s.script_data[0].hook.data
    else {
        panic!("blocking particle")
    };
    assert_eq!(
        (
            emitter_info_id,
            part_index,
            offset.orientation.w.to_bits(),
            offset.orientation.x.to_bits(),
            emitter_id
        ),
        (0x3200_05F0, 0, 0x3F80_0000, 0x8000_0000, 0x7B5)
    );

    let (mut entries, mut scripts, mut fsum, mut isum) = (0u64, 0u64, 0u64, 0u64);
    let mut first = None;
    let n = sweep::<PhysicsScriptTable>(&d.portal, 0x3400_0000, 0x3400_FFFF, |_, t| {
        entries += t.script_table.len() as u64;
        for (key, x) in &t.script_table {
            isum += u64::from(*key);
            scripts += x.scripts.len() as u64;
            for s in &x.scripts {
                fsum += bits(s.modifier);
                isum += u64::from(s.script_id);
            }
        }
        first.get_or_insert(t);
    });
    assert_eq!((n, entries, scripts), (164, 11_427, 31_337));
    assert_eq!((fsum, isum), (24_476_083_290_116, 26_813_161_537_969));
    let t = first.unwrap();
    assert_eq!((t.id, t.script_table.len()), (0x3400_0004, 139));
    let (key, x) = &t.script_table[0];
    assert_eq!((*key, x.scripts.len()), (165, 1));
    assert_eq!(
        (x.scripts[0].modifier.to_bits(), x.scripts[0].script_id),
        (0x3F80_0000, 0x3300_125E)
    );
}

#[test]
#[ignore = "needs the retail dat files"]
fn particle_emitters_decode_to_the_golden_totals() {
    let d = dats();
    let (mut isum, mut fsum) = (0u64, 0u64);
    let mut first = None;
    let n = sweep::<ParticleEmitter>(&d.portal, 0x3200_0000, 0x3200_FFFF, |_, p| {
        isum += u64::from(
            p.unknown
                .wrapping_add(p.emitter_type as u32)
                .wrapping_add(p.particle_type as u32)
                .wrapping_add(p.gfx_obj_id)
                .wrapping_add(p.hw_gfx_obj_id)
                .wrapping_add(p.max_particles as u32)
                .wrapping_add(p.initial_particles as u32)
                .wrapping_add(p.total_particles as u32)
                .wrapping_add(u32::from(p.is_parent_local)),
        );
        fsum = fsum
            .wrapping_add(dbits(p.birthrate))
            .wrapping_add(dbits(p.lifespan))
            .wrapping_add(bits(p.a.z))
            .wrapping_add(bits(p.max_c))
            .wrapping_add(bits(p.trans_rand));
        first.get_or_insert(p);
    });
    assert_eq!(
        (n, isum, fsum),
        (2051, 61_628_659_163, 834_505_481_606_287_740)
    );
    let p = first.unwrap();
    assert_eq!(
        (
            p.id,
            p.unknown,
            p.emitter_type,
            p.particle_type,
            p.gfx_obj_id,
            p.hw_gfx_obj_id
        ),
        (0x3200_0004, 0, 2, 3, 0x0100_0FB3, 0x0100_0FC4)
    );
    assert_eq!(
        [p.birthrate, p.total_seconds, p.lifespan, p.lifespan_rand].map(f64::to_bits),
        [
            0x3FB9_9999_9999_999A,
            0x4024_0000_0000_0000,
            0x3FF0_0000_0000_0000,
            0
        ]
    );
    assert_eq!(
        (p.max_particles, p.initial_particles, p.total_particles),
        (20, 0, 0)
    );
    assert_eq!(
        [
            p.offset_dir.x,
            p.offset_dir.y,
            p.offset_dir.z,
            p.min_offset,
            p.max_offset,
            p.a.x,
            p.a.y,
            p.a.z,
            p.min_a,
            p.max_a,
            p.b.z,
            p.min_b,
            p.max_b,
            p.c.z,
            p.min_c,
            p.max_c
        ]
        .map(f32::to_bits),
        [
            0,
            0x3F80_0000,
            0,
            0,
            0x3DCC_CCCD,
            0,
            0x3F80_0000,
            0x3F80_0000,
            0x3DCC_CCCD,
            0x3E4C_CCCD,
            0xC000_0000,
            0x3F80_0000,
            0x3F80_0000,
            0,
            0x3F80_0000,
            0x3F80_0000
        ]
    );
    assert_eq!(
        [
            p.start_scale,
            p.final_scale,
            p.scale_rand,
            p.start_trans,
            p.final_trans,
            p.trans_rand
        ]
        .map(f32::to_bits),
        [0x3F00_0000, 0x3DCC_CCCD, 0, 0, 0x3F80_0000, 0]
    );
    assert!(!p.is_parent_local);
}

#[test]
#[ignore = "needs the retail dat files"]
fn combat_tables_decode_to_the_golden_totals() {
    let d = dats();
    let (mut maneuvers, mut isum) = (0u64, 0u64);
    let mut first = None;
    let n = sweep::<CombatTable>(&d.portal, 0x3000_0000, 0x3000_FFFF, |_, t| {
        maneuvers += t.combat_maneuvers.len() as u64;
        for m in &t.combat_maneuvers {
            isum += u64::from(
                m.style
                    .wrapping_add(m.attack_height as u32)
                    .wrapping_add(m.attack_type as u32)
                    .wrapping_add(m.min_skill_level)
                    .wrapping_add(m.motion),
            );
        }
        first.get_or_insert(t);
    });
    assert_eq!((n, maneuvers, isum), (71, 886, 2_140_505_049_268));
    let t = first.unwrap();
    assert_eq!((t.id, t.combat_maneuvers.len()), (0x3000_0000, 102));
    assert_eq!(
        t.combat_maneuvers[0],
        CombatManeuver {
            style: 0x8000_003C,
            attack_height: 1,
            attack_type: 8,
            min_skill_level: 0,
            motion: 0x1000_0068
        }
    );
}

#[test]
#[ignore = "needs the retail dat files"]
fn spell_tables_decode_to_the_golden_totals() {
    let d = dats();
    let bytes = d.portal.get_bytes(SPELL_TABLE_ID).unwrap().unwrap();
    let r = &mut Reader::new(&bytes);
    let t = SpellTable::unpack(r).unwrap();
    assert_eq!(r.remaining(), 0);
    let (mut comps, mut enchantments, mut portals, mut non_ascii) = (0u64, 0u64, 0u64, 0u64);
    let (mut isum, mut fsum) = (0u64, 0u64);
    for (key, s) in &t.spells {
        isum += u64::from(
            key.wrapping_add(s.school as u32)
                .wrapping_add(s.icon)
                .wrapping_add(s.category)
                .wrapping_add(s.bitfield)
                .wrapping_add(s.base_mana)
                .wrapping_add(s.power)
                .wrapping_add(s.formula_version)
                .wrapping_add(s.meta_spell_type)
                .wrapping_add(s.meta_spell_id)
                .wrapping_add(s.caster_effect)
                .wrapping_add(s.target_effect)
                .wrapping_add(s.fizzle_effect)
                .wrapping_add(s.display_order)
                .wrapping_add(s.non_component_target_type)
                .wrapping_add(s.mana_mod),
        );
        for c in s.components().into_iter().filter(|&c| c != 0) {
            comps += 1;
            isum += u64::from(c);
        }
        fsum = fsum
            .wrapping_add(bits(s.base_range_constant))
            .wrapping_add(bits(s.component_loss))
            .wrapping_add(bits(s.recovery_amount))
            .wrapping_add(dbits(s.recovery_interval))
            .wrapping_add(dbits(s.duration))
            .wrapping_add(dbits(s.portal_lifetime))
            .wrapping_add(bits(s.degrade_limit));
        enchantments += u64::from(s.meta_spell_type == 1 || s.meta_spell_type == 12);
        portals += u64::from(s.meta_spell_type == 7);
        non_ascii += u64::from(!s.name.is_ascii() || !s.description.is_ascii());
    }
    let tiers: usize = t.spell_sets.iter().map(|(_, s)| s.tiers.len()).sum();
    let tier_spells: usize = t
        .spell_sets
        .iter()
        .flat_map(|(_, s)| &s.tiers)
        .map(|(_, spells)| spells.len())
        .sum();
    assert_eq!(
        (
            t.spells.len(),
            comps,
            enchantments,
            portals,
            non_ascii,
            t.spell_sets.len(),
            tiers,
            tier_spells
        ),
        (6266, 43_455, 4375, 11, 11, 139, 902, 4013)
    );
    assert_eq!((isum, fsum), (631_186_387_752, 935_453_988_072_201_134));

    let s = &t.spells.iter().find(|(k, _)| *k == 1).unwrap().1;
    assert_eq!(s.name, "Strength Other I");
    assert_eq!(
        s.description,
        "Increases the target's Strength by 10 points."
    );
    assert_eq!(
        (s.school, s.icon, s.category, s.bitfield, s.base_mana),
        (4, 0x0600_138C, 1, 6, 10)
    );
    assert_eq!(
        [
            s.base_range_constant,
            s.base_range_mod,
            s.spell_economy_mod,
            s.component_loss,
            s.degrade_modifier,
            s.degrade_limit,
            s.recovery_amount
        ]
        .map(f32::to_bits),
        [0x40A0_0000, 0x3F80_0000, 0x3F80_0000, 0, 0, 0xC426_8000, 0]
    );
    assert_eq!(
        (
            s.power,
            s.formula_version,
            s.meta_spell_type,
            s.meta_spell_id
        ),
        (1, 1, 1, 1)
    );
    assert_eq!(s.duration.to_bits(), 0x409C_2000_0000_0000);
    assert_eq!(s.components(), [1, 7, 33, 44, 49, 0, 0, 0]);
    assert_eq!(
        (
            s.caster_effect,
            s.target_effect,
            s.fizzle_effect,
            s.recovery_interval,
            s.display_order,
            s.non_component_target_type,
            s.mana_mod
        ),
        (0, 6, 0, 0.0, 10_412, 16, 0)
    );
    let (key, s) = t
        .spells
        .iter()
        .find(|(_, s)| s.meta_spell_type == 7)
        .unwrap();
    assert_eq!((*key, s.name.as_str()), (157, "Summon Primary Portal I"));
    assert_eq!(s.portal_lifetime.to_bits(), 0x404E_0000_0000_0000);
    assert_eq!(s.components(), [4, 69, 24, 66, 32, 42, 60, 0]);
    // Names with Windows-1252 punctuation unscramble the same way.
    let (key, s) = t.spells.iter().find(|(_, s)| !s.name.is_ascii()).unwrap();
    assert_eq!(
        (*key, s.name.as_str()),
        (3810, "Asheron\u{2019}s Benediction")
    );
    assert_eq!(s.components(), [1, 2, 8, 73, 26, 111, 66, 50]);
    let (key, set) = &t.spell_sets[0];
    assert_eq!((*key, set.tiers.len()), (138, 50));
    assert_eq!(set.tiers[0], (1, vec![6221, 6226, 6251, 6231]));

    let bytes = d
        .portal
        .get_bytes(SPELL_COMPONENT_TABLE_ID)
        .unwrap()
        .unwrap();
    let r = &mut Reader::new(&bytes);
    let t = SpellComponentTable::unpack(r).unwrap();
    assert_eq!(r.remaining(), 0);
    let sum: u64 = t
        .components
        .iter()
        .map(|(key, c)| {
            u64::from(
                key.wrapping_add(c.category)
                    .wrapping_add(c.icon)
                    .wrapping_add(c.component_type)
                    .wrapping_add(c.gesture),
            ) + bits(c.time)
                + bits(c.cdm)
        })
        .sum();
    assert_eq!((t.components.len(), sum), (163, 515_209_282_459));
    let (key, c) = &t.components[0];
    assert_eq!(
        (*key, c.name.as_str(), c.text.as_str()),
        (1, "Lead Scarab", "")
    );
    assert_eq!(
        (
            c.category,
            c.icon,
            c.component_type,
            c.gesture,
            c.time.to_bits(),
            c.cdm.to_bits()
        ),
        (0, 0x0600_13E7, 1, 0x8000_0000, 0, 0x3DCC_CCCD)
    );
}

#[test]
#[ignore = "needs the retail dat files"]
fn string_tables_decode_to_the_golden_totals() {
    let d = dats();
    let (mut entries, mut strings, mut vars, mut chars, mut isum) = (0u64, 0u64, 0u64, 0u64, 0u64);
    let mut first = None;
    let n = sweep::<StringTable>(&d.local, 0x2300_0000, 0x24FF_FFFF, |_, t| {
        entries += t.strings.len() as u64;
        isum += u64::from(t.language);
        for (key, s) in &t.strings {
            isum += u64::from(
                key.wrapping_add(s.data_id)
                    .wrapping_add(u32::from(s.is_var_name_table_worth_packing)),
            );
            strings += s.strings.len() as u64;
            vars += s.variables.len() as u64;
            isum += s.variables.iter().map(|&v| u64::from(v)).sum::<u64>();
            for text in &s.strings {
                for unit in text.encode_utf16() {
                    chars += 1;
                    isum += u64::from(unit);
                }
            }
        }
        first.get_or_insert(t);
    });
    assert_eq!(
        (n, entries, strings, vars, chars, isum),
        (15, 6899, 7050, 151, 154_546, 1_079_444_735_016)
    );
    let t = first.unwrap();
    assert_eq!((t.id, t.language, t.strings.len()), (0x2300_0001, 1, 502));
    let (key, s) = &t.strings[0];
    assert_eq!(*key, 0x052B_A517);
    assert_eq!(
        s,
        &StringTableString {
            data_id: 0,
            strings: vec!["Low".to_owned()],
            variables: vec![],
            is_var_name_table_worth_packing: false
        }
    );
}

#[test]
#[ignore = "needs the retail dat files"]
fn char_gen_matches_the_goldens() {
    let d = dats();
    let bytes = d.portal.get_bytes(CHAR_GEN_ID).unwrap().unwrap();
    let r = &mut Reader::new(&bytes);
    let c = CharGen::unpack(r).unwrap();
    assert_eq!(r.remaining(), 0);
    assert_eq!(
        (c.data_id, c.starting_areas.len(), c.heritage_groups.len()),
        (CHAR_GEN_ID, 5, 13)
    );
    let areas: Vec<_> = c
        .starting_areas
        .iter()
        .map(|a| {
            (
                a.name.as_str(),
                a.locations.len(),
                a.locations[0].cell_id,
                a.locations[0].frame.origin.x.to_bits(),
                a.locations[0].frame.orientation.w.to_bits(),
            )
        })
        .collect();
    assert_eq!(
        areas,
        [
            ("Holtburg", 5, 0x8602_01AD, 0x4145_1E4F, 0x3EAD_8A54),
            ("Shoushi", 5, 0x7F03_01AD, 0x4145_1E4F, 0x3EAD_8A54),
            ("Yaraq", 5, 0x8C04_01AD, 0x4145_1E4F, 0x3EAD_8A54),
            ("Sanamar", 5, 0x7202_01AD, 0x4145_1E4F, 0x3EAD_8A54),
            ("OlthoiLair", 1, 0xE6D3_001E, 0x42B4_B6D1, 0xBF3C_4418)
        ]
    );
    let names: Vec<(u32, &str)> = c
        .heritage_groups
        .iter()
        .map(|(k, h)| (*k, h.name.as_str()))
        .collect();
    assert_eq!(names[0], (1, "Aluvian"));
    assert_eq!(names[1], (2, "Gharu'ndim"));
    assert_eq!(names[12], (13, "OlthoiAcid"));
    let h = &c.heritage_groups[1].1;
    assert_eq!(
        (
            h.icon_id,
            h.setup_id,
            h.environment_setup_id,
            h.attribute_credits,
            h.skill_credits
        ),
        (0x0600_04C8, 0x0200_0054, 0x0200_05A4, 330, 52)
    );
    assert_eq!(
        (&h.primary_start_areas[..], &h.secondary_start_areas[..]),
        (&[2][..], &[0, 1, 3][..])
    );
    let olthoi = &c.heritage_groups[11].1;
    assert_eq!(
        (
            olthoi.attribute_credits,
            olthoi.skill_credits,
            olthoi.templates.len()
        ),
        (60, 68, 1)
    );
    assert!(olthoi.secondary_start_areas.is_empty());

    let h = &c.heritage_groups[0].1;
    assert_eq!(
        (h.skills.len(), h.templates.len(), h.genders.len()),
        (1, 7, 2)
    );
    assert_eq!(
        h.skills[0],
        SkillCg {
            id: 14,
            normal_cost: 0,
            primary_cost: 2
        }
    );
    let t = &h.templates[0];
    assert_eq!(
        (t.name.as_str(), t.icon_id, t.title),
        ("Adventurer", 0x0600_1152, 1)
    );
    assert_eq!(
        [
            t.strength,
            t.endurance,
            t.coordination,
            t.quickness,
            t.focus,
            t.self_
        ],
        [10; 6]
    );
    assert!(t.normal_skills.is_empty() && t.primary_skills.is_empty());
    let (key, s) = &h.genders[0];
    assert_eq!((*key, s.name.as_str(), s.scale), (1, "Male", 100));
    assert_eq!(
        [
            s.setup_id,
            s.sound_table,
            s.icon_id,
            s.base_palette,
            s.skin_pal_set,
            s.physics_table,
            s.motion_table,
            s.combat_table
        ],
        [
            0x0200_0001,
            0x2000_0001,
            0x0600_04DA,
            0x0400_007E,
            0x0F00_0013,
            0x3400_0004,
            0x0900_0001,
            0x3000_0000
        ]
    );
    let od = &s.base_obj_desc;
    assert_eq!(
        (
            od.version,
            od.palette_id,
            od.sub_palettes.len(),
            od.texture_changes.len(),
            od.anim_part_changes.len()
        ),
        (17, 0, 0, 0, 17)
    );
    assert_eq!(
        od.anim_part_changes[0],
        AnimationPartChange {
            part_index: 0,
            part_id: 0x0100_004E
        }
    );
    assert_eq!(
        [
            s.hair_colors.len(),
            s.hair_styles.len(),
            s.eye_colors.len(),
            s.eye_strips.len(),
            s.nose_strips.len(),
            s.mouth_strips.len(),
            s.headgears.len(),
            s.shirts.len(),
            s.pants.len(),
            s.footwear.len(),
            s.clothing_colors.len()
        ],
        [4, 51, 8, 19, 20, 42, 2, 6, 3, 2, 9]
    );
    let hair = &s.hair_styles[0];
    assert_eq!(
        (
            hair.icon_id,
            hair.bald,
            hair.alternate_setup,
            hair.obj_desc.anim_part_changes.len(),
            hair.obj_desc.texture_changes.len()
        ),
        (0x0600_0F54, true, 0, 1, 1)
    );
    let eye = &s.eye_strips[0];
    assert_eq!(
        (
            eye.icon_id,
            eye.bald_icon_id,
            eye.obj_desc.texture_changes.len(),
            eye.bald_obj_desc.texture_changes.len()
        ),
        (0, 0, 1, 1)
    );
    assert_eq!(
        s.shirts[0],
        GearCg {
            name: "Shirt".to_owned(),
            clothing_table: 0x1000_0001,
            weenie_default: 130
        }
    );
    assert_eq!((s.hair_colors[0], s.clothing_colors[0]), (0x0F00_0B50, 9));

    let (mut genders, mut hair, mut parts, mut textures, mut sub_palettes) = (0, 0, 0, 0, 0);
    let mut count = |o: &ObjDesc| {
        parts += o.anim_part_changes.len();
        textures += o.texture_changes.len();
        sub_palettes += o.sub_palettes.len();
    };
    for (_, h) in &c.heritage_groups {
        for (_, s) in &h.genders {
            genders += 1;
            hair += s.hair_styles.len();
            count(&s.base_obj_desc);
            s.hair_styles.iter().for_each(|x| count(&x.obj_desc));
            for e in &s.eye_strips {
                count(&e.obj_desc);
                count(&e.bald_obj_desc);
            }
            s.nose_strips.iter().for_each(|x| count(&x.obj_desc));
            s.mouth_strips.iter().for_each(|x| count(&x.obj_desc));
        }
    }
    assert_eq!(
        (genders, hair, parts, textures, sub_palettes),
        (26, 869, 1693, 2786, 0)
    );
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_skill_table_decodes_to_its_last_byte() {
    let d = dats();
    let bytes = d.portal.get_bytes(SKILL_TABLE_ID).unwrap().unwrap();
    let r = &mut Reader::new(&bytes);
    let table = SkillTable::unpack(r).unwrap();
    assert_eq!(r.remaining(), 0);
    // The retired weapon skills are not in the table (a server adds them
    // back itself).
    assert!(table.skills.len() > 30, "{} skills", table.skills.len());
    assert!(
        table.skills.iter().all(|(id, _)| *id != 1),
        "Axe is retired"
    );
    for (id, skill) in &table.skills {
        assert!(!skill.name.is_empty(), "skill {id} has no name");
        // Every formula names its attributes among the six primaries.
        assert!(skill.formula.attribute1 <= 6 && skill.formula.attribute2 <= 6);
        assert!((1..=3).contains(&skill.min_level), "skill {id}");
    }
    let run = table.skills.iter().find(|(id, _)| *id == 24).unwrap();
    assert_eq!(run.1.name, "Run");
    // Run is Quickness alone, usable untrained.
    assert_eq!(
        (
            run.1.formula.attribute1,
            run.1.formula.attribute1_multiplier,
            run.1.formula.attribute2_multiplier,
            run.1.formula.divisor,
            run.1.min_level
        ),
        (3, 1, 0, 1, 1)
    );
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_experience_table_matches_the_goldens() {
    // The end-of-retail dats' experience table.
    let d = dats();
    let bytes = d.portal.get_bytes(EXPERIENCE_TABLE_ID).unwrap().unwrap();
    let r = &mut Reader::new(&bytes);
    let t = ExperienceTable::unpack(r).unwrap();
    assert_eq!(r.remaining(), 0);
    let ends = |v: &[u32]| (v.len(), v[0], *v.last().unwrap());
    assert_eq!(ends(&t.attributes), (191, 0, 4_019_438_644));
    assert_eq!(ends(&t.vitals), (197, 0, 4_285_430_197));
    assert_eq!(ends(&t.trained_skills), (209, 0, 4_203_819_496));
    assert_eq!(ends(&t.specialized_skills), (227, 0, 4_100_490_438));
    assert_eq!(t.levels.len(), 276);
    assert_eq!(
        (
            t.levels[0],
            t.levels[1],
            t.levels[2],
            *t.levels.last().unwrap()
        ),
        (0, 0, 1000, 191_226_310_247)
    );
    assert_eq!(ends(&t.skill_credits), (276, 0, 1));
    assert_eq!(t.skill_credits[1], 0);
    let typed: ExperienceTable = d.get(EXPERIENCE_TABLE_ID).unwrap().unwrap();
    assert_eq!(typed, t);
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_action_map_and_master_input_maps_decode_with_the_pinned_shape() {
    // OpenAC `RetailActionMapReader_LiveDatTests.Read_AgainstInstalledDats_
    // MatchesPinnedShape` (the action map and the master input map):
    // 306 player-bindable actions (14 movement, 22 camera, 103 UI, 32
    // combat, 87 emote, 48 character settings); move forward defaults to
    // DIK_W and DIK_UPARROW across the two master maps.
    use ac_dat::objects::{
        ACTION_MAP_ID, ActionMap, GAMEPLAY_MASTER_INPUT_MAP_ID, MasterInputMap,
        SYSTEM_MASTER_INPUT_MAP_ID,
    };
    let d = dats();
    let bytes = d.portal.get_bytes(ACTION_MAP_ID).unwrap().unwrap();
    let r = &mut Reader::new(&bytes);
    let map = ActionMap::unpack(r).unwrap();
    assert_eq!(r.remaining(), 0);
    let mut by_class: BTreeMap<u32, usize> = BTreeMap::new();
    for (_, actions) in &map.input_maps {
        for (_, value) in actions {
            let class = value.user_binding.action_class;
            if class != 0 {
                *by_class.entry(class).or_default() += 1;
            }
        }
    }
    assert_eq!(by_class.values().sum::<usize>(), 306);
    assert_eq!(
        by_class,
        BTreeMap::from([(1, 14), (2, 22), (3, 103), (4, 32), (5, 87), (7, 48)])
    );
    let mut forward_scans = Vec::new();
    for id in [GAMEPLAY_MASTER_INPUT_MAP_ID, SYSTEM_MASTER_INPUT_MAP_ID] {
        let bytes = d.portal.get_bytes(id).unwrap().unwrap();
        let r = &mut Reader::new(&bytes);
        let master = MasterInputMap::unpack(r).unwrap();
        assert_eq!(r.remaining(), 0, "{id:#010x} has trailing bytes");
        let typed: MasterInputMap = d.get(id).unwrap().unwrap();
        assert_eq!(typed, master);
        for (input_map, controls) in &master.input_maps {
            if *input_map == 4 {
                forward_scans.extend(
                    controls
                        .iter()
                        .filter(|c| c.unknown == 0x29)
                        .map(|c| c.key.key >> 16),
                );
            }
        }
    }
    forward_scans.sort_unstable();
    assert_eq!(forward_scans, [0x11, 0xC8]);
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_taboo_table_decodes_to_its_last_byte() {
    // The end-of-retail dats' taboo table: 32 audiences.
    use ac_dat::objects::{CHAT_AUDIENCE, TABOO_TABLE_ID, TabooTable};
    let d = dats();
    let bytes = d.portal.get_bytes(TABOO_TABLE_ID).unwrap().unwrap();
    let r = &mut Reader::new(&bytes);
    let table = TabooTable::unpack(r).unwrap();
    assert_eq!(r.remaining(), 0);
    assert_eq!(table.id, TABOO_TABLE_ID);
    assert_eq!(table.audiences.len(), 32);
    let chat = table.patterns(CHAT_AUDIENCE).expect("the chat audience");
    assert!(!chat.is_empty());
    assert!(chat.iter().all(|p| !p.is_empty() && p.is_ascii()));
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_chat_pose_table_decodes_to_its_last_byte() {
    // The end-of-retail dats' chat pose table: 309 poses, 74 emote lines,
    // and the entries it names.
    use ac_dat::objects::{CHAT_POSE_TABLE_ID, ChatPoseTable};
    let d = dats();
    let bytes = d.portal.get_bytes(CHAT_POSE_TABLE_ID).unwrap().unwrap();
    let r = &mut Reader::new(&bytes);
    let table = ChatPoseTable::unpack(r).unwrap();
    assert_eq!(r.remaining(), 0);
    assert_eq!(table.id, CHAT_POSE_TABLE_ID);
    assert_eq!(table.chat_poses.len(), 309);
    assert_eq!(table.chat_emotes.len(), 74);
    let pose = |word: &str| {
        table
            .chat_poses
            .iter()
            .find(|(w, _)| w == word)
            .map(|(_, m)| m.as_str())
    };
    assert_eq!(pose("waving"), Some("WAVESTATE"));
    assert_eq!(pose("kisses"), Some("BlowKiss"));
    let emote = |motion: &str| {
        table
            .chat_emotes
            .iter()
            .find(|(m, _)| m == motion)
            .map(|(_, e)| (e.my_emote.as_str(), e.other_emote.as_str()))
    };
    assert_eq!(
        emote("Cringe"),
        Some(("cringe in terror.", "cringes in terror."))
    );
    assert_eq!(
        emote("ATOYOT"),
        Some((
            "experience quite the feeling!",
            "experiences quite the feeling!"
        ))
    );
}
