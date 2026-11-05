//! The character (API group D): who it is, its numbers, its enchantments,
//! spending experience, and its options.

use crate::{wire_enum, wire_record};

wire_record! {
    /// The character in the world ([`crate::Import::CharacterInfo`]).
    pub struct CharacterInfo {
        /// Its object id.
        pub id: u32,
        /// Its name.
        pub name: String,
        /// The world (server) it is on.
        pub world: String,
        /// The account, as the server spells it.
        pub account: String,
        /// Its slot on the character list, -1 when unknown.
        pub character_index: i32,
        /// The population the server reported at login, -1 before.
        pub server_population: i32,
        /// Its level.
        pub level: i32,
        /// Free item slots in the main pack (102 minus what it holds).
        pub main_pack_free_slots: i32,
        /// Summoning mastery.
        pub summoning_mastery: i32,
        /// Health now, at full (enchanted), and at full with no
        /// enchantments.
        pub health: (u32, u32, u32),
        /// Stamina: now, at full, at full unenchanted.
        pub stamina: (u32, u32, u32),
        /// Mana: now, at full, at full unenchanted.
        pub mana: (u32, u32, u32),
        /// The title shown, 0 for none.
        pub current_title_id: u32,
        /// Every title earned: id and name.
        pub titles: Vec<(u32, String)>,
        /// The vitae multiplier (1.0 with no death penalty).
        pub vitae_multiplier: f32,
    }
}

wire_record! {
    /// The character's experience and luminance as the server keeps them
    /// ([`crate::Import::CharacterExperience`],
    /// [`crate::types::Event::ExperienceChanged`]).
    pub struct CharacterExperience {
        /// Every point of experience ever earned; spending does not lower
        /// it.
        pub total: i64,
        /// Experience not yet spent on a stat.
        pub unassigned: i64,
        /// Experience still needed for the next level, measured on the
        /// dats' level curve; `None` at the top of the table (the sheet's
        /// "Infinity!") or with no table. 0 for a moment after a level's
        /// worth lands, until the server sends the new level.
        pub to_next_level: Option<i64>,
        /// Luminance held now.
        pub available_luminance: i64,
        /// The cap on luminance; 0 for a character that cannot earn it yet.
        pub maximum_luminance: i64,
    }
}

wire_enum! {
    /// How far a skill is trained.
    pub enum Training: "training" {
        /// The client has not been told.
        Unknown = 0,
        /// Untrained.
        Untrained = 1,
        /// Trained.
        Trained = 2,
        /// Specialized.
        Specialized = 3,
    }
}

wire_record! {
    /// One skill.
    pub struct Skill {
        /// The skill id.
        pub skill_id: u32,
        /// Its name.
        pub name: String,
        /// Its training.
        pub training: Training,
        /// The level with every bonus and enchantment.
        pub current: u32,
        /// The level before enchantments.
        pub base: u32,
        /// Its icon.
        pub icon_id: u32,
        /// Ranks raised.
        pub ranks: u32,
        /// Experience spent on it.
        pub experience_spent: u64,
    }
}

wire_record! {
    /// One attribute.
    pub struct Attribute {
        /// 0 strength .. 5 self.
        pub kind: i32,
        /// Its name.
        pub name: String,
        /// With enchantments.
        pub current: u32,
        /// Without.
        pub base: u32,
        /// The attribute id (kind + 1).
        pub stat_id: u32,
        /// Ranks raised.
        pub ranks: u32,
        /// Experience spent.
        pub experience_spent: u64,
    }
}

wire_record! {
    /// One vital.
    pub struct Vital {
        /// 0 health, 1 stamina, 2 mana.
        pub kind: i32,
        /// Its name.
        pub name: String,
        /// Now.
        pub current: u32,
        /// At full, with enchantments.
        pub maximum: u32,
        /// The id of its maximum (kind * 2 + 1).
        pub stat_id: u32,
        /// At full, without enchantments.
        pub base: u32,
        /// Ranks raised.
        pub ranks: u32,
        /// Experience spent.
        pub experience_spent: u64,
    }
}

wire_record! {
    /// An enchantment on the character.
    pub struct ActiveEnchantment {
        /// The spell.
        pub spell_id: u32,
        /// Its family (one of a family is in force at a time).
        pub family: u32,
        /// Its rank in the family.
        pub tier: i32,
        /// Seconds left; negative for one that does not run out.
        pub seconds_remaining: f64,
    }
}

wire_enum! {
    /// What experience or credits are spent on.
    pub enum AdvancementKind: "advancement kind" {
        /// An attribute.
        Attribute = 0,
        /// A vital's maximum.
        Vital = 1,
        /// A trained skill.
        Skill = 2,
        /// Training a skill with credits.
        TrainSkill = 3,
    }
}

/// The largest experience cost one request may spend (32 bits on the
/// wire).
pub const MAX_EXPERIENCE_COST: u64 = u32::MAX as u64;
/// The most skill credits one training request may spend.
pub const MAX_SKILL_CREDITS: u64 = 1_000;

wire_enum! {
    /// The answer to an advancement request.
    pub enum AdvancementStatus: "advancement status" {
        /// Not in the world.
        Unavailable = 0,
        /// The id names no such stat.
        UnknownStat = 1,
        /// The cost is outside the allowed range.
        InvalidCost = 2,
        /// Sent to the server.
        Sent = 3,
        /// The client declined.
        Refused = 4,
    }
}

wire_record! {
    /// Spend on one stat ([`crate::Import::CharacterAdvance`]).
    pub struct AdvancementRequest {
        /// What kind of stat.
        pub kind: AdvancementKind,
        /// The stat id.
        pub stat_id: u32,
        /// Experience (or credits for training).
        pub cost: u64,
    }
}

wire_record! {
    /// The answer to an [`AdvancementRequest`].
    pub struct AdvancementResult {
        /// The verdict.
        pub status: AdvancementStatus,
        /// Why, in words.
        pub notice: Option<String>,
    }
}

wire_enum! {
    /// The answer to a character option change.
    pub enum OptionStatus: "option status" {
        /// Not in the world.
        Unavailable = 0,
        /// No option by that name.
        UnknownOption = 1,
        /// Set.
        Accepted = 2,
    }
}

wire_record! {
    /// The answer to a character option change.
    pub struct OptionResult {
        /// The verdict.
        pub status: OptionStatus,
        /// Why, in words.
        pub notice: Option<String>,
    }
}
