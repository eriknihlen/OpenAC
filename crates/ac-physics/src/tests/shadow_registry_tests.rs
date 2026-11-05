//! Ported from OpenAC `tests/AcDream.Core.Tests/Physics/ShadowObjectRegistryTests.cs`,
//! `ShadowObjectRegistryMultiPartTests.cs`, `ShadowObjectRegistryRetailCellArrayTests.cs`,
//! `ShadowObjectRegistryCellRenderRevisionTests.cs`, `ShadowObjectRegistryCaptureTests.cs`,
//! `ShadowRegistrationOverflowTests.cs`, the direct-commit half of
//! `ShadowSetPositionCommitTests.cs`, `ShadowShapeBuilderTests.cs` and
//! `ShadowShapeBuilderShapeSourceTests.cs`.
//!
//! Not ported, because the pieces they test were left out of this port
//! (deliberate divergences): OpenAC's two-phase prepared set-position
//! commits and their receipts, owner versions and the global mutation
//! counter (the attach/detach revision case uses the cell-array revision),
//! copying a retained owner between registries, the snapshot list type,
//! appending captures into a caller's list, and the null-setup guard.

use ac_dat::objects::{AnimationFrame, Setup, SetupFlags};
use ac_dat::types::{
    CylSphere, Frame as DatFrame, Quaternion as DatQuat, Sphere as DatSphere, Vector3 as DatVec3,
};
use glam::{Quat, Vec2, Vec3};

use super::support::*;
use crate::bsp::{CellBsp, CellBspNode};
use crate::collision_flags::{EntityCollisionFlags, should_skip};
use crate::geometry::{Plane, Sphere};
use crate::polygon::ResolvedPolygon;
use crate::portal::PortalInfo;
use crate::shadow::{
    RetailCellArrayRoute, ShadowCollisionType, ShadowEntry, ShadowPartGeometry, ShadowShape,
};
use crate::shadow_builder::{PartRef, from_landblock_bsp_parts, from_setup};
use crate::shadow_registry::{
    PhysicsShadowCommitAction, RegistrationSite, ShadowObjectRegistry, SingleShape,
};
use crate::state::ObjectInfoState;
use crate::world::{CellPhysics, CollisionWorld, GfxObjPhysics, Placement};

const LB: u32 = 0xA9B4_0000;
const CELL_A: u32 = LB | 1;
const CELL_B: u32 = LB | 9;

/// One `ShadowObjectRegistry.Register` call, with OpenAC's defaults.
#[derive(Clone, Copy)]
struct One {
    gfx: u32,
    pos: Vec3,
    radius: f32,
    off: (f32, f32),
    lb: u32,
    ty: ShadowCollisionType,
    cyl: f32,
    state: u32,
    flags: u8,
    seed: u32,
    is_static: bool,
}

impl Default for One {
    fn default() -> Self {
        Self {
            gfx: 0,
            pos: Vec3::ZERO,
            radius: 1.0,
            off: (0.0, 0.0),
            lb: LB,
            ty: ShadowCollisionType::Bsp,
            cyl: 0.0,
            state: 0,
            flags: 0,
            seed: 0,
            is_static: true,
        }
    }
}

fn site_at(pos: Vec3, rot: Quat, off: (f32, f32), lb: u32, seed: u32) -> RegistrationSite {
    RegistrationSite {
        world_pos: pos,
        rotation: rot,
        world_offset_x: off.0,
        world_offset_y: off.1,
        landblock_id: lb,
        seed_cell_id: seed,
    }
}

fn site(pos: Vec3, seed: u32) -> RegistrationSite {
    site_at(pos, Quat::IDENTITY, (0.0, 0.0), LB, seed)
}

fn reg_one(reg: &mut ShadowObjectRegistry, world: &CollisionWorld, id: u32, o: One) {
    reg.register(
        world,
        id,
        &SingleShape {
            gfx_obj_id: o.gfx,
            radius: o.radius,
            collision_type: o.ty,
            cyl_height: o.cyl,
            scale: 1.0,
        },
        site_at(o.pos, Quat::IDENTITY, o.off, o.lb, o.seed),
        o.state,
        EntityCollisionFlags(o.flags),
        o.is_static,
        None,
    )
    .unwrap();
}

#[allow(clippy::too_many_arguments)]
fn reg_multi(
    reg: &mut ShadowObjectRegistry,
    world: &CollisionWorld,
    id: u32,
    pos: Vec3,
    shapes: &[ShadowShape],
    state: u32,
    seed: u32,
    is_static: bool,
    part_array: Option<&[ShadowShape]>,
) {
    reg.register_multi_part(
        world,
        id,
        shapes,
        site(pos, seed),
        state,
        EntityCollisionFlags::default(),
        is_static,
        part_array,
    )
    .unwrap();
}

fn move_to(reg: &mut ShadowObjectRegistry, world: &CollisionWorld, id: u32, pos: Vec3, seed: u32) {
    reg.update_position(world, id, site(pos, seed)).unwrap();
}

fn holds(reg: &ShadowObjectRegistry, cell: u32, id: u32) -> bool {
    reg.objects_in_cell(cell).iter().any(|e| e.entity_id == id)
}

fn rows_of(reg: &ShadowObjectRegistry, cell: u32, id: u32) -> Vec<ShadowEntry> {
    reg.objects_in_cell(cell)
        .iter()
        .filter(|e| e.entity_id == id)
        .copied()
        .collect()
}

fn bsp_shape(gfx: u32, local: Vec3, center: Vec3, radius: f32) -> ShadowShape {
    ShadowShape::bsp(
        gfx,
        local,
        Quat::IDENTITY,
        1.0,
        ShadowPartGeometry::new(Sphere::new(center, radius), None),
    )
}

fn cyl_shape(gfx: u32, local: Vec3, radius: f32, height: f32) -> ShadowShape {
    ShadowShape::cylinder(gfx, local, Quat::IDENTITY, 1.0, radius, height)
}

fn p(x: f32, y: f32) -> Vec3 {
    Vec3::new(x, y, 50.0)
}

// ── ShadowObjectRegistryTests ──────────────────────────────────────────

#[test]
fn register_single_entity_total_registered_is_one() {
    let (mut reg, w) = (ShadowObjectRegistry::new(), CollisionWorld::new());
    reg_one(
        &mut reg,
        &w,
        1,
        One {
            gfx: 0x0100_0001,
            pos: p(12.0, 12.0),
            ..One::default()
        },
    );
    assert_eq!(reg.total_registered(), 1);
}

#[test]
fn register_same_entity_twice_no_duplicate() {
    let (mut reg, w) = (ShadowObjectRegistry::new(), CollisionWorld::new());
    reg_one(
        &mut reg,
        &w,
        1,
        One {
            pos: p(12.0, 12.0),
            ..One::default()
        },
    );
    reg_one(
        &mut reg,
        &w,
        1,
        One {
            pos: p(13.0, 12.0),
            ..One::default()
        },
    );
    assert_eq!(reg.total_registered(), 1);
}

#[test]
fn register_corner_landblock_derives_real_outdoor_seed() {
    let (mut reg, w) = (ShadowObjectRegistry::new(), CollisionWorld::new());
    reg_one(
        &mut reg,
        &w,
        1,
        One {
            pos: p(12.0, 12.0),
            lb: 0x0000_FFFF,
            ..One::default()
        },
    );
    assert_eq!(reg.total_registered(), 1);
    assert!(holds(&reg, 0x0000_0001, 1));
}

#[test]
fn register_absent_landblock_id_registers_nothing() {
    let (mut reg, w) = (ShadowObjectRegistry::new(), CollisionWorld::new());
    reg_one(
        &mut reg,
        &w,
        1,
        One {
            pos: p(12.0, 12.0),
            lb: 0,
            ..One::default()
        },
    );
    assert_eq!(reg.total_registered(), 0);
}

#[test]
fn objects_in_cell_entity_in_center_in_expected_cell() {
    let (mut reg, w) = (ShadowObjectRegistry::new(), CollisionWorld::new());
    reg_one(
        &mut reg,
        &w,
        42,
        One {
            pos: p(12.0, 12.0),
            ..One::default()
        },
    );
    let objs = reg.objects_in_cell(CELL_A);
    assert_eq!(objs.len(), 1);
    assert_eq!(objs[0].entity_id, 42);
}

#[test]
fn objects_in_cell_entity_spanning_two_cells_in_both() {
    let (mut reg, w) = (ShadowObjectRegistry::new(), CollisionWorld::new());
    reg_one(
        &mut reg,
        &w,
        7,
        One {
            pos: p(24.0, 12.0),
            radius: 2.0,
            ..One::default()
        },
    );
    assert!(holds(&reg, CELL_A, 7));
    assert!(holds(&reg, CELL_B, 7));
}

#[test]
fn deregister_removes_from_all_cells() {
    let (mut reg, w) = (ShadowObjectRegistry::new(), CollisionWorld::new());
    reg_one(
        &mut reg,
        &w,
        5,
        One {
            pos: p(24.0, 12.0),
            radius: 2.0,
            ..One::default()
        },
    );
    reg.deregister(5);
    assert_eq!(reg.total_registered(), 0);
    assert!(reg.objects_in_cell(CELL_A).is_empty());
    assert!(reg.objects_in_cell(CELL_B).is_empty());
}

#[test]
fn deregister_nonexistent_entity_is_harmless() {
    ShadowObjectRegistry::new().deregister(999);
}

#[test]
fn suspend_withdraws_cells_and_update_position_restores() {
    let (mut reg, w) = (ShadowObjectRegistry::new(), CollisionWorld::new());
    reg_one(
        &mut reg,
        &w,
        23,
        One {
            pos: p(12.0, 12.0),
            ..One::default()
        },
    );
    assert!(reg.suspend(23));
    assert!(reg.objects_in_cell(CELL_A).is_empty());
    assert_eq!(reg.total_registered(), 0);

    move_to(&mut reg, &w, 23, p(36.0, 12.0), CELL_B);
    let rows = reg.objects_in_cell(CELL_B);
    assert_eq!(rows.len(), 1);
    assert_eq!(rows[0].entity_id, 23);
    assert_eq!(rows[0].position, p(36.0, 12.0));
    assert_eq!(reg.total_registered(), 1);
}

#[test]
fn suspend_state_change_updates_retained_registration_before_restore() {
    let (mut reg, w) = (ShadowObjectRegistry::new(), CollisionWorld::new());
    reg_one(
        &mut reg,
        &w,
        24,
        One {
            pos: p(12.0, 12.0),
            state: 0x40,
            seed: CELL_A,
            is_static: false,
            ..One::default()
        },
    );
    assert!(reg.suspend(24));
    reg.update_physics_state(24, 0x44);
    move_to(&mut reg, &w, 24, p(12.0, 12.0), CELL_A);
    let rows = reg.objects_in_cell(CELL_A);
    assert_eq!(rows.len(), 1);
    assert_eq!(rows[0].state, 0x44);
}

const WEENIE: u8 = EntityCollisionFlags::HAS_WEENIE;
const CREATURE: u8 = EntityCollisionFlags::IS_CREATURE;
const PLAYER: u8 = EntityCollisionFlags::IS_PLAYER;
const PK_LITE: u8 = EntityCollisionFlags::IS_PK_LITE;

#[test]
fn update_pwd_bitfield_flags_replaces_only_pwd_derived_bits() {
    let (mut reg, w) = (ShadowObjectRegistry::new(), CollisionWorld::new());
    reg_one(
        &mut reg,
        &w,
        60,
        One {
            pos: p(12.0, 12.0),
            flags: WEENIE | CREATURE | PLAYER,
            seed: CELL_A,
            ..One::default()
        },
    );
    reg.update_pwd_bitfield_flags(60, 0x200_0008);
    assert_eq!(
        reg.objects_in_cell(CELL_A)[0].flags,
        EntityCollisionFlags(WEENIE | CREATURE | PLAYER | PK_LITE)
    );
}

#[test]
fn update_pwd_bitfield_flags_clears_stale_pk_state() {
    let (mut reg, w) = (ShadowObjectRegistry::new(), CollisionWorld::new());
    reg_one(
        &mut reg,
        &w,
        61,
        One {
            pos: p(12.0, 12.0),
            flags: WEENIE | PLAYER | PK_LITE,
            seed: CELL_A,
            ..One::default()
        },
    );
    reg.update_pwd_bitfield_flags(61, 0x8);
    assert_eq!(
        reg.objects_in_cell(CELL_A)[0].flags,
        EntityCollisionFlags(WEENIE | PLAYER)
    );
}

#[test]
fn update_pwd_bitfield_flags_unregistered_entity_no_op() {
    let mut reg = ShadowObjectRegistry::new();
    reg.update_pwd_bitfield_flags(999, 0x200_0000);
    assert_eq!(reg.total_registered(), 0);
}

#[test]
fn update_pwd_bitfield_flags_then_exemption_both_pk_lite_now_collide() {
    let (mut reg, w) = (ShadowObjectRegistry::new(), CollisionWorld::new());
    reg_one(
        &mut reg,
        &w,
        62,
        One {
            pos: p(12.0, 12.0),
            flags: WEENIE | PLAYER,
            seed: CELL_A,
            ..One::default()
        },
    );
    let mover = ObjectInfoState(ObjectInfoState::IS_PLAYER | ObjectInfoState::IS_PK_LITE);
    let before = reg.objects_in_cell(CELL_A)[0];
    assert!(should_skip(before.state, before.flags, mover));
    reg.update_pwd_bitfield_flags(62, 0x200_0008);
    let after = reg.objects_in_cell(CELL_A)[0];
    assert!(!should_skip(after.state, after.flags, mover));
}

#[allow(clippy::too_many_arguments)]
fn replace(
    reg: &mut ShadowObjectRegistry,
    world: &CollisionWorld,
    id: u32,
    pos: Vec3,
    shapes: &[ShadowShape],
    lb: u32,
    seed: u32,
    suspend_if_new: bool,
    part_array: Option<&[ShadowShape]>,
) {
    reg.replace_multi_part_payload(
        world,
        id,
        shapes,
        site_at(pos, Quat::IDENTITY, (0.0, 0.0), lb, seed),
        0,
        EntityCollisionFlags::default(),
        false,
        suspend_if_new,
        part_array,
    )
    .unwrap();
}

