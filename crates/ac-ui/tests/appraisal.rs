//! The examination window and the item report.
//!
//! Ported from OpenAC `tests/AcDream.App.Tests/UI/Layout/ItemAppraisalTextFormatterTests.cs`
//! (the sections ported: value and burden, tinkering, spells, special
//! properties, use, capacity and lock, craftsman, sale and rarity,
//! description) and `AppraisalUiControllerTests.cs` (the item page, the
//! title, the spell page), on a synthetic layout shaped like the retail
//! examination window 0x2100006B (the real one is imported in `real_dats`).

mod common;

use std::collections::HashMap;
use std::sync::Arc;

use ac_core::items::{ClientObject, ItemType};
use ac_core::spells::{MagicSchool, SpellMetadata};
use ac_proto::appraise::{CreatureProfile, HookProfile, IdentifyObjectResponse};
use ac_runtime::inventory::{AppraisalView, ItemCommand, ObjectRef};
use ac_runtime::session::SessionCommand;
use ac_ui::ElementId;
use ac_ui::draw::{Color, TextureRef};
use ac_ui::event::kind;
use ac_ui::host::UiHost;
use ac_ui::import::ImportedLayout;
use ac_ui::panels::PanelOutput;
use ac_ui::panels::appraisal::{
    self, AppraisalController, AppraisalNames, AppraisalPage, ItemAppraisalReport, NameTables,
    NoNames, ReportFragment, ReportStyle, Separator, SpellExamineComponent,
};
use ac_ui::widgets::{Button, CatalogSlot, DatElement, ScrollSource, Scrollbar, Text};
use common::items::*;
use common::*;

macro_rules! assert_dbg_eq {
    ($a:expr, $b:expr $(,)?) => {
        assert_eq!(format!("{:?}", $a), format!("{:?}", $b))
    };
}

fn parsed(f: impl FnOnce(&mut IdentifyObjectResponse)) -> IdentifyObjectResponse {
    let mut a = IdentifyObjectResponse {
        guid: 0x5000_0001,
        success: true,
        ..IdentifyObjectResponse::default()
    };
    f(&mut a);
    a
}

fn spell(id: u32, name: &str, description: &str) -> SpellMetadata {
    let mut m = SpellMetadata::new(id, name, id);
    m.school = "War Magic".into();
    m.description = description.into();
    m
}

fn spells(list: Vec<SpellMetadata>) -> impl Fn(u32) -> Option<SpellMetadata> {
    move |id| list.iter().find(|m| m.spell_id == id).cloned()
}

fn none(_: u32) -> Option<SpellMetadata> {
    None
}

/// No row templates (the item page builds none).
fn no_rows(_: &mut ac_ui::root::UiRoot, _: u32, _: u32) -> Option<ElementId> {
    None
}

/// The spell page's component cell as the examination layout authors it:
/// a 20 by 20 image with the missing overlay as its one child.
fn component_cells(ui: &mut ac_ui::root::UiRoot, layout: u32, element: u32) -> Option<ElementId> {
    if (layout, element) != (appraisal::LAYOUT_ID, appraisal::SPELL_COMPONENT_TEMPLATE_ID) {
        return None;
    }
    let dat = |id: u32| {
        let mut e = at(0.0, 0.0, 20.0, 20.0);
        e.dat_element_id = id;
        let info = Arc::new(ac_ui::element_info::ElementInfo {
            id,
            width: 20.0,
            height: 20.0,
            ..ac_ui::element_info::ElementInfo::default()
        });
        (e, DatElement::new(info))
    };
    let (e, d) = dat(appraisal::SPELL_COMPONENT_TEMPLATE_ID);
    let cell = ui.insert(e, d);
    let (e, d) = dat(appraisal::SPELL_COMPONENT_MISSING_ID);
    ui.add(cell, e, d);
    Some(cell)
}

/// Whether each cell's missing overlay shows.
fn missing_overlays(w: &Window, cells: &[ElementId]) -> Vec<bool> {
    cells
        .iter()
        .map(|&c| {
            let m = w
                .host
                .root
                .find_descendant(c, appraisal::SPELL_COMPONENT_MISSING_ID)
                .unwrap();
            w.host.root.element(m).unwrap().visible
        })
        .collect()
}

fn object(id: u32, name: &str, t: ItemType) -> ClientObject {
    let mut o = ClientObject::new(id);
    o.name = name.into();
    o.item_type = t;
    o
}

fn report(obj: &ClientObject, a: &IdentifyObjectResponse) -> String {
    appraisal::item_report(obj, a, &none, &NoNames).to_string()
}

// ManaDrain_IsAnIntervalWhateverTheSignOfTheRate.
#[test]
fn a_mana_drain_is_an_interval_whatever_the_sign_of_the_rate() {
    for (rate, expected) in [
        (-0.05, "Mana Cost: 1 point per 20 seconds."),
        (0.05, "Mana Cost: 1 point per 20 seconds."),
        (-0.4, "Mana Cost: 1 point per 3 seconds."),
        (-1.0, "Mana Cost: 1 point per 1 seconds."),
    ] {
        let obj = object(0x5000_0011, "Amulet", ItemType::JEWELRY);
        let a = parsed(|a| {
            a.properties.floats.insert(5, rate);
            a.spell_book = vec![102];
        });
        let armor = spell(102, "Impenetrability VI", "Increases the target's armor.");
        let text = appraisal::item_report(&obj, &a, &spells(vec![armor]), &NoNames).to_string();
        assert!(text.contains(expected), "{rate}: {text}");
    }
}

// WeaponAndMagic_AreProjectedInRetailOrderWithDatDescriptions (the
// sections ported; the weapon lines are not).
#[test]
fn magic_is_listed_in_retail_order_with_its_descriptions() {
    let obj = object(0x5000_0001, "Flaming Sword", ItemType::MELEE_WEAPON);
    let a = parsed(|a| {
        let p = &mut a.properties;
        p.ints.insert(19, 12_500);
        p.ints.insert(5, 450);
        p.ints.insert(105, 8);
        p.ints.insert(106, 300);
        p.ints.insert(107, 250);
        p.ints.insert(108, 500);
        p.ints.insert(117, 20);
        p.strings.insert(14, "Use this item to attack.".into());
        p.strings
            .insert(16, "A finely balanced enchanted blade.".into());
        a.spell_book = vec![101, 0x8000_0066];
    });
    let table = spells(vec![
        spell(101, "Flame Bolt VI", "Hurls a bolt of flame."),
        spell(102, "Impenetrability VI", "Increases the target's armor."),
    ]);
    let text = appraisal::item_report(&obj, &a, &table, &NoNames).to_string();
    for expected in [
        "Value: 12,500",
        "Burden: 450",
        "Workmanship: Utterly flawless (8)",
        "Spells: Flame Bolt VI",
        "Spellcraft: 300.",
        "Mana: 250 / 500.",
        "Mana Cost: 20.\n(Can be reduced by the Mana Conversion skill).",
        "Spell Descriptions:",
        "~ Flame Bolt VI: Hurls a bolt of flame.",
        "Enchantments:",
        "~ Impenetrability VI: Increases the target's armor.",
        "Use this item to attack.",
    ] {
        assert!(text.contains(expected), "{expected:?} in {text}");
    }
    assert!(!text.contains("Spells: Flame Bolt VI, Impenetrability VI"));
    assert!(text.find("Spells:").unwrap() < text.find("Spell Descriptions:").unwrap());
    assert!(text.ends_with("A finely balanced enchanted blade."));
}

// NpcLooksLikeObjectCreature_UsesRetailUnknownsAndSuppressesNegativeCapacity.
#[test]
fn a_creature_object_shows_unknowns_and_no_negative_capacity() {
    let mut obj = object(0x5000_0005, "Black Phyntos Hive", ItemType::CREATURE);
    obj.items_capacity = -1;
    obj.containers_capacity = -1;
    let hive = "A hollowed out tree trunk that has a Phyntos Wasp Hive in it.";
    let a = parsed(|a| {
        a.properties.strings.insert(16, hive.into());
    });
    let r = appraisal::item_report(&obj, &a, &none, &NoNames);
    assert_eq!(
        r.to_string(),
        format!("Value: ???\nBurden: Unknown\n\n\n\n{hive}")
    );
    let f = |text: &str, separator| ReportFragment {
        text: text.into(),
        separator,
        style: ReportStyle::Normal,
    };
    assert_eq!(
        r.fragments,
        [
            f("Value: ???", Separator::None),
            f("Burden: Unknown", Separator::Line),
            f("", Separator::Line),
            f("", Separator::Line),
            f(hive, Separator::Paragraph),
        ]
    );
}

