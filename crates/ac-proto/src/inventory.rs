//! Items and containers: use, pick up, wield, drop, give, stack merges and
//! splits, shortcuts, inscriptions and salvage, and the events that confirm
//! or refuse them.
//!
//! Game-action builders take the session's next action sequence number
//! and are sent on the UI queue.

use ac_bits::Reader;

use crate::wire::{action, check_count, read_count, u32_action};
use crate::{GameActionType, ProtoError, Result, Shortcut};

/// Use an object.
#[must_use]
pub fn build_use(sequence: u32, target_guid: u32) -> Vec<u8> {
    u32_action(sequence, GameActionType::USE, target_guid)
}

/// Use one item on another: source, then target.
#[must_use]
pub fn build_use_with_target(sequence: u32, source_guid: u32, target_guid: u32) -> Vec<u8> {
    let mut w = action(sequence, GameActionType::USE_WITH_TARGET);
    w.u32(source_guid);
    w.u32(target_guid);
    w.into_vec()
}

/// Pick an item up or move it into a container: item, container, then the
/// signed slot placement.
#[must_use]
pub fn build_put_item_in_container(
    sequence: u32,
    item_guid: u32,
    container_guid: u32,
    placement: i32,
) -> Vec<u8> {
    let mut w = action(sequence, GameActionType::PUT_ITEM_IN_CONTAINER);
    w.u32(item_guid);
    w.u32(container_guid);
    w.i32(placement);
    w.into_vec()
}

/// Wield an item in the slots of `equip_mask`.
#[must_use]
pub fn build_get_and_wield_item(sequence: u32, item_guid: u32, equip_mask: u32) -> Vec<u8> {
    let mut w = action(sequence, GameActionType::GET_AND_WIELD_ITEM);
    w.u32(item_guid);
    w.u32(equip_mask);
    w.into_vec()
}

/// Drop an item on the ground.
#[must_use]
pub fn build_drop_item(sequence: u32, item_guid: u32) -> Vec<u8> {
    u32_action(sequence, GameActionType::DROP_ITEM, item_guid)
}

/// Merge `amount` from one stack into another: from, to, amount.
#[must_use]
pub fn build_stackable_merge(sequence: u32, from_guid: u32, to_guid: u32, amount: u32) -> Vec<u8> {
    let mut w = action(sequence, GameActionType::STACKABLE_MERGE);
    w.u32(from_guid);
    w.u32(to_guid);
    w.u32(amount);
    w.into_vec()
}

/// Split `amount` off a stack into a container slot: stack, container,
/// placement, amount.
#[must_use]
pub fn build_stackable_split_to_container(
    sequence: u32,
    stack_guid: u32,
    container_guid: u32,
    placement: u32,
    amount: u32,
) -> Vec<u8> {
    let mut w = action(sequence, GameActionType::STACKABLE_SPLIT_TO_CONTAINER);
    w.u32(stack_guid);
    w.u32(container_guid);
    w.u32(placement);
    w.u32(amount);
    w.into_vec()
}

/// Split `amount` off a stack onto the ground.
#[must_use]
pub fn build_stackable_split_to_3d(sequence: u32, stack_guid: u32, amount: u32) -> Vec<u8> {
    let mut w = action(sequence, GameActionType::STACKABLE_SPLIT_TO_3D);
    w.u32(stack_guid);
    w.u32(amount);
    w.into_vec()
}

/// Split `amount` off a stack into a wield slot: stack, slot, amount.
#[must_use]
pub fn build_stackable_split_to_wield(
    sequence: u32,
    stack_guid: u32,
    equip_location: u32,
    amount: u32,
) -> Vec<u8> {
    let mut w = action(sequence, GameActionType::STACKABLE_SPLIT_TO_WIELD);
    w.u32(stack_guid);
    w.u32(equip_location);
    w.u32(amount);
    w.into_vec()
}

/// Give `amount` of an item to another object: target, item, amount.
#[must_use]
pub fn build_give_object(sequence: u32, target_guid: u32, item_guid: u32, amount: u32) -> Vec<u8> {
    let mut w = action(sequence, GameActionType::GIVE_OBJECT_REQUEST);
    w.u32(target_guid);
    w.u32(item_guid);
    w.u32(amount);
    w.into_vec()
}

/// Put an entry on the shortcut bar: signed slot, object, spell word.
#[must_use]
pub fn build_add_shortcut(sequence: u32, shortcut: &Shortcut) -> Vec<u8> {
    let mut w = action(sequence, GameActionType::ADD_SHORTCUT);
    w.i32(shortcut.index);
    w.u32(shortcut.object_id);
    w.u32(shortcut.spell_id);
    w.into_vec()
}

