//! World reads (API group C): where things are, what the client knows about
//! each object, contracts, the game clock, dungeon floorplans and the
//! projectile path test.

use crate::{wire_enum, wire_record};

wire_record! {
    /// A place in the world as the game's map coordinates name it.
    pub struct Position {
        /// The full cell id (landblock in the high 16 bits).
        pub cell_id: u32,
        /// East (positive) or west of the map's centre, in map units of
        /// 240 metres.
        pub east_west: f64,
        /// North (positive) or south, in map units of 240 metres.
        pub north_south: f64,
        /// Height, in map units of 240 metres.
        pub elevation: f64,
        /// Compass heading, degrees (0 north, 90 east).
        pub heading_degrees: f32,
        /// The cell is on the landscape (cell index 1..=64).
        pub is_outdoor: bool,
    }
}

/// Metres in one map unit.
pub const METRES_PER_MAP_UNIT: f64 = 240.0;

impl Position {
    /// A position from a cell and its landblock-local offset (metres) and
    /// heading: the map coordinates the game shows.
    #[must_use]
    pub fn from_cell(cell_id: u32, local: [f32; 3], heading_degrees: f32) -> Self {
        let block_x = f64::from((cell_id >> 24) & 0xFF);
        let block_y = f64::from((cell_id >> 16) & 0xFF);
        Self {
            cell_id,
            east_west: ((block_x - 127.0) * 192.0 + f64::from(local[0]) - 84.0)
                / METRES_PER_MAP_UNIT,
            north_south: ((block_y - 127.0) * 192.0 + f64::from(local[1]) - 84.0)
                / METRES_PER_MAP_UNIT,
            elevation: f64::from(local[2]) / METRES_PER_MAP_UNIT,
            heading_degrees,
            is_outdoor: (1..=0x40).contains(&(cell_id & 0xFFFF)),
        }
    }

    /// The landblock-local offset, metres (the inverse of
    /// [`Self::from_cell`]).
    #[must_use]
    #[allow(clippy::cast_possible_truncation)]
    pub fn landblock_local(&self) -> [f32; 3] {
        let block_x = f64::from((self.cell_id >> 24) & 0xFF);
        let block_y = f64::from((self.cell_id >> 16) & 0xFF);
        [
            (self.east_west * METRES_PER_MAP_UNIT + 84.0 - (block_x - 127.0) * 192.0) as f32,
            (self.north_south * METRES_PER_MAP_UNIT + 84.0 - (block_y - 127.0) * 192.0) as f32,
            (self.elevation * METRES_PER_MAP_UNIT) as f32,
        ]
    }

    /// The distance to `other` across the ground, metres.
    #[must_use]
    pub fn horizontal_distance_metres(&self, other: &Self) -> f64 {
        let dx = self.east_west - other.east_west;
        let dy = self.north_south - other.north_south;
        (dx * dx + dy * dy).sqrt() * METRES_PER_MAP_UNIT
    }
}

wire_enum! {
    /// What kind of thing an object is, from its item type and public
    /// flags ([`classify`]).
    pub enum ObjectClass: "object class" {
        /// Unknown.
        Unknown = 0,
        /// A melee weapon.
        MeleeWeapon = 1,
        /// Armor.
        Armor = 2,
        /// Clothing.
        Clothing = 3,
        /// Jewelry.
        Jewelry = 4,
        /// A monster.
        Monster = 5,
        /// Food.
        Food = 6,
        /// Money.
        Money = 7,
        /// Miscellaneous.
        Misc = 8,
        /// A missile weapon.
        MissileWeapon = 9,
        /// A container.
        Container = 10,
        /// A gem.
        Gem = 11,
        /// A spell component.
        SpellComponent = 12,
        /// A key.
        Key = 13,
        /// A portal.
        Portal = 14,
        /// A trade note.
        TradeNote = 15,
        /// A mana stone.
        ManaStone = 16,
        /// A plant.
        Plant = 17,
        /// A cooking base.
        BaseCooking = 18,
        /// An alchemy base.
        BaseAlchemy = 19,
        /// A fletching base.
        BaseFletching = 20,
        /// Crafted cooking.
        CraftedCooking = 21,
        /// Crafted alchemy.
        CraftedAlchemy = 22,
        /// Crafted fletching.
        CraftedFletching = 23,
        /// A player.
        Player = 24,
        /// A vendor.
        Vendor = 25,
        /// A door.
        Door = 26,
        /// A corpse.
        Corpse = 27,
        /// A lifestone.
        Lifestone = 28,
        /// A healing kit.
        HealingKit = 29,
        /// A lockpick.
        Lockpick = 30,
        /// A wand, staff or orb.
        WandStaffOrb = 31,
        /// A bundle.
        Bundle = 32,
        /// A book.
        Book = 33,
        /// A journal.
        Journal = 34,
        /// A sign.
        Sign = 35,
        /// Housing.
        Housing = 36,
        /// A non-player creature that is not a monster.
        Npc = 37,
        /// A focus.
        Foci = 38,
        /// Salvage.
        Salvage = 39,
        /// An ust.
        Ust = 40,
        /// Services.
        Services = 41,
        /// A scroll.
        Scroll = 42,
        /// A combat pet.
        CombatPet = 43,
    }
}

