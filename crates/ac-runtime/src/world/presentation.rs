//! What a window needs to draw the world, as owned data: a snapshot when it
//! attaches, then deltas. The window keeps its own copy built from these,
//! which it can drop at any time; the session never depends on it.

use ac_core::items::ClientObject;
use ac_core::player::{LocalPlayerState, VitalKind};
use ac_physics::movement::{InboundInterpretedState, inbound_interpreted_state};
use ac_proto::{
    ModelData, MovementData, PhysicsAttachment, PhysicsDesc, PhysicsMovement, Position, Vector3,
};

use super::{ObjectKey, World, WorldChange, WorldObject};

/// What the radar reads about an object besides its place: from the
/// object's description (the item table); zero or `None` while undescribed.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct RadarFacts {
    /// Its item type bits.
    pub item_type: u32,
    /// Its public description bit field.
    pub public_weenie_bitfield: u32,
    /// Its radar behaviour byte (`None`: the description carried none).
    pub radar_behavior: Option<u8>,
    /// Its own blip colour (0: none).
    pub radar_blip_color: u8,
    /// Its allegiance's monarch (0: none); the radar draws an object that
    /// shares the player's monarch as a square.
    pub monarch_id: u32,
}

impl RadarFacts {
    fn of(item: Option<&ClientObject>) -> Self {
        item.map_or_else(Self::default, |item| Self {
            item_type: item.item_type.bits(),
            public_weenie_bitfield: item.public_weenie_bitfield.map_or(0, |f| f.bits()),
            radar_behavior: item.radar_behavior,
            radar_blip_color: item.radar_blip_color.unwrap_or(0),
            monarch_id: item.monarch_id().unwrap_or(0),
        })
    }
}

/// The effect fields of an object's physics description, each `None` when
/// the server did not send it: the physics script table typed plays pick
/// from, and the default script type and intensity its default-script hook
/// plays.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct EffectFacts {
    /// Physics script table id.
    pub physics_script_table_id: Option<u32>,
    /// Default script type.
    pub default_script_type: Option<u32>,
    /// Default script intensity.
    pub default_script_intensity: Option<f32>,
}

impl EffectFacts {
    fn of(physics: &ac_proto::PhysicsDesc) -> Self {
        Self {
            physics_script_table_id: physics.physics_script_table_id,
            default_script_type: physics.default_script,
            default_script_intensity: physics.default_script_intensity,
        }
    }
}

/// What an animated object is doing, as a window needs it to pose the
/// object: the interpreted motion state (stance, forward, sidestep and turn
/// commands with their speeds, and the actions with their stamps) and
/// whether it stands on walkable ground (in the air it falls).
///
/// For a body the session's physics moves (the local player, other animated
/// objects near it) the axes are its motion interpreter's now, so motions
/// the client makes itself (a move-to's run, the player's keys) are seen;
/// otherwise they are the last interpreted state the server sent. The
/// actions are always the server's, with the server's stamps.
///
/// The game applies every interpreted state the server sends, even one
/// that reads the same as the last: a forward command that is an action
/// (a melee swing) plays again each time. `serial` counts those
/// applications, so a window adopts a repeated state once per message, not
/// once per change.
#[derive(Debug, Clone, PartialEq)]
pub struct MotionView {
    /// The interpreted motion state.
    pub state: InboundInterpretedState,
    /// How many server interpreted states the object has taken up: the
    /// ones the session applied to its body, for a body the session's
    /// physics moves (`body` is `Some`), else the ones accepted. It grows
    /// by one for each, whether or not the state changed.
    pub serial: u32,
    /// On walkable ground.
    pub grounded: bool,
    /// A standing long jump is charging: the jump began standing still, so
    /// walk, run and sidestep only change the state while the body keeps
    /// its ready pose until it jumps.
    pub standing_long_jump: bool,
    /// The body's motion, for a body the session's physics moves (`None`
    /// for one only the server moves).
    pub body: Option<BodyFacts>,
}

