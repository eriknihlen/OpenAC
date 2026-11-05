//! Everything a plugin only reads, field by field (OpenAC
//! `Scenarios/ReadParityTests.cs`): the character, its skills and
//! attributes, a world object, and the navigation snapshot. Every value is
//! recorded and asserted, and the character read is one the server has
//! really said something about: two sessions both reading zero would agree.

use ac_plugin::abi::calls;
use ac_plugin::abi::character::Training;
use ac_plugin::abi::world::{ObjectClass, WorldObject};

use crate::host_parity::arm::{Arm, Setup, run_with};
use crate::host_parity::scenarios::advancement;
use crate::host_parity::transcript::Transcript;
use crate::host_parity::wire::Description;
use crate::host_parity::world;

/// Melee defence, the skill the server states.
const MELEE_DEFENCE: u32 = 6;
/// Endurance, as the server names it.
const ENDURANCE: u32 = 2;
/// What the server said each pool is at.
const HEALTH_LEFT: u32 = 55;
const STAMINA_LEFT: u32 = 42;
const MANA_LEFT: u32 = 40;
/// How high the server says the character is.
const LEVEL: i32 = 27;
/// What each pool adds up to: the hundred stated as its base, the ranks
/// bought into it, and what endurance or self adds (OpenAC's totals).
const MAX_HEALTH_TOTAL: u32 = 159;
const MAX_STAMINA_TOTAL: u32 = 220;
const MAX_MANA_TOTAL: u32 = 101;
/// The skill's ranks and what was spent on them; the attribute's start and
/// raises.
const SKILL_RANKS: u32 = 37;
const SKILL_EXPERIENCE: u64 = 4242;
const ATTRIBUTE_START: u32 = 100;
const ATTRIBUTE_RANKS: u32 = 11;
/// The level property (`PropertyInt::Level`).
const PROPERTY_LEVEL: u32 = 25;

/// The character's level rides on its description, said on arrival.
fn at_level() -> Setup {
    Setup {
        description: Description {
            ints: vec![(PROPERTY_LEVEL, LEVEL)],
            ..Description::default()
        },
        ..Setup::default()
    }
}

/// The staged world plus one skill, one attribute, the three pools.
fn stage(arm: &mut Arm) {
    advancement::stage(arm);
}

// OpenAC `ReadParityTests.TheCharacterReadsTheSameOnBothClients`.
#[test]
fn the_character_reads_the_same_on_both_clients() {
    run_with(&at_level(), &[], true, |arm, transcript| {
        stage(arm);
        transcript.step("character");
        let character = arm.call::<calls::CharacterInfo>(&()).expect("in the world");
        transcript.record("character", &character);
        assert_eq!(character.id, world::PLAYER, "{}", arm.name());
        assert_eq!(character.name, world::PLAYER_NAME);
        assert_eq!(character.world, world::WORLD_NAME);
        assert_eq!(character.account, world::ACCOUNT_NAME);
        assert_eq!(character.character_index, 0);
        assert_eq!(character.server_population, world::SERVER_POPULATION);
        assert_eq!(character.level, LEVEL);
        // Nothing is carried, so the main pack is empty.
        assert_eq!(character.main_pack_free_slots, 102);
        assert_eq!(
            (character.health.0, character.stamina.0, character.mana.0),
            (HEALTH_LEFT, STAMINA_LEFT, MANA_LEFT)
        );
        assert_eq!(
            (character.health.1, character.stamina.1, character.mana.1),
            (MAX_HEALTH_TOTAL, MAX_STAMINA_TOTAL, MAX_MANA_TOTAL),
            "{}",
            arm.name()
        );
    });
}

// OpenAC `ReadParityTests.SkillsAndAttributesReadTheSameOnBothClients`.
#[test]
fn skills_and_attributes_read_the_same_on_both_clients() {
    run_with(&at_level(), &[], true, |arm, transcript| {
        stage(arm);

        transcript.step("skills");
        let skills = arm.call::<calls::CharacterSkills>(&());
        transcript.record("skills", &skills);

        transcript.step("attributes");
        let attributes = arm.call::<calls::CharacterAttributes>(&());
        transcript.record("attributes", &attributes);
        // The one attribute the server stated: a hundred to start with and
        // eleven raises bought into it.
        let endurance = attributes
            .iter()
            .find(|a| a.stat_id == ENDURANCE)
            .unwrap_or_else(|| panic!("{}: no endurance in {attributes:?}", arm.name()));
        assert_eq!(endurance.base, ATTRIBUTE_START + ATTRIBUTE_RANKS);
        assert_eq!(endurance.current, ATTRIBUTE_START + ATTRIBUTE_RANKS);

        transcript.step("skills/one by id");
        let one = arm.call::<calls::CharacterSkill>(&MELEE_DEFENCE);
        transcript.record("one", &one);
        let one = one.unwrap_or_else(|| panic!("{} cannot find the skill stated", arm.name()));
        assert_eq!(one.skill_id, MELEE_DEFENCE);
        assert_eq!(one.ranks, SKILL_RANKS);
        assert_eq!(one.experience_spent, SKILL_EXPERIENCE);
        assert_eq!(one.training, Training::Trained);

        transcript.step("skills/one nobody stated");
        let none = arm.call::<calls::CharacterSkill>(&9999);
        transcript.record("none", &none);
        assert!(none.is_none(), "{} found a skill nobody stated", arm.name());
    });
}

