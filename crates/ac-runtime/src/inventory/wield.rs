//! Wielding: which slot an item goes to, and the switch that first moves a
//! blocking item (the weapon in hand, a shield a two-handed weapon cannot
//! share, mismatched ammunition) to the backpack and then wields the new
//! one when the server confirms the move.

use ac_core::combat::CombatMode;
use ac_core::items::{
    ClientObject, CombatUse, EquipMask, InventoryRequestKind, ItemType, TableEvent, placement,
};
use ac_proto::combat::build_change_combat_mode;
use ac_proto::inventory::{build_get_and_wield_item, build_put_item_in_container};

use super::{Inventory, ItemContext, Notice};

/// The weapon-ready slots.
pub const WEAPON_READY_MASK: EquipMask = EquipMask(
    EquipMask::MELEE_WEAPON.0
        | EquipMask::MISSILE_WEAPON.0
        | EquipMask::HELD.0
        | EquipMask::TWO_HANDED.0,
);

/// The slots worn without choosing (clothing, armor, the cloak).
pub const AUTO_WEAR_MASK: EquipMask = EquipMask(
    EquipMask::HEAD_WEAR.0
        | EquipMask::CHEST_WEAR.0
        | EquipMask::ABDOMEN_WEAR.0
        | EquipMask::UPPER_ARM_WEAR.0
        | EquipMask::LOWER_ARM_WEAR.0
        | EquipMask::HAND_WEAR.0
        | EquipMask::UPPER_LEG_WEAR.0
        | EquipMask::LOWER_LEG_WEAR.0
        | EquipMask::FOOT_WEAR.0
        | EquipMask::CHEST_ARMOR.0
        | EquipMask::ABDOMEN_ARMOR.0
        | EquipMask::UPPER_ARM_ARMOR.0
        | EquipMask::LOWER_ARM_ARMOR.0
        | EquipMask::UPPER_LEG_ARMOR.0
        | EquipMask::LOWER_LEG_ARMOR.0
        | EquipMask::CLOAK.0,
);

/// The order free slots are tried in.
pub const AUTO_EQUIP_ORDER: [EquipMask; 31] = [
    EquipMask::HEAD_WEAR,
    EquipMask::CHEST_WEAR,
    EquipMask::ABDOMEN_WEAR,
    EquipMask::UPPER_ARM_WEAR,
    EquipMask::LOWER_ARM_WEAR,
    EquipMask::HAND_WEAR,
    EquipMask::UPPER_LEG_WEAR,
    EquipMask::LOWER_LEG_WEAR,
    EquipMask::FOOT_WEAR,
    EquipMask::CHEST_ARMOR,
    EquipMask::ABDOMEN_ARMOR,
    EquipMask::UPPER_ARM_ARMOR,
    EquipMask::LOWER_ARM_ARMOR,
    EquipMask::UPPER_LEG_ARMOR,
    EquipMask::LOWER_LEG_ARMOR,
    EquipMask::NECK_WEAR,
    EquipMask::WRIST_WEAR_LEFT,
    EquipMask::WRIST_WEAR_RIGHT,
    EquipMask::FINGER_WEAR_LEFT,
    EquipMask::FINGER_WEAR_RIGHT,
    EquipMask::SHIELD,
    EquipMask::MISSILE_AMMO,
    EquipMask::MELEE_WEAPON,
    EquipMask::MISSILE_WEAPON,
    EquipMask::HELD,
    EquipMask::TWO_HANDED,
    EquipMask::TRINKET_ONE,
    EquipMask::CLOAK,
    EquipMask::SIGIL_ONE,
    EquipMask::SIGIL_TWO,
    EquipMask::SIGIL_THREE,
];

/// Whether the item is clothing or armor (worn without choosing a slot).
#[must_use]
pub fn is_auto_wear_item(item: &ClientObject) -> bool {
    item.valid_locations.intersects(AUTO_WEAR_MASK)
}

