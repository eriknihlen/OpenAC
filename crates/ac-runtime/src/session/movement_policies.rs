//! The movement apparatus policies: `observer-movement` teleports the
//! character to a fixed spot and then alternates turning and running for
//! someone to watch; `jump-probe` charges a jump, releases it, presses jump
//! again in the air and reports when the body leaves and meets the ground.
//! Both act on the host's clock ([`Policy::tick`]).

use super::{LocalPlayerWatch, Policy};
use crate::movement::{MovementCommand, MovementEvent, MovementKeys};
use crate::session::runner::{HostEvent, SessionCommand};
use crate::world::transit::PortalKind;

/// The cell `observer-movement` teleports to (a Rynthid landblock).
pub const OBSERVER_CELL: u32 = 0xF682_0033;
/// The admin teleport `observer-movement` sends.
pub const OBSERVER_TELEPORT: &str = "@teleloc 0xF6820033 145.7 49.855 58.010 1 0 0 0";
/// What `observer-movement` says once it arrived.
pub const OBSERVER_LINE: &str = "[openac headless observer active]";
/// Seconds of turning, then of running, in each observer cycle.
pub const OBSERVER_TURN_SECONDS: f64 = 1.25;
/// Seconds of running in each observer cycle.
pub const OBSERVER_RUN_SECONDS: f64 = 4.0;

fn keys(keys: MovementKeys) -> SessionCommand {
    SessionCommand::Movement(MovementCommand::Keys(keys))
}

/// Teleport to [`OBSERVER_CELL`], say [`OBSERVER_LINE`], then turn right
/// for 1.25 s and run forward for 4 s, over and over. Never finishes.
#[derive(Debug, Clone, Default)]
pub struct ObserverMovementPolicy {
    player: LocalPlayerWatch,
    stage: u8,
    last_portal_generation: u64,
    issued_after_portal_generation: u64,
    arrived: bool,
    movement_deadline: f64,
    turning: bool,
    failure: Option<String>,
}

impl ObserverMovementPolicy {
    fn start_movement(&mut self, now: f64) -> SessionCommand {
        self.turning = !self.turning;
        let input = if self.turning {
            MovementKeys {
                turn_right: true,
                ..MovementKeys::default()
            }
        } else {
            // Running is the default pace.
            MovementKeys {
                forward: true,
                ..MovementKeys::default()
            }
        };
        self.movement_deadline = now
            + if self.turning {
                OBSERVER_TURN_SECONDS
            } else {
                OBSERVER_RUN_SECONDS
            };
        self.player.at(keys(input))
    }
}

impl Policy for ObserverMovementPolicy {
    fn observe(&mut self, event: &HostEvent) -> Vec<SessionCommand> {
        self.player.observe(event);
        if let HostEvent::Portal(portal) = event {
            self.last_portal_generation = portal.generation;
            if self.stage == 1
                && portal.generation > self.issued_after_portal_generation
                && portal.kind == PortalKind::Portal
                && portal.completed
            {
                let cell = portal.readiness.destination_cell;
                if cell == OBSERVER_CELL {
                    self.arrived = true;
                } else if self.failure.is_none() {
                    self.failure = Some(format!(
                        "Observer gate completed at 0x{cell:08X}; expected 0x{OBSERVER_CELL:08X}."
                    ));
                }
            }
        }
        Vec::new()
    }

    fn tick(&mut self, now: f64) -> Vec<SessionCommand> {
        if self.failure.is_some() || !self.player.has_local_player() {
            return Vec::new();
        }
        match self.stage {
            0 => {
                self.issued_after_portal_generation = self.last_portal_generation;
                self.stage = 1;
                vec![
                    self.player
                        .at(SessionCommand::Chat(OBSERVER_TELEPORT.to_owned())),
                ]
            }
            1 if self.arrived => {
                self.stage = 2;
                let say = self
                    .player
                    .at(SessionCommand::Say(OBSERVER_LINE.to_owned()));
                vec![say, self.start_movement(now)]
            }
            2 if now >= self.movement_deadline => vec![self.start_movement(now)],
            _ => Vec::new(),
        }
    }

    fn failure(&self) -> Option<&str> {
        self.failure.as_deref()
    }

