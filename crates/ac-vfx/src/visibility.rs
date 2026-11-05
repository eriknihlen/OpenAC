//! The world view particles are tested against. The renderer reports, per
//! frame, which outdoor land cells it drew; once the frame completes that
//! set (and the viewer position) is what the next particle update uses. An
//! aborted frame keeps the previous completed view.

use std::collections::HashSet;
use std::fmt;

use glam::Vec3;

use crate::particles::ParticleSystem;

/// The particle range setting "extended" doubles every emitter's authored
/// degrade distance.
pub const EXTENDED_RANGE_MULTIPLIER: f32 = 2.0;

/// A cell id that is not an outdoor land cell (low word `1..0x100`).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct NotALandscapeCell(pub u32);

impl fmt::Display for NotALandscapeCell {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(
            f,
            "Landscape visibility accepts only outdoor land cells; 0x{:08X} is not one.",
            self.0
        )
    }
}

impl std::error::Error for NotALandscapeCell {}

/// The last completed view: the outdoor cells drawn, and whether there was
/// a world view at all (none in portal space or at login).
#[derive(Debug, Clone, Copy)]
pub struct LandscapeVisibilityFrame<'a> {
    /// Outdoor land cells drawn.
    pub cell_ids: &'a HashSet<u32>,
    /// The frame drew the world.
    pub has_completed_world_view: bool,
}

/// Collects one frame's view and hands the last completed one to the
/// particle system.
#[derive(Debug, Default)]
pub struct ParticleVisibilityController {
    building: HashSet<u32>,
    completed: HashSet<u32>,
    building_viewer: Vec3,
    completed_viewer: Vec3,
    frame_open: bool,
    frame_uses_world_view: bool,
    has_completed_world_view: bool,
}

impl ParticleVisibilityController {
    /// No view yet.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Start a frame seen from `viewer_position`.
    pub fn begin_frame(&mut self, viewer_position: Vec3) {
        self.building.clear();
        self.building_viewer = viewer_position;
        self.frame_uses_world_view = false;
        self.frame_open = true;
    }

    /// The frame draws the world.
    pub fn use_world_view(&mut self) {
        if self.frame_open {
            self.frame_uses_world_view = true;
        }
    }

    /// Add outdoor land cells the frame drew. Any other id is refused (and
    /// none of the batch is added).
    pub fn mark_visible_landscape_cells(
        &mut self,
        cell_ids: impl IntoIterator<Item = u32> + Clone,
    ) -> Result<(), NotALandscapeCell> {
        if !self.frame_open || !self.frame_uses_world_view {
            return Ok(());
        }
        if let Some(bad) = cell_ids.clone().into_iter().find(|id| {
            let low = id & 0xFFFF;
            low == 0 || low >= 0x0100
        }) {
            return Err(NotALandscapeCell(bad));
        }
        self.building.extend(cell_ids);
        Ok(())
    }

    /// The frame finished: its view becomes the completed one.
    pub fn complete_frame(&mut self) {
        if !self.frame_open {
            return;
        }
        self.frame_open = false;
        self.completed.clear();
        self.completed_viewer = self.building_viewer;
        if self.frame_uses_world_view {
            self.completed.extend(self.building.iter().copied());
            self.has_completed_world_view = true;
        } else {
            self.has_completed_world_view = false;
        }
    }

    /// The frame was abandoned: the previous completed view stays.
    pub fn abort_frame(&mut self) {
        self.building.clear();
        self.frame_uses_world_view = false;
        self.frame_open = false;
    }

    /// Test every world emitter against the completed view, with the
    /// range setting's multiplier.
    pub fn apply(&self, particles: &mut ParticleSystem, range_multiplier: f32) {
        particles.apply_retail_view(
            self.completed_viewer,
            &self.completed,
            self.has_completed_world_view,
            range_multiplier,
        );
    }

    /// The completed view.
    #[must_use]
    pub fn completed_landscape_visibility(&self) -> LandscapeVisibilityFrame<'_> {
        LandscapeVisibilityFrame {
            cell_ids: &self.completed,
            has_completed_world_view: self.has_completed_world_view,
        }
    }

    /// Forget every view.
    pub fn reset(&mut self) {
        *self = Self::default();
    }
}

#[cfg(test)]
mod tests {
    //! Cases from OpenAC `tests/AcDream.App.Tests/Rendering/Vfx/ParticleVisibilityControllerTests.cs`.
    use std::sync::Arc;