// HookWithHookProfile_SuppressesHooksOwnCapacity.
#[test]
fn a_hook_with_a_hook_profile_hides_its_own_capacity() {
    let mut obj = object(0x5000_0015, "Weapon Hook", ItemType::CONTAINER);
    obj.items_capacity = 24;
    obj.containers_capacity = 1;
    obj.hook_item_types = ItemType::WEAPON.0;
    obj.hook_type = 4;
    let a = parsed(|a| {
        a.hook_profile = Some(HookProfile {
            flags: 0,
            valid_locations: 0,
            ammo_type: 0,
        });
    });
    assert!(!report(&obj, &a).contains("Can hold"));
}

// OrdinaryContainer_PresentsCapacityAsRetailParagraph.
#[test]
fn a_container_shows_its_capacity_as_a_paragraph() {
    let mut obj = object(0x5000_0006, "Backpack", ItemType::CONTAINER);
    obj.items_capacity = 24;
    obj.containers_capacity = 1;
    assert_eq!(
        report(&obj, &parsed(|_| {})),
        "Value: ???\nBurden: Unknown\n\n\n\nCan hold up to 24 items and 1 containers."
    );
}

// BookCapacity_UsesRetailUsedThenTotalPropertyOrder.
#[test]
fn a_book_shows_pages_used_of_total() {
    let obj = object(0x5000_000B, "Book", ItemType::WRITABLE);
    let a = parsed(|a| {
        a.properties.ints.insert(174, 3);
        a.properties.ints.insert(175, 10);
    });
    assert!(report(&obj, &a).ends_with("3 of 10 pages full."));
}

// TextLayout_PreservesRetailPaletteAcrossParagraphs.
#[test]
fn the_layout_keeps_the_palette_across_paragraphs() {
    let normal = Color::WHITE;
    let beneficial = Color::rgba(0.0, 1.0, 0.0, 1.0);
    let detrimental = Color::rgba(1.0, 0.0, 0.0, 1.0);
    let r = ItemAppraisalReport {
        fragments: vec![
            ReportFragment {
                text: "Normal".into(),
                separator: Separator::None,
                style: ReportStyle::Normal,
            },
            ReportFragment {
                text: "High".into(),
                separator: Separator::Line,
                style: ReportStyle::Beneficial,
            },
            ReportFragment {
                text: "Low".into(),
                separator: Separator::Paragraph,
                style: ReportStyle::Detrimental,
            },
        ],
    };
    let lines = appraisal::shape_report(
        &r,
        &[normal, beneficial, detrimental],
        normal,
        1000.0,
        &|s| s.len() as f32 * 8.0,
    );
    let texts: Vec<&str> = lines.iter().map(|l| l.text.as_str()).collect();
    assert_eq!(texts, ["Normal", "High", "", "Low"]);
    assert_eq!(lines[0].color, normal);
    assert_eq!(lines[1].color, beneficial);
    assert_eq!(lines[3].color, detrimental);
    // A long line wraps at the width.
    let wrapped = appraisal::shape_report(&r, &[], normal, 40.0, &|s| s.len() as f32 * 8.0);
    assert_eq!(wrapped[0].text, "Norma");
}

// LockInformation_UsesRetailPresenceRulesWordingAndParagraphs.
#[test]
fn a_lock_is_described_in_retail_wording_and_paragraphs() {
    let obj = object(0x5000_0009, "Locked Chest", ItemType::CONTAINER);
    let a = parsed(|a| {
        a.properties.bools.insert(3, true);
        a.properties.ints.insert(38, 212);
        a.properties.ints.insert(173, 42);
    });
    let r = appraisal::item_report(&obj, &a, &none, &NoNames);
    let has = |text: &str| {
        r.fragments
            .iter()
            .any(|f| f.text == text && f.separator == Separator::Paragraph)
    };
    assert!(has("Locked"));
    assert!(has("The lock looks difficult to pick (Resistance 212)."));
    let a = parsed(|a| {
        a.properties.bools.insert(3, true);
    });
    assert!(report(&obj, &a).ends_with("Locked\n\nYou can't tell how hard the lock is to pick."));
    let a = parsed(|a| {
        a.properties.bools.insert(3, false);
    });
    assert!(report(&obj, &a).ends_with("Unlocked"));
}

// MissingLockedFlag_ReportsLockpickBonusInsteadOfInventingLockState.
#[test]
fn without_a_locked_flag_the_lockpick_bonus_shows() {
    let obj = object(0x5000_000A, "Lockpick", ItemType::MISC);
    let a = parsed(|a| {
        a.properties.ints.insert(38, -7);
    });
    let text = report(&obj, &a);
    assert!(text.contains("Bonus to Lockpick Skill: -7"));
    assert!(!text.contains("Locked") && !text.contains("Unlocked"));
}

// ArmorRequirementsAndUses_UseRetailLabelsAndResistanceBands (the
// craftsman and sale lines; the armor and requirement lines are not
// ported).
#[test]
fn the_craftsman_and_an_unsellable_item_are_named() {
    let obj = object(0x5000_0002, "Platemail Hauberk", ItemType::ARMOR);
    let a = parsed(|a| {
        a.properties.bools.insert(69, false);
        a.properties.strings.insert(25, "Tester".into());
    });
    let text = report(&obj, &a);
    assert!(text.contains("Created by Tester."));
    assert!(text.contains("This item cannot be sold."));
    let a = parsed(|a| {
        a.properties.strings.insert(25, "Tester".into());
        a.properties.bools.insert(85, true);
        a.properties.ints.insert(17, 42);
    });
    let text = report(&obj, &a);
    assert!(!text.contains("Created by"));
    assert!(text.ends_with("Rare #42"));
}

// SpecialProperties_AreCombinedUsingRetailNames.
#[test]
fn special_properties_are_combined_with_retail_names() {
    let obj = object(0x5000_0004, "Imbued Katar", ItemType::MELEE_WEAPON);
    let a = parsed(|a| {
        let p = &mut a.properties;
        p.ints.insert(279, 3);
        p.ints.insert(292, 4);
        p.ints.insert(179, 0x0000_0481);
        p.ints.insert(114, 1);
        p.ints.insert(33, 1);
        p.floats.insert(167, 90.0);
        p.bools.insert(99, true);
        p.bools.insert(100, true);
    });
    let text = report(&obj, &a);
    for expected in [
        "You can only carry 3 of these items.",
        "Cooldown When Used: 1m 30s ",
        "Cleave: 4 enemies in front arc.",
        "Properties: Critical Strike, Cold Rending, +1 Melee Defense, Attuned, Bonded, Ivoryable, Dyeable",
        "This item cannot be further imbued.",
    ] {
        assert!(text.contains(expected), "{expected:?} in {text}");
    }
}

#[test]
fn a_slayer_and_resistance_cleaving_use_the_names() {
    let obj = object(0x5000_0004, "Katar", ItemType::MELEE_WEAPON);
    let names = NameTables {
        creatures: HashMap::from([(2, "Olthoi".to_owned())]),
        ..NameTables::default()
    };
    let a = parsed(|a| {
        a.properties.ints.insert(166, 2);
        a.properties.floats.insert(157, 0.1);
        a.properties.ints.insert(263, 0x3);
    });
    let text = appraisal::item_report(&obj, &a, &none, &names).to_string();
    assert!(text.contains("Properties: Olthoi slayer, Resistance Cleaving: Slashing/Piercing"));
}

#[test]
fn a_decorated_description_names_workmanship_material_and_gems() {
    let obj = object(0x5000_0004, "Ring", ItemType::JEWELRY);
    let names = NameTables {
        materials: HashMap::from([(0x0B, "Agate".to_owned()), (0x26, "Ruby".to_owned())]),
        ..NameTables::default()
    };
    let a = parsed(|a| {
        let p = &mut a.properties;
        p.strings.insert(16, "Agate Ring".into());
        p.ints.insert(172, 1 | 4);
        p.ints.insert(105, 5);
        p.ints.insert(131, 0x0B);
        p.ints.insert(177, 3);
        p.ints.insert(178, 0x26);
    });
    let text = appraisal::item_report(&obj, &a, &none, &names).to_string();
    assert!(
        text.ends_with("Magnificent Agate Ring, set with 3 Rubies"),
        "{text}"
    );
}

#[test]
fn the_helpers_format_as_retail() {
    assert_eq!(appraisal::group_thousands(1_234_567), "1,234,567");
    assert_eq!(appraisal::group_thousands(-950), "-950");
    assert_eq!(appraisal::format_delta_time(90.0), "1m 30s ");
    assert_eq!(appraisal::format_delta_time(0.0), "0s ");
    assert_eq!(
        appraisal::format_expiry(3_661),
        "1 hours, 1 minutes, 1 seconds."
    );
    assert_eq!(
        appraisal::damage_type_name(0x1000_0040),
        "Electrical/Prismatic"
    );
    assert_eq!(appraisal::workmanship_adjective(0), "");
    assert_eq!(appraisal::workmanship_adjective(11), "Priceless");
}

