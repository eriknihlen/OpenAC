//! The character-creation preview's state: the figure turning on its two
//! buttons, the camera sliding between its close and far eyes, and the
//! rule for when the figure is rebuilt.
//!
//! Two previews exist, one per wizard page: the appearance page starts at
//! the close eye (the face) and has the turn and zoom buttons; the summary
//! page stays at the far eye (the whole body).

use ac_core::chargen::ChargenAppearanceSelection;
use glam::{Quat, Vec3};

use super::camera::{
    ROTATION_SECONDS_PER_REVOLUTION, ZOOM_TWEEN_SECONDS, chargen_default_eye,
    chargen_zoomed_out_eye,
};
use super::pose::ChargenFigure;

/// The heading the figure starts at, degrees: facing the viewer.
pub const DEFAULT_HEADING: f32 = 180.0;

/// A turn button's direction.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum RotateDirection {
    /// Clockwise seen from above (the heading grows).
    Clockwise,
    /// Counter-clockwise (the heading shrinks).
    CounterClockwise,
}

/// The figure's turn: a button starts it turning one way at a full turn
/// per three seconds; the same button again stops it, the other turns it
/// the other way.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Rotation {
    /// The heading, degrees in 0..=360.
    pub heading: f32,
    direction: Option<RotateDirection>,
    rotating: bool,
    /// The time of the last tick (`None`: the next tick only starts the
    /// clock).
    last: Option<f64>,
}

impl Default for Rotation {
    fn default() -> Self {
        Self::new(DEFAULT_HEADING)
    }
}

impl Rotation {
    /// Standing still at `heading` degrees.
    #[must_use]
    pub fn new(heading: f32) -> Self {
        Self {
            heading,
            direction: None,
            rotating: false,
            last: None,
        }
    }

    /// Turning now.
    #[must_use]
    pub fn is_rotating(&self) -> bool {
        self.rotating
    }

    /// The way it turns (or last turned).
    #[must_use]
    pub fn direction(&self) -> Option<RotateDirection> {
        self.direction
    }

    /// A turn button was pressed.
    pub fn toggle(&mut self, direction: RotateDirection) {
        if self.rotating && self.direction == Some(direction) {
            self.rotating = false;
            return;
        }
        self.direction = Some(direction);
        self.last = None;
        self.rotating = true;
    }

    /// Turn for the time since the last tick (`now`, seconds).
    pub fn tick(&mut self, now: f64) {
        if !self.rotating {
            return;
        }
        let last = match self.last {
            Some(t) if t > 0.0 => t,
            _ => now,
        };
        let delta = ((now - last) / f64::from(ROTATION_SECONDS_PER_REVOLUTION)) * 360.0;
        self.heading = match self.direction {
            Some(RotateDirection::CounterClockwise) => self.heading - delta as f32,
            _ => self.heading + delta as f32,
        };
        if self.heading < 0.0 {
            self.heading += 360.0;
        }
        if self.heading > 360.0 {
            self.heading -= 360.0;
        }
        self.last = Some(now);
    }

    /// The figure's orientation: a pure turn to the heading.
    #[must_use]
    pub fn orientation(&self) -> Quat {
        ac_motion::move_to_math::set_heading(self.heading)
    }
}

/// The camera's slide between its eyes: linear over 0.6 s from where the
/// eye was when the button was pressed, the clock starting at the first
/// tick after.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct ZoomTween {
    start: Vec3,
    target: Vec3,
    started: f64,
    duration: Option<f64>,
    animating: bool,
}

impl ZoomTween {
    fn start(&mut self, from: Vec3, to: Vec3) {
        self.start = from;
        self.target = to;
        self.animating = true;
        self.duration = None;
    }

