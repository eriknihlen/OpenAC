//! The window host: owns the platform event loop on the main thread and opens or closes one
//! window on command from other threads.

use std::path::PathBuf;
use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::mpsc::{self, Receiver, Sender};
use std::time::Instant;

use winit::application::ApplicationHandler;
use winit::event::{DeviceEvent, DeviceId, ElementState, MouseScrollDelta, WindowEvent};
use winit::event_loop::{ActiveEventLoop, ControlFlow, EventLoop, EventLoopProxy};
use winit::window::WindowId;

use ac_runtime::session::{PresentationEvent, SessionCommand};

use crate::RenderError;
use crate::attach::{AttachAction, AttachInput, AttachMachine, DetachReason};
use crate::camera::OrbitOverrides;
use crate::content::ContentSlot;
use crate::platform::DisplayProtocol;
use crate::renderer::{WorldSettings, WorldStats};
use crate::scene::SceneSource;
use crate::surface::{AdapterSummary, AttachedWindow, GraphicsOptions};

/// What to open on attach.
#[derive(Debug, Clone)]
pub struct WindowSpec {
    /// The title bar text until the window's first refresh; from then on
    /// the window titles itself ([`crate::title`]: the character's name in
    /// the world, else the program and its version).
    pub title: String,
    /// Inner width: pixels, points on macOS (as the resolution picks).
    pub width: u32,
    /// Inner height: pixels, points on macOS (as the resolution picks).
    pub height: u32,
    /// The color every frame is cleared to (the sky until a sky is drawn).
    pub clear_color: wgpu::Color,
}

/// Settings every window of this host shares.
#[derive(Debug, Clone, PartialEq)]
pub struct HostOptions {
    /// Directory holding the dat files; `None` draws no world (the window
    /// only clears).
    pub dat_dir: Option<PathBuf>,
    /// Startup overrides of the quality preset the player's display
    /// settings choose: the near radius (landblocks loaded in full around
    /// the player's: 4 gives 9 x 9), the far radius (terrain only, never
    /// less than the near), anti-aliasing, filtering and streaming
    /// completions.
    pub quality: crate::quality::QualityOverrides,
    /// Budgets for meshes and textures nothing draws any more
    /// (`OPENAC_RESIDENCY_*`).
    pub residency: crate::unowned_cache::ResidencyBudgets,
    /// GPU choice, pacing and frame diagnostics.
    pub graphics: GraphicsOptions,
    /// Where the orbit camera starts.
    pub orbit: OrbitOverrides,
    /// The windowing system (Linux: X11 or Wayland).
    pub display: DisplayProtocol,
    /// The reveal-radius diagnostic (`OPENAC_PROBE_REVEAL_RADIUS`): wait for this many
    /// landblocks around the player before showing the world.
    pub reveal_radius: Option<u32>,
    /// The pinned time of day and forced day group (`OPENAC_WORLD_TIME`,
    /// `OPENAC_DAY_GROUP`).
    pub sky: crate::environment::SkyOptions,
    /// The window's sound: startup switches, the player's settings and the
    /// mixer.
    pub audio: crate::audio::AudioOptions,
    /// The player's client settings every window opens with (display,
    /// sound, mixer, chat, camera, tooltips). The host keeps them current
    /// as an options window changes them, and saves the changes to
    /// `settings_file`.
    pub settings: ac_host::settings::PlayerSettings,
    /// Where the settings are saved (`None`: nothing is saved).
    pub settings_file: Option<PathBuf>,
}

impl Default for HostOptions {
    fn default() -> Self {
        Self {
            dat_dir: ac_dat::default_dat_dir(),
            quality: crate::quality::QualityOverrides::default(),
            residency: crate::unowned_cache::ResidencyBudgets::default(),
            graphics: GraphicsOptions {
                vsync: true,
                ..GraphicsOptions::default()
            },
            orbit: OrbitOverrides::default(),
            display: DisplayProtocol::Automatic,
            reveal_radius: None,
            sky: crate::environment::SkyOptions::default(),
            audio: crate::audio::AudioOptions::default(),
            settings: ac_host::settings::PlayerSettings::default(),
            settings_file: None,
        }
    }
}