// ── The window ──────────────────────────────────────────────────────────

const ITEM: u32 = 0x5000_0001;

struct Window {
    host: UiHost,
    ids: HashMap<u32, ElementId>,
    objects: Objects,
}

fn text_element(
    ui: &mut ac_ui::root::UiRoot,
    parent: ElementId,
    x: f32,
    y: f32,
    w: f32,
    h: f32,
) -> ElementId {
    ui.add(parent, at(x, y, w, h), Text::default())
}

fn window() -> Window {
    window_with(|_, _, _| {})
}

/// The synthetic window, with `extra` adding elements to the creature page
/// before the controller binds.
fn window_with(
    extra: impl FnOnce(&mut ac_ui::root::UiRoot, ElementId, &mut HashMap<u32, ElementId>),
) -> Window {
    let mut host = UiHost::new();
    host.root.set_size(800.0, 600.0);
    let ui = &mut host.root;
    let r = ui.root();
    let root = panel(ui, r, at(0.0, 0.0, 400.0, 500.0));
    let mut ids = HashMap::new();
    ids.insert(
        appraisal::TITLE_ID,
        text_element(ui, root, 0.0, 0.0, 300.0, 20.0),
    );
    let info = Arc::new(ac_ui::element_info::ElementInfo {
        id: appraisal::CLOSE_ID,
        width: 16.0,
        height: 16.0,
        ..ac_ui::element_info::ElementInfo::default()
    });
    ids.insert(
        appraisal::CLOSE_ID,
        ui.add(
            root,
            at(380.0, 0.0, 16.0, 16.0),
            Button::new(info, None, Vec::new(), 0.0),
        ),
    );
    let item = panel(ui, root, at(0.0, 30.0, 400.0, 400.0));
    ids.insert(appraisal::ITEM_PANEL_ID, item);
    ids.insert(
        appraisal::ITEM_TEXT_ID,
        text_element(ui, item, 0.0, 0.0, 380.0, 300.0),
    );
    ids.insert(
        appraisal::ITEM_SCROLLBAR_ID,
        ui.add(item, at(382.0, 0.0, 16.0, 300.0), Scrollbar::default()),
    );
    let creature = panel(ui, root, at(0.0, 30.0, 400.0, 400.0));
    ids.insert(appraisal::CREATURE_PANEL_ID, creature);
    ids.insert(
        appraisal::CREATURE_INFO_ID,
        panel(ui, creature, at(0.0, 0.0, 400.0, 40.0)),
    );
    ids.insert(
        appraisal::CHARACTER_INFO_ID,
        panel(ui, creature, at(0.0, 0.0, 400.0, 40.0)),
    );
    let spell_page = panel(ui, root, at(0.0, 30.0, 400.0, 400.0));
    ids.insert(appraisal::SPELL_PANEL_ID, spell_page);
    for (i, id) in [
        appraisal::SPELL_SCHOOL_TEXT_ID,
        appraisal::SPELL_MANA_TEXT_ID,
        appraisal::SPELL_DURATION_TEXT_ID,
        appraisal::SPELL_RANGE_TEXT_ID,
    ]
    .into_iter()
    .enumerate()
    {
        ids.insert(
            id,
            text_element(ui, spell_page, 40.0, i as f32 * 20.0, 300.0, 20.0),
        );
    }
    ids.insert(
        appraisal::SPELL_DISPLAY_TEXT_ID,
        text_element(ui, spell_page, 0.0, 100.0, 380.0, 200.0),
    );
    ids.insert(
        appraisal::SPELL_ICON_ID,
        panel(ui, spell_page, at(0.0, 0.0, 32.0, 32.0)),
    );
    ids.insert(
        appraisal::SPELL_FORMULA_LIST_ID,
        panel(ui, spell_page, at(190.0, 320.0, 20.0, 20.0)),
    );
    let item_text = ids[&appraisal::ITEM_TEXT_ID];
    ui.widget_mut::<Text>(item_text).unwrap().font_color_palette = vec![
        Color::WHITE,
        Color::rgba(0.0, 1.0, 0.0, 1.0),
        Color::rgba(1.0, 0.0, 0.0, 1.0),
    ];
    extra(ui, creature, &mut ids);
    let layout = ImportedLayout::from_parts(root, ids.clone());
    ui.register_window("examine", root, None, 0).unwrap();
    let c = AppraisalController::bind(ui, &layout).expect("binds");
    host.attach_controller("examine", Box::new(c)).unwrap();
    let mut objects = Objects::new();
    objects.player(102, 7);
    Window { host, ids, objects }
}

impl Window {
    fn with<R>(
        &mut self,
        f: impl FnOnce(&mut AppraisalController, &mut ac_ui::root::UiRoot) -> R,
    ) -> R {
        self.host
            .with_controller::<AppraisalController, _>("examine", f)
            .unwrap()
    }

    fn apply(&mut self, a: IdentifyObjectResponse, names: &dyn AppraisalNames) -> bool {
        let view = AppraisalView {
            name: None,
            response: a,
            present_in_ui: true,
            opens_window: true,
        };
        let objects = &self.objects.t;
        self.host
            .with_controller::<AppraisalController, _>("examine", |c, ui| {
                c.apply(ui, &view, objects, &none, names, &mut no_rows)
            })
            .unwrap()
    }

    fn lines(&self, id: u32) -> Vec<String> {
        self.host
            .root
            .widget::<Text>(self.ids[&id])
            .unwrap()
            .content_lines()
            .into_iter()
            .map(|l| l.text)
            .collect()
    }

    fn visible(&self, id: u32) -> bool {
        self.host.root.element(self.ids[&id]).unwrap().visible
    }
}

// ItemResponse_UsesAuthoredItemSubviewTitleAndScrollbars (the inscription
// is not ported).
#[test]
fn an_item_answer_shows_the_item_page_title_and_report() {
    let mut w = window();
    w.objects
        .add(object(ITEM, "Atlan Weapon", ItemType::MELEE_WEAPON));
    assert!(w.apply(
        parsed(|a| {
            a.guid = ITEM;
            a.properties.ints.insert(19, 1_250);
            a.properties.ints.insert(5, 350);
            a.properties
                .strings
                .insert(16, "A finely balanced weapon.".into());
        }),
        &NoNames,
    ));
    assert_eq!(w.with(|c, _| c.page()), AppraisalPage::Item);
    assert_eq!(w.with(|c, _| c.current_object()), ITEM);
    assert!(w.visible(appraisal::ITEM_PANEL_ID));
    assert!(!w.visible(appraisal::CREATURE_PANEL_ID));
    assert!(!w.visible(appraisal::SPELL_PANEL_ID));
    assert_eq!(w.lines(appraisal::TITLE_ID), ["Atlan Weapon"]);
    let item_text = w.ids[&appraisal::ITEM_TEXT_ID];
    let t = w.host.root.widget::<Text>(item_text).unwrap();
    assert_eq!(t.vertical_justify, ac_ui::text::VJustify::Top);
    let report = w.lines(appraisal::ITEM_TEXT_ID).join("\n");
    assert!(report.contains("Value: 1,250"));
    assert!(report.contains("Burden: 350"));
    assert!(report.contains("A finely balanced weapon."));
    let bar = w
        .host
        .root
        .widget::<Scrollbar>(w.ids[&appraisal::ITEM_SCROLLBAR_ID])
        .unwrap();
    assert!(matches!(bar.model, ScrollSource::Element(e) if e == item_text));
    // Unknown objects are not shown.
    assert!(!w.apply(parsed(|a| a.guid = 0x5000_0999), &NoNames));
    let close = w.ids[&appraisal::CLOSE_ID];
    w.host.root.fire_event(kind::CLICK, close, None);
    w.host.pump();
    assert_dbg_eq!(panel_outputs(&mut w.host), [PanelOutput::CloseExamination]);
}

// The item text wraps again at its new width when the window is resized,
// as OpenAC's item-text layout cache does when its width changes.
#[test]
fn the_item_report_wraps_again_when_the_window_is_widened() {
    let mut w = window();
    w.objects
        .add(object(ITEM, "Atlan Weapon", ItemType::MELEE_WEAPON));
    let words = "a finely balanced weapon with a long description ".repeat(4);
    assert!(w.apply(
        parsed(|a| {
            a.guid = ITEM;
            a.properties.strings.insert(16, words.trim_end().into());
        }),
        &NoNames,
    ));
    let longest = |w: &Window| {
        w.lines(appraisal::ITEM_TEXT_ID)
            .iter()
            .map(|l| l.chars().count())
            .max()
            .unwrap()
    };
    // Eight pixels a character without a font: 380 pixels hold 47.
    assert!(longest(&w) <= 47);
    let item_text = w.ids[&appraisal::ITEM_TEXT_ID];
    w.host.root.element_mut(item_text).unwrap().width = 760.0;
    w.host.tick(0.016, 16, None);
    assert!(longest(&w) > 47, "{:?}", w.lines(appraisal::ITEM_TEXT_ID));
}