/// A switch in flight: the item asked for, the item being moved out of its
/// way first (0 when the wield itself is in flight), the slots asked for.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
struct PendingSwitch {
    requested: u32,
    blocking: u32,
    mask: EquipMask,
    secondary: bool,
    /// The weapon was wielded in combat: the stance to be back in after it.
    combat_mode_after_wield: Option<CombatMode>,
}

/// Where a wield made in combat stands in the server's stance shuffle.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum SettlementPhase {
    /// The wield is confirmed; the server's mode change is awaited.
    AwaitingPostWieldMode,
    /// The server dropped to peace on the way.
    SawTransitionalPeace,
    /// The server came back to the ready mode after that peace.
    SawReadyAfterPeace,
}

/// A wield made in combat, settling: when the server ends the shuffle at
/// peace after having reached the ready mode, the ready mode is asked for
/// again.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
struct CombatSettlement {
    item_id: u32,
    ready_mode: CombatMode,
    phase: SettlementPhase,
    expect_trailing_peace: bool,
}

/// The switch in flight, and the stance settlement after a wield in
/// combat.
#[derive(Debug, Default)]
pub(super) struct AutoWield {
    pending: Option<PendingSwitch>,
    settlement: Option<CombatSettlement>,
    transition_observed_during_switch: bool,
}

/// The combat mode a weapon in these slots readies.
fn combat_mode_for_weapon_location(location: EquipMask) -> Option<CombatMode> {
    if location.intersects(EquipMask::MISSILE_WEAPON) {
        Some(CombatMode::MISSILE)
    } else if location.intersects(EquipMask::HELD) {
        Some(CombatMode::MAGIC)
    } else if location.intersects(EquipMask::MELEE_WEAPON | EquipMask::TWO_HANDED) {
        Some(CombatMode::MELEE)
    } else {
        None
    }
}

impl AutoWield {
    pub(super) fn is_busy(&self) -> bool {
        self.pending.is_some()
    }
}

fn blocks_use_of_shield(item: &ClientObject) -> bool {
    let combat_use = item.combat_use.unwrap_or(0);
    combat_use == CombatUse::TWO_HANDED.bits() as u8
        || (combat_use == CombatUse::MISSILE.bits() as u8 && item.ammo_type.unwrap_or(0) > 0)
        || item.item_type.intersects(ItemType::CASTER)
}

impl Inventory {
    /// Wield `item_id` in `requested` slots, or (NONE) where it fits. True
    /// when a request went out or a switch is already in flight.
    pub(super) fn try_wield(
        &mut self,
        item_id: u32,
        requested: EquipMask,
        ctx: &mut ItemContext<'_>,
    ) -> bool {
        // A new wield the player asked for ends any stance settlement.
        self.wield.settlement = None;
        self.wield.transition_observed_during_switch = false;
        self.try_wield_for(item_id, requested, None, ctx)
    }

