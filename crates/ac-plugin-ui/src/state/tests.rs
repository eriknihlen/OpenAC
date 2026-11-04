//! Ported from OpenAC `tests/AcDream.App.Tests/Plugins/BufferedUiRegistryTests.cs`,
//! `BufferedUiRegistryCanvasTests.cs`, `tests/AcDream.Core.Tests/Plugins/ScopedUiRegistryPanelControlTests.cs`,
//! `ScopedUiRegistryClientWindowTests.cs`, `tests/AcDream.Runtime.Tests/Plugins/WorldLabelAutomationTests.cs`
//! and `tests/AcDream.Plugin.Tests/PluginCanvasContractTests.cs`. OpenAC's
//! answers before a window mounts the panel become the answers of the
//! session's state, the same with or without a window.

use super::*;
use crate::model::{
    ActionValue, CanvasAnchor, CanvasPlace, Color8, PaintRect, PointerEvent, PointerKind,
    canvas_origin,
};
use crate::value::BindingKind;
use crate::view::PluginUiView;

fn tank() -> UiOwner {
    UiOwner::new("edwards.tank", "Tank")
}

fn other() -> UiOwner {
    UiOwner::new("another.plugin", "Other")
}

fn label(id: u32) -> WorldLabel {
    WorldLabel {
        object_id: id,
        text: "name".into(),
        color: [1.0; 4],
        height_offset: 0.0,
        line: 0,
        max_range: 60.0,
        outline: true,
    }
}

fn status_view(ui: &mut PluginUi) -> u32 {
    ui.register_panel(
        &tank(),
        PanelDescriptor::new("meta", "Status View"),
        "<panel><button name=\"Action\" text=\"Old\"/><label name=\"Line\" text=\"x\"/><group name=\"Box\"/></panel>",
        BindingSchema::new("Binding"),
    )
    .unwrap()
}

#[test]
fn scoped_views_expose_only_their_own_named_controls() {
    // BufferedUiRegistryTests.ScopedViewsExposeOnlyTheirOwnNamedControls
    let mut ui = PluginUi::new();
    let panel = status_view(&mut ui);
    assert!(ui.view_exists("edwards.tank", "Status View"));
    assert!(ui.view_exists("edwards.tank", "meta"));
    assert!(ui.is_view_visible("edwards.tank", "meta"));
    assert!(ui.control_exists("edwards.tank", "Status View", "Action"));
    assert!(ui.set_control_label("edwards.tank", "Status View", "Action", "New"));
    assert!(ui.set_control_visible("edwards.tank", "Status View", "Action", false));
    let view = ui.panels().next().unwrap();
    assert_eq!(
        view.label_overrides.get(&0).map(String::as_str),
        Some("New")
    );
    assert!(!view.node_visible(0));
    assert!(!ui.view_exists("another.plugin", "Status View"));
    assert!(!ui.control_exists("edwards.tank", "Status View", "Nobody"));
    // A group carries no text.
    assert!(!ui.set_control_label("edwards.tank", "meta", "Box", "x"));
    assert!(ui.set_control_label("edwards.tank", "meta", "Line", "y"));
    assert!(ui.remove_panel("edwards.tank", panel));
    assert!(!ui.view_exists("edwards.tank", "meta"));
    assert!(!ui.remove_panel("edwards.tank", panel));
}

#[test]
fn a_panel_carries_its_owner_and_stable_window_name() {
    // BufferedUiRegistryTests.FirstClassPanelCarriesManifestOwnerAndStableWindowIdentity,
    // InlinePanelContentHasAnIndependentlyRemovableLifetime
    let mut ui = PluginUi::new();
    let descriptor = PanelDescriptor {
        icon_text: Some("MT".into()),
        start_visible: false,
        ..PanelDescriptor::new("main", "Tank")
    };
    let id = ui
        .register_panel(
            &tank(),
            descriptor,
            "<panel w=\"100\" h=\"50\" />",
            BindingSchema::new("B"),
        )
        .unwrap();
    let view = ui.panels().next().unwrap();
    assert_eq!(view.window_name(), "plugin:edwards.tank:main");
    assert!(!view.requested_visible);
    assert!(!ui.is_view_visible("edwards.tank", "main"));
    assert!(ui.remove_panel("edwards.tank", id));
    assert_eq!(ui.panels().count(), 0);
}

#[test]
fn registration_refuses_blank_names_duplicates_and_bad_markup() {
    let mut ui = PluginUi::new();
    let schema = || BindingSchema::new("B");
    assert!(
        ui.register_panel(
            &tank(),
            PanelDescriptor::new(" ", "T"),
            "<panel/>",
            schema()
        )
        .is_err()
    );
    assert!(
        ui.register_panel(&tank(), PanelDescriptor::new("a", ""), "<panel/>", schema())
            .is_err()
    );
    assert!(
        ui.register_panel(&tank(), PanelDescriptor::new("a", "T"), "  ", schema())
            .is_err()
    );
    assert_eq!(
        ui.register_panel(
            &tank(),
            PanelDescriptor::new("a", "T"),
            "<panel><nope/></panel>",
            schema()
        ),
        Err("unknown element <nope>".to_owned())
    );
    ui.register_panel(
        &tank(),
        PanelDescriptor::new("a", "T"),
        "<panel/>",
        schema(),
    )
    .unwrap();
    assert!(
        ui.register_panel(
            &tank(),
            PanelDescriptor::new("a", "T2"),
            "<panel/>",
            schema()
        )
        .is_err()
    );
    // Another plugin may use the same window id.
    assert!(
        ui.register_panel(
            &other(),
            PanelDescriptor::new("a", "T"),
            "<panel/>",
            schema()
        )
        .is_ok()
    );
}

#[test]
fn a_plugin_reopens_its_window_after_the_player_closed_it() {
    // BufferedUiRegistryTests.APluginReopensItsOwnWindowAfterThePlayerClosedIt:
    // the player's close, the plugin's bound visibility, ShowPanel and
    // HidePanel, as the window's visibility controller combines them.
    let mut ui = PluginUi::new();
    let schema = BindingSchema::new("B").with("Visible", BindingKind::Bool);
    let id = ui
        .register_panel(
            &tank(),
            PanelDescriptor::new("main", "Main"),
            "<panel visible=\"{Visible}\"/>",
            schema,
        )
        .unwrap();
    ui.set_values(
        "edwards.tank",
        id,
        vec![("Visible".into(), UiValue::Bool(true))],
    )
    .unwrap();
    assert!(ui.is_view_visible("edwards.tank", "main"));
    // The player's close button.
    ui.input(UiInput::PanelShown {
        panel: id,
        shown: false,
    });
    assert!(!ui.is_view_visible("edwards.tank", "main"));
    // The plugin's own binding cannot bring it back...
    ui.set_values(
        "edwards.tank",
        id,
        vec![("Visible".into(), UiValue::Bool(false))],
    )
    .unwrap();
    ui.set_values(
        "edwards.tank",
        id,
        vec![("Visible".into(), UiValue::Bool(true))],
    )
    .unwrap();
    assert!(!ui.is_view_visible("edwards.tank", "main"));
    // ...and ShowPanel does.
    assert!(ui.show_panel("edwards.tank", "Main", true));
    assert!(ui.is_view_visible("edwards.tank", "main"), "shown again");
    assert!(ui.show_panel("edwards.tank", "main", false));
    assert!(!ui.is_view_visible("edwards.tank", "main"));
    // Only this plugin's own windows, by id or title.
    assert!(!ui.show_panel("another", "main", true));
    assert!(!ui.show_panel("edwards.tank", "missing", true));
    // A hide while the binding is false leaves the request: the window
    // shows again when the binding turns true.
    assert!(ui.show_panel("edwards.tank", "main", true));
    ui.set_values(
        "edwards.tank",
        id,
        vec![("Visible".into(), UiValue::Bool(false))],
    )
    .unwrap();
    assert!(ui.show_panel("edwards.tank", "main", false));
    ui.set_values(
        "edwards.tank",
        id,
        vec![("Visible".into(), UiValue::Bool(true))],
    )
    .unwrap();
    assert!(ui.is_view_visible("edwards.tank", "main"));
}

#[test]
fn values_must_be_declared_and_of_their_kind() {
    let mut ui = PluginUi::new();
    let schema = BindingSchema::new("Panel")
        .with("Status", BindingKind::Text)
        .with("Go", BindingKind::Action)
        .with("Count", BindingKind::Int);
    let id = ui
        .register_panel(&tank(), PanelDescriptor::new("p", "P"), "<panel/>", schema)
        .unwrap();
    assert!(
        ui.set_values(
            "edwards.tank",
            id,
            vec![("Status".into(), UiValue::Text("ok".into()))]
        )
        .is_ok()
    );
    let e = ui
        .set_values("edwards.tank", id, vec![("Nope".into(), UiValue::Int(1))])
        .unwrap_err();
    assert!(e.contains("not a binding of Panel"), "{e}");
    assert!(
        ui.set_values("edwards.tank", id, vec![("Go".into(), UiValue::Int(1))])
            .is_err()
    );
    // All or nothing: the good value before the bad one is not applied.
    assert!(
        ui.set_values(
            "edwards.tank",
            id,
            vec![
                ("Count".into(), UiValue::Int(3)),
                ("Status".into(), UiValue::Int(1)),
            ],
        )
        .is_err()
    );
    assert_eq!(ui.panels().next().unwrap().values.get("Count"), None);
    // Another plugin cannot push into it.
    assert!(ui.set_values("another.plugin", id, vec![]).is_err());
}

