//! The public description block of the create-object and update-object
//! messages: name, class, icon, item type, and the optional item fields
//! selected by two flag words.

use ac_bits::Reader;

use crate::{ProtoError, Result};

/// Dat id type of an icon.
pub const ICON_ID_TYPE: u32 = 0x0600_0000;

/// Object description bit: a second header flag word follows the header.
pub const OBJECT_DESC_INCLUDES_SECOND_HEADER: u32 = 0x0400_0000;

/// Header flag: plural name.
pub const WD_PLURAL_NAME: u32 = 0x0000_0001;
/// Header flag: items capacity.
pub const WD_ITEMS_CAPACITY: u32 = 0x0000_0002;
/// Header flag: containers capacity.
pub const WD_CONTAINERS_CAPACITY: u32 = 0x0000_0004;
/// Header flag: value.
pub const WD_VALUE: u32 = 0x0000_0008;
/// Header flag: useability.
pub const WD_USEABLE: u32 = 0x0000_0010;
/// Header flag: use radius.
pub const WD_USE_RADIUS: u32 = 0x0000_0020;
/// Header flag: monarch.
pub const WD_MONARCH: u32 = 0x0000_0040;
/// Header flag: UI effects.
pub const WD_UI_EFFECTS: u32 = 0x0000_0080;
/// Header flag: ammunition type.
pub const WD_AMMO_TYPE: u32 = 0x0000_0100;
/// Header flag: combat use.
pub const WD_COMBAT_USE: u32 = 0x0000_0200;
/// Header flag: structure (uses left).
pub const WD_STRUCTURE: u32 = 0x0000_0400;
/// Header flag: maximum structure.
pub const WD_MAX_STRUCTURE: u32 = 0x0000_0800;
/// Header flag: stack size.
pub const WD_STACK_SIZE: u32 = 0x0000_1000;
/// Header flag: maximum stack size.
pub const WD_MAX_STACK_SIZE: u32 = 0x0000_2000;
/// Header flag: container.
pub const WD_CONTAINER: u32 = 0x0000_4000;
/// Header flag: wielder.
pub const WD_WIELDER: u32 = 0x0000_8000;
/// Header flag: valid wield locations.
pub const WD_VALID_LOCATIONS: u32 = 0x0001_0000;
/// Header flag: current wield location.
pub const WD_CURRENTLY_WIELDED_LOCATION: u32 = 0x0002_0000;
/// Header flag: priority.
pub const WD_PRIORITY: u32 = 0x0004_0000;
/// Header flag: target type.
pub const WD_TARGET_TYPE: u32 = 0x0008_0000;
/// Header flag: radar blip color.
pub const WD_RADAR_BLIP_COLOR: u32 = 0x0010_0000;
/// Header flag: burden.
pub const WD_BURDEN: u32 = 0x0020_0000;
/// Header flag: spell.
pub const WD_SPELL: u32 = 0x0040_0000;
/// Header flag: radar behavior.
pub const WD_RADAR_BEHAVIOR: u32 = 0x0080_0000;
/// Header flag: workmanship.
pub const WD_WORKMANSHIP: u32 = 0x0100_0000;
/// Header flag: house owner.
pub const WD_HOUSE_OWNER: u32 = 0x0200_0000;
/// Header flag: house restrictions.
pub const WD_HOUSE_RESTRICTIONS: u32 = 0x0400_0000;
/// Header flag: physics script.
pub const WD_PSCRIPT: u32 = 0x0800_0000;
/// Header flag: hook type.
pub const WD_HOOK_TYPE: u32 = 0x1000_0000;
/// Header flag: hook item types.
pub const WD_HOOK_ITEM_TYPES: u32 = 0x2000_0000;
/// Header flag: icon overlay.
pub const WD_ICON_OVERLAY: u32 = 0x4000_0000;
/// Header flag: material type.
pub const WD_MATERIAL_TYPE: u32 = 0x8000_0000;

/// Second header flag: icon underlay.
pub const WD2_ICON_UNDERLAY: u32 = 0x0000_0001;
/// Second header flag: cooldown id.
pub const WD2_COOLDOWN: u32 = 0x0000_0002;
/// Second header flag: cooldown duration.
pub const WD2_COOLDOWN_DURATION: u32 = 0x0000_0004;
/// Second header flag: pet owner.
pub const WD2_PET_OWNER: u32 = 0x0000_0008;

