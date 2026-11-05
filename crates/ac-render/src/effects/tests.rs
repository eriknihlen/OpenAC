//! The window's effects against in-memory records: server objects and dat
//! statics as effect owners, the server's messages, and what the effects
//! hand back to the window. After OpenAC's presentation wiring of
//! `EntityScriptActivator`, `EntityEffectController` and the hook sinks
//! (`EntityScriptActivatorTests`, `EntityEffectControllerTests`) and the
//! game's effect routing.

use std::collections::HashMap;
use std::sync::Arc;

use ac_dat::objects::{AnimationHook, HookData, PhysicsScriptTableData, ScriptAndModData, Setup};
use ac_dat::types::{Frame, Quaternion, Vector3};
use ac_motion::{AnimationLoader, MotionTableRules};
use ac_proto::{ModelData, Position};
use ac_runtime::session::PresentationEvent;
use ac_runtime::world::{
    EffectFacts, ObjectDelta, ObjectKey, ObjectView, RadarFacts, WorldSnapshot,
};
use ac_vfx::{
    ConstantRandom, DotNetRandom, EmitterDesc, EmitterDescRegistry, EmitterKind,
    LoadedPhysicsScript, ParticleGfxCache, ParticleSystem, ParticleType, PhysicsScriptRunner,
    PhysicsScriptTableResolver, VfxWorld,
};
use glam::{Mat4, Vec3};

use super::*;
use crate::animation::source::empty_table;
use crate::animation::test_support::{MapLoader, setup};
use crate::assets::StaticInstance;

const SCRIPT: u32 = 0x3300_00AA;
const SOUND_SCRIPT: u32 = 0x3300_00AB;
const FADE_SCRIPT: u32 = 0x3300_00AC;
const LIGHT_OFF_SCRIPT: u32 = 0x3300_00AD;
const HIDDEN_SCRIPT: u32 = 0x3300_00AE;
const UNHIDE_SCRIPT: u32 = 0x3300_00AF;
/// A physics script table naming the two hidden-transition scripts.
const TABLE: u32 = 0x3400_0004;
/// The logical emitter id the hidden script creates and the unhide script
/// stops (the human table's Hidden and UnHide scripts use 1000..1013).
const BUBBLES: u32 = 1000;
const EMITTER: u32 = 0x3200_0100;
const SETUP: u32 = 0x0200_0001;
const PLAIN_SETUP: u32 = 0x0200_0002;
const CELL: u32 = 0xA9B4_0019;

struct Setups(HashMap<u32, Arc<Setup>>);

impl AnimationSource for Setups {
    fn setup(&self, id: u32) -> Option<Arc<Setup>> {
        self.0.get(&id).cloned()
    }
    fn motion_table(&mut self, _: u32) -> Option<Arc<MotionTableRules>> {
        None
    }
    fn empty_motion_table(&mut self) -> Arc<MotionTableRules> {
        Arc::new(MotionTableRules::new(Arc::new(empty_table())))
    }
    fn animations(&self) -> Arc<dyn AnimationLoader + Send + Sync> {
        Arc::new(MapLoader::default())
    }
}

fn setups() -> Setups {
    let mut with_script = setup(2);
    with_script.id = SETUP;
    with_script.default_script = SCRIPT;
    let mut plain = setup(1);
    plain.id = PLAIN_SETUP;
    Setups(HashMap::from([
        (SETUP, Arc::new(with_script)),
        (PLAIN_SETUP, Arc::new(plain)),
    ]))
}

fn create_particle() -> AnimationHook {
    AnimationHook {
        direction: 0,
        data: HookData::CreateParticle {
            emitter_info_id: EMITTER,
            part_index: u32::MAX,
            offset: Frame {
                origin: Vector3 {
                    x: 0.0,
                    y: 0.0,
                    z: 1.0,
                },
                orientation: Quaternion {
                    w: 1.0,
                    x: 0.0,
                    y: 0.0,
                    z: 0.0,
                },
            },
            emitter_id: 0,
        },
    }
}

