//! Character selection: the roster as the selection screen shows it
//! (sorted by name, characters pending deletion last), the highlighted
//! character, delete with its confirmation, restore, the character errors
//! the server sends, and which buttons are enabled.
//!
//! The session owns one of these per connection. Every change bumps the
//! revision and records a delta the host reads; nothing here sends: the
//! session sends the delete, restore and enter-world requests the state
//! hands back.

use std::time::Duration;

use ac_proto::character::CharGenVerificationResponse;
use ac_proto::{CharacterError, CharacterErrorCode};

use super::generation::Generation;

/// How long a restore waits for its answer before the restore button is
/// released again.
pub const RESTORE_CORRELATION_TIMEOUT: Duration = Duration::from_secs(5);

/// Where character selection is.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum SelectionLifecycle {
    /// No connection.
    #[default]
    Inactive,
    /// Connecting: no roster yet.
    Connecting,
    /// The roster is shown; a character can be chosen.
    AwaitingSelection,
    /// Entering the world with the highlighted character.
    EnteringWorld,
    /// A character is in the world.
    InWorld,
}

/// The delete or restore under way.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum SelectionOperation {
    /// None.
    #[default]
    None,
    /// A delete was sent.
    DeleteRequested,
    /// The server acknowledged the delete; the refreshed roster follows.
    DeleteAcknowledged,
    /// A restore was sent.
    RestoreRequested,
    /// The character was restored.
    RestoreSucceeded,
    /// The server refused the restore.
    RestoreRejected,
}

/// What changed.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SelectionDeltaKind {
    /// Everything was cleared (a new generation).
    Reset,
    /// A new roster arrived.
    RosterChanged,
    /// Another character was highlighted.
    HighlightChanged,
    /// The delete confirmation opened.
    DeleteConfirmationOpened,
    /// The delete confirmation was dismissed.
    DeleteConfirmationCancelled,
    /// The delete was sent.
    DeleteRequested,
    /// The server acknowledged the delete.
    DeleteAcknowledged,
    /// The restore was sent.
    RestoreRequested,
    /// The restore was answered.
    RestoreCompleted,
    /// The restore was not answered in time.
    RestoreCorrelationExpired,
    /// The error shown changed.
    ErrorChanged,
    /// Entering the world began.
    EnteringWorld,
    /// The character entered the world.
    EnteredWorld,
    /// The world's name or population changed.
    WorldNameChanged,
}

/// One change, in order.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct SelectionDelta {
    /// The generation it happened in.
    pub generation: Generation,
    /// Increases by one per delta.
    pub sequence: u64,
    /// The revision after the change.
    pub revision: u64,
    /// What changed.
    pub kind: SelectionDeltaKind,
    /// The character it is about (0 when none).
    pub character_id: u32,
    /// The error code it carries (0 when none).
    pub error_code: u32,
}

/// One character of the roster.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SelectionEntry {
    /// Its index in the server's list (delete and enter use it).
    pub active_index: usize,
    /// Object id.
    pub character_id: u32,
    /// Name.
    pub name: String,
    /// Seconds until a pending deletion completes (0 when none).
    pub seconds_greyed_out: u32,
}

impl SelectionEntry {
    /// The character is pending deletion.
    #[must_use]
    pub fn is_pending_delete(&self) -> bool {
        self.seconds_greyed_out != 0
    }

    /// The character may enter the world.
    #[must_use]
    pub fn can_enter(&self) -> bool {
        self.character_id != 0 && !self.is_pending_delete()
    }
}

/// Which selection-screen buttons are enabled and shown.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct SelectionButtons {
    /// Enter the world.
    pub can_enter: bool,
    /// Delete.
    pub can_delete: bool,
    /// Restore.
    pub can_restore: bool,
    /// The delete button is shown.
    pub delete_visible: bool,
    /// The restore button is shown.
    pub restore_visible: bool,
    /// A free slot allows creating a character.
    pub can_create: bool,
}

impl SelectionButtons {
    /// Nothing enabled; delete shown.
    const NONE: Self = Self {
        can_enter: false,
        can_delete: false,
        can_restore: false,
        delete_visible: true,
        restore_visible: false,
        can_create: false,
    };
}

/// An error the selection screen shows.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SelectionError {
    /// The code as sent.
    pub raw_code: u32,
    /// The known code, if it is one.
    pub code: Option<CharacterErrorCode>,
    /// The text shown.
    pub message: String,
}