/// Object capability bits ([`WorldObject::capabilities`]).
pub mod capabilities {
    /// It can be used or activated.
    pub const INTERACTABLE: u32 = 1 << 0;
    /// A portal.
    pub const PORTAL: u32 = 1 << 1;
    /// A door.
    pub const DOOR: u32 = 1 << 2;
    /// A vendor.
    pub const VENDOR: u32 = 1 << 3;
    /// A container.
    pub const CONTAINER: u32 = 1 << 4;
    /// A player.
    pub const PLAYER: u32 = 1 << 5;
    /// A non-player creature that is not a monster.
    pub const NPC: u32 = 1 << 6;
}

/// The object class an item type and public weenie flags make.
#[must_use]
pub fn classify(item_type: u32, public_flags: u32) -> ObjectClass {
    const BY_TYPE: [(u32, ObjectClass); 28] = [
        (0x0000_0001, ObjectClass::MeleeWeapon),
        (0x0000_0002, ObjectClass::Armor),
        (0x0000_0004, ObjectClass::Clothing),
        (0x0000_0008, ObjectClass::Jewelry),
        (0x0000_0010, ObjectClass::Monster),
        (0x0000_0020, ObjectClass::Food),
        (0x0000_0040, ObjectClass::Money),
        (0x0000_0080, ObjectClass::Misc),
        (0x0000_0100, ObjectClass::MissileWeapon),
        (0x0000_0200, ObjectClass::Container),
        (0x0000_0400, ObjectClass::Bundle),
        (0x0000_0800, ObjectClass::Gem),
        (0x0000_1000, ObjectClass::SpellComponent),
        (0x0000_4000, ObjectClass::Key),
        (0x0000_8000, ObjectClass::WandStaffOrb),
        (0x0001_0000, ObjectClass::Portal),
        (0x0004_0000, ObjectClass::TradeNote),
        (0x0008_0000, ObjectClass::ManaStone),
        (0x0010_0000, ObjectClass::Services),
        (0x0020_0000, ObjectClass::Plant),
        (0x0040_0000, ObjectClass::BaseCooking),
        (0x0080_0000, ObjectClass::BaseAlchemy),
        (0x0100_0000, ObjectClass::BaseFletching),
        (0x0200_0000, ObjectClass::CraftedCooking),
        (0x0400_0000, ObjectClass::CraftedAlchemy),
        (0x0800_0000, ObjectClass::CraftedFletching),
        (0x2000_0000, ObjectClass::Ust),
        (0x4000_0000, ObjectClass::Salvage),
    ];
    const BY_FLAGS: [(u32, ObjectClass); 11] = [
        (0x0000_0008, ObjectClass::Player),
        (0x0000_0200, ObjectClass::Vendor),
        (0x0000_1000, ObjectClass::Door),
        (0x0000_2000, ObjectClass::Corpse),
        (0x0000_4000, ObjectClass::Lifestone),
        (0x0000_8000, ObjectClass::Food),
        (0x0001_0000, ObjectClass::HealingKit),
        (0x0002_0000, ObjectClass::Lockpick),
        (0x0004_0000, ObjectClass::Portal),
        (0x0080_0000, ObjectClass::Foci),
        (0x0000_0001, ObjectClass::Container),
    ];
    let mut result = BY_TYPE
        .iter()
        .find(|(bit, _)| item_type & bit != 0)
        .map_or(ObjectClass::Unknown, |(_, class)| *class);
    if let Some((_, class)) = BY_FLAGS.iter().find(|(bit, _)| public_flags & bit != 0) {
        result = *class;
    }
    if item_type & 0x0000_2000 != 0 && result == ObjectClass::Unknown {
        result = if public_flags & 0x2 != 0 {
            ObjectClass::Journal
        } else if public_flags & 0x4 != 0 {
            ObjectClass::Sign
        } else if public_flags & 0xF != 0 {
            ObjectClass::Book
        } else {
            result
        };
    }
    if result == ObjectClass::Monster && public_flags & 0x10 == 0 {
        result = ObjectClass::Npc;
    }
    if result == ObjectClass::Monster && public_flags & 0x0400_0000 != 0 {
        result = ObjectClass::CombatPet;
    }
    result
}

/// The capability bits ([`capabilities`]) of an object class.
#[must_use]
pub fn capabilities_of(class: ObjectClass) -> u32 {
    use capabilities::{CONTAINER, DOOR, INTERACTABLE, NPC, PLAYER, PORTAL, VENDOR};
    match class {
        ObjectClass::Portal => INTERACTABLE | PORTAL,
        ObjectClass::Door => INTERACTABLE | DOOR,
        ObjectClass::Vendor => INTERACTABLE | VENDOR,
        ObjectClass::Container => INTERACTABLE | CONTAINER,
        ObjectClass::Player => INTERACTABLE | PLAYER,
        ObjectClass::Npc => INTERACTABLE | NPC,
        ObjectClass::Corpse | ObjectClass::Lifestone | ObjectClass::Services => INTERACTABLE,
        _ => 0,
    }
}