    fn try_wield_for(
        &mut self,
        item_id: u32,
        requested: EquipMask,
        combat_mode_after_wield: Option<CombatMode>,
        ctx: &mut ItemContext<'_>,
    ) -> bool {
        if self.wield.pending.is_some() {
            return true;
        }
        let Some(item) = ctx.world.item(item_id).cloned() else {
            return false;
        };
        if item.valid_locations == EquipMask::NONE {
            return false;
        }
        if requested != EquipMask::NONE && (requested & item.valid_locations) != requested {
            return false;
        }
        let player = ctx.player();
        if item.currently_equipped_location != EquipMask::NONE {
            if requested == EquipMask::NONE || requested == item.currently_equipped_location {
                return false;
            }
            if let Some(blocker) = equipped_at(ctx, player, requested, 0, item_id) {
                return self.begin_replacement(
                    item_id,
                    blocker,
                    requested,
                    false,
                    combat_mode_after_wield,
                    ctx,
                );
            }
            return self.send_wield(item_id, requested, combat_mode_after_wield, ctx);
        }

        if is_auto_wear_item(&item) {
            return match self.auto_wear_blocker(&item, ctx) {
                None => self.send_wield(item_id, item.valid_locations, None, ctx),
                Some(blocker) => {
                    if blocker != 0
                        && let Some(name) = ctx.world.item(blocker).map(|b| b.appropriate_name())
                    {
                        self.notices.push(Notice::Interface(format!(
                            "You must remove your {name} to wear that"
                        )));
                    }
                    false
                }
            };
        }

        let ready = if requested == EquipMask::NONE {
            item.valid_locations & WEAPON_READY_MASK
        } else {
            requested & WEAPON_READY_MASK
        };
        if ready != EquipMask::NONE {
            // Wielded in combat, the weapon's own stance is the one to end
            // in.
            let after = combat_mode_after_wield.or_else(|| {
                if ctx.in_peace {
                    None
                } else {
                    combat_mode_for_weapon_location(ready)
                }
            });
            if let Some(blocker) = equipped_at(ctx, player, WEAPON_READY_MASK, 0, item_id) {
                return self.begin_replacement(item_id, blocker, ready, false, after, ctx);
            }
            if blocks_use_of_shield(&item)
                && let Some(shield) = equipped_at(ctx, player, EquipMask::SHIELD, 0, item_id)
            {
                return self.begin_replacement(item_id, shield, ready, false, after, ctx);
            }
            if item.ammo_type.unwrap_or(0) > 0
                && let Some(ammo) = equipped_at(ctx, player, EquipMask::MISSILE_AMMO, 0, item_id)
                && ctx.world.item(ammo).and_then(|a| a.ammo_type) != item.ammo_type
            {
                return self.begin_replacement(item_id, ammo, ready, false, after, ctx);
            }
            return self.send_wield(item_id, ready, after, ctx);
        }

        let mask = if requested == EquipMask::NONE {
            AUTO_EQUIP_ORDER
                .into_iter()
                .find(|&mask| {
                    item.valid_locations.intersects(mask)
                        && equipped_at(ctx, player, mask, 0, item_id).is_none()
                })
                .unwrap_or(EquipMask::NONE)
        } else {
            requested
        };
        if mask == EquipMask::NONE {
            let first = AUTO_EQUIP_ORDER
                .into_iter()
                .find(|&mask| item.valid_locations.intersects(mask))
                .unwrap_or(EquipMask::NONE);
            return match equipped_at(ctx, player, first, 0, item_id) {
                Some(blocker) => self.begin_replacement(item_id, blocker, first, false, None, ctx),
                None => false,
            };
        }
        // Whatever already holds the shield slot (a shield or an off-hand
        // weapon) goes to the backpack first, and the wield follows.
        if mask.intersects(EquipMask::SHIELD)
            && let Some(blocker) = equipped_at(ctx, player, EquipMask::SHIELD, 0, item_id)
        {
            return self.begin_replacement(item_id, blocker, mask, false, None, ctx);
        }
        self.send_wield(item_id, mask, None, ctx)
    }

    /// Wield a weapon in the shield hand (dual wield).
    pub(super) fn try_wield_secondary(&mut self, item_id: u32, ctx: &mut ItemContext<'_>) -> bool {
        let Some(item) = ctx.world.item(item_id).cloned() else {
            return false;
        };
        self.wield.settlement = None;
        self.wield.transition_observed_during_switch = false;
        let side = EquipMask::MELEE_WEAPON | EquipMask::MISSILE_WEAPON | EquipMask::TWO_HANDED;
        if !item.valid_locations.intersects(side) {
            return self.try_wield(item_id, EquipMask::NONE, ctx);
        }
        if self.wield.pending.is_some() {
            return true;
        }
        if item.currently_equipped_location == EquipMask::SHIELD {
            return false;
        }
        match equipped_at(ctx, ctx.player(), EquipMask::SHIELD, 0, item_id) {
            Some(blocker) => {
                self.begin_replacement(item_id, blocker, EquipMask::SHIELD, true, None, ctx)
            }
            None => self.send_wield(item_id, EquipMask::SHIELD, None, ctx),
        }
    }