impl HostOptions {
    /// The defaults (the usual dat install directory, the display
    /// settings' quality, vsync on) with the given settings replacing them.
    #[must_use]
    pub fn with_overrides(dat_dir: Option<PathBuf>, landblock_radius: Option<u32>) -> Self {
        let defaults = Self::default();
        Self {
            dat_dir: dat_dir.or(defaults.dat_dir),
            quality: crate::quality::QualityOverrides {
                near_radius: landblock_radius,
                ..defaults.quality
            },
            residency: defaults.residency,
            graphics: defaults.graphics,
            orbit: OrbitOverrides::default(),
            display: DisplayProtocol::Automatic,
            reveal_radius: None,
            sky: defaults.sky,
            audio: defaults.audio,
            settings: defaults.settings,
            settings_file: None,
        }
    }
}

/// Where a screenshot goes: `requested` when given, else
/// `logs/screenshot-<seconds since 1970>.png`.
#[must_use]
pub fn screenshot_path(requested: &str) -> PathBuf {
    if requested.is_empty() {
        let seconds = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map_or(0, |d| d.as_secs());
        PathBuf::from("logs").join(format!("screenshot-{seconds}.png"))
    } else {
        PathBuf::from(requested)
    }
}

/// A command sent to the host from any thread.
#[derive(Debug)]
enum HostCommand {
    Attach(WindowSpec, Option<Box<SceneSource>>),
    Detach,
    Screenshot(PathBuf),
    Zoom(f32),
    Vsync(bool),
    ToggleFly,
    CycleTimeOfDay,
    CycleWeather,
    DumpNearby,
    ToggleMute,
    Mixer(String),
    UiType(String, bool),
    UiScript(crate::ui::window::UiScript),
    /// Answered by raising the counter: the event loop is still turning.
    Ping(Arc<AtomicU64>),
    Shutdown,
}

/// What the host reports back. Delivered on the receiver returned by [`WindowHost::new`]; if that
/// receiver is dropped, the host keeps running and the reports are discarded.
#[derive(Debug)]
pub enum HostEvent {
    /// A window is open and presenting.
    Attached(AdapterSummary),
    /// An attach arrived while a window was already open; it was ignored.
    AttachRejected,
    /// The window could not be opened; the host is detached.
    AttachFailed(RenderError),
    /// Presenting failed; the window is being dropped (a `Detached` follows).
    RenderFailed(RenderError),
    /// The GPU reported an error that spoiled a frame; drawing goes on. Each
    /// distinct error is reported once per window.
    GpuError(String),
    /// The world was withheld until the area around the player loaded and is now shown.
    WorldRevealed {
        /// How long it was withheld.
        after: std::time::Duration,
    },
    /// The frame profiler's five-second report (only when it is on).
    FrameProfile(String),
    /// The world renderer cannot draw the world (the dats could not be read); the window stays
    /// open and only clears.
    WorldUnavailable(String),
    /// Something the window tells its owner: the sound device or state, a
    /// debug time or weather step, a fog override.
    Notice(String),
    /// A screenshot was written.
    ScreenshotSaved {
        /// Where it went.
        path: PathBuf,
        /// What the world renderer had on the GPU and drew in that frame.
        stats: Option<WorldStats>,
    },
    /// A screenshot could not be taken (no window, or the write failed).
    ScreenshotFailed(RenderError),
    /// The window and everything built for it are gone.
    Detached {
        reason: DetachReason,
        frames_presented: u64,
    },
}

/// The host has stopped and no longer accepts commands.
#[derive(Debug, thiserror::Error)]
#[error("the window host has stopped")]
pub struct HostClosed;

/// A cloneable, thread-safe remote control for a running [`WindowHost`].
#[derive(Debug, Clone)]
pub struct HostHandle {
    proxy: EventLoopProxy<HostCommand>,
    /// The host has the dats: every window it opens draws the world.
    draws_world: bool,
    /// Pings the event loop has answered.
    answered_pings: Arc<AtomicU64>,
}

