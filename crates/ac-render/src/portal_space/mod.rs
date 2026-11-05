//! What a window shows while the player is in portal space: the world gives
//! way to the portal tunnel (a swirl model seen from inside, against black),
//! the tunnel holds until the window has the destination loaded, plays on,
//! warps away, and the destination warps in (see [`sequencer`] for the
//! timing and [`view_plane`] for the warp).
//!
//! Three things are presented this way, each from the session's
//! portal-space events: a portal transit (a teleport, a recall, a death's
//! respawn), the login (the tunnel from the enter request until the
//! player's surroundings have loaded) and the log out (the world warps away
//! into the tunnel, which holds until the session leaves the world).
//!
//! The session decides the teleport and places the player; the window
//! presents it. While a window is attached the session holds the arrival
//! (login complete) until the window answers that it has shown the
//! destination, at the end of the warp-in ([`PortalCue::DestinationShown`],
//! [`PortalCue::LoginShown`]).

pub mod sequencer;
pub mod tunnel;
pub mod view_plane;

use std::time::Duration;

use ac_dat::objects::AnimationHook;
use ac_runtime::world::transit::{
    DestinationReadiness, DestinationShown, PortalSpaceEvent, RETAIL_WAIT_CUE_DELAY,
};
use glam::Mat4;

use crate::reveal::ReadinessSnapshot;
use sequencer::{
    TUNNEL_END_FRAME, TeleportAnimEvent, TeleportAnimSequencer, TeleportAnimState,
    TeleportEntryKind,
};
use tunnel::PortalTunnel;
use view_plane::TeleportViewPlane;

/// Something portal space asks the window to do besides drawing.
#[derive(Debug, Clone, PartialEq)]
pub enum PortalCue {
    /// Play the enter-portal interface sound.
    EnterSound,
    /// Play the exit-portal interface sound.
    ExitSound,
    /// A hook of the tunnel's animation (its sound), played centred.
    TunnelHook(AnimationHook),
    /// Show the wait line ([`tunnel::WAIT_TEXT`]).
    WaitText,
    /// The destination the session holds the arrival for has been shown:
    /// tell the session.
    DestinationShown(DestinationShown),
    /// The login destination the session holds login complete for has been
    /// shown, with what the reveal rule found: tell the session.
    LoginShown(DestinationReadiness),
    /// The reveal the session holds for this window has waited
    /// [`RETAIL_WAIT_CUE_DELAY`] without its destination being ready (once
    /// per reveal): tell the session, which records the wait cue.
    RevealWaited {
        /// The reveal.
        generation: u64,
        /// How long it has waited.
        waited: Duration,
    },
    /// The tunnel has held for [`STALL_NOTICE_SECONDS`] with the
    /// destination known and not loaded (once per destination): the window
    /// says what it still waits for. The game waits on regardless.
    DestinationStalled {
        /// The destination cell.
        destination_cell: u32,
        /// What the reveal rule found.
        readiness: ReadinessSnapshot,
    },
}

/// How long the tunnel may hold for a known destination before the window
/// says what it waits for, seconds. A diagnostic: nothing else changes.
pub const STALL_NOTICE_SECONDS: f32 = 30.0;

/// What the presentation is for.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum PresentationKind {
    /// A teleport (a portal, a recall, a death's respawn).
    #[default]
    Portal,
    /// Entering the world.
    Login,
    /// Leaving it.
    Logout,
}

/// One window's portal-space presentation.
#[derive(Debug, Default)]
pub struct PortalSpace {
    sequencer: TeleportAnimSequencer,
    view_plane: TeleportViewPlane,
    kind: PresentationKind,
    teleport: Option<u16>,
    destination: Option<u32>,
    /// The reveal the session holds the arrival (or login complete) of for
    /// this window.
    held_generation: Option<u64>,
    /// What the reveal rule found when the destination was ready.
    placed: Option<ReadinessSnapshot>,
    /// Seconds the tunnel has held with the destination known.
    waited: f32,
    /// The stall was reported for this destination.
    stall_reported: bool,
    /// Seconds held without the destination being ready (the wait cue's
    /// clock).
    hold_seconds: f32,
    /// The wait cue was reported for the held reveal.
    wait_cue_reported: bool,
    /// The login presentation has the player's mode (the character is
    /// present and in the window's hands).
    login_mode_entered: bool,
}

impl PortalSpace {
    /// Not in portal space.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// The presentation runs.
    #[must_use]
    pub fn is_active(&self) -> bool {
        self.sequencer.is_active()
    }

    /// What the running presentation is for.
    #[must_use]
    pub fn kind(&self) -> Option<PresentationKind> {
        self.is_active().then_some(self.kind)
    }

    /// The presentation's state.
    #[must_use]
    pub fn state(&self) -> TeleportAnimState {
        self.sequencer.state()
    }

    /// The destination cell, once the session has it.
    #[must_use]
    pub fn destination(&self) -> Option<u32> {
        self.destination
    }

