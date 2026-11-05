//! The raw description words and optional values of an object, as the
//! server sent them, reach a plugin on both arms, for a thing out in the
//! world and for one in a pack, and a value the description did not carry
//! reads as absent (OpenAC `Scenarios/ObjectHeaderParityTests.cs`).
//!
//! OpenAC hands its arms a parsed spawn whose flag words and fields need not
//! agree; here the server writes real bytes, so each flag word names exactly
//! the fields that follow it (the lantern's and the gem's carry the use
//! radius bit their radius needs). The pebble OpenAC writes straight into
//! the pack record is created by the server like the gem.

use ac_plugin::abi::calls;

use crate::host_parity::arm::run;
use crate::host_parity::wire::Object;
use crate::host_parity::world;

const LANTERN: u32 = 0x5000_0091;
const GEM: u32 = 0x5000_0092;
const PEBBLE: u32 = 0x5000_0093;

// OpenAC `ObjectHeaderParityTests.EitherClientReportsAWorldObjectsHeader`.
#[test]
fn either_client_reports_a_world_objects_header() {
    run(|arm, transcript| {
        world::stage(arm);
        let lantern = Object {
            physics_state: world::ETHEREAL,
            motion_table: Some(0x0900_0001),
            scale: Some(1.25),
            object_description_flags: 0x10,
            useability: Some(1),
            use_radius: Some(3.0),
            target_type: Some(0),
            burden: Some(10),
            hook_type: Some(2),
            ..world::on_the_ground(LANTERN, "Lantern", world::MISC, world::PLAYER_X + 3.0)
        };
        arm.server.send(&lantern.build());
        arm.sync();

        let object = arm
            .call::<calls::WorldObject>(&LANTERN)
            .expect("the lantern is known");
        let header = object.header.expect("a header");
        transcript.record("weenieFlags", header.weenie_header_flags);
        transcript.record("physicsFlags", header.physics_description_flags);
        transcript.record("header", &header);
        assert_eq!(header.weenie_header_flags, Some(0x1028_0030));
        assert_eq!(header.weenie_header_flags2, None);
        assert_eq!(header.physics_description_flags, Some(0x0000_8083));
        assert_eq!(header.physics_state, Some(world::ETHEREAL));
        assert_eq!(header.object_description_flags, Some(0x10));
        assert_eq!(header.setup_id, Some(world::SETUP));
        assert_eq!(header.scale, Some(1.25));
        assert_eq!(header.hook_type, Some(2));
        assert_eq!(header.parent_id, None);
        assert_eq!(header.parent_location, None);
        assert_eq!(header.use_radius, Some(3.0));
    });
}

// OpenAC `ObjectHeaderParityTests.EitherClientReportsAPackedItemsHeaderAndUseRadius`.
#[test]
fn either_client_reports_a_packed_items_header_and_use_radius() {
    run(|arm, transcript| {
        world::stage(arm);
        let gem = Object {
            container: Some(world::PLAYER),
            burden: Some(5),
            use_radius: Some(0.5),
            icon_underlay_id: Some(0x0600_1234),
            ..Object::new(GEM, "Gem", world::GEM)
        };
        let pebble = Object {
            container: Some(world::PLAYER),
            burden: Some(1),
            use_radius: Some(0.75),
            stack_size: Some(1),
            ..Object::new(PEBBLE, "Pebble", world::MISC)
        };
        arm.server.send(&gem.build());
        arm.server.send(&pebble.build());
        arm.sync();

        let owned = arm.call::<calls::ItemsOwned>(&());
        let gem = owned
            .iter()
            .find(|i| i.object_id == GEM)
            .unwrap_or_else(|| panic!("{}: the gem is owned: {owned:?}", arm.name()));
        let header = gem.header.clone().expect("a header");
        transcript.record("weenieFlags", header.weenie_header_flags);
        transcript.record("weenieFlags2", header.weenie_header_flags2);
        transcript.record("useRadius", gem.use_radius);
        assert_eq!(header.weenie_header_flags, Some(0x0020_4020));
        assert_eq!(header.weenie_header_flags2, Some(0x1));
        assert_eq!(header.physics_description_flags, Some(0));
        assert_eq!(header.scale, None);
        assert!((gem.use_radius - 0.5).abs() < f32::EPSILON);

        let pebble = owned
            .iter()
            .find(|i| i.object_id == PEBBLE)
            .expect("the pebble is owned");
        transcript.record("packedUseRadius", pebble.use_radius);
        assert!((pebble.use_radius - 0.75).abs() < f32::EPSILON);
    });
}
