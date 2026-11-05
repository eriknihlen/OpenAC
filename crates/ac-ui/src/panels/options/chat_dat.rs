//! What the Chat page reads from the dats for its two opacity sliders:
//! their defaults (a property collection the interface enumeration names
//! 0x16) and their captions and tooltips (the option catalog it names
//! 0x15).

use ac_dat::DatCollection;
use ac_dat::objects::{DbProperties, EnumIdMap, MasterProperty, PropertyValue};

use crate::strings::StringResolver;

/// The interface enumeration in the master map.
const ENUM_CATEGORY: u32 = 2;
/// The defaults collection's enumeration value.
const DEFAULTS_ENUM_VALUE: u32 = 0x16;
/// The captions catalog's enumeration value.
const CAPTIONS_ENUM_VALUE: u32 = 0x15;

/// The unfocused-window opacity slider's property.
pub const DEFAULT_OPACITY_PROPERTY_ID: u32 = 0x1000_0080;
/// The focused-window opacity slider's property.
pub const ACTIVE_OPACITY_PROPERTY_ID: u32 = 0x1000_0081;
/// The game's own fallback for the unfocused opacity.
pub const FALLBACK_DEFAULT_OPACITY: f32 = 0.5;
/// The fallback for the focused opacity.
pub const FALLBACK_ACTIVE_OPACITY: f32 = 1.0;

const CATALOG_ARRAY_PROPERTY_ID: u32 = 0xD2;
const ENTRY_NAME_PROPERTY_ID: u32 = 0xD4;
const ENTRY_TOOLTIP_PROPERTY_ID: u32 = 0xD5;
const ENTRY_OWNING_PROPERTY_ID: u32 = 0xD6;

/// A slider's caption and tooltip (either may be missing).
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct Caption {
    /// The caption.
    pub name: Option<String>,
    /// The tooltip.
    pub tooltip: Option<String>,
}

/// The dat id the interface enumeration maps `value` to (0: none).
#[must_use]
pub fn resolve_data_id(dats: &DatCollection, value: u32, category: u32) -> u32 {
    let master_id = dats.portal.header().master_map_id.cast_unsigned();
    if master_id == 0 {
        return 0;
    }
    let map = |id| dats.get::<EnumIdMap>(id).ok().flatten();
    map(master_id)
        .and_then(|m| m.client_id(category))
        .and_then(map)
        .and_then(|m| m.client_id(value))
        .unwrap_or(0)
}

fn collection(dats: &DatCollection, master: &MasterProperty, value: u32) -> Option<DbProperties> {
    let id = resolve_data_id(dats, value, ENUM_CATEGORY);
    if id == 0 {
        return None;
    }
    DbProperties::read(&dats.portal, master, id).ok().flatten()
}

/// The two opacity defaults (unfocused, focused); `None` when the dats do
/// not have both (the caller then uses the fallbacks).
#[must_use]
pub fn opacity_defaults(dats: &DatCollection, master: &MasterProperty) -> Option<(f32, f32)> {
    let props = collection(dats, master, DEFAULTS_ENUM_VALUE)?;
    let float = |id| match props.get(id).map(|p| &p.value) {
        Some(PropertyValue::Float(f)) => Some(*f),
        _ => None,
    };
    Some((
        float(DEFAULT_OPACITY_PROPERTY_ID)?,
        float(ACTIVE_OPACITY_PROPERTY_ID)?,
    ))
}

/// The two opacity sliders' captions (unfocused, focused).
#[must_use]
pub fn opacity_captions(
    dats: &DatCollection,
    master: &MasterProperty,
    strings: &StringResolver,
) -> Option<(Caption, Caption)> {
    let props = collection(dats, master, CAPTIONS_ENUM_VALUE)?;
    let PropertyValue::Array(entries) = &props.get(CATALOG_ARRAY_PROPERTY_ID)?.value else {
        return None;
    };
    let (mut default, mut active) = (Caption::default(), Caption::default());
    for entry in entries {
        let PropertyValue::Struct(members) = &entry.value else {
            continue;
        };
        let member = |id| {
            members
                .iter()
                .find(|(k, _)| *k == id)
                .map(|(_, p)| &p.value)
        };
        let Some(PropertyValue::Enum(owner)) = member(ENTRY_OWNING_PROPERTY_ID) else {
            continue;
        };
        let text = |id| match member(id) {
            Some(PropertyValue::StringInfo(info)) => strings.resolve_info(info),
            _ => None,
        };
        let caption = Caption {
            name: text(ENTRY_NAME_PROPERTY_ID),
            tooltip: text(ENTRY_TOOLTIP_PROPERTY_ID),
        };
        if *owner == DEFAULT_OPACITY_PROPERTY_ID {
            default = caption;
        } else if *owner == ACTIVE_OPACITY_PROPERTY_ID {
            active = caption;
        }
    }
    Some((default, active))
}
