//! Round trips of every event and command, decoder robustness, the
//! subscription and the shape pin.

use std::collections::{BTreeMap, HashMap, HashSet};
use std::sync::Arc;
use std::time::{Duration, Instant};

use ac_core::chargen::{
    ChargenAttributeValues, ChargenGenderOptions, ChargenHeritageOptions, ChargenObjDesc,
    ChargenOptions, ChargenPosition, ChargenSkillAdvancementClass, ChargenSkillCost,
    ChargenSkillDetail, ChargenStarterArea, ChargenSubPalette, ChargenTemplate,
};
use ac_core::chat::{
    ChatKind, ChatSpanRole, ChatTextSpan, ChatTextTag, CombatLineKind, LogTextType,
};
use ac_core::chess::{ChessBoardParts, ChessPiece, ChessPieceType, ChessSquare, ChessTeam};
use ac_core::combat::{AttackHeight, CombatMode};
use ac_core::items::{
    ClientArmorProfile, ClientCreatureAttributes, ClientCreatureProfile, ClientObjectHeader,
    ClientObjectParts, ClientWeaponProfile, EquipMask, HouseRestrictionRecord, ItemManaChange,
    ItemType, ItemUseable, ObjectIdentity, PropertyBundle, PublicWeenieFlags, ShortcutEntry,
    ShortcutStoreParts, VendorShopItem, VendorShopProfile,
};
use ac_core::journal::JournalPage;
use ac_core::player::{AttributeSnapshot, LocalPlayerStateParts, SkillSnapshot, VitalSnapshot};
use ac_core::quests::{ContractCatalog, ContractEntry};
use ac_core::social::{
    FriendEntry, FriendsStateParts, SquelchDatabase, SquelchInfo, SquelchStateParts,
};
use ac_core::spells::{ActiveEnchantmentRecord, SpellbookParts};
use ac_nav::motor::{MoveDirection, MovePace, MoveRequest, MoveUnit};
use ac_nav::view::{NavLine, NavLineKind};
use ac_physics::movement::{InboundInterpretedState, InboundMotionAction};
use ac_plugin_ui::markup::{
    Anchors, BoolSource, ColorSource, Common, Element, FlexContainer, IconKind, IconSource,
    ListBody, ListHeader, Node, PanelSpec, Rect, TextSource, UintSource,
};
use ac_plugin_ui::{
    ActionValue, CanvasAnchor, CanvasDescriptor, CanvasPlace, CanvasView, ClientWindow,
    ClientWindowAction, Color8, DialogView, GroundShape, GroundShapeKind, ImageSource, ImageView,
    ItemRecord, MapPoint, MarkerAnchor, MarkupDocument, PaintCmd, PaintRect, PanelDescriptor,
    PanelView, PluginIcon, PointerEvent, PointerKind, ProjectileSample, Rgba, ThemeKind, UiChange,
    UiInput, UiOwner, UiValue, WorldIcon, WorldIconEntry, WorldLabel, WorldLine, WorldTriangle,
};
use ac_proto::allegiance::{AllegianceMember, AllegianceProfile};
use ac_proto::appraise::{ArmorProfile, IdentifyObjectResponse};
use ac_proto::book::BookPage;
use ac_proto::contract::ContractTracker;
use ac_proto::effects::{PlayScript, PlayScriptType, SoundEvent};
use ac_proto::house::{HousePayment, HouseProfile};
use ac_proto::{
    AnimPartChange, CharacterErrorCode, ModelData, PhysicsAttachment, Position, Quaternion,
    SubPalette, TextureChange, Vector3,
};
use ac_runtime::character::{CharacterCommand, CharacterOptionsParts};
use ac_runtime::communication::{
    ChatChannelKind, ChatEntryCommand, ChatLine, ClientConfirmation, ItemUseConfirmation,
    JournalCommand, JournalView, TrackedContract, WindowRequest,
};
use ac_runtime::gameplay::selection_cycle::{SelectionCycleDirection, SelectionCycleKind};
use ac_runtime::gameplay::{
    AllegianceCommand, BarberCommand, BarberView, BookCommand, BookView, ChessCommand, ChessStage,
    ChessView, CombatAttackCommand, FellowView, FellowshipCommand, FellowshipView, GameplayCommand,
    HouseLineColor, HouseView, InputActivation, SelectionSource,
};
use ac_runtime::inventory::{
    AppraisalView, HousePurchaseCommand, HousePurchaseTab, HousePurchaseView, InteractionMode,
    ItemCommand, ObjectRef, PendingPlacement, StagedRow, TradeView, VendorCommand, VendorListsView,
    VendorView, VendorViewReason,
};
use ac_runtime::movement::{MovementCommand, MovementKeys, Posture};
use ac_runtime::navigation::NavigationView;
use ac_runtime::session::creation::{
    AppearanceSlot, ChargenAttribute, CreatedIdentity, CreationAppearance, CreationCommand,
    CreationRejection, CreationSnapshot, LocalRefusal, ShadeSlot,
};
use ac_runtime::session::interface::{
    ChatEntryView, ContractFacts, InterfaceEvent, InterfaceState, ItemsUpdate, PlayerFacts,
    SelectedHealth, SelectedMana, SocialFacts, TitleFacts,
};
use ac_runtime::session::pregame::{ConnectionStatus, ConnectionView, PreGameEvent};
use ac_runtime::session::selection::{
    SelectionButtons, SelectionEntry, SelectionError, SelectionLifecycle, SelectionOperation,
    SelectionSnapshot,
};
use ac_runtime::session::{
    CharacterSelector, Generation, LifecycleCommand, LinkStatus, PresentationEvent, Recall,
    SessionCommand,
};
use ac_runtime::world::transit::{DestinationReadiness, DestinationShown, PortalSpaceEvent};
use ac_runtime::world::{
    AppliedMotion, AttackBarView, BodyFacts, EffectFacts, MotionView, ObjectDelta, ObjectKey,
    ObjectView, PlayerView, RadarFacts, StepBlend, VitalReading, WorldSnapshot,
};

use crate::{
    DecodeContext, DecodeError, EncodeContext, EncodeError, Objects, STREAM_FORMAT, Subscription,
    decode_batch, decode_command, encode_batch, encode_command, shape, shape_hex,
};

/// A sender and a receiver whose epochs stand for the same moment.
fn contexts() -> (EncodeContext, DecodeContext) {
    let epoch = Instant::now();
    // The samples' spellbooks have an empty table installed: so does the
    // receiver's copy, by name.
    let table = Some(Arc::new(ac_core::spells::SpellTable::empty()));
    (
        EncodeContext::new(epoch),
        DecodeContext::new(epoch, table, None),
    )
}

fn v3(x: f32) -> Vector3 {
    Vector3 {
        x,
        y: x + 0.25,
        z: -x,
    }
}

fn motion(serial: u32) -> MotionView {
    MotionView {
        state: InboundInterpretedState {
            current_style: 0x8000_003D,
            forward_command: 0x4500_0005,
            forward_speed: 1.5,
            side_step_command: 0x6500_000F,
            side_step_speed: -0.5,
            turn_command: 0x6500_000D,
            turn_speed: 0.75,
            actions: Some(vec![InboundMotionAction {
                command: 0x1000_0051,
                stamp: -3,
                autonomous: true,
                speed: 2.0,
            }]),
        },
        serial,
        grounded: true,
        standing_long_jump: true,
        body: Some(BodyFacts {
            velocity: [1.0, 2.0, 3.0],
            in_contact: true,
            contact_normal: [0.0, 0.0, 1.0],
        }),
    }
}

pub(crate) fn object_view(n: u32) -> ObjectView {
    ObjectView {
        key: ObjectKey {
            local_id: n,
            incarnation: 7,
        },
        guid: 0x8000_0000 + n,
        name: Some(format!("Drudge {n}")),
        setup_id: Some(0x0200_0001),
        position: Some(Position {
            cell_id: 0xA9B4_0021,
            origin: v3(12.5),
            orientation: Quaternion {
                w: 0.5,
                x: 0.5,
                y: -0.5,
                z: 0.5,
            },
        }),
        step_blend: Some(StepBlend {
            previous: v3(1.0),
            current: v3(2.0),
            step_at: Duration::new(3, 123_456_789),
            step_seconds: 1.0 / 30.0,
        }),
        parent: Some(PhysicsAttachment {
            guid: 0x5000_0001,
            location_id: 2,
        }),
        placement: Some(0x65),
        rest_placement: 0x65,
        scale: Some(1.25),
        translucency: Some(0.5),
        state: 0x0040_8C08,
        velocity: Some(v3(-4.0)),
        model: ModelData {
            base_palette_id: Some(0x0400_007E),
            sub_palettes: vec![SubPalette {
                sub_palette_id: 0x0400_1000,
                offset: 3,
                length: 9,
            }],
            texture_changes: vec![TextureChange {
                part_index: 1,
                old_texture: 0x0500_0001,
                new_texture: 0x0500_0002,
            }],
            anim_part_changes: vec![AnimPartChange {
                part_index: 4,
                new_model_id: 0x0100_0042,
            }],
        },
        motion_table_id: Some(0x0900_0001),
        sound_table_id: Some(0x2000_0001),
        radar: RadarFacts {
            item_type: 0x10,
            public_weenie_bitfield: 0x14,
            radar_behavior: Some(3),
            radar_blip_color: 5,
            monarch_id: 0x5000_0009,
        },
        motion: Some(motion(n)),
        effects: EffectFacts {
            physics_script_table_id: Some(0x3400_0001),
            default_script_type: Some(0x17),
            default_script_intensity: Some(0.8),
        },
    }
}

fn chat_line() -> ChatLine {
    ChatLine {
        sequence: 42,
        kind: ChatKind::Channel,
        log_text_type: 8,
        combat_kind: Some(CombatLineKind::Warning),
        received_unix_ms: 1_790_000_000_123,
        text: "[General] Bob says, \"hi\"".into(),
        channel_name: "General".into(),
        sender: "Bob".into(),
        sender_guid: 0x5000_0002,
        words: "hi".into(),
        display_text: "Bob: hi".into(),
        channel_id: 0x0B,
        spans: Some(vec![
            ChatTextSpan {
                text: "12:00 ".into(),
                tag: None,
                role: ChatSpanRole::Timestamp,
            },
            ChatTextSpan {
                text: "Bob".into(),
                tag: Some(ChatTextTag {
                    tag_type: "Tell".into(),
                    format: "f".into(),
                    data: "0x50000002".into(),
                }),
                role: ChatSpanRole::Body,
            },
        ]),
    }
}

fn player_view() -> PlayerView {
    let vital = |c| VitalReading {
        current: Some(c),
        max: Some(c * 2),
        percent: Some(0.5),
    };
    PlayerView {
        health: vital(10),
        stamina: vital(20),
        mana: vital(30),
        selected: Some(0x8000_0001),
        coordinates_on_radar: true,
        ui_locked: true,
        stay_in_chat_mode: true,
        disable_most_weather_effects: true,
        options1: 0x50C4_A54A,
        options2: 0x0094_8700,
        options_seeded: true,
        jump_charge: Some(0.3),
        combat_camera_target: Some(0x8000_0002),
        attack: AttackBarView {
            height: AttackHeight::LOW,
            desired_power: 0.6,
            power_bar: 0.4,
        },
    }
}

