//! Combat against the combat owners. Cases from OpenAC
//! `tests/AcDream.Runtime.Tests/Plugins/RuntimeAutomationSurfaceAttackRefusalTests.cs`
//! and `RuntimeAutomationSurfaceCombatPaceTests.cs`.

use ac_plugin::abi::calls;
use ac_plugin::abi::combat::{AttackHeight, AttackRequest, CombatMode, CombatStatus};

use super::events_tests::described;
use super::tests::{PLAYER, call, in_world, with_binding};
use crate::session::{Outbox, SessionState};

const MONSTER: u32 = 0x5000_0010;
const OTHER: u32 = 0x5000_0011;
const CELL: u32 = 0xA9B4_0031;

/// The player and two drudges in one cell (creature type, attackable).
fn arena() -> SessionState {
    let mut state = in_world();
    state.world.apply(&described(
        PLAYER,
        1,
        "+Horan",
        0x10,
        0x8 | 0x10,
        Some(CELL),
    ));
    state
        .world
        .apply(&described(MONSTER, 1, "Drudge", 0x10, 0x10, Some(CELL)));
    state
        .world
        .apply(&described(OTHER, 1, "Drudge", 0x10, 0x10, Some(CELL)));
    state.world.drain_changes().for_each(drop);
    state
}

// OpenAC `BeginPhysicalAttack`: a creature that is not hostile is an
// invalid target; peace is the wrong mode.
#[test]
fn a_swing_needs_a_hostile_creature_and_a_fighting_mode() {
    let mut state = arena();
    let mut outbox = Outbox::new();
    with_binding(&mut state, &mut outbox, |b| {
        let swing = |target_id| AttackRequest {
            target_id,
            height: AttackHeight::High,
            power: 1.0,
        };
        assert_eq!(
            call::<calls::CombatBeginAttack>(b, &swing(PLAYER)).status,
            CombatStatus::InvalidTarget
        );
        assert_eq!(
            call::<calls::CombatBeginAttack>(b, &swing(0x7000_0099)).status,
            CombatStatus::InvalidTarget
        );
        assert_eq!(
            call::<calls::CombatBeginAttack>(b, &swing(MONSTER)).status,
            CombatStatus::WrongMode
        );
        let targets = call::<calls::CombatHostileTargets>(b, &50.0);
        let ids: Vec<u32> = targets.iter().map(|t| t.object_id).collect();
        assert_eq!(ids, [MONSTER, OTHER]);
        assert!(targets.iter().all(|t| !t.is_dead && !t.is_health_known));
        assert!(call::<calls::CombatHostileTargets>(b, &0.0).is_empty());
        assert_eq!(
            call::<calls::CombatEnterMode>(b, &CombatMode::Peace).status,
            CombatStatus::AlreadyReady
        );
        assert_eq!(
            call::<calls::CombatEnterMode>(b, &CombatMode::Unknown).status,
            CombatStatus::Refused
        );
        assert_eq!(
            call::<calls::CombatSnapshot>(b, &()).mode,
            CombatMode::Peace
        );
    });
}

// OpenAC `ARefusedPluginAttackSaysWhy`: each cause has its own words.
#[test]
fn a_refused_attack_says_why() {
    let mut state = arena();
    let mut outbox = Outbox::new();
    with_binding(&mut state, &mut outbox, |b| {
        let select = |b: &mut super::PluginBinding<'_>, id| {
            let command = crate::session::SessionCommand::Gameplay(
                crate::gameplay::GameplayCommand::Select(crate::inventory::ObjectRef::Guid(id)),
            );
            let _ = b.run(command);
        };
        select(b, OTHER);
        let lost = b.describe_attack_refusal(MONSTER);
        b.state
            .world
            .apply(&ac_proto::Inbound::SetState(ac_proto::SetState {
                guid: MONSTER,
                physics_state: 0x0000_0020,
                instance_sequence: 1,
                state_sequence: 2,
            }));
        select(b, MONSTER);
        let unattackable = b.describe_attack_refusal(MONSTER);
        select(b, OTHER);
        let not_ready = b.describe_attack_refusal(OTHER);
        assert!(!lost.trim().is_empty());
        let distinct: std::collections::HashSet<&String> =
            [&lost, &unattackable, &not_ready].into_iter().collect();
        assert_eq!(distinct.len(), 3, "{lost} / {unattackable} / {not_ready}");
    });
}

