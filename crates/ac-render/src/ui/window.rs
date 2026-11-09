//! The retail UI of one attached window: the chat, vitals and radar
//! windows, the toolbar, the spell bars, the spellbook, the examination
//! window, the character window, the inventory (with the paperdoll),
//! the social panel, the journal's contracts page, the vendor and the
//! secure-trade windows and the options window, imported from their
//! layouts and mounted as
//! OpenAC's `RetailUiRuntime` mounts them, fed from the session's
//! presentation stream every frame, and their requests turned into session
//! commands. The yes/no dialog is shared by the session's questions and the
//! panels' own.
//!
//! Built on attach, dropped on detach; everything it shows is rebuilt from
//! the session.

use std::sync::Arc;
use std::time::Instant;

use ac_core::spells::SpellMetadata;
use ac_dat::DatCollection;
use ac_dat::objects::{Palette, PixelFormat, RenderSurface, SkillTable, SpellComponentTable};
use ac_dat::texture::DecodeOptions;
use ac_host::settings::PlayerSettings;
use ac_proto::Position;
use ac_runtime::character::{CharacterCommand, option_id};
use ac_runtime::communication::ClientConfirmation;
use ac_runtime::gameplay::GameplayCommand;
use ac_runtime::gameplay::magic::cast_gate;
use ac_runtime::inventory::{InteractionMode, ItemCommand, ObjectRef};
use ac_runtime::session::SessionCommand;
use ac_runtime::world::PlayerView;
use ac_ui::draw::{DrawList, TextureRef};
use ac_ui::event::{Key, Modifiers};
use ac_ui::font::DEFAULT_FONT_ID;
use ac_ui::icons::IconComposer;
use ac_ui::import::{ImportedLayout, LayoutSource, import};
use ac_ui::panels::PanelOutput;
use ac_ui::panels::appraisal::{AppraisalController, AppraisalPage, SpellExamineComponent};
use ac_ui::panels::character::{
    CharacterSheet, CharacterStatController, CharacterTab, ExperienceCurves, SheetSource,
};
use ac_ui::panels::chat::{self, ChatContext, ChatWindowController};
use ac_ui::panels::dialog::{DialogAnswer, DialogQueue, confirmation_text, train_skill_message};
use ac_ui::panels::items::ItemsView;
use ac_ui::panels::magic::{
    MagicView, SpellKey, SpellbookPage, SpellbookWindowController, SpellcastingController,
};
use ac_ui::panels::main_panels::MainPanels;
use ac_ui::panels::panel_id;
use ac_ui::panels::paperdoll::InventoryWindowController;
use ac_ui::panels::radar::{self, RadarController, RadarObject, RadarPlayer, RadarScene};
use ac_ui::panels::toolbar::{ToolbarKey, ToolbarWindowController};
use ac_ui::panels::vitals::{self, VitalsController, VitalsSideBySide, VitalsView};
use ac_ui::radar::{RadarBehavior, RelationshipTraits};
use ac_ui::root::{UiOutput, UiRoot};
use ac_ui::strings::{PLAYER_VARIABLE, StringResolver, compute_hash};
use ac_ui::windows::WindowEvent;
use ac_ui::{DatContent, UiHost};
use glam::{Quat, Vec3};

use super::clipboard::SystemClipboard;
use super::facts::{Dirty, InterfaceFacts};
use super::gpu::{UiPixels, UiTextureSource};
use super::icons::RuntimeImages;
use super::input::{self as ui_input, Routed, WorldInput};
pub use super::mount::{Chrome, MountOptions, mount_window};
pub use super::social_panels::{
    HOUSE_PURCHASE_WINDOW, JOURNAL_WINDOW, SOCIAL_WINDOW, SocialPage, TRADE_WINDOW, VENDOR_WINDOW,
};
use crate::scene::Scene;

/// The main chat window's name.
pub const CHAT_WINDOW: &str = "chat";
/// The vitals window's name.
pub const VITALS_WINDOW: &str = ac_ui::layout_persistence::window_names::VITALS;
/// The side-by-side vitals window's name (shown instead of the stacked
/// one while the character option `SideBySideVitals` is on).
pub const SIDE_VITALS_WINDOW: &str = ac_ui::layout_persistence::window_names::SIDE_VITALS;
/// The radar's name.
pub const RADAR_WINDOW: &str = "radar";
/// The toolbar's name.
pub const TOOLBAR_WINDOW: &str = "toolbar";
/// The combat window's name (the spell bars).
pub const COMBAT_WINDOW: &str = "combat";
/// The spellbook's name.
pub const SPELLBOOK_WINDOW: &str = "spellbook";
/// The examination window's name.
pub const EXAMINATION_WINDOW: &str = "examination";
/// The character window's name.
pub const CHARACTER_WINDOW: &str = "character";
/// The inventory window's name (the paperdoll is its other half).
pub const INVENTORY_WINDOW: &str = "inventory";

/// The interface string table captions and questions come from.
pub(super) const UI_STRING_TABLE: u32 = 0x2300_0001;

/// Meters along one side of a landblock.
const LANDBLOCK_METERS: f32 = 192.0;

/// A position in one world frame, meters: the landblock's corner plus the
/// offset inside it (east +x, north +y).
#[must_use]
pub fn world_meters(position: &Position) -> Vec3 {
    let lx = (position.cell_id >> 24) & 0xFF;
    let ly = (position.cell_id >> 16) & 0xFF;
    Vec3::new(
        lx as f32 * LANDBLOCK_METERS + position.origin.x,
        ly as f32 * LANDBLOCK_METERS + position.origin.y,
        position.origin.z,
    )
}

/// A wire orientation as a rotation.
fn rotation(position: &Position) -> Quat {
    let q = position.orientation;
    Quat::from_xyzw(q.x, q.y, q.z, q.w)
}

/// What the radar and the vivid target indicator know of `guid`'s place in
/// the player's fellowship (OpenAC's `relationshipFor` bindings: a member,
/// and the leader, take their own blip; nothing else is filled in).
#[must_use]
pub fn fellowship_relationship(
    fellowship: Option<&ac_runtime::gameplay::FellowshipView>,
    guid: u32,
) -> RelationshipTraits {
    let Some(fellowship) = fellowship else {
        return RelationshipTraits::default();
    };
    RelationshipTraits {
        is_fellowship_member: fellowship.members.iter().any(|m| m.guid == guid),
        is_fellowship_leader: fellowship.leader_guid == guid,
        ..RelationshipTraits::default()
    }
}

/// What the radar is built from: the player and every placed object of
/// the scene (the radar's own range test leaves out the far ones), each
/// with its place in the player's `fellowship`.
#[must_use]
pub fn radar_scene(
    scene: &Scene,
    view: Option<&PlayerView>,
    fellowship: Option<&ac_runtime::gameplay::FellowshipView>,
) -> RadarScene {
    let player = scene.player().and_then(|p| {
        let position = p.position?;
        let rotation = rotation(&position);
        Some(RadarPlayer {
            guid: p.guid,
            position: world_meters(&position),
            rotation,
            yaw_radians: crate::camera::yaw_of(rotation),
            cell_id: position.cell_id,
            item_type: p.radar.item_type,
            pwd_bitfield: p.radar.public_weenie_bitfield,
            monarch_id: p.radar.monarch_id,
        })
    });
    let objects = scene
        .objects()
        .filter(|o| o.parent.is_none() && Some(o.guid) != player.map(|p| p.guid))
        .filter_map(|o| {
            let position = o.position?;
            Some(RadarObject {
                guid: o.guid,
                name: o.name.clone(),
                position: world_meters(&position),
                behavior: o.radar.radar_behavior.map(RadarBehavior::from_raw),
                item_type: o.radar.item_type,
                pwd_bitfield: o.radar.public_weenie_bitfield,
                color_override: o.radar.radar_blip_color,
                monarch_id: o.radar.monarch_id,
                relationship: fellowship_relationship(fellowship, o.guid),
            })
        })
        .collect();
    RadarScene {
        player,
        objects,
        selected: view.and_then(|v| v.selected),
        coordinates_on_radar: view.is_some_and(|v| v.coordinates_on_radar),
        ui_locked: view.is_some_and(|v| v.ui_locked),
    }
}

/// The session command a panel's request is, if it is one. The requests
/// the window answers itself (panels, dialogs, the spell bars, the
/// examination window) and the ones it does not act on are `None`.
#[must_use]
pub fn session_command(output: &PanelOutput) -> Option<SessionCommand> {
    let item = |c| Some(SessionCommand::Item(c));
    let character = |c| Some(SessionCommand::Character(c));
    match output {
        PanelOutput::Session(command) => Some(command.clone()),
        PanelOutput::SelectObject(guid) => Some(SessionCommand::Gameplay(
            GameplayCommand::SelectClicked(ObjectRef::Guid(*guid)),
        )),
        PanelOutput::SetUiLocked(locked) => Some(SessionCommand::SetCharacterOption {
            option: option_id::LOCK_UI,
            value: *locked,
        }),
        PanelOutput::AddShortcut(entry) => item(ItemCommand::AddShortcut(*entry)),
        PanelOutput::RemoveShortcut(slot) => item(ItemCommand::RemoveShortcut(*slot)),
        PanelOutput::EnterUseMode => item(ItemCommand::EnterUseMode),
        PanelOutput::EnterExamineMode => item(ItemCommand::EnterExamineMode),
        PanelOutput::CancelTargetMode => item(ItemCommand::CancelTargetMode),
        PanelOutput::ToggleCombat => Some(SessionCommand::Gameplay(GameplayCommand::ToggleCombat)),
        PanelOutput::RaiseAttribute { attribute, xp } => {
            character(CharacterCommand::RaiseAttribute {
                attribute: *attribute,
                xp: *xp,
            })
        }
        PanelOutput::RaiseVital { vital, xp } => character(CharacterCommand::RaiseVital {
            vital: *vital,
            xp: *xp,
        }),
        PanelOutput::RaiseSkill { skill, xp } => character(CharacterCommand::RaiseSkill {
            skill: *skill,
            xp: *xp,
        }),
        // Asked first (the training confirmation), then sent.
        PanelOutput::TrainSkill { .. } => None,
        // Answered by the window: the main panels, the dialog, the spell
        // bars, the examination window.
        PanelOutput::TogglePanel(_)
        | PanelOutput::ClosePanel(_)
        | PanelOutput::ResetSplitQuantity { .. }
        | PanelOutput::Confirm { .. }
        | PanelOutput::ConfirmEither { .. }
        | PanelOutput::ChessResignAnswered(_)
        | PanelOutput::FavoriteSpell(_)
        | PanelOutput::ExamineSpell(_)
        | PanelOutput::CloseExamination
        | PanelOutput::Options(_)
        | PanelOutput::KeyboardConfig(_)
        | PanelOutput::ToggleFloatingChat(_) => None,
        // Client-local text goes to the session, which shows it in the
        // status box (OpenAC `ReportClientLocal`).
        PanelOutput::Notice(text) => Some(SessionCommand::ShowInterfaceText(text.clone())),
        // A map link goes to the session's plugins.
        PanelOutput::CoordinateLinkClicked {
            east_west,
            north_south,
            text,
        } => Some(SessionCommand::ChatLinkClicked {
            east_west: *east_west,
            north_south: *north_south,
            text: text.clone(),
        }),
        // No session command: the window flashes the figure itself, and a
        // panel's hovered object is not acted on, as OpenAC.
        PanelOutput::HoveredObject(_) | PanelOutput::FlashFigure { .. } => None,
        // The selected-object strip's mana query (0 closes it; OpenAC
        // `WorldSession.SendQueryItemMana`).
        PanelOutput::QueryItemMana(guid) => item(ItemCommand::QueryMana(ObjectRef::Guid(*guid))),
        // The link panel's ping; its answer gives the round trip the
        // panel shows (OpenAC `LinkStatusUiController`).
        PanelOutput::RequestLinkPing => Some(SessionCommand::Ping),
    }
}