#[test]
fn replace_multi_part_payload_visible_owner_keeps_existing_cells() {
    let (mut reg, w) = (ShadowObjectRegistry::new(), CollisionWorld::new());
    let pos = p(12.0, 12.0);
    reg_multi(
        &mut reg,
        &w,
        25,
        pos,
        &[bsp_shape(0x0100_0011, Vec3::ZERO, Vec3::ZERO, 0.5)],
        0,
        CELL_A,
        false,
        None,
    );
    replace(
        &mut reg,
        &w,
        25,
        pos,
        &[bsp_shape(0x0100_0012, Vec3::ZERO, Vec3::ZERO, 40.0)],
        0,
        0,
        false,
        None,
    );
    let rows = reg.objects_in_cell(CELL_A);
    assert_eq!(rows.len(), 1);
    assert_eq!(rows[0].gfx_obj_id, 0x0100_0012);
    assert!(reg.objects_in_cell(CELL_B).is_empty());
}

#[test]
fn replace_multi_part_payload_suspended_shaped_empty_shaped_remains_suspended() {
    let (mut reg, w) = (ShadowObjectRegistry::new(), CollisionWorld::new());
    let pos = p(12.0, 12.0);
    reg_multi(
        &mut reg,
        &w,
        26,
        pos,
        &[bsp_shape(0x0100_0021, Vec3::ZERO, Vec3::ZERO, 0.5)],
        0,
        CELL_A,
        false,
        None,
    );
    assert!(reg.suspend(26));
    replace(&mut reg, &w, 26, pos, &[], LB, CELL_A, false, None);
    replace(
        &mut reg,
        &w,
        26,
        pos,
        &[bsp_shape(0x0100_0022, Vec3::ZERO, Vec3::ZERO, 0.5)],
        LB,
        CELL_A,
        false,
        None,
    );
    assert_eq!(reg.retained_registration_count(), 1);
    assert_eq!(reg.suspended_registration_count(), 1);
    assert!(reg.objects_in_cell(CELL_A).is_empty());
    move_to(&mut reg, &w, 26, pos, CELL_A);
    assert_eq!(reg.objects_in_cell(CELL_A)[0].gfx_obj_id, 0x0100_0022);
}

#[test]
fn replace_multi_part_payload_new_cellless_owner_registers_retained_but_suspended() {
    let (mut reg, w) = (ShadowObjectRegistry::new(), CollisionWorld::new());
    replace(
        &mut reg,
        &w,
        27,
        p(12.0, 12.0),
        &[bsp_shape(0x0100_0031, Vec3::ZERO, Vec3::ZERO, 0.5)],
        LB,
        CELL_A,
        true,
        None,
    );
    assert_eq!(reg.retained_registration_count(), 1);
    assert_eq!(reg.suspended_registration_count(), 1);
    assert!(reg.objects_in_cell(CELL_A).is_empty());
}

#[test]
fn remove_landblock_clears_all_entities_for_that_block() {
    let (mut reg, w) = (ShadowObjectRegistry::new(), CollisionWorld::new());
    reg_one(
        &mut reg,
        &w,
        1,
        One {
            pos: p(12.0, 12.0),
            ..One::default()
        },
    );
    reg_one(
        &mut reg,
        &w,
        2,
        One {
            pos: p(204.0, 12.0),
            off: (192.0, 0.0),
            lb: 0xAAAA_0000,
            ..One::default()
        },
    );
    reg.remove_landblock(LB);
    assert_eq!(reg.total_registered(), 1);
    assert!(reg.objects_in_cell(CELL_A).is_empty());
}

#[test]
fn remove_landblock_dynamic_registration_refloods_after_reload() {
    let (mut reg, w) = (ShadowObjectRegistry::new(), CollisionWorld::new());
    reg_one(
        &mut reg,
        &w,
        31,
        One {
            pos: p(12.0, 12.0),
            seed: CELL_A,
            is_static: false,
            ..One::default()
        },
    );
    reg.remove_landblock(LB);
    assert!(reg.objects_in_cell(CELL_A).is_empty());
    reg.reflood_landblock(&w, LB).unwrap();
    assert!(holds(&reg, CELL_A, 31));
}

#[test]
fn streaming_receipts_are_stable_ordered_and_advance_one_static_owner() {
    let (mut reg, w) = (ShadowObjectRegistry::new(), CollisionWorld::new());
    reg_one(
        &mut reg,
        &w,
        9,
        One {
            pos: p(12.0, 12.0),
            seed: CELL_A,
            ..One::default()
        },
    );
    reg_one(
        &mut reg,
        &w,
        2,
        One {
            pos: p(14.0, 12.0),
            seed: CELL_A,
            ..One::default()
        },
    );
    reg_one(
        &mut reg,
        &w,
        5,
        One {
            pos: p(16.0, 12.0),
            seed: CELL_A,
            is_static: false,
            ..One::default()
        },
    );
    assert_eq!(reg.capture_static_owners_for_landblock(LB), vec![2, 9]);
    assert_eq!(reg.capture_reflood_owners_for_landblock(LB), vec![2, 5, 9]);
    reg.deregister_static_owner_for_landblock(2, LB);
    assert_eq!(reg.retained_registration_count(), 2);
    assert!(!holds(&reg, CELL_A, 2));
    assert!(holds(&reg, CELL_A, 5));
    assert!(holds(&reg, CELL_A, 9));
}

fn world_with_terrain(lb: u32, origin: Vec3) -> CollisionWorld {
    let mut w = CollisionWorld::new();
    w.register_terrain(lb, origin);
    w
}

#[test]
fn reflood_landblock_restores_adjacent_owned_footprint_withdrawn_by_unload() {
    const ADJ: u32 = 0xAAB4_0000;
    const OWNER_CELL: u32 = ADJ | 1;
    const TOUCHED: u32 = LB | 57;
    let w = world_with_terrain(ADJ, Vec3::new(192.0, 0.0, 0.0));
    let mut reg = ShadowObjectRegistry::new();
    reg_one(
        &mut reg,
        &w,
        32,
        One {
            pos: p(192.5, 12.0),
            radius: 2.0,
            off: (192.0, 0.0),
            lb: ADJ,
            seed: OWNER_CELL,
            is_static: false,
            ..One::default()
        },
    );
    assert!(holds(&reg, TOUCHED, 32));
    assert!(holds(&reg, OWNER_CELL, 32));
    reg.remove_landblock(LB);
    assert!(!holds(&reg, TOUCHED, 32));
    assert!(holds(&reg, OWNER_CELL, 32));
    assert_eq!(reg.withdrawn_prefix_marker_count(), 1);
    reg.reflood_landblock(&w, LB).unwrap();
    assert!(holds(&reg, TOUCHED, 32));
    assert!(holds(&reg, OWNER_CELL, 32));
    assert_eq!(reg.withdrawn_prefix_marker_count(), 0);
}

#[test]
fn authoritative_move_clears_obsolete_withdrawn_prefix() {
    const ADJ: u32 = 0xAAB4_0000;
    const OWNER_CELL: u32 = ADJ | 1;
    let w = world_with_terrain(ADJ, Vec3::new(192.0, 0.0, 0.0));
    let mut reg = ShadowObjectRegistry::new();
    reg_one(
        &mut reg,
        &w,
        33,
        One {
            pos: p(192.5, 12.0),
            radius: 2.0,
            off: (192.0, 0.0),
            lb: ADJ,
            seed: OWNER_CELL,
            is_static: false,
            ..One::default()
        },
    );
    reg.remove_landblock(LB);
    assert_eq!(reg.withdrawn_prefix_marker_count(), 1);
    reg.update_position(
        &w,
        33,
        site_at(
            p(204.0, 12.0),
            Quat::IDENTITY,
            (192.0, 0.0),
            ADJ,
            OWNER_CELL,
        ),
    )
    .unwrap();
    assert_eq!(reg.withdrawn_prefix_marker_count(), 0);
    reg.reflood_landblock(&w, LB).unwrap();
    assert!(!holds(&reg, LB | 57, 33));
}

#[test]
fn reflood_landblock_retires_only_current_marker_when_two_prefixes_were_withdrawn() {
    const OWNER_LB: u32 = 0xAAB5_0000;
    const WEST: u32 = 0xA9B5_0000;
    const SOUTH: u32 = 0xAAB4_0000;
    let w = world_with_terrain(OWNER_LB, Vec3::new(192.0, 192.0, 0.0));
    let mut reg = ShadowObjectRegistry::new();
    reg_one(
        &mut reg,
        &w,
        35,
        One {
            pos: p(192.5, 192.5),
            radius: 2.0,
            off: (192.0, 192.0),
            lb: OWNER_LB,
            seed: OWNER_LB | 1,
            is_static: false,
            ..One::default()
        },
    );
    assert!(holds(&reg, WEST | 57, 35));
    assert!(holds(&reg, SOUTH | 8, 35));
    reg.remove_landblock(WEST);
    reg.remove_landblock(SOUTH);
    assert_eq!(reg.withdrawn_prefix_marker_count(), 2);
    reg.reflood_landblock(&w, WEST).unwrap();
    assert_eq!(reg.withdrawn_prefix_marker_count(), 1);
    reg.reflood_landblock(&w, SOUTH).unwrap();
    assert_eq!(reg.withdrawn_prefix_marker_count(), 0);
    assert!(holds(&reg, WEST | 57, 35));
    assert!(holds(&reg, SOUTH | 8, 35));
}

#[test]
fn remove_landblock_direct_static_retirement_clears_withdrawn_marker() {
    let (mut reg, w) = (ShadowObjectRegistry::new(), CollisionWorld::new());
    reg_one(
        &mut reg,
        &w,
        34,
        One {
            pos: p(12.0, 12.0),
            ..One::default()
        },
    );
    reg.remove_landblock(LB);
    assert_eq!(reg.retained_registration_count(), 0);
    assert_eq!(reg.withdrawn_prefix_marker_count(), 0);
}

#[test]
fn per_cell_query_entity_cell_returns_it() {
    let (mut reg, w) = (ShadowObjectRegistry::new(), CollisionWorld::new());
    reg_one(
        &mut reg,
        &w,
        10,
        One {
            pos: p(30.0, 30.0),
            ..One::default()
        },
    );
    let rows = reg.objects_in_cell(LB | 10);
    assert_eq!(rows.len(), 1);
    assert_eq!(rows[0].entity_id, 10);
}

#[test]
fn per_cell_query_far_cell_returns_empty() {
    let (mut reg, w) = (ShadowObjectRegistry::new(), CollisionWorld::new());
    reg_one(
        &mut reg,
        &w,
        11,
        One {
            pos: p(12.0, 12.0),
            ..One::default()
        },
    );
    assert!(reg.objects_in_cell(LB | 64).is_empty());
}

#[test]
fn per_cell_query_entity_in_multiple_cells_once_per_cell() {
    let (mut reg, w) = (ShadowObjectRegistry::new(), CollisionWorld::new());
    reg_one(
        &mut reg,
        &w,
        20,
        One {
            pos: p(24.0, 12.0),
            radius: 2.0,
            ..One::default()
        },
    );
    assert_eq!(rows_of(&reg, CELL_A, 20).len(), 1);
    assert_eq!(rows_of(&reg, CELL_B, 20).len(), 1);
}

#[test]
fn objects_in_cell_cell_id_formula() {
    for (lx, ly, low) in [
        (0.0, 0.0, 1),
        (48.0, 0.0, 17),
        (0.0, 48.0, 3),
        (168.0, 168.0, 64),
    ] {
        let (mut reg, w) = (ShadowObjectRegistry::new(), CollisionWorld::new());
        reg_one(
            &mut reg,
            &w,
            99,
            One {
                pos: p(lx + 0.5, ly + 0.5),
                radius: 0.1,
                ..One::default()
            },
        );
        assert!(holds(&reg, LB | low, 99), "({lx}, {ly})");
    }
}

#[test]
fn update_position_moved_entity_new_cell_occupied() {
    let (mut reg, w) = (ShadowObjectRegistry::new(), CollisionWorld::new());
    reg_one(
        &mut reg,
        &w,
        42,
        One {
            pos: p(12.0, 12.0),
            radius: 0.5,
            ..One::default()
        },
    );
    move_to(&mut reg, &w, 42, p(60.0, 12.0), 0);
    assert!(reg.objects_in_cell(CELL_A).is_empty());
    let rows = reg.objects_in_cell(LB | 17);
    assert_eq!(rows.len(), 1);
    assert_eq!(rows[0].entity_id, 42);
    assert_eq!(reg.total_registered(), 1);
}

#[test]
fn update_position_preserves_flags() {
    let (mut reg, w) = (ShadowObjectRegistry::new(), CollisionWorld::new());
    let flags = EntityCollisionFlags::IS_PLAYER | EntityCollisionFlags::IS_PK;
    reg_one(
        &mut reg,
        &w,
        50,
        One {
            pos: p(12.0, 12.0),
            radius: 0.5,
            state: 0x4,
            flags,
            ..One::default()
        },
    );
    move_to(&mut reg, &w, 50, p(60.0, 12.0), 0);
    let rows = reg.objects_in_cell(LB | 17);
    assert_eq!(rows.len(), 1);
    assert_eq!(rows[0].state, 0x4);
    assert_eq!(rows[0].flags, EntityCollisionFlags(flags));
}

#[test]
fn update_position_unregistered_entity_no_op() {
    let (mut reg, w) = (ShadowObjectRegistry::new(), CollisionWorld::new());
    move_to(&mut reg, &w, 99, p(12.0, 12.0), 0);
    assert_eq!(reg.total_registered(), 0);
}

