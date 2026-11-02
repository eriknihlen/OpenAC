//! The plugins' UI in the game window (OpenAC `RetailUiRuntime`'s plugin
//! panel host, `PluginShelfController`, `PluginCanvasElement`, the world
//! label, world line and projectile overlays): the window's copy of the
//! session's plugin UI (`InterfaceFacts::plugin_ui`) drawn with the
//! widgets of `ac_ui::plugin`, and what the player does with it sent back
//! as session commands.
//!
//! Nothing here is kept beyond what the copy rebuilds: a window that
//! attaches mid-run is handed the session's snapshot and mounts every
//! plugin window from it.

use std::collections::{BTreeMap, HashMap};
use std::rc::Rc;
use std::sync::Arc;
use std::time::{Duration, Instant};

use ac_dat::DatCollection;
use ac_dat::objects::{BspNode, GfxObj, Setup};
use ac_host::settings::{MarkupControlKey, SettingsStore};
use ac_plugin_ui::control_state::{ControlKey, ControlStateFile, ControlStateStore};
use ac_plugin_ui::{
    ClientWindow, ClientWindowAction, ImageSource, MapPoint, MarkerAnchor, PanelView, ThemeKind,
    UiInput, WorldIconEntry,
};
use ac_proto::Quaternion;
use ac_runtime::session::SessionCommand;
use ac_ui::DatContent;
use ac_ui::bundled_font::{self, BundledFontError, ModernFonts, Weight};
use ac_ui::draw::{Color, SpriteSizes, TextureRef};
use ac_ui::font::{DEFAULT_FONT_ID, SharpFont, SharpFonts, UiFont};
use ac_ui::icons::{IconComposer, IconSource};
use ac_ui::import::LayoutSource;
use ac_ui::overlay::{WorldIcon, WorldIconAnchor, WorldLabel, WorldLabelAnchor};
use ac_ui::panels::projectile_debug::{ProjectileDebugOverlay, ProjectileDebugSample};
use ac_ui::plugin::canvas::{CanvasFont, CanvasLayer};
use ac_ui::plugin::dock::{self, DockArt, DockMode, DockOpens, DockSize, PluginDock};
use ac_ui::plugin::fold::SharedControlState;
use ac_ui::plugin::{
    Icon, MarkupIcons, PluginOutbox, PluginPanelController, PluginTheme, build_panel,
    build_panel_with_state,
};
use glam::{Quat, Vec3};

use super::character_pages::CHARACTER_INFO_WINDOW;
use super::indicator_panels::{
    LINK_STATUS_WINDOW, NEGATIVE_EFFECTS_WINDOW, POSITIVE_EFFECTS_WINDOW, VITAE_WINDOW,
};
use super::map_window::MAP_HOUSE_WINDOW;
use super::options::OPTIONS_WINDOW;
use super::overlays::Placement;
use super::social_panels::{JOURNAL_WINDOW, SOCIAL_WINDOW};
use super::window::{CHARACTER_WINDOW, INVENTORY_WINDOW, RADAR_WINDOW, SPELLBOOK_WINDOW, WindowUi};
use crate::meshing::flatten_setup;
use crate::renderer::WorldRenderer;
use crate::renderer::icon_probes::{IconProbe, IconProbeKey};
use crate::scene::Scene;
use crate::world_lines::PlacedLine;
use crate::world_shapes::{self, PlacedShape, ShapeAnchor};
use crate::world_triangles::PlacedTriangle;

/// How long a set of projectile markers shows after it arrives.
pub const PROJECTILE_SAMPLES_SHOWN: Duration = Duration::from_millis(350);

/// A label hangs this high over an object nothing else gives a height,
/// metres (about a standing human).
pub const FALLBACK_LABEL_HEIGHT: f32 = 1.1;

/// OpenAC's crescent on a clear background, which the plugin dock's tile shows
/// on every theme's fill.
const APP_ICON_PNG: &[u8] = include_bytes!("../../../../assets/icons/crescent-256.png");
/// The size of the count on the dock's tile, pixels.
const DOCK_COUNT_FONT_SIZE: f32 = 10.0;
/// The size of a monogram's letters on the smaller dock, pixels.
const DOCK_SMALLER_LETTERS: f32 = 12.0;
/// The size of a monogram's letters on the bigger dock, pixels.
const DOCK_BIGGER_LETTERS: f32 = 20.0;

/// The size composed item and spell icons are drawn at, pixels.
const ICON_SIZE: f32 = 32.0;

/// The window's name for one of its own windows a plugin may ask about.
#[must_use]
pub fn client_window_name(window: ClientWindow) -> &'static str {
    match window {
        ClientWindow::Inventory => INVENTORY_WINDOW,
        ClientWindow::Character => CHARACTER_WINDOW,
        ClientWindow::CharacterInformation => CHARACTER_INFO_WINDOW,
        ClientWindow::Spellbook => SPELLBOOK_WINDOW,
        ClientWindow::Map => MAP_HOUSE_WINDOW,
        ClientWindow::Options => OPTIONS_WINDOW,
        ClientWindow::Social => SOCIAL_WINDOW,
        ClientWindow::Journal => JOURNAL_WINDOW,
        ClientWindow::PositiveEffects => POSITIVE_EFFECTS_WINDOW,
        ClientWindow::NegativeEffects => NEGATIVE_EFFECTS_WINDOW,
        ClientWindow::LinkStatus => LINK_STATUS_WINDOW,
        ClientWindow::Vitae => VITAE_WINDOW,
        ClientWindow::Radar => RADAR_WINDOW,
    }
}

/// The first cell number inside a building or dungeon.
const FIRST_INDOOR_CELL: u32 = 0x0100;

/// A sharper bake of the bundled font a display wants: the font, its weight
/// and size, and the scale to bake it at.
#[derive(Debug)]
struct SharpWanted {
    font: Arc<UiFont>,
    weight: Weight,
    size: f32,
    scale: f32,
}

impl SharpWanted {
    /// The font and the scale in quarter steps.
    fn key(&self) -> (u32, u32) {
        (self.font.id, (self.scale * 4.0) as u32)
    }
}

/// A plugin's icon placed this frame: which of the merged icons, and where.
#[derive(Debug, Clone, Copy)]
struct PlacedIcon {
    entry: usize,
    anchor: WorldIconAnchor,
}

/// What the window keeps of the plugins' UI.
#[derive(Debug, Default)]
pub struct PluginWindowUi {
    outbox: PluginOutbox,
    /// The plugin windows mounted, by the session's number, with their
    /// window names.
    panels: BTreeMap<u32, String>,
    dock: bool,
    canvases: Option<CanvasLayer>,
    projectile: Option<ProjectileDebugOverlay>,
    /// The plugins' images as textures, by plugin and handle, each with
    /// whether this window uploaded it (and gives it back when the plugin
    /// lets go of the image).
    images: HashMap<(String, u32), (Icon, bool)>,
    /// The PNGs on the plugin dock as textures, uploaded once per picture
    /// (OpenAC `RetailUiRuntime.ResolvePluginFileIcon`), by the pixels each was
    /// made from, which an entry keeps alive so its key stays that picture's. A
    /// picture nothing holds any more is let go.
    file_icons: HashMap<usize, (Arc<Vec<u8>>, Icon)>,
    /// The file icons each plugin window draws, as the session was last
    /// told them.
    files_shown: HashMap<u32, Vec<String>>,
    synced: Option<u64>,
    reported: Option<(u16, u16)>,
    /// The key map's chords as last told to the session, with the
    /// snapshot (attach) they were told in.
    reported_chords: Option<(u64, Vec<ac_plugin_ui::hotkeys::Chord>)>,
    /// The first chord the key map binds to Toggle Plugin Manager.
    manager_chord: Option<ac_ui::input::KeyChord>,
    /// The bundled sans font the modern themes draw opted-in windows in
    /// (OpenAC `BundledUiFont.Load`), baked when the plugin UI is mounted.
    modern_fonts: Option<ModernFonts>,
    /// The dock's own sizes of the bundled SemiBold, each with its size.
    dock_fonts: Vec<(Arc<UiFont>, f32)>,
    /// The display density the sharper bakes of those fonts were last chosen
    /// for.
    sharp_density: Option<f32>,
    /// The atlases of the fonts plugins hold, by bake: a bake the bundled font
    /// shares between plugins is one texture.
    font_textures: HashMap<usize, (Arc<ac_plugin_ui::fonts::FontBake>, TextureRef)>,
    /// Those sharper bakes for the display density in use, by font and scale
    /// in quarter steps. A bake for another density is let go of when the
    /// density changes.
    sharp_bakes: HashMap<(u32, u32), SharpFont>,
    /// The sharper bakes the density in use still wants, baked one a frame:
    /// each takes several milliseconds, and its font draws from its own atlas
    /// until then.
    sharp_pending: Vec<SharpWanted>,
    /// A markup window's title colour without a theme, from the player's
    /// `controlsntrols.ini` (`[title] color`); white without one.
    title_color: Option<Color>,
    theme: PluginTheme,
    /// The theme as the player picked it (what the session is told).
    theme_kind: ThemeKind,
    /// The Appearance window, once the player opened it.
    appearance: Option<Appearance>,
    /// This frame's labels, the objects they hang from, the lines and the
    /// projectile markers, in the drawing frame.
    pub(super) labels: Vec<WorldLabel>,
    pub(super) anchors: HashMap<u32, WorldLabelAnchor>,
    world_lines: Vec<PlacedLine>,
    world_triangles: Vec<PlacedTriangle>,
    world_shapes: Vec<PlacedShape>,
    /// This frame's icons: none while the portal view hides the world.
    world_icons: Vec<PlacedIcon>,
    /// The anchors of the icons hidden behind the world, to test this frame.
    icon_probes: Vec<IconProbe>,
    icon_entries: Arc<Vec<WorldIconEntry>>,
    samples: Vec<ProjectileDebugSample>,
    projectile_set: Option<(u64, Instant)>,
    setup_shapes: HashMap<u32, Option<SetupShape>>,
    /// What the plugin windows' controls remember for the character (an
    /// expander folded, a split moved, a list sorted), with the character
    /// it is kept for.
    control_state: Option<(SharedControlState, Rc<std::cell::RefCell<String>>)>,
    /// The plugins' dialogs and modal windows on screen.
    dialogs: ac_ui::plugin::dialog::DialogPresenter,
}

