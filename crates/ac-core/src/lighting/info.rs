//! Lights described by a model (torches, lamps, glowing items).

use glam::{Mat4, Quat, Vec3};

use super::source::{LightKind, LightSource};

/// Reach factor of a static light: reach = falloff × 1.3.
pub const STATIC_LIGHT_RANGE_FACTOR: f32 = 1.3;
/// Reach factor of a dynamic light: reach = falloff × 1.5.
pub const DYNAMIC_LIGHT_RANGE_FACTOR: f32 = 1.5;

/// One light of a model, as stored: the caller copies each entry of the
/// setup's light list (in stored order) into this.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct LightInfoSource {
    /// Position relative to the model.
    pub origin: Vec3,
    /// Rotation relative to the model.
    pub orientation: Quat,
    /// Colour, red, green, blue bytes.
    pub color: [u8; 3],
    /// Brightness.
    pub intensity: f32,
    /// Falloff distance, metres.
    pub falloff: f32,
    /// Cone angle, radians; above 0 makes a spot light.
    pub cone_angle: f32,
}

/// Where the lights' owner stands and how they behave.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct LightOwnerPlacement {
    /// Owner object id.
    pub owner_id: u32,
    /// Owner position.
    pub position: Vec3,
    /// Owner rotation.
    pub rotation: Quat,
    /// Dynamic lights (on a live object) rather than static ones.
    pub is_dynamic: bool,
    /// The cell the owner is in.
    pub cell_id: u32,
    /// The lights follow the owner's pose every frame.
    pub tracks_owner_pose: bool,
}

/// World-space lights for a model's light list placed at the owner's
/// frame. Each light is ranked by the owner's position; its forward is the
/// light's +y axis.
#[must_use]
pub fn load_lights(lights: &[LightInfoSource], owner: &LightOwnerPlacement) -> Vec<LightSource> {
    let root_world = Mat4::from_rotation_translation(owner.rotation, owner.position);
    lights
        .iter()
        .map(|info| {
            let local_pose = Mat4::from_rotation_translation(info.orientation, info.origin);
            let light_world = root_world * local_pose;
            let world_position = light_world.w_axis.truncate();
            let mut forward = light_world.transform_vector3(Vec3::Y);
            if forward.length_squared() > 1e-8 {
                forward = forward.normalize();
            }
            let factor = if owner.is_dynamic {
                DYNAMIC_LIGHT_RANGE_FACTOR
            } else {
                STATIC_LIGHT_RANGE_FACTOR
            };
            LightSource {
                kind: if info.cone_angle > 0.0 {
                    LightKind::Spot
                } else {
                    LightKind::Point
                },
                world_position,
                world_forward: forward,
                color_linear: Vec3::new(
                    f32::from(info.color[0]) / 255.0,
                    f32::from(info.color[1]) / 255.0,
                    f32::from(info.color[2]) / 255.0,
                ),
                intensity: info.intensity,
                range: info.falloff * factor,
                cone_angle: info.cone_angle,
                owner_id: owner.owner_id,
                cell_id: owner.cell_id,
                is_lit: true,
                is_dynamic: owner.is_dynamic,
                tracks_owner_pose: owner.tracks_owner_pose,
                local_pose,
                ..LightSource::default()
            }
            .with_ranking_origin(owner.position)
        })
        .collect()
}

#[cfg(test)]
mod tests {
    //! OpenAC `LightInfoLoaderTests`.
    use super::*;

    fn owner(position: Vec3, is_dynamic: bool) -> LightOwnerPlacement {
        LightOwnerPlacement {
            owner_id: 77,
            position,
            rotation: Quat::IDENTITY,
            is_dynamic,
            cell_id: 0,
            tracks_owner_pose: false,
        }
    }

    fn info(
        origin: Vec3,
        color: [u8; 3],
        intensity: f32,
        falloff: f32,
        cone: f32,
    ) -> LightInfoSource {
        LightInfoSource {
            origin,
            orientation: Quat::IDENTITY,
            color,
            intensity,
            falloff,
            cone_angle: cone,
        }
    }

    #[test]
    fn empty_lights_give_nothing() {
        assert!(load_lights(&[], &owner(Vec3::ZERO, false)).is_empty());
    }

    #[test]
    fn a_point_light_is_placed_and_scaled() {
        let lights = load_lights(
            &[info(
                Vec3::new(1.0, 2.0, 3.0),
                [255, 200, 50],
                0.8,
                8.0,
                0.0,
            )],
            &owner(Vec3::new(100.0, 200.0, 300.0), false),
        );
        assert_eq!(lights.len(), 1);
        let light = &lights[0];
        assert_eq!(light.kind, LightKind::Point);
        assert_eq!(light.owner_id, 77);
        assert!((light.range - 10.4).abs() < 1e-3);
        assert_eq!(light.intensity, 0.8);
        assert_eq!(light.world_position, Vec3::new(101.0, 202.0, 303.0));
        assert_eq!(light.ranking_point(), Vec3::new(100.0, 200.0, 300.0));
        assert_eq!(light.local_pose.w_axis.truncate(), Vec3::new(1.0, 2.0, 3.0));
        assert!((light.color_linear.x - 1.0).abs() < 0.01);
    }

    #[test]
    fn a_dynamic_light_uses_the_hardware_range_factor() {
        let lights = load_lights(
            &[info(Vec3::ZERO, [255, 255, 255], 1.0, 8.0, 0.0)],
            &owner(Vec3::ZERO, true),
        );
        assert!(lights[0].is_dynamic);
        assert!((lights[0].range - 12.0).abs() < 1e-3);
    }

    #[test]
    fn a_cone_angle_makes_a_spot_light() {
        let lights = load_lights(
            &[info(Vec3::ZERO, [255, 255, 255], 1.0, 5.0, 0.5)],
            &owner(Vec3::ZERO, false),
        );
        assert_eq!(lights[0].kind, LightKind::Spot);
        assert_eq!(lights[0].cone_angle, 0.5);
    }

    #[test]
    fn the_owner_rotation_turns_offset_and_forward() {
        // Owner turned 90 degrees about z: the local +x offset points +y and
        // the light's forward (+y) points -x.
        let placement = LightOwnerPlacement {
            rotation: Quat::from_rotation_z(std::f32::consts::FRAC_PI_2),
            ..owner(Vec3::new(10.0, 0.0, 0.0), false)
        };
        let light = load_lights(&[info(Vec3::X, [255, 255, 255], 1.0, 1.0, 0.0)], &placement)[0];
        assert!((light.world_position - Vec3::new(10.0, 1.0, 0.0)).length() < 1e-5);
        assert!((light.world_forward - Vec3::new(-1.0, 0.0, 0.0)).length() < 1e-5);
    }
}