/// What a panel hotkey asks for (OpenAC `KeyBindings.RetailDefaults`).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Hotkey {
    /// The spellbook on this page (F5 spells, F6 components).
    Spellbook(SpellbookPage),
    /// The character window on this tab (F8 attributes, F9 skills).
    Character(CharacterTab),
    /// The inventory (F12).
    Inventory,
    /// The social panel on this page (F3 allegiance, F4 fellowship).
    Social(SocialPage),
    /// A toolbar key: a quick slot or the new shortcut.
    Toolbar(ToolbarKey),
    /// A spell-bar key (the spell slots and steps of the magic key map).
    Spell(SpellKey),
    /// The combat toggle (the grave accent).
    ToggleCombat,
    /// The options window (F11).
    Options,
    /// The map and house panel (F10).
    WorldPanel,
}

/// The hotkey `key` with `modifiers` names, if any.
#[must_use]
pub fn hotkey(key: Key, modifiers: Modifiers) -> Option<Hotkey> {
    if modifiers.contains(Modifiers::ALT) || modifiers.contains(Modifiers::SHIFT) {
        return None;
    }
    Some(match key {
        Key::GraveAccent if !modifiers.contains(Modifiers::CTRL) => Hotkey::ToggleCombat,
        _ => return None,
    })
}

/// A scripted input step ([`WindowUi::script`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum UiScript {
    /// Press and release a key.
    Key {
        /// The key.
        key: Key,
        /// Held modifiers.
        modifiers: Modifiers,
    },
    /// Move to (`x`, `y`) and click (twice for a double click).
    Click {
        /// Window pixel x.
        x: i32,
        /// Window pixel y.
        y: i32,
        /// The window's button number (0 left, 1 right, 2 middle).
        button: u16,
        /// Two clicks.
        double: bool,
    },
    /// Move the window's pointer to (`x`, `y`) through its own input path:
    /// the UI and the world both see it (the world's hover).
    Pointer {
        /// Window pixel x.
        x: i32,
        /// Window pixel y.
        y: i32,
    },
    /// Move the window's pointer to (`x`, `y`) and click there (twice for
    /// a double click) through its own input path: a click over no UI
    /// element reaches the world.
    PointerClick {
        /// Window pixel x.
        x: i32,
        /// Window pixel y.
        y: i32,
        /// The window's button number (0 left, 1 right, 2 middle).
        button: u16,
        /// Two clicks.
        double: bool,
    },
    /// Move to (`x`, `y`) and press the left button there (a drag starts
    /// with the moves that follow; each step in its own frame lets the
    /// panels answer the drag as it goes).
    Press {
        /// Window pixel x.
        x: i32,
        /// Window pixel y.
        y: i32,
    },
    /// Move the pointer (with the left button held when it was pressed).
    Move {
        /// Window pixel x.
        x: i32,
        /// Window pixel y.
        y: i32,
    },
    /// Release the left button at (`x`, `y`).
    Release {
        /// Window pixel x.
        x: i32,
        /// Window pixel y.
        y: i32,
    },
    /// Report where things are ([`WindowUi::probe`]) as notices.
    Probe,
}

/// A key's name as the console writes it ("f12", "alt+1", "0", "grave",
/// "a"); modifiers come first, joined by `+`.
#[must_use]
pub fn parse_key(text: &str) -> Option<(Key, Modifiers)> {
    let mut modifiers = Modifiers::NONE;
    let mut parts: Vec<&str> = text.split('+').collect();
    let name = parts.pop()?.to_ascii_lowercase();
    for m in parts {
        modifiers = modifiers
            | match m.to_ascii_lowercase().as_str() {
                "alt" => Modifiers::ALT,
                "ctrl" => Modifiers::CTRL,
                "shift" => Modifiers::SHIFT,
                _ => return None,
            };
    }
    let key = match name.as_str() {
        "0" => Key::Number0,
        "1" => Key::Number1,
        "2" => Key::Number2,
        "3" => Key::Number3,
        "4" => Key::Number4,
        "5" => Key::Number5,
        "6" => Key::Number6,
        "7" => Key::Number7,
        "8" => Key::Number8,
        "9" => Key::Number9,
        "f1" => Key::F1,
        "f2" => Key::F2,
        "f3" => Key::F3,
        "f4" => Key::F4,
        "f5" => Key::F5,
        "f6" => Key::F6,
        "f7" => Key::F7,
        "f8" => Key::F8,
        "f9" => Key::F9,
        "f10" => Key::F10,
        "f11" => Key::F11,
        "f12" => Key::F12,
        "grave" => Key::GraveAccent,
        "enter" => Key::Enter,
        "escape" | "esc" => Key::Escape,
        "tab" => Key::Tab,
        _ => return None,
    };
    Some((key, modifiers))
}

/// What an answer to the open dialog does.
#[derive(Debug, Clone)]
pub(super) enum DialogAction {
    /// A question the session asked (the server's, or `/die`): the
    /// answer goes back to it.
    Session(ClientConfirmation),
    /// A panel's question: a yes acts on this request.
    Panel(PanelOutput),
    /// A panel's question with an answer for each: a yes acts on the
    /// first request, a no on the second.
    Either(PanelOutput, PanelOutput),
    /// The training confirmation: a yes trains, a no frees the raise
    /// buttons.
    Train {
        /// Skill id.
        skill: u32,
        /// Credits.
        credits: u32,
    },
}

/// Render surfaces read from the dats, and the composed icons, for the
/// UI's textures.
#[derive(Debug)]
pub struct DatSurfaces<'a> {
    dats: &'a DatCollection,
    images: &'a RuntimeImages,
}

impl UiTextureSource for DatSurfaces<'_> {
    fn decode(&mut self, texture: TextureRef) -> Option<UiPixels> {
        let id = match texture {
            TextureRef::Surface(id) => id,
            TextureRef::Runtime(id) => return self.images.take(id),
        };
        let surface = self.dats.get::<RenderSurface>(id).ok()??;
        if let Some(coverage) = coverage_pixels(&surface) {
            return Some(coverage);
        }
        let palette = if surface.default_palette_id != 0 {
            self.dats
                .get::<Palette>(surface.default_palette_id)
                .ok()
                .flatten()
        } else {
            None
        };
        ac_dat::texture::decode(&surface, palette.as_ref(), DecodeOptions::default())
            .ok()
            .map(UiPixels::Rgba)
    }

    fn released(&mut self) -> Vec<TextureRef> {
        self.images.take_released()
    }
}

/// A one-byte alpha image (the font atlases) as stored, drawn white with
/// that alpha: what its four-channel decode (255, 255, 255, value) draws.
/// `None` for any other image, or one whose stored bytes are short.
fn coverage_pixels(surface: &RenderSurface) -> Option<UiPixels> {
    if !matches!(
        surface.format,
        PixelFormat::A8 | PixelFormat::CUSTOM_LSCAPE_ALPHA
    ) {
        return None;
    }
    let len = (surface.width as usize).checked_mul(surface.height as usize)?;
    if len == 0 {
        return None;
    }
    Some(UiPixels::Coverage {
        width: surface.width,
        height: surface.height,
        alpha: surface.data.get(..len)?.to_vec(),
    })
}

