//! Spell formulas: the component recipe a spell needs, its power level, the
//! target type its last component names, and the per-account shuffle of
//! the taper slots.

/// The cheapest taper; the twelve tapers are this id and the eleven after it.
const LOWEST_TAPER_ID: u32 = 63;
/// The prismatic taper that pads a scarab-only formula.
pub const PRISMATIC_TAPER_ID: u32 = 0xBC;
/// Formulas hold at most eight components.
pub const MAX_FORMULA_COMPONENTS: usize = 8;

/// The power level (1..=10) a power component (scarab) stands for, or 0
/// for any other component.
#[must_use]
pub fn determine_power_level_of_component(component_id: u32) -> u32 {
    match component_id {
        1..=6 => component_id,
        0x6E => 7,
        0x70 => 8,
        0xC0 => 9,
        0xC1 => 10,
        _ => 0,
    }
}

/// The spell level the formula's first component suggests: power levels
/// 1-6 are levels 1-6, 7-8 are levels 6-7 and 9-10 are levels 7-8.
#[must_use]
pub fn inq_spell_level_by_rough_heuristic(components: &[u32]) -> i32 {
    let power = components
        .first()
        .map_or(0, |&first| determine_power_level_of_component(first));
    // `power` is at most 10, so these casts cannot truncate.
    match power {
        0..=6 => power as i32,
        9.. => power as i32 - 2,
        _ => power as i32 - 1,
    }
}

/// The item-type mask of valid targets the formula names, read from its
/// last populated component at index 4 or later (index 7 at most); zero
/// when the formula is shorter than five components.
#[must_use]
pub fn get_targeting_type(components: &[u32]) -> u32 {
    if components.len() <= 4 {
        return 0;
    }
    let mut last = 4;
    while last < 7 && last + 1 < components.len() && components[last + 1] != 0 {
        last += 1;
    }
    get_target_type_from_component_id(components[last])
}

/// The item-type mask a talisman stands for (zero for any other component).
#[must_use]
pub fn get_target_type_from_component_id(component_id: u32) -> u32 {
    match component_id {
        0x31..=0x38 | 0x3C..=0x3E | 0xBE => 0x10,
        0x39 => 0x0008_8B8F,
        0x3B => 0x1001_0000,
        _ => 0,
    }
}

fn is_power_component(component: u32) -> bool {
    matches!(component, 1..=6 | 0x6E | 0x6F | 0x70 | 0xC0 | 0xC1)
}

/// The scarab-only recipe: the formula's power components (up to the first
/// zero, within the first eight) followed by prismatic tapers, one to four
/// by the strongest power level, eight components at most.
#[must_use]
pub fn inq_scarab_only_formula(components: &[u32]) -> Vec<u32> {
    let mut result = Vec::with_capacity(MAX_FORMULA_COMPONENTS);
    let mut strongest_power = 0;
    for &component in components.iter().take(MAX_FORMULA_COMPONENTS) {
        if component == 0 {
            break;
        }
        if !is_power_component(component) {
            continue;
        }
        result.push(component);
        strongest_power = strongest_power.max(determine_power_level_of_component(component));
    }

    let taper_count = match strongest_power {
        1 => 1,
        2 => 2,
        3 | 4 | 7 => 3,
        5 | 6 | 8 | 9 | 10 => 4,
        _ => 0,
    };
    for _ in 0..taper_count {
        if result.len() >= MAX_FORMULA_COMPONENTS {
            break;
        }
        result.push(PRISMATIC_TAPER_ID);
    }
    result
}

/// The account-name hash that seeds the taper shuffle: the name's
/// Windows-1252 bytes, each sign-extended, folded into a 28-bit hash.
#[must_use]
pub fn compute_name_hash(value: &str) -> u32 {
    let mut result: i64 = 0;
    for raw in ac_bits::cp1252::encode(value) {
        let character = i64::from(raw.cast_signed());
        result = character + (result << 4);
        if result & 0xF000_0000 != 0 {
            result = (result ^ ((result & 0xF000_0000) >> 24)) & 0x0FFF_FFFF;
        }
    }
    // The hash keeps the low 32 bits, as the game's unsigned arithmetic does.
    result as u32
}

