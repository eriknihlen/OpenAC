//! Layout import: descriptions → widgets, and LayoutDesc → descriptions.
//!
//! Ported from OpenAC `tests/AcDream.App.Tests/UI/Layout/LayoutImporterTests.cs`,
//! `tests/AcDream.App.Tests/UI/LayoutImporterMountTests.cs` and
//! `tests/AcDream.App.Tests/UI/DatWidgetFactoryZOrderTests.cs`. The synthetic
//! LayoutDesc cases below are new and pin the inheritance rules of
//! `LayoutImporter.Resolve`/`IncorporateChildren`.

mod common;

use std::collections::HashMap;
use std::sync::Arc;

use ac_dat::objects::{
    DIRECT_STATE_ID, DrawModeType, ElementDesc, IncorporationFlags, LayoutDesc, Media, MediaDesc,
    PropertyValue, StateDesc, StringInfo,
};
use ac_ui::element_info::{ElementInfo, ImageMedia, StateInfo};
use ac_ui::event::{UiEvent, kind};
use ac_ui::import::{
    BuildContext, ImportedLayout, LayoutSource, build, create, import, import_infos,
    import_infos_rooted, should_mount_base_children,
};
use ac_ui::root::UiRoot;
use ac_ui::states;
use ac_ui::text::HJustify;
use ac_ui::widgets::{Button, DatElement, Text};
use common::*;

#[derive(Debug, Default)]
struct Source {
    layouts: HashMap<u32, Arc<LayoutDesc>>,
    strings: HashMap<u32, String>,
}

impl LayoutSource for Source {
    fn layout(&self, id: u32) -> Option<Arc<LayoutDesc>> {
        self.layouts.get(&id).cloned()
    }
    fn string(&self, info: &StringInfo) -> Option<String> {
        self.strings.get(&info.string_id).cloned()
    }
}

fn info(id: u32, kind: u32, x: f32, y: f32, w: f32, h: f32) -> ElementInfo {
    ElementInfo {
        id,
        kind,
        x,
        y,
        width: w,
        height: h,
        ..ElementInfo::default()
    }
}

fn build_from(
    root_info: ElementInfo,
    children: Vec<ElementInfo>,
    source: &Source,
) -> (UiRoot, ImportedLayout) {
    let mut ui = root(800.0, 600.0);
    let mut r = root_info;
    r.children = children;
    let ctx = BuildContext {
        source,
        default_font: None,
        source_layout_did: 0,
        media_seconds: 0.0,
    };
    let imported = build(&mut ui, &r, &ctx);
    let top = ui.root();
    ui.add_child(top, imported.root);
    (ui, imported)
}

fn slice_container(id: u32, read_order: u32, files: [u32; 3]) -> ElementInfo {
    let mut c = ElementInfo {
        id,
        kind: 3,
        read_order,
        ..ElementInfo::default()
    };
    for (x, f) in [0.0, 10.0, 140.0].into_iter().zip(files) {
        let mut s = ElementInfo {
            x,
            ..ElementInfo::default()
        };
        s.state_media.insert(
            String::new(),
            ImageMedia {
                file: f,
                draw_mode: 1,
            },
        );
        c.children.push(s);
    }
    c
}

#[test]
fn a_meter_lands_at_its_rectangle_as_a_meter() {
    let src = Source::default();
    let (ui, t) = build_from(
        info(0x1000_05F9, 3, 0.0, 0.0, 160.0, 58.0),
        vec![info(0x1000_00E6, 7, 5.0, 5.0, 150.0, 16.0)],
        &src,
    );
    let m = t.find(0x1000_00E6).unwrap();
    assert!(ui.widget::<ac_ui::widgets::Meter>(m).is_some());
    let e = ui.element(m).unwrap();
    assert_eq!((e.left, e.width), (5.0, 150.0));
}

