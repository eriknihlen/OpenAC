//! Filling a vendor's buy list from the component book's desired counts:
//! for each component of the requested category the player is short of,
//! stage what the shop stocks, until the spending ceiling is reached.

use std::cmp::Ordering;
use std::collections::BTreeMap;

use super::ClientObjectTable;
use super::vendor::{
    UNLIMITED_STOCK, VendorShopItem, VendorShopProfile, per_unit_value, sell_price,
};
use super::vendor_staging::VendorStagingEntry;

/// The seven component categories, numbered as the game data's component
/// table numbers them.
pub const SCARAB_CATEGORY: u32 = 0;
pub const HERB_CATEGORY: u32 = 1;
pub const POWDERED_GEM_CATEGORY: u32 = 2;
pub const ALCHEMICAL_SUBSTANCE_CATEGORY: u32 = 3;
pub const TALISMAN_CATEGORY: u32 = 4;
pub const TAPER_CATEGORY: u32 = 5;
pub const PEA_CATEGORY: u32 = 6;
/// How many real categories there are.
pub const CATEGORY_COUNT: u32 = 7;
/// "No category in particular": a fill matches every row.
pub const ANY_CATEGORY: u32 = 8;

pub const SHORT_COMPONENTS_PREFIX: &str = "There was not enough: ";
pub const SHORT_COMPONENTS_SEPARATOR: &str = ", ";
pub const ABORTED_ON_PRICE_MESSAGE: &str = "Buying aborted; max price reached.";

/// One component-book row: the component, its category and name, how
/// many the player wants and how many are carried.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ComponentFillDesire {
    pub weenie_class_id: u32,
    pub category: u32,
    pub name: String,
    pub desired: i32,
    pub owned: i32,
}

/// What the component catalog knows about one component.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ComponentDescription {
    pub category: u32,
    pub name: String,
}

/// One purchase to stage, in order.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ComponentFillAdd {
    pub item_guid: u32,
    pub quantity: i32,
}

/// What a fill decided: the buys, whether the price ceiling stopped it
/// (the newest staged row is then dropped), and the components the shop
/// could not fully supply.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct ComponentFillPlan {
    pub adds: Vec<ComponentFillAdd>,
    pub aborted_on_price: bool,
    pub short_components: Vec<String>,
}

/// The rows a fill walks: each desired component with its category and
/// name from `describe` (falling back to the shop listing's name), its
/// carried count from everything `player_guid` owns, ordered by category
/// then name as the component book lists them. Rows asking for none are
/// dropped.
pub fn build_component_desires(
    desired_components: &BTreeMap<u32, u32>,
    objects: &ClientObjectTable,
    player_guid: u32,
    describe: impl Fn(u32) -> Option<ComponentDescription>,
    shop_items: &[VendorShopItem],
) -> Vec<ComponentFillDesire> {
    let mut owned: BTreeMap<u32, i32> = BTreeMap::new();
    for item in objects.objects() {
        if !objects.is_owned_by_object(item.object_id(), player_guid) {
            continue;
        }
        let count = owned.entry(item.weenie_class_id).or_default();
        *count = count.wrapping_add(item.stack_size.max(1));
    }

    let mut desires: Vec<ComponentFillDesire> = desired_components
        .iter()
        .filter(|&(_, &desired)| desired != 0)
        .map(|(&weenie_class_id, &desired)| {
            let described = describe(weenie_class_id);
            let mut name = described
                .as_ref()
                .map(|d| d.name.clone())
                .unwrap_or_default();
            if name.trim().is_empty() {
                name = find_stock_name(shop_items, weenie_class_id);
            }
            ComponentFillDesire {
                weenie_class_id,
                category: described.map_or(ANY_CATEGORY, |d| d.category),
                name,
                desired: desired as i32,
                owned: owned.get(&weenie_class_id).copied().unwrap_or(0),
            }
        })
        .collect();

    // The order the component book shows, so a ceiling stops at the row
    // the player can see.
    desires.sort_by(|left, right| {
        left.category
            .cmp(&right.category)
            .then_with(|| compare_ignore_case(&left.name, &right.name))
    });
    desires
}