/// The UI of one window. See the module documentation.
pub struct WindowUi {
    pub(super) host: UiHost,
    pub(super) content: DatContent,
    started: Instant,
    last_tick: Option<Instant>,
    context: ChatContext,
    pub(super) player_view: Option<PlayerView>,
    pub(super) clipboard: SystemClipboard,
    pub(super) notices: Vec<String>,
    /// Plugins asked the window to minimize (`true`) or restore.
    pub(super) minimize_requests: Vec<bool>,
    pub(super) mounted: Vec<&'static str>,
    pub(super) facts: InterfaceFacts,
    pub(super) images: RuntimeImages,
    pub(super) icons: IconComposer,
    pub(super) experience: Option<ExperienceCurves>,
    pub(super) skill_table: Option<SkillTable>,
    pub(super) components: Option<SpellComponentTable>,
    /// Each spell component's weenie class (the component id map
    /// 0x27000002): the components page knows a component by its class.
    pub(super) component_classes: std::collections::HashMap<u32, u32>,
    /// The component check over the component classes and each school's
    /// focus: which formula an examined spell shows.
    pub(super) spell_components: ac_core::spells::SpellComponentRequirements,
    pub(super) panels: MainPanels,
    pub(super) dialogs: DialogQueue<DialogAction>,
    pub(super) dialog_layout: Option<u32>,
    /// The selection the panels were last fed.
    fed_selection: Option<Option<u32>>,
    /// Panels shown since they were last fed.
    stale: Vec<&'static str>,
    /// Whether the character was in the world when the social panel was
    /// last fed (its declarations follow the world entries).
    pub(super) social_in_world: Option<bool>,
    /// The selection the social panel was last fed.
    pub(super) fed_social_selection: Option<Option<u32>>,
    /// The client settings in force in this window: the one copy every
    /// editor here changes, only through [`WindowUi::edit_settings`] (or
    /// [`WindowUi::replace_settings`] for a change made outside the
    /// window), so the options window's copy always follows it.
    settings: PlayerSettings,
    /// Sections changed, not yet taken by the window, each with whether to
    /// save it now.
    settings_changes: Vec<(ac_ui::panels::options::Section, bool)>,
    /// The pre-game screens (`super::pregame`).
    pub(super) pregame: Option<ac_ui::pregame::PreGameScreens>,
    /// The chat entry, set aside while a pre-game screen shows.
    pub(super) parked_text_input: Option<ac_ui::tree::ElementId>,
    /// The in-game windows hidden while a pre-game screen shows (shown
    /// again when it goes).
    pub(super) pregame_hidden: Vec<String>,
    /// The 3-D viewports (`super::viewports`).
    pub(super) viewports: super::viewports::UiViewports,
    /// The status box and the other status pieces.
    pub(super) status: super::status::StatusUi,
    /// The Configure Keyboard screen (`super::keyboard_window`).
    pub(super) keyboard: super::keyboard_window::KeyboardUi,
    /// The shared item cooldown sweep (`super::cooldowns`).
    pub(super) item_cooldowns: Option<ac_ui::panels::item_cooldown::ItemCooldownController>,
    /// The map's clock (`super::map_window`).
    pub(super) map_state: super::map_window::MapWindowState,
    /// The titles' names and the information report's phrases
    /// (`super::character_pages`).
    pub(super) character_pages: super::character_pages::CharacterPagesState,
    /// The plugins' UI (`super::plugin_ui`).
    pub(super) plugins: super::plugin_ui::PluginWindowUi,
    /// Which vitals window shows, as the character option says (only when
    /// the side-by-side window mounted).
    vitals_side_by_side: Option<VitalsSideBySide>,
    /// The barber window's swatch sources (`super::barber_window`).
    pub(super) barber: super::barber_window::BarberWindow,
}

impl std::fmt::Debug for WindowUi {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("WindowUi")
            .field("mounted", &self.mounted)
            .finish_non_exhaustive()
    }
}

impl WindowUi {
    /// Import and mount the windows for a screen of `size` pixels, as
    /// OpenAC's `RetailUiRuntime.Initialize` does (in its order). A window
    /// whose layout cannot be read is left out (and named in the notices);
    /// no window at all is an error.
    pub fn build(dats: Arc<DatCollection>, size: (u32, u32)) -> Result<Self, String> {
        Self::build_with(
            dats,
            size,
            &PlayerSettings::default(),
            super::options::resolution_choices(&[], None),
        )
    }

    /// [`WindowUi::build`] with the player's client settings (the options
    /// window edits them; the chat font and filters start from them) and
    /// the resolutions the display menu offers.
    pub fn build_with(
        dats: Arc<DatCollection>,
        size: (u32, u32),
        settings: &PlayerSettings,
        resolution_choices: Vec<String>,
    ) -> Result<Self, String> {
        let content = DatContent::new(dats.clone()).map_err(|e| e.to_string())?;
        let character_pages = super::character_pages::CharacterPagesState::new(&content);
        let mut host = UiHost::new();
        host.root.set_size(size.0 as f32, size.1 as f32);
        let images = RuntimeImages::new();
        let icons = IconComposer::new(dats.clone(), images.uploader());
        let mut ui = Self {
            host,
            content,
            started: Instant::now(),
            last_tick: None,
            context: ChatContext::default(),
            player_view: None,
            clipboard: SystemClipboard::new(),
            minimize_requests: Vec::new(),
            notices: Vec::new(),
            mounted: Vec::new(),
            facts: InterfaceFacts::default(),
            images,
            icons,
            experience: None,
            skill_table: None,
            components: None,
            component_classes: std::collections::HashMap::new(),
            spell_components: ac_core::spells::SpellComponentRequirements::default(),
            panels: MainPanels::new(),
            dialogs: DialogQueue::default(),
            dialog_layout: None,
            fed_selection: None,
            stale: Vec::new(),
            social_in_world: None,
            fed_social_selection: None,
            settings: settings.clone(),
            settings_changes: Vec::new(),
            pregame: None,
            parked_text_input: None,
            pregame_hidden: Vec::new(),
            viewports: super::viewports::UiViewports::new(dats.clone()),
            status: super::status::StatusUi::default(),
            keyboard: super::keyboard_window::KeyboardUi::default(),
            item_cooldowns: None,
            map_state: super::map_window::MapWindowState::default(),
            character_pages,
            plugins: super::plugin_ui::PluginWindowUi::default(),
            vitals_side_by_side: None,
            barber: super::barber_window::BarberWindow::default(),
        };
        let pregame_dats = dats.clone();
        ui.load_tables(&dats);
        if let Err(e) = ui.mount_vitals() {
            ui.notices.push(format!("ui: vitals: {e}"));
        }
        if let Err(e) = ui.mount_radar() {
            ui.notices.push(format!("ui: radar: {e}"));
        }
        if let Err(e) = ui.mount_chat(dats) {
            ui.notices.push(format!("ui: chat: {e}"));
        }
        ui.mount_floating_chat();
        ui.mount_panels();
        match ui.mount_options(resolution_choices) {
            Ok(()) => {
                ui.mounted.push(super::options::OPTIONS_WINDOW);
                ui.bind_panel_buttons();
            }
            Err(e) => ui.notices.push(format!("ui: options: {e}")),
        }
        ui.apply_chat_settings();
        ui.mount_pregame(&pregame_dats);
        ui.mount_barber(&pregame_dats);
        ui.mount_status();
        ui.mount_item_cooldowns();
        ui.mount_plugin_ui();
        for error in ui.content.take_errors() {
            ui.notices.push(format!(
                "ui: cannot read {} {:#010x}: {}",
                error.what, error.id, error.message
            ));
        }
        if ui.mounted.is_empty() {
            return Err(ui.notices.join("; "));
        }
        Ok(ui)
    }

    /// The client settings in force in this window.
    #[must_use]
    pub fn settings(&self) -> &PlayerSettings {
        &self.settings
    }

    /// The sections changed since the last call, each with whether to save
    /// it now. The window applies them; its host saves them.
    pub fn take_settings_changes(&mut self) -> Vec<(ac_ui::panels::options::Section, bool)> {
        std::mem::take(&mut self.settings_changes)
    }

    /// Change the client settings: `edit` changes only the fields its
    /// editor owns, on the settings as they stand now, and `section` is
    /// recorded for the window to apply (and to save when `persist`).
    ///
    /// The options window keeps a copy of the settings to show and edit; it
    /// takes every change at once, so its own next change starts from the
    /// settings in force and cannot put another editor's older values
    /// back (the dock's place, the theme, a console's display change).
    pub(super) fn edit_settings(
        &mut self,
        section: ac_ui::panels::options::Section,
        persist: bool,
        edit: impl FnOnce(&mut PlayerSettings),
    ) {
        edit(&mut self.settings);
        self.show_settings_in_options();
        if !self.settings_changes.contains(&(section, persist)) {
            self.settings_changes.push((section, persist));
        }
    }

    /// The settings as changed outside this window (a console command, the
    /// mixer): they replace this window's, and the options window's copy
    /// follows. Nothing is recorded: the change's owner applied and saved
    /// it.
    pub(super) fn replace_settings(&mut self, settings: &PlayerSettings) {
        self.settings.clone_from(settings);
        self.show_settings_in_options();
    }

    /// The options window's copy of the settings follows this window's.
    fn show_settings_in_options(&mut self) {
        let settings = self.settings.clone();
        self.host
            .with_controller::<ac_ui::panels::options::OptionsWindowController, _>(
                super::options::OPTIONS_WINDOW,
                |c, _| {
                    let mut live = c.live().clone();
                    live.settings = settings;
                    c.set_live(live);
                },
            );
    }

    pub(super) fn import(&mut self, layout_id: u32) -> Result<ImportedLayout, String> {
        let font = self.content.font(DEFAULT_FONT_ID);
        import(&mut self.host.root, &self.content, layout_id, font)
            .ok_or_else(|| format!("layout {layout_id:#010x} is not in the dats"))
    }

    /// OpenAC `RetailUiRuntime.MountVitals`.
    fn mount_vitals(&mut self) -> Result<(), String> {
        let layout = self.import(vitals::LAYOUT_ID)?;
        let controller = VitalsController::bind(&mut self.host.root, &layout);
        mount_window(
            &mut self.host.root,
            VITALS_WINDOW,
            layout.root,
            &MountOptions {
                left: 10.0,
                top: 30.0,
                resize_x: true,
                resize_y: false,
                min_width: Some(40.0),
                content_click_through: Some(false),
                ..MountOptions::default()
            },
        )?;
        self.host
            .attach_controller(VITALS_WINDOW, Box::new(controller))
            .map_err(|e| e.to_string())?;
        self.mounted.push(VITALS_WINDOW);
        match self.mount_side_vitals() {
            Ok(()) => self.mounted.push(SIDE_VITALS_WINDOW),
            Err(e) => self.notices.push(format!(
                "ui: side vitals: {e}; the SideBySideVitals option has no window"
            )),
        }
        Ok(())
    }

    /// OpenAC `RetailUiRuntime.MountSideVitals`: the one-row vitals window,
    /// hidden, at the stacked window's place; which of the two shows
    /// follows the character option ([`Self::follow_side_by_side_vitals`]).
    fn mount_side_vitals(&mut self) -> Result<(), String> {
        let info = ac_ui::import::import_infos(&self.content, vitals::SIDE_BY_SIDE_LAYOUT_ID)
            .ok_or("layout 0x21000075 is not in the dats")?;
        let layout = self.import(vitals::SIDE_BY_SIDE_LAYOUT_ID)?;
        let controller = VitalsController::bind(&mut self.host.root, &layout);
        mount_window(
            &mut self.host.root,
            SIDE_VITALS_WINDOW,
            layout.root,
            &MountOptions {
                left: 10.0,
                top: 30.0,
                resize_x: true,
                resize_y: false,
                dat_constraint_source: Some(info),
                visible: false,
                content_click_through: Some(false),
                ..MountOptions::default()
            },
        )?;
        self.host
            .attach_controller(SIDE_VITALS_WINDOW, Box::new(controller))
            .map_err(|e| e.to_string())?;
        self.vitals_side_by_side = Some(VitalsSideBySide::new(VITALS_WINDOW, SIDE_VITALS_WINDOW));
        Ok(())
    }

