//! Buttons, generic dat elements and dat-stated text.
//!
//! Ported from OpenAC `tests/AcDream.App.Tests/UI/UiButtonTests.cs` and
//! `tests/AcDream.App.Tests/UI/Layout/UiDatElementTests.cs`. OpenAC's
//! `OnClick`/`OnPressed`/... callbacks are [`ElementMessage`]s here, with
//! [`ButtonListeners`] standing for "a handler is set".

mod common;

use std::collections::HashMap;
use std::sync::Arc;

use ac_dat::objects::{DIRECT_STATE_ID, PropertyValue, StringInfo};
use ac_ui::draw::{DrawCmd, SpriteSizes, TextureRef};
use ac_ui::element_info::{ElementInfo, ImageMedia, StateInfo};
use ac_ui::event::{UiEvent, kind};
use ac_ui::import::{BuildContext, LayoutSource, create};
use ac_ui::root::{ElementMessage, UiOutput, UiRoot};
use ac_ui::states;
use ac_ui::tree::Element;
use ac_ui::widgets::button::wrap_block_lines;
use ac_ui::widgets::{Button, DatElement, LabelAlign, Text};
use ac_ui::{Color, ElementId};
use common::*;

#[derive(Debug, Default)]
struct Source(HashMap<u32, String>);

impl LayoutSource for Source {
    fn layout(&self, _id: u32) -> Option<Arc<ac_dat::objects::LayoutDesc>> {
        None
    }
    fn string(&self, info: &StringInfo) -> Option<String> {
        self.0.get(&info.string_id).cloned()
    }
}

fn ctx(source: &Source) -> BuildContext<'_> {
    BuildContext {
        source,
        default_font: None,
        source_layout_did: 0,
        media_seconds: 0.0,
    }
}

#[derive(Debug)]
struct Eight;

impl SpriteSizes for Eight {
    fn sprite_size(&self, _file: u32) -> Option<(u32, u32)> {
        Some((8, 8))
    }
}

fn button_info(states: &[&str]) -> ElementInfo {
    let mut info = ElementInfo {
        kind: 1,
        width: 20.0,
        height: 20.0,
        ..ElementInfo::default()
    };
    for (i, s) in states.iter().enumerate() {
        info.state_media.insert(
            (*s).to_owned(),
            ImageMedia {
                file: i as u32 + 1,
                draw_mode: 1,
            },
        );
    }
    if let Some(first) = states.first() {
        info.default_state_name = (*first).to_owned();
    }
    info
}

fn direct_property(info: &mut ElementInfo, id: u32, value: PropertyValue) {
    if info.state(DIRECT_STATE_ID).is_none() {
        info.set_state(DIRECT_STATE_ID, StateInfo::named(DIRECT_STATE_ID, ""));
    }
    let mut s = info.state(DIRECT_STATE_ID).unwrap().clone();
    s.properties.set_value(id, value);
    info.set_state(DIRECT_STATE_ID, s);
}

fn state_with_invisible(id: u32, name: &str, invisible: bool) -> StateInfo {
    let mut s = StateInfo::named(id, name);
    s.properties.set_value(0x3B, PropertyValue::Bool(invisible));
    s
}

/// A root with `button` added as a 20x20 (or info-sized) element.
fn mount(info: ElementInfo) -> (UiRoot, ElementId) {
    let mut ui = root(200.0, 200.0);
    let r = ui.root();
    let (w, h) = (info.width, info.height);
    let b = Button::new(Arc::new(info), None, Vec::new(), 0.0);
    let id = ui.add(r, at(0.0, 0.0, w, h), b);
    (ui, id)
}

fn send(ui: &mut UiRoot, id: ElementId, k: i32, x: i32, y: i32) -> bool {
    let e = UiEvent::new(0, id, k).with_data(0, x, y);
    ui.send_event(id, &e)
}

fn button(ui: &UiRoot, id: ElementId) -> &Button {
    ui.widget::<Button>(id).unwrap()
}

fn messages(ui: &mut UiRoot) -> Vec<ElementMessage> {
    ui.drain_output()
        .into_iter()
        .filter_map(|o| match o {
            UiOutput::Element { message, .. } => Some(message),
            _ => None,
        })
        .collect()
}

fn clicks(ui: &mut UiRoot) -> usize {
    messages(ui)
        .iter()
        .filter(|m| matches!(m, ElementMessage::Clicked { .. }))
        .count()
}

fn drawn_face_file(ui: &mut UiRoot) -> u32 {
    let list = ui.draw((200.0, 200.0), &Eight);
    list.main
        .iter()
        .find_map(|c| match c {
            DrawCmd::Sprite {
                texture: TextureRef::Surface(f),
                ..
            } => Some(*f),
            _ => None,
        })
        .unwrap_or(0)
}

