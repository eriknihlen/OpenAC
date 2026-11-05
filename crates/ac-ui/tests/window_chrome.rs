//! The chat windows' opacity and the lock presentation.
//! Ported from OpenAC `tests/AcDream.App.Tests/UI/RetailWindowOpacityControllerTests.cs`
//! and `RetailWindowLockPresentationControllerTests.cs`. OpenAC's controllers
//! hear window events; these follow the root each frame (`apply`,
//! `follow`), so the tests call them where an event would have fired.

use ac_ui::draw::{DrawCmd, TextureRef};
use ac_ui::root::UiRoot;
use ac_ui::tree::{Element, ElementId};
use ac_ui::widgets::chrome::{
    BOTTOM_EDGE, CENTER_FILL, CORNER_BL, CORNER_BR, CORNER_TL, CORNER_TR, LEFT_EDGE, RIGHT_EDGE,
    TOP_EDGE,
};
use ac_ui::widgets::frame::{GRIP_BOTTOM, GRIP_CORNER, GRIP_LEFT, GRIP_RIGHT, GRIP_TOP};
use ac_ui::widgets::{NineSliceFrame, Panel};
use ac_ui::window_chrome::{LockPresentation, WindowOpacity};

fn root() -> UiRoot {
    let mut ui = UiRoot::new();
    ui.set_size(800.0, 600.0);
    ui
}

fn register(ui: &mut UiRoot, name: &str) -> (ElementId, ElementId) {
    let r = ui.root();
    let frame = ui.add(r, Element::at(0.0, 0.0, 100.0, 100.0), Panel::default());
    let mut c = Element::at(0.0, 0.0, 10.0, 10.0);
    c.accepts_focus = true;
    let child = ui.add(frame, c, Panel::default());
    ui.register_window(name, frame, None, 0).unwrap();
    (frame, child)
}

fn opacity(ui: &UiRoot, frame: ElementId) -> f32 {
    ui.element(frame).unwrap().opacity
}

#[test]
fn already_registered_chat_windows_take_the_default_opacity() {
    let mut ui = root();
    let (chat, _) = register(&mut ui, "chat");
    let o = WindowOpacity::new(0.5, 1.0);
    o.apply(&mut ui);
    assert_eq!(opacity(&ui, chat), 0.5);
    assert_eq!((o.default_opacity(), o.active_opacity()), (0.5, 1.0));
}

#[test]
fn a_window_registered_later_takes_the_live_opacity() {
    let mut ui = root();
    let o = WindowOpacity::new(0.3, 0.9);
    o.apply(&mut ui);
    let (w, _) = register(&mut ui, "chat-window-1");
    o.apply(&mut ui);
    assert_eq!(opacity(&ui, w), 0.3);
}

#[test]
fn focus_inside_a_window_switches_it_to_the_active_opacity() {
    let mut ui = root();
    let (chat, child) = register(&mut ui, "chat");
    let o = WindowOpacity::new(0.5, 1.0);
    o.apply(&mut ui);
    assert_eq!(opacity(&ui, chat), 0.5);
    ui.set_keyboard_focus(Some(child));
    o.apply(&mut ui);
    assert_eq!(opacity(&ui, chat), 1.0);
    ui.set_keyboard_focus(None);
    o.apply(&mut ui);
    assert_eq!(opacity(&ui, chat), 0.5);
}

#[test]
fn the_fade_applies_only_to_chat_windows() {
    let mut ui = root();
    let (vitals, _) = register(&mut ui, "vitals");
    let (toolbar, toolbar_child) = register(&mut ui, "toolbar");
    let (chat, _) = register(&mut ui, "chat");
    let (floaty, floaty_child) = register(&mut ui, "chat-window-1");
    let mut o = WindowOpacity::new(0.4, 1.0);
    o.apply(&mut ui);
    assert_eq!(
        [vitals, toolbar, chat, floaty].map(|w| opacity(&ui, w)),
        [1.0, 1.0, 0.4, 0.4]
    );
    ui.set_keyboard_focus(Some(toolbar_child));
    o.apply(&mut ui);
    assert_eq!(
        [vitals, toolbar, chat, floaty].map(|w| opacity(&ui, w)),
        [1.0, 1.0, 0.4, 0.4]
    );
    ui.set_keyboard_focus(Some(floaty_child));
    o.set_active_opacity(0.6);
    o.apply(&mut ui);
    assert_eq!(
        [vitals, toolbar, chat, floaty].map(|w| opacity(&ui, w)),
        [1.0, 1.0, 0.4, 0.6]
    );
}

