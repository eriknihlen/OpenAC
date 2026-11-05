//! The selection flash: an object picked in the world lights up bright,
//! then dim, then bright and dim again, one flip every 0.2 s, and returns
//! to normal. The paper doll's selected-item flash lights only the item's
//! parts (or a whole figure) for one bright and one dim interval.
//!
//! A flash lights by two numbers: a luminosity added to every surface and
//! a factor on the scene's light (normal is 0 and 1).

/// How a flashing object is lit.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct SelectionLighting {
    /// Light added regardless of the scene, 0..1.
    pub luminosity: f32,
    /// Factor on the scene's light.
    pub diffuse: f32,
}

impl SelectionLighting {
    /// Unlit by any flash.
    pub const NORMAL: Self = Self {
        luminosity: 0.0,
        diffuse: 1.0,
    };
    /// The flash's dim step.
    pub const LOW: Self = Self {
        luminosity: 0.0,
        diffuse: 0.35,
    };
    /// The flash's bright step.
    pub const HIGH: Self = Self {
        luminosity: 0.99,
        diffuse: 1.0,
    };
}

/// Seconds between flips.
pub const FLIP_INTERVAL_SECONDS: f64 = 0.2;
/// Flips a world pick runs through.
pub const WORLD_FLIP_BUDGET: u32 = 4;
/// Flips the doll's flash runs through: bright, dim, done.
pub const PART_FLIP_BUDGET: u32 = 2;

/// The one flash in flight (a new one replaces it). Times are seconds on
/// any steady clock the caller keeps.
#[derive(Debug, Clone, Default)]
pub struct SelectionLightingPulse {
    guid: u32,
    local_id: u32,
    part_mask: u32,
    flip_count: u32,
    flip_budget: u32,
    next_flip: f64,
    lighting: Option<SelectionLighting>,
}

impl SelectionLightingPulse {
    /// A pulse with nothing flashing.
    #[must_use]
    pub fn new() -> Self {
        Self {
            flip_budget: WORLD_FLIP_BUDGET,
            ..Self::default()
        }
    }

    /// Flash the whole object picked in the world.
    pub fn start(&mut self, guid: u32, local_id: u32, now: f64) {
        self.begin(guid, local_id, 0, WORLD_FLIP_BUDGET, now);
    }

    /// Flash a whole figure on the doll's schedule.
    pub fn start_whole_figure(&mut self, guid: u32, local_id: u32, now: f64) {
        self.begin(guid, local_id, 0, PART_FLIP_BUDGET, now);
    }

    /// Flash only the parts of one object whose bits are set (bit N lights
    /// part N) on the doll's schedule. No parts flashes nothing and leaves
    /// a flash in flight alone.
    pub fn start_parts(&mut self, guid: u32, local_id: u32, part_mask: u32, now: f64) {
        if part_mask == 0 {
            return;
        }
        self.begin(guid, local_id, part_mask, PART_FLIP_BUDGET, now);
    }

    fn begin(&mut self, guid: u32, local_id: u32, part_mask: u32, budget: u32, now: f64) {
        if guid == 0 || local_id == 0 {
            self.clear();
            return;
        }
        self.guid = guid;
        self.local_id = local_id;
        self.part_mask = part_mask;
        self.flip_count = 1;
        self.flip_budget = budget;
        self.lighting = Some(SelectionLighting::HIGH);
        self.next_flip = now + FLIP_INTERVAL_SECONDS;
    }

    /// One frame at `now`: at most one flip however long the frame was.
    pub fn tick(&mut self, now: f64) {
        if self.flip_count == 0 || now < self.next_flip {
            return;
        }
        let next = self.flip_count + 1;
        if next > self.flip_budget {
            self.clear();
            return;
        }
        self.flip_count = next;
        self.lighting = Some(if next & 1 != 0 {
            SelectionLighting::HIGH
        } else {
            SelectionLighting::LOW
        });
        self.next_flip = now + FLIP_INTERVAL_SECONDS;
    }

    fn is(&self, guid: u32, local_id: u32) -> bool {
        self.flip_count != 0
            && guid != 0
            && local_id != 0
            && guid == self.guid
            && local_id == self.local_id
    }

