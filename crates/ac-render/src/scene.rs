//! The window's own copy of what it draws, built from the session's
//! presentation stream. It can be dropped and rebuilt at any time.

use std::collections::HashMap;
use std::sync::mpsc::{Receiver, Sender, TryRecvError};
use std::time::Instant;

use ac_runtime::communication::{ChatLine, WindowRequest};
use ac_runtime::navigation::NavigationView;
use ac_runtime::session::interface::InterfaceEvent;
use ac_runtime::session::{PresentationEvent, SessionCommand};
use ac_runtime::world::{AppliedMotion, ObjectDelta, ObjectView, PlayerView};
use ac_ui::pregame::PreGameState;

/// A one-off the session asked the window to present.
#[derive(Debug, Clone, Copy, PartialEq)]
pub enum SceneEffect {
    /// An object plays a sound.
    Sound(ac_proto::effects::SoundEvent),
    /// An object plays a physics script by id.
    PlayScript(ac_proto::effects::PlayScript),
    /// An object plays the physics script of a type at an intensity.
    PlayScriptType(ac_proto::effects::PlayScriptType),
    /// The server changed the environment.
    AdminEnvirons(u32),
    /// The player entered portal space or arrived.
    PortalSpace(ac_runtime::world::transit::PortalSpaceEvent),
    /// A new snapshot replaced the world (portal space under way ends; one
    /// still under way is announced again after it).
    WorldReplaced,
}

/// Objects to draw, keyed by local id, the game clock and the effects not
/// yet presented.
#[derive(Debug, Default)]
pub struct Scene {
    player_guid: Option<u32>,
    /// Snapshots that brought a character into the world (a player where
    /// there was none, or another one).
    world_entries: u64,
    objects: HashMap<u32, ObjectView>,
    received_snapshot: bool,
    /// The server's tick count at the latest sync and when it arrived.
    game_time: Option<(f64, Instant)>,
    effects: Vec<SceneEffect>,
    /// Motion states objects took up, not yet taken by the animations.
    motions: Vec<AppliedMotion>,
    chat: Vec<ChatLine>,
    last_chat_sequence: Option<u64>,
    player_view: Option<PlayerView>,
    interface: Vec<InterfaceEvent>,
    pregame: PreGameState,
    status_text: Vec<String>,
    window_requests: Vec<WindowRequest>,
    link: Option<ac_runtime::session::LinkStatus>,
    navigation: NavigationView,
    /// The instant the session clock counts from, once the session sent it.
    session_clock: Option<Instant>,
}

impl Scene {
    /// Apply one message from the session.
    pub fn apply(&mut self, event: PresentationEvent) {
        match event {
            PresentationEvent::Snapshot(snapshot) => {
                if snapshot.player_guid.is_some() && snapshot.player_guid != self.player_guid {
                    self.world_entries += 1;
                }
                self.player_guid = snapshot.player_guid;
                self.objects = snapshot
                    .objects
                    .into_iter()
                    .map(|o| (o.key.local_id, o))
                    .collect();
                self.received_snapshot = true;
                self.effects.push(SceneEffect::WorldReplaced);
                // The snapshot holds each object's motion as it stands.
                self.motions.clear();
            }
            PresentationEvent::Motions(motions) => self.motions.extend(motions),
            PresentationEvent::Deltas(deltas) => {
                for delta in deltas {
                    match delta {
                        ObjectDelta::Upsert(view) => {
                            self.objects.insert(view.key.local_id, *view);
                        }
                        ObjectDelta::Remove(key) => {
                            self.objects.remove(&key.local_id);
                        }
                    }
                }
            }
            PresentationEvent::GameTime(ticks) => self.game_time = Some((ticks, Instant::now())),
            PresentationEvent::Sound(sound) => self.effects.push(SceneEffect::Sound(sound)),
            PresentationEvent::PlayScript(play) => {
                self.effects.push(SceneEffect::PlayScript(play));
            }
            PresentationEvent::PlayScriptType(play) => {
                self.effects.push(SceneEffect::PlayScriptType(play));
            }
            PresentationEvent::AdminEnvirons(change) => {
                self.effects.push(SceneEffect::AdminEnvirons(change));
            }
            PresentationEvent::Chat(lines) => {
                for line in lines {
                    // Around the attach a line can come in the transcript
                    // and again as new; the first copy stands.
                    if self.last_chat_sequence.is_some_and(|s| line.sequence <= s) {
                        continue;
                    }
                    self.last_chat_sequence = Some(line.sequence);
                    self.chat.push(line);
                }
            }
            PresentationEvent::Player(view) => self.player_view = Some(view),
            PresentationEvent::Interface(event) => self.interface.push(event),
            PresentationEvent::PortalSpace(event) => {
                self.effects.push(SceneEffect::PortalSpace(event));
            }
            PresentationEvent::PreGame(event) => self.pregame.apply(event),
            PresentationEvent::StatusText(text) => self.status_text.push(text),
            PresentationEvent::WindowRequest(request) => self.window_requests.push(request),
            PresentationEvent::Link(link) => self.link = Some(link),
            PresentationEvent::Navigation(view) => self.navigation = view,
            PresentationEvent::SessionClock(origin) => self.session_clock = Some(origin),
        }
    }