fn show_examination(w: &mut Window, shown: bool) {
    if shown {
        w.host.root.show_window("examine");
    } else {
        w.host.root.hide_window("examine");
    }
    w.host.pump();
    let _ = panel_outputs(&mut w.host);
}

fn follow(w: &mut Window, selected: Option<u32>) -> Vec<PanelOutput> {
    w.with(|c, ui| c.follow_selection(ui, selected));
    panel_outputs(&mut w.host)
}

// OpenAC `AppraisalUiController.HandleSelectionChanged`: while the window
// shows, a new selection is examined and an empty one closes the window;
// while it is hidden a change does nothing.
#[test]
fn the_examination_follows_the_selection_while_it_shows() {
    let mut w = window();
    show_examination(&mut w, false);
    assert!(follow(&mut w, Some(0x5000_0010)).is_empty());
    assert!(follow(&mut w, Some(0x5000_0011)).is_empty(), "hidden");
    show_examination(&mut w, true);
    assert_dbg_eq!(
        follow(&mut w, Some(0x5000_0012)),
        [PanelOutput::Session(SessionCommand::Item(
            ItemCommand::Appraise(ObjectRef::Guid(0x5000_0012))
        ))]
    );
    assert_dbg_eq!(follow(&mut w, None), [PanelOutput::CloseExamination]);
    show_examination(&mut w, false);
    assert!(follow(&mut w, Some(0x5000_0013)).is_empty(), "hidden");
}

// OpenAC `AppraisalUiController.HandleSelectionChanged` runs on every
// change, so changes the window hears together are each followed: away
// and back examines both, in order; cleared and back closes the window,
// and the change after the close finds it hidden.
#[test]
fn every_selection_change_is_followed_in_order() {
    let appraise = |guid| {
        PanelOutput::Session(SessionCommand::Item(ItemCommand::Appraise(
            ObjectRef::Guid(guid),
        )))
    };
    let mut w = window();
    show_examination(&mut w, true);
    let changes = |w: &mut Window, changes: &[Option<u32>]| {
        w.with(|c, ui| {
            for &selected in changes {
                c.follow_selection(ui, selected);
            }
        });
        panel_outputs(&mut w.host)
    };
    assert_dbg_eq!(
        changes(&mut w, &[Some(0x5000_0012), Some(0x5000_0011)]),
        [appraise(0x5000_0012), appraise(0x5000_0011)]
    );
    assert_dbg_eq!(
        changes(&mut w, &[None, Some(0x5000_0011)]),
        [PanelOutput::CloseExamination]
    );
}

// OpenAC `AppraisalUiController.HandleInputAction`: the examine key closes
// the window while it shows and is taken; hidden, it is not the window's.
#[test]
fn the_examine_key_closes_the_examination_while_it_shows() {
    use ac_ui::input::InputAction;
    let mut w = window();
    show_examination(&mut w, false);
    assert!(!w.with(|c, ui| c.handle_input_action(ui, InputAction::SelectionExamine)));
    assert!(panel_outputs(&mut w.host).is_empty());
    show_examination(&mut w, true);
    assert!(!w.with(|c, ui| c.handle_input_action(ui, InputAction::UseSelected)));
    assert!(panel_outputs(&mut w.host).is_empty());
    assert!(w.with(|c, ui| c.handle_input_action(ui, InputAction::SelectionExamine)));
    assert_dbg_eq!(panel_outputs(&mut w.host), [PanelOutput::CloseExamination]);
}

// ItemResponse_TitleUsesRetailMaterialDecoratedAppropriateName.
#[test]
fn the_title_puts_the_material_in_front_of_the_name() {
    let mut w = window();
    let mut boots = object(ITEM, "Steel Toed Boots", ItemType::CLOTHING);
    boots.material_type = Some(77);
    w.objects.add(boots);
    let names = NameTables {
        materials: HashMap::from([(77, "Reed Shark Hide".to_owned())]),
        ..NameTables::default()
    };
    assert!(w.apply(parsed(|a| a.guid = ITEM), &names));
    assert_eq!(
        w.lines(appraisal::TITLE_ID),
        ["Reed Shark Hide Steel Toed Boots"]
    );
    // A stack shows its count before the (plural) name.
    w.objects.get_mut(ITEM).stack_size = 5;
    assert!(w.apply(parsed(|a| a.guid = ITEM), &names));
    assert!(w.lines(appraisal::TITLE_ID)[0].starts_with("5 Reed Shark Hide Steel Toed Boots"));
}

// CreatureResponse_SelectsCreatureSubviewAndRefreshesInCombatWithoutBusy
// and CharacterResponse_* (the page choice; the header and rows are the
// real-dat test `the_creature_and_character_pages_fill_their_header_and_rows`).
#[test]
fn a_creature_or_character_answer_opens_its_page() {
    let mut w = window();
    w.objects
        .add(object(0x5000_0010, "Drudge", ItemType::CREATURE));
    let creature = CreatureProfile {
        flags: 0,
        health: 10,
        health_max: 10,
        attributes: None,
        attribute_highlights: None,
    };
    assert!(w.apply(
        parsed(|a| {
            a.guid = 0x5000_0010;
            a.creature_profile = Some(creature);
        }),
        &NoNames,
    ));
    assert_eq!(w.with(|c, _| c.page()), AppraisalPage::Creature);
    assert!(w.visible(appraisal::CREATURE_PANEL_ID));
    assert!(w.visible(appraisal::CREATURE_INFO_ID));
    assert!(!w.visible(appraisal::CHARACTER_INFO_ID));
    assert!(!w.visible(appraisal::ITEM_PANEL_ID));
    assert!(w.apply(
        parsed(|a| {
            a.guid = 0x5000_0010;
            a.creature_profile = Some(creature);
            a.properties.ints.insert(0x105, 1);
        }),
        &NoNames,
    ));
    assert_eq!(w.with(|c, _| c.page()), AppraisalPage::Character);
    assert!(w.visible(appraisal::CHARACTER_INFO_ID));
    assert!(!w.visible(appraisal::CREATURE_INFO_ID));
}

// CreatureResponse_SelectsCreatureSubviewAndRefreshesInCombatWithoutBusy
// (the window half; the runtime's refresh is `ac-runtime`
// `inventory::tests::a_refresh_asks_again_about_the_shown_object_without_a_busy_count`):
// shown, on a creature, in combat, the page asks again every 0.75 seconds;
// hidden it never does.
#[test]
fn the_creature_page_asks_again_every_three_quarter_seconds_in_combat_while_shown() {
    use ac_ui::host::PanelController;
    let mut w = window();
    w.objects
        .add(object(0x5000_0010, "Drudge Slinker", ItemType::CREATURE));
    assert!(w.apply(
        parsed(|a| {
            a.guid = 0x5000_0010;
            a.properties.ints.insert(25, 12);
            a.creature_profile = Some(CreatureProfile {
                flags: 0,
                health: 80,
                health_max: 100,
                attributes: None,
                attribute_highlights: None,
            });
        }),
        &NoNames,
    ));
    w.with(|c, ui| c.on_shown(ui));
    // Out of combat: nothing.
    assert!(!w.with(|c, _| c.refresh_due(1.0)));
    w.with(|c, _| c.set_in_combat(true));
    assert!(!w.with(|c, _| c.refresh_due(0.74)));
    assert!(w.with(|c, _| c.refresh_due(0.01)));
    assert!(!w.with(|c, _| c.refresh_due(0.5)));
    w.with(|c, ui| c.on_hidden(ui));
    assert!(!w.with(|c, _| c.refresh_due(0.75)));
}