/// The settings file as the control-state store's file.
struct ControlStateSettings(SettingsStore);

impl ControlStateFile for ControlStateSettings {
    fn load(&self, character: &str) -> Result<BTreeMap<ControlKey, String>, String> {
        Ok(self
            .0
            .try_load_markup_control_state(character)?
            .into_iter()
            .map(|(k, v)| (ControlKey::new(&k.window, &k.control, &k.kind), v))
            .collect())
    }

    fn save(&self, character: &str, values: &BTreeMap<ControlKey, String>) -> Result<(), String> {
        let values = values
            .iter()
            .map(|(k, v)| {
                (
                    MarkupControlKey::new(&k.window, &k.control, &k.kind),
                    v.clone(),
                )
            })
            .collect();
        self.0
            .save_markup_control_state(character, &values)
            .map_err(|e| e.to_string())
    }
}

impl PluginWindowUi {
    /// Where `guid`'s markers hang this frame, kept once per frame.
    fn keep_anchor(
        &mut self,
        world: &WorldRenderer,
        scene: &Scene,
        dats: &DatCollection,
        guid: u32,
    ) {
        if !self.anchors.contains_key(&guid)
            && let Some(anchor) = label_anchor(world, scene, dats, &mut self.setup_shapes, guid)
        {
            self.anchors.insert(guid, anchor);
        }
    }

    /// The store, made on first use (values last the session until a
    /// settings file is given).
    fn control_state(&mut self) -> SharedControlState {
        let (store, _) = self.control_state.get_or_insert_with(|| {
            let character = Rc::new(std::cell::RefCell::new(String::new()));
            let key = Rc::clone(&character);
            let started = Instant::now();
            let store = ControlStateStore::new(
                None,
                move || key.borrow().clone(),
                move || u64::try_from(started.elapsed().as_millis()).unwrap_or(u64::MAX),
            );
            (Rc::new(std::cell::RefCell::new(store)), character)
        });
        Rc::clone(store)
    }

    /// Where the remembered control values are kept.
    pub fn set_control_state_file(&mut self, store: Option<SettingsStore>) {
        let shared = self.control_state();
        shared.borrow_mut().set_file(
            store.map(|s| Box::new(ControlStateSettings(s)) as Box<dyn ControlStateFile>),
        );
    }

    /// The character the controls remember for (`default` before one is
    /// in the world, which is never written).
    pub fn set_control_state_character(&mut self, character: &str) {
        self.control_state();
        if let Some((_, key)) = &self.control_state
            && *key.borrow() != character
        {
            *key.borrow_mut() = character.to_owned();
        }
    }

    /// Writes what changed (at most every few seconds after a failure);
    /// the log lines it has.
    pub fn save_control_state(&mut self, last: bool) -> Vec<String> {
        let Some((store, _)) = &self.control_state else {
            return Vec::new();
        };
        let mut store = store.borrow_mut();
        store.save(last);
        store.take_log()
    }
}

/// The Appearance window (OpenAC `RetailUiRuntime.ShowPluginAppearance`):
/// the client's own markup window, built from a plugin UI of its own that
/// only this window holds, offering the themes.
#[derive(Debug)]
struct Appearance {
    ui: ac_plugin_ui::PluginUi,
    panel: u32,
    window: String,
    outbox: PluginOutbox,
    revision: u64,
}

/// The owner the Appearance window's markup is registered under.
const APPEARANCE_OWNER: &str = "openac";
/// Its window id.
const APPEARANCE_VIEW: &str = "appearance";

/// OpenAC `RetailUiRuntime.ShowPluginAppearance`'s markup.
const APPEARANCE_MARKUP: &str = r#"<panel x="70" y="90" w="310" h="308" title="Plugin appearance" theme="plugin">
  <label x="12" y="33" text="Theme" />
  <menu x="12" y="55" w="286" h="24" items="{Themes}" selected="{Selected}" onchange="{Select}" rows="8" />
  <label x="12" y="89" text="Dock" />
  <menu x="12" y="111" w="286" h="24" items="{Docks}" selected="{SelectedDock}" onchange="{SelectDock}" rows="6" />
  <label x="12" y="145" text="Dock size" />
  <menu x="12" y="167" w="286" h="24" items="{DockSizes}" selected="{SelectedDockSize}" onchange="{SelectDockSize}" rows="3" />
  <label x="12" y="201" text="Dock opens" />
  <menu x="12" y="223" w="286" h="24" items="{DockOpenings}" selected="{SelectedDockOpens}" onchange="{SelectDockOpens}" rows="2" />
  <button x="226" y="270" w="72" h="24" text="Close" onclick="{Close}" />
</panel>"#;

/// Markup with no images to resolve.
struct NoMarkupIcons;

impl MarkupIcons for NoMarkupIcons {
    fn surface(&mut self, _id: u32) -> Option<Icon> {
        None
    }
    fn spell(&mut self, _spell: u32) -> Option<Icon> {
        None
    }
    fn item(&mut self, _object: u32) -> Option<Icon> {
        None
    }
}

/// The window's art, its icon composer and the objects it knows, as the
/// images plugin markup names; and the file icons the session holds for
/// the plugin window being synced.
struct WindowIcons<'a> {
    content: &'a DatContent,
    icons: &'a mut IconComposer,
    objects: &'a ac_core::items::ClientObjectTable,
    view: &'a ac_plugin_ui::PluginUiView,
    images: &'a super::icons::RuntimeImages,
    textures: &'a mut HashMap<(String, u32), (Icon, bool)>,
    /// The plugin window being synced.
    panel: u32,
}

/// A plugin's own image (its pixels, or a file its markup draws) as a
/// texture of this window, uploaded the first time it is drawn; none for
/// the client's art, which plugins name by id.
fn own_image(
    images: &super::icons::RuntimeImages,
    image: &ac_plugin_ui::ImageView,
) -> Option<Icon> {
    let level = |width: u32, height: u32, rgba: &[u8]| ac_dat::texture::DecodedTexture {
        width,
        height,
        rgba: rgba.to_vec(),
    };
    let levels = match &image.source {
        ImageSource::Rgba(pixels) => vec![level(image.width, image.height, pixels)],
        ImageSource::File(file) => file
            .levels
            .iter()
            .map(|l| level(l.width, l.height, &l.rgba))
            .collect(),
        ImageSource::ClientArt(_) | ImageSource::SpellIcon { .. } | ImageSource::ObjectIcon(_) => {
            return None;
        }
    };
    Some((
        images.add_plugin_image(levels),
        image.width as f32,
        image.height as f32,
    ))
}

fn surface_icon(content: &DatContent, id: u32) -> Option<Icon> {
    let (w, h) = content.sprite_size(id)?;
    Some((TextureRef::Surface(id), w as f32, h as f32))
}

/// An icon a plugin names by its image id, its pure white keyed out as the
/// item icons' is (OpenAC `IMarkupIconResolver`): such art has opaque white
/// where an item's underlay would show.
fn keyed_surface_icon(content: &DatContent, icons: &mut IconComposer, id: u32) -> Option<Icon> {
    let (w, h) = content.sprite_size(id)?;
    Some((icons.keyed_icon(id)?, w as f32, h as f32))
}

/// The dock's slot images: the window's art and its runtime textures.
/// A picture for the plugin dock as a texture, smaller copies and all, as it is
/// drawn below its size.
fn plugin_icon_texture(
    images: &super::icons::RuntimeImages,
    icon: &ac_plugin_ui::PluginIcon,
) -> Icon {
    let (w, h) = (icon.width, icon.height);
    let levels = ac_plugin_ui::mips::build(icon.rgba.as_ref().clone(), w, h)
        .into_iter()
        .map(|l| ac_dat::texture::DecodedTexture {
            width: l.width,
            height: l.height,
            rgba: l.rgba,
        })
        .collect();
    (images.add_plugin_image(levels), w as f32, h as f32)
}

struct WindowDockIcons<'a> {
    content: &'a DatContent,
    icons: &'a mut IconComposer,
    images: &'a super::icons::RuntimeImages,
    file_icons: &'a mut HashMap<usize, (Arc<Vec<u8>>, Icon)>,
}