#[test]
fn dat_element_ids_are_stamped_on_the_root_and_children() {
    let src = Source::default();
    let (ui, t) = build_from(
        info(0x1000_05F9, 3, 0.0, 0.0, 160.0, 58.0),
        vec![info(0x1000_01C6, 0x1000_0031, 5.0, 5.0, 72.0, 72.0)],
        &src,
    );
    assert_eq!(ui.element(t.root).unwrap().dat_element_id, 0x1000_05F9);
    let list = t.find(0x1000_01C6).unwrap();
    assert_eq!(ui.element(list).unwrap().dat_element_id, 0x1000_01C6);
    assert!(ui.widget::<ac_ui::widgets::ItemList>(list).is_some());
}

#[test]
fn a_zero_size_text_prototype_is_still_built() {
    let src = Source::default();
    let (ui, t) = build_from(
        info(0x1000_0001, 3, 0.0, 0.0, 160.0, 58.0),
        vec![
            info(0x2000_0001, 12, 0.0, 0.0, 0.0, 0.0),
            info(0x2000_0002, 3, 0.0, 0.0, 100.0, 20.0),
        ],
        &src,
    );
    assert!(ui.widget::<Text>(t.find(0x2000_0001).unwrap()).is_some());
    assert!(t.find(0x2000_0002).is_some());
}

#[test]
fn text_pass_to_children_states_reach_its_children() {
    let mut tab = info(0x1000_02A9, 12, 0.0, 0.0, 100.0, 25.0);
    tab.default_state_id = states::CLOSED;
    tab.default_state_name = "Closed".into();
    for (id, name) in [(states::CLOSED, "Closed"), (states::OPEN, "Open")] {
        let mut s = StateInfo::named(id, name);
        s.pass_to_children = true;
        tab.set_state(id, s);
    }
    let mut cap = info(0x1000_0439, 3, 0.0, 0.0, 17.0, 25.0);
    for (id, name, file) in [
        (states::CLOSED, "Closed", 0x0600_5D93),
        (states::OPEN, "Open", 0x0600_5D92),
    ] {
        let mut s = StateInfo::named(id, name);
        s.image = Some(ImageMedia { file, draw_mode: 3 });
        cap.set_state(id, s);
        cap.state_media
            .insert(name.into(), ImageMedia { file, draw_mode: 3 });
    }
    tab.children.push(cap);
    let src = Source::default();
    let (mut ui, t) = build_from(info(1, 3, 0.0, 0.0, 100.0, 25.0), vec![tab], &src);
    let text = t.find(0x1000_02A9).unwrap();
    let child = t.find(0x1000_0439).unwrap();
    assert!(ui.widget::<Text>(text).is_some());
    assert_eq!(ui.children(text), [child]);
    let c = ui.widget::<DatElement>(child).unwrap();
    assert_eq!(
        (c.active_retail_state_id(), c.active_media().file),
        (states::CLOSED, 0x0600_5D93)
    );
    assert!(ui.set_retail_state(text, states::OPEN));
    let c = ui.widget::<DatElement>(child).unwrap();
    assert_eq!(
        (c.active_retail_state_id(), c.active_media().file),
        (states::OPEN, 0x0600_5D92)
    );
}

#[test]
fn a_meter_imports_its_text_children_but_not_its_slice_containers() {
    let mut meter = info(0x1000_0236, 7, 0.0, 0.0, 200.0, 13.0);
    meter.children.push(slice_container(
        0x1000_0237,
        0,
        [0x0600_747E, 0x0600_747F, 0x0600_7480],
    ));
    meter.children.push(slice_container(
        0x1000_0238,
        1,
        [0x0600_7481, 0x0600_7482, 0x0600_7483],
    ));
    meter
        .children
        .push(info(0x1000_0239, 12, 0.0, 0.0, 120.0, 13.0));
    let mut value = info(0x1000_023A, 12, 0.0, 0.0, 200.0, 13.0);
    value.h_justify = HJustify::Right;
    meter.children.push(value);
    let src = Source::default();
    let (ui, t) = build_from(
        info(0x1000_05F9, 3, 0.0, 0.0, 210.0, 20.0),
        vec![meter],
        &src,
    );
    assert!(t.find(0x1000_0237).is_none() && t.find(0x1000_0238).is_none());
    let m = t.find(0x1000_0236).unwrap();
    assert_eq!(ui.children(m).len(), 2);
    assert!(ui.widget::<Text>(t.find(0x1000_0239).unwrap()).is_some());
    assert!(
        ui.widget::<Text>(t.find(0x1000_023A).unwrap())
            .unwrap()
            .right_aligned
    );

    let mut vitals = info(0x1000_00E6, 7, 0.0, 0.0, 150.0, 16.0);
    vitals
        .children
        .push(slice_container(0x1000_00E7, 0, [1, 2, 3]));
    vitals
        .children
        .push(slice_container(0x1000_00E8, 1, [4, 5, 6]));
    let (ui, t) = build_from(
        info(0x1000_05F9, 3, 0.0, 0.0, 160.0, 58.0),
        vec![vitals],
        &src,
    );
    assert!(ui.children(t.find(0x1000_00E6).unwrap()).is_empty());
}

