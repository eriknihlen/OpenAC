//! The Configure Keyboard screen.
//!
//! Ported from OpenAC `tests/AcDream.App.Tests/UI/Layout/KeyboardConfigControllerTests.cs`
//! (rows from the action map, seeding, capture, erase, conflicts, the
//! screen buttons, profiles) over a hand-built layout with the screen's
//! element ids; the authored layout is the real-dat test's.

mod common;

use std::collections::HashMap;
use std::sync::Arc;

use ac_ui::draw::Color;
use ac_ui::element_info::{ElementInfo, TemplateEntry};
use ac_ui::event::{Key, Modifiers};
use ac_ui::host::UiHost;
use ac_ui::input::action_map::{
    RetailActionClass, RetailActionMapRow, RetailActionMapSnapshot, RetailKeyChord,
};
use ac_ui::input::key_names::RetailKeyNames;
use ac_ui::input::unmapped::RetailUnmappedKeyBindings;
use ac_ui::input::{ActivationType, Binding, InputAction, InputScope, KeyBindings, KeyChord};
use ac_ui::panels::PanelOutput;
use ac_ui::panels::keyboard_config::{
    self as kb, CaptureReply, KeyboardConfigController, KeyboardConfigOutput, KeyboardConfigSources,
};
use ac_ui::root::{ElementMessage, UiOutput, UiRoot};
use ac_ui::strings::compute_hash;
use ac_ui::tree::{Element, ElementId};
use ac_ui::widgets::{Button, ListBox, Text};
use common::panels::{add_std_button, add_std_text};
use common::{at, panel};

const LAYOUT: u32 = kb::LAYOUT_ID;
const HEADER_TEMPLATE: u32 = 0x1000_002E;
const ROW_TEMPLATE: u32 = 0x1000_002F;

fn row(map: u32, action: u32, class: RetailActionClass) -> RetailActionMapRow {
    row_with(map, action, class, &[])
}

fn row_with(
    map: u32,
    action: u32,
    class: RetailActionClass,
    defaults: &[RetailKeyChord],
) -> RetailActionMapRow {
    RetailActionMapRow {
        input_map: map,
        action_id: action,
        class,
        label_hash: 0xDE00_0000 | (action & 0x00FF_FFFF),
        tooltip_hash: 0,
        default_bindings: defaults.to_vec(),
    }
}

fn snapshot(rows: Vec<RetailActionMapRow>) -> RetailActionMapSnapshot {
    RetailActionMapSnapshot {
        rows,
        conflicting_input_maps: HashMap::new(),
    }
}

fn key(k: Key) -> KeyChord {
    KeyChord::key(k, Modifiers::NONE)
}

fn bind(bindings: &mut KeyBindings, chord: KeyChord, action: InputAction) {
    bindings.add(Binding::new(chord, action));
}

/// OpenAC's fake template table.
fn template(key: &str, v: &HashMap<u32, String>) -> Option<String> {
    let var = |name: &str| v.get(&compute_hash(name)).cloned().unwrap_or_default();
    Some(match key {
        "ID_ActionKeyMap_ButtonLabel" => var("LABEL"),
        "ID_ActionKeyMap_TT_ExistingBinding" => format!("({}) existing binding", var("VALUE")),
        "ID_ActionKeyMap_TT_NewBinding" => "new binding".to_owned(),
        "ID_ActionKeyMap_NonUserBindableBinding" => format!("cannot overwrite {}", var("KEY")),
        "ID_ActionKeyMap_OverwriteExistingBinding" => {
            format!("overwrite {} {}", var("KEY"), var("ACTION"))
        }
        "ID_ActionKeyMap_Binding" => format!("{} ({})", var("ACTION"), var("KEY")),
        "ID_ActionKeyMap_OverwriteExistingBindings" => {
            format!("overwrite {}\n{}", var("KEY"), var("BINDINGS"))
        }
        _ => return None,
    })
}

fn synthetic_string(_table: u32, hash: u32) -> Option<String> {
    (hash & 0xFF00_0000 == 0xDE00_0000).then(|| format!("Action {:X}", hash & 0x00FF_FFFF))
}

fn build_template(ui: &mut UiRoot, layout: u32, element: u32) -> Option<ElementId> {
    if layout != LAYOUT {
        return None;
    }
    match element {
        HEADER_TEMPLATE => {
            let mut e = Element::at(0.0, 0.0, 300.0, 20.0);
            Text::configure(&mut e);
            Some(ui.insert(e, Text::default()))
        }
        ROW_TEMPLATE => {
            let row = ui.insert(
                Element::at(0.0, 0.0, 500.0, 20.0),
                ac_ui::widgets::Panel::default(),
            );
            let mut ids = HashMap::new();
            for (i, id) in kb::KEY_BUTTON_IDS.iter().enumerate() {
                let b = add_std_button(ui, &mut ids, row, *id);
                if let Some(e) = ui.element_mut(b) {
                    e.left = 270.0 + 70.0 * i as f32;
                }
            }
            Some(row)
        }
        _ => None,
    }
}

