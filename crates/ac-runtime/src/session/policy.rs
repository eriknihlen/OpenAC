//! Bot policies a windowless session can run: they watch what the session
//! reports and answer with commands. `idle` does nothing and never
//! finishes; `lifecycle-smoke` enters the world, speaks, recalls to the
//! lifestone, waits to arrive, reconnects, and finishes once the new
//! generation's character is in the world (the host then logs it off).
//! `observer-movement` and `jump-probe` drive the character's movement on
//! a clock (see [`movement`]).

use super::SessionEvent;
use super::generation::{Generation, LifecycleState};
use super::runner::{HostEvent, LifecycleCommand, Recall, SessionCommand};
use crate::world::transit::PortalKind;

#[path = "movement_policies.rs"]
pub mod movement;

pub use movement::{JumpProbePolicy, ObserverMovementPolicy};

/// A policy: fed every event in order, answers with commands to send.
pub trait Policy: Send + std::fmt::Debug {
    /// React to one event.
    fn observe(&mut self, event: &HostEvent) -> Vec<SessionCommand>;

    /// Once a host step, after the events: `now_seconds` is the host's
    /// clock (seconds since the policy started). Timed stages act here.
    fn tick(&mut self, _now_seconds: f64) -> Vec<SessionCommand> {
        Vec::new()
    }

    /// What the policy reports, for the console, since the last call.
    fn take_lines(&mut self) -> Vec<String> {
        Vec::new()
    }

    /// The policy has finished; the host ends the session.
    fn is_complete(&self) -> bool;

    /// The policy found the session wrong; the host ends the session as a
    /// failure.
    fn failure(&self) -> Option<&str> {
        None
    }
}

/// The policy a session-config id names; `None` for an unknown id.
#[must_use]
pub fn create(id: &str) -> Option<Box<dyn Policy>> {
    match id {
        "idle" => Some(Box::new(IdlePolicy)),
        "lifecycle-smoke" => Some(Box::new(LifecycleSmokePolicy::default())),
        "portal-route-smoke" => Some(Box::new(PortalRouteSmokePolicy::default())),
        "observer-movement" => Some(Box::new(ObserverMovementPolicy::default())),
        "jump-probe" => Some(Box::new(JumpProbePolicy::default())),
        _ => None,
    }
}

/// Whether the character is in the world with its object arrived, as the
/// session's events tell it (the server takes requests from then on).
#[derive(Debug, Clone, Copy, Default)]
pub(crate) struct LocalPlayerWatch {
    generation: Generation,
    in_world: bool,
    player_ready: bool,
}

impl LocalPlayerWatch {
    /// Follow one event.
    pub(crate) fn observe(&mut self, event: &HostEvent) {
        match event {
            HostEvent::Lifecycle(lifecycle) => {
                if lifecycle.generation != self.generation {
                    self.player_ready = false;
                }
                self.generation = lifecycle.generation;
                self.in_world =
                    lifecycle.state == LifecycleState::InWorld && lifecycle.player_guid != 0;
            }
            HostEvent::Session(SessionEvent::LoginCompleteSent) => self.player_ready = true,
            _ => {}
        }
    }

    /// The character is present.
    pub(crate) fn has_local_player(&self) -> bool {
        self.in_world && self.player_ready
    }

    /// `command`, refused if it arrives in another generation.
    pub(crate) fn at(&self, command: SessionCommand) -> SessionCommand {
        SessionCommand::AtGeneration {
            generation: self.generation,
            command: Box::new(command),
        }
    }
}

/// Stays in the world and never finishes.
#[derive(Debug, Clone, Copy, Default)]
pub struct IdlePolicy;

impl Policy for IdlePolicy {
    fn observe(&mut self, _event: &HostEvent) -> Vec<SessionCommand> {
        Vec::new()
    }

    fn is_complete(&self) -> bool {
        false
    }
}

/// What the lifecycle smoke says before it recalls.
pub const LIFECYCLE_SMOKE_LINE: &str = "[openac headless lifecycle gate]";