wire_record! {
    /// One object the client knows (in the world or in a container).
    pub struct WorldObject {
        /// Its server id.
        pub object_id: u32,
        /// Its weenie class.
        pub weenie_class_id: u32,
        /// Its name.
        pub name: String,
        /// What kind of thing it is.
        pub object_class: ObjectClass,
        /// Its item type bits.
        pub item_type: u32,
        /// The container holding it, 0 for none.
        pub container_id: u32,
        /// The creature wielding it, 0 for none.
        pub wielder_id: u32,
        /// [`capabilities`] bits.
        pub capabilities: u32,
        /// The player carries or wields it (directly or in a pack).
        pub is_owned: bool,
        /// A landscape object (a static, non-item object).
        pub is_landscape: bool,
        /// Where it is, when it is placed in the world.
        pub position: Option<Position>,
        /// An appraisal of it has arrived.
        pub has_appraisal_data: bool,
        /// The server's time of the last appraisal, 0 for none.
        pub last_id_time: i32,
        /// A door that stands open.
        pub is_door_open: bool,
        /// How many in its stack (1 for a single item).
        pub stack_size: i32,
        /// Items a container holds.
        pub items_capacity: i32,
        /// Packs a container holds.
        pub containers_capacity: i32,
        /// The spells an appraisal listed on it.
        pub spell_ids: Vec<u32>,
        /// The spells active on it.
        pub active_spell_ids: Vec<u32>,
        /// Its icon (a dat id).
        pub icon_id: u32,
        /// The latest thing the server said about it was an appraisal
        /// answer saying it could not appraise it. The server also refuses
        /// an object that resists appraisal and a repeat request made too
        /// soon, so this alone does not mean the object is gone.
        pub last_appraisal_unsuccessful: bool,
        /// The body parts a worn piece covers, as the coverage bits the
        /// server sends when the object first appears (known before any
        /// appraisal; the same value as the inventory record's); 0 for
        /// anything not worn.
        pub coverage_mask: u32,
        /// Its latest full description's layout and optional values, as
        /// sent; none before one arrived.
        pub header: Option<crate::items::ObjectHeader>,
        /// A portal's appraised destination, a label to show (not a cell
        /// id); none when it is not a portal or the label is unknown.
        pub portal_destination: Option<String>,
        /// A portal's minimum character level; none when it is not a
        /// portal or no minimum is known.
        pub portal_minimum_level: Option<i32>,
        /// A portal's maximum character level; none when it is not a
        /// portal or no maximum is known.
        pub portal_maximum_level: Option<i32>,
        /// The kinds of item a house hook will take, as the item-type bits
        /// the server sends when the object first appears (the same value
        /// as the inventory record's); 0 for anything that is not a hook.
        /// Added in API 1.2: a plugin built for an older version is sent
        /// the record without it.
        since (1, 2) pub hook_item_types: u32,
        /// The last position the server sent for it, when it is placed in
        /// the world. [`Self::position`] is where the client has it now: a
        /// creature's simulated body, which moves between the server's
        /// positions and can stand apart from the last one. For the
        /// character it is likewise the last position the server sent, which
        /// can lag where the player has moved it. A change to it alone raises no
        /// [`ObjectChange`] (an object is told moved only when its cell
        /// changes), so a plugin that follows it asks again. Added in API
        /// 1.7.
        since (1, 7) pub server_position: Option<Position>,
    }
}

impl WorldObject {
    /// It can be used or activated.
    #[must_use]
    pub fn can_activate(&self) -> bool {
        self.capabilities & capabilities::INTERACTABLE != 0
    }
}

wire_record! {
    /// A weapon's profile from its last appraisal.
    pub struct WeaponProfile {
        /// Damage type bits.
        pub damage_type: i32,
        /// Speed.
        pub weapon_time: i32,
        /// The skill it uses.
        pub weapon_skill: u32,
        /// Maximum damage.
        pub damage: i32,
        /// Damage variance.
        pub damage_variance: f64,
        /// Damage modifier.
        pub damage_mod: f64,
        /// Length.
        pub weapon_length: f64,
        /// Maximum velocity.
        pub max_velocity: f64,
        /// Offense modifier.
        pub weapon_offense: f64,
        /// The velocity was estimated.
        pub max_velocity_estimated: i32,
    }
}

wire_record! {
    /// Armor's profile from its last appraisal.
    pub struct ArmorProfile {
        /// Armor level.
        pub armor_level: i32,
        /// Slashing.
        pub slash_mod: f32,
        /// Piercing.
        pub pierce_mod: f32,
        /// Bludgeoning.
        pub bludgeon_mod: f32,
        /// Cold.
        pub cold_mod: f32,
        /// Fire.
        pub fire_mod: f32,
        /// Acid.
        pub acid_mod: f32,
        /// Nether.
        pub nether_mod: f32,
        /// Electric.
        pub electric_mod: f32,
    }
}

wire_record! {
    /// A creature's attributes and its stamina and mana, as its appraisal
    /// showed them.
    pub struct CreatureAttributes {
        /// Strength.
        pub strength: u32,
        /// Endurance.
        pub endurance: u32,
        /// Quickness.
        pub quickness: u32,
        /// Coordination.
        pub coordination: u32,
        /// Focus.
        pub focus: u32,
        /// Self.
        pub self_: u32,
        /// Stamina.
        pub stamina: u32,
        /// Maximum stamina.
        pub max_stamina: u32,
        /// Mana.
        pub mana: u32,
        /// Maximum mana.
        pub max_mana: u32,
    }
}

