//! The character window's titles page and the character information
//! panel.
//!
//! Ported from OpenAC `tests/AcDream.App.Tests/UI/Layout/CharacterTitlesControllerTests.cs`,
//! `CharacterTitleResolverLiveDatTests.cs` and `CharacterControllerTests.cs`
//! (the real-dat module binds the retail layouts). OpenAC's title state
//! raises events; here the controller compares the `TitleFacts`s it is
//! handed (a new table and another displayed title drop the selection, an
//! earned title keeps it).

mod common;

use std::collections::{BTreeMap, HashMap};
use std::sync::Arc;

use ac_runtime::gameplay::GameplayCommand;
use ac_runtime::session::SessionCommand;
use ac_runtime::session::interface::TitleFacts;
use ac_ui::ElementId;
use ac_ui::element_info::{ElementInfo, TemplateEntry};
use ac_ui::event::kind;
use ac_ui::host::UiHost;
use ac_ui::panels::PanelOutput;
use ac_ui::panels::character_info::{
    CharacterInfoSource, CharacterInfoStrings, build_report, format_birth_date,
    format_retail_duration, resistance_grade,
};
use ac_ui::panels::character_titles::{self as titles, CharacterTitlesController, sorted_titles};
use ac_ui::root::UiRoot;
use ac_ui::states;
use ac_ui::tree::Element;
use ac_ui::widgets::{DatElement, ListBox, Text};
use common::items::panel_outputs;
use common::panels::*;
use common::*;

fn name(id: u32) -> Option<String> {
    match id {
        1 => Some("Adventurer".into()),
        5 => Some("Life Mage".into()),
        13 => Some("War Mage".into()),
        14 => Some("Wayfarer".into()),
        _ => None,
    }
}

struct Page {
    host: UiHost,
    ids: HashMap<u32, ElementId>,
    sent: Vec<u32>,
}

fn row_template(ui: &mut UiRoot, _layout: u32, _element: u32) -> Option<ElementId> {
    let mut e = Element::at(0.0, 0.0, 200.0, 24.0);
    DatElement::configure(&mut e);
    let mut info = ElementInfo {
        width: 200.0,
        height: 24.0,
        ..ElementInfo::default()
    };
    for (id, n) in [(states::NORMAL, "Normal"), (states::HIGHLIGHT, "Highlight")] {
        info.set_state(id, ac_ui::element_info::StateInfo::named(id, n));
    }
    let row = ui.insert(e, DatElement::new(Arc::new(info)));
    let mut t = Element::at(0.0, 0.0, 200.0, 24.0);
    t.dat_element_id = titles::ROW_TEXT_ID;
    Text::configure(&mut t);
    ui.add(row, t, Text::default());
    Some(row)
}

fn titles_page() -> Page {
    let mut host = UiHost::new();
    host.root.set_size(800.0, 600.0);
    let ui = &mut host.root;
    let r = ui.root();
    let page = panel(ui, r, at(0.0, 0.0, 300.0, 400.0));
    let mut ids = HashMap::new();
    let mut e = Element::at(0.0, 40.0, 280.0, 300.0);
    e.dat_element_id = titles::TITLE_LIST_BOX_ID;
    let mut list = ListBox::new(Arc::new(ElementInfo {
        id: titles::TITLE_LIST_BOX_ID,
        kind: 5,
        ..ElementInfo::default()
    }));
    list.templates = vec![TemplateEntry {
        layout: 0x2100_0055,
        element: 0x1000_0536,
    }];
    ids.insert(titles::TITLE_LIST_BOX_ID, ui.add(page, e, list));
    add_std_text(ui, &mut ids, page, titles::CURRENT_DISPLAY_TITLE_TEXT_ID);
    add_std_button(ui, &mut ids, page, titles::SET_DISPLAY_BUTTON_ID);
    ui.register_window("titles", page, None, 0).unwrap();
    let c = CharacterTitlesController::bind(ui, page).expect("binds");
    host.attach_controller("titles", Box::new(TitlesHost(c)))
        .unwrap();
    Page {
        host,
        ids,
        sent: Vec::new(),
    }
}

