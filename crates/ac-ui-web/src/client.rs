//! The client's own panels behind the module: [`ClientPanels`] runs the
//! window UI of `ac-ui-window` over dats the page hands in file by file,
//! feeds it the session's stream, routes the page's input through it and
//! the world input as the native window does, and hands back each frame's
//! triangles, textures, commands and requests.

use std::collections::HashSet;
use std::sync::Arc;
use std::time::Duration;

use ac_clock::Instant;
use ac_dat::{DatCollection, DatFile, DatHeader, DatSource};
use ac_host::settings::{PlayerSettings, SettingsStore};
use ac_remote_wire::{DecodeContext, EncodeContext};
use ac_runtime::session::SessionCommand;
use ac_ui::cursor::{CursorFeedback, CursorFeedbackKind, GlobalCursorKind};
use ac_ui::draw::TextureRef;
use ac_ui::event::{Key, Modifiers as UiModifiers};
use ac_ui_window::gameplay_input::{DeviceState, GameplayInput, GameplayOutput, session_output};
use ac_ui_window::input::InputState;
use ac_ui_window::scene::Scene;
use ac_ui_window::ui::input::{self as ui_input, Routed, WorldInput};
use ac_ui_window::ui::keyboard_window::KeyboardRequest;
use ac_ui_window::ui::stores::ControlStateSettings;
use ac_ui_window::ui::{TextureLedger, UiBatch, UiPixels, WindowUi};

use crate::commands::Outbox;
use crate::host::{Modifiers, Panels, Session};
use crate::keys;
use crate::output::{Cursor, Level, Outputs, Texels};
use crate::page::{
    self, ArrivingSurfaces, ClipboardState, HEADER_ID, KeptSettings, PageClipboard, PageStorage,
    PictureSent, SurfacesOnly,
};

/// Where the window layouts are kept: a settings file of their own.
pub const LAYOUTS_KEY: &str = "window-layouts/settings.json";
/// Where the plugin windows' remembered control values are kept: a
/// settings file of their own.
pub const PLUGIN_CONTROLS_KEY: &str = "plugin-controls/settings.json";

/// The canvas: its size in pixels and its pixels per UI unit.
#[derive(Debug, Clone, Copy, PartialEq)]
struct Canvas {
    width: u32,
    height: u32,
    scale: f32,
}

impl Canvas {
    /// Pixels per UI unit, 1 when the page gives no usable scale.
    fn scale(self) -> f32 {
        if self.scale.is_finite() && self.scale > 0.0 {
            self.scale
        } else {
            1.0
        }
    }

    /// The UI's size in its own units.
    fn ui_size(self) -> (u32, u32) {
        let s = self.scale();
        let units = |pixels: u32| ((pixels as f32 / s).round() as u32).max(1);
        (units(self.width), units(self.height))
    }

    /// A canvas point in whole UI units.
    fn ui_point(self, (x, y): (f32, f32)) -> (i32, i32) {
        let s = self.scale();
        ((x / s) as i32, (y / s) as i32)
    }
}

/// Where the UI's build stands.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Build {
    /// Not built yet; each round asks for what the last attempt read and
    /// the dats did not have.
    Waiting,
    /// Every file it read was there and it still could not be built.
    Failed,
}

/// The client's panels, built for a page. See the module documentation.
pub struct ClientPanels {
    canvas: Option<Canvas>,
    /// Each dat's header, in the page's numbering: `None` until handed in,
    /// `Some(None)` when the page has no such dat.
    headers: [Option<Option<DatHeader>>; 4],
    /// The header requests are out.
    headers_asked: bool,
    dats: Option<Arc<DatCollection>>,
    /// Files handed in before the dats could be made, in order.
    early_files: Vec<(DatSource, u32, Option<Vec<u8>>)>,
    /// Files asked for and not answered yet.
    in_flight: HashSet<(DatSource, u32)>,
    /// Textures drawn while their files were missing, to read again.
    waiting_textures: HashSet<TextureRef>,
    /// Dat files arrived since the panels last forgot their misses.
    arrived: bool,
    storage: PageStorage,
    clipboard: PageClipboard,
    settings: PlayerSettings,
    ui: Option<WindowUi>,
    build: Build,
    rounds: u32,
    /// The empty picture shown while building went out.
    building_picture_sent: bool,
    scene: Option<Scene>,
    decode: Option<DecodeContext>,
    gameplay: GameplayInput,
    held: InputState<&'static str>,
    gameplay_tick: Option<f64>,
    outbox: Outbox,
    ledger: TextureLedger,
    picture: PictureSent,
    cursor: Option<Cursor>,
    modifiers: UiModifiers,
    pointer: Option<(f32, f32)>,
    /// Text that follows a key the world input took is not typed.
    swallow_text: bool,
    /// The page's time last handed in, milliseconds.
    now_ms: f64,
    /// The epoch commands count instants from when no stream is attached.
    epoch: Instant,
    log: Vec<String>,
}

impl std::fmt::Debug for ClientPanels {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ClientPanels")
            .field("canvas", &self.canvas)
            .field("built", &self.ui.is_some())
            .field("rounds", &self.rounds)
            .field("in_flight", &self.in_flight.len())
            .finish_non_exhaustive()
    }
}