fn client_object(guid: u32) -> ac_core::items::ClientObject {
    let mut properties = PropertyBundle::default();
    properties.ints.insert(1, -5);
    properties.int64s.insert(2, i64::MIN);
    properties.bools.insert(3, true);
    properties.floats.insert(4, 0.125);
    properties.strings.insert(5, "Inscribed".into());
    properties.data_ids.insert(6, 0x0600_1234);
    properties.instance_ids.insert(7, 0x5000_0001);
    ClientObjectParts {
        object_id: guid,
        identity: ObjectIdentity(99),
        weenie_class_id: 0x1234,
        name: "Pyreal".into(),
        plural_name: "Pyreals".into(),
        item_type: ItemType(0x40),
        valid_locations: EquipMask(0x0010_0000),
        currently_equipped_location: EquipMask(0x0010_0000),
        icon_id: 0x0600_0001,
        icon_underlay_id: 0x0600_0002,
        icon_overlay_id: 0x0600_0003,
        effects: 1,
        stack_size: 25,
        stack_size_max: 25_000,
        burden: 25,
        value: 25,
        container_id: 0x5000_0001,
        container_slot: 3,
        container_type_hint: 2,
        attuned: true,
        bonded: true,
        wielder_id: 0x5000_0001,
        items_capacity: 24,
        containers_capacity: 7,
        hook_item_types: 0x2,
        hook_type: 4,
        priority: 8,
        useability: Some(ItemUseable(0x0008_0001)),
        target_type: Some(ItemType(0x10)),
        public_weenie_bitfield: Some(PublicWeenieFlags(0x14)),
        pet_owner_id: 0x5000_0003,
        combat_use: Some(1),
        ammo_type: Some(2),
        spell_id: Some(1234),
        appraised_spell_ids: vec![1, 2, 3],
        last_appraisal_time_ms: 77,
        appraisal_answered: true,
        last_appraisal_unsuccessful: true,
        last_appraisal_unsuccessful_at_seconds: 12.5,
        weapon_profile: Some(ClientWeaponProfile {
            damage_type: 1,
            weapon_time: 2,
            weapon_skill: 3,
            damage: u32::MAX,
            damage_variance: 0.5,
            damage_mod: 1.5,
            weapon_length: 2.5,
            max_velocity: 3.5,
            weapon_offense: 4.5,
            max_velocity_estimated: 5,
        }),
        armor_profile: Some(ClientArmorProfile {
            slashing_protection: 1.0,
            piercing_protection: 0.9,
            bludgeoning_protection: 0.8,
            cold_protection: 0.7,
            fire_protection: 0.6,
            acid_protection: 0.5,
            nether_protection: 0.4,
            lightning_protection: 0.3,
        }),
        creature_profile: Some(ClientCreatureProfile {
            flags: 8,
            health: 50,
            max_health: 60,
            attributes: Some(ClientCreatureAttributes {
                strength: 1,
                endurance: 2,
                quickness: 3,
                coordination: 4,
                focus: 5,
                self_: 6,
                stamina: 7,
                max_stamina: 8,
                mana: 9,
                max_mana: 10,
            }),
            attribute_highlights: 0x3,
            attribute_colors: 0x1,
        }),
        cooldown_id: Some(9),
        cooldown_duration: Some(30.0),
        trade_state: 1,
        sell_state: 2,
        is_component_pack: true,
        radar_blip_color: Some(4),
        radar_behavior: Some(2),
        structure: 5,
        max_structure: 10,
        workmanship: 7.5,
        material_type: Some(0x13),
        house_owner_id: Some(0x5000_0004),
        monarch_id: Some(0x5000_0005),
        restrictions: Some(HouseRestrictionRecord {
            open_to_public: true,
            allegiance_monarch_id: 0x5000_0005,
            guests: BTreeMap::from([(0x5000_0006, 1)]),
        }),
        properties,
        header: Some(ClientObjectHeader {
            weenie_header_flags: Some(1),
            weenie_header_flags2: Some(2),
            physics_description_flags: Some(3),
            physics_state: Some(4),
            object_description_flags: Some(5),
            setup_id: Some(6),
            scale: Some(1.5),
            hook_type: Some(7),
            parent_id: Some(8),
            parent_location: Some(9),
            use_radius: Some(2.5),
        }),
    }
    .into()
}

fn player_facts() -> PlayerFacts {
    let book = SpellbookParts {
        learned: BTreeMap::from([(1, 0.5), (2, 1.0)]),
        active: vec![ActiveEnchantmentRecord {
            spell_id: 1,
            layer_id: 2,
            duration: 3.0,
            caster_guid: 4,
            stat_mod_type: Some(5),
            stat_mod_key: Some(6),
            stat_mod_value: Some(7.5),
            bucket: 8,
            start_time: 9.0,
            spell_category: 10,
            power_level: 11,
            degrade_modifier: 12.0,
            degrade_limit: 13.0,
            last_time_degraded: 14.0,
            spell_set_id: Some(15),
        }],
        order_by_bucket: BTreeMap::from([(8, vec![1, 2])]),
        favorites: [
            vec![1],
            vec![],
            vec![2, 3],
            vec![],
            vec![],
            vec![],
            vec![],
            vec![4],
        ],
        desired_components: BTreeMap::from([(0x10, 5)]),
        table: Arc::default(),
        metadata_installed: true,
        filters: 0x3FFF,
        spellbook_revision: 3,
        enchantment_revision: 4,
        desired_components_revision: 5,
    };
    let vital = |n| {
        Some(VitalSnapshot {
            ranks: n,
            start: n + 1,
            xp: n + 2,
            current: n + 3,
        })
    };
    let attribute = |n| {
        Some(AttributeSnapshot {
            ranks: n,
            start: n * 10,
            xp: n * 100,
        })
    };
    PlayerFacts {
        revision: 17,
        numbers: LocalPlayerStateParts {
            health: vital(1),
            stamina: vital(2),
            mana: None,
            attributes: [
                attribute(1),
                attribute(2),
                None,
                attribute(4),
                attribute(5),
                attribute(6),
            ],
            skills: BTreeMap::from([(
                6,
                SkillSnapshot {
                    skill_id: 6,
                    ranks: 10,
                    status: 2,
                    xp: 1000,
                    init: 10,
                    resistance: 3,
                    last_used: 1.5,
                    formula_bonus: 50,
                },
            )]),
            int_properties: BTreeMap::from([(25, 126)]),
            spellbook: Some(book.into()),
            skill_formula_bonus_resolver: None,
        }
        .into(),
        session_seconds: 123.25,
    }
}

fn interface_state() -> InterfaceState {
    let mut entries = [None; 18];
    entries[2] = Some(ShortcutEntry {
        index: 2,
        object_id: 0x8000_0001,
        spell_id: 0,
    });
    InterfaceState {
        player: 0x5000_0001,
        mode: InteractionMode::UseItemOnTarget(0x8000_0002),
        pending_item: Some(0x8000_0003),
        pending_placement: Some(PendingPlacement {
            token: 9,
            item_id: 0x8000_0003,
            container_id: 0x5000_0001,
            placement: -1,
        }),
        can_make_request: true,
        combat_mode: CombatMode::MELEE,
        busy_count: 2,
        shortcuts: ShortcutStoreParts {
            entries,
            revision: 4,
        }
        .into(),
        confirmations: vec![
            ClientConfirmation::Suicide,
            ClientConfirmation::ItemUse {
                kind: ItemUseConfirmation::VolatileRare,
                object_id: 7,
            },
            ClientConfirmation::Server {
                confirmation_type: 5,
                context_id: 6,
                message: "Join?".into(),
            },
        ],
        selected_health: Some(SelectedHealth {
            object_id: 1,
            percent: Some(0.25),
        }),
        selected_mana: Some(SelectedMana {
            object_id: 2,
            fraction: 0.75,
        }),
        external_container: 0x8000_0010,
        external_container_in_range: true,
        account: "someone".into(),
    }
}

fn social_facts() -> SocialFacts {
    let member = |n: u32| AllegianceMember {
        character_id: n,
        parent_guid: n + 1,
        is_logged_in: true,
        name: format!("Member {n}"),
        rank: 3,
        level: 50,
        loyalty: 10,
        leadership: 20,
        cp_cached: 30,
        cp_tithed: 40,
        gender: 1,
        heritage_group: 2,
        may_passup_experience: true,
    };
    SocialFacts {
        in_world: true,
        options: CharacterOptionsParts {
            options1: 1,
            options2: 2,
            revision: 3,
            dirty: true,
            first_dirtied_at: Some(Duration::from_millis(1500)),
            has_server_seed: true,
            echo_shortcuts: vec![ac_proto::Shortcut {
                index: 1,
                object_id: 2,
                spell_id: 3,
            }],
            echo_favorite_spells: Default::default(),
            echo_desired_components: vec![ac_proto::DesiredComponent { id: 4, amount: 5 }],
            echo_spellbook_filters: 0x3FFF,
        }
        .into(),
        friends: FriendsStateParts {
            entries: vec![FriendEntry {
                id: 1,
                name: "Pal".into(),
                online: true,
                appear_offline: true,
                friends: vec![2],
                friend_of: vec![3],
            }],
            revision: 6,
        }
        .into(),
        squelch: SquelchStateParts {
            database: SquelchDatabase {
                accounts: HashMap::from([("bad".to_owned(), 9)]),
                characters: HashMap::from([(
                    10,
                    SquelchInfo {
                        name: "Spammer".into(),
                        account_wide: true,
                        message_types: HashSet::from([1, 5, 9]),
                    },
                )]),
                global: SquelchInfo::default(),
            },
            revision: 7,
        }
        .into(),
        fellowship: FellowshipView {
            revision: 8,
            in_fellowship: true,
            name: "Fellows".into(),
            leader_guid: 1,
            share_xp: true,
            even_xp_split: true,
            open: true,
            locked: true,
            members: vec![FellowView {
                guid: 1,
                name: "Lead".into(),
                level: 100,
                health: (1, 2),
                stamina: (3, 4),
                mana: (5, 6),
                share_loot: true,
                vitals_age_seconds: Some(1.5),
            }],
        },
        allegiance: Some(AllegianceProfile {
            total_members: 1,
            total_vassals: 2,
            record_count: 3,
            old_version: 4,
            motd: "Hello".into(),
            motd_set_by: "Monarch".into(),
            chat_room_id: 5,
            allegiance_name: "House".into(),
            name_last_set_time: 6,
            is_locked: true,
            approved_vassal: 7,
            monarch: Some(member(1)),
            records: vec![member(2), member(3)],
        }),
        allegiance_rank: 9,
        allegiance_revision: 10,
    }
}

fn contract_entry(id: u32) -> ContractEntry {
    ContractEntry {
        version: 1,
        contract_id: id,
        contract_name: format!("Contract {id}"),
        description: "Kill things".into(),
        description_progress: "%d/20 Tuskers".into(),
        name_npc_start: "Start".into(),
        name_npc_end: "End".into(),
        questflag_stamped: "a".into(),
        questflag_started: "b".into(),
        questflag_finished: "c".into(),
        questflag_progress: "d".into(),
        questflag_timer: "e".into(),
        questflag_repeat_time: "f".into(),
        location_npc_start_cell: 1,
        location_npc_end_cell: 2,
        location_quest_area_cell: 3,
    }
}

fn chess_view() -> ChessView {
    let mut squares = [None; 64];
    squares[0] = Some(ChessPiece {
        piece_type: ChessPieceType::Rook,
        team: ChessTeam::White,
        has_moved: true,
    });
    squares[63] = Some(ChessPiece {
        piece_type: ChessPieceType::King,
        team: ChessTeam::Black,
        has_moved: false,
    });
    ChessView {
        revision: 1,
        stage: ChessStage::OpponentsTurn,
        board_guid: 0x8000_0020,
        team: Some(ChessTeam::Black),
        offering_stalemate: true,
        games_ended: 2,
        board: ChessBoardParts {
            squares,
            en_passant_target: Some(ChessSquare { x: 3, y: 2 }),
            en_passant_victim: ChessSquare { x: 3, y: 3 },
            turn: ChessTeam::Black,
        }
        .into(),
    }
}

fn appearance() -> CreationAppearance {
    CreationAppearance {
        eyes_strip: 1,
        nose_strip: 2,
        mouth_strip: 3,
        hair_style: 4,
        hair_color: 5,
        eye_color: 6,
        headgear_style: 7,
        headgear_color: 8,
        shirt_style: 9,
        shirt_color: 10,
        trousers_style: 11,
        trousers_color: 12,
        footwear_style: 13,
        footwear_color: 14,
        skin_shade: 0.1,
        hair_shade: 0.2,
        headgear_shade: 0.3,
        shirt_shade: 0.4,
        trousers_shade: 0.5,
        footwear_shade: 0.6,
    }
}

fn owner() -> UiOwner {
    UiOwner::new("example", "Example")
}

fn panel_view() -> PanelView {
    let icon = PluginIcon {
        width: 2,
        height: 1,
        rgba: Arc::new(vec![1, 2, 3, 4, 5, 6, 7, 8]),
    };
    let document = MarkupDocument {
        panel: PanelSpec {
            rect: Rect {
                x: 1.0,
                y: 2.0,
                w: 300.0,
                h: 200.0,
            },
            resizable: true,
            title: Some("Tank".into()),
            layout: Some(FlexContainer {
                direction: ac_plugin_ui::flex::FlexDirection::Column,
                gap: 4.0,
                padding: ac_plugin_ui::flex::FlexEdges {
                    top: 1.0,
                    right: 2.0,
                    bottom: 3.0,
                    left: 4.0,
                },
                justify: ac_plugin_ui::flex::FlexJustify::SpaceBetween,
                align: ac_plugin_ui::flex::FlexAlign::Center,
                wrap: true,
            }),
            authored_size: (Some(300.0), None),
            geometry_revision: 3,
            ..PanelSpec::default()
        },
        nodes: vec![
            Node {
                element: Element::Button {
                    text: TextSource::Bound("Caption".into()),
                    onclick: Some("OnGo".into()),
                    color: Some(ColorSource::Paired {
                        dark: Rgba::new(1.0, 0.0, 0.0, 1.0),
                        light: Rgba::new(0.0, 0.0, 1.0, 0.5),
                    }),
                    background: None,
                    border: Some(ColorSource::Theme {
                        token: "border".into(),
                        classic: Rgba::new(0.5, 0.5, 0.5, 1.0),
                    }),
                    icon: Some(IconSource::Id {
                        kind: IconKind::Spell,
                        id: UintSource::Literal(42),
                    }),
                },
                common: Common {
                    name: Some("go".into()),
                    anchors: Anchors::TOP_LEFT,
                    visible: BoolSource::Bound("ShowGo".into()),
                    enabled: BoolSource::Literal(false),
                    tooltip: Some(TextSource::Literal("Go!".into())),
                    default_color: true,
                    ..Common::default()
                },
                rect: Rect {
                    x: 5.0,
                    y: 6.0,
                    w: 7.0,
                    h: 8.0,
                },
                parent: None,
                children: vec![1],
                flex: None,
                repeat: None,
            },
            Node {
                element: Element::List {
                    selected: "Row".into(),
                    onchange: None,
                    row_height: 16.0,
                    selection_band: true,
                    multi: false,
                    body: ListBody::Single {
                        items: "Rows".into(),
                        colors: Some("Colors".into()),
                        icons: Some(("Icons".into(), IconKind::Item)),
                    },
                    header: ListHeader {
                        show: true,
                        sort: Some(("Col".into(), "Desc".into())),
                        ..ListHeader::default()
                    },
                    tree: None,
                },
                common: Common::default(),
                rect: Rect::default(),
                parent: Some(0),
                children: vec![],
                flex: None,
                repeat: Some(0),
            },
        ],
        roots: vec![0],
        warnings: vec!["a warning".into()],
    };
    PanelView {
        id: 3,
        owner: owner(),
        descriptor: PanelDescriptor::new("main", "Example"),
        document: Arc::new(document),
        values: BTreeMap::from([
            ("Caption".to_owned(), UiValue::Text("Go".into())),
            ("ShowGo".to_owned(), UiValue::Bool(true)),
            ("Opt".to_owned(), UiValue::OptionalBool(None)),
            (
                "Items".to_owned(),
                UiValue::Items(Arc::new(vec![ItemRecord {
                    values: BTreeMap::from([(
                        "inner".to_owned(),
                        UiValue::FloatList(vec![1.5, -2.0]),
                    )]),
                }])),
            ),
        ]),
        label_overrides: BTreeMap::from([(0, "Override".to_owned())]),
        visible_overrides: BTreeMap::from([(1, false)]),
        requested_visible: true,
        file_icon: Some(icon.clone()),
        window_icon: Some(ac_plugin_ui::WindowIcon {
            named: None,
            light: Some(icon.clone()),
            dark: Some(icon),
        }),
    }
}

