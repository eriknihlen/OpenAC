//! Objects, icons and output readers shared by the item panel tests.

use ac_core::items::{ClientObject, ClientObjectTable, EquipMask, ItemType, TableEvent};
use ac_runtime::inventory::{ItemCommand, ObjectRef};
use ac_runtime::session::SessionCommand;
use ac_ui::ElementId;
use ac_ui::draw::TextureRef;
use ac_ui::event::{ItemDragPayload, ItemDragSource};
use ac_ui::host::UiHost;
use ac_ui::icons::IconSource;
use ac_ui::panels::PanelOutput;
use ac_ui::root::{UiOutput, UiRoot};
use ac_ui::tree::Element;
use ac_ui::widgets::ItemList;

/// The player's object id.
pub const PLAYER: u32 = 0x5000_0001;

/// Gives every icon a texture named after its icon id (drag icons offset by
/// 0x1000_0000), so tests can tell which icon a cell asked for.
#[derive(Debug, Default)]
pub struct TestIcons {
    /// Item icons asked for: (type, icon).
    pub asked: Vec<(ItemType, u32)>,
}

impl IconSource for TestIcons {
    fn item_icon(&mut self, t: ItemType, icon: u32, _: u32, _: u32, _: u32) -> Option<TextureRef> {
        self.asked.push((t, icon));
        (icon != 0).then_some(TextureRef::Runtime(icon))
    }
    fn drag_icon(&mut self, _: ItemType, icon: u32, _: u32, _: u32, _: u32) -> Option<TextureRef> {
        (icon != 0).then_some(TextureRef::Runtime(0x1000_0000 | icon))
    }
    fn spell_icon(&mut self, id: u32) -> Option<TextureRef> {
        Some(TextureRef::Runtime(0x2000_0000 | id))
    }
    fn component_icon(&mut self, icon: u32) -> Option<TextureRef> {
        Some(TextureRef::Runtime(icon))
    }
}

/// An object table and the events it raised.
pub struct Objects {
    /// The table.
    pub t: ClientObjectTable,
    /// Its events.
    pub ev: Vec<TableEvent>,
}

impl Objects {
    /// An empty table.
    pub fn new() -> Self {
        Self {
            t: ClientObjectTable::new(),
            ev: Vec::new(),
        }
    }

    /// Add or replace `item`.
    pub fn add(&mut self, item: ClientObject) {
        self.t.add_or_update(item, &mut self.ev);
    }

    /// Move `id` into `container` at `slot`.
    pub fn mv(&mut self, id: u32, container: u32, slot: i32) {
        self.t
            .move_item(id, container, slot, EquipMask::NONE, None, &mut self.ev);
    }

    /// The player, with these capacities.
    pub fn player(&mut self, items_capacity: i32, containers_capacity: i32) {
        let mut p = ClientObject::new(PLAYER);
        p.name = "Tester".into();
        p.item_type = ItemType::CREATURE;
        p.items_capacity = items_capacity;
        p.containers_capacity = containers_capacity;
        self.add(p);
    }

    /// An item of `item_type` in `container` at `slot` (icon 0x06000000 |
    /// guid).
    pub fn contained(&mut self, guid: u32, container: u32, slot: i32, item_type: ItemType) {
        let mut o = ClientObject::new(guid);
        o.item_type = item_type;
        o.icon_id = 0x0600_0000 | guid;
        self.add(o);
        self.mv(guid, container, slot);
    }

    /// A side pack in the player's main pack.
    pub fn bag(&mut self, bag: u32, slot: i32, items_capacity: i32) {
        let mut o = ClientObject::new(bag);
        o.name = format!("Pack {bag:X}");
        o.item_type = ItemType::CONTAINER;
        o.items_capacity = items_capacity;
        self.add(o);
        self.mv(bag, PLAYER, slot);
    }

    /// An item the player wears at `location`.
    pub fn equipped(&mut self, guid: u32, location: EquipMask) {
        let mut o = ClientObject::new(guid);
        o.icon_id = 0x0600_0000 | guid;
        o.wielder_id = PLAYER;
        o.currently_equipped_location = location;
        o.valid_locations = location;
        self.add(o);
    }

    /// An item in the main pack that can be worn at `valid`.
    pub fn pack_item(&mut self, guid: u32, valid: EquipMask) {
        let mut o = ClientObject::new(guid);
        o.valid_locations = valid;
        self.add(o);
        self.mv(guid, PLAYER, 0);
    }

    /// Object `id`.
    pub fn get_mut(&mut self, id: u32) -> &mut ClientObject {
        self.t.get_mut(id).unwrap()
    }
}

/// The union of `bits`.
pub fn mask_of(bits: &[EquipMask]) -> EquipMask {
    EquipMask(bits.iter().fold(0, |a, b| a | b.bits()))
}

/// An item list under `parent`.
pub fn list(ui: &mut UiRoot, parent: ElementId, x: f32, y: f32, w: f32, h: f32) -> ElementId {
    ItemList::create(ui, parent, Element::at(x, y, w, h))
}

/// The panel outputs the host queued.
pub fn panel_outputs(host: &mut UiHost) -> Vec<PanelOutput> {
    host.take_outputs()
        .into_iter()
        .filter_map(|o| match o {
            UiOutput::Panel(p) => Some(p),
            _ => None,
        })
        .collect()
}

/// The session commands among `outputs`.
pub fn sessions(outputs: &[PanelOutput]) -> Vec<SessionCommand> {
    outputs
        .iter()
        .filter_map(|o| match o {
            PanelOutput::Session(c) => Some(c.clone()),
            _ => None,
        })
        .collect()
}

/// The objects used.
pub fn uses(outputs: &[PanelOutput]) -> Vec<u32> {
    sessions(outputs)
        .into_iter()
        .filter_map(|c| match c {
            SessionCommand::Item(ItemCommand::Use(ObjectRef::Guid(g))) => Some(g),
            _ => None,
        })
        .collect()
}

/// The moves (a window drop of the player's item, or a plain move):
/// (item, container, slot, amount).
pub fn moves(outputs: &[PanelOutput]) -> Vec<(u32, ObjectRef, i32, u32)> {
    sessions(outputs)
        .into_iter()
        .filter_map(|c| match c {
            SessionCommand::Item(
                ItemCommand::MoveToContainer {
                    item: ObjectRef::Guid(i),
                    container,
                    slot,
                    amount,
                    join_stack: false,
                }
                | ItemCommand::DropInto {
                    item: ObjectRef::Guid(i),
                    container,
                    slot,
                    amount,
                    pickup: false,
                    join_stack: false,
                },
            ) => Some((i, container, slot, amount)),
            _ => None,
        })
        .collect()
}

/// The wields: (item, location bits).
pub fn wields(outputs: &[PanelOutput]) -> Vec<(u32, u32)> {
    sessions(outputs)
        .into_iter()
        .filter_map(|c| match c {
            SessionCommand::Item(ItemCommand::Wield {
                item: ObjectRef::Guid(i),
                location,
            }) => Some((i, location)),
            _ => None,
        })
        .collect()
}

/// The client-local notices.
pub fn notices(outputs: &[PanelOutput]) -> Vec<String> {
    outputs
        .iter()
        .filter_map(|o| match o {
            PanelOutput::Notice(n) => Some(n.clone()),
            _ => None,
        })
        .collect()
}

/// A drag of `obj` from an inventory slot.
pub fn payload(obj: u32) -> ItemDragPayload {
    ItemDragPayload::new(obj, ItemDragSource::Inventory, 0)
}