/// Enter, chat, recall, arrive, reconnect, enter again.
#[derive(Debug, Clone, Default)]
pub struct LifecycleSmokePolicy {
    stage: u8,
    player: LocalPlayerWatch,
    first_generation: Generation,
}

impl Policy for LifecycleSmokePolicy {
    fn observe(&mut self, event: &HostEvent) -> Vec<SessionCommand> {
        self.player.observe(event);
        match self.stage {
            0 if self.player.has_local_player() => {
                self.first_generation = self.player.generation;
                self.stage = 1;
                vec![
                    self.player
                        .at(SessionCommand::Say(LIFECYCLE_SMOKE_LINE.to_owned())),
                    self.player.at(SessionCommand::Recall(Recall::Lifestone)),
                ]
            }
            1 if matches!(event, HostEvent::Portal(p) if p.kind == PortalKind::Portal && p.completed) =>
            {
                self.stage = 2;
                vec![
                    self.player
                        .at(SessionCommand::Lifecycle(LifecycleCommand::Reconnect)),
                ]
            }
            2 if self.player.generation > self.first_generation
                && self.player.has_local_player() =>
            {
                self.stage = 3;
                Vec::new()
            }
            _ => Vec::new(),
        }
    }

    fn is_complete(&self) -> bool {
        self.stage == 3
    }
}

/// One stop on the portal route: the server command that sends the
/// character there (none: recall to the lifestone) and the cell the
/// portal must arrive in (none: any).
#[derive(Debug, Clone, Copy)]
struct RouteStop {
    name: &'static str,
    command: Option<&'static str>,
    expected_cell: Option<u32>,
}

/// Aerlinthe, Caul, Rynthid, then the lifestone.
const ROUTE: [RouteStop; 4] = [
    RouteStop {
        name: "aerlinthe",
        command: Some("@teleloc 0x09040008 11.4 188.6 87.705 1 0 0 0"),
        expected_cell: Some(0x0904_0008),
    },
    RouteStop {
        name: "caul",
        command: Some("@teleloc 0xC95B0001 14.8 0.3 12.005 1 0 0 0"),
        expected_cell: Some(0xC95B_0001),
    },
    RouteStop {
        name: "rynthid",
        command: Some("@teleloc 0xF6820033 145.7 49.855 58.010 1 0 0 0"),
        expected_cell: Some(0xF682_0033),
    },
    RouteStop {
        name: "lifestone",
        command: None,
        expected_cell: None,
    },
];

/// The walk after each arrival: run forward 4 s, turn right 2 s, run
/// forward 4 s.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
enum RouteStage {
    #[default]
    None,
    ForwardOne,
    TurnRight,
    ForwardTwo,
}

/// Teleport through a chain of far destinations and recall to the
/// lifestone, each arrival completing in exactly the expected cell and
/// followed by a short walk; finishes after the last walk.
#[derive(Debug, Clone, Default)]
pub struct PortalRouteSmokePolicy {
    generation: Generation,
    in_world: bool,
    player_ready: bool,
    portal_generation: u64,
    next_stop: usize,
    awaiting_portal: bool,
    issued_after: u64,
    stage: RouteStage,
    failure: Option<String>,
}

impl PortalRouteSmokePolicy {
    fn at(&self, command: SessionCommand) -> SessionCommand {
        SessionCommand::AtGeneration {
            generation: self.generation,
            command: Box::new(command),
        }
    }

    fn walk(&mut self, stage: RouteStage) -> SessionCommand {
        use crate::movement::{MoveDirection, MovePace, MoveRequest, MovementCommand};
        self.stage = stage;
        let request = match stage {
            RouteStage::TurnRight => {
                MoveRequest::for_seconds(MoveDirection::TurnRight, MovePace::Walk, 2.0)
            }
            _ => MoveRequest::for_seconds(MoveDirection::Forward, MovePace::Run, 4.0),
        };
        self.at(SessionCommand::Movement(MovementCommand::Move(request)))
    }

