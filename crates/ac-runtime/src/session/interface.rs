//! What an attached window's panels read besides the world: the item
//! table (inventory, paperdoll, toolbar, examination), the player's
//! numbers and spellbook (character window, spellbook, spell bars), the
//! interaction facts the session owns (the target mode, the request in
//! flight, the combat mode, the shortcut bar, the open question), the
//! secure trade and the vendor's shop, the social facts (options, friends,
//! squelch list, fellowship, allegiance) and the quest tracker.
//!
//! The window keeps its own copy, built from a snapshot when it attaches
//! and kept in step by updates; it can drop that copy at any time.

use std::sync::Arc;

use ac_core::combat::CombatMode;
use ac_core::items::{ClientObject, ClientObjectTable, ItemManaChange, ShortcutStore};
use ac_core::player::LocalPlayerState;
use ac_core::quests::ContractCatalog;
use ac_core::social::{FriendsState, SquelchState};
use ac_proto::allegiance::AllegianceProfile;

use crate::character::CharacterOptions;
use crate::communication::{ClientConfirmation, JournalView, TrackedContract};
use crate::gameplay::{BarberView, BookView, ChessView, FellowshipView, HouseView, TitleState};
use crate::inventory::{
    AppraisalView, HousePurchaseView, InteractionMode, PendingPlacement, TradeView, VendorView,
};
use crate::world::{ItemChange, WorldChange};

mod feed;
pub(crate) use feed::InterfaceFeed;

/// One message about the panels' facts.
#[derive(Debug, Clone, PartialEq)]
pub enum InterfaceEvent {
    /// The item table changed (or, with `reset`, here it is whole).
    Items(ItemsUpdate),
    /// The player's numbers and spellbook as they stand.
    Player(Box<PlayerFacts>),
    /// The interaction facts as they stand.
    State(Box<InterfaceState>),
    /// An appraisal answered the player's question.
    Appraisal(Box<AppraisalView>),
    /// The secure trade as it stands (after the snapshot and each change).
    Trade(Box<TradeView>),
    /// The vendor's shop opened, refreshed or its lists changed (`Some`,
    /// with why), or it closed (`None`).
    Vendor(Option<Box<VendorView>>),
    /// The social facts as they stand.
    Social(Box<SocialFacts>),
    /// The quest tracker as it stands.
    Contracts(Box<ContractFacts>),
    /// The open book as it stands (after the snapshot and each change).
    Book(Box<BookView>),
    /// The house as it stands (after the snapshot and each change).
    House(Box<HouseView>),
    /// The slumlord window as it stands (after the snapshot and each
    /// change).
    HousePurchase(Box<HousePurchaseView>),
    /// The journal as it stands (after the snapshot and each change).
    Journal(Box<JournalView>),
    /// A salvaging tool was used: the salvage panel opens on it.
    OpenSalvage(u32),
    /// The chess game as it stands (after the snapshot and each change).
    Chess(Box<ChessView>),
    /// The barber as it stands (after the snapshot and each change).
    Barber(Box<BarberView>),
    /// Someone else's game board was used: the game window shows.
    BeginGame(u32),
    /// A pack the player carries was used: the inventory window shows and
    /// opens on it (the main pack when it is the player).
    OpenContainedContainer(u32),
    /// The server answered a mana query (each answer once, valid or not):
    /// the selected-object strip shows its mana bar or closes the query.
    ItemMana(ItemManaChange),
    /// The plugins' UI changed (or, starting with a clear, here it is
    /// whole): the window's copy applies these in order.
    PluginUi(Vec<ac_plugin_ui::UiChange>),
    /// The character's titles as they stand (after the snapshot and each
    /// change).
    Titles(Box<TitleFacts>),
    /// The chat entry and the reply targets as they stand (after the
    /// snapshot and each change).
    ChatEntry(Box<ChatEntryView>),
    /// The selection changed (each change once, in order): what follows
    /// every change, not only the selection a frame ends on, reads these.
    Selection(Option<u32>),
}

