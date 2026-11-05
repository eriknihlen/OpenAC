//! LayoutDesc import: dat layouts → element descriptions (with base-element
//! inheritance resolved) → widgets in the tree.
//!
//! Two layers. [`import_infos`] reads a layout and resolves inheritance into
//! an [`ElementInfo`] tree (pure data). [`build`] turns such a tree into
//! elements under a [`UiRoot`] through the widget factory ([`create`]).

use std::collections::{HashMap, HashSet};
use std::sync::Arc;

use ac_dat::objects::{
    DIRECT_STATE_ID, ElementDesc, LayoutDesc, Media, PropertyValue, StateDesc, StringInfo,
    ui_state_name,
};

use crate::draw::Color;
use crate::element_info::{
    CursorMedia, ElementInfo, ImageMedia, StateInfo, apply_canonical_projection, color_of, merge,
    referenced_id, to_anchors,
};
use crate::font::UiFont;
use crate::geometry::PixelRect;
use crate::layout_policy::LayoutPolicy;
use crate::media::{LoopingAnimation, MediaStep, MediaStepKind};
use crate::root::UiRoot;
use crate::text::{HJustify, VJustify};
use crate::tree::{Element, ElementId, Widget};
use crate::widgets::button::{Button, LabelAlign};
use crate::widgets::dat_element::DatElement;
use crate::widgets::meter::{DetailOverlay, Meter, MeterLabelAlign};
use crate::widgets::resize_grip::{Border, ResizeGrip};
use crate::widgets::text::Text;
use crate::widgets::{
    Bitfield64, ChildImport, DialogRoot, Field, ItemList, ListBox, Menu, OptionToggleSlider, Radar,
    Scrollbar, TabPanel, Viewport, VitalsRoot,
};

/// Element classes the factory maps to dedicated widgets.
pub mod class {
    /// Button.
    pub const BUTTON: u32 = 1;
    /// Window drag bar.
    pub const DRAG_BAR: u32 = 2;
    /// Container ("field").
    pub const CONTAINER: u32 = 3;
    /// Template list box.
    pub const LIST_BOX: u32 = 5;
    /// Drop-down menu.
    pub const MENU: u32 = 6;
    /// Meter.
    pub const METER: u32 = 7;
    /// Tab panel.
    pub const TAB_PANEL: u32 = 8;
    /// Resize grip.
    pub const RESIZE_BAR: u32 = 9;
    /// Scrollbar.
    pub const SCROLLBAR: u32 = 0xB;
    /// Text.
    pub const TEXT: u32 = 0xC;
    /// 3-D viewport.
    pub const VIEWPORT: u32 = 0xD;
    /// Burden indicator (a button).
    pub const BURDEN: u32 = 0x1000_0001;
    /// Effects indicator (a button).
    pub const EFFECTS: u32 = 0x1000_0002;
    /// Link-status indicator (a button).
    pub const LINK: u32 = 0x1000_0003;
    /// Mini-game indicator (a button).
    pub const MINI_GAME: u32 = 0x1000_0004;
    /// Vitae indicator (a button).
    pub const VITAE: u32 = 0x1000_0006;
    /// Stacked vitals window root.
    pub const GM_VITALS: u32 = 0x1000_0009;
    /// Radar.
    pub const RADAR: u32 = 0x1000_0010;
    /// Item list (toolbar, inventory, paperdoll slots).
    pub const ITEM_LIST: u32 = 0x1000_0031;
    /// Option checkbox.
    pub const CHECKBOX: u32 = 0x1000_0035;
    /// Option toggle slider.
    pub const TOGGLE_SLIDER: u32 = 0x1000_0036;
    /// Option scrollbar.
    pub const OPTION_SCROLLBAR: u32 = 0x1000_0037;
    /// Option menu.
    pub const OPTION_MENU: u32 = 0x1000_0038;
    /// 64-bit checkbox bit field.
    pub const BITFIELD64: u32 = 0x1000_0044;
    /// Floating vitals root.
    pub const GM_FLOATY_VITALS: u32 = 0x1000_004D;
    /// Side-by-side floating vitals root.
    pub const GM_FLOATY_SIDE_VITALS: u32 = 0x1000_0056;
}

/// Where layouts, fonts and strings come from.
pub trait LayoutSource {
    /// Layout `id`.
    fn layout(&self, id: u32) -> Option<Arc<LayoutDesc>>;
    /// Font `id`.
    fn font(&self, _id: u32) -> Option<Arc<UiFont>> {
        None
    }
    /// The text a string-info property names.
    fn string(&self, _info: &StringInfo) -> Option<String> {
        None
    }
}

// ── Element descriptions ────────────────────────────────────────────────

fn to_info(d: &ElementDesc) -> ElementInfo {
    let default_name = ui_state_name(d.default_state);
    let mut info = ElementInfo {
        id: d.element_id,
        kind: d.element_type,
        x: d.x as f32,
        y: d.y as f32,
        width: d.width as f32,
        height: d.height as f32,
        left: d.left_edge,
        top: d.top_edge,
        right: d.right_edge,
        bottom: d.bottom_edge,
        read_order: d.read_order,
        z_level: d.z_level,
        default_state_id: d.default_state,
        default_state_name: if matches!(default_name.as_ref(), "Undef" | "Undefined" | "0") {
            String::new()
        } else {
            default_name.into_owned()
        },
        ..ElementInfo::default()
    };
    read_state(&d.state_desc, DIRECT_STATE_ID, "", &mut info);
    for (k, s) in &d.states {
        read_state(s, *k, &ui_state_name(*k), &mut info);
    }
    apply_canonical_projection(&mut info);
    info
}

