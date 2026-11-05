//! API group J: fellowship, allegiance, login and logout, recalls, the
//! selection, dialogs and recovery (OpenAC `RuntimeAutomationSurface`'s
//! `IFellowshipAutomation`, `IAllegianceAutomation`, `ILoginAutomation`,
//! `IRecallAutomation`, `ISelectionService`, `ISelectionAutomation`,
//! `IDialogAutomation` and `IRecoveryAutomation`) over the gameplay,
//! communication and inventory owners and what the session flow knows.

use ac_core::physics::PhysicsState;
use ac_plugin::abi::social::{
    AllegianceResult, AllegianceState, AllegianceStatus, BusyState, FellowMember, FellowshipCreate,
    FellowshipState, FellowshipStatus, LoginCharacter, LoginState, RecallKind, RecallLocation,
    RecallRequest, RecallResult, RecallState, RecallStatus, RecoveryResult, SelectionAction,
    SelectionState,
};
use ac_plugin::abi::world::{ObjectClass, Position};

use super::combat::global;
use super::world::{classify, position_of};
use super::{PluginBinding, Ran};
use crate::communication::ClientConfirmation;
use crate::gameplay::selection_cycle::{SelectionCycleDirection, SelectionCycleKind};
use crate::gameplay::{AllegianceCommand, FellowshipCommand, GameplayCommand};
use crate::inventory::ObjectRef;
use crate::session::{CharacterSelector, LifecycleCommand, Recall, SessionCommand};
use crate::world::World;

/// The recalls plugins asked for and the places they taught (OpenAC
/// `_lastRecallRequest`, `_pendingRecallRequestRevision` and the learned
/// recall locations).
#[derive(Debug, Default)]
pub struct RecallWatch {
    request_revision: i64,
    last_request: RecallRequest,
    pending_revision: i64,
    last_successful: Option<RecallKind>,
    lifestone: Option<RecallLocation>,
    marketplace: Option<RecallLocation>,
    mansion: Option<RecallLocation>,
    allegiance: Option<RecallLocation>,
    learned_revision: i64,
}

impl RecallWatch {
    /// The recall request a portal transition answers: the pending one,
    /// taken (the next transition carries none).
    pub(super) fn take_pending(&mut self) -> i64 {
        std::mem::take(&mut self.pending_revision)
    }

    /// A transition a recall asked for completed at `destination_cell`:
    /// the place is learned (OpenAC `CaptureRecallLocation`).
    pub(super) fn learn(&mut self, destination_cell: u32, house: Option<&ac_proto::Position>) {
        let Some(kind) = self.last_successful.take() else {
            return;
        };
        let at_cell = |cell: u32| Position {
            cell_id: cell,
            is_outdoor: true,
            ..Position::default()
        };
        let (place, name) = match kind {
            // The marketplace's standard arrival point.
            RecallKind::Marketplace => (
                Position {
                    east_west: 0.05,
                    north_south: 0.05,
                    ..at_cell(0x0166)
                },
                "Marketplace",
            ),
            // The house is read from the house owner when asked.
            RecallKind::House => return,
            RecallKind::Mansion => match house {
                Some(house) => (position_of(house), "Mansion"),
                None => return,
            },
            RecallKind::Lifestone if destination_cell != 0 => {
                (at_cell(destination_cell), "Lifestone")
            }
            RecallKind::Allegiance if destination_cell != 0 => {
                (at_cell(destination_cell), "Allegiance Hometown")
            }
            RecallKind::Lifestone | RecallKind::Allegiance => return,
        };
        self.learned_revision += 1;
        let location = RecallLocation {
            kind,
            position: place,
            name: name.to_owned(),
            revision: self.learned_revision,
            is_known: true,
        };
        let slot = match kind {
            RecallKind::Lifestone => &mut self.lifestone,
            RecallKind::Marketplace => &mut self.marketplace,
            RecallKind::Mansion => &mut self.mansion,
            RecallKind::Allegiance | RecallKind::House => &mut self.allegiance,
        };
        *slot = Some(location);
    }
}

