//! Panel controllers: they bind an imported retail layout to plain view
//! models (built by the client from the session's events and snapshots)
//! and turn what the player does in the panel into commands
//! ([`PanelOutput`], queued as [`crate::UiOutput::Panel`]). They never
//! change runtime state themselves.

pub mod appraisal;
pub mod book;
pub mod cell_template;
pub mod character;
pub mod character_info;
pub mod character_titles;
pub mod chat;
pub mod combat_bar;
pub mod contracts;
pub mod dialog;
pub mod dialog_factory;
pub mod external_container;
pub mod floating_chat;
pub mod fps;
pub mod house_purchase;
pub mod indicators;
pub mod inventory;
pub mod item_cooldown;
pub mod items;
pub mod journal;
pub mod journal_notes;
pub mod journal_pages;
pub mod jump_powerbar;
pub mod keyboard_config;
pub mod magic;
pub mod main_panels;
pub mod map_house;
pub mod mini_game;
pub mod options;
pub mod paperdoll;
pub mod projectile_debug;
pub mod radar;
pub mod salvage;
pub mod selected_object;
pub mod social;
pub mod spell_components;
pub mod spew_box;
pub mod toolbar;
pub mod trade;
pub mod transcript;
pub mod vendor;
pub mod vitals;

use std::collections::HashMap;
use std::sync::Arc;

use ac_core::items::ShortcutEntry;
use ac_runtime::session::SessionCommand;

use crate::element_info::ElementInfo;
use crate::import::{BuildContext, LayoutSource, build, import_infos_rooted};
use crate::root::UiRoot;
use crate::strings::StringResolver;
use crate::tree::ElementId;

pub use chat::{ChatContext, ChatFeed, ChatSubmission, ChatWindowController};
pub use inventory::{InventoryController, InventorySprites};
pub use items::ItemsView;
pub use radar::{RadarController, RadarObject, RadarPlayer, RadarScene, build_snapshot};
pub use transcript::TranscriptLine;
pub use vitals::{VitalView, VitalsController, VitalsSideBySide, VitalsView};

/// Authored strings a panel reads: plain entries and templates, by string
/// table. [`StringResolver`] is the dat-backed one; tests give their own.
pub trait PanelStrings {
    /// String `string_id` of table `table_id` (its first variant).
    fn string(&self, table_id: u32, string_id: u32) -> Option<String>;
    /// Template `key_hash` of table `table_id` with `variables` filled in.
    fn template(
        &self,
        table_id: u32,
        key_hash: u32,
        variables: &HashMap<u32, String>,
    ) -> Option<String>;
}

impl PanelStrings for StringResolver {
    fn string(&self, table_id: u32, string_id: u32) -> Option<String> {
        self.resolve(table_id, string_id, 0)
    }

    fn template(
        &self,
        table_id: u32,
        key_hash: u32,
        variables: &HashMap<u32, String>,
    ) -> Option<String> {
        self.resolve_template_hash(table_id, key_hash, variables)
    }
}

/// Builds a list row from an authored row template (a list's template
/// entry names its layout and element). The row comes back detached.
pub trait RowTemplates {
    /// Build element `element` of layout `layout` with its subtree.
    fn build_row(&mut self, ui: &mut UiRoot, layout: u32, element: u32) -> Option<ElementId>;
}

/// Row templates read from the dats: each template's element tree is read
/// once (a miss too), and every row gets its own widgets built from it.
pub struct DatRowTemplates<'a> {
    source: &'a dyn LayoutSource,
    font: Option<Arc<crate::UiFont>>,
    imported: HashMap<(u32, u32), Option<Arc<ElementInfo>>>,
}

impl<'a> DatRowTemplates<'a> {
    /// Templates from `source`, `font` for elements that name none.
    #[must_use]
    pub fn new(source: &'a dyn LayoutSource, font: Option<Arc<crate::UiFont>>) -> Self {
        Self {
            source,
            font,
            imported: HashMap::new(),
        }
    }
}

impl std::fmt::Debug for DatRowTemplates<'_> {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("DatRowTemplates").finish_non_exhaustive()
    }
}

impl RowTemplates for DatRowTemplates<'_> {
    fn build_row(&mut self, ui: &mut UiRoot, layout: u32, element: u32) -> Option<ElementId> {
        let source = self.source;
        let info = self
            .imported
            .entry((layout, element))
            .or_insert_with(|| import_infos_rooted(source, layout, element).map(Arc::new))
            .clone()?;
        let ctx = BuildContext {
            source,
            default_font: self.font.clone(),
            source_layout_did: layout,
            media_seconds: ui.media_seconds(),
        };
        Some(build(ui, &info, &ctx).root)
    }
}

