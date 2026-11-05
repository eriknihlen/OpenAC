//! Ported from OpenAC
//! `tests/AcDream.Runtime.Tests/CharGen/RuntimeCharacterCreationStateTests.cs`
//! with its fixture (`RuntimeCharacterCreationStateFixture.cs`). .NET's
//! seeded `Random` is not reproduced; the tests that roll check ranges and
//! invariants over many seeds, as the originals do.

use std::collections::HashMap;

use ac_core::chargen::{
    ChargenEyeStrip, ChargenFaceStrip, ChargenGearOption, ChargenHairStyle, ChargenPosition,
    ChargenSkillCost, ChargenStarterArea, ChargenTemplate,
};
use ac_proto::character::CreatedCharacter;

use super::*;

const ALUVIAN: u32 = 1;
const OLTHOI: u32 = 12;
const IMPOVERISHED: u32 = 90;
const MALE: u32 = 1;
const FEMALE: u32 = 2;
const CUSTOM: u32 = 0;
/// Strength 16, the rest at the floor: 66 points, fully spent.
const PRESET: u32 = 1;
const SKILL_TRAIN_SPECIALIZE: u32 = 1;
const SKILL_PRESET_PRIMARY: u32 = 2;
/// Free and specialized by the reset baseline (normal 0, primary 0).
const SKILL_FREE_SPECIALIZED: u32 = 3;
const SKILL_FREE_TRAINED: u32 = 4;
/// Priced nowhere: never settable.
const SKILL_UNCOSTABLE: u32 = 5;
const SKILL_CUSTOM_NORMAL: u32 = 24;

fn cost(skill_id: u32, normal_cost: i32, primary_cost: i32) -> (u32, ChargenSkillCost) {
    (
        skill_id,
        ChargenSkillCost {
            skill_id,
            normal_cost,
            primary_cost,
        },
    )
}

fn template(name: &str, attributes: [i32; 6], normal: &[u32], primary: &[u32]) -> ChargenTemplate {
    ChargenTemplate {
        name: name.to_owned(),
        attributes: ChargenAttributeValues {
            strength: attributes[0],
            endurance: attributes[1],
            coordination: attributes[2],
            quickness: attributes[3],
            focus: attributes[4],
            self_: attributes[5],
        },
        normal_skills: normal.to_vec(),
        primary_skills: primary.to_vec(),
        ..ChargenTemplate::default()
    }
}

fn gear(name: &str, table: u32, weenie: u32) -> ChargenGearOption {
    ChargenGearOption {
        name: name.to_owned(),
        clothing_table_id: table,
        weenie_default_id: weenie,
    }
}

