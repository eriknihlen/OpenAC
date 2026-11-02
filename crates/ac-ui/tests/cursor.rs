//! The mouse cursor's decision: drag, resize and move controls, the
//! click modes, combat, the busy state and the element's authored cursor.
//! Ported from OpenAC `tests/AcDream.App.Tests/UI/CursorFeedbackControllerTests.cs`,
//! `RetailCursorCatalogTests.cs` and `RetailCursorManagerTests.cs`
//! (`PlanApplication`, the key colour). OpenAC's `RuntimeItemInteraction`
//! is the [`CursorFacts`] the window hands over (the click mode, the busy
//! count, the target check); its toolbar fixture is the backpack button's
//! represented object.

mod common;

use std::collections::HashMap;

use ac_core::combat::CombatMode;
use ac_ui::cursor::{
    CURSOR_ENUM_TABLE, CursorFacts, CursorFeedbackController, CursorFeedbackKind, CursorLayer,
    CursorSnapshot, CursorTargetMode, GlobalCursorKind, global_cursor, knock_out_key_color,
    plan_application, window_control_cursor,
};
use ac_ui::element_info::CursorMedia;
use ac_ui::event::{DragAcceptance, MouseButton};
use ac_ui::geometry::ResizeEdges;
use ac_ui::root::UiRoot;
use ac_ui::tree::{Element, ElementId};
use ac_ui::widgets::{Border, Button, ItemSlot, Panel, ResizeGrip};
use common::{at, root};

const PLAYER: u32 = 0x5000_0001;
const TARGET: u32 = 0x5000_0021;

fn media(file: u32, x: i32, y: i32) -> CursorMedia {
    CursorMedia {
        file,
        hotspot_x: x,
        hotspot_y: y,
    }
}

fn control(file: u32) -> Option<CursorMedia> {
    Some(media(file, 16, 16))
}

fn resolve(s: CursorSnapshot) -> ac_ui::cursor::CursorFeedback {
    CursorFeedbackController::new().resolve(&s)
}

fn never(_: u32) -> bool {
    false
}

fn always(_: u32) -> bool {
    true
}

fn facts(
    target_mode: CursorTargetMode,
    world_target: u32,
    compatible: &dyn Fn(u32) -> bool,
) -> CursorFacts<'_> {
    CursorFacts {
        target_mode,
        busy_count: 0,
        combat_mode: CombatMode::NON_COMBAT,
        world_target,
        target_compatible: compatible,
    }
}

fn update(ui: &mut UiRoot) -> ac_ui::cursor::CursorFeedback {
    CursorFeedbackController::new().update(ui, &facts(CursorTargetMode::None, 0, &never))
}

fn with_cursor(ui: &mut UiRoot, parent: ElementId, e: Element, cursor: CursorMedia) -> ElementId {
    let id = ui.add(parent, e, Panel::default());
    let cursors: HashMap<String, CursorMedia> = [(String::new(), cursor)].into_iter().collect();
    ui.element_mut(id).unwrap().set_state_cursors(&cursors);
    id
}

fn window(ui: &mut UiRoot, resizable: bool) -> ElementId {
    let r = ui.root();
    ui.add(
        r,
        Element {
            draggable: true,
            resizable,
            min_width: 40.0,
            min_height: 40.0,
            ..at(50.0, 40.0, 150.0, 100.0)
        },
        Panel::default(),
    )
}

// ── The decision ──────────────────────────────────────────────────────────

#[test]
fn a_drag_wins_over_resize_and_shows_accept_or_reject() {
    let s = |accept| CursorSnapshot {
        dragging: true,
        drag_accept: accept,
        hover_resize_edges: ResizeEdges::RIGHT,
        ..CursorSnapshot::default()
    };
    assert_eq!(
        resolve(s(DragAcceptance::Accept)).kind,
        CursorFeedbackKind::DragAccept
    );
    assert_eq!(
        resolve(s(DragAcceptance::Reject)).kind,
        CursorFeedbackKind::DragReject
    );
}

