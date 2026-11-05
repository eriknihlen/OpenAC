//! The floating world origin: which landblock sits at (0, 0) of the
//! coordinates the world is drawn and streamed in.
//!
//! Far from the map's corner, f32 coordinates lose precision, so the world
//! is expressed relative to a center landblock near the player and
//! recentered as the player travels. A landblock (x, y) then lies at
//! `((x - center_x) * 192, (y - center_y) * 192)` meters. Before the first
//! server-confirmed player position the center is only a placeholder.

use std::fmt;

use crate::physics::BLOCK_LENGTH;

/// The current origin landblock and whether the server confirmed it.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct LiveWorldOrigin {
    center_x: i32,
    center_y: i32,
    is_known: bool,
}

/// The runtime's world frame and the streamed origin name different center
/// landblocks: every entity placed now would land a whole number of
/// landblocks away from the ground around it.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct WorldFrameDisagreement {
    /// The runtime's center landblock (x, y).
    pub runtime_center: (i32, i32),
    /// The streamed origin's center landblock (x, y).
    pub streamed_center: (i32, i32),
    /// The landblock id being placed when the check failed.
    pub projecting_landblock_id: u32,
}

impl WorldFrameDisagreement {
    /// The misplacement in meters (x, y).
    #[must_use]
    pub fn offset_meters(&self) -> (f32, f32) {
        (
            (self.runtime_center.0 - self.streamed_center.0) as f32 * BLOCK_LENGTH,
            (self.runtime_center.1 - self.streamed_center.1) as f32 * BLOCK_LENGTH,
        )
    }
}

impl fmt::Display for WorldFrameDisagreement {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        let (dx, dy) = self.offset_meters();
        write!(
            f,
            "World-frame owners disagree: Runtime centre ({},{}) vs streamed origin ({},{}) \
             while projecting landblock 0x{:08X}. That offsets the entity by ({dx:.0}m,{dy:.0}m) \
             from its geometry.",
            self.runtime_center.0,
            self.runtime_center.1,
            self.streamed_center.0,
            self.streamed_center.1,
            self.projecting_landblock_id,
        )
    }
}

impl std::error::Error for WorldFrameDisagreement {}

impl LiveWorldOrigin {
    /// An unknown origin at landblock (0, 0).
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// The center landblock (x, y).
    #[must_use]
    pub fn center(&self) -> (i32, i32) {
        (self.center_x, self.center_y)
    }

    /// True once a server-confirmed position set the origin.
    #[must_use]
    pub fn is_known(&self) -> bool {
        self.is_known
    }

    /// Sets a provisional center; the origin stays unknown.
    pub fn set_placeholder(&mut self, center_x: i32, center_y: i32) {
        self.center_x = center_x;
        self.center_y = center_y;
        self.is_known = false;
    }

    /// Accepts the first confirmed center; false (and no change) if the
    /// origin is already known.
    pub fn try_initialize(&mut self, center_x: i32, center_y: i32) -> bool {
        if self.is_known {
            return false;
        }
        self.center_x = center_x;
        self.center_y = center_y;
        self.is_known = true;
        true
    }

    /// Moves the center, keeping whether it is known.
    pub fn recenter(&mut self, center_x: i32, center_y: i32) {
        self.center_x = center_x;
        self.center_y = center_y;
    }

    /// Ends the confirmed origin; its center remains as the next
    /// placeholder.
    pub fn reset(&mut self) {
        self.is_known = false;
    }

    /// Checks that the runtime's world frame (center landblock id
    /// `runtime_center_landblock_id`, 0 when it has none yet) agrees with
    /// this origin before landblock `projecting_landblock_id` is placed.
    /// While a portal or teleport transit is in flight a disagreement is
    /// expected and only deferred: the answer is `Ok(false)` and nothing
    /// changes.
    ///
    /// # Errors
    ///
    /// [`WorldFrameDisagreement`] when both frames are established, differ,
    /// and no transit is in flight.
    pub fn try_ensure_agrees_with_runtime_frame(
        &self,
        runtime_center_landblock_id: u32,
        projecting_landblock_id: u32,
        transit_in_flight: bool,
    ) -> Result<bool, WorldFrameDisagreement> {
        if !self.is_known || runtime_center_landblock_id == 0 {
            return Ok(true);
        }
        let runtime_center = (
            ((runtime_center_landblock_id >> 24) & 0xFF) as i32,
            ((runtime_center_landblock_id >> 16) & 0xFF) as i32,
        );
        if runtime_center == self.center() {
            return Ok(true);
        }
        if transit_in_flight {
            return Ok(false);
        }
        Err(WorldFrameDisagreement {
            runtime_center,
            streamed_center: self.center(),
            projecting_landblock_id,
        })
    }

    /// [`Self::try_ensure_agrees_with_runtime_frame`] with no transit in
    /// flight.
    ///
    /// # Errors
    ///
    /// [`WorldFrameDisagreement`] when both frames are established and
    /// differ.
    pub fn ensure_agrees_with_runtime_frame(
        &self,
        runtime_center_landblock_id: u32,
        projecting_landblock_id: u32,
    ) -> Result<(), WorldFrameDisagreement> {
        self.try_ensure_agrees_with_runtime_frame(
            runtime_center_landblock_id,
            projecting_landblock_id,
            false,
        )
        .map(|_| ())
    }

