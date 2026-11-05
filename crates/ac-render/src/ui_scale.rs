//! The UI's scale: how many framebuffer pixels one UI unit covers.
//!
//! Two things set it. On macOS the UI is laid out in points, as OpenAC
//! does there: a Retina display's framebuffer has two pixels a point, and
//! the retail UI drawn one unit a pixel would come out at half its size.
//! Elsewhere one unit is one pixel. On top of that the player's UI Scale
//! option (50% to 300%) multiplies it, in the game only (the pre-game
//! screens fill the window whatever the scale).
//!
//! The UI is laid out and hit-tested in units; the world, picking and
//! screenshots stay in pixels. Every crossing between the two goes through
//! this type: a pointer in pixels into units ([`UiScale::ui_point`]), a UI
//! position into pixels ([`UiScale::window_point`]), the world's projection
//! into units ([`UiScale::overlay_viewport`]), and what the UI draws into
//! pixels ([`UiScale::pixels_per_unit`], snapped to whole pixels by
//! `crate::ui::batch`).

use crate::platform::HostOs;

/// Framebuffer pixels per UI unit (more than 0).
#[derive(Debug, Clone, Copy, PartialEq)]
pub(crate) struct UiScale(f64);

impl UiScale {
    /// One unit a pixel.
    pub(crate) const ONE: Self = Self(1.0);

    /// The scale for a window whose monitor reports `scale_factor` (winit's
    /// pixels per point) on `os`.
    /// A factor below one, or not a number, counts as one.
    pub(crate) fn for_window(os: HostOs, scale_factor: f64) -> Self {
        if os == HostOs::MacOs && scale_factor.is_finite() && scale_factor > 1.0 {
            Self(scale_factor)
        } else {
            Self::ONE
        }
    }

    /// This scale with the player's UI Scale option of `percent` on top
    /// (read through `ac_host::settings::ui_scale_percent`, so within 50 to
    /// 300).
    pub(crate) fn with_user_percent(self, percent: i32) -> Self {
        let percent = ac_host::settings::ui_scale_percent(percent);
        Self(self.0 * f64::from(percent) / 100.0)
    }

    /// The UI's size, in units, of a framebuffer of `pixels`: whole units,
    /// at least one along a side that has pixels.
    pub(crate) fn ui_size(self, pixels: (u32, u32)) -> (u32, u32) {
        let side = |p: u32| {
            if p == 0 {
                0
            } else {
                ((f64::from(p) / self.0).round() as u32).max(1)
            }
        };
        (side(pixels.0), side(pixels.1))
    }

    /// Framebuffer pixels per UI unit, for what the UI shows that is drawn
    /// in pixels (its 3-D viewports) and for drawing the UI itself.
    pub(crate) fn pixels_per_unit(self) -> f32 {
        self.0 as f32
    }

    /// A window position in pixels, in UI units.
    pub(crate) fn ui_point(self, x: f64, y: f64) -> (f64, f64) {
        (x / self.0, y / self.0)
    }

    /// A position the UI reports in its units, in window pixels: the
    /// world picks in pixels, so what the UI hands it (where an item was
    /// let go, where a drag over the world began) is scaled back first.
    pub(crate) fn window_point(self, x: i32, y: i32) -> (f32, f32) {
        (
            (f64::from(x) * self.0) as f32,
            (f64::from(y) * self.0) as f32,
        )
    }

