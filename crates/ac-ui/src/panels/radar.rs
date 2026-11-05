//! The radar window: the compass letters turned with the player, the
//! coordinates line, the UI-lock button, and the radar frame built from the
//! objects around the player.
//!
//! The client builds a [`RadarScene`] (the player and the objects near
//! him, plain data from the session's world state) and hands it to the
//! controller; the controller projects it ([`build_snapshot`]) and shows a
//! new frame every 25 ms, as the radar does.

use glam::{Quat, Vec2, Vec3};

use crate::draw::Color;
use crate::geometry::AnchorEdges;
use crate::host::PanelController;
use crate::import::ImportedLayout;
use crate::panels::PanelOutput;
use crate::radar::{
    self, CompassPoint, ObjectTraits, RadarBehavior, RadarBlip, RadarCoordinates, RadarSnapshot,
    RelationshipTraits,
};
use crate::root::{ElementMessage, UiOutput, UiRoot};
use crate::states;
use crate::text::Line;
use crate::tree::{Element, ElementId};
use crate::widgets::{Button, Radar, Text};

/// The radar's layout.
pub const LAYOUT_ID: u32 = 0x2100_0074;
/// Radius of the disc, pixels.
pub const RADAR_PIXEL_RADIUS: i32 = 50;
/// Window root (the radar widget).
pub const ROOT_ID: u32 = 0x1000_06D3;
/// The coordinates box.
pub const COORDINATE_CONTAINER_ID: u32 = 0x1000_003E;
/// The disc.
pub const RADAR_DISC_ID: u32 = 0x1000_003F;
/// N.
pub const NORTH_TOKEN_ID: u32 = 0x1000_0040;
/// E.
pub const EAST_TOKEN_ID: u32 = 0x1000_0041;
/// S.
pub const SOUTH_TOKEN_ID: u32 = 0x1000_0042;
/// W.
pub const WEST_TOKEN_ID: u32 = 0x1000_0043;
/// The UI-lock button.
pub const LOCK_BUTTON_ID: u32 = 0x1000_0619;
/// The drag handle.
pub const DRAG_BUTTON_ID: u32 = 0x1000_06A3;

/// The radar centre the projection uses.
pub const PRODUCTION_CENTER: Vec2 = Vec2::new(60.0, 60.0);

/// The coordinates' colour.
pub const COORDINATE_COLOR: Color = Color::WHITE;

/// The player, as the radar reads him.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct RadarPlayer {
    /// Object id.
    pub guid: u32,
    /// Position, world meters (one frame for every object).
    pub position: Vec3,
    /// Orientation.
    pub rotation: Quat,
    /// Yaw, radians counter-clockwise from east (the heading source).
    pub yaw_radians: f32,
    /// The cell he stands in.
    pub cell_id: u32,
    /// His item type.
    pub item_type: u32,
    /// His public description bit field.
    pub pwd_bitfield: u32,
    /// His allegiance's monarch (0: none).
    pub monarch_id: u32,
}

/// One object near the player.
#[derive(Debug, Clone, PartialEq)]
pub struct RadarObject {
    /// Object id.
    pub guid: u32,
    /// Name (`None`: shown by id).
    pub name: Option<String>,
    /// Position, world meters.
    pub position: Vec3,
    /// Its radar behaviour (`None`: never shown).
    pub behavior: Option<RadarBehavior>,
    /// Its item type.
    pub item_type: u32,
    /// Its public description bit field.
    pub pwd_bitfield: u32,
    /// Its own blip colour (0: none).
    pub color_override: u8,
    /// Its allegiance's monarch (0: none).
    pub monarch_id: u32,
    /// Its place in the player's fellowship. Allegiance membership and the
    /// player-killer relation are filled in from the monarchs and the
    /// player's description.
    pub relationship: RelationshipTraits,
}