/// One motion state an object took up, as presentation replays it: the
/// game applies every server motion state in the order it arrives, and a
/// one-shot action (a melee swing) is played by that application, even
/// when a later state in the same pass replaces it before a window looks.
/// The session sends these in order with the deltas; the window takes each
/// up before it follows the object's latest view.
#[derive(Debug, Clone, PartialEq)]
pub struct AppliedMotion {
    /// The object.
    pub key: ObjectKey,
    /// Its server id.
    pub guid: u32,
    /// Its motion right after it took the state up (with that state's
    /// serial).
    pub motion: MotionView,
    /// A server state taken up (`true`), or the state the object's own
    /// movement passed through just before one (`false`: its move-to
    /// interrupted, a turn stopped), which a window follows like a view.
    pub taken_up: bool,
}

/// The motion log's time stamp: the wall clock in seconds with
/// milliseconds (`t=<seconds>.<ms>`, the seconds counted modulo a million),
/// the same on the session's and the window's lines.
#[must_use]
pub fn motion_log_stamp() -> String {
    let ms = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map_or(0, |d| d.as_millis())
        % 1_000_000_000;
    format!("t={}.{:03}", ms / 1000, ms % 1000)
}

/// One line describing an interpreted state, for the motion log: stance,
/// forward, sidestep and turn commands with their speeds, and the actions
/// with their stamps (`*` marks an autonomous one).
#[must_use]
pub fn motion_line(state: &InboundInterpretedState) -> String {
    use std::fmt::Write;
    let mut line = format!(
        "style=0x{:08X} fwd=0x{:08X}@{:.2} side=0x{:08X}@{:.2} turn=0x{:08X}@{:.2}",
        state.current_style,
        state.forward_command,
        state.forward_speed,
        state.side_step_command,
        state.side_step_speed,
        state.turn_command,
        state.turn_speed,
    );
    if let Some(actions) = &state.actions {
        line.push_str(" actions=[");
        for (i, a) in actions.iter().enumerate() {
            if i > 0 {
                line.push(' ');
            }
            let _ = write!(
                line,
                "0x{:08X}#{}{}@{:.2}",
                a.command,
                a.stamp,
                if a.autonomous { "*" } else { "" },
                a.speed
            );
        }
        line.push(']');
    }
    line
}

/// What a window needs to draw a body the session's physics moves in fixed
/// steps (the local player: 30 steps a second) at its own frame's time:
/// the places of the last two steps and when the last one ended. The
/// window blends between them by the time since then, as the physics does
/// for its own smoothed place, so the character and the camera move on
/// every frame however the frames and the session's passes fall.
///
/// Both places are origins in the cell of the object's position, so a step
/// across a landblock edge blends straight; a jump of the body (a teleport
/// or a forced place) leaves both the same, so nothing blends across it.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct StepBlend {
    /// Where the step before the last one left the body.
    pub previous: Vector3,
    /// Where the last step left the body.
    pub current: Vector3,
    /// When the last step ended, on the session clock.
    pub step_at: std::time::Duration,
    /// How long that step was, seconds.
    pub step_seconds: f32,
}

impl StepBlend {
    /// The place to draw at `now` (session clock): from `previous` toward
    /// `current` by the share of a step elapsed since the last one ended,
    /// held at `current` once a whole step has passed.
    #[must_use]
    pub fn origin_at(&self, now: std::time::Duration) -> Vector3 {
        let elapsed = now.saturating_sub(self.step_at).as_secs_f64();
        let alpha = if self.step_seconds > 0.0 {
            ((elapsed / f64::from(self.step_seconds)) as f32).clamp(0.0, 1.0)
        } else {
            1.0
        };
        let (a, b) = (self.previous, self.current);
        Vector3 {
            x: a.x + (b.x - a.x) * alpha,
            y: a.y + (b.y - a.y) * alpha,
            z: a.z + (b.z - a.z) * alpha,
        }
    }

