//! Who may be attacked, whose health bar the toolbar asks for, and which
//! creatures automatic targeting may pick.

use crate::items::{ItemType, PublicWeenieFlags};

/// The facts about an object the attack rules read.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct CombatObject {
    /// Object id.
    pub object_id: u32,
    /// Item type.
    pub item_type: ItemType,
    /// Public weenie bit field (`None` when not sent).
    pub public_weenie_bitfield: Option<u32>,
    /// The pet's owner (0 when not a pet).
    pub pet_owner_id: u32,
}

impl CombatObject {
    fn flags(&self) -> PublicWeenieFlags {
        PublicWeenieFlags(self.public_weenie_bitfield.unwrap_or(0))
    }
}

/// Whether the toolbar asks the server for the selected object's health:
/// for players, pets, and anything attackable.
#[must_use]
pub fn should_query_health(
    player_id: u32,
    player: Option<&CombatObject>,
    selected: Option<&CombatObject>,
) -> bool {
    let Some(selected) = selected else {
        return false;
    };
    selected.flags().intersects(PublicWeenieFlags::PLAYER)
        || selected.pet_owner_id != 0
        || object_is_attackable(player_id, player, selected.object_id, Some(selected))
}

/// Whether the player may attack the target. No target (id 0) and the
/// player themself count as attackable. Otherwise the target must be a
/// creature; free-for-all player-killer status on either side allows it;
/// another player needs a matching player-killer or PK-lite status; a pet
/// never; anything else needs the attackable bit.
#[must_use]
pub fn object_is_attackable(
    player_id: u32,
    player: Option<&CombatObject>,
    target_id: u32,
    target: Option<&CombatObject>,
) -> bool {
    if target_id == 0 || target_id == player_id {
        return true;
    }
    let Some(target) = target else {
        return false;
    };
    if !target.item_type.intersects(ItemType::CREATURE) {
        return false;
    }
    let target_flags = target.flags();
    if target_flags.intersects(PublicWeenieFlags::FREE_PLAYER_KILLER) {
        return true;
    }
    let Some(player) = player else {
        return false;
    };
    let player_flags = player.flags();
    if player_flags.intersects(PublicWeenieFlags::FREE_PLAYER_KILLER) {
        return true;
    }
    if target_flags.intersects(PublicWeenieFlags::PLAYER) {
        let both = |bit| target_flags.intersects(bit) && player_flags.intersects(bit);
        return both(PublicWeenieFlags::PLAYER_KILLER)
            || both(PublicWeenieFlags::PLAYER_KILLER_LITE);
    }
    if target.pet_owner_id != 0 {
        return false;
    }
    target_flags.intersects(PublicWeenieFlags::ATTACKABLE)
}

/// Whether automatic targeting may pick the candidate: an attackable
/// creature that is not the player, not a player and not a pet.
#[must_use]
pub fn is_hostile_monster(
    player_id: u32,
    player: Option<&CombatObject>,
    candidate: Option<&CombatObject>,
) -> bool {
    let Some(candidate) = candidate else {
        return false;
    };
    if candidate.object_id == player_id
        || !candidate.item_type.intersects(ItemType::CREATURE)
        || candidate.flags().intersects(PublicWeenieFlags::PLAYER)
        || candidate.pet_owner_id != 0
    {
        return false;
    }
    object_is_attackable(player_id, player, candidate.object_id, Some(candidate))
}

#[cfg(test)]
mod tests {
    use super::*;

    // Golden behavior from OpenAC
    // `tests/AcDream.Core.Tests/Combat/SelectedObjectHealthPolicyTests.cs` and
    // `CombatTargetPolicyTests.cs`.

    const PLAYER_ID: u32 = 0x5000_0001;
    const PLAYER: PublicWeenieFlags = PublicWeenieFlags::PLAYER;
    const ATTACKABLE: PublicWeenieFlags = PublicWeenieFlags::ATTACKABLE;
    const PK: PublicWeenieFlags = PublicWeenieFlags::PLAYER_KILLER;
    const FREE_PK: PublicWeenieFlags = PublicWeenieFlags::FREE_PLAYER_KILLER;
    const PK_LITE: PublicWeenieFlags = PublicWeenieFlags::PLAYER_KILLER_LITE;

    fn obj(id: u32, item_type: ItemType, flags: PublicWeenieFlags, pet_owner: u32) -> CombatObject {
        CombatObject {
            object_id: id,
            item_type,
            public_weenie_bitfield: Some(flags.bits()),
            pet_owner_id: pet_owner,
        }
    }

    fn creature(id: u32, flags: PublicWeenieFlags) -> CombatObject {
        obj(id, ItemType::CREATURE, flags, 0)
    }