    /// Place the bodies the session's physics moves in fixed steps (the
    /// local player) where they are at `now`, a frame's time: each blends
    /// between its last two steps by the time since the last one ended,
    /// read on the session clock, so it moves on every frame however the
    /// frames and the session's passes fall. Until the session clock
    /// arrives such a body stays on its last step.
    pub fn present_at(&mut self, now: Instant) {
        let Some(origin) = self.session_clock else {
            return;
        };
        let session_now = now.saturating_duration_since(origin);
        for view in self.objects.values_mut() {
            if let (Some(position), Some(blend)) = (view.position.as_mut(), view.step_blend) {
                position.origin = blend.origin_at(session_now);
            }
        }
    }

    /// What the window draws of navigation, as the session last sent it.
    #[must_use]
    pub fn navigation(&self) -> &NavigationView {
        &self.navigation
    }

    /// The panels' facts that arrived since the last call, oldest first
    /// (the UI keeps its own copy of them).
    pub fn take_interface(&mut self) -> Vec<InterfaceEvent> {
        std::mem::take(&mut self.interface)
    }

    /// The pre-game screens' state: the connection, character selection
    /// and creation, the creation choices, the refusals not yet taken.
    pub fn pregame_mut(&mut self) -> &mut PreGameState {
        &mut self.pregame
    }

    /// The pre-game screens' state.
    #[must_use]
    pub fn pregame(&self) -> &PreGameState {
        &self.pregame
    }

    /// Status-box text that arrived since the last call, oldest first.
    pub fn take_status_text(&mut self) -> Vec<String> {
        std::mem::take(&mut self.status_text)
    }

    /// What client commands asked of the window since the last call.
    pub fn take_window_requests(&mut self) -> Vec<WindowRequest> {
        std::mem::take(&mut self.window_requests)
    }

    /// The link as the session last reported it.
    #[must_use]
    pub fn link(&self) -> Option<ac_runtime::session::LinkStatus> {
        self.link
    }

    /// The character is in the world (the snapshot named a player).
    #[must_use]
    pub fn in_world(&self) -> bool {
        self.player_guid.is_some()
    }

    /// Chat lines that arrived since the last call, oldest first.
    pub fn take_chat(&mut self) -> Vec<ChatLine> {
        std::mem::take(&mut self.chat)
    }

    /// The player's vitals, selection and panel options, once sent.
    #[must_use]
    pub fn player_view(&self) -> Option<&PlayerView> {
        self.player_view.as_ref()
    }

    /// Whether the attach snapshot arrived.
    #[must_use]
    pub fn is_ready(&self) -> bool {
        self.received_snapshot
    }

    /// The local player's object id: a character is in the world.
    #[must_use]
    pub fn player_guid(&self) -> Option<u32> {
        self.player_guid
    }

    /// How many times a character has entered the world in the snapshots
    /// seen: each log in (and an attach to a session in the world) counts
    /// one.
    #[must_use]
    pub fn world_entries(&self) -> u64 {
        self.world_entries
    }

    /// The local player's object.
    #[must_use]
    pub fn player(&self) -> Option<&ObjectView> {
        let guid = self.player_guid?;
        self.objects.values().find(|o| o.guid == guid)
    }