/// The whole selection state as one value.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SelectionSnapshot {
    /// The generation.
    pub generation: Generation,
    /// Where selection is.
    pub lifecycle: SelectionLifecycle,
    /// Bumped by every change.
    pub revision: u64,
    /// The account name as the server spells it.
    pub account_name: String,
    /// Character slots the account has.
    pub slot_count: i32,
    /// The roster, in display order.
    pub entries: Vec<SelectionEntry>,
    /// The world's name.
    pub world_name: String,
    /// Players connected, once the server said.
    pub server_population: Option<u32>,
    /// The highlighted character (0 when none).
    pub highlighted_character_id: u32,
    /// Its display index, if any.
    pub highlighted_display_index: Option<usize>,
    /// The character the delete confirmation is open for (0 when none).
    pub pending_delete_character_id: u32,
    /// The character the last restore was for (0 when none).
    pub last_restore_requested_character_id: u32,
    /// The delete or restore under way.
    pub operation: SelectionOperation,
    /// The error shown.
    pub error: Option<SelectionError>,
    /// The buttons.
    pub buttons: SelectionButtons,
}

/// The character-selection owner.
#[derive(Debug, Default)]
pub struct CharacterSelection {
    entries: Vec<SelectionEntry>,
    generation: Generation,
    lifecycle: SelectionLifecycle,
    revision: u64,
    sequence: u64,
    account_name: String,
    slot_count: i32,
    world_name: String,
    server_population: Option<u32>,
    highlighted_character_id: u32,
    pending_delete_character_id: u32,
    last_restore_requested_character_id: u32,
    restore_response_armed: bool,
    restore_correlation_started: Duration,
    flag_only_restore_response_ambiguous: bool,
    operation: SelectionOperation,
    error: Option<SelectionError>,
    deltas: Vec<SelectionDelta>,
}

impl CharacterSelection {
    /// Inactive, generation 0.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// The state as one value.
    #[must_use]
    pub fn snapshot(&self) -> SelectionSnapshot {
        let index = self.find_display_index(self.highlighted_character_id);
        SelectionSnapshot {
            generation: self.generation,
            lifecycle: self.lifecycle,
            revision: self.revision,
            account_name: self.account_name.clone(),
            slot_count: self.slot_count,
            entries: self.entries.clone(),
            world_name: self.world_name.clone(),
            server_population: self.server_population,
            highlighted_character_id: self.highlighted_character_id,
            highlighted_display_index: index,
            pending_delete_character_id: self.pending_delete_character_id,
            last_restore_requested_character_id: self.last_restore_requested_character_id,
            operation: self.operation,
            error: self.error.clone(),
            buttons: self.buttons(index),
        }
    }

    /// Where selection is.
    #[must_use]
    pub fn lifecycle(&self) -> SelectionLifecycle {
        self.lifecycle
    }

    /// The bump counter.
    #[must_use]
    pub fn revision(&self) -> u64 {
        self.revision
    }

    /// The roster in display order.
    #[must_use]
    pub fn entries(&self) -> &[SelectionEntry] {
        &self.entries
    }

    /// The account name as the server spells it.
    #[must_use]
    pub fn account_name(&self) -> &str {
        &self.account_name
    }

    /// A character by id.
    #[must_use]
    pub fn get(&self, character_id: u32) -> Option<&SelectionEntry> {
        self.find_display_index(character_id)
            .map(|i| &self.entries[i])
    }

    /// Take the deltas recorded since the last call, oldest first.
    pub fn drain_deltas(&mut self) -> Vec<SelectionDelta> {
        std::mem::take(&mut self.deltas)
    }

    /// A connection starts in `generation`.
    pub fn begin(&mut self, generation: Generation) {
        self.generation = generation;
        self.lifecycle = SelectionLifecycle::Connecting;
        self.clear_session_state();
        self.revision += 1;
        self.publish(SelectionDeltaKind::Reset, 0, 0);
    }

    /// The server's roster arrived: sorted by name, characters pending
    /// deletion moved to the end, the highlight kept when the character is
    /// still there and otherwise the first available one (the first
    /// occupied one when none is available).
    pub fn apply_roster(
        &mut self,
        account_name: &str,
        slot_count: i32,
        roster: &[(u32, String, u32)],
    ) {
        let previous = self.highlighted_character_id;
        let mut fallback = 0;
        let mut found_available = false;
        let mut wire: Vec<SelectionEntry> = roster
            .iter()
            .enumerate()
            .map(|(i, (id, name, greyed))| {
                if fallback == 0 && *id != 0 {
                    fallback = *id;
                }
                if !found_available && *id != 0 && *greyed == 0 {
                    fallback = *id;
                    found_available = true;
                }
                SelectionEntry {
                    active_index: i,
                    character_id: *id,
                    name: name.clone(),
                    seconds_greyed_out: *greyed,
                }
            })
            .collect();
        sort_by_name(&mut wire);
        self.entries = partition_greyed_to_tail(wire);
        account_name.clone_into(&mut self.account_name);
        self.slot_count = slot_count;
        self.lifecycle = SelectionLifecycle::AwaitingSelection;
        self.pending_delete_character_id = 0;
        self.last_restore_requested_character_id = 0;
        self.restore_response_armed = false;
        self.restore_correlation_started = Duration::ZERO;
        self.flag_only_restore_response_ambiguous = false;
        self.operation = SelectionOperation::None;
        self.error = None;
        self.highlighted_character_id = if self.contains(previous) {
            previous
        } else {
            fallback
        };
        self.revision += 1;
        self.publish(
            SelectionDeltaKind::RosterChanged,
            self.highlighted_character_id,
            0,
        );
    }

