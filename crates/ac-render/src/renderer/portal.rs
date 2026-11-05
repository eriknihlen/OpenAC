//! Portal space in the renderer: the session's events drive the
//! presentation, the tunnel's assets are resolved from the dats once, and
//! while the world is hidden the frame is the tunnel alone, seen from its
//! camera under its own light against black.

use ac_core::lighting::{SceneLightingUbo, UboLight};
use ac_core::sky::DotNetRandom;
use ac_runtime::world::transit::PortalSpaceEvent;
use glam::{Mat4, Vec3};

use super::{Globals, WorldRenderer};
use crate::animation::source::AnimationSource;
use crate::draws::{AlphaDraw, StreamBuilder, instance};
use crate::lights::NO_LIGHTS;
use crate::loader::LoadRequest;
use crate::portal_space::tunnel::{
    ANIMATION_CLIENT_ENUM, CLIENT_ENUM_CATEGORY, PortalTunnel, PortalTunnelCamera,
    SETUP_CLIENT_ENUM, resolve_client_enum,
};
use crate::portal_space::{PortalCue, PresentationKind, STALL_NOTICE_SECONDS};
use crate::reveal::{ReadinessBarrier, ReadinessSnapshot, is_indoor};
use crate::world_frame::{LandblockCoord, landblocks_around};

/// Where the tunnel's assets stand.
#[derive(Debug, Default)]
pub(crate) enum TunnelAssets {
    /// Not looked for yet (the dats may not be open).
    #[default]
    Unresolved,
    /// Resolved.
    Ready(Box<PortalTunnel>),
    /// The dats lack them: portal space shows black.
    Missing,
}

/// The light the tunnel is seen under: one distant white light of
/// intensity 2 from the game's direction, ambient 0.3, no fog.
#[must_use]
pub(crate) fn tunnel_lighting() -> SceneLightingUbo {
    let direction = Vec3::new(0.3, -1.9, 0.65).normalize();
    let eye = PortalTunnelCamera::RETAIL_EYE;
    let mut ubo = SceneLightingUbo::default();
    ubo.lights[0] = UboLight {
        pos_and_kind: [0.0; 4],
        dir_and_range: [direction.x, direction.y, direction.z, 1e9],
        color_and_intensity: [1.0, 1.0, 1.0, 2.0],
        cone_angle_etc: [0.0; 4],
    };
    ubo.cell_ambient = [0.3, 0.3, 0.3, 1.0];
    ubo.fog_params = [1e9, 1e9, 0.0, 0.0];
    ubo.camera_and_time = [eye.x, eye.y, eye.z, 0.0];
    ubo
}

/// A teleport puts the chase camera's eye back on the player: when the
/// session places the body at the teleport's destination, the eye restarts
/// there and the boom re-extends from it, instead of easing over from where
/// the eye was before the trip (kilometres away, with a collision sweep
/// across that whole distance every frame until it caught up).
#[derive(Debug, Default, Clone, Copy, PartialEq, Eq)]
pub(crate) struct ViewerReseat {
    /// The destination cell of the teleport whose placement is awaited.
    destination: Option<u32>,
}

impl ViewerReseat {
    /// A portal-space event: the session's destination is where the body is
    /// placed.
    pub(crate) fn on_event(&mut self, event: &PortalSpaceEvent) {
        if let PortalSpaceEvent::Destination {
            destination_cell, ..
        } = event
        {
            self.destination = Some(*destination_cell);
        }
    }

    /// Whether the eye goes back on the player this frame: the player now
    /// stands in the awaited destination cell (once per placement).
    pub(crate) fn take(&mut self, player_cell: u32) -> bool {
        if self.destination == Some(player_cell) {
            self.destination = None;
            true
        } else {
            false
        }
    }

    /// Nothing is awaited any more (portal space ended or was reset).
    pub(crate) fn clear(&mut self) {
        self.destination = None;
    }
}