impl HostHandle {
    /// Opens a window. Answered with `Attached`, `AttachFailed` or `AttachRejected`.
    pub fn attach(&self, spec: WindowSpec) -> Result<(), HostClosed> {
        self.send(HostCommand::Attach(spec, None))
    }

    /// Opens a window that draws the session feeding `presentation` (the
    /// receiver whose sender went to the session with its attach command);
    /// what the window's UI asks for goes to the session through
    /// `commands`. The session hears at once, ahead of the attach command
    /// sent after this call, whether the window draws the world.
    pub fn attach_to_session(
        &self,
        spec: WindowSpec,
        presentation: Receiver<PresentationEvent>,
        commands: Sender<SessionCommand>,
    ) -> Result<(), HostClosed> {
        self.send(HostCommand::Attach(
            spec,
            Some(session_scene(presentation, commands, self.draws_world)),
        ))
    }

    /// Closes the window if one is open. Answered with `Detached` when something closed.
    pub fn detach(&self) -> Result<(), HostClosed> {
        self.send(HostCommand::Detach)
    }

    /// Writes the next frame to `path` as a PNG. Answered with `ScreenshotSaved` or
    /// `ScreenshotFailed` once a frame has been drawn (a minimized window answers when it is
    /// restored).
    pub fn screenshot(&self, path: PathBuf) -> Result<(), HostClosed> {
        self.send(HostCommand::Screenshot(path))
    }

    /// Zooms the open window's camera by `steps` (positive moves out), as the mouse wheel does.
    pub fn zoom(&self, steps: f32) -> Result<(), HostClosed> {
        self.send(HostCommand::Zoom(steps))
    }

    /// Turns vsync on or off for the open window and every later one.
    pub fn set_vsync(&self, vsync: bool) -> Result<(), HostClosed> {
        self.send(HostCommand::Vsync(vsync))
    }

    /// Switches the open window's fly camera on or off.
    pub fn toggle_fly(&self) -> Result<(), HostClosed> {
        self.send(HostCommand::ToggleFly)
    }

    /// Steps the open window's time of day (midnight, dawn, noon, dusk,
    /// then the clock again), as Ctrl+F7 does.
    pub fn cycle_time_of_day(&self) -> Result<(), HostClosed> {
        self.send(HostCommand::CycleTimeOfDay)
    }

    /// Steps the open window's weather, as Ctrl+F10 does.
    pub fn cycle_weather(&self) -> Result<(), HostClosed> {
        self.send(HostCommand::CycleWeather)
    }

    /// Logs what stands near the player in the open window, as Ctrl+F3
    /// does (window notices).
    pub fn dump_nearby(&self) -> Result<(), HostClosed> {
        self.send(HostCommand::DumpNearby)
    }

    /// Mutes or unmutes the open window's sound, as Ctrl+M does.
    pub fn toggle_mute(&self) -> Result<(), HostClosed> {
        self.send(HostCommand::ToggleMute)
    }

    /// Runs `/mixer <arguments>`: says what the mixer does, or changes it,
    /// saves the change and applies it to the running mixer, as the chat
    /// box's `/mixer` does. The reply comes back as notices.
    pub fn mixer_command(&self, arguments: String) -> Result<(), HostClosed> {
        self.send(HostCommand::Mixer(arguments))
    }

    /// Types `line` into the open window's chat entry as the keyboard
    /// would (Enter, the characters, Enter), through the same UI routing:
    /// a diagnostic for unattended live checks, where no keyboard reaches
    /// the window.
    pub fn type_into_ui(&self, line: String) -> Result<(), HostClosed> {
        self.send(HostCommand::UiType(line, true))
    }

    /// Types `text` as the keyboard would, without Enter, into whatever
    /// has the open window's keyboard focus (a panel's name field): the
    /// same diagnostic as [`HostHandle::type_into_ui`].
    pub fn type_text_into_ui(&self, text: String) -> Result<(), HostClosed> {
        self.send(HostCommand::UiType(text, false))
    }

    /// Runs one scripted input step through the open window's UI (keys,
    /// clicks, drags, the probe): a diagnostic for unattended live checks.
    pub fn script_ui(&self, step: crate::ui::window::UiScript) -> Result<(), HostClosed> {
        self.send(HostCommand::UiScript(step))
    }

