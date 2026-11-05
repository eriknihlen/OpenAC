//! The character creation wizard: six pages (heritage and gender,
//! profession and attributes, skills, appearance, starting town, summary
//! with the name) under one shell with its tabs, back, next, finish, exit
//! and random buttons, and the wizard's dialogs (leave without creating,
//! no name, attribute points left, randomize everything, a name the
//! server refused, a name too long).
//!
//! Every choice is a creation request to the session; the pages show the
//! session's creation snapshot. Opening the wizard rolls a random
//! character and turns its gender to the other one.

pub mod appearance;
pub mod catalog;
pub mod heritage;
pub mod profession;
pub mod skills;
pub mod summary;
pub mod swatch;
pub mod town;

use std::sync::Arc;

use ac_core::chargen::{ChargenHeritageGroup as H, ChargenOptions};
use ac_proto::character::chargen_code;
use ac_runtime::session::Generation;
use ac_runtime::session::creation::{
    ChargenRandom, CreationAppearance, CreationCommand, CreationRejection, CreationSnapshot,
    SeededRandom,
};

use self::appearance::{AppearancePage, Choice, PreviewRequest, SwatchSourcesDyn, SwatchTextures};
use self::heritage::HeritagePage;
use self::profession::ProfessionPage;
use self::skills::SkillsPage;
use self::summary::{NameCommit, SummaryPage};
use self::town::TownPage;
use super::DialogResult;
use super::Dialogs;
use crate::import::ImportedLayout;
use crate::panels::RowTemplates;
use crate::panels::dialog_factory::DialogData;
use crate::root::{ElementMessage, UiRoot};
use crate::tree::ElementId;
use crate::widgets::Button;

/// The wizard's entry in the interface enumeration (category 5).
pub const ROOT_ENUM: u32 = 0x1000_0039;
/// The wizard's root.
pub const ROOT_ELEMENT_ID: u32 = 0x1000_03CC;
/// The progress bar holding the tabs.
pub const PROGRESS_BAR_ID: u32 = 0x1000_03CE;
/// Back.
pub const BACK_ID: u32 = 0x1000_03C6;
/// Next.
pub const NEXT_ID: u32 = 0x1000_03C7;
/// Finish.
pub const FINISH_ID: u32 = 0x1000_03C8;
/// Help.
pub const HELP_ID: u32 = 0x1000_03C9;
/// Exit.
pub const EXIT_ID: u32 = 0x1000_03CA;
/// Random.
pub const RANDOM_ID: u32 = 0x1000_03CB;
/// The page shell.
pub const MASTER_PAGE_ID: u32 = 0x1000_03D0;
/// The pages, in order.
pub const PAGE_IDS: [u32; 6] = [
    0x1000_03D1,
    0x1000_03D2,
    0x1000_03D3,
    0x1000_03D4,
    0x1000_03D5,
    0x1000_03D6,
];
/// The tabs, in page order.
pub const TAB_IDS: [u32; 6] = [
    0x1000_03EF,
    0x1000_03F0,
    0x1000_03F1,
    0x1000_03F2,
    0x1000_03F3,
    0x1000_03F4,
];
/// The shell's state for the first page (the others follow).
pub const MASTER_PAGE_FIRST_STATE: u32 = 0x1000_0025;

/// Heritage buttons whose click shows the profession, skills and town
/// tabs again.
const TAB_SHOW_BUTTONS: [u32; 10] = [
    0x1000_03BF,
    0x1000_03C1,
    0x1000_03C2,
    0x1000_03C3,
    0x1000_0590,
    0x1000_0591,
    0x1000_05A9,
    0x1000_05BF,
    0x1000_05C4,
    0x1000_05E8,
];
/// Heritage buttons whose click hides them.
const TAB_HIDE_BUTTONS: [u32; 2] = [0x1000_05C7, 0x1000_05C8];

/// The fixed-canvas owner number of this screen.
pub(crate) const CANVAS_OWNER: u64 = 3;