#[test]
fn a_click_reports_its_local_coordinates_when_listened_for() {
    let (mut ui, id) = mount(ElementInfo {
        kind: 1,
        width: 46.0,
        height: 18.0,
        ..ElementInfo::default()
    });
    assert!(
        !send(&mut ui, id, kind::CLICK, 17, 9),
        "no listener: bubbles"
    );
    ui.widget_mut::<Button>(id).unwrap().listeners.click = true;
    assert!(send(&mut ui, id, kind::CLICK, 17, 9));
    assert!(matches!(
        messages(&mut ui).as_slice(),
        [ElementMessage::Clicked { x: 17, y: 9 }]
    ));
}

#[test]
fn double_click_is_opt_in_and_a_disabled_button_swallows_it() {
    let (mut ui, id) = mount(button_info(&[]));
    assert!(!send(&mut ui, id, kind::DOUBLE_CLICK, 0, 0));
    ui.widget_mut::<Button>(id).unwrap().listeners.double_click = true;
    assert!(send(&mut ui, id, kind::DOUBLE_CLICK, 0, 0));
    assert_eq!(messages(&mut ui).len(), 1);
    ui.set_enabled(id, false);
    assert!(send(&mut ui, id, kind::DOUBLE_CLICK, 0, 0));
    assert!(messages(&mut ui).is_empty());
}

#[test]
fn press_and_release_are_distinct_messages() {
    let (mut ui, id) = mount(button_info(&[]));
    send(&mut ui, id, kind::MOUSE_DOWN, 5, 5);
    send(&mut ui, id, kind::MOUSE_UP, 30, 30);
    assert!(matches!(
        messages(&mut ui).as_slice(),
        [ElementMessage::Pressed, ElementMessage::Released]
    ));
}

#[test]
fn a_button_takes_the_pointer() {
    let mut e = Element {
        click_through: true,
        ..Element::default()
    };
    Button::configure(&mut e);
    assert!(!e.click_through);
}

#[test]
fn pointer_transitions_use_the_retail_normal_states() {
    let mut info = button_info(&["Normal", "Normal_rollover", "Normal_pressed"]);
    direct_property(&mut info, 0x13, PropertyValue::Bool(true));
    let (mut ui, id) = mount(info);
    send(&mut ui, id, kind::HOVER_ENTER, 0, 0);
    assert_eq!(button(&ui, id).active_state(), "Normal_rollover");
    send(&mut ui, id, kind::MOUSE_DOWN, 5, 5);
    assert_eq!(button(&ui, id).active_state(), "Normal_pressed");
    send(&mut ui, id, kind::MOUSE_MOVE, 50, 50);
    assert_eq!(button(&ui, id).active_state(), "Normal_rollover");
    send(&mut ui, id, kind::MOUSE_MOVE, 5, 5);
    assert_eq!(button(&ui, id).active_state(), "Normal_pressed");
    send(&mut ui, id, kind::MOUSE_UP, 5, 5);
    assert_eq!(button(&ui, id).active_state(), "Normal_rollover");
}

#[test]
fn a_toggle_release_selects_the_highlight_state() {
    let mut info = button_info(&["Normal", "Highlight"]);
    direct_property(&mut info, 0x0B, PropertyValue::Bool(true));
    let (mut ui, id) = mount(info);
    send(&mut ui, id, kind::MOUSE_DOWN, 5, 5);
    send(&mut ui, id, kind::MOUSE_UP, 5, 5);
    assert!(button(&ui, id).selected());
    assert_eq!(button(&ui, id).active_state(), "Highlight");
}

#[test]
fn suppress_self_toggle_leaves_selection_to_the_controller() {
    let mut info = button_info(&["Normal", "Highlight"]);
    direct_property(&mut info, 0x0B, PropertyValue::Bool(true));
    let (mut ui, id) = mount(info);
    ui.widget_mut::<Button>(id).unwrap().suppress_self_toggle = true;
    send(&mut ui, id, kind::MOUSE_DOWN, 5, 5);
    send(&mut ui, id, kind::MOUSE_UP, 5, 5);
    assert!(!button(&ui, id).selected());
    assert_eq!(button(&ui, id).active_state(), "Normal");
    ui.with::<Button, _>(id, |b, ui| b.set_selected(ui, id, true));
    assert!(button(&ui, id).selected());
    assert_eq!(button(&ui, id).active_state(), "Highlight");
}

#[test]
fn property_0x0d_does_not_disable_the_button() {
    let mut info = button_info(&["Normal", "Ghosted"]);
    direct_property(&mut info, 0x0D, PropertyValue::Bool(true));
    let (mut ui, id) = mount(info);
    ui.widget_mut::<Button>(id).unwrap().listeners.click = true;
    send(&mut ui, id, kind::CLICK, 0, 0);
    assert!(ui.element(id).unwrap().enabled());
    assert_eq!(clicks(&mut ui), 1);
}

