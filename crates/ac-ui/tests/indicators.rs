//! The indicator bar, the effects panels, the link-status and vitae panels.
//! Ported from OpenAC `tests/AcDream.App.Tests/UI/Layout/IndicatorBarControllerTests.cs`,
//! `IndicatorDetailPanelControllerTests.cs` and `EffectsUiControllerTests.cs`.
//! OpenAC's fixture cases read the retail dats here (`real_dats`); its
//! spellbook events are the spellbook handed to each update, its clicks the
//! window controller's element messages.

mod common;

use std::collections::HashMap;
use std::sync::Arc;

use ac_core::items::ClientObject;
use ac_core::items::burden::{encumbrance_capacity, load_ratio};
use ac_core::spells::{ActiveEnchantmentRecord, SpellTable, Spellbook};
use ac_runtime::session::LinkStatus;
use ac_ui::host::{PanelController, UiHost};
use ac_ui::import::ImportedLayout;
use ac_ui::panels::indicators::{
    self as ind, EffectRowTemplate, EffectsController, EffectsView, EffectsWindowController,
    LinkStatusStrings, VitaeStrings,
};
use ac_ui::root::{ElementMessage, UiRoot};
use ac_ui::tree::{Element, ElementId};
use ac_ui::widgets::{ItemList, Panel, TemplateSlot, Text};
use common::items::{Objects, PLAYER};

fn link(connected: bool, age: f64) -> LinkStatus {
    LinkStatus {
        connected,
        seconds_since_last_packet: age,
        packet_loss_percent: 0.0,
        round_trip_seconds: None,
    }
}

fn record(spell: u32, layer: u32, duration: f64, bucket: u32) -> ActiveEnchantmentRecord {
    ActiveEnchantmentRecord {
        bucket,
        spell_category: spell,
        ..ActiveEnchantmentRecord::new(spell, layer, duration, 1)
    }
}

fn book(csv: &str) -> Spellbook {
    Spellbook::new(Some(Arc::new(SpellTable::load_from_csv(csv))))
}

const BOON: &str = "Spell ID,Name,Flags [Hex]\n42,Boon,0x4\n";

#[test]
fn the_link_indicator_uses_the_retail_thresholds() {
    for (connected, age, state) in [
        (true, 4.999, ind::CONNECTION_GOOD_STATE),
        (true, 5.0, ind::CONNECTION_UNCERTAIN_STATE),
        (true, 19.999, ind::CONNECTION_UNCERTAIN_STATE),
        (true, 20.0, ind::CONNECTION_BAD_STATE),
        (true, 39.999, ind::CONNECTION_BAD_STATE),
        (true, 40.0, ind::CONNECTION_DISCONNECTED_STATE),
        (false, 0.0, ind::CONNECTION_DISCONNECTED_STATE),
    ] {
        assert_eq!(
            ind::link_state(&link(connected, age)),
            state,
            "{connected} {age}"
        );
    }
}

#[test]
fn burden_uses_the_exact_retail_load_boundaries() {
    let capacity = encumbrance_capacity(10, 0);
    for (burden, state) in [
        (1499, ind::UNENCUMBERED_STATE),
        (1500, ind::ENCUMBERED_STATE),
        (2999, ind::ENCUMBERED_STATE),
        (3000, ind::HEAVILY_ENCUMBERED_STATE),
    ] {
        assert_eq!(
            ind::burden_state(load_ratio(capacity, burden)),
            state,
            "{burden}"
        );
    }
}

#[test]
fn the_link_status_text_has_the_loss_and_the_round_trip() {
    let strings = LinkStatusStrings::english();
    let body = strings.body(&LinkStatus {
        connected: true,
        seconds_since_last_packet: 0.25,
        packet_loss_percent: 1.25,
        round_trip_seconds: Some(0.123),
    });
    assert!(body.contains("Link Indicator"));
    assert!(body.contains("1.25"));
    assert!(body.contains("123"));
    assert!(strings.body(&link(true, 0.0)).ends_with("????"));
}

#[test]
fn vitae_uses_the_death_level_the_pool_and_the_retail_threshold() {
    let strings = VitaeStrings::english();
    let mut objects = Objects::new();
    let mut p = ClientObject::new(PLAYER);
    p.properties.ints.insert(ind::DEATH_LEVEL_PROPERTY, 10);
    p.properties.ints.insert(ind::VITAE_CP_POOL_PROPERTY, 100);
    objects.add(p);
    let mut b = book(BOON);
    assert!(ind::vitae_text(&strings, Some(&b), &objects.t, PLAYER).contains("full strength"));
    let mut vitae = record(666, 1, -1.0, 4);
    vitae.stat_mod_value = Some(0.9);
    b.on_enchantment_added(vitae);
    let text = ind::vitae_text(&strings, Some(&b), &objects.t, PLAYER);
    assert!(text.contains("lost 10%"), "{text}");
    assert!(text.contains("health, stamina, mana, and skills are temporarily reduced by 10%"));
    assert!(text.contains("earn 379 more experience"), "{text}");
    assert_eq!(ind::vitae_cp_pool_threshold(0.9, 10), 479);
    assert_eq!(ind::format_experience(61_000_000), "61,000,000");
}