/// Clear a shortcut bar slot.
#[must_use]
pub fn build_remove_shortcut(sequence: u32, slot_index: u32) -> Vec<u8> {
    u32_action(sequence, GameActionType::REMOVE_SHORTCUT, slot_index)
}

/// Stop viewing a container's contents.
#[must_use]
pub fn build_no_longer_viewing_contents(sequence: u32, container_guid: u32) -> Vec<u8> {
    u32_action(
        sequence,
        GameActionType::NO_LONGER_VIEWING_CONTENTS,
        container_guid,
    )
}

/// Inscribe an item (empty text clears the inscription).
pub fn build_set_inscription(sequence: u32, item_guid: u32, inscription: &str) -> Result<Vec<u8>> {
    let mut w = action(sequence, GameActionType::SET_INSCRIPTION);
    w.u32(item_guid);
    w.string16l(inscription)?;
    Ok(w.into_vec())
}

/// Salvage items with a tool: tool, count, then the items. The tool and
/// every item must be non-zero and the list must not be empty.
pub fn build_create_tinkering_tool(
    sequence: u32,
    tool_guid: u32,
    item_guids: &[u32],
) -> Result<Vec<u8>> {
    if tool_guid == 0 {
        return Err(ProtoError::InvalidArgument {
            what: "salvage tool id",
        });
    }
    if item_guids.is_empty() || item_guids.contains(&0) {
        return Err(ProtoError::InvalidArgument {
            what: "salvage item ids",
        });
    }
    let mut w = action(sequence, GameActionType::CREATE_TINKERING_TOOL);
    w.u32(tool_guid);
    w.u32(item_guids.len() as u32);
    for &item in item_guids {
        w.u32(item);
    }
    Ok(w.into_vec())
}

/// The server wielded an item ([`crate::GameEventType::WIELD_OBJECT`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct WieldObject {
    /// The item.
    pub item_guid: u32,
    /// The slot bits.
    pub equip_location: u32,
}

impl WieldObject {
    /// Parse the event payload: item, slot bits.
    pub fn parse(payload: &[u8]) -> Result<Self> {
        let mut r = Reader::new(payload);
        Ok(Self {
            item_guid: r.u32()?,
            equip_location: r.u32()?,
        })
    }
}

/// The server put an item into a container
/// ([`crate::GameEventType::INVENTORY_PUT_OBJ_IN_CONTAINER`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct PutObjInContainer {
    /// The item.
    pub item_guid: u32,
    /// The container.
    pub container_guid: u32,
    /// Slot placement.
    pub placement: u32,
    /// Container type of the item (0 item, 1 container, 2 foci).
    pub container_type: u32,
}

impl PutObjInContainer {
    /// Parse the event payload: item, container, placement, type.
    pub fn parse(payload: &[u8]) -> Result<Self> {
        let mut r = Reader::new(payload);
        Ok(Self {
            item_guid: r.u32()?,
            container_guid: r.u32()?,
            placement: r.u32()?,
            container_type: r.u32()?,
        })
    }
}

/// One entry of a container's contents.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ContentsEntry {
    /// The object.
    pub guid: u32,
    /// Its container type.
    pub container_type: u32,
}

/// A container's full contents ([`crate::GameEventType::VIEW_CONTENTS`]);
/// they replace whatever the client held for it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ViewContents {
    /// The container.
    pub container_guid: u32,
    /// Its contents, in wire order.
    pub items: Vec<ContentsEntry>,
}

impl ViewContents {
    /// Parse the event payload: container, count, then id and type each.
    pub fn parse(payload: &[u8]) -> Result<Self> {
        let mut r = Reader::new(payload);
        let container_guid = r.u32()?;
        let count = read_count(&mut r, 8, "container contents")?;
        let items = (0..count)
            .map(|_| {
                Ok(ContentsEntry {
                    guid: r.u32()?,
                    container_type: r.u32()?,
                })
            })
            .collect::<Result<_>>()?;
        Ok(Self {
            container_guid,
            items,
        })
    }
}

/// An optimistic inventory move the server refused
/// ([`crate::GameEventType::INVENTORY_SERVER_SAVE_FAILED`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct InventoryServerSaveFailed {
    /// The item to put back.
    pub item_guid: u32,
    /// Why (an error code).
    pub weenie_error: u32,
}

impl InventoryServerSaveFailed {
    /// Parse the event payload: item, error code.
    pub fn parse(payload: &[u8]) -> Result<Self> {
        let mut r = Reader::new(payload);
        Ok(Self {
            item_guid: r.u32()?,
            weenie_error: r.u32()?,
        })
    }
}

/// The single word of a use-done ([`crate::GameEventType::USE_DONE`], an
/// error code, 0 for success), put-in-3D
/// ([`crate::GameEventType::INVENTORY_PUT_OBJECT_IN_3D`], the item) or
/// close-ground-container ([`crate::GameEventType::CLOSE_GROUND_CONTAINER`],
/// the container) event.
pub fn parse_inventory_word(payload: &[u8]) -> Result<u32> {
    Ok(Reader::new(payload).u32()?)
}

