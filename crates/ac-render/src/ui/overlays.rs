//! The window's overlays over the world: the overlay band with the world
//! labels (the plugins', `super::plugin_ui`) and the vivid target
//! indicator around the selected object (OpenAC `RetailUiRuntime`'s
//! `MountOverlayHost`, `MountWorldLabels`, `MountVividTargetIndicator`),
//! and the navigation view (`/nav grid`, `/nav route` and walks under way,
//! OpenAC `NavMeshDebugOverlay`) on a layer of the band, over the world
//! rather than hidden behind it. The camera and the
//! selected object's selection sphere come from the world renderer each
//! frame.

use ac_core::items::ClientObjectTable;
use ac_dat::DatCollection;
use ac_dat::objects::Setup;
use ac_runtime::world::ObjectView;
use ac_ui::draw::Color;
use ac_ui::font::DEFAULT_FONT_ID;
use ac_ui::import::LayoutSource;
use ac_ui::overlay::{
    OverlayCamera, OverlayHost, VividTargetIndicator, VividTargetInfo, WorldIconOverlay,
    WorldLabelOverlay, WorldLine, WorldLineOverlay, load_vivid_target_images,
};
use glam::{Mat4, Quat, Vec3};

use super::window::WindowUi;
use crate::renderer::WorldRenderer;
use crate::scene::Scene;

/// The character option bit of the vivid targeting indicator (options 1).
const VIVID_TARGETING_BIT: u32 = 0x0000_8000;

/// The overlays the window keeps.
#[derive(Debug, Default)]
pub struct Overlays {
    host: Option<OverlayHost>,
    icons: Option<WorldIconOverlay>,
    labels: Option<WorldLabelOverlay>,
    vivid: Option<VividTargetIndicator>,
    camera: Option<OverlayCamera>,
    target: Option<VividTargetInfo>,
    navigation: Option<WorldLineOverlay>,
    navigation_lines: Vec<WorldLine>,
}

/// The overlay band layer of the navigation view.
pub const NAVIGATION_LAYER: &str = "NavigationOverlay";

impl Overlays {
    /// The overlay band (none before it is mounted).
    pub(super) fn host_mut(&mut self) -> Option<&mut OverlayHost> {
        self.host.as_mut()
    }

    /// The plugins' world icons (none before they are mounted).
    pub(super) fn icons_mut(&mut self) -> Option<&mut WorldIconOverlay> {
        self.icons.as_mut()
    }

    /// The camera the world was last drawn with.
    pub(super) fn camera(&self) -> Option<OverlayCamera> {
        self.camera
    }
}

impl WindowUi {
    /// Mount the overlay band, the world icons and labels (the labels'
    /// text over the icons) and the vivid target indicator.
    pub(super) fn mount_overlays(&mut self) {
        let mut host = OverlayHost::mount(&mut self.host.root);
        let font = self.content.font(DEFAULT_FONT_ID);
        let line_height = font.as_ref().map_or(0.0, |f| f.line_height);
        self.status.overlays.icons = Some(WorldIconOverlay::mount(
            &mut self.host.root,
            &mut host,
            line_height,
        ));
        let labels = WorldLabelOverlay::mount(&mut self.host.root, &mut host, font);
        let navigation = WorldLineOverlay::mount(&mut self.host.root, &mut host, NAVIGATION_LAYER);
        self.status.overlays.host = Some(host);
        self.status.overlays.labels = Some(labels);
        self.status.overlays.navigation = Some(navigation);
        match load_vivid_target_images(self.content.dats()) {
            Some(images) => {
                self.status.overlays.vivid =
                    Some(VividTargetIndicator::mount(&mut self.host.root, images));
            }
            None => self
                .notices
                .push("ui: the vivid target images are not in the dats".to_owned()),
        }
    }

    /// The camera and the selected object as the world renderer last drew
    /// them (none without a world).
    pub fn set_world_view(
        &mut self,
        camera: Option<OverlayCamera>,
        target: Option<VividTargetInfo>,
    ) {
        self.status.overlays.camera = camera;
        self.status.overlays.target = target;
    }

    /// The navigation view's segments in the drawing frame (none without
    /// a world).
    pub fn set_navigation_lines(&mut self, lines: Vec<WorldLine>) {
        self.status.overlays.navigation_lines = lines;
    }

