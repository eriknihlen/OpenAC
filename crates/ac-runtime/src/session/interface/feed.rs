//! The session's side of the panels' facts: each fact read from the session
//! state, and the feed that tells an attached window only what changed. It
//! needs the session state, so it comes with the runner; the facts
//! themselves (the parent module) are plain data a window builds alone.

use std::sync::Arc;

use super::super::runner::HostEvent;
use super::super::state::SessionState;
use super::{
    ChatEntryView, ContractFacts, InterfaceEvent, InterfaceState, PlayerFacts, SelectedHealth,
    SelectedMana, SocialFacts, TitleFacts, items_snapshot, items_update,
};
use crate::inventory::VendorViewReason;
use crate::world::WorldChange;

impl ChatEntryView {
    /// The entry and the targets as `state` holds them now.
    #[must_use]
    pub fn of(state: &SessionState) -> Self {
        let entry = state.communication.chat_entry();
        let targets = state.communication.targets();
        Self {
            // A relay token being typed is never shown to a view (an
            // attached browser sees the draft as it is typed).
            draft: crate::communication::remote_command::masked_draft(entry.draft()).into_owned(),
            shown_revision: entry.shown_revision(),
            composed: entry.is_input_active(),
            channel: entry.active_channel(),
            tell_target: entry.tell_target().map(str::to_owned),
            tell_target_guid: entry.tell_target_guid(),
            last_incoming_tell_sender: targets.last_incoming_tell_sender().map(str::to_owned),
            last_monarch_sender: targets.last_monarch_sender().map(str::to_owned),
            last_patron_sender: targets.last_patron_sender().map(str::to_owned),
        }
    }
}

impl SocialFacts {
    /// The facts as `state` holds them now; `now` is the session time in
    /// seconds (the fellows' vitals ages).
    #[must_use]
    pub fn of(state: &SessionState, in_world: bool, now: f64) -> Self {
        let allegiance = state.gameplay.allegiance();
        Self {
            in_world,
            options: state.options.clone(),
            friends: state.communication.friends().clone(),
            squelch: state.communication.squelch().clone(),
            fellowship: state.gameplay.fellowship().view(now),
            allegiance: allegiance.profile().cloned(),
            allegiance_rank: allegiance.rank(),
            allegiance_revision: allegiance.revision(),
        }
    }

