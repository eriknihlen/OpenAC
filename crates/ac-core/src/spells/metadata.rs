//! What the client knows about one spell: the authored record from the
//! spell table plus the presentation fields derived from it.

use crate::wire_enum::wire_enum;

wire_enum! {
    values
    /// The school a spell belongs to.
    MagicSchool {
        NONE = 0 => "None",
        WAR_MAGIC = 1 => "WarMagic",
        LIFE_MAGIC = 2 => "LifeMagic",
        ITEM_ENCHANTMENT = 3 => "ItemEnchantment",
        CREATURE_ENCHANTMENT = 4 => "CreatureEnchantment",
        VOID_MAGIC = 5 => "VoidMagic",
    }
}

wire_enum! {
    flags
    /// The spell record's bit field.
    SpellFlags {
        NONE = 0 => "None",
        RESISTABLE = 0x0000_0001 => "Resistable",
        PK_SENSITIVE = 0x0000_0002 => "PKSensitive",
        BENEFICIAL = 0x0000_0004 => "Beneficial",
        SELF_TARGETED = 0x0000_0008 => "SelfTargeted",
        REVERSED = 0x0000_0010 => "Reversed",
        NOT_INDOORS = 0x0000_0020 => "NotIndoors",
        NOT_OUTDOORS = 0x0000_0040 => "NotOutdoors",
        NOT_RESEARCHABLE = 0x0000_0080 => "NotResearchable",
        PROJECTILE = 0x0000_0100 => "Projectile",
        CREATURE_SPELL = 0x0000_0200 => "CreatureSpell",
        EXCLUDED_FROM_ITEM_DESCRIPTIONS = 0x0000_0400 => "ExcludedFromItemDescriptions",
        IGNORES_MANA_CONVERSION = 0x0000_0800 => "IgnoresManaConversion",
        NON_TRACKING_PROJECTILE = 0x0000_1000 => "NonTrackingProjectile",
        FELLOWSHIP_SPELL = 0x0000_2000 => "FellowshipSpell",
        FAST_CAST = 0x0000_4000 => "FastCast",
        INDOOR_LONG_RANGE = 0x0000_8000 => "IndoorLongRange",
        DAMAGE_OVER_TIME = 0x0001_0000 => "DamageOverTime",
    }
}

wire_enum! {
    values
    /// The kind of effect a spell has (the spell record's meta type).
    SpellType {
        UNDEF = 0x00 => "Undef",
        ENCHANTMENT = 0x01 => "Enchantment",
        PROJECTILE = 0x02 => "Projectile",
        BOOST = 0x03 => "Boost",
        TRANSFER = 0x04 => "Transfer",
        PORTAL_LINK = 0x05 => "PortalLink",
        PORTAL_RECALL = 0x06 => "PortalRecall",
        PORTAL_SUMMON = 0x07 => "PortalSummon",
        PORTAL_SENDING = 0x08 => "PortalSending",
        DISPEL = 0x09 => "Dispel",
        LIFE_PROJECTILE = 0x0A => "LifeProjectile",
        FELLOW_BOOST = 0x0B => "FellowBoost",
        FELLOW_ENCHANTMENT = 0x0C => "FellowEnchantment",
        FELLOW_PORTAL_SENDING = 0x0D => "FellowPortalSending",
        FELLOW_DISPEL = 0x0E => "FellowDispel",
        ENCHANTMENT_PROJECTILE = 0x0F => "EnchantmentProjectile",
    }
}

wire_enum! {
    values
    /// What kind of reagent a spell component is.
    ComponentType {
        UNDEF = 0 => "Undef",
        SCARAB = 1 => "Scarab",
        HERB = 2 => "Herb",
        POWDER = 3 => "Powder",
        POTION = 4 => "Potion",
        TALISMAN = 5 => "Talisman",
        TAPER = 6 => "Taper",
        POTION_PEA = 7 => "PotionPea",
    }
}

/// The herb, powder, potion and talisman of a spell's formula: the part
/// that names the spell family and tells "self" from "other" versions apart.
/// Zero means the formula has none of that kind. Ordered herb first, then
/// powder, potion, talisman.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord, Default)]
pub struct SpellComponentSet {
    /// Herb component id.
    pub herb: u32,
    /// Powder component id.
    pub powder: u32,
    /// Potion component id.
    pub potion: u32,
    /// Talisman component id.
    pub talisman: u32,
}

