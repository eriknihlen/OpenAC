//! Radar rules, pure: which objects show, the blip shapes and colours, the
//! projection of a player-relative position to radar pixels, the compass
//! letters' placement and the coordinates line.

use glam::{Quat, Vec2, Vec3};

use crate::draw::Color;

/// Outdoor radar range, meters.
pub const OUTDOOR_RANGE_METERS: f32 = 75.0;
/// Indoor radar range, meters.
pub const INDOOR_RANGE_METERS: f32 = 25.0;
/// Height difference past which a blip dims, meters.
pub const ALTITUDE_DIM_THRESHOLD_METERS: f32 = 5.0;
/// How much a blip at another height dims (RGB multiplier).
pub const ALTITUDE_DIM_MULTIPLIER: f32 = 0.65;
/// How often the radar redraws its objects, seconds.
pub const UPDATE_INTERVAL_SECONDS: f32 = 0.025;

/// An object's radar behaviour (its weenie's setting).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub enum RadarBehavior {
    /// Unset.
    #[default]
    Undef,
    /// Never shown.
    ShowNever,
    /// Shown while moving.
    ShowMovement,
    /// Shown while attacking.
    ShowAttacking,
    /// Always shown.
    ShowAlways,
}

impl RadarBehavior {
    /// From the wire byte (unknown values are Undef).
    #[must_use]
    pub fn from_raw(raw: u8) -> Self {
        match raw {
            1 => Self::ShowNever,
            2 => Self::ShowMovement,
            3 => Self::ShowAttacking,
            4 => Self::ShowAlways,
            _ => Self::Undef,
        }
    }
}

/// A blip's shape.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub enum BlipShape {
    /// None (not drawn).
    #[default]
    Undef,
    /// One pixel.
    Circle,
    /// A hollow 3×3 box.
    Box,
    /// An X.
    X,
    /// A plus (the default).
    Plus,
    /// A triangle (fellowship leader).
    Triangle,
    /// An upside-down triangle (fellow).
    InvertedTriangle,
    /// A box with an X (threat in the allegiance).
    XBox,
}

/// A pixel offset from a blip's centre.
pub type PixelOffset = (i32, i32);

const EDGE: [PixelOffset; 4] = [(0, -1), (0, 1), (-1, 0), (1, 0)];
const CORNER: [PixelOffset; 4] = [(1, 1), (-1, -1), (-1, 1), (1, -1)];
const POINT: [PixelOffset; 1] = [(0, 0)];
const BOX: [PixelOffset; 8] = [
    EDGE[0], EDGE[1], EDGE[2], EDGE[3], CORNER[0], CORNER[1], CORNER[2], CORNER[3],
];
const X: [PixelOffset; 5] = [(0, 0), CORNER[0], CORNER[1], CORNER[2], CORNER[3]];
const PLUS: [PixelOffset; 5] = [(0, 0), EDGE[0], EDGE[1], EDGE[2], EDGE[3]];
const TRIANGLE: [PixelOffset; 4] = [(0, 0), (-1, 1), (0, 1), (1, 1)];
const INVERTED_TRIANGLE: [PixelOffset; 4] = [(0, 0), (-1, -1), (0, -1), (1, -1)];
const X_BOX: [PixelOffset; 12] = [
    EDGE[0],
    EDGE[1],
    EDGE[2],
    EDGE[3],
    CORNER[0],
    CORNER[1],
    CORNER[2],
    CORNER[3],
    (-2, -2),
    (2, -2),
    (-2, 2),
    (2, 2),
];
/// The fixed bright-green player marker at the radar's centre.
pub const PLAYER_MARKER_PIXELS: [PixelOffset; 9] = [
    (0, 0),
    EDGE[0],
    EDGE[1],
    EDGE[2],
    EDGE[3],
    (-2, 0),
    (2, 0),
    (0, -2),
    (0, 2),
];
/// The outline around the selected object's blip.
pub const SELECTION_PIXELS: [PixelOffset; 20] = selection_pixels();

const fn selection_pixels() -> [PixelOffset; 20] {
    let mut p = [(0, 0); 20];
    let mut i = 0;
    let mut x = -2;
    while x <= 2 {
        p[i] = (x, 3);
        p[i + 1] = (x, -3);
        i += 2;
        x += 1;
    }
    let mut y = -2;
    while y <= 2 {
        p[i] = (3, y);
        p[i + 1] = (-3, y);
        i += 2;
        y += 1;
    }
    p
}

