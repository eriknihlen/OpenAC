//! What the chase camera asks of the world's collision: sweep the eye from
//! the pivot toward where the boom wants it, and say where it stopped. The
//! sweep through the collision world is the renderer's; the camera only
//! needs this probe.

use glam::Vec3;

/// Where the sweep left the eye and the cell holding it (0: the sweep
/// failed and the eye was put back on the player).
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct CameraSweep {
    /// The eye, drawing frame.
    pub eye: Vec3,
    /// The cell holding the eye, 0 for none.
    pub viewer_cell: u32,
}

/// Something that can sweep the camera's eye from the pivot toward where
/// the boom wants it.
pub trait CameraCollisionProbe {
    /// Sweep from `pivot` toward `desired_eye`, starting in the player's
    /// cell `cell_id`, ignoring the object `self_id` (the player);
    /// `player_position` is where the eye goes when nothing works.
    fn sweep_eye(
        &self,
        pivot: Vec3,
        desired_eye: Vec3,
        cell_id: u32,
        self_id: u32,
        player_position: Vec3,
    ) -> CameraSweep;
}
