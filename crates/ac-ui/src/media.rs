//! A state's media program played over time: images, pauses, jumps and a
//! hand-off to another state.

/// What one step of a state's media program does.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub enum MediaStepKind {
    /// Anything not acted on (sound, movie, message, ...); kept so jump
    /// indices still land on the authored step.
    #[default]
    Other,
    /// Show an image.
    Image,
    /// Hold the current frame.
    Pause,
    /// Continue at another step.
    Jump,
    /// Hand the element to another state when the program ends.
    State,
}

/// One step of a media program.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct MediaStep {
    /// What the step does.
    pub kind: MediaStepKind,
    /// Image: the render surface.
    pub file: u32,
    /// Image: the draw mode.
    pub draw_mode: u32,
    /// Pause: shortest hold, seconds.
    pub min_duration: f32,
    /// Pause: longest hold, seconds.
    pub max_duration: f32,
    /// Jump: the step index; State: the state id.
    pub jump_index: u32,
    /// Jump / State: chance it happens.
    pub probability: f32,
    /// The dat media kind number of an `Other` step.
    pub raw_type: i32,
}

/// Steps followed in one sample before giving up (a zero-time jump cycle).
const MAXIMUM_STEPS: usize = 512;

/// True when the program changes over time. A single image is a still
/// frame and keeps the ordinary path.
#[must_use]
pub fn is_animated(steps: &[MediaStep]) -> bool {
    if steps.len() < 2 {
        return false;
    }
    let mut images = 0;
    for step in steps {
        match step.kind {
            MediaStepKind::Pause | MediaStepKind::Jump | MediaStepKind::State => return true,
            MediaStepKind::Image => {
                images += 1;
                if images > 1 {
                    return true;
                }
            }
            MediaStepKind::Other => {}
        }
    }
    false
}

/// The frame showing `elapsed` seconds after the state was entered (0 when
/// no image has been reached), and the state the program hands off to once
/// it has run out. Pauses hold for their minimum; jumps and hand-offs
/// happen only when certain (probability 1).
#[must_use]
pub fn sample(steps: &[MediaStep], elapsed: f32) -> (u32, Option<u32>) {
    let mut file = 0;
    let mut at = 0.0f32;
    let mut cursor = 0usize;
    for _ in 0..MAXIMUM_STEPS {
        let Some(step) = steps.get(cursor) else {
            return (file, None);
        };
        match step.kind {
            MediaStepKind::Image => {
                file = step.file;
                cursor += 1;
            }
            MediaStepKind::Pause => {
                at += step.min_duration.max(0.0);
                if elapsed < at {
                    return (file, None);
                }
                cursor += 1;
            }
            MediaStepKind::Jump => {
                if step.probability >= 1.0 {
                    cursor = step.jump_index as usize;
                } else {
                    cursor += 1;
                }
            }
            MediaStepKind::State => {
                return (file, (step.probability >= 1.0).then_some(step.jump_index));
            }
            MediaStepKind::Other => cursor += 1,
        }
    }
    (file, None)
}

/// A looping frame animation (an animation step followed by a jump back to
/// it).
#[derive(Debug, Clone, PartialEq)]
pub struct LoopingAnimation {
    /// Render surfaces, in order.
    pub frames: Vec<u32>,
    /// Seconds for one cycle.
    pub duration: f32,
    /// Draw mode.
    pub draw_mode: u32,
}

