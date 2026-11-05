//! What the server says, as bytes on the wire: the messages the scenarios
//! script, written the way the client's own parsers (`ac-proto`) read
//! them. Each writer names the parser it mirrors.

use ac_bits::Writer;

fn words(words: &[u32]) -> Vec<u8> {
    words.iter().flat_map(|w| w.to_le_bytes()).collect()
}

/// An object is somewhere else now (`ac-proto` `UpdatePosition`): on the
/// ground, facing north, with these counters.
pub fn update_position(
    guid: u32,
    (cell, x, y, z): (u32, f32, f32, f32),
    instance: u16,
    position: u16,
    teleport: u16,
    force_position: u16,
) -> Vec<u8> {
    let mut w = Writer::new();
    w.u32(0xF748);
    w.u32(guid);
    // Grounded; of the orientation, only w.
    w.u32(0x04 | 0x10 | 0x20 | 0x40);
    w.u32(cell);
    w.f32(x);
    w.f32(y);
    w.f32(z);
    w.f32(1.0);
    w.u16(instance);
    w.u16(position);
    w.u16(teleport);
    w.u16(force_position);
    w.into_vec()
}

/// The server moves the character through a portal (`ac-proto`
/// `PlayerTeleport`).
pub fn player_teleport(teleport_sequence: u16) -> Vec<u8> {
    let mut w = Writer::new();
    w.u32(0xF751);
    w.u16(teleport_sequence);
    w.align(4);
    w.into_vec()
}

/// An object as the server creates it (`ac-proto` `CreateObject`): an
/// empty appearance, the physics block (a place when it has one) and the
/// public description with the fields set.
#[derive(Debug, Clone, Default)]
pub struct Object {
    pub guid: u32,
    pub name: String,
    pub item_type: u32,
    pub physics_state: u32,
    /// Cell, x, y, z.
    pub position: Option<(u32, f32, f32, f32)>,
    pub setup: Option<u32>,
    pub motion_table: Option<u32>,
    pub instance_seq: u16,
    pub position_seq: u16,
    pub object_description_flags: u32,
    pub weenie_class_id: u32,
    pub icon_id: u32,
    pub plural_name: Option<String>,
    pub items_capacity: Option<i8>,
    pub containers_capacity: Option<i8>,
    pub value: Option<u32>,
    pub useability: Option<u32>,
    pub use_radius: Option<f32>,
    pub target_type: Option<u32>,
    pub structure: Option<u16>,
    pub max_structure: Option<u16>,
    pub stack_size: Option<u16>,
    pub max_stack_size: Option<u16>,
    pub container: Option<u32>,
    pub wielder: Option<u32>,
    pub valid_locations: Option<u32>,
    pub currently_wielded_location: Option<u32>,
    pub priority: Option<u32>,
    pub burden: Option<u16>,
    pub spell_id: Option<u16>,
    pub icon_overlay_id: Option<u32>,
    pub icon_underlay_id: Option<u32>,
    pub material_type: Option<u32>,
    pub cooldown: Option<(u32, f64)>,
    pub pet_owner_id: Option<u32>,
    pub scale: Option<f32>,
    /// The kinds of item a house hook takes (item-type bits).
    pub hook_item_types: Option<u32>,
    pub hook_type: Option<u16>,
    /// When the radar shows it (4: always).
    pub radar_behavior: Option<u8>,
}

/// Physics description flags (`ac-proto` `physics_desc`).
const PD_SETUP: u32 = 0x0000_0001;
const PD_MOTION_TABLE: u32 = 0x0000_0002;
const PD_OBJ_SCALE: u32 = 0x0000_0080;
const PD_POSITION: u32 = 0x0000_8000;

impl Object {
    pub fn new(guid: u32, name: &str, item_type: u32) -> Self {
        Self {
            guid,
            name: name.to_owned(),
            item_type,
            instance_seq: 1,
            position_seq: 1,
            weenie_class_id: guid & 0xFFFF,
            ..Self::default()
        }
    }