    fn is_complete(&self) -> bool {
        false
    }
}

/// The jump probe's stages.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum JumpProbeStage {
    /// Waiting for the character.
    #[default]
    WaitForPlayer,
    /// Holding jump.
    Charging,
    /// Released; waiting to leave the ground.
    WaitReleaseAirborne,
    /// Pressed jump again in the air; waiting out a second.
    WaitMidAirPress,
    /// Finished.
    Done,
}

/// Seconds the jump is charged.
pub const JUMP_PROBE_CHARGE_SECONDS: f64 = 0.6;
/// Seconds to wait for the released jump to leave the ground.
pub const JUMP_PROBE_AIRBORNE_TIMEOUT_SECONDS: f64 = 3.0;
/// Seconds the mid-air press is held before the probe ends.
pub const JUMP_PROBE_MID_AIR_SECONDS: f64 = 1.0;
/// The shortest time between two movement status requests, seconds.
const JUMP_PROBE_STATUS_INTERVAL_SECONDS: f64 = 0.05;

/// Charge a jump for 0.6 s, release it, and once the body is airborne
/// press jump again for a second; report each airborne transition. It
/// finishes after the mid-air press, or 3 s after the release when the
/// body never left the ground. The body's state comes from the movement
/// status it asks for each step.
#[derive(Debug, Clone, Default)]
pub struct JumpProbePolicy {
    player: LocalPlayerWatch,
    stage: JumpProbeStage,
    stage_deadline: f64,
    airborne: bool,
    was_airborne: bool,
    jump_held: bool,
    now: f64,
    last_status_request: Option<f64>,
    lines: Vec<String>,
}

impl JumpProbePolicy {
    /// The stage it is in.
    #[must_use]
    pub fn stage(&self) -> JumpProbeStage {
        self.stage
    }

    fn hold_jump(&mut self, held: bool) -> SessionCommand {
        self.jump_held = held;
        self.player.at(keys(MovementKeys {
            jump: held,
            ..MovementKeys::default()
        }))
    }
}

impl Policy for JumpProbePolicy {
    fn observe(&mut self, event: &HostEvent) -> Vec<SessionCommand> {
        self.player.observe(event);
        if let HostEvent::Movement(MovementEvent::Status(Some(status))) = event {
            self.airborne = !status.on_ground;
            if self.airborne != self.was_airborne {
                self.lines.push(format!(
                    "[jump-probe] airborne-transition {} -> {} stage={:?} time={:.3} jump={}",
                    self.was_airborne, self.airborne, self.stage, self.now, self.jump_held
                ));
                self.was_airborne = self.airborne;
            }
        }
        Vec::new()
    }

    fn tick(&mut self, now: f64) -> Vec<SessionCommand> {
        self.now = now;
        let mut out = Vec::new();
        match self.stage {
            JumpProbeStage::WaitForPlayer => {
                if !self.player.has_local_player() {
                    return out;
                }
                self.lines
                    .push("[jump-probe] local player present; charging jump".to_owned());
                out.push(self.hold_jump(true));
                self.stage_deadline = now + JUMP_PROBE_CHARGE_SECONDS;
                self.stage = JumpProbeStage::Charging;
            }
            JumpProbeStage::Charging => {
                if now >= self.stage_deadline {
                    self.lines
                        .push("[jump-probe] releasing jump (fire)".to_owned());
                    out.push(self.hold_jump(false));
                    self.stage_deadline = now + JUMP_PROBE_AIRBORNE_TIMEOUT_SECONDS;
                    self.stage = JumpProbeStage::WaitReleaseAirborne;
                }
            }
            JumpProbeStage::WaitReleaseAirborne => {
                if self.airborne {
                    self.lines.push(format!(
                        "[jump-probe] airborne confirmed; pressing jump mid-air time={now:.3}"
                    ));
                    out.push(self.hold_jump(true));
                    self.stage_deadline = now + JUMP_PROBE_MID_AIR_SECONDS;
                    self.stage = JumpProbeStage::WaitMidAirPress;
                } else if now >= self.stage_deadline {
                    self.lines.push(
                        "[jump-probe] TIMEOUT waiting for airborne after jump fire -- the \
                         released jump never registered as airborne."
                            .to_owned(),
                    );
                    out.push(self.hold_jump(false));
                    self.stage = JumpProbeStage::Done;
                }
            }
            JumpProbeStage::WaitMidAirPress => {
                if now >= self.stage_deadline {
                    self.lines.push(format!(
                        "[jump-probe] probe complete; final airborne={}",
                        self.airborne
                    ));
                    out.push(self.hold_jump(false));
                    self.stage = JumpProbeStage::Done;
                }
            }
            JumpProbeStage::Done => {}
        }
        if !matches!(
            self.stage,
            JumpProbeStage::WaitForPlayer | JumpProbeStage::Done
        ) && self
            .last_status_request
            .is_none_or(|at| now - at >= JUMP_PROBE_STATUS_INTERVAL_SECONDS)
        {
            // The body's state for the next step (asked at most every 50 ms:
            // each answer is an event that steps the policy again).
            self.last_status_request = Some(now);
            out.push(SessionCommand::Movement(MovementCommand::Status));
        }
        out
    }