    /// Send the character to the next stop.
    fn issue(&mut self) -> Vec<SessionCommand> {
        let Some(stop) = ROUTE.get(self.next_stop) else {
            return Vec::new();
        };
        self.issued_after = self.portal_generation;
        self.awaiting_portal = true;
        let command = match stop.command {
            Some(text) => SessionCommand::Say(text.to_owned()),
            None => SessionCommand::Recall(Recall::Lifestone),
        };
        vec![self.at(command)]
    }
}

impl Policy for PortalRouteSmokePolicy {
    fn observe(&mut self, event: &HostEvent) -> Vec<SessionCommand> {
        use crate::movement::{MoveChannel, MovementEvent, ScriptedMoveState};
        match event {
            HostEvent::Lifecycle(lifecycle) => {
                if lifecycle.generation != self.generation {
                    self.player_ready = false;
                }
                self.generation = lifecycle.generation;
                self.in_world =
                    lifecycle.state == LifecycleState::InWorld && lifecycle.player_guid != 0;
            }
            HostEvent::Session(SessionEvent::LoginCompleteSent) => self.player_ready = true,
            _ => {}
        }
        if self.failure.is_some() || !(self.in_world && self.player_ready) {
            if let HostEvent::Portal(p) = event {
                self.portal_generation = p.generation;
            }
            return Vec::new();
        }
        if self.awaiting_portal {
            let HostEvent::Portal(p) = event else {
                return Vec::new();
            };
            self.portal_generation = p.generation;
            if p.generation <= self.issued_after || p.kind != PortalKind::Portal || !p.completed {
                return Vec::new();
            }
            let stop = ROUTE[self.next_stop];
            if let Some(expected) = stop.expected_cell
                && p.readiness.destination_cell != expected
            {
                self.failure = Some(format!(
                    "portal route '{}' completed at 0x{:08X}; expected 0x{expected:08X}",
                    stop.name, p.readiness.destination_cell
                ));
                return Vec::new();
            }
            self.awaiting_portal = false;
            return vec![self.walk(RouteStage::ForwardOne)];
        }
        let walked_through_a_portal = matches!(
            event,
            HostEvent::Portal(p) if p.generation > self.portal_generation && p.completed
        );
        if let HostEvent::Portal(p) = event {
            self.portal_generation = p.generation;
        }
        if self.stage != RouteStage::None {
            // A portal the walk ran into ends its move; the walk goes on
            // where it came out (OpenAC holds its movement intent across).
            if walked_through_a_portal {
                return vec![self.walk(self.stage)];
            }
            let HostEvent::Movement(MovementEvent::MoveEnded { channel, state, .. }) = event else {
                return Vec::new();
            };
            let expected = if self.stage == RouteStage::TurnRight {
                MoveChannel::Turn
            } else {
                MoveChannel::Travel
            };
            if *channel != expected || *state == ScriptedMoveState::Lost {
                return Vec::new();
            }
            return match self.stage {
                RouteStage::ForwardOne => vec![self.walk(RouteStage::TurnRight)],
                RouteStage::TurnRight => vec![self.walk(RouteStage::ForwardTwo)],
                _ => {
                    self.stage = RouteStage::None;
                    self.next_stop += 1;
                    self.issue()
                }
            };
        }
        if self.next_stop < ROUTE.len() {
            return self.issue();
        }
        Vec::new()
    }

    fn is_complete(&self) -> bool {
        self.next_stop == ROUTE.len() && !self.awaiting_portal && self.stage == RouteStage::None
    }

    fn failure(&self) -> Option<&str> {
        self.failure.as_deref()
    }
}

#[cfg(test)]
mod tests {
    //! OpenAC `tests/AcDream.Headless.Tests/HeadlessBotPolicyTests.cs` (idle)
    //! and the lifecycle-smoke stages of `HeadlessBotPolicy.cs`.
    use super::*;
    use crate::session::Lifecycle;
    use crate::world::transit::PortalSnapshot;

    fn lifecycle(generation: u64, state: LifecycleState, player: u32) -> HostEvent {
        HostEvent::Lifecycle(Lifecycle {
            generation: Generation(generation),
            state,
            player_guid: player,
            has_transport: true,
        })
    }