#[test]
fn actions_reach_only_their_plugin_and_only_declared_handlers() {
    let mut ui = PluginUi::new();
    let schema = BindingSchema::new("B")
        .with("Go", BindingKind::Action)
        .with("Pick", BindingKind::IntAction);
    let id = ui
        .register_panel(
            &tank(),
            PanelDescriptor::new("p", "P"),
            "<panel><button onclick=\"{Go}\"/></panel>",
            schema,
        )
        .unwrap();
    ui.input(UiInput::Action {
        panel: id,
        handler: "Go".into(),
        value: ActionValue::None,
    });
    ui.input(UiInput::Action {
        panel: id,
        handler: "Pick".into(),
        value: ActionValue::Int(2),
    });
    // A handler the panel does not declare with that argument, or a panel
    // that is gone, is dropped.
    ui.input(UiInput::Action {
        panel: id,
        handler: "Go".into(),
        value: ActionValue::Int(2),
    });
    ui.input(UiInput::Action {
        panel: 999,
        handler: "Go".into(),
        value: ActionValue::None,
    });
    assert_eq!(
        ui.take_events(),
        vec![
            (
                "edwards.tank".to_owned(),
                UiEvent::Action {
                    panel: id,
                    handler: "Go".into(),
                    value: ActionValue::None
                }
            ),
            (
                "edwards.tank".to_owned(),
                UiEvent::Action {
                    panel: id,
                    handler: "Pick".into(),
                    value: ActionValue::Int(2)
                }
            ),
        ]
    );
    assert!(ui.take_events().is_empty());
}

#[test]
fn client_windows_need_a_window_that_has_them() {
    // BufferedUiRegistryTests.ClientWindowControlIsUnavailableUntilBound,
    // ClientWindowControlForwardsToTheBoundRetailSeamOnceBound,
    // UnbindClientWindowControl_ClearsAllFourDelegates
    let mut ui = PluginUi::new();
    let inventory = ClientWindow::Inventory;
    for action in [
        ClientWindowAction::Toggle,
        ClientWindowAction::Show,
        ClientWindowAction::Hide,
    ] {
        assert!(!ui.client_window(inventory, action));
    }
    assert!(!ui.is_client_window_visible(inventory));
    ui.set_window_attached(true);
    // Attached, but the window has not said which it mounts yet.
    assert!(!ui.client_window(inventory, ClientWindowAction::Show));
    ui.input(UiInput::ClientWindows {
        mounted: inventory.bit() | ClientWindow::Character.bit(),
        visible: 0,
    });
    // The first toggle shows, the second hides.
    assert!(ui.client_window(inventory, ClientWindowAction::Toggle));
    assert!(ui.is_client_window_visible(inventory));
    assert!(!ui.client_window(inventory, ClientWindowAction::Toggle));
    assert!(!ui.is_client_window_visible(inventory));
    assert!(ui.client_window(ClientWindow::Character, ClientWindowAction::Show));
    assert!(ui.is_client_window_visible(ClientWindow::Character));
    assert!(ui.client_window(ClientWindow::Character, ClientWindowAction::Hide));
    assert!(!ui.is_client_window_visible(ClientWindow::Character));
    // A window this build does not mount.
    assert!(!ui.client_window(ClientWindow::Radar, ClientWindowAction::Show));
    // The requests reach the window, in order.
    let requests: Vec<_> = ui
        .take_changes()
        .into_iter()
        .filter_map(|c| match c {
            UiChange::ClientWindow { window, action } => Some((window, action)),
            _ => None,
        })
        .collect();
    assert_eq!(requests.len(), 4);
    // The window leaves: unavailable again.
    ui.set_window_attached(false);
    assert!(!ui.client_window(inventory, ClientWindowAction::Toggle));
    assert!(!ui.is_client_window_visible(ClientWindow::Character));
}

fn canvas(id: &str) -> CanvasDescriptor {
    CanvasDescriptor {
        canvas_id: id.into(),
        width: 64,
        height: 32,
        anchor: CanvasAnchor::TopLeft,
        offset: (0.0, 0.0),
        start_visible: true,
        accepts_pointer_input: false,
    }
}

#[test]
fn canvases_are_unique_per_plugin_and_capped_at_eight() {
    // BufferedUiRegistryCanvasTests (registration rules)
    let mut ui = PluginUi::new();
    let first = ui.register_canvas(&tank(), canvas("map")).unwrap();
    assert_eq!(
        ui.register_canvas(&tank(), canvas("map")),
        Err("Plugin canvas 'map' is already registered. Canvas ids must be unique within one plugin.".to_owned())
    );
    assert!(ui.register_canvas(&other(), canvas("map")).is_ok());
    for i in 1..MAXIMUM_CANVASES {
        ui.register_canvas(&tank(), canvas(&format!("c{i}")))
            .unwrap();
    }
    assert_eq!(
        ui.register_canvas(&tank(), canvas("ninth")),
        Err("Plugin canvas 'ninth' refused: the plugin already holds 8 canvases, which is the most it may.".to_owned())
    );
    assert!(
        ui.register_canvas(
            &tank(),
            CanvasDescriptor {
                width: 0,
                ..canvas("z")
            }
        )
        .is_err()
    );
    assert!(ui.register_canvas(&tank(), canvas(" ")).is_err());
    // Removing one makes room.
    assert!(ui.remove_canvas("edwards.tank", first));
    assert!(ui.register_canvas(&tank(), canvas("ninth")).is_ok());
}

#[test]
fn a_canvas_keeps_the_state_the_plugin_sets_with_or_without_a_window() {
    // PluginCanvasContractTests (NoOpPluginCanvas keeps what the plugin sets)
    let mut ui = PluginUi::new();
    let id = ui
        .register_canvas(
            &tank(),
            CanvasDescriptor {
                start_visible: false,
                anchor: CanvasAnchor::BottomRight,
                offset: (-10.0, -10.0),
                ..canvas("hud")
            },
        )
        .unwrap();
    let c = ui.canvas("edwards.tank", id).unwrap();
    assert_eq!(
        (c.visible, c.anchor, c.offset),
        (false, CanvasAnchor::BottomRight, (-10.0, -10.0))
    );
    assert!(!ui.canvas_available("edwards.tank", id));
    assert!(ui.place_canvas(
        "edwards.tank",
        id,
        Some(true),
        Some(CanvasAnchor::Center),
        Some((1.0, 2.0))
    ));
    let c = ui.canvas("edwards.tank", id).unwrap();
    assert_eq!(
        (c.visible, c.anchor, c.offset),
        (true, CanvasAnchor::Center, (1.0, 2.0))
    );
    ui.set_window_attached(true);
    assert!(ui.canvas_available("edwards.tank", id));
    assert!(!ui.place_canvas("another.plugin", id, Some(false), None, None));
}

#[test]
fn a_paint_must_balance_its_clips() {
    let mut ui = PluginUi::new();
    let id = ui.register_canvas(&tank(), canvas("map")).unwrap();
    let rect = PaintRect {
        x: 0.0,
        y: 0.0,
        w: 4.0,
        h: 4.0,
    };
    assert!(
        ui.paint_canvas(
            "edwards.tank",
            id,
            vec![
                PaintCmd::PushClip(rect),
                PaintCmd::FillRect {
                    rect,
                    color: Color8::WHITE
                },
                PaintCmd::PopClip
            ]
        )
        .is_ok()
    );
    assert_eq!(
        ui.paint_canvas("edwards.tank", id, vec![PaintCmd::PushClip(rect)]),
        Err("the paint leaves 1 clip(s) pushed".to_owned())
    );
    assert_eq!(
        ui.paint_canvas("edwards.tank", id, vec![PaintCmd::PopClip]),
        Err("the paint pops a clip it did not push".to_owned())
    );
    // The good paint stays.
    assert_eq!(ui.canvas("edwards.tank", id).unwrap().paint.len(), 3);
}

#[test]
fn pointer_input_reaches_a_listening_canvas_that_opted_in() {
    let mut ui = PluginUi::new();
    let quiet = ui.register_canvas(&tank(), canvas("quiet")).unwrap();
    let map = ui
        .register_canvas(
            &tank(),
            CanvasDescriptor {
                accepts_pointer_input: true,
                ..canvas("map")
            },
        )
        .unwrap();
    let event = PointerEvent {
        kind: PointerKind::Down,
        x: 3.0,
        y: 4.0,
        button: 1,
        modifiers: 0,
        wheel_delta: 0,
    };
    // Nobody listens yet: click-through.
    ui.input(UiInput::Pointer {
        canvas: map,
        event,
        place: None,
    });
    assert!(ui.take_events().is_empty());
    assert!(ui.listen_canvas("edwards.tank", map, true));
    assert!(ui.listen_canvas("edwards.tank", quiet, true));
    ui.input(UiInput::Pointer {
        canvas: map,
        event,
        place: None,
    });
    ui.input(UiInput::Pointer {
        canvas: quiet,
        event,
        place: None,
    });
    assert_eq!(
        ui.take_events(),
        vec![(
            "edwards.tank".to_owned(),
            UiEvent::Pointer {
                canvas: map,
                event,
                place: None,
            }
        )]
    );
    assert!(ui.release_canvas_pointer("edwards.tank", map));
    assert_eq!(ui.canvas("edwards.tank", map).unwrap().release_count, 1);
}

/// The pointer events a plugin is handed, as (kind, x).
fn pointer_kinds(ui: &mut PluginUi) -> Vec<(PointerKind, f32)> {
    ui.take_events()
        .into_iter()
        .filter_map(|(_, e)| match e {
            UiEvent::Pointer { event, .. } => Some((event.kind, event.x)),
            _ => None,
        })
        .collect()
}