    /// The login presentation waits for the player's mode: the window
    /// should take it ([`PortalSpace::enter_login_mode`] once it has).
    #[must_use]
    pub fn needs_login_mode(&self) -> bool {
        self.kind() == Some(PresentationKind::Login)
            && self.destination.is_some()
            && !self.login_mode_entered
    }

    /// The window took the player's mode for the login.
    pub fn enter_login_mode(&mut self) {
        if self.kind() == Some(PresentationKind::Login) {
            self.login_mode_entered = true;
        }
    }

    /// The session's event. `game_projection` is the world's projection now
    /// (the warp starts from it). False when the event withdraws the
    /// presentation: the window resets it ([`PortalSpace::reset`]).
    pub fn on_event(&mut self, event: PortalSpaceEvent, game_projection: &Mat4) -> bool {
        match event {
            PortalSpaceEvent::Entered { teleport_sequence } => {
                self.begin(teleport_sequence, game_projection);
            }
            PortalSpaceEvent::Destination {
                generation,
                teleport_sequence,
                destination_cell,
            } => {
                // The session waits for this window: a destination for a
                // teleport it is not presenting starts the presentation, so
                // the answer always comes.
                if self.kind() != Some(PresentationKind::Portal)
                    || self.teleport != Some(teleport_sequence)
                {
                    self.begin(teleport_sequence, game_projection);
                }
                self.hold_destination(destination_cell, generation);
                self.hold_seconds = 0.0;
            }
            PortalSpaceEvent::Arrived {
                teleport_sequence,
                destination_cell,
            } => {
                if self.kind() == Some(PresentationKind::Portal)
                    && self.teleport == Some(teleport_sequence)
                    && self.destination.is_none()
                {
                    self.destination = Some(destination_cell);
                }
            }
            // The tunnel shows from the enter request; one already running
            // (a teleport, or this entry's) is kept.
            PortalSpaceEvent::LoginEntering => {
                if !self.is_active() {
                    self.begin_kind(PresentationKind::Login, None, game_projection);
                }
            }
            // The tunnel shown since the enter request is adopted without a
            // restart or a second cue; without one it starts now.
            PortalSpaceEvent::LoginDestination {
                generation,
                destination_cell,
            } => {
                if self.kind() == Some(PresentationKind::Portal) {
                    return true;
                }
                if self.kind() != Some(PresentationKind::Login) {
                    self.begin_kind(PresentationKind::Login, None, game_projection);
                }
                self.hold_destination(destination_cell, generation);
            }
            PortalSpaceEvent::LoginWithdrawn => {
                return self.kind() != Some(PresentationKind::Login);
            }
            PortalSpaceEvent::LogoutStarted => {
                self.begin_kind(PresentationKind::Logout, None, game_projection);
            }
        }
        true
    }

    fn hold_destination(&mut self, destination_cell: u32, generation: u64) {
        if self.destination != Some(destination_cell) {
            self.waited = 0.0;
            self.stall_reported = false;
        }
        if self.held_generation != Some(generation) {
            self.wait_cue_reported = false;
        }
        self.destination = Some(destination_cell);
        self.held_generation = Some(generation);
    }

    fn begin(&mut self, teleport_sequence: u16, game_projection: &Mat4) {
        self.begin_kind(
            PresentationKind::Portal,
            Some(teleport_sequence),
            game_projection,
        );
    }

    fn begin_kind(
        &mut self,
        kind: PresentationKind,
        teleport_sequence: Option<u16>,
        game_projection: &Mat4,
    ) {
        self.kind = kind;
        self.teleport = teleport_sequence;
        self.destination = None;
        self.held_generation = None;
        self.placed = None;
        self.waited = 0.0;
        self.stall_reported = false;
        self.hold_seconds = 0.0;
        self.wait_cue_reported = false;
        self.login_mode_entered = false;
        self.view_plane.begin(game_projection);
        // A login and a portal start in the tunnel; a log out first warps
        // the world away.
        self.sequencer.begin(match kind {
            PresentationKind::Portal => TeleportEntryKind::Portal,
            PresentationKind::Login => TeleportEntryKind::Login,
            PresentationKind::Logout => TeleportEntryKind::Logout,
        });
    }

    /// Leave portal space at once (the session's world was replaced, or
    /// the entry went back to selection).
    pub fn reset(&mut self, tunnel: Option<&mut PortalTunnel>) {
        self.sequencer.reset();
        self.view_plane.reset();
        self.kind = PresentationKind::Portal;
        self.teleport = None;
        self.destination = None;
        self.held_generation = None;
        self.placed = None;
        self.waited = 0.0;
        self.stall_reported = false;
        self.hold_seconds = 0.0;
        self.wait_cue_reported = false;
        self.login_mode_entered = false;
        if let Some(tunnel) = tunnel {
            tunnel.exit();
        }
    }