    /// OpenAC `VitalsSideBySideController.Tick`: the stacked or the
    /// side-by-side vitals window, as the character option says (only a
    /// change moves them). Not while a pre-game screen shows: the in-game
    /// windows are hidden under it and come back when it goes.
    fn follow_side_by_side_vitals(&mut self) {
        if self.pregame_active() {
            return;
        }
        let Some(switch) = self.vitals_side_by_side.as_mut() else {
            return;
        };
        // Before the player's view arrives, the client's starting words.
        let (options1, options2) = self.player_view.map_or(
            (
                ac_runtime::character::DEFAULT_OPTIONS1,
                ac_runtime::character::DEFAULT_OPTIONS2,
            ),
            |v| (v.options1, v.options2),
        );
        let side_by_side = ac_runtime::character::option(option_id::SIDE_BY_SIDE_VITALS)
            .is_some_and(|e| {
                let word = if e.in_options1 { options1 } else { options2 };
                word & e.mask != 0
            });
        switch.tick(&mut self.host.root, side_by_side);
    }

    /// OpenAC `RetailUiRuntime.MountRadar`.
    fn mount_radar(&mut self) -> Result<(), String> {
        let layout = self.import(radar::LAYOUT_ID)?;
        let font = self.content.font(DEFAULT_FONT_ID);
        let controller = RadarController::bind(&mut self.host.root, &layout, font)
            .ok_or("the layout's root is not the radar")?;
        let screen_width = self.host.root.size().0;
        let width = self.host.root.element(layout.root).map_or(0.0, |e| e.width);
        mount_window(
            &mut self.host.root,
            RADAR_WINDOW,
            layout.root,
            &MountOptions {
                left: (screen_width - width - 10.0).max(0.0),
                top: 10.0,
                resizable: false,
                resize_x: false,
                resize_y: false,
                content_click_through: Some(false),
                ..MountOptions::default()
            },
        )?;
        self.host
            .attach_controller(RADAR_WINDOW, Box::new(controller))
            .map_err(|e| e.to_string())?;
        self.mounted.push(RADAR_WINDOW);
        Ok(())
    }

    /// OpenAC `RetailUiRuntime.MountChat`.
    fn mount_chat(&mut self, dats: Arc<DatCollection>) -> Result<(), String> {
        let layout = self.import(chat::LAYOUT_ID)?;
        let font = self.content.font(DEFAULT_FONT_ID);
        let resolver = StringResolver::new(move |id| dats.get(id).ok().flatten());
        let strings: chat::StringLookup =
            Box::new(move |key| resolver.resolve(UI_STRING_TABLE, compute_hash(key), 0));
        let controller =
            ChatWindowController::bind(&mut self.host.root, &layout, font, Some(strings))
                .ok_or("required role elements are missing")?;
        let root = controller.root();
        let input = controller.input();
        mount_window(
            &mut self.host.root,
            CHAT_WINDOW,
            root,
            &MountOptions {
                left: 10.0,
                top: 440.0,
                authored_geometry_revision: 1,
                ..MountOptions::default()
            },
        )?;
        self.host
            .attach_controller(CHAT_WINDOW, Box::new(controller))
            .map_err(|e| e.to_string())?;
        self.host.root.default_text_input = Some(input);
        self.host
            .with_controller::<ChatWindowController, _>(CHAT_WINDOW, |c, ui| {
                c.bind_indicator_clicks(ui);
            });
        self.mounted.push(CHAT_WINDOW);
        Ok(())
    }

    /// The windows that are mounted.
    #[must_use]
    pub fn mounted(&self) -> &[&'static str] {
        &self.mounted
    }

    /// The UI host (input goes through [`WindowUi::key`] and
    /// [`super::input`]).
    pub fn host_mut(&mut self) -> &mut UiHost {
        &mut self.host
    }

    /// The UI host.
    #[must_use]
    pub fn host(&self) -> &UiHost {
        &self.host
    }

    /// The window's copy of the panels' facts.
    #[must_use]
    pub fn facts(&self) -> &InterfaceFacts {
        &self.facts
    }

    /// What the window should tell its owner since the last call.
    pub fn take_notices(&mut self) -> Vec<String> {
        std::mem::take(&mut self.notices)
    }

    /// Feed the panels from the session: new chat lines, the player's view
    /// (vitals, selection, options), the radar's scene, and the item,
    /// character and magic panels' facts.
    pub fn feed(&mut self, scene: &mut Scene) {
        self.feed_pregame(scene);
        self.feed_status(scene);
        let lines = scene.take_chat();
        if !lines.is_empty() {
            self.host
                .with_controller::<ChatWindowController, _>(CHAT_WINDOW, |c, _| {
                    for line in &lines {
                        c.push_chat_line(line);
                    }
                });
            self.feed_floating_chat(&lines);
        }
        let view = scene.player_view().copied();
        if view != self.player_view {
            let words =
                |v: Option<PlayerView>| v.map(|v| (v.options1, v.options2, v.options_seeded));
            let words_changed = words(view) != words(self.player_view);
            let seeded_now = view.is_some_and(|v| v.options_seeded)
                && !self.player_view.is_some_and(|v| v.options_seeded);
            self.player_view = view;
            if words_changed {
                self.follow_options_words(seeded_now);
            }
            let vitals = view
                .as_ref()
                .map(VitalsView::from_player_view)
                .unwrap_or_default();
            for name in [VITALS_WINDOW, SIDE_VITALS_WINDOW] {
                self.host
                    .with_controller::<VitalsController, _>(name, |c, ui| {
                        c.apply(ui, &vitals);
                    });
            }
            if let Some(view) = view {
                self.host.root.set_ui_locked(view.ui_locked);
            }
        }
        self.follow_side_by_side_vitals();
        let selected = self.player_view.and_then(|v| v.selected).map(|guid| {
            let name = scene
                .object_by_guid(guid)
                .and_then(|o| o.name.clone())
                .unwrap_or_else(|| format!("0x{guid:08X}"));
            (name, guid)
        });
        let context = ChatContext {
            selected_target: selected,
            stay_in_chat_mode: self.player_view.is_some_and(|v| v.stay_in_chat_mode),
        };
        if context != self.context {
            self.context = context.clone();
            self.host
                .with_controller::<ChatWindowController, _>(CHAT_WINDOW, |c, _| {
                    c.set_context(context);
                });
        }
        for event in scene.take_interface() {
            self.facts.apply(event);
        }
        // The examination window follows every selection change while it
        // shows, in order, not only the selection this frame ends on.
        let changes = std::mem::take(&mut self.facts.selection_changes);
        if !changes.is_empty() {
            self.host
                .with_controller::<AppraisalController, _>(EXAMINATION_WINDOW, |c, ui| {
                    for &selected in &changes {
                        c.follow_selection(ui, selected);
                    }
                });
        }
        let fellowship = self.facts.social.as_ref().map(|s| &s.fellowship);
        let radar = radar_scene(scene, self.player_view.as_ref(), fellowship);
        self.host
            .with_controller::<RadarController, _>(RADAR_WINDOW, |c, _| c.set_scene(&radar));
        self.feed_plugin_ui();
        let mut dirty = self.facts.take_dirty();
        if dirty.chat_entry
            && let Some(entry) = self.facts.chat_entry.clone()
        {
            self.host
                .with_controller::<ChatWindowController, _>(CHAT_WINDOW, |c, ui| {
                    c.set_entry(ui, &entry);
                });
        }
        let selection = self.player_view.and_then(|v| v.selected);
        if self.fed_selection != Some(selection) {
            self.fed_selection = Some(selection);
            dirty.state = true;
        }
        let stale = self.stale.clone();
        self.update_panels(dirty);
        self.tick_item_cooldowns();
        self.update_dealings(dirty, &stale);
        self.update_character_pages(dirty, &stale);
        self.update_components_page(dirty, &stale);
        self.update_external_container();
        self.update_salvage();
        self.update_book(dirty);
        self.update_map_house(scene);
        self.update_barber(dirty, scene);
        self.update_journal_pages(dirty, scene);
        self.refresh_examined_spell(dirty);
        self.show_appraisals();
        self.follow_confirmation();
        let link = self.status.link.unwrap_or(ac_runtime::session::LinkStatus {
            connected: true,
            ..Default::default()
        });
        self.update_indicator_panels(link);
        self.update_mini_game();
    }