impl dock::DockIcons for WindowDockIcons<'_> {
    fn file(&mut self, icon: &ac_plugin_ui::PluginIcon) -> Option<Icon> {
        let key = Arc::as_ptr(&icon.rgba) as usize;
        if let Some((_, texture)) = self.file_icons.get(&key) {
            return Some(*texture);
        }
        let texture = plugin_icon_texture(self.images, icon);
        self.file_icons
            .insert(key, (Arc::clone(&icon.rgba), texture));
        Some(texture)
    }

    fn surface(&mut self, id: u32) -> Option<Icon> {
        keyed_surface_icon(self.content, self.icons, id)
    }
}

impl MarkupIcons for WindowIcons<'_> {
    fn surface(&mut self, id: u32) -> Option<Icon> {
        keyed_surface_icon(self.content, self.icons, id)
    }

    fn spell(&mut self, spell: u32) -> Option<Icon> {
        self.icons
            .spell_icon(spell)
            .map(|t| (t, ICON_SIZE, ICON_SIZE))
    }

    fn item(&mut self, object: u32) -> Option<Icon> {
        let o = self.objects.get(object)?;
        self.icons
            .item_icon(
                o.item_type,
                o.icon_id,
                o.icon_underlay_id,
                o.icon_overlay_id,
                o.effects,
            )
            .map(|t| (t, ICON_SIZE, ICON_SIZE))
    }

    fn file(&mut self, path: &str) -> Option<Icon> {
        let image = self.view.file_icon(self.panel, path)?;
        let key = (image.owner.clone(), image.handle);
        if let Some((icon, _)) = self.textures.get(&key) {
            return Some(*icon);
        }
        let icon = own_image(self.images, image)?;
        self.textures.insert(key, (icon, true));
        Some(icon)
    }
}

impl PluginWindowUi {
    /// A markup window's title colour without a theme.
    fn title_color(&self) -> Color {
        self.title_color.unwrap_or(Color::WHITE)
    }
}

impl WindowUi {
    /// The player's `controls\controls.ini` (OpenAC
    /// `InteractionRetainedUiComposition`, read from the game folder): its
    /// `[title] color` colours the titles of the markup windows built from
    /// now on where no theme colours them.
    pub fn set_controls(&mut self, controls: &ac_ui::controls_ini::ControlsIni) {
        self.plugins.title_color = controls.color("title", "color");
    }

    /// Mount the plugins' dock and their layers of the overlay band
    /// (after the overlays).
    pub(super) fn mount_plugin_ui(&mut self) {
        // The bundled sans font of the modern themes, baked into an atlas
        // texture of this window (OpenAC `BundledUiFont.Load`).
        let size = bundled_font::DEFAULT_PIXEL_HEIGHT;
        match self
            .bake_bundled_font(size, Weight::Regular)
            .and_then(|regular| {
                let semibold = self.bake_bundled_font(size, Weight::SemiBold)?;
                Ok(ModernFonts { regular, semibold })
            }) {
            Ok(fonts) => self.plugins.modern_fonts = Some(fonts),
            Err(e) => self.notices.push(format!("ui: bundled font: {e}")),
        }
        let font = self.content.font(DEFAULT_FONT_ID);
        let outbox = Rc::clone(&self.plugins.outbox);
        let modern = self.plugins.modern_fonts.clone();
        let dock = PluginDock::build(&mut self.host.root, font, modern, outbox);
        let frame = dock.root;
        let top = self.host.root.root();
        self.host.root.add_child(top, frame);
        let art = DockArt {
            icon: self.dock_app_icon(),
            count_font: self.dock_font(DOCK_COUNT_FONT_SIZE),
            smaller_letters: self.dock_font(DOCK_SMALLER_LETTERS),
            bigger_letters: self.dock_font(DOCK_BIGGER_LETTERS),
        };
        let mut dock = dock;
        dock.set_art(&mut self.host.root, art);
        match self
            .host
            .root
            .register_window(dock::WINDOW, frame, Some(frame), 0)
        {
            Ok(()) => {
                if self
                    .host
                    .attach_controller(dock::WINDOW, Box::new(dock))
                    .is_ok()
                {
                    self.plugins.dock = true;
                }
            }
            Err(e) => self.notices.push(format!("ui: plugin dock: {e:?}")),
        }
        if let Some(host) = self.status.overlays.host_mut() {
            let root = &mut self.host.root;
            self.plugins.canvases = Some(CanvasLayer::mount(root, host));
            self.plugins.projectile = Some(ProjectileDebugOverlay::mount(root, host));
        }
        self.follow_plugin_dock_setting();
        // The theme the player saved: opted-in windows take it from their
        // first draw, and plugins are told it (OpenAC `FollowTheme`).
        let saved = ThemeKind::from_saved(&self.settings.plugin_ui.theme);
        // The session starts in the classic theme, so telling it that
        // changes nothing (as OpenAC's publish of an unchanged theme
        // raises nothing); only another theme is sent.
        self.apply_plugin_theme(saved, saved != ThemeKind::Classic);
    }

    /// OpenAC's icon as a texture.
    fn dock_app_icon(&mut self) -> Option<Icon> {
        let Some(icon) = ac_plugin_ui::icon::decode_png(APP_ICON_PNG) else {
            self.notices
                .push("ui: the OpenAC icon does not decode".to_owned());
            return None;
        };
        Some(plugin_icon_texture(&self.images, &icon))
    }

    /// The bundled SemiBold at one of the dock's sizes.
    fn dock_font(&mut self, size: f32) -> Option<Arc<UiFont>> {
        match self.bake_bundled_font(size, Weight::SemiBold) {
            Ok(font) => {
                self.plugins.dock_fonts.push((Arc::clone(&font), size));
                Some(font)
            }
            Err(e) => {
                self.notices.push(format!("ui: bundled font: {e}"));
                None
            }
        }
    }

    /// The sharper bakes of the bundled font for a display with `pixels` a
    /// UI unit. Called every frame: when the density changes, the bakes for
    /// the old one are let go of and those the new one wants are listed; each
    /// frame then bakes one of them, so no frame waits for them all. On a
    /// standard display there are none.
    pub(super) fn follow_display_density(&mut self, pixels: f32) {
        if self.plugins.sharp_density != Some(pixels) {
            self.plugins.sharp_density = Some(pixels);
            // The session bakes plugins' fonts for this density too.
            self.plugins
                .outbox
                .borrow_mut()
                .push(UiInput::DisplayDensity(pixels));
            self.want_sharp_fonts(pixels);
        }
        self.bake_one_sharp_font();
    }

    /// Lists the sharper bakes a display with `pixels` a UI unit wants, and
    /// lets go of every other.
    fn want_sharp_fonts(&mut self, pixels: f32) {
        let mut wanted = Vec::new();
        if let Some(fonts) = self.plugins.modern_fonts.clone() {
            let size = bundled_font::DEFAULT_PIXEL_HEIGHT;
            wanted.push((fonts.regular, Weight::Regular, size));
            wanted.push((fonts.semibold, Weight::SemiBold, size));
            for (font, size) in self.plugins.dock_fonts.clone() {
                wanted.push((font, Weight::SemiBold, size));
            }
        }
        let wanted: Vec<SharpWanted> = wanted
            .into_iter()
            .filter_map(|(font, weight, size)| {
                let scale = bundled_font::sharp_scale_for(size, pixels);
                (scale > 1.0).then_some(SharpWanted {
                    font,
                    weight,
                    size,
                    scale,
                })
            })
            .collect();
        let images = &self.images;
        self.plugins.sharp_bakes.retain(|key, sharp| {
            let kept = wanted.iter().any(|w| w.key() == *key);
            if !kept {
                images.release(sharp.font.foreground.texture);
            }
            kept
        });
        let baked = &self.plugins.sharp_bakes;
        self.plugins.sharp_pending = wanted
            .into_iter()
            .filter(|w| !baked.contains_key(&w.key()))
            .collect();
        self.publish_sharp_fonts();
    }

    /// Bakes the next sharper font the display wants, if any.
    fn bake_one_sharp_font(&mut self) {
        let Some(next) = self.plugins.sharp_pending.pop() else {
            return;
        };
        match self.bake_bundled_font(next.size * next.scale, next.weight) {
            Ok(font) => {
                let sharp = SharpFont {
                    font,
                    scale: next.scale,
                };
                self.plugins.sharp_bakes.insert(next.key(), sharp);
            }
            Err(e) => self.notices.push(format!("ui: bundled font: {e}")),
        }
        self.publish_sharp_fonts();
    }

    /// Hands the sharper bakes made so far to the UI.
    fn publish_sharp_fonts(&mut self) {
        let chosen: SharpFonts = self
            .plugins
            .sharp_bakes
            .iter()
            .map(|((id, _), sharp)| (*id, sharp.clone()))
            .collect();
        let chosen = (!chosen.is_empty()).then(|| Arc::new(chosen));
        self.host.root.set_sharp_fonts(chosen);
    }
    /// The bundled font in `weight` at `pixel_height`, as a font over a new
    /// atlas texture.
    fn bake_bundled_font(
        &self,
        pixel_height: f32,
        weight: Weight,
    ) -> Result<Arc<UiFont>, BundledFontError> {
        let mut atlas = bundled_font::bake(pixel_height, weight)?;
        let coverage = std::mem::take(&mut atlas.coverage);
        let texture = self
            .images
            .add_coverage(atlas.width, atlas.height, coverage);
        Ok(Arc::new(atlas.create_font(texture)))
    }