    /// A character the server just created joins the roster at `wire_index`.
    pub fn append_created_character(&mut self, character_id: u32, name: &str, wire_index: usize) {
        let mut entries = std::mem::take(&mut self.entries);
        entries.push(SelectionEntry {
            active_index: wire_index,
            character_id,
            name: name.to_owned(),
            seconds_greyed_out: 0,
        });
        sort_by_name(&mut entries);
        self.entries = partition_greyed_to_tail(entries);
        self.revision += 1;
        self.publish(SelectionDeltaKind::RosterChanged, character_id, 0);
    }

    /// The world's name (and, when sent, its population).
    pub fn apply_world_name(&mut self, world_name: &str, population: Option<u32>) {
        if self.world_name == world_name
            && (population.is_none() || self.server_population == population)
        {
            return;
        }
        world_name.clone_into(&mut self.world_name);
        if population.is_some() {
            self.server_population = population;
        }
        self.revision += 1;
        self.publish(SelectionDeltaKind::WorldNameChanged, 0, 0);
    }

    /// Highlight a character. Refused outside selection, while a delete is
    /// open or sent, and for a character not on the roster.
    pub fn try_highlight(&mut self, character_id: u32) -> bool {
        if self.lifecycle != SelectionLifecycle::AwaitingSelection
            || self.has_delete_modal_or_request()
            || !self.contains(character_id)
        {
            return false;
        }
        if self.highlighted_character_id == character_id {
            return true;
        }
        self.highlighted_character_id = character_id;
        self.pending_delete_character_id = 0;
        self.error = None;
        self.revision += 1;
        self.publish(SelectionDeltaKind::HighlightChanged, character_id, 0);
        true
    }

    /// Open the delete confirmation for the highlighted character. Returns
    /// its id.
    pub fn try_request_delete(&mut self) -> Option<u32> {
        let index = self.find_display_index(self.highlighted_character_id)?;
        if self.lifecycle != SelectionLifecycle::AwaitingSelection
            || self.has_delete_modal_or_request()
            || !self.entries[index].can_enter()
        {
            return None;
        }
        let character_id = self.entries[index].character_id;
        self.pending_delete_character_id = character_id;
        self.error = None;
        self.revision += 1;
        self.publish(
            SelectionDeltaKind::DeleteConfirmationOpened,
            character_id,
            0,
        );
        Some(character_id)
    }

    /// Confirm the open delete: the character and the account name the
    /// delete request carries.
    pub fn try_take_delete_confirmation(&mut self) -> Option<(SelectionEntry, String)> {
        let index = self.find_display_index(self.pending_delete_character_id)?;
        if self.lifecycle != SelectionLifecycle::AwaitingSelection
            || self.pending_delete_character_id == 0
            || self.has_confirmed_delete()
            || !self.entries[index].can_enter()
        {
            return None;
        }
        let character = self.entries[index].clone();
        self.pending_delete_character_id = 0;
        self.operation = SelectionOperation::DeleteRequested;
        self.error = None;
        self.revision += 1;
        self.publish(
            SelectionDeltaKind::DeleteRequested,
            character.character_id,
            0,
        );
        Some((character, self.account_name.clone()))
    }

    /// Restore the highlighted character (pending deletion) at session time
    /// `now`. Fire and observe: the answer, if any, arrives later.
    pub fn try_begin_restore(&mut self, now: Duration) -> Option<SelectionEntry> {
        let index = self.find_display_index(self.highlighted_character_id)?;
        if self.lifecycle != SelectionLifecycle::AwaitingSelection
            || self.has_delete_modal_or_request()
            || self.restore_response_armed
            || !self.entries[index].is_pending_delete()
        {
            return None;
        }
        let character = self.entries[index].clone();
        self.pending_delete_character_id = 0;
        self.last_restore_requested_character_id = character.character_id;
        self.restore_response_armed = true;
        self.restore_correlation_started = now;
        self.operation = SelectionOperation::RestoreRequested;
        self.error = None;
        self.revision += 1;
        self.publish(
            SelectionDeltaKind::RestoreRequested,
            character.character_id,
            0,
        );
        Some(character)
    }

    /// Release a restore that was not answered in time. An answer without a
    /// character id that comes later is then ambiguous and ignored.
    pub fn sweep_restore_correlation(&mut self, now: Duration) -> bool {
        if !self.restore_response_armed
            || now.saturating_sub(self.restore_correlation_started) < RESTORE_CORRELATION_TIMEOUT
        {
            return false;
        }
        let character_id = self.last_restore_requested_character_id;
        self.restore_response_armed = false;
        self.restore_correlation_started = Duration::ZERO;
        self.flag_only_restore_response_ambiguous = true;
        if !self.has_confirmed_delete() {
            self.operation = SelectionOperation::None;
        }
        self.revision += 1;
        self.publish(
            SelectionDeltaKind::RestoreCorrelationExpired,
            character_id,
            0,
        );
        true
    }

