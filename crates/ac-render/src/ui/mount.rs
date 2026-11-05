//! Mounting a window: an imported layout placed on the screen as a named,
//! registered window, as OpenAC's `RetailWindowFrame.Mount` does. A layout
//! with its own art is its own frame (imported chrome); one without is
//! wrapped in the shared nine-piece frame (the inventory, character and
//! spellbook windows, and the toolbar's two-stop variant).

use ac_ui::element_info::ElementInfo;
use ac_ui::geometry::{AnchorEdges, ResizeEdges};
use ac_ui::root::UiRoot;
use ac_ui::tree::{Element, ElementId};
use ac_ui::widgets::NineSliceFrame;
use ac_ui::widgets::chrome::BORDER;

/// What frames a mounted window.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum Chrome {
    /// The layout's own art is the frame.
    #[default]
    Imported,
    /// The shared nine-piece frame around the content.
    NineSlice,
    /// The nine-piece frame with two height stops (the toolbar).
    CollapsibleNineSlice,
}

/// Where and how a window is mounted (OpenAC `RetailWindowFrame.Options`).
#[derive(Debug, Clone, PartialEq)]
pub struct MountOptions {
    /// The frame.
    pub chrome: Chrome,
    /// Left edge of the outer frame, screen pixels.
    pub left: f32,
    /// Top edge of the outer frame, screen pixels.
    pub top: f32,
    /// The content's width when not its authored one.
    pub content_width: Option<f32>,
    /// The content's height when not its authored one.
    pub content_height: Option<f32>,
    /// Where the size limits come from when not given (dat properties
    /// 0x3F/0x3E/0x3D/0x3C: minimum and maximum width and height).
    pub dat_constraint_source: Option<ElementInfo>,
    /// The constraint source sizes the outer frame (no chrome added).
    pub dat_constraint_source_is_outer_frame: bool,
    /// The smallest width, when given.
    pub min_width: Option<f32>,
    /// The smallest height, when given.
    pub min_height: Option<f32>,
    /// The largest width, when given.
    pub max_width: Option<f32>,
    /// The largest height, when given.
    pub max_height: Option<f32>,
    /// The window can be dragged.
    pub draggable: bool,
    /// The window can be resized at all.
    pub resizable: bool,
    /// Its width can change.
    pub resize_x: bool,
    /// Its height can change.
    pub resize_y: bool,
    /// Which edges resize it.
    pub resizable_edges: ResizeEdges,
    /// A resize stays inside the screen.
    pub constrain_resize_to_parent: bool,
    /// Shown at once.
    pub visible: bool,
    /// The geometry revision persisted layouts are checked against.
    pub authored_geometry_revision: i32,
    /// The outer frame's anchors.
    pub outer_anchors: AnchorEdges,
    /// The content's anchors inside a nine-piece frame.
    pub content_anchors: AnchorEdges,
    /// Presses go through the content to what is under it.
    pub content_click_through: Option<bool>,
}

impl Default for MountOptions {
    fn default() -> Self {
        Self {
            chrome: Chrome::Imported,
            left: 0.0,
            top: 0.0,
            content_width: None,
            content_height: None,
            dat_constraint_source: None,
            dat_constraint_source_is_outer_frame: false,
            min_width: None,
            min_height: None,
            max_width: None,
            max_height: None,
            draggable: true,
            resizable: true,
            resize_x: true,
            resize_y: true,
            resizable_edges: ResizeEdges::ALL,
            constrain_resize_to_parent: false,
            visible: true,
            authored_geometry_revision: 0,
            outer_anchors: AnchorEdges::NONE,
            content_anchors: AnchorEdges::ALL,
            content_click_through: None,
        }
    }
}

fn constraint(
    explicit: Option<f32>,
    source: Option<&ElementInfo>,
    property: u32,
    fallback: f32,
    inset: f32,
) -> f32 {
    if let Some(v) = explicit {
        return v;
    }
    match source.and_then(|s| s.effective_integer(property, None)) {
        Some(v) if v >= 0 => v as f32 + inset,
        _ => fallback,
    }
}