impl LoopingAnimation {
    /// The frame at `elapsed` seconds: the phase through the cycle picks a
    /// frame, rounding to the nearest. 0 without frames or a usable
    /// duration.
    #[must_use]
    pub fn sample(&self, elapsed: f64) -> u32 {
        if self.frames.is_empty() || !self.duration.is_finite() || self.duration <= 0.0 {
            return 0;
        }
        let time = if elapsed.is_finite() {
            elapsed.max(0.0)
        } else {
            0.0
        };
        let duration = f64::from(self.duration);
        let phase = ((time % duration) / duration) as f32;
        let index = (f64::from(phase) * (self.frames.len() - 1) as f64 + 0.5) as usize;
        self.frames[index.min(self.frames.len() - 1)]
    }
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.App.Tests/UI/Layout/UiMediaSequenceTests.cs`.

    use super::*;

    fn image(file: u32) -> MediaStep {
        MediaStep {
            kind: MediaStepKind::Image,
            file,
            draw_mode: 1,
            ..MediaStep::default()
        }
    }

    fn pause(seconds: f32) -> MediaStep {
        MediaStep {
            kind: MediaStepKind::Pause,
            min_duration: seconds,
            max_duration: seconds,
            ..MediaStep::default()
        }
    }

    fn state(id: u32, probability: f32) -> MediaStep {
        MediaStep {
            kind: MediaStepKind::State,
            jump_index: id,
            probability,
            ..MediaStep::default()
        }
    }

    fn jump(index: u32, probability: f32) -> MediaStep {
        MediaStep {
            kind: MediaStepKind::Jump,
            jump_index: index,
            probability,
            ..MediaStep::default()
        }
    }

    fn blink() -> Vec<MediaStep> {
        let mut v = Vec::new();
        for _ in 0..3 {
            v.extend([
                image(0x0600_5F0E),
                pause(0.5),
                image(0x0600_5F0F),
                pause(0.5),
            ]);
        }
        v.push(state(13, 1.0));
        v
    }

    #[test]
    fn the_frame_alternates_every_half_second() {
        for (elapsed, expected) in [
            (0.0, 0x0600_5F0E),
            (0.4, 0x0600_5F0E),
            (0.5, 0x0600_5F0F),
            (0.9, 0x0600_5F0F),
            (1.0, 0x0600_5F0E),
            (2.75, 0x0600_5F0F),
        ] {
            assert_eq!(sample(&blink(), elapsed), (expected, None), "{elapsed}");
        }
    }

    #[test]
    fn after_three_seconds_it_hands_off_to_ghosted() {
        assert_eq!(sample(&blink(), 3.0).1, Some(13));
        assert_eq!(sample(&blink(), 2.99).1, None);
    }

    #[test]
    fn a_single_image_is_not_an_animation() {
        assert!(!is_animated(&[image(1)]));
        assert!(!is_animated(&[]));
        assert!(is_animated(&[image(1), pause(1.0)]));
        assert!(is_animated(&[image(1), image(2)]));
    }

    #[test]
    fn a_jump_loops_forever() {
        let looping = [
            image(0xAA),
            pause(1.0),
            image(0xBB),
            pause(1.0),
            jump(0, 1.0),
        ];
        assert_eq!(sample(&looping, 0.5).0, 0xAA);
        assert_eq!(sample(&looping, 1.5).0, 0xBB);
        assert_eq!(sample(&looping, 2.5).0, 0xAA);
        assert_eq!(sample(&looping, 101.5).0, 0xBB);
    }

    #[test]
    fn a_zero_time_jump_cycle_terminates() {
        assert_eq!(sample(&[image(0xAA), jump(0, 1.0)], 1.0).0, 0xAA);
    }

    #[test]
    fn an_uncertain_branch_falls_through() {
        let maybe = [image(0xAA), pause(1.0), jump(0, 0.5), image(0xBB)];
        assert_eq!(sample(&maybe, 1.5).0, 0xBB);
    }

    #[test]
    fn unknown_steps_keep_jump_indices() {
        let with_other = [MediaStep::default(), image(0xCC), pause(1.0), jump(1, 1.0)];
        assert_eq!(sample(&with_other, 0.5).0, 0xCC);
        assert_eq!(sample(&with_other, 5.0).0, 0xCC);
    }

    #[test]
    fn looping_animation_rounds_the_phase_to_a_frame() {
        let a = LoopingAnimation {
            frames: vec![1, 2, 3],
            duration: 1.0,
            draw_mode: 1,
        };
        assert_eq!(a.sample(0.0), 1);
        assert_eq!(a.sample(0.3), 2);
        assert_eq!(a.sample(0.8), 3);
        assert_eq!(a.sample(1.1), 1);
        assert_eq!(
            LoopingAnimation {
                frames: vec![],
                duration: 1.0,
                draw_mode: 1
            }
            .sample(0.5),
            0
        );
    }
}
