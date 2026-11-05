//! Typed [`Host`] methods for API group J: fellowship, allegiance, login,
//! recalls, the selection, dialogs and recovery.

use ac_plugin_abi::calls;
use ac_plugin_abi::social::{
    AllegianceResult, AllegianceState, BusyState, FellowMember, FellowshipCreate, FellowshipState,
    FellowshipStatus, LoginState, RecallKind, RecallResult, RecallState, RecoveryResult,
    SelectionAction, SelectionState,
};

use crate::Host;

/// J. Social and session.
impl Host<'_> {
    /// The character's fellowship, with its terms (whether it shares
    /// experience and whether the split is even).
    pub fn fellowship(&mut self) -> FellowshipState {
        self.call::<calls::FellowshipState>(&())
    }

    /// The other fellows the client can see, with their distance.
    pub fn fellowship_members(&mut self) -> Vec<FellowMember> {
        self.call::<calls::FellowshipMembers>(&())
    }

    /// Every fellow in the fellowship, the character included, each with
    /// vitals and level. A fellow the client cannot see (not loaded, or
    /// hidden) is listed too, with `distance` set to `f32::INFINITY`.
    pub fn fellowship_roster(&mut self) -> Vec<FellowMember> {
        self.call::<calls::FellowshipRoster>(&())
    }

    /// Create a fellowship.
    pub fn fellowship_create(&mut self, name: &str, share_experience: bool) -> FellowshipStatus {
        self.call::<calls::FellowshipCreate>(&FellowshipCreate {
            name: name.to_owned(),
            share_experience,
        })
    }

    /// Recruit a player.
    pub fn fellowship_recruit(&mut self, player_id: u32) -> FellowshipStatus {
        self.call::<calls::FellowshipRecruit>(&player_id)
    }

    /// Dismiss a fellow.
    pub fn fellowship_dismiss(&mut self, player_id: u32) -> FellowshipStatus {
        self.call::<calls::FellowshipDismiss>(&player_id)
    }

    /// Leave the fellowship, disbanding it when `disband`.
    pub fn fellowship_quit(&mut self, disband: bool) -> FellowshipStatus {
        self.call::<calls::FellowshipQuit>(&disband)
    }

    /// Hand the leadership to a fellow.
    pub fn fellowship_assign_leader(&mut self, player_id: u32) -> FellowshipStatus {
        self.call::<calls::FellowshipAssignLeader>(&player_id)
    }

    /// Open or close the fellowship to recruiting by anyone.
    pub fn fellowship_set_open(&mut self, open: bool) -> FellowshipStatus {
        self.call::<calls::FellowshipSetOpen>(&open)
    }

    /// Ask for, or stop, the fellows' live vitals.
    pub fn fellowship_request_vitals(&mut self, requested: bool) -> FellowshipStatus {
        self.call::<calls::FellowshipRequestVitals>(&requested)
    }

    /// The character's allegiance.
    pub fn allegiance(&mut self) -> AllegianceState {
        self.call::<calls::AllegianceState>(&())
    }

    /// Swear to a patron standing there.
    pub fn allegiance_swear(&mut self, patron_id: u32) -> AllegianceResult {
        self.call::<calls::AllegianceSwear>(&patron_id)
    }

    /// Break from someone in the allegiance.
    pub fn allegiance_break(&mut self, target_id: u32) -> AllegianceResult {
        self.call::<calls::AllegianceBreak>(&target_id)
    }

    /// The account's characters and the next login.
    pub fn login(&mut self) -> LoginState {
        self.call::<calls::LoginState>(&())
    }

    /// Enter this character at the next return to the list.
    pub fn login_set_next(&mut self, character_id: u32) -> bool {
        self.call::<calls::LoginSetNext>(&character_id)
    }

    /// No character is entered automatically.
    pub fn login_clear_next(&mut self) -> bool {
        self.call::<calls::LoginClearNext>(&())
    }

    /// Log the character out to the character list.
    pub fn logout(&mut self) -> bool {
        self.call::<calls::LoginLogout>(&())
    }

    /// The latest recall and the destinations known.
    pub fn recalls(&mut self) -> RecallState {
        self.call::<calls::RecallState>(&())
    }

    /// Recall.
    pub fn recall(&mut self, kind: RecallKind) -> RecallResult {
        self.call::<calls::RecallGo>(&kind)
    }

    /// The selection.
    pub fn selection(&mut self) -> SelectionState {
        self.call::<calls::SelectionState>(&())
    }

    /// Select an object (0 clears); false when nothing changed.
    pub fn select(&mut self, object_id: u32) -> bool {
        self.call::<calls::SelectionSelect>(&object_id)
    }

    /// Clear the selection.
    pub fn clear_selection(&mut self) -> bool {
        self.call::<calls::SelectionClear>(&())
    }

    /// Step the selection.
    pub fn selection_step(&mut self, action: SelectionAction) -> bool {
        self.call::<calls::SelectionExecute>(&action)
    }

    /// Answer the server's open question.
    pub fn answer_dialog(&mut self, context_id: u32, accept: bool) -> bool {
        self.call::<calls::DialogAnswer>(&(context_id, accept))
    }

    /// What holds the character busy.
    pub fn busy_state(&mut self) -> BusyState {
        self.call::<calls::RecoveryBusyState>(&())
    }

    /// Free one busy reference (or the unanswered description).
    pub fn clear_one_busy_reference(&mut self) -> RecoveryResult {
        self.call::<calls::RecoveryClearOneBusy>(&())
    }
}
