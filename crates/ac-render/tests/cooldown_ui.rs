//! The shared item cooldown overlays in the game window on the retail
//! dats: once every window is mounted, every item list (the toolbar's
//! shortcuts, the pack, the paperdoll, ...) takes the ten authored steps.

use std::path::PathBuf;
use std::sync::Arc;

use ac_dat::DatCollection;
use ac_render::ui::WindowUi;
use ac_ui::widgets::ItemList;

fn dats() -> Arc<DatCollection> {
    let dir = std::env::var_os("OPENAC_DAT_DIR")
        .map(PathBuf::from)
        .or_else(ac_dat::default_dat_dir)
        .expect("no dat directory: set OPENAC_DAT_DIR");
    Arc::new(DatCollection::open(dir).expect("open the retail dats"))
}

#[test]
#[ignore = "needs the retail dat files"]
fn every_mounted_item_list_takes_the_ten_cooldown_overlays() {
    let ui = WindowUi::build(dats(), (1280, 720)).expect("the UI builds");
    let root = &ui.host().root;
    let mut lists = 0;
    let mut stack = vec![root.root()];
    while let Some(e) = stack.pop() {
        if let Some(l) = root.widget::<ItemList>(e) {
            lists += 1;
            assert_eq!(l.cooldown_sprites().len(), 10);
        }
        stack.extend(root.children(e));
    }
    assert!(lists >= 5, "only {lists} item lists");
}
