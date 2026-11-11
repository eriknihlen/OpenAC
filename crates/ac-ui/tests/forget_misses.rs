//! The UI content's and the icon composer's caches over dats that arrive
//! while they run (a page hands its dat files in one by one): a read of a
//! file not handed in yet is a miss, kept as one as with any dat, until the
//! misses are forgotten; then it is read again and found.
//!
//! Over packs filled from the installed dats (`Documents\Asheron's Call`).

use std::sync::Arc;

use ac_core::items::ItemType;
use ac_dat::{DatCollection, DatFile};
use ac_ui::DatContent;
use ac_ui::draw::{SpriteSizes, TextureRef};
use ac_ui::icons::{IconComposer, IconSource, IconUploader, Rgba};
use ac_ui::import::LayoutSource;

/// A render surface the installed portal dat holds.
const SURFACE: u32 = 0x0600_1100;
/// Another one.
const OTHER_SURFACE: u32 = 0x0600_10FA;

fn installed() -> DatCollection {
    DatCollection::open(ac_dat::default_dat_dir().expect("a user profile")).expect("the dats")
}

/// Empty packs with the installed dats' headers (no high-resolution dat).
fn packs(from: &DatCollection) -> Arc<DatCollection> {
    let pack = |file: &DatFile| DatFile::new_pack(file.header().clone());
    Arc::new(
        DatCollection::from_packs(
            pack(&from.portal),
            pack(&from.cell),
            None,
            pack(&from.local),
        )
        .unwrap(),
    )
}

/// Hand in every file the packs were asked for, except `held_back`.
fn answer(from: &DatCollection, to: &DatCollection, held_back: &[u32]) -> usize {
    let wanted = to.take_wanted();
    for &(source, id) in &wanted {
        let pack = to.pack(source).unwrap();
        if held_back.contains(&id) {
            continue;
        }
        match from.file(source).and_then(|f| f.get_bytes(id).unwrap()) {
            Some(bytes) => pack.insert(id, bytes),
            None => pack.mark_missing(id),
        }
    }
    wanted.len()
}

/// The content over `dats`, its master table handed in.
fn content(from: &DatCollection, dats: &Arc<DatCollection>) -> DatContent {
    loop {
        if let Ok(content) = DatContent::new(Arc::clone(dats)) {
            return content;
        }
        assert!(
            answer(from, dats, &[]) > 0,
            "the master table is in the dats"
        );
    }
}

#[test]
#[ignore = "reads the installed dats"]
fn the_content_reads_again_what_was_missing_once_the_misses_are_forgotten() {
    let from = installed();
    let dats = packs(&from);
    let content = content(&from, &dats);
    let native = DatContent::new(Arc::new(installed())).unwrap();

    // A sprite size: missing, still missing once the file is in, found
    // after the misses are forgotten.
    assert_eq!(content.sprite_size(SURFACE), None);
    assert!(answer(&from, &dats, &[]) > 0);
    assert_eq!(content.sprite_size(SURFACE), None, "the miss is kept");
    content.forget_misses();
    let size = content.sprite_size(SURFACE);
    assert!(size.is_some());
    assert_eq!(size, native.sprite_size(SURFACE));

    // A layout and a font: each read again after every round of files.
    let layout = ac_ui::panels::vitals::LAYOUT_ID;
    let font = ac_ui::font::DEFAULT_FONT_ID;
    for rounds in 0.. {
        let found = content.layout(layout).is_some() && content.font(font).is_some();
        let asked = answer(&from, &dats, &[]);
        if found && asked == 0 {
            break;
        }
        assert!(asked > 0, "nothing more to hand in");
        assert!(rounds < 20);
        content.forget_misses();
    }
    assert_eq!(
        content.layout(layout).map(|l| l.elements.len()),
        native.layout(layout).map(|l| l.elements.len())
    );
    // What was found is kept: forgetting again reads nothing.
    content.forget_misses();
    assert!(content.layout(layout).is_some() && content.font(font).is_some());
    let left = dats.take_wanted();
    assert!(left.is_empty(), "{left:x?}");
}

/// Uploads counted: each gets the next runtime texture.
#[derive(Default)]
struct Uploads(u32);

impl IconUploader for Uploads {
    fn upload(&mut self, _image: &Rgba) -> TextureRef {
        self.0 += 1;
        TextureRef::Runtime(self.0)
    }
}

#[test]
#[ignore = "reads the installed dats"]
fn the_icon_composer_composes_again_what_was_missing_once_the_misses_are_forgotten() {
    let from = installed();
    let dats = packs(&from);
    let mut icons = IconComposer::new(Arc::clone(&dats), Box::new(Uploads::default()));

    // A surface: missing, kept missing, found once forgotten.
    assert!(icons.surface(SURFACE).is_none());
    answer(&from, &dats, &[]);
    assert!(icons.surface(SURFACE).is_none(), "the miss is kept");
    icons.forget_misses();
    assert!(icons.surface(SURFACE).is_some());

    // An item icon made while its own art is still missing (the type's
    // underlay and the effect tiles are in): made again with it once it
    // is in and the misses are forgotten.
    let kind = ItemType(1);
    let mut partial = None;
    for _ in 0..20 {
        partial = icons.item_icon(kind, OTHER_SURFACE, 0, 0, 0);
        if answer(&from, &dats, &[OTHER_SURFACE]) == 0 {
            break;
        }
        icons.forget_misses();
    }
    let partial = partial.expect("the underlay alone makes an icon");
    assert_eq!(
        icons.item_icon(kind, OTHER_SURFACE, 0, 0, 0),
        Some(partial),
        "kept as made"
    );
    let pack = dats.pack(ac_dat::DatSource::Portal).unwrap();
    pack.insert(
        OTHER_SURFACE,
        from.portal.get_bytes(OTHER_SURFACE).unwrap().unwrap(),
    );
    let mut whole = None;
    for _ in 0..20 {
        icons.forget_misses();
        whole = icons.item_icon(kind, OTHER_SURFACE, 0, 0, 0);
        if answer(&from, &dats, &[]) == 0 {
            break;
        }
    }
    let whole = whole.unwrap();
    assert_ne!(whole, partial, "composed again with its art");
    icons.forget_misses();
    assert_eq!(
        icons.item_icon(kind, OTHER_SURFACE, 0, 0, 0),
        Some(whole),
        "a whole icon is kept"
    );
}