wire_record! {
    /// A creature's profile from its last appraisal answer, as the assess
    /// window shows it.
    pub struct CreatureProfile {
        /// The profile's flags as the server sent them.
        pub flags: u32,
        /// Health.
        pub health: u32,
        /// Maximum health.
        pub max_health: u32,
        /// Attributes, stamina and mana: only when the appraisal could
        /// assess the creature. An answer that could not still carries its
        /// health.
        pub attributes: Option<CreatureAttributes>,
        /// Which values enchantments change, one bit each: strength 0x1,
        /// endurance 0x2, quickness 0x4, coordination 0x8, focus 0x10,
        /// self 0x20, health 0x40, stamina 0x80, mana 0x100. Zero when the
        /// answer carried none (nothing enchanted).
        pub attribute_highlights: u16,
        /// For each bit set in `attribute_highlights`: set when the value
        /// is raised (shown green), clear when it is lowered (red).
        pub attribute_colors: u16,
    }
}

wire_record! {
    /// Every property the client holds for an object, by property id,
    /// with the profiles kept from the last appraisal.
    pub struct ItemProperties {
        /// Integer properties.
        pub ints: Vec<(u32, i32)>,
        /// 64-bit integer properties.
        pub int64s: Vec<(u32, i64)>,
        /// Boolean properties.
        pub bools: Vec<(u32, bool)>,
        /// Floating-point properties.
        pub floats: Vec<(u32, f64)>,
        /// String properties.
        pub strings: Vec<(u32, String)>,
        /// Data-id properties.
        pub data_ids: Vec<(u32, u32)>,
        /// Instance-id properties.
        pub instance_ids: Vec<(u32, u32)>,
        /// The weapon profile, kept until a fresh appraisal replaces it.
        pub weapon: Option<WeaponProfile>,
        /// The armor profile, kept until a fresh appraisal replaces it.
        pub armor: Option<ArmorProfile>,
        /// The creature profile the last appraisal answer carried, whether
        /// or not the appraisal succeeded (a creature that could not be
        /// assessed still reports its health); none when that answer
        /// carried none. Added in API 1.9.
        since (1, 9) pub creature: Option<CreatureProfile>,
    }
}

impl ItemProperties {
    /// The integer property `id`.
    #[must_use]
    pub fn int(&self, id: u32) -> Option<i32> {
        self.ints.iter().find(|(k, _)| *k == id).map(|(_, v)| *v)
    }
}

wire_enum! {
    /// How an activation (a door, lever, portal, NPC) ended.
    pub enum ActivationOutcome: "activation outcome" {
        /// Nothing yet.
        None = 0,
        /// It completed.
        Completed = 1,
        /// The server refused it.
        Refused = 2,
        /// Something interrupted it.
        Interrupted = 3,
        /// The object went away.
        TargetLost = 4,
        /// The way was blocked.
        Blocked = 5,
        /// It took too long.
        TimedOut = 6,
    }
}

wire_record! {
    /// The end of an activation.
    pub struct ActivationCompletion {
        /// Counts completions; 0 for none yet.
        pub revision: i64,
        /// The object.
        pub object_id: u32,
        /// How it ended.
        pub outcome: ActivationOutcome,
        /// The server's error code, 0 for none.
        pub weenie_error: u32,
    }
}

wire_enum! {
    /// What happened to an object.
    pub enum ObjectChangeKind: "object change" {
        /// It appeared.
        Created = 0,
        /// Its description changed.
        Updated = 1,
        /// An appraisal of it arrived.
        IdentReceived = 2,
        /// It crossed into another cell (or container).
        Moved = 3,
        /// It left the client's table.
        Released = 4,
    }
}

/// [`ObjectChange::changed_fields`] bits.
pub mod changed_fields {
    /// It appeared or left.
    pub const LIFECYCLE: u32 = 1 << 0;
    /// Its identity (name, class, container).
    pub const IDENTITY: u32 = 1 << 1;
    /// Its place.
    pub const POSITION: u32 = 1 << 2;
    /// Its appraisal.
    pub const APPRAISAL: u32 = 1 << 3;
}

impl ObjectChangeKind {
    /// The fields a change of this kind touches.
    #[must_use]
    pub const fn fields(self) -> u32 {
        use changed_fields::{APPRAISAL, IDENTITY, LIFECYCLE, POSITION};
        match self {
            Self::Created => LIFECYCLE | IDENTITY | POSITION,
            Self::Updated => IDENTITY,
            Self::IdentReceived => APPRAISAL,
            Self::Moved => POSITION,
            Self::Released => LIFECYCLE,
        }
    }
}

wire_record! {
    /// An object changed ([`crate::Event::ObjectChanged`]).
    pub struct ObjectChange {
        /// The object.
        pub object_id: u32,
        /// What happened.
        pub kind: ObjectChangeKind,
        /// Counts changes for the session, from 1.
        pub revision: i64,
        /// [`changed_fields`] bits.
        pub changed_fields: u32,
        /// The object as it is now (none once released).
        pub current: Option<WorldObject>,
    }
}

wire_record! {
    /// Something the client tracks in the world, with where it stands.
    pub struct EntitySnapshot {
        /// The client's id for it.
        pub id: u32,
        /// Its server id (0 for none).
        pub source_id: u32,
        /// Landblock-local position, metres.
        pub position: (f32, f32, f32),
        /// Orientation quaternion (x, y, z, w).
        pub rotation: (f32, f32, f32, f32),
    }
}

wire_record! {
    /// One tracked contract (quest).
    pub struct Contract {
        /// Its id.
        pub contract_id: u32,
        /// Its stage.
        pub stage: u32,
        /// Progress in the stage.
        pub progress: u32,
        /// The quest tracker shows it.
        pub is_displayed: bool,
        /// Its name (from the dats).
        pub name: String,
        /// Its description.
        pub description: String,
        /// Its status text.
        pub status: String,
    }
}