fn hover_canvas(ui: &mut PluginUi) -> u32 {
    let map = ui
        .register_canvas(
            &tank(),
            CanvasDescriptor {
                accepts_pointer_input: true,
                ..canvas("map")
            },
        )
        .unwrap();
    assert!(ui.listen_canvas("edwards.tank", map, true));
    map
}

fn point(ui: &mut PluginUi, canvas: u32, kind: PointerKind, x: f32) {
    ui.input(UiInput::Pointer {
        canvas,
        event: PointerEvent {
            kind,
            x,
            y: 4.0,
            button: u8::from(matches!(kind, PointerKind::Down | PointerKind::Up)),
            modifiers: 0,
            wheel_delta: 0,
        },
        place: None,
    });
}

#[test]
fn hovers_not_yet_handed_over_are_one_at_the_latest_place() {
    let mut ui = PluginUi::new();
    let map = hover_canvas(&mut ui);
    let other = ui
        .register_canvas(
            &tank(),
            CanvasDescriptor {
                accepts_pointer_input: true,
                ..canvas("other")
            },
        )
        .unwrap();
    assert!(ui.listen_canvas("edwards.tank", other, true));
    point(&mut ui, map, PointerKind::Hover, 1.0);
    point(&mut ui, other, PointerKind::Hover, 9.0);
    point(&mut ui, map, PointerKind::Hover, 2.0);
    point(&mut ui, map, PointerKind::Hover, 3.0);
    assert_eq!(
        pointer_kinds(&mut ui),
        vec![(PointerKind::Hover, 9.0), (PointerKind::Hover, 3.0)]
    );
    // Once handed over, the next hover is its own; a press between two
    // hovers keeps both, in order.
    point(&mut ui, map, PointerKind::Hover, 4.0);
    point(&mut ui, map, PointerKind::Down, 4.0);
    point(&mut ui, map, PointerKind::Up, 4.0);
    point(&mut ui, map, PointerKind::Hover, 5.0);
    assert_eq!(
        pointer_kinds(&mut ui),
        vec![
            (PointerKind::Hover, 4.0),
            (PointerKind::Down, 4.0),
            (PointerKind::Up, 4.0),
            (PointerKind::Hover, 5.0),
        ]
    );
}

#[test]
fn a_hover_is_owed_exactly_one_leave() {
    let mut ui = PluginUi::new();
    let map = hover_canvas(&mut ui);
    // No hover, no leave.
    point(&mut ui, map, PointerKind::Leave, 0.0);
    assert!(pointer_kinds(&mut ui).is_empty());
    point(&mut ui, map, PointerKind::Hover, 7.0);
    // The plugin stops listening: the leave it is owed still comes, once.
    assert!(ui.listen_canvas("edwards.tank", map, false));
    point(&mut ui, map, PointerKind::Leave, 8.0);
    point(&mut ui, map, PointerKind::Leave, 8.0);
    assert_eq!(
        pointer_kinds(&mut ui),
        vec![(PointerKind::Hover, 7.0), (PointerKind::Leave, 8.0)]
    );
    // The window goes while the pointer hovers: the leave comes from the
    // session, at the last hover's place; the window's own never comes.
    assert!(ui.listen_canvas("edwards.tank", map, true));
    ui.set_window_attached(true);
    point(&mut ui, map, PointerKind::Hover, 6.0);
    ui.set_window_attached(false);
    assert_eq!(
        pointer_kinds(&mut ui),
        vec![(PointerKind::Hover, 6.0), (PointerKind::Leave, 6.0)]
    );
    // Removed while hovered: one leave, and none from the window after.
    point(&mut ui, map, PointerKind::Hover, 5.0);
    assert!(ui.remove_canvas("edwards.tank", map));
    point(&mut ui, map, PointerKind::Leave, 0.0);
    assert_eq!(
        pointer_kinds(&mut ui),
        vec![(PointerKind::Hover, 5.0), (PointerKind::Leave, 5.0)]
    );
    // A plugin that is unloaded while its canvas is hovered hears nothing.
    let map = hover_canvas(&mut ui);
    point(&mut ui, map, PointerKind::Hover, 5.0);
    ui.take_events();
    ui.release("edwards.tank");
    assert!(pointer_kinds(&mut ui).is_empty());
}

#[test]
fn a_held_pointer_is_measured_from_where_the_plugin_last_put_the_canvas() {
    let mut ui = PluginUi::new();
    let owner = UiOwner::new("edwards.tank", "Tank");
    let hud = ui
        .register_canvas(
            &owner,
            CanvasDescriptor {
                canvas_id: "hud".into(),
                width: 100,
                height: 50,
                anchor: CanvasAnchor::TopRight,
                offset: (-20.0, 30.0),
                start_visible: true,
                accepts_pointer_input: true,
            },
        )
        .unwrap();
    // The window has it at (680, 30) on an 800x600 layer.
    let place = CanvasPlace {
        origin: (680.0, 30.0),
        layer: (800.0, 600.0),
    };
    let heard = |ui: &PluginUi, kind| {
        let event = PointerEvent {
            kind,
            x: 15.0,
            y: 25.0,
            button: 1,
            modifiers: 0,
            wheel_delta: 0,
        };
        let event = ui.pointer_as_placed_now(UiEvent::Pointer {
            canvas: hud,
            event,
            place: Some(place),
        });
        let UiEvent::Pointer { event, .. } = event else {
            panic!("still a pointer event");
        };
        (event.x, event.y)
    };
    // Nothing moved: as the window measured it.
    assert_eq!(heard(&ui, PointerKind::Move), (15.0, 25.0));
    // The plugin moved it, and re-anchored it, as a drag does; the window has
    // not caught up.
    assert!(ui.place_canvas(
        "edwards.tank",
        hud,
        None,
        Some(CanvasAnchor::TopLeft),
        Some((690.0, 40.0)),
    ));
    assert_eq!(heard(&ui, PointerKind::Move), (5.0, 15.0));
    assert_eq!(heard(&ui, PointerKind::Up), (5.0, 15.0));
    // A press stays as the player saw it.
    assert_eq!(heard(&ui, PointerKind::Down), (15.0, 25.0));
}

#[test]
fn a_held_pointer_whose_place_is_not_known_is_heard_as_sent() {
    // Input that no window measured (a test, a script) carries no place: a
    // canvas anchored away from the top-left is not moved by a made-up
    // zero-sized layer.
    let mut ui = PluginUi::new();
    let owner = UiOwner::new("edwards.tank", "Tank");
    let hud = ui
        .register_canvas(
            &owner,
            CanvasDescriptor {
                canvas_id: "hud".into(),
                width: 100,
                height: 50,
                anchor: CanvasAnchor::BottomRight,
                offset: (-20.0, -30.0),
                start_visible: true,
                accepts_pointer_input: true,
            },
        )
        .unwrap();
    for kind in [PointerKind::Down, PointerKind::Move, PointerKind::Up] {
        let event = PointerEvent {
            kind,
            x: 15.0,
            y: 25.0,
            button: 1,
            modifiers: 2,
            wheel_delta: 0,
        };
        let sent = UiEvent::Pointer {
            canvas: hud,
            event,
            place: None,
        };
        assert_eq!(ui.pointer_as_placed_now(sent.clone()), sent, "{kind:?}");
    }
}

#[test]
fn canvases_sit_at_their_anchor_plus_offset() {
    // PluginCanvasElementTests (Layout): a 100x50 canvas on an 800x600 layer.
    let at = |a, o| canvas_origin(a, o, (100.0, 50.0), (800.0, 600.0));
    assert_eq!(at(CanvasAnchor::TopLeft, (10.0, 20.0)), (10.0, 20.0));
    assert_eq!(at(CanvasAnchor::TopCenter, (0.0, 0.0)), (350.0, 0.0));
    assert_eq!(
        at(CanvasAnchor::BottomRight, (-10.0, -10.0)),
        (690.0, 540.0)
    );
    assert_eq!(at(CanvasAnchor::Center, (0.4, 0.5)), (350.0, 276.0));
    assert_eq!(at(CanvasAnchor::CenterLeft, (0.0, 0.0)), (0.0, 275.0));
}

