//! Small item enums from object descriptions and appraisals.

use crate::wire_enum::wire_enum;

wire_enum! {
    flags
    /// The ammunition a launcher fires or an ammo stack is. The three base
    /// kinds (arrow, bolt, atlatl dart) each have crystal and chorizite
    /// variants.
    AmmoType {
        NONE = 0x000 => "None",
        ARROW = 0x001 => "Arrow",
        BOLT = 0x002 => "Bolt",
        ATLATL = 0x004 => "Atlatl",
        ARROW_CRYSTAL = 0x008 => "ArrowCrystal",
        BOLT_CRYSTAL = 0x010 => "BoltCrystal",
        ATLATL_CRYSTAL = 0x020 => "AtlatlCrystal",
        ARROW_CHORIZITE = 0x040 => "ArrowChorizite",
        BOLT_CHORIZITE = 0x080 => "BoltChorizite",
        ATLATL_CHORIZITE = 0x100 => "AtlatlChorizite",
    }
}

wire_enum! {
    values
    /// How a wieldable takes part in combat (a sequence, not bits).
    CombatUse {
        NONE = 0 => "None",
        MELEE = 1 => "Melee",
        MISSILE = 2 => "Missile",
        AMMO = 3 => "Ammo",
        SHIELD = 4 => "Shield",
        TWO_HANDED = 5 => "TwoHanded",
    }
}

