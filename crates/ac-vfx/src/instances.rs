//! What the renderer draws for the live particles: per particle either a
//! quad (center plus its two half-spans... see [`BillboardInstance`]) or a
//! whole particle model with a model matrix. Each comes with its distance
//! to the camera, and the frame lists them in the order to draw: far to
//! near, ties in emission order.
//!
//! Which form a particle takes comes from its model's first degrade entry:
//! mode 1 (or no degrade table) draws the full model; any other mode draws
//! a quad sized from the model's bounds, turned by the facing rules of
//! [`crate::facing`].

use std::collections::HashMap;
use std::fmt;
use std::sync::Arc;

use ac_dat::objects::{GfxObj, GfxObjFlags, SurfaceType};
use glam::{Mat4, Quat, Vec2, Vec3};

use crate::dats::VfxDats;
use crate::desc::{EmitterDesc, EmitterFlags, vec3};
use crate::facing;
use crate::particles::{EmitterHandle, Particle, ParticleEmitter};

/// How a particle model draws.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ParticleGeometryKind {
    /// The whole model.
    FullMesh,
    /// A quad.
    Billboard,
}

/// Mode 1, or no degrade table, draws the full model; any other first
/// degrade mode draws a quad.
#[must_use]
pub fn classify_particle_geometry(first_degrade_mode: Option<u32>) -> ParticleGeometryKind {
    match first_degrade_mode {
        Some(mode) if mode != 1 => ParticleGeometryKind::Billboard,
        _ => ParticleGeometryKind::FullMesh,
    }
}

/// How a particle model is drawn as a quad.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct ParticleGfxInfo {
    /// The surface to texture the quad with (0: none; the renderer draws a
    /// soft round sprite).
    pub surface_id: u32,
    /// Quad width and height at scale 1, metres.
    pub size: Vec2,
    /// The quad's X span direction in model space.
    pub axis_x: Vec3,
    /// The quad's Y span direction in model space.
    pub axis_y: Vec3,
    /// The model's bounds centre, model space (scaled by particle size).
    pub center_offset: Vec3,
    /// The model's sort centre, model space.
    pub sort_center: Vec3,
    /// Always faces the camera (mode 2 point sprites and descriptions
    /// without a model).
    pub is_billboard: bool,
    /// Additive blending (from the surface; the emitter flag when the
    /// model has no surface).
    pub additive: bool,
    /// The model has a surface.
    pub has_material: bool,
    /// The model's first degrade mode (facing rule).
    pub degrade_mode: u32,
}

impl ParticleGfxInfo {
    /// A unit, untextured, camera-facing quad.
    pub const DEFAULT: Self = Self::billboard(0, Vec2::ONE, Vec3::ZERO, Vec3::ZERO, false, false);

    const fn billboard(
        surface_id: u32,
        size: Vec2,
        center_offset: Vec3,
        sort_center: Vec3,
        additive: bool,
        has_material: bool,
    ) -> Self {
        Self {
            surface_id,
            size,
            axis_x: Vec3::X,
            axis_y: Vec3::Y,
            center_offset,
            sort_center,
            is_billboard: true,
            additive,
            has_material,
            degrade_mode: 2,
        }
    }

