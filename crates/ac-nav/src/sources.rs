//! The rules the runtime's goal source answers a walk by: which objects
//! move, which stand in the way, which a grid takes as floor, which are
//! portals and where a walk going to a portal ends, and where a place lies
//! relative to the character. The runtime binds these to its object model
//! and the physics world; the rules themselves are here.

use ac_core::items::{ItemType, PublicWeenieFlags};
use ac_core::physics::PhysicsState;
use ac_physics::PhysicsEngine;
use glam::Vec3;

use crate::geometry::{LANDBLOCK_SIZE, highest_floor_under};
use crate::route::NavAvoidance;

/// The least radius a portal is kept out of by, before the body's own,
/// metres: a portal's physics may state no shape at all, and a route that
/// brushed its middle would still be sent through it.
pub const PORTAL_LEAST_RADIUS: f32 = 1.5;

/// How near a walk's goal a portal may stand, beyond its own radius, and
/// still be the portal the walk is going to, metres.
pub const PORTAL_GOAL_REACH: f32 = 3.0;

/// What the rules need to know of an object the client knows.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct NavObjectKind {
    /// Its item type bits.
    pub item_type: u32,
    /// Its public weenie flags.
    pub public_weenie_flags: u32,
    /// The object it follows as a pet, or 0.
    pub pet_owner_id: u32,
}

impl NavObjectKind {
    fn flags(&self) -> u32 {
        self.public_weenie_flags
    }

    fn is_creature(&self) -> bool {
        self.item_type & ItemType::CREATURE.bits() != 0
    }

    /// Whether an object moves about: a player, or a creature that can be
    /// attacked or follows an owner. A creature that can't be attacked and
    /// follows no one, such as a vendor, stands where it is.
    #[must_use]
    pub fn moves(&self) -> bool {
        self.flags() & PublicWeenieFlags::PLAYER.bits() != 0
            || (self.is_creature()
                && (self.flags() & PublicWeenieFlags::ATTACKABLE.bits() != 0
                    || self.pet_owner_id != 0))
    }

    /// Whether a route keeps out of the object's collision: not a player's
    /// or a moving creature's, nor a door's, which walks open, nor a
    /// corpse's, which routes cross rather than go around.
    #[must_use]
    pub fn stands_in_the_way(&self) -> bool {
        !self.moves()
            && self.flags() & (PublicWeenieFlags::DOOR.bits() | PublicWeenieFlags::CORPSE.bits())
                == 0
    }

    /// Whether a grid takes the object's collision as the floors and walls
    /// it is: anything that stands in the way but a creature.
    #[must_use]
    pub fn can_be_stood_on(&self) -> bool {
        self.stands_in_the_way() && !self.is_creature()
    }

    /// Whether the object is a portal, by its type or by the flag the server
    /// sends for one.
    #[must_use]
    pub fn is_portal(&self) -> bool {
        self.item_type & ItemType::PORTAL.bits() != 0
            || self.flags() & PublicWeenieFlags::PORTAL.bits() != 0
    }

    /// Whether the object is a player, the only kind a walk follows.
    #[must_use]
    pub fn is_player(&self) -> bool {
        self.flags() & PublicWeenieFlags::PLAYER.bits() != 0
    }
}

/// Whether an object in this physics state stands anywhere at all: a
/// missile in flight does not.
#[must_use]
pub fn stands_anywhere(state: u32) -> bool {
    state & PhysicsState::MISSILE.bits() == 0
}

/// A portal's footprint, at least [`PORTAL_LEAST_RADIUS`] across.
#[must_use]
pub fn portal_footprint(footprint: NavAvoidance) -> NavAvoidance {
    NavAvoidance::new(footprint.centre, footprint.radius.max(PORTAL_LEAST_RADIUS))
}

