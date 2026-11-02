//! The options window against the retail layout (0x2100006E/0x1000018D).
//!
//! Ported from OpenAC `tests/AcDream.App.Tests/UI/Layout/`:
//! `OptionsPanelLayoutConformanceTests.cs` (tab table, row templates,
//! scrollbar links), `OptionsPanelControllerTests.cs` (four pages, footer
//! backing, gating, tab and window show/hide, the Gameplay buttons),
//! `CharacterOptionsPageControllerTests.cs`, `ConfigOptionsPageControllerTests.cs`
//! and `ChatOptionsPageControllerTests.cs` (row counts, seeding, clicks,
//! Apply/Reset/Defaults, captions from the string tables, the opacity link)
//! and `OptionsPanelLiveMountProbeTests.cs` (the opacity defaults and
//! captions read from the dats). These tests need the dats: set
//! `OPENAC_DAT_DIR` or install them under `Documents\Asheron's Call`; run
//! with `cargo test -- --include-ignored`.

use std::path::PathBuf;
use std::sync::Arc;

use ac_dat::DatCollection;
use ac_host::settings::PlayerSettings;
use ac_runtime::character::option_id;
use ac_ui::ElementId;
use ac_ui::content::DatContent;
use ac_ui::font::DEFAULT_FONT_ID;
use ac_ui::host::PanelController;
use ac_ui::import::{LayoutSource, import_infos_rooted, import_rooted};
use ac_ui::panels::PanelOutput;
use ac_ui::panels::options::{
    self, LiveValues, OptionValue, OptionsOutput, OptionsPage, OptionsSources,
    OptionsWindowController, Section, chat_dat, resolutions,
};
use ac_ui::root::{ElementMessage, UiOutput, UiRoot};
use ac_ui::strings::compute_hash;
use ac_ui::widgets::{
    Bitfield64, Button, ListBox, Menu, Panel, ScrollSource, Scrollbar, TabPanel, Text, TextContent,
};

fn content() -> DatContent {
    let dir = std::env::var_os("OPENAC_DAT_DIR")
        .map(PathBuf::from)
        .or_else(ac_dat::default_dat_dir)
        .expect("no dat directory: set OPENAC_DAT_DIR");
    DatContent::new(Arc::new(
        DatCollection::open(dir).expect("open the retail dats"),
    ))
    .expect("master property table")
}

struct Bound {
    ui: UiRoot,
    c: OptionsWindowController,
    layout: ac_ui::import::ImportedLayout,
}

fn live() -> LiveValues {
    LiveValues {
        settings: PlayerSettings::default(),
        options1: ac_runtime::character::DEFAULT_OPTIONS1,
        options2: ac_runtime::character::DEFAULT_OPTIONS2,
    }
}

fn bind_with(content: &DatContent, live: LiveValues) -> Bound {
    let mut ui = UiRoot::new();
    ui.set_size(1280.0, 720.0);
    let font = content.font(DEFAULT_FONT_ID);
    let layout = import_rooted(
        &mut ui,
        content,
        options::HOST_LAYOUT_ID,
        options::SLOT_ELEMENT_ID,
        font.clone(),
    )
    .expect("the options window imports");
    let strings = content.strings();
    let lookup = |table: u32, key: &str| strings.resolve(table, compute_hash(key), 0);
    let master = content.master_property();
    let sources = OptionsSources {
        layouts: content,
        font,
        strings: &lookup,
        resolutions: resolutions::AVAILABLE_RESOLUTIONS
            .iter()
            .map(|s| (*s).to_owned())
            .collect(),
        opacity_defaults: chat_dat::opacity_defaults(content.dats(), master).unwrap_or((0.5, 1.0)),
        opacity_captions: chat_dat::opacity_captions(content.dats(), master, strings)
            .unwrap_or_default(),
    };
    let c = OptionsWindowController::bind(&mut ui, &layout, &sources, live).expect("binds");
    let mut b = Bound { ui, c, layout };
    pump(&mut b);
    b
}

/// A panel request, comparable.
#[derive(Debug, Clone, PartialEq)]
enum Out {
    Opt(OptionsOutput),
    Close(u32),
    Other,
}