    /// The navigation view's segments as last set.
    #[must_use]
    pub fn navigation_lines(&self) -> &[WorldLine] {
        &self.status.overlays.navigation_lines
    }

    /// One frame of the overlays. The plugins' labels follow only the
    /// labels, their anchors and the game camera; the window gives them no
    /// labels while the portal view hides the world.
    pub(super) fn tick_overlays(&mut self) {
        let overlays = &mut self.status.overlays;
        let camera = overlays.camera;
        // The band follows the interface root, which restoring the layout
        // can resize without passing through a window resize.
        if let Some(host) = overlays.host.as_mut() {
            host.follow_root(&mut self.host.root);
        }
        if let Some(labels) = overlays.labels.as_mut() {
            let anchors = &self.plugins.anchors;
            labels.tick(
                &mut self.host.root,
                &self.plugins.labels,
                &|id| anchors.get(&id).copied(),
                camera.as_ref(),
            );
        }
        if let Some(navigation) = overlays.navigation.as_mut() {
            navigation.tick(
                &mut self.host.root,
                &overlays.navigation_lines,
                camera.as_ref(),
            );
        }
        let enabled = self
            .player_view
            .is_some_and(|v| v.options1 & VIVID_TARGETING_BIT != 0);
        // The indicator takes the radar's colour for the selected object,
        // fellows and their leader included (OpenAC `RelationshipFor`).
        let relationship = super::window::fellowship_relationship(
            self.facts.social.as_ref().map(|s| &s.fellowship),
            self.player_view.and_then(|v| v.selected).unwrap_or(0),
        );
        if let Some(vivid) = overlays.vivid.as_ref() {
            let target = overlays.target.filter(|_| enabled);
            vivid.tick(
                &mut self.host.root,
                target.as_ref(),
                &relationship,
                camera.as_ref(),
            );
        }
    }
}

/// The session's navigation view (OpenAC `NavMeshDebugOverlay`) in the
/// drawing frame: its segments are measured from the corner of its frame's
/// centre landblock, which the world's floating origin places.
pub(crate) fn navigation_lines(world: &WorldRenderer, scene: &Scene) -> Vec<WorldLine> {
    let view = scene.navigation();
    if view.lines.is_empty() {
        return Vec::new();
    }
    let Some(offset) = world.frame_position(view.frame_center, Vec3::ZERO) else {
        return Vec::new();
    };
    view.lines
        .iter()
        .map(|line| {
            let [r, g, b] = line.kind.rgb();
            WorldLine {
                from: line.from + offset,
                to: line.to + offset,
                color: Color::rgba(r, g, b, 1.0),
            }
        })
        .collect()
}

/// Where an object stands, for its selection sphere and its label: placed
/// in the world (its position and turn), or held by another (the root pose
/// its holder's hand gives it, as last published).
#[derive(Debug, Clone, Copy, PartialEq)]
pub enum Placement {
    /// Placed: its base in the drawing frame and its turn.
    World {
        /// The base, drawing frame.
        base: Vec3,
        /// The turn.
        rotation: Quat,
    },
    /// Held: its root, drawing frame.
    Held(Mat4),
}

impl Placement {
    /// The point the object stands on.
    #[must_use]
    pub fn base(&self) -> Vec3 {
        match *self {
            Self::World { base, .. } => base,
            Self::Held(root) => root.w_axis.truncate(),
        }
    }
}

/// The selection sphere of an object at `placement` (OpenAC
/// `WorldSelectionQuery.TryGetSelectionSphere`): the setup's `sphere`
/// (centre, radius) scaled by `scale` and carried by the turn or by the
/// held root; a 0.1 m sphere at the base without one.
#[must_use]
pub fn selection_sphere(
    placement: Placement,
    sphere: Option<(Vec3, f32)>,
    scale: f32,
) -> (Vec3, f32) {
    let Some((origin, radius)) = sphere.filter(|(_, r)| *r > 1e-4) else {
        return (placement.base(), 0.1);
    };
    let local = origin * scale;
    let center = match placement {
        Placement::World { base, rotation } => base + rotation * local,
        Placement::Held(root) => root.transform_point3(local),
    };
    (center, radius * scale)
}

