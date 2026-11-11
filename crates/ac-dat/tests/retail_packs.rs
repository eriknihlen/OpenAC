//! Packs built from the retail dats' own bytes read as the mapped dats do:
//! the same typed objects, the same dat for each id, the high-resolution
//! dat ahead of the portal dat. Run with `cargo test -- --include-ignored`
//! when the dats are installed (`OPENAC_DAT_DIR`, else
//! `Documents\Asheron's Call`).

use std::path::PathBuf;

use ac_dat::objects::{
    DatObject, Font, LayoutDesc, MASTER_PROPERTY_ID, MasterProperty, RenderSurface, StringTable,
};
use ac_dat::{CachedDats, DatCollection, DatFile, DatSource};

fn mapped() -> DatCollection {
    let dir = std::env::var_os("OPENAC_DAT_DIR")
        .map(PathBuf::from)
        .or_else(ac_dat::default_dat_dir)
        .expect("no dat directory: set OPENAC_DAT_DIR");
    DatCollection::open(dir).expect("open the retail dats")
}

/// Empty packs with the mapped dats' headers.
fn empty_packs(files: &DatCollection) -> DatCollection {
    let pack = |dat: &DatFile| DatFile::new_pack(dat.header().clone());
    DatCollection::from_packs(
        pack(&files.portal),
        pack(&files.cell),
        files.high_res.as_ref().map(pack),
        pack(&files.local),
    )
    .unwrap()
}

/// Packs preloaded with `ids` as a pack builder makes them: every dat's
/// bytes of each id, and "no such file" from every dat that lacks it.
fn preloaded_packs(files: &DatCollection, ids: &[u32]) -> DatCollection {
    let packs = empty_packs(files);
    for source in DatSource::PRECEDENCE {
        let (Some(file), Some(pack)) = (files.file(source), packs.pack(source)) else {
            continue;
        };
        for &id in ids {
            match file.get_bytes(id).unwrap() {
                Some(bytes) => pack.insert(id, bytes),
                None => pack.mark_missing(id),
            }
        }
    }
    packs
}

/// Answer what the packs ask for from the mapped dats, as a host fetching
/// files by id does.
fn serve(files: &DatCollection, packs: &DatCollection) -> usize {
    let wanted = packs.take_wanted();
    for &(source, id) in &wanted {
        let pack = packs.pack(source).unwrap();
        match files.file(source).unwrap().get_bytes(id).unwrap() {
            Some(bytes) => pack.insert(id, bytes),
            None => pack.mark_missing(id),
        }
    }
    wanted.len()
}

/// `read` once the packs know everything it asks for.
fn settled<R>(files: &DatCollection, packs: &DatCollection, read: impl Fn() -> R) -> R {
    while {
        read();
        serve(files, packs) > 0
    } {}
    read()
}

/// A render surface the high-resolution dat holds (the installed dats keep
/// their objects apart: the portal dat does not hold it).
fn high_res_texture(files: &DatCollection) -> u32 {
    let high_res = files
        .high_res
        .as_ref()
        .expect("the install has client_highres.dat");
    high_res
        .entries()
        .map(|e| e.unwrap().id)
        .find(|id| (0x0600_0000..=0x06FF_FFFF).contains(id))
        .expect("a render surface in the high-resolution dat")
}

/// The objects compared: the master property table, a layout, a font, a
/// string table, render surfaces (the font's and a high-resolution one)
/// and a spell icon.
const FONT: u32 = 0x4000_0000;
const LAYOUT: u32 = 0x2100_0006;
const STRING_TABLE: u32 = 0x2300_0001;
const FONT_SURFACE: u32 = 0x0600_5EE5;
const ICON: u32 = 0x0600_138C;

fn compare(files: &DatCollection, packs: &DatCollection, surfaces: &[u32]) {
    fn same<T: DatObject + PartialEq + std::fmt::Debug>(
        files: &DatCollection,
        packs: &DatCollection,
        id: u32,
    ) {
        let expected = files.get::<T>(id).unwrap();
        assert!(expected.is_some(), "{id:#010x} is in the dats");
        assert_eq!(
            settled(files, packs, || packs.get::<T>(id).unwrap()),
            expected
        );
    }
    assert_eq!(packs.has_high_res(), files.has_high_res());
    assert_eq!(
        packs.portal.header().master_map_id,
        files.portal.header().master_map_id
    );
    same::<MasterProperty>(files, packs, MASTER_PROPERTY_ID);
    same::<Font>(files, packs, FONT);
    same::<StringTable>(files, packs, STRING_TABLE);
    for &id in surfaces {
        same::<RenderSurface>(files, packs, id);
    }
    let master = files
        .get::<MasterProperty>(MASTER_PROPERTY_ID)
        .unwrap()
        .unwrap();
    assert_eq!(
        settled(files, packs, || LayoutDesc::read(
            &packs.local,
            &master,
            LAYOUT
        )
        .unwrap()),
        LayoutDesc::read(&files.local, &master, LAYOUT).unwrap()
    );
    for id in [MASTER_PROPERTY_ID, FONT, STRING_TABLE, LAYOUT]
        .into_iter()
        .chain(surfaces.iter().copied())
    {
        assert_eq!(
            settled(files, packs, || packs.resolve(id).unwrap()),
            files.resolve(id).unwrap(),
            "{id:#010x}"
        );
        assert_eq!(
            settled(files, packs, || packs.portal_source(id).ok()),
            Some(files.portal_source(id).unwrap()),
            "{id:#010x}"
        );
    }
}

#[test]
#[ignore = "needs the retail dat files"]
fn packs_fetched_on_demand_read_as_the_mapped_dats() {
    let files = mapped();
    let surfaces = [FONT_SURFACE, ICON, high_res_texture(&files)];
    let packs = empty_packs(&files);
    compare(&files, &packs, &surfaces);
    assert_eq!(
        packs.portal_source(surfaces[2]).unwrap(),
        DatSource::HighRes
    );
    assert_eq!(packs.portal_source(ICON).unwrap(), DatSource::Portal);
}

#[test]
#[ignore = "needs the retail dat files"]
fn preloaded_packs_read_as_the_mapped_dats_without_asking() {
    let files = mapped();
    let surfaces = [FONT_SURFACE, ICON, high_res_texture(&files)];
    let mut ids = vec![MASTER_PROPERTY_ID, FONT, STRING_TABLE, LAYOUT];
    ids.extend(&surfaces);
    let packs = preloaded_packs(&files, &ids);
    compare(&files, &packs, &surfaces);
    assert!(packs.take_wanted().is_empty());

    // The same through the object caches, a second time from memory.
    let cached = CachedDats::new(preloaded_packs(&files, &ids), PathBuf::new());
    for _ in 0..2 {
        assert_eq!(
            cached.get::<Font>(FONT).unwrap().as_deref(),
            files.get::<Font>(FONT).unwrap().as_ref()
        );
        for &id in &surfaces {
            assert_eq!(
                cached.get::<RenderSurface>(id).unwrap().as_deref(),
                files.get::<RenderSurface>(id).unwrap().as_ref()
            );
        }
    }
    assert!(cached.uncached().take_wanted().is_empty());
    assert_eq!(cached.stats().hits, 1 + surfaces.len() as u64);
}
