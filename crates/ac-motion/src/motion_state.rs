//! What an object's motion table believes it is doing: the stance, the
//! looping state and its speed, the overlays (modifiers) layered on top,
//! and the one-shot actions still in flight.

/// A motion with its speed factor.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct MotionEntry {
    /// Motion command.
    pub motion: u32,
    /// Speed factor (1.0 is the authored speed; negative plays backwards).
    pub speed_mod: f32,
}

/// The motion-table state of one object.
#[derive(Debug, Clone, PartialEq)]
pub struct MotionState {
    /// Stance (style) command, e.g. non-combat.
    pub style: u32,
    /// Looping state (substate) command, e.g. ready or walk forward.
    pub substate: u32,
    /// Speed factor of the substate.
    pub substate_mod: f32,
    /// Active modifiers, newest first.
    modifiers: Vec<MotionEntry>,
    /// Pending actions, oldest first.
    actions: Vec<MotionEntry>,
}

impl Default for MotionState {
    fn default() -> Self {
        Self {
            style: 0,
            substate: 0,
            substate_mod: 1.0,
            modifiers: Vec::new(),
            actions: Vec::new(),
        }
    }
}

impl MotionState {
    /// Active modifiers, newest first.
    #[must_use]
    pub fn modifiers(&self) -> &[MotionEntry] {
        &self.modifiers
    }

    /// Pending actions, oldest first.
    #[must_use]
    pub fn actions(&self) -> &[MotionEntry] {
        &self.actions
    }

    /// Push a modifier on top without checking for duplicates.
    pub fn add_modifier_no_check(&mut self, motion: u32, speed_mod: f32) {
        self.modifiers.insert(0, MotionEntry { motion, speed_mod });
    }

    /// Push a modifier unless it is already active or is the current
    /// substate. Returns whether it was added.
    pub fn add_modifier(&mut self, motion: u32, speed_mod: f32) -> bool {
        if self.modifiers.iter().any(|m| m.motion == motion) || self.substate == motion {
            return false;
        }
        self.add_modifier_no_check(motion, speed_mod);
        true
    }

    /// Remove the modifier at `index` (0 is the newest); out of range does
    /// nothing.
    pub fn remove_modifier(&mut self, index: usize) {
        if index < self.modifiers.len() {
            self.modifiers.remove(index);
        }
    }

    /// Remove every modifier.
    pub fn clear_modifiers(&mut self) {
        self.modifiers.clear();
    }

    /// Queue an action.
    pub fn add_action(&mut self, motion: u32, speed_mod: f32) {
        self.actions.push(MotionEntry { motion, speed_mod });
    }

    /// Pop the oldest action; 0 when there is none.
    pub fn remove_action_head(&mut self) -> u32 {
        if self.actions.is_empty() {
            0
        } else {
            self.actions.remove(0).motion
        }
    }

    /// Remove every action.
    pub fn clear_actions(&mut self) {
        self.actions.clear();
    }
}

#[cfg(test)]
mod tests {
    //! Golden cases from OpenAC `MotionStateTests`.
    use super::*;

    fn motions(list: &[MotionEntry]) -> Vec<u32> {
        list.iter().map(|m| m.motion).collect()
    }

    #[test]
    fn defaults_match_the_retail_constructor() {
        let ms = MotionState::default();
        assert_eq!((ms.style, ms.substate, ms.substate_mod), (0, 0, 1.0));
        assert!(ms.modifiers().is_empty());
        assert!(ms.actions().is_empty());
    }

    #[test]
    fn modifiers_are_a_push_front_stack() {
        let mut ms = MotionState::default();
        ms.add_modifier_no_check(0x0D, 1.0);
        ms.add_modifier_no_check(0x0F, 1.5);
        assert_eq!(motions(ms.modifiers()), [0x0F, 0x0D]);
    }

    #[test]
    fn actions_are_a_fifo() {
        let mut ms = MotionState::default();
        ms.add_action(0x62, 1.0);
        ms.add_action(0x63, 1.25);
        assert_eq!(motions(ms.actions()), [0x62, 0x63]);
    }

    #[test]
    fn add_modifier_rejects_duplicates_and_the_current_substate() {
        let mut ms = MotionState::default();
        assert!(ms.add_modifier(0x0D, 1.0));
        assert!(!ms.add_modifier(0x0D, 2.0));
        assert_eq!(ms.modifiers().len(), 1);
        assert_eq!(ms.modifiers()[0].speed_mod, 1.0);

        let mut ms = MotionState {
            substate: 0x4500_0005,
            ..MotionState::default()
        };
        assert!(!ms.add_modifier(0x4500_0005, 1.0));
        assert!(ms.modifiers().is_empty());
        ms.add_modifier_no_check(0x4500_0005, 1.0);
        ms.add_modifier_no_check(0x4500_0005, 2.0);
        assert_eq!(ms.modifiers().len(), 2);
    }

    #[test]
    fn remove_modifier_by_position() {
        let mut ms = MotionState::default();
        ms.add_modifier_no_check(0x0D, 1.0);
        ms.add_modifier_no_check(0x0F, 1.5);
        let target = ms
            .modifiers()
            .iter()
            .position(|m| m.motion == 0x0D)
            .unwrap();
        ms.remove_modifier(target);
        assert_eq!(motions(ms.modifiers()), [0x0F]);
    }

    #[test]
    fn remove_action_head_pops_in_order_and_returns_zero_when_empty() {
        let mut ms = MotionState::default();
        ms.add_action(0x62, 1.0);
        ms.add_action(0x63, 1.0);
        assert_eq!(ms.remove_action_head(), 0x62);
        assert_eq!(ms.remove_action_head(), 0x63);
        assert_eq!(ms.remove_action_head(), 0);
        ms.add_action(0x64, 1.0);
        assert_eq!(motions(ms.actions()), [0x64]);
    }

    #[test]
    fn modifier_and_action_chains_are_independent() {
        let mut ms = MotionState::default();
        ms.add_modifier_no_check(0x0D, 1.0);
        ms.add_action(0x62, 1.0);
        ms.clear_modifiers();
        assert!(ms.modifiers().is_empty());
        assert_eq!(ms.actions().len(), 1);
        ms.clear_actions();
        assert!(ms.actions().is_empty());
    }

    #[test]
    fn a_copy_shares_nothing() {
        let mut ms = MotionState {
            style: 0x8000_003D,
            substate: 0x4400_0007,
            substate_mod: 2.85,
            ..MotionState::default()
        };
        ms.add_modifier_no_check(0x0D, 1.5);
        ms.add_action(0x62, 1.0);
        let snap = ms.clone();
        ms.clear_modifiers();
        ms.remove_action_head();
        ms.substate = 0x4100_0003;
        assert_eq!(snap.style, 0x8000_003D);
        assert_eq!(snap.substate, 0x4400_0007);
        assert_eq!(snap.substate_mod, 2.85);
        assert_eq!(motions(snap.modifiers()), [0x0D]);
        assert_eq!(snap.actions().len(), 1);
    }
}
