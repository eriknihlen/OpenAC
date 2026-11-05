//! The timing of portal space: which of the seven states the presentation
//! is in, how far the view-plane warp has gone, and the one-off edges (the
//! enter and exit sounds, the tunnel replacing the world, the placement, the
//! end).
//!
//! A portal, login or death transit starts in the tunnel; a log out first
//! warps the world away. The tunnel holds until the destination is ready,
//! then plays on for at least two seconds, leaving inside a narrow window of
//! the tunnel animation (so the outgoing warp ends as the animation does),
//! or after five seconds at most. Every "fade" is the view-plane warp, one
//! second long, eased by the game's cumulative sine table.

/// The presentation's state.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum TeleportAnimState {
    /// No portal space.
    #[default]
    Off = 0,
    /// The world warps away (log out only).
    WorldFadeOut = 1,
    /// The tunnel warps in.
    TunnelFadeIn = 2,
    /// The tunnel, until the destination is ready.
    Tunnel = 3,
    /// The tunnel plays on after the destination is ready.
    TunnelContinue = 4,
    /// The tunnel warps away.
    TunnelFadeOut = 5,
    /// The destination warps in.
    WorldFadeIn = 6,
}

/// Why portal space began; it picks the starting state.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TeleportEntryKind {
    /// A portal or a recall.
    Portal,
    /// Entering the world.
    Login,
    /// A death.
    Death,
    /// Leaving the world.
    Logout,
}

/// A one-off edge of a tick.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TeleportAnimEvent {
    /// The first tick after a begin: the enter-portal sound.
    PlayEnterSound,
    /// The first tick of a tunnel state: the tunnel replaces the world.
    EnterTunnel,
    /// The destination is ready: place the player.
    Place,
    /// The tunnel warp ended: the exit-portal sound, the world is back.
    PlayExitSound,
    /// The destination warp ended: the server hears login complete.
    FireLoginComplete,
}

/// What a tick leaves the presentation with.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct TeleportAnimSnapshot {
    /// The state.
    pub state: TeleportAnimState,
    /// The view-plane warp: 0 is the game's own distance, 1 the transition
    /// distance.
    pub view_plane_blend: f32,
    /// The tunnel is drawn (the fade-in through the fade-out).
    pub show_tunnel: bool,
    /// The wait line shows (the continue state only).
    pub show_please_wait: bool,
}

/// One warp, seconds.
pub const FADE_TIME: f32 = 1.0;
/// The least the tunnel plays on after the destination is ready, seconds.
pub const MIN_CONTINUE: f32 = 2.0;
/// The most the tunnel plays on, seconds.
pub const MAX_CONTINUE: f32 = 5.0;
/// The tunnel animation's rate, frames per second.
pub const TUNNEL_FRAMES_PER_SECOND: f32 = 40.0;
/// The tunnel animation frame the outgoing warp is timed to end on.
pub const TUNNEL_END_FRAME: i32 = 120;
/// The shortest remaining tunnel time the outgoing warp may start at.
pub const EXIT_WINDOW_LOW: f32 = FADE_TIME + 0.1;
/// The longest remaining tunnel time the outgoing warp may start at.
pub const EXIT_WINDOW_HIGH: f32 = FADE_TIME + 0.3;
/// The last eased level an outgoing view is still drawn at; past it the
/// view is replaced.
pub const LAST_VISIBLE_OUTGOING_ANIMATION_LEVEL: i16 = 1001;

/// The game's easing table: 100 cumulative samples of a sine half-wave,
/// scaled to 0..=1024.
fn retail_animation_levels() -> [i16; 100] {
    // The game's own truncated pi, not the true one.
    #[allow(clippy::approx_constant)]
    const RETAIL_PI: f64 = 3.141_592;
    const RECIPROCAL_99: f64 = 0.010_101_010_101_010_102;
    const SCALE: f64 = 1024.0;
    let mut samples = [0i16; 100];
    let mut total: i32 = 0;
    for (i, sample) in samples.iter_mut().enumerate() {
        *sample = (((i as f64) * RETAIL_PI * RECIPROCAL_99).sin() * SCALE) as i16;
        total += i32::from(*sample);
    }
    let mut running: i32 = 0;
    for sample in &mut samples {
        running += i32::from(*sample);
        *sample = ((running << 10) / total) as i16;
    }
    samples
}

/// The eased level, 0..=1024, at `t` (0..1) through a transition.
#[must_use]
pub fn retail_animation_level(t: f32) -> i16 {
    use std::sync::OnceLock;
    static LEVELS: OnceLock<[i16; 100]> = OnceLock::new();
    let levels = LEVELS.get_or_init(retail_animation_levels);
    let t = t.clamp(0.0, 1.0);
    let negative_index = (-99.0 * f64::from(t)).trunc() as i32;
    levels[negative_index.unsigned_abs() as usize]
}

/// The state machine.
#[derive(Debug, Clone, Default)]
pub struct TeleportAnimSequencer {
    state: TeleportAnimState,
    elapsed: f32,
    continue_elapsed: f32,
    enter_sound_pending: bool,
    enter_tunnel_pending: bool,
}

impl TeleportAnimSequencer {
    /// Off.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Not off.
    #[must_use]
    pub fn is_active(&self) -> bool {
        self.state != TeleportAnimState::Off
    }

    /// The state.
    #[must_use]
    pub fn state(&self) -> TeleportAnimState {
        self.state
    }

