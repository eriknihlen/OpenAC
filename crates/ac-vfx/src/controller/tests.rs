//! Cases from OpenAC `tests/AcDream.App.Tests/Rendering/Vfx/EntityEffectControllerTests.cs`.
//! OpenAC drives them through its live-entity runtime; here a small table of
//! owner states stands in for it (in a cell, frozen, position, attachment).

use std::collections::HashMap;
use std::sync::Arc;

use ac_dat::objects::{PhysicsScriptTable, PhysicsScriptTableData, ScriptAndModData};
use glam::Mat4;

use super::*;
use crate::poses::EffectPoseRegistry;
use crate::profile::NetworkEffectDescription;
use crate::random::ConstantRandom;
use crate::script::tests::{build_script, create_hook, emitter_info_id};
use crate::script::{LoadedPhysicsScript, ScriptHookSink};
use crate::script_table::TableLoadError;

const GUID: u32 = 0x7000_0001;
const DIRECT_DID: u32 = 0x3300_0011;
const TYPED_DID: u32 = 0x3300_0022;
const TABLE_DID: u32 = 0x3400_0033;
const RAW_TYPE: u32 = 0x16;

#[derive(Default)]
struct Owners {
    roots: HashMap<u32, LiveRootState>,
    parents: HashMap<u32, u32>,
    children: HashMap<(u32, u32), u32>,
}

impl EffectOwnerQueries for Owners {
    fn live_root(&self, local_id: u32) -> Option<LiveRootState> {
        self.roots.get(&local_id).copied()
    }

    fn parent_of_attached_child(&self, local_id: u32) -> Option<u32> {
        self.parents.get(&local_id).copied()
    }

    fn child_at_part(&self, parent: u32, part_index: u32) -> Option<u32> {
        self.children.get(&(parent, part_index)).copied()
    }
}

struct FixtureSink<'a> {
    controller: &'a mut EntityEffectController,
    poses: &'a EffectPoseRegistry,
    owners: &'a Owners,
    calls: &'a mut Vec<(u32, Vec3, AnimationHook)>,
}

impl ScriptHookSink for FixtureSink<'_> {
    fn on_script_hook(
        &mut self,
        runner: &mut PhysicsScriptRunner,
        owner: u32,
        anchor: Vec3,
        hook: &AnimationHook,
    ) {
        self.calls.push((owner, anchor, *hook));
        let mut ctx = EffectContext {
            runner,
            poses: self.poses,
            queries: self.owners,
        };
        self.controller.on_hook(&mut ctx, owner, anchor, hook);
    }

    fn can_advance_owner(&self, owner: u32) -> bool {
        self.controller.can_advance_owner(self.owners, owner)
    }
}

fn build_table(table_did: u32, raw_type: u32, entries: &[(f32, u32)]) -> Arc<PhysicsScriptTable> {
    Arc::new(PhysicsScriptTable {
        id: table_did,
        script_table: vec![(
            raw_type,
            PhysicsScriptTableData {
                scripts: entries
                    .iter()
                    .map(|&(modifier, script_id)| ScriptAndModData {
                        modifier,
                        script_id,
                    })
                    .collect(),
            },
        )],
    })
}

fn live_profile(sound_table: Option<u32>) -> EntityEffectProfile {
    EntityEffectProfile::live(
        0,
        0,
        0,
        &NetworkEffectDescription {
            physics_script_table_id: Some(TABLE_DID),
            sound_table_id: sound_table,
            default_script_type: Some(RAW_TYPE),
            default_script_intensity: Some(0.5),
        },
    )
}

struct Fixture {
    runner: PhysicsScriptRunner,
    controller: EntityEffectController,
    poses: EffectPoseRegistry,
    owners: Owners,
    calls: Vec<(u32, Vec3, AnimationHook)>,
    next_local: u32,
    scripts: Arc<std::sync::Mutex<HashMap<u32, Arc<LoadedPhysicsScript>>>>,
}

impl Fixture {
    fn new() -> Self {
        let table = build_table(TABLE_DID, RAW_TYPE, &[(1.0, TYPED_DID)]);
        Self::with_tables(PhysicsScriptTableResolver::new(move |id| {
            Ok((id == TABLE_DID).then(|| Arc::clone(&table)))
        }))
    }