fn fixture() -> ChargenOptions {
    let starter_areas = vec![
        ChargenStarterArea {
            index: 0,
            name: "Holtburg".to_owned(),
            locations: vec![ChargenPosition {
                cell_id: 1,
                ..ChargenPosition::default()
            }],
        },
        ChargenStarterArea {
            index: 1,
            name: "Yaraq".to_owned(),
            locations: vec![ChargenPosition {
                cell_id: 2,
                ..ChargenPosition::default()
            }],
        },
    ];
    let skill_costs: HashMap<u32, ChargenSkillCost> = [
        cost(SKILL_TRAIN_SPECIALIZE, 4, 12),
        cost(SKILL_PRESET_PRIMARY, 3, 9),
        cost(SKILL_FREE_SPECIALIZED, 0, 0),
        cost(SKILL_FREE_TRAINED, 0, 5),
        cost(SKILL_CUSTOM_NORMAL, 2, 6),
    ]
    .into();
    let male = ChargenGenderOptions {
        gender_key: 1,
        name: "Male".to_owned(),
        scale: 1,
        setup_id: 0x0200_0054,
        hair_colors: vec![100, 101],
        hair_styles: vec![
            ChargenHairStyle {
                icon_id: 1,
                ..ChargenHairStyle::default()
            },
            ChargenHairStyle {
                icon_id: 2,
                bald: true,
                ..ChargenHairStyle::default()
            },
        ],
        eye_colors: vec![200, 201],
        eye_strips: vec![ChargenEyeStrip {
            icon_id: 1,
            bald_icon_id: 2,
            ..ChargenEyeStrip::default()
        }],
        nose_strips: vec![ChargenFaceStrip {
            icon_id: 1,
            ..ChargenFaceStrip::default()
        }],
        mouth_strips: vec![ChargenFaceStrip {
            icon_id: 1,
            ..ChargenFaceStrip::default()
        }],
        headgears: vec![gear("Cap", 1, 300)],
        shirts: vec![gear("Shirt", 2, 301)],
        pants: vec![gear("Pants", 3, 302)],
        footwear: vec![gear("Boots", 4, 303)],
        clothing_colors: vec![400, 401, 402],
        ..ChargenGenderOptions::default()
    };
    let female = ChargenGenderOptions {
        gender_key: 2,
        name: "Female".to_owned(),
        ..male.clone()
    };
    let both: HashMap<i32, ChargenGenderOptions> = [(1, male.clone()), (2, female)].into();
    let aluvian_templates = vec![
        template("Custom", [10; 6], &[SKILL_CUSTOM_NORMAL], &[]),
        template(
            "Preset",
            [16, 10, 10, 10, 10, 10],
            &[SKILL_TRAIN_SPECIALIZE],
            &[SKILL_PRESET_PRIMARY],
        ),
    ];
    let human = |id: u32, name: &str| ChargenHeritageOptions {
        heritage_id: id,
        name: name.to_owned(),
        setup_id: 0x0200_0054,
        attribute_credits: 66,
        skill_credits: 50,
        primary_start_area_indices: vec![0, 1],
        skill_costs_by_skill_id: skill_costs.clone(),
        templates: aluvian_templates.clone(),
        genders_by_key: both.clone(),
        ..ChargenHeritageOptions::default()
    };
    let olthoi = ChargenHeritageOptions {
        heritage_id: OLTHOI,
        name: "Olthoi".to_owned(),
        attribute_credits: 60,
        skill_credits: 0,
        primary_start_area_indices: vec![0],
        templates: vec![
            template("Custom", [10; 6], &[], &[]),
            template("NeverChosen", [20; 6], &[], &[]),
        ],
        genders_by_key: [(1, male.clone())].into(),
        ..ChargenHeritageOptions::default()
    };
    let impoverished = ChargenHeritageOptions {
        heritage_id: IMPOVERISHED,
        name: "Impoverished".to_owned(),
        attribute_credits: 60,
        skill_credits: 5,
        primary_start_area_indices: vec![0],
        skill_costs_by_skill_id: [cost(SKILL_TRAIN_SPECIALIZE, 4, 12)].into(),
        templates: vec![template("Custom", [10; 6], &[], &[])],
        genders_by_key: [(1, male)].into(),
        ..ChargenHeritageOptions::default()
    };
    ChargenOptions {
        starter_areas,
        heritages_by_id: [
            (ALUVIAN, human(ALUVIAN, "Aluvian")),
            (2, human(2, "Gharu'ndim")),
            (3, human(3, "Sho")),
            (4, human(4, "Viamontian")),
            (OLTHOI, olthoi),
            (IMPOVERISHED, impoverished),
        ]
        .into(),
        ..ChargenOptions::default()
    }
}

fn with_random(random: Box<dyn ChargenRandom>) -> CharacterCreation {
    let mut state = CharacterCreation::new(Arc::new(fixture()), random);
    state.begin(Generation(1));
    state
}

fn active() -> CharacterCreation {
    with_random(Box::new(SeededRandom::new(1234)))
}