#[test]
fn a_default_above_the_active_drags_the_active_up() {
    let mut ui = root();
    let (unfocused, _) = register(&mut ui, "chat");
    let (focused, child) = register(&mut ui, "chat-window-1");
    let mut o = WindowOpacity::new(0.3, 0.5);
    ui.set_keyboard_focus(Some(child));
    o.apply(&mut ui);
    assert_eq!((opacity(&ui, unfocused), opacity(&ui, focused)), (0.3, 0.5));
    o.set_default_opacity(0.9);
    o.apply(&mut ui);
    assert_eq!((o.default_opacity(), o.active_opacity()), (0.9, 0.9));
    assert_eq!((opacity(&ui, unfocused), opacity(&ui, focused)), (0.9, 0.9));
}

#[test]
fn an_active_below_the_default_drags_the_default_down() {
    let mut ui = root();
    let (unfocused, _) = register(&mut ui, "chat");
    let (focused, child) = register(&mut ui, "chat-window-1");
    let mut o = WindowOpacity::new(0.5, 0.7);
    ui.set_keyboard_focus(Some(child));
    o.set_active_opacity(0.1);
    o.apply(&mut ui);
    assert_eq!((o.default_opacity(), o.active_opacity()), (0.1, 0.1));
    assert_eq!((opacity(&ui, unfocused), opacity(&ui, focused)), (0.1, 0.1));
}

#[test]
fn set_opacity_applies_both_default_first() {
    let mut ui = root();
    let (chat, _) = register(&mut ui, "chat");
    let mut o = WindowOpacity::new(0.5, 1.0);
    o.set_opacity(0.2, 0.6);
    o.apply(&mut ui);
    assert_eq!((o.default_opacity(), o.active_opacity()), (0.2, 0.6));
    assert_eq!(opacity(&ui, chat), 0.2);
}

#[test]
fn the_seed_keeps_the_active_at_least_the_default() {
    let o = WindowOpacity::new(0.8, 0.2);
    assert!(o.active_opacity() >= o.default_opacity());
    assert_eq!((o.default_opacity(), o.active_opacity()), (0.2, 0.2));
}

#[test]
fn a_disposed_rule_follows_and_changes_nothing() {
    let mut ui = root();
    let (chat, child) = register(&mut ui, "chat");
    let mut o = WindowOpacity::new(0.5, 1.0);
    o.apply(&mut ui);
    o.dispose();
    ui.set_keyboard_focus(Some(child));
    o.apply(&mut ui);
    assert_eq!(opacity(&ui, chat), 0.5);
    o.set_default_opacity(0.9);
    o.set_active_opacity(0.9);
    o.set_opacity(0.2, 0.3);
    assert_eq!((o.default_opacity(), o.active_opacity()), (0.5, 1.0));
}

// ── The lock presentation ───────────────────────────────────────────────

fn nine_slice(ui: &mut UiRoot, name: &str, center: bool) -> ElementId {
    let r = ui.root();
    let frame = ui.add(
        r,
        Element::at(0.0, 0.0, 200.0, 100.0),
        NineSliceFrame {
            draw_center_fill: center,
            ..NineSliceFrame::default()
        },
    );
    ui.register_window(name, frame, None, 0).unwrap();
    frame
}

fn frame(ui: &UiRoot, id: ElementId) -> &NineSliceFrame {
    ui.widget::<NineSliceFrame>(id).unwrap()
}

#[test]
fn nine_slice_windows_follow_the_lock_now_and_later() {
    let mut ui = root();
    let existing = nine_slice(&mut ui, "existing", true);
    let mut p = LockPresentation::new();
    p.follow(&mut ui);
    assert!(frame(&ui, existing).draw_resize_affordances);
    ui.set_ui_locked(true);
    p.follow(&mut ui);
    assert!(!frame(&ui, existing).draw_resize_affordances);
    assert!(frame(&ui, existing).draw_center_fill);

    let late = nine_slice(&mut ui, "future", false);
    p.follow(&mut ui);
    assert!(!frame(&ui, late).draw_resize_affordances);
    assert!(!frame(&ui, late).draw_center_fill);

    ui.set_ui_locked(false);
    p.follow(&mut ui);
    assert!(frame(&ui, existing).draw_resize_affordances);
    assert!(frame(&ui, late).draw_resize_affordances);
    assert!(frame(&ui, existing).draw_center_fill);
    assert!(!frame(&ui, late).draw_center_fill);
}