/// How far the character is from `guid`, metres, when both stand where the
/// client can see them (OpenAC `RuntimeFriendlyTargetQuery.TryGetDistance`).
fn distance_to(world: &World, guid: u32) -> Option<f32> {
    let player = world.player_guid().filter(|p| *p != 0)?;
    let from = global(world.object(player)?.position()?);
    let target = world.object(guid)?;
    let hidden = PhysicsState::HIDDEN.bits() | PhysicsState::NO_DRAW.bits();
    if target.physics().state & hidden != 0 {
        return None;
    }
    Some(from.distance(global(target.position()?)))
}

fn fellowship_status(ran: Ran) -> FellowshipStatus {
    match ran {
        Ran::Done => FellowshipStatus::Accepted,
        Ran::Refused(_) => FellowshipStatus::Rejected,
        Ran::NoConnection => FellowshipStatus::Unavailable,
    }
}

impl PluginBinding<'_> {
    pub(super) fn fellowship_state(&self) -> FellowshipState {
        let view = self
            .state
            .gameplay
            .fellowship()
            .view(self.session_seconds());
        FellowshipState {
            is_in_fellowship: view.in_fellowship,
            name: view.name,
            leader_id: view.leader_guid,
            is_open: view.open,
            is_locked: view.locked,
            member_count: i32::try_from(view.members.len()).unwrap_or(i32::MAX),
            shares_experience: view.in_fellowship && view.share_xp,
            splits_experience_evenly: view.in_fellowship && view.even_xp_split,
        }
    }

    /// The fellowship's members (OpenAC `CaptureFellowshipMembers`), the
    /// character at distance 0. With `include_self` it is the whole roster:
    /// a fellow the client cannot see (not in the world, hidden or not
    /// drawn) stays, at an infinite distance. Without it, only the other
    /// fellows the client can see, as before.
    pub(super) fn fellowship_members(&self, include_self: bool) -> Vec<FellowMember> {
        if !self.in_world() {
            return Vec::new();
        }
        let view = self
            .state
            .gameplay
            .fellowship()
            .view(self.session_seconds());
        if !view.in_fellowship {
            return Vec::new();
        }
        let world = &self.state.world;
        let me = world.player_guid().unwrap_or(0);
        view.members
            .into_iter()
            .filter_map(|member| {
                let distance = if member.guid == me {
                    if !include_self {
                        return None;
                    }
                    0.0
                } else {
                    match distance_to(world, member.guid) {
                        Some(distance) => distance,
                        None if include_self => f32::INFINITY,
                        None => return None,
                    }
                };
                Some(FellowMember {
                    object_id: member.guid,
                    name: member.name,
                    current_health: member.health.0,
                    max_health: member.health.1,
                    current_stamina: member.stamina.0,
                    max_stamina: member.stamina.1,
                    current_mana: member.mana.0,
                    max_mana: member.mana.1,
                    distance,
                    share_loot: member.share_loot,
                    level: member.level,
                    vitals_age_seconds: member.vitals_age_seconds,
                })
            })
            .collect()
    }

    fn fellowship(&mut self, command: FellowshipCommand) -> FellowshipStatus {
        if !self.in_world() {
            return FellowshipStatus::Unavailable;
        }
        fellowship_status(
            self.run(SessionCommand::Gameplay(GameplayCommand::Fellowship(
                command,
            ))),
        )
    }

    pub(super) fn fellowship_create(&mut self, request: FellowshipCreate) -> FellowshipStatus {
        self.fellowship(FellowshipCommand::Create {
            name: request.name,
            share_xp: request.share_experience,
        })
    }

    pub(super) fn fellowship_recruit(&mut self, target: u32) -> FellowshipStatus {
        self.fellowship(FellowshipCommand::Recruit(ObjectRef::Guid(target)))
    }

    pub(super) fn fellowship_dismiss(&mut self, target: u32) -> FellowshipStatus {
        self.fellowship(FellowshipCommand::Dismiss(ObjectRef::Guid(target)))
    }

    pub(super) fn fellowship_quit(&mut self, disband: bool) -> FellowshipStatus {
        self.fellowship(FellowshipCommand::Quit { disband })
    }

    pub(super) fn fellowship_assign_leader(&mut self, target: u32) -> FellowshipStatus {
        self.fellowship(FellowshipCommand::AssignLeader(ObjectRef::Guid(target)))
    }

    pub(super) fn fellowship_set_open(&mut self, open: bool) -> FellowshipStatus {
        self.fellowship(FellowshipCommand::SetOpen(open))
    }

    pub(super) fn fellowship_request_vitals(&mut self, requested: bool) -> FellowshipStatus {
        self.fellowship(FellowshipCommand::RequestVitals(requested))
    }

    pub(super) fn allegiance_state(&self) -> AllegianceState {
        if !self.in_world() {
            return AllegianceState::default();
        }
        let allegiance = self.state.gameplay.allegiance();
        let player = self.state.world.player_guid().unwrap_or(0);
        let view = allegiance.view(player);
        AllegianceState {
            is_available: true,
            revision: i64::try_from(view.revision).unwrap_or(i64::MAX),
            is_known: allegiance.profile().is_some(),
            name: view.name,
            rank: view.rank,
            member_count: view.total_members,
            vassal_count: view.total_vassals,
            monarch_id: view.monarch.as_ref().map_or(0, |m| m.guid),
            monarch: view.monarch.as_ref().map(allegiance_member),
            patron: view.patron.as_ref().map(allegiance_member),
            vassals: view.vassals.iter().map(allegiance_member).collect(),
        }
    }

    /// A command the client turned down is about the session, not the
    /// target (the target was checked first).
    fn allegiance(&mut self, command: AllegianceCommand) -> AllegianceResult {
        let status = match self.run(SessionCommand::Gameplay(GameplayCommand::Allegiance(
            command,
        ))) {
            Ran::Done => AllegianceStatus::Sent,
            Ran::Refused(_) => {
                return AllegianceResult {
                    status: AllegianceStatus::Refused,
                    notice: Some("The client did not send the command.".into()),
                };
            }
            Ran::NoConnection => AllegianceStatus::Unavailable,
        };
        AllegianceResult {
            status,
            notice: None,
        }
    }

    /// Swearing is face to face: the patron is a player the client can see.
    pub(super) fn allegiance_swear(&mut self, patron: u32) -> AllegianceResult {
        if !self.in_world() {
            return AllegianceResult::default();
        }
        let world = &self.state.world;
        if patron == 0
            || world.object(patron).is_none()
            || classify(world.item(patron)) != ObjectClass::Player
        {
            return AllegianceResult {
                status: AllegianceStatus::InvalidTarget,
                notice: Some("Swearing needs another player the client can see.".into()),
            };
        }
        self.allegiance(AllegianceCommand::Swear(ObjectRef::Guid(patron)))
    }

    /// Breaking needs only that the target is in the allegiance the server
    /// described (a patron may be far away or logged out).
    pub(super) fn allegiance_break(&mut self, target: u32) -> AllegianceResult {
        if !self.in_world() {
            return AllegianceResult::default();
        }
        let known = self
            .state
            .gameplay
            .allegiance()
            .profile()
            .is_some_and(|p| p.find_data(target).is_some());
        if target == 0 || !known {
            return AllegianceResult {
                status: AllegianceStatus::InvalidTarget,
                notice: Some(
                    "Only someone in the character's allegiance can be broken from.".into(),
                ),
            };
        }
        self.allegiance(AllegianceCommand::Break(ObjectRef::Guid(target)))
    }

    pub(super) fn login_state(&self) -> LoginState {
        if self.link.is_none() {
            return LoginState::default();
        }
        LoginState {
            is_available: true,
            next_login_id: self.facts.next_login,
            can_request_logout: self.in_world(),
            roster: self
                .facts
                .roster
                .iter()
                .map(|c| LoginCharacter {
                    object_id: c.id,
                    name: c.name.clone(),
                    active_index: c.index,
                    is_pending_delete: c.pending_delete,
                })
                .collect(),
        }
    }

    /// Only a character on the list that may enter (OpenAC
    /// `TrySetNextLogin`).
    pub(super) fn login_set_next(&mut self, character: u32) -> bool {
        let can_enter = self
            .facts
            .roster
            .iter()
            .any(|c| c.id == character && !c.pending_delete);
        if self.link.is_none() || character == 0 || !can_enter {
            return false;
        }
        self.facts.next_login = character;
        self.deferred
            .push(SessionCommand::Lifecycle(LifecycleCommand::SetNextLogin(
                Some(CharacterSelector::Id(character)),
            )));
        true
    }

    pub(super) fn login_clear_next(&mut self) -> bool {
        if self.link.is_none() {
            return false;
        }
        self.facts.next_login = 0;
        self.deferred
            .push(SessionCommand::Lifecycle(LifecycleCommand::SetNextLogin(
                None,
            )));
        true
    }

    pub(super) fn login_logout(&mut self) -> bool {
        if !self.in_world() {
            return false;
        }
        self.deferred
            .push(SessionCommand::Lifecycle(LifecycleCommand::LogOff));
        true
    }

    pub(super) fn recall_state(&self) -> RecallState {
        if !self.in_world() {
            return RecallState::default();
        }
        let watch = &self.state.plugin_services.recalls;
        let house = self.state.gameplay.house();
        let mut locations = Vec::new();
        if let Some(position) = house.position() {
            locations.push(RecallLocation {
                kind: RecallKind::House,
                position: position_of(position),
                name: "House".into(),
                revision: i64::try_from(house.revision()).unwrap_or(i64::MAX),
                is_known: true,
            });
        }
        locations.extend(
            [
                &watch.lifestone,
                &watch.marketplace,
                &watch.mansion,
                &watch.allegiance,
            ]
            .into_iter()
            .flatten()
            .filter(|l| l.is_known)
            .cloned(),
        );
        RecallState {
            is_available: true,
            last_request: watch.last_request.clone(),
            locations,
        }
    }

    pub(super) fn recall_go(&mut self, kind: RecallKind) -> RecallResult {
        if !self.in_world() {
            return RecallResult::default();
        }
        let recall = match kind {
            RecallKind::Lifestone => Recall::Lifestone,
            RecallKind::Marketplace => Recall::Marketplace,
            RecallKind::House => Recall::House,
            RecallKind::Mansion => Recall::Mansion,
            RecallKind::Allegiance => Recall::AllegianceHometown,
        };
        let (status, notice) = match self.run(SessionCommand::Recall(recall)) {
            Ran::Done => (RecallStatus::Started, None),
            Ran::Refused(why) => (RecallStatus::Refused, Some(why)),
            Ran::NoConnection => (RecallStatus::Unavailable, None),
        };
        let watch = &mut self.state.plugin_services.recalls;
        watch.request_revision += 1;
        watch.last_request = RecallRequest {
            revision: watch.request_revision,
            kind,
            status,
        };
        watch.pending_revision = if status == RecallStatus::Started {
            watch.request_revision
        } else {
            0
        };
        if status == RecallStatus::Started {
            watch.last_successful = Some(kind);
        }
        RecallResult { status, notice }
    }

    pub(super) fn selection_state(&self) -> SelectionState {
        let selection = self.state.gameplay.selection();
        SelectionState {
            selected: selection.selected(),
            previous: selection.previous(),
        }
    }

    /// Runs a selection command; whether the selection changed.
    fn select_with(&mut self, command: GameplayCommand) -> bool {
        let before = self.state.gameplay.selection().selected();
        if matches!(
            self.run(SessionCommand::Gameplay(command)),
            Ran::NoConnection
        ) {
            return false;
        }
        self.state.gameplay.selection().selected() != before
    }

    pub(super) fn selection_select(&mut self, object_id: u32) -> bool {
        if object_id == 0 {
            return self.selection_clear();
        }
        self.select_with(GameplayCommand::Select(ObjectRef::Guid(object_id)))
    }

    pub(super) fn selection_clear(&mut self) -> bool {
        self.select_with(GameplayCommand::ClearSelection)
    }

    pub(super) fn selection_execute(&mut self, action: SelectionAction) -> bool {
        let command = match action {
            SelectionAction::PreviousSelection => GameplayCommand::SelectPrevious,
            SelectionAction::PreviousPlayer => GameplayCommand::CycleSelection {
                kind: SelectionCycleKind::Player,
                direction: SelectionCycleDirection::Previous,
            },
            SelectionAction::NextPlayer => GameplayCommand::CycleSelection {
                kind: SelectionCycleKind::Player,
                direction: SelectionCycleDirection::Next,
            },
        };
        self.select_with(command)
    }

    /// Answers the server's open question when its context matches
    /// (OpenAC `TryAnswerConfirmation`).
    pub(super) fn dialog_answer(&mut self, (context, accept): (u32, bool)) -> bool {
        let open = self
            .state
            .communication
            .server_confirmation()
            .filter(|c| matches!(c, ClientConfirmation::Server { context_id, .. } if *context_id == context))
            .cloned();
        let Some(confirmation) = open else {
            return false;
        };
        matches!(
            self.run(SessionCommand::AnswerConfirmation {
                confirmation,
                accepted: accept,
            }),
            Ran::Done
        )
    }

    pub(super) fn recovery_busy_state(&self) -> BusyState {
        if self.link.is_none() {
            return BusyState::default();
        }
        let inventory = &self.state.inventory;
        let transactions = inventory.transactions();
        let (use_source, use_target, awaiting_use_completion) = inventory.interaction().last_use();
        BusyState {
            busy_count: i32::try_from(transactions.busy_count()).unwrap_or(i32::MAX),
            pending_inventory: transactions.has_pending_request(),
            awaiting_appraisal: inventory.interaction().awaiting_appraisal(),
            use_source,
            use_target,
            awaiting_use_completion,
        }
    }

    /// Frees exactly one busy reference; with none, the description the
    /// server never answered (OpenAC `ClearOneBusyReference`).
    pub(super) fn recovery_clear_one_busy(&mut self) -> RecoveryResult {
        if self.link.is_none() {
            return RecoveryResult {
                message: "No game session is bound.".into(),
                ..RecoveryResult::default()
            };
        }
        let inventory = &mut self.state.inventory;
        if inventory.transactions().busy_count() != 0 {
            let (before, after) = inventory.clear_one_busy();
            return RecoveryResult {
                accepted: true,
                previous_count: i32::try_from(before).unwrap_or(i32::MAX),
                current_count: i32::try_from(after).unwrap_or(i32::MAX),
                message: "Cleared one action busy reference.".into(),
            };
        }
        let message = if inventory.abandon_awaiting_appraisal() {
            "Gave up an unanswered appraisal request."
        } else {
            "The action busy count was already zero."
        };
        RecoveryResult {
            accepted: true,
            previous_count: 0,
            current_count: 0,
            message: message.into(),
        }
    }
}

/// One allegiance member as a plugin reads it (OpenAC
/// `RuntimeAutomationSurface.ProjectAllegianceMember`).
fn allegiance_member(
    m: &crate::gameplay::AllegianceMemberView,
) -> ac_plugin::abi::social::AllegianceMember {
    ac_plugin::abi::social::AllegianceMember {
        object_id: m.guid,
        name: m.name.clone(),
        rank: u32::from(m.rank),
        level: m.level,
        heritage_group: i32::from(m.heritage_group),
        gender: i32::from(m.gender),
        is_online: m.online,
    }
}
