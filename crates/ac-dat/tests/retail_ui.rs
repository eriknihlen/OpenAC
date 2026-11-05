//! Conformance of the UI objects (master property table, layouts, fonts,
//! enum mappers) against the retail dat files.
//!
//! Golden values read from the same dat files by an independent reader (a
//! throwaway console program); sums wrap at 64
//! bits as the C# `unchecked` sums do. Floats are summed by bit pattern.
//! Every sweep also proves each object decodes to its last byte. These tests
//! need the dats: set `OPENAC_DAT_DIR` or install them under
//! `Documents\Asheron's Call`; run with `cargo test -- --include-ignored`.

use std::collections::{BTreeMap, HashMap};
use std::path::PathBuf;

use ac_bits::Reader;
use ac_core::names::{CreatureDisplayNames, EnumMapping};
use ac_dat::objects::{
    DatObject, ElementDesc, EnumMapper, Font, LayoutDesc, MASTER_PROPERTY_ID, MasterProperty,
    Media, Property, PropertyValue, RenderSurface, StateDesc, ui_state_name,
};
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

fn master(d: &DatCollection) -> MasterProperty {
    d.get(MASTER_PROPERTY_ID)
        .unwrap()
        .expect("master property table")
}

fn bits(v: f32) -> u64 {
    u64::from(v.to_bits())
}

/// Decode every file whose id is in `lo..=hi`, checking that each one
/// decodes to its last byte.
fn sweep<T: DatObject>(dat: &DatFile, lo: u32, hi: u32, mut each: impl FnMut(u32, T)) -> usize {
    let mut n = 0;
    for entry in dat.entries() {
        let entry = entry.unwrap();
        if !(lo..=hi).contains(&entry.id) {
            continue;
        }
        let bytes = dat.read_entry(&entry).unwrap();
        let r = &mut Reader::new(&bytes);
        let object = T::unpack(r).unwrap_or_else(|e| panic!("{:#010x}: {e}", entry.id));
        assert_eq!(r.remaining(), 0, "{:#010x} has trailing bytes", entry.id);
        each(entry.id, object);
        n += 1;
    }
    n
}

/// The C# program's per-value number: the value itself, a bool as 0/1, a
/// float or vector by bit pattern, a colour packed blue-low.
fn scalar(v: &PropertyValue) -> u64 {
    match v {
        PropertyValue::Enum(x)
        | PropertyValue::DataId(x)
        | PropertyValue::Bitfield32(x)
        | PropertyValue::InstanceId(x) => u64::from(*x),
        PropertyValue::Bool(b) => u64::from(*b),
        PropertyValue::Float(f) => bits(*f),
        PropertyValue::Integer(i) => u64::from(*i as u32),
        PropertyValue::StringInfo(s) => {
            u64::from(s.token)
                + u64::from(s.string_id)
                + u64::from(s.table_id)
                + u64::from(s.override_flag)
                + u64::from(s.english)
                + u64::from(s.comment)
        }
        PropertyValue::Color(c) => {
            u64::from(c.blue)
                | (u64::from(c.green) << 8)
                | (u64::from(c.red) << 16)
                | (u64::from(c.alpha) << 24)
        }
        PropertyValue::Vector(v) => bits(v.x) + bits(v.y) + bits(v.z),
        PropertyValue::Bitfield64(x) => *x,
        PropertyValue::Array(_) | PropertyValue::Struct(_) => 0,
    }
}

fn kind_number(v: &PropertyValue) -> u32 {
    match v {
        PropertyValue::Bool(_) => 1,
        PropertyValue::Integer(_) => 2,
        PropertyValue::Float(_) => 4,
        PropertyValue::Vector(_) => 5,
        PropertyValue::Color(_) => 6,
        PropertyValue::StringInfo(_) => 8,
        PropertyValue::Enum(_) => 9,
        PropertyValue::DataId(_) => 10,
        PropertyValue::InstanceId(_) => 12,
        PropertyValue::Bitfield32(_) => 15,
        PropertyValue::Bitfield64(_) => 16,
        PropertyValue::Array(_) => 17,
        PropertyValue::Struct(_) => 18,
    }
}