#[test]
fn labels_are_detached_validated_capped_and_per_owner() {
    // WorldLabelAutomationTests.ThePushedSetIsDetachedAndUnusableLabelsAreDropped,
    // ASetPastTheCapIsRefusedAndTheOldSetStays,
    // EachOwnerReplacesOnlyItsOwnSetAndTheCapIsPerOwner,
    // ReleasingAnOwnerTakesItsLabelsDownAndUnbindTakesEveryOnesDown
    let mut ui = PluginUi::new();
    let mut set = vec![
        label(0x7000_0001),
        label(0),
        WorldLabel {
            text: String::new(),
            ..label(0x7000_0002)
        },
        WorldLabel {
            max_range: 0.0,
            ..label(0x7000_0003)
        },
        WorldLabel {
            max_range: f32::INFINITY,
            ..label(0x7000_0004)
        },
        WorldLabel {
            max_range: f32::NAN,
            ..label(0x7000_0005)
        },
        WorldLabel {
            height_offset: f32::NAN,
            ..label(0x7000_0006)
        },
        WorldLabel {
            max_range: 12.0,
            line: 1,
            ..label(0x7000_0007)
        },
        WorldLabel {
            text: "x".repeat(MAXIMUM_LABEL_TEXT + 1),
            ..label(0x7000_0008)
        },
        WorldLabel {
            text: "x".repeat(MAXIMUM_LABEL_TEXT),
            ..label(0x7000_0009)
        },
        WorldLabel {
            color: [f32::NAN, 1.0, 1.0, 1.0],
            ..label(0x7000_000A)
        },
        WorldLabel {
            color: [1.0, 1.0, 1.0, f32::INFINITY],
            ..label(0x7000_000B)
        },
    ];
    assert!(ui.show_labels("first", set.clone()));
    set[0] = label(0x1234);
    let shown = ui.merged_labels();
    assert_eq!(
        shown.iter().map(|l| l.object_id).collect::<Vec<_>>(),
        [0x7000_0001, 0x7000_0007, 0x7000_0009]
    );
    assert_eq!(shown[1].line, 1);

    let too_many: Vec<WorldLabel> = (1..=MAXIMUM_LABELS as u32 + 1).map(label).collect();
    assert!(!ui.show_labels("first", too_many.clone()));
    assert_eq!(ui.merged_labels().len(), 3);
    assert!(ui.show_labels("first", too_many[..MAXIMUM_LABELS].to_vec()));
    assert!(ui.show_labels("second", too_many[..MAXIMUM_LABELS].to_vec()));
    assert_eq!(ui.merged_labels().len(), 2 * MAXIMUM_LABELS);
    assert!(ui.show_labels(
        "first",
        vec![WorldLabel {
            text: "one".into(),
            ..label(0x7000_0001)
        }]
    ));
    assert_eq!(ui.merged_labels().len(), 1 + MAXIMUM_LABELS);
    assert!(ui.show_labels("second", vec![]));
    let only = ui.merged_labels();
    assert_eq!(only.len(), 1);
    assert_eq!(only[0].text, "one");
    ui.release("first");
    assert!(ui.merged_labels().is_empty());
}

#[test]
fn triangle_layers_belong_to_their_plugin_and_go_with_it() {
    let mut ui = PluginUi::new();
    let corner = crate::model::MapPoint::default();
    let triangle = WorldTriangle {
        corners: [corner; 3],
        colors_rgba: [0xFF00_0080; 3],
    };
    let a = ui.create_triangle_layer("edwards.tank");
    let b = ui.create_triangle_layer("another.plugin");
    assert!(ui.set_triangles("edwards.tank", a, vec![triangle; 3]));
    assert!(ui.set_triangles("another.plugin", b, vec![triangle]));
    assert!(!ui.set_triangles("another.plugin", a, vec![]));
    assert_eq!(ui.merged_triangles().len(), 4);
    assert!(matches!(
        ui.take_changes().last(),
        Some(UiChange::Triangles(t)) if t.len() == 4
    ));
    assert!(ui.set_triangles("edwards.tank", a, vec![triangle; MAXIMUM_TRIANGLES]));
    assert_eq!(ui.merged_triangles().len(), MAXIMUM_TRIANGLES + 1);
    assert!(
        ui.snapshot()
            .iter()
            .any(|c| matches!(c, UiChange::Triangles(t) if t.len() == MAXIMUM_TRIANGLES + 1))
    );
    ui.release("edwards.tank");
    assert_eq!(ui.merged_triangles().len(), 1);
    assert!(!ui.remove_triangle_layer("edwards.tank", b));
    assert!(ui.remove_triangle_layer("another.plugin", b));
    assert!(ui.merged_triangles().is_empty());
}

/// A plugin holds at most `MAXIMUM_TRIANGLES` across its layers, as it
/// does icons and shapes: a set that would take it over is refused and the
/// layer keeps what it had, so more layers buy no more triangles. Other
/// plugins count apart.
#[test]
fn a_plugin_holds_a_limited_number_of_triangles_across_its_layers() {
    let mut ui = PluginUi::new();
    let corner = crate::model::MapPoint::default();
    let triangle = WorldTriangle {
        corners: [corner; 3],
        colors_rgba: [0xFF00_0080; 3],
    };
    let half = MAXIMUM_TRIANGLES / 2;
    let a = ui.create_triangle_layer("edwards.tank");
    let b = ui.create_triangle_layer("edwards.tank");
    let c = ui.create_triangle_layer("edwards.tank");
    let other = ui.create_triangle_layer("another.plugin");
    assert!(ui.set_triangles("edwards.tank", a, vec![triangle; half]));
    assert!(ui.set_triangles("edwards.tank", b, vec![triangle; half]));
    drop(ui.take_changes());
    assert!(!ui.set_triangles("edwards.tank", c, vec![triangle]));
    assert!(ui.take_changes().is_empty());
    assert_eq!(ui.merged_triangles().len(), MAXIMUM_TRIANGLES);
    // Replacing a layer counts its new set, not the one it replaces.
    assert!(ui.set_triangles("edwards.tank", b, vec![triangle; half - 1]));
    assert!(ui.set_triangles("edwards.tank", c, vec![triangle]));
    assert!(!ui.set_triangles("edwards.tank", a, vec![triangle; half + 1]));
    assert_eq!(ui.merged_triangles().len(), MAXIMUM_TRIANGLES);
    // One set over the limit is refused whole.
    assert!(!ui.set_triangles(
        "another.plugin",
        other,
        vec![triangle; MAXIMUM_TRIANGLES + 1]
    ));
    assert!(ui.set_triangles("another.plugin", other, vec![triangle; MAXIMUM_TRIANGLES]));
    assert_eq!(ui.merged_triangles().len(), 2 * MAXIMUM_TRIANGLES);
}

fn icon(anchor: crate::model::MarkerAnchor) -> crate::model::WorldIcon {
    crate::model::WorldIcon {
        anchor,
        image: 7,
        size: 24.0,
        tint_rgba: 0xFFFF_FFFF,
        border_rgba: None,
        max_range: 60.0,
        hidden_by_world: false,
    }
}

#[test]
fn marker_layers_merge_by_plugin_and_go_with_it() {
    use crate::model::MarkerAnchor;
    let mut ui = PluginUi::new();
    let later = ui.create_marker_layer("z.plugin");
    let first = ui.create_marker_layer("a.plugin");
    let second = ui.create_marker_layer("a.plugin");
    assert!(ui.set_icons("z.plugin", later, vec![icon(MarkerAnchor::Object(3))]));
    assert!(ui.set_icons("a.plugin", second, vec![icon(MarkerAnchor::Object(2))]));
    assert!(ui.set_icons("a.plugin", first, vec![icon(MarkerAnchor::Object(1))]));
    assert!(!ui.set_icons("z.plugin", first, vec![]));
    let merged = ui.merged_icons();
    let order: Vec<_> = merged.iter().map(|e| (&*e.owner, e.icon.anchor)).collect();
    assert_eq!(
        order,
        [
            ("a.plugin", MarkerAnchor::Object(1)),
            ("a.plugin", MarkerAnchor::Object(2)),
            ("z.plugin", MarkerAnchor::Object(3)),
        ]
    );
    assert!(matches!(
        ui.take_changes().last(),
        Some(UiChange::Icons(i)) if i.len() == 3
    ));
    assert!(
        ui.snapshot()
            .iter()
            .any(|c| matches!(c, UiChange::Icons(i) if i.len() == 3))
    );
    assert!(ui.remove_marker_layer("a.plugin", second));
    assert_eq!(ui.merged_icons().len(), 2);
    ui.release("a.plugin");
    assert_eq!(ui.merged_icons().len(), 1);
    assert!(!ui.remove_marker_layer("a.plugin", first));
}

#[test]
fn unusable_icons_are_dropped_and_sizes_are_clamped() {
    use crate::model::{MapPoint, MarkerAnchor};
    let mut ui = PluginUi::new();
    let layer = ui.create_marker_layer("edwards.tank");
    let good = icon(MarkerAnchor::Object(5));
    let bad_point = MapPoint {
        east_west: f64::NAN,
        ..MapPoint::default()
    };
    let given = vec![
        good,
        icon(MarkerAnchor::None),
        icon(MarkerAnchor::Object(0)),
        icon(MarkerAnchor::Position {
            point: bad_point,
            is_outdoor: true,
        }),
        crate::model::WorldIcon { image: 0, ..good },
        crate::model::WorldIcon {
            size: f32::NAN,
            ..good
        },
        crate::model::WorldIcon {
            max_range: 0.0,
            ..good
        },
        crate::model::WorldIcon {
            max_range: f32::INFINITY,
            ..good
        },
        crate::model::WorldIcon { size: 1.0, ..good },
        crate::model::WorldIcon {
            size: 500.0,
            ..good
        },
    ];
    assert!(ui.set_icons("edwards.tank", layer, given));
    let sizes: Vec<f32> = ui.merged_icons().iter().map(|e| e.icon.size).collect();
    assert_eq!(sizes, [24.0, MINIMUM_ICON_SIZE, MAXIMUM_ICON_SIZE]);
}

#[test]
fn the_icon_cap_counts_a_plugins_other_layers_and_only_its_own() {
    use crate::model::MarkerAnchor;
    let mut ui = PluginUi::new();
    let one = icon(MarkerAnchor::Object(1));
    let a = ui.create_marker_layer("edwards.tank");
    let b = ui.create_marker_layer("edwards.tank");
    let other = ui.create_marker_layer("another.plugin");
    assert!(ui.set_icons("edwards.tank", a, vec![one; MAXIMUM_ICONS - 1]));
    assert!(ui.set_icons("edwards.tank", b, vec![one]));
    assert!(!ui.set_icons("edwards.tank", b, vec![one; 2]));
    assert_eq!(ui.merged_icons().len(), MAXIMUM_ICONS);
    assert!(ui.set_icons("another.plugin", other, vec![one; MAXIMUM_ICONS]));
    // Replacing a layer counts its new set, not its old one.
    assert!(ui.set_icons("edwards.tank", a, vec![one; MAXIMUM_ICONS - 1]));
}

