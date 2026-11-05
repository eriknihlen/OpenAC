//! The motion table's rules: given what an object is doing and a requested
//! motion, which animations to queue (links first, then the new cycle),
//! what velocity and spin the motion carries, and which overlays stay on.
//!
//! Motion classes, by the command's top bits: a stance change (bit 31)
//! leaves the old stance through its default state; a looping state (bit
//! 30) replaces the cycle, going through a transition link when the table
//! has one or through the stance's default state when it does not; an
//! action (bit 28) plays out and returns to the current cycle; a modifier
//! (bit 29) only adds velocity or spin on top of the cycle.

use std::collections::HashMap;
use std::sync::Arc;

use ac_dat::objects::{AnimData, MotionData, MotionTable};

use crate::command::{ACTION_BIT, MODIFIER_BIT, SUBSTATE_BIT};
use crate::frame::{F_EPSILON, vec3_from_dat};
use crate::motion_state::MotionState;
use crate::sequence::Sequence;

const LOW_24: u32 = 0x00FF_FFFF;

/// A motion table with its lookups indexed.
#[derive(Debug, Clone)]
pub struct MotionTableRules {
    table: Arc<MotionTable>,
    style_defaults: HashMap<u32, u32>,
    cycles: HashMap<u32, usize>,
    modifiers: HashMap<u32, usize>,
    links: HashMap<u32, (usize, HashMap<u32, usize>)>,
}

/// True when `a` and `b` are both non-negative or both negative.
#[must_use]
pub fn same_sign(a: f32, b: f32) -> bool {
    (a >= 0.0) == (b >= 0.0)
}

/// Rescale the cyclic tail's rate from `old_speed` to `new_speed`. From a
/// speed of about zero the rate cannot be rescaled; it is only zeroed when
/// the new speed is about zero too.
pub fn change_cycle_speed(sequence: &mut Sequence, old_speed: f32, new_speed: f32) {
    if old_speed.abs() > F_EPSILON {
        sequence.multiply_cyclic_animation_framerate(new_speed / old_speed);
    } else if new_speed.abs() <= F_EPSILON {
        sequence.multiply_cyclic_animation_framerate(0.0);
    }
}

/// Replace the sequence's velocity and spin with `motion`'s, scaled, and
/// append its animations at the scaled rate.
pub fn add_motion(sequence: &mut Sequence, motion: Option<&MotionData>, speed_mod: f32) {
    let Some(motion) = motion else {
        return;
    };
    sequence.set_velocity(vec3_from_dat(motion.velocity) * speed_mod);
    sequence.set_omega(vec3_from_dat(motion.omega) * speed_mod);
    for ad in &motion.anims {
        sequence.append_animation(&AnimData {
            framerate: ad.framerate * speed_mod,
            ..*ad
        });
    }
}

/// Add `motion`'s scaled velocity and spin; animations are untouched.
pub fn combine_motion(sequence: &mut Sequence, motion: Option<&MotionData>, speed_mod: f32) {
    if let Some(motion) = motion {
        sequence.combine_physics(
            vec3_from_dat(motion.velocity) * speed_mod,
            vec3_from_dat(motion.omega) * speed_mod,
        );
    }
}

/// Subtract `motion`'s scaled velocity and spin; animations are untouched.
pub fn subtract_motion(sequence: &mut Sequence, motion: Option<&MotionData>, speed_mod: f32) {
    if let Some(motion) = motion {
        sequence.subtract_physics(
            vec3_from_dat(motion.velocity) * speed_mod,
            vec3_from_dat(motion.omega) * speed_mod,
        );
    }
}

/// Number of animations in an optional motion, as the tick counts use it.
fn num_anims(motion: Option<&MotionData>) -> u32 {
    motion.map_or(0, |m| m.anims.len() as u32)
}

fn index<V>(entries: &[(u32, V)]) -> HashMap<u32, usize> {
    entries
        .iter()
        .enumerate()
        .map(|(i, (key, _))| (*key, i))
        .collect()
}