    fn with_tables(tables: PhysicsScriptTableResolver) -> Self {
        let scripts: Arc<std::sync::Mutex<HashMap<u32, Arc<LoadedPhysicsScript>>>> = Arc::default();
        let source = Arc::clone(&scripts);
        let runner = PhysicsScriptRunner::new(
            move |id| Ok(source.lock().unwrap().get(&id).cloned()),
            Box::new(ConstantRandom(0.5)),
        );
        let mut fixture = Self {
            runner,
            controller: EntityEffectController::new(tables),
            poses: EffectPoseRegistry::new(),
            owners: Owners::default(),
            calls: Vec::new(),
            next_local: 0x5000_0000,
            scripts,
        };
        fixture.add_script(DIRECT_DID, 1, 0.0);
        fixture.add_script(TYPED_DID, 2, 0.0);
        fixture
    }

    fn add_script(&mut self, did: u32, emitter: u32, start: f64) {
        self.add_hook_script(did, create_hook(emitter), start);
    }

    fn add_hook_script(&mut self, did: u32, hook: AnimationHook, start: f64) {
        self.scripts
            .lock()
            .unwrap()
            .insert(did, Arc::new(build_script(&[(start, hook)])));
    }

    fn ctx_call<R>(
        &mut self,
        f: impl FnOnce(&mut EntityEffectController, &mut EffectContext<'_>) -> R,
    ) -> R {
        let mut ctx = EffectContext {
            runner: &mut self.runner,
            poses: &self.poses,
            queries: &self.owners,
        };
        f(&mut self.controller, &mut ctx)
    }

    fn tick(&mut self, time: f64) {
        let mut sink = FixtureSink {
            controller: &mut self.controller,
            poses: &self.poses,
            owners: &self.owners,
            calls: &mut self.calls,
        };
        self.runner.tick(time, &mut sink).unwrap();
    }

    /// Register a live object in a cell at (1, 2, 3); returns its local id.
    fn register(&mut self, in_cell: bool) -> u32 {
        self.next_local += 1;
        let local = self.next_local;
        self.owners.roots.insert(
            local,
            LiveRootState {
                in_cell,
                frozen: false,
                position: Vec3::new(1.0, 2.0, 3.0),
            },
        );
        local
    }

    fn ready_live(&mut self, guid: u32, profile: EntityEffectProfile) -> u32 {
        let local = self.register(true);
        self.ctx_call(|c, ctx| c.on_live_entity_ready(ctx, guid, local, profile, false));
        local
    }

    fn unregister(&mut self, guid: u32, local: u32) {
        self.owners.roots.remove(&local);
        self.controller
            .on_live_entity_unregistered(&mut self.runner, guid, Some(local), true);
    }

    fn set_in_cell(&mut self, local: u32, in_cell: bool) {
        self.owners.roots.get_mut(&local).unwrap().in_cell = in_cell;
    }

    fn handle_direct(&mut self, guid: u32, did: u32) {
        self.ctx_call(|c, ctx| c.handle_direct(ctx, guid, did));
    }

    fn handle_typed(&mut self, guid: u32, raw: u32, intensity: f32) {
        self.ctx_call(|c, ctx| c.handle_typed(ctx, guid, raw, intensity));
    }

    fn handle_sound(&mut self, guid: u32, sound_type: u32, volume: f32) {
        self.ctx_call(|c, ctx| c.handle_sound(ctx, guid, sound_type, volume));
    }

    fn emitter_ids(&self) -> Vec<u32> {
        self.calls
            .iter()
            .map(|(_, _, h)| emitter_info_id(h))
            .collect()
    }

    fn sounds(&mut self) -> Vec<(u32, u32, f32)> {
        self.controller
            .take_events()
            .into_iter()
            .filter_map(|e| match e {
                EffectEvent::ServerSound {
                    owner,
                    sound_type,
                    volume,
                    ..
                } => Some((owner, sound_type, volume)),
                _ => None,
            })
            .collect()
    }
}

#[test]
fn server_sound_for_ready_owner_plays_immediately_with_local_id_and_wire_volume() {
    let mut f = Fixture::new();
    let local = f.ready_live(GUID, live_profile(None));
    f.controller.take_events();

    f.handle_sound(GUID, 0x30, 0.75);

    // Downstream sinks must see the local id, never the server guid.
    assert_eq!(f.sounds(), [(local, 0x30, 0.75)]);
}

#[test]
fn server_sound_for_unknown_guid_is_queued_and_replayed_on_arrival() {
    let mut f = Fixture::new();
    f.handle_sound(GUID, 0x09, 1.0);
    assert_eq!(f.controller.pending_packet_count(), 1);
    assert!(f.sounds().is_empty());

    let local = f.ready_live(GUID, live_profile(None));

    assert_eq!(f.controller.pending_packet_count(), 0);
    assert_eq!(f.sounds(), [(local, 0x09, 1.0)]);
}

#[test]
fn server_sound_shares_one_queue_with_script_packets_and_all_drain_together() {
    let mut f = Fixture::new();
    f.handle_direct(GUID, DIRECT_DID);
    f.handle_sound(GUID, 0x0A, 1.0);
    f.handle_typed(GUID, RAW_TYPE, 0.5);
    assert_eq!(f.controller.pending_packet_count(), 3);

    f.ready_live(GUID, live_profile(None));

    assert_eq!(f.controller.pending_packet_count(), 0);
    assert_eq!(f.sounds().len(), 1);
    assert_eq!(f.runner.active_script_count(), 2);
}

#[test]
fn server_sound_with_zero_guid_is_ignored() {
    let mut f = Fixture::new();
    f.handle_sound(0, 0x30, 1.0);
    assert!(f.sounds().is_empty());
    assert_eq!(f.controller.pending_packet_count(), 0);
}

#[test]
fn server_sound_plays_at_the_owners_current_root_pose() {
    let mut f = Fixture::new();
    let local = f.ready_live(GUID, live_profile(None));
    f.poses
        .publish(
            local,
            Mat4::from_translation(Vec3::new(4.0, 5.0, 6.0)),
            &[],
            0,
            None,
        )
        .unwrap();
    f.controller.take_events();

    f.handle_sound(GUID, 0x30, 1.0);

    let events = f.controller.take_events();
    assert_eq!(
        events,
        [EffectEvent::ServerSound {
            owner: local,
            position: Vec3::new(4.0, 5.0, 6.0),
            sound_type: 0x30,
            volume: 1.0
        }]
    );
}

#[test]
fn pre_materialization_direct_and_typed_packets_replay_once_in_mixed_order() {
    let mut f = Fixture::new();
    f.handle_direct(GUID, DIRECT_DID);
    f.handle_typed(GUID, RAW_TYPE, 0.5);
    assert_eq!(f.controller.pending_packet_count(), 2);

    let local = f.ready_live(GUID, live_profile(None));
    assert_eq!(f.controller.pending_packet_count(), 0);
    assert_eq!(f.runner.active_script_count(), 2);

    assert!(f.ctx_call(|c, ctx| c.replay_pending_for_live_entity(ctx, GUID)));
    f.tick(0.0);

    assert_eq!(f.emitter_ids(), [1, 2]);
    assert!(f.calls.iter().all(|c| c.0 == local && c.0 != GUID));
}

#[test]
fn prepared_owner_does_not_replay_pending_packets_until_construction_barrier_opens() {
    let mut f = Fixture::new();
    f.handle_direct(GUID, DIRECT_DID);
    let local = f.register(true);

    f.ctx_call(|c, ctx| c.prepare_live_entity_owner(ctx, GUID, local, live_profile(None), false));
    assert_eq!(f.controller.pending_packet_count(), 1);
    assert_eq!(f.runner.active_script_count(), 0);

    assert!(f.ctx_call(|c, ctx| c.replay_pending_for_live_entity(ctx, GUID)));
    assert_eq!(f.controller.pending_packet_count(), 0);
    assert_eq!(f.runner.active_script_count(), 1);
}

#[test]
fn runtime_initial_placement_window_defers_packets_until_presentation_binds() {
    let mut f = Fixture::new();
    let local = f.register(false);
    f.ctx_call(|c, ctx| c.prepare_live_entity_owner(ctx, GUID, local, live_profile(None), true));

    f.handle_direct(GUID, DIRECT_DID);
    f.handle_typed(GUID, RAW_TYPE, 0.5);
    assert_eq!(f.controller.pending_packet_count(), 2);
    assert_eq!(f.runner.active_script_count(), 0);

    f.set_in_cell(local, true);
    assert!(f.ctx_call(|c, ctx| c.on_presentation_bound(ctx, GUID)));
    assert_eq!(f.controller.pending_packet_count(), 0);
    assert_eq!(f.runner.active_script_count(), 2);

    f.tick(0.0);
    assert_eq!(f.emitter_ids(), [1, 2]);
}

#[test]
fn ready_owner_receives_direct_and_typed_packets_immediately() {
    let mut f = Fixture::new();
    f.ready_live(GUID, live_profile(None));
    f.handle_typed(GUID, RAW_TYPE, 0.5);
    f.handle_direct(GUID, DIRECT_DID);
    f.tick(0.0);
    assert_eq!(f.emitter_ids(), [2, 1]);
    assert_eq!(f.controller.pending_packet_count(), 0);
}

#[test]
fn cellless_owner_rejects_new_packet_and_pauses_existing_queue_until_reentry() {
    let mut f = Fixture::new();
    f.add_script(DIRECT_DID, 1, 1.0);
    let local = f.ready_live(GUID, live_profile(None));
    f.handle_direct(GUID, DIRECT_DID);
    f.tick(0.5);

    f.set_in_cell(local, false);
    f.handle_direct(GUID, DIRECT_DID);
    f.tick(2.0);
    assert!(f.calls.is_empty());
    assert_eq!(f.runner.active_script_count(), 1);

    f.set_in_cell(local, true);
    f.tick(2.0);
    assert_eq!(f.emitter_ids(), [1]);
}

#[test]
fn cellless_owner_hidden_transition_queues_typed_script_until_reentry() {
    let mut f = Fixture::new();
    let local = f.ready_live(GUID, live_profile(None));
    f.set_in_cell(local, false);

    assert!(f.ctx_call(|c, ctx| c.play_typed_from_hidden_transition(ctx, local, RAW_TYPE, 0.5)));
    assert_eq!(f.runner.active_script_count(), 1);
    f.tick(1.0);
    assert!(f.calls.is_empty());

    f.set_in_cell(local, true);
    f.tick(1.0);
    assert_eq!(f.emitter_ids(), [2]);
}

#[test]
fn frozen_owner_pauses_queue_until_fresh_state_unfreezes_it() {
    let mut f = Fixture::new();
    f.add_script(DIRECT_DID, 1, 1.0);
    let local = f.ready_live(GUID, live_profile(None));
    f.handle_direct(GUID, DIRECT_DID);
    f.owners.roots.get_mut(&local).unwrap().frozen = true;
    f.handle_direct(GUID, DIRECT_DID);

    f.tick(2.0);
    assert!(f.calls.is_empty());
    assert_eq!(f.runner.active_script_count(), 2);

    f.owners.roots.get_mut(&local).unwrap().frozen = false;
    f.tick(2.0);
    assert_eq!(f.emitter_ids(), [1, 1]);
}

#[test]
fn pre_create_packet_waits_for_projection_but_post_create_cellless_packet_is_dropped() {
    let mut f = Fixture::new();
    f.handle_direct(GUID, DIRECT_DID);
    // Registered into a landblock that is not loaded: no cell yet.
    let local = f.register(false);
    f.ctx_call(|c, ctx| c.on_live_entity_ready(ctx, GUID, local, live_profile(None), false));
    assert_eq!(f.controller.pending_packet_count(), 1);
    f.handle_typed(GUID, RAW_TYPE, 0.5);
    assert_eq!(f.controller.pending_packet_count(), 1);

    // The landblock loads; the object becomes visible.
    f.set_in_cell(local, true);
    f.controller.mark_live_owner_pose_dirty(GUID);
    f.ctx_call(|c, ctx| c.refresh_live_owner_poses(ctx));
    f.tick(0.0);

    assert_eq!(f.controller.pending_packet_count(), 0);
    assert_eq!(f.emitter_ids(), [1]);
    assert!(f.calls.iter().all(|c| c.0 == local));
}

#[test]
fn loaded_to_pending_move_pauses_active_queue_until_landblock_loads() {
    let mut f = Fixture::new();
    f.add_script(DIRECT_DID, 1, 1.0);
    let local = f.ready_live(GUID, live_profile(None));
    f.handle_direct(GUID, DIRECT_DID);
    f.tick(0.5);

    f.set_in_cell(local, false);
    f.tick(2.0);
    assert!(f.calls.is_empty());
    assert_eq!(f.runner.active_script_count(), 1);

    f.set_in_cell(local, true);
    f.tick(2.0);
    assert_eq!(f.emitter_ids(), [1]);
}

#[test]
fn pending_invalid_typed_packet_does_not_corrupt_following_direct_packet() {
    let mut f = Fixture::new();
    f.handle_typed(GUID, RAW_TYPE, f32::NAN);
    f.handle_direct(GUID, DIRECT_DID);
    f.ready_live(GUID, live_profile(None));
    f.tick(0.0);
    assert_eq!(f.emitter_ids(), [1]);
    assert_eq!(f.controller.pending_packet_count(), 0);
}

#[test]
fn packet_never_uses_server_guid_or_fallback_anchor_before_owner_is_ready() {
    let mut f = Fixture::new();
    f.handle_direct(GUID, DIRECT_DID);
    f.tick(10.0);
    assert!(f.calls.is_empty());
    assert_eq!(f.runner.active_script_count(), 0);
    assert_eq!(f.controller.pending_packet_count(), 1);
}

#[test]
fn accepted_delete_clears_pending_generation_before_guid_reuse() {
    let mut f = Fixture::new();
    f.handle_direct(GUID, DIRECT_DID);
    f.controller.forget_unknown_owner(GUID);
    f.controller
        .on_live_entity_unregistered(&mut f.runner, GUID, None, true);
    assert_eq!(f.controller.pending_packet_count(), 0);

    f.ready_live(GUID, live_profile(None));
    f.tick(0.0);
    assert!(f.calls.is_empty());
}

#[test]
fn stale_delete_does_not_clear_current_generation_queue() {
    let mut f = Fixture::new();
    f.handle_direct(GUID, DIRECT_DID);
    f.controller
        .on_live_entity_unregistered(&mut f.runner, GUID, None, false);
    assert_eq!(f.controller.pending_packet_count(), 1);

    f.ready_live(GUID, live_profile(None));
    f.tick(0.0);
    assert_eq!(f.emitter_ids(), [1]);
}

#[test]
fn unknown_delete_clears_queue_that_never_received_create_object() {
    let mut f = Fixture::new();
    f.handle_direct(GUID, DIRECT_DID);
    f.controller.forget_unknown_owner(GUID);
    assert_eq!(f.controller.pending_packet_count(), 0);
}

#[test]
fn logical_teardown_stops_queued_scripts_and_clears_ready_identity() {
    let mut f = Fixture::new();
    f.add_script(DIRECT_DID, 1, 60.0);
    let local = f.ready_live(GUID, live_profile(None));
    f.handle_direct(GUID, DIRECT_DID);
    assert_eq!(f.runner.active_script_count(), 1);

    f.unregister(GUID, local);

    assert_eq!(f.runner.active_script_count(), 0);
    assert_eq!(f.controller.ready_owner_count(), 0);
    assert_eq!(f.controller.pending_packet_count(), 0);
}

#[test]
fn logical_teardown_releases_owner_scoped_resources_before_guid_reuse() {
    let mut f = Fixture::new();
    let first = f.ready_live(GUID, live_profile(Some(0x2000_00EE)));
    assert_eq!(
        f.controller.take_events(),
        [EffectEvent::SoundTableChanged {
            owner: first,
            sound_table: Some(0x2000_00EE)
        }]
    );

    f.unregister(GUID, first);
    let replacement = f.ready_live(GUID, live_profile(None));

    assert_eq!(
        f.controller.take_events(),
        [
            EffectEvent::OwnerUnregistered { owner: first },
            EffectEvent::SoundTableChanged {
                owner: replacement,
                sound_table: None
            }
        ]
    );
    assert_ne!(first, replacement);
}

#[test]
fn delayed_old_effect_teardown_does_not_clear_ready_replacement_generation() {
    let mut f = Fixture::new();
    let old = f.ready_live(GUID, live_profile(None));
    let replacement = f.ready_live(GUID, live_profile(None));
    f.controller
        .on_live_entity_unregistered(&mut f.runner, GUID, Some(old), false);

    assert_ne!(old, replacement);
    assert_eq!(f.controller.ready_owner_count(), 1);
    f.handle_direct(GUID, DIRECT_DID);
    f.tick(0.0);
    assert_eq!(f.calls.len(), 1);
    assert_eq!(f.calls[0].0, replacement);
}

#[test]
fn ready_owner_publishes_network_sound_override_without_setup_fallback() {
    let mut f = Fixture::new();
    let with_override = EntityEffectProfile::live(
        0,
        0,
        0x2000_00DD,
        &NetworkEffectDescription {
            sound_table_id: Some(0x2000_00EE),
            ..Default::default()
        },
    );
    let first = f.ready_live(GUID, with_override);
    assert_eq!(
        f.controller.take_events(),
        [EffectEvent::SoundTableChanged {
            owner: first,
            sound_table: Some(0x2000_00EE)
        }]
    );

    let absent = EntityEffectProfile::live(0, 0, 0x2000_00DD, &NetworkEffectDescription::default());
    let second = f.ready_live(0x7000_0002, absent);
    assert_eq!(
        f.controller.take_events(),
        [EffectEvent::SoundTableChanged {
            owner: second,
            sound_table: None
        }]
    );
}

#[test]
fn same_generation_description_refresh_replaces_and_clears_live_sound_table() {
    let mut f = Fixture::new();
    let mut profile = live_profile(Some(0x2000_00EE));
    let local = f.ready_live(GUID, profile.clone());
    f.controller.take_events();

    profile.apply_network_description(&NetworkEffectDescription {
        physics_script_table_id: Some(TABLE_DID),
        sound_table_id: Some(0x2000_00EF),
        ..Default::default()
    });
    assert!(
        f.controller
            .on_live_entity_description_changed(GUID, profile.clone())
    );
    profile.apply_network_description(&NetworkEffectDescription {
        physics_script_table_id: Some(TABLE_DID),
        sound_table_id: Some(0),
        ..Default::default()
    });
    assert!(
        f.controller
            .on_live_entity_description_changed(GUID, profile)
    );

    assert_eq!(
        f.controller.take_events(),
        [
            EffectEvent::SoundTableChanged {
                owner: local,
                sound_table: Some(0x2000_00EF)
            },
            EffectEvent::SoundTableChanged {
                owner: local,
                sound_table: None
            }
        ]
    );
}

fn hook(data: HookData) -> AnimationHook {
    AnimationHook { direction: 0, data }
}

#[test]
fn call_pes_and_default_hooks_enqueue_through_owning_profiles() {
    const NESTED: u32 = 0x3300_0044;
    let mut f = Fixture::new();
    f.add_script(NESTED, 4, 0.0);
    let local = f.ready_live(GUID, live_profile(None));

    let position = Vec3::new(1.0, 2.0, 3.0);
    f.ctx_call(|c, ctx| c.on_hook(ctx, local, position, &hook(HookData::DefaultScript)));
    f.ctx_call(|c, ctx| {
        c.on_hook(
            ctx,
            local,
            position,
            &hook(HookData::CallPes {
                pes: NESTED,
                pause: 0.0,
            }),
        );
    });
    f.tick(0.0);

    assert_eq!(f.emitter_ids(), [2, 4]);
}

#[test]
fn runner_router_controller_path_executes_nested_call_pes() {
    const OUTER: u32 = 0x3300_0043;
    const NESTED: u32 = 0x3300_0044;
    let mut f = Fixture::new();
    f.add_hook_script(
        OUTER,
        hook(HookData::CallPes {
            pes: NESTED,
            pause: 0.0,
        }),
        0.0,
    );
    f.add_script(NESTED, 4, 0.0);
    f.ready_live(GUID, live_profile(None));

    f.handle_direct(GUID, OUTER);
    f.tick(0.0);

    assert_eq!(f.calls.len(), 2);
    assert!(matches!(f.calls[0].2.data, HookData::CallPes { .. }));
    assert_eq!(emitter_info_id(&f.calls[1].2), 4);
}

#[test]
fn default_script_part_resolves_attached_child_and_uses_child_profile() {
    let mut f = Fixture::new();
    let parent = f.ready_live(GUID, live_profile(None));
    let child = f.ready_live(0x7000_0002, live_profile(None));
    f.owners.children.insert((parent, 7), child);

    f.ctx_call(|c, ctx| {
        c.on_hook(
            ctx,
            parent,
            Vec3::ZERO,
            &hook(HookData::DefaultScriptPart { part_index: 7 }),
        );
    });
    f.tick(0.0);

    assert_eq!(f.calls.len(), 1);
    assert_eq!(f.calls[0].0, child);
    assert_eq!(emitter_info_id(&f.calls[0].2), 2);
}

#[test]
fn attached_child_advances_through_eligible_parent_while_cellless() {
    let mut f = Fixture::new();
    let parent = f.ready_live(GUID, live_profile(None));
    let child = f.ready_live(0x7000_0002, live_profile(None));
    f.owners.parents.insert(child, parent);
    f.set_in_cell(child, false);

    assert!(f.ctx_call(|c, ctx| c.play_default(ctx, child)));
    f.tick(0.0);
    assert_eq!(f.calls.len(), 1);
    assert_eq!(f.calls[0].0, child);

    f.set_in_cell(parent, false);
    assert!(!f.ctx_call(|c, ctx| c.play_default(ctx, child)));
}

#[test]
fn missing_direct_script_reports_owner_and_did() {
    const MISSING: u32 = 0x3300_DEAD;
    let mut f = Fixture::new();
    let local = f.ready_live(GUID, live_profile(None));
    f.handle_direct(GUID, MISSING);
    let diagnostics = f.runner.take_diagnostics();
    assert_eq!(diagnostics.len(), 1);
    assert!(diagnostics[0].contains(&format!("0x{local:08X}")));
    assert!(diagnostics[0].contains("0x3300DEAD"));
}

#[test]
fn typed_table_load_failure_reports_owner_table_and_error() {
    let mut f = Fixture::with_tables(PhysicsScriptTableResolver::new(|_| {
        Err(TableLoadError("fixture corrupt table".into()))
    }));
    let local = f.ready_live(GUID, live_profile(None));
    f.handle_typed(GUID, RAW_TYPE, 0.5);
    let diagnostics = f.controller.take_diagnostics();
    assert_eq!(diagnostics.len(), 1);
    assert!(diagnostics[0].contains(&format!("0x{local:08X}")));
    assert!(diagnostics[0].contains("0x34000033"));
    assert!(diagnostics[0].contains("fixture corrupt table"));
}

#[test]
fn static_owner_retains_setup_table_and_is_removed_symmetrically() {
    let mut f = Fixture::new();
    let profile = EntityEffectProfile::dat_static(0, TABLE_DID, 0x2000_00DD);
    f.controller
        .on_dat_static_entity_ready(&mut f.runner, 55, Vec3::new(1.0, 2.0, 3.0), profile);
    assert_eq!(
        f.controller.take_events(),
        [EffectEvent::SoundTableChanged {
            owner: 55,
            sound_table: Some(0x2000_00DD)
        }]
    );
    assert!(f.ctx_call(|c, ctx| c.play_typed(ctx, 55, RAW_TYPE, 0.5)));
    f.tick(0.0);
    assert_eq!(f.emitter_ids(), [2]);

    f.controller.on_dat_static_entity_removed(&mut f.runner, 55);
    assert!(!f.ctx_call(|c, ctx| c.play_typed(ctx, 55, RAW_TYPE, 0.5)));
}

#[test]
fn registered_synthetic_owner_advances_through_global_runner_eligibility() {
    const SKY: u32 = 0xF000_0042;
    let mut f = Fixture::new();
    f.controller.register_synthetic_owner(SKY);
    assert!(f.runner.play_direct(SKY, DIRECT_DID));
    f.tick(0.0);
    assert_eq!(f.calls.len(), 1);
    assert_eq!(f.calls[0].0, SKY);
    f.controller.unregister_synthetic_owner(SKY);
    assert!(!f.controller.can_advance_owner(&f.owners, SKY));
}

#[test]
fn refresh_live_owner_poses_uses_current_world_position_at_dispatch() {
    let mut f = Fixture::new();
    f.add_script(DIRECT_DID, 1, 1.0);
    let local = f.ready_live(GUID, live_profile(None));
    f.handle_direct(GUID, DIRECT_DID);
    f.owners.roots.get_mut(&local).unwrap().position = Vec3::new(10.0, 20.0, 30.0);
    f.controller.mark_live_owner_pose_dirty(GUID);

    f.ctx_call(|c, ctx| c.refresh_live_owner_poses(ctx));
    f.tick(1.0);

    assert_eq!(f.calls.len(), 1);
    assert_eq!(f.calls[0].1, Vec3::new(10.0, 20.0, 30.0));
}

#[test]
fn pose_refresh_workset_visits_only_dirty_owner_across_dense_ready_set() {
    let mut f = Fixture::new();
    let mut changed = (0, 0);
    for i in 0..2_000u32 {
        let guid = 0x7100_0000 + i;
        let local = f.ready_live(guid, live_profile(None));
        if i == 777 {
            changed = (guid, local);
        }
    }

    f.ctx_call(|c, ctx| c.refresh_live_owner_poses(ctx));
    assert_eq!(f.controller.last_pose_refresh_owner_visit_count(), 0);

    f.poses
        .publish(
            changed.1,
            Mat4::from_translation(Vec3::new(7.0, 8.0, 9.0)),
            &[],
            0,
            None,
        )
        .unwrap();
    for owner in f.poses.take_changed_owners() {
        f.controller.on_pose_changed(owner);
    }
    f.ctx_call(|c, ctx| c.refresh_live_owner_poses(ctx));
    assert_eq!(f.controller.last_pose_refresh_owner_visit_count(), 1);
    assert_eq!(
        f.runner.owner_anchor(changed.1),
        Some(Vec3::new(7.0, 8.0, 9.0))
    );

    f.ctx_call(|c, ctx| c.refresh_live_owner_poses(ctx));
    assert_eq!(f.controller.last_pose_refresh_owner_visit_count(), 0);
}

#[test]
fn pose_refresh_workset_rejects_deleted_incarnation_after_guid_reuse() {
    let mut f = Fixture::new();
    let first = f.ready_live(GUID, live_profile(None));
    f.controller.mark_live_owner_pose_dirty(GUID);
    f.unregister(GUID, first);

    f.ready_live(GUID, live_profile(None));
    f.controller.mark_live_owner_pose_dirty(GUID);
    f.ctx_call(|c, ctx| c.refresh_live_owner_poses(ctx));

    assert_eq!(f.controller.last_pose_refresh_owner_visit_count(), 1);
}

#[test]
fn clear_network_state_preserves_dat_static_profiles() {
    let mut f = Fixture::new();
    f.ready_live(GUID, live_profile(None));
    f.controller.on_dat_static_entity_ready(
        &mut f.runner,
        55,
        Vec3::ZERO,
        EntityEffectProfile::dat_static(0, TABLE_DID, 0),
    );
    f.handle_direct(0x7000_0009, DIRECT_DID);

    f.controller.clear_network_state(&mut f.runner);

    assert_eq!(f.controller.pending_packet_count(), 0);
    assert_eq!(f.controller.ready_owner_count(), 1);
    assert!(f.ctx_call(|c, ctx| c.play_typed(ctx, 55, RAW_TYPE, 0.5)));
}