// OpenAC `AcquireCombatControl`: one plugin at a time; it goes back when
// that plugin lets go or unloads.
#[test]
fn combat_control_is_held_by_one_plugin_until_released() {
    let mut state = arena();
    let mut outbox = Outbox::new();
    with_binding(&mut state, &mut outbox, |b| {
        assert!(b.combat_control("a.one", true));
        assert!(b.state.gameplay.automation_controlled());
        assert!(!b.combat_control("b.two", true));
        assert!(b.combat_control("a.one", true));
        ac_plugin::SessionApi::released(b, "a.one");
        assert!(!b.state.gameplay.automation_controlled());
        assert!(b.combat_control("b.two", true));
        assert!(!b.combat_control("b.two", false));
        assert!(!b.state.gameplay.automation_controlled());
    });
}

// OpenAC `CaptureHostileTargets`: the species name is the creature kind's
// display name from the dats (`CreatureDisplayNameResolver`, empty for an
// unknown kind); the maximum health reads 0, as OpenAC's capture.
#[test]
fn hostile_targets_name_their_species() {
    let mut state = arena();
    let names = ac_core::names::CreatureDisplayNames::new(std::collections::HashMap::from([(
        3,
        "Drudge".to_owned(),
    )]));
    state.install_tables(
        std::sync::Arc::new(crate::gameplay::GameTables::empty().with_creature_names(names)),
        "testaccount2",
    );
    let mut response = ac_proto::appraise::IdentifyObjectResponse {
        guid: MONSTER,
        success: true,
        ..Default::default()
    };
    response
        .properties
        .ints
        .insert(ac_core::properties::PropertyInt::CREATURE_TYPE.0, 3);
    state.world.apply_appraisal(&response, 1.0);
    let mut outbox = Outbox::new();
    with_binding(&mut state, &mut outbox, |b| {
        let targets = call::<calls::CombatHostileTargets>(b, &50.0);
        assert_eq!(
            (targets[0].species_id, targets[0].species_name.as_str()),
            (3, "Drudge")
        );
        assert_eq!(targets[0].maximum_health, 0);
        assert_eq!(
            (targets[1].species_id, targets[1].species_name.as_str()),
            (0, "")
        );
    });
}

// API 1.7: a creature's world object and hostile-target entry carry the
// server's last position beside the place the client has it now. Its
// simulated body standing 3 m east of the server's place, the distance is
// measured to the body and the server's place is the accepted one.
#[test]
fn a_creature_carries_the_servers_last_position_beside_its_body() {
    let mut state = arena();
    let accepted = *state
        .world
        .object(MONSTER)
        .and_then(crate::world::WorldObject::accepted_position)
        .expect("placed");
    let mut body = accepted;
    body.origin.x += 3.0;
    state.world.set_live_position(MONSTER, body);
    let mut outbox = Outbox::new();
    with_binding(&mut state, &mut outbox, |b| {
        let targets = call::<calls::CombatHostileTargets>(b, &50.0);
        let target = targets.iter().find(|t| t.object_id == MONSTER).unwrap();
        let server = super::world::position_of(&accepted);
        assert_eq!(target.server_position.as_ref(), Some(&server));
        assert!((target.distance - 3.0).abs() < 1e-3, "{}", target.distance);
        let object = call::<calls::WorldObject>(b, &MONSTER).unwrap();
        assert_eq!(object.server_position, Some(server));
        assert_eq!(object.position, Some(super::world::position_of(&body)));
        let other = targets.iter().find(|t| t.object_id == OTHER).unwrap();
        assert!(
            other.distance < 1e-3,
            "no body: both are the server's place"
        );
        assert!(other.server_position.is_some());
    });
}

fn creature_profile(
    health: u32,
    health_max: u32,
    strength: Option<u32>,
) -> ac_proto::appraise::CreatureProfile {
    ac_proto::appraise::CreatureProfile {
        // Attributes (0x8) and, with them, enchanted values (0x1).
        flags: if strength.is_some() { 9 } else { 0 },
        health,
        health_max,
        attributes: strength.map(|strength| ac_proto::appraise::CreatureAttributes {
            strength,
            endurance: 40,
            quickness: 45,
            coordination: 50,
            focus: 35,
            self_: 30,
            stamina: 90,
            mana: 25,
            stamina_max: 100,
            mana_max: 60,
        }),
        // Strength raised, health lowered.
        attribute_highlights: strength.map(|_| (0x41, 0x01)),
    }
}

