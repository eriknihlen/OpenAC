//! API group D: the character (OpenAC `ICharacterInfo`,
//! `ICharacterOptionsAutomation`) over the player's numbers, its item
//! record, the titles and the options.

use ac_core::player::{AttributeKind, VitalKind, skill_names};
use ac_core::properties::PropertyInt;
use ac_core::spells::get_vitae_multiplier;
use ac_plugin::abi::CharacterInfo;
use ac_plugin::abi::character::{
    ActiveEnchantment, AdvancementKind, AdvancementRequest, AdvancementResult, AdvancementStatus,
    Attribute, CharacterExperience, MAX_EXPERIENCE_COST, MAX_SKILL_CREDITS, OptionResult,
    OptionStatus, Skill, Training, Vital,
};
use ac_plugin::abi::world::ObjectClass;

use super::{PluginBinding, Ran};
use crate::character::{CharacterCommand, OPTIONS, OptionEntry, option_by_name, option_id};
use crate::session::SessionCommand;

/// The attributes in id order, with their names (OpenAC `AttributeNames`).
const ATTRIBUTES: [(AttributeKind, &str); 6] = [
    (AttributeKind::Strength, "Strength"),
    (AttributeKind::Endurance, "Endurance"),
    (AttributeKind::Quickness, "Quickness"),
    (AttributeKind::Coordination, "Coordination"),
    (AttributeKind::Focus, "Focus"),
    (AttributeKind::Self_, "Self"),
];

/// The pools in the order a plugin reads them (OpenAC `VitalNames`).
const VITALS: [(VitalKind, &str); 3] = [
    (VitalKind::Health, "Health"),
    (VitalKind::Stamina, "Stamina"),
    (VitalKind::Mana, "Mana"),
];

/// A main pack holds this many items (OpenAC `MainPackFreeSlots`).
const MAIN_PACK_SLOTS: i32 = 102;

/// The character's experience and luminance as a plugin reads them (OpenAC
/// `RuntimeAutomationSurface.ReadExperience`): none outside a stay in the
/// world or before the character's description arrived. The level is the
/// player object's, which a level-up updates; the distance to the next
/// level is measured on the dats' level curve.
pub(crate) fn experience(state: &crate::session::SessionState) -> Option<CharacterExperience> {
    use ac_core::properties::PropertyInt64;
    if !super::events::in_stay(state) || state.world.player().is_none() {
        return None;
    }
    let player = state.world.player_guid()?;
    let properties = &state.world.item(player)?.properties;
    let int64 = |key: PropertyInt64| properties.int64s.get(&key.0).copied().unwrap_or(0);
    let total = int64(PropertyInt64::TOTAL_EXPERIENCE);
    let level = properties
        .ints
        .get(&PropertyInt::LEVEL.0)
        .copied()
        .unwrap_or(0);
    let progress = ac_core::player::LevelProgress::measure(
        state.gameplay.tables().experience_levels(),
        level,
        total,
    );
    Some(CharacterExperience {
        total,
        unassigned: int64(PropertyInt64::AVAILABLE_EXPERIENCE),
        to_next_level: progress.filter(|p| !p.at_top_of_table).map(|p| p.to_next),
        available_luminance: int64(PropertyInt64::AVAILABLE_LUMINANCE),
        maximum_luminance: int64(PropertyInt64::MAXIMUM_LUMINANCE),
    })
}

fn training(status: u32) -> Training {
    match status {
        1 => Training::Untrained,
        2 => Training::Trained,
        3 => Training::Specialized,
        _ => Training::Unknown,
    }
}