/// A wizard page.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord)]
pub enum Page {
    /// Heritage and gender.
    Heritage = 1,
    /// Profession and attributes.
    Profession = 2,
    /// Skills.
    Skills = 3,
    /// Appearance.
    Appearance = 4,
    /// Starting town.
    Town = 5,
    /// Summary and name.
    Summary = 6,
}

impl Page {
    const ALL: [Self; 6] = [
        Self::Heritage,
        Self::Profession,
        Self::Skills,
        Self::Appearance,
        Self::Town,
        Self::Summary,
    ];

    fn index(self) -> usize {
        self as usize - 1
    }

    fn offset(self, by: i32) -> Self {
        let i = (self as i32 + by).clamp(1, 6);
        Self::ALL[(i - 1) as usize]
    }
}

/// What the pages read: the snapshot, the creation choices and the
/// strings of table 0x23000002.
pub struct PageContext<'a> {
    /// The creation state.
    pub snapshot: &'a CreationSnapshot,
    /// The creation choices.
    pub options: &'a ChargenOptions,
    /// A string of table 0x23000002 by key.
    pub text: &'a dyn Fn(&str) -> Option<String>,
}

impl std::fmt::Debug for PageContext<'_> {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("PageContext")
            .field("revision", &self.snapshot.revision)
            .finish_non_exhaustive()
    }
}

/// A string of table 0x23000002 by key.
pub type TextLookup = Box<dyn Fn(&str) -> Option<String>>;

/// The wizard's dialog texts and string lookup.
pub struct CreationStrings {
    /// "Are you sure you want to exit character creation?"
    pub exit_warning: String,
    /// No name entered.
    pub no_name_warning: String,
    /// Attribute points left.
    pub credit_warning: String,
    /// Randomize everything?
    pub randomize_warning: String,
    /// The name is too long.
    pub name_too_long: String,
    /// A string of table 0x23000002 by key.
    pub text: TextLookup,
}

impl std::fmt::Debug for CreationStrings {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("CreationStrings")
            .field("exit_warning", &self.exit_warning)
            .finish_non_exhaustive()
    }
}

/// The look the 3-D preview shows and where.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct ChargenPreview {
    /// The viewport element it draws in (the appearance or summary page's).
    pub viewport: ElementId,
    /// The summary page's preview (else the appearance page's).
    pub summary: bool,
    /// Heritage.
    pub heritage: u32,
    /// Gender key.
    pub gender: u32,
    /// The look.
    pub appearance: CreationAppearance,
}

/// The string key of a refused create's message.
#[must_use]
pub fn rejection_key(code: u32) -> &'static str {
    match code {
        chargen_code::NAME_IN_USE => "ID_Character_Err_NameReserved",
        chargen_code::NAME_BANNED => "ID_Character_Err_NameBanned",
        chargen_code::ADMIN_PRIVILEGE_DENIED => "ID_Character_Err_NameAdminDenied",
        _ => "ID_Character_Err_NameDBDown",
    }
}

type Key = (Generation, u64);

fn key(s: &CreationSnapshot) -> Key {
    (s.generation, s.revision)
}

fn is_olthoi(heritage: u32) -> bool {
    heritage == H::OLTHOI.0 || heritage == H::OLTHOI_ACID.0
}

/// The character creation wizard. See the module documentation.
pub struct CreationScreen {
    root: ElementId,
    back: ElementId,
    next: ElementId,
    finish: ElementId,
    help: ElementId,
    exit: ElementId,
    random: ElementId,
    master_page: ElementId,
    pages: [ElementId; 6],
    tabs: [ElementId; 6],
    heritage: HeritagePage,
    profession: ProfessionPage,
    skills: SkillsPage,
    appearance: AppearancePage,
    town: TownPage,
    summary: SummaryPage,
    strings: CreationStrings,
    current: Page,
    active: bool,
    open: bool,
    refreshed: Option<Key>,
    snapshot: Option<CreationSnapshot>,
    options: Option<Arc<ChargenOptions>>,
    exit_dialog: u32,
    credit_dialog: u32,
    randomize_dialog: u32,
    no_name_dialog: u32,
    error_dialog: u32,
    name_too_long_dialog: u32,
    last_rejection: Option<CreationRejection>,
    finish_sent: Option<Key>,
    roll_sent: Option<Key>,
    random_source: Box<dyn ChargenRandom>,
    preview_requests: Vec<PreviewRequest>,
    swatch_sources: Option<Box<dyn SwatchSourcesDyn>>,
    swatch_textures: Option<Box<dyn SwatchTextures>>,
}