    /// The eye at `now` (`None`: not sliding).
    fn tick(&mut self, now: f64) -> Option<Vec3> {
        if !self.animating {
            return None;
        }
        let duration = match self.duration {
            Some(d) if d > 0.0 => d,
            _ => {
                self.started = now;
                let d = f64::from(ZOOM_TWEEN_SECONDS);
                self.duration = Some(d);
                d
            }
        };
        let mut elapsed = now - self.started;
        if elapsed >= duration {
            self.animating = false;
            elapsed = duration;
        }
        Some(self.start.lerp(self.target, (elapsed / duration) as f32))
    }
}

/// What the preview was last built from.
#[derive(Debug, Clone, Copy, PartialEq)]
struct Built {
    heritage: u32,
    gender: i32,
    selection: ChargenAppearanceSelection,
}

/// One creation preview. See the module documentation.
#[derive(Debug, Clone)]
pub struct ChargenPreviewState {
    summary: bool,
    /// The camera's eye.
    pub eye: Vec3,
    rotation: Rotation,
    /// Exists once a figure was built (nothing slides before).
    zoom: Option<ZoomTween>,
    /// The zoom the page asked for. It outlives each built figure, and a
    /// request made before the first one (the page opens zoomed in) is
    /// kept for it.
    zoomed_in: bool,
    figure: Option<ChargenFigure>,
    built: Option<Built>,
    backdrop: u32,
    last_tick: Option<f64>,
}

impl ChargenPreviewState {
    /// The appearance page's preview (`summary` false: the close eye) or
    /// the summary page's (the far eye).
    #[must_use]
    pub fn new(summary: bool) -> Self {
        Self {
            summary,
            eye: if summary {
                chargen_zoomed_out_eye(0)
            } else {
                chargen_default_eye(0)
            },
            rotation: Rotation::default(),
            zoom: None,
            zoomed_in: false,
            figure: None,
            built: None,
            backdrop: 0,
            last_tick: None,
        }
    }

    /// Whether the figure must be rebuilt for this look.
    #[must_use]
    pub fn needs_rebuild(
        &self,
        heritage: u32,
        gender: i32,
        selection: &ChargenAppearanceSelection,
    ) -> bool {
        self.built
            != Some(Built {
                heritage,
                gender,
                selection: *selection,
            })
    }

    /// Show `figure` for this look; `backdrop` is the heritage's scenery
    /// Setup. A new heritage or gender moves the eye to that body's eye and
    /// drops a slide in progress; a new look of the same body keeps both
    /// (the face and clothes choices change the headgear as they zoom); a
    /// zoomed-in preview stays zoomed in (at rest).
    pub fn rebuild(
        &mut self,
        heritage: u32,
        gender: i32,
        selection: ChargenAppearanceSelection,
        mut figure: ChargenFigure,
        backdrop: u32,
    ) {
        if self.zoomed_in {
            figure.set_zoomed_in(true);
        }
        let body_changed = self
            .built
            .is_none_or(|b| b.heritage != heritage || b.gender != gender);
        if body_changed {
            self.eye = if self.summary {
                chargen_zoomed_out_eye(heritage)
            } else {
                chargen_default_eye(heritage)
            };
            self.zoom = None;
        }
        if self.built.is_none_or(|b| b.heritage != heritage) {
            self.backdrop = backdrop;
        }
        self.zoom.get_or_insert_with(ZoomTween::default);
        self.figure = Some(figure);
        self.built = Some(Built {
            heritage,
            gender,
            selection,
        });
    }

    /// Zoom in: slide to the close eye and freeze the figure at rest.
    pub fn zoom_in(&mut self) {
        self.zoomed_in = true;
        let heritage = self.built.map_or(0, |b| b.heritage);
        let (Some(zoom), Some(figure)) = (self.zoom.as_mut(), self.figure.as_mut()) else {
            return;
        };
        if figure.is_zoomed_in() {
            return;
        }
        zoom.start(self.eye, chargen_default_eye(heritage));
        figure.set_zoomed_in(true);
    }