fn ring(radius: f32, width: f32) -> crate::model::GroundShape {
    crate::model::GroundShape {
        anchor: crate::model::MarkerAnchor::Object(5),
        kind: crate::model::GroundShapeKind::Ring,
        radius,
        width,
        start_degrees: 40.0,
        sweep_degrees: 90.0,
        filled: true,
        faces_object: true,
        color_rgba: 0xFF00_0080,
    }
}

#[test]
fn a_ring_or_disc_is_a_full_turn_and_unusable_shapes_are_dropped() {
    use crate::model::{GroundShape, GroundShapeKind, MarkerAnchor};
    let mut ui = PluginUi::new();
    let layer = ui.create_marker_layer("edwards.tank");
    let good = ring(2.0, 0.5);
    let disc = GroundShape {
        kind: GroundShapeKind::Disc,
        width: f32::NAN,
        ..good
    };
    let arc = GroundShape {
        kind: GroundShapeKind::Arc,
        filled: false,
        ..good
    };
    let wedge = GroundShape {
        kind: GroundShapeKind::Arc,
        width: f32::NAN,
        ..good
    };
    let given = vec![
        good,
        disc,
        arc,
        wedge,
        GroundShape {
            kind: GroundShapeKind::None,
            ..good
        },
        GroundShape {
            anchor: MarkerAnchor::None,
            ..good
        },
        ring(0.05, 0.01),
        ring(101.0, 1.0),
        ring(f32::NAN, 1.0),
        ring(2.0, 0.0),
        ring(2.0, 2.5),
        GroundShape {
            sweep_degrees: 0.0,
            ..arc
        },
        GroundShape {
            sweep_degrees: 361.0,
            ..arc
        },
        GroundShape {
            start_degrees: f32::NAN,
            ..arc
        },
        GroundShape { width: 0.0, ..arc },
    ];
    assert!(ui.set_shapes("edwards.tank", layer, given));
    let shapes = ui.merged_shapes();
    assert_eq!(shapes.len(), 4);
    let (r, d, a, w) = (shapes[0], shapes[1], shapes[2], shapes[3]);
    assert_eq!((r.start_degrees, r.sweep_degrees), (0.0, 360.0));
    assert!(!r.filled && !r.faces_object);
    assert_eq!(
        (d.width, d.start_degrees, d.sweep_degrees),
        (0.0, 0.0, 360.0)
    );
    assert!(d.filled && !d.faces_object);
    assert_eq!(
        (a.start_degrees, a.sweep_degrees, a.width),
        (40.0, 90.0, 0.5)
    );
    assert!(!a.filled && a.faces_object);
    assert_eq!(w.width, 0.0);
    assert!(w.filled);
}

#[test]
fn a_shapes_limits_themselves_are_accepted() {
    use crate::model::{GroundShape, GroundShapeKind};
    let mut ui = PluginUi::new();
    let layer = ui.create_marker_layer("edwards.tank");
    let arc = GroundShape {
        kind: GroundShapeKind::Arc,
        filled: false,
        sweep_degrees: 360.0,
        ..ring(MAXIMUM_SHAPE_RADIUS, MAXIMUM_SHAPE_RADIUS)
    };
    let given = vec![
        arc,
        ring(MINIMUM_SHAPE_RADIUS, MINIMUM_SHAPE_RADIUS),
        ring(MAXIMUM_SHAPE_RADIUS, MAXIMUM_SHAPE_RADIUS),
    ];
    assert!(ui.set_shapes("edwards.tank", layer, given));
    assert_eq!(ui.merged_shapes().len(), 3);
}

#[test]
fn setting_one_kind_of_marker_leaves_the_other_alone() {
    use crate::model::MarkerAnchor;
    let mut ui = PluginUi::new();
    let layer = ui.create_marker_layer("edwards.tank");
    assert!(ui.set_icons("edwards.tank", layer, vec![icon(MarkerAnchor::Object(5))]));
    assert!(ui.set_shapes("edwards.tank", layer, vec![ring(2.0, 0.5)]));
    assert!(ui.set_shapes("edwards.tank", layer, vec![]));
    assert_eq!(ui.merged_icons().len(), 1);
    assert!(ui.set_shapes("edwards.tank", layer, vec![ring(2.0, 0.5)]));
    assert!(ui.set_icons("edwards.tank", layer, vec![]));
    assert_eq!(ui.merged_shapes().len(), 1);
}

#[test]
fn the_shape_cap_counts_a_plugins_other_layers_apart_from_its_icons() {
    use crate::model::MarkerAnchor;
    let mut ui = PluginUi::new();
    let a = ui.create_marker_layer("edwards.tank");
    let b = ui.create_marker_layer("edwards.tank");
    let one = ring(2.0, 0.5);
    assert!(ui.set_icons(
        "edwards.tank",
        a,
        vec![icon(MarkerAnchor::Object(1)); MAXIMUM_ICONS]
    ));
    assert!(ui.set_shapes("edwards.tank", a, vec![one; MAXIMUM_SHAPES - 1]));
    assert!(ui.set_shapes("edwards.tank", b, vec![one]));
    assert!(!ui.set_shapes("edwards.tank", b, vec![one; 2]));
    assert_eq!(ui.merged_shapes().len(), MAXIMUM_SHAPES);
    assert!(!ui.set_shapes("another.plugin", b, vec![one]));
    assert!(
        ui.snapshot()
            .iter()
            .any(|c| matches!(c, UiChange::Shapes(s) if s.len() == MAXIMUM_SHAPES))
    );
    ui.clear_object_markers();
    assert!(ui.merged_shapes().is_empty());
    assert!(ui.set_shapes("edwards.tank", a, vec![one]));
    assert!(ui.remove_marker_layer("edwards.tank", a));
    assert!(ui.merged_shapes().is_empty());
}

#[test]
fn clearing_markers_empties_the_layers_and_they_stay_usable() {
    use crate::model::MarkerAnchor;
    let mut ui = PluginUi::new();
    let layer = ui.create_marker_layer("edwards.tank");
    assert!(ui.set_icons("edwards.tank", layer, vec![icon(MarkerAnchor::Object(5))]));
    drop(ui.take_changes());
    ui.clear_object_markers();
    assert!(ui.merged_icons().is_empty());
    assert!(matches!(
        ui.take_changes().as_slice(),
        [UiChange::Icons(i), UiChange::Shapes(s)] if i.is_empty() && s.is_empty()
    ));
    ui.clear_object_markers();
    assert!(ui.take_changes().is_empty());
    assert!(ui.set_icons("edwards.tank", layer, vec![icon(MarkerAnchor::Object(5))]));
    assert_eq!(ui.merged_icons().len(), 1);
}

/// Leaving the world takes down what hangs on an object, whose id means
/// nothing on the next stay, and keeps what sits at a fixed position, which
/// is the same place in the world after a relog: markers at positions stay,
/// as lines and triangles do.
#[test]
fn clearing_object_markers_keeps_markers_lines_and_triangles_at_positions() {
    use crate::model::{MapPoint, MarkerAnchor};
    let mut ui = PluginUi::new();
    let here = MarkerAnchor::Position {
        point: MapPoint {
            east_west: 40.0,
            north_south: 12.5,
            elevation: 0.3,
        },
        is_outdoor: true,
    };
    let layer = ui.create_marker_layer("edwards.tank");
    assert!(ui.set_icons(
        "edwards.tank",
        layer,
        vec![icon(MarkerAnchor::Object(5)), icon(here)]
    ));
    let on_object = ring(2.0, 0.5);
    let at_position = crate::model::GroundShape {
        anchor: here,
        ..on_object
    };
    assert!(ui.set_shapes("edwards.tank", layer, vec![on_object, at_position]));
    let lines = ui.create_line_layer("edwards.tank");
    let line = WorldLine {
        start: MapPoint::default(),
        end: MapPoint::default(),
        color_rgb: 0xFF0000,
        follow_terrain: false,
        width_metres: 0.25,
    };
    assert!(ui.set_lines("edwards.tank", lines, vec![line]));
    let triangles = ui.create_triangle_layer("edwards.tank");
    let triangle = WorldTriangle {
        corners: [MapPoint::default(); 3],
        colors_rgba: [0xFF00_0080; 3],
    };
    assert!(ui.set_triangles("edwards.tank", triangles, vec![triangle]));
    drop(ui.take_changes());

    ui.clear_object_markers();
    let icons = ui.merged_icons();
    assert_eq!(icons.len(), 1);
    assert_eq!(icons[0].icon.anchor, here);
    let shapes = ui.merged_shapes();
    assert_eq!(shapes.len(), 1);
    assert_eq!(shapes[0].anchor, here);
    assert_eq!(ui.merged_lines().len(), 1);
    assert_eq!(ui.merged_triangles().len(), 1);
    assert!(matches!(
        ui.take_changes().as_slice(),
        [UiChange::Icons(i), UiChange::Shapes(s)] if i.len() == 1 && s.len() == 1
    ));
    // Nothing on an object is left, so a second clear changes nothing.
    ui.clear_object_markers();
    assert!(ui.take_changes().is_empty());
}