/// The creature page's two row lists, shaped like the installed layout's:
/// lists that stretch with the page, and a row template that stretches in
/// width only, its label and value stretching with it. The stats list is
/// short enough here to scroll.
fn creature_rows_window() -> Window {
    use ac_ui::element_info::ElementInfo;
    use ac_ui::geometry::AnchorEdges;
    use ac_ui::widgets::ListBox;
    window_with(|ui, creature, ids| {
        ui.set_anchors(creature, AnchorEdges::ALL);
        let viewport = ui.add(
            creature,
            at(5.0, 30.0, 300.0, 365.0)
                .with_anchors(AnchorEdges::ALL)
                .with_z_order(10),
            ac_ui::widgets::Panel::default(),
        );
        ids.insert(appraisal::CREATURE_VIEWPORT_ID, viewport);
        for (id, (x, y, w, h)) in [
            (appraisal::CREATURE_STATS_LIST_ID, (0.0, 68.0, 300.0, 100.0)),
            (appraisal::CREATURE_EXTRA_LIST_ID, (0.0, 278.0, 292.0, 87.0)),
        ] {
            let info = Arc::new(ElementInfo {
                id,
                width: w,
                height: h,
                ..ElementInfo::default()
            });
            let list = ui.add(
                creature,
                at(x, y, w, h).with_anchors(AnchorEdges::ALL),
                ListBox::new(info),
            );
            ids.insert(id, list);
        }
    })
}

/// A creature row as the template builds it (detached): 292 by 20,
/// following its parent's width; the label and value follow the row.
fn creature_row(ui: &mut ac_ui::root::UiRoot, _layout: u32, element: u32) -> Option<ElementId> {
    use ac_ui::geometry::{AnchorEdges, PixelRect};
    use ac_ui::layout_policy::LayoutPolicy;
    assert_eq!(element, appraisal::CREATURE_ROW_TEMPLATE_ID);
    let (rw, rh) = appraisal::CREATURE_ROW_SIZE;
    let row = ui.insert(
        at(0.0, 0.0, rw, rh)
            .with_anchors(AnchorEdges::LEFT | AnchorEdges::RIGHT | AnchorEdges::TOP),
        ac_ui::widgets::Panel::default(),
    );
    for (id, x, w) in [
        (appraisal::CREATURE_ROW_LABEL_ID, 0.0, 128.0),
        (appraisal::CREATURE_ROW_VALUE_ID, 27.0, 256.0),
    ] {
        let mut e = at(x, 0.0, w, rh);
        e.dat_element_id = id;
        e.layout_policy = LayoutPolicy::new(
            1,
            1,
            1,
            1,
            PixelRect::from_position_and_size(x as i32, 0, w as i32, rh as i32),
            PixelRect::from_position_and_size(0, 0, rw as i32, rh as i32),
        )
        .ok();
        let mut t = Text::default();
        t.right_aligned = id == appraisal::CREATURE_ROW_VALUE_ID;
        ui.add(row, e, t);
    }
    Some(row)
}

fn creature_rows_answer() -> IdentifyObjectResponse {
    parsed(|a| {
        a.guid = 0x5000_0010;
        a.creature_profile = Some(CreatureProfile {
            flags: 0x08,
            health: 295,
            health_max: 295,
            attributes: Some(ac_proto::appraise::CreatureAttributes {
                strength: 120,
                endurance: 190,
                quickness: 190,
                coordination: 190,
                focus: 330,
                self_: 350,
                stamina: 190,
                mana: 550,
                stamina_max: 190,
                mana_max: 550,
            }),
            attribute_highlights: None,
        });
        a.properties.ints.insert(0x133, 35);
        a.properties.ints.insert(0x13A, 4);
    })
}

// When the window is widened the attribute and vital rows widen with their
// list, in both layers, so each value stays right-aligned at the row's
// right edge; the extra rows keep their size. Rows built while the window
// is wide come out wide; scrolling still places the rows a row height
// apart from the scroll offset.
#[test]
fn the_creature_rows_widen_with_the_window_in_both_layers() {
    use ac_ui::widgets::{ListBox, ScrollablePanel};
    let mut w = creature_rows_window();
    w.objects
        .add(object(0x5000_0010, "Drudge", ItemType::CREATURE));
    let apply = |w: &mut Window| {
        let view = AppraisalView {
            name: None,
            response: creature_rows_answer(),
            present_in_ui: true,
            opens_window: true,
        };
        let objects = &w.objects.t;
        let mut rows = creature_row;
        assert!(
            w.host
                .with_controller::<AppraisalController, _>("examine", |c, ui| {
                    c.apply(ui, &view, objects, &none, &NoNames, &mut rows)
                })
                .unwrap()
        );
    };
    let draw = |w: &mut Window| {
        w.host.tick(0.016, 16, None);
        w.host.draw((800.0, 600.0), &ac_ui::draw::NoSprites);
    };
    w.host.root.show_window("examine");
    apply(&mut w);
    draw(&mut w);
    let (stats, extra) = w.with(|c, _| c.creature_lists());
    let lists = [stats.expect("stats list"), extra.expect("extra list")];
    // Each row of both layers of a list: (chrome row, text row).
    let layers = |w: &Window, list: usize| {
        let ui = &w.host.root;
        let l = lists[list];
        let chrome = ui
            .widget::<ListBox>(l.background)
            .and_then(ListBox::viewport_id)
            .map(|v| ui.children(v))
            .unwrap_or_default();
        let front = l.rows(ui);
        assert_eq!(chrome.len(), front.len());
        assert!(!front.is_empty());
        chrome.into_iter().zip(front).collect::<Vec<_>>()
    };
    let value = |w: &Window, row| {
        w.host
            .root
            .find_descendant(row, appraisal::CREATURE_ROW_VALUE_ID)
            .unwrap()
    };
    let right = |w: &Window, e| {
        let el = w.host.root.element(e).unwrap();
        el.left + el.width
    };
    let screen_right =
        |w: &Window, e| w.host.root.screen_position(e).0 + w.host.root.element(e).unwrap().width;
    let width = |w: &Window, e| w.host.root.element(e).unwrap().width;
    let (rw, _) = appraisal::CREATURE_ROW_SIZE;
    // The stats rows grow with the window; the extra rows keep their size.
    // A row's label and value are laid out when the row is drawn, so only
    // the rows in view are checked for them.
    let check = |w: &Window, grown: f32| {
        for (list, grown) in [(0, grown), (1, 0.0)] {
            for (chrome, front) in layers(w, list) {
                for row in [chrome, front] {
                    assert_eq!(width(w, row), rw + grown, "list {list} row width");
                }
                if !w.host.root.element(front).unwrap().visible {
                    continue;
                }
                for row in [chrome, front] {
                    assert_eq!(
                        right(w, value(w, row)),
                        283.0 + grown,
                        "list {list} value right edge"
                    );
                }
                // The text sits the inset right of its chrome.
                assert_eq!(
                    screen_right(w, value(w, front)),
                    screen_right(w, value(w, chrome)) + appraisal::CREATURE_ROW_TEXT_INSET
                );
            }
        }
    };
    check(&w, 0.0);

    // Widen the window by 100 pixels.
    assert!(w.host.root.resize_window("examine", 500.0, 500.0));
    draw(&mut w);
    check(&w, 100.0);

    // Rows built while it is wide (a refresh) come out wide.
    apply(&mut w);
    draw(&mut w);
    check(&w, 100.0);

    // Scrolling the stats list two rows still steps the rows from the
    // offset, in both layers.
    let fg = lists[0].foreground;
    w.host.root.with::<ScrollablePanel, _>(fg, |p, ui| {
        p.scroll.scroll_by_lines(2);
        p.layout_rows(ui, fg);
    });
    draw(&mut w);
    let ui = &w.host.root;
    let chrome_viewport = ui
        .widget::<ListBox>(lists[0].background)
        .and_then(ListBox::viewport_id)
        .unwrap();
    for rows in [ui.children(chrome_viewport), lists[0].rows(ui)] {
        assert_eq!(rows.len(), 9);
        for (i, row) in rows.into_iter().enumerate() {
            let e = ui.element(row).unwrap();
            assert_eq!(e.top, (i as f32 - 2.0) * 20.0, "row {i} top");
            assert_eq!(e.width, rw + 100.0, "row {i} width");
            assert_eq!(e.visible, (2..7).contains(&i), "row {i} visible");
        }
    }
    check(&w, 100.0);
}

fn examined_spell() -> SpellMetadata {
    let mut m = spell(42, "Incantation of Test", "A projected retail spell.");
    m.duration = 90.0;
    m.mana_cost = 50;
    m.school_id = MagicSchool::WAR_MAGIC;
    m.base_range_constant = 40.0;
    m.base_range_modifier = 0.25;
    m.mana_modifier = 14;
    m.formula_components = vec![10, 11];
    m
}