#[test]
fn each_resize_edge_chooses_its_retail_cursor() {
    use CursorFeedbackKind as K;
    for (edges, kind, file) in [
        (ResizeEdges::LEFT, K::ResizeHorizontal, 0x0600_6128),
        (ResizeEdges::RIGHT, K::ResizeHorizontal, 0x0600_6128),
        (ResizeEdges::TOP, K::ResizeVertical, 0x0600_5E66),
        (ResizeEdges::BOTTOM, K::ResizeVertical, 0x0600_5E66),
        (
            ResizeEdges::LEFT | ResizeEdges::TOP,
            K::ResizeDiagonalNwse,
            0x0600_6126,
        ),
        (
            ResizeEdges::RIGHT | ResizeEdges::BOTTOM,
            K::ResizeDiagonalNwse,
            0x0600_6126,
        ),
        (
            ResizeEdges::LEFT | ResizeEdges::BOTTOM,
            K::ResizeDiagonalNesw,
            0x0600_6127,
        ),
        (
            ResizeEdges::RIGHT | ResizeEdges::TOP,
            K::ResizeDiagonalNesw,
            0x0600_6127,
        ),
    ] {
        let f = resolve(CursorSnapshot {
            hover_resize_edges: edges,
            ..CursorSnapshot::default()
        });
        assert_eq!((f.kind, f.cursor), (kind, control(file)), "{edges:?}");
    }
}

/// A piece of a window that resizes it by its own means.
#[derive(Debug)]
struct OwnResize;

impl ac_ui::tree::Widget for OwnResize {
    fn resize_cursor_edges(&self) -> Option<ResizeEdges> {
        Some(ResizeEdges::BOTTOM)
    }

    // A press on it does not move the window either.
    fn handles_click(&self, _element: &Element) -> bool {
        true
    }
}

#[test]
fn an_element_can_show_a_resize_cursor_and_the_window_stays_as_it_is() {
    let mut ui = root(800.0, 600.0);
    let w = window(&mut ui, false);
    ui.add(w, at(0.0, 0.0, 150.0, 20.0), OwnResize);
    let vertical = control(0x0600_5E66);

    ui.on_mouse_move(60, 50);
    let f = update(&mut ui);
    assert_eq!(
        (f.kind, f.cursor),
        (CursorFeedbackKind::ResizeVertical, vertical)
    );
    assert_eq!(ui.hover_resize_edges(), ResizeEdges::NONE);

    // It keeps showing while the button is down, wherever the pointer goes.
    ui.on_mouse_down(MouseButton::Left, 60, 50, 0);
    ui.on_mouse_move(300, 300);
    let f = update(&mut ui);
    assert_eq!(
        (f.kind, f.cursor),
        (CursorFeedbackKind::ResizeVertical, vertical)
    );
    assert_eq!(ui.active_resize_edges(), ResizeEdges::NONE);
    assert_eq!(
        ui.element(w).map(|e| (e.left, e.top, e.width, e.height)),
        Some((50.0, 40.0, 150.0, 100.0))
    );

    ui.on_mouse_up(MouseButton::Left, 300, 300, 0);
    assert_eq!(update(&mut ui).kind, CursorFeedbackKind::Default);
}

#[test]
fn move_and_text_hover_have_their_own_cursors() {
    let mv = resolve(CursorSnapshot {
        hover_window_move: true,
        ..CursorSnapshot::default()
    });
    assert_eq!(mv.kind, CursorFeedbackKind::WindowMove);
    assert_eq!(mv.cursor, control(0x0600_6119));
    let text = resolve(CursorSnapshot {
        hover_text_edit: true,
        ..CursorSnapshot::default()
    });
    assert_eq!(text.kind, CursorFeedbackKind::Text);
}