#[test]
fn time_left_reads_minutes_or_hours_and_nothing_for_a_permanent_effect() {
    let e = record(42, 1, 60.0, 1);
    assert_eq!(ind::format_remaining(&e, 0.0), "1:00");
    assert_eq!(ind::format_remaining(&e, 3.0), "0:57");
    assert_eq!(
        ind::format_remaining(&record(42, 1, 3725.0, 1), 0.0),
        "1:02:05"
    );
    assert_eq!(ind::format_remaining(&record(42, 1, -1.0, 1), 0.0), "");
    assert_eq!(ind::format_remaining(&e, 100.0), "0:00");
}

// ── The effects panel on a synthetic layout ─────────────────────────────

struct Effects {
    host: UiHost,
    list: ElementId,
    info: ElementId,
}

fn effects_layout(with_info: bool) -> (Effects, ImportedLayout) {
    let mut host = UiHost::new();
    host.root.set_size(800.0, 600.0);
    let ui = &mut host.root;
    let r = ui.root();
    let root = ui.add(r, Element::at(0.0, 0.0, 160.0, 100.0), Panel::default());
    let list = ItemList::create(ui, root, Element::at(0.0, 0.0, 160.0, 64.0));
    let mut by_id: HashMap<u32, ElementId> = [(ind::LIST_ID, list)].into_iter().collect();
    let mut te = Element::at(0.0, 64.0, 160.0, 32.0);
    Text::configure(&mut te);
    let info = ui.add(root, te, Text::default());
    if with_info {
        by_id.insert(ind::INFO_TEXT_ID, info);
    }
    (
        Effects { host, list, info },
        ImportedLayout::from_parts(root, by_id),
    )
}

/// A row like the authored template: an icon host, the name and the time.
fn build_row(ui: &mut UiRoot, _layout: u32, _element: u32) -> Option<ElementId> {
    let root = ui.insert(Element::at(0.0, 0.0, 300.0, 32.0), Panel::default());
    let mut icon = Element::at(0.0, 0.0, 32.0, 32.0);
    icon.dat_element_id = ind::ROW_ICON_ID;
    ui.add(root, icon, Panel::default());
    for (id, x, w) in [
        (ind::ROW_LABEL_ID, 37.0, 188.0),
        (ind::ROW_DURATION_ID, 225.0, 50.0),
    ] {
        let mut e = Element::at(x, 0.0, w, 32.0);
        e.dat_element_id = id;
        Text::configure(&mut e);
        ui.add(root, e, Text::default());
    }
    Some(root)
}

const TEMPLATE: EffectRowTemplate = EffectRowTemplate {
    layout: ind::EFFECTS_LAYOUT_ID,
    element: ind::ROW_TEMPLATE_ID,
    width: 300.0,
    height: 32.0,
};

use common::items::TestIcons;

fn texts(ui: &UiRoot, id: ElementId) -> Vec<String> {
    ui.widget::<Text>(id)
        .unwrap()
        .content_lines()
        .into_iter()
        .map(|l| l.text)
        .collect()
}

fn duration_of(ui: &UiRoot, cell: ElementId) -> Vec<String> {
    let d = ui.find_descendant(cell, ind::ROW_DURATION_ID).unwrap();
    texts(ui, d)
}

impl Effects {
    fn window(&mut self, layout: &ImportedLayout) -> EffectsWindowController {
        let c = EffectsController::bind(
            &mut self.host.root,
            layout,
            true,
            TEMPLATE,
            "SELECT A SPELL".to_owned(),
        )
        .unwrap();
        EffectsWindowController::new(c, ac_ui::panels::panel_id::POSITIVE_EFFECTS)
    }

    fn update(&mut self, c: &mut EffectsWindowController, book: &Spellbook, now: f64, show: bool) {
        let view = EffectsView {
            spellbook: book,
            now,
            show_duration: show,
        };
        c.update(
            &mut self.host.root,
            &view,
            &mut build_row,
            &mut TestIcons::default(),
        );
    }

    fn click(&mut self, c: &mut EffectsWindowController, cell: ElementId) {
        assert!(c.on_element_message(
            &mut self.host.root,
            cell,
            &ElementMessage::Clicked { x: 1, y: 1 }
        ));
    }