    use super::*;
    use crate::desc::{EmitterDesc, EmitterDescRegistry, ParticleType};
    use crate::particles::SpawnOptions;
    use crate::random::DotNetRandom;

    fn system() -> ParticleSystem {
        ParticleSystem::new(EmitterDescRegistry::new(), Box::new(DotNetRandom::new(1)))
    }

    fn spawn(sys: &mut ParticleSystem, dat_id: u32, owner: u32, cell: u32) -> i32 {
        let handle = sys.spawn_emitter(
            Arc::new(EmitterDesc {
                dat_id,
                particle_type: ParticleType::Still,
                max_degrade_distance: 100.0,
                max_particles: 1,
                ..EmitterDesc::default()
            }),
            Vec3::ZERO,
            SpawnOptions {
                attached_object_id: owner,
                ..SpawnOptions::default()
            },
        );
        sys.update_emitter_owner_cell(handle, cell);
        handle
    }

    fn eligible(sys: &ParticleSystem, handle: i32) -> bool {
        sys.emitter(handle).unwrap().view_eligible
    }

    #[test]
    fn completed_retail_view_feeds_next_particle_update() {
        let mut sys = system();
        let h = spawn(&mut sys, 0x3200_0001, 99, 0x0101_0001);
        let mut c = ParticleVisibilityController::new();
        c.begin_frame(Vec3::ZERO);
        c.use_world_view();
        c.mark_visible_landscape_cells([0x0101_0001]).unwrap();
        c.complete_frame();
        c.apply(&mut sys, 1.0);
        assert!(eligible(&sys, h));
    }

    #[test]
    fn unresolved_frame_publishes_empty_world_view() {
        let mut sys = system();
        let h = spawn(&mut sys, 0x3200_0002, 42, 0x0101_0001);
        let mut c = ParticleVisibilityController::new();
        c.begin_frame(Vec3::ZERO);
        c.use_world_view();
        c.complete_frame();
        c.apply(&mut sys, 1.0);
        assert!(!eligible(&sys, h));

        c.begin_frame(Vec3::ZERO);
        c.complete_frame();
        c.apply(&mut sys, 1.0);
        assert!(!eligible(&sys, h));
    }

    #[test]
    fn aborted_frame_preserves_the_last_completed_world_view() {
        let mut sys = system();
        let h = spawn(&mut sys, 0x3200_0003, 42, 0x0101_0001);
        let mut c = ParticleVisibilityController::new();
        c.begin_frame(Vec3::ZERO);
        c.use_world_view();
        c.mark_visible_landscape_cells([0x0101_0001]).unwrap();
        c.complete_frame();

        c.begin_frame(Vec3::new(1000.0, 1000.0, 0.0));
        c.use_world_view();
        c.mark_visible_landscape_cells([0x0202_0001]).unwrap();
        c.abort_frame();
        c.apply(&mut sys, 1.0);
        assert!(eligible(&sys, h));
    }

    #[test]
    fn completed_frame_reports_only_completed_transactions() {
        let mut c = ParticleVisibilityController::new();
        let first = c.completed_landscape_visibility();
        assert!(!first.has_completed_world_view);
        assert!(first.cell_ids.is_empty());

        c.begin_frame(Vec3::ZERO);
        c.use_world_view();
        c.complete_frame();
        let empty = c.completed_landscape_visibility();
        assert!(empty.has_completed_world_view);
        assert!(empty.cell_ids.is_empty());

        c.begin_frame(Vec3::ONE);
        c.use_world_view();
        c.mark_visible_landscape_cells([0x1234_0001]).unwrap();
        assert!(c.completed_landscape_visibility().cell_ids.is_empty());
        c.complete_frame();
        assert!(
            c.completed_landscape_visibility()
                .cell_ids
                .contains(&0x1234_0001)
        );

        c.reset();
        let reset = c.completed_landscape_visibility();
        assert!(!reset.has_completed_world_view);
        assert!(reset.cell_ids.is_empty());
    }

    #[test]
    fn mark_visible_landscape_cells_rejects_non_landscape_ids() {
        for cell in [0u32, 0x0101_0100] {
            let mut c = ParticleVisibilityController::new();
            c.begin_frame(Vec3::ZERO);
            c.use_world_view();
            let error = c.mark_visible_landscape_cells([cell]).unwrap_err();
            assert!(error.to_string().contains(&format!("0x{cell:08X}")));
        }
    }
}