impl std::fmt::Debug for CreationScreen {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("CreationScreen")
            .field("current", &self.current)
            .field("open", &self.open)
            .finish_non_exhaustive()
    }
}

impl CreationScreen {
    /// Bind the imported wizard (root 0x100003CC) and add it to the screen
    /// root, hidden. `None` (and nothing mounted) when the shell, a page or
    /// a button is missing.
    pub fn bind(
        ui: &mut UiRoot,
        layout: &ImportedLayout,
        strings: CreationStrings,
    ) -> Option<Self> {
        let root = layout.root;
        if ui.element(root)?.dat_element_id != ROOT_ELEMENT_ID {
            return None;
        }
        layout.find(PROGRESS_BAR_ID)?;
        let button = |ui: &mut UiRoot, id| {
            let b = layout
                .find(id)
                .filter(|b| ui.widget::<Button>(*b).is_some())?;
            if let Some(w) = ui.widget_mut::<Button>(b) {
                w.listeners.click = true;
            }
            Some(b)
        };
        let back = button(ui, BACK_ID)?;
        let next = button(ui, NEXT_ID)?;
        let finish = button(ui, FINISH_ID)?;
        let help = button(ui, HELP_ID)?;
        let exit = button(ui, EXIT_ID)?;
        let random = button(ui, RANDOM_ID)?;
        let master_page = layout.find(MASTER_PAGE_ID)?;
        let mut pages = [root; 6];
        for (slot, id) in pages.iter_mut().zip(PAGE_IDS) {
            *slot = layout.find(id)?;
        }
        let mut tabs = [root; 6];
        for (slot, id) in tabs.iter_mut().zip(TAB_IDS) {
            *slot = button(ui, id)?;
        }
        if let Some(e) = ui.element_mut(root) {
            e.left = 0.0;
            e.top = 0.0;
            e.click_through = false;
            e.visible = false;
        }
        let heritage = HeritagePage::bind(ui, pages[0]);
        let profession = ProfessionPage::bind(ui, pages[1]);
        let skills = SkillsPage::bind(ui, pages[2]);
        let appearance = AppearancePage::bind(ui, pages[3]);
        // The preview starts framed as the page opens.
        let initial_zoom = appearance.zoom_request();
        let town = TownPage::bind(ui, pages[4]);
        let summary = SummaryPage::bind(ui, pages[5]);
        let screen = ui.root();
        ui.add_child(screen, root);
        Some(Self {
            root,
            back,
            next,
            finish,
            help,
            exit,
            random,
            master_page,
            pages,
            tabs,
            heritage,
            profession,
            skills,
            appearance,
            town,
            summary,
            strings,
            current: Page::Heritage,
            active: false,
            open: false,
            refreshed: None,
            snapshot: None,
            options: None,
            exit_dialog: 0,
            credit_dialog: 0,
            randomize_dialog: 0,
            no_name_dialog: 0,
            error_dialog: 0,
            name_too_long_dialog: 0,
            last_rejection: None,
            finish_sent: None,
            roll_sent: None,
            random_source: Box::new(SeededRandom::from_clock()),
            preview_requests: vec![initial_zoom],
            swatch_sources: None,
            swatch_textures: None,
        })
    }

    /// Paint the swatches from these sources (the dats' palettes) with this
    /// art.
    pub fn set_swatch_sources(
        &mut self,
        sources: Option<Box<dyn SwatchSourcesDyn>>,
        textures: Option<Box<dyn SwatchTextures>>,
    ) {
        self.swatch_sources = sources;
        self.swatch_textures = textures;
        self.refreshed = None;
    }

    /// Draw the random choices from `random` (tests).
    pub fn set_random(&mut self, random: Box<dyn ChargenRandom>) {
        self.random_source = random;
    }