#[test]
#[ignore = "needs the retail dat files"]
fn master_property_table_matches_the_goldens() {
    let d = dats();
    let bytes = d.portal.get_bytes(MASTER_PROPERTY_ID).unwrap().unwrap();
    let r = &mut Reader::new(&bytes);
    let m = MasterProperty::unpack(r).unwrap();
    assert_eq!(r.remaining(), 0);
    let (mut keys, mut names, mut data, mut pt, mut flags) = (0u64, 0u64, 0u64, 0u64, 0u64);
    let (mut avail, mut avail_sum, mut defs, mut maxs, mut mins, mut def_sum) =
        (0u64, 0u64, 0u64, 0u64, 0u64, 0u64);
    let mut types = BTreeMap::new();
    for (k, p) in &m.properties {
        keys = keys.wrapping_add(u64::from(*k));
        names = names.wrapping_add(u64::from(p.name));
        data = data.wrapping_add(u64::from(p.data));
        pt = pt.wrapping_add(bits(p.prediction_timeout));
        let bools = [
            p.required,
            p.read_only,
            p.no_checkpoint,
            p.recorded,
            p.do_not_replay,
            p.absolute_time_stamp,
            p.groupable,
            p.propagate_to_children,
        ];
        let mut f = u64::from(p.inheritance_type)
            + u64::from(p.dat_file_type)
            + u64::from(p.propagation_type)
            + u64::from(p.caching_type);
        for (i, b) in bools.iter().enumerate() {
            f += u64::from(*b) << i;
        }
        f = f
            .wrapping_add(u64::from(p.patch_flags as u32))
            .wrapping_add(u64::from(p.group))
            .wrapping_add(u64::from(p.provider));
        flags = flags.wrapping_add(f);
        *types.entry(p.kind.0).or_insert(0) += 1;
        for (a, b) in &p.available_properties {
            avail += 1;
            avail_sum = avail_sum.wrapping_add(u64::from(*a) + u64::from(*b));
        }
        for (count, value) in [
            (&mut defs, &p.default_value),
            (&mut maxs, &p.max_value),
            (&mut mins, &p.min_value),
        ] {
            if let Some(v) = value {
                *count += 1;
                def_sum = def_sum.wrapping_add(scalar(v));
            }
        }
    }
    assert_eq!(m.properties.len(), 383);
    assert_eq!(
        (keys, names, data, pt, flags),
        (
            29_259_512_914,
            29_259_512_914,
            40_063_994_837,
            409_636_700_160,
            25_769_812_553
        )
    );
    assert_eq!(
        (avail, avail_sum, defs, maxs, mins, def_sum),
        (47, 9_663_687_574, 102, 9, 9, 79_808_255_970)
    );
    assert_eq!(
        types.into_iter().collect::<Vec<_>>(),
        [
            (1, 126),
            (2, 60),
            (4, 31),
            (5, 1),
            (6, 14),
            (7, 7),
            (8, 20),
            (9, 65),
            (10, 19),
            (12, 8),
            (15, 3),
            (16, 1),
            (17, 21),
            (18, 7)
        ]
    );
    let em = &m.enum_mapper;
    let em_keys: u64 = em.id_to_string.iter().map(|(k, _)| u64::from(*k)).sum();
    let em_len: usize = em.id_to_string.iter().map(|(_, s)| s.chars().count()).sum();
    assert_eq!(
        (
            em.base_enum_map,
            em.unknown,
            em.id_to_string.len(),
            em_keys,
            em_len
        ),
        (0, 0, 384, 29_259_512_914, 9443)
    );
    assert_eq!(m.property_type(0x14).unwrap().0, 9);
    assert_eq!(m.property_type(0x1A).unwrap().0, 17);
    assert_eq!(m.property_type(0x2E).unwrap().0, 17);
}

#[derive(Default)]
struct LayoutSums {
    elements: u64,
    id: u64,
    kind: u64,
    geo: u64,
    read_order: u64,
    edges: u64,
    base: u64,
    default_state: u64,
    states: u64,
    state_id: u64,
    pass: u64,
    incorporation: u64,
    props: u64,
    prop_keys: u64,
    prop_scalar: u64,
    kinds: BTreeMap<u32, u64>,
    media: BTreeMap<i32, u64>,
    media_sum: u64,
    declared: u64,
    state_names: BTreeMap<String, u64>,
}