struct Screen {
    host: UiHost,
    ids: HashMap<u32, ElementId>,
    outputs: Vec<KeyboardConfigOutput>,
}

fn screen(snapshot: RetailActionMapSnapshot, bindings: KeyBindings) -> Screen {
    screen_with(snapshot, bindings, RetailUnmappedKeyBindings::new(), None)
}

fn screen_with(
    snapshot: RetailActionMapSnapshot,
    bindings: KeyBindings,
    unmapped: RetailUnmappedKeyBindings,
    filename: Option<&str>,
) -> Screen {
    let mut host = UiHost::new();
    host.root.set_size(1024.0, 768.0);
    let ui = &mut host.root;
    let r = ui.root();
    let mut e = at(0.0, 0.0, 600.0, 500.0);
    e.dat_element_id = kb::WINDOW_ROOT_ID;
    let root = panel(ui, r, e);
    let mut ids = HashMap::new();
    for (page_id, _) in kb::PAGES {
        let mut e = at(0.0, 30.0, 600.0, 400.0);
        e.dat_element_id = page_id;
        let page = panel(ui, root, e);
        let mut e = Element::at(0.0, 0.0, 580.0, 380.0);
        e.dat_element_id = kb::LIST_BOX_ID;
        let mut list = ListBox::new(Arc::new(ElementInfo {
            id: kb::LIST_BOX_ID,
            kind: 5,
            ..ElementInfo::default()
        }));
        list.templates = vec![
            TemplateEntry {
                layout: LAYOUT,
                element: HEADER_TEMPLATE,
            },
            TemplateEntry {
                layout: LAYOUT,
                element: ROW_TEMPLATE,
            },
        ];
        ui.add(page, e, list);
    }
    for id in [
        kb::LOAD_BUTTON_ID,
        kb::SAVE_AS_BUTTON_ID,
        kb::DEFAULTS_BUTTON_ID,
        kb::REVERT_BUTTON_ID,
        kb::OK_BUTTON_ID,
        kb::CANCEL_BUTTON_ID,
    ] {
        add_std_button(ui, &mut ids, root, id);
    }
    add_std_text(ui, &mut ids, root, kb::FILENAME_LABEL_ID);
    ui.register_window("kb", root, None, 0).unwrap();
    let mut rows = build_template;
    let c = KeyboardConfigController::bind(
        ui,
        root,
        KeyboardConfigSources {
            snapshot,
            rows: &mut rows,
            resolve_string: &synthetic_string,
            templates: Box::new(template),
            names: RetailKeyNames::new(Box::new(|_, _| None), None),
            template_font: &|_, _| None,
            bindings,
            unmapped,
            filename: filename.map(str::to_owned),
        },
    )
    .expect("binds");
    host.attach_controller("kb", Box::new(c)).unwrap();
    let mut s = Screen {
        host,
        ids,
        outputs: Vec::new(),
    };
    s.collect();
    s.outputs.clear();
    s
}

impl Screen {
    fn with<R>(&mut self, f: impl FnOnce(&mut KeyboardConfigController, &mut UiRoot) -> R) -> R {
        let r = self
            .host
            .with_controller::<KeyboardConfigController, _>("kb", f)
            .unwrap();
        self.collect();
        r
    }

    fn collect(&mut self) {
        for o in self.host.take_outputs() {
            if let UiOutput::Panel(PanelOutput::KeyboardConfig(k)) = o {
                self.outputs.push(k);
            }
        }
    }

    fn take(&mut self) -> Vec<KeyboardConfigOutput> {
        std::mem::take(&mut self.outputs)
    }

    fn rows(&mut self) -> Vec<kb::KeyRow> {
        self.with(|c, _| c.rows().to_vec())
    }

    fn row(&mut self, action: u32) -> kb::KeyRow {
        self.rows()
            .into_iter()
            .find(|r| r.action_id == action)
            .expect("the row")
    }

    fn message(&mut self, element: ElementId, message: ElementMessage) {
        self.with(|c, ui| assert!(c.handle(ui, element, &message)));
    }

    fn click_key(&mut self, action: u32, slot: usize) {
        let b = self.row(action).key_buttons[slot];
        self.message(b, ElementMessage::Clicked { x: 0, y: 0 });
    }

    fn right_click_key(&mut self, action: u32, slot: usize) {
        let b = self.row(action).key_buttons[slot];
        self.message(b, ElementMessage::RightClicked);
    }

    fn click(&mut self, id: u32) {
        let b = self.ids[&id];
        self.message(b, ElementMessage::Clicked { x: 0, y: 0 });
    }

    fn capture(&mut self, chord: Option<KeyChord>) -> CaptureReply {
        self.with(|c, ui| c.captured(ui, chord))
    }

