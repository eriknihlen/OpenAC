//! Mounting the secure-trade window, the vendor window, the social panel
//! and the journal (its contracts page) in the game window, as OpenAC's
//! `RetailUiRuntime` mounts them (`MountSecureTrade`, `MountVendor`,
//! `MountSocialPanel`, `MountJournalPanel`), feeding them from the window's
//! copy of the session's facts every frame, and the social panel's keys
//! (`OpenSocialPanel`).

use std::time::{SystemTime, UNIX_EPOCH};

use ac_ui::element_info::ElementInfo;
use ac_ui::font::DEFAULT_FONT_ID;
use ac_ui::geometry::{AnchorEdges, ResizeEdges};
use ac_ui::import::{LayoutSource, import_infos_rooted, import_rooted};
use ac_ui::panels::cell_template::empty_sprite;
use ac_ui::panels::contracts::ContractsView;
use ac_ui::panels::house_purchase::{self, HousePurchaseController, HousePurchaseSprites};
use ac_ui::panels::journal::{self, JournalPanelController};
use ac_ui::panels::main_panels::RESTORE_PREVIOUS_PROPERTY;
use ac_ui::panels::social::{self, AllegianceSource, SocialPanelController, SocialView};
use ac_ui::panels::toolbar::ToolbarWindowController;
use ac_ui::panels::trade::{self, SecureTradeController, TradeSprites};
use ac_ui::panels::vendor::{self, VendorController, VendorSprites};
use ac_ui::panels::{DatRowTemplates, PanelOutput, panel_id};
use ac_ui::root::UiRoot;
use ac_ui::widgets::NineSliceFrame;
use ac_ui::widgets::chrome::CENTER_FILL;

use super::facts::Dirty;
use super::mount::{Chrome, MountOptions, mount_window};
use super::window::{DialogAction, TOOLBAR_WINDOW, WindowUi, make_dialog};

/// The secure-trade window's name.
pub const TRADE_WINDOW: &str = "secure-trade";
/// The vendor window's name.
pub const VENDOR_WINDOW: &str = "vendor";
/// The social panel's name.
pub const SOCIAL_WINDOW: &str = "social-panel";
/// The journal's name.
pub const JOURNAL_WINDOW: &str = "journal";
/// The slumlord window's name.
pub const HOUSE_PURCHASE_WINDOW: &str = "house-purchase";

/// The player's allegiance rank property.
const ALLEGIANCE_RANK_PROPERTY: u32 = 0x1E;

/// A page of the social panel a key opens.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SocialPage {
    /// The allegiance (F3).
    Allegiance,
    /// The fellowship (F4).
    Fellowship,
    /// The friends list (no default key).
    Friends,
}

/// The experience a fellow at `level` needs for the next level (0 at the
/// top or past the table): OpenAC `RetailUiRuntime.ExperienceToRaiseLevel`.
#[must_use]
pub fn experience_to_raise(levels: &[u64], level: u32) -> i64 {
    let level = level as usize;
    if level + 1 >= levels.len() {
        return 0;
    }
    let (current, next) = (levels[level], levels[level + 1]);
    if next > current {
        i64::try_from(next - current).unwrap_or(i64::MAX)
    } else {
        0
    }
}

/// The panel authors its own full centre (the chrome's centre art over
/// its body), so the frame does not draw one: OpenAC
/// `RetailUiRuntime.AuthorsFullPanelCenter`.
#[must_use]
pub fn authors_full_panel_center(root: &ElementInfo) -> bool {
    const TITLE_HEIGHT: f32 = 25.0;
    fn visit(info: &ElementInfo, root: &ElementInfo) -> bool {
        let covers_body = info.width >= root.width && info.height >= root.height - TITLE_HEIGHT;
        if covers_body && info.state_media.values().any(|m| m.file == CENTER_FILL) {
            return true;
        }
        info.children.iter().any(|c| visit(c, root))
    }
    visit(root, root)
}