    /// Dismiss the open delete confirmation, else the error shown.
    pub fn cancel(&mut self) -> bool {
        if self.lifecycle != SelectionLifecycle::AwaitingSelection {
            return false;
        }
        let (kind, character_id) = if self.pending_delete_character_id != 0 {
            let id = self.pending_delete_character_id;
            self.pending_delete_character_id = 0;
            (SelectionDeltaKind::DeleteConfirmationCancelled, id)
        } else if self.error.is_some() {
            self.error = None;
            (SelectionDeltaKind::ErrorChanged, 0)
        } else {
            return false;
        };
        self.revision += 1;
        self.publish(kind, character_id, 0);
        true
    }

    /// The server acknowledged the delete.
    pub fn apply_delete_acknowledged(&mut self) {
        if self.lifecycle != SelectionLifecycle::AwaitingSelection
            || self.operation != SelectionOperation::DeleteRequested
        {
            return;
        }
        self.operation = SelectionOperation::DeleteAcknowledged;
        self.revision += 1;
        self.publish(
            SelectionDeltaKind::DeleteAcknowledged,
            self.highlighted_character_id,
            0,
        );
    }

    /// The answer to a restore.
    pub fn apply_restore(&mut self, response: &CharGenVerificationResponse) {
        if self.lifecycle != SelectionLifecycle::AwaitingSelection || !self.restore_response_armed {
            return;
        }
        let guid = response.character.as_ref().map(|c| c.guid);
        if guid.is_none() && self.flag_only_restore_response_ambiguous {
            return;
        }
        if let Some(guid) = guid
            && guid != self.last_restore_requested_character_id
        {
            return;
        }
        self.restore_response_armed = false;
        self.restore_correlation_started = Duration::ZERO;
        let character_id = guid.unwrap_or(self.last_restore_requested_character_id);
        let delete_in_flight = self.has_confirmed_delete();
        let mut error_code = 0;
        if let (true, Some(restored)) = (response.is_ok(), &response.character) {
            if let Some(index) = self.find_display_index(restored.guid) {
                let entry = &mut self.entries[index];
                restored.name.clone_into(&mut entry.name);
                entry.seconds_greyed_out = restored.seconds_greyed_out;
                let mut entries = std::mem::take(&mut self.entries);
                sort_by_name(&mut entries);
                self.entries = partition_greyed_to_tail(entries);
            }
            if !delete_in_flight {
                self.operation = SelectionOperation::RestoreSucceeded;
            }
            self.error = None;
        } else {
            error_code = response.code;
            if !delete_in_flight {
                self.operation = SelectionOperation::RestoreRejected;
            }
            self.error = Some(SelectionError {
                raw_code: response.code,
                code: Some(CharacterErrorCode::Undefined),
                message: format!(
                    "The character could not be restored (verification 0x{:08X}).",
                    response.code
                ),
            });
        }
        self.revision += 1;
        self.publish(
            SelectionDeltaKind::RestoreCompleted,
            character_id,
            error_code,
        );
    }

    /// A character error from the server. The count sentinel is ignored; an
    /// error while entering the world returns to selection.
    pub fn apply_error(&mut self, error: CharacterError) {
        if error.is_sentinel() {
            return;
        }
        if !matches!(
            self.lifecycle,
            SelectionLifecycle::AwaitingSelection | SelectionLifecycle::EnteringWorld
        ) {
            return;
        }
        self.pending_delete_character_id = 0;
        if self.restore_response_armed {
            self.flag_only_restore_response_ambiguous = true;
        }
        self.restore_response_armed = false;
        self.restore_correlation_started = Duration::ZERO;
        self.operation = SelectionOperation::None;
        self.error = Some(SelectionError {
            raw_code: error.raw_error_code,
            code: error.code(),
            message: error_message(error),
        });
        if self.lifecycle == SelectionLifecycle::EnteringWorld {
            self.lifecycle = SelectionLifecycle::AwaitingSelection;
        }
        self.revision += 1;
        self.publish(SelectionDeltaKind::ErrorChanged, 0, error.raw_error_code);
    }

