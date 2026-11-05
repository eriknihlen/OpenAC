//! The dat widget factory: element class → widget, and what each builder
//! reads from the element description.
//! Ported from OpenAC `tests/AcDream.App.Tests/UI/Layout/DatWidgetFactoryTests.cs`.
//! OpenAC's `Create` returns the widget; here [`build`] places it in a root
//! and the test reads the widget and its element back. OpenAC's
//! `stringResolve` is the source's `string`. The two fixture cases
//! (`RetailToolbarFixture_...`, `ChatTalkFocusMenu_...`) read the real
//! dats in `real_dats`.

mod common;

use std::collections::HashMap;
use std::sync::Arc;

use ac_dat::objects::{DIRECT_STATE_ID, LayoutDesc, PropertyValue, StringInfo};
use ac_dat::types::ColorArgb;
use ac_ui::draw::Color;
use ac_ui::element_info::{CursorMedia, ElementInfo, ImageMedia, StateInfo};
use ac_ui::geometry::{AnchorEdges, PixelRect, ResizeEdges};
use ac_ui::import::{BuildContext, LayoutSource, build};
use ac_ui::root::UiRoot;
use ac_ui::text::{HJustify, VJustify};
use ac_ui::tree::{Element, ElementId};
use ac_ui::widgets::{
    Border, Button, DatElement, DialogRoot, Field, ItemList, LabelAlign, Menu, Meter,
    MeterLabelAlign, Radar, ResizeGrip, Scrollbar, Text,
};
use common::root;

/// The combat bar's power slider (OpenAC `CombatUiController.PowerControlId`).
const POWER_CONTROL_ID: u32 = 0x1000_004F;

/// Strings by string id; everything else absent.
#[derive(Default)]
struct Source {
    strings: HashMap<u32, String>,
    any: Option<String>,
}

impl LayoutSource for Source {
    fn layout(&self, _id: u32) -> Option<Arc<LayoutDesc>> {
        None
    }
    fn string(&self, info: &StringInfo) -> Option<String> {
        self.strings
            .get(&info.string_id)
            .cloned()
            .or_else(|| self.any.clone())
    }
}

fn strings(pairs: &[(u32, &str)]) -> Source {
    Source {
        strings: pairs.iter().map(|(k, v)| (*k, (*v).to_owned())).collect(),
        any: None,
    }
}

fn every_string(s: &str) -> Source {
    Source {
        strings: HashMap::new(),
        any: Some(s.to_owned()),
    }
}

/// Build `info` as an imported root under the UI root.
fn create_with(info: &ElementInfo, source: &Source) -> (UiRoot, ElementId) {
    let mut ui = root(1600.0, 1200.0);
    let ctx = BuildContext {
        source,
        default_font: None,
        source_layout_did: 0,
        media_seconds: 0.0,
    };
    let imported = build(&mut ui, info, &ctx);
    let top = ui.root();
    ui.add_child(top, imported.root);
    (ui, imported.root)
}

fn create(info: &ElementInfo) -> (UiRoot, ElementId) {
    create_with(info, &Source::default())
}

fn el(kind: u32, w: f32, h: f32) -> ElementInfo {
    ElementInfo {
        kind,
        width: w,
        height: h,
        ..ElementInfo::default()
    }
}

fn media(file: u32) -> ImageMedia {
    ImageMedia { file, draw_mode: 1 }
}

fn direct(properties: &[(u32, PropertyValue)]) -> (u32, StateInfo) {
    let mut s = StateInfo::named(DIRECT_STATE_ID, "");
    for (id, v) in properties {
        s.properties.set_value(*id, v.clone());
    }
    (DIRECT_STATE_ID, s)
}

fn named(id: u32, name: &str) -> (u32, StateInfo) {
    (id, StateInfo::named(id, name))
}

fn string_info(string_id: u32) -> PropertyValue {
    PropertyValue::StringInfo(StringInfo {
        token: 0,
        string_id,
        table_id: 0,
        override_flag: 0,
        english: 0,
        comment: 0,
    })
}

fn rgb(red: u8, green: u8, blue: u8) -> PropertyValue {
    PropertyValue::Color(ColorArgb {
        blue,
        green,
        red,
        alpha: 255,
    })
}

fn is<T: ac_ui::tree::Widget>(ui: &UiRoot, id: ElementId) -> bool {
    ui.widget::<T>(id).is_some()
}

fn element(ui: &UiRoot, id: ElementId) -> &Element {
    ui.element(id).unwrap()
}

// ── Classes ────────────────────────────────────────────────────────────────

#[test]
fn a_meter_class_makes_a_meter() {
    let (ui, id) = create(&el(7, 150.0, 16.0));
    assert!(is::<Meter>(&ui, id));
}

#[test]
fn the_retail_radar_class_makes_a_radar() {
    let (ui, id) = create(&el(ac_ui::widgets::radar::RETAIL_CLASS_ID, 120.0, 140.0));
    assert!(is::<Radar>(&ui, id));
}

#[test]
fn the_dialog_root_classes_make_dialog_roots() {
    for kind in [0x13, 0x15, 0x17, 0x19] {
        let (ui, id) = create(&el(kind, 800.0, 600.0));
        assert!(is::<DialogRoot>(&ui, id), "class {kind:#x}");
    }
}

#[test]
fn an_unknown_class_falls_back_to_the_generic_element() {
    let (ui, id) = create(&el(999, 0.0, 0.0));
    assert!(is::<DatElement>(&ui, id));
}

#[test]
fn a_drag_bar_moves_its_window_and_claims_the_pointer() {
    let (ui, id) = create(&el(2, 600.0, 5.0));
    assert!(is::<DatElement>(&ui, id));
    assert!(element(&ui, id).window_move_handle);
    assert!(!element(&ui, id).click_through);
}

fn grip(bottom: bool, left: bool, right: bool, top: bool) -> ElementInfo {
    let mut info = el(9, 5.0, 5.0);
    info.id = 0x1000_069B;
    let mut props = Vec::new();
    for (on, id) in [(bottom, 0x2A), (left, 0x2B), (right, 0x2C), (top, 0x2D)] {
        if on {
            props.push((id, PropertyValue::Bool(true)));
        }
    }
    info.states.push(direct(&props));
    info
}

#[test]
fn a_resize_bar_with_no_edges_set_has_none() {
    let (ui, id) = create(&grip(false, false, false, false));
    let g = ui.widget::<ResizeGrip>(id).unwrap();
    assert_eq!(g.border, Border::None);
    assert_eq!(g.border.edges(), ResizeEdges::NONE);
}

#[test]
fn a_resize_bar_decodes_each_of_the_eight_authored_grips() {
    for (bottom, left, right, top, border, edges) in [
        (false, false, false, true, Border::Top, ResizeEdges::TOP),
        (
            true,
            false,
            false,
            false,
            Border::Bottom,
            ResizeEdges::BOTTOM,
        ),
        (false, true, false, false, Border::Left, ResizeEdges::LEFT),
        (false, false, true, false, Border::Right, ResizeEdges::RIGHT),
        (
            false,
            true,
            false,
            true,
            Border::UpperLeft,
            ResizeEdges::LEFT | ResizeEdges::TOP,
        ),
        (
            false,
            false,
            true,
            true,
            Border::UpperRight,
            ResizeEdges::RIGHT | ResizeEdges::TOP,
        ),
        (
            true,
            true,
            false,
            false,
            Border::LowerLeft,
            ResizeEdges::LEFT | ResizeEdges::BOTTOM,
        ),
        (
            true,
            false,
            true,
            false,
            Border::LowerRight,
            ResizeEdges::RIGHT | ResizeEdges::BOTTOM,
        ),
    ] {
        let (ui, id) = create(&grip(bottom, left, right, top));
        let g = ui.widget::<ResizeGrip>(id).unwrap();
        assert_eq!(g.border, border);
        assert_eq!(g.border.edges(), edges);
    }
}

#[test]
fn a_resize_bar_captures_the_pointer() {
    let (ui, id) = create(&grip(true, false, false, false));
    assert!(!element(&ui, id).click_through);
}

#[test]
fn the_border_decode_follows_the_retail_branch_order() {
    assert_eq!(Border::decode(true, true, true, true), Border::UpperRight);
    assert_eq!(Border::decode(true, true, false, true), Border::UpperLeft);
    assert_eq!(Border::decode(true, false, false, true), Border::Top);
}

// ── Text and fields ───────────────────────────────────────────────────────

fn text_info(properties: &[(u32, PropertyValue)]) -> ElementInfo {
    let mut info = el(12, 100.0, 20.0);
    info.id = 0x1000_0016;
    info.states.push(direct(properties));
    info
}

