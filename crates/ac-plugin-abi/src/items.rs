//! Items, equipment and loot (API group G): what the character owns, the
//! item commands and their answers, the corpses nearby, and the loot
//! classifiers plugins share.

use crate::world::{ItemProperties, ObjectClass, Position};
use crate::{wire_enum, wire_record};

wire_record! {
    /// A palette entry of an item's look.
    pub struct Palette {
        /// The palette.
        pub palette_id: u32,
        /// Where it starts in the base palette.
        pub offset: u8,
        /// How many entries.
        pub length: u8,
        /// A representative colour: red.
        pub red: u8,
        /// Green.
        pub green: u8,
        /// Blue.
        pub blue: u8,
    }
}

wire_record! {
    /// One item the character owns (or one in an open container), with the
    /// numbers the automation reads. The server lists a container's
    /// contents before it describes them (every side pack at login), so an
    /// item may be known only by its id and place until its description
    /// arrives: an empty name, zeroed numbers, class `Unknown` and no
    /// `header`. The same item, with the same id, is described later.
    pub struct InventoryItem {
        /// Its id.
        pub object_id: u32,
        /// Its weenie class.
        pub weenie_class_id: u32,
        /// Its name.
        pub name: String,
        /// Item type bits.
        pub item_type: u32,
        /// The container holding it, 0 for none.
        pub container_id: u32,
        /// The creature wielding it, 0 for none.
        pub wielder_id: u32,
        /// Where it may be worn or wielded (bits).
        pub valid_locations: u32,
        /// Where it is worn or wielded now (bits), 0 when not.
        pub equipped_location: u32,
        /// How it is used.
        pub useability: u32,
        /// What it is used on (item type bits).
        pub target_type: u32,
        /// Public weenie flags.
        pub public_flags: u32,
        /// How many in its stack.
        pub stack_size: i32,
        /// Uses left.
        pub structure: i32,
        /// Uses at full.
        pub maximum_structure: i32,
        /// The spell it casts when used.
        pub spell_id: u32,
        /// A pet device's pet class, 0 for none.
        pub pet_class: i32,
        /// The summoning mastery it needs.
        pub summoning_mastery: i32,
        /// The shared cooldown it belongs to: using it starts that cooldown
        /// for every item with the same id. The server sends it with the
        /// item and again in an appraisal; 0 when it has none.
        pub shared_cooldown_id: u32,
        /// How long, seconds, its shared cooldown lasts once it is used; 0
        /// when the server sent no length.
        pub cooldown_seconds: f64,
        /// Its cast-on-strike spell.
        pub proc_spell_id: u32,
        /// The cast-on-strike spell targets the wielder.
        pub proc_spell_self_targeted: bool,
        /// How often it casts on strike.
        pub proc_spell_rate: f64,
        /// The weapon skill it uses.
        pub weapon_skill: i32,
        /// Damage type bits.
        pub damage_type: i32,
        /// Maximum damage.
        pub damage: i32,
        /// Damage variance.
        pub damage_variance: f64,
        /// The skill its use requires.
        pub use_requires_skill: i32,
        /// The level of that skill.
        pub use_requires_skill_level: i32,
        /// The skill it requires specialized.
        pub use_requires_skill_specialized: i32,
        /// Combat use (melee, missile, ammo, shield, two-handed).
        pub combat_use: i32,
        /// Item spellcraft.
        pub item_spellcraft: i32,
        /// Wield requirement kind.
        pub wield_requirements: i32,
        /// Wield requirement skill.
        pub wield_skill_type: i32,
        /// Wield requirement level.
        pub wield_difficulty: i32,
        /// Attack type bits.
        pub attack_type: i32,
        /// Weapon type.
        pub weapon_type: i32,
        /// Ammunition type.
        pub ammo_type: u32,
        /// The vital a consumable boosts.
        pub booster_vital: i32,
        /// How much it boosts it.
        pub boost_value: i32,
        /// A healing kit's modifier.
        pub heal_kit_modifier: f64,
        /// The spells its appraisal listed.
        pub appraised_spell_ids: Vec<u32>,
        /// Gear damage rating.
        pub gear_damage: i32,
        /// Gear damage resistance rating.
        pub gear_damage_resistance: i32,
        /// Gear critical rating.
        pub gear_critical_chance: i32,
        /// Gear critical resistance rating.
        pub gear_critical_resistance: i32,
        /// Gear critical damage rating.
        pub gear_critical_damage: i32,
        /// Gear critical damage resistance rating.
        pub gear_critical_damage_resistance: i32,
        /// The largest stack it makes.
        pub maximum_stack_size: i32,
        /// Its place in its container's contents, counted among its own
        /// kind: a loose item's index among the container's loose items, a
        /// pack's (or spell focus's) among its packs, both from 0 in the
        /// order the container lists them. The same for the main pack, a
        /// side pack or an open chest. -1 when it is in no container
        /// (worn or wielded, on the ground) or its container's list does
        /// not hold it; 0 when its container has no list at all (never
        /// listed, or its view closed).
        pub container_slot: i32,
        /// Items a container holds.
        pub items_capacity: i32,
        /// Packs a container holds.
        pub containers_capacity: i32,
        /// Burden.
        pub burden: i32,
        /// Value.
        pub value: i32,
        /// Mana now.
        pub item_current_mana: i32,
        /// Mana at full.
        pub item_maximum_mana: i32,
        /// Workmanship.
        pub workmanship: f32,
        /// Salvage workmanship.
        pub salvage_workmanship: f64,
        /// Times tinkered.
        pub num_times_tinkered: i32,
        /// Imbued effect bits.
        pub imbued_effect: i32,
        /// Armor level.
        pub armor_level: i32,
        /// Maximum damage (tinker value).
        pub max_damage: i32,
        /// A wand's elemental damage type.
        pub wand_elemental_damage_type: i32,
        /// It is retained.
        pub retained: bool,
        /// Its material.
        pub material_type: u32,
        /// What kind of thing it is.
        pub object_class: ObjectClass,
        /// Its look's palettes.
        pub palettes: Vec<Palette>,
        /// Its icon.
        pub icon_id: u32,
        /// Icon effect bits.
        pub effects: u32,
        /// The icon drawn beneath its icon (a full icon id, such as a rare
        /// item's backdrop); 0 for none.
        pub icon_underlay_id: u32,
        /// The icon drawn over its icon (such as an imbue's mark); 0 for
        /// none.
        pub icon_overlay_id: u32,
        /// The body parts a worn piece covers (the coverage bits sent with
        /// the object); 0 for anything not worn.
        pub coverage_mask: u32,
        /// Its name for more than one; empty when not sent.
        pub plural_name: String,
        /// How close, metres, the character must be to use it; 0 when not
        /// sent.
        pub use_radius: f32,
        /// Its latest full description's layout and optional values, as
        /// sent; none before one arrived.
        pub header: Option<ObjectHeader>,
        /// The kinds of item a house hook will take, as the item-type bits
        /// the server sends with the object; 0 for anything that is not a
        /// hook, and for a hook the server sent none for. The original
        /// client's macro tools read this as the hook mask. Added in API
        /// 1.2: a plugin built for an older version is sent the record
        /// without it.
        since (1, 2) pub hook_item_types: u32,
    }
}