    /// The plugin theme in force: opted-in windows restyle, the dock takes
    /// its colours, the Appearance window shows it and
    /// the session tells plugins (OpenAC `PluginUiThemeSettings.Theme`'s
    /// setter and `BufferedUiRegistry.PublishTheme`).
    fn apply_plugin_theme(&mut self, kind: ThemeKind, tell_session: bool) {
        self.plugins.theme_kind = kind;
        self.plugins.theme = kind.into();
        let theme = self.plugins.theme;
        if self.plugins.dock {
            self.host
                .with_controller::<PluginDock, _>(dock::WINDOW, |c, ui| c.set_theme(ui, theme));
        }
        // Every window draws again in the theme.
        self.plugins.synced = None;
        if tell_session {
            self.plugins.outbox.borrow_mut().push(UiInput::Theme(kind));
        }
        self.sync_appearance();
    }

    /// The player picked a theme in the Appearance window: it is saved
    /// with the client settings and applied.
    fn pick_plugin_theme(&mut self, kind: ThemeKind) {
        if kind == self.plugins.theme_kind {
            return;
        }
        kind.saved_name()
            .clone_into(&mut self.settings.plugin_ui.theme);
        let section = ac_ui::panels::options::Section::PluginUi;
        if !self.settings_changes.iter().any(|(s, _)| *s == section) {
            self.settings_changes.push((section, true));
        }
        self.apply_plugin_theme(kind, true);
    }

    /// The settings changed in another window: a different saved theme is
    /// applied here too.
    pub(super) fn follow_plugin_theme_setting(&mut self) {
        let saved = ThemeKind::from_saved(&self.settings.plugin_ui.theme);
        if saved != self.plugins.theme_kind {
            self.apply_plugin_theme(saved, true);
        }
    }

    /// The dock's mode, as last saved or picked.
    fn plugin_dock_mode(&mut self) -> DockMode {
        self.host
            .with_controller::<PluginDock, _>(dock::WINDOW, |c, _| c.mode())
            .unwrap_or_default()
    }

    /// The dock's mode is saved with the client settings.
    fn save_plugin_dock(&mut self, mode: DockMode) {
        mode.saved_name()
            .clone_into(&mut self.settings.plugin_ui.dock);
        let section = ac_ui::panels::options::Section::PluginUi;
        if !self.settings_changes.iter().any(|(s, _)| *s == section) {
            self.settings_changes.push((section, true));
        }
    }

    /// The player picked a place for the dock in the Appearance window: it is
    /// saved with the client settings and applied.
    fn pick_plugin_dock(&mut self, mode: DockMode) {
        if mode == self.plugin_dock_mode() {
            return;
        }
        self.save_plugin_dock(mode);
        self.host
            .with_controller::<PluginDock, _>(dock::WINDOW, |c, ui| c.set_mode(ui, mode));
    }

    /// The player picked a size for the dock in the Appearance window: it is
    /// saved with the client settings and applied.
    fn pick_plugin_dock_size(&mut self, size: DockSize) {
        size.saved_name()
            .clone_into(&mut self.settings.plugin_ui.dock_size);
        let section = ac_ui::panels::options::Section::PluginUi;
        if !self.settings_changes.iter().any(|(s, _)| *s == section) {
            self.settings_changes.push((section, true));
        }
        self.host
            .with_controller::<PluginDock, _>(dock::WINDOW, |c, ui| c.set_size(ui, size));
    }

    /// The player picked how the dock opens in the Appearance window: it is
    /// saved with the client settings and applied.
    fn pick_plugin_dock_opens(&mut self, opens: DockOpens) {
        opens
            .saved_name()
            .clone_into(&mut self.settings.plugin_ui.dock_opens);
        let section = ac_ui::panels::options::Section::PluginUi;
        if !self.settings_changes.iter().any(|(s, _)| *s == section) {
            self.settings_changes.push((section, true));
        }
        self.host
            .with_controller::<PluginDock, _>(dock::WINDOW, |c, ui| c.set_opens(ui, opens));
    }

    /// The limit on the dock's slots, as a drag of its handle left it, is saved
    /// with the client settings.
    fn save_plugin_dock_limit(&mut self, limit: Option<usize>) {
        self.settings.plugin_ui.dock_slots = limit.and_then(|l| i32::try_from(l).ok()).unwrap_or(0);
        let section = ac_ui::panels::options::Section::PluginUi;
        if !self.settings_changes.iter().any(|(s, _)| *s == section) {
            self.settings_changes.push((section, true));
        }
    }

    /// The saved place, size, opening and limit of the dock are applied, when
    /// they differ.
    pub(super) fn follow_plugin_dock_setting(&mut self) {
        let saved = DockMode::from_saved(&self.settings.plugin_ui.dock);
        let size = DockSize::from_saved(&self.settings.plugin_ui.dock_size);
        let opens = DockOpens::from_saved(&self.settings.plugin_ui.dock_opens);
        let limit = usize::try_from(self.settings.plugin_ui.dock_slots)
            .ok()
            .filter(|l| *l > 0);
        self.host
            .with_controller::<PluginDock, _>(dock::WINDOW, |c, ui| {
                c.set_mode(ui, saved);
                c.set_size(ui, size);
                c.set_opens(ui, opens);
                c.set_slot_limit(ui, limit);
            });
    }

    /// Open the Appearance window, building it the first time, or close it
    /// when it is open (OpenAC `ShowPluginAppearance`).
    fn toggle_plugin_appearance(&mut self) {
        let first_time = self.plugins.appearance.is_none();
        if first_time {
            let mut ui = ac_plugin_ui::PluginUi::new();
            let schema = ac_plugin_ui::BindingSchema::new("PluginAppearanceBinding")
                .with("Themes", ac_plugin_ui::BindingKind::TextList)
                .with("Selected", ac_plugin_ui::BindingKind::Text)
                .with("Select", ac_plugin_ui::BindingKind::TextAction)
                .with("Docks", ac_plugin_ui::BindingKind::TextList)
                .with("SelectedDock", ac_plugin_ui::BindingKind::Text)
                .with("SelectDock", ac_plugin_ui::BindingKind::TextAction)
                .with("DockSizes", ac_plugin_ui::BindingKind::TextList)
                .with("SelectedDockSize", ac_plugin_ui::BindingKind::Text)
                .with("SelectDockSize", ac_plugin_ui::BindingKind::TextAction)
                .with("DockOpenings", ac_plugin_ui::BindingKind::TextList)
                .with("SelectedDockOpens", ac_plugin_ui::BindingKind::Text)
                .with("SelectDockOpens", ac_plugin_ui::BindingKind::TextAction)
                .with("Close", ac_plugin_ui::BindingKind::Action);
            let mut descriptor =
                ac_plugin_ui::PanelDescriptor::new(APPEARANCE_VIEW, "Plugin appearance");
            descriptor.show_in_side_panel = false;
            let owner = ac_plugin_ui::UiOwner::new(APPEARANCE_OWNER, "OpenAC");
            let panel = match ui.register_panel(&owner, descriptor, APPEARANCE_MARKUP, schema) {
                Ok(panel) => panel,
                Err(e) => {
                    self.notices.push(format!("ui: plugin appearance: {e}"));
                    return;
                }
            };
            let Some(view) = ui.panels().next().cloned() else {
                return;
            };
            let outbox: PluginOutbox = Rc::new(std::cell::RefCell::new(Vec::new()));
            let font = self.content.font(DEFAULT_FONT_ID);
            let controller = build_panel(&mut self.host.root, &view, font, Rc::clone(&outbox))
                .with_modern_fonts(self.plugins.modern_fonts.clone())
                .with_title_color(&mut self.host.root, self.plugins.title_color());
            let frame = controller.root;
            let window = controller.window_name.clone();
            let top = self.host.root.root();
            self.host.root.add_child(top, frame);
            if let Err(e) = self
                .host
                .root
                .register_window(&window, frame, Some(frame), 0)
            {
                self.notices.push(format!("ui: plugin appearance: {e:?}"));
                self.host.root.destroy(frame);
                return;
            }
            if let Err(e) = self.host.attach_controller(&window, Box::new(controller)) {
                self.notices.push(format!("ui: plugin appearance: {e:?}"));
            }
            self.plugins.appearance = Some(Appearance {
                ui,
                panel,
                window,
                outbox,
                revision: 0,
            });
        }
        let mut opened = false;
        if let Some(a) = self.plugins.appearance.as_mut() {
            opened = first_time || !a.ui.panels().next().is_some_and(PanelView::shown);
            a.ui.show_panel(APPEARANCE_OWNER, APPEARANCE_VIEW, opened);
        }
        self.sync_appearance();
        if !opened {
            return;
        }
        if let Some(frame) = self
            .plugins
            .appearance
            .as_ref()
            .and_then(|a| self.host.root.window(&a.window))
            .map(|w| w.frame)
        {
            self.host.root.bring_to_front(frame);
        }
    }

