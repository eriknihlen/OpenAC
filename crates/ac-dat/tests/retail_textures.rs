//! Texture decoding against the retail dat files.
//!
//! Golden values come from a throwaway console program that decodes the
//! same render surfaces the way OpenAC does (BCnEncoder.Net 2.2.1 for DXT, StbImageSharp 2.30.16 for JPEG, OpenAC's
//! own loops for the rest), with one exception: `CUSTOM_LSCAPE_R8G8B8`,
//! which OpenAC does not decode, is decoded like `R8G8B8` as retail does.
//! A texture's hash is 64-bit FNV-1a over its width and
//! height (u32, little-endian) and its RGBA bytes. These tests need the
//! dats: set `OPENAC_DAT_DIR` or install them under `Documents\Asheron's
//! Call`; run with `cargo test -- --include-ignored`.

use std::collections::BTreeMap;
use std::path::PathBuf;

use ac_dat::objects::{Palette, RenderSurface};
use ac_dat::texture::{self, DecodeOptions, DecodedTexture};
use ac_dat::{DatCollection, DatFile};

fn dats() -> DatCollection {
    let dir = std::env::var_os("OPENAC_DAT_DIR")
        .map(PathBuf::from)
        .or_else(ac_dat::default_dat_dir)
        .expect("no dat directory: set OPENAC_DAT_DIR");
    DatCollection::open(dir).expect("open the retail dats")
}

fn fnv(t: &DecodedTexture) -> u64 {
    let mut h = 0xCBF2_9CE4_8422_2325u64;
    let bytes = t
        .width
        .to_le_bytes()
        .into_iter()
        .chain(t.height.to_le_bytes())
        .chain(t.rgba.iter().copied());
    for b in bytes {
        h ^= u64::from(b);
        h = h.wrapping_mul(0x0100_0000_01B3);
    }
    h
}

/// Decode a render surface with its default palette.
fn decode(d: &DatCollection, rs: &RenderSurface, options: DecodeOptions) -> DecodedTexture {
    let palette: Option<Palette> = if rs.default_palette_id != 0 {
        d.get(rs.default_palette_id).unwrap()
    } else {
        None
    };
    texture::decode(rs, palette.as_ref(), options)
        .unwrap_or_else(|e| panic!("{:#010x}: {e}", rs.id))
}

/// Pixels at 0, 1, the middle-ish `w*h/2 + w/3`, and the last, as
/// `0xRRGGBBAA`.
fn samples(t: &DecodedTexture) -> [u32; 4] {
    let (w, h) = (t.width as usize, t.height as usize);
    [0, 1, w * h / 2 + w / 3, w * h - 1]
        .map(|i| u32::from_be_bytes(t.rgba[i * 4..i * 4 + 4].try_into().unwrap()))
}

