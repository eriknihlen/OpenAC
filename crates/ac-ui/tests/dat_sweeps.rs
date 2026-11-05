//! Sweeps of the retail dats: the panels' layouts keep their authored
//! roots and states, every string decodes its escapes at the source, the
//! chat strings are the retail set, and the button shapes the factory
//! distinguishes occur where retail authored them.
//! Ported from OpenAC `tests/AcDream.App.Tests/UI/Layout/RetailFixtureConformanceTests.cs`
//! (on the dats instead of OpenAC's committed fixtures),
//! `LayoutConformanceTests.cs`, `DatStringEscapeSweepTests.cs`,
//! `ChatStringsLiveDatTests.cs` and `UiButtonCorpusSweepTests.cs`. All but
//! the reflow case need the retail dat files.

mod common;

use std::path::PathBuf;
use std::sync::Arc;

use ac_dat::DatCollection;
use ac_dat::objects::{DIRECT_STATE_ID, PropertyValue, StringTable};
use ac_ui::content::DatContent;
use ac_ui::element_info::ElementInfo;
use ac_ui::font::DEFAULT_FONT_ID;
use ac_ui::import::{LayoutSource, import, import_infos, import_infos_rooted, import_rooted};
use ac_ui::panels::{appraisal, character, inventory, paperdoll, toolbar};
use ac_ui::strings::{StringResolver, compute_hash, unescape};
use ac_ui::widgets::{Button, Scrollbar, Text};
use common::root;

fn dats() -> DatCollection {
    let dir = std::env::var_os("OPENAC_DAT_DIR")
        .map(PathBuf::from)
        .or_else(ac_dat::default_dat_dir)
        .expect("no dat directory: set OPENAC_DAT_DIR");
    DatCollection::open(dir).unwrap()
}

fn content() -> DatContent {
    DatContent::new(Arc::new(dats())).unwrap()
}

fn all(e: &ElementInfo) -> Vec<&ElementInfo> {
    let mut out = vec![e];
    for c in &e.children {
        out.extend(all(c));
    }
    out
}

fn find(e: &ElementInfo, id: u32) -> Option<&ElementInfo> {
    all(e).into_iter().find(|x| x.id == id)
}

fn infos(c: &DatContent, layout: u32) -> ElementInfo {
    import_infos(c, layout).expect("the layout")
}

// ── The panels' layouts ───────────────────────────────────────────────────

