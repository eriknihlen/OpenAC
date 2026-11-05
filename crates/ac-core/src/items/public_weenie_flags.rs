//! The public description bits every object's description carries.

use crate::wire_enum::wire_enum;

wire_enum! {
    flags
    /// Facts about an object every client may know: openable, a player, a
    /// door, attackable, and so on. Bit 0x0008_0000 is unused.
    PublicWeenieFlags {
        NONE = 0x0000_0000 => "None",
        OPENABLE = 0x0000_0001 => "Openable",
        INSCRIBABLE = 0x0000_0002 => "Inscribable",
        STUCK = 0x0000_0004 => "Stuck",
        PLAYER = 0x0000_0008 => "Player",
        ATTACKABLE = 0x0000_0010 => "Attackable",
        PLAYER_KILLER = 0x0000_0020 => "PlayerKiller",
        HIDDEN_ADMIN = 0x0000_0040 => "HiddenAdmin",
        UI_HIDDEN = 0x0000_0080 => "UiHidden",
        BOOK = 0x0000_0100 => "Book",
        VENDOR = 0x0000_0200 => "Vendor",
        PLAYER_KILLER_SWITCH = 0x0000_0400 => "PlayerKillerSwitch",
        NON_PLAYER_KILLER_SWITCH = 0x0000_0800 => "NonPlayerKillerSwitch",
        DOOR = 0x0000_1000 => "Door",
        CORPSE = 0x0000_2000 => "Corpse",
        LIFESTONE = 0x0000_4000 => "Lifestone",
        FOOD = 0x0000_8000 => "Food",
        HEALER = 0x0001_0000 => "Healer",
        LOCKPICK = 0x0002_0000 => "Lockpick",
        PORTAL = 0x0004_0000 => "Portal",
        ADMIN = 0x0010_0000 => "Admin",
        FREE_PLAYER_KILLER = 0x0020_0000 => "FreePlayerKiller",
        IMMUNE_CELL_RESTRICTIONS = 0x0040_0000 => "ImmuneCellRestrictions",
        REQUIRES_PACK_SLOT = 0x0080_0000 => "RequiresPackSlot",
        /// Vendors will not buy it.
        RETAINED = 0x0100_0000 => "Retained",
        PLAYER_KILLER_LITE = 0x0200_0000 => "PlayerKillerLite",
        /// A second header of optional fields follows in the description.
        INCLUDES_SECOND_HEADER = 0x0400_0000 => "IncludesSecondHeader",
        BINDSTONE = 0x0800_0000 => "Bindstone",
        VOLATILE_RARE = 0x1000_0000 => "VolatileRare",
        WIELD_ON_USE = 0x2000_0000 => "WieldOnUse",
        WIELD_LEFT = 0x4000_0000 => "WieldLeft",
    }
}

#[cfg(test)]
mod tests {
    //! Golden values of the game's public-description bitfield (the same
    //! list as OpenAC's `PublicWeenieFlags`).

    use super::*;

    #[test]
    fn values_match_the_header() {
        let golden: &[(&str, u32)] = &[
            ("Openable", 0x1),
            ("Inscribable", 0x2),
            ("Stuck", 0x4),
            ("Player", 0x8),
            ("Attackable", 0x10),
            ("PlayerKiller", 0x20),
            ("HiddenAdmin", 0x40),
            ("UiHidden", 0x80),
            ("Book", 0x100),
            ("Vendor", 0x200),
            ("PlayerKillerSwitch", 0x400),
            ("NonPlayerKillerSwitch", 0x800),
            ("Door", 0x1000),
            ("Corpse", 0x2000),
            ("Lifestone", 0x4000),
            ("Food", 0x8000),
            ("Healer", 0x10000),
            ("Lockpick", 0x20000),
            ("Portal", 0x40000),
            ("Admin", 0x100000),
            ("FreePlayerKiller", 0x200000),
            ("ImmuneCellRestrictions", 0x400000),
            ("RequiresPackSlot", 0x800000),
            ("Retained", 0x1000000),
            ("PlayerKillerLite", 0x2000000),
            ("IncludesSecondHeader", 0x4000000),
            ("Bindstone", 0x8000000),
            ("VolatileRare", 0x10000000),
            ("WieldOnUse", 0x20000000),
            ("WieldLeft", 0x40000000),
        ];
        for &(name, value) in golden {
            assert_eq!(
                PublicWeenieFlags::from_name(name),
                Some(PublicWeenieFlags(value)),
                "{name}"
            );
        }
        assert_eq!(PublicWeenieFlags::NAMED.len(), golden.len() + 1);
    }
}
