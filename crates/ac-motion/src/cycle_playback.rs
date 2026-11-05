//! A stand-alone looping playback cursor over one animation span, for
//! presentation-only animation that has no motion table (it never fires
//! hooks or moves the object).

use ac_dat::objects::Animation;
use glam::{Quat, Vec3};

use crate::frame::{quat_from_dat, vec3_from_dat};

/// Advance `curr_frame` by `elapsed_seconds × framerate` within
/// `low_frame..=high_frame`, wrapping past the high frame and stopping at
/// the low frame. A span of one frame or less, or no elapsed time, leaves
/// the cursor where it is.
#[must_use]
pub fn advance(
    curr_frame: f32,
    low_frame: i32,
    high_frame: i32,
    framerate: f32,
    elapsed_seconds: f32,
) -> f32 {
    let span = high_frame - low_frame;
    if span <= 0 || elapsed_seconds <= 0.0 {
        return curr_frame;
    }
    let mut next = curr_frame + elapsed_seconds * framerate;
    if next > high_frame as f32 {
        let over = next - low_frame as f32;
        next = low_frame as f32 + over % (span + 1) as f32;
    } else if next < low_frame as f32 {
        next = low_frame as f32;
    }
    next
}

/// Part `part_index`'s placement at fractional frame `curr_frame`, blended
/// between its keyframe and the next (wrapping to `low_frame` at the end of
/// the span). `None` when the animation has no such part.
#[must_use]
pub fn try_interpolate_part(
    animation: &Animation,
    curr_frame: f32,
    low_frame: i32,
    high_frame: i32,
    part_index: usize,
) -> Option<(Vec3, Quat)> {
    let count = animation.part_frames.len();
    let mut frame_index = curr_frame.floor() as i32;
    if frame_index < low_frame || frame_index > high_frame || frame_index as usize >= count {
        frame_index = low_frame;
    }
    let mut next_index = frame_index + 1;
    if next_index > high_frame || next_index as usize >= count {
        next_index = low_frame;
    }
    let t = (curr_frame - frame_index as f32).clamp(0.0, 1.0);

    let frames = &animation
        .part_frames
        .get(usize::try_from(frame_index).ok()?)?
        .frames;
    let next_frames = &animation
        .part_frames
        .get(usize::try_from(next_index).ok()?)?
        .frames;
    let first = frames.get(part_index)?;
    let next = next_frames.get(part_index).unwrap_or(first);
    let origin = lerp(vec3_from_dat(first.origin), vec3_from_dat(next.origin), t);
    let orientation = slerp(
        quat_from_dat(first.orientation),
        quat_from_dat(next.orientation),
        t,
    );
    Some((origin, orientation))
}

/// Linear blend `a·(1−t) + b·t`.
#[must_use]
pub(crate) fn lerp(a: Vec3, b: Vec3, t: f32) -> Vec3 {
    a * (1.0 - t) + b * t
}

/// Spherical blend along the shorter arc, falling back to a straight
/// blend when the rotations are almost equal.
#[must_use]
fn slerp(q1: Quat, q2: Quat, amount: f32) -> Quat {
    const EPSILON: f32 = 1e-6;
    let mut cos_omega = q1.dot(q2);
    let mut flip = false;
    if cos_omega < 0.0 {
        flip = true;
        cos_omega = -cos_omega;
    }
    let (s1, s2) = if cos_omega > 1.0 - EPSILON {
        (1.0 - amount, if flip { -amount } else { amount })
    } else {
        let omega = cos_omega.acos();
        let inv_sin_omega = 1.0 / omega.sin();
        let s1 = ((1.0 - amount) * omega).sin() * inv_sin_omega;
        let s2 = (amount * omega).sin() * inv_sin_omega;
        (s1, if flip { -s2 } else { s2 })
    };
    Quat::from_xyzw(
        s1 * q1.x + s2 * q2.x,
        s1 * q1.y + s2 * q2.y,
        s1 * q1.z + s2 * q2.z,
        s1 * q1.w + s2 * q2.w,
    )
}

#[cfg(test)]
mod tests {
    //! Golden cases from OpenAC `RetailAnimationCyclePlaybackTests`.
    use super::*;
    use crate::frame::Frame;
    use crate::test_support::{make_anim, uniform_anim};

    #[test]
    fn advance_within_the_span() {
        assert!((advance(5.0, 0, 29, 30.0, 0.1) - 8.0).abs() < 1e-4);
    }

    #[test]
    fn advance_past_the_high_frame_wraps() {
        // 25 + 6 = 31; over = 31; 31 % 30 = 1.
        assert!((advance(25.0, 0, 29, 30.0, 0.2) - 1.0).abs() < 1e-4);
    }

    #[test]
    fn advance_below_the_low_frame_clamps() {
        assert_eq!(advance(-5.0, 0, 29, 30.0, 0.05), 0.0);
    }

    #[test]
    fn degenerate_span_or_time_leaves_the_cursor() {
        assert_eq!(advance(3.0, 0, 0, 30.0, 1.0), 3.0);
        assert_eq!(advance(3.0, 0, -1, 30.0, 1.0), 3.0);
        assert_eq!(advance(3.0, 0, 29, 0.0, 1.0), 3.0);
        assert_eq!(advance(3.0, 0, 29, 30.0, 0.0), 3.0);
        assert_eq!(advance(3.0, 0, 29, 30.0, -1.0), 3.0);
    }

    #[test]
    fn negative_framerate_moves_back_and_clamps() {
        assert_eq!(advance(5.0, 0, 29, -2.0, 1.0), 3.0);
        assert_eq!(advance(1.0, 0, 29, -2.0, 1.0), 0.0);
    }

    #[test]
    fn exact_frame_returns_that_pose() {
        let anim = uniform_anim(3, 2, Vec3::new(1.0, 2.0, 3.0), Quat::IDENTITY);
        let (origin, orientation) = try_interpolate_part(&anim, 1.0, 0, 2, 0).unwrap();
        assert_eq!(origin, Vec3::new(1.0, 2.0, 3.0));
        assert_eq!(orientation, Quat::IDENTITY);
    }

    fn two_frames(x0: f32, x1: f32) -> Animation {
        make_anim(2, 1, |f, _| {
            Frame::new(
                Vec3::new(if f == 0 { x0 } else { x1 }, 0.0, 0.0),
                Quat::IDENTITY,
            )
        })
    }

    #[test]
    fn between_frames_blends_halfway() {
        let (origin, _) = try_interpolate_part(&two_frames(0.0, 10.0), 0.5, 0, 1, 0).unwrap();
        assert_eq!(origin, Vec3::new(5.0, 0.0, 0.0));
    }

    #[test]
    fn at_the_high_frame_the_next_frame_wraps_to_low() {
        let (origin, _) = try_interpolate_part(&two_frames(1.0, 2.0), 1.0, 0, 1, 0).unwrap();
        assert_eq!(origin, Vec3::new(2.0, 0.0, 0.0));
    }

    #[test]
    fn missing_part_is_none() {
        let anim = uniform_anim(2, 1, Vec3::ZERO, Quat::IDENTITY);
        assert!(try_interpolate_part(&anim, 0.0, 0, 1, 5).is_none());
    }
}