#[test]
#[ignore = "needs the retail dat files"]
fn textures_match_the_reference_decoders() {
    let d = dats();
    let plain = DecodeOptions::default();
    // (id, width, height, hash, samples)
    let cases: [(u32, u32, u32, u64, [u32; 4]); 19] = [
        (
            0x0600_0133,
            20,
            20,
            0x8D1C_CAA5_8527_16BC,
            [0x0000_00FF, 0x0000_00FF, 0xF471_73FF, 0x0000_00FF],
        ),
        (
            0x0600_0FAA,
            32,
            32,
            0x762C_1D9F_83C1_8E8E,
            [0xFFFF_FF00, 0xFFFF_FF00, 0x9A5F_28FF, 0xFFFF_FF00],
        ),
        (
            0x0600_378B,
            256,
            256,
            0x4AE1_2E20_853E_F393,
            [0x3934_10FF, 0x3934_10FF, 0x4A79_29FF, 0x1820_08FF],
        ),
        (
            0x0600_6992,
            256,
            512,
            0xC950_4CD2_8DFC_6B51,
            [0x3C3C_3CEE, 0x3C3C_3CFF, 0x0000_0000, 0x3130_31EE],
        ),
        (
            0x0600_3789,
            512,
            512,
            0x4BAE_D9D2_82D0_56F7,
            [0x392C_31FF, 0x392C_31FF, 0x3630_2E00, 0x4762_2E00],
        ),
        (
            0x0600_378E,
            128,
            128,
            0xD992_11B8_AB8B_A695,
            [0x5F58_2CFF, 0x4A5E_2BFF, 0x424B_25FF, 0x1942_10FF],
        ),
        (
            0x0600_5CD8,
            256,
            256,
            0x12BB_E508_770B_D9C0,
            [0x6058_0AFF, 0x645B_21FF, 0x7068_1BFF, 0x716B_25FF],
        ),
        (
            0x0600_6402,
            256,
            256,
            0xE3DD_B87F_842C_B239,
            [0xC0C0_C0FF; 4],
        ),
        (
            0x0600_6430,
            64,
            64,
            0x5EFC_36F7_DBA7_8538,
            [0x0000_0011, 0x0000_0011, 0x0000_00FF, 0x0000_0011],
        ),
        (
            0x0600_5ECE,
            512,
            189,
            0x779C_C84C_DC07_FA9F,
            [0xFFFF_FF00, 0xFFFF_FF00, 0xFFFF_FF73, 0xFFFF_FF00],
        ),
        (
            0x0600_37D8,
            64,
            64,
            0xAD24_9C4D_099A_2EB3,
            [0xFFFF_FF00, 0xFFFF_FF00, 0xFFFF_FFFF, 0xFFFF_FFFF],
        ),
        (
            0x0600_3794,
            256,
            256,
            0x2929_891D_3F9F_8CD1,
            [0x0F4D_47FF, 0x0F4B_46FF, 0x0C43_45FF, 0x0E4E_45FF],
        ),
        // JPEG: baseline 4:4:4 with restarts, baseline 4:2:0 without,
        // baseline 4:2:0 with restarts, progressive 4:4:4.
        (
            0x0600_4D26,
            800,
            600,
            0xB698_00A1_F66B_E2B1,
            [0x4C2E_08FF, 0x492B_05FF, 0x0F1A_2BFF, 0x1423_36FF],
        ),
        (
            0x0600_4D5F,
            471,
            471,
            0x61E4_4C4E_3FA4_89EF,
            [0x5842_1BFF, 0x5B45_1EFF, 0x7C61_34FF, 0x775B_33FF],
        ),
        (
            0x0600_5F14,
            400,
            300,
            0x2173_6FCF_AE47_EA11,
            [0x0101_01FF, 0x0101_01FF, 0x3551_1EFF, 0x0101_01FF],
        ),
        (
            0x0600_66AB,
            640,
            480,
            0x5075_D209_D8E8_31CB,
            [0x0101_01FF, 0x0101_01FF, 0x3B27_28FF, 0x0000_00FF],
        ),
        // High-resolution dat.
        (
            0x0600_389B,
            256,
            256,
            0x7266_3890_B008_8651,
            [0x0808_08FF, 0x0808_08FF, 0x1C1C_10FF, 0x0808_08FF],
        ),
        (
            0x0600_3797,
            128,
            128,
            0xD4C7_80A8_673D_D99C,
            [0x0000_00FF, 0x0000_00FF, 0x3C08_00FF, 0x0000_00FF],
        ),
        (
            0x0600_3E9B,
            128,
            128,
            0xEE0E_34F1_4C5E_D282,
            [0x1510_05FF, 0x1510_05FF, 0x365B_3C00, 0x1B0E_02FF],
        ),
    ];
    for (id, w, h, hash, px) in cases {
        let rs: RenderSurface = d.get(id).unwrap().unwrap();
        let t = decode(&d, &rs, plain);
        assert_eq!((t.width, t.height), (w, h), "{id:#010x}");
        assert_eq!(samples(&t), px, "{id:#010x}");
        assert_eq!(fnv(&t), hash, "{id:#010x}");
    }

    // The surface options.
    let rs: RenderSurface = d.get(0x0600_5ECE).unwrap().unwrap();
    let additive = DecodeOptions {
        additive: true,
        ..plain
    };
    let t = decode(&d, &rs, additive);
    assert_eq!(samples(&t)[2], 0x7373_7373);
    assert_eq!(fnv(&t), 0x458E_E8AC_93DB_34CE);
    let rs: RenderSurface = d.get(0x0600_3789).unwrap().unwrap();
    let clip = DecodeOptions {
        clip_map: true,
        ..plain
    };
    assert_eq!(fnv(&decode(&d, &rs, clip)), 0xFA5D_38C9_62DD_945F);
}

