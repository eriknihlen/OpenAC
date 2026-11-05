//! The login reveal: the world is withheld (the window shows only its clear
//! colour) until the area around the player has been loaded, so the player
//! never watches the ground and the town assemble piece by piece.
//!
//! One readiness rule decides it ([`ReadinessBarrier`]); the window asks
//! the renderer the facts (which landblocks, models and textures have
//! arrived) and shows the world once the rule says ready.

use crate::world_frame::{LandblockCoord, landblocks_around};

/// How far around the destination must be loaded: models within `near`
/// landblocks, terrain within `far`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct RevealWindow {
    /// Landblocks whose objects must be loaded.
    pub near_radius: u32,
    /// Landblocks whose terrain must be loaded.
    pub far_radius: u32,
}

/// Where the reveal stands.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct ReadinessSnapshot {
    /// The destination cell; 0 when there is none.
    pub destination_cell: u32,
    /// The destination is an interior cell.
    pub is_indoor: bool,
    /// The destination can never load (the dats lack it): reveal anyway.
    pub is_unhydratable: bool,
    /// The far radius required.
    pub required_render_radius: u32,
    /// The near radius required.
    pub required_near_radius: u32,
    /// The landblocks and their objects have arrived.
    pub render_ready: bool,
    /// The objects' textures have arrived.
    pub composites_ready: bool,
    /// The ground the player stands on has arrived.
    pub collision_ready: bool,
}

impl ReadinessSnapshot {
    /// There is a destination.
    #[must_use]
    pub fn has_destination(&self) -> bool {
        self.destination_cell != 0
    }

    /// The world may be shown.
    #[must_use]
    pub fn is_ready(&self) -> bool {
        self.has_destination()
            && (self.is_unhydratable
                || (self.render_ready && self.composites_ready && self.collision_ready))
    }
}

/// The facts the readiness rule asks about.
pub trait RevealFacts {
    /// The configured reveal window.
    fn reveal_window(&self) -> RevealWindow;
    /// Everything within `near` (objects) and `far` (terrain) landblocks of
    /// the destination has arrived.
    fn render_neighborhood_ready(&self, cell: u32, near: u32, far: u32) -> bool;
    /// The interior cell the destination is in has arrived.
    fn spawn_cell_ready(&self, cell: u32) -> bool;
    /// The ground within `radius` landblocks has arrived.
    fn terrain_neighborhood_ready(&self, cell: u32, radius: u32) -> bool;
    /// The objects' textures have arrived.
    fn composite_textures_ready(&self) -> bool;
    /// Start loading the objects' textures for the area.
    fn prepare_composite_textures(&mut self, cell: u32, radius: u32);
    /// Forget texture readiness of an earlier destination.
    fn invalidate_composite_textures(&mut self);
    /// The destination can never load.
    fn spawn_claim_unhydratable(&self, cell: u32) -> bool;
}

/// True for an interior cell id (`XXYYNNNN` with `NNNN` at least `0x0100`).
#[must_use]
pub fn is_indoor(cell: u32) -> bool {
    cell & 0xFFFF >= 0x0100
}

/// The readiness rule. Stateless: every evaluation reads the facts afresh.
#[derive(Debug, Default, Clone, Copy)]
pub struct ReadinessBarrier;

impl ReadinessBarrier {
    /// A new reveal begins: texture readiness of the last one is void.
    pub fn begin(facts: &mut impl RevealFacts) {
        facts.invalidate_composite_textures();
    }

    /// The window to wait for: none for an interior (only its own cell),
    /// else the configured window with the near radius clamped to the far.
    pub fn required_window(facts: &impl RevealFacts, cell: u32) -> RevealWindow {
        if is_indoor(cell) {
            return RevealWindow::default();
        }
        let window = facts.reveal_window();
        RevealWindow {
            near_radius: window.near_radius.min(window.far_radius),
            far_radius: window.far_radius,
        }
    }

