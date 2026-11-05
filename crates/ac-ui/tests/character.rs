//! The character window: attributes, vitals and skills with raises.
//!
//! Ported from OpenAC `tests/AcDream.App.Tests/UI/Layout/CharacterStatControllerTests.cs`
//! (header, rows, footer, raise buttons, tabs, deltas and colours, with
//! OpenAC's `SampleData.SampleCharacter` as the sheet) and
//! `CharacterSheetProviderTests.cs` (level progress, raise costs and the
//! sheet built from the local player). OpenAC's `RaiseRequestHandler` is
//! [`PanelOutput::RaiseAttribute`] and its siblings here; its awaiting-raise
//! gate is the controller's own, released by the next different sheet.

mod common;

use std::collections::HashMap;
use std::sync::Arc;

macro_rules! assert_dbg_eq {
    ($a:expr, $b:expr $(,)?) => {
        assert_eq!(format!("{:?}", $a), format!("{:?}", $b))
    };
}

use ac_core::items::PropertyBundle;
use ac_core::player::LocalPlayerState;
use ac_ui::ElementId;
use ac_ui::draw::Color;
use ac_ui::element_info::{ElementInfo, ImageMedia};
use ac_ui::event::kind;
use ac_ui::host::UiHost;
use ac_ui::import::ImportedLayout;
use ac_ui::panels::PanelOutput;
use ac_ui::panels::character::{
    self, BODY, BUFF_GREEN, CharacterSheet, CharacterSkill, CharacterStatController, CharacterTab,
    DEBUFF_RED, ExperienceCurves, Selection, SheetSource, SkillAdvancement, StatRow, VITAE_BLUE,
};
use ac_ui::panels::items::caption_of;
use ac_ui::root::UiRoot;
use ac_ui::tree::Element;
use ac_ui::widgets::{Button, DatElement, ListBox, Meter, ScrollSource, Scrollbar, Text};
use common::items::panel_outputs;
use common::*;

#[allow(clippy::too_many_arguments)]
fn skill(
    id: u32,
    name: &str,
    advancement: SkillAdvancement,
    base: i32,
    current: i32,
    usable: bool,
    trained: i32,
    raise: u64,
    raise10: u64,
) -> CharacterSkill {
    CharacterSkill {
        id,
        name: name.to_owned(),
        icon: 0x0600_0100 + id,
        advancement,
        base_level: base,
        current_level: current,
        usable_untrained: usable,
        trained_cost: trained,
        specialized_cost: trained * 2,
        raise_cost: raise,
        raise10_cost: raise10,
        vitae_modifier: 0,
        tooltip: None,
    }
}

/// OpenAC's `SampleData.SampleCharacter`.
fn sample() -> CharacterSheet {
    use SkillAdvancement::{Specialized as S, Trained as T, Untrained as U};
    CharacterSheet {
        name: "Studio Player".into(),
        level: Some(126),
        gender: Some("Female".into()),
        heritage: Some("Aluvian".into()),
        title: Some("the Adventurer".into()),
        pk_status: Some("Non-Player Killer".into()),
        total_xp: 1_250_000_000,
        xp_to_next_level: 42_000_000,
        xp_to_next_level_text: None,
        xp_fraction: 0.63,
        vital_current: [5, 10, 10],
        vital_max: [5, 10, 10],
        vital_base_max: [5, 10, 10],
        attributes: [200, 10, 10, 200, 10, 10],
        attribute_base: [200, 10, 10, 200, 10, 10],
        attribute_innate: [100, 10, 10, 100, 10, 10],
        skill_credits: 96,
        unassigned_xp: 87_757_321_741,
        attribute_raise_costs: [0, 95, 100, 0, 110, 105, 90, 88, 112],
        attribute_raise10_costs: [0, 950, 1_000, 0, 1_100, 1_050, 900, 880, 1_120],
        skills: vec![
            skill(
                6,
                "Melee Defense",
                S,
                350,
                354,
                false,
                10,
                18_250_000,
                182_500_000,
            ),
            skill(
                34,
                "War Magic",
                S,
                280,
                285,
                false,
                16,
                11_100_000,
                111_000_000,
            ),
            skill(
                14,
                "Arcane Lore",
                T,
                260,
                269,
                false,
                4,
                7_500_000,
                75_000_000,
            ),
            skill(
                33,
                "Life Magic",
                T,
                250,
                252,
                false,
                12,
                6_800_000,
                68_000_000,
            ),
            skill(
                47,
                "Missile Weapons",
                T,
                220,
                221,
                false,
                6,
                5_250_000,
                52_500_000,
            ),
            skill(21, "Healing", U, 10, 10, true, 6, 0, 0),
            skill(22, "Jump", U, 210, 210, true, 0, 0, 0),
            skill(36, "Loyalty", U, 10, 10, true, 0, 0, 0),
            skill(24, "Run", U, 390, 390, true, 0, 0, 0),
            skill(38, "Alchemy", U, 10, 10, false, 6, 0, 0),
            skill(39, "Cooking", U, 10, 10, false, 4, 0, 0),
            skill(37, "Fletching", U, 10, 10, false, 4, 0, 0),
        ],
        ..CharacterSheet::default()
    }
}

fn button_info(states: &[(&str, u32)]) -> Arc<ElementInfo> {
    let mut info = ElementInfo {
        kind: 1,
        width: 20.0,
        height: 20.0,
        default_state_name: states.first().map(|s| s.0.to_owned()).unwrap_or_default(),
        ..ElementInfo::default()
    };
    for (name, file) in states {
        info.state_media.insert(
            (*name).to_owned(),
            ImageMedia {
                file: *file,
                draw_mode: 1,
            },
        );
    }
    Arc::new(info)
}