/// The pixels a blip of `shape` fills, around its centre.
#[must_use]
pub fn blip_pixels(shape: BlipShape) -> &'static [PixelOffset] {
    match shape {
        BlipShape::Circle => &POINT,
        BlipShape::Box => &BOX,
        BlipShape::X => &X,
        BlipShape::Plus => &PLUS,
        BlipShape::Triangle => &TRIANGLE,
        BlipShape::InvertedTriangle => &INVERTED_TRIANGLE,
        BlipShape::XBox => &X_BOX,
        BlipShape::Undef => &[],
    }
}

/// True when an object with this behaviour (and a physics object) shows.
#[must_use]
pub fn is_showable(behavior: RadarBehavior, has_physics_object: bool) -> bool {
    has_physics_object
        && matches!(
            behavior,
            RadarBehavior::ShowMovement | RadarBehavior::ShowAttacking | RadarBehavior::ShowAlways
        )
}

/// What the radar reads about an object (from its public description).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct ObjectTraits {
    /// Shown in the UI (the description's UI-hidden bit, 0x80, is clear).
    pub is_valid: bool,
    /// The weenie's own blip colour (0: none).
    pub blip_color_override: u8,
    /// A portal.
    pub is_portal: bool,
    /// A vendor.
    pub is_vendor: bool,
    /// Attackable.
    pub is_attackable: bool,
    /// A creature.
    pub is_creature: bool,
    /// A player.
    pub is_player: bool,
    /// An admin.
    pub is_admin: bool,
    /// An admin hiding as one.
    pub is_hidden_admin: bool,
    /// A player killer.
    pub is_player_killer: bool,
    /// PK-lite.
    pub is_pk_lite: bool,
    /// Free PK.
    pub is_free_pk: bool,
}

impl Default for ObjectTraits {
    fn default() -> Self {
        Self {
            is_valid: true,
            blip_color_override: 0,
            is_portal: false,
            is_vendor: false,
            is_attackable: false,
            is_creature: false,
            is_player: false,
            is_admin: false,
            is_hidden_admin: false,
            is_player_killer: false,
            is_pk_lite: false,
            is_free_pk: false,
        }
    }
}

/// The creature bit of an item type.
pub const ITEM_TYPE_CREATURE: u32 = 0x10;

impl ObjectTraits {
    /// Read the item type and the public description bit field.
    #[must_use]
    pub fn from_public_weenie_description(
        item_type: u32,
        bitfield: u32,
        color_override: u8,
    ) -> Self {
        Self {
            is_valid: bitfield & 0x0000_0080 == 0,
            blip_color_override: color_override,
            is_portal: bitfield & 0x0004_0000 != 0,
            is_vendor: bitfield & 0x0000_0200 != 0,
            is_attackable: bitfield & 0x0000_0010 != 0,
            is_creature: item_type & ITEM_TYPE_CREATURE != 0,
            is_player: bitfield & 0x0000_0008 != 0,
            is_admin: bitfield & 0x0010_0000 != 0,
            is_hidden_admin: bitfield & 0x0000_0040 != 0,
            is_player_killer: bitfield & 0x0000_0020 != 0,
            is_pk_lite: bitfield & 0x0200_0000 != 0,
            is_free_pk: bitfield & 0x0020_0000 != 0,
        }
    }
}

/// The object's relation to the player.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub struct RelationshipTraits {
    /// In the player's fellowship.
    pub is_fellowship_member: bool,
    /// The fellowship's leader.
    pub is_fellowship_leader: bool,
    /// In the player's allegiance.
    pub is_allegiance_member: bool,
    /// The player is a player killer.
    pub player_is_player_killer: bool,
    /// The player is PK-lite.
    pub player_is_pk_lite: bool,
}

/// True when an object whose allegiance's monarch is `object_monarch` is in
/// the allegiance of a player whose monarch is `player_monarch`: it has a
/// monarch, and it is the player's (0: no monarch).
#[must_use]
pub fn is_allegiance_member(object_monarch: u32, player_monarch: u32) -> bool {
    object_monarch != 0 && object_monarch == player_monarch
}