    /// The game clock now: the latest synced tick count advanced by one
    /// tick per second since it arrived (`None` before the first sync).
    #[must_use]
    pub fn game_ticks(&self, now: Instant) -> Option<f64> {
        self.game_time
            .map(|(ticks, at)| ticks + now.saturating_duration_since(at).as_secs_f64())
    }

    /// The tick count of the latest sync (changes when the server syncs).
    #[must_use]
    pub fn synced_ticks(&self) -> Option<f64> {
        self.game_time.map(|(ticks, _)| ticks)
    }

    /// The effects that arrived since the last call, oldest first.
    pub fn take_effects(&mut self) -> Vec<SceneEffect> {
        std::mem::take(&mut self.effects)
    }

    /// The motion states objects took up since the last call, in order.
    pub fn take_motions(&mut self) -> Vec<AppliedMotion> {
        std::mem::take(&mut self.motions)
    }

    /// An object by server id.
    #[must_use]
    pub fn object_by_guid(&self, guid: u32) -> Option<&ObjectView> {
        self.objects.values().find(|o| o.guid == guid)
    }

    /// Every object.
    pub fn objects(&self) -> impl Iterator<Item = &ObjectView> {
        self.objects.values()
    }

    /// How many objects.
    #[must_use]
    pub fn len(&self) -> usize {
        self.objects.len()
    }

    /// True with no objects.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.objects.is_empty()
    }
}

/// A session's presentation stream feeding a [`Scene`].
#[derive(Debug)]
pub struct SceneSource {
    receiver: Receiver<PresentationEvent>,
    scene: Scene,
    session_gone: bool,
    commands: Option<Sender<SessionCommand>>,
}

impl SceneSource {
    /// Follow a session through the receiver its attach was sent with.
    #[must_use]
    pub fn new(receiver: Receiver<PresentationEvent>) -> Self {
        Self {
            receiver,
            scene: Scene::default(),
            session_gone: false,
            commands: None,
        }
    }

    /// Also send the session what the window asks for (the UI's requests,
    /// the movement keys): the window's one way back to the session.
    #[must_use]
    pub fn with_commands(mut self, commands: Sender<SessionCommand>) -> Self {
        self.commands = Some(commands);
        self
    }

    /// Send `command` to the session. False when the window has no way
    /// back or the session is gone.
    pub fn send(&self, command: SessionCommand) -> bool {
        self.commands
            .as_ref()
            .is_some_and(|c| c.send(command).is_ok())
    }

    /// Apply everything that arrived since the last call.
    pub fn pump(&mut self) {
        loop {
            match self.receiver.try_recv() {
                Ok(event) => self.scene.apply(event),
                Err(TryRecvError::Empty) => break,
                Err(TryRecvError::Disconnected) => {
                    self.session_gone = true;
                    break;
                }
            }
        }
    }

    /// The scene as of the last pump.
    #[must_use]
    pub fn scene(&self) -> &Scene {
        &self.scene
    }

    /// The scene, to take its effects.
    pub fn scene_mut(&mut self) -> &mut Scene {
        &mut self.scene
    }

