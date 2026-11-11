//! The world as a window draws it: objects, their places, looks and
//! motions, the player's vitals, effects, chat lines, portal space, the
//! link, navigation and the interface requests of client commands.

use ac_core::chat::{ChatKind, ChatSpanRole, ChatTextSpan, ChatTextTag, CombatLineKind};
use ac_core::combat::AttackHeight;
use ac_nav::view::{NavLine, NavLineKind};
use ac_physics::movement::{InboundInterpretedState, InboundMotionAction};
use ac_proto::effects::{PlayScript, PlayScriptType, SoundEvent};
use ac_proto::{
    AnimPartChange, ModelData, PhysicsAttachment, Position, Quaternion, SubPalette, TextureChange,
    Vector3,
};
use ac_runtime::communication::{ChatLine, WindowRequest};
use ac_runtime::navigation::NavigationView;
use ac_runtime::session::LinkStatus;
use ac_runtime::world::transit::{DestinationReadiness, DestinationShown, PortalSpaceEvent};
use ac_runtime::world::{
    AppliedMotion, AttackBarView, BodyFacts, EffectFacts, MotionView, ObjectDelta, ObjectKey,
    ObjectView, PlayerView, RadarFacts, StepBlend, VitalReading, WorldSnapshot,
};
use glam::Vec3;

use crate::codec::{record, tuple_record, union};

tuple_record! {
    AttackHeight(value: u32)
}

record! {
    Vector3 { x: f32, y: f32, z: f32 }
    Quaternion { w: f32, x: f32, y: f32, z: f32 }
    Vec3 { x: f32, y: f32, z: f32 }
    Position { cell_id: u32, origin: Vector3, orientation: Quaternion }
    PhysicsAttachment { guid: u32, location_id: u32 }
    SubPalette { sub_palette_id: u32, offset: u8, length: u8 }
    TextureChange { part_index: u8, old_texture: u32, new_texture: u32 }
    AnimPartChange { part_index: u8, new_model_id: u32 }
    ModelData {
        base_palette_id: Option<u32>,
        sub_palettes: Vec<SubPalette>,
        texture_changes: Vec<TextureChange>,
        anim_part_changes: Vec<AnimPartChange>,
    }
    ObjectKey { local_id: u32, incarnation: u16 }
    StepBlend {
        previous: Vector3,
        current: Vector3,
        step_at: std::time::Duration,
        step_seconds: f32,
    }
    RadarFacts {
        item_type: u32,
        public_weenie_bitfield: u32,
        radar_behavior: Option<u8>,
        radar_blip_color: u8,
        monarch_id: u32,
    }
    EffectFacts {
        physics_script_table_id: Option<u32>,
        default_script_type: Option<u32>,
        default_script_intensity: Option<f32>,
    }
    InboundMotionAction { command: u32, stamp: i32, autonomous: bool, speed: f32 }
    InboundInterpretedState {
        current_style: u32,
        forward_command: u32,
        forward_speed: f32,
        side_step_command: u32,
        side_step_speed: f32,
        turn_command: u32,
        turn_speed: f32,
        actions: Option<Vec<InboundMotionAction>>,
    }
    BodyFacts { velocity: [f32; 3], in_contact: bool, contact_normal: [f32; 3] }
    MotionView {
        state: InboundInterpretedState,
        serial: u32,
        grounded: bool,
        standing_long_jump: bool,
        body: Option<BodyFacts>,
    }
    AppliedMotion { key: ObjectKey, guid: u32, motion: MotionView, taken_up: bool }
    ObjectView {
        key: ObjectKey,
        guid: u32,
        name: Option<String>,
        setup_id: Option<u32>,
        position: Option<Position>,
        step_blend: Option<StepBlend>,
        parent: Option<PhysicsAttachment>,
        placement: Option<u32>,
        rest_placement: u32,
        scale: Option<f32>,
        translucency: Option<f32>,
        state: u32,
        velocity: Option<Vector3>,
        model: ModelData,
        motion_table_id: Option<u32>,
        sound_table_id: Option<u32>,
        radar: RadarFacts,
        motion: Option<MotionView>,
        effects: EffectFacts,
    }
    WorldSnapshot { player_guid: Option<u32>, objects: Vec<ObjectView> }
    SoundEvent { guid: u32, sound_type: u32, volume: f32 }
    PlayScript { guid: u32, script_id: u32 }
    PlayScriptType { guid: u32, script_type: u32, intensity: f32 }
    VitalReading { current: Option<u32>, max: Option<u32>, percent: Option<f32> }
    AttackBarView { height: AttackHeight, desired_power: f32, power_bar: f32 }
    PlayerView {
        health: VitalReading,
        stamina: VitalReading,
        mana: VitalReading,
        selected: Option<u32>,
        coordinates_on_radar: bool,
        ui_locked: bool,
        stay_in_chat_mode: bool,
        disable_most_weather_effects: bool,
        options1: u32,
        options2: u32,
        options_seeded: bool,
        jump_charge: Option<f32>,
        combat_camera_target: Option<u32>,
        attack: AttackBarView,
    }
    ChatTextTag { tag_type: String, format: String, data: String }
    ChatTextSpan { text: String, tag: Option<ChatTextTag>, role: ChatSpanRole }
    ChatLine {
        sequence: u64,
        kind: ChatKind,
        log_text_type: u32,
        combat_kind: Option<CombatLineKind>,
        received_unix_ms: i64,
        text: String,
        channel_name: String,
        sender: String,
        sender_guid: u32,
        words: String,
        display_text: String,
        channel_id: u32,
        spans: Option<Vec<ChatTextSpan>>,
    }
    LinkStatus {
        connected: bool,
        seconds_since_last_packet: f64,
        packet_loss_percent: f64,
        round_trip_seconds: Option<f64>,
    }
    NavLine { from: Vec3, to: Vec3, kind: NavLineKind, follows_ground: bool }
    NavigationView { frame_center: u32, lines: Vec<NavLine> }
    DestinationReadiness {
        generation: u64,
        destination_cell: u32,
        is_indoor: bool,
        is_unhydratable: bool,
        required_render_radius: i32,
        is_render_neighborhood_ready: bool,
        are_composite_textures_ready: bool,
        is_collision_ready: bool,
    }
    DestinationShown { teleport_sequence: u16, readiness: DestinationReadiness }
}