fn read_state(sd: &StateDesc, id: u32, name: &str, info: &mut ElementInfo) {
    let mut state = StateInfo {
        id,
        name: name.to_owned(),
        pass_to_children: sd.pass_to_children,
        incorporation_flags: sd.incorporation_flags.0,
        media_count: sd.media.len(),
        ..StateInfo::default()
    };
    let steps: Vec<MediaStep> = sd
        .media
        .iter()
        .map(|m| match &m.media {
            Media::Image { file, draw_mode } => MediaStep {
                kind: MediaStepKind::Image,
                file: *file,
                draw_mode: draw_mode.0,
                ..MediaStep::default()
            },
            Media::Pause {
                min_duration,
                max_duration,
            } => MediaStep {
                kind: MediaStepKind::Pause,
                min_duration: *min_duration,
                max_duration: *max_duration,
                ..MediaStep::default()
            },
            Media::State {
                state_id,
                probability,
            } => MediaStep {
                kind: MediaStepKind::State,
                jump_index: *state_id,
                probability: *probability,
                ..MediaStep::default()
            },
            Media::Jump {
                jump_item_index,
                probability,
            } => MediaStep {
                kind: MediaStepKind::Jump,
                jump_index: *jump_item_index,
                probability: *probability,
                ..MediaStep::default()
            },
            _ => MediaStep {
                kind: MediaStepKind::Other,
                raw_type: m.media_type(),
                ..MediaStep::default()
            },
        })
        .collect();
    state.media_steps = Arc::new(steps);

    if let [first, second] = sd.media.as_slice()
        && let Media::Animation {
            duration,
            draw_mode,
            frames,
        } = &first.media
        && matches!(
            second.media,
            Media::Jump {
                jump_item_index: 0,
                probability
            } if probability == 1.0
        )
        && !frames.is_empty()
        && duration.is_finite()
        && *duration > 0.0
    {
        state.looping_animation = Some(Arc::new(LoopingAnimation {
            frames: frames.clone(),
            duration: *duration,
            draw_mode: draw_mode.0,
        }));
    }

    let mut image_read = false;
    for m in &sd.media {
        match &m.media {
            Media::Image { file, draw_mode } => {
                state.image_media_count += 1;
                if !image_read && *file != 0 {
                    let image = ImageMedia {
                        file: *file,
                        draw_mode: draw_mode.0,
                    };
                    info.state_media.insert(name.to_owned(), image);
                    state.image = Some(image);
                    image_read = true;
                }
            }
            Media::Cursor {
                file,
                x_hotspot,
                y_hotspot,
            } if *file != 0 => {
                let cursor = CursorMedia {
                    file: *file,
                    hotspot_x: i32::try_from(*x_hotspot).unwrap_or(i32::MAX),
                    hotspot_y: i32::try_from(*y_hotspot).unwrap_or(i32::MAX),
                };
                info.state_cursors.insert(name.to_owned(), cursor);
                state.cursor = Some(cursor);
            }
            _ => {}
        }
    }

    for (k, p) in &sd.properties {
        state.properties.set(*k, p.clone());
    }
    info.set_state(id, state);

    if info.font_did == 0
        && let Some(PropertyValue::Array(items)) = sd.property(0x1A).map(|p| &p.value)
        && let Some(PropertyValue::DataId(did)) = items.first().map(|p| &p.value)
    {
        info.font_did = *did;
    }
    if info.font_color.is_none()
        && let Some(PropertyValue::Color(c)) = sd.property(0x1B).map(|p| &p.value)
    {
        info.font_color = Some(Color::from_dat(*c));
    }
    if info.outline_color.is_none()
        && let Some(PropertyValue::Color(c)) = sd.property(0x22).map(|p| &p.value)
    {
        info.outline_color = Some(Color::from_dat(*c));
    }
}

/// A base element's children are mounted for an element that inherits from
/// it only when the inheritor has no children and no images of its own.
#[must_use]
pub fn should_mount_base_children(
    derived_children: usize,
    derived_media: usize,
    base_children: usize,
) -> bool {
    derived_children == 0 && derived_media == 0 && base_children > 0
}

fn find_desc(l: &LayoutDesc, id: u32) -> Option<&ElementDesc> {
    l.find(id)
}

fn set_original_parent_size(child: &mut ElementInfo, width: f32, height: f32) {
    child.original_parent_size = Some((width, height));
}

/// Resolve one element and its inheritance chain. `chain` guards against
/// cycles of base references.
fn resolve(
    source: &dyn LayoutSource,
    d: &ElementDesc,
    chain: &mut HashSet<(u32, u32)>,
) -> ElementInfo {
    let own = to_info(d);
    let mut result = own.clone();
    let mut base_info = None;
    if d.base_element != 0
        && d.base_layout_id != 0
        && chain.insert((d.base_layout_id, d.base_element))
        && let Some(base_layout) = source.layout(d.base_layout_id)
        && let Some(base_desc) = find_desc(&base_layout, d.base_element)
    {
        let b = resolve(source, base_desc, chain);
        result = merge(&b, &own);
        base_info = Some(b);
    }
    incorporate_children(
        source,
        &mut result,
        base_info.as_ref().map_or(&[][..], |b| &b.children[..]),
        d,
    );
    if let Some(b) = &base_info
        && should_mount_base_children(d.children.len(), own.state_media.len(), b.children.len())
    {
        result.z_level = own.z_level;
    }
    result
}

fn incorporate_children(
    source: &dyn LayoutSource,
    result: &mut ElementInfo,
    base_children: &[ElementInfo],
    derived: &ElementDesc,
) {
    let derived_has = |id: u32| derived.children.iter().any(|(k, _)| *k == id);
    let retained = base_children.iter().filter(|c| !derived_has(c.id)).count();
    let (pw, ph) = (result.width, result.height);
    for base_child in base_children {
        match derived.children.iter().find(|(k, _)| *k == base_child.id) {
            Some((_, overlay)) => {
                let own = to_info(overlay);
                let mut child = merge(base_child, &own);
                incorporate_children(source, &mut child, &base_child.children, overlay);
                set_original_parent_size(&mut child, pw, ph);
                result.children.push(child);
            }
            None => result.children.push(base_child.clone()),
        }
    }
    let base_ids: HashSet<u32> = base_children.iter().map(|c| c.id).collect();
    for (k, desc) in &derived.children {
        if base_ids.contains(k) {
            continue;
        }
        let mut child = resolve(source, desc, &mut HashSet::new());
        child.read_order = child.read_order.wrapping_add(retained as u32);
        set_original_parent_size(&mut child, pw, ph);
        result.children.push(child);
    }
}

fn collect_base_refs(d: &ElementDesc, layout_id: u32, out: &mut HashSet<u32>) {
    if d.base_element != 0 && d.base_layout_id == layout_id {
        out.insert(d.base_element);
    }
    for (_, c) in &d.children {
        collect_base_refs(c, layout_id, out);
    }
}

fn collect_template_refs(info: &ElementInfo, layout_id: u32, out: &mut HashSet<u32>) {
    for entry in &info.template_list {
        if entry.layout == layout_id {
            out.insert(entry.element);
        }
    }
    for c in &info.children {
        collect_template_refs(c, layout_id, out);
    }
}