fn el(id: u32, x: f32, y: f32, w: f32, h: f32) -> Element {
    Element {
        dat_element_id: id,
        ..Element::at(x, y, w, h)
    }
}

fn text(ui: &mut UiRoot, parent: ElementId, id: u32, x: f32, y: f32, w: f32) -> ElementId {
    let mut e = el(id, x, y, w, 16.0);
    Text::configure(&mut e);
    ui.add(parent, e, Text::default())
}

struct Win {
    host: UiHost,
    ids: HashMap<u32, ElementId>,
}

/// A character window shaped like 0x2100002E: the attributes page with the
/// header, the meter, the list and the two footers; a hidden skills page
/// repeating the header ids; the titles page; the tabs.
fn window() -> Win {
    window_with(true)
}

/// [`window`], its stat list a list box or (`false`) a plain element.
fn window_with(list_box: bool) -> Win {
    let mut host = UiHost::new();
    host.root.set_size(800.0, 700.0);
    let ui = &mut host.root;
    let r = ui.root();
    let root = panel(ui, r, at(0.0, 0.0, 300.0, 600.0));
    let mut ids = HashMap::new();
    let page = ui.add(
        root,
        el(character::ATTRIBUTES_PAGE_ID, 0.0, 25.0, 300.0, 575.0),
        ac_ui::widgets::Panel::default(),
    );
    let hidden = ui.add(
        root,
        el(character::SKILLS_PAGE_ID, 0.0, 25.0, 300.0, 575.0),
        ac_ui::widgets::Panel::default(),
    );
    let titles = ui.add(
        root,
        el(character::TITLES_PAGE_ID, 0.0, 25.0, 300.0, 575.0),
        ac_ui::widgets::Panel::default(),
    );
    ids.insert(character::ATTRIBUTES_PAGE_ID, page);
    ids.insert(character::SKILLS_PAGE_ID, hidden);
    ids.insert(character::TITLES_PAGE_ID, titles);
    for p in [hidden, page] {
        for (i, id) in [
            character::NAME_ID,
            character::HERITAGE_ID,
            character::PK_STATUS_ID,
            character::LEVEL_ID,
            character::LEVEL_CAPTION_ID,
            character::TOTAL_XP_LABEL_ID,
            character::TOTAL_XP_ID,
            character::LUMINANCE_LABEL_ID,
            character::LUMINANCE_VALUE_ID,
        ]
        .into_iter()
        .enumerate()
        {
            let t = text(ui, p, id, 0.0, i as f32 * 16.0, 200.0);
            ids.insert(id, t);
        }
    }
    let meter = ui.add(
        page,
        el(character::XP_METER_ID, 0.0, 150.0, 280.0, 30.0),
        Meter::default(),
    );
    ids.insert(character::XP_METER_ID, meter);
    for (id, x) in [
        (character::XP_NEXT_LABEL_ID, 0.0),
        (character::XP_NEXT_VALUE_ID, 150.0),
    ] {
        ids.insert(id, text(ui, meter, id, x, 0.0, 120.0));
    }
    let list_el = el(character::LIST_BOX_ID, 5.0, 200.0, 282.0, 200.0);
    let list = if list_box {
        ui.add(
            page,
            list_el,
            ListBox::new(Arc::new(ElementInfo::default())),
        )
    } else {
        ui.add(page, list_el, ac_ui::widgets::Panel::default())
    };
    ids.insert(character::LIST_BOX_ID, list);
    let bar = ui.add(
        page,
        el(character::LIST_SCROLLBAR_ID, 287.0, 200.0, 13.0, 200.0),
        Scrollbar::default(),
    );
    ids.insert(character::LIST_SCROLLBAR_ID, bar);
    for (gid, y) in [
        (character::FOOTER_STATE_A_ID, 420.0),
        (character::FOOTER_STATE_B_ID, 420.0),
    ] {
        let g = ui.add(
            page,
            el(gid, 0.0, y, 300.0, 80.0),
            DatElement::new(Arc::new(ElementInfo::default())),
        );
        ids.insert(gid, g);
        for (id, x, y) in [
            (character::FOOTER_TITLE_ID, 0.0, 0.0),
            (character::FOOTER_LINE1_LABEL, 5.0, 20.0),
            (character::FOOTER_LINE1_VALUE, 200.0, 20.0),
            (character::FOOTER_LINE2_LABEL, 5.0, 37.0),
            (character::FOOTER_LINE2_VALUE, 200.0, 37.0),
        ] {
            // The idle footer's texts carry no ids (found by position).
            let id = if gid == character::FOOTER_STATE_A_ID {
                0
            } else {
                id
            };
            text(ui, g, id, x, y, 90.0);
        }
    }
    for (id, x) in [
        (character::RAISE_ONE_ID, 0.0),
        (character::RAISE_TEN_ID, 30.0),
    ] {
        let b = ui.add(
            page,
            el(id, x, 500.0, 20.0, 20.0),
            Button::new(
                button_info(&[("Normal", 1), ("Ghosted", 2)]),
                None,
                Vec::new(),
                0.0,
            ),
        );
        ids.insert(id, b);
    }
    for (id, x) in [
        (character::TAB_ATTRIBUTES_ID, 0.0),
        (character::TAB_SKILLS_ID, 80.0),
        (character::TAB_TITLES_ID, 160.0),
        (character::CLOSE_BUTTON_ID, 280.0),
    ] {
        let b = ui.add(
            root,
            el(id, x, 0.0, 70.0, 20.0),
            Button::new(
                button_info(&[("Closed", 1), ("Open", 2)]),
                None,
                Vec::new(),
                0.0,
            ),
        );
        ids.insert(id, b);
    }
    // The visible page's elements win over the hidden page's.
    let layout = ImportedLayout::from_parts(root, ids.clone());
    ui.register_window("character", root, None, 0).unwrap();
    let c = CharacterStatController::bind(ui, &layout, None, None);
    host.attach_controller("character", Box::new(c)).unwrap();
    let mut w = Win { host, ids };
    w.apply(&sample());
    w
}

