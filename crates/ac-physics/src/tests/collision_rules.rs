//! Ported from OpenAC `tests/AcDream.Core.Tests/Physics/CollisionExemptionTests.cs`,
//! `EntityCollisionFlagsTests.cs`, `MissileCollisionRulesTests.cs`,
//! `A6P7DispatchRulesTests.cs` and `EntryRestrictionGateTests.cs`. OpenAC's
//! client-object table is represented by [`ObjectRestrictions`] (restriction
//! objects and monarchs) and by the public weenie bitfield itself
//! ([`resolve_mover_pvp_state`]).

use std::collections::HashSet;

use glam::Vec3;

use super::bsp_step_up::make_grounded_transition_in;
use super::support::*;
use crate::PhysicsState;
use crate::bsp::{CellBsp, CellBspNode};
use crate::collision_flags::{EntityCollisionFlags as F, resolve_mover_pvp_state, should_skip};
use crate::engine::PhysicsEngine;
use crate::state::{ObjectInfoState as S, TransitionState};
use crate::transition::{
    HouseRestrictions, ObjectInfo, ObjectRestrictions, RestrictionObject, bsp_only_dispatch,
};
use crate::world::{CellPhysics, CollisionWorld, Placement};

const ETHEREAL_PS: u32 = 0x4;
const IGNORE_COLLISIONS_PS: u32 = 0x10;

fn skip(target_state: u32, target_flags: u8, mover: u32) -> bool {
    should_skip(target_state, F(target_flags), S(mover))
}

// ── CollisionExemptionTests ────────────────────────────────────────────

#[test]
fn ethereal_and_ignore_collisions_always_skipped() {
    assert!(skip(
        ETHEREAL_PS | IGNORE_COLLISIONS_PS,
        F::NONE,
        S::IS_PLAYER
    ));
}

#[test]
fn ethereal_only_not_instant_skipped() {
    assert!(!skip(ETHEREAL_PS, F::NONE, S::IS_PLAYER));
}

#[test]
fn viewer_vs_creature_skipped() {
    assert!(skip(0, F::IS_CREATURE, S::IS_VIEWER));
}

#[test]
fn viewer_vs_non_creature_not_skipped() {
    assert!(!skip(0, F::NONE, S::IS_VIEWER));
}

#[test]
fn ignore_creatures_vs_creature_skipped() {
    assert!(skip(0, F::IS_CREATURE, S::IGNORE_CREATURES));
}

#[test]
fn non_pk_player_vs_non_pk_player_skipped() {
    assert!(skip(0, F::IS_PLAYER, S::IS_PLAYER));
}

#[test]
fn pk_vs_pk_not_skipped() {
    assert!(!skip(0, F::IS_PLAYER | F::IS_PK, S::IS_PLAYER | S::IS_PK));
}

#[test]
fn pk_lite_vs_pk_lite_not_skipped() {
    assert!(!skip(
        0,
        F::IS_PLAYER | F::IS_PK_LITE,
        S::IS_PLAYER | S::IS_PK_LITE
    ));
}

#[test]
fn pk_vs_non_pk_skipped() {
    assert!(skip(0, F::IS_PLAYER, S::IS_PLAYER | S::IS_PK));
}

#[test]
fn pk_vs_pk_lite_skipped() {
    assert!(skip(
        0,
        F::IS_PLAYER | F::IS_PK_LITE,
        S::IS_PLAYER | S::IS_PK
    ));
}

#[test]
fn impenetrable_target_vs_any_player_not_skipped() {
    assert!(!skip(0, F::IS_PLAYER | F::IS_IMPENETRABLE, S::IS_PLAYER));
}

#[test]
fn player_vs_creature_not_skipped() {
    assert!(!skip(0, F::IS_CREATURE, S::IS_PLAYER));
}

#[test]
fn non_player_mover_vs_player_not_skipped() {
    assert!(!skip(0, F::IS_PLAYER, S::NONE));
}

#[test]
fn impenetrable_mover_vs_ordinary_player_not_skipped() {
    assert!(!skip(0, F::IS_PLAYER, S::IS_PLAYER | S::IS_IMPENETRABLE));
}

#[test]
fn impenetrable_mover_still_collides_even_when_also_pk() {
    assert!(!skip(
        0,
        F::IS_PLAYER,
        S::IS_PLAYER | S::IS_IMPENETRABLE | S::IS_PK
    ));
}

// ── EntityCollisionFlagsTests ──────────────────────────────────────────