#[test]
fn register_with_state_and_flags_stored_on_entry() {
    let (mut reg, w) = (ShadowObjectRegistry::new(), CollisionWorld::new());
    reg_one(
        &mut reg,
        &w,
        60,
        One {
            pos: p(12.0, 12.0),
            radius: 0.5,
            state: 0x10,
            flags: EntityCollisionFlags::IS_IMPENETRABLE,
            ..One::default()
        },
    );
    let e = reg.objects_in_cell(CELL_A)[0];
    assert_eq!(e.state, 0x10);
    assert_eq!(
        e.flags,
        EntityCollisionFlags(EntityCollisionFlags::IS_IMPENETRABLE)
    );
}

#[test]
fn register_default_state_and_flags_are_zero() {
    let (mut reg, w) = (ShadowObjectRegistry::new(), CollisionWorld::new());
    reg_one(
        &mut reg,
        &w,
        70,
        One {
            pos: p(12.0, 12.0),
            radius: 0.5,
            ..One::default()
        },
    );
    let e = reg.objects_in_cell(CELL_A)[0];
    assert_eq!(e.state, 0);
    assert_eq!(e.flags, EntityCollisionFlags::default());
}

#[test]
fn update_physics_state_flips_ethereal_next_lookup_sees_new_bits() {
    const DOOR: u32 = 0x000F_4244;
    let (mut reg, w) = (ShadowObjectRegistry::new(), CollisionWorld::new());
    reg_one(
        &mut reg,
        &w,
        DOOR,
        One {
            gfx: 0x0200_19FF,
            pos: p(12.0, 12.0),
            ..One::default()
        },
    );
    let state_of = |reg: &ShadowObjectRegistry| {
        reg.capture_entries()
            .iter()
            .find(|e| e.entity_id == DOOR)
            .unwrap()
            .state
    };
    assert_eq!(state_of(&reg), 0);
    reg.update_physics_state(DOOR, 0x4);
    assert_eq!(state_of(&reg), 0x4);
}

#[test]
fn update_physics_state_unregistered_entity_is_no_op() {
    let mut reg = ShadowObjectRegistry::new();
    reg.update_physics_state(0xDEAD_BEEF, 0x4);
    assert_eq!(reg.total_registered(), 0);
}

#[test]
fn update_physics_state_entity_spanning_cells_all_updated() {
    let (mut reg, w) = (ShadowObjectRegistry::new(), CollisionWorld::new());
    reg_one(
        &mut reg,
        &w,
        99,
        One {
            pos: p(24.0, 12.0),
            radius: 2.0,
            ..One::default()
        },
    );
    reg.update_physics_state(99, 0x4);
    assert_eq!(rows_of(&reg, CELL_A, 99)[0].state, 0x4);
    assert_eq!(rows_of(&reg, CELL_B, 99)[0].state, 0x4);
}

#[test]
fn register_outdoor_seed_door_in_its_outdoor_cell_not_in_unrelated_indoor_cell() {
    const DOOR: u32 = 0x000F_4244;
    let (mut reg, w) = (ShadowObjectRegistry::new(), CollisionWorld::new());
    reg_one(
        &mut reg,
        &w,
        DOOR,
        One {
            gfx: 0x0200_19FF,
            pos: p(12.0, 12.0),
            ty: ShadowCollisionType::Cylinder,
            cyl: 2.5,
            is_static: false,
            ..One::default()
        },
    );
    assert!(holds(&reg, CELL_A, DOOR));
    assert!(!holds(&reg, LB | 0x0145, DOOR));
}

#[test]
fn register_outdoor_footprint_never_lands_in_interior_cells() {
    const COTTAGE: u32 = 0xA9B4_7900;
    let (mut reg, w) = (ShadowObjectRegistry::new(), CollisionWorld::new());
    reg_one(
        &mut reg,
        &w,
        COTTAGE,
        One {
            gfx: 0x0100_0A2B,
            pos: Vec3::new(12.0, 12.0, 90.0),
            radius: 5.5,
            ..One::default()
        },
    );
    assert!(reg.objects_in_cell(LB | 0x0146).is_empty());
    assert!(reg.capture_entries().iter().all(|e| e.entity_id == COTTAGE));
}

#[test]
fn reflood_landblock_reruns_flood_after_cells_arrive() {
    const SEED: u32 = 0xA9B4_0100;
    const NEIGHBOR: u32 = 0xA9B4_0101;
    let mut w = CollisionWorld::new();
    let mut reg = ShadowObjectRegistry::new();
    reg_one(
        &mut reg,
        &w,
        77,
        One {
            gfx: 0x0100_0009,
            pos: Vec3::new(2.0, 0.0, 2.5),
            radius: 0.5,
            seed: SEED,
            is_static: false,
            ..One::default()
        },
    );
    assert!(holds(&reg, SEED, 77));
    assert!(reg.objects_in_cell(NEIGHBOR).is_empty());

    let leaf = CellBsp::new(vec![CellBspNode::Leaf]).unwrap();
    let mut seed = CellPhysics {
        id: SEED,
        portals: vec![PortalInfo::new(0x0101, 10, 0)],
        containment_bsp: leaf.clone(),
        ..CellPhysics::default()
    };
    seed.portal_polygons.insert(
        10,
        ResolvedPolygon::new(
            10,
            vec![
                Vec3::new(2.5, -2.5, 0.0),
                Vec3::new(2.5, 2.5, 0.0),
                Vec3::new(2.5, 2.5, 5.0),
                Vec3::new(2.5, -2.5, 5.0),
            ],
            Plane::new(Vec3::X, -2.5),
        ),
    );
    w.insert_cell(seed);
    w.insert_cell(CellPhysics {
        id: NEIGHBOR,
        placement: Placement::at(Vec3::new(5.0, 0.0, 0.0)),
        containment_bsp: leaf,
        ..CellPhysics::default()
    });
    reg.reflood_landblock(&w, LB).unwrap();
    assert!(holds(&reg, SEED, 77));
    assert!(holds(&reg, NEIGHBOR, 77));
}

// ── ShadowObjectRegistryCaptureTests ───────────────────────────────────

const DOOR: u32 = 0x000F_4244;
const POST: u32 = 0x000F_4245;
const WIDE: u32 = 0x000F_4246;

fn populated() -> ShadowObjectRegistry {
    let w = CollisionWorld::new();
    let mut reg = ShadowObjectRegistry::new();
    let sphere = |local: Vec3| bsp_shape(0x0100_44B6, local, Vec3::ZERO, 0.4);
    reg_multi(
        &mut reg,
        &w,
        DOOR,
        Vec3::new(20.0, 20.0, 0.0),
        &[
            cyl_shape(0, Vec3::new(0.0, 0.0, 0.1), 0.1, 0.2),
            sphere(Vec3::new(0.5, 0.0, 0.0)),
            sphere(Vec3::new(-0.5, 0.0, 0.0)),
            sphere(Vec3::new(0.0, 0.5, 0.0)),
        ],
        0,
        0,
        false,
        None,
    );
    reg_one(
        &mut reg,
        &w,
        POST,
        One {
            pos: Vec3::new(60.0, 22.0, 0.0),
            radius: 0.3,
            ty: ShadowCollisionType::Cylinder,
            cyl: 2.0,
            ..One::default()
        },
    );
    reg_one(
        &mut reg,
        &w,
        WIDE,
        One {
            pos: Vec3::new(96.0, 96.0, 0.0),
            radius: 30.0,
            ty: ShadowCollisionType::Cylinder,
            cyl: 2.0,
            ..One::default()
        },
    );
    reg
}

#[test]
fn capture_every_object_has_each_part() {
    let reg = populated();
    assert_eq!(
        reg.capture_entries()
            .iter()
            .filter(|e| e.entity_id == DOOR)
            .count(),
        4
    );
}

#[test]
fn capture_object_spanning_cells_once() {
    let reg = populated();
    assert!(reg.owner_cells(WIDE).len() > 1);
    assert_eq!(
        reg.capture_entries()
            .iter()
            .filter(|e| e.entity_id == WIDE)
            .count(),
        1
    );
}

#[test]
fn capture_one_object_gives_only_its_parts_and_nothing_for_unknown_id() {
    let reg = populated();
    let all: Vec<ShadowEntry> = reg
        .capture_entries()
        .into_iter()
        .filter(|e| e.entity_id == DOOR)
        .collect();
    assert_eq!(reg.capture_entries_for(DOOR), all);
    assert!(reg.capture_entries_for(0x7FFF_FFFF).is_empty());
}

#[test]
fn capture_near_a_point_keeps_parts_whose_reach_comes_within_radius() {
    let reg = populated();
    for (x, y, radius) in [
        (20.0, 20.0, 3.0),
        (60.0, 20.0, 10.0),
        (20.0, 20.0, 100.0),
        (150.0, 150.0, 1.0),
    ] {
        let centre = Vec2::new(x, y);
        let expected: Vec<ShadowEntry> = reg
            .capture_entries()
            .into_iter()
            .filter(|e| Vec2::new(e.position.x, e.position.y).distance(centre) <= radius + e.radius)
            .collect();
        assert_eq!(reg.capture_entries_near(centre, radius), expected);
    }
}

// ── ShadowObjectRegistryCellRenderRevisionTests ────────────────────────

fn mesh_parts(mesh: u32) -> Vec<ShadowShape> {
    vec![bsp_shape(mesh, Vec3::ZERO, Vec3::ZERO, 1.0)]
}

fn reg_render(reg: &mut ShadowObjectRegistry, owner: u32, x: f32) {
    let parts = mesh_parts(0x0100_0001);
    let seed = LB | ((x / 24.0) as u32 * 8 + 1);
    reg_multi(
        reg,
        &CollisionWorld::new(),
        owner,
        p(x, 12.0),
        &parts,
        0,
        seed,
        false,
        Some(&parts),
    );
}

#[test]
fn render_revision_stable_reads_and_unrelated_motion_preserve_revision() {
    let w = CollisionWorld::new();
    let mut reg = ShadowObjectRegistry::new();
    assert_eq!(reg.cell_render_revision(CELL_A), 0);
    reg_render(&mut reg, 1, 12.0);
    reg_render(&mut reg, 2, 84.0);
    let revision = reg.cell_render_revision(CELL_A);
    assert_ne!(revision, 0);
    move_to(&mut reg, &w, 2, p(85.0, 12.0), 0);
    assert_eq!(reg.cell_render_revision(CELL_A), revision);
    move_to(&mut reg, &w, 1, p(13.0, 12.0), 0);
    assert!(reg.cell_render_revision(CELL_A) > revision);
}

#[test]
fn render_revision_removing_one_of_several_owners_invalidates_bucket() {
    let mut reg = ShadowObjectRegistry::new();
    reg_render(&mut reg, 1, 12.0);
    reg_render(&mut reg, 2, 12.0);
    let revision = reg.cell_render_revision(CELL_A);
    reg.deregister(1);
    assert!(!reg.retail_part_entries_in_cell(CELL_A).is_empty());
    assert!(reg.cell_render_revision(CELL_A) > revision);
}

#[test]
fn render_revision_empty_recreated_and_reset_cells_never_reuse_revision() {
    let mut reg = ShadowObjectRegistry::new();
    reg_render(&mut reg, 1, 12.0);
    let first = reg.cell_render_revision(CELL_A);
    reg.deregister(1);
    assert_eq!(reg.cell_render_revision(CELL_A), 0);
    reg_render(&mut reg, 1, 12.0);
    let second = reg.cell_render_revision(CELL_A);
    assert!(second > first);
    reg.clear();
    assert_eq!(reg.cell_render_revision(CELL_A), 0);
    reg_render(&mut reg, 1, 12.0);
    assert!(reg.cell_render_revision(CELL_A) > second);
}

#[test]
fn render_revision_attached_part_publication_and_removal_invalidate_parent_cell() {
    let mut reg = ShadowObjectRegistry::new();
    reg_render(&mut reg, 1, 12.0);
    let before = reg.cell_render_revision(CELL_A);
    assert!(reg.attach_child(2, 1, mesh_parts(0x0100_0002)));
    let attached = reg.cell_render_revision(CELL_A);
    assert!(attached > before);
    assert!(reg.detach_child(2));
    assert!(reg.cell_render_revision(CELL_A) > attached);
}

#[test]
fn render_revision_replacing_part_payload_invalidates_without_changing_membership() {
    let w = CollisionWorld::new();
    let mut reg = ShadowObjectRegistry::new();
    reg_render(&mut reg, 1, 12.0);
    let before = reg.cell_render_revision(CELL_A);
    let parts = mesh_parts(0x0100_0002);
    replace(
        &mut reg,
        &w,
        1,
        p(12.0, 12.0),
        &parts,
        LB,
        CELL_A,
        false,
        Some(&parts),
    );
    assert!(reg.cell_render_revision(CELL_A) > before);
    assert!(
        reg.retail_part_entries_in_cell(CELL_A)
            .iter()
            .all(|e| e.gfx_obj_id == 0x0100_0002)
    );
}

#[test]
fn render_revision_retiring_owner_invalidates_and_block_removal_empties() {
    let mut reg = ShadowObjectRegistry::new();
    reg_render(&mut reg, 1, 12.0);
    reg_render(&mut reg, 2, 12.0);
    let before = reg.cell_render_revision(CELL_A);
    reg.retire_owner_from_landblock(1, LB);
    assert!(reg.cell_render_revision(CELL_A) > before);
    reg.remove_landblock(LB);
    assert_eq!(reg.cell_render_revision(CELL_A), 0);
    reg_render(&mut reg, 1, 12.0);
    assert!(reg.cell_render_revision(CELL_A) > before);
}

// ── ShadowRegistrationOverflowTests ────────────────────────────────────

const ENTITY_A: u32 = 0x40F6_8221;
const ENTITY_B: u32 = 0xC0F6_8221;
const OVERFLOW_LB: u32 = 0xF682_0000;

#[test]
fn old_part_id_scheme_overflows_and_collides() {
    let a = ENTITY_A.wrapping_mul(256);
    let b = ENTITY_B.wrapping_mul(256);
    assert_eq!(a, 0xF682_2100);
    assert_eq!(a, b);
    assert_ne!(ENTITY_A, ENTITY_B);
}

