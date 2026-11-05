//! The slumlord window (layout 0x2100000E): a Buy page and a Maintenance
//! page, each with the requirements, the owner, a row the player drags
//! payment onto, and a button that offers the staged items; and a close
//! button.
//!
//! The staging is the session's ([`HousePurchaseView`], rebuilt here when
//! its revision moves); the window shows and hides itself as the view opens
//! and closes. Items dropped on a row are offered to the session, dragging
//! one off or right-clicking it takes it off. The button asks first when
//! the payment calls for it (buying a house other than an apartment,
//! paying maintenance on someone else's house); a no closes the window.
//! The tab stays as the player chose it when a profile arrives.

use std::sync::Arc;

use ac_proto::house::HouseProfile;
use ac_runtime::gameplay::house::payment_name;
use ac_runtime::inventory::{
    HousePurchaseCommand, HousePurchaseTab, HousePurchaseView, ItemCommand,
};
use ac_runtime::session::SessionCommand;

use crate::event::{DragAcceptance, ItemDragSource};
use crate::host::PanelController;
use crate::icons::IconSource;
use crate::import::ImportedLayout;
use crate::panels::PanelOutput;
use crate::panels::external_container::apply_horizontal_bar_art;
use crate::panels::items::{ItemsView, bind_click, emit, fill_slot};
use crate::root::{ElementMessage, UiRoot};
use crate::text::Line;
use crate::tree::ElementId;
use crate::widgets::item_slot::slot_element;
use crate::widgets::{Button, ItemList, ItemSlot, ScrollSource, Scrollbar, Text};

/// The window's layout.
pub const LAYOUT_ID: u32 = 0x2100_000E;
/// Its root.
pub const ROOT_ID: u32 = 0x1000_008C;
/// The Buy tab.
pub const BUY_TAB_ID: u32 = 0x1000_008E;
/// The Maintenance tab.
pub const MAINTENANCE_TAB_ID: u32 = 0x1000_008F;
/// The Buy page.
pub const BUY_PAGE_ID: u32 = 0x1000_0090;
/// The Buy page's requirements.
pub const BUY_REQUIREMENTS_TEXT_ID: u32 = 0x1000_0091;
/// The Buy page's owner line.
pub const BUY_OWNER_ID: u32 = 0x1000_0093;
/// The Buy button.
pub const BUY_BUTTON_ID: u32 = 0x1000_0094;
/// The Buy page's row of staged items.
pub const BUY_ITEMS_LIST_ID: u32 = 0x1000_0095;
/// The Buy row's scrollbar.
pub const BUY_ITEM_LIST_SCROLL_ID: u32 = 0x1000_0096;
/// The Maintenance page.
pub const MAINTENANCE_PAGE_ID: u32 = 0x1000_0097;
/// The Maintenance page's requirements.
pub const MAINTENANCE_REQUIREMENTS_TEXT_ID: u32 = 0x1000_0098;
/// The Maintenance page's owner line.
pub const MAINTENANCE_OWNER_ID: u32 = 0x1000_009A;
/// The Maintenance button.
pub const MAINTENANCE_BUTTON_ID: u32 = 0x1000_009B;
/// The Maintenance page's row of staged items.
pub const MAINTENANCE_ITEMS_LIST_ID: u32 = 0x1000_009C;
/// The Maintenance row's scrollbar.
pub const MAINTENANCE_ITEM_LIST_SCROLL_ID: u32 = 0x1000_009D;
/// The close button.
pub const CLOSE_BUTTON_ID: u32 = 0x1000_009E;
/// The marker drawn over staged items.
pub const TRADE_OVERLAY_SPRITE: u32 = 0x0600_1DAE;
/// Cell size of both rows, pixels.
pub const CELL_PX: f32 = 32.0;

/// The owner line: the owner's name, or "None" when the profile names
/// nobody.
#[must_use]
pub fn owner_line(profile: &HouseProfile) -> String {
    let name = if profile.owner_name.is_empty() {
        "None"
    } else {
        profile.owner_name.as_str()
    };
    format!("Owner: {name}")
}

/// A tab's requirements on one line: "N Name, N Name" to buy,
/// "paid/num Name, ..." for maintenance, with the server's paid amounts.
#[must_use]
pub fn requirement_text(profile: &HouseProfile, tab: HousePurchaseTab) -> String {
    let lines = match tab {
        HousePurchaseTab::Buy => &profile.buy,
        HousePurchaseTab::Rent => &profile.rent,
    };
    lines
        .iter()
        .map(|p| match tab {
            HousePurchaseTab::Buy => format!("{} {}", p.num, payment_name(p)),
            HousePurchaseTab::Rent => format!("{}/{} {}", p.paid, p.num, payment_name(p)),
        })
        .collect::<Vec<_>>()
        .join(", ")
}