/// The formula this account casts the spell with: the authored formula
/// (first eight components) with some taper slots replaced by
/// account-specific tapers, by the formula version (1, 2 or 3; any other
/// version leaves the formula as authored). Only taper slots change.
#[must_use]
pub fn customize_for_account(
    components: &[u32],
    formula_version: u32,
    account_name: &str,
) -> Vec<u32> {
    let mut result: Vec<u32> = components
        .iter()
        .copied()
        .take(MAX_FORMULA_COMPONENTS)
        .collect();
    match formula_version {
        1 => randomize_version1(&mut result, account_name),
        2 => randomize_version2(&mut result, account_name),
        3 => randomize_version3(&mut result, account_name),
        _ => {}
    }
    result
}

fn taper(value: u32) -> u32 {
    value % 12 + LOWEST_TAPER_ID
}

fn randomize_version1(c: &mut [u32], account_name: &str) {
    let count = c.iter().filter(|&&value| value != 0).count();
    if count < 5 {
        return;
    }

    let seed = compute_name_hash(account_name) % 0x0013_D573;
    let scarab = c[0];
    let herb_index = if count > 5 { 2 } else { 1 };
    let herb = c[herb_index];
    let powder_index = herb_index + 1 + usize::from(count > 6);
    if powder_index + 1 >= c.len() {
        return;
    }
    let powder = c[powder_index];
    let potion = c[powder_index + 1];
    let talisman_index = powder_index + 2 + usize::from(count > 7);
    if talisman_index >= c.len() {
        return;
    }
    let talisman = c[talisman_index];

    if count > 5 {
        c[1] = taper(
            powder
                .wrapping_add(herb.wrapping_mul(2))
                .wrapping_add(potion)
                .wrapping_add(talisman)
                .wrapping_add(scarab),
        );
    }
    if count > 6 {
        let denominator = scarab.wrapping_add(powder).wrapping_add(potion);
        if let Some(quotient) = seed.checked_div(denominator) {
            c[3] = taper(
                scarab
                    .wrapping_add(herb)
                    .wrapping_add(talisman)
                    .wrapping_add(powder.wrapping_add(potion).wrapping_mul(2))
                    .wrapping_mul(quotient),
            );
        }
    }
    if count > 7 {
        let denominator = talisman.wrapping_add(scarab);
        if let Some(quotient) = seed.checked_div(denominator) {
            c[6] = taper(
                powder
                    .wrapping_add(talisman.wrapping_mul(2))
                    .wrapping_add(potion)
                    .wrapping_add(herb)
                    .wrapping_add(scarab)
                    .wrapping_mul(quotient),
            );
        }
    }
}

fn randomize_version2(c: &mut [u32], account_name: &str) {
    if c.len() < 8 {
        return;
    }
    let seed = compute_name_hash(account_name) % 0x0013_D573;
    let (p1, c4, x, a) = (c[0], c[4], c[5], c[7]);
    c[3] = taper(
        a.wrapping_add(p1.wrapping_mul(3))
            .wrapping_add(c4.wrapping_mul(2).wrapping_mul(x))
            .wrapping_add(c[2])
            .wrapping_add(c[1]),
    );
    let denominator = c[1].wrapping_mul(a).wrapping_add(c4.wrapping_mul(2));
    if let Some(quotient) = seed.checked_div(denominator) {
        c[6] = taper(
            a.wrapping_add(p1.wrapping_mul(3).wrapping_mul(c[2]))
                .wrapping_add(x.wrapping_mul(2))
                .wrapping_add(c4)
                .wrapping_mul(quotient),
        );
    }
}

fn randomize_version3(c: &mut [u32], account_name: &str) {
    if c.len() < 7 {
        return;
    }
    let hash = compute_name_hash(account_name);
    let h0 = (hash % 0x0013_D573).wrapping_add(c[0]) % 12;
    let h1 = (hash % 0x0004_AEFD).wrapping_add(c[1]) % 12;
    let h2 = (hash % 0x0009_6A7F).wrapping_add(c[2]) % 12;
    let h4 = (hash % 0x0010_0A03).wrapping_add(c[4]) % 12;
    let h5 = (hash % 0x000E_B2EF).wrapping_add(c[5]) % 12;
    let h7 = (hash % 0x0012_1E7D).wrapping_add(c.get(7).copied().unwrap_or(0)) % 12;
    // Every h is below 12, so these sums and products cannot overflow.
    c[3] = taper(h0 + h1 + h2 + h4 + h5 + h2 * h5 + h0 * h1 + h7 * (h4 + 1));
    c[6] = taper(
        h0 + h1
            + h2
            + h4
            + hash % 0x0006_5039 % 12
            + h7 * (h4 * (h0 * h1 * h2 * h5 + 7) + 1)
            + h5
            + 5 * h0 * h1
            + 11 * h2 * h5,
    );
}

