//! The importer's description of one element: its dat fields, its states
//! with their media and properties, and the values projected from those
//! properties (font, colours, justification, margins, tooltip, ...).

use std::collections::HashMap;
use std::sync::Arc;

use ac_dat::objects::{DIRECT_STATE_ID, Property, PropertyValue, StringInfo};

use crate::draw::Color;
use crate::geometry::AnchorEdges;
use crate::media::{LoopingAnimation, MediaStep};
use crate::text::{HJustify, VJustify};

/// An image a state shows: render surface and draw mode.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub struct ImageMedia {
    /// Render surface id.
    pub file: u32,
    /// Draw mode.
    pub draw_mode: u32,
}

/// A cursor a state shows.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub struct CursorMedia {
    /// Render surface id (0: none).
    pub file: u32,
    /// Hot spot x, pixels.
    pub hotspot_x: i32,
    /// Hot spot y, pixels.
    pub hotspot_y: i32,
}

impl CursorMedia {
    /// True when there is a cursor image.
    #[must_use]
    pub fn is_valid(&self) -> bool {
        self.file != 0
    }
}

/// Properties keyed by property id, in insertion order; setting an id again
/// replaces its value in place.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct PropertyBag {
    values: Vec<(u32, Property)>,
}

impl PropertyBag {
    /// Set property `id`.
    pub fn set(&mut self, id: u32, value: Property) {
        match self.values.iter_mut().find(|(k, _)| *k == id) {
            Some((_, v)) => *v = value,
            None => self.values.push((id, value)),
        }
    }

    /// Set property `id` to a plain value (master id = `id`).
    pub fn set_value(&mut self, id: u32, value: PropertyValue) {
        self.set(
            id,
            Property {
                master_property_id: id,
                value,
            },
        );
    }

    /// Property `id`.
    #[must_use]
    pub fn get(&self, id: u32) -> Option<&Property> {
        self.values.iter().find(|(k, _)| *k == id).map(|(_, v)| v)
    }

    /// Every property, in order.
    pub fn iter(&self) -> impl Iterator<Item = &(u32, Property)> {
        self.values.iter()
    }

    /// Number of properties.
    #[must_use]
    pub fn len(&self) -> usize {
        self.values.len()
    }

    /// True when there are none.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.values.is_empty()
    }

    /// The base's properties with the derived ones laid over them.
    #[must_use]
    pub fn merge(base: &PropertyBag, derived: &PropertyBag) -> PropertyBag {
        let mut merged = base.clone();
        for (k, v) in &derived.values {
            merged.set(*k, v.clone());
        }
        merged
    }
}

/// One state of an element.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct StateInfo {
    /// State id ([`DIRECT_STATE_ID`] for the unnamed default state).
    pub id: u32,
    /// State name ("" for the default state).
    pub name: String,
    /// Entering it also sets it on the children.
    pub pass_to_children: bool,
    /// Incorporation flags.
    pub incorporation_flags: u32,
    /// The first non-zero image of the program.
    pub image: Option<ImageMedia>,
    /// A looping animation (an animation then a jump back to it).
    pub looping_animation: Option<Arc<LoopingAnimation>>,
    /// The whole media program, in order.
    pub media_steps: Arc<Vec<MediaStep>>,
    /// The cursor.
    pub cursor: Option<CursorMedia>,
    /// Properties.
    pub properties: PropertyBag,
    /// Media entries authored.
    pub media_count: usize,
    /// Image entries authored.
    pub image_media_count: usize,
}

impl StateInfo {
    /// A state with only an id and a name.
    #[must_use]
    pub fn named(id: u32, name: impl Into<String>) -> Self {
        Self {
            id,
            name: name.into(),
            ..Self::default()
        }
    }

    /// The derived state laid over the base: the derived identity, the
    /// derived media where present, merged properties.
    #[must_use]
    pub fn merge(base: &StateInfo, derived: &StateInfo) -> StateInfo {
        StateInfo {
            id: derived.id,
            name: derived.name.clone(),
            pass_to_children: derived.pass_to_children,
            incorporation_flags: derived.incorporation_flags,
            image: derived.image.or(base.image),
            looping_animation: derived
                .looping_animation
                .clone()
                .or_else(|| base.looping_animation.clone()),
            // As OpenAC: the merged state keeps no media program.
            media_steps: Arc::default(),
            cursor: derived.cursor.or(base.cursor),
            properties: PropertyBag::merge(&base.properties, &derived.properties),
            media_count: base.media_count + derived.media_count,
            image_media_count: base.image_media_count + derived.image_media_count,
        }
    }
}

/// A tab-table entry: the tab button and the page it shows.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct TabEntry {
    /// Tab button element id.
    pub button: u32,
    /// Page element id.
    pub page: u32,
    /// Whether this tab starts selected.
    pub is_default: bool,
}