/// Read layout `layout_id` into an element-description tree. Top elements
/// that only serve as a base for others (no images of their own) and row
/// templates referenced by a list in the same layout are skipped. One top
/// element is the root; several are wrapped in a container the size of the
/// layout.
#[must_use]
pub fn import_infos(source: &dyn LayoutSource, layout_id: u32) -> Option<ElementInfo> {
    let layout = source.layout(layout_id)?;
    let mut base_refs = HashSet::new();
    for (_, e) in &layout.elements {
        collect_base_refs(e, layout_id, &mut base_refs);
    }
    let mut tops: Vec<ElementInfo> = Vec::new();
    for (_, d) in &layout.elements {
        if base_refs.contains(&d.element_id) && to_info(d).state_media.is_empty() {
            continue;
        }
        tops.push(resolve(source, d, &mut HashSet::new()));
    }
    let mut template_refs = HashSet::new();
    for t in &tops {
        collect_template_refs(t, layout_id, &mut template_refs);
    }
    if !template_refs.is_empty() {
        tops.retain(|t| !template_refs.contains(&t.id));
    }
    if tops.len() == 1 {
        return tops.pop();
    }
    let (w, h) = (layout.width as f32, layout.height as f32);
    for t in &mut tops {
        set_original_parent_size(t, w, h);
    }
    Some(ElementInfo {
        kind: class::CONTAINER,
        width: w,
        height: h,
        children: tops,
        ..ElementInfo::default()
    })
}

/// Read the subtree of element `root_element_id` of layout `layout_id`.
#[must_use]
pub fn import_infos_rooted(
    source: &dyn LayoutSource,
    layout_id: u32,
    root_element_id: u32,
) -> Option<ElementInfo> {
    let layout = source.layout(layout_id)?;
    let root = find_desc(&layout, root_element_id)?;
    Some(resolve(source, root, &mut HashSet::new()))
}

// ── Building widgets ────────────────────────────────────────────────────

/// What the factory needs besides the description.
pub struct BuildContext<'a> {
    /// Fonts and strings.
    pub source: &'a dyn LayoutSource,
    /// The font of elements that name none.
    pub default_font: Option<Arc<UiFont>>,
    /// The layout being built (recorded on every element; tooltips use it).
    pub source_layout_did: u32,
    /// UI media clock at build time.
    pub media_seconds: f64,
}

impl std::fmt::Debug for BuildContext<'_> {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("BuildContext")
            .field("source_layout_did", &self.source_layout_did)
            .finish_non_exhaustive()
    }
}

impl BuildContext<'_> {
    fn font_for(&self, did: u32) -> Option<Arc<UiFont>> {
        if did != 0 {
            self.source.font(did).or_else(|| self.default_font.clone())
        } else {
            self.default_font.clone()
        }
    }

    fn string(&self, info: &StringInfo) -> Option<String> {
        self.source.string(info)
    }
}

/// An imported tree in the root: its root element and its elements by dat
/// element id.
#[derive(Debug, Clone, PartialEq)]
pub struct ImportedLayout {
    /// The root element (detached until the caller attaches it).
    pub root: ElementId,
    by_id: HashMap<u32, ElementId>,
}

impl ImportedLayout {
    /// A layout built by hand: `root` and its elements by dat id (tests
    /// and synthesized windows).
    #[must_use]
    pub fn from_parts(root: ElementId, by_id: HashMap<u32, ElementId>) -> Self {
        Self { root, by_id }
    }

    /// The element with dat id `id`.
    #[must_use]
    pub fn find(&self, id: u32) -> Option<ElementId> {
        self.by_id.get(&id).copied()
    }
}

fn authored_string(info: &ElementInfo, ctx: &BuildContext<'_>) -> Option<String> {
    match info.effective_property(0x17, None) {
        Some(PropertyValue::StringInfo(s)) => ctx.string(s),
        _ => None,
    }
}

fn layout_policy_of(info: &ElementInfo) -> Option<LayoutPolicy> {
    let (pw, ph) = info.original_parent_size?;
    LayoutPolicy::new(
        info.left,
        info.top,
        info.right,
        info.bottom,
        PixelRect::from_position_and_size(
            info.x as i32,
            info.y as i32,
            info.width as i32,
            info.height as i32,
        ),
        PixelRect::from_position_and_size(0, 0, pw as i32, ph as i32),
    )
    .ok()
}

/// Create the element and widget for one description (children not
/// included).
#[must_use]
pub fn create(info: &ElementInfo, ctx: &BuildContext<'_>) -> (Element, Box<dyn Widget>) {
    let arc = Arc::new(info.clone());
    let element_font = ctx.font_for(info.font_did);
    let mut element = Element::default();
    let widget: Box<dyn Widget> = match info.kind {
        class::BUTTON
        | class::BURDEN
        | class::EFFECTS
        | class::LINK
        | class::MINI_GAME
        | class::VITAE => {
            Button::configure(&mut element);
            Box::new(build_button(arc, element_font, ctx))
        }
        class::CHECKBOX => {
            Button::configure(&mut element);
            Box::new(build_checkbox(arc, element_font, ctx))
        }
        class::DRAG_BAR => {
            element.window_move_handle = true;
            element.click_through = false;
            Box::new(DatElement::new(arc))
        }
        class::RESIZE_BAR => {
            let flag = |id| info.effective_bool(id, None) == Some(true);
            Box::new(ResizeGrip {
                border: Border::decode(flag(0x2A), flag(0x2B), flag(0x2C), flag(0x2D)),
                sprite: info.state_media.get("").map_or(0, |m| m.file),
            })
        }
        class::TEXT if info.effective_bool(0x16, None) != Some(true) => {
            let mut text = build_text(arc.clone(), element_font, ctx);
            Text::configure(&mut element);
            let selectable = info.effective_bool(0x27, None) == Some(true);
            text.set_selectable(&mut element, selectable);
            Box::new(text)
        }
        class::GM_VITALS | class::GM_FLOATY_VITALS | class::GM_FLOATY_SIDE_VITALS => {
            DatElement::configure(&mut element);
            Box::new(VitalsRoot::new(DatElement::new(arc)))
        }
        0x13 | 0x14 | 0x15 | 0x17 | 0x19 => {
            element.click_through = false;
            Box::new(DialogRoot)
        }
        class::RADAR => Box::new(Radar::default()),
        class::TEXT => {
            // Text with the editable flag (0x16) is a text field.
            Field::configure(&mut element);
            Box::new(build_field(info, element_font))
        }
        class::LIST_BOX => {
            DatElement::configure(&mut element);
            Box::new(ListBox::new(arc))
        }
        class::MENU | class::OPTION_MENU => {
            Menu::configure(&mut element);
            Box::new(build_menu(info, element_font, ctx))
        }
        class::METER => {
            Meter::configure(&mut element);
            Box::new(build_meter(info, element_font, ctx))
        }
        class::TAB_PANEL => {
            DatElement::configure(&mut element);
            Box::new(TabPanel::new(arc))
        }
        class::VIEWPORT => Box::new(Viewport::default()),
        class::SCROLLBAR | class::OPTION_SCROLLBAR => {
            Scrollbar::configure(&mut element);
            Box::new(build_scrollbar(info))
        }
        class::ITEM_LIST => Box::new(ItemList::default()),
        class::TOGGLE_SLIDER => Box::new(OptionToggleSlider::default()),
        class::BITFIELD64 => {
            let mut b = Bitfield64::default();
            b.templates = info.template_list.clone();
            b.checked_led_sprite = info.led_checked_sprite;
            b.unchecked_led_sprite = info.led_unchecked_sprite;
            Box::new(b)
        }
        _ => {
            DatElement::configure(&mut element);
            Box::new(DatElement::new(arc))
        }
    };

    element.dat_element_id = info.id;
    element.set_state_cursors(&info.state_cursors);
    element.left = info.x;
    element.top = info.y;
    element.width = info.width;
    element.height = info.height;
    element.z_order = (info.read_order as i32).wrapping_sub(info.z_level.wrapping_mul(10_000));
    element.set_anchors(to_anchors(info.left, info.top, info.right, info.bottom));
    element.layout_policy = layout_policy_of(info);
    (element, widget)
}

