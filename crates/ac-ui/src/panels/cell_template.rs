//! The item-cell prototypes: an item list names the cell it builds
//! (property 0x1000000E) by an element of the shared catalog layout
//! 0x21000037, and that prototype's icon child carries the empty-slot image
//! (its `ItemSlot_Empty` state) and the ten cooldown sweep images.

use ac_dat::objects::{ElementDesc, LayoutDesc, Media, PropertyValue, StateDesc};

use crate::element_info::ElementInfo;
use crate::import::{LayoutSource, import_infos_rooted};
use crate::states;

/// The catalog layout holding the item-cell prototypes.
pub const CATALOG_LAYOUT_ID: u32 = 0x2100_0037;
/// The property naming a list's cell prototype.
pub const CELL_TEMPLATE_PROPERTY: u32 = 0x1000_000E;
/// The prototype's icon child.
pub const ICON_CHILD_ID: u32 = 0x1000_033B;
/// The shared item prototype carrying the cooldown overlays.
pub const SHARED_ITEM_PROTOTYPE_ID: u32 = 0x1000_033E;
/// The first of the ten cooldown overlay elements.
pub const FIRST_COOLDOWN_OVERLAY_ID: u32 = 0x1000_054F;
/// Cooldown steps.
pub const COOLDOWN_OVERLAY_COUNT: usize = 10;

fn find_desc(layout: &LayoutDesc, id: u32) -> Option<&ElementDesc> {
    fn walk(d: &ElementDesc, id: u32) -> Option<&ElementDesc> {
        if d.element_id == id {
            return Some(d);
        }
        d.children.iter().find_map(|(_, c)| walk(c, id))
    }
    layout.elements.iter().find_map(|(_, e)| walk(e, id))
}

fn find_info(e: &ElementInfo, id: u32) -> Option<&ElementInfo> {
    if e.id == id {
        return Some(e);
    }
    e.children.iter().find_map(|c| find_info(c, id))
}

fn read_id(value: &PropertyValue) -> u32 {
    match value {
        PropertyValue::Array(items) => items.first().map_or(0, |p| read_id(&p.value)),
        PropertyValue::DataId(v) | PropertyValue::Enum(v) => *v,
        _ => 0,
    }
}

fn id_from_state(s: &StateDesc) -> u32 {
    s.properties
        .iter()
        .find(|(k, _)| *k == CELL_TEMPLATE_PROPERTY)
        .map_or(0, |(_, p)| read_id(&p.value))
}

fn cell_template_id(e: &ElementDesc) -> u32 {
    match id_from_state(&e.state_desc) {
        0 => e
            .states
            .iter()
            .map(|(_, s)| id_from_state(s))
            .find(|id| *id != 0)
            .unwrap_or(0),
        id => id,
    }
}

fn state_image(s: &StateDesc) -> u32 {
    s.media
        .iter()
        .find_map(|m| match m.media {
            Media::Image { file, .. } if file != 0 => Some(file),
            _ => None,
        })
        .unwrap_or(0)
}

fn icon_empty_state(icon: &ElementDesc) -> u32 {
    icon.states
        .iter()
        .filter(|(id, _)| *id == states::ITEM_SLOT_EMPTY)
        .map(|(_, s)| state_image(s))
        .find(|f| *f != 0)
        .unwrap_or_else(|| state_image(&icon.state_desc))
}

fn find_icon_empty(catalog: &LayoutDesc, e: &ElementDesc, seen: &mut Vec<u32>) -> u32 {
    if e.element_id == ICON_CHILD_ID {
        let f = icon_empty_state(e);
        if f != 0 {
            return f;
        }
    }
    for (_, c) in &e.children {
        let f = find_icon_empty(catalog, c, seen);
        if f != 0 {
            return f;
        }
    }
    if e.base_element != 0
        && e.base_layout_id == CATALOG_LAYOUT_ID
        && !seen.contains(&e.base_element)
    {
        seen.push(e.base_element);
        if let Some(base) = find_desc(catalog, e.base_element) {
            return find_icon_empty(catalog, base, seen);
        }
    }
    0
}

/// The empty-slot image of catalog prototype `prototype_id` (0: none).
#[must_use]
pub fn prototype_empty_sprite(source: &dyn LayoutSource, prototype_id: u32) -> u32 {
    if prototype_id == 0 {
        return 0;
    }
    let Some(catalog) = source.layout(CATALOG_LAYOUT_ID) else {
        return 0;
    };
    let Some(proto) = find_desc(&catalog, prototype_id) else {
        return 0;
    };
    find_icon_empty(&catalog, proto, &mut Vec::new())
}

