//! What kind of thing an object is, and where it can be worn or wielded.

use crate::wire_enum::wire_enum;

wire_enum! {
    flags
    /// The kind of an object, as bits: one object may carry several. Bit
    /// 0x0200_0000 is unused. The composite masks are the game's own
    /// groupings, not derived unions (`ITEM` in particular is far broader
    /// than any obvious union).
    ItemType {
        NONE = 0x0000_0000 => "None",
        MELEE_WEAPON = 0x0000_0001 => "MeleeWeapon",
        ARMOR = 0x0000_0002 => "Armor",
        CLOTHING = 0x0000_0004 => "Clothing",
        JEWELRY = 0x0000_0008 => "Jewelry",
        CREATURE = 0x0000_0010 => "Creature",
        FOOD = 0x0000_0020 => "Food",
        MONEY = 0x0000_0040 => "Money",
        MISC = 0x0000_0080 => "Misc",
        MISSILE_WEAPON = 0x0000_0100 => "MissileWeapon",
        CONTAINER = 0x0000_0200 => "Container",
        USELESS = 0x0000_0400 => "Useless",
        GEM = 0x0000_0800 => "Gem",
        SPELL_COMPONENTS = 0x0000_1000 => "SpellComponents",
        WRITABLE = 0x0000_2000 => "Writable",
        KEY = 0x0000_4000 => "Key",
        CASTER = 0x0000_8000 => "Caster",
        PORTAL = 0x0001_0000 => "Portal",
        LOCKABLE = 0x0002_0000 => "Lockable",
        PROMISSORY_NOTE = 0x0004_0000 => "PromissoryNote",
        MANA_STONE = 0x0008_0000 => "ManaStone",
        SERVICE = 0x0010_0000 => "Service",
        MAGIC_WIELDABLE = 0x0020_0000 => "MagicWieldable",
        CRAFT_COOKING_BASE = 0x0040_0000 => "CraftCookingBase",
        CRAFT_ALCHEMY_BASE = 0x0080_0000 => "CraftAlchemyBase",
        CRAFT_FLETCHING_BASE = 0x0100_0000 => "CraftFletchingBase",
        CRAFT_ALCHEMY_INTERMEDIATE = 0x0400_0000 => "CraftAlchemyIntermediate",
        CRAFT_FLETCHING_INTERMEDIATE = 0x0800_0000 => "CraftFletchingIntermediate",
        LIFE_STONE = 0x1000_0000 => "LifeStone",
        TINKERING_TOOL = 0x2000_0000 => "TinkeringTool",
        TINKERING_MATERIAL = 0x4000_0000 => "TinkeringMaterial",
        GAMEBOARD = 0x8000_0000 => "Gameboard",

        /// Armor and clothing.
        VESTEMENTS = 0x0000_0006 => "Vestements",
        /// Melee and missile weapons; casters are not weapons.
        WEAPON = 0x0000_0101 => "Weapon",
        WEAPON_OR_CASTER = 0x0000_8101 => "WeaponOrCaster",
        LOCKABLE_MAGIC_TARGET = 0x0000_0280 => "LockableMagicTarget",
        REDIRECTABLE_ITEM_ENCHANTMENT_TARGET = 0x0000_8107 => "RedirectableItemEnchantmentTarget",
        PORTAL_MAGIC_TARGET = 0x1001_0000 => "PortalMagicTarget",
        ITEM_ENCHANTABLE_TARGET = 0x0008_8B8F => "ItemEnchantableTarget",
        ITEM = 0x002D_FBEF => "Item",
        VENDOR_SHOPKEEP = 0x4804_67A7 => "VendorShopkeep",
        VENDOR_GROCER = 0x0044_6220 => "VendorGrocer",
    }
}

