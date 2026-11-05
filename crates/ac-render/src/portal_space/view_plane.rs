//! The view-plane warp of portal space. The game describes its field of
//! view as a "view-plane distance": how far the picture plane sits in front
//! of the eye for a picture one unit tall, `cot(fov / 2)`. A warp slides that
//! distance between the game's own and a tiny one (0.001), which widens the
//! view to almost 180 degrees: the world (or the tunnel) shrinks into the
//! centre and grows back out of it.

use glam::Mat4;

use super::sequencer::{TeleportAnimSnapshot, TeleportAnimState};

/// The distance a warp slides to (an almost 180 degree view).
pub const TRANSITION_VIEW_PLANE_DISTANCE: f32 = 0.001;

/// The far plane when a projection does not say, metres.
const FALLBACK_FAR: f32 = 5000.0;

/// A projection's view-plane distance (its vertical scale).
#[must_use]
pub fn view_plane_distance(projection: &Mat4) -> f32 {
    projection.y_axis.y
}

/// A projection's near plane, metres (NaN when it has none).
#[must_use]
pub fn near_plane(projection: &Mat4) -> f32 {
    if projection.z_axis.z == 0.0 {
        f32::NAN
    } else {
        projection.w_axis.z / projection.z_axis.z
    }
}

/// A projection's far plane, metres (NaN when it has none).
#[must_use]
pub fn far_plane(projection: &Mat4) -> f32 {
    if projection.z_axis.z == -1.0 {
        f32::NAN
    } else {
        projection.w_axis.z / (projection.z_axis.z + 1.0)
    }
}

/// A right-handed perspective with depth 0..1, the window's projection kind.
#[must_use]
pub fn perspective(fov_y: f32, aspect: f32, near: f32, far: f32) -> Mat4 {
    glam::camera::rh::proj::directx::perspective(fov_y, aspect, near, far)
}

/// The warp, from the game's projection captured when portal space began.
#[derive(Debug, Clone)]
pub struct TeleportViewPlane {
    game_view_plane_distance: f32,
    state: TeleportAnimState,
    enabled: bool,
    current: f32,
}

impl Default for TeleportViewPlane {
    fn default() -> Self {
        Self {
            game_view_plane_distance: 1.0,
            state: TeleportAnimState::Off,
            enabled: false,
            current: 1.0,
        }
    }
}

impl TeleportViewPlane {
    /// Off.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// The warp changes the projection.
    #[must_use]
    pub fn enabled(&self) -> bool {
        self.enabled
    }

    /// The view-plane distance now.
    #[must_use]
    pub fn current_view_plane_distance(&self) -> f32 {
        self.current
    }

    /// Capture the game's distance from its projection.
    pub fn begin(&mut self, game_projection: &Mat4) {
        let mut distance = view_plane_distance(game_projection);
        if !distance.is_finite() || distance <= 0.0 {
            distance = 1.0;
        }
        self.game_view_plane_distance = distance;
        self.current = distance;
        self.state = TeleportAnimState::Off;
        self.enabled = false;
    }

    /// Follow the sequencer's snapshot.
    pub fn update(&mut self, snapshot: &TeleportAnimSnapshot) {
        self.state = snapshot.state;
        match snapshot.state {
            TeleportAnimState::WorldFadeOut
            | TeleportAnimState::TunnelFadeIn
            | TeleportAnimState::TunnelFadeOut
            | TeleportAnimState::WorldFadeIn => {
                self.enabled = true;
                self.current = lerp(
                    self.game_view_plane_distance,
                    TRANSITION_VIEW_PLANE_DISTANCE,
                    snapshot.view_plane_blend,
                );
            }
            TeleportAnimState::Tunnel => {
                if self.enabled {
                    self.current = self.game_view_plane_distance;
                }
            }
            TeleportAnimState::TunnelContinue | TeleportAnimState::Off => {
                self.enabled = false;
                self.current = self.game_view_plane_distance;
            }
        }
    }

    /// Off again.
    pub fn reset(&mut self) {
        self.state = TeleportAnimState::Off;
        self.enabled = false;
        self.current = self.game_view_plane_distance;
    }

    /// `base` with the warp's field of view and near plane (its aspect and
    /// far plane kept); `base` itself when the warp is off.
    #[must_use]
    pub fn apply(&self, base: Mat4) -> Mat4 {
        if !self.enabled {
            return base;
        }
        let mut aspect = if base.x_axis.x == 0.0 {
            1.0
        } else {
            base.y_axis.y / base.x_axis.x
        };
        let mut far = if base.z_axis.z == -1.0 {
            FALLBACK_FAR
        } else {
            base.w_axis.z / (base.z_axis.z + 1.0)
        };
        if !aspect.is_finite() || aspect <= 0.0 {
            aspect = 1.0;
        }
        if !far.is_finite() || far <= 0.1 {
            far = FALLBACK_FAR;
        }
        let distance = self.current.max(TRANSITION_VIEW_PLANE_DISTANCE);
        let fov = 2.0 * (1.0 / distance).atan();
        let mut near = self.world_transition_near_plane(distance);
        if near >= far {
            near = 0.1f32.min(far * 0.5);
        }
        perspective(fov, aspect, near, far)
    }