    fn cells(&self) -> Vec<ElementId> {
        self.host
            .root
            .widget::<ItemList>(self.list)
            .unwrap()
            .cells()
            .to_vec()
    }

    fn selected(&self, cell: ElementId) -> bool {
        self.host
            .root
            .widget::<TemplateSlot>(cell)
            .unwrap()
            .selected
    }
}

#[test]
fn a_rebuild_selects_nothing_and_forgets_a_removed_selection() {
    let (mut s, layout) = effects_layout(true);
    let mut c = s.window(&layout);
    assert!(
        !s.host
            .root
            .widget::<Text>(s.info)
            .unwrap()
            .preserve_end_on_layout
    );
    let mut b = book(BOON);
    b.on_enchantment_added(record(42, 1, 60.0, 1));
    b.on_enchantment_added(record(42, 2, 60.0, 4));
    b.on_enchantment_added(record(42, 3, 60.0, 8));
    s.update(&mut c, &b, 0.0, true);
    assert_eq!(c.effects.selected_spell(), None);
    assert_eq!(texts(&s.host.root, s.info), ["SELECT A SPELL"]);
    let cells = s.cells();
    assert_eq!(cells.len(), 1, "only the enchantment in effect");
    assert_eq!(duration_of(&s.host.root, cells[0]), ["1:00"]);
    s.click(&mut c, cells[0]);
    s.update(&mut c, &b, 0.0, true);
    assert_eq!(c.effects.selected_spell(), Some(42));
    assert_eq!(texts(&s.host.root, s.info), ["Boon", "", ""]);

    b.on_enchantment_removed(1, 42);
    s.update(&mut c, &b, 0.0, true);
    assert_eq!(c.effects.selected_spell(), None);
}

#[test]
fn the_time_left_clears_and_returns_as_the_option_flips() {
    let (mut s, layout) = effects_layout(true);
    let mut c = s.window(&layout);
    let mut b = book(BOON);
    b.on_enchantment_added(record(42, 1, 60.0, 1));
    s.update(&mut c, &b, 0.0, true);
    let cell = s.cells()[0];
    assert_eq!(duration_of(&s.host.root, cell), ["1:00"]);
    s.update(&mut c, &b, 2.0, false);
    assert_eq!(duration_of(&s.host.root, cell), [""]);
    s.update(&mut c, &b, 3.0, true);
    assert_eq!(duration_of(&s.host.root, cell), ["0:57"]);
}

#[test]
fn layers_of_one_spell_share_the_selection() {
    let (mut s, layout) = effects_layout(false);
    let mut c = s.window(&layout);
    let mut b = book(BOON);
    let mut first = record(42, 1, 60.0, 1);
    first.spell_category = 100;
    let mut second = record(42, 2, 60.0, 2);
    second.spell_category = 101;
    second.caster_guid = 2;
    b.on_enchantment_added(first);
    b.on_enchantment_added(second);
    s.update(&mut c, &b, 0.0, true);
    let cells = s.cells();
    assert_eq!(cells.len(), 2);
    s.click(&mut c, cells[0]);
    s.update(&mut c, &b, 0.0, true);
    assert_eq!(c.effects.selected_spell(), Some(42));
    assert!(s.selected(cells[0]) && s.selected(cells[1]));
    s.click(&mut c, cells[1]);
    s.update(&mut c, &b, 0.0, true);
    assert_eq!(c.effects.selected_spell(), None);
    assert!(!s.selected(cells[0]) && !s.selected(cells[1]));
}

#[test]
fn a_permanent_effect_leaves_the_time_column_empty() {
    let (mut s, layout) = effects_layout(false);
    let mut c = s.window(&layout);
    let mut b = book(BOON);
    b.on_enchantment_added(record(42, 1, -1.0, 1));
    s.update(&mut c, &b, 0.0, true);
    assert_eq!(duration_of(&s.host.root, s.cells()[0]), [""]);
}

#[test]
fn a_selected_effect_wraps_its_description() {
    let (mut s, layout) = effects_layout(true);
    s.host.root.element_mut(s.info).unwrap().width = 96.0;
    let mut c = s.window(&layout);
    let mut b = book(
        "Spell ID,Name,Description,Flags [Hex]\n42,Boon,This description contains enough words to wrap across several lines.,0x4\n",
    );
    b.on_enchantment_added(record(42, 1, 60.0, 1));
    s.update(&mut c, &b, 0.0, true);
    let cell = s.cells()[0];
    s.click(&mut c, cell);
    s.update(&mut c, &b, 0.0, true);
    let lines = texts(&s.host.root, s.info);
    assert_eq!(lines[0], "Boon");
    assert_eq!(lines[1], "");
    assert!(lines.len() > 3);
    // Eight pixels a character without a font: 96 pixels hold twelve.
    assert!(lines.iter().all(|l| l.chars().count() <= 12), "{lines:?}");
    let t = s.host.root.widget::<Text>(s.info).unwrap();
    assert!(t.wheel_scroll_enabled);
    assert!(!s.host.root.element(s.info).unwrap().click_through);
}

