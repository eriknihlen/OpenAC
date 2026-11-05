//! The pre-game screens on the authored 800x600 canvas: connecting,
//! character selection, the credits and the character creation wizard,
//! with their dialogs from the dialog catalog.
//!
//! The screens show what the session's pre-game state says
//! ([`PreGameState`], filled from the presentation stream) and answer the
//! player with session commands; they never change session state
//! themselves. One [`PreGameScreens`] holds them all: the window feeds it
//! every frame and hands it the messages of the elements inside it.

pub mod characters;
pub mod chargen;
pub mod connection;
pub mod credits;
pub mod rich_text;

use std::collections::HashMap;
use std::sync::Arc;
use std::time::Instant;

use ac_core::chargen::ChargenOptions;
use ac_dat::DatCollection;
use ac_runtime::session::creation::CreationSnapshot;
use ac_runtime::session::pregame::{ConnectionView, PreGameEvent};
use ac_runtime::session::selection::SelectionSnapshot;
use ac_runtime::session::{LifecycleCommand, SessionCommand};

use self::characters::{CharacterScreen, CharacterStrings};
use self::chargen::appearance::{SwatchSourcesDyn, SwatchTextures};
use self::chargen::{CreationScreen, CreationStrings};
use self::connection::ConnectionScreen;
use self::credits::{CreditsResources, CreditsScreen};
use crate::content::DatContent;
use crate::draw::SpriteSizes;
use crate::font::{DEFAULT_FONT_ID, UiFont};
use crate::import::{ImportedLayout, LayoutSource, import_infos_rooted, import_rooted};
use crate::panels::dialog_factory::{
    DialogData, DialogFactory, DialogType, DialogView, MakeLayout, property,
};
use crate::panels::options::chat_dat::resolve_data_id;
use crate::panels::{DatRowTemplates, PanelOutput};
use crate::root::{ElementMessage, UiRoot};
use crate::strings::{PLAYER_VARIABLE, StringResolver, compute_hash};
use crate::tree::ElementId;

/// The interface enumeration category the screens are listed under.
pub const SCREEN_ENUM_CATEGORY: u32 = 5;
/// The screens' string table.
pub const SCREEN_STRING_TABLE: u32 = 0x2300_0002;
/// The general string table ("Please Wait").
pub const GENERAL_STRING_TABLE: u32 = 0x2300_0001;

/// The latest pre-game state the session sent the window.
#[derive(Debug, Clone, Default)]
pub struct PreGameState {
    /// The connection.
    pub connection: Option<ConnectionView>,
    /// Character selection.
    pub selection: Option<SelectionSnapshot>,
    /// Character creation.
    pub creation: Option<CreationSnapshot>,
    /// The creation choices.
    pub options: Option<Arc<ChargenOptions>>,
    /// Refusals of the window's requests not yet taken.
    pub refusals: Vec<String>,
}

impl PreGameState {
    /// Keep one message from the session.
    pub fn apply(&mut self, event: PreGameEvent) {
        match event {
            PreGameEvent::Connection(v) => self.connection = Some(v),
            PreGameEvent::Selection(s) => self.selection = Some(*s),
            PreGameEvent::Creation(c) => self.creation = Some(*c),
            PreGameEvent::Options(o) => self.options = Some(o),
            PreGameEvent::Refused(text) => self.refusals.push(text),
        }
    }
}

/// What a screen asks the rest of the pre-game screens or the session.
#[derive(Debug, Clone)]
pub enum PreGameRequest {
    /// Send this to the session.
    Session(SessionCommand),
    /// Open the creation wizard.
    OpenCreation,
    /// Show the credits.
    OpenCredits,
}

/// What the player answered a pre-game dialog.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum DialogResult {
    /// A confirmation: yes (true) or no.
    Confirmed(bool),
    /// A text entry: the text on accept, empty on reject.
    Text(String),
    /// A message was dismissed.
    Dismissed,
}

impl DialogResult {
    /// The answer a closed dialog's data carries.
    fn of(data: &DialogData) -> Self {
        match data.kind() {
            Some(DialogType::ConfirmationTextInput) => Self::Text(
                data.get_str(property::TEXT_INPUT_RESULT)
                    .unwrap_or_default()
                    .to_owned(),
            ),
            Some(DialogType::Confirmation) => {
                Self::Confirmed(data.get_bool(property::CONFIRMATION_RESULT))
            }
            _ => Self::Dismissed,
        }
    }
}