/// One kind of salvage produced.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct SalvageResult {
    /// Material type.
    pub material_type: u32,
    /// Workmanship.
    pub workmanship: f64,
    /// Units produced.
    pub units: u32,
}

/// What a salvage produced
/// ([`crate::GameEventType::SALVAGE_OPERATIONS_RESULT`]).
#[derive(Debug, Clone, PartialEq)]
pub struct SalvageOperationsResult {
    /// The salvaging skill used.
    pub skill_id: u32,
    /// Items that could not be salvaged.
    pub unsuitable_item_guids: Vec<u32>,
    /// The salvage produced.
    pub results: Vec<SalvageResult>,
    /// Bonus from augmentations, percent.
    pub augmentation_bonus_percent: i32,
}

impl SalvageOperationsResult {
    /// Parse the event payload: skill, the unsuitable item list, the result
    /// list (material, workmanship, units each), the bonus.
    pub fn parse(payload: &[u8]) -> Result<Self> {
        let mut r = Reader::new(payload);
        let skill_id = r.u32()?;
        let count = read_count(&mut r, 4, "unsuitable salvage items")?;
        let unsuitable_item_guids = (0..count)
            .map(|_| r.u32())
            .collect::<ac_bits::Result<_>>()?;
        let count = r.u32()?;
        // Sixteen bytes per result, and the bonus word after them.
        check_count(&r, count, 16, "salvage results")?;
        let results = (0..count)
            .map(|_| {
                Ok(SalvageResult {
                    material_type: r.u32()?,
                    workmanship: r.f64()?,
                    units: r.u32()?,
                })
            })
            .collect::<Result<_>>()?;
        Ok(Self {
            skill_id,
            unsuitable_item_guids,
            results,
            augmentation_bonus_percent: r.i32()?,
        })
    }
}

#[cfg(test)]
mod tests {
    //! Cases ported from OpenAC `tests/AcDream.Core.Net.Tests/Messages/`
    //! `InventoryActionsTests.cs`, `InteractRequestsTests.cs`,
    //! `GameEventsInventoryTests.cs`, and the body shapes of
    //! `WorldSessionInventoryActionTests.cs`.

    use ac_bits::Writer;

    use super::*;
    use crate::test_util::{assert_every_prefix_fails, words};

    #[test]
    fn use_and_container_requests() {
        assert_eq!(build_use(2, 0xDEAD), words(&[0xF7B1, 2, 0x36, 0xDEAD]));
        assert_eq!(
            build_use_with_target(3, 0xA, 0xB),
            words(&[0xF7B1, 3, 0x35, 0xA, 0xB])
        );
        assert_eq!(
            build_put_item_in_container(5, 0xABCD, 0x5000_000A, 0),
            words(&[0xF7B1, 5, 0x19, 0xABCD, 0x5000_000A, 0])
        );
        let body = build_put_item_in_container(1, 1, 2, -1);
        assert_eq!(body[20..], (-1i32).to_le_bytes());
        assert_eq!(
            build_get_and_wield_item(7, 0x5000_0A01, 2),
            words(&[0xF7B1, 7, 0x1A, 0x5000_0A01, 2])
        );
        assert_eq!(
            build_drop_item(7, 0x5000_0A01),
            words(&[0xF7B1, 7, 0x1B, 0x5000_0A01])
        );
        assert_eq!(
            build_no_longer_viewing_contents(7, 0x5000_00C9),
            words(&[0xF7B1, 7, 0x0195, 0x5000_00C9])
        );
    }

    #[test]
    fn stack_and_give_requests() {
        assert_eq!(
            build_stackable_merge(1, 0xAA, 0xBB, 5),
            words(&[0xF7B1, 1, 0x54, 0xAA, 0xBB, 5])
        );
        assert_eq!(
            build_stackable_split_to_container(1, 0xAA, 0xBB, 4, 2),
            words(&[0xF7B1, 1, 0x55, 0xAA, 0xBB, 4, 2])
        );
        assert_eq!(
            build_stackable_split_to_3d(1, 0xAA, 2),
            words(&[0xF7B1, 1, 0x56, 0xAA, 2])
        );
        assert_eq!(
            build_stackable_split_to_wield(1, 0xAA, 0x200, 2),
            words(&[0xF7B1, 1, 0x019B, 0xAA, 0x200, 2])
        );
        assert_eq!(
            build_give_object(1, 0xAA, 0xBB, 3),
            words(&[0xF7B1, 1, 0xCD, 0xAA, 0xBB, 3])
        );
    }

