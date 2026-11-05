//! Reading an item's use rule: whether it is usable at all, whether it
//! needs a target, and the least restrictive place the item (the source)
//! or its target may be.

use super::ItemUseable;

impl ItemUseable {
    /// The use needs a target (some target bit is set).
    #[must_use]
    pub const fn is_targeted(self) -> bool {
        self.0 & Self::TARGET_MASK.0 != 0
    }

    /// The target may be the user.
    #[must_use]
    pub const fn allows_self_target(self) -> bool {
        (self.0 >> 16) & Self::SELF.0 != 0
    }

    /// The target may be the object itself.
    #[must_use]
    pub const fn allows_object_self_target(self) -> bool {
        (self.0 >> 16) & Self::OBJ_SELF.0 != 0
    }

    /// The source half.
    #[must_use]
    pub const fn source_flags(self) -> Self {
        Self(self.0 & Self::SOURCE_MASK.0)
    }

    /// The target half, shifted down into the low bits.
    #[must_use]
    pub const fn target_flags(self) -> Self {
        Self((self.0 & Self::TARGET_MASK.0) >> 16)
    }

    /// The use is allowed at all (the "no" bit is clear). An undefined use
    /// counts as usable.
    #[must_use]
    pub const fn is_useable(self) -> bool {
        self.0 & Self::NO.0 == 0
    }

    /// Usable without a target.
    #[must_use]
    pub const fn is_direct_useable(self) -> bool {
        !self.is_targeted() && self.is_useable()
    }

    /// The most permissive source place present, in the order remote,
    /// viewed, contained, wielded, self; `UNDEF` when none is.
    #[must_use]
    pub const fn least_limited_source_use(self) -> Self {
        Self::least_limited(self.source_flags(), Self::UNDEF)
    }

    /// The most permissive target place present, in the order remote,
    /// viewed, contained, wielded, self, then the object itself.
    #[must_use]
    pub const fn least_limited_target_use(self) -> Self {
        let target = self.target_flags();
        Self::least_limited(target, Self(target.0 & Self::OBJ_SELF.0))
    }

    const fn least_limited(flags: Self, otherwise: Self) -> Self {
        if flags.0 & Self::REMOTE.0 != 0 {
            Self::REMOTE
        } else if flags.0 & Self::VIEWED.0 != 0 {
            Self::VIEWED
        } else if flags.0 & Self::CONTAINED.0 != 0 {
            Self::CONTAINED
        } else if flags.0 & Self::WIELDED.0 != 0 {
            Self::WIELDED
        } else if flags.0 & Self::SELF.0 != 0 {
            Self::SELF
        } else {
            otherwise
        }
    }
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.Core.Tests/Items/ItemUseabilityTests.cs`.

    use super::*;

    #[test]
    fn targeted_use_has_high_target_bits() {
        let health_kit = ItemUseable(0x000A_0008);
        assert!(health_kit.is_targeted());
        assert!(health_kit.allows_self_target());
        assert_eq!(health_kit.source_flags(), ItemUseable::CONTAINED);
        assert_eq!(
            health_kit.target_flags(),
            ItemUseable::SELF | ItemUseable::CONTAINED
        );
    }

    #[test]
    fn direct_contained_use_is_not_targeted() {
        assert!(!ItemUseable::CONTAINED.is_targeted());
        assert!(ItemUseable::CONTAINED.is_direct_useable());
    }

    #[test]
    fn useable_no_is_not_direct_useable() {
        assert!(!ItemUseable::NO.is_direct_useable());
    }

    #[test]
    fn is_useable_matches_low_no_bit() {
        for (value, expected) in [
            (ItemUseable::UNDEF, true),
            (ItemUseable::NO, false),
            (ItemUseable::CONTAINED, true),
            (ItemUseable::NEVER_WALK, true),
            (ItemUseable::NO | ItemUseable::CONTAINED, false),
        ] {
            assert_eq!(value.is_useable(), expected, "{value:?}");
        }
    }

    #[test]
    fn undef_is_a_zero_valued_direct_use() {
        assert!(ItemUseable::UNDEF.is_direct_useable());
    }

    #[test]
    fn least_limited_source_use_matches_priority_and_ignores_obj_self() {
        assert_eq!(
            (ItemUseable::REMOTE | ItemUseable::CONTAINED | ItemUseable::WIELDED)
                .least_limited_source_use(),
            ItemUseable::REMOTE
        );
        assert_eq!(
            (ItemUseable::WIELDED | ItemUseable::SELF).least_limited_source_use(),
            ItemUseable::WIELDED
        );
        assert_eq!(
            ItemUseable::OBJ_SELF.least_limited_source_use(),
            ItemUseable::UNDEF
        );
    }

    #[test]
    fn least_limited_target_use_falls_back_to_obj_self() {
        assert_eq!(
            ItemUseable(0x0088_0008).least_limited_target_use(),
            ItemUseable::CONTAINED
        );
        assert_eq!(
            ItemUseable(0x0080_0008).least_limited_target_use(),
            ItemUseable::OBJ_SELF
        );
        assert_eq!(
            ItemUseable::CONTAINED.least_limited_target_use(),
            ItemUseable::UNDEF
        );
    }
}