    /// `None` when the worn item may go on; otherwise the item in the way
    /// (0 when none is found to name).
    fn auto_wear_blocker(&self, item: &ClientObject, ctx: &ItemContext<'_>) -> Option<u32> {
        let player = ctx.player();
        let mut priorities = 0u32;
        let mut locations = EquipMask::NONE;
        for worn in ctx.world.items().get_equipped_by(player) {
            if worn.object_id() == item.object_id()
                || !worn.currently_equipped_location.intersects(AUTO_WEAR_MASK)
            {
                continue;
            }
            priorities |= worn.priority;
            locations |= worn.currently_equipped_location;
        }
        if item.priority & priorities == 0 {
            return None;
        }
        Some(
            equipped_at(
                ctx,
                player,
                locations & item.valid_locations,
                item.priority,
                item.object_id(),
            )
            .unwrap_or(0),
        )
    }

    fn begin_replacement(
        &mut self,
        requested: u32,
        blocker: u32,
        mask: EquipMask,
        secondary: bool,
        combat_mode_after_wield: Option<CombatMode>,
        ctx: &mut ItemContext<'_>,
    ) -> bool {
        let player = ctx.player();
        if player == 0 {
            return false;
        }
        // The server fills exactly the pack it is asked for, so the pack is
        // chosen here: the main pack when it has room, else the first side
        // pack with room. With no room anywhere nothing is sent and the
        // blocker stays where it is.
        let name = ctx.world.item(blocker).map(|b| b.appropriate_name());
        let destination = match placement::choose_container(
            ctx.world.items(),
            blocker,
            player,
            0,
            player,
            true,
        ) {
            Ok(destination) => destination,
            Err(no_room) => {
                let items = ctx.world.items();
                let full = placement::placement_rejection_text(
                    no_room,
                    items.get(blocker),
                    items.get(player),
                    player,
                );
                self.notices.push(Notice::Interface(full));
                if let Some(name) = name {
                    self.notices.push(Notice::Interface(format!(
                        "Moving {name} to your backpack - cannot unwield the {name}"
                    )));
                }
                return false;
            }
        };
        self.wield.pending = Some(PendingSwitch {
            requested,
            blocking: blocker,
            mask,
            secondary,
            combat_mode_after_wield,
        });
        if let Some(name) = name {
            self.notices
                .push(Notice::Interface(format!("Moving {name} to your backpack")));
        }
        let dispatched = self.dispatch(InventoryRequestKind::PutInContainer, blocker, ctx, |ctx| {
            ctx.send(|seq| build_put_item_in_container(seq, blocker, destination, 0))
        });
        if !dispatched {
            self.wield.pending = None;
        }
        dispatched
    }

    fn send_wield(
        &mut self,
        item_id: u32,
        mask: EquipMask,
        combat_mode_after_wield: Option<CombatMode>,
        ctx: &mut ItemContext<'_>,
    ) -> bool {
        self.wield.pending = Some(PendingSwitch {
            requested: item_id,
            blocking: 0,
            mask,
            secondary: false,
            combat_mode_after_wield,
        });
        let dispatched = self.dispatch(InventoryRequestKind::Wield, item_id, ctx, |ctx| {
            ctx.send(|seq| build_get_and_wield_item(seq, item_id, mask.bits()))
        });
        if !dispatched {
            self.wield.pending = None;
        }
        dispatched
    }