/// Everything a radar frame is built from.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct RadarScene {
    /// The player (`None`: not in the world; an empty frame).
    pub player: Option<RadarPlayer>,
    /// Objects near the player (the player himself is skipped).
    pub objects: Vec<RadarObject>,
    /// The selected object.
    pub selected: Option<u32>,
    /// The "show coordinates on the radar" option.
    pub coordinates_on_radar: bool,
    /// The UI is locked.
    pub ui_locked: bool,
}

/// Project a scene into a radar frame.
#[must_use]
pub fn build_snapshot(scene: &RadarScene) -> RadarSnapshot {
    let Some(player) = scene.player else {
        return RadarSnapshot {
            ui_locked: scene.ui_locked,
            ..RadarSnapshot::default()
        };
    };
    let heading = radar::heading_from_yaw(player.yaw_radians);
    let coordinates = RadarCoordinates::from_cell(player.cell_id);
    let is_outside = coordinates.is_some();
    let coordinates_text = coordinates
        .filter(|_| scene.coordinates_on_radar)
        .map(|c| c.combined_text());
    let player_traits =
        ObjectTraits::from_public_weenie_description(player.item_type, player.pwd_bitfield, 0);
    let range = radar::range_meters(is_outside);
    let mut blips = Vec::new();
    for o in &scene.objects {
        if o.guid == player.guid {
            continue;
        }
        let Some(behavior) = o.behavior else {
            continue;
        };
        if !radar::is_showable(behavior, true) {
            continue;
        }
        let traits = ObjectTraits::from_public_weenie_description(
            o.item_type,
            o.pwd_bitfield,
            o.color_override,
        );
        let relationship = RelationshipTraits {
            is_allegiance_member: radar::is_allegiance_member(o.monarch_id, player.monarch_id),
            player_is_player_killer: player_traits.is_player_killer,
            player_is_pk_lite: player_traits.is_pk_lite,
            ..o.relationship
        };
        let shape = radar::blip_shape(&traits, &relationship);
        if shape == radar::BlipShape::Undef {
            continue;
        }
        let local = radar::global_to_local(player.rotation, o.position - player.position);
        let Some(p) = radar::project(local, PRODUCTION_CENTER, RADAR_PIXEL_RADIUS, range) else {
            continue;
        };
        let color = radar::dim_rgb(radar::blip_color(&traits, &relationship), p.rgb_multiplier);
        let name = o
            .name
            .clone()
            .filter(|n| !n.is_empty())
            .unwrap_or_else(|| format!("0x{:08X}", o.guid));
        blips.push(RadarBlip {
            object_id: o.guid,
            name,
            pixel_x: p.pixel.x,
            pixel_y: p.pixel.y,
            color,
            shape,
            selected: scene.selected == Some(o.guid),
        });
    }
    RadarSnapshot {
        player_heading_degrees: heading,
        blips,
        coordinates_text,
        blank_blips: false,
        ui_locked: scene.ui_locked,
    }
}

#[derive(Debug, Clone, Copy, PartialEq)]
struct Token {
    element: Option<ElementId>,
    magnitude: f32,
    point: CompassPoint,
}

/// The radar window's controller.
#[derive(Debug, Clone)]
pub struct RadarController {
    radar: ElementId,
    coordinate_container: Option<ElementId>,
    coordinate_text: Option<ElementId>,
    lock_button: Option<ElementId>,
    drag_button: Option<ElementId>,
    tokens: [Token; 4],
    last_heading: Option<f32>,
    last_coordinates: Option<Option<String>>,
    last_ui_locked: Option<bool>,
    pending: Option<RadarSnapshot>,
    refresh_accumulator: f64,
    /// Frames shown so far.
    pub frames_applied: u32,
}

