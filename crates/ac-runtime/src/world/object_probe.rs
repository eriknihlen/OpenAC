//! The object probe (`OPENAC_PROBE_OBJECTS=1`), to find out why the client
//! lacks an object the server should have sent, or holds one the server
//! thinks gone. One `[objects]` line per object placed in the world that is
//! created, created again, described again, deleted by the server, held for
//! a create that has not come, or forgotten by the client (25 seconds out of
//! range); and one when the session's world is emptied. Each line names the
//! object, where it stands, where the player stands and how far apart the
//! two are, with the wall-clock stamp the motion log uses, so a line can be
//! matched against another client's log.
//!
//! Objects in a container or held by a parent or wielder, and projectiles in
//! flight, are left out: they come and go by the hundred and are not what
//! goes missing from the landscape. A held delete names an object the client
//! does not hold, so it is told whatever the object is. An object that
//! leaves or enters the world without a create or delete (picked up, put
//! down) has no line.

use ac_proto::Position;

/// The size of a landblock, metres: places in two landblocks are compared
/// on one map with it.
const LANDBLOCK_METRES: f32 = 192.0;

/// What happened to an object.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ObjectEvent {
    /// A create for an object the client did not hold.
    Created,
    /// A create for a newer incarnation of an object held.
    CreatedAgain,
    /// A create for the incarnation held: its parts are taken again.
    Resent,
    /// A create for an older incarnation than the one held: ignored.
    StaleCreate,
    /// A full description sent again (an update-object message).
    Described,
    /// A server delete took the object away.
    Deleted,
    /// A server delete for another incarnation than the one held: ignored.
    DeleteIgnored,
    /// A server delete for an object the client does not hold: kept for
    /// its create.
    DeleteHeld,
    /// The client forgot the object on its own clock: 25 seconds out of
    /// range (or, for something on the destruction queue, 25 seconds after
    /// it was queued).
    Forgotten,
}

impl ObjectEvent {
    fn label(self) -> &'static str {
        match self {
            Self::Created => "created",
            Self::CreatedAgain => "created again",
            Self::Resent => "resent",
            Self::StaleCreate => "stale create ignored",
            Self::Described => "described again",
            Self::Deleted => "deleted by the server",
            Self::DeleteIgnored => "delete for another incarnation ignored",
            Self::DeleteHeld => "delete held for a create",
            Self::Forgotten => "forgotten by the client",
        }
    }
}

/// One line's facts.
#[derive(Debug, Clone, PartialEq)]
pub struct ObjectLine<'a> {
    /// What happened.
    pub event: ObjectEvent,
    /// The object's server id.
    pub guid: u32,
    /// Its name, when described.
    pub name: Option<&'a str>,
    /// The incarnation the message or the client named.
    pub incarnation: u16,
    /// Where the object stands, when known.
    pub at: Option<&'a Position>,
    /// Where the player stands, when known.
    pub player: Option<&'a Position>,
}

/// The probe: off until switched on; its lines wait to be taken.
#[derive(Debug, Default)]
pub struct ObjectProbe {
    on: bool,
    lines: Vec<String>,
}

impl ObjectProbe {
    /// Switch the probe on.
    pub fn enable(&mut self) {
        self.on = true;
    }

    /// Whether the probe is on.
    #[must_use]
    pub fn is_on(&self) -> bool {
        self.on
    }

    /// Keep a line about an object (nothing while off).
    pub fn object(&mut self, line: &ObjectLine<'_>) {
        if self.on {
            self.lines
                .push(format_object_line(&crate::world::motion_log_stamp(), line));
        }
    }

    /// Keep the line for the session's world emptied with `objects` in it
    /// (nothing while off).
    pub fn emptied(&mut self, objects: usize) {
        if self.on {
            self.lines.push(format!(
                "[objects] {} world emptied with {objects} objects",
                crate::world::motion_log_stamp()
            ));
        }
    }