/// The empty-slot image of the cells list `list_element_id` of layout
/// `list_layout_id` builds (0: none).
#[must_use]
pub fn empty_sprite(source: &dyn LayoutSource, list_layout_id: u32, list_element_id: u32) -> u32 {
    let Some(layout) = source.layout(list_layout_id) else {
        return 0;
    };
    let Some(list) = find_desc(&layout, list_element_id) else {
        return 0;
    };
    prototype_empty_sprite(source, cell_template_id(list))
}

/// The empty-slot image of the cells list `list_element_id` builds, read
/// from an imported description tree (inherited properties resolved).
#[must_use]
pub fn empty_sprite_from_info(
    source: &dyn LayoutSource,
    root: &ElementInfo,
    list_element_id: u32,
) -> u32 {
    let Some(list) = find_info(root, list_element_id) else {
        return 0;
    };
    match list.effective_property(CELL_TEMPLATE_PROPERTY, None) {
        Some(PropertyValue::Enum(v) | PropertyValue::DataId(v)) if *v != 0 => {
            prototype_empty_sprite(source, *v)
        }
        _ => 0,
    }
}

/// The catalog's composite item cell whose number child carries the
/// shortcut digits.
pub const DIGITS_COMPOSITE_ID: u32 = 0x1000_0346;
/// The empty cell's composite.
pub const EMPTY_DIGITS_COMPOSITE_ID: u32 = 0x1000_0341;
/// The number child of both.
pub const DIGITS_CHILD_ID: u32 = 0x1000_034A;
/// The digit images over an occupied cell.
pub const REGULAR_DIGITS_PROPERTY: u32 = 0x1000_0042;
/// The digit images in magic mode.
pub const GHOSTED_DIGITS_PROPERTY: u32 = 0x1000_0043;
/// The digit images over an empty cell.
pub const EMPTY_DIGITS_PROPERTY: u32 = 0x1000_005E;

fn data_ids(state: &StateDesc, property: u32) -> Option<Vec<u32>> {
    let (_, p) = state.properties.iter().find(|(k, _)| *k == property)?;
    match &p.value {
        PropertyValue::Array(items) => Some(
            items
                .iter()
                .map(|i| match i.value {
                    PropertyValue::DataId(v) | PropertyValue::Enum(v) => v,
                    _ => 0,
                })
                .collect(),
        ),
        _ => None,
    }
}

/// The shortcut number images every item cell shares: regular and
/// ghosted digits from the catalog's composite cell, the empty cell's
/// digits from its empty composite; the retail images when the catalog
/// does not name them.
#[must_use]
pub fn shortcut_digits(source: &dyn LayoutSource) -> crate::widgets::ShortcutDigits {
    let layout = source.layout(CATALOG_LAYOUT_ID);
    let number = |composite: u32| {
        let layout = layout.as_ref()?;
        let c = find_desc(layout, composite)?;
        c.children
            .iter()
            .find(|(_, d)| d.element_id == DIGITS_CHILD_ID)
            .map(|(_, d)| d.state_desc.clone())
    };
    let full = number(DIGITS_COMPOSITE_ID);
    let regular = full
        .as_ref()
        .and_then(|s| data_ids(s, REGULAR_DIGITS_PROPERTY));
    let ghosted = full
        .as_ref()
        .and_then(|s| data_ids(s, GHOSTED_DIGITS_PROPERTY));
    let empty = number(EMPTY_DIGITS_COMPOSITE_ID).and_then(|s| data_ids(&s, EMPTY_DIGITS_PROPERTY));
    crate::widgets::ShortcutDigits {
        regular: regular.unwrap_or_else(|| (0x0600_109E..=0x0600_10A6).collect()),
        ghosted: ghosted.unwrap_or_else(|| (0x0600_1ACC..=0x0600_1AD4).collect()),
        empty,
    }
}

/// The ten cooldown sweep images of the shared item prototype (`None`
/// when any is missing).
#[must_use]
pub fn cooldown_sprites(source: &dyn LayoutSource) -> Option<Vec<u32>> {
    let proto = import_infos_rooted(source, CATALOG_LAYOUT_ID, SHARED_ITEM_PROTOTYPE_ID)?;
    (0..COOLDOWN_OVERLAY_COUNT)
        .map(|i| {
            let overlay = find_info(&proto, FIRST_COOLDOWN_OVERLAY_ID + i as u32)?;
            overlay
                .state_media
                .get("")
                .map(|m| m.file)
                .filter(|f| *f != 0)
        })
        .collect()
}