/// Route the root's queued messages to the controller; the panel outputs.
fn pump(b: &mut Bound) -> Vec<Out> {
    let mut out = Vec::new();
    loop {
        let batch = b.ui.drain_output();
        if batch.is_empty() {
            return out;
        }
        for o in batch {
            match o {
                UiOutput::Element { element, message } => {
                    b.c.on_element_message(&mut b.ui, element, &message);
                }
                UiOutput::Panel(PanelOutput::Options(o)) => out.push(Out::Opt(o)),
                UiOutput::Panel(PanelOutput::ClosePanel(p)) => out.push(Out::Close(p)),
                UiOutput::Panel(_) => out.push(Out::Other),
                _ => {}
            }
        }
    }
}

fn send(b: &mut Bound, element: ElementId, message: ElementMessage) -> Vec<Out> {
    b.c.on_element_message(&mut b.ui, element, &message);
    pump(b)
}

fn click(b: &mut Bound, element: ElementId) -> Vec<Out> {
    send(b, element, ElementMessage::Clicked { x: 1, y: 1 })
}

fn page_root(b: &Bound, page: OptionsPage) -> ElementId {
    b.ui.find_descendant(b.c.root(), page.element_id()).unwrap()
}

fn footer(b: &Bound, page: OptionsPage, id: u32) -> ElementId {
    b.ui.find_descendant(page_root(b, page), id).unwrap()
}

fn enabled(b: &Bound, id: ElementId) -> bool {
    b.ui.element(id).is_some_and(|e| e.enabled)
}

fn list_rows(b: &Bound, list_id: u32) -> Vec<ElementId> {
    let list = b.layout.find(list_id).unwrap();
    let v = b.ui.widget::<ListBox>(list).unwrap().viewport_id().unwrap();
    b.ui.children(v)
}

fn text_of(ui: &UiRoot, id: ElementId) -> String {
    match ui.widget::<Text>(id).map(Text::content) {
        Some(TextContent::Lines(lines)) => lines.iter().map(|l| l.text.clone()).collect(),
        _ => String::new(),
    }
}