    #[test]
    fn shortcut_requests() {
        let body = build_add_shortcut(
            1,
            &Shortcut {
                index: 0,
                object_id: 0x3E1,
                spell_id: 0,
            },
        );
        assert_eq!(body, words(&[0xF7B1, 1, 0x019C, 0, 0x3E1, 0]));
        let body = build_add_shortcut(
            1,
            &Shortcut {
                index: -1,
                object_id: 0,
                spell_id: 0xA5C3_1234,
            },
        );
        assert_eq!(body[12..16], (-1i32).to_le_bytes());
        assert_eq!(body[20..], 0xA5C3_1234u32.to_le_bytes());
        assert_eq!(build_remove_shortcut(1, 5), words(&[0xF7B1, 1, 0x019D, 5]));
    }

    #[test]
    fn inscription_requests() {
        let body = build_set_inscription(7, 0x5000_0A01, "Café").unwrap();
        assert_eq!(body.len(), 24);
        assert_eq!(body[..16], words(&[0xF7B1, 7, 0xBF, 0x5000_0A01]));
        assert_eq!(body[16..], [4, 0, 0x43, 0x61, 0x66, 0xE9, 0, 0]);
        let clear = build_set_inscription(1, 0x5000_0A01, "").unwrap();
        assert_eq!(clear.len(), 20);
        assert_eq!(clear[16..], [0, 0, 0, 0]);
    }

    #[test]
    fn salvage_request_and_its_checks() {
        assert_eq!(
            build_create_tinkering_tool(7, 0x5000_0010, &[0x5000_0020, 0x5000_0030]).unwrap(),
            words(&[0xF7B1, 7, 0x027D, 0x5000_0010, 2, 0x5000_0020, 0x5000_0030])
        );
        assert!(build_create_tinkering_tool(7, 0, &[1]).is_err());
        assert!(build_create_tinkering_tool(7, 1, &[]).is_err());
        assert!(build_create_tinkering_tool(7, 1, &[2, 0]).is_err());
    }

    #[test]
    fn container_events() {
        let contents = words(&[0x5000_00C9, 2, 0x5000_0A01, 0, 0x5000_0A02, 1]);
        let parsed = ViewContents::parse(&contents).unwrap();
        assert_eq!(parsed.container_guid, 0x5000_00C9);
        assert_eq!(
            parsed.items,
            [
                ContentsEntry {
                    guid: 0x5000_0A01,
                    container_type: 0
                },
                ContentsEntry {
                    guid: 0x5000_0A02,
                    container_type: 1
                }
            ]
        );
        assert_every_prefix_fails(&contents, ViewContents::parse);
        assert!(
            ViewContents::parse(&words(&[0x5000_00C9, 0]))
                .unwrap()
                .items
                .is_empty()
        );
        let put = words(&[0x5000_0A01, 0x5000_00C9, 3, 1]);
        assert_eq!(
            PutObjInContainer::parse(&put),
            Ok(PutObjInContainer {
                item_guid: 0x5000_0A01,
                container_guid: 0x5000_00C9,
                placement: 3,
                container_type: 1
            })
        );
        assert_every_prefix_fails(&put, PutObjInContainer::parse);
        assert_eq!(
            InventoryServerSaveFailed::parse(&words(&[0x5000_0A01, 0x0A])),
            Ok(InventoryServerSaveFailed {
                item_guid: 0x5000_0A01,
                weenie_error: 0x0A
            })
        );
        assert_eq!(
            WieldObject::parse(&words(&[0x5000_0A01, 2])),
            Ok(WieldObject {
                item_guid: 0x5000_0A01,
                equip_location: 2
            })
        );
        assert_every_prefix_fails(&words(&[1, 2]), WieldObject::parse);
        assert_eq!(
            parse_inventory_word(&words(&[0x5000_0A01])),
            Ok(0x5000_0A01)
        );
        assert!(parse_inventory_word(&[]).is_err());
    }

    #[test]
    fn salvage_result() {
        let mut w = Writer::new();
        w.u32(28);
        w.u32(1);
        w.u32(0x5000_0020);
        w.u32(1);
        w.u32(63);
        w.f64(8.0);
        w.u32(1);
        w.i32(25);
        let result = SalvageOperationsResult::parse(w.as_slice()).unwrap();
        assert_eq!(result.skill_id, 28);
        assert_eq!(result.unsuitable_item_guids, [0x5000_0020]);
        assert_eq!(
            result.results,
            [SalvageResult {
                material_type: 63,
                workmanship: 8.0,
                units: 1
            }]
        );
        assert_eq!(result.augmentation_bonus_percent, 25);
        assert_every_prefix_fails(w.as_slice(), SalvageOperationsResult::parse);
        assert!(SalvageOperationsResult::parse(&words(&[28, 0, 1, 63])).is_err());
    }
}
