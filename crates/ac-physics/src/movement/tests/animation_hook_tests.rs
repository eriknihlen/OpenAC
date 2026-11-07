//! The animation hooks that change the object itself, carried out by the
//! session's own simulation: an ethereal hook turns the body ethereal or
//! solid, and turning solid waits while another object overlaps the body
//! (retail's ethereal hook and its overlap wait).

use std::cell::Cell;
use std::sync::Arc;

use ac_dat::objects::{AnimationHook, HookData};
use glam::{Quat, Vec3};

use super::motion_fixtures::{MapLoader, TableBuilder, rest_anim};
use crate::CellPosition;
use crate::movement::{ObjectToken, RemoteMotionDriver, TargetWorld};
use crate::{PhysicsState, TransientState};

const DEFAULT_ANIMATION: u32 = 0x0300_0001;
const CELL: u32 = 0xA9B4_0001;
const AT: Vec3 = Vec3::new(12.0, 12.0, 0.0);

/// A world whose answer to "does another object overlap it" is set by the
/// test, counting the questions.
#[derive(Debug, Default)]
struct OverlapWorld {
    overlapped: Cell<bool>,
    asked: Cell<u32>,
    asked_state: Cell<Option<PhysicsState>>,
}

impl TargetWorld for OverlapWorld {
    fn resolve(&self, _id: u32) -> Option<ObjectToken> {
        None
    }
    fn position_of(&self, _token: ObjectToken) -> Option<CellPosition> {
        None
    }
    fn overlapped_by_another(&self, _token: ObjectToken, state: PhysicsState) -> bool {
        self.asked.set(self.asked.get() + 1);
        self.asked_state.set(Some(state));
        self.overlapped.get()
    }
}

/// A body playing a long default animation with `data` on keyframe 1.
fn body_with_hook(data: HookData) -> RemoteMotionDriver {
    let mut driver = RemoteMotionDriver::new(
        ObjectToken {
            id: 0x7000_0001,
            incarnation: 1,
        },
        CELL,
        AT,
        Quat::IDENTITY,
    );
    driver.obj.body.state = PhysicsState::REPORT_COLLISIONS;
    let mut anim = rest_anim(300, 1);
    anim.part_frames[1]
        .hooks
        .push(AnimationHook { direction: 0, data });
    let mut loader = MapLoader::default();
    loader.add(DEFAULT_ANIMATION, anim);
    driver.attach_sequencer(ac_motion::AnimationSequencer::new(
        1,
        DEFAULT_ANIMATION,
        TableBuilder::new(0x8000_003D).rules(),
        Arc::new(loader),
    ));
    driver
}

fn tick(driver: &mut RemoteMotionDriver, world: &dyn TargetWorld) {
    driver.advance(None, world, 0.1, 1.0, Some(AT), true, true);
}

fn ethereal(driver: &RemoteMotionDriver) -> bool {
    driver.obj.body.state.contains(PhysicsState::ETHEREAL)
}

fn checking(driver: &RemoteMotionDriver) -> bool {
    driver
        .obj
        .body
        .transient_state
        .contains(TransientState::CHECK_ETHEREAL)
}

#[test]
fn an_ethereal_hook_turns_the_body_ethereal() {
    let world = OverlapWorld::default();
    let mut d = body_with_hook(HookData::Ethereal { ethereal: true });
    for _ in 0..3 {
        tick(&mut d, &world);
    }
    assert!(ethereal(&d));
    assert!(!checking(&d));
    // Turning ethereal asks nothing of the world.
    assert_eq!(world.asked.get(), 0);
}

#[test]
fn a_solid_hook_turns_an_ethereal_body_solid_when_nothing_overlaps_it() {
    let world = OverlapWorld::default();
    let mut d = body_with_hook(HookData::Ethereal { ethereal: false });
    d.obj.body.state.insert(PhysicsState::ETHEREAL);
    for _ in 0..3 {
        tick(&mut d, &world);
    }
    assert!(!ethereal(&d));
    assert!(!checking(&d));
    assert_eq!(world.asked.get(), 1);
    // The overlap is tested as the body would be once solid.
    let asked = world.asked_state.get().unwrap();
    assert!(!asked.contains(PhysicsState::ETHEREAL));
    assert!(asked.contains(PhysicsState::REPORT_COLLISIONS));
}

#[test]
fn turning_solid_waits_while_another_object_overlaps_and_retries_each_update() {
    let world = OverlapWorld::default();
    world.overlapped.set(true);
    let mut d = body_with_hook(HookData::Ethereal { ethereal: false });
    d.obj.body.state.insert(PhysicsState::ETHEREAL);
    for _ in 0..3 {
        tick(&mut d, &world);
    }
    assert!(ethereal(&d), "still ethereal while overlapped");
    assert!(checking(&d), "marked to try again");
    let asked = world.asked.get();
    tick(&mut d, &world);
    assert!(ethereal(&d));
    assert!(world.asked.get() > asked, "tried again on the next update");

    world.overlapped.set(false);
    tick(&mut d, &world);
    assert!(!ethereal(&d), "solid once nothing overlaps");
    assert!(!checking(&d));
    let settled = world.asked.get();
    tick(&mut d, &world);
    assert_eq!(world.asked.get(), settled, "no more tries once solid");
}

#[test]
fn a_body_in_no_cell_turns_solid_without_asking() {
    let world = OverlapWorld::default();
    world.overlapped.set(true);
    let mut d = body_with_hook(HookData::Ethereal { ethereal: false });
    d.obj.cell_id = 0;
    d.obj.body.state.insert(PhysicsState::ETHEREAL);
    for _ in 0..3 {
        tick(&mut d, &world);
    }
    assert!(!ethereal(&d));
    assert_eq!(world.asked.get(), 0);
}

/// The yaw of `q`, radians (turned about the up axis).
fn yaw(q: Quat) -> f32 {
    let forward = q * Vec3::X;
    forward.y.atan2(forward.x)
}

#[test]
fn a_spin_hook_turns_a_server_object_by_its_spin_each_second() {
    // A swarm's spin: radians per second about the up axis.
    const SPIN: f32 = -0.026_797_784;
    let world = OverlapWorld::default();
    let mut d = body_with_hook(HookData::SetOmega {
        axis: ac_dat::types::Vector3 {
            x: 0.0,
            y: 0.0,
            z: SPIN,
        },
    });
    // Off the ground (a floating swarm): nothing holds its spin at zero.
    d.obj.body.transient_state = TransientState::ACTIVE;
    d.airborne = true;
    for _ in 0..3 {
        tick(&mut d, &world);
    }
    assert_eq!(d.obj.body.omega, Vec3::new(0.0, 0.0, SPIN));
    let before = yaw(d.obj.body.orientation);
    for _ in 0..10 {
        tick(&mut d, &world);
    }
    let turned = yaw(d.obj.body.orientation) - before;
    assert!(
        (turned - SPIN * 1.0).abs() < 1e-4,
        "one second turned it {turned} radians"
    );
}