    pub fn build(&self) -> Vec<u8> {
        let mut w = Writer::new();
        w.u32(0xF745);
        w.u32(self.guid);
        // The empty appearance.
        w.bytes(&[0x11, 0, 0, 0]);

        let mut flags = 0;
        if self.position.is_some() {
            flags |= PD_POSITION;
        }
        if self.setup.is_some() {
            flags |= PD_SETUP;
        }
        if self.motion_table.is_some() {
            flags |= PD_MOTION_TABLE;
        }
        if self.scale.is_some() {
            flags |= PD_OBJ_SCALE;
        }
        w.u32(flags);
        w.u32(self.physics_state);
        if let Some((cell, x, y, z)) = self.position {
            w.u32(cell);
            w.f32(x);
            w.f32(y);
            w.f32(z);
            // Facing north: w, x, y, z.
            w.f32(1.0);
            w.f32(0.0);
            w.f32(0.0);
            w.f32(0.0);
        }
        if let Some(motion) = self.motion_table {
            w.u32(motion);
        }
        if let Some(setup) = self.setup {
            w.u32(setup);
        }
        if let Some(scale) = self.scale {
            w.f32(scale);
        }
        // The counters: position, movement, state, vector, teleport,
        // server-controlled move, forced position, appearance, instance.
        for (index, counter) in [self.position_seq, 1, 1, 1, 0, 1, 0, 1, self.instance_seq]
            .into_iter()
            .enumerate()
        {
            let _ = index;
            w.u16(counter);
        }
        w.align(4);

        let mut wf = 0u32;
        let mut wf2 = 0u32;
        let set = |flag: &mut u32, bit: u32, on: bool| {
            if on {
                *flag |= bit;
            }
        };
        set(&mut wf, 0x1, self.plural_name.is_some());
        set(&mut wf, 0x2, self.items_capacity.is_some());
        set(&mut wf, 0x4, self.containers_capacity.is_some());
        set(&mut wf, 0x8, self.value.is_some());
        set(&mut wf, 0x10, self.useability.is_some());
        set(&mut wf, 0x20, self.use_radius.is_some());
        set(&mut wf, 0x8_0000, self.target_type.is_some());
        set(&mut wf, 0x400, self.structure.is_some());
        set(&mut wf, 0x800, self.max_structure.is_some());
        set(&mut wf, 0x1000, self.stack_size.is_some());
        set(&mut wf, 0x2000, self.max_stack_size.is_some());
        set(&mut wf, 0x4000, self.container.is_some());
        set(&mut wf, 0x8000, self.wielder.is_some());
        set(&mut wf, 0x1_0000, self.valid_locations.is_some());
        set(&mut wf, 0x2_0000, self.currently_wielded_location.is_some());
        set(&mut wf, 0x4_0000, self.priority.is_some());
        set(&mut wf, 0x80_0000, self.radar_behavior.is_some());
        set(&mut wf, 0x20_0000, self.burden.is_some());
        set(&mut wf, 0x40_0000, self.spell_id.is_some());
        set(&mut wf, 0x2000_0000, self.hook_item_types.is_some());
        set(&mut wf, 0x1000_0000, self.hook_type.is_some());
        set(&mut wf, 0x4000_0000, self.icon_overlay_id.is_some());
        set(&mut wf, 0x8000_0000, self.material_type.is_some());
        set(&mut wf2, 0x1, self.icon_underlay_id.is_some());
        set(&mut wf2, 0x2 | 0x4, self.cooldown.is_some());
        set(&mut wf2, 0x8, self.pet_owner_id.is_some());
        let mut description_flags = self.object_description_flags;
        if wf2 != 0 {
            description_flags |= 0x0400_0000;
        }
        w.u32(wf);
        w.string16l(&self.name).unwrap();
        w.packed_dword(self.weenie_class_id).unwrap();
        w.packed_dword(self.icon_id).unwrap();
        w.u32(self.item_type);
        w.u32(description_flags);
        w.align(4);
        if description_flags & 0x0400_0000 != 0 {
            w.u32(wf2);
        }
        if let Some(plural) = &self.plural_name {
            w.string16l(plural).unwrap();
        }
        if let Some(v) = self.items_capacity {
            w.i8(v);
        }
        if let Some(v) = self.containers_capacity {
            w.i8(v);
        }
        if let Some(v) = self.value {
            w.u32(v);
        }
        if let Some(v) = self.useability {
            w.u32(v);
        }
        if let Some(v) = self.use_radius {
            w.f32(v);
        }
        if let Some(v) = self.target_type {
            w.u32(v);
        }
        if let Some(v) = self.structure {
            w.u16(v);
        }
        if let Some(v) = self.max_structure {
            w.u16(v);
        }
        if let Some(v) = self.stack_size {
            w.u16(v);
        }
        if let Some(v) = self.max_stack_size {
            w.u16(v);
        }
        if let Some(v) = self.container {
            w.u32(v);
        }
        if let Some(v) = self.wielder {
            w.u32(v);
        }
        if let Some(v) = self.valid_locations {
            w.u32(v);
        }
        if let Some(v) = self.currently_wielded_location {
            w.u32(v);
        }
        if let Some(v) = self.priority {
            w.u32(v);
        }
        if let Some(v) = self.radar_behavior {
            w.u8(v);
        }
        if let Some(v) = self.burden {
            w.u16(v);
        }
        if let Some(v) = self.spell_id {
            w.u16(v);
        }
        if let Some(v) = self.hook_item_types {
            w.u32(v);
        }
        if let Some(v) = self.hook_type {
            w.u16(v);
        }
        if let Some(v) = self.icon_overlay_id {
            w.packed_dword(v).unwrap();
        }
        if let Some(v) = self.icon_underlay_id {
            w.packed_dword(v).unwrap();
        }
        if let Some(v) = self.material_type {
            w.u32(v);
        }
        if let Some((id, duration)) = self.cooldown {
            w.u32(id);
            w.f64(duration);
        }
        if let Some(v) = self.pet_owner_id {
            w.u32(v);
        }
        w.align(4);
        w.into_vec()
    }
}