#[test]
fn a_text_class_makes_plain_text() {
    let (ui, id) = create(&el(12, 100.0, 40.0));
    let t = ui.widget::<Text>(id).unwrap();
    assert!(!t.selectable());
    let e = element(&ui, id);
    assert!(e.click_through);
    assert!(!e.accepts_focus);
    assert!(!e.captures_pointer_drag);
}

#[test]
fn an_editable_text_is_a_field_in_its_place() {
    let info = text_info(&[
        (0x16, PropertyValue::Bool(true)),
        (0x20, PropertyValue::Bool(true)),
        (0x27, PropertyValue::Bool(true)),
        (0x1E, PropertyValue::Integer(80)),
    ]);
    let (ui, id) = create(&info);
    let f = ui.widget::<Field>(id).unwrap();
    assert_eq!(f.element_id, info.id);
    assert!(f.one_line);
    assert!(f.selectable);
    assert_eq!(f.max_characters, 80);
}

#[test]
fn an_editable_field_folds_its_authored_focus_rails_in() {
    let mut info = text_info(&[(0x16, PropertyValue::Bool(true))]);
    info.width = 306.0;
    info.height = 17.0;
    for (id, x) in [(0x1000_0017, 0.0), (0x1000_0018, 305.0)] {
        let mut rail = el(3, 1.0, 17.0);
        rail.id = id;
        rail.x = x;
        rail.state_media
            .insert("Normal_focussed".into(), media(0x0600_4D67));
        info.children.push(rail);
    }
    let (ui, id) = create(&info);
    let f = ui.widget::<Field>(id).unwrap();
    assert_eq!(f.focus_rail_left_sprite, 0x0600_4D67);
    assert_eq!(f.focus_rail_left_width, 1.0);
    assert_eq!(f.focus_rail_right_sprite, 0x0600_4D67);
    assert_eq!(f.focus_rail_right_width, 1.0);
}

#[test]
fn a_selectable_text_takes_focus_and_the_pointer() {
    let (ui, id) = create(&text_info(&[(0x27, PropertyValue::Bool(true))]));
    assert!(ui.widget::<Text>(id).unwrap().selectable());
    let e = element(&ui, id);
    assert!(!e.click_through);
    assert!(e.accepts_focus);
    assert!(e.captures_pointer_drag);
}

#[test]
fn a_text_class_is_always_text_with_or_without_media() {
    let mut with_media = el(12, 32.0, 16.0);
    with_media
        .state_media
        .insert("Normal".into(), media(0x0000_1234));
    let (ui, id) = create(&with_media);
    assert!(is::<Text>(&ui, id));
    let (ui, id) = create(&el(12, 0.0, 0.0));
    assert!(is::<Text>(&ui, id));
}

/// The lines an authored text lays out at its own width (no font: eight
/// pixels a character).
fn authored_lines(one_line: Option<bool>, text: &str) -> Vec<String> {
    let mut properties = vec![(0x17, string_info(42))];
    if let Some(v) = one_line {
        properties.push((0x20, PropertyValue::Bool(v)));
    }
    let info = text_info(&properties);
    let (mut ui, id) = create_with(&info, &strings(&[(42, text)]));
    let width = element(&ui, id).width;
    let t = ui.widget_mut::<Text>(id).unwrap();
    t.refresh_lines(width)
        .iter()
        .map(|l| l.text.clone())
        .collect()
}

#[test]
fn authored_text_wraps_to_the_element_unless_it_is_one_line() {
    // 100 pixels wide: twelve characters a line.
    let text = "From this screen you can modify";
    assert_eq!(
        authored_lines(None, text),
        ["From this", "screen you", "can modify"]
    );
    assert_eq!(
        authored_lines(Some(false), text),
        ["From this", "screen you", "can modify"]
    );
    assert_eq!(authored_lines(Some(true), text), [text]);
    assert_eq!(authored_lines(None, "Fits"), ["Fits"]);
}

#[test]
fn a_state_string_wraps_like_the_direct_one() {
    let mut info = text_info(&[(0x17, string_info(42))]);
    let mut state = StateInfo::named(0x1000_0019, "Locked");
    state.properties.set_value(0x17, string_info(43));
    info.states.push((0x1000_0019, state));
    let source = strings(&[(42, "Short"), (43, "From this screen you can modify")]);
    let (mut ui, id) = create_with(&info, &source);
    ui.set_retail_state(id, 0x1000_0019);
    let width = element(&ui, id).width;
    let t = ui.widget_mut::<Text>(id).unwrap();
    let lines: Vec<String> = t
        .refresh_lines(width)
        .iter()
        .map(|l| l.text.clone())
        .collect();
    assert_eq!(lines, ["From this", "screen you", "can modify"]);
}

// ── Geometry ──────────────────────────────────────────────────────────────

#[test]
fn rectangle_and_anchors_come_from_the_description() {
    let info = ElementInfo {
        kind: 3,
        x: 5.0,
        y: 21.0,
        width: 150.0,
        height: 16.0,
        left: 1,
        top: 1,
        right: 1,
        bottom: 0,
        ..ElementInfo::default()
    };
    let (ui, id) = create(&info);
    let e = element(&ui, id);
    assert_eq!((e.left, e.top, e.width, e.height), (5.0, 21.0, 150.0, 16.0));
    assert_eq!(
        e.anchors,
        AnchorEdges::LEFT | AnchorEdges::TOP | AnchorEdges::RIGHT
    );
}

#[test]
fn an_imported_descendant_keeps_its_exact_edge_policy() {
    let info = ElementInfo {
        kind: 3,
        x: 10.0,
        y: 20.0,
        width: 30.0,
        height: 40.0,
        left: 4,
        top: 3,
        right: 1,
        bottom: 0,
        original_parent_size: Some((100.0, 200.0)),
        ..ElementInfo::default()
    };
    let (ui, id) = create(&info);
    let policy = element(&ui, id).layout_policy.expect("the edge policy");
    assert_eq!(policy.modes(), (4, 3, 1, 0));
    assert_eq!(policy.original_parent, PixelRect::new(0, 0, 99, 199));
}

#[test]
fn an_imported_root_has_no_edge_policy() {
    let (ui, id) = create(&el(3, 100.0, 200.0));
    assert!(element(&ui, id).layout_policy.is_none());
}

#[test]
fn state_cursors_come_through() {
    let mut info = el(3, 0.0, 0.0);
    let cursor = CursorMedia {
        file: 0x0600_8888,
        hotspot_x: 7,
        hotspot_y: 8,
    };
    info.state_cursors
        .insert("Drag_rollover_accept".into(), cursor);
    let (ui, id) = create(&info);
    assert_eq!(
        element(&ui, id).cursor_for_state("Drag_rollover_accept", false),
        Some(cursor)
    );
}

#[test]
fn the_read_order_is_the_z_order() {
    let mut info = el(3, 0.0, 0.0);
    info.read_order = 7;
    let (ui, id) = create(&info);
    assert_eq!(element(&ui, id).z_order(), 7);
}

// ── Buttons ───────────────────────────────────────────────────────────────

#[test]
fn a_button_class_makes_a_button() {
    let (ui, id) = create(&el(1, 46.0, 18.0));
    assert!(is::<Button>(&ui, id));
}

#[test]
fn a_left_justified_button_with_stateful_faces_aligns_its_label_left() {
    let mut info = el(1, 160.0, 16.0);
    info.h_justify = HJustify::Left;
    info.states.extend([
        named(1, "Normal"),
        named(2, "Normal_rollover"),
        named(3, "Highlight"),
    ]);
    for i in 0..3 {
        let mut face = el(3, 0.0, 0.0);
        face.read_order = i;
        face.state_media
            .insert("Normal_rollover".into(), media(0x0600_0000 + i));
        info.children.push(face);
    }
    let (ui, id) = create(&info);
    let b = ui.widget::<Button>(id).unwrap();
    assert_eq!(b.label_align, LabelAlign::Left);
    assert_eq!(b.label_offset_x, 3.0);
}

#[test]
fn a_centre_justified_button_stays_centred() {
    let mut info = el(1, 160.0, 16.0);
    info.h_justify = HJustify::Center;
    let (ui, id) = create(&info);
    assert_eq!(
        ui.widget::<Button>(id).unwrap().label_align,
        LabelAlign::Center
    );
}

fn lifted_caption_button(string_id: u32, centred: bool) -> ElementInfo {
    let mut info = el(1, 106.0, 80.0);
    info.states
        .extend([named(1, "Normal"), named(6, "Highlight")]);
    let mut caption = el(12, 100.0, 37.0);
    caption.y = 4.0;
    if centred {
        caption.h_justify = HJustify::Center;
    }
    caption
        .states
        .push(direct(&[(0x17, string_info(string_id))]));
    info.children.push(caption);
    let mut marker = el(3, 38.0, 38.0);
    marker.x = 36.0;
    marker.y = 36.0;
    marker
        .state_media
        .insert("Normal".into(), media(0x0600_4D60));
    marker
        .state_media
        .insert("Highlight".into(), media(0x0600_4D61));
    info.children.push(marker);
    info
}