#[test]
fn an_explicit_ghosted_state_suppresses_the_click() {
    let (mut ui, id) = mount(button_info(&["Normal", "Ghosted"]));
    ui.widget_mut::<Button>(id).unwrap().listeners.click = true;
    ui.set_retail_state(id, states::GHOSTED);
    send(&mut ui, id, kind::CLICK, 0, 0);
    assert!(!ui.element(id).unwrap().enabled());
    assert_eq!(clicks(&mut ui), 0);
}

#[test]
fn a_missing_standard_state_keeps_the_custom_state() {
    let (mut ui, id) = mount(button_info(&["LockedUI"]));
    send(&mut ui, id, kind::HOVER_ENTER, 0, 0);
    send(&mut ui, id, kind::MOUSE_DOWN, 5, 5);
    assert_eq!(button(&ui, id).active_state(), "LockedUI");
}

#[test]
fn a_property_only_pressed_state_keeps_the_normal_face() {
    let mut info = button_info(&["Normal", "Highlight"]);
    info.set_state(
        states::NORMAL_PRESSED,
        StateInfo::named(states::NORMAL_PRESSED, "Normal_pressed"),
    );
    let (mut ui, id) = mount(info);
    assert_eq!(drawn_face_file(&mut ui), 1);
    send(&mut ui, id, kind::MOUSE_DOWN, 5, 5);
    assert_eq!(button(&ui, id).active_state(), "Normal_pressed");
    assert_eq!(drawn_face_file(&mut ui), 1);
}

#[test]
fn the_default_state_needs_real_media() {
    let mut info = button_info(&["Normal", "Highlight"]);
    direct_property(&mut info, 0x13, PropertyValue::Bool(true));
    let (mut ui, id) = mount(info);
    assert_eq!(button(&ui, id).active_state(), "Normal");
    assert!(!ui.set_retail_state(id, DIRECT_STATE_ID));
    assert_eq!(button(&ui, id).active_state(), "Normal");

    let mut info = button_info(&["Normal"]);
    info.state_media.insert(
        String::new(),
        ImageMedia {
            file: 7,
            draw_mode: 1,
        },
    );
    let (mut ui, id) = mount(info);
    assert!(ui.set_retail_state(id, DIRECT_STATE_ID));
    assert_eq!(button(&ui, id).active_state(), "");
}

#[test]
fn hot_click_fires_at_once_repeats_and_suppresses_the_release_click() {
    let mut info = button_info(&["Normal"]);
    direct_property(&mut info, 0x0F, PropertyValue::Bool(true));
    direct_property(&mut info, 0x10, PropertyValue::Float(0.10));
    direct_property(&mut info, 0x11, PropertyValue::Float(0.05));
    let (mut ui, id) = mount(info);
    ui.widget_mut::<Button>(id).unwrap().listeners.click = true;
    send(&mut ui, id, kind::MOUSE_DOWN, 5, 5);
    assert_eq!(clicks(&mut ui), 1);
    let pulse = |ui: &mut UiRoot, t: f64| {
        ui.with_widget(id, |w, ui| w.global_time(ui, id, t));
    };
    pulse(&mut ui, 1.00);
    pulse(&mut ui, 1.09);
    assert_eq!(clicks(&mut ui), 0);
    pulse(&mut ui, 1.11);
    assert_eq!(clicks(&mut ui), 1);
    send(&mut ui, id, kind::MOUSE_UP, 5, 5);
    send(&mut ui, id, kind::CLICK, 5, 5);
    assert_eq!(clicks(&mut ui), 0);
}

#[test]
fn a_custom_selection_pair_on_the_button_follows_selected() {
    let (mut ui, id) = mount(button_info(&["Unselected", "Selected"]));
    assert_eq!(button(&ui, id).active_state(), "Unselected");
    ui.with::<Button, _>(id, |b, ui| b.set_selected(ui, id, true));
    assert_eq!(button(&ui, id).active_state(), "Selected");
    ui.with::<Button, _>(id, |b, ui| b.set_selected(ui, id, false));
    assert_eq!(button(&ui, id).active_state(), "Unselected");
}

#[test]
fn a_custom_selection_pair_on_a_face_child_follows_selected() {
    let mut info = ElementInfo {
        kind: 1,
        width: 305.0,
        height: 32.0,
        default_state_name: "Unselected".into(),
        ..ElementInfo::default()
    };
    info.set_state(
        states::NORMAL_PRESSED,
        StateInfo::named(states::NORMAL_PRESSED, "Normal_pressed"),
    );
    info.set_state(
        states::UNSELECTED,
        StateInfo::named(states::UNSELECTED, "Unselected"),
    );
    info.set_state(
        states::SELECTED,
        StateInfo::named(states::SELECTED, "Selected"),
    );
    let mut dot = ElementInfo {
        kind: 3,
        width: 32.0,
        height: 32.0,
        ..ElementInfo::default()
    };
    dot.state_media.insert(
        "Unselected".into(),
        ImageMedia {
            file: 0x0600_6E35,
            draw_mode: 1,
        },
    );
    dot.state_media.insert(
        "Selected".into(),
        ImageMedia {
            file: 0x0600_6E21,
            draw_mode: 1,
        },
    );
    info.children.push(dot);
    let source = Source::default();
    let (element, widget) = create(&info, &ctx(&source));
    let mut ui = root(400.0, 100.0);
    let r = ui.root();
    let id = ui.insert_boxed(element, widget);
    ui.add_child(r, id);
    assert_eq!(button(&ui, id).active_state(), "Unselected");
    ui.with::<Button, _>(id, |b, ui| b.set_selected(ui, id, true));
    assert_eq!(button(&ui, id).active_state(), "Selected");
    assert_eq!(button(&ui, id).active_retail_state_id(), states::SELECTED);
    ui.with::<Button, _>(id, |b, ui| b.set_selected(ui, id, false));
    assert_eq!(button(&ui, id).active_state(), "Unselected");
}