    /// Converts a world-frame position back to the landblock-local frame of
    /// `cell_id`'s landblock.
    #[must_use]
    pub fn cell_local_for_seed(&self, world_position: [f32; 3], cell_id: u32) -> [f32; 3] {
        let landblock_x = ((cell_id >> 24) & 0xFF) as i32;
        let landblock_y = ((cell_id >> 16) & 0xFF) as i32;
        [
            world_position[0] - (landblock_x - self.center_x) as f32 * BLOCK_LENGTH,
            world_position[1] - (landblock_y - self.center_y) as f32 * BLOCK_LENGTH,
            world_position[2],
        ]
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // Golden values below: OpenAC tests/AcDream.App.Tests/World/LiveWorldOriginStateTests.cs.

    #[test]
    fn placeholder_is_not_authoritative_until_initialized() {
        let mut s = LiveWorldOrigin::new();
        s.set_placeholder(42, 43);
        assert_eq!(s.center(), (42, 43));
        assert!(!s.is_known());
        assert!(s.try_initialize(10, 11));
        assert_eq!(s.center(), (10, 11));
        assert!(s.is_known());
        assert!(!s.try_initialize(20, 21));
        assert_eq!(s.center(), (10, 11));
    }

    #[test]
    fn recenter_then_reset_keeps_the_center_as_placeholder() {
        let mut s = LiveWorldOrigin::new();
        assert!(s.try_initialize(1, 2));
        s.recenter(3, 4);
        s.reset();
        assert_eq!(s.center(), (3, 4));
        assert!(!s.is_known());
        assert!(s.try_initialize(5, 6));
    }

    #[test]
    fn recenter_before_initialization_stays_unknown_and_reset_is_idempotent() {
        let mut s = LiveWorldOrigin::new();
        s.set_placeholder(20, 21);
        s.recenter(30, 31);
        s.reset();
        s.reset();
        assert_eq!(s.center(), (30, 31));
        assert!(!s.is_known());
        assert!(s.try_initialize(40, 41));
        s.recenter(50, 51);
        assert!(s.is_known());
        assert_eq!(s.center(), (50, 51));
    }

    #[test]
    fn cell_local_for_seed_uses_the_current_origin() {
        let mut s = LiveWorldOrigin::new();
        s.set_placeholder(0x30, 0x32);
        assert_eq!(
            s.cell_local_for_seed([200.0, -300.0, 5.0], 0x3130_0001),
            [8.0, 84.0, 5.0]
        );
    }

    #[test]
    fn agreeing_frames_pass() {
        let mut s = LiveWorldOrigin::new();
        assert!(s.try_initialize(0xA9, 0xB6));
        assert_eq!(
            s.ensure_agrees_with_runtime_frame(0xA9B6_FFFF, 0xA9B6_0001),
            Ok(())
        );
        assert_eq!(
            s.try_ensure_agrees_with_runtime_frame(0xA9B6_FFFF, 0xA9B6_0001, true),
            Ok(true)
        );
    }

    #[test]
    fn nothing_to_agree_on_before_both_frames_exist() {
        let mut s = LiveWorldOrigin::new();
        assert!(
            s.ensure_agrees_with_runtime_frame(0xA9B6_FFFF, 0xA9B6_0001)
                .is_ok()
        );
        assert!(s.try_initialize(0xA9, 0xB6));
        assert!(s.ensure_agrees_with_runtime_frame(0, 0xA9B6_0001).is_ok());
    }

    #[test]
    fn disagreeing_frames_report_the_offset_in_meters() {
        for (runtime_center, offset) in [
            (0xAAB6_FFFF, "(192m,0m)"),
            (0xA8B6_FFFF, "(-192m,0m)"),
            (0xA9B7_FFFF, "(0m,192m)"),
            (0xA9B5_FFFF, "(0m,-192m)"),
        ] {
            let mut s = LiveWorldOrigin::new();
            assert!(s.try_initialize(0xA9, 0xB6));
            let err = s
                .ensure_agrees_with_runtime_frame(runtime_center, 0xA9B6_0001)
                .unwrap_err()
                .to_string();
            assert!(err.contains("World-frame owners disagree"), "{err}");
            assert!(err.contains(offset), "{err}");
            assert!(err.contains("0xA9B60001"), "{err}");
        }
    }

    #[test]
    fn a_runtime_rebase_ahead_of_the_streamed_origin_is_caught() {
        let mut s = LiveWorldOrigin::new();
        assert!(s.try_initialize(0x09, 0x04));
        let err = s
            .ensure_agrees_with_runtime_frame(0xF682_FFFF, 0xF682_0033)
            .unwrap_err()
            .to_string();
        assert!(err.contains("45504m"), "{err}");
        assert!(err.contains("0xF6820033"), "{err}");
    }

    #[test]
    fn frames_agree_again_after_both_rebase() {
        let mut s = LiveWorldOrigin::new();
        assert!(s.try_initialize(0x09, 0x04));
        s.recenter(0xF6, 0x82);
        assert!(
            s.ensure_agrees_with_runtime_frame(0xF682_FFFF, 0xF682_0033)
                .is_ok()
        );
    }

    #[test]
    fn disagreement_during_transit_defers_without_changing_the_origin() {
        let mut s = LiveWorldOrigin::new();
        assert!(s.try_initialize(0x09, 0x04));
        assert_eq!(
            s.try_ensure_agrees_with_runtime_frame(0xF682_FFFF, 0xF682_0033, true),
            Ok(false)
        );
        assert_eq!(s.center(), (0x09, 0x04));
        s.recenter(0xF6, 0x82);
        assert_eq!(
            s.try_ensure_agrees_with_runtime_frame(0xF682_FFFF, 0xF682_0033, false),
            Ok(true)
        );
    }
}