    /// The quad for particle model `gfx` with first degrade mode
    /// `first_degrade_mode`, textured with its first surface
    /// (`surface_additive`: that surface blends additively).
    #[must_use]
    pub fn from_gfx_obj(
        gfx: &GfxObj,
        first_degrade_mode: Option<u32>,
        surface_additive: bool,
    ) -> Self {
        let surface_id = gfx.surfaces.first().copied().unwrap_or(0);
        let has_material = surface_id != 0;
        let additive = has_material && surface_additive;
        let sort_center = vec3(gfx.sort_center);
        if gfx.vertices.vertices.is_empty() {
            return Self::billboard(
                surface_id,
                Vec2::ONE,
                Vec3::ZERO,
                sort_center,
                additive,
                has_material,
            );
        }

        let mut min = Vec3::splat(f32::INFINITY);
        let mut max = Vec3::splat(f32::NEG_INFINITY);
        for (_, v) in &gfx.vertices.vertices {
            let p = vec3(v.origin);
            min = min.min(p);
            max = max.max(p);
        }
        let size = max - min;
        let center = (min + max) * 0.5;
        if first_degrade_mode == Some(2) {
            // A point sprite: faces the camera, sized from its X/Z extent.
            let sx = fallback_extent(size.x) * 0.9;
            let sy = fallback_extent(size.z) * 0.9;
            return Self::billboard(
                surface_id,
                Vec2::new(sx, sy),
                center,
                sort_center,
                additive,
                has_material,
            );
        }

        // The plane of the two largest extents.
        let (axis_x, axis_y, plane) = if size.y > size.x && size.y > size.z {
            if size.x > size.z {
                (Vec3::X, Vec3::Y, Vec2::new(size.x, size.y))
            } else {
                (Vec3::Y, Vec3::Z, Vec2::new(size.y, size.z))
            }
        } else if size.x > size.y && size.x > size.z {
            if size.z > size.y {
                (Vec3::X, Vec3::Z, Vec2::new(size.x, size.z))
            } else {
                (Vec3::X, Vec3::Y, Vec2::new(size.x, size.y))
            }
        } else if size.x > size.y {
            (Vec3::X, Vec3::Z, Vec2::new(size.x, size.z))
        } else {
            (Vec3::Y, Vec3::Z, Vec2::new(size.y, size.z))
        };
        Self {
            surface_id,
            size: Vec2::new(fallback_extent(plane.x), fallback_extent(plane.y)),
            axis_x,
            axis_y,
            center_offset: center,
            sort_center,
            is_billboard: false,
            additive,
            has_material,
            degrade_mode: first_degrade_mode.unwrap_or(0),
        }
    }
}

fn fallback_extent(value: f32) -> f32 {
    if value > 1e-4 {
        value.clamp(1e-4, 10_000.0)
    } else {
        1.0
    }
}

/// Per particle model: its first degrade mode, its quad description and
/// its sort centre, read once from the dats.
pub struct ParticleGfxCache {
    dats: Option<Arc<dyn VfxDats>>,
    first_degrade_modes: HashMap<u32, Option<u32>>,
    infos: HashMap<u32, ParticleGfxInfo>,
    sort_centers: HashMap<u32, Vec3>,
    diagnostics: Vec<String>,
}

impl fmt::Debug for ParticleGfxCache {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("ParticleGfxCache")
            .field("infos", &self.infos.len())
            .finish_non_exhaustive()
    }
}

impl ParticleGfxCache {
    /// A cache reading the dats (without dats every particle is a default
    /// quad).
    #[must_use]
    pub fn new(dats: Option<Arc<dyn VfxDats>>) -> Self {
        Self {
            dats,
            first_degrade_modes: HashMap::new(),
            infos: HashMap::new(),
            sort_centers: HashMap::new(),
            diagnostics: Vec::new(),
        }
    }

    /// Messages about particle models that failed to decode.
    pub fn take_diagnostics(&mut self) -> Vec<String> {
        std::mem::take(&mut self.diagnostics)
    }

    /// The model's first degrade mode (`None`: no degrade table).
    pub fn first_degrade_mode(&mut self, gfx_obj_id: u32) -> Option<u32> {
        if let Some(mode) = self.first_degrade_modes.get(&gfx_obj_id) {
            return *mode;
        }
        let mode = self.read_first_degrade_mode(gfx_obj_id);
        self.first_degrade_modes.insert(gfx_obj_id, mode);
        mode
    }