fn canvas_view() -> CanvasView {
    CanvasView {
        id: 4,
        owner: owner(),
        descriptor: CanvasDescriptor {
            canvas_id: "map".into(),
            width: 100,
            height: 50,
            anchor: CanvasAnchor::BottomRight,
            offset: (-10.0, -20.0),
            start_visible: true,
            accepts_pointer_input: true,
            in_markup: true,
        },
        size: (120, 60),
        hosted: true,
        visible: true,
        anchor: CanvasAnchor::Center,
        offset: (1.0, 2.0),
        listening: true,
        paint: Arc::new(paint()),
        release_count: 3,
    }
}

fn paint() -> Vec<PaintCmd> {
    let c = Color8::new(1, 2, 3, 4);
    let rect = PaintRect {
        x: 1.0,
        y: 2.0,
        w: 3.0,
        h: 4.0,
    };
    vec![
        PaintCmd::Clear(c),
        PaintCmd::FillRect { rect, color: c },
        PaintCmd::StrokeRect {
            rect,
            color: c,
            thickness: 2.0,
        },
        PaintCmd::Line {
            from: (0.0, 1.0),
            to: (2.0, 3.0),
            color: c,
            thickness: 1.5,
        },
        PaintCmd::Text {
            text: "hp".into(),
            at: (4.0, 5.0),
            color: c,
            outline: true,
            font: 2,
        },
        PaintCmd::Image {
            image: 7,
            rect,
            tint: Color8::WHITE,
        },
        PaintCmd::ImageTransformed {
            image: 8,
            rect,
            tint: c,
            rotation: 0.5,
            pivot: (1.0, 1.0),
            scale_x: 2.0,
            scale_y: 3.0,
        },
        PaintCmd::PushClip(rect),
        PaintCmd::PopClip,
    ]
}

fn map_point(n: f64) -> MapPoint {
    MapPoint {
        east_west: n,
        north_south: -n,
        elevation: n / 2.0,
    }
}

fn plugin_ui_changes() -> Vec<UiChange> {
    vec![
        UiChange::Clear,
        UiChange::PanelAdded(Box::new(panel_view())),
        UiChange::PanelRemoved(9),
        UiChange::PanelValues {
            panel: 3,
            values: vec![
                ("A".into(), UiValue::IntList(vec![1, -2])),
                ("B".into(), UiValue::UintList(vec![3])),
                ("C".into(), UiValue::BoolList(vec![true, false])),
                ("D".into(), UiValue::TextList(vec!["x".into()])),
                ("E".into(), UiValue::Float(0.5)),
                ("F".into(), UiValue::Uint(7)),
                ("G".into(), UiValue::Int(-7)),
            ],
        },
        UiChange::PanelRequestedVisible {
            panel: 3,
            visible: true,
        },
        UiChange::ControlLabel {
            panel: 3,
            node: 1,
            label: "New".into(),
        },
        UiChange::ControlVisible {
            panel: 3,
            node: 0,
            visible: false,
        },
        UiChange::CanvasAdded(Box::new(canvas_view())),
        UiChange::CanvasRemoved(4),
        UiChange::CanvasPaint {
            canvas: 4,
            paint: Arc::new(paint()),
        },
        UiChange::CanvasPlacement {
            canvas: 4,
            visible: true,
            anchor: CanvasAnchor::TopCenter,
            offset: (3.0, 4.0),
        },
        UiChange::CanvasListening {
            canvas: 4,
            listening: true,
        },
        UiChange::CanvasReleasePointer(4),
        UiChange::CanvasResized {
            canvas: 4,
            size: (10, 20),
        },
        UiChange::ImageAdded(Box::new(ImageView {
            owner: "example".into(),
            handle: 1,
            width: 1,
            height: 1,
            source: ImageSource::Rgba(Arc::new(vec![9, 8, 7, 6])),
        })),
        UiChange::ImageAdded(Box::new(ImageView {
            owner: "example".into(),
            handle: 2,
            width: 2,
            height: 2,
            source: ImageSource::File(Arc::new(ac_plugin_ui::FileImage {
                path: "icons/a.png".into(),
                version: "v1".into(),
                levels: vec![ac_plugin_ui::DecodedImage {
                    width: 1,
                    height: 1,
                    rgba: vec![1, 2, 3, 4],
                }],
                full_path: Some("plugins/example/icons/a.png".into()),
            })),
        })),
        UiChange::ImageAdded(Box::new(ImageView {
            owner: "example".into(),
            handle: 3,
            width: 32,
            height: 32,
            source: ImageSource::SpellIcon { spell: 1, icon: 2 },
        })),
        UiChange::FileIcon {
            panel: 3,
            path: "icon.png".into(),
            handle: 5,
        },
        UiChange::ImageRemoved {
            owner: "example".into(),
            handle: 1,
        },
        UiChange::Labels(Arc::new(vec![WorldLabel {
            object_id: 1,
            text: "Target".into(),
            color: [1.0, 0.5, 0.25, 1.0],
            height_offset: 0.5,
            line: 1,
            max_range: 50.0,
            outline: true,
        }])),
        UiChange::Lines(Arc::new(vec![WorldLine {
            start: map_point(1.0),
            end: map_point(2.0),
            color_rgb: 0xFF00FF,
            follow_terrain: true,
            width_metres: 0.1,
        }])),
        UiChange::Triangles(Arc::new(vec![WorldTriangle {
            corners: [map_point(1.0), map_point(2.0), map_point(3.0)],
            colors_rgba: [1, 2, 3],
        }])),
        UiChange::Icons(Arc::new(vec![WorldIconEntry {
            owner: "example".into(),
            icon: WorldIcon {
                anchor: MarkerAnchor::Position {
                    point: map_point(4.0),
                    is_outdoor: true,
                },
                image: 2,
                size: 24.0,
                tint_rgba: 0xFFFF_FFFF,
                border_rgba: Some(0xFF),
                max_range: 100.0,
                hidden_by_world: true,
            },
        }])),
        UiChange::Shapes(Arc::new(vec![GroundShape {
            anchor: MarkerAnchor::Object(0x8000_0001),
            kind: GroundShapeKind::Arc,
            radius: 5.0,
            width: 1.0,
            start_degrees: 45.0,
            sweep_degrees: 90.0,
            filled: true,
            faces_object: true,
            color_rgba: 0x00FF_0080,
        }])),
        UiChange::ProjectileDebug(Arc::new(vec![ProjectileSample {
            position: map_point(5.0),
            is_clear: true,
            radius: 0.2,
        }])),
        UiChange::ClientWindow {
            window: ClientWindow::Radar,
            action: ClientWindowAction::Hide,
        },
        UiChange::Hotkeys(Arc::new(vec![ac_plugin_ui::hotkeys::BoundHotkey {
            owner: "example".into(),
            id: "toggle".into(),
            chord: ac_plugin_ui::hotkeys::Chord {
                key: 0x41,
                ctrl: true,
                alt: false,
                shift: true,
            },
        }])),
        UiChange::FontRemoved {
            owner: "example".into(),
            handle: 1,
        },
        UiChange::DialogAdded(Box::new(DialogView {
            handle: 6,
            owner: owner(),
            title: "Sure?".into(),
            message: "Really?".into(),
            buttons: vec!["Yes".into(), "No".into()],
            owner_panel: Some(3),
        })),
        UiChange::DialogRemoved(6),
        UiChange::Modal {
            panel: 3,
            owner: Some(6),
        },
    ]
}

fn interface_events() -> Vec<InterfaceEvent> {
    vec![
        InterfaceEvent::Items(ItemsUpdate {
            reset: true,
            objects: vec![client_object(0x8000_0001), client_object(0x8000_0002)],
            removed: vec![0x8000_0003],
            contents: vec![(0x5000_0001, Some(vec![0x8000_0001])), (0x8000_0004, None)],
        }),
        InterfaceEvent::Player(Box::new(player_facts())),
        InterfaceEvent::State(Box::new(interface_state())),
        InterfaceEvent::Appraisal(Box::new(AppraisalView {
            name: Some("Sword".into()),
            response: IdentifyObjectResponse {
                guid: 1,
                flags: 2,
                success: true,
                spell_book: vec![3, 4],
                armor_profile: Some(ArmorProfile {
                    slashing: 1.0,
                    piercing: 2.0,
                    bludgeoning: 3.0,
                    cold: 4.0,
                    fire: 5.0,
                    acid: 6.0,
                    nether: 7.0,
                    lightning: 8.0,
                }),
                ..IdentifyObjectResponse::default()
            },
            present_in_ui: true,
            opens_window: true,
        })),
        InterfaceEvent::Trade(Box::new(TradeView {
            open: true,
            partner_guid: 2,
            self_items: vec![1],
            item_names: BTreeMap::from([(1, "Gem".to_owned())]),
            partner_name: Some("Bob".into()),
            revision: 3,
            ..TradeView::default()
        })),
        InterfaceEvent::Vendor(Some(Box::new(VendorView {
            vendor_guid: 7,
            name: Some("Shopkeep".into()),
            profile: VendorShopProfile {
                buy_price: 0.9,
                sell_price: 1.1,
                alternate_currency_plural_name: "Tokens".into(),
                ..VendorShopProfile::default()
            },
            items: vec![VendorShopItem {
                item_guid: 1,
                name: Some("Bread".into()),
                workmanship: Some(2.5),
                ..VendorShopItem::default()
            }],
            lists: VendorListsView {
                buying: vec![StagedRow {
                    item_guid: 1,
                    name: "Bread".into(),
                    quantity: 2,
                    price: 3,
                }],
                money: 100,
                ..VendorListsView::default()
            },
            reason: VendorViewReason::ComponentsFilled,
        }))),
        InterfaceEvent::Vendor(None),
        InterfaceEvent::Social(Box::new(social_facts())),
        InterfaceEvent::Contracts(Box::new(ContractFacts {
            contracts: vec![TrackedContract {
                tracker: ContractTracker {
                    version: 1,
                    contract_id: 2,
                    stage: 3,
                    time_when_done: 4.0,
                    time_when_repeats: 5.0,
                },
                received_unix_ms: 6,
            }],
            display_contract_id: 2,
            revision: 7,
            catalog: Arc::new(ContractCatalog::new(HashMap::from([
                (2, contract_entry(2)),
                (9, contract_entry(9)),
            ]))),
        })),
        InterfaceEvent::Book(Box::new(BookView {
            revision: 1,
            book_guid: 2,
            pages: vec![BookPage {
                author_id: 1,
                author_name: "Me".into(),
                author_account: "acct".into(),
                text_included: 1,
                ignore_author: 0,
                page_text: "Once".into(),
            }],
            request_pending: true,
            ..BookView::default()
        })),
        InterfaceEvent::House(Box::new(HouseView {
            revision: 1,
            has_received_notice: true,
            owns_house: true,
            lines: vec![("Paid".into(), HouseLineColor::RentPaid)],
            position_cell: Some(0x1234_0001),
        })),
        InterfaceEvent::HousePurchase(Box::new(HousePurchaseView {
            revision: 2,
            profile: Some(HouseProfile {
                slumlord_guid: 1,
                dwelling_id: 2,
                owner_guid: 3,
                bitmask: 4,
                min_level: 5,
                max_level: 6,
                min_allegiance_rank: 7,
                max_allegiance_rank: 8,
                maintenance_free: true,
                house_type: 9,
                owner_name: "Owner".into(),
                buy: vec![HousePayment {
                    num: 1,
                    paid: 0,
                    weenie_id: 2,
                    name: "Pyreal".into(),
                    plural_name: "Pyreals".into(),
                }],
                rent: vec![],
            }),
            buy: vec![1],
            rent: vec![2],
            buy_amounts: vec![3],
            rent_amounts: vec![4],
        })),
        InterfaceEvent::Journal(Box::new(JournalView {
            pages: vec![JournalPage {
                label: "L".into(),
                title: "T".into(),
                notes: "N".into(),
                timer_days: 1,
                timer_hours: 2,
                timer_minutes: 3,
                location_x: 4.5,
                location_y: 5.5,
                has_location: true,
                running_timer_seconds: 6.0,
            }],
            current_page: 1,
            revision: 2,
            timer_started_unix_ms: Some(3),
            timer_seconds_at_start: 4.0,
        })),
        InterfaceEvent::OpenSalvage(0x8000_0005),
        InterfaceEvent::Chess(Box::new(chess_view())),
        InterfaceEvent::Barber(Box::new(BarberView {
            revision: 1,
            openings: 2,
            open: true,
            heritage: 3,
            gender: 4,
            appearance: appearance(),
            toggle: Some(ac_core::chargen::barber::BarberToggle::ShadowNoCrown),
            toggle_on: true,
            preview_setup: 5,
        })),
        InterfaceEvent::BeginGame(0x8000_0020),
        InterfaceEvent::OpenContainedContainer(0x8000_0021),
        InterfaceEvent::ItemMana(ItemManaChange {
            item_id: 1,
            fraction: 0.5,
            valid: true,
        }),
        InterfaceEvent::PluginUi(plugin_ui_changes()),
        InterfaceEvent::Titles(Box::new(TitleFacts {
            display_title_id: 1,
            title_ids: vec![1, 2],
            revision: 3,
            table_generation: 4,
        })),
        InterfaceEvent::ChatEntry(Box::new(ChatEntryView {
            draft: "hel".into(),
            shown_revision: 2,
            composed: true,
            channel: ChatChannelKind::Trade,
            tell_target: Some("Bob".into()),
            tell_target_guid: 3,
            last_incoming_tell_sender: Some("Ann".into()),
            last_monarch_sender: None,
            last_patron_sender: Some("Pat".into()),
        })),
        InterfaceEvent::Selection(Some(0x8000_0001)),
        InterfaceEvent::Selection(None),
    ]
}