/// The empty-cell images of the two rows (from the cell prototypes; see
/// [`crate::panels::cell_template::empty_sprite`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct HousePurchaseSprites {
    /// The Buy row.
    pub buy_empty: u32,
    /// The Maintenance row.
    pub maintenance_empty: u32,
}

#[derive(Debug, Clone, Copy)]
struct Page {
    tab: HousePurchaseTab,
    tab_element: Option<ElementId>,
    page: Option<ElementId>,
    requirements: Option<ElementId>,
    owner: Option<ElementId>,
    button: Option<ElementId>,
    list: ElementId,
}

/// The slumlord window's controller.
#[derive(Debug)]
pub struct HousePurchaseController {
    window: String,
    pages: [Page; 2],
    close: Option<ElementId>,
    selected: HousePurchaseTab,
    view: HousePurchaseView,
    last_revision: Option<u64>,
    was_open: bool,
    text_profile: Option<HouseProfile>,
    player: u32,
    font: Option<Arc<crate::UiFont>>,
}

fn configure_row(ui: &mut UiRoot, list: ElementId, empty_sprite: u32) {
    ui.with::<ItemList, _>(list, |l, ui| {
        l.flush(ui);
        l.columns = 1;
        l.single_row = true;
        l.horizontal_scroll = true;
        l.cell_width = CELL_PX;
        l.cell_height = CELL_PX;
        l.fill_visible_empty_slots = true;
        if empty_sprite != 0 {
            l.set_cell_empty_sprite(ui, empty_sprite);
        }
        l.empty_slot_factory = Some(Box::new(|ui: &mut UiRoot| {
            ui.insert(
                slot_element(),
                ItemSlot {
                    allow_drag_source: false,
                    ..ItemSlot::default()
                },
            )
        }));
    });
}

fn command(c: HousePurchaseCommand) -> PanelOutput {
    PanelOutput::Session(SessionCommand::Item(ItemCommand::HousePurchase(c)))
}

impl HousePurchaseController {
    /// Bind to the imported window registered as `window` (`None` without
    /// both rows). The window starts hidden, on the Buy page.
    pub fn bind(
        ui: &mut UiRoot,
        layout: &ImportedLayout,
        window: &str,
        sprites: HousePurchaseSprites,
        font: Option<Arc<crate::UiFont>>,
    ) -> Option<Self> {
        let list = |ui: &UiRoot, id| {
            layout
                .find(id)
                .filter(|l| ui.widget::<ItemList>(*l).is_some())
        };
        let buy_list = list(ui, BUY_ITEMS_LIST_ID)?;
        let rent_list = list(ui, MAINTENANCE_ITEMS_LIST_ID)?;
        let page = |ui: &mut UiRoot, tab, ids: [u32; 6], list: ElementId, empty: u32| -> Page {
            let [
                tab_id,
                page_id,
                requirements_id,
                owner_id,
                button_id,
                scroll_id,
            ] = ids;
            configure_row(ui, list, empty);
            if let Some(bar) = layout.find(scroll_id)
                && let Some(s) = ui.widget_mut::<Scrollbar>(bar)
            {
                s.model = ScrollSource::Element(list);
                apply_horizontal_bar_art(s);
            }
            let button = layout.find(button_id).filter(|b| bind_click(ui, *b));
            if let Some(b) = button
                && let Some(w) = ui.widget_mut::<Button>(b)
            {
                // The button shows whether it may send, not its own toggling.
                w.suppress_self_toggle = true;
            }
            let text =
                |ui: &UiRoot, id| layout.find(id).filter(|t| ui.widget::<Text>(*t).is_some());
            Page {
                tab,
                tab_element: layout.find(tab_id).filter(|t| bind_click(ui, *t)),
                page: layout.find(page_id),
                requirements: text(ui, requirements_id),
                owner: text(ui, owner_id),
                button,
                list,
            }
        };
        let buy = page(
            ui,
            HousePurchaseTab::Buy,
            [
                BUY_TAB_ID,
                BUY_PAGE_ID,
                BUY_REQUIREMENTS_TEXT_ID,
                BUY_OWNER_ID,
                BUY_BUTTON_ID,
                BUY_ITEM_LIST_SCROLL_ID,
            ],
            buy_list,
            sprites.buy_empty,
        );
        let rent = page(
            ui,
            HousePurchaseTab::Rent,
            [
                MAINTENANCE_TAB_ID,
                MAINTENANCE_PAGE_ID,
                MAINTENANCE_REQUIREMENTS_TEXT_ID,
                MAINTENANCE_OWNER_ID,
                MAINTENANCE_BUTTON_ID,
                MAINTENANCE_ITEM_LIST_SCROLL_ID,
            ],
            rent_list,
            sprites.maintenance_empty,
        );
        let close = layout.find(CLOSE_BUTTON_ID).filter(|b| bind_click(ui, *b));
        let mut c = Self {
            window: window.to_owned(),
            pages: [buy, rent],
            close,
            selected: HousePurchaseTab::Buy,
            view: HousePurchaseView::default(),
            last_revision: None,
            was_open: false,
            text_profile: None,
            player: 0,
            font,
        };
        for p in c.pages {
            if let Some(b) = p.button {
                ui.set_enabled(b, false);
            }
        }
        c.select(ui, HousePurchaseTab::Buy);
        ui.hide_window(window);
        Some(c)
    }