    /// The session ended or detached this window.
    #[must_use]
    pub fn session_gone(&self) -> bool {
        self.session_gone
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use ac_proto::ModelData;
    use ac_runtime::world::{ObjectKey, WorldSnapshot};
    use std::sync::mpsc;

    fn view(local_id: u32, guid: u32) -> ObjectView {
        ObjectView {
            key: ObjectKey {
                local_id,
                incarnation: 1,
            },
            guid,
            name: None,
            setup_id: None,
            position: None,
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
            radar: ac_runtime::world::RadarFacts::default(),
            motion: None,
            effects: ac_runtime::world::EffectFacts::default(),
            step_blend: None,
        }
    }

    /// Each snapshot that brings a character into the world counts one
    /// entry: a log in after character selection (the same character or
    /// another), or a switch straight to another character; a snapshot of
    /// the same character again does not.
    #[test]
    fn snapshots_count_the_entries_into_the_world() {
        let mut scene = Scene::default();
        let world = |player_guid| {
            PresentationEvent::Snapshot(WorldSnapshot {
                player_guid,
                objects: Vec::new(),
            })
        };
        scene.apply(world(None));
        assert_eq!(scene.world_entries(), 0, "character selection");
        scene.apply(world(Some(0x5000_000A)));
        assert_eq!(scene.world_entries(), 1);
        scene.apply(world(Some(0x5000_000A)));
        assert_eq!(scene.world_entries(), 1, "the same entry");
        scene.apply(world(None));
        scene.apply(world(Some(0x5000_000A)));
        assert_eq!(scene.world_entries(), 2, "logged out and in again");
        scene.apply(world(Some(0x5000_000B)));
        assert_eq!(scene.world_entries(), 3, "another character");
    }

    #[test]
    fn snapshot_then_deltas_build_the_scene() {
        let (tx, rx) = mpsc::channel();
        let mut source = SceneSource::new(rx);
        tx.send(PresentationEvent::Snapshot(WorldSnapshot {
            player_guid: Some(10),
            objects: vec![view(1, 10), view(2, 20)],
        }))
        .unwrap();
        tx.send(PresentationEvent::Deltas(vec![
            ObjectDelta::Remove(view(2, 20).key),
            ObjectDelta::Upsert(Box::new(view(3, 30))),
        ]))
        .unwrap();
        source.pump();
        assert!(source.scene().is_ready());
        assert_eq!(source.scene().len(), 2);
        assert_eq!(source.scene().player().map(|p| p.guid), Some(10));
        drop(tx);
        source.pump();
        assert!(source.session_gone());
    }

    #[test]
    fn chat_lines_are_kept_once_and_the_player_view_replaced() {
        let line = |sequence: u64| ac_runtime::communication::ChatLine {
            sequence,
            kind: ac_runtime::communication::ChatKind::LocalSpeech,
            log_text_type: 2,
            combat_kind: None,
            received_unix_ms: 0,
            text: format!("{sequence}"),
            channel_name: String::new(),
            sender: String::new(),
            sender_guid: 0,
            words: String::new(),
            display_text: String::new(),
            channel_id: 0,
            spans: None,
        };
        let mut scene = Scene::default();
        // The attach transcript, then a pass that repeats its last line.
        scene.apply(PresentationEvent::Chat(vec![line(1), line(2)]));
        scene.apply(PresentationEvent::Chat(vec![line(2), line(3)]));
        let kept: Vec<u64> = scene.take_chat().iter().map(|l| l.sequence).collect();
        assert_eq!(kept, [1, 2, 3]);
        assert!(scene.take_chat().is_empty());
        assert_eq!(scene.player_view(), None);
        let view = PlayerView {
            selected: Some(9),
            ..PlayerView::default()
        };
        scene.apply(PresentationEvent::Player(view));
        assert_eq!(scene.player_view(), Some(&view));
    }

    /// The window draws the local player at each frame's own time, on the
    /// session clock, between the last two physics steps it was sent, with
    /// no new message from the session in between; other objects stay where
    /// they were sent. Golden values from OpenAC `PlayerMovementController
    /// .ComputeRenderPosition` (`Vector3.Lerp(prev, curr, clamp(pending /
    /// lastQuantum, 0, 1))`): a 1/30 s step ending 5 s into the session,
    /// frames 1/120 s and 1/60 s after it, a quarter and a half of a step.
    #[test]
    fn the_player_is_drawn_at_each_frames_own_time_between_its_steps() {
        use ac_proto::{Position, Quaternion, Vector3};
        use ac_runtime::world::StepBlend;
        use std::time::Duration;
        let v = |x: f32, y: f32, z: f32| Vector3 { x, y, z };
        let place = |x: f32, y: f32, z: f32| Position {
            cell_id: 0xA9B4_0031,
            origin: v(x, y, z),
            orientation: Quaternion::IDENTITY,
        };
        let mut player = view(1, 10);
        player.position = Some(place(10.4, 20.3, 0.1));
        player.step_blend = Some(StepBlend {
            previous: v(10.0, 20.0, 0.0),
            current: v(10.4, 20.3, 0.1),
            step_at: Duration::from_secs(5),
            step_seconds: 1.0 / 30.0,
        });
        let mut door = view(2, 20);
        door.position = Some(place(30.0, 40.0, 0.0));
        let mut scene = Scene::default();
        scene.apply(PresentationEvent::Snapshot(WorldSnapshot {
            player_guid: Some(10),
            objects: vec![player, door],
        }));
        let origin = Instant::now();
        scene.apply(PresentationEvent::SessionClock(origin));
        let drawn = |scene: &Scene| scene.player().and_then(|p| p.position).unwrap();
        let near = |a: Vector3, b: Vector3| {
            (a.x - b.x).abs() < 1e-5 && (a.y - b.y).abs() < 1e-5 && (a.z - b.z).abs() < 1e-5
        };
        let step_end = origin + Duration::from_secs(5);

        scene.present_at(step_end + Duration::from_nanos(8_333_333));
        let quarter = drawn(&scene);
        assert!(near(quarter.origin, v(10.1, 20.075, 0.025)), "{quarter:?}");
        assert_eq!(quarter.cell_id, 0xA9B4_0031, "in the body's cell");

        scene.present_at(step_end + Duration::from_nanos(16_666_667));
        let half = drawn(&scene);
        assert!(near(half.origin, v(10.2, 20.15, 0.05)), "{half:?}");

        scene.present_at(step_end + Duration::from_millis(100));
        assert_eq!(drawn(&scene).origin, v(10.4, 20.3, 0.1), "held on the step");
        assert_eq!(
            scene.object_by_guid(20).and_then(|o| o.position),
            Some(place(30.0, 40.0, 0.0)),
            "an object the physics does not step stays put"
        );
    }

    /// Before the session clock arrives the player is drawn on its step.
    #[test]
    fn without_the_session_clock_the_player_stays_on_its_step() {
        use ac_runtime::world::StepBlend;
        let mut player = view(1, 10);
        let mut at = ac_proto::Position::default();
        at.origin.x = 6.0;
        player.position = Some(at);
        player.step_blend = Some(StepBlend {
            previous: ac_proto::Vector3 {
                x: 5.0,
                ..at.origin
            },
            current: at.origin,
            step_at: std::time::Duration::ZERO,
            step_seconds: 1.0 / 30.0,
        });
        let mut scene = Scene::default();
        scene.apply(PresentationEvent::Snapshot(WorldSnapshot {
            player_guid: Some(10),
            objects: vec![player],
        }));
        scene.present_at(Instant::now());
        assert_eq!(scene.player().and_then(|p| p.position), Some(at));
    }
}

#[cfg(test)]
mod clock_tests {
    use super::*;
    use std::time::Duration;

