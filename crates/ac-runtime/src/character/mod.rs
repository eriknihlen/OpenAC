//! The character options: the two option words the character description
//! carries, the one table that names every option's word and bit, and the
//! way a change reaches the server.
//!
//! Some options are saved at once (one `SetSingleCharacterOption` each);
//! the rest mark the options dirty, and the whole block goes out with
//! `SetCharacterOptions` before log off or 480 seconds after the first
//! unsaved change. A block is only ever sent after a description seeded
//! the words: until then they are client defaults, not the character's.

use std::time::Duration;

use ac_proto::social::{CharacterOptions as OptionsBlock, build_set_character_options};
use ac_proto::{DesiredComponent, PlayerDescription, Shortcut};

/// The options word the client starts from before a description arrives.
pub const DEFAULT_OPTIONS1: u32 = 0x50C4_A54A;
/// The second options word the client starts from.
pub const DEFAULT_OPTIONS2: u32 = 0x0094_8700;

/// How long after the first unsaved change the options save themselves.
pub const AUTO_SAVE_DELAY: Duration = Duration::from_secs(480);

/// One character option: its id (the number the single-option request
/// carries), name, which word holds it and at which bit, whether a change
/// is saved at once, and its client default.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct OptionEntry {
    /// The option id, 0x00..=0x34.
    pub id: u32,
    /// The option's name.
    pub name: &'static str,
    /// The bit lives in the first word (else the second).
    pub in_options1: bool,
    /// The bit.
    pub mask: u32,
    /// A change is sent at once rather than with the next block save.
    pub auto_save: bool,
    /// The client's default value.
    pub client_default: bool,
}

macro_rules! option_table {
    ($($konst:ident = $id:literal, $name:literal, $word:literal, $mask:literal, $save:literal, $default:literal;)*) => {
        /// Option ids.
        pub mod option_id {
            $(
                #[doc = concat!("`", $name, "`")]
                pub const $konst: u32 = $id;
            )*
        }

        /// Every option, by ascending id.
        pub const OPTIONS: &[OptionEntry] = &[$(OptionEntry {
            id: $id,
            name: $name,
            in_options1: $word == 1,
            mask: $mask,
            auto_save: $save,
            client_default: $default,
        },)*];
    };
}