/// One spell's record.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct SpellMetadata {
    /// Spell id.
    pub spell_id: u32,
    /// Name.
    pub name: String,
    /// School display name ("War Magic", ..., "None").
    pub school: String,
    /// Stacking family (the spell category); 0 means none.
    pub family: u32,
    /// Icon (0x06xxxxxx).
    pub icon_id: u32,
    /// The spell words ("Malar Cazael").
    pub spell_words: String,
    /// Duration in seconds, for enchantments.
    pub duration: f32,
    /// Base mana cost.
    pub mana_cost: i32,
    /// A reversed (harmful) spell.
    pub is_debuff: bool,
    /// Cast on a whole fellowship.
    pub is_fellowship: bool,
    /// Description.
    pub description: String,
    /// Display order in the spellbook.
    pub sort_key: i32,
    /// Power (difficulty).
    pub difficulty: i32,
    /// The record's bit field.
    pub flags: SpellFlags,
    /// Spell level from the formula's power component.
    pub generation: i32,
    /// Fast-cast windup.
    pub is_fast_windup: bool,
    /// Neither beneficial nor self-targeted.
    pub is_offensive: bool,
    /// The formula names no target type.
    pub is_untargeted: bool,
    /// Projectile speed (not in the spell table; zero there).
    pub speed: f32,
    /// Script played on the caster.
    pub caster_effect: u32,
    /// Script played on the target.
    pub target_effect: u32,
    /// Item-type mask of valid targets.
    pub target_mask: u32,
    /// Meta spell type.
    pub spell_type: SpellType,
    /// What the caster says while casting.
    pub saying: String,
    /// Herb, powder, potion and talisman of the formula.
    pub component_set: SpellComponentSet,
    /// School.
    pub school_id: MagicSchool,
    /// The authored formula (up to eight component ids).
    pub formula_components: Vec<u32>,
    /// Which account randomisation the formula uses.
    pub formula_version: u32,
    /// Chance to lose components.
    pub component_loss: f32,
    /// Range constant.
    pub base_range_constant: f32,
    /// Range modifier.
    pub base_range_modifier: f32,
    /// Economy modifier.
    pub spell_economy_modifier: f32,
    /// Script played on a fizzle.
    pub fizzle_effect: u32,
    /// Recovery interval (seconds).
    pub recovery_interval: f64,
    /// Recovery amount.
    pub recovery_amount: f32,
    /// Target type when components are not required.
    pub non_component_target_type: u32,
    /// Target type read from the formula.
    pub formula_target_type: u32,
    /// Mana per extra target.
    pub mana_modifier: u32,
    /// Degrade modifier.
    pub degrade_modifier: f32,
    /// Degrade limit.
    pub degrade_limit: f32,
    /// Portal lifetime (seconds).
    pub portal_lifetime: f64,
}

impl SpellMetadata {
    /// A spell with only an id, a name and a family; every other field empty.
    #[must_use]
    pub fn new(spell_id: u32, name: impl Into<String>, family: u32) -> Self {
        Self {
            spell_id,
            name: name.into(),
            family,
            ..Self::default()
        }
    }

    /// The record says the spell targets its caster.
    #[must_use]
    pub fn is_self_targeted(&self) -> bool {
        self.flags.contains(SpellFlags::SELF_TARGETED)
    }

    /// The record says the spell is beneficial.
    #[must_use]
    pub fn is_beneficial(&self) -> bool {
        self.flags.contains(SpellFlags::BENEFICIAL)
    }

    /// A projectile spell, by its flag or its meta type.
    #[must_use]
    pub fn is_projectile(&self) -> bool {
        self.flags.contains(SpellFlags::PROJECTILE)
            || matches!(
                self.spell_type,
                SpellType::PROJECTILE
                    | SpellType::LIFE_PROJECTILE
                    | SpellType::ENCHANTMENT_PROJECTILE
            )
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn component_set_orders_herb_then_powder_then_potion_then_talisman() {
        let a = SpellComponentSet {
            herb: 1,
            powder: 9,
            potion: 9,
            talisman: 9,
        };
        let b = SpellComponentSet {
            herb: 2,
            powder: 0,
            potion: 0,
            talisman: 0,
        };
        assert!(a < b);
        let c = SpellComponentSet { talisman: 10, ..a };
        assert!(a < c);
    }

    #[test]
    fn projectile_by_flag_or_meta_type() {
        let mut spell = SpellMetadata::new(1, "Test", 0);
        assert!(!spell.is_projectile());
        spell.spell_type = SpellType::LIFE_PROJECTILE;
        assert!(spell.is_projectile());
        spell.spell_type = SpellType::ENCHANTMENT;
        spell.flags = SpellFlags::PROJECTILE;
        assert!(spell.is_projectile());
    }
}