fn effects() -> WindowEffects {
    let mut registry = EmitterDescRegistry::new();
    registry.register(EmitterDesc {
        dat_id: EMITTER,
        particle_type: ParticleType::Still,
        emitter_kind: EmitterKind::BirthratePerSec,
        max_particles: 4,
        initial_particles: 1,
        lifespan: 999.0,
        lifetime_min: 999.0,
        lifetime_max: 999.0,
        birthrate: 0.5,
        start_size: 0.5,
        end_size: 0.5,
        start_alpha: 1.0,
        end_alpha: 1.0,
        ..EmitterDesc::default()
    });
    let sound = AnimationHook {
        direction: 0,
        data: HookData::Sound { id: 0x0A00_0001 },
    };
    let fade = AnimationHook {
        direction: 0,
        data: HookData::TransparentPart {
            part_index: 1,
            start: 0.0,
            end: 1.0,
            time: 0.0,
        },
    };
    let scripts: HashMap<u32, Arc<LoadedPhysicsScript>> = [
        (SCRIPT, vec![(0.0, create_particle())]),
        (SOUND_SCRIPT, vec![(0.0, sound)]),
        (FADE_SCRIPT, vec![(0.0, fade)]),
        (
            HIDDEN_SCRIPT,
            vec![(
                0.0,
                AnimationHook {
                    direction: 0,
                    data: match create_particle().data {
                        HookData::CreateParticle {
                            emitter_info_id,
                            part_index,
                            offset,
                            ..
                        } => HookData::CreateParticle {
                            emitter_info_id,
                            part_index,
                            offset,
                            emitter_id: BUBBLES,
                        },
                        other => other,
                    },
                },
            )],
        ),
        (
            UNHIDE_SCRIPT,
            vec![(
                0.0,
                AnimationHook {
                    direction: 0,
                    data: HookData::StopParticle {
                        emitter_id: BUBBLES,
                    },
                },
            )],
        ),
        (
            LIGHT_OFF_SCRIPT,
            vec![(
                0.0,
                AnimationHook {
                    direction: 0,
                    data: HookData::SetLight { lights_on: false },
                },
            )],
        ),
    ]
    .into_iter()
    .map(|(id, hooks)| (id, Arc::new(LoadedPhysicsScript { id, hooks })))
    .collect();
    let vfx = VfxWorld::from_parts(
        ParticleSystem::new(registry, Box::new(DotNetRandom::new(42))),
        PhysicsScriptRunner::new(
            move |id| Ok(scripts.get(&id).cloned()),
            Box::new(ConstantRandom(0.5)),
        ),
        PhysicsScriptTableResolver::new(|id| {
            let entry = |script_id| PhysicsScriptTableData {
                scripts: vec![ScriptAndModData {
                    modifier: 1.0,
                    script_id,
                }],
            };
            Ok((id == TABLE).then(|| {
                Arc::new(PhysicsScriptTable {
                    id: TABLE,
                    script_table: vec![(0x76, entry(HIDDEN_SCRIPT)), (0x75, entry(UNHIDE_SCRIPT))],
                })
            }))
        }),
    );
    WindowEffects::from_world(vfx, ParticleGfxCache::new(None))
}

fn view(local_id: u32, guid: u32, setup_id: u32) -> ObjectView {
    ObjectView {
        key: ObjectKey {
            local_id,
            incarnation: 1,
        },
        guid,
        name: None,
        setup_id: Some(setup_id),
        position: Some(Position {
            cell_id: CELL,
            origin: ac_proto::Vector3 {
                x: 10.0,
                y: 20.0,
                z: 30.0,
            },
            orientation: ac_proto::Quaternion::IDENTITY,
        }),
        parent: None,
        placement: None,
        rest_placement: 0,
        scale: None,
        translucency: None,
        state: 0,
        velocity: None,
        model: ModelData::default(),
        motion_table_id: None,
        sound_table_id: None,
        radar: RadarFacts::default(),
        motion: None,
        effects: EffectFacts::default(),
        step_blend: None,
    }
}

fn scene(objects: Vec<ObjectView>) -> Scene {
    let mut scene = Scene::default();
    scene.apply(PresentationEvent::Snapshot(WorldSnapshot {
        player_guid: None,
        objects,
    }));
    scene
}