impl WorldRenderer {
    /// The session's portal-space event.
    pub(crate) fn portal_space_event(&mut self, event: PortalSpaceEvent) {
        self.viewer_reseat.on_event(&event);
        match event {
            PortalSpaceEvent::Entered { .. } => {
                self.resolve_tunnel();
                // A teleport retires the player-mode trigger once the
                // session moves the body.
                self.cameras.enter_portal_space(false);
            }
            PortalSpaceEvent::Destination { .. }
            | PortalSpaceEvent::LoginEntering
            | PortalSpaceEvent::LoginDestination { .. }
            | PortalSpaceEvent::LogoutStarted => self.resolve_tunnel(),
            PortalSpaceEvent::Arrived { .. } | PortalSpaceEvent::LoginWithdrawn => {}
        }
        let projection = self.lens.projection();
        if !self.portal.on_event(event, &projection) {
            self.portal_space_reset();
        }
    }

    /// The world was replaced: portal space under way ends.
    pub(crate) fn portal_space_reset(&mut self) {
        let tunnel = match &mut self.tunnel {
            TunnelAssets::Ready(t) => Some(t.as_mut()),
            _ => None,
        };
        self.portal.reset(tunnel);
        self.viewer_reseat.clear();
    }

    /// What portal space asked for since the last call (sounds, the wait
    /// line).
    pub(crate) fn take_portal_cues(&mut self) -> Vec<PortalCue> {
        std::mem::take(&mut self.portal_cues)
    }

    /// The world is hidden behind portal space.
    pub(crate) fn portal_hides_world(&self) -> bool {
        self.portal.hides_world()
    }

    /// A reveal holds the world back: a portal from its start until the
    /// destination is shown, or the login reveal while it withholds the
    /// world. The world's sounds stay quiet meanwhile.
    pub(crate) fn holds_world(&self) -> bool {
        let reveal = matches!(
            self.portal.kind(),
            Some(PresentationKind::Portal | PresentationKind::Login)
        );
        reveal || self.reveal.is_withholding()
    }

    /// The login presentation runs (player mode waits for its end).
    pub(crate) fn presents_login(&self) -> bool {
        self.portal.kind() == Some(PresentationKind::Login)
    }

    /// Find the tunnel's Setup and animation, once the dats are open, and
    /// ask for its models.
    fn resolve_tunnel(&mut self) {
        if !matches!(self.tunnel, TunnelAssets::Unresolved) {
            return;
        }
        let Some(content) = self.content.get_if_open() else {
            return;
        };
        let dats = content.dats.uncached();
        let setup_id = resolve_client_enum(dats, SETUP_CLIENT_ENUM, CLIENT_ENUM_CATEGORY);
        let animation_id = resolve_client_enum(dats, ANIMATION_CLIENT_ENUM, CLIENT_ENUM_CATEGORY);
        let Some(source) = self.animation_source() else {
            return;
        };
        let setup = source.setup(setup_id);
        let loader = source.animations();
        let animation = loader.load_animation(animation_id);
        let (Some(setup), Some(_)) = (setup, animation) else {
            self.notices.push(format!(
                "portal space: the tunnel's dat assets are missing \
                 (setup {setup_id:#010X}, animation {animation_id:#010X})"
            ));
            self.tunnel = TunnelAssets::Missing;
            return;
        };
        // Unseeded in the game; any seed will do.
        let seed = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map_or(0, |d| d.subsec_nanos() as i32);
        let tunnel = PortalTunnel::new(
            setup_id,
            setup,
            animation_id,
            loader,
            DotNetRandom::new(seed),
        );
        if self.requested_models.ask(setup_id) {
            self.loader.request(LoadRequest::Model(setup_id));
        }
        self.tunnel_setup = Some(setup_id);
        self.tunnel = TunnelAssets::Ready(Box::new(tunnel));
    }