impl Win {
    fn with<R>(&mut self, f: impl FnOnce(&mut CharacterStatController, &mut UiRoot) -> R) -> R {
        self.host
            .with_controller::<CharacterStatController, _>("character", f)
            .unwrap()
    }

    fn apply(&mut self, sheet: &CharacterSheet) {
        let s = sheet.clone();
        self.with(move |c, ui| c.apply(ui, &s));
    }

    fn page_text(&self, id: u32) -> String {
        let page = self.ids[&character::ATTRIBUTES_PAGE_ID];
        let t = self.host.root.find_descendant(page, id);
        caption_of(&self.host.root, t)
    }

    fn page_color(&self, id: u32) -> Color {
        let page = self.ids[&character::ATTRIBUTES_PAGE_ID];
        let t = self.host.root.find_descendant(page, id).unwrap();
        let w = self.host.root.widget::<Text>(t).unwrap();
        match w.content() {
            ac_ui::widgets::TextContent::Lines(l) => l[0].color,
            _ => Color::WHITE,
        }
    }

    fn rows(&mut self) -> Vec<(ElementId, Option<usize>, Option<u32>)> {
        self.with(|c, _| c.rows())
    }

    fn click(&mut self, e: ElementId) {
        self.host.root.fire_event(kind::CLICK, e, None);
        self.host.pump();
    }

    fn click_id(&mut self, id: u32) {
        let e = self.ids[&id];
        self.click(e);
    }

    fn click_attribute(&mut self, index: usize) {
        let row = self
            .rows()
            .into_iter()
            .find(|r| r.1 == Some(index))
            .unwrap()
            .0;
        self.click(row);
    }

    fn click_skill(&mut self, id: u32) {
        let row = self.rows().into_iter().find(|r| r.2 == Some(id)).unwrap().0;
        self.click(row);
    }

    fn footer(&mut self) -> (String, String, String, String, String) {
        let (titles, lines, selected) =
            self.with(|c, _| (c.footer_titles(), c.footer_lines(), c.selection()));
        let tab = self.with(|c, _| c.current_tab());
        let any = match tab {
            CharacterTab::Skills => selected.skill.is_some(),
            _ => selected.attribute.is_some(),
        };
        let i = usize::from(any);
        let ui = &self.host.root;
        (
            caption_of(ui, titles[i]),
            caption_of(ui, lines[i][0]),
            caption_of(ui, lines[i][1]),
            caption_of(ui, lines[i][2]),
            caption_of(ui, lines[i][3]),
        )
    }

    fn visible(&self, e: ElementId) -> bool {
        self.host.root.element(e).unwrap().visible
    }

    fn state(&self, id: u32) -> String {
        self.host
            .root
            .widget::<Button>(self.ids[&id])
            .unwrap()
            .active_state()
            .to_owned()
    }

    fn value_text(&mut self, row: ElementId) -> (String, Color) {
        let v = self.with(|c, _| c.row_value_element(row)).unwrap();
        let w = self.host.root.widget::<Text>(v).unwrap();
        match w.content() {
            ac_ui::widgets::TextContent::Lines(l) => (l[0].text.clone(), l[0].color),
            _ => (String::new(), Color::WHITE),
        }
    }
}

// ── Header ──────────────────────────────────────────────────────────────

#[test]
fn the_header_shows_name_level_heritage_title_and_pk_status_on_the_visible_page() {
    let w = window();
    assert_eq!(w.page_text(character::NAME_ID), "Studio Player");
    assert_eq!(w.page_text(character::LEVEL_ID), "126");
    assert_eq!(
        w.page_text(character::HERITAGE_ID),
        "Female Aluvian the Adventurer"
    );
    assert_eq!(w.page_text(character::PK_STATUS_ID), "Non-Player Killer");
    // The hidden page's copies stay empty.
    let hidden = w.ids[&character::SKILLS_PAGE_ID];
    let t = w.host.root.find_descendant(hidden, character::NAME_ID);
    assert_eq!(caption_of(&w.host.root, t), "");
    assert!(!w.visible(hidden));
    assert_eq!(
        w.page_text(character::TOTAL_XP_LABEL_ID),
        "Total Experience (XP):"
    );
    assert_eq!(w.page_text(character::TOTAL_XP_ID), "1,250,000,000");
    let total = w.ids[&character::TOTAL_XP_ID];
    assert!(w.host.root.widget::<Text>(total).unwrap().right_aligned);
    assert_eq!(
        w.page_text(character::XP_NEXT_LABEL_ID),
        "XP for next level:"
    );
    assert_eq!(w.page_text(character::XP_NEXT_VALUE_ID), "42,000,000");
    let meter = w.ids[&character::XP_METER_ID];
    assert!((w.host.root.widget::<Meter>(meter).unwrap().fill.unwrap() - 0.63).abs() < 1e-6);
    let caption = w.ids[&character::LEVEL_CAPTION_ID];
    let lines = w.host.root.widget::<Text>(caption).unwrap().content_lines();
    assert_eq!(
        lines.iter().map(|l| l.text.as_str()).collect::<Vec<_>>(),
        vec!["Character", "Level"]
    );
}