fn chosen(template_index: u32) -> CharacterCreation {
    let mut state = active();
    assert!(state.try_select_heritage(ALUVIAN));
    assert!(state.try_select_gender(MALE));
    assert!(state.try_select_template(template_index));
    state
}

fn ready_to_finish() -> CharacterCreation {
    let mut state = chosen(PRESET);
    state.try_set_name("Adventurer");
    state
}

fn values(a: [i32; 6]) -> ChargenAttributeValues {
    ChargenAttributeValues {
        strength: a[0],
        endurance: a[1],
        coordination: a[2],
        quickness: a[3],
        focus: a[4],
        self_: a[5],
    }
}

#[derive(Debug)]
struct MaxRandom;

impl ChargenRandom for MaxRandom {
    fn next(&mut self, max: u32) -> u32 {
        max.saturating_sub(1)
    }
}

#[test]
fn begin_starts_with_nothing_chosen() {
    let s = active().snapshot();
    assert!(s.is_active);
    assert_eq!((s.heritage_id, s.gender_key), (0, 0));
    assert_eq!(s.template, TEMPLATE_UNSET);
    assert_eq!(s.start_area, -1);
    assert!(!s.verification_pending);
    assert_eq!(s.name, "");
}

#[test]
fn reset_clears_every_field_and_deactivates() {
    let mut state = active();
    assert!(state.try_select_heritage(ALUVIAN));
    assert!(state.try_select_gender(MALE));
    assert!(state.try_set_name("Someone"));
    state.reset(Generation(2));
    let s = state.snapshot();
    assert!(!s.is_active);
    assert_eq!((s.heritage_id, s.gender_key), (0, 0));
    assert_eq!(s.name, "");
    assert_eq!(s.template, TEMPLATE_UNSET);
    assert!(!state.try_select_heritage(ALUVIAN));
}

#[test]
fn options_install_before_begin_and_not_while_active() {
    let mut state = CharacterCreation::new(Arc::default(), Box::new(SeededRandom::new(1)));
    state.install_options(Arc::new(fixture())).unwrap();
    state.begin(Generation(1));
    assert!(state.try_select_heritage(ALUVIAN));
    assert_eq!(state.snapshot().heritage_id, ALUVIAN);
    assert!(state.install_options(Arc::new(fixture())).is_err());
}

#[test]
fn an_unknown_heritage_or_gender_is_refused_and_gender_needs_a_heritage() {
    let mut state = active();
    assert!(!state.try_select_heritage(0xDEAD));
    assert_eq!(state.snapshot().heritage_id, 0);
    assert!(!state.try_select_gender(MALE));
    state.try_select_heritage(ALUVIAN);
    assert!(!state.try_select_gender(99));
}

#[test]
fn a_heritage_sets_the_budgets_and_rolls_a_primary_town() {
    let mut state = active();
    assert!(state.try_select_heritage(ALUVIAN));
    let s = state.snapshot();
    assert_eq!(s.heritage_id, ALUVIAN);
    assert_eq!(s.total_attribute_credits, 66);
    assert_eq!(s.total_skill_credits, 50);
    assert_eq!(s.attributes.total(), 0);
    assert_eq!(s.remaining_attribute_credits, 66);
    assert!(matches!(s.start_area, 0 | 1));
}

#[test]
fn the_custom_template_leaves_points_unspent_and_trains_its_skill() {
    let state = chosen(CUSTOM);
    let s = state.snapshot();
    assert_eq!(s.template, 0);
    assert_eq!(s.attributes, values([10; 6]));
    assert_eq!(s.remaining_attribute_credits, 6);
    assert_eq!(
        state.skill_level(SKILL_CUSTOM_NORMAL),
        ChargenSkillAdvancementClass::Trained
    );
}