#[test]
fn from_pwd_bitfield_all_zeros_no_flags() {
    assert_eq!(F::from_pwd_bitfield(0), F(F::NONE));
}

#[test]
fn from_pwd_bitfield_player_bit_sets_is_player() {
    let flags = F::from_pwd_bitfield(0x8);
    assert!(flags.contains(F::IS_PLAYER));
    assert!(!flags.contains(F::IS_PK));
}

#[test]
fn from_pwd_bitfield_player_killer_bit_sets_is_pk() {
    let flags = F::from_pwd_bitfield(0x20);
    assert!(flags.contains(F::IS_PK));
    assert!(!flags.contains(F::IS_PK_LITE));
}

#[test]
fn from_pwd_bitfield_pk_lite_bit_sets_is_pk_lite() {
    let flags = F::from_pwd_bitfield(0x200_0000);
    assert!(flags.contains(F::IS_PK_LITE));
    assert!(!flags.contains(F::IS_PK));
}

#[test]
fn from_pwd_bitfield_free_pk_status_bit_sets_is_impenetrable() {
    assert!(F::from_pwd_bitfield(0x20_0000).contains(F::IS_IMPENETRABLE));
}

#[test]
fn from_pwd_bitfield_player_and_pk_sets_both() {
    let flags = F::from_pwd_bitfield(0x28);
    assert!(flags.contains(F::IS_PLAYER));
    assert!(flags.contains(F::IS_PK));
    assert!(!flags.contains(F::IS_PK_LITE));
    assert!(!flags.contains(F::IS_IMPENETRABLE));
}

#[test]
fn from_pwd_bitfield_unrelated_bits_ignored() {
    assert_eq!(F::from_pwd_bitfield(0x1 | 0x2 | 0x4 | 0x10), F(F::NONE));
}

#[test]
fn to_mover_state_none_produces_none_not_is_player() {
    assert_eq!(F(F::NONE).to_mover_state(), S(S::NONE));
    assert_eq!(F(F::IS_PLAYER).to_mover_state(), S(S::NONE));
}

#[test]
fn to_mover_state_is_pk_translates() {
    assert_eq!(F(F::IS_PK).to_mover_state(), S(S::IS_PK));
}

#[test]
fn to_mover_state_is_pk_lite_translates() {
    assert_eq!(F(F::IS_PK_LITE).to_mover_state(), S(S::IS_PK_LITE));
}

#[test]
fn to_mover_state_is_impenetrable_translates() {
    assert_eq!(
        F(F::IS_IMPENETRABLE).to_mover_state(),
        S(S::IS_IMPENETRABLE)
    );
}

#[test]
fn to_mover_state_all_three_bits_translate_independently() {
    let flags = F(F::IS_PLAYER
        | F::IS_PK
        | F::IS_PK_LITE
        | F::IS_IMPENETRABLE
        | F::IS_CREATURE
        | F::HAS_WEENIE);
    assert_eq!(
        flags.to_mover_state(),
        S(S::IS_PK | S::IS_PK_LITE | S::IS_IMPENETRABLE)
    );
}

#[test]
fn from_pwd_bitfield_then_to_mover_state_pk_player_produces_is_pk_only() {
    let state = F::from_pwd_bitfield(0x8 | 0x20).to_mover_state();
    assert_eq!(state, S(S::IS_PK));
    assert!(!state.contains(S::IS_PLAYER));
}

#[test]
fn resolve_mover_pvp_state_no_row_or_zero_bitfield_is_none() {
    assert_eq!(resolve_mover_pvp_state(None), S(S::NONE));
    assert_eq!(resolve_mover_pvp_state(Some(0)), S(S::NONE));
}

#[test]
fn resolve_mover_pvp_state_pk_player_row_resolves_to_is_pk() {
    assert_eq!(resolve_mover_pvp_state(Some(0x8 | 0x20)), S(S::IS_PK));
}

#[test]
fn pk_vs_pk_collides_pk_vs_non_pk_stays_exempt_through_bitfields() {
    let pk_bits = 0x8 | 0x20;
    let non_pk_bits = 0x8;
    let pk_mover = S(S::IS_PLAYER | resolve_mover_pvp_state(Some(pk_bits)).0);
    let non_pk_target = F::from_pwd_bitfield(non_pk_bits);
    let pk_target = F::from_pwd_bitfield(pk_bits);
    assert!(should_skip(0, non_pk_target, pk_mover));
    assert!(!should_skip(0, pk_target, pk_mover));
    let non_pk_mover = S(S::IS_PLAYER | resolve_mover_pvp_state(Some(non_pk_bits)).0);
    assert!(should_skip(0, non_pk_target, non_pk_mover));
}