/// The titles page on its own.
#[derive(Debug)]
struct TitlesHost(CharacterTitlesController);

impl ac_ui::host::PanelController for TitlesHost {
    fn on_element_message(
        &mut self,
        ui: &mut UiRoot,
        element: ElementId,
        message: &ac_ui::root::ElementMessage,
    ) -> bool {
        self.0.on_message(ui, element, message)
    }
}

fn view(display: u32, ids: &[u32], revision: u64, table: u64) -> TitleFacts {
    TitleFacts {
        display_title_id: display,
        title_ids: ids.to_vec(),
        revision,
        table_generation: table,
    }
}

impl Page {
    fn with<R>(&mut self, f: impl FnOnce(&mut CharacterTitlesController, &mut UiRoot) -> R) -> R {
        self.host
            .with_controller::<TitlesHost, _>("titles", |c, ui| f(&mut c.0, ui))
            .unwrap()
    }

    fn show(&mut self, v: &TitleFacts) {
        let mut rows = row_template;
        self.with(|c, ui| c.update(ui, v, &name, &mut rows));
    }

    fn rows(&mut self) -> Vec<(ElementId, u32)> {
        self.with(|c, _| c.rows().to_vec())
    }

    fn row_texts(&mut self) -> Vec<String> {
        let rows = self.rows();
        rows.iter()
            .map(|(r, _)| {
                let t = self
                    .host
                    .root
                    .find_descendant(*r, titles::ROW_TEXT_ID)
                    .unwrap();
                text_of(&self.host.root, t)
            })
            .collect()
    }

    fn click(&mut self, element: ElementId) {
        self.host.root.fire_event(kind::CLICK, element, None);
        self.host.pump();
        for out in panel_outputs(&mut self.host) {
            if let PanelOutput::Session(SessionCommand::Gameplay(GameplayCommand::SetTitle(id))) =
                out
            {
                self.sent.push(id);
            }
        }
    }

    fn click_row(&mut self, title: u32) {
        let row = self
            .rows()
            .into_iter()
            .find(|(_, id)| *id == title)
            .unwrap()
            .0;
        self.click(row);
    }

    fn button_enabled(&self) -> bool {
        let b = self.ids[&titles::SET_DISPLAY_BUTTON_ID];
        self.host.root.element(b).unwrap().enabled
    }

    fn highlighted(&self, row: ElementId) -> bool {
        self.host.root.retail_state(row) == Some(states::HIGHLIGHT)
    }

    fn display_text(&self) -> String {
        text_of(
            &self.host.root,
            self.ids[&titles::CURRENT_DISPLAY_TITLE_TEXT_ID],
        )
    }
}

#[test]
fn rows_are_sorted_by_their_text_and_unnamed_titles_have_none() {
    assert_eq!(
        sorted_titles(&[13, 1, 5, 0, 99], &name),
        [
            (1, "Adventurer".to_owned()),
            (5, "Life Mage".to_owned()),
            (13, "War Mage".to_owned())
        ]
    );
    let mut p = titles_page();
    p.show(&view(0, &[13, 1, 5], 1, 1));
    assert_eq!(p.row_texts(), ["Adventurer", "Life Mage", "War Mage"]);
    p.show(&view(0, &[99], 2, 2));
    assert!(p.rows().is_empty());
}

#[test]
fn a_selected_row_is_highlighted_and_the_others_not() {
    let mut p = titles_page();
    p.show(&view(0, &[1, 5], 1, 1));
    let rows = p.rows();
    p.click_row(1);
    assert!(p.highlighted(rows[0].0) && !p.highlighted(rows[1].0));
    p.click_row(5);
    assert!(!p.highlighted(rows[0].0) && p.highlighted(rows[1].0));
}

#[test]
fn the_button_is_ghosted_without_a_selection_or_on_the_displayed_title() {
    let mut p = titles_page();
    p.show(&view(1, &[1, 5], 1, 1));
    assert!(!p.button_enabled());
    p.click_row(1);
    assert!(!p.button_enabled(), "already displayed");
    p.click_row(5);
    assert!(p.button_enabled());
}