#[test]
fn a_harmful_panel_lists_only_harmful_spells() {
    let (mut s, layout) = effects_layout(false);
    let c = EffectsController::bind(
        &mut s.host.root,
        &layout,
        false,
        TEMPLATE,
        "SELECT A SPELL".to_owned(),
    )
    .unwrap();
    let mut c = EffectsWindowController::new(c, ac_ui::panels::panel_id::NEGATIVE_EFFECTS);
    let mut b = book("Spell ID,Name,Flags [Hex]\n42,Boon,0x4\n43,Bane,0x0\n");
    b.on_enchantment_added(record(42, 1, 60.0, 1));
    b.on_enchantment_added(record(43, 1, 60.0, 2));
    s.update(&mut c, &b, 0.0, true);
    let cells = s.cells();
    assert_eq!(cells.len(), 1);
    let label = s
        .host
        .root
        .find_descendant(cells[0], ind::ROW_LABEL_ID)
        .unwrap();
    assert_eq!(texts(&s.host.root, label), ["Bane"]);
}

/// The list made in a plain host takes its margins when it is made, so a
/// panel that grew while hidden (a saved size, a resize before it was
/// first shown) shows the list at the panel's full height. OpenAC
/// `EffectsUiController.Create` captures the baseline right after adding
/// the list.
#[test]
fn a_list_made_in_its_host_follows_a_panel_that_grew_while_hidden() {
    let mut host = UiHost::new();
    host.root.set_size(800.0, 600.0);
    let ui = &mut host.root;
    let r = ui.root();
    let window = ui.add(r, Element::at(0.0, 0.0, 300.0, 100.0), Panel::default());
    let list_host = ui.add(window, Element::at(0.0, 0.0, 300.0, 64.0), Panel::default());
    let layout =
        ImportedLayout::from_parts(window, [(ind::LIST_ID, list_host)].into_iter().collect());
    ui.element_mut(window).unwrap().visible = false;
    let c = EffectsController::bind(ui, &layout, true, TEMPLATE, "SELECT A SPELL".into()).unwrap();
    let list = c.list();
    assert_ne!(list, list_host, "the list is made in the host");
    ui.element_mut(list_host).unwrap().height = 300.0;
    ui.element_mut(window).unwrap().visible = true;
    host.draw((800.0, 600.0), &ac_ui::draw::NoSprites);
    assert_eq!(host.root.element(list).unwrap().height, 300.0);
}

mod real_dats {
    use super::*;
    use ac_ui::content::DatContent;
    use ac_ui::font::DEFAULT_FONT_ID;
    use ac_ui::import::{LayoutSource, import, import_infos_rooted, import_rooted};
    use ac_ui::panels::main_panels::RESTORE_PREVIOUS_PROPERTY;
    use ac_ui::panels::{DatRowTemplates, PanelOutput, panel_id};
    use ac_ui::states;
    use common::items::panel_outputs;
    use std::path::PathBuf;

    fn content() -> DatContent {
        let dir = std::env::var_os("OPENAC_DAT_DIR")
            .map(PathBuf::from)
            .or_else(ac_dat::default_dat_dir)
            .expect("no dat directory: set OPENAC_DAT_DIR");
        DatContent::new(Arc::new(
            ac_dat::DatCollection::open(dir).expect("open the retail dats"),
        ))
        .expect("master property table")
    }

    struct Bar {
        host: UiHost,
        bar: ind::IndicatorBarController,
    }

    fn bar(c: &DatContent) -> Bar {
        let mut host = UiHost::new();
        host.root.set_size(800.0, 600.0);
        let layout = import(
            &mut host.root,
            c,
            ind::BAR_LAYOUT_ID,
            c.font(DEFAULT_FONT_ID),
        )
        .unwrap();
        let r = host.root.root();
        host.root.add_child(r, layout.root);
        let bar = ind::IndicatorBarController::bind(&mut host.root, &layout, "End?".into(), 0.0)
            .expect("all seven indicators");
        Bar { host, bar }
    }