    fn take_lines(&mut self) -> Vec<String> {
        std::mem::take(&mut self.lines)
    }

    fn is_complete(&self) -> bool {
        self.stage == JumpProbeStage::Done
    }
}

#[cfg(test)]
mod tests {
    //! The `observer-movement` and `jump-probe` stages of OpenAC
    //! `src/AcDream.Headless/Policies/HeadlessBotPolicy.cs`
    //! (`ObserverMovementHeadlessBotPolicy`, `JumpProbeHeadlessBotPolicy`):
    //! the teleport line and cell, the 1.25 s / 4 s cycle, the 0.6 s charge,
    //! the 3 s airborne timeout and the 1 s mid-air press.
    use super::*;
    use crate::movement::MovementStatus;
    use crate::session::generation::{Generation, LifecycleState};
    use crate::session::{Lifecycle, SessionEvent};
    use crate::world::transit::{DestinationReadiness, PortalSnapshot};
    use ac_proto::{Position, Quaternion, Vector3};

    fn enter(policy: &mut dyn Policy) {
        policy.observe(&HostEvent::Lifecycle(Lifecycle {
            generation: Generation(1),
            state: LifecycleState::InWorld,
            player_guid: 5,
            has_transport: true,
        }));
        policy.observe(&HostEvent::Session(SessionEvent::LoginCompleteSent));
    }

    fn inner(command: &SessionCommand) -> &SessionCommand {
        match command {
            SessionCommand::AtGeneration {
                generation: Generation(1),
                command,
            } => command,
            other => panic!("not at the generation: {other:?}"),
        }
    }

    fn held(command: &SessionCommand) -> MovementKeys {
        match inner(command) {
            SessionCommand::Movement(MovementCommand::Keys(k)) => *k,
            other => panic!("not keys: {other:?}"),
        }
    }

    fn portal(generation: u64, kind: PortalKind, completed: bool, cell: u32) -> HostEvent {
        HostEvent::Portal(PortalSnapshot {
            generation,
            kind,
            completed,
            readiness: DestinationReadiness {
                destination_cell: cell,
                ..DestinationReadiness::default()
            },
            ..PortalSnapshot::IDLE
        })
    }

    #[test]
    fn the_observer_teleports_then_alternates_turning_and_running() {
        let mut p = ObserverMovementPolicy::default();
        assert!(p.tick(0.0).is_empty(), "no character yet");
        p.observe(&portal(1, PortalKind::Login, true, 0xA9B4_0001));
        enter(&mut p);
        let sent = p.tick(1.0);
        assert!(matches!(
            inner(&sent[0]),
            SessionCommand::Chat(line) if line == OBSERVER_TELEPORT
        ));
        // The login reveal and an unfinished portal do not count.
        p.observe(&portal(1, PortalKind::Login, true, OBSERVER_CELL));
        p.observe(&portal(2, PortalKind::Portal, false, OBSERVER_CELL));
        assert!(p.tick(2.0).is_empty());
        p.observe(&portal(2, PortalKind::Portal, true, OBSERVER_CELL));
        let sent = p.tick(3.0);
        assert!(matches!(
            inner(&sent[0]),
            SessionCommand::Say(line) if line == OBSERVER_LINE
        ));
        assert!(held(&sent[1]).turn_right);
        assert!(p.tick(4.0).is_empty(), "turning for 1.25 s");
        let run = p.tick(4.25);
        let keys = held(&run[0]);
        assert!(keys.forward && !keys.walk && !keys.turn_right, "running");
        assert!(p.tick(8.0).is_empty(), "running for 4 s");
        assert!(held(&p.tick(8.25)[0]).turn_right);
        assert!(!p.is_complete());
        assert!(p.failure().is_none());
    }