    /// The wizard's root.
    #[must_use]
    pub fn root(&self) -> ElementId {
        self.root
    }

    /// Whether the wizard shows.
    #[must_use]
    pub fn is_open(&self) -> bool {
        self.open
    }

    /// The page shown.
    #[must_use]
    pub fn current_page(&self) -> Page {
        self.current
    }

    /// A shell button by its dat id.
    #[must_use]
    pub fn button(&self, dat_id: u32) -> Option<ElementId> {
        [
            (BACK_ID, self.back),
            (NEXT_ID, self.next),
            (FINISH_ID, self.finish),
            (HELP_ID, self.help),
            (EXIT_ID, self.exit),
            (RANDOM_ID, self.random),
        ]
        .into_iter()
        .chain(TAB_IDS.into_iter().zip(self.tabs))
        .find_map(|(id, e)| (id == dat_id).then_some(e))
    }

    /// A page's root.
    #[must_use]
    pub fn page_root(&self, page: Page) -> ElementId {
        self.pages[page.index()]
    }

    /// The heritage page.
    #[must_use]
    pub fn heritage_page(&self) -> &HeritagePage {
        &self.heritage
    }

    /// The profession page.
    #[must_use]
    pub fn profession_page(&self) -> &ProfessionPage {
        &self.profession
    }

    /// The skills page.
    #[must_use]
    pub fn skills_page(&self) -> &SkillsPage {
        &self.skills
    }

    /// The appearance page.
    #[must_use]
    pub fn appearance_page(&self) -> &AppearancePage {
        &self.appearance
    }

    /// The town page.
    #[must_use]
    pub fn town_page(&self) -> &TownPage {
        &self.town
    }

    /// The summary page.
    #[must_use]
    pub fn summary_page(&self) -> &SummaryPage {
        &self.summary
    }

    /// The open dialogs' contexts: exit, credits left, randomize, no
    /// name, refused name, name too long (0 for none).
    #[must_use]
    pub fn dialog_contexts(&self) -> [u32; 6] {
        [
            self.exit_dialog,
            self.credit_dialog,
            self.randomize_dialog,
            self.no_name_dialog,
            self.error_dialog,
            self.name_too_long_dialog,
        ]
    }

    /// What the 3-D preview should show, when the appearance or summary
    /// page shows a chosen heritage and gender. (No renderer draws it yet:
    /// the UI viewport has no scene path.)
    #[must_use]
    pub fn preview(&self) -> Option<ChargenPreview> {
        if !self.open {
            return None;
        }
        let viewport = match self.current {
            Page::Appearance => self.appearance.viewport()?,
            Page::Summary => self.summary.viewport()?,
            _ => return None,
        };
        let s = self.snapshot.as_ref()?;
        (s.heritage_id != 0 && s.gender_key != 0).then_some(ChargenPreview {
            viewport,
            summary: self.current == Page::Summary,
            heritage: s.heritage_id,
            gender: s.gender_key,
            appearance: s.appearance,
        })
    }

    /// The rotate and zoom requests for the 3-D preview since the last
    /// call.
    pub fn take_preview_requests(&mut self) -> Vec<PreviewRequest> {
        std::mem::take(&mut self.preview_requests)
    }

    /// Open the wizard on the heritage page with a freshly rolled
    /// character.
    pub fn open(&mut self, ui: &mut UiRoot) -> Vec<CreationCommand> {
        self.open = true;
        let size = ui
            .element(self.root)
            .map_or((800.0, 600.0), |e| (e.width, e.height));
        let _ = ui.declare_fixed_canvas(CANVAS_OWNER, size);
        // The roll's answer turns the gender over (see `update`).
        self.roll_sent = self.snapshot.as_ref().map(key);
        let _ = self.apply_progress_state(ui, Page::Heritage);
        vec![CreationCommand::RandomizeCharacter]
    }

    fn close(&mut self, ui: &mut UiRoot) {
        if !self.open {
            return;
        }
        self.open = false;
        ui.set_visible(self.root, false);
        ui.revoke_fixed_canvas(CANVAS_OWNER);
    }