    /// Whether some parts of this object are lit by a parts flash.
    #[must_use]
    pub fn has_part_lighting(&self, guid: u32, local_id: u32) -> bool {
        self.part_mask != 0 && self.is(guid, local_id)
    }

    /// How part `part` of this object is lit by a parts flash.
    #[must_use]
    pub fn part_lighting(&self, guid: u32, local_id: u32, part: u32) -> Option<SelectionLighting> {
        (self.has_part_lighting(guid, local_id) && part < 32 && self.part_mask & (1 << part) != 0)
            .then_some(self.lighting)
            .flatten()
    }

    /// How this whole object is lit (a parts flash never answers).
    #[must_use]
    pub fn lighting(&self, guid: u32, local_id: u32) -> Option<SelectionLighting> {
        (self.part_mask == 0 && self.is(guid, local_id))
            .then_some(self.lighting)
            .flatten()
    }

    /// End any flash.
    pub fn clear(&mut self) {
        *self = Self::new();
    }
}

#[cfg(test)]
mod tests {
    //! OpenAC `tests/AcDream.App.Tests/Rendering/RetailSelectionLightingPulseTests.cs`
    //! and `RetailSelectionPartLightingTests.cs`.

    use super::*;

    const GUID: u32 = 0xDA11_D011;
    const ENTITY: u32 = 0xDA11_D012;
    const STEP: f64 = FLIP_INTERVAL_SECONDS;

    fn mask(parts: &[u32]) -> u32 {
        parts.iter().fold(0, |m, p| m | (1 << p))
    }

    #[test]
    fn a_click_runs_high_low_high_low_then_restores() {
        let mut p = SelectionLightingPulse::new();
        p.start(0x5000_1234, 1234, 10.0);
        assert_eq!(p.lighting(0x5000_1234, 1234), Some(SelectionLighting::HIGH));
        p.tick(10.199);
        assert_eq!(p.lighting(0x5000_1234, 1234), Some(SelectionLighting::HIGH));
        p.tick(10.2);
        assert_eq!(p.lighting(0x5000_1234, 1234), Some(SelectionLighting::LOW));
        p.tick(10.4);
        assert_eq!(p.lighting(0x5000_1234, 1234), Some(SelectionLighting::HIGH));
        p.tick(10.6);
        assert_eq!(p.lighting(0x5000_1234, 1234), Some(SelectionLighting::LOW));
        p.tick(10.8);
        assert_eq!(p.lighting(0x5000_1234, 1234), None);
    }

    #[test]
    fn a_long_frame_advances_only_one_flip() {
        let mut p = SelectionLightingPulse::new();
        p.start(0x5000_1234, 1234, 1.0);
        p.tick(20.0);
        assert_eq!(p.lighting(0x5000_1234, 1234), Some(SelectionLighting::LOW));
    }

    #[test]
    fn a_new_click_restores_the_old_target_and_starts_high_on_the_new() {
        let mut p = SelectionLightingPulse::new();
        p.start(0x5000_0001, 1, 4.0);
        p.tick(4.2);
        assert_eq!(p.lighting(0x5000_0001, 1), Some(SelectionLighting::LOW));
        p.start(0x5000_0002, 2, 4.2);
        assert_eq!(p.lighting(0x5000_0001, 1), None);
        assert_eq!(p.lighting(0x5000_0002, 2), Some(SelectionLighting::HIGH));
    }

    #[test]
    fn a_reused_guid_does_not_pass_the_flash_to_the_new_incarnation() {
        let mut p = SelectionLightingPulse::new();
        p.start(0x5000_0001, 10, 1.0);
        assert_eq!(p.lighting(0x5000_0001, 11), None);
        assert_eq!(p.lighting(0x5000_0001, 10), Some(SelectionLighting::HIGH));
    }

    #[test]
    fn a_part_flash_lights_only_its_own_parts() {
        let mut p = SelectionLightingPulse::new();
        p.start_parts(GUID, ENTITY, mask(&[0x00, 0x09, 0x0A]), 0.0);
        assert!(p.has_part_lighting(GUID, ENTITY));
        for lit in [0x00, 0x09, 0x0A] {
            assert!(p.part_lighting(GUID, ENTITY, lit).is_some(), "{lit:#x}");
        }
        for dark in [0x01, 0x08, 0x0B, 0x10, 31] {
            assert!(p.part_lighting(GUID, ENTITY, dark).is_none(), "{dark:#x}");
        }
    }

