//! What a window's panels read besides the world: the item table, the
//! player's numbers and spellbook, the interaction facts, appraisals, the
//! trade and the shop, the social facts, the quest tracker, books, houses,
//! the journal, chess, the barber, titles and the chat entry.

use std::collections::{BTreeMap, HashMap, HashSet};
use std::sync::Arc;

use ac_core::chargen::barber::BarberToggle;
use ac_core::chess::{
    ChessBoard, ChessBoardParts, ChessPiece, ChessPieceType, ChessSquare, ChessTeam,
};
use ac_core::combat::CombatMode;
use ac_core::items::{
    ClientArmorProfile, ClientCreatureAttributes, ClientCreatureProfile, ClientObject,
    ClientObjectHeader, ClientObjectParts, ClientWeaponProfile, EquipMask, HouseRestrictionRecord,
    ItemManaChange, ItemType, ItemUseable, ObjectIdentity, PropertyBundle, PublicWeenieFlags,
    ShortcutEntry, ShortcutStore, ShortcutStoreParts, VendorShopItem, VendorShopProfile,
};
use ac_core::journal::JournalPage;
use ac_core::player::{
    AttributeSnapshot, LocalPlayerState, LocalPlayerStateParts, SkillSnapshot, VitalSnapshot,
};
use ac_core::quests::{ContractCatalog, ContractEntry};
use ac_core::social::{
    FriendEntry, FriendsState, FriendsStateParts, SquelchDatabase, SquelchInfo, SquelchState,
    SquelchStateParts,
};
use ac_core::spells::{ActiveEnchantmentRecord, FAVORITE_BAR_COUNT, Spellbook, SpellbookParts};
use ac_proto::allegiance::{AllegianceMember, AllegianceProfile};
use ac_proto::appraise::{
    ArmorLevels, ArmorProfile, CreatureAttributes, CreatureProfile, EnchantmentHighlight,
    HookProfile, IdentifyObjectResponse, WeaponProfile,
};
use ac_proto::book::BookPage;
use ac_proto::contract::ContractTracker;
use ac_proto::house::{HousePayment, HouseProfile};
use ac_proto::{DesiredComponent, PropertyTables, Shortcut};
use ac_runtime::character::{CharacterOptions, CharacterOptionsParts};
use ac_runtime::communication::{
    ChatChannelKind, ClientConfirmation, ItemUseConfirmation, JournalView, TrackedContract,
};
use ac_runtime::gameplay::{
    BarberView, BookView, ChessStage, ChessView, FellowView, FellowshipView, HouseLineColor,
    HouseView,
};
use ac_runtime::inventory::{
    AppraisalView, HousePurchaseView, InteractionMode, PendingPlacement, StagedRow, TradeView,
    VendorListsView, VendorView, VendorViewReason,
};
use ac_runtime::session::creation::CreationAppearance;
use ac_runtime::session::interface::{
    ChatEntryView, ContractFacts, InterfaceEvent, InterfaceState, ItemsUpdate, PlayerFacts,
    SelectedHealth, SelectedMana, SocialFacts, TitleFacts,
};

use crate::codec::{
    DecodeContext, DecodeError, EncodeContext, EncodeError, Reader, Shape, Wire, Writer, record,
    tuple_record, union,
};

/// A type with private fields goes out as its owning crate's view of every
/// field (`Parts`), whose conversions name every field.
macro_rules! via_parts {
    ($($name:ident => $parts:ident),* $(,)?) => {$(
        impl Wire for $name {
            const SHAPE: u64 = <$parts as Wire>::SHAPE;
            const MIN_SIZE: usize = <$parts as Wire>::MIN_SIZE;
            fn encode(&self, w: &mut Writer, cx: &EncodeContext) -> Result<(), EncodeError> {
                $parts::from(self.clone()).encode(w, cx)
            }
            fn decode(r: &mut Reader<'_>, cx: &DecodeContext) -> Result<Self, DecodeError> {
                Ok(<$parts as Wire>::decode(r, cx)?.into())
            }
        }
    )*};
}