fn build_text(info: Arc<ElementInfo>, font: Option<Arc<UiFont>>, ctx: &BuildContext<'_>) -> Text {
    let mut t = Text::default();
    t.element_id = info.id;
    t.background_sprite = info
        .state_media
        .get(if !info.default_state_name.is_empty() {
            info.default_state_name.as_str()
        } else if info.state_media.contains_key("Normal") {
            "Normal"
        } else {
            ""
        })
        .map_or(0, |m| m.file);
    t.centered = info.h_justify == HJustify::Center;
    t.right_aligned = info.h_justify == HJustify::Right;
    t.vertical_justify = info.v_justify;
    t.one_line = info.effective_bool(0x20, None) == Some(true);
    t.font = font;
    t.font_color_palette = color_palette(&info, 0x1B);
    t.outline = info.outline;
    t.margin_left = info.margin_left as f32;
    t.margin_right = info.margin_right as f32;
    t.margin_top = info.margin_top as f32;
    t.margin_bottom = info.margin_bottom as f32;
    if let Some(c) = info.font_color {
        t.default_color = c;
    }
    if let Some(c) = info.tag_font_color {
        t.tag_color = Some(c);
    }
    if let Some(c) = info.outline_color {
        t.outline_color = c;
    }
    t.configure_dat_state(info.clone());
    if let Some(authored) = authored_string(&info, ctx).filter(|s| !s.is_empty()) {
        t.set_authored(authored);
    }
    let mut state_strings = Vec::new();
    for (id, state) in &info.states {
        if *id == DIRECT_STATE_ID {
            continue;
        }
        if let Some(PropertyValue::StringInfo(s)) = state.properties.get(0x17).map(|p| &p.value)
            && let Some(text) = ctx.string(s).filter(|t| !t.is_empty())
        {
            state_strings.push((*id, text));
        }
    }
    if !state_strings.is_empty() {
        t.set_authored_state_strings(state_strings);
    }
    t
}

/// Every colour of property `id` (a colour or an array of colours).
#[must_use]
pub fn color_palette(info: &ElementInfo, id: u32) -> Vec<Color> {
    match info.effective_property(id, None) {
        Some(PropertyValue::Color(c)) => vec![Color::from_dat(*c)],
        Some(PropertyValue::Array(items)) => items
            .iter()
            .filter_map(|p| match &p.value {
                PropertyValue::Color(c) => Some(Color::from_dat(*c)),
                _ => None,
            })
            .collect(),
        _ => Vec::new(),
    }
}

/// Per-state values of a colour property, when they differ between states.
fn per_state_colors(info: &ElementInfo, id: u32) -> Option<Vec<(u32, Color)>> {
    let mut map: Vec<(u32, Color)> = Vec::new();
    for (state, _) in &info.states {
        if let Some(c) = info.effective_property(id, Some(*state)).and_then(color_of) {
            map.push((*state, c));
        }
    }
    let distinct = map.iter().any(|(_, c)| *c != map[0].1);
    (map.len() > 1 && distinct).then_some(map)
}

/// Per-state values of a bool property, when they differ between states.
fn per_state_bools(info: &ElementInfo, id: u32) -> Option<Vec<(u32, bool)>> {
    let mut map: Vec<(u32, bool)> = Vec::new();
    for (state, _) in &info.states {
        if let Some(b) = info.effective_bool(id, Some(*state)) {
            map.push((*state, b));
        }
    }
    let distinct = map.iter().any(|(_, b)| *b != map[0].1);
    (map.len() > 1 && distinct).then_some(map)
}

/// Children whose images belong to states the parent has: a button face
/// drawn by its child. A child that is itself a button is not one (see
/// [`is_child_button`]).
fn stateful_face_children(info: &ElementInfo) -> Vec<Arc<ElementInfo>> {
    let mut faces: Vec<&ElementInfo> = info
        .children
        .iter()
        .filter(|c| {
            !is_child_button(c)
                && !c.state_media.is_empty()
                && c.state_media
                    .keys()
                    .any(|k| info.states.iter().any(|(_, s)| s.name == *k))
        })
        .collect();
    faces.sort_by_key(|c| c.read_order);
    faces.into_iter().map(|c| Arc::new(c.clone())).collect()
}

/// A button's child that is itself a button (a spin's arrows) is a control
/// of its own, with its own states, rollover and hit area: it is built
/// under the button and never drawn as part of the button's face.
#[must_use]
pub fn is_child_button(child: &ElementInfo) -> bool {
    child.kind == class::BUTTON
}