    /// The near plane at `distance`: a quarter of it, at least 0.1 m, except
    /// for the world's own warps at small distances, which come much nearer
    /// so the world still covers the screen.
    fn world_transition_near_plane(&self, distance: f32) -> f32 {
        let world = matches!(
            self.state,
            TeleportAnimState::WorldFadeOut | TeleportAnimState::WorldFadeIn
        );
        if world && distance < 0.4 {
            0.0001f32.max(distance * 0.25)
        } else {
            0.1f32.max(distance * 0.25)
        }
    }
}

fn lerp(from: f32, to: f32, amount: f32) -> f32 {
    from + (to - from) * amount.clamp(0.0, 1.0)
}

#[cfg(test)]
mod tests {
    //! OpenAC `PortalTunnelAssetTests` (the `TeleportViewPlane_*` cases).
    use super::*;
    use std::f32::consts::PI;

    fn snapshot(state: TeleportAnimState, blend: f32) -> TeleportAnimSnapshot {
        TeleportAnimSnapshot {
            state,
            view_plane_blend: blend,
            show_tunnel: false,
            show_please_wait: false,
        }
    }

    fn base() -> Mat4 {
        perspective(PI / 3.0, 16.0 / 9.0, 0.1, 5000.0)
    }

    fn close(a: f32, b: f32, eps: f32) -> bool {
        (a - b).abs() <= eps
    }

    #[test]
    fn fade_out_ports_retail_distance_fov_and_near_plane() {
        let base = base();
        let mut c = TeleportViewPlane::new();
        c.begin(&base);
        c.update(&snapshot(TeleportAnimState::TunnelFadeOut, 0.5));
        let projection = c.apply(base);
        let game = view_plane_distance(&base);
        let expected = game + (TRANSITION_VIEW_PLANE_DISTANCE - game) * 0.5;
        assert!(c.enabled());
        assert!(close(c.current_view_plane_distance(), expected, 1e-5));
        assert!(close(view_plane_distance(&projection), expected, 1e-5));
        assert!(close(
            near_plane(&projection),
            0.1f32.max(expected * 0.25),
            1e-5
        ));
    }

    #[test]
    fn world_fade_in_starts_at_retail_wide_projection_and_restores_game_projection() {
        let base = base();
        let mut c = TeleportViewPlane::new();
        c.begin(&base);
        c.update(&snapshot(TeleportAnimState::WorldFadeIn, 1.0));
        let wide = c.apply(base);
        assert!(close(
            view_plane_distance(&wide),
            TRANSITION_VIEW_PLANE_DISTANCE,
            1e-5
        ));
        assert!(close(near_plane(&wide), 0.00025, 1e-7));
        c.update(&snapshot(TeleportAnimState::Off, 0.0));
        assert!(!c.enabled());
        assert_eq!(c.apply(base), base);
    }

    #[test]
    fn tunnel_keeps_retail_near_plane_while_world_uses_coverage_near_plane() {
        let base = base();
        let mut c = TeleportViewPlane::new();
        c.begin(&base);
        c.update(&snapshot(TeleportAnimState::TunnelFadeOut, 1.0));
        let tunnel = c.apply(base);
        c.update(&snapshot(TeleportAnimState::WorldFadeIn, 1.0));
        let world = c.apply(base);
        assert!(close(near_plane(&tunnel), 0.1, 1e-5));
        assert!(close(near_plane(&world), 0.00025, 1e-7));
        assert_eq!(tunnel.x_axis.x, world.x_axis.x);
        assert_eq!(tunnel.y_axis.y, world.y_axis.y);
    }

    #[test]
    fn tunnel_preserves_logout_override_until_tunnel_continue() {
        let captured = base();
        let changed = perspective(PI / 2.0, 16.0 / 9.0, 0.1, 5000.0);
        let mut c = TeleportViewPlane::new();
        c.begin(&captured);
        c.update(&snapshot(TeleportAnimState::WorldFadeOut, 1.0));
        c.update(&snapshot(TeleportAnimState::TunnelFadeIn, 0.0));
        c.update(&snapshot(TeleportAnimState::Tunnel, 0.0));
        assert!(c.enabled());
        assert!(close(
            view_plane_distance(&c.apply(changed)),
            view_plane_distance(&captured),
            1e-5
        ));
        c.update(&snapshot(TeleportAnimState::TunnelContinue, 0.0));
        assert!(!c.enabled());
        assert_eq!(c.apply(changed), changed);
    }

    #[test]
    fn normal_portal_tunnel_does_not_enable_override() {
        let base = base();
        let mut c = TeleportViewPlane::new();
        c.begin(&base);
        c.update(&snapshot(TeleportAnimState::Tunnel, 0.0));
        assert!(!c.enabled());
        assert_eq!(c.apply(base), base);
    }
}