/// Leaving the world takes every plugin's labels down; the plugins stay and
/// show labels again on the next stay.
#[test]
fn clearing_labels_takes_every_plugins_labels_down_and_leaves_lines() {
    let mut ui = PluginUi::new();
    let line = WorldLine {
        start: crate::model::MapPoint::default(),
        end: crate::model::MapPoint::default(),
        color_rgb: 0xFF0000,
        follow_terrain: false,
        width_metres: 0.25,
    };
    let layer = ui.create_line_layer("edwards.tank");
    assert!(ui.set_lines("edwards.tank", layer, vec![line]));
    ui.show_labels("edwards.tank", vec![label(5)]);
    ui.show_labels("another.plugin", vec![label(6)]);
    drop(ui.take_changes());

    ui.clear_labels();
    assert!(ui.merged_labels().is_empty());
    assert_eq!(ui.merged_lines().len(), 1);
    let changes = ui.take_changes();
    assert!(matches!(changes.as_slice(), [UiChange::Labels(l)] if l.is_empty()));

    ui.clear_labels();
    assert!(ui.take_changes().is_empty());

    assert!(ui.show_labels("edwards.tank", vec![label(7)]));
    assert_eq!(ui.merged_labels().len(), 1);
}

#[test]
fn line_layers_belong_to_their_plugin_and_go_with_it() {
    let mut ui = PluginUi::new();
    let line = WorldLine {
        start: crate::model::MapPoint::default(),
        end: crate::model::MapPoint {
            east_west: 0.1,
            north_south: 0.0,
            elevation: 0.0,
        },
        color_rgb: 0xFF0000,
        follow_terrain: true,
        width_metres: 0.25,
    };
    let a = ui.create_line_layer("edwards.tank");
    let b = ui.create_line_layer("another.plugin");
    assert!(ui.set_lines("edwards.tank", a, vec![line; 3]));
    assert!(ui.set_lines("another.plugin", b, vec![line]));
    assert!(!ui.set_lines("another.plugin", a, vec![]));
    assert_eq!(ui.merged_lines().len(), 4);
    assert!(ui.set_lines("edwards.tank", a, vec![line; MAXIMUM_LINES_PER_LAYER + 5]));
    assert_eq!(ui.merged_lines().len(), MAXIMUM_LINES_PER_LAYER + 1);
    ui.release("edwards.tank");
    assert_eq!(ui.merged_lines().len(), 1);
    assert!(ui.remove_line_layer("another.plugin", b));
    assert!(ui.merged_lines().is_empty());
}

#[test]
fn release_takes_everything_of_one_plugin_down() {
    let mut ui = PluginUi::new();
    status_view(&mut ui);
    ui.register_panel(
        &other(),
        PanelDescriptor::new("o", "O"),
        "<panel/>",
        BindingSchema::new("B"),
    )
    .unwrap();
    ui.register_canvas(&tank(), canvas("map")).unwrap();
    let (handle, _, _) = ui.image_rgba("edwards.tank", "dot", 1, 1, vec![1, 2, 3, 4]);
    assert_ne!(handle, 0);
    ui.show_labels("edwards.tank", vec![label(5)]);
    ui.release("edwards.tank");
    assert_eq!(ui.panels().count(), 1);
    assert_eq!(ui.canvases().count(), 0);
    assert_eq!(ui.image_count("edwards.tank"), 0);
    assert!(ui.merged_labels().is_empty());
}

#[derive(Debug)]
struct Content;

impl UiResources for Content {
    fn measure_text(&self, text: &str) -> (f32, f32) {
        TextMetrics::new([('A', 7.0), ('B', 8.0)], 16.0).measure(text)
    }
    fn surface_size(&self, surface_id: u32) -> Option<(u32, u32)> {
        (surface_id == 0x0600_1234).then_some((32, 16))
    }
}

#[test]
fn text_is_measured_and_art_sized_from_the_clients_content() {
    let mut ui = PluginUi::new();
    assert_eq!(ui.measure_text("AB"), (0.0, 0.0), "no content");
    ui.set_resources(Some(Arc::new(Content)));
    assert_eq!(ui.measure_text("AB"), (15.0, 16.0));
    assert_eq!(ui.measure_text("AxB"), (15.0, 16.0));
    assert_eq!(ui.measure_text(""), (0.0, 0.0));
    // A bare index is normalised to the image block.
    assert_eq!(ui.image_client_art("p", 0x1234).1, 32);
    let (missing, _, _) = ui.image_client_art("p", 0x0600_9999);
    assert_eq!(missing, 0);
    assert_eq!(
        ui.take_reports(),
        vec![(
            "p".to_owned(),
            "client art 0x06009999 is not something the client can draw".to_owned()
        )]
    );
    assert_eq!(ui.image_spell_icon("p", 7, None).0, 0);
    assert_ne!(ui.image_spell_icon("p", 7, Some(0x0600_1234)).0, 0);
    assert_eq!(ui.image_object_icon("p", 0x8000_0001, false).0, 0);
    assert_eq!(ui.image_object_icon("p", 0x8000_0001, true), (3, 32, 32));
}

#[derive(Debug)]
struct Decoding;

impl UiResources for Decoding {
    fn measure_text(&self, _text: &str) -> (f32, f32) {
        (0.0, 0.0)
    }
    fn surface_size(&self, _surface_id: u32) -> Option<(u32, u32)> {
        None
    }
    fn image_dimensions(&self, bytes: &[u8]) -> Option<(u32, u32)> {
        bytes
            .first()
            .map(|side| (u32::from(*side), u32::from(*side)))
    }
    fn decode_image(&self, bytes: &[u8]) -> Result<crate::DecodedImage, String> {
        let side = u32::from(*bytes.first().ok_or("empty file")?);
        Ok(crate::DecodedImage {
            width: side,
            height: side,
            rgba: vec![7; (side * side * 4) as usize],
        })
    }
}

// OpenAC `IPluginImages.FromStream` (`PluginImages` over `PluginImageTable.AcquireDecoded`):
// the client decodes a plugin's file and the image lands like its own
// pixels; a host that lends no decoder refuses, and says so once.
#[test]
fn a_plugins_file_is_decoded_by_the_hosts_resources() {
    let mut ui = PluginUi::new();
    assert_eq!(ui.image_decode("p", "map.png", &[4]), (0, 0, 0));
    assert_eq!(
        ui.take_reports(),
        vec![(
            "p".to_owned(),
            "image 'map.png' could not be decoded: this host decodes no images".to_owned()
        )]
    );
    ui.set_resources(Some(Arc::new(Decoding)));
    let (handle, width, height) = ui.image_decode("p", "dot.png", &[4]);
    assert_ne!(handle, 0);
    assert_eq!((width, height), (4, 4));
    assert_eq!(ui.image_count("p"), 1);
    assert_eq!(ui.image_decode("p", "empty.png", &[]).0, 0);
    assert!(ui.take_reports()[0].1.contains("empty file"));
}

#[test]
fn a_copy_built_from_the_snapshot_and_changes_matches_the_state() {
    let mut ui = PluginUi::new();
    let schema = BindingSchema::new("B")
        .with("Status", BindingKind::Text)
        .with("Shown", BindingKind::Bool);
    let id = ui
        .register_panel(
            &tank(),
            PanelDescriptor::new("main", "Main"),
            "<panel><label name=\"L\" text=\"{Status}\"/></panel>",
            schema,
        )
        .unwrap();
    ui.set_values(
        "edwards.tank",
        id,
        vec![("Status".into(), UiValue::Text("a".into()))],
    )
    .unwrap();
    // A window attaches now: snapshot, then the changes after it.
    ui.take_changes();
    let mut copy = PluginUiView::new();
    copy.apply_all(ui.snapshot());
    assert_eq!(copy.panels, ui.panels().cloned().collect::<Vec<_>>());
    let map = ui.register_canvas(&tank(), canvas("map")).unwrap();
    ui.paint_canvas("edwards.tank", map, vec![PaintCmd::Clear(Color8::WHITE)])
        .unwrap();
    ui.place_canvas("edwards.tank", map, Some(false), None, Some((5.0, 6.0)));
    ui.set_values(
        "edwards.tank",
        id,
        vec![("Status".into(), UiValue::Text("b".into()))],
    )
    .unwrap();
    ui.set_control_label("edwards.tank", "main", "L", "fixed");
    ui.input(UiInput::PanelShown {
        panel: id,
        shown: false,
    });
    let (image, _, _) = ui.image_rgba("edwards.tank", "dot", 1, 1, vec![0, 0, 0, 255]);
    ui.show_labels("edwards.tank", vec![label(9)]);
    copy.apply_all(ui.take_changes());
    assert_eq!(copy.panels, ui.panels().cloned().collect::<Vec<_>>());
    assert_eq!(copy.canvases, ui.canvases().cloned().collect::<Vec<_>>());
    assert!(copy.image("edwards.tank", image).is_some());
    assert_eq!(copy.labels, ui.merged_labels());
    // Gone in the state, gone in the copy.
    ui.release("edwards.tank");
    copy.apply_all(ui.take_changes());
    assert!(copy.panels.is_empty() && copy.canvases.is_empty() && copy.images.is_empty());
    assert!(copy.labels.is_empty());
}