impl Object {
    /// The same object said again as an update (`ac-proto`
    /// `CreateObject::parse_update`, opcode `0xF7DB`).
    pub fn build_update(&self) -> Vec<u8> {
        let mut body = self.build();
        body[..4].copy_from_slice(&0xF7DBu32.to_le_bytes());
        body
    }
}

/// What a container holds (`ac-proto` `ViewContents`): the container,
/// then each object and its container type (0 item, 1 pack).
pub fn view_contents(container: u32, items: &[(u32, u32)]) -> Vec<u8> {
    let mut w = Writer::new();
    w.u32(container);
    w.u32(u32::try_from(items.len()).unwrap());
    for (guid, kind) in items {
        w.u32(*guid);
        w.u32(*kind);
    }
    w.into_vec()
}

/// The description of an object the character asked about, with these
/// whole-number properties (`ac-proto` `IdentifyObjectResponse`).
pub fn appraisal_response(guid: u32, ints: &[(u32, i32)], success: bool) -> Vec<u8> {
    let mut w = Writer::new();
    w.u32(guid);
    w.u32(if ints.is_empty() { 0 } else { 0x1 });
    w.u32(u32::from(success));
    if !ints.is_empty() {
        w.u16(u16::try_from(ints.len()).unwrap());
        w.u16(u16::try_from(ints.len()).unwrap());
        for (k, v) in ints {
            w.u32(*k);
            w.i32(*v);
        }
    }
    w.into_vec()
}

/// A line of text with its length in front and padding behind (the shape
/// of every wire string).
pub fn text(line: &str) -> Vec<u8> {
    let mut w = Writer::new();
    w.string16l(line).unwrap();
    w.into_vec()
}

/// An object leaves the world (`ac-proto` `DeleteObject`).
pub fn delete_object(guid: u32, instance_sequence: u16) -> Vec<u8> {
    let mut w = Writer::new();
    w.u32(0xF747);
    w.u32(guid);
    w.u16(instance_sequence);
    w.align(4);
    w.into_vec()
}

/// An object's physics state bits change (`ac-proto` `SetState`).
pub fn set_state(guid: u32, state: u32, instance_sequence: u16, state_sequence: u16) -> Vec<u8> {
    let mut w = Writer::new();
    w.u32(0xF74B);
    w.u32(guid);
    w.u32(state);
    w.u16(instance_sequence);
    w.u16(state_sequence);
    w.into_vec()
}

/// Local speech (`ac-proto` `HearSpeech`): text, speaker, speaker id, chat
/// type.
pub fn hear_speech(sender: &str, text: &str, sender_guid: u32, chat_type: u32) -> Vec<u8> {
    let mut w = Writer::new();
    w.u32(0x02BB);
    w.string16l(text).unwrap();
    w.string16l(sender).unwrap();
    w.u32(sender_guid);
    w.u32(chat_type);
    w.into_vec()
}

/// Game event payload: these words.
pub fn payload(parts: &[u32]) -> Vec<u8> {
    words(parts)
}