fn chargen_options() -> ChargenOptions {
    let desc = ChargenObjDesc {
        palette_id: 1,
        sub_palettes: vec![ChargenSubPalette {
            sub_palette_id: 2,
            offset: 3,
            num_colors: 4,
        }],
        ..ChargenObjDesc::default()
    };
    ChargenOptions {
        starter_areas: vec![ChargenStarterArea {
            index: 0,
            name: "Holtburg".into(),
            locations: vec![ChargenPosition {
                cell_id: 0xA9B4_0021,
                origin: [1.0, 2.0, 3.0],
                rotation_wxyz: [1.0, 0.0, 0.0, 0.0],
            }],
        }],
        heritages_by_id: HashMap::from([(
            1,
            ChargenHeritageOptions {
                heritage_id: 1,
                name: "Aluvian".into(),
                skill_costs_by_skill_id: HashMap::from([(
                    6,
                    ChargenSkillCost {
                        skill_id: 6,
                        normal_cost: 4,
                        primary_cost: 8,
                    },
                )]),
                templates: vec![ChargenTemplate {
                    name: "Custom".into(),
                    attributes: ChargenAttributeValues {
                        strength: 10,
                        ..ChargenAttributeValues::default()
                    },
                    normal_skills: vec![1],
                    ..ChargenTemplate::default()
                }],
                genders_by_key: HashMap::from([(
                    1,
                    ChargenGenderOptions {
                        gender_key: 1,
                        name: "Male".into(),
                        base_obj_desc: desc.clone(),
                        hair_colors: vec![1, 2],
                        ..ChargenGenderOptions::default()
                    },
                )]),
                ..ChargenHeritageOptions::default()
            },
        )]),
        global_skill_costs_by_skill_id: HashMap::new(),
        global_skill_details_by_skill_id: HashMap::from([(
            6,
            ChargenSkillDetail {
                skill_id: 6,
                name: "Melee Defense".into(),
                min_level: 1,
                description: "Dodge".into(),
                formula: ac_core::player::SkillFormula {
                    additive_bonus: 0,
                    attribute1_multiplier: 1,
                    attribute2_multiplier: 1,
                    divisor: 3,
                    attribute1: 4,
                    attribute2: 2,
                },
            },
        )]),
    }
}

fn pregame_events(epoch: Instant) -> Vec<PreGameEvent> {
    vec![
        PreGameEvent::Connection(ConnectionView {
            status: ConnectionStatus::CheckingData,
            connection_progress: 0.5,
            update_progress: 0.25,
            error: Some("slow".into()),
            started: epoch + Duration::from_micros(1_234_567),
            direct_launch: true,
        }),
        PreGameEvent::Selection(Box::new(SelectionSnapshot {
            generation: Generation(3),
            lifecycle: SelectionLifecycle::AwaitingSelection,
            revision: 4,
            account_name: "acct".into(),
            slot_count: 11,
            entries: vec![SelectionEntry {
                active_index: 0,
                character_id: 0x5000_0001,
                name: "+Hero".into(),
                seconds_greyed_out: 0,
            }],
            world_name: "World".into(),
            server_population: Some(120),
            highlighted_character_id: 0x5000_0001,
            highlighted_display_index: Some(0),
            pending_delete_character_id: 0,
            last_restore_requested_character_id: 2,
            operation: SelectionOperation::RestoreRejected,
            error: Some(SelectionError {
                raw_code: 0x17,
                code: Some(CharacterErrorCode::EnterGameCharacterLocked),
                message: "Locked".into(),
            }),
            buttons: SelectionButtons {
                can_enter: true,
                can_delete: true,
                can_restore: false,
                delete_visible: true,
                restore_visible: false,
                can_create: true,
            },
        })),
        PreGameEvent::Creation(Box::new(CreationSnapshot {
            generation: Generation(5),
            is_active: true,
            revision: 6,
            heritage_id: 1,
            gender_key: 2,
            appearance: appearance(),
            template: 3,
            attributes: ChargenAttributeValues {
                strength: 1,
                endurance: 2,
                coordination: 3,
                quickness: 4,
                focus: 5,
                self_: 6,
            },
            attribute_lock_mask: 0x3,
            total_attribute_credits: 330,
            remaining_attribute_credits: -1,
            total_skill_credits: 50,
            remaining_skill_credits: 2,
            skills: vec![
                ChargenSkillAdvancementClass::Specialized,
                ChargenSkillAdvancementClass::Inactive,
            ],
            name: "Newbie".into(),
            start_area: 2,
            slot: 1,
            verification_pending: true,
            last_local_refusal: LocalRefusal {
                no_name: true,
                roster_full: true,
                ..LocalRefusal::default()
            },
            last_rejection: Some(CreationRejection {
                raw_code: 3,
                reason: "Taken".into(),
                attempted_name: "Hero".into(),
            }),
            last_created: Some(CreatedIdentity {
                guid: 0x5000_0007,
                name: "Hero".into(),
            }),
        })),
        PreGameEvent::Options(Arc::new(chargen_options())),
        PreGameEvent::Refused("not now".into()),
    ]
}

/// One of every event (and of every interface fact and plugin UI change).
fn every_event(epoch: Instant) -> Vec<PresentationEvent> {
    let mut events = vec![
        PresentationEvent::Snapshot(WorldSnapshot {
            player_guid: Some(0x5000_0001),
            objects: vec![object_view(1), object_view(2)],
        }),
        PresentationEvent::Deltas(vec![
            ObjectDelta::Upsert(Box::new(object_view(3))),
            ObjectDelta::Remove(ObjectKey {
                local_id: 4,
                incarnation: 1,
            }),
        ]),
        PresentationEvent::GameTime(1_234_567.89),
        PresentationEvent::Sound(SoundEvent {
            guid: 1,
            sound_type: 2,
            volume: 0.5,
        }),
        PresentationEvent::PlayScript(PlayScript {
            guid: 3,
            script_id: 0x3300_0001,
        }),
        PresentationEvent::PlayScriptType(PlayScriptType {
            guid: 4,
            script_type: 0x52,
            intensity: 0.7,
        }),
        PresentationEvent::DestroyParticles(5),
        PresentationEvent::AdminEnvirons(0x66),
        PresentationEvent::Chat(vec![chat_line()]),
        PresentationEvent::Player(player_view()),
    ];
    events.extend(
        interface_events()
            .into_iter()
            .map(PresentationEvent::Interface),
    );
    events.extend(
        [
            PortalSpaceEvent::Entered {
                teleport_sequence: 1,
            },
            PortalSpaceEvent::Destination {
                generation: 2,
                teleport_sequence: 3,
                destination_cell: 4,
            },
            PortalSpaceEvent::Arrived {
                teleport_sequence: 5,
                destination_cell: 6,
            },
            PortalSpaceEvent::LoginEntering,
            PortalSpaceEvent::LoginDestination {
                generation: 7,
                destination_cell: 8,
            },
            PortalSpaceEvent::LoginWithdrawn,
            PortalSpaceEvent::LogoutStarted,
        ]
        .map(PresentationEvent::PortalSpace),
    );
    events.extend(
        pregame_events(epoch)
            .into_iter()
            .map(PresentationEvent::PreGame),
    );
    events.push(PresentationEvent::StatusText("Waiting".into()));
    events.extend(
        [
            WindowRequest::ToggleFrameRate,
            WindowRequest::SaveUi("a".into()),
            WindowRequest::LoadUi("b".into()),
            WindowRequest::SaveAutoUi,
            WindowRequest::LoadAutoUi,
            WindowRequest::SetLandscapeRadius(5),
            WindowRequest::SetFieldOfView(90),
            WindowRequest::SetClipboard("c".into()),
            WindowRequest::Minimize,
            WindowRequest::Restore,
            WindowRequest::GameplayOptionsPage,
        ]
        .map(PresentationEvent::WindowRequest),
    );
    events.extend([
        PresentationEvent::Link(LinkStatus {
            connected: true,
            seconds_since_last_packet: 0.25,
            packet_loss_percent: 1.5,
            round_trip_seconds: Some(0.08),
        }),
        PresentationEvent::Navigation(NavigationView {
            frame_center: 0xA9B4_0000,
            lines: vec![NavLine {
                from: glam::Vec3::new(1.0, 2.0, 3.0),
                to: glam::Vec3::new(4.0, 5.0, 6.0),
                kind: NavLineKind::Portal,
                follows_ground: true,
            }],
        }),
        PresentationEvent::Motions(vec![AppliedMotion {
            key: ObjectKey {
                local_id: 1,
                incarnation: 2,
            },
            guid: 3,
            motion: motion(4),
            taken_up: true,
        }]),
        PresentationEvent::SessionClock(epoch + Duration::from_micros(987_654)),
    ]);
    events
}

/// The variant's name, matched with no wildcard: a new event breaks this
/// test's build until it has a sample above.
fn event_name(event: &PresentationEvent) -> &'static str {
    match event {
        PresentationEvent::Snapshot(_) => "Snapshot",
        PresentationEvent::Deltas(_) => "Deltas",
        PresentationEvent::GameTime(_) => "GameTime",
        PresentationEvent::Sound(_) => "Sound",
        PresentationEvent::PlayScript(_) => "PlayScript",
        PresentationEvent::PlayScriptType(_) => "PlayScriptType",
        PresentationEvent::DestroyParticles(_) => "DestroyParticles",
        PresentationEvent::AdminEnvirons(_) => "AdminEnvirons",
        PresentationEvent::Chat(_) => "Chat",
        PresentationEvent::Player(_) => "Player",
        PresentationEvent::Interface(_) => "Interface",
        PresentationEvent::PortalSpace(_) => "PortalSpace",
        PresentationEvent::PreGame(_) => "PreGame",
        PresentationEvent::StatusText(_) => "StatusText",
        PresentationEvent::WindowRequest(_) => "WindowRequest",
        PresentationEvent::Link(_) => "Link",
        PresentationEvent::Navigation(_) => "Navigation",
        PresentationEvent::Motions(_) => "Motions",
        PresentationEvent::SessionClock(_) => "SessionClock",
    }
}

const EVENT_NAMES: [&str; 19] = [
    "Snapshot",
    "Deltas",
    "GameTime",
    "Sound",
    "PlayScript",
    "PlayScriptType",
    "DestroyParticles",
    "AdminEnvirons",
    "Chat",
    "Player",
    "Interface",
    "PortalSpace",
    "PreGame",
    "StatusText",
    "WindowRequest",
    "Link",
    "Navigation",
    "Motions",
    "SessionClock",
];

