//! The client objects a house's entry barrier consults. A room whose
//! environment cell names a restriction object (a house) admits a player
//! only as the house says: an unknown house blocks, an unowned house, its
//! owner, or a house without an access list admits, otherwise the list
//! (public, the owner's allegiance, guests) decides.
//!
//! The sweep knows the body it moves by its client id, while owners,
//! guests and monarchs are server ids; the table names every object the
//! world knows by its client id so the comparison is between the same
//! kind of id (retail compares the moving object's own id).

use ac_physics::{HouseRestrictions, ObjectRestrictions, RestrictionObject};

use crate::world::World;

/// The restriction table for the world as it stands: every item record
/// (houses keep their owner and access list), with owners, guests and
/// monarchs that are present in the world named by their client ids.
#[must_use]
pub fn build(world: &World) -> ObjectRestrictions {
    let local = |guid: u32| world.object(guid).map_or(guid, |o| o.key().local_id);
    let mut table = ObjectRestrictions::new();
    for item in world.items().objects() {
        let guid = item.object_id();
        table.set_object(
            guid,
            RestrictionObject {
                house_owner_id: item.house_owner_id().map(local),
                restrictions: item.restrictions().map(|r| HouseRestrictions {
                    open_to_public: r.open_to_public,
                    allegiance_monarch_id: r.allegiance_monarch_id,
                    guests: r.guests.keys().map(|&g| local(g)).collect(),
                }),
            },
        );
        if let (Some(monarch), Some(object)) = (item.monarch_id(), world.object(guid)) {
            table.set_monarch(object.key().local_id, monarch);
        }
    }
    table
}

#[cfg(test)]
mod tests {
    //! The barrier through the runtime's table: OpenAC
    //! `EntryRestrictionGateTests` (`ResolvedHouse_MoverIsOwner_Admits`,
    //! `..._PresentListExcludesMover_Blocks`, `..._PresentListIncludesMover_Admits`,
    //! `..._MoverSharesAllegianceMonarch_Admits`, the unresolved and plain
    //! object cases) with the ids the sweep uses.
    use super::*;
    use ac_physics::{ObjectInfo, ObjectInfoState, TransitionState};
    use ac_proto::{
        CreateObject, HouseGuest, ModelData, PhysicsTimestamps, Position, PublicWeenieDesc,
        Quaternion, Vector3,
    };

    const HOUSE: u32 = 0x7A9B_4001;
    const PLAIN: u32 = 0x7A9B_4002;
    const ME: u32 = 0x5000_000A;

    fn create(world: &mut World, guid: u32, weenie: PublicWeenieDesc) {
        world.apply(&ac_proto::Inbound::CreateObject(Box::new(CreateObject {
            guid,
            model: ModelData::default(),
            physics: crate::world::tests::physics(
                PhysicsTimestamps {
                    instance: 1,
                    position: 1,
                    ..PhysicsTimestamps::default()
                },
                Some(Position {
                    cell_id: 0xA9B4_0001,
                    origin: Vector3 {
                        x: 10.0,
                        y: 10.0,
                        z: 0.0,
                    },
                    orientation: Quaternion::IDENTITY,
                }),
            ),
            weenie: Some(weenie),
        })));
    }

    fn house(owner: u32, guests: &[u32], monarch: u32) -> PublicWeenieDesc {
        PublicWeenieDesc {
            name: "Cottage".into(),
            house_owner_id: Some(owner),
            house_restrictions: Some(ac_proto::HouseRestrictions {
                version: 1,
                flags: 0,
                monarch_id: monarch,
                guests: guests
                    .iter()
                    .map(|&g| HouseGuest {
                        guest_id: g,
                        permission: 0,
                    })
                    .collect(),
            }),
            ..PublicWeenieDesc::default()
        }
    }

    fn me(world: &mut World, monarch: Option<u32>) -> u32 {
        create(
            world,
            ME,
            PublicWeenieDesc {
                name: "+Me".into(),
                monarch_id: monarch,
                ..PublicWeenieDesc::default()
            },
        );
        world.object(ME).unwrap().key().local_id
    }

    fn enter(world: &World, mover_local: u32, restriction: u32) -> TransitionState {
        let mover = ObjectInfo {
            state: ObjectInfoState(ObjectInfoState::IS_PLAYER),
            self_entity_id: mover_local,
            ..ObjectInfo::default()
        };
        mover.check_entry_restrictions(restriction, Some(&build(world)))
    }

    #[test]
    fn the_owner_enters_and_a_stranger_is_kept_out() {
        let mut world = World::new();
        let mine = me(&mut world, None);
        create(&mut world, HOUSE, house(ME, &[], 0));
        assert_eq!(enter(&world, mine, HOUSE), TransitionState::Ok);
        let mut world = World::new();
        let mine = me(&mut world, None);
        create(&mut world, HOUSE, house(0x5000_0077, &[], 0));
        assert_eq!(enter(&world, mine, HOUSE), TransitionState::Collided);
    }

    #[test]
    fn a_guest_or_a_follower_of_the_monarch_enters() {
        let mut world = World::new();
        let mine = me(&mut world, None);
        create(&mut world, HOUSE, house(0x5000_0077, &[ME], 0));
        assert_eq!(enter(&world, mine, HOUSE), TransitionState::Ok);
        let mut world = World::new();
        let mine = me(&mut world, Some(0x5000_0099));
        create(&mut world, HOUSE, house(0x5000_0077, &[], 0x5000_0099));
        assert_eq!(enter(&world, mine, HOUSE), TransitionState::Ok);
    }

    #[test]
    fn an_unknown_guard_blocks_and_a_plain_one_admits() {
        let mut world = World::new();
        let mine = me(&mut world, None);
        assert_eq!(enter(&world, mine, HOUSE), TransitionState::Collided);
        create(
            &mut world,
            PLAIN,
            PublicWeenieDesc {
                name: "Thing".into(),
                ..PublicWeenieDesc::default()
            },
        );
        assert_eq!(enter(&world, mine, PLAIN), TransitionState::Ok);
    }
}