    fn answer(&mut self, accept: bool) {
        self.with(|c, ui| c.overwrite_answered(ui, accept));
    }

    fn label(&self, button: ElementId) -> Option<String> {
        self.host
            .root
            .widget::<Button>(button)
            .unwrap()
            .label
            .clone()
    }

    fn tooltip(&self, button: ElementId) -> Option<String> {
        self.host
            .root
            .widget::<Button>(button)
            .unwrap()
            .tooltip_text
            .clone()
    }

    fn bindings(&mut self) -> KeyBindings {
        self.with(|c, _| c.bindings().clone())
    }

    fn applied(&self) -> usize {
        self.outputs
            .iter()
            .filter(|o| matches!(o, KeyboardConfigOutput::Apply { .. }))
            .count()
    }
}

#[test]
fn bind_builds_one_row_per_snapshot_row_on_its_class_page() {
    let mut s = screen(
        snapshot(vec![
            row(0x4, 0x29, RetailActionClass::Movement),
            row(0x4, 0x2A, RetailActionClass::Movement),
            row(0x5, 0x33, RetailActionClass::Camera),
        ]),
        KeyBindings::new(),
    );
    assert_eq!(s.rows().len(), 3);
    // Each page's list: a header per input map, then its rows.
    let ui = &s.host.root;
    let list_rows = |page: u32| {
        let page = ui.find_descendant(ui.root(), page).unwrap();
        let list = ui.find_descendant(page, kb::LIST_BOX_ID).unwrap();
        ui.widget::<ListBox>(list).unwrap().item_count(ui)
    };
    assert_eq!(list_rows(0x1000_049D), 3);
    assert_eq!(list_rows(0x1000_049F), 2);
    assert_eq!(list_rows(0x1000_04A5), 0);
}

#[test]
fn bind_maps_every_retail_action_row_and_seeds_it_from_the_live_bindings() {
    let mut b = KeyBindings::new();
    bind(&mut b, key(Key::W), InputAction::MovementForward);
    bind(&mut b, key(Key::Up), InputAction::MovementForward);
    bind(&mut b, key(Key::A), InputAction::EmoteBowDeep);
    let mut s = screen(
        snapshot(vec![
            row(0x4, 0x29, RetailActionClass::Movement),
            row(0x1000_0006, 0x1000_00A0, RetailActionClass::Emote),
        ]),
        b,
    );
    let forward = s.row(0x29);
    assert_eq!(forward.mapped, Some(InputAction::MovementForward));
    assert_eq!(forward.current, [key(Key::W), key(Key::Up)]);
    let bow = s.row(0x1000_00A0);
    assert_eq!(bow.mapped, Some(InputAction::EmoteBowDeep));
    assert_eq!(bow.current, [key(Key::A)]);
    // Every retail row's caption is in the enabled colour.
    for r in s.rows() {
        let t = s.host.root.widget::<Text>(r.caption).unwrap();
        assert_eq!(t.default_color, Color::WHITE);
    }
}

#[test]
fn a_key_button_captures_and_applies_a_new_binding_live() {
    let mut s = screen(
        snapshot(vec![row(0x4, 0x29, RetailActionClass::Movement)]),
        KeyBindings::new(),
    );
    s.click_key(0x29, 0);
    assert_eq!(
        s.take(),
        [KeyboardConfigOutput::BeginCapture {
            action: "Action 29".into()
        }]
    );
    assert_eq!(s.capture(Some(key(Key::W))), CaptureReply::Ended);
    let r = s.row(0x29);
    assert_eq!(r.current, [key(Key::W)]);
    let live: Vec<Binding> = s
        .bindings()
        .for_action(InputAction::MovementForward)
        .copied()
        .collect();
    assert_eq!(
        live,
        [Binding::new(key(Key::W), InputAction::MovementForward)]
    );
    assert_eq!(live[0].activation, ActivationType::Press);
    assert_eq!(live[0].scope, InputScope::Game);
    assert_eq!(s.applied(), 1);
    assert_eq!(s.label(r.key_buttons[0]).as_deref(), Some("W"));
}

#[test]
fn escape_cancels_the_capture_and_leaves_the_binding() {
    let mut b = KeyBindings::new();
    bind(&mut b, key(Key::W), InputAction::MovementForward);
    let mut s = screen(
        snapshot(vec![row(0x4, 0x29, RetailActionClass::Movement)]),
        b,
    );
    s.click_key(0x29, 0);
    s.take();
    assert_eq!(s.capture(None), CaptureReply::Ended);
    assert_eq!(s.row(0x29).current, [key(Key::W)]);
    assert_eq!(s.applied(), 0);
    // Nothing is captured any more.
    assert_eq!(s.capture(Some(key(Key::A))), CaptureReply::NotCapturing);
}