#[test]
fn an_unknown_level_reads_question_marks_and_no_pk_status_is_empty() {
    let mut w = window();
    w.apply(&CharacterSheet {
        level: None,
        pk_status: None,
        ..sample()
    });
    assert_eq!(w.page_text(character::LEVEL_ID), "???");
    assert_eq!(w.page_text(character::PK_STATUS_ID), "");
    assert_eq!(w.page_color(character::LEVEL_ID), Color::WHITE);
}

#[test]
fn the_identity_line_joins_gender_heritage_and_the_title_verbatim() {
    let s = CharacterSheet {
        gender: Some("Male".into()),
        heritage: Some("Aluvian".into()),
        title: Some("The Noob".into()),
        ..CharacterSheet::default()
    };
    assert_eq!(character::stat_header_line(&s), "Male Aluvian The Noob");
    let no_gender = CharacterSheet {
        gender: None,
        ..s.clone()
    };
    assert_eq!(character::stat_header_line(&no_gender), "Aluvian The Noob");
    assert_eq!(character::gender_name(1), Some("Male"));
    assert_eq!(character::gender_name(2), Some("Female"));
    assert_eq!(character::gender_name(0), None);
    assert_eq!(character::heritage_name(1), Some("Aluvian"));
    assert_eq!(character::heritage_name(2), Some("Gharu'ndim"));
    assert_eq!(character::heritage_name(5), Some("Umbraen"));
    assert_eq!(character::heritage_name(13), Some("Olthoi"));
    assert_eq!(character::heritage_name(99), None);
}

#[test]
fn luminance_shows_from_level_two_hundred_with_a_maximum() {
    for (level, max, shown) in [
        (Some(199), 1000, false),
        (Some(200), 0, false),
        (Some(200), 1000, true),
        (None, 1000, false),
    ] {
        let mut w = window();
        w.apply(&CharacterSheet {
            level,
            available_luminance: 500,
            maximum_luminance: max,
            ..sample()
        });
        let label = w.page_text(character::LUMINANCE_LABEL_ID);
        let value = w.page_text(character::LUMINANCE_VALUE_ID);
        if shown {
            assert_eq!(label, "Luminance:");
            assert_eq!(value, "500 / 1,000");
        } else {
            assert_eq!(
                (label.as_str(), value.as_str()),
                ("", ""),
                "{level:?} {max}"
            );
        }
    }
}

// ── Attribute rows and the footer ───────────────────────────────────────