/// Whether a portal stands where a walk ends, so the walk is going to it
/// rather than past it.
#[must_use]
pub fn is_where_the_walk_ends(portal: &NavAvoidance, goal: Vec3) -> bool {
    portal.centre.truncate().distance(goal.truncate()) <= portal.radius + PORTAL_GOAL_REACH
}

/// The portals a route keeps out of: all but the one nearest where the
/// walk ends, if that one stands near enough to be where it is going. In
/// the Town Network the next portal stands ten metres away, so leaving out
/// every portal near the goal would leave a neighbour to run into.
#[must_use]
pub fn except_the_one_the_walk_ends(portals: &[NavAvoidance], goal: Vec3) -> Vec<NavAvoidance> {
    let mut going = None;
    let mut nearest = f32::INFINITY;
    for (index, portal) in portals.iter().enumerate() {
        let away = portal.centre.truncate().distance(goal.truncate());
        if away < nearest && is_where_the_walk_ends(portal, goal) {
            nearest = away;
            going = Some(index);
        }
    }
    portals
        .iter()
        .enumerate()
        .filter(|(index, _)| Some(*index) != going)
        .map(|(_, portal)| *portal)
        .collect()
}

/// How far a place lies from where the character stands, each given as a
/// cell and a point in that cell's landblock frame. A cell of zero measures
/// its point from the corner of the first landblock.
#[must_use]
pub fn place_offset(cell_id: u32, local: Vec3, here_cell_id: u32, here_local: Vec3) -> Vec3 {
    let blocks_east = ((cell_id >> 24) & 0xFF) as i32 - ((here_cell_id >> 24) & 0xFF) as i32;
    let blocks_north = ((cell_id >> 16) & 0xFF) as i32 - ((here_cell_id >> 16) & 0xFF) as i32;
    Vec3::new(
        blocks_east as f32 * LANDBLOCK_SIZE + local.x - here_local.x,
        blocks_north as f32 * LANDBLOCK_SIZE + local.y - here_local.y,
        local.z - here_local.z,
    )
}

/// A point whose height is not known (NaN) stood on the terrain under it,
/// or at `fallback_z` where no terrain under it is loaded; a point with a
/// height is left as it is.
#[must_use]
pub fn on_the_ground(physics: &PhysicsEngine, point: Vec3, fallback_z: f32) -> Vec3 {
    if point.z.is_nan() {
        point.with_z(
            physics
                .sample_terrain_z(point.x, point.y)
                .unwrap_or(fallback_z),
        )
    } else {
        point
    }
}

/// How far a place may be from a floor, over it or under it, and still
/// count as given on that floor, metres: a route waypoint recorded where the
/// character stood is at foot height, a little out at most, so a place this
/// near a floor is not moved to another one.
pub const PLACE_ABOVE_FLOOR_METERS: f32 = 0.5;