fn frame(effects: &mut WindowEffects, scene: Option<&Scene>, source: &Setups) {
    frame_with(effects, scene, source, &|_| true);
}

/// A frame with `resident` telling which cells' landblocks are loaded.
fn frame_with(
    effects: &mut WindowEffects,
    scene: Option<&Scene>,
    source: &Setups,
    resident: &dyn Fn(u32) -> bool,
) {
    frame_at_range(
        effects,
        scene,
        source,
        resident,
        ac_vfx::EXTENDED_RANGE_MULTIPLIER,
    );
}

/// A frame with the particle range setting's multiplier `particle_range`.
fn frame_at_range(
    effects: &mut WindowEffects,
    scene: Option<&Scene>,
    source: &Setups,
    resident: &dyn Fn(u32) -> bool,
    particle_range: f32,
) {
    let animations = AnimationPresenter::new();
    effects.frame(EffectsFrame {
        dt: 1.0 / 30.0,
        scene,
        to_frame: &|_, local| local,
        poses: &[],
        animations: &animations,
        landblock_offset: &|_| Vec3::ZERO,
        resident,
        hooks: Vec::new(),
        sky: None,
        sky_active: false,
        camera: Vec3::ZERO,
        source,
        particle_range,
    });
}

fn emitters_of(effects: &WindowEffects, owner: u32) -> Vec<Vec3> {
    effects
        .vfx()
        .particles()
        .emitters()
        .filter(|e| e.attached_object_id == owner)
        .map(|e| e.anchor_pos)
        .collect()
}

#[test]
fn a_server_object_plays_its_setups_default_script_at_its_place() {
    let source = setups();
    let mut fx = effects();
    let s = scene(vec![
        view(7, 0x5000_0007, SETUP),
        view(8, 0x5000_0008, PLAIN_SETUP),
    ]);
    frame(&mut fx, Some(&s), &source);
    assert_eq!(fx.owner_counts(), (2, 0));
    // The create-particle hook's offset is a metre above the root.
    assert_eq!(emitters_of(&fx, 7), [Vec3::new(10.0, 20.0, 31.0)]);
    assert!(emitters_of(&fx, 8).is_empty());
}

#[test]
fn a_play_script_for_an_object_not_here_yet_waits_for_it() {
    let source = setups();
    let mut fx = effects();
    fx.queue_server(ServerEffect::PlayScript {
        guid: 0x5000_0008,
        script_id: SCRIPT,
    });
    frame(&mut fx, Some(&scene(Vec::new())), &source);
    assert_eq!(fx.vfx().particles().active_emitter_count(), 0);
    assert_eq!(fx.vfx().effects().pending_packet_count(), 1);
    let s = scene(vec![view(8, 0x5000_0008, PLAIN_SETUP)]);
    frame(&mut fx, Some(&s), &source);
    frame(&mut fx, Some(&s), &source);
    assert_eq!(emitters_of(&fx, 8).len(), 1);
}

#[test]
fn an_object_that_leaves_ends_its_effects_and_its_emitters_retire() {
    let source = setups();
    let mut fx = effects();
    let s = scene(vec![view(7, 0x5000_0007, SETUP)]);
    frame(&mut fx, Some(&s), &source);
    assert_eq!(emitters_of(&fx, 7).len(), 1);
    let mut s = s;
    s.apply(PresentationEvent::Deltas(vec![ObjectDelta::Remove(
        ObjectKey {
            local_id: 7,
            incarnation: 1,
        },
    )]));
    frame(&mut fx, Some(&s), &source);
    assert_eq!(fx.owner_counts(), (0, 0));
    assert_eq!(fx.vfx().particles().active_emitter_count(), 0);
}

/// Whether every emitter of owner 7 is drawn (`None` when it has none).
fn owner_7_drawn(fx: &WindowEffects) -> Option<bool> {
    let drawn: Vec<bool> = fx
        .vfx()
        .particles()
        .emitters()
        .filter(|e| e.attached_object_id == 7)
        .map(|e| e.presentation_visible && e.simulation_enabled)
        .collect();
    (!drawn.is_empty()).then(|| drawn.iter().all(|d| *d))
}