#[test]
fn without_a_custom_pair_toggling_uses_normal_and_highlight() {
    let mut info = button_info(&["Normal", "Highlight"]);
    direct_property(&mut info, 0x0B, PropertyValue::Bool(true));
    let (mut ui, id) = mount(info);
    ui.with::<Button, _>(id, |b, ui| b.set_selected(ui, id, true));
    assert_eq!(button(&ui, id).active_state(), "Highlight");
    ui.with::<Button, _>(id, |b, ui| b.set_selected(ui, id, false));
    assert_eq!(button(&ui, id).active_state(), "Normal");
}

#[test]
fn per_state_label_style_follows_a_property_only_highlight() {
    let mut info = button_info(&["Normal"]);
    info.set_state(
        states::HIGHLIGHT,
        StateInfo::named(states::HIGHLIGHT, "Highlight"),
    );
    direct_property(&mut info, 0x0B, PropertyValue::Bool(true));
    let (mut ui, id) = mount(info);
    let normal = Color::rgba(218.0 / 255.0, 167.0 / 255.0, 85.0 / 255.0, 1.0);
    let highlight = Color::rgba(1.0, 221.0 / 255.0, 131.0 / 255.0, 1.0);
    {
        let b = ui.widget_mut::<Button>(id).unwrap();
        b.label = Some("Hair Style".into());
        b.label_color = Color::WHITE;
        b.set_per_state_label_style(
            Some(vec![
                (states::NORMAL, normal),
                (states::HIGHLIGHT, highlight),
            ]),
            Some(vec![(states::NORMAL, false), (states::HIGHLIGHT, true)]),
        );
    }
    assert_eq!(button(&ui, id).label_color, normal);
    assert!(!button(&ui, id).outline);
    assert_eq!(drawn_face_file(&mut ui), 1);
    ui.with::<Button, _>(id, |b, ui| b.set_selected(ui, id, true));
    assert_eq!(button(&ui, id).active_state(), "Highlight");
    assert_eq!(button(&ui, id).label_color, highlight);
    assert!(button(&ui, id).outline);
    assert_eq!(drawn_face_file(&mut ui), 1);
}

#[test]
fn without_per_state_style_an_assigned_label_colour_survives() {
    let mut info = button_info(&["Normal", "Highlight"]);
    direct_property(&mut info, 0x0B, PropertyValue::Bool(true));
    let (mut ui, id) = mount(info);
    let external = Color::rgba(1.0, 0.92, 0.72, 1.0);
    ui.widget_mut::<Button>(id).unwrap().label_color = external;
    ui.with::<Button, _>(id, |b, ui| b.set_selected(ui, id, true));
    assert_eq!(button(&ui, id).label_color, external);
    ui.with::<Button, _>(id, |b, ui| b.set_selected(ui, id, false));
    assert_eq!(button(&ui, id).label_color, external);
}

fn bitmap(s: &str) -> f32 {
    s.chars().count() as f32 * 8.0
}

#[test]
fn block_labels_sit_in_their_box() {
    let l = wrap_block_lines(
        "1358",
        bitmap,
        16.0,
        (0.0, 0.0, 55.0, 58.0),
        LabelAlign::Center,
        0.0,
        true,
    );
    assert_eq!(l.len(), 1);
    assert_eq!((l[0].1, l[0].2), ((55.0 - 32.0) * 0.5, 58.0 - 16.0));
    let l = wrap_block_lines(
        "Health",
        bitmap,
        24.0,
        (0.0, 0.0, 150.0, 50.0),
        LabelAlign::Left,
        3.0,
        false,
    );
    assert_eq!((l[0].0.as_str(), l[0].1, l[0].2), ("Health", 3.0, 13.0));
    let l = wrap_block_lines(
        "Attribute\nCredits",
        bitmap,
        24.0,
        (0.0, 0.0, 90.0, 50.0),
        LabelAlign::Left,
        3.0,
        false,
    );
    assert_eq!(
        l.iter().map(|x| (x.0.as_str(), x.2)).collect::<Vec<_>>(),
        [("Attribute", 1.0), ("Credits", 25.0)]
    );
    for (text, w) in [("Available Skill Credits", 113.0), ("Coordination", 115.0)] {
        let l = wrap_block_lines(
            text,
            bitmap,
            24.0,
            (0.0, 0.0, w, 24.0),
            LabelAlign::Left,
            3.0,
            false,
        );
        assert_eq!(l.len(), 1);
        assert_eq!(l[0].0, text);
    }
}