/// A template-list entry: the layout and element a list row is built from.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct TemplateEntry {
    /// Layout id.
    pub layout: u32,
    /// Element id within it.
    pub element: u32,
}

/// The importer's description of one element.
#[derive(Debug, Clone, PartialEq)]
pub struct ElementInfo {
    /// Element id.
    pub id: u32,
    /// Element type (widget class).
    pub kind: u32,
    /// Left in the parent, pixels.
    pub x: f32,
    /// Top in the parent, pixels.
    pub y: f32,
    /// Width, pixels.
    pub width: f32,
    /// Height, pixels.
    pub height: f32,
    /// The parent's size the geometry was authored against, when known.
    pub original_parent_size: Option<(f32, f32)>,
    /// Left edge mode.
    pub left: u32,
    /// Top edge mode.
    pub top: u32,
    /// Right edge mode.
    pub right: u32,
    /// Bottom edge mode.
    pub bottom: u32,
    /// Order among siblings (lower draws first).
    pub read_order: u32,
    /// Z level (higher draws behind).
    pub z_level: i32,
    /// States by id, in authored order.
    pub states: Vec<(u32, StateInfo)>,
    /// State entered on creation (0: none).
    pub default_state_id: u32,
    /// Font (property 0x1A's first data id); 0 for none.
    pub font_did: u32,
    /// Horizontal justification (property 0x14).
    pub h_justify: HJustify,
    /// Vertical justification (property 0x15).
    pub v_justify: VJustify,
    /// Font colour (property 0x1B).
    pub font_color: Option<Color>,
    /// Tag font colour (property 0x1D).
    pub tag_font_color: Option<Color>,
    /// Outlined text (property 0x21).
    pub outline: bool,
    /// Outline colour (property 0x22).
    pub outline_color: Option<Color>,
    /// Image per state name ("" for the default state).
    pub state_media: HashMap<String, ImageMedia>,
    /// Cursor per state name.
    pub state_cursors: HashMap<String, CursorMedia>,
    /// Name of the default state ("" for none).
    pub default_state_name: String,
    /// Children.
    pub children: Vec<ElementInfo>,
    /// Tab table (property 0x2E).
    pub tab_table: Vec<TabEntry>,
    /// Row templates (property 0x64).
    pub template_list: Vec<TemplateEntry>,
    /// Lamp image when checked (property 0x10000082).
    pub led_checked_sprite: u32,
    /// Lamp image when unchecked (property 0x10000083).
    pub led_unchecked_sprite: u32,
    /// Linked scrollbar element (property 0x72).
    pub scrollbar_element_id: u32,
    /// Hidden on creation (property 0x3B).
    pub invisible: bool,
    /// Text margin left (property 0x23).
    pub margin_left: i32,
    /// Text margin right (property 0x24).
    pub margin_right: i32,
    /// Text margin top (property 0x25).
    pub margin_top: i32,
    /// Text margin bottom (property 0x26).
    pub margin_bottom: i32,
    /// Tooltips enabled (property 0x4B).
    pub tooltip_enabled: bool,
    /// Tooltip text (property 0x49).
    pub tooltip_text: Option<StringInfo>,
    /// Tooltip popup root element (property 0x47).
    pub tooltip_root_element_id: u32,
    /// Tooltip popup layout (property 0x48).
    pub tooltip_layout_did: u32,
    /// Tooltip text element inside the popup (property 0x4A).
    pub tooltip_text_child_element_id: u32,
    /// Tooltip delay override, seconds (property 0x50).
    pub tooltip_delay_seconds: Option<f32>,
    /// Largest width (property 0x3D).
    pub max_width: Option<i32>,
    /// Smallest width (property 0x3F).
    pub min_width: Option<i32>,
    /// Largest height (property 0x3C).
    pub max_height: Option<i32>,
    /// Smallest height (property 0x3E).
    pub min_height: Option<i32>,
}

impl Default for ElementInfo {
    fn default() -> Self {
        Self {
            id: 0,
            kind: 0,
            x: 0.0,
            y: 0.0,
            width: 0.0,
            height: 0.0,
            original_parent_size: None,
            left: 0,
            top: 0,
            right: 0,
            bottom: 0,
            read_order: 0,
            z_level: 0,
            states: Vec::new(),
            default_state_id: 0,
            font_did: 0,
            h_justify: HJustify::Left,
            v_justify: VJustify::Top,
            font_color: None,
            tag_font_color: None,
            outline: false,
            outline_color: None,
            state_media: HashMap::new(),
            state_cursors: HashMap::new(),
            default_state_name: String::new(),
            children: Vec::new(),
            tab_table: Vec::new(),
            template_list: Vec::new(),
            led_checked_sprite: 0,
            led_unchecked_sprite: 0,
            scrollbar_element_id: 0,
            invisible: false,
            margin_left: 0,
            margin_right: 0,
            margin_top: 0,
            margin_bottom: 0,
            tooltip_enabled: false,
            tooltip_text: None,
            tooltip_root_element_id: 0,
            tooltip_layout_did: 0,
            tooltip_text_child_element_id: 0,
            tooltip_delay_seconds: None,
            max_width: None,
            min_width: None,
            max_height: None,
            min_height: None,
        }
    }
}