/// Mount `content` as window `name` on the screen root; the outer frame
/// (the content itself under imported chrome) is returned. Fails when the
/// window would open outside its own size limits.
pub fn mount_window(
    ui: &mut UiRoot,
    name: &str,
    content: ElementId,
    options: &MountOptions,
) -> Result<ElementId, String> {
    let Some((authored_w, authored_h)) = ui.element(content).map(|e| (e.width, e.height)) else {
        return Err(format!("{name}: the window element is gone"));
    };
    let content_w = options.content_width.unwrap_or(authored_w);
    let content_h = options.content_height.unwrap_or(authored_h);
    let wrapped = options.chrome != Chrome::Imported;
    let inset = if wrapped { 2.0 * BORDER as f32 } else { 0.0 };
    let (outer_w, outer_h) = (content_w + inset, content_h + inset);
    let frame = if wrapped {
        let frame = ui.insert(
            Element::at(0.0, 0.0, outer_w, outer_h),
            NineSliceFrame::default(),
        );
        if let Some(e) = ui.element_mut(content) {
            e.left = BORDER as f32;
            e.top = BORDER as f32;
            e.width = content_w;
            e.height = content_h;
            e.draggable = false;
            e.resizable = false;
            if let Some(through) = options.content_click_through {
                e.click_through = through;
            }
        }
        ui.set_anchors(content, options.content_anchors);
        ui.add_child(frame, content);
        frame
    } else {
        if let Some(e) = ui.element_mut(content) {
            e.width = content_w;
            e.height = content_h;
            if let Some(through) = options.content_click_through {
                e.click_through = through;
            }
        }
        ui.set_anchors(content, AnchorEdges::NONE);
        content
    };
    let source = options.dat_constraint_source.as_ref();
    let constraint_inset = if options.dat_constraint_source_is_outer_frame {
        0.0
    } else {
        inset
    };
    ui.set_anchors(frame, options.outer_anchors);
    let Some(e) = ui.element_mut(frame) else {
        return Err(format!("{name}: the window element is gone"));
    };
    e.left = options.left;
    e.top = options.top;
    e.width = outer_w;
    e.height = outer_h;
    e.draggable = options.draggable;
    e.resizable = options.resizable;
    e.resize_x = options.resize_x;
    e.resize_y = options.resize_y;
    e.resizable_edges = options.resizable_edges;
    e.constrain_resize_to_parent = options.constrain_resize_to_parent;
    e.opacity = 1.0;
    // Imported chrome keeps the layout's own limits unless given.
    let (own_min_w, own_min_h, own_max_w, own_max_h) =
        (e.min_width, e.min_height, e.max_width, e.max_height);
    let (min_w_fallback, min_h_fallback, max_w_fallback, max_h_fallback) = if wrapped {
        (outer_w, outer_h, f32::MAX, f32::MAX)
    } else {
        (own_min_w, own_min_h, own_max_w, own_max_h)
    };
    e.min_width = constraint(
        options.min_width,
        source,
        0x3F,
        min_w_fallback,
        constraint_inset,
    );
    e.min_height = constraint(
        options.min_height,
        source,
        0x3E,
        min_h_fallback,
        constraint_inset,
    );
    e.max_width = e.min_width.max(constraint(
        options.max_width,
        source,
        0x3D,
        max_w_fallback,
        constraint_inset,
    ));
    e.max_height = e.min_height.max(constraint(
        options.max_height,
        source,
        0x3C,
        max_h_fallback,
        constraint_inset,
    ));
    let (w, h) = (e.width, e.height);
    let width_ok = w >= e.min_width && w <= e.max_width;
    let height_ok = h >= e.min_height && h <= e.max_height;
    if !width_ok || !height_ok {
        return Err(format!(
            "{name}: mounted size {w} x {h} is outside its own limits \
             [{}, {}] x [{}, {}]",
            e.min_width, e.max_width, e.min_height, e.max_height
        ));
    }
    if wrapped {
        ui.apply_anchor(content, outer_w, outer_h);
    }
    let root = ui.root();
    ui.add_child(root, frame);
    ui.register_window(
        name,
        frame,
        Some(content),
        options.authored_geometry_revision,
    )
    .map_err(|e| format!("{name}: {e:?}"))?;
    if !options.visible {
        ui.set_visible(frame, false);
    }
    Ok(frame)
}