impl InventoryItem {
    /// The shared cooldown every summoning essence belongs to in the game's
    /// item data: using any one of them starts it for all of them. The
    /// time left on it is the `spells_cooldown` import of this id (the
    /// SDK's `cooldown_remaining`).
    pub const SUMMONING_COOLDOWN_ID: u32 = 213;

    /// True when the item summons a pet: it belongs to the summoning
    /// essences' shared cooldown ([`Self::SUMMONING_COOLDOWN_ID`]), which
    /// the server sends with the item itself, or the server named the pet
    /// it summons (`pet_class`).
    #[must_use]
    pub fn is_pet_device(&self) -> bool {
        self.shared_cooldown_id == Self::SUMMONING_COOLDOWN_ID || self.pet_class != 0
    }
}

wire_record! {
    /// How the server laid out an object's latest full description, and
    /// the optional values it carried, each exactly as sent (none when the
    /// description did not carry it, so a value left out differs from one
    /// sent as zero). For a plugin that reproduces another tool's record of
    /// an object; the rest of the API reports the same things plainly.
    pub struct ObjectHeader {
        /// Which optional item values follow.
        pub weenie_header_flags: Option<u32>,
        /// The second such word.
        pub weenie_header_flags2: Option<u32>,
        /// Which optional physics values follow.
        pub physics_description_flags: Option<u32>,
        /// The physics state word.
        pub physics_state: Option<u32>,
        /// The object's description bit field.
        pub object_description_flags: Option<u32>,
        /// The physics setup file.
        pub setup_id: Option<u32>,
        /// The scale, a multiplier of its natural size.
        pub scale: Option<f32>,
        /// What kind of house hook it hangs on.
        pub hook_type: Option<u32>,
        /// The object it is attached to.
        pub parent_id: Option<u32>,
        /// Where on that parent.
        pub parent_location: Option<u32>,
        /// How close, metres, a user has to be.
        pub use_radius: Option<f32>,
    }
}