#[derive(Debug)]
struct Sizes;
impl ac_ui::draw::SpriteSizes for Sizes {
    fn sprite_size(&self, _file: u32) -> Option<(u32, u32)> {
        Some((5, 5))
    }
}

fn sprite_counts(ui: &mut UiRoot) -> std::collections::HashMap<u32, usize> {
    let list = ui.draw((800.0, 600.0), &Sizes);
    let mut counts = std::collections::HashMap::new();
    for cmd in &list.main {
        if let DrawCmd::Sprite {
            texture: TextureRef::Surface(id),
            ..
        } = cmd
        {
            *counts.entry(*id).or_insert(0) += 1;
        }
    }
    counts
}

#[test]
fn a_locked_frame_draws_its_centre_and_bevel_but_no_grips() {
    let mut ui = root();
    nine_slice(&mut ui, "frame", true);
    let mut p = LockPresentation::new();
    p.follow(&mut ui);
    let grips = [GRIP_TOP, GRIP_BOTTOM, GRIP_LEFT, GRIP_RIGHT, GRIP_CORNER];
    let unlocked = sprite_counts(&mut ui);
    assert!(grips.iter().any(|g| unlocked.contains_key(g)));
    ui.set_ui_locked(true);
    p.follow(&mut ui);
    let locked = sprite_counts(&mut ui);
    assert!(grips.iter().all(|g| !locked.contains_key(g)), "{locked:?}");
    for piece in [
        CENTER_FILL,
        TOP_EDGE,
        BOTTOM_EDGE,
        LEFT_EDGE,
        RIGHT_EDGE,
        CORNER_TL,
        CORNER_TR,
        CORNER_BL,
        CORNER_BR,
    ] {
        assert!(locked.contains_key(&piece), "{piece:#x}");
    }
}

fn piece(ui: &mut UiRoot, parent: ElementId, id: u32, visible: bool) -> ElementId {
    let mut e = Element::default().with_visible(visible);
    e.dat_element_id = id;
    ui.add(parent, e, Panel::default())
}

fn plain_window(ui: &mut UiRoot, name: &str) -> ElementId {
    let r = ui.root();
    let f = ui.add(r, Element::at(0.0, 0.0, 200.0, 100.0), Panel::default());
    ui.register_window(name, f, None, 0).unwrap();
    f
}

fn shown(ui: &UiRoot, id: ElementId) -> bool {
    ui.element(id).unwrap().visible()
}

#[test]
fn missing_pair_members_and_unrelated_decoration_transition_safely() {
    let mut ui = root();
    let f = plain_window(&mut ui, "partial");
    let locked_only = piece(&mut ui, f, 0x1000_0633, false);
    let live_only = piece(&mut ui, f, 0x1000_063C, true);
    let unrelated = piece(&mut ui, f, 0x1000_0632, true);
    let hidden_live = piece(&mut ui, f, 0x1000_063D, false);
    let mut p = LockPresentation::new();
    p.follow(&mut ui);
    ui.set_ui_locked(true);
    p.follow(&mut ui);
    assert!(shown(&ui, locked_only));
    assert!(!shown(&ui, live_only));
    assert!(shown(&ui, unrelated));
    assert!(!shown(&ui, hidden_live));
    ui.set_ui_locked(false);
    p.follow(&mut ui);
    assert!(!shown(&ui, locked_only));
    assert!(shown(&ui, live_only));
    assert!(shown(&ui, unrelated));
    assert!(!shown(&ui, hidden_live));
}

#[test]
fn every_authored_chrome_block_swaps_all_eight_members() {
    for (locked_start, live_start) in ac_ui::window_chrome::AUTHORED_CHROME_BLOCKS {
        let mut ui = root();
        let f = plain_window(&mut ui, "authored");
        let locked: Vec<ElementId> = (0..8)
            .map(|i| piece(&mut ui, f, locked_start + i, true))
            .collect();
        let live: Vec<ElementId> = (0..8)
            .map(|i| piece(&mut ui, f, live_start + i, true))
            .collect();
        let mut p = LockPresentation::new();
        p.follow(&mut ui);
        assert!(locked.iter().all(|&e| !shown(&ui, e)));
        assert!(live.iter().all(|&e| shown(&ui, e)));
        ui.set_ui_locked(true);
        p.follow(&mut ui);
        assert!(locked.iter().all(|&e| shown(&ui, e)));
        assert!(live.iter().all(|&e| !shown(&ui, e)));
        ui.set_ui_locked(false);
        p.follow(&mut ui);
        assert!(locked.iter().all(|&e| !shown(&ui, e)));
        assert!(live.iter().all(|&e| shown(&ui, e)));
    }
}