impl Default for ClientPanels {
    fn default() -> Self {
        Self::new()
    }
}

impl ClientPanels {
    /// Panels with nothing handed in yet.
    #[must_use]
    pub fn new() -> Self {
        Self {
            canvas: None,
            headers: [None, None, None, None],
            headers_asked: false,
            dats: None,
            early_files: Vec::new(),
            in_flight: HashSet::new(),
            waiting_textures: HashSet::new(),
            arrived: false,
            storage: PageStorage::default(),
            clipboard: PageClipboard::default(),
            settings: PlayerSettings::default(),
            ui: None,
            build: Build::Waiting,
            rounds: 0,
            building_picture_sent: false,
            scene: None,
            decode: None,
            gameplay: GameplayInput::new(ac_ui::input::KeyBindings::retail_defaults()),
            held: InputState::new(),
            gameplay_tick: None,
            outbox: Outbox::new(),
            ledger: TextureLedger::new(),
            picture: PictureSent::default(),
            cursor: None,
            modifiers: UiModifiers::NONE,
            pointer: None,
            swallow_text: false,
            now_ms: 0.0,
            epoch: Instant::now(),
            log: Vec::new(),
        }
    }

    /// The window UI, once built.
    #[must_use]
    pub fn ui(&self) -> Option<&WindowUi> {
        self.ui.as_ref()
    }

    /// Build rounds so far: attempts that read files the dats did not
    /// have yet.
    #[must_use]
    pub fn rounds(&self) -> u32 {
        self.rounds
    }

    /// The dats, once every header is in.
    #[must_use]
    pub fn dats(&self) -> Option<&Arc<DatCollection>> {
        self.dats.as_ref()
    }

    fn encode_context(&self) -> EncodeContext {
        EncodeContext::new(self.decode.as_ref().map_or(self.epoch, |d| d.epoch))
    }

    fn send(&mut self, command: SessionCommand) {
        let cx = self.encode_context();
        self.outbox.send(command, self.now_ms, &cx);
    }

    // ── Dats ────────────────────────────────────────────────────────────

    /// Make the packs once every header is in.
    fn make_dats(&mut self) {
        if self.dats.is_some() || self.headers.iter().any(Option::is_none) {
            return;
        }
        let pack = |source: DatSource| -> Option<DatFile> {
            self.headers[page::source_number(source) as usize]
                .clone()
                .flatten()
                .map(DatFile::new_pack)
        };
        let (Some(portal), Some(cell), Some(local)) = (
            pack(DatSource::Portal),
            pack(DatSource::Cell),
            pack(DatSource::Local),
        ) else {
            self.log
                .push("ui: the portal, cell and language dats are all needed".into());
            self.build = Build::Failed;
            return;
        };
        match DatCollection::from_packs(portal, cell, pack(DatSource::HighRes), local) {
            Ok(dats) => {
                let dats = Arc::new(dats);
                for (source, id, bytes) in std::mem::take(&mut self.early_files) {
                    fill(&dats, source, id, bytes);
                }
                self.dats = Some(dats);
            }
            Err(error) => {
                self.log
                    .push(format!("ui: the dats cannot be used: {error}"));
                self.build = Build::Failed;
            }
        }
    }

    /// The headers not handed in yet, as requests.
    fn header_requests(&self) -> Vec<(u32, u32)> {
        (0..4u32)
            .filter(|&n| self.headers[n as usize].is_none())
            .map(|n| (n, HEADER_ID))
            .collect()
    }

    /// Ask the page for `wanted`, each once while it is unanswered.
    fn request(&mut self, wanted: Vec<(DatSource, u32)>, out: &mut Outputs) {
        for (source, id) in wanted {
            if self.in_flight.insert((source, id)) {
                out.dat_request(page::source_number(source), id);
            }
        }
    }

    // ── Build ───────────────────────────────────────────────────────────

    /// Try to build the UI: build it, wire it, draw one frame and read its
    /// textures; if anything read was not in the dats yet, drop it and ask
    /// for those files (one round), else keep it.
    fn try_build(&mut self, canvas: Canvas, out: &mut Outputs) {
        let Some(dats) = self.dats.clone() else {
            return;
        };
        if self.build == Build::Failed || !self.in_flight.is_empty() {
            return;
        }
        let size = canvas.ui_size();
        let built = WindowUi::build_with(
            Arc::clone(&dats),
            size,
            &self.settings,
            ac_ui_window::ui::options::resolution_choices(&[], None),
            Some(Box::new(self.storage.clone())),
        )
        .map(|mut ui| {
            self.wire(&mut ui);
            ui.set_pixels_per_unit(canvas.scale());
            let list = ui.frame(Instant::now(), size);
            let batch = UiBatch::build_scaled(&list, canvas.scale());
            let mut surfaces = ui.surfaces();
            let _ = TextureLedger::new().plan(
                &batch,
                (canvas.width, canvas.height),
                &mut SurfacesOnly(&mut surfaces),
            );
            ui
        });
        let wanted = dats.take_wanted();
        if !wanted.is_empty() {
            self.rounds += 1;
            self.log.push(format!(
                "ui: building, round {}: {} dat files wanted",
                self.rounds,
                wanted.len()
            ));
            self.request(wanted, out);
            if !self.building_picture_sent {
                out.picture(&[], &[]);
                self.building_picture_sent = true;
            }
            return;
        }
        match built {
            Ok(mut ui) => {
                self.log.extend(ui.take_notices());
                self.log.push(format!(
                    "ui: built after {} rounds; mounted {}",
                    self.rounds,
                    ui.mounted().join(", ")
                ));
                self.ui = Some(ui);
                self.ledger.lost_everything();
                self.picture.owe();
            }
            Err(error) => {
                self.log.push(format!("ui: no panels: {error}"));
                self.build = Build::Failed;
            }
        }
    }