/// The dialogs as a screen uses them (on the retail dialog factory's
/// default queue): make one, close one by its context (clearing it), make
/// one unless it is open. A dialog closed in code reports nothing; only
/// the player's answer does (OpenAC closes with the callback suppressed).
pub struct Dialogs<'a> {
    factory: &'a mut DialogFactory<()>,
    make: MakeLayout<'a>,
}

impl std::fmt::Debug for Dialogs<'_> {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("Dialogs")
            .field("factory", &self.factory)
            .finish_non_exhaustive()
    }
}

impl Dialogs<'_> {
    /// Show the dialog of `data`; its context.
    pub fn make(&mut self, ui: &mut UiRoot, data: DialogData) -> u32 {
        self.factory.make(ui, data, (), &mut *self.make)
    }

    /// Close dialog `*context` (if any) and clear it; the next waiting
    /// dialog opens.
    pub fn close(&mut self, ui: &mut UiRoot, context: &mut u32) {
        let c = std::mem::take(context);
        if c != 0 && self.factory.close(ui, c) {
            drop(self.factory.take_closed());
            self.factory.settle(ui, &mut *self.make);
        }
    }

    /// Show the dialog of `data` unless `*context` is one already.
    pub fn ensure(&mut self, ui: &mut UiRoot, context: &mut u32, data: DialogData) {
        if *context == 0 {
            *context = self.make(ui, data);
        }
    }
}

/// The catalog layout of a dialog kind.
fn dialog_layout(
    ui: &mut UiRoot,
    layouts: &dyn LayoutSource,
    catalog: Option<u32>,
    kind: DialogType,
) -> Option<ImportedLayout> {
    import_rooted(
        ui,
        layouts,
        catalog?,
        kind.root_element_id(),
        layouts.font(DEFAULT_FONT_ID),
    )
}

/// Where the screens' layouts, fonts and sprite sizes come from.
#[derive(Clone, Copy)]
pub struct PreGameAssets<'a> {
    /// Layouts (and the fonts and strings they name).
    pub layouts: &'a dyn LayoutSource,
    /// Sprite sizes (the credits' pictures).
    pub sprites: &'a dyn SpriteSizes,
}

impl std::fmt::Debug for PreGameAssets<'_> {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("PreGameAssets").finish_non_exhaustive()
    }
}

impl<'a> PreGameAssets<'a> {
    /// The assets of the dats.
    #[must_use]
    pub fn of(content: &'a DatContent) -> Self {
        Self {
            layouts: content,
            sprites: content,
        }
    }
}

/// The pre-game screens. See the module documentation.
#[derive(Debug)]
pub struct PreGameScreens {
    connection: Option<ConnectionScreen>,
    characters: Option<CharacterScreen>,
    creation: Option<CreationScreen>,
    credits: Option<CreditsScreen>,
    dialogs: DialogFactory<()>,
    catalog: Option<u32>,
    epoch: Instant,
}

/// A screen's layout, rooted at `root`, from the interface enumeration.
fn screen_layout(
    ui: &mut UiRoot,
    content: &DatContent,
    value: u32,
    root: u32,
) -> Result<(u32, ImportedLayout), String> {
    let id = resolve_data_id(content.dats(), value, SCREEN_ENUM_CATEGORY);
    if id == 0 {
        return Err(format!("enumeration entry {value:#010x} names no layout"));
    }
    import_rooted(ui, content, id, root, content.font(DEFAULT_FONT_ID))
        .map(|l| (id, l))
        .ok_or_else(|| format!("layout {id:#010x} has no element {root:#010x}"))
}

impl PreGameScreens {
    /// No screens (tests build them one by one).
    #[must_use]
    pub fn empty(catalog: Option<u32>) -> Self {
        Self {
            connection: None,
            characters: None,
            creation: None,
            credits: None,
            dialogs: DialogFactory::new(),
            catalog,
            epoch: Instant::now(),
        }
    }