    /// The Appearance window shows the theme in force and where the dock is.
    fn sync_appearance(&mut self) {
        let theme = self.plugins.theme;
        let kind = self.plugins.theme_kind;
        let mode = self.plugin_dock_mode();
        let (size, opens) = self
            .host
            .with_controller::<PluginDock, _>(dock::WINDOW, |c, _| (c.size(), c.opens()))
            .unwrap_or_default();
        let Some(a) = self.plugins.appearance.as_mut() else {
            return;
        };
        let names: Vec<String> = ThemeKind::ALL
            .iter()
            .map(|t| t.display_name().to_owned())
            .collect();
        let _ = a.ui.set_values(
            APPEARANCE_OWNER,
            a.panel,
            vec![
                ("Themes".into(), ac_plugin_ui::UiValue::TextList(names)),
                (
                    "Selected".into(),
                    ac_plugin_ui::UiValue::Text(kind.display_name().to_owned()),
                ),
                (
                    "Docks".into(),
                    ac_plugin_ui::UiValue::TextList(
                        DockMode::ALL
                            .iter()
                            .map(|m| m.display_name().to_owned())
                            .collect(),
                    ),
                ),
                (
                    "SelectedDock".into(),
                    ac_plugin_ui::UiValue::Text(mode.display_name().to_owned()),
                ),
                (
                    "DockSizes".into(),
                    ac_plugin_ui::UiValue::TextList(
                        DockSize::ALL
                            .iter()
                            .map(|s| s.display_name().to_owned())
                            .collect(),
                    ),
                ),
                (
                    "SelectedDockSize".into(),
                    ac_plugin_ui::UiValue::Text(size.display_name().to_owned()),
                ),
                (
                    "DockOpenings".into(),
                    ac_plugin_ui::UiValue::TextList(
                        DockOpens::ALL
                            .iter()
                            .map(|o| o.display_name().to_owned())
                            .collect(),
                    ),
                ),
                (
                    "SelectedDockOpens".into(),
                    ac_plugin_ui::UiValue::Text(opens.display_name().to_owned()),
                ),
            ],
        );
        a.revision += 1;
        let revision = a.revision;
        let Some(view) = a.ui.panels().next().cloned() else {
            return;
        };
        let window = a.window.clone();
        self.host
            .with_controller::<PluginPanelController, _>(&window, |c, ui| {
                c.sync(ui, &view, revision, &mut NoMarkupIcons, theme);
            });
    }

    /// What the player did in the Appearance window: a pick, Close, the
    /// frame's close button.
    fn follow_appearance(&mut self) {
        let Some(a) = self.plugins.appearance.as_mut() else {
            return;
        };
        let inputs: Vec<UiInput> = a.outbox.borrow_mut().drain(..).collect();
        if inputs.is_empty() {
            return;
        }
        let mut picked = None;
        let mut picked_dock = None;
        let mut picked_size = None;
        let mut picked_opens = None;
        for input in inputs {
            match &input {
                UiInput::Action {
                    handler,
                    value: ac_plugin_ui::ActionValue::Text(name),
                    ..
                } if handler == "Select" => picked = ThemeKind::from_display_name(name),
                UiInput::Action {
                    handler,
                    value: ac_plugin_ui::ActionValue::Text(name),
                    ..
                } if handler == "SelectDock" => picked_dock = DockMode::from_display_name(name),
                UiInput::Action {
                    handler,
                    value: ac_plugin_ui::ActionValue::Text(name),
                    ..
                } if handler == "SelectDockSize" => {
                    picked_size = DockSize::from_display_name(name);
                }
                UiInput::Action {
                    handler,
                    value: ac_plugin_ui::ActionValue::Text(name),
                    ..
                } if handler == "SelectDockOpens" => {
                    picked_opens = DockOpens::from_display_name(name);
                }
                UiInput::Action { handler, .. } if handler == "Close" => {
                    a.ui.show_panel(APPEARANCE_OWNER, APPEARANCE_VIEW, false);
                }
                _ => a.ui.input(input),
            }
        }
        if let Some(kind) = picked {
            self.pick_plugin_theme(kind);
        }
        if let Some(mode) = picked_dock {
            self.pick_plugin_dock(mode);
        }
        if let Some(size) = picked_size {
            self.pick_plugin_dock_size(size);
        }
        if let Some(opens) = picked_opens {
            self.pick_plugin_dock_opens(opens);
        }
        self.sync_appearance();
    }

    /// Follow the window's copy of the plugin UI: mount the windows that
    /// came, drop the ones that went, show every one as the session has
    /// it, and carry out what plugins asked of the client's windows.
    pub(super) fn feed_plugin_ui(&mut self) {
        let dirty = self.facts.plugin_ui.take_dirty();
        let revision = self.facts.plugin_ui.revision;
        if dirty.panels || self.plugins.synced.is_none() {
            self.mount_plugin_panels();
        }
        if self.plugins.synced != Some(revision) {
            self.plugins.synced = Some(revision);
            self.sync_plugin_panels(revision);
        }
        self.sync_plugin_dialogs();
        let requests = self.facts.plugin_ui.take_client_requests();
        for (window, action) in requests {
            self.client_window_request(window, action);
        }
        self.report_client_windows();
        let appearance_asked = self.plugins.dock
            && self
                .host
                .with_controller::<PluginDock, _>(dock::WINDOW, |c, _| c.take_appearance_request())
                .unwrap_or(false);
        if appearance_asked {
            self.toggle_plugin_appearance();
        }
        let dragged = self
            .host
            .with_controller::<PluginDock, _>(dock::WINDOW, |c, _| c.take_dragged_mode())
            .flatten();
        if let Some(mode) = dragged {
            self.save_plugin_dock(mode);
            self.sync_appearance();
        }
        let stretched = self
            .host
            .with_controller::<PluginDock, _>(dock::WINDOW, |c, _| c.take_stretched_limit())
            .flatten();
        if let Some(limit) = stretched {
            self.save_plugin_dock_limit(limit);
        }
        self.follow_appearance();
    }

    /// The session's dialogs and modal windows (OpenAC `PluginDialogHost`).
    fn sync_plugin_dialogs(&mut self) {
        let mut frames = HashMap::new();
        let mut palettes = HashMap::new();
        for (id, name) in &self.plugins.panels {
            if let Some(window) = self.host.root.window(name) {
                frames.insert(*id, window.frame);
            }
            if let Some(view) = self.facts.plugin_ui.panel(*id)
                && view.document.panel.themed
                && let Some(p) = self.plugins.theme.palette()
            {
                palettes.insert(*id, p);
            }
        }
        let font = self.content.font(DEFAULT_FONT_ID);
        let modern = self.plugins.modern_fonts.clone();
        let outbox = Rc::clone(&self.plugins.outbox);
        let dialogs = self.facts.plugin_ui.dialogs.clone();
        let modals = self.facts.plugin_ui.modals.clone();
        self.plugins.dialogs.sync(
            &mut self.host.root,
            &dialogs,
            &frames,
            &palettes,
            font.as_ref(),
            modern.as_ref(),
            &outbox,
        );
        self.plugins
            .dialogs
            .sync_modals(&mut self.host.root, &modals, &frames);
    }

    fn mount_plugin_panels(&mut self) {
        let view = &self.facts.plugin_ui;
        let gone: Vec<(u32, String)> = self
            .plugins
            .panels
            .iter()
            .filter(|(id, _)| view.panel(**id).is_none())
            .map(|(id, name)| (*id, name.clone()))
            .collect();
        for (id, name) in gone {
            self.plugins.panels.remove(&id);
            self.plugins.files_shown.remove(&id);
            let frame = self.host.root.window(&name).map(|w| w.frame);
            self.host.root.unregister_window(&name);
            self.host.pump();
            if let Some(frame) = frame {
                self.host.root.destroy(frame);
            }
        }
        let font = self.content.font(DEFAULT_FONT_ID);
        let new: Vec<&PanelView> = view
            .panels
            .iter()
            .filter(|p| !self.plugins.panels.contains_key(&p.id))
            .collect();
        for panel in new {
            let state = self.plugins.control_state();
            let controller = build_panel_with_state(
                &mut self.host.root,
                panel,
                font.clone(),
                Rc::clone(&self.plugins.outbox),
                Some(state),
            )
            .with_modern_fonts(self.plugins.modern_fonts.clone())
            .with_title_color(&mut self.host.root, self.plugins.title_color());
            for warning in controller.warnings() {
                self.notices.push(format!(
                    "ui: plugin window {}: {warning}",
                    controller.window_name
                ));
            }
            let frame = controller.root;
            let name = controller.window_name.clone();
            let revision = controller.geometry_revision();
            let content = controller.content_root().unwrap_or(frame);
            let top = self.host.root.root();
            self.host.root.add_child(top, frame);
            if let Err(e) = self
                .host
                .root
                .register_window(&name, frame, Some(content), revision)
            {
                self.notices
                    .push(format!("ui: plugin window {name}: {e:?}"));
                self.host.root.destroy(frame);
                continue;
            }
            if let Err(e) = self.host.attach_controller(&name, Box::new(controller)) {
                self.notices
                    .push(format!("ui: plugin window {name}: {e:?}"));
            }
            self.plugins.panels.insert(panel.id, name);
        }
    }

