//! The presenter's bookkeeping: which objects animate, following their
//! views, dropping them, and hooks reaching their destinations. Scenarios
//! after OpenAC `LiveEntityAnimationSchedulerTests` (an object without
//! animation stays out of the animation workset; a replaced owner's state
//! never reaches its replacement) on a hand-made Setup and animation.

use std::collections::HashMap;
use std::sync::Arc;

use ac_dat::objects::{AnimationHook, HookData, Setup};
use ac_motion::{AnimationLoader, MotionTableRules};
use ac_proto::{ModelData, Position, Quaternion, Vector3};
use ac_runtime::world::{ObjectKey, ObjectView, RadarFacts};
use glam::Vec3;

use super::source::empty_table;
use super::test_support::{MapLoader, setup, two_frame_animation};
use super::*;

const ANIMATION: u32 = 0x0300_0001;

struct FakeSource {
    setups: HashMap<u32, Arc<Setup>>,
    loader: Arc<MapLoader>,
    empty: Arc<MotionTableRules>,
}

impl FakeSource {
    fn new(animation: ac_dat::objects::Animation) -> Self {
        let mut animated = setup(1);
        animated.id = 0x0200_0002;
        animated.default_animation = ANIMATION;
        let mut loader = MapLoader::default();
        loader.add(ANIMATION, animation);
        Self {
            setups: HashMap::from([
                (0x0200_0001, Arc::new(setup(1))),
                (0x0200_0002, Arc::new(animated)),
            ]),
            loader: Arc::new(loader),
            empty: Arc::new(MotionTableRules::new(Arc::new(empty_table()))),
        }
    }
}

impl AnimationSource for FakeSource {
    fn setup(&self, id: u32) -> Option<Arc<Setup>> {
        self.setups.get(&id).cloned()
    }
    fn motion_table(&mut self, _id: u32) -> Option<Arc<MotionTableRules>> {
        None
    }
    fn empty_motion_table(&mut self) -> Arc<MotionTableRules> {
        Arc::clone(&self.empty)
    }
    fn animations(&self) -> Arc<dyn AnimationLoader + Send + Sync> {
        self.loader.clone()
    }
}