/// How much of a creature's health is left (`ac-proto` `UpdateHealth`
/// event payload).
pub fn update_health(target: u32, fraction: f32) -> Vec<u8> {
    let mut w = Writer::new();
    w.u32(target);
    w.f32(fraction);
    w.into_vec()
}

/// One contract changed (`ac-proto` `ContractTrackerUpdate`).
pub fn contract_tracker(
    contract_id: u32,
    stage: u32,
    delete: bool,
    set_as_display: bool,
) -> Vec<u8> {
    let mut w = Writer::new();
    w.u32(1);
    w.u32(contract_id);
    w.u32(stage);
    w.f64(0.0);
    w.f64(0.0);
    w.u32(u32::from(delete));
    w.u32(u32::from(set_as_display));
    w.into_vec()
}

/// The character's description (`ac-proto` `PlayerDescription::parse`):
/// the property tables, the attributes and vitals, the skills, the
/// spellbook, the enchantments, the options and the inventory lists.
///
/// The options default to the words the client starts from before any
/// description (`ac-runtime` `character::DEFAULT_OPTIONS1`/`2`): OpenAC's
/// harness never says a description, so its arms play with those, and a
/// scenario that wants others names them.
#[derive(Debug, Clone)]
pub struct Description {
    pub ints: Vec<(u32, i32)>,
    pub int64s: Vec<(u32, i64)>,
    pub bools: Vec<(u32, bool)>,
    pub strings: Vec<(u32, String)>,
    pub data_ids: Vec<(u32, u32)>,
    /// Attribute and vital id (1..=9): ranks, start, xp, current (vitals).
    pub attributes: Vec<(u32, u32, u32, u32, u32)>,
    /// Skill id, ranks, advancement class, xp.
    pub skills: Vec<(u32, u16, u32, u32)>,
    pub spells: Vec<u32>,
    /// Multiplicative enchantments.
    pub enchantments: Vec<Enchantment>,
    pub vitae: Option<Enchantment>,
    pub options1: u32,
    pub options2: u32,
    /// Guid, container type (0 item, 1 pack, 2 foci).
    pub inventory: Vec<(u32, u32)>,
    /// Guid, location, priority.
    pub equipped: Vec<(u32, u32, u32)>,
    /// The shortcut bar: slot, object, spell.
    pub shortcuts: Vec<(i32, u32, u32)>,
}

impl Default for Description {
    fn default() -> Self {
        Self {
            ints: Vec::new(),
            int64s: Vec::new(),
            bools: Vec::new(),
            strings: Vec::new(),
            data_ids: Vec::new(),
            attributes: Vec::new(),
            skills: Vec::new(),
            spells: Vec::new(),
            enchantments: Vec::new(),
            vitae: None,
            options1: ac_runtime::character::DEFAULT_OPTIONS1,
            options2: ac_runtime::character::DEFAULT_OPTIONS2,
            inventory: Vec::new(),
            equipped: Vec::new(),
            shortcuts: Vec::new(),
        }
    }
}