    /// Begin entering the world with the highlighted character.
    pub fn begin_enter(&mut self) -> Option<SelectionEntry> {
        let index = self.find_display_index(self.highlighted_character_id)?;
        if self.lifecycle != SelectionLifecycle::AwaitingSelection
            || self.has_delete_modal_or_request()
            || !self.entries[index].can_enter()
        {
            return None;
        }
        let character = self.entries[index].clone();
        self.pending_delete_character_id = 0;
        self.last_restore_requested_character_id = 0;
        self.restore_response_armed = false;
        self.restore_correlation_started = Duration::ZERO;
        self.flag_only_restore_response_ambiguous = false;
        self.operation = SelectionOperation::None;
        self.error = None;
        self.lifecycle = SelectionLifecycle::EnteringWorld;
        self.revision += 1;
        self.publish(SelectionDeltaKind::EnteringWorld, character.character_id, 0);
        Some(character)
    }

    /// The character entered the world.
    pub fn complete_enter(&mut self, character_id: u32) {
        self.lifecycle = SelectionLifecycle::InWorld;
        self.highlighted_character_id = character_id;
        self.operation = SelectionOperation::None;
        self.error = None;
        self.revision += 1;
        self.publish(SelectionDeltaKind::EnteredWorld, character_id, 0);
    }

    /// Entering failed before the server answered; back to the roster.
    pub fn return_to_selection(&mut self) {
        if self.lifecycle != SelectionLifecycle::EnteringWorld {
            return;
        }
        self.lifecycle = SelectionLifecycle::AwaitingSelection;
        self.revision += 1;
    }

    /// Clear everything for `generation`.
    pub fn reset(&mut self, generation: Generation) {
        self.generation = generation;
        self.lifecycle = SelectionLifecycle::Inactive;
        self.clear_session_state();
        self.revision += 1;
        self.publish(SelectionDeltaKind::Reset, 0, 0);
    }

    fn buttons(&self, selected: Option<usize>) -> SelectionButtons {
        let can_create = i32::try_from(self.entries.len()).unwrap_or(i32::MAX) < self.slot_count;
        let none = SelectionButtons {
            can_create,
            ..SelectionButtons::NONE
        };
        if self.has_confirmed_delete() {
            return none;
        }
        let Some(index) = selected else {
            return none;
        };
        let entry = &self.entries[index];
        if entry.is_pending_delete() {
            return SelectionButtons {
                can_enter: false,
                can_delete: false,
                can_restore: !self.restore_response_armed,
                delete_visible: false,
                restore_visible: true,
                can_create,
            };
        }
        SelectionButtons {
            can_enter: entry.can_enter(),
            can_delete: entry.can_enter(),
            can_restore: false,
            delete_visible: true,
            restore_visible: false,
            can_create,
        }
    }

    fn find_display_index(&self, character_id: u32) -> Option<usize> {
        if character_id == 0 {
            return None;
        }
        self.entries
            .iter()
            .position(|e| e.character_id == character_id)
    }

    fn contains(&self, character_id: u32) -> bool {
        self.find_display_index(character_id).is_some()
    }

    fn has_delete_modal_or_request(&self) -> bool {
        self.pending_delete_character_id != 0 || self.has_confirmed_delete()
    }

    fn has_confirmed_delete(&self) -> bool {
        matches!(
            self.operation,
            SelectionOperation::DeleteRequested | SelectionOperation::DeleteAcknowledged
        )
    }

    fn clear_session_state(&mut self) {
        self.entries.clear();
        self.account_name.clear();
        self.slot_count = 0;
        self.world_name.clear();
        self.server_population = None;
        self.highlighted_character_id = 0;
        self.pending_delete_character_id = 0;
        self.last_restore_requested_character_id = 0;
        self.restore_response_armed = false;
        self.restore_correlation_started = Duration::ZERO;
        self.flag_only_restore_response_ambiguous = false;
        self.operation = SelectionOperation::None;
        self.error = None;
    }

    fn publish(&mut self, kind: SelectionDeltaKind, character_id: u32, error_code: u32) {
        self.sequence += 1;
        self.deltas.push(SelectionDelta {
            generation: self.generation,
            sequence: self.sequence,
            revision: self.revision,
            kind,
            character_id,
            error_code,
        });
    }
}

/// Ordinal (byte-wise) name order, stable.
fn sort_by_name(entries: &mut [SelectionEntry]) {
    entries.sort_by(|a, b| a.name.as_bytes().cmp(b.name.as_bytes()));
}

/// Characters pending deletion move to the end, each group keeping its
/// order.
fn partition_greyed_to_tail(sorted: Vec<SelectionEntry>) -> Vec<SelectionEntry> {
    let (mut active, greyed): (Vec<_>, Vec<_>) =
        sorted.into_iter().partition(|e| !e.is_pending_delete());
    active.extend(greyed);
    active
}