    /// What the native window hands a new UI: the clipboard, the stores,
    /// the key bindings and the style sheet.
    fn wire(&mut self, ui: &mut WindowUi) {
        ui.set_clipboard(Box::new(self.clipboard.clone()));
        let layouts = SettingsStore::with_bytes(
            LAYOUTS_KEY,
            Arc::new(KeptSettings::new(self.storage.clone(), LAYOUTS_KEY)),
        );
        let controls = SettingsStore::with_bytes(
            PLUGIN_CONTROLS_KEY,
            Arc::new(KeptSettings::new(self.storage.clone(), PLUGIN_CONTROLS_KEY)),
        );
        ui.set_stores(
            Some(Box::new(layouts)),
            Some(Box::new(ControlStateSettings(controls))),
        );
        ui.attach_key_bindings(self.gameplay.dispatcher().bindings());
        let key = ac_ui::storage::keys::CONTROLS_INI;
        match ac_ui::controls_ini::ControlsIni::load_from(&self.storage, key) {
            Ok(controls) => ui.set_controls(&controls),
            Err(error) => self.log.push(format!("ui: {key}: {error}")),
        }
    }

    /// The bindings the page stored (the active key map over the bindings
    /// file), or the retail key map.
    fn load_bindings(&mut self) {
        let mut storage = self.storage.clone();
        let (bindings, notices) = ac_ui::input::keymap_file::load_active(&mut storage);
        self.log.extend(notices);
        self.gameplay = GameplayInput::new(bindings);
    }

    // ── Input ───────────────────────────────────────────────────────────