wire_record! {
    /// The game's calendar and time of day.
    pub struct WorldTime {
        /// The server has told the client the time.
        pub is_available: bool,
        /// Game ticks.
        pub game_ticks: f64,
        /// Year.
        pub year: i32,
        /// Month (0-based).
        pub month: i32,
        /// Day of the month.
        pub day: i32,
        /// Hour slot of the day.
        pub hour: i32,
        /// The month's name.
        pub month_name: String,
        /// The hour's name.
        pub hour_name: String,
        /// It is day.
        pub is_day: bool,
        /// Real minutes until day.
        pub minutes_until_day: f64,
        /// Real minutes until night.
        pub minutes_until_night: f64,
    }
}

wire_record! {
    /// A wall of a dungeon floor, landblock-local metres.
    pub struct DungeonWall {
        /// One end.
        pub start: (f32, f32),
        /// The other.
        pub end: (f32, f32),
    }
}

wire_record! {
    /// A dungeon cell on the floorplan.
    pub struct DungeonCell {
        /// The cell.
        pub cell_id: u32,
        /// The middle of the cell's geometry, landblock-local metres. In a
        /// tall room it is well above the floor; a walk or preview to it
        /// stands it on the floor under it.
        pub center: (f32, f32, f32),
        /// The layer it belongs to: the 6 m band of its floor's height (or,
        /// for a cell with no floor, of the floor it opens onto below). A
        /// band is coarse: two cells on one floor can sit in different
        /// bands, so compare `floor_z` to tell floors apart.
        pub layer_z: f32,
        /// The lower corner of the box around every polygon of the cell,
        /// landblock-local metres (API 1.5).
        since (1, 5) pub bounds_min: (f32, f32, f32),
        /// The upper corner of that box (API 1.5).
        since (1, 5) pub bounds_max: (f32, f32, f32),
        /// The height of the cell's lowest floor, landblock-local metres:
        /// the lowest level polygon of the cell that faces up, at its
        /// corners' mean height (API 1.5). `None` for a cell with no floor
        /// of its own, such as a shaft or the upper half of a tall room.
        since (1, 5) pub floor_z: Option<f32>,
    }
}

wire_record! {
    /// One floor of a dungeon.
    pub struct DungeonLayer {
        /// Its height, metres.
        pub z: f32,
        /// Floor polygons.
        pub floors: Vec<Vec<(f32, f32)>>,
        /// Walls.
        pub walls: Vec<DungeonWall>,
    }
}

wire_record! {
    /// A dungeon's floorplan.
    pub struct Floorplan {
        /// The landblock (high 16 bits of its cells).
        pub landblock_id: u32,
        /// Floors, lowest first.
        pub layers: Vec<DungeonLayer>,
        /// Cells.
        pub cells: Vec<DungeonCell>,
        /// The lower corner of everything, metres.
        pub bounds_min: (f32, f32, f32),
        /// The upper corner, metres.
        pub bounds_max: (f32, f32, f32),
    }
}

wire_record! {
    /// One doorway of an indoor cell (API 1.5): the opening its portal
    /// polygon makes onto a neighbouring cell or the outdoors, as the game
    /// data stores it. A door that stands in it is an object, not part of
    /// this: whether it is shut or locked is for the world objects to say.
    pub struct CellDoorway {
        /// The full id of the cell on the other side, landblock in the high
        /// half; 0 when the doorway leads outdoors.
        pub other_cell_id: u32,
        /// No cell is on the other side: the doorway leads outdoors (a
        /// dungeon's exit to the landscape, a building's door).
        pub leads_outside: bool,
        /// The data's exact-match flag: the opening must line up exactly
        /// with its partner on the other side.
        pub exact_match: bool,
        /// The data's portal-side flag as stored (set for side 0, clear
        /// for side 1): which side of its polygon the cell lies on.
        pub portal_side: bool,
        /// The middle of the doorway, landblock-local metres (x east, y
        /// north, z up): the middle of the box around its polygon's corners
        /// placed as the cell is, so both sides of a doorway give the same
        /// point. The cell's origin when the data has no such polygon.
        pub center: (f32, f32, f32),
    }
}

wire_record! {
    /// One indoor cell exactly as the game data places it: which
    /// environment piece it is built from and how that piece stands in the
    /// landblock, its doorways, and the cells the data lists as seen from
    /// it. A plugin that draws dungeons from its own tiles, one per piece,
    /// places each tile with this.
    pub struct IndoorCell {
        /// The full cell id, landblock in the high half.
        pub cell_id: u32,
        /// The environment piece, as the cell stores it (the low sixteen
        /// bits of the environment file's id); zero for a cell naming none.
        pub environment_id: u32,
        /// Which of the piece's structures the cell uses.
        pub cell_structure: u32,
        /// Where the piece's origin sits, landblock-local metres (x east, y
        /// north from the landblock's south-west corner, z up).
        pub origin: (f32, f32, f32),
        /// How the piece is turned, as stored, in the order W, X, Y, Z: a
        /// turn about the vertical by an angle a has W = cos(a/2) and
        /// Z = sin(a/2), a positive a turning east towards north.
        pub orientation: (f32, f32, f32, f32),
        /// The cell sees the landscape (a building's room); false for a
        /// sealed dungeon's cell.
        pub sees_outside: bool,
        /// The cell's doorways, in the data's order (API 1.5): which cell
        /// each opens onto, or whether it leads outdoors.
        since (1, 5) pub doorways: Vec<CellDoorway>,
        /// The full ids of the cells the data lists as seen from this one,
        /// in the data's order (API 1.5). This is the list the game draws
        /// from when the camera is in this cell, not a line of sight: it is
        /// worked out from doorways ahead of time, so it takes in cells round
        /// a corner or behind a closed door, and a cell in it may be far
        /// from or hidden from a point in this one.
        since (1, 5) pub visible_cells: Vec<u32>,
    }
}