impl MotionTableRules {
    /// Index `table` for lookups.
    #[must_use]
    pub fn new(table: Arc<MotionTable>) -> Self {
        let style_defaults = table.style_defaults.iter().copied().collect();
        let cycles = index(&table.cycles);
        let modifiers = index(&table.modifiers);
        let links = table
            .links
            .iter()
            .enumerate()
            .map(|(i, (key, targets))| (*key, (i, index(targets))))
            .collect();
        Self {
            table,
            style_defaults,
            cycles,
            modifiers,
            links,
        }
    }

    /// The table being read.
    #[must_use]
    pub fn table(&self) -> &MotionTable {
        &self.table
    }

    /// The default state of `style`; 0 when the table has none.
    #[must_use]
    pub fn style_default(&self, style: u32) -> u32 {
        self.style_defaults.get(&style).copied().unwrap_or(0)
    }

    /// The cycle for `substate` in `style`.
    #[must_use]
    pub fn lookup_cycle(&self, style: u32, substate: u32) -> Option<&MotionData> {
        let key = (style << 16) | (substate & LOW_24);
        self.cycles.get(&key).map(|&i| &self.table.cycles[i].1)
    }

    /// The modifier `mod_key` (low 24 bits of the command), under `style`
    /// or style-independent.
    #[must_use]
    pub fn lookup_modifier(
        &self,
        style: u32,
        mod_key: u32,
        style_specific: bool,
    ) -> Option<&MotionData> {
        let key = if style_specific {
            (style << 16) | mod_key
        } else {
            mod_key
        };
        self.modifiers
            .get(&key)
            .map(|&i| &self.table.modifiers[i].1)
    }

    fn link_entry(&self, outer: u32, to: u32) -> Option<&MotionData> {
        let (i, targets) = self.links.get(&outer)?;
        let j = targets.get(&to)?;
        Some(&self.table.links[*i].1[*j].1)
    }

    /// The transition from `from_substate` to `to_substate` in `from_style`.
    /// When either speed is negative the transition is looked up the other
    /// way round (played backwards), falling back to the link from
    /// `from_substate` to the stance's default; otherwise falling back to
    /// the stance-wide link to `to_substate`.
    #[must_use]
    pub fn get_link(
        &self,
        from_style: u32,
        from_substate: u32,
        from_substate_mod: f32,
        to_substate: u32,
        to_substate_mod: f32,
    ) -> Option<&MotionData> {
        if to_substate_mod < 0.0 || from_substate_mod < 0.0 {
            let reversed_key = (from_style << 16) | (to_substate & LOW_24);
            if let Some(link) = self.link_entry(reversed_key, from_substate) {
                return Some(link);
            }
            let default_substate = self.style_default(from_style);
            let sub_key = (from_style << 16) | (from_substate & LOW_24);
            return self.link_entry(sub_key, default_substate);
        }
        let key = (from_style << 16) | (from_substate & LOW_24);
        if let Some(link) = self.link_entry(key, to_substate) {
            return Some(link);
        }
        self.link_entry(from_style << 16, to_substate)
    }

    /// Whether `candidate` (the data of `candidate_substate`) may start now.
    /// Data flagged as gated (bit 1) may only start from itself or from the
    /// stance's default state.
    #[must_use]
    pub fn is_allowed(
        &self,
        candidate_substate: u32,
        candidate: Option<&MotionData>,
        state: &MotionState,
    ) -> bool {
        let Some(candidate) = candidate else {
            return false;
        };
        if candidate.bitfield & 2 != 0 {
            let substate = state.substate;
            if candidate_substate != substate {
                return self.style_default(state.style) == substate;
            }
        }
        true
    }