    fn sync_plugin_panels(&mut self, revision: u64) {
        let Self {
            host,
            content,
            icons,
            facts,
            plugins,
            images,
            notices,
            ..
        } = self;
        let mut markup_icons = WindowIcons {
            content,
            icons,
            objects: &facts.objects,
            view: &facts.plugin_ui,
            images,
            textures: &mut plugins.images,
            panel: 0,
        };
        let theme = plugins.theme;
        for panel in &facts.plugin_ui.panels {
            let Some(name) = plugins.panels.get(&panel.id) else {
                continue;
            };
            markup_icons.panel = panel.id;
            let noticed = host.with_controller::<PluginPanelController, _>(name, |c, ui| {
                c.sync(ui, panel, revision, &mut markup_icons, theme);
                c.take_notices()
            });
            for notice in noticed.into_iter().flatten() {
                notices.push(format!("ui: plugin window {name}: {notice}"));
            }
        }
        if plugins.dock {
            let mut dock_icons = WindowDockIcons {
                content,
                icons: &mut *markup_icons.icons,
                images,
                file_icons: &mut plugins.file_icons,
            };
            host.with_controller::<PluginDock, _>(dock::WINDOW, |c, ui| {
                c.sync(ui, &facts.plugin_ui.panels, &mut dock_icons);
            });
            // The window's copy of the panels holds what is still on the dock.
            plugins.file_icons.retain(|_, (pixels, (texture, _, _))| {
                let held = Arc::strong_count(pixels) > 1;
                if !held {
                    images.release(*texture);
                }
                held
            });
        }
    }

    /// The session holds the file icons each plugin window drew this frame
    /// (only the elements drawn: none in a hidden window, a closed tab's
    /// page or a list row scrolled away): told when they change.
    pub(super) fn report_drawn_file_icons(&mut self) {
        let panels: Vec<(u32, String)> = self
            .plugins
            .panels
            .iter()
            .map(|(id, name)| (*id, name.clone()))
            .collect();
        for (panel, name) in panels {
            let drawn = self
                .host
                .with_controller::<PluginPanelController, _>(&name, |c, _| c.take_drawn_files())
                .unwrap_or_default();
            let told = self.plugins.files_shown.get(&panel);
            if told.map_or(!drawn.is_empty(), |t| *t != drawn) {
                self.plugins.files_shown.insert(panel, drawn.clone());
                self.plugins
                    .outbox
                    .borrow_mut()
                    .push(UiInput::FileIconsShown {
                        panel,
                        paths: drawn,
                    });
            }
        }
    }
    /// OpenAC `RetailUiRuntime.ToggleClientWindow` and its siblings: a
    /// main panel through the panel switcher (so the toolbar follows), any
    /// other window directly.
    fn client_window_request(&mut self, window: ClientWindow, action: ClientWindowAction) {
        let name = client_window_name(window);
        let root = &mut self.host.root;
        if let Some(panel) = self.panels.panel_of(name) {
            match action {
                ClientWindowAction::Toggle => {
                    self.panels.toggle(root, panel);
                }
                ClientWindowAction::Show => {
                    self.panels.set_visibility(root, panel, true);
                }
                ClientWindowAction::Hide => {
                    self.panels.set_visibility(root, panel, false);
                }
            }
        } else {
            match action {
                ClientWindowAction::Toggle => {
                    root.toggle_window(name);
                }
                ClientWindowAction::Show => {
                    root.show_window(name);
                }
                ClientWindowAction::Hide => {
                    root.hide_window(name);
                }
            }
        }
        self.host.pump();
    }

    /// Tell the session which of the client's windows this window has and
    /// which show, when that changed.
    /// Only a session that handed the window its plugin UI (the snapshot
    /// every attach starts with) hears it.
    fn report_client_windows(&mut self) {
        if self.facts.plugin_ui.revision == 0 {
            return;
        }
        let (mut mounted, mut visible) = (0u16, 0u16);
        for window in ClientWindow::ALL {
            let name = client_window_name(window);
            if self.host.root.window(name).is_some() {
                mounted |= window.bit();
            }
            let shown = match self.panels.panel_of(name) {
                Some(panel) => self.panels.is_visible(&self.host.root, panel),
                None => self.host.root.is_window_visible(name),
            };
            if shown {
                visible |= window.bit();
            }
        }
        if self.plugins.reported != Some((mounted, visible)) {
            self.plugins.reported = Some((mounted, visible));
            self.plugins
                .outbox
                .borrow_mut()
                .push(UiInput::ClientWindows { mounted, visible });
        }
    }

    /// One frame of the plugins' overlays: the canvases on the screen and
    /// the projectile markers (the labels go with the window's world
    /// labels; the world lines are drawn by the world renderer).
    pub(super) fn tick_plugin_ui(&mut self, now: Instant) {
        let saved = self.plugins.save_control_state(false);
        self.notices.extend(saved);
        let camera = self.status.overlays.camera();
        let Self {
            host,
            content,
            icons,
            facts,
            plugins,
            images,
            status,
            ..
        } = self;
        let Some(band) = status.overlays.host_mut() else {
            return;
        };
        band.follow_root(&mut host.root);
        let view = &facts.plugin_ui;
        // An image the plugin let go of goes, and a texture this window
        // uploaded for it is given back.
        plugins.images.retain(|key, (icon, owned)| {
            let kept = view.images.contains_key(key);
            if !kept && *owned {
                images.release(icon.0);
            }
            kept
        });
        let mut image = |owner: &str, handle: u32| -> Option<Icon> {
            let key = (owner.to_owned(), handle);
            if let Some((icon, _)) = plugins.images.get(&key) {
                return Some(*icon);
            }
            let source = view.images.get(&key)?;
            let icon = match &source.source {
                ImageSource::ClientArt(id) => surface_icon(content, *id),
                ImageSource::SpellIcon { spell, icon } => surface_icon(content, *icon)
                    .or_else(|| icons.spell_icon(*spell).map(|t| (t, ICON_SIZE, ICON_SIZE))),
                ImageSource::ObjectIcon(object) => facts.objects.get(*object).and_then(|o| {
                    icons
                        .item_icon(
                            o.item_type,
                            o.icon_id,
                            o.icon_underlay_id,
                            o.icon_overlay_id,
                            o.effects,
                        )
                        .map(|t| (t, ICON_SIZE, ICON_SIZE))
                }),
                ImageSource::Rgba(_) | ImageSource::File(_) => own_image(images, source),
            }?;
            let owned = matches!(source.source, ImageSource::Rgba(_) | ImageSource::File(_));
            plugins.images.insert(key, (icon, owned));
            Some(icon)
        };
        // A font's atlases go when no font the plugins hold uses them.
        let in_use = |key: usize| {
            view.fonts.values().any(|f| {
                Arc::as_ptr(&f.font) as usize == key
                    || f.sharp
                        .as_ref()
                        .is_some_and(|s| Arc::as_ptr(&s.font) as usize == key)
            })
        };
        plugins.font_textures.retain(|key, (_, texture)| {
            let kept = in_use(*key);
            if !kept {
                images.release(*texture);
            }
            kept
        });
        let font_textures = &mut plugins.font_textures;
        let mut font_of = |view: &ac_plugin_ui::fonts::FontView| {
            let mut upload = |font: &Arc<ac_plugin_ui::fonts::FontBake>| {
                font_textures
                    .entry(Arc::as_ptr(font) as usize)
                    .or_insert_with(|| {
                        let texture = images.add_coverage(
                            font.atlas_width,
                            font.atlas_height,
                            font.coverage.clone(),
                        );
                        (Arc::clone(font), texture)
                    })
                    .1
            };
            CanvasFont {
                texture: upload(&view.font),
                font: Arc::clone(&view.font),
                sharp: view
                    .sharp
                    .as_ref()
                    .map(|s| (s.scale, Arc::clone(&s.font), upload(&s.font))),
            }
        };
        let font = content.font(DEFAULT_FONT_ID);
        if let Some(layer) = plugins.canvases.as_mut() {
            layer.sync(
                &mut host.root,
                band,
                view,
                &mut image,
                &mut font_of,
                font.as_ref(),
                &plugins.outbox,
            );
        }
        let shown: Vec<WorldIcon> = plugins
            .world_icons
            .iter()
            .filter_map(|placed| {
                let entry = plugins.icon_entries.get(placed.entry)?;
                let icon = &entry.icon;
                Some(WorldIcon {
                    anchor: placed.anchor,
                    image: image(&entry.owner, icon.image),
                    size: icon.size,
                    tint: rgba_color(icon.tint_rgba),
                    border: icon.border_rgba.map(rgba_color),
                    max_range: icon.max_range,
                })
            })
            .collect();
        if let Some(overlay) = status.overlays.icons_mut() {
            let anchors = &plugins.anchors;
            overlay.tick(
                &mut host.root,
                &shown,
                &|id| anchors.get(&id).copied(),
                &plugins.labels,
                camera.as_ref(),
            );
        }
        let (sets, _) = &view.projectile;
        if plugins.projectile_set.is_none_or(|(seen, _)| seen != *sets) {
            plugins.projectile_set = Some((*sets, now));
        }
        let fresh = plugins
            .projectile_set
            .is_some_and(|(n, at)| n > 0 && now.duration_since(at) <= PROJECTILE_SAMPLES_SHOWN);
        if let Some(projectile) = plugins.projectile.as_mut() {
            let samples: &[ProjectileDebugSample] = if fresh { &plugins.samples } else { &[] };
            projectile.tick(&mut host.root, samples, camera.as_ref());
        }
    }