/// One guest on a house's access list.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct HouseGuest {
    /// The guest.
    pub guest_id: u32,
    /// Permission value (storage access).
    pub permission: u32,
}

/// A house's access list.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct HouseRestrictions {
    /// Record version; 0 for the oldest shape, which carries none.
    pub version: u32,
    /// Access flags; non-zero means open to the public. The oldest shape
    /// carries only the open bit.
    pub flags: u32,
    /// Allegiance monarch with access; 0 in the oldest shape.
    pub monarch_id: u32,
    /// Guests, in wire order.
    pub guests: Vec<HouseGuest>,
}

impl HouseRestrictions {
    /// True when the house is open to the public.
    #[must_use]
    pub fn open_to_public(&self) -> bool {
        self.flags != 0
    }

    /// Read one of the three shapes, chosen by the first word: with its high
    /// half zero it is the open-to-public switch of the oldest shape (no
    /// monarch, the old guest table); otherwise it is the version, the flags
    /// and monarch follow, then the old guest table below
    /// [`HouseRestrictions::CURRENT_VERSION`] and the current one from it on.
    pub(crate) fn read(r: &mut Reader<'_>) -> Result<Self> {
        let first = r.u32()?;
        if first & 0xFFFF_0000 == 0 {
            return Ok(Self {
                version: 0,
                flags: u32::from(first != 0),
                monarch_id: 0,
                guests: read_old_guests(r)?,
            });
        }
        let flags = r.u32()?;
        let monarch_id = r.u32()?;
        let guests = if first >= Self::CURRENT_VERSION {
            read_guests(r)?
        } else {
            read_old_guests(r)?
        };
        Ok(Self {
            version: first,
            flags,
            monarch_id,
            guests,
        })
    }

    /// The version of the current shape, the one servers send today.
    pub const CURRENT_VERSION: u32 = 0x1000_0002;
}

/// The current guest table: a header with the bucket size index in its top
/// byte and the entry count in the low 24 bits, then (guest, permission)
/// pairs.
fn read_guests(r: &mut Reader<'_>) -> Result<Vec<HouseGuest>> {
    let count = r.u32()? & 0x00FF_FFFF;
    read_guest_pairs(r, count)
}

/// The old guest table: a header with the number of bucket bits in its top
/// byte (at most 32) and the entry count in the low 24 bits, then (guest,
/// permission) pairs. With no bucket bits only an empty table is valid, and
/// a guest may appear only once.
fn read_old_guests(r: &mut Reader<'_>) -> Result<Vec<HouseGuest>> {
    let header = r.u32()?;
    let bucket_bits = header >> 24;
    if bucket_bits > 32 {
        return Err(ProtoError::InvalidValue {
            what: "old house guest table bucket bits",
            value: bucket_bits,
        });
    }
    let count = header & 0x00FF_FFFF;
    if bucket_bits == 0 {
        return if count == 0 {
            Ok(Vec::new())
        } else {
            Err(ProtoError::Inconsistent {
                what: "old house guest table without buckets",
            })
        };
    }
    let guests = read_guest_pairs(r, count)?;
    for (i, guest) in guests.iter().enumerate() {
        if guests[..i].iter().any(|g| g.guest_id == guest.guest_id) {
            return Err(ProtoError::Inconsistent {
                what: "old house guest table repeats a guest",
            });
        }
    }
    Ok(guests)
}

fn read_guest_pairs(r: &mut Reader<'_>, count: u32) -> Result<Vec<HouseGuest>> {
    if count as usize * 8 > r.remaining() {
        return Err(ProtoError::CountExceedsPayload {
            what: "house guests",
            count,
        });
    }
    (0..count)
        .map(|_| {
            Ok(HouseGuest {
                guest_id: r.u32()?,
                permission: r.u32()?,
            })
        })
        .collect()
}