#[test]
fn tooltip_properties_are_copied_and_the_text_resolved() {
    let mut trigger = info(2, 3, 0.0, 0.0, 40.0, 20.0);
    trigger.tooltip_enabled = true;
    trigger.tooltip_root_element_id = 0x1000_0487;
    trigger.tooltip_layout_did = 0x2100_0041;
    trigger.tooltip_text_child_element_id = 0x1000_0396;
    trigger.tooltip_delay_seconds = Some(0.5);
    trigger.tooltip_text = Some(StringInfo {
        string_id: 0x0AAA_AAAA,
        table_id: 0x2300_0003,
        ..StringInfo::default()
    });
    let src = Source {
        strings: HashMap::from([(0x0AAA_AAAA, "Rotate left.".to_owned())]),
        ..Source::default()
    };
    let (ui, t) = build_from(
        info(1, 3, 0.0, 0.0, 100.0, 40.0),
        vec![trigger.clone()],
        &src,
    );
    let a = &ui.element(t.find(2).unwrap()).unwrap().authored;
    assert!(a.tooltip_enabled);
    assert_eq!(
        (
            a.tooltip_root_element_id,
            a.tooltip_layout_did,
            a.tooltip_text_child_element_id
        ),
        (0x1000_0487, 0x2100_0041, 0x1000_0396)
    );
    assert_eq!(a.tooltip_delay_seconds, Some(0.5));
    assert_eq!(a.tooltip_text.as_deref(), Some("Rotate left."));

    let (ui, t) = build_from(
        info(1, 3, 0.0, 0.0, 100.0, 40.0),
        vec![trigger],
        &Source::default(),
    );
    assert_eq!(
        ui.element(t.find(2).unwrap())
            .unwrap()
            .authored
            .tooltip_text,
        None
    );

    let (ui, t) = build_from(
        info(1, 3, 0.0, 0.0, 100.0, 40.0),
        vec![info(2, 3, 0.0, 0.0, 40.0, 20.0)],
        &Source::default(),
    );
    assert_eq!(
        ui.element(t.find(2).unwrap()).unwrap().authored,
        Default::default()
    );
}

#[test]
fn authored_invisible_hides_initially_without_latching() {
    let mut hidden = info(2, 3, 0.0, 0.0, 20.0, 20.0);
    hidden.invisible = true;
    let src = Source::default();
    let (mut ui, t) = build_from(info(1, 3, 0.0, 0.0, 100.0, 40.0), vec![hidden], &src);
    let id = t.find(2).unwrap();
    assert!(ui.element(id).unwrap().authored.invisible);
    assert!(!ui.element(id).unwrap().visible());
    ui.set_visible(id, true);
    assert!(ui.element(id).unwrap().visible());
}

#[test]
fn base_children_mount_only_for_a_bare_inheritor() {
    assert!(should_mount_base_children(0, 0, 5));
    assert!(!should_mount_base_children(0, 1, 5));
    assert!(!should_mount_base_children(2, 0, 5));
    assert!(!should_mount_base_children(0, 0, 0));
}