    /// Zoom out: slide to the far eye and start the idle loop again.
    pub fn zoom_out(&mut self) {
        self.zoomed_in = false;
        let heritage = self.built.map_or(0, |b| b.heritage);
        let (Some(zoom), Some(figure)) = (self.zoom.as_mut(), self.figure.as_mut()) else {
            return;
        };
        if !figure.is_zoomed_in() {
            return;
        }
        zoom.start(self.eye, chargen_zoomed_out_eye(heritage));
        figure.set_zoomed_in(false);
    }

    /// A turn button was pressed.
    pub fn rotate(&mut self, direction: RotateDirection) {
        self.rotation.toggle(direction);
    }

    /// Advance the idle loop, the turn and the slide to `now` (seconds).
    pub fn tick(&mut self, now: f64) {
        let elapsed = self.last_tick.map_or(now, |t| (now - t).max(0.0));
        self.last_tick = Some(now);
        if let Some(figure) = self.figure.as_mut() {
            figure.tick(elapsed as f32);
        }
        self.rotation.tick(now);
        if let Some(eye) = self.zoom.as_mut().and_then(|z| z.tick(now)) {
            self.eye = eye;
        }
    }

    /// The figure, once built.
    #[must_use]
    pub fn figure(&self) -> Option<&ChargenFigure> {
        self.figure.as_ref()
    }

    /// The figure's orientation.
    #[must_use]
    pub fn orientation(&self) -> Quat {
        self.rotation.orientation()
    }

    /// The scenery Setup behind the figure (0: none).
    #[must_use]
    pub fn backdrop(&self) -> u32 {
        self.backdrop
    }

    /// The preview is zoomed in (its figure, once built, at rest).
    #[must_use]
    pub fn is_zoomed_in(&self) -> bool {
        self.zoomed_in
    }
}

#[cfg(test)]
mod tests {
    //! Cases from OpenAC `ChargenPreviewRotationControllerTests.cs`,
    //! `ChargenPreviewZoomControllerTests.cs` and
    //! `ChargenPreviewControllerTests.cs`.
    use std::sync::Arc;

    use super::*;
    use crate::meshing::MeshRef;
    use ac_dat::objects::{Animation, AnimationFlags, AnimationFrame};
    use ac_dat::types::{Frame, Quaternion, Vector3};
    use glam::Mat4;

    const ALUVIAN: u32 = 1;
    const OLTHOI: u32 = 12;

    fn figure() -> ChargenFigure {
        let mut setup = crate::animation::test_support::setup(1);
        setup.default_scale = vec![Vector3 {
            x: 1.0,
            y: 1.0,
            z: 1.0,
        }];
        let at = |x| AnimationFrame {
            frames: vec![Frame {
                origin: Vector3 { x, y: 0.0, z: 0.0 },
                orientation: Quaternion {
                    w: 1.0,
                    x: 0.0,
                    y: 0.0,
                    z: 0.0,
                },
            }],
            hooks: Vec::new(),
        };
        let idle = Arc::new(Animation {
            id: 0x0300_0001,
            flags: AnimationFlags(0),
            num_parts: 1,
            pos_frames: Vec::new(),
            part_frames: vec![at(1.0)],
        });
        ChargenFigure::new(
            &setup,
            &[MeshRef {
                gfx_obj_id: 0x0100_0001,
                transform: Mat4::IDENTITY,
                variant: 0,
            }],
            Some(&at(99.0)),
            Some(idle),
        )
    }

    fn built(summary: bool, heritage: u32) -> ChargenPreviewState {
        let mut p = ChargenPreviewState::new(summary);
        p.rebuild(
            heritage,
            1,
            ChargenAppearanceSelection::default(),
            figure(),
            0x0200_0BEE,
        );
        p
    }

    fn near(a: Vec3, b: Vec3) -> bool {
        a.abs_diff_eq(b, 1e-3)
    }

    #[test]
    fn the_turn_starts_at_180_degrees() {
        assert_eq!(Rotation::default().heading, 180.0);
    }