    /// Import and bind every screen from the dats, hidden. A screen whose
    /// layout or strings cannot be read is left out and named in the
    /// notices. `version` is the client version line on the selection
    /// screen; `swatches` paints the appearance page's colour spots.
    pub fn mount(
        ui: &mut UiRoot,
        content: &DatContent,
        version: &str,
        swatches: Option<(Box<dyn SwatchSourcesDyn>, Box<dyn SwatchTextures>)>,
    ) -> (Self, Vec<String>) {
        let mut notices = Vec::new();
        let dats = Arc::clone(content.dats());
        let mut screens = Self::empty(crate::panels::dialog::catalog_layout_id(&dats));
        if screens.catalog.is_none() {
            notices.push("pre-game: the dialog catalog is not in the dats".to_owned());
        }
        let strings = owned_strings(&dats);
        let text = |table: u32, key: &str| strings.resolve(table, compute_hash(key), 0);

        match screen_layout(
            ui,
            content,
            connection::ROOT_ENUM,
            connection::ROOT_ELEMENT_ID,
        )
        .and_then(|(_, layout)| {
            let checking = text(SCREEN_STRING_TABLE, "ID_DataPatch_Interrogation");
            let done = text(SCREEN_STRING_TABLE, "ID_DataPatch_PatchingDone");
            let (Some(checking), Some(done)) = (checking, done) else {
                ui.destroy(layout.root);
                return Err("its data-patch strings are missing".to_owned());
            };
            ConnectionScreen::bind(ui, &layout, checking, done).ok_or_else(|| {
                ui.destroy(layout.root);
                "its meters, texts or cancel button are missing".to_owned()
            })
        }) {
            Ok(s) => screens.connection = Some(s),
            Err(e) => notices.push(format!("pre-game: connecting screen: {e}")),
        }

        match screen_layout(
            ui,
            content,
            characters::ROOT_ENUM,
            characters::ROOT_ELEMENT_ID,
        )
        .and_then(|(_, layout)| {
            let Some(strings) = character_strings(&dats) else {
                ui.destroy(layout.root);
                return Err("its dialog strings are missing".to_owned());
            };
            CharacterScreen::bind(ui, &layout, strings, version).ok_or_else(|| {
                ui.destroy(layout.root);
                "its list, world text or buttons are missing".to_owned()
            })
        }) {
            Ok(s) => screens.characters = Some(s),
            Err(e) => notices.push(format!("pre-game: character selection: {e}")),
        }

        match screen_layout(ui, content, chargen::ROOT_ENUM, chargen::ROOT_ELEMENT_ID).and_then(
            |(_, layout)| {
                let Some(strings) = creation_strings(&dats) else {
                    ui.destroy(layout.root);
                    return Err("its dialog strings are missing".to_owned());
                };
                CreationScreen::bind(ui, &layout, strings).ok_or_else(|| {
                    ui.destroy(layout.root);
                    "its shell, pages or buttons are missing".to_owned()
                })
            },
        ) {
            Ok(mut s) => {
                if let Some((sources, textures)) = swatches {
                    s.set_swatch_sources(Some(sources), Some(textures));
                }
                screens.creation = Some(s);
            }
            Err(e) => notices.push(format!("pre-game: character creation: {e}")),
        }

        match credits_screen(ui, content, &strings) {
            Ok(s) => screens.credits = Some(s),
            Err(e) => notices.push(format!("pre-game: credits: {e}")),
        }
        (screens, notices)
    }

    /// Use these screens (tests).
    pub fn set_screens(
        &mut self,
        connection: Option<ConnectionScreen>,
        characters: Option<CharacterScreen>,
        creation: Option<CreationScreen>,
        credits: Option<CreditsScreen>,
    ) {
        self.connection = connection;
        self.characters = characters;
        self.creation = creation;
        self.credits = credits;
    }

    /// The connecting screen.
    #[must_use]
    pub fn connection(&self) -> Option<&ConnectionScreen> {
        self.connection.as_ref()
    }

    /// The character selection screen.
    #[must_use]
    pub fn characters(&self) -> Option<&CharacterScreen> {
        self.characters.as_ref()
    }

    /// The creation wizard.
    #[must_use]
    pub fn creation(&self) -> Option<&CreationScreen> {
        self.creation.as_ref()
    }

    /// The creation wizard, to take its preview requests.
    pub fn creation_mut(&mut self) -> Option<&mut CreationScreen> {
        self.creation.as_mut()
    }

    /// The credits.
    #[must_use]
    pub fn credits(&self) -> Option<&CreditsScreen> {
        self.credits.as_ref()
    }

    /// The dialogs.
    #[must_use]
    pub fn dialogs(&self) -> &DialogFactory<()> {
        &self.dialogs
    }

    /// The dialog in front.
    #[must_use]
    pub fn open_dialog(&self) -> Option<&DialogView> {
        let context = *self.dialogs.open_contexts().last()?;
        self.dialogs.view(context)
    }