/// Hotkeys as session state: registered with no window they
/// report unbound (OpenAC's headless registration); a window that attaches
/// and names its key map's chords binds them and gets the bound table; a
/// press it reports reaches the owner only for a bound hotkey; the window
/// leaving unbinds everything; unloading takes a plugin's hotkeys.
#[test]
fn hotkeys_bind_while_a_window_with_a_key_map_is_attached() {
    use crate::hotkeys::Chord;
    const H: u32 = 8;
    const ESCAPE: u32 = 51;
    let heal = Chord::key(H).with_ctrl();
    let mut ui = PluginUi::new();
    let state = ui
        .register_hotkey("edwards.tank", "heal", "Heal", heal)
        .unwrap();
    assert!(!state.is_bound);
    ui.input(UiInput::Hotkey {
        owner: "edwards.tank".into(),
        id: "heal".into(),
    });
    assert!(ui.take_events().is_empty(), "nothing is bound headless");
    ui.take_changes();
    ui.set_window_attached(true);
    ui.input(UiInput::ClientChords(vec![Chord::key(ESCAPE)]));
    assert!(ui.hotkeys().state("edwards.tank", "heal").unwrap().is_bound);
    let mut copy = PluginUiView::new();
    copy.apply_all(ui.take_changes());
    assert_eq!(copy.hotkeys.len(), 1);
    assert_eq!(copy.hotkeys[0].chord, heal);
    ui.input(UiInput::Hotkey {
        owner: "edwards.tank".into(),
        id: "heal".into(),
    });
    assert_eq!(
        ui.take_events(),
        [("edwards.tank".to_owned(), UiEvent::Hotkey("heal".into()))]
    );
    // A snapshot carries the table too.
    let mut fresh = PluginUiView::new();
    fresh.apply_all(ui.snapshot());
    assert_eq!(fresh.hotkeys, copy.hotkeys);
    ui.set_window_attached(false);
    copy.apply_all(ui.take_changes());
    assert!(copy.hotkeys.is_empty());
    assert!(!ui.hotkeys().state("edwards.tank", "heal").unwrap().is_bound);
    ui.release("edwards.tank");
    assert!(ui.hotkeys().state("edwards.tank", "heal").is_none());
}

/// Input action listeners: the setting answers
/// what it was; a fired action reaches every listener in the order they
/// asked, only while a window is attached; unloading stops a plugin
/// listening.
#[test]
fn input_actions_reach_the_plugins_that_listen_while_a_window_is_attached() {
    let fire = |ui: &mut PluginUi| {
        ui.input(UiInput::InputAction {
            action: "MovementForward".into(),
            activation: "Press".into(),
        });
    };
    let heard = |owner: &str| {
        (
            owner.to_owned(),
            UiEvent::InputAction {
                action: "MovementForward".into(),
                activation: "Press".into(),
            },
        )
    };
    let mut ui = PluginUi::new();
    assert!(!ui.listen_input_actions("b.second", true));
    assert!(ui.listen_input_actions("b.second", true));
    assert!(!ui.listen_input_actions("a.first", true));
    fire(&mut ui);
    assert!(ui.take_events().is_empty(), "no window, no actions");
    ui.set_window_attached(true);
    fire(&mut ui);
    assert_eq!(ui.take_events(), [heard("b.second"), heard("a.first")]);
    assert!(ui.listen_input_actions("b.second", false));
    assert!(!ui.listen_input_actions("b.second", false));
    ui.release("a.first");
    assert!(!ui.hears_input_actions("a.first"));
    fire(&mut ui);
    assert!(ui.take_events().is_empty());
}

#[test]
fn the_theme_is_classic_until_a_window_tells_and_outlives_the_window() {
    // BufferedUiRegistryThemeTests: plugins read Classic on a
    // session no window told; the window publishes the saved theme, then
    // each pick; a pick of the same theme is no change; the theme outlives
    // the window, so a reattach answers the same one.
    use crate::theme::ThemeKind;
    let mut ui = PluginUi::new();
    assert_eq!(ui.theme(), ThemeKind::Classic);
    assert_eq!(ui.take_theme_change(), None);
    ui.set_window_attached(true);
    ui.input(UiInput::Theme(ThemeKind::Classic));
    assert_eq!(ui.take_theme_change(), None, "the same theme is no change");
    ui.input(UiInput::Theme(ThemeKind::Moss));
    assert_eq!(ui.theme(), ThemeKind::Moss);
    assert_eq!(ui.take_theme_change(), Some(ThemeKind::Moss));
    assert_eq!(ui.take_theme_change(), None, "told once");
    ui.set_window_attached(false);
    assert_eq!(ui.theme(), ThemeKind::Moss);
    assert_eq!(ui.take_theme_change(), None);
}

#[test]
fn a_plugins_file_icon_rides_with_its_windows_and_goes_with_it() {
    // OpenAC RetailUiRuntime.ResolvePluginFileIcon: the dock slot of
    // every window a plugin registers gets the plugin's icon.png; another
    // plugin's windows do not, and an unloaded plugin's icon is forgotten.
    let mut ui = PluginUi::new();
    let icon = PluginIcon {
        width: 64,
        height: 64,
        rgba: Arc::new(vec![7; 64 * 64 * 4]),
    };
    ui.set_file_icon("edwards.tank", Some(icon.clone()));
    let panel = status_view(&mut ui);
    ui.register_panel(
        &other(),
        PanelDescriptor::new("main", "Other"),
        "<panel/>",
        BindingSchema::new("Binding"),
    )
    .unwrap();
    let mut view = PluginUiView::new();
    view.apply_all(ui.snapshot());
    assert_eq!(view.panel(panel).unwrap().file_icon.as_ref(), Some(&icon));
    assert!(
        view.panels
            .iter()
            .find(|p| p.owner.id == "another.plugin")
            .unwrap()
            .file_icon
            .is_none()
    );
    ui.release("edwards.tank");
    status_view(&mut ui);
    assert!(ui.panels().all(|p| p.file_icon.is_none()));
}

// ── Dialogs and modal windows: OpenAC
// `tests/AcDream.App.Tests/Plugins/BufferedUiRegistryDialogTests.cs`,
// `tests/AcDream.App.Tests/UI/PluginDialogHostTests.cs` (what the session
// decides: queueing, answering once, owners closing, modals) and
// `tests/AcDream.Core.Tests/Plugins/ScopedUiRegistryDialogTests.cs`.

fn attached_with_two_windows() -> (PluginUi, u32, u32) {
    let mut ui = PluginUi::new();
    ui.set_window_attached(true);
    let a = ui
        .register_panel(
            &tank(),
            PanelDescriptor::new("main", "Main Window"),
            "<panel/>",
            BindingSchema::new("Binding"),
        )
        .unwrap();
    let b = ui
        .register_panel(
            &tank(),
            PanelDescriptor::new("edit", "Editor"),
            "<panel/>",
            BindingSchema::new("Binding"),
        )
        .unwrap();
    (ui, a, b)
}

fn answers(ui: &mut PluginUi) -> Vec<(u32, i32)> {
    ui.take_events()
        .into_iter()
        .filter_map(|(_, e)| match e {
            UiEvent::DialogAnswered { handle, button } => Some((handle, button)),
            _ => None,
        })
        .collect()
}

fn ask(ui: &mut PluginUi, owner_view: Option<&str>) -> u32 {
    ui.confirm(&tank(), "T", "M", Vec::new(), owner_view)
        .unwrap()
        .unwrap()
}

#[test]
fn without_a_window_nothing_is_queued() {
    // BufferedUiRegistryDialogTests.WithoutADialogHostNothingIsQueued,
    // ScopedUiRegistryDialogTests.AHostWithoutDialogsAnswersFalseAndNeverCallsBack
    let mut ui = PluginUi::new();
    assert_eq!(ui.confirm(&tank(), "T", "M", Vec::new(), None), Ok(None));
    assert_eq!(ui.dialogs().count(), 0);
    assert!(!ui.show_modal("edwards.tank", "main", None));
}

#[test]
fn bad_buttons_are_refused_even_without_a_window() {
    // BufferedUiRegistryDialogTests.BadButtonsAreRefusedEvenWithoutAHost,
    // ADialogWithoutButtonsShowsOkAndCancel
    let mut ui = PluginUi::new();
    let five: Vec<String> = (0..5).map(|i| i.to_string()).collect();
    assert!(ui.confirm(&tank(), "T", "M", five, None).is_err());
    assert!(
        ui.confirm(&tank(), "T", "M", vec!["Yes".into(), " ".into()], None)
            .is_err()
    );
    let (mut ui, _, _) = attached_with_two_windows();
    let handle = ask(&mut ui, None);
    let dialog = ui.dialogs().find(|d| d.handle == handle).unwrap();
    assert_eq!(dialog.buttons, ["OK", "Cancel"]);
}

#[test]
fn the_owner_is_the_plugins_shown_window_by_id_or_title() {
    // BufferedUiRegistryDialogTests.TheOwnerIsThePluginsMountedWindowByIdOrTitle,
    // AnUnknownOrForeignOwnerBlocksNothing,
    // PluginDialogHostTests.AHiddenOwnerOrNoOwnerCentresOnTheScreenAndBlocksNothing
    let (mut ui, a, b) = attached_with_two_windows();
    ask(&mut ui, Some("Main Window"));
    ask(&mut ui, Some("edit"));
    ui.confirm(&other(), "T", "M", Vec::new(), Some("main"))
        .unwrap();
    ask(&mut ui, Some("nope"));
    let owners: Vec<Option<u32>> = ui.dialogs().map(|d| d.owner_panel).collect();
    assert_eq!(owners, [Some(a), Some(b), None, None]);
    ui.show_panel("edwards.tank", "main", false);
    ask(&mut ui, Some("main"));
    assert_eq!(ui.dialogs().last().unwrap().owner_panel, None);
}

#[test]
fn a_dialog_is_answered_once_by_the_player_or_its_handle() {
    // PluginDialogHostTests.AButtonAnswersOnceAfterTheDialogIsGone,
    // DisposingTheHandleAnswersTheLastButtonOnce,
    // ScopedUiRegistryDialogTests.ThePluginWithdrawsADialogThroughItsHandle
    let (mut ui, _, _) = attached_with_two_windows();
    let yes = ui
        .confirm(
            &tank(),
            "T",
            "M",
            vec!["Yes".into(), "No".into(), "Later".into()],
            None,
        )
        .unwrap()
        .unwrap();
    ui.take_changes();
    ui.input(UiInput::DialogAnswered {
        handle: yes,
        button: 1,
    });
    ui.input(UiInput::DialogAnswered {
        handle: yes,
        button: 0,
    });
    assert_eq!(answers(&mut ui), [(yes, 1)]);
    assert_eq!(ui.take_changes(), [UiChange::DialogRemoved(yes)]);
    let withdrawn = ask(&mut ui, None);
    assert!(!ui.withdraw_dialog("another.plugin", withdrawn));
    assert!(ui.withdraw_dialog("edwards.tank", withdrawn));
    assert!(!ui.withdraw_dialog("edwards.tank", withdrawn));
    assert_eq!(answers(&mut ui), [(withdrawn, 1)]);
}