    /// Follow character creation. `refused`: the session refused a
    /// request of this window since the last update.
    pub fn update(
        &mut self,
        ui: &mut UiRoot,
        dialogs: &mut Dialogs<'_>,
        snapshot: Option<&CreationSnapshot>,
        options: Option<&Arc<ChargenOptions>>,
        refused: bool,
        templates: &mut dyn RowTemplates,
    ) -> Vec<CreationCommand> {
        let mut commands = Vec::new();
        if refused {
            self.roll_sent = None;
        }
        if let Some(o) = options {
            self.options = Some(Arc::clone(o));
        }
        let Some(snapshot) = snapshot.filter(|s| s.is_active) else {
            self.snapshot = snapshot.cloned();
            self.deactivate(ui, dialogs);
            return commands;
        };
        self.active = true;
        if self.open {
            ui.set_visible(self.root, true);
            ui.bring_to_front(self.root);
        } else if ui.element(self.root).is_some_and(|e| e.visible) {
            ui.set_visible(self.root, false);
        }
        let current = key(snapshot);
        let changed = self.snapshot.as_ref().map(key) != Some(current);
        self.snapshot = Some(snapshot.clone());
        if changed {
            // The opening roll arrived: the appearance page starts on the
            // face choice, which takes the rolled headgear off (kept for
            // the clothes choice), then the gender turns over.
            if self.roll_sent.is_some_and(|sent| sent != current) {
                self.roll_sent = None;
                let (more, zoom) =
                    self.appearance
                        .set_choice(ui, Choice::Face, snapshot, self.options.as_deref());
                commands.extend(more);
                self.preview_requests.extend(zoom);
                match snapshot.gender_key {
                    1 => commands.push(CreationCommand::SelectGender(2)),
                    2 => commands.push(CreationCommand::SelectGender(1)),
                    _ => {}
                }
            }
            if self.finish_sent.is_some_and(|sent| sent != current) {
                self.finish_sent = None;
                self.after_finish(ui, dialogs, snapshot);
            }
        }
        if self.refreshed != Some(current) {
            self.refresh_pages(ui, templates);
            self.refreshed = Some(current);
        }
        self.reconcile_rejection(ui, dialogs, snapshot);
        commands.extend(self.follow_name_focus(ui, dialogs));
        commands
    }

    fn refresh_pages(&mut self, ui: &mut UiRoot, templates: &mut dyn RowTemplates) {
        let (Some(snapshot), Some(options)) = (self.snapshot.clone(), self.options.clone()) else {
            return;
        };
        let text: &dyn Fn(&str) -> Option<String> = &self.strings.text;
        let ctx = PageContext {
            snapshot: &snapshot,
            options: &options,
            text,
        };
        self.heritage.refresh(ui, &ctx);
        self.profession.refresh(ui, &ctx);
        self.skills.refresh(ui, &ctx, templates);
        self.town.refresh(ui, &ctx);
        let textures = self
            .swatch_textures
            .as_deref_mut()
            .map(|t| t as &mut dyn SwatchTextures);
        self.appearance
            .refresh(ui, &ctx, self.swatch_sources.as_deref(), textures);
        self.summary.refresh(ui, &ctx, templates);
    }

    fn follow_name_focus(
        &mut self,
        ui: &mut UiRoot,
        dialogs: &mut Dialogs<'_>,
    ) -> Vec<CreationCommand> {
        match self.summary.follow_focus(ui) {
            Some(commit) => self.name_commit(ui, dialogs, commit),
            None => Vec::new(),
        }
    }

    fn name_commit(
        &mut self,
        ui: &mut UiRoot,
        dialogs: &mut Dialogs<'_>,
        commit: NameCommit,
    ) -> Vec<CreationCommand> {
        match commit {
            NameCommit::Set(c) => vec![c],
            NameCommit::TooLong => {
                if self.name_too_long_dialog == 0 {
                    let text = self.strings.name_too_long.clone();
                    self.name_too_long_dialog = dialogs.make(ui, DialogData::message(&text));
                }
                Vec::new()
            }
        }
    }