    /// Advance portal space by `dt` seconds: the destination is ready when
    /// the window's reveal rule says so for its cell.
    pub(super) fn tick_portal(&mut self, dt: f32) {
        if !self.portal.is_active() {
            return;
        }
        // The login presentation takes the player's mode once the
        // character is present (retried each frame until it is).
        if self.portal.needs_login_mode() && self.cameras.enter_portal_space(true) {
            self.portal.enter_login_mode();
        }
        let readiness = self
            .portal
            .destination()
            .map(|cell| ReadinessBarrier::evaluate(&self.reveal_facts(), cell))
            .unwrap_or_default();
        let tunnel = match &mut self.tunnel {
            TunnelAssets::Ready(t) => Some(t.as_mut()),
            _ => None,
        };
        let cues = self.portal.tick(dt, readiness, tunnel);
        for cue in cues {
            match cue {
                // The game waits on; the owner hears what for.
                PortalCue::DestinationStalled {
                    destination_cell,
                    readiness,
                } => {
                    let waits_for = self.reveal_shortfall(destination_cell, &readiness);
                    self.notices.push(format!(
                        "portal: the destination {destination_cell:#010X} has not loaded after \
                         {STALL_NOTICE_SECONDS:.0} s; waiting for {waits_for}"
                    ));
                }
                cue => self.portal_cues.push(cue),
            }
        }
    }

    /// What the reveal rule still waits for at `cell`, in words: the
    /// landblocks without terrain or near content, the models near content
    /// names that have not arrived, the server objects without models.
    pub(super) fn reveal_shortfall(&self, cell: u32, found: &ReadinessSnapshot) -> String {
        const SHOWN: usize = 6;
        fn list<T: std::fmt::Display>(what: &str, items: &[T]) -> Option<String> {
            if items.is_empty() {
                return None;
            }
            let shown: Vec<String> = items.iter().take(SHOWN).map(ToString::to_string).collect();
            let more = items.len().saturating_sub(SHOWN);
            let tail = if more > 0 {
                format!(" and {more} more")
            } else {
                String::new()
            };
            Some(format!(
                "{what} {} ({}{tail})",
                items.len(),
                shown.join(", ")
            ))
        }
        if found.is_unhydratable {
            return "nothing (the destination cannot load)".to_owned();
        }
        let facts = self.reveal_facts();
        let window = ReadinessBarrier::required_window(&facts, cell);
        let center = LandblockCoord::of_cell(cell);
        let name = |b: LandblockCoord| format!("{:02X}{:02X}", b.x, b.y);
        let no_terrain: Vec<String> = landblocks_around(center, window.far_radius)
            .into_iter()
            .filter(|b| {
                self.residents
                    .get(b)
                    .is_none_or(|r| r.tile.is_none() && !r.missing)
            })
            .map(name)
            .collect();
        let mut no_near = Vec::new();
        let mut no_models: Vec<String> = Vec::new();
        for b in landblocks_around(center, window.near_radius) {
            let Some(r) = self.residents.get(&b).filter(|r| !r.missing) else {
                continue;
            };
            match &r.near {
                None => no_near.push(name(b)),
                Some(near) => no_models.extend(
                    near.model_ids
                        .iter()
                        .filter(|m| !self.models.contains_key(m))
                        .map(|m| format!("{m:#010X} in {}", name(b))),
                ),
            }
        }
        let retiring: Vec<String> = self.retiring.coords().map(name).collect();
        let parts: Vec<String> = [
            list("the release of the last window's landblocks", &retiring),
            list("terrain of landblocks", &no_terrain),
            list("near content of landblocks", &no_near),
            list("models", &no_models),
            list(
                "server objects' models",
                &self
                    .server_models_missing
                    .iter()
                    .map(|(guid, setup)| format!("{guid:#010X} ({setup:#010X})"))
                    .collect::<Vec<_>>(),
            ),
            (is_indoor(cell) && found.composites_ready && !found.collision_ready)
                .then(|| format!("the interior cell {cell:#010X}")),
        ]
        .into_iter()
        .flatten()
        .collect();
        if parts.is_empty() {
            format!("nothing now ({found:?})")
        } else {
            parts.join("; ")
        }
    }

