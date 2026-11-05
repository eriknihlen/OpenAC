//! The shape of one frame: prepare, open it, draw the world, draw the
//! presentation over it (the retail UI), close it, then the after-frame
//! work (a pending screenshot, diagnostics), each in a fixed order and each
//! timed by the frame profiler.
//!
//! The frame is prepared before its swapchain image is acquired: preparing
//! needs no image, and acquiring waits until the presentation engine has
//! let go of an image, which it has more likely done once the preparation
//! has run. Acquiring first left that wait (about a millisecond a frame on
//! the Vulkan swapchain on Windows) on the frame's critical path.
//!
//! The window supplies the phases ([`FramePhases`]); [`render_frame`] owns
//! the order and the failure rule: a phase that fails after the frame was
//! opened closes the frame without presenting it, exactly once, and the
//! failure is returned. A failed preparation opens no frame.

use crate::profiler::{FrameProfiler, FrameStage};

/// What a frame is drawn for.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct FrameInput {
    /// Seconds since the previous frame.
    pub delta_seconds: f64,
    /// Drawable width in pixels.
    pub width: u32,
    /// Drawable height in pixels.
    pub height: u32,
}

/// What the world pass drew.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct WorldOutcome {
    /// Landblocks drawn after culling.
    pub visible_landblocks: usize,
    /// Landblocks loaded.
    pub total_landblocks: usize,
    /// The world was drawn (false while it is withheld or not loaded).
    pub normal_world_drawn: bool,
}

/// What a frame produced.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct FrameOutcome {
    /// The world pass.
    pub world: WorldOutcome,
    /// A pending screenshot was taken from this frame.
    pub screenshot_captured: bool,
    /// The window has no area: nothing was opened or drawn.
    pub skipped_zero_area: bool,
    /// The swapchain had no image this time: nothing was drawn.
    pub skipped_no_image: bool,
}

/// The window's part of a frame.
pub trait FramePhases {
    /// Why a phase failed.
    type Error;

    /// Open the frame (acquire the swapchain image). `Ok(false)` when there
    /// is no image this time; the frame is then skipped.
    fn begin_frame(&mut self) -> Result<bool, Self::Error>;
    /// Start measuring the frame's GPU work.
    fn begin_measure(&mut self) -> Result<(), Self::Error>;
    /// Upload and prepare everything the world pass draws. Runs before the
    /// frame is opened, and also for a frame that then has no image.
    fn prepare(&mut self, input: &FrameInput) -> Result<(), Self::Error>;
    /// Record the world pass.
    fn render_world(&mut self, input: &FrameInput) -> Result<WorldOutcome, Self::Error>;
    /// Record what is drawn over the world: the retail UI.
    fn render_presentation(
        &mut self,
        input: &FrameInput,
        world: &WorldOutcome,
    ) -> Result<(), Self::Error>;
    /// Stop measuring the frame's GPU work.
    fn end_measure(&mut self) -> Result<(), Self::Error>;
    /// Close the frame: submit and present.
    fn end_frame(&mut self) -> Result<(), Self::Error>;
    /// Close a frame that failed: drop what was recorded and the acquired
    /// image without presenting.
    fn abort_frame(&mut self);
    /// Take a screenshot if one is pending; true when one was taken.
    fn capture_pending_screenshot(&mut self, input: &FrameInput) -> Result<bool, Self::Error>;
    /// Publish the frame's diagnostics (title, profiler).
    fn publish(&mut self, input: &FrameInput, outcome: &FrameOutcome) -> Result<(), Self::Error>;
}