/// The shape of an object's blip.
#[must_use]
pub fn blip_shape(target: &ObjectTraits, relationship: &RelationshipTraits) -> BlipShape {
    if !target.is_valid {
        return BlipShape::Undef;
    }
    if relationship.is_fellowship_leader {
        return BlipShape::Triangle;
    }
    if relationship.is_fellowship_member {
        return BlipShape::InvertedTriangle;
    }
    if relationship.is_allegiance_member {
        return BlipShape::Box;
    }
    if (target.is_player_killer && relationship.player_is_player_killer)
        || (target.is_pk_lite && relationship.player_is_pk_lite)
    {
        return BlipShape::X;
    }
    BlipShape::Plus
}

/// The radar's blip colours.
pub mod colors {
    use crate::draw::Color;

    /// Blue.
    pub const BLUE: Color = Color::rgba(0.25, 0.66, 1.0, 1.0);
    /// Gold.
    pub const GOLD: Color = Color::rgba(1.0, 0.67, 0.0, 1.0);
    /// Yellow.
    pub const YELLOW: Color = Color::rgba(1.0, 1.0, 0.5, 1.0);
    /// White.
    pub const WHITE: Color = Color::rgba(1.0, 1.0, 1.0, 1.0);
    /// Red.
    pub const RED: Color = Color::rgba(1.0, 0.25, 0.39, 1.0);
    /// Purple.
    pub const PURPLE: Color = Color::rgba(0.75, 0.39, 1.0, 1.0);
    /// Pink.
    pub const PINK: Color = Color::rgba(1.0, 0.66, 0.75, 1.0);
    /// Green.
    pub const GREEN: Color = Color::rgba(0.0, 0.5, 0.25, 1.0);
    /// Cyan.
    pub const CYAN: Color = Color::rgba(0.0, 1.0, 1.0, 1.0);
    /// Bright green (the player, the fellowship).
    pub const BRIGHT_GREEN: Color = Color::rgba(0.0, 1.0, 0.0, 1.0);
}

/// A weenie's own blip colour by its override value.
#[must_use]
pub fn color_for_override(value: u8) -> Color {
    match value {
        1 => colors::BLUE,
        2 => colors::GOLD,
        4 => colors::PURPLE,
        5 => colors::RED,
        6 => colors::PINK,
        7 => colors::GREEN,
        8 => colors::YELLOW,
        9 => colors::CYAN,
        10 => colors::BRIGHT_GREEN,
        _ => colors::WHITE,
    }
}

/// The colour of an object's blip.
#[must_use]
pub fn blip_color(traits: &ObjectTraits, relationship: &RelationshipTraits) -> Color {
    if !traits.is_valid {
        return colors::WHITE;
    }
    if traits.blip_color_override != 0 {
        return color_for_override(traits.blip_color_override);
    }
    if traits.is_portal {
        return colors::PURPLE;
    }
    if traits.is_vendor {
        return colors::YELLOW;
    }
    if traits.is_attackable && traits.is_creature && !traits.is_player {
        return colors::GOLD;
    }
    if !traits.is_player {
        return colors::WHITE;
    }
    let mut color = colors::WHITE;
    if traits.is_admin && !traits.is_hidden_admin {
        color = colors::CYAN;
    } else if traits.is_player_killer {
        color = colors::RED;
    } else if traits.is_pk_lite {
        color = colors::PINK;
    } else if traits.is_free_pk {
        color = colors::GOLD;
    }
    if relationship.is_fellowship_leader || relationship.is_fellowship_member {
        color = colors::BRIGHT_GREEN;
    }
    color
}

/// `color` with its red, green and blue scaled by `m` (alpha kept).
#[must_use]
pub fn dim_rgb(color: Color, m: f32) -> Color {
    Color::rgba(color.r * m, color.g * m, color.b * m, color.a)
}

/// The radar range for where the player is, meters.
#[must_use]
pub fn range_meters(is_outside: bool) -> f32 {
    if is_outside {
        OUTDOOR_RANGE_METERS
    } else {
        INDOOR_RANGE_METERS
    }
}