    /// The frame while the world is hidden: the tunnel (when shown), with
    /// its camera's matrices and light in the globals.
    pub(super) fn build_tunnel_frame(&mut self, device: &wgpu::Device, queue: &wgpu::Queue) {
        let (width, height) = (self.size.0.max(1) as f32, self.size.1.max(1) as f32);
        let world_projection = self.portal.world_projection(self.lens.projection());
        let mut out = StreamBuilder::new();
        let mut missing = Vec::new();
        if let TunnelAssets::Ready(tunnel) = &mut self.tunnel
            && tunnel.is_visible()
        {
            tunnel.camera.aspect = width / height;
            tunnel.camera.use_smart_box_fov(&world_projection);
            let view_proj = tunnel.camera.projection() * tunnel.camera.view();
            let eye = PortalTunnelCamera::RETAIL_EYE;
            let globals = Globals {
                view_proj: view_proj.to_cols_array_2d(),
                camera_pos: eye.extend(1.0).to_array(),
            };
            queue.write_buffer(&self.globals, 0, bytemuck::bytes_of(&globals));
            queue.write_buffer(
                &self.scene_lighting,
                0,
                bytemuck::cast_slice(&tunnel_lighting().to_floats()),
            );
            for (part, pose) in tunnel.setup().parts.iter().zip(tunnel.poses()) {
                let mesh = u64::from(*part);
                let Some(gpu) = self.meshes.get(&mesh) else {
                    missing.push(*part);
                    continue;
                };
                let world: Mat4 = pose.visual;
                let inst = instance(world, NO_LIGHTS, false);
                if gpu.has_opaque {
                    out.push_opaque(mesh, false, 0, inst);
                }
                let distance = (world.transform_point3(gpu.sort_center) - eye).length();
                for (sub, blended) in gpu.sub_blended.iter().enumerate() {
                    if *blended {
                        out.push_alpha(
                            distance,
                            AlphaDraw {
                                mesh,
                                variant: 0,
                                shell: false,
                                sub: sub as u32,
                                instance: 0,
                                faded: false,
                            },
                            inst,
                        );
                    }
                }
            }
        }
        for id in missing {
            if self.requested_gfx.insert(id) {
                self.loader.request(LoadRequest::GfxObj(id));
            }
        }
        let frame = out.finish();
        self.upload_frame(device, queue, &frame);
        self.stats.objects_drawn = 0;
        self.stats.landblocks_drawn = 0;
        self.stats.cells_drawn = 0;
        self.stats.particles_drawn = 0;
        self.frame = frame;
    }
}

#[cfg(test)]
mod frame_tests {
    //! The window's portal space over the retail dats, drawn headless: the
    //! login tunnel from the enter request (OpenAC
    //! `ArmedLoginTunnel_PresentsTunnelNotBlack_FromTheClickFrame`), its
    //! withdrawal, and the log out warping the world (and its sky) away
    //! into the tunnel (`LogoutRequest_SendsWireThenHoldsThreeSeconds_ThenBeginsWormhole`).
    use std::path::PathBuf;
    use std::sync::Arc;
    use std::time::{Duration, Instant};

    use ac_host::settings::DisplaySettings;

    use super::*;
    use crate::camera::FlyInput;
    use crate::content::ContentSlot;
    use crate::portal_space::sequencer::TeleportAnimState;
    use crate::renderer::WorldSettings;
    use crate::sky::{SKY_FAR, SKY_NEAR, sky_projection, sky_view};

    const FORMAT: wgpu::TextureFormat = wgpu::TextureFormat::Rgba8Unorm;
    const SIZE: (u32, u32) = (96, 64);

    fn dat_dir() -> PathBuf {
        std::env::var_os("OPENAC_DAT_DIR")
            .map(PathBuf::from)
            .or_else(ac_dat::default_dat_dir)
            .expect("no dat directory: set OPENAC_DAT_DIR")
    }

    fn gpu() -> Option<(wgpu::Device, wgpu::Queue)> {
        crate::test_gpu::device()
    }