/// Run one frame through `phases` in the fixed order. A zero-area window
/// skips the frame before anything is opened; a phase that fails while the
/// frame is open aborts it once and returns the failure.
pub fn render_frame<P: FramePhases>(
    phases: &mut P,
    profiler: &mut FrameProfiler,
    input: &FrameInput,
) -> Result<FrameOutcome, P::Error> {
    if input.width == 0 || input.height == 0 {
        return Ok(FrameOutcome {
            skipped_zero_area: true,
            ..FrameOutcome::default()
        });
    }
    let token = profiler.begin(FrameStage::Upload);
    let prepared = phases.prepare(input);
    profiler.end(token);
    prepared?;
    let token = profiler.begin(FrameStage::Present);
    let opened = phases.begin_frame();
    profiler.end(token);
    if !opened? {
        return Ok(FrameOutcome {
            skipped_no_image: true,
            ..FrameOutcome::default()
        });
    }

    let drawn = (|| {
        phases.begin_measure()?;
        let token = profiler.begin(FrameStage::Render);
        let world = phases
            .render_world(input)
            .and_then(|world| phases.render_presentation(input, &world).map(|()| world));
        profiler.end(token);
        let world = world?;
        phases.end_measure()?;
        Ok(world)
    })();
    let world = match drawn {
        Ok(world) => world,
        Err(error) => {
            phases.abort_frame();
            return Err(error);
        }
    };

    let token = profiler.begin(FrameStage::Present);
    let closed = phases.end_frame();
    profiler.end(token);
    closed?;

    let token = profiler.begin(FrameStage::Diagnostics);
    let after = (|| {
        let screenshot_captured = phases.capture_pending_screenshot(input)?;
        let outcome = FrameOutcome {
            world,
            screenshot_captured,
            ..FrameOutcome::default()
        };
        phases.publish(input, &outcome)?;
        Ok(outcome)
    })();
    profiler.end(token);
    after
}

#[cfg(test)]
mod tests {
    //! Cases from OpenAC `tests/AcDream.App.Tests/Rendering/RenderFrameOrchestratorTests.cs`
    //! and `RenderFrameRecoveryIntegrationTests.cs`.
    use super::*;

    #[derive(Default)]
    struct Recording {
        calls: Vec<&'static str>,
        fail_at: Option<&'static str>,
        no_image: bool,
        world: WorldOutcome,
        capture: bool,
        published: Option<FrameOutcome>,
    }

    impl Recording {
        fn step(&mut self, name: &'static str) -> Result<(), &'static str> {
            self.calls.push(name);
            if self.fail_at == Some(name) {
                Err(name)
            } else {
                Ok(())
            }
        }
    }