    /// What the gameplay dispatcher reads of the devices now.
    fn device_state(&self) -> DeviceState<'static> {
        DeviceState {
            keys: None,
            buttons: 0,
            modifiers: self.modifiers,
            want_mouse: false,
            want_keyboard: self
                .ui
                .as_ref()
                .is_some_and(|ui| ui.host().root.keyboard_focus().is_some()),
        }
    }

    fn gameplay_now(&self) -> f32 {
        (self.now_ms / 1000.0) as f32
    }

    /// Act on what the UI asked for besides the world's part and hand that
    /// part back.
    fn after_ui<K>(&mut self, routed: Routed<K>) -> Option<WorldInput<K>> {
        let Routed { world, outputs } = routed;
        let Some(ui) = self.ui.as_mut() else {
            return world;
        };
        let commands = ui.handle(outputs);
        self.log.extend(ui.take_notices());
        // Items let go over the world and drags begun on it need a pick of
        // the 3-D view, which is the page's: none is made here.
        let _ = ui.take_world_drops();
        let _ = ui.take_world_drag_starts();
        for command in commands {
            self.send(command);
        }
        self.follow_keyboard();
        world
    }

    /// What the key configuration screen asked of the key dispatcher.
    fn follow_keyboard(&mut self) {
        let Some(ui) = self.ui.as_mut() else {
            return;
        };
        for request in ui.take_keyboard_requests() {
            match request {
                KeyboardRequest::Apply(bindings) => self.gameplay.set_bindings(bindings),
                KeyboardRequest::BeginCapture => self.gameplay.begin_capture(),
            }
        }
    }

    /// The world's keys, after the UI: presses only when the UI let them
    /// through, releases always; a held key's auto-repeat is not a press.
    fn world_key(&mut self, physical: &'static str, key: Key, pressed: bool) {
        let new_press = self.held.key(physical, pressed);
        if pressed && !new_press {
            return;
        }
        if key == Key::Unknown {
            return;
        }
        if let Some(ui) = self.ui.as_ref() {
            self.gameplay.set_combat_mode(ui.combat_mode());
        }
        let state = self.device_state();
        let now = self.gameplay_now();
        let mut out = Vec::new();
        self.gameplay.key(key, pressed, &state, None, now, &mut out);
        self.carry_out(out);
    }

    /// Carry out what the gameplay input asked for, as the native window
    /// does for everything a page can do: the camera, the sound, the
    /// cursor, screenshots and clicks on the 3-D view are the page's.
    fn carry_out(&mut self, outputs: Vec<GameplayOutput>) {
        let fired = self.gameplay.take_fired();
        if !fired.is_empty()
            && let Some(ui) = self.ui.as_mut()
        {
            ui.plugin_input_actions(&fired);
        }
        for output in outputs {
            match output {
                GameplayOutput::Movement(command) => self.send(SessionCommand::Movement(command)),
                GameplayOutput::Escape => self.send(SessionCommand::Gameplay(
                    ac_runtime::gameplay::GameplayCommand::Escape,
                )),
                GameplayOutput::Gameplay(command) => self.send(SessionCommand::Gameplay(command)),
                GameplayOutput::Item(command) => {
                    let command = self.with_split_amount(command);
                    self.send(SessionCommand::Item(command));
                }
                GameplayOutput::ChatEntry(action, key) => {
                    let Some(ui) = self.ui.as_mut() else {
                        continue;
                    };
                    ui.chat_entry_key(action, key);
                    let outputs = ui.host_mut().take_outputs();
                    let _ = self.after_ui(Routed::<()> {
                        world: None,
                        outputs,
                    });
                }
                GameplayOutput::LogOut => {
                    if self.outbox.session().may_log_off {
                        self.send(SessionCommand::Lifecycle(
                            ac_runtime::session::LifecycleCommand::EndCharacterSession,
                        ));
                    } else {
                        self.log
                            .push("ui: this page may not log the character off".into());
                    }
                }
                GameplayOutput::ToggleCharacterOption(option) => {
                    let current = self
                        .scene
                        .as_ref()
                        .and_then(Scene::player_view)
                        .is_some_and(|v| v.option(option));
                    self.send(SessionCommand::SetCharacterOption {
                        option,
                        value: !current,
                    });
                }
                GameplayOutput::Captured(chord) => self.keyboard_captured(chord),
                GameplayOutput::Interface(action) => {
                    let Some(ui) = self.ui.as_mut() else {
                        continue;
                    };
                    let taken = ui.handle_input_action(action);
                    let outputs = ui.host_mut().take_outputs();
                    let _ = self.after_ui(Routed::<()> {
                        world: None,
                        outputs,
                    });
                    if !taken && let Some(next) = session_output(action) {
                        self.carry_out(vec![next]);
                    }
                }
                GameplayOutput::Notice(text) => self.log.push(text),
                GameplayOutput::CycleTimeOfDay
                | GameplayOutput::CycleWeather
                | GameplayOutput::ToggleMute
                | GameplayOutput::ToggleFly
                | GameplayOutput::TogglePlayerMode
                | GameplayOutput::GrabCursor
                | GameplayOutput::ReleaseCursor
                | GameplayOutput::CaptureScreenshot
                | GameplayOutput::DumpNearby
                | GameplayOutput::WorldClick(_) => {}
            }
        }
    }

    /// The give and drop keys move the split chooser's quantity of the
    /// selected stack.
    fn with_split_amount(
        &mut self,
        command: ac_runtime::inventory::ItemCommand,
    ) -> ac_runtime::inventory::ItemCommand {
        use ac_runtime::inventory::ItemCommand;
        let mut amount = || self.ui.as_mut().map_or(0, |ui| ui.selection_split_amount());
        match command {
            ItemCommand::GiveSelectedToPrevious { amount: 0 } => {
                ItemCommand::GiveSelectedToPrevious { amount: amount() }
            }
            ItemCommand::DropSelected { amount: 0 } => {
                ItemCommand::DropSelected { amount: amount() }
            }
            other => other,
        }
    }

    /// A key capture ended: the screen takes it, or asks for another key.
    fn keyboard_captured(&mut self, chord: Option<ac_ui::input::KeyChord>) {
        let again = self
            .ui
            .as_mut()
            .is_some_and(|ui| ui.keyboard_captured(chord));
        if again {
            self.gameplay.begin_capture();
        }
        if let Some(ui) = self.ui.as_mut() {
            let commands = ui.drain();
            self.log.extend(ui.take_notices());
            for command in commands {
                self.send(command);
            }
        }
        self.follow_keyboard();
    }

    /// Every held key is let go in the module's own input state; the
    /// session hears it when `tell`.
    fn release_all(&mut self, tell: bool) {
        self.held.focus_changed(false);
        self.held.focus_changed(true);
        let mut out = Vec::new();
        self.gameplay.focus_lost(&mut out);
        let _ = self.gameplay.take_fired();
        let cx = self.encode_context();
        self.outbox.release_keys(tell, self.now_ms, &cx);
    }

    /// The pointer is at `at` for the UI (a wheel or a press elsewhere
    /// than the last move moves it there first).
    fn pointer_at(&mut self, at: (f32, f32)) {
        if self.pointer != Some(at) {
            self.pointer_move(at.0, at.1, self.now_ms);
        }
    }

    // ── Frame ───────────────────────────────────────────────────────────

    /// One frame of the built UI: the gameplay tick, the scene fed, the
    /// frame drawn and handed over.
    fn draw(&mut self, canvas: Canvas, out: &mut Outputs) {
        self.tick_gameplay();
        let Some(ui) = self.ui.as_mut() else {
            return;
        };
        let Some(dats) = self.dats.clone() else {
            return;
        };
        // Files arrived: what the panels read and did not find is read
        // again, and they redraw with it.
        if std::mem::take(&mut self.arrived) {
            ui.forget_misses();
        }
        if let Some(scene) = self.scene.as_mut() {
            scene.present_at(Instant::now());
            ui.feed(scene);
            // The world's effects and motions are the 3-D view's.
            let _ = scene.take_effects();
            let _ = scene.take_motions();
        }
        let _ = ui.take_minimize_requests();
        ui.report_client_chords(self.gameplay.dispatcher().bindings());
        let size = canvas.ui_size();
        ui.set_pixels_per_unit(canvas.scale());
        let list = ui.frame(Instant::now(), size);
        let batch = UiBatch::build_scaled(&list, canvas.scale());
        // What the feed and the frame wanted, taken first, so a texture is
        // held back only for what its own decode wanted.
        let mut wanted = dats.take_wanted();
        let plan = {
            let mut surfaces = ui.surfaces();
            let mut source = ArrivingSurfaces {
                inner: &mut surfaces,
                dats: &dats,
                wanted: &mut wanted,
                waiting: &mut self.waiting_textures,
            };
            self.ledger
                .plan(&batch, (canvas.width, canvas.height), &mut source)
        };
        for id in self.ledger.take_missing() {
            self.log.push(format!(
                "ui: render surface {id:#010x} is not in the dats; drawn as magenta"
            ));
        }
        for texture in plan.releases {
            out.release(page::texture_ref(texture));
        }
        for (texture, pixels) in &plan.uploads {
            upload(out, *texture, pixels);
        }
        let (vertices, runs) = page::picture_of(&batch, &plan.runs);
        self.picture.offer(vertices, runs, out);
        let cursor = page_cursor(ui.cursor_feedback());
        if self.cursor != Some(cursor) {
            self.cursor = Some(cursor);
            out.cursor(cursor, None);
        }
        let commands = ui.drain();
        self.log.extend(ui.take_notices());
        wanted.extend(dats.take_wanted());
        self.request(wanted, out);
        for command in commands {
            self.send(command);
        }
        self.follow_keyboard();
    }

    /// The gameplay input's frame: held actions, held movement (sent when
    /// it changed).
    fn tick_gameplay(&mut self) {
        let now = self.now_ms;
        let dt = self
            .gameplay_tick
            .replace(now)
            .map_or(0.0, |t| ((now - t).max(0.0) / 1000.0) as f32);
        let state = self.device_state();
        let mut out = Vec::new();
        let _ = self
            .gameplay
            .tick(dt, self.gameplay_now(), &state, None, &mut out);
        self.carry_out(out);
    }

    /// What the frame owes the page besides the picture.
    fn hand_over(&mut self, out: &mut Outputs) {
        let cx = self.encode_context();
        self.outbox.heartbeat(self.now_ms, &cx);
        for frame in self.outbox.take_frames() {
            out.command_frame(&frame);
        }
        self.log.extend(self.outbox.take_errors());
        page::storage_records(out, self.storage.take_writes());
        let ClipboardState {
            copied,
            paste_wanted,
        } = std::mem::take(&mut *self.clipboard.0.borrow_mut());
        if let Some(text) = copied {
            out.clipboard_copy(&text);
        }
        if paste_wanted {
            out.clipboard_paste();
        }
        for line in self.log.drain(..) {
            out.log(&line);
        }
    }
}