    #[test]
    fn the_clock_advances_a_tick_per_second_after_a_sync_and_effects_are_taken_once() {
        let mut scene = Scene::default();
        assert_eq!(scene.game_ticks(Instant::now()), None);
        scene.apply(PresentationEvent::GameTime(1000.0));
        let (_, at) = scene.game_time.unwrap();
        let ticks = scene.game_ticks(at + Duration::from_millis(2500)).unwrap();
        assert!((ticks - 1002.5).abs() < 1e-9);
        assert_eq!(scene.synced_ticks(), Some(1000.0));

        scene.apply(PresentationEvent::AdminEnvirons(0x65));
        assert_eq!(scene.take_effects(), [SceneEffect::AdminEnvirons(0x65)]);
        assert!(scene.take_effects().is_empty());
    }

    #[test]
    fn motions_taken_up_queue_in_order_until_taken_and_a_snapshot_drops_them() {
        use ac_runtime::world::{AppliedMotion, MotionView, ObjectKey, WorldSnapshot};
        let motion = |serial| AppliedMotion {
            key: ObjectKey {
                local_id: 1,
                incarnation: 1,
            },
            guid: 0x5000_0001,
            motion: MotionView {
                state: ac_physics::movement::InboundInterpretedState::default(),
                serial,
                grounded: true,
                standing_long_jump: false,
                body: None,
            },
            taken_up: true,
        };
        let mut scene = Scene::default();
        scene.apply(PresentationEvent::Motions(vec![motion(1), motion(2)]));
        scene.apply(PresentationEvent::Motions(vec![motion(3)]));
        let serials: Vec<u32> = scene
            .take_motions()
            .iter()
            .map(|m| m.motion.serial)
            .collect();
        assert_eq!(serials, [1, 2, 3]);
        assert!(scene.take_motions().is_empty());
        scene.apply(PresentationEvent::Motions(vec![motion(4)]));
        scene.apply(PresentationEvent::Snapshot(WorldSnapshot {
            player_guid: None,
            objects: Vec::new(),
        }));
        assert!(scene.take_motions().is_empty(), "the snapshot holds them");
    }
}