    /// Closes any window and makes [`WindowHost::run`] return.
    pub fn shutdown(&self) -> Result<(), HostClosed> {
        self.send(HostCommand::Shutdown)
    }

    /// Asks the event loop to answer: [`HostHandle::answered_pings`] rises
    /// once it has. The loop answers while it turns, minimized or not (a
    /// command wakes it); a hung loop does not, which is how a stuck game
    /// window is told apart.
    pub fn ping(&self) -> Result<(), HostClosed> {
        self.send(HostCommand::Ping(Arc::clone(&self.answered_pings)))
    }

    /// How many pings the event loop has answered.
    #[must_use]
    pub fn answered_pings(&self) -> u64 {
        self.answered_pings.load(Ordering::Relaxed)
    }

    fn send(&self, command: HostCommand) -> Result<(), HostClosed> {
        self.proxy.send_event(command).map_err(|_| HostClosed)
    }
}

/// Owns the platform event loop. Create it on the main thread and call [`WindowHost::run`] there;
/// drive it from other threads through the [`HostHandle`].
///
/// With no window open the loop sleeps until a command arrives, so a process with zero windows
/// costs nothing here. The loop runs until [`HostHandle::shutdown`]; closing the last window does
/// not end it.
#[derive(Debug)]
pub struct WindowHost {
    event_loop: EventLoop<HostCommand>,
    events: Sender<HostEvent>,
    options: HostOptions,
    /// The open game window, the one window a close request reaches.
    game_window: crate::platform::GameWindow,
}

impl WindowHost {
    /// Creates the event loop. Must be called on the main thread (a platform rule on Windows and
    /// macOS), and at most once per process.
    pub fn new(
        options: HostOptions,
    ) -> Result<(Self, HostHandle, Receiver<HostEvent>), RenderError> {
        let mut builder = EventLoop::<HostCommand>::with_user_event();
        let game_window = crate::platform::GameWindow::default();
        crate::platform::apply(&mut builder, options.display, &game_window);
        let event_loop = builder.build()?;
        event_loop.set_control_flow(ControlFlow::Wait);
        let handle = HostHandle {
            proxy: event_loop.create_proxy(),
            // Its windows draw the world exactly when it has the dats
            // (`run` builds their world settings from them).
            draws_world: options.dat_dir.is_some(),
            answered_pings: Arc::default(),
        };
        let (events, receiver) = mpsc::channel();
        Ok((
            Self {
                event_loop,
                events,
                options,
                game_window,
            },
            handle,
            receiver,
        ))
    }

    /// Runs the event loop on this thread until shutdown.
    pub fn run(self) -> Result<(), RenderError> {
        let (near, far) =
            crate::content::cache_area(&self.options.settings.display, &self.options.quality);
        let world = self.options.dat_dir.map(|dat_dir| WorldSettings {
            content: Arc::new(ContentSlot::for_area(dat_dir, near, far)),
            quality_overrides: self.options.quality,
            residency: self.options.residency,
            orbit: self.options.orbit,
            reveal_radius: self.options.reveal_radius,
            sky: self.options.sky,
            audio: self.options.audio,
        });
        let mut app = HostApp {
            instance: None,
            game_window: self.game_window,
            machine: AttachMachine::new(),
            window: None,
            pending: None,
            events: self.events,
            world,
            graphics: self.options.graphics,
            settings: self.options.settings,
            store: self
                .options
                .settings_file
                .map(ac_host::settings::SettingsStore::new),
        };
        self.event_loop.run_app(&mut app)?;
        Ok(())
    }
}

/// The scene a window opening on a session follows. The session hears now,
/// before the window has opened, whether the window draws the world: a
/// player who enters the world while the window is still opening has the
/// login presented (login complete held for the window), as in a graphical
/// client whose window is there before its session. The opened window says
/// it again (`AttachedWindow::set_scene`).
fn session_scene(
    presentation: Receiver<PresentationEvent>,
    commands: Sender<SessionCommand>,
    draws_world: bool,
) -> Box<SceneSource> {
    let scene = SceneSource::new(presentation).with_commands(commands);
    scene.send(SessionCommand::PresentsWorld(draws_world));
    Box::new(scene)
}