fn find_stock_name(shop_items: &[VendorShopItem], weenie_class_id: u32) -> String {
    shop_items
        .iter()
        .filter(|item| item.weenie_class_id == weenie_class_id)
        .filter_map(|item| item.name.as_deref())
        .find(|name| !name.trim().is_empty())
        .unwrap_or_default()
        .to_owned()
}

/// Compares two names character by character after upper-casing each
/// character on its own.
fn compare_ignore_case(left: &str, right: &str) -> Ordering {
    let upper = |c: char| {
        let mut upper = c.to_uppercase();
        match (upper.next(), upper.next()) {
            (Some(single), None) => single,
            _ => c,
        }
    };
    left.chars().map(upper).cmp(right.chars().map(upper))
}

/// The buys a fill stages. `category` is one category or
/// [`ANY_CATEGORY`]; `maximum_price` is the spending ceiling (0 for none),
/// tested before each row so the buy that crossed it is the one dropped;
/// what `staged` already holds counts against each shortfall. A component
/// the shop lacks, or stocks too few of, is reported (when it has a name).
#[must_use]
pub fn plan_component_fill(
    desires: &[ComponentFillDesire],
    category: u32,
    maximum_price: i32,
    shop_items: &[VendorShopItem],
    profile: &VendorShopProfile,
    staged: &[VendorStagingEntry],
) -> ComponentFillPlan {
    let mut plan = ComponentFillPlan::default();
    let mut staged_quantities: BTreeMap<u32, i32> = staged
        .iter()
        .map(|entry| (entry.item_guid, entry.quantity))
        .collect();
    let mut spent = 0i32;

    for desire in desires {
        if maximum_price != 0 && spent >= maximum_price {
            plan.aborted_on_price = true;
            break;
        }
        if category != ANY_CATEGORY && desire.category != category {
            continue;
        }
        if desire.owned >= desire.desired {
            continue;
        }

        let shortfall = desire.desired.wrapping_sub(desire.owned);
        let Some((stock, available)) = find_stock(shop_items, desire.weenie_class_id, shortfall)
        else {
            report(&mut plan.short_components, &desire.name);
            continue;
        };

        let already_staged = staged_quantities
            .get(&stock.item_guid)
            .copied()
            .unwrap_or(0);
        let mut wanted = shortfall.wrapping_sub(already_staged);
        if available < wanted {
            report(&mut plan.short_components, &desire.name);
            wanted = available;
        }
        if wanted <= 0 {
            continue;
        }

        plan.adds.push(ComponentFillAdd {
            item_guid: stock.item_guid,
            quantity: wanted,
        });
        staged_quantities.insert(stock.item_guid, already_staged.wrapping_add(wanted));
        spent = spent.wrapping_add(sell_price(
            per_unit_value(stock.value.unwrap_or(0), stock.desc_stack_size),
            stock.item_type.unwrap_or(0),
            profile.sell_price,
            wanted,
        ));
    }
    plan
}

/// Names a short component; a nameless one is left out rather than
/// reported as a blank.
fn report(short_components: &mut Vec<String>, name: &str) {
    if !name.trim().is_empty() {
        short_components.push(name.to_owned());
    }
}

/// The shop's listing for a component, with `wanted` narrowed to what it
/// stocks.
fn find_stock(
    shop_items: &[VendorShopItem],
    weenie_class_id: u32,
    wanted: i32,
) -> Option<(&VendorShopItem, i32)> {
    let item = shop_items
        .iter()
        .find(|item| item.weenie_class_id == weenie_class_id)?;
    let available = if item.stack_size != UNLIMITED_STOCK && item.stack_size < wanted {
        item.stack_size
    } else {
        wanted
    };
    Some((item, available))
}