    fn renderer(device: &wgpu::Device, queue: &wgpu::Queue) -> WorldRenderer {
        let content = Arc::new(ContentSlot::new(dat_dir()));
        content.get().expect("open the retail dats");
        let settings = WorldSettings {
            content,
            quality_overrides: crate::quality::QualityOverrides {
                near_radius: Some(0),
                far_radius: Some(0),
                msaa_samples: Some(0),
                ..Default::default()
            },
            residency: Default::default(),
            orbit: Default::default(),
            reveal_radius: None,
            sky: Default::default(),
            audio: Default::default(),
        };
        WorldRenderer::new(
            device,
            queue,
            FORMAT,
            SIZE.0,
            SIZE.1,
            &settings,
            &DisplaySettings::default(),
            &[1],
        )
    }

    /// Prepare frames until `done` holds (at most a minute).
    fn frames_until(
        world: &mut WorldRenderer,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        what: &str,
        mut done: impl FnMut(&mut WorldRenderer) -> bool,
    ) {
        let started = Instant::now();
        loop {
            world.prepare(device, queue, None, FlyInput::default());
            if done(world) {
                return;
            }
            assert!(
                started.elapsed() < Duration::from_secs(60),
                "never: {what} ({:?})",
                world.failure()
            );
            std::thread::sleep(Duration::from_millis(10));
        }
    }

    /// Simulated frame time for [`steps_until`], seconds.
    const STEP: f32 = 1.0 / 60.0;

    /// Prepare frames `STEP` seconds apart until `done` holds (at most ten
    /// simulated seconds). Portal space advances by the step alone, whatever
    /// the wall clock says, so a slow frame on a busy machine cannot carry
    /// the warp past the stage a test looks at.
    fn steps_until(
        world: &mut WorldRenderer,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        what: &str,
        mut done: impl FnMut(&mut WorldRenderer) -> bool,
    ) {
        for _ in 0..600 {
            world.tick_portal(STEP);
            // The frame itself then sees no time pass.
            world.last_update = None;
            world.prepare(device, queue, None, FlyInput::default());
            if done(world) {
                return;
            }
        }
        panic!("never: {what} ({:?})", world.failure());
    }