fn set_center_fill(ui: &mut UiRoot, frame: ac_ui::tree::ElementId, draw: bool) {
    if let Some(f) = ui.widget_mut::<NineSliceFrame>(frame) {
        f.draw_center_fill = draw;
    }
}

impl WindowUi {
    /// Mount the social panel, the journal, the vendor window and the trade
    /// window (OpenAC's order).
    pub(super) fn mount_dealings(&mut self) {
        let results = [
            (SOCIAL_WINDOW, self.mount_social()),
            (JOURNAL_WINDOW, self.mount_journal()),
            (VENDOR_WINDOW, self.mount_vendor()),
            (TRADE_WINDOW, self.mount_trade()),
            (HOUSE_PURCHASE_WINDOW, self.mount_house_purchase()),
        ];
        for (name, result) in results {
            match result {
                Ok(()) => self.mounted.push(name),
                Err(e) => self.notices.push(format!("ui: {name}: {e}")),
            }
        }
    }

    /// OpenAC `RetailUiRuntime.MountSocialPanel`.
    fn mount_social(&mut self) -> Result<(), String> {
        let info = import_infos_rooted(
            &self.content,
            social::HOST_LAYOUT_ID,
            social::SLOT_ELEMENT_ID,
        )
        .ok_or("layout 0x2100006E slot 0x1000018F is not in the dats")?;
        let font = self.content.font(DEFAULT_FONT_ID);
        let layout = import_rooted(
            &mut self.host.root,
            &self.content,
            social::HOST_LAYOUT_ID,
            social::SLOT_ELEMENT_ID,
            font,
        )
        .ok_or("layout 0x2100006E slot 0x1000018F is not in the dats")?;
        let ui = &mut self.host.root;
        let mut controller = SocialPanelController::bind(ui, &layout, Some(self.content.strings()))
            .ok_or("the panel's root is not a tab panel")?;
        controller.activate_tabs(ui);
        let frame = mount_window(
            ui,
            SOCIAL_WINDOW,
            layout.root,
            &MountOptions {
                chrome: Chrome::NineSlice,
                left: 200.0,
                top: 140.0,
                visible: false,
                resize_x: false,
                resize_y: true,
                resizable_edges: ResizeEdges::BOTTOM,
                constrain_resize_to_parent: true,
                content_anchors: AnchorEdges::ALL,
                content_click_through: Some(false),
                ..MountOptions::default()
            },
        )?;
        set_center_fill(ui, frame, !authors_full_panel_center(&info));
        self.host
            .attach_controller(SOCIAL_WINDOW, Box::new(controller))
            .map_err(|e| e.to_string())?;
        let restore = info.effective_bool(RESTORE_PREVIOUS_PROPERTY, None) == Some(true);
        self.panels.register(
            &mut self.host.root,
            panel_id::SOCIAL,
            SOCIAL_WINDOW,
            restore,
        );
        Ok(())
    }

    /// OpenAC `RetailUiRuntime.MountJournalPanel` (the contracts page only).
    fn mount_journal(&mut self) -> Result<(), String> {
        let info = import_infos_rooted(
            &self.content,
            journal::HOST_LAYOUT_ID,
            journal::SLOT_ELEMENT_ID,
        )
        .ok_or("layout 0x2100006E slot 0x10000559 is not in the dats")?;
        let font = self.content.font(DEFAULT_FONT_ID);
        let layout = import_rooted(
            &mut self.host.root,
            &self.content,
            journal::HOST_LAYOUT_ID,
            journal::SLOT_ELEMENT_ID,
            font,
        )
        .ok_or("layout 0x2100006E slot 0x10000559 is not in the dats")?;
        let ui = &mut self.host.root;
        let mut controller = JournalPanelController::bind(ui, &layout)
            .ok_or("the panel's root is not a tab panel")?;
        controller.activate_tabs(ui);
        let frame = mount_window(
            ui,
            JOURNAL_WINDOW,
            layout.root,
            &MountOptions {
                chrome: Chrome::NineSlice,
                left: 230.0,
                top: 160.0,
                visible: false,
                resize_x: false,
                resize_y: true,
                resizable_edges: ResizeEdges::BOTTOM,
                constrain_resize_to_parent: true,
                content_anchors: AnchorEdges::ALL,
                content_click_through: Some(false),
                ..MountOptions::default()
            },
        )?;
        set_center_fill(ui, frame, !authors_full_panel_center(&info));
        self.host
            .attach_controller(JOURNAL_WINDOW, Box::new(controller))
            .map_err(|e| e.to_string())?;
        let restore = info.effective_bool(RESTORE_PREVIOUS_PROPERTY, None) == Some(true);
        self.panels.register(
            &mut self.host.root,
            panel_id::JOURNAL,
            JOURNAL_WINDOW,
            restore,
        );
        Ok(())
    }