    impl FramePhases for Recording {
        type Error = &'static str;
        fn begin_frame(&mut self) -> Result<bool, &'static str> {
            self.step("gpu-begin")?;
            Ok(!self.no_image)
        }
        fn begin_measure(&mut self) -> Result<(), &'static str> {
            self.step("measure-begin")
        }
        fn prepare(&mut self, _: &FrameInput) -> Result<(), &'static str> {
            self.step("resources")
        }
        fn render_world(&mut self, _: &FrameInput) -> Result<WorldOutcome, &'static str> {
            self.step("world")?;
            Ok(self.world)
        }
        fn render_presentation(
            &mut self,
            _: &FrameInput,
            _: &WorldOutcome,
        ) -> Result<(), &'static str> {
            self.step("presentation")
        }
        fn end_measure(&mut self) -> Result<(), &'static str> {
            self.step("measure-end")
        }
        fn end_frame(&mut self) -> Result<(), &'static str> {
            self.step("gpu-end")
        }
        fn abort_frame(&mut self) {
            self.calls.push("abort");
        }
        fn capture_pending_screenshot(&mut self, _: &FrameInput) -> Result<bool, &'static str> {
            self.step("screenshot")?;
            Ok(self.capture)
        }
        fn publish(&mut self, _: &FrameInput, outcome: &FrameOutcome) -> Result<(), &'static str> {
            self.step("diagnostics")?;
            self.published = Some(*outcome);
            Ok(())
        }
    }

    const INPUT: FrameInput = FrameInput {
        delta_seconds: 1.0 / 60.0,
        width: 1280,
        height: 720,
    };

    fn run(phases: &mut Recording, input: &FrameInput) -> Result<FrameOutcome, &'static str> {
        render_frame(phases, &mut FrameProfiler::new(true, false), input)
    }

    #[test]
    fn the_outer_order_is_fixed_and_the_outcome_carries_the_world() {
        let mut p = Recording {
            world: WorldOutcome {
                visible_landblocks: 12,
                total_landblocks: 40,
                normal_world_drawn: true,
            },
            capture: true,
            ..Recording::default()
        };
        let outcome = run(&mut p, &INPUT).unwrap();
        assert_eq!(
            p.calls,
            [
                "resources",
                "gpu-begin",
                "measure-begin",
                "world",
                "presentation",
                "measure-end",
                "gpu-end",
                "screenshot",
                "diagnostics"
            ]
        );
        assert_eq!(outcome.world, p.world);
        assert!(outcome.screenshot_captured);
        assert_eq!(p.published, Some(outcome));
    }

    #[test]
    fn a_zero_area_window_skips_before_any_gpu_work() {
        for (width, height) in [(0, 1080), (1920, 0), (0, 0)] {
            let mut p = Recording::default();
            let outcome = run(
                &mut p,
                &FrameInput {
                    width,
                    height,
                    ..INPUT
                },
            )
            .unwrap();
            assert!(outcome.skipped_zero_area);
            assert!(p.calls.is_empty());
        }
    }

    #[test]
    fn no_swapchain_image_skips_the_frame_after_trying_to_open_it() {
        let mut p = Recording {
            no_image: true,
            ..Recording::default()
        };
        let outcome = run(&mut p, &INPUT).unwrap();
        assert!(outcome.skipped_no_image);
        assert_eq!(p.calls, ["resources", "gpu-begin"]);
    }

    #[test]
    fn a_failure_to_open_attempts_nothing_else() {
        let mut p = Recording {
            fail_at: Some("gpu-begin"),
            ..Recording::default()
        };
        assert_eq!(run(&mut p, &INPUT), Err("gpu-begin"));
        assert_eq!(p.calls, ["resources", "gpu-begin"]);
    }

    #[test]
    fn a_failure_to_prepare_opens_no_frame() {
        let mut p = Recording {
            fail_at: Some("resources"),
            ..Recording::default()
        };
        assert_eq!(run(&mut p, &INPUT), Err("resources"));
        assert_eq!(p.calls, ["resources"]);
    }

    #[test]
    fn a_failure_inside_the_frame_aborts_it_once_and_propagates() {
        for point in ["measure-begin", "world", "presentation", "measure-end"] {
            let mut p = Recording {
                fail_at: Some(point),
                ..Recording::default()
            };
            assert_eq!(run(&mut p, &INPUT), Err(point));
            let at = p.calls.iter().position(|c| *c == point).unwrap();
            assert_eq!(p.calls[at + 1..], ["abort"], "{point}");
            assert!(!p.calls.contains(&"gpu-end"));
        }
    }

    #[test]
    fn a_failure_after_the_frame_closed_does_not_abort_or_close_again() {
        for point in ["screenshot", "diagnostics"] {
            let mut p = Recording {
                fail_at: Some(point),
                ..Recording::default()
            };
            assert_eq!(run(&mut p, &INPUT), Err(point));
            assert_eq!(p.calls.iter().filter(|c| **c == "gpu-end").count(), 1);
            assert!(!p.calls.contains(&"abort"));
        }
    }

    #[test]
    fn a_close_failure_propagates_directly() {
        let mut p = Recording {
            fail_at: Some("gpu-end"),
            ..Recording::default()
        };
        assert_eq!(run(&mut p, &INPUT), Err("gpu-end"));
        assert_eq!(p.calls.last(), Some(&"gpu-end"));
        assert!(!p.calls.contains(&"abort"));
    }

    #[test]
    fn the_next_frame_after_a_failure_succeeds() {
        let mut p = Recording {
            fail_at: Some("world"),
            ..Recording::default()
        };
        assert!(run(&mut p, &INPUT).is_err());
        p.fail_at = None;
        p.calls.clear();
        assert!(run(&mut p, &INPUT).is_ok());
        assert_eq!(p.calls.last(), Some(&"diagnostics"));
    }

    #[test]
    fn the_profiler_sees_render_present_and_diagnostics() {
        let mut p = Recording::default();
        let mut profiler = FrameProfiler::new(true, false);
        let t = std::time::Instant::now();
        profiler.frame_boundary(t);
        render_frame(&mut p, &mut profiler, &INPUT).unwrap();
        profiler.frame_boundary(t + std::time::Duration::from_millis(16));
        // Each stage was opened and closed; the recording phases are too
        // quick to measure reliably, so only the bookkeeping is checked.
        for stage in [
            FrameStage::Present,
            FrameStage::Render,
            FrameStage::Upload,
            FrameStage::Diagnostics,
        ] {
            assert!(profiler.last_stage_us(stage) >= 0);
        }
    }
}