fn caption(info: &mut ElementInfo, string_id: u32) {
    direct_property(
        info,
        0x17,
        PropertyValue::StringInfo(StringInfo {
            string_id,
            ..StringInfo::default()
        }),
    );
}

#[test]
fn an_own_caption_with_a_value_child_stops_before_the_value_box() {
    let mut info = ElementInfo {
        kind: 1,
        width: 231.0,
        height: 28.0,
        ..ElementInfo::default()
    };
    caption(&mut info, 333);
    info.state_media.insert(
        String::new(),
        ImageMedia {
            file: 0x0600_0001,
            draw_mode: 1,
        },
    );
    info.children.push(ElementInfo {
        kind: 12,
        x: 116.0,
        width: 34.0,
        height: 28.0,
        ..ElementInfo::default()
    });
    let source = Source(HashMap::from([(333, "Available Skill Credits".to_owned())]));
    let (_, widget) = create(&info, &ctx(&source));
    let b = widget.as_any().downcast_ref::<Button>().unwrap();
    assert_eq!(b.label.as_deref(), Some("Available Skill Credits"));
    assert_eq!(b.value_box, Some((116.0, 0.0, 34.0, 28.0)));
}

#[test]
fn an_own_caption_passes_the_resolved_text_through() {
    let mut info = ElementInfo {
        kind: 1,
        width: 150.0,
        height: 50.0,
        ..ElementInfo::default()
    };
    caption(&mut info, 444);
    let source = Source(HashMap::from([(
        444,
        "Attribute\n Credits \\not-an-escape".to_owned(),
    )]));
    let (_, widget) = create(&info, &ctx(&source));
    let b = widget.as_any().downcast_ref::<Button>().unwrap();
    assert_eq!(
        b.label.as_deref(),
        Some("Attribute\n Credits \\not-an-escape")
    );
}

fn map_note_info(pass: bool) -> ElementInfo {
    let mut info = ElementInfo {
        kind: 1,
        width: 10.0,
        height: 10.0,
        ..ElementInfo::default()
    };
    direct_property(&mut info, 0x13, PropertyValue::Bool(true));
    let mut normal = StateInfo::named(states::NORMAL, "Normal");
    normal.pass_to_children = pass;
    let mut rollover = StateInfo::named(states::NORMAL_ROLLOVER, "Normal_rollover");
    rollover.pass_to_children = pass;
    info.set_state(states::NORMAL, normal);
    info.set_state(states::NORMAL_ROLLOVER, rollover);
    info
}

fn highlight_child() -> DatElement {
    let mut info = ElementInfo {
        kind: 3,
        width: 10.0,
        height: 10.0,
        ..ElementInfo::default()
    };
    info.set_state(1, state_with_invisible(1, "Normal", true));
    info.set_state(2, state_with_invisible(2, "Normal_rollover", false));
    DatElement::new(Arc::new(info))
}

#[test]
fn pass_to_children_states_drive_the_childrens_invisibility() {
    let (mut ui, id) = mount(map_note_info(true));
    let kid = ui.add(id, at(0.0, 0.0, 10.0, 10.0), highlight_child());
    assert!(ui.element(kid).unwrap().visible());
    assert!(ui.set_retail_state(id, states::NORMAL));
    assert!(!ui.element(kid).unwrap().visible());
    send(&mut ui, id, kind::HOVER_ENTER, 0, 0);
    assert!(ui.element(kid).unwrap().visible());
    send(&mut ui, id, kind::HOVER_LEAVE, 0, 0);
    assert!(!ui.element(kid).unwrap().visible());

    let (mut ui, id) = mount(map_note_info(false));
    let kid = ui.add(id, at(0.0, 0.0, 10.0, 10.0), highlight_child());
    ui.set_retail_state(id, states::NORMAL);
    send(&mut ui, id, kind::HOVER_ENTER, 0, 0);
    assert!(ui.element(kid).unwrap().visible());
}

#[test]
fn a_dat_element_honours_per_state_invisibility() {
    let mut ui = root(100.0, 100.0);
    let r = ui.root();
    let kid = ui.add(r, at(0.0, 0.0, 10.0, 10.0), highlight_child());
    assert!(ui.set_retail_state(kid, 1));
    assert!(!ui.element(kid).unwrap().visible());
    assert!(ui.set_retail_state(kid, 2));
    assert!(ui.element(kid).unwrap().visible());
}