wire_record! {
    /// The end of an item use a plugin started
    /// ([`crate::Event::ItemUseCompleted`]).
    pub struct ItemUseCompletion {
        /// Counts completions; 0 for none yet.
        pub revision: i64,
        /// The item used.
        pub source_id: u32,
        /// What it was used on, 0 for nothing.
        pub target_id: u32,
        /// The server's error code, 0 for none.
        pub weenie_error: u32,
    }
}

wire_enum! {
    /// The answer to an item command.
    pub enum ItemStatus: "item status" {
        /// Not in the world.
        Unavailable = 0,
        /// The item is not one the command can act on.
        InvalidItem = 1,
        /// The target is not one the command can act on.
        InvalidTarget = 2,
        /// Another inventory request is in flight; try later.
        Busy = 3,
        /// Sent.
        Started = 4,
        /// The client declined.
        Refused = 5,
        /// Carried out by the client alone and already in effect; nothing
        /// was sent.
        Completed = 6,
    }
}

wire_record! {
    /// The answer to an item command.
    pub struct ItemResult {
        /// The verdict.
        pub status: ItemStatus,
        /// Why, in words.
        pub notice: Option<String>,
    }
}

impl ItemResult {
    /// An answer with no words.
    #[must_use]
    pub fn of(status: ItemStatus) -> Self {
        Self {
            status,
            notice: None,
        }
    }

    /// An answer with a reason.
    #[must_use]
    pub fn because(status: ItemStatus, notice: &str) -> Self {
        Self {
            status,
            notice: Some(notice.to_owned()),
        }
    }

    /// Sent.
    #[must_use]
    pub fn accepted(&self) -> bool {
        self.status == ItemStatus::Started
    }
}

wire_enum! {
    /// The kind of an inventory request.
    pub enum InventoryCommandKind: "inventory command kind" {
        /// Unknown.
        Unknown = 0,
        /// Pick up.
        Pickup = 1,
        /// Put into a container.
        PutInContainer = 2,
        /// Split into a container.
        SplitToContainer = 3,
        /// Merge stacks.
        Merge = 4,
        /// Move.
        Move = 5,
        /// Drop on the ground.
        DropToWorld = 6,
        /// Split onto the ground.
        SplitToWorld = 7,
        /// Wield.
        Wield = 8,
        /// Give.
        Give = 9,
    }
}

wire_record! {
    /// The end of an inventory request.
    pub struct InventoryCompletion {
        /// Counts completions; 0 for none yet.
        pub revision: i64,
        /// What it was.
        pub kind: InventoryCommandKind,
        /// The item.
        pub source_id: u32,
        /// The server's error code, 0 for none.
        pub weenie_error: u32,
    }
}

wire_record! {
    /// Where the item commands stand.
    pub struct ItemState {
        /// The character is in the world.
        pub is_available: bool,
        /// A command now would be answered busy.
        pub is_busy: bool,
        /// Pets of the character's out in the world.
        pub active_owned_pet_count: i32,
        /// The vendor whose shop is open, 0 for none.
        pub active_vendor_id: u32,
        /// The last use a plugin started that ended.
        pub last_completion: ItemUseCompletion,
        /// The last inventory request that ended.
        pub last_inventory_completion: InventoryCompletion,
    }
}

wire_record! {
    /// Put an item into a container ([`crate::Import::ItemsMove`]).
    pub struct MoveRequest {
        /// The item.
        pub object_id: u32,
        /// The container (the character for the main pack).
        pub container_id: u32,
        /// How many of a stack; 0 for all.
        pub amount: u32,
        /// The slot.
        pub placement: i32,
    }
}

wire_record! {
    /// Move `amount` of one stack onto another.
    pub struct MergeRequest {
        /// The stack taken from.
        pub source_id: u32,
        /// The stack added to.
        pub target_id: u32,
        /// How many; 0 for all that fit.
        pub amount: u32,
    }
}

