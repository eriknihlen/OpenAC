//! The slumlord window's state: the last house profile a slumlord sent and
//! the items the player dragged onto each of its two tabs.
//!
//! Nothing is sent from the staging itself; the window's button sends the
//! staged items with a buy or a maintenance payment, and the server takes
//! only what is still owed. The window closes when the player walks
//! [`CLOSE_DISTANCE`] metres from the slumlord, on a teleport, when the
//! slumlord leaves the world, and at the session's end.

use std::collections::HashMap;
use std::sync::Arc;

use ac_core::items::{ClientObject, ClientObjectTable, ItemType};
use ac_proto::house::{HousePayment, HouseProfile};

/// The pyreal's weenie class. Trade notes pay towards it too.
pub const PYREAL_WCID: u32 = 273;

/// The slumlord window closes at this distance, metres, whatever the
/// slumlord's own use radius.
pub const CLOSE_DISTANCE: f32 = 9.0;

/// Whether the slumlord window stays open: with no place for the player
/// nothing is decided (it stays); a slumlord no longer in the world closes
/// it; otherwise both bodies are measured as the vendor's window measures
/// them, against the fixed [`CLOSE_DISTANCE`] rather than the slumlord's
/// use radius.
#[must_use]
pub fn stays_open(
    player: Option<ac_physics::object_range::RangeBody>,
    slumlord: Option<ac_physics::object_range::RangeBody>,
) -> bool {
    super::shop::vendor_stays_open(player, slumlord, CLOSE_DISTANCE)
}

/// The apartment house type: buying one asks nothing first.
pub const APARTMENT_TYPE: u32 = 4;

/// What the player is told when an item it does not carry is offered.
pub const NOT_CARRYING: &str = "You can only trade items you are carrying";

/// What is asked before buying a house other than an apartment.
pub const BUY_CONFIRMATION: &str = "When you buy a landscape house like this one, you are restricted from buying another for 30 days. Are you sure you want to buy this house?";

/// What is asked before paying the maintenance of a house someone else
/// owns.
pub const OTHERS_MAINTENANCE_CONFIRMATION: &str =
    "You are paying maintenance on someone else's house. Are you sure you wish to continue?";

/// The two pages of the slumlord window.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum HousePurchaseTab {
    /// Buying the house.
    Buy,
    /// Paying its maintenance.
    Rent,
}

/// What happened to an item offered to the slumlord window.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum HouseStageResult {
    /// It is on the tab's strip.
    Staged,
    /// No slumlord window is open.
    NotOpen,
    /// A house someone owns is not for sale.
    AlreadyOwned,
    /// Nobody pays maintenance on a house nobody owns.
    NotOwned,
    /// The house's maintenance is free.
    MaintenanceFree,
    /// The player does not carry it (or wields it).
    NotInInventory,
    /// It is on the strip already.
    AlreadyStaged,
    /// No line of the tab asks for it.
    NotRequired,
    /// Its line is covered already.
    AlreadyCovered,
    /// A trade note worth more than is still owed (a note is not split).
    NoteTooLarge,
}

/// What the slumlord window's controls ask of the session.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum HousePurchaseCommand {
    /// Put an item (a pack: each item in it the tab needs) on a tab's
    /// strip; a refusal of an item the player does not carry is said.
    Stage {
        /// The tab.
        tab: HousePurchaseTab,
        /// The item.
        item: u32,
    },
    /// Take an item off a tab's strip.
    Unstage {
        /// The tab.
        tab: HousePurchaseTab,
        /// The item.
        item: u32,
    },
    /// Send the tab's staged items (a buy or a maintenance payment) to
    /// `slumlord`, and empty its strip. Nothing when the window moved on
    /// to another slumlord meanwhile, or the tab may not be sent.
    Submit {
        /// The tab.
        tab: HousePurchaseTab,
        /// The slumlord the window showed when asked.
        slumlord: u32,
    },
    /// Close the window.
    Close,
}

/// The slumlord window as a host shows it.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct HousePurchaseView {
    /// Counts changes.
    pub revision: u64,
    /// The last profile (`None`: the window is closed).
    pub profile: Option<HouseProfile>,
    /// The Buy tab's staged items still carried, in staging order.
    pub buy: Vec<u32>,
    /// The Maintenance tab's staged items still carried, in staging order.
    pub rent: Vec<u32>,
    /// How much of each Buy line the staged items pay.
    pub buy_amounts: Vec<i32>,
    /// How much of each Maintenance line the staged items pay.
    pub rent_amounts: Vec<i32>,
}

