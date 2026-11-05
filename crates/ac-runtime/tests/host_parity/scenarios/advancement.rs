//! Spending experience through the plugin surface (OpenAC
//! `Scenarios/AdvancementParityTests.cs`): how a stat was bought reads the
//! same, a spend is answered the same, a refused spend is refused for the
//! same reason, and nothing is sent before the world; each answer is held
//! to what really left the arm.

use ac_plugin::abi::calls;
use ac_plugin::abi::character::{
    AdvancementKind, AdvancementRequest, AdvancementStatus, MAX_EXPERIENCE_COST, MAX_SKILL_CREDITS,
};

use crate::host_parity::arm::{Arm, run, run_from_login};
use crate::host_parity::scenarios::item::sent;
use crate::host_parity::transcript::Transcript;
use crate::host_parity::{wire, world};

/// Melee defence.
const SKILL: u32 = 6;
const UNKNOWN_SKILL: u32 = 9999;
/// Endurance, as a request names it.
const ENDURANCE: u32 = 2;
/// Stamina at full, as a request names it.
const MAX_STAMINA: u32 = 3;

/// The character: one skill, one attribute and all three pools, each with
/// ranks bought and experience banked (OpenAC `Stage`).
pub fn stage(arm: &mut Arm) {
    world::stage(arm);
    arm.server.send(&wire::skill_update(1, SKILL, 37, 4242));
    arm.server
        .send(&wire::attribute_update(2, ENDURANCE, 11, 100, 1234));
    arm.server.send(&wire::vital_update(3, 1, 3, 100, 111, 55));
    arm.server
        .send(&wire::vital_update(4, MAX_STAMINA, 9, 100, 777, 42));
    arm.server.send(&wire::vital_update(5, 5, 1, 100, 22, 40));
    arm.sync();
}

fn wire_action(kind: AdvancementKind) -> u32 {
    match kind {
        AdvancementKind::Attribute => 0x0045,
        AdvancementKind::Vital => 0x0044,
        AdvancementKind::Skill => 0x0046,
        AdvancementKind::TrainSkill => 0x0047,
    }
}

fn ask(
    transcript: &mut Transcript,
    arm: &mut Arm,
    what: &str,
    kind: AdvancementKind,
    stat_id: u32,
    cost: u64,
    expected: AdvancementStatus,
) {
    transcript.step(&format!("spend/{what}"));
    let _ = arm.outbound();
    let result = arm.call::<calls::CharacterAdvance>(&AdvancementRequest {
        kind,
        stat_id,
        cost,
    });
    assert_eq!(
        result.status,
        expected,
        "the {} arm answered {:?} to {what}",
        arm.name(),
        result.status
    );
    transcript.record("status", result.status);
    transcript.record(
        "hasNotice",
        result.notice.as_ref().is_some_and(|n| !n.trim().is_empty()),
    );
    let out = arm.outbound();
    let wire_cost = sent(&out, wire_action(kind))
        .first()
        .map(|m| u64::from(m.u32_at(16)));
    transcript.record("onTheWire", wire_cost.is_some());
    transcript.record("wireCost", wire_cost.unwrap_or(0));
    if expected == AdvancementStatus::Sent {
        assert_eq!(wire_cost, Some(cost), "{}: {what}", arm.name());
    } else {
        assert_eq!(wire_cost, None, "{}: {what} was sent anyway", arm.name());
    }
}