#[test]
fn an_owner_closing_or_going_answers_its_dialogs_with_the_last_button() {
    // PluginDialogHostTests.AnOwnerClosingAnswersEveryDialogQueuedOnIt,
    // AnOwnerRemovedAnswersItsDialog
    let (mut ui, a, b) = attached_with_two_windows();
    let first = ask(&mut ui, Some("main"));
    let second = ask(&mut ui, Some("main"));
    let elsewhere = ask(&mut ui, Some("edit"));
    ui.input(UiInput::PanelShown {
        panel: a,
        shown: false,
    });
    assert_eq!(answers(&mut ui), [(first, 1), (second, 1)]);
    assert!(ui.remove_panel("edwards.tank", b));
    assert_eq!(answers(&mut ui), [(elsewhere, 1)]);
}

#[test]
fn detaching_the_window_answers_every_dialog_and_ends_modals() {
    // PluginDialogHostTests.DisposingTheHostAnswersEverythingAndEndsModals
    let (mut ui, _, _) = attached_with_two_windows();
    let handle = ask(&mut ui, None);
    assert!(ui.show_modal("edwards.tank", "edit", Some("main")));
    ui.set_window_attached(false);
    assert_eq!(answers(&mut ui), [(handle, 1)]);
    assert_eq!(ui.modals().count(), 0);
}

#[test]
fn unloading_drops_the_plugins_dialogs() {
    // ScopedUiRegistryDialogTests.UnloadingAnswersAnOpenDialogWithItsLastButton
    // (the unloading plugin hears nothing more; the window's box goes)
    let (mut ui, _, _) = attached_with_two_windows();
    let handle = ask(&mut ui, None);
    ui.take_changes();
    ui.release("edwards.tank");
    assert!(ui.take_changes().contains(&UiChange::DialogRemoved(handle)));
    assert_eq!(ui.dialogs().count(), 0);
}

#[test]
fn a_modal_window_blocks_its_owner_until_it_hides() {
    // BufferedUiRegistryDialogTests.ShowModalShowsTheWindowAndBlocksItsOwnerUntilItHides,
    // ShowModalWithoutAnOwnerShowsButBlocksNothing,
    // ShowModalOfAnUnknownWindowOrWithoutAHostIsFalse,
    // PluginDialogHostTests.AModalPanelEndsWhenRemovedAndMovesToANewOwner,
    // TwoPanelsCannotBeShownModalOverEachOther
    let (mut ui, a, b) = attached_with_two_windows();
    ui.show_panel("edwards.tank", "edit", false);
    assert!(!ui.show_modal("edwards.tank", "nope", None));
    assert!(ui.show_modal("edwards.tank", "edit", Some("Main Window")));
    assert!(ui.is_view_visible("edwards.tank", "edit"));
    assert_eq!(ui.modals().collect::<Vec<_>>(), [(b, a)]);
    // The owner cannot then be shown modal over the window blocking it.
    assert!(ui.show_modal("edwards.tank", "main", Some("edit")));
    assert_eq!(ui.modals().collect::<Vec<_>>(), [(b, a)]);
    ui.show_panel("edwards.tank", "edit", false);
    assert_eq!(ui.modals().count(), 0);
    assert!(ui.show_modal("edwards.tank", "edit", None));
    assert_eq!(ui.modals().count(), 0);
    assert!(ui.show_modal("edwards.tank", "edit", Some("main")));
    assert!(ui.remove_panel("edwards.tank", a));
    assert_eq!(ui.modals().count(), 0);
}

#[test]
fn a_snapshot_carries_the_dialogs_and_modals() {
    let (mut ui, a, b) = attached_with_two_windows();
    let handle = ask(&mut ui, None);
    ui.show_modal("edwards.tank", "edit", Some("main"));
    let mut view = PluginUiView::new();
    view.apply_all(ui.snapshot());
    assert_eq!(view.dialogs.len(), 1);
    assert_eq!(view.dialogs[0].handle, handle);
    assert_eq!(view.modals, [(b, a)]);
}

/// A handler in a `<repeat>` row belongs to the row's item (OpenAC: the
/// delegate is the item's own): it is checked against the item type the
/// rows name and reaches the plugin with its rows
/// (`MarkupRepeatBuildTests.A_copy_reads_its_item_and_calls_back_on_its_item`).
#[test]
fn a_row_handler_is_checked_against_its_item_type() {
    let mut ui = PluginUi::new();
    let row = BindingSchema::new("Row")
        .with("Id", BindingKind::Text)
        .with("On", BindingKind::Bool)
        .with("SetOn", BindingKind::BoolAction);
    let section = BindingSchema::new("Section")
        .with("Id", BindingKind::Text)
        .with_items("Rows", row.clone());
    let schema = BindingSchema::new("B")
        .with_items("Rows", row)
        .with_items("Sections", section)
        .with("Go", BindingKind::Action);
    let id = ui
        .register_panel(
            &tank(),
            PanelDescriptor::new("p", "P"),
            "<panel layout=\"column\"><repeat items=\"{Rows}\" key=\"{Id}\">\
             <checkbox checked=\"{On}\" onchange=\"{SetOn}\"/></repeat></panel>",
            schema,
        )
        .unwrap();
    let act = |ui: &mut PluginUi, handler: &str, value: ActionValue| {
        ui.input(UiInput::Action {
            panel: id,
            handler: handler.into(),
            value,
        });
    };
    act(&mut ui, "Rows[1:b]/SetOn", ActionValue::Bool(true));
    act(
        &mut ui,
        "Sections[0:s]/Rows[2]/SetOn",
        ActionValue::Bool(false),
    );
    // Not on the item type, a list the scope does not have, the wrong
    // argument, or a window handler inside a row: dropped.
    act(&mut ui, "Rows[1:b]/Go", ActionValue::None);
    act(&mut ui, "Names[1]/SetOn", ActionValue::Bool(true));
    act(&mut ui, "Rows[1]/SetOn", ActionValue::Int(1));
    act(&mut ui, "Rows[0]/Rows[0]/SetOn", ActionValue::Bool(true));
    assert_eq!(
        ui.take_events()
            .into_iter()
            .map(|(_, e)| match e {
                UiEvent::Action { handler, .. } => handler,
                other => panic!("{other:?}"),
            })
            .collect::<Vec<_>>(),
        vec![
            "Rows[1:b]/SetOn".to_owned(),
            "Sections[0:s]/Rows[2]/SetOn".to_owned()
        ]
    );
    assert_eq!(
        ui.handler_kind(id, "Sections[0:s]/Rows[2]/SetOn"),
        Some(BindingKind::BoolAction)
    );
}

/// OpenAC `BufferedUiRegistryTests.NamedControlLookupsSkipRepeatRowsButFindTheControlsAfterThem`:
/// a name in a `<repeat>` template is on every row, so the by-name calls
/// do not reach it; a control after the repeat is still found.
#[test]
fn named_control_lookups_skip_repeat_rows_but_find_the_controls_after_them() {
    let mut ui = PluginUi::new();
    let schema = BindingSchema::new("RepeatBinding").with_items(
        "Rows",
        BindingSchema::new("RepeatRow").with("Id", BindingKind::Text),
    );
    ui.register_panel(
        &tank(),
        PanelDescriptor::new("meta", "Status View"),
        "<panel x=\"0\" y=\"0\" w=\"300\" h=\"400\" layout=\"column\">\
         <repeat items=\"{Rows}\" key=\"{Id}\"><label name=\"row\" h=\"20\" text=\"{Id}\"/></repeat>\
         <label name=\"after\" h=\"20\" text=\"after\"/></panel>",
        schema,
    )
    .unwrap();
    assert!(!ui.control_exists("edwards.tank", "Status View", "row"));
    assert!(!ui.set_control_visible("edwards.tank", "Status View", "row", false));
    assert!(!ui.set_control_label("edwards.tank", "Status View", "row", "x"));
    assert!(ui.control_exists("edwards.tank", "Status View", "after"));
}

// OpenAC's image handles only ever increase. A plugin reloaded within one
// window's frame (its old images removed and its new ones added in the
// same batch of changes) must not get its first handle again, or a window
// keeping textures by plugin and handle would draw the old pixels.
#[test]
fn a_reloaded_plugin_never_gets_a_handle_again_so_a_window_draws_its_new_image() {
    let mut ui = PluginUi::new();
    let mut view = PluginUiView::new();
    let (old, ..) = ui.image_rgba("edwards.tank", "art", 1, 1, vec![1, 2, 3, 4]);
    view.apply_all(ui.take_changes());
    ui.release("edwards.tank");
    let (new, ..) = ui.image_rgba("edwards.tank", "art", 1, 1, vec![9, 9, 9, 9]);
    assert_ne!(new, 0);
    assert!(new > old, "{new} after {old}");
    view.apply_all(ui.take_changes());
    assert!(view.image("edwards.tank", old).is_none());
    let image = view.image("edwards.tank", new).unwrap();
    assert!(matches!(&image.source, ImageSource::Rgba(p) if p.as_slice() == [9, 9, 9, 9]));
}