via_parts! {
    ClientObject => ClientObjectParts,
    ShortcutStore => ShortcutStoreParts,
    FriendsState => FriendsStateParts,
    SquelchState => SquelchStateParts,
    CharacterOptions => CharacterOptionsParts,
    ChessBoard => ChessBoardParts,
}

tuple_record! {
    ObjectIdentity(v: u64)
    ItemType(v: u32)
    EquipMask(v: u32)
    ItemUseable(v: u32)
    PublicWeenieFlags(v: u32)
    CombatMode(v: u32)
}

record! {
    PropertyBundle {
        ints: BTreeMap<u32, i32>,
        int64s: BTreeMap<u32, i64>,
        bools: BTreeMap<u32, bool>,
        floats: BTreeMap<u32, f64>,
        strings: BTreeMap<u32, String>,
        data_ids: BTreeMap<u32, u32>,
        instance_ids: BTreeMap<u32, u32>,
    }
    ClientWeaponProfile {
        damage_type: u32,
        weapon_time: u32,
        weapon_skill: u32,
        damage: u32,
        damage_variance: f64,
        damage_mod: f64,
        weapon_length: f64,
        max_velocity: f64,
        weapon_offense: f64,
        max_velocity_estimated: u32,
    }
    ClientCreatureAttributes {
        strength: u32,
        endurance: u32,
        quickness: u32,
        coordination: u32,
        focus: u32,
        self_: u32,
        stamina: u32,
        max_stamina: u32,
        mana: u32,
        max_mana: u32,
    }
    ClientCreatureProfile {
        flags: u32,
        health: u32,
        max_health: u32,
        attributes: Option<ClientCreatureAttributes>,
        attribute_highlights: u16,
        attribute_colors: u16,
    }
    ClientArmorProfile {
        slashing_protection: f32,
        piercing_protection: f32,
        bludgeoning_protection: f32,
        cold_protection: f32,
        fire_protection: f32,
        acid_protection: f32,
        nether_protection: f32,
        lightning_protection: f32,
    }
    HouseRestrictionRecord {
        open_to_public: bool,
        allegiance_monarch_id: u32,
        guests: BTreeMap<u32, u32>,
    }
    ClientObjectHeader {
        weenie_header_flags: Option<u32>,
        weenie_header_flags2: Option<u32>,
        physics_description_flags: Option<u32>,
        physics_state: Option<u32>,
        object_description_flags: Option<u32>,
        setup_id: Option<u32>,
        scale: Option<f32>,
        hook_type: Option<u32>,
        parent_id: Option<u32>,
        parent_location: Option<u32>,
        use_radius: Option<f32>,
    }
    ClientObjectParts {
        object_id: u32,
        identity: ObjectIdentity,
        weenie_class_id: u32,
        name: String,
        plural_name: String,
        item_type: ItemType,
        valid_locations: EquipMask,
        currently_equipped_location: EquipMask,
        icon_id: u32,
        icon_underlay_id: u32,
        icon_overlay_id: u32,
        effects: u32,
        stack_size: i32,
        stack_size_max: i32,
        burden: i32,
        value: i32,
        container_id: u32,
        container_slot: i32,
        container_type_hint: u32,
        attuned: bool,
        bonded: bool,
        wielder_id: u32,
        items_capacity: i32,
        containers_capacity: i32,
        hook_item_types: u32,
        hook_type: u32,
        priority: u32,
        useability: Option<ItemUseable>,
        target_type: Option<ItemType>,
        public_weenie_bitfield: Option<PublicWeenieFlags>,
        pet_owner_id: u32,
        combat_use: Option<u8>,
        ammo_type: Option<u16>,
        spell_id: Option<u32>,
        appraised_spell_ids: Vec<u32>,
        last_appraisal_time_ms: i32,
        appraisal_answered: bool,
        last_appraisal_unsuccessful: bool,
        last_appraisal_unsuccessful_at_seconds: f64,
        weapon_profile: Option<ClientWeaponProfile>,
        armor_profile: Option<ClientArmorProfile>,
        creature_profile: Option<ClientCreatureProfile>,
        cooldown_id: Option<u32>,
        cooldown_duration: Option<f64>,
        trade_state: i32,
        sell_state: i32,
        is_component_pack: bool,
        radar_blip_color: Option<u8>,
        radar_behavior: Option<u8>,
        structure: i32,
        max_structure: i32,
        workmanship: f32,
        material_type: Option<u32>,
        house_owner_id: Option<u32>,
        monarch_id: Option<u32>,
        restrictions: Option<HouseRestrictionRecord>,
        properties: PropertyBundle,
        header: Option<ClientObjectHeader>,
    }
    ItemsUpdate {
        reset: bool,
        objects: Vec<ClientObject>,
        removed: Vec<u32>,
        contents: Vec<(u32, Option<Vec<u32>>)>,
    }
    VitalSnapshot { ranks: u32, start: u32, xp: u32, current: u32 }
    AttributeSnapshot { ranks: u32, start: u32, xp: u32 }
    SkillSnapshot {
        skill_id: u32,
        ranks: u32,
        status: u32,
        xp: u32,
        init: u32,
        resistance: u32,
        last_used: f64,
        formula_bonus: u32,
    }
    ActiveEnchantmentRecord {
        spell_id: u32,
        layer_id: u32,
        duration: f64,
        caster_guid: u32,
        stat_mod_type: Option<u32>,
        stat_mod_key: Option<u32>,
        stat_mod_value: Option<f32>,
        bucket: u32,
        start_time: f64,
        spell_category: u32,
        power_level: u32,
        degrade_modifier: f32,
        degrade_limit: f32,
        last_time_degraded: f64,
        spell_set_id: Option<u32>,
    }
    PlayerFacts { revision: u64, numbers: LocalPlayerState, session_seconds: f64 }
    PendingPlacement { token: u64, item_id: u32, container_id: u32, placement: i32 }
    ShortcutEntry { index: i32, object_id: u32, spell_id: u32 }
    ShortcutStoreParts { entries: [Option<ShortcutEntry>; 18], revision: u64 }
    SelectedHealth { object_id: u32, percent: Option<f32> }
    SelectedMana { object_id: u32, fraction: f32 }
    InterfaceState {
        player: u32,
        mode: InteractionMode,
        pending_item: Option<u32>,
        pending_placement: Option<PendingPlacement>,
        can_make_request: bool,
        combat_mode: CombatMode,
        busy_count: u32,
        shortcuts: ShortcutStore,
        confirmations: Vec<ClientConfirmation>,
        selected_health: Option<SelectedHealth>,
        selected_mana: Option<SelectedMana>,
        external_container: u32,
        external_container_in_range: bool,
        account: String,
    }
    PropertyTables {
        ints: BTreeMap<u32, i32>,
        int64s: BTreeMap<u32, i64>,
        bools: BTreeMap<u32, bool>,
        floats: BTreeMap<u32, f64>,
        strings: BTreeMap<u32, String>,
        data_ids: BTreeMap<u32, u32>,
        instance_ids: BTreeMap<u32, u32>,
    }
    ArmorProfile {
        slashing: f32,
        piercing: f32,
        bludgeoning: f32,
        cold: f32,
        fire: f32,
        acid: f32,
        nether: f32,
        lightning: f32,
    }
    CreatureAttributes {
        strength: u32,
        endurance: u32,
        quickness: u32,
        coordination: u32,
        focus: u32,
        self_: u32,
        stamina: u32,
        mana: u32,
        stamina_max: u32,
        mana_max: u32,
    }
    CreatureProfile {
        flags: u32,
        health: u32,
        health_max: u32,
        attributes: Option<CreatureAttributes>,
        attribute_highlights: Option<(u16, u16)>,
    }
    WeaponProfile {
        damage_type: u32,
        weapon_time: u32,
        weapon_skill: u32,
        damage: u32,
        damage_variance: f64,
        damage_mod: f64,
        weapon_length: f64,
        max_velocity: f64,
        weapon_offense: f64,
        max_velocity_estimated: u32,
    }
    HookProfile { flags: u32, valid_locations: u32, ammo_type: u32 }
    EnchantmentHighlight { highlight: u16, color: u16 }
    ArmorLevels {
        head: i32,
        chest: i32,
        abdomen: i32,
        upper_arm: i32,
        lower_arm: i32,
        hand: i32,
        upper_leg: i32,
        lower_leg: i32,
        foot: i32,
    }
    IdentifyObjectResponse {
        guid: u32,
        flags: u32,
        success: bool,
        properties: PropertyTables,
        spell_book: Vec<u32>,
        armor_profile: Option<ArmorProfile>,
        creature_profile: Option<CreatureProfile>,
        weapon_profile: Option<WeaponProfile>,
        hook_profile: Option<HookProfile>,
        armor_enchantments: Option<EnchantmentHighlight>,
        weapon_enchantments: Option<EnchantmentHighlight>,
        resist_enchantments: Option<EnchantmentHighlight>,
        armor_levels: Option<ArmorLevels>,
    }
    AppraisalView {
        name: Option<String>,
        response: IdentifyObjectResponse,
        present_in_ui: bool,
        opens_window: bool,
    }
    TradeView {
        open: bool,
        partner_guid: u32,
        self_accepted: bool,
        partner_accepted: bool,
        self_items: Vec<u32>,
        partner_items: Vec<u32>,
        last_failure_item: u32,
        last_failure_reason: u32,
        revision: u64,
        partner_name: Option<String>,
        item_names: BTreeMap<u32, String>,
    }
    VendorShopProfile {
        merchandise_item_types: u32,
        merchandise_min_value: u32,
        merchandise_max_value: u32,
        deal_magical_items: bool,
        buy_price: f32,
        sell_price: f32,
        alternate_currency_wcid: u32,
        alternate_currency_amount: u32,
        alternate_currency_plural_name: String,
    }
    VendorShopItem {
        item_guid: u32,
        stack_size: i32,
        weenie_class_id: u32,
        name: Option<String>,
        item_type: Option<u32>,
        icon_id: u32,
        value: Option<i32>,
        desc_stack_size: Option<i32>,
        max_stack_size: Option<i32>,
        icon_underlay_id: u32,
        icon_overlay_id: u32,
        effects: u32,
        plural_name: Option<String>,
        valid_locations: Option<u32>,
        priority: Option<u32>,
        items_capacity: Option<i32>,
        containers_capacity: Option<i32>,
        structure: Option<i32>,
        max_structure: Option<i32>,
        workmanship: Option<f32>,
        burden: Option<i32>,
        material_type: Option<u32>,
        target_type: Option<u32>,
        combat_use: Option<u8>,
        ammo_type: Option<u16>,
        public_weenie_bitfield: Option<u32>,
        useability: Option<u32>,
        hook_item_types: Option<u32>,
        hook_type: Option<u32>,
    }
    StagedRow { item_guid: u32, name: String, quantity: i32, price: i32 }
    VendorListsView {
        buying: Vec<StagedRow>,
        selling: Vec<StagedRow>,
        buying_text: String,
        selling_text: String,
        purse_text: String,
        money: i32,
    }
    VendorView {
        vendor_guid: u32,
        name: Option<String>,
        profile: VendorShopProfile,
        items: Vec<VendorShopItem>,
        lists: VendorListsView,
        reason: VendorViewReason,
    }
    Shortcut { index: i32, object_id: u32, spell_id: u32 }
    DesiredComponent { id: u32, amount: u32 }
    CharacterOptionsParts {
        options1: u32,
        options2: u32,
        revision: u64,
        dirty: bool,
        first_dirtied_at: Option<std::time::Duration>,
        has_server_seed: bool,
        echo_shortcuts: Vec<Shortcut>,
        echo_favorite_spells: [Vec<u32>; 8],
        echo_desired_components: Vec<DesiredComponent>,
        echo_spellbook_filters: u32,
    }
    FriendEntry {
        id: u32,
        name: String,
        online: bool,
        appear_offline: bool,
        friends: Vec<u32>,
        friend_of: Vec<u32>,
    }
    FriendsStateParts { entries: Vec<FriendEntry>, revision: u64 }
    SquelchInfo { name: String, account_wide: bool, message_types: HashSet<u32> }
    SquelchDatabase {
        accounts: HashMap<String, u32>,
        characters: HashMap<u32, SquelchInfo>,
        global: SquelchInfo,
    }
    SquelchStateParts { database: SquelchDatabase, revision: u64 }
    FellowView {
        guid: u32,
        name: String,
        level: u32,
        health: (u32, u32),
        stamina: (u32, u32),
        mana: (u32, u32),
        share_loot: bool,
        vitals_age_seconds: Option<f64>,
    }
    FellowshipView {
        revision: u64,
        in_fellowship: bool,
        name: String,
        leader_guid: u32,
        share_xp: bool,
        even_xp_split: bool,
        open: bool,
        locked: bool,
        members: Vec<FellowView>,
    }
    AllegianceMember {
        character_id: u32,
        parent_guid: u32,
        is_logged_in: bool,
        name: String,
        rank: u16,
        level: u32,
        loyalty: u16,
        leadership: u16,
        cp_cached: u32,
        cp_tithed: u32,
        gender: u8,
        heritage_group: u8,
        may_passup_experience: bool,
    }
    AllegianceProfile {
        total_members: u32,
        total_vassals: u32,
        record_count: u16,
        old_version: u16,
        motd: String,
        motd_set_by: String,
        chat_room_id: u32,
        allegiance_name: String,
        name_last_set_time: u32,
        is_locked: bool,
        approved_vassal: u32,
        monarch: Option<AllegianceMember>,
        records: Vec<AllegianceMember>,
    }
    SocialFacts {
        in_world: bool,
        options: CharacterOptions,
        friends: FriendsState,
        squelch: SquelchState,
        fellowship: FellowshipView,
        allegiance: Option<AllegianceProfile>,
        allegiance_rank: u32,
        allegiance_revision: u64,
    }
    ContractTracker {
        version: u32,
        contract_id: u32,
        stage: u32,
        time_when_done: f64,
        time_when_repeats: f64,
    }
    TrackedContract { tracker: ContractTracker, received_unix_ms: i64 }
    ContractEntry {
        version: u32,
        contract_id: u32,
        contract_name: String,
        description: String,
        description_progress: String,
        name_npc_start: String,
        name_npc_end: String,
        questflag_stamped: String,
        questflag_started: String,
        questflag_finished: String,
        questflag_progress: String,
        questflag_timer: String,
        questflag_repeat_time: String,
        location_npc_start_cell: u32,
        location_npc_end_cell: u32,
        location_quest_area_cell: u32,
    }
    ContractFacts {
        contracts: Vec<TrackedContract>,
        display_contract_id: u32,
        revision: u64,
        catalog: Arc<ContractCatalog>,
    }
    BookPage {
        author_id: u32,
        author_name: String,
        author_account: String,
        text_included: u32,
        ignore_author: u32,
        page_text: String,
    }
    BookView {
        revision: u64,
        book_guid: u32,
        max_num_pages: i32,
        max_num_chars_per_page: i32,
        pages: Vec<BookPage>,
        current_page: i32,
        inscription: String,
        scribe_id: u32,
        scribe_name: String,
        request_pending: bool,
    }
    HouseView {
        revision: u64,
        has_received_notice: bool,
        owns_house: bool,
        lines: Vec<(String, HouseLineColor)>,
        position_cell: Option<u32>,
    }
    HousePayment { num: i32, paid: i32, weenie_id: u32, name: String, plural_name: String }
    HouseProfile {
        slumlord_guid: u32,
        dwelling_id: u32,
        owner_guid: u32,
        bitmask: u32,
        min_level: i32,
        max_level: i32,
        min_allegiance_rank: i32,
        max_allegiance_rank: i32,
        maintenance_free: bool,
        house_type: u32,
        owner_name: String,
        buy: Vec<HousePayment>,
        rent: Vec<HousePayment>,
    }
    HousePurchaseView {
        revision: u64,
        profile: Option<HouseProfile>,
        buy: Vec<u32>,
        rent: Vec<u32>,
        buy_amounts: Vec<i32>,
        rent_amounts: Vec<i32>,
    }
    JournalPage {
        label: String,
        title: String,
        notes: String,
        timer_days: i32,
        timer_hours: i32,
        timer_minutes: i32,
        location_x: f32,
        location_y: f32,
        has_location: bool,
        running_timer_seconds: f64,
    }
    JournalView {
        pages: Vec<JournalPage>,
        current_page: usize,
        revision: u64,
        timer_started_unix_ms: Option<i64>,
        timer_seconds_at_start: f64,
    }
    ChessSquare { x: i32, y: i32 }
    ChessPiece { piece_type: ChessPieceType, team: ChessTeam, has_moved: bool }
    ChessBoardParts {
        squares: [Option<ChessPiece>; 64],
        en_passant_target: Option<ChessSquare>,
        en_passant_victim: ChessSquare,
        turn: ChessTeam,
    }
    ChessView {
        revision: u64,
        stage: ChessStage,
        board_guid: u32,
        team: Option<ChessTeam>,
        offering_stalemate: bool,
        games_ended: u64,
        board: ChessBoard,
    }
    CreationAppearance {
        eyes_strip: u32,
        nose_strip: u32,
        mouth_strip: u32,
        hair_style: u32,
        hair_color: u32,
        eye_color: u32,
        headgear_style: u32,
        headgear_color: u32,
        shirt_style: u32,
        shirt_color: u32,
        trousers_style: u32,
        trousers_color: u32,
        footwear_style: u32,
        footwear_color: u32,
        skin_shade: f64,
        hair_shade: f64,
        headgear_shade: f64,
        shirt_shade: f64,
        trousers_shade: f64,
        footwear_shade: f64,
    }
    BarberView {
        revision: u64,
        openings: u64,
        open: bool,
        heritage: u32,
        gender: u32,
        appearance: CreationAppearance,
        toggle: Option<BarberToggle>,
        toggle_on: bool,
        preview_setup: u32,
    }
    ItemManaChange { item_id: u32, fraction: f32, valid: bool }
    TitleFacts {
        display_title_id: u32,
        title_ids: Vec<u32>,
        revision: u64,
        table_generation: u64,
    }
    ChatEntryView {
        draft: String,
        shown_revision: u64,
        composed: bool,
        channel: ChatChannelKind,
        tell_target: Option<String>,
        tell_target_guid: u32,
        last_incoming_tell_sender: Option<String>,
        last_monarch_sender: Option<String>,
        last_patron_sender: Option<String>,
    }
}