#[test]
fn a_lifted_caption_with_its_own_rectangle_keeps_its_label_box() {
    let (ui, id) = create_with(
        &lifted_caption_button(555, true),
        &strings(&[(555, "Holtburg")]),
    );
    let b = ui.widget::<Button>(id).unwrap();
    assert_eq!(b.label.as_deref(), Some("Holtburg"));
    assert_eq!(b.label_align, LabelAlign::Center);
    assert_eq!(b.label_box, Some((0.0, 4.0, 100.0, 37.0)));
    assert_eq!(
        (b.face_left, b.face_top, b.face_width, b.face_height),
        (36.0, 36.0, 38.0, 38.0)
    );
}

#[test]
fn a_direct_label_beside_a_single_face_keeps_the_face_offset() {
    let mut info = el(1, 305.0, 32.0);
    info.h_justify = HJustify::Left;
    info.states.push(direct(&[(0x17, string_info(777))]));
    info.states.extend([
        named(ac_ui::states::UNSELECTED, "Unselected"),
        named(ac_ui::states::SELECTED, "Selected"),
    ]);
    let mut dot = el(3, 32.0, 32.0);
    dot.state_media
        .insert("Unselected".into(), media(0x0600_6E35));
    dot.state_media
        .insert("Selected".into(), media(0x0600_6E21));
    info.children.push(dot);
    let (ui, id) = create_with(&info, &strings(&[(777, "Aluvian")]));
    let b = ui.widget::<Button>(id).unwrap();
    assert_eq!(b.label.as_deref(), Some("Aluvian"));
    assert_eq!(b.label_box, None);
    assert_eq!(b.label_align, LabelAlign::Left);
    assert_eq!(b.label_offset_x, 36.0, "the face's x + width + 4");
}

#[test]
fn an_own_caption_and_a_bare_text_child_make_a_value_slot() {
    let mut info = el(1, 80.0, 20.0);
    info.states.push(direct(&[(0x17, string_info(111))]));
    info.state_media.insert(String::new(), media(0x0600_0001));
    let mut value = el(12, 60.0, 16.0);
    value.x = 5.0;
    value.y = 2.0;
    info.children.push(value);
    let (mut ui, id) = create_with(&info, &strings(&[(111, "Attribute Credits")]));
    let b = ui.widget_mut::<Button>(id).unwrap();
    assert_eq!(b.label.as_deref(), Some("Attribute Credits"));
    assert_eq!(b.value_label, None);
    assert_eq!(b.value_box, Some((5.0, 2.0, 60.0, 16.0)));
    b.value_label = Some("42".into());
    assert_eq!(b.label.as_deref(), Some("Attribute Credits"));
}

fn value_button(width: f32, string_id: u32, right: bool) -> ElementInfo {
    let mut info = el(1, width, 28.0);
    info.states.push(direct(&[(0x17, string_info(string_id))]));
    let mut value = el(12, 34.0, 28.0);
    value.x = 116.0;
    value.left = 2;
    value.top = 1;
    value.right = 1;
    value.bottom = 1;
    value.original_parent_size = Some((150.0, 28.0));
    if right {
        value.h_justify = HJustify::Right;
    }
    info.children.push(value);
    info
}

#[test]
fn a_value_slot_authored_for_a_narrower_parent_reflows_to_the_button() {
    let (ui, id) = create_with(
        &value_button(231.0, 333, true),
        &strings(&[(333, "Available Skill Credits")]),
    );
    let b = ui.widget::<Button>(id).unwrap();
    assert_eq!(b.label.as_deref(), Some("Available Skill Credits"));
    assert_eq!(
        b.value_box,
        Some((197.0, 0.0, 34.0, 28.0)),
        "116 + (231 - 150)"
    );
    assert_eq!(b.value_align, LabelAlign::Right);
}

#[test]
fn a_value_slot_authored_for_this_width_keeps_its_rectangle() {
    let (ui, id) = create_with(
        &value_button(150.0, 334, false),
        &strings(&[(334, "Health")]),
    );
    assert_eq!(
        ui.widget::<Button>(id).unwrap().value_box,
        Some((116.0, 0.0, 34.0, 28.0))
    );
}

#[test]
fn a_lifted_caption_never_makes_a_value_slot() {
    let (ui, id) = create_with(
        &lifted_caption_button(222, false),
        &strings(&[(222, "Holtburg")]),
    );
    let b = ui.widget::<Button>(id).unwrap();
    assert_eq!(b.label.as_deref(), Some("Holtburg"));
    assert_eq!(b.value_box, None);
    assert_eq!(b.value_label, None);
}

// ── Scrollbars ────────────────────────────────────────────────────────────

fn with_direct_image(mut e: ElementInfo, file: u32) -> ElementInfo {
    let mut s = StateInfo::named(DIRECT_STATE_ID, "");
    s.image = Some(media(file));
    e.states.push((DIRECT_STATE_ID, s));
    e
}

fn arrows(info: &mut ElementInfo, increment: u32, decrement: u32, hide: bool) {
    let mut props = vec![
        (0x77, PropertyValue::Enum(increment)),
        (0x78, PropertyValue::Enum(decrement)),
    ];
    if hide {
        props.push((0x79, PropertyValue::Bool(true)));
    }
    info.states.push(direct(&props));
}

#[test]
fn a_scrollbar_class_makes_a_scrollbar() {
    let (ui, id) = create(&el(11, 16.0, 68.0));
    assert!(is::<Scrollbar>(&ui, id));
}

#[test]
fn a_horizontal_bar_uses_its_direct_fill_and_thumb_children() {
    const TRACK: u32 = 0x0600_4CF6;
    const THUMB: u32 = 0x0600_5DC3;
    let mut info = el(11, 90.0, 14.0);
    info.id = 0x1000_01A4;
    let mut track = with_direct_image(el(3, 90.0, 14.0), TRACK);
    track.id = 4;
    let mut thumb = with_direct_image(el(3, 16.0, 14.0), THUMB);
    thumb.id = 1;
    info.children = vec![track, thumb];
    let (ui, id) = create(&info);
    let bar = ui.widget::<Scrollbar>(id).unwrap();
    assert!(bar.horizontal);
    assert_eq!((bar.track_sprite, bar.thumb_fill_sprite), (0, TRACK));
    assert_eq!(bar.thumb_sprite, THUMB);
    assert_eq!((bar.up_sprite, bar.down_sprite), (0, 0));
}

/// A button at `[x, y, w, h]` with Normal, rollover and pressed art (0: none).
fn arrow_button(
    id: u32,
    [x, y, w, h]: [f32; 4],
    [normal, rollover, pressed]: [u32; 3],
) -> ElementInfo {
    let mut b = el(1, w, h);
    b.id = id;
    b.x = x;
    b.y = y;
    b.state_media.insert("Normal".into(), media(normal));
    if rollover != 0 {
        b.state_media
            .insert("Normal_rollover".into(), media(rollover));
    }
    if pressed != 0 {
        b.state_media
            .insert("Normal_pressed".into(), media(pressed));
    }
    b
}

#[test]
fn a_horizontal_bar_takes_its_authored_arrows_and_hides_when_disabled() {
    const DECREMENT: u32 = 0x1000_0071;
    const INCREMENT: u32 = 0x1000_0072;
    let decrement = with_direct_image(
        arrow_button(
            DECREMENT,
            [63.0, 0.0, 23.0, 36.0],
            [0x0600_4CDC, 0x0600_4CDD, 0x0600_4CDC],
        ),
        0x0600_4CDC,
    );
    let increment = with_direct_image(
        arrow_button(
            INCREMENT,
            [0.0, 0.0, 23.0, 36.0],
            [0x0600_4CDE, 0x0600_4CDF, 0x0600_4CDE],
        ),
        0x0600_4CDE,
    );
    let mut info = el(11, 685.0, 36.0);
    info.id = ac_ui::panels::magic::FAVORITE_SCROLLBAR_ID;
    info.children = vec![decrement, increment];
    arrows(&mut info, INCREMENT, DECREMENT, true);
    let (ui, id) = create(&info);
    let bar = ui.widget::<Scrollbar>(id).unwrap();
    assert!(bar.horizontal);
    assert!(bar.hide_when_disabled);
    assert_eq!(bar.decrement_button_extent, 23.0);
    assert_eq!(bar.increment_button_extent, 23.0);
    assert_eq!(
        (bar.up_sprite, bar.up_rollover_sprite, bar.up_pressed_sprite),
        (0x0600_4CDE, 0x0600_4CDF, 0x0600_4CDE)
    );
    assert_eq!(
        (
            bar.down_sprite,
            bar.down_rollover_sprite,
            bar.down_pressed_sprite
        ),
        (0x0600_4CDC, 0x0600_4CDD, 0x0600_4CDC)
    );
    assert_eq!((bar.track_sprite, bar.thumb_sprite), (0, 0));
}

