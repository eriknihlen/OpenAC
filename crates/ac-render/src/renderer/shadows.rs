//! The atmospheric rendering in the world frame: turning it on and off
//! from the display settings, each frame's shadow verdict and cascades, the
//! shadow casters, the shadow passes and the atmospheric world pass.

use std::collections::{HashMap, HashSet};

use glam::{Mat4, Vec3};

use super::atmosphere::{
    AtmosphereGpu, CasterFingerprint, CasterFrame, STAR_LAYER_GFX_OBJ_ID, WorldLayouts,
};
use super::sink::{SCENERY_WIND_BIT, has_cutout_subset, static_pose};
use super::{ObjectDraw, WorldRenderer, player_outdoors};
use crate::animation::StaticKey;

use crate::atmosphere::policy::{
    DIRECTIONAL_SHADOW_ELEVATION_RESPONSE, day_group_multiplier, directional_shadow_from_sin,
};
use crate::atmosphere::shadow::{
    CascadeFitInput, ShadowAtmospherePolicy, ShadowEnvironmentInput, ShadowGateReason,
    ShadowUniforms, caster_depth_padding_meters, evaluate_shadow_gate, fit,
    resolve_celestial_source,
};
use crate::atmosphere::{AtmosphereConfig, FrameInputs};
use crate::draws::{InstanceGpu, MeshKey, TerrainInstanceGpu, instance};
use crate::environment::EnvironmentFrame;
use crate::lights::NO_LIGHTS;
use crate::meshing::{Translucency, untextured_subset_draws};
use crate::scene::Scene;
use crate::streaming::Tier;
use crate::world_frame::{LandblockCoord, landblock_offset};

/// The configured room toward the light for casters outside the view,
/// metres (OpenAC `DirectionalShadowCascadeFitInput.CasterDepthPaddingMeters`).
const CASTER_DEPTH_PADDING_METERS: f32 = 48.0;

/// A landblock's side, metres.
const LANDBLOCK_METERS: f32 = 192.0;

/// The pipelines a world pass draws with.
pub(super) struct PassPipelines<'a> {
    pub terrain: &'a wgpu::RenderPipeline,
    pub portal: &'a wgpu::RenderPipeline,
    pub sky: &'a [wgpu::RenderPipeline; 2],
    /// The night sky's (the atmospheric rendering only).
    pub sky_night: Option<&'a wgpu::RenderPipeline>,
    pub meshes: &'a HashMap<(Translucency, crate::meshing::Cull), wgpu::RenderPipeline>,
    /// Clip-mapped sub-meshes have pipelines of their own (the wind).
    pub clip_map_meshes: bool,
    pub billboard: &'a [wgpu::RenderPipeline; 2],
    pub particle_meshes:
        &'a HashMap<(crate::draws::ParticleBlend, crate::meshing::Cull), wgpu::RenderPipeline>,
    /// The atmosphere group: terrain's group 2, objects' group 3.
    pub atmosphere: Option<&'a wgpu::BindGroup>,
    /// The plugins' world lines.
    pub world_line: &'a wgpu::RenderPipeline,
    /// The plugins' world triangles.
    pub world_triangle: &'a wgpu::RenderPipeline,
    /// The invisible probes of the icons hidden behind the world.
    pub icon_probe: &'a wgpu::RenderPipeline,
}

