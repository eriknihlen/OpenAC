//! What the pre-game screens show: the connection, character selection,
//! character creation and its choices.

use std::collections::HashMap;
use std::sync::Arc;

use ac_clock::Instant;
use ac_core::chargen::{
    ChargenAnimPartChange, ChargenAttributeValues, ChargenEyeStrip, ChargenFaceStrip,
    ChargenGearOption, ChargenGenderOptions, ChargenHairStyle, ChargenHeritageOptions,
    ChargenObjDesc, ChargenOptions, ChargenPosition, ChargenSkillAdvancementClass,
    ChargenSkillCost, ChargenSkillDetail, ChargenStarterArea, ChargenSubPalette, ChargenTemplate,
    ChargenTextureChange,
};
use ac_core::player::SkillFormula;
use ac_proto::CharacterErrorCode;
use ac_runtime::session::Generation;
use ac_runtime::session::creation::{
    CreatedIdentity, CreationAppearance, CreationRejection, CreationSnapshot, LocalRefusal,
};
use ac_runtime::session::pregame::{ConnectionStatus, ConnectionView, PreGameEvent};
use ac_runtime::session::selection::{
    SelectionButtons, SelectionEntry, SelectionError, SelectionLifecycle, SelectionOperation,
    SelectionSnapshot,
};

use crate::codec::{record, tuple_record, union};

tuple_record! {
    Generation(number: u64)
}

record! {
    ConnectionView {
        status: ConnectionStatus,
        connection_progress: f32,
        update_progress: f32,
        error: Option<String>,
        started: Instant,
        direct_launch: bool,
    }
    SelectionEntry { active_index: usize, character_id: u32, name: String, seconds_greyed_out: u32 }
    SelectionError { raw_code: u32, code: Option<CharacterErrorCode>, message: String }
    SelectionButtons {
        can_enter: bool,
        can_delete: bool,
        can_restore: bool,
        delete_visible: bool,
        restore_visible: bool,
        can_create: bool,
    }
    SelectionSnapshot {
        generation: Generation,
        lifecycle: SelectionLifecycle,
        revision: u64,
        account_name: String,
        slot_count: i32,
        entries: Vec<SelectionEntry>,
        world_name: String,
        server_population: Option<u32>,
        highlighted_character_id: u32,
        highlighted_display_index: Option<usize>,
        pending_delete_character_id: u32,
        last_restore_requested_character_id: u32,
        operation: SelectionOperation,
        error: Option<SelectionError>,
        buttons: SelectionButtons,
    }
    ChargenAttributeValues {
        strength: i32,
        endurance: i32,
        coordination: i32,
        quickness: i32,
        focus: i32,
        self_: i32,
    }
    LocalRefusal {
        no_name: bool,
        attribute_credits_unspent: bool,
        already_pending: bool,
        roster_full: bool,
        heritage_or_gender_unset: bool,
    }
    CreationRejection { raw_code: u32, reason: String, attempted_name: String }
    CreatedIdentity { guid: u32, name: String }
    CreationSnapshot {
        generation: Generation,
        is_active: bool,
        revision: u64,
        heritage_id: u32,
        gender_key: u32,
        appearance: CreationAppearance,
        template: u32,
        attributes: ChargenAttributeValues,
        attribute_lock_mask: u32,
        total_attribute_credits: u32,
        remaining_attribute_credits: i32,
        total_skill_credits: u32,
        remaining_skill_credits: i32,
        skills: Vec<ChargenSkillAdvancementClass>,
        name: String,
        start_area: i32,
        slot: u32,
        verification_pending: bool,
        last_local_refusal: LocalRefusal,
        last_rejection: Option<CreationRejection>,
        last_created: Option<CreatedIdentity>,
    }
    ChargenSubPalette { sub_palette_id: u32, offset: u8, num_colors: u8 }
    ChargenTextureChange { part_index: u8, old_texture_id: u32, new_texture_id: u32 }
    ChargenAnimPartChange { part_index: u8, part_id: u32 }
    ChargenObjDesc {
        palette_id: u32,
        sub_palettes: Vec<ChargenSubPalette>,
        texture_changes: Vec<ChargenTextureChange>,
        anim_part_changes: Vec<ChargenAnimPartChange>,
    }
    ChargenHairStyle { icon_id: u32, bald: bool, alternate_setup: u32, obj_desc: ChargenObjDesc }
    ChargenEyeStrip {
        icon_id: u32,
        bald_icon_id: u32,
        obj_desc: ChargenObjDesc,
        bald_obj_desc: ChargenObjDesc,
    }
    ChargenFaceStrip { icon_id: u32, obj_desc: ChargenObjDesc }
    ChargenGearOption { name: String, clothing_table_id: u32, weenie_default_id: u32 }
    ChargenGenderOptions {
        gender_key: i32,
        name: String,
        scale: u32,
        setup_id: u32,
        sound_table_id: u32,
        icon_id: u32,
        base_palette_id: u32,
        skin_pal_set_id: u32,
        physics_table_id: u32,
        motion_table_id: u32,
        combat_table_id: u32,
        base_obj_desc: ChargenObjDesc,
        hair_colors: Vec<u32>,
        hair_styles: Vec<ChargenHairStyle>,
        eye_colors: Vec<u32>,
        eye_strips: Vec<ChargenEyeStrip>,
        nose_strips: Vec<ChargenFaceStrip>,
        mouth_strips: Vec<ChargenFaceStrip>,
        headgears: Vec<ChargenGearOption>,
        shirts: Vec<ChargenGearOption>,
        pants: Vec<ChargenGearOption>,
        footwear: Vec<ChargenGearOption>,
        clothing_colors: Vec<u32>,
    }
    ChargenSkillCost { skill_id: u32, normal_cost: i32, primary_cost: i32 }
    SkillFormula {
        additive_bonus: i32,
        attribute1_multiplier: i32,
        attribute2_multiplier: i32,
        divisor: i32,
        attribute1: u32,
        attribute2: u32,
    }
    ChargenSkillDetail {
        skill_id: u32,
        name: String,
        min_level: u32,
        description: String,
        formula: SkillFormula,
    }
    ChargenTemplate {
        name: String,
        icon_id: u32,
        title_string_id: u32,
        attributes: ChargenAttributeValues,
        normal_skills: Vec<u32>,
        primary_skills: Vec<u32>,
    }
    ChargenPosition { cell_id: u32, origin: [f32; 3], rotation_wxyz: [f32; 4] }
    ChargenStarterArea { index: i32, name: String, locations: Vec<ChargenPosition> }
    ChargenHeritageOptions {
        heritage_id: u32,
        name: String,
        icon_id: u32,
        setup_id: u32,
        environment_setup_id: u32,
        attribute_credits: u32,
        skill_credits: u32,
        primary_start_area_indices: Vec<i32>,
        secondary_start_area_indices: Vec<i32>,
        skill_costs_by_skill_id: HashMap<u32, ChargenSkillCost>,
        templates: Vec<ChargenTemplate>,
        genders_by_key: HashMap<i32, ChargenGenderOptions>,
    }
    ChargenOptions {
        starter_areas: Vec<ChargenStarterArea>,
        heritages_by_id: HashMap<u32, ChargenHeritageOptions>,
        global_skill_costs_by_skill_id: HashMap<u32, ChargenSkillCost>,
        global_skill_details_by_skill_id: HashMap<u32, ChargenSkillDetail>,
    }
}