impl RadarController {
    /// Bind to an imported radar layout; `None` when its root is not the
    /// radar widget.
    pub fn bind(
        ui: &mut UiRoot,
        layout: &ImportedLayout,
        font: Option<std::sync::Arc<crate::UiFont>>,
    ) -> Option<Self> {
        let radar = layout.root;
        ui.widget::<Radar>(radar)?;
        let center = match layout.find(RADAR_DISC_ID).and_then(|d| ui.element(d)) {
            Some(d) => (d.left + d.width * 0.5, d.top + d.height * 0.5),
            None => {
                let e = ui.element(radar)?;
                (e.width * 0.5, e.height.min(120.0) * 0.5)
            }
        };
        if let Some(r) = ui.widget_mut::<Radar>(radar) {
            r.center = center;
        }
        let coordinate_container = layout.find(COORDINATE_CONTAINER_ID);
        let coordinate_text = coordinate_container.map(|c| coordinate_text(ui, c, font));
        let lock_button = layout
            .find(LOCK_BUTTON_ID)
            .filter(|b| ui.widget::<Button>(*b).is_some());
        if let Some(b) = lock_button
            && let Some(w) = ui.widget_mut::<Button>(b)
        {
            // The locked/unlocked faces are semantic: pointer traffic must
            // not replace them.
            w.controller_owns_visual_state = true;
            w.listeners.click = true;
        }
        let drag_button = layout.find(DRAG_BUTTON_ID);
        if let Some(d) = drag_button
            && let Some(e) = ui.element_mut(d)
        {
            e.click_through = false;
        }
        let c = Vec2::new(center.0, center.1);
        let token = |ui: &UiRoot, id: u32, point| {
            let element = layout.find(id);
            let magnitude = element.and_then(|e| ui.element(e)).map_or(0.0, |e| {
                Vec2::new(e.left + e.width * 0.5, e.top + e.height * 0.5).distance(c)
            });
            Token {
                element,
                magnitude,
                point,
            }
        };
        let tokens = [
            token(ui, NORTH_TOKEN_ID, CompassPoint::North),
            token(ui, EAST_TOKEN_ID, CompassPoint::East),
            token(ui, SOUTH_TOKEN_ID, CompassPoint::South),
            token(ui, WEST_TOKEN_ID, CompassPoint::West),
        ];
        if let Some(e) = ui.element_mut(radar) {
            e.draggable = true;
            e.resize_x = false;
            e.resize_y = false;
        }
        let mut controller = Self {
            radar,
            coordinate_container,
            coordinate_text,
            lock_button,
            drag_button,
            tokens,
            last_heading: None,
            last_coordinates: None,
            last_ui_locked: None,
            pending: None,
            refresh_accumulator: 0.0,
            frames_applied: 0,
        };
        controller.apply_snapshot(ui, RadarSnapshot::default());
        Some(controller)
    }

    /// The radar widget.
    #[must_use]
    pub fn radar(&self) -> ElementId {
        self.radar
    }

    /// The coordinates text.
    #[must_use]
    pub fn coordinate_text(&self) -> Option<ElementId> {
        self.coordinate_text
    }

    /// A new scene; shown at the next 25 ms step.
    pub fn set_scene(&mut self, scene: &RadarScene) {
        self.pending = Some(build_snapshot(scene));
    }

    /// A new frame; shown at the next 25 ms step.
    pub fn set_snapshot(&mut self, snapshot: RadarSnapshot) {
        self.pending = Some(snapshot);
    }

    /// Show the pending frame now.
    pub fn refresh(&mut self, ui: &mut UiRoot) {
        if let Some(s) = self.pending.clone() {
            self.apply_snapshot(ui, s);
        }
    }