impl WorldRenderer {
    /// The plain world's pipelines.
    pub(super) fn plain_pipelines(&self) -> PassPipelines<'_> {
        PassPipelines {
            terrain: &self.pipelines.terrain,
            portal: &self.pipelines.portal,
            sky: &self.pipelines.sky,
            sky_night: None,
            meshes: &self.pipelines.meshes,
            clip_map_meshes: false,
            billboard: &self.particle_gpu.billboard,
            particle_meshes: &self.particle_gpu.meshes,
            atmosphere: None,
            world_line: &self.pipelines.world_line,
            world_triangle: &self.pipelines.world_triangle,
            icon_probe: &self.pipelines.icon_probe,
        }
    }

    fn world_layouts(&self) -> WorldLayouts<'_> {
        WorldLayouts {
            globals: &self.globals_layout,
            terrain: &self.terrain_layout,
            material: &self.material_layout,
            sky: &self.sky_layout,
            detail: &self.detail_layout,
        }
    }

    /// The display's enhancement selection: build, change or drop the
    /// atmospheric rendering (built at the next frame).
    pub(super) fn apply_atmosphere_display(
        &mut self,
        device: &wgpu::Device,
        display: &ac_host::settings::DisplaySettings,
    ) {
        let selection = display.render_pack_selection();
        if self.atmosphere_selection.as_ref() == Some(&selection) {
            let samples = self.hdr_samples();
            if let Some(a) = self.atmosphere.as_mut() {
                let layouts = WorldLayouts {
                    globals: &self.globals_layout,
                    terrain: &self.terrain_layout,
                    material: &self.material_layout,
                    sky: &self.sky_layout,
                    detail: &self.detail_layout,
                };
                a.rebuild_world_pipelines(device, samples, self.alpha_to_coverage, &layouts);
            }
            return;
        }
        self.atmosphere_selection = Some(selection.clone());
        self.atmosphere_failed = false;
        match AtmosphereConfig::resolve(&selection) {
            Ok(Some(config)) => {
                if let Some(a) = self.atmosphere.as_mut() {
                    a.set_config(device, config);
                } else {
                    self.atmosphere_wanted = Some(config);
                }
            }
            Ok(None) => {
                self.atmosphere = None;
                self.atmosphere_wanted = None;
            }
            Err(reason) => {
                self.notices
                    .push(format!("render: atmospheric rendering off: {reason}"));
                self.atmosphere = None;
                self.atmosphere_wanted = None;
            }
        }
    }

    /// The world pass's sample count with the atmospheric rendering on
    /// (the counts the half-float target supports).
    pub(super) fn hdr_samples(&self) -> u32 {
        super::world_pipelines::usable_sample_count(
            self.quality.msaa_samples,
            &self.hdr_msaa_supported,
        )
    }

    /// The multisample counts the HDR world target supports.
    pub(crate) fn set_hdr_sample_counts(&mut self, counts: Vec<u32>) {
        self.hdr_msaa_supported = counts;
    }

    /// The window measured a frame's GPU time (the world, its shadows and
    /// post): automatic quality judges it.
    pub(crate) fn observe_frame_gpu(
        &mut self,
        device: &wgpu::Device,
        elapsed: std::time::Duration,
    ) {
        let Some(a) = self.atmosphere.as_mut() else {
            return;
        };
        if let Some(level) = a.observe(Some(elapsed.as_secs_f64() * 1000.0)) {
            let config = a.config.at_level(level);
            a.set_config(device, config);
            self.notices
                .push(format!("render: automatic quality chose {level:?}"));
        }
        self.notices.append(&mut a.notices);
        if a.failed {
            self.atmosphere = None;
            self.atmosphere_failed = true;
        }
    }

    /// This frame's atmospheric work: build the GPU side when wanted, the
    /// frame inputs, the shadow verdict and cascades, the blocks.
    #[allow(clippy::too_many_arguments)]
    pub(super) fn prepare_atmosphere(
        &mut self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        scene: Option<&Scene>,
        env: &EnvironmentFrame,
        view: Mat4,
        projection: Mat4,
        eye: Vec3,
        dt: f32,
    ) {
        if let Some(config) = self.atmosphere_wanted.take()
            && !self.atmosphere_failed
        {
            let samples = self.hdr_samples();
            let layouts = self.world_layouts();
            let built = AtmosphereGpu::new(
                device,
                queue,
                config,
                samples,
                self.alpha_to_coverage,
                &layouts,
                self.color_format,
            );
            self.atmosphere = Some(built);
        }
        let started = std::time::Instant::now();
        let samples = self.hdr_samples();
        let size = self.size;
        let outdoor = player_outdoors(scene) && self.viewer_cell & 0xFFFF < 0x0100;
        let cover = self.portal.hides_world() || self.reveal.is_withholding();
        let resident_reach = self.complete_radius(scene) as f32 * LANDBLOCK_METERS;
        let day_group_index = self
            .environment
            .as_ref()
            .and_then(|e| e.day_group_index())
            .map_or(-1, |i| i as i32);
        let source = resolve_celestial_source(
            self.environment.as_ref().and_then(|e| e.day_group()),
            env.day_fraction,
            &env.keyframe,
        );
        let Some(a) = self.atmosphere.as_mut() else {
            return;
        };
        a.ensure_targets(device, size, samples);
        let inputs = FrameInputs::publish(
            &env.keyframe,
            &env.atmosphere,
            eye,
            projection * view,
            day_group_index,
            dt,
            size,
            outdoor,
        );
        let multiplier = (day_group_multiplier(day_group_index)
            * directional_shadow_from_sin(
                &DIRECTIONAL_SHADOW_ELEVATION_RESPONSE,
                source.elevation_sin,
                0.0,
            )
            * a.config.shadow_strength)
            .clamp(0.0, 1.0);
        let mut state = evaluate_shadow_gate(
            &ShadowEnvironmentInput {
                enabled: true,
                portal_or_login_cover: cover,
                player_inside_cell: !outdoor,
                source,
                atmosphere: env.atmosphere,
                active_day_group_multiplier: multiplier,
            },
            &ShadowAtmospherePolicy::neutral_elevation(),
        );
        let near = crate::camera::NEAR_PLANE;
        a.cascades.clear();
        let mut uniforms = ShadowUniforms::disabled();
        if state.should_render() {
            if resident_reach <= near {
                state.reason = ShadowGateReason::ResidentWindowUnavailable;
            } else {
                let quality = a.config.shadow;
                let mut input =
                    CascadeFitInput::new(view, projection, state.surface_to_light, quality);
                input.camera_near_meters = near;
                input.resident_maximum_reach_meters = resident_reach;
                input.caster_depth_padding_meters = caster_depth_padding_meters(
                    CASTER_DEPTH_PADDING_METERS,
                    quality.maximum_reach_meters,
                    resident_reach,
                );
                match fit(&input) {
                    Ok(cascades) if !cascades.is_empty() => {
                        uniforms = ShadowUniforms::create(&cascades, &state, &quality);
                        a.cascades = cascades;
                    }
                    Ok(_) => state.reason = ShadowGateReason::ResidentWindowUnavailable,
                    Err(error) => {
                        // OpenAC's runtime failure: back to the plain
                        // renderer for this selection.
                        a.notices.push(format!(
                            "render: atmospheric rendering off: the shadow cascades failed: {error}"
                        ));
                        a.failed = true;
                        state.reason = ShadowGateReason::ResidentWindowUnavailable;
                    }
                }
            }
        }
        a.shadow_state = Some(state);
        a.write_frame(queue, inputs, &uniforms);
        a.cpu_added += started.elapsed();
        self.notices.append(&mut a.notices);
        if a.failed {
            self.atmosphere = None;
            self.atmosphere_failed = true;
        }
    }

    /// The largest square around the player (radius, landblocks) whose near
    /// content has all arrived (OpenAC's resident window's complete
    /// radius).
    fn complete_radius(&self, scene: Option<&Scene>) -> u32 {
        let Some(cell) = scene
            .and_then(Scene::player)
            .and_then(|p| p.position)
            .map(|p| p.cell_id)
        else {
            return 0;
        };
        let center = LandblockCoord::of_cell(cell);
        let complete = |coord: LandblockCoord| {
            self.residents
                .get(&coord)
                .is_some_and(|r| r.missing || (r.tier == Tier::Near && r.near.is_some()))
        };
        let mut radius = 0;
        for r in 0..=self.near_radius {
            let ring = crate::world_frame::landblocks_around(center, r);
            if !ring.into_iter().all(complete) {
                break;
            }
            radius = r;
        }
        radius
    }

    /// The frame's shadow casters: terrain of the landblocks with a land
    /// cell the walk reached, and the solid and clip-mapped parts of the
    /// statics, scenery, buildings and server objects standing in those
    /// cells (OpenAC `DirectionalShadowCasterFrame` selection: drawable,
    /// not translucent, not indoors, in a visible outdoor cell).
    pub(super) fn collect_shadow_casters(
        &self,
        previous: CasterFrame,
        land_cells: &[u32],
        objects: &HashMap<u32, Vec<ObjectDraw<'_>>>,
        eye: Vec3,
    ) -> CasterFrame {
        let mut cells = land_cells.to_vec();
        cells.sort_unstable();
        cells.dedup();
        let origin = landblock_offset(&self.origin, LandblockCoord { x: 0, y: 0 });
        let fingerprint = CasterFingerprint {
            cells,
            residents: self.residents.len(),
            models: self.models.len(),
            meshes: self.meshes.len(),
            surfaces: self.surfaces.len(),
            origin: [origin.x.to_bits(), origin.y.to_bits(), origin.z.to_bits()],
        };
        let visible: HashSet<u32> = fingerprint.cells.iter().copied().collect();
        let reuse = previous.fingerprint.as_ref() == Some(&fingerprint);
        let mut frame = if reuse {
            let mut kept = previous;
            kept.instances.truncate(kept.still_instances as usize);
            kept.groups.truncate(kept.still_groups);
            kept.still_reused = true;
            kept
        } else {
            let (instances, groups, terrain) = self.still_casters(&visible);
            CasterFrame {
                still_instances: instances.len() as u32,
                still_groups: groups.len(),
                instances,
                groups,
                terrain,
                fingerprint: Some(fingerprint),
                still_reused: false,
            }
        };

        // The moving casters: animating statics, buildings (their level of
        // detail follows the eye), server objects.
        let mut parts: Vec<((MeshKey, u32, bool), InstanceGpu)> = Vec::new();
        for (coord, near, prefix) in self.near_residents() {
            let cell_visible = |cell: usize| visible.contains(&(prefix | (cell as u32 + 1)));
            let offset = Mat4::from_translation(landblock_offset(&self.origin, coord));
            for (cell, statics) in near.by_cell.iter().enumerate() {
                if !cell_visible(cell) {
                    continue;
                }
                for &i in statics {
                    let key = StaticKey {
                        landblock: prefix,
                        cell: 0,
                        index: i as u32,
                    };
                    if self.animations.static_animation(key).is_none() {
                        continue;
                    }
                    let s = &near.statics[i];
                    let Some(Some(model_parts)) = self.models.get(&s.model_id) else {
                        continue;
                    };
                    match static_pose(&self.animations, key, model_parts, s.transform) {
                        // A fading static is translucent: it casts nothing.
                        Some((_, _, Some(_))) | None => {}
                        Some((posed, placed, None)) => {
                            let wind =
                                i >= near.scenery_start && has_cutout_subset(&posed, &self.meshes);
                            for p in &posed {
                                caster(
                                    &mut parts,
                                    p.gfx_obj_id,
                                    p.variant,
                                    false,
                                    offset * placed * p.transform,
                                    wind,
                                );
                            }
                        }
                    }
                }
            }
            for b in &near.buildings {
                if !cell_visible(b.land_cell) {
                    continue;
                }
                let Some(Some(model_parts)) = self.models.get(&b.model_id) else {
                    continue;
                };
                let world = offset * b.transform;
                let part0 = self.building_level(b, world, eye);
                if part0 == 0 {
                    continue;
                }
                for (i, p) in model_parts.iter().enumerate() {
                    let id = if i == 0 { part0 } else { p.gfx_obj_id };
                    caster(&mut parts, id, p.variant, true, world * p.transform, false);
                }
            }
        }
        for key in &visible {
            for object in objects.get(key).into_iter().flatten() {
                if object.indoor || object.opacities.is_some() {
                    continue;
                }
                for p in object.parts.iter() {
                    caster(
                        &mut parts,
                        p.gfx_obj_id,
                        p.variant,
                        false,
                        object.world * p.transform,
                        false,
                    );
                }
            }
        }
        append_grouped(&mut frame.instances, &mut frame.groups, &parts);
        frame
    }

    /// The near-tier landblocks with their content and cell-id prefix.
    fn near_residents(&self) -> impl Iterator<Item = (LandblockCoord, &super::NearResident, u32)> {
        self.residents.iter().filter_map(|(coord, r)| {
            let near = r.near.as_ref().filter(|_| r.tier == Tier::Near)?;
            Some((*coord, near, coord.terrain_id() & 0xFFFF_0000))
        })
    }

    /// The still casters of the `visible` land cells: the terrain of their
    /// landblocks and the statics and scenery standing in them that do not
    /// animate, grouped by mesh.
    #[allow(clippy::type_complexity)]
    fn still_casters(
        &self,
        visible: &HashSet<u32>,
    ) -> (
        Vec<InstanceGpu>,
        Vec<(MeshKey, u32, bool, std::ops::Range<u32>)>,
        Vec<(LandblockCoord, TerrainInstanceGpu)>,
    ) {
        let mut blocks: Vec<LandblockCoord> = Vec::new();
        for (coord, resident) in &self.residents {
            let prefix = coord.terrain_id() & 0xFFFF_0000;
            if resident.tile.is_some() && (1..=64).any(|low| visible.contains(&(prefix | low))) {
                blocks.push(*coord);
            }
        }
        let mut parts: Vec<((MeshKey, u32, bool), InstanceGpu)> = Vec::new();
        for (coord, near, prefix) in self.near_residents() {
            let offset = Mat4::from_translation(landblock_offset(&self.origin, coord));
            for (cell, statics) in near.by_cell.iter().enumerate() {
                if !visible.contains(&(prefix | (cell as u32 + 1))) {
                    continue;
                }
                for &i in statics {
                    let key = StaticKey {
                        landblock: prefix,
                        cell: 0,
                        index: i as u32,
                    };
                    if self.animations.static_animation(key).is_some() {
                        continue;
                    }
                    let s = &near.statics[i];
                    let Some(Some(model_parts)) = self.models.get(&s.model_id) else {
                        continue;
                    };
                    let world = offset * s.transform;
                    let wind =
                        i >= near.scenery_start && has_cutout_subset(model_parts, &self.meshes);
                    for p in model_parts {
                        caster(
                            &mut parts,
                            p.gfx_obj_id,
                            p.variant,
                            false,
                            world * p.transform,
                            wind,
                        );
                    }
                }
            }
        }
        let mut instances = Vec::new();
        let mut groups = Vec::new();
        append_grouped(&mut instances, &mut groups, &parts);
        blocks.sort();
        let terrain = blocks
            .into_iter()
            .map(|coord| {
                (
                    coord,
                    TerrainInstanceGpu::at(landblock_offset(&self.origin, coord).to_array()),
                )
            })
            .collect();
        (instances, groups, terrain)
    }

    /// Record the shadow maps: one depth pass per cascade, the terrain then
    /// each caster's solid and clip-mapped sub-meshes. `begin` times the
    /// first pass; whether it was used.
    pub(super) fn encode_shadows(
        &self,
        encoder: &mut wgpu::CommandEncoder,
        a: &AtmosphereGpu,
        begin: Option<wgpu::RenderPassTimestampWrites<'_>>,
    ) -> bool {
        let mut begin = begin;
        let used = begin.is_some();
        for cascade in 0..a.cascades.len() {
            let Some(layer) = a.shadow_layer(cascade) else {
                continue;
            };
            let mut pass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
                label: Some("directional shadow cascade"),
                color_attachments: &[],
                depth_stencil_attachment: Some(wgpu::RenderPassDepthStencilAttachment {
                    view: layer,
                    depth_ops: Some(wgpu::Operations {
                        load: wgpu::LoadOp::Clear(1.0),
                        store: wgpu::StoreOp::Store,
                    }),
                    stencil_ops: None,
                }),
                timestamp_writes: begin.take(),
                ..Default::default()
            });
            let offset = AtmosphereGpu::cascade_offset(cascade);
            let frame = &a.caster_frame;
            if !frame.terrain.is_empty() {
                pass.set_pipeline(&a.casters.terrain);
                pass.set_bind_group(0, &a.cascade_group, &[offset]);
                pass.set_vertex_buffer(1, a.caster_terrain.slice(..));
                for (j, (coord, _)) in frame.terrain.iter().enumerate() {
                    let Some(tile) = self.residents.get(coord).and_then(|r| r.tile.as_ref()) else {
                        continue;
                    };
                    pass.set_vertex_buffer(0, tile.vertices.slice(..));
                    let j = j as u32;
                    pass.draw(0..ac_core::terrain::VERTICES_PER_LANDBLOCK as u32, j..j + 1);
                }
            }
            if frame.groups.is_empty() {
                continue;
            }
            pass.set_bind_group(0, &a.cascade_group, &[offset]);
            pass.set_vertex_buffer(1, a.caster_instances.slice(..));
            for (mesh, variant, shell, instances) in &frame.groups {
                // An editor-only marker casts nothing, as it draws nothing.
                let Some(gfx) = self.meshes.get(mesh).filter(|g| !g.hidden_marker) else {
                    continue;
                };
                pass.set_vertex_buffer(0, gfx.vertices.slice(..));
                pass.set_index_buffer(gfx.indices.slice(..), wgpu::IndexFormat::Uint32);
                for sub in &gfx.sub_meshes {
                    let Some(surface) = self.surface_of(*variant, sub.surface_id) else {
                        continue;
                    };
                    if !untextured_subset_draws(*shell, surface.untextured) {
                        continue;
                    }
                    let pipeline = match surface.translucency {
                        Translucency::Opaque => &a.casters.opaque[&sub.cull],
                        Translucency::ClipMap => &a.casters.cutout[&sub.cull],
                        _ => continue,
                    };
                    pass.set_pipeline(pipeline);
                    pass.set_bind_group(
                        1,
                        if sub.repeat {
                            &surface.repeat
                        } else {
                            &surface.clamp
                        },
                        &[],
                    );
                    pass.draw_indexed(
                        sub.first_index..sub.first_index + sub.index_count,
                        sub.base_vertex,
                        instances.clone(),
                    );
                }
            }
        }
        used && begin.is_none()
    }

    /// The world with the atmospheric rendering: shadow maps, the world
    /// into the HDR target, then the post into `target`.
    pub(super) fn draw_atmospheric(
        &self,
        encoder: &mut wgpu::CommandEncoder,
        target: &wgpu::TextureView,
        clear: wgpu::Color,
        timestamps: Option<wgpu::RenderPassTimestampWrites<'_>>,
        a: &AtmosphereGpu,
    ) {
        let Some((color, resolve, depth)) = a.world_targets() else {
            return;
        };
        let (begin, end) = match timestamps {
            Some(t) => (
                Some(wgpu::RenderPassTimestampWrites {
                    query_set: t.query_set,
                    beginning_of_pass_write_index: t.beginning_of_pass_write_index,
                    end_of_pass_write_index: None,
                }),
                Some(wgpu::RenderPassTimestampWrites {
                    query_set: t.query_set,
                    beginning_of_pass_write_index: None,
                    end_of_pass_write_index: t.end_of_pass_write_index,
                }),
            ),
            None => (None, None),
        };
        let withheld = self.reveal.is_withholding() && !self.portal.hides_world();
        let mut begin = begin;
        if a.shadows_on() && !withheld && self.encode_shadows(encoder, a, begin.clone()) {
            begin = None;
        }
        {
            let mut pass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
                label: Some("atmospheric world"),
                color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                    view: color,
                    depth_slice: None,
                    resolve_target: resolve,
                    ops: wgpu::Operations {
                        load: wgpu::LoadOp::Clear(clear),
                        store: if resolve.is_some() {
                            wgpu::StoreOp::Discard
                        } else {
                            wgpu::StoreOp::Store
                        },
                    },
                })],
                depth_stencil_attachment: Some(wgpu::RenderPassDepthStencilAttachment {
                    view: depth,
                    depth_ops: Some(wgpu::Operations {
                        load: wgpu::LoadOp::Clear(1.0),
                        store: wgpu::StoreOp::Store,
                    }),
                    stencil_ops: None,
                }),
                timestamp_writes: begin,
                occlusion_query_set: self.icon_probes.query_set(),
                ..Default::default()
            });
            if !withheld {
                let w = &a.world;
                let pipelines = PassPipelines {
                    terrain: &w.terrain,
                    portal: &w.portal,
                    sky: &w.sky,
                    sky_night: Some(&w.sky_night),
                    meshes: &w.meshes,
                    clip_map_meshes: true,
                    billboard: &w.billboard,
                    particle_meshes: &w.particle_meshes,
                    atmosphere: Some(&a.group),
                    world_line: &w.world_line,
                    world_triangle: &w.world_triangle,
                    icon_probe: &w.icon_probe,
                };
                self.draw_steps(&mut pass, &pipelines);
                self.draw_world_lines(&mut pass, &pipelines);
                self.draw_world_triangles(&mut pass, &pipelines);
                self.draw_icon_probes(&mut pass, &pipelines);
            }
        }
        a.encode_post(encoder, target, end);
    }

    /// The sky draw's pipeline: the night sky for the star layer when the
    /// pass has one (OpenAC `SkyRenderer`'s `EnhancedNightSkyActive`).
    pub(super) fn sky_pipeline_for<'a>(
        p: &PassPipelines<'a>,
        gfx_id: u32,
        additive: bool,
    ) -> &'a wgpu::RenderPipeline {
        match p.sky_night {
            Some(night) if gfx_id == STAR_LAYER_GFX_OBJ_ID => night,
            _ => &p.sky[usize::from(additive)],
        }
    }
}