    /// Start the texture warm-up once the near window has arrived.
    pub fn prepare(facts: &mut impl RevealFacts, cell: u32) {
        if cell == 0 || facts.spawn_claim_unhydratable(cell) {
            return;
        }
        let required = Self::required_window(facts, cell);
        if facts.render_neighborhood_ready(cell, required.near_radius, required.near_radius) {
            facts.prepare_composite_textures(cell, required.near_radius);
        }
    }

    /// Where the reveal of `cell` stands. Each domain is asked only once the
    /// one before it is ready.
    pub fn evaluate(facts: &impl RevealFacts, cell: u32) -> ReadinessSnapshot {
        if cell == 0 {
            return ReadinessSnapshot::default();
        }
        let is_indoor = is_indoor(cell);
        let required = Self::required_window(facts, cell);
        if facts.spawn_claim_unhydratable(cell) {
            return ReadinessSnapshot {
                destination_cell: cell,
                is_indoor,
                is_unhydratable: true,
                required_render_radius: required.far_radius,
                required_near_radius: required.near_radius,
                ..ReadinessSnapshot::default()
            };
        }
        let render_ready =
            facts.render_neighborhood_ready(cell, required.near_radius, required.far_radius);
        let composites_ready = render_ready && facts.composite_textures_ready();
        let collision_ready = composites_ready
            && if is_indoor {
                facts.spawn_cell_ready(cell)
            } else {
                facts.terrain_neighborhood_ready(cell, required.far_radius)
            };
        ReadinessSnapshot {
            destination_cell: cell,
            is_indoor,
            is_unhydratable: false,
            required_render_radius: required.far_radius,
            required_near_radius: required.near_radius,
            render_ready,
            composites_ready,
            collision_ready,
        }
    }
}

/// Every landblock within `radius` of `cell`'s landblock satisfies `loaded`.
#[must_use]
pub fn neighborhood_loaded(
    cell: u32,
    radius: u32,
    mut loaded: impl FnMut(LandblockCoord) -> bool,
) -> bool {
    landblocks_around(LandblockCoord::of_cell(cell), radius)
        .into_iter()
        .all(&mut loaded)
}

/// The diagnostic that replaces the reveal window's far radius.
pub const REVEAL_RADIUS_VAR: &str = "OPENAC_PROBE_REVEAL_RADIUS";

/// Parse the reveal-radius diagnostic: a whole number of at least 1;
/// anything else (including 0, which would never wait for the ground) is
/// ignored.
#[must_use]
pub fn parse_reveal_radius(raw: Option<&str>) -> Option<u32> {
    raw?.trim().parse::<u32>().ok().filter(|r| *r >= 1)
}

/// Apply the reveal-radius diagnostic: it replaces the far radius and the
/// near radius is clamped to it.
#[must_use]
pub fn apply_reveal_radius_override(window: RevealWindow, radius: Option<u32>) -> RevealWindow {
    match radius {
        None => window,
        Some(far) => RevealWindow {
            near_radius: window.near_radius.min(far),
            far_radius: far,
        },
    }
}

/// Raises the renderer's upload budget while the world is withheld (nothing
/// is drawn, so frames can spend their time uploading), for exactly the
/// reveal generation that asked.
#[derive(Debug, Default)]
pub struct RevealUploadPriority {
    active_generation: u64,
}

impl RevealUploadPriority {
    /// A reveal began; returns true (priority on).
    pub fn begin(&mut self, generation: u64) -> bool {
        self.active_generation = generation;
        true
    }

    /// A reveal ended; returns `Some(false)` (priority off) only when it is
    /// the generation that holds the priority.
    pub fn end(&mut self, generation: u64) -> Option<bool> {
        if generation == 0 || self.active_generation != generation {
            return None;
        }
        self.active_generation = 0;
        Some(false)
    }

    /// Whether a reveal holds the priority.
    #[must_use]
    pub fn is_active(&self) -> bool {
        self.active_generation != 0
    }
}