    /// Advance `dt` seconds. `readiness`: the window's reveal rule for the
    /// destination ([`PortalSpace::destination`]); the tunnel holds until
    /// it is ready (a log out holds until the session leaves the world).
    /// The tunnel, when its assets resolved, is shown, hidden and animated
    /// here.
    pub fn tick(
        &mut self,
        dt: f32,
        readiness: ReadinessSnapshot,
        mut tunnel: Option<&mut PortalTunnel>,
    ) -> Vec<PortalCue> {
        let mut cues = Vec::new();
        if !self.sequencer.is_active() {
            return cues;
        }
        let loaded = self
            .destination
            .is_some_and(|cell| readiness.destination_cell == cell && readiness.is_ready());
        let ready = match self.kind {
            PresentationKind::Portal => loaded,
            PresentationKind::Login => loaded && self.login_mode_entered,
            PresentationKind::Logout => false,
        };
        // The wait cue's clock: a portal waits once its destination is
        // known, the login from the enter request.
        let waiting = match self.kind {
            PresentationKind::Portal => self.destination.is_some() && !ready,
            PresentationKind::Login => !ready,
            PresentationKind::Logout => false,
        };
        if waiting {
            self.hold_seconds += dt;
        }
        let waited = Duration::from_secs_f32(self.hold_seconds.max(0.0));
        if let Some(generation) = self.held_generation
            && waiting
            && !self.wait_cue_reported
            && waited >= RETAIL_WAIT_CUE_DELAY
        {
            self.wait_cue_reported = true;
            cues.push(PortalCue::RevealWaited { generation, waited });
        }
        // Without the tunnel's animation the exit window is never met and
        // the five-second ceiling ends the tunnel.
        let frame = tunnel
            .as_ref()
            .map_or(TUNNEL_END_FRAME, |t| t.current_animation_frame());
        let (snapshot, events) = self.sequencer.tick(dt, ready, frame, false);
        if let Some(destination_cell) = self.destination
            && snapshot.state == TeleportAnimState::Tunnel
        {
            self.waited += dt;
            if self.waited >= STALL_NOTICE_SECONDS && !self.stall_reported {
                self.stall_reported = true;
                cues.push(PortalCue::DestinationStalled {
                    destination_cell,
                    readiness,
                });
            }
        }
        // The tunnel goes before the destination's warp is published.
        if !snapshot.show_tunnel
            && let Some(t) = tunnel.as_deref_mut()
            && t.is_visible()
        {
            t.exit();
        }
        self.view_plane.update(&snapshot);
        for event in events {
            match event {
                TeleportAnimEvent::PlayEnterSound => cues.push(PortalCue::EnterSound),
                TeleportAnimEvent::EnterTunnel => {
                    if let Some(t) = tunnel.as_deref_mut() {
                        t.enter();
                    }
                }
                // The session placed the body when it had the destination;
                // what the reveal rule found here goes with the answer.
                TeleportAnimEvent::Place => self.placed = Some(readiness),
                TeleportAnimEvent::PlayExitSound => {
                    if let Some(t) = tunnel.as_deref_mut() {
                        t.exit();
                    }
                    cues.push(PortalCue::ExitSound);
                }
                // The session completes what it holds for this window: the
                // player enters the world and the server hears login
                // complete.
                TeleportAnimEvent::FireLoginComplete => {
                    match (self.kind, self.held_generation, self.placed) {
                        (PresentationKind::Portal, Some(generation), Some(placed)) => {
                            if let Some(teleport_sequence) = self.teleport {
                                cues.push(PortalCue::DestinationShown(DestinationShown {
                                    teleport_sequence,
                                    readiness: destination_readiness(generation, &placed),
                                }));
                            }
                        }
                        (PresentationKind::Login, Some(generation), Some(placed)) => {
                            cues.push(PortalCue::LoginShown(destination_readiness(
                                generation, &placed,
                            )));
                        }
                        _ => {}
                    }
                    self.teleport = None;
                    self.destination = None;
                    self.held_generation = None;
                    self.placed = None;
                    self.login_mode_entered = false;
                    return cues;
                }
            }
        }
        if let Some(t) = tunnel {
            let (hooks, new_turn) = t.tick(dt);
            cues.extend(hooks.into_iter().map(PortalCue::TunnelHook));
            if new_turn {
                cues.push(PortalCue::WaitText);
            }
        }
        cues
    }

    /// The world's projection (its sky's as well): `base`, warped while the
    /// warp runs.
    #[must_use]
    pub fn world_projection(&self, base: Mat4) -> Mat4 {
        self.view_plane.apply(base)
    }

    /// The world is replaced by the tunnel (or by black while the tunnel's
    /// assets are missing).
    #[must_use]
    pub fn hides_world(&self) -> bool {
        matches!(
            self.sequencer.state(),
            TeleportAnimState::TunnelFadeIn
                | TeleportAnimState::Tunnel
                | TeleportAnimState::TunnelContinue
                | TeleportAnimState::TunnelFadeOut
        )
    }
}