    /// OpenAC `RetailUiRuntime.MountVendor`: at the layout's own corner, its
    /// own size, no frame centre; hidden until a shop opens.
    fn mount_vendor(&mut self) -> Result<(), String> {
        let sprite = |list| empty_sprite(&self.content, vendor::LAYOUT_ID, list);
        let sprites = VendorSprites {
            items_empty: sprite(vendor::ITEM_LIST_ID),
            buying_empty: sprite(vendor::BUYING_LIST_ID),
            selling_empty: sprite(vendor::SELLING_LIST_ID),
        };
        let font = self.content.font(DEFAULT_FONT_ID);
        let layout = import_rooted(
            &mut self.host.root,
            &self.content,
            vendor::LAYOUT_ID,
            vendor::ROOT_ID,
            font.clone(),
        )
        .ok_or("layout 0x21000012 root 0x100000B7 is not in the dats")?;
        let ui = &mut self.host.root;
        let (left, top) = ui
            .element(layout.root)
            .map_or((0.0, 0.0), |e| (e.left, e.top));
        let frame = mount_window(
            ui,
            VENDOR_WINDOW,
            layout.root,
            &MountOptions {
                chrome: Chrome::NineSlice,
                left,
                top,
                visible: false,
                resize_x: false,
                resize_y: false,
                constrain_resize_to_parent: true,
                ..MountOptions::default()
            },
        )?;
        set_center_fill(ui, frame, false);
        let controller = VendorController::bind(ui, &layout, VENDOR_WINDOW, sprites, font)
            .ok_or("required authored controls are missing")?;
        self.host
            .attach_controller(VENDOR_WINDOW, Box::new(controller))
            .map_err(|e| e.to_string())
    }

    /// OpenAC `RetailUiRuntime.MountSecureTrade`: centred, its own size;
    /// hidden until a trade opens.
    fn mount_trade(&mut self) -> Result<(), String> {
        let sprites = TradeSprites {
            self_empty: empty_sprite(&self.content, trade::LAYOUT_ID, trade::SELF_LIST_ID),
            partner_empty: empty_sprite(&self.content, trade::LAYOUT_ID, trade::PARTNER_LIST_ID),
        };
        let font = self.content.font(DEFAULT_FONT_ID);
        let layout = import_rooted(
            &mut self.host.root,
            &self.content,
            trade::LAYOUT_ID,
            trade::ROOT_ID,
            font.clone(),
        )
        .ok_or("layout 0x2100000D root 0x1000007A is not in the dats")?;
        let ui = &mut self.host.root;
        let (width, height) = ui
            .element(layout.root)
            .map_or((0.0, 0.0), |e| (e.width, e.height));
        let (screen_w, screen_h) = ui.size();
        mount_window(
            ui,
            TRADE_WINDOW,
            layout.root,
            &MountOptions {
                chrome: Chrome::NineSlice,
                left: ((screen_w - width) * 0.5).max(0.0),
                top: ((screen_h - height) * 0.5).max(0.0),
                visible: false,
                resize_x: false,
                resize_y: false,
                constrain_resize_to_parent: true,
                ..MountOptions::default()
            },
        )?;
        let controller = SecureTradeController::bind(ui, &layout, TRADE_WINDOW, sprites, font)
            .ok_or("required authored grids are missing")?;
        self.host
            .attach_controller(TRADE_WINDOW, Box::new(controller))
            .map_err(|e| e.to_string())
    }