/// One caster part: its mesh's instance at `world` (0: no model).
fn caster(
    parts: &mut Vec<((MeshKey, u32, bool), InstanceGpu)>,
    mesh: u32,
    variant: u32,
    shell: bool,
    world: Mat4,
    wind: bool,
) {
    if mesh == 0 {
        return;
    }
    let mut inst = instance(world, NO_LIGHTS, false);
    if wind {
        inst.indoor |= SCENERY_WIND_BIT;
    }
    parts.push(((u64::from(mesh), variant, shell), inst));
}

/// Append `parts` grouped by mesh (sorted by key, instances copied in that
/// order).
fn append_grouped(
    instances: &mut Vec<InstanceGpu>,
    groups: &mut Vec<(MeshKey, u32, bool, std::ops::Range<u32>)>,
    parts: &[((MeshKey, u32, bool), InstanceGpu)],
) {
    let mut order: Vec<((MeshKey, u32, bool), u32)> = parts
        .iter()
        .enumerate()
        .map(|(i, (k, _))| (*k, i as u32))
        .collect();
    order.sort_unstable();
    instances.reserve(order.len());
    let first_group = groups.len();
    for (key, i) in order {
        let at = instances.len() as u32;
        let extends = groups.len() > first_group
            && groups
                .last()
                .is_some_and(|g| (g.0, g.1, g.2) == key && g.3.end == at);
        if extends {
            if let Some(g) = groups.last_mut() {
                g.3.end = at + 1;
            }
        } else {
            groups.push((key.0, key.1, key.2, at..at + 1));
        }
        instances.push(parts[i as usize].1);
    }
}