// API 1.9: identifying a creature lets a plugin read its appraisal profile
// from its world properties: health, maximum health, attributes and the
// other vitals, as the assess window shows them.
#[test]
fn an_appraised_creature_profile_reaches_world_properties() {
    let mut state = arena();
    state.world.apply_appraisal(
        &ac_proto::appraise::IdentifyObjectResponse {
            guid: MONSTER,
            success: true,
            creature_profile: Some(creature_profile(120, 200, Some(55))),
            ..Default::default()
        },
        1.0,
    );
    let mut outbox = Outbox::new();
    with_binding(&mut state, &mut outbox, |b| {
        let properties = call::<calls::WorldProperties>(b, &MONSTER).expect("known");
        let creature = properties.creature.expect("appraised");
        assert_eq!((creature.health, creature.max_health), (120, 200));
        let attributes = creature.attributes.expect("assessed");
        assert_eq!(
            (
                attributes.strength,
                attributes.self_,
                attributes.max_stamina
            ),
            (55, 30, 100)
        );
        assert_eq!((attributes.mana, attributes.max_mana), (25, 60));
        // Which values are enchanted, and which way: strength up, health
        // down.
        assert_eq!(
            (creature.attribute_highlights, creature.attribute_colors),
            (0x41, 0x01)
        );
        // The hostile-target capture is as it was: an appraisal does not
        // give it a maximum.
        let targets = call::<calls::CombatHostileTargets>(b, &50.0);
        assert_eq!(targets[0].maximum_health, 0);
    });
}

// The server answers an appraisal of a creature it could not assess with
// the creature's health alone, and the assess window shows it: so does the
// plugin's view, after an earlier full profile.
#[test]
fn a_failed_creature_appraisal_still_reports_health_to_plugins() {
    let mut state = arena();
    state.world.apply_appraisal(
        &ac_proto::appraise::IdentifyObjectResponse {
            guid: MONSTER,
            success: true,
            creature_profile: Some(creature_profile(120, 200, Some(55))),
            ..Default::default()
        },
        1.0,
    );
    state.world.apply_appraisal(
        &ac_proto::appraise::IdentifyObjectResponse {
            guid: MONSTER,
            success: false,
            creature_profile: Some(creature_profile(70, 200, None)),
            ..Default::default()
        },
        2.0,
    );
    let mut outbox = Outbox::new();
    with_binding(&mut state, &mut outbox, |b| {
        let creature = call::<calls::WorldProperties>(b, &MONSTER)
            .and_then(|p| p.creature)
            .expect("health kept");
        assert_eq!((creature.health, creature.max_health), (70, 200));
        assert!(creature.attributes.is_none());
        assert_eq!(
            (creature.attribute_highlights, creature.attribute_colors),
            (0, 0)
        );
    });
}

// An item's appraisal carries no creature profile, and keeps its weapon
// profile as before.
#[test]
fn an_item_appraisal_reports_no_creature_profile() {
    let mut state = arena();
    state
        .world
        .apply(&described(0x5000_0020, 1, "Sword", 0x1, 0, Some(CELL)));
    state.world.apply_appraisal(
        &ac_proto::appraise::IdentifyObjectResponse {
            guid: 0x5000_0020,
            success: true,
            weapon_profile: Some(ac_proto::appraise::WeaponProfile {
                damage_type: 1,
                weapon_time: 30,
                weapon_skill: 45,
                damage: 12,
                damage_variance: 0.5,
                damage_mod: 1.0,
                weapon_length: 1.0,
                max_velocity: 0.0,
                weapon_offense: 1.0,
                max_velocity_estimated: 0,
            }),
            ..Default::default()
        },
        1.0,
    );
    let mut outbox = Outbox::new();
    with_binding(&mut state, &mut outbox, |b| {
        let properties = call::<calls::WorldProperties>(b, &0x5000_0020).expect("known");
        assert!(properties.creature.is_none());
        assert_eq!(properties.weapon.map(|w| w.damage), Some(12));
    });
}