    /// Rebuild `sequence` and `state` for `motion` at `speed`. Returns the
    /// number of animation completions the motion waits for, or `None` when
    /// the table cannot do it (nothing is changed then).
    ///
    /// `stop_call` marks a return to the default state that must run even
    /// when the target equals the stance default.
    pub fn get_object_sequence(
        &self,
        motion: u32,
        state: &mut MotionState,
        sequence: &mut Sequence,
        speed: f32,
        stop_call: bool,
    ) -> Option<u32> {
        let style = state.style;
        if style == 0 {
            return None;
        }
        let substate = state.substate;
        if substate == 0 {
            return None;
        }
        let style_default = self.style_default(style);
        let target = motion;
        if target == style_default && !stop_call && substate & MODIFIER_BIT != 0 {
            return Some(0);
        }
        let requested_speed = speed;
        let default_style = self.table.default_style;

        // Stance change.
        if (target as i32) < 0 {
            if style == target {
                return Some(0);
            }
            let current_style_default = self.style_default(style);
            let exit_link = if substate == current_style_default {
                None
            } else {
                self.get_link(
                    style,
                    substate,
                    state.substate_mod,
                    current_style_default,
                    requested_speed,
                )
            };
            let target_style_default = self.style_default(target);
            if self.style_defaults.contains_key(&target)
                && let Some(new_cycle) = self.lookup_cycle(target, target_style_default)
            {
                if new_cycle.bitfield & 1 != 0 {
                    state.clear_modifiers();
                }
                let mut direct_or_hop1 = self.get_link(
                    style,
                    style_default,
                    state.substate_mod,
                    target,
                    requested_speed,
                );
                let mut hop2 = None;
                if direct_or_hop1.is_none() && target != style {
                    // Through the table's default stance.
                    direct_or_hop1 = self.get_link(style, style_default, 1.0, default_style, 1.0);
                    let default_style_default = self.style_default(default_style);
                    hop2 = self.get_link(default_style, default_style_default, 1.0, target, 1.0);
                }

                sequence.clear_physics();
                sequence.remove_cyclic_anims();
                add_motion(sequence, exit_link, requested_speed);
                add_motion(sequence, direct_or_hop1, requested_speed);
                add_motion(sequence, hop2, requested_speed);
                add_motion(sequence, Some(new_cycle), requested_speed);

                state.substate = target_style_default;
                state.style = target;
                state.substate_mod = speed;
                self.re_modify(sequence, state);

                return Some(
                    num_anims(Some(new_cycle))
                        .wrapping_add(num_anims(hop2))
                        .wrapping_add(num_anims(direct_or_hop1))
                        .wrapping_add(num_anims(exit_link))
                        .wrapping_sub(1),
                );
            }
        }

        // Looping state.
        if target & SUBSTATE_BIT != 0 {
            let substate_id = target & LOW_24;
            let cyclic = self
                .lookup_cycle(style, substate_id)
                .or_else(|| self.lookup_cycle(default_style, substate_id));
            if let Some(cyclic) = cyclic
                && self.is_allowed(target, Some(cyclic), state)
            {
                // Same state, same direction: only the speed changes.
                if target == substate
                    && same_sign(requested_speed, state.substate_mod)
                    && sequence.has_anims()
                {
                    change_cycle_speed(sequence, state.substate_mod, requested_speed);
                    subtract_motion(sequence, Some(cyclic), state.substate_mod);
                    combine_motion(sequence, Some(cyclic), requested_speed);
                    state.substate_mod = speed;
                    return Some(0);
                }

                if cyclic.bitfield & 1 != 0 {
                    state.clear_modifiers();
                }

                let direct_link =
                    self.get_link(style, substate, state.substate_mod, target, requested_speed);
                let same_sign_direct =
                    direct_link.is_some() && same_sign(requested_speed, state.substate_mod);
                let mut link_or_hop1 = direct_link;
                let mut hop2 = None;
                if direct_link.is_none() || !same_sign_direct {
                    // Through the stance's default state.
                    let style_default_substate = self.style_default(style);
                    link_or_hop1 = self.get_link(
                        style,
                        substate,
                        state.substate_mod,
                        style_default_substate,
                        1.0,
                    );
                    hop2 =
                        self.get_link(style, style_default_substate, 1.0, target, requested_speed);
                }

                sequence.clear_physics();
                sequence.remove_cyclic_anims();
                if hop2.is_none() {
                    let signed_speed =
                        if state.substate_mod == 0.0 || same_sign(state.substate_mod, speed) {
                            speed
                        } else {
                            -speed
                        };
                    add_motion(sequence, link_or_hop1, signed_speed);
                } else {
                    add_motion(sequence, link_or_hop1, state.substate_mod);
                    add_motion(sequence, hop2, requested_speed);
                }
                add_motion(sequence, Some(cyclic), requested_speed);

                // A substate that was also a modifier stays on as one.
                let old_substate = state.substate;
                if old_substate != target
                    && old_substate & MODIFIER_BIT != 0
                    && self.style_default(style) != target
                {
                    state.add_modifier_no_check(old_substate, state.substate_mod);
                }

                state.substate_mod = speed;
                state.substate = target;
                self.re_modify(sequence, state);

                return Some(
                    num_anims(Some(cyclic))
                        .wrapping_add(num_anims(hop2))
                        .wrapping_add(num_anims(link_or_hop1))
                        .wrapping_sub(1),
                );
            }
        }

        // Action.
        if target & ACTION_BIT != 0
            && let Some(base_cycle) = self.lookup_cycle(style, substate & LOW_24)
        {
            if let Some(direct_link) =
                self.get_link(style, substate, state.substate_mod, target, requested_speed)
            {
                state.add_action(target, requested_speed);
                sequence.clear_physics();
                sequence.remove_cyclic_anims();
                add_motion(sequence, Some(direct_link), requested_speed);
                add_motion(sequence, Some(base_cycle), state.substate_mod);
                self.re_modify(sequence, state);
                return Some(num_anims(Some(direct_link)));
            }

            // Out to the stance's default state, the action, and back.
            let style_default_substate = self.style_default(style);
            if let Some(out_hop) = self.get_link(
                style,
                substate,
                state.substate_mod,
                style_default_substate,
                1.0,
            ) && let Some(action_link) =
                self.get_link(style, style_default_substate, 1.0, target, requested_speed)
                && let Some(base_cycle) = self.lookup_cycle(style, substate & LOW_24)
            {
                let return_hop = self.get_link(
                    style,
                    style_default_substate,
                    1.0,
                    substate,
                    state.substate_mod,
                );

                state.add_action(target, requested_speed);
                sequence.clear_physics();
                sequence.remove_cyclic_anims();
                add_motion(sequence, Some(out_hop), 1.0);
                add_motion(sequence, Some(action_link), requested_speed);
                add_motion(sequence, return_hop, 1.0);
                add_motion(sequence, Some(base_cycle), state.substate_mod);
                self.re_modify(sequence, state);

                return Some(
                    num_anims(Some(out_hop))
                        .wrapping_add(num_anims(Some(action_link)))
                        .wrapping_add(num_anims(return_hop)),
                );
            }
        }

        // Modifier.
        if target & MODIFIER_BIT != 0
            && let Some(base_cycle) = self.lookup_cycle(style, substate & LOW_24)
            && base_cycle.bitfield & 1 == 0
        {
            let mod_key = target & LOW_24;
            let modifier = self
                .lookup_modifier(style, mod_key, true)
                .or_else(|| self.lookup_modifier(style, mod_key, false));
            if modifier.is_some() {
                let mut added = state.add_modifier(target, requested_speed);
                if !added {
                    self.stop_sequence_motion(target, 1.0, state, sequence);
                    added = state.add_modifier(target, requested_speed);
                }
                if added {
                    combine_motion(sequence, modifier, requested_speed);
                    return Some(0);
                }
            }
        }

        None
    }

