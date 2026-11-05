//! Planning a combat animation from a motion command the server sent: its
//! combat kind, how the sequencer routes it, the full command and the
//! speed. Combat notifications plan nothing; the game animates combat only
//! from the motion commands.

use ac_core::combat::{CombatAnimationEvent, CombatAnimationKind, classify_motion_command};
use ac_motion::command::reconstruct_full_command;
use ac_motion::{AnimationCommandRouteKind, classify};

/// A combat animation to play.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct CombatAnimationPlan {
    /// The combat kind.
    pub kind: CombatAnimationKind,
    /// How the sequencer takes the command.
    pub route: AnimationCommandRouteKind,
    /// The full motion command.
    pub motion_command: u32,
    /// The speed multiplier.
    pub speed_mod: f32,
}

impl CombatAnimationPlan {
    /// Nothing to play.
    pub const NONE: Self = Self {
        kind: CombatAnimationKind::None,
        route: AnimationCommandRouteKind::None,
        motion_command: 0,
        speed_mod: 0.0,
    };

    /// Whether the plan plays a motion.
    #[must_use]
    pub fn has_motion(&self) -> bool {
        self.kind != CombatAnimationKind::None && self.motion_command != 0
    }
}

/// The plan for a combat notification: none.
#[must_use]
pub fn plan_for_event(_event: CombatAnimationEvent) -> CombatAnimationPlan {
    CombatAnimationPlan::NONE
}

/// The plan for a 16-bit wire command (the server's numbering).
#[must_use]
pub fn plan_from_wire_command(wire_command: u16, speed_mod: f32) -> CombatAnimationPlan {
    plan_from_full_command(reconstruct_full_command(wire_command), speed_mod)
}

/// The plan for a full motion command; [`CombatAnimationPlan::NONE`] when
/// it is not a combat animation.
#[must_use]
pub fn plan_from_full_command(full_command: u32, speed_mod: f32) -> CombatAnimationPlan {
    let kind = classify_motion_command(full_command);
    if kind == CombatAnimationKind::None {
        return CombatAnimationPlan::NONE;
    }
    CombatAnimationPlan {
        kind,
        route: classify(full_command),
        motion_command: full_command,
        speed_mod,
    }
}

#[cfg(test)]
mod tests {
    //! OpenAC `tests/AcDream.Core.Tests/Combat/CombatAnimationPlannerTests.cs`
    //! (the classification cases are `ac_core::combat`'s tests).

    use super::*;

    #[test]
    fn wire_commands_use_the_named_late_combat_commands() {
        for (wire, full) in [
            (0x0170u16, 0x0900_0170u32),
            (0x017D, 0x1000_017D),
            (0x018B, 0x1000_018B),
            (0x018E, 0x1000_018E),
        ] {
            assert_eq!(reconstruct_full_command(wire), full, "{wire:#06X}");
        }
    }

    #[test]
    fn the_late_combat_block_uses_the_servers_numbering() {
        use CombatAnimationKind::{CreatureAttack, MeleeSwing};
        for (wire, full, kind) in [
            (0x0173u16, 0x1000_0173u32, MeleeSwing),
            (0x0175, 0x1000_0175, MeleeSwing),
            (0x0176, 0x1000_0176, MeleeSwing),
            (0x017E, 0x1000_017E, MeleeSwing),
            (0x0182, 0x1000_0182, MeleeSwing),
            (0x0185, 0x1000_0185, MeleeSwing),
            (0x0186, 0x1000_0186, CreatureAttack),
            (0x0188, 0x1000_0188, CreatureAttack),
            (0x018C, 0x1000_018C, CreatureAttack),
            (0x018E, 0x1000_018E, CreatureAttack),
            (0x018F, 0x1000_018F, MeleeSwing),
            (0x0191, 0x1000_0191, MeleeSwing),
            (0x0197, 0x1000_0197, MeleeSwing),
            (0x019A, 0x1000_019A, MeleeSwing),
        ] {
            let plan = plan_from_wire_command(wire, 1.0);
            assert_eq!(plan.motion_command, full, "{wire:#06X}");
            assert_eq!(plan.kind, kind, "{wire:#06X}");
        }
    }

    #[test]
    fn a_reload_is_a_substate() {
        let plan = plan_from_wire_command(0x0016, 1.0);
        assert_eq!(plan.motion_command, 0x4000_0016);
        assert_eq!(plan.kind, CombatAnimationKind::MissileAttack);
        assert_eq!(plan.route, AnimationCommandRouteKind::SubState);
    }

    #[test]
    fn a_swing_is_an_action_overlay_at_its_speed() {
        let plan = plan_from_wire_command(0x0058, 1.25);
        assert_eq!(plan.kind, CombatAnimationKind::MeleeSwing);
        assert_eq!(plan.route, AnimationCommandRouteKind::Action);
        assert_eq!(plan.motion_command, 0x1000_0058);
        assert_eq!(plan.speed_mod, 1.25);
        assert!(plan.has_motion());
    }

    #[test]
    fn dead_is_a_persistent_substate() {
        let plan = plan_from_wire_command(0x0011, 1.0);
        assert_eq!(plan.kind, CombatAnimationKind::Death);
        assert_eq!(plan.route, AnimationCommandRouteKind::SubState);
        assert_eq!(plan.motion_command, 0x4000_0011);
    }

    #[test]
    fn an_unknown_command_plans_nothing() {
        let plan = plan_from_wire_command(0xFFFF, 1.0);
        assert_eq!(plan, CombatAnimationPlan::NONE);
        assert!(!plan.has_motion());
    }

    #[test]
    fn notifications_do_not_invent_animations() {
        use CombatAnimationEvent::*;
        for event in [
            CombatCommenceAttack,
            AttackDone,
            AttackerNotification,
            DefenderNotification,
            EvasionAttackerNotification,
            EvasionDefenderNotification,
            VictimNotification,
            KillerNotification,
        ] {
            assert_eq!(plan_for_event(event), CombatAnimationPlan::NONE);
        }
    }
}
