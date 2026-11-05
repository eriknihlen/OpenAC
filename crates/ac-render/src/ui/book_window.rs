//! The book reader in the game window, as OpenAC's
//! `RetailUiRuntime.MountBookPanel` mounts it (the main panel frame's
//! slot 0x10000182 at (250, 150), its page text made typeable before the
//! build), fed the session's open book, shown when a book opens and
//! closing the book when it is hidden.

use ac_core::properties::PropertyBool;
use ac_ui::font::DEFAULT_FONT_ID;
use ac_ui::geometry::AnchorEdges;
use ac_ui::import::{BuildContext, LayoutSource, build, import_infos_rooted};
use ac_ui::panels::book::{self, BookContext, BookPanelController};
use ac_ui::panels::main_panels::RESTORE_PREVIOUS_PROPERTY;
use ac_ui::panels::panel_id;
use ac_ui::widgets::NineSliceFrame;

use super::facts::Dirty;
use super::mount::{Chrome, MountOptions, mount_window};
use super::social_panels::authors_full_panel_center;
use super::window::WindowUi;

/// The book reader's name.
pub const BOOK_WINDOW: &str = "book";

/// The player's flags that let him see the writers' accounts (OpenAC's
/// `ShowsAuthorAccount` binding).
const ACCOUNT_SEERS: [PropertyBool; 5] = [
    PropertyBool::IS_ADMIN,
    PropertyBool::IS_ARCH,
    PropertyBool::IS_SENTINEL,
    PropertyBool::IS_ADVOCATE,
    PropertyBool::IS_PSR,
];

impl WindowUi {
    /// OpenAC `RetailUiRuntime.MountBookPanel`.
    pub(super) fn mount_book(&mut self) {
        match self.mount_book_window() {
            Ok(()) => self.mounted.push(BOOK_WINDOW),
            Err(e) => self.notices.push(format!("ui: {BOOK_WINDOW}: {e}")),
        }
    }

    fn mount_book_window(&mut self) -> Result<(), String> {
        let mut info =
            import_infos_rooted(&self.content, book::HOST_LAYOUT_ID, book::SLOT_ELEMENT_ID)
                .ok_or("layout 0x2100006E slot 0x10000182 is not in the dats")?;
        book::mark_page_text_typeable(&mut info);
        let ctx = BuildContext {
            source: &self.content,
            default_font: self.content.font(DEFAULT_FONT_ID),
            source_layout_did: book::HOST_LAYOUT_ID,
            media_seconds: self.host.root.media_seconds(),
        };
        let layout = build(&mut self.host.root, &info, &ctx);
        let ui = &mut self.host.root;
        let controller =
            BookPanelController::bind(ui, &layout).ok_or("the slot carries no page text")?;
        let frame = mount_window(
            ui,
            BOOK_WINDOW,
            layout.root,
            &MountOptions {
                chrome: Chrome::NineSlice,
                left: 250.0,
                top: 150.0,
                visible: false,
                resize_x: false,
                resize_y: false,
                content_anchors: AnchorEdges::LEFT | AnchorEdges::TOP,
                content_click_through: Some(false),
                ..MountOptions::default()
            },
        )?;
        if let Some(f) = ui.widget_mut::<NineSliceFrame>(frame) {
            f.draw_center_fill = !authors_full_panel_center(&info);
        }
        self.host
            .attach_controller(BOOK_WINDOW, Box::new(controller))
            .map_err(|e| e.to_string())?;
        let restore = info.effective_bool(RESTORE_PREVIOUS_PROPERTY, None) == Some(true);
        self.panels
            .register(&mut self.host.root, panel_id::BOOK, BOOK_WINDOW, restore);
        Ok(())
    }

    /// Feed the reader the open book when it changed: a book opening shows
    /// it, a book closing hides it.
    pub(super) fn update_book(&mut self, dirty: Dirty) {
        if !dirty.book {
            return;
        }
        let Some(view) = self.facts.book.clone() else {
            return;
        };
        let player = self.facts.state.player;
        let objects = &self.facts.objects;
        let book_name = objects
            .get(view.book_guid)
            .map(|o| o.appropriate_name().into_owned())
            .unwrap_or_default();
        let shows_account = objects.get(player).is_some_and(|p| {
            ACCOUNT_SEERS
                .iter()
                .any(|b| p.properties.bools.get(&b.0).copied() == Some(true))
        });
        let context = BookContext {
            player,
            book_name: &book_name,
            shows_account,
        };
        let shown = self
            .host
            .with_controller::<BookPanelController, _>(BOOK_WINDOW, |c, ui| {
                c.update(ui, &view, &context)
            })
            .flatten();
        if let Some(open) = shown {
            self.panels
                .set_visibility(&mut self.host.root, panel_id::BOOK, open);
        }
    }

    /// The reader was hidden (its close, the panel frame, another main
    /// panel): the book closes, saving the open page.
    pub(super) fn book_shown(&mut self, name: &str, visible: bool) {
        if name != BOOK_WINDOW || visible {
            return;
        }
        self.host
            .with_controller::<BookPanelController, _>(BOOK_WINDOW, |c, ui| c.on_hidden(ui));
    }
}