/// OpenAC wires the particle sink's presentation visibility to the live
/// object's projection residency (`LivePresentationComposition`
/// "particle projection visibility"), never to its hidden flag; its recall
/// stress test (`LiveEntityLifecycleStressTests.
/// RepeatedRetailRecallMotion_HiddenTeleportUnhide_DrainsEveryPresentationOwner`)
/// keeps the recall emitter through the hidden teleport. A hidden object
/// stays in its cell (the game's hiding leaves it there), so the
/// portal bubbles on a hidden character keep drawing.
#[test]
fn a_hidden_object_keeps_drawing_its_emitters() {
    let source = setups();
    let mut fx = effects();
    let mut s = scene(vec![view(7, 0x5000_0007, SETUP)]);
    frame(&mut fx, Some(&s), &source);
    assert_eq!(owner_7_drawn(&fx), Some(true));
    let mut hidden = view(7, 0x5000_0007, SETUP);
    hidden.state = ac_core::physics::PhysicsState::HIDDEN.bits();
    s.apply(PresentationEvent::Deltas(vec![ObjectDelta::Upsert(
        Box::new(hidden),
    )]));
    frame(&mut fx, Some(&s), &source);
    frame(&mut fx, Some(&s), &source);
    assert_eq!(owner_7_drawn(&fx), Some(true), "hidden, still drawn");
    s.apply(PresentationEvent::Deltas(vec![ObjectDelta::Upsert(
        Box::new(view(7, 0x5000_0007, SETUP)),
    )]));
    frame(&mut fx, Some(&s), &source);
    assert_eq!(owner_7_drawn(&fx), Some(true));
}

/// OpenAC `ParticleHookSinkTests.PendingProjection_HidesPresentationWhileContinuingLiveAnchorRefresh`
/// at the window: an object whose landblock is not resident hides and
/// pauses its emitters, and shows them again when it is.
#[test]
fn an_object_outside_the_resident_world_hides_its_emitters_until_it_is_back() {
    let source = setups();
    let mut fx = effects();
    let s = scene(vec![view(7, 0x5000_0007, SETUP)]);
    frame(&mut fx, Some(&s), &source);
    assert_eq!(owner_7_drawn(&fx), Some(true));
    frame_with(&mut fx, Some(&s), &source, &|cell| cell != CELL);
    assert_eq!(owner_7_drawn(&fx), Some(false), "its landblock left");
    frame(&mut fx, Some(&s), &source);
    assert_eq!(owner_7_drawn(&fx), Some(true), "its landblock is back");
}

#[test]
fn statics_with_a_default_script_become_owners_until_their_landblock_leaves() {
    let source = setups();
    let mut fx = effects();
    let at = |x: f32| StaticInstance {
        model_id: SETUP,
        transform: Mat4::from_translation(Vec3::new(x, 30.0, 5.0)),
        is_building_shell: false,
    };
    let plain = StaticInstance {
        model_id: PLAIN_SETUP,
        ..at(0.0)
    };
    let statics = [at(50.0), plain];
    let key = |index: u32| StaticKey {
        landblock: 0xA9B4_0000,
        cell: 0,
        index,
    };
    fx.register_statics(
        0xA9B4_0000,
        Vec3::new(1000.0, 0.0, 0.0),
        statics.iter().enumerate().map(|(i, s)| (key(i as u32), s)),
        |_| false,
        &source,
    );
    // Only the static with a default script (or an animation) is an owner.
    assert_eq!(fx.owner_counts(), (0, 1));
    frame(&mut fx, None, &source);
    let anchors: Vec<Vec3> = fx
        .vfx()
        .particles()
        .emitters()
        .map(|e| e.anchor_pos)
        .collect();
    assert_eq!(anchors, [Vec3::new(1050.0, 30.0, 6.0)]);
    // It stands in the land cell under it: x 50 is column 2, y 30 row 1.
    let cell = fx
        .vfx()
        .particles()
        .emitters()
        .next()
        .unwrap()
        .owner_cell_id;
    assert_eq!(cell, 0xA9B4_0000 | (2 * 8 + 1 + 1));
    fx.drop_statics(0xA9B4_0000);
    assert_eq!(fx.owner_counts(), (0, 0));
    assert_eq!(fx.vfx().particles().active_emitter_count(), 0);
}