impl Description {
    pub fn build(&self) -> Vec<u8> {
        fn header(w: &mut Writer, count: usize) {
            w.u16(u16::try_from(count).unwrap());
            w.u16(0);
        }
        let mut w = Writer::new();
        let mut flags = 0;
        if !self.ints.is_empty() {
            flags |= 0x1;
        }
        if !self.bools.is_empty() {
            flags |= 0x2;
        }
        if !self.data_ids.is_empty() {
            flags |= 0x8;
        }
        if !self.strings.is_empty() {
            flags |= 0x10;
        }
        if !self.int64s.is_empty() {
            flags |= 0x80;
        }
        w.u32(flags);
        // The weenie type: a creature.
        w.u32(1);
        if !self.ints.is_empty() {
            header(&mut w, self.ints.len());
            for (k, v) in &self.ints {
                w.u32(*k);
                w.i32(*v);
            }
        }
        if !self.int64s.is_empty() {
            header(&mut w, self.int64s.len());
            for (k, v) in &self.int64s {
                w.u32(*k);
                w.i64(*v);
            }
        }
        if !self.bools.is_empty() {
            header(&mut w, self.bools.len());
            for (k, v) in &self.bools {
                w.u32(*k);
                w.u32(u32::from(*v));
            }
        }
        if !self.strings.is_empty() {
            header(&mut w, self.strings.len());
            for (k, v) in &self.strings {
                w.u32(*k);
                w.string16l(v).unwrap();
                w.align(4);
            }
        }
        if !self.data_ids.is_empty() {
            header(&mut w, self.data_ids.len());
            for (k, v) in &self.data_ids {
                w.u32(*k);
                w.u32(*v);
            }
        }
        let mut vector = 0x1 | 0x2;
        if !self.spells.is_empty() {
            vector |= 0x100;
        }
        if !self.enchantments.is_empty() || self.vitae.is_some() {
            vector |= 0x200;
        }
        w.u32(vector);
        // Has health.
        w.u32(1);
        let present = self
            .attributes
            .iter()
            .fold(0u32, |bits, a| bits | (1 << (a.0 - 1)));
        w.u32(present);
        let mut attributes = self.attributes.clone();
        attributes.sort_by_key(|a| a.0);
        for (id, ranks, start, xp, current) in attributes {
            w.u32(ranks);
            w.u32(start);
            w.u32(xp);
            if id >= 7 {
                w.u32(current);
            }
        }
        header(&mut w, self.skills.len());
        for (skill, ranks, class, xp) in &self.skills {
            w.u32(*skill);
            w.u16(*ranks);
            w.u16(1);
            w.u32(*class);
            w.u32(*xp);
            w.u32(0);
            w.u32(0);
            w.f64(0.0);
        }
        if !self.spells.is_empty() {
            header(&mut w, self.spells.len());
            for spell in &self.spells {
                w.u32(*spell);
                w.f32(2.0);
            }
        }
        if vector & 0x200 != 0 {
            let mut mask = 0;
            if !self.enchantments.is_empty() {
                mask |= 0x1;
            }
            if self.vitae.is_some() {
                mask |= 0x4;
            }
            w.u32(mask);
            if !self.enchantments.is_empty() {
                w.u32(u32::try_from(self.enchantments.len()).unwrap());
                for e in &self.enchantments {
                    w.bytes(&enchantment(e));
                }
            }
            if let Some(vitae) = &self.vitae {
                w.bytes(&enchantment(vitae));
            }
        }
        // The trailer: options (with the second word), the shortcuts when
        // there are any, one empty spell bar, the spellbook filters, the
        // inventory lists.
        let shortcut_flag = if self.shortcuts.is_empty() { 0 } else { 0x1 };
        w.u32(0x40 | shortcut_flag);
        w.u32(self.options1);
        if !self.shortcuts.is_empty() {
            w.u32(u32::try_from(self.shortcuts.len()).unwrap());
            for (index, object, spell) in &self.shortcuts {
                w.i32(*index);
                w.u32(*object);
                w.u32(*spell);
            }
        }
        w.u32(0);
        w.u32(0x3FFF);
        w.u32(self.options2);
        w.u32(u32::try_from(self.inventory.len()).unwrap());
        for (guid, kind) in &self.inventory {
            w.u32(*guid);
            w.u32(*kind);
        }
        w.u32(u32::try_from(self.equipped.len()).unwrap());
        for (guid, location, priority) in &self.equipped {
            w.u32(*guid);
            w.u32(*location);
            w.u32(*priority);
        }
        w.into_vec()
    }
}

/// One enchantment (`ac-proto` `Enchantment::read`), as the update event
/// carries it.
#[derive(Debug, Clone, Copy, Default)]
pub struct Enchantment {
    pub spell_id: u16,
    pub layer: u16,
    pub spell_category: u16,
    pub power_level: u32,
    pub duration: f64,
    pub caster_guid: u32,
    pub stat_mod_type: u32,
    pub stat_mod_key: u32,
    pub stat_mod_value: f32,
}

pub fn enchantment(e: &Enchantment) -> Vec<u8> {
    let mut w = Writer::new();
    w.u16(e.spell_id);
    w.u16(e.layer);
    w.u16(e.spell_category);
    w.u16(0);
    w.u32(e.power_level);
    w.f64(0.0);
    w.f64(e.duration);
    w.u32(e.caster_guid);
    w.f32(0.0);
    w.f32(0.0);
    w.f64(0.0);
    w.u32(e.stat_mod_type);
    w.u32(e.stat_mod_key);
    w.f32(e.stat_mod_value);
    w.into_vec()
}