/// The window's reveal rule, as the session's transit owner hears it.
#[must_use]
pub fn destination_readiness(generation: u64, found: &ReadinessSnapshot) -> DestinationReadiness {
    DestinationReadiness {
        generation,
        destination_cell: found.destination_cell,
        is_indoor: found.is_indoor,
        is_unhydratable: found.is_unhydratable,
        required_render_radius: i32::try_from(found.required_render_radius).unwrap_or(i32::MAX),
        is_render_neighborhood_ready: found.render_ready,
        are_composite_textures_ready: found.composites_ready,
        is_collision_ready: found.collision_ready,
    }
}

#[cfg(test)]
mod tests {
    //! The window's side of OpenAC `LocalPlayerTeleportController.Tick` and
    //! `LocalPlayerTeleportPresentation.Tick` for a portal transit
    //! (`tests/AcDream.App.Tests/Streaming/LocalPlayerTeleportControllerTests.cs`
    //! where a case names its source).
    use super::*;
    use std::f32::consts::PI;

    const CELL: u32 = 0xA9B4_0031;

    fn projection() -> Mat4 {
        view_plane::perspective(PI / 3.0, 16.0 / 9.0, 0.1, 5000.0)
    }

    fn entered(sequence: u16) -> PortalSpaceEvent {
        PortalSpaceEvent::Entered {
            teleport_sequence: sequence,
        }
    }

    fn arrived(sequence: u16, cell: u32) -> PortalSpaceEvent {
        PortalSpaceEvent::Arrived {
            teleport_sequence: sequence,
            destination_cell: cell,
        }
    }

    fn destination(generation: u64, sequence: u16, cell: u32) -> PortalSpaceEvent {
        PortalSpaceEvent::Destination {
            generation,
            teleport_sequence: sequence,
            destination_cell: cell,
        }
    }

    /// The reveal rule for `cell`: everything loaded, or nothing yet.
    fn rule(cell: u32, loaded: bool) -> ReadinessSnapshot {
        ReadinessSnapshot {
            destination_cell: cell,
            is_indoor: false,
            is_unhydratable: false,
            required_render_radius: 4,
            required_near_radius: 2,
            render_ready: loaded,
            composites_ready: loaded,
            collision_ready: loaded,
        }
    }

    /// Run to the end with the world ready from `ready_after` seconds on;
    /// every cue and the seconds the world was hidden.
    fn run(space: &mut PortalSpace, ready_after: f32) -> (Vec<PortalCue>, f32) {
        let mut cues = Vec::new();
        let (mut t, mut hidden) = (0.0f32, 0.0f32);
        let dt = 1.0 / 60.0;
        while space.is_active() && t < 60.0 {
            cues.extend(space.tick(dt, rule(CELL, t >= ready_after), None));
            if space.hides_world() {
                hidden += dt;
            }
            t += dt;
        }
        (cues, hidden)
    }

    #[test]
    fn the_tunnel_holds_until_the_destination_has_arrived_and_is_loaded() {
        let mut space = PortalSpace::new();
        space.on_event(entered(3), &projection());
        assert!(space.is_active() && space.hides_world());
        // Loaded, but no destination yet: the tunnel holds.
        for _ in 0..600 {
            space.tick(1.0 / 60.0, rule(0x7D64_0020, true), None);
        }
        assert_eq!(space.state(), TeleportAnimState::Tunnel);
        // A destination for another teleport is not this one's.
        space.on_event(arrived(2, 0x7D64_0020), &projection());
        space.tick(1.0 / 60.0, rule(0x7D64_0020, true), None);
        assert_eq!(space.state(), TeleportAnimState::Tunnel);
        space.on_event(arrived(3, 0x7D64_0020), &projection());
        assert_eq!(space.destination(), Some(0x7D64_0020));
        // The rule's answer for another cell is not this destination's.
        space.tick(1.0 / 60.0, rule(0x7D64_0021, true), None);
        assert_eq!(space.state(), TeleportAnimState::Tunnel);
        space.tick(1.0 / 60.0, rule(0x7D64_0020, true), None);
        assert_eq!(space.state(), TeleportAnimState::TunnelContinue);
    }

    #[test]
    fn a_portal_transit_sounds_enter_then_exit_and_ends_with_the_world_back() {
        let mut space = PortalSpace::new();
        space.on_event(entered(1), &projection());
        space.on_event(arrived(1, CELL), &projection());
        let (cues, hidden) = run(&mut space, 0.5);
        // The session did not wait for this window: no answer.
        assert_eq!(cues, [PortalCue::EnterSound, PortalCue::ExitSound]);
        assert!(!space.is_active() && !space.hides_world());
        assert_eq!(space.destination(), None);
        // Held 0.5 s, then five seconds on (without the tunnel's animation
        // the exit window never comes), then the warp away until its last
        // visible level (0.98 s).
        assert!((6.4..=6.6).contains(&hidden), "{hidden}");
        assert_eq!(space.world_projection(projection()), projection());
    }