#[test]
fn every_event_round_trips() {
    let (e, d) = contexts();
    let events = every_event(e.epoch);
    let names: HashSet<&str> = events.iter().map(event_name).collect();
    assert_eq!(names, HashSet::from(EVENT_NAMES), "a variant has no sample");
    let bytes = encode_batch(&events, &e).expect("encodes");
    let back = decode_batch(&bytes, &d).expect("decodes");
    assert_eq!(back.len(), events.len());
    for (sent, received) in events.iter().zip(&back) {
        assert_eq!(sent, received);
        // The player's numbers compare by revision alone: compare what
        // they print (hash sets elsewhere print in no fixed order).
        if let PresentationEvent::Interface(InterfaceEvent::Player(_)) = sent {
            assert_eq!(format!("{sent:?}"), format!("{received:?}"));
        }
    }
    // The same value gives the same bytes again (maps go out sorted).
    assert_eq!(encode_batch(&back, &e).expect("encodes"), bytes);
}

#[test]
fn instants_land_at_the_same_offset_from_the_receivers_epoch() {
    let (e, _) = contexts();
    let receiver = DecodeContext::new(e.epoch + Duration::from_secs(60), None, None);
    let sent = PresentationEvent::SessionClock(e.epoch + Duration::from_millis(1500));
    let bytes = encode_batch(std::slice::from_ref(&sent), &e).unwrap();
    let back = decode_batch(&bytes, &receiver).unwrap();
    assert_eq!(
        back,
        [PresentationEvent::SessionClock(
            receiver.epoch + Duration::from_millis(1500)
        )]
    );
}

/// A small spell table with names, as the receiver's game data has it.
fn named_table() -> Arc<ac_core::spells::SpellTable> {
    use ac_core::spells::{SpellMetadata, SpellTable};
    Arc::new(
        SpellTable::create([
            SpellMetadata::new(1, "Flame Bolt I", 0),
            SpellMetadata::new(2, "Strength Self I", 0),
        ])
        .expect("unique ids"),
    )
}

/// The player's facts with this spellbook, through the wire with `d`.
fn book_through(book: ac_core::spells::Spellbook, d: &DecodeContext) -> ac_core::spells::Spellbook {
    let (e, _) = contexts();
    let facts = PlayerFacts {
        revision: 1,
        numbers: ac_core::player::LocalPlayerState::new(Some(book)),
        session_seconds: 0.0,
    };
    let event = PresentationEvent::Interface(InterfaceEvent::Player(Box::new(facts)));
    let back = decode_batch(&encode_batch(&[event], &e).unwrap(), d).unwrap();
    let [PresentationEvent::Interface(InterfaceEvent::Player(facts))] = &back[..] else {
        panic!("{back:?}");
    };
    facts.numbers.spellbook().expect("a spellbook").clone()
}

/// A receiver with no spell table yet gets a book with none installed, so
/// installing its table later is not refused (and the spells get their
/// names); a receiver with one gets its own table, names and all.
#[test]
fn a_decoded_spellbook_has_the_receivers_table_or_none() {
    let sender_table = named_table();
    let book = ac_core::spells::Spellbook::new(Some(Arc::clone(&sender_table)));
    assert_eq!(
        book.try_get_metadata(1).map(|s| s.name.as_str()),
        Some("Flame Bolt I")
    );
    let epoch = Instant::now();

    let mut none = book_through(book.clone(), &DecodeContext::new(epoch, None, None));
    assert!(none.try_get_metadata(1).is_none());
    let receiver_table = named_table();
    none.install_metadata(Arc::clone(&receiver_table))
        .expect("a book with no table takes one");
    assert_eq!(
        none.try_get_metadata(1).map(|s| s.name.as_str()),
        Some("Flame Bolt I")
    );

    let given = DecodeContext::new(epoch, Some(Arc::clone(&receiver_table)), None);
    let mut with = book_through(book, &given);
    assert!(Arc::ptr_eq(with.metadata(), &receiver_table));
    assert_eq!(
        with.try_get_metadata(2).map(|s| s.name.as_str()),
        Some("Strength Self I")
    );
    with.install_metadata(Arc::clone(&receiver_table))
        .expect("installing the same table again is no change");

    // A book the sender had no table for stays without one.
    let bare = book_through(ac_core::spells::Spellbook::new(None), &given);
    assert!(bare.try_get_metadata(1).is_none());
}