option_table! {
    AUTO_REPEAT_ATTACK = 0x00, "AutoRepeatAttack", 1, 0x0000_0002, true, true;
    IGNORE_ALLEGIANCE_REQUESTS = 0x01, "IgnoreAllegianceRequests", 1, 0x0000_0004, true, false;
    IGNORE_FELLOWSHIP_REQUESTS = 0x02, "IgnoreFellowshipRequests", 1, 0x0000_0008, true, true;
    IGNORE_TRADE_REQUESTS = 0x03, "IgnoreTradeRequests", 1, 0x0002_0000, false, false;
    DISABLE_MOST_WEATHER_EFFECTS = 0x04, "DisableMostWeatherEffects", 1, 0x0001_0000, false, false;
    PERSISTENT_AT_DAY = 0x05, "PersistentAtDay", 2, 0x0000_0001, false, false;
    ALLOW_GIVE = 0x06, "AllowGive", 1, 0x0000_0040, false, true;
    VIEW_COMBAT_TARGET = 0x07, "ViewCombatTarget", 1, 0x0000_0080, false, false;
    SHOW_TOOLTIPS = 0x08, "ShowTooltips", 1, 0x0000_0100, false, true;
    USE_DECEPTION = 0x09, "UseDeception", 1, 0x0000_0200, false, false;
    TOGGLE_RUN = 0x0A, "ToggleRun", 1, 0x0000_0400, false, true;
    STAY_IN_CHAT_MODE = 0x0B, "StayInChatMode", 1, 0x0000_0800, false, false;
    ADVANCED_COMBAT_UI = 0x0C, "AdvancedCombatUI", 1, 0x0000_1000, false, false;
    AUTO_TARGET = 0x0D, "AutoTarget", 1, 0x0000_2000, false, true;
    VIVID_TARGETING_INDICATOR = 0x0E, "VividTargetingIndicator", 1, 0x0000_8000, false, true;
    FELLOWSHIP_SHARE_XP = 0x0F, "FellowshipShareXP", 1, 0x0004_0000, true, true;
    ACCEPT_LOOT_PERMITS = 0x10, "AcceptLootPermits", 1, 0x0008_0000, true, false;
    FELLOWSHIP_SHARE_LOOT = 0x11, "FellowshipShareLoot", 1, 0x0010_0000, true, false;
    FELLOWSHIP_AUTO_ACCEPT_REQUESTS = 0x12, "FellowshipAutoAcceptRequests", 1, 0x2000_0000, true, false;
    SIDE_BY_SIDE_VITALS = 0x13, "SideBySideVitals", 1, 0x0020_0000, false, false;
    COORDINATES_ON_RADAR = 0x14, "CoordinatesOnRadar", 1, 0x0040_0000, false, true;
    SPELL_DURATION = 0x15, "SpellDuration", 1, 0x0080_0000, false, true;
    DISABLE_HOUSE_RESTRICTION_EFFECTS = 0x16, "DisableHouseRestrictionEffects", 1, 0x0200_0000, false, false;
    DRAG_ITEM_ON_PLAYER_OPENS_SECURE_TRADE = 0x17, "DragItemOnPlayerOpensSecureTrade", 1, 0x0400_0000, false, false;
    DISPLAY_ALLEGIANCE_LOGON_NOTIFICATIONS = 0x18, "DisplayAllegianceLogonNotifications", 1, 0x0800_0000, false, false;
    USE_CHARGE_ATTACK = 0x19, "UseChargeAttack", 1, 0x1000_0000, true, true;
    USE_CRAFT_SUCCESS_DIALOG = 0x1A, "UseCraftSuccessDialog", 1, 0x8000_0000, false, false;
    LISTEN_TO_ALLEGIANCE_CHAT = 0x1B, "ListenToAllegianceChat", 1, 0x4000_0000, true, true;
    DISPLAY_DATE_OF_BIRTH = 0x1C, "DisplayDateOfBirth", 2, 0x0000_0002, false, false;
    DISPLAY_AGE = 0x1D, "DisplayAge", 2, 0x0000_0020, false, false;
    DISPLAY_CHESS_RANK = 0x1E, "DisplayChessRank", 2, 0x0000_0004, false, false;
    DISPLAY_FISHING_SKILL = 0x1F, "DisplayFishingSkill", 2, 0x0000_0008, false, false;
    DISPLAY_NUMBER_DEATHS = 0x20, "DisplayNumberDeaths", 2, 0x0000_0010, false, false;
    DISPLAY_TIME_STAMPS = 0x21, "DisplayTimeStamps", 2, 0x0000_0040, false, false;
    SALVAGE_MULTIPLE = 0x22, "SalvageMultiple", 2, 0x0000_0080, false, false;
    LISTEN_TO_GENERAL_CHAT = 0x23, "ListenToGeneralChat", 2, 0x0000_0100, true, true;
    LISTEN_TO_TRADE_CHAT = 0x24, "ListenToTradeChat", 2, 0x0000_0200, true, true;
    LISTEN_TO_LFG_CHAT = 0x25, "ListenToLFGChat", 2, 0x0000_0400, true, true;
    LISTEN_TO_ROLEPLAY_CHAT = 0x26, "ListenToRoleplayChat", 2, 0x0000_0800, true, false;
    APPEAR_OFFLINE = 0x27, "AppearOffline", 2, 0x0000_1000, true, false;
    DISPLAY_NUMBER_CHARACTER_TITLES = 0x28, "DisplayNumberCharacterTitles", 2, 0x0000_2000, false, false;
    MAIN_PACK_PREFERRED = 0x29, "MainPackPreferred", 2, 0x0000_4000, false, false;
    LEAD_MISSILE_TARGETS = 0x2A, "LeadMissileTargets", 2, 0x0000_8000, true, true;
    USE_FAST_MISSILES = 0x2B, "UseFastMissiles", 2, 0x0001_0000, true, false;
    FILTER_LANGUAGE = 0x2C, "FilterLanguage", 2, 0x0002_0000, false, false;
    CONFIRM_VOLATILE_RARE_USE = 0x2D, "ConfirmVolatileRareUse", 2, 0x0004_0000, false, false;
    LISTEN_TO_SOCIETY_CHAT = 0x2E, "ListenToSocietyChat", 2, 0x0008_0000, true, false;
    SHOW_HELM = 0x2F, "ShowHelm", 2, 0x0010_0000, true, false;
    DISABLE_DISTANCE_FOG = 0x30, "DisableDistanceFog", 2, 0x0020_0000, false, false;
    USE_MOUSE_TURNING = 0x31, "UseMouseTurning", 2, 0x0040_0000, true, false;
    SHOW_CLOAK = 0x32, "ShowCloak", 2, 0x0080_0000, true, false;
    LOCK_UI = 0x33, "LockUI", 2, 0x0100_0000, true, false;
    HEAR_PK_DEATH_MESSAGES = 0x34, "HearPkDeathMessages", 2, 0x0200_0000, false, false;
}