#[test]
fn the_smart_box_live_chrome_hides_without_touching_other_decoration() {
    let mut ui = root();
    let f = plain_window(&mut ui, "smartbox");
    let chrome: Vec<ElementId> = (0..8)
        .map(|i| piece(&mut ui, f, 0x1000_06CA + i, true))
        .collect();
    let type2 = piece(&mut ui, f, 0x1000_0529, true);
    let type3 = piece(&mut ui, f, 0x1000_06D2, true);
    let mut p = LockPresentation::new();
    p.follow(&mut ui);
    ui.set_ui_locked(true);
    p.follow(&mut ui);
    assert!(chrome.iter().all(|&e| !shown(&ui, e)));
    assert!(shown(&ui, type2) && shown(&ui, type3));
    ui.set_ui_locked(false);
    p.follow(&mut ui);
    assert!(chrome.iter().all(|&e| shown(&ui, e)));
    assert!(shown(&ui, type2) && shown(&ui, type3));
}

mod real_dats {
    use super::*;
    use ac_ui::content::DatContent;
    use ac_ui::font::DEFAULT_FONT_ID;
    use ac_ui::import::{LayoutSource, import};
    use ac_ui::panels::chat;
    use std::path::PathBuf;
    use std::sync::Arc;

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

    const LOCKED: [u32; 8] = [
        0x1000_0693,
        0x1000_0694,
        0x1000_0695,
        0x1000_0696,
        0x1000_0697,
        0x1000_0698,
        0x1000_0699,
        0x1000_069A,
    ];
    const LIVE: [u32; 8] = [
        0x1000_069B,
        0x1000_069C,
        0x1000_069D,
        0x1000_069E,
        0x1000_069F,
        0x1000_06A0,
        0x1000_06A1,
        0x1000_06A2,
    ];

    fn assert_chrome(ui: &UiRoot, layout: &ac_ui::import::ImportedLayout, live_visible: bool) {
        for id in LIVE {
            let e = layout.find(id).expect("live chrome");
            assert_eq!(shown(ui, e), live_visible, "{id:#x}");
        }
        for id in LOCKED {
            let e = layout.find(id).expect("locked chrome");
            assert_eq!(shown(ui, e), !live_visible, "{id:#x}");
        }
    }

    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_chat_windows_authored_chrome_swaps_and_restores() {
        let c = content();
        for locked_at_registration in [false, true] {
            let mut ui = root();
            ui.set_ui_locked(locked_at_registration);
            let mut p = LockPresentation::new();
            p.follow(&mut ui);
            let layout = import(&mut ui, &c, chat::LAYOUT_ID, c.font(DEFAULT_FONT_ID)).unwrap();
            let r = ui.root();
            ui.add_child(r, layout.root);
            ui.register_window("chat", layout.root, None, 0).unwrap();
            p.follow(&mut ui);
            assert_chrome(&ui, &layout, !locked_at_registration);
            if locked_at_registration {
                continue;
            }
            for (lock, live) in [(true, false), (true, false), (false, true), (false, true)] {
                ui.set_ui_locked(lock);
                p.follow(&mut ui);
                assert_chrome(&ui, &layout, live);
            }
        }
    }
}

mod scrollbar_skin {
    //! Ported from OpenAC `tests/AcDream.App.Tests/UI/Layout/ScrollbarSkinLiveDatTests.cs`
    //! (the art ids are OpenAC's `RetailScrollbarChrome`).
    use ac_ui::content::DatContent;
    use ac_ui::element_info::ElementInfo;
    use ac_ui::import::import_infos;
    use std::path::PathBuf;
    use std::sync::Arc;