/// What the chat window shows of the one chat entry every front end types
/// into (OpenAC `RuntimeChatEntryOwner`, which the window's field is a
/// view of) and of the reply targets (`ChatCommandTargetState`).
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct ChatEntryView {
    /// The line as it stands, unsent.
    pub draft: String,
    /// Moves when the draft changed other than by the window's own typing
    /// (a staged line, a recall, a send): the window shows the draft then.
    pub shown_revision: u64,
    /// A staged line waits to be sent: the window hands the entry the
    /// keyboard.
    pub composed: bool,
    /// Where a plain line goes.
    pub channel: crate::communication::ChatChannelKind,
    /// The listener a plain line goes to on the tell focus.
    pub tell_target: Option<String>,
    /// That listener's object id (0: by name).
    pub tell_target_guid: u32,
    /// Who last sent the player a tell (`/r`, the reply key).
    pub last_incoming_tell_sender: Option<String>,
    /// Who last spoke on the monarch channel (the monarch reply key).
    pub last_monarch_sender: Option<String>,
    /// Who last spoke on the patron channel (the patron reply key).
    pub last_patron_sender: Option<String>,
}

/// The titles as the titles page reads them.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct TitleFacts {
    /// The displayed title (0: none).
    pub display_title_id: u32,
    /// Every earned title, ascending.
    pub title_ids: Vec<u32>,
    /// Counts changes.
    pub revision: u64,
    /// Counts whole tables (each table the server sends, and the session
    /// reset): the page drops its selection on a new table.
    pub table_generation: u64,
}

impl TitleFacts {
    /// The facts of `titles`.
    #[must_use]
    pub fn of(titles: &TitleState) -> Self {
        Self {
            display_title_id: titles.display_title_id(),
            title_ids: titles.earned_title_ids().collect(),
            revision: titles.revision(),
            table_generation: titles.table_generation(),
        }
    }
}

/// What the social panel reads: the character options, the friends and
/// squelch lists, the fellowship and the allegiance.
#[derive(Debug, Clone, PartialEq)]
pub struct SocialFacts {
    /// The character is in the world (a panel's declarations count only
    /// then).
    pub in_world: bool,
    /// The character options.
    pub options: CharacterOptions,
    /// The friends list.
    pub friends: FriendsState,
    /// The squelch database.
    pub squelch: SquelchState,
    /// The fellowship.
    pub fellowship: FellowshipView,
    /// The allegiance as the server described it, with the members'
    /// heritage, gender and experience passed up.
    pub allegiance: Option<AllegianceProfile>,
    /// The player's rank in it.
    pub allegiance_rank: u32,
    /// Moves on every allegiance change.
    pub allegiance_revision: u64,
}

/// What the journal's contracts page reads: the tracker and the authored
/// contract texts.
#[derive(Debug, Clone, PartialEq)]
pub struct ContractFacts {
    /// Every tracked contract, by id.
    pub contracts: Vec<TrackedContract>,
    /// The contract the server put on display (0: none).
    pub display_contract_id: u32,
    /// Moves on every change.
    pub revision: u64,
    /// The dat's contract table.
    pub catalog: Arc<ContractCatalog>,
}

/// A change to the window's copy of the item table.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct ItemsUpdate {
    /// Drop the copy before applying this (a snapshot, or the table was
    /// cleared).
    pub reset: bool,
    /// Objects added or changed: their whole current records.
    pub objects: Vec<ClientObject>,
    /// Objects that left the table.
    pub removed: Vec<u32>,
    /// Contents lists that changed: the list as it stands, or `None` when
    /// the container has none any more.
    pub contents: Vec<(u32, Option<Vec<u32>>)>,
}

impl ItemsUpdate {
    /// Nothing to change.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        !self.reset
            && self.objects.is_empty()
            && self.removed.is_empty()
            && self.contents.is_empty()
    }
}

/// The whole table, for a window that attaches.
#[must_use]
pub fn items_snapshot(table: &ClientObjectTable) -> ItemsUpdate {
    ItemsUpdate {
        reset: true,
        objects: table.objects().cloned().collect(),
        removed: Vec::new(),
        contents: table
            .contents_lists()
            .map(|(container, list)| (container, Some(list.to_vec())))
            .collect(),
    }
}

