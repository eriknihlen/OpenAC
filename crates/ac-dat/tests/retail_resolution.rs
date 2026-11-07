//! Which dat an id comes from, and the object cache, against the retail
//! dat files. Run with `cargo test -- --include-ignored` when the dats are
//! installed (`OPENAC_DAT_DIR`, else `Documents\Asheron's Call`).

use std::path::PathBuf;
use std::sync::Arc;

use ac_dat::objects::{Palette, RenderSurface, Setup, SurfaceTexture};
use ac_dat::texture::{self, DecodeOptions};
use ac_dat::{CachedDats, DatFile, DatSource, ITERATION_ID};

fn dats() -> CachedDats {
    let dir = std::env::var_os("OPENAC_DAT_DIR")
        .map(PathBuf::from)
        .or_else(ac_dat::default_dat_dir)
        .expect("no dat directory: set OPENAC_DAT_DIR");
    CachedDats::open(dir).expect("open the retail dats")
}

fn high_res(d: &CachedDats) -> &DatFile {
    d.uncached()
        .high_res
        .as_ref()
        .expect("the install has client_highres.dat")
}

/// The texture ids (0x06xxxxxx and 0x07xxxxxx) the high-resolution dat
/// holds, split by whether the portal dat holds them too.
fn high_res_textures(d: &CachedDats) -> (Vec<u32>, Vec<u32>) {
    let portal = &d.uncached().portal;
    high_res(d)
        .entries()
        .map(|e| e.unwrap().id)
        .filter(|id| (0x0600_0000..=0x07FF_FFFF).contains(id))
        .partition(|id| portal.find_entry(*id).unwrap().is_some())
}