    /// The page shown.
    #[must_use]
    pub fn selected(&self) -> HousePurchaseTab {
        self.selected
    }

    /// A tab's row of staged items.
    #[must_use]
    pub fn list(&self, tab: HousePurchaseTab) -> ElementId {
        self.page(tab).list
    }

    fn page(&self, tab: HousePurchaseTab) -> &Page {
        match tab {
            HousePurchaseTab::Buy => &self.pages[0],
            HousePurchaseTab::Rent => &self.pages[1],
        }
    }

    /// Show page `tab`.
    pub fn select(&mut self, ui: &mut UiRoot, tab: HousePurchaseTab) {
        self.selected = tab;
        for p in self.pages {
            let open = p.tab == tab;
            if let Some(page) = p.page {
                ui.set_visible(page, open);
            }
            if let Some(t) = p.tab_element {
                crate::widgets::tab_panel::set_open(ui, t, open);
            }
        }
    }

    /// Show `view`: open or close the window as it does, and when its
    /// revision moved, write the text (only when a new profile arrived) and
    /// show both rows and buttons.
    pub fn update(
        &mut self,
        ui: &mut UiRoot,
        view: &HousePurchaseView,
        items: &ItemsView<'_>,
        icons: &mut dyn IconSource,
    ) {
        self.player = items.player;
        if view.is_open() != self.was_open {
            self.was_open = view.is_open();
            if self.was_open {
                ui.show_window(&self.window);
            } else {
                ui.hide_window(&self.window);
            }
        }
        if self.last_revision == Some(view.revision) {
            return;
        }
        self.last_revision = Some(view.revision);
        self.view.clone_from(view);
        let Some(profile) = &view.profile else {
            self.text_profile = None;
            return;
        };
        if self.text_profile.as_ref() != Some(profile) {
            self.text_profile = Some(profile.clone());
            for p in self.pages {
                self.set_text(ui, p.requirements, &requirement_text(profile, p.tab));
                self.set_text(ui, p.owner, &owner_line(profile));
            }
        }
        for p in self.pages {
            populate(ui, p.list, view.staged(p.tab), items, icons);
            if let Some(b) = p.button {
                ui.set_enabled(b, view.can_submit(p.tab));
            }
        }
    }

    /// Show or hide the window as the view stands now (after the window
    /// was attached).
    pub fn sync_visibility(
        &mut self,
        ui: &mut UiRoot,
        view: &HousePurchaseView,
        items: &ItemsView<'_>,
        icons: &mut dyn IconSource,
    ) {
        self.was_open = !view.is_open();
        self.last_revision = None;
        self.update(ui, view, items, icons);
    }

    fn set_text(&self, ui: &mut UiRoot, id: Option<ElementId>, s: &str) {
        if let Some(t) = id.and_then(|id| ui.widget_mut::<Text>(id)) {
            if t.font.is_none() {
                t.font.clone_from(&self.font);
            }
            t.set_lines(vec![Line::new(s, crate::draw::Color::WHITE)]);
        }
    }