    /// Whether a screen (or one of its dialogs) shows: the window's own
    /// hotkeys and chat entry stand aside.
    #[must_use]
    pub fn is_active(&self, ui: &UiRoot) -> bool {
        self.connection.as_ref().is_some_and(|c| c.is_visible(ui))
            || self
                .characters
                .as_ref()
                .is_some_and(CharacterScreen::is_active)
            || self.creation.as_ref().is_some_and(CreationScreen::is_open)
            || self.credits.as_ref().is_some_and(CreditsScreen::is_active)
            || self.dialogs.is_open()
    }

    /// Whether `element` is inside a screen or one of its dialogs.
    #[must_use]
    pub fn owns(&self, ui: &UiRoot, element: ElementId) -> bool {
        let roots = [
            self.connection.as_ref().map(ConnectionScreen::root),
            self.characters.as_ref().map(CharacterScreen::root),
            self.creation.as_ref().map(CreationScreen::root),
            self.credits.as_ref().map(CreditsScreen::surface),
            self.open_dialog().map(DialogView::root),
        ];
        roots
            .into_iter()
            .flatten()
            .any(|r| ui.is_within(Some(element), r))
    }

    fn seconds(&self, now: Instant) -> f64 {
        now.saturating_duration_since(self.epoch).as_secs_f64()
    }

    /// One frame at `now`: every screen follows the state; the requests
    /// they make for the session come back.
    pub fn update(
        &mut self,
        ui: &mut UiRoot,
        assets: PreGameAssets<'_>,
        state: &mut PreGameState,
        now: Instant,
    ) -> Vec<PanelOutput> {
        let refused = !std::mem::take(&mut state.refusals).is_empty();
        let seconds = self.seconds(now);
        let catalog = self.catalog;
        let mut maker =
            |ui: &mut UiRoot, kind: DialogType| dialog_layout(ui, assets.layouts, catalog, kind);
        let mut templates =
            DatRowTemplates::new(assets.layouts, assets.layouts.font(DEFAULT_FONT_ID));
        let mut dialogs = Dialogs {
            factory: &mut self.dialogs,
            make: &mut maker,
        };
        let mut outputs = Vec::new();
        if let Some(c) = self.connection.as_mut() {
            c.update(ui, state.connection.as_ref(), now);
        }
        let blocked = self.connection.as_ref().is_some_and(|c| c.is_visible(ui));
        let direct_launch = state.connection.as_ref().is_some_and(|c| c.direct_launch);
        if let Some(c) = self.characters.as_mut() {
            c.update(
                ui,
                &mut dialogs,
                state.selection.as_ref(),
                direct_launch,
                blocked,
                refused,
                &mut templates,
            );
        }
        if let Some(c) = self.credits.as_mut()
            && c.tick(ui, &mut dialogs, seconds, assets.sprites)
            && let Some(characters) = self.characters.as_mut()
        {
            characters.set_presentation_suppressed(ui, &mut dialogs, false);
        }
        if let Some(c) = self.creation.as_mut() {
            let commands = c.update(
                ui,
                &mut dialogs,
                state.creation.as_ref(),
                state.options.as_ref(),
                refused,
                &mut templates,
            );
            outputs.extend(commands.into_iter().map(creation_output));
        }
        self.dialogs.tick(ui, &mut maker);
        // A dialog the catalog cannot make waits and is retried every
        // frame; a missing catalog is reported once, at mount.
        drop(self.dialogs.take_failures());
        outputs
    }