/// The toolbar's two stops: collapsed shows its first row (down to the
/// top of the second), expanded the whole content; the second row shows
/// only expanded (OpenAC `RetailUiRuntime.ConfigureToolbarCollapse`).
pub fn configure_collapse(
    ui: &mut UiRoot,
    frame: ElementId,
    second_row: Vec<ElementId>,
    content_height: f32,
) {
    let Some(top) = second_row
        .iter()
        .filter_map(|&id| ui.element(id).map(|e| e.top))
        .reduce(f32::min)
    else {
        return;
    };
    let border = BORDER as f32;
    let collapsed = top + 2.0 * border;
    let expanded = content_height + 2.0 * border;
    if let Some(f) = ui.widget_mut::<NineSliceFrame>(frame) {
        f.collapsed_height = collapsed;
        f.expanded_height = expanded;
        f.second_row = second_row;
    }
    if let Some(e) = ui.element_mut(frame) {
        e.resizable = true;
        e.resizable_edges = ResizeEdges::BOTTOM;
        e.min_height = collapsed;
        e.max_height = expanded;
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use ac_ui::widgets::Panel;

    fn content(ui: &mut UiRoot, w: f32, h: f32) -> ElementId {
        let mut e = Element::at(3.0, 4.0, w, h);
        e.max_width = f32::MAX;
        e.max_height = f32::MAX;
        ui.insert(e, Panel::default())
    }

    #[test]
    fn a_wrapped_window_sits_inside_the_frame_border() {
        // OpenAC `RetailWindowFrame.Mount`: the outer frame is the content
        // plus the border on each side; the content sits at the border.
        let mut ui = UiRoot::new();
        ui.set_size(1000.0, 800.0);
        let c = content(&mut ui, 300.0, 200.0);
        let frame = mount_window(
            &mut ui,
            "panel",
            c,
            &MountOptions {
                chrome: Chrome::NineSlice,
                left: 18.0,
                top: 18.0,
                content_height: Some(362.0),
                visible: false,
                ..MountOptions::default()
            },
        )
        .unwrap();
        assert_ne!(frame, c);
        let f = ui.element(frame).unwrap();
        assert_eq!(
            (f.left, f.top, f.width, f.height),
            (18.0, 18.0, 310.0, 372.0)
        );
        assert_eq!((f.min_width, f.min_height), (310.0, 372.0));
        let e = ui.element(c).unwrap();
        assert_eq!((e.left, e.top), (5.0, 5.0));
        assert!(!ui.is_window_visible("panel"));
        assert_eq!(ui.window("panel").unwrap().content_root, c);
    }

    #[test]
    fn the_toolbar_frame_snaps_to_its_stops() {
        let mut ui = UiRoot::new();
        ui.set_size(1000.0, 800.0);
        let c = content(&mut ui, 300.0, 80.0);
        let row = ui.add(c, Element::at(0.0, 40.0, 10.0, 10.0), Panel::default());
        let frame = mount_window(
            &mut ui,
            "toolbar",
            c,
            &MountOptions {
                chrome: Chrome::CollapsibleNineSlice,
                ..MountOptions::default()
            },
        )
        .unwrap();
        configure_collapse(&mut ui, frame, vec![row], 80.0);
        ui.element_mut(frame).unwrap().height = 60.0;
        ui.tick(0.016, 16);
        assert_eq!(ui.element(frame).unwrap().height, 50.0);
        assert!(!ui.element(row).unwrap().visible);
        ui.element_mut(frame).unwrap().height = 80.0;
        ui.tick(0.016, 32);
        assert_eq!(ui.element(frame).unwrap().height, 90.0);
        assert!(ui.element(row).unwrap().visible);
    }
}

#[cfg(test)]
mod frame_tests {
    //! Ported from OpenAC `tests/AcDream.App.Tests/UI/Layout/RetailWindowFrameTests.cs`
    //! (the mount cases; the combat and inventory cases are the panels').
    use super::*;
    use ac_dat::objects::{DIRECT_STATE_ID, PropertyValue};
    use ac_ui::element_info::StateInfo;
    use ac_ui::widgets::Panel;

    fn ui(w: f32, h: f32) -> UiRoot {
        let mut ui = UiRoot::new();
        ui.set_size(w, h);
        ui
    }

    fn content(ui: &mut UiRoot, w: f32, h: f32) -> ElementId {
        let mut e = Element::at(0.0, 0.0, w, h);
        e.max_width = f32::MAX;
        e.max_height = f32::MAX;
        ui.insert(e, Panel::default())
    }

    fn constraints(props: &[(u32, i32)]) -> ElementInfo {
        let mut info = ElementInfo::default();
        let mut s = StateInfo::named(DIRECT_STATE_ID, "");
        for &(id, v) in props {
            s.properties.set_value(id, PropertyValue::Integer(v));
        }
        info.set_state(DIRECT_STATE_ID, s);
        info
    }

    #[test]
    fn imported_chrome_mounts_the_root_without_a_second_frame() {
        let mut ui = ui(800.0, 600.0);
        let c = content(&mut ui, 220.0, 90.0);
        let frame = mount_window(
            &mut ui,
            "vitals",
            c,
            &MountOptions {
                left: 12.0,
                top: 18.0,
                resize_x: true,
                resize_y: false,
                min_width: Some(40.0),
                content_click_through: Some(false),
                ..MountOptions::default()
            },
        )
        .unwrap();
        assert_eq!(frame, c);
        let w = ui.window("vitals").unwrap();
        assert_eq!((w.frame, w.content_root), (c, c));
        assert_eq!(ui.parent(c), Some(ui.root()));
        let e = ui.element(c).unwrap();
        assert_eq!(
            (e.left, e.top, e.width, e.height),
            (12.0, 18.0, 220.0, 90.0)
        );
        assert!(e.draggable && e.resizable && e.resize_x && !e.resize_y);
        assert_eq!(e.min_width, 40.0);
    }

    #[test]
    fn imported_chrome_can_keep_fixed_hud_anchors() {
        let mut ui = ui(1920.0, 1080.0);
        let c = content(&mut ui, 1730.0, 90.0);
        let frame = mount_window(
            &mut ui,
            "combat",
            c,
            &MountOptions {
                top: 980.0,
                outer_anchors: AnchorEdges::LEFT | AnchorEdges::BOTTOM,
                ..MountOptions::default()
            },
        )
        .unwrap();
        assert_eq!(
            ui.element(frame).unwrap().anchors,
            AnchorEdges::LEFT | AnchorEdges::BOTTOM
        );
    }

    #[test]
    fn a_nine_piece_frame_uses_cropped_content_and_the_dat_height_limits() {
        let mut ui = ui(800.0, 600.0);
        let c = content(&mut ui, 800.0, 100.0);
        let frame = mount_window(
            &mut ui,
            "chat",
            c,
            &MountOptions {
                chrome: Chrome::NineSlice,
                left: 10.0,
                top: 440.0,
                content_width: Some(490.0),
                dat_constraint_source: Some(constraints(&[(0x3E, 100), (0x3C, 360)])),
                min_width: Some(200.0),
                ..MountOptions::default()
            },
        )
        .unwrap();
        assert_eq!(ui.parent(c), Some(frame));
        let f = ui.element(frame).unwrap();
        assert_eq!((f.width, f.height), (500.0, 110.0));
        let e = ui.element(c).unwrap();
        assert_eq!((e.left, e.top, e.width, e.height), (5.0, 5.0, 490.0, 100.0));
        let f = ui.element(frame).unwrap();
        assert_eq!(
            (f.min_width, f.min_height, f.max_height),
            (200.0, 110.0, 370.0)
        );
    }

    #[test]
    fn content_shaped_limits_add_the_border_and_clamp_a_programmatic_resize() {
        let mut ui = ui(1920.0, 1080.0);
        let c = content(&mut ui, 490.0, 100.0);
        mount_window(
            &mut ui,
            "chat-shaped",
            c,
            &MountOptions {
                chrome: Chrome::NineSlice,
                left: 10.0,
                top: 20.0,
                resize_x: false,
                resize_y: true,
                dat_constraint_source: Some(constraints(&[(0x3E, 100), (0x3C, 2000)])),
                ..MountOptions::default()
            },
        )
        .unwrap();
        let height = |ui: &UiRoot| {
            ui.element(ui.window("chat-shaped").unwrap().frame)
                .unwrap()
                .height
        };
        let width = ui
            .element(ui.window("chat-shaped").unwrap().frame)
            .unwrap()
            .width;
        ui.resize_window("chat-shaped", width, 5.0);
        assert_eq!(height(&ui), 110.0);
        ui.resize_window("chat-shaped", width, 50_000.0);
        assert_eq!(height(&ui), 2010.0);
        ui.resize_window("chat-shaped", width, 500.0);
        assert_eq!(height(&ui), 500.0);
    }

    #[test]
    fn imported_chat_clamps_at_its_authored_bounds_with_no_border() {
        let mut ui = ui(1920.0, 1080.0);
        let c = content(&mut ui, 410.0, 100.0);
        let frame = mount_window(
            &mut ui,
            "chat",
            c,
            &MountOptions {
                left: 10.0,
                top: 440.0,
                dat_constraint_source: Some(constraints(&[
                    (0x3F, 300),
                    (0x3E, 100),
                    (0x3D, 2000),
                    (0x3C, 2000),
                ])),
                ..MountOptions::default()
            },
        )
        .unwrap();
        let f = ui.element(frame).unwrap();
        assert_eq!(
            (f.min_width, f.min_height, f.max_width, f.max_height),
            (300.0, 100.0, 2000.0, 2000.0)
        );
        let width = f.width;
        ui.resize_window("chat", width, 5.0);
        assert_eq!(ui.element(frame).unwrap().height, 100.0);
        ui.resize_window("chat", width, 50_000.0);
        assert_eq!(ui.element(frame).unwrap().height, 2000.0);
    }

    #[test]
    fn a_collapsible_mount_gives_the_toolbar_frame_its_own_axes() {
        let mut ui = ui(800.0, 600.0);
        let c = content(&mut ui, 300.0, 122.0);
        let frame = mount_window(
            &mut ui,
            "toolbar",
            c,
            &MountOptions {
                chrome: Chrome::CollapsibleNineSlice,
                resize_x: false,
                resize_y: true,
                resizable_edges: ResizeEdges::BOTTOM,
                constrain_resize_to_parent: true,
                content_anchors: AnchorEdges::LEFT | AnchorEdges::TOP | AnchorEdges::RIGHT,
                content_click_through: Some(true),
                ..MountOptions::default()
            },
        )
        .unwrap();
        assert!(ui.widget::<NineSliceFrame>(frame).is_some());
        let f = ui.element(frame).unwrap();
        assert!(!f.resize_x && f.resize_y);
        assert_eq!(f.resizable_edges, ResizeEdges::BOTTOM);
        assert!(f.constrain_resize_to_parent);
        let e = ui.element(c).unwrap();
        assert!(e.click_through && !e.draggable && !e.resizable);
    }
}
