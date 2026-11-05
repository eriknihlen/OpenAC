//! Routes a received motion command to the sequencer by its class: looping
//! states become cycles in the current stance; actions, modifiers and chat
//! emotes play over the current cycle.

use crate::command::{ACTION_BIT, MODIFIER_BIT, SUBSTATE_BIT, reconstruct_full_command};
use crate::manager::MotionTableError;
use crate::sequencer::AnimationSequencer;

const CLASS_MASK: u32 = 0xFF00_0000;

/// How a command is routed.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AnimationCommandRouteKind {
    /// No command (0).
    None,
    /// A one-shot action.
    Action,
    /// An overlay on the cycle.
    Modifier,
    /// A chat emote (class byte `0x12` or `0x13`).
    ChatEmote,
    /// A looping state.
    SubState,
    /// Anything else (stances, UI commands): not routed.
    Ignored,
}

/// The route for a full command.
#[must_use]
pub fn classify(full_command: u32) -> AnimationCommandRouteKind {
    if full_command == 0 {
        return AnimationCommandRouteKind::None;
    }
    let class = full_command & CLASS_MASK;
    if class == 0x1200_0000 || class == 0x1300_0000 {
        AnimationCommandRouteKind::ChatEmote
    } else if full_command & MODIFIER_BIT != 0 {
        AnimationCommandRouteKind::Modifier
    } else if full_command & ACTION_BIT != 0 {
        AnimationCommandRouteKind::Action
    } else if full_command & SUBSTATE_BIT != 0 {
        AnimationCommandRouteKind::SubState
    } else {
        AnimationCommandRouteKind::Ignored
    }
}

/// Restore a 16-bit wire command and route it.
pub fn route_wire_command(
    sequencer: &mut AnimationSequencer,
    current_style: u32,
    wire_command: u16,
    speed_mod: f32,
) -> (AnimationCommandRouteKind, Result<(), MotionTableError>) {
    route_full_command(
        sequencer,
        current_style,
        reconstruct_full_command(wire_command),
        speed_mod,
    )
}

/// Route a full command: looping states through
/// [`AnimationSequencer::set_cycle`], actions, modifiers and emotes through
/// [`AnimationSequencer::play_action`]. Returns the route and the
/// sequencer's answer (`Ok` for commands that are not routed).
pub fn route_full_command(
    sequencer: &mut AnimationSequencer,
    current_style: u32,
    full_command: u32,
    speed_mod: f32,
) -> (AnimationCommandRouteKind, Result<(), MotionTableError>) {
    let route = classify(full_command);
    let result = match route {
        AnimationCommandRouteKind::Action
        | AnimationCommandRouteKind::Modifier
        | AnimationCommandRouteKind::ChatEmote => sequencer.play_action(full_command, speed_mod),
        AnimationCommandRouteKind::SubState => {
            sequencer.set_cycle(current_style, full_command, speed_mod)
        }
        AnimationCommandRouteKind::None | AnimationCommandRouteKind::Ignored => Ok(()),
    };
    (route, result)
}

#[cfg(test)]
mod tests {
    //! Golden cases from OpenAC `AnimationCommandRouterTests`.
    use std::sync::Arc;

    use glam::{Quat, Vec3};

    use super::*;
    use crate::motion_command::DEAD;
    use crate::motion_table::MotionTableRules;
    use crate::test_support::{MapLoader, TableBuilder, motion_data, uniform_anim};

    const NON_COMBAT: u32 = 0x8000_003D;
    const READY: u32 = 0x4100_0003;

    #[test]
    fn classify_matches_the_retail_classes() {
        for (command, expected) in [
            (0x0000_0000, AnimationCommandRouteKind::None),
            (0x1000_0057, AnimationCommandRouteKind::Action),
            (0x2500_003B, AnimationCommandRouteKind::Modifier),
            (0x1300_0087, AnimationCommandRouteKind::ChatEmote),
            (0x4100_0003, AnimationCommandRouteKind::SubState),
            (0x4000_0011, AnimationCommandRouteKind::SubState),
            (0x8000_003D, AnimationCommandRouteKind::Ignored),
        ] {
            assert_eq!(classify(command), expected, "{command:#x}");
        }
    }

    fn empty_sequencer() -> AnimationSequencer {
        AnimationSequencer::new(
            0,
            0,
            Arc::new(MotionTableRules::new(Arc::new(
                TableBuilder::new(0).build(),
            ))),
            MapLoader::default().shared(),
        )
    }

    fn routable_sequencer() -> AnimationSequencer {
        let table = TableBuilder::new(NON_COMBAT)
            .style_default(NON_COMBAT, READY)
            .cycle(NON_COMBAT, READY, motion_data(0x0300_0001, 30.0))
            .cycle(NON_COMBAT, DEAD, motion_data(0x0300_0001, 30.0))
            .build();
        let mut loader = MapLoader::default();
        loader.add(0x0300_0001, uniform_anim(1, 1, Vec3::ZERO, Quat::IDENTITY));
        AnimationSequencer::new(
            1,
            0,
            Arc::new(MotionTableRules::new(Arc::new(table))),
            loader.shared(),
        )
    }

    #[test]
    fn a_wire_substate_sets_the_cycle() {
        let mut seq = routable_sequencer();
        let (route, result) = route_wire_command(&mut seq, NON_COMBAT, 0x0011, 1.0);
        assert_eq!(route, AnimationCommandRouteKind::SubState);
        assert!(result.is_ok());
        assert_eq!(seq.current_style(), NON_COMBAT);
        assert_eq!(seq.current_motion(), DEAD);
    }

    #[test]
    fn sanctuary_is_an_action_not_the_dead_cycle() {
        let mut seq = empty_sequencer();
        let (route, _) = route_wire_command(&mut seq, NON_COMBAT, 0x0057, 1.0);
        assert_eq!(route, AnimationCommandRouteKind::Action);
        assert_eq!(seq.current_motion(), 0);
    }

    #[test]
    fn wave_is_a_chat_emote() {
        let mut seq = empty_sequencer();
        let (route, _) = route_wire_command(&mut seq, NON_COMBAT, 0x0087, 1.0);
        assert_eq!(route, AnimationCommandRouteKind::ChatEmote);
    }
}