    /// A message from an element: what it asks of the session. `None`
    /// when the element is not inside a screen or one of its dialogs.
    pub fn on_message(
        &mut self,
        ui: &mut UiRoot,
        assets: PreGameAssets<'_>,
        element: ElementId,
        message: &ElementMessage,
        now: Instant,
    ) -> Option<Vec<PanelOutput>> {
        if !self.owns(ui, element) {
            return None;
        }
        let seconds = self.seconds(now);
        let catalog = self.catalog;
        let mut maker =
            |ui: &mut UiRoot, kind: DialogType| dialog_layout(ui, assets.layouts, catalog, kind);
        if self.dialogs.handle(ui, element, message) {
            // The owner acts on the answer before the queue moves on.
            let mut outputs = Vec::new();
            for closed in self.dialogs.take_closed() {
                let result = DialogResult::of(&closed.data);
                outputs.extend(self.on_dialog(ui, closed.context, &result));
            }
            self.dialogs.settle(ui, &mut maker);
            return Some(outputs);
        }
        let mut dialogs = Dialogs {
            factory: &mut self.dialogs,
            make: &mut maker,
        };
        if let Some(c) = &self.connection
            && element == c.cancel_button()
        {
            return Some(match message {
                ElementMessage::Clicked { .. } => {
                    vec![PanelOutput::Session(SessionCommand::Shutdown)]
                }
                _ => Vec::new(),
            });
        }
        if let Some(c) = self.credits.as_mut()
            && c.on_message(ui, &mut dialogs, element, message)
        {
            return Some(Vec::new());
        }
        if let Some(c) = self.creation.as_mut()
            && let Some(commands) = c.on_message(ui, &mut dialogs, element, message)
        {
            return Some(commands.into_iter().map(creation_output).collect());
        }
        let requests = self
            .characters
            .as_mut()
            .and_then(|c| c.on_message(ui, &mut dialogs, element, message))
            .unwrap_or_default();
        let mut outputs = Vec::new();
        for request in requests {
            match request {
                PreGameRequest::Session(command) => outputs.push(PanelOutput::Session(command)),
                PreGameRequest::OpenCreation => {
                    if let Some(c) = self.creation.as_mut() {
                        outputs.extend(c.open(ui).into_iter().map(creation_output));
                    }
                }
                PreGameRequest::OpenCredits => {
                    if let Some(credits) = self.credits.as_mut()
                        && let Some(characters) = self.characters.as_mut()
                    {
                        characters.set_presentation_suppressed(ui, &mut dialogs, true);
                        credits.activate(ui, &mut dialogs, seconds, assets.sprites);
                    }
                }
            }
        }
        Some(outputs)
    }

    fn on_dialog(
        &mut self,
        ui: &mut UiRoot,
        context: u32,
        result: &DialogResult,
    ) -> Vec<PanelOutput> {
        if let Some(c) = self.characters.as_mut()
            && let Some(requests) = c.on_dialog(context, result)
        {
            return requests
                .into_iter()
                .filter_map(|r| match r {
                    PreGameRequest::Session(command) => Some(PanelOutput::Session(command)),
                    _ => None,
                })
                .collect();
        }
        if let Some(c) = self.creation.as_mut()
            && let Some(commands) = c.on_dialog(ui, context, result)
        {
            return commands.into_iter().map(creation_output).collect();
        }
        Vec::new()
    }

    /// Take every screen down (the window goes).
    pub fn dispose(&mut self, ui: &mut UiRoot, layouts: &dyn LayoutSource) {
        let catalog = self.catalog;
        let mut maker =
            |ui: &mut UiRoot, kind: DialogType| dialog_layout(ui, layouts, catalog, kind);
        let mut dialogs = Dialogs {
            factory: &mut self.dialogs,
            make: &mut maker,
        };
        if let Some(mut c) = self.credits.take() {
            c.dispose(ui, &mut dialogs);
        }
        if let Some(mut c) = self.creation.take() {
            c.dispose(ui, &mut dialogs);
        }
        if let Some(mut c) = self.characters.take() {
            c.dispose(ui, &mut dialogs);
        }
        if let Some(mut c) = self.connection.take() {
            c.dispose(ui);
        }
        // Closed without an answer: nothing reports.
        self.dialogs.reset(ui);
        drop(self.dialogs.take_closed());
    }
}

fn creation_output(command: ac_runtime::session::creation::CreationCommand) -> PanelOutput {
    PanelOutput::Session(SessionCommand::Lifecycle(LifecycleCommand::Create(command)))
}

/// A string resolver that owns its handle to the dats.
fn owned_strings(dats: &Arc<DatCollection>) -> StringResolver {
    let dats = Arc::clone(dats);
    StringResolver::new(move |id| dats.get(id).ok().flatten())
}