    fn flatten(e: &ElementInfo, out: &mut Vec<ElementInfo>) {
        out.push(e.clone());
        for c in &e.children {
            flatten(c, out);
        }
    }

    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_vertical_base_skin_makes_the_up_arrow_the_increment_with_three_state_art() {
        let dir = std::env::var_os("OPENAC_DAT_DIR")
            .map(PathBuf::from)
            .or_else(ac_dat::default_dat_dir)
            .expect("no dat directory: set OPENAC_DAT_DIR");
        let c = DatContent::new(Arc::new(ac_dat::DatCollection::open(dir).unwrap())).unwrap();
        let tree = import_infos(&c, 0x2100_003E).unwrap();
        let mut all = Vec::new();
        flatten(&tree, &mut all);
        let bars: Vec<&ElementInfo> = all.iter().filter(|e| e.id == 0x1000_0455).collect();
        assert_eq!(bars.len(), 1);
        let bar = bars[0];
        let prop = |id| match bar.effective_property(id, None) {
            Some(
                ac_dat::objects::PropertyValue::Enum(v) | ac_dat::objects::PropertyValue::DataId(v),
            ) => *v,
            Some(ac_dat::objects::PropertyValue::Integer(v)) => *v as u32,
            other => panic!("{id:#x}: {other:?}"),
        };
        assert_eq!(prop(0x77), 0x1000_0072, "increment = the up arrow");
        assert_eq!(prop(0x78), 0x1000_0071, "decrement = the down arrow");
        let child = |parent: &ElementInfo, id: u32| {
            parent
                .children
                .iter()
                .find(|c| c.id == id)
                .cloned()
                .unwrap()
        };
        let media = |e: &ElementInfo, state: &str| e.state_media[state].file;
        let up = child(bar, 0x1000_0072);
        assert_eq!(
            [
                media(&up, "Normal"),
                media(&up, "Normal_rollover"),
                media(&up, "Normal_pressed")
            ],
            [0x0600_4C6C, 0x0600_4C6D, 0x0600_4C6E]
        );
        let down = child(bar, 0x1000_0071);
        assert_eq!(
            [
                media(&down, "Normal"),
                media(&down, "Normal_rollover"),
                media(&down, "Normal_pressed")
            ],
            [0x0600_4C69, 0x0600_4C6A, 0x0600_4C6B]
        );
        let thumb = child(bar, 1);
        let mid = child(&thumb, 0x1000_0365);
        assert_eq!(
            [
                media(&mid, "Normal"),
                media(&mid, "Normal_rollover"),
                media(&mid, "Normal_pressed")
            ],
            [0x0600_4C63, 0x0600_4C64, 0x0600_4C65]
        );
    }
}

// ── Two-stop frames ───────────────────────────────────────────────────────
// OpenAC `tests/AcDream.App.Tests/UI/UiCollapsibleFrameTests.cs`.

fn two_stop(ui: &mut UiRoot, height: f32, configured: bool) -> (ElementId, [ElementId; 2]) {
    let r = ui.root();
    let rows = [
        ui.insert(Element::at(0.0, 0.0, 10.0, 10.0), Panel::default()),
        ui.insert(Element::at(0.0, 0.0, 10.0, 10.0), Panel::default()),
    ];
    let frame = NineSliceFrame {
        collapsed_height: if configured { 96.0 } else { 0.0 },
        expanded_height: if configured { 128.0 } else { 0.0 },
        second_row: if configured {
            rows.to_vec()
        } else {
            Vec::new()
        },
        ..NineSliceFrame::default()
    };
    let f = ui.add(r, Element::at(0.0, 0.0, 300.0, height), frame);
    for row in rows {
        ui.add_child(f, row);
    }
    (f, rows)
}

#[test]
fn a_frame_below_the_midpoint_snaps_collapsed_and_hides_the_second_row() {
    let mut ui = root();
    let (f, rows) = two_stop(&mut ui, 100.0, true);
    ui.tick(0.016, 0);
    assert_eq!(ui.element(f).unwrap().height, 96.0);
    assert!(rows.iter().all(|r| !ui.element(*r).unwrap().visible()));
    let frame = ui.widget::<NineSliceFrame>(f).unwrap();
    assert!(!frame.is_expanded(96.0));
}

#[test]
fn a_frame_above_the_midpoint_snaps_expanded_and_shows_the_second_row() {
    let mut ui = root();
    let (f, rows) = two_stop(&mut ui, 120.0, true);
    ui.tick(0.016, 0);
    assert_eq!(ui.element(f).unwrap().height, 128.0);
    assert!(rows.iter().all(|r| ui.element(*r).unwrap().visible()));
    assert!(ui.widget::<NineSliceFrame>(f).unwrap().is_expanded(128.0));
}

#[test]
fn a_frame_without_stops_keeps_its_height() {
    let mut ui = root();
    let (f, _) = two_stop(&mut ui, 50.0, false);
    ui.tick(0.016, 0);
    assert_eq!(ui.element(f).unwrap().height, 50.0);
}