    #[test]
    fn the_observer_fails_when_the_teleport_lands_elsewhere() {
        let mut p = ObserverMovementPolicy::default();
        enter(&mut p);
        p.tick(0.0);
        p.observe(&portal(1, PortalKind::Portal, true, 0xA9B4_0001));
        assert_eq!(
            p.failure(),
            Some("Observer gate completed at 0xA9B40001; expected 0xF6820033.")
        );
        assert!(p.tick(1.0).is_empty());
    }

    fn status(on_ground: bool) -> HostEvent {
        HostEvent::Movement(MovementEvent::Status(Some(MovementStatus {
            position: Position {
                cell_id: 0xA9B4_0001,
                origin: Vector3 {
                    x: 0.0,
                    y: 0.0,
                    z: 0.0,
                },
                orientation: Quaternion::IDENTITY,
            },
            heading_degrees: 0.0,
            on_ground,
            velocity: [0.0; 3],
            resident_landblocks: 9,
            auto_run: false,
        })))
    }

    fn asks_status(sent: &[SessionCommand]) -> bool {
        sent.iter()
            .any(|c| matches!(c, SessionCommand::Movement(MovementCommand::Status)))
    }

    #[test]
    fn the_jump_probe_charges_releases_and_presses_again_in_the_air() {
        let mut p = JumpProbePolicy::default();
        assert!(p.tick(0.0).is_empty());
        enter(&mut p);
        let sent = p.tick(1.0);
        assert!(held(&sent[0]).jump, "charging");
        assert!(asks_status(&sent));
        assert_eq!(p.stage(), JumpProbeStage::Charging);
        assert!(
            !p.tick(1.5)
                .iter()
                .any(|c| matches!(c, SessionCommand::AtGeneration { .. }))
        );
        let sent = p.tick(1.6);
        assert!(!held(&sent[0]).jump, "released at 0.6 s");
        assert_eq!(p.stage(), JumpProbeStage::WaitReleaseAirborne);
        p.observe(&status(true));
        p.tick(1.7);
        p.observe(&status(false));
        let sent = p.tick(1.8);
        assert!(held(&sent[0]).jump, "pressed again mid-air");
        assert_eq!(p.stage(), JumpProbeStage::WaitMidAirPress);
        assert!(
            p.tick(2.7)
                .iter()
                .all(|c| !matches!(c, SessionCommand::AtGeneration { .. }))
        );
        p.observe(&status(true));
        let sent = p.tick(2.8);
        assert!(!held(&sent[0]).jump);
        assert!(p.is_complete());
        assert!(!asks_status(&sent));
        let lines = p.take_lines();
        assert!(
            lines
                .iter()
                .any(|l| l.contains("airborne-transition false -> true"))
        );
        assert!(
            lines
                .iter()
                .any(|l| l.contains("airborne-transition true -> false"))
        );
        assert!(
            lines
                .last()
                .is_some_and(|l| l == "[jump-probe] probe complete; final airborne=false")
        );
    }

    #[test]
    fn the_jump_probe_times_out_when_the_body_never_leaves_the_ground() {
        let mut p = JumpProbePolicy::default();
        enter(&mut p);
        p.tick(0.0);
        p.tick(0.6);
        p.observe(&status(true));
        assert!(
            !p.tick(3.5)
                .iter()
                .any(|c| matches!(c, SessionCommand::AtGeneration { .. }))
        );
        let sent = p.tick(3.6);
        assert!(!held(&sent[0]).jump);
        assert!(p.is_complete());
        assert!(p.take_lines().iter().any(|l| l.contains("TIMEOUT")));
    }
}