#[test]
fn a_preset_template_trains_normal_and_specializes_primary_skills() {
    let state = chosen(PRESET);
    let s = state.snapshot();
    assert_eq!(s.attributes, values([16, 10, 10, 10, 10, 10]));
    assert_eq!(s.remaining_attribute_credits, 0);
    use ChargenSkillAdvancementClass as C;
    assert_eq!(state.skill_level(SKILL_TRAIN_SPECIALIZE), C::Trained);
    assert_eq!(state.skill_level(SKILL_PRESET_PRIMARY), C::Specialized);
    assert_eq!(state.skill_level(SKILL_FREE_SPECIALIZED), C::Specialized);
    assert_eq!(state.skill_level(SKILL_FREE_TRAINED), C::Trained);
}

#[test]
fn an_olthoi_always_takes_template_zero() {
    let mut state = active();
    state.try_select_heritage(OLTHOI);
    state.try_select_gender(MALE);
    assert!(state.try_select_template(1));
    assert_eq!(state.snapshot().template, 0);
    assert_eq!(state.snapshot().attributes, values([10; 6]));
}

#[test]
fn a_template_the_new_heritage_lacks_becomes_unset() {
    let mut state = chosen(PRESET);
    assert_eq!(state.snapshot().template, 1);
    assert!(state.try_select_heritage(IMPOVERISHED));
    assert_eq!(state.snapshot().template, TEMPLATE_UNSET);
}

#[test]
fn attributes_clamp_to_the_floor_and_ceiling() {
    let mut state = chosen(CUSTOM);
    assert!(state.try_set_attribute(ChargenAttribute::Strength, 5));
    assert_eq!(state.snapshot().attributes.strength, 10);
    assert!(state.try_set_attribute(ChargenAttribute::Strength, 999));
    assert!(state.snapshot().attributes.strength <= ATTRIBUTE_MAX);
}

#[test]
fn raising_one_attribute_takes_points_from_one_above_the_floor() {
    let mut state = chosen(PRESET);
    assert!(state.try_set_attribute(ChargenAttribute::Endurance, 16));
    let s = state.snapshot();
    assert_eq!(s.attributes, values([10, 16, 10, 10, 10, 10]));
    assert_eq!(s.attributes.total(), 66);
    assert_eq!(s.remaining_attribute_credits, 0);
}

#[test]
fn a_locked_attribute_gives_nothing() {
    let mut state = chosen(PRESET);
    assert!(state.try_set_attribute_lock(ChargenAttribute::Strength, true));
    assert!(state.try_set_attribute(ChargenAttribute::Endurance, 16));
    assert_eq!(state.snapshot().attributes.endurance, 10);
    assert_eq!(state.snapshot().attributes.strength, 16);
}

#[test]
fn successive_overspends_take_from_different_attributes() {
    let mut state = chosen(PRESET);
    assert!(state.try_set_attribute(ChargenAttribute::Endurance, 11));
    assert_eq!(state.snapshot().attributes.strength, 15);
    assert_eq!(state.snapshot().attributes.endurance, 11);
    assert!(state.try_set_attribute(ChargenAttribute::Coordination, 11));
    let a = state.snapshot().attributes;
    assert_eq!((a.strength, a.endurance, a.coordination), (15, 10, 11));
}

#[test]
fn the_balance_cursor_wraps_from_self_to_strength() {
    let mut state = chosen(PRESET);
    for attribute in [
        ChargenAttribute::Endurance,
        ChargenAttribute::Coordination,
        ChargenAttribute::Quickness,
        ChargenAttribute::Focus,
    ] {
        assert!(state.try_set_attribute_lock(attribute, true));
    }
    assert!(state.try_set_attribute(ChargenAttribute::Self_, 16));
    let a = state.snapshot().attributes;
    assert_eq!((a.strength, a.self_), (10, 16));
    assert!(state.try_set_attribute(ChargenAttribute::Strength, 11));
    let a = state.snapshot().attributes;
    assert_eq!((a.strength, a.self_), (11, 15));
    assert!(state.try_set_attribute(ChargenAttribute::Endurance, 11));
    let a = state.snapshot().attributes;
    assert_eq!((a.strength, a.self_, a.endurance), (10, 15, 11));
}