/// What `changes` did to the table, as an update of a copy: each object
/// named once with its current record, each contents list it touched as it
/// stands. A cleared table is a whole snapshot.
#[must_use]
pub fn items_update(table: &ClientObjectTable, changes: &[WorldChange]) -> ItemsUpdate {
    use std::collections::BTreeSet;
    let mut objects = BTreeSet::new();
    let mut lists = BTreeSet::new();
    let mut cleared = false;
    for change in changes {
        let WorldChange::Item(change) = change else {
            continue;
        };
        match *change {
            ItemChange::Added(guid) | ItemChange::Updated(guid) => {
                objects.insert(guid);
                lists.insert(guid);
                if let Some(item) = table.get(guid) {
                    lists.insert(item.container_id);
                }
            }
            ItemChange::Moved {
                guid,
                previous,
                current,
                ..
            } => {
                objects.insert(guid);
                lists.insert(previous.container_id);
                lists.insert(current.container_id);
            }
            ItemChange::Removed { guid, .. } => {
                objects.insert(guid);
                lists.insert(guid);
            }
            ItemChange::ContentsChanged(container) => {
                lists.insert(container);
            }
            ItemChange::Cleared => cleared = true,
        }
    }
    if cleared {
        return items_snapshot(table);
    }
    let mut update = ItemsUpdate::default();
    for guid in objects {
        match table.get(guid) {
            Some(item) => update.objects.push(item.clone()),
            None => update.removed.push(guid),
        }
    }
    update.contents = lists
        .into_iter()
        .filter(|&c| c != 0)
        .map(|c| (c, table.contents_list(c).map(<[u32]>::to_vec)))
        .collect();
    update
}

/// Apply `update` to a copy of a table.
pub fn apply_items_update(copy: &mut ClientObjectTable, update: &ItemsUpdate) {
    let mut events = Vec::new();
    if update.reset {
        copy.clear(&mut events);
    }
    for item in &update.objects {
        copy.add_or_update(item.clone(), &mut events);
    }
    for &guid in &update.removed {
        copy.remove(guid, &mut events);
    }
    for (container, list) in &update.contents {
        copy.set_contents_list(*container, list.clone());
    }
}

/// The player's numbers (vitals, attributes, skills, int properties) and
/// spellbook, as they stand. Two are equal when they carry the same
/// revision: the session numbers each one it sends.
#[derive(Debug, Clone)]
pub struct PlayerFacts {
    /// Bumped by the session for every one it sends.
    pub revision: u64,
    /// The numbers, with the spellbook inside.
    pub numbers: LocalPlayerState,
    /// The session clock when these were sent, seconds (enchantment start
    /// times are on it).
    pub session_seconds: f64,
}

impl PartialEq for PlayerFacts {
    fn eq(&self, other: &Self) -> bool {
        self.revision == other.revision
    }
}

/// The interaction facts the session owns that the panels show.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct InterfaceState {
    /// The player's object id (0 before the character entered).
    pub player: u32,
    /// What the next object clicked does.
    pub mode: InteractionMode,
    /// The item whose request is in flight (drawn waiting).
    pub pending_item: Option<u32>,
    /// The item on its way into a pack, shown waiting at the slot its
    /// request names until the server answers.
    pub pending_placement: Option<PendingPlacement>,
    /// An item request may be made now.
    pub can_make_request: bool,
    /// The combat mode.
    pub combat_mode: CombatMode,
    /// Uses in flight plus a question being answered (the busy cursor);
    /// a question past its time limit no longer counts.
    pub busy_count: u32,
    /// The shortcut bar.
    pub shortcuts: ShortcutStore,
    /// The open questions for the dialog: the server's, then the
    /// client's own (`/die`).
    pub confirmations: Vec<ClientConfirmation>,
    /// The selected object's health as the panels show it: `Some` while the
    /// health stream follows the selection (a player, a pet or anything
    /// attackable), with its health share once the server sent one.
    pub selected_health: Option<SelectedHealth>,
    /// The selected object's mana share as the server last answered its
    /// mana query: `Some` while an answer for the selection is known.
    pub selected_mana: Option<SelectedMana>,
    /// The outside container the player is looking into (0: none).
    pub external_container: u32,
    /// The player is within use range of it (its window closes when not).
    pub external_container_in_range: bool,
    /// The account name spell formulas are personalised for (the
    /// examination window shows a spell's components by it).
    pub account: String,
}

/// The selected object's health, for the selected-object strip.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct SelectedHealth {
    /// The object.
    pub object_id: u32,
    /// Its health share, 0..=1, once the server sent one.
    pub percent: Option<f32>,
}