#[test]
fn old_per_part_register_colliding_ids_second_overwrites_first() {
    let (mut reg, w) = (ShadowObjectRegistry::new(), CollisionWorld::new());
    let cyl = |pos| One {
        pos,
        lb: OVERFLOW_LB,
        ty: ShadowCollisionType::Cylinder,
        cyl: 2.0,
        ..One::default()
    };
    reg_one(&mut reg, &w, ENTITY_A.wrapping_mul(256), cyl(p(12.0, 12.0)));
    reg_one(&mut reg, &w, ENTITY_B.wrapping_mul(256), cyl(p(42.0, 12.0)));
    assert!(reg.objects_in_cell(OVERFLOW_LB | 1).is_empty());
    assert!(!reg.objects_in_cell(OVERFLOW_LB | 9).is_empty());
    assert_eq!(reg.total_registered(), 1);
}

#[test]
fn register_multi_part_colliding_low_bits_ids_both_survive() {
    let (mut reg, w) = (ShadowObjectRegistry::new(), CollisionWorld::new());
    let shapes = [cyl_shape(0, Vec3::ZERO, 1.0, 2.0)];
    for (id, pos) in [(ENTITY_A, p(12.0, 12.0)), (ENTITY_B, p(42.0, 12.0))] {
        reg.register_multi_part(
            &w,
            id,
            &shapes,
            site_at(pos, Quat::IDENTITY, (0.0, 0.0), OVERFLOW_LB, 0),
            0,
            EntityCollisionFlags::default(),
            true,
            None,
        )
        .unwrap();
    }
    assert!(holds(&reg, OVERFLOW_LB | 1, ENTITY_A));
    assert!(holds(&reg, OVERFLOW_LB | 9, ENTITY_B));
    assert_eq!(reg.total_registered(), 2);
}

fn bsp_gfx(id: u32, radius: f32, center_z: f32) -> GfxObjPhysics {
    let mut g = gfx_obj(
        id,
        single_leaf(Vec3::new(0.0, 0.0, center_z), radius, Vec::new()),
        radius,
    );
    g.bounding_sphere.center = Vec3::new(0.0, 0.0, center_z);
    g
}

fn part_ref(gfx: u32, position: Vec3, scale: f32) -> PartRef {
    PartRef {
        gfx_obj_id: gfx,
        position,
        rotation: Quat::IDENTITY,
        scale: Vec3::splat(scale),
    }
}

#[test]
fn from_landblock_bsp_parts_one_shape_per_bsp_part_local_transform_preserved() {
    let gfx = bsp_gfx(0x0100_0AC5, 1.05, 0.75);
    let parts = [
        part_ref(0x0100_0AC5, Vec3::new(0.0, 0.5, 0.4), 1.0),
        part_ref(0x0100_0AC5, Vec3::new(0.0, 1.0, 0.8), 1.0),
        part_ref(0x00BA_DBAD, Vec3::ZERO, 1.0),
    ];
    let shapes = from_landblock_bsp_parts(&parts, false, &|id| (id == 0x0100_0AC5).then_some(&gfx));
    assert_eq!(shapes.len(), 2);
    assert!(
        shapes
            .iter()
            .all(|s| s.collision_type == ShadowCollisionType::Bsp)
    );
    assert!(shapes.iter().all(|s| s.gfx_obj_id == 0x0100_0AC5));
    assert!(
        shapes
            .iter()
            .any(|s| s.local_position.distance(Vec3::new(0.0, 0.5, 0.4)) < 1e-4)
    );
    assert!(
        shapes
            .iter()
            .any(|s| s.local_position.distance(Vec3::new(0.0, 1.0, 0.8)) < 1e-4)
    );
    for s in &shapes {
        assert_eq_prec(1.05, s.radius, 3);
    }
}

#[test]
fn from_landblock_bsp_parts_carries_the_scaled_root_sphere_centre() {
    let gfx = bsp_gfx(0x0100_0AC5, 4.0, 3.0);
    let shapes = from_landblock_bsp_parts(
        &[part_ref(0x0100_0AC5, Vec3::new(0.0, 2.0, 0.0), 0.5)],
        false,
        &|_| Some(&gfx),
    );
    assert_eq!(shapes.len(), 1);
    assert_eq_prec(0.5, shapes[0].scale, 3);
    assert_eq_prec(2.0, shapes[0].radius, 3);
    assert_eq!(shapes[0].bounds_center, Vec3::new(0.0, 0.0, 1.5));
}

#[test]
fn from_landblock_bsp_parts_building_shell_returns_empty() {
    let gfx = bsp_gfx(0x0100_0AC5, 1.0, 0.75);
    assert!(
        from_landblock_bsp_parts(&[part_ref(0x0100_0AC5, Vec3::ZERO, 1.0)], true, &|_| Some(
            &gfx
        ))
        .is_empty()
    );
}

// ── ShadowObjectRegistryMultiPartTests ─────────────────────────────────

fn door_shapes() -> Vec<ShadowShape> {
    vec![
        cyl_shape(0, Vec3::new(0.0, 0.0, 0.018), 0.1, 0.2),
        bsp_shape(0x0100_44B5, Vec3::ZERO, Vec3::ZERO, 2.0),
        bsp_shape(0x0100_44B6, Vec3::ZERO, Vec3::ZERO, 2.0),
        bsp_shape(0x0100_44B6, Vec3::ZERO, Vec3::ZERO, 2.0),
    ]
}

const DOOR_POS: Vec3 = Vec3::new(132.6, 17.1, 94.08);

fn door_reg() -> ShadowObjectRegistry {
    let mut reg = ShadowObjectRegistry::new();
    reg_multi(
        &mut reg,
        &CollisionWorld::new(),
        DOOR,
        DOOR_POS,
        &door_shapes(),
        0x10008,
        0,
        false,
        None,
    );
    reg
}

#[test]
fn register_multi_part_four_shapes_all_share_entity_id() {
    let reg = door_reg();
    assert!(
        reg.capture_entries()
            .iter()
            .filter(|e| e.entity_id == DOOR)
            .count()
            >= 4
    );
}

#[test]
fn register_multi_part_empty_shape_list_no_op() {
    let mut reg = ShadowObjectRegistry::new();
    reg_multi(
        &mut reg,
        &CollisionWorld::new(),
        1,
        Vec3::ZERO,
        &[],
        0,
        0,
        false,
        None,
    );
    assert_eq!(reg.total_registered(), 0);
}

#[test]
fn multi_part_deregister_removes_all_parts() {
    let mut reg = door_reg();
    reg.deregister(DOOR);
    assert_eq!(reg.total_registered(), 0);
    assert!(reg.capture_entries().iter().all(|e| e.entity_id != DOOR));
}

#[test]
fn multi_part_update_physics_state_propagates_to_all_parts() {
    let mut reg = door_reg();
    reg.update_physics_state(DOOR, 0x1000C);
    let parts: Vec<ShadowEntry> = reg
        .capture_entries()
        .into_iter()
        .filter(|e| e.entity_id == DOOR)
        .collect();
    assert!(parts.len() >= 4);
    assert!(parts.iter().all(|e| e.state == 0x1000C));
}

#[test]
fn register_multi_part_parts_across_multiple_cells_all_cells_listed() {
    let mut reg = ShadowObjectRegistry::new();
    let shapes = [
        cyl_shape(0, Vec3::ZERO, 1.0, 2.0),
        cyl_shape(0, Vec3::new(30.0, 0.0, 0.0), 1.0, 2.0),
    ];
    reg_multi(
        &mut reg,
        &CollisionWorld::new(),
        1,
        p(12.0, 12.0),
        &shapes,
        0,
        0,
        false,
        None,
    );
    assert!(holds(&reg, CELL_A, 1));
    assert!(holds(&reg, CELL_B, 1));
}

#[test]
fn multi_part_update_position_moves_all_parts_with_entity() {
    let w = CollisionWorld::new();
    let mut reg = ShadowObjectRegistry::new();
    let shapes = [
        cyl_shape(0, Vec3::ZERO, 0.5, 1.0),
        cyl_shape(0, Vec3::X, 0.5, 1.0),
    ];
    reg_multi(
        &mut reg,
        &w,
        0xA1,
        p(10.0, 10.0),
        &shapes,
        0,
        0,
        false,
        None,
    );
    move_to(&mut reg, &w, 0xA1, p(50.0, 10.0), 0);
    let parts: Vec<ShadowEntry> = reg
        .capture_entries()
        .into_iter()
        .filter(|e| e.entity_id == 0xA1)
        .collect();
    assert_eq!(parts.len(), 2);
    assert!(
        parts
            .iter()
            .any(|e| e.position.distance(p(50.0, 10.0)) < 0.01)
    );
    assert!(
        parts
            .iter()
            .any(|e| e.position.distance(p(51.0, 10.0)) < 0.01)
    );
}

#[test]
fn multi_part_deregister_then_stray_update_position_is_no_op() {
    let mut reg = door_reg();
    reg.deregister(DOOR);
    move_to(&mut reg, &CollisionWorld::new(), DOOR, p(200.0, 200.0), 0);
    assert_eq!(reg.total_registered(), 0);
}

fn outdoor_cells_holding(reg: &ShadowObjectRegistry, owner: u32) -> Vec<u32> {
    (1..=64)
        .map(|i| LB | i)
        .filter(|c| holds(reg, *c, owner))
        .collect()
}

/// OpenAC's `Bsp(radius, boundsCenter, localPosition, localRotation)` with
/// its default centre (0, 6, 0).
fn flood_bsp(radius: f32, center: Option<Vec3>, local: Vec3, rotation: Quat) -> ShadowShape {
    ShadowShape::bsp(
        0x0100_44B5,
        local,
        rotation,
        1.0,
        ShadowPartGeometry::new(
            Sphere::new(center.unwrap_or(Vec3::new(0.0, 6.0, 0.0)), radius),
            None,
        ),
    )
}

fn fb(radius: f32) -> ShadowShape {
    flood_bsp(radius, None, Vec3::ZERO, Quat::IDENTITY)
}

fn fbc(radius: f32, center: Vec3) -> ShadowShape {
    flood_bsp(radius, Some(center), Vec3::ZERO, Quat::IDENTITY)
}

fn fcyl(radius: f32, local: Vec3) -> ShadowShape {
    cyl_shape(0, local, radius, radius * 2.0)
}

fn fsph(radius: f32, local: Vec3) -> ShadowShape {
    ShadowShape::sphere(0, local, Quat::IDENTITY, 1.0, radius)
}

const FLOOD_POS: Vec3 = Vec3::new(36.0, 36.0, 50.0);

fn flood_cells_for(shapes: &[ShadowShape]) -> Vec<u32> {
    let mut reg = ShadowObjectRegistry::new();
    reg_multi(
        &mut reg,
        &CollisionWorld::new(),
        0xBEEF01,
        FLOOD_POS,
        shapes,
        0x10008,
        0,
        false,
        None,
    );
    outdoor_cells_holding(&reg, 0xBEEF01)
}

#[test]
fn multi_part_flood_narrow_owner_after_wide_owner_floods_as_though_alone() {
    let w = CollisionWorld::new();
    let mut shared = ShadowObjectRegistry::new();
    reg_multi(
        &mut shared,
        &w,
        0xBEEF10,
        FLOOD_POS,
        &[
            fb(14.0),
            fbc(14.0, Vec3::new(0.0, -6.0, 0.0)),
            fbc(14.0, Vec3::new(6.0, 0.0, 0.0)),
        ],
        0x10008,
        0,
        false,
        None,
    );
    reg_multi(
        &mut shared,
        &w,
        0xBEEF11,
        FLOOD_POS,
        &[fb(0.5)],
        0x10008,
        0,
        false,
        None,
    );
    let alone = flood_cells_for(&[fb(0.5)]);
    let after_wide = outdoor_cells_holding(&shared, 0xBEEF11);
    let wide = outdoor_cells_holding(&shared, 0xBEEF10);
    assert_eq!(alone, after_wide);
    assert!(wide.len() > after_wide.len());
}

#[test]
fn cylinder_flood_second_owner_floods_as_though_alone() {
    let w = CollisionWorld::new();
    let mut shared = ShadowObjectRegistry::new();
    reg_multi(
        &mut shared,
        &w,
        0xBEEF20,
        FLOOD_POS,
        &[
            fcyl(12.0, Vec3::ZERO),
            fcyl(12.0, Vec3::new(0.0, -8.0, 0.0)),
        ],
        0x10008,
        0,
        false,
        None,
    );
    reg_multi(
        &mut shared,
        &w,
        0xBEEF21,
        FLOOD_POS,
        &[fcyl(0.5, Vec3::ZERO)],
        0x10008,
        0,
        false,
        None,
    );
    assert_eq!(
        flood_cells_for(&[fcyl(0.5, Vec3::ZERO)]),
        outdoor_cells_holding(&shared, 0xBEEF21)
    );
}

#[test]
fn flood_spheres_bsp_bearing_owner_floods_from_bsp_not_cylinder() {
    let cylinder_only = flood_cells_for(&[fcyl(0.5, Vec3::ZERO)]);
    let bsp_only = flood_cells_for(&[fb(14.0)]);
    let mixed = flood_cells_for(&[fcyl(0.5, Vec3::ZERO), fb(14.0)]);
    assert_eq!(cylinder_only, vec![LB | 10]);
    assert!(bsp_only.len() > 1);
    assert_eq!(bsp_only, mixed);
    assert_ne!(cylinder_only, mixed);
}

#[test]
fn flood_spheres_bsp_shape_centres_on_bounds_centre_not_part_origin() {
    let concentric = flood_cells_for(&[fbc(6.0, Vec3::new(0.001, 0.0, 0.0))]);
    let via_bounds = flood_cells_for(&[fbc(6.0, Vec3::new(0.0, 30.0, 0.0))]);
    let via_origin = flood_cells_for(&[flood_bsp(
        6.0,
        Some(Vec3::new(0.001, 0.0, 0.0)),
        Vec3::new(0.0, 30.0, 0.0),
        Quat::IDENTITY,
    )]);
    assert_ne!(concentric, via_bounds);
    assert_eq!(via_origin, via_bounds);
}