#[test]
fn the_attribute_list_has_nine_rows_in_retail_order_with_values_and_icons() {
    let mut w = window();
    let rows = w.rows();
    assert_eq!(rows.len(), 9);
    let names: Vec<&str> = (0..9).map(character::row_name).collect();
    assert_eq!(
        names,
        vec![
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
    let values: Vec<String> = rows.iter().map(|r| w.value_text(r.0).0).collect();
    assert_eq!(
        values,
        vec![
            "200", "10", "10", "200", "10", "10", "5/5", "10/10", "10/10"
        ]
    );
    let first = w.host.root.widget::<StatRow>(rows[0].0).unwrap();
    assert_eq!(first.icon, 0x0600_02C8);
    assert_eq!(first.background, character::ROW_NORMAL_SPRITE);
    assert_eq!(
        first.tooltip.as_deref(),
        Some("Measures your character's muscular power.")
    );
    let e = w.host.root.element(rows[0].0).unwrap();
    assert_eq!((e.width, e.height), (282.0, 20.0));
    assert_eq!(e.authored.tooltip_root_element_id, 0x1000_0395);
    // The list's scrollbar drives the rows' viewport.
    let bar = w.ids[&character::LIST_SCROLLBAR_ID];
    assert!(matches!(
        w.host.root.widget::<Scrollbar>(bar).unwrap().model,
        ScrollSource::Element(_)
    ));
}

#[test]
fn the_idle_footer_prompts_and_shows_credits_and_unassigned_experience() {
    let mut w = window();
    let a = w.ids[&character::FOOTER_STATE_A_ID];
    let b = w.ids[&character::FOOTER_STATE_B_ID];
    assert!(w.visible(a) && !w.visible(b));
    assert_eq!(
        w.footer(),
        (
            "Select an Attribute to Improve".into(),
            "Skill Credits Available:".into(),
            "96".into(),
            "Unassigned Experience:".into(),
            "87,757,321,741".into()
        )
    );
}

#[test]
fn clicking_focus_selects_it_and_the_footer_prices_the_raise() {
    let mut w = window();
    w.click_attribute(4);
    let a = w.ids[&character::FOOTER_STATE_A_ID];
    let b = w.ids[&character::FOOTER_STATE_B_ID];
    assert!(!w.visible(a) && w.visible(b));
    assert_eq!(
        w.footer(),
        (
            "Focus: 10".into(),
            "Experience To Raise:".into(),
            "110".into(),
            "Unassigned Experience:".into(),
            "87,757,321,741".into()
        )
    );
    let rows = w.rows();
    let bg = |w: &Win, i: usize| w.host.root.widget::<StatRow>(rows[i].0).unwrap().background;
    assert_eq!(bg(&w, 4), character::ROW_HIGHLIGHT_SPRITE);
    assert_eq!(bg(&w, 0), character::ROW_NORMAL_SPRITE);
    // Another row moves the selection; the same row again clears it.
    w.click_attribute(1);
    assert_eq!(w.footer().0, "Endurance: 10");
    assert_eq!(bg(&w, 4), character::ROW_NORMAL_SPRITE);
    w.click_attribute(1);
    assert_eq!(w.footer().0, "Select an Attribute to Improve");
    assert_eq!(bg(&w, 1), character::ROW_NORMAL_SPRITE);
    assert!(w.visible(a) && !w.visible(b));
}

#[test]
fn a_maxed_row_prices_at_infinity() {
    let mut w = window();
    w.click_attribute(0);
    assert_eq!(w.footer().2, "Infinity!");
}

// ── Raise buttons ───────────────────────────────────────────────────────

#[test]
fn the_raise_buttons_hide_until_a_row_is_selected_and_ghost_when_unaffordable() {
    let mut w = window();
    let one = w.ids[&character::RAISE_ONE_ID];
    let ten = w.ids[&character::RAISE_TEN_ID];
    assert!(!w.visible(one) && !w.visible(ten));
    w.click_attribute(4);
    assert!(w.visible(one) && w.visible(ten));
    assert_eq!(w.state(character::RAISE_ONE_ID), "Normal");
    assert_eq!(w.state(character::RAISE_TEN_ID), "Normal");
    // A maxed row ghosts both.
    w.click_attribute(0);
    assert_eq!(w.state(character::RAISE_ONE_ID), "Ghosted");
    assert_eq!(w.state(character::RAISE_TEN_ID), "Ghosted");
    // Experience for one raise but not ten splits them.
    w.apply(&CharacterSheet {
        unassigned_xp: 500,
        ..sample()
    });
    w.click_attribute(4);
    assert_eq!(w.state(character::RAISE_ONE_ID), "Normal");
    assert_eq!(w.state(character::RAISE_TEN_ID), "Ghosted");
    w.click_id(character::RAISE_TEN_ID);
    assert!(panel_outputs(&mut w.host).is_empty(), "ten is unaffordable");
    // Deselecting hides them.
    w.click_attribute(4);
    assert!(!w.visible(one) && !w.visible(ten));
}

#[test]
fn raising_focus_sends_one_raise_and_waits_for_the_next_sheet() {
    let mut w = window();
    w.click_attribute(4);
    panel_outputs(&mut w.host);
    w.click_id(character::RAISE_ONE_ID);
    assert_dbg_eq!(
        panel_outputs(&mut w.host),
        vec![PanelOutput::RaiseAttribute {
            attribute: 5,
            xp: 110
        }]
    );
    assert_eq!(w.state(character::RAISE_ONE_ID), "Ghosted");
    w.click_id(character::RAISE_ONE_ID);
    assert!(panel_outputs(&mut w.host).is_empty(), "one raise at a time");
    // The same sheet again keeps the gate; the server's answer opens it.
    w.apply(&sample());
    assert!(w.with(|c, _| c.awaiting_raise()));
    let mut raised = sample();
    raised.attributes[4] = 11;
    raised.attribute_base[4] = 11;
    raised.attribute_raise_costs[4] = 120;
    w.apply(&raised);
    assert!(!w.with(|c, _| c.awaiting_raise()));
    assert_eq!(w.state(character::RAISE_ONE_ID), "Normal");
    assert_eq!(w.footer().0, "Focus: 11");
    w.click_id(character::RAISE_TEN_ID);
    assert_dbg_eq!(
        panel_outputs(&mut w.host),
        vec![PanelOutput::RaiseAttribute {
            attribute: 5,
            xp: 1_100
        }]
    );
}

#[test]
fn raising_a_vital_names_its_maximum() {
    let mut w = window();
    w.click_attribute(7);
    panel_outputs(&mut w.host);
    w.click_id(character::RAISE_ONE_ID);
    assert_dbg_eq!(
        panel_outputs(&mut w.host),
        vec![PanelOutput::RaiseVital { vital: 3, xp: 88 }]
    );
}

/// The viewport made in a plain stat list takes its margins when it is
/// made, so a list that grew while the window was hidden shows its rows
/// at the full height. OpenAC `CharacterStatController` captures the
/// viewport's baseline right after adding it.
#[test]
fn a_viewport_made_in_a_plain_list_follows_a_list_that_grew_while_hidden() {
    let mut w = window_with(false);
    let list = w.ids[&character::LIST_BOX_ID];
    let bar = w.ids[&character::LIST_SCROLLBAR_ID];
    let ScrollSource::Element(viewport) = w.host.root.widget::<Scrollbar>(bar).unwrap().model
    else {
        panic!("the scrollbar follows the viewport");
    };
    assert_eq!(w.host.root.parent(viewport), Some(list));
    let page = w.ids[&character::ATTRIBUTES_PAGE_ID];
    let window = w.host.root.parent(page).unwrap();
    w.host.root.element_mut(window).unwrap().visible = false;
    w.host.root.element_mut(list).unwrap().height = 350.0;
    w.host.root.element_mut(window).unwrap().visible = true;
    w.host.draw((800.0, 700.0), &ac_ui::draw::NoSprites);
    assert_eq!(w.host.root.element(viewport).unwrap().height, 350.0);
}

// ── Tabs and skills ─────────────────────────────────────────────────────

#[test]
fn the_tabs_switch_lists_and_open_their_buttons() {
    let mut w = window();
    assert_eq!(w.state(character::TAB_ATTRIBUTES_ID), "Open");
    assert_eq!(w.state(character::TAB_SKILLS_ID), "Closed");
    w.click_id(character::TAB_SKILLS_ID);
    assert_eq!(w.with(|c, _| c.current_tab()), CharacterTab::Skills);
    assert_eq!(w.state(character::TAB_ATTRIBUTES_ID), "Closed");
    assert_eq!(w.state(character::TAB_SKILLS_ID), "Open");
    // Retail buckets, each by name.
    let ids: Vec<u32> = w.rows().iter().filter_map(|r| r.2).collect();
    assert_eq!(ids, vec![6, 34, 14, 33, 47, 21, 22, 36, 24, 38, 39, 37]);
    assert_eq!(w.footer().0, "Select a Skill to Improve");
    // Titles: its page shows, the stats page hides, no raise buttons.
    w.click_id(character::TAB_TITLES_ID);
    let titles = w.ids[&character::TITLES_PAGE_ID];
    let page = w.ids[&character::ATTRIBUTES_PAGE_ID];
    assert!(w.visible(titles) && !w.visible(page));
    let one = w.ids[&character::RAISE_ONE_ID];
    assert!(!w.visible(one));
    // Back to the attributes.
    w.click_id(character::TAB_ATTRIBUTES_ID);
    assert!(w.visible(page) && !w.visible(titles));
    assert_eq!(w.rows().len(), 9);
}

#[test]
fn a_trained_skill_prices_in_experience_and_raises_by_ten() {
    let mut w = window();
    w.click_id(character::TAB_SKILLS_ID);
    w.click_skill(34);
    assert_eq!(
        w.footer(),
        (
            "War Magic: 285 (+5)".into(),
            "Experience To Raise:".into(),
            "11,100,000".into(),
            "Unassigned Experience:".into(),
            "87,757,321,741".into()
        )
    );
    let ten = w.ids[&character::RAISE_TEN_ID];
    assert!(w.visible(ten));
    panel_outputs(&mut w.host);
    w.click_id(character::RAISE_TEN_ID);
    assert_dbg_eq!(
        panel_outputs(&mut w.host),
        vec![PanelOutput::RaiseSkill {
            skill: 34,
            xp: 111_000_000
        }]
    );
}

#[test]
fn an_untrained_skill_prices_in_credits_and_trains() {
    let mut w = window();
    w.click_id(character::TAB_SKILLS_ID);
    w.click_skill(21);
    assert_eq!(
        w.footer(),
        (
            "Healing".into(),
            "Skill Credits To Raise:".into(),
            "6".into(),
            "Skill Credits Available:".into(),
            "96".into()
        )
    );
    let ten = w.ids[&character::RAISE_TEN_ID];
    assert!(!w.visible(ten), "no raise by ten for an untrained skill");
    panel_outputs(&mut w.host);
    w.click_id(character::RAISE_ONE_ID);
    assert_dbg_eq!(
        panel_outputs(&mut w.host),
        vec![PanelOutput::TrainSkill {
            skill: 21,
            credits: 6
        }]
    );
    // The trained skill moves to its group and stays selected.
    let mut trained = sample();
    let h = trained.skills.iter_mut().find(|s| s.id == 21).unwrap();
    h.advancement = SkillAdvancement::Trained;
    h.raise_cost = 1_000;
    trained.skill_credits = 90;
    w.apply(&trained);
    let ids: Vec<u32> = w.rows().iter().filter_map(|r| r.2).collect();
    assert_eq!(ids, vec![6, 34, 14, 21, 33, 47, 22, 36, 24, 38, 39, 37]);
    assert_eq!(w.with(|c, _| c.selection().skill), Some(21));
    assert_eq!(w.footer().1, "Experience To Raise:");
}

#[test]
fn skill_values_and_colours_follow_the_sheet_without_a_rebuild() {
    let mut w = window();
    w.click_id(character::TAB_SKILLS_ID);
    let before = w.rows();
    let row = before.iter().find(|r| r.2 == Some(14)).unwrap().0;
    assert_eq!(w.value_text(row), ("269".to_owned(), BUFF_GREEN));
    let mut s = sample();
    s.skills
        .iter_mut()
        .find(|k| k.id == 14)
        .unwrap()
        .current_level = 250;
    s.unassigned_xp = 10;
    w.apply(&s);
    assert_eq!(w.rows(), before, "same layout, same rows");
    assert_eq!(w.value_text(row), ("250".to_owned(), DEBUFF_RED));
    w.click_skill(14);
    assert_eq!(w.state(character::RAISE_ONE_ID), "Ghosted");
}

// ── Deltas and colours ──────────────────────────────────────────────────

#[test]
fn deltas_and_colours_follow_the_retail_residual_comparison() {
    let mut s = sample();
    s.attributes[0] = 210;
    s.attributes[1] = 8;
    assert_eq!(character::attribute_delta(&s, 0), 10);
    assert_eq!(character::attribute_delta(&s, 1), -2);
    assert_eq!(character::attribute_delta(&s, 6), 0);
    assert_eq!(character::attribute_value_color(&s, 0), BUFF_GREEN);
    assert_eq!(character::attribute_value_color(&s, 1), DEBUFF_RED);
    assert_eq!(character::attribute_value_color(&s, 2), Color::WHITE);
    s.vital_max[0] = 15;
    s.vital_vitae[0] = -2;
    assert_eq!(character::vital_buff_delta(&s, 0), 12);
    assert_eq!(character::attribute_value_color(&s, 6), BUFF_GREEN);
    let mut k = skill(1, "x", SkillAdvancement::Trained, 100, 95, true, 0, 0, 0);
    k.vitae_modifier = -5;
    assert_eq!(character::skill_buff_delta(&k), 0);
    assert_eq!(character::skill_value_color(&k), Color::WHITE);
    k.current_level = 105;
    assert_eq!(character::skill_buff_delta(&k), 10);
    // Footer runs: vitae in the vitae colour, the buff in the buff colour.
    let mut sheet = sample();
    sheet.skills.push(k);
    let runs = character::footer_title_runs(
        CharacterTab::Skills,
        &sheet,
        Selection {
            attribute: None,
            skill: Some(1),
        },
        &[],
    );
    let texts: Vec<(&str, Color)> = runs.iter().map(|r| (r.text.as_str(), r.color)).collect();
    assert_eq!(
        texts,
        vec![
            ("x: 105", Color::WHITE),
            (" (-5)", VITAE_BLUE),
            (" (+10)", BUFF_GREEN)
        ]
    );
    let attr = character::footer_title_runs(
        CharacterTab::Attributes,
        &s,
        Selection {
            attribute: Some(1),
            skill: None,
        },
        &[
            Color::WHITE,
            Color::rgba(0.0, 0.5, 0.0, 1.0),
            Color::rgba(0.5, 0.0, 0.0, 1.0),
        ],
    );
    assert_eq!(attr[1].text, " (-2)");
    assert_eq!(
        attr[1].color,
        Color::rgba(0.5, 0.0, 0.0, 1.0),
        "the authored palette"
    );
    let prompt =
        character::footer_title_runs(CharacterTab::Attributes, &s, Selection::default(), &[]);
    assert_eq!(prompt[0].color, BODY);
}

// VitalFooter_ShowsVitaeBeforeBuffAndUpdatesAfterRecovery (OpenAC v0.1.25,
// #211): a vital's footer shows its vitae penalty in the vitae
// colour before the spell delta, and drops it once vitae is gone.
#[test]
fn a_vital_footer_shows_vitae_before_the_buff_and_updates_after_recovery() {
    let palette = [
        Color::WHITE,
        Color::rgba(0.0, 1.0, 0.0, 1.0),
        Color::rgba(1.0, 0.0, 0.0, 1.0),
        Color::rgba(0.2, 0.7, 0.8, 1.0),
    ];
    for (row, name) in [(6, "Health"), (7, "Stamina"), (8, "Mana")] {
        let mut s = sample();
        s.vital_current = [198; 3];
        s.vital_max = [198; 3];
        s.vital_base_max = [180; 3];
        s.vital_vitae = [-5; 3];
        let sel = Selection {
            attribute: Some(row),
            skill: None,
        };
        let runs = character::footer_title_runs(CharacterTab::Attributes, &s, sel, &palette);
        let text: String = runs.iter().map(|r| r.text.as_str()).collect();
        assert_eq!(text, format!("{name}: 198/198 (-5) (+23)"));
        assert_eq!(runs[1].color, palette[3]);
        assert_eq!(runs[2].color, palette[1]);

        s.vital_vitae[row - 6] = 0;
        let text: String =
            character::footer_title_runs(CharacterTab::Attributes, &s, sel, &palette)
                .iter()
                .map(|r| r.text.as_str())
                .collect();
        assert_eq!(text, format!("{name}: 198/198 (+18)"));
    }
}

#[test]
fn raise_costs_index_the_sheet_by_row_and_amount() {
    let s = sample();
    assert_eq!(character::raise_cost(&s, 4, 1), 110);
    assert_eq!(character::raise_cost(&s, 4, 10), 1_100);
    assert_eq!(character::raise_cost(&s, 0, 1), 0);
    assert_eq!(character::raise_cost(&s, 99, 1), 0);
    assert_eq!(character::row_name(0), "Strength");
    assert_eq!(character::row_name(4), "Focus");
    assert_eq!(character::row_name(6), "Health");
    assert_eq!(character::row_name(usize::MAX), "");
}

// ── The sheet from the player (CharacterSheetProviderTests) ─────────────

#[test]
fn raise_costs_come_from_the_experience_curve() {
    let curve = [0, 10, 30, 60, 100];
    assert_eq!(character::raise_cost_from_curve(&curve, 1, 10, 1), 20);
    assert_eq!(
        character::raise_cost_from_curve(&curve, 1, 10, 10),
        90,
        "capped at the top"
    );
    assert_eq!(
        character::raise_cost_from_curve(&curve, 4, 100, 1),
        0,
        "at the top"
    );
    assert_eq!(
        character::raise_cost_from_curve(&curve, 1, 40, 1),
        0,
        "already past"
    );
    assert_eq!(character::raise_cost_from_curve(&[], 0, 0, 1), 0);
    assert_eq!(character::raise_cost_from_curve(&curve, 0, 0, 0), 0);
}

#[test]
fn level_progress_measures_toward_the_next_level_and_knows_the_top() {
    let levels = [0, 1_000, 3_000];
    assert_eq!(
        character::level_progress(&levels, 1, 2_000),
        (1_000, 0.5, false)
    );
    assert_eq!(character::level_progress(&levels, 2, 5_000), (0, 0.0, true));
    assert_eq!(
        character::level_progress(&levels, 1, 500),
        (2_000, 0.0, false)
    );
    assert_eq!(character::level_progress(&[], 1, 0), (0, 0.0, false));
    assert_eq!(character::format_xp(87_757_321_741), "87,757,321,741");
    assert_eq!(character::format_xp(0), "0");
    assert_eq!(character::format_xp(999), "999");
    assert_eq!(character::format_xp(1_000), "1,000");
}

#[test]
fn a_sheet_built_from_the_local_player_reads_levels_costs_and_skills() {
    let mut p = LocalPlayerState::new(None);
    p.on_attribute_update(5, 10, 50, 30);
    p.on_attribute_update(1, 0, 100, 0);
    p.on_vital_update(3, 4, 0, 40, 77);
    p.on_skill_update(34, 10, 3, 60, 0, 0, 0.0, 100);
    p.on_skill_update(21, 0, 1, 0, 0, 0, 0.0, 10);
    let mut props = PropertyBundle::default();
    props.ints.insert(0x19, 1);
    props.ints.insert(0x18, 96);
    props.ints.insert(0x71, 2);
    props.ints.insert(0xBC, 1);
    props.int64s.insert(1, 2_000);
    props.int64s.insert(2, 5_000);
    let xp = ExperienceCurves {
        levels: vec![0, 1_000, 3_000],
        attributes: (0..20).map(|i| i * 10).collect(),
        vitals: (0..20).map(|i| i * 20).collect(),
        trained_skills: (0..20).map(|i| i * 5).collect(),
        specialized_skills: (0..20).map(|i| i * 7).collect(),
    };
    let ui_string = |key: &str| Some(format!("<{key}>"));
    let sheet = CharacterSheet::build(&SheetSource {
        player: &p,
        properties: &props,
        pwd_bitfield: 0x20,
        name: "Horan",
        title: Some("the Adventurer"),
        experience: Some(&xp),
        skill_table: None,
        ui_string: Some(&ui_string),
    });
    assert_eq!(sheet.name, "Horan");
    assert_eq!(sheet.level, Some(1));
    assert_eq!(sheet.gender.as_deref(), Some("Female"));
    assert_eq!(sheet.heritage.as_deref(), Some("Aluvian"));
    assert_eq!(
        sheet.pk_status.as_deref(),
        Some("<ID_StatManagement_Header_PKStatus_PK>")
    );
    assert_eq!((sheet.xp_to_next_level, sheet.xp_fraction), (1_000, 0.5));
    assert_eq!(sheet.unassigned_xp, 5_000);
    assert_eq!(sheet.skill_credits, 96);
    // Focus: rank 10 with 30 spent: 110 - 30 to rank 11, 200 - 30 to 20.
    assert_eq!(sheet.attribute_raise_costs[4], 80);
    assert_eq!(sheet.attribute_raise10_costs[4], 160);
    assert_eq!(sheet.attribute_base[4], 60);
    // Stamina: rank 4 with 40 spent.
    assert_eq!(sheet.attribute_raise_costs[7], 60);
    let war = sheet.skill(34).unwrap();
    assert_eq!(war.advancement, SkillAdvancement::Specialized);
    assert_eq!(war.raise_cost, 77 - 60);
    assert_eq!(war.name, "War Magic");
    let healing = sheet.skill(21).unwrap();
    assert_eq!(healing.advancement, SkillAdvancement::Untrained);
    assert!(healing.usable_untrained);

    // An allegiance rank puts its title before the name (OpenAC
    // `AllegianceRankTitleTable.ComposeFullName`: rank 3 of an Aluvian
    // woman is "Baroness"; a man of rank 10 "High King").
    props.ints.insert(0x1E, 3);
    let ranked = |props: &PropertyBundle| {
        CharacterSheet::build(&SheetSource {
            player: &p,
            properties: props,
            pwd_bitfield: 0,
            name: "Horan",
            title: None,
            experience: None,
            skill_table: None,
            ui_string: None,
        })
        .name
    };
    assert_eq!(ranked(&props), "Baroness Horan");
    props.ints.insert(0x1E, 10);
    props.ints.insert(0x71, 1);
    assert_eq!(ranked(&props), "High King Horan");
    props.ints.insert(0x1E, 0);
    assert_eq!(ranked(&props), "Horan");
}

// ── Real dats ───────────────────────────────────────────────────────────

mod real_dats {
    use super::*;
    use ac_ui::content::DatContent;
    use ac_ui::font::DEFAULT_FONT_ID;
    use ac_ui::import::{LayoutSource, import};
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

    // CharacterPanelLiveDatTests.
    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_character_window_imports_binds_lists_and_raises() {
        let c = content();
        let mut host = UiHost::new();
        host.root.set_size(1024.0, 768.0);
        let layout = import(
            &mut host.root,
            &c,
            character::LAYOUT_ID,
            c.font(DEFAULT_FONT_ID),
        )
        .expect("character layout");
        let r = host.root.root();
        host.root.add_child(r, layout.root);
        host.root
            .register_window("character", layout.root, None, 0)
            .unwrap();
        let ctl =
            CharacterStatController::bind(&mut host.root, &layout, c.font(DEFAULT_FONT_ID), None);
        let (one, ten) = ctl.raise_buttons();
        assert!(!one.is_empty() && !ten.is_empty(), "raise buttons bound");
        host.attach_controller("character", Box::new(ctl)).unwrap();
        host.with_controller::<CharacterStatController, _>("character", |ctl, ui| {
            ctl.apply(ui, &sample());
        });
        let rows = host
            .with_controller::<CharacterStatController, _>("character", |ctl, _| ctl.rows())
            .unwrap();
        assert_eq!(rows.len(), 9);
        let focus = rows[4].0;
        host.root.fire_event(kind::CLICK, focus, None);
        host.pump();
        let (one, _) = host
            .with_controller::<CharacterStatController, _>("character", |ctl, _| {
                (ctl.raise_buttons().0.to_vec(), ())
            })
            .unwrap();
        host.take_outputs();
        let visible: Vec<ElementId> = one
            .into_iter()
            .filter(|b| host.root.element(*b).is_some_and(|e| e.visible))
            .collect();
        assert!(!visible.is_empty());
        host.root.fire_event(kind::CLICK, visible[0], None);
        host.pump();
        assert_dbg_eq!(
            panel_outputs(&mut host),
            vec![PanelOutput::RaiseAttribute {
                attribute: 5,
                xp: 110
            }]
        );
        // The skills tab rebuilds the list into the retail groups.
        host.with_controller::<CharacterStatController, _>("character", |ctl, ui| {
            ctl.show_tab(ui, CharacterTab::Skills);
        });
        let skills = host
            .with_controller::<CharacterStatController, _>("character", |ctl, _| ctl.rows())
            .unwrap();
        assert_eq!(skills.len(), 12);
        let list = host.draw((1024.0, 768.0), &c);
        assert!(!list.main.is_empty());
        assert!(LayoutSource::layout(&c, character::LAYOUT_ID).is_some());
        assert!(c.take_errors().is_empty());
    }
}