#[test]
fn set_display_sends_one_request_and_changes_nothing_here() {
    let mut p = titles_page();
    p.show(&view(1, &[1, 13], 1, 1));
    p.click_row(13);
    let rows = p.rows();
    let button = p.ids[&titles::SET_DISPLAY_BUTTON_ID];
    p.click(button);
    assert_eq!(p.sent, [13]);
    assert_eq!(p.display_text(), "Adventurer");
    assert_eq!(p.rows(), rows);
    assert!(p.highlighted(rows[1].0));
    assert!(p.button_enabled());
}

#[test]
fn set_display_while_ghosted_sends_nothing() {
    let mut p = titles_page();
    p.show(&view(1, &[1], 1, 1));
    let button = p.ids[&titles::SET_DISPLAY_BUTTON_ID];
    p.click(button);
    assert!(p.sent.is_empty());
}

#[test]
fn a_new_table_rebuilds_and_drops_the_selection_even_when_still_earned() {
    let mut p = titles_page();
    p.show(&view(0, &[1, 5], 1, 1));
    p.click_row(5);
    assert!(p.button_enabled());
    p.show(&view(0, &[1, 5], 1, 2));
    let rows = p.rows();
    assert!(!p.highlighted(rows[1].0));
    assert!(!p.button_enabled());
    p.show(&view(0, &[13], 2, 3));
    assert_eq!(p.row_texts(), ["War Mage"]);
}

#[test]
fn another_displayed_title_drops_the_selection_and_shows_its_name() {
    let mut p = titles_page();
    p.show(&view(0, &[1, 5], 1, 1));
    p.click_row(5);
    assert!(p.button_enabled());
    p.show(&view(5, &[1, 5], 2, 1));
    assert_eq!(p.display_text(), "Life Mage");
    assert!(!p.button_enabled());
    let rows = p.rows();
    assert!(!p.highlighted(rows[1].0));
}

#[test]
fn an_earned_title_keeps_the_selection_and_slots_in_order() {
    let mut p = titles_page();
    p.show(&view(0, &[1], 1, 1));
    p.click_row(1);
    p.show(&view(0, &[1, 5], 2, 1));
    assert_eq!(p.row_texts(), ["Adventurer", "Life Mage"]);
    let rows = p.rows();
    assert!(p.highlighted(rows[0].0));
    assert!(p.button_enabled());
}

#[test]
fn an_unknown_or_absent_display_title_reads_unknown() {
    let mut p = titles_page();
    p.show(&view(99, &[1], 1, 1));
    assert_eq!(p.display_text(), "Unknown");
    p.show(&view(0, &[1], 2, 1));
    assert_eq!(p.display_text(), "Unknown");
}

#[test]
fn the_list_is_24_pixel_rows() {
    let mut p = titles_page();
    p.show(&view(0, &[1], 1, 1));
    let list = p.ids[&titles::TITLE_LIST_BOX_ID];
    let vp = p
        .host
        .root
        .widget::<ListBox>(list)
        .unwrap()
        .viewport_id()
        .unwrap();
    assert_eq!(
        p.host
            .root
            .widget::<ac_ui::widgets::ScrollablePanel>(vp)
            .unwrap()
            .line_height,
        24
    );
}

// ── The character information report ────────────────────────────────────

/// OpenAC `SampleData.SampleCharacter`'s report-relevant numbers.
fn sample() -> CharacterInfoSource {
    let mut ints = BTreeMap::new();
    ints.insert(0x2B, 42); // deaths
    ints.insert(0xB5, 12); // chess rank
    ints.insert(0xC0, 4); // fishing
    ints.insert(0x162, 2); // melee mastery: swords
    ints.insert(5, 1000); // burden
    CharacterInfoSource {
        ints,
        attributes: [200, 150, 10, 60, 100, 100],
        base: [200, 150, 10, 60, 100, 100],
        innate: [200, 50, 10, 60, 100, 100],
        birth_text: Some("1/1/2020 12:00:00 PM".into()),
    }
}