/// The selected item's mana, for the selected-object strip's mana bar.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct SelectedMana {
    /// The item.
    pub object_id: u32,
    /// Its mana share, 0..=1.
    pub fraction: f32,
}

#[cfg(test)]
mod tests {
    use super::feed::busy_count;
    use super::*;
    use crate::session::{HostEvent, SessionState};
    use ac_core::items::{ItemType, WeenieData};

    fn data(guid: u32, container: u32) -> WeenieData {
        WeenieData {
            guid,
            name: Some(format!("item {guid:x}")),
            item_type: Some(ItemType::MISC),
            container_id: Some(container),
            ..WeenieData::default()
        }
    }

    fn same(a: &ClientObjectTable, b: &ClientObjectTable) {
        let ids = |t: &ClientObjectTable| t.objects().map(|o| o.object_id()).collect::<Vec<_>>();
        assert_eq!(ids(a), ids(b));
        // Identities are each table's own numbering; the rest is copied.
        let key = |o: &ClientObject| {
            (
                o.name.clone(),
                o.container_id,
                o.container_slot,
                o.wielder_id,
                o.currently_equipped_location,
                o.stack_size,
            )
        };
        for o in a.objects() {
            assert_eq!(key(o), key(b.get(o.object_id()).unwrap()));
        }
        let lists = |t: &ClientObjectTable| {
            t.contents_lists()
                .map(|(c, l)| (c, l.to_vec()))
                .collect::<Vec<_>>()
        };
        assert_eq!(lists(a), lists(b));
    }

    #[test]
    fn a_copy_built_from_the_snapshot_and_updates_matches_the_table() {
        let mut table = ClientObjectTable::new();
        let mut events = Vec::new();
        table.ingest(&data(0x8000_0001, 0x5000_0001), &mut events);
        table.replace_contents_ids(0x5000_0001, &[0x8000_0001], &mut events);
        let mut copy = ClientObjectTable::new();
        apply_items_update(&mut copy, &items_snapshot(&table));
        same(&table, &copy);

        // A second item arrives and the first leaves.
        let mut events = Vec::new();
        table.ingest(&data(0x8000_0002, 0x5000_0001), &mut events);
        table.replace_contents_ids(0x5000_0001, &[0x8000_0002], &mut events);
        table.remove(0x8000_0001, &mut events);
        let changes: Vec<WorldChange> = events
            .iter()
            .filter_map(ItemChange::of)
            .map(WorldChange::Item)
            .collect();
        let update = items_update(&table, &changes);
        assert_eq!(update.removed, [0x8000_0001]);
        apply_items_update(&mut copy, &update);
        same(&table, &copy);

        // A cleared table is sent whole.
        let mut events = Vec::new();
        table.clear(&mut events);
        let update = items_update(&table, &[WorldChange::Item(ItemChange::Cleared)]);
        assert!(update.reset);
        apply_items_update(&mut copy, &update);
        same(&table, &copy);
    }

    // OpenAC clears its object table when a session ends and the
    // inventory, toolbar and the other item panels rebuild from the
    // cleared table: a window's copy of the items follows the same way,
    // or it shows the last character's items under the next one.
    #[test]
    fn a_session_ending_empties_the_windows_copy_of_the_items() {
        use crate::communication::CommunicationPaths;
        let mut state = SessionState::new(CommunicationPaths::default());
        state.world.set_player(0x5000_000B);
        state.world.ingest_item(&data(0x8000_0001, 0x5000_000B));
        let mut feed = InterfaceFeed::default();
        let mut copy = ClientObjectTable::new();
        for event in feed.snapshot(&state, true) {
            if let InterfaceEvent::Items(update) = event {
                apply_items_update(&mut copy, &update);
            }
        }
        assert!(copy.get(0x8000_0001).is_some());
        let _ = state.world.drain_changes().count();

        state.end_generation(0);
        let changes: Vec<WorldChange> = state.world.drain_changes().collect();
        let Some(InterfaceEvent::Items(update)) = feed.world_changed(&state, &changes) else {
            panic!("the window hears nothing of the items leaving");
        };
        assert!(update.reset);
        apply_items_update(&mut copy, &update);
        assert!(copy.get(0x8000_0001).is_none());
        assert_eq!(copy.object_count(), 0);
    }

