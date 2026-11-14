//! The module without a session, driven as a page drives it over the
//! installed dats: one window shown read-only from a character's stored
//! data, with selection kept in the module and no command sent.
//!
//! Needs the dats in `Documents\Asheron's Call` under the user's profile.
//! `OPENAC_OFFLINE_PNG` names a folder to write each picture to.

mod harness;

use ac_ui_web::host::Panels;
use ac_ui_web::offline::{OfflineData, OfflineKind};
use harness::Page;

const WIDTH: u32 = 800;
const HEIGHT: u32 = 600;
const PLAYER: u32 = 0x5000_0001;
const PACK: u32 = 0x8000_0100;
const FIXTURE: &str = include_str!("fixtures/offline_inventory.json");

fn open(kind: OfflineKind) -> Page {
    let mut page = Page::open();
    page.module.panels().set_offline(kind);
    page.module
        .panels()
        .offline_data(FIXTURE.as_bytes())
        .expect("the fixture reads");
    for (source, id) in page.module.panels().dat_preload() {
        page.answer(source, id);
    }
    page.module.panels().init(WIDTH, HEIGHT, 1.0, 0.0);
    page.settle(400);
    // The data reaches the panels on the next frames.
    page.settle(50);
    page
}

fn picture(page: &Page, name: &str) {
    if let Ok(dir) = std::env::var("OPENAC_OFFLINE_PNG") {
        let path = std::path::Path::new(&dir).join(format!("{name}.png"));
        page.write_png(WIDTH, HEIGHT, &path);
        println!("picture: {}", path.display());
    }
}

#[test]
fn the_records_become_the_client_objects() {
    let data = OfflineData::parse(FIXTURE.as_bytes()).unwrap();
    assert_eq!(data.player, PLAYER);
    assert_eq!(data.name, "Offline Tester");
    let sleeves = &data.items[0];
    assert_eq!(sleeves.name, "Chainmail Sleeves");
    assert_eq!(sleeves.icon_id, 0x0600_1582);
    assert_eq!(sleeves.wielder_id, PLAYER, "worn: wielded by the player");
    assert_eq!(sleeves.container_id, 0);
    assert_eq!(sleeves.currently_equipped_location.0, 0x1800);
    assert_eq!(sleeves.burden, 600);
    let pack = data.items.iter().find(|i| i.object_id() == PACK).unwrap();
    assert_eq!(pack.container_id, PLAYER);
    assert_eq!(pack.items_capacity, 24);
    assert_eq!(pack.container_type_hint, 1, "a pack");
    let main: Vec<u32> = data
        .contents
        .iter()
        .find(|(c, _)| *c == PLAYER)
        .map(|(_, l)| l.clone())
        .unwrap();
    assert!(main.contains(&PACK));
    let in_pack = data.contents.iter().find(|(c, _)| *c == PACK).unwrap();
    assert_eq!(in_pack.1.len(), 3);
    // Strength without spells stays; strength now comes from the
    // capacity (45000 / 150).
    assert_eq!(data.stats.attributes[0], Some((100, 100)));
    assert_eq!(data.stats.strength_now, Some(300));
    assert_eq!(data.stats.ints.get(&25), Some(&126), "the level");
    assert_eq!(
        data.stats.int64s.get(&1),
        Some(&191_226_310),
        "total experience"
    );
    let war = data
        .stats
        .reported
        .skills
        .iter()
        .find(|s| s.key == "war_magic")
        .unwrap();
    assert_eq!(war.base, 330);
    assert_eq!(data.stats.reported.vitals[1].unwrap().max, 330);
    // Framework keys stay out of the game properties.
    assert!(sleeves.properties.ints.keys().all(|k| *k < 167_772_160));
    assert_eq!(sleeves.properties.ints.get(&5), Some(&600));
}

#[test]
#[ignore = "reads the installed dats"]
fn the_inventory_shows_alone_and_a_click_selects_without_a_command() {
    let mut page = open(OfflineKind::Inventory);
    println!("{}", page.log);
    picture(&page, "inventory");
    let ui = page.module.panels().ui().expect("built");
    let root = &ui.host().root;
    assert!(root.is_window_visible("inventory"), "{}", page.log);
    let others: Vec<String> = root
        .windows()
        .iter()
        .filter(|w| w.name != "inventory" && root.is_window_visible(&w.name))
        .map(|w| w.name.clone())
        .collect();
    assert!(others.is_empty(), "only the inventory shows: {others:?}");
    let facts = ui.facts();
    assert_eq!(facts.state.player, PLAYER);
    assert!(facts.objects.get(PACK).is_some());
    assert!(!page.runs.is_empty(), "a picture");

    // A click on the worn boots (the foot slot, at the slot view's place)
    // selects them; nothing is sent.
    click(&mut page, (141.0, 215.0));
    let boots = OfflineData::parse(FIXTURE.as_bytes())
        .unwrap()
        .items
        .iter()
        .find(|i| i.name == "Leather Boots" && i.wielder_id == PLAYER)
        .map(ac_core::items::ClientObject::object_id);
    assert_eq!(page.module.panels().offline_selected(), boots);
    picture(&page, "inventory-selected");

    // A click on the side pack opens it: its three items show.
    click(&mut page, (267.0, 118.0));
    picture(&page, "inventory-side-pack");
    assert!(
        page.sent_stamped().is_empty(),
        "no command leaves the module"
    );

    // The same data again: the side pack stays open and selected (opening
    // a pack selects it).
    page.module
        .panels()
        .offline_data(FIXTURE.as_bytes())
        .unwrap();
    page.settle(20);
    picture(&page, "inventory-refreshed");
    assert_eq!(page.module.panels().offline_selected(), Some(PACK));
    let size = page.module.panels().offline_size();
    println!("natural size: {size:?}");
    assert!(size.is_some_and(|(w, h)| w > 200 && h > 200), "{size:?}");
}

fn click(page: &mut Page, at: (f32, f32)) {
    page.module
        .panels()
        .pointer_button(0, true, at, Default::default(), page.now);
    page.frame_after(50.0);
    page.module
        .panels()
        .pointer_button(0, false, at, Default::default(), page.now);
    page.frame_after(400.0);
    page.settle(20);
}

#[test]
#[ignore = "reads the installed dats"]
fn the_character_window_shows_alone() {
    let mut page = open(OfflineKind::Character);
    println!("{}", page.log);
    picture(&page, "character");
    let ui = page.module.panels().ui().expect("built");
    let root = &ui.host().root;
    assert!(root.is_window_visible("character"), "{}", page.log);
    // The skills tab lists the reported skills by training.
    click(&mut page, (143.0, 18.0));
    picture(&page, "character-skills");
    assert!(page.sent_stamped().is_empty());
}