union! {
    ConnectionStatus: "connection status" {
        0 => Inactive,
        1 => Connecting,
        2 => CheckingData,
        3 => Ready,
        4 => Unsupported,
        5 => Failed,
    }
    SelectionLifecycle: "selection lifecycle" {
        0 => Inactive,
        1 => Connecting,
        2 => AwaitingSelection,
        3 => EnteringWorld,
        4 => InWorld,
    }
    SelectionOperation: "selection operation" {
        0 => None,
        1 => DeleteRequested,
        2 => DeleteAcknowledged,
        3 => RestoreRequested,
        4 => RestoreSucceeded,
        5 => RestoreRejected,
    }
    CharacterErrorCode: "character error code" {
        0x00 => Undefined,
        0x01 => Logon,
        0x02 => LoggedOn,
        0x03 => AccountLogon,
        0x04 => ServerCrash,
        0x05 => Logoff,
        0x06 => Delete,
        0x07 => NoPremade,
        0x08 => AccountInUse,
        0x09 => AccountInvalid,
        0x0A => AccountDoesntExist,
        0x0B => EnterGameGeneric,
        0x0C => EnterGameStressAccount,
        0x0D => EnterGameCharacterInWorld,
        0x0E => EnterGamePlayerAccountMissing,
        0x0F => EnterGameCharacterNotOwned,
        0x10 => EnterGameCharacterInWorldServer,
        0x11 => EnterGameOldCharacter,
        0x12 => EnterGameCorruptCharacter,
        0x13 => EnterGameStartServerDown,
        0x14 => EnterGameCouldntPlaceCharacter,
        0x15 => LogonServerFull,
        0x16 => CharacterIsBooted,
        0x17 => EnterGameCharacterLocked,
        0x18 => SubscriptionExpired,
        0x19 => NumErrors,
    }
    ChargenSkillAdvancementClass: "skill advancement class" {
        0 => Inactive,
        1 => Untrained,
        2 => Trained,
        3 => Specialized,
    }
    PreGameEvent: "pre-game event" {
        0 => Connection(view: ConnectionView),
        1 => Selection(snapshot: Box<SelectionSnapshot>),
        2 => Creation(snapshot: Box<CreationSnapshot>),
        3 => Options(options: Arc<ChargenOptions>),
        4 => Refused(reason: String),
    }
}