// ── MissileCollisionRulesTests ─────────────────────────────────────────

const TARGET: u32 = 0x1111;
const OTHER: u32 = 0x2222;

fn missile(target_id: u32) -> ObjectInfo {
    ObjectInfo {
        mover_physics_state: PhysicsState::MISSILE,
        target_id,
        ..ObjectInfo::default()
    }
}

#[test]
fn other_missile_is_always_ignored_even_when_designated() {
    assert!(missile(TARGET).missile_ignore(TARGET, PhysicsState::MISSILE.bits(), F(F::HAS_WEENIE)));
}

#[test]
fn non_missile_mover_does_not_use_missile_exemptions() {
    assert!(!ObjectInfo::default().missile_ignore(
        OTHER,
        PhysicsState::ETHEREAL.bits(),
        F(F::HAS_WEENIE | F::IS_CREATURE)
    ));
}

#[test]
fn designated_target_is_never_ignored_by_later_branches() {
    assert!(!missile(TARGET).missile_ignore(
        TARGET,
        PhysicsState::ETHEREAL.bits(),
        F(F::HAS_WEENIE | F::IS_CREATURE)
    ));
}

#[test]
fn ethereal_target_requires_weenie_metadata() {
    for (has_weenie, ignored) in [(true, true), (false, false)] {
        let flags = if has_weenie {
            F(F::HAS_WEENIE)
        } else {
            F(F::NONE)
        };
        assert_eq!(
            missile(0).missile_ignore(OTHER, PhysicsState::ETHEREAL.bits(), flags),
            ignored
        );
    }
}

#[test]
fn known_target_ignores_other_creatures() {
    assert!(missile(TARGET).missile_ignore(OTHER, 0, F(F::HAS_WEENIE | F::IS_CREATURE)));
}

#[test]
fn unknown_target_does_not_ignore_creatures() {
    assert!(!missile(0).missile_ignore(OTHER, 0, F(F::HAS_WEENIE | F::IS_CREATURE)));
}

#[test]
fn known_target_does_not_ignore_non_creature_weenie() {
    assert!(!missile(TARGET).missile_ignore(OTHER, 0, F(F::HAS_WEENIE)));
}

// ── A6P7DispatchRulesTests ─────────────────────────────────────────────

#[test]
fn physics_state_flags_has_physics_bsp_is_bit_16() {
    assert_eq!(PhysicsState::HAS_PHYSICS_BSP.bits(), 0x0001_0000);
}

#[test]
fn bsp_only_dispatch_respects_has_physics_bsp_flag() {
    for (state, expected) in [
        (0x0001_0008, true),
        (0x0001_0000, true),
        (0x0011_0000, true),
        (0x0000_0008, false),
        (0x0000_0000, false),
        (0x0000_FFFF, false),
    ] {
        assert_eq!(bsp_only_dispatch(state), expected, "{state:#010X}");
    }
}

#[test]
fn non_missile_mover_does_not_ignore_ordinary_target() {
    assert!(!ObjectInfo::default().missile_ignore(1, 0, F(F::HAS_WEENIE)));
}

#[test]
fn bsp_only_dispatch_door_state_still_dispatches_bsp_only() {
    assert!(bsp_only_dispatch(0x0001_0008));
}

// ── EntryRestrictionGateTests ──────────────────────────────────────────

const RESTRICTION_OBJ_GUID: u32 = 0x8000_1234;
const MOVER_GUID: u32 = 0x5000_0001;

fn player(extra: u32) -> ObjectInfo {
    ObjectInfo {
        state: S(S::IS_PLAYER | extra),
        self_entity_id: MOVER_GUID,
        ..ObjectInfo::default()
    }
}

fn objects_with_restriction_object(
    house_owner_id: Option<u32>,
    restrictions: Option<HouseRestrictions>,
) -> ObjectRestrictions {
    let mut objects = ObjectRestrictions::new();
    objects.set_object(
        RESTRICTION_OBJ_GUID,
        RestrictionObject {
            house_owner_id,
            restrictions,
        },
    );
    objects
}

fn closed(guests: &[u32], monarch: u32) -> HouseRestrictions {
    HouseRestrictions {
        open_to_public: false,
        allegiance_monarch_id: monarch,
        guests: guests.iter().copied().collect::<HashSet<u32>>(),
    }
}

#[test]
fn ordinary_cell_no_restriction_obj_is_no_op_for_player() {
    assert_eq!(
        player(0).check_entry_restrictions(0, None),
        TransitionState::Ok
    );
}