#[test]
fn the_target_mode_shows_valid_invalid_and_pending() {
    let s = |target, compatible| CursorSnapshot {
        hover_ui: true,
        hover_target: target,
        hover_target_compatible: compatible,
        target_mode: CursorTargetMode::UseTarget,
        ..CursorSnapshot::default()
    };
    assert_eq!(
        resolve(s(PLAYER, Some(true))).kind,
        CursorFeedbackKind::TargetValid
    );
    assert_eq!(
        resolve(s(0x5000_0BAD, Some(false))).kind,
        CursorFeedbackKind::TargetInvalid
    );
    assert_eq!(resolve(s(0, None)).kind, CursorFeedbackKind::TargetPending);
}

#[test]
fn the_use_and_examine_modes_drive_the_global_cursors() {
    use GlobalCursorKind as G;
    let g = |mode, target| {
        resolve(CursorSnapshot {
            target_mode: mode,
            hover_target: target,
            ..CursorSnapshot::default()
        })
        .global
    };
    assert_eq!(g(CursorTargetMode::Use, 0), G::Use);
    assert_eq!(g(CursorTargetMode::Use, TARGET), G::UseFound);
    assert_eq!(g(CursorTargetMode::Examine, 0), G::Examine);
    assert_eq!(g(CursorTargetMode::Examine, TARGET), G::ExamineFound);
}

#[test]
fn the_global_cursor_follows_the_retail_decision_tree() {
    use CursorTargetMode as M;
    use GlobalCursorKind as G;
    let base = CursorSnapshot::default();
    for (s, expected) in [
        (base, G::Default),
        (
            CursorSnapshot {
                hover_target: TARGET,
                ..base
            },
            G::DefaultFound,
        ),
        (
            CursorSnapshot {
                combat_mode: CombatMode::MELEE,
                ..base
            },
            G::MeleeOrMissile,
        ),
        (
            CursorSnapshot {
                hover_target: TARGET,
                combat_mode: CombatMode::MELEE,
                ..base
            },
            G::MeleeOrMissileFound,
        ),
        (
            CursorSnapshot {
                combat_mode: CombatMode::MISSILE,
                ..base
            },
            G::MeleeOrMissile,
        ),
        (
            CursorSnapshot {
                combat_mode: CombatMode::MAGIC,
                ..base
            },
            G::Magic,
        ),
        (
            CursorSnapshot {
                hover_target: TARGET,
                combat_mode: CombatMode::MAGIC,
                ..base
            },
            G::MagicFound,
        ),
        (
            CursorSnapshot {
                target_mode: M::Examine,
                ..base
            },
            G::Examine,
        ),
        (
            CursorSnapshot {
                hover_target: TARGET,
                target_mode: M::Examine,
                ..base
            },
            G::ExamineFound,
        ),
        (
            CursorSnapshot {
                target_mode: M::Use,
                ..base
            },
            G::Use,
        ),
        (
            CursorSnapshot {
                hover_target: TARGET,
                target_mode: M::Use,
                ..base
            },
            G::UseFound,
        ),
        (
            CursorSnapshot {
                busy_count: 1,
                ..base
            },
            G::Busy,
        ),
        (
            CursorSnapshot {
                hover_target: TARGET,
                busy_count: 1,
                ..base
            },
            G::BusyFound,
        ),
        (
            CursorSnapshot {
                target_mode: M::UseTarget,
                ..base
            },
            G::TargetPending,
        ),
        (
            CursorSnapshot {
                hover_target: TARGET,
                hover_target_compatible: Some(true),
                target_mode: M::UseTarget,
                ..base
            },
            G::TargetValid,
        ),
        (
            CursorSnapshot {
                hover_target: TARGET,
                hover_target_compatible: Some(false),
                target_mode: M::UseTarget,
                ..base
            },
            G::TargetInvalid,
        ),
    ] {
        assert_eq!(resolve(s).global, expected, "{s:?}");
    }
}