    fn read_first_degrade_mode(&mut self, gfx_obj_id: u32) -> Option<u32> {
        let dats = self.dats.as_ref()?;
        let gfx = match dats.gfx_obj(gfx_obj_id) {
            Ok(gfx) => gfx?,
            Err(e) => {
                self.diagnostics.push(format!(
                    "[particle-geometry] Failed to decode GfxObj 0x{gfx_obj_id:08X} degrade metadata: {e}"
                ));
                return None;
            }
        };
        if !gfx.flags.contains(GfxObjFlags::HAS_DID_DEGRADE) || gfx.did_degrade == 0 {
            return None;
        }
        match dats.gfx_obj_degrade_info(gfx.did_degrade) {
            Ok(degrade) => degrade?.degrades.first().map(|d| d.degrade_mode),
            Err(e) => {
                self.diagnostics.push(format!(
                    "[particle-geometry] Failed to decode GfxObj 0x{gfx_obj_id:08X} degrade metadata: {e}"
                ));
                None
            }
        }
    }

    /// How the model draws.
    pub fn geometry_kind(&mut self, gfx_obj_id: u32) -> ParticleGeometryKind {
        classify_particle_geometry(self.first_degrade_mode(gfx_obj_id))
    }

    /// The quad description of the emitter's particles.
    pub fn gfx_info(&mut self, desc: &EmitterDesc) -> ParticleGfxInfo {
        if desc.texture_surface_id != 0 {
            return ParticleGfxInfo::billboard(
                desc.texture_surface_id,
                Vec2::ONE,
                Vec3::ZERO,
                Vec3::ZERO,
                desc.flags.contains(EmitterFlags::ADDITIVE),
                false,
            );
        }
        let gfx_obj_id = particle_gfx_obj_id(desc);
        if gfx_obj_id == 0 || self.dats.is_none() {
            return ParticleGfxInfo::DEFAULT;
        }
        if let Some(info) = self.infos.get(&gfx_obj_id) {
            return *info;
        }
        let info = self.read_gfx_info(gfx_obj_id);
        self.infos.insert(gfx_obj_id, info);
        info
    }

    fn read_gfx_info(&mut self, gfx_obj_id: u32) -> ParticleGfxInfo {
        let mode = self.first_degrade_mode(gfx_obj_id);
        let Some(dats) = self.dats.as_ref() else {
            return ParticleGfxInfo::DEFAULT;
        };
        let Ok(Some(gfx)) = dats.gfx_obj(gfx_obj_id) else {
            return ParticleGfxInfo::DEFAULT;
        };
        let surface_additive = gfx
            .surfaces
            .first()
            .and_then(|&id| dats.surface(id).ok().flatten())
            .is_some_and(|s| s.surface_type.0 & SurfaceType::ADDITIVE != 0);
        ParticleGfxInfo::from_gfx_obj(&gfx, mode, surface_additive)
    }

    /// The sort centre of a full particle model, model space.
    pub fn sort_center(&mut self, gfx_obj_id: u32) -> Vec3 {
        if let Some(center) = self.sort_centers.get(&gfx_obj_id) {
            return *center;
        }
        let center = self
            .dats
            .as_ref()
            .and_then(|d| d.gfx_obj(gfx_obj_id).ok().flatten())
            .map_or(Vec3::ZERO, |g| vec3(g.sort_center));
        self.sort_centers.insert(gfx_obj_id, center);
        center
    }
}

/// The model an emitter's particles draw with: the hardware model, else the
/// software one.
#[must_use]
pub fn particle_gfx_obj_id(desc: &EmitterDesc) -> u32 {
    if desc.hw_gfx_obj_id != 0 {
        desc.hw_gfx_obj_id
    } else {
        desc.gfx_obj_id
    }
}

/// A particle's world orientation: the emitter's live orientation for
/// attached particles, else the one it was born with; the spinning types
/// also turn by `c` (radians per second, axis-angle) times the age.
#[must_use]
pub fn particle_orientation(em: &ParticleEmitter, p: &Particle) -> Quat {
    let mut orientation = if em.desc.flags.contains(EmitterFlags::ATTACH_LOCAL) {
        em.anchor_rot
    } else {
        p.spawn_rotation
    };
    if em.desc.particle_type.spins() {
        let angular = p.c * p.age;
        let radians = angular.length();
        if radians > 1e-6 {
            orientation =
                (orientation * Quat::from_axis_angle(angular / radians, radians)).normalize();
        }
    }
    orientation
}

