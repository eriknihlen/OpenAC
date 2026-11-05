//! The spell table: every spell's record by id.
//!
//! The runtime builds it from the portal dat's spell table (see
//! [`project_spell`](super::project_spell)). A comma-separated export with a
//! header row can also be loaded, for research fixtures and tests.

use std::collections::HashMap;

use super::metadata::{MagicSchool, SpellFlags, SpellMetadata, SpellType};
use crate::text::{parse_float_f32, parse_integer_u32};

/// Two records with the same spell id were given to [`SpellTable::create`].
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct DuplicateSpellId(pub u32);

impl std::fmt::Display for DuplicateSpellId {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "Duplicate spell id 0x{:08X}.", self.0)
    }
}

impl std::error::Error for DuplicateSpellId {}

/// Spell records by id.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct SpellTable {
    by_id: HashMap<u32, SpellMetadata>,
}

impl SpellTable {
    /// A table with no spells.
    #[must_use]
    pub fn empty() -> Self {
        Self::default()
    }

    /// A table of these records. A repeated spell id is an error.
    ///
    /// # Errors
    /// [`DuplicateSpellId`] for the first id given twice.
    pub fn create(
        metadata: impl IntoIterator<Item = SpellMetadata>,
    ) -> Result<Self, DuplicateSpellId> {
        let mut by_id = HashMap::new();
        for spell in metadata {
            let id = spell.spell_id;
            if by_id.insert(id, spell).is_some() {
                return Err(DuplicateSpellId(id));
            }
        }
        Ok(Self { by_id })
    }

    /// Number of spells.
    #[must_use]
    pub fn len(&self) -> usize {
        self.by_id.len()
    }