    /// OpenAC `RetailUiRuntime.MountHousePurchase`: the slumlord window,
    /// centred, its own size; hidden until a slumlord's profile opens it.
    fn mount_house_purchase(&mut self) -> Result<(), String> {
        let sprites = HousePurchaseSprites {
            buy_empty: empty_sprite(
                &self.content,
                house_purchase::LAYOUT_ID,
                house_purchase::BUY_ITEMS_LIST_ID,
            ),
            maintenance_empty: empty_sprite(
                &self.content,
                house_purchase::LAYOUT_ID,
                house_purchase::MAINTENANCE_ITEMS_LIST_ID,
            ),
        };
        let font = self.content.font(DEFAULT_FONT_ID);
        let layout = import_rooted(
            &mut self.host.root,
            &self.content,
            house_purchase::LAYOUT_ID,
            house_purchase::ROOT_ID,
            font.clone(),
        )
        .ok_or("layout 0x2100000E root 0x1000008C is not in the dats")?;
        let ui = &mut self.host.root;
        let (width, height) = ui
            .element(layout.root)
            .map_or((0.0, 0.0), |e| (e.width, e.height));
        let (screen_w, screen_h) = ui.size();
        mount_window(
            ui,
            HOUSE_PURCHASE_WINDOW,
            layout.root,
            &MountOptions {
                chrome: Chrome::NineSlice,
                left: ((screen_w - width) * 0.5).max(0.0),
                top: ((screen_h - height) * 0.5).max(0.0),
                visible: false,
                resize_x: false,
                resize_y: false,
                constrain_resize_to_parent: true,
                ..MountOptions::default()
            },
        )?;
        let controller =
            HousePurchaseController::bind(ui, &layout, HOUSE_PURCHASE_WINDOW, sprites, font)
                .ok_or("required authored item lists are missing")?;
        self.host
            .attach_controller(HOUSE_PURCHASE_WINDOW, Box::new(controller))
            .map_err(|e| e.to_string())
    }