#[test]
fn button_named_and_default_states_apply_invisibility() {
    let mut info = button_info(&["Normal", "Ghosted"]);
    info.state_media.insert(
        String::new(),
        ImageMedia {
            file: 99,
            draw_mode: 1,
        },
    );
    info.set_state(
        DIRECT_STATE_ID,
        state_with_invisible(DIRECT_STATE_ID, "", true),
    );
    info.set_state(
        states::NORMAL,
        state_with_invisible(states::NORMAL, "Normal", false),
    );
    info.set_state(
        states::GHOSTED,
        state_with_invisible(states::GHOSTED, "Ghosted", true),
    );
    let (mut ui, id) = mount(info);
    ui.set_visible(id, false);
    assert!(ui.set_retail_state(id, states::NORMAL));
    assert!(ui.element(id).unwrap().visible());
    assert!(ui.set_retail_state(id, states::GHOSTED));
    assert!(!ui.element(id).unwrap().visible());
    ui.set_visible(id, true);
    assert!(ui.set_retail_state(id, DIRECT_STATE_ID));
    assert!(!ui.element(id).unwrap().visible());
}

#[test]
fn text_returning_to_its_default_state_restores_invisibility() {
    let mut info = ElementInfo {
        kind: 12,
        width: 20.0,
        height: 20.0,
        ..ElementInfo::default()
    };
    info.set_state(
        DIRECT_STATE_ID,
        state_with_invisible(DIRECT_STATE_ID, "", true),
    );
    info.set_state(
        states::NORMAL,
        state_with_invisible(states::NORMAL, "Normal", false),
    );
    let mut text = Text::default();
    text.configure_dat_state(Arc::new(info));
    let mut ui = root(100.0, 100.0);
    let r = ui.root();
    let id = ui.add(r, at(0.0, 0.0, 20.0, 20.0), text);
    assert!(ui.set_retail_state(id, DIRECT_STATE_ID));
    assert!(!ui.element(id).unwrap().visible());
    assert!(ui.set_retail_state(id, states::NORMAL));
    assert!(ui.element(id).unwrap().visible());
    assert!(ui.set_retail_state(id, DIRECT_STATE_ID));
    assert!(!ui.element(id).unwrap().visible());
}

#[test]
fn a_multi_segment_face_without_media_draws_nothing() {
    let info = ElementInfo {
        kind: 1,
        width: 32.0,
        height: 16.0,
        ..ElementInfo::default()
    };
    let segment = Arc::new(ElementInfo {
        kind: 1,
        width: 16.0,
        height: 16.0,
        ..ElementInfo::default()
    });
    let mut ui = root(200.0, 200.0);
    let r = ui.root();
    ui.add(
        r,
        at(0.0, 0.0, 32.0, 16.0),
        Button::new(Arc::new(info), None, vec![segment], 0.0),
    );
    assert_eq!(drawn_face_file(&mut ui), 0);
}

// ── UiDatElementTests ───────────────────────────────────────────────────

fn dat(info: ElementInfo) -> DatElement {
    DatElement::new(Arc::new(info))
}

#[test]
fn a_tiled_background_has_texture_coordinates_past_one() {
    let mut info = ElementInfo {
        width: 1920.0,
        height: 1080.0,
        ..ElementInfo::default()
    };
    info.state_media.insert(
        String::new(),
        ImageMedia {
            file: 0x0600_7576,
            draw_mode: 1,
        },
    );
    #[derive(Debug)]
    struct Big;
    impl SpriteSizes for Big {
        fn sprite_size(&self, _file: u32) -> Option<(u32, u32)> {
            Some((800, 600))
        }
    }
    let mut ui = root(1920.0, 1080.0);
    let r = ui.root();
    ui.add(r, at(0.0, 0.0, 1920.0, 1080.0), dat(info));
    let list = ui.draw((1920.0, 1080.0), &Big);
    match &list.main[0] {
        DrawCmd::Sprite { texture, quad, .. } => {
            assert_eq!(*texture, TextureRef::Surface(0x0600_7576));
            assert!((quad.u1 - 2.4).abs() < 1e-3 && (quad.v1 - 1.8).abs() < 1e-3);
        }
        other => panic!("{other:?}"),
    }
}

#[test]
fn the_canvas_scale_travels_with_the_draw_list() {
    let mut ui = root(1920.0, 1080.0);
    ui.declare_fixed_canvas(1, (800.0, 600.0)).unwrap();
    let list = ui.draw((1920.0, 1080.0), &Eight);
    assert_eq!(list.canvas_scale, (2.4, 1.8));
}