    /// Take the lines kept since the last call, oldest first.
    pub fn take_lines(&mut self) -> Vec<String> {
        std::mem::take(&mut self.lines)
    }
}

/// The `[objects]` line: the stamp, what happened, the object (id, name,
/// incarnation), its cell, the player's cell and the distance between the
/// two in metres along the ground.
#[must_use]
pub fn format_object_line(stamp: &str, line: &ObjectLine<'_>) -> String {
    let name = line.name.map_or_else(String::new, |n| format!(" \"{n}\""));
    let at = line
        .at
        .map_or_else(|| "nowhere".to_owned(), |p| format!("0x{:08X}", p.cell_id));
    let player = line
        .player
        .map_or_else(|| "unknown".to_owned(), |p| format!("0x{:08X}", p.cell_id));
    let apart = match (line.at, line.player) {
        (Some(a), Some(b)) => format!(" {:.0} m apart", ground_distance(a, b)),
        _ => String::new(),
    };
    format!(
        "[objects] {stamp} {} 0x{:08X}{name} inst={} at {at}, player at {player}{apart}",
        line.event.label(),
        line.guid,
        line.incarnation,
    )
}

/// The distance between two places along the ground, metres: each place's
/// landblock corner plus its offset in it.
#[must_use]
pub fn ground_distance(a: &Position, b: &Position) -> f32 {
    let on_map = |p: &Position| {
        let x = ((p.cell_id >> 24) & 0xFF) as f32 * LANDBLOCK_METRES + p.origin.x;
        let y = ((p.cell_id >> 16) & 0xFF) as f32 * LANDBLOCK_METRES + p.origin.y;
        (x, y)
    };
    let (ax, ay) = on_map(a);
    let (bx, by) = on_map(b);
    (ax - bx).hypot(ay - by)
}

#[cfg(test)]
mod tests {
    use super::*;
    use ac_proto::{Quaternion, Vector3};

    fn at(cell: u32, x: f32, y: f32) -> Position {
        Position {
            cell_id: cell,
            origin: Vector3 { x, y, z: 0.0 },
            orientation: Quaternion::IDENTITY,
        }
    }

    #[test]
    fn a_line_names_the_object_both_places_and_the_distance() {
        let statue = at(0xB349_0012, 100.0, 50.0);
        let player = at(0xB348_002A, 100.0, 170.0);
        let line = format_object_line(
            "t=1.000",
            &ObjectLine {
                event: ObjectEvent::Forgotten,
                guid: 0x7B34_9078,
                name: Some("Ancient Statue"),
                incarnation: 1,
                at: Some(&statue),
                player: Some(&player),
            },
        );
        // One landblock north (192 m) less the 120 m between the offsets.
        assert_eq!(
            line,
            "[objects] t=1.000 forgotten by the client 0x7B349078 \"Ancient Statue\" \
             inst=1 at 0xB3490012, player at 0xB348002A 72 m apart"
        );
    }

    #[test]
    fn a_line_without_places_says_so() {
        let line = format_object_line(
            "t=2.500",
            &ObjectLine {
                event: ObjectEvent::DeleteHeld,
                guid: 0x8000_0001,
                name: None,
                incarnation: 3,
                at: None,
                player: None,
            },
        );
        assert_eq!(
            line,
            "[objects] t=2.500 delete held for a create 0x80000001 inst=3 at nowhere, player at unknown"
        );
    }

    #[test]
    fn the_distance_crosses_landblocks_east_and_north() {
        let a = at(0x1020_0001, 190.0, 0.0);
        let b = at(0x1120_0001, 2.0, 0.0);
        assert!((ground_distance(&a, &b) - 4.0).abs() < 1e-3);
        let c = at(0x1021_0001, 190.0, 3.0);
        assert!((ground_distance(&a, &c) - 195.0).abs() < 1e-3);
    }