union! {
    InteractionMode: "interaction mode" {
        0 => None,
        1 => Use,
        2 => Examine,
        3 => UseItemOnTarget(item: u32),
    }
    ItemUseConfirmation: "item use confirmation" {
        0 => PlayerKillerSwitch,
        1 => NonPlayerKillerSwitch,
        2 => VolatileRare,
    }
    ClientConfirmation: "client confirmation" {
        0 => Suicide,
        1 => AbandonHouse,
        2 => AbandonHouseCertain,
        3 => ItemUse { kind: ItemUseConfirmation, object_id: u32 },
        4 => ManaStoneDrain { stone: u32, target: u32, message: String },
        5 => Server { confirmation_type: u32, context_id: u32, message: String },
    }
    VendorViewReason: "vendor view reason" {
        0 => Opened,
        1 => Refreshed,
        2 => Lists,
        3 => ComponentsFilled,
        4 => Asked,
    }
    HouseLineColor: "house line colour" {
        0 => Normal,
        1 => RentPaid,
        2 => RentNotPaid,
    }
    ChessStage: "chess stage" {
        0 => NotPlaying,
        1 => Joining,
        2 => WaitingForStart,
        3 => MyTurn,
        4 => MoveSent,
        5 => OpponentsTurn,
    }
    ChessTeam: "chess team" {
        0 => White,
        1 => Black,
    }
    ChessPieceType: "chess piece" {
        1 => Pawn,
        2 => Rook,
        3 => Knight,
        4 => Bishop,
        5 => Queen,
        6 => King,
    }
    BarberToggle: "barber toggle" {
        0 => EmpyreanEarthbound,
        1 => UndeadNoFlame,
        2 => ShadowNoCrown,
    }
    ChatChannelKind: "chat channel" {
        0 => Say,
        1 => Tell,
        2 => Fellowship,
        3 => Allegiance,
        4 => Vassals,
        5 => Patron,
        6 => Monarch,
        7 => CoVassals,
        8 => General,
        9 => Trade,
        10 => Lfg,
        11 => Roleplay,
        12 => Society,
        13 => Olthoi,
        14 => AllegianceBroadcast,
        15 => Unknown,
    }
    InterfaceEvent: "interface event" {
        0 => Items(update: ItemsUpdate),
        1 => Player(facts: Box<PlayerFacts>),
        2 => State(state: Box<InterfaceState>),
        3 => Appraisal(view: Box<AppraisalView>),
        4 => Trade(view: Box<TradeView>),
        5 => Vendor(view: Option<Box<VendorView>>),
        6 => Social(facts: Box<SocialFacts>),
        7 => Contracts(facts: Box<ContractFacts>),
        8 => Book(view: Box<BookView>),
        9 => House(view: Box<HouseView>),
        10 => HousePurchase(view: Box<HousePurchaseView>),
        11 => Journal(view: Box<JournalView>),
        12 => OpenSalvage(tool: u32),
        13 => Chess(view: Box<ChessView>),
        14 => Barber(view: Box<BarberView>),
        15 => BeginGame(board: u32),
        16 => OpenContainedContainer(pack: u32),
        17 => ItemMana(change: ItemManaChange),
        18 => PluginUi(changes: Vec<ac_plugin_ui::UiChange>),
        19 => Titles(facts: Box<TitleFacts>),
        20 => ChatEntry(view: Box<ChatEntryView>),
        21 => Selection(selected: Option<u32>),
    }
}