/// The login reveal of one window: begun when the player's position is
/// first known, complete once ready.
#[derive(Debug, Default)]
pub struct LoginReveal {
    generation: u64,
    destination: u32,
    complete: bool,
    priority: RevealUploadPriority,
}

impl LoginReveal {
    /// The world is being withheld.
    #[must_use]
    pub fn is_withholding(&self) -> bool {
        self.destination != 0 && !self.complete
    }

    /// The reveal has finished (the world is shown).
    #[must_use]
    pub fn is_complete(&self) -> bool {
        self.complete
    }

    /// The upload priority is on.
    #[must_use]
    pub fn upload_priority(&self) -> bool {
        self.priority.is_active()
    }

    /// A new login (the session handed the window a new world): the next
    /// known cell begins a new reveal, which withholds the world again
    /// until its area has loaded. An unfinished reveal gives up its upload
    /// priority; generations keep counting.
    pub fn restart(&mut self) {
        self.priority.end(self.generation);
        self.destination = 0;
        self.complete = false;
    }

    /// The player's cell is known: begin the reveal, once per login.
    pub fn begin(&mut self, facts: &mut impl RevealFacts, destination_cell: u32) {
        if self.destination != 0 || destination_cell == 0 {
            return;
        }
        self.generation += 1;
        self.destination = destination_cell;
        ReadinessBarrier::begin(facts);
        self.priority.begin(self.generation);
    }

    /// Prepare and evaluate; when ready, show the world. Returns the
    /// snapshot (none before the reveal began).
    pub fn advance(&mut self, facts: &mut impl RevealFacts) -> Option<ReadinessSnapshot> {
        if self.destination == 0 {
            return None;
        }
        if self.complete {
            return Some(ReadinessBarrier::evaluate(facts, self.destination));
        }
        ReadinessBarrier::prepare(facts, self.destination);
        let snapshot = ReadinessBarrier::evaluate(facts, self.destination);
        if snapshot.is_ready() {
            self.complete = true;
            self.priority.end(self.generation);
        }
        Some(snapshot)
    }
}

#[cfg(test)]
mod tests {
    //! Cases from OpenAC `tests/AcDream.App.Tests/Streaming/WorldRevealReadinessBarrierTests.cs`,
    //! `WorldRevealDerivedWindowIntegrationTests.cs` and
    //! `tests/AcDream.App.Tests/Rendering/WorldRevealRenderResourceSchedulerTests.cs`.
    use super::*;
    use std::cell::Cell;
    use std::collections::HashSet;

    #[derive(Default)]
    struct State {
        render_ready: bool,
        render_ready_by_radius: Option<fn(u32, u32) -> bool>,
        spawn_cell_ready: bool,
        terrain_ready: bool,
        composite_ready: bool,
        unhydratable: bool,
        invalidations: u32,
        preparations: u32,
        prepared_cell: u32,
        prepared_radius: Option<u32>,
        render_near: Cell<Option<u32>>,
        render_far: Cell<Option<u32>>,
        terrain_radius: Cell<Option<u32>>,
        window: RevealWindow,
    }

    impl State {
        fn new() -> Self {
            Self {
                window: RevealWindow {
                    near_radius: 1,
                    far_radius: 1,
                },
                ..Self::default()
            }
        }
    }

    impl RevealFacts for State {
        fn reveal_window(&self) -> RevealWindow {
            self.window
        }
        fn render_neighborhood_ready(&self, _: u32, near: u32, far: u32) -> bool {
            self.render_near.set(Some(near));
            self.render_far.set(Some(far));
            self.render_ready_by_radius
                .map_or(self.render_ready, |f| f(near, far))
        }
        fn spawn_cell_ready(&self, _: u32) -> bool {
            self.spawn_cell_ready
        }
        fn terrain_neighborhood_ready(&self, _: u32, radius: u32) -> bool {
            self.terrain_radius.set(Some(radius));
            self.terrain_ready
        }
        fn composite_textures_ready(&self) -> bool {
            self.composite_ready
        }
        fn prepare_composite_textures(&mut self, cell: u32, radius: u32) {
            self.preparations += 1;
            self.prepared_cell = cell;
            self.prepared_radius = Some(radius);
        }
        fn invalidate_composite_textures(&mut self) {
            self.invalidations += 1;
        }
        fn spawn_claim_unhydratable(&self, _: u32) -> bool {
            self.unhydratable
        }
    }