    /// Replay every active modifier through
    /// [`MotionTableRules::get_object_sequence`] after the cycle changed, so
    /// their velocity and spin sit on top of the new cycle's.
    pub fn re_modify(&self, sequence: &mut Sequence, state: &mut MotionState) {
        let mut remaining = state.modifiers().len();
        while remaining > 0 {
            let Some(head) = state.modifiers().first().copied() else {
                break;
            };
            state.remove_modifier(0);
            remaining -= 1;
            self.get_object_sequence(head.motion, state, sequence, head.speed_mod, false);
        }
    }

    /// Stop `motion`: a current looping state returns to the stance's
    /// default state; an active modifier takes its velocity and spin back
    /// off. Returns the completions to wait for, or `None` when `motion` is
    /// neither.
    pub fn stop_sequence_motion(
        &self,
        motion: u32,
        _speed: f32,
        state: &mut MotionState,
        sequence: &mut Sequence,
    ) -> Option<u32> {
        if motion & SUBSTATE_BIT != 0 && motion == state.substate {
            let style_default_substate = self.style_default(state.style);
            let ticks = self
                .get_object_sequence(style_default_substate, state, sequence, 1.0, true)
                .unwrap_or(0);
            return Some(ticks);
        }

        if motion & MODIFIER_BIT != 0
            && let Some(i) = state.modifiers().iter().position(|m| m.motion == motion)
        {
            let speed_mod = state.modifiers()[i].speed_mod;
            let mod_key = motion & LOW_24;
            let data = self
                .lookup_modifier(state.style, mod_key, true)
                .or_else(|| self.lookup_modifier(state.style, mod_key, false));
            if data.is_some() {
                subtract_motion(sequence, data, speed_mod);
                state.remove_modifier(i);
                return Some(0);
            }
        }

        None
    }