/// The public description. Every optional field is present exactly when
/// its flag is set.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct PublicWeenieDesc {
    /// Header flags (`WD_*`).
    pub header_flags: u32,
    /// Name.
    pub name: String,
    /// Weenie class id.
    pub weenie_class_id: u32,
    /// Icon id (zero for none).
    pub icon_id: u32,
    /// Item type bits.
    pub item_type: u32,
    /// Object description bits (player, player killer, ...).
    pub object_description_flags: u32,
    /// Second header flags (`WD2_*`), when the description bits say so.
    pub header_flags2: Option<u32>,
    /// Plural name.
    pub plural_name: Option<String>,
    /// Items capacity (a signed byte; -1 occurs).
    pub items_capacity: Option<i8>,
    /// Containers capacity (a signed byte).
    pub containers_capacity: Option<i8>,
    /// Ammunition type; zero is an explicit "none".
    pub ammo_type: Option<u16>,
    /// Value.
    pub value: Option<i32>,
    /// Useability bits.
    pub useability: Option<u32>,
    /// Use radius, meters.
    pub use_radius: Option<f32>,
    /// Target type bits.
    pub target_type: Option<u32>,
    /// UI effects bits.
    pub ui_effects: Option<u32>,
    /// Combat use.
    pub combat_use: Option<u8>,
    /// Structure (uses left).
    pub structure: Option<u16>,
    /// Maximum structure.
    pub max_structure: Option<u16>,
    /// Stack size.
    pub stack_size: Option<u16>,
    /// Maximum stack size.
    pub max_stack_size: Option<u16>,
    /// Container.
    pub container_id: Option<u32>,
    /// Wielder.
    pub wielder_id: Option<u32>,
    /// Valid wield locations.
    pub valid_locations: Option<u32>,
    /// Current wield location.
    pub currently_wielded_location: Option<u32>,
    /// Priority.
    pub priority: Option<u32>,
    /// Radar blip color.
    pub radar_blip_color: Option<u8>,
    /// Radar behavior.
    pub radar_behavior: Option<u8>,
    /// Physics script.
    pub physics_script: Option<u16>,
    /// Workmanship.
    pub workmanship: Option<f32>,
    /// Burden.
    pub burden: Option<u16>,
    /// Spell.
    pub spell_id: Option<u16>,
    /// House owner.
    pub house_owner_id: Option<u32>,
    /// House access list.
    pub house_restrictions: Option<HouseRestrictions>,
    /// Item types a hook accepts.
    pub hook_item_types: Option<u32>,
    /// Monarch.
    pub monarch_id: Option<u32>,
    /// Hook type.
    pub hook_type: Option<u16>,
    /// Icon overlay id.
    pub icon_overlay_id: Option<u32>,
    /// Icon underlay id.
    pub icon_underlay_id: Option<u32>,
    /// Material type.
    pub material_type: Option<u32>,
    /// Cooldown group id.
    pub cooldown_id: Option<u32>,
    /// Cooldown duration, seconds.
    pub cooldown_duration: Option<f64>,
    /// Pet owner.
    pub pet_owner_id: Option<u32>,
}