wire_record! {
    /// An item and how many of its stack (0 for all), with a target where
    /// the command has one.
    pub struct ItemAmount {
        /// The item.
        pub object_id: u32,
        /// The target (a give's recipient), 0 for none.
        pub target_id: u32,
        /// How many; 0 for all.
        pub amount: u32,
    }
}

wire_record! {
    /// Salvage items with a tool.
    pub struct SalvageRequest {
        /// The salvaging tool.
        pub tool_id: u32,
        /// What to salvage.
        pub item_ids: Vec<u32>,
    }
}

wire_record! {
    /// An item the character can equip.
    pub struct EquipmentItem {
        /// Its id.
        pub object_id: u32,
        /// Its name.
        pub name: String,
        /// Item type bits.
        pub item_type: u32,
        /// Where it may go.
        pub valid_locations: u32,
        /// Where it is now, 0 when not equipped.
        pub equipped_location: u32,
        /// Its container.
        pub container_id: u32,
        /// Its wielder.
        pub wielder_id: u32,
        /// Combat use.
        pub combat_use: u8,
        /// Damage type bits.
        pub damage_type: i32,
        /// Weapon skill.
        pub weapon_skill: i32,
        /// Maximum damage.
        pub damage: i32,
        /// Damage variance.
        pub damage_variance: f64,
        /// Ammunition type.
        pub ammo_type: u32,
        /// Stack size.
        pub stack_size: i32,
        /// What kind of thing it is.
        pub object_class: ObjectClass,
        /// Weapon type.
        pub weapon_type: i32,
        /// Cleaving.
        pub cleaving: i32,
        /// Imbued effect bits.
        pub imbued_effect: i32,
        /// Resistance cleaving.
        pub resistance_cleaving: i32,
        /// Slayer creature type.
        pub slayer_creature_type: i32,
        /// Crushing blow.
        pub crushing_blow: bool,
        /// Biting strike.
        pub biting_strike: bool,
        /// Armor cleaving.
        pub armor_cleaving: bool,
    }
}

wire_enum! {
    /// The answer to an equip command.
    pub enum EquipmentStatus: "equipment status" {
        /// Not in the world.
        Unavailable = 0,
        /// Not an item the character can equip.
        InvalidItem = 1,
        /// Another request is in flight.
        Busy = 2,
        /// Already where it was asked to go.
        AlreadyEquipped = 3,
        /// Sent.
        Started = 4,
        /// The client declined.
        Refused = 5,
    }
}

wire_record! {
    /// The answer to an equip command.
    pub struct EquipmentResult {
        /// The verdict.
        pub status: EquipmentStatus,
        /// Why, in words.
        pub notice: Option<String>,
    }
}

wire_record! {
    /// Equip an item ([`crate::Import::EquipmentEquip`]).
    pub struct EquipRequest {
        /// The item.
        pub object_id: u32,
        /// The slot bits; 0 to choose.
        pub location: u32,
    }
}

wire_record! {
    /// Where one equipped item went, in the order the world placed them.
    pub struct EquipmentPlacement {
        /// The item.
        pub object_id: u32,
        /// Where it is worn or wielded.
        pub equipped_location: u32,
    }
}

wire_record! {
    /// An item went on or came off ([`crate::Event::EquipmentPlacement`]).
    pub struct EquipmentObservation {
        /// The item.
        pub object_id: u32,
        /// Where (for a removal: where it was).
        pub equipped_location: u32,
        /// It came off.
        pub is_removal: bool,
        /// It was already on when the character entered the world.
        pub is_initial_placement: bool,
    }
}

wire_record! {
    /// A corpse (or outside container) near the character.
    pub struct LootContainer {
        /// Its id.
        pub object_id: u32,
        /// Its weenie class.
        pub weenie_class_id: u32,
        /// Its name.
        pub name: String,
        /// How far, metres.
        pub distance: f32,
        /// It was opened this session.
        pub has_been_opened: bool,
        /// Its opening was asked for.
        pub is_requested: bool,
        /// It is the open container.
        pub is_current: bool,
        /// Its long description.
        pub long_description: String,
        /// A generated rare lies in it.
        pub is_generated_rare: bool,
        /// It was appraised.
        pub is_identified: bool,
        /// The server answered an appraisal of it, successfully or not (a
        /// corpse the server no longer has is answered with nothing and
        /// never becomes identified); always true when identified.
        pub is_appraisal_answered: bool,
        /// Where it lies, when known.
        pub position: Option<Position>,
    }
}