#[test]
fn z_order_is_read_order_with_z_level_drawing_behind() {
    let src = Source::default();
    let ctx = BuildContext {
        source: &src,
        default_font: None,
        source_layout_did: 0,
        media_seconds: 0.0,
    };
    let z = |read_order, z_level| {
        let i = ElementInfo {
            kind: 3,
            read_order,
            z_level,
            ..ElementInfo::default()
        };
        create(&i, &ctx).0.z_order
    };
    assert!(z(4, 100) < z(1, 0));
    assert_eq!(z(5, 0), 5);
    assert!(z(2, 2) < z(7, 2));
}

// ── LayoutDesc → descriptions ───────────────────────────────────────────

fn state(media: Vec<Media>) -> StateDesc {
    StateDesc {
        state_id: 0,
        pass_to_children: false,
        incorporation_flags: IncorporationFlags(0x3E),
        properties: Vec::new(),
        media: media
            .into_iter()
            .map(|m| MediaDesc {
                declared_type: 0,
                media: m,
            })
            .collect(),
    }
}

fn image(file: u32) -> Media {
    Media::Image {
        file,
        draw_mode: DrawModeType::NORMAL,
    }
}

fn desc(id: u32, kind: u32, rect: (i32, i32, i32, i32), media: Vec<Media>) -> ElementDesc {
    ElementDesc {
        state_desc: state(media),
        read_order: 0,
        element_id: id,
        element_type: kind,
        base_element: 0,
        base_layout_id: 0,
        default_state: 0,
        x: rect.0,
        y: rect.1,
        width: rect.2,
        height: rect.3,
        z_level: 0,
        left_edge: 1,
        top_edge: 1,
        right_edge: 2,
        bottom_edge: 2,
        states: Vec::new(),
        children: Vec::new(),
    }
}

fn layout(id: u32, elements: Vec<ElementDesc>) -> Arc<LayoutDesc> {
    Arc::new(LayoutDesc {
        id,
        width: 800,
        height: 600,
        elements: elements.into_iter().map(|e| (e.element_id, e)).collect(),
    })
}

#[test]
fn a_single_top_element_is_the_root_several_are_wrapped() {
    let mut src = Source::default();
    src.layouts.insert(
        1,
        layout(1, vec![desc(10, 3, (0, 0, 100, 50), vec![image(5)])]),
    );
    let r = import_infos(&src, 1).unwrap();
    assert_eq!((r.id, r.width), (10, 100.0));
    assert_eq!(r.state_media[""].file, 5);

    src.layouts.insert(
        2,
        layout(
            2,
            vec![
                desc(10, 3, (0, 0, 100, 50), vec![]),
                desc(11, 3, (5, 5, 10, 10), vec![]),
            ],
        ),
    );
    let r = import_infos(&src, 2).unwrap();
    assert_eq!(
        (r.id, r.kind, r.width, r.height, r.children.len()),
        (0, 3, 800.0, 600.0, 2)
    );
    assert!(
        r.children
            .iter()
            .all(|c| c.original_parent_size == Some((800.0, 600.0)))
    );
    assert!(import_infos(&src, 3).is_none());
}

#[test]
fn a_same_layout_prototype_without_media_is_skipped() {
    let mut proto = desc(20, 12, (0, 0, 0, 0), vec![]);
    proto.state_desc.properties = Vec::new();
    let mut user = desc(21, 12, (5, 5, 30, 10), vec![]);
    user.base_element = 20;
    user.base_layout_id = 1;
    let mut src = Source::default();
    src.layouts.insert(1, layout(1, vec![proto, user]));
    let r = import_infos(&src, 1).unwrap();
    assert_eq!(
        r.id, 21,
        "the prototype was dropped, leaving one top element"
    );
}