/// The GPU instance for windows on this display.
fn gpu_instance(display: winit::event_loop::OwnedDisplayHandle) -> wgpu::Instance {
    let mut descriptor = wgpu::InstanceDescriptor::new_with_display_handle(Box::new(display));
    // DX12 shaders compile with FXC, which ships with Windows. The default would load a
    // dxcompiler.dll found on PATH: a native library that is neither OS nor driver.
    descriptor.backend_options.dx12.shader_compiler = wgpu::Dx12Compiler::Fxc;
    wgpu::Instance::new(descriptor)
}

struct HostApp {
    /// The GPU instance, made on the first attach: a process that never
    /// opens a window loads no graphics driver.
    instance: Option<wgpu::Instance>,
    /// The open game window, for the event loop's message hook.
    game_window: crate::platform::GameWindow,
    machine: AttachMachine,
    window: Option<AttachedWindow>,
    /// The spec and scene source of the attach being processed.
    pending: Option<(WindowSpec, Option<Box<SceneSource>>)>,
    events: Sender<HostEvent>,
    world: Option<WorldSettings>,
    graphics: GraphicsOptions,
    /// The client settings windows open with, kept current.
    settings: ac_host::settings::PlayerSettings,
    /// Where they are saved.
    store: Option<ac_host::settings::SettingsStore>,
}

impl HostApp {
    /// Save one section of the settings; the failure, worded.
    fn save(&self, section: ac_ui::panels::options::Section) -> Option<String> {
        use ac_ui::panels::options::Section;
        let store = self.store.as_ref()?;
        let s = &self.settings;
        let (name, result) = match section {
            Section::Display => ("display", store.save_display(&s.display)),
            Section::Audio => ("sound", store.save_audio(&s.audio)),
            Section::Mixer => ("mixer", store.save_audio_mixer(&s.mixer)),
            Section::Chat => ("chat", store.save_chat(&s.chat)),
            Section::PluginUi => ("plugin appearance", store.save_plugin_ui(&s.plugin_ui)),
            Section::Camera => ("camera", store.save_camera_turning(&s.camera)),
            Section::Character(_) => return None,
        };
        result.err().map(|error| {
            format!(
                "settings: cannot save the {name} settings to {}: {error}",
                store.path().display()
            )
        })
    }

    fn report(&self, event: HostEvent) {
        // A dropped receiver means the owner stopped listening; the host keeps working.
        let _ = self.events.send(event);
    }

    /// `/mixer <arguments>`: the change is saved first and applied to the
    /// window's running mixer only once saved; the reply goes to the
    /// owner and into the window's status text (OpenAC's client-local
    /// text), as OpenAC's command binding says it.
    fn run_mixer(&mut self, arguments: &str) {
        let before = self.settings.mixer;
        let current = crate::audio::mixer_options_of(&before);
        let mut lines = Vec::new();
        let store = self.store.as_ref();
        let settings = &mut self.settings;
        let window = &mut self.window;
        ac_audio::options::run_mixer_command(
            &current,
            Some(arguments),
            |options| {
                let mixer = ac_host::settings::AudioMixerSettings {
                    retail_mixer: options.retail_mixer,
                    voice_count: options.voice_count,
                    use_authored_priority: options.use_authored_priority,
                    max_voices_per_wave: options.max_voices_per_wave,
                };
                if store.is_some_and(|store| store.save_audio_mixer(&mixer).is_err()) {
                    return false;
                }
                settings.mixer = mixer;
                true
            },
            |options| {
                window
                    .as_mut()
                    .is_some_and(|window| window.apply_mixer_options(*options))
            },
            |line| lines.push(line.to_owned()),
        );
        if let Some(window) = self.window.as_mut() {
            if self.settings.mixer != before {
                window.settings_changed_elsewhere(&self.settings);
            }
            for line in &lines {
                window.show_interface_text(line.clone());
            }
        }
        for line in lines {
            self.report(HostEvent::Notice(line));
        }
    }