#[test]
#[ignore = "needs the retail dat files"]
fn every_texture_decodes_like_the_reference_decoders() {
    let d = dats();
    let sweep = |dat: &DatFile, name: &str, out: &mut BTreeMap<String, (u64, u64)>| {
        for entry in dat.entries() {
            let id = entry.unwrap().id;
            if !(0x0600_0000..=0x07FF_FFFF).contains(&id) {
                continue;
            }
            let rs: RenderSurface = dat.get(id).unwrap().unwrap();
            let t = decode(&d, &rs, DecodeOptions::default());
            let e = out
                .entry(format!("{name}:{}", rs.format.name()))
                .or_default();
            e.0 += 1;
            e.1 = e.1.wrapping_add(fnv(&t));
        }
    };
    let mut got = BTreeMap::new();
    sweep(&d.portal, "portal", &mut got);
    sweep(&d.high_res, "highres", &mut got);
    let total = got.values().fold(0u64, |a, (_, h)| a.wrapping_add(*h));
    let expected: BTreeMap<String, (u64, u64)> = [
        ("highres:A8R8G8B8", 3, 0x54FD_BBC1_BF5F_B9DE),
        ("highres:CUSTOM_LSCAPE_ALPHA", 8, 0x3E20_5AC9_F18B_AF2E),
        ("highres:DXT1", 986, 0x11CD_3246_91A4_DECB),
        ("highres:DXT5", 1, 0xEE0E_34F1_4C5E_D282),
        ("highres:INDEX16", 1283, 0xD0BC_CEE6_A033_C517),
        ("highres:R8G8B8", 13, 0x8267_8CC9_4A0B_BE5D),
        ("portal:A4R4G4B4", 2, 0xA3E6_F9F4_C67B_4D89),
        ("portal:A8", 86, 0x7397_939C_9B3A_618A),
        ("portal:A8R8G8B8", 12_984, 0xBCDF_DBDA_78B9_01CC),
        ("portal:CUSTOM_LSCAPE_ALPHA", 16, 0x8C05_A616_E7BC_FD5C),
        ("portal:CUSTOM_LSCAPE_R8G8B8", 33, 0x83B6_9B88_1170_F4D5),
        ("portal:CUSTOM_RAW_JPEG", 79, 0x6012_7739_6E20_9117),
        ("portal:DXT1", 1971, 0x7369_0F3C_D600_8018),
        ("portal:DXT3", 5, 0x175A_948C_5840_3D88),
        ("portal:DXT5", 127, 0x7A4B_6EF0_529D_C7CA),
        ("portal:INDEX16", 4182, 0xD816_E350_F282_A997),
        ("portal:P8", 6, 0xDCDC_5895_E4E8_8618),
        ("portal:R5G6B5", 3, 0xAB99_297E_8C86_16AB),
        ("portal:R8G8B8", 1190, 0x758E_CF78_6FA1_DF91),
    ]
    .into_iter()
    .map(|(k, n, h)| (k.to_owned(), (n, h)))
    .collect();
    assert_eq!(got, expected);
    assert_eq!(total, 0x0575_434F_0F5D_7D49);
}