// OpenAC `AdvancementParityTests.HowAStatWasBoughtReadsTheSameOnBothClients`.
#[test]
fn how_a_stat_was_bought_reads_the_same_on_both_clients() {
    run(|arm, transcript| {
        stage(arm);

        transcript.step("skill");
        let skill = arm.call::<calls::CharacterSkill>(&SKILL);
        transcript.record("found", skill.is_some());
        let skill = skill.unwrap_or_default();
        transcript.record("ranks", skill.ranks);
        transcript.record("experience", skill.experience_spent);
        assert!(
            skill.ranks == 37 && skill.experience_spent == 4242,
            "the {} arm lost how the skill was bought: {} ranks, {} experience",
            arm.name(),
            skill.ranks,
            skill.experience_spent
        );

        transcript.step("attributes");
        let attributes = arm.call::<calls::CharacterAttributes>(&());
        for a in &attributes {
            transcript.record(&format!("{}.statId", a.kind), a.stat_id);
            transcript.record(&format!("{}.ranks", a.kind), a.ranks);
            transcript.record(&format!("{}.experience", a.kind), a.experience_spent);
        }

        transcript.step("vitals");
        let vitals = arm.call::<calls::CharacterVitals>(&());
        transcript.record("count", vitals.len());
        for v in &vitals {
            transcript.record(&format!("{}.name", v.kind), &v.name);
            transcript.record(&format!("{}.statId", v.kind), v.stat_id);
            transcript.record(&format!("{}.current", v.kind), v.current);
            transcript.record(&format!("{}.maximum", v.kind), v.maximum);
            transcript.record(&format!("{}.base", v.kind), v.base);
            transcript.record(&format!("{}.ranks", v.kind), v.ranks);
            transcript.record(&format!("{}.experience", v.kind), v.experience_spent);
        }

        transcript.step("vitals/one by kind");
        let stamina = vitals.iter().find(|v| v.kind == 1);
        transcript.record("found", stamina.is_some());
        let stamina = stamina.cloned().unwrap_or_default();
        transcript.record("ranks", stamina.ranks);
        transcript.record("experience", stamina.experience_spent);
        assert!(
            vitals.len() == 3 && stamina.ranks == 9 && stamina.experience_spent == 777,
            "the {} arm lost how a pool was bought: {vitals:?}",
            arm.name()
        );
        let endurance = attributes
            .iter()
            .find(|a| a.stat_id == ENDURANCE)
            .expect("endurance");
        assert!(
            endurance.ranks == 11 && endurance.experience_spent == 1234,
            "the {} arm lost how the attribute was bought: {endurance:?}",
            arm.name()
        );

        transcript.step("vitals/unknown kind");
        let missing = vitals.iter().find(|v| v.kind == 7);
        transcript.record("found", missing.is_some());
    });
}

// OpenAC `AdvancementParityTests.AskingToSpendAnswersTheSameOnBothClients`.
#[test]
fn asking_to_spend_answers_the_same_on_both_clients() {
    run(|arm, transcript| {
        stage(arm);
        use AdvancementKind as K;
        use AdvancementStatus as S;
        ask(
            transcript,
            arm,
            "attribute",
            K::Attribute,
            ENDURANCE,
            1500,
            S::Sent,
        );
        ask(
            transcript,
            arm,
            "vital",
            K::Vital,
            MAX_STAMINA,
            900,
            S::Sent,
        );
        ask(transcript, arm, "skill", K::Skill, SKILL, 2500, S::Sent);
        ask(transcript, arm, "train", K::TrainSkill, SKILL, 4, S::Sent);
    });
}

// OpenAC `AdvancementParityTests.ARefusedSpendIsRefusedForTheSameReasonOnBothClients`.
#[test]
fn a_refused_spend_is_refused_for_the_same_reason_on_both_clients() {
    run(|arm, transcript| {
        stage(arm);
        use AdvancementKind as K;
        use AdvancementStatus as S;
        ask(transcript, arm, "no stat", K::Skill, 0, 100, S::UnknownStat);
        ask(
            transcript,
            arm,
            "no such attribute",
            K::Attribute,
            7,
            100,
            S::UnknownStat,
        );
        ask(
            transcript,
            arm,
            "pool that cannot be bought",
            K::Vital,
            2,
            100,
            S::UnknownStat,
        );
        ask(
            transcript,
            arm,
            "no such skill",
            K::Skill,
            UNKNOWN_SKILL,
            100,
            S::UnknownStat,
        );
        ask(
            transcript,
            arm,
            "no cost",
            K::Skill,
            SKILL,
            0,
            S::InvalidCost,
        );
        ask(
            transcript,
            arm,
            "absurd experience",
            K::Skill,
            SKILL,
            MAX_EXPERIENCE_COST + 1,
            S::InvalidCost,
        );
        ask(
            transcript,
            arm,
            "absurd credits",
            K::TrainSkill,
            SKILL,
            MAX_SKILL_CREDITS + 1,
            S::InvalidCost,
        );
    });
}

// OpenAC `AdvancementParityTests.ASpendBeforeTheWorldIsRefusedOnBothClients`.
#[test]
fn a_spend_before_the_world_is_refused_on_both_clients() {
    run_from_login(|arm, transcript| {
        transcript.step("before the world");
        let in_world = arm.call::<calls::CharacterInfo>(&()).is_some();
        transcript.record("isInWorld", in_world);
        assert!(
            !in_world,
            "the {} arm is in the world before it was let in",
            arm.name()
        );
        ask(
            transcript,
            arm,
            "skill",
            AdvancementKind::Skill,
            SKILL,
            2500,
            AdvancementStatus::Unavailable,
        );
    });
}