#[test]
fn flood_spheres_caps_cylinders_at_ten_but_never_bsp_parts() {
    let far = Vec3::new(0.0, 72.0, 0.0);
    let own = LB | (8 + 1 + 1);
    let far_cell = LB | (8 + 4 + 1);
    let mut bsp: Vec<ShadowShape> = (0..10)
        .map(|_| fbc(1.0, Vec3::new(0.001, 0.0, 0.0)))
        .collect();
    bsp.push(fbc(1.0, far));
    let bsp_cells = flood_cells_for(&bsp);
    let mut cyls: Vec<ShadowShape> = (0..10).map(|_| fcyl(1.0, Vec3::ZERO)).collect();
    cyls.push(fcyl(1.0, far));
    let cyl_cells = flood_cells_for(&cyls);
    assert!(bsp_cells.contains(&own));
    assert!(cyl_cells.contains(&own));
    assert!(bsp_cells.contains(&far_cell));
    assert!(!cyl_cells.contains(&far_cell));
}

#[test]
fn flood_spheres_sphere_branch_is_never_capped_at_ten() {
    let mut spheres: Vec<ShadowShape> = (0..10).map(|_| fsph(1.0, Vec3::ZERO)).collect();
    spheres.push(fsph(1.0, Vec3::new(0.0, 72.0, 0.0)));
    let cells = flood_cells_for(&spheres);
    assert!(cells.contains(&(LB | (8 + 1 + 1))));
    assert!(cells.contains(&(LB | (8 + 4 + 1))));
}

#[test]
fn flood_spheres_bsp_shape_rotates_bounds_centre_by_part_rotation() {
    let yaw180 = Quat::from_axis_angle(Vec3::Z, std::f32::consts::PI);
    let yawed = flood_cells_for(&[flood_bsp(
        6.0,
        Some(Vec3::new(0.0, 20.0, 0.0)),
        Vec3::ZERO,
        yaw180,
    )]);
    let negated = flood_cells_for(&[fbc(6.0, Vec3::new(0.0, -20.0, 0.0))]);
    let unrotated = flood_cells_for(&[fbc(6.0, Vec3::new(0.0, 20.0, 0.0))]);
    assert_ne!(unrotated, negated);
    assert_eq!(negated, yawed);
}

fn dv(x: f32, y: f32, z: f32) -> DatVec3 {
    DatVec3 { x, y, z }
}

fn empty_setup() -> Setup {
    Setup {
        id: 0,
        flags: SetupFlags::default(),
        parts: Vec::new(),
        parent_index: Vec::new(),
        default_scale: Vec::new(),
        holding_locations: Vec::new(),
        connection_points: Vec::new(),
        placement_frames: Vec::new(),
        cyl_spheres: Vec::new(),
        spheres: Vec::new(),
        height: 0.0,
        radius: 0.0,
        step_up_height: 0.0,
        step_down_height: 0.0,
        sorting_sphere: DatSphere::default(),
        selection_sphere: DatSphere::default(),
        lights: Vec::new(),
        default_animation: 0,
        default_script: 0,
        default_motion_table: 0,
        default_sound_table: 0,
        default_script_table: 0,
    }
}

#[test]
fn from_setup_cylsphere_and_bsp_setup_floods_the_bsp_footprint() {
    const PART: u32 = 0x0100_44B5;
    let mut setup = empty_setup();
    setup.parts = vec![PART];
    setup.cyl_spheres = vec![CylSphere {
        origin: dv(0.0, 0.0, 0.0),
        radius: 0.5,
        height: 1.0,
    }];
    let bounds = |id: u32| {
        (id == PART)
            .then(|| ShadowPartGeometry::new(Sphere::new(Vec3::new(0.0, 18.0, 0.0), 14.0), None))
    };
    let shapes = from_setup(
        &setup,
        1.0,
        &|id| id == PART,
        None,
        None,
        Some(&bounds),
        None,
    );
    assert_eq!(shapes.len(), 1);
    assert_eq!(shapes[0].collision_type, ShadowCollisionType::Bsp);
    assert_eq!(shapes[0].radius, 14.0);
    assert_eq!(shapes[0].bounds_center, Vec3::new(0.0, 18.0, 0.0));

    let mut reg = ShadowObjectRegistry::new();
    reg_multi(
        &mut reg,
        &CollisionWorld::new(),
        0xBEEF02,
        FLOOD_POS,
        &shapes,
        0x10008,
        0,
        false,
        None,
    );
    let cells = outdoor_cells_holding(&reg, 0xBEEF02);
    assert!(cells.len() > 1);
    assert!(cells.contains(&(LB | (8 + 2 + 1))));
    assert!(!cells.contains(&(LB | (8 + 1))));
}

// ── ShadowObjectRegistryRetailCellArrayTests ───────────────────────────

const POS: Vec3 = Vec3::new(12.0, 12.0, 50.0);

fn rbsp(gfx: u32, local: Vec3, radius: f32) -> ShadowShape {
    bsp_shape(gfx, local, Vec3::ZERO, radius)
}

fn rcyl(gfx: u32, local: Vec3) -> ShadowShape {
    cyl_shape(gfx, local, 1.0, 1.0)
}

fn retail_cells(reg: &ShadowObjectRegistry, id: u32) -> Option<Vec<u32>> {
    reg.retail_cell_array(id).map(<[u32]>::to_vec)
}

fn part_rows(reg: &ShadowObjectRegistry, cell: u32, id: u32) -> Vec<(usize, u32, bool)> {
    let mut rows: Vec<(usize, u32, bool)> = reg
        .retail_part_entries_in_cell(cell)
        .iter()
        .filter(|e| e.entity_id == id)
        .map(|e| (e.part_index, e.gfx_obj_id, e.clip_planes_required))
        .collect();
    rows.sort_by_key(|r| r.0);
    rows
}

#[test]
fn register_multi_part_without_part_array_no_retail_product() {
    let mut reg = ShadowObjectRegistry::new();
    reg_multi(
        &mut reg,
        &CollisionWorld::new(),
        0x10,
        POS,
        &[rbsp(0x0100_0001, Vec3::ZERO, 1.0)],
        0,
        0,
        false,
        None,
    );
    assert!(reg.retail_cell_array(0x10).is_none());
    assert_eq!(
        reg.retail_cell_array_route(0x10),
        RetailCellArrayRoute::None
    );
    for cell in reg.owner_cells(0x10) {
        assert!(reg.retail_part_entries_in_cell(*cell).is_empty());
    }
}

#[test]
fn register_single_shape_without_part_array_no_retail_product() {
    let (mut reg, w) = (ShadowObjectRegistry::new(), CollisionWorld::new());
    reg_one(
        &mut reg,
        &w,
        0x11,
        One {
            gfx: 0x0100_0002,
            pos: POS,
            ..One::default()
        },
    );
    assert!(reg.retail_cell_array(0x11).is_none());
    assert_eq!(
        reg.retail_cell_array_route(0x11),
        RetailCellArrayRoute::None
    );
}

#[test]
fn register_multi_part_with_part_array_bounding_box_route_matches_collision_flood() {
    let w = CollisionWorld::new();
    let parts = [rbsp(0x0100_0044, Vec3::ZERO, 2.0)];
    let mut with = ShadowObjectRegistry::new();
    let mut without = ShadowObjectRegistry::new();
    reg_multi(
        &mut with,
        &w,
        0x12,
        POS,
        &parts,
        0,
        CELL_A,
        true,
        Some(&parts),
    );
    reg_multi(&mut without, &w, 0x12, POS, &parts, 0, CELL_A, true, None);
    assert_eq!(
        with.retail_cell_array_route(0x12),
        RetailCellArrayRoute::BoundingBox
    );
    let cells = retail_cells(&with, 0x12).unwrap();
    assert!(!cells.is_empty());
    assert_eq!(with.owner_cells(0x12), cells.as_slice());
    assert_eq!(without.owner_cells(0x12), with.owner_cells(0x12));
}

#[test]
fn register_multi_part_route_dispatches_on_state_bit_and_cylinder_presence() {
    let w = CollisionWorld::new();
    let mut reg = ShadowObjectRegistry::new();
    let mixed = [
        rcyl(0x3001, Vec3::ZERO),
        rbsp(0x3002, Vec3::new(30.0, 0.0, 0.0), 1.0),
    ];
    reg_multi(&mut reg, &w, 0x13, POS, &mixed, 0, 0, false, Some(&mixed));
    assert_eq!(
        reg.retail_cell_array_route(0x13),
        RetailCellArrayRoute::Cylsphere
    );
    assert_eq!(retail_cells(&reg, 0x13).unwrap(), vec![CELL_A]);
    assert_eq!(
        part_rows(&reg, CELL_A, 0x13),
        vec![(0, 0x3001, false), (1, 0x3002, false)]
    );

    reg_multi(
        &mut reg,
        &w,
        0x13,
        POS,
        &mixed,
        0x10000,
        0,
        false,
        Some(&mixed),
    );
    assert_eq!(
        reg.retail_cell_array_route(0x13),
        RetailCellArrayRoute::BoundingBox
    );
    let cells = retail_cells(&reg, 0x13).unwrap();
    assert_eq!(cells, vec![CELL_A, CELL_B]);
    for cell in cells {
        assert_eq!(
            part_rows(&reg, cell, 0x13),
            vec![(0, 0x3001, true), (1, 0x3002, true)]
        );
    }
}

#[test]
fn register_multi_part_route_is_decided_by_collision_cylinders_not_visual_parts() {
    let w = CollisionWorld::new();
    let mut reg = ShadowObjectRegistry::new();
    let visual = [rbsp(0x3002, Vec3::new(30.0, 0.0, 0.0), 1.0)];
    reg_multi(
        &mut reg,
        &w,
        0x14,
        POS,
        &[rcyl(0x3001, Vec3::ZERO)],
        0,
        0,
        false,
        Some(&visual),
    );
    assert_eq!(
        reg.retail_cell_array_route(0x14),
        RetailCellArrayRoute::Cylsphere
    );
    assert_eq!(retail_cells(&reg, 0x14).unwrap(), vec![CELL_A]);
    let rows = part_rows(&reg, CELL_A, 0x14);
    assert_eq!(rows.len(), 1);
    assert_eq!(rows[0].1, 0x3002);

    reg_multi(
        &mut reg,
        &w,
        0x14,
        POS,
        &[rbsp(0x3001, Vec3::ZERO, 1.0)],
        0,
        0,
        false,
        Some(&visual),
    );
    assert_eq!(
        reg.retail_cell_array_route(0x14),
        RetailCellArrayRoute::BoundingBox
    );
}

#[test]
fn deregister_clears_retail_cell_array_and_every_per_cell_entry() {
    let mut reg = ShadowObjectRegistry::new();
    let parts = [
        rcyl(0x4001, Vec3::ZERO),
        rbsp(0x4002, Vec3::new(30.0, 0.0, 0.0), 1.0),
    ];
    reg_multi(
        &mut reg,
        &CollisionWorld::new(),
        0x14,
        POS,
        &parts,
        0x10000,
        0,
        false,
        Some(&parts),
    );
    let tracked = retail_cells(&reg, 0x14).unwrap();
    assert!(!tracked.is_empty());
    reg.deregister(0x14);
    assert!(reg.retail_cell_array(0x14).is_none());
    assert_eq!(
        reg.retail_cell_array_route(0x14),
        RetailCellArrayRoute::None
    );
    for cell in tracked {
        assert!(reg.retail_part_entries_in_cell(cell).is_empty());
    }
}

#[test]
fn update_position_with_retained_part_array_recomputes_retail_cell_array() {
    let w = CollisionWorld::new();
    let mut reg = ShadowObjectRegistry::new();
    let parts = [rcyl(0x5001, Vec3::ZERO)];
    reg_multi(&mut reg, &w, 0x15, POS, &parts, 0, 0, false, Some(&parts));
    assert_eq!(retail_cells(&reg, 0x15).unwrap(), vec![CELL_A]);
    move_to(&mut reg, &w, 0x15, p(42.0, 12.0), 0);
    assert_eq!(retail_cells(&reg, 0x15).unwrap(), vec![CELL_B]);
    assert_eq!(part_rows(&reg, CELL_B, 0x15), vec![(0, 0x5001, false)]);
    assert!(part_rows(&reg, CELL_A, 0x15).is_empty());
}

fn commit(
    reg: &mut ShadowObjectRegistry,
    id: u32,
    pos: Vec3,
    rot: Quat,
    seed: u32,
    action: PhysicsShadowCommitAction,
    cross: &[u32],
) {
    reg.commit_set_position(
        &CollisionWorld::new(),
        id,
        pos,
        rot,
        seed,
        0.0,
        0.0,
        action,
        cross,
    )
    .unwrap();
}

#[test]
fn commit_set_position_none_publishes_retail_product_from_retained_cells() {
    let mut reg = ShadowObjectRegistry::new();
    let parts = [rcyl(0x6001, Vec3::ZERO)];
    reg_multi(
        &mut reg,
        &CollisionWorld::new(),
        0x16,
        POS,
        &parts,
        0,
        0,
        false,
        Some(&parts),
    );
    commit(
        &mut reg,
        0x16,
        p(42.0, 12.0),
        Quat::IDENTITY,
        CELL_B,
        PhysicsShadowCommitAction::None,
        &[],
    );
    assert_eq!(reg.owner_cells(0x16), &[CELL_A]);
    assert_eq!(retail_cells(&reg, 0x16).unwrap(), reg.owner_cells(0x16));
}