/// The one line naming every component the shop could not fully supply.
#[must_use]
pub fn format_short_components(short_components: &[String]) -> String {
    format!(
        "{SHORT_COMPONENTS_PREFIX}{}",
        short_components.join(SHORT_COMPONENTS_SEPARATOR)
    )
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.Core.Tests/Items/VendorComponentFillTests.cs`.

    use super::*;
    use crate::items::{ClientObject, TableEvent};

    const TAPER_WCID: u32 = 0x0000_055C;
    const SCARAB_WCID: u32 = 0x0000_029A;
    const PEA_WCID: u32 = 0x0000_04DF;
    const TAPER_GUID: u32 = 0x6000_0101;
    const SCARAB_GUID: u32 = 0x6000_0102;
    const PEA_GUID: u32 = 0x6000_0103;

    fn shop() -> VendorShopProfile {
        VendorShopProfile {
            merchandise_item_types: 0x1000,
            buy_price: 0.75,
            sell_price: 1.0,
            ..VendorShopProfile::default()
        }
    }

    fn stock(guid: u32, wcid: u32, listed_stack: i32, unit_value: i32) -> VendorShopItem {
        VendorShopItem {
            item_guid: guid,
            stack_size: listed_stack,
            weenie_class_id: wcid,
            name: Some("component".into()),
            item_type: Some(0x1000),
            value: Some(unit_value),
            desc_stack_size: Some(1),
            max_stack_size: Some(100),
            ..VendorShopItem::default()
        }
    }

    fn desire(
        wcid: u32,
        category: u32,
        name: &str,
        desired: i32,
        owned: i32,
    ) -> ComponentFillDesire {
        ComponentFillDesire {
            weenie_class_id: wcid,
            category,
            name: name.into(),
            desired,
            owned,
        }
    }

    fn add(item_guid: u32, quantity: i32) -> ComponentFillAdd {
        ComponentFillAdd {
            item_guid,
            quantity,
        }
    }

    fn staged(item_guid: u32, quantity: i32) -> VendorStagingEntry {
        VendorStagingEntry {
            item_guid,
            quantity,
        }
    }

    #[test]
    fn stages_the_shortfall_of_every_stocked_component() {
        let plan = plan_component_fill(
            &[
                desire(TAPER_WCID, TAPER_CATEGORY, "Taper", 20, 12),
                desire(SCARAB_WCID, SCARAB_CATEGORY, "Lead Scarab", 5, 0),
            ],
            ANY_CATEGORY,
            0,
            &[
                stock(TAPER_GUID, TAPER_WCID, UNLIMITED_STOCK, 5),
                stock(SCARAB_GUID, SCARAB_WCID, UNLIMITED_STOCK, 10),
            ],
            &shop(),
            &[],
        );
        assert_eq!(plan.adds, vec![add(TAPER_GUID, 8), add(SCARAB_GUID, 5)]);
        assert!(plan.short_components.is_empty());
        assert!(!plan.aborted_on_price);
    }

    #[test]
    fn components_already_covered_by_the_pack_are_skipped() {
        let plan = plan_component_fill(
            &[desire(TAPER_WCID, TAPER_CATEGORY, "Taper", 20, 20)],
            ANY_CATEGORY,
            0,
            &[stock(TAPER_GUID, TAPER_WCID, UNLIMITED_STOCK, 5)],
            &shop(),
            &[],
        );
        assert!(plan.adds.is_empty());
        assert!(plan.short_components.is_empty());
    }

    #[test]
    fn a_component_the_shop_does_not_carry_is_skipped_and_reported() {
        let plan = plan_component_fill(
            &[
                desire(TAPER_WCID, TAPER_CATEGORY, "Taper", 20, 12),
                desire(PEA_WCID, PEA_CATEGORY, "Pea", 4, 0),
            ],
            ANY_CATEGORY,
            0,
            &[stock(TAPER_GUID, TAPER_WCID, UNLIMITED_STOCK, 5)],
            &shop(),
            &[],
        );
        assert_eq!(plan.adds, vec![add(TAPER_GUID, 8)]);
        assert_eq!(plan.short_components, vec!["Pea".to_owned()]);
    }

    #[test]
    fn short_stock_is_staged_down_to_what_the_shop_has_and_reported() {
        let plan = plan_component_fill(
            &[desire(TAPER_WCID, TAPER_CATEGORY, "Taper", 20, 12)],
            ANY_CATEGORY,
            0,
            &[stock(TAPER_GUID, TAPER_WCID, 3, 5)],
            &shop(),
            &[],
        );
        assert_eq!(plan.adds, vec![add(TAPER_GUID, 3)]);
        assert_eq!(plan.short_components, vec!["Taper".to_owned()]);
    }

    #[test]
    fn only_the_requested_category_is_filled() {
        let plan = plan_component_fill(
            &[
                desire(TAPER_WCID, TAPER_CATEGORY, "Taper", 20, 12),
                desire(SCARAB_WCID, SCARAB_CATEGORY, "Lead Scarab", 5, 0),
            ],
            SCARAB_CATEGORY,
            0,
            &[
                stock(TAPER_GUID, TAPER_WCID, UNLIMITED_STOCK, 5),
                stock(SCARAB_GUID, SCARAB_WCID, UNLIMITED_STOCK, 10),
            ],
            &shop(),
            &[],
        );
        assert_eq!(plan.adds, vec![add(SCARAB_GUID, 5)]);
    }

    #[test]
    fn already_staged_quantity_counts_against_the_shortfall() {
        let plan = plan_component_fill(
            &[desire(TAPER_WCID, TAPER_CATEGORY, "Taper", 20, 12)],
            ANY_CATEGORY,
            0,
            &[stock(TAPER_GUID, TAPER_WCID, UNLIMITED_STOCK, 5)],
            &shop(),
            &[staged(TAPER_GUID, 6)],
        );
        assert_eq!(plan.adds, vec![add(TAPER_GUID, 2)]);
    }

    #[test]
    fn a_shortfall_already_fully_staged_adds_nothing() {
        let plan = plan_component_fill(
            &[desire(TAPER_WCID, TAPER_CATEGORY, "Taper", 20, 12)],
            ANY_CATEGORY,
            0,
            &[stock(TAPER_GUID, TAPER_WCID, UNLIMITED_STOCK, 5)],
            &shop(),
            &[staged(TAPER_GUID, 8)],
        );
        assert!(plan.adds.is_empty());
        assert!(plan.short_components.is_empty());
    }

    #[test]
    fn a_shortfall_already_over_staged_adds_nothing_and_reports_nothing() {
        let plan = plan_component_fill(
            &[desire(TAPER_WCID, TAPER_CATEGORY, "Taper", 20, 12)],
            ANY_CATEGORY,
            0,
            &[stock(TAPER_GUID, TAPER_WCID, UNLIMITED_STOCK, 5)],
            &shop(),
            &[staged(TAPER_GUID, 15)],
        );
        assert!(plan.adds.is_empty());
        assert!(plan.short_components.is_empty());
        assert!(!plan.aborted_on_price);
    }

    #[test]
    fn the_seven_categories_are_numbered_in_table_order() {
        assert_eq!(
            [
                SCARAB_CATEGORY,
                HERB_CATEGORY,
                POWDERED_GEM_CATEGORY,
                ALCHEMICAL_SUBSTANCE_CATEGORY,
                TALISMAN_CATEGORY,
                TAPER_CATEGORY,
                PEA_CATEGORY,
                CATEGORY_COUNT,
                ANY_CATEGORY
            ],
            [0, 1, 2, 3, 4, 5, 6, 7, 8]
        );
    }

    #[test]
    fn a_nameless_component_is_skipped_without_naming_a_blank() {
        let plan = plan_component_fill(
            &[
                desire(PEA_WCID, PEA_CATEGORY, "", 4, 0),
                desire(SCARAB_WCID, SCARAB_CATEGORY, "Lead Scarab", 4, 0),
            ],
            ANY_CATEGORY,
            0,
            &[stock(TAPER_GUID, TAPER_WCID, UNLIMITED_STOCK, 5)],
            &shop(),
            &[],
        );
        assert!(plan.adds.is_empty());
        assert_eq!(plan.short_components, vec!["Lead Scarab".to_owned()]);
    }

    #[test]
    fn the_price_ceiling_rolls_back_the_buy_that_crossed_it_and_stops() {
        let plan = plan_component_fill(
            &[
                desire(TAPER_WCID, TAPER_CATEGORY, "Taper", 10, 0),
                desire(SCARAB_WCID, SCARAB_CATEGORY, "Lead Scarab", 10, 0),
                desire(PEA_WCID, PEA_CATEGORY, "Pea", 10, 0),
            ],
            ANY_CATEGORY,
            60,
            &[
                stock(TAPER_GUID, TAPER_WCID, UNLIMITED_STOCK, 5),
                stock(SCARAB_GUID, SCARAB_WCID, UNLIMITED_STOCK, 5),
                stock(PEA_GUID, PEA_WCID, UNLIMITED_STOCK, 5),
            ],
            &shop(),
            &[],
        );
        // 50 after the tapers is under 60, so the scarabs go on too (100);
        // the peas row then finds the ceiling met.
        assert_eq!(plan.adds, vec![add(TAPER_GUID, 10), add(SCARAB_GUID, 10)]);
        assert!(plan.aborted_on_price);
    }

    #[test]
    fn no_ceiling_means_no_abort() {
        let plan = plan_component_fill(
            &[
                desire(TAPER_WCID, TAPER_CATEGORY, "Taper", 10, 0),
                desire(SCARAB_WCID, SCARAB_CATEGORY, "Lead Scarab", 10, 0),
            ],
            ANY_CATEGORY,
            0,
            &[
                stock(TAPER_GUID, TAPER_WCID, UNLIMITED_STOCK, 5000),
                stock(SCARAB_GUID, SCARAB_WCID, UNLIMITED_STOCK, 5000),
            ],
            &shop(),
            &[],
        );
        assert_eq!(plan.adds.len(), 2);
        assert!(!plan.aborted_on_price);
    }

    #[test]
    fn short_components_read_as_one_sentence() {
        assert_eq!(
            format_short_components(&["Taper".into(), "Pea".into()]),
            "There was not enough: Taper, Pea"
        );
        assert_eq!(
            format_short_components(&["Taper".into()]),
            "There was not enough: Taper"
        );
    }

    #[test]
    fn an_empty_component_book_stages_nothing() {
        let plan = plan_component_fill(
            &[],
            ANY_CATEGORY,
            0,
            &[stock(TAPER_GUID, TAPER_WCID, UNLIMITED_STOCK, 5)],
            &shop(),
            &[],
        );
        assert_eq!(plan, ComponentFillPlan::default());
    }

    #[test]
    fn the_plan_prices_each_buy_with_the_shop_sell_rate() {
        let dear = VendorShopProfile {
            sell_price: 2.0,
            ..shop()
        };
        let plan = plan_component_fill(
            &[
                desire(TAPER_WCID, TAPER_CATEGORY, "Taper", 10, 0),
                desire(SCARAB_WCID, SCARAB_CATEGORY, "Lead Scarab", 10, 0),
            ],
            ANY_CATEGORY,
            60,
            &[
                stock(TAPER_GUID, TAPER_WCID, UNLIMITED_STOCK, 5),
                stock(SCARAB_GUID, SCARAB_WCID, UNLIMITED_STOCK, 5),
            ],
            &dear,
            &[],
        );
        assert_eq!(plan.adds, vec![add(TAPER_GUID, 10)]);
        assert!(plan.aborted_on_price);
    }

    const PLAYER_GUID: u32 = 0x5000_0001;

    fn pack_with(items: &[(u32, u32, i32)]) -> ClientObjectTable {
        let mut ev: Vec<TableEvent> = Vec::new();
        let mut objects = ClientObjectTable::new();
        objects.add_or_update(ClientObject::new(PLAYER_GUID), &mut ev);
        for &(guid, wcid, stack) in items {
            let mut item = ClientObject::new(guid);
            item.weenie_class_id = wcid;
            item.container_id = PLAYER_GUID;
            item.stack_size = stack;
            objects.add_or_update(item, &mut ev);
        }
        objects
    }

    fn catalog(entries: &[(u32, u32, &str)]) -> impl Fn(u32) -> Option<ComponentDescription> {
        let entries: Vec<(u32, u32, String)> = entries
            .iter()
            .map(|&(wcid, category, name)| (wcid, category, name.to_owned()))
            .collect();
        move |wcid| {
            entries
                .iter()
                .find(|(entry, _, _)| *entry == wcid)
                .map(|(_, category, name)| ComponentDescription {
                    category: *category,
                    name: name.clone(),
                })
        }
    }

    #[test]
    fn build_desires_sums_every_carried_stack_of_each_component() {
        let desires = build_component_desires(
            &BTreeMap::from([(TAPER_WCID, 20)]),
            &pack_with(&[
                (0x6000_0201, TAPER_WCID, 7),
                (0x6000_0202, TAPER_WCID, 5),
                (0x6000_0203, SCARAB_WCID, 99),
            ]),
            PLAYER_GUID,
            catalog(&[(TAPER_WCID, TAPER_CATEGORY, "Prismatic Taper")]),
            &[],
        );
        assert_eq!(
            desires,
            vec![desire(
                TAPER_WCID,
                TAPER_CATEGORY,
                "Prismatic Taper",
                20,
                12
            )]
        );
    }

    #[test]
    fn build_desires_ignores_items_the_player_is_not_carrying() {
        let mut ev: Vec<TableEvent> = Vec::new();
        let mut objects = ClientObjectTable::new();
        objects.add_or_update(ClientObject::new(PLAYER_GUID), &mut ev);
        let mut loose = ClientObject::new(0x6000_0301);
        loose.weenie_class_id = TAPER_WCID;
        loose.stack_size = 40;
        objects.add_or_update(loose, &mut ev);
        let desires = build_component_desires(
            &BTreeMap::from([(TAPER_WCID, 20)]),
            &objects,
            PLAYER_GUID,
            catalog(&[(TAPER_WCID, TAPER_CATEGORY, "Prismatic Taper")]),
            &[],
        );
        assert_eq!(desires.len(), 1);
        assert_eq!(desires[0].owned, 0);
    }

    #[test]
    fn build_desires_drops_rows_asking_for_none() {
        let desires = build_component_desires(
            &BTreeMap::from([(TAPER_WCID, 0), (SCARAB_WCID, 3)]),
            &pack_with(&[]),
            PLAYER_GUID,
            catalog(&[
                (TAPER_WCID, TAPER_CATEGORY, "Prismatic Taper"),
                (SCARAB_WCID, SCARAB_CATEGORY, "Lead Scarab"),
            ]),
            &[],
        );
        assert_eq!(desires.len(), 1);
        assert_eq!(desires[0].weenie_class_id, SCARAB_WCID);
    }

    #[test]
    fn build_desires_orders_by_category_then_name() {
        let desires = build_component_desires(
            &BTreeMap::from([(TAPER_WCID, 1), (PEA_WCID, 1), (SCARAB_WCID, 1), (0x999, 1)]),
            &pack_with(&[]),
            PLAYER_GUID,
            catalog(&[
                (TAPER_WCID, TAPER_CATEGORY, "Prismatic Taper"),
                (PEA_WCID, PEA_CATEGORY, "Pea"),
                (SCARAB_WCID, SCARAB_CATEGORY, "Lead Scarab"),
                (0x999, SCARAB_CATEGORY, "Amber Scarab"),
            ]),
            &[],
        );
        let names: Vec<&str> = desires.iter().map(|d| d.name.as_str()).collect();
        assert_eq!(
            names,
            vec!["Amber Scarab", "Lead Scarab", "Prismatic Taper", "Pea"]
        );
    }

    #[test]
    fn build_desires_names_an_uncataloged_component_from_the_shop_listing() {
        let desires = build_component_desires(
            &BTreeMap::from([(PEA_WCID, 4)]),
            &pack_with(&[]),
            PLAYER_GUID,
            catalog(&[]),
            &[VendorShopItem {
                name: Some("Pea".into()),
                ..stock(PEA_GUID, PEA_WCID, UNLIMITED_STOCK, 5)
            }],
        );
        assert_eq!(desires.len(), 1);
        assert_eq!(desires[0].name, "Pea");
        assert_eq!(desires[0].category, ANY_CATEGORY);
    }

    #[test]
    fn build_desires_leaves_a_component_nothing_can_name_nameless() {
        let desires = build_component_desires(
            &BTreeMap::from([(PEA_WCID, 4)]),
            &pack_with(&[]),
            PLAYER_GUID,
            catalog(&[]),
            &[],
        );
        assert_eq!(desires.len(), 1);
        assert_eq!(desires[0].name, "");
    }
}