    const OUTDOOR: u32 = 0x1134_0021;
    const INDOOR: u32 = 0x1134_0100;

    fn window(near_radius: u32, far_radius: u32) -> RevealWindow {
        RevealWindow {
            near_radius,
            far_radius,
        }
    }

    #[test]
    fn begin_invalidates_prior_texture_readiness() {
        let mut s = State::new();
        ReadinessBarrier::begin(&mut s);
        assert_eq!(s.invalidations, 1);
    }

    #[test]
    fn the_outdoor_window_is_the_configured_window() {
        for (near, far) in [(3, 8), (5, 15), (4, 12)] {
            let s = State {
                window: window(near, far),
                ..State::new()
            };
            assert!(!ReadinessBarrier::evaluate(&s, OUTDOOR).is_ready());
            assert_eq!(s.render_far.get(), Some(far));
            assert_eq!(s.render_near.get(), Some(near));
            assert_eq!(
                ReadinessBarrier::required_window(&s, OUTDOOR),
                window(near, far)
            );
        }
    }

    #[test]
    fn an_indoor_window_is_zero() {
        let s = State {
            window: window(3, 8),
            ..State::new()
        };
        assert_eq!(ReadinessBarrier::required_window(&s, INDOOR), window(0, 0));
    }

    #[test]
    fn the_window_is_reread_on_every_evaluation() {
        let mut s = State {
            window: window(3, 8),
            ..State::new()
        };
        ReadinessBarrier::evaluate(&s, OUTDOOR);
        assert_eq!(s.render_far.get(), Some(8));
        s.window = window(5, 15);
        let second = ReadinessBarrier::evaluate(&s, OUTDOOR);
        assert_eq!(s.render_far.get(), Some(15));
        assert_eq!(s.render_near.get(), Some(5));
        assert_eq!(
            (second.required_render_radius, second.required_near_radius),
            (15, 5)
        );
    }

    #[test]
    fn outdoor_reveal_joins_render_textures_and_terrain() {
        let mut s = State {
            window: window(4, 12),
            ..State::new()
        };
        assert!(!ReadinessBarrier::evaluate(&s, OUTDOOR).is_ready());
        s.render_ready = true;
        ReadinessBarrier::prepare(&mut s, OUTDOOR);
        assert_eq!(
            (s.preparations, s.prepared_cell, s.prepared_radius),
            (1, OUTDOOR, Some(4))
        );
        s.composite_ready = true;
        assert!(!ReadinessBarrier::evaluate(&s, OUTDOOR).is_ready());
        s.terrain_ready = true;
        assert!(ReadinessBarrier::evaluate(&s, OUTDOOR).is_ready());
        assert_eq!(s.terrain_radius.get(), Some(12));
    }

    #[test]
    fn indoor_reveal_uses_the_centre_and_the_cell_itself() {
        let mut s = State {
            window: window(4, 12),
            render_ready: true,
            composite_ready: true,
            terrain_ready: true,
            ..State::new()
        };
        ReadinessBarrier::prepare(&mut s, INDOOR);
        assert_eq!(s.prepared_radius, Some(0));
        assert!(!ReadinessBarrier::evaluate(&s, INDOOR).is_ready());
        assert_eq!(s.terrain_radius.get(), None);
        s.spawn_cell_ready = true;
        assert!(ReadinessBarrier::evaluate(&s, INDOOR).is_ready());
        assert_eq!(
            (s.render_near.get(), s.render_far.get()),
            (Some(0), Some(0))
        );
    }

