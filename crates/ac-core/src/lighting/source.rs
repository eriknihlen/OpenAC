//! A light in the world: the sun, a torch, a lamp, a spell glow.

use glam::{Mat4, Vec3};

/// Kinds of light.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum LightKind {
    /// The sun or moon: a direction, no position, unlimited reach.
    Directional = 0,
    /// A light shining in every direction from a point (torch, fireplace).
    #[default]
    Point = 1,
    /// A light shining in a cone.
    Spot = 2,
}

/// One light. Positions and directions are world space, metres, z up.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct LightSource {
    /// Kind.
    pub kind: LightKind,
    /// Where the light is.
    pub world_position: Vec3,
    /// The point lights are ranked by: the owner's root position, not the
    /// light's own offset position. Point and spot lights need one to be
    /// registered.
    pub ranking_origin: Option<Vec3>,
    /// Which way a spot or directional light points (unit length).
    pub world_forward: Vec3,
    /// Colour, linear RGB 0..1.
    pub color_linear: Vec3,
    /// Brightness multiplier.
    pub intensity: f32,
    /// Reach in metres: the dat falloff times 1.3 (static) or 1.5
    /// (dynamic).
    pub range: f32,
    /// Cone angle of a spot light, radians.
    pub cone_angle: f32,
    /// The object it belongs to; 0 for none.
    pub owner_id: u32,
    /// The cell it is in.
    pub cell_id: u32,
    /// On or off (an animation can switch an object's lights).
    pub is_lit: bool,
    /// A dynamic light (on a live object; lit by `1/d`) rather than a
    /// static one baked into walls (`1/d³`).
    pub is_dynamic: bool,
    /// Follows a moving owner: its world frame is recomputed from the
    /// owner's pose every frame.
    pub tracks_owner_pose: bool,
    /// The light's frame relative to its owner.
    pub local_pose: Mat4,
    /// Squared distance used by the last ranking.
    pub dist_sq: f32,
}

impl Default for LightSource {
    fn default() -> Self {
        Self {
            kind: LightKind::Point,
            world_position: Vec3::ZERO,
            ranking_origin: None,
            world_forward: Vec3::ZERO,
            color_linear: Vec3::ONE,
            intensity: 1.0,
            range: 10.0,
            cone_angle: 0.0,
            owner_id: 0,
            cell_id: 0,
            is_lit: true,
            is_dynamic: false,
            tracks_owner_pose: false,
            local_pose: Mat4::IDENTITY,
            dist_sq: 0.0,
        }
    }
}

impl LightSource {
    /// The ranking point, zero when none was given.
    #[must_use]
    pub fn ranking_point(&self) -> Vec3 {
        self.ranking_origin.unwrap_or(Vec3::ZERO)
    }

    /// This light with ranking point origin.
    #[must_use]
    pub fn with_ranking_origin(mut self, origin: Vec3) -> Self {
        self.ranking_origin = Some(origin);
        self
    }
}

/// The ambient light and sun the scene is lit with.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct CellAmbientState {
    /// Ambient colour.
    pub ambient_color: Vec3,
    /// Sunlight colour (black indoors).
    pub sun_color: Vec3,
    /// Direction the sunlight travels (from the sun to the world).
    pub sun_direction: Vec3,
}