/// The option with this id.
#[must_use]
pub fn option(id: u32) -> Option<&'static OptionEntry> {
    OPTIONS.get(id as usize).filter(|entry| entry.id == id)
}

/// The option with this name, ignoring case.
#[must_use]
pub fn option_by_name(name: &str) -> Option<&'static OptionEntry> {
    OPTIONS
        .iter()
        .find(|entry| entry.name.eq_ignore_ascii_case(name))
}

/// What the full block repeats besides the two words: the parts of the
/// character description's option trailer the client sends back as it
/// received them (the shortcut bar, favorite spells, desired components
/// and spellbook filters). The spellbook parts follow the live spellbook
/// before a save ([`CharacterOptions::echo_spellbook`]) and the shortcut bar
/// follows its owner, the inventory ([`CharacterOptions::echo_shortcuts`]).
#[derive(Debug, Clone, Default, PartialEq)]
struct BlockEcho {
    shortcuts: Vec<Shortcut>,
    favorite_spells: [Vec<u32>; 8],
    desired_components: Vec<DesiredComponent>,
    spellbook_filters: u32,
}

/// The character's option words and their save state.
#[derive(Debug, Clone, PartialEq)]
pub struct CharacterOptions {
    options1: u32,
    options2: u32,
    revision: u64,
    dirty: bool,
    first_dirtied_at: Option<Duration>,
    has_server_seed: bool,
    echo: BlockEcho,
}

impl Default for CharacterOptions {
    fn default() -> Self {
        Self {
            options1: DEFAULT_OPTIONS1,
            options2: DEFAULT_OPTIONS2,
            revision: 0,
            dirty: false,
            first_dirtied_at: None,
            has_server_seed: false,
            echo: BlockEcho {
                spellbook_filters: ac_proto::DEFAULT_SPELLBOOK_FILTERS,
                ..BlockEcho::default()
            },
        }
    }
}

impl CharacterOptions {
    /// The client defaults, unseeded.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// The first word.
    #[must_use]
    pub fn options1(&self) -> u32 {
        self.options1
    }

    /// The second word.
    #[must_use]
    pub fn options2(&self) -> u32 {
        self.options2
    }

    /// Bumped by every change to either word.
    #[must_use]
    pub fn revision(&self) -> u64 {
        self.revision
    }

    /// A change waits for the next block save.
    #[must_use]
    pub fn is_dirty(&self) -> bool {
        self.dirty
    }

    /// When the first unsaved change was made (session time).
    #[must_use]
    pub fn first_dirtied_at(&self) -> Option<Duration> {
        self.first_dirtied_at.filter(|_| self.dirty)
    }

    /// A character description seeded the words.
    #[must_use]
    pub fn has_server_seed(&self) -> bool {
        self.has_server_seed
    }