/// One particle drawn as a quad: the corners are
/// `center ± axis_x / 2 ± axis_y / 2` (a unit quad from -0.5 to 0.5 scaled
/// by the two spans). Texture coordinates 0..1 run along the spans.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct BillboardInstance {
    /// Centre, world.
    pub center: Vec3,
    /// Full X span, world (direction and length).
    pub axis_x: Vec3,
    /// Full Y span, world.
    pub axis_y: Vec3,
    /// Colour and opacity, ARGB (multiplies the texture).
    pub color_argb: u32,
    /// Texture surface (0: none, a soft round sprite).
    pub surface_id: u32,
    /// Blend additively (else straight alpha).
    pub additive: bool,
    /// Squared distance from the camera to the particle's sort point.
    pub distance_sq: f32,
    /// The emitter it belongs to.
    pub emitter: EmitterHandle,
}

/// One particle drawn as its whole model.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct MeshInstance {
    /// Model to world: uniform scale by the particle size, rotation,
    /// translation.
    pub model: Mat4,
    /// Colour and opacity, ARGB (multiplies every surface).
    pub color_argb: u32,
    /// The particle model.
    pub gfx_obj_id: u32,
    /// Squared distance from the camera to the model's sort centre.
    pub distance_sq: f32,
    /// The emitter it belongs to.
    pub emitter: EmitterHandle,
}

/// Which list a submission points into.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ParticleSubmissionKind {
    /// [`ParticleFrame::billboards`].
    Billboard,
    /// [`ParticleFrame::meshes`].
    Mesh,
}

/// One draw in order.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct ParticleSubmission {
    /// Which list.
    pub kind: ParticleSubmissionKind,
    /// Index in that list.
    pub index: usize,
    /// Squared camera distance.
    pub distance_sq: f32,
    /// Emission order (emitters in spawn order, particles in slot order).
    pub sequence: u32,
}

/// The camera the instances are built for.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct ParticleCamera {
    /// Position, world.
    pub position: Vec3,
    /// Right, world (unit).
    pub right: Vec3,
    /// Up, world (unit).
    pub up: Vec3,
}

/// A frame's particle draw lists.
#[derive(Debug, Default, Clone)]
pub struct ParticleFrame {
    /// Quads.
    pub billboards: Vec<BillboardInstance>,
    /// Full models.
    pub meshes: Vec<MeshInstance>,
    /// Every draw, in emission order until [`Self::sort_far_to_near`].
    pub submissions: Vec<ParticleSubmission>,
}

impl ParticleFrame {
    /// Empty the lists, keeping their capacity.
    pub fn clear(&mut self) {
        self.billboards.clear();
        self.meshes.clear();
        self.submissions.clear();
    }

    /// Put the submissions in draw order: far to near, equal distances in
    /// emission order.
    pub fn sort_far_to_near(&mut self) {
        sort_submissions(&mut self.submissions);
    }
}

/// Far to near; equal distances keep emission order.
pub fn sort_submissions(submissions: &mut [ParticleSubmission]) {
    submissions.sort_by(|a, b| {
        b.distance_sq
            .total_cmp(&a.distance_sq)
            .then(a.sequence.cmp(&b.sequence))
    });
}