impl ElementInfo {
    /// State `id`.
    #[must_use]
    pub fn state(&self, id: u32) -> Option<&StateInfo> {
        self.states.iter().find(|(k, _)| *k == id).map(|(_, s)| s)
    }

    /// True when state `id` is authored.
    #[must_use]
    pub fn has_state(&self, id: u32) -> bool {
        self.state(id).is_some()
    }

    /// Set (or replace) state `id`, keeping its position.
    pub fn set_state(&mut self, id: u32, state: StateInfo) {
        match self.states.iter_mut().find(|(k, _)| *k == id) {
            Some((_, s)) => *s = state,
            None => self.states.push((id, state)),
        }
    }

    /// The first state named `name`.
    #[must_use]
    pub fn state_named(&self, name: &str) -> Option<(u32, &StateInfo)> {
        self.states
            .iter()
            .find(|(_, s)| s.name == name)
            .map(|(k, s)| (*k, s))
    }

    /// The state an element starts in: the authored default when it exists,
    /// else Normal (1) when authored, else the unnamed default state.
    #[must_use]
    pub fn effective_default_state_id(&self) -> u32 {
        if self.default_state_id != 0 && self.has_state(self.default_state_id) {
            self.default_state_id
        } else if self.has_state(1) {
            1
        } else {
            DIRECT_STATE_ID
        }
    }

    /// Property `id` as state `state` (default: the effective default state)
    /// sees it: the default state's value, overridden by the state's own.
    #[must_use]
    pub fn effective_property(&self, id: u32, state: Option<u32>) -> Option<&PropertyValue> {
        let effective = state.unwrap_or_else(|| self.effective_default_state_id());
        let mut resolved = self
            .state(DIRECT_STATE_ID)
            .and_then(|s| s.properties.get(id));
        if effective != DIRECT_STATE_ID
            && let Some(p) = self.state(effective).and_then(|s| s.properties.get(id))
        {
            resolved = Some(p);
        }
        resolved.map(|p| &p.value)
    }

    /// A bool property, when present as a bool.
    #[must_use]
    pub fn effective_bool(&self, id: u32, state: Option<u32>) -> Option<bool> {
        match self.effective_property(id, state) {
            Some(PropertyValue::Bool(b)) => Some(*b),
            _ => None,
        }
    }

    /// An integer property, when present as an integer.
    #[must_use]
    pub fn effective_integer(&self, id: u32, state: Option<u32>) -> Option<i32> {
        match self.effective_property(id, state) {
            Some(PropertyValue::Integer(i)) => Some(*i),
            _ => None,
        }
    }

    /// A float property, when present as a float.
    #[must_use]
    pub fn effective_float(&self, id: u32, state: Option<u32>) -> Option<f32> {
        match self.effective_property(id, state) {
            Some(PropertyValue::Float(f)) => Some(*f),
            _ => None,
        }
    }

    /// The image of state `name`, or the default state's.
    #[must_use]
    pub fn media_or_default(&self, name: &str) -> Option<ImageMedia> {
        self.state_media
            .get(name)
            .or_else(|| self.state_media.get(""))
            .copied()
    }
}

/// The compatibility anchors of the raw edge modes: a near edge of mode 1 or
/// 4 anchors that side; a far edge of mode 1 or 4, or a near edge of mode 2,
/// anchors the far side; none at all means top-left.
#[must_use]
pub fn to_anchors(left: u32, top: u32, right: u32, bottom: u32) -> AnchorEdges {
    let mut a = AnchorEdges::NONE;
    if left == 1 || left == 4 {
        a |= AnchorEdges::LEFT;
    }
    if right == 1 || right == 4 || left == 2 {
        a |= AnchorEdges::RIGHT;
    }
    if top == 1 || top == 4 {
        a |= AnchorEdges::TOP;
    }
    if bottom == 1 || bottom == 4 || top == 2 {
        a |= AnchorEdges::BOTTOM;
    }
    if a.is_empty() {
        AnchorEdges::TOP_LEFT
    } else {
        a
    }
}