    /// The button: send the tab's staged items, asking first when the
    /// payment calls for it (a no closes the window).
    fn submit(&self, ui: &mut UiRoot, tab: HousePurchaseTab) {
        let Some(profile) = &self.view.profile else {
            return;
        };
        if !self.view.can_submit(tab) {
            return;
        }
        let send = command(HousePurchaseCommand::Submit {
            tab,
            slumlord: profile.slumlord_guid,
        });
        match self.view.confirmation(tab, self.player) {
            Some(question) => emit(
                ui,
                PanelOutput::ConfirmEither {
                    message: question.to_owned(),
                    on_yes: Box::new(send),
                    on_no: Box::new(command(HousePurchaseCommand::Close)),
                },
            ),
            None => emit(ui, send),
        }
    }

    fn tab_of_list(&self, ui: &UiRoot, element: ElementId) -> Option<HousePurchaseTab> {
        self.pages
            .iter()
            .find(|p| element == p.list || ui.parent(element) == Some(p.list))
            .map(|p| p.tab)
    }
}

fn populate(
    ui: &mut UiRoot,
    list: ElementId,
    guids: &[u32],
    items: &ItemsView<'_>,
    icons: &mut dyn IconSource,
) {
    ui.with::<ItemList, _>(list, |l, ui| {
        l.defer_layout();
        l.flush(ui);
    });
    for &guid in guids {
        let Some(item) = items.get(guid) else {
            continue;
        };
        let index = ui.widget::<ItemList>(list).map_or(0, ItemList::num_items);
        let mut slot = ItemSlot {
            slot_index: i32::try_from(index).unwrap_or(i32::MAX),
            allow_drag_source: true,
            source_kind: ItemDragSource::Vendor,
            ..ItemSlot::default()
        };
        fill_slot(&mut slot, Some(item), items, icons);
        slot.show_trade_overlay = true;
        slot.trade_overlay_sprite = TRADE_OVERLAY_SPRITE;
        let cell = ui.insert(slot_element(), slot);
        ui.with::<ItemList, _>(list, |l, ui| l.add_item(ui, list, cell));
    }
    ui.with::<ItemList, _>(list, |l, ui| l.end_defer_layout(ui, list));
}

fn slot_object(ui: &UiRoot, cell: ElementId) -> u32 {
    ui.widget::<ItemSlot>(cell).map_or(0, |s| s.object_id)
}

impl PanelController for HousePurchaseController {
    fn on_element_message(
        &mut self,
        ui: &mut UiRoot,
        element: ElementId,
        message: &ElementMessage,
    ) -> bool {
        let clicked = matches!(message, ElementMessage::Clicked { .. });
        if Some(element) == self.close {
            if clicked && self.view.is_open() {
                emit(ui, command(HousePurchaseCommand::Close));
            }
            return true;
        }
        for p in self.pages {
            if Some(element) == p.tab_element {
                if clicked {
                    self.select(ui, p.tab);
                }
                return true;
            }
            if Some(element) == p.button {
                if clicked {
                    self.submit(ui, p.tab);
                }
                return true;
            }
        }
        let Some(tab) = self.tab_of_list(ui, element) else {
            return false;
        };
        match message {
            ElementMessage::ItemDragLifted(p) if p.obj_id != 0 => {
                emit(
                    ui,
                    command(HousePurchaseCommand::Unstage {
                        tab,
                        item: p.obj_id,
                    }),
                );
            }
            ElementMessage::ItemDragEntered(p) => {
                let a = if p.source_kind == ItemDragSource::Inventory {
                    DragAcceptance::Accept
                } else {
                    DragAcceptance::Reject
                };
                if let Some(s) = ui.widget_mut::<ItemSlot>(element) {
                    s.set_drag_acceptance(a);
                }
            }
            // A drop from the packs, the ground or an outside container
            // reaches the session, which refuses (and for one the player
            // does not carry, says so) what it does not take.
            ElementMessage::ItemDropped(p)
                if self.view.is_open()
                    && matches!(
                        p.source_kind,
                        ItemDragSource::Inventory | ItemDragSource::Ground
                    ) =>
            {
                emit(
                    ui,
                    command(HousePurchaseCommand::Stage {
                        tab,
                        item: p.obj_id,
                    }),
                );
            }
            // A right click takes a staged item off the row.
            ElementMessage::RightClicked => {
                let id = slot_object(ui, element);
                if id != 0 {
                    emit(ui, command(HousePurchaseCommand::Unstage { tab, item: id }));
                }
            }
            _ => return false,
        }
        true
    }
}