wire_enum! {
    flags
    /// Where an object must be, and where its target must be, for a use to
    /// be allowed. The low 16 bits describe the object itself (the source),
    /// the high 16 bits the target. The combined values are the game's own
    /// list, not every possible union.
    ItemUseable {
        UNDEF = 0x0 => "Undef",
        NO = 0x1 => "No",
        SELF = 0x2 => "Self",
        WIELDED = 0x4 => "Wielded",
        CONTAINED = 0x8 => "Contained",
        VIEWED = 0x10 => "Viewed",
        REMOTE = 0x20 => "Remote",
        NEVER_WALK = 0x40 => "NeverWalk",
        OBJ_SELF = 0x80 => "ObjSelf",

        CONTAINED_VIEWED = 0x18 => "ContainedViewed",
        VIEWED_REMOTE = 0x30 => "ViewedRemote",
        CONTAINED_VIEWED_REMOTE = 0x38 => "ContainedViewedRemote",
        REMOTE_NEVER_WALK = 0x60 => "RemoteNeverWalk",
        VIEWED_REMOTE_NEVER_WALK = 0x70 => "ViewedRemoteNeverWalk",
        CONTAINED_VIEWED_REMOTE_NEVER_WALK = 0x78 => "ContainedViewedRemoteNeverWalk",

        SOURCE_WIELDED_TARGET_WIELDED = 0x0004_0004 => "SourceWieldedTargetWielded",
        SOURCE_WIELDED_TARGET_CONTAINED = 0x0008_0004 => "SourceWieldedTargetContained",
        SOURCE_WIELDED_TARGET_VIEWED = 0x0010_0004 => "SourceWieldedTargetViewed",
        SOURCE_WIELDED_TARGET_REMOTE = 0x0020_0004 => "SourceWieldedTargetRemote",
        SOURCE_WIELDED_TARGET_REMOTE_NEVER_WALK = 0x0060_0004 => "SourceWieldedTargetRemoteNeverWalk",

        SOURCE_CONTAINED_TARGET_WIELDED = 0x0004_0008 => "SourceContainedTargetWielded",
        SOURCE_CONTAINED_TARGET_CONTAINED = 0x0008_0008 => "SourceContainedTargetContained",
        SOURCE_CONTAINED_TARGET_SELF_OR_CONTAINED = 0x000A_0008 => "SourceContainedTargetSelfOrContained",
        SOURCE_CONTAINED_TARGET_VIEWED = 0x0010_0008 => "SourceContainedTargetViewed",
        SOURCE_CONTAINED_TARGET_REMOTE = 0x0020_0008 => "SourceContainedTargetRemote",
        SOURCE_CONTAINED_TARGET_REMOTE_OR_SELF = 0x0022_0008 => "SourceContainedTargetRemoteOrSelf",
        SOURCE_CONTAINED_TARGET_REMOTE_NEVER_WALK = 0x0060_0008 => "SourceContainedTargetRemoteNeverWalk",
        SOURCE_CONTAINED_TARGET_OBJ_SELF_OR_CONTAINED = 0x0088_0008 => "SourceContainedTargetObjSelfOrContained",

        SOURCE_VIEWED_TARGET_WIELDED = 0x0004_0010 => "SourceViewedTargetWielded",
        SOURCE_VIEWED_TARGET_CONTAINED = 0x0008_0010 => "SourceViewedTargetContained",
        SOURCE_VIEWED_TARGET_VIEWED = 0x0010_0010 => "SourceViewedTargetViewed",
        SOURCE_VIEWED_TARGET_REMOTE = 0x0020_0010 => "SourceViewedTargetRemote",

        SOURCE_REMOTE_TARGET_WIELDED = 0x0004_0020 => "SourceRemoteTargetWielded",
        SOURCE_REMOTE_TARGET_CONTAINED = 0x0008_0020 => "SourceRemoteTargetContained",
        SOURCE_REMOTE_TARGET_VIEWED = 0x0010_0020 => "SourceRemoteTargetViewed",
        SOURCE_REMOTE_TARGET_REMOTE = 0x0020_0020 => "SourceRemoteTargetRemote",
        SOURCE_REMOTE_TARGET_REMOTE_NEVER_WALK = 0x0060_0020 => "SourceRemoteTargetRemoteNeverWalk",

        /// The source half.
        SOURCE_MASK = 0x0000_FFFF => "SourceMask",
        /// The target half.
        TARGET_MASK = 0xFFFF_0000 => "TargetMask",
    }
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC
    //! `tests/AcDream.Core.Tests/Properties/ItemWireEnumConformanceTests.cs`.

    use super::*;

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

    const RETAIL_AMMO_TYPE: &[(&str, u32)] = &[
        ("None", 0x0),
        ("Arrow", 0x1),
        ("Bolt", 0x2),
        ("Atlatl", 0x4),
        ("ArrowCrystal", 0x8),
        ("BoltCrystal", 0x10),
        ("AtlatlCrystal", 0x20),
        ("ArrowChorizite", 0x40),
        ("BoltChorizite", 0x80),
        ("AtlatlChorizite", 0x100),
    ];

    #[test]
    fn ammo_type_matches_retail() {
        for &(name, value) in RETAIL_AMMO_TYPE {
            assert_eq!(AmmoType::from_name(name), Some(AmmoType(value)), "{name}");
        }
    }

    #[test]
    fn ammo_type_declares_nothing_retail_does_not() {
        assert_eq!(
            sorted_names(AmmoType::NAMED),
            golden_names(RETAIL_AMMO_TYPE)
        );
    }

    #[test]
    fn combat_use_matches_retail() {
        for (name, value) in [
            ("None", 0),
            ("Melee", 1),
            ("Missile", 2),
            ("Ammo", 3),
            ("Shield", 4),
            ("TwoHanded", 5),
        ] {
            assert_eq!(CombatUse::from_name(name), Some(CombatUse(value)), "{name}");
        }
    }

    const RETAIL_ITEM_USEABLE: &[(&str, u32)] = &[
        ("Undef", 0x0),
        ("No", 0x1),
        ("Self", 0x2),
        ("Wielded", 0x4),
        ("Contained", 0x8),
        ("Viewed", 0x10),
        ("ContainedViewed", 0x18),
        ("Remote", 0x20),
        ("ViewedRemote", 0x30),
        ("ContainedViewedRemote", 0x38),
        ("NeverWalk", 0x40),
        ("RemoteNeverWalk", 0x60),
        ("ViewedRemoteNeverWalk", 0x70),
        ("ContainedViewedRemoteNeverWalk", 0x78),
        ("ObjSelf", 0x80),
        ("SourceMask", 0xFFFF),
        ("SourceWieldedTargetWielded", 0x40004),
        ("SourceContainedTargetWielded", 0x40008),
        ("SourceViewedTargetWielded", 0x40010),
        ("SourceRemoteTargetWielded", 0x40020),
        ("SourceWieldedTargetContained", 0x80004),
        ("SourceContainedTargetContained", 0x80008),
        ("SourceViewedTargetContained", 0x80010),
        ("SourceRemoteTargetContained", 0x80020),
        ("SourceContainedTargetSelfOrContained", 0xA0008),
        ("SourceWieldedTargetViewed", 0x100004),
        ("SourceContainedTargetViewed", 0x100008),
        ("SourceViewedTargetViewed", 0x100010),
        ("SourceRemoteTargetViewed", 0x100020),
        ("SourceWieldedTargetRemote", 0x200004),
        ("SourceContainedTargetRemote", 0x200008),
        ("SourceViewedTargetRemote", 0x200010),
        ("SourceRemoteTargetRemote", 0x200020),
        ("SourceContainedTargetRemoteOrSelf", 0x220008),
        ("SourceWieldedTargetRemoteNeverWalk", 0x600004),
        ("SourceContainedTargetRemoteNeverWalk", 0x600008),
        ("SourceRemoteTargetRemoteNeverWalk", 0x600020),
        ("SourceContainedTargetObjSelfOrContained", 0x880008),
        ("TargetMask", 0xFFFF0000),
    ];

    #[test]
    fn item_useable_matches_retail() {
        for &(name, value) in RETAIL_ITEM_USEABLE {
            assert_eq!(
                ItemUseable::from_name(name),
                Some(ItemUseable(value)),
                "{name}"
            );
        }
    }

    #[test]
    fn item_useable_declares_nothing_retail_does_not() {
        assert_eq!(
            sorted_names(ItemUseable::NAMED),
            golden_names(RETAIL_ITEM_USEABLE)
        );
    }

    #[test]
    fn item_useable_source_and_target_masks_partition_the_word() {
        assert_eq!(
            ItemUseable::SOURCE_MASK.bits() & ItemUseable::TARGET_MASK.bits(),
            0
        );
        assert_eq!(
            ItemUseable::SOURCE_MASK.bits() | ItemUseable::TARGET_MASK.bits(),
            u32::MAX
        );
        let naive_union = ItemUseable::OBJ_SELF.bits()
            | ItemUseable::CONTAINED.bits()
            | (ItemUseable::CONTAINED.bits() << 16);
        assert_ne!(
            ItemUseable::SOURCE_CONTAINED_TARGET_OBJ_SELF_OR_CONTAINED.bits(),
            naive_union
        );
    }
}