#[test]
fn active_media_prefers_the_named_state_then_the_default() {
    let mut info = ElementInfo::default();
    info.state_media.insert(
        String::new(),
        ImageMedia {
            file: 0x0600_0001,
            draw_mode: 1,
        },
    );
    info.state_media.insert(
        "ShowDetail".into(),
        ImageMedia {
            file: 0x0600_0002,
            draw_mode: 3,
        },
    );
    let mut e = dat(info);
    e.active_state = "ShowDetail".into();
    assert_eq!(
        e.active_media(),
        ImageMedia {
            file: 0x0600_0002,
            draw_mode: 3
        }
    );
    e.active_state.clear();
    assert_eq!(
        e.active_media(),
        ImageMedia {
            file: 0x0600_0001,
            draw_mode: 1
        }
    );
    e.active_state = "NoSuchState".into();
    assert_eq!(e.active_media().file, 0x0600_0001);
    assert_eq!(
        dat(ElementInfo::default()).active_media(),
        ImageMedia::default()
    );
}

#[test]
fn the_initial_state_is_the_default_name_else_normal_else_the_default_state() {
    let mut info = ElementInfo::default();
    info.state_media.insert(
        "Normal".into(),
        ImageMedia {
            file: 0xAAAA,
            draw_mode: 1,
        },
    );
    info.state_media.insert(
        "Hover".into(),
        ImageMedia {
            file: 0xBBBB,
            draw_mode: 1,
        },
    );
    assert_eq!(dat(info).active_media().file, 0xAAAA);
    let mut info = ElementInfo {
        default_state_name: "Minimized".into(),
        ..ElementInfo::default()
    };
    info.state_media.insert(
        "Minimized".into(),
        ImageMedia {
            file: 0xBBBB,
            draw_mode: 1,
        },
    );
    info.state_media.insert(
        "Maximized".into(),
        ImageMedia {
            file: 0xCCCC,
            draw_mode: 1,
        },
    );
    info.state_media.insert(
        "Normal".into(),
        ImageMedia {
            file: 0xDDDD,
            draw_mode: 1,
        },
    );
    assert_eq!(dat(info).active_media().file, 0xBBBB);
    let mut info = ElementInfo::default();
    info.state_media.insert(
        String::new(),
        ImageMedia {
            file: 0x0600_7777,
            draw_mode: 1,
        },
    );
    assert_eq!(dat(info).active_media().file, 0x0600_7777);
}

#[test]
fn numeric_states_set_the_named_state_without_moving_the_element() {
    let mut info = ElementInfo {
        x: 4.0,
        y: 5.0,
        width: 30.0,
        height: 40.0,
        ..ElementInfo::default()
    };
    let mut s = StateInfo::named(states::SHOW_DETAIL, "ShowDetail");
    s.image = Some(ImageMedia {
        file: 0x0600_0009,
        draw_mode: 3,
    });
    info.set_state(states::SHOW_DETAIL, s);
    info.state_media.insert(
        "ShowDetail".into(),
        ImageMedia {
            file: 0x0600_0009,
            draw_mode: 3,
        },
    );
    let mut ui = root(100.0, 100.0);
    let r = ui.root();
    let id = ui.add(r, at(4.0, 5.0, 30.0, 40.0), dat(info));
    assert!(ui.set_retail_state(id, states::SHOW_DETAIL));
    assert_eq!(ui.retail_state(id), Some(states::SHOW_DETAIL));
    assert_eq!(
        ui.widget::<DatElement>(id).unwrap().active_media(),
        ImageMedia {
            file: 0x0600_0009,
            draw_mode: 3
        }
    );
    let e = ui.element(id).unwrap();
    assert_eq!((e.left, e.top, e.width, e.height), (4.0, 5.0, 30.0, 40.0));
}

#[test]
fn a_missing_numeric_state_commits_the_default_state() {
    let mut info = ElementInfo {
        default_state_name: "Normal".into(),
        ..ElementInfo::default()
    };
    info.state_media.insert(
        "Normal".into(),
        ImageMedia {
            file: 0x0600_0001,
            draw_mode: 1,
        },
    );
    let mut ui = root(100.0, 100.0);
    let r = ui.root();
    let id = ui.add(r, at(0.0, 0.0, 1.0, 1.0), dat(info));
    assert!(ui.set_retail_state(id, states::SHOW_DETAIL));
    assert_eq!(ui.widget::<DatElement>(id).unwrap().active_state, "");
}

#[test]
fn an_unauthored_normal_clears_rollover_media() {
    let mut info = ElementInfo::default();
    info.set_state(DIRECT_STATE_ID, StateInfo::named(DIRECT_STATE_ID, ""));
    info.set_state(2, StateInfo::named(2, "Normal_rollover"));
    info.state_media.insert(
        "Normal_rollover".into(),
        ImageMedia {
            file: 0x0600_5EB6,
            draw_mode: 1,
        },
    );
    let mut ui = root(100.0, 100.0);
    let r = ui.root();
    let id = ui.add(r, at(0.0, 0.0, 1.0, 1.0), dat(info));
    assert!(ui.set_retail_state(id, 2));
    assert_eq!(
        ui.widget::<DatElement>(id).unwrap().active_state,
        "Normal_rollover"
    );
    assert!(ui.set_retail_state(id, states::NORMAL));
    let e = ui.widget::<DatElement>(id).unwrap();
    assert_eq!(e.active_state, "");
    assert_eq!(e.active_media(), ImageMedia::default());
}