// SpellExamination_UsesAuthoredLocalSubviewWithoutSelectingOrAppraisingSpellId.
#[test]
fn examining_a_spell_shows_the_spell_page() {
    let mut w = window();
    let m = examined_spell();
    let components = [
        SpellExamineComponent {
            name: "Lead Scarab".into(),
            icon: Some(TextureRef::Runtime(0x0600_0010 + 2_000)),
            owned: true,
        },
        SpellExamineComponent {
            name: "Malar Herb".into(),
            icon: Some(TextureRef::Runtime(0x0600_0011 + 2_000)),
            owned: false,
        },
    ];
    w.with(|c, ui| {
        c.examine_spell(
            ui,
            &m,
            Some(TextureRef::Runtime(42 + 1_000)),
            200,
            &components,
            &mut component_cells,
        );
    });
    assert_eq!(w.with(|c, _| c.page()), AppraisalPage::Spell);
    assert_eq!(w.with(|c, _| c.current_object()), 0);
    assert!(w.visible(appraisal::SPELL_PANEL_ID));
    assert!(!w.visible(appraisal::ITEM_PANEL_ID));
    assert!(!w.visible(appraisal::CREATURE_PANEL_ID));
    assert_eq!(w.lines(appraisal::TITLE_ID), ["Incantation of Test"]);
    assert_eq!(
        w.lines(appraisal::SPELL_SCHOOL_TEXT_ID),
        ["School: War Magic"]
    );
    assert_eq!(
        w.lines(appraisal::SPELL_MANA_TEXT_ID),
        ["Mana: 50 + 14 per target"]
    );
    assert_eq!(
        w.lines(appraisal::SPELL_DURATION_TEXT_ID),
        ["Duration: 1 min."]
    );
    assert_eq!(
        w.lines(appraisal::SPELL_RANGE_TEXT_ID),
        ["Range: 82.0 yds."]
    );
    let display = w.lines(appraisal::SPELL_DISPLAY_TEXT_ID).join("\n");
    for expected in [
        "A projected retail spell.",
        "COMPONENTS:",
        "Lead Scarab",
        "Malar Herb",
    ] {
        assert!(display.contains(expected), "{expected}");
    }
    let icon = w.with(|c, _| c.spell_icon());
    assert_eq!(
        w.host.root.widget::<CatalogSlot>(icon).unwrap().icon,
        Some(TextureRef::Runtime(1_042))
    );
    let cells = w.with(|c, _| c.formula_cells().to_vec());
    let icons: Vec<_> = cells
        .iter()
        .map(|c| w.host.root.widget::<DatElement>(*c).unwrap().runtime_image)
        .collect();
    assert_eq!(
        icons,
        [
            Some(Some(TextureRef::Runtime(0x0600_0010 + 2_000))),
            Some(Some(TextureRef::Runtime(0x0600_0011 + 2_000)))
        ]
    );
    // SpellExamineComponentTemplateFactoryTests: the missing overlay shows
    // over the herb the player does not carry, not over the scarab.
    assert_eq!(missing_overlays(&w, &cells), [false, true]);
    // The row is centred on the authored cell.
    let f = w
        .host
        .root
        .element(w.ids[&appraisal::SPELL_FORMULA_LIST_ID])
        .unwrap();
    assert_eq!((f.left, f.width), (180.0, 40.0));
}

// OpenAC `AppraisalUiController.RefreshSpellComponents`: an item change
// while the spell page shows lists the components again and rebuilds the
// formula, leaving the rest of the page; on another page, or for another
// spell, nothing changes.
#[test]
fn an_item_change_refreshes_the_examined_spell_s_components() {
    let mut w = window();
    let m = examined_spell();
    let component = |name: &str, icon: u32, owned| SpellExamineComponent {
        name: name.into(),
        icon: Some(TextureRef::Runtime(icon)),
        owned,
    };
    let before = [
        component("Lead Scarab", 1, true),
        component("Malar Herb", 2, false),
    ];
    w.with(|c, ui| c.examine_spell(ui, &m, None, 200, &before, &mut component_cells));
    let after = [
        component("Lead Scarab", 1, true),
        component("Prismatic Taper", 3, true),
    ];
    let mut other = m.clone();
    other.spell_id = 43;
    w.with(|c, ui| c.refresh_spell_components(ui, &other, &after, &mut component_cells));
    assert!(
        w.lines(appraisal::SPELL_DISPLAY_TEXT_ID)
            .iter()
            .any(|l| l.contains("Malar Herb")),
        "another spell's refresh leaves the page"
    );
    w.with(|c, ui| c.refresh_spell_components(ui, &m, &after, &mut component_cells));
    let display = w.lines(appraisal::SPELL_DISPLAY_TEXT_ID).join("\n");
    assert!(display.contains("Prismatic Taper"), "{display}");
    assert!(!display.contains("Malar Herb"), "{display}");
    assert_eq!(w.lines(appraisal::TITLE_ID), ["Incantation of Test"]);
    let cells = w.with(|c, _| c.formula_cells().to_vec());
    assert_eq!(missing_overlays(&w, &cells), [false, false]);
    // Without the authored cell the row holds no cells and goes back to
    // its authored place.
    w.with(|c, ui| c.refresh_spell_components(ui, &m, &after, &mut no_rows));
    assert!(w.with(|c, _| c.formula_cells().is_empty()));
    let f = w
        .host
        .root
        .element(w.ids[&appraisal::SPELL_FORMULA_LIST_ID])
        .unwrap();
    assert_eq!((f.left, f.width), (190.0, 20.0));
    // On the item page the spell's components are not shown again.
    w.apply(parsed(|a| a.guid = 0x5000_0001), &NoNames);
    assert_eq!(w.with(|c, _| c.page()), AppraisalPage::Item);
    w.with(|c, ui| c.refresh_spell_components(ui, &m, &before, &mut component_cells));
    assert_eq!(w.with(|c, _| c.page()), AppraisalPage::Item);
    assert!(
        !w.lines(appraisal::SPELL_DISPLAY_TEXT_ID)
            .join("\n")
            .contains("Malar Herb")
    );
}

#[test]
fn the_spell_lines_follow_retail_rules() {
    let mut m = examined_spell();
    m.duration = 30.0;
    m.mana_cost = 0;
    m.mana_modifier = 0;
    assert_eq!(appraisal::spell_duration_text(&m), "Duration: 30 sec.");
    assert_eq!(appraisal::spell_mana_text(&m), "Mana: ???");
    m.duration = -1.0;
    assert_eq!(appraisal::spell_duration_text(&m), "");
    m.base_range_constant = 0.0;
    m.base_range_modifier = 0.0;
    assert_eq!(appraisal::spell_range_text(&m, 300), "");
    assert_eq!(
        appraisal::spell_display_text(&m, &[]),
        "A projected retail spell."
    );
}

// ── Real dats ───────────────────────────────────────────────────────────

mod real_dats {
    use super::*;
    use ac_ui::content::DatContent;
    use ac_ui::font::DEFAULT_FONT_ID;
    use ac_ui::import::{LayoutSource, import_rooted};
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

    struct CreatureNames;

    impl AppraisalNames for CreatureNames {
        fn creature(&self, creature: i32) -> String {
            if creature == 77 {
                "Ghost".into()
            } else {
                String::new()
            }
        }

        fn character_title(&self, title_id: u32) -> Option<String> {
            (title_id == 13).then(|| "War Mage".into())
        }
    }

    fn texts(host: &UiHost, id: ElementId) -> Vec<String> {
        host.root
            .widget::<Text>(id)
            .unwrap()
            .content_lines()
            .into_iter()
            .map(|l| l.text)
            .collect()
    }

    fn row_text(host: &UiHost, row: ElementId, id: u32) -> (String, Color) {
        let t = host.root.find_descendant(row, id).unwrap();
        let lines = host.root.widget::<Text>(t).unwrap().content_lines();
        let line = lines.first().expect("one line");
        (line.text.clone(), line.color)
    }