#[test]
fn a_skill_nothing_prices_can_never_be_set() {
    let mut state = chosen(CUSTOM);
    assert!(!state.try_specialize_skill(SKILL_UNCOSTABLE));
    assert!(!state.try_train_skill(SKILL_UNCOSTABLE));
    assert!(!state.try_untrain_skill(SKILL_UNCOSTABLE));
    assert_eq!(
        state.skill_level(SKILL_UNCOSTABLE),
        ChargenSkillAdvancementClass::Inactive
    );
}

#[test]
fn specializing_charges_the_specialized_price_not_both() {
    let mut state = chosen(CUSTOM);
    let before = state.snapshot().remaining_skill_credits;
    assert!(state.try_train_skill(SKILL_TRAIN_SPECIALIZE));
    assert_eq!(state.snapshot().remaining_skill_credits, before - 4);
    assert!(state.try_specialize_skill(SKILL_TRAIN_SPECIALIZE));
    assert_eq!(state.snapshot().remaining_skill_credits, before - 12);
    assert!(state.try_untrain_skill(SKILL_TRAIN_SPECIALIZE));
    assert_eq!(state.snapshot().remaining_skill_credits, before);
}

#[test]
fn too_few_credits_refuse_and_leave_the_state_alone() {
    let mut state = active();
    state.try_select_heritage(IMPOVERISHED);
    state.try_select_gender(MALE);
    state.try_select_template(CUSTOM);
    assert_eq!(state.snapshot().remaining_skill_credits, 5);
    assert!(!state.try_specialize_skill(SKILL_TRAIN_SPECIALIZE));
    assert_eq!(state.snapshot().remaining_skill_credits, 5);
    assert_eq!(
        state.skill_level(SKILL_TRAIN_SPECIALIZE),
        ChargenSkillAdvancementClass::Untrained
    );
    assert!(state.try_train_skill(SKILL_TRAIN_SPECIALIZE));
    assert_eq!(state.snapshot().remaining_skill_credits, 1);
    assert!(!state.try_specialize_skill(SKILL_TRAIN_SPECIALIZE));
    assert_eq!(
        state.skill_level(SKILL_TRAIN_SPECIALIZE),
        ChargenSkillAdvancementClass::Trained
    );
}

#[test]
fn the_finish_gate_refuses_each_missing_piece() {
    let mut state = ready_to_finish();
    state.try_set_name("   ");
    let refusal = state.try_begin_finish(0, 11, false).unwrap_err();
    assert!(refusal.no_name);
    assert!(!state.snapshot().verification_pending);

    let mut state = chosen(CUSTOM);
    state.try_set_name("Adventurer");
    assert!(
        state
            .try_begin_finish(0, 11, false)
            .unwrap_err()
            .attribute_credits_unspent
    );
    let finish = state.try_begin_finish(0, 11, true).unwrap();
    assert!(state.snapshot().verification_pending);
    assert_eq!(finish.request.attributes.strength, 10);

    let mut state = ready_to_finish();
    assert!(state.try_begin_finish(0, 11, false).is_ok());
    assert!(
        state
            .try_begin_finish(0, 11, false)
            .unwrap_err()
            .already_pending
    );

    let mut state = active();
    state.try_set_name("Adventurer");
    let refusal = state.try_begin_finish(0, 11, false).unwrap_err();
    assert!(refusal.heritage_or_gender_unset && !refusal.no_name);
    state.try_select_heritage(ALUVIAN);
    assert!(
        state
            .try_begin_finish(0, 11, false)
            .unwrap_err()
            .heritage_or_gender_unset
    );

    let mut state = ready_to_finish();
    assert!(
        state
            .try_begin_finish(11, 11, false)
            .unwrap_err()
            .roster_full
    );
}