/// A place in an interior cell stood where a walk can arrive at it. A place
/// in a cell is often given by the middle of the cell's bounds, which in a
/// tall room is metres above the floor, higher than a walk arrives at.
///
/// - When a face-up surface the route grid holds (a cell's floor, or the
///   top of an object such as a bridge, a platform or a crate) lies no more
///   than [`PLACE_ABOVE_FLOOR_METERS`] under the place, the place is left
///   where it is; when one lies no more than that over it, the place is
///   lifted onto it. So a waypoint recorded on top of a platform stays on
///   the platform, while the middle of a room, well over a table in it,
///   does not stop on the table.
/// - Otherwise the place drops to its cell's own floor nearest at or below
///   it, or, where that cell has no floor there (the upper half of a tall
///   room authored as a cell on top of a cell), to the highest floor under
///   it of any cell in the landblock, so a place over a pit or a shaft drops
///   to the pit's bottom.
/// - An outdoor place, one with no height (NaN), or one with no floor under
///   it at all is left as it is.
///
/// `stands_on` names the objects the server placed that the walk's grid
/// stands on, as it does when it builds the grid.
#[must_use]
pub fn on_the_cell_floor(
    physics: &PhysicsEngine,
    cell_id: u32,
    point: Vec3,
    stands_on: Option<&dyn Fn(u32) -> bool>,
) -> Vec3 {
    let low = cell_id & 0xFFFF;
    if low < 0x0100 || low == 0xFFFF || point.z.is_nan() {
        return point;
    }
    let Some(landblock) = physics.landblock((cell_id & 0xFFFF_0000) | 0xFFFF) else {
        return point;
    };
    let at_most = point.z + PLACE_ABOVE_FLOOR_METERS;
    if let Some(standing) = highest_floor_under(
        physics,
        cell_id,
        point.x,
        point.y,
        point.z - PLACE_ABOVE_FLOOR_METERS,
        at_most,
        stands_on,
    ) {
        return if standing > point.z {
            point.with_z(standing)
        } else {
            point
        };
    }
    let mut own: Option<f32> = None;
    let mut any: Option<f32> = None;
    for cell in &landblock.cells {
        let Some(z) = cell.sample_level_z_at_or_below(point.x, point.y, at_most) else {
            continue;
        };
        if cell.cell_id() == cell_id {
            own = Some(own.map_or(z, |o| o.max(z)));
        }
        any = Some(any.map_or(z, |a| a.max(z)));
    }
    match (own, any) {
        (Some(floor), _) => point.with_z(floor),
        (None, Some(below)) => point.with_z(below),
        (None, None) => point,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn kind(item_type: ItemType, flags: PublicWeenieFlags, pet_owner_id: u32) -> NavObjectKind {
        NavObjectKind {
            item_type: item_type.bits(),
            public_weenie_flags: flags.bits(),
            pet_owner_id,
        }
    }

    #[test]
    fn players_and_creatures_that_fight_or_follow_move_and_a_vendor_does_not() {
        let player = kind(ItemType::CREATURE, PublicWeenieFlags::PLAYER, 0);
        let monster = kind(ItemType::CREATURE, PublicWeenieFlags::ATTACKABLE, 0);
        let pet = kind(ItemType::CREATURE, PublicWeenieFlags(0), 0x5000_0001);
        let vendor = kind(ItemType::CREATURE, PublicWeenieFlags(0), 0);

        assert!(player.moves() && monster.moves() && pet.moves());
        assert!(!vendor.moves());
        assert!(vendor.stands_in_the_way());
        assert!(!vendor.can_be_stood_on(), "no body stands on a creature");
        assert!(player.is_player() && !monster.is_player());
    }

    #[test]
    fn doors_and_corpses_are_not_in_the_way_and_a_chest_is_floor() {
        let door = kind(ItemType::MISC, PublicWeenieFlags::DOOR, 0);
        let corpse = kind(ItemType::CONTAINER, PublicWeenieFlags::CORPSE, 0);
        let chest = kind(ItemType::CONTAINER, PublicWeenieFlags(0), 0);

        assert!(!door.stands_in_the_way());
        assert!(!corpse.stands_in_the_way());
        assert!(chest.stands_in_the_way() && chest.can_be_stood_on());
    }

    #[test]
    fn a_portal_is_known_by_its_type_or_its_flag() {
        assert!(kind(ItemType::PORTAL, PublicWeenieFlags(0), 0).is_portal());
        assert!(kind(ItemType::MISC, PublicWeenieFlags::PORTAL, 0).is_portal());
        assert!(!kind(ItemType::MISC, PublicWeenieFlags(0), 0).is_portal());
    }

    #[test]
    fn a_missile_in_flight_stands_nowhere() {
        assert!(!stands_anywhere(PhysicsState::MISSILE.bits()));
        assert!(stands_anywhere(PhysicsState::ETHEREAL.bits()));
    }

    /// The walk going to a portal keeps out of its neighbour ten metres
    /// on, as in the Town Network.
    #[test]
    fn only_the_portal_the_walk_ends_at_is_left_out() {
        let going = portal_footprint(NavAvoidance::new(Vec3::new(10.0, 10.0, 0.0), 0.0));
        let neighbour = portal_footprint(NavAvoidance::new(Vec3::new(20.0, 10.0, 0.0), 0.5));
        let goal = Vec3::new(11.0, 11.0, 0.0);

        let kept = except_the_one_the_walk_ends(&[going, neighbour], goal);

        assert_eq!(going.radius, PORTAL_LEAST_RADIUS);
        assert_eq!(kept, vec![neighbour]);
        assert_eq!(
            except_the_one_the_walk_ends(&[going, neighbour], Vec3::new(50.0, 50.0, 0.0)).len(),
            2
        );
    }

    #[test]
    fn a_place_lies_from_the_character_by_the_landblocks_between_them() {
        let offset = place_offset(
            0xAAB5_0021,
            Vec3::new(10.0, 20.0, 5.0),
            0xA9B4_0021,
            Vec3::new(100.0, 50.0, 2.0),
        );

        assert_eq!(
            offset,
            Vec3::new(192.0 + 10.0 - 100.0, 192.0 + 20.0 - 50.0, 3.0)
        );
    }
}

#[cfg(test)]
mod goal_source_tests {
    //! Ported from OpenAC
    //! `tests/AcDream.App.Tests/Navigation/RuntimeNavigationGoalSourceTests.cs`
    //! (every case; the runtime's goal source answers by these rules).

    use super::*;
    use ac_physics::TerrainSurface;

    fn item(item_type: ItemType, flags: PublicWeenieFlags, pet_owner_id: u32) -> NavObjectKind {
        NavObjectKind {
            item_type: item_type.bits(),
            public_weenie_flags: flags.bits(),
            pet_owner_id,
        }
    }

    const NONE: PublicWeenieFlags = PublicWeenieFlags(0);
    const NO_TYPE: ItemType = ItemType::NONE;

    /// A place given no height stands on the terrain under it, and at the
    /// fallback height where none is loaded.
    #[test]
    fn a_place_with_no_height_stands_on_the_terrain_under_it() {
        let mut heights = [0.0f32; 256];
        heights[0] = 5.0;
        let mut physics = PhysicsEngine::new();
        physics.add_landblock(
            0xA9B4_FFFF,
            TerrainSurface::new(&[0; 81], &heights, 0, 0, None),
            Vec::new(),
            Vec::new(),
            0.0,
            0.0,
        );

        let loaded = on_the_ground(&physics, Vec3::new(60.0, 70.0, f32::NAN), 90.0);
        let unloaded = on_the_ground(&physics, Vec3::new(400.0, 70.0, f32::NAN), 90.0);
        let given = on_the_ground(&physics, Vec3::new(60.0, 70.0, 12.0), 90.0);

        assert!((loaded.z - 5.0).abs() < 1e-3);
        assert_eq!(unloaded.z, 90.0);
        assert_eq!(given.z, 12.0);
    }

    #[test]
    fn a_route_keeps_out_of_an_object_that_stands_still() {
        assert!(item(NO_TYPE, NONE, 0).stands_in_the_way());
    }

    #[test]
    fn a_route_keeps_out_of_a_creature_that_cannot_be_attacked_and_follows_no_one_such_as_a_vendor()
    {
        for flags in [NONE, PublicWeenieFlags::VENDOR] {
            let vendor = item(ItemType::CREATURE, flags, 0);
            assert!(!vendor.moves());
            assert!(vendor.stands_in_the_way());
        }
    }

    #[test]
    fn players_and_creatures_that_can_be_attacked_or_follow_an_owner_move() {
        for (flags, pet_owner) in [
            (PublicWeenieFlags::ATTACKABLE, 0),
            (NONE, 0x5000_0001),
            (PublicWeenieFlags::PLAYER, 0),
        ] {
            let mover = item(ItemType::CREATURE, flags, pet_owner);
            assert!(mover.moves());
            assert!(!mover.stands_in_the_way());
        }
    }

    /// A grid takes the collision of anything that stands where it is and
    /// can be met from any side.
    #[test]
    fn a_grid_takes_the_collision_of_an_object_that_stands_still() {
        assert!(item(NO_TYPE, NONE, 0).can_be_stood_on());
    }

    /// A creature holds its ground, but no body stands on one.
    #[test]
    fn a_grid_leaves_creatures_to_be_walked_around() {
        for flags in [NONE, PublicWeenieFlags::VENDOR] {
            let vendor = item(ItemType::CREATURE, flags, 0);
            assert!(vendor.stands_in_the_way());
            assert!(!vendor.can_be_stood_on());
        }
    }

    #[test]
    fn a_grid_leaves_out_what_comes_and_goes_or_moves() {
        for flags in [
            PublicWeenieFlags::DOOR,
            PublicWeenieFlags::CORPSE,
            PublicWeenieFlags::PLAYER,
        ] {
            assert!(!item(NO_TYPE, flags, 0).can_be_stood_on());
        }
    }

    #[test]
    fn a_route_does_not_keep_out_of_doors_or_corpses() {
        for flags in [PublicWeenieFlags::DOOR, PublicWeenieFlags::CORPSE] {
            assert!(!item(NO_TYPE, flags, 0).stands_in_the_way());
        }
    }

    #[test]
    fn a_portal_is_known_by_its_type_or_its_flag_alone() {
        assert!(item(ItemType::PORTAL, NONE, 0).is_portal());
        assert!(item(NO_TYPE, PublicWeenieFlags::PORTAL, 0).is_portal());
    }

    #[test]
    fn an_ordinary_object_is_no_portal() {
        assert!(!item(NO_TYPE, PublicWeenieFlags::DOOR, 0).is_portal());
    }

    /// A portal whose physics states no shape is still kept out of by a
    /// body's width and more.
    #[test]
    fn a_portal_with_no_shape_is_kept_out_of_all_the_same() {
        assert_eq!(
            portal_footprint(NavAvoidance::new(Vec3::ZERO, 0.0)).radius,
            PORTAL_LEAST_RADIUS
        );
    }

    /// Travel walks to a spot a couple of metres from its portal, so a
    /// portal that near the goal is the one being gone to, and one across
    /// the square is not.
    #[test]
    fn a_portal_at_the_walks_end_is_the_one_it_goes_to() {
        for (away, goes_to) in [(2.0, true), (4.0, true), (8.0, false)] {
            assert_eq!(
                is_where_the_walk_ends(
                    &NavAvoidance::new(Vec3::new(away, 0.0, 0.0), 1.5),
                    Vec3::ZERO
                ),
                goes_to,
                "{away} m away"
            );
        }
    }

    /// Two portals near the walk's end, as in the Town Network: only the
    /// nearer is the one being gone to, and the other is still kept out of.
    #[test]
    fn only_the_portal_nearest_the_walks_end_is_left_out() {
        let going = NavAvoidance::new(Vec3::new(1.0, 0.0, 0.0), 1.5);
        let neighbour = NavAvoidance::new(Vec3::new(3.5, 0.0, 0.0), 1.5);

        let kept = except_the_one_the_walk_ends(&[neighbour, going], Vec3::ZERO);

        assert_eq!(kept, vec![neighbour]);
    }

    /// A missile in flight stands nowhere, so it is none of the three things
    /// a walk asks about objects.
    #[test]
    fn a_missile_in_flight_stands_nowhere_even_when_ethereal() {
        assert!(!stands_anywhere(PhysicsState::MISSILE.bits()));
        assert!(!stands_anywhere(
            (PhysicsState::MISSILE | PhysicsState::ETHEREAL).bits()
        ));
        assert!(stands_anywhere(0));
        assert!(stands_anywhere(PhysicsState::ETHEREAL.bits()));
    }
}