#[test]
fn commit_set_position_replace_publishes_retail_product_from_transition_cells() {
    let mut reg = ShadowObjectRegistry::new();
    let parts = [rcyl(0x6101, Vec3::ZERO)];
    reg_multi(
        &mut reg,
        &CollisionWorld::new(),
        0x18,
        POS,
        &parts,
        0,
        0,
        false,
        Some(&parts),
    );
    commit(
        &mut reg,
        0x18,
        p(42.0, 12.0),
        Quat::IDENTITY,
        CELL_B,
        PhysicsShadowCommitAction::Replace,
        &[CELL_B],
    );
    assert_eq!(reg.owner_cells(0x18), &[CELL_B]);
    assert_eq!(retail_cells(&reg, 0x18).unwrap(), reg.owner_cells(0x18));
}

#[test]
fn commit_set_position_preserve_leaves_both_products_untouched() {
    let mut reg = ShadowObjectRegistry::new();
    let parts = [rcyl(0x6201, Vec3::ZERO)];
    reg_multi(
        &mut reg,
        &CollisionWorld::new(),
        0x19,
        POS,
        &parts,
        0,
        0,
        false,
        Some(&parts),
    );
    commit(
        &mut reg,
        0x19,
        POS,
        Quat::IDENTITY,
        CELL_A,
        PhysicsShadowCommitAction::Preserve,
        &[],
    );
    assert_eq!(reg.owner_cells(0x19), &[CELL_A]);
    assert_eq!(retail_cells(&reg, 0x19).unwrap(), vec![CELL_A]);
}

#[test]
fn replace_multi_part_payload_swaps_part_array_without_reflooding() {
    let w = CollisionWorld::new();
    let mut reg = ShadowObjectRegistry::new();
    let initial = [rcyl(0x7001, Vec3::ZERO)];
    reg_multi(
        &mut reg,
        &w,
        0x17,
        POS,
        &initial,
        0,
        0,
        false,
        Some(&initial),
    );
    let before = retail_cells(&reg, 0x17).unwrap();
    assert_eq!(before, vec![CELL_A]);
    assert_eq!(
        reg.retail_cell_array_route(0x17),
        RetailCellArrayRoute::Cylsphere
    );
    let replacement = [rcyl(0x7002, Vec3::ZERO), rcyl(0x7003, Vec3::ZERO)];
    replace(
        &mut reg,
        &w,
        0x17,
        POS,
        &replacement,
        LB,
        0,
        false,
        Some(&replacement),
    );
    assert_eq!(retail_cells(&reg, 0x17).unwrap(), before);
    assert_eq!(
        reg.retail_cell_array_route(0x17),
        RetailCellArrayRoute::Cylsphere
    );
    let rows = part_rows(&reg, CELL_A, 0x17);
    assert_eq!(
        rows.iter().map(|r| r.1).collect::<Vec<_>>(),
        vec![0x7002, 0x7003]
    );
}

#[test]
fn register_multi_part_decorative_part_array_entry_widens_collision_rows_too() {
    const COLLIDING: u32 = 0xAAAA_0001;
    const DECORATIVE: u32 = 0xBBBB_0001;
    let mut reg = ShadowObjectRegistry::new();
    let parts = [
        rbsp(COLLIDING, Vec3::ZERO, 1.0),
        rbsp(DECORATIVE, Vec3::new(30.0, 0.0, 0.0), 1.0),
    ];
    reg_multi(
        &mut reg,
        &CollisionWorld::new(),
        0x20,
        POS,
        &[rbsp(COLLIDING, Vec3::ZERO, 1.0)],
        0,
        0,
        false,
        Some(&parts),
    );
    let cells = retail_cells(&reg, 0x20).unwrap();
    assert_eq!(cells, vec![CELL_A, CELL_B]);
    assert_eq!(
        reg.retail_cell_array_route(0x20),
        RetailCellArrayRoute::BoundingBox
    );
    assert_eq!(reg.owner_cells(0x20), cells.as_slice());
    for cell in [CELL_A, CELL_B] {
        let rows = rows_of(&reg, cell, 0x20);
        assert_eq!(rows.len(), 1);
        assert_eq!(rows[0].gfx_obj_id, COLLIDING);
        let drawn: Vec<u32> = part_rows(&reg, cell, 0x20).iter().map(|r| r.1).collect();
        assert!(drawn.contains(&COLLIDING));
        assert!(drawn.contains(&DECORATIVE));
    }
}

#[test]
fn register_multi_part_without_decorative_parts_retail_array_equals_collision_cells() {
    let w = CollisionWorld::new();
    let shapes = [rbsp(0x0100_0055, Vec3::ZERO, 2.0)];
    let mut with = ShadowObjectRegistry::new();
    let mut without = ShadowObjectRegistry::new();
    reg_multi(
        &mut with,
        &w,
        0x21,
        POS,
        &shapes,
        0,
        CELL_A,
        true,
        Some(&shapes),
    );
    reg_multi(&mut without, &w, 0x21, POS, &shapes, 0, CELL_A, true, None);
    assert_eq!(without.owner_cells(0x21), with.owner_cells(0x21));
    assert_eq!(retail_cells(&with, 0x21).unwrap(), with.owner_cells(0x21));
}

fn child_parts(ids: &[u32]) -> Vec<ShadowShape> {
    ids.iter().map(|id| rbsp(*id, Vec3::ZERO, 1.0)).collect()
}

fn root_with_cyl(reg: &mut ShadowObjectRegistry, id: u32, gfx: u32) {
    let parts = [rcyl(gfx, Vec3::ZERO)];
    reg_multi(
        reg,
        &CollisionWorld::new(),
        id,
        POS,
        &parts,
        0,
        0,
        false,
        Some(&parts),
    );
}

#[test]
fn attach_child_gives_child_the_roots_array_and_entries() {
    let mut reg = ShadowObjectRegistry::new();
    root_with_cyl(&mut reg, 0x40, 0x9001);
    let root_cells = retail_cells(&reg, 0x40).unwrap();
    assert_eq!(root_cells, vec![CELL_A]);
    assert!(reg.attach_child(0x41, 0x40, child_parts(&[0xC001, 0xC002])));
    assert_eq!(retail_cells(&reg, 0x41).unwrap(), root_cells);
    let rows: Vec<u32> = part_rows(&reg, CELL_A, 0x41).iter().map(|r| r.1).collect();
    assert_eq!(rows, vec![0xC001, 0xC002]);
    assert!(reg.owner_cells(0x41).is_empty());
    assert!(!holds(&reg, CELL_A, 0x41));
}

#[test]
fn attach_child_root_move_republishes_the_child() {
    let mut reg = ShadowObjectRegistry::new();
    root_with_cyl(&mut reg, 0x42, 0x9101);
    assert!(reg.attach_child(0x43, 0x42, child_parts(&[0xC101])));
    assert_eq!(retail_cells(&reg, 0x43).unwrap(), vec![CELL_A]);
    commit(
        &mut reg,
        0x42,
        p(42.0, 12.0),
        Quat::IDENTITY,
        CELL_B,
        PhysicsShadowCommitAction::Replace,
        &[CELL_B],
    );
    let root_after = retail_cells(&reg, 0x42).unwrap();
    assert_eq!(root_after, vec![CELL_B]);
    assert_eq!(retail_cells(&reg, 0x43).unwrap(), root_after);
    assert!(part_rows(&reg, CELL_A, 0x43).is_empty());
    assert_eq!(
        part_rows(&reg, CELL_B, 0x43)
            .iter()
            .map(|r| r.1)
            .collect::<Vec<_>>(),
        vec![0xC101]
    );
}

#[test]
fn detach_child_clears_the_childs_products() {
    let mut reg = ShadowObjectRegistry::new();
    root_with_cyl(&mut reg, 0x44, 0x9201);
    assert!(reg.attach_child(0x45, 0x44, child_parts(&[0xC201])));
    assert!(reg.retail_cell_array(0x45).is_some());
    assert!(reg.detach_child(0x45));
    assert!(reg.retail_cell_array(0x45).is_none());
    assert!(part_rows(&reg, CELL_A, 0x45).is_empty());
    assert_eq!(retail_cells(&reg, 0x44).unwrap(), vec![CELL_A]);
    assert!(!reg.detach_child(0x45));
}

#[test]
fn deregister_root_cascades_detach_of_attached_children() {
    let mut reg = ShadowObjectRegistry::new();
    root_with_cyl(&mut reg, 0x46, 0x9301);
    assert!(reg.attach_child(0x47, 0x46, child_parts(&[0xC301])));
    reg.deregister(0x46);
    assert!(reg.retail_cell_array(0x46).is_none());
    assert!(reg.retail_cell_array(0x47).is_none());
    assert!(part_rows(&reg, CELL_A, 0x47).is_empty());
}

#[test]
fn attach_child_nested_child_of_a_child_resolves_to_the_root() {
    let mut reg = ShadowObjectRegistry::new();
    root_with_cyl(&mut reg, 0x48, 0x9401);
    assert!(reg.attach_child(0x49, 0x48, child_parts(&[0xC401])));
    assert!(reg.attach_child(0x4A, 0x49, child_parts(&[0xC402])));
    assert_eq!(retail_cells(&reg, 0x4A), retail_cells(&reg, 0x48));
    assert_eq!(
        part_rows(&reg, CELL_A, 0x4A)
            .iter()
            .map(|r| r.1)
            .collect::<Vec<_>>(),
        vec![0xC402]
    );
    commit(
        &mut reg,
        0x48,
        p(42.0, 12.0),
        Quat::IDENTITY,
        CELL_B,
        PhysicsShadowCommitAction::Replace,
        &[CELL_B],
    );
    assert_eq!(retail_cells(&reg, 0x4A).unwrap(), vec![CELL_B]);
    assert!(reg.detach_child(0x49));
    assert!(reg.retail_cell_array(0x4A).is_none());
}

#[test]
fn attach_child_self_attach_rejected() {
    let mut reg = ShadowObjectRegistry::new();
    root_with_cyl(&mut reg, 0x4B, 0x9501);
    assert!(!reg.attach_child(0x4B, 0x4B, child_parts(&[0xC501])));
}

#[test]
fn attach_child_cycle_rejected_root_unaffected() {
    let mut reg = ShadowObjectRegistry::new();
    root_with_cyl(&mut reg, 0x4C, 0x9601);
    assert!(reg.attach_child(0x4D, 0x4C, child_parts(&[0xC601])));
    let before = retail_cells(&reg, 0x4C);
    assert!(!reg.attach_child(0x4C, 0x4D, child_parts(&[0xC602])));
    assert_eq!(retail_cells(&reg, 0x4C), before);
    assert_eq!(reg.owner_cells(0x4C), &[CELL_A]);
    assert_eq!(retail_cells(&reg, 0x4D), before);
}

fn render_only(
    reg: &mut ShadowObjectRegistry,
    id: u32,
    gfx: u32,
    radius: f32,
    seed: u32,
    is_static: bool,
) {
    let parts = [rbsp(gfx, Vec3::ZERO, radius)];
    reg_multi(
        reg,
        &CollisionWorld::new(),
        id,
        POS,
        &[],
        0,
        seed,
        is_static,
        Some(&parts),
    );
}

#[test]
fn register_multi_part_empty_shapes_with_part_array_registers_render_only() {
    let mut reg = ShadowObjectRegistry::new();
    render_only(&mut reg, 0x50, 0x0100_0060, 2.0, CELL_A, true);
    assert_eq!(retail_cells(&reg, 0x50).unwrap(), vec![CELL_A]);
    assert_eq!(
        reg.retail_cell_array_route(0x50),
        RetailCellArrayRoute::BoundingBox
    );
    assert_eq!(
        part_rows(&reg, CELL_A, 0x50)
            .iter()
            .map(|r| r.1)
            .collect::<Vec<_>>(),
        vec![0x0100_0060]
    );
    assert!(reg.owner_cells(0x50).is_empty());
}

#[test]
fn register_multi_part_empty_shapes_and_no_part_array_still_deregisters() {
    let w = CollisionWorld::new();
    let mut reg = ShadowObjectRegistry::new();
    let parts = [rbsp(0x0100_0061, Vec3::ZERO, 1.0)];
    reg_multi(
        &mut reg,
        &w,
        0x51,
        POS,
        &parts,
        0,
        CELL_A,
        true,
        Some(&parts),
    );
    assert!(reg.retail_cell_array(0x51).is_some());
    reg_multi(&mut reg, &w, 0x51, POS, &[], 0, CELL_A, true, None);
    assert!(reg.retail_cell_array(0x51).is_none());
    assert!(reg.owner_cells(0x51).is_empty());
    assert!(reg.retail_part_entries_in_cell(CELL_A).is_empty());
}

#[test]
fn register_multi_part_render_only_keep_when_empty_preserves_prior_registration() {
    let w = CollisionWorld::new();
    let mut reg = ShadowObjectRegistry::new();
    let parts = [rbsp(0x0100_0062, Vec3::ZERO, 2.0)];
    reg_multi(&mut reg, &w, 0x52, POS, &[], 0, CELL_A, true, Some(&parts));
    let before = retail_cells(&reg, 0x52).unwrap();
    assert_eq!(before, vec![CELL_A]);
    reg.register_multi_part(
        &w,
        0x52,
        &[],
        site_at(POS, Quat::IDENTITY, (0.0, 0.0), 0, 0),
        0,
        EntityCollisionFlags::default(),
        true,
        Some(&parts),
    )
    .unwrap();
    assert_eq!(retail_cells(&reg, 0x52).unwrap(), before);
    assert!(!reg.retail_part_entries_in_cell(CELL_A).is_empty());
}

#[test]
fn register_multi_part_render_only_deregister_clears_every_product() {
    let mut reg = ShadowObjectRegistry::new();
    render_only(&mut reg, 0x53, 0x0100_0063, 2.0, CELL_A, true);
    assert!(reg.retail_cell_array(0x53).is_some());
    reg.deregister(0x53);
    assert!(reg.retail_cell_array(0x53).is_none());
    assert!(reg.retail_part_entries_in_cell(CELL_A).is_empty());
}