#[test]
fn busy_wins_over_target_and_combat() {
    let f = resolve(CursorSnapshot {
        hover_target: TARGET,
        hover_target_compatible: Some(true),
        busy_count: 1,
        target_mode: CursorTargetMode::UseTarget,
        combat_mode: CombatMode::MAGIC,
        ..CursorSnapshot::default()
    });
    assert_eq!(f.global, GlobalCursorKind::BusyFound);
}

// ── From the UI ───────────────────────────────────────────────────────────

#[test]
fn an_active_resize_keeps_the_control_cursor_over_an_authored_element() {
    let mut ui = root(400.0, 300.0);
    let w = window(&mut ui, true);
    with_cursor(
        &mut ui,
        w,
        at(0.0, 0.0, 150.0, 100.0),
        media(0x0600_9999, 3, 4),
    );
    ui.on_mouse_move(199, 90);
    assert_eq!(update(&mut ui).cursor, control(0x0600_6128));
    ui.on_mouse_down(MouseButton::Left, 199, 90, 0);
    ui.on_mouse_move(100, 90);
    let f = update(&mut ui);
    assert_eq!(f.kind, CursorFeedbackKind::ResizeHorizontal);
    assert_eq!(f.cursor, control(0x0600_6128));
}

fn grip_window(border: Border, rect: (f32, f32, f32, f32)) -> (UiRoot, (i32, i32)) {
    let mut ui = root(400.0, 300.0);
    let w = window(&mut ui, true);
    let g = ui.add(
        w,
        at(rect.0, rect.1, rect.2, rect.3),
        ResizeGrip { border, sprite: 0 },
    );
    let (x, y) = ui.screen_position(g);
    (ui, (x as i32 + 2, y as i32 + 2))
}

#[test]
fn a_corner_grip_shows_the_diagonal_cursor() {
    let (mut ui, (x, y)) = grip_window(Border::LowerRight, (145.0, 95.0, 5.0, 5.0));
    ui.on_mouse_move(x, y);
    let f = update(&mut ui);
    assert_eq!(f.kind, CursorFeedbackKind::ResizeDiagonalNwse);
    assert_eq!(f.cursor, control(0x0600_6126));
}

#[test]
fn an_edge_grip_shows_the_straight_cursor() {
    let (mut ui, (x, y)) = grip_window(Border::Bottom, (5.0, 95.0, 140.0, 5.0));
    ui.on_mouse_move(x, y);
    let f = update(&mut ui);
    assert_eq!(f.kind, CursorFeedbackKind::ResizeVertical);
    assert_eq!(f.cursor, control(0x0600_5E66));
}

#[test]
fn an_authored_cursor_wins_until_a_window_move_starts() {
    let mut ui = root(400.0, 300.0);
    let w = window(&mut ui, false);
    let authored = media(0x0600_8888, 7, 8);
    with_cursor(&mut ui, w, at(0.0, 0.0, 150.0, 100.0), authored);
    ui.on_mouse_move(100, 90);
    assert_eq!(update(&mut ui).cursor, Some(authored));
    ui.on_mouse_down(MouseButton::Left, 100, 90, 0);
    ui.on_mouse_move(110, 95);
    assert_eq!(update(&mut ui).cursor, control(0x0600_6119));
    ui.on_mouse_move(300, 200);
    assert_eq!(update(&mut ui).cursor, control(0x0600_6119));
}

#[test]
fn the_ui_lock_hides_a_move_handles_authored_cursor() {
    let mut ui = root(800.0, 600.0);
    let r = ui.root();
    let w = ui.add(r, at(100.0, 500.0, 610.0, 90.0), Panel::default());
    let authored = media(0x0600_7777, 2, 2);
    let handle = with_cursor(&mut ui, w, at(5.0, 0.0, 600.0, 5.0), authored);
    ui.element_mut(handle).unwrap().window_move_handle = true;
    ui.on_mouse_move(110, 502);
    assert_eq!(update(&mut ui).cursor, Some(authored));
    ui.set_ui_locked(true);
    assert_ne!(update(&mut ui).cursor, Some(authored));
}