    /// The panels' views from the window's copy of the facts, and each
    /// mounted panel updated: the toolbar every frame (its drag answers are
    /// due before the next draw), the others while shown or when what they
    /// show changed.
    fn update_panels(&mut self, dirty: Dirty) {
        let selected = self.player_view.and_then(|v| v.selected);
        let stale = std::mem::take(&mut self.stale);
        let display_title = self.display_title_text();
        let Self {
            host,
            facts,
            icons,
            experience,
            skill_table,
            content,
            status,
            panels,
            ..
        } = self;
        let mana_answers = std::mem::take(&mut facts.item_mana);
        let container_opens = std::mem::take(&mut facts.container_opens);
        let state = &facts.state;
        let selection_facts = super::status::selection_facts(state, selected, status);
        let player = state.player;
        let pending: Vec<u32> = state.pending_item.into_iter().collect();
        let split = host.with_controller::<ToolbarWindowController, _>(TOOLBAR_WINDOW, |c, _| {
            c.split.quantity().clone()
        });
        let numbers = facts.player.as_ref().map(|p| &p.numbers);
        let resolve = super::facts::composed_names(&status.names);
        let view = facts.items_view(selected, &pending, split.as_ref(), &resolve);
        let shown =
            |host: &UiHost, name: &str| host.root.is_window_visible(name) || stale.contains(&name);
        host.with_controller::<ToolbarWindowController, _>(TOOLBAR_WINDOW, |c, ui| {
            c.update(ui, &view, icons);
            if let Some(s) = c.selected.as_mut() {
                // The answers first: the strip shows the bar, then fills
                // it from the facts.
                for answer in &mana_answers {
                    s.on_item_mana(ui, answer.item_id, answer.valid);
                }
                s.update(ui, &view, &selection_facts);
            }
        });
        // A carried pack used from anywhere (the main pack too): the
        // inventory window shows, open on it.
        if !container_opens.is_empty() {
            let opened = host
                .with_controller::<InventoryWindowController, _>(INVENTORY_WINDOW, |c, ui| {
                    // Every one is applied, in order: the last decides what
                    // shows open.
                    let mut opened = false;
                    for &pack in &container_opens {
                        opened |= c.inventory.open_contained_container(ui, pack, &view);
                    }
                    opened
                })
                .unwrap_or(false);
            if opened && !panels.is_visible(&host.root, panel_id::INVENTORY) {
                panels.set_visibility(&mut host.root, panel_id::INVENTORY, true);
            }
        }
        if shown(host, INVENTORY_WINDOW) || dirty.any() {
            host.with_controller::<InventoryWindowController, _>(INVENTORY_WINDOW, |c, ui| {
                c.update(ui, &view, icons);
            });
        }
        // The character window: its sheet when the player or his object
        // changed.
        let player_object = facts.objects.get(player);
        if let (Some(numbers), Some(object)) = (numbers, player_object)
            && (dirty.player || dirty.items || dirty.titles || stale.contains(&CHARACTER_WINDOW))
        {
            let strings = content.strings();
            let ui_string = |key: &str| strings.resolve(UI_STRING_TABLE, compute_hash(key), 0);
            let sheet = CharacterSheet::build(&SheetSource {
                player: numbers,
                properties: &object.properties,
                pwd_bitfield: object.public_weenie_bitfield.map_or(0, |f| f.bits()),
                name: &object.name,
                title: display_title.as_deref(),
                experience: experience.as_ref(),
                skill_table: skill_table.as_ref(),
                ui_string: Some(&ui_string),
            });
            host.with_controller::<CharacterStatController, _>(CHARACTER_WINDOW, |c, ui| {
                c.apply(ui, &sheet);
            });
        }
        // The spellbook and the spell bars.
        if let Some(book) = numbers.and_then(|n| n.spellbook()) {
            let objects = &facts.objects;
            let gate = |spell| cast_gate(objects, player, book, spell, selected);
            let magic = MagicView {
                selected,
                cast_gate: Some(&gate),
                ..MagicView::new(book, objects, player)
            };
            if shown(host, SPELLBOOK_WINDOW) || dirty.any() {
                host.with_controller::<SpellbookWindowController, _>(SPELLBOOK_WINDOW, |c, ui| {
                    c.update(ui, &magic, icons);
                });
            }
            if shown(host, COMBAT_WINDOW) || dirty.any() {
                host.with_controller::<SpellcastingController, _>(COMBAT_WINDOW, |c, ui| {
                    c.update(ui, &magic, icons);
                });
            }
        }
        let combat_mode = state.combat_mode;
        self.update_combat_bar(combat_mode);
    }

    /// Appraisals that arrived: the examination window shows the last one
    /// it takes, and opens when one answers the player's own question (an
    /// automation answer never opens it; OpenAC `AppraisalUiController.Apply`).
    fn show_appraisals(&mut self) {
        let appraisals = std::mem::take(&mut self.facts.appraisals);
        let Some(view) = appraisals.iter().rev().find(|v| v.present_in_ui) else {
            return;
        };
        let opens = appraisals.iter().any(|v| v.opens_window);
        let Self {
            host,
            content,
            character_pages,
            status,
            facts,
            ..
        } = self;
        let book = facts.numbers().and_then(|n| n.spellbook());
        let spell = |id| book.and_then(|b| b.try_get_metadata(id)).cloned();
        let objects = &facts.objects;
        let player = facts.state.player;
        let player_name = objects.get(player).map_or("", |o| o.name.as_str());
        let strings = content.strings();
        let titles = std::cell::RefCell::new(&mut character_pages.titles);
        let character_title = |id| titles.borrow_mut().resolve(strings, id);
        let names = ViewerNames {
            tables: &status.names,
            numbers: facts.numbers(),
            character_title: &character_title,
        };
        let mut rows =
            ac_ui::panels::DatRowTemplates::new(&*content, content.font(DEFAULT_FONT_ID));
        let shown = host
            .with_controller::<AppraisalController, _>(EXAMINATION_WINDOW, |c, ui| {
                c.set_player(player, player_name);
                c.apply(ui, view, objects, &spell, &names, &mut rows)
            })
            .unwrap_or(false);
        if shown && opens {
            self.host.root.show_window(EXAMINATION_WINDOW);
        }
    }

    /// OpenAC `AppraisalUiController.Tick`: while the creature page shows
    /// in combat, it asks again about the creature every 0.75 seconds.
    fn tick_examination(&mut self, dt: f64) {
        let in_combat = self.facts.state.combat_mode != ac_core::combat::CombatMode::NON_COMBAT;
        let due = self
            .host
            .with_controller::<AppraisalController, _>(EXAMINATION_WINDOW, |c, _| {
                c.set_in_combat(in_combat);
                c.refresh_due(dt)
            })
            .unwrap_or(false);
        if due {
            self.host
                .root
                .emit(UiOutput::Panel(PanelOutput::Session(SessionCommand::Item(
                    ItemCommand::RefreshCurrentAppraisal,
                ))));
        }
    }

    /// The session's open questions (the server's and the client's own)
    /// show as the dialog until answered or withdrawn.
    fn follow_confirmation(&mut self) {
        let open = self.facts.state.confirmations.clone();
        let layout_id = self.dialog_layout;
        let Self {
            host,
            content,
            dialogs,
            ..
        } = self;
        let mut make = |ui: &mut UiRoot, kind| make_dialog(ui, content, layout_id, kind);
        dialogs.withdraw(
            &mut host.root,
            |a| matches!(a, DialogAction::Session(c) if !open.contains(c)),
            &mut make,
        );
        for question in open {
            let asked = matches!(dialogs.open_action(), Some(DialogAction::Session(c)) if *c == question)
                || dialogs.is_waiting(|a| matches!(a, DialogAction::Session(c) if *c == question));
            if asked {
                continue;
            }
            let text = match &question {
                ClientConfirmation::Server {
                    confirmation_type,
                    message,
                    ..
                } => {
                    let strings = content.strings();
                    confirmation_text(*confirmation_type, message, |kind, name| {
                        let key = match kind {
                            1 => "ID_Allegiance_AcceptSwearConfirmation",
                            4 => "ID_Fellowship_FellowshipRequest",
                            _ => return None,
                        };
                        let variables = [(PLAYER_VARIABLE, name.to_owned())].into_iter().collect();
                        strings.resolve_template(UI_STRING_TABLE, key, &variables)
                    })
                }
                client => client.text().to_owned(),
            };
            dialogs.ask(
                &mut host.root,
                text,
                DialogAction::Session(question),
                &mut make,
            );
        }
        dialogs.tick(&mut host.root, &mut make);
        // A dialog the catalog cannot make is retried every frame; its
        // failure is reported once.
        let failures = dialogs.take_failures();
        self.notices.extend(failures);
    }

    /// One UI frame at `now` for a screen of `size` pixels: tick, then
    /// draw.
    pub fn frame(&mut self, now: Instant, size: (u32, u32)) -> DrawList {
        let dt = self
            .last_tick
            .replace(now)
            .map_or(0.0, |t| now.duration_since(t).as_secs_f64());
        let now_ms =
            i64::try_from(now.duration_since(self.started).as_millis()).unwrap_or(i64::MAX);
        self.tick_status(dt, size);
        self.tick_plugin_ui(now);
        self.tick_examination(dt);
        self.tick_keyboard_dialogs();
        self.tick_tooltips(dt, now_ms);
        let list = self
            .host
            .draw((size.0 as f32, size.1 as f32), &self.content);
        // What the plugin windows drew of their file icons goes to the
        // session, which holds those and lets go of the rest.
        self.report_drawn_file_icons();
        list
    }

    /// How many framebuffer pixels one UI unit covers.
    pub fn set_pixels_per_unit(&mut self, pixels: f32) {
        self.host.root.set_pixels_per_unit(pixels);
        self.follow_display_density(pixels);
    }

