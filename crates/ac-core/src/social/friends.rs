//! The friends list as the server keeps it: a full list at login, then
//! incremental adds, removes and online-status changes keyed by character
//! id.

use crate::wire_enum::wire_enum;

/// One friend.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct FriendEntry {
    /// The friend's character id.
    pub id: u32,
    /// The friend's name.
    pub name: String,
    /// Whether the friend is online.
    pub online: bool,
    /// Whether the friend chose to appear offline.
    pub appear_offline: bool,
    /// The friend's own friends (character ids).
    pub friends: Vec<u32>,
    /// Who has the friend on their list (character ids).
    pub friend_of: Vec<u32>,
}

wire_enum! {
    values
    /// What a friends update does with its entries.
    FriendsUpdateType {
        FULL = 0 => "Full",
        ADD = 1 => "Add",
        REMOVE = 2 => "Remove",
        REMOVE_SILENT = 3 => "RemoveSilent",
        ONLINE_STATUS = 4 => "OnlineStatus",
    }
}

/// A friends update from the server.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct FriendsUpdate {
    /// What to do.
    pub update_type: FriendsUpdateType,
    /// The entries it concerns.
    pub entries: Vec<FriendEntry>,
}

/// The client's copy of the friends list, in server order.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct FriendsState {
    entries: Vec<FriendEntry>,
    revision: u64,
}

impl FriendsState {
    /// An empty list.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// How many friends.
    #[must_use]
    pub fn len(&self) -> usize {
        self.entries.len()
    }

    /// True when the list is empty.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.entries.is_empty()
    }

    /// Advances on every update and clear.
    #[must_use]
    pub fn revision(&self) -> u64 {
        self.revision
    }

    /// The friends, in order.
    #[must_use]
    pub fn entries(&self) -> &[FriendEntry] {
        &self.entries
    }

    /// The friend with this character id.
    #[must_use]
    pub fn get(&self, character_id: u32) -> Option<&FriendEntry> {
        self.entries.iter().find(|entry| entry.id == character_id)
    }

    /// Applies an update. `Full` replaces the list; `Add` replaces a friend
    /// already listed or appends a new one; `Remove` and `RemoveSilent`
    /// drop by id; `OnlineStatus` replaces only friends already listed. An
    /// unknown update type changes nothing (the revision still advances).
    pub fn apply(&mut self, update: &FriendsUpdate) {
        match update.update_type {
            FriendsUpdateType::FULL => update.entries.clone_into(&mut self.entries),
            FriendsUpdateType::ADD => {
                for entry in &update.entries {
                    match self.entries.iter_mut().find(|c| c.id == entry.id) {
                        Some(slot) => *slot = entry.clone(),
                        None => self.entries.push(entry.clone()),
                    }
                }
            }
            FriendsUpdateType::REMOVE | FriendsUpdateType::REMOVE_SILENT => {
                for entry in &update.entries {
                    self.entries.retain(|c| c.id != entry.id);
                }
            }
            FriendsUpdateType::ONLINE_STATUS => {
                for entry in &update.entries {
                    if let Some(slot) = self.entries.iter_mut().find(|c| c.id == entry.id) {
                        *slot = entry.clone();
                    }
                }
            }
            _ => {}
        }
        self.revision += 1;
    }

    /// Empties the list.
    pub fn clear(&mut self) {
        self.entries.clear();
        self.revision += 1;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn friend(id: u32, name: &str, online: bool) -> FriendEntry {
        FriendEntry {
            id,
            name: name.to_owned(),
            online,
            appear_offline: false,
            friends: Vec::new(),
            friend_of: Vec::new(),
        }
    }

    fn update(update_type: FriendsUpdateType, entries: Vec<FriendEntry>) -> FriendsUpdate {
        FriendsUpdate {
            update_type,
            entries,
        }
    }

    // Golden values: OpenAC tests/AcDream.Core.Tests/Social/SocialStateTests.cs
    #[test]
    fn applies_incremental_update_kinds_by_object_id() {
        let mut state = FriendsState::new();
        state.apply(&update(
            FriendsUpdateType::FULL,
            vec![friend(1, "Alice", false), friend(2, "Bjørn", true)],
        ));
        state.apply(&update(
            FriendsUpdateType::ONLINE_STATUS,
            vec![friend(1, "Alice", true)],
        ));
        state.apply(&update(
            FriendsUpdateType::ADD,
            vec![friend(3, "Cara", true)],
        ));
        state.apply(&update(
            FriendsUpdateType::REMOVE_SILENT,
            vec![friend(2, "Bjørn", false)],
        ));

        let entries = state.entries();
        assert_eq!(entries.len(), 2);
        assert_eq!(entries[0].id, 1);
        assert!(entries[0].online);
        assert_eq!(entries[1].id, 3);
    }

    #[test]
    fn online_status_for_a_stranger_adds_nobody() {
        let mut state = FriendsState::new();
        state.apply(&update(
            FriendsUpdateType::ONLINE_STATUS,
            vec![friend(9, "Nobody", true)],
        ));
        assert!(state.is_empty());
        assert_eq!(state.get(9), None);
    }

    #[test]
    fn add_replaces_a_listed_friend_in_place() {
        let mut state = FriendsState::new();
        state.apply(&update(
            FriendsUpdateType::FULL,
            vec![friend(1, "Alice", false), friend(2, "Bo", false)],
        ));
        state.apply(&update(
            FriendsUpdateType::ADD,
            vec![friend(1, "Alice", true)],
        ));
        assert_eq!(state.len(), 2);
        assert!(state.get(1).is_some_and(|f| f.online));
        assert_eq!(state.entries()[0].id, 1);
    }

    #[test]
    fn unknown_update_type_changes_nothing_but_the_revision() {
        let mut state = FriendsState::new();
        let before = state.revision();
        state.apply(&update(
            FriendsUpdateType(99),
            vec![friend(1, "Alice", true)],
        ));
        assert!(state.is_empty());
        assert!(state.revision() > before);
    }
}