/// Append the instances of every live particle of `emitters` (typically
/// [`crate::ParticleSystem::renderable_emitters`] for one pass, or a scoped
/// copy) to `frame`, seen from `camera`. Call
/// [`ParticleFrame::sort_far_to_near`] before drawing.
pub fn build_particle_frame<'a>(
    emitters: impl IntoIterator<Item = &'a ParticleEmitter>,
    camera: &ParticleCamera,
    gfx: &mut ParticleGfxCache,
    frame: &mut ParticleFrame,
) {
    let mut sequence = frame.submissions.len() as u32;
    for em in emitters {
        let gfx_obj_id = particle_gfx_obj_id(&em.desc);
        let full_mesh =
            gfx_obj_id != 0 && gfx.geometry_kind(gfx_obj_id) == ParticleGeometryKind::FullMesh;
        let mut info = None;
        let mut sort_center = None;
        for p in em.particles.iter().filter(|p| p.alive) {
            if full_mesh {
                let orientation = particle_orientation(em, p);
                let model = Mat4::from_scale_rotation_translation(
                    Vec3::splat(p.size),
                    orientation,
                    p.position,
                );
                let center = *sort_center.get_or_insert_with(|| gfx.sort_center(gfx_obj_id));
                let distance_sq = model
                    .transform_point3(center)
                    .distance_squared(camera.position);
                frame.submissions.push(ParticleSubmission {
                    kind: ParticleSubmissionKind::Mesh,
                    index: frame.meshes.len(),
                    distance_sq,
                    sequence,
                });
                frame.meshes.push(MeshInstance {
                    model,
                    color_argb: p.color_argb,
                    gfx_obj_id,
                    distance_sq,
                    emitter: em.handle,
                });
                sequence += 1;
                continue;
            }

            let info = *info.get_or_insert_with(|| gfx.gfx_info(&em.desc));
            let instance = billboard_instance(em, p, &info, camera);
            frame.submissions.push(ParticleSubmission {
                kind: ParticleSubmissionKind::Billboard,
                index: frame.billboards.len(),
                distance_sq: instance.distance_sq,
                sequence,
            });
            frame.billboards.push(instance);
            sequence += 1;
        }
    }
}

fn billboard_instance(
    em: &ParticleEmitter,
    p: &Particle,
    info: &ParticleGfxInfo,
    camera: &ParticleCamera,
) -> BillboardInstance {
    let orientation = particle_orientation(em, p);
    let sort_point = p.position + orientation * (info.sort_center * p.size);
    let distance_sq = sort_point.distance_squared(camera.position);
    let additive = if info.has_material {
        info.additive
    } else {
        em.desc.flags.contains(EmitterFlags::ADDITIVE)
    };
    let mut center = p.position;
    let to_viewer = camera.position - center;
    let to_viewer_length = to_viewer.length();
    let (axis_x, axis_y);
    if info.is_billboard {
        let (xd, yd) = if to_viewer_length > 1e-3 {
            facing::orient_quad(
                2,
                Quat::IDENTITY,
                Vec3::X,
                Vec3::Y,
                to_viewer / to_viewer_length,
                camera.right,
                camera.up,
            )
        } else {
            (camera.right, camera.up)
        };
        center += (xd * info.center_offset.x + yd * info.center_offset.z) * p.size;
        axis_x = xd * (info.size.x * p.size);
        axis_y = yd * (info.size.y * p.size);
    } else if facing::faces(info.degrade_mode) && to_viewer_length > 1e-3 {
        let (xd, yd) = facing::orient_quad(
            info.degrade_mode,
            orientation,
            info.axis_x,
            info.axis_y,
            to_viewer / to_viewer_length,
            camera.right,
            camera.up,
        );
        let local_normal = info.axis_x.cross(info.axis_y);
        let mut spun_normal = xd.cross(yd);
        if spun_normal.length_squared() > 1e-10 {
            spun_normal /= spun_normal.length();
        }
        let c = info.center_offset;
        center +=
            (xd * c.dot(info.axis_x) + yd * c.dot(info.axis_y) + spun_normal * c.dot(local_normal))
                * p.size;
        axis_x = xd * (info.size.x * p.size);
        axis_y = yd * (info.size.y * p.size);
    } else {
        center += orientation * (info.center_offset * p.size);
        axis_x = (orientation * info.axis_x) * (info.size.x * p.size);
        axis_y = (orientation * info.axis_y) * (info.size.y * p.size);
    }
    BillboardInstance {
        center,
        axis_x,
        axis_y,
        color_argb: p.color_argb,
        surface_id: info.surface_id,
        additive,
        distance_sq,
        emitter: em.handle,
    }
}

