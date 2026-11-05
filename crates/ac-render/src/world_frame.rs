//! The coordinates the window draws in.
//!
//! Positions arrive as a cell id (which landblock) plus meters from that
//! landblock's south-west corner. The window draws everything relative to
//! one center landblock near the player (the floating origin), so numbers
//! stay small and f32 stays precise: landblock (x, y) sits at
//! `((x - cx) * 192, (y - cy) * 192)` meters, z unchanged.

use ac_core::physics::BLOCK_LENGTH;
use ac_core::world::LiveWorldOrigin;
use glam::Vec3;

/// A landblock's grid coordinates (the two high bytes of its cell ids).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub struct LandblockCoord {
    /// West to east, 0..=254.
    pub x: u8,
    /// South to north, 0..=254.
    pub y: u8,
}

impl LandblockCoord {
    /// The landblock a cell id belongs to.
    #[must_use]
    pub fn of_cell(cell_id: u32) -> Self {
        Self {
            x: (cell_id >> 24) as u8,
            y: (cell_id >> 16) as u8,
        }
    }

    /// The cell-dat id of the landblock's terrain (`XXYYFFFF`).
    #[must_use]
    pub fn terrain_id(self) -> u32 {
        (u32::from(self.x) << 24) | (u32::from(self.y) << 16) | 0xFFFF
    }

    /// The cell-dat id of the landblock's object list (`XXYYFFFE`).
    #[must_use]
    pub fn info_id(self) -> u32 {
        (u32::from(self.x) << 24) | (u32::from(self.y) << 16) | 0xFFFE
    }
}

/// Where a landblock's south-west corner lies in the drawing frame.
#[must_use]
pub fn landblock_offset(origin: &LiveWorldOrigin, block: LandblockCoord) -> Vec3 {
    let (cx, cy) = origin.center();
    Vec3::new(
        (i32::from(block.x) - cx) as f32 * BLOCK_LENGTH,
        (i32::from(block.y) - cy) as f32 * BLOCK_LENGTH,
        0.0,
    )
}

/// A landblock-relative position (`cell_id` and meters from that
/// landblock's corner) in the drawing frame.
#[must_use]
pub fn to_frame(origin: &LiveWorldOrigin, cell_id: u32, local: Vec3) -> Vec3 {
    landblock_offset(origin, LandblockCoord::of_cell(cell_id)) + local
}

/// The landblocks within `radius` of `center` (a square of side
/// `2 * radius + 1`), nearest rings first, clipped to the map
/// (coordinates 0..=254).
#[must_use]
pub fn landblocks_around(center: LandblockCoord, radius: u32) -> Vec<LandblockCoord> {
    let r = radius as i32;
    let mut out = Vec::with_capacity(((2 * r + 1) * (2 * r + 1)) as usize);
    for ring in 0..=r {
        for dy in -ring..=ring {
            for dx in -ring..=ring {
                if dx.abs().max(dy.abs()) != ring {
                    continue;
                }
                let x = i32::from(center.x) + dx;
                let y = i32::from(center.y) + dy;
                if (0..=254).contains(&x) && (0..=254).contains(&y) {
                    out.push(LandblockCoord {
                        x: x as u8,
                        y: y as u8,
                    });
                }
            }
        }
    }
    out
}

/// Chebyshev distance in landblocks (the larger of the x and y gaps).
#[must_use]
pub fn ring_distance(a: LandblockCoord, b: LandblockCoord) -> u32 {
    let dx = (i32::from(a.x) - i32::from(b.x)).unsigned_abs();
    let dy = (i32::from(a.y) - i32::from(b.y)).unsigned_abs();
    dx.max(dy)
}

/// What [`follow_player`] did to the origin.
#[derive(Debug, Clone, Copy, PartialEq)]
pub enum OriginMove {
    /// Nothing: the player is still near the center.
    Kept,
    /// The first position set the center.
    Initialized,
    /// The center moved to the player's landblock; everything placed in
    /// the frame moved by `shift` meters (old position + shift = new).
    Recentered {
        /// How far frame positions moved.
        shift: Vec3,
    },
}