    /// A finish was answered (locally): no name and points left ask the
    /// player; an accepted one waits for the server.
    fn after_finish(
        &mut self,
        ui: &mut UiRoot,
        dialogs: &mut Dialogs<'_>,
        snapshot: &CreationSnapshot,
    ) {
        let refusal = snapshot.last_local_refusal;
        if !refusal.any() {
            return;
        }
        if refusal.no_name {
            if self.no_name_dialog == 0 {
                let text = self.strings.no_name_warning.clone();
                self.no_name_dialog = dialogs.make(ui, DialogData::message(&text));
            }
        } else if refusal.attribute_credits_unspent && self.credit_dialog == 0 {
            let text = self.strings.credit_warning.clone();
            self.credit_dialog = dialogs.make(ui, DialogData::confirmation(&text));
        }
    }

    fn reconcile_rejection(
        &mut self,
        ui: &mut UiRoot,
        dialogs: &mut Dialogs<'_>,
        snapshot: &CreationSnapshot,
    ) {
        let Some(rejection) = &snapshot.last_rejection else {
            self.last_rejection = None;
            return;
        };
        if self.last_rejection.as_ref() == Some(rejection) {
            return;
        }
        self.last_rejection = Some(rejection.clone());
        if self.error_dialog != 0 {
            return;
        }
        if let Some(message) = (self.strings.text)(rejection_key(rejection.raw_code)) {
            self.error_dialog = dialogs.make(ui, DialogData::message(&message));
        }
    }

    /// A message from inside the wizard: the creation requests it makes.
    /// `None` when it is not the wizard's.
    pub fn on_message(
        &mut self,
        ui: &mut UiRoot,
        dialogs: &mut Dialogs<'_>,
        element: ElementId,
        message: &ElementMessage,
    ) -> Option<Vec<CreationCommand>> {
        if !self.open || !ui.is_within(Some(element), self.root) {
            return None;
        }
        // A name typed and left by this press is committed first.
        let mut commands = self.follow_name_focus(ui, dialogs);
        if let ElementMessage::Clicked { .. } = message {
            if let Some(i) = self.tabs.iter().position(|t| *t == element) {
                commands.extend(self.apply_progress_state(ui, Page::ALL[i]));
                return Some(commands);
            }
            if element == self.back {
                if self.current <= Page::Heritage {
                    self.ask_exit(ui, dialogs);
                } else {
                    commands.extend(self.apply_progress_state(ui, self.current.offset(-1)));
                }
                return Some(commands);
            }
            if element == self.next {
                if self.current < Page::Summary {
                    commands.extend(self.apply_progress_state(ui, self.current.offset(1)));
                }
                return Some(commands);
            }
            if element == self.finish {
                if self.current == Page::Summary {
                    commands.push(self.send_finish(false));
                }
                return Some(commands);
            }
            if element == self.exit {
                self.ask_exit(ui, dialogs);
                return Some(commands);
            }
            if element == self.random {
                commands.extend(self.on_random(ui, dialogs));
                return Some(commands);
            }
            if element == self.help {
                return Some(commands);
            }
        }
        let (Some(snapshot), Some(options)) = (self.snapshot.clone(), self.options.clone()) else {
            return Some(commands);
        };
        let text: &dyn Fn(&str) -> Option<String> = &self.strings.text;
        let ctx = PageContext {
            snapshot: &snapshot,
            options: &options,
            text,
        };
        if let Some((button_id, heritage)) = self.heritage.clicked(element, message) {
            self.heritage_tab_restore(ui, button_id);
            commands.push(CreationCommand::SelectHeritage(heritage));
        } else if let Some(c) = self.profession.on_message(element, message, &ctx) {
            commands.extend(c);
        } else if let Some(c) = self.skills.on_message(ui, element, message, &ctx) {
            commands.extend(c);
        } else if let Some((c, preview)) = self.appearance.on_message(ui, element, message, &ctx) {
            commands.extend(c);
            self.preview_requests.extend(preview);
            // The page's own choice changed what the swatches show.
            let textures = self
                .swatch_textures
                .as_deref_mut()
                .map(|t| t as &mut dyn SwatchTextures);
            self.appearance
                .refresh_colors(ui, &ctx, self.swatch_sources.as_deref(), textures);
        } else if let Some(c) = self.town.on_message(element, message) {
            commands.extend(c);
        } else if let Some(commit) = self.summary.on_message(ui, element, message) {
            commands.extend(self.name_commit(ui, dialogs, commit));
        }
        Some(commands)
    }