/// The text the selection screen shows for a character error.
#[must_use]
pub fn error_message(error: CharacterError) -> String {
    use CharacterErrorCode as C;
    let text = match error.code() {
        Some(C::Logon) => "Another account is already logged on from this client.",
        Some(C::LoggedOn) => "This account is already logged on.",
        Some(C::AccountLogon) => {
            "The server could not access the account. Please try again shortly."
        }
        Some(C::ServerCrash | C::AccountInUse) => {
            "The server disconnected. Please try again shortly."
        }
        Some(C::Logoff) => "The server could not log off the character.",
        Some(C::Delete) => "The server could not delete the character.",
        Some(C::NoPremade) => "No premade character is available.",
        Some(C::AccountInvalid) => "The account name is not valid.",
        Some(C::AccountDoesntExist) => "The account does not exist.",
        Some(C::EnterGameGeneric) => "The character could not enter the world.",
        Some(C::EnterGameStressAccount) => "A stress-test character cannot enter the world.",
        Some(C::EnterGameCharacterInWorld) => {
            "One of this account's characters is still in the world. Please try again shortly."
        }
        Some(C::EnterGamePlayerAccountMissing) => {
            "The server could not find the player account. Please try again later."
        }
        Some(C::EnterGameCharacterNotOwned) => "This account does not own the selected character.",
        Some(C::EnterGameCharacterInWorldServer) => {
            "One of this account's characters is already in the world."
        }
        Some(C::EnterGameOldCharacter) => {
            "The selected character must be updated before entering the world."
        }
        Some(C::EnterGameCorruptCharacter) => "The selected character's data is corrupt.",
        Some(C::EnterGameStartServerDown) => {
            "The selected character's starting server is unavailable."
        }
        Some(C::EnterGameCouldntPlaceCharacter) => {
            "The selected character could not be placed in the world. Please try again shortly."
        }
        Some(C::LogonServerFull) => "The server is currently full. Please try again later.",
        Some(C::CharacterIsBooted) => "The selected character is temporarily unavailable.",
        Some(C::EnterGameCharacterLocked) => {
            "A save of the selected character is still in progress. Please try again later."
        }
        Some(C::SubscriptionExpired) => "The account subscription has expired.",
        Some(C::Undefined | C::NumErrors) | None => {
            return format!(
                "Character selection failed (error 0x{:08X}).",
                error.raw_error_code
            );
        }
    };
    text.to_owned()
}

/// Whether a character error ends the connection, whatever the session is
/// doing: the game shows its disconnected screen with the error's text for
/// these (logon refused or booted, the server down, the account unusable,
/// the server full, the subscription over); the others return to, or stay
/// at, character selection. The server drops the connection after sending
/// one of these, so nothing would follow.
#[must_use]
pub fn ends_the_session(error: CharacterError) -> bool {
    use CharacterErrorCode as C;
    matches!(
        error.code(),
        Some(
            C::Logon
                | C::AccountLogon
                | C::ServerCrash
                | C::AccountInUse
                | C::AccountInvalid
                | C::AccountDoesntExist
                | C::EnterGamePlayerAccountMissing
                | C::LogonServerFull
                | C::SubscriptionExpired
        )
    )
}