/// Put a file the page handed in into its pack.
fn fill(dats: &DatCollection, source: DatSource, id: u32, bytes: Option<Vec<u8>>) {
    let Some(pack) = dats.pack(source) else {
        return;
    };
    match bytes {
        Some(bytes) => pack.insert(id, bytes),
        None => pack.mark_missing(id),
    }
}

/// A texture's pixels as the page uploads them.
fn upload(out: &mut Outputs, texture: TextureRef, pixels: &UiPixels) {
    let (levels, per_texel) = pixels.levels();
    let levels: Vec<Level<'_>> = levels
        .into_iter()
        .map(|(width, height, bytes)| Level {
            width,
            height,
            texels: if per_texel == 1 {
                Texels::Alpha(bytes)
            } else {
                Texels::Rgba(bytes)
            },
        })
        .collect();
    out.upload(page::texture_ref(texture), &levels);
}

/// The page's pointer shape for what the UI's cursor shows: the window
/// controls' and the text field's shapes, and for the global cursors the
/// shapes the native window falls back to when the dats lack their art.
fn page_cursor(feedback: CursorFeedback) -> Cursor {
    use CursorFeedbackKind as K;
    match feedback.kind {
        K::Text => return Cursor::Text,
        K::WindowMove => return Cursor::Move,
        K::ResizeHorizontal => return Cursor::ResizeHorizontal,
        K::ResizeVertical => return Cursor::ResizeVertical,
        K::ResizeDiagonalNwse => return Cursor::ResizeDiagonalDown,
        K::ResizeDiagonalNesw => return Cursor::ResizeDiagonalUp,
        _ => {}
    }
    match feedback.global {
        GlobalCursorKind::Use | GlobalCursorKind::UseFound => Cursor::Hand,
        GlobalCursorKind::TargetValid => Cursor::Move,
        _ => Cursor::Default,
    }
}

impl Panels for ClientPanels {
    fn shape(&self) -> String {
        ac_remote_wire::shape_hex()
    }

    fn init(&mut self, width: u32, height: u32, scale: f32, now: f64) {
        self.now_ms = now;
        self.epoch = Instant::now();
        self.canvas = Some(Canvas {
            width,
            height,
            scale,
        });
        self.load_bindings();
    }

    fn resize(&mut self, width: u32, height: u32, scale: f32) {
        self.canvas = Some(Canvas {
            width,
            height,
            scale,
        });
        self.picture.owe();
    }