    /// The screen the overlays project the world onto, in UI units, for a
    /// framebuffer of `pixels`: the world fills the framebuffer, so a point
    /// the camera puts at pixel `p` sits at unit `p / scale`, where the UI
    /// draws it back at pixel `p`.
    pub(crate) fn overlay_viewport(self, pixels: (u32, u32)) -> (f32, f32) {
        (
            (f64::from(pixels.0) / self.0) as f32,
            (f64::from(pixels.1) / self.0) as f32,
        )
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_retina_mac_lays_the_ui_out_in_points() {
        let scale = UiScale::for_window(HostOs::MacOs, 2.0);
        assert_eq!(scale.ui_size((3024, 1842)), (1512, 921));
        assert_eq!(scale.ui_point(800.0, 401.0), (400.0, 200.5));
    }

    /// An item let go over the world at (400, 300) points on a Retina
    /// screen lands on what is drawn at (800, 600) pixels, not on what is
    /// at half the pointer's position.
    #[test]
    fn a_ui_position_picks_the_world_at_its_pixel() {
        let scale = UiScale::for_window(HostOs::MacOs, 2.0);
        assert_eq!(scale.window_point(400, 300), (800.0, 600.0));
        // The pointer the UI saw, back where the window had it.
        let (x, y) = scale.ui_point(800.0, 600.0);
        assert_eq!(scale.window_point(x as i32, y as i32), (800.0, 600.0));
    }

    #[test]
    fn one_unit_a_pixel_picks_where_the_ui_says() {
        for (x, y) in [(0, 0), (301, 7), (-5, 1_000_000), (i32::MAX, i32::MIN)] {
            assert_eq!(
                UiScale::ONE.window_point(x, y),
                (x as f32, y as f32),
                "{x},{y}"
            );
        }
    }

    #[test]
    fn a_one_x_mac_display_keeps_one_unit_a_pixel() {
        let scale = UiScale::for_window(HostOs::MacOs, 1.0);
        assert_eq!(scale, UiScale::ONE);
        assert_eq!(scale.ui_size((2560, 1440)), (2560, 1440));
    }

    #[test]
    fn windows_and_linux_keep_one_unit_a_pixel_whatever_the_monitor_says() {
        for os in [HostOs::Windows, HostOs::Linux] {
            let scale = UiScale::for_window(os, 1.5);
            assert_eq!(scale, UiScale::ONE, "{os:?}");
            assert_eq!(scale.ui_point(301.0, 7.0), (301.0, 7.0), "{os:?}");
        }
    }

    #[test]
    fn a_factor_that_is_not_a_scale_counts_as_one() {
        for factor in [0.0, -2.0, 0.5, f64::NAN, f64::INFINITY] {
            assert_eq!(
                UiScale::for_window(HostOs::MacOs, factor),
                UiScale::ONE,
                "{factor}"
            );
        }
    }

    #[test]
    fn an_odd_pixel_size_rounds_to_whole_units_and_never_to_nothing() {
        let scale = UiScale::for_window(HostOs::MacOs, 2.0);
        assert_eq!(scale.ui_size((1001, 1)), (501, 1));
        assert_eq!(scale.ui_size((0, 0)), (0, 0));
    }

    #[test]
    fn one_hundred_percent_changes_nothing() {
        assert_eq!(UiScale::ONE.with_user_percent(100), UiScale::ONE);
        let retina = UiScale::for_window(HostOs::MacOs, 2.0);
        assert_eq!(retina.with_user_percent(100), retina);
    }

    /// The player's scale multiplies the platform's: 150% on a Retina Mac
    /// is three pixels a unit; 50% elsewhere is two units a pixel.
    #[test]
    fn the_players_scale_multiplies_the_platforms() {
        let retina = UiScale::for_window(HostOs::MacOs, 2.0).with_user_percent(150);
        assert_eq!(retina.pixels_per_unit(), 3.0);
        assert_eq!(retina.ui_size((3000, 1800)), (1000, 600));
        let half = UiScale::ONE.with_user_percent(50);
        assert_eq!(half.pixels_per_unit(), 0.5);
        assert_eq!(half.ui_size((1920, 1080)), (3840, 2160));
        assert_eq!(half.ui_point(100.0, 50.0), (200.0, 100.0));
        assert_eq!(half.window_point(200, 100), (100.0, 50.0));
    }

    /// The UI at 150% on 1920 x 1080 pixels is 1280 x 720 units; a click
    /// at pixel (960, 540) is unit (640, 360), and that unit picks the
    /// world at the same pixel again.
    #[test]
    fn at_one_hundred_fifty_percent_a_click_and_its_pick_meet_at_one_pixel() {
        let scale = UiScale::ONE.with_user_percent(150);
        assert_eq!(scale.ui_size((1920, 1080)), (1280, 720));
        assert_eq!(scale.ui_point(960.0, 540.0), (640.0, 360.0));
        assert_eq!(scale.window_point(640, 360), (960.0, 540.0));
        assert_eq!(scale.overlay_viewport((1920, 1080)), (1280.0, 720.0));
    }

    #[test]
    fn a_scale_outside_the_offered_range_is_brought_into_it() {
        assert_eq!(UiScale::ONE.with_user_percent(10).pixels_per_unit(), 0.5);
        assert_eq!(UiScale::ONE.with_user_percent(900).pixels_per_unit(), 3.0);
        assert_eq!(UiScale::ONE.with_user_percent(154).pixels_per_unit(), 1.5);
    }

    /// The vivid target bracket at 150%: the world is drawn at full
    /// resolution, so the selected creature's sphere is at a pixel square;
    /// the bracket is placed in units on the overlay viewport, and the UI
    /// draws units at 1.5 pixels each, so it frames the same pixel square.
    /// Projected onto the framebuffer's pixels instead, the bracket would
    /// be drawn half as far again from the top-left corner (the reported
    /// "worse towards the bottom right" drift).
    #[test]
    fn the_target_bracket_frames_the_creature_at_one_hundred_fifty_percent() {
        use ac_ui::overlay::{OverlayCamera, TargetProjection, compute_projection, target_layout};
        use glam::{Mat4, Vec2, Vec3, Vec4};
        let framebuffer = (1920, 1080);
        // At the origin looking down -z, y up; a 1-radian vertical field of
        // view at 16:9 (depth 0.1 to 1000 m).
        let view = Mat4::IDENTITY;
        let f = 1.0 / 0.5f32.tan();
        let (near, far) = (0.1f32, 1000.0f32);
        let projection = Mat4::from_cols(
            Vec4::new(f / (1920.0 / 1080.0), 0.0, 0.0, 0.0),
            Vec4::new(0.0, f, 0.0, 0.0),
            Vec4::new(0.0, 0.0, far / (near - far), -1.0),
            Vec4::new(0.0, 0.0, near * far / (near - far), 0.0),
        );
        // Down and to the right of the screen's centre, 10 m away.
        let (center, radius) = (Vec3::new(3.0, -1.5, -10.0), 0.5);
        let square = |viewport: (f32, f32)| {
            let camera = OverlayCamera {
                view,
                projection,
                viewport,
            };
            match compute_projection(center, radius, &camera) {
                TargetProjection::OnScreen { min, max } => (min, max),
                other => panic!("{other:?}"),
            }
        };
        let (pixel_min, pixel_max) = square((1920.0, 1080.0));
        let scale = UiScale::ONE.with_user_percent(150);
        let viewport = scale.overlay_viewport(framebuffer);
        let (min, max) = square(viewport);
        let ppu = scale.pixels_per_unit();
        let near = |a: Vec2, b: Vec2| (a - b).length() < 1e-2;
        assert!(near(min * ppu, pixel_min), "{min:?} {pixel_min:?}");
        assert!(near(max * ppu, pixel_max), "{max:?} {pixel_max:?}");
        // The bracket's corners sit just outside that square.
        let corner = Vec2::new(12.0, 12.0);
        let layout = target_layout(min, max, Vec2::new(viewport.0, viewport.1), [corner; 4]);
        assert!(near((layout.position + corner) * ppu, pixel_min));
        assert!(near(
            (layout.position + layout.size - corner) * ppu,
            pixel_max
        ));
        // Placed on the pixel viewport and then drawn at 150%, it misses.
        assert!(!near(pixel_min * ppu, pixel_min));
    }

    /// The plugins' world icons and labels at 150%, on a Retina Mac (two
    /// pixels a point) and both together: placed in units on the overlay
    /// viewport, like the target bracket, and drawn at the UI's scale, an
    /// icon at a position is centred on the pixel where the world draws
    /// that position, and an object's label and first row of icons are
    /// centred over the pixel of its head, one label line and a gap apart.
    /// Their sizes scale with the UI, as the labels' text does: a 24-point
    /// icon is 36 pixels at 150%. (The world pass, which draws the ground
    /// shapes and triangles and tests the icons' anchors, is in pixels and
    /// never sees the UI's scale.)
    #[test]
    fn plugin_icons_and_labels_land_on_their_anchors_at_any_ui_scale() {
        use crate::ui::batch::{UiBatch, UiTexture};
        use ac_ui::UiRoot;
        use ac_ui::draw::{Color, NoSprites, TextureRef};
        use ac_ui::overlay::{
            ICON_GAP, OverlayCamera, OverlayHost, WorldIcon, WorldIconAnchor, WorldIconOverlay,
            WorldLabel, WorldLabelAnchor, place_labels, project_sphere,
        };
        use glam::{Vec2, Vec3};

        const LINE_HEIGHT: f32 = 16.0;
        const SIZE: f32 = 24.0;
        const ART: TextureRef = TextureRef::Runtime(1);
        let framebuffer = (1920, 1080);
        // At the origin looking north (+y) with +z up.
        let camera = |viewport: (f32, f32)| OverlayCamera {
            view: glam::camera::rh::view::look_at_mat4(Vec3::ZERO, Vec3::Y, Vec3::Z),
            projection: glam::camera::rh::proj::directx::perspective(
                1.0,
                1920.0 / 1080.0,
                0.1,
                1000.0,
            ),
            viewport,
        };
        let pixels = camera((1920.0, 1080.0));
        let pixel_of = |p: Vec3| {
            let r = project_sphere(p, 0.0, &pixels, 0.0).expect("in view");
            (r.min + r.max) * 0.5
        };
        // A spot 12 m ahead, right and a little below the eye; a creature
        // 15 m ahead and to the left, 1.8 m tall, with one label.
        let spot = Vec3::new(3.0, 12.0, -1.0);
        let creature = WorldLabelAnchor {
            base: Vec3::new(-2.0, 15.0, -1.5),
            height: 1.8,
        };
        let anchor = |id: u32| (id == 7).then_some(creature);
        let icon = |anchor| WorldIcon {
            anchor,
            image: Some((ART, 32.0, 32.0)),
            size: SIZE,
            tint: Color::WHITE,
            border: None,
            max_range: 60.0,
        };
        let icons = [
            icon(WorldIconAnchor::Point(spot)),
            icon(WorldIconAnchor::Object(7)),
        ];
        let labels = [WorldLabel {
            object_id: 7,
            text: "name".to_owned(),
            color: Color::WHITE,
            height_offset: 0.0,
            line: 0,
            max_range: 60.0,
            outline: false,
        }];
        let spot_pixel = pixel_of(spot);
        let head_pixel = pixel_of(creature.base + Vec3::Z * creature.height);

        let retina = UiScale::for_window(HostOs::MacOs, 2.0);
        for scale in [
            UiScale::ONE,
            UiScale::ONE.with_user_percent(150),
            retina,
            retina.with_user_percent(150),
        ] {
            let ppu = scale.pixels_per_unit();
            // Whole units, then whole pixels: within a unit and a pixel.
            let slack = ppu + 1.0;
            let near = |a: Vec2, b: Vec2| (a - b).abs().max_element() <= slack;
            let units = camera(scale.overlay_viewport(framebuffer));

            // The labels, placed as the label overlay does.
            let mut placed = Vec::new();
            place_labels(
                &labels,
                &anchor,
                &units,
                LINE_HEIGHT,
                &|_| 40.0,
                &mut placed,
            );
            let label = placed[0];
            let label_bottom = Vec2::new(label.x + 20.0, label.y + LINE_HEIGHT) * ppu;
            assert!(near(label_bottom, head_pixel), "{ppu}: {label_bottom}");

            // The icons, placed and drawn through the overlay and the UI's
            // batch, in framebuffer pixels.
            let (w, h) = scale.ui_size(framebuffer);
            let mut ui = UiRoot::new();
            ui.set_size(w as f32, h as f32);
            let mut host = OverlayHost::mount(&mut ui);
            let mut overlay = WorldIconOverlay::mount(&mut ui, &mut host, LINE_HEIGHT);
            overlay.tick(&mut ui, &icons, &anchor, &labels, Some(&units));
            let batch = UiBatch::build_scaled(&ui.draw((w as f32, h as f32), &NoSprites), ppu);
            let squares: Vec<(Vec2, Vec2)> = batch
                .runs
                .iter()
                .filter(|r| r.key.texture == UiTexture::Named(ART))
                .flat_map(|r| {
                    let run = &batch.vertices[r.first as usize..(r.first + r.count) as usize];
                    run.chunks(6)
                        .map(|quad| {
                            let corners = quad.iter().map(|v| Vec2::from_array(v.position));
                            let min = corners.clone().fold(Vec2::MAX, Vec2::min);
                            (min, corners.fold(Vec2::MIN, Vec2::max))
                        })
                        .collect::<Vec<_>>()
                })
                .collect();
            assert_eq!(squares.len(), 2, "{ppu}");
            for (min, max) in &squares {
                let side = *max - *min;
                assert!(near(side, Vec2::splat(SIZE * ppu)), "{ppu}: {side}");
            }
            let at_spot = squares
                .iter()
                .any(|(min, max)| near((*min + *max) * 0.5, spot_pixel));
            assert!(at_spot, "{ppu}: {squares:?} {spot_pixel}");
            let row_bottom = head_pixel - Vec2::new(0.0, (LINE_HEIGHT + ICON_GAP) * ppu);
            let over_head = squares
                .iter()
                .any(|(min, max)| near(Vec2::new((min.x + max.x) * 0.5, max.y), row_bottom));
            assert!(over_head, "{ppu}: {squares:?} {row_bottom}");

            // Placed on the framebuffer's pixels instead, the icons would be
            // drawn away from their anchors at any scale but one.
            if ppu != 1.0 {
                assert!(!near(spot_pixel * ppu, spot_pixel));
            }
        }
    }

    /// A drop-down at 50% on a 1280 x 720 window: the UI is 2560 x 1440
    /// units; a popup too wide for the room right of its button shifts
    /// left to end at the screen's right edge in units, is drawn inside the
    /// window's pixels, and a click at a row's pixel picks that row.
    #[test]
    fn a_drop_down_at_fifty_percent_opens_on_screen_and_picks_where_clicked() {
        use crate::ui::batch::UiBatch;
        use ac_ui::draw::{DrawList, NoSprites};
        use ac_ui::widgets::{Menu, MenuItem};
        use ac_ui::{Element, ElementMessage, MouseButton, UiOutput, UiRoot};

        let framebuffer = (1280, 720);
        let scale = UiScale::ONE.with_user_percent(50);
        let (w, h) = scale.ui_size(framebuffer);
        assert_eq!((w, h), (2560, 1440));
        let mut ui = UiRoot::new();
        ui.set_size(w as f32, h as f32);
        let mut m = Menu::default();
        m.column_width = 100.0;
        m.fits_long_labels = true;
        m.retail_button_art = false;
        m.open_upward = false;
        m.scrollable = true;
        m.popup_scrollbar_hide_when_disabled = true;
        m.rows_per_column = 4;
        m.row_height = 18.0;
        m.text_indent = 6.0;
        // 40 characters at 7 units each: wider than the 160 units left of
        // the screen's edge.
        let long = "x".repeat(40);
        m.items = ["A", "B", "C", long.as_str()]
            .iter()
            .enumerate()
            .map(|(i, l)| MenuItem::new(*l, i as u64))
            .collect();
        let mut e = Element::at(2400.0, 1300.0, 100.0, 20.0);
        Menu::configure(&mut e);
        let root = ui.root();
        let menu = ui.add(root, e, m);
        let click = |ui: &mut UiRoot, px: f64, py: f64| {
            let (x, y) = scale.ui_point(px, py);
            ui.on_mouse_down(MouseButton::Left, x as i32, y as i32, 0);
            ui.on_mouse_up(MouseButton::Left, x as i32, y as i32, 0);
        };
        // The button is pixels (1200, 650) to (1250, 660).
        click(&mut ui, 1210.0, 655.0);
        let opened = ui.widget::<Menu>(menu).unwrap();
        assert!(opened.is_open());
        let right = 2400.0 + opened.popup_left() + opened.popup_outer_width();
        assert_eq!(right, 2560.0);
        let list = ui.draw((w as f32, h as f32), &NoSprites);
        let popup = UiBatch::build_scaled(
            &DrawList {
                main: Vec::new(),
                overlay: list.overlay,
                canvas_scale: list.canvas_scale,
            },
            scale.pixels_per_unit(),
        );
        assert!(!popup.is_empty());
        for v in &popup.vertices {
            let [x, y] = v.position;
            assert!(
                (0.0..=1280.0).contains(&x) && (0.0..=720.0).contains(&y),
                "{x},{y}"
            );
        }
        ui.drain_output();
        // The third row is units 1361 to 1379 high (under the 20-unit
        // button and the 5-unit border, 18 units a row), left of the button:
        // pixels (1160, 685).
        click(&mut ui, 1160.0, 685.0);
        let picked: Vec<u64> = ui
            .drain_output()
            .into_iter()
            .filter_map(|o| match o {
                UiOutput::Element {
                    message: ElementMessage::MenuSelected(p),
                    ..
                } => Some(p),
                _ => None,
            })
            .collect();
        assert_eq!(picked, vec![2]);
    }

    /// A plugin's Ctrl+drag of its canvas at 150% on 1920 x 1080 pixels.
    /// The UI is 1280 x 720 units and a canvas pixel is one unit, drawn at
    /// 1.5 window pixels: the plugin hears the pointer in canvas pixels
    /// (window pixels / 1.5, from the canvas's corner), with Ctrl held, and
    /// the window's place is in units, the same rectangle the session
    /// measures from. The plugin moves its canvas (anchored bottom-right,
    /// so the layer's size matters) by where the pointer is from the
    /// grabbed point; the session and the window each see the other's news
    /// a frame late. The canvas never runs ahead of the pointer and ends
    /// with the grabbed point under it.
    #[test]
    fn a_canvas_ctrl_dragged_at_one_hundred_fifty_percent_follows_the_pointer() {
        use ac_plugin_ui::{
            CanvasAnchor, CanvasDescriptor, PluginUi, PluginUiView, PointerKind, UiChange, UiEvent,
            UiInput, UiOwner, canvas_origin,
        };
        use ac_ui::event::Modifiers;
        use ac_ui::overlay::OverlayHost;
        use ac_ui::plugin::PluginOutbox;
        use ac_ui::plugin::canvas::CanvasLayer;
        use ac_ui::{MouseButton, UiRoot};
        use std::cell::RefCell;
        use std::collections::VecDeque;
        use std::rc::Rc;

        let framebuffer = (1920, 1080);
        let scale = UiScale::ONE.with_user_percent(150);
        let (w, h) = scale.ui_size(framebuffer);
        assert_eq!((w, h), (1280, 720));
        let layer_units = (w as f32, h as f32);
        let mut state = PluginUi::new();
        let owner = UiOwner::new("test.canvas", "Canvas");
        let size = (100.0, 50.0);
        let id = state
            .register_canvas(
                &owner,
                CanvasDescriptor {
                    canvas_id: "hud".into(),
                    width: 100,
                    height: 50,
                    anchor: CanvasAnchor::BottomRight,
                    offset: (-200.0, -100.0),
                    start_visible: true,
                    accepts_pointer_input: true,
                },
            )
            .unwrap();
        assert!(state.listen_canvas("test.canvas", id, true));
        let mut view = PluginUiView::new();
        view.apply_all(state.snapshot());
        state.take_changes();
        let mut ui = UiRoot::new();
        ui.set_size(layer_units.0, layer_units.1);
        let mut band = OverlayHost::mount(&mut ui);
        let mut layer = CanvasLayer::mount(&mut ui, &mut band);
        let outbox: PluginOutbox = Rc::new(RefCell::new(Vec::new()));
        let mut no_image = |_: &str, _: u32| None;
        layer.sync(
            &mut ui,
            &band,
            &view,
            &mut no_image,
            &mut |_| unreachable!("no fonts"),
            None,
            &outbox,
        );
        let at = |offset| canvas_origin(CanvasAnchor::BottomRight, offset, size, layer_units);
        // At units (980, 570), window pixels (1470, 855).
        assert_eq!(at((-200.0, -100.0)), (980.0, 570.0));

        let pointer_at = |px: f64, py: f64| {
            let (x, y) = scale.ui_point(px, py);
            (x as i32, y as i32)
        };
        let mut to_session: VecDeque<Vec<UiInput>> = VecDeque::new();
        let mut to_window: VecDeque<Vec<UiChange>> = VecDeque::new();
        let mut offset = (-200.0_f32, -100.0_f32);
        let mut grab = None;
        let mut heard = 0;
        let mut worst = f32::MIN;
        ui.set_modifiers(Modifiers::CTRL);
        // Pixels (1485, 870): units (990, 580), canvas pixels (10, 10).
        let (x, y) = pointer_at(1485.0, 870.0);
        ui.on_mouse_down(MouseButton::Left, x, y, 0);
        let mut pointer = (x, y);
        for frame in 0..120 {
            // Moving left and up for a second (5 and 3 pixels a frame, not
            // whole units), then held still.
            let step = f64::from(frame.min(60));
            pointer = pointer_at(1485.0 - step * 5.0, 870.0 - step * 3.0);
            ui.on_mouse_move(pointer.0, pointer.1);
            to_session.push_back(outbox.borrow_mut().drain(..).collect());
            if to_session.len() > 1 {
                for input in to_session.pop_front().unwrap() {
                    if let UiInput::Pointer { place, .. } = &input {
                        let place = place.expect("the window measured it");
                        assert_eq!(place.layer, layer_units, "the layer is in units");
                    }
                    state.input(input);
                }
            }
            for (_, event) in state.take_events() {
                let UiEvent::Pointer { event, .. } = state.pointer_as_placed_now(event) else {
                    continue;
                };
                heard += 1;
                assert_eq!(event.modifiers, 2, "Ctrl is held");
                match (event.kind, grab) {
                    (PointerKind::Down, _) => {
                        assert_eq!((event.x, event.y), (10.0, 10.0), "canvas pixels");
                        grab = Some((event.x, event.y));
                    }
                    (PointerKind::Move, Some((gx, gy))) => {
                        offset = (offset.0 + event.x - gx, offset.1 + event.y - gy);
                        assert!(state.place_canvas("test.canvas", id, None, None, Some(offset)));
                    }
                    _ => {}
                }
            }
            to_window.push_back(state.take_changes());
            if to_window.len() > 1 {
                view.apply_all(to_window.pop_front().unwrap());
                layer.sync(
                    &mut ui,
                    &band,
                    &view,
                    &mut no_image,
                    &mut |_| unreachable!("no fonts"),
                    None,
                    &outbox,
                );
            }
            // Moving up and left, ahead means further up or left than the
            // grabbed point under the pointer.
            let origin = at(offset);
            let target = (pointer.0 as f32 - 10.0, pointer.1 as f32 - 10.0);
            worst = worst.max(target.0 - origin.0).max(target.1 - origin.1);
        }
        assert!(heard > 60, "{heard}");
        assert_eq!(worst, 0.0, "the canvas ran ahead of the pointer");
        assert_eq!(
            at(offset),
            (pointer.0 as f32 - 10.0, pointer.1 as f32 - 10.0)
        );
    }

    /// The overlays' screen is the framebuffer in units, not the UI's
    /// rounded size: at 110% a 1001-pixel-wide window is 910 units, and
    /// the world's right edge is at unit 910 exactly.
    #[test]
    fn the_overlay_viewport_is_the_framebuffer_in_units() {
        let scale = UiScale::ONE.with_user_percent(110);
        let (w, h) = scale.overlay_viewport((1001, 600));
        assert!((w - 910.0).abs() < 1e-3, "{w}");
        assert!((h - 545.454_5).abs() < 1e-3, "{h}");
        assert_eq!(
            UiScale::ONE.overlay_viewport((1920, 1080)),
            (1920.0, 1080.0)
        );
    }
}