#[test]
fn ordinary_cell_no_restriction_obj_is_no_op_for_non_player() {
    assert_eq!(
        ObjectInfo::default().check_entry_restrictions(0, None),
        TransitionState::Ok
    );
}

#[test]
fn restricted_cell_non_player_mover_bypasses() {
    assert_eq!(
        ObjectInfo::default().check_entry_restrictions(RESTRICTION_OBJ_GUID, None),
        TransitionState::Ok
    );
}

#[test]
fn restricted_cell_player_mover_unresolved_restriction_object_fails_closed() {
    assert_eq!(
        player(0).check_entry_restrictions(RESTRICTION_OBJ_GUID, None),
        TransitionState::Collided
    );
}

#[test]
fn restricted_cell_player_mover_resolved_but_objects_table_has_no_row_fails_closed() {
    let objects = ObjectRestrictions::new();
    assert_eq!(
        player(0).check_entry_restrictions(RESTRICTION_OBJ_GUID, Some(&objects)),
        TransitionState::Collided
    );
}

#[test]
fn restricted_cell_player_mover_can_bypass_move_restrictions_passes_through() {
    assert_eq!(
        player(S::CAN_BYPASS_MOVE_RESTRICTIONS)
            .check_entry_restrictions(RESTRICTION_OBJ_GUID, None),
        TransitionState::Ok
    );
}

#[test]
fn resolved_unowned_house_no_owner_no_restrictions_admits() {
    let objects = objects_with_restriction_object(None, None);
    assert_eq!(
        player(0).check_entry_restrictions(RESTRICTION_OBJ_GUID, Some(&objects)),
        TransitionState::Ok
    );
}

#[test]
fn resolved_house_mover_is_owner_admits() {
    let objects = objects_with_restriction_object(Some(MOVER_GUID), Some(closed(&[], 0)));
    assert_eq!(
        player(0).check_entry_restrictions(RESTRICTION_OBJ_GUID, Some(&objects)),
        TransitionState::Ok
    );
}

#[test]
fn resolved_house_no_restriction_db_admits() {
    let objects = objects_with_restriction_object(Some(0x5000_0099), None);
    assert_eq!(
        player(0).check_entry_restrictions(RESTRICTION_OBJ_GUID, Some(&objects)),
        TransitionState::Ok
    );
}

#[test]
fn resolved_house_present_list_excludes_mover_blocks() {
    let objects =
        objects_with_restriction_object(Some(0x5000_0099), Some(closed(&[0x5000_0002], 0)));
    assert_eq!(
        player(0).check_entry_restrictions(RESTRICTION_OBJ_GUID, Some(&objects)),
        TransitionState::Collided
    );
}

#[test]
fn resolved_house_present_list_includes_mover_admits() {
    let objects =
        objects_with_restriction_object(Some(0x5000_0099), Some(closed(&[MOVER_GUID], 0)));
    assert_eq!(
        player(0).check_entry_restrictions(RESTRICTION_OBJ_GUID, Some(&objects)),
        TransitionState::Ok
    );
}

#[test]
fn resolved_house_open_to_public_admits_even_without_guest_entry() {
    let objects = objects_with_restriction_object(
        Some(0x5000_0099),
        Some(HouseRestrictions {
            open_to_public: true,
            ..HouseRestrictions::default()
        }),
    );
    assert_eq!(
        player(0).check_entry_restrictions(RESTRICTION_OBJ_GUID, Some(&objects)),
        TransitionState::Ok
    );
}

#[test]
fn resolved_house_mover_shares_allegiance_monarch_admits() {
    let monarch = 0x5000_0500;
    let mut objects =
        objects_with_restriction_object(Some(0x5000_0099), Some(closed(&[], monarch)));
    objects.set_monarch(MOVER_GUID, monarch);
    assert_eq!(
        player(0).check_entry_restrictions(RESTRICTION_OBJ_GUID, Some(&objects)),
        TransitionState::Ok
    );
}

#[test]
fn from_pwd_bitfield_only_admin_bit_does_not_grant_bypass() {
    assert!(!F::from_pwd_bitfield(0x10_0000).contains(F::CAN_BYPASS_MOVE_RESTRICTIONS));
}

#[test]
fn from_pwd_bitfield_only_immune_cell_restrictions_bit_does_not_grant_bypass() {
    assert!(!F::from_pwd_bitfield(0x40_0000).contains(F::CAN_BYPASS_MOVE_RESTRICTIONS));
}