    /// Whether `other` draws differently: a place moved by a millimetre;
    /// or, while either blends between two places, the step's end moved by
    /// a microsecond (below that it is rounding in the summed frame times,
    /// not a new step) or the step's length changed. A body standing still
    /// draws the same whenever its steps end.
    #[must_use]
    pub fn differs(&self, other: &Self) -> bool {
        let far = |a: Vector3, b: Vector3| {
            (a.x - b.x).powi(2) + (a.y - b.y).powi(2) + (a.z - b.z).powi(2) > 1e-6
        };
        if far(self.previous, other.previous) || far(self.current, other.current) {
            return true;
        }
        let blends = far(self.previous, self.current) || far(other.previous, other.current);
        blends
            && (self.step_at.abs_diff(other.step_at) > std::time::Duration::from_micros(1)
                || self.step_seconds.to_bits() != other.step_seconds.to_bits())
    }
}

/// How a simulated body moves, as the chase camera reads it (it heads
/// along the slope the body moves on).
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct BodyFacts {
    /// The velocity the last step moved at, metres per second.
    pub velocity: [f32; 3],
    /// Touching a surface.
    pub in_contact: bool,
    /// The contact plane's normal.
    pub contact_normal: [f32; 3],
}

/// The interpreted state a server motion record describes, when it is one
/// (movement type 0); move-to and turn-to records carry none.
#[must_use]
pub fn accepted_motion_state(data: &MovementData) -> Option<InboundInterpretedState> {
    if data.movement_type != 0 {
        return None;
    }
    Some(inbound_interpreted_state(
        &crate::movement::wire_motion_state(data),
        0x4000_0000,
    ))
}

/// Placement id of the resting pose.
const PLACEMENT_RESTING: u32 = 0x65;
/// Placement id of the default pose.
const PLACEMENT_DEFAULT: u32 = 0;

/// The placement an object rests in when no motion cycle poses it, the same
/// for drawing and collision. An object described with motion data keeps
/// the resting placement; otherwise it takes the placement the server named
/// (an item hung on a house hook is sent its hook placement), and the
/// default one when it named none.
#[must_use]
pub fn rest_placement(physics: &PhysicsDesc) -> u32 {
    match &physics.movement {
        Some(PhysicsMovement::Present { raw, .. }) if !raw.is_empty() => PLACEMENT_RESTING,
        _ => physics.animation_frame.unwrap_or(PLACEMENT_DEFAULT),
    }
}

/// One object as presentation sees it.
#[derive(Debug, Clone, PartialEq)]
pub struct ObjectView {
    /// Presentation identity.
    pub key: ObjectKey,
    /// Server object id.
    pub guid: u32,
    /// Name, if described.
    pub name: Option<String>,
    /// Model setup (dat id), if any.
    pub setup_id: Option<u32>,
    /// Where it is (for the local player, the body on its last physics
    /// step; a window draws it at [`step_blend`](Self::step_blend) at its
    /// frame's time); `None` while held or in a container, or while it is
    /// not in the world (its entry found no clear spot, so it is not
    /// drawn).
    pub position: Option<Position>,
    /// For a body the session's physics moves in fixed steps (the local
    /// player), what to blend to draw it between them, its places origins
    /// in the cell of [`position`](Self::position).
    pub step_blend: Option<StepBlend>,
    /// The parent holding it, if any.
    pub parent: Option<PhysicsAttachment>,
    /// Placement frame index, if any.
    pub placement: Option<u32>,
    /// The placement it rests in when no motion cycle poses it
    /// ([`rest_placement`]).
    pub rest_placement: u32,
    /// Size multiplier.
    pub scale: Option<f32>,
    /// Translucency, 0 opaque to 1 invisible.
    pub translucency: Option<f32>,
    /// Physics state bits.
    pub state: u32,
    /// Velocity, meters per second.
    pub velocity: Option<Vector3>,
    /// Appearance changes.
    pub model: ModelData,
    /// Motion table (dat id), if animated.
    pub motion_table_id: Option<u32>,
    /// Sound table (dat id) its sounds are chosen from, if any.
    pub sound_table_id: Option<u32>,
    /// What the radar reads about it.
    pub radar: RadarFacts,
    /// What it is doing, when it has motion to show (see [`MotionView`]).
    pub motion: Option<MotionView>,
    /// Its physics-script table and default script, for its effects.
    pub effects: EffectFacts,
}