/// The skill formulas are the receiver's: decoded numbers score a skill
/// from buffed attributes as the session does.
#[test]
fn decoded_numbers_score_buffed_skills_with_the_receivers_formulas() {
    use ac_core::player::{LocalPlayerState, SkillFormulaBonusResolver};
    use ac_core::spells::{EnchantmentTypeFlags, Spellbook};
    let mut book = Spellbook::new(None);
    // A blessing adding 9 to every attribute.
    book.on_enchantment_added(ActiveEnchantmentRecord {
        stat_mod_type: Some(
            (EnchantmentTypeFlags::ATTRIBUTE | EnchantmentTypeFlags::MULTIPLE_STAT).0 | 0x8000,
        ),
        stat_mod_key: Some(0),
        stat_mod_value: Some(9.0),
        bucket: 2,
        spell_category: 2,
        ..ActiveEnchantmentRecord::new(2, 2, 60.0, 0)
    });
    // Half the endurance (attribute 2), the shape of a skill formula.
    let formulas: SkillFormulaBonusResolver =
        Arc::new(|_, _, attributes| attributes.get(&2).copied().unwrap_or(0) / 2);
    let mut state = LocalPlayerState::new(Some(book));
    state.set_skill_formula_bonus_resolver(Some(Arc::clone(&formulas)));
    state.on_attribute_update(2, 0, 200, 0);
    state.on_skill_update(24, 0, 2, 0, 0, 0, 0.0, 100);
    let term = state.attribute_enchantment_skill_delta(24);
    assert_ne!(term, 0, "the buff changes the skill");
    let expected = state.get_skill_value(24, None);

    let (e, _) = contexts();
    let facts = PlayerFacts {
        revision: 1,
        numbers: state,
        session_seconds: 0.0,
    };
    let event = PresentationEvent::Interface(InterfaceEvent::Player(Box::new(facts)));
    let bytes = encode_batch(&[event], &e).unwrap();
    let d = DecodeContext::new(e.epoch, None, Some(formulas));
    let back = decode_batch(&bytes, &d).unwrap();
    let [PresentationEvent::Interface(InterfaceEvent::Player(facts))] = &back[..] else {
        panic!("{back:?}");
    };
    assert_eq!(facts.numbers.attribute_enchantment_skill_delta(24), term);
    assert_eq!(facts.numbers.get_skill_value(24, None), expected);
}
fn every_command() -> Vec<SessionCommand> {
    use SessionCommand as C;
    let g = ObjectRef::Guid(0x8000_0001);
    let n = || ObjectRef::Name("Sword".into());
    let items = [
        ItemCommand::Use(g.clone()),
        ItemCommand::UseWorldObject(n()),
        ItemCommand::UseOn {
            item: g.clone(),
            target: ObjectRef::Player,
        },
        ItemCommand::PickUp(g.clone()),
        ItemCommand::PlaceInPack(g.clone()),
        ItemCommand::PickUpToMainPack(g.clone()),
        ItemCommand::SetOpenPack(3),
        ItemCommand::MoveToContainer {
            item: g.clone(),
            container: n(),
            slot: 2,
            amount: 5,
            join_stack: true,
        },
        ItemCommand::Merge {
            from: g.clone(),
            to: n(),
            amount: 6,
        },
        ItemCommand::Drop {
            item: g.clone(),
            amount: 7,
        },
        ItemCommand::Give {
            item: g.clone(),
            target: n(),
            amount: 8,
        },
        ItemCommand::Wield {
            item: g.clone(),
            location: 0x10,
        },
        ItemCommand::WieldSecondary(g.clone()),
        ItemCommand::Unwield(g.clone()),
        ItemCommand::Appraise(g.clone()),
        ItemCommand::StopViewing(g.clone()),
        ItemCommand::Buy(vec![(g.clone(), 2)]),
        ItemCommand::Sell(vec![(n(), 3)]),
        ItemCommand::Vendor(VendorCommand::AddToBuyList {
            item: g.clone(),
            amount: 4,
        }),
        ItemCommand::Vendor(VendorCommand::SellAll),
        ItemCommand::OpenTrade(n()),
        ItemCommand::AddToTrade(g.clone()),
        ItemCommand::AcceptTrade,
        ItemCommand::DeclineTrade,
        ItemCommand::ResetTrade,
        ItemCommand::CloseTrade,
        ItemCommand::HousePurchase(HousePurchaseCommand::Submit {
            tab: HousePurchaseTab::Rent,
            slumlord: 9,
        }),
        ItemCommand::DropInto {
            item: g.clone(),
            container: n(),
            slot: 1,
            amount: 2,
            pickup: true,
            join_stack: true,
        },
        ItemCommand::DropOnStack {
            item: g.clone(),
            target: n(),
            merge_amount: 3,
            container: ObjectRef::Player,
            slot: Some(4),
            amount: 5,
            pickup: true,
        },
        ItemCommand::DropOnBackpack(g.clone()),
        ItemCommand::MarkSalvage(vec![1, 2]),
        ItemCommand::AddShortcut(ShortcutEntry {
            index: 1,
            object_id: 2,
            spell_id: 3,
        }),
        ItemCommand::RemoveShortcut(4),
        ItemCommand::PickUpToShortcut {
            item: g.clone(),
            slot: 5,
        },
        ItemCommand::EnterUseMode,
        ItemCommand::EnterExamineMode,
        ItemCommand::CancelTargetMode,
        ItemCommand::OfferTarget(g.clone()),
        ItemCommand::CloseContainer(g.clone()),
        ItemCommand::Inscribe {
            item: g.clone(),
            text: "Mine".into(),
        },
        ItemCommand::Salvage {
            tool: g.clone(),
            items: vec![n(), ObjectRef::Player],
        },
        ItemCommand::QueryMana(g.clone()),
        ItemCommand::RefreshCurrentAppraisal,
        ItemCommand::UseSelected,
        ItemCommand::ExamineSelected,
        ItemCommand::PickUpSelected,
        ItemCommand::PlaceSelectedInPack,
        ItemCommand::PickUpSelectedToMainPack,
        ItemCommand::GiveSelectedToPrevious { amount: 6 },
        ItemCommand::DropSelected { amount: 7 },
        ItemCommand::Place {
            item: g.clone(),
            target: Some(n()),
            amount: 8,
        },
    ];
    let gameplay = [
        GameplayCommand::Select(g.clone()),
        GameplayCommand::SelectAs(g.clone(), SelectionSource::Mouse),
        GameplayCommand::ClearSelection,
        GameplayCommand::ClearRevealedSelection(1),
        GameplayCommand::Escape,
        GameplayCommand::SelectPrevious,
        GameplayCommand::SelectNearestMonster,
        GameplayCommand::CycleSelection {
            kind: SelectionCycleKind::UnopenedCorpse,
            direction: SelectionCycleDirection::Previous,
        },
        GameplayCommand::UseUnopenedCorpse(SelectionCycleDirection::Next),
        GameplayCommand::SelectSelf,
        GameplayCommand::SelectLastAttacker,
        GameplayCommand::CycleFellow { previous: true },
        GameplayCommand::Cast {
            spell: "Flame Bolt I".into(),
            target: Some(g.clone()),
        },
        GameplayCommand::CastWithoutSelecting {
            spell: 1,
            target: 2,
        },
        GameplayCommand::ListSpells(Some("bolt".into())),
        GameplayCommand::ShowEnchantments,
        GameplayCommand::ShowCharacter,
        GameplayCommand::ForgetSpell("x".into()),
        GameplayCommand::AddFavorite {
            tab: 1,
            position: 2,
            spell: "y".into(),
        },
        GameplayCommand::RemoveFavorite {
            tab: 3,
            spell: "z".into(),
        },
        GameplayCommand::SetSpellbookFilter(0x3FFF),
        GameplayCommand::SetDesiredComponent {
            component: 1,
            amount: 2,
        },
        GameplayCommand::ClearDesiredComponents,
        GameplayCommand::ToggleCombat,
        GameplayCommand::SetCombatMode(CombatMode::MAGIC),
        GameplayCommand::Attack {
            height: AttackHeight::HIGH,
            target: None,
        },
        GameplayCommand::AttackInput(CombatAttackCommand::IncreasePower, InputActivation::Release),
        GameplayCommand::SetAttackPower(0.75),
        GameplayCommand::ShowCombat,
        GameplayCommand::Book(BookCommand::TurnWriting {
            page: 2,
            typed: Some("words".into()),
        }),
        GameplayCommand::Chess(ChessCommand::Move {
            from: ChessSquare { x: 1, y: 2 },
            to: ChessSquare { x: 1, y: 4 },
        }),
        GameplayCommand::Barber(BarberCommand::SetShade(ShadeSlot::Hair, 0.5)),
        GameplayCommand::Fellowship(FellowshipCommand::Create {
            name: "F".into(),
            share_xp: true,
        }),
        GameplayCommand::Allegiance(AllegianceCommand::Swear(n())),
        GameplayCommand::QueryHouse,
        GameplayCommand::ShowHouse,
        GameplayCommand::ShowVitae,
        GameplayCommand::SetTitle(5),
        GameplayCommand::ShowTitles,
    ];
    let lifecycle = [
        LifecycleCommand::Reconnect,
        LifecycleCommand::LogOff,
        LifecycleCommand::EndCharacterSession,
        LifecycleCommand::ShowSelection,
        LifecycleCommand::Highlight(CharacterSelector::Index(2)),
        LifecycleCommand::Enter(Some(CharacterSelector::Name("+Hero".into()))),
        LifecycleCommand::RequestDelete(Some(CharacterSelector::Id(7))),
        LifecycleCommand::ConfirmDelete,
        LifecycleCommand::Restore(None),
        LifecycleCommand::Cancel,
        LifecycleCommand::SetNextLogin(Some(CharacterSelector::FirstAvailable)),
        LifecycleCommand::Create(CreationCommand::SetAttribute(ChargenAttribute::Self_, 100)),
        LifecycleCommand::Create(CreationCommand::SetAppearance(
            AppearanceSlot::FootwearColor,
            3,
        )),
        LifecycleCommand::Create(CreationCommand::Finish {
            confirm_unspent_credits: true,
        }),
        LifecycleCommand::Create(CreationCommand::Show),
        LifecycleCommand::Create(CreationCommand::SelectHeritage(1)),
        LifecycleCommand::Create(CreationCommand::SelectGender(2)),
        LifecycleCommand::Create(CreationCommand::SelectTemplate(3)),
        LifecycleCommand::Create(CreationCommand::SetAttributeLock(
            ChargenAttribute::Focus,
            true,
        )),
        LifecycleCommand::Create(CreationCommand::TrainSkill(6)),
        LifecycleCommand::Create(CreationCommand::SpecializeSkill(7)),
        LifecycleCommand::Create(CreationCommand::UntrainSkill(8)),
        LifecycleCommand::Create(CreationCommand::SetShade(ShadeSlot::Skin, 0.25)),
        LifecycleCommand::Create(CreationCommand::SelectStartArea(2)),
        LifecycleCommand::Create(CreationCommand::SetName("Newbie".into())),
        LifecycleCommand::Create(CreationCommand::SetSlot(4)),
        LifecycleCommand::Create(CreationCommand::AcknowledgeRejection),
        LifecycleCommand::Create(CreationCommand::RandomizeCharacter),
        LifecycleCommand::Create(CreationCommand::RandomizeAppearance),
        LifecycleCommand::Create(CreationCommand::RandomizeClothing),
    ];
    let movement = [
        MovementCommand::Keys(MovementKeys {
            forward: true,
            strafe_left: true,
            jump: true,
            ..MovementKeys::default()
        }),
        MovementCommand::ToggleRunLock,
        MovementCommand::Stop,
        MovementCommand::Posture(Posture::Crouch),
        MovementCommand::Move(MoveRequest {
            direction: MoveDirection::TurnLeft,
            pace: MovePace::Run,
            amount: 90.0,
            unit: MoveUnit::Seconds,
        }),
        MovementCommand::Jump {
            power: 0.5,
            leave_at: Some(MovePace::Walk),
        },
        MovementCommand::StopMoves,
        MovementCommand::TurnToHeading(180.0),
        MovementCommand::FirstPersonTurn(-10.0),
        MovementCommand::Status,
        MovementCommand::BeginMouseLook,
        MovementCommand::MouseTurn(3.5),
        MovementCommand::StopMouseDrift,
        MovementCommand::EndMouseLook,
        MovementCommand::BeginModernMouseLook,
        MovementCommand::MouseLookFacing(45.0),
        MovementCommand::Motion(0x1300_0087),
        MovementCommand::ReleaseKeys,
    ];
    let ui = [
        UiInput::Action {
            panel: 1,
            handler: "OnGo".into(),
            value: ActionValue::TextBool("key".into(), true),
        },
        UiInput::Action {
            panel: 1,
            handler: "OnSort".into(),
            value: ActionValue::IntBool(2, false),
        },
        UiInput::PanelShown {
            panel: 2,
            shown: true,
        },
        UiInput::Pointer {
            canvas: 3,
            event: PointerEvent {
                kind: PointerKind::Wheel,
                x: 1.0,
                y: 2.0,
                button: 1,
                modifiers: 6,
                wheel_delta: -2,
            },
            place: Some(CanvasPlace {
                origin: (10.0, 20.0),
                layer: (800.0, 600.0),
            }),
        },
        UiInput::ClientWindows {
            mounted: 0x1FFF,
            visible: 0x10,
        },
        UiInput::ClientChords(vec![ac_plugin_ui::hotkeys::Chord {
            key: 0x42,
            ctrl: false,
            alt: true,
            shift: false,
        }]),
        UiInput::Hotkey {
            owner: "example".into(),
            id: "go".into(),
        },
        UiInput::InputAction {
            action: "Jump".into(),
            activation: "Press".into(),
        },
        UiInput::Theme(ThemeKind::Glacier),
        UiInput::FileIconsShown {
            panel: 1,
            paths: vec!["a.png".into()],
        },
        UiInput::DisplayDensity(1.5),
        UiInput::CanvasHosted {
            canvas: 3,
            size: Some((100, 50)),
        },
        UiInput::DialogAnswered {
            handle: 6,
            button: 1,
        },
    ];
    let mut commands = vec![
        C::Chat("/who".into()),
        C::ChatInput {
            text: "hello".into(),
            channel: ChatChannelKind::Tell,
            tell_target: Some("Bob".into()),
            tell_target_guid: 0x5000_0002,
        },
        C::ChatEntry(ChatEntryCommand::SetTellTarget {
            name: "Ann".into(),
            guid: 3,
        }),
        C::ChatEntry(ChatEntryCommand::Submit(None)),
        C::Say("hi".into()),
        C::Tell {
            target: "Bob".into(),
            text: "psst".into(),
        },
        C::Reply("yes".into()),
        C::Emote("waves".into()),
        C::Channel {
            channel: ChatChannelKind::Lfg,
            text: "lf2m".into(),
        },
        C::Recall(Recall::AllegianceHometown),
        C::AddFriend("Pal".into()),
        C::RemoveFriend("Foe".into()),
        C::Squelch {
            add: true,
            name: "Spam".into(),
            account: true,
        },
        C::AbandonContract(7),
        C::ListContracts,
        C::Journal(JournalCommand::SetTimer {
            days: 1,
            hours: 2,
            minutes: 3,
        }),
        C::Journal(JournalCommand::DeletePage(4)),
        C::ShowJournal,
        C::SetCharacterOption {
            option: 0x14,
            value: true,
        },
        C::SaveCharacterOptions,
        C::ShowCharacterOptions,
        C::ListInventory,
        C::AtGeneration {
            generation: Generation(9),
            command: Box::new(C::Movement(MovementCommand::Stop)),
        },
        C::ListObjects,
        C::AnswerConfirmation {
            confirmation: ClientConfirmation::ManaStoneDrain {
                stone: 1,
                target: 2,
                message: "Drain?".into(),
            },
            accepted: true,
        },
        C::AnswerOpenConfirmation { accepted: false },
        C::ListCorpses(25.0),
        C::GameTime,
        C::Character(CharacterCommand::TrainSkill {
            skill: 6,
            credits: 4,
        }),
        C::Character(CharacterCommand::RaiseAttribute {
            attribute: 1,
            xp: u64::MAX,
        }),
        C::Character(CharacterCommand::RaiseVital { vital: 2, xp: 3 }),
        C::Character(CharacterCommand::RaiseSkill { skill: 4, xp: 5 }),
        C::ChatEntry(ChatEntryCommand::SetDraft("dra".into())),
        C::ChatEntry(ChatEntryCommand::Compose("/vt nav".into())),
        C::ChatEntry(ChatEntryCommand::SetChannel(ChatChannelKind::Society)),
        C::ChatEntry(ChatEntryCommand::RecallPrevious),
        C::ChatEntry(ChatEntryCommand::RecallNext),
        C::ChatEntry(ChatEntryCommand::ResetRecall),
        C::ChatEntry(ChatEntryCommand::Clear),
        C::Journal(JournalCommand::NewPage),
        C::Journal(JournalCommand::GotoPage(2)),
        C::Journal(JournalCommand::Edit {
            label: "l".into(),
            title: "t".into(),
            notes: "n".into(),
        }),
        C::Journal(JournalCommand::RecordLocation { x: 1.5, y: -2.5 }),
        C::Journal(JournalCommand::StartTimer),
        C::Journal(JournalCommand::ResetTimer),
        C::Ping,
        C::ShowLinkStatus,
        C::DismissGhost(0x8000_0009),
        C::ShowInterfaceText("Waiting".into()),
        C::ShowText {
            text: "Hi".into(),
            log_text_type: LogTextType::TurbineSociety,
        },
        C::WindowMinimized(true),
        C::ChatLinkClicked {
            east_west: 12.5,
            north_south: -30.25,
            text: "12.5E, 30.3S".into(),
        },
        C::DestinationShown(DestinationShown {
            teleport_sequence: 3,
            readiness: DestinationReadiness {
                generation: 1,
                destination_cell: 2,
                is_indoor: true,
                is_unhydratable: true,
                required_render_radius: 3,
                is_render_neighborhood_ready: true,
                are_composite_textures_ready: true,
                is_collision_ready: true,
            },
        }),
        C::LoginShown(DestinationReadiness {
            generation: 4,
            destination_cell: 5,
            ..DestinationReadiness::default()
        }),
        C::PresentsWorld(true),
        C::RevealWaited {
            generation: 6,
            waited: Duration::from_millis(5500),
        },
    ];
    commands.extend(items.into_iter().map(C::Item));
    commands.extend(gameplay.into_iter().map(C::Gameplay));
    commands.extend(lifecycle.into_iter().map(C::Lifecycle));
    commands.extend(movement.into_iter().map(C::Movement));
    commands.extend(ui.into_iter().map(C::PluginUi));
    commands
}

/// The command's name, matched with no wildcard: a new command breaks this
/// test's build until it has a sample above (or is listed as in-process).
fn command_name(command: &SessionCommand) -> &'static str {
    use SessionCommand as C;
    match command {
        C::Chat(_) => "Chat",
        C::ChatInput { .. } => "ChatInput",
        C::ChatEntry(_) => "ChatEntry",
        C::Say(_) => "Say",
        C::Tell { .. } => "Tell",
        C::Reply(_) => "Reply",
        C::Emote(_) => "Emote",
        C::Channel { .. } => "Channel",
        C::Recall(_) => "Recall",
        C::AddFriend(_) => "AddFriend",
        C::RemoveFriend(_) => "RemoveFriend",
        C::Squelch { .. } => "Squelch",
        C::AbandonContract(_) => "AbandonContract",
        C::ListContracts => "ListContracts",
        C::Journal(_) => "Journal",
        C::ShowJournal => "ShowJournal",
        C::SetCharacterOption { .. } => "SetCharacterOption",
        C::SaveCharacterOptions => "SaveCharacterOptions",
        C::ShowCharacterOptions => "ShowCharacterOptions",
        C::Item(_) => "Item",
        C::ListInventory => "ListInventory",
        C::Gameplay(_) => "Gameplay",
        C::Lifecycle(_) => "Lifecycle",
        C::AtGeneration { .. } => "AtGeneration",
        C::Movement(_) => "Movement",
        C::Attach(_) => "Attach",
        C::AttachRemote(_) => "AttachRemote",
        C::Detach => "Detach",
        C::WorldTap(_) => "WorldTap",
        C::Shutdown => "Shutdown",
        C::ListObjects => "ListObjects",
        C::AnswerConfirmation { .. } => "AnswerConfirmation",
        C::AnswerOpenConfirmation { .. } => "AnswerOpenConfirmation",
        C::ListCorpses(_) => "ListCorpses",
        C::GameTime => "GameTime",
        C::Character(_) => "Character",
        C::Ping => "Ping",
        C::ShowLinkStatus => "ShowLinkStatus",
        C::DismissGhost(_) => "DismissGhost",
        C::ShowInterfaceText(_) => "ShowInterfaceText",
        C::ShowText { .. } => "ShowText",
        C::WindowMinimized(_) => "WindowMinimized",
        C::ChatLinkClicked { .. } => "ChatLinkClicked",
        C::PluginUi(_) => "PluginUi",
        C::DestinationShown(_) => "DestinationShown",
        C::LoginShown(_) => "LoginShown",
        C::PresentsWorld(_) => "PresentsWorld",
        C::RevealWaited { .. } => "RevealWaited",
    }
}