/// The grades read Strength and Endurance without spells (innate plus
/// raised); the burden reads Strength with them.
#[test]
fn the_grades_read_strength_and_endurance_without_spells() {
    let mut s = CharacterInfoSource {
        // With spells 300 + 300 would be Indomitable for all three.
        attributes: [300, 300, 10, 10, 10, 10],
        base: [200, 50, 10, 10, 10, 10],
        ..CharacterInfoSource::default()
    };
    s.ints.insert(5, 40_000); // burden
    let r = report(&s);
    // 200 + 50 = 250 and 200 + 2 * 50 = 300: both Poor.
    assert!(r.contains("Natural Resistances: Poor"), "{r}");
    assert!(r.contains("Drain Resistances: Poor"), "{r}");
    assert!(r.contains("Regeneration Bonus: Poor"), "{r}");
    // Capacity at 300 Strength is 45,000: not burdened (at 200 it would
    // be 30,000).
    assert!(r.contains("You are not overburdened at this time."), "{r}");
}

/// Every integer in the report is grouped by thousands, and a character
/// without a chess rank is ranked 1400.
#[test]
fn integers_are_grouped_and_an_unranked_chess_player_is_1400() {
    let r = report(&CharacterInfoSource::default());
    assert!(r.contains("Chess Rank: 1,400"), "{r}");
    let mut s = CharacterInfoSource::default();
    s.ints.insert(0xB5, 1_850);
    s.ints.insert(0x2B, 1_234); // deaths
    s.ints.insert(0xC0, 2_000); // fishing
    let r = report(&s);
    assert!(r.contains("Chess Rank: 1,850"), "{r}");
    assert!(r.contains("died 1,234 times"), "{r}");
    assert!(r.contains("Fishing Skill: 2,000"), "{r}");
    assert!(!r.contains("Chess Rank: 1400"), "{r}");
    let mut s = CharacterInfoSource {
        attributes: [100, 10, 10, 10, 10, 10],
        ..CharacterInfoSource::default()
    };
    s.ints.insert(5, 20_000); // burden: 5,000 over a capacity of 15,000
    assert!(
        report(&s).contains("overburdened by 5,000 burden units"),
        "{}",
        report(&s)
    );
}

fn report(source: &CharacterInfoSource) -> String {
    build_report(source, &CharacterInfoStrings::english())
}

#[test]
fn the_report_reads_as_the_game_writes_it() {
    let r = report(&sample());
    assert!(r.contains("You were born on 1/1/2020 12:00:00 PM."));
    for wanted in [
        "You have died 42 times.",
        "Natural Resistances:",
        "Drain Resistances:",
        "Regeneration Bonus:",
        "Innate Strength: 200",
        "Innate Coordination: 10",
        "Chess Rank: 12",
        "Fishing Skill: 4",
        "Your melee mastery is Swords.",
        "You are not overburdened at this time.",
    ] {
        assert!(r.contains(wanted), "{wanted} in {r}");
    }
    for unwanted in ["Health:", "Augmentations", "Encumbrance"] {
        assert!(!r.contains(unwanted), "{unwanted}");
    }
}

#[test]
fn resistance_grades_use_inclusive_bounds() {
    for (v, g) in [
        (200, "None"),
        (201, "Poor"),
        (260, "Poor"),
        (261, "Mediocre"),
        (321, "Hardy"),
        (381, "Resilient"),
        (441, "Indomitable"),
    ] {
        assert_eq!(resistance_grade(v, 200, 260, 320, 380, 440), g, "{v}");
    }
}

#[test]
fn a_play_time_reads_in_whole_units() {
    for (s, t) in [
        (0, ""),
        (1, "1 second"),
        (61, "1 minute 1 second"),
        (3_196_800, "1 month 1 week"),
        (31_536_000, "1 year"),
    ] {
        assert_eq!(format_retail_duration(s), t, "{s}");
    }
}

#[test]
fn deaths_have_their_own_words_for_none_one_and_two() {
    let with = |n| {
        let mut s = CharacterInfoSource::default();
        s.ints.insert(0x2B, n);
        report(&s)
    };
    assert!(with(0).contains("never died"));
    assert!(with(1).contains("only once"));
    assert!(with(2).contains("died twice"));
    assert!(with(3).contains("died 3 times"));
}