/// Keep the drawing frame's origin near the player. The first known
/// position sets the center; after that the center stays put while the
/// player walks around it (frame coordinates stay well within f32
/// precision for kilometers) and moves to the player's landblock only once
/// the player is more than `keep_radius` landblocks away: a teleport, or a
/// long walk out of the loaded area.
pub fn follow_player(
    origin: &mut LiveWorldOrigin,
    player: LandblockCoord,
    keep_radius: u32,
) -> OriginMove {
    let wanted = (i32::from(player.x), i32::from(player.y));
    if !origin.is_known() {
        origin.try_initialize(wanted.0, wanted.1);
        return OriginMove::Initialized;
    }
    let (cx, cy) = origin.center();
    let distance = (wanted.0 - cx)
        .unsigned_abs()
        .max((wanted.1 - cy).unsigned_abs());
    if distance <= keep_radius {
        return OriginMove::Kept;
    }
    origin.recenter(wanted.0, wanted.1);
    OriginMove::Recentered {
        shift: Vec3::new(
            (cx - wanted.0) as f32 * BLOCK_LENGTH,
            (cy - wanted.1) as f32 * BLOCK_LENGTH,
            0.0,
        ),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn lb(x: u8, y: u8) -> LandblockCoord {
        LandblockCoord { x, y }
    }

    #[test]
    fn ids_of_a_landblock() {
        let b = LandblockCoord::of_cell(0xA9B4_0031);
        assert_eq!(b, lb(0xA9, 0xB4));
        assert_eq!(b.terrain_id(), 0xA9B4_FFFF);
        assert_eq!(b.info_id(), 0xA9B4_FFFE);
    }

    #[test]
    fn offsets_are_relative_to_the_center() {
        let mut o = LiveWorldOrigin::new();
        o.try_initialize(0xA9, 0xB4);
        assert_eq!(landblock_offset(&o, lb(0xA9, 0xB4)), Vec3::ZERO);
        assert_eq!(
            landblock_offset(&o, lb(0xA8, 0xB6)),
            Vec3::new(-192.0, 384.0, 0.0)
        );
        assert_eq!(
            to_frame(&o, 0xAAB4_0001, Vec3::new(10.0, 20.0, 30.0)),
            Vec3::new(202.0, 20.0, 30.0)
        );
    }

    #[test]
    fn landblocks_around_is_a_square_nearest_first() {
        let set = landblocks_around(lb(10, 10), 2);
        assert_eq!(set.len(), 25);
        assert_eq!(set[0], lb(10, 10));
        assert!(set[1..9].iter().all(|&b| ring_distance(b, lb(10, 10)) == 1));
        assert!(set[9..].iter().all(|&b| ring_distance(b, lb(10, 10)) == 2));
    }

    #[test]
    fn landblocks_around_clips_at_the_map_edge() {
        assert_eq!(landblocks_around(lb(0, 0), 2).len(), 9);
        assert_eq!(landblocks_around(lb(254, 254), 1).len(), 4);
    }

    #[test]
    fn follow_player_initializes_keeps_then_recenters_when_far() {
        let mut o = LiveWorldOrigin::new();
        assert_eq!(
            follow_player(&mut o, lb(10, 20), 3),
            OriginMove::Initialized
        );
        assert!(o.is_known());
        assert_eq!(follow_player(&mut o, lb(10, 20), 3), OriginMove::Kept);
        // Walking within the kept area leaves the origin alone.
        assert_eq!(follow_player(&mut o, lb(13, 17), 3), OriginMove::Kept);
        assert_eq!(o.center(), (10, 20));
        // Beyond it (a teleport), the origin follows and frame positions
        // shift by the difference.
        assert_eq!(
            follow_player(&mut o, lb(14, 19), 3),
            OriginMove::Recentered {
                shift: Vec3::new(-4.0 * 192.0, 192.0, 0.0)
            }
        );
        assert_eq!(o.center(), (14, 19));
    }

    #[test]
    fn a_recenter_shift_keeps_world_points_in_place() {
        let mut o = LiveWorldOrigin::new();
        follow_player(&mut o, lb(10, 10), 0);
        let before = to_frame(&o, 0x0B0B_0001, Vec3::new(5.0, 6.0, 7.0));
        let OriginMove::Recentered { shift } = follow_player(&mut o, lb(12, 9), 0) else {
            panic!("expected a recenter");
        };
        let after = to_frame(&o, 0x0B0B_0001, Vec3::new(5.0, 6.0, 7.0));
        assert_eq!(before + shift, after);
    }
}