/// What crosses of the player's numbers: every field but the function that
/// recomputes skill formula terms, which the receiver supplies
/// ([`DecodeContext::skill_formula_bonus_resolver`]). One definition gives
/// the layout, the shape and the types read.
struct PlayerNumbersOnWire {
    health: Option<VitalSnapshot>,
    stamina: Option<VitalSnapshot>,
    mana: Option<VitalSnapshot>,
    attributes: [Option<AttributeSnapshot>; 6],
    skills: BTreeMap<u32, SkillSnapshot>,
    int_properties: BTreeMap<u32, i32>,
    spellbook: Option<Spellbook>,
}

/// What crosses of a spellbook: every field but its spell table, which the
/// receiver supplies ([`DecodeContext::spell_table`]).
struct SpellbookOnWire {
    learned: BTreeMap<u32, f32>,
    active: Vec<ActiveEnchantmentRecord>,
    order_by_bucket: BTreeMap<u32, Vec<u32>>,
    favorites: [Vec<u32>; FAVORITE_BAR_COUNT],
    desired_components: BTreeMap<u32, u32>,
    metadata_installed: bool,
    filters: u32,
    spellbook_revision: u64,
    enchantment_revision: u64,
    desired_components_revision: u64,
}

record! {
    PlayerNumbersOnWire {
        health: Option<VitalSnapshot>,
        stamina: Option<VitalSnapshot>,
        mana: Option<VitalSnapshot>,
        attributes: [Option<AttributeSnapshot>; 6],
        skills: BTreeMap<u32, SkillSnapshot>,
        int_properties: BTreeMap<u32, i32>,
        spellbook: Option<Spellbook>,
    }
    SpellbookOnWire {
        learned: BTreeMap<u32, f32>,
        active: Vec<ActiveEnchantmentRecord>,
        order_by_bucket: BTreeMap<u32, Vec<u32>>,
        favorites: [Vec<u32>; FAVORITE_BAR_COUNT],
        desired_components: BTreeMap<u32, u32>,
        metadata_installed: bool,
        filters: u32,
        spellbook_revision: u64,
        enchantment_revision: u64,
        desired_components_revision: u64,
    }
}