    /// The textures the UI names: dat surfaces and composed icons.
    #[must_use]
    pub fn surfaces(&self) -> DatSurfaces<'_> {
        DatSurfaces {
            dats: self.content.dats(),
            images: &self.images,
        }
    }

    /// A key went down (`text`: what it typed) or up, UI first: a key the
    /// UI leaves and nothing focused may be a panel hotkey (F-keys, the
    /// quick slots, the combat toggle) before it reaches the world.
    #[allow(clippy::too_many_arguments)]
    pub fn key<K>(
        &mut self,
        physical: K,
        key: Key,
        pressed: bool,
        repeat: bool,
        modifiers: Modifiers,
        text: Option<&str>,
    ) -> Routed<K> {
        let mut routed = ui_input::key(
            &mut self.host,
            physical,
            key,
            pressed,
            repeat,
            modifiers,
            text,
        );
        if matches!(routed.world, Some(WorldInput::KeyDown { .. }))
            && !repeat
            && !self.pregame_active()
            && let Some(hotkey) = hotkey(key, modifiers)
            && self.run_hotkey(hotkey)
        {
            routed.world = None;
            routed.outputs.extend(self.host.take_outputs());
        }
        routed
    }

    /// Carry out a hotkey; false when it did nothing (the world gets the
    /// key).
    pub fn run_hotkey(&mut self, hotkey: Hotkey) -> bool {
        match hotkey {
            Hotkey::Spellbook(page) => self.open_spellbook(page),
            Hotkey::Character(tab) => self.open_character(tab),
            Hotkey::Inventory => {
                self.panels.toggle(&mut self.host.root, panel_id::INVENTORY);
                true
            }
            Hotkey::Social(page) => self.open_social(page),
            Hotkey::ToggleCombat => {
                self.host
                    .root
                    .emit(UiOutput::Panel(PanelOutput::ToggleCombat));
                true
            }
            Hotkey::Spell(key) => self.spell_key(key),
            Hotkey::Toolbar(key) => self.toolbar_key(key),
            Hotkey::Options => self.toggle_options(),
            Hotkey::WorldPanel => self.toggle_map_house(),
        }
    }

    fn spell_key(&mut self, key: SpellKey) -> bool {
        self.host
            .with_controller::<SpellcastingController, _>(COMBAT_WINDOW, |c, ui| {
                c.handle_key(ui, key)
            })
            .unwrap_or(false)
    }

    fn toolbar_key(&mut self, key: ToolbarKey) -> bool {
        let selected = self.player_view.and_then(|v| v.selected);
        let Self { host, facts, .. } = self;
        let state = &facts.state;
        let view = ItemsView {
            selected,
            target_mode: state.mode != InteractionMode::None,
            target_source: state.mode.source(),
            shortcuts: Some(&state.shortcuts),
            combat_mode: state.combat_mode,
            ..ItemsView::new(&facts.objects, state.player)
        };
        host.with_controller::<ToolbarWindowController, _>(TOOLBAR_WINDOW, |c, ui| {
            c.toolbar.handle_key(ui, &view, key)
        })
        .unwrap_or(false)
    }

    /// OpenAC `RetailUiRuntime.OpenSpellbook`: shown on that page, it
    /// closes; else it opens there.
    fn open_spellbook(&mut self, page: SpellbookPage) -> bool {
        let visible = self.host.root.is_window_visible(SPELLBOOK_WINDOW);
        let Some(on_page) = self
            .host
            .with_controller::<SpellbookWindowController, _>(SPELLBOOK_WINDOW, |c, _| {
                c.page() == page
            })
        else {
            return false;
        };
        if visible && on_page {
            self.panels
                .set_visibility(&mut self.host.root, panel_id::MAGIC, false);
            return true;
        }
        self.host
            .with_controller::<SpellbookWindowController, _>(SPELLBOOK_WINDOW, |c, ui| {
                c.show_page(ui, page);
            });
        self.panels
            .set_visibility(&mut self.host.root, panel_id::MAGIC, true);
        true
    }

    /// OpenAC `RetailUiRuntime.OpenCharacterPanel`.
    pub(super) fn open_character(&mut self, tab: CharacterTab) -> bool {
        let visible = self.host.root.is_window_visible(CHARACTER_WINDOW);
        let Some(on_tab) = self
            .host
            .with_controller::<CharacterStatController, _>(CHARACTER_WINDOW, |c, _| {
                c.current_tab() == tab
            })
        else {
            return false;
        };
        if visible && on_tab {
            self.panels
                .set_visibility(&mut self.host.root, panel_id::CHARACTER, false);
            return true;
        }
        self.host
            .with_controller::<CharacterStatController, _>(CHARACTER_WINDOW, |c, ui| {
                c.show_tab(ui, tab);
            });
        self.panels
            .set_visibility(&mut self.host.root, panel_id::CHARACTER, true);
        true
    }

    /// Act on the UI's outputs: the clipboard, the window's own panels and
    /// dialogs here, and the session commands returned for the window to
    /// send. The world's fall-through outputs were taken by the input
    /// router already.
    pub fn handle(&mut self, outputs: Vec<UiOutput>) -> Vec<SessionCommand> {
        let mut commands = Vec::new();
        for output in outputs {
            match output {
                UiOutput::Panel(panel) => self.panel_output(panel, &mut commands),
                UiOutput::Window { name, event } => self.window_event(&name, &event),
                UiOutput::Element { element, message }
                    if !self.pregame_message(element, &message, &mut commands)
                        && !self.keyboard_dialog_message(element, &message) =>
                {
                    self.dialog_message(element, &message, &mut commands);
                }
                UiOutput::SetClipboard(text) => {
                    if let Err(Some(e)) = self.clipboard.set_text(&text) {
                        self.notices.push(format!("ui: clipboard: {e}"));
                    }
                }
                UiOutput::PasteRequest(field) => match self.clipboard.text() {
                    Ok(text) => {
                        self.host.root.paste(field, &text);
                        self.host.pump();
                    }
                    Err(Some(e)) => self.notices.push(format!("ui: clipboard: {e}")),
                    Err(None) => {}
                },
                // An item let go over the world: the window picks there.
                UiOutput::WorldDragStart { x, y } => {
                    self.status.world_drag_starts.push((x, y));
                }
                UiOutput::DragReleasedOutsideUi { payload, x, y } => {
                    if let Some(item) = payload.get::<ac_ui::event::ItemDragPayload>() {
                        self.status.world_drops.push((item.clone(), x, y));
                    }
                }
                _ => {}
            }
        }
        // Acting on a request can make the panels ask for more.
        let more = self.host.take_outputs();
        if !more.is_empty() {
            commands.extend(self.handle(more));
        }
        commands.extend(self.take_plugin_commands());
        commands
    }

    fn panel_output(&mut self, output: PanelOutput, commands: &mut Vec<SessionCommand>) {
        if let Some(command) = session_command(&output) {
            commands.push(command);
            return;
        }
        if self.dealings_output(&output) {
            return;
        }
        match output {
            PanelOutput::TogglePanel(panel) => {
                self.panels.toggle(&mut self.host.root, panel);
            }
            PanelOutput::ClosePanel(panel) => {
                self.panels
                    .set_visibility(&mut self.host.root, panel, false);
            }
            PanelOutput::CloseExamination => {
                self.host.root.hide_window(EXAMINATION_WINDOW);
            }
            PanelOutput::FavoriteSpell(spell) => {
                self.host
                    .with_controller::<SpellcastingController, _>(COMBAT_WINDOW, |c, ui| {
                        c.add_favorite(ui, spell);
                    });
            }
            PanelOutput::ExamineSpell(spell) => self.examine_spell(spell),
            PanelOutput::ChessResignAnswered(confirmed) => {
                self.chess_resign_answered(confirmed, commands);
            }
            PanelOutput::Confirm { message, on_yes } => {
                self.ask(message, DialogAction::Panel(*on_yes))
            }
            PanelOutput::ConfirmEither {
                message,
                on_yes,
                on_no,
            } => self.ask(message, DialogAction::Either(*on_yes, *on_no)),
            PanelOutput::TrainSkill { skill, credits } => {
                let name = self
                    .host
                    .with_controller::<CharacterStatController, _>(CHARACTER_WINDOW, |c, _| {
                        c.sheet().skill(skill).map(|s| s.name.clone())
                    })
                    .flatten()
                    .unwrap_or_else(|| format!("skill {skill}"));
                self.ask(
                    train_skill_message(credits, &name),
                    DialogAction::Train { skill, credits },
                );
            }
            PanelOutput::Options(output) => self.options_output(output, commands),
            PanelOutput::KeyboardConfig(output) => self.keyboard_output(output),
            PanelOutput::FlashFigure {
                whole_figure,
                part_mask,
            } => self.viewports.flash_figure(whole_figure, part_mask),
            PanelOutput::ToggleFloatingChat(n) => {
                self.toggle_floating_chat(n);
            }
            _ => {}
        }
    }

    pub(super) fn ask(&mut self, text: String, action: DialogAction) {
        let layout_id = self.dialog_layout;
        let Self {
            host,
            content,
            dialogs,
            ..
        } = self;
        dialogs.ask(&mut host.root, text, action, |ui, kind| {
            make_dialog(ui, content, layout_id, kind)
        });
        // A question the catalog cannot make waits; the factory's failure
        // line is reported with the frame's dialog tick.
    }

    fn dialog_message(
        &mut self,
        element: ac_ui::tree::ElementId,
        message: &ac_ui::root::ElementMessage,
        commands: &mut Vec<SessionCommand>,
    ) {
        let layout_id = self.dialog_layout;
        let Self {
            host,
            content,
            dialogs,
            ..
        } = self;
        let Some((answer, action)) =
            dialogs.answer(&mut host.root, element, message, |ui, kind| {
                make_dialog(ui, content, layout_id, kind)
            })
        else {
            return;
        };
        let accept = answer == DialogAnswer::Accept;
        match action {
            DialogAction::Session(confirmation) => {
                commands.push(SessionCommand::AnswerConfirmation {
                    confirmation,
                    accepted: accept,
                });
            }
            DialogAction::Panel(output) if accept => self.panel_output(output, commands),
            DialogAction::Panel(_) => {}
            DialogAction::Either(yes, no) => {
                self.panel_output(if accept { yes } else { no }, commands);
            }
            DialogAction::Train { skill, credits } if accept => {
                commands.push(SessionCommand::Character(CharacterCommand::TrainSkill {
                    skill,
                    credits,
                }));
            }
            DialogAction::Train { .. } => {
                self.host.with_controller::<CharacterStatController, _>(
                    CHARACTER_WINDOW,
                    |c, ui| {
                        c.release_raise(ui);
                    },
                );
            }
        }
    }

    /// A window was shown, hidden, moved or resized: the toolbar's panel
    /// buttons and the main panels follow.
    fn window_event(&mut self, name: &str, event: &WindowEvent) {
        let visible = match event {
            WindowEvent::Shown => true,
            WindowEvent::Hidden => false,
            WindowEvent::Moved | WindowEvent::Resized => {
                self.panels.observe_geometry(&mut self.host.root, name);
                return;
            }
            _ => return,
        };
        // Events are handled after the fact: one that no longer matches
        // the window (shown, then hidden again before this) is stale.
        if self.host.root.is_window_visible(name) != visible {
            return;
        }
        if visible && let Some(name) = self.mounted.iter().find(|n| **n == name) {
            // Fed on the next frame, whatever changed while it was hidden.
            self.stale.push(name);
        }
        self.floating_chat_shown(name, visible);
        self.book_shown(name, visible);
        self.journal_shown(name, visible);
        self.panels
            .observe_visibility(&mut self.host.root, name, visible);
        if let Some(panel) = self.panels.panel_of(name) {
            self.host
                .with_controller::<ToolbarWindowController, _>(TOOLBAR_WINDOW, |c, ui| {
                    c.toolbar.set_panel_open(ui, panel, visible);
                });
        }
    }

    /// An examined spell's components as the player would cast it, each
    /// with its name, icon and whether the player carries one.
    fn spell_examine_components(&mut self, spell: &SpellMetadata) -> Vec<SpellExamineComponent> {
        let mut components = Vec::new();
        let Some(table) = &self.components else {
            return components;
        };
        let world = ac_runtime::gameplay::magic::TableComponentWorld {
            objects: &self.facts.objects,
            player: self.facts.state.player,
        };
        let formula = ac_runtime::gameplay::magic::examine_components(
            &self.spell_components,
            &world,
            &self.facts.state.account,
            spell,
        );
        for (id, owned) in formula {
            // A component the table does not describe is left out.
            let Some((_, base)) = table.components.iter().find(|(c, _)| *c == id) else {
                continue;
            };
            components.push(SpellExamineComponent {
                name: base.name.clone(),
                icon: ac_ui::icons::IconSource::component_icon(&mut self.icons, base.icon),
                owned,
            });
        }
        components
    }

    /// The player's items changed while the examination window shows a
    /// spell: its components again, as they stand now.
    fn refresh_examined_spell(&mut self, dirty: Dirty) {
        if !dirty.items {
            return;
        }
        let examined = self
            .host
            .with_controller::<AppraisalController, _>(EXAMINATION_WINDOW, |c, _| {
                (c.page() == AppraisalPage::Spell).then(|| c.spell())
            })
            .flatten()
            .filter(|&id| id != 0);
        let Some(spell) = examined.and_then(|id| {
            self.facts
                .numbers()
                .and_then(|n| n.spellbook())
                .and_then(|b| b.try_get_metadata(id))
                .cloned()
        }) else {
            return;
        };
        let components = self.spell_examine_components(&spell);
        let Self { host, content, .. } = self;
        let mut cells =
            ac_ui::panels::DatRowTemplates::new(&*content, content.font(DEFAULT_FONT_ID));
        host.with_controller::<AppraisalController, _>(EXAMINATION_WINDOW, |c, ui| {
            c.refresh_spell_components(ui, &spell, &components, &mut cells);
        });
    }

    /// A spell examined from the spellbook or a spell bar: the examination
    /// window's spell page, with no server request.
    fn examine_spell(&mut self, spell_id: u32) {
        let Some(numbers) = self.facts.numbers() else {
            return;
        };
        let Some(spell) = numbers
            .spellbook()
            .and_then(|b| b.try_get_metadata(spell_id))
            .cloned()
        else {
            return;
        };
        let magic_skill = magic_skill(numbers, &spell.school);
        let icon = ac_ui::icons::IconSource::spell_icon(&mut self.icons, spell_id);
        let components = self.spell_examine_components(&spell);
        let Self { host, content, .. } = self;
        let mut cells =
            ac_ui::panels::DatRowTemplates::new(&*content, content.font(DEFAULT_FONT_ID));
        let shown = host
            .with_controller::<AppraisalController, _>(EXAMINATION_WINDOW, |c, ui| {
                c.examine_spell(ui, &spell, icon, magic_skill, &components, &mut cells);
            })
            .is_some();
        if shown {
            self.host.root.show_window(EXAMINATION_WINDOW);
        }
    }

    /// One scripted input step through the UI's own routing (the world's
    /// part is dropped): a diagnostic for unattended live checks, where no
    /// keyboard or mouse reaches the window. Returns the
    /// UI's outputs for [`WindowUi::handle`].
    pub fn script(&mut self, step: &UiScript) -> Vec<UiOutput> {
        let mut outputs = Vec::new();
        match *step {
            UiScript::Key { key, modifiers } => {
                for pressed in [true, false] {
                    let routed: Routed<()> = self.key((), key, pressed, false, modifiers, None);
                    outputs.extend(routed.outputs);
                    // What the UI let through goes to the interface's actions of
                    // the retail key map (the world's part is dropped).
                    if pressed
                        && matches!(routed.world, Some(WorldInput::KeyDown { .. }))
                        && let Some(binding) = ac_ui::input::KeyBindings::retail_defaults().find(
                            ac_ui::input::KeyChord::key(key, modifiers),
                            ac_ui::input::ActivationType::Press,
                        )
                        && self.handle_input_action(binding.action)
                    {
                        outputs.extend(self.host.take_outputs());
                    }
                }
            }
            UiScript::Click {
                x,
                y,
                button,
                double,
            } => {
                let number = button;
                let routed: Routed<()> = ui_input::moved(&mut self.host, x, y, false);
                outputs.extend(routed.outputs);
                for _ in 0..if double { 2 } else { 1 } {
                    for pressed in [true, false] {
                        let held = if pressed { vec![number] } else { Vec::new() };
                        let flags = ui_input::mouse_flags(&held, Modifiers::NONE);
                        let routed: Routed<()> =
                            ui_input::button(&mut self.host, number, pressed, x, y, flags, false);
                        outputs.extend(routed.outputs);
                    }
                }
            }
            UiScript::Press { x, y } => {
                let routed: Routed<()> = ui_input::moved(&mut self.host, x, y, false);
                outputs.extend(routed.outputs);
                let held = ui_input::mouse_flags(&[0], Modifiers::NONE);
                let routed: Routed<()> =
                    ui_input::button(&mut self.host, 0, true, x, y, held, false);
                outputs.extend(routed.outputs);
            }
            UiScript::Move { x, y } => {
                let routed: Routed<()> = ui_input::moved(&mut self.host, x, y, false);
                outputs.extend(routed.outputs);
            }
            UiScript::Release { x, y } => {
                let routed: Routed<()> = ui_input::moved(&mut self.host, x, y, false);
                outputs.extend(routed.outputs);
                let routed: Routed<()> = ui_input::button(&mut self.host, 0, false, x, y, 0, false);
                outputs.extend(routed.outputs);
            }
            UiScript::Probe => {
                let lines = self.probe();
                self.notices.extend(lines);
            }
            // The window drives these through its own input path.
            UiScript::Pointer { .. } | UiScript::PointerClick { .. } => {}
        }
        outputs
    }

    /// Where the shown windows, their item cells, spell cells, stat rows,
    /// buttons (with their captions) and sliders are on the screen (whole
    /// pixels), one line each: what an unattended check clicks on.
    #[must_use]
    pub fn probe(&self) -> Vec<String> {
        use ac_ui::panels::character::StatRow;
        use ac_ui::widgets::{Button, CatalogSlot, ItemSlot, Scrollbar};
        let root = &self.host.root;
        let rect = |id| {
            let (x, y) = root.screen_position(id);
            let (w, h) = root.element(id).map_or((0.0, 0.0), |e| (e.width, e.height));
            format!("{} {} {} {}", x as i32, y as i32, w as i32, h as i32)
        };
        fn shown(root: &UiRoot, id: ac_ui::tree::ElementId) -> bool {
            let mut at = Some(id);
            while let Some(e) = at {
                if !root.element(e).is_some_and(|e| e.visible) {
                    return false;
                }
                at = root.parent(e);
            }
            true
        }
        let mut lines = Vec::new();
        for window in root.windows() {
            if !root.is_window_visible(&window.name) {
                continue;
            }
            lines.push(format!(
                "ui-probe: window {} {}",
                window.name,
                rect(window.frame)
            ));
            let mut stack = vec![window.frame];
            let mut rows = 0;
            while let Some(id) = stack.pop() {
                let mut children = root.children(id);
                children.reverse();
                stack.extend(children);
                if !shown(root, id) {
                    continue;
                }
                let name = &window.name;
                if let Some(s) = root.widget::<ItemSlot>(id)
                    && s.object_id != 0
                {
                    lines.push(format!(
                        "ui-probe: slot {name} {:#010x} {}",
                        s.object_id,
                        rect(id)
                    ));
                } else if let Some(s) = root.widget::<CatalogSlot>(id)
                    && s.entry_id != 0
                {
                    lines.push(format!(
                        "ui-probe: spell {name} {} {}",
                        s.entry_id,
                        rect(id)
                    ));
                } else if root.widget::<StatRow>(id).is_some() {
                    lines.push(format!("ui-probe: row {name} {rows} {}", rect(id)));
                    rows += 1;
                } else if let Some(b) = root.widget::<Button>(id) {
                    let dat = root.element(id).map_or(0, |e| e.dat_element_id);
                    if dat != 0 {
                        let label = b
                            .label
                            .as_deref()
                            .map_or_else(String::new, |l| format!(" \"{l}\""));
                        lines.push(format!(
                            "ui-probe: button {name} {dat:#010x} {}{label}",
                            rect(id)
                        ));
                    }
                } else if root.widget::<Scrollbar>(id).is_some_and(|s| s.scalar) {
                    let dat = root.element(id).map_or(0, |e| e.dat_element_id);
                    lines.push(format!("ui-probe: slider {name} {dat:#010x} {}", rect(id)));
                }
            }
        }
        // The open dialogs (a question such as `/die`'s), which are no
        // registered window: OpenAC's probe dump walks the whole tree, so
        // its dialogs and their buttons are listed too.
        let dialogs = self.dialogs.factory();
        let open = dialogs
            .open_contexts()
            .iter()
            .filter_map(|&c| dialogs.view(c).map(|v| v.root()))
            .chain(self.keyboard.open_dialog_roots());
        for dialog in open.collect::<Vec<_>>() {
            if shown(root, dialog) {
                self.probe_screen(&mut lines, "dialog", dialog);
            }
        }
        self.probe_pregame(&mut lines);
        lines
    }

    /// Everything queued since the last input, handled.
    pub fn drain(&mut self) -> Vec<SessionCommand> {
        let outputs = self.host.take_outputs();
        self.handle(outputs)
    }

    /// The layout source (tests).
    #[must_use]
    pub fn layouts(&self) -> &dyn LayoutSource {
        &self.content
    }
}