    #[test]
    fn nothing_is_kept_while_off() {
        let mut probe = ObjectProbe::default();
        probe.object(&ObjectLine {
            event: ObjectEvent::Created,
            guid: 1,
            name: None,
            incarnation: 0,
            at: None,
            player: None,
        });
        probe.emptied(3);
        assert!(probe.take_lines().is_empty());
        probe.enable();
        probe.emptied(3);
        let lines = probe.take_lines();
        assert_eq!(lines.len(), 1);
        assert!(
            lines[0].ends_with("world emptied with 3 objects"),
            "{}",
            lines[0]
        );
        assert!(probe.take_lines().is_empty());
    }
}

#[cfg(test)]
mod world_tests {
    //! The probe as the world drives it.

    use crate::world::World;
    use crate::world::liveness::ExpiryCandidate;
    use crate::world::tests::{create, position, stamps};
    use ac_core::physics::PhysicsState;
    use ac_proto::{DeleteObject, Inbound, PublicWeenieDesc};

    const PLAYER: u32 = 0x5000_000A;
    const STATUE: u32 = 0x7B34_9078;
    const PACK_ITEM: u32 = 0x8000_0100;
    const BOLT: u32 = 0x8000_0200;

    fn named(guid: u32, name: &str, cell: u32, container: Option<u32>) -> Inbound<'static> {
        Inbound::CreateObject(Box::new(described(guid, name, cell, container, 1)))
    }

    fn described(
        guid: u32,
        name: &str,
        cell: u32,
        container: Option<u32>,
        instance: u16,
    ) -> ac_proto::CreateObject {
        let Inbound::CreateObject(mut c) =
            create(guid, stamps(instance, 1), Some(position(cell, 5.0)))
        else {
            unreachable!()
        };
        c.weenie = Some(PublicWeenieDesc {
            name: name.into(),
            container_id: container,
            ..PublicWeenieDesc::default()
        });
        *c
    }

    /// A world with the probe on (unless `on` is false) and the player
    /// standing in landblock 0xB348.
    fn world(on: bool) -> World {
        let mut world = World::new();
        if on {
            world.enable_object_probe();
        }
        world.set_player(PLAYER);
        world.apply(&named(PLAYER, "Kolossos", 0xB348_002A, None));
        world.take_object_probe_lines();
        world
    }

    fn delete(guid: u32, instance: u16) -> Inbound<'static> {
        Inbound::DeleteObject(DeleteObject {
            guid,
            instance_sequence: instance,
        })
    }

    #[test]
    fn a_create_a_server_delete_and_a_create_again_each_leave_a_line() {
        let mut world = world(true);
        world.apply(&named(STATUE, "Ancient Statue", 0xB349_0012, None));
        world.apply(&delete(STATUE, 1));
        world.apply(&named(STATUE, "Ancient Statue", 0xB349_0012, None));
        let lines = world.take_object_probe_lines();
        assert_eq!(lines.len(), 3, "{lines:#?}");
        assert!(
            lines[0].contains(
                "created 0x7B349078 \"Ancient Statue\" inst=1 at 0xB3490012, player at 0xB348002A 192 m apart"
            ),
            "{}",
            lines[0]
        );
        assert!(
            lines[1].contains("deleted by the server 0x7B349078"),
            "{}",
            lines[1]
        );
        assert!(lines[2].contains("created 0x7B349078"), "{}", lines[2]);
    }

    #[test]
    fn the_client_forgetting_an_object_is_told_apart_from_a_server_delete() {
        let mut world = world(true);
        world.apply(&named(STATUE, "Ancient Statue", 0xB349_0012, None));
        world.take_object_probe_lines();
        let key = world.object(STATUE).unwrap().key();
        world.expire(ExpiryCandidate {
            key,
            guid: STATUE,
            generation: key.incarnation,
        });
        world.apply(&delete(0x7000_0001, 1));
        let lines = world.take_object_probe_lines();
        assert!(
            lines[0].contains("forgotten by the client 0x7B349078 \"Ancient Statue\""),
            "{}",
            lines[0]
        );
        // A later server delete is the server's again.
        assert!(
            lines[1].contains("delete held for a create 0x70000001 inst=1 at nowhere"),
            "{}",
            lines[1]
        );
    }

    #[test]
    fn a_resent_create_and_a_delete_for_another_incarnation_are_named() {
        let mut world = world(true);
        world.apply(&named(STATUE, "Ancient Statue", 0xB349_0012, None));
        world.apply(&named(STATUE, "Ancient Statue", 0xB349_0012, None));
        world.apply(&delete(STATUE, 7));
        let lines = world.take_object_probe_lines();
        assert!(lines[1].contains("resent 0x7B349078"), "{}", lines[1]);
        assert!(
            lines[2].contains(
                "delete for another incarnation ignored 0x7B349078 \"Ancient Statue\" inst=7"
            ),
            "{}",
            lines[2]
        );
        assert!(world.object(STATUE).is_some());
    }

    #[test]
    fn a_newer_incarnation_an_older_one_and_a_new_description_are_named() {
        let mut world = world(true);
        let statue = |instance| described(STATUE, "Ancient Statue", 0xB349_0012, None, instance);
        world.apply(&Inbound::CreateObject(Box::new(statue(1))));
        world.apply(&Inbound::CreateObject(Box::new(statue(2))));
        world.apply(&Inbound::CreateObject(Box::new(statue(1))));
        world.apply(&Inbound::UpdateObject(Box::new(statue(2))));
        let lines = world.take_object_probe_lines();
        assert_eq!(lines.len(), 4, "{lines:#?}");
        assert!(
            lines[1].contains("created again 0x7B349078 \"Ancient Statue\" inst=2"),
            "{}",
            lines[1]
        );
        assert!(
            lines[2].contains("stale create ignored 0x7B349078 \"Ancient Statue\" inst=1"),
            "{}",
            lines[2]
        );
        assert!(
            lines[3].contains("described again 0x7B349078 \"Ancient Statue\" inst=2"),
            "{}",
            lines[3]
        );
    }

    #[test]
    fn objects_in_containers_projectiles_and_the_player_are_left_out() {
        let mut world = world(true);
        world.apply(&named(PACK_ITEM, "Mana Stone", 0xB348_002A, Some(PLAYER)));
        let Inbound::CreateObject(mut bolt) =
            create(BOLT, stamps(1, 1), Some(position(0xB348_002A, 9.0)))
        else {
            unreachable!()
        };
        bolt.physics.state = PhysicsState::MISSILE.bits();
        world.apply(&Inbound::CreateObject(bolt));
        world.apply(&delete(PACK_ITEM, 1));
        world.apply(&delete(BOLT, 1));
        world.apply(&named(PLAYER, "Kolossos", 0xB348_002A, None));
        assert!(
            world.take_object_probe_lines().is_empty(),
            "nothing is said about them"
        );
    }

    #[test]
    fn nothing_is_said_while_the_probe_is_off() {
        let mut world = world(false);
        world.apply(&named(STATUE, "Ancient Statue", 0xB349_0012, None));
        world.apply(&delete(STATUE, 1));
        world.apply(&delete(0x7000_0001, 1));
        world.reset_session();
        assert!(world.take_object_probe_lines().is_empty());
    }

    #[test]
    fn an_emptied_world_says_so_and_the_probe_stays_on() {
        let mut world = world(true);
        world.apply(&named(STATUE, "Ancient Statue", 0xB349_0012, None));
        world.take_object_probe_lines();
        world.reset_session();
        world.apply(&named(STATUE, "Ancient Statue", 0xB349_0012, None));
        let lines = world.take_object_probe_lines();
        assert!(
            lines[0].ends_with("world emptied with 2 objects"),
            "{}",
            lines[0]
        );
        // No player any more: the line says so.
        assert!(
            lines[1].contains(
                "created 0x7B349078 \"Ancient Statue\" inst=1 at 0xB3490012, player at unknown"
            ),
            "{}",
            lines[1]
        );
    }
}