/// The player's numbers, with the receiver's skill formulas.
impl Wire for LocalPlayerState {
    const SHAPE: u64 = <PlayerNumbersOnWire as Wire>::SHAPE;
    fn encode(&self, w: &mut Writer, cx: &EncodeContext) -> Result<(), EncodeError> {
        let LocalPlayerStateParts {
            health,
            stamina,
            mana,
            attributes,
            skills,
            int_properties,
            spellbook,
            skill_formula_bonus_resolver: _,
        } = LocalPlayerStateParts::from(self.clone());
        PlayerNumbersOnWire {
            health,
            stamina,
            mana,
            attributes,
            skills,
            int_properties,
            spellbook,
        }
        .encode(w, cx)
    }
    fn decode(r: &mut Reader<'_>, cx: &DecodeContext) -> Result<Self, DecodeError> {
        let PlayerNumbersOnWire {
            health,
            stamina,
            mana,
            attributes,
            skills,
            int_properties,
            spellbook,
        } = PlayerNumbersOnWire::decode(r, cx)?;
        Ok(LocalPlayerStateParts {
            health,
            stamina,
            mana,
            attributes,
            skills,
            int_properties,
            spellbook,
            skill_formula_bonus_resolver: cx.skill_formula_bonus_resolver.clone(),
        }
        .into())
    }
}