    /// The player answered dialog `context`: the creation requests it
    /// makes. `None` when the dialog is not the wizard's.
    pub fn on_dialog(
        &mut self,
        ui: &mut UiRoot,
        context: u32,
        result: &DialogResult,
    ) -> Option<Vec<CreationCommand>> {
        if context == 0 {
            return None;
        }
        let yes = *result == DialogResult::Confirmed(true);
        if context == self.exit_dialog {
            self.exit_dialog = 0;
            if yes {
                self.close(ui);
            }
            return Some(Vec::new());
        }
        if context == self.credit_dialog {
            self.credit_dialog = 0;
            return Some(if yes {
                vec![self.send_finish(true)]
            } else {
                Vec::new()
            });
        }
        if context == self.randomize_dialog {
            self.randomize_dialog = 0;
            return Some(if yes {
                vec![CreationCommand::RandomizeCharacter]
            } else {
                Vec::new()
            });
        }
        if context == self.no_name_dialog {
            self.no_name_dialog = 0;
            return Some(Vec::new());
        }
        if context == self.name_too_long_dialog {
            self.name_too_long_dialog = 0;
            return Some(Vec::new());
        }
        if context == self.error_dialog {
            self.error_dialog = 0;
            return Some(vec![CreationCommand::AcknowledgeRejection]);
        }
        None
    }

    fn send_finish(&mut self, confirm_unspent_credits: bool) -> CreationCommand {
        self.finish_sent = self.snapshot.as_ref().map(key);
        CreationCommand::Finish {
            confirm_unspent_credits,
        }
    }

    fn ask_exit(&mut self, ui: &mut UiRoot, dialogs: &mut Dialogs<'_>) {
        if self.exit_dialog == 0 {
            let text = self.strings.exit_warning.clone();
            self.exit_dialog = dialogs.make(ui, DialogData::confirmation(&text));
        }
    }

    fn on_random(&mut self, ui: &mut UiRoot, dialogs: &mut Dialogs<'_>) -> Vec<CreationCommand> {
        let (Some(snapshot), Some(options)) = (self.snapshot.clone(), self.options.clone()) else {
            return Vec::new();
        };
        let text: &dyn Fn(&str) -> Option<String> = &self.strings.text;
        let ctx = PageContext {
            snapshot: &snapshot,
            options: &options,
            text,
        };
        let random = &mut self.random_source;
        let mut draw = |max: u32| random.next(max);
        match self.current {
            Page::Heritage => HeritagePage::randomize(&ctx, &mut draw)
                .into_iter()
                .collect(),
            Page::Profession => ProfessionPage::randomize(&ctx, &mut draw)
                .into_iter()
                .collect(),
            Page::Appearance => {
                let command = self.appearance.randomize();
                let (more, zoom) = self.appearance.on_page_update(ui, &ctx);
                self.preview_requests.extend(zoom);
                let mut commands = vec![command];
                commands.extend(more);
                commands
            }
            Page::Town => TownPage::randomize(&ctx, &mut draw).into_iter().collect(),
            Page::Summary => {
                if self.randomize_dialog == 0 {
                    let text = self.strings.randomize_warning.clone();
                    self.randomize_dialog = dialogs.make(ui, DialogData::confirmation(&text));
                }
                Vec::new()
            }
            Page::Skills => Vec::new(),
        }
    }

    fn heritage_tab_restore(&self, ui: &mut UiRoot, button_id: u32) {
        let visible = if TAB_SHOW_BUTTONS.contains(&button_id) {
            true
        } else if TAB_HIDE_BUTTONS.contains(&button_id) {
            false
        } else {
            return;
        };
        for page in [Page::Profession, Page::Skills, Page::Town] {
            ui.set_visible(self.tabs[page.index()], visible);
        }
    }