// OpenAC `CharacterControllerTests.Report_TinkeringAugmentationUsesMatchingProperty`
// (#200): 0xE2 is armor tinkering, 0xE3 magic item tinkering.
#[test]
fn each_tinkering_augmentation_reads_its_own_property() {
    let mut strings = CharacterInfoStrings::english();
    strings.augmentations = HashMap::from([
        (
            "ID_CharacterInfo_Augmentation_Spec_ArmorTinkering".to_owned(),
            vec!["Armor Tinkering\n".to_owned()],
        ),
        (
            "ID_CharacterInfo_Augmentation_Spec_MagicItemTinkering".to_owned(),
            vec!["Magic Item Tinkering\n".to_owned()],
        ),
    ]);
    for (property, expected, other) in [
        (0xE2, "Armor Tinkering", "Magic Item Tinkering"),
        (0xE3, "Magic Item Tinkering", "Armor Tinkering"),
    ] {
        let mut s = CharacterInfoSource::default();
        s.ints.insert(property, 1);
        let r = build_report(&s, &strings);
        assert!(r.contains(expected), "{property:#x}");
        assert!(!r.contains(other), "{property:#x}");
    }
}

// OpenAC `CharacterControllerTests.Report_SpecializationAugmentationUsesMatchingSkillLine`
// (#255): each specialization gem property shows its own skill's line.
#[test]
fn each_specialization_augmentation_shows_its_own_skill() {
    let skills = [
        "Salvaging",
        "ItemTinkering",
        "ArmorTinkering",
        "MagicItemTinkering",
        "WeaponTinkering",
        "Gearcraft",
    ];
    let mut strings = CharacterInfoStrings::english();
    strings.augmentations = skills
        .iter()
        .map(|name| {
            (
                format!("ID_CharacterInfo_Augmentation_Spec_{name}"),
                vec![format!("<{name}>\n")],
            )
        })
        .collect();
    for (property, skill) in [
        (224, "Salvaging"),
        (225, "ItemTinkering"),
        (226, "ArmorTinkering"),
        (227, "MagicItemTinkering"),
        (228, "WeaponTinkering"),
        (293, "Gearcraft"),
    ] {
        let mut s = CharacterInfoSource::default();
        s.ints.insert(property, 1);
        let r = build_report(&s, &strings);
        assert!(r.contains(&format!("<{skill}>")), "{property}");
        for other in skills.iter().filter(|name| **name != skill) {
            assert!(!r.contains(&format!("<{other}>")), "{property}: {other}");
        }
    }
}

// OpenAC `CharacterControllerTests.Report_DoesNotAppendUnusedValueAfterSingleFragmentAugmentation`
// (#200): a one-part phrase drops the value it has no place for.
#[test]
fn a_one_part_augmentation_phrase_drops_its_value() {
    let mut strings = CharacterInfoStrings::english();
    strings.augmentations = HashMap::from([
        (
            "ID_CharacterInfo_Augmentation_DamageBonus".to_owned(),
            vec!["Damage bonus.\n".to_owned()],
        ),
        (
            "ID_CharacterInfo_Augmentation_JackOfAllTrades".to_owned(),
            vec!["Jack of All Trades.\n".to_owned()],
        ),
    ]);
    let mut s = CharacterInfoSource::default();
    s.ints.insert(0x135, 1);
    s.ints.insert(0x146, 1);
    let r = build_report(&s, &strings);
    assert!(r.contains("Damage bonus.\nJack of All Trades."), "{r}");
    assert!(!r.contains("Damage bonus.\n1Jack"), "{r}");
}

// ── Luminance auras (OpenAC issue #227,
//    `CharacterControllerTests.Report_*Luminance*`, `FromDat_ResolvesLuminanceStrings`) ──

/// `PropertyInt` ids: the luminance auras and the other rows the tests use.
const LUM_AUG_DAMAGE_RATING: u32 = 0x14D;
const LUM_AUG_ITEM_MANA_USAGE: u32 = 0x153;
const LUM_AUG_ITEM_MANA_GAIN: u32 = 0x154;
const LUM_AUG_SKILLED_SPEC: u32 = 0x158;
const LUM_AUG_ALL_SKILLS: u32 = 0x16D;
const AUGMENTATION_INNATE_STRENGTH: u32 = 0xDA;
const SUMMONING_MASTERY: u32 = 0x16A;