#[cfg(test)]
mod tests {
    use super::*;

    // Golden values from OpenAC
    // `tests/AcDream.Core.Tests/Spells/RetailSpellFormulaTests.cs`.

    #[test]
    fn rough_heuristic_uses_formula_power_component() {
        for (component, expected) in [
            (1, 1),
            (6, 6),
            (0x6E, 6),
            (0x70, 7),
            (0xC0, 7),
            (0xC1, 8),
            (7, 0),
        ] {
            assert_eq!(
                inq_spell_level_by_rough_heuristic(&[component]),
                expected,
                "component {component:#X}"
            );
        }
        assert_eq!(inq_spell_level_by_rough_heuristic(&[]), 0);
    }

    #[test]
    fn target_component_uses_item_type_mask() {
        for (component, expected) in [
            (0x31, 0x10),
            (0x39, 0x0008_8B8F),
            (0x3B, 0x1001_0000),
            (0xBE, 0x10),
            (0x30, 0),
        ] {
            assert_eq!(get_target_type_from_component_id(component), expected);
        }
    }

    #[test]
    fn targeting_type_uses_target_formula_slots_only() {
        assert_eq!(get_targeting_type(&[1, 2, 3, 4, 5, 0x31]), 0x10);
        assert_eq!(get_targeting_type(&[1, 2, 3, 0x31]), 0);
    }

    #[test]
    fn targeting_type_maps_final_populated_formula_component() {
        assert_eq!(get_targeting_type(&[1, 2, 3, 4, 0x39, 0x31]), 0x10);
        assert_eq!(
            get_targeting_type(&[1, 2, 3, 4, 0x39, 0, 0x31]),
            0x0008_8B8F
        );
    }

    #[test]
    fn customize_for_account_is_deterministic_and_only_changes_tapers() {
        let formula = [6, 63, 10, 64, 20, 30, 65, 40];
        let first = customize_for_account(&formula, 3, "testaccount");
        let second = customize_for_account(&formula, 3, "testaccount");
        assert_eq!(first, second);
        assert_eq!(first[0], formula[0]);
        assert_eq!(first[1], formula[1]);
        assert_eq!(first[2], formula[2]);
        assert!((63..=74).contains(&first[3]));
        assert_eq!(first[4], formula[4]);
        assert_eq!(first[5], formula[5]);
        assert!((63..=74).contains(&first[6]));
        assert_eq!(first[7], formula[7]);
    }

    #[test]
    fn customize_for_account_depends_on_exact_spelling() {
        // OpenAC SpellComponentRequirementServiceTests
        // .PersonalizedFormula_UsesExactCanonicalAccountSpelling.
        let formula = [6, 63, 10, 64, 20, 30, 65, 40];
        assert_ne!(
            customize_for_account(&formula, 3, "CanonicalAccount"),
            customize_for_account(&formula, 3, "canonicalaccount")
        );
    }

    #[test]
    fn unknown_version_keeps_authored_formula() {
        let formula = [6, 63, 10, 64, 20, 30, 65, 40, 99];
        assert_eq!(customize_for_account(&formula, 0, "x"), formula[..8]);
    }

    #[test]
    fn scarab_only_formula_keeps_power_and_appends_taper_count() {
        for (power, tapers) in [(1, 1), (2, 2), (3, 3), (0x6E, 3), (0x70, 4), (0xC1, 4)] {
            let result = inq_scarab_only_formula(&[power, 63, 10, 64]);
            assert_eq!(result[0], power);
            assert_eq!(
                result.iter().filter(|&&c| c == PRISMATIC_TAPER_ID).count(),
                tapers,
                "power {power:#X}"
            );
            assert!(!result.contains(&63));
            assert!(!result.contains(&10));
        }
    }

    #[test]
    fn name_hash_folds_high_nibble() {
        assert_eq!(compute_name_hash(""), 0);
        assert_eq!(compute_name_hash("a"), 0x61);
        assert_eq!(compute_name_hash("ab"), 0x61 * 16 + 0x62);
        // A byte above 0x7F counts as negative (0xE9 is -23), which sets the
        // high nibble and folds: (-23 ^ 0xF0) & 0x0FFFFFFF.
        assert_eq!(compute_name_hash("\u{E9}"), 0x0FFF_FF19);
    }
}