    /// Show `snapshot`: turn the compass, show or hide the coordinates,
    /// follow the UI lock, hand the blips to the radar.
    pub fn apply_snapshot(&mut self, ui: &mut UiRoot, snapshot: RadarSnapshot) {
        self.frames_applied += 1;
        let center = ui
            .widget::<Radar>(self.radar)
            .map_or((60.0, 60.0), |r| r.center);
        if self.last_heading != Some(snapshot.player_heading_degrees) {
            for t in self.tokens {
                let Some(e) = t.element else {
                    continue;
                };
                let Some((w, h)) = ui.element(e).map(|e| (e.width, e.height)) else {
                    continue;
                };
                let p = radar::compass_token_top_left(
                    snapshot.player_heading_degrees,
                    t.point,
                    Vec2::new(center.0, center.1),
                    t.magnitude,
                    Vec2::new(w, h),
                );
                if let Some(el) = ui.element_mut(e) {
                    el.left = p.x;
                    el.top = p.y;
                }
            }
            self.last_heading = Some(snapshot.player_heading_degrees);
        }
        if self.last_coordinates.as_ref() != Some(&snapshot.coordinates_text) {
            self.last_coordinates = Some(snapshot.coordinates_text.clone());
            let text = snapshot.coordinates_text.clone().filter(|t| !t.is_empty());
            let visible = text.is_some();
            if let Some(c) = self.coordinate_container {
                ui.set_visible(c, visible);
            }
            if let Some(t) = self.coordinate_text {
                ui.set_visible(t, visible);
                if let Some(w) = ui.widget_mut::<Text>(t) {
                    w.set_lines(
                        text.map(|s| vec![Line::new(s, COORDINATE_COLOR)])
                            .unwrap_or_default(),
                    );
                }
            }
        }
        if self.last_ui_locked != Some(snapshot.ui_locked) {
            self.last_ui_locked = Some(snapshot.ui_locked);
            if let Some(e) = ui.element_mut(self.radar) {
                e.draggable = !snapshot.ui_locked;
            }
            if let Some(d) = self.drag_button {
                ui.set_visible(d, !snapshot.ui_locked);
            }
            if let Some(b) = self.lock_button {
                ui.set_retail_state(
                    b,
                    if snapshot.ui_locked {
                        states::LOCKED_UI
                    } else {
                        states::UNLOCKED_UI
                    },
                );
            }
        }
        let radar = self.radar;
        ui.with::<Radar, _>(radar, |r, ui| r.apply_snapshot(ui, radar, snapshot));
    }

    /// The UI lock last shown.
    #[must_use]
    pub fn ui_locked(&self) -> bool {
        self.last_ui_locked.unwrap_or(false)
    }
}

/// The coordinates text: the imported text element itself, or a text
/// child filling the container.
fn coordinate_text(
    ui: &mut UiRoot,
    container: ElementId,
    font: Option<std::sync::Arc<crate::UiFont>>,
) -> ElementId {
    let text = if ui.widget::<Text>(container).is_some() {
        container
    } else {
        let (w, h) = ui
            .element(container)
            .map_or((0.0, 0.0), |e| (e.width, e.height));
        let mut e = Element::at(0.0, 0.0, w, h).with_anchors(AnchorEdges::ALL);
        e.name = Some("gmRadarUI.Coordinates".to_owned());
        e.z_order = i32::MAX;
        Text::configure(&mut e);
        ui.add(container, e, Text::default())
    };
    if let Some(e) = ui.element_mut(text) {
        Text::configure(e);
    }
    if let Some(t) = ui.widget_mut::<Text>(text) {
        t.centered = true;
        t.one_line = true;
        t.padding = 0.0;
        if font.is_some() {
            t.font = font;
        }
    }
    text
}

impl PanelController for RadarController {
    fn tick(&mut self, ui: &mut UiRoot, dt: f64) {
        self.refresh_accumulator += dt;
        let interval = f64::from(radar::UPDATE_INTERVAL_SECONDS);
        if self.refresh_accumulator >= interval {
            self.refresh_accumulator %= interval;
            self.refresh(ui);
        }
    }

    fn on_element_message(
        &mut self,
        ui: &mut UiRoot,
        element: ElementId,
        message: &ElementMessage,
    ) -> bool {
        match message {
            ElementMessage::ObjectSelected(id) if element == self.radar => {
                ui.emit(UiOutput::Panel(PanelOutput::SelectObject(*id)));
                true
            }
            ElementMessage::HoveredObjectChanged(id) if element == self.radar => {
                ui.emit(UiOutput::Panel(PanelOutput::HoveredObject(*id)));
                true
            }
            ElementMessage::Clicked { .. } if Some(element) == self.lock_button => {
                ui.emit(UiOutput::Panel(PanelOutput::SetUiLocked(!self.ui_locked())));
                true
            }
            _ => false,
        }
    }
}