    /// What moves when anything here changes.
    fn key(state: &SessionState, in_world: bool) -> SocialKey {
        SocialKey {
            in_world,
            options: state.options.revision(),
            friends: state.communication.friends().revision(),
            squelch: state.communication.squelch().revision(),
            fellowship: state.gameplay.fellowship().revision(),
            allegiance: state.gameplay.allegiance().revision(),
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
struct SocialKey {
    in_world: bool,
    options: u64,
    friends: u64,
    squelch: u64,
    fellowship: u64,
    allegiance: u64,
}

impl ContractFacts {
    /// The tracker as `state` holds it now.
    #[must_use]
    pub fn of(state: &SessionState) -> Self {
        let tracker = state.communication.contracts();
        Self {
            contracts: tracker.contracts().copied().collect(),
            display_contract_id: tracker.display_contract_id(),
            revision: tracker.revision(),
            catalog: Arc::clone(state.communication.contract_catalog()),
        }
    }
}

impl InterfaceState {
    /// The facts as `state` holds them now.
    #[must_use]
    pub fn of(state: &SessionState, active: bool) -> Self {
        let external_container = state.inventory.external_containers().current_container_id();
        Self {
            player: state.world.player_guid().unwrap_or(0),
            mode: state.inventory.interaction_mode(),
            pending_item: state.inventory.pending_item(),
            pending_placement: state.inventory.pending_placement(),
            can_make_request: state.inventory.is_ready(active),
            combat_mode: state.gameplay.combat_mode(),
            busy_count: busy_count(state),
            shortcuts: state.inventory.shortcuts().clone(),
            confirmations: state
                .communication
                .server_confirmation()
                .cloned()
                .into_iter()
                .chain(state.communication.pending_confirmation())
                .collect(),
            selected_health: selected_health(state),
            selected_mana: selected_mana(state),
            external_container,
            external_container_in_range: external_container == 0
                || state.within_use_range(external_container),
            account: state.gameplay.account().to_owned(),
        }
    }
}

/// OpenAC `RuntimeItemInteraction.BusyCount` + `AppraisalCount`, the
/// appraisal counted only while the awaited question is the player's own:
/// the busy cursor belongs to the player's examine, not to a question a
/// plugin or a walk asks for him (an identify, a door check), which would
/// otherwise keep the cursor busy for seconds at a time.
pub(super) fn busy_count(state: &SessionState) -> u32 {
    use crate::inventory::AppraisalOrigin;
    let transactions = state.inventory.transactions();
    let interaction = state.inventory.interaction();
    let now_ms = (state.session_seconds() * 1000.0) as i64;
    let appraisals = if interaction.is_awaiting_expired(now_ms)
        || interaction.awaiting_origin() != AppraisalOrigin::User
    {
        0
    } else {
        transactions.appraisal_count()
    };
    transactions.busy_count() + appraisals
}

/// The selected object's health while the health stream follows it.
fn selected_health(state: &SessionState) -> Option<SelectedHealth> {
    let selected = state.gameplay.selection().selected()?;
    if state.gameplay.health_query().streaming_object_id() != selected {
        return None;
    }
    let combat = state.gameplay.combat();
    Some(SelectedHealth {
        object_id: selected,
        percent: combat
            .has_health(selected)
            .then(|| combat.health_percent(selected)),
    })
}

/// The selected object's mana while the server's answer for it is known
/// (OpenAC `ItemManaState.GetManaPercent` of the selection).
fn selected_mana(state: &SessionState) -> Option<SelectedMana> {
    let selected = state.gameplay.selection().selected()?;
    let fraction = state.inventory.item_mana().try_mana_fraction(selected)?;
    Some(SelectedMana {
        object_id: selected,
        fraction,
    })
}

/// What a window was last told, to tell it only what changed.
#[derive(Debug, Default)]
pub(crate) struct InterfaceFeed {
    revision: u64,
    player_changed: bool,
    spellbook: Option<(u64, u64, u64)>,
    state: Option<InterfaceState>,
    social: Option<SocialKey>,
    contracts: Option<u64>,
    book: Option<u64>,
    chess: Option<u64>,
    barber: Option<u64>,
    house: Option<u64>,
    house_purchase: Option<u64>,
    journal: Option<u64>,
    titles: Option<(u64, u64)>,
    chat_entry: Option<ChatEntryView>,
}

impl InterfaceFeed {
    /// Everything, for a window that attaches.
    pub(crate) fn snapshot(&mut self, state: &SessionState, active: bool) -> Vec<InterfaceEvent> {
        *self = Self {
            revision: self.revision,
            ..Self::default()
        };
        self.player_changed = true;
        let mut events = vec![InterfaceEvent::Items(items_snapshot(state.world.items()))];
        events.extend(self.follow(state, active, &[]));
        events.push(InterfaceEvent::Trade(Box::new(
            state.inventory.trade_view(&state.world),
        )));
        if let Some(shop) = state
            .inventory
            .vendor_view(&state.world, VendorViewReason::Opened)
        {
            events.push(InterfaceEvent::Vendor(Some(Box::new(shop))));
        }
        events
    }

    /// The world changed: what the item table's copy needs, and a note to
    /// send the player's numbers when they changed.
    pub(crate) fn world_changed(
        &mut self,
        state: &SessionState,
        changes: &[WorldChange],
    ) -> Option<InterfaceEvent> {
        if changes.contains(&WorldChange::PlayerChanged) {
            self.player_changed = true;
        }
        let update = items_update(state.world.items(), changes);
        (!update.is_empty()).then_some(InterfaceEvent::Items(update))
    }

    /// Once per pass: the player's numbers when they or the spellbook
    /// changed, the interaction facts, the social facts and the quest
    /// tracker when they changed, and the appraisals, trade changes, shop
    /// events, salvage panels and mana answers among the host's `events`.
    pub(crate) fn follow(
        &mut self,
        state: &SessionState,
        active: bool,
        host_events: &[HostEvent],
    ) -> Vec<InterfaceEvent> {
        let mut events = Vec::new();
        if let Some(player) = state.world.player() {
            let book = player.spellbook().map(|b| {
                (
                    b.spellbook_revision(),
                    b.enchantment_revision(),
                    b.desired_components_revision(),
                )
            });
            if self.player_changed || book != self.spellbook {
                self.player_changed = false;
                self.spellbook = book;
                self.revision += 1;
                events.push(InterfaceEvent::Player(Box::new(PlayerFacts {
                    revision: self.revision,
                    numbers: player.numbers().clone(),
                    session_seconds: state.session_seconds(),
                })));
            }
        }
        let now = InterfaceState::of(state, active);
        if self.state.as_ref() != Some(&now) {
            events.push(InterfaceEvent::State(Box::new(now.clone())));
            self.state = Some(now);
        }
        let social = SocialFacts::key(state, active);
        if self.social != Some(social) {
            self.social = Some(social);
            events.push(InterfaceEvent::Social(Box::new(SocialFacts::of(
                state,
                active,
                state.session_seconds(),
            ))));
        }
        let contracts = state.communication.contracts().revision();
        if self.contracts != Some(contracts) {
            self.contracts = Some(contracts);
            events.push(InterfaceEvent::Contracts(Box::new(ContractFacts::of(
                state,
            ))));
        }
        let book = state.gameplay.book();
        if self.book != Some(book.revision()) {
            self.book = Some(book.revision());
            events.push(InterfaceEvent::Book(Box::new(book.view())));
        }
        let chess = state.gameplay.chess();
        if self.chess != Some(chess.revision()) {
            self.chess = Some(chess.revision());
            events.push(InterfaceEvent::Chess(Box::new(chess.view())));
        }
        let barber = state.gameplay.barber();
        if self.barber != Some(barber.revision()) {
            self.barber = Some(barber.revision());
            events.push(InterfaceEvent::Barber(Box::new(barber.view())));
        }
        let house = state.gameplay.house();
        if self.house != Some(house.revision()) {
            self.house = Some(house.revision());
            events.push(InterfaceEvent::House(Box::new(house.view())));
        }
        let house_purchase = state.inventory.house_purchase().revision();
        if self.house_purchase != Some(house_purchase) {
            self.house_purchase = Some(house_purchase);
            events.push(InterfaceEvent::HousePurchase(Box::new(
                state.inventory.house_purchase_view(&state.world),
            )));
        }
        let journal = state.communication.journal();
        if self.journal != Some(journal.revision()) {
            self.journal = Some(journal.revision());
            events.push(InterfaceEvent::Journal(Box::new(journal.view())));
        }
        let titles = state.gameplay.titles();
        let key = (titles.revision(), titles.table_generation());
        if self.titles != Some(key) {
            self.titles = Some(key);
            events.push(InterfaceEvent::Titles(Box::new(TitleFacts::of(titles))));
        }
        let chat_entry = ChatEntryView::of(state);
        if self.chat_entry.as_ref() != Some(&chat_entry) {
            events.push(InterfaceEvent::ChatEntry(Box::new(chat_entry.clone())));
            self.chat_entry = Some(chat_entry);
        }
        events.extend(host_events.iter().filter_map(|event| match event {
            HostEvent::Appraisal(view) => Some(InterfaceEvent::Appraisal(view.clone())),
            HostEvent::Trade(view) => Some(InterfaceEvent::Trade(Box::new(view.clone()))),
            HostEvent::OpenSalvage(tool) => Some(InterfaceEvent::OpenSalvage(*tool)),
            HostEvent::BeginGame(board) => Some(InterfaceEvent::BeginGame(*board)),
            HostEvent::OpenContainedContainer(pack) => {
                Some(InterfaceEvent::OpenContainedContainer(*pack))
            }
            HostEvent::Vendor(view) => Some(InterfaceEvent::Vendor(view.clone())),
            HostEvent::ItemMana(change) => Some(InterfaceEvent::ItemMana(*change)),
            HostEvent::Gameplay(event) => match **event {
                crate::gameplay::GameplayEvent::Selection { selected, .. } => {
                    Some(InterfaceEvent::Selection(selected))
                }
                _ => None,
            },
            _ => None,
        }));
        events
    }
}