impl ObjectView {
    pub(super) fn of(object: &WorldObject, world: &World) -> Self {
        let physics = object.physics();
        let name = world.name(object.guid());
        Self {
            key: object.key(),
            guid: object.guid(),
            name: name.map(str::to_owned),
            setup_id: physics.setup_id,
            position: if object.entry_refused() {
                None
            } else {
                object.position().copied()
            },
            step_blend: if object.entry_refused() {
                None
            } else {
                object.step_blend().copied()
            },
            parent: physics.parent,
            placement: physics.animation_frame,
            rest_placement: rest_placement(physics),
            scale: physics.scale,
            translucency: physics.translucency,
            state: physics.state,
            velocity: physics.velocity,
            model: object.model().clone(),
            motion_table_id: physics.motion_table_id,
            sound_table_id: physics.sound_table_id,
            radar: RadarFacts::of(world.item(object.guid())),
            motion: object.motion_view(),
            effects: EffectFacts::of(physics),
        }
    }
}

/// Everything presentation needs at attach time.
#[derive(Debug, Clone, PartialEq)]
pub struct WorldSnapshot {
    /// The local player's object id.
    pub player_guid: Option<u32>,
    /// Every known object.
    pub objects: Vec<ObjectView>,
}

/// A change since the snapshot.
#[derive(Debug, Clone, PartialEq)]
pub enum ObjectDelta {
    /// The object appeared or changed; this is its current state.
    Upsert(Box<ObjectView>),
    /// The object is gone.
    Remove(ObjectKey),
}

impl World {
    /// A snapshot of everything presentation needs.
    #[must_use]
    pub fn snapshot(&self) -> WorldSnapshot {
        WorldSnapshot {
            player_guid: self.player_guid(),
            objects: self.objects().map(|o| ObjectView::of(o, self)).collect(),
        }
    }

    /// Turn recorded changes into deltas: one per object, its latest state.
    /// An item change shows as its object's latest state when the object is
    /// in the world (its name may have changed); changes about the player's
    /// own state, contents lists, or items without a physics record carry no
    /// drawable object and are skipped.
    #[must_use]
    pub fn deltas(&self, changes: &[WorldChange]) -> Vec<ObjectDelta> {
        let mut seen = std::collections::HashSet::new();
        let mut out = Vec::new();
        for change in changes.iter().rev() {
            let (key, removed) = match *change {
                WorldChange::Removed(key) => (key, true),
                WorldChange::Spawned(key)
                | WorldChange::Respawned(key)
                | WorldChange::Refreshed(key)
                | WorldChange::Moved { key, .. }
                | WorldChange::MotionChanged(key)
                | WorldChange::StateChanged(key)
                | WorldChange::VectorChanged(key)
                | WorldChange::AppearanceChanged(key)
                | WorldChange::Parented(key)
                | WorldChange::PickedUp(key) => (key, false),
                WorldChange::Item(change) => {
                    match change.guid().and_then(|guid| self.object(guid)) {
                        Some(object) => (object.key(), false),
                        None => continue,
                    }
                }
                WorldChange::PlayerChanged => continue,
            };
            if !seen.insert(key.local_id) {
                continue;
            }
            let current = self.object_by_local_id(key.local_id);
            out.push(match (removed, current) {
                (false, Some(object)) => {
                    ObjectDelta::Upsert(Box::new(ObjectView::of(object, self)))
                }
                _ => ObjectDelta::Remove(key),
            });
        }
        out.reverse();
        out
    }

    /// The player's view: his vitals and the selection `selected`; the
    /// option fields are left off for the caller.
    #[must_use]
    pub fn player_view(&self, selected: Option<u32>) -> PlayerView {
        let view = PlayerView {
            selected,
            ..PlayerView::default()
        };
        match self.player() {
            Some(player) => view.with_vitals(player.numbers()),
            None => view,
        }
    }
}

/// One vital as the vitals window reads it.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct VitalReading {
    /// Current value.
    pub current: Option<u32>,
    /// Maximum, enchantments and vitae included.
    pub max: Option<u32>,
    /// Current share of the maximum, 0..=1.
    pub percent: Option<f32>,
}