    /// `LoginComplete_EntersWorldThenSendsThenCompletesAndResets` and
    /// `ExitSound_HidesTunnelBeforeReleasingDestinationAndKeepsProtocolActive`:
    /// the session hears the window only when the destination's warp-in
    /// ends, after the exit sound, with what the reveal rule found.
    #[test]
    fn a_held_arrival_is_answered_when_the_destination_has_warped_in() {
        let mut space = PortalSpace::new();
        space.on_event(entered(4), &projection());
        space.on_event(destination(9, 4, CELL), &projection());
        let (cues, hidden) = run(&mut space, 1.0);
        let expected = DestinationShown {
            teleport_sequence: 4,
            readiness: DestinationReadiness {
                generation: 9,
                destination_cell: CELL,
                is_indoor: false,
                is_unhydratable: false,
                required_render_radius: 4,
                is_render_neighborhood_ready: true,
                are_composite_textures_ready: true,
                is_collision_ready: true,
            },
        };
        assert_eq!(
            cues,
            [
                PortalCue::EnterSound,
                PortalCue::ExitSound,
                PortalCue::DestinationShown(expected)
            ]
        );
        assert!(expected.readiness.is_ready());
        assert!(hidden > 6.9, "the answer comes after the tunnel: {hidden}");
        assert!(!space.is_active());
    }

    /// OpenAC `WorldRevealCoordinator`: the portal's world-generation
    /// quiescence (the world's sounds quiet) runs from the start of the
    /// portal until its destination is shown. The presentation is active
    /// for exactly that span, past the exit sound and the warp-in.
    #[test]
    fn the_portal_holds_the_world_from_its_start_until_the_destination_is_shown() {
        let mut space = PortalSpace::new();
        assert!(!space.is_active());
        space.on_event(entered(6), &projection());
        assert!(space.is_active(), "held from the start");
        space.on_event(destination(2, 6, CELL), &projection());
        let mut exit_heard = false;
        for _ in 0..3600 {
            let cues = space.tick(1.0 / 60.0, rule(CELL, true), None);
            exit_heard |= cues.contains(&PortalCue::ExitSound);
            if cues
                .iter()
                .any(|c| matches!(c, PortalCue::DestinationShown(_)))
            {
                assert!(exit_heard);
                assert!(!space.is_active(), "released with the destination");
                return;
            }
            assert!(space.is_active(), "held until the destination is shown");
        }
        panic!("the destination was never shown");
    }

    /// `ReadinessHold_NeverRevealsIncompleteWorldAndShowsWaitCueAfterFiveSeconds`:
    /// a destination that never loads is never answered; the window says
    /// once, after [`STALL_NOTICE_SECONDS`], what it waits for.
    #[test]
    fn a_destination_that_never_loads_is_never_answered_but_reported() {
        let mut space = PortalSpace::new();
        space.on_event(entered(5), &projection());
        space.on_event(destination(2, 5, CELL), &projection());
        let mut cues = Vec::new();
        let dt = 1.0 / 60.0;
        let mut stalled_at = None;
        let mut waits = Vec::new();
        for frame in 0..(90 * 60) {
            let new = space.tick(dt, rule(CELL, false), None);
            if new
                .iter()
                .any(|c| matches!(c, PortalCue::DestinationStalled { .. }))
            {
                stalled_at = Some(frame as f32 * dt);
            }
            for cue in new {
                match cue {
                    PortalCue::RevealWaited { generation, waited } => {
                        waits.push((generation, waited, frame as f32 * dt));
                    }
                    cue => cues.push(cue),
                }
            }
        }
        // The wait cue, once, from five seconds on.
        let [(generation, waited, at)] = waits[..] else {
            panic!("one wait cue: {waits:?}");
        };
        assert_eq!(generation, 2);
        assert!(waited >= RETAIL_WAIT_CUE_DELAY);
        assert!((4.9..5.1).contains(&at), "{at}");
        assert_eq!(
            cues,
            [
                PortalCue::EnterSound,
                PortalCue::DestinationStalled {
                    destination_cell: CELL,
                    readiness: rule(CELL, false),
                }
            ]
        );
        let at = stalled_at.expect("reported");
        assert!((STALL_NOTICE_SECONDS - 0.1..STALL_NOTICE_SECONDS + 0.1).contains(&at));
        assert_eq!(space.state(), TeleportAnimState::Tunnel);
        // A destination that loads in time is never reported.
        let mut space = PortalSpace::new();
        space.on_event(entered(6), &projection());
        space.on_event(destination(3, 6, CELL), &projection());
        let (cues, _) = run(&mut space, STALL_NOTICE_SECONDS - 1.0);
        assert!(
            !cues
                .iter()
                .any(|c| matches!(c, PortalCue::DestinationStalled { .. }))
        );
    }