#[test]
fn a_vertical_bar_with_a_single_sprite_thumb_sets_the_thumb() {
    const THUMB: u32 = 0x0600_5A11;
    let decrement = arrow_button(0x1000_0071, [0.0, 83.0, 37.0, 17.0], [0x0600_4C69, 0, 0]);
    let increment = arrow_button(0x1000_0072, [0.0, 0.0, 37.0, 17.0], [0x0600_4C6C, 0, 0]);
    let mut thumb = arrow_button(1, [0.0, 0.0, 37.0, 39.0], [THUMB, 0x0600_5A12, 0x0600_5A13]);
    let mut normal = StateInfo::named(1, "Normal");
    normal.image = Some(media(THUMB));
    thumb.states.push((1, normal));
    let mut info = el(11, 37.0, 307.0);
    info.children = vec![decrement, increment, thumb];
    arrows(&mut info, 0x1000_0072, 0x1000_0071, false);
    let (ui, id) = create(&info);
    let bar = ui.widget::<Scrollbar>(id).unwrap();
    assert!(!bar.horizontal);
    assert_eq!(bar.thumb_sprite, THUMB);
    assert_eq!((bar.thumb_top_sprite, bar.thumb_bot_sprite), (0, 0));
    assert_eq!(bar.thumb_rollover_sprite, 0x0600_5A12);
    assert_eq!(bar.thumb_pressed_sprite, 0x0600_5A13);
}

#[test]
fn a_vertical_bar_seats_its_arrows_by_designation_not_position() {
    // The decrement (down) arrow is authored at the top, the increment (up)
    // one below it.
    let decrement = arrow_button(
        0x1000_0071,
        [0.0, 0.0, 16.0, 16.0],
        [0x0600_4C69, 0x0600_4C6A, 0x0600_4C6B],
    );
    let increment = arrow_button(
        0x1000_0072,
        [0.0, 32.0, 16.0, 16.0],
        [0x0600_4C6C, 0x0600_4C6D, 0x0600_4C6E],
    );
    let mut info = el(11, 16.0, 48.0);
    info.children = vec![decrement, increment];
    arrows(&mut info, 0x1000_0072, 0x1000_0071, false);
    let (ui, id) = create(&info);
    let bar = ui.widget::<Scrollbar>(id).unwrap();
    assert_eq!(
        (bar.up_sprite, bar.up_rollover_sprite, bar.up_pressed_sprite),
        (0x0600_4C6C, 0x0600_4C6D, 0x0600_4C6E)
    );
    assert_eq!(
        (
            bar.down_sprite,
            bar.down_rollover_sprite,
            bar.down_pressed_sprite
        ),
        (0x0600_4C69, 0x0600_4C6A, 0x0600_4C6B)
    );
}

#[test]
fn a_vertical_thumb_with_slice_children_uses_the_slices() {
    const TOP: u32 = 0x0600_4C60;
    const MID: u32 = 0x0600_4C63;
    const BOT: u32 = 0x0600_4C66;
    let decrement = arrow_button(0x1000_0071, [0.0, 0.0, 16.0, 16.0], [0x0600_4C69, 0, 0]);
    let increment = arrow_button(0x1000_0072, [0.0, 32.0, 16.0, 16.0], [0x0600_4C6C, 0, 0]);
    let slice = |id: u32, y: f32, h: f32, file: u32, rollover: u32, pressed: u32| {
        let mut s = arrow_button(id, [0.0, y, 16.0, h], [file, rollover, pressed]);
        s.kind = 3;
        let mut normal = StateInfo::named(1, "Normal");
        normal.image = Some(media(file));
        s.states.push((1, normal));
        s
    };
    let mut thumb = el(1, 16.0, 16.0);
    thumb.id = 1;
    thumb.children = vec![
        slice(0x1000_0364, 0.0, 3.0, TOP, 0, 0),
        slice(0x1000_0365, 3.0, 10.0, MID, 0x0600_4C64, 0x0600_4C65),
        slice(0x1000_0366, 13.0, 3.0, BOT, 0, 0),
    ];
    let mut info = el(11, 16.0, 73.0);
    info.children = vec![decrement, increment, thumb];
    arrows(&mut info, 0x1000_0072, 0x1000_0071, false);
    let (ui, id) = create(&info);
    let bar = ui.widget::<Scrollbar>(id).unwrap();
    assert!(!bar.horizontal);
    assert_eq!(
        (bar.thumb_top_sprite, bar.thumb_sprite, bar.thumb_bot_sprite),
        (TOP, MID, BOT)
    );
    assert_eq!(bar.thumb_rollover_sprite, 0x0600_4C64);
    assert_eq!(bar.thumb_pressed_sprite, 0x0600_4C65);
}

#[test]
fn a_horizontal_bar_keeps_its_nested_power_meter_fill() {
    let track = {
        let mut t = with_direct_image(el(3, 507.0, 14.0), 0x0600_1919);
        t.id = 4;
        t.state_media.insert(String::new(), media(0x0600_1919));
        t
    };
    let thumb = {
        let mut t = with_direct_image(el(1, 12.0, 14.0), 0x0600_1923);
        t.id = 1;
        t.state_media.insert(String::new(), media(0x0600_1923));
        t
    };
    let fill = {
        let mut f = with_direct_image(el(3, 507.0, 14.0), 0x0600_1200);
        f.id = 2;
        f.state_media.insert(String::new(), media(0x0600_1200));
        f
    };
    let range = {
        let mut r = with_direct_image(el(3, 407.0, 14.0), 0x0600_715E);
        r.id = 0x1000_05EF;
        r.x = 63.0;
        r.original_parent_size = Some((507.0, 14.0));
        r.state_media.insert(String::new(), media(0x0600_715E));
        r
    };
    let mut meter = el(7, 507.0, 14.0);
    meter.id = POWER_CONTROL_ID + 1;
    meter.children = vec![fill, range];
    meter.states.push(direct(&[(0x6F, PropertyValue::Enum(1))]));
    let mut info = el(11, 507.0, 14.0);
    info.id = POWER_CONTROL_ID;
    info.children = vec![track, meter, thumb];
    let (ui, id) = create(&info);
    let bar = ui.widget::<Scrollbar>(id).unwrap();
    assert!(bar.horizontal);
    assert_eq!(bar.thumb_fill_sprite, 0x0600_1919);
    assert_eq!(bar.thumb_sprite, 0x0600_1923);
    assert_eq!(bar.scalar_fill_sprite, 0x0600_1200);
    assert_eq!(bar.scalar_range_sprite, 0x0600_715E);
    assert!(bar.scalar_range_layout_policy.is_some());
    assert!(!bar.scalar_fill_from_right);
}

#[test]
fn a_container_class_is_the_generic_element() {
    let (ui, id) = create(&el(3, 200.0, 16.0));
    assert!(is::<DatElement>(&ui, id));
}

#[test]
fn a_menu_class_makes_a_menu() {
    let (ui, id) = create(&el(6, 46.0, 18.0));
    assert!(is::<Menu>(&ui, id));
}

#[test]
fn the_item_list_class_makes_an_item_list() {
    let mut info = el(0x1000_0031, 32.0, 32.0);
    info.id = 0x1000_01A7;
    let (ui, id) = create(&info);
    assert!(is::<ItemList>(&ui, id));
}

// ── Meters ────────────────────────────────────────────────────────────────

#[test]
fn a_single_image_meter_reads_its_own_track_and_the_fill_child() {
    const BACK: u32 = 0x0600_193E;
    const FILL: u32 = 0x0600_193F;
    let mut meter = el(7, 140.0, 31.0);
    meter.id = 0x1000_01A1;
    meter.state_media.insert(String::new(), media(BACK));
    let mut fill = el(3, 0.0, 0.0);
    fill.read_order = 1;
    fill.state_media.insert(String::new(), media(FILL));
    meter.children.push(fill);
    let (ui, id) = create(&meter);
    let m = ui.widget::<Meter>(id).unwrap();
    assert_eq!((m.back_tile, m.back_left, m.back_right), (BACK, 0, 0));
    assert_eq!((m.front_tile, m.front_left, m.front_right), (FILL, 0, 0));
}