    fn feed(&mut self, event_loop: &ActiveEventLoop, input: AttachInput) {
        for action in self.machine.step(input) {
            match action {
                AttachAction::OpenWindow => self.open(event_loop),
                AttachAction::CloseWindow(reason) => self.close(reason),
                AttachAction::RejectAttach => self.report(HostEvent::AttachRejected),
                AttachAction::ExitLoop => event_loop.exit(),
            }
        }
    }

    fn open(&mut self, event_loop: &ActiveEventLoop) {
        let Some((mut spec, scene)) = self.pending.take() else {
            unreachable!("the machine opens a window only in answer to an attach command");
        };
        // The window opens as the settings stand now: an options window
        // may have changed them since the process started.
        let display = &self.settings.display;
        if let Some((width, height)) = display.resolution_size() {
            spec.width = width;
            spec.height = height;
        }
        let graphics = GraphicsOptions {
            vsync: display.vsync,
            fullscreen: display.fullscreen,
            field_of_view_degrees: Some(display.field_of_view),
            ..self.graphics.clone()
        };
        let world = self.world.as_ref().map(|w| {
            let mut w = w.clone();
            w.audio.settings = crate::audio::audio_settings_of(&self.settings.audio);
            w.audio.mixer = crate::audio::mixer_options_of(&self.settings.mixer);
            w
        });
        let instance = self
            .instance
            .get_or_insert_with(|| gpu_instance(event_loop.owned_display_handle()));
        match AttachedWindow::open(
            event_loop,
            instance,
            &spec,
            world.as_ref(),
            &graphics,
            &self.settings,
        ) {
            Ok(mut window) => {
                window.set_scene(scene);
                window.set_layout_store(self.store.clone());
                self.report(HostEvent::Attached(window.adapter().clone()));
                window.request_redraw();
                self.game_window.opened(window.winit_window());
                self.window = Some(window);
            }
            Err(error) => {
                self.report(HostEvent::AttachFailed(error));
                self.feed(event_loop, AttachInput::AttachFailed);
            }
        }
    }

    fn close(&mut self, reason: DetachReason) {
        if let Some(window) = self.window.take() {
            self.game_window.closed();
            let (frames_presented, failure) = window.close();
            // The window's loaders are gone with it: the dats and caches it
            // read go too, and the next window opens them again.
            if let Some(world) = self.world.as_mut() {
                world.content = Arc::new(world.content.emptied());
            }
            if let Some(failure) = failure {
                self.report(HostEvent::GpuError(format!(
                    "the window closed after a teardown step failed: {failure}"
                )));
            }
            self.report(HostEvent::Detached {
                reason,
                frames_presented,
            });
        }
    }

    /// A window whose GPU device is known to be lost closes before any
    /// event or command reaches it, as when a frame meets the loss: nothing
    /// may call into a lost device, and the session plays on without the
    /// window.
    fn close_if_device_lost(&mut self, event_loop: &ActiveEventLoop) {
        if let Some(lost) = self.window.as_ref().and_then(AttachedWindow::device_lost) {
            self.report(HostEvent::RenderFailed(RenderError::Gpu(lost)));
            self.feed(event_loop, AttachInput::RenderFailed);
        }
    }