/// The appraisal names with the viewer's own skills (OpenAC's appraisal
/// binding: the effective level and the skill's training).
struct ViewerNames<'a> {
    tables: &'a ac_ui::panels::appraisal::NameTables,
    numbers: Option<&'a ac_core::player::LocalPlayerState>,
    character_title: &'a dyn Fn(u32) -> Option<String>,
}

impl ac_ui::panels::appraisal::AppraisalNames for ViewerNames<'_> {
    fn material(&self, material: i32) -> String {
        self.tables.material(material)
    }

    fn creature(&self, creature: i32) -> String {
        self.tables.creature(creature)
    }

    fn skill(&self, skill: i32) -> Option<String> {
        self.tables.skill(skill)
    }

    fn player_skill(&self, skill: u32) -> ac_ui::panels::appraisal::AppraisalPlayerSkill {
        let Some(numbers) = self.numbers else {
            return Default::default();
        };
        ac_ui::panels::appraisal::AppraisalPlayerSkill {
            level: numbers.get_effective_skill(skill).unwrap_or(0),
            advancement_class: numbers.get_skill(skill).map_or(0, |s| s.status),
        }
    }

    fn character_title(&self, title_id: u32) -> Option<String> {
        (self.character_title)(title_id)
    }

    // OpenAC `InteractionRetainedUiComposition`: the local player's
    // Faction1Bits.
    fn local_faction_bits(&self) -> i32 {
        self.numbers
            .and_then(|n| n.int_properties().get(&281).copied())
            .unwrap_or(0)
    }
}

/// The player's skill in a spell's school (the highest of the five when
/// the school is none of them), as OpenAC's examination binding reads it.
fn magic_skill(numbers: &ac_core::player::LocalPlayerState, school: &str) -> u32 {
    let level = |id| numbers.get_skill(id).map_or(0, |s| s.current_level());
    match school {
        "Creature Enchantment" => level(0x1F),
        "Item Enchantment" => level(0x20),
        "Life Magic" => level(0x21),
        "War Magic" => level(0x22),
        "Void Magic" => level(0x2B),
        _ => [0x1F, 0x20, 0x21, 0x22, 0x2B]
            .into_iter()
            .map(level)
            .max()
            .unwrap_or(0),
    }
}