fn sheet(values: &[(u32, i32)]) -> CharacterInfoSource {
    CharacterInfoSource {
        ints: values.iter().copied().collect(),
        ..CharacterInfoSource::default()
    }
}

fn luminance_strings() -> CharacterInfoStrings {
    let mut strings = CharacterInfoStrings::english();
    strings.augmentations = [
        (
            "ID_CharacterInfo_Luminance_Header",
            &["Luminance Augmentations:\n\n"][..],
        ),
        (
            "ID_CharacterInfo_Luminance_Base_Damage",
            &["Aura of Valor. Damage ", ".\n\n"],
        ),
        (
            "ID_CharacterInfo_Luminance_Spec_Damage",
            &["Aura of Destruction. Seer damage ", ".\n\n"],
        ),
        (
            "ID_CharacterInfo_Luminance_Base_Mana_Use",
            &[
                "Aura of Mana Flow. The mana consumption of your items is decreased by a rating of ",
                ".\n\n",
            ],
        ),
        (
            "ID_CharacterInfo_Luminance_Base_Mana_Gain",
            &["Aura of Mana Infusion. Mana gain ", ".\n\n"],
        ),
        (
            "ID_CharacterInfo_Luminance_Spec_Skilled_Spec",
            &["Aura of the Specialist. Spec ", ".\n\n"],
        ),
        (
            "ID_CharacterInfo_Luminance_Base_All_Skills",
            &["Aura of the World. All skills ", ".\n\n"],
        ),
        (
            "ID_CharacterInfo_Augmentation_Attribute_Strength",
            &["Innate Strength augmented ", " {time[1]|times}.\n\n"],
        ),
    ]
    .into_iter()
    .map(|(k, v)| (k.to_owned(), v.iter().map(|s| (*s).to_owned()).collect()))
    .collect();
    strings
}

// Report_ListsManaFlowAuraUnderLuminanceHeader.
#[test]
fn the_mana_flow_aura_is_listed_under_the_luminance_header() {
    let r = build_report(
        &sheet(&[(LUM_AUG_ITEM_MANA_USAGE, 4)]),
        &luminance_strings(),
    );
    assert!(
        r.contains(
            "Luminance Augmentations:\n\n\
             Aura of Mana Flow. The mana consumption of your items is decreased by a rating of 20."
        ),
        "{r}"
    );
}

// Report_WritesLuminanceHeaderWithoutAnyAura.
#[test]
fn the_luminance_header_is_written_without_any_aura() {
    let r = build_report(&CharacterInfoSource::default(), &luminance_strings());
    assert!(r.contains("Luminance Augmentations:"), "{r}");
    assert!(!r.contains("Aura of"), "{r}");
}

// Report_SplitsTieredAuraRanksPastFiveIntoSeerLine (3, 5 and 8 ranks).
#[test]
fn a_tiered_auras_ranks_past_five_go_on_the_seers_line() {
    for (ranks, base, seer) in [
        (3, "Aura of Valor. Damage 3.", None),
        (5, "Aura of Valor. Damage 5.", None),
        (
            8,
            "Aura of Valor. Damage 5.",
            Some("Aura of Destruction. Seer damage 3."),
        ),
    ] {
        let r = build_report(
            &sheet(&[(LUM_AUG_DAMAGE_RATING, ranks)]),
            &luminance_strings(),
        );
        assert!(r.contains(base), "{ranks}: {r}");
        match seer {
            None => assert!(!r.contains("Aura of Destruction"), "{ranks}: {r}"),
            Some(seer) => assert!(r.contains(&format!("{base}\n\n{seer}")), "{ranks}: {r}"),
        }
    }
}