/// The checkboxes of the Character page, in order.
fn checkboxes(b: &Bound, list_id: u32) -> Vec<ElementId> {
    let mut out = Vec::new();
    for row in list_rows(b, list_id) {
        let mut stack = vec![row];
        while let Some(id) = stack.pop() {
            if b.ui.widget::<Button>(id).is_some_and(|w| w.label.is_some()) {
                out.push(id);
                break;
            }
            let mut children = b.ui.children(id);
            children.reverse();
            stack.extend(children);
        }
    }
    out
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_tab_table_has_four_pages_with_gameplay_the_default() {
    let c = content();
    let info = import_infos_rooted(&c, options::HOST_LAYOUT_ID, options::SLOT_ELEMENT_ID).unwrap();
    let tabs = &info.tab_table;
    assert_eq!(tabs.len(), 4);
    let entries: Vec<(u32, u32, bool)> = tabs
        .iter()
        .map(|t| (t.button, t.page, t.is_default))
        .collect();
    assert_eq!(
        entries,
        [
            (0x1000_020D, 0x1000_0212, true),
            (0x1000_020E, 0x1000_0211, false),
            (0x1000_050B, 0x1000_050C, false),
            (0x1000_020F, 0x1000_0213, false),
        ]
    );
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_lists_have_their_row_templates_and_scrollbars() {
    let c = content();
    let mut ui = UiRoot::new();
    let layout = import_rooted(
        &mut ui,
        &c,
        options::HOST_LAYOUT_ID,
        options::SLOT_ELEMENT_ID,
        None,
    )
    .unwrap();
    let templates = |id: u32| -> Vec<(u32, u32)> {
        let l = ui.widget::<ListBox>(layout.find(id).unwrap()).unwrap();
        l.templates.iter().map(|t| (t.layout, t.element)).collect()
    };
    let ids = |list: Vec<(u32, u32)>| -> Vec<u32> {
        assert!(list.iter().all(|(l, _)| *l == 0x2100_002B));
        list.into_iter().map(|(_, e)| e).collect()
    };
    assert_eq!(
        ids(templates(options::CHARACTER_LIST_ID)),
        [0x1000_0216, 0x1000_0217, 0x1000_0218]
    );
    assert_eq!(
        ids(templates(options::CONFIG_LIST_ID)),
        [
            0x1000_0216,
            0x1000_0217,
            0x1000_0218,
            0x1000_021A,
            0x1000_0222,
            0x1000_0220,
            0x1000_021D,
            0x1000_0221
        ]
    );
    assert_eq!(
        ids(templates(options::CHAT_LIST_ID)),
        [
            0x1000_0216,
            0x1000_0217,
            0x1000_0218,
            0x1000_021A,
            0x1000_0222,
            0x1000_0220,
            0x1000_021D,
            0x1000_0221,
            0x1000_0520
        ]
    );
    let bar = |id: u32| {
        ui.widget::<ListBox>(layout.find(id).unwrap())
            .unwrap()
            .scrollbar_element_id
    };
    assert_eq!(bar(options::CHARACTER_LIST_ID), 0x1000_01FB);
    assert_eq!(bar(options::CONFIG_LIST_ID), 0x1000_0201);
    assert_eq!(bar(options::CHAT_LIST_ID), 0x1000_0201);
}

#[test]
#[ignore = "needs the retail dat files"]
fn binding_builds_every_page_and_shows_gameplay() {
    let c = content();
    let mut b = bind_with(&c, live());
    assert!(b.ui.widget::<TabPanel>(b.c.root()).is_some());
    assert_eq!(b.c.active_page(&b.ui), Some(OptionsPage::Gameplay));
    assert!(b.c.page(OptionsPage::Gameplay).is_empty());
    assert_eq!(b.c.page(OptionsPage::Character).len(), 50);
    assert_eq!(b.c.page(OptionsPage::Chat).len(), 7);
    // The game's 39, the UI Scale row, the landscape detail textures row,
    // the Graphics Enhancements section's 21 and the two modern mouse
    // turning rows in the input section.
    assert_eq!(b.c.page(OptionsPage::Configuration).len(), 64);
    // 6 headers, 50 toggles, 6 separators; 6 + 40 + 6 (the game's 36, the
    // UI Scale row, the landscape detail textures row and the two modern
    // mouse turning rows) and 1 + 21 + 1; 1 + 2 + 1 + 5 x 3.
    assert_eq!(list_rows(&b, options::CHARACTER_LIST_ID).len(), 62);
    assert_eq!(list_rows(&b, options::CONFIG_LIST_ID).len(), 75);
    assert_eq!(list_rows(&b, options::CHAT_LIST_ID).len(), 19);
    let notices = b.c.take_notices();
    assert!(notices.is_empty(), "{notices:?}");
    // Each list scrolls with its own bar.
    for (page, list) in [
        (OptionsPage::Character, options::CHARACTER_LIST_ID),
        (OptionsPage::Configuration, options::CONFIG_LIST_ID),
        (OptionsPage::Chat, options::CHAT_LIST_ID),
    ] {
        let lb = b.layout.find(list).unwrap();
        let v = b.ui.widget::<ListBox>(lb).unwrap().viewport_id().unwrap();
        let bar_id = b.ui.widget::<ListBox>(lb).unwrap().scrollbar_element_id;
        let bar = b.ui.find_descendant(page_root(&b, page), bar_id).unwrap();
        assert_eq!(
            b.ui.widget::<Scrollbar>(bar).unwrap().model,
            ScrollSource::Element(v)
        );
    }
    let _ = pump(&mut b);
}

#[test]
#[ignore = "needs the retail dat files"]
fn each_list_page_has_one_footer_backing_under_everything() {
    let c = content();
    let b = bind_with(&c, live());
    for page in [
        OptionsPage::Character,
        OptionsPage::Chat,
        OptionsPage::Configuration,
    ] {
        let root = page_root(&b, page);
        let backings: Vec<ElementId> =
            b.ui.children(root)
                .into_iter()
                .filter(|c| b.ui.widget::<Panel>(*c).is_some())
                .collect();
        assert_eq!(backings.len(), 1, "{page:?}");
        let backing = backings[0];
        let e = b.ui.element(backing).unwrap();
        assert!(e.click_through);
        assert_eq!(e.width, b.ui.element(root).unwrap().width);
        assert_eq!(
            b.ui.widget::<Panel>(backing).unwrap().background_sprite,
            0x0600_4CC2
        );
        for sibling in b.ui.children(root) {
            if sibling != backing {
                assert!(e.z_order < b.ui.element(sibling).unwrap().z_order);
            }
        }
    }
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_character_rows_are_captioned_from_the_dat_and_seeded_from_the_options() {
    let c = content();
    let mut l = live();
    // Weather off: option 0x04 (word 1, 0x0001_0000).
    l.options1 |= 0x0001_0000;
    let b = bind_with(&c, l);
    let boxes = checkboxes(&b, options::CHARACTER_LIST_ID);
    assert_eq!(boxes.len(), 50);
    let strings = c.strings();
    let caption = |key: &str| strings.resolve(0x2300_0003, compute_hash(key), 0);
    let label = |id: ElementId| b.ui.widget::<Button>(id).unwrap().label.clone();
    // UIDisplay's sixth row is "disable most weather effects".
    let weather = boxes[3 + 5];
    assert_eq!(
        label(weather),
        caption("ID_PlayerOption_DisableMostWeatherEffects")
    );
    assert!(b.ui.widget::<Button>(weather).unwrap().selected());
    // Store-only rows are grey, the others white.
    let house = boxes[3 + 8];
    assert_eq!(
        b.ui.widget::<Button>(house).unwrap().label_color,
        options::STORE_ONLY_CAPTION_COLOR
    );
    assert_eq!(
        b.ui.widget::<Button>(weather).unwrap().label_color,
        ac_ui::draw::Color::WHITE
    );
    // Headers come from the string table.
    let rows = list_rows(&b, options::CHARACTER_LIST_ID);
    assert_eq!(
        Some(text_of(&b.ui, rows[0])),
        caption("ID_CharacterOption_UIBehavior_Section")
    );
}

#[test]
#[ignore = "needs the retail dat files"]
fn a_character_option_click_applies_live_and_apply_saves_the_block() {
    let c = content();
    let mut b = bind_with(&c, live());
    b.c.show_page(&mut b.ui, OptionsPage::Character);
    let out = pump(&mut b);
    // Showing a list page applies it, and Apply saves the options block.
    assert!(out.contains(&Out::Opt(OptionsOutput::Applied)));
    let apply = footer(&b, OptionsPage::Character, options::APPLY_BUTTON_ID);
    let reset = footer(&b, OptionsPage::Character, options::RESET_BUTTON_ID);
    let defaults = footer(&b, OptionsPage::Character, options::DEFAULTS_BUTTON_ID);
    assert!(!enabled(&b, apply) && !enabled(&b, reset));
    assert!(enabled(&b, defaults));

    let boxes = checkboxes(&b, options::CHARACTER_LIST_ID);
    let weather = boxes[3 + 5];
    let was = b.ui.widget::<Button>(weather).unwrap().selected();
    b.ui.with::<Button, _>(weather, |w, ui| w.set_selected(ui, weather, !was));
    let out = click(&mut b, weather);
    assert_eq!(
        out,
        vec![Out::Opt(OptionsOutput::Changed {
            section: Section::Character(option_id::DISABLE_MOST_WEATHER_EFFECTS),
            persist: true
        })]
    );
    assert_eq!(
        b.c.live()
            .character_option(option_id::DISABLE_MOST_WEATHER_EFFECTS),
        Some(!was)
    );
    assert!(enabled(&b, apply) && enabled(&b, reset));

    let out = click(&mut b, apply);
    assert_eq!(out, vec![Out::Opt(OptionsOutput::Applied)]);
    assert!(!enabled(&b, apply) && !enabled(&b, reset));
    assert!(!b.c.page(OptionsPage::Character).changed());

    // Reset after another change puts it back and re-sends the old value.
    b.ui.with::<Button, _>(weather, |w, ui| w.set_selected(ui, weather, was));
    click(&mut b, weather);
    let out = click(&mut b, reset);
    assert!(out.contains(&Out::Opt(OptionsOutput::Changed {
        section: Section::Character(option_id::DISABLE_MOST_WEATHER_EFFECTS),
        persist: true
    })));
    assert_eq!(
        b.c.live()
            .character_option(option_id::DISABLE_MOST_WEATHER_EFFECTS),
        Some(!was)
    );
    assert!(b.ui.widget::<Button>(weather).unwrap().selected() != was);
}

#[test]
#[ignore = "needs the retail dat files"]
fn defaults_apply_every_client_default_without_committing() {
    let c = content();
    let mut b = bind_with(&c, live());
    b.c.show_page(&mut b.ui, OptionsPage::Character);
    pump(&mut b);
    let defaults = footer(&b, OptionsPage::Character, options::DEFAULTS_BUTTON_ID);
    click(&mut b, defaults);
    for entry in ac_runtime::character::OPTIONS {
        if [
            option_id::LOCK_UI,
            option_id::USE_MOUSE_TURNING,
            option_id::APPEAR_OFFLINE,
        ]
        .contains(&entry.id)
        {
            continue;
        }
        assert_eq!(
            b.c.live().character_option(entry.id),
            Some(entry.client_default),
            "{}",
            entry.name
        );
    }
}

#[test]
#[ignore = "needs the retail dat files"]
fn leaving_a_tab_resets_what_was_not_applied() {
    let c = content();
    let mut b = bind_with(&c, live());
    b.c.show_page(&mut b.ui, OptionsPage::Character);
    pump(&mut b);
    let boxes = checkboxes(&b, options::CHARACTER_LIST_ID);
    let first = boxes[0];
    let was = b.ui.widget::<Button>(first).unwrap().selected();
    b.ui.with::<Button, _>(first, |w, ui| w.set_selected(ui, first, !was));
    click(&mut b, first);
    assert!(b.c.page(OptionsPage::Character).changed());
    b.c.show_page(&mut b.ui, OptionsPage::Gameplay);
    pump(&mut b);
    assert!(!b.c.page(OptionsPage::Character).changed());
    assert_eq!(b.ui.widget::<Button>(first).unwrap().selected(), was);
    // Hiding the window resets the page shown.
    b.c.show_page(&mut b.ui, OptionsPage::Character);
    pump(&mut b);
    b.ui.with::<Button, _>(first, |w, ui| w.set_selected(ui, first, !was));
    click(&mut b, first);
    b.c.on_hidden(&mut b.ui);
    pump(&mut b);
    assert!(!b.c.page(OptionsPage::Character).changed());
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_server_seeding_the_options_reloads_the_page_shown_without_saving() {
    let c = content();
    let mut b = bind_with(&c, live());
    b.c.show_page(&mut b.ui, OptionsPage::Character);
    pump(&mut b);
    let mut l = b.c.live().clone();
    l.options1 ^= 0x0001_0000;
    let now = l
        .character_option(option_id::DISABLE_MOST_WEATHER_EFFECTS)
        .unwrap();
    b.c.set_live(l);
    b.c.on_server_options_seeded(&mut b.ui);
    let out = pump(&mut b);
    assert!(out.is_empty(), "{out:?}");
    let weather = checkboxes(&b, options::CHARACTER_LIST_ID)[3 + 5];
    assert_eq!(b.ui.widget::<Button>(weather).unwrap().selected(), now);
    assert!(!b.c.page(OptionsPage::Character).changed());
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_gameplay_buttons_ask_for_their_actions() {
    let c = content();
    let mut b = bind_with(&c, live());
    let find = |b: &Bound, id| b.layout.find(id).unwrap();
    let close = find(&b, 0x1000_0210);
    assert_eq!(
        click(&mut b, close),
        vec![Out::Close(ac_ui::panels::panel_id::OPTIONS)]
    );
    for (id, want) in [
        (0x1000_0203, OptionsOutput::ExitToCharacterSelection),
        (0x1000_0617, OptionsOutput::ExitGame),
        (0x1000_05CC, OptionsOutput::UseMouseTurningSettings),
        (
            0x1000_0206,
            OptionsOutput::SystemMessage(options::urgent_assistance_unavailable()),
        ),
        (
            0x1000_0207,
            OptionsOutput::SystemMessage(options::report_abuse_unavailable()),
        ),
        // OpenAC `OptionsPanelController`: Configure Keyboard opens the
        // keyboard screen.
        (0x1000_0204, OptionsOutput::ConfigureKeyboard),
    ] {
        let button = find(&b, id);
        assert_eq!(click(&mut b, button), vec![Out::Opt(want)], "{id:#x}");
    }
    // The help files do nothing (help is a web page).
    let help = find(&b, 0x1000_0205);
    assert!(click(&mut b, help).is_empty());
}

fn config_slider(b: &Bound, nth: usize) -> ElementId {
    let mut out = Vec::new();
    for row in list_rows(b, options::CONFIG_LIST_ID) {
        let mut stack = vec![row];
        while let Some(id) = stack.pop() {
            if b.ui
                .element(id)
                .is_some_and(|e| e.dat_element_id == 0x1000_021C)
            {
                out.push(id);
            }
            let mut children = b.ui.children(id);
            children.reverse();
            stack.extend(children);
        }
    }
    out[nth]
}

#[test]
#[ignore = "needs the retail dat files"]
fn a_volume_slider_writes_the_sound_section_in_its_own_unit() {
    let c = content();
    let mut b = bind_with(&c, live());
    b.c.show_page(&mut b.ui, OptionsPage::Configuration);
    pump(&mut b);
    // The first slider is the effects volume (the first trio).
    let sfx = config_slider(&b, 0);
    let out = send(&mut b, sfx, ElementMessage::ScalarChanged(0.25));
    assert_eq!(
        out,
        vec![Out::Opt(OptionsOutput::Changed {
            section: Section::Audio,
            persist: true
        })]
    );
    assert_eq!(b.c.live().settings.audio.sfx, 0.25);
    // The camera adjustment speed runs 5..80.
    // Three volumes, two mixer sliders, the stiffness, then it.
    let adjustment = config_slider(&b, 6);
    send(&mut b, adjustment, ElementMessage::ScalarChanged(0.5));
    assert_eq!(b.c.live().settings.camera.adjustment_speed, 42.5);
    // The voices slider writes whole numbers within 16..64.
    let voices = config_slider(&b, 3);
    send(&mut b, voices, ElementMessage::ScalarChanged(0.49));
    assert_eq!(b.c.live().settings.mixer.voice_count, 40);
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_vsync_and_resolution_rows_write_the_display_section() {
    let c = content();
    let mut b = bind_with(&c, live());
    b.c.show_page(&mut b.ui, OptionsPage::Configuration);
    pump(&mut b);
    let strings = c.strings();
    let caption = |key: &str| strings.resolve(0x2300_0003, compute_hash(key), 0);
    let boxes = checkboxes(&b, options::CONFIG_LIST_ID);
    let vsync = *boxes
        .iter()
        .find(|id| {
            b.ui.widget::<Button>(**id).unwrap().label
                == caption("ID_Rendering_SyncToDisplayRefresh")
        })
        .expect("the vsync row");
    assert!(
        b.ui.widget::<Button>(vsync).unwrap().selected(),
        "seeded from the settings (on)"
    );
    b.ui.with::<Button, _>(vsync, |w, ui| w.set_selected(ui, vsync, false));
    let out = click(&mut b, vsync);
    assert_eq!(
        out,
        vec![Out::Opt(OptionsOutput::Changed {
            section: Section::Display,
            persist: true
        })]
    );
    assert!(!b.c.live().settings.display.vsync);

    // The resolution menu offers the ladder and stores the name.
    let mut menus = Vec::new();
    for row in list_rows(&b, options::CONFIG_LIST_ID) {
        if let Some(m) = b.ui.find_descendant(row, 0x1000_0224) {
            menus.push(m);
        }
    }
    // The game's nine, then the atmospheric quality and the shadow filter.
    assert_eq!(menus.len(), 11);
    let resolution = menus[1];
    let items: Vec<String> =
        b.ui.widget::<Menu>(resolution)
            .unwrap()
            .items
            .iter()
            .map(|i| i.label.clone())
            .collect();
    assert_eq!(items, resolutions::AVAILABLE_RESOLUTIONS);
    assert_eq!(
        b.ui.widget::<Menu>(resolution).unwrap().button_label,
        "1280x720"
    );
    send(&mut b, resolution, ElementMessage::MenuSelected(3));
    assert_eq!(b.c.live().settings.display.resolution, "1920x1080");
    // The landscape draw distance stores its radius, not its index.
    // Sound features, resolution, profile, three texture menus, then it.
    let draw_distance = menus[6];
    send(&mut b, draw_distance, ElementMessage::MenuSelected(5));
    assert_eq!(b.c.live().settings.display.landscape_draw_distance, 25);
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_retail_mixer_dims_the_three_rows_it_overrides() {
    let c = content();
    let mut b = bind_with(&c, live());
    b.c.show_page(&mut b.ui, OptionsPage::Configuration);
    pump(&mut b);
    let boxes = checkboxes(&b, options::CONFIG_LIST_ID);
    let label = |b: &Bound, id: ElementId| b.ui.widget::<Button>(id).unwrap().label.clone();
    let retail = *boxes
        .iter()
        .find(|id| label(&b, **id).as_deref() == Some("Retail Mixer"))
        .unwrap();
    let priority = *boxes
        .iter()
        .find(|id| label(&b, **id).as_deref() == Some("Priority"))
        .unwrap();
    assert_eq!(
        b.ui.widget::<Button>(priority).unwrap().label_color,
        ac_ui::draw::Color::WHITE
    );
    b.ui.with::<Button, _>(retail, |w, ui| w.set_selected(ui, retail, true));
    let out = click(&mut b, retail);
    assert_eq!(
        out,
        vec![Out::Opt(OptionsOutput::Changed {
            section: Section::Mixer,
            persist: true
        })]
    );
    assert!(b.c.live().settings.mixer.retail_mixer);
    assert_eq!(
        b.ui.widget::<Button>(priority).unwrap().label_color,
        options::STORE_ONLY_CAPTION_COLOR
    );
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_chat_page_reads_its_opacity_defaults_and_captions_from_the_dats() {
    let c = content();
    let master = c.master_property();
    let defaults = chat_dat::opacity_defaults(c.dats(), master);
    assert!(defaults.is_some(), "the defaults collection resolves");
    let (default_caption, active_caption) =
        chat_dat::opacity_captions(c.dats(), master, c.strings()).expect("the caption catalog");
    assert!(default_caption.name.is_some_and(|n| !n.is_empty()));
    assert!(active_caption.name.is_some_and(|n| !n.is_empty()));
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_chat_filter_blocks_have_twelve_and_thirteen_rows() {
    let c = content();
    let b = bind_with(&c, live());
    let blocks: Vec<ElementId> = list_rows(&b, options::CHAT_LIST_ID)
        .into_iter()
        .filter(|r| b.ui.widget::<Bitfield64>(*r).is_some())
        .collect();
    assert_eq!(blocks.len(), 5);
    let counts: Vec<usize> = blocks
        .iter()
        .map(|id| b.ui.widget::<Bitfield64>(*id).unwrap().rows().len())
        .collect();
    assert_eq!(counts, [12, 13, 13, 13, 13]);
    // Each block is as tall as its rows, and the next row starts below it.
    let main = b.ui.widget::<Bitfield64>(blocks[0]).unwrap();
    let height = b.ui.element(blocks[0]).unwrap().height;
    let row_h = b.ui.element(main.rows()[0].row_root).unwrap().height;
    assert_eq!(height, row_h * 12.0);
    // Seeded from the main window's filter (all but the error class).
    assert_eq!(main.current(), (0xFBFF_FFFF, 0));
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_opacity_sliders_drag_each_other_and_save_when_the_drag_ends() {
    let c = content();
    let mut b = bind_with(&c, live());
    b.c.show_page(&mut b.ui, OptionsPage::Chat);
    pump(&mut b);
    let mut sliders = Vec::new();
    for row in list_rows(&b, options::CHAT_LIST_ID) {
        if let Some(s) = b.ui.find_descendant(row, 0x1000_021C) {
            sliders.push(s);
        }
    }
    assert_eq!(sliders.len(), 2);
    // Default 1.0 / active 1.0 (the settings' defaults): lowering the
    // active one drags the default one down with it.
    send(&mut b, sliders[1], ElementMessage::ScalarChanged(0.3));
    let chat = b.c.live().settings.chat;
    assert_eq!((chat.default_opacity, chat.active_opacity), (0.3, 0.3));
    assert_eq!(
        b.ui.widget::<Scrollbar>(sliders[0])
            .unwrap()
            .scalar_position(),
        0.3
    );
    assert_eq!(
        b.c.page(OptionsPage::Chat).current(0),
        Some(&OptionValue::Float(0.3))
    );
    let out = send(&mut b, sliders[1], ElementMessage::DragCompleted);
    assert_eq!(
        out,
        vec![Out::Opt(OptionsOutput::Changed {
            section: Section::Chat,
            persist: true
        })]
    );
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_ui_scale_row_follows_potato_mode_and_shows_its_percent() {
    let c = content();
    let mut b = bind_with(&c, live());
    b.c.show_page(&mut b.ui, OptionsPage::Configuration);
    pump(&mut b);
    // The row's caption carries the value; it is the slider after Potato
    // Mode's checkbox.
    let rows = list_rows(&b, options::CONFIG_LIST_ID);
    let labelled = |row: ElementId, label: &str| {
        let mut stack = vec![row];
        while let Some(id) = stack.pop() {
            if b.ui
                .widget::<Button>(id)
                .is_some_and(|w| w.label.as_deref() == Some(label))
            {
                return true;
            }
            stack.extend(b.ui.children(id));
        }
        false
    };
    let potato = rows
        .iter()
        .position(|r| labelled(*r, "Potato Mode"))
        .expect("the potato mode row");
    let row = rows[potato + 1];
    let caption = b.ui.find_descendant(row, 0x1000_021B).expect("its caption");
    assert_eq!(text_of(&b.ui, caption), "UI Scale (100%)");
    let slider = b.ui.find_descendant(row, 0x1000_021C).expect("its slider");
    // 50..300 in tens: 0.41 of the way is 152.5, which snaps to 150.
    let out = send(&mut b, slider, ElementMessage::ScalarChanged(0.41));
    assert_eq!(
        out,
        vec![Out::Opt(OptionsOutput::Changed {
            section: Section::Display,
            persist: true
        })]
    );
    assert_eq!(b.c.live().settings.display.ui_scale_percent, 150);
    assert_eq!(text_of(&b.ui, caption), "UI Scale (150%)");
    // The end of a drag settles the scale: it applies and saves.
    let out = send(&mut b, slider, ElementMessage::DragCompleted);
    assert_eq!(
        out,
        vec![Out::Opt(OptionsOutput::Changed {
            section: Section::Display,
            persist: true
        })]
    );
}

/// Modern mouse turning's two rows: off by default; Both Mouse Buttons
/// Run Forward is grey and refuses a click until Modern Mouse Turning is
/// on; while it is on, Use Mouse Turning refuses a click.
#[test]
#[ignore = "needs the retail dat files"]
fn modern_mouse_turning_gates_its_run_row_and_the_legacy_mouse_turning_row() {
    let c = content();
    let mut b = bind_with(&c, live());
    b.c.show_page(&mut b.ui, OptionsPage::Configuration);
    pump(&mut b);
    let strings = c.strings();
    let caption = |key: &str| strings.resolve(0x2300_0003, compute_hash(key), 0);
    let boxes = checkboxes(&b, options::CONFIG_LIST_ID);
    let label = |b: &Bound, id: ElementId| b.ui.widget::<Button>(id).unwrap().label.clone();
    let find = |b: &Bound, text: Option<String>| {
        *boxes
            .iter()
            .find(|id| label(b, **id) == text)
            .unwrap_or_else(|| panic!("the row {text:?}"))
    };
    let modern = find(&b, Some("Modern Mouse Turning".to_owned()));
    let both = find(&b, Some("Both Mouse Buttons Run Forward".to_owned()));
    let legacy = find(&b, caption("ID_Input_UseMouseTurning"));
    let selected = |b: &Bound, id: ElementId| b.ui.widget::<Button>(id).unwrap().selected();
    let color = |b: &Bound, id: ElementId| b.ui.widget::<Button>(id).unwrap().label_color;
    assert!(
        !selected(&b, modern) && !selected(&b, both),
        "off by default"
    );
    assert_eq!(color(&b, both), options::STORE_ONLY_CAPTION_COLOR);

    // Both buttons cannot be turned on without modern turning.
    b.ui.with::<Button, _>(both, |w, ui| w.set_selected(ui, both, true));
    assert!(click(&mut b, both).is_empty());
    assert!(!b.c.live().settings.camera.both_mouse_buttons_run_forward);
    assert!(!selected(&b, both), "the checkbox shows the refusal");

    // Modern turning on: both buttons become available.
    b.ui.with::<Button, _>(modern, |w, ui| w.set_selected(ui, modern, true));
    let out = click(&mut b, modern);
    assert_eq!(
        out,
        vec![Out::Opt(OptionsOutput::Changed {
            section: Section::Camera,
            persist: true
        })]
    );
    assert!(b.c.live().settings.camera.modern_mouse_turning);
    assert_eq!(color(&b, both), ac_ui::draw::Color::WHITE);
    b.ui.with::<Button, _>(both, |w, ui| w.set_selected(ui, both, true));
    click(&mut b, both);
    assert!(b.c.live().settings.camera.both_mouse_buttons_run_forward);

    // The legacy mouse turning row refuses while modern turning is on.
    b.ui.with::<Button, _>(legacy, |w, ui| w.set_selected(ui, legacy, true));
    assert!(click(&mut b, legacy).is_empty());
    assert!(!b.c.live().settings.camera.use_mouse_turning);
    assert!(!selected(&b, legacy));

    // Modern turning off again: the legacy row takes clicks again.
    b.ui.with::<Button, _>(modern, |w, ui| w.set_selected(ui, modern, false));
    click(&mut b, modern);
    assert_eq!(color(&b, both), options::STORE_ONLY_CAPTION_COLOR);
    b.ui.with::<Button, _>(legacy, |w, ui| w.set_selected(ui, legacy, true));
    click(&mut b, legacy);
    assert!(b.c.live().settings.camera.use_mouse_turning);
}