    /// Show page `target` (an Olthoi skips the profession, skills and town
    /// pages in the direction of travel). Returns the requests showing the
    /// page makes.
    pub fn apply_progress_state(&mut self, ui: &mut UiRoot, target: Page) -> Vec<CreationCommand> {
        for page in self.pages {
            ui.set_visible(page, false);
        }
        ui.set_visible(self.next, true);
        ui.set_visible(self.finish, false);
        let previous = self.current;
        self.current = target;
        for tab in self.tabs {
            ui.with::<Button, _>(tab, |b, ui| b.set_selected(ui, tab, false));
        }
        let heritage = self.snapshot.as_ref().map_or(0, |s| s.heritage_id);
        let olthoi = is_olthoi(heritage);
        for page in [Page::Profession, Page::Skills, Page::Town] {
            ui.set_visible(self.tabs[page.index()], !olthoi);
        }
        if olthoi {
            self.current = if self.current < previous {
                match self.current {
                    Page::Profession | Page::Skills => Page::Heritage,
                    Page::Town => Page::Appearance,
                    other => other,
                }
            } else {
                match self.current {
                    Page::Profession | Page::Skills => Page::Appearance,
                    Page::Town => Page::Summary,
                    other => other,
                }
            };
        }
        ui.set_retail_state(
            self.master_page,
            MASTER_PAGE_FIRST_STATE + self.current as u32 - 1,
        );
        let i = self.current.index();
        ui.set_visible(self.pages[i], true);
        let mut commands = Vec::new();
        if self.current == Page::Appearance
            && let (Some(snapshot), Some(options)) = (self.snapshot.clone(), self.options.clone())
        {
            let text: &dyn Fn(&str) -> Option<String> = &self.strings.text;
            let ctx = PageContext {
                snapshot: &snapshot,
                options: &options,
                text,
            };
            let (more, zoom) = self.appearance.on_page_update(ui, &ctx);
            commands = more;
            self.preview_requests.extend(zoom);
        }
        let tab = self.tabs[i];
        ui.with::<Button, _>(tab, |b, ui| b.set_selected(ui, tab, true));
        if self.current == Page::Summary {
            ui.set_visible(self.next, false);
            ui.set_visible(self.finish, true);
        }
        ui.set_enabled(self.random, self.current != Page::Skills);
        ui.set_enabled(self.finish, self.current == Page::Summary);
        self.refreshed = None;
        commands
    }

    fn deactivate(&mut self, ui: &mut UiRoot, dialogs: &mut Dialogs<'_>) {
        if self.active {
            self.active = false;
            self.close(ui);
        }
        for context in [
            &mut self.exit_dialog,
            &mut self.credit_dialog,
            &mut self.randomize_dialog,
            &mut self.no_name_dialog,
            &mut self.error_dialog,
            &mut self.name_too_long_dialog,
        ] {
            dialogs.close(ui, context);
        }
        self.finish_sent = None;
        self.roll_sent = None;
    }

    /// Take the wizard down (the window goes).
    pub fn dispose(&mut self, ui: &mut UiRoot, dialogs: &mut Dialogs<'_>) {
        self.deactivate(ui, dialogs);
        ui.destroy(self.root);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_refused_name_is_worded_by_its_code() {
        // OpenAC `CharacterCreationUiController.ReconcileDialogs`.
        assert_eq!(
            rejection_key(chargen_code::NAME_IN_USE),
            "ID_Character_Err_NameReserved"
        );
        assert_eq!(
            rejection_key(chargen_code::NAME_BANNED),
            "ID_Character_Err_NameBanned"
        );
        assert_eq!(
            rejection_key(chargen_code::ADMIN_PRIVILEGE_DENIED),
            "ID_Character_Err_NameAdminDenied"
        );
        assert_eq!(
            rejection_key(chargen_code::DATABASE_DOWN),
            "ID_Character_Err_NameDBDown"
        );
    }

    #[test]
    fn pages_step_within_the_wizard() {
        assert_eq!(Page::Heritage.offset(-1), Page::Heritage);
        assert_eq!(Page::Heritage.offset(1), Page::Profession);
        assert_eq!(Page::Summary.offset(1), Page::Summary);
    }
}