    #[test]
    fn a_window_hears_the_facts_once_and_then_only_their_changes() {
        use crate::communication::CommunicationPaths;
        let mut state = SessionState::new(CommunicationPaths::default());
        state.world.set_player(0x5000_000B);
        state
            .world
            .apply_player_description(&crate::world::tests::empty_description());
        let mut feed = InterfaceFeed::default();
        let first = feed.snapshot(&state, true);
        assert!(matches!(
            first[0],
            InterfaceEvent::Items(ItemsUpdate { reset: true, .. })
        ));
        assert!(first.iter().any(|e| matches!(e, InterfaceEvent::Player(_))));
        assert!(first.iter().any(|e| matches!(e, InterfaceEvent::State(_))));
        assert!(feed.follow(&state, true, &[]).is_empty(), "nothing changed");
        // The player changed: his numbers go again, under a new revision.
        assert!(
            feed.world_changed(&state, &[WorldChange::PlayerChanged])
                .is_none()
        );
        let again = feed.follow(&state, true, &[]);
        assert!(matches!(&again[..], [InterfaceEvent::Player(p)] if p.revision == 2));
        // Leaving the world changes what may be asked, and the social
        // panel's declarations stop counting.
        let left = feed.follow(&state, false, &[]);
        assert!(
            matches!(&left[..], [InterfaceEvent::State(s), InterfaceEvent::Social(f)]
                if !s.can_make_request && !f.in_world),
            "{left:?}"
        );
    }

    #[test]
    fn a_window_hears_the_trade_the_shop_the_social_facts_and_the_tracker() {
        use crate::communication::CommunicationPaths;
        let mut state = SessionState::new(CommunicationPaths::default());
        state.world.set_player(0x5000_000B);
        let mut feed = InterfaceFeed::default();
        let first = feed.snapshot(&state, true);
        let has = |events: &[InterfaceEvent], f: fn(&InterfaceEvent) -> bool| {
            events.iter().filter(|e| f(e)).count()
        };
        assert_eq!(has(&first, |e| matches!(e, InterfaceEvent::Trade(_))), 1);
        assert_eq!(
            has(
                &first,
                |e| matches!(e, InterfaceEvent::Social(s) if s.in_world)
            ),
            1
        );
        assert_eq!(
            has(&first, |e| matches!(e, InterfaceEvent::Contracts(_))),
            1
        );
        assert_eq!(
            has(&first, |e| matches!(e, InterfaceEvent::Vendor(_))),
            0,
            "no shop is open"
        );
        // The trade and the shop reach the window as the host hears them.
        let trade = TradeView {
            open: true,
            partner_guid: 0x5000_000C,
            revision: 3,
            ..TradeView::default()
        };
        let passed = feed.follow(
            &state,
            true,
            &[
                HostEvent::Trade(trade.clone()),
                HostEvent::Vendor(None),
                HostEvent::InterfaceText("ignored".into()),
            ],
        );
        assert_eq!(
            passed,
            [
                InterfaceEvent::Trade(Box::new(trade)),
                InterfaceEvent::Vendor(None)
            ]
        );
        // An options change sends the social facts again.
        state.options.replace(0x0000_0004, 0, false);
        let again = feed.follow(&state, true, &[]);
        assert!(
            matches!(&again[..], [InterfaceEvent::Social(s)] if s.options.options1() == 4),
            "{again:?}"
        );
    }

    /// OpenAC's examination window hears every selection change: a window
    /// hears each change the pass made, in order, not only the selection
    /// it ends on.
    #[test]
    fn a_window_hears_every_selection_change_in_order() {
        use crate::communication::CommunicationPaths;
        use crate::gameplay::GameplayEvent;
        let mut state = SessionState::new(CommunicationPaths::default());
        state.world.set_player(0x5000_000B);
        let mut feed = InterfaceFeed::default();
        let _ = feed.snapshot(&state, true);
        let selection = |selected| {
            HostEvent::Gameplay(Box::new(GameplayEvent::Selection {
                selected,
                name: None,
            }))
        };
        let passed = feed.follow(
            &state,
            true,
            &[
                selection(Some(0x8000_0002)),
                selection(Some(0x8000_0001)),
                selection(None),
            ],
        );
        assert_eq!(
            passed,
            [
                InterfaceEvent::Selection(Some(0x8000_0002)),
                InterfaceEvent::Selection(Some(0x8000_0001)),
                InterfaceEvent::Selection(None),
            ]
        );
    }