    /// True when there are no spells.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.by_id.is_empty()
    }

    /// The record for a spell.
    #[must_use]
    pub fn get(&self, spell_id: u32) -> Option<&SpellMetadata> {
        self.by_id.get(&spell_id)
    }

    /// Every spell id, in no particular order.
    pub fn spell_ids(&self) -> impl Iterator<Item = u32> + '_ {
        self.by_id.keys().copied()
    }

    /// Loads a comma-separated export whose first line names the columns
    /// (matched without regard to case). Without a "Spell ID" or "Name"
    /// column the table is empty. Blank lines and rows whose spell id does
    /// not parse are skipped; a later row with the same id replaces an
    /// earlier one; a missing or unparsable field reads as zero, false or
    /// empty.
    #[must_use]
    pub fn load_from_csv(text: &str) -> Self {
        let mut by_id = HashMap::new();
        let mut lines = split_lines(text);
        let Some(header) = lines.next() else {
            return Self { by_id };
        };

        let columns = parse_row(header);
        let column = |name: &str| -> Option<usize> {
            // A later duplicate header wins, as a dictionary assignment does.
            columns
                .iter()
                .rposition(|candidate| candidate.eq_ignore_ascii_case(name))
        };

        let i_spell_id = column("Spell ID");
        let i_name = column("Name");
        let i_school = column("School");
        let i_family = column("Family");
        let i_icon = column("IconId [Hex]");
        let i_words = column("Spell Words");
        let i_duration = column("Duration");
        let i_mana = column("Mana");
        let i_debuff = column("IsDebuff");
        let i_fellowship = column("IsFellowship");
        let i_description = column("Description");
        let i_sort_key = column("SortKey");
        let i_difficulty = column("Difficulty");
        let i_flags = column("Flags [Hex]");
        let i_generation = column("Generation");
        let i_fast_windup = column("IsFastWindup");
        let i_offensive = column("IsOffensive");
        let i_untargeted = column("IsUntargetted");
        let i_speed = column("Speed");
        let i_caster_effect = column("CasterEffect");
        let i_target_effect = column("TargetEffect");
        let i_target_mask = column("TargetMask [Hex]");
        let i_spell_type = column("Type");

        let (Some(i_spell_id), Some(_)) = (i_spell_id, i_name) else {
            return Self { by_id };
        };

        for line in lines {
            if line.trim().is_empty() {
                continue;
            }
            let fields = parse_row(line);
            let Some(spell_id) = fields.get(i_spell_id).and_then(|f| parse_integer_u32(f)) else {
                continue;
            };

            let text = |i: Option<usize>| -> String {
                i.and_then(|i| fields.get(i)).cloned().unwrap_or_default()
            };
            let uint = |i: Option<usize>| -> u32 {
                i.and_then(|i| fields.get(i))
                    .and_then(|f| parse_integer_u32(f))
                    .unwrap_or(0)
            };
            let hex = |i: Option<usize>| -> u32 {
                i.and_then(|i| fields.get(i))
                    .and_then(|f| parse_hex_u32(f))
                    .unwrap_or(0)
            };
            let float = |i: Option<usize>| -> f32 {
                i.and_then(|i| fields.get(i))
                    .and_then(|f| parse_float_f32(f))
                    .unwrap_or(0.0)
            };
            let boolean = |i: Option<usize>| -> bool {
                i.and_then(|i| fields.get(i))
                    .is_some_and(|f| f.eq_ignore_ascii_case("True"))
            };
            // The export's integers are unsigned; a signed field takes the
            // same 32 bits.
            let int = |i: Option<usize>| -> i32 { uint(i).cast_signed() };

            let school = text(i_school);
            let school_id = parse_school(&school);
            let spell = SpellMetadata {
                spell_id,
                name: text(i_name),
                school,
                family: uint(i_family),
                icon_id: hex(i_icon),
                spell_words: text(i_words),
                duration: float(i_duration),
                mana_cost: int(i_mana),
                is_debuff: boolean(i_debuff),
                is_fellowship: boolean(i_fellowship),
                description: text(i_description),
                sort_key: int(i_sort_key),
                difficulty: int(i_difficulty),
                flags: SpellFlags(hex(i_flags)),
                generation: int(i_generation),
                is_fast_windup: boolean(i_fast_windup),
                is_offensive: boolean(i_offensive),
                is_untargeted: boolean(i_untargeted),
                speed: float(i_speed),
                caster_effect: uint(i_caster_effect),
                target_effect: uint(i_target_effect),
                target_mask: hex(i_target_mask),
                spell_type: SpellType(uint(i_spell_type)),
                school_id,
                ..SpellMetadata::default()
            };
            by_id.insert(spell_id, spell);
        }

        Self { by_id }
    }
}

fn parse_school(school: &str) -> MagicSchool {
    match school {
        "War Magic" => MagicSchool::WAR_MAGIC,
        "Life Magic" => MagicSchool::LIFE_MAGIC,
        "Item Enchantment" => MagicSchool::ITEM_ENCHANTMENT,
        "Creature Enchantment" => MagicSchool::CREATURE_ENCHANTMENT,
        "Void Magic" => MagicSchool::VOID_MAGIC,
        _ => MagicSchool::NONE,
    }
}

/// Lines split at `\r\n`, `\n` or a lone `\r`.
fn split_lines(text: &str) -> impl Iterator<Item = &str> {
    let mut rest = Some(text);
    std::iter::from_fn(move || {
        let current = rest?;
        if current.is_empty() {
            rest = None;
            return None;
        }
        match current.find(['\r', '\n']) {
            Some(at) => {
                let skip = if current[at..].starts_with("\r\n") {
                    2
                } else {
                    1
                };
                rest = Some(&current[at + skip..]);
                Some(&current[..at])
            }
            None => {
                rest = None;
                Some(current)
            }
        }
    })
}