    /// One option's value; false for an unknown id.
    #[must_use]
    pub fn get(&self, id: u32) -> bool {
        option(id).is_some_and(|entry| self.word(entry) & entry.mask != 0)
    }

    fn word(&self, entry: &OptionEntry) -> u32 {
        if entry.in_options1 {
            self.options1
        } else {
            self.options2
        }
    }

    /// Install a description's words as the character's, clearing any
    /// unsaved change (the server's words supersede it) and, when
    /// `arm_server_seed`, allowing block saves from now on.
    pub fn replace(&mut self, options1: u32, options2: u32, arm_server_seed: bool) {
        self.options1 = options1;
        self.options2 = options2;
        self.revision += 1;
        self.dirty = false;
        self.first_dirtied_at = None;
        if arm_server_seed {
            self.has_server_seed = true;
        }
    }

    /// The character description: its option words and the trailer parts
    /// a block save sends back. A description whose option trailer was cut
    /// short carries no real words and changes nothing.
    pub fn apply_player_description(&mut self, description: &PlayerDescription) {
        if description.trailer_truncated {
            return;
        }
        self.replace(description.options1, description.options2, true);
        self.echo.shortcuts.clone_from(&description.shortcuts);
        for (tab, list) in self.echo.favorite_spells.iter_mut().enumerate() {
            *list = description
                .hotbar_spells
                .get(tab)
                .cloned()
                .unwrap_or_default();
        }
        self.echo
            .desired_components
            .clone_from(&description.desired_comps);
        self.echo.spellbook_filters = description.spellbook_filters;
    }

    /// Take the shortcut bar from its owner, so a block save repeats what
    /// the player now has.
    pub fn echo_shortcuts(&mut self, store: &ac_core::items::ShortcutStore) {
        self.echo.shortcuts = store
            .items()
            .into_iter()
            .map(|e| Shortcut {
                index: e.index,
                object_id: e.object_id,
                spell_id: e.spell_id,
            })
            .collect();
    }

    /// Take the favorite bars, desired components and spellbook filter from
    /// the live spellbook (the magic owner edits them), so a block save
    /// repeats what the player now has.
    pub fn echo_spellbook(&mut self, book: &ac_core::spells::Spellbook) {
        for (tab, list) in self.echo.favorite_spells.iter_mut().enumerate() {
            *list = book.get_favorites(tab).to_vec();
        }
        self.echo.desired_components = book
            .desired_components()
            .iter()
            .map(|(&id, &amount)| DesiredComponent { id, amount })
            .collect();
        self.echo.spellbook_filters = book.spellbook_filters();
    }

    /// Set one bit locally; nothing for an unknown id.
    pub fn set_bit(&mut self, id: u32, value: bool) {
        let Some(entry) = option(id) else {
            return;
        };
        let word = if entry.in_options1 {
            &mut self.options1
        } else {
            &mut self.options2
        };
        if value {
            *word |= entry.mask;
        } else {
            *word &= !entry.mask;
        }
        self.revision += 1;
    }

    /// Change one option the way the options panel does: write it locally,
    /// then save it at once through `send` (an auto-save option) or mark
    /// the block dirty at session time `now`. Turning on "ignore fellowship
    /// requests" turns off "auto-accept fellowship requests" first, and the
    /// other way round. An unchanged value does nothing. False for an
    /// unknown id.
    pub fn try_set(
        &mut self,
        id: u32,
        value: bool,
        now: Duration,
        send: &mut impl FnMut(u32, bool),
    ) -> bool {
        let Some(entry) = option(id) else {
            return false;
        };
        if (self.word(entry) & entry.mask != 0) == value {
            return true;
        }
        self.set_bit(id, value);
        if value && id == option_id::IGNORE_FELLOWSHIP_REQUESTS {
            self.try_set(option_id::FELLOWSHIP_AUTO_ACCEPT_REQUESTS, false, now, send);
        } else if value && id == option_id::FELLOWSHIP_AUTO_ACCEPT_REQUESTS {
            self.try_set(option_id::IGNORE_FELLOWSHIP_REQUESTS, false, now, send);
        }
        if entry.auto_save {
            send(id, value);
        } else {
            self.mark_dirty(now);
        }
        true
    }