/// The RGB multiplier for an object `z` meters above or below the player.
#[must_use]
pub fn altitude_rgb_multiplier(player_space_z: f32) -> f32 {
    if player_space_z.abs() < ALTITUDE_DIM_THRESHOLD_METERS {
        1.0
    } else {
        ALTITUDE_DIM_MULTIPLIER
    }
}

/// A projected object: its pixel and its RGB multiplier.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Projection {
    /// Radar pixel (whole pixels).
    pub pixel: Vec2,
    /// RGB multiplier for height.
    pub rgb_multiplier: f32,
}

/// Project a player-relative position (meters; +y ahead, +x right, +z up)
/// onto the radar. Objects at or past `range - 1` meters are left out.
#[must_use]
pub fn project(
    player_space: Vec3,
    center: Vec2,
    radius_pixels: i32,
    range_meters: f32,
) -> Option<Projection> {
    let inclusion = range_meters - 1.0;
    let d2 = player_space.x * player_space.x + player_space.y * player_space.y;
    if d2 >= inclusion * inclusion {
        return None;
    }
    let scale = radius_pixels as f32 / range_meters;
    Some(Projection {
        pixel: Vec2::new(
            (center.x + player_space.x * scale).trunc(),
            (center.y - player_space.y * scale).trunc(),
        ),
        rgb_multiplier: altitude_rgb_multiplier(player_space.z),
    })
}

/// A world offset turned into the player's frame (the inverse of the
/// player's rotation).
#[must_use]
pub fn global_to_local(player_rotation: Quat, world_offset: Vec3) -> Vec3 {
    player_rotation.conjugate() * world_offset
}

/// Compass heading, degrees clockwise from north, from a yaw in radians
/// (counter-clockwise from east).
#[must_use]
pub fn heading_from_yaw(yaw_radians: f32) -> f32 {
    let mut h = (90.0 - yaw_radians * (180.0 / std::f32::consts::PI)) % 360.0;
    if h < 0.0 {
        h += 360.0;
    }
    h
}

/// A compass letter.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum CompassPoint {
    /// N.
    North,
    /// E.
    East,
    /// S.
    South,
    /// W.
    West,
}

/// Where a compass letter's top-left goes for the player's heading: on a
/// circle of `magnitude` pixels around `center`, turned with the player.
#[must_use]
pub fn compass_token_top_left(
    heading_degrees: f32,
    point: CompassPoint,
    center: Vec2,
    magnitude: f32,
    size: Vec2,
) -> Vec2 {
    let heading = heading_degrees * 0.017_453_292;
    let angle = f64::from(heading)
        + match point {
            CompassPoint::North => std::f64::consts::PI,
            CompassPoint::East => f64::from(1.570_796_4_f32),
            CompassPoint::South => 0.0,
            CompassPoint::West => f64::from(4.712_389_f32),
        };
    let offset_x = (angle.sin() * f64::from(magnitude)) as f32;
    let cx = (f64::from(offset_x) + f64::from(center.x)) as f32;
    let cy = (angle.cos() * f64::from(magnitude) + f64::from(center.y)) as f32;
    Vec2::new((cx - size.x * 0.5).trunc(), (cy - size.y * 0.5).trunc())
}

/// Map coordinates: east-west and north-south, in tenths of a landblock.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct RadarCoordinates {
    /// East (+) / west (−).
    pub x: f64,
    /// North (+) / south (−).
    pub y: f64,
}

impl RadarCoordinates {
    /// The coordinates of an outdoor cell; `None` indoors.
    #[must_use]
    pub fn from_cell(cell_id: u32) -> Option<Self> {
        let (lx, ly) = ac_core::physics::gid_to_lcoord(cell_id)?;
        Some(Self {
            x: f64::from(lx - 1024) * 0.1 + 0.5,
            y: f64::from(ly - 1024) * 0.1 + 0.5,
        })
    }

    fn axis(value: f64, negative: &str, positive: &str) -> String {
        let suffix = if value < 0.0 {
            negative
        } else if value > 0.0 {
            positive
        } else {
            ""
        };
        format!("{:.1}{suffix}", value.abs())
    }

    /// "12.3E".
    #[must_use]
    pub fn x_text(&self) -> String {
        Self::axis(self.x, "W", "E")
    }

    /// "45.6N".
    #[must_use]
    pub fn y_text(&self) -> String {
        Self::axis(self.y, "S", "N")
    }