/// A hex number with an optional `0x`/`0X` prefix, surrounding white space
/// allowed after the prefix is removed (as the export writes it).
fn parse_hex_u32(field: &str) -> Option<u32> {
    let digits = field
        .strip_prefix("0x")
        .or_else(|| field.strip_prefix("0X"))
        .unwrap_or(field);
    let digits =
        digits.trim_matches(|c: char| matches!(c, '\t' | '\n' | '\u{B}' | '\u{C}' | '\r' | ' '));
    if digits.is_empty() || !digits.bytes().all(|b| b.is_ascii_hexdigit()) {
        return None;
    }
    // Leading zeros do not count toward the eight-digit limit.
    let significant = digits.trim_start_matches('0');
    if significant.len() > 8 {
        return None;
    }
    if significant.is_empty() {
        return Some(0);
    }
    u32::from_str_radix(significant, 16).ok()
}

/// Splits one comma-separated row. A field that starts with `"` runs to
/// the next lone `"`, with `""` standing for one quote; anything after the
/// closing quote up to the comma is dropped.
#[must_use]
pub fn parse_row(row: &str) -> Vec<String> {
    let chars: Vec<char> = row.chars().collect();
    let mut fields = Vec::new();
    let mut i = 0;
    while i < chars.len() {
        let field = if chars[i] == '"' {
            i += 1;
            let mut field = String::new();
            while i < chars.len() {
                if chars[i] == '"' {
                    if chars.get(i + 1) == Some(&'"') {
                        field.push('"');
                        i += 2;
                        continue;
                    }
                    i += 1;
                    break;
                }
                field.push(chars[i]);
                i += 1;
            }
            field
        } else {
            let start = i;
            while i < chars.len() && chars[i] != ',' {
                i += 1;
            }
            chars[start..i].iter().collect()
        };
        fields.push(field);
        if chars.get(i) == Some(&',') {
            i += 1;
        }
    }
    fields
}

#[cfg(test)]
mod tests {
    use super::*;

    // Golden rows and expectations from OpenAC
    // `tests/AcDream.Core.Tests/Spells/SpellTableTests.cs`.
    const HEADER: &str = "Spell ID,Spell ID [Hex],Name,SortKey,IconId [Hex],Difficulty,Duration,Family,Flags [Hex],Generation,IsDebuff,IsFastWindup,IsFellowship,IsIrresistible,IsOffensive,IsUntargetted,Mana,School,Speed,Spell Words,CasterEffect,TargetEffect,TargetMask [Hex],Type,Description,Unknown1,Unknown2,Unknown3,Unknown4,Unknown5,Unknown6,Unknown7,Unknown8,Unknown9,Unknown10";

    #[test]
    fn empty_table_has_zero_entries() {
        let table = SpellTable::empty();
        assert_eq!(table.len(), 0);
        assert!(table.get(1).is_none());
    }

    #[test]
    fn create_rejects_duplicate_spell_ids() {
        let spell = SpellMetadata::new(1, "One", 0);
        assert_eq!(
            SpellTable::create([spell.clone(), spell]),
            Err(DuplicateSpellId(1))
        );
    }

    #[test]
    fn header_only_is_empty() {
        assert!(SpellTable::load_from_csv(HEADER).is_empty());
    }

    #[test]
    fn single_simple_row_has_metadata() {
        let csv = format!(
            "{HEADER}\n1,0x1,Strength Other I,6450,0x600138C,1,1800,1,0x6,1,False,False,False,True,False,False,10,Creature Enchantment,0,Malar Cazael,0,6,0x10,1,Increases Strength.,5,1,1,1,0,0,0,0,0,0"
        );
        let table = SpellTable::load_from_csv(&csv);
        assert_eq!(table.len(), 1);
        let meta = table.get(1).expect("spell 1");
        assert_eq!(meta.name, "Strength Other I");
        assert_eq!(meta.school, "Creature Enchantment");
        assert_eq!(meta.school_id, MagicSchool::CREATURE_ENCHANTMENT);
        assert_eq!(meta.family, 1);
        assert_eq!(meta.icon_id, 0x0600_138C);
        assert_eq!(meta.spell_words, "Malar Cazael");
        assert!((meta.duration - 1800.0).abs() < f32::EPSILON);
        assert_eq!(meta.mana_cost, 10);
        assert!(!meta.is_debuff);
        assert!(!meta.is_fellowship);
        assert_eq!(meta.description, "Increases Strength.");
        assert_eq!(meta.flags, SpellFlags(0x6));
        assert_eq!(meta.target_mask, 0x10);
        assert_eq!(meta.target_effect, 6);
    }