impl PluginBinding<'_> {
    /// The character in the world with everything OpenAC's
    /// `ICharacterInfo` reads.
    pub(super) fn character_info(&self) -> Option<CharacterInfo> {
        let identity = self.character.as_ref()?;
        let world = &self.state.world;
        let player = world.player_guid().unwrap_or(identity.id);
        let record = world.item(player);
        let int = |key: PropertyInt| {
            record.map_or(0, |r| r.properties.ints.get(&key.0).copied().unwrap_or(0))
        };
        let occupied = world
            .items()
            .objects()
            .filter(|item| {
                item.container_id == player
                    && !matches!(
                        super::world::classify(Some(item)),
                        ObjectClass::Container | ObjectClass::Foci
                    )
                    && item.currently_equipped_location.bits() == 0
            })
            .count();
        let numbers = world.player().map(|p| p.numbers());
        let vital = |kind: VitalKind| {
            numbers.map_or((0, 0, 0), |n| {
                let current = n.get(kind).map_or(0, |v| v.current);
                let maximum = n.get_max_approx(kind).unwrap_or(0);
                (
                    current,
                    maximum,
                    n.get_base_max_approx(kind).unwrap_or(maximum),
                )
            })
        };
        let titles = self.state.gameplay.titles();
        let roster_index = self
            .facts
            .roster
            .iter()
            .find(|c| c.id == identity.id)
            .map_or(-1, |c| c.index);
        Some(CharacterInfo {
            id: identity.id,
            name: record
                .map(|r| r.name.clone())
                .filter(|n| !n.is_empty())
                .unwrap_or_else(|| identity.name.clone()),
            world: identity.world.clone(),
            account: self.facts.account.clone(),
            character_index: roster_index,
            server_population: self.facts.server_population,
            level: int(PropertyInt::LEVEL),
            main_pack_free_slots: (MAIN_PACK_SLOTS - i32::try_from(occupied).unwrap_or(i32::MAX))
                .max(0),
            summoning_mastery: int(PropertyInt::SUMMONING_MASTERY),
            health: vital(VitalKind::Health),
            stamina: vital(VitalKind::Stamina),
            mana: vital(VitalKind::Mana),
            current_title_id: titles.display_title_id(),
            titles: titles
                .earned_title_ids()
                .map(|id| (id, String::new()))
                .collect(),
            vitae_multiplier: world
                .spellbook()
                .map_or(1.0, |book| get_vitae_multiplier(book.active_enchantments())),
        })
    }

    fn skill_name(&self, skill_id: u32) -> String {
        self.state
            .gameplay
            .tables()
            .skills()
            .get(&skill_id)
            .map(|s| s.name.clone())
            .filter(|n| !n.is_empty())
            .or_else(|| skill_names::try_get_name(skill_id.cast_signed()).map(str::to_owned))
            .unwrap_or_default()
    }

    /// One skill (OpenAC `TryProjectSkill`): current with every bonus, base
    /// before enchantments.
    pub(super) fn character_skill(&self, skill_id: u32) -> Option<Skill> {
        self.character.as_ref()?;
        let numbers = self.state.world.player()?.numbers();
        let snapshot = numbers.get_skill(skill_id)?;
        let base = snapshot.current_level();
        let current = numbers
            .get_effective_skill(skill_id)
            .unwrap_or_else(|| base.cast_signed())
            .max(0)
            .cast_unsigned();
        Some(Skill {
            skill_id,
            name: self.skill_name(skill_id),
            training: training(snapshot.status),
            current,
            base,
            icon_id: 0,
            ranks: snapshot.ranks,
            experience_spent: u64::from(snapshot.xp),
        })
    }

    /// Every skill the server told, sorted by name (ordinal).
    pub(super) fn character_skills(&self) -> Vec<Skill> {
        let Some(player) = self.state.world.player() else {
            return Vec::new();
        };
        let ids: Vec<u32> = player.numbers().skills().keys().copied().collect();
        let mut skills: Vec<Skill> = ids
            .into_iter()
            .filter_map(|id| self.character_skill(id))
            .collect();
        skills.sort_by(|a, b| a.name.cmp(&b.name));
        skills
    }

    /// The six attributes: current with enchantments, base without.
    pub(super) fn character_attributes(&self) -> Vec<Attribute> {
        if self.character.is_none() {
            return Vec::new();
        }
        let Some(player) = self.state.world.player() else {
            return Vec::new();
        };
        let numbers = player.numbers();
        ATTRIBUTES
            .iter()
            .enumerate()
            .filter_map(|(kind, (attribute, name))| {
                let snapshot = numbers.get_attribute(*attribute)?;
                let effective = numbers
                    .get_effective_attribute(*attribute)
                    .unwrap_or_else(|| snapshot.current().cast_signed())
                    .max(0)
                    .cast_unsigned();
                let kind = i32::try_from(kind).unwrap_or(0);
                Some(Attribute {
                    kind,
                    name: (*name).to_owned(),
                    current: effective,
                    base: snapshot.current(),
                    stat_id: attribute.id(),
                    ranks: snapshot.ranks,
                    experience_spent: u64::from(snapshot.xp),
                })
            })
            .collect()
    }

    /// Health, stamina and mana: now, at full, and at full without
    /// enchantments.
    pub(super) fn character_vitals(&self) -> Vec<Vital> {
        if self.character.is_none() {
            return Vec::new();
        }
        let Some(player) = self.state.world.player() else {
            return Vec::new();
        };
        let numbers = player.numbers();
        VITALS
            .iter()
            .enumerate()
            .filter_map(|(kind, (pool, name))| {
                let snapshot = numbers.get(*pool)?;
                let maximum = numbers.get_max_approx(*pool).unwrap_or(0);
                let kind = u32::try_from(kind).unwrap_or(0);
                Some(Vital {
                    kind: kind.cast_signed(),
                    name: (*name).to_owned(),
                    current: snapshot.current,
                    maximum,
                    stat_id: kind * 2 + 1,
                    base: numbers.get_base_max_approx(*pool).unwrap_or(maximum),
                    ranks: snapshot.ranks,
                    experience_spent: u64::from(snapshot.xp),
                })
            })
            .collect()
    }

    /// The enchantments in effect, or (`timed`) every timed one in the
    /// multiplicative and additive lists, with their seconds left at
    /// session time (infinite for one that does not run out).
    pub(super) fn character_enchantments(&self, timed: bool) -> Vec<ActiveEnchantment> {
        if self.character.is_none() {
            return Vec::new();
        }
        let Some(book) = self.state.world.spellbook() else {
            return Vec::new();
        };
        let now = self.session_seconds();
        let records = if timed {
            book.active_enchantment_snapshot()
                .into_iter()
                .filter(|r| matches!(r.bucket, 1 | 2) && r.duration > 0.0)
                .collect()
        } else {
            book.enchantments_in_effect_snapshot()
        };
        records
            .into_iter()
            .map(|record| {
                let meta = book.try_get_metadata(record.spell_id);
                ActiveEnchantment {
                    spell_id: record.spell_id,
                    family: meta.map_or(0, |m| m.family),
                    tier: meta.map_or(0, |m| m.generation),
                    seconds_remaining: if record.duration < 0.0 {
                        f64::INFINITY
                    } else {
                        (record.start_time + record.duration - now).max(0.0)
                    },
                }
            })
            .collect()
    }

    /// Spend on one stat, checked here so every client refuses the same
    /// requests for the same reasons (OpenAC `RequestAdvancement`): the
    /// cost before the stat, a vital only by its maximum's id.
    pub(super) fn character_advance(&mut self, request: AdvancementRequest) -> AdvancementResult {
        let answer = |status, notice: Option<String>| AdvancementResult { status, notice };
        if !self.in_world() {
            return answer(
                AdvancementStatus::Unavailable,
                Some("the character is not in the world".to_owned()),
            );
        }
        let ceiling = if request.kind == AdvancementKind::TrainSkill {
            MAX_SKILL_CREDITS
        } else {
            MAX_EXPERIENCE_COST
        };
        if request.cost == 0 || request.cost > ceiling {
            return answer(
                AdvancementStatus::InvalidCost,
                Some(format!(
                    "a cost of {} is outside 1..{ceiling}",
                    request.cost
                )),
            );
        }
        let id = request.stat_id;
        let known = id != 0
            && match request.kind {
                AdvancementKind::Attribute => ac_core::player::attribute_id_to_kind(id).is_some(),
                AdvancementKind::Vital => matches!(id, 1 | 3 | 5),
                AdvancementKind::Skill | AdvancementKind::TrainSkill => self
                    .state
                    .world
                    .player()
                    .is_some_and(|p| p.numbers().get_skill(id).is_some()),
            };
        if !known {
            let kind = match request.kind {
                AdvancementKind::Attribute => "attribute",
                AdvancementKind::Vital => "vital",
                AdvancementKind::Skill => "skill",
                AdvancementKind::TrainSkill => "trainskill",
            };
            return answer(
                AdvancementStatus::UnknownStat,
                Some(format!("{id} names no {kind} this character has")),
            );
        }
        let command = match request.kind {
            AdvancementKind::Attribute => CharacterCommand::RaiseAttribute {
                attribute: id,
                xp: request.cost,
            },
            AdvancementKind::Vital => CharacterCommand::RaiseVital {
                vital: id,
                xp: request.cost,
            },
            AdvancementKind::Skill => CharacterCommand::RaiseSkill {
                skill: id,
                xp: request.cost,
            },
            AdvancementKind::TrainSkill => CharacterCommand::TrainSkill {
                skill: id,
                credits: u32::try_from(request.cost).unwrap_or(u32::MAX),
            },
        };
        match self.run(SessionCommand::Character(command)) {
            Ran::Done => answer(AdvancementStatus::Sent, None),
            Ran::Refused(_) => answer(
                AdvancementStatus::Refused,
                Some("the client declined it".to_owned()),
            ),
            Ran::NoConnection => answer(AdvancementStatus::Unavailable, None),
        }
    }

    /// Every character option by its page name, with its value.
    pub(super) fn character_options(&self) -> Vec<(String, bool)> {
        OPTIONS
            .iter()
            .map(|entry| {
                (
                    page_name(entry).to_owned(),
                    self.state.options.get(entry.id),
                )
            })
            .collect()
    }

    /// Set one option by name (its page name or the client's own), the
    /// way the options panel does.
    pub(super) fn character_set_option(&mut self, (name, value): (String, bool)) -> OptionResult {
        let Some(entry) = option_by_plugin_name(&name) else {
            return OptionResult {
                status: OptionStatus::UnknownOption,
                notice: Some(format!("no character option is named '{name}'")),
            };
        };
        if !self.in_world() {
            return OptionResult {
                status: OptionStatus::Unavailable,
                notice: None,
            };
        }
        let command = SessionCommand::SetCharacterOption {
            option: entry.id,
            value,
        };
        match self.run(command) {
            Ran::Done => OptionResult {
                status: OptionStatus::Accepted,
                notice: None,
            },
            Ran::Refused(why) => OptionResult {
                status: OptionStatus::Unavailable,
                notice: Some(why),
            },
            Ran::NoConnection => OptionResult {
                status: OptionStatus::Unavailable,
                notice: None,
            },
        }
    }
}

/// The name a plugin knows an option by: its name on the character options
/// page. The chat-channel options are called "Hear...Chat" there.
fn page_name(entry: &OptionEntry) -> &'static str {
    match entry.id {
        option_id::LISTEN_TO_ALLEGIANCE_CHAT => "HearAllegianceChat",
        option_id::LISTEN_TO_GENERAL_CHAT => "HearGeneralChat",
        option_id::LISTEN_TO_TRADE_CHAT => "HearTradeChat",
        option_id::LISTEN_TO_LFG_CHAT => "HearLFGChat",
        option_id::LISTEN_TO_ROLEPLAY_CHAT => "HearRoleplayChat",
        option_id::LISTEN_TO_SOCIETY_CHAT => "HearSocietyChat",
        _ => entry.name,
    }
}

/// The option a plugin's name stands for: its page name or the client's
/// own spelling, without regard to case (a bare number names none).
fn option_by_plugin_name(name: &str) -> Option<&'static OptionEntry> {
    let name = name.trim();
    OPTIONS
        .iter()
        .find(|entry| page_name(entry).eq_ignore_ascii_case(name))
        .or_else(|| option_by_name(name))
}