    /// "45.6N,12.3E" as the radar shows it.
    #[must_use]
    pub fn combined_text(&self) -> String {
        format!("{},{}", self.y_text(), self.x_text())
    }
}

/// One blip as the radar draws it.
#[derive(Debug, Clone, PartialEq)]
pub struct RadarBlip {
    /// Object id.
    pub object_id: u32,
    /// Name (the hover tooltip).
    pub name: String,
    /// Pixel x on the radar.
    pub pixel_x: f32,
    /// Pixel y on the radar.
    pub pixel_y: f32,
    /// Colour.
    pub color: Color,
    /// Shape.
    pub shape: BlipShape,
    /// The selected object (outlined).
    pub selected: bool,
}

/// One radar frame.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct RadarSnapshot {
    /// The player's heading, degrees.
    pub player_heading_degrees: f32,
    /// The blips.
    pub blips: Vec<RadarBlip>,
    /// The coordinates line (`None`: hidden).
    pub coordinates_text: Option<String>,
    /// Blips hidden (and not hoverable).
    pub blank_blips: bool,
    /// The UI is locked.
    pub ui_locked: bool,
}

/// Round to the nearest whole pixel, ties to even.
#[must_use]
pub fn round_pixel(value: f32) -> i32 {
    value.round_ties_even() as i32
}

/// The fixed player marker colour.
pub const PLAYER_MARKER_COLOR: Color = colors::BRIGHT_GREEN;

#[cfg(test)]
mod tests {
    //! Golden values from OpenAC `AcDream.Core/Ui/RetailRadar.cs` and
    //! `RadarBlipColors.cs` (ports of the game's radar).

    use super::*;

    #[test]
    fn selection_outline_is_a_hollow_seven_by_seven_ring() {
        assert_eq!(SELECTION_PIXELS.len(), 20);
        assert!(SELECTION_PIXELS.contains(&(-2, 3)));
        assert!(SELECTION_PIXELS.contains(&(3, 2)));
        assert!(!SELECTION_PIXELS.contains(&(3, 3)));
    }

    #[test]
    fn shapes_fill_their_retail_pixels() {
        assert_eq!(blip_pixels(BlipShape::Plus).len(), 5);
        assert_eq!(blip_pixels(BlipShape::XBox).len(), 12);
        assert_eq!(blip_pixels(BlipShape::Box).len(), 8);
        assert!(blip_pixels(BlipShape::Undef).is_empty());
        assert_eq!(PLAYER_MARKER_PIXELS.len(), 9);
    }

    #[test]
    fn projection_excludes_the_last_meter_and_truncates() {
        let c = Vec2::new(60.0, 60.0);
        let p = project(Vec3::new(15.0, 30.0, 0.0), c, 50, 75.0).unwrap();
        assert_eq!(p.pixel, Vec2::new(70.0, 40.0));
        assert_eq!(p.rgb_multiplier, 1.0);
        assert!(project(Vec3::new(74.0, 0.0, 0.0), c, 50, 75.0).is_none());
        let high = project(Vec3::new(0.0, 0.0, 6.0), c, 50, 75.0).unwrap();
        assert_eq!(high.rgb_multiplier, 0.65);
    }

    #[test]
    fn heading_is_clockwise_from_north() {
        assert_eq!(heading_from_yaw(0.0), 90.0);
        assert!((heading_from_yaw(std::f32::consts::FRAC_PI_2) - 0.0).abs() < 1e-4);
        assert!((heading_from_yaw(std::f32::consts::PI) - 270.0).abs() < 1e-4);
    }

    #[test]
    fn north_token_sits_above_center_at_heading_zero() {
        // OpenAC RadarControllerTests: north token 10x9 at (55,1) for a
        // 120x120 disc centred at (60,60).
        let center = Vec2::new(60.0, 60.0);
        let magnitude = Vec2::new(60.0, 5.5).distance(center);
        let p = compass_token_top_left(
            0.0,
            CompassPoint::North,
            center,
            magnitude,
            Vec2::new(10.0, 9.0),
        );
        assert_eq!(p, Vec2::new(55.0, 1.0));
    }