#[test]
fn from_pwd_bitfield_both_admin_and_immune_bits_grants_bypass() {
    assert!(F::from_pwd_bitfield(0x10_0000 | 0x40_0000).contains(F::CAN_BYPASS_MOVE_RESTRICTIONS));
}

#[test]
fn to_mover_state_can_bypass_move_restrictions_translates_independently() {
    assert_eq!(
        F(F::CAN_BYPASS_MOVE_RESTRICTIONS).to_mover_state(),
        S(S::CAN_BYPASS_MOVE_RESTRICTIONS)
    );
}

#[test]
fn from_pwd_bitfield_then_to_mover_state_end_to_end_admin_bypasses_restriction() {
    let bits = 0x8 | 0x10_0000 | 0x40_0000;
    let state = S(S::IS_PLAYER | F::from_pwd_bitfield(bits).to_mover_state().0);
    let mover = ObjectInfo {
        state,
        self_entity_id: MOVER_GUID,
        ..ObjectInfo::default()
    };
    assert_eq!(
        mover.check_entry_restrictions(RESTRICTION_OBJ_GUID, None),
        TransitionState::Ok
    );
}

#[test]
fn cell_physics_default_restriction_obj_is_zero() {
    assert_eq!(CellPhysics::default().restriction_obj, 0);
}

const CELL_ID: u32 = 0xA9B4_0157;

fn engine_with_indoor_cell(
    restriction_obj: u32,
    objects: Option<ObjectRestrictions>,
) -> PhysicsEngine {
    let mut cell = cell_with_bsp(
        CELL_ID,
        single_leaf(Vec3::ZERO, 10.0, Vec::new()),
        Placement::default(),
    );
    cell.containment_bsp = CellBsp::new(vec![CellBspNode::Leaf]).unwrap();
    cell.restriction_obj = restriction_obj;
    let mut world = CollisionWorld::new();
    world.insert_cell(cell);
    let mut engine = PhysicsEngine::new();
    engine.attach_world(world);
    engine.set_objects(objects);
    engine
}

fn run_end_to_end(engine: &PhysicsEngine, extra_state: u32, self_id: u32) -> f32 {
    let from = Vec3::new(0.1, 0.0, 0.2);
    let to = Vec3::new(0.7, 0.0, 0.2);
    let mut t = make_grounded_transition_in(from, to, 0.30, CELL_ID);
    t.object_info.state.insert(S::IS_PLAYER | extra_state);
    t.object_info.self_entity_id = self_id;
    let _ = t.find_transitional_position(engine);
    t.sphere_path.cur_pos.x
}

#[test]
fn end_to_end_restricted_cell_player_cannot_bypass_transition_halts_at_origin() {
    let engine = engine_with_indoor_cell(0xABCD, None);
    let x = run_end_to_end(&engine, 0, 0);
    assert!((x - 0.1).abs() < 1e-4, "x {x}");
}

#[test]
fn end_to_end_restricted_cell_player_can_bypass_transition_reaches_target() {
    let engine = engine_with_indoor_cell(0xABCD, None);
    let x = run_end_to_end(&engine, S::CAN_BYPASS_MOVE_RESTRICTIONS, 0);
    assert!((x - 0.7).abs() < 1e-4, "x {x}");
}

#[test]
fn end_to_end_ordinary_cell_no_restriction_obj_player_unaffected() {
    let engine = engine_with_indoor_cell(0, None);
    let x = run_end_to_end(&engine, 0, 0);
    assert!((x - 0.7).abs() < 1e-4, "x {x}");
}

#[test]
fn end_to_end_restricted_cell_mover_is_resolved_owner_transition_reaches_target() {
    let objects = objects_with_restriction_object(Some(MOVER_GUID), Some(closed(&[], 0)));
    let engine = engine_with_indoor_cell(RESTRICTION_OBJ_GUID, Some(objects));
    let x = run_end_to_end(&engine, 0, MOVER_GUID);
    assert!((x - 0.7).abs() < 1e-4, "x {x}");
}

#[test]
fn end_to_end_restricted_cell_mover_is_resolved_non_guest_transition_halts_at_origin() {
    let objects =
        objects_with_restriction_object(Some(0x5000_0099), Some(closed(&[0x5000_0002], 0)));
    let engine = engine_with_indoor_cell(RESTRICTION_OBJ_GUID, Some(objects));
    let x = run_end_to_end(&engine, 0, MOVER_GUID);
    assert!((x - 0.1).abs() < 1e-4, "x {x}");
}