    #[test]
    fn a_button_starts_turning_the_same_stops_the_other_reverses() {
        let mut r = Rotation::new(0.0);
        r.toggle(RotateDirection::Clockwise);
        assert!(r.is_rotating());
        assert_eq!(r.direction(), Some(RotateDirection::Clockwise));
        r.toggle(RotateDirection::Clockwise);
        assert!(!r.is_rotating());
        r.toggle(RotateDirection::Clockwise);
        r.toggle(RotateDirection::CounterClockwise);
        assert!(r.is_rotating());
        assert_eq!(r.direction(), Some(RotateDirection::CounterClockwise));
    }

    #[test]
    fn the_turn_is_three_seconds_a_revolution() {
        let mut idle = Rotation::new(0.0);
        idle.tick(100.0);
        assert_eq!(idle.heading, 0.0);

        let mut r = Rotation::new(0.0);
        r.toggle(RotateDirection::Clockwise);
        r.tick(1000.0);
        assert_eq!(r.heading, 0.0, "the first tick only starts the clock");

        let mut r = Rotation::new(0.0);
        r.toggle(RotateDirection::Clockwise);
        r.tick(10.0);
        r.tick(11.5);
        assert!((r.heading - 180.0).abs() < 1e-3);

        let mut r = Rotation::new(0.0);
        r.toggle(RotateDirection::CounterClockwise);
        r.tick(10.0);
        r.tick(11.5);
        assert!((r.heading - 180.0).abs() < 1e-3, "wraps positive");

        let mut r = Rotation::new(0.0);
        r.toggle(RotateDirection::Clockwise);
        r.tick(10.0);
        r.tick(10.5);
        r.tick(11.0);
        assert!((r.heading - 120.0).abs() < 1e-3);

        let mut r = Rotation::new(0.0);
        r.toggle(RotateDirection::Clockwise);
        r.tick(10.0);
        r.tick(13.5);
        assert!((r.heading - 60.0).abs() < 1e-3, "past 360 comes back");
    }

    #[test]
    fn heading_zero_is_no_turn() {
        let q = Rotation::new(0.0).orientation();
        assert!(q.abs_diff_eq(Quat::IDENTITY, 1e-4));
    }

    #[test]
    fn a_zoom_before_the_figure_is_built_is_kept_for_it() {
        // OpenAC `ChargenPreviewControllerTests.
        // ZoomIn_BeforeTheFirstCompose_IsKept_SoZoomOutWorksRightAfter`: the
        // appearance page opens zoomed in before the figure exists.
        let mut p = ChargenPreviewState::new(false);
        let eye = p.eye;
        p.zoom_in();
        p.tick(1.0);
        p.tick(5.0);
        assert_eq!(p.eye, eye, "nothing to slide yet");
        p.rebuild(
            ALUVIAN,
            1,
            ChargenAppearanceSelection::default(),
            figure(),
            0x0200_0BEE,
        );
        assert!(p.is_zoomed_in());
        assert_eq!(p.figure().unwrap().parts()[0].transform.w_axis.x, 99.0);
        p.zoom_out();
        assert!(!p.is_zoomed_in());
        p.tick(100.0);
        p.tick(200.0);
        assert_eq!(p.eye, chargen_zoomed_out_eye(ALUVIAN));
    }

    #[test]
    fn zoom_in_freezes_at_once_and_slides_after() {
        let mut p = built(false, ALUVIAN);
        p.eye = chargen_zoomed_out_eye(ALUVIAN);
        p.zoom_in();
        assert!(p.is_zoomed_in());
        assert_eq!(p.figure().unwrap().parts()[0].transform.w_axis.x, 99.0);
        // The eye has not jumped.
        assert_eq!(p.eye, chargen_zoomed_out_eye(ALUVIAN));
        p.tick(10.0);
        p.tick(10.0 + f64::from(ZOOM_TWEEN_SECONDS) + 1.0);
        let done = p.eye;
        assert!(near(done, chargen_default_eye(ALUVIAN)));
        // Zooming in again changes nothing.
        p.zoom_in();
        p.tick(9999.0);
        assert_eq!(p.eye, done);
    }