/// One of the character's skills, stated in full (`ac-proto`
/// `PrivateUpdateSkill`): trained, with ranks and banked experience.
pub fn skill_update(sequence: u8, skill: u32, ranks: u16, xp: u32) -> Vec<u8> {
    let mut w = Writer::new();
    w.u32(0x02DD);
    w.u8(sequence);
    w.u32(skill);
    w.u16(ranks);
    w.u16(0);
    // Trained, which is what a raised skill is.
    w.u32(2);
    w.u32(xp);
    w.u32(0);
    w.u32(0);
    w.f64(0.0);
    w.into_vec()
}

/// One of the character's primary attributes (`ac-proto`
/// `PrivateUpdateAttribute`).
pub fn attribute_update(sequence: u8, attribute: u32, ranks: u32, start: u32, xp: u32) -> Vec<u8> {
    let mut w = Writer::new();
    w.u32(0x02E3);
    w.u8(sequence);
    w.u32(attribute);
    w.u32(ranks);
    w.u32(start);
    w.u32(xp);
    w.into_vec()
}

/// One of the character's vitals in full (`ac-proto` `PrivateUpdateVital`).
pub fn vital_update(
    sequence: u8,
    vital: u32,
    ranks: u32,
    start: u32,
    xp: u32,
    current: u32,
) -> Vec<u8> {
    let mut w = Writer::new();
    w.u32(0x02E7);
    w.u8(sequence);
    w.u32(vital);
    w.u32(ranks);
    w.u32(start);
    w.u32(xp);
    w.u32(current);
    w.into_vec()
}

/// One member of an allegiance, as the profile carries it.
#[derive(Debug, Clone, Default)]
pub struct AllegianceMember {
    pub id: u32,
    pub parent: u32,
    pub logged_in: bool,
    pub name: &'static str,
    pub rank: u16,
    pub level: u32,
    pub gender: u8,
    pub heritage_group: u8,
}

impl AllegianceMember {
    pub fn new(id: u32, parent: u32, logged_in: bool, name: &'static str) -> Self {
        Self {
            id,
            parent,
            logged_in,
            name,
            ..Self::default()
        }
    }
}

/// The allegiance as the server states it (`ac-proto` `AllegianceUpdate`,
/// written as OpenAC's `ParityServer.AllegianceUpdate` writes it: profile
/// version 8, no officers, the monarch then each record under its parent).
pub fn allegiance_update(
    rank: u32,
    total_members: u32,
    total_vassals: u32,
    name: &str,
    monarch: &AllegianceMember,
    records: &[AllegianceMember],
) -> Vec<u8> {
    fn member(w: &mut Writer, m: &AllegianceMember) {
        const LOGGED_IN: u32 = 0x1;
        const HAS_PACKED_LEVEL: u32 = 0x8;
        const MAY_PASSUP_EXPERIENCE: u32 = 0x10;
        let bits =
            HAS_PACKED_LEVEL | if m.logged_in { LOGGED_IN } else { 0 } | MAY_PASSUP_EXPERIENCE;
        w.u32(m.id);
        w.u32(0);
        w.u32(0);
        w.u32(bits);
        w.u8(m.gender);
        w.u8(m.heritage_group);
        w.u16(m.rank);
        w.u32(m.level);
        w.u16(0);
        w.u16(0);
        w.u32(0);
        w.u32(0);
        w.string16l(m.name).unwrap();
    }
    let mut w = Writer::new();
    w.u32(rank);
    w.u32(total_members);
    w.u32(total_vassals);
    w.u16(u16::try_from(records.len() + 1).unwrap());
    w.u16(8);
    // No officers.
    w.u16(0);
    w.u16(0);
    for _ in 0..4 {
        w.u32(0);
    }
    w.string16l("").unwrap();
    w.string16l("").unwrap();
    w.u32(0);
    for _ in 0..8 {
        w.u32(0);
    }
    w.string16l(name).unwrap();
    w.u32(0);
    member(&mut w, monarch);
    for record in records {
        w.u32(record.parent);
        member(&mut w, record);
    }
    w.into_vec()
}

/// The whole fellowship as the server states it (`ac-proto`
/// `FellowshipFullUpdate`, written as OpenAC's
/// `ParityServer.FellowshipFullUpdate` writes it): every member at full
/// vitals of 100 taking a share of the loot, nobody departed.
pub fn fellowship_full_update(
    name: &str,
    leader: u32,
    share_experience: bool,
    even_split: bool,
    members: &[(u32, &str, u32)],
) -> Vec<u8> {
    let mut w = Writer::new();
    w.u16(u16::try_from(members.len()).unwrap());
    w.u16(0);
    for (guid, member_name, level) in members {
        w.u32(*guid);
        w.u32(0);
        w.u32(0);
        w.u32(*level);
        for _ in 0..6 {
            w.u32(100);
        }
        w.u32(1);
        w.string16l(member_name).unwrap();
    }
    w.string16l(name).unwrap();
    w.u32(leader);
    w.u32(u32::from(share_experience));
    w.u32(u32::from(even_split));
    w.u32(0);
    w.u32(0);
    w.u16(0);
    w.u16(0);
    w.into_vec()
}