#[test]
fn every_wire_command_round_trips() {
    let (e, d) = contexts();
    let commands = every_command();
    let names: HashSet<&str> = commands.iter().map(command_name).collect();
    assert_eq!(
        names.len(),
        46 - 3,
        "a wire command has no sample: {names:?}"
    );
    for command in &commands {
        let bytes = encode_command(command, &e).unwrap_or_else(|e| panic!("{command:?}: {e}"));
        let back = decode_command(&bytes, &d).unwrap_or_else(|e| panic!("{command:?}: {e}"));
        // Commands are not `PartialEq` (one variant carries a channel):
        // compare what they print and the bytes they make.
        assert_eq!(format!("{command:?}"), format!("{back:?}"));
        assert_eq!(encode_command(&back, &e).unwrap(), bytes);
    }
}

#[test]
fn in_process_commands_have_no_encoding() {
    let (e, _) = contexts();
    let (tx, _rx) = std::sync::mpsc::channel();
    for (command, name) in [
        (SessionCommand::Attach(tx.clone()), "Attach"),
        (SessionCommand::AttachRemote(tx), "AttachRemote"),
        (SessionCommand::Detach, "Detach"),
        (SessionCommand::Shutdown, "Shutdown"),
    ] {
        assert_eq!(
            encode_command(&command, &e).unwrap_err(),
            EncodeError::NotAWireCommand(name)
        );
        // Not even behind a generation stamp.
        let stamped = SessionCommand::AtGeneration {
            generation: Generation(1),
            command: Box::new(command),
        };
        assert_eq!(
            encode_command(&stamped, &e).unwrap_err(),
            EncodeError::NotAWireCommand(name)
        );
    }
}

#[test]
fn a_stamp_holds_one_unstamped_command() {
    let (e, d) = contexts();
    let inner = SessionCommand::AtGeneration {
        generation: Generation(1),
        command: Box::new(SessionCommand::Ping),
    };
    let nested = SessionCommand::AtGeneration {
        generation: Generation(2),
        command: Box::new(inner),
    };
    assert_eq!(
        encode_command(&nested, &e).unwrap_err(),
        EncodeError::NestedAtGeneration
    );
    // Bytes that stamp a stamp are refused too.
    let one = encode_command(
        &SessionCommand::AtGeneration {
            generation: Generation(1),
            command: Box::new(SessionCommand::Ping),
        },
        &e,
    )
    .unwrap();
    let mut two = vec![STREAM_FORMAT];
    two.extend_from_slice(&one[1..10]);
    two.extend_from_slice(&one[1..]);
    assert_eq!(
        decode_command(&two, &d).unwrap_err(),
        DecodeError::NestedAtGeneration
    );
}

#[test]
fn unknown_tags_and_formats_are_errors() {
    let (_, d) = contexts();
    // One event with tag 200.
    let batch = [STREAM_FORMAT, 1, 0, 0, 0, 200];
    assert_eq!(
        decode_batch(&batch, &d).unwrap_err(),
        DecodeError::UnknownTag {
            what: "presentation event",
            tag: 200
        }
    );
    assert_eq!(
        decode_command(&[STREAM_FORMAT, 250], &d).unwrap_err(),
        DecodeError::UnknownTag {
            what: "session command",
            tag: 250
        }
    );
    assert_eq!(
        decode_batch(&[STREAM_FORMAT + 1, 0, 0, 0, 0], &d).unwrap_err(),
        DecodeError::Version {
            found: STREAM_FORMAT + 1,
            expected: STREAM_FORMAT
        }
    );
    // A plugin font that cannot be baked again: an interface event (tag
    // 10) of plugin UI changes (18) with one font (25) owned by "x", handle
    // 1, from its own file (source 1) of four bytes that are no font.
    let font = |file: &[u8], size: f32| {
        let mut bytes = vec![STREAM_FORMAT, 1, 0, 0, 0, 10, 18, 1, 0, 0, 0, 25];
        bytes.extend_from_slice(&[1, 0, 0, 0, b'x', 1, 0, 0, 0, 1]);
        bytes.extend_from_slice(&(file.len() as u32).to_le_bytes());
        bytes.extend_from_slice(file);
        bytes.extend_from_slice(&size.to_le_bytes());
        // One run of characters, 'A' to 'C'; no sharper bake.
        bytes.extend_from_slice(&[1, 0, 0, 0, 0x41, 0, 0, 0, 0x43, 0, 0, 0, 0]);
        bytes
    };
    assert!(matches!(
        decode_batch(&font(b"junk", 12.0), &d).unwrap_err(),
        DecodeError::Font(_)
    ));
    let noto = ac_plugin_ui::fonts::NOTO_SANS_REGULAR;
    assert!(
        decode_batch(&font(noto, 12.0), &d).is_ok(),
        "the real file bakes"
    );
    for size in [0.0, -1.0, f32::NAN, 1.0e9] {
        assert_eq!(
            decode_batch(&font(noto, size), &d).unwrap_err(),
            DecodeError::OutOfRange("font size")
        );
    }
    // Trailing bytes.
    assert_eq!(
        decode_batch(&[STREAM_FORMAT, 0, 0, 0, 0, 9], &d).unwrap_err(),
        DecodeError::Trailing(1)
    );
}

#[test]
fn every_strict_prefix_of_a_batch_is_an_error() {
    let (e, d) = contexts();
    let bytes = encode_batch(&every_event(e.epoch), &e).unwrap();
    for end in 0..bytes.len() {
        assert!(
            decode_batch(&bytes[..end], &d).is_err(),
            "a prefix of {end} of {} bytes decoded",
            bytes.len()
        );
    }
    for command in every_command() {
        let bytes = encode_command(&command, &e).unwrap();
        for end in 0..bytes.len() {
            assert!(
                decode_command(&bytes[..end], &d).is_err(),
                "{command:?} cut at {end} decoded"
            );
        }
    }
}

/// A small deterministic generator (xorshift), so a failure repeats.
struct Noise(u64);

impl Noise {
    fn next(&mut self) -> u64 {
        self.0 ^= self.0 << 13;
        self.0 ^= self.0 >> 7;
        self.0 ^= self.0 << 17;
        self.0
    }
}

#[test]
fn flipped_bytes_never_panic() {
    let (e, d) = contexts();
    let events = every_event(e.epoch);
    let batches: Vec<Vec<u8>> = events
        .chunks(7)
        .map(|chunk| encode_batch(chunk, &e).unwrap())
        .collect();
    let commands: Vec<Vec<u8>> = every_command()
        .iter()
        .map(|c| encode_command(c, &e).unwrap())
        .collect();
    let mut noise = Noise(0x9E37_79B9_7F4A_7C15);
    let mut decoded = 0;
    for round in 0..4000 {
        let source = if round % 2 == 0 {
            &batches[round / 2 % batches.len()]
        } else {
            &commands[round / 2 % commands.len()]
        };
        let mut bytes = source.clone();
        for _ in 0..1 + noise.next() % 3 {
            let at = (noise.next() % bytes.len() as u64) as usize;
            // Mostly a random byte; sometimes the largest count.
            if noise.next().is_multiple_of(4) && at + 4 <= bytes.len() {
                bytes[at..at + 4].copy_from_slice(&u32::MAX.to_le_bytes());
            } else {
                bytes[at] = noise.next() as u8;
            }
        }
        // Whatever comes back is a value that encodes again.
        if let Ok(events) = decode_batch(&bytes, &d) {
            encode_batch(&events, &e).expect("a decoded batch encodes");
            decoded += 1;
        }
        if let Ok(command) = decode_command(&bytes, &d) {
            encode_command(&command, &e).expect("a decoded command encodes");
            decoded += 1;
        }
    }
    // Flips into names, numbers and padding leave values that still read.
    assert!(decoded > 100, "only {decoded} flipped inputs decoded");
}

#[test]
fn a_hostile_count_allocates_nothing() {
    let (_, d) = contexts();
    // A batch claiming four billion events, a snapshot claiming four
    // billion objects, a chat claiming four billion lines.
    for bytes in [
        vec![STREAM_FORMAT, 0xFF, 0xFF, 0xFF, 0xFF],
        vec![STREAM_FORMAT, 1, 0, 0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF],
        vec![
            STREAM_FORMAT,
            1,
            0,
            0,
            0,
            8,
            0xFF,
            0xFF,
            0xFF,
            0xFF,
            1,
            2,
            3,
        ],
    ] {
        assert!(matches!(
            decode_batch(&bytes, &d),
            Err(DecodeError::CountTooLarge { .. })
        ));
    }
}

#[test]
fn deeply_nested_values_are_refused() {
    let (_, d) = contexts();
    // Plugin UI changes > panel values > a value holding item records
    // holding values, two hundred levels deep.
    let mut bytes = vec![STREAM_FORMAT, 1, 0, 0, 0, 10, 18, 1, 0, 0, 0, 3];
    bytes.extend_from_slice(&3u32.to_le_bytes());
    bytes.extend_from_slice(&1u32.to_le_bytes());
    bytes.extend_from_slice(&1u32.to_le_bytes());
    bytes.push(b'v');
    for _ in 0..200 {
        // Items: one record with one value named "v".
        bytes.push(11);
        bytes.extend_from_slice(&1u32.to_le_bytes());
        bytes.extend_from_slice(&1u32.to_le_bytes());
        bytes.extend_from_slice(&1u32.to_le_bytes());
        bytes.push(b'v');
    }
    bytes.extend_from_slice(&[0, 1]);
    assert_eq!(decode_batch(&bytes, &d).unwrap_err(), DecodeError::TooDeep);
}

/// What a baked font is made of and measures, compared field by field (a
/// font compares equal only to the very same bake).
fn same_bake(a: &ac_plugin_ui::fonts::FontBake, b: &ac_plugin_ui::fonts::FontBake) {
    assert_eq!(a.pixel_size.to_bits(), b.pixel_size.to_bits());
    assert_eq!(a.line_height.to_bits(), b.line_height.to_bits());
    assert_eq!(a.ascent.to_bits(), b.ascent.to_bits());
    assert_eq!(
        (a.atlas_width, a.atlas_height),
        (b.atlas_width, b.atlas_height)
    );
    assert!(a.coverage == b.coverage, "the atlases differ");
    assert_eq!(a.glyph_count(), b.glyph_count());
    for c in (0x20..0x250).filter_map(char::from_u32) {
        assert_eq!(a.glyph(c), b.glyph(c), "{c:?}");
    }
    assert_eq!(a.measure_width("AVAWA To"), b.measure_width("AVAWA To"));
}

fn same_font(a: &ac_plugin_ui::fonts::FontView, b: &ac_plugin_ui::fonts::FontView) {
    assert_eq!((&a.owner, a.handle), (&b.owner, b.handle));
    same_bake(&a.font, &b.font);
    match (&a.sharp, &b.sharp) {
        (Some(x), Some(y)) => {
            assert_eq!(x.scale.to_bits(), y.scale.to_bits());
            same_bake(&x.font, &y.font);
        }
        (None, None) => {}
        _ => panic!("one has a sharper bake, the other not"),
    }
}

/// The fonts a session holds, as a window attaching sees them and as a
/// density change re-bakes them: each one crosses and is baked again the
/// same on the far side.
#[test]
fn plugin_fonts_cross_and_are_baked_again() {
    let (e, d) = contexts();
    let mut ui = ac_plugin_ui::PluginUi::new();
    let bundled = ui.font_bundled("example", 12.0, ac_plugin_ui::fonts::FontWeight::SemiBold);
    assert!(bundled.handle != 0, "the bundled font bakes");
    let own = ui.font_load(
        "example",
        "digits.ttf",
        ac_plugin_ui::fonts::NOTO_SANS_REGULAR,
        16.0,
        &[(0x30, 0x39), (0x41, 0x43)],
    );
    assert!(own.handle != 0, "the plugin's own font bakes");
    ui.input(UiInput::DisplayDensity(2.0));
    let mut changes = ui.snapshot();
    changes.extend(ui.take_changes());
    let fonts: Vec<_> = changes
        .iter()
        .filter(|c| matches!(c, UiChange::FontAdded(_)))
        .cloned()
        .collect();
    assert!(fonts.len() >= 4, "{} font changes", fonts.len());
    let event = PresentationEvent::Interface(InterfaceEvent::PluginUi(fonts.clone()));
    let bytes = encode_batch(std::slice::from_ref(&event), &e).expect("fonts encode");
    let back = decode_batch(&bytes, &d).expect("fonts decode");
    let [PresentationEvent::Interface(InterfaceEvent::PluginUi(received))] = &back[..] else {
        panic!("{back:?}");
    };
    assert_eq!(received.len(), fonts.len());
    let mut sharp = 0;
    for (sent, received) in fonts.iter().zip(received) {
        let (UiChange::FontAdded(sent), UiChange::FontAdded(received)) = (sent, received) else {
            panic!("not a font");
        };
        same_font(sent, received);
        sharp += usize::from(sent.sharp.is_some());
    }
    assert!(sharp > 0, "the density gave sharper bakes");
    // The copy goes out as the same bytes again.
    assert_eq!(encode_batch(&back, &e).unwrap(), bytes);
}