    /// Feed the trade and vendor windows, the social panel and the
    /// contracts page from the window's copy of the facts: the trade and
    /// the shop's events as they come, the social panel when its facts or
    /// the selection changed or while it shows, the contracts page when the
    /// tracker changed or while it shows (its timers count down).
    pub(super) fn update_dealings(&mut self, dirty: Dirty, stale: &[&str]) {
        let selected = self.player_view.and_then(|v| v.selected);
        let shop_closed = self.facts.shop_events.iter().any(Option::is_none);
        if shop_closed {
            self.withdraw_vendor_close_question();
        }
        let shop_events = std::mem::take(&mut self.facts.shop_events);
        let selection_moved = self.fed_social_selection != Some(selected);
        self.fed_social_selection = Some(selected);
        let in_world = self.facts.social.as_ref().map(|s| s.in_world);
        let previous_in_world = std::mem::replace(&mut self.social_in_world, in_world);
        let Self {
            host,
            facts,
            icons,
            content,
            experience,
            status,
            ..
        } = self;
        let split = host.with_controller::<ToolbarWindowController, _>(TOOLBAR_WINDOW, |c, _| {
            c.split.quantity().clone()
        });
        let pending: Vec<u32> = facts.state.pending_item.into_iter().collect();
        let resolve = super::facts::composed_names(&status.names);
        let view = facts.items_view(selected, &pending, split.as_ref(), &resolve);
        let strings = content.strings();
        let shown = |host: &ac_ui::UiHost, name: &str| {
            host.root.is_window_visible(name) || stale.contains(&name)
        };

        host.with_controller::<SecureTradeController, _>(TRADE_WINDOW, |c, ui| {
            c.update(ui, &facts.trade, &view, icons, Some(strings));
        });
        host.with_controller::<HousePurchaseController, _>(HOUSE_PURCHASE_WINDOW, |c, ui| {
            c.update(ui, &facts.house_purchase, &view, icons);
        });

        for event in &shop_events {
            host.with_controller::<VendorController, _>(VENDOR_WINDOW, |c, ui| {
                c.on_vendor_event(ui, event.as_deref(), &view, icons);
            });
        }
        if facts.shop.is_some() {
            host.with_controller::<VendorController, _>(VENDOR_WINDOW, |c, ui| {
                c.update(ui, &view, icons);
            });
        }

        let mut rows = DatRowTemplates::new(&*content, content.font(DEFAULT_FONT_ID));
        if let Some(social) = &facts.social {
            // A log off ends the declarations; entering again makes them
            // anew (OpenAC `ResetSessionTransientUi`,
            // `RedeclareSocialPanelAfterWorldEntry`).
            match (previous_in_world, social.in_world) {
                (Some(true), false) => {
                    host.with_controller::<SocialPanelController, _>(SOCIAL_WINDOW, |c, _| {
                        c.reset_session_declaration();
                    });
                }
                (Some(false), true) => {
                    host.with_controller::<SocialPanelController, _>(SOCIAL_WINDOW, |c, ui| {
                        c.redeclare_after_world_entry(ui);
                    });
                }
                _ => {}
            }
            if dirty.social || selection_moved || shown(host, SOCIAL_WINDOW) {
                let player = facts.state.player;
                let rank_quality = facts
                    .objects
                    .get(player)
                    .and_then(|o| o.properties.ints.get(&ALLEGIANCE_RANK_PROPERTY).copied());
                let levels = experience.as_ref().map(|e| e.levels.as_slice());
                let to_raise = |level: u32| levels.map_or(0, |l| experience_to_raise(l, level));
                let objects = &facts.objects;
                let name_of =
                    |guid: u32| objects.get(guid).map(|o| o.appropriate_name().into_owned());
                let social_view = SocialView {
                    player,
                    selected,
                    in_world: social.in_world,
                    options: &social.options,
                    friends: &social.friends,
                    squelch: &social.squelch,
                    fellowship: &social.fellowship,
                    allegiance: AllegianceSource {
                        profile: social.allegiance.as_ref(),
                        rank: social.allegiance_rank,
                        revision: social.allegiance_revision,
                        rank_quality,
                    },
                    experience_to_raise: Some(&to_raise),
                    strings: Some(strings),
                    name_of: Some(&name_of),
                };
                host.with_controller::<SocialPanelController, _>(SOCIAL_WINDOW, |c, ui| {
                    c.update(ui, &social_view, &mut rows);
                });
            }
        }

        if let Some(tracker) = &facts.contracts
            && (dirty.contracts || shown(host, JOURNAL_WINDOW))
        {
            let now_unix_ms = SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .map_or(0, |d| i64::try_from(d.as_millis()).unwrap_or(i64::MAX));
            let contracts = ContractsView {
                contracts: &tracker.contracts,
                display_contract_id: tracker.display_contract_id,
                revision: tracker.revision,
                catalog: &tracker.catalog,
                now_unix_ms,
            };
            host.with_controller::<JournalPanelController, _>(JOURNAL_WINDOW, |c, ui| {
                c.update(ui, &contracts, &mut rows);
            });
        }
    }

    /// The shop closed: a question about leaving it is moot (OpenAC closes
    /// its dialog with the window).
    fn withdraw_vendor_close_question(&mut self) {
        let layout_id = self.dialog_layout;
        let Self {
            host,
            content,
            dialogs,
            ..
        } = self;
        dialogs.withdraw(
            &mut host.root,
            |a| {
                matches!(
                    a,
                    DialogAction::Panel(PanelOutput::ClosePanel(panel_id::VENDOR))
                )
            },
            |ui, kind| make_dialog(ui, content, layout_id, kind),
        );
    }