impl LayoutSums {
    fn prop(&mut self, p: &Property) {
        self.props += 1;
        *self.kinds.entry(kind_number(&p.value)).or_insert(0) += 1;
        self.prop_scalar = self.prop_scalar.wrapping_add(scalar(&p.value));
        self.prop_keys = self.prop_keys.wrapping_add(u64::from(p.master_property_id));
        match &p.value {
            PropertyValue::Array(items) => items.iter().for_each(|x| self.prop(x)),
            PropertyValue::Struct(members) => {
                for (k, x) in members {
                    self.prop_keys = self.prop_keys.wrapping_add(u64::from(*k));
                    self.prop(x);
                }
            }
            _ => {}
        }
    }

    fn state(&mut self, s: &StateDesc) {
        self.state_id = self.state_id.wrapping_add(u64::from(s.state_id));
        self.incorporation = self
            .incorporation
            .wrapping_add(u64::from(s.incorporation_flags.0));
        self.pass += u64::from(s.pass_to_children);
        for (k, p) in &s.properties {
            self.prop_keys = self.prop_keys.wrapping_add(u64::from(*k));
            self.prop(p);
        }
        for m in &s.media {
            *self.media.entry(m.media_type()).or_insert(0) += 1;
            self.declared = self
                .declared
                .wrapping_add(u64::from(m.declared_type as u32));
            let add = match &m.media {
                Media::Image { file, draw_mode } => u64::from(*file) + u64::from(draw_mode.0),
                Media::Animation {
                    duration,
                    draw_mode,
                    frames,
                } => {
                    bits(*duration)
                        + u64::from(draw_mode.0)
                        + frames.len() as u64
                        + frames.iter().map(|f| u64::from(*f)).sum::<u64>()
                }
                Media::Cursor {
                    file,
                    x_hotspot,
                    y_hotspot,
                } => u64::from(*file) + u64::from(*x_hotspot) + u64::from(*y_hotspot),
                Media::Jump {
                    jump_item_index,
                    probability,
                } => u64::from(*jump_item_index) + bits(*probability),
                Media::Message { id, probability } => u64::from(*id) + bits(*probability),
                Media::Pause {
                    min_duration,
                    max_duration,
                } => bits(*min_duration) + bits(*max_duration),
                Media::Sound { file, sound } => u64::from(*file) + u64::from(*sound),
                Media::State {
                    state_id,
                    probability,
                } => u64::from(*state_id) + bits(*probability),
                Media::Fade {
                    start_alpha,
                    end_alpha,
                    duration,
                } => bits(*start_alpha) + bits(*end_alpha) + bits(*duration),
                Media::Alpha { file } => u64::from(*file),
                Media::Movie {
                    file_name,
                    stretch_to_full_screen,
                } => file_name.chars().count() as u64 + u64::from(*stretch_to_full_screen),
            };
            self.media_sum = self.media_sum.wrapping_add(add);
        }
    }

    fn element(&mut self, e: &ElementDesc) {
        self.elements += 1;
        self.id = self.id.wrapping_add(u64::from(e.element_id));
        self.kind = self.kind.wrapping_add(u64::from(e.element_type));
        self.geo = self.geo.wrapping_add(
            u64::from(e.x as u32)
                + u64::from(e.y as u32)
                + u64::from(e.width as u32)
                + u64::from(e.height as u32)
                + u64::from(e.z_level as u32),
        );
        self.read_order += u64::from(e.read_order);
        self.edges += u64::from(e.left_edge)
            + 3 * u64::from(e.top_edge)
            + 5 * u64::from(e.right_edge)
            + 7 * u64::from(e.bottom_edge);
        self.base = self
            .base
            .wrapping_add(u64::from(e.base_element) + u64::from(e.base_layout_id));
        self.default_state = self.default_state.wrapping_add(u64::from(e.default_state));
        self.state(&e.state_desc);
        for (k, s) in &e.states {
            self.states += 1;
            self.state_id = self.state_id.wrapping_add(u64::from(*k));
            *self
                .state_names
                .entry(ui_state_name(*k).into_owned())
                .or_insert(0) += 1;
            self.state(s);
        }
        for (k, c) in &e.children {
            self.id = self.id.wrapping_add(u64::from(*k));
            self.element(c);
        }
    }
}