impl IndoorCell {
    /// The turn about the vertical the orientation describes, degrees from
    /// 0 up to 360, counted from east towards north. Dungeon pieces lie at
    /// quarter turns, so this is 0, 90, 180 or 270 to within rounding; a
    /// piece also tilted reads only its turn about the vertical.
    #[must_use]
    pub fn yaw_degrees(&self) -> f32 {
        let (w, x, y, z) = self.orientation;
        let (w, x, y, z) = (f64::from(w), f64::from(x), f64::from(y), f64::from(z));
        let yaw = (2.0 * (w * z + x * y)).atan2(1.0 - 2.0 * (y * y + z * z));
        let mut degrees = yaw.to_degrees();
        if degrees < 0.0 {
            degrees += 360.0;
        }
        #[allow(clippy::cast_possible_truncation)]
        if degrees >= 360.0 {
            0.0
        } else {
            degrees as f32
        }
    }
}

/// The most landblocks one [`crate::calls::LandscapeTerrain`] answers: the
/// first this many of a longer request are answered, the rest left out.
pub const LANDSCAPE_TERRAIN_MAX: usize = 256;

/// Vertices along one side of a landblock's terrain grid.
pub const TERRAIN_VERTICES_PER_SIDE: usize = 9;

/// Metres between neighbouring terrain vertices.
pub const TERRAIN_VERTEX_SPACING_METERS: f32 = 24.0;

/// Metres along one side of a landblock.
pub const LANDBLOCK_METERS: f32 = 192.0;

wire_record! {
    /// One vertex of a landblock's terrain grid (API 1.5), as the game
    /// files store it.
    pub struct TerrainVertex {
        /// The terrain type, 0 to 31: an index into the region's terrain
        /// types ([`TerrainColor`]).
        pub terrain_type: u8,
        /// The road bits, 0 to 3: non-zero where a road runs over the
        /// vertex.
        pub road: u8,
        /// The ground's height at the vertex, metres, through the region's
        /// land height table: the height the character stands at there.
        pub height_meters: f32,
    }
}

wire_record! {
    /// A landblock's outdoor terrain (API 1.5): a 9 by 9 grid of vertices
    /// 24 m apart, covering the landblock's 192 m square edge to edge (a
    /// landblock's east and north edges are its neighbours' west and south
    /// edges).
    pub struct LandblockTerrain {
        /// The landblock, `x << 8 | y` (x counts east, y north).
        pub landblock: u16,
        /// The 81 vertices; vertex `(x, y)` is at index `x * 9 + y`, `x`
        /// counting east and `y` north from vertex `(0, 0)`, the
        /// landblock's south-west corner. It stands at landblock-local
        /// `(24 x, 24 y)` metres.
        pub vertices: Vec<TerrainVertex>,
    }
}

impl LandblockTerrain {
    /// The vertex `x` east and `y` north of the south-west corner (each 0
    /// to 8); none outside the grid.
    #[must_use]
    pub fn vertex(&self, x: usize, y: usize) -> Option<&TerrainVertex> {
        if x >= TERRAIN_VERTICES_PER_SIDE || y >= TERRAIN_VERTICES_PER_SIDE {
            return None;
        }
        self.vertices.get(x * TERRAIN_VERTICES_PER_SIDE + y)
    }
}

/// The palette entry for roads ([`TerrainColor::terrain_type`]): not a
/// terrain type a vertex has, but the texture the game lays over the ground
/// where a vertex's [`TerrainVertex::road`] bits are non-zero.
pub const ROAD_TERRAIN_TYPE: u8 = 32;

wire_record! {
    /// How one of the region's terrain types looks (API 1.5), so a map
    /// drawn from [`LandblockTerrain`] looks like the game. The palette
    /// also has one entry for roads, [`ROAD_TERRAIN_TYPE`].
    pub struct TerrainColor {
        /// The terrain type, as [`TerrainVertex::terrain_type`] names it,
        /// or [`ROAD_TERRAIN_TYPE`].
        pub terrain_type: u8,
        /// The mean colour of the type's landscape texture, `0xRRGGBB`; 0
        /// when the game files have no texture for it or it does not decode.
        pub rgb: u32,
        /// The colour the game's data assigns the type for maps,
        /// `0xRRGGBB`, as the region file stores it. The region has none
        /// for roads: their entry repeats `rgb`.
        pub map_rgb: u32,
        /// The type's name as the game files give it, "Road" for roads;
        /// none when the files give none.
        pub name: Option<String>,
    }
}

wire_enum! {
    /// The flight a projectile path test models.
    pub enum ProjectilePathKind: "projectile path kind" {
        /// A straight line.
        Straight = 0,
        /// A lobbed arc.
        Arc = 1,
        /// A missile weapon's arc.
        Missile = 2,
    }
}