wire_enum! {
    flags
    /// Equipment slots, as bits. Valid locations, the wielded location and
    /// wield requests all carry these bits. The composites are the game's
    /// own slot groups; bit 31 is never used.
    EquipMask {
        NONE = 0x0000_0000 => "None",
        HEAD_WEAR = 0x0000_0001 => "HeadWear",
        CHEST_WEAR = 0x0000_0002 => "ChestWear",
        ABDOMEN_WEAR = 0x0000_0004 => "AbdomenWear",
        UPPER_ARM_WEAR = 0x0000_0008 => "UpperArmWear",
        LOWER_ARM_WEAR = 0x0000_0010 => "LowerArmWear",
        HAND_WEAR = 0x0000_0020 => "HandWear",
        UPPER_LEG_WEAR = 0x0000_0040 => "UpperLegWear",
        LOWER_LEG_WEAR = 0x0000_0080 => "LowerLegWear",
        FOOT_WEAR = 0x0000_0100 => "FootWear",
        CHEST_ARMOR = 0x0000_0200 => "ChestArmor",
        ABDOMEN_ARMOR = 0x0000_0400 => "AbdomenArmor",
        UPPER_ARM_ARMOR = 0x0000_0800 => "UpperArmArmor",
        LOWER_ARM_ARMOR = 0x0000_1000 => "LowerArmArmor",
        UPPER_LEG_ARMOR = 0x0000_2000 => "UpperLegArmor",
        LOWER_LEG_ARMOR = 0x0000_4000 => "LowerLegArmor",
        NECK_WEAR = 0x0000_8000 => "NeckWear",
        WRIST_WEAR_LEFT = 0x0001_0000 => "WristWearLeft",
        WRIST_WEAR_RIGHT = 0x0002_0000 => "WristWearRight",
        FINGER_WEAR_LEFT = 0x0004_0000 => "FingerWearLeft",
        FINGER_WEAR_RIGHT = 0x0008_0000 => "FingerWearRight",
        MELEE_WEAPON = 0x0010_0000 => "MeleeWeapon",
        SHIELD = 0x0020_0000 => "Shield",
        MISSILE_WEAPON = 0x0040_0000 => "MissileWeapon",
        MISSILE_AMMO = 0x0080_0000 => "MissileAmmo",
        HELD = 0x0100_0000 => "Held",
        TWO_HANDED = 0x0200_0000 => "TwoHanded",
        TRINKET_ONE = 0x0400_0000 => "TrinketOne",
        CLOAK = 0x0800_0000 => "Cloak",
        SIGIL_ONE = 0x1000_0000 => "SigilOne",
        SIGIL_TWO = 0x2000_0000 => "SigilTwo",
        SIGIL_THREE = 0x4000_0000 => "SigilThree",

        /// The nine wear slots plus the cloak.
        CLOTHING = 0x0800_01FF => "Clothing",
        ARMOR = 0x0000_7E00 => "Armor",
        JEWELRY = 0x7C0F_8000 => "Jewelry",
        WRIST_WEAR = 0x0003_0000 => "WristWear",
        FINGER_WEAR = 0x000C_0000 => "FingerWear",
        SIGIL = 0x7000_0000 => "Sigil",
        READY_SLOT = 0x03F0_0000 => "ReadySlot",
        WEAPON = 0x0250_0000 => "Weapon",
        WEAPON_READY_SLOT = 0x0350_0000 => "WeaponReadySlot",
        ALL = 0x7FFF_FFFF => "All",
        /// Same value as `ALL`.
        CAN_GO_IN_READY_SLOT = 0x7FFF_FFFF => "CanGoInReadySlot",
    }
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC
    //! `tests/AcDream.Core.Tests/Properties/RetailEnumConformanceTests.cs`
    //! (ItemType, EquipMask composites) and
    //! `tests/AcDream.Core.Tests/Items/EquipMaskTests.cs`.

    use super::*;

    const RETAIL_ITEM_TYPE: &[(&str, u32)] = &[
        ("None", 0x0),
        ("MeleeWeapon", 0x1),
        ("Armor", 0x2),
        ("Vestements", 0x6),
        ("Clothing", 0x4),
        ("Jewelry", 0x8),
        ("Creature", 0x10),
        ("Food", 0x20),
        ("Money", 0x40),
        ("Misc", 0x80),
        ("MissileWeapon", 0x100),
        ("Weapon", 0x101),
        ("Container", 0x200),
        ("LockableMagicTarget", 0x280),
        ("Useless", 0x400),
        ("Gem", 0x800),
        ("SpellComponents", 0x1000),
        ("Writable", 0x2000),
        ("Key", 0x4000),
        ("Caster", 0x8000),
        ("WeaponOrCaster", 0x8101),
        ("RedirectableItemEnchantmentTarget", 0x8107),
        ("Portal", 0x10000),
        ("Lockable", 0x20000),
        ("PromissoryNote", 0x40000),
        ("ManaStone", 0x80000),
        ("ItemEnchantableTarget", 0x88B8F),
        ("Service", 0x100000),
        ("MagicWieldable", 0x200000),
        ("Item", 0x2DFBEF),
        ("CraftCookingBase", 0x400000),
        ("VendorGrocer", 0x446220),
        ("CraftAlchemyBase", 0x800000),
        ("CraftFletchingBase", 0x1000000),
        ("CraftAlchemyIntermediate", 0x4000000),
        ("CraftFletchingIntermediate", 0x8000000),
        ("LifeStone", 0x10000000),
        ("PortalMagicTarget", 0x10010000),
        ("TinkeringTool", 0x20000000),
        ("TinkeringMaterial", 0x40000000),
        ("VendorShopkeep", 0x480467A7),
        ("Gameboard", 0x80000000),
    ];

    fn golden_names(rows: &[(&'static str, u32)]) -> Vec<&'static str> {
        let mut names: Vec<_> = rows.iter().map(|&(name, _)| name).collect();
        names.sort_unstable();
        names
    }

    fn sorted_names<T>(named: &[(T, &'static str)]) -> Vec<&'static str> {
        let mut names: Vec<_> = named.iter().map(|&(_, name)| name).collect();
        names.sort_unstable();
        names
    }

    #[test]
    fn item_type_matches_retail() {
        for &(name, value) in RETAIL_ITEM_TYPE {
            assert_eq!(ItemType::from_name(name), Some(ItemType(value)), "{name}");
        }
    }

    #[test]
    fn item_type_declares_nothing_retail_does_not() {
        assert_eq!(
            sorted_names(ItemType::NAMED),
            golden_names(RETAIL_ITEM_TYPE)
        );
    }

    #[test]
    fn craft_ladder_matches_retail_and_leaves_the_unused_bit_unclaimed() {
        assert_eq!(ItemType::CRAFT_COOKING_BASE.bits(), 0x0040_0000);
        assert_eq!(ItemType::CRAFT_ALCHEMY_BASE.bits(), 0x0080_0000);
        assert_eq!(ItemType::CRAFT_FLETCHING_BASE.bits(), 0x0100_0000);
        assert_eq!(ItemType::CRAFT_ALCHEMY_INTERMEDIATE.bits(), 0x0400_0000);
        assert_eq!(ItemType::CRAFT_FLETCHING_INTERMEDIATE.bits(), 0x0800_0000);
        assert!(
            ItemType::NAMED
                .iter()
                .all(|&(t, _)| t.bits() != 0x0200_0000)
        );
    }

    #[test]
    fn weapon_excludes_caster_and_weapon_or_caster_includes_it() {
        assert_ne!(ItemType::WEAPON, ItemType::WEAPON_OR_CASTER);
        assert_eq!(
            ItemType::MELEE_WEAPON | ItemType::MISSILE_WEAPON,
            ItemType::WEAPON
        );
        assert_eq!(
            ItemType::WEAPON | ItemType::CASTER,
            ItemType::WEAPON_OR_CASTER
        );
    }

    const RETAIL_EQUIP_MASK_COMPOSITES: &[(&str, u32)] = &[
        ("Clothing", 0x080001FF),
        ("Armor", 0x00007E00),
        ("Jewelry", 0x7C0F8000),
        ("WristWear", 0x00030000),
        ("FingerWear", 0x000C0000),
        ("Sigil", 0x70000000),
        ("ReadySlot", 0x03F00000),
        ("Weapon", 0x02500000),
        ("WeaponReadySlot", 0x03500000),
        ("All", 0x7FFFFFFF),
        ("CanGoInReadySlot", 0x7FFFFFFF),
    ];

    #[test]
    fn equip_mask_composite_matches_retail() {
        for &(name, value) in RETAIL_EQUIP_MASK_COMPOSITES {
            assert_eq!(EquipMask::from_name(name), Some(EquipMask(value)), "{name}");
        }
    }

    #[test]
    fn clothing_composite_is_the_wear_slots_plus_cloak() {
        let wear_slots = EquipMask::HEAD_WEAR
            | EquipMask::CHEST_WEAR
            | EquipMask::ABDOMEN_WEAR
            | EquipMask::UPPER_ARM_WEAR
            | EquipMask::LOWER_ARM_WEAR
            | EquipMask::HAND_WEAR
            | EquipMask::UPPER_LEG_WEAR
            | EquipMask::LOWER_LEG_WEAR
            | EquipMask::FOOT_WEAR;
        assert_eq!(wear_slots.bits(), 0x0000_01FF);
        assert_eq!(wear_slots | EquipMask::CLOAK, EquipMask::CLOTHING);
        assert_ne!(wear_slots, EquipMask::CLOTHING);
        assert_eq!(EquipMask::CLOTHING.bits() & 0x8000_0000, 0);
    }

    #[test]
    fn member_has_canonical_retail_value() {
        let singles = [
            EquipMask::NONE,
            EquipMask::HEAD_WEAR,
            EquipMask::CHEST_WEAR,
            EquipMask::ABDOMEN_WEAR,
            EquipMask::UPPER_ARM_WEAR,
            EquipMask::LOWER_ARM_WEAR,
            EquipMask::HAND_WEAR,
            EquipMask::UPPER_LEG_WEAR,
            EquipMask::LOWER_LEG_WEAR,
            EquipMask::FOOT_WEAR,
            EquipMask::CHEST_ARMOR,
            EquipMask::ABDOMEN_ARMOR,
            EquipMask::UPPER_ARM_ARMOR,
            EquipMask::LOWER_ARM_ARMOR,
            EquipMask::UPPER_LEG_ARMOR,
            EquipMask::LOWER_LEG_ARMOR,
            EquipMask::NECK_WEAR,
            EquipMask::WRIST_WEAR_LEFT,
            EquipMask::WRIST_WEAR_RIGHT,
            EquipMask::FINGER_WEAR_LEFT,
            EquipMask::FINGER_WEAR_RIGHT,
            EquipMask::MELEE_WEAPON,
            EquipMask::SHIELD,
            EquipMask::MISSILE_WEAPON,
            EquipMask::MISSILE_AMMO,
            EquipMask::HELD,
            EquipMask::TWO_HANDED,
            EquipMask::TRINKET_ONE,
            EquipMask::CLOAK,
            EquipMask::SIGIL_ONE,
            EquipMask::SIGIL_TWO,
            EquipMask::SIGIL_THREE,
        ];
        assert_eq!(singles[0].bits(), 0);
        for (bit, mask) in singles.iter().skip(1).enumerate() {
            assert_eq!(mask.bits(), 1 << bit, "{mask:?}");
        }
    }

    #[test]
    fn weapon_ready_slot_composite_is_0x3500000() {
        let mask = EquipMask::MELEE_WEAPON
            | EquipMask::MISSILE_WEAPON
            | EquipMask::HELD
            | EquipMask::TWO_HANDED;
        assert_eq!(mask.bits(), 0x0350_0000);
        assert_eq!(mask, EquipMask::WEAPON_READY_SLOT);
    }

    #[test]
    fn alias_names_resolve_to_the_first_declared() {
        assert_eq!(EquipMask::CAN_GO_IN_READY_SLOT.name(), Some("All"));
    }
}