/// Retail horizontal justification values: 1 centre, 3 and 5 right, the
/// rest left.
#[must_use]
pub fn map_horizontal_justification(raw: u64) -> HJustify {
    match raw {
        1 => HJustify::Center,
        3 | 5 => HJustify::Right,
        _ => HJustify::Left,
    }
}

/// Retail vertical justification values: 1 centre, 3 and 5 bottom, the
/// rest top.
#[must_use]
pub fn map_vertical_justification(raw: u64) -> VJustify {
    match raw {
        1 => VJustify::Center,
        3 | 5 => VJustify::Bottom,
        _ => VJustify::Top,
    }
}

/// A colour property, or the first colour of a colour array.
#[must_use]
pub fn color_of(value: &PropertyValue) -> Option<Color> {
    match value {
        PropertyValue::Color(c) => Some(Color::from_dat(*c)),
        PropertyValue::Array(items) => match items.first().map(|p| &p.value) {
            Some(PropertyValue::Color(c)) => Some(Color::from_dat(*c)),
            _ => None,
        },
        _ => None,
    }
}

/// An element id named by a property: an enum or data id, or a
/// non-negative integer; 0 otherwise.
#[must_use]
pub fn referenced_id(value: Option<&PropertyValue>) -> u32 {
    match value {
        Some(PropertyValue::Enum(v) | PropertyValue::DataId(v)) => *v,
        Some(PropertyValue::Integer(i)) if *i >= 0 => *i as u32,
        _ => 0,
    }
}

fn struct_member(members: &[(u32, Property)], key: u32) -> Option<&PropertyValue> {
    members
        .iter()
        .find(|(k, _)| *k == key)
        .map(|(_, p)| &p.value)
}

fn read_tab_table(info: &ElementInfo) -> Vec<TabEntry> {
    let Some(PropertyValue::Array(items)) = info.effective_property(0x2E, None) else {
        return Vec::new();
    };
    let mut entries = Vec::new();
    for item in items {
        let PropertyValue::Struct(members) = &item.value else {
            continue;
        };
        let button = referenced_id(struct_member(members, 0x30));
        let page = referenced_id(struct_member(members, 0x31));
        if button == 0 || page == 0 {
            continue;
        }
        let is_default = matches!(
            struct_member(members, 0x32),
            Some(PropertyValue::Bool(true))
        );
        entries.push(TabEntry {
            button,
            page,
            is_default,
        });
    }
    entries
}

fn read_template_list(info: &ElementInfo) -> Vec<TemplateEntry> {
    let Some(PropertyValue::Array(items)) = info.effective_property(0x64, None) else {
        return Vec::new();
    };
    items
        .iter()
        .filter_map(|item| match &item.value {
            PropertyValue::Struct(members) => Some(TemplateEntry {
                layout: referenced_id(struct_member(members, 0x63)),
                element: referenced_id(struct_member(members, 0x62)),
            }),
            _ => None,
        })
        .collect()
}

/// Recompute the projected fields from the effective properties: each
/// field authored (directly or through the default state) takes its
/// property's value; the lists and referenced ids are rebuilt outright.
pub fn apply_canonical_projection(info: &mut ElementInfo) {
    if let Some(PropertyValue::Array(items)) = info.effective_property(0x1A, None)
        && let Some(PropertyValue::DataId(did)) = items.first().map(|p| &p.value)
    {
        info.font_did = *did;
    }
    if let Some(PropertyValue::Enum(v)) = info.effective_property(0x14, None) {
        info.h_justify = map_horizontal_justification(u64::from(*v));
    }
    if let Some(PropertyValue::Enum(v)) = info.effective_property(0x15, None) {
        info.v_justify = map_vertical_justification(u64::from(*v));
    }
    if let Some(c) = info.effective_property(0x1B, None).and_then(color_of) {
        info.font_color = Some(c);
    }
    if let Some(c) = info.effective_property(0x1D, None).and_then(color_of) {
        info.tag_font_color = Some(c);
    }
    if let Some(b) = info.effective_bool(0x21, None) {
        info.outline = b;
    }
    if let Some(c) = info.effective_property(0x22, None).and_then(color_of) {
        info.outline_color = Some(c);
    }
    if let Some(v) = info.effective_integer(0x23, None) {
        info.margin_left = v;
    }
    if let Some(v) = info.effective_integer(0x24, None) {
        info.margin_right = v;
    }
    if let Some(v) = info.effective_integer(0x25, None) {
        info.margin_top = v;
    }
    if let Some(v) = info.effective_integer(0x26, None) {
        info.margin_bottom = v;
    }
    info.tab_table = read_tab_table(info);
    info.template_list = read_template_list(info);
    info.scrollbar_element_id = referenced_id(info.effective_property(0x72, None));
    info.led_checked_sprite = referenced_id(info.effective_property(0x1000_0082, None));
    info.led_unchecked_sprite = referenced_id(info.effective_property(0x1000_0083, None));
    if let Some(b) = info.effective_bool(0x3B, None) {
        info.invisible = b;
    }
    if let Some(b) = info.effective_bool(0x4B, None) {
        info.tooltip_enabled = b;
    }
    if let Some(PropertyValue::StringInfo(s)) = info.effective_property(0x49, None) {
        info.tooltip_text = Some(*s);
    }
    info.tooltip_root_element_id = referenced_id(info.effective_property(0x47, None));
    info.tooltip_layout_did = referenced_id(info.effective_property(0x48, None));
    info.tooltip_text_child_element_id = referenced_id(info.effective_property(0x4A, None));
    if let Some(f) = info.effective_float(0x50, None) {
        info.tooltip_delay_seconds = Some(f);
    }
    if let Some(v) = info.effective_integer(0x3D, None) {
        info.max_width = Some(v);
    }
    if let Some(v) = info.effective_integer(0x3F, None) {
        info.min_width = Some(v);
    }
    if let Some(v) = info.effective_integer(0x3C, None) {
        info.max_height = Some(v);
    }
    if let Some(v) = info.effective_integer(0x3E, None) {
        info.min_height = Some(v);
    }
}