    /// The plugins' world lines placed in the drawing frame this frame (the
    /// world renderer draws them in the scene).
    pub(crate) fn plugin_world_lines(&self) -> &[PlacedLine] {
        &self.plugins.world_lines
    }

    /// The plugins' world triangles placed in the drawing frame this frame
    /// (the world renderer draws them in the scene).
    pub(crate) fn plugin_world_triangles(&self) -> &[PlacedTriangle] {
        &self.plugins.world_triangles
    }

    /// The anchors of the plugins' icons that hide behind the world, to test
    /// in the world pass this frame.
    pub(crate) fn plugin_icon_probes(&self) -> &[IconProbe] {
        &self.plugins.icon_probes
    }

    /// The plugins' ground shapes placed in the drawing frame this frame
    /// (the world renderer draws them in the scene).
    pub(crate) fn plugin_world_shapes(&self) -> &[PlacedShape] {
        &self.plugins.world_shapes
    }

    /// The plugins' labels, lines, triangles, icons, ground shapes and
    /// projectile markers placed in the drawing frame from the world as last
    /// drawn (none without a world, and no labels or icons in the portal
    /// view).
    pub(crate) fn set_plugin_world(
        &mut self,
        world: Option<&WorldRenderer>,
        scene: Option<&Scene>,
    ) {
        let dats = self.content.dats().clone();
        let view = &self.facts.plugin_ui;
        let plugins = &mut self.plugins;
        plugins.labels.clear();
        plugins.anchors.clear();
        plugins.world_lines.clear();
        plugins.world_triangles.clear();
        plugins.world_icons.clear();
        plugins.icon_probes.clear();
        plugins.world_shapes.clear();
        plugins.samples.clear();
        let (Some(world), Some(scene)) = (world, scene) else {
            return;
        };
        // Nothing of the world shows in the portal view, so nothing hangs over
        // it either.
        let world_shown = !world.portal_hides_world();
        if world_shown {
            for label in view.labels.iter() {
                let [r, g, b, a] = label.color;
                plugins.labels.push(WorldLabel {
                    object_id: label.object_id,
                    text: label.text.clone(),
                    color: Color::rgba(r, g, b, a),
                    height_offset: label.height_offset,
                    line: u32::try_from(label.line).unwrap_or(0),
                    max_range: label.max_range,
                    outline: label.outline,
                });
                plugins.keep_anchor(world, scene, &dats, label.object_id);
            }
        }
        for line in view.lines.iter() {
            if let (Some(from), Some(to)) = (
                map_point_in_frame(world, line.start),
                map_point_in_frame(world, line.end),
            ) {
                plugins.world_lines.push(PlacedLine {
                    start: from,
                    end: to,
                    color_rgb: line.color_rgb,
                    follow_terrain: line.follow_terrain,
                    width_metres: line.width_metres,
                });
            }
        }
        for triangle in view.triangles.iter() {
            let [a, b, c] = triangle
                .corners
                .map(|corner| map_point_in_frame(world, corner));
            if let (Some(a), Some(b), Some(c)) = (a, b, c) {
                plugins.world_triangles.push(PlacedTriangle {
                    corners: [a, b, c],
                    colors: triangle.colors_rgba.map(rgba_unit),
                });
            }
        }
        plugins.icon_entries = Arc::clone(&view.icons);
        if world_shown {
            for (entry, icon) in view.icons.iter().enumerate() {
                let (anchor, key, at) = match icon.icon.anchor {
                    MarkerAnchor::None => continue,
                    MarkerAnchor::Object(id) => {
                        plugins.keep_anchor(world, scene, &dats, id);
                        let head = plugins
                            .anchors
                            .get(&id)
                            .map(|a| a.base + Vec3::Z * a.height);
                        (WorldIconAnchor::Object(id), IconProbeKey::Object(id), head)
                    }
                    MarkerAnchor::Position { point, .. } => {
                        let key = icon_probe_key(point);
                        let Some(point) = map_point_in_frame(world, point) else {
                            continue;
                        };
                        (WorldIconAnchor::Point(point), key, Some(point))
                    }
                };
                if icon.icon.hidden_by_world {
                    let Some(at) = at else {
                        continue;
                    };
                    plugins.icon_probes.push(IconProbe { key, anchor: at });
                    if world.icon_probe_visible(key) != Some(true) {
                        continue;
                    }
                }
                plugins.world_icons.push(PlacedIcon { entry, anchor });
            }
        }
        for shape in view.shapes.iter() {
            if let Some(placed) = shape_anchor(world, scene, shape.anchor)
                .and_then(|at| world_shapes::place(shape, at, |x, y| world.sample_terrain_z(x, y)))
            {
                plugins.world_shapes.push(placed);
            }
        }
        for sample in view.projectile.1.iter() {
            if let Some(world_position) = map_point_in_frame(world, sample.position) {
                plugins.samples.push(ProjectileDebugSample {
                    world_position,
                    is_clear: sample.is_clear,
                    radius: sample.radius,
                });
            }
        }
    }

    /// Tell the session which chords the window's key map uses, at each
    /// attach and whenever the bindings change: plugin hotkeys bind around
    /// them (OpenAC `AppHotkeyRegistry.Bind`).
    pub fn report_client_chords(&mut self, bindings: &ac_ui::input::KeyBindings) {
        self.plugins.manager_chord = bindings
            .for_action(ac_ui::input::InputAction::TogglePluginManager)
            .next()
            .map(|b| b.chord);
        let snapshots = self.facts.plugin_ui.snapshots;
        if snapshots == 0 {
            return;
        }
        let chords = ac_ui::plugin::hotkeys::client_chords(bindings);
        let told = self
            .plugins
            .reported_chords
            .as_ref()
            .is_some_and(|(s, c)| *s == snapshots && *c == chords);
        if !told {
            self.plugins.reported_chords = Some((snapshots, chords.clone()));
            self.plugins
                .outbox
                .borrow_mut()
                .push(UiInput::ClientChords(chords));
        }
    }

    /// OpenAC `RetailUiRuntime`'s `TogglePluginManager` (Shift+Ctrl+F1 by
    /// default): with no plugin window on the dock it says so; else the
    /// dock hides, telling the player the key that brings it back (or to
    /// bind one), or shows again.
    pub(super) fn toggle_plugin_dock(&mut self) -> bool {
        let state = if self.plugins.dock {
            self.host
                .with_controller::<PluginDock, _>(dock::WINDOW, |c, _| {
                    (c.entry_count(), c.is_shown())
                })
                .unwrap_or((0, false))
        } else {
            (0, false)
        };
        let (entries, shown) = state;
        if entries == 0 {
            self.client_line(super::panel_keys::NO_PLUGIN_WINDOWS);
            return true;
        }
        self.host
            .with_controller::<PluginDock, _>(dock::WINDOW, |c, ui| c.set_hidden(ui, shown));
        if shown {
            let text = self.plugin_dock_hidden_message();
            self.client_line(&text);
        }
        true
    }

    /// OpenAC `PluginShelfHiddenMessage`.
    fn plugin_dock_hidden_message(&self) -> String {
        let Some(chord) = self.plugins.manager_chord else {
            return "Plugin dock hidden. Bind Toggle Plugin Manager in Configure Keyboard to \
                    show it again."
                .to_owned();
        };
        let dats = self.content.dats().clone();
        let strings = ac_ui::strings::StringResolver::new(move |id| {
            dats.get::<ac_dat::objects::StringTable>(id).ok().flatten()
        });
        let names = ac_ui::input::key_names::RetailKeyNames::new(
            Box::new(move |table, hash| strings.resolve(table, hash, 0)),
            crate::platform::key_name_provider(),
        );
        format!(
            "Plugin dock hidden. Press {} to show it again.",
            names.describe(chord)
        )
    }

    /// A key went down (not a repeat): the bound plugin hotkeys it fires go
    /// to the session. Asked before the interface takes the key, as OpenAC
    /// listens to the keyboard itself; its gates stand in for the interface
    /// first: nothing fires during a key capture or under a modal dialog,
    /// and with a text entry focused only a chord with Ctrl or Alt fires.
    pub fn plugin_hotkeys(
        &mut self,
        key: ac_ui::Key,
        modifiers: ac_ui::event::Modifiers,
        capturing: bool,
    ) {
        let gate = ac_plugin_ui::hotkeys::HotkeyGate {
            capturing,
            modal: self.host.root.modal.is_some(),
            chat_focused: self.host.root.wants_keyboard(),
        };
        let fired: Vec<UiInput> =
            ac_ui::plugin::hotkeys::fired(&self.facts.plugin_ui.hotkeys, key, modifiers, gate)
                .into_iter()
                .map(|h| UiInput::Hotkey {
                    owner: h.owner.clone(),
                    id: h.id.clone(),
                })
                .collect();
        self.plugins.outbox.borrow_mut().extend(fired);
    }