#[test]
fn the_ui_lock_hides_the_window_control_cursors() {
    let mut ui = root(400.0, 300.0);
    ui.set_ui_locked(true);
    window(&mut ui, true);
    ui.on_mouse_move(199, 90);
    let f = update(&mut ui);
    assert_eq!(f.kind, CursorFeedbackKind::Default);
    assert_eq!(f.cursor, None);
}

fn slot_root() -> UiRoot {
    let mut ui = root(800.0, 600.0);
    let r = ui.root();
    let s = ui.add(r, at(10.0, 10.0, 32.0, 32.0), ItemSlot::default());
    ui.widget_mut::<ItemSlot>(s)
        .unwrap()
        .set_item(TARGET, None, None, None);
    let accept: HashMap<String, CursorMedia> = [(
        "Drag_rollover_accept".to_owned(),
        media(0x0600_9999, 11, 12),
    )]
    .into_iter()
    .collect();
    ui.element_mut(s).unwrap().set_state_cursors(&accept);
    ui.on_mouse_move(20, 20);
    ui
}

#[test]
fn an_item_cell_drives_the_global_target_cursor_without_a_widget_cursor() {
    let mut ui = slot_root();
    let f = CursorFeedbackController::new()
        .update(&mut ui, &facts(CursorTargetMode::UseTarget, 0, &always));
    assert_eq!(f.kind, CursorFeedbackKind::TargetValid);
    assert_eq!(f.global, GlobalCursorKind::TargetValid);
    assert_eq!(f.cursor, None);
}

#[test]
fn an_item_cell_shows_the_found_cursor_in_peace_and_in_combat() {
    let mut ui = slot_root();
    assert_eq!(update(&mut ui).global, GlobalCursorKind::DefaultFound);
    let melee = CursorFacts {
        combat_mode: CombatMode::MELEE,
        ..facts(CursorTargetMode::None, 0, &never)
    };
    let f = CursorFeedbackController::new().update(&mut ui, &melee);
    assert_eq!(f.global, GlobalCursorKind::MeleeOrMissileFound);
}

#[test]
fn the_world_target_shows_through_ui_that_is_no_item() {
    let mut ui = root(800.0, 600.0);
    ui.on_mouse_move(400, 300);
    let world = facts(CursorTargetMode::UseTarget, PLAYER, &always);
    let mut c = CursorFeedbackController::new();
    assert_eq!(
        c.update(&mut ui, &world).kind,
        CursorFeedbackKind::TargetValid
    );
    let r = ui.root();
    ui.add(r, at(390.0, 290.0, 40.0, 40.0), Panel::default());
    assert_eq!(
        c.update(&mut ui, &world).kind,
        CursorFeedbackKind::TargetValid
    );
}

#[test]
fn a_button_standing_for_the_player_shows_the_found_cursor_without_a_world_hit() {
    let mut ui = root(800.0, 600.0);
    let r = ui.root();
    let info = std::sync::Arc::new(ac_ui::element_info::ElementInfo {
        id: 0x1000_01B1,
        kind: 1,
        width: 32.0,
        height: 32.0,
        ..Default::default()
    });
    let b = ui.add(
        r,
        at(10.0, 10.0, 32.0, 32.0),
        Button::new(info, None, Vec::new(), 0.0),
    );
    ui.widget_mut::<Button>(b).unwrap().represented_object = PLAYER;
    ui.on_mouse_move(26, 26);
    let f = update(&mut ui);
    assert_eq!(f.kind, CursorFeedbackKind::Default);
    assert_eq!(f.global, GlobalCursorKind::DefaultFound);
}