fn reflow_value_child_rect(child: &ElementInfo, parent: &ElementInfo) -> (f32, f32, f32, f32) {
    let (opw, oph) = child
        .original_parent_size
        .unwrap_or((parent.width, parent.height));
    let original_child = PixelRect::from_position_and_size(
        child.x as i32,
        child.y as i32,
        child.width as i32,
        child.height as i32,
    );
    let original_parent = PixelRect::from_position_and_size(0, 0, opw as i32, oph as i32);
    let current_parent =
        PixelRect::from_position_and_size(0, 0, parent.width as i32, parent.height as i32);
    let r = LayoutPolicy::apply_raw(
        (child.left, child.top, child.right, child.bottom),
        original_child,
        original_parent,
        PixelRect::new(0, 0, -1, -1),
        current_parent,
        false,
    )
    .unwrap_or(original_child);
    (
        r.x0 as f32,
        r.y0 as f32,
        r.width() as f32,
        r.height() as f32,
    )
}

fn build_button(
    info: Arc<ElementInfo>,
    element_font: Option<Arc<UiFont>>,
    ctx: &BuildContext<'_>,
) -> Button {
    let faces = if info.state_media.is_empty() {
        stateful_face_children(&info)
    } else {
        Vec::new()
    };
    let (face, segments) = match faces.len() {
        1 => (faces.into_iter().next(), Vec::new()),
        0 => (None, Vec::new()),
        _ => (None, faces),
    };

    let mut label = authored_string(&info, ctx);
    let mut label_info: &ElementInfo = &info;
    if label.is_none() {
        for child in info.children.iter().filter(|c| c.kind == class::TEXT) {
            if let Some(s) = authored_string(child, ctx) {
                label = Some(s);
                label_info = child;
                break;
            }
        }
    }
    let label_is_self = std::ptr::eq(label_info, info.as_ref());
    let label_font = if label_info.font_did != 0 {
        ctx.source
            .font(label_info.font_did)
            .or_else(|| element_font.clone())
    } else {
        element_font.clone()
    };

    let mut b = Button::new(info.clone(), face.clone(), segments, ctx.media_seconds);
    b.label = label.clone();
    b.label_font = label_font;
    b.label_color = label_info
        .font_color
        .or(info.font_color)
        .unwrap_or(Color::WHITE);
    b.outline = label_info.outline || info.outline;
    if let Some(c) = label_info.outline_color.or(info.outline_color) {
        b.outline_color = c;
    }
    b.label_at_bottom = label_info.v_justify == VJustify::Bottom;

    if let Some(face) = &face {
        b.face_left = face.x;
        b.face_top = face.y;
        b.face_width = face.width;
        b.face_height = face.height;
        if !label_is_self {
            b.label_box = Some((
                label_info.x,
                label_info.y,
                label_info.width,
                label_info.height,
            ));
            b.label_align = if label_info.h_justify == HJustify::Left {
                LabelAlign::Left
            } else {
                LabelAlign::Center
            };
        } else {
            b.label_align = LabelAlign::Left;
            b.label_offset_x = face.x + face.width + 4.0;
        }
    } else if label_info.h_justify == HJustify::Left {
        b.label_align = LabelAlign::Left;
        if !label_is_self {
            b.label_offset_x = label_info.x;
        }
    }

    b.set_per_state_label_style(
        per_state_colors(label_info, 0x1B),
        per_state_bools(label_info, 0x21),
    );

    if label_is_self
        && label.is_some()
        && let Some(value) = info
            .children
            .iter()
            .find(|c| c.kind == class::TEXT && c.state_media.is_empty())
    {
        b.value_box = Some(reflow_value_child_rect(value, &info));
        b.value_font = if value.font_did != 0 {
            ctx.source
                .font(value.font_did)
                .or_else(|| element_font.clone())
        } else {
            element_font.clone()
        };
        b.value_color = value.font_color.unwrap_or(Color::WHITE);
        b.value_align = match value.h_justify {
            HJustify::Left => LabelAlign::Left,
            HJustify::Right => LabelAlign::Right,
            HJustify::Center => LabelAlign::Center,
        };
        b.value_label = authored_string(value, ctx);
    }
    b
}

fn build_checkbox(
    info: Arc<ElementInfo>,
    element_font: Option<Arc<UiFont>>,
    ctx: &BuildContext<'_>,
) -> Button {
    let indicator = stateful_face_children(&info).into_iter().next();
    let mut b = Button::new(
        info.clone(),
        indicator.clone(),
        Vec::new(),
        ctx.media_seconds,
    );
    b.label = authored_string(&info, ctx);
    b.label_font = if info.font_did != 0 {
        ctx.source.font(info.font_did).or(element_font)
    } else {
        element_font
    };
    b.label_color = info.font_color.unwrap_or(Color::WHITE);
    b.label_align = LabelAlign::Left;
    b.outline = info.outline;
    if let Some(c) = info.outline_color {
        b.outline_color = c;
    }
    if let Some(ind) = indicator {
        b.face_left = ind.x;
        b.face_top = ind.y;
        b.face_width = ind.width;
        b.face_height = ind.height;
        b.label_offset_x = ind.x + ind.width + 4.0;
    }
    b
}

/// The image of an element's effective default state, else of its direct
/// state; 0 for none.
#[must_use]
pub fn default_image(info: &ElementInfo) -> u32 {
    let id = info.effective_default_state_id();
    info.state(id)
        .and_then(|s| s.image)
        .or_else(|| info.state(DIRECT_STATE_ID).and_then(|s| s.image))
        .map_or(0, |m| m.file)
}

/// The image each named state authors; empty when fewer than two do.
fn state_images(info: &ElementInfo) -> Vec<(u32, u32)> {
    let images: Vec<(u32, u32)> = info
        .states
        .iter()
        .filter(|(id, _)| *id != DIRECT_STATE_ID)
        .filter_map(|(id, s)| s.image.map(|m| (*id, m.file)))
        .collect();
    if images.len() > 1 { images } else { Vec::new() }
}

/// The image of the state named `name` (Normal falls back to the default
/// image); 0 for none.
#[must_use]
pub fn button_state_image(info: Option<&ElementInfo>, name: &str) -> u32 {
    let Some(info) = info else {
        return 0;
    };
    if let Some(m) = info.state_media.get(name) {
        return m.file;
    }
    if let Some(image) = info.state_named(name).and_then(|(_, s)| s.image) {
        return image.file;
    }
    if name == "Normal" {
        default_image(info)
    } else {
        0
    }
}