#[test]
fn a_refused_instruction_dialog_does_not_arm_the_capture() {
    let mut s = screen(
        snapshot(vec![row(0x4, 0x29, RetailActionClass::Movement)]),
        KeyBindings::new(),
    );
    s.click_key(0x29, 0);
    s.with(|c, _| c.capture_refused());
    assert_eq!(s.capture(Some(key(Key::W))), CaptureReply::NotCapturing);
    assert!(s.row(0x29).current.is_empty());
}

#[test]
fn right_click_erases_that_slot_and_an_empty_slot_is_a_no_op() {
    let mut b = KeyBindings::new();
    bind(&mut b, key(Key::W), InputAction::MovementForward);
    bind(&mut b, key(Key::Up), InputAction::MovementForward);
    let mut s = screen(
        snapshot(vec![row(0x4, 0x29, RetailActionClass::Movement)]),
        b,
    );
    s.right_click_key(0x29, 0);
    assert_eq!(s.row(0x29).current, [key(Key::Up)]);
    s.take();
    s.right_click_key(0x29, 2);
    assert_eq!(s.row(0x29).current, [key(Key::Up)]);
    assert_eq!(s.applied(), 0);
}

#[test]
fn a_capture_past_the_dense_tail_appends_at_the_first_free_button() {
    let mut s = screen(
        snapshot(vec![row(0x4, 0x29, RetailActionClass::Movement)]),
        KeyBindings::new(),
    );
    s.click_key(0x29, 2);
    s.capture(Some(key(Key::W)));
    let r = s.row(0x29);
    assert_eq!(s.label(r.key_buttons[0]).as_deref(), Some("W"));
    assert_eq!(s.label(r.key_buttons[1]), None);
    assert_eq!(s.label(r.key_buttons[2]), None);
    assert_eq!(r.current, [key(Key::W)]);
}

#[test]
fn a_conflict_with_another_row_asks_and_a_yes_reassigns() {
    let mut b = KeyBindings::new();
    bind(&mut b, key(Key::A), InputAction::MovementBackup);
    let mut s = screen(
        snapshot(vec![
            row(0x4, 0x29, RetailActionClass::Movement),
            row(0x4, 0x2A, RetailActionClass::Movement),
        ]),
        b,
    );
    s.click_key(0x29, 0);
    s.take();
    s.capture(Some(key(Key::A)));
    assert_eq!(
        s.take(),
        [KeyboardConfigOutput::ConfirmOverwrite(
            "overwrite A Action 2A".into()
        )]
    );
    assert!(!s.row(0x29).current.contains(&key(Key::A)));
    assert!(s.row(0x2A).current.contains(&key(Key::A)));
    s.answer(true);
    assert!(s.row(0x29).current.contains(&key(Key::A)));
    assert!(!s.row(0x2A).current.contains(&key(Key::A)));
}

#[test]
fn a_no_to_the_overwrite_leaves_both_rows() {
    let mut b = KeyBindings::new();
    bind(&mut b, key(Key::W), InputAction::MovementForward);
    bind(&mut b, key(Key::A), InputAction::MovementBackup);
    let mut s = screen(
        snapshot(vec![
            row(0x4, 0x29, RetailActionClass::Movement),
            row(0x4, 0x2A, RetailActionClass::Movement),
        ]),
        b,
    );
    s.click_key(0x29, 0);
    s.capture(Some(key(Key::A)));
    s.answer(false);
    assert_eq!(s.row(0x29).current, [key(Key::W)]);
    assert_eq!(s.row(0x2A).current, [key(Key::A)]);
}

#[test]
fn a_bare_shift_held_by_walk_mode_always_asks() {
    let shift = key(Key::ShiftLeft);
    let mut b = KeyBindings::new();
    b.add(Binding::new(shift, InputAction::MovementWalkMode).activation(ActivationType::Hold));
    let mut s = screen(
        snapshot(vec![
            row(0x4, 0x29, RetailActionClass::Movement),
            row(0x4, 0x32, RetailActionClass::Movement),
        ]),
        b,
    );
    s.click_key(0x29, 0);
    s.take();
    s.capture(Some(shift));
    assert!(matches!(
        s.take().as_slice(),
        [KeyboardConfigOutput::ConfirmOverwrite(_)]
    ));
    assert!(!s.row(0x29).current.contains(&shift));
    assert_eq!(s.row(0x32).current, [shift]);
}

#[test]
fn several_conflicting_rows_use_the_plural_binding_list() {
    let mut b = KeyBindings::new();
    bind(&mut b, key(Key::A), InputAction::MovementBackup);
    bind(&mut b, key(Key::A), InputAction::MovementStop);
    let mut s = screen(
        snapshot(vec![
            row(0x4, 0x29, RetailActionClass::Movement),
            row(0x4, 0x2A, RetailActionClass::Movement),
            row(0x4, 0x2B, RetailActionClass::Movement),
        ]),
        b,
    );
    s.click_key(0x29, 0);
    s.take();
    s.capture(Some(key(Key::A)));
    assert_eq!(
        s.take(),
        [KeyboardConfigOutput::ConfirmOverwrite(
            "overwrite A\nAction 2A (A)\nAction 2B (A)".into()
        )]
    );
}