    #[test]
    fn a_part_flash_never_answers_for_the_whole_object() {
        let mut p = SelectionLightingPulse::new();
        p.start_parts(GUID, ENTITY, mask(&[0x10]), 0.0);
        assert_eq!(p.lighting(GUID, ENTITY), None);
    }

    #[test]
    fn a_part_flash_is_bright_then_dim_then_over() {
        let mut p = SelectionLightingPulse::new();
        let mut now = 0.0;
        p.start_parts(GUID, ENTITY, mask(&[0x10]), now);
        assert_eq!(
            p.part_lighting(GUID, ENTITY, 0x10),
            Some(SelectionLighting::HIGH)
        );
        now += STEP;
        p.tick(now);
        assert_eq!(
            p.part_lighting(GUID, ENTITY, 0x10),
            Some(SelectionLighting::LOW)
        );
        now += STEP;
        p.tick(now);
        assert!(!p.has_part_lighting(GUID, ENTITY));
        assert!(p.part_lighting(GUID, ENTITY, 0x10).is_none());
    }

    #[test]
    fn a_part_flash_reaches_neither_another_object_nor_the_id_under_another_guid() {
        let mut p = SelectionLightingPulse::new();
        p.start_parts(GUID, ENTITY, mask(&[0x10]), 0.0);
        assert!(!p.has_part_lighting(GUID, ENTITY + 1));
        assert!(p.part_lighting(GUID, ENTITY + 1, 0x10).is_none());
        assert!(!p.has_part_lighting(GUID + 1, ENTITY));
        assert!(p.part_lighting(GUID + 1, ENTITY, 0x10).is_none());
    }

    #[test]
    fn a_whole_figure_flash_runs_the_dolls_two_flips() {
        let mut p = SelectionLightingPulse::new();
        let mut now = 0.0;
        p.start_whole_figure(GUID, ENTITY, now);
        assert_eq!(p.lighting(GUID, ENTITY), Some(SelectionLighting::HIGH));
        assert!(!p.has_part_lighting(GUID, ENTITY));
        now += STEP;
        p.tick(now);
        assert_eq!(p.lighting(GUID, ENTITY), Some(SelectionLighting::LOW));
        now += STEP;
        p.tick(now);
        assert_eq!(p.lighting(GUID, ENTITY), None);
    }

    #[test]
    fn an_empty_mask_flashes_nothing_and_disturbs_nothing() {
        let mut p = SelectionLightingPulse::new();
        p.start_parts(GUID, ENTITY, 0, 0.0);
        assert!(!p.has_part_lighting(GUID, ENTITY));
        assert_eq!(p.lighting(GUID, ENTITY), None);
        p.start(GUID, ENTITY, 0.0);
        p.start_parts(GUID, ENTITY, 0, 0.0);
        assert!(p.lighting(GUID, ENTITY).is_some());
    }

    #[test]
    fn a_world_pick_lights_the_whole_object_for_four_flips() {
        let mut p = SelectionLightingPulse::new();
        let mut now = 0.0;
        p.start(GUID, ENTITY, now);
        assert!(!p.has_part_lighting(GUID, ENTITY));
        for flip in 1..WORLD_FLIP_BUDGET {
            assert!(p.lighting(GUID, ENTITY).is_some(), "flip {flip}");
            now += STEP;
            p.tick(now);
        }
        assert!(p.lighting(GUID, ENTITY).is_some());
        now += STEP;
        p.tick(now);
        assert!(p.lighting(GUID, ENTITY).is_none());
    }

    #[test]
    fn a_part_flash_replaces_a_world_pick_and_back_again() {
        let mut p = SelectionLightingPulse::new();
        p.start(GUID, ENTITY, 0.0);
        p.start_parts(GUID, ENTITY, mask(&[0x09]), 0.0);
        assert!(p.lighting(GUID, ENTITY).is_none());
        assert!(p.part_lighting(GUID, ENTITY, 0x09).is_some());
        p.start(GUID, ENTITY, 0.0);
        assert!(p.lighting(GUID, ENTITY).is_some());
        assert!(!p.has_part_lighting(GUID, ENTITY));
    }
}