/// A fresh dialog of `kind` from the catalog (OpenAC
/// `RetailUiRuntime.MountDialogFactory`'s layout maker).
pub(super) fn make_dialog(
    ui: &mut UiRoot,
    content: &DatContent,
    layout_id: Option<u32>,
    kind: ac_ui::panels::dialog_factory::DialogType,
) -> Option<ImportedLayout> {
    let font = content.font(DEFAULT_FONT_ID);
    ac_ui::import::import_rooted(ui, content, layout_id?, kind.root_element_id(), font)
}

#[cfg(test)]
mod tests {
    use super::*;
    use ac_proto::{Quaternion, Vector3};

    #[test]
    fn fellows_and_their_leader_take_their_relationship() {
        // OpenAC `LivePresentationComposition` (`relationshipFor`): a member
        // of the player's fellowship, and its leader, are marked; nothing
        // else is filled in.
        let fellow = |guid| ac_runtime::gameplay::FellowView {
            guid,
            name: String::new(),
            level: 1,
            health: (1, 1),
            stamina: (1, 1),
            mana: (1, 1),
            share_loot: false,
            vitals_age_seconds: None,
        };
        let fellowship = ac_runtime::gameplay::FellowshipView {
            in_fellowship: true,
            leader_guid: 0x5000_0002,
            members: vec![fellow(0x5000_0001), fellow(0x5000_0002)],
            ..Default::default()
        };
        let member = fellowship_relationship(Some(&fellowship), 0x5000_0001);
        assert!(member.is_fellowship_member && !member.is_fellowship_leader);
        let leader = fellowship_relationship(Some(&fellowship), 0x5000_0002);
        assert!(leader.is_fellowship_member && leader.is_fellowship_leader);
        assert_eq!(
            fellowship_relationship(Some(&fellowship), 0x5000_0003),
            RelationshipTraits::default()
        );
        assert_eq!(
            fellowship_relationship(None, 0x5000_0001),
            RelationshipTraits::default()
        );
    }

    #[test]
    fn the_radar_scene_squares_the_players_allegiance() {
        // The session's monarchs reach the radar: an object that shares
        // the player's monarch is drawn as a box; one with another monarch
        // keeps the plus.
        use ac_runtime::session::PresentationEvent;
        use ac_runtime::world::{ObjectKey, ObjectView, RadarFacts, WorldSnapshot};
        const MONARCH: u32 = 0x5000_0777;
        let view = |local_id, guid, y, monarch_id| ObjectView {
            key: ObjectKey {
                local_id,
                incarnation: 1,
            },
            guid,
            name: None,
            setup_id: None,
            position: Some(Position {
                cell_id: 0xA9B4_0031,
                origin: Vector3 {
                    x: 100.0,
                    y,
                    z: 0.0,
                },
                orientation: Quaternion::IDENTITY,
            }),
            step_blend: None,
            parent: None,
            placement: None,
            rest_placement: 0,
            scale: None,
            translucency: None,
            state: 0,
            velocity: None,
            model: ac_proto::ModelData::default(),
            motion_table_id: None,
            sound_table_id: None,
            radar: RadarFacts {
                public_weenie_bitfield: 0x8,
                radar_behavior: Some(4),
                monarch_id,
                ..RadarFacts::default()
            },
            motion: None,
            effects: ac_runtime::world::EffectFacts::default(),
        };
        let mut scene = Scene::default();
        scene.apply(PresentationEvent::Snapshot(WorldSnapshot {
            player_guid: Some(0x5000_0001),
            objects: vec![
                view(1, 0x5000_0001, 100.0, MONARCH),
                view(2, 0x5000_0002, 110.0, MONARCH),
                view(3, 0x5000_0003, 90.0, 0x5000_0778),
            ],
        }));
        let snapshot = ac_ui::panels::radar::build_snapshot(&radar_scene(&scene, None, None));
        let shape = |guid| {
            snapshot
                .blips
                .iter()
                .find(|b| b.object_id == guid)
                .map(|b| b.shape)
        };
        assert_eq!(shape(0x5000_0002), Some(ac_ui::radar::BlipShape::Box));
        assert_eq!(shape(0x5000_0003), Some(ac_ui::radar::BlipShape::Plus));
    }

    #[test]
    fn world_meters_add_the_landblock_corner() {
        let p = Position {
            cell_id: 0xA9B4_0031,
            origin: Vector3 {
                x: 10.0,
                y: 20.0,
                z: 30.0,
            },
            orientation: Quaternion::IDENTITY,
        };
        assert_eq!(
            world_meters(&p),
            Vec3::new(0xA9 as f32 * 192.0 + 10.0, 0xB4 as f32 * 192.0 + 20.0, 30.0)
        );
    }

    #[test]
    fn panel_requests_become_session_commands() {
        // OpenAC `RetailUiRuntime.MountRadar` (a blip click selects through
        // the selection owner) and `RadarController` (the lock button flips
        // the LockUI option). A click on an object's icon selects as the
        // mouse.
        assert!(matches!(
            session_command(&PanelOutput::SelectObject(0x5000_0001)),
            Some(SessionCommand::Gameplay(GameplayCommand::SelectClicked(
                ObjectRef::Guid(0x5000_0001)
            )))
        ));
        assert!(matches!(
            session_command(&PanelOutput::SetUiLocked(true)),
            Some(SessionCommand::SetCharacterOption { option, value: true })
                if option == option_id::LOCK_UI
        ));
        assert!(matches!(
            session_command(&PanelOutput::Session(SessionCommand::Chat("hi".into()))),
            Some(SessionCommand::Chat(line)) if line == "hi"
        ));
        assert!(session_command(&PanelOutput::HoveredObject(Some(1))).is_none());
    }

    #[test]
    fn item_and_character_requests_reach_their_session_commands() {
        // OpenAC: the toolbar's `sendAddShortcut`/`sendRemoveShortcut` and
        // the interaction owner's modes, `CharacterSheetProvider`'s raises;
        // a training request is asked first (no command yet).
        let entry = ac_core::items::ShortcutEntry {
            index: 2,
            object_id: 0x8000_0001,
            spell_id: 0,
        };
        assert!(matches!(
            session_command(&PanelOutput::AddShortcut(entry)),
            Some(SessionCommand::Item(ItemCommand::AddShortcut(e))) if e == entry
        ));
        assert!(matches!(
            session_command(&PanelOutput::RemoveShortcut(2)),
            Some(SessionCommand::Item(ItemCommand::RemoveShortcut(2)))
        ));
        assert!(matches!(
            session_command(&PanelOutput::EnterExamineMode),
            Some(SessionCommand::Item(ItemCommand::EnterExamineMode))
        ));
        // The link panel's ping goes to the session, whose answer gives the
        // round trip.
        assert!(matches!(
            session_command(&PanelOutput::RequestLinkPing),
            Some(SessionCommand::Ping)
        ));
        assert!(matches!(
            session_command(&PanelOutput::RaiseAttribute {
                attribute: 1,
                xp: 500
            }),
            Some(SessionCommand::Character(
                CharacterCommand::RaiseAttribute {
                    attribute: 1,
                    xp: 500
                }
            ))
        ));
        assert!(matches!(
            session_command(&PanelOutput::ToggleCombat),
            Some(SessionCommand::Gameplay(GameplayCommand::ToggleCombat))
        ));
        assert!(
            session_command(&PanelOutput::TrainSkill {
                skill: 33,
                credits: 4
            })
            .is_none()
        );
    }

    #[test]
    fn retail_hotkeys_name_their_panels_and_slots() {
        // OpenAC `KeyBindings.RetailDefaults`.
        let none = Modifiers::NONE;
        // The panel keys go through the key map: F5, F9, F12, F3, F4 and F11
        // are its actions, answered by the interface.
        use super::super::status::hotkey_for_action;
        use ac_ui::input::{ActivationType, KeyBindings, KeyChord};
        let map = KeyBindings::retail_defaults();
        let action = |key| {
            map.find(KeyChord::key(key, none), ActivationType::Press)
                .and_then(|b| hotkey_for_action(b.action))
        };
        assert_eq!(hotkey(Key::F5, none), None);
        assert_eq!(
            action(Key::F5),
            Some(Hotkey::Spellbook(SpellbookPage::Spells))
        );
        assert_eq!(
            action(Key::F9),
            Some(Hotkey::Character(CharacterTab::Skills))
        );
        assert_eq!(action(Key::F12), Some(Hotkey::Inventory));
        assert_eq!(
            action(Key::F3),
            Some(Hotkey::Social(SocialPage::Allegiance))
        );
        assert_eq!(
            action(Key::F4),
            Some(Hotkey::Social(SocialPage::Fellowship))
        );
        assert_eq!(action(Key::F11), Some(Hotkey::Options));
        // The quick slots and the spell bar go through the key map.
        assert_eq!(hotkey(Key::Number3, none), None);
        // A chord fires every action bound to it (Alt+1..4 also toggle the
        // floating chat windows).
        let chord = |key, modifiers| {
            map.all()
                .iter()
                .filter(|b| {
                    b.chord == KeyChord::key(key, modifiers)
                        && b.activation == ActivationType::Press
                })
                .find_map(|b| hotkey_for_action(b.action))
        };
        assert_eq!(
            chord(Key::Number3, none),
            Some(Hotkey::Toolbar(ToolbarKey::Use(2)))
        );
        assert_eq!(
            chord(Key::Number1, Modifiers::ALT),
            Some(Hotkey::Toolbar(ToolbarKey::Use(9)))
        );
        assert_eq!(
            chord(Key::Number9, Modifiers::ALT),
            Some(Hotkey::Toolbar(ToolbarKey::Use(17)))
        );
        assert_eq!(
            chord(Key::Number0, none),
            Some(Hotkey::Toolbar(ToolbarKey::CreateShortcut))
        );
        let magic = map
            .find_in(
                KeyChord::key(Key::Number3, none),
                ActivationType::Press,
                ac_ui::input::InputScope::MagicCombat,
            )
            .and_then(|b| hotkey_for_action(b.action));
        assert_eq!(magic, Some(Hotkey::Spell(SpellKey::UseSlot(2))));
        assert_eq!(hotkey(Key::GraveAccent, none), Some(Hotkey::ToggleCombat));
        assert_eq!(hotkey(Key::W, none), None);
        assert_eq!(hotkey(Key::Number3, Modifiers::SHIFT), None);
    }
}