#[test]
fn a_stateful_meter_maps_its_numeric_fill_states_and_hides_the_range() {
    const TRACK: u32 = 0x0600_4D0B;
    const JUMP_STATE: u32 = 0x1000_0042;
    const JUMP: u32 = 0x0600_1354;
    const RANGE: u32 = 0x0600_715E;
    let mut meter = el(7, 600.0, 15.0);
    meter.id = 0x1000_0034;
    meter.state_media.insert(String::new(), media(TRACK));
    let mut fills = el(3, 0.0, 0.0);
    fills.id = 2;
    fills.read_order = 2;
    fills.states.push(named(JUMP_STATE, "JumpMode"));
    fills.state_media.insert("JumpMode".into(), media(JUMP));
    meter.children.push(fills);
    let mut range = el(3, 0.0, 0.0);
    range.id = 0x1000_05EE;
    range.read_order = 1;
    range.state_media.insert(String::new(), media(RANGE));
    meter.children.push(range);
    let (mut ui, id) = create(&meter);
    {
        let m = ui.widget::<Meter>(id).unwrap();
        assert_eq!(m.back_tile, TRACK);
        assert_eq!(m.front_tile, 0);
    }
    assert!(ui.set_retail_state(id, JUMP_STATE));
    let m = ui.widget::<Meter>(id).unwrap();
    assert_eq!(m.active_retail_state_id(), JUMP_STATE);
    assert_eq!(m.front_tile, JUMP);
    assert_ne!(m.back_tile, RANGE);
    assert_ne!(m.front_tile, RANGE);
}

#[test]
fn meter_slices_come_from_the_grandchildren_and_skip_the_overlay() {
    const BACK: [u32; 3] = [0x0600_747E, 0x0600_747F, 0x0600_7480];
    const FRONT: [u32; 3] = [0x0600_7481, 0x0600_7482, 0x0600_7483];
    const OVERLAY: u32 = 0x0600_7490;
    let slices = |read_order: u32, files: [u32; 3]| {
        let mut c = el(3, 0.0, 0.0);
        c.read_order = read_order;
        for (x, f) in [0.0, 10.0, 140.0].into_iter().zip(files) {
            let mut s = ElementInfo {
                x,
                ..ElementInfo::default()
            };
            s.state_media.insert(String::new(), media(f));
            c.children.push(s);
        }
        c
    };
    let back = slices(0, BACK);
    let mut front = slices(1, FRONT);
    let mut overlay = ElementInfo::default();
    overlay.state_media.insert(
        "ShowDetail".into(),
        ImageMedia {
            file: OVERLAY,
            draw_mode: 3,
        },
    );
    front.children.push(overlay);
    let mut meter = el(7, 150.0, 16.0);
    meter.children = vec![back, front];
    let (ui, id) = create(&meter);
    let m = ui.widget::<Meter>(id).unwrap();
    assert_eq!([m.back_left, m.back_tile, m.back_right], BACK);
    assert_eq!([m.front_left, m.front_tile, m.front_right], FRONT);
}

#[test]
fn a_stateful_fill_takes_its_caption_childs_per_state_strings() {
    let mut meter = el(7, 600.0, 15.0);
    meter.id = 0x34;
    meter.state_media.insert(String::new(), media(0x0600_4D0B));
    let mut fill = el(3, 600.0, 15.0);
    fill.id = 2;
    fill.states.push(named(0x1000_0042, "JumpMode"));
    fill.state_media
        .insert("JumpMode".into(), media(0x0600_1354));
    meter.children.push(fill);
    let mut caption = el(12, 600.0, 15.0);
    caption.id = 0x35;
    let mut jump_text = StateInfo::named(0x1000_0042, "JumpMode");
    jump_text.properties.set_value(0x17, string_info(1));
    jump_text.properties.set_value(0x14, PropertyValue::Enum(3));
    caption.states.push((0x1000_0042, jump_text));
    meter.children.push(caption);
    let (mut ui, id) = create_with(&meter, &every_string("Height"));
    assert_eq!(ui.widget::<Meter>(id).unwrap().active_state_label(), None);
    assert!(ui.set_retail_state(id, 0x1000_0042));
    let m = ui.widget::<Meter>(id).unwrap();
    assert_eq!(m.active_state_label(), Some("Height"));
    assert_eq!(m.active_state_label_align(), Some(MeterLabelAlign::Right));
    assert_eq!(m.front_tile, 0x0600_1354);
}

// ── Text justification and colour ─────────────────────────────────────────

fn text_with(edit: impl FnOnce(&mut ElementInfo)) -> (UiRoot, ElementId) {
    let mut info = el(12, 100.0, 20.0);
    edit(&mut info);
    create(&info)
}

#[test]
fn centre_justified_text_is_centred_and_top_aligned() {
    let (ui, id) = text_with(|i| i.h_justify = HJustify::Center);
    let t = ui.widget::<Text>(id).unwrap();
    assert!(t.centered && !t.right_aligned);
    assert_eq!(t.vertical_justify, VJustify::Top);
}

#[test]
fn unauthored_justification_is_the_retail_near_edges() {
    let (ui, id) = text_with(|_| {});
    let t = ui.widget::<Text>(id).unwrap();
    assert!(!t.centered && !t.right_aligned);
    assert_eq!(t.vertical_justify, VJustify::Top);
}

#[test]
fn right_justified_text_is_right_aligned() {
    let (ui, id) = text_with(|i| i.h_justify = HJustify::Right);
    let t = ui.widget::<Text>(id).unwrap();
    assert!(!t.centered && t.right_aligned);
}

#[test]
fn left_justified_text_is_neither_centred_nor_right() {
    let (ui, id) = text_with(|i| i.h_justify = HJustify::Left);
    let t = ui.widget::<Text>(id).unwrap();
    assert!(!t.centered && !t.right_aligned);
}

#[test]
fn top_justified_text_is_top_aligned() {
    let (ui, id) = text_with(|i| {
        i.height = 55.0;
        i.v_justify = VJustify::Top;
    });
    assert_eq!(
        ui.widget::<Text>(id).unwrap().vertical_justify,
        VJustify::Top
    );
}

#[test]
fn a_controller_override_wins_after_the_build() {
    let (mut ui, id) = text_with(|i| i.h_justify = HJustify::Center);
    let t = ui.widget_mut::<Text>(id).unwrap();
    assert!(t.centered);
    t.centered = false;
    assert!(!t.centered);
}

#[test]
fn the_font_colour_is_the_default_colour() {
    let gold = Color::rgba(1.0, 0.82, 0.36, 1.0);
    let (ui, id) = text_with(|i| i.font_color = Some(gold));
    assert_eq!(ui.widget::<Text>(id).unwrap().default_color, gold);
}

#[test]
fn without_a_font_colour_the_default_is_white() {
    let (ui, id) = text_with(|i| i.font_color = None);
    assert_eq!(ui.widget::<Text>(id).unwrap().default_color, Color::WHITE);
}

#[test]
fn text_starts_with_no_padding_and_many_lines() {
    let (ui, id) = text_with(|_| {});
    let t = ui.widget::<Text>(id).unwrap();
    assert_eq!(t.padding, 0.0);
    assert!(!t.one_line);
}

#[test]
fn an_explicit_line_colour_is_not_the_default_colour() {
    let parchment = Color::rgba(0.92, 0.90, 0.82, 1.0);
    let gold = Color::rgba(1.0, 0.82, 0.36, 1.0);
    let (mut ui, id) = text_with(|i| i.font_color = Some(parchment));
    let t = ui.widget_mut::<Text>(id).unwrap();
    t.set_lines(vec![ac_ui::text::Line::new("text", gold)]);
    assert_eq!(t.content_lines()[0].color, gold);
    assert_eq!(t.default_color, parchment);
}

#[test]
fn the_authored_line_follows_the_states_font_colour() {
    let mut info = el(12, 100.0, 20.0);
    info.default_state_id = ac_ui::states::CLOSED;
    info.default_state_name = "Closed".into();
    info.states.push(direct(&[(0x17, string_info(1))]));
    let mut closed = StateInfo::named(ac_ui::states::CLOSED, "Closed");
    closed.properties.set_value(0x1B, rgb(127, 127, 127));
    let mut open = StateInfo::named(ac_ui::states::OPEN, "Open");
    open.properties.set_value(0x1B, rgb(204, 204, 204));
    info.states
        .extend([(ac_ui::states::CLOSED, closed), (ac_ui::states::OPEN, open)]);
    let (mut ui, id) = create_with(&info, &every_string("Spells"));
    let red = |ui: &UiRoot| ui.widget::<Text>(id).unwrap().content_lines()[0].color.r;
    assert!((red(&ui) - 127.0 / 255.0).abs() < 1e-5);
    assert!(ui.set_retail_state(id, ac_ui::states::OPEN));
    assert!((red(&ui) - 204.0 / 255.0).abs() < 1e-5);
}