wire_enum! {
    /// The answer to a projectile path test.
    pub enum ProjectilePathStatus: "projectile path status" {
        /// No world to test against.
        Unavailable = 0,
        /// Nothing in the way.
        Clear = 1,
        /// Something is in the way.
        Blocked = 2,
        /// The target is not usable.
        InvalidTarget = 3,
        /// More checks were needed than allowed.
        BudgetExceeded = 4,
        /// The test failed.
        Error = 5,
    }
}

wire_record! {
    /// A projectile path test against the session's collision world.
    pub struct ProjectilePathQuery {
        /// The target.
        pub target_id: u32,
        /// The flight.
        pub kind: ProjectilePathKind,
        /// The target height: 1 high, 2 medium, 3 low.
        pub target_height: u8,
        /// The projectile's radius, metres.
        pub radius: f32,
        /// The step between collision samples, metres.
        pub step: f32,
        /// The most collision checks allowed.
        pub maximum_checks: i32,
        /// Keep the samples in the answer.
        pub diagnostics: bool,
    }
}

wire_record! {
    /// One collision sample of a path test.
    pub struct ProjectileSample {
        /// Where, landblock-local metres.
        pub position: (f32, f32, f32),
        /// Nothing was hit there.
        pub is_clear: bool,
        /// The radius tested.
        pub radius: f32,
    }
}

wire_record! {
    /// The answer to a [`ProjectilePathQuery`].
    pub struct ProjectilePath {
        /// The verdict.
        pub status: ProjectilePathStatus,
        /// Collision checks made.
        pub collision_checks: i32,
        /// The object in the way, 0 for none or the landscape.
        pub blocking_id: u32,
        /// Why, in words.
        pub notice: Option<String>,
        /// The samples, when asked for.
        pub samples: Vec<ProjectileSample>,
    }
}

wire_record! {
    /// A portal or login transition ([`crate::Event::PortalTransition`]).
    pub struct PortalTransition {
        /// Counts transitions.
        pub revision: i64,
        /// The transition's generation, 0 for none.
        pub generation: i64,
        /// Where it goes.
        pub destination_cell: u32,
        /// The destination is ready.
        pub is_ready: bool,
        /// The player materialized.
        pub is_materialized: bool,
        /// It completed.
        pub is_completed: bool,
        /// It was cancelled.
        pub is_cancelled: bool,
        /// The recall request that caused it, 0 for none.
        pub recall_request_revision: i64,
        /// 0 unknown, 1 login, 2 portal.
        pub kind: u8,
    }
}

wire_record! {
    /// A yes/no question the server asked
    /// ([`crate::Event::ConfirmationRequested`]).
    pub struct Confirmation {
        /// The question's context id (answer with it).
        pub context_id: u32,
        /// The confirmation type.
        pub kind: i32,
        /// The question.
        pub text: String,
    }
}