#[test]
fn script_sounds_and_server_sounds_go_to_the_audio_with_the_owners_guid() {
    let source = setups();
    let mut fx = effects();
    let s = scene(vec![view(8, 0x5000_0008, PLAIN_SETUP)]);
    frame(&mut fx, Some(&s), &source);
    fx.queue_server(ServerEffect::PlayScript {
        guid: 0x5000_0008,
        script_id: SOUND_SCRIPT,
    });
    fx.queue_server(ServerEffect::Sound {
        guid: 0x5000_0008,
        sound_type: 3,
        volume: 0.5,
    });
    frame(&mut fx, Some(&s), &source);
    frame(&mut fx, Some(&s), &source);
    let sounds = fx.take_sounds();
    assert!(sounds.contains(&EffectSound::Server {
        guid: 0x5000_0008,
        position: Vec3::new(10.0, 20.0, 30.0),
        sound_type: 3,
        volume: 0.5,
    }));
    assert!(sounds.iter().any(|s| matches!(
        s,
        EffectSound::Hook {
            guid: 0x5000_0008,
            hook: HookData::Sound { id: 0x0A00_0001 },
            ..
        }
    )));
}

#[test]
fn a_scripts_part_fade_reaches_the_animation_fades() {
    let source = setups();
    let mut fx = effects();
    let s = scene(vec![view(8, 0x5000_0008, PLAIN_SETUP)]);
    frame(&mut fx, Some(&s), &source);
    fx.queue_server(ServerEffect::PlayScript {
        guid: 0x5000_0008,
        script_id: FADE_SCRIPT,
    });
    frame(&mut fx, Some(&s), &source);
    frame(&mut fx, Some(&s), &source);
    assert_eq!(
        fx.take_part_fades(),
        [ScriptPartFade {
            owner: HookOwner::Object(8),
            part: 1,
            start: 0.0,
            end: 1.0,
            time: 0.0,
        }]
    );
}

#[test]
fn a_scripts_set_light_hook_switches_its_objects_lights() {
    // OpenAC LightingHookSink.OnHook, fed by the script runner.
    let source = setups();
    let mut fx = effects();
    let s = scene(vec![view(8, 0x5000_0008, PLAIN_SETUP)]);
    frame(&mut fx, Some(&s), &source);
    fx.queue_server(ServerEffect::PlayScript {
        guid: 0x5000_0008,
        script_id: LIGHT_OFF_SCRIPT,
    });
    frame(&mut fx, Some(&s), &source);
    frame(&mut fx, Some(&s), &source);
    assert_eq!(fx.take_light_switches(), [(8, false)]);
}

#[test]
fn outdoor_cells_count_from_one_by_column_then_row() {
    assert_eq!(outdoor_cell_index(Vec3::ZERO), 1);
    assert_eq!(outdoor_cell_index(Vec3::new(23.9, 191.0, 0.0)), 8);
    assert_eq!(outdoor_cell_index(Vec3::new(191.0, 0.0, 0.0)), 57);
    assert_eq!(outdoor_cell_index(Vec3::new(-1.0, 500.0, 0.0)), 8);
}