    /// Portals in a row, each held until its own destination has loaded:
    /// each is answered with its own teleport, reveal and cell.
    #[test]
    fn portals_in_a_row_are_each_answered() {
        let mut space = PortalSpace::new();
        let cells = [0x977B_000C, 0x5650_0140, 0x9F44_001A, 0xA9B4_0031];
        for (i, &cell) in cells.iter().enumerate() {
            let sequence = i as u16 + 1;
            space.on_event(entered(sequence), &projection());
            space.on_event(destination(i as u64 + 10, sequence, cell), &projection());
            let mut answer = None;
            for frame in 0..(20 * 60) {
                // The destination loads two seconds in.
                let loaded = frame >= 120;
                for cue in space.tick(1.0 / 60.0, rule(cell, loaded), None) {
                    if let PortalCue::DestinationShown(shown) = cue {
                        answer = Some(shown);
                    }
                }
                if !space.is_active() {
                    break;
                }
            }
            let shown = answer.unwrap_or_else(|| panic!("portal {sequence} to {cell:#010X}"));
            assert_eq!(shown.teleport_sequence, sequence);
            assert_eq!(shown.readiness.generation, i as u64 + 10);
            assert_eq!(shown.readiness.destination_cell, cell);
        }
    }

    /// A window that attaches while the session holds the arrival hears the
    /// destination right after the start; one that missed the start begins
    /// the tunnel on the destination, so the answer always comes.
    #[test]
    fn a_held_destination_without_its_start_still_runs_and_is_answered() {
        let mut space = PortalSpace::new();
        space.on_event(destination(3, 7, CELL), &projection());
        assert!(space.is_active() && space.hides_world());
        let (cues, _) = run(&mut space, 0.0);
        assert!(matches!(
            cues.last(),
            Some(PortalCue::DestinationShown(DestinationShown {
                teleport_sequence: 7,
                readiness: DestinationReadiness { generation: 3, .. }
            }))
        ));
    }

    /// `NewerStart_ReplacesOldDestinationWithoutReusingIt`: a newer start
    /// forgets the held destination; its answer never comes.
    #[test]
    fn a_newer_start_forgets_the_held_destination() {
        let mut space = PortalSpace::new();
        space.on_event(entered(1), &projection());
        space.on_event(destination(1, 1, CELL), &projection());
        space.on_event(entered(2), &projection());
        assert_eq!(space.destination(), None);
        for _ in 0..600 {
            assert!(
                space
                    .tick(1.0 / 60.0, rule(CELL, true), None)
                    .iter()
                    .all(|cue| !matches!(cue, PortalCue::DestinationShown(_)))
            );
        }
        assert_eq!(space.state(), TeleportAnimState::Tunnel);
    }

    #[test]
    fn the_destination_warps_in_from_almost_180_degrees() {
        let mut space = PortalSpace::new();
        space.on_event(entered(1), &projection());
        space.on_event(arrived(1, CELL), &projection());
        while space.state() != TeleportAnimState::WorldFadeIn {
            space.tick(1.0 / 60.0, rule(CELL, true), None);
        }
        let warped = space.world_projection(projection());
        assert!(view_plane::view_plane_distance(&warped) < 0.05);
        assert!(!space.hides_world());
    }

    #[test]
    fn a_reset_leaves_portal_space_at_once() {
        let mut space = PortalSpace::new();
        space.on_event(entered(1), &projection());
        space.on_event(destination(1, 1, CELL), &projection());
        space.reset(None);
        assert!(!space.is_active() && !space.hides_world());
        assert!(space.tick(0.1, rule(CELL, true), None).is_empty());
    }

    /// OpenAC `PortalTunnelPresentation.TickRotation`: the wait line is
    /// posted each time the tunnel camera starts a turn (every 0.6..1.8 s),
    /// and only while the tunnel is shown (`Tick` returns at once once
    /// `Exit` hid it, at the end of the tunnel's warp-out). Nothing ends the
    /// line at the arrival: the status box keeps it for its five seconds
    /// from the last post (OpenAC `SpewBoxState.DefaultLifetime`), so it is
    /// still up for 2.2..4 s after the destination is shown, as in OpenAC.
    #[test]
    fn the_wait_line_is_posted_only_while_the_tunnel_is_shown() {
        use crate::animation::test_support::{MapLoader, setup};
        use ac_core::sky::DotNetRandom;
        use std::sync::Arc;

        let mut tunnel = tunnel::PortalTunnel::new(
            0x0200_0001,
            Arc::new(setup(1)),
            0x0300_0001,
            Arc::new(MapLoader::default()),
            DotNetRandom::new(11),
        );
        let mut space = PortalSpace::new();
        space.on_event(entered(8), &projection());
        space.on_event(destination(4, 8, CELL), &projection());
        let dt = 1.0 / 60.0;
        let mut t = 0.0f32;
        let (mut posts, mut exit, mut shown) = (Vec::new(), None, None);
        while space.is_active() && t < 60.0 {
            let cues = space.tick(dt, rule(CELL, t >= 1.0), Some(&mut tunnel));
            for cue in cues {
                match cue {
                    PortalCue::WaitText => {
                        assert!(tunnel.is_visible(), "posted with the tunnel shown");
                        assert!(exit.is_none(), "no post after the exit");
                        posts.push(t);
                    }
                    PortalCue::ExitSound => exit = Some(t),
                    PortalCue::DestinationShown(_) => shown = Some(t),
                    _ => {}
                }
            }
            t += dt;
        }
        let (exit, shown) = (exit.expect("exit"), shown.expect("shown"));
        assert!(posts.len() > 3, "a post per turn: {posts:?}");
        assert!(posts[0] < 0.1, "the first turn starts at once");
        for pair in posts.windows(2) {
            let turn = pair[1] - pair[0];
            assert!((0.58..=1.82).contains(&turn), "a turn: {turn}");
        }
        let last = *posts.last().expect("posts");
        assert!(exit - last < 1.82, "the last post is the last turn's");
        let after_shown = last + 5.0 - shown;
        assert!(
            (2.1..=4.1).contains(&after_shown),
            "the line outlives the arrival by {after_shown} s"
        );
    }