wire_record! {
    /// The selection changed ([`crate::Event::SelectionChanged`]).
    pub struct SelectionChange {
        /// What was selected before.
        pub previous: Option<u32>,
        /// What is selected now.
        pub selected: Option<u32>,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::ApiVersion;
    use crate::wire::Wire;

    fn appraised_creature() -> ItemProperties {
        ItemProperties {
            ints: vec![(25, 30)],
            creature: Some(CreatureProfile {
                flags: 8,
                health: 120,
                max_health: 200,
                attribute_highlights: 0x41,
                attribute_colors: 0x01,
                attributes: Some(CreatureAttributes {
                    strength: 55,
                    self_: 70,
                    max_stamina: 100,
                    max_mana: 50,
                    ..CreatureAttributes::default()
                }),
            }),
            ..ItemProperties::default()
        }
    }

    // API 1.9 added the creature profile: a plugin built for 1.8 is sent
    // the item properties without it, byte for byte as before, and one
    // built for 1.9 gets it.
    #[test]
    fn the_creature_profile_reaches_only_plugins_built_for_api_1_9() {
        let v1_8 = ApiVersion::new(1, 8);
        let appraised = appraised_creature();
        let without = ItemProperties {
            creature: None,
            ..appraised.clone()
        };
        assert_eq!(appraised.to_bytes_at(v1_8), without.to_bytes_at(v1_8));
        let old = ItemProperties::from_bytes_at(&appraised.to_bytes_at(v1_8), v1_8).unwrap();
        assert_eq!(old, without);
        let now = ItemProperties::from_bytes(&appraised.to_bytes()).unwrap();
        assert_eq!(now, appraised);
    }

    // A loot classifier's question carries the item properties too.
    #[test]
    fn a_loot_question_sent_to_an_api_1_8_classifier_has_no_creature_profile() {
        use crate::callback::{Callback, LootQuestion};
        use crate::items::LootContext;
        let v1_8 = ApiVersion::new(1, 8);
        let question = |properties: ItemProperties| {
            Callback::LootClassify(LootQuestion {
                handle: 1,
                profile: String::new(),
                context: LootContext {
                    item: crate::items::InventoryItem::default(),
                    properties,
                    owned_items: Vec::new(),
                },
            })
        };
        let appraised = appraised_creature();
        let without = ItemProperties {
            creature: None,
            ..appraised.clone()
        };
        assert_eq!(
            question(appraised).to_bytes_at(v1_8),
            question(without).to_bytes_at(v1_8)
        );
    }

    // OpenAC `PluginObjectClassifier.Classify`: item-type bits first, then
    // public flags win; books, NPCs and combat pets as special cases.
    #[test]
    fn objects_classify_as_openac_does() {
        assert_eq!(classify(0x1, 0), ObjectClass::MeleeWeapon);
        assert_eq!(classify(0x10, 0x10), ObjectClass::Monster);
        assert_eq!(classify(0x10, 0), ObjectClass::Npc);
        assert_eq!(classify(0x10, 0x0400_0010), ObjectClass::CombatPet);
        assert_eq!(classify(0x10, 0x8), ObjectClass::Player);
        assert_eq!(classify(0x200, 0x2000), ObjectClass::Corpse);
        assert_eq!(classify(0x2000, 0x2), ObjectClass::Journal);
        assert_eq!(classify(0x2000, 0x4), ObjectClass::Sign);
        assert_eq!(classify(0x2000, 0x1), ObjectClass::Container);
        assert_eq!(classify(0x2000, 0x8 | 0x10), ObjectClass::Player);
        assert_eq!(classify(0, 0), ObjectClass::Unknown);
        assert_eq!(
            capabilities_of(ObjectClass::Door),
            capabilities::INTERACTABLE | capabilities::DOOR
        );
        assert_eq!(capabilities_of(ObjectClass::Gem), 0);
    }

    // OpenAC `RuntimeNavigationProjection.Position` / `LandblockLocal`:
    // landblock 0x7F7F at (84, 84) is the map's centre (0, 0); one
    // landblock east is 192 metres away.
    #[test]
    fn map_coordinates_come_from_the_cell_and_go_back() {
        let at = Position::from_cell(0x7F7F_0001, [84.0, 84.0, 24.0], 90.0);
        assert!(at.east_west.abs() < 1e-9);
        assert!(at.north_south.abs() < 1e-9);
        assert!((at.elevation - 0.1).abs() < 1e-9);
        assert!(at.is_outdoor);
        let local = at.landblock_local();
        assert!((local[0] - 84.0).abs() < 1e-4 && (local[2] - 24.0).abs() < 1e-4);
        let east = Position::from_cell(0x807F_0001, [84.0, 84.0, 0.0], 0.0);
        assert!((at.horizontal_distance_metres(&east) - 192.0).abs() < 1e-6);
        assert!(!Position::from_cell(0x7F7F_0100, [0.0; 3], 0.0).is_outdoor);
    }

    // OpenAC `IndoorCellCaptureTests.YawIsCountedFromEastTowardsNorth`.
    #[test]
    fn yaw_is_counted_from_east_towards_north() {
        for (turn, expected) in [
            (0.0f32, 0.0f32),
            (90.0, 90.0),
            (180.0, 180.0),
            (270.0, 270.0),
            (-90.0, 270.0),
        ] {
            let half = turn.to_radians() / 2.0;
            let cell = IndoorCell {
                cell_id: 0x0123_0100,
                environment_id: 1,
                orientation: (half.cos(), 0.0, 0.0, half.sin()),
                ..IndoorCell::default()
            };
            let yaw = cell.yaw_degrees();
            // 360 and 0 are the same turn; rounding may land either side.
            let off = (yaw - expected).abs().min(360.0 - (yaw - expected).abs());
            assert!(off < 0.01, "turn {turn}: {yaw}");
        }
    }

    // A landblock's terrain and the terrain colours cross the boundary as
    // they were sent, and a vertex is found by its place east and north of
    // the south-west corner.
    #[test]
    fn landscape_records_round_trip_and_vertices_are_found_by_place() {
        use crate::wire::Wire;
        let terrain = LandblockTerrain {
            landblock: 0xA9B4,
            vertices: (0..81u8)
                .map(|i| TerrainVertex {
                    terrain_type: i % 32,
                    road: i % 4,
                    height_meters: f32::from(i) * 2.0,
                })
                .collect(),
        };
        let sent = vec![terrain.clone()];
        assert_eq!(
            Vec::<LandblockTerrain>::from_bytes(&sent.to_bytes()).unwrap(),
            sent
        );
        assert_eq!(terrain.vertex(0, 0).unwrap().height_meters, 0.0);
        // One step east is nine along: the grid runs north first.
        assert_eq!(terrain.vertex(1, 0).unwrap().height_meters, 18.0);
        assert_eq!(terrain.vertex(0, 1).unwrap().height_meters, 2.0);
        assert_eq!(terrain.vertex(8, 8).unwrap().height_meters, 160.0);
        assert!(terrain.vertex(9, 0).is_none() && terrain.vertex(0, 9).is_none());
        assert_eq!(
            TERRAIN_VERTEX_SPACING_METERS * (TERRAIN_VERTICES_PER_SIDE - 1) as f32,
            LANDBLOCK_METERS
        );

        let colours = vec![
            TerrainColor {
                terrain_type: 1,
                rgb: 0x5A_5E30,
                map_rgb: 0x64_9664,
                name: Some("Grassland".into()),
            },
            TerrainColor {
                terrain_type: 2,
                rgb: 0,
                map_rgb: 0,
                name: None,
            },
        ];
        assert_eq!(
            Vec::<TerrainColor>::from_bytes(&colours.to_bytes()).unwrap(),
            colours
        );
    }
}