    /// Input actions the key map fired go to the session for the plugins
    /// that listen for them (it drops them when none does).
    pub fn plugin_input_actions(
        &mut self,
        fired: &[(ac_ui::input::InputAction, ac_ui::input::ActivationType)],
    ) {
        self.plugins
            .outbox
            .borrow_mut()
            .extend(
                fired
                    .iter()
                    .map(|(action, activation)| UiInput::InputAction {
                        action: action.name().to_owned(),
                        activation: activation.name().to_owned(),
                    }),
            );
    }

    /// What the player did with the plugins' UI since the last call, as
    /// session commands.
    pub fn take_plugin_commands(&mut self) -> Vec<SessionCommand> {
        self.plugins
            .outbox
            .borrow_mut()
            .drain(..)
            .map(SessionCommand::PluginUi)
            .collect()
    }

    /// The window names of the plugin windows mounted (diagnostics).
    #[must_use]
    pub fn plugin_windows(&self) -> Vec<String> {
        self.plugins.panels.values().cloned().collect()
    }
}

/// What the occlusion answer of an icon at a fixed position is kept under:
/// the position itself, which is what the probe tests.
fn icon_probe_key(point: MapPoint) -> IconProbeKey {
    IconProbeKey::Position([
        point.east_west.to_bits(),
        point.north_south.to_bits(),
        point.elevation.to_bits(),
    ])
}

/// `0xRRGGBBAA` as red, green, blue and alpha in 0..1.
fn rgba_unit(rgba: u32) -> [f32; 4] {
    rgba.to_be_bytes().map(|channel| f32::from(channel) / 255.0)
}

/// `0xRRGGBBAA` as a colour.
fn rgba_color(rgba: u32) -> Color {
    let [r, g, b, a] = rgba_unit(rgba);
    Color::rgba(r, g, b, a)
}

/// A map point in the drawing frame: its landblock placed by the world's
/// floating origin.
fn map_point_in_frame(world: &WorldRenderer, point: MapPoint) -> Option<Vec3> {
    let (x, y, lx, ly, z) = point.to_landblock()?;
    let cell = (u32::from(x) << 24) | (u32::from(y) << 16) | 1;
    world.frame_position(cell, Vec3::new(lx, ly, z))
}

/// Where a ground shape's anchor is this frame: an object's base and the
/// way it faces, a held object's from the pose its holder's hand gives it,
/// or a fixed position. An object with no cell yet counts as outdoors, so
/// the height rule alone decides.
fn shape_anchor(world: &WorldRenderer, scene: &Scene, anchor: MarkerAnchor) -> Option<ShapeAnchor> {
    match anchor {
        MarkerAnchor::None => None,
        MarkerAnchor::Object(guid) => {
            let object = scene.object_by_guid(guid)?;
            let placement = super::overlays::placement(world, object)?;
            let rotation = match placement {
                Placement::World { rotation, .. } => rotation,
                Placement::Held(root) => root.to_scale_rotation_translation().1,
            };
            Some(ShapeAnchor {
                centre: placement.base(),
                heading_degrees: ac_physics::scenery::heading_of(rotation * Vec3::Y),
                is_outdoor: object
                    .position
                    .is_none_or(|pos| pos.cell_id & 0xFFFF < FIRST_INDOOR_CELL),
            })
        }
        MarkerAnchor::Position { point, is_outdoor } => Some(ShapeAnchor {
            centre: map_point_in_frame(world, point)?,
            heading_degrees: 0.0,
            is_outdoor,
        }),
    }
}

/// Where a label over `guid` hangs (OpenAC
/// `WorldSelectionQuery.TryResolveWorldLabelAnchor`): its base, and its
/// height from its body (the setup's cylinder, scaled), else the top of
/// its selection sphere, else the top of its model at rest, else a fixed
/// 1.1 m. An object carried by another hangs from the root pose its
/// holder's hand gives it, as last published.
fn label_anchor(
    world: &WorldRenderer,
    scene: &Scene,
    dats: &DatCollection,
    shapes: &mut HashMap<u32, Option<SetupShape>>,
    guid: u32,
) -> Option<WorldLabelAnchor> {
    let object = scene.object_by_guid(guid)?;
    let base = super::overlays::placement(world, object)?.base();
    // The sphere's centre turns with the object's own turn (OpenAC: the
    // entity's rotation, a held object's included); one that never had a
    // position has none.
    let orientation = object
        .position
        .map_or(Quaternion::IDENTITY, |pos| pos.orientation);
    let scale = object.scale.filter(|s| *s > 0.0).unwrap_or(1.0);
    let shape = object.setup_id.and_then(|id| {
        *shapes
            .entry(id)
            .or_insert_with(|| SetupShape::read(dats, id))
    });
    let height = shape.and_then(|shape| {
        let body = shape.height * scale;
        if body > 0.0 && body.is_finite() {
            return Some(body);
        }
        if let Some((origin, radius)) = shape.sphere {
            // The sphere's centre turns with the object; its top does not
            // depend on the turn.
            let o = orientation;
            let rotation = Quat::from_xyzw(o.x, o.y, o.z, o.w);
            let top = (rotation * (origin * scale)).z + radius * scale;
            if top > 0.0 && top.is_finite() {
                return Some(top);
            }
        }
        shape.model_top.map(|top| top * scale)
    });
    Some(WorldLabelAnchor {
        base,
        height: height.unwrap_or(FALLBACK_LABEL_HEIGHT),
    })
}

/// What a label's height comes from in one setup, read once per setup.
#[derive(Debug, Clone, Copy)]
struct SetupShape {
    height: f32,
    sphere: Option<(Vec3, f32)>,
    model_top: Option<f32>,
}

impl SetupShape {
    fn read(dats: &DatCollection, id: u32) -> Option<Self> {
        let setup = dats.get::<Setup>(id).ok().flatten()?;
        let s = setup.selection_sphere;
        Some(Self {
            height: setup.height,
            sphere: (s.radius > 1e-4)
                .then(|| (Vec3::new(s.origin.x, s.origin.y, s.origin.z), s.radius)),
            model_top: model_height(dats, &setup),
        })
    }
}

/// The top of a model at rest in its own metres, unscaled (OpenAC
/// `SetupModelHeightResolver`): the highest point of any part's drawing
/// sphere with the parts placed in the resting pose.
fn model_height(dats: &DatCollection, setup: &Setup) -> Option<f32> {
    let mut top = f32::NEG_INFINITY;
    for part in flatten_setup(setup, None) {
        let Ok(Some(gfx)) = dats.get::<GfxObj>(part.gfx_obj_id) else {
            continue;
        };
        let sphere = match gfx.drawing_bsp.as_ref().and_then(|t| t.root()) {
            Some(BspNode::Split(s)) => s.sphere,
            Some(BspNode::Portal(p)) => p.sphere,
            _ => continue,
        };
        let center = part.transform.transform_point3(Vec3::new(
            sphere.origin.x,
            sphere.origin.y,
            sphere.origin.z,
        ));
        top = top.max(center.z + sphere.radius);
    }
    (top.is_finite() && top > 0.0).then_some(top)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_openac_icon_decodes() {
        let icon = ac_plugin_ui::icon::decode_png(APP_ICON_PNG).expect("the icon decodes");
        assert_eq!((icon.width, icon.height), (256, 256));
    }

    #[test]
    fn the_openac_icon_is_clear_round_and_inside_its_ring() {
        let icon = ac_plugin_ui::icon::decode_png(APP_ICON_PNG).expect("the icon decodes");
        let alpha = |x: usize, y: usize| icon.rgba[(y * 256 + x) * 4 + 3];
        // A corner, and the open middle of the crescent.
        assert_eq!(alpha(4, 4), 0);
        assert_eq!(alpha(180, 128), 0);
        // The crescent's body.
        assert_eq!(alpha(80, 128), 255);
    }

    #[test]
    fn every_client_window_has_a_name() {
        let names: std::collections::HashSet<&str> = ClientWindow::ALL
            .iter()
            .map(|w| client_window_name(*w))
            .collect();
        assert_eq!(names.len(), ClientWindow::ALL.len());
    }

    fn at(east_west: f64) -> MapPoint {
        MapPoint {
            east_west,
            north_south: 12.5,
            elevation: 0.3,
        }
    }

    /// An icon at a fixed position keeps its probe's answer under its
    /// position, not its place in the merged list: when a plugin replaces
    /// its icons, a new icon elsewhere at the same place in the list has no
    /// answer yet (so it stays hidden until its own probe answers) rather
    /// than the old icon's "in sight", which would flash it through a wall.
    /// (Keyed by place in the list, the two keys below were equal.)
    #[test]
    fn a_new_icon_elsewhere_does_not_inherit_an_old_icons_probe_answer() {
        let old = icon_probe_key(at(40.0));
        let new = icon_probe_key(at(41.0));
        assert_ne!(old, new);
        // The same position is the same anchor with the same answer,
        // wherever its icon sits in the list.
        assert_eq!(icon_probe_key(at(40.0)), old);
    }

    #[test]
    fn a_triangle_colour_is_red_green_blue_then_alpha() {
        assert_eq!(rgba_unit(0xFF00_0080), [1.0, 0.0, 0.0, 128.0 / 255.0]);
        assert_eq!(rgba_unit(0x0000_FFFF), [0.0, 0.0, 1.0, 1.0]);
    }
}