fn has_effective_enum(info: &ElementInfo, id: u32) -> bool {
    matches!(
        info.effective_property(id, None),
        Some(PropertyValue::Enum(_))
    )
}

/// An element that inherits from `base`: a field the derived element sets
/// (non-zero, non-empty, present) wins, the base fills the rest; images,
/// cursors and states are laid over the base's; children come from the
/// derived element alone. The projected fields are then recomputed.
///
/// Width and height of 0 mean "inherit" (a zero-size derived element cannot
/// override a sized base; no retail element needs to).
#[must_use]
pub fn merge(base: &ElementInfo, derived: &ElementInfo) -> ElementInfo {
    let pick = |d: u32, b: u32| if d != 0 { d } else { b };
    let pick_i = |d: i32, b: i32| if d != 0 { d } else { b };
    let pick_f = |d: f32, b: f32| if d != 0.0 { d } else { b };
    let mut m = ElementInfo {
        id: pick(derived.id, base.id),
        kind: pick(derived.kind, base.kind),
        x: derived.x,
        y: derived.y,
        width: pick_f(derived.width, base.width),
        height: pick_f(derived.height, base.height),
        original_parent_size: None,
        left: derived.left,
        top: derived.top,
        right: derived.right,
        bottom: derived.bottom,
        read_order: derived.read_order,
        z_level: pick_i(derived.z_level, base.z_level),
        default_state_id: pick(derived.default_state_id, base.default_state_id),
        font_did: pick(derived.font_did, base.font_did),
        h_justify: if has_effective_enum(derived, 0x14) {
            derived.h_justify
        } else {
            base.h_justify
        },
        v_justify: if has_effective_enum(derived, 0x15) {
            derived.v_justify
        } else {
            base.v_justify
        },
        font_color: derived.font_color.or(base.font_color),
        tag_font_color: derived.tag_font_color.or(base.tag_font_color),
        outline: derived.outline || base.outline,
        outline_color: derived.outline_color.or(base.outline_color),
        margin_left: pick_i(derived.margin_left, base.margin_left),
        margin_right: pick_i(derived.margin_right, base.margin_right),
        margin_top: pick_i(derived.margin_top, base.margin_top),
        margin_bottom: pick_i(derived.margin_bottom, base.margin_bottom),
        default_state_name: if derived.default_state_name.is_empty() {
            base.default_state_name.clone()
        } else {
            derived.default_state_name.clone()
        },
        children: derived.children.clone(),
        ..ElementInfo::default()
    };
    m.state_media = base.state_media.clone();
    for (k, v) in &derived.state_media {
        m.state_media.insert(k.clone(), *v);
    }
    m.state_cursors = base.state_cursors.clone();
    for (k, v) in &derived.state_cursors {
        m.state_cursors.insert(k.clone(), *v);
    }
    m.states = base.states.clone();
    for (id, state) in &derived.states {
        let merged = match m.state(*id) {
            Some(b) => StateInfo::merge(b, state),
            None => state.clone(),
        };
        m.set_state(*id, merged);
    }
    apply_canonical_projection(&mut m);
    m
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.App.Tests/UI/Layout/ElementReaderTests.cs`
    //! and `UiPropertyBagTests.cs`.

    use super::*;
    use ac_dat::types::ColorArgb;

    fn prop(value: PropertyValue) -> Property {
        Property {
            master_property_id: 0,
            value,
        }
    }

    fn with_direct(id: u32, value: PropertyValue) -> ElementInfo {
        let mut info = ElementInfo::default();
        let mut direct = StateInfo::named(DIRECT_STATE_ID, "");
        direct.properties.set_value(id, value);
        info.set_state(DIRECT_STATE_ID, direct);
        info
    }

    fn projected(id: u32, value: PropertyValue) -> ElementInfo {
        let mut info = with_direct(id, value);
        apply_canonical_projection(&mut info);
        info
    }

    fn strct(members: &[(u32, PropertyValue)]) -> Property {
        prop(PropertyValue::Struct(
            members.iter().map(|(k, v)| (*k, prop(v.clone()))).collect(),
        ))
    }

    #[test]
    fn to_anchors_follows_the_edge_modes() {
        let a = to_anchors(1, 1, 1, 2);
        assert_eq!(a, AnchorEdges::LEFT | AnchorEdges::TOP | AnchorEdges::RIGHT);
        assert_eq!(to_anchors(1, 1, 2, 2), AnchorEdges::TOP_LEFT);
        assert_eq!(
            to_anchors(2, 1, 1, 2),
            AnchorEdges::TOP | AnchorEdges::RIGHT
        );
        assert_eq!(
            to_anchors(1, 1, 2, 1),
            AnchorEdges::LEFT | AnchorEdges::TOP | AnchorEdges::BOTTOM
        );
        assert_eq!(to_anchors(1, 1, 1, 1), AnchorEdges::ALL);
        assert_eq!(to_anchors(0, 0, 0, 0), AnchorEdges::TOP_LEFT);
        assert_eq!(
            to_anchors(3, 1, 3, 1),
            AnchorEdges::TOP | AnchorEdges::BOTTOM
        );
    }

    #[test]
    fn merge_derived_wins_base_fills() {
        let base = ElementInfo {
            font_did: 0x4000_0000,
            width: 150.0,
            height: 16.0,
            ..ElementInfo::default()
        };
        let derived = ElementInfo {
            width: 200.0,
            ..ElementInfo::default()
        };
        let m = merge(&base, &derived);
        assert_eq!((m.width, m.height, m.font_did), (200.0, 16.0, 0x4000_0000));
        let derived = ElementInfo {
            font_did: 0x4000_0001,
            width: 100.0,
            ..ElementInfo::default()
        };
        assert_eq!(merge(&base, &derived).font_did, 0x4000_0001);
    }

    #[test]
    fn merge_lays_derived_media_and_cursors_over_the_base() {
        let mut base = ElementInfo::default();
        base.state_media.insert(
            String::new(),
            ImageMedia {
                file: 0x0600_1000,
                draw_mode: 1,
            },
        );
        base.state_media.insert(
            "HideDetail".into(),
            ImageMedia {
                file: 0x0600_1001,
                draw_mode: 1,
            },
        );
        base.state_cursors.insert(
            String::new(),
            CursorMedia {
                file: 0x0600_3000,
                hotspot_x: 1,
                hotspot_y: 2,
            },
        );
        base.state_cursors.insert(
            "Drag_rollover_accept".into(),
            CursorMedia {
                file: 0x0600_3001,
                hotspot_x: 3,
                hotspot_y: 4,
            },
        );
        let mut derived = ElementInfo::default();
        derived.state_media.insert(
            String::new(),
            ImageMedia {
                file: 0x0600_2000,
                draw_mode: 3,
            },
        );
        derived.state_cursors.insert(
            String::new(),
            CursorMedia {
                file: 0x0600_4000,
                hotspot_x: 5,
                hotspot_y: 6,
            },
        );
        let m = merge(&base, &derived);
        assert_eq!(
            m.state_media[""],
            ImageMedia {
                file: 0x0600_2000,
                draw_mode: 3
            }
        );
        assert_eq!(
            m.state_media["HideDetail"],
            ImageMedia {
                file: 0x0600_1001,
                draw_mode: 1
            }
        );
        assert_eq!(m.state_cursors[""].file, 0x0600_4000);
        assert_eq!(m.state_cursors["Drag_rollover_accept"].file, 0x0600_3001);
    }

    #[test]
    fn merge_children_come_from_the_derived_element() {
        let mut base = ElementInfo::default();
        base.children.push(ElementInfo {
            id: 1,
            ..ElementInfo::default()
        });
        let mut derived = ElementInfo::default();
        derived.children.push(ElementInfo {
            id: 2,
            ..ElementInfo::default()
        });
        let m = merge(&base, &derived);
        assert_eq!(m.children.len(), 1);
        assert_eq!(m.children[0].id, 2);
    }

    #[test]
    fn merge_justification_is_presence_based() {
        let m = merge(
            &projected(0x14, PropertyValue::Enum(1)),
            &projected(0x14, PropertyValue::Enum(3)),
        );
        assert_eq!(m.h_justify, HJustify::Right);
        let m = merge(
            &projected(0x14, PropertyValue::Enum(2)),
            &projected(0x14, PropertyValue::Enum(1)),
        );
        assert_eq!(m.h_justify, HJustify::Center);
        let m = merge(
            &projected(0x15, PropertyValue::Enum(4)),
            &ElementInfo::default(),
        );
        assert_eq!(m.v_justify, VJustify::Top);
        let m = merge(
            &projected(0x15, PropertyValue::Enum(1)),
            &projected(0x15, PropertyValue::Enum(3)),
        );
        assert_eq!(m.v_justify, VJustify::Bottom);
    }

    #[test]
    fn merge_colours_and_outline() {
        let red = Some(Color::rgba(1.0, 0.0, 0.0, 1.0));
        let gold = Some(Color::rgba(1.0, 0.82, 0.36, 1.0));
        let e = |font_color, outline, outline_color| ElementInfo {
            font_color,
            outline,
            outline_color,
            ..ElementInfo::default()
        };
        assert_eq!(
            merge(&e(None, false, None), &e(red, false, None)).font_color,
            red
        );
        assert_eq!(
            merge(&e(gold, false, None), &e(None, false, None)).font_color,
            gold
        );
        assert_eq!(
            merge(&e(None, false, None), &e(None, false, None)).font_color,
            None
        );
        assert!(merge(&e(None, false, None), &e(None, true, None)).outline);
        assert!(merge(&e(None, true, None), &e(None, false, None)).outline);
        assert!(!merge(&e(None, false, None), &e(None, false, None)).outline);
        let blue = Some(Color::rgba(0.0, 0.0, 0.4, 1.0));
        assert_eq!(
            merge(&e(None, false, None), &e(None, false, blue)).outline_color,
            blue
        );
        assert_eq!(
            merge(&e(None, false, blue), &e(None, false, None)).outline_color,
            blue
        );
        assert_eq!(
            merge(&e(None, false, None), &e(None, false, None)).outline_color,
            None
        );
    }

    #[test]
    fn unauthored_justification_matches_the_retail_constructors() {
        let info = ElementInfo::default();
        assert_eq!(
            (info.h_justify, info.v_justify),
            (HJustify::Left, VJustify::Top)
        );
    }

    #[test]
    fn every_retail_justification_value() {
        for (raw, h, v) in [
            (0, HJustify::Left, VJustify::Top),
            (1, HJustify::Center, VJustify::Center),
            (2, HJustify::Left, VJustify::Top),
            (3, HJustify::Right, VJustify::Bottom),
            (4, HJustify::Left, VJustify::Top),
            (5, HJustify::Right, VJustify::Bottom),
        ] {
            assert_eq!(projected(0x14, PropertyValue::Enum(raw)).h_justify, h);
            assert_eq!(projected(0x15, PropertyValue::Enum(raw)).v_justify, v);
        }
    }

    #[test]
    fn tab_table_decodes_in_authored_order_and_skips_incomplete_entries() {
        let table = PropertyValue::Array(vec![
            strct(&[
                (0x30, PropertyValue::Enum(0x1000_AAAA)),
                (0x31, PropertyValue::Enum(0x1000_BBBB)),
                (0x32, PropertyValue::Bool(true)),
            ]),
            strct(&[(0x30, PropertyValue::Enum(0x1000_EEEE))]),
            strct(&[(0x31, PropertyValue::Enum(0x1000_FFFF))]),
            strct(&[
                (0x30, PropertyValue::Enum(0x1000_CCCC)),
                (0x31, PropertyValue::Enum(0x1000_DDDD)),
            ]),
        ]);
        let info = projected(0x2E, table);
        assert_eq!(
            info.tab_table,
            [
                TabEntry {
                    button: 0x1000_AAAA,
                    page: 0x1000_BBBB,
                    is_default: true
                },
                TabEntry {
                    button: 0x1000_CCCC,
                    page: 0x1000_DDDD,
                    is_default: false
                },
            ]
        );
    }

    #[test]
    fn template_list_decodes_in_authored_order() {
        let list = PropertyValue::Array(vec![
            strct(&[
                (0x63, PropertyValue::Enum(0x2100_0099)),
                (0x62, PropertyValue::Enum(0x1000_3333)),
            ]),
            strct(&[
                (0x63, PropertyValue::Enum(0x2100_0099)),
                (0x62, PropertyValue::Enum(0x1000_4444)),
            ]),
        ]);
        assert_eq!(
            projected(0x64, list).template_list,
            [
                TemplateEntry {
                    layout: 0x2100_0099,
                    element: 0x1000_3333
                },
                TemplateEntry {
                    layout: 0x2100_0099,
                    element: 0x1000_4444
                },
            ]
        );
    }

    #[test]
    fn referenced_ids_and_tooltip_properties() {
        assert_eq!(
            projected(0x72, PropertyValue::Enum(0x1000_5555)).scrollbar_element_id,
            0x1000_5555
        );
        let mut empty = ElementInfo::default();
        apply_canonical_projection(&mut empty);
        assert_eq!(empty.scrollbar_element_id, 0);
        assert!(!empty.tooltip_enabled);
        assert_eq!(empty.tooltip_text, None);
        assert_eq!(
            (
                empty.tooltip_root_element_id,
                empty.tooltip_layout_did,
                empty.tooltip_text_child_element_id
            ),
            (0, 0, 0)
        );
        assert_eq!(empty.tooltip_delay_seconds, None);
        assert_eq!(
            projected(0x47, PropertyValue::Enum(0x1000_0487)).tooltip_root_element_id,
            0x1000_0487
        );
        assert_eq!(
            projected(0x48, PropertyValue::DataId(0x2100_0041)).tooltip_layout_did,
            0x2100_0041
        );
        let text = StringInfo {
            string_id: 0x0AAA_AAAA,
            table_id: 0x2300_0003,
            ..StringInfo::default()
        };
        assert_eq!(
            projected(0x49, PropertyValue::StringInfo(text)).tooltip_text,
            Some(text)
        );
        assert_eq!(
            projected(0x4A, PropertyValue::Enum(0x1000_0396)).tooltip_text_child_element_id,
            0x1000_0396
        );
        assert!(projected(0x4B, PropertyValue::Bool(true)).tooltip_enabled);
        assert_eq!(
            projected(0x50, PropertyValue::Float(0.0)).tooltip_delay_seconds,
            Some(0.0)
        );
    }

    #[test]
    fn merge_recomputes_tooltip_properties_from_the_merged_states() {
        let mut direct = StateInfo::named(DIRECT_STATE_ID, "");
        direct
            .properties
            .set_value(0x47, PropertyValue::Enum(0x1000_0487));
        direct
            .properties
            .set_value(0x48, PropertyValue::DataId(0x2100_0041));
        direct.properties.set_value(0x4B, PropertyValue::Bool(true));
        let mut base = ElementInfo::default();
        base.set_state(DIRECT_STATE_ID, direct);
        apply_canonical_projection(&mut base);
        let m = merge(&base, &ElementInfo::default());
        assert_eq!(
            (m.tooltip_root_element_id, m.tooltip_layout_did),
            (0x1000_0487, 0x2100_0041)
        );
        assert!(m.tooltip_enabled);
    }

    #[test]
    fn effective_property_lets_the_state_override_the_default() {
        let mut info = with_direct(
            0x1B,
            PropertyValue::Color(ColorArgb {
                blue: 0,
                green: 0,
                red: 255,
                alpha: 0,
            }),
        );
        let mut normal = StateInfo::named(1, "Normal");
        normal.properties.set_value(
            0x1B,
            PropertyValue::Color(ColorArgb {
                blue: 255,
                green: 0,
                red: 0,
                alpha: 128,
            }),
        );
        info.set_state(1, normal);
        apply_canonical_projection(&mut info);
        assert_eq!(info.effective_default_state_id(), 1);
        let c = info.font_color.unwrap();
        assert_eq!((c.r, c.b, c.a), (0.0, 1.0, 128.0 / 255.0));
        let direct = color_of(
            info.effective_property(0x1B, Some(DIRECT_STATE_ID))
                .unwrap(),
        )
        .unwrap();
        assert_eq!((direct.r, direct.a), (1.0, 1.0));
    }

    #[test]
    fn property_bags_merge_derived_over_base() {
        let mut base = PropertyBag::default();
        base.set_value(1, PropertyValue::Integer(1));
        base.set_value(2, PropertyValue::Integer(2));
        let mut derived = PropertyBag::default();
        derived.set_value(2, PropertyValue::Integer(20));
        derived.set_value(3, PropertyValue::Integer(30));
        let m = PropertyBag::merge(&base, &derived);
        let values: Vec<_> = m.iter().map(|(k, p)| (*k, p.value.clone())).collect();
        assert_eq!(
            values,
            [
                (1, PropertyValue::Integer(1)),
                (2, PropertyValue::Integer(20)),
                (3, PropertyValue::Integer(30)),
            ]
        );
        // Explicit false and 0 are values, not absence.
        let mut b = PropertyBag::default();
        b.set_value(0x3B, PropertyValue::Bool(true));
        let mut d = PropertyBag::default();
        d.set_value(0x3B, PropertyValue::Bool(false));
        assert_eq!(
            PropertyBag::merge(&b, &d).get(0x3B).unwrap().value,
            PropertyValue::Bool(false)
        );
    }
}