#[test]
fn update_position_render_only_recomputes_retail_cell_array() {
    let w = CollisionWorld::new();
    let mut reg = ShadowObjectRegistry::new();
    render_only(&mut reg, 0x54, 0x0100_0064, 2.0, CELL_A, true);
    move_to(&mut reg, &w, 0x54, p(42.0, 12.0), 0);
    assert_eq!(retail_cells(&reg, 0x54).unwrap(), vec![CELL_B]);
    assert!(part_rows(&reg, CELL_A, 0x54).is_empty());
    assert_eq!(
        part_rows(&reg, CELL_B, 0x54)
            .iter()
            .map(|r| r.1)
            .collect::<Vec<_>>(),
        vec![0x0100_0064]
    );
    assert!(reg.owner_cells(0x54).is_empty());
}

#[test]
fn reflood_landblock_keeps_the_retail_product_of_a_collision_owner_with_part_array() {
    let w = CollisionWorld::new();
    let mut reg = ShadowObjectRegistry::new();
    let parts = [
        rbsp(0x0100_0070, Vec3::ZERO, 2.0),
        rbsp(0x0100_0071, Vec3::new(0.5, 0.0, 0.0), 1.0),
    ];
    reg_multi(
        &mut reg,
        &w,
        0x60,
        POS,
        &[rbsp(0x0100_0070, Vec3::ZERO, 2.0)],
        0,
        CELL_A,
        true,
        Some(&parts),
    );
    let before = retail_cells(&reg, 0x60);
    let before_rows: Vec<u32> = part_rows(&reg, CELL_A, 0x60).iter().map(|r| r.1).collect();
    assert_eq!(before_rows, vec![0x0100_0070, 0x0100_0071]);
    reg.reflood_landblock(&w, LB).unwrap();
    assert_eq!(retail_cells(&reg, 0x60), before);
    assert_eq!(
        reg.retail_cell_array_route(0x60),
        RetailCellArrayRoute::BoundingBox
    );
    assert_eq!(
        part_rows(&reg, CELL_A, 0x60)
            .iter()
            .map(|r| r.1)
            .collect::<Vec<_>>(),
        before_rows
    );
    assert_eq!(reg.owner_cells(0x60), &[CELL_A]);
}

#[test]
fn reflood_landblock_keeps_a_render_only_owner() {
    let w = CollisionWorld::new();
    let mut reg = ShadowObjectRegistry::new();
    render_only(&mut reg, 0x61, 0x0100_0072, 2.0, CELL_A, true);
    reg.reflood_landblock(&w, LB).unwrap();
    assert_eq!(retail_cells(&reg, 0x61).unwrap(), vec![CELL_A]);
    assert_eq!(
        part_rows(&reg, CELL_A, 0x61)
            .iter()
            .map(|r| r.1)
            .collect::<Vec<_>>(),
        vec![0x0100_0072]
    );
    assert!(reg.owner_cells(0x61).is_empty());
}

#[test]
fn suspend_clears_the_retail_product_and_the_unsuspending_move_republishes_it() {
    let mut reg = ShadowObjectRegistry::new();
    let parts = [rbsp(0x0100_0080, Vec3::ZERO, 2.0)];
    reg_multi(
        &mut reg,
        &CollisionWorld::new(),
        0x70,
        POS,
        &parts,
        0,
        CELL_A,
        false,
        Some(&parts),
    );
    assert_eq!(part_rows(&reg, CELL_A, 0x70).len(), 1);
    assert!(reg.suspend(0x70));
    assert!(reg.retail_cell_array(0x70).is_none());
    assert!(part_rows(&reg, CELL_A, 0x70).is_empty());
    assert!(reg.owner_cells(0x70).is_empty());
    assert_eq!(
        reg.retail_cell_array_route(0x70),
        RetailCellArrayRoute::BoundingBox
    );
    commit(
        &mut reg,
        0x70,
        POS,
        Quat::IDENTITY,
        CELL_A,
        PhysicsShadowCommitAction::None,
        &[],
    );
    assert_eq!(reg.owner_cells(0x70), &[CELL_A]);
    assert_eq!(retail_cells(&reg, 0x70).unwrap(), vec![CELL_A]);
    assert_eq!(part_rows(&reg, CELL_A, 0x70).len(), 1);
}

#[test]
fn attach_child_and_detach_child_advance_the_cell_array_revision() {
    let mut reg = ShadowObjectRegistry::new();
    let parts = [rbsp(0x0100_0081, Vec3::ZERO, 2.0)];
    reg_multi(
        &mut reg,
        &CollisionWorld::new(),
        0x71,
        POS,
        &parts,
        0,
        CELL_A,
        false,
        Some(&parts),
    );
    let before = reg.retail_cell_array_revision();
    assert!(reg.attach_child(0x72, 0x71, vec![rbsp(0x0100_0082, Vec3::ZERO, 1.0)]));
    assert!(reg.retail_cell_array_revision() > before);
    let before = reg.retail_cell_array_revision();
    assert!(reg.detach_child(0x72));
    assert!(reg.retail_cell_array_revision() > before);
}

#[test]
fn register_multi_part_of_an_attached_child_keeps_inheriting_the_roots_cells() {
    let w = CollisionWorld::new();
    let mut reg = ShadowObjectRegistry::new();
    let parts = [rbsp(0x0100_0083, Vec3::ZERO, 2.0)];
    reg_multi(
        &mut reg,
        &w,
        0x73,
        POS,
        &parts,
        0,
        CELL_A,
        false,
        Some(&parts),
    );
    assert!(reg.attach_child(0x74, 0x73, vec![rbsp(0x0100_0084, Vec3::ZERO, 1.0)]));
    assert_eq!(part_rows(&reg, CELL_A, 0x74).len(), 1);
    let new_parts = [rbsp(0x0100_0085, Vec3::ZERO, 1.0)];
    reg_multi(
        &mut reg,
        &w,
        0x74,
        p(42.0, 12.0),
        &new_parts,
        0,
        CELL_B,
        false,
        Some(&new_parts),
    );
    assert_eq!(retail_cells(&reg, 0x74).unwrap(), vec![CELL_A]);
    assert!(part_rows(&reg, CELL_B, 0x74).is_empty());
    assert_eq!(
        part_rows(&reg, CELL_A, 0x74)
            .iter()
            .map(|r| r.1)
            .collect::<Vec<_>>(),
        vec![0x0100_0085]
    );
}

#[test]
fn remove_landblock_clears_the_retail_product_including_render_only_statics() {
    let w = CollisionWorld::new();
    let mut reg = ShadowObjectRegistry::new();
    let parts = [rbsp(0x0100_0086, Vec3::ZERO, 2.0)];
    reg_multi(
        &mut reg,
        &w,
        0x75,
        POS,
        &parts,
        0,
        CELL_A,
        true,
        Some(&parts),
    );
    reg_multi(&mut reg, &w, 0x76, POS, &[], 0, CELL_A, true, Some(&parts));
    assert_eq!(part_rows(&reg, CELL_A, 0x76).len(), 1);
    reg.remove_landblock(LB);
    assert!(reg.retail_cell_array(0x75).is_none());
    assert!(reg.retail_cell_array(0x76).is_none());
    assert!(reg.retail_part_entries_in_cell(CELL_A).is_empty());
    assert!(!reg.suspend(0x75));
    assert!(!reg.suspend(0x76));
}

#[test]
fn retire_owner_from_landblock_prunes_retail_rows_of_a_non_rooted_owner() {
    let mut reg = ShadowObjectRegistry::new();
    let parts = [rbsp(0x0100_0087, Vec3::ZERO, 2.0)];
    reg_multi(
        &mut reg,
        &CollisionWorld::new(),
        0x77,
        POS,
        &parts,
        0,
        CELL_A,
        false,
        Some(&parts),
    );
    assert_eq!(part_rows(&reg, CELL_A, 0x77).len(), 1);
    reg.retire_owner_from_landblock(0x77, LB);
    assert!(part_rows(&reg, CELL_A, 0x77).is_empty());
    assert!(reg.retail_cell_array(0x77).is_none());
    assert!(reg.owner_cells(0x77).is_empty());
    assert!(reg.suspend(0x77));
}

#[test]
fn replace_multi_part_payload_with_an_empty_part_array_keeps_prior_rows() {
    let w = CollisionWorld::new();
    let mut reg = ShadowObjectRegistry::new();
    let parts = [rbsp(0x0100_0088, Vec3::ZERO, 2.0)];
    reg_multi(
        &mut reg,
        &w,
        0x78,
        POS,
        &parts,
        0,
        CELL_A,
        false,
        Some(&parts),
    );
    replace(
        &mut reg,
        &w,
        0x78,
        POS,
        &parts,
        LB,
        CELL_A,
        false,
        Some(&[]),
    );
    assert_eq!(retail_cells(&reg, 0x78).unwrap(), vec![CELL_A]);
    assert_eq!(part_rows(&reg, CELL_A, 0x78).len(), 1);
}

#[test]
fn commit_set_position_none_moves_a_render_only_owner_to_its_destination_cell() {
    let mut reg = ShadowObjectRegistry::new();
    render_only(&mut reg, 0x79, 0x0100_0089, 1.0, CELL_A, false);
    assert_eq!(part_rows(&reg, CELL_A, 0x79).len(), 1);
    commit(
        &mut reg,
        0x79,
        p(42.0, 12.0),
        Quat::IDENTITY,
        CELL_B,
        PhysicsShadowCommitAction::None,
        &[],
    );
    assert_eq!(retail_cells(&reg, 0x79).unwrap(), vec![CELL_B]);
    assert!(part_rows(&reg, CELL_A, 0x79).is_empty());
    assert_eq!(part_rows(&reg, CELL_B, 0x79).len(), 1);
    assert!(reg.owner_cells(0x79).is_empty());
}

// ── ShadowSetPositionCommitTests (direct commits) ──────────────────────

fn registered_single() -> ShadowObjectRegistry {
    let (mut reg, w) = (ShadowObjectRegistry::new(), CollisionWorld::new());
    reg_one(
        &mut reg,
        &w,
        1,
        One {
            gfx: 0x0100_0001,
            pos: POS,
            seed: CELL_A,
            is_static: false,
            ..One::default()
        },
    );
    reg
}

#[test]
fn commit_none_refreshes_frame_without_changing_membership() {
    let mut reg = registered_single();
    let moved = p(14.0, 12.0);
    commit(
        &mut reg,
        1,
        moved,
        Quat::IDENTITY,
        CELL_A,
        PhysicsShadowCommitAction::None,
        &[],
    );
    let rows = reg.objects_in_cell(CELL_A);
    assert_eq!(rows.len(), 1);
    assert_eq!(rows[0].position, moved);
    assert!(reg.objects_in_cell(CELL_B).is_empty());
}

#[test]
fn commit_replace_deduplicates_exact_cells_and_preserve_retains_them() {
    let mut reg = registered_single();
    let replaced = p(36.0, 12.0);
    commit(
        &mut reg,
        1,
        replaced,
        Quat::IDENTITY,
        CELL_B,
        PhysicsShadowCommitAction::Replace,
        &[CELL_B, CELL_B, 0],
    );
    assert!(reg.objects_in_cell(CELL_A).is_empty());
    assert_eq!(reg.objects_in_cell(CELL_B).len(), 1);
    assert_eq!(reg.objects_in_cell(CELL_B)[0].position, replaced);

    let preserved = p(38.0, 12.0);
    commit(
        &mut reg,
        1,
        preserved,
        Quat::IDENTITY,
        CELL_B,
        PhysicsShadowCommitAction::Preserve,
        &[],
    );
    assert_eq!(reg.objects_in_cell(CELL_B).len(), 1);
    assert_eq!(reg.objects_in_cell(CELL_B)[0].position, preserved);

    let empty_replace = p(40.0, 12.0);
    commit(
        &mut reg,
        1,
        empty_replace,
        Quat::IDENTITY,
        CELL_B,
        PhysicsShadowCommitAction::Replace,
        &[],
    );
    assert_eq!(reg.objects_in_cell(CELL_B).len(), 1);
    assert_eq!(reg.objects_in_cell(CELL_B)[0].position, empty_replace);
}

#[test]
fn commit_recalculate_uses_canonical_flood_instead_of_retained_cells() {
    let mut reg = registered_single();
    let moved = p(36.0, 12.0);
    commit(
        &mut reg,
        1,
        moved,
        Quat::IDENTITY,
        CELL_B,
        PhysicsShadowCommitAction::Recalculate,
        &[],
    );
    assert!(reg.objects_in_cell(CELL_A).is_empty());
    assert_eq!(reg.objects_in_cell(CELL_B).len(), 1);
    assert_eq!(reg.objects_in_cell(CELL_B)[0].position, moved);
}

#[test]
fn commit_suspended_preserve_restores_exact_rows() {
    let mut reg = registered_single();
    assert!(reg.suspend(1));
    assert_eq!(reg.total_registered(), 0);
    assert_eq!(reg.suspended_registration_count(), 1);
    let moved = p(15.0, 12.0);
    commit(
        &mut reg,
        1,
        moved,
        Quat::IDENTITY,
        CELL_A,
        PhysicsShadowCommitAction::Preserve,
        &[],
    );
    assert_eq!(reg.total_registered(), 1);
    assert_eq!(reg.suspended_registration_count(), 0);
    assert_eq!(reg.objects_in_cell(CELL_A)[0].position, moved);
}

#[test]
fn commit_multi_part_none_refreshes_each_part_from_root_transform() {
    let mut reg = ShadowObjectRegistry::new();
    let shapes = [
        bsp_shape(0x0100_0001, Vec3::X, Vec3::ZERO, 0.25),
        bsp_shape(0x0100_0002, Vec3::Y, Vec3::ZERO, 0.25),
    ];
    reg_multi(
        &mut reg,
        &CollisionWorld::new(),
        2,
        POS,
        &shapes,
        0,
        CELL_A,
        false,
        None,
    );
    let rotation = Quat::from_axis_angle(Vec3::Z, std::f32::consts::FRAC_PI_2);
    let root = p(14.0, 12.0);
    commit(
        &mut reg,
        2,
        root,
        rotation,
        CELL_A,
        PhysicsShadowCommitAction::None,
        &[],
    );
    let mut rows = rows_of(&reg, CELL_A, 2);
    rows.sort_by_key(|e| e.gfx_obj_id);
    assert_eq!(rows.len(), 2);
    assert_eq!(rows[0].position, root + rotation * shapes[0].local_position);
    assert_eq!(rows[1].position, root + rotation * shapes[1].local_position);
}