/// OpenAC `LiveEntityPresentationController.ApplyPendingTransitions`: an
/// object going hidden plays its Hidden script (0x76) and coming back its
/// UnHide script (0x75), whose stop hooks end the bubbles. A teleport on the
/// server plays the character's Hide script (0x74, the same emitters as Hidden),
/// hides it and shows it again at the destination; without the UnHide play
/// those bubbles came back with the character and never ended.
#[test]
fn bubbles_raised_before_going_hidden_end_when_the_object_comes_back() {
    let source = setups();
    let mut fx = effects();
    let with_table = |state: u32| {
        let mut v = view(9, 0x5000_0009, PLAIN_SETUP);
        v.effects.physics_script_table_id = Some(TABLE);
        v.state = state;
        v
    };
    let bubbles = |fx: &WindowEffects| {
        fx.vfx()
            .particles()
            .emitters()
            .filter(|e| e.attached_object_id == 9)
            .map(|e| e.finished)
            .collect::<Vec<_>>()
    };
    let mut s = scene(vec![with_table(0)]);
    frame(&mut fx, Some(&s), &source);
    fx.queue_server(ServerEffect::PlayScript {
        guid: 0x5000_0009,
        script_id: HIDDEN_SCRIPT,
    });
    frame(&mut fx, Some(&s), &source);
    frame(&mut fx, Some(&s), &source);
    assert_eq!(bubbles(&fx), [false], "the server's script raised them");

    let hidden = ac_core::physics::PhysicsState::HIDDEN.bits();
    s.apply(PresentationEvent::Deltas(vec![ObjectDelta::Upsert(
        Box::new(with_table(hidden)),
    )]));
    frame(&mut fx, Some(&s), &source);
    frame(&mut fx, Some(&s), &source);
    let drawn_while_hidden: Vec<bool> = fx
        .vfx()
        .particles()
        .emitters()
        .filter(|e| e.attached_object_id == 9 && !e.finished)
        .map(|e| e.presentation_visible)
        .collect();
    assert!(
        !drawn_while_hidden.is_empty() && drawn_while_hidden.iter().all(|v| *v),
        "the bubbles draw while the object is hidden: {drawn_while_hidden:?}"
    );
    s.apply(PresentationEvent::Deltas(vec![ObjectDelta::Upsert(
        Box::new(with_table(0)),
    )]));
    frame(&mut fx, Some(&s), &source);
    frame(&mut fx, Some(&s), &source);
    let after = bubbles(&fx);
    assert!(
        !after.is_empty() && after.iter().all(|finished| *finished),
        "the unhide script stopped them: {after:?}"
    );
}

/// The server plays the Hide script and hides the character in the same tick, so
/// the window meets the script with its owner already hidden: the script
/// runs while the owner is hidden (it stays in its cell), and its bubbles
/// must not outlive the UnHide that comes back with it.
#[test]
fn a_script_that_waited_out_the_hidden_state_ends_with_the_unhide() {
    let source = setups();
    let mut fx = effects();
    let with_table = |state: u32| {
        let mut v = view(9, 0x5000_0009, PLAIN_SETUP);
        v.effects.physics_script_table_id = Some(TABLE);
        v.state = state;
        v
    };
    let hidden = ac_core::physics::PhysicsState::HIDDEN.bits();
    let mut s = scene(vec![with_table(0)]);
    frame(&mut fx, Some(&s), &source);
    s.apply(PresentationEvent::Deltas(vec![ObjectDelta::Upsert(
        Box::new(with_table(hidden)),
    )]));
    fx.queue_server(ServerEffect::PlayScript {
        guid: 0x5000_0009,
        script_id: HIDDEN_SCRIPT,
    });
    for _ in 0..3 {
        frame(&mut fx, Some(&s), &source);
    }
    s.apply(PresentationEvent::Deltas(vec![ObjectDelta::Upsert(
        Box::new(with_table(0)),
    )]));
    for _ in 0..3 {
        frame(&mut fx, Some(&s), &source);
    }
    let live: Vec<bool> = fx
        .vfx()
        .particles()
        .emitters()
        .filter(|e| e.attached_object_id == 9)
        .map(|e| e.finished)
        .collect();
    assert!(
        live.iter().all(|finished| *finished),
        "no bubble outlives the unhide: {live:?}"
    );
}