    #[test]
    fn toolbar_player_and_pet_short_circuit() {
        let local = creature(PLAYER_ID, PLAYER);
        let other = creature(0x5000_0002, PLAYER);
        let pet = obj(
            0x5000_0003,
            ItemType::CREATURE,
            PublicWeenieFlags(0),
            PLAYER_ID,
        );
        assert!(should_query_health(PLAYER_ID, Some(&local), Some(&other)));
        assert!(should_query_health(PLAYER_ID, Some(&local), Some(&pet)));
        assert!(should_query_health(PLAYER_ID, Some(&local), Some(&local)));
        assert!(!should_query_health(PLAYER_ID, Some(&local), None));
    }

    #[test]
    fn toolbar_attackable_creature_only() {
        let local = creature(PLAYER_ID, PLAYER);
        let monster = creature(0x5000_0010, ATTACKABLE);
        let npc = creature(0x5000_0011, PublicWeenieFlags(0));
        let door = obj(0x5000_0012, ItemType::MISC, ATTACKABLE, 0);
        assert!(should_query_health(PLAYER_ID, Some(&local), Some(&monster)));
        assert!(!should_query_health(PLAYER_ID, Some(&local), Some(&npc)));
        assert!(!should_query_health(PLAYER_ID, Some(&local), Some(&door)));
    }

    #[test]
    fn free_pk_on_either_side_is_attackable() {
        let normal_player = creature(PLAYER_ID, PLAYER);
        let free_player = creature(PLAYER_ID, PLAYER | FREE_PK);
        let normal = creature(0x5000_0020, PublicWeenieFlags(0));
        let free = creature(0x5000_0021, FREE_PK);
        assert!(object_is_attackable(
            PLAYER_ID,
            Some(&normal_player),
            free.object_id,
            Some(&free)
        ));
        assert!(object_is_attackable(
            PLAYER_ID,
            Some(&free_player),
            normal.object_id,
            Some(&normal)
        ));
    }

    #[test]
    fn players_need_a_matching_pk_pool() {
        let pk_player = creature(PLAYER_ID, PLAYER | PK);
        let pk_target = creature(0x5000_0030, PLAYER | PK);
        let lite_target = creature(0x5000_0031, PLAYER | PK_LITE);
        assert!(object_is_attackable(
            PLAYER_ID,
            Some(&pk_player),
            pk_target.object_id,
            Some(&pk_target)
        ));
        assert!(!object_is_attackable(
            PLAYER_ID,
            Some(&pk_player),
            lite_target.object_id,
            Some(&lite_target)
        ));
    }

    #[test]
    fn zero_or_self_true_missing_player_blocks_creature() {
        let creature = creature(0x5000_0040, ATTACKABLE);
        assert!(object_is_attackable(PLAYER_ID, None, 0, None));
        assert!(object_is_attackable(PLAYER_ID, None, PLAYER_ID, None));
        assert!(!object_is_attackable(
            PLAYER_ID,
            None,
            creature.object_id,
            Some(&creature)
        ));
    }

    #[test]
    fn attackable_pet_is_rejected() {
        let player = creature(PLAYER_ID, PLAYER);
        let pet = obj(0x5000_0041, ItemType::CREATURE, ATTACKABLE, PLAYER_ID);
        assert!(!object_is_attackable(
            PLAYER_ID,
            Some(&player),
            pet.object_id,
            Some(&pet)
        ));
    }

    #[test]
    fn automatic_acquisition_accepts_only_attackable_non_player_monsters() {
        let player = creature(PLAYER_ID, PLAYER | PK);
        let monster = creature(0x5000_0010, ATTACKABLE);
        let npc = creature(0x5000_0011, PublicWeenieFlags(0));
        let pet = obj(
            0x5000_0012,
            ItemType::CREATURE,
            PublicWeenieFlags(0),
            PLAYER_ID,
        );
        let hostile_player = creature(0x5000_0013, PLAYER | PK);
        let door = obj(0x5000_0014, ItemType::MISC, ATTACKABLE, 0);
        assert!(is_hostile_monster(PLAYER_ID, Some(&player), Some(&monster)));
        assert!(!is_hostile_monster(PLAYER_ID, Some(&player), Some(&npc)));
        assert!(!is_hostile_monster(PLAYER_ID, Some(&player), Some(&pet)));
        assert!(!is_hostile_monster(
            PLAYER_ID,
            Some(&player),
            Some(&hostile_player)
        ));
        assert!(!is_hostile_monster(PLAYER_ID, Some(&player), Some(&door)));
        assert!(!is_hostile_monster(PLAYER_ID, Some(&player), None));
    }
}