fn view(local_id: u32, incarnation: u16, setup_id: u32) -> ObjectView {
    ObjectView {
        key: ObjectKey {
            local_id,
            incarnation,
        },
        guid: 0x7000_0000 + local_id,
        name: None,
        setup_id: Some(setup_id),
        position: Some(Position {
            cell_id: 0xA9B4_0019,
            origin: Vector3 {
                x: 10.0,
                y: 10.0,
                z: 0.0,
            },
            orientation: Quaternion::IDENTITY,
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
        effects: ac_runtime::world::EffectFacts::default(),
        step_blend: None,
    }
}

fn tick(p: &mut AnimationPresenter, dt: f64) {
    p.tick(dt, Some(Vec3::ZERO), |_| Some(Vec3::ONE), |_, l| Some(l));
}

#[test]
fn only_objects_with_motion_or_a_default_animation_animate() {
    let mut source = FakeSource::new(two_frame_animation());
    let mut p = AnimationPresenter::new();
    let still = view(1, 1, 0x0200_0001);
    let animated = view(2, 1, 0x0200_0002);
    p.sync([&still, &animated].into_iter(), &mut source);
    assert!(p.object(1).is_none());
    assert!(p.object(2).is_some());
    assert_eq!(p.counts(), (1, 0));
}

#[test]
fn the_default_animation_poses_the_parts_over_object_quanta() {
    let mut source = FakeSource::new(two_frame_animation());
    let mut p = AnimationPresenter::new();
    let animated = view(2, 1, 0x0200_0002);
    p.sync([&animated].into_iter(), &mut source);
    let rest = p.object(2).unwrap().poses[0];
    tick(&mut p, 1.0 / 30.0);
    tick(&mut p, 1.0 / 60.0);
    let posed = p.object(2).unwrap().poses[0];
    assert_ne!(posed, rest);
    assert!(posed.visual.is_finite());
}

#[test]
fn a_new_incarnation_or_a_departure_drops_the_old_animation() {
    let mut source = FakeSource::new(two_frame_animation());
    let mut p = AnimationPresenter::new();
    p.sync([&view(2, 1, 0x0200_0002)].into_iter(), &mut source);
    tick(&mut p, 0.1);
    let advanced = p.object(2).unwrap().poses[0];
    // The server re-created the object: a fresh animation from its rest.
    p.sync([&view(2, 2, 0x0200_0002)].into_iter(), &mut source);
    assert_eq!(p.object(2).unwrap().key.incarnation, 2);
    assert_ne!(p.object(2).unwrap().poses[0], advanced);
    p.sync(std::iter::empty(), &mut source);
    assert!(p.object(2).is_none());
}

#[test]
fn a_far_object_sleeps_and_holds_its_pose() {
    let mut source = FakeSource::new(two_frame_animation());
    let mut p = AnimationPresenter::new();
    p.sync([&view(2, 1, 0x0200_0002)].into_iter(), &mut source);
    let rest = p.object(2).unwrap().poses[0];
    // The player is 200 m away: beyond the 96 m activity bubble.
    p.tick(
        0.1,
        Some(Vec3::new(200.0, 0.0, 0.0)),
        |_| Some(Vec3::ZERO),
        |_, l| Some(l),
    );
    assert_eq!(p.object(2).unwrap().poses[0], rest);
}

#[test]
fn a_hidden_part_hook_hides_the_part_and_sounds_reach_the_audio() {
    let mut animation = two_frame_animation();
    animation.part_frames[0].hooks = vec![
        AnimationHook {
            direction: 0,
            data: HookData::TransparentPart {
                part_index: 0,
                start: 1.0,
                end: 1.0,
                time: 0.0,
            },
        },
        AnimationHook {
            direction: 0,
            data: HookData::Sound { id: 0x0A00_0001 },
        },
        AnimationHook {
            direction: 0,
            data: HookData::CreateParticle {
                emitter_info_id: 0x3200_0001,
                part_index: 0,
                offset: ac_dat::types::Frame::default(),
                emitter_id: 1,
            },
        },
    ];
    animation.part_frames[1].hooks = animation.part_frames[0].hooks.clone();
    let mut source = FakeSource::new(animation);
    let mut p = AnimationPresenter::new();
    p.sync([&view(2, 1, 0x0200_0002)].into_iter(), &mut source);
    for _ in 0..6 {
        tick(&mut p, 1.0 / 30.0);
    }
    assert!(p.part_hidden(HookOwner::Object(2), 0));
    let sounds = p.take_sounds();
    assert!(!sounds.is_empty());
    assert_eq!(sounds[0].guid, 0x7000_0002);
    assert_eq!(sounds[0].position, Vec3::ONE);
    assert!(!p.take_effects().is_empty());
    // Taken once.
    assert!(p.take_sounds().is_empty());
}

#[test]
fn statics_with_a_default_animation_animate_until_their_landblock_goes() {
    let mut source = FakeSource::new(two_frame_animation());
    let mut p = AnimationPresenter::new();
    let key = StaticKey {
        landblock: 0xA9B4_0000,
        cell: 0,
        index: 3,
    };
    let still = StaticKey { index: 4, ..key };
    assert!(p.register_static(key, 0x0200_0002, Vec3::ZERO, &mut source));
    assert!(!p.register_static(still, 0x0200_0001, Vec3::ZERO, &mut source));
    tick(&mut p, 1.0 / 30.0);
    assert!(p.static_animation(key).unwrap().poses[0].visual.w_axis.x > 0.0);
    p.drop_statics(0xA9B4_0000);
    assert!(p.static_animation(key).is_none());
}

#[test]
fn part_fades_set_each_parts_opacity_and_an_invisible_part_is_left_out() {
    // OpenAC WbDrawDispatcher: a part's opacity is the object's times one
    // minus its part fade; a part at translucency 1 is not drawn.
    use crate::meshing::MeshRef;
    use glam::Mat4;
    let mut presenter = AnimationPresenter::new();
    let owner = HookOwner::Object(7);
    let part = |id: u32| MeshRef {
        gfx_obj_id: id,
        transform: Mat4::IDENTITY,
        variant: 0,
    };
    let parts = [part(0x0100_0001), part(0x0100_0002), part(0x0100_0003)];
    let place = |_: usize, p: &MeshRef| p.transform;
    // No fades and a solid object: no opacities.
    let (drawn, opacities) = presenter.faded_parts(owner, 1.0, &parts, place);
    assert_eq!(drawn.len(), 3);
    assert_eq!(opacities, None);
    // A whole-object translucency reaches every part.
    let (_, opacities) = presenter.faded_parts(owner, 0.5, &parts, place);
    assert_eq!(opacities, Some(vec![0.5; 3]));
    // Part 1 half faded, part 2 gone (instant fades).
    presenter.start_part_fade(owner, 1, 0.0, 0.5, 0.0);
    presenter.start_part_fade(owner, 2, 0.0, 1.0, 0.0);
    let (drawn, opacities) = presenter.faded_parts(owner, 0.8, &parts, place);
    assert_eq!(
        drawn.iter().map(|p| p.gfx_obj_id).collect::<Vec<_>>(),
        [0x0100_0001, 0x0100_0002]
    );
    let opacities = opacities.unwrap();
    assert!((opacities[0] - 0.8).abs() < 1e-6 && (opacities[1] - 0.4).abs() < 1e-6);
}