impl HousePurchaseView {
    /// The window is open.
    #[must_use]
    pub fn is_open(&self) -> bool {
        self.profile.is_some()
    }

    /// A tab's staged items, in staging order.
    #[must_use]
    pub fn staged(&self, tab: HousePurchaseTab) -> &[u32] {
        match tab {
            HousePurchaseTab::Buy => &self.buy,
            HousePurchaseTab::Rent => &self.rent,
        }
    }

    /// How much of payment line `line` the tab's staged items pay, counted
    /// from nothing: a stack up to what is owed, a stack of notes the whole
    /// notes that fit.
    #[must_use]
    pub fn staged_amount(&self, tab: HousePurchaseTab, line: usize) -> i32 {
        let amounts = match tab {
            HousePurchaseTab::Buy => &self.buy_amounts,
            HousePurchaseTab::Rent => &self.rent_amounts,
        };
        amounts.get(line).copied().unwrap_or(0)
    }

    /// The tab's button may send: something is staged, the house is for
    /// sale (Buy) or owned and not free of maintenance (Maintenance), and a
    /// purchase has every line covered by the staged items alone (what the
    /// server counts as paid is not counted; maintenance may be paid in
    /// part).
    #[must_use]
    pub fn can_submit(&self, tab: HousePurchaseTab) -> bool {
        let Some(profile) = &self.profile else {
            return false;
        };
        if self.staged(tab).is_empty() || refusal(profile, tab).is_some() {
            return false;
        }
        tab != HousePurchaseTab::Buy
            || profile
                .buy
                .iter()
                .enumerate()
                .all(|(i, line)| self.staged_amount(tab, i) >= line.num)
    }

    /// What is asked before the tab's payment goes, if anything: buying
    /// anything but an apartment, or paying maintenance on a house someone
    /// other than `player` owns.
    #[must_use]
    pub fn confirmation(&self, tab: HousePurchaseTab, player: u32) -> Option<&'static str> {
        let profile = self.profile.as_ref()?;
        match tab {
            HousePurchaseTab::Buy if profile.house_type != APARTMENT_TYPE => Some(BUY_CONFIRMATION),
            HousePurchaseTab::Rent if profile.owner_guid != player => {
                Some(OTHERS_MAINTENANCE_CONFIRMATION)
            }
            _ => None,
        }
    }
}

/// Why the profile refuses anything on `tab`, if it does.
fn refusal(profile: &HouseProfile, tab: HousePurchaseTab) -> Option<HouseStageResult> {
    match tab {
        HousePurchaseTab::Buy if profile.owner_guid != 0 => Some(HouseStageResult::AlreadyOwned),
        HousePurchaseTab::Rent if profile.owner_guid == 0 => Some(HouseStageResult::NotOwned),
        HousePurchaseTab::Rent if profile.maintenance_free => {
            Some(HouseStageResult::MaintenanceFree)
        }
        _ => None,
    }
}

/// A staged item, with the class and stack size it had when staged, for
/// taking its payment off after the item is gone.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
struct StagedItem {
    guid: u32,
    wcid: u32,
    count: i32,
}

/// One tab's strip and the running paid amount of each of its lines.
#[derive(Debug, Clone, Default)]
struct Strip {
    items: Vec<StagedItem>,
    paid: Vec<i32>,
}

/// See the module documentation.
///
/// Each tab keeps a running paid amount per payment line. It starts at zero
/// with every profile (the server's paid amounts are shown, never counted),
/// grows as items are staged and shrinks as they are taken off. Staging a
/// stack counts no more than the line still owes; a stack of trade notes
/// counts only the whole notes that fit. Taking an item off subtracts its
/// whole stack, never going below zero.
#[derive(Debug, Clone, Default)]
pub struct HousePurchaseState {
    profile: Option<HouseProfile>,
    buy: Strip,
    rent: Strip,
    /// Each trade note's value in pyreals, by its weenie class (the dats'
    /// trade-note map): only these classes pay as trade notes.
    notes: Arc<HashMap<u32, i32>>,
    revision: u64,
}

impl HousePurchaseState {
    /// Install the trade-note classes, each with its value in pyreals.
    pub fn install_trade_notes(&mut self, notes: Arc<HashMap<u32, i32>>) {
        self.notes = notes;
    }

    /// Counts changes.
    #[must_use]
    pub fn revision(&self) -> u64 {
        self.revision
    }