    /// The window's answer to the trade, vendor and social panels' own
    /// requests: true when `output` was one.
    pub(super) fn dealings_output(&mut self, output: &PanelOutput) -> bool {
        match output {
            PanelOutput::ClosePanel(panel_id::VENDOR) => {
                self.host
                    .with_controller::<VendorController, _>(VENDOR_WINDOW, |c, ui| {
                        c.confirm_close(ui);
                    });
                true
            }
            PanelOutput::ResetSplitQuantity { stack } => {
                self.host
                    .with_controller::<ToolbarWindowController, _>(TOOLBAR_WINDOW, |c, ui| {
                        c.split.reset_to_whole(ui, *stack);
                    });
                true
            }
            _ => false,
        }
    }

    /// OpenAC `RetailUiRuntime.OpenSocialPanel`: shown on that page, it
    /// closes; else it opens there.
    pub(super) fn open_social(&mut self, page: SocialPage) -> bool {
        let visible = self.host.root.is_window_visible(SOCIAL_WINDOW);
        let Some(on_page) = self.host.with_controller::<SocialPanelController, _>(
            SOCIAL_WINDOW,
            |c, ui| match page {
                SocialPage::Allegiance => c.is_showing_allegiance(ui),
                SocialPage::Fellowship => c.is_showing_fellowship(ui),
                SocialPage::Friends => c.is_showing_friends(ui),
            },
        ) else {
            return false;
        };
        if visible && on_page {
            self.panels
                .set_visibility(&mut self.host.root, panel_id::SOCIAL, false);
            return true;
        }
        self.host
            .with_controller::<SocialPanelController, _>(SOCIAL_WINDOW, |c, ui| match page {
                SocialPage::Allegiance => c.show_allegiance(ui),
                SocialPage::Fellowship => c.show_fellowship(ui),
                SocialPage::Friends => c.show_friends(ui),
            });
        self.panels
            .set_visibility(&mut self.host.root, panel_id::SOCIAL, true);
        true
    }

    /// OpenAC `RetailUiRuntime.OpenJournalPanel(Contracts)`: the journal
    /// on its contracts page.
    pub fn open_contracts(&mut self) -> bool {
        let shown = self
            .host
            .with_controller::<JournalPanelController, _>(JOURNAL_WINDOW, |c, ui| {
                c.show_contracts(ui);
            })
            .is_some();
        if shown {
            self.panels
                .set_visibility(&mut self.host.root, panel_id::JOURNAL, true);
        }
        shown
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use ac_ui::element_info::ImageMedia;

    #[test]
    fn a_fellow_needs_the_next_levels_difference() {
        // OpenAC `RetailUiRuntime.ExperienceToRaiseLevel`.
        let levels = [0, 1_000, 2_500];
        assert_eq!(experience_to_raise(&levels, 0), 1_000);
        assert_eq!(experience_to_raise(&levels, 1), 1_500);
        assert_eq!(experience_to_raise(&levels, 2), 0, "the top level");
        assert_eq!(experience_to_raise(&levels, 9), 0);
    }

    #[test]
    fn a_panel_with_its_own_full_centre_gets_no_frame_centre() {
        // OpenAC `RetailUiRuntime.AuthorsFullPanelCenter`: a descendant
        // covering the body below the 25-pixel title with the centre art.
        let mut root = ElementInfo {
            width: 300.0,
            height: 400.0,
            ..ElementInfo::default()
        };
        let mut body = ElementInfo {
            width: 300.0,
            height: 375.0,
            ..ElementInfo::default()
        };
        body.state_media.insert(
            String::new(),
            ImageMedia {
                file: CENTER_FILL,
                draw_mode: 0,
            },
        );
        assert!(!authors_full_panel_center(&root));
        root.children.push(body.clone());
        assert!(authors_full_panel_center(&root));
        root.children[0].height = 370.0;
        assert!(!authors_full_panel_center(&root), "short of the body");
    }
}
