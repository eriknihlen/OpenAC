//! Which dat an id comes from, and the object cache, against the retail
//! dat files. Run with `cargo test -- --include-ignored` when the dats are
//! installed (`OPENAC_DAT_DIR`, else `Documents\Asheron's Call`).

use std::path::PathBuf;
use std::sync::Arc;

use ac_dat::objects::{RenderSurface, Setup};
use ac_dat::{CachedDats, DatSource};

fn dats() -> CachedDats {
    let dir = std::env::var_os("OPENAC_DAT_DIR")
        .map(PathBuf::from)
        .or_else(ac_dat::default_dat_dir)
        .expect("no dat directory: set OPENAC_DAT_DIR");
    CachedDats::open(dir).expect("open the retail dats")
}

#[test]
#[ignore = "needs the retail dat files"]
fn a_texture_only_in_the_high_res_dat_is_read_from_there() {
    let d = dats();
    let raw = d.uncached();
    let high_res_only = raw
        .high_res
        .entries()
        .map(|e| e.unwrap().id)
        .filter(|id| (0x0600_0000..=0x07FF_FFFF).contains(id))
        .find(|id| raw.portal.find_entry(*id).unwrap().is_none())
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
fn a_texture_in_both_dats_is_read_from_the_portal_dat() {
    let d = dats();
    let raw = d.uncached();
    let both = raw
        .high_res
        .entries()
        .map(|e| e.unwrap().id)
        .filter(|id| (0x0600_0000..=0x07FF_FFFF).contains(id))
        .find(|id| raw.portal.find_entry(*id).unwrap().is_some());
    // The retail dats may keep the two sets apart; when an id is in both,
    // the portal copy wins.
    if let Some(id) = both {
        assert_eq!(raw.resolve(id).unwrap(), Some(DatSource::Portal));
        let cached = d.get::<RenderSurface>(id).unwrap().unwrap();
        assert_eq!(
            *cached,
            raw.portal.get::<RenderSurface>(id).unwrap().unwrap()
        );
    }
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