    #[test]
    fn idle_never_acts_and_never_finishes() {
        let mut policy = create("idle").unwrap();
        for event in [
            lifecycle(1, LifecycleState::InWorld, 5),
            HostEvent::Session(SessionEvent::LoginCompleteSent),
            HostEvent::Portal(PortalSnapshot::IDLE),
        ] {
            assert!(policy.observe(&event).is_empty());
        }
        assert!(!policy.is_complete());
        assert!(create("no-such-policy").is_none());
    }

    #[test]
    fn the_lifecycle_smoke_chats_recalls_reconnects_and_finishes_in_a_new_generation() {
        let mut policy = LifecycleSmokePolicy::default();
        assert!(
            policy
                .observe(&lifecycle(1, LifecycleState::Starting, 0))
                .is_empty()
        );
        // In the world, but the player object has not arrived: wait.
        assert!(
            policy
                .observe(&lifecycle(1, LifecycleState::InWorld, 5))
                .is_empty()
        );
        let sent = policy.observe(&HostEvent::Session(SessionEvent::LoginCompleteSent));
        assert_eq!(sent.len(), 2);
        assert!(matches!(
            &sent[0],
            SessionCommand::AtGeneration { generation: Generation(1), command }
                if matches!(command.as_ref(), SessionCommand::Say(s) if s == LIFECYCLE_SMOKE_LINE)
        ));
        assert!(matches!(
            &sent[1],
            SessionCommand::AtGeneration { command, .. }
                if matches!(command.as_ref(), SessionCommand::Recall(Recall::Lifestone))
        ));
        // A login reveal or an unfinished portal does not count.
        let begun = PortalSnapshot {
            generation: 1,
            kind: PortalKind::Portal,
            ..PortalSnapshot::IDLE
        };
        assert!(policy.observe(&HostEvent::Portal(begun)).is_empty());
        let arrived = PortalSnapshot {
            completed: true,
            ..begun
        };
        let sent = policy.observe(&HostEvent::Portal(arrived));
        assert!(matches!(
            &sent[..],
            [SessionCommand::AtGeneration { command, .. }]
                if matches!(command.as_ref(), SessionCommand::Lifecycle(LifecycleCommand::Reconnect))
        ));
        assert!(!policy.is_complete());
        for event in [
            lifecycle(2, LifecycleState::Stopped, 0),
            lifecycle(3, LifecycleState::Starting, 0),
            lifecycle(3, LifecycleState::InWorld, 5),
        ] {
            assert!(policy.observe(&event).is_empty());
        }
        assert!(!policy.is_complete());
        policy.observe(&HostEvent::Session(SessionEvent::LoginCompleteSent));
        assert!(policy.is_complete());
    }

    fn arrived(generation: u64, cell: u32) -> HostEvent {
        let mut p = PortalSnapshot {
            generation,
            kind: PortalKind::Portal,
            completed: true,
            ..PortalSnapshot::IDLE
        };
        p.readiness.destination_cell = cell;
        HostEvent::Portal(p)
    }

    fn move_ended(channel: crate::movement::MoveChannel) -> HostEvent {
        HostEvent::Movement(crate::movement::MovementEvent::MoveEnded {
            channel,
            state: crate::movement::ScriptedMoveState::Completed,
            covered: 0.0,
            elapsed_seconds: 0.0,
        })
    }

    fn inner(command: &SessionCommand) -> &SessionCommand {
        match command {
            SessionCommand::AtGeneration { command, .. } => command,
            other => other,
        }
    }