    impl Bar {
        fn state(&self, id: u32) -> Option<u32> {
            self.host.root.retail_state(self.bar.button(id).unwrap())
        }
        fn enabled(&self, id: u32) -> bool {
            self.host
                .root
                .element(self.bar.button(id).unwrap())
                .unwrap()
                .enabled()
        }
        fn update(
            &mut self,
            book: &Spellbook,
            objects: &Objects,
            strength: i32,
            l: LinkStatus,
            now: f64,
        ) {
            let view = ind::IndicatorsView {
                spellbook: Some(book),
                objects: &objects.t,
                player: PLAYER,
                strength: Some(strength),
                link: l,
                now,
            };
            self.bar.update(&mut self.host.root, &view);
        }
        fn click(&mut self, id: u32) -> Vec<PanelOutput> {
            let b = self.bar.button(id).unwrap();
            assert!(self.bar.on_element_message(
                &mut self.host.root,
                b,
                &ElementMessage::Clicked { x: 1, y: 1 }
            ));
            self.host.pump();
            panel_outputs(&mut self.host)
        }
    }

    fn toggled(outputs: &[PanelOutput]) -> Vec<u32> {
        outputs
            .iter()
            .filter_map(|o| match o {
                PanelOutput::TogglePanel(p) => Some(*p),
                _ => None,
            })
            .collect()
    }

    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_authored_bar_builds_all_seven_indicator_buttons() {
        let c = content();
        let info = ac_ui::import::import_infos(&c, ind::BAR_LAYOUT_ID).unwrap();
        assert_eq!(info.id, 0x1000_0610);
        assert_eq!((info.width, info.height), (150.0, 30.0));
        let mut b = bar(&c);
        let ctx = ac_ui::import::BuildContext {
            source: &c,
            default_font: c.font(DEFAULT_FONT_ID),
            source_layout_did: ind::BAR_LAYOUT_ID,
            media_seconds: 0.0,
        };
        b.bar.attach_press_highlights(&mut b.host.root, &info, &ctx);
        // Each indicator carries its authored press highlight (OpenAC
        // attaches them after the import; the importer builds them here).
        for id in [
            ind::LINK_BUTTON_ID,
            ind::HELPFUL_BUTTON_ID,
            ind::HARMFUL_BUTTON_ID,
            ind::VITAE_BUTTON_ID,
            ind::BURDEN_BUTTON_ID,
            ind::MINI_GAME_BUTTON_ID,
        ] {
            let button = b.bar.button(id).unwrap();
            assert!(
                b.host
                    .root
                    .find_descendant(button, ind::PRESS_HIGHLIGHT_ID)
                    .is_some(),
                "{id:#x}"
            );
        }
    }

    #[test]
    #[ignore = "needs the retail dat files"]
    fn enchantments_light_the_helpful_harmful_and_vitae_indicators_and_open_their_panels() {
        let c = content();
        let mut b = bar(&c);
        let mut objects = Objects::new();
        objects.add(ClientObject::new(PLAYER));
        let mut book = book("Spell ID,Name,Flags [Hex]\n42,Boon,0x4\n43,Bane,0x0\n50,Vitae,0x0\n");
        b.update(&book, &objects, 10, link(true, 0.0), 0.0);
        for id in [
            ind::HELPFUL_BUTTON_ID,
            ind::HARMFUL_BUTTON_ID,
            ind::VITAE_BUTTON_ID,
        ] {
            assert!(!b.enabled(id));
        }
        book.on_enchantment_added(record(42, 1, 60.0, 1));
        let mut bane = record(43, 2, 60.0, 2);
        bane.spell_category = 99;
        book.on_enchantment_added(bane);
        b.update(&book, &objects, 10, link(true, 0.0), 0.0);
        assert!(b.enabled(ind::HELPFUL_BUTTON_ID));
        assert!(b.enabled(ind::HARMFUL_BUTTON_ID));
        let mut opened = toggled(&b.click(ind::HELPFUL_BUTTON_ID));
        opened.extend(toggled(&b.click(ind::HARMFUL_BUTTON_ID)));
        assert_eq!(
            opened,
            [panel_id::POSITIVE_EFFECTS, panel_id::NEGATIVE_EFFECTS]
        );

        let mut full = record(50, 3, 60.0, 4);
        full.stat_mod_value = Some(1.0);
        book.on_enchantment_added(full);
        b.update(&book, &objects, 10, link(true, 0.0), 0.0);
        assert!(!b.enabled(ind::VITAE_BUTTON_ID));
        let mut lost = record(50, 3, 60.0, 4);
        lost.stat_mod_value = Some(0.99);
        book.on_enchantment_added(lost);
        b.update(&book, &objects, 10, link(true, 0.0), 0.0);
        assert!(b.enabled(ind::VITAE_BUTTON_ID));
        assert_eq!(b.state(ind::VITAE_BUTTON_ID), Some(states::NORMAL));
        assert_eq!(toggled(&b.click(ind::VITAE_BUTTON_ID)), [panel_id::VITAE]);
        book.on_enchantment_removed(3, 50);
        b.update(&book, &objects, 10, link(true, 0.0), 0.0);
        assert!(!b.enabled(ind::VITAE_BUTTON_ID));
        // It is drawn ghosted again, not left lit.
        assert_eq!(b.state(ind::VITAE_BUTTON_ID), Some(states::GHOSTED));

        book.on_enchantment_removed(1, 42);
        book.on_enchantment_removed(2, 43);
        b.update(&book, &objects, 10, link(true, 0.0), 0.0);
        for id in [ind::HELPFUL_BUTTON_ID, ind::HARMFUL_BUTTON_ID] {
            assert!(!b.enabled(id));
            assert_eq!(b.state(id), Some(states::GHOSTED), "{id:#x}");
        }
    }