/// Whether a character error refuses the login because the account, or
/// one of its characters, is still logged on: an earlier session the
/// server has not let go of yet (a client that crashed or was killed).
/// Such a refusal clears by itself once the server drops the old session,
/// so a launcher may try again shortly.
#[must_use]
pub fn means_still_logged_on(error: CharacterError) -> bool {
    use CharacterErrorCode as C;
    matches!(
        error.code(),
        Some(
            C::Logon
                | C::LoggedOn
                | C::EnterGameCharacterInWorld
                | C::EnterGameCharacterInWorldServer
        )
    )
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC
    //! `tests/AcDream.Runtime.Tests/Session/RuntimeCharacterSelectionStateTests.cs`.
    use super::*;
    use ac_proto::character::CreatedCharacter;

    // Not an OpenAC test: the refusals a launcher may retry after a short
    // wait, by their codes' names (another log on, the account or a
    // character of it still in the world).
    #[test]
    fn only_a_login_still_held_by_the_server_means_still_logged_on() {
        let held = [0x01, 0x02, 0x0D, 0x10];
        for raw in 0u32..=0x1A {
            let error = CharacterError {
                raw_error_code: raw,
            };
            assert_eq!(
                means_still_logged_on(error),
                held.contains(&raw),
                "code {raw:#x}"
            );
        }
    }

    #[test]
    fn the_errors_that_end_the_session_are_the_games_disconnected_screen_set() {
        // Per error code (0 to
        // the 0x19 sentinel), whether the game shows its disconnected screen.
        let ending = [0x01, 0x03, 0x04, 0x08, 0x09, 0x0A, 0x0E, 0x15, 0x18];
        for raw in 0u32..=0x1A {
            let error = CharacterError {
                raw_error_code: raw,
            };
            assert_eq!(
                ends_the_session(error),
                ending.contains(&raw),
                "code {raw:#x}"
            );
        }
    }

    fn roster(state: &mut CharacterSelection, entries: &[(u32, &str, u32)]) {
        let list: Vec<(u32, String, u32)> = entries
            .iter()
            .map(|(id, name, greyed)| (*id, (*name).to_owned(), *greyed))
            .collect();
        state.apply_roster("Canonical", 11, &list);
    }

    fn begun(generation: u64) -> CharacterSelection {
        let mut state = CharacterSelection::new();
        state.begin(Generation(generation));
        state
    }

    fn restore(code: u32, character: Option<(u32, &str, u32)>) -> CharGenVerificationResponse {
        CharGenVerificationResponse {
            code,
            character: character.map(|(guid, name, seconds)| CreatedCharacter {
                guid,
                name: name.to_owned(),
                seconds_greyed_out: seconds,
            }),
        }
    }

    fn error(code: u32) -> CharacterError {
        CharacterError {
            raw_error_code: code,
        }
    }

    #[test]
    fn the_roster_takes_retail_order_fallback_and_button_matrix() {
        let mut state = begun(7);
        roster(
            &mut state,
            &[
                (0x5000_0001, "Zulu", 1),
                (0x5000_0002, "Bravo", 0),
                (0x5000_0003, "Alpha", 0),
                (0x5000_0004, "Able", 9),
            ],
        );
        let snapshot = state.snapshot();
        assert_eq!(snapshot.lifecycle, SelectionLifecycle::AwaitingSelection);
        assert_eq!(snapshot.highlighted_character_id, 0x5000_0002);
        let b = snapshot.buttons;
        assert!(b.can_enter && b.can_delete && !b.can_restore && b.delete_visible);
        assert!(!b.restore_visible);
        let order: Vec<(u32, usize, bool)> = snapshot
            .entries
            .iter()
            .map(|e| (e.character_id, e.active_index, e.is_pending_delete()))
            .collect();
        assert_eq!(
            order,
            [
                (0x5000_0003, 2, false),
                (0x5000_0002, 1, false),
                (0x5000_0004, 3, true),
                (0x5000_0001, 0, true),
            ]
        );
        assert!(state.try_highlight(0x5000_0004));
        let b = state.snapshot().buttons;
        assert!(!b.can_enter && !b.can_delete && b.can_restore);
        assert!(!b.delete_visible && b.restore_visible);
    }

    #[test]
    fn the_highlight_survives_the_refresh_after_a_delete() {
        let mut state = begun(3);
        roster(
            &mut state,
            &[(0x5000_0001, "One", 0), (0x5000_0002, "Two", 0)],
        );
        assert!(state.try_highlight(0x5000_0002));
        assert_eq!(state.try_request_delete(), Some(0x5000_0002));
        let (deleted, account) = state.try_take_delete_confirmation().unwrap();
        assert_eq!(deleted.active_index, 1);
        assert_eq!(account, "Canonical");
        state.apply_delete_acknowledged();
        assert_eq!(
            state.snapshot().operation,
            SelectionOperation::DeleteAcknowledged
        );
        roster(
            &mut state,
            &[(0x5000_0001, "One", 0), (0x5000_0002, "Two", 1)],
        );
        let snapshot = state.snapshot();
        assert_eq!(snapshot.highlighted_character_id, 0x5000_0002);
        assert_eq!(snapshot.operation, SelectionOperation::None);
        assert!(snapshot.buttons.can_restore);
        assert!(!snapshot.buttons.can_enter);
    }

    #[test]
    fn the_delete_confirmation_cancels_once_and_a_reset_clears_it() {
        let mut state = begun(4);
        roster(&mut state, &[(0x5000_0001, "One", 0)]);
        assert!(state.try_request_delete().is_some());
        assert_eq!(state.snapshot().pending_delete_character_id, 0x5000_0001);
        assert!(!state.try_highlight(0x5000_0001));
        assert!(state.begin_enter().is_none());
        assert!(state.cancel());
        assert_eq!(state.snapshot().pending_delete_character_id, 0);
        assert!(!state.cancel());
        state.reset(Generation(5));
        let snapshot = state.snapshot();
        assert_eq!(snapshot.lifecycle, SelectionLifecycle::Inactive);
        assert!(snapshot.entries.is_empty());
        assert_eq!(snapshot.generation, Generation(5));
    }

    #[test]
    fn a_restore_is_fire_and_observe_and_success_updates_the_entry() {
        let mut state = begun(11);
        roster(&mut state, &[(0x5000_0001, "Grey", 1)]);
        let request = state.try_begin_restore(Duration::ZERO).unwrap();
        assert_eq!(request.character_id, 0x5000_0001);
        assert_eq!(
            state.snapshot().operation,
            SelectionOperation::RestoreRequested
        );
        assert!(state.try_highlight(0x5000_0001));
        state.apply_restore(&restore(1, Some((0x5000_0001, "Restored", 0))));
        let restored = state.get(0x5000_0001).unwrap();
        assert_eq!(restored.name, "Restored");
        assert!(!restored.is_pending_delete());
        assert!(state.snapshot().buttons.can_enter);
        assert_eq!(
            state.snapshot().operation,
            SelectionOperation::RestoreSucceeded
        );
    }

    #[test]
    fn a_late_flag_only_restore_answer_cannot_complete_a_newer_request() {
        let mut state = begun(12);
        roster(
            &mut state,
            &[(0x5000_0001, "First", 1), (0x5000_0002, "Second", 1)],
        );
        assert!(state.try_begin_restore(Duration::ZERO).is_some());
        assert!(state.try_highlight(0x5000_0002));
        assert!(state.try_begin_restore(Duration::ZERO).is_none());
        assert!(state.sweep_restore_correlation(RESTORE_CORRELATION_TIMEOUT));
        assert!(
            state
                .try_begin_restore(RESTORE_CORRELATION_TIMEOUT)
                .is_some()
        );
        let revision = state.revision();
        state.apply_restore(&restore(2, None));
        let snapshot = state.snapshot();
        assert_eq!(snapshot.operation, SelectionOperation::RestoreRequested);
        assert_eq!(snapshot.last_restore_requested_character_id, 0x5000_0002);
        assert_eq!(snapshot.revision, revision);
        assert!(state.get(0x5000_0001).unwrap().is_pending_delete());
        assert!(state.get(0x5000_0002).unwrap().is_pending_delete());
        assert!(!snapshot.buttons.can_restore);
    }

    #[test]
    fn an_unanswered_restore_releases_only_the_restore_gate() {
        let mut state = begun(13);
        roster(
            &mut state,
            &[(0x5000_0001, "Pending", 1), (0x5000_0002, "Ready", 0)],
        );
        assert!(state.try_highlight(0x5000_0001));
        assert!(state.try_begin_restore(Duration::ZERO).is_some());
        assert!(!state.snapshot().buttons.can_restore);
        assert!(state.try_highlight(0x5000_0002));
        assert_eq!(state.begin_enter().unwrap().character_id, 0x5000_0002);
        state.return_to_selection();
        assert!(state.try_highlight(0x5000_0001));
        assert!(state.try_begin_restore(Duration::ZERO).is_some());
        assert!(state.sweep_restore_correlation(RESTORE_CORRELATION_TIMEOUT));
        assert_eq!(state.snapshot().operation, SelectionOperation::None);
        assert!(state.snapshot().buttons.can_restore);
    }

    #[test]
    fn errors_map_known_and_unknown_codes_and_never_publish_the_sentinel() {
        let mut state = begun(2);
        roster(&mut state, &[(0x5000_0001, "One", 0)]);
        state.drain_deltas();
        state.apply_error(error(CharacterErrorCode::Delete.value()));
        let known = state.snapshot().error.unwrap();
        assert_eq!(known.code, Some(CharacterErrorCode::Delete));
        assert!(known.message.to_lowercase().contains("delete"));
        assert!(state.cancel());
        state.apply_error(error(0xDEAD_BEEF));
        assert!(
            state
                .snapshot()
                .error
                .unwrap()
                .message
                .contains("0xDEADBEEF")
        );
        let revision = state.revision();
        let count = state.drain_deltas().len();
        assert_eq!(count, 3);
        state.apply_error(error(CharacterErrorCode::NumErrors.value()));
        assert_eq!(state.revision(), revision);
        assert!(state.drain_deltas().is_empty());
        assert_eq!(state.snapshot().error.unwrap().raw_code, 0xDEAD_BEEF);
    }

    #[test]
    fn deltas_are_ordered_and_carry_the_generation() {
        let mut state = begun(9);
        state.drain_deltas();
        roster(
            &mut state,
            &[(0x5000_0001, "Ready", 0), (0x5000_0002, "Grey", 1)],
        );
        assert!(state.try_highlight(0x5000_0002));
        let deltas = state.drain_deltas();
        let kinds: Vec<_> = deltas.iter().map(|d| d.kind).collect();
        assert_eq!(
            kinds,
            [
                SelectionDeltaKind::RosterChanged,
                SelectionDeltaKind::HighlightChanged
            ]
        );
        assert!(deltas[0].sequence < deltas[1].sequence);
        assert!(deltas.iter().all(|d| d.generation == Generation(9)));
    }

    #[test]
    fn the_world_name_is_kept_apart_from_the_roster_and_reset_clears_it() {
        let mut state = begun(5);
        assert_eq!(state.snapshot().world_name, "");
        state.apply_world_name("sawato", None);
        roster(&mut state, &[(0x5000_0001, "One", 0)]);
        assert_eq!(state.snapshot().world_name, "sawato");
        assert_eq!(state.snapshot().highlighted_character_id, 0x5000_0001);
        state.drain_deltas();
        let revision = state.revision();
        state.apply_world_name("sawato", None);
        assert_eq!(state.revision(), revision);
        assert!(state.drain_deltas().is_empty());
        state.reset(Generation(9));
        assert_eq!(state.snapshot().world_name, "");
    }
}