fn record_object(transcript: &mut Transcript, value: Option<&WorldObject>) {
    transcript.record("found", value.is_some());
    if let Some(value) = value {
        transcript.record_id("id", value.object_id);
        transcript.record("weenieClass", value.weenie_class_id);
        transcript.record("name", &value.name);
        transcript.record("class", value.object_class);
        transcript.record("itemType", value.item_type);
        transcript.record_id("container", value.container_id);
        transcript.record_id("wielder", value.wielder_id);
        transcript.record("isOwned", value.is_owned);
        transcript.record("isLandscape", value.is_landscape);
        transcript.record("hasPosition", value.position.is_some());
    }
}

// OpenAC `ReadParityTests.AWorldObjectReadsTheSameOnBothClients`.
#[test]
fn a_world_object_reads_the_same_on_both_clients() {
    run_with(&at_level(), &[], true, |arm, transcript| {
        stage(arm);

        transcript.step("objects");
        let open = arm.call::<calls::WorldOpenContainer>(&());
        transcript.record("openContainer", open);
        let found = arm.call::<calls::WorldObject>(&world::MONSTER);
        record_object(transcript, found.as_ref());
        let found = found.unwrap_or_else(|| panic!("{} cannot see the creature", arm.name()));
        assert_eq!(found.object_id, world::MONSTER);
        assert_eq!(found.name, "Monster 50000012");
        assert_eq!(found.object_class, ObjectClass::Monster);
        assert_eq!((found.container_id, found.wielder_id), (0, 0));
        assert!(!found.is_owned);
        assert!(
            found.position.is_some(),
            "{} knows the creature but not where it is",
            arm.name()
        );

        transcript.step("objects/unknown");
        let missing = arm.call::<calls::WorldObject>(&0x5000_BEEF);
        record_object(transcript, missing.as_ref());
        assert!(
            missing.is_none(),
            "{} found an object nobody sent",
            arm.name()
        );

        transcript.step("objects/all");
        let mut all = arm.call::<calls::WorldObjects>(&());
        all.sort_by_key(|o| o.object_id);
        transcript.record("count", all.len());
        for captured in &all {
            transcript.record(&format!("{:08X}.name", captured.object_id), &captured.name);
            transcript.record(
                &format!("{:08X}.class", captured.object_id),
                captured.object_class,
            );
        }
        // Everything staged, and nothing else.
        let ids: Vec<u32> = all.iter().map(|o| o.object_id).collect();
        assert_eq!(
            ids,
            [
                world::PLAYER,
                world::HIDDEN_MONSTER,
                world::DEAD_MONSTER,
                world::MONSTER,
                world::SECOND_MONSTER,
                world::BYSTANDER,
            ],
            "{}",
            arm.name()
        );
        let class_of = |id| {
            all.iter()
                .find(|o| o.object_id == id)
                .map(|o| o.object_class)
        };
        assert_eq!(class_of(world::PLAYER), Some(ObjectClass::Player));
        assert_eq!(class_of(world::BYSTANDER), Some(ObjectClass::Npc));
    });
}

// OpenAC `ReadParityTests.TheNavigationSnapshotReadsTheSameOnBothClients`:
// navigation reads the character's body, which OpenAC's harness always
// lays down and which here needs the dats (the human setup and the ground).
#[test]
#[ignore = "needs the retail dats: a body to navigate with"]
fn the_navigation_snapshot_reads_the_same_on_both_clients() {
    let setup = Setup {
        dats: true,
        ..at_level()
    };
    run_with(&setup, &[], true, |arm, transcript| {
        stage(arm);
        let snapshot = arm.call::<calls::NavigationSnapshot>(&());

        transcript.step("navigation");
        transcript.record("isAvailable", snapshot.is_available);
        transcript.record("isPortalSpace", snapshot.is_portal_space);
        transcript.record_id("localObjectId", snapshot.local_object_id);
        transcript.record("isMoving", snapshot.is_moving);
        transcript.record("isAirborne", snapshot.is_airborne);
        transcript.record("confirmedRevision", snapshot.confirmed_position_revision);
        transcript.record_id("cell", snapshot.position.cell_id);
        assert!(snapshot.is_available, "{} has no navigation", arm.name());
        assert_eq!(snapshot.local_object_id, world::PLAYER);
        assert!(!snapshot.is_moving && !snapshot.is_airborne && !snapshot.is_portal_space);
        assert_eq!(snapshot.position.cell_id & 0xFFFF_0000, world::LANDBLOCK);

        transcript.step("navigation/how far off the creatures are");
        for (name, id, metres) in [
            ("monster", world::MONSTER, 3.0),
            ("second", world::SECOND_MONSTER, 7.0),
        ] {
            let object = arm.call::<calls::NavigationObject>(&id);
            transcript.record(&format!("{name}.known"), object.is_some());
            let distance = object
                .map(|o| snapshot.position.horizontal_distance_metres(&o.position))
                .unwrap_or(f64::NAN);
            transcript.record(&format!("{name}.distance"), format!("{distance:.3}"));
            assert!(
                (distance - metres).abs() < 1e-3,
                "{}: {name} is {distance} m off",
                arm.name()
            );
        }
    });
}
