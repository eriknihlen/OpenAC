//! The window's copy of what the panels read: the item table, the player's
//! numbers and spellbook, the interaction facts, the trade, the vendor's
//! shop, the social facts and the quest tracker, kept from the session's
//! interface stream. Dropped with the window and rebuilt from the snapshot
//! on the next attach.

use ac_core::items::vendor::is_split_exempt;
use ac_core::items::{ClientObject, ClientObjectTable, ItemManaChange, StackSplitQuantity};
use ac_core::player::{AttributeKind, LocalPlayerState};
use ac_runtime::communication::JournalView;
use ac_runtime::gameplay::{BookView, ChessView, HouseView};
use ac_runtime::inventory::{
    AppraisalView, HousePurchaseView, InteractionMode, TradeView, VendorView,
};
use ac_runtime::session::interface::{ChatEntryView, TitleFacts};
use ac_runtime::session::interface::{
    ContractFacts, InterfaceEvent, InterfaceState, PlayerFacts, SocialFacts, apply_items_update,
};
use ac_ui::panels::appraisal::{AppraisalNames, NameTables};
use ac_ui::panels::items::{ItemsView, NameResolver};

/// What changed since the panels were last fed.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct Dirty {
    /// The item table.
    pub items: bool,
    /// The player's numbers or spellbook.
    pub player: bool,
    /// The interaction facts.
    pub state: bool,
    /// The trade.
    pub trade: bool,
    /// The social facts.
    pub social: bool,
    /// The quest tracker.
    pub contracts: bool,
    /// The open book.
    pub book: bool,
    /// The house.
    pub house: bool,
    /// The slumlord window.
    pub house_purchase: bool,
    /// The journal.
    pub journal: bool,
    /// The titles.
    pub titles: bool,
    /// The chat entry.
    pub chat_entry: bool,
}

impl Dirty {
    /// The item table, the player or the interaction facts (what the item,
    /// character and magic panels show).
    #[must_use]
    pub fn any(self) -> bool {
        self.items || self.player || self.state
    }
}

/// See the module documentation.
#[derive(Debug, Default)]
pub struct InterfaceFacts {
    /// The item table.
    pub objects: ClientObjectTable,
    /// The player's numbers, once sent.
    pub player: Option<PlayerFacts>,
    /// The interaction facts.
    pub state: InterfaceState,
    /// Appraisals that arrived and are not shown yet, oldest first.
    pub appraisals: Vec<AppraisalView>,
    /// The secure trade.
    pub trade: TradeView,
    /// The shop as last sent (`None`: none open).
    pub shop: Option<Box<VendorView>>,
    /// Shop events that arrived and the vendor window has not seen yet,
    /// oldest first.
    pub shop_events: Vec<Option<Box<VendorView>>>,
    /// The social facts, once sent.
    pub social: Option<SocialFacts>,
    /// The quest tracker, once sent.
    pub contracts: Option<ContractFacts>,
    /// When the player's numbers arrived (their session clock runs on
    /// from it).
    player_at: Option<std::time::Instant>,
    /// The open book, once sent.
    pub book: Option<BookView>,
    /// The house, once sent.
    pub house: Option<HouseView>,
    /// The slumlord window.
    pub house_purchase: HousePurchaseView,
    /// The journal, once sent.
    pub journal: Option<JournalView>,
    /// The titles, once sent.
    pub titles: Option<TitleFacts>,
    /// Salvaging tools used, oldest first (the salvage panel opens on
    /// the last).
    pub salvage_opens: Vec<u32>,
    /// The chess game, once sent.
    pub chess: Option<ChessView>,
    /// Game boards used, oldest first: the game window shows for each.
    pub game_begins: Vec<u32>,
    /// Carried packs used, oldest first: the inventory window shows and
    /// opens on each.
    pub container_opens: Vec<u32>,
    /// Mana query answers the selected-object strip has not heard yet,
    /// oldest first.
    pub item_mana: Vec<ItemManaChange>,
    /// The selection changes the window has not followed yet, oldest
    /// first (the examination window follows each).
    pub selection_changes: Vec<Option<u32>>,
    /// The plugins' UI: their windows, canvases, images, labels and lines.
    pub plugin_ui: ac_plugin_ui::PluginUiView,
    /// The session's chat entry and reply targets, once sent.
    pub chat_entry: Option<ChatEntryView>,
    dirty: Dirty,
}

impl InterfaceFacts {
    /// Apply one message of the stream.
    pub fn apply(&mut self, event: InterfaceEvent) {
        match event {
            InterfaceEvent::Items(update) => {
                apply_items_update(&mut self.objects, &update);
                self.dirty.items = true;
            }
            InterfaceEvent::Player(facts) => {
                self.player = Some(*facts);
                self.player_at = Some(std::time::Instant::now());
                self.dirty.player = true;
            }
            InterfaceEvent::State(state) => {
                self.state = *state;
                self.dirty.state = true;
            }
            InterfaceEvent::Appraisal(view) => self.appraisals.push(*view),
            InterfaceEvent::Trade(view) => {
                self.trade = *view;
                self.dirty.trade = true;
            }
            InterfaceEvent::Vendor(view) => {
                self.shop.clone_from(&view);
                self.shop_events.push(view);
            }
            InterfaceEvent::Social(facts) => {
                self.social = Some(*facts);
                self.dirty.social = true;
            }
            InterfaceEvent::Contracts(facts) => {
                self.contracts = Some(*facts);
                self.dirty.contracts = true;
            }
            InterfaceEvent::Book(view) => {
                self.book = Some(*view);
                self.dirty.book = true;
            }
            InterfaceEvent::House(view) => {
                self.house = Some(*view);
                self.dirty.house = true;
            }
            InterfaceEvent::HousePurchase(view) => {
                self.house_purchase = *view;
                self.dirty.house_purchase = true;
            }
            InterfaceEvent::Journal(view) => {
                self.journal = Some(*view);
                self.dirty.journal = true;
            }
            InterfaceEvent::Titles(view) => {
                self.titles = Some(*view);
                self.dirty.titles = true;
            }
            InterfaceEvent::OpenSalvage(tool) => self.salvage_opens.push(tool),
            InterfaceEvent::Chess(view) => self.chess = Some(*view),
            InterfaceEvent::BeginGame(board) => self.game_begins.push(board),
            InterfaceEvent::OpenContainedContainer(pack) => self.container_opens.push(pack),
            InterfaceEvent::ItemMana(change) => self.item_mana.push(change),
            InterfaceEvent::Selection(selected) => self.selection_changes.push(selected),
            InterfaceEvent::PluginUi(changes) => self.plugin_ui.apply_all(changes),
            InterfaceEvent::ChatEntry(view) => {
                self.chat_entry = Some(*view);
                self.dirty.chat_entry = true;
            }
        }
    }