    /// OpenAC `AppraisalUiControllerTests`
    /// `CreatureResponse_UsesRetailHeaderAndNineOrderedTemplateRows`,
    /// `CharacterResponse_ComposesRetailHeaderIdentityBlock`,
    /// `CharacterResponse_TitleBarPrefixesAllegianceRankTitle` and
    /// `CreatureAppraisalRowsTests.AuthoredRowTemplateCarriesRetailOverlappingLabelValueGeometry`
    /// on the installed window: the header lines, the rows in two layers
    /// around the portrait (chrome behind it, text in front, 8 pixels in),
    /// the value colours from the row's palette.
    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_creature_and_character_pages_fill_their_header_and_rows() {
        use ac_ui::panels::DatRowTemplates;
        use ac_ui::widgets::ListBox;
        const CREATURE: u32 = 0x5000_0020;
        const CHARACTER: u32 = 0x5000_0021;
        let c = content();
        let mut host = UiHost::new();
        host.root.set_size(1024.0, 768.0);
        let layout = import_rooted(
            &mut host.root,
            &c,
            appraisal::LAYOUT_ID,
            appraisal::ROOT_ID,
            c.font(DEFAULT_FONT_ID),
        )
        .expect("examination layout");
        let r = host.root.root();
        host.root.add_child(r, layout.root);
        host.root
            .register_window("examine", layout.root, None, 0)
            .unwrap();
        let ctl = AppraisalController::bind(&mut host.root, &layout).expect("binds");
        host.attach_controller("examine", Box::new(ctl)).unwrap();
        let mut o = Objects::new();
        o.player(102, 7);
        o.add(object(CREATURE, "Specter", ItemType::CREATURE));
        let mut dww = object(CHARACTER, "Dww", ItemType::CREATURE);
        dww.public_weenie_bitfield = Some(ac_core::items::PublicWeenieFlags(0x20));
        o.add(dww);
        let mut rows = DatRowTemplates::new(&c, c.font(DEFAULT_FONT_ID));

        let profile = CreatureProfile {
            flags: 0x08,
            health: 295,
            health_max: 295,
            attributes: Some(ac_proto::appraise::CreatureAttributes {
                strength: 120,
                endurance: 190,
                quickness: 190,
                coordination: 190,
                focus: 330,
                self_: 350,
                stamina: 190,
                mana: 550,
                stamina_max: 190,
                mana_max: 550,
            }),
            // Strength raised by an enchantment.
            attribute_highlights: Some((1, 1)),
        };
        let view = AppraisalView {
            name: None,
            response: parsed(|a| {
                a.guid = CREATURE;
                a.creature_profile = Some(profile);
                a.properties.ints.insert(2, 77);
                a.properties.ints.insert(25, 80);
                a.properties.ints.insert(0x133, 35);
                a.properties.ints.insert(0x13A, 4);
            }),
            present_in_ui: true,
            opens_window: true,
        };
        let shown = host
            .with_controller::<AppraisalController, _>("examine", |w, ui| {
                w.apply(ui, &view, &o.t, &none, &CreatureNames, &mut rows)
            })
            .unwrap();
        assert!(shown);
        let find = |id| layout.find(id).unwrap();
        assert_eq!(
            texts(&host, find(appraisal::CREATURE_LEVEL_VALUE_ID)),
            ["80"]
        );
        assert_eq!(
            texts(&host, find(appraisal::CREATURE_DISPLAY_NAME_ID)),
            ["Ghost"]
        );
        let (stats, extra) = host
            .with_controller::<AppraisalController, _>("examine", |w, _| w.creature_lists())
            .unwrap();
        let (stats, extra) = (stats.expect("stats list"), extra.expect("extra list"));
        let viewport = find(appraisal::CREATURE_VIEWPORT_ID);
        let z = |e| host.root.element(e).unwrap().z_order;
        assert!(z(stats.background) < z(viewport) && z(viewport) < z(stats.foreground));
        assert!(z(extra.background) < z(viewport) && z(viewport) < z(extra.foreground));
        let el = |e| host.root.element(e).unwrap();
        assert_eq!(
            el(stats.foreground).left,
            el(stats.background).left + appraisal::CREATURE_ROW_TEXT_INSET
        );
        assert_eq!(el(stats.foreground).top, el(stats.background).top);
        // The chrome layer holds a row per text row, with no text.
        let chrome = host
            .root
            .widget::<ListBox>(stats.background)
            .unwrap()
            .item_count(&host.root);
        assert_eq!(chrome, 9);
        let front = stats.rows(&host.root);
        assert_eq!(front.len(), 9);
        let label = |row| row_text(&host, row, appraisal::CREATURE_ROW_LABEL_ID).0;
        let value = |row| row_text(&host, row, appraisal::CREATURE_ROW_VALUE_ID);
        assert_eq!(
            front.iter().map(|r| label(*r)).collect::<Vec<_>>(),
            [
                "Strength",
                "Endurance",
                "Coordination",
                "Quickness",
                "Focus",
                "Self",
                "Health",
                "Stamina",
                "Mana"
            ]
        );
        assert_eq!(
            front.iter().map(|r| value(*r).0).collect::<Vec<_>>(),
            [
                "120",
                "190",
                "190",
                "190",
                "330",
                "350",
                "295/295 (100 %)",
                "190/190",
                "550/550"
            ]
        );
        // The row template's palette: white, green, red, yellow.
        assert_eq!(value(front[0]).1, Color::rgba(0.0, 1.0, 0.0, 1.0));
        assert_eq!(value(front[1]).1, Color::WHITE);
        let row = el(front[0]);
        assert_eq!((row.width, row.height), appraisal::CREATURE_ROW_SIZE);
        let extra_rows = extra.rows(&host.root);
        assert_eq!(extra_rows.len(), 3);
        assert_eq!(label(extra_rows[1]), "Dmg/CritDmg");
        assert_eq!(value(extra_rows[1]).0, "Rating: 35/4");

        // A character: the identity block and the rank title in the bar.
        let view = AppraisalView {
            name: None,
            response: parsed(|a| {
                a.guid = CHARACTER;
                a.creature_profile = Some(CreatureProfile {
                    flags: 0,
                    health: 1,
                    health_max: 1,
                    attributes: None,
                    attribute_highlights: None,
                });
                a.properties.ints.insert(0x71, 2);
                a.properties.ints.insert(0xBC, 1);
                a.properties.ints.insert(0x105, 13);
                a.properties.ints.insert(30, 3);
                a.properties.strings.insert(47, "The Empire".into());
            }),
            present_in_ui: true,
            opens_window: true,
        };
        host.with_controller::<AppraisalController, _>("examine", |w, ui| {
            w.apply(ui, &view, &o.t, &none, &CreatureNames, &mut rows)
        });
        let page = host
            .with_controller::<AppraisalController, _>("examine", |w, _| w.page())
            .unwrap();
        assert_eq!(page, AppraisalPage::Character);
        assert_eq!(
            texts(&host, find(appraisal::CHARACTER_HERITAGE_ID)),
            ["Female Aluvian"]
        );
        assert_eq!(
            texts(&host, find(appraisal::CHARACTER_TITLE_TEXT_ID)),
            ["War Mage"]
        );
        assert_eq!(
            texts(&host, find(appraisal::CHARACTER_PLAYER_KILLER_ID)),
            ["Player Killer"]
        );
        assert_eq!(
            texts(&host, find(appraisal::CHARACTER_ALLEGIANCE_ID)),
            ["The Empire"]
        );
        // Aluvian female rank 3.
        assert_eq!(texts(&host, find(appraisal::TITLE_ID)), ["Baroness Dww"]);
        // The creature's own lines are cleared; the character's extras end
        // in the legend.
        assert!(texts(&host, find(appraisal::CREATURE_DISPLAY_NAME_ID)).is_empty());
        let extra_rows = extra.rows(&host.root);
        let last = row_text(
            &host,
            *extra_rows.last().unwrap(),
            appraisal::CREATURE_ROW_LABEL_ID,
        );
        assert_eq!(last.0, "* = Unenchantable");
        let drawn = host.draw((1024.0, 768.0), &c);
        assert!(!drawn.main.is_empty());
        assert!(c.take_errors().is_empty());
    }

    // AppraisalUiControllerTests on the installed layout.
    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_examination_window_imports_binds_and_shows_an_item() {
        let c = content();
        let mut host = UiHost::new();
        host.root.set_size(1024.0, 768.0);
        let layout = import_rooted(
            &mut host.root,
            &c,
            appraisal::LAYOUT_ID,
            appraisal::ROOT_ID,
            c.font(DEFAULT_FONT_ID),
        )
        .expect("examination layout");
        let r = host.root.root();
        host.root.add_child(r, layout.root);
        host.root
            .register_window("examine", layout.root, None, 0)
            .unwrap();
        let ctl = AppraisalController::bind(&mut host.root, &layout).expect("binds");
        host.attach_controller("examine", Box::new(ctl)).unwrap();
        let item_text = layout.find(appraisal::ITEM_TEXT_ID).unwrap();
        let palette = host
            .root
            .widget::<Text>(item_text)
            .unwrap()
            .font_color_palette
            .clone();
        assert_eq!(
            palette,
            [
                Color::WHITE,
                Color::rgba(0.0, 1.0, 0.0, 1.0),
                Color::rgba(1.0, 0.0, 0.0, 1.0)
            ]
        );
        let mut o = Objects::new();
        o.add(object(ITEM, "Atlan Weapon", ItemType::MELEE_WEAPON));
        let view = AppraisalView {
            name: None,
            response: parsed(|a| {
                a.guid = ITEM;
                a.properties.ints.insert(19, 1_250);
                a.properties
                    .strings
                    .insert(16, "A finely balanced weapon.".into());
            }),
            present_in_ui: true,
            opens_window: true,
        };
        let shown = host
            .with_controller::<AppraisalController, _>("examine", |w, ui| {
                w.apply(ui, &view, &o.t, &none, &NoNames, &mut no_rows)
            })
            .unwrap();
        assert!(shown);
        let lines: Vec<String> = host
            .root
            .widget::<Text>(item_text)
            .unwrap()
            .content_lines()
            .into_iter()
            .map(|l| l.text)
            .collect();
        assert_eq!(lines[0], "Value: 1,250");
        assert!(lines.iter().any(|l| l.contains("finely balanced")));
        host.with_controller::<AppraisalController, _>("examine", |w, ui| {
            w.examine_spell(ui, &examined_spell(), None, 200, &[], &mut no_rows);
        });
        let spell = layout.find(appraisal::SPELL_PANEL_ID).unwrap();
        assert!(host.root.element(spell).unwrap().visible);
        let drawn = host.draw((1024.0, 768.0), &c);
        assert!(!drawn.main.is_empty());
        assert!(c.take_errors().is_empty());
    }