// Report_ScalesAndOrdersLuminanceAurasBeforeOtherAugmentations.
#[test]
fn luminance_auras_are_scaled_and_come_before_the_other_augmentations() {
    let r = build_report(
        &sheet(&[
            (AUGMENTATION_INNATE_STRENGTH, 1),
            (LUM_AUG_ALL_SKILLS, 2),
            (LUM_AUG_SKILLED_SPEC, 3),
            (LUM_AUG_ITEM_MANA_GAIN, 2),
            (SUMMONING_MASTERY, 1),
        ]),
        &luminance_strings(),
    );
    let at = |text: &str| r.find(text).unwrap_or_else(|| panic!("{text} in {r}"));
    let summoning = at("Your summoning mastery is Primalist.");
    let header = at("Luminance Augmentations:");
    let mana_gain = at("Aura of Mana Infusion. Mana gain 10.");
    let specialist = at("Aura of the Specialist. Spec 6.");
    let world = at("Aura of the World. All skills 2.");
    let strength = at("Innate Strength augmented");
    assert!(summoning < header, "{r}");
    assert!(header < mana_gain, "{r}");
    assert!(mana_gain < specialist, "{r}");
    assert!(specialist < world, "{r}");
    assert!(world < strength, "{r}");
}

// FromDat_ResolvesLuminanceStrings.
#[test]
fn the_luminance_phrases_are_read_from_the_dats() {
    let strings = CharacterInfoStrings::from_dat(&|key: &str| {
        key.contains("Luminance")
            .then(|| vec![format!("{key}: "), "\n".to_owned()])
    });
    for key in [
        "ID_CharacterInfo_Luminance_Header",
        "ID_CharacterInfo_Luminance_Base_Mana_Use",
        "ID_CharacterInfo_Luminance_Spec_Crit_Reduction",
    ] {
        assert!(strings.augmentations.contains_key(key), "{key}");
    }
    let r = build_report(&sheet(&[(LUM_AUG_ITEM_MANA_USAGE, 3)]), &strings);
    assert!(
        r.contains("ID_CharacterInfo_Luminance_Base_Mana_Use: 15"),
        "{r}"
    );
}

#[test]
fn the_innate_line_shows_the_starting_values() {
    let s = CharacterInfoSource {
        attributes: [290, 250, 240, 230, 220, 210],
        innate: [100, 60, 50, 40, 30, 10],
        ..CharacterInfoSource::default()
    };
    let r = report(&s);
    for wanted in [
        "Innate Strength: 100",
        "Innate Endurance: 60",
        "Innate Coordination: 50",
        "Innate Quickness: 40",
        "Innate Focus: 30",
        "Innate Self: 10",
    ] {
        assert!(r.contains(wanted), "{wanted}");
    }
}

#[test]
fn a_birth_date_reads_as_a_local_general_date() {
    assert_eq!(format_birth_date(0, 0), "1/1/1970 12:00:00 AM");
    assert_eq!(
        format_birth_date(1_787_313_600, 2 * 3600),
        "8/21/2026 2:00:00 PM"
    );
}

// ── Real dats ───────────────────────────────────────────────────────────

mod real_dats {
    use super::*;
    use ac_ui::content::DatContent;
    use ac_ui::font::DEFAULT_FONT_ID;
    use ac_ui::import::{LayoutSource, import, import_rooted};
    use ac_ui::panels::DatRowTemplates;
    use ac_ui::panels::character::CharacterStatController;
    use ac_ui::panels::character_info::{self as info, CharacterInfoController};
    use ac_ui::panels::character_titles::TitleNames;
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

    /// OpenAC `CharacterTitleResolverLiveDatTests`.
    #[test]
    #[ignore = "needs the retail dat files"]
    fn title_ids_resolve_to_their_retail_names() {
        let c = content();
        let mut names = TitleNames::new(c.dats());
        let s = c.strings();
        assert_eq!(names.resolve(s, 0), None);
        for (id, text) in [
            (1, "Adventurer"),
            (2, "Archer"),
            (3, "Blademaster"),
            (5, "Life Mage"),
            (13, "War Mage"),
            (14, "Wayfarer"),
        ] {
            assert_eq!(names.resolve(s, id).as_deref(), Some(text), "{id}");
        }
        assert_eq!(names.resolve(s, 0xFFFF_FFFE), None);
        assert_eq!(names.resolve(s, 13).as_deref(), Some("War Mage"));
    }