    #[test]
    #[ignore = "needs the retail dat files"]
    fn burden_reads_the_exact_retail_boundaries_and_follows_strength() {
        let c = content();
        let mut b = bar(&c);
        let book = book(BOON);
        for (burden, state) in [
            (1499, ind::UNENCUMBERED_STATE),
            (1500, ind::ENCUMBERED_STATE),
            (2999, ind::ENCUMBERED_STATE),
            (3000, ind::HEAVILY_ENCUMBERED_STATE),
        ] {
            let mut objects = Objects::new();
            let mut p = ClientObject::new(PLAYER);
            p.properties.ints.insert(5, burden);
            objects.add(p);
            b.update(&book, &objects, 10, link(true, 0.0), 0.0);
            assert_eq!(b.state(ind::BURDEN_BUTTON_ID), Some(state), "{burden}");
        }
        let mut objects = Objects::new();
        let mut p = ClientObject::new(PLAYER);
        p.properties.ints.insert(5, 1600);
        objects.add(p);
        b.update(&book, &objects, 10, link(true, 0.0), 0.0);
        assert_eq!(b.state(ind::BURDEN_BUTTON_ID), Some(ind::ENCUMBERED_STATE));
        b.update(&book, &objects, 20, link(true, 0.0), 0.0);
        assert_eq!(
            b.state(ind::BURDEN_BUTTON_ID),
            Some(ind::UNENCUMBERED_STATE)
        );
        assert_eq!(
            toggled(&b.click(ind::BURDEN_BUTTON_ID)),
            [panel_id::CHARACTER_INFORMATION]
        );
    }

    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_link_updates_every_four_seconds_and_a_bad_link_flashes() {
        let c = content();
        let mut b = bar(&c);
        let book = book(BOON);
        let mut objects = Objects::new();
        objects.add(ClientObject::new(PLAYER));
        for (age, state) in [
            (4.999, ind::CONNECTION_GOOD_STATE),
            (20.0, ind::CONNECTION_BAD_STATE),
        ] {
            let mut fresh = bar(&c);
            fresh.update(&book, &objects, 10, link(true, age), 4.0);
            assert_eq!(fresh.state(ind::LINK_BUTTON_ID), Some(state));
        }
        let l = link(true, 20.0);
        b.update(&book, &objects, 10, l, 3.999);
        assert_eq!(
            b.state(ind::LINK_BUTTON_ID),
            Some(ind::CONNECTION_GOOD_STATE)
        );
        b.update(&book, &objects, 10, l, 4.0);
        assert_eq!(
            b.state(ind::LINK_BUTTON_ID),
            Some(ind::CONNECTION_BAD_STATE)
        );
        b.update(&book, &objects, 10, l, 4.749);
        assert_eq!(
            b.state(ind::LINK_BUTTON_ID),
            Some(ind::CONNECTION_BAD_STATE)
        );
        b.update(&book, &objects, 10, l, 4.75);
        assert_eq!(
            b.state(ind::LINK_BUTTON_ID),
            Some(ind::CONNECTION_UNCERTAIN_STATE)
        );
        b.update(&book, &objects, 10, l, 5.5);
        assert_eq!(
            b.state(ind::LINK_BUTTON_ID),
            Some(ind::CONNECTION_BAD_STATE)
        );
        assert_eq!(
            toggled(&b.click(ind::LINK_BUTTON_ID)),
            [panel_id::LINK_STATUS]
        );
    }

    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_mini_game_and_end_session_indicators_use_their_authored_buttons() {
        let c = content();
        let mut b = bar(&c);
        assert!(!b.enabled(ind::MINI_GAME_BUTTON_ID));
        assert!(b.enabled(ind::END_CHARACTER_SESSION_BUTTON_ID));
        assert_eq!(
            b.state(ind::END_CHARACTER_SESSION_BUTTON_ID),
            Some(states::NORMAL)
        );
        b.bar.set_mini_game_active(&mut b.host.root, true);
        assert!(b.enabled(ind::MINI_GAME_BUTTON_ID));
        assert_eq!(
            toggled(&b.click(ind::MINI_GAME_BUTTON_ID)),
            [panel_id::MINI_GAME]
        );
        b.bar.set_mini_game_active(&mut b.host.root, false);
        assert!(!b.enabled(ind::MINI_GAME_BUTTON_ID));
        let out = b.click(ind::END_CHARACTER_SESSION_BUTTON_ID);
        assert!(matches!(
            out.as_slice(),
            [PanelOutput::Confirm { message, .. }] if message == "End?"
        ));
    }

    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_authored_effect_row_has_icon_name_time_and_selection_states() {
        let c = content();
        let row = import_infos_rooted(&c, ind::EFFECTS_LAYOUT_ID, ind::ROW_TEMPLATE_ID).unwrap();
        assert_eq!((row.width, row.height), (300.0, 32.0));
        assert!(row.state(states::NORMAL).is_some());
        assert!(row.state(states::HIGHLIGHT).is_some());
        let find = |id| {
            fn f(
                i: &ac_ui::element_info::ElementInfo,
                id: u32,
            ) -> Option<&ac_ui::element_info::ElementInfo> {
                if i.id == id {
                    return Some(i);
                }
                i.children.iter().find_map(|c| f(c, id))
            }
            f(&row, id).map(|i| (i.x, i.width))
        };
        assert_eq!(find(ind::ROW_ICON_ID), Some((0.0, 32.0)));
        assert_eq!(find(ind::ROW_LABEL_ID), Some((37.0, 188.0)));
        assert_eq!(find(ind::ROW_DURATION_ID), Some((225.0, 50.0)));
    }

