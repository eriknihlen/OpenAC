//! What the combat and magic owners ask of the local body: the stance and
//! motion its interpreter holds (is it ready for a mode change or a
//! swing), and the full stop before a swing or a cast.

use super::Movement;
use crate::session::Outbox;
use crate::world::World;

/// The local body's motion as its interpreter holds it (full commands).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct BodyMotion {
    /// The stance.
    pub current_style: u32,
    /// The forward motion.
    pub forward_command: u32,
    /// Motions are still queued (a stance being taken up).
    pub motions_pending: bool,
}

impl Movement {
    /// The local body's motion; `None` with no body.
    #[must_use]
    pub fn body_motion(&self) -> Option<BodyMotion> {
        let interp = &self.player.as_ref()?.controller.object().interp;
        Some(BodyMotion {
            current_style: interp.interpreted_state.current_style,
            forward_command: interp.interpreted_state.forward_command,
            motions_pending: interp.motions_pending(),
        })
    }

    /// Before a swing or a cast: end autorun, stop the body completely and
    /// tell the server at once where it stopped (else the server works the
    /// swing out from where it still believes the body is). False when
    /// there is no body or the server is moving it.
    pub fn prepare_for_attack_request(&mut self, world: &World, outbox: &mut Outbox) -> bool {
        self.auto_run = false;
        let view = self.bodies_view();
        let Some(player) = self.player.as_mut() else {
            return false;
        };
        let c = &mut player.controller;
        {
            let mut env = super::MovementEnv::new(&mut self.engine, &view);
            if !c.prepare_for_attack_request(&mut env) {
                return false;
            }
        }
        let stamps = Self::timestamps(world, player.guid);
        let movement = c.capture_movement_result(false);
        self.outbound
            .try_send_movement(outbox, c, &movement, &stamps);
        true
    }
}

#[cfg(test)]
#[path = "readiness_tests.rs"]
mod tests;