impl PublicWeenieDesc {
    /// Read the block: header flags, name, class id, icon, item type,
    /// description bits, padding to 4 (measured from the reader's start,
    /// which must be the message start), the second flag word when the
    /// description bits announce it, then the optional fields in wire order.
    pub fn read(r: &mut Reader<'_>) -> Result<Self> {
        let header_flags = r.u32()?;
        let name = r.string16l()?;
        let weenie_class_id = r.packed_dword()?;
        let icon_id = r.packed_dword_of_known_type(ICON_ID_TYPE)?;
        let item_type = r.u32()?;
        let object_description_flags = r.u32()?;
        r.align(4)?;
        let header_flags2 = if object_description_flags & OBJECT_DESC_INCLUDES_SECOND_HEADER != 0 {
            Some(r.u32()?)
        } else {
            None
        };

        let has = |bit: u32| header_flags & bit != 0;
        let has2 = |bit: u32| header_flags2.unwrap_or(0) & bit != 0;
        macro_rules! opt {
            ($cond:expr, $read:expr) => {
                if $cond { Some($read) } else { None }
            };
        }

        let plural_name = opt!(has(WD_PLURAL_NAME), r.string16l()?);
        let items_capacity = opt!(has(WD_ITEMS_CAPACITY), r.i8()?);
        let containers_capacity = opt!(has(WD_CONTAINERS_CAPACITY), r.i8()?);
        let ammo_type = opt!(has(WD_AMMO_TYPE), r.u16()?);
        let value = opt!(has(WD_VALUE), r.i32()?);
        let useability = opt!(has(WD_USEABLE), r.u32()?);
        let use_radius = opt!(has(WD_USE_RADIUS), r.f32()?);
        let target_type = opt!(has(WD_TARGET_TYPE), r.u32()?);
        let ui_effects = opt!(has(WD_UI_EFFECTS), r.u32()?);
        let combat_use = opt!(has(WD_COMBAT_USE), r.u8()?);
        let structure = opt!(has(WD_STRUCTURE), r.u16()?);
        let max_structure = opt!(has(WD_MAX_STRUCTURE), r.u16()?);
        let stack_size = opt!(has(WD_STACK_SIZE), r.u16()?);
        let max_stack_size = opt!(has(WD_MAX_STACK_SIZE), r.u16()?);
        let container_id = opt!(has(WD_CONTAINER), r.u32()?);
        let wielder_id = opt!(has(WD_WIELDER), r.u32()?);
        let valid_locations = opt!(has(WD_VALID_LOCATIONS), r.u32()?);
        let currently_wielded_location = opt!(has(WD_CURRENTLY_WIELDED_LOCATION), r.u32()?);
        let priority = opt!(has(WD_PRIORITY), r.u32()?);
        let radar_blip_color = opt!(has(WD_RADAR_BLIP_COLOR), r.u8()?);
        let radar_behavior = opt!(has(WD_RADAR_BEHAVIOR), r.u8()?);
        let physics_script = opt!(has(WD_PSCRIPT), r.u16()?);
        let workmanship = opt!(has(WD_WORKMANSHIP), r.f32()?);
        let burden = opt!(has(WD_BURDEN), r.u16()?);
        let spell_id = opt!(has(WD_SPELL), r.u16()?);
        let house_owner_id = opt!(has(WD_HOUSE_OWNER), r.u32()?);
        let house_restrictions = opt!(has(WD_HOUSE_RESTRICTIONS), HouseRestrictions::read(r)?);
        let hook_item_types = opt!(has(WD_HOOK_ITEM_TYPES), r.u32()?);
        let monarch_id = opt!(has(WD_MONARCH), r.u32()?);
        let hook_type = opt!(has(WD_HOOK_TYPE), r.u16()?);
        let icon_overlay_id = opt!(
            has(WD_ICON_OVERLAY),
            r.packed_dword_of_known_type(ICON_ID_TYPE)?
        );
        let icon_underlay_id = opt!(
            has2(WD2_ICON_UNDERLAY),
            r.packed_dword_of_known_type(ICON_ID_TYPE)?
        );
        let material_type = opt!(has(WD_MATERIAL_TYPE), r.u32()?);
        let cooldown_id = opt!(has2(WD2_COOLDOWN), r.u32()?);
        let cooldown_duration = opt!(has2(WD2_COOLDOWN_DURATION), r.f64()?);
        let pet_owner_id = opt!(has2(WD2_PET_OWNER), r.u32()?);

        Ok(Self {
            header_flags,
            name,
            weenie_class_id,
            icon_id,
            item_type,
            object_description_flags,
            header_flags2,
            plural_name,
            items_capacity,
            containers_capacity,
            ammo_type,
            value,
            useability,
            use_radius,
            target_type,
            ui_effects,
            combat_use,
            structure,
            max_structure,
            stack_size,
            max_stack_size,
            container_id,
            wielder_id,
            valid_locations,
            currently_wielded_location,
            priority,
            radar_blip_color,
            radar_behavior,
            physics_script,
            workmanship,
            burden,
            spell_id,
            house_owner_id,
            house_restrictions,
            hook_item_types,
            monarch_id,
            hook_type,
            icon_overlay_id,
            icon_underlay_id,
            material_type,
            cooldown_id,
            cooldown_duration,
            pet_owner_id,
        })
    }
}

#[cfg(test)]
mod tests {
    //! The three access-list shapes the game client accepts, worked from
    //! its reader: a first word with a zero high half is the open switch of
    //! the oldest shape; below version `0x1000_0002` the flags and monarch
    //! follow, then the old table; from that version on the current table.

    use ac_bits::Writer;