    #[test]
    #[ignore = "needs the retail dat files"]
    fn both_authored_effects_panels_bind_list_info_scrollbars_and_close() {
        let c = content();
        for positive in [true, false] {
            let root_id = if positive {
                ind::POSITIVE_ROOT_ID
            } else {
                ind::NEGATIVE_ROOT_ID
            };
            let info = import_infos_rooted(&c, ind::EFFECTS_LAYOUT_ID, root_id).unwrap();
            assert_eq!(info.kind, 0x1000_001B);
            assert_eq!((info.width, info.height), (300.0, 362.0));
            assert_eq!(
                info.effective_bool(RESTORE_PREVIOUS_PROPERTY, None) == Some(true),
                positive
            );
            let mut host = UiHost::new();
            host.root.set_size(800.0, 600.0);
            let layout = import_rooted(
                &mut host.root,
                &c,
                ind::EFFECTS_LAYOUT_ID,
                root_id,
                c.font(DEFAULT_FONT_ID),
            )
            .unwrap();
            let r = host.root.root();
            host.root.add_child(r, layout.root);
            let effects = EffectsController::bind(
                &mut host.root,
                &layout,
                positive,
                TEMPLATE,
                "SELECT A SPELL".into(),
            )
            .unwrap();
            let host_el = layout.find(ind::LIST_ID).unwrap();
            assert_eq!(host.root.parent(effects.list()), Some(host_el));
            for id in [ind::LIST_SCROLLBAR_ID, ind::INFO_SCROLLBAR_ID] {
                let bar = layout.find(id).unwrap();
                assert!(host.root.widget::<ac_ui::widgets::Scrollbar>(bar).is_some());
            }
            let mut w = EffectsWindowController::new(
                effects,
                if positive {
                    panel_id::POSITIVE_EFFECTS
                } else {
                    panel_id::NEGATIVE_EFFECTS
                },
            );
            let close = w.effects.close_button().unwrap();
            assert!(w.on_element_message(
                &mut host.root,
                close,
                &ElementMessage::Clicked { x: 1, y: 1 }
            ));
            host.pump();
            let out = panel_outputs(&mut host);
            assert!(matches!(out.as_slice(), [PanelOutput::ClosePanel(p)] if *p == w.panel));

            // Rows from the authored template.
            let mut b = book(if positive {
                "Spell ID,Name,Flags [Hex]\n42,Boon,0x4\n"
            } else {
                "Spell ID,Name,Flags [Hex]\n42,Bane,0x0\n"
            });
            b.on_enchantment_added(record(42, 1, 60.0, 1));
            let view = EffectsView {
                spellbook: &b,
                now: 0.0,
                show_duration: true,
            };
            let mut rows = DatRowTemplates::new(&c, c.font(DEFAULT_FONT_ID));
            w.update(&mut host.root, &view, &mut rows, &mut TestIcons::default());
            let cells = w.effects.row_cells();
            assert_eq!(cells.len(), 1);
            assert_eq!(duration_of(&host.root, cells[0]), ["1:00"]);
        }
    }

    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_detail_panels_are_retail_main_panel_pages() {
        let c = content();
        for (layout_id, root, kind, text, close) in [
            (
                ind::LINK_LAYOUT_ID,
                ind::LINK_ROOT_ID,
                0x1000_001D,
                ind::LINK_MAIN_TEXT_ID,
                ind::CLOSE_ID,
            ),
            (
                ind::VITAE_LAYOUT_ID,
                ind::VITAE_ROOT_ID,
                0x1000_0020,
                ind::VITAE_MAIN_TEXT_ID,
                ind::CLOSE_ID,
            ),
            (
                ind::MINI_GAME_LAYOUT_ID,
                ind::MINI_GAME_ROOT_ID,
                0x1000_001E,
                0x1000_016E,
                ind::MINI_GAME_CLOSE_ID,
            ),
        ] {
            let info = import_infos_rooted(&c, layout_id, root).unwrap();
            assert_eq!(info.id, root);
            assert_eq!(info.kind, kind);
            assert_eq!((info.width, info.height), (300.0, 362.0));
            fn has(i: &ac_ui::element_info::ElementInfo, id: u32) -> bool {
                i.id == id || i.children.iter().any(|c| has(c, id))
            }
            assert!(has(&info, text) && has(&info, close), "{root:#x}");
            assert_eq!(
                info.effective_bool(RESTORE_PREVIOUS_PROPERTY, None),
                Some(true)
            );
        }
        assert_eq!(
            (panel_id::LINK_STATUS, panel_id::MINI_GAME, panel_id::VITAE),
            (8, 9, 15)
        );
    }

    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_link_status_panel_pings_on_show_refreshes_and_pings_again_after_two_minutes() {
        let c = content();
        let mut host = UiHost::new();
        host.root.set_size(800.0, 600.0);
        let layout = import_rooted(
            &mut host.root,
            &c,
            ind::LINK_LAYOUT_ID,
            ind::LINK_ROOT_ID,
            c.font(DEFAULT_FONT_ID),
        )
        .unwrap();
        let mut ls =
            ind::LinkStatusController::bind(&mut host.root, &layout, LinkStatusStrings::english())
                .unwrap();
        let l = LinkStatus {
            connected: true,
            seconds_since_last_packet: 0.25,
            packet_loss_percent: 1.25,
            round_trip_seconds: Some(0.123),
        };
        let pings = |host: &mut UiHost| {
            host.pump();
            panel_outputs(host)
                .iter()
                .filter(|o| matches!(o, PanelOutput::RequestLinkPing))
                .count()
        };
        ls.on_shown(&mut host.root);
        ls.update(&mut host.root, &l, 0.0);
        assert_eq!(pings(&mut host), 1);
        let text = texts(&host.root, ls.text()).join(" ");
        assert!(text.contains("Link Indicator") && text.contains("1.25") && text.contains("123"));
        ls.update(&mut host.root, &l, 0.0);
        ls.update(&mut host.root, &l, 119.0);
        assert_eq!(pings(&mut host), 0);
        ls.update(&mut host.root, &l, 124.0);
        assert_eq!(pings(&mut host), 1);
        ls.on_hidden(&mut host.root);
        ls.update(&mut host.root, &l, 300.0);
        assert_eq!(pings(&mut host), 0);
    }

    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_vitae_panel_shows_full_strength_then_the_loss() {
        let c = content();
        let mut host = UiHost::new();
        host.root.set_size(800.0, 600.0);
        let layout = import_rooted(
            &mut host.root,
            &c,
            ind::VITAE_LAYOUT_ID,
            ind::VITAE_ROOT_ID,
            c.font(DEFAULT_FONT_ID),
        )
        .unwrap();
        let mut v =
            ind::VitaeController::bind(&mut host.root, &layout, VitaeStrings::english()).unwrap();
        let mut objects = Objects::new();
        let mut p = ClientObject::new(PLAYER);
        p.properties.ints.insert(ind::DEATH_LEVEL_PROPERTY, 10);
        p.properties.ints.insert(ind::VITAE_CP_POOL_PROPERTY, 100);
        objects.add(p);
        let mut b = book(BOON);
        v.update(&mut host.root, Some(&b), &objects.t, PLAYER);
        assert!(
            texts(&host.root, v.text())
                .join(" ")
                .contains("full strength")
        );
        let mut vitae = record(666, 1, -1.0, 4);
        vitae.stat_mod_value = Some(0.9);
        b.on_enchantment_added(vitae);
        v.update(&mut host.root, Some(&b), &objects.t, PLAYER);
        let text = texts(&host.root, v.text()).join(" ");
        assert!(text.contains("lost 10%") && text.contains("379"), "{text}");
    }
}