    fn login_destination(generation: u64, cell: u32) -> PortalSpaceEvent {
        PortalSpaceEvent::LoginDestination {
            generation,
            destination_cell: cell,
        }
    }

    /// Tick `seconds` at 60 frames per second with the rule's answer
    /// `loaded`; every cue.
    fn ticks(space: &mut PortalSpace, seconds: f32, loaded: bool) -> Vec<PortalCue> {
        let mut cues = Vec::new();
        let frames = (seconds * 60.0).round() as usize;
        for _ in 0..frames {
            cues.extend(space.tick(1.0 / 60.0, rule(CELL, loaded), None));
        }
        cues
    }

    /// OpenAC `ArmLoginTunnel_ShowsTunnelAndPlaysCueSynchronously`,
    /// `ArmedLoginTunnel_IsAdoptedByTheRevealWithoutRestartOrSecondCue`,
    /// `ArmedLoginTunnel_RetriesModeOwnershipWithoutRestartOrEarlyCompletion`
    /// and `LoginPump_SendsLoginCompleteOnlyAtThePresentationEnd`: the tunnel
    /// shows from the enter request, the destination adopts it without a
    /// second cue, a loaded destination waits for the player's mode, and the
    /// session hears the login only at the end of the warp-in.
    #[test]
    fn the_login_tunnel_runs_from_the_enter_request_to_the_shown_destination() {
        let mut space = PortalSpace::new();
        assert!(space.on_event(PortalSpaceEvent::LoginEntering, &projection()));
        assert_eq!(space.kind(), Some(PresentationKind::Login));
        assert!(space.hides_world());
        // Entering again (a window re-joining) neither restarts nor cues.
        assert!(space.on_event(PortalSpaceEvent::LoginEntering, &projection()));
        assert_eq!(ticks(&mut space, 1.0, true), [PortalCue::EnterSound]);
        assert_eq!(
            space.state(),
            TeleportAnimState::Tunnel,
            "no destination yet"
        );
        assert!(!space.needs_login_mode());

        space.on_event(login_destination(7, CELL), &projection());
        assert_eq!(space.destination(), Some(CELL));
        assert!(space.needs_login_mode());
        // Loaded, but the player's mode is not taken: it holds.
        assert!(ticks(&mut space, 1.0, true).is_empty());
        assert_eq!(space.state(), TeleportAnimState::Tunnel);
        space.enter_login_mode();
        assert!(!space.needs_login_mode());
        let mut cues = Vec::new();
        while space.is_active() {
            cues.extend(space.tick(1.0 / 60.0, rule(CELL, true), None));
            if space.is_active() {
                assert!(
                    !cues.iter().any(|c| matches!(c, PortalCue::LoginShown(_))),
                    "only at the end"
                );
            }
        }
        assert_eq!(
            cues,
            [
                PortalCue::ExitSound,
                PortalCue::LoginShown(destination_readiness(7, &rule(CELL, true)))
            ]
        );
        assert_eq!(space.kind(), None);
        assert_eq!(space.world_projection(projection()), projection());
    }

    /// OpenAC `LoginReveal_ArmsThePortalSpacePresentation_WithRetailCues`:
    /// a login destination without the enter request (a window attached
    /// late) starts the tunnel, with its cue.
    #[test]
    fn a_login_destination_without_the_enter_request_starts_the_tunnel() {
        let mut space = PortalSpace::new();
        space.on_event(login_destination(3, CELL), &projection());
        assert_eq!(space.kind(), Some(PresentationKind::Login));
        space.enter_login_mode();
        let cues = ticks(&mut space, 10.0, true);
        assert_eq!(cues.first(), Some(&PortalCue::EnterSound));
        assert!(matches!(
            cues.last(),
            Some(PortalCue::LoginShown(DestinationReadiness {
                generation: 3,
                ..
            }))
        ));
    }