    /// OpenAC `CharacterTitlesControllerTests.Bind_FindsEveryTitlesPageElement_InTheRealImportedFixture`:
    /// the character window's titles page binds and lists titles from its
    /// own template.
    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_titles_page_binds_in_the_character_window_and_lists_titles() {
        let c = content();
        let mut host = UiHost::new();
        host.root.set_size(1024.0, 768.0);
        let layout = import(&mut host.root, &c, 0x2100_002E, c.font(DEFAULT_FONT_ID))
            .expect("character window");
        let r = host.root.root();
        host.root.add_child(r, layout.root);
        let mut ctl =
            CharacterStatController::bind(&mut host.root, &layout, c.font(DEFAULT_FONT_ID), None);
        let mut names = TitleNames::new(c.dats());
        let strings = c.strings();
        let mut resolve = |id| names.resolve(strings, id);
        let resolved: HashMap<u32, String> = [1, 5, 13]
            .into_iter()
            .filter_map(|id| resolve(id).map(|t| (id, t)))
            .collect();
        let lookup = |id: u32| resolved.get(&id).cloned();
        let mut rows = DatRowTemplates::new(&c, c.font(DEFAULT_FONT_ID));
        let t = ctl.titles_mut().expect("the titles page binds");
        t.update(
            &mut host.root,
            &view(13, &[13, 1, 5], 1, 1),
            &lookup,
            &mut rows,
        );
        let rows: Vec<(ElementId, u32)> = t.rows().to_vec();
        assert_eq!(rows.iter().map(|r| r.1).collect::<Vec<_>>(), [1, 5, 13]);
        let first = host
            .root
            .find_descendant(rows[0].0, titles::ROW_TEXT_ID)
            .expect("row text");
        assert_eq!(text_of(&host.root, first), "Adventurer");
        let list = t.list();
        let vp = host
            .root
            .widget::<ListBox>(list)
            .unwrap()
            .viewport_id()
            .unwrap();
        assert_eq!(
            host.root
                .widget::<ac_ui::widgets::ScrollablePanel>(vp)
                .unwrap()
                .line_height,
            24
        );
        assert!(c.take_errors().is_empty());
    }

    /// OpenAC `CharacterControllerTests.AuthoredPanel_BindsTextScrollbarAndClose`
    /// with the dat's own phrases.
    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_information_panel_binds_and_reports_with_the_dats_phrases() {
        let c = content();
        let mut host = UiHost::new();
        host.root.set_size(1024.0, 768.0);
        let layout = import_rooted(
            &mut host.root,
            &c,
            info::HOST_LAYOUT_ID,
            info::ROOT_ID,
            c.font(DEFAULT_FONT_ID),
        )
        .expect("slot 0x10000183");
        let r = host.root.root();
        host.root.add_child(r, layout.root);
        let text = layout.find(info::MAIN_TEXT_ID).expect("authored text");
        assert!(host.root.widget::<Text>(text).is_some());
        let bar = layout.find(info::SCROLLBAR_ID).expect("scrollbar");
        let mut ctl =
            CharacterInfoController::bind(&mut host.root, &layout, c.font(DEFAULT_FONT_ID));
        assert_eq!(ctl.text(), text);
        assert_eq!(
            host.root
                .widget::<ac_ui::widgets::Scrollbar>(bar)
                .unwrap()
                .model,
            ac_ui::widgets::ScrollSource::Element(text)
        );
        let strings = c.strings();
        let phrases = CharacterInfoStrings::from_dat(&|key| {
            strings.resolve_all(0x2300_0001, ac_ui::strings::compute_hash(key))
        });
        ctl.update(&mut host.root, &sample(), &phrases);
        let shown = text_of(&host.root, text);
        assert!(shown.contains("You were born on"), "{shown}");
        assert!(shown.contains("42"), "{shown}");
        let builds = ctl.layouts;
        ctl.update(&mut host.root, &sample(), &phrases);
        assert_eq!(
            ctl.layouts, builds,
            "an unchanged report is not laid out again"
        );
        assert!(layout.find(info::CLOSE_ID).is_some());
        assert!(c.take_errors().is_empty());
    }
}