    fn session(&mut self, session: Session) {
        if self.outbox.set_session(session) {
            self.release_all(true);
        }
    }

    fn stream(&mut self, frame: &[u8], now: f64) -> Result<(), String> {
        self.now_ms = now;
        let cx = self
            .decode
            .get_or_insert_with(|| DecodeContext::new(Instant::now(), None, None));
        let events = ac_remote_wire::decode_batch(frame, cx).map_err(|e| e.to_string())?;
        let scene = self.scene.get_or_insert_with(Scene::default);
        for event in events {
            scene.apply(event);
        }
        Ok(())
    }

    fn detached(&mut self) {
        self.scene = None;
        self.decode = None;
        let tell = self.outbox.session().controlling;
        self.release_all(tell);
        // The panels show what the next attach's snapshot brings: they are
        // built again (from the files already in).
        if self.ui.take().is_some() {
            self.ledger.lost_everything();
            self.picture.owe();
            self.cursor = None;
        }
    }

    fn frame(&mut self, now: f64, out: &mut Outputs) {
        self.now_ms = now;
        let Some(canvas) = self.canvas else {
            return;
        };
        if !self.headers_asked {
            self.headers_asked = true;
            for (source, id) in self.header_requests() {
                out.dat_request(source, id);
            }
        }
        if self.ui.is_none() {
            self.try_build(canvas, out);
        }
        if self.ui.is_some() {
            self.draw(canvas, out);
        }
        self.hand_over(out);
    }

    fn key(
        &mut self,
        down: bool,
        code: &str,
        modifiers: Modifiers,
        repeat: bool,
        now: f64,
    ) -> bool {
        self.now_ms = now;
        let modifiers = keys::ui_modifiers(modifiers);
        self.modifiers = modifiers;
        let (key, physical) = (keys::ui_key(code), keys::physical(code));
        let controlling = self.outbox.session().controlling;
        let capturing = self.gameplay.is_capturing();
        let routed = match self.ui.as_mut() {
            Some(ui) => {
                ui.host_mut().set_modifiers(modifiers);
                if down && !repeat {
                    ui.plugin_hotkeys(key, modifiers, capturing);
                }
                (!capturing).then(|| ui.key(physical, key, down, repeat, modifiers, None))
            }
            None => return false,
        };
        let world = match routed {
            Some(routed) => self.after_ui(routed),
            None if down => Some(WorldInput::KeyDown {
                key: physical,
                repeat,
            }),
            None => Some(WorldInput::KeyUp(physical)),
        };
        if down {
            self.swallow_text = world.is_some();
        }
        match world {
            Some(WorldInput::KeyDown { key: p, .. }) if controlling => {
                self.world_key(p, key, true);
                true
            }
            Some(WorldInput::KeyUp(p)) => {
                self.world_key(p, key, false);
                controlling
            }
            Some(_) => false,
            None => true,
        }
    }

    fn text(&mut self, text: &str) -> bool {
        if std::mem::take(&mut self.swallow_text) {
            return true;
        }
        let modifiers = self.modifiers;
        let Some(ui) = self.ui.as_mut() else {
            return false;
        };
        let used = ui.host().root.wants_keyboard();
        let outputs = ui_input::typed(ui.host_mut(), text, modifiers);
        let _ = self.after_ui(Routed::<()> {
            world: None,
            outputs,
        });
        used
    }

    fn pointer_move(&mut self, x: f32, y: f32, now: f64) -> bool {
        self.now_ms = now;
        self.pointer = Some((x, y));
        let Some(canvas) = self.canvas else {
            return false;
        };
        let (ux, uy) = canvas.ui_point((x, y));
        let Some(ui) = self.ui.as_mut() else {
            return false;
        };
        let routed = ui_input::moved::<()>(ui.host_mut(), ux, uy, false);
        // Every move reaches the world too (its hover follows the pointer);
        // the panels use it when they hold the pointer or are under it.
        let used = ui.host_mut().root.wants_mouse();
        let _ = self.after_ui(routed);
        used
    }

    fn pointer_button(
        &mut self,
        button: u32,
        down: bool,
        at: (f32, f32),
        modifiers: Modifiers,
        now: f64,
    ) -> bool {
        self.now_ms = now;
        self.modifiers = keys::ui_modifiers(modifiers);
        self.pointer_at(at);
        let Some(canvas) = self.canvas else {
            return false;
        };
        // The page counts left, middle, right; the window left, right,
        // middle.
        let index: u16 = match button {
            0 => 0,
            1 => 2,
            2 => 1,
            other => u16::try_from(other).unwrap_or(u16::MAX),
        };
        let (x, y) = canvas.ui_point(at);
        let held: &[u16] = if down { &[index] } else { &[] };
        let flags = ui_input::mouse_flags(held, self.modifiers);
        let modifiers = self.modifiers;
        let Some(ui) = self.ui.as_mut() else {
            return false;
        };
        ui.host_mut().set_modifiers(modifiers);
        let routed = ui_input::button::<()>(ui.host_mut(), index, down, x, y, flags, false);
        self.after_ui(routed).is_none()
    }