    const SWORD: u32 = 0x5000_0010;

    struct Examined {
        host: UiHost,
        layout: ImportedLayout,
    }

    /// The installed examination window showing `item`, its appraisal
    /// shaped by `f`, the player "Tester" looking.
    fn examined(item: ClientObject, f: impl FnOnce(&mut IdentifyObjectResponse)) -> Examined {
        let c = content();
        let mut host = UiHost::new();
        host.root.set_size(1024.0, 768.0);
        let layout = import_rooted(
            &mut host.root,
            &c,
            appraisal::LAYOUT_ID,
            appraisal::ROOT_ID,
            c.font(DEFAULT_FONT_ID),
        )
        .expect("examination layout");
        let r = host.root.root();
        host.root.add_child(r, layout.root);
        host.root
            .register_window("examine", layout.root, None, 0)
            .unwrap();
        let mut ctl = AppraisalController::bind(&mut host.root, &layout).expect("binds");
        ctl.set_player(PLAYER, "Tester");
        host.attach_controller("examine", Box::new(ctl)).unwrap();
        let mut o = Objects::new();
        o.player(102, 7);
        let guid = item.object_id();
        o.add(item);
        let view = AppraisalView {
            name: None,
            response: parsed(|a| {
                a.guid = guid;
                f(a);
            }),
            present_in_ui: true,
            opens_window: true,
        };
        let shown = host
            .with_controller::<AppraisalController, _>("examine", |w, ui| {
                w.apply(ui, &view, &o.t, &none, &NoNames, &mut no_rows)
            })
            .unwrap();
        assert!(shown);
        host.pump();
        host.take_outputs();
        Examined { host, layout }
    }

    fn sword(container: u32, inscribable: bool) -> ClientObject {
        let mut o = object(SWORD, "Sword", ItemType::MELEE_WEAPON);
        o.container_id = container;
        if inscribable {
            o.public_weenie_bitfield = Some(ac_core::items::PublicWeenieFlags::INSCRIBABLE);
        }
        o
    }

    impl Examined {
        fn field(&self) -> ElementId {
            self.layout.find(appraisal::INSCRIPTION_TEXT_ID).unwrap()
        }

        fn field_widget(&self) -> &ac_ui::widgets::Field {
            self.host
                .root
                .widget::<ac_ui::widgets::Field>(self.field())
                .expect("the inscription is a field")
        }

        fn signature(&self) -> Vec<String> {
            let s = self.layout.find(appraisal::SIGNATURE_TEXT_ID).unwrap();
            self.host
                .root
                .widget::<Text>(s)
                .unwrap()
                .content_lines()
                .into_iter()
                .map(|l| l.text)
                .collect()
        }

        fn focus(&mut self, on: bool) {
            let target = on.then(|| self.field());
            self.host.root.set_keyboard_focus(target);
            self.host.pump();
        }

        fn outputs(&mut self) -> Vec<PanelOutput> {
            self.host.pump();
            self.host
                .take_outputs()
                .into_iter()
                .filter_map(|o| match o {
                    ac_ui::root::UiOutput::Panel(p) => Some(p),
                    _ => None,
                })
                .collect()
        }

        fn notices(&mut self) -> Vec<String> {
            self.outputs()
                .into_iter()
                .filter_map(|p| match p {
                    PanelOutput::Notice(t) => Some(t),
                    _ => None,
                })
                .collect()
        }

        fn click(&mut self, element: ElementId, message: ac_ui::root::ElementMessage) {
            self.host.root.send_message(element, message);
        }
    }

    fn inscriptions(outputs: &[PanelOutput]) -> Vec<(u32, String)> {
        use ac_runtime::inventory::{ItemCommand, ObjectRef};
        use ac_runtime::session::SessionCommand;
        outputs
            .iter()
            .filter_map(|p| match p {
                PanelOutput::Session(SessionCommand::Item(ItemCommand::Inscribe {
                    item: ObjectRef::Guid(id),
                    text,
                })) => Some((*id, text.clone())),
                _ => None,
            })
            .collect()
    }

    // EmptyOwnedInscription_FocusEditAndBlur_SendsRetailTransactionOnce.
    #[test]
    #[ignore = "needs the retail dat files"]
    fn an_empty_owned_inscription_is_written_once_on_leaving() {
        let mut e = examined(sword(PLAYER, true), |_| {});
        assert!(e.field_widget().editable());
        assert_eq!(e.field_widget().text(), appraisal::INSCRIBE_HERE);
        e.focus(true);
        assert_eq!(e.field_widget().text(), "");
        assert_eq!(e.signature(), ["--Tester"]);
        let field = e.field();
        e.host.root.with::<ac_ui::widgets::Field, _>(field, |f, _| {
            for c in "For glory".chars() {
                f.insert_char(c);
            }
        });
        e.focus(false);
        let out = e.outputs();
        assert_eq!(inscriptions(&out), [(SWORD, "For glory".to_owned())]);
        assert_eq!(e.signature(), ["--Tester"]);
        e.focus(true);
        e.focus(false);
        assert!(inscriptions(&e.outputs()).is_empty());
    }

    // ExistingInscription_ClearOnBlur_SendsEmptyAndRestoresPlaceholder.
    #[test]
    #[ignore = "needs the retail dat files"]
    fn clearing_an_inscription_sends_empty_and_shows_the_placeholder() {
        let mut e = examined(sword(PLAYER, true), |a| {
            a.properties.strings.insert(7, "Old words".into());
            a.properties.strings.insert(8, "Tester".into());
        });
        assert_eq!(e.field_widget().text(), "Old words");
        assert_eq!(e.signature(), ["--Tester"]);
        e.focus(true);
        let field = e.field();
        e.host
            .root
            .with::<ac_ui::widgets::Field, _>(field, |f, _| f.set_text(""));
        e.focus(false);
        let out = e.outputs();
        assert_eq!(inscriptions(&out), [(SWORD, String::new())]);
        assert_eq!(e.field_widget().text(), appraisal::INSCRIBE_HERE);
        assert!(e.signature().iter().all(String::is_empty));
    }

    // InscriptionPermissionMessages_MatchRetail.
    #[test]
    #[ignore = "needs the retail dat files"]
    fn a_click_where_the_player_may_not_write_says_why() {
        let mut e = examined(sword(PLAYER, true), |a| {
            a.properties.strings.insert(7, "Hands off".into());
            a.properties.strings.insert(8, "Other".into());
        });
        assert!(!e.field_widget().editable());
        let field = e.field();
        e.click(field, ac_ui::root::ElementMessage::ReadOnlyClicked);
        assert_eq!(e.notices(), ["Only Other can change the inscription"]);

        let mut e = examined(sword(0x6000_0000, true), |_| {});
        let field = e.field();
        e.click(field, ac_ui::root::ElementMessage::ReadOnlyClicked);
        assert_eq!(e.notices(), ["Item must be in your inventory to inscribe."]);
    }

    // NonInscribableItem_HidesEditorAndBackgroundReportsRetailMessage.
    #[test]
    #[ignore = "needs the retail dat files"]
    fn an_uninscribable_item_hides_the_inscription() {
        let mut e = examined(sword(PLAYER, false), |_| {});
        let field = e.field();
        let signature = e.layout.find(appraisal::SIGNATURE_TEXT_ID).unwrap();
        assert!(!e.host.root.element(field).unwrap().visible);
        assert!(!e.host.root.element(signature).unwrap().visible);
        let background = e.layout.find(appraisal::INSCRIPTION_BACKGROUND_ID).unwrap();
        e.click(
            background,
            ac_ui::root::ElementMessage::Clicked { x: 1, y: 1 },
        );
        assert_eq!(e.notices(), ["This item is not inscribable."]);
    }
}