#[test]
#[ignore = "needs the retail dat files"]
fn the_panel_layouts_keep_their_roots_and_states() {
    let c = content();
    for (layout, root_id, kind, w, h, minimum) in [
        (
            toolbar::LAYOUT_ID,
            0x1000_0191,
            0x1000_0007,
            300.0,
            122.0,
            20,
        ),
        (
            inventory::LAYOUT_ID,
            0x1000_01CC,
            0x1000_0023,
            300.0,
            362.0,
            40,
        ),
        (0x2100_0024, 0x1000_01D4, 0x1000_0024, 224.0, 214.0, 20),
        (character::LAYOUT_ID, 0x1000_0227, 8, 300.0, 600.0, 60),
    ] {
        let r = infos(&c, layout);
        assert_eq!((r.id, r.kind), (root_id, kind), "{layout:#x}");
        assert_eq!((r.width, r.height), (w, h), "{layout:#x}");
        let nodes = all(&r);
        assert!(nodes.len() >= minimum, "{layout:#x}: {} nodes", nodes.len());
        assert!(r.state(DIRECT_STATE_ID).is_some(), "{layout:#x}");
        assert!(nodes.iter().any(|e| e.states.len() > 1), "{layout:#x}");
        assert!(
            nodes
                .iter()
                .any(|e| e.states.iter().any(|(_, s)| !s.properties.is_empty())),
            "{layout:#x}"
        );
    }
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_toolbar_has_its_object_buttons_and_their_retail_states() {
    let c = content();
    let r = infos(&c, toolbar::LAYOUT_ID);
    assert!(find(&r, 0x1000_01B1).is_some(), "the inventory button");
    let character = find(&r, 0x1000_0199).expect("the character button");
    for state in [1, 3, 6] {
        assert!(character.state(state).is_some(), "state {state}");
    }
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_inventory_has_its_backpack_and_paperdoll_content() {
    let c = content();
    let r = infos(&c, inventory::LAYOUT_ID);
    for id in [
        inventory::CONTENTS_GRID_ID,
        inventory::CONTAINER_LIST_ID,
        inventory::BURDEN_METER_ID,
        paperdoll::DOLL_VIEWPORT_ID,
        paperdoll::DOLL_DRAG_MASK_ID,
    ] {
        assert!(find(&r, id).is_some(), "{id:#x}");
    }
    let mut ui = root(800.0, 600.0);
    let l = import(&mut ui, &c, inventory::LAYOUT_ID, c.font(DEFAULT_FONT_ID)).unwrap();
    let mask = l.find(paperdoll::DOLL_DRAG_MASK_ID).unwrap();
    assert!(ui.widget::<Button>(mask).is_some());
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_character_window_has_all_three_pages() {
    let c = content();
    let r = infos(&c, character::LAYOUT_ID);
    for id in [
        character::ATTRIBUTES_PAGE_ID,
        character::SKILLS_PAGE_ID,
        character::TITLES_PAGE_ID,
    ] {
        assert!(find(&r, id).is_some(), "{id:#x}");
    }
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_examination_window_has_its_chrome_views_and_scrollbars() {
    let c = content();
    let r = import_infos_rooted(&c, appraisal::LAYOUT_ID, appraisal::ROOT_ID).unwrap();
    assert_eq!(r.id, appraisal::ROOT_ID);
    assert_eq!((r.width, r.height), (310.0, 400.0));
    for id in [
        appraisal::CLOSE_ID,
        appraisal::TITLE_ID,
        appraisal::ITEM_PANEL_ID,
        appraisal::ITEM_TEXT_ID,
        appraisal::ITEM_SCROLLBAR_ID,
        appraisal::CREATURE_PANEL_ID,
        appraisal::SPELL_PANEL_ID,
    ] {
        assert!(find(&r, id).is_some(), "{id:#x}");
    }
    let mut ui = root(800.0, 600.0);
    let l = import_rooted(
        &mut ui,
        &c,
        appraisal::LAYOUT_ID,
        appraisal::ROOT_ID,
        c.font(DEFAULT_FONT_ID),
    )
    .unwrap();
    assert!(
        ui.widget::<Button>(l.find(appraisal::CLOSE_ID).unwrap())
            .is_some()
    );
    assert!(
        ui.widget::<Text>(l.find(appraisal::TITLE_ID).unwrap())
            .is_some()
    );
    assert!(
        ui.widget::<Scrollbar>(l.find(appraisal::ITEM_SCROLLBAR_ID).unwrap())
            .is_some()
    );
}

// ── Strings ───────────────────────────────────────────────────────────────

fn resolver() -> StringResolver {
    let d = Arc::new(dats());
    StringResolver::new(move |id| d.get::<StringTable>(id).ok().flatten())
}

#[test]
#[ignore = "needs the retail dat files"]
fn every_string_resolves_with_its_escapes_decoded() {
    let d = dats();
    let strings = resolver();
    let (mut tables, mut total, mut with_newline_escape) = (0, 0, 0);
    let ids: Vec<u32> = d
        .local
        .entries()
        .map(|e| e.unwrap().id)
        .filter(|id| id >> 24 == 0x23)
        .collect();
    for table_id in ids {
        let Some(table) = d.get::<StringTable>(table_id).unwrap() else {
            continue;
        };
        tables += 1;
        for (string_id, entry) in &table.strings {
            for (token, raw) in entry.strings.iter().enumerate() {
                total += 1;
                if raw.contains("\\n") {
                    with_newline_escape += 1;
                }
                assert_eq!(
                    strings.resolve(table_id, *string_id, i32::try_from(token).unwrap()),
                    Some(unescape(raw)),
                    "{table_id:#x}/{string_id:#x}/{token}"
                );
            }
        }
    }
    assert!(tables > 0 && total > 0);
    assert!(
        with_newline_escape > 0,
        "some retail string carries the \\n escape"
    );
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_talk_focus_strings_are_the_retail_set() {
    let strings = resolver();
    let resolve = |key: &str| strings.resolve(0x2300_0001, compute_hash(key), 0);
    for (key, text) in [
        ("ID_Chat_ChatTargetMenu", "Chat"),
        ("ID_Chat_ChatTargetMenuSelected", "Tell"),
        ("ID_Chat_ChatTargetMenuFellows", "Fell"),
        ("ID_Chat_ChatTargetMenuPatron", "Pat"),
        ("ID_Chat_ChatTargetMenuMonarch", "Mon"),
        ("ID_Chat_ChatTargetMenuVassals", "Vas"),
        ("ID_Chat_ChatTargetMenuAllegiance", "Alg"),
        ("ID_Chat_ChatTargetMenuGeneral", "Gen"),
        ("ID_Chat_ChatTargetMenuTrade", "Trade"),
        ("ID_Chat_ChatTargetMenuLFG", "LFG"),
        ("ID_Chat_ChatTargetMenuRoleplay", "RP"),
        ("ID_Chat_ChatTargetMenuSociety", "Soc"),
        ("ID_Chat_ChatTargetMenuOlthoi", "Olt"),
        ("ID_Chat_TellToAll", "Chat to All"),
        ("ID_Chat_TellToGeneral", "Tell to General Chat"),
        ("ID_Chat_TellToSelected", "Tell to "),
        ("ID_Chat_TellToSelectedNoSelection", "Tell to Selected"),
        ("ID_Chat_SquelchSelected", "Squelch (ignore) "),
        (
            "ID_Chat_SquelchSelectedNoSelection",
            "Squelch (ignore) Selected",
        ),
    ] {
        assert_eq!(resolve(key).as_deref(), Some(text), "{key}");
    }
}

// ── Button shapes ─────────────────────────────────────────────────────────

/// Every button of every layout: (layout, element).
fn buttons(c: &DatContent, wanted: impl Fn(&ElementInfo) -> bool) -> Vec<(u32, u32)> {
    let mut found = Vec::new();
    for layout in 0x2100_0000..=0x2100_00FF_u32 {
        if c.layout(layout).is_none() {
            continue;
        }
        let Some(tree) = import_infos(c, layout) else {
            continue;
        };
        for e in all(&tree) {
            if e.kind == 1 && wanted(e) {
                found.push((layout, e.id));
            }
        }
    }
    found
}

fn has_caption(e: &ElementInfo) -> bool {
    matches!(
        e.effective_property(0x17, None),
        Some(PropertyValue::StringInfo(_))
    )
}

fn stateful_faces(e: &ElementInfo) -> usize {
    e.children
        .iter()
        .filter(|c| {
            !c.state_media.is_empty()
                && c.state_media
                    .keys()
                    .any(|name| e.states.iter().any(|(_, s)| s.name == *name))
        })
        .count()
}

fn is_town_button(id: u32) -> bool {
    matches!(id, 0x1000_040B | 0x1000_040D | 0x1000_040E | 0x1000_040F)
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_label_box_shape_includes_the_town_buttons() {
    let c = content();
    let found = buttons(&c, |e| {
        e.state_media.is_empty()
            && stateful_faces(e) == 1
            && !has_caption(e)
            && e.children.iter().any(|c| c.kind == 12 && has_caption(c))
    });
    assert!(
        found.iter().any(|(_, id)| is_town_button(*id)),
        "{found:x?}"
    );
}

#[test]
#[ignore = "needs the retail dat files"]
fn no_button_authors_both_selection_pairs() {
    let c = content();
    let found = buttons(&c, |e| {
        let custom =
            e.state_media.contains_key("Unselected") && e.state_media.contains_key("Selected");
        let standard =
            e.state_media.contains_key("Normal") || e.state_media.contains_key("Highlight");
        custom && standard
    });
    assert!(found.is_empty(), "{found:x?}");
}

#[test]
#[ignore = "needs the retail dat files"]
fn per_state_label_styles_reach_beyond_character_creation() {
    fn per_state_differs(info: &ElementInfo, id: u32) -> bool {
        let values: Vec<String> = info
            .states
            .iter()
            .filter_map(|(state, _)| info.effective_property(id, Some(*state)))
            .map(|v| format!("{v:?}"))
            .collect();
        values.len() > 1 && values.iter().any(|v| *v != values[0])
    }
    let c = content();
    let found = buttons(&c, |e| {
        let label = if has_caption(e) {
            e
        } else {
            e.children
                .iter()
                .find(|c| c.kind == 12 && has_caption(c))
                .unwrap_or(e)
        };
        per_state_differs(label, 0x1B) || per_state_differs(label, 0x21)
    });
    // The appearance page's hair spinner (OpenAC
    // `CharacterCreationAppearancePage.HairSpinId`) and the town buttons.
    assert!(found.iter().any(|(_, id)| *id == 0x1000_03AF), "{found:x?}");
    assert!(
        found.iter().any(|(_, id)| is_town_button(*id)),
        "{found:x?}"
    );
}

// ── The vitals window ─────────────────────────────────────────────────────
// OpenAC `tests/AcDream.App.Tests/UI/Layout/LayoutConformanceTests.cs`.

fn vitals(c: &DatContent) -> (ac_ui::root::UiRoot, ac_ui::import::ImportedLayout) {
    let mut ui = root(800.0, 600.0);
    let l = import(
        &mut ui,
        c,
        ac_ui::panels::vitals::LAYOUT_ID,
        c.font(DEFAULT_FONT_ID),
    )
    .unwrap();
    (ui, l)
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_vitals_window_has_three_meters_with_their_slices() {
    use ac_ui::panels::vitals::{HEALTH, MANA, STAMINA};
    use ac_ui::widgets::Meter;
    let c = content();
    let (ui, l) = vitals(&c);
    for (id, top, slices) in [
        (
            HEALTH,
            5.0,
            [
                0x0600_747E,
                0x0600_747F,
                0x0600_7480,
                0x0600_7481,
                0x0600_7482,
                0x0600_7483,
            ],
        ),
        (
            STAMINA,
            21.0,
            [
                0x0600_7484,
                0x0600_7485,
                0x0600_7486,
                0x0600_7487,
                0x0600_7488,
                0x0600_7489,
            ],
        ),
        (
            MANA,
            37.0,
            [
                0x0600_748A,
                0x0600_748B,
                0x0600_748C,
                0x0600_748D,
                0x0600_748E,
                0x0600_748F,
            ],
        ),
    ] {
        let e = l.find(id).unwrap();
        let el = ui.element(e).unwrap();
        assert_eq!(
            (el.left, el.top, el.width, el.height),
            (5.0, top, 150.0, 16.0)
        );
        let m = ui.widget::<Meter>(e).unwrap();
        assert_eq!(
            [
                m.back_left,
                m.back_tile,
                m.back_right,
                m.front_left,
                m.front_tile,
                m.front_right
            ],
            slices
        );
    }
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_vitals_chrome_corner_has_its_sprite() {
    let c = content();
    let (ui, l) = vitals(&c);
    let corner = l.find(0x1000_0633).unwrap();
    let e = ui.widget::<ac_ui::widgets::DatElement>(corner).unwrap();
    assert_eq!(e.active_media().file, 0x0600_74C3);
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_vitals_labels_inherit_the_base_layouts_font() {
    let c = content();
    let r = infos(&c, ac_ui::panels::vitals::LAYOUT_ID);
    assert!(all(&r).iter().any(|e| e.font_did == 0x4000_0000));
}

#[test]
#[ignore = "needs the retail dat files"]
fn binding_the_vitals_reuses_the_authored_text_nodes() {
    use ac_ui::panels::vitals::{
        HEALTH, HEALTH_TEXT, MANA, MANA_TEXT, STAMINA, STAMINA_TEXT, VitalsController,
    };
    let c = content();
    let (mut ui, l) = vitals(&c);
    let _bound = VitalsController::bind(&mut ui, &l);
    for (meter, text) in [
        (HEALTH, HEALTH_TEXT),
        (STAMINA, STAMINA_TEXT),
        (MANA, MANA_TEXT),
    ] {
        let (meter, text) = (l.find(meter).unwrap(), l.find(text).unwrap());
        assert_eq!(ui.parent(text), Some(meter));
        assert_eq!(ui.children(meter).iter().filter(|c| **c == text).count(), 1);
        assert!(!ui.widget::<Text>(text).unwrap().selectable());
    }
}

#[test]
fn a_wider_vitals_window_reflows_its_frame_and_meters() {
    // (piece, design x, design width, left mode, right mode, x, width at
    // 200 px), the window authored 160 px wide.
    for (piece, x, w, l, r, x2, w2) in [
        ("top-left corner", 0.0, 5.0, 1, 2, 0.0, 5.0),
        ("top edge", 5.0, 150.0, 1, 1, 5.0, 190.0),
        ("top-right corner", 155.0, 5.0, 2, 1, 195.0, 5.0),
        ("meter", 5.0, 150.0, 1, 1, 5.0, 190.0),
    ] {
        let mut ui = root(800.0, 600.0);
        let top = ui.root();
        let parent = ui.add(
            top,
            ac_ui::tree::Element::at(0.0, 0.0, 160.0, 58.0),
            ac_ui::widgets::Panel::default(),
        );
        let anchors = ac_ui::root::UiRoot::anchors_for(l, 1, r, 2);
        let child = ui.add(
            parent,
            ac_ui::tree::Element::at(x, 0.0, w, 5.0).with_anchors(anchors),
            ac_ui::widgets::Panel::default(),
        );
        ui.apply_anchor(child, 160.0, 58.0);
        ui.apply_anchor(child, 200.0, 58.0);
        let e = ui.element(child).unwrap();
        assert!((e.left - x2).abs() < 0.5, "{piece}: x {}", e.left);
        assert!((e.width - w2).abs() < 0.5, "{piece}: width {}", e.width);
    }
}

// ── Translucent window backgrounds ────────────────────────────────────────

#[test]
#[ignore = "needs the retail dat files"]
fn the_examination_and_inventory_backgrounds_are_authored_translucent() {
    // The examination window's page backgrounds and the inventory's backdrop
    // are Alphablend media (draw mode 3)
    // over art that carries its own alpha: the examination tile is 48 x 48
    // at alpha 175 everywhere, so a window under it shows through at about
    // a third, as in retail.
    use ac_dat::objects::{Palette, RenderSurface};
    let c = content();
    let r = import_infos_rooted(&c, appraisal::LAYOUT_ID, appraisal::ROOT_ID).unwrap();
    let panel = find(&r, appraisal::ITEM_PANEL_ID).unwrap();
    let media = panel.state_media[""];
    assert_eq!((media.file, media.draw_mode), (0x0600_4CC2, 3));
    let inv = infos(&c, inventory::LAYOUT_ID);
    let backdrop = find(&inv, 0x1000_01D0).unwrap();
    assert_eq!(backdrop.state_media[""].draw_mode, 3);
    let d = dats();
    let rs = d.get::<RenderSurface>(0x0600_4CC2).unwrap().unwrap();
    let palette = (rs.default_palette_id != 0)
        .then(|| d.get::<Palette>(rs.default_palette_id).unwrap())
        .flatten();
    let t = ac_dat::texture::decode(
        &rs,
        palette.as_ref(),
        ac_dat::texture::DecodeOptions::default(),
    )
    .unwrap();
    assert_eq!((t.width, t.height), (48, 48));
    assert!(t.rgba.chunks(4).all(|p| p[3] == 175));
}

// ── Authored text ─────────────────────────────────────────────────────────

#[test]
#[ignore = "needs the retail dat files"]
fn authored_text_too_tall_for_its_box_shows_its_first_line() {
    // A text element lays its own string out from the first line: one that
    // wraps past its box (the squelch page's 100-pixel "Character Name:"
    // label) shows its start, not its end.
    use ac_ui::draw::{DrawList, RenderContext};
    use ac_ui::widgets::TextContent;
    let c = content();
    let layouts: Vec<u32> = c
        .dats()
        .local
        .entries()
        .filter_map(Result::ok)
        .map(|e| e.id)
        .filter(|id| id >> 24 == 0x21)
        .collect();
    let mut overflowing = Vec::new();
    for layout in layouts {
        let mut ui = root(1600.0, 1200.0);
        let Some(l) = import(&mut ui, &c, layout, c.font(DEFAULT_FONT_ID)) else {
            continue;
        };
        let mut stack = vec![l.root];
        while let Some(e) = stack.pop() {
            stack.extend(ui.children(e));
            let Some(t) = ui.widget::<Text>(e) else {
                continue;
            };
            if t.one_line
                || !matches!(
                    t.content(),
                    TextContent::Single(_) | TextContent::Wrapped(_)
                )
            {
                continue;
            }
            ui.set_visible(e, true);
            let mut list = DrawList::default();
            let mut rc = RenderContext::new(&mut list, (1600.0, 1200.0), &c);
            ui.draw_tree(e, &mut rc);
            let dat = ui.element(e).unwrap().dat_element_id;
            let t = ui.widget::<Text>(e).unwrap();
            if t.scroll.has_overflow() {
                overflowing.push((layout, dat));
                assert_eq!(
                    t.scroll.scroll_y(),
                    0,
                    "{layout:#X} {dat:#X} shows {:?} from its end",
                    t.shown_lines().iter().map(|l| &l.text).collect::<Vec<_>>()
                );
            }
        }
    }
    assert!(overflowing.contains(&(0x2100_0060, 0x1000_053F)));
}
