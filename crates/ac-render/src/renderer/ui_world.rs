//! The world renderer as the UI sees the world: what the panels ask of the
//! world as last drawn (`ac_ui_window::world::WorldFrame`).

use ac_ui_window::world::{IconProbeKey, WorldFrame};
use glam::{Mat4, Vec3};

use super::WorldRenderer;

impl WorldFrame for WorldRenderer {
    fn frame_position(&self, cell_id: u32, local: Vec3) -> Option<Vec3> {
        WorldRenderer::frame_position(self, cell_id, local)
    }

    fn published_root_pose(&self, guid: u32) -> Option<Mat4> {
        WorldRenderer::published_root_pose(self, guid)
    }

    fn view_matrices(&self) -> Option<(Mat4, Mat4)> {
        WorldRenderer::view_matrices(self)
    }

    fn portal_hides_world(&self) -> bool {
        WorldRenderer::portal_hides_world(self)
    }

    fn icon_probe_visible(&self, key: IconProbeKey) -> Option<bool> {
        WorldRenderer::icon_probe_visible(self, key)
    }

    fn sample_terrain_z(&self, x: f32, y: f32) -> Option<f32> {
        WorldRenderer::sample_terrain_z(self, x, y)
    }
}