    /// The slumlord the window is open on (0: closed).
    #[must_use]
    pub fn slumlord(&self) -> u32 {
        self.profile.as_ref().map_or(0, |p| p.slumlord_guid)
    }

    /// The window is open.
    #[must_use]
    pub fn is_open(&self) -> bool {
        self.profile.is_some()
    }

    /// `item` is on either tab's strip.
    #[must_use]
    pub fn is_staged(&self, item: u32) -> bool {
        [&self.buy, &self.rent]
            .iter()
            .any(|s| s.items.iter().any(|e| e.guid == item))
    }

    /// A slumlord's profile arrived: the window opens on it. Every profile,
    /// the same house's included, empties both strips: staging starts again
    /// from nothing, whatever the server says is already paid.
    pub fn apply_profile(&mut self, profile: HouseProfile) {
        self.buy = Strip {
            items: Vec::new(),
            paid: vec![0; profile.buy.len()],
        };
        self.rent = Strip {
            items: Vec::new(),
            paid: vec![0; profile.rent.len()],
        };
        self.profile = Some(profile);
        self.revision += 1;
    }

    /// Stage one item, or for a pack, every item in it that the tab takes.
    pub fn stage(
        &mut self,
        tab: HousePurchaseTab,
        item: u32,
        objects: &ClientObjectTable,
        player: u32,
    ) -> HouseStageResult {
        let Some(profile) = self.profile.clone() else {
            return HouseStageResult::NotOpen;
        };
        // An item not carried is refused first, whatever the tab.
        let Some(object) = carried(objects, item, player) else {
            return HouseStageResult::NotInInventory;
        };
        if let Some(refused) = refusal(&profile, tab) {
            return refused;
        }
        if object.item_type.intersects(ItemType::CONTAINER) {
            let mut any = false;
            for &content in objects.get_contents(item) {
                any |= self.stage_item(&profile, tab, content, objects, player)
                    == HouseStageResult::Staged;
            }
            return if any {
                HouseStageResult::Staged
            } else {
                HouseStageResult::NotRequired
            };
        }
        self.stage_item(&profile, tab, item, objects, player)
    }

    fn stage_item(
        &mut self,
        profile: &HouseProfile,
        tab: HousePurchaseTab,
        item: u32,
        objects: &ClientObjectTable,
        player: u32,
    ) -> HouseStageResult {
        let Some(object) = carried(objects, item, player) else {
            return HouseStageResult::NotInInventory;
        };
        if self.strip(tab).items.iter().any(|e| e.guid == item) {
            return HouseStageResult::AlreadyStaged;
        }
        let wcid = object.weenie_class_id;
        let Some(line) = self.line_index(lines(profile, tab), wcid) else {
            return HouseStageResult::NotRequired;
        };
        let owed = lines(profile, tab)[line].num - self.strip(tab).paid[line];
        if owed <= 0 {
            return HouseStageResult::AlreadyCovered;
        }
        let count = payment_count(object);
        let pays = if let Some(note) = self.note_value(wcid) {
            // A note cannot be split: as many whole notes as fit, at least
            // one.
            let notes = if note > 0 && owed >= note {
                (owed / note).min(count)
            } else {
                0
            };
            if notes <= 0 {
                return HouseStageResult::NoteTooLarge;
            }
            notes * note
        } else {
            // A stack may run over (the server takes only what it needs),
            // but it counts no more than is owed.
            count.min(owed)
        };
        let strip = self.strip_mut(tab);
        strip.paid[line] += pays;
        strip.items.push(StagedItem {
            guid: item,
            wcid,
            count,
        });
        self.revision += 1;
        HouseStageResult::Staged
    }

    /// Take `item` off a tab's strip (and its payment off its line); false
    /// when it was not on it.
    pub fn unstage(
        &mut self,
        tab: HousePurchaseTab,
        item: u32,
        objects: &ClientObjectTable,
    ) -> bool {
        let Some(index) = self.strip(tab).items.iter().position(|e| e.guid == item) else {
            return false;
        };
        self.remove(tab, index, objects);
        self.revision += 1;
        true
    }

    /// Empty one tab's strip, as sending its payment does.
    pub fn clear_staged(&mut self, tab: HousePurchaseTab) {
        let strip = self.strip_mut(tab);
        if strip.items.is_empty() {
            return;
        }
        strip.items.clear();
        strip.paid.iter_mut().for_each(|p| *p = 0);
        self.revision += 1;
    }