/// A spellbook, with the receiver's spell table. A book the sender had a
/// table for is installed with the receiver's when it has one; otherwise
/// it has none installed (and an empty one), so the receiver can install
/// its own later instead of being refused.
impl Wire for Spellbook {
    const SHAPE: u64 = <SpellbookOnWire as Wire>::SHAPE;
    fn encode(&self, w: &mut Writer, cx: &EncodeContext) -> Result<(), EncodeError> {
        let SpellbookParts {
            learned,
            active,
            order_by_bucket,
            favorites,
            desired_components,
            table: _,
            metadata_installed,
            filters,
            spellbook_revision,
            enchantment_revision,
            desired_components_revision,
        } = SpellbookParts::from(self.clone());
        SpellbookOnWire {
            learned,
            active,
            order_by_bucket,
            favorites,
            desired_components,
            metadata_installed,
            filters,
            spellbook_revision,
            enchantment_revision,
            desired_components_revision,
        }
        .encode(w, cx)
    }
    fn decode(r: &mut Reader<'_>, cx: &DecodeContext) -> Result<Self, DecodeError> {
        let SpellbookOnWire {
            learned,
            active,
            order_by_bucket,
            favorites,
            desired_components,
            metadata_installed,
            filters,
            spellbook_revision,
            enchantment_revision,
            desired_components_revision,
        } = SpellbookOnWire::decode(r, cx)?;
        let table = cx.spell_table.as_ref().filter(|_| metadata_installed);
        Ok(SpellbookParts {
            learned,
            active,
            order_by_bucket,
            favorites,
            desired_components,
            table: table.map_or_else(Arc::default, Arc::clone),
            metadata_installed: table.is_some(),
            filters,
            spellbook_revision,
            enchantment_revision,
            desired_components_revision,
        }
        .into())
    }
}
/// The contract catalog is its one table of entries.
impl Wire for ContractCatalog {
    const SHAPE: u64 = Shape::new("record")
        .name("contracts")
        .of(<HashMap<u32, ContractEntry> as Wire>::SHAPE)
        .finish();
    const MIN_SIZE: usize = 4;
    fn encode(&self, w: &mut Writer, cx: &EncodeContext) -> Result<(), EncodeError> {
        <HashMap<u32, ContractEntry> as Wire>::encode(self.contracts(), w, cx)
    }
    fn decode(r: &mut Reader<'_>, cx: &DecodeContext) -> Result<Self, DecodeError> {
        Ok(ContractCatalog::new(
            <HashMap<u32, ContractEntry> as Wire>::decode(r, cx)?,
        ))
    }
}