/// A decimal's arithmetic assumes at most 96 bits and 28 places: one past
/// either is refused rather than handed to it.
#[test]
fn a_decimal_out_of_range_is_refused() {
    use ac_plugin_ui::number::Dec;
    let (e, d) = contexts();
    let good = Dec::from_raw(-12_345, 2).unwrap();
    let bytes = crate::to_bytes(&good, &e).unwrap();
    let back: Dec = crate::from_bytes(&bytes, &d).unwrap();
    assert_eq!((back.mantissa(), back.scale()), (-12_345, 2));
    let mut scale = bytes.clone();
    scale[16..20].copy_from_slice(&29u32.to_le_bytes());
    assert_eq!(
        crate::from_bytes::<Dec>(&scale, &d),
        Err(DecodeError::OutOfRange("decimal"))
    );
    let mut wide = bytes;
    wide[..16].copy_from_slice(&i128::MIN.to_le_bytes());
    assert_eq!(
        crate::from_bytes::<Dec>(&wide, &d),
        Err(DecodeError::OutOfRange("decimal"))
    );
}

#[test]
fn the_subscription_filters_the_stateless_kinds() {
    let (e, _) = contexts();
    let events = every_event(e.epoch);
    let kinds = |s: &Subscription| -> HashSet<&'static str> {
        EVENT_NAMES
            .iter()
            .copied()
            .filter(|name| {
                events
                    .iter()
                    .filter(|e| event_name(e) == *name)
                    .all(|e| s.wanted(e))
            })
            .collect()
    };
    let default = Subscription::default();
    assert_eq!(default.objects, Objects::Radar);
    let refused = [
        "Motions",
        "Sound",
        "PlayScript",
        "PlayScriptType",
        "DestroyParticles",
        "Navigation",
    ];
    let wanted = kinds(&default);
    for name in EVENT_NAMES {
        assert_eq!(wanted.contains(name), !refused.contains(&name), "{name}");
    }
    let everything = Subscription {
        motions: true,
        sound: true,
        effects: true,
        navigation: true,
        objects: Objects::All,
    };
    assert_eq!(kinds(&everything).len(), EVENT_NAMES.len());
    let only_sound = Subscription {
        sound: true,
        ..Subscription::default()
    };
    assert!(kinds(&only_sound).contains("Sound"));
    assert!(!kinds(&only_sound).contains("Motions"));
}

/// The layout fingerprint of everything this crate carries. When a type in
/// the stream or the commands changes deliberately, this changes: update
/// the value here and bump `STREAM_FORMAT` in the same commit (both ends of
/// a link must be built from the same layout).
#[test]
fn the_shape_is_pinned() {
    assert_eq!(shape_hex(), "a5d6f5fd8af69fd7", "{:#x}", shape());
}

/// The size of a representative attach: a snapshot of a busy spot, a full
/// item table and the chat transcript.
#[test]
fn a_representative_attach_is_measured() {
    let (e, _) = contexts();
    let snapshot = PresentationEvent::Snapshot(WorldSnapshot {
        player_guid: Some(0x5000_0001),
        objects: (0..300).map(object_view).collect(),
    });
    let items = PresentationEvent::Interface(InterfaceEvent::Items(ItemsUpdate {
        reset: true,
        objects: (0..200).map(|n| client_object(0x8000_0000 + n)).collect(),
        removed: vec![],
        contents: vec![(
            0x5000_0001,
            Some((0..200).map(|n| 0x8000_0000 + n).collect()),
        )],
    }));
    let chat = PresentationEvent::Chat((0..500).map(|_| chat_line()).collect());
    let one =
        |event: &PresentationEvent| encode_batch(std::slice::from_ref(event), &e).unwrap().len();
    let (s, i, c) = (one(&snapshot), one(&items), one(&chat));
    eprintln!(
        "300 objects: {s} bytes ({} per object); 200 items: {i} bytes ({} per item); \
         500 chat lines: {c} bytes",
        s / 300,
        i / 200
    );
    assert!(s > 0 && i > 0 && c > 0);
}

/// Each enum's variant names, matched with no wildcard: a variant added to
/// one of these breaks this test's build until it has a sample.
macro_rules! variants {
    ($( $name:ident, $all:ident : $ty:ident { $($v:ident),+ $(,)? } )*) => {$(
        fn $name(value: &$ty) -> &'static str {
            match value {
                $( $ty::$v { .. } => stringify!($v), )+
            }
        }
        const $all: &[&str] = &[$( stringify!($v) ),+];
    )*};
}

variants! {
    item_variant, ITEM: ItemCommand {
        Use, UseWorldObject, UseOn, PickUp, PlaceInPack, PickUpToMainPack, SetOpenPack,
        MoveToContainer, Merge, Drop, Give, Wield, WieldSecondary, Unwield, Appraise,
        StopViewing, Buy, Sell, Vendor, OpenTrade, AddToTrade, AcceptTrade, DeclineTrade,
        ResetTrade, CloseTrade, HousePurchase, DropInto, DropOnStack, DropOnBackpack,
        MarkSalvage, AddShortcut, RemoveShortcut, PickUpToShortcut, EnterUseMode,
        EnterExamineMode, CancelTargetMode, OfferTarget, CloseContainer, Inscribe, Salvage,
        QueryMana, RefreshCurrentAppraisal, UseSelected, ExamineSelected, PickUpSelected,
        PlaceSelectedInPack, PickUpSelectedToMainPack, GiveSelectedToPrevious, DropSelected,
        Place,
    }
    gameplay_variant, GAMEPLAY: GameplayCommand {
        Select, SelectAs, ClearSelection, ClearRevealedSelection, Escape, SelectPrevious,
        SelectNearestMonster, CycleSelection, UseUnopenedCorpse, SelectSelf,
        SelectLastAttacker, CycleFellow, Cast, CastWithoutSelecting, ListSpells,
        ShowEnchantments, ShowCharacter, ForgetSpell, AddFavorite, RemoveFavorite,
        SetSpellbookFilter, SetDesiredComponent, ClearDesiredComponents, ToggleCombat,
        SetCombatMode, Attack, AttackInput, SetAttackPower, ShowCombat, Book, Chess, Barber,
        Fellowship, Allegiance, QueryHouse, ShowHouse, ShowVitae, SetTitle, ShowTitles,
    }
    lifecycle_variant, LIFECYCLE: LifecycleCommand {
        Reconnect, LogOff, EndCharacterSession, ShowSelection, Highlight, Enter,
        RequestDelete, ConfirmDelete, Restore, Cancel, SetNextLogin, Create,
    }
    creation_variant, CREATION: CreationCommand {
        Show, SelectHeritage, SelectGender, SelectTemplate, SetAttribute, SetAttributeLock,
        TrainSkill, SpecializeSkill, UntrainSkill, SetAppearance, SetShade, SelectStartArea,
        SetName, SetSlot, Finish, AcknowledgeRejection, RandomizeCharacter,
        RandomizeAppearance, RandomizeClothing,
    }
    movement_variant, MOVEMENT: MovementCommand {
        Keys, ToggleRunLock, Stop, Posture, Move, Jump, StopMoves, TurnToHeading,
        FirstPersonTurn, Status, BeginMouseLook, MouseTurn, StopMouseDrift, EndMouseLook,
        BeginModernMouseLook, MouseLookFacing, Motion, ReleaseKeys,
    }
    character_variant, CHARACTER: CharacterCommand {
        RaiseAttribute, RaiseVital, RaiseSkill, TrainSkill,
    }
    chat_entry_variant, CHAT_ENTRY: ChatEntryCommand {
        Submit, SetDraft, Compose, SetChannel, SetTellTarget, RecallPrevious, RecallNext,
        ResetRecall, Clear,
    }
    journal_variant, JOURNAL: JournalCommand {
        NewPage, DeletePage, GotoPage, Edit, RecordLocation, SetTimer, StartTimer, ResetTimer,
    }
    ui_input_variant, UI_INPUT: UiInput {
        Action, PanelShown, Pointer, ClientWindows, ClientChords, Hotkey, InputAction, Theme,
        FileIconsShown, DisplayDensity, CanvasHosted, DialogAnswered,
    }
    interface_variant, INTERFACE: InterfaceEvent {
        Items, Player, State, Appraisal, Trade, Vendor, Social, Contracts, Book, House,
        HousePurchase, Journal, OpenSalvage, Chess, Barber, BeginGame, OpenContainedContainer,
        ItemMana, PluginUi, Titles, ChatEntry, Selection,
    }
    ui_change_variant, UI_CHANGE: UiChange {
        Clear, PanelAdded, PanelRemoved, PanelValues, PanelRequestedVisible, ControlLabel,
        ControlVisible, CanvasAdded, CanvasRemoved, CanvasPaint, CanvasPlacement,
        CanvasListening, CanvasReleasePointer, CanvasResized, ImageAdded, FileIcon,
        ImageRemoved, Labels, Lines, Triangles, Icons, Shapes, ProjectileDebug, ClientWindow,
        Hotkeys, FontAdded, FontRemoved, DialogAdded, DialogRemoved, Modal,
    }
    pregame_variant, PREGAME: PreGameEvent { Connection, Selection, Creation, Options, Refused }
    portal_variant, PORTAL: PortalSpaceEvent {
        Entered, Destination, Arrived, LoginEntering, LoginDestination, LoginWithdrawn,
        LogoutStarted,
    }
    window_request_variant, WINDOW_REQUEST: WindowRequest {
        ToggleFrameRate, SaveUi, LoadUi, SaveAutoUi, LoadAutoUi, SetLandscapeRadius,
        SetFieldOfView, SetClipboard, Minimize, Restore, GameplayOptionsPage,
    }
    delta_variant, DELTA: ObjectDelta { Upsert, Remove }
}

/// A plugin font change, small enough to bake quickly.
fn font_change() -> UiChange {
    use ac_plugin_ui::fonts::{FontView, FontWeight, SharpBake, bake_bundled};
    let ranges = [(0x41, 0x43)];
    UiChange::FontAdded(Box::new(FontView {
        owner: "example".into(),
        handle: 7,
        font: Arc::new(bake_bundled(FontWeight::Regular, 12.0, &ranges).unwrap()),
        sharp: Some(SharpBake {
            scale: 2.0,
            font: Arc::new(bake_bundled(FontWeight::Regular, 24.0, &ranges).unwrap()),
        }),
    }))
}

#[test]
fn every_sub_variant_has_a_sample() {
    let mut seen: BTreeMap<&str, HashSet<&str>> = BTreeMap::new();
    let mut note = |kind: &'static str, name: &'static str| {
        seen.entry(kind).or_default().insert(name);
    };
    for command in every_command() {
        match &command {
            SessionCommand::Item(c) => note("item", item_variant(c)),
            SessionCommand::Gameplay(c) => note("gameplay", gameplay_variant(c)),
            SessionCommand::Lifecycle(c) => {
                note("lifecycle", lifecycle_variant(c));
                if let LifecycleCommand::Create(c) = c {
                    note("creation", creation_variant(c));
                }
            }
            SessionCommand::Movement(c) => note("movement", movement_variant(c)),
            SessionCommand::Character(c) => note("character", character_variant(c)),
            SessionCommand::ChatEntry(c) => note("chat entry", chat_entry_variant(c)),
            SessionCommand::Journal(c) => note("journal", journal_variant(c)),
            SessionCommand::PluginUi(c) => note("ui input", ui_input_variant(c)),
            _ => {}
        }
    }
    let (e, _) = contexts();
    let mut changes = vec![font_change()];
    for event in every_event(e.epoch) {
        match &event {
            PresentationEvent::Interface(i) => {
                note("interface", interface_variant(i));
                if let InterfaceEvent::PluginUi(c) = i {
                    changes.extend(c.iter().cloned());
                }
            }
            PresentationEvent::PreGame(p) => note("pregame", pregame_variant(p)),
            PresentationEvent::PortalSpace(p) => note("portal", portal_variant(p)),
            PresentationEvent::WindowRequest(w) => {
                note("window request", window_request_variant(w))
            }
            PresentationEvent::Deltas(d) => d.iter().for_each(|d| note("delta", delta_variant(d))),
            _ => {}
        }
    }
    for change in &changes {
        note("ui change", ui_change_variant(change));
    }
    for (kind, all) in [
        ("item", ITEM),
        ("gameplay", GAMEPLAY),
        ("lifecycle", LIFECYCLE),
        ("creation", CREATION),
        ("movement", MOVEMENT),
        ("character", CHARACTER),
        ("chat entry", CHAT_ENTRY),
        ("journal", JOURNAL),
        ("ui input", UI_INPUT),
        ("interface", INTERFACE),
        ("ui change", UI_CHANGE),
        ("pregame", PREGAME),
        ("portal", PORTAL),
        ("window request", WINDOW_REQUEST),
        ("delta", DELTA),
    ] {
        let missing: Vec<&str> = all
            .iter()
            .copied()
            .filter(|name| !seen.get(kind).is_some_and(|s| s.contains(name)))
            .collect();
        assert!(missing.is_empty(), "{kind} has no sample of {missing:?}");
    }
    // The font sample crosses too.
    let event = PresentationEvent::Interface(InterfaceEvent::PluginUi(vec![font_change()]));
    let (e, d) = contexts();
    let back = decode_batch(&encode_batch(&[event], &e).unwrap(), &d).unwrap();
    assert_eq!(back.len(), 1);
}