/// Where `object` stands in the drawing frame: its position, or, held by
/// another, the root pose the world renderer last published for it (none
/// before one was).
pub(crate) fn placement(world: &WorldRenderer, object: &ObjectView) -> Option<Placement> {
    if object.parent.is_some() {
        return world.published_root_pose(object.guid).map(Placement::Held);
    }
    let pos = object.position?;
    let base = world.frame_position(
        pos.cell_id,
        Vec3::new(pos.origin.x, pos.origin.y, pos.origin.z),
    )?;
    let o = pos.orientation;
    Some(Placement::World {
        base,
        rotation: Quat::from_xyzw(o.x, o.y, o.z, o.w),
    })
}

/// The camera and the selected object's selection sphere (OpenAC
/// `WorldSelectionQuery.ResolveVividTargetInfo`, `TryGetSelectionSphere`)
/// in the drawing frame: an object other than the player and not his own
/// (by `objects`: in his packs or wielded by him), placed or held by
/// another, its setup's selection sphere turned with it and scaled (a
/// 0.1 m sphere at its base when the setup has none).
///
/// `viewport` is the framebuffer in UI units
/// (`crate::ui_scale::UiScale::overlay_viewport`): what the camera puts at
/// a pixel the overlays place at that pixel in units, so the UI scale
/// draws it back over the pixel.
pub(crate) fn world_view(
    world: &WorldRenderer,
    scene: &Scene,
    dats: &DatCollection,
    objects: &ClientObjectTable,
    viewport: (f32, f32),
) -> (Option<OverlayCamera>, Option<VividTargetInfo>) {
    let camera = world
        .view_matrices()
        .map(|(view, projection)| OverlayCamera {
            view,
            projection,
            viewport,
        });
    let target = (|| {
        let selected = scene.player_view()?.selected?;
        let player = scene.player_guid().unwrap_or(0);
        if selected == 0
            || selected == player
            || objects.is_owned_by_object(selected, player)
            || objects.get(selected).is_some_and(|o| o.container_id != 0)
        {
            return None;
        }
        let object = scene.object_by_guid(selected)?;
        let placement = placement(world, object)?;
        let scale = object.scale.filter(|s| *s > 0.0).unwrap_or(1.0);
        let sphere = object
            .setup_id
            .and_then(|id| dats.get::<Setup>(id).ok().flatten())
            .map(|s| s.selection_sphere)
            .map(|s| (Vec3::new(s.origin.x, s.origin.y, s.origin.z), s.radius));
        let (center, radius) = selection_sphere(placement, sphere, scale);
        Some(VividTargetInfo {
            center,
            radius,
            item_type: object.radar.item_type,
            description_flags: object.radar.public_weenie_bitfield,
            blip_color: object.radar.radar_blip_color,
        })
    })();
    (camera, target)
}

#[cfg(test)]
mod tests {
    use super::*;

    // OpenAC `WorldSelectionQuery.TryGetSelectionSphere`: a placed object's
    // sphere turns with it from its base; a held one's is carried by the
    // root its holder's hand gives it; without a sphere, 0.1 m at the base.
    #[test]
    fn the_selection_sphere_follows_the_placement_or_the_holders_hand() {
        let sphere = Some((Vec3::new(0.0, 1.0, 0.5), 0.4));
        let turned = Placement::World {
            base: Vec3::new(10.0, 0.0, 0.0),
            rotation: Quat::from_rotation_z(std::f32::consts::FRAC_PI_2),
        };
        let (center, radius) = selection_sphere(turned, sphere, 2.0);
        assert!(
            (center - Vec3::new(8.0, 0.0, 1.0)).length() < 1e-5,
            "{center}"
        );
        assert!((radius - 0.8).abs() < 1e-6);
        let hand = Mat4::from_rotation_translation(
            Quat::from_rotation_x(std::f32::consts::PI),
            Vec3::new(1.0, 2.0, 3.0),
        );
        let (center, radius) = selection_sphere(Placement::Held(hand), sphere, 1.0);
        assert!(
            (center - Vec3::new(1.0, 1.0, 2.5)).length() < 1e-5,
            "{center}"
        );
        assert!((radius - 0.4).abs() < 1e-6);
        assert_eq!(
            selection_sphere(Placement::Held(hand), None, 1.0),
            (Vec3::new(1.0, 2.0, 3.0), 0.1)
        );
    }
}