#[test]
fn the_buttons_use_the_existing_and_new_binding_tooltips() {
    let mut b = KeyBindings::new();
    bind(&mut b, key(Key::W), InputAction::MovementForward);
    let mut s = screen(
        snapshot(vec![row(0x4, 0x29, RetailActionClass::Movement)]),
        b,
    );
    let r = s.row(0x29);
    assert_eq!(
        s.tooltip(r.key_buttons[0]).as_deref(),
        Some("(W) existing binding")
    );
    assert_eq!(s.tooltip(r.key_buttons[1]).as_deref(), Some("new binding"));
    assert_eq!(s.tooltip(r.key_buttons[2]).as_deref(), Some("new binding"));
    s.right_click_key(0x29, 0);
    assert_eq!(s.tooltip(r.key_buttons[0]).as_deref(), Some("new binding"));
}

#[test]
fn a_chord_already_in_the_row_is_a_no_op() {
    let mut b = KeyBindings::new();
    bind(&mut b, key(Key::W), InputAction::MovementForward);
    bind(&mut b, key(Key::Up), InputAction::MovementForward);
    bind(&mut b, key(Key::W), InputAction::AcdreamToggleAudioMute);
    let mut s = screen(
        snapshot(vec![row(0x4, 0x29, RetailActionClass::Movement)]),
        b,
    );
    s.click_key(0x29, 2);
    s.take();
    s.capture(Some(key(Key::W)));
    assert_eq!(s.row(0x29).current, [key(Key::W), key(Key::Up)]);
    assert!(s.take().is_empty());
}

#[test]
fn the_left_or_right_mouse_button_keeps_the_capture_armed() {
    use ac_ui::input::chord::mouse;
    let mut s = screen(
        snapshot(vec![row(0x4, 0x29, RetailActionClass::Movement)]),
        KeyBindings::new(),
    );
    s.click_key(0x29, 0);
    assert_eq!(
        s.capture(Some(KeyChord::mouse(mouse::LEFT, Modifiers::NONE))),
        CaptureReply::StillCapturing
    );
    assert_eq!(
        s.capture(Some(KeyChord::mouse(mouse::RIGHT, Modifiers::NONE))),
        CaptureReply::StillCapturing
    );
    assert!(s.row(0x29).current.is_empty());
    assert_eq!(s.capture(Some(key(Key::W))), CaptureReply::Ended);
    assert_eq!(s.row(0x29).current, [key(Key::W)]);
}

#[test]
fn a_shared_chord_across_non_conflicting_combat_maps_keeps_both() {
    const MELEE: u32 = 0x1000_0003;
    const MISSILE: u32 = 0x1000_0004;
    let mut snap = snapshot(vec![
        row(MELEE, 0x1000_005D, RetailActionClass::Combat),
        row(MISSILE, 0x1000_00F1, RetailActionClass::Combat),
    ]);
    snap.conflicting_input_maps
        .insert(MELEE, [MELEE].into_iter().collect());
    snap.conflicting_input_maps
        .insert(MISSILE, [MISSILE].into_iter().collect());
    let mut b = KeyBindings::new();
    b.add(Binding::new(key(Key::A), InputAction::CombatAimLow).scope(InputScope::MissileCombat));
    let mut s = screen(snap, b);
    s.click_key(0x1000_005D, 0);
    s.capture(Some(key(Key::A)));
    assert!(
        !s.take()
            .iter()
            .any(|o| matches!(o, KeyboardConfigOutput::ConfirmOverwrite(_)))
    );
    assert!(s.row(0x1000_005D).current.contains(&key(Key::A)));
    assert!(s.row(0x1000_00F1).current.contains(&key(Key::A)));
}

#[test]
fn a_shared_chord_across_dat_conflicting_maps_still_asks() {
    const MOVEMENT: u32 = 0x4;
    const UI: u32 = 0x1000_0009;
    let mut snap = snapshot(vec![
        row(MOVEMENT, 0x29, RetailActionClass::Movement),
        row(UI, 0x1000_0019, RetailActionClass::Ui),
    ]);
    snap.conflicting_input_maps
        .insert(MOVEMENT, [MOVEMENT, UI].into_iter().collect());
    let mut b = KeyBindings::new();
    bind(&mut b, key(Key::A), InputAction::ToggleInventoryPanel);
    let mut s = screen(snap, b);
    s.click_key(0x29, 0);
    s.take();
    s.capture(Some(key(Key::A)));
    assert!(matches!(
        s.take().as_slice(),
        [KeyboardConfigOutput::ConfirmOverwrite(_)]
    ));
    assert!(!s.row(0x29).current.contains(&key(Key::A)));
}