#[test]
#[ignore = "needs the retail dat files"]
fn every_layout_decodes_to_the_golden_totals() {
    let d = dats();
    let m = master(&d);
    let mut s = LayoutSums::default();
    let (mut n, mut w, mut h) = (0u64, 0u64, 0u64);
    for entry in d.local.entries() {
        let entry = entry.unwrap();
        if entry.id >> 24 != 0x21 {
            continue;
        }
        let bytes = d.local.read_entry(&entry).unwrap();
        let r = &mut Reader::new(&bytes);
        let l = LayoutDesc::unpack(r, &m).unwrap_or_else(|e| panic!("{:#010x}: {e}", entry.id));
        assert_eq!(r.remaining(), 0, "{:#010x} has trailing bytes", entry.id);
        n += 1;
        w += u64::from(l.width);
        h += u64::from(l.height);
        for (k, e) in &l.elements {
            s.id = s.id.wrapping_add(u64::from(*k));
            s.element(e);
        }
    }
    assert_eq!((n, w, h), (101, 80_800, 60_300));
    assert_eq!(
        (
            s.elements,
            s.id,
            s.kind,
            s.geo,
            s.read_order,
            s.edges,
            s.base,
            s.default_state
        ),
        (
            2162,
            1_116_694_828_916,
            26_306_682_892,
            42_950_552_325,
            13_155,
            29_493,
            894_427_940_847,
            17_448_306_726
        )
    );
    assert_eq!(
        (
            s.states,
            s.state_id,
            s.pass,
            s.incorporation,
            s.props,
            s.prop_keys,
            s.prop_scalar
        ),
        (
            1142,
            230_317_654_160,
            447,
            72_836,
            5033,
            169_920_142_591,
            2_233_431_598_630
        )
    );
    assert_eq!(
        s.kinds.into_iter().collect::<Vec<_>>(),
        [
            (1, 1221),
            (2, 661),
            (4, 37),
            (6, 311),
            (8, 465),
            (9, 1304),
            (10, 502),
            (17, 424),
            (18, 108)
        ]
    );
    assert_eq!(
        s.media.into_iter().collect::<Vec<_>>(),
        [
            (1, 2),
            (2, 3),
            (3, 2),
            (4, 101),
            (5, 1310),
            (6, 2),
            (7, 6),
            (8, 13),
            (9, 3),
            (10, 3),
            (11, 6)
        ]
    );
    assert_eq!((s.media_sum, s.declared), (198_654_955_304, 7249));
    let numeric: Vec<_> = s
        .state_names
        .iter()
        .filter(|(k, _)| k.starts_with(|c: char| c.is_ascii_digit()))
        .map(|(k, v)| (k.as_str(), *v))
        .collect();
    assert_eq!(
        numeric,
        [("268435458", 1), ("268435461", 1), ("268435486", 4)]
    );
    assert_eq!(s.state_names.len(), 105);
}

#[test]
#[ignore = "needs the retail dat files"]
fn chat_layout_top_elements() {
    let d = dats();
    let m = master(&d);
    let l = LayoutDesc::read(&d.local, &m, 0x2100_0006)
        .unwrap()
        .unwrap();
    assert_eq!(
        (l.id, l.width, l.height, l.elements.len()),
        (0x2100_0006, 800, 600, 4)
    );
    let tops: Vec<_> = l
        .elements
        .iter()
        .map(|(k, e)| {
            (
                *k,
                e.element_type,
                (e.x, e.y, e.width, e.height, e.z_level, e.read_order),
                (e.left_edge, e.top_edge, e.right_edge, e.bottom_edge),
                (e.children.len(), e.states.len()),
                (e.base_element, e.base_layout_id),
            )
        })
        .collect();
    assert_eq!(
        tops,
        [
            (
                0x1000_001C,
                3,
                (0, 0, 382, 104, 0, 1),
                (1, 2, 2, 1),
                (1, 0),
                (0, 0)
            ),
            (
                0x1000_0526,
                0,
                (2, 0, 16, 16, 0, 3),
                (1, 1, 2, 2),
                (0, 2),
                (0x1000_02B9, 0x2100_0040)
            ),
            (
                0x1000_001E,
                0,
                (0, 0, 191, 17, 0, 2),
                (0, 0, 0, 0),
                (0, 3),
                (0x1000_0377, 0x2100_003F)
            ),
            (
                0x1000_000E,
                0x1000_0041,
                (0, 0, 800, 100, 900, 0),
                (1, 2, 1, 1),
                (8, 0),
                (0, 0)
            ),
        ]
    );
}