    fn wheel(&mut self, lines: f32, at: (f32, f32), modifiers: Modifiers) -> bool {
        self.modifiers = keys::ui_modifiers(modifiers);
        self.pointer_at(at);
        let shift = modifiers.shift;
        let Some(ui) = self.ui.as_mut() else {
            return false;
        };
        let routed = ui_input::wheel::<()>(ui.host_mut(), (0.0, lines), shift, false);
        self.after_ui(routed).is_none()
    }

    fn focus(&mut self, focused: bool) {
        if !focused {
            let tell = self.outbox.session().controlling;
            self.release_all(tell);
        }
    }

    fn paste(&mut self, text: &str) {
        let Some(ui) = self.ui.as_mut() else {
            return;
        };
        let outputs = ui_input::typed(ui.host_mut(), text, UiModifiers::NONE);
        let _ = self.after_ui(Routed::<()> {
            world: None,
            outputs,
        });
    }

    fn dat(&mut self, source: u32, id: u32, bytes: Option<&[u8]>) {
        let Some(dat) = page::source_of(source) else {
            self.log.push(format!("ui: dat {source} is no dat"));
            return;
        };
        if id == HEADER_ID {
            let header = match bytes {
                Some(bytes) => match DatHeader::unpack(bytes) {
                    Ok(header) => Some(header),
                    Err(error) => {
                        self.log.push(format!(
                            "ui: the header of dat {source} cannot be read: {error}"
                        ));
                        None
                    }
                },
                None => None,
            };
            self.headers[source as usize] = Some(header);
            self.make_dats();
            return;
        }
        self.in_flight.remove(&(dat, id));
        let bytes = bytes.map(<[u8]>::to_vec);
        self.arrived = true;
        match &self.dats {
            Some(dats) => fill(dats, dat, id, bytes),
            None => self.early_files.push((dat, id, bytes)),
        }
        // Every answer is in: what was drawn without its files is read
        // again.
        if self.in_flight.is_empty() {
            for texture in self.waiting_textures.drain() {
                self.ledger.release(texture);
            }
        }
    }

    fn storage(&mut self, key: &str, value: Option<&[u8]>) {
        if let Some(value) = value {
            self.storage.insert(key, value);
        }
    }

    fn dat_preload(&mut self) -> Vec<(u32, u32)> {
        self.headers_asked = true;
        self.header_requests()
    }

    fn textures_lost(&mut self) {
        self.ledger.lost_everything();
        self.picture.owe();
    }
}

/// How long `ms` page milliseconds are.
#[must_use]
pub fn page_duration(ms: f64) -> Duration {
    Duration::from_secs_f64((ms / 1000.0).max(0.0))
}

#[cfg(test)]
mod tests {
    use ac_remote_wire::DecodeContext;
    use ac_runtime::movement::MovementCommand;

    use super::*;
    use crate::output::kind;

    fn words(bytes: &[u8]) -> Vec<u32> {
        bytes
            .chunks_exact(4)
            .map(|w| u32::from_le_bytes([w[0], w[1], w[2], w[3]]))
            .collect()
    }

    /// The commands of the command frames, unstamped.
    fn commands(bytes: &[u8]) -> Vec<String> {
        let cx = DecodeContext::new(Instant::now(), None, None);
        let mut at = 0;
        let mut found = Vec::new();
        while at < bytes.len() {
            let len = words(&bytes[at..at + 4])[0] as usize;
            let frame = &bytes[at + 4..at + 4 + len];
            at += 4 + len;
            match ac_remote_wire::decode_command(frame, &cx).unwrap() {
                SessionCommand::AtGeneration { command, .. } => found.push(format!("{command:?}")),
                other => panic!("not stamped: {other:?}"),
            }
        }
        found
    }

    fn controlling(on: bool) -> Session {
        Session {
            generation: 4,
            controlling: on,
            may_control: true,
            ..Session::default()
        }
    }

    #[test]
    fn the_headers_are_asked_for_before_init_and_only_once() {
        let mut panels = ClientPanels::new();
        let headers: Vec<_> = (0..4).map(|source| (source, HEADER_ID)).collect();
        assert_eq!(panels.dat_preload(), headers);
        panels.dat(2, HEADER_ID, None);
        assert_eq!(
            panels.dat_preload(),
            headers[..2]
                .iter()
                .chain(&headers[3..])
                .copied()
                .collect::<Vec<_>>()
        );
        panels.init(800, 600, 1.0, 0.0);
        let mut out = Outputs::default();
        panels.frame(16.0, &mut out);
        assert!(
            out.take(kind::DAT_REQUESTS).is_empty(),
            "asked by the preload"
        );

        // A page that did not ask: the first frame asks.
        let mut panels = ClientPanels::new();
        panels.init(800, 600, 1.0, 0.0);
        let mut out = Outputs::default();
        panels.frame(16.0, &mut out);
        assert_eq!(
            words(&out.take(kind::DAT_REQUESTS)),
            [0, HEADER_ID, 1, HEADER_ID, 2, HEADER_ID, 3, HEADER_ID]
        );
        panels.frame(32.0, &mut out);
        assert!(out.take(kind::DAT_REQUESTS).is_empty());
    }