#[test]
fn a_key_the_client_keeps_for_itself_is_refused_with_a_message() {
    let mute = KeyChord::key(Key::M, Modifiers::CTRL);
    let mut b = KeyBindings::new();
    bind(&mut b, mute, InputAction::AcdreamToggleAudioMute);
    bind(&mut b, mute, InputAction::MovementBackup);
    let mut s = screen(
        snapshot(vec![
            row(0x4, 0x29, RetailActionClass::Movement),
            row(0x4, 0x2A, RetailActionClass::Movement),
        ]),
        b,
    );
    s.click_key(0x29, 0);
    s.take();
    s.capture(Some(mute));
    let out = s.take();
    // The client's own binding wins over the conflicting row.
    assert!(
        matches!(out.as_slice(), [KeyboardConfigOutput::ShowMessage(m)] if m.contains("cannot overwrite"))
    );
    assert!(!s.row(0x29).current.contains(&mute));
    let kept: Vec<Binding> = s
        .bindings()
        .for_action(InputAction::AcdreamToggleAudioMute)
        .copied()
        .collect();
    assert_eq!(kept.len(), 1);
}

#[test]
fn ok_commits_saves_and_closes() {
    let mut s = screen(
        snapshot(vec![row(0x4, 0x29, RetailActionClass::Movement)]),
        KeyBindings::new(),
    );
    s.click_key(0x29, 0);
    s.capture(Some(key(Key::W)));
    assert!(s.row(0x29).changed());
    s.take();
    s.click(kb::OK_BUTTON_ID);
    assert!(!s.row(0x29).changed());
    assert_eq!(
        s.take(),
        [KeyboardConfigOutput::Save, KeyboardConfigOutput::Close]
    );
    // Unchanged: OK only closes.
    s.click(kb::OK_BUTTON_ID);
    assert_eq!(s.take(), [KeyboardConfigOutput::Close]);
}

#[test]
fn a_loaded_profile_replaces_the_rows_and_the_revert_baseline() {
    let mut b = KeyBindings::new();
    bind(&mut b, key(Key::W), InputAction::MovementForward);
    let mut s = screen_with(
        snapshot(vec![row(0x4, 0x29, RetailActionClass::Movement)]),
        b,
        RetailUnmappedKeyBindings::new(),
        Some("openac.keymap"),
    );
    s.click(kb::LOAD_BUTTON_ID);
    assert_eq!(s.take(), [KeyboardConfigOutput::OpenLoad]);
    let mut loaded = KeyBindings::new();
    bind(&mut loaded, key(Key::Up), InputAction::MovementForward);
    s.with(|c, ui| {
        c.reload(ui, loaded, RetailUnmappedKeyBindings::new());
        c.set_filename(ui, "friends.keymap");
    });
    let r = s.row(0x29);
    assert_eq!(r.current, [key(Key::Up)]);
    assert_eq!(r.saved, [key(Key::Up)]);
    assert!(!r.changed());
    let name = s.with(|c, ui| c.filename_text(ui));
    assert_eq!(name.as_deref(), Some("friends.keymap"));
}

#[test]
fn save_as_asks_for_a_name_and_the_filename_changes_after() {
    let mut s = screen_with(
        snapshot(vec![row(0x4, 0x29, RetailActionClass::Movement)]),
        KeyBindings::new(),
        RetailUnmappedKeyBindings::new(),
        Some("openac.keymap"),
    );
    s.click(kb::SAVE_AS_BUTTON_ID);
    assert_eq!(s.take(), [KeyboardConfigOutput::OpenSaveAs]);
    let name = s.with(|c, ui| c.filename_text(ui));
    assert_eq!(name.as_deref(), Some("openac.keymap"));
    s.with(|c, ui| c.set_filename(ui, "alternate.keymap"));
    let name = s.with(|c, ui| c.filename_text(ui));
    assert_eq!(name.as_deref(), Some("alternate.keymap"));
}

#[test]
fn revert_is_enabled_exactly_while_the_working_map_differs() {
    let mut b = KeyBindings::new();
    bind(&mut b, key(Key::W), InputAction::MovementForward);
    let mut s = screen(
        snapshot(vec![row(0x4, 0x29, RetailActionClass::Movement)]),
        b,
    );
    let revert = s.ids[&kb::REVERT_BUTTON_ID];
    let enabled = |s: &Screen| s.host.root.element(revert).unwrap().enabled;
    assert!(!enabled(&s));
    s.click_key(0x29, 0);
    s.capture(Some(key(Key::Up)));
    assert!(enabled(&s));
    s.click(kb::REVERT_BUTTON_ID);
    assert_eq!(s.row(0x29).current, [key(Key::W)]);
    assert!(!enabled(&s));
    // Defaults differ from the saved map here (no dat default).
    s.click(kb::DEFAULTS_BUTTON_ID);
    assert!(enabled(&s));
    s.click(kb::OK_BUTTON_ID);
    assert!(!enabled(&s));
}