#[test]
fn the_widget_cursor_is_the_captured_then_the_hovered_then_none() {
    let mut ui = root(200.0, 100.0);
    let r = ui.root();
    let captured_cursor = media(0x0600_1001, 1, 2);
    let hovered_cursor = media(0x0600_1002, 3, 4);
    with_cursor(&mut ui, r, at(0.0, 0.0, 50.0, 50.0), captured_cursor);
    with_cursor(&mut ui, r, at(60.0, 0.0, 50.0, 50.0), hovered_cursor);
    let mut c = CursorFeedbackController::new();
    let none = facts(CursorTargetMode::None, 0, &never);
    ui.on_mouse_move(10, 10);
    ui.on_mouse_down(MouseButton::Left, 10, 10, 0);
    ui.on_mouse_move(70, 10);
    assert_eq!(c.update(&mut ui, &none).cursor, Some(captured_cursor));
    ui.on_mouse_up(MouseButton::Left, 70, 10, 0);
    assert_eq!(c.update(&mut ui, &none).cursor, Some(hovered_cursor));
    ui.on_mouse_move(150, 75);
    let f = c.update(&mut ui, &none);
    assert_eq!(f.cursor, None);
    assert_eq!(f.global, GlobalCursorKind::Default);
}

// ── The catalogue ─────────────────────────────────────────────────────────

#[test]
fn the_window_control_cursors_are_the_retail_layouts_art() {
    use CursorFeedbackKind as K;
    for (kind, file) in [
        (K::WindowMove, 0x0600_6119),
        (K::ResizeHorizontal, 0x0600_6128),
        (K::ResizeVertical, 0x0600_5E66),
        (K::ResizeDiagonalNwse, 0x0600_6126),
        (K::ResizeDiagonalNesw, 0x0600_6127),
    ] {
        assert_eq!(window_control_cursor(kind), control(file), "{kind:?}");
    }
}

#[test]
fn the_global_cursors_name_their_enumeration_entries() {
    use GlobalCursorKind as G;
    assert_eq!(CURSOR_ENUM_TABLE, 6);
    for (kind, id, hotspot) in [
        (G::Default, 0x01, 0),
        (G::DefaultFound, 0x02, 0),
        (G::MeleeOrMissile, 0x03, 0),
        (G::MeleeOrMissileFound, 0x04, 0),
        (G::Magic, 0x05, 0),
        (G::MagicFound, 0x06, 0),
        (G::Examine, 0x0A, 0),
        (G::ExamineFound, 0x0B, 0),
        (G::Use, 0x0C, 14),
        (G::UseFound, 0x0D, 14),
        (G::Busy, 0x0E, 0),
        (G::BusyFound, 0x0F, 0),
        (G::TargetPending, 0x27, 14),
        (G::TargetValid, 0x28, 14),
        (G::TargetInvalid, 0x29, 14),
    ] {
        let spec = global_cursor(kind);
        assert_eq!(
            (spec.enum_id, spec.hotspot_x, spec.hotspot_y),
            (id, hotspot, hotspot),
            "{kind:?}"
        );
    }
}

// ── Applying it ───────────────────────────────────────────────────────────

const WIDGET_A: CursorMedia = CursorMedia {
    file: 0x0600_1001,
    hotspot_x: 1,
    hotspot_y: 2,
};
const WIDGET_B: CursorMedia = CursorMedia {
    file: 0x0600_1002,
    hotspot_x: 3,
    hotspot_y: 4,
};

#[test]
fn the_first_cursor_sets_the_global_then_the_widget_layer() {
    assert_eq!(
        plan_application(None, GlobalCursorKind::Default, Some(WIDGET_A)),
        [CursorLayer::Global, CursorLayer::Widget]
    );
}

#[test]
fn a_global_change_alone_sets_only_the_global_layer() {
    assert_eq!(
        plan_application(
            Some((GlobalCursorKind::Default, Some(WIDGET_A))),
            GlobalCursorKind::TargetPending,
            Some(WIDGET_A)
        ),
        [CursorLayer::Global]
    );
}