#[test]
fn an_unauthored_state_falls_back_to_the_default_states_invisibility() {
    let mut info = ElementInfo::default();
    info.set_state(
        DIRECT_STATE_ID,
        state_with_invisible(DIRECT_STATE_ID, "", true),
    );
    info.state_media.insert(
        "Normal_rollover".into(),
        ImageMedia {
            file: 0x0600_5EB6,
            draw_mode: 1,
        },
    );
    let mut ui = root(100.0, 100.0);
    let r = ui.root();
    let id = ui.add(r, at(0.0, 0.0, 1.0, 1.0), dat(info));
    assert!(ui.set_retail_state(id, states::NORMAL));
    assert_eq!(ui.widget::<DatElement>(id).unwrap().active_state, "");
    assert!(!ui.element(id).unwrap().visible());
}

#[test]
fn an_unauthored_state_cascades_the_default_state() {
    let mut child_info = ElementInfo::default();
    child_info.state_media.insert(
        String::new(),
        ImageMedia {
            file: 0x0600_0002,
            draw_mode: 1,
        },
    );
    child_info.state_media.insert(
        "Normal_rollover".into(),
        ImageMedia {
            file: 0x0600_5EB6,
            draw_mode: 1,
        },
    );
    child_info.set_state(2, StateInfo::named(2, "Normal_rollover"));
    let mut parent_info = ElementInfo::default();
    let mut direct = StateInfo::named(DIRECT_STATE_ID, "");
    direct.pass_to_children = true;
    parent_info.set_state(DIRECT_STATE_ID, direct);
    let mut ui = root(100.0, 100.0);
    let r = ui.root();
    let parent = ui.add(r, at(0.0, 0.0, 1.0, 1.0), dat(parent_info));
    let child = ui.add(parent, at(0.0, 0.0, 1.0, 1.0), dat(child_info));
    assert!(ui.set_retail_state(child, 2));
    assert!(ui.set_retail_state(parent, states::NORMAL));
    assert_eq!(ui.widget::<DatElement>(child).unwrap().active_state, "");
}

#[test]
fn a_click_only_element_leaves_the_press_to_bubble() {
    let mut ui = root(100.0, 100.0);
    let r = ui.root();
    let mut e = dat(ElementInfo::default());
    e.emits_clicks = true;
    let id = ui.add(r, at(0.0, 0.0, 10.0, 10.0), e);
    assert!(!send(&mut ui, id, kind::MOUSE_DOWN, 0, 0));
    assert!(!send(&mut ui, id, kind::RIGHT_DOWN, 0, 0));
    assert!(send(&mut ui, id, kind::CLICK, 3, 4));
    assert!(matches!(
        messages(&mut ui).as_slice(),
        [ElementMessage::Clicked { x: 3, y: 4 }]
    ));
}

#[derive(Debug, Clone, Default)]
struct PressPoint(
    Arc<std::sync::Mutex<Option<(i32, i32)>>>,
    Arc<std::sync::Mutex<u32>>,
);

impl ac_ui::tree::RegionHandler for PressPoint {
    fn is_drag_source(&self) -> bool {
        true
    }
    fn drag_payload_at(&mut self, x: i32, y: i32) -> Option<ac_ui::DragPayload> {
        *self.0.lock().unwrap() = Some((x, y));
        Some(ac_ui::DragPayload::new((x, y)))
    }
    fn right_clicked(&mut self, _x: i32, _y: i32) -> bool {
        *self.1.lock().unwrap() += 1;
        true
    }
}

#[test]
fn the_press_point_is_recorded_only_for_the_events_own_target() {
    let mut ui = root(100.0, 100.0);
    let r = ui.root();
    let id = ui.add(r, at(0.0, 0.0, 50.0, 50.0), dat(ElementInfo::default()));
    let child = ui.add(id, at(0.0, 0.0, 5.0, 5.0), dat(ElementInfo::default()));
    let region = PressPoint::default();
    ui.element_mut(id).unwrap().pointer_region =
        Some(ac_ui::tree::PointerRegion::new(region.clone()));
    let press = UiEvent::new(0, id, kind::MOUSE_DOWN).with_data(0, 7, 9);
    assert!(!ui.send_event(id, &press));
    let bubbled = UiEvent::new(0, child, kind::MOUSE_DOWN).with_data(0, 1, 2);
    ui.send_event(id, &bubbled);
    let payload = ui
        .with_widget(id, |w, ui| w.drag_payload(ui, id))
        .flatten()
        .unwrap();
    assert_eq!(payload.get::<(i32, i32)>(), Some(&(7, 9)));
    let rc = UiEvent::new(0, child, kind::RIGHT_CLICK);
    assert!(!ui.send_event(id, &rc));
    assert_eq!(*region.1.lock().unwrap(), 0);
}
