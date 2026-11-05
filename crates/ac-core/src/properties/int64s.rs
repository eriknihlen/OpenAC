//! The `PropertyInt64` table. Generated from the verified id list; edit the
//! list, not single entries, and keep the conformance tests green.

property_table! {
    /// The key of a signed 64-bit integer property (payload: `i64`).
    PropertyInt64 {
        UNDEF = 0 => "Undef",
        TOTAL_EXPERIENCE = 1 => "TotalExperience",
        AVAILABLE_EXPERIENCE = 2 => "AvailableExperience",
        AUGMENTATION_COST = 3 => "AugmentationCost",
        ITEM_TOTAL_XP = 4 => "ItemTotalXp",
        ITEM_BASE_XP = 5 => "ItemBaseXp",
        AVAILABLE_LUMINANCE = 6 => "AvailableLuminance",
        MAXIMUM_LUMINANCE = 7 => "MaximumLuminance",
        INTERACTION_REQS = 8 => "InteractionReqs",
    }
}