#[test]
fn inheritance_merges_the_base_and_mounts_its_children_under_a_bare_inheritor() {
    let mut base = desc(0x100, 3, (0, 0, 60, 20), vec![image(0x0600_0001)]);
    let mut base_child = desc(0x101, 3, (2, 2, 10, 10), vec![image(0x0600_0002)]);
    base_child.read_order = 0;
    base.children.push((0x101, base_child));
    base.z_level = 7;
    let mut src = Source::default();
    src.layouts
        .insert(0x2100_0040, layout(0x2100_0040, vec![base]));

    let mut user = desc(0x200, 0, (5, 6, 0, 0), vec![]);
    user.base_element = 0x100;
    user.base_layout_id = 0x2100_0040;
    let mut extra = desc(0x202, 3, (0, 0, 4, 4), vec![]);
    extra.read_order = 3;
    let mut overlay = desc(0x101, 0, (9, 9, 0, 0), vec![]);
    overlay.read_order = 0;
    src.layouts.insert(1, layout(1, vec![user.clone()]));

    let r = import_infos(&src, 1).unwrap();
    assert_eq!(
        (r.id, r.kind, r.x, r.y, r.width, r.height),
        (0x200, 3, 5.0, 6.0, 60.0, 20.0)
    );
    assert_eq!(r.state_media[""].file, 0x0600_0001, "media inherited");
    assert_eq!(
        r.z_level, 0,
        "a bare inheritor mounting base children keeps its own z level"
    );
    assert_eq!(r.children.len(), 1);
    assert_eq!(r.children[0].id, 0x101);
    assert_eq!(r.children[0].state_media[""].file, 0x0600_0002);

    // An overlay of the base child and a new child: the overlay merges in
    // place, the new child comes after with its read order bumped past the
    // retained base children.
    user.children.push((0x101, overlay));
    user.children.push((0x202, extra));
    src.layouts.insert(1, layout(1, vec![user]));
    let r = import_infos(&src, 1).unwrap();
    let ids: Vec<u32> = r.children.iter().map(|c| c.id).collect();
    assert_eq!(ids, [0x101, 0x202]);
    assert_eq!((r.children[0].x, r.children[0].width), (9.0, 10.0));
    assert_eq!(r.children[0].original_parent_size, Some((60.0, 20.0)));
    assert_eq!(
        r.children[1].read_order, 3,
        "no base child was retained unchanged"
    );
}

#[test]
fn a_base_cycle_is_cut() {
    let mut a = desc(1, 3, (0, 0, 10, 10), vec![]);
    a.base_element = 2;
    a.base_layout_id = 9;
    let mut b = desc(2, 3, (0, 0, 20, 20), vec![]);
    b.base_element = 1;
    b.base_layout_id = 9;
    let mut src = Source::default();
    src.layouts.insert(9, layout(9, vec![a, b]));
    let r = import_infos_rooted(&src, 9, 1).unwrap();
    assert_eq!(r.id, 1);
}

#[test]
fn a_state_program_is_kept_and_a_looping_animation_recognised() {
    let mut d = desc(1, 3, (0, 0, 10, 10), vec![]);
    d.states.push((
        states::SHOW_DETAIL,
        StateDesc {
            state_id: states::SHOW_DETAIL,
            pass_to_children: true,
            incorporation_flags: IncorporationFlags(0),
            properties: Vec::new(),
            media: vec![
                MediaDesc {
                    declared_type: 3,
                    media: Media::Animation {
                        duration: 1.0,
                        draw_mode: DrawModeType::NORMAL,
                        frames: vec![7, 8],
                    },
                },
                MediaDesc {
                    declared_type: 6,
                    media: Media::Jump {
                        jump_item_index: 0,
                        probability: 1.0,
                    },
                },
            ],
        },
    ));
    let mut src = Source::default();
    src.layouts.insert(1, layout(1, vec![d]));
    let r = import_infos(&src, 1).unwrap();
    let s = r.state(states::SHOW_DETAIL).unwrap();
    assert_eq!(s.name, "ShowDetail");
    assert!(s.pass_to_children);
    assert_eq!(s.media_steps.len(), 2);
    assert_eq!(s.looping_animation.as_ref().unwrap().frames, [7, 8]);
    assert!(r.state(DIRECT_STATE_ID).is_some());
}