    /// OpenAC `ArmedLoginTunnel_DisarmsWhenTheEnterFallsBackToSelection`:
    /// the entry back at selection withdraws the login tunnel (and nothing
    /// else); a later enter arms afresh with its own cue.
    #[test]
    fn a_withdrawn_entry_ends_the_login_tunnel_only() {
        let mut space = PortalSpace::new();
        space.on_event(PortalSpaceEvent::LoginEntering, &projection());
        assert!(!space.on_event(PortalSpaceEvent::LoginWithdrawn, &projection()));
        space.reset(None);
        assert!(!space.is_active());
        space.on_event(PortalSpaceEvent::LoginEntering, &projection());
        assert_eq!(ticks(&mut space, 0.1, false), [PortalCue::EnterSound]);
        // A teleport's tunnel is not the entry's.
        let mut space = PortalSpace::new();
        space.on_event(entered(2), &projection());
        assert!(space.on_event(PortalSpaceEvent::LoginWithdrawn, &projection()));
        assert_eq!(space.kind(), Some(PresentationKind::Portal));
    }

    /// OpenAC `RealTeleportStart_SupersedesTheLoginPresentation`: a teleport
    /// start replaces the login tunnel; the login is never answered, and a
    /// late login destination does not take the teleport's tunnel over.
    #[test]
    fn a_teleport_start_replaces_the_login_tunnel() {
        let mut space = PortalSpace::new();
        space.on_event(login_destination(4, CELL), &projection());
        space.enter_login_mode();
        space.on_event(entered(9), &projection());
        assert_eq!(space.kind(), Some(PresentationKind::Portal));
        space.on_event(login_destination(4, CELL), &projection());
        assert_eq!(space.kind(), Some(PresentationKind::Portal));
        assert_eq!(space.destination(), None);
        space.on_event(destination(5, 9, CELL), &projection());
        let cues = ticks(&mut space, 12.0, true);
        assert!(!cues.iter().any(|c| matches!(c, PortalCue::LoginShown(_))));
        assert!(matches!(
            cues.last(),
            Some(PortalCue::DestinationShown(DestinationShown {
                teleport_sequence: 9,
                ..
            }))
        ));
    }

    /// OpenAC `LogoutRequest_SendsWireThenHoldsThreeSeconds_ThenBeginsWormhole`
    /// and `LogoutConfirmation_RunsTheHandoffOnceAndCompletesTheLifecycle`:
    /// the log out warps the world away (the enter cue at once), enters the
    /// tunnel on its own edge and holds there; it never answers, and the
    /// session leaving the world ends it without the exit cue.
    #[test]
    fn the_log_out_warps_the_world_away_into_a_tunnel_that_holds() {
        let mut space = PortalSpace::new();
        space.on_event(PortalSpaceEvent::LogoutStarted, &projection());
        assert_eq!(space.kind(), Some(PresentationKind::Logout));
        assert_eq!(space.state(), TeleportAnimState::WorldFadeOut);
        assert!(!space.hides_world(), "the world is seen warping away");
        let cues = ticks(&mut space, 0.5, true);
        assert_eq!(cues, [PortalCue::EnterSound]);
        assert!(
            view_plane::view_plane_distance(&space.world_projection(projection()))
                < view_plane::view_plane_distance(&projection()),
            "the view widens"
        );
        let cues = ticks(&mut space, 60.0, true);
        assert!(cues.is_empty(), "{cues:?}");
        assert_eq!(space.state(), TeleportAnimState::Tunnel);
        assert!(space.hides_world());
        space.reset(None);
        assert!(!space.is_active() && !space.hides_world());
    }

    /// OpenAC `ReadinessHold_NeverRevealsIncompleteWorldAndShowsWaitCueAfterFiveSeconds`
    /// for the login: its wait cue's clock runs from the enter request (an
    /// adopted tunnel keeps it), and it is reported once the reveal is held.
    #[test]
    fn the_login_wait_cue_counts_from_the_enter_request() {
        let mut space = PortalSpace::new();
        space.on_event(PortalSpaceEvent::LoginEntering, &projection());
        assert!(
            !ticks(&mut space, 4.0, false)
                .iter()
                .any(|c| matches!(c, PortalCue::RevealWaited { .. }))
        );
        space.on_event(login_destination(8, CELL), &projection());
        let cues = ticks(&mut space, 1.1, false);
        let waits: Vec<_> = cues
            .iter()
            .filter_map(|c| match c {
                PortalCue::RevealWaited { generation, waited } => Some((*generation, *waited)),
                _ => None,
            })
            .collect();
        assert_eq!(waits.len(), 1, "{cues:?}");
        assert_eq!(waits[0].0, 8);
        assert!(waits[0].1 >= RETAIL_WAIT_CUE_DELAY);
        assert!(
            !ticks(&mut space, 10.0, false)
                .iter()
                .any(|c| matches!(c, PortalCue::RevealWaited { .. })),
            "once"
        );
    }

    #[test]
    fn an_interior_destination_reports_no_render_radius() {
        let found = ReadinessSnapshot {
            destination_cell: 0xA9B4_0105,
            is_indoor: true,
            collision_ready: true,
            composites_ready: true,
            render_ready: true,
            ..ReadinessSnapshot::default()
        };
        let readiness = destination_readiness(6, &found);
        assert_eq!(readiness.required_render_radius, 0);
        assert!(readiness.is_indoor && readiness.is_ready());
    }
}