#[test]
fn a_widget_change_alone_sets_only_the_widget_layer() {
    assert_eq!(
        plan_application(
            Some((GlobalCursorKind::TargetPending, Some(WIDGET_A))),
            GlobalCursorKind::TargetPending,
            Some(WIDGET_B)
        ),
        [CursorLayer::Widget]
    );
}

#[test]
fn clearing_the_widget_cursor_brings_back_the_global_one() {
    assert_eq!(
        plan_application(
            Some((GlobalCursorKind::TargetPending, Some(WIDGET_A))),
            GlobalCursorKind::TargetPending,
            None
        ),
        [CursorLayer::Global]
    );
}

#[test]
fn opaque_art_loses_its_pure_white_and_nothing_else() {
    let mut rgba = [
        255, 255, 255, 255, // the key
        200, 160, 40, 255, // gold
        0, 0, 0, 255, // black
        255, 255, 254, 255, // nearly white
    ];
    knock_out_key_color(&mut rgba);
    assert_eq!([rgba[3], rgba[7], rgba[11], rgba[15]], [0, 255, 255, 255]);
}

#[test]
fn art_with_real_alpha_is_left_alone() {
    let mut rgba = [255, 255, 255, 255, 255, 255, 255, 0];
    knock_out_key_color(&mut rgba);
    assert_eq!(rgba[3], 255);
}

mod real_dats {
    use std::path::PathBuf;

    use ac_dat::objects::RenderSurface;
    use ac_ui::cursor::{
        CursorFeedbackKind as K, GlobalCursorKind as G, resolve_global_cursor,
        window_control_cursor,
    };
    use ac_ui::element_info::CursorMedia;

    fn dats() -> ac_dat::DatCollection {
        let dir = std::env::var_os("OPENAC_DAT_DIR")
            .map(PathBuf::from)
            .or_else(ac_dat::default_dat_dir)
            .expect("no dat directory: set OPENAC_DAT_DIR");
        ac_dat::DatCollection::open(dir).unwrap()
    }

    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_window_control_cursors_are_32_pixel_surfaces() {
        let d = dats();
        for kind in [
            K::WindowMove,
            K::ResizeHorizontal,
            K::ResizeVertical,
            K::ResizeDiagonalNwse,
            K::ResizeDiagonalNesw,
        ] {
            let c = window_control_cursor(kind).unwrap();
            let s = d
                .get::<RenderSurface>(c.file)
                .unwrap()
                .expect("the surface");
            assert_eq!((s.width, s.height), (32, 32), "{kind:?}");
        }
    }

    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_global_cursors_resolve_to_their_retail_surfaces() {
        let d = dats();
        let m = |file, h| CursorMedia {
            file,
            hotspot_x: h,
            hotspot_y: h,
        };
        for (kind, cursor) in [
            (G::Default, m(0x0600_4D68, 0)),
            (G::DefaultFound, m(0x0600_4D69, 0)),
            (G::MeleeOrMissile, m(0x0600_4D6A, 0)),
            (G::MeleeOrMissileFound, m(0x0600_4D6B, 0)),
            (G::Magic, m(0x0600_4D6C, 0)),
            (G::MagicFound, m(0x0600_4D6D, 0)),
            (G::Examine, m(0x0600_4D71, 0)),
            (G::ExamineFound, m(0x0600_4D71, 0)),
            (G::Use, m(0x0600_4D72, 14)),
            (G::UseFound, m(0x0600_4D72, 14)),
            (G::Busy, m(0x0600_4D74, 0)),
            (G::BusyFound, m(0x0600_4D75, 0)),
            (G::TargetPending, m(0x0600_4D73, 14)),
            (G::TargetValid, m(0x0600_5E6B, 14)),
            (G::TargetInvalid, m(0x0600_5E6A, 14)),
        ] {
            assert_eq!(resolve_global_cursor(&d, kind), Some(cursor), "{kind:?}");
        }
    }
}
