//! Parent relations through the world: events and creates in either order,
//! parent incarnations replaced under a waiting relation, and the children
//! list following moves, pickups and deletes.
//!
//! Sources: OpenAC `tests/AcDream.Runtime.Tests/Entities/ParentAttachmentStateTests.cs`
//! (`ParentEventBeforeChildCreateResolvesToCanonicalAttachment`,
//! `FutureRelationSurvivesAtomicParentGenerationReplacement`) and the
//! wiring of `src/AcDream.Runtime/Session/RuntimeLiveEntitySessionController.cs`
//! (`OnParentUpdated`, `RetryChildrenWaitingForParent`) and
//! `src/AcDream.App/Rendering/EquippedChildRenderController.cs`
//! (`AcceptLateBoundCreateObjectRelation`, `AdmitRelationsAwaitingParent`).

use super::tests::{position, stamps};
use super::*;
use ac_proto::{ModelData, PhysicsAttachment, PhysicsTimestamps as WireStamps};

const CELL: u32 = 0x0101_0001;

fn spawn(guid: u32, instance: u16, position_sequence: u16) -> Inbound<'static> {
    super::tests::create(
        guid,
        stamps(instance, position_sequence),
        Some(position(CELL, 10.0)),
    )
}

fn held_by(guid: u32, ts: WireStamps, parent: u32, location: u32) -> Inbound<'static> {
    let mut physics = super::tests::physics(ts, None);
    physics.parent = Some(PhysicsAttachment {
        guid: parent,
        location_id: location,
    });
    physics.animation_frame = Some(0x65);
    Inbound::CreateObject(Box::new(CreateObject {
        guid,
        model: ModelData::default(),
        physics,
        weenie: None,
    }))
}

fn parent_event(parent: u32, child: u32, parent_instance: u16, position: u16) -> Inbound<'static> {
    Inbound::ParentEvent(ParentEvent {
        parent_guid: parent,
        child_guid: child,
        parent_location: 1,
        placement_id: 2,
        parent_instance_sequence: parent_instance,
        child_position_sequence: position,
    })
}

#[test]
fn a_parent_event_before_the_childs_create_attaches_it_on_arrival() {
    // ParentEventBeforeChildCreateResolvesToCanonicalAttachment.
    let (parent, child) = (0x7000_0100, 0x7000_0101);
    let mut world = World::new();
    world.apply(&spawn(parent, 9, 1));
    world.apply(&parent_event(parent, child, 9, 5));
    assert!(world.object(child).is_none());
    assert_eq!(world.attachments().unresolved_count(), 1);

    world.apply(&spawn(child, 3, 4));

    let held = world.object(child).unwrap();
    assert_eq!(held.parent().map(|p| p.guid), Some(parent));
    assert!(held.position().is_none());
    assert_eq!(held.timestamps().position, 5);
    assert_eq!(
        world.attachments().committed_parent(child),
        Some((parent, 9))
    );
    assert_eq!(world.attachments().children_attached_to(parent, 9), [child]);
}

#[test]
fn a_parent_event_before_the_parents_create_waits_for_it() {
    let (parent, child) = (0x7000_0102, 0x7000_0103);
    let mut world = World::new();
    world.apply(&spawn(child, 3, 4));
    world.apply(&parent_event(parent, child, 9, 5));
    assert!(world.object(child).unwrap().parent().is_none());

    world.apply(&spawn(parent, 9, 1));

    assert_eq!(
        world.object(child).unwrap().parent().map(|p| p.guid),
        Some(parent)
    );
    assert_eq!(world.attachments().children_attached_to(parent, 9), [child]);
}

#[test]
fn a_future_relation_survives_the_parents_generation_replacement() {
    // FutureRelationSurvivesAtomicParentGenerationReplacement.
    let (parent, child) = (0x7000_0150, 0x7000_0151);
    let mut world = World::new();
    world.apply(&spawn(parent, 9, 1));
    // The child is created hanging from the parent.
    world.apply(&held_by(child, stamps(3, 4), parent, 1));
    assert_eq!(
        world.attachments().committed_parent(child),
        Some((parent, 9))
    );
    // A relation about the parent's next incarnation waits.
    world.apply(&parent_event(parent, child, 10, 5));
    assert_eq!(world.object(child).unwrap().timestamps().position, 4);

    world.apply(&spawn(parent, 10, 1));

    let held = world.object(child).unwrap();
    assert_eq!(held.parent().map(|p| p.guid), Some(parent));
    assert_eq!(held.timestamps().position, 5);
    assert_eq!(
        world.attachments().committed_parent(child),
        Some((parent, 10))
    );
    assert!(
        world
            .attachments()
            .children_attached_to(parent, 9)
            .is_empty()
    );
    assert_eq!(
        world.attachments().children_attached_to(parent, 10),
        [child]
    );
}