#[test]
fn an_accepted_finish_trims_the_name_and_sends_55_skill_slots() {
    let mut state = ready_to_finish();
    state.try_set_name("  Adventurer  ");
    let finish = state.try_begin_finish(2, 11, false).unwrap();
    assert!(state.snapshot().verification_pending);
    let r = &finish.request;
    assert_eq!(r.name, "Adventurer");
    assert_eq!((r.heritage, r.gender, r.template), (ALUVIAN, MALE, PRESET));
    assert_eq!(r.attributes.strength, 16);
    let classes = finish.skill_advancement_classes;
    assert_eq!(classes.len(), 55);
    assert_eq!(classes[SKILL_TRAIN_SPECIALIZE as usize], 2);
    assert_eq!(classes[SKILL_PRESET_PRIMARY as usize], 3);
}

fn pending() -> CharacterCreation {
    let mut state = ready_to_finish();
    state.try_begin_finish(0, 11, false).unwrap();
    state
}

fn answer(code: u32, character: Option<(u32, &str)>) -> CharGenVerificationResponse {
    CharGenVerificationResponse {
        code,
        character: character.map(|(guid, name)| CreatedCharacter {
            guid,
            name: name.to_owned(),
            seconds_greyed_out: 0,
        }),
    }
}

#[test]
fn an_ok_answer_records_the_created_character() {
    let mut state = pending();
    state.apply_creation_response(&answer(1, Some((0x5000_1234, "Adventurer"))));
    let s = state.snapshot();
    assert!(!s.verification_pending);
    assert_eq!(
        s.last_created,
        Some(CreatedIdentity {
            guid: 0x5000_1234,
            name: "Adventurer".to_owned()
        })
    );
    assert!(s.last_rejection.is_none());
}

#[test]
fn every_refusal_code_records_its_name_and_the_attempted_name() {
    for (code, name) in [
        (3, "NameInUse"),
        (4, "NameBanned"),
        (5, "Corrupt"),
        (6, "DatabaseDown"),
        (7, "AdminPrivilegeDenied"),
        (2, "Pending"),
        (0, "Undef"),
    ] {
        let mut state = pending();
        state.apply_creation_response(&answer(code, None));
        let s = state.snapshot();
        let rejection = s.last_rejection.unwrap();
        assert_eq!(rejection.raw_code, code);
        assert_eq!(rejection.reason, name);
        assert_eq!(rejection.attempted_name, "Adventurer");
        assert!(!s.verification_pending);
        assert!(s.last_created.is_none());
    }
}

#[test]
fn a_duplicate_answer_is_ignored_and_a_rejection_can_be_dismissed() {
    let mut state = pending();
    state.apply_creation_response(&answer(3, None));
    assert!(state.snapshot().last_rejection.is_some());
    assert!(state.try_acknowledge_rejection());
    state.apply_creation_response(&answer(3, None));
    assert!(state.snapshot().last_rejection.is_none());
    assert!(!state.try_acknowledge_rejection());
}

#[test]
fn a_random_character_is_human_with_a_gender_over_many_seeds() {
    for seed in 0..200 {
        let mut state = with_random(Box::new(SeededRandom::new(seed)));
        assert!(state.try_randomize_character());
        let s = state.snapshot();
        assert!((1..=4).contains(&s.heritage_id), "seed {seed}");
        assert!(matches!(s.gender_key, 1 | 2));
        assert!(s.heritage_id != OLTHOI && s.heritage_id != IMPOVERISHED);
    }
}