    #[test]
    fn prepare_waits_for_the_near_window() {
        let mut s = State::new();
        ReadinessBarrier::prepare(&mut s, OUTDOOR);
        assert_eq!(s.preparations, 0);
        let mut s = State {
            window: window(4, 12),
            render_ready_by_radius: Some(|_, far| far <= 4),
            ..State::new()
        };
        ReadinessBarrier::prepare(&mut s, OUTDOOR);
        assert_eq!((s.preparations, s.prepared_radius), (1, Some(4)));
        assert!(!ReadinessBarrier::evaluate(&s, OUTDOOR).is_ready());
        assert_eq!(s.render_far.get(), Some(12));
        let mut s = State {
            window: window(4, 12),
            render_ready_by_radius: Some(|_, _| false),
            ..State::new()
        };
        ReadinessBarrier::prepare(&mut s, OUTDOOR);
        assert_eq!(s.preparations, 0);
    }

    #[test]
    fn an_impossible_destination_reveals_at_once() {
        let mut s = State {
            unhydratable: true,
            ..State::new()
        };
        ReadinessBarrier::prepare(&mut s, 0x1134_01FF);
        assert!(ReadinessBarrier::evaluate(&s, 0x1134_01FF).is_ready());
        assert_eq!(s.preparations, 0);
        assert_eq!(s.render_far.get(), None);
    }

    #[test]
    fn evaluate_reports_every_domain_and_short_circuits() {
        let s = State {
            window: window(4, 12),
            render_ready: true,
            composite_ready: true,
            terrain_ready: true,
            ..State::new()
        };
        let snapshot = ReadinessBarrier::evaluate(&s, OUTDOOR);
        assert_eq!(snapshot.destination_cell, OUTDOOR);
        assert!(!snapshot.is_indoor);
        assert!(snapshot.render_ready && snapshot.composites_ready && snapshot.collision_ready);
        assert!(snapshot.is_ready());
        let s = State {
            composite_ready: true,
            terrain_ready: true,
            spawn_cell_ready: true,
            ..State::new()
        };
        let snapshot = ReadinessBarrier::evaluate(&s, INDOOR);
        assert!(!snapshot.render_ready && !snapshot.composites_ready && !snapshot.collision_ready);
        assert_eq!(s.terrain_radius.get(), None);
    }

    #[test]
    fn a_near_radius_beyond_the_far_is_clamped() {
        let s = State {
            window: window(9, 4),
            ..State::new()
        };
        assert_eq!(ReadinessBarrier::required_window(&s, OUTDOOR), window(4, 4));
    }

    #[test]
    fn the_reveal_radius_override_replaces_the_far_radius() {
        let w = window(4, 12);
        assert_eq!(apply_reveal_radius_override(w, None), w);
        assert_eq!(apply_reveal_radius_override(w, Some(1)), window(1, 1));
        assert_eq!(apply_reveal_radius_override(w, Some(25)), window(4, 25));
        for (raw, expected) in [
            (None, None),
            (Some(""), None),
            (Some("nonsense"), None),
            (Some("0"), None),
            (Some("-1"), None),
            (Some("1"), Some(1)),
            (Some("12"), Some(12)),
        ] {
            assert_eq!(parse_reveal_radius(raw), expected, "{raw:?}");
        }
    }

    #[test]
    fn a_replacement_generation_keeps_the_priority_until_it_ends() {
        let mut p = RevealUploadPriority::default();
        let mut seen = Vec::new();
        seen.push(p.begin(3));
        seen.push(p.begin(4));
        seen.extend(p.end(3));
        seen.extend(p.end(4));
        seen.extend(p.end(4));
        assert_eq!(seen, [true, true, false]);
    }