/// What a window shows of the local player besides his object: the vitals,
/// the selection, and the options the always-shown panels read.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct PlayerView {
    /// Health.
    pub health: VitalReading,
    /// Stamina.
    pub stamina: VitalReading,
    /// Mana.
    pub mana: VitalReading,
    /// The selected object.
    pub selected: Option<u32>,
    /// The "show coordinates on the radar" option.
    pub coordinates_on_radar: bool,
    /// The "lock UI" option.
    pub ui_locked: bool,
    /// The "stay in chat mode" option: the chat entry keeps the focus
    /// after a line is sent.
    pub stay_in_chat_mode: bool,
    /// The "disable most weather effects" option: the window draws no
    /// weather (sky weather objects and their effects).
    pub disable_most_weather_effects: bool,
    /// The character's first options word (the options window's character
    /// page reads every option from the two words).
    pub options1: u32,
    /// The second options word.
    pub options2: u32,
    /// The words are the character's (a description seeded them), not the
    /// client defaults.
    pub options_seeded: bool,
    /// A jump being charged: its power, 0..=1 (the jump power bar).
    pub jump_charge: Option<f32>,
    /// The creature the chase camera keeps in view (the ViewCombatTarget
    /// option in melee or missile combat).
    pub combat_camera_target: Option<u32>,
    /// The combat bar: the attack height, the power setting and its level.
    pub attack: AttackBarView,
}

/// What the combat bar shows of the attack request.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct AttackBarView {
    /// The height the next swing goes at.
    pub height: ac_core::combat::AttackHeight,
    /// The power bar's setting, 0..=1.
    pub desired_power: f32,
    /// The bar's level now (it fills while an attack key is held), 0..=1.
    pub power_bar: f32,
}

impl PlayerView {
    /// Character option `option_id` as the two words hold it (false for an
    /// unknown option).
    #[must_use]
    pub fn option(&self, option_id: u32) -> bool {
        crate::character::option(option_id).is_some_and(|entry| {
            let word = if entry.in_options1 {
                self.options1
            } else {
                self.options2
            };
            word & entry.mask != 0
        })
    }

    /// The "persistent daylight" option (`/day`): the landscape is lit as
    /// at noon.
    #[must_use]
    pub fn persistent_daylight(&self) -> bool {
        self.option(crate::character::option_id::PERSISTENT_AT_DAY)
    }

    /// The "disable distance fog" option: the world is drawn unfogged.
    #[must_use]
    pub fn disable_distance_fog(&self) -> bool {
        self.option(crate::character::option_id::DISABLE_DISTANCE_FOG)
    }

    /// This view with the vitals of the player's numbers.
    #[must_use]
    pub fn with_vitals(mut self, numbers: &LocalPlayerState) -> Self {
        let reading = |kind: VitalKind, percent: Option<f32>| VitalReading {
            current: numbers.get(kind).map(|v| v.current),
            max: numbers.get_max_approx(kind),
            percent,
        };
        self.health = reading(VitalKind::Health, numbers.health_percent());
        self.stamina = reading(VitalKind::Stamina, numbers.stamina_percent());
        self.mana = reading(VitalKind::Mana, numbers.mana_percent());
        self
    }
}

#[cfg(test)]
mod tests {
    use super::PlayerView;
    use crate::character::option_id;

    #[test]
    fn the_view_reads_an_option_from_its_word() {
        // PersistentAtDay is bit 0x1 of the second word; AutoRepeatAttack
        // bit 0x2 of the first (OpenAC `CharacterOptionTable`).
        let view = PlayerView {
            options1: 0x2,
            options2: 0x1,
            ..PlayerView::default()
        };
        assert!(view.persistent_daylight());
        assert!(view.option(option_id::AUTO_REPEAT_ATTACK));
        assert!(!PlayerView::default().persistent_daylight());
        assert!(!view.option(0xFF));
    }

    #[test]
    fn the_view_reads_the_distance_fog_option() {
        // DisableDistanceFog is bit 0x0020_0000 of the second word (OpenAC
        // `CharacterOptionTableTests`, InlineData DisableDistanceFog).
        let view = PlayerView {
            options2: 0x0020_0000,
            ..PlayerView::default()
        };
        assert!(view.disable_distance_fog());
        assert!(!PlayerView::default().disable_distance_fog());
    }
}