    /// The session clock now, seconds (the enchantments' clock): the
    /// clock the numbers were sent at, run on since they arrived.
    #[must_use]
    pub fn session_now(&self) -> Option<f64> {
        let p = self.player.as_ref()?;
        let at = self.player_at?;
        Some(p.session_seconds + at.elapsed().as_secs_f64())
    }

    /// What changed since the last call.
    pub fn take_dirty(&mut self) -> Dirty {
        std::mem::take(&mut self.dirty)
    }

    /// The player's numbers.
    #[must_use]
    pub fn numbers(&self) -> Option<&LocalPlayerState> {
        self.player.as_ref().map(|p| &p.numbers)
    }

    /// `selected` is a listing of the open shop whose split starts at one
    /// (OpenAC's `IsVendorSplitExempt` binding: held by the vendor, of a
    /// split-exempt type).
    #[must_use]
    pub fn vendor_split_exempt(&self, selected: Option<u32>) -> bool {
        let Some(vendor) = self.shop.as_ref().map(|s| s.vendor_guid) else {
            return false;
        };
        selected
            .and_then(|guid| self.objects.get(guid))
            .is_some_and(|o| o.container_id == vendor && is_split_exempt(o.item_type))
    }

    /// The player's current strength (burden capacity), once known.
    #[must_use]
    pub fn strength(&self) -> Option<i32> {
        self.numbers()
            .and_then(|n| n.get_effective_attribute(AttributeKind::Strength))
    }

    /// What the item panels read: the table, the player, `selected`, the
    /// interaction facts, the items with a request in flight (`pending`),
    /// the selected stack's split quantity, the player's strength and name,
    /// and how an item's name is composed (`names`, see [`composed_names`]).
    /// The composer is required: an item surface without it would caption
    /// the plain name and disagree with the selection caption.
    #[must_use]
    pub fn items_view<'a>(
        &'a self,
        selected: Option<u32>,
        pending: &'a [u32],
        split: Option<&'a StackSplitQuantity>,
        names: NameResolver<'a>,
    ) -> ItemsView<'a> {
        let state = &self.state;
        let player = state.player;
        ItemsView {
            selected,
            target_mode: state.mode != InteractionMode::None,
            target_source: state.mode.source(),
            pending_sources: pending,
            pending_placement: state.pending_placement,
            can_make_request: state.can_make_request,
            shortcuts: Some(&state.shortcuts),
            combat_mode: state.combat_mode,
            split,
            vendor_split_exempt: self.vendor_split_exempt(selected),
            strength: self.strength(),
            open_vendor: self.shop.as_ref().map_or(0, |s| s.vendor_guid),
            owner_name: self.objects.get(player).map(|o| o.name.as_str()),
            names: Some(names),
            ..ItemsView::new(&self.objects, player)
        }
    }
}

/// An item's displayed name from the name tables: its appropriate name
/// with its material in front.
pub fn composed_names(tables: &NameTables) -> impl Fn(&ClientObject) -> String + Copy + '_ {
    move |o| AppraisalNames::appropriate_name(tables, o)
}

#[cfg(test)]
mod tests {
    use super::*;
    use ac_core::items::ClientObject;
    use ac_runtime::session::interface::ItemsUpdate;

    #[test]
    fn the_stream_builds_the_copy_and_says_what_changed() {
        let mut facts = InterfaceFacts::default();
        facts.apply(InterfaceEvent::Items(ItemsUpdate {
            reset: true,
            objects: vec![ClientObject::new(0x8000_0001)],
            removed: Vec::new(),
            contents: vec![(0x5000_0001, Some(vec![0x8000_0001]))],
        }));
        assert_eq!(facts.objects.get_contents(0x5000_0001), [0x8000_0001]);
        let dirty = facts.take_dirty();
        assert!(dirty.items && !dirty.player && !dirty.state);
        assert!(!facts.take_dirty().any());
        facts.apply(InterfaceEvent::State(Box::default()));
        assert!(facts.take_dirty().state);
        // The shop's events queue for the vendor window; the last one is
        // the shop as it stands.
        facts.apply(InterfaceEvent::Vendor(None));
        facts.apply(InterfaceEvent::Trade(Box::default()));
        assert_eq!(facts.shop_events.len(), 1);
        assert!(facts.shop.is_none());
        let dirty = facts.take_dirty();
        assert!(dirty.trade && !dirty.any());
    }
}
