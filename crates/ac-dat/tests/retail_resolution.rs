//! Which dat an id comes from, and the object cache, against the retail
//! dat files. Run with `cargo test -- --include-ignored` when the dats are
//! installed (`OPENAC_DAT_DIR`, else `Documents\Asheron's Call`).

use std::path::PathBuf;
use std::sync::Arc;

use ac_dat::objects::{RenderSurface, Setup};
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

/// An install without `client_highres.dat`: the other three dats, linked
/// into a fresh folder, open and read as usual, and the high-resolution
/// textures are simply not there.
#[test]
#[ignore = "needs the retail dat files"]
fn an_install_without_the_high_res_dat_still_reads() {
    let full = dats();
    let (_, only) = high_res_textures(&full);
    let dir = std::env::temp_dir().join(format!("ac-dat-no-high-res-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&dir);
    std::fs::create_dir_all(&dir).unwrap();
    for name in [ac_dat::PORTAL_DAT, ac_dat::CELL_DAT, ac_dat::LOCAL_DAT] {
        std::fs::hard_link(full.directory().join(name), dir.join(name))
            .expect("link the dat into the test folder (same volume)");
    }
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