    #[test]
    fn zoom_out_restarts_the_loop_and_slides_halfway_at_half_time() {
        let mut p = built(false, ALUVIAN);
        let start = p.eye;
        // Zooming out when not zoomed in does nothing.
        p.zoom_out();
        assert_eq!(p.eye, start);
        assert!(!p.is_zoomed_in());
        p.zoom_in();
        p.zoom_out();
        assert_eq!(p.figure().unwrap().parts()[0].transform.w_axis.x, 1.0);
        p.tick(100.0);
        p.tick(100.0 + f64::from(ZOOM_TWEEN_SECONDS) / 2.0);
        assert!(near(
            p.eye,
            start.lerp(chargen_zoomed_out_eye(ALUVIAN), 0.5)
        ));
        p.tick(200.0);
        assert_eq!(p.eye, chargen_zoomed_out_eye(ALUVIAN));
        p.tick(300.0);
        assert_eq!(p.eye, chargen_zoomed_out_eye(ALUVIAN));
    }

    #[test]
    fn a_new_body_moves_the_eye_and_a_new_look_keeps_it() {
        let mut p = built(false, ALUVIAN);
        assert_eq!(p.eye, chargen_default_eye(ALUVIAN));
        assert_eq!(p.backdrop(), 0x0200_0BEE);
        p.eye = Vec3::splat(5.0);
        let other = ChargenAppearanceSelection {
            hair_style: 2,
            ..ChargenAppearanceSelection::default()
        };
        assert!(p.needs_rebuild(ALUVIAN, 1, &other));
        assert!(!p.needs_rebuild(ALUVIAN, 1, &ChargenAppearanceSelection::default()));
        p.rebuild(ALUVIAN, 1, other, figure(), 0x0200_0001);
        assert_eq!(p.eye, Vec3::splat(5.0), "same body: the eye stays");
        assert_eq!(
            p.backdrop(),
            0x0200_0BEE,
            "same heritage: the backdrop stays"
        );
        p.rebuild(OLTHOI, 1, other, figure(), 0x0200_0002);
        assert_eq!(p.eye, chargen_default_eye(OLTHOI));
        assert_eq!(p.backdrop(), 0x0200_0002);
    }

    #[test]
    fn a_new_look_keeps_the_slide_in_progress() {
        // The clothes choice puts the headgear back and zooms out: the new
        // look's figure must not stop the slide.
        let mut p = built(false, ALUVIAN);
        p.zoom_in();
        p.tick(1.0);
        p.tick(10.0);
        p.zoom_out();
        let other = ChargenAppearanceSelection {
            headgear_style: 1,
            ..ChargenAppearanceSelection::default()
        };
        p.rebuild(ALUVIAN, 1, other, figure(), 0x0200_0BEE);
        p.tick(20.0);
        p.tick(30.0);
        assert_eq!(p.eye, chargen_zoomed_out_eye(ALUVIAN));
    }

    #[test]
    fn the_summary_preview_stays_at_the_far_eye() {
        let p = ChargenPreviewState::new(true);
        assert_eq!(p.eye, chargen_zoomed_out_eye(0));
        let p = built(true, OLTHOI);
        assert_eq!(p.eye, chargen_zoomed_out_eye(OLTHOI));
    }

    #[test]
    fn a_rebuild_keeps_a_zoomed_in_figure_at_rest() {
        let mut p = built(false, ALUVIAN);
        p.zoom_in();
        p.rebuild(
            ALUVIAN,
            2,
            ChargenAppearanceSelection::default(),
            figure(),
            0,
        );
        assert!(p.is_zoomed_in());
        assert_eq!(p.figure().unwrap().parts()[0].transform.w_axis.x, 99.0);
    }
}