    /// Hard reset to the table's default stance and state: modifiers and
    /// actions dropped, the animation list replaced by the default cycle.
    pub fn set_default_state(
        &self,
        state: &mut MotionState,
        sequence: &mut Sequence,
    ) -> Option<u32> {
        let default_style = self.table.default_style;
        let &default_substate = self.style_defaults.get(&default_style)?;

        state.clear_modifiers();
        state.clear_actions();

        let cyclic = self.lookup_cycle(default_style, default_substate)?;
        state.style = default_style;
        state.substate = default_substate;
        state.substate_mod = 1.0;
        let ticks = num_anims(Some(cyclic)).wrapping_sub(1);

        sequence.clear_physics();
        sequence.clear_animations();
        add_motion(sequence, Some(cyclic), state.substate_mod);
        Some(ticks)
    }

    /// Start `motion` (never a stop call).
    pub fn do_object_motion(
        &self,
        motion: u32,
        state: &mut MotionState,
        sequence: &mut Sequence,
        speed: f32,
    ) -> Option<u32> {
        self.get_object_sequence(motion, state, sequence, speed, false)
    }

    /// Stop `motion`.
    pub fn stop_object_motion(
        &self,
        motion: u32,
        speed: f32,
        state: &mut MotionState,
        sequence: &mut Sequence,
    ) -> Option<u32> {
        self.stop_sequence_motion(motion, speed, state, sequence)
    }

    /// Stop every modifier, then the looping state.
    pub fn stop_object_completely(
        &self,
        state: &mut MotionState,
        sequence: &mut Sequence,
    ) -> Option<u32> {
        let mut any_modifier_stopped = false;
        while let Some(node) = state.modifiers().first().copied() {
            if self
                .stop_sequence_motion(node.motion, node.speed_mod, state, sequence)
                .is_some()
            {
                any_modifier_stopped = true;
            } else {
                // A modifier with no data cannot be taken off; stop trying
                // rather than loop forever.
                break;
            }
        }
        match self.stop_sequence_motion(state.substate, state.substate_mod, state, sequence) {
            Some(ticks) => Some(ticks),
            None => any_modifier_stopped.then_some(0),
        }
    }
}

#[cfg(test)]
mod tests;