fn build_field(info: &ElementInfo, font: Option<Arc<UiFont>>) -> Field {
    let bg_name = if !info.default_state_name.is_empty() {
        info.default_state_name.as_str()
    } else if info.state_media.contains_key("Normal") {
        "Normal"
    } else {
        ""
    };
    let mut f = Field::default();
    f.element_id = info.id;
    f.font = font;
    f.background_sprite = info.state_media.get(bg_name).map_or(0, |m| m.file);
    f.focus_field_sprite = info
        .state_media
        .get("Normal_focussed")
        .map_or(0, |m| m.file);
    f.selectable = info.effective_bool(0x27, None) == Some(true);
    f.one_line = info.effective_bool(0x20, None) == Some(true);
    f.centered = info.h_justify == HJustify::Center;
    f.right_aligned = info.h_justify == HJustify::Right;
    f.outline = info.outline;
    if let Some(max) = info.effective_integer(0x1E, None) {
        f.max_characters = usize::try_from(max).unwrap_or(0);
    }
    if let Some(c) = info.font_color {
        f.text_color = c;
    }
    if let Some(c) = info.outline_color {
        f.outline_color = c;
    }
    for child in &info.children {
        let Some(rail) = info_rail(child) else {
            continue;
        };
        if child.x < info.width * 0.5 {
            f.focus_rail_left_sprite = rail;
            if child.width > 0.0 {
                f.focus_rail_left_width = child.width;
            }
        } else {
            f.focus_rail_right_sprite = rail;
            if child.width > 0.0 {
                f.focus_rail_right_width = child.width;
            }
        }
    }
    f
}

fn info_rail(child: &ElementInfo) -> Option<u32> {
    if child.kind != class::CONTAINER {
        return None;
    }
    child
        .state_media
        .get("Normal_focussed")
        .map(|m| m.file)
        .filter(|f| *f != 0)
}

fn build_menu(
    info: &ElementInfo,
    element_font: Option<Arc<UiFont>>,
    ctx: &BuildContext<'_>,
) -> Menu {
    let label = info.children.iter().find(|c| c.kind == class::TEXT);
    let label_font = match label {
        Some(l) if l.font_did != 0 => ctx.source.font(l.font_did).or_else(|| element_font.clone()),
        _ => element_font.clone(),
    };
    let mut m = Menu::default();
    m.font = label_font.clone();
    m.button_font = label_font;
    m.normal_sprite = 0x0600_4D65;
    m.pressed_sprite = 0x0600_4D66;
    m.popup_bg_sprite = 0x0600_124C;
    m.item_normal_sprite = 0x0600_124E;
    m.item_highlight_sprite = 0x0600_124D;
    m.button_text_centered = label.is_some_and(|l| l.h_justify == HJustify::Center);
    if let Some(c) = label.and_then(|l| l.font_color) {
        m.text_color = c;
    }
    // A drop-down authors its face on its text child and its open/closed
    // arrow on the small image child beside it.
    let face = label.map_or(0, default_image);
    if face != 0 {
        m.normal_sprite = face;
        m.pressed_sprite = face;
    }
    let cap = info.children.iter().find(|c| c.kind == class::CONTAINER);
    let closed = button_state_image(cap, "Normal");
    if let Some(cap) = cap
        && closed != 0
    {
        m.arrow_cap_closed_sprite = closed;
        let open = button_state_image(Some(cap), "Highlight");
        m.arrow_cap_open_sprite = if open != 0 { open } else { closed };
        if cap.width > 0.0 {
            m.arrow_cap_width = cap.width;
        }
        if cap.height > 0.0 {
            m.arrow_cap_height = cap.height;
        }
    }
    if let Some(l) = label
        && l.height > 0.0
    {
        m.row_height = l.height;
    }
    m
}

fn slices_by(children: &[ElementInfo], horizontal: bool) -> Vec<&ElementInfo> {
    let mut v: Vec<&ElementInfo> = children.iter().filter(|c| default_image(c) != 0).collect();
    v.sort_by(|a, b| {
        let (ka, kb) = if horizontal { (a.x, b.x) } else { (a.y, b.y) };
        ka.total_cmp(&kb).then(a.read_order.cmp(&b.read_order))
    });
    v
}

fn apply_thumb_slices(bar: &mut Scrollbar, slices: &[&ElementInfo]) {
    if let Some(top) = slices.first() {
        bar.thumb_top_sprite = button_state_image(Some(top), "Normal");
        bar.thumb_top_rollover_sprite = button_state_image(Some(top), "Normal_rollover");
        bar.thumb_top_pressed_sprite = button_state_image(Some(top), "Normal_pressed");
    }
    if let Some(mid) = slices.get(1) {
        bar.thumb_sprite = button_state_image(Some(mid), "Normal");
        bar.thumb_rollover_sprite = button_state_image(Some(mid), "Normal_rollover");
        bar.thumb_pressed_sprite = button_state_image(Some(mid), "Normal_pressed");
    }
    if slices.len() > 2
        && let Some(bot) = slices.last()
    {
        bar.thumb_bot_sprite = button_state_image(Some(bot), "Normal");
        bar.thumb_bot_rollover_sprite = button_state_image(Some(bot), "Normal_rollover");
        bar.thumb_bot_pressed_sprite = button_state_image(Some(bot), "Normal_pressed");
    }
}