#[test]
fn authored_text_of_many_lines_wraps_to_the_live_width() {
    let mut info = el(12, 100.0, 80.0);
    info.states.push(direct(&[(0x17, string_info(1))]));
    let (mut ui, id) = create_with(&info, &every_string("one two three four five\nsix"));
    let lines = |ui: &mut UiRoot| -> Vec<String> {
        let w = ui.element(id).unwrap().width;
        ui.widget_mut::<Text>(id)
            .unwrap()
            .refresh_lines(w)
            .iter()
            .map(|l| l.text.clone())
            .collect()
    };
    let narrow = lines(&mut ui);
    assert!(narrow.len() >= 3);
    assert!(narrow.iter().all(|l| l.chars().count() <= 12));
    assert_eq!(narrow.join(" "), "one two three four five six");
    ui.element_mut(id).unwrap().width = 400.0;
    assert_eq!(lines(&mut ui), ["one two three four five", "six"]);
}

#[test]
fn per_state_authored_strings_swap_with_the_state() {
    let mut info = el(12, 270.0, 24.0);
    let mut online = StateInfo::named(0x1000_0054, "Online");
    online.properties.set_value(0x17, string_info(2));
    online.properties.set_value(0x1B, rgb(0, 255, 0));
    let mut offline = StateInfo::named(0x1000_0055, "Offline");
    offline.properties.set_value(0x17, string_info(3));
    info.states
        .extend([(0x1000_0054, online), (0x1000_0055, offline)]);
    let (mut ui, id) = create_with(&info, &strings(&[(2, "Online"), (3, "Offline")]));
    assert!(ui.set_retail_state(id, 0x1000_0054));
    let lines = ui.widget::<Text>(id).unwrap().content_lines();
    assert_eq!(lines.len(), 1);
    assert_eq!(lines[0].text, "Online");
    assert!((lines[0].color.g - 1.0).abs() < 1e-3, "the state's green");
    assert!(ui.set_retail_state(id, 0x1000_0055));
    let lines = ui.widget::<Text>(id).unwrap().content_lines();
    assert_eq!(lines.len(), 1);
    assert_eq!(lines[0].text, "Offline");
}

// ── Element fonts ─────────────────────────────────────────────────────────
// OpenAC `tests/AcDream.App.Tests/UI/Layout/DatWidgetFactoryFontResolveTests.cs`:
// an element naming a font asks the source for it, and takes the default
// font when the source has none; an element naming none never asks.

mod fonts {
    use std::cell::RefCell;
    use std::sync::Arc;

    use ac_dat::objects::{FontCharDesc, LayoutDesc};
    use ac_ui::draw::TextureRef;
    use ac_ui::element_info::ElementInfo;
    use ac_ui::font::{Atlas, UiFont};
    use ac_ui::import::{BuildContext, LayoutSource, build};
    use ac_ui::widgets::{Meter, Text};

    use super::common::root;
    use super::el;

    #[derive(Default)]
    struct Fonts {
        asked: RefCell<Vec<u32>>,
        known: Option<(u32, Arc<UiFont>)>,
    }

    impl LayoutSource for Fonts {
        fn layout(&self, _id: u32) -> Option<Arc<LayoutDesc>> {
            None
        }
        fn font(&self, id: u32) -> Option<Arc<UiFont>> {
            self.asked.borrow_mut().push(id);
            self.known
                .as_ref()
                .filter(|(k, _)| *k == id)
                .map(|(_, f)| f.clone())
        }
    }

    fn font(id: u32) -> Arc<UiFont> {
        let atlas = Atlas {
            texture: TextureRef::Surface(id),
            width: 64,
            height: 64,
        };
        Arc::new(UiFont::new(
            id,
            atlas,
            None,
            16.0,
            12.0,
            std::iter::empty::<FontCharDesc>(),
            0,
            0,
        ))
    }

    fn built(
        info: &ElementInfo,
        source: &Fonts,
        default: Option<Arc<UiFont>>,
    ) -> (ac_ui::root::UiRoot, ac_ui::import::ImportedLayout) {
        let mut ui = root(800.0, 600.0);
        let ctx = BuildContext {
            source,
            default_font: default,
            source_layout_did: 0,
            media_seconds: 0.0,
        };
        let layout = build(&mut ui, info, &ctx);
        (ui, layout)
    }

    fn text_font(info: &ElementInfo, source: &Fonts, default: Option<Arc<UiFont>>) -> Option<u32> {
        let (ui, layout) = built(info, source, default);
        ui.widget::<Text>(layout.root)
            .unwrap()
            .font
            .as_ref()
            .map(|f| f.id)
    }

    fn text(font_did: u32) -> ElementInfo {
        let mut info = el(12, 100.0, 20.0);
        info.font_did = font_did;
        info
    }

    #[test]
    fn with_no_source_font_the_text_takes_the_default() {
        let source = Fonts::default();
        assert_eq!(text_font(&text(0x4000_0001), &source, None), None);
        assert_eq!(
            text_font(&text(0x4000_0001), &source, Some(font(0x4000_0000))),
            Some(0x4000_0000)
        );
    }

    #[test]
    fn an_element_naming_no_font_never_asks() {
        let source = Fonts::default();
        text_font(&text(0), &source, None);
        assert!(source.asked.borrow().is_empty());
    }

    #[test]
    fn a_missing_font_is_asked_once_and_falls_back() {
        let source = Fonts::default();
        assert_eq!(
            text_font(&text(0x4000_0005), &source, Some(font(0x4000_0000))),
            Some(0x4000_0000)
        );
        assert_eq!(*source.asked.borrow(), [0x4000_0005]);
    }

    #[test]
    fn the_source_is_asked_for_the_elements_font() {
        let source = Fonts {
            known: Some((0x4000_0002, font(0x4000_0002))),
            ..Fonts::default()
        };
        assert_eq!(
            text_font(&text(0x4000_0002), &source, Some(font(0x4000_0000))),
            Some(0x4000_0002)
        );
        assert_eq!(*source.asked.borrow(), [0x4000_0002]);
    }

    #[test]
    fn a_controller_font_override_wins_after_the_build() {
        let source = Fonts::default();
        let (mut ui, layout) = built(&text(0x4000_0003), &source, None);
        let t = ui.widget_mut::<Text>(layout.root).unwrap();
        assert!(t.font.is_none());
        t.font = Some(font(9));
        assert_eq!(t.font.as_ref().map(|f| f.id), Some(9));
    }

    #[test]
    fn a_child_element_asks_for_its_font() {
        let source = Fonts::default();
        let mut info = el(3, 200.0, 100.0);
        info.id = 1;
        let mut child = text(0x4000_0004);
        child.id = 2;
        info.children.push(child);
        let (_ui, layout) = built(&info, &source, None);
        assert!(!source.asked.borrow().is_empty());
        assert!(layout.find(2).is_some());
    }

    #[test]
    fn a_meter_asks_for_its_font() {
        let source = Fonts::default();
        let mut meter = el(7, 150.0, 16.0);
        meter.font_did = 0x4000_0006;
        let (ui, layout) = built(&meter, &source, None);
        assert!(!source.asked.borrow().is_empty());
        assert!(ui.widget::<Meter>(layout.root).unwrap().font.is_none());
    }
}

// ── Scrollbar thumbs ──────────────────────────────────────────────────────
// OpenAC `tests/AcDream.App.Tests/UI/Layout/DatWidgetFactoryScrollbarTests.cs`
// (the synthetic bar is OpenAC's `ScrollbarFixtures.FixedThumbBar`: the
// character-creation skills bar, 37 x 307, 17 px arrows, a 37 x 39 thumb).

fn fixed_thumb_bar(proportional: bool, min_thumb: i32) -> ElementInfo {
    let mut bar = el(11, 37.0, 307.0);
    bar.id = 0x1000_03F8;
    bar.states.push(direct(&[
        (0x77, PropertyValue::Enum(0x1000_0071)),
        (0x78, PropertyValue::Enum(0x1000_0072)),
        (0x82, PropertyValue::Bool(proportional)),
        (0x89, PropertyValue::Integer(min_thumb)),
    ]));
    for (id, y, h, sprite) in [
        (1, 0.0, 39.0, 0x0600_1111),
        (0x1000_0071, 290.0, 17.0, 0x0600_2222),
        (0x1000_0072, 0.0, 17.0, 0x0600_3333),
    ] {
        bar.children
            .push(arrow_button(id, [0.0, y, 37.0, h], [sprite, 0, 0]));
    }
    bar
}