#[test]
#[ignore = "needs the retail dat files"]
fn every_font_decodes_to_the_golden_totals() {
    let d = dats();
    let (mut glyphs, mut uni, mut off, mut wh, mut signed, mut metrics) =
        (0u64, 0u64, 0u64, 0u64, 0u64, 0u64);
    let n = sweep::<Font>(&d.portal, 0x4000_0000, 0x4000_0FFF, |_, f| {
        metrics = metrics.wrapping_add(
            u64::from(f.max_char_height)
                + 3 * u64::from(f.max_char_width)
                + 5 * u64::from(f.num_horizontal_border_pixels)
                + 7 * u64::from(f.num_vertical_border_pixels)
                + 11 * u64::from(f.baseline_offset)
                + u64::from(f.foreground_surface_data_id)
                + u64::from(f.background_surface_data_id),
        );
        for c in &f.char_descs {
            glyphs += 1;
            uni += u64::from(c.unicode);
            off += u64::from(c.offset_x) + u64::from(c.offset_y);
            wh += u64::from(c.width) + u64::from(c.height);
            let s = i64::from(c.horizontal_offset_before)
                + 3 * i64::from(c.horizontal_offset_after)
                + 5 * i64::from(c.vertical_offset_before);
            signed = signed.wrapping_add(s as u64);
        }
    });
    assert_eq!(
        (n, glyphs, uni, off, wh, signed, metrics),
        (
            49,
            153_749,
            4_779_316_113,
            267_836_462,
            3_528_859,
            2_817_763,
            8_659_153_602
        )
    );

    let f: Font = d.get(0x4000_0000).unwrap().unwrap();
    assert_eq!(
        (
            f.max_char_height,
            f.max_char_width,
            f.char_descs.len(),
            f.num_horizontal_border_pixels,
            f.num_vertical_border_pixels,
            f.baseline_offset,
            f.foreground_surface_data_id,
            f.background_surface_data_id
        ),
        (16, 16, 1050, 4, 4, 12, 0x0600_5EE5, 0x0600_5EE6)
    );
    let a = f
        .char_descs
        .iter()
        .find(|c| c.unicode == u16::from(b'A'))
        .unwrap();
    assert_eq!(
        (
            a.offset_x,
            a.offset_y,
            a.width,
            a.height,
            a.horizontal_offset_before,
            a.horizontal_offset_after,
            a.vertical_offset_before
        ),
        (405, 4, 9, 8, 0, 0, 4)
    );
    for atlas in [f.foreground_surface_data_id, f.background_surface_data_id] {
        let rs: RenderSurface = d.get(atlas).unwrap().unwrap();
        assert_eq!((rs.width, rs.height), (1024, 312));
    }
}

#[test]
#[ignore = "needs the retail dat files"]
fn every_enum_mapper_decodes_to_the_golden_totals() {
    let d = dats();
    let (mut names, mut keys, mut len, mut bases) = (0u64, 0u64, 0u64, 0u64);
    let mut all = HashMap::new();
    let n = sweep::<EnumMapper>(&d.portal, 0x2200_0000, 0x22FF_FFFF, |id, m| {
        bases = bases.wrapping_add(u64::from(m.base_enum_map));
        for (k, s) in &m.id_to_string {
            names += 1;
            keys = keys.wrapping_add(u64::from(*k));
            len += s.chars().count() as u64;
            assert!(s.is_ascii(), "{id:#010x}: non-ASCII name {s:?}");
        }
        all.insert(id, m);
    });
    assert_eq!(
        (n, names, keys, len, bases),
        (40, 3567, 583_312_257_143, 75_477, 5_133_828_516)
    );
    let creatures = &all[&0x2200_000E];
    assert_eq!(
        (creatures.base_enum_map, creatures.id_to_string.len()),
        (0, 102)
    );

    // The creature-kind names, read through ac-core's chain walker.
    let names = CreatureDisplayNames::load(|did| {
        all.get(&did).map(|m| EnumMapping {
            base_enum_map: m.base_enum_map,
            id_to_string: m.id_to_string.clone(),
        })
    });
    for (kind, name) in [
        (1, "Olthoi"),
        (2, "Banderling"),
        (19, "Virindi"),
        (20, "Wisp"),
        (31, "Human"),
        (50, "Idol"),
        (77, "Ghost"),
    ] {
        assert_eq!(names.resolve(kind), name);
    }
}