union! {
    ObjectDelta: "object delta" {
        0 => Upsert(view: Box<ObjectView>),
        1 => Remove(key: ObjectKey),
    }
    ChatSpanRole: "chat span role" {
        0 => Body,
        1 => Timestamp,
    }
    CombatLineKind: "combat line kind" {
        0 => Info,
        1 => Warning,
        2 => Error,
    }
    ChatKind: "chat kind" {
        0 => LocalSpeech,
        1 => RangedSpeech,
        2 => Channel,
        3 => Tell,
        4 => System,
        5 => Popup,
        6 => Emote,
        7 => SoulEmote,
        8 => Combat,
    }
    NavLineKind: "navigation line kind" {
        0 => Clear,
        1 => Tight,
        2 => Path,
        3 => Route,
        4 => Beyond,
        5 => Portal,
    }
    PortalSpaceEvent: "portal space event" {
        0 => Entered { teleport_sequence: u16 },
        1 => Destination { generation: u64, teleport_sequence: u16, destination_cell: u32 },
        2 => Arrived { teleport_sequence: u16, destination_cell: u32 },
        3 => LoginEntering,
        4 => LoginDestination { generation: u64, destination_cell: u32 },
        5 => LoginWithdrawn,
        6 => LogoutStarted,
    }
    WindowRequest: "window request" {
        0 => ToggleFrameRate,
        1 => SaveUi(name: String),
        2 => LoadUi(name: String),
        3 => SaveAutoUi,
        4 => LoadAutoUi,
        5 => SetLandscapeRadius(radius: i32),
        6 => SetFieldOfView(degrees: i32),
        7 => SetClipboard(text: String),
        8 => Minimize,
        9 => Restore,
        10 => GameplayOptionsPage,
    }
}
