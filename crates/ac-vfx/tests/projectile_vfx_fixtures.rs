//! The projectile effects' dat fixtures: a blocking particle hook keeps its
//! emitter, part, offset and logical id, and the hook after it is read from
//! the right place, in a physics script and in an animation frame.
//!
//! OpenAC `tests/AcDream.Content.Tests/Vfx/ProjectileVfxDatFixtures.cs`
//! (the fixtures and `ProjectileVfxDatFixtureTests`) and the fixture cases
//! of `RetailDatLoaderTests` (`PhysicsScript_BlockingHookRetainsPayloadAndFollowingCursor`,
//! `Animation_BlockingHookRetainsPayloadAndFollowingCursor`,
//! `OrdinaryPhysicsScript_MatchesPackageDecoder`,
//! `OrdinaryAnimation_MatchesPackageDecoder`,
//! `PhysicsScript_SortsHooksByRetailStartTimeAfterDecode`).

use ac_bits::Reader;
use ac_dat::DatObject;
use ac_dat::objects::{Animation, HookData};
use ac_dat::types::{Quaternion, Vector3};
use ac_vfx::parse_physics_script;

#[derive(Default)]
struct Bytes(Vec<u8>);

impl Bytes {
    fn u32(mut self, v: u32) -> Self {
        self.0.extend_from_slice(&v.to_le_bytes());
        self
    }
    fn f32(mut self, v: f32) -> Self {
        self.0.extend_from_slice(&v.to_le_bytes());
        self
    }
    fn f64(mut self, v: f64) -> Self {
        self.0.extend_from_slice(&v.to_le_bytes());
        self
    }
}

/// A physics script: a blocking particle hook at 0 s (root attachment,
/// offset (1, 2, 3), rotation WXYZ (0.5, 0.5, -0.5, -0.5), logical id 7),
/// then an animation-done hook at `second_start` seconds.
fn blocking_then_done_script(first_start: f64) -> Vec<u8> {
    let b = Bytes::default()
        .u32(0x3300_F001)
        .u32(2)
        .f64(first_start)
        .u32(0x1A)
        .u32(0)
        .u32(0x3200_F001)
        .u32(0xFFFF_FFFF)
        .f32(1.0)
        .f32(2.0)
        .f32(3.0)
        .f32(0.5)
        .f32(0.5)
        .f32(-0.5)
        .f32(-0.5)
        .u32(7)
        .f64(1.25)
        .u32(0x04)
        .u32(1);
    assert_eq!(b.0.len(), 80);
    b.0
}

/// An animation with one frame and no parts: a blocking particle hook on
/// part 0 (offset (-1, 4, 0.25), no turn, logical id 9) then an
/// animation-done hook running backward.
fn blocking_then_done_animation() -> Vec<u8> {
    let b = Bytes::default()
        .u32(0x0300_F001)
        .u32(0)
        .u32(0)
        .u32(1)
        .u32(2)
        .u32(0x1A)
        .u32(0)
        .u32(0x3200_F002)
        .u32(0)
        .f32(-1.0)
        .f32(4.0)
        .f32(0.25)
        .f32(1.0)
        .f32(0.0)
        .f32(0.0)
        .f32(0.0)
        .u32(9)
        .u32(0x04)
        .u32(0xFFFF_FFFF);
    assert_eq!(b.0.len(), 76);
    b.0
}

#[test]
fn a_physics_scripts_blocking_hook_keeps_its_payload_and_the_next_hook_follows() {
    let script = parse_physics_script(0x3300_F001, &blocking_then_done_script(0.0)).unwrap();
    assert_eq!(script.id, 0x3300_F001);
    assert_eq!(script.hooks.len(), 2);
    assert_eq!(script.hooks[0].0, 0.0);
    let HookData::CreateBlockingParticle {
        emitter_info_id,
        part_index,
        offset,
        emitter_id,
    } = script.hooks[0].1.data
    else {
        panic!("{:?}", script.hooks[0].1);
    };
    assert_eq!(emitter_info_id, 0x3200_F001);
    assert_eq!(part_index, 0xFFFF_FFFF);
    assert_eq!(
        offset.origin,
        Vector3 {
            x: 1.0,
            y: 2.0,
            z: 3.0
        }
    );
    assert_eq!(
        offset.orientation,
        Quaternion {
            w: 0.5,
            x: 0.5,
            y: -0.5,
            z: -0.5
        }
    );
    assert_eq!(emitter_id, 7);
    assert_eq!(script.hooks[1].0, 1.25);
    assert!(matches!(script.hooks[1].1.data, HookData::AnimationDone));
    assert_eq!(script.hooks[1].1.direction, 1);
}

#[test]
fn an_animation_frames_blocking_hook_keeps_its_payload_and_the_next_hook_follows() {
    let bytes = blocking_then_done_animation();
    let mut r = Reader::new(&bytes);
    let animation = Animation::unpack(&mut r).unwrap();
    assert_eq!(r.remaining(), 0);
    assert_eq!(animation.id, 0x0300_F001);
    assert_eq!(animation.part_frames.len(), 1);
    let hooks = &animation.part_frames[0].hooks;
    assert_eq!(hooks.len(), 2);
    let HookData::CreateBlockingParticle {
        emitter_info_id,
        part_index,
        offset,
        emitter_id,
    } = hooks[0].data
    else {
        panic!("{:?}", hooks[0]);
    };
    assert_eq!(emitter_info_id, 0x3200_F002);
    assert_eq!(part_index, 0);
    assert_eq!(
        offset.origin,
        Vector3 {
            x: -1.0,
            y: 4.0,
            z: 0.25
        }
    );
    assert_eq!(
        offset.orientation,
        Quaternion {
            w: 1.0,
            x: 0.0,
            y: 0.0,
            z: 0.0
        }
    );
    assert_eq!(emitter_id, 9);
    assert!(matches!(hooks[1].data, HookData::AnimationDone));
    assert_eq!(hooks[1].direction, 0xFFFF_FFFF);
}

#[test]
fn ordinary_scripts_and_animations_read_to_their_last_byte() {
    let script = Bytes::default()
        .u32(0x3300_F002)
        .u32(1)
        .f64(2.5)
        .u32(0x04)
        .u32(1)
        .0;
    let loaded = parse_physics_script(0x3300_F002, &script).unwrap();
    assert_eq!(loaded.hooks.len(), 1);
    assert_eq!(loaded.hooks[0].0, 2.5);
    assert!(matches!(loaded.hooks[0].1.data, HookData::AnimationDone));

    let animation = Bytes::default()
        .u32(0x0300_F002)
        .u32(0)
        .u32(0)
        .u32(1)
        .u32(1)
        .u32(0x04)
        .u32(1)
        .0;
    let mut r = Reader::new(&animation);
    let a = Animation::unpack(&mut r).unwrap();
    assert_eq!(r.remaining(), 0);
    assert_eq!(a.num_parts, 0);
    assert_eq!(a.part_frames.len(), 1);
    assert!(matches!(
        a.part_frames[0].hooks[0].data,
        HookData::AnimationDone
    ));
}

#[test]
fn a_physics_scripts_hooks_run_in_start_time_order_after_reading() {
    let script = parse_physics_script(0x3300_F001, &blocking_then_done_script(2.0)).unwrap();
    assert_eq!(script.hooks[0].0, 1.25);
    assert!(matches!(script.hooks[0].1.data, HookData::AnimationDone));
    assert_eq!(script.hooks[1].0, 2.0);
    assert!(matches!(
        script.hooks[1].1.data,
        HookData::CreateBlockingParticle { .. }
    ));
}