    use super::*;
    use crate::test_util::assert_every_prefix_fails;

    fn read(bytes: &[u8]) -> Result<HouseRestrictions> {
        HouseRestrictions::read(&mut Reader::new(bytes))
    }

    fn guests(pairs: &[(u32, u32)]) -> Vec<HouseGuest> {
        pairs
            .iter()
            .map(|&(guest_id, permission)| HouseGuest {
                guest_id,
                permission,
            })
            .collect()
    }

    #[test]
    fn current_shape_reads_flags_monarch_and_table() {
        let mut w = Writer::new();
        w.u32(0x1000_0002);
        w.u32(1);
        w.u32(0x5000_0009);
        w.u32(0x0300_0002); // bucket size index 3, two entries
        w.u32(0x5000_0001);
        w.u32(0);
        w.u32(0x5000_0002);
        w.u32(1);
        let h = read(w.as_slice()).unwrap();
        assert_eq!(
            h,
            HouseRestrictions {
                version: 0x1000_0002,
                flags: 1,
                monarch_id: 0x5000_0009,
                guests: guests(&[(0x5000_0001, 0), (0x5000_0002, 1)]),
            }
        );
        assert_every_prefix_fails(w.as_slice(), read);
    }

    #[test]
    fn oldest_shape_is_an_open_switch_then_the_old_table() {
        let mut w = Writer::new();
        w.u32(1); // open to the public
        w.u32(0x0300_0001); // four buckets, one entry
        w.u32(0x5000_0001);
        w.u32(1);
        let h = read(w.as_slice()).unwrap();
        assert_eq!(
            h,
            HouseRestrictions {
                version: 0,
                flags: 1,
                monarch_id: 0,
                guests: guests(&[(0x5000_0001, 1)]),
            }
        );
        assert!(h.open_to_public());
        assert_every_prefix_fails(w.as_slice(), read);

        // Any non-zero switch below the high half sets only the open bit.
        let mut w = Writer::new();
        w.u32(0xFFFF);
        w.u32(0);
        let h = read(w.as_slice()).unwrap();
        assert_eq!((h.version, h.flags, h.guests.len()), (0, 1, 0));
        let mut w = Writer::new();
        w.u32(0);
        w.u32(0);
        assert_eq!(read(w.as_slice()).unwrap().flags, 0);
    }

    #[test]
    fn older_version_reads_flags_and_monarch_then_the_old_table() {
        let mut w = Writer::new();
        w.u32(0x1000_0001);
        w.u32(4);
        w.u32(0x5000_0009);
        w.u32(0x0100_0002); // one bucket, two entries
        w.u32(0x5000_0001);
        w.u32(0);
        w.u32(0x5000_0002);
        w.u32(1);
        let h = read(w.as_slice()).unwrap();
        assert_eq!(
            h,
            HouseRestrictions {
                version: 0x1000_0001,
                flags: 4,
                monarch_id: 0x5000_0009,
                guests: guests(&[(0x5000_0001, 0), (0x5000_0002, 1)]),
            }
        );
        assert_every_prefix_fails(w.as_slice(), read);
    }

    #[test]
    fn old_table_rejects_what_the_game_client_rejects() {
        let old = |table: &[u32]| {
            let mut w = Writer::new();
            w.u32(0x0001_0000);
            w.u32(0);
            w.u32(0);
            for &word in table {
                w.u32(word);
            }
            read(w.as_slice())
        };
        // More than 32 bucket bits.
        assert!(old(&[0x2100_0000]).is_err());
        // No buckets: only an empty table is accepted.
        assert_eq!(old(&[0x0000_0000]).unwrap().guests, []);
        assert!(old(&[0x0000_0001, 0x5000_0001, 0]).is_err());
        // A guest listed twice.
        assert!(old(&[0x0100_0002, 0x5000_0001, 0, 0x5000_0001, 1]).is_err());
        // The current table keeps a repeated guest.
        let mut w = Writer::new();
        w.u32(0x1000_0002);
        w.u32(0);
        w.u32(0);
        for word in [0x0100_0002, 0x5000_0001, 0, 0x5000_0001, 1] {
            w.u32(word);
        }
        assert_eq!(read(w.as_slice()).unwrap().guests.len(), 2);
    }
}