#[test]
fn a_stale_parent_incarnation_is_dropped() {
    let (parent, child) = (0x7000_0104, 0x7000_0105);
    let mut world = World::new();
    world.apply(&spawn(parent, 9, 1));
    world.apply(&spawn(child, 3, 4));
    world.apply(&parent_event(parent, child, 8, 5));
    assert!(world.object(child).unwrap().parent().is_none());
    assert_eq!(world.attachments().unresolved_count(), 0);
}

#[test]
fn a_create_held_by_an_unknown_parent_binds_to_it_when_it_appears() {
    // AcceptLateBoundCreateObjectRelation / AdmitRelationsAwaitingParent:
    // the relation binds to the incarnation that appears.
    let (parent, child) = (0x7000_0110, 0x7000_0111);
    let mut world = World::new();
    world.apply(&held_by(child, stamps(1, 1), parent, 1));
    assert_eq!(world.attachments().awaiting_parent_count(), 1);
    assert_eq!(
        world.object(child).unwrap().parent().map(|p| p.guid),
        Some(parent)
    );

    world.apply(&spawn(parent, 7, 1));

    assert_eq!(world.attachments().awaiting_parent_count(), 0);
    assert_eq!(
        world.attachments().committed_parent(child),
        Some((parent, 7))
    );
    assert_eq!(world.attachments().children_attached_to(parent, 7), [child]);
}

#[test]
fn wield_and_unwield_follow_the_children_list() {
    // A creature wields a sword (parent event), then drops it (a position
    // in the world), wields it again, and it goes into a pack (pickup).
    let (creature, sword) = (0x5000_0200, 0x7000_0201);
    let mut world = World::new();
    world.apply(&spawn(creature, 1, 1));
    world.apply(&spawn(sword, 1, 1));

    world.apply(&parent_event(creature, sword, 1, 2));
    assert_eq!(
        world.attachments().children_attached_to(creature, 1),
        [sword]
    );
    assert!(world.object(sword).unwrap().position().is_none());

    world.apply(&Inbound::UpdatePosition(UpdatePosition {
        guid: sword,
        flags: 0,
        position: position(CELL, 12.0),
        velocity: None,
        placement_id: None,
        instance_sequence: 1,
        position_sequence: 3,
        teleport_sequence: 0,
        force_position_sequence: 0,
    }));
    assert!(
        world
            .attachments()
            .children_attached_to(creature, 1)
            .is_empty()
    );
    assert!(world.object(sword).unwrap().parent().is_none());

    world.apply(&parent_event(creature, sword, 1, 4));
    assert_eq!(
        world.attachments().children_attached_to(creature, 1),
        [sword]
    );
    world.apply(&Inbound::PickupEvent(PickupEvent {
        guid: sword,
        instance_sequence: 1,
        position_sequence: 5,
    }));
    assert!(!world.attachments().has_committed_parent(sword));
    assert!(world.object(sword).unwrap().parent().is_none());
}

#[test]
fn deleting_the_parent_lets_its_children_go() {
    let (creature, sword, shield) = (0x5000_0210, 0x7000_0211, 0x7000_0212);
    let mut world = World::new();
    world.apply(&spawn(creature, 4, 1));
    world.apply(&held_by(sword, stamps(1, 1), creature, 1));
    world.apply(&held_by(shield, stamps(1, 1), creature, 2));
    assert_eq!(
        world.attachments().children_attached_to(creature, 4),
        [sword, shield]
    );

    world.apply(&Inbound::DeleteObject(ac_proto::DeleteObject {
        guid: creature,
        instance_sequence: 4,
    }));

    assert!(
        world
            .attachments()
            .children_attached_to(creature, 4)
            .is_empty()
    );
    assert_eq!(world.attachments().committed_count(), 0);
}

#[test]
fn a_new_session_generation_forgets_every_relation() {
    let (parent, child) = (0x7000_0220, 0x7000_0221);
    let mut world = World::new();
    world.apply(&spawn(parent, 1, 1));
    world.apply(&held_by(child, stamps(1, 1), parent, 1));
    world.apply(&parent_event(0x7000_0229, child, 1, 2));
    world.reset_session();
    assert_eq!(world.attachments().committed_count(), 0);
    assert_eq!(world.attachments().unresolved_count(), 0);
}