    /// Take the switch's next step from one item-table event.
    pub(super) fn observe_wield(&mut self, event: &TableEvent, ctx: &mut ItemContext<'_>) {
        if let TableEvent::Removed { object, .. } = event
            && self
                .wield
                .settlement
                .is_some_and(|s| s.item_id == object.object_id())
        {
            self.wield.settlement = None;
        }
        if *event == TableEvent::Cleared {
            self.wield = AutoWield::default();
            return;
        }
        let Some(pending) = self.wield.pending else {
            return;
        };
        match event {
            TableEvent::Moved(m)
                if pending.blocking != 0
                    && m.item_id == pending.blocking
                    && m.current.equip_location == EquipMask::NONE =>
            {
                // The blocker has moved, so the switch is over either way.
                // The wield follows only when it landed in the main pack;
                // one that went to a side pack (the main pack was full)
                // leaves the wield to the next attempt.
                self.wield.pending = None;
                if m.current.container_id != ctx.player() {
                    self.wield.transition_observed_during_switch = false;
                    return;
                }
                if pending.secondary {
                    self.try_wield_secondary(pending.requested, ctx);
                } else {
                    self.try_wield_for(
                        pending.requested,
                        pending.mask,
                        pending.combat_mode_after_wield,
                        ctx,
                    );
                }
            }
            TableEvent::WieldConfirmed(id) if pending.blocking == 0 && *id == pending.requested => {
                self.wield.pending = None;
                if let Some(ready_mode) = pending.combat_mode_after_wield {
                    self.wield.settlement = Some(CombatSettlement {
                        item_id: *id,
                        ready_mode,
                        phase: SettlementPhase::AwaitingPostWieldMode,
                        expect_trailing_peace: self.wield.transition_observed_during_switch,
                    });
                }
                self.wield.transition_observed_during_switch = false;
            }
            TableEvent::MoveRequestFailed(failure)
                if failure.item_id == pending.blocking || failure.item_id == pending.requested =>
            {
                self.wield.pending = None;
                self.wield.transition_observed_during_switch = false;
            }
            TableEvent::Removed { object, .. }
                if object.object_id() == pending.blocking
                    || object.object_id() == pending.requested =>
            {
                self.wield.pending = None;
                self.wield.transition_observed_during_switch = false;
            }
            _ => {}
        }
    }

    /// The combat mode changed. A wield made in combat waits out the
    /// server's stance shuffle (ready, peace, ready, peace); when it ends at
    /// peace after the ready mode came back, the ready mode is asked for
    /// again.
    pub fn on_combat_mode_changed(&mut self, mode: CombatMode, ctx: &mut ItemContext<'_>) {
        if let Some(PendingSwitch {
            combat_mode_after_wield: Some(ready),
            ..
        }) = self.wield.pending
            && mode != ready
        {
            self.wield.transition_observed_during_switch = true;
        }
        let Some(settlement) = self.wield.settlement else {
            return;
        };
        if mode == settlement.ready_mode {
            self.wield.settlement = (settlement.phase == SettlementPhase::SawTransitionalPeace
                || settlement.expect_trailing_peace)
                .then_some(CombatSettlement {
                    phase: SettlementPhase::SawReadyAfterPeace,
                    ..settlement
                });
            return;
        }
        if mode != CombatMode::NON_COMBAT {
            return;
        }
        if settlement.phase == SettlementPhase::SawReadyAfterPeace {
            self.wield.settlement = None;
            ctx.send(|seq| build_change_combat_mode(seq, settlement.ready_mode.bits()));
        } else {
            self.wield.settlement = Some(CombatSettlement {
                phase: SettlementPhase::SawTransitionalPeace,
                ..settlement
            });
        }
    }

    /// The player asked for a stance: a wield settlement stands down, and a
    /// switch in flight no longer re-asks for its stance.
    pub fn notify_explicit_combat_mode_request(&mut self) {
        self.wield.settlement = None;
        self.wield.transition_observed_during_switch = false;
        if let Some(pending) = &mut self.wield.pending {
            pending.combat_mode_after_wield = None;
        }
    }
}

/// The first item the player wears at any of `mask`'s slots (with a
/// matching layering priority, unless `priority` is 0), other than
/// `except`.
fn equipped_at(
    ctx: &ItemContext<'_>,
    player: u32,
    mask: EquipMask,
    priority: u32,
    except: u32,
) -> Option<u32> {
    if mask == EquipMask::NONE {
        return None;
    }
    ctx.world
        .items()
        .get_equipped_by(player)
        .into_iter()
        .find(|item| {
            item.object_id() != except
                && item.currently_equipped_location.intersects(mask)
                && (priority == 0 || item.priority & priority != 0)
        })
        .map(ClientObject::object_id)
}