    /// Mark the block dirty; only the first change since the last save
    /// starts the auto-save clock.
    pub fn mark_dirty(&mut self, now: Duration) {
        if self.dirty {
            return;
        }
        self.dirty = true;
        self.first_dirtied_at = Some(now);
    }

    /// Save the block through `flush` when dirty and seeded. True when it
    /// was sent.
    pub fn try_flush(&mut self, flush: impl FnOnce(&Self)) -> bool {
        if !self.dirty || !self.has_server_seed {
            return false;
        }
        flush(self);
        self.dirty = false;
        self.first_dirtied_at = None;
        true
    }

    /// Save the block when dirty, seeded, and [`AUTO_SAVE_DELAY`] has passed
    /// since the first unsaved change.
    pub fn try_flush_if_auto_save_due(&mut self, now: Duration, flush: impl FnOnce(&Self)) -> bool {
        let due = self
            .first_dirtied_at
            .is_some_and(|first| now.saturating_sub(first) >= AUTO_SAVE_DELAY);
        due && self.try_flush(flush)
    }

    /// The block-save body: both words and the trailer parts as the
    /// description sent them.
    #[must_use]
    pub fn build_block(&self, sequence: u32) -> Vec<u8> {
        let favorites = &self.echo.favorite_spells;
        build_set_character_options(
            sequence,
            &OptionsBlock {
                options1: self.options1,
                options2: self.options2,
                shortcuts: &self.echo.shortcuts,
                favorite_spells: [
                    &favorites[0],
                    &favorites[1],
                    &favorites[2],
                    &favorites[3],
                    &favorites[4],
                    &favorites[5],
                    &favorites[6],
                    &favorites[7],
                ],
                desired_components: &self.echo.desired_components,
                spellbook_filters: self.echo.spellbook_filters,
            },
        )
    }

    /// Session end: the client defaults again, clean and unseeded, so a
    /// later session's description has to seed them before a save.
    pub fn reset_session(&mut self) {
        *self = Self {
            revision: self.revision + 1,
            ..Self::default()
        };
    }
}

/// A request to spend experience or skill credits (the character window's
/// raise buttons, after any confirmation).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CharacterCommand {
    /// Raise an attribute (1 strength .. 6 self) by spending `xp`.
    RaiseAttribute {
        /// Attribute id.
        attribute: u32,
        /// Experience spent.
        xp: u64,
    },
    /// Raise a vital's maximum (1 health, 3 stamina, 5 mana).
    RaiseVital {
        /// Vital id.
        vital: u32,
        /// Experience spent.
        xp: u64,
    },
    /// Raise a trained or specialized skill.
    RaiseSkill {
        /// Skill id.
        skill: u32,
        /// Experience spent.
        xp: u64,
    },
    /// Train a skill with skill credits.
    TrainSkill {
        /// Skill id.
        skill: u32,
        /// Credits spent.
        credits: u32,
    },
}

impl CharacterCommand {
    /// The request's body. The experience field is 32 bits on the wire; a
    /// larger amount keeps its low 32 bits, as the game's client sends it.
    #[must_use]
    pub fn build(self, sequence: u32) -> Vec<u8> {
        use ac_proto::character::{
            build_raise_attribute, build_raise_skill, build_raise_vital, build_train_skill,
        };
        let low = |xp: u64| xp as u32;
        match self {
            Self::RaiseAttribute { attribute, xp } => {
                build_raise_attribute(sequence, attribute, low(xp))
            }
            Self::RaiseVital { vital, xp } => build_raise_vital(sequence, vital, low(xp)),
            Self::RaiseSkill { skill, xp } => build_raise_skill(sequence, skill, low(xp)),
            Self::TrainSkill { skill, credits } => build_train_skill(sequence, skill, credits),
        }
    }
}

mod seeder;
pub use seeder::{OptionsSeeder, SeedStep};

#[cfg(test)]
mod tests;