#[test]
fn cancel_reverts_and_closes_keeping_the_activation_and_scope() {
    let mut b = KeyBindings::new();
    b.add(
        Binding::new(key(Key::W), InputAction::MovementWalkMode).activation(ActivationType::Hold),
    );
    let mut s = screen(
        snapshot(vec![row(0x4, 0x32, RetailActionClass::Movement)]),
        b,
    );
    s.click_key(0x32, 0);
    s.capture(Some(key(Key::Up)));
    s.take();
    s.click(kb::CANCEL_BUTTON_ID);
    assert_eq!(s.row(0x32).current, [key(Key::W)]);
    let out = s.take();
    assert!(matches!(out.last(), Some(KeyboardConfigOutput::Close)));
    assert!(!out.contains(&KeyboardConfigOutput::Save));
    let live: Vec<Binding> = s
        .bindings()
        .for_action(InputAction::MovementWalkMode)
        .copied()
        .collect();
    assert_eq!(live.len(), 1);
    assert_eq!(live[0].chord, key(Key::W));
    assert_eq!(live[0].activation, ActivationType::Hold);
}

#[test]
fn defaults_restore_the_dat_default_live_keeping_activation_and_scope() {
    let mut b = KeyBindings::new();
    b.add(
        Binding::new(key(Key::Up), InputAction::CombatLowAttack)
            .activation(ActivationType::Hold)
            .scope(InputScope::MeleeCombat),
    );
    let mut s = screen(
        snapshot(vec![row_with(
            0x1000_0003,
            0x1000_005D,
            RetailActionClass::Combat,
            &[RetailKeyChord {
                scan: 0xD3,
                device: 0,
                modifier: 0,
                activation: 3,
            }],
        )]),
        b,
    );
    s.click(kb::DEFAULTS_BUTTON_ID);
    let r = s.row(0x1000_005D);
    assert_eq!(r.current, [key(Key::Delete)]);
    assert!(r.changed());
    let live: Vec<Binding> = s
        .bindings()
        .for_action(InputAction::CombatLowAttack)
        .copied()
        .collect();
    assert_eq!(live.len(), 1);
    assert_eq!(live[0].chord, key(Key::Delete));
    assert_eq!(live[0].activation, ActivationType::Hold);
    assert_eq!(live[0].scope, InputScope::MeleeCombat);
}

#[test]
fn camera_maps_5_and_6_are_independent_rows() {
    let mut b = KeyBindings::new();
    bind(&mut b, key(Key::A), InputAction::CameraRotateLeft);
    b.add(
        Binding::new(key(Key::Up), InputAction::CameraAlternateRotateLeft)
            .scope(InputScope::Camera),
    );
    let mut s = screen(
        snapshot(vec![
            row(0x5, 0x35, RetailActionClass::Camera),
            row(0x6, 0x35, RetailActionClass::Camera),
        ]),
        b,
    );
    let rows = s.rows();
    let five = rows.iter().find(|r| r.input_map == 0x5).unwrap().clone();
    let six = rows.iter().find(|r| r.input_map == 0x6).unwrap().clone();
    assert_eq!(five.mapped, Some(InputAction::CameraRotateLeft));
    assert_eq!(six.mapped, Some(InputAction::CameraAlternateRotateLeft));
    s.message(five.key_buttons[0], ElementMessage::Clicked { x: 0, y: 0 });
    s.capture(Some(key(Key::W)));
    let rows = s.rows();
    assert!(rows[0].current.contains(&key(Key::W)) || rows[1].current.contains(&key(Key::W)));
    let six_now = rows.iter().find(|r| r.input_map == 0x6).unwrap();
    assert_eq!(six_now.current, six.current);
}

#[test]
fn an_unmapped_row_keeps_its_keys_in_the_unmapped_bindings() {
    // A retail action with no client action (an input map the client has no
    // identities in) seeds from the dat default and saves apart.
    let mut s = screen(
        snapshot(vec![row_with(
            0x1000_000A,
            0x7777_7777,
            RetailActionClass::Ui,
            &[RetailKeyChord {
                scan: 0x11,
                device: 0,
                modifier: 0,
                activation: 3,
            }],
        )]),
        KeyBindings::new(),
    );
    let r = s.row(0x7777_7777);
    assert_eq!(r.mapped, None);
    assert_eq!(r.current, [key(Key::W)]);
    let caption = s.host.root.widget::<Text>(r.caption).unwrap();
    assert_eq!(
        caption.default_color,
        ac_ui::panels::options::STORE_ONLY_CAPTION_COLOR
    );
    s.click_key(0x7777_7777, 1);
    s.capture(Some(key(Key::Q)));
    let unmapped = s.with(|c, _| c.unmapped().clone());
    assert_eq!(
        unmapped.get(0x1000_000A, 0x7777_7777),
        [key(Key::W), key(Key::Q)]
    );
}