#[test]
fn a_random_character_rolls_look_clothing_template_and_town() {
    let mut state = with_random(Box::new(SeededRandom::new(7)));
    assert!(state.try_randomize_character());
    let s = state.snapshot();
    let a = s.appearance;
    for value in [
        a.hair_style,
        a.eyes_strip,
        a.hair_color,
        a.shirt_style,
        a.trousers_style,
        a.footwear_style,
    ] {
        assert_ne!(value, UNSET);
    }
    assert!(s.template != TEMPLATE_UNSET && s.template != 0);
    assert!(matches!(s.start_area, 0 | 1));
    for shade in [
        a.skin_shade,
        a.hair_shade,
        a.headgear_shade,
        a.shirt_shade,
        a.trousers_shade,
        a.footwear_shade,
    ] {
        assert!((0.0..=1.0).contains(&shade));
    }
}

#[test]
fn the_shade_lattice_reaches_exactly_one_at_the_top_roll() {
    let mut state = with_random(Box::new(MaxRandom));
    assert!(state.try_randomize_character());
    let a = state.snapshot().appearance;
    for shade in [
        a.skin_shade,
        a.hair_shade,
        a.headgear_shade,
        a.shirt_shade,
        a.trousers_shade,
        a.footwear_shade,
    ] {
        assert!((shade - 1.0).abs() < f64::EPSILON);
    }
}

#[test]
fn randomizing_needs_an_active_creation_and_a_heritage_and_gender() {
    let mut state = CharacterCreation::new(Arc::new(fixture()), Box::new(MaxRandom));
    assert!(!state.try_randomize_character());
    let mut state = active();
    assert!(!state.try_randomize_appearance());
    state.try_select_heritage(ALUVIAN);
    assert!(!state.try_randomize_appearance());
    state.try_select_gender(MALE);
    assert!(state.try_randomize_appearance());
    assert_ne!(state.snapshot().appearance.hair_style, UNSET);
}

#[test]
fn clothing_rolls_every_garment_and_single_choices_never_hang() {
    let mut state = active();
    state.try_select_heritage(ALUVIAN);
    state.try_select_gender(MALE);
    assert!(state.try_randomize_clothing());
    let a = state.snapshot().appearance;
    assert!(a.shirt_style != UNSET && a.trousers_style != UNSET && a.footwear_style != UNSET);
    for _ in 0..50 {
        assert!(state.try_randomize_clothing());
    }
    assert_eq!(state.snapshot().appearance.shirt_style, 0);
}

#[test]
fn a_second_appearance_roll_flips_every_two_choice_list() {
    for seed in [1, 999] {
        let mut state = with_random(Box::new(SeededRandom::new(seed)));
        state.try_select_heritage(ALUVIAN);
        state.try_select_gender(MALE);
        assert!(state.try_randomize_appearance());
        let first = state.snapshot().appearance;
        assert!(state.try_randomize_appearance());
        let second = state.snapshot().appearance;
        assert!(first.hair_style <= 1 && first.hair_color <= 1 && first.eye_color <= 1);
        assert_ne!(first.hair_style, second.hair_style);
        assert_ne!(first.hair_color, second.hair_color);
        assert_ne!(first.eye_color, second.eye_color);
    }
}

#[test]
fn a_random_template_leaves_the_points_left_right() {
    // A template applies each attribute through its setter, which
    // recomputes the points left, so a template from the random roll leaves
    // them consistent. OpenAC wrote the attributes without recomputing.
    for seed in 0..50 {
        let mut state = with_random(Box::new(SeededRandom::new(seed)));
        assert!(state.try_randomize_character());
        let s = state.snapshot();
        assert_eq!(
            i64::from(s.remaining_attribute_credits),
            i64::from(s.total_attribute_credits) - s.attributes.total(),
            "seed {seed}"
        );
        assert_eq!(s.remaining_attribute_credits, 0, "the presets spend it all");
    }
}

#[test]
fn a_random_character_clears_the_name() {
    let mut state = active();
    state.try_select_heritage(ALUVIAN);
    state.try_select_gender(FEMALE);
    assert!(state.try_set_name("Bob"));
    assert!(state.try_randomize_character());
    assert_eq!(state.snapshot().name, "");
}