    /// Each login has its own reveal (OpenAC `WorldRevealCoordinator.BeginLogin`
    /// per login generation, `Begin_InvalidatesPriorDestinationAndStartsOneGeneration`):
    /// after a log off and a new entry the world is withheld again until
    /// the area has loaded, under a new generation; a reveal given up
    /// unfinished gives up its upload priority.
    #[test]
    fn every_login_is_revealed_afresh() {
        let mut s = State {
            render_ready: true,
            composite_ready: true,
            terrain_ready: true,
            ..State::new()
        };
        let mut reveal = LoginReveal::default();
        reveal.begin(&mut s, OUTDOOR);
        assert!(reveal.advance(&mut s).unwrap().is_ready());
        assert!(reveal.is_complete() && !reveal.upload_priority());
        // The same login: nothing more.
        reveal.begin(&mut s, OUTDOOR);
        assert!(!reveal.is_withholding());
        assert_eq!(s.invalidations, 1);

        reveal.restart();
        assert!(!reveal.is_withholding() && !reveal.is_complete());
        assert!(reveal.advance(&mut s).is_none(), "no cell known yet");
        s.terrain_ready = false;
        reveal.begin(&mut s, INDOOR);
        assert_eq!(reveal.generation, 2);
        assert_eq!(s.invalidations, 2);
        assert!(reveal.is_withholding() && reveal.upload_priority());
        assert!(!reveal.advance(&mut s).unwrap().is_ready());
        // Given up before it finished: the priority goes with it.
        reveal.restart();
        assert!(!reveal.upload_priority() && !reveal.is_withholding());
        reveal.begin(&mut s, OUTDOOR);
        assert_eq!(reveal.generation, 3);
        assert!(reveal.is_withholding() && reveal.upload_priority());
    }

    /// The hold tracks the outer boundary ring by ring (OpenAC
    /// `OutdoorReveal_HoldsUntilTheWholeDerivedWindowIsPublished`).
    #[test]
    fn the_reveal_holds_until_the_whole_window_is_loaded() {
        struct Area {
            loaded: HashSet<LandblockCoord>,
            window: RevealWindow,
        }
        impl RevealFacts for Area {
            fn reveal_window(&self) -> RevealWindow {
                self.window
            }
            fn render_neighborhood_ready(&self, cell: u32, near: u32, far: u32) -> bool {
                neighborhood_loaded(cell, near.max(far), |b| self.loaded.contains(&b))
            }
            fn spawn_cell_ready(&self, _: u32) -> bool {
                true
            }
            fn terrain_neighborhood_ready(&self, cell: u32, radius: u32) -> bool {
                neighborhood_loaded(cell, radius, |b| self.loaded.contains(&b))
            }
            fn composite_textures_ready(&self) -> bool {
                true
            }
            fn prepare_composite_textures(&mut self, _: u32, _: u32) {}
            fn invalidate_composite_textures(&mut self) {}
            fn spawn_claim_unhydratable(&self, _: u32) -> bool {
                false
            }
        }
        const CELL: u32 = 0x4040_0021;
        for (near, far) in [(1, 2), (2, 4)] {
            let mut area = Area {
                loaded: HashSet::new(),
                window: window(near, far),
            };
            let mut reveal = LoginReveal::default();
            reveal.begin(&mut area, CELL);
            assert!(reveal.is_withholding() && reveal.upload_priority());
            let center = LandblockCoord::of_cell(CELL);
            for ring in 0..=far {
                assert!(
                    !reveal.advance(&mut area).unwrap().is_ready(),
                    "ring {ring}"
                );
                area.loaded.extend(
                    landblocks_around(center, ring)
                        .into_iter()
                        .filter(|b| crate::world_frame::ring_distance(*b, center) == ring),
                );
            }
            let ready = reveal.advance(&mut area).unwrap();
            assert!(ready.is_ready());
            assert_eq!(
                (ready.required_render_radius, ready.required_near_radius),
                (far, near)
            );
            assert!(!reveal.is_withholding() && reveal.is_complete() && !reveal.upload_priority());
        }
    }
}