fn build_scrollbar(info: &ElementInfo) -> Scrollbar {
    let mut bar = Scrollbar::default();
    bar.track_sprite = default_image(info);
    bar.horizontal = info.width > info.height;
    let increment_id = referenced_element_id(info, 0x77);
    let decrement_id = referenced_element_id(info, 0x78);
    let mut leading = info.children.iter().find(|c| c.id == increment_id);
    let mut trailing = info.children.iter().find(|c| c.id == decrement_id);
    if leading.is_none() && trailing.is_none() {
        let mut ones: Vec<&ElementInfo> = info
            .children
            .iter()
            .filter(|c| c.kind == class::BUTTON && c.id != 1)
            .collect();
        ones.sort_by(|a, b| {
            let (ka, kb) = if bar.horizontal {
                (a.x, b.x)
            } else {
                (a.y, b.y)
            };
            ka.total_cmp(&kb).then(a.read_order.cmp(&b.read_order))
        });
        leading = ones.first().copied();
        trailing = if ones.len() > 1 {
            ones.last().copied()
        } else {
            None
        };
    }
    bar.up_sprite = button_state_image(leading, "Normal");
    bar.up_rollover_sprite = button_state_image(leading, "Normal_rollover");
    bar.up_pressed_sprite = button_state_image(leading, "Normal_pressed");
    bar.down_sprite = button_state_image(trailing, "Normal");
    bar.down_rollover_sprite = button_state_image(trailing, "Normal_rollover");
    bar.down_pressed_sprite = button_state_image(trailing, "Normal_pressed");
    if let Some(hide) = info.effective_bool(0x79, None) {
        bar.hide_when_disabled = hide;
    }
    if let Some(p) = info.effective_bool(0x82, None) {
        bar.proportional = p;
    }
    if let Some(min) = info.effective_integer(0x89, None)
        && min > 0
    {
        bar.min_thumb_extent = min as f32;
    }

    if bar.horizontal {
        if let Some(l) = leading.filter(|l| l.width > 0.0) {
            bar.decrement_button_extent = l.width;
        }
        if let Some(t) = trailing.filter(|t| t.width > 0.0) {
            bar.increment_button_extent = t.width;
        }
        let scalar_thumb = info.children.iter().find(|c| c.id == 1);
        bar.thumb_sprite = scalar_thumb.map_or(0, default_image);
        if let Some(t) = scalar_thumb.filter(|t| t.width > 0.0) {
            bar.thumb_extent = t.width;
        }
        if let Some(t) = scalar_thumb {
            apply_thumb_slices(&mut bar, &slices_by(&t.children, true));
        }
        // Child 4 is no track: it is the fill from the bar's start to the
        // thumb's centre, and takes the bar's state art.
        if let Some(fill) = info.children.iter().find(|c| c.id == 4) {
            bar.thumb_fill_sprite = default_image(fill);
            bar.thumb_fill_sprites_by_state = state_images(fill);
        }
        let meter = info.children.iter().find(|c| c.kind == class::METER);
        let fill = meter.and_then(|m| {
            m.children.iter().find(|c| c.id == 2).or_else(|| {
                m.children
                    .iter()
                    .filter(|c| default_image(c) != 0)
                    .max_by_key(|c| c.read_order)
            })
        });
        bar.scalar_fill_sprite = fill.map_or(0, default_image);
        let range = meter.and_then(|m| m.children.iter().find(|c| c.id == 0x1000_05EF));
        bar.scalar_range_sprite = range.map_or(0, default_image);
        bar.scalar_range_layout_policy = range.and_then(layout_policy_of);
        bar.scalar_fill_from_right = meter.is_some_and(|m| {
            matches!(
                m.effective_property(0x6F, None),
                Some(PropertyValue::Enum(3))
            )
        });
        return bar;
    }

    if let Some(l) = leading.filter(|l| l.height > 0.0) {
        bar.decrement_button_extent = l.height;
    }
    if let Some(t) = trailing.filter(|t| t.height > 0.0) {
        bar.increment_button_extent = t.height;
    }
    if let Some(thumb) = info
        .children
        .iter()
        .find(|c| c.kind == class::BUTTON && c.id != increment_id && c.id != decrement_id)
    {
        if thumb.height > 0.0 {
            bar.thumb_extent = thumb.height;
        }
        let slices = slices_by(&thumb.children, false);
        if slices.is_empty() {
            bar.thumb_sprite = button_state_image(Some(thumb), "Normal");
            bar.thumb_rollover_sprite = button_state_image(Some(thumb), "Normal_rollover");
            bar.thumb_pressed_sprite = button_state_image(Some(thumb), "Normal_pressed");
        } else {
            apply_thumb_slices(&mut bar, &slices);
        }
    }
    bar
}

fn has_three_slice_shape(container: &ElementInfo) -> bool {
    container
        .children
        .iter()
        .filter(|c| c.state_media.get("").is_some_and(|m| m.file != 0))
        .count()
        >= 3
}

fn slice_ids(container: &ElementInfo) -> (u32, u32, u32) {
    let mut slices: Vec<(f32, u32)> = container
        .children
        .iter()
        .filter_map(|c| {
            c.state_media
                .get("")
                .filter(|m| m.file != 0)
                .map(|m| (c.x, m.file))
        })
        .collect();
    slices.sort_by(|a, b| a.0.total_cmp(&b.0));
    let at = |i: usize| slices.get(i).map_or(0, |s| s.1);
    (at(0), at(1), at(2))
}

fn detail_overlay(container: &ElementInfo) -> Option<&ElementInfo> {
    container.children.iter().find(|c| {
        !c.state_media.contains_key("")
            && c.state_media.get("ShowDetail").is_some_and(|m| m.file != 0)
    })
}

fn detail_overlay_spec(overlay: Option<&ElementInfo>, container: &ElementInfo) -> DetailOverlay {
    let Some(o) = overlay else {
        return DetailOverlay::default();
    };
    DetailOverlay {
        sprite: o.state_media.get("ShowDetail").map_or(0, |m| m.file),
        x: o.x,
        y: o.y,
        w: o.width,
        h: o.height,
        left_mode: o.left,
        top_mode: o.top,
        right_mode: o.right,
        bottom_mode: o.bottom,
        parent_w: container.width,
        parent_h: container.height,
    }
}

fn has_stateful_fill(container: &ElementInfo) -> bool {
    container.states.iter().any(|(id, s)| {
        *id != DIRECT_STATE_ID
            && container
                .state_media
                .get(&s.name)
                .is_some_and(|m| m.file != 0)
    })
}