#[test]
fn authored_text_and_font_colour_come_through_the_factory() {
    let mut d = desc(1, 12, (0, 0, 100, 16), vec![]);
    d.state_desc.properties.push((
        0x17,
        ac_dat::objects::Property {
            master_property_id: 0x17,
            value: PropertyValue::StringInfo(StringInfo {
                string_id: 5,
                table_id: 0x2300_0001,
                ..StringInfo::default()
            }),
        },
    ));
    let mut src = Source::default();
    src.strings.insert(5, "Hello".into());
    src.layouts.insert(1, layout(1, vec![d]));
    let mut ui = root(800.0, 600.0);
    let t = import(&mut ui, &src, 1, None).unwrap();
    let text = ui.widget::<Text>(t.root).unwrap();
    assert_eq!(text.content_lines()[0].text, "Hello");
    assert_eq!(ui.element(t.root).unwrap().source_layout_did, 1);
}

/// A captioned spin whose arrows share its Normal state name: the arrows
/// are buttons of their own, with their own rollover and hit area. Ported
/// from OpenAC `LayoutImporterTests`
/// `ButtonChildThatIsItselfAButton_IsBuiltAsItsOwnControl_NotFoldedIntoTheParentFace`.
#[test]
fn a_button_child_that_is_a_button_is_its_own_control_not_part_of_the_face() {
    fn arrow(id: u32, x: f32) -> ElementInfo {
        let mut a = info(id, 1, x, 0.0, 47.0, 44.0);
        for (name, file) in [
            ("Normal", 0x0600_4C92),
            ("Normal_rollover", 0x0600_4C93),
            ("Ghosted", 0x0600_5F13),
        ] {
            a.state_media
                .insert(name.to_owned(), ImageMedia { file, draw_mode: 3 });
        }
        a.default_state_name = "Normal".to_owned();
        let mut direct = StateInfo::named(DIRECT_STATE_ID, "");
        direct.properties.set_value(0x13, PropertyValue::Bool(true));
        a.set_state(DIRECT_STATE_ID, direct);
        a
    }
    let mut spin = info(0x10, 1, 0.0, 0.0, 175.0, 44.0);
    spin.default_state_name = "Normal".to_owned();
    spin.set_state(states::NORMAL, StateInfo::named(states::NORMAL, "Normal"));
    spin.set_state(
        states::HIGHLIGHT,
        StateInfo::named(states::HIGHLIGHT, "Highlight"),
    );
    let mut direct = StateInfo::named(DIRECT_STATE_ID, "");
    direct.properties.set_value(0x0B, PropertyValue::Bool(true));
    spin.set_state(DIRECT_STATE_ID, direct);
    spin.tooltip_enabled = true;
    spin.children = vec![arrow(0x11, 80.0), arrow(0x12, 127.0)];

    let src = Source::default();
    let (mut ui, t) = build_from(info(1, 3, 0.0, 0.0, 300.0, 100.0), vec![spin], &src);

    let spin = t.find(0x10).unwrap();
    assert_eq!(ui.widget::<Button>(spin).unwrap().face_segment_count(), 0);
    let decrement = ui
        .find_descendant(spin, 0x11)
        .expect("decrement arrow built");
    let increment = ui
        .find_descendant(spin, 0x12)
        .expect("increment arrow built");
    assert!(ui.widget::<Button>(increment).is_some());
    assert_eq!(ui.parent(increment), Some(spin));

    // The spin's own state does not reach its arrows' art.
    assert!(ui.set_retail_state(spin, states::HIGHLIGHT));
    assert_eq!(
        ui.widget::<Button>(increment).unwrap().active_state(),
        "Normal"
    );

    // Each arrow rolls over on its own.
    let hover = UiEvent::new(0, increment, kind::HOVER_ENTER);
    ui.send_event(increment, &hover);
    assert_eq!(
        ui.widget::<Button>(increment).unwrap().active_state(),
        "Normal_rollover"
    );
    assert_eq!(
        ui.widget::<Button>(decrement).unwrap().active_state(),
        "Normal"
    );

    // The pointer over an arrow is over the arrow, which authors no
    // tooltip; the spin keeps its own for the rest of its area.
    assert_eq!(ui.pick(150, 20), Some(increment));
    assert_eq!(ui.pick(20, 20), Some(spin));
    assert!(!ui.element(increment).unwrap().authored.tooltip_enabled);
    assert!(ui.element(spin).unwrap().authored.tooltip_enabled);
}