#[test]
#[ignore = "needs the retail dat files"]
fn a_texture_only_in_the_high_res_dat_is_read_from_there() {
    let d = dats();
    let raw = d.uncached();
    let (_, only) = high_res_textures(&d);
    let high_res_only = *only
        .first()
        .expect("the high-resolution dat has textures the portal dat lacks");
    assert_eq!(
        raw.resolve(high_res_only).unwrap(),
        Some(DatSource::HighRes)
    );
    let surface = d.get::<RenderSurface>(high_res_only).unwrap().unwrap();
    assert_eq!(surface.id, high_res_only);
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_high_res_dat_is_asked_before_the_portal_dat() {
    let d = dats();
    let raw = d.uncached();
    // The installed dats keep their objects apart: the high-resolution
    // dat's 2,294 textures are all missing from the portal dat, and the
    // only id the two share is the iteration record every dat carries. So
    // the order changes no object read here; the shared record shows it.
    let (both, only) = high_res_textures(&d);
    assert_eq!((both.len(), only.len()), (0, 2294));
    let shared: Vec<u32> = high_res(&d)
        .entries()
        .map(|e| e.unwrap().id)
        .filter(|id| raw.portal.find_entry(*id).unwrap().is_some())
        .collect();
    assert_eq!(shared, [ITERATION_ID]);
    assert_eq!(raw.resolve(ITERATION_ID).unwrap(), Some(DatSource::HighRes));
    assert_eq!(raw.portal_source(ITERATION_ID).unwrap(), DatSource::HighRes);
    assert_eq!(raw.resolve(0x0200_0001).unwrap(), Some(DatSource::Portal));
    assert_eq!(raw.resolve(0xA9B4_FFFF).unwrap(), Some(DatSource::Cell));
    assert_eq!(raw.resolve(0x7FFF_FFFE).unwrap(), None);
}

#[test]
#[ignore = "needs the retail dat files"]
fn a_second_read_comes_from_the_cache() {
    let d = dats();
    let first = d.get::<Setup>(0x0200_0001).unwrap().unwrap();
    let second = d.get::<Setup>(0x0200_0001).unwrap().unwrap();
    assert!(Arc::ptr_eq(&first, &second));
    let stats = d.stats();
    assert_eq!((stats.hits, stats.misses), (1, 1));
}

/// A fresh folder holding the portal, cell and language dats of `full`
/// (hard links, so the temp folder must be on the dats' volume) and no
/// `client_highres.dat`.
fn without_high_res(full: &CachedDats, tag: &str) -> PathBuf {
    let dir = std::env::temp_dir().join(format!("ac-dat-no-high-res-{}-{tag}", std::process::id()));
    let _ = std::fs::remove_dir_all(&dir);
    std::fs::create_dir_all(&dir).unwrap();
    for name in [ac_dat::PORTAL_DAT, ac_dat::CELL_DAT, ac_dat::LOCAL_DAT] {
        std::fs::hard_link(full.directory().join(name), dir.join(name))
            .expect("link the dat into the test folder (same volume)");
    }
    dir
}

/// Every surface texture (0x05xxxxxx) with its levels.
fn surface_textures(d: &CachedDats) -> Vec<Arc<SurfaceTexture>> {
    d.uncached()
        .portal
        .entries()
        .map(|e| e.unwrap().id)
        .filter(|id| id >> 24 == 0x05)
        .map(|id| d.get::<SurfaceTexture>(id).unwrap().unwrap())
        .collect()
}

/// A two-level surface texture keeps its full-detail image in the
/// high-resolution dat and a reduced one in the portal dat. Without the
/// high-resolution dat the reduced one is drawn; with it, the full one.
#[test]
#[ignore = "needs the retail dat files"]
fn a_two_level_texture_is_drawn_from_the_level_the_dats_hold() {
    let full = dats();
    let textures = surface_textures(&full);
    let mut by_levels = std::collections::BTreeMap::<usize, usize>::new();
    for t in &textures {
        *by_levels.entry(t.textures.len()).or_default() += 1;
    }
    let two: Vec<_> = textures.iter().filter(|t| t.textures.len() == 2).collect();
    let portal = &full.uncached().portal;
    // Where each of the two levels lives: H high-resolution dat only, P
    // portal dat only, B both, - neither.
    let place = |id: u32| match (
        high_res(&full).find_entry(id).unwrap().is_some(),
        portal.find_entry(id).unwrap().is_some(),
    ) {
        (true, false) => 'H',
        (false, true) => 'P',
        (true, true) => 'B',
        (false, false) => '-',
    };
    let mut layout = std::collections::BTreeMap::<String, usize>::new();
    for t in &two {
        let key: String = t.textures.iter().map(|id| place(*id)).collect();
        *layout.entry(key).or_default() += 1;
    }
    println!("surface textures by level count: {by_levels:?}; two-level layouts: {layout:?}");
    // The installed dats: 4,197 one-level and 3,024 two-level textures; of
    // the two-level ones 2,284 keep level 0 only in the high-resolution dat
    // and level 1 in the portal dat, and 740 keep both in the portal dat.
    assert_eq!(by_levels, [(1, 4197), (2, 3024)].into());
    assert_eq!(
        layout,
        [("HP".to_owned(), 2284), ("PP".to_owned(), 740)].into()
    );

    let dir = without_high_res(&full, "levels");
    let reduced = CachedDats::open(&dir).expect("open without client_highres.dat");
    for (dats, level) in [(&full, 0), (&reduced, 1)] {
        for t in &two {
            let id = dats.texture_source(t).unwrap();
            assert_eq!(id, t.textures[level], "{:08X}", t.id);
            let rs = dats
                .get::<RenderSurface>(id)
                .unwrap()
                .unwrap_or_else(|| panic!("{:08X}: no render surface {id:08X}", t.id));
            let palette = (rs.default_palette_id != 0)
                .then(|| dats.get::<Palette>(rs.default_palette_id).unwrap())
                .flatten();
            texture::decode(&rs, palette.as_deref(), DecodeOptions::default())
                .unwrap_or_else(|e| panic!("{:08X} level {level}: {e}", t.id));
        }
    }
    drop(reduced);
    std::fs::remove_dir_all(dir).ok();
}

/// An install without `client_highres.dat`: the other three dats, linked
/// into a fresh folder, open and read as usual, and the high-resolution
/// textures are simply not there.
#[test]
#[ignore = "needs the retail dat files"]
fn an_install_without_the_high_res_dat_still_reads() {
    let full = dats();
    let (_, only) = high_res_textures(&full);
    let dir = without_high_res(&full, "reads");
    let d = CachedDats::open(&dir).expect("open without client_highres.dat");
    assert!(d.uncached().high_res.is_none());
    assert!(d.get::<Setup>(0x0200_0001).unwrap().is_some());
    assert!(d.get::<RenderSurface>(only[0]).unwrap().is_none());
    assert_eq!(d.uncached().resolve(only[0]).unwrap(), None);
    assert_eq!(
        d.uncached().resolve(ITERATION_ID).unwrap(),
        Some(DatSource::Portal)
    );
    drop(d);
    std::fs::remove_dir_all(dir).ok();
}