    /// The first bytes of a dat file of `kind` (1 portal, 2 cell, 3 local):
    /// its header with that type.
    fn header(kind: u32) -> Vec<u8> {
        let mut bytes = vec![0; crate::page::HEADER_BYTES];
        bytes[320..324].copy_from_slice(&DatHeader::RETAIL_MAGIC.to_le_bytes());
        bytes[332..336].copy_from_slice(&kind.to_le_bytes());
        bytes
    }

    /// The sources of the dat requests of the first frames, with or
    /// without a high-resolution dat.
    fn asked_sources(high_res: bool) -> Vec<u32> {
        let mut panels = ClientPanels::new();
        for (source, kind) in [(0, 1), (1, 2), (3, 3)] {
            panels.dat(source, HEADER_ID, Some(&header(kind)));
        }
        let high = high_res.then(|| header(1));
        panels.dat(2, HEADER_ID, high.as_deref());
        panels.init(800, 600, 1.0, 0.0);
        assert!(panels.dat_preload().is_empty(), "every header is in");
        let mut out = Outputs::default();
        panels.frame(16.0, &mut out);
        let requests = words(&out.take(kind::DAT_REQUESTS));
        assert!(!requests.is_empty(), "the build asked for files");
        requests.chunks_exact(2).map(|r| r[0]).collect()
    }

    #[test]
    fn with_control_a_frame_goes_every_quarter_second_held_keys_or_not() {
        let up = format!(
            "{:?}",
            SessionCommand::Movement(MovementCommand::Keys(Default::default()))
        );
        let mut panels = ClientPanels::new();
        panels.init(800, 600, 1.0, 0.0);
        let mut out = Outputs::default();
        panels.frame(16.0, &mut out);
        assert!(out.take(kind::COMMAND_FRAMES).is_empty(), "no control");
        panels.session(Session {
            generation: 3,
            ..controlling(true)
        });
        panels.session(controlling(true));
        panels.frame(32.0, &mut out);
        let frames = out.take(kind::COMMAND_FRAMES);
        assert_eq!(commands(&frames), std::slice::from_ref(&up), "at once");
        assert_eq!(generations(&frames), [4], "the latest generation");
        panels.frame(200.0, &mut out);
        assert!(out.take(kind::COMMAND_FRAMES).is_empty());
        panels.frame(282.0, &mut out);
        assert_eq!(
            commands(&out.take(kind::COMMAND_FRAMES)),
            std::slice::from_ref(&up)
        );
        // Several seconds at once: one frame.
        panels.frame(5000.0, &mut out);
        assert_eq!(commands(&out.take(kind::COMMAND_FRAMES)), [up]);
    }

    /// The generation each command frame is stamped with.
    fn generations(bytes: &[u8]) -> Vec<u64> {
        let cx = DecodeContext::new(Instant::now(), None, None);
        let mut at = 0;
        let mut found = Vec::new();
        while at < bytes.len() {
            let len = words(&bytes[at..at + 4])[0] as usize;
            match ac_remote_wire::decode_command(&bytes[at + 4..at + 4 + len], &cx).unwrap() {
                SessionCommand::AtGeneration { generation, .. } => found.push(generation.0),
                other => panic!("not stamped: {other:?}"),
            }
            at += 4 + len;
        }
        found
    }

    #[test]
    fn without_a_high_resolution_dat_nothing_is_asked_of_it() {
        assert!(!asked_sources(false).contains(&2));
        assert!(
            asked_sources(true).contains(&2),
            "with one, a portal-type file is asked of it first"
        );
    }

    #[test]
    fn losing_the_keyboard_or_control_lets_the_keys_go_and_tells_the_session_once() {
        let release = format!(
            "{:?}",
            SessionCommand::Movement(MovementCommand::ReleaseKeys)
        );
        let mut panels = ClientPanels::new();
        panels.init(800, 600, 1.0, 0.0);
        let mut out = Outputs::default();
        // Without control nothing is said.
        panels.focus(false);
        panels.frame(16.0, &mut out);
        assert!(out.take(kind::COMMAND_FRAMES).is_empty());
        panels.session(controlling(true));
        panels.focus(false);
        panels.frame(32.0, &mut out);
        assert_eq!(
            commands(&out.take(kind::COMMAND_FRAMES)),
            std::slice::from_ref(&release)
        );
        panels.focus(true);
        panels.session(controlling(false));
        panels.frame(48.0, &mut out);
        assert_eq!(commands(&out.take(kind::COMMAND_FRAMES)), [release]);
        panels.session(controlling(false));
        panels.frame(64.0, &mut out);
        assert!(out.take(kind::COMMAND_FRAMES).is_empty(), "once");
    }

    #[test]
    fn stored_values_handed_in_are_read_and_not_handed_back() {
        let mut panels = ClientPanels::new();
        panels.storage("keybinds.json", Some(b"{}"));
        panels.storage("absent", None);
        let mut storage = panels.storage.clone();
        assert_eq!(UiStorageGet::get(&mut storage, "keybinds.json"), b"{}");
        assert!(panels.storage.take_writes().is_empty());
    }

    /// Reads a stored value through the panels' storage interface.
    trait UiStorageGet {
        fn get(&mut self, key: &str) -> Vec<u8>;
    }

    impl UiStorageGet for PageStorage {
        fn get(&mut self, key: &str) -> Vec<u8> {
            ac_ui::storage::UiStorage::get(self, key).unwrap()
        }
    }
}