/// A row-template resolver over any import and build: a template's import
/// (layout, element) runs once, a miss included, and every row is built
/// fresh from it.
pub struct RowTemplateResolver<I, B> {
    import: I,
    build: B,
    imported: HashMap<(u32, u32), Option<Arc<ElementInfo>>>,
    import_count: usize,
}

impl<I, B> std::fmt::Debug for RowTemplateResolver<I, B> {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("RowTemplateResolver")
            .field("import_count", &self.import_count)
            .finish_non_exhaustive()
    }
}

impl<I, B> RowTemplateResolver<I, B>
where
    I: FnMut(u32, u32) -> Option<ElementInfo>,
    B: FnMut(&mut UiRoot, &ElementInfo) -> ElementId,
{
    /// A resolver that reads templates with `import` and builds rows with
    /// `build`.
    pub fn new(import: I, build: B) -> Self {
        Self {
            import,
            build,
            imported: HashMap::new(),
            import_count: 0,
        }
    }

    /// How many templates were read.
    #[must_use]
    pub fn import_count(&self) -> usize {
        self.import_count
    }
}

impl<I, B> RowTemplates for RowTemplateResolver<I, B>
where
    I: FnMut(u32, u32) -> Option<ElementInfo>,
    B: FnMut(&mut UiRoot, &ElementInfo) -> ElementId,
{
    fn build_row(&mut self, ui: &mut UiRoot, layout: u32, element: u32) -> Option<ElementId> {
        let info = if let Some(info) = self.imported.get(&(layout, element)) {
            info.clone()
        } else {
            self.import_count += 1;
            let info = (self.import)(layout, element).map(Arc::new);
            self.imported.insert((layout, element), info.clone());
            info
        }?;
        Some((self.build)(ui, &info))
    }
}

impl<F: FnMut(&mut UiRoot, u32, u32) -> Option<ElementId>> RowTemplates for F {
    fn build_row(&mut self, ui: &mut UiRoot, layout: u32, element: u32) -> Option<ElementId> {
        self(ui, layout, element)
    }
}

/// Link list box `list` to its authored scrollbar (its scrollbar property,
/// found under `scope`): the bar scrolls the list's rows. False when the
/// list names none or it is not there.
pub fn link_list_scrollbar(ui: &mut UiRoot, scope: ElementId, list: ElementId) -> bool {
    let bar_id = ui
        .widget::<crate::widgets::ListBox>(list)
        .map_or(0, |b| b.scrollbar_element_id);
    if bar_id == 0 {
        return false;
    }
    let Some(bar) = ui.find_descendant(scope, bar_id) else {
        return false;
    };
    match ui.widget_mut::<crate::widgets::Scrollbar>(bar) {
        Some(s) => {
            s.model = crate::widgets::ScrollSource::Element(list);
            true
        }
        None => false,
    }
}

/// The retail main-panel ids (the toolbar buttons' property 0x10000029).
pub mod panel_id {
    /// A book.
    pub const BOOK: u32 = 2;
    /// Character information.
    pub const CHARACTER_INFORMATION: u32 = 3;
    /// Positive effects.
    pub const POSITIVE_EFFECTS: u32 = 4;
    /// Negative effects.
    pub const NEGATIVE_EFFECTS: u32 = 5;
    /// The inventory.
    pub const INVENTORY: u32 = 7;
    /// Link status.
    pub const LINK_STATUS: u32 = 8;
    /// The mini-game.
    pub const MINI_GAME: u32 = 9;
    /// Options.
    pub const OPTIONS: u32 = 10;
    /// The character (attributes and skills).
    pub const CHARACTER: u32 = 11;
    /// Social.
    pub const SOCIAL: u32 = 12;
    /// Magic (the spellbook).
    pub const MAGIC: u32 = 13;
    /// Vitae.
    pub const VITAE: u32 = 15;
    /// Map and house.
    pub const MAP_HOUSE: u32 = 16;
    /// The journal.
    pub const JOURNAL: u32 = 25;
    /// The vendor window. Not a retail toolbar id (retail opens the window
    /// from the shop itself): the client's id for it, so its close can be
    /// confirmed through [`super::PanelOutput::Confirm`] and answered with
    /// [`super::vendor::VendorController::confirm_close`].
    pub const VENDOR: u32 = 0x8000_0001;
}