    /// OpenAC `HeadlessBotPolicy.cs` `PortalRouteSmokeHeadlessBotPolicy`:
    /// each stop is a teleport (the last a lifestone recall) that must
    /// arrive in its cell, followed by forward, turn, forward.
    #[test]
    fn the_portal_route_teleports_walks_and_recalls_in_order() {
        use crate::movement::{MoveChannel, MoveDirection, MovementCommand};
        let mut policy = create("portal-route-smoke").unwrap();
        policy.observe(&lifecycle(1, LifecycleState::InWorld, 5));
        let sent = policy.observe(&HostEvent::Session(SessionEvent::LoginCompleteSent));
        assert!(matches!(
            inner(&sent[0]),
            SessionCommand::Say(s) if s.starts_with("@teleloc 0x09040008 ")
        ));
        let cells = [0x0904_0008, 0xC95B_0001, 0xF682_0033, 0xA9B4_0017];
        for (i, cell) in cells.into_iter().enumerate() {
            let generation = i as u64 + 1;
            // An unrelated event does not move the route on.
            assert!(policy.observe(&move_ended(MoveChannel::Travel)).is_empty());
            let sent = policy.observe(&arrived(generation, cell));
            assert!(matches!(
                inner(&sent[0]),
                SessionCommand::Movement(MovementCommand::Move(r))
                    if r.direction == MoveDirection::Forward && r.amount == 4.0
            ));
            let sent = policy.observe(&move_ended(MoveChannel::Travel));
            assert!(matches!(
                inner(&sent[0]),
                SessionCommand::Movement(MovementCommand::Move(r))
                    if r.direction == MoveDirection::TurnRight && r.amount == 2.0
            ));
            let sent = policy.observe(&move_ended(MoveChannel::Turn));
            assert!(matches!(
                inner(&sent[0]),
                SessionCommand::Movement(MovementCommand::Move(r))
                    if r.direction == MoveDirection::Forward
            ));
            let sent = policy.observe(&move_ended(MoveChannel::Travel));
            match i {
                0 => assert!(matches!(
                    inner(&sent[0]),
                    SessionCommand::Say(s) if s.starts_with("@teleloc 0xC95B0001 ")
                )),
                1 => assert!(matches!(
                    inner(&sent[0]),
                    SessionCommand::Say(s) if s.starts_with("@teleloc 0xF6820033 ")
                )),
                2 => assert!(matches!(
                    inner(&sent[0]),
                    SessionCommand::Recall(Recall::Lifestone)
                )),
                _ => assert!(sent.is_empty()),
            }
        }
        assert!(policy.is_complete());
        assert!(policy.failure().is_none());
    }

    /// A walk that runs into a portal goes on where the portal put the
    /// character (OpenAC's held intent outlives the teleport); the move the
    /// portal ended does not count as the walk's.
    #[test]
    fn a_walk_into_a_portal_goes_on_where_it_came_out() {
        use crate::movement::{
            MoveChannel, MoveDirection, MovementCommand, MovementEvent, ScriptedMoveState,
        };
        let mut policy = PortalRouteSmokePolicy::default();
        policy.observe(&lifecycle(1, LifecycleState::InWorld, 5));
        policy.observe(&HostEvent::Session(SessionEvent::LoginCompleteSent));
        policy.observe(&arrived(1, 0x0904_0008));
        let lost = HostEvent::Movement(MovementEvent::MoveEnded {
            channel: MoveChannel::Travel,
            state: ScriptedMoveState::Lost,
            covered: 3.0,
            elapsed_seconds: 0.2,
        });
        assert!(policy.observe(&lost).is_empty());
        let sent = policy.observe(&arrived(2, 0x0007_0145));
        assert!(matches!(
            inner(&sent[0]),
            SessionCommand::Movement(MovementCommand::Move(r))
                if r.direction == MoveDirection::Forward && r.amount == 4.0
        ));
        let sent = policy.observe(&move_ended(MoveChannel::Travel));
        assert!(matches!(
            inner(&sent[0]),
            SessionCommand::Movement(MovementCommand::Move(r))
                if r.direction == MoveDirection::TurnRight
        ));
        assert!(policy.failure().is_none());
    }

    #[test]
    fn a_portal_route_arriving_in_the_wrong_cell_fails() {
        let mut policy = PortalRouteSmokePolicy::default();
        policy.observe(&lifecycle(1, LifecycleState::InWorld, 5));
        policy.observe(&HostEvent::Session(SessionEvent::LoginCompleteSent));
        assert!(policy.observe(&arrived(1, 0x0904_0009)).is_empty());
        assert_eq!(
            policy.failure(),
            Some("portal route 'aerlinthe' completed at 0x09040009; expected 0x09040008")
        );
        assert!(!policy.is_complete());
    }
}