    /// Drop staged items the player no longer carries, taking their
    /// payment off; true when any was dropped.
    pub fn revalidate(&mut self, objects: &ClientObjectTable, player: u32) -> bool {
        let mut removed = false;
        for tab in [HousePurchaseTab::Buy, HousePurchaseTab::Rent] {
            for i in (0..self.strip(tab).items.len()).rev() {
                if carried(objects, self.strip(tab).items[i].guid, player).is_none() {
                    self.remove(tab, i, objects);
                    removed = true;
                }
            }
        }
        if removed {
            self.revision += 1;
        }
        removed
    }

    /// A staged item changed in place (a stack merged or split): the
    /// window reads it again.
    pub fn touch(&mut self) {
        self.revision += 1;
    }

    /// Close the window; false when it was closed with nothing staged.
    pub fn close(&mut self) -> bool {
        if self.profile.is_none() && self.buy.items.is_empty() && self.rent.items.is_empty() {
            return false;
        }
        self.profile = None;
        self.buy = Strip::default();
        self.rent = Strip::default();
        self.revision += 1;
        true
    }

    /// The window as a host shows it: the staged items the player still
    /// carries and what the staging paid of each line.
    #[must_use]
    pub fn view(&self, objects: &ClientObjectTable, player: u32) -> HousePurchaseView {
        let carried_only = |strip: &Strip| {
            strip
                .items
                .iter()
                .map(|e| e.guid)
                .filter(|&id| carried(objects, id, player).is_some())
                .collect::<Vec<_>>()
        };
        HousePurchaseView {
            revision: self.revision,
            profile: self.profile.clone(),
            buy: carried_only(&self.buy),
            rent: carried_only(&self.rent),
            buy_amounts: self.buy.paid.clone(),
            rent_amounts: self.rent.paid.clone(),
        }
    }

    /// Take an item off its strip, and its whole stack off its line's paid
    /// amount, never below zero: the item's stack now, or the one it had
    /// when staged if it is gone.
    fn remove(&mut self, tab: HousePurchaseTab, index: usize, objects: &ClientObjectTable) {
        let entry = self.strip_mut(tab).items.remove(index);
        let Some(profile) = &self.profile else {
            return;
        };
        let Some(line) = self.line_index(lines(profile, tab), entry.wcid) else {
            return;
        };
        let count = objects.get(entry.guid).map_or(entry.count, payment_count);
        let amount = self
            .note_value(entry.wcid)
            .map_or(count, |note| count.saturating_mul(note));
        if amount <= 0 {
            return;
        }
        let paid = &mut self.strip_mut(tab).paid[line];
        *paid = if amount > *paid { 0 } else { *paid - amount };
    }

    /// A trade note's value in pyreals, or `None` for any other class.
    fn note_value(&self, wcid: u32) -> Option<i32> {
        self.notes.get(&wcid).copied()
    }

    /// The first line the class pays: a trade note pays the pyreal line,
    /// anything else the line asking for its own class.
    fn line_index(&self, lines: &[HousePayment], wcid: u32) -> Option<usize> {
        let note = self.note_value(wcid).is_some();
        lines
            .iter()
            .position(|l| (note && l.weenie_id == PYREAL_WCID) || l.weenie_id == wcid)
    }

    fn strip(&self, tab: HousePurchaseTab) -> &Strip {
        match tab {
            HousePurchaseTab::Buy => &self.buy,
            HousePurchaseTab::Rent => &self.rent,
        }
    }

    fn strip_mut(&mut self, tab: HousePurchaseTab) -> &mut Strip {
        match tab {
            HousePurchaseTab::Buy => &mut self.buy,
            HousePurchaseTab::Rent => &mut self.rent,
        }
    }
}

/// `item` as the player carries it: in its packs (not the player itself,
/// not wielded).
fn carried(objects: &ClientObjectTable, item: u32, player: u32) -> Option<&ClientObject> {
    if item == player || !objects.is_owned_by_object(item, player) {
        return None;
    }
    objects.get(item).filter(|o| o.wielder_id == 0)
}

fn lines(profile: &HouseProfile, tab: HousePurchaseTab) -> &[HousePayment] {
    match tab {
        HousePurchaseTab::Buy => &profile.buy,
        HousePurchaseTab::Rent => &profile.rent,
    }
}

/// How many of an item one staging offers: its stack size, at least one.
fn payment_count(item: &ClientObject) -> i32 {
    if item.stack_size != 0 {
        item.stack_size
    } else {
        1
    }
}

#[cfg(test)]
#[path = "house_purchase_tests.rs"]
mod tests;