/// A line of text from the server itself (`ac-proto` `ServerMessage`).
pub fn server_message(text: &str, chat_type: u32) -> Vec<u8> {
    let mut w = Writer::new();
    w.u32(0xF7E0);
    w.string16l(text).unwrap();
    w.u32(chat_type);
    w.into_vec()
}

/// A line on a numbered chat channel (`ac-proto` `ChannelBroadcast`).
pub fn channel_broadcast(channel: u32, sender: &str, text: &str) -> Vec<u8> {
    let mut w = Writer::new();
    w.u32(channel);
    w.string16l(sender).unwrap();
    w.string16l(text).unwrap();
    w.into_vec()
}

/// A tell to its listener (`ac-proto` `Tell` event).
pub fn tell(
    text: &str,
    sender: &str,
    sender_guid: u32,
    target_guid: u32,
    chat_type: u32,
) -> Vec<u8> {
    let mut w = Writer::new();
    w.string16l(text).unwrap();
    w.string16l(sender).unwrap();
    w.u32(sender_guid);
    w.u32(target_guid);
    w.u32(chat_type);
    w.into_vec()
}

/// A line said at range (a shout; `ac-proto` `HearRangedSpeech`).
pub fn ranged_speech(
    sender: &str,
    text: &str,
    sender_guid: u32,
    chat_type: u32,
    range: f32,
) -> Vec<u8> {
    let mut w = Writer::new();
    w.u32(0x02BC);
    w.string16l(text).unwrap();
    w.string16l(sender).unwrap();
    w.u32(sender_guid);
    w.f32(range);
    w.u32(chat_type);
    w.into_vec()
}

/// `guid` is now playing `forward` (a motion command's low 16 bits, e.g.
/// walk forward 0x0005) at `speed` in the non-combat stance (`ac-proto`
/// `UpdateMotion` with an interpreted state).
pub fn interpreted_motion(guid: u32, movement_sequence: u16, forward: u16, speed: f32) -> Vec<u8> {
    let mut w = Writer::new();
    w.u32(0xF74C);
    w.u32(guid);
    // Instance, movement and server-control counters; not autonomous.
    w.u16(1);
    w.u16(movement_sequence);
    w.u16(1);
    w.u8(0);
    w.align(4);
    // Interpreted, no motion flags, the non-combat stance.
    w.u8(0);
    w.u8(0);
    w.u16(0x003D);
    // Stance, forward command and forward speed; no actions.
    w.u32(0x01 | 0x02 | 0x04);
    w.u16(0x003D);
    w.u16(forward);
    w.f32(speed);
    w.align(4);
    w.into_vec()
}

/// The server orders `guid` to walk to `target` (`ac-proto` `UpdateMotion`
/// with a move-to-object movement, OpenAC `ParityServer.MoveToObject`): from
/// `origin` in `cell`, stopping `distance` metres off, giving up at 50,
/// running above a metre, at run rate 1.
pub fn move_to_object(
    guid: u32,
    movement_sequence: u16,
    target: u32,
    cell: u32,
    origin: (f32, f32, f32),
    distance: f32,
) -> Vec<u8> {
    let mut w = Writer::new();
    w.u32(0xF74C);
    w.u32(guid);
    // Instance, movement and server-control counters; not autonomous.
    w.u16(1);
    w.u16(movement_sequence);
    w.u16(movement_sequence);
    w.u8(0);
    w.align(4);
    // Move to an object, no motion flags, the default stance.
    w.u8(6);
    w.u8(0);
    w.u16(0x003D);
    w.u32(target);
    w.u32(cell);
    w.f32(origin.0);
    w.f32(origin.1);
    w.f32(origin.2);
    // Can walk and can charge (OpenAC's `MoveToFlags`).
    w.u32(0x2 | 0x10);
    for v in [distance, 0.0, 50.0, 1.0, 1.0, 0.0, 1.0] {
        w.f32(v);
    }
    w.into_vec()
}