/// What a panel asks the rest of the client to do.
#[derive(Debug, Clone)]
pub enum PanelOutput {
    /// Send this command to the session.
    Session(SessionCommand),
    /// Select this object (a radar blip or an item cell was clicked).
    SelectObject(u32),
    /// The object under the pointer on a panel changed (status text,
    /// target highlight).
    HoveredObject(Option<u32>),
    /// A map-coordinate link in chat was clicked: (east-west, north-south),
    /// east and north positive, with the text it read.
    CoordinateLinkClicked {
        /// East (+) / west (−).
        east_west: f64,
        /// North (+) / south (−).
        north_south: f64,
        /// "12.3N, 45.6E".
        text: String,
    },
    /// Show or hide floating chat window `1..=4`.
    ToggleFloatingChat(u8),
    /// Lock (`true`) or unlock the UI.
    SetUiLocked(bool),
    /// Client-local text the player reads (the status line), worded by
    /// the panel (a refused drop).
    Notice(String),
    /// Ask the server for this owned item's mana (0: close the open
    /// query): the selected-object strip's mana bar.
    QueryItemMana(u32),
    /// Ping the server for the link-status panel's round trip.
    RequestLinkPing,
    /// Open or close main panel `panel_id` (see [`panel_id`]).
    TogglePanel(u32),
    /// Close main panel `panel_id` (its close button).
    ClosePanel(u32),
    /// Put the selected stack's split quantity back to the whole stack of
    /// `stack` units (the vendor window refused lifting part of a stack;
    /// the toolbar's split controls own the quantity:
    /// [`crate::panels::toolbar::StackSplitController::reset_to_whole`]).
    ResetSplitQuantity {
        /// The stack's size.
        stack: u32,
    },
    /// Put this shortcut on the toolbar (the client sends it as
    /// `ItemCommand::AddShortcut`).
    AddShortcut(ShortcutEntry),
    /// Take the shortcut in this slot off the toolbar
    /// (`ItemCommand::RemoveShortcut`).
    RemoveShortcut(i32),
    /// The combat mode toggle (a combat-mode indicator was clicked).
    ToggleCombat,
    /// Enter the use target mode (the use button with nothing selected;
    /// `ItemCommand::EnterUseMode`).
    EnterUseMode,
    /// Enter the examine target mode (the examine button with nothing
    /// selected; `ItemCommand::EnterExamineMode`).
    EnterExamineMode,
    /// Leave the target mode (an empty shortcut key pressed in it;
    /// `ItemCommand::CancelTargetMode`).
    CancelTargetMode,
    /// Spend experience raising an attribute (1..=6)
    /// (`CharacterCommand::RaiseAttribute`).
    RaiseAttribute {
        /// Attribute id.
        attribute: u32,
        /// Experience spent.
        xp: u64,
    },
    /// Spend experience raising a vital (1 health, 3 stamina, 5 mana)
    /// (`CharacterCommand::RaiseVital`).
    RaiseVital {
        /// Vital id.
        vital: u32,
        /// Experience spent.
        xp: u64,
    },
    /// Spend experience raising a trained skill
    /// (`CharacterCommand::RaiseSkill`).
    RaiseSkill {
        /// Skill id.
        skill: u32,
        /// Experience spent.
        xp: u64,
    },
    /// Spend skill credits training a skill: the client asks the player to
    /// confirm first, then sends `CharacterCommand::TrainSkill`.
    TrainSkill {
        /// Skill id.
        skill: u32,
        /// Credits spent.
        credits: u32,
    },
    /// Examine a spell locally (no server request).
    ExamineSpell(u32),
    /// Flash the paperdoll figure's parts (the selection changed): the
    /// whole figure, or one bit per part.
    FlashFigure {
        /// The player selected himself.
        whole_figure: bool,
        /// One bit per figure part.
        part_mask: u32,
    },
    /// Close the examination window (its close button).
    CloseExamination,
    /// Put this spell on the open favourite bar (a double click in the
    /// spellbook; the spell bars' controller answers it with
    /// [`crate::panels::magic::SpellcastingController::add_favorite`]).
    FavoriteSpell(u32),
    /// The options window's request (settings changed, a Gameplay button).
    Options(options::OptionsOutput),
    /// The Configure Keyboard screen's request.
    KeyboardConfig(keyboard_config::KeyboardConfigOutput),
    /// Ask the player yes or no; on yes, act on `on_yes` (the confirmation
    /// dialog is the host's).
    Confirm {
        /// The question.
        message: String,
        /// What a yes does.
        on_yes: Box<PanelOutput>,
    },
    /// Ask the player yes or no; on yes act on `on_yes`, on no on `on_no`.
    ConfirmEither {
        /// The question.
        message: String,
        /// What a yes does.
        on_yes: Box<PanelOutput>,
        /// What a no does.
        on_no: Box<PanelOutput>,
    },
    /// The Game Center's resignation question was answered (yes:
    /// resign); the window tells the board, which sends the resignation on
    /// a yes.
    ChessResignAnswered(bool),
}