#[test]
fn a_bar_authored_not_proportional_keeps_its_thumb_extent() {
    let (ui, id) = create(&fixed_thumb_bar(false, 37));
    let bar = ui.widget::<Scrollbar>(id).unwrap();
    assert!(!bar.horizontal);
    assert!(!bar.proportional);
    assert_eq!(bar.min_thumb_extent, 37.0);
    assert_eq!(bar.thumb_extent, 39.0);
    assert_eq!(bar.decrement_button_extent, 17.0);
    assert_eq!(bar.increment_button_extent, 17.0);
    let mut model = ac_ui::text::Scrollable::default();
    model.set_extents(400, 300, false);
    model.scroll_to_end();
    let (start, extent) = bar.model_thumb_rect(&model, 17.0, 273.0);
    assert_eq!(extent, 39.0);
    assert_eq!(start, 17.0 + 273.0 - 39.0);
}

#[test]
fn a_proportional_bar_never_sizes_its_thumb_below_the_minimum() {
    let (ui, id) = create(&fixed_thumb_bar(true, 37));
    let bar = ui.widget::<Scrollbar>(id).unwrap();
    let mut model = ac_ui::text::Scrollable::default();
    model.set_extents(10_000, 100, false);
    let (_, extent) = bar.model_thumb_rect(&model, 17.0, 273.0);
    assert_eq!(extent, 37.0);
}

mod real_dats {
    //! The fixture cases, on the retail dats.
    use std::path::PathBuf;
    use std::sync::Arc;

    use ac_ui::content::DatContent;
    use ac_ui::font::DEFAULT_FONT_ID;
    use ac_ui::import::{LayoutSource, import};
    use ac_ui::widgets::{Field, Menu, Scrollbar};

    use super::common::root;

    fn content() -> DatContent {
        let dir = std::env::var_os("OPENAC_DAT_DIR")
            .map(PathBuf::from)
            .or_else(ac_dat::default_dat_dir)
            .expect("no dat directory: set OPENAC_DAT_DIR");
        DatContent::new(Arc::new(ac_dat::DatCollection::open(dir).unwrap())).unwrap()
    }

    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_toolbar_builds_its_stack_entry_and_its_horizontal_slider() {
        let c = content();
        let mut ui = root(800.0, 600.0);
        let layout = import(&mut ui, &c, 0x2100_0016, c.font(DEFAULT_FONT_ID)).unwrap();
        let entry = layout.find(0x1000_01A3).unwrap();
        assert!(ui.widget::<Field>(entry).unwrap().right_aligned);
        let bar = layout.find(0x1000_01A4).unwrap();
        let bar = ui.widget::<Scrollbar>(bar).unwrap();
        assert!(bar.horizontal);
        assert_eq!(bar.thumb_fill_sprite, 0x0600_4CF6);
        assert_eq!(bar.thumb_sprite, 0x0600_5DC3);
    }

    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_talk_focus_popup_keeps_its_authored_row_width() {
        let c = content();
        let mut ui = root(800.0, 600.0);
        let layout = import(&mut ui, &c, 0x2100_0006, c.font(DEFAULT_FONT_ID)).unwrap();
        let menu = layout.find(0x1000_0014).unwrap();
        assert_eq!(ui.element(menu).unwrap().width, 46.0);
        assert_eq!(ui.widget::<Menu>(menu).unwrap().column_width, 191.0);
    }
}

mod dormant_real_dats {
    //! Ported from OpenAC `tests/AcDream.App.Tests/UI/Layout/DormantDatWidgetConformanceTests.cs`
    //! and the vendor cases of `DatWidgetFactoryScrollbarTests.cs`, on the
    //! retail dats: authored tab hosts and list boxes stay dormant (nothing
    //! activates them at import), their controllers own them.
    use std::path::PathBuf;
    use std::sync::Arc;

    use ac_ui::content::DatContent;
    use ac_ui::element_info::ElementInfo;
    use ac_ui::font::DEFAULT_FONT_ID;
    use ac_ui::import::{ImportedLayout, LayoutSource, import, import_infos};
    use ac_ui::root::UiRoot;
    use ac_ui::widgets::{ListBox, Scrollbar, TabPanel};

    use super::common::root;

    fn content() -> DatContent {
        let dir = std::env::var_os("OPENAC_DAT_DIR")
            .map(PathBuf::from)
            .or_else(ac_dat::default_dat_dir)
            .expect("no dat directory: set OPENAC_DAT_DIR");
        DatContent::new(Arc::new(ac_dat::DatCollection::open(dir).unwrap())).unwrap()
    }

    fn load(c: &DatContent, layout: u32) -> (UiRoot, ImportedLayout) {
        let mut ui = root(1600.0, 1200.0);
        let l = import(&mut ui, c, layout, c.font(DEFAULT_FONT_ID)).unwrap();
        (ui, l)
    }

    fn dormant_tabs(ui: &UiRoot, id: ac_ui::ElementId) -> usize {
        let host = ui.widget::<TabPanel>(id).expect("a tab panel");
        assert!(!host.behavior_active(), "nobody activated it");
        assert_eq!(host.active_page(), 0);
        assert!(ui.element(id).unwrap().click_through);
        host.tabs().len()
    }

    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_vendor_backdrop_draws_its_media_and_stays_click_through() {
        let c = content();
        let (ui, l) = load(&c, ac_ui::panels::vendor::LAYOUT_ID);
        let backdrop = l.find(0x1000_008D).unwrap();
        assert_eq!(dormant_tabs(&ui, backdrop), 0);
        let host = ui.widget::<TabPanel>(backdrop).unwrap();
        assert_ne!(host.inner.active_media().file, 0, "the authored fill draws");
    }

    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_vendor_tab_host_stays_dormant_and_its_buttons_unbound() {
        let c = content();
        let (ui, l) = load(&c, ac_ui::panels::vendor::LAYOUT_ID);
        let host = l.find(0x1000_00B8).unwrap();
        assert_eq!(dormant_tabs(&ui, host), 3);
        for tab in ui.widget::<TabPanel>(host).unwrap().tabs() {
            let button = l.find(tab.button).unwrap();
            assert!(
                ui.widget::<ac_ui::widgets::Button>(button)
                    .is_none_or(|b| !b.listeners.click),
                "tab button {:#x} was bound at import",
                tab.button
            );
        }
    }

    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_character_and_spellbook_roots_stay_dormant() {
        let c = content();
        for (layout, root_id) in [
            (ac_ui::panels::character::LAYOUT_ID, 0x1000_0227),
            (ac_ui::panels::magic::SPELLBOOK_LAYOUT_ID, 0x1000_02A8),
        ] {
            let (ui, l) = load(&c, layout);
            assert_eq!(ui.element(l.root).unwrap().dat_element_id, root_id);
            assert!(dormant_tabs(&ui, l.root) > 0);
            assert!(ui.retail_state(l.root).is_some(), "it follows states");
        }
    }

    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_combat_tab_host_is_not_taken_over_at_import() {
        let c = content();
        let (ui, l) = load(&c, ac_ui::panels::magic::COMBAT_LAYOUT_ID);
        let host = l.find(0x1000_00A2).unwrap();
        assert_eq!(dormant_tabs(&ui, host), 8);
        let children = ui.children(host);
        assert_eq!(children.len(), 16);
        assert!(children.iter().all(|c| ui.element(*c).unwrap().visible()));
    }

    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_effects_list_stays_dormant_with_no_viewport() {
        let c = content();
        let (ui, l) = load(&c, ac_ui::panels::indicators::EFFECTS_LAYOUT_ID);
        let list = l.find(ac_ui::panels::indicators::LIST_ID).unwrap();
        let b = ui.widget::<ListBox>(list).unwrap();
        assert!(!b.templates.is_empty());
        assert!(ui.children(list).is_empty(), "no viewport injected");
        assert!(b.viewport_id().is_none());
        assert!(ui.element(list).unwrap().click_through);
    }

    #[test]
    #[ignore = "needs the retail dat files"]
    fn no_authored_list_box_has_children() {
        fn walk(e: &ElementInfo, layout: u32, seen: &mut usize) {
            if e.kind == 5 {
                *seen += 1;
                assert!(
                    e.children.is_empty(),
                    "{layout:#x}: list box {:#x} authors {} children",
                    e.id,
                    e.children.len()
                );
            }
            for child in &e.children {
                walk(child, layout, seen);
            }
        }
        let c = content();
        let mut seen = 0;
        for layout in 0x2100_0000..=0x2100_00FF_u32 {
            if c.layout(layout).is_none() {
                continue;
            }
            if let Some(info) = import_infos(&c, layout) {
                walk(&info, layout, &mut seen);
            }
        }
        assert!(seen >= 10, "the sweep saw only {seen} list boxes");
    }

    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_vendor_item_bar_composes_its_thumb_from_three_slices() {
        let c = content();
        let (ui, l) = load(&c, ac_ui::panels::vendor::LAYOUT_ID);
        let bar = l.find(ac_ui::panels::vendor::ITEM_SCROLLBAR_ID).unwrap();
        let bar = ui.widget::<Scrollbar>(bar).unwrap();
        assert!(bar.horizontal);
        assert_eq!(
            (bar.thumb_top_sprite, bar.thumb_sprite, bar.thumb_bot_sprite),
            (0x0600_4C80, 0x0600_4C83, 0x0600_4C86)
        );
        assert_eq!(bar.thumb_top_rollover_sprite, 0x0600_4C81);
        assert_eq!(bar.thumb_pressed_sprite, 0x0600_4C85);
        assert!(bar.proportional);
        assert_eq!(bar.min_thumb_extent, 16.0);
        assert_eq!(bar.thumb_extent, 16.0);
    }
}