wire_record! {
    /// The one appraisal slot.
    pub struct AppraisalState {
        /// Counts changes.
        pub revision: i64,
        /// The object whose appraisal is awaited, 0 for none.
        pub awaiting_id: u32,
        /// The object shown, 0 for none.
        pub current_id: u32,
        /// The last appraisal given up on, 0 for none.
        pub last_abandoned_id: u32,
        /// The answer that completed [`Self::current_id`] said the server
        /// could not appraise it; false for a successful answer, when
        /// nothing has completed, and when the server has since said
        /// something else about the object. Not proof on its own that the
        /// object is gone.
        pub current_object_unsuccessful: bool,
    }
}

wire_record! {
    /// Where looting stands.
    pub struct LootState {
        /// The character is in the world.
        pub is_available: bool,
        /// A loot command now would be answered busy.
        pub is_busy: bool,
        /// The container asked to open, 0 for none.
        pub requested_container_id: u32,
        /// The open container, 0 for none.
        pub current_container_id: u32,
        /// The open container's contents have arrived.
        pub current_contents_ready: bool,
        /// The last item use that ended.
        pub last_item_use_completion: ItemUseCompletion,
        /// The last inventory request that ended.
        pub last_inventory_completion: InventoryCompletion,
        /// The appraisal slot.
        pub appraisal: AppraisalState,
    }
}

wire_record! {
    /// Pick an item up.
    pub struct PickupRequest {
        /// The item.
        pub object_id: u32,
        /// Name the main pack first.
        pub main_pack: bool,
    }
}

wire_enum! {
    /// What a loot rule says to do with an item.
    pub enum LootAction: "loot action" {
        /// Leave it.
        NoLoot = 0,
        /// Keep it.
        Keep = 1,
        /// Salvage it.
        Salvage = 2,
        /// Sell it.
        Sell = 3,
        /// Read it.
        Read = 4,
        /// User action 1.
        User1 = 5,
        /// User action 2.
        User2 = 6,
        /// User action 3.
        User3 = 7,
        /// User action 4.
        User4 = 8,
        /// User action 5.
        User5 = 9,
        /// Keep up to a count.
        KeepUpTo = 10,
        /// A mana stone.
        ManaStone = 11,
        /// A mana tank.
        ManaTank = 12,
    }
}

wire_record! {
    /// What a classifier looks at.
    pub struct LootContext {
        /// The item.
        pub item: InventoryItem,
        /// Its properties.
        pub properties: ItemProperties,
        /// Everything the character owns.
        pub owned_items: Vec<InventoryItem>,
    }
}

wire_record! {
    /// A classifier's verdict.
    pub struct LootClassification {
        /// A rule matched.
        pub matched: bool,
        /// What to do.
        pub action: LootAction,
        /// The rule's name.
        pub rule_name: String,
        /// Its priority.
        pub priority: i32,
        /// For keep-up-to: how many.
        pub keep_count: i32,
    }
}

wire_record! {
    /// An item was looted under an action.
    pub struct LootedItem {
        /// The item.
        pub item: InventoryItem,
        /// The action.
        pub action: LootAction,
    }
}

wire_record! {
    /// A registered classifier, as other plugins see it.
    pub struct ClassifierInfo {
        /// Its scoped id (`<plugin id>/<id>`).
        pub id: String,
        /// Its name for people.
        pub display_name: String,
    }
}

wire_record! {
    /// Register a loot classifier (the plugin answers it through its
    /// callback export).
    pub struct ClassifierRegistration {
        /// The plugin's own id for it; scoped as `<plugin id>/<id>`.
        pub id: String,
        /// Its name for people.
        pub display_name: String,
    }
}

wire_record! {
    /// Ask a registered classifier (another plugin's or one's own).
    pub struct ClassifyRequest {
        /// The scoped classifier id.
        pub classifier_id: String,
        /// A stored profile to classify with; empty for the current one.
        pub profile: String,
        /// What to classify.
        pub context: LootContext,
    }
}

wire_record! {
    /// Tell a classifier an item was looted.
    pub struct LootedNotice {
        /// The scoped classifier id.
        pub classifier_id: String,
        /// The item.
        pub item: LootedItem,
    }
}

wire_record! {
    /// Tell a classifier an item went away.
    pub struct RemovedNotice {
        /// The scoped classifier id.
        pub classifier_id: String,
        /// The item.
        pub object_id: u32,
    }
}