    #[test]
    fn an_object_hidden_from_the_ui_gets_no_blip() {
        // A trap hidden from the UI (bit 0x80 of the description) is left
        // off the radar; the top bit of the field is no such mark.
        let trap = ObjectTraits::from_public_weenie_description(0x10, 0x0000_0094, 0);
        assert!(!trap.is_valid);
        assert_eq!(
            blip_shape(&trap, &RelationshipTraits::default()),
            BlipShape::Undef
        );
        let top_bit = ObjectTraits::from_public_weenie_description(0x10, 0x8000_0014, 0);
        assert!(top_bit.is_valid);
        assert_eq!(
            blip_shape(&top_bit, &RelationshipTraits::default()),
            BlipShape::Plus
        );
    }

    #[test]
    fn colors_follow_the_retail_priority() {
        let player = ObjectTraits {
            is_player: true,
            is_player_killer: true,
            ..ObjectTraits::default()
        };
        assert_eq!(
            blip_color(&player, &RelationshipTraits::default()),
            colors::RED
        );
        let fellow = RelationshipTraits {
            is_fellowship_member: true,
            ..RelationshipTraits::default()
        };
        assert_eq!(blip_color(&player, &fellow), colors::BRIGHT_GREEN);
        let monster = ObjectTraits::from_public_weenie_description(0x10, 0x10, 0);
        assert_eq!(
            blip_color(&monster, &RelationshipTraits::default()),
            colors::GOLD
        );
        let portal = ObjectTraits::from_public_weenie_description(0, 0x0004_0000, 0);
        assert_eq!(
            blip_color(&portal, &RelationshipTraits::default()),
            colors::PURPLE
        );
        let overridden = ObjectTraits::from_public_weenie_description(0, 0x0004_0000, 7);
        assert_eq!(
            blip_color(&overridden, &RelationshipTraits::default()),
            colors::GREEN
        );
        assert_eq!(
            blip_shape(
                &player,
                &RelationshipTraits {
                    player_is_player_killer: true,
                    ..RelationshipTraits::default()
                }
            ),
            BlipShape::X
        );
        assert_eq!(blip_shape(&player, &fellow), BlipShape::InvertedTriangle);
    }

    #[test]
    fn coordinates_read_like_the_radar() {
        // Cell 0xA9B40031 is land coordinate (1358, 1440) (ac-core's
        // golden): ((1358 - 1024) * 0.1 + 0.5, (1440 - 1024) * 0.1 + 0.5).
        let c = RadarCoordinates::from_cell(0xA9B4_0031).unwrap();
        assert_eq!(c.combined_text(), "42.1N,33.9E");
        assert!(RadarCoordinates::from_cell(0xA9B4_0164).is_none());
        let south_west = RadarCoordinates {
            x: -0.04,
            y: -12.26,
        };
        assert_eq!(south_west.combined_text(), "12.3S,0.0W");
    }

    #[test]
    fn allegiance_membership_is_a_shared_nonzero_monarch() {
        assert!(is_allegiance_member(0x5000_0777, 0x5000_0777));
        assert!(!is_allegiance_member(0x5000_0777, 0x5000_0778));
        assert!(!is_allegiance_member(0x5000_0777, 0));
        // Neither in an allegiance: not "the same allegiance".
        assert!(!is_allegiance_member(0, 0));
    }

    #[test]
    fn an_allegiance_member_is_a_box_below_the_fellowship() {
        let player = ObjectTraits {
            is_player: true,
            is_player_killer: true,
            ..ObjectTraits::default()
        };
        let member = RelationshipTraits {
            is_allegiance_member: true,
            player_is_player_killer: true,
            ..RelationshipTraits::default()
        };
        // Before the player-killer threat.
        assert_eq!(blip_shape(&player, &member), BlipShape::Box);
        let fellow_member = RelationshipTraits {
            is_fellowship_member: true,
            ..member
        };
        assert_eq!(
            blip_shape(&player, &fellow_member),
            BlipShape::InvertedTriangle
        );
    }

    #[test]
    fn showable_behaviors() {
        assert!(is_showable(RadarBehavior::ShowAlways, true));
        assert!(is_showable(RadarBehavior::ShowMovement, true));
        assert!(!is_showable(RadarBehavior::ShowNever, true));
        assert!(!is_showable(RadarBehavior::ShowAlways, false));
    }
}