/// The selection screen's dialog texts; `None` when one is missing.
#[must_use]
pub fn character_strings(dats: &Arc<DatCollection>) -> Option<CharacterStrings> {
    let strings = owned_strings(dats);
    let text = |key: &str| strings.resolve(SCREEN_STRING_TABLE, compute_hash(key), 0);
    let template = |name: &str| {
        let variables: HashMap<u32, String> =
            std::iter::once((PLAYER_VARIABLE, name.to_owned())).collect();
        strings.resolve_template(
            SCREEN_STRING_TABLE,
            "ID_CharacterManagement_DeleteCharacterConfirmation",
            &variables,
        )
    };
    template("")?;
    let delete_response = text("ID_CharacterManagement_DeleteCharacterResponse")?;
    let please_wait = text("ID_CharacterManagement_PleaseWait")?;
    let entering_world = text("ID_Character_EnteringWorld")?;
    let confirm_exit = text("ID_CharacterManagement_ConfirmExit")?;
    let owned = owned_strings(dats);
    Some(CharacterStrings {
        delete_confirmation: Box::new(move |name| {
            let variables: HashMap<u32, String> =
                std::iter::once((PLAYER_VARIABLE, name.to_owned())).collect();
            owned
                .resolve_template(
                    SCREEN_STRING_TABLE,
                    "ID_CharacterManagement_DeleteCharacterConfirmation",
                    &variables,
                )
                .unwrap_or_default()
        }),
        delete_response,
        please_wait,
        entering_world,
        confirm_exit,
    })
}

/// The wizard's dialog texts and string lookup; `None` when a dialog
/// text is missing.
#[must_use]
pub fn creation_strings(dats: &Arc<DatCollection>) -> Option<CreationStrings> {
    let strings = owned_strings(dats);
    let text = |key: &str| strings.resolve(SCREEN_STRING_TABLE, compute_hash(key), 0);
    let exit_warning = text("ID_CharGen_ExitWarning")?;
    let no_name_warning = text("ID_CharGen_NoNameWarning")?;
    let credit_warning = text("ID_CharGen_CreditWarning")?;
    let randomize_warning = text("ID_CharGen_RandomizeWarning")?;
    let name_too_long = text("ID_CharGen_NameTooLong")?;
    let owned = owned_strings(dats);
    Some(CreationStrings {
        exit_warning,
        no_name_warning,
        credit_warning,
        randomize_warning,
        name_too_long,
        text: Box::new(move |key| owned.resolve(SCREEN_STRING_TABLE, compute_hash(key), 0)),
    })
}

/// The credits' text and pictures, read from the two roots and the
/// string tables.
fn credits_screen(
    ui: &mut UiRoot,
    content: &DatContent,
    strings: &StringResolver,
) -> Result<CreditsScreen, String> {
    let id = resolve_data_id(content.dats(), credits::ROOT_ENUM, SCREEN_ENUM_CATEGORY);
    if id == 0 {
        return Err("the enumeration names no layout".to_owned());
    }
    let infos = |root| import_infos_rooted(content, id, root);
    let (Some(text_info), Some(picture_info)) = (
        infos(credits::TEXT_ROOT_ID),
        infos(credits::PICTURE_ROOT_ID),
    ) else {
        return Err(format!("layout {id:#010x} lacks its roots"));
    };
    let Some((text_area, table, seconds, pictures)) =
        credits::authored_properties(&text_info, &picture_info)
    else {
        return Err("the roots' properties are missing".to_owned());
    };
    if text_area != credits::TEXT_AREA_ID || pictures.is_empty() {
        return Err("the roots name another text area or no picture".to_owned());
    }
    let fragments: Vec<String> = (1..=4096)
        .map_while(|i| strings.resolve(table, compute_hash(&format!("ID_Credits{i}")), 0))
        .collect();
    let please_wait = strings
        .resolve(GENERAL_STRING_TABLE, compute_hash("ID_Wait_PleaseWait"), 0)
        .ok_or("the wait text is missing")?;
    let font = content.font(DEFAULT_FONT_ID);
    let picture = import_rooted(ui, content, id, credits::PICTURE_ROOT_ID, font.clone())
        .ok_or("the picture root does not build")?;
    let Some(text) = import_rooted(ui, content, id, credits::TEXT_ROOT_ID, font) else {
        ui.destroy(picture.root);
        return Err("the text root does not build".to_owned());
    };
    let resources = CreditsResources {
        text_fragments: fragments,
        picture_ids: pictures,
        section_seconds: seconds,
        please_wait,
    };
    CreditsScreen::bind(ui, &picture, &text, resources).ok_or_else(|| {
        ui.destroy(picture.root);
        ui.destroy(text.root);
        "the authored roots, text or pictures are missing".to_owned()
    })
}

/// The font the screens' authored elements fall back to.
#[must_use]
pub fn default_font(content: &DatContent) -> Option<Arc<UiFont>> {
    content.font(DEFAULT_FONT_ID)
}