    /// The pixels of one drawn frame that are not the clear colour.
    fn drawn_pixels(world: &WorldRenderer, device: &wgpu::Device, queue: &wgpu::Queue) -> usize {
        let texture = device.create_texture(&wgpu::TextureDescriptor {
            label: Some("portal space test target"),
            size: wgpu::Extent3d {
                width: SIZE.0,
                height: SIZE.1,
                depth_or_array_layers: 1,
            },
            mip_level_count: 1,
            sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            format: FORMAT,
            usage: wgpu::TextureUsages::RENDER_ATTACHMENT | wgpu::TextureUsages::COPY_SRC,
            view_formats: &[],
        });
        let view = texture.create_view(&wgpu::TextureViewDescriptor::default());
        let mut encoder = device.create_command_encoder(&wgpu::CommandEncoderDescriptor::default());
        let clear = world.clear_color(wgpu::Color::BLACK);
        world.draw(&mut encoder, &view, clear, None);
        let padded = (SIZE.0 * 4).div_ceil(wgpu::COPY_BYTES_PER_ROW_ALIGNMENT)
            * wgpu::COPY_BYTES_PER_ROW_ALIGNMENT;
        let buffer = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("portal space test readback"),
            size: u64::from(padded) * u64::from(SIZE.1),
            usage: wgpu::BufferUsages::COPY_DST | wgpu::BufferUsages::MAP_READ,
            mapped_at_creation: false,
        });
        encoder.copy_texture_to_buffer(
            texture.as_image_copy(),
            wgpu::TexelCopyBufferInfo {
                buffer: &buffer,
                layout: wgpu::TexelCopyBufferLayout {
                    offset: 0,
                    bytes_per_row: Some(padded),
                    rows_per_image: Some(SIZE.1),
                },
            },
            texture.size(),
        );
        queue.submit([encoder.finish()]);
        buffer.map_async(wgpu::MapMode::Read, .., |_| {});
        device
            .poll(wgpu::PollType::wait_indefinitely())
            .expect("the GPU finishes");
        let mapped = buffer.slice(..).get_mapped_range().expect("mapped");
        (0..SIZE.1)
            .flat_map(|y| {
                let line = &mapped[(y * padded) as usize..][..(SIZE.0 * 4) as usize];
                line.chunks_exact(4)
                    .map(|px| usize::from(px[0].max(px[1]).max(px[2]) > 8))
                    .collect::<Vec<_>>()
            })
            .sum()
    }

    fn tunnel_visible(world: &WorldRenderer) -> bool {
        matches!(&world.tunnel, TunnelAssets::Ready(t) if t.is_visible())
    }

    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_login_tunnel_shows_from_the_enter_request_until_withdrawn() {
        let Some((device, queue)) = gpu() else {
            eprintln!("no GPU adapter on this machine: portal space is not drawn");
            return;
        };
        let scope = device.push_error_scope(wgpu::ErrorFilter::Validation);
        let mut world = renderer(&device, &queue);
        world.prepare(&device, &queue, None, FlyInput::default());
        world.portal_space_event(PortalSpaceEvent::LoginEntering);
        assert!(matches!(world.tunnel, TunnelAssets::Ready(_)));
        world.prepare(&device, &queue, None, FlyInput::default());
        // The enter cue plays at once and the tunnel replaces the world.
        assert_eq!(
            world.take_portal_cues().first(),
            Some(&PortalCue::EnterSound)
        );
        assert!(tunnel_visible(&world));
        assert!(world.portal_hides_world() && world.holds_world() && world.presents_login());
        assert_eq!(
            world.clear_color(wgpu::Color::WHITE),
            crate::portal_space::tunnel::PORTAL_SPACE_CLEAR
        );
        // The tunnel's models arrive and it is drawn: portal space, not
        // black.
        frames_until(&mut world, &device, &queue, "the tunnel is drawn", |w| {
            !w.frame.steps.is_empty()
        });
        assert!(
            drawn_pixels(&world, &device, &queue) > 0,
            "the tunnel shows"
        );
        // No destination and no player: it holds, and nothing is answered.
        let cues = world.take_portal_cues();
        assert!(
            cues.iter()
                .all(|c| !matches!(c, PortalCue::LoginShown(_) | PortalCue::EnterSound)),
            "{cues:?}"
        );
        assert_eq!(world.portal.state(), TeleportAnimState::Tunnel);
        // The entry went back to selection: portal space ends.
        world.portal_space_event(PortalSpaceEvent::LoginWithdrawn);
        assert!(!world.portal.is_active() && !world.portal_hides_world());
        assert!(!tunnel_visible(&world));
        assert!(crate::surface::block_on(scope.pop()).is_none());
    }

    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_logout_warps_the_world_and_its_sky_away_into_the_tunnel() {
        let Some((device, queue)) = gpu() else {
            eprintln!("no GPU adapter on this machine: portal space is not drawn");
            return;
        };
        let scope = device.push_error_scope(wgpu::ErrorFilter::Validation);
        let mut world = renderer(&device, &queue);
        frames_until(&mut world, &device, &queue, "the sky is drawn", |w| {
            !w.sky_templates.is_empty()
        });
        world.portal_space_event(PortalSpaceEvent::LogoutStarted);
        // The world warps away first, the sky with it.
        steps_until(&mut world, &device, &queue, "the warp narrows", |w| {
            let (_, projection) = w.view_matrices.expect("matrices");
            view_plane_ratio(&projection, &w.lens.projection()) < 0.5
        });
        assert_eq!(world.portal.state(), TeleportAnimState::WorldFadeOut);
        assert!(!world.portal_hides_world());
        assert!(!world.holds_world(), "a log out holds no reveal");
        let (view, projection) = world.view_matrices.expect("matrices");
        assert_ne!(projection, world.lens.projection());
        let sky = (sky_projection(projection, SKY_NEAR, SKY_FAR) * sky_view(view)).to_cols_array();
        assert!(!world.sky_templates.is_empty());
        assert!(
            world
                .sky_templates
                .iter()
                .all(|s| s.params.view_proj == sky),
            "the sky warps with the world"
        );
        // Then the tunnel, where the log out holds.
        steps_until(&mut world, &device, &queue, "the tunnel", |w| {
            w.portal.state() == TeleportAnimState::Tunnel
        });
        assert!(world.portal_hides_world() && tunnel_visible(&world));
        let cues = world.take_portal_cues();
        assert_eq!(
            cues.iter()
                .filter(|c| matches!(c, PortalCue::EnterSound))
                .count(),
            1
        );
        assert!(!cues.iter().any(|c| matches!(c, PortalCue::ExitSound)));
        // The session leaving the world ends it, with no exit cue.
        world.portal_space_reset();
        assert!(!world.portal.is_active() && !tunnel_visible(&world));
        assert!(crate::surface::block_on(scope.pop()).is_none());
    }

    /// How far the warp has narrowed the view: the warped view-plane
    /// distance over the game's.
    fn view_plane_ratio(warped: &Mat4, game: &Mat4) -> f32 {
        warped.y_axis.y / game.y_axis.y
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn destination(cell: u32) -> PortalSpaceEvent {
        PortalSpaceEvent::Destination {
            generation: 1,
            teleport_sequence: 2,
            destination_cell: cell,
        }
    }

    // OpenAC `LocalPlayerTeleportPlacement.Place` calls
    // `RetailChaseCamera.ResetViewerToPlayer` when the arrival is placed.
    #[test]
    fn a_teleport_reseats_the_viewer_once_the_player_stands_at_the_destination() {
        const DEST: u32 = 0x7D64_0021;
        let mut r = ViewerReseat::default();
        assert!(!r.take(DEST), "nothing awaited before a teleport");
        r.on_event(&PortalSpaceEvent::Entered {
            teleport_sequence: 2,
        });
        r.on_event(&destination(DEST));
        // The player is still where it was: not yet.
        assert!(!r.take(0xA9B4_0009));
        assert!(r.take(DEST), "placed at the destination");
        assert!(!r.take(DEST), "once per placement");
    }

    #[test]
    fn a_newer_destination_replaces_the_awaited_one_and_a_reset_drops_it() {
        let mut r = ViewerReseat::default();
        r.on_event(&destination(0x7D64_0021));
        r.on_event(&destination(0xA9B4_0009));
        assert!(!r.take(0x7D64_0021));
        assert!(r.take(0xA9B4_0009));
        r.on_event(&destination(0xA9B4_0009));
        r.clear();
        assert!(!r.take(0xA9B4_0009));
        // The arrival and the login events await nothing.
        r.on_event(&PortalSpaceEvent::Arrived {
            teleport_sequence: 2,
            destination_cell: 0xA9B4_0009,
        });
        r.on_event(&PortalSpaceEvent::LoginDestination {
            generation: 1,
            destination_cell: 0xA9B4_0009,
        });
        assert!(!r.take(0xA9B4_0009));
    }

    #[test]
    fn the_tunnel_is_lit_by_one_distant_light_over_a_grey_ambient_without_fog() {
        // OpenAC PortalTunnelPresentation.UploadRetailLight.
        let ubo = tunnel_lighting();
        let d = Vec3::new(0.3, -1.9, 0.65).normalize();
        assert_eq!(ubo.lights[0].pos_and_kind, [0.0; 4]);
        assert_eq!(ubo.lights[0].dir_and_range, [d.x, d.y, d.z, 1e9]);
        assert_eq!(ubo.lights[0].color_and_intensity, [1.0, 1.0, 1.0, 2.0]);
        assert_eq!(ubo.cell_ambient, [0.3, 0.3, 0.3, 1.0]);
        assert_eq!(ubo.fog_params[3], 0.0, "no fog");
        assert_eq!(ubo.lights[1], UboLight::default());
    }
}