/// A character-creation attribute slider fills in another colour while its
/// attribute is locked: the bar's state picks the fill art. OpenAC
/// `DatWidgetFactoryScrollbarTests.
/// HorizontalBar_FillFollowsTheBarsStateWhenTheFillAuthorsArtPerState`.
#[test]
fn a_scalar_bars_fill_follows_the_bar_state_when_it_authors_art_per_state() {
    const UNLOCKED: u32 = 0x1000_0018;
    const LOCKED: u32 = 0x1000_0019;
    let mut bar = el(11, 291.0, 39.0);
    bar.default_state_id = UNLOCKED;
    for (id, name) in [(UNLOCKED, "Unlocked"), (LOCKED, "Locked")] {
        let mut s = StateInfo::named(id, name);
        s.pass_to_children = true;
        bar.set_state(id, s);
    }
    let mut thumb = el(1, 37.0, 39.0);
    thumb.id = 1;
    let mut thumb_state = StateInfo::named(DIRECT_STATE_ID, "");
    thumb_state.image = Some(media(0x0600_4D34));
    thumb.set_state(DIRECT_STATE_ID, thumb_state);
    let mut track = el(3, 291.0, 39.0);
    track.id = 4;
    track.default_state_id = UNLOCKED;
    for (id, name, file) in [
        (UNLOCKED, "Unlocked", 0x0600_4D39),
        (LOCKED, "Locked", 0x0600_4D3A),
    ] {
        let mut s = StateInfo::named(id, name);
        s.image = Some(media(file));
        track.set_state(id, s);
    }
    bar.children = vec![thumb, track];

    let (mut ui, id) = create(&bar);
    let sprite = |ui: &UiRoot| ui.widget::<Scrollbar>(id).unwrap().thumb_fill_sprite;
    assert_eq!(sprite(&ui), 0x0600_4D39);
    assert!(ui.set_retail_state(id, LOCKED));
    assert_eq!(sprite(&ui), 0x0600_4D3A);
    assert!(ui.set_retail_state(id, UNLOCKED));
    assert_eq!(sprite(&ui), 0x0600_4D39);
    assert!(!ui.set_retail_state(id, 0x1000_0099));
    assert_eq!(sprite(&ui), 0x0600_4D39);
}

#[derive(Debug)]
struct FillSizes(Vec<(u32, (u32, u32))>);

impl ac_ui::draw::SpriteSizes for FillSizes {
    fn sprite_size(&self, file: u32) -> Option<(u32, u32)> {
        self.0.iter().find(|(f, _)| *f == file).map(|(_, s)| *s)
    }
}

/// The left edge and width of every quad `ui` draws with image `file`.
fn drawn_spans(ui: &mut UiRoot, sizes: &FillSizes, file: u32) -> Vec<(f32, f32)> {
    let list = ui.draw((1600.0, 1200.0), sizes);
    list.commands()
        .filter_map(|c| match c {
            ac_ui::draw::DrawCmd::Sprite { texture, quad, .. }
                if *texture == ac_ui::draw::TextureRef::Surface(file) =>
            {
                Some((quad.x, quad.w))
            }
            _ => None,
        })
        .collect()
}

/// The character-creation attribute slider (layout 0x2100004C, element
/// 0x100002EA): its child 4 is not a track but a fill from the bar's start
/// to the jewel's centre, so locking colours the bar up to the jewel and
/// no further. 291 wide, jewel 37: at half the fill is 127 + 18 pixels.
/// Research note "scrollbar fill up to the thumb"; OpenAC
/// `DatWidgetFactoryScrollbarTests.HorizontalBar_Child4FillsOnlyUpToTheThumbsCentre`.
#[test]
fn a_scalar_bars_child_4_fills_only_up_to_the_thumbs_centre_in_either_state() {
    const UNLOCKED: u32 = 0x1000_0018;
    const LOCKED: u32 = 0x1000_0019;
    const TAN: u32 = 0x0600_4D39;
    const ORANGE: u32 = 0x0600_4D3A;
    const JEWEL: u32 = 0x0600_4D34;
    let mut bar = el(11, 291.0, 39.0);
    bar.default_state_id = UNLOCKED;
    for (id, name) in [(UNLOCKED, "Unlocked"), (LOCKED, "Locked")] {
        let mut s = StateInfo::named(id, name);
        s.pass_to_children = true;
        bar.set_state(id, s);
    }
    let mut thumb = el(1, 37.0, 39.0);
    thumb.id = 1;
    let mut thumb_state = StateInfo::named(DIRECT_STATE_ID, "");
    thumb_state.image = Some(media(JEWEL));
    thumb.set_state(DIRECT_STATE_ID, thumb_state);
    let mut fill = el(3, 100.0, 39.0);
    fill.id = 4;
    fill.default_state_id = UNLOCKED;
    for (id, name, file) in [(UNLOCKED, "Unlocked", TAN), (LOCKED, "Locked", ORANGE)] {
        let mut s = StateInfo::named(id, name);
        s.image = Some(media(file));
        fill.set_state(id, s);
    }
    bar.children = vec![thumb, fill];
    let sizes = FillSizes(vec![(TAN, (7, 39)), (ORANGE, (7, 39)), (JEWEL, (37, 39))]);

    let (mut ui, id) = create(&bar);
    for (position, width) in [(0.0, 18.0), (0.5, 145.0), (1.0, 272.0)] {
        ui.widget_mut::<Scrollbar>(id)
            .unwrap()
            .set_scalar_position(position);
        assert_eq!(drawn_spans(&mut ui, &sizes, TAN), [(0.0, width)]);
    }
    ui.widget_mut::<Scrollbar>(id)
        .unwrap()
        .set_scalar_position(0.5);
    assert!(ui.set_retail_state(id, LOCKED));
    assert_eq!(drawn_spans(&mut ui, &sizes, ORANGE), [(0.0, 145.0)]);
    assert!(drawn_spans(&mut ui, &sizes, TAN).is_empty());
    assert!(ui.set_retail_state(id, UNLOCKED));
    assert_eq!(drawn_spans(&mut ui, &sizes, TAN), [(0.0, 145.0)]);
}

/// A bar with art of its own keeps it as the full-width track and still
/// draws its child 4 up to the thumb's centre (the combat power bar,
/// layout 0x21000007: 707 wide, thumb 12, child 4 authored 0 by 0). OpenAC
/// `DatWidgetFactoryScrollbarTests.HorizontalBar_WithItsOwnTrack_StillFillsItsChild4ToTheThumb`.
#[test]
fn a_scalar_bar_with_its_own_track_still_fills_its_child_4_to_the_thumb() {
    const TRACK: u32 = 0x0600_74CA;
    const FILL: u32 = 0x0600_1919;
    const THUMB: u32 = 0x0600_1923;
    let mut bar = el(11, 707.0, 14.0);
    let mut bar_state = StateInfo::named(DIRECT_STATE_ID, "");
    bar_state.image = Some(media(TRACK));
    bar.set_state(DIRECT_STATE_ID, bar_state);
    let mut thumb = el(1, 12.0, 14.0);
    thumb.id = 1;
    let mut thumb_state = StateInfo::named(DIRECT_STATE_ID, "");
    thumb_state.image = Some(media(THUMB));
    thumb.set_state(DIRECT_STATE_ID, thumb_state);
    let mut fill = el(3, 0.0, 0.0);
    fill.id = 4;
    let mut fill_state = StateInfo::named(DIRECT_STATE_ID, "");
    fill_state.image = Some(media(FILL));
    fill.set_state(DIRECT_STATE_ID, fill_state);
    bar.children = vec![thumb, fill];
    let sizes = FillSizes(vec![(TRACK, (60, 13)), (FILL, (60, 13)), (THUMB, (12, 14))]);

    let (mut ui, id) = create(&bar);
    ui.widget_mut::<Scrollbar>(id)
        .unwrap()
        .set_scalar_position(0.5);
    assert_eq!(drawn_spans(&mut ui, &sizes, TRACK), [(0.0, 707.0)]);
    assert_eq!(drawn_spans(&mut ui, &sizes, FILL), [(0.0, 353.0)]);
}