    /// OpenAC's window is a view of `RuntimeChatEntryOwner`: here the
    /// window hears the entry at attach and whenever it changes; a line
    /// typed in a front end moves the draft but not the shown revision (that
    /// front end shows it already), a staged line moves both.
    #[test]
    fn a_window_hears_the_chat_entry_and_which_changes_to_show() {
        use crate::communication::{ChatChannelKind, CommunicationPaths};
        let mut state = SessionState::new(CommunicationPaths::default());
        let mut feed = InterfaceFeed::default();
        let entry = |events: &[InterfaceEvent]| {
            events.iter().find_map(|e| match e {
                InterfaceEvent::ChatEntry(v) => Some((**v).clone()),
                _ => None,
            })
        };
        let first = entry(&feed.snapshot(&state, true)).expect("sent at attach");
        assert_eq!(first.channel, ChatChannelKind::Say);
        assert!(entry(&feed.follow(&state, true, &[])).is_none());
        state.communication.chat_entry_mut().type_draft("hel");
        let typed = entry(&feed.follow(&state, true, &[])).expect("the draft moved");
        assert_eq!(typed.draft, "hel");
        assert_eq!(typed.shown_revision, first.shown_revision);
        state.communication.chat_entry_mut().clear();
        state.communication.chat_entry_mut().compose("/vt nav");
        state
            .communication
            .chat_entry_mut()
            .set_channel(ChatChannelKind::Trade);
        let staged = entry(&feed.follow(&state, true, &[])).expect("staged");
        assert_eq!(staged.draft, "/vt nav");
        assert!(staged.composed);
        assert!(staged.shown_revision > typed.shown_revision);
        assert_eq!(staged.channel, ChatChannelKind::Trade);
    }

    #[test]
    fn nothing_changed_is_an_empty_update() {
        let table = ClientObjectTable::new();
        assert!(items_update(&table, &[WorldChange::PlayerChanged]).is_empty());
    }

    /// The busy cursor counts the player's own examine only: a question
    /// asked for the player (a plugin's identify, a walk's door check)
    /// leaves the cursor alone, while the player's examine counts, also
    /// when it takes over a question asked for the player.
    #[test]
    fn only_the_players_own_examine_counts_toward_the_busy_cursor() {
        use super::super::{Clocks, Outbox, SessionCommand};
        use crate::communication::CommunicationPaths;
        use crate::inventory::{ItemCommand, ObjectRef};
        const PLAYER: u32 = 0x5000_000B;
        const ITEM: u32 = 0x8000_0001;
        const OTHER: u32 = 0x8000_0002;
        let clocks = Clocks {
            wall_unix_ms: 1_790_000_000_000,
            session: std::time::Duration::ZERO,
        };
        let fresh = || {
            let mut state = SessionState::new(CommunicationPaths::default());
            state.world.set_player(PLAYER);
            state.world.ingest_item(&data(ITEM, PLAYER));
            state.world.ingest_item(&data(OTHER, PLAYER));
            (state, Outbox::new())
        };
        let examine = |state: &mut SessionState, outbox: &mut Outbox, object: u32| {
            let command = SessionCommand::Item(ItemCommand::Appraise(ObjectRef::Guid(object)));
            assert!(state.command(command, outbox, true, clocks).is_none());
        };

        // A plugin's identify (or a walk's door check): no busy cursor.
        let (mut state, mut outbox) = fresh();
        assert!(state.inventory.appraise_quietly(ITEM, 0, true, &mut outbox));
        assert_eq!(state.inventory.interaction().awaiting_appraisal(), ITEM);
        assert_eq!(busy_count(&state), 0, "an identify shows no busy cursor");

        // The player's own examine: busy until its answer.
        let (mut state, mut outbox) = fresh();
        examine(&mut state, &mut outbox, ITEM);
        assert_eq!(state.inventory.interaction().awaiting_appraisal(), ITEM);
        assert_eq!(busy_count(&state), 1);

        // The player's examine takes over a question asked for him.
        let (mut state, mut outbox) = fresh();
        assert!(state.inventory.appraise_quietly(ITEM, 0, true, &mut outbox));
        examine(&mut state, &mut outbox, OTHER);
        assert_eq!(state.inventory.interaction().awaiting_appraisal(), OTHER);
        assert_eq!(busy_count(&state), 1);
    }
}