    /// Start: a log out warps the world away first; every other kind starts
    /// in the tunnel.
    pub fn begin(&mut self, kind: TeleportEntryKind) {
        self.state = if kind == TeleportEntryKind::Logout {
            TeleportAnimState::WorldFadeOut
        } else {
            TeleportAnimState::Tunnel
        };
        self.elapsed = 0.0;
        self.continue_elapsed = 0.0;
        self.enter_sound_pending = true;
        self.enter_tunnel_pending = self.state == TeleportAnimState::Tunnel;
    }

    /// Off, dropping pending edges.
    pub fn reset(&mut self) {
        *self = Self::default();
    }

    /// Advance `dt` seconds. `world_ready`: the destination may be shown;
    /// `tunnel_animation_frame`: the tunnel animation's current frame;
    /// `hold_in_tunnel`: a diagnostic hold that keeps a ready tunnel.
    pub fn tick(
        &mut self,
        dt: f32,
        world_ready: bool,
        tunnel_animation_frame: i32,
        hold_in_tunnel: bool,
    ) -> (TeleportAnimSnapshot, Vec<TeleportAnimEvent>) {
        let mut events = Vec::new();
        if self.enter_sound_pending {
            events.push(TeleportAnimEvent::PlayEnterSound);
            self.enter_sound_pending = false;
        }
        if self.enter_tunnel_pending {
            events.push(TeleportAnimEvent::EnterTunnel);
            self.enter_tunnel_pending = false;
        }
        self.elapsed += dt;
        match self.state {
            TeleportAnimState::WorldFadeOut => {
                if self.outgoing_viewport_reached_terminal_projection() {
                    self.advance(TeleportAnimState::TunnelFadeIn, true);
                }
            }
            TeleportAnimState::TunnelFadeIn => {
                if self.elapsed >= FADE_TIME {
                    self.advance(TeleportAnimState::Tunnel, false);
                }
            }
            TeleportAnimState::Tunnel => {
                if world_ready && !hold_in_tunnel {
                    events.push(TeleportAnimEvent::Place);
                    self.advance(TeleportAnimState::TunnelContinue, false);
                    self.continue_elapsed = 0.0;
                }
            }
            TeleportAnimState::TunnelContinue => {
                self.continue_elapsed += dt;
                // A frame past the end wraps to a huge remainder, as the
                // game's unsigned subtraction does.
                let remaining_frames =
                    (TUNNEL_END_FRAME as u32).wrapping_sub(tunnel_animation_frame as u32);
                let remaining_seconds = remaining_frames as f32 / TUNNEL_FRAMES_PER_SECOND;
                let min_met = self.continue_elapsed >= MIN_CONTINUE
                    && remaining_seconds > EXIT_WINDOW_LOW
                    && remaining_seconds < EXIT_WINDOW_HIGH;
                let max_force = self.continue_elapsed >= MAX_CONTINUE;
                if min_met || max_force {
                    self.advance(TeleportAnimState::TunnelFadeOut, false);
                }
            }
            TeleportAnimState::TunnelFadeOut => {
                if self.outgoing_viewport_reached_terminal_projection() {
                    self.advance(TeleportAnimState::WorldFadeIn, false);
                    events.push(TeleportAnimEvent::PlayExitSound);
                }
            }
            TeleportAnimState::WorldFadeIn => {
                if self.elapsed >= FADE_TIME {
                    self.advance(TeleportAnimState::Off, false);
                    events.push(TeleportAnimEvent::FireLoginComplete);
                }
            }
            TeleportAnimState::Off => {}
        }
        (self.snapshot(), events)
    }

    fn outgoing_viewport_reached_terminal_projection(&self) -> bool {
        retail_animation_level(self.elapsed / FADE_TIME) > LAST_VISIBLE_OUTGOING_ANIMATION_LEVEL
    }

    fn advance(&mut self, next: TeleportAnimState, enter_tunnel: bool) {
        self.state = next;
        self.elapsed = 0.0;
        if enter_tunnel {
            self.enter_tunnel_pending = true;
        }
    }

    fn snapshot(&self) -> TeleportAnimSnapshot {
        use TeleportAnimState as S;
        TeleportAnimSnapshot {
            state: self.state,
            view_plane_blend: view_plane_blend(self.state, self.elapsed),
            show_tunnel: matches!(
                self.state,
                S::TunnelFadeIn | S::Tunnel | S::TunnelContinue | S::TunnelFadeOut
            ),
            show_please_wait: self.state == S::TunnelContinue,
        }
    }
}

/// The warp of `state` after `elapsed` seconds in it.
fn view_plane_blend(state: TeleportAnimState, elapsed: f32) -> f32 {
    let t = (elapsed / FADE_TIME).clamp(0.0, 1.0);
    let level = f32::from(retail_animation_level(t)) / 1024.0;
    match state {
        // The game's distance toward the transition distance.
        TeleportAnimState::WorldFadeOut | TeleportAnimState::TunnelFadeOut => level,
        // The transition distance back to the game's.
        TeleportAnimState::TunnelFadeIn | TeleportAnimState::WorldFadeIn => 1.0 - level,
        TeleportAnimState::Tunnel | TeleportAnimState::TunnelContinue | TeleportAnimState::Off => {
            0.0
        }
    }
}

#[cfg(test)]
#[path = "sequencer_tests.rs"]
mod tests;