    #[test]
    fn quoted_description_with_commas_parses_intact() {
        let csv = format!(
            "{HEADER}\n2,0x2,Strength Self I,6464,0x600138C,1,1800,1,0x400C,1,False,True,False,True,False,True,15,Creature Enchantment,0.01,Malar Cazael,0,6,0x10,1,\"Increases the caster's Strength by 10 points, lasting 30 minutes.\",0,0,1,2,0,0,0,0,0,0"
        );
        let table = SpellTable::load_from_csv(&csv);
        let meta = table.get(2).expect("spell 2");
        assert_eq!(
            meta.description,
            "Increases the caster's Strength by 10 points, lasting 30 minutes."
        );
        assert_eq!(meta.name, "Strength Self I");
        assert!(meta.is_fast_windup);
        assert!(meta.is_untargeted);
    }

    #[test]
    fn blank_lines_are_skipped() {
        let row = |id: u32| {
            format!(
                "{id},0x{id:X},Test,0,0x0,1,1,1,0x0,1,False,False,False,False,False,False,1,War Magic,0,Words,0,0,0x0,1,Desc,0,0,0,0,0,0,0,0,0,0"
            )
        };
        let csv = format!("{HEADER}\n{}\n\n  \n{}", row(1), row(2));
        assert_eq!(SpellTable::load_from_csv(&csv).len(), 2);
        let crlf = format!("{HEADER}\r\n{}\r\n{}\r\n", row(1), row(2));
        assert_eq!(SpellTable::load_from_csv(&crlf).len(), 2);
    }

    #[test]
    fn bad_spell_id_row_is_skipped() {
        let csv = format!(
            "{HEADER}\nnot_a_uint,0x1,Bad,0,0x0,1,1,1,0x0,1,False,False,False,False,False,False,1,War Magic,0,Words,0,0,0x0,1,Desc,0,0,0,0,0,0,0,0,0,0\n5,0x5,Good,0,0x0,1,1,1,0x0,1,False,False,False,False,False,False,1,War Magic,0,Words,0,0,0x0,1,Desc,0,0,0,0,0,0,0,0,0,0"
        );
        let table = SpellTable::load_from_csv(&csv);
        assert_eq!(table.len(), 1);
        assert!(table.get(5).is_some());
    }

    #[test]
    fn unknown_spell_id_is_absent() {
        assert!(SpellTable::load_from_csv(HEADER).get(99_999).is_none());
    }

    #[test]
    fn parse_row_splits_on_commas() {
        assert_eq!(parse_row("a,b,c"), ["a", "b", "c"]);
    }

    #[test]
    fn parse_row_quoted_field_keeps_comma() {
        assert_eq!(parse_row("a,\"b,c\",d"), ["a", "b,c", "d"]);
    }

    #[test]
    fn parse_row_escaped_double_quote() {
        assert_eq!(parse_row("a,\"b\"\"c\",d"), ["a", "b\"c", "d"]);
    }

    #[test]
    fn hex_fields_take_an_optional_prefix() {
        assert_eq!(parse_hex_u32("0x600138C"), Some(0x0600_138C));
        assert_eq!(parse_hex_u32("ff"), Some(0xFF));
        assert_eq!(parse_hex_u32("000000000001"), Some(1));
        assert_eq!(parse_hex_u32("1FFFFFFFF"), None);
        assert_eq!(parse_hex_u32("+1"), None);
        assert_eq!(parse_hex_u32(""), None);
    }
}