#[test]
fn a_screen_without_its_window_root_does_not_bind() {
    let mut ui = UiRoot::new();
    let r = ui.root();
    let root = panel(&mut ui, r, at(0.0, 0.0, 10.0, 10.0));
    let mut rows = build_template;
    let c = KeyboardConfigController::bind(
        &mut ui,
        root,
        KeyboardConfigSources {
            snapshot: snapshot(Vec::new()),
            rows: &mut rows,
            resolve_string: &|_, _| None,
            templates: Box::new(|_, _| None),
            names: RetailKeyNames::new(Box::new(|_, _| None), None),
            template_font: &|_, _| None,
            bindings: KeyBindings::new(),
            unmapped: RetailUnmappedKeyBindings::new(),
            filename: None,
        },
    );
    assert!(c.is_none());
}

#[test]
fn the_identity_map_is_injective() {
    // OpenAC IdentityMap_IsInjective_NoTwoRowsShareOneInputAction.
    let mut seen = HashMap::new();
    for (identity, action) in ac_ui::input::identity::all() {
        assert!(
            seen.insert(*action, *identity).is_none(),
            "{action:?} is mapped twice"
        );
    }
    assert_eq!(seen.len(), 306);
}

// ── Real dats ───────────────────────────────────────────────────────────

mod real_dats {
    //! OpenAC `KeyboardConfigInstalledDatConformanceTests`: the authored
    //! layout mounts every bindable action as a live row.

    use super::*;
    use ac_ui::content::DatContent;
    use ac_ui::font::DEFAULT_FONT_ID;
    use ac_ui::import::{LayoutSource, import};
    use ac_ui::panels::DatRowTemplates;
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

    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_authored_layout_mounts_every_bindable_action_as_a_live_row() {
        let c = content();
        let snapshot = ac_ui::input::action_map::read(c.dats())
            .unwrap()
            .expect("the action map");
        let bindable = snapshot
            .rows
            .iter()
            .filter(|r| r.class != RetailActionClass::None)
            .count();
        let mut host = UiHost::new();
        host.root.set_size(1024.0, 768.0);
        let font = c.font(DEFAULT_FONT_ID);
        let layout = import(&mut host.root, &c, kb::LAYOUT_ID, font.clone()).expect("layout");
        let r = host.root.root();
        host.root.add_child(r, layout.root);
        let strings = c.strings();
        let resolve = |t: u32, h: u32| strings.resolve(t, h, 0);
        let dats = c.dats().clone();
        let templates = ac_ui::strings::StringResolver::new(move |id| {
            dats.get::<ac_dat::objects::StringTable>(id).ok().flatten()
        });
        let dats = c.dats().clone();
        let names = ac_ui::strings::StringResolver::new(move |id| {
            dats.get::<ac_dat::objects::StringTable>(id).ok().flatten()
        });
        let mut rows = DatRowTemplates::new(&c, font);
        let ctl = KeyboardConfigController::bind(
            &mut host.root,
            layout.root,
            KeyboardConfigSources {
                snapshot: snapshot.clone(),
                rows: &mut rows,
                resolve_string: &resolve,
                templates: Box::new(move |k, v| {
                    templates.resolve_template(kb::TEMPLATE_STRING_TABLE_ID, k, v)
                }),
                names: RetailKeyNames::new(Box::new(move |t, h| names.resolve(t, h, 0)), None),
                template_font: &|_, _| None,
                bindings: KeyBindings::retail_defaults(),
                unmapped: RetailUnmappedKeyBindings::new(),
                filename: Some("openac.keymap".into()),
            },
        )
        .expect("binds on the authored layout");
        assert_eq!(
            ctl.rows().len(),
            bindable,
            "every bindable action has a row"
        );
        let mut unmapped = 0;
        for row in ctl.rows() {
            assert_eq!(
                row.key_buttons.len(),
                3,
                "{:#x}/{:#x}",
                row.input_map,
                row.action_id
            );
            assert!(
                row.label.is_some(),
                "{:#x}/{:#x} has a caption",
                row.input_map,
                row.action_id
            );
            if row.mapped.is_none() {
                unmapped += 1;
            }
        }
        assert_eq!(unmapped, 0, "every retail row maps to a client action");
        // The forward row shows the retail default W from the dat's own key
        // name table.
        let forward = ctl
            .rows()
            .iter()
            .find(|r| r.action_id == 0x29 && r.input_map == 4)
            .unwrap();
        let label = host
            .root
            .widget::<Button>(forward.key_buttons[0])
            .unwrap()
            .label
            .clone();
        assert!(label.is_some_and(|l| !l.is_empty()));
    }
}