/// OpenAC `RuntimeEntityRecord` starts every object from the constructor's
/// visible state (`RetailPhysicsStateTransitions.ConstructorState`) and
/// applies its described state on top, so an object created hidden queues
/// `BecameHidden`, and `LiveEntityPresentationController.OnLiveEntityReady`
/// plays its Hidden script (0x76) between preparing and replaying its
/// effects. The server creates a character logging in hidden (its "pink
/// bubble state") and shows it after LoginComplete: the window must
/// raise the bubbles at the first sight, and the UnHide must end them.
#[test]
fn an_object_first_seen_hidden_stands_in_its_bubbles_until_it_is_shown() {
    let source = setups();
    let mut fx = effects();
    let with_table = |state: u32| {
        let mut v = view(9, 0x5000_0009, PLAIN_SETUP);
        v.effects.physics_script_table_id = Some(TABLE);
        v.state = state;
        v
    };
    let bubbles = |fx: &WindowEffects| {
        fx.vfx()
            .particles()
            .emitters()
            .filter(|e| e.attached_object_id == 9)
            .map(|e| (e.finished, e.presentation_visible))
            .collect::<Vec<_>>()
    };
    let hidden = ac_core::physics::PhysicsState::HIDDEN.bits();
    let mut s = scene(vec![with_table(hidden)]);
    frame(&mut fx, Some(&s), &source);
    frame(&mut fx, Some(&s), &source);
    assert_eq!(
        bubbles(&fx),
        [(false, true)],
        "the Hidden script raised drawn bubbles at the first sight"
    );

    s.apply(PresentationEvent::Deltas(vec![ObjectDelta::Upsert(
        Box::new(with_table(0)),
    )]));
    frame(&mut fx, Some(&s), &source);
    frame(&mut fx, Some(&s), &source);
    let after = bubbles(&fx);
    assert!(
        !after.is_empty() && after.iter().all(|(finished, _)| *finished),
        "the unhide script stopped them: {after:?}"
    );
}

/// The constructor's state is visible: an object first seen visible has
/// no hidden edge and raises no bubbles.
#[test]
fn an_object_first_seen_visible_raises_no_bubbles() {
    let source = setups();
    let mut fx = effects();
    let mut v = view(9, 0x5000_0009, PLAIN_SETUP);
    v.effects.physics_script_table_id = Some(TABLE);
    let s = scene(vec![v]);
    frame(&mut fx, Some(&s), &source);
    frame(&mut fx, Some(&s), &source);
    assert_eq!(
        fx.vfx()
            .particles()
            .emitters()
            .filter(|e| e.attached_object_id == 9)
            .count(),
        0
    );
}

/// Whether owner 7's emitters passed the last view test.
fn owner_7_in_range(fx: &WindowEffects) -> Vec<bool> {
    fx.vfx()
        .particles()
        .emitters()
        .filter(|e| e.attached_object_id == 7)
        .map(|e| e.view_eligible)
        .collect()
}

/// A completed world view from `viewer` with the object's outdoor cell in
/// it.
fn complete_view(fx: &mut WindowEffects, viewer: Vec3) {
    let view = fx.vfx_mut().visibility_mut();
    view.begin_frame(viewer);
    view.use_world_view();
    view.mark_visible_landscape_cells([CELL]).unwrap();
    view.complete_frame();
}

/// The particle range setting is the frame's: "Retail" draws an emitter
/// out to its authored degrade distance (100 m here), "Extended" out to
/// twice it (OpenAC `SettingsParticleRangeSource`), and a change applies
/// on the next frame.
#[test]
fn the_particle_range_setting_decides_how_far_emitters_are_drawn() {
    use ac_host::settings::ParticleRange;
    let source = setups();
    let mut fx = effects();
    let s = scene(vec![view(7, 0x5000_0007, SETUP)]);
    let range = |r| crate::quality::particle_range_multiplier(r);
    frame_at_range(
        &mut fx,
        Some(&s),
        &source,
        &|_| true,
        range(ParticleRange::Extended),
    );
    // The owner stands at (10, 20, 30); the viewer 150 m off along x.
    let viewer = Vec3::new(160.0, 20.0, 30.0);
    complete_view(&mut fx, viewer);
    frame_at_range(
        &mut fx,
        Some(&s),
        &source,
        &|_| true,
        range(ParticleRange::Extended),
    );
    assert_eq!(
        owner_7_in_range(&fx),
        [true],
        "extended: 150 m is within 200 m"
    );
    complete_view(&mut fx, viewer);
    frame_at_range(
        &mut fx,
        Some(&s),
        &source,
        &|_| true,
        range(ParticleRange::Retail),
    );
    assert_eq!(
        owner_7_in_range(&fx),
        [false],
        "retail: 150 m is past 100 m"
    );
}