// ── ShadowShapeBuilderTests and ShadowShapeBuilderShapeSourceTests ─────

fn dat_identity_frame() -> DatFrame {
    DatFrame {
        origin: dv(0.0, 0.0, 0.0),
        orientation: DatQuat {
            w: 1.0,
            x: 0.0,
            y: 0.0,
            z: 0.0,
        },
    }
}

fn door_setup() -> Setup {
    let mut s = empty_setup();
    s.radius = 0.141;
    s.height = 0.2;
    s.step_up_height = 0.09;
    s.step_down_height = 0.09;
    s.parts = vec![0x0100_44B5, 0x0100_44B6, 0x0100_44B6];
    s.spheres = vec![DatSphere {
        origin: dv(0.0, 0.0, 0.018),
        radius: 0.1,
    }];
    s.placement_frames = vec![(
        0,
        AnimationFrame {
            frames: vec![dat_identity_frame(); 3],
            hooks: Vec::new(),
        },
    )];
    s
}

fn shapes_of(
    setup: &Setup,
    scale: f32,
    has_bsp: &dyn Fn(u32) -> bool,
    effective: Option<&[u32]>,
) -> Vec<ShadowShape> {
    from_setup(setup, scale, has_bsp, None, effective, None, None)
}

#[test]
fn from_setup_door_setup_emits_bsp_parts_only() {
    let shapes = shapes_of(
        &door_setup(),
        1.0,
        &|id| id == 0x0100_44B5 || id == 0x0100_44B6,
        None,
    );
    assert_eq!(shapes.len(), 3);
    assert!(
        shapes
            .iter()
            .all(|s| s.collision_type == ShadowCollisionType::Bsp)
    );
}

// ── HubDoorPoseInspectionTests (the pose override) ─────────────────────

/// OpenAC `HubDoorPoseInspectionTests.MakeTwoPartSetup`: a double door
/// whose placement frames stand the leaves open.
fn two_part_door() -> Setup {
    let mut s = empty_setup();
    s.parts = vec![0x0100_0001, 0x0100_0002];
    let frame = |x: f32, z: f32, w: f32| DatFrame {
        origin: dv(x, -0.44, 1.37),
        orientation: DatQuat {
            w,
            x: 0.0,
            y: 0.0,
            z,
        },
    };
    s.placement_frames = vec![(
        0,
        AnimationFrame {
            frames: vec![frame(0.88, -0.966, 0.259), frame(-0.88, -0.259, 0.966)],
            hooks: Vec::new(),
        },
    )];
    s
}

fn closed_leaf(x: f32) -> ac_motion::Frame {
    ac_motion::Frame {
        origin: Vec3::new(x, 0.0, 1.37),
        orientation: Quat::IDENTITY,
    }
}

/// OpenAC `HubDoorPoseInspectionTests.FromSetup_PartPoseOverride_ReplacesPlacementFrames`.
#[test]
fn from_setup_a_pose_override_replaces_the_placement_frames() {
    let closed = [closed_leaf(0.85), closed_leaf(-0.85)];
    let shapes = from_setup(
        &two_part_door(),
        1.0,
        &|_| true,
        Some(&closed),
        None,
        None,
        None,
    );
    assert_eq!(shapes.len(), 2);
    assert_eq!(shapes[0].local_position, Vec3::new(0.85, 0.0, 1.37));
    assert_eq!(shapes[0].local_rotation, Quat::IDENTITY);
    assert_eq!(shapes[1].local_position, Vec3::new(-0.85, 0.0, 1.37));
}

/// OpenAC `HubDoorPoseInspectionTests.FromSetup_NoOverride_KeepsPlacementFrames`.
#[test]
fn from_setup_without_an_override_keeps_the_placement_frames() {
    let shapes = from_setup(&two_part_door(), 1.0, &|_| true, None, None, None, None);
    assert_eq!(shapes.len(), 2);
    assert_eq!(shapes[0].local_position, Vec3::new(0.88, -0.44, 1.37));
    assert_eq!(
        shapes[0].local_rotation,
        Quat::from_xyzw(0.0, 0.0, -0.966, 0.259)
    );
}

/// An item with a resting frame (lifted 1 m), a hook frame (a quarter turn
/// about x) and, when asked, a default frame.
fn placed_item(with_default: bool) -> Setup {
    let mut s = empty_setup();
    s.parts = vec![0x0100_0001];
    let frame = |z: f32, q: Quat| AnimationFrame {
        frames: vec![DatFrame {
            origin: dv(0.0, 0.0, z),
            orientation: DatQuat {
                w: q.w,
                x: q.x,
                y: q.y,
                z: q.z,
            },
        }],
        hooks: Vec::new(),
    };
    s.placement_frames = vec![
        (0x65, frame(1.0, Quat::IDENTITY)),
        (
            0x67,
            frame(0.0, Quat::from_rotation_x(std::f32::consts::FRAC_PI_2)),
        ),
    ];
    if with_default {
        s.placement_frames.push((0, frame(0.0, Quat::IDENTITY)));
    }
    s
}

/// OpenAC `LiveEntityCollisionBuilderTests.Build_CollidesInThePlacementTheServerNamed`.
#[test]
fn from_setup_collides_in_the_placement_the_object_is_drawn_in() {
    let shapes = from_setup(
        &placed_item(true),
        1.0,
        &|_| true,
        None,
        None,
        None,
        Some(0x67),
    );
    assert_eq!(shapes.len(), 1);
    assert_eq!(shapes[0].local_position, Vec3::ZERO);
    assert!(
        shapes[0]
            .local_rotation
            .abs_diff_eq(Quat::from_rotation_x(std::f32::consts::FRAC_PI_2), 1e-6)
    );
}

/// OpenAC `LiveEntityCollisionBuilderTests.Build_WithoutAPlacementOrMotion_CollidesInDefault`.
#[test]
fn from_setup_with_the_default_placement_collides_in_default() {
    let shapes = from_setup(
        &placed_item(true),
        1.0,
        &|_| true,
        None,
        None,
        None,
        Some(0),
    );
    assert_eq!(shapes[0].local_position, Vec3::ZERO);
    assert_eq!(shapes[0].local_rotation, Quat::IDENTITY);
}

/// A placement the setup lacks, with no default either: no frame, the part
/// at the object's origin (not the resting frame).
#[test]
fn from_setup_without_the_placement_or_a_default_has_no_frame() {
    let shapes = from_setup(
        &placed_item(false),
        1.0,
        &|_| true,
        None,
        None,
        None,
        Some(0x01),
    );
    assert_eq!(shapes[0].local_position, Vec3::ZERO);
    assert_eq!(shapes[0].local_rotation, Quat::IDENTITY);
}

/// OpenAC `HubDoorPoseInspectionTests.FromSetup_ShortOverride_FallsBackPerPart`.
#[test]
fn from_setup_a_short_override_falls_back_part_by_part() {
    let short = [closed_leaf(0.85)];
    let shapes = from_setup(
        &two_part_door(),
        1.0,
        &|_| true,
        Some(&short),
        None,
        None,
        None,
    );
    assert_eq!(shapes.len(), 2);
    assert_eq!(shapes[0].local_position, Vec3::new(0.85, 0.0, 1.37));
    assert_eq!(shapes[1].local_position, Vec3::new(-0.88, -0.44, 1.37));
}

#[test]
fn from_setup_door_setup_sphere_at_expected_local_offset() {
    let shapes = shapes_of(&door_setup(), 1.0, &|_| false, None);
    assert_eq!(shapes.len(), 1);
    let s = shapes[0];
    assert_eq!(s.collision_type, ShadowCollisionType::Sphere);
    assert_eq_prec(0.0, s.local_position.x, 4);
    assert_eq_prec(0.0, s.local_position.y, 4);
    assert_eq_prec(0.018, s.local_position.z, 4);
    assert_eq_prec(0.1, s.radius, 4);
    assert_eq_prec(0.0, s.cyl_height, 4);
}

#[test]
fn from_setup_dispatch_gate_reads_the_effective_part_identities() {
    const BASE: u32 = 0x0100_44B5;
    const REPLACEMENT: u32 = 0x0100_AA01;
    let mut setup = empty_setup();
    setup.parts = vec![BASE];
    setup.cyl_spheres = vec![CylSphere {
        origin: dv(0.0, 0.0, 0.0),
        radius: 0.4,
        height: 1.2,
    }];
    let has_bsp = |id: u32| id == REPLACEMENT;
    let swapped = shapes_of(&setup, 1.0, &has_bsp, Some(&[REPLACEMENT]));
    assert_eq!(swapped.len(), 1);
    assert_eq!(swapped[0].collision_type, ShadowCollisionType::Bsp);
    assert_eq!(swapped[0].gfx_obj_id, REPLACEMENT);
    let unswapped = shapes_of(&setup, 1.0, &has_bsp, None);
    assert_eq!(unswapped.len(), 1);
    assert_eq!(unswapped[0].collision_type, ShadowCollisionType::Cylinder);
    assert_eq_prec(0.4, unswapped[0].radius, 4);
}

#[test]
fn from_setup_part_without_bsp_skips_bsp_shape() {
    let shapes = shapes_of(&door_setup(), 1.0, &|id| id == 0x0100_44B5, None);
    assert_eq!(
        shapes
            .iter()
            .filter(|s| s.collision_type == ShadowCollisionType::Bsp)
            .count(),
        1
    );
}

#[test]
fn from_setup_effective_part_identities_control_physics_bsp_selection() {
    const WITH: u32 = 0x0100_AA01;
    const WITHOUT: u32 = 0x0100_AA02;
    let mut setup = empty_setup();
    setup.parts = vec![0x0100_44B5, 0x0100_44B6];
    let shapes = shapes_of(&setup, 1.0, &|id| id == WITH, Some(&[WITH, WITHOUT]));
    assert_eq!(shapes.len(), 1);
    assert_eq!(shapes[0].gfx_obj_id, WITH);
}

#[test]
fn from_setup_creature_with_cylspheres_only_emits_cylinders() {
    let mut setup = empty_setup();
    setup.parts = vec![0x0200_0001];
    setup.cyl_spheres = vec![CylSphere {
        origin: dv(0.0, 0.0, 0.6),
        radius: 0.4,
        height: 1.2,
    }];
    setup.spheres = vec![DatSphere {
        origin: dv(0.0, 0.0, 0.7),
        radius: 0.5,
    }];
    let shapes = shapes_of(&setup, 1.0, &|_| false, None);
    assert_eq!(shapes.len(), 1);
    assert_eq!(shapes[0].collision_type, ShadowCollisionType::Cylinder);
    assert_eq_prec(0.4, shapes[0].radius, 3);
    assert_eq_prec(1.2, shapes[0].cyl_height, 3);
}

#[test]
fn from_setup_scale_factor_multiplies_radii_and_offsets() {
    let shapes = shapes_of(&door_setup(), 2.0, &|_| false, None);
    assert_eq!(shapes.len(), 1);
    let s = shapes[0];
    assert_eq!(s.collision_type, ShadowCollisionType::Sphere);
    assert_eq_prec(2.0, s.scale, 3);
    assert_eq_prec(0.2, s.radius, 3);
    assert_eq_prec(0.036, s.local_position.z, 3);

    let mut cyl_setup = empty_setup();
    cyl_setup.cyl_spheres = vec![CylSphere {
        origin: dv(0.1, 0.2, 0.6),
        radius: 0.4,
        height: 1.2,
    }];
    let shapes = shapes_of(&cyl_setup, 2.0, &|_| false, None);
    assert_eq!(shapes.len(), 1);
    let c = shapes[0];
    assert_eq!(c.collision_type, ShadowCollisionType::Cylinder);
    assert_eq_prec(2.0, c.scale, 3);
    assert_eq_prec(0.8, c.radius, 3);
    assert_eq_prec(2.4, c.cyl_height, 3);
    assert_eq_prec(0.2, c.local_position.x, 3);
    assert_eq_prec(0.4, c.local_position.y, 3);
    assert_eq_prec(1.2, c.local_position.z, 3);
}

#[test]
fn from_setup_empty_setup_returns_empty_list() {
    assert!(shapes_of(&empty_setup(), 1.0, &|_| true, None).is_empty());
}

#[test]
fn shape_source_no_cylinders_no_spheres_no_bsp_parts_yields_nothing() {
    let mut setup = empty_setup();
    setup.parts = vec![0x0100_0ABC];
    assert!(shapes_of(&setup, 1.0, &|_| false, None).is_empty());
}

#[test]
fn shape_source_bsp_part_no_cylinders_emits_bsp_shape() {
    let mut setup = empty_setup();
    setup.parts = vec![0x0100_AAAA];
    let shapes = shapes_of(&setup, 1.0, &|id| id == 0x0100_AAAA, None);
    assert!(
        shapes
            .iter()
            .any(|s| s.collision_type == ShadowCollisionType::Bsp)
    );
}

#[test]
fn shape_source_body_spheres_no_cylinders_emits_sphere_shapes() {
    let mut setup = empty_setup();
    setup.spheres = vec![
        DatSphere {
            origin: dv(0.0, 0.0, 0.475),
            radius: 0.48,
        },
        DatSphere {
            origin: dv(0.0, 0.0, 1.35),
            radius: 0.48,
        },
    ];
    let shapes = shapes_of(&setup, 1.0, &|_| false, None);
    assert_eq!(shapes.len(), 2);
    assert!(
        shapes
            .iter()
            .all(|s| s.collision_type == ShadowCollisionType::Sphere)
    );
}