    fn redraw(&mut self, event_loop: &ActiveEventLoop) {
        let Some(window) = self.window.as_mut() else {
            return;
        };
        let result = window.render();
        let screenshots = window.take_screenshot_results();
        let failure = window.take_world_failure();
        let session_gone = window.session_gone();
        let close_requested = window.take_close_request();
        for notice in window.take_notices() {
            let _ = self.events.send(HostEvent::Notice(notice));
        }
        let presented = matches!(&result, Ok((outcome, _)) if !outcome.skipped_zero_area && !outcome.skipped_no_image);
        if presented {
            window.frame_rendered();
        }
        window.apply_cursor(event_loop);
        let saves = window.take_settings_saves();
        let mixer_requests = window.take_mixer_requests();
        if !saves.is_empty() {
            self.settings = window.settings().clone();
            for section in saves {
                if let Some(notice) = self.save(section) {
                    let _ = self.events.send(HostEvent::Notice(notice));
                }
            }
        }
        for arguments in mixer_requests {
            self.run_mixer(&arguments);
        }
        for (path, result) in screenshots {
            self.report(match result {
                Ok(stats) => HostEvent::ScreenshotSaved { path, stats },
                Err(error) => HostEvent::ScreenshotFailed(error),
            });
        }
        match result {
            Ok((_, report)) => {
                for fault in report.faults {
                    self.report(HostEvent::GpuError(fault));
                }
                if let Some(line) = report.profile {
                    self.report(HostEvent::FrameProfile(line));
                }
                if let Some(after) = report.revealed_after {
                    self.report(HostEvent::WorldRevealed { after });
                }
            }
            Err(error) => {
                self.report(HostEvent::RenderFailed(error));
                self.feed(event_loop, AttachInput::RenderFailed);
                return;
            }
        }
        if let Some(reason) = failure {
            self.report(HostEvent::WorldUnavailable(reason));
        }
        if session_gone {
            // The session ended or detached this window.
            self.feed(event_loop, AttachInput::DetachRequested);
        } else if close_requested {
            // The UI probe script's `close-client`: as the player closing
            // the window.
            self.feed(event_loop, AttachInput::WindowClosedByUser);
        }
    }
}

impl ApplicationHandler<HostCommand> for HostApp {
    /// A window whose surface gave no image is asked again once its wait
    /// is over; until then the loop sleeps.
    fn about_to_wait(&mut self, event_loop: &ActiveEventLoop) {
        let pending = self
            .window
            .as_mut()
            .and_then(|window| window.pending_retry(Instant::now()));
        event_loop.set_control_flow(pending.map_or(ControlFlow::Wait, ControlFlow::WaitUntil));
    }

    fn resumed(&mut self, _event_loop: &ActiveEventLoop) {
        // Windows open on command, not at startup.
    }

    /// A window still open when the loop ends outlives the loop's display
    /// connection: what holds the screen awake goes now, while it is open.
    fn exiting(&mut self, _event_loop: &ActiveEventLoop) {
        if let Some(window) = self.window.as_mut() {
            window.release_screen();
        }
    }

    fn user_event(&mut self, event_loop: &ActiveEventLoop, command: HostCommand) {
        self.close_if_device_lost(event_loop);
        let input = match command {
            HostCommand::Attach(spec, scene) => {
                self.pending = Some((spec, scene));
                AttachInput::AttachRequested
            }
            HostCommand::Detach => AttachInput::DetachRequested,
            HostCommand::Ping(answered) => {
                answered.fetch_add(1, Ordering::Relaxed);
                return;
            }
            HostCommand::Zoom(steps) => {
                if let Some(window) = self.window.as_mut() {
                    window.zoom(steps);
                }
                return;
            }
            HostCommand::Vsync(vsync) => {
                self.graphics.vsync = vsync;
                self.settings.display.vsync = vsync;
                if let Some(notice) = self.save(ac_ui::panels::options::Section::Display) {
                    self.report(HostEvent::Notice(notice));
                }
                if let Some(window) = self.window.as_mut() {
                    window.set_vsync(vsync);
                    window.settings_changed_elsewhere(&self.settings);
                }
                return;
            }
            HostCommand::ToggleFly => {
                if let Some(window) = self.window.as_mut() {
                    window.toggle_fly();
                }
                return;
            }
            HostCommand::UiScript(step) => {
                if let Some(window) = self.window.as_mut() {
                    window.script_ui(&step);
                    window.request_redraw();
                    for notice in window.take_notices() {
                        let _ = self.events.send(HostEvent::Notice(notice));
                    }
                }
                return;
            }
            HostCommand::UiType(line, enter) => {
                if let Some(window) = self.window.as_mut() {
                    window.type_line(&line, enter);
                    window.request_redraw();
                    for notice in window.take_notices() {
                        let _ = self.events.send(HostEvent::Notice(notice));
                    }
                }
                return;
            }
            HostCommand::CycleTimeOfDay
            | HostCommand::CycleWeather
            | HostCommand::DumpNearby
            | HostCommand::ToggleMute => {
                if let Some(window) = self.window.as_mut() {
                    match command {
                        HostCommand::CycleTimeOfDay => window.cycle_time_of_day(),
                        HostCommand::CycleWeather => window.cycle_weather(),
                        HostCommand::DumpNearby => window.dump_nearby(),
                        _ => window.toggle_mute(),
                    }
                    for notice in window.take_notices() {
                        let _ = self.events.send(HostEvent::Notice(notice));
                    }
                }
                return;
            }
            HostCommand::Mixer(arguments) => {
                self.run_mixer(&arguments);
                return;
            }
            HostCommand::Screenshot(path) => {
                match self.window.as_mut() {
                    Some(window) => window.request_screenshot(path),
                    None => self.report(HostEvent::ScreenshotFailed(RenderError::Screenshot(
                        "no window is open".into(),
                    ))),
                }
                return;
            }
            HostCommand::Shutdown => AttachInput::ShutdownRequested,
        };
        self.feed(event_loop, input);
        // An attach that was rejected leaves its spec unused.
        self.pending = None;
    }