#[cfg(test)]
mod tests {
    //! Classifier and ordering cases from OpenAC
    //! `tests/AcDream.App.Tests/Rendering/RetailParticleGeometryClassifierTests.cs`.
    use super::*;

    #[test]
    fn classify_matches_retail_always_2d() {
        assert_eq!(
            classify_particle_geometry(None),
            ParticleGeometryKind::FullMesh
        );
        assert_eq!(
            classify_particle_geometry(Some(1)),
            ParticleGeometryKind::FullMesh
        );
        assert_eq!(
            classify_particle_geometry(Some(0)),
            ParticleGeometryKind::Billboard
        );
        assert_eq!(
            classify_particle_geometry(Some(2)),
            ParticleGeometryKind::Billboard
        );
        assert_eq!(
            classify_particle_geometry(Some(3)),
            ParticleGeometryKind::Billboard
        );
        assert_eq!(
            classify_particle_geometry(Some(u32::MAX)),
            ParticleGeometryKind::Billboard
        );
    }

    #[test]
    fn submission_ordering_interleaves_geometry_by_distance_and_keeps_equal_distance_sequence() {
        let s = |kind, distance_sq, sequence| ParticleSubmission {
            kind,
            index: 0,
            distance_sq,
            sequence,
        };
        let mut submissions = vec![
            s(ParticleSubmissionKind::Mesh, 4.0, 0),
            s(ParticleSubmissionKind::Billboard, 9.0, 1),
            s(ParticleSubmissionKind::Mesh, 9.0, 2),
            s(ParticleSubmissionKind::Billboard, 1.0, 3),
        ];
        sort_submissions(&mut submissions);
        let order: Vec<_> = submissions.iter().map(|x| (x.kind, x.sequence)).collect();
        assert_eq!(
            order,
            [
                (ParticleSubmissionKind::Billboard, 1),
                (ParticleSubmissionKind::Mesh, 2),
                (ParticleSubmissionKind::Mesh, 0),
                (ParticleSubmissionKind::Billboard, 3),
            ]
        );
    }

    #[test]
    fn default_quad_faces_the_camera_with_the_particle_size() {
        use crate::desc::{EmitterDescRegistry, ParticleType};
        use crate::particles::{ParticleSystem, SpawnOptions};
        use crate::random::DotNetRandom;

        let mut system =
            ParticleSystem::new(EmitterDescRegistry::new(), Box::new(DotNetRandom::new(42)));
        system.spawn_emitter(
            Arc::new(EmitterDesc {
                particle_type: ParticleType::Still,
                max_particles: 1,
                initial_particles: 1,
                lifespan: 10.0,
                start_size: 2.0,
                end_size: 2.0,
                start_alpha: 1.0,
                end_alpha: 1.0,
                ..EmitterDesc::default()
            }),
            Vec3::new(0.0, 10.0, 0.0),
            SpawnOptions::default(),
        );
        let camera = ParticleCamera {
            position: Vec3::ZERO,
            right: Vec3::X,
            up: Vec3::Z,
        };
        let mut gfx = ParticleGfxCache::new(None);
        let mut frame = ParticleFrame::default();
        build_particle_frame(system.emitters(), &camera, &mut gfx, &mut frame);
        frame.sort_far_to_near();

        assert_eq!(frame.billboards.len(), 1);
        assert!(frame.meshes.is_empty());
        let b = frame.billboards[0];
        assert_eq!(b.center, Vec3::new(0.0, 10.0, 0.0));
        // Viewer to the south: X span runs east-west, Y span up, 2 m each.
        assert!(
            b.axis_x.distance(Vec3::new(-2.0, 0.0, 0.0)) < 1e-5,
            "{}",
            b.axis_x
        );
        assert!(
            b.axis_y.distance(Vec3::new(0.0, 0.0, 2.0)) < 1e-5,
            "{}",
            b.axis_y
        );
        assert_eq!(b.distance_sq, 100.0);
        assert_eq!(b.color_argb, 0xFFFF_FFFF);
    }
}