fn build_meter(info: &ElementInfo, font: Option<Arc<UiFont>>, ctx: &BuildContext<'_>) -> Meter {
    let mut m = Meter::default();
    m.element_id = info.id;
    m.font = font;
    m.outline = info.outline;
    if let Some(c) = info.outline_color {
        m.outline_color = c;
    }
    let mut containers: Vec<&ElementInfo> = info
        .children
        .iter()
        .filter(|c| c.kind == class::CONTAINER)
        .collect();
    containers.sort_by_key(|c| c.read_order);

    let direct_loop = |e: &ElementInfo| {
        e.state(DIRECT_STATE_ID)
            .and_then(|s| s.looping_animation.clone())
            .filter(|a| a.draw_mode == 1)
    };

    if containers.len() >= 2
        && has_three_slice_shape(containers[0])
        && has_three_slice_shape(containers[1])
    {
        (m.back_left, m.back_tile, m.back_right) = slice_ids(containers[0]);
        (m.front_left, m.front_tile, m.front_right) = slice_ids(containers[1]);
        let back = detail_overlay(containers[0]);
        let front = detail_overlay(containers[1]);
        if back.is_some() || front.is_some() {
            let pass = info.states.iter().any(|(_, s)| {
                (s.name == "HideDetail" || s.name == "ShowDetail") && s.pass_to_children
            });
            m.configure_detail_overlay(
                detail_overlay_spec(back, containers[0]),
                detail_overlay_spec(front, containers[1]),
                pass,
            );
        }
    } else if containers.len() == 1
        && containers[0].x == 0.0
        && containers[0].y == 0.0
        && containers[0].width == info.width
        && containers[0].height == info.height
        && let (Some(back), Some(front)) = (direct_loop(info), direct_loop(containers[0]))
        && back.duration == front.duration
    {
        m.configure_animated_tracks(back, front);
    } else if containers.len() == 1 && containers[0].state_media.contains_key("") {
        m.back_tile = info.state_media.get("").map_or(0, |s| s.file);
        m.front_tile = containers[0].state_media.get("").map_or(0, |s| s.file);
    } else if containers.iter().any(|c| has_stateful_fill(c)) {
        m.back_tile = info.state_media.get("").map_or(0, |s| s.file);
        for c in &containers {
            for (id, s) in &c.states {
                if *id == DIRECT_STATE_ID {
                    continue;
                }
                if let Some(media) = c.state_media.get(&s.name) {
                    m.configure_state_fill(*id, media.file);
                }
            }
        }
        for text in info.children.iter().filter(|c| c.kind == class::TEXT) {
            for (id, s) in &text.states {
                if *id == DIRECT_STATE_ID {
                    continue;
                }
                let Some(PropertyValue::StringInfo(caption)) =
                    s.properties.get(0x17).map(|p| &p.value)
                else {
                    continue;
                };
                let Some(t) = ctx.string(caption).filter(|t| !t.is_empty()) else {
                    continue;
                };
                // The state's own justification wins over the element's.
                let justify = match s.properties.get(0x14).map(|p| &p.value) {
                    Some(PropertyValue::Enum(raw)) => {
                        crate::element_info::map_horizontal_justification(u64::from(*raw))
                    }
                    _ => text.h_justify,
                };
                let align = match justify {
                    HJustify::Left => MeterLabelAlign::Left,
                    HJustify::Right => MeterLabelAlign::Right,
                    HJustify::Center => MeterLabelAlign::Center,
                };
                m.configure_state_label(*id, &t, align);
            }
        }
    }
    m
}

fn tooltip_text(info: &ElementInfo, ctx: &BuildContext<'_>) -> Option<String> {
    info.tooltip_text.as_ref().and_then(|t| ctx.string(t))
}

/// Build `info` and its subtree as detached elements in `ui`.
pub fn build(ui: &mut UiRoot, info: &ElementInfo, ctx: &BuildContext<'_>) -> ImportedLayout {
    let mut by_id = HashMap::new();
    let root = build_widget(ui, info, ctx, &mut by_id);
    ImportedLayout { root, by_id }
}

fn build_widget(
    ui: &mut UiRoot,
    info: &ElementInfo,
    ctx: &BuildContext<'_>,
    by_id: &mut HashMap<u32, ElementId>,
) -> ElementId {
    let (mut element, widget) = create(info, ctx);
    element.source_layout_did = ctx.source_layout_did;
    element.authored = crate::tree::Authored {
        invisible: info.invisible,
        tooltip_enabled: info.tooltip_enabled,
        tooltip_text: tooltip_text(info, ctx),
        tooltip_root_element_id: info.tooltip_root_element_id,
        tooltip_layout_did: info.tooltip_layout_did,
        tooltip_text_child_element_id: info.tooltip_text_child_element_id,
        tooltip_delay_seconds: info.tooltip_delay_seconds,
        resize_max_width: info.max_width,
        resize_min_width: info.min_width,
        resize_max_height: info.max_height,
        resize_min_height: info.min_height,
    };
    if info.invisible {
        element.visible = false;
    }
    let policy = widget.child_import();
    let id = ui.insert_boxed(element, widget);
    if info.id != 0 {
        by_id.insert(info.id, id);
    }
    for child in &info.children {
        let wanted = match policy {
            ChildImport::All => true,
            ChildImport::None => false,
            ChildImport::NonContainers => child.kind != class::CONTAINER,
            ChildImport::MediaBearing => !child.state_media.is_empty(),
            ChildImport::Buttons => is_child_button(child),
        };
        if wanted {
            let c = build_widget(ui, child, ctx, by_id);
            ui.add_child(id, c);
        }
    }
    // An element enters its authored default state through its own state
    // change (a button whose default is Ghosted is disabled and hidden by
    // it); without one it keeps the state it chose on construction. A
    // button then settles its look from its inputs.
    let authored_default = (info.default_state_id != 0 && info.has_state(info.default_state_id))
        .then_some(info.default_state_id);
    if let Some(state) = authored_default.or_else(|| ui.retail_state(id)) {
        ui.set_retail_state(id, state);
    }
    ui.with::<Button, _>(id, |b, ui| b.refresh(ui, id));
    ui.with_widget(id, |w, ui| w.on_children_attached(ui, id));
    id
}

/// Read layout `layout_id` and build it (detached) in `ui`.
pub fn import(
    ui: &mut UiRoot,
    source: &dyn LayoutSource,
    layout_id: u32,
    default_font: Option<Arc<UiFont>>,
) -> Option<ImportedLayout> {
    let info = import_infos(source, layout_id)?;
    let ctx = BuildContext {
        source,
        default_font,
        source_layout_did: layout_id,
        media_seconds: ui.media_seconds(),
    };
    Some(build(ui, &info, &ctx))
}

/// Read the subtree of element `root_element_id` of layout `layout_id` and
/// build it (detached) in `ui`.
pub fn import_rooted(
    ui: &mut UiRoot,
    source: &dyn LayoutSource,
    layout_id: u32,
    root_element_id: u32,
    default_font: Option<Arc<UiFont>>,
) -> Option<ImportedLayout> {
    let info = import_infos_rooted(source, layout_id, root_element_id)?;
    let ctx = BuildContext {
        source,
        default_font,
        source_layout_did: layout_id,
        media_seconds: ui.media_seconds(),
    };
    Some(build(ui, &info, &ctx))
}

/// Referenced element id of property `id` (see
/// [`crate::element_info::referenced_id`]).
#[must_use]
pub fn referenced_element_id(info: &ElementInfo, id: u32) -> u32 {
    referenced_id(info.effective_property(id, None))
}