    fn window_event(&mut self, event_loop: &ActiveEventLoop, id: WindowId, event: WindowEvent) {
        self.close_if_device_lost(event_loop);
        let Some(window) = self.window.as_mut().filter(|w| w.id() == id) else {
            // A late event for a window that is already gone.
            return;
        };
        match event {
            WindowEvent::CloseRequested => self.feed(event_loop, AttachInput::WindowClosedByUser),
            WindowEvent::Resized(size) => window.resize(size),
            WindowEvent::Moved(_) | WindowEvent::ScaleFactorChanged { .. } => {
                window.monitor_changed();
            }
            WindowEvent::MouseWheel { delta, .. } => {
                // Notches away from the user (positive y) or toward, and
                // sideways (positive x toward the left).
                let (x, y) = match delta {
                    MouseScrollDelta::LineDelta(x, y) => (x, y),
                    MouseScrollDelta::PixelDelta(p) => (p.x as f32 / 120.0, p.y as f32 / 120.0),
                };
                window.wheel(x, y);
            }
            WindowEvent::ModifiersChanged(modifiers) => window.modifiers_changed(modifiers.state()),
            WindowEvent::RedrawRequested => self.redraw(event_loop),
            WindowEvent::Focused(focused) => window.focus_changed(focused),
            WindowEvent::KeyboardInput { event, .. } => window.keyboard(&event),
            WindowEvent::MouseInput { state, button, .. } => {
                window.mouse_button(button, state == ElementState::Pressed);
            }
            WindowEvent::CursorMoved { position, .. } => {
                window.cursor_moved(position.x, position.y);
            }
            WindowEvent::CursorLeft { .. } => window.cursor_left(),
            _ => {}
        }
    }

    fn device_event(
        &mut self,
        event_loop: &ActiveEventLoop,
        _device: DeviceId,
        event: DeviceEvent,
    ) {
        self.close_if_device_lost(event_loop);
        if let (Some(window), DeviceEvent::MouseMotion { delta }) = (self.window.as_mut(), event) {
            window.mouse_motion(delta.0, delta.1);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// A live dungeon login: the launched client sends
    /// its session the attach at once, and its window opened 0.64 s after
    /// the character was in the world. The session decides at the player's
    /// creation whether login complete waits for a window's presentation;
    /// in OpenAC's graphical client it always does (its window exists
    /// before its session: `LocalPlayerTeleportController`
    /// `TickLoginPresentation` sends login complete at `FireLoginComplete`).
    /// So the window says whether it draws the world with its attach
    /// request, not once it has opened.
    #[test]
    fn a_window_says_whether_it_draws_the_world_with_its_attach_request() {
        for draws_world in [true, false] {
            let (_to_window, presentation) = mpsc::channel();
            let (commands, session) = mpsc::channel();
            let _scene = session_scene(presentation, commands, draws_world);
            match session.try_recv() {
                Ok(SessionCommand::PresentsWorld(presents)) => assert_eq!(presents, draws_world),
                other => panic!("the session heard {other:?}"),
            }
            assert!(session.try_recv().is_err(), "said once");
        }
    }
}
