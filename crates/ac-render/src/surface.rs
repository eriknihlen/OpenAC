//! One attached window and the GPU objects built for it.
//!
//! Everything here is rebuilt on attach and dropped on detach: the window, its surface, the
//! adapter, the device, the queue and the world renderer (with its loader thread). Only the
//! `wgpu::Instance` (no GPU state, just the loaded backends) and the process's content outlive a
//! window.

use std::collections::HashSet;
use std::future::Future;
use std::path::{Path, PathBuf};
use std::pin::pin;
use std::sync::Arc;
use std::task::{Context, Poll, Wake, Waker};
use std::thread::{self, Thread};
use std::time::{Duration, Instant};

use winit::dpi::PhysicalSize;
use winit::event::{ElementState, KeyEvent, MouseButton};
use winit::event_loop::ActiveEventLoop;
use winit::keyboard::{KeyCode, ModifiersState, PhysicalKey};
use winit::monitor::MonitorHandle;
use winit::window::{CursorGrabMode, Window, WindowId};

use crate::audio::{PlayerPlace, WindowAudio};
use crate::camera::{CameraMode, ChaseTuning, FlyInput};
use crate::device_health::DeviceHealth;
use crate::effects::{EffectSound, ServerEffect};
use crate::facts::{PresentationFacts, SharedFacts};
use crate::frame::{FrameInput, FrameOutcome, FramePhases, WorldOutcome, render_frame};
use crate::gameplay_input::{DeviceState, GameplayOutput};
use crate::gpu::{self, AdapterCandidate, GpuFault};
use crate::gpu_timer::GpuTimer;
use crate::input::InputState;
use crate::lifetime::OrderedTeardown;
use crate::pacing::DisplayPacing;
use crate::platform::HostOs;
use crate::portal_space::PortalCue;
use crate::profiler::{FrameProfiler, FrameRateTitle, FrameStage};
use crate::renderer::{WorldRenderer, WorldSettings, WorldStats};
use crate::scene::{SceneEffect, SceneSource};
use crate::title::{TitleFacts, TitleStats, TitleSubject, format_title};
use crate::ui::input::{self as ui_input, Routed, WorldInput};
use crate::ui::{UiBatch, UiRenderer, WindowUi};
use crate::ui_scale::UiScale;
use crate::{RenderError, WindowSpec};
use ac_core::sky::EnvironmentEffect;
use ac_runtime::session::SessionCommand;
use ac_runtime::world::transit::{DestinationReadiness, DestinationShown, PortalSpaceEvent};
use glam::Vec3;

/// What the owner learns about the GPU once a window is attached.
#[derive(Debug, Clone)]
pub struct AdapterSummary {
    /// The graphics API in use (Vulkan, DX12, ...).
    pub backend: wgpu::Backend,
    /// The adapter's name as the driver reports it.
    pub name: String,
    /// How the adapter was chosen.
    pub selection: String,
    /// Every adapter that could present to the window, one line each.
    pub candidates: Vec<String>,
    /// Where the capability report was written, if anywhere.
    pub report: Option<PathBuf>,
    /// The adapter can time GPU work (the frame profiler's GPU column).
    pub timestamps: bool,
    /// The driver's name and version.
    pub driver: String,
}

/// How windows render: GPU choice, pacing and diagnostics. Shared by every
/// window of the host.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct GraphicsOptions {
    /// Adapter by enumeration index or name substring (`OPENAC_GPU_ADAPTER`).
    pub gpu_adapter: Option<String>,
    /// Pretend this required capability is missing (`OPENAC_GPU_FORCE_UNSUPPORTED`).
    pub gpu_force_unsupported: Option<String>,
    /// Where the capability report and other diagnostics go.
    pub diagnostics_dir: Option<PathBuf>,
    /// The player's vsync preference.
    pub vsync: bool,
    /// Turn off vsync and the software limiter, to measure (`OPENAC_UNCAPPED_RENDER`).
    pub uncapped: bool,
    /// Measure every frame and print a report every five seconds (`OPENAC_FRAME_PROF`).
    pub frame_profiler: bool,
    /// Keep every frame's timings and write them here as CSV when the
    /// window closes (`OPENAC_FRAME_HISTORY`).
    pub frame_history: Option<PathBuf>,
    /// Where the open window publishes what a crash report wants.
    pub facts: SharedFacts,
    /// Open windows fullscreen (a mode switch to the resolution setting).
    pub fullscreen: bool,
    /// The field of view the player chose, degrees (`None`: the game's 90).
    pub field_of_view_degrees: Option<f32>,
    /// The player's key bindings file (`keybinds.json`; `None`: the retail
    /// key map).
    pub key_bindings_file: Option<PathBuf>,
    /// The folder of retail `.keymap` profiles (the game's folder).
    pub keymap_directory: Option<PathBuf>,
    /// The game's own install folder (`OPENAC_AC_DIR`): the interface
    /// reads the player's `controlsntrols.ini` there (`None`: none).
    pub ac_dir: Option<PathBuf>,
    /// Where the screenshot key saves (`screenshots` beside the settings
    /// file; `None`: the key does nothing).
    pub screenshots_directory: Option<PathBuf>,
    /// Decode block-compressed textures to RGBA even when the GPU samples
    /// them (`OPENAC_NO_BLOCK_COMPRESSION`), to compare.
    pub no_block_compression: bool,
    /// A UI probe script to play against the window
    /// (`OPENAC_UI_PROBE_SCRIPT`).
    pub ui_probe_script: Option<PathBuf>,
    /// Dump the interface's element tree when the script starts
    /// (`OPENAC_UI_PROBE_DUMP=1`).
    pub ui_probe_dump: bool,
    /// Where named screenshots, checkpoints and signals go
    /// (`OPENAC_AUTOMATION_ARTIFACT_DIR`).
    pub automation_artifact_dir: Option<PathBuf>,
    /// Log the motion states the window takes up for the player and the
    /// selection (`OPENAC_MOTION_LOG=1`).
    pub motion_log: bool,
}

/// The screenshot key's file in `directory`: the first of
/// `ScreenShot00000.png` .. `ScreenShot99999.png` that `taken` does not
/// claim (a file that exists or one already asked for).
#[must_use]
pub fn retail_screenshot_path(directory: &Path, taken: impl Fn(&Path) -> bool) -> Option<PathBuf> {
    (0..100_000)
        .map(|index| directory.join(format!("ScreenShot{index:05}.png")))
        .find(|path| !taken(path))
}

/// The swapchain image of the frame being drawn and what is recorded for it.
struct OpenFrame {
    texture: wgpu::SurfaceTexture,
    view: wgpu::TextureView,
    encoder: Option<wgpu::CommandEncoder>,
    reconfigure_after: bool,
}

/// A size `monitor` reports in pixels (its own, one of its modes'), in
/// the unit the window's size and the resolution picks are counted in
/// (points on macOS, as OpenAC's window library lists modes there).
fn in_window_unit(monitor: &MonitorHandle, pixels: (i32, i32)) -> (i32, i32) {
    match crate::display_mode::size_unit(HostOs::current()) {
        crate::display_mode::SizeUnit::Pixels => pixels,
        unit @ crate::display_mode::SizeUnit::Points => {
            let side = |p: i32| u32::try_from(p).unwrap_or(0);
            let (w, h) =
                unit.from_pixels((side(pixels.0), side(pixels.1)), || monitor.scale_factor());
            (i32::try_from(w).unwrap_or(0), i32::try_from(h).unwrap_or(0))
        }
    }
}

/// `monitor`'s size, in the window's size unit.
fn desktop_of(monitor: &MonitorHandle) -> Option<(i32, i32)> {
    crate::platform::monitor_size(monitor).map(|size| in_window_unit(monitor, size))
}

/// The fullscreen modes offered for the monitor `window` is on: the
/// monitor's curated modes, else the standard sizes (OpenAC
/// `DisplayModeCatalog.Resolutions ?? AvailableResolutions`).
fn fullscreen_modes_of(window: &Window) -> Vec<String> {
    use ac_ui::panels::options::resolutions::{AVAILABLE_RESOLUTIONS, curate};
    let monitor = window.current_monitor();
    let size = |monitor: &MonitorHandle, s: PhysicalSize<u32>| {
        in_window_unit(
            monitor,
            (
                i32::try_from(s.width).unwrap_or(0),
                i32::try_from(s.height).unwrap_or(0),
            ),
        )
    };
    let curated = monitor
        .as_ref()
        .and_then(desktop_of)
        .filter(|(w, h)| *w > 0 && *h > 0)
        .map(|desktop| {
            let modes: Vec<(i32, i32)> = monitor
                .iter()
                .flat_map(|monitor| monitor.video_modes().map(|m| size(monitor, m.size())))
                .collect();
            curate(&modes, desktop)
        })
        .unwrap_or_default();
    if curated.is_empty() {
        AVAILABLE_RESOLUTIONS
            .iter()
            .map(|s| (*s).to_owned())
            .collect()
    } else {
        curated
    }
}

/// The resolutions the display menu offers for the monitor `window` is on.
fn resolution_choices_of(window: &Window) -> Vec<String> {
    let monitor = window.current_monitor();
    let desktop = monitor.as_ref().and_then(desktop_of);
    let modes: Vec<(i32, i32)> = monitor
        .iter()
        .flat_map(|monitor| {
            monitor.video_modes().map(|m| {
                let s = m.size();
                in_window_unit(
                    monitor,
                    (
                        i32::try_from(s.width).unwrap_or(0),
                        i32::try_from(s.height).unwrap_or(0),
                    ),
                )
            })
        })
        .collect();
    crate::ui::options::resolution_choices(&modes, desktop)
}

/// How long a window whose surface gave no image waits before asking
/// again. Asking at once builds frames nobody sees as fast as the CPU
/// allows, for as long as the surface refuses (a covered window, a locked
/// session, a display being switched).
pub(crate) const NO_IMAGE_RETRY: Duration = Duration::from_millis(100);

const SURFACE_DRAWING: &str = "drawing";
const SURFACE_OUTDATED: &str = "outdated (reconfigured)";
const SURFACE_TIMED_OUT: &str = "timed out";
const SURFACE_OCCLUDED: &str = "occluded";
const SURFACE_LOST: &str = "lost";
const SURFACE_INVALID: &str = "failed validation";

/// When the window draws again after a frame: at once after one it drew,
/// after [`NO_IMAGE_RETRY`] after one its surface gave no image for, and
/// not until it is resized after one it skipped for having no area.
fn redraw_after(outcome: &FrameOutcome) -> Option<Duration> {
    if outcome.skipped_zero_area {
        None
    } else if outcome.skipped_no_image {
        Some(NO_IMAGE_RETRY)
    } else {
        Some(Duration::ZERO)
    }
}

/// What the window's surface last gave, so each change is reported once.
#[derive(Debug, Default)]
struct SurfaceNotes {
    last: Option<&'static str>,
}

impl SurfaceNotes {
    fn last(&self) -> Option<&'static str> {
        self.last
    }

    /// Records `state`; the line to report when it changed (the first
    /// image is not news).
    fn observe(&mut self, state: &'static str) -> Option<String> {
        match self.last.replace(state) {
            Some(was) if was == state => None,
            None if state == SURFACE_DRAWING => None,
            Some(was) => Some(format!("surface {state} (was {was})")),
            None => Some(format!("surface {state}")),
        }
    }
}

/// An open window drawing the world of the session it follows.
pub(crate) struct AttachedWindow {
    // Drop order matters: the frame, the renderer and the surface go before the device and the
    // window.
    frame: Option<OpenFrame>,
    gpu_timer: Option<GpuTimer>,
    world: Option<WorldRenderer>,
    /// The retail UI's GPU side.
    ui_renderer: Option<UiRenderer>,
    /// The screen-brightness ramp, drawn over the finished frame in
    /// exclusive fullscreen (made on first use).
    brightness: Option<crate::gamma::BrightnessPass>,
    /// The retail UI: its panels, fed from the scene.
    ui: Option<WindowUi>,
    /// Keeps the screen awake while the window is fullscreen (let go
    /// before the window closes).
    screen_saver: crate::screen_saver::ScreenSaver,
    /// What a fullscreen request to the compositor still waits for.
    fullscreen_awaiting: crate::display_mode::Awaiting,
    surface: wgpu::Surface<'static>,
    device: wgpu::Device,
    queue: wgpu::Queue,
    config: wgpu::SurfaceConfiguration,
    window: Arc<Window>,
    /// The window was minimized when last told to the session.
    reported_minimized: bool,
    clear_color: wgpu::Color,
    adapter: AdapterSummary,
    frames_presented: u64,
    /// The title the window shows (set again only when it changes).
    title: String,
    scene: Option<Box<SceneSource>>,
    world_failure_reported: bool,
    health: Arc<DeviceHealth>,
    reported_faults: HashSet<String>,
    pacing: DisplayPacing,
    profiler: FrameProfiler,
    frame_history: Option<PathBuf>,
    title_rate: FrameRateTitle,
    last_frame: Option<Instant>,
    /// Screenshots asked for and taken since the host last looked.
    screenshots: Screenshots<Result<Option<WorldStats>, RenderError>>,
    /// Where the screenshot key saves.
    screenshots_directory: Option<PathBuf>,
    /// The screenshot key's requests not yet written.
    key_screenshots: Vec<PathBuf>,
    /// Held keys and buttons, focus and mouse look.
    input: InputState<KeyCode>,
    /// The gameplay keys: the dispatcher, mouse look, the held movement
    /// last sent.
    gameplay: crate::gameplay_input::GameplayInput,
    /// The clock mouse look and the dispatcher read (seconds since open).
    input_clock: Instant,
    /// When the gameplay input last ticked.
    gameplay_tick: Option<Instant>,
    /// The modifier keys held, as the window last reported them.
    modifiers: ModifiersState,
    /// The last cursor position, for drags.
    cursor: Option<(f64, f64)>,
    /// Framebuffer pixels per UI unit as the window's monitor has it,
    /// before the player's UI Scale (see [`Self::ui_scale`]).
    platform_scale: UiScale,
    /// The UI Scale option in force, percent: it follows the settings once
    /// a change settles (not while its slider is dragged).
    ui_scale_percent: i32,
    /// Where this window publishes what a crash report wants.
    facts: SharedFacts,
    last_world: WorldOutcome,
    /// The profiler's frame number while the phases run.
    frame_index: u64,
    /// GPU times read during a frame, for the profiler.
    gpu_samples: Vec<(u64, std::time::Duration)>,
    /// The window's sound (dropped with the window).
    audio: Option<WindowAudio>,
    /// The retail mouse cursor on the window.
    cursor_art: crate::ui::cursor::CursorManager,
    /// What clicks on the 3-D view remember (a target mode's claim).
    world_clicks: crate::world_click::WorldClicks,
    /// Messages for the owner (sound state, debug cycles), reported after
    /// the next frame or command.
    notices: Vec<String>,
    /// What the surface last gave, for logging its changes.
    surface_watch: SurfaceNotes,
    /// When to ask again a surface that gave no image.
    retry_at: Option<Instant>,
    /// The client settings in force in this window.
    settings: ac_host::settings::PlayerSettings,
    /// Sections the options window changed and the host should save.
    settings_saves: Vec<ac_ui::panels::options::Section>,
    /// `/mixer` lines typed into the chat (their arguments), for the host,
    /// which owns the settings file, to run.
    mixer_requests: Vec<String>,
    /// A login or portal reveal held the world back at the last look.
    world_held: bool,
    /// The UI probe script being played (`OPENAC_UI_PROBE_SCRIPT`).
    ui_probe: Option<ac_ui::automation::ScriptRunner>,
    /// Named screenshots, checkpoints and signals
    /// (`OPENAC_AUTOMATION_ARTIFACT_DIR`).
    world_automation: Option<crate::automation::WorldAutomation>,
    /// Portal arrivals shown so far.
    portal_materializations: u32,
    /// Automation asked the window to close.
    close_requested: bool,
}

/// What [`AttachedWindow::render`] has to report besides its result.
#[derive(Debug, Default)]
pub(crate) struct FrameReport {
    /// Non-fatal GPU faults seen for the first time.
    pub(crate) faults: Vec<String>,
    /// The frame profiler's five-second line.
    pub(crate) profile: Option<String>,
    /// The world was just shown after being withheld this long.
    pub(crate) revealed_after: Option<std::time::Duration>,
}

impl AttachedWindow {
    /// Opens the window and builds a surface, adapter and device that can present to it, and the
    /// world renderer when `world` says where the dats are.
    pub(crate) fn open(
        event_loop: &ActiveEventLoop,
        instance: &wgpu::Instance,
        spec: &WindowSpec,
        world: Option<&WorldSettings>,
        options: &GraphicsOptions,
        settings: &ac_host::settings::PlayerSettings,
    ) -> Result<Self, RenderError> {
        // The size picked is in points on macOS (the UI's unit there), in
        // pixels elsewhere; the drawing surface follows in pixels.
        let attributes = Window::default_attributes()
            .with_title(spec.title.clone())
            .with_active(true)
            .with_inner_size(
                crate::display_mode::size_unit(HostOs::current()).request(spec.width, spec.height),
            );
        let window = Arc::new(event_loop.create_window(attributes)?);
        // A window opened onto a running session (the console's or the
        // launcher's switch to graphical) comes to the front with the
        // keyboard, though this process was not the one in front. A
        // compositor may refuse (Wayland grants focus only to a request it
        // handed a token for; some X11 window managers prevent focus
        // stealing): the window then opens behind, as it would without this.
        window.focus_window();
        // Fullscreen is a mode switch to the chosen resolution, as later
        // (on Wayland the compositor's fullscreen at the monitor's mode).
        let mut display_lines = Vec::new();
        let mut fullscreen_awaiting = crate::display_mode::Awaiting::Nothing;
        if options.fullscreen {
            let display = ac_host::settings::DisplaySettings {
                fullscreen: true,
                ..settings.display.clone()
            };
            let offered = fullscreen_modes_of(&window);
            crate::display_mode::apply_display(
                &display,
                &mut crate::display_mode::WinitDisplay(&window),
                &mut crate::display_mode::WinitDisplay(&window),
                &mut fullscreen_awaiting,
                |m| offered.iter().any(|o| o == m),
                &mut display_lines,
            );
        }
        let mut screen_saver = crate::screen_saver::ScreenSaver::default();
        screen_saver.follow(&window, &mut display_lines);
        let surface = instance.create_surface(Arc::clone(&window))?;

        let (adapter, mut summary) = choose_adapter(instance, &surface, options)?;
        let mut features = wgpu::Features::empty();
        if summary.timestamps && (options.frame_profiler || options.frame_history.is_some()) {
            features |= wgpu::Features::TIMESTAMP_QUERY;
        }
        // Multisample counts beyond the WebGPU guarantee (4) need the
        // adapter's own format features.
        if adapter
            .features()
            .contains(wgpu::Features::TEXTURE_ADAPTER_SPECIFIC_FORMAT_FEATURES)
        {
            features |= wgpu::Features::TEXTURE_ADAPTER_SPECIFIC_FORMAT_FEATURES;
        }
        // Block-compressed dat textures stay compressed on the GPU when it can
        // sample them; without it they are decoded to RGBA on the loader.
        if !options.no_block_compression
            && adapter
                .features()
                .contains(wgpu::Features::TEXTURE_COMPRESSION_BC)
        {
            features |= wgpu::Features::TEXTURE_COMPRESSION_BC;
        }
        let (device, queue) =
            block_on(adapter.request_device(&device_descriptor("ac-render device", features)))?;
        let health = DeviceHealth::watch(&device);

        let mut pacing = DisplayPacing::new(options.vsync, options.uncapped);
        pacing.set_monitor_refresh(monitor_refresh_hz(&window));

        let size = window.inner_size();
        let mut config = surface
            .get_default_config(&adapter, size.width.max(1), size.height.max(1))
            .ok_or(RenderError::SurfaceUnsupported)?;
        // Colors are written as stored, like the game's own frame buffer: a non-sRGB format, so
        // the hardware does not re-encode them.
        let capabilities = surface.get_capabilities(&adapter);
        if let Some(format) = capabilities.formats.iter().find(|f| !f.is_srgb()) {
            config.format = *format;
        }
        // Allow copying a frame out for screenshots.
        config.usage |= wgpu::TextureUsages::COPY_SRC;
        if !capabilities.usages.contains(wgpu::TextureUsages::COPY_SRC) {
            config.usage = wgpu::TextureUsages::RENDER_ATTACHMENT;
        }
        config.present_mode = pacing.policy().present_mode();
        if size.width != 0 && size.height != 0 {
            surface.configure(&device, &config);
        }

        let mut notices = std::mem::take(&mut display_lines);
        if !device
            .features()
            .contains(wgpu::Features::TEXTURE_COMPRESSION_BC)
        {
            notices.push(if options.no_block_compression {
                "textures: block-compressed textures are decoded to RGBA (OPENAC_NO_BLOCK_COMPRESSION)"
                    .into()
            } else {
                "textures: the GPU cannot sample block-compressed textures; they are decoded to RGBA"
                    .into()
            });
        }
        let audio = match world {
            Some(settings) => match WindowAudio::open(&settings.content, &settings.audio) {
                Ok(Some(audio)) => {
                    notices.push(format!("audio: playing on {}", audio.device()));
                    Some(audio)
                }
                Ok(None) => None,
                Err(error) => {
                    notices.push(format!("audio: no sound: {error}"));
                    None
                }
            },
            None => None,
        };
        // The retail UI reads its layouts, fonts and art from the dats; a
        // window without dats draws no UI.
        let choices = resolution_choices_of(&window);
        let platform_scale = UiScale::for_window(HostOs::current(), window.scale_factor());
        // The key configuration screen edits the dispatcher's bindings and
        // saves where they were read from.
        let keyboard_storage = match (&options.key_bindings_file, &options.keymap_directory) {
            (Some(bindings_file), Some(keymap_directory)) => Some(Box::new(
                crate::ui::keyboard_window::KeyboardFiles {
                    bindings_file: bindings_file.clone(),
                    keymap_directory: keymap_directory.clone(),
                }
                .storage(),
            )
                as Box<dyn ac_ui::storage::UiStorage>),
            _ => None,
        };
        let ui = world.and_then(|world| {
            let built = world.content.ui_dats().and_then(|dats| {
                WindowUi::build_with(
                    dats,
                    platform_scale.ui_size((config.width, config.height)),
                    settings,
                    choices,
                    keyboard_storage,
                )
            });
            match built {
                Ok(mut ui) => {
                    notices.extend(ui.take_notices());
                    notices.push(format!("ui: mounted {}", ui.mounted().join(", ")));
                    Some(ui)
                }
                Err(error) => {
                    notices.push(format!("ui: no retail UI: {error}"));
                    None
                }
            }
        });
        let ui_renderer = ui
            .as_ref()
            .map(|_| UiRenderer::new(&device, &queue, config.format));
        let msaa_supported = supported_sample_counts(&adapter, &device, config.format);
        let display = crate::quality::effective_display(&settings.display);
        let world = world.map(|world_settings| {
            WorldRenderer::new(
                &device,
                &queue,
                config.format,
                config.width,
                config.height,
                world_settings,
                &display,
                &msaa_supported,
            )
        });
        let mut world = world;
        if let Some(world) = world.as_mut() {
            world.set_motion_log(options.motion_log);
            // The atmospheric rendering's half-float world target.
            world.set_hdr_sample_counts(supported_sample_counts(
                &adapter,
                &device,
                crate::renderer::atmosphere::HDR_FORMAT,
            ));
        }
        if let (Some(world), Some(degrees)) = (world.as_mut(), options.field_of_view_degrees) {
            world.set_game_fov(degrees.to_radians());
        }
        let gpu_timer = if features.contains(wgpu::Features::TIMESTAMP_QUERY) {
            GpuTimer::new(&device, &queue)
        } else {
            None
        };
        summary.timestamps = gpu_timer.is_some();
        let gameplay = crate::gameplay_input::load(
            options.key_bindings_file.as_deref(),
            options.keymap_directory.as_deref(),
            &mut notices,
        );
        let mut ui = ui;
        if let Some(ui) = ui.as_mut() {
            ui.attach_key_bindings(gameplay.dispatcher().bindings());
            // The player's style sheet in the game folder (OpenAC
            // `InteractionRetainedUiComposition`): a missing file is an empty
            // sheet.
            if let Some(dir) = &options.ac_dir {
                let path = dir.join("controls").join("controls.ini");
                match ac_ui::controls_ini::ControlsIni::load(&path) {
                    Ok(controls) => ui.set_controls(&controls),
                    Err(error) => notices.push(format!("ui: {}: {error}", path.display())),
                }
            }
            notices.extend(ui.take_notices());
        }

        Ok(Self {
            frame: None,
            gpu_timer,
            world,
            ui_renderer,
            brightness: None,
            ui,
            screen_saver,
            fullscreen_awaiting,
            surface,
            device,
            queue,
            config,
            window,
            reported_minimized: false,
            clear_color: spec.clear_color,
            adapter: summary,
            frames_presented: 0,
            title: spec.title.clone(),
            scene: None,
            world_failure_reported: false,
            health,
            reported_faults: HashSet::new(),
            pacing,
            profiler: FrameProfiler::new(options.frame_profiler, options.frame_history.is_some()),
            frame_history: options.frame_history.clone(),
            title_rate: FrameRateTitle::default(),
            last_frame: None,
            screenshots: Screenshots::default(),
            screenshots_directory: options.screenshots_directory.clone(),
            key_screenshots: Vec::new(),
            input: InputState::new(),
            gameplay,
            input_clock: Instant::now(),
            gameplay_tick: None,
            modifiers: ModifiersState::empty(),
            cursor: None,
            platform_scale,
            ui_scale_percent: settings.display.ui_scale_percent,
            facts: options.facts.clone(),
            last_world: WorldOutcome::default(),
            frame_index: 0,
            gpu_samples: Vec::new(),
            audio,
            cursor_art: crate::ui::cursor::CursorManager::new(),
            world_clicks: crate::world_click::WorldClicks::default(),
            notices,
            surface_watch: SurfaceNotes::default(),
            retry_at: None,
            settings: settings.clone(),
            settings_saves: Vec::new(),
            mixer_requests: Vec::new(),
            world_held: false,
            ui_probe: options
                .ui_probe_script
                .as_deref()
                .map(|path| ac_ui::automation::ScriptRunner::load(path, options.ui_probe_dump))
                .or_else(|| {
                    options
                        .ui_probe_dump
                        .then(|| ac_ui::automation::ScriptRunner::parse("", true))
                }),
            world_automation: options
                .automation_artifact_dir
                .clone()
                .map(crate::automation::WorldAutomation::new),
            portal_materializations: 0,
            close_requested: false,
        })
        .map(|mut window| {
            window.apply_camera_tuning();
            window
        })
    }

    /// The camera settings on the chase camera.
    fn apply_camera_tuning(&mut self) {
        let tuning = ChaseTuning::from(&self.settings.camera);
        if let Some(world) = self.world.as_mut() {
            world.cameras_mut().chase.set_tuning(tuning);
        }
    }

    /// The client settings in force in this window.
    pub(crate) fn settings(&self) -> &ac_host::settings::PlayerSettings {
        &self.settings
    }

    /// The sections changed since the last call, for the host to save.
    pub(crate) fn take_settings_saves(&mut self) -> Vec<ac_ui::panels::options::Section> {
        std::mem::take(&mut self.settings_saves)
    }

    /// The `/mixer` lines typed since the last call (their arguments).
    pub(crate) fn take_mixer_requests(&mut self) -> Vec<String> {
        std::mem::take(&mut self.mixer_requests)
    }

    /// New mixer settings for the running mixer; false when this window
    /// has no sound running.
    pub(crate) fn apply_mixer_options(&mut self, mixer: ac_audio::AudioMixerOptions) -> bool {
        match self.audio.as_mut() {
            Some(audio) => {
                audio.apply_mixer(mixer);
                true
            }
            None => false,
        }
    }

    /// A line in the client's status text (OpenAC's client-local text).
    pub(crate) fn show_interface_text(&mut self, text: String) {
        self.send_session(SessionCommand::ShowInterfaceText(text));
    }

    /// Apply what the options window changed: the display (vsync, field of
    /// view, fullscreen, the window size), the sound and the mixer at once;
    /// the camera and the rest of the display are stored for now.
    fn follow_settings(&mut self) {
        use ac_ui::panels::options::Section;
        let Some(ui) = self.ui.as_mut() else {
            return;
        };
        let changes = ui.take_settings_changes();
        if changes.is_empty() {
            return;
        }
        let new = ui.settings().clone();
        let old = std::mem::replace(&mut self.settings, new.clone());
        for (section, persist) in changes {
            match section {
                Section::Display => {
                    let (o, n) = (&old.display, &new.display);
                    if o.vsync != n.vsync {
                        self.set_vsync(n.vsync);
                    }
                    if o.field_of_view != n.field_of_view
                        && let Some(world) = self.world.as_mut()
                    {
                        world.set_game_fov(n.field_of_view.to_radians());
                    }
                    if o.fullscreen != n.fullscreen || o.resolution != n.resolution {
                        self.apply_window_mode(n);
                    }
                    // The UI takes its new size on its next frame, and the
                    // windows are placed again on it as after a resize.
                    if persist {
                        self.ui_scale_percent = n.ui_scale_percent;
                    }
                    if let Some(world) = self.world.as_mut() {
                        world.apply_display(
                            &self.device,
                            &self.queue,
                            &crate::quality::effective_display_for_focus(
                                n,
                                self.input.is_focused(),
                            ),
                        );
                    }
                }
                Section::Audio => {
                    if let Some(audio) = self.audio.as_mut() {
                        audio.apply_settings(crate::audio::audio_settings_of(&new.audio));
                    }
                }
                Section::Mixer => {
                    if let Some(audio) = self.audio.as_mut() {
                        audio.apply_mixer(crate::audio::mixer_options_of(&new.mixer));
                    }
                }
                Section::Camera => self.apply_camera_tuning(),
                Section::Chat | Section::Character(_) | Section::PluginUi => {}
            }
            if persist
                && !matches!(section, Section::Character(_))
                && !self.settings_saves.contains(&section)
            {
                self.settings_saves.push(section);
            }
        }
    }

    /// What the key configuration screen asked of the key dispatcher: new
    /// live bindings, a key capture.
    fn follow_keyboard(&mut self) {
        let Some(ui) = self.ui.as_mut() else {
            return;
        };
        for request in ui.take_keyboard_requests() {
            match request {
                crate::ui::keyboard_window::KeyboardRequest::Apply(bindings) => {
                    self.gameplay.set_bindings(bindings);
                }
                crate::ui::keyboard_window::KeyboardRequest::BeginCapture => {
                    self.gameplay.begin_capture();
                }
            }
        }
    }

    /// A key capture ended: the screen takes it, or asks for another key
    /// (one it cannot bind).
    fn keyboard_captured(&mut self, chord: Option<ac_ui::input::KeyChord>) {
        let again = self
            .ui
            .as_mut()
            .is_some_and(|ui| ui.keyboard_captured(chord));
        if again {
            self.gameplay.begin_capture();
        }
        // What the screen asked while taking the key (live bindings, a
        // question).
        if let Some(ui) = self.ui.as_mut() {
            let commands = ui.drain();
            self.notices.extend(ui.take_notices());
            self.send_to_session(commands);
        }
        self.follow_keyboard();
    }

    /// The settings changed outside this window (a console command).
    pub(crate) fn settings_changed_elsewhere(
        &mut self,
        settings: &ac_host::settings::PlayerSettings,
    ) {
        self.settings = settings.clone();
        if let Some(ui) = self.ui.as_mut() {
            ui.settings_changed_elsewhere(settings);
        }
    }

    pub(crate) fn set_scene(&mut self, scene: Option<Box<SceneSource>>) {
        self.scene = scene;
        // A window that draws the world presents the login: the session
        // holds login complete for it.
        self.send_session(SessionCommand::PresentsWorld(self.world.is_some()));
    }

    /// Where the UI saves its window layouts.
    pub(crate) fn set_layout_store(&mut self, store: Option<ac_host::settings::SettingsStore>) {
        if let Some(ui) = self.ui.as_mut() {
            ui.set_layout_store(store);
        }
    }

    /// The session feeding this window is gone.
    pub(crate) fn session_gone(&self) -> bool {
        self.scene.as_deref().is_some_and(SceneSource::session_gone)
    }

    /// Why the world cannot be drawn, the first time it is known.
    pub(crate) fn take_world_failure(&mut self) -> Option<String> {
        if self.world_failure_reported {
            return None;
        }
        let reason = self.world.as_ref()?.failure()?.to_owned();
        self.world_failure_reported = true;
        Some(reason)
    }

    pub(crate) fn id(&self) -> WindowId {
        self.window.id()
    }

    pub(crate) fn adapter(&self) -> &AdapterSummary {
        &self.adapter
    }

    /// The loss, when the window's GPU device is known to be lost: the
    /// window then only closes.
    pub(crate) fn device_lost(&self) -> Option<GpuFault> {
        self.health.lost()
    }

    pub(crate) fn request_redraw(&self) {
        self.window.request_redraw();
    }

    /// Zoom the camera: positive moves out.
    pub(crate) fn zoom(&mut self, steps: f32) {
        if let Some(world) = self.world.as_mut() {
            world.zoom(steps);
        }
    }

    /// The window moved or changed scale: it may be on another monitor.
    pub(crate) fn monitor_changed(&mut self) {
        // The UI follows on its next frame (its size comes from the scale).
        self.platform_scale = UiScale::for_window(HostOs::current(), self.window.scale_factor());
        let policy = self
            .pacing
            .set_monitor_refresh(monitor_refresh_hz(&self.window));
        self.apply_present_mode(policy.present_mode());
    }

    /// The player turned vsync on or off.
    pub(crate) fn set_vsync(&mut self, vsync: bool) {
        let policy = self.pacing.set_vsync(vsync);
        self.apply_present_mode(policy.present_mode());
    }

    fn apply_present_mode(&mut self, mode: wgpu::PresentMode) {
        if self.config.present_mode != mode {
            self.config.present_mode = mode;
            self.reconfigure();
        }
    }

    fn drawable(&self) -> bool {
        self.config.width != 0 && self.config.height != 0
    }

    fn reconfigure(&mut self) {
        if self.drawable() && !self.minimized() {
            self.surface.configure(&self.device, &self.config);
        }
    }

    fn minimized(&self) -> bool {
        let size = self.window.inner_size();
        size.width == 0 || size.height == 0
    }

    /// Follows the window's new size: the swapchain and depth buffer, then
    /// the camera's aspect. A zero size (minimized) changes nothing; drawing
    /// pauses until the window has an area again.
    pub(crate) fn resize(&mut self, size: PhysicalSize<u32>) {
        // Plugins read whether the window is minimized (OpenAC
        // `IHostWindow.IsMinimized`); a minimized window resizes to
        // nothing.
        let minimized = self
            .window
            .is_minimized()
            .unwrap_or(size.width == 0 || size.height == 0);
        if minimized != self.reported_minimized {
            self.reported_minimized = minimized;
            self.send_session(SessionCommand::WindowMinimized(minimized));
        }
        // A compositor's answer to a fullscreen request resizes the window.
        // The drawing surface follows the window's size afterwards: a size
        // asked for while following may take at once, with no resize of
        // its own (Wayland).
        let size = self.follow_fullscreen();
        if self.apply_size(size) {
            self.window.request_redraw();
        }
    }

    /// Rebuilds the drawing surface when the window's size is no longer
    /// the surface's: a size asked for can take at once with no resize of
    /// its own (Wayland).
    fn follow_window_size(&mut self) {
        let size = self.window.inner_size();
        if (size.width, size.height) != (self.config.width, self.config.height)
            && self.apply_size(size)
        {
            self.window.request_redraw();
        }
    }

    /// Rebuilds the swapchain and the world's targets at `size`; false
    /// when the size has no area.
    fn apply_size(&mut self, size: PhysicalSize<u32>) -> bool {
        let Some((width, height)) = framebuffer_resize(size.width, size.height) else {
            return false;
        };
        self.config.width = width;
        self.config.height = height;
        self.surface.configure(&self.device, &self.config);
        if let Some(world) = self.world.as_mut() {
            world.resize(&self.device, width, height);
        }
        true
    }

    /// Draw a screenshot after the next frame (every request is answered,
    /// several in one frame each in turn).
    pub(crate) fn request_screenshot(&mut self, path: PathBuf) {
        self.screenshots.request(path);
        self.window.request_redraw();
    }

    /// The screenshots taken since the last call, in the order asked.
    pub(crate) fn take_screenshot_results(
        &mut self,
    ) -> Vec<(PathBuf, Result<Option<WorldStats>, RenderError>)> {
        self.screenshots.take_results()
    }

    /// Take what the session sent since the last frame, and place what
    /// moves between the session's physics steps at the frame's time `now`.
    fn pump_scene(&mut self, now: Instant) {
        let token = self.profiler.begin(FrameStage::Update);
        if let Some(scene) = self.scene.as_mut() {
            scene.pump();
            scene.scene_mut().present_at(now);
        }
        if let Some(icon) = self.world.as_mut().and_then(WorldRenderer::take_icon) {
            set_icon(&self.window, &icon);
        }
        self.profiler.end(token);
    }

    /// Faults wgpu reported since the last frame: a fatal one ends the
    /// window; the others are returned once each for the owner to log.
    fn check_health(&mut self, report: &mut FrameReport) -> Result<(), RenderError> {
        if let Some(lost) = self.health.lost() {
            return Err(RenderError::Gpu(lost));
        }
        for fault in self.health.drain() {
            if gpu::is_fatal(&fault) {
                return Err(RenderError::Gpu(fault));
            }
            let text = fault.to_string();
            // Each distinct fault is reported once; a bounded set keeps a
            // fault that varies every frame from growing it without end.
            if self.reported_faults.len() < 64 && self.reported_faults.insert(text.clone()) {
                report.faults.push(text);
            }
        }
        Ok(())
    }

    /// Draws a frame and presents it. Asks for the next frame unless paused.
    pub(crate) fn render(&mut self) -> Result<(FrameOutcome, FrameReport), RenderError> {
        let mut report = FrameReport::default();
        self.check_health(&mut report)?;
        let now = Instant::now();
        let delta = self
            .last_frame
            .replace(now)
            .map_or(0.0, |t| now.duration_since(t).as_secs_f64());
        if let Some(line) = self.profiler.frame_boundary(now) {
            report.profile = Some(line);
        }
        self.pump_scene(now);
        let input = FrameInput {
            delta_seconds: delta,
            width: if self.minimized() {
                0
            } else {
                self.config.width
            },
            height: if self.minimized() {
                0
            } else {
                self.config.height
            },
        };
        // The phases run with the profiler lent out; what they measure for
        // it (the GPU times of earlier frames) is handed over afterwards.
        self.frame_index = self.profiler.frame_index();
        let mut profiler = std::mem::replace(&mut self.profiler, FrameProfiler::new(false, false));
        let result = render_frame(self, &mut profiler, &input);
        self.profiler = profiler;
        report.revealed_after = self
            .world
            .as_mut()
            .and_then(WorldRenderer::take_revealed_after);
        for (frame, elapsed) in self.gpu_samples.drain(..) {
            self.profiler.record_gpu(frame, elapsed);
        }
        // A device lost during the frame ends the window now, before
        // anything else reaches it.
        self.check_health(&mut report)?;
        let outcome = result?;
        self.retry_at = None;
        match redraw_after(&outcome) {
            Some(Duration::ZERO) => self.window.request_redraw(),
            Some(wait) => self.retry_at = Instant::now().checked_add(wait),
            None => {}
        }
        Ok((outcome, report))
    }

    /// When a surface that gave no image is to be asked again: `None`
    /// when nothing waits, or when the wait is over (the window is then
    /// asked to draw now).
    pub(crate) fn pending_retry(&mut self, now: Instant) -> Option<Instant> {
        match self.retry_at {
            Some(at) if at <= now => {
                self.retry_at = None;
                self.window.request_redraw();
                None
            }
            pending => pending,
        }
    }

    /// Records what the surface gave and reports a change once.
    fn note_surface(&mut self, state: &'static str) {
        if let Some(line) = self.surface_watch.observe(state) {
            self.notices.push(line);
        }
    }

    /// After a presented frame: wait out the rest of the frame period when
    /// software pacing is on.
    pub(crate) fn frame_rendered(&mut self) {
        let waited = self.pacing.frame_rendered();
        self.profiler.add(FrameStage::Pacing, waited);
    }

    /// Show the retail cursor the UI decided on this frame.
    pub(crate) fn apply_cursor(&mut self, event_loop: &ActiveEventLoop) {
        let Some(ui) = self.ui.as_mut() else {
            return;
        };
        let feedback = ui.cursor_feedback();
        let dats = ui.dats().clone();
        let notices = self
            .cursor_art
            .apply(event_loop, &self.window, &dats, feedback);
        self.notices.extend(notices);
    }

    /// The title for the last refresh interval's frame rate and `world`,
    /// the last frame's landblocks.
    fn window_title(&self, fps: f64, mean_ms: f64, world: &WorldOutcome) -> String {
        let scene = self.scene.as_deref().map(SceneSource::scene);
        let subject = TitleSubject::of(scene);
        if subject == TitleSubject::BeforeWorld {
            return crate::title::product();
        }
        let stats = self
            .world
            .as_ref()
            .map(WorldRenderer::stats)
            .unwrap_or_default();
        let placed_server_objects =
            scene.map_or(0, |s| s.objects().filter(|o| o.position.is_some()).count());
        let facts = TitleFacts {
            visible_landblocks: world.visible_landblocks,
            total_landblocks: world.total_landblocks,
            objects: self.world.as_ref().map_or(0, WorldRenderer::loaded_objects)
                + placed_server_objects,
            animated: stats.animated_objects,
            calendar: self
                .world
                .as_ref()
                .and_then(|w| w.calendar_date(scene, Instant::now())),
        };
        format_title(
            subject,
            &TitleStats {
                fps,
                mean_ms,
                facts,
            },
        )
    }

    /// Record the world (or a clear) into `target`.
    /// Whether the world is drawn this frame: not while UI Only (stored, or
    /// in the background with the option on) is in effect.
    fn world_pass_enabled(&self) -> bool {
        !crate::quality::ui_only_for_focus(&self.settings.display, self.input.is_focused())
    }

    fn encode_world(
        &self,
        encoder: &mut wgpu::CommandEncoder,
        target: &wgpu::TextureView,
        timestamps: Option<wgpu::RenderPassTimestampWrites<'_>>,
    ) {
        // A pre-game screen shows: the session has no player, so no world
        // (the last one would show through the credits); black behind.
        let pregame = self.ui.as_ref().is_some_and(WindowUi::pregame_active);
        let clear = if pregame {
            wgpu::Color::BLACK
        } else {
            self.clear_color
        };
        // UI Only: the frame still clears and presents; the world pass is
        // skipped (OpenAC `WorldSceneRenderer.DrawWorld`).
        let world_pass = !pregame && self.world_pass_enabled();
        match self.world.as_ref().filter(|_| world_pass) {
            Some(world) => world.draw(
                encoder,
                target,
                world.clear_color(self.clear_color),
                timestamps,
            ),
            None => {
                let _pass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
                    label: Some("clear"),
                    color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                        view: target,
                        depth_slice: None,
                        resolve_target: None,
                        ops: wgpu::Operations {
                            load: wgpu::LoadOp::Clear(clear),
                            store: wgpu::StoreOp::Store,
                        },
                    })],
                    timestamp_writes: timestamps,
                    ..Default::default()
                });
            }
        }
    }

    /// Draw the prepared frame again into an image the size of the window and write it to
    /// `path` as a PNG.
    fn screenshot(&mut self, path: &Path) -> Result<Option<WorldStats>, RenderError> {
        if let Some(lost) = self.health.lost() {
            return Err(RenderError::Gpu(lost));
        }
        let (width, height) = (self.config.width, self.config.height);
        let format = self.config.format;
        let bgra = match format {
            wgpu::TextureFormat::Bgra8Unorm | wgpu::TextureFormat::Bgra8UnormSrgb => true,
            wgpu::TextureFormat::Rgba8Unorm | wgpu::TextureFormat::Rgba8UnormSrgb => false,
            other => {
                return Err(RenderError::Screenshot(format!(
                    "frame format {other:?} cannot be saved"
                )));
            }
        };
        let texture = self.device.create_texture(&wgpu::TextureDescriptor {
            label: Some("screenshot"),
            size: wgpu::Extent3d {
                width,
                height,
                depth_or_array_layers: 1,
            },
            mip_level_count: 1,
            sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            format,
            usage: wgpu::TextureUsages::RENDER_ATTACHMENT | wgpu::TextureUsages::COPY_SRC,
            view_formats: &[],
        });
        let view = texture.create_view(&wgpu::TextureViewDescriptor::default());
        let mut encoder = self
            .device
            .create_command_encoder(&wgpu::CommandEncoderDescriptor {
                label: Some("screenshot"),
            });
        self.encode_world(&mut encoder, &view, None);
        if let Some(ui) = self.ui_renderer.as_ref() {
            ui.draw(&mut encoder, &view);
        }

        // Rows of a buffer copy are padded to 256 bytes.
        let row = width * 4;
        let padded_row =
            row.div_ceil(wgpu::COPY_BYTES_PER_ROW_ALIGNMENT) * wgpu::COPY_BYTES_PER_ROW_ALIGNMENT;
        let buffer = self.device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("screenshot readback"),
            size: u64::from(padded_row) * u64::from(height),
            usage: wgpu::BufferUsages::COPY_DST | wgpu::BufferUsages::MAP_READ,
            mapped_at_creation: false,
        });
        encoder.copy_texture_to_buffer(
            wgpu::TexelCopyTextureInfo {
                texture: &texture,
                mip_level: 0,
                origin: wgpu::Origin3d::ZERO,
                aspect: wgpu::TextureAspect::All,
            },
            wgpu::TexelCopyBufferInfo {
                buffer: &buffer,
                layout: wgpu::TexelCopyBufferLayout {
                    offset: 0,
                    bytes_per_row: Some(padded_row),
                    rows_per_image: Some(height),
                },
            },
            wgpu::Extent3d {
                width,
                height,
                depth_or_array_layers: 1,
            },
        );
        self.queue.submit([encoder.finish()]);

        let (tx, rx) = std::sync::mpsc::channel();
        buffer.map_async(wgpu::MapMode::Read, .., move |result| {
            let _ = tx.send(result);
        });
        self.health
            .poll(&self.device, wgpu::PollType::wait_indefinitely())
            .map_err(|fault| match fault {
                GpuFault::DeviceLost(_) => RenderError::Gpu(fault),
                other => RenderError::Screenshot(other.to_string()),
            })?;
        rx.recv()
            .map_err(|_| RenderError::Screenshot("the readback was dropped".into()))?
            .map_err(|e| RenderError::Screenshot(format!("mapping the readback: {e}")))?;

        let mut rgba = Vec::with_capacity((row * height) as usize);
        {
            let mapped = buffer
                .slice(..)
                .get_mapped_range()
                .map_err(|e| RenderError::Screenshot(format!("reading the readback: {e}")))?;
            for line in mapped.chunks(padded_row as usize).take(height as usize) {
                for px in line[..row as usize].chunks_exact(4) {
                    if bgra {
                        rgba.extend_from_slice(&[px[2], px[1], px[0], 255]);
                    } else {
                        rgba.extend_from_slice(&[px[0], px[1], px[2], 255]);
                    }
                }
            }
        }
        buffer.unmap();
        if let Some(parent) = path.parent().filter(|p| !p.as_os_str().is_empty()) {
            std::fs::create_dir_all(parent).map_err(|e| {
                RenderError::Screenshot(format!("cannot create {}: {e}", parent.display()))
            })?;
        }
        std::fs::write(path, ac_bits::png::encode_rgba(width, height, &rgba)).map_err(|e| {
            RenderError::Screenshot(format!("cannot write {}: {e}", path.display()))
        })?;
        Ok(self.world.as_ref().map(WorldRenderer::stats))
    }
}

impl FramePhases for AttachedWindow {
    type Error = RenderError;

    fn begin_frame(&mut self) -> Result<bool, RenderError> {
        let (texture, reconfigure_after) = match self.surface.get_current_texture() {
            wgpu::CurrentSurfaceTexture::Success(texture) => (texture, false),
            wgpu::CurrentSurfaceTexture::Suboptimal(texture) => (texture, true),
            // The swapchain no longer matches the window (a resize the event has not reported
            // yet): rebuild it at the window's current size and draw next frame.
            // A swapchain that stays outdated (the window mid-change) waits
            // like any surface without an image instead of asking at once.
            wgpu::CurrentSurfaceTexture::Outdated => {
                let again = self.surface_watch.last() == Some(SURFACE_OUTDATED);
                self.note_surface(SURFACE_OUTDATED);
                if self.apply_size(self.window.inner_size()) && !again {
                    self.window.request_redraw();
                }
                return Ok(false);
            }
            // No image this time; the window asks again after a wait
            // (`render`), never at once: a covered window or a display
            // being switched can refuse for a long time.
            wgpu::CurrentSurfaceTexture::Timeout => {
                self.note_surface(SURFACE_TIMED_OUT);
                return Ok(false);
            }
            wgpu::CurrentSurfaceTexture::Occluded => {
                self.note_surface(SURFACE_OCCLUDED);
                return Ok(false);
            }
            wgpu::CurrentSurfaceTexture::Lost => {
                self.note_surface(SURFACE_LOST);
                return Err(RenderError::Gpu(GpuFault::SurfaceLost));
            }
            wgpu::CurrentSurfaceTexture::Validation => {
                self.note_surface(SURFACE_INVALID);
                return Err(RenderError::SurfaceValidation);
            }
        };
        self.note_surface(SURFACE_DRAWING);
        let view = texture
            .texture
            .create_view(&wgpu::TextureViewDescriptor::default());
        self.frame = Some(OpenFrame {
            texture,
            view,
            encoder: None,
            reconfigure_after,
        });
        Ok(true)
    }

    fn begin_measure(&mut self) -> Result<(), RenderError> {
        if let Some(timer) = self.gpu_timer.as_mut() {
            // Reads finished measurements of earlier frames without waiting.
            // A lost device ends the frame; anything else only delays them.
            if let Err(lost @ GpuFault::DeviceLost(_)) =
                self.health.poll(&self.device, wgpu::PollType::Poll)
            {
                return Err(RenderError::Gpu(lost));
            }
            for (frame, elapsed) in timer.collect() {
                self.gpu_samples.push((frame, elapsed));
                // The atmospheric rendering's automatic quality judges the
                // world's GPU time.
                if let Some(world) = self.world.as_mut() {
                    world.observe_frame_gpu(&self.device, elapsed);
                }
            }
        }
        let encoder = self
            .device
            .create_command_encoder(&wgpu::CommandEncoderDescriptor {
                label: Some("frame"),
            });
        if let Some(frame) = self.frame.as_mut() {
            frame.encoder = Some(encoder);
        }
        Ok(())
    }

    fn prepare(&mut self, input: &FrameInput) -> Result<(), RenderError> {
        let fly = self.tick_gameplay();
        self.tick_automation(input.delta_seconds);
        self.present_effects();
        let motions = self
            .scene
            .as_mut()
            .map(|source| source.scene_mut().take_motions())
            .unwrap_or_default();
        if let Some(world) = self.world.as_mut() {
            world.queue_motions(motions);
            world.prepare(
                &self.device,
                &self.queue,
                self.scene.as_deref().map(SceneSource::scene),
                fly,
            );
            self.notices.extend(world.take_notices());
        }
        self.present_portal_cues();
        self.tick_audio();
        self.prepare_ui();
        self.flush_plugin_ui();
        self.follow_keyboard();
        Ok(())
    }

    fn render_world(&mut self, _input: &FrameInput) -> Result<WorldOutcome, RenderError> {
        let Some(mut frame) = self.frame.take() else {
            return Ok(WorldOutcome::default());
        };
        let Some(mut encoder) = frame.encoder.take() else {
            self.frame = Some(frame);
            return Ok(WorldOutcome::default());
        };
        let frame_index = self.frame_index;
        let mut timer = self.gpu_timer.take();
        let timestamps = timer.as_mut().and_then(|t| t.begin(frame_index));
        self.encode_world(&mut encoder, &frame.view, timestamps);
        // The UI's 3-D viewports, into their textures before the UI draws.
        if let Some(world) = self.world.as_ref() {
            world.draw_viewports(&mut encoder);
        }
        self.gpu_timer = timer;
        frame.encoder = Some(encoder);
        self.frame = Some(frame);
        let stats = self
            .world
            .as_ref()
            .map(WorldRenderer::stats)
            .unwrap_or_default();
        self.last_world = WorldOutcome {
            visible_landblocks: stats.landblocks_drawn,
            total_landblocks: stats.landblocks,
            normal_world_drawn: self.world_pass_enabled()
                && self
                    .world
                    .as_ref()
                    .is_some_and(|w| !w.is_withholding() && !w.portal_hides_world()),
        };
        Ok(self.last_world)
    }

    fn render_presentation(
        &mut self,
        _input: &FrameInput,
        _world: &WorldOutcome,
    ) -> Result<(), RenderError> {
        let Some(frame) = self.frame.as_mut() else {
            return Ok(());
        };
        let Some(encoder) = frame.encoder.as_mut() else {
            return Ok(());
        };
        if let Some(renderer) = self.ui_renderer.as_ref() {
            renderer.draw(encoder, &frame.view);
        }
        // The brightness ramp acts on the whole picture, and only in
        // exclusive fullscreen (a window's ramp has no effect).
        if matches!(
            self.window.fullscreen(),
            Some(winit::window::Fullscreen::Exclusive(_))
        ) {
            let pass = self.brightness.get_or_insert_with(|| {
                crate::gamma::BrightnessPass::new(&self.device, self.config.format)
            });
            pass.draw(
                &self.device,
                &self.queue,
                encoder,
                &frame.view,
                self.settings.display.screen_brightness,
            );
        }
        Ok(())
    }

    fn end_measure(&mut self) -> Result<(), RenderError> {
        if let (Some(timer), Some(encoder)) = (
            self.gpu_timer.as_mut(),
            self.frame.as_mut().and_then(|f| f.encoder.as_mut()),
        ) {
            timer.resolve(encoder);
        }
        Ok(())
    }

    fn end_frame(&mut self) -> Result<(), RenderError> {
        let Some(mut frame) = self.frame.take() else {
            return Ok(());
        };
        if let Some(encoder) = frame.encoder.take() {
            self.queue.submit([encoder.finish()]);
        }
        if let Some(timer) = self.gpu_timer.as_mut() {
            timer.submitted();
        }
        if let Some(world) = self.world.as_mut() {
            world.icon_probes_submitted();
        }
        self.window.pre_present_notify();
        self.queue.present(frame.texture);
        self.frames_presented += 1;
        if frame.reconfigure_after {
            self.resize(self.window.inner_size());
        }
        Ok(())
    }

    fn abort_frame(&mut self) {
        // Dropping the image without presenting discards it.
        self.frame = None;
        if let Some(timer) = self.gpu_timer.as_mut() {
            timer.abandon();
        }
    }

    fn capture_pending_screenshot(&mut self, _input: &FrameInput) -> Result<bool, RenderError> {
        let Some(path) = self.screenshots.next() else {
            return Ok(false);
        };
        let result = self.screenshot(&path);
        let taken = result.is_ok();
        if let Some(i) = self.key_screenshots.iter().position(|p| *p == path) {
            self.key_screenshots.remove(i);
            let line = match &result {
                Ok(_) => format!("Screenshot saved to {}", path.display()),
                Err(error) => format!("Screenshot failed: {error}"),
            };
            self.client_line(&line);
        }
        self.screenshots.answer(path, result);
        if self.screenshots.is_waiting() {
            // The next one is taken after the next frame.
            self.window.request_redraw();
        }
        Ok(taken)
    }

    fn publish(&mut self, input: &FrameInput, outcome: &FrameOutcome) -> Result<(), RenderError> {
        self.after_automation_frame(outcome);
        if let Some((fps, mean_ms)) = self.title_rate.frame(input.delta_seconds) {
            let title = self.window_title(fps, mean_ms, &outcome.world);
            if title != self.title {
                self.window.set_title(&title);
                self.title = title;
            }
            self.publish_facts();
        }
        Ok(())
    }
}

impl AttachedWindow {
    /// Present what the session asked for since the last frame: the
    /// server's sounds and physics-script plays (to the effects) and its
    /// environment changes (a fog override, or a sound cue).
    fn present_effects(&mut self) {
        let Some(source) = self.scene.as_mut() else {
            return;
        };
        let effects = source.scene_mut().take_effects();
        for effect in effects {
            match effect {
                // Sounds and script plays queue with the effects, which
                // hold them until their object is ready.
                SceneEffect::Sound(sound) => {
                    if let Some(world) = self.world.as_mut() {
                        world.queue_server_effect(ServerEffect::Sound {
                            guid: sound.guid,
                            sound_type: sound.sound_type,
                            volume: sound.volume,
                        });
                    }
                }
                SceneEffect::PlayScript(play) => {
                    if let Some(world) = self.world.as_mut() {
                        world.queue_server_effect(ServerEffect::PlayScript {
                            guid: play.guid,
                            script_id: play.script_id,
                        });
                    }
                }
                SceneEffect::DestroyParticles(guid) => {
                    if let Some(world) = self.world.as_mut() {
                        world.queue_server_effect(ServerEffect::DestroyParticles { guid });
                    }
                }
                SceneEffect::PlayScriptType(play) => {
                    if let Some(world) = self.world.as_mut() {
                        world.queue_server_effect(ServerEffect::PlayScriptType {
                            guid: play.guid,
                            script_type: play.script_type,
                            intensity: play.intensity,
                        });
                    }
                }
                SceneEffect::AdminEnvirons(change) => {
                    let effect = self
                        .world
                        .as_mut()
                        .map(|world| world.admin_environs(change));
                    match effect {
                        Some(EnvironmentEffect::SoundCue(_)) => {
                            if let Some(audio) = self.audio.as_mut() {
                                audio.play_environ_cue(change);
                            }
                        }
                        Some(EnvironmentEffect::FogOverride(fog)) => {
                            self.notices.push(format!("environment: {}", fog.name()));
                        }
                        _ => {}
                    }
                }
                SceneEffect::PortalSpace(event) => {
                    if let Some(world) = self.world.as_mut() {
                        world.portal_space_event(event);
                        // The world goes quiet the moment the portal starts.
                        self.follow_world_hold();
                    } else {
                        // A window without a world has nothing to load or
                        // show: what the session holds goes at once.
                        match event {
                            PortalSpaceEvent::Destination {
                                generation,
                                teleport_sequence,
                                destination_cell,
                            } => {
                                self.send_session(SessionCommand::DestinationShown(
                                    DestinationShown {
                                        teleport_sequence,
                                        readiness: DestinationReadiness::immediate(
                                            generation,
                                            destination_cell,
                                        ),
                                    },
                                ));
                            }
                            PortalSpaceEvent::LoginDestination {
                                generation,
                                destination_cell,
                            } => {
                                self.send_session(SessionCommand::LoginShown(
                                    DestinationReadiness::immediate(generation, destination_cell),
                                ));
                            }
                            _ => {}
                        }
                    }
                }
                SceneEffect::WorldReplaced => {
                    // A new world from the session (an attach, entering or
                    // leaving the world): portal space under way ends, and
                    // the next login is withheld until its area has loaded.
                    if let Some(world) = self.world.as_mut() {
                        world.portal_space_reset();
                        world.restart_login_reveal();
                    }
                    // World sounds only while a character is in the world:
                    // none at character selection or after a log off.
                    let in_world = self
                        .scene
                        .as_deref()
                        .is_some_and(|source| source.scene().player_guid().is_some());
                    if let Some(audio) = self.audio.as_mut() {
                        audio.set_in_world(in_world);
                    }
                    self.follow_world_hold();
                }
            }
        }
    }

    /// World sounds are quiet while a login or portal reveal holds the
    /// world back, from its start until the destination is shown.
    ///
    /// When a hold begins, a selected object the window's world shows
    /// loses the selection (OpenAC `WorldGenerationQuiescence.CaptureBegin`
    /// and `CommitBegin`, once per hold).
    fn follow_world_hold(&mut self) {
        let Some(world) = self.world.as_ref() else {
            return;
        };
        let held = world.holds_world();
        if held && !self.world_held {
            let selected = self
                .scene
                .as_deref()
                .and_then(|source| source.scene().player_view())
                .and_then(|view| view.selected);
            if let Some(guid) = revealed_selection(selected, |guid| world.is_live_resident(guid)) {
                self.send_session(SessionCommand::Gameplay(
                    ac_runtime::gameplay::GameplayCommand::ClearRevealedSelection(guid),
                ));
            }
        }
        self.world_held = held;
        if let Some(audio) = self.audio.as_mut() {
            audio.set_world_held(held);
        }
    }

    /// Portal space's sounds (the enter and exit cues, the tunnel's own,
    /// played centred) and its wait line (to the session, which shows the
    /// client's interface text).
    fn present_portal_cues(&mut self) {
        let Some(world) = self.world.as_mut() else {
            return;
        };
        for cue in world.take_portal_cues() {
            match cue {
                PortalCue::EnterSound => {
                    if let Some(audio) = self.audio.as_mut() {
                        audio.play_ui(ac_audio::SoundId::UiEnterPortal);
                    }
                }
                PortalCue::ExitSound => {
                    if let Some(audio) = self.audio.as_mut() {
                        audio.play_ui(ac_audio::SoundId::UiExitPortal);
                    }
                }
                PortalCue::TunnelHook(hook) => {
                    if let Some(audio) = self.audio.as_mut() {
                        audio.play_ui_hook(&hook.data);
                    }
                }
                PortalCue::WaitText => self.send_session(SessionCommand::ShowInterfaceText(
                    crate::portal_space::tunnel::WAIT_TEXT.to_owned(),
                )),
                // The world turned it into a notice with what it waits for.
                PortalCue::DestinationStalled { .. } => {}
                PortalCue::DestinationShown(shown) => {
                    self.portal_materializations += 1;
                    self.send_session(SessionCommand::DestinationShown(shown));
                }
                PortalCue::LoginShown(readiness) => {
                    self.send_session(SessionCommand::LoginShown(readiness));
                }
                PortalCue::RevealWaited { generation, waited } => {
                    self.send_session(SessionCommand::RevealWaited { generation, waited });
                }
            }
        }
    }

    /// Feed the sound: the region's ambient once read, the scene's sound
    /// tables, the listener (the camera) and the player's place.
    fn tick_audio(&mut self) {
        // Before this frame's sounds: a reveal begun or ended in the
        // world's update (the login reveal, the destination shown).
        self.follow_world_hold();
        let (Some(audio), Some(world)) = (self.audio.as_mut(), self.world.as_mut()) else {
            return;
        };
        if let Some(region) = world.take_ambient_region() {
            audio.install_region(region);
        }
        let scene = self.scene.as_deref().map(SceneSource::scene);
        if let Some(scene) = scene {
            audio.sync_sound_tables(scene.objects().map(|o| (o.guid, o.sound_table_id)));
        }
        for fired in world.take_animation_sounds() {
            audio.play_hook(fired.guid, fired.position, &fired.hook.data);
        }
        for sound in world.take_effect_sounds() {
            match sound {
                EffectSound::Hook {
                    guid,
                    position,
                    hook,
                } => audio.play_hook(guid, position, &hook),
                EffectSound::Server {
                    guid,
                    position,
                    sound_type,
                    volume,
                } => audio.play_server_sound(guid, position, sound_type, volume),
            }
        }
        let player = scene
            .and_then(|s| s.player())
            .and_then(|p| p.position)
            .and_then(|p| {
                let local = Vec3::new(p.origin.x, p.origin.y, p.origin.z);
                Some(PlayerPlace {
                    cell_id: p.cell_id,
                    position: world.frame_position(p.cell_id, local)?,
                    local,
                    seen_outside: p.cell_id & 0xFFFF >= 0x0100
                        && world.player_cell_seen_outside(p.cell_id),
                })
            });
        audio.tick(world.camera(), player, Instant::now());
    }

    /// Ctrl+M (or the console's `/mute`): silence the window's sound or
    /// sound again.
    pub(crate) fn toggle_mute(&mut self) {
        let notice = match self.audio.as_mut().map(WindowAudio::toggle_mute) {
            Some(true) => "audio: muted",
            Some(false) => "audio: unmuted",
            None => "audio: this window has no sound",
        };
        self.notices.push(notice.to_owned());
    }

    /// F7 (or `/time`): step the time of day through midnight, dawn, noon,
    /// dusk and back to the clock.
    pub(crate) fn cycle_time_of_day(&mut self) {
        if let Some(env) = self.world.as_mut().and_then(WorldRenderer::environment_mut) {
            let notice = env.cycle_time_of_day();
            self.notices.push(notice);
        }
    }

    /// F10 (or `/weather`): step the weather.
    pub(crate) fn cycle_weather(&mut self) {
        if let Some(env) = self.world.as_mut().and_then(WorldRenderer::environment_mut) {
            let notice = env.cycle_weather();
            self.notices.push(notice);
        }
    }

    /// Apply the display settings' fullscreen and resolution to the window
    /// (a fullscreen mode switch, leaving it, or a windowed resize).
    fn apply_window_mode(&mut self, display: &ac_host::settings::DisplaySettings) {
        let offered = fullscreen_modes_of(&self.window);
        let mut lines = Vec::new();
        crate::display_mode::apply_display(
            display,
            &mut crate::display_mode::WinitDisplay(&self.window),
            &mut crate::display_mode::WinitDisplay(&self.window),
            &mut self.fullscreen_awaiting,
            |m| offered.iter().any(|o| o == m),
            &mut lines,
        );
        self.screen_saver.follow(&self.window, &mut lines);
        self.notices.extend(lines);
        self.follow_window_size();
    }

    /// Follow a resize of the window: finish what a request to the
    /// compositor still owes, and keep the screen awake while fullscreen.
    /// Returns the window's size afterwards.
    fn follow_fullscreen(&mut self) -> PhysicalSize<u32> {
        let mut lines = Vec::new();
        // The window's size in its own unit (points on macOS); the
        // drawing surface reads it again in pixels below.
        let _ = crate::display_mode::follow_resize(
            &mut self.fullscreen_awaiting,
            &mut crate::display_mode::WinitDisplay(&self.window),
            &mut crate::display_mode::WinitDisplay(&self.window),
            &mut lines,
        );
        self.screen_saver.follow(&self.window, &mut lines);
        self.notices.extend(lines);
        self.window.inner_size()
    }

    /// The event loop is ending: let go of the screen while the window's
    /// display connection is still open.
    pub(crate) fn release_screen(&mut self) {
        self.screen_saver.release();
    }

    /// Ctrl+F3 (or `/dumpnearby`): log what stands near the player.
    pub(crate) fn dump_nearby(&mut self) {
        if let Some(world) = self.world.as_ref() {
            let scene = self.scene.as_deref().map(SceneSource::scene);
            let lines = world.nearby_dump(scene);
            self.notices.extend(lines);
        }
    }

    /// Messages for the owner since the last call.
    pub(crate) fn take_notices(&mut self) -> Vec<String> {
        std::mem::take(&mut self.notices)
    }

    /// Refresh what a crash report would say about this window.
    fn publish_facts(&mut self) {
        let player = self
            .scene
            .as_ref()
            .and_then(|s| s.scene().player())
            .and_then(|p| p.position);
        let camera = self
            .camera_mode()
            .map_or_else(String::new, |m| format!("{m:?}"));
        self.facts.set(Some(PresentationFacts {
            device_name: self.adapter.name.clone(),
            backend: format!("{:?}", self.adapter.backend),
            driver: self.adapter.driver.clone(),
            width: self.config.width,
            height: self.config.height,
            cell_id: player.map(|p| p.cell_id),
            position: player.map(|p| [p.origin.x, p.origin.y, p.origin.z]),
            camera,
        }));
    }
}

impl AttachedWindow {
    /// Close the window in order: stop taking input (and give the cursor
    /// back), discard an open frame, let the GPU finish what was submitted,
    /// write the frame history, release the renderer (its loader thread is
    /// joined), then the surface, device and native window. A stage that
    /// fails is retried once; if it still fails the rest is released
    /// anyway, since a window being closed cannot be kept. Returns the
    /// frames presented and any stage failure, for the report.
    pub(crate) fn close(self) -> (u64, Option<String>) {
        let frames = self.frames_presented;
        let window = std::cell::RefCell::new(Some(self));
        let with = |f: &mut dyn FnMut(&mut Self) -> Result<(), String>| -> Result<(), String> {
            window.borrow_mut().as_mut().map_or(Ok(()), f)
        };
        let mut teardown = OrderedTeardown::new(vec![
            Box::new(|| {
                with(&mut |w| {
                    if w.input.quiesce() {
                        w.release_cursor();
                    }
                    w.scene = None;
                    w.facts.set(None);
                    Ok(())
                })
            }),
            Box::new(|| {
                with(&mut |w| {
                    w.abort_frame();
                    Ok(())
                })
            }),
            Box::new(|| {
                with(&mut |w| {
                    // A lost device has nothing left to finish and is not
                    // waited on (the loss was reported when it closed the
                    // window).
                    match w
                        .health
                        .poll(&w.device, wgpu::PollType::wait_indefinitely())
                    {
                        Ok(_) | Err(GpuFault::DeviceLost(_)) => Ok(()),
                        Err(fault) => Err(fault.to_string()),
                    }
                })
            }),
            Box::new(|| {
                with(&mut |w| {
                    w.write_frame_history();
                    Ok(())
                })
            }),
            Box::new(|| {
                with(&mut |w| {
                    w.gpu_timer = None;
                    w.ui = None;
                    w.ui_renderer = None;
                    w.world = None;
                    w.audio = None;
                    Ok(())
                })
            }),
            Box::new(|| {
                let mut window = window.borrow_mut();
                if let Some(w) = window.as_ref() {
                    w.health.releasing();
                }
                drop(window.take());
                Ok(())
            }),
        ]);
        let failure = teardown.advance().or_else(|_| teardown.advance()).err();
        drop(teardown);
        // Whatever a failed stage left behind goes now.
        drop(window.into_inner());
        (frames, failure)
    }

    fn write_frame_history(&mut self) {
        if let (Some(path), Some(csv)) = (&self.frame_history, self.profiler.history_csv()) {
            match std::fs::write(path, csv) {
                Ok(()) => println!("[frame-prof] wrote frame history to {}", path.display()),
                Err(error) => eprintln!(
                    "[frame-prof] cannot write frame history to {}: {error}",
                    path.display()
                ),
            }
        }
    }

    /// Send the session a movement command from the window's keys; a window
    /// without a session (or whose session ended) has nobody to move.
    fn send_session(&self, command: SessionCommand) {
        if let Some(scene) = &self.scene {
            let _ = scene.send(command);
        }
    }

    /// What the gameplay dispatcher reads of the devices now: the buttons
    /// and modifiers held, and whether a UI field has the keyboard (the UI
    /// takes the pointer itself before the world sees it).
    fn device_state(&self) -> DeviceState<'static> {
        let mut buttons = 0u32;
        for button in 0..8u16 {
            if self.input.is_button_held(button) {
                buttons |= 1 << button;
            }
        }
        DeviceState {
            keys: None,
            buttons,
            modifiers: crate::ui::keys::ui_modifiers(self.modifiers),
            want_mouse: false,
            want_keyboard: self
                .ui
                .as_ref()
                .is_some_and(|ui| ui.host().root.keyboard_focus().is_some()),
        }
    }

    /// Seconds on the gameplay input's clock.
    fn gameplay_now(&self) -> f32 {
        self.input_clock.elapsed().as_secs_f32()
    }

    /// One frame of gameplay input: holds, mouse look, held camera keys
    /// and the held movement; returns the fly camera's keys.
    fn tick_gameplay(&mut self) -> FlyInput {
        let now_instant = Instant::now();
        let dt = self
            .gameplay_tick
            .replace(now_instant)
            .map_or(0.0, |t| now_instant.duration_since(t).as_secs_f32());
        let state = self.device_state();
        let now = self.gameplay_now();
        let mut out = Vec::new();
        let cameras = self.world.as_mut().map(WorldRenderer::cameras_mut);
        let fly = self.gameplay.tick(dt, now, &state, cameras, &mut out);
        self.carry_out(out);
        fly
    }

    /// Carry out what the gameplay input asked for.
    fn carry_out(&mut self, outputs: Vec<GameplayOutput>) {
        let fired = self.gameplay.take_fired();
        if !fired.is_empty()
            && let Some(ui) = self.ui.as_mut()
        {
            ui.plugin_input_actions(&fired);
        }
        for output in outputs {
            match output {
                GameplayOutput::Movement(command) => {
                    self.send_session(SessionCommand::Movement(command));
                }
                GameplayOutput::CycleTimeOfDay => self.cycle_time_of_day(),
                GameplayOutput::CycleWeather => self.cycle_weather(),
                GameplayOutput::ToggleMute => self.toggle_mute(),
                GameplayOutput::GrabCursor => self.grab_cursor(),
                GameplayOutput::ReleaseCursor => {
                    if !self.input.mouse_look() {
                        self.release_cursor();
                    }
                }
                GameplayOutput::Notice(text) => self.notices.push(text),
                GameplayOutput::ToggleFly => self.toggle_fly(),
                GameplayOutput::TogglePlayerMode => {
                    if let Some(world) = self.world.as_mut() {
                        world.cameras_mut().toggle_player_mode();
                    }
                    self.follow_camera_mode();
                }
                GameplayOutput::Escape => {
                    if self.input.mouse_look() {
                        self.end_mouse_look();
                    }
                    // The session decides what Escape stops, cancels or
                    // opens (a jump, a moving body, a target mode, the
                    // selection, else the options' gameplay page).
                    self.send_session(SessionCommand::Gameplay(
                        ac_runtime::gameplay::GameplayCommand::Escape,
                    ));
                }
                GameplayOutput::Gameplay(command) => {
                    self.send_session(SessionCommand::Gameplay(command));
                }
                GameplayOutput::Item(command) => {
                    let command = self.with_split_amount(command);
                    self.send_session(SessionCommand::Item(command));
                }
                GameplayOutput::ChatEntry(action, key) => {
                    let outputs = match self.ui.as_mut() {
                        Some(ui) => {
                            ui.chat_entry_key(action, key);
                            ui.host_mut().take_outputs()
                        }
                        None => continue,
                    };
                    let _ = self.after_ui(Routed::<()> {
                        world: None,
                        outputs,
                    });
                }
                GameplayOutput::LogOut => self.send_session(SessionCommand::Lifecycle(
                    ac_runtime::session::LifecycleCommand::EndCharacterSession,
                )),
                // OpenAC `HandleCharacterOptionAction`: the option's value now,
                // flipped, set as the options panel sets it.
                GameplayOutput::ToggleCharacterOption(option) => {
                    let current = self
                        .scene
                        .as_ref()
                        .and_then(|s| s.scene().player_view())
                        .is_some_and(|v| v.option(option));
                    self.send_session(SessionCommand::SetCharacterOption {
                        option,
                        value: !current,
                    });
                }
                GameplayOutput::CaptureScreenshot => self.capture_key_screenshot(),
                GameplayOutput::Captured(chord) => self.keyboard_captured(chord),
                GameplayOutput::DumpNearby => self.dump_nearby(),
                GameplayOutput::WorldClick(click) => self.world_click(click),
                GameplayOutput::Interface(action) => {
                    let (taken, outputs) = match self.ui.as_mut() {
                        Some(ui) => {
                            let taken = ui.handle_input_action(action);
                            (taken, ui.host_mut().take_outputs())
                        }
                        None => (false, Vec::new()),
                    };
                    let _ = self.after_ui(Routed::<()> {
                        world: None,
                        outputs,
                    });
                    // What the interface did not take goes on to the
                    // session (the examine key with no examination shown).
                    if !taken && let Some(next) = crate::gameplay_input::session_output(action) {
                        self.carry_out(vec![next]);
                    }
                }
            }
        }
    }

    /// The give and drop keys move the split chooser's quantity of the
    /// selected stack, as a drag of it would (OpenAC `PlaceIn3D` reads the
    /// split quantity); any other item request is as it came.
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

    /// The screenshot key: the next frame goes to the first free
    /// `ScreenShotNNNNN.png` in the screenshots folder; the chat says where
    /// (or why not) once it is written.
    fn capture_key_screenshot(&mut self) {
        let Some(directory) = self.screenshots_directory.clone() else {
            self.notices
                .push("screenshot: no screenshots folder (no settings file)".into());
            return;
        };
        let asked = &self.key_screenshots;
        match retail_screenshot_path(&directory, |p| p.exists() || asked.iter().any(|a| a == p)) {
            Some(path) => {
                if let Err(error) = std::fs::create_dir_all(&directory) {
                    self.client_line(&format!("Screenshot failed: {error}"));
                    return;
                }
                self.key_screenshots.push(path.clone());
                self.request_screenshot(path);
            }
            None => self.client_line(
                "Screenshot failed: all retail screenshot names ScreenShot00000 through ScreenShot99999 are in use",
            ),
        }
    }

    /// A client-local chat line (the window's notice log without a UI).
    fn client_line(&mut self, text: &str) {
        match self.ui.as_mut() {
            Some(ui) => ui.client_line(text),
            None => self.notices.push(text.to_owned()),
        }
    }

    fn camera_mode(&mut self) -> Option<CameraMode> {
        Some(self.world.as_mut()?.cameras_mut().mode())
    }

    /// A key went down or up: the UI first (the chat entry, its keys), the
    /// world with what the UI leaves ([`crate::ui::input`]).
    pub(crate) fn keyboard(&mut self, event: &KeyEvent) {
        let PhysicalKey::Code(code) = event.physical_key else {
            return;
        };
        let pressed = event.state == ElementState::Pressed;
        let modifiers = crate::ui::keys::ui_modifiers(self.modifiers);
        // A key capture (the key configuration screen) takes the key
        // before the interface does.
        let capturing = self.gameplay.is_capturing();
        // Plugin hotkeys hear a fresh press before the interface takes it
        // (OpenAC listens to the keyboard itself; its gates keep the chat
        // entry, dialogs and a key capture first).
        if pressed
            && !event.repeat
            && let Some(ui) = self.ui.as_mut()
        {
            ui.plugin_hotkeys(crate::ui::keys::ui_key(code), modifiers, capturing);
        }
        let routed = match self.ui.as_mut() {
            Some(ui) if self.input.is_accepting() && !capturing => Some(ui.key(
                code,
                crate::ui::keys::ui_key(code),
                pressed,
                event.repeat,
                modifiers,
                event.text.as_deref(),
            )),
            _ => None,
        };
        let world = match routed {
            Some(routed) => self.after_ui(routed),
            None if pressed => Some(WorldInput::KeyDown {
                key: code,
                repeat: event.repeat,
            }),
            None => Some(WorldInput::KeyUp(code)),
        };
        match world {
            Some(WorldInput::KeyDown { key, .. }) => self.world_key(key, true),
            Some(WorldInput::KeyUp(key)) => self.world_key(key, false),
            _ => {}
        }
    }

    /// The modifier keys changed.
    pub(crate) fn modifiers_changed(&mut self, modifiers: ModifiersState) {
        self.modifiers = modifiers;
        if let Some(ui) = self.ui.as_mut() {
            ui.host_mut()
                .set_modifiers(crate::ui::keys::ui_modifiers(modifiers));
        }
    }

    /// The world's keys, after the UI: presses only when the UI let them
    /// through, releases always; they go to the gameplay dispatcher (the
    /// key map), whose actions move the character and the camera
    /// ([`crate::gameplay_input`]). A held key's auto-repeat is not a
    /// press.
    fn world_key(&mut self, code: KeyCode, pressed: bool) {
        let new_press = self.input.key(code, pressed);
        if pressed && !new_press {
            return;
        }
        let key = crate::ui::keys::ui_key(code);
        if key == ac_ui::Key::Unknown {
            return;
        }
        if let Some(ui) = self.ui.as_ref() {
            self.gameplay.set_combat_mode(ui.combat_mode());
        }
        let state = self.device_state();
        let now = self.gameplay_now();
        let mut out = Vec::new();
        let cameras = self.world.as_mut().map(WorldRenderer::cameras_mut);
        self.gameplay
            .key(key, pressed, &state, cameras, now, &mut out);
        self.carry_out(out);
    }

    /// Switch the fly camera on or off (the console's `/fly`).
    pub(crate) fn toggle_fly(&mut self) {
        if let Some(world) = self.world.as_mut() {
            world.cameras_mut().toggle_fly();
        }
        self.follow_camera_mode();
    }

    /// Mouse look is on exactly while the fly camera is.
    fn follow_camera_mode(&mut self) {
        if self.camera_mode() == Some(CameraMode::Fly) {
            if self.input.set_mouse_look(true) {
                self.grab_cursor();
            }
        } else {
            self.end_mouse_look();
        }
    }

    fn end_mouse_look(&mut self) {
        if self.input.mouse_look() {
            self.input.set_mouse_look(false);
        }
        self.release_cursor();
    }

    fn grab_cursor(&self) {
        // Locked keeps the cursor still; where that is not offered,
        // confined keeps it in the window.
        let grabbed = self
            .window
            .set_cursor_grab(CursorGrabMode::Locked)
            .or_else(|_| self.window.set_cursor_grab(CursorGrabMode::Confined));
        if grabbed.is_ok() {
            self.window.set_cursor_visible(false);
        }
    }

    fn release_cursor(&self) {
        let _ = self.window.set_cursor_grab(CursorGrabMode::None);
        self.window.set_cursor_visible(true);
    }

    /// The window gained or lost the focus. Losing it releases every held
    /// key and button and gives the cursor back; regaining it in fly mode
    /// takes the cursor again.
    pub(crate) fn focus_changed(&mut self, focused: bool) {
        if let Some(audio) = self.audio.as_mut() {
            audio.focus_changed(focused);
        }
        let display = &self.settings.display;
        let was_ui_only = crate::quality::ui_only_for_focus(display, self.input.is_focused());
        let change = self.input.focus_changed(focused);
        // With "UI Only in Background" on, losing the focus switches the
        // world off (streaming shrinks, nothing unowned kept) and gaining it
        // switches it back (OpenAC `RuntimeSettingsController.SetWindowFocused`).
        if crate::quality::ui_only_for_focus(display, focused) != was_ui_only
            && let Some(world) = self.world.as_mut()
        {
            world.apply_display(
                &self.device,
                &self.queue,
                &crate::quality::effective_display_for_focus(display, focused),
            );
        }
        if !focused {
            let mut out = Vec::new();
            self.gameplay.focus_lost(&mut out);
            self.carry_out(out);
        }
        if change.end_mouse_look {
            self.release_cursor();
        }
        if focused {
            self.follow_camera_mode();
        }
    }

    /// A mouse button went down or up: the UI's when over it, else the
    /// world's.
    pub(crate) fn mouse_button(&mut self, button: MouseButton, pressed: bool) {
        let index = button_index(button);
        let (x, y) = self.cursor_ui();
        let held: &[u16] = if pressed { &[index] } else { &[] };
        let flags = ui_input::mouse_flags(held, crate::ui::keys::ui_modifiers(self.modifiers));
        let mouse_look = self.cursor_held();
        // A key capture (the key configuration screen) takes the button.
        let capturing = self.gameplay.is_capturing();
        let routed = match self.ui.as_mut() {
            Some(ui) if self.input.is_accepting() && !capturing => Some(
                ui_input::button::<KeyCode>(ui.host_mut(), index, pressed, x, y, flags, mouse_look),
            ),
            _ => None,
        };
        let world = match routed {
            Some(routed) => self.after_ui(routed),
            None => Some(WorldInput::Button {
                button: index,
                pressed,
            }),
        };
        let world = world_button(world, index, pressed, self.input.is_button_held(index));
        if let Some((button, pressed, ui_took_it)) = world {
            self.input.button(button, pressed);
            let mut state = self.device_state();
            state.want_mouse |= ui_took_it;
            let now = self.gameplay_now();
            let now_ms = u64::try_from(self.input_clock.elapsed().as_millis()).unwrap_or(u64::MAX);
            let mut out = Vec::new();
            let cameras = self.world.as_mut().map(WorldRenderer::cameras_mut);
            self.gameplay.button(
                u8::try_from(button).unwrap_or(u8::MAX),
                pressed,
                now_ms,
                &state,
                cameras,
                now,
                &mut out,
            );
            self.carry_out(out);
        }
    }

    /// The cursor is held by the window (fly camera, mouse look or a modern
    /// mouse turning drag): the UI gets no pointer input.
    fn cursor_held(&self) -> bool {
        self.input.mouse_look() || self.gameplay.holds_cursor()
    }

    /// The wheel turned `notches` (positive away from the user) and
    /// `sideways` (positive toward the left): the UI's over a window (Shift
    /// turns it sideways), else the gameplay dispatcher's scroll (the camera
    /// zooms; toward the user zooms out).
    pub(crate) fn wheel(&mut self, sideways: f32, notches: f32) {
        let mouse_look = self.cursor_held();
        let shift = self.modifiers.shift_key();
        let routed = match self.ui.as_mut() {
            Some(ui) if self.input.is_accepting() => Some(ui_input::wheel::<KeyCode>(
                ui.host_mut(),
                (sideways, notches),
                shift,
                mouse_look,
            )),
            _ => None,
        };
        let world = match routed {
            Some(routed) => self.after_ui(routed),
            None => Some(WorldInput::Wheel(notches)),
        };
        if let Some(WorldInput::Wheel(notches)) = world {
            let state = self.device_state();
            let now = self.gameplay_now();
            let mut out = Vec::new();
            let cameras = self.world.as_mut().map(WorldRenderer::cameras_mut);
            self.gameplay.wheel(notches, &state, cameras, now, &mut out);
            self.carry_out(out);
        }
    }

    /// Type `line` into the chat entry as the keyboard would: Enter (which
    /// focuses the entry), each character, Enter (which sends it). The
    /// world's part of each key is dropped: this is the UI's input only.
    /// Without `enter`, only the characters, into whatever has the focus.
    pub(crate) fn type_line(&mut self, line: &str, enter: bool) {
        if self.ui.is_none() {
            self.notices
                .push("ui: no retail UI to type into".to_owned());
            return;
        }
        let modifiers = crate::ui::keys::ui_modifiers(ModifiersState::empty());
        let enter_key = || enter.then(|| (ac_ui::Key::Enter, "\r".to_owned()));
        let keys = enter_key()
            .into_iter()
            .chain(line.chars().map(|c| (ac_ui::Key::Unknown, c.to_string())))
            .chain(enter_key());
        for (key, typed) in keys {
            for pressed in [true, false] {
                let routed = match self.ui.as_mut() {
                    Some(ui) => ui_input::key::<()>(
                        ui.host_mut(),
                        (),
                        key,
                        pressed,
                        false,
                        modifiers,
                        pressed.then_some(typed.as_str()),
                    ),
                    None => return,
                };
                let _ = self.after_ui(routed);
            }
        }
    }

    /// One scripted input step through the UI's own routing; the world's
    /// part is dropped (see [`crate::ui::window::UiScript`]).
    pub(crate) fn script_ui(&mut self, step: &crate::ui::window::UiScript) {
        use crate::ui::window::UiScript;
        match *step {
            UiScript::Pointer { x, y } => {
                self.cursor_moved(f64::from(x), f64::from(y));
                return;
            }
            UiScript::PointerClick {
                x,
                y,
                button,
                double,
            } => {
                self.cursor_moved(f64::from(x), f64::from(y));
                let button = match button {
                    1 => MouseButton::Right,
                    2 => MouseButton::Middle,
                    _ => MouseButton::Left,
                };
                for _ in 0..if double { 2 } else { 1 } {
                    self.mouse_button(button, true);
                    self.mouse_button(button, false);
                }
                return;
            }
            _ => {}
        }
        let Some(ui) = self.ui.as_mut() else {
            self.notices.push("ui: no retail UI to script".to_owned());
            return;
        };
        let outputs = ui.script(step);
        let _ = self.after_ui(Routed::<()> {
            world: None,
            outputs,
        });
    }

    /// A click on the 3-D view: what is under the pointer is selected,
    /// examined or used (or offered to a waiting target mode), and flashes.
    fn world_click(&mut self, click: crate::world_click::WorldClick) {
        if self.cursor_held() {
            return;
        }
        let (x, y) = self.cursor.unwrap_or_default();
        let picked = self.pick_object(x as f32, y as f32);
        let facts = match (picked, self.ui.as_ref()) {
            (Some((guid, _)), Some(ui)) => ui.click_facts(guid),
            _ => crate::world_click::ClickFacts::default(),
        };
        let now_ms = u64::try_from(self.input_clock.elapsed().as_millis()).unwrap_or(u64::MAX);
        let outcome = self
            .world_clicks
            .click(click, picked.map(|(guid, _)| guid), facts, now_ms);
        self.carry_out_click(outcome, picked);
    }

    /// Place the items the UI saw let go over the world: on the object
    /// under each, or on the ground.
    fn place_world_drops(&mut self) {
        let Some(ui) = self.ui.as_mut() else {
            return;
        };
        let drops = ui.take_world_drops();
        for (payload, x, y) in drops {
            let picked = self.pick_at_ui(x, y);
            let amount = self
                .ui
                .as_mut()
                .map_or(0, |ui| ui.drag_amount(payload.obj_id));
            let outcome = crate::world_click::WorldClicks::drop_item(
                payload.obj_id,
                payload.source_kind,
                picked.map(|(guid, _)| guid),
                amount,
            );
            self.carry_out_click(outcome, picked);
        }
    }

    /// Lift what is under each world drag's press point, if anything lifts
    /// (out of combat a loose item lifts itself, your own character your
    /// main pack).
    fn start_world_drags(&mut self) {
        let starts = match self.ui.as_mut() {
            Some(ui) => ui.take_world_drag_starts(),
            None => return,
        };
        for (x, y) in starts {
            let hit = self.pick_at_ui(x, y).map_or(0, |(guid, _)| guid);
            if let Some(ui) = self.ui.as_mut() {
                ui.begin_world_drag(hit);
            }
        }
    }

    fn carry_out_click(
        &mut self,
        outcome: crate::world_click::ClickOutcome,
        picked: Option<(u32, u32)>,
    ) {
        if let (Some((guid, local_id)), Some(world)) = (picked, self.world.as_mut())
            && outcome.pulse == Some(guid)
        {
            world.begin_selection_flash(guid, local_id);
        }
        for command in outcome.commands {
            self.send_session(command);
        }
    }

    /// The server object (id and incarnation) the last world frame drew
    /// under window pixel (`x`, `y`), the player included (OpenAC
    /// `WorldSelectionQuery.PickAt`).
    fn pick_object(&mut self, x: f32, y: f32) -> Option<(u32, u32)> {
        pick_eligible(
            self.world.as_mut()?,
            self.scene.as_deref().map(SceneSource::scene),
            x,
            y,
        )
    }

    /// [`Self::pick_object`] at a position the UI reports in its own units
    /// (points on macOS), which the world picks in pixels.
    fn pick_at_ui(&mut self, x: i32, y: i32) -> Option<(u32, u32)> {
        let (x, y) = self.ui_scale().window_point(x, y);
        self.pick_object(x, y)
    }

    /// Framebuffer pixels per UI unit now: the monitor's, times the
    /// player's UI Scale while in the game (the pre-game screens fill the
    /// window on their own canvas and keep the monitor's).
    fn ui_scale(&self) -> UiScale {
        match self.ui.as_ref() {
            Some(ui) if !ui.pregame_active() => {
                self.platform_scale.with_user_percent(self.ui_scale_percent)
            }
            _ => self.platform_scale,
        }
    }

    /// The cursor in whole UI units (the world picks in pixels).
    fn cursor_ui(&self) -> (i32, i32) {
        let scale = self.ui_scale();
        self.cursor.map_or((0, 0), |(x, y)| {
            let (x, y) = scale.ui_point(x, y);
            (x as i32, y as i32)
        })
    }

    /// Act on what the UI asked for besides the world's part (session
    /// commands, the clipboard) and hand that part back.
    fn after_ui<K>(&mut self, routed: Routed<K>) -> Option<WorldInput<K>> {
        let Routed { world, outputs } = routed;
        if let Some(ui) = self.ui.as_mut() {
            let commands = ui.handle(outputs);
            self.notices.extend(ui.take_notices());
            self.send_to_session(commands);
            self.follow_settings();
            self.place_world_drops();
            self.start_world_drags();
            self.follow_keyboard();
        }
        world
    }

    /// The plugin UI's reports that do not wait for the player's input:
    /// the key map's chords (plugin hotkeys bind around them) and what
    /// the frame queued, sent to the session each frame.
    fn flush_plugin_ui(&mut self) {
        let Some(ui) = self.ui.as_mut() else {
            return;
        };
        ui.report_client_chords(self.gameplay.dispatcher().bindings());
        let commands = ui.take_plugin_commands();
        self.send_to_session(commands);
    }

    /// Send what the UI asked of the session. A typed `/mixer` line is the
    /// window's own command (it changes this program's sound settings), so
    /// it goes to the host instead.
    fn send_to_session(&mut self, commands: Vec<ac_runtime::session::SessionCommand>) {
        for command in commands {
            let typed = match &command {
                SessionCommand::Chat(text)
                | SessionCommand::ChatInput { text, .. }
                | SessionCommand::ChatEntry(ac_runtime::communication::ChatEntryCommand::Submit(
                    Some(text),
                )) => ac_audio::options::mixer_command_arguments(text),
                _ => None,
            };
            if let Some(arguments) = typed {
                self.mixer_requests.push(arguments.to_owned());
                continue;
            }
            let sent = self.scene.as_deref().is_some_and(|s| s.send(command));
            if !sent {
                self.notices
                    .push("ui: a request was dropped: no session to send it to".to_owned());
            }
        }
    }

    /// Feed the UI from the scene, draw it, and upload what it draws; its
    /// requests go to the session.
    fn prepare_ui(&mut self) {
        let scale = self.ui_scale();
        let framebuffer = (self.config.width, self.config.height);
        let (Some(ui), Some(renderer)) = (self.ui.as_mut(), self.ui_renderer.as_mut()) else {
            return;
        };
        if let Some(source) = self.scene.as_deref_mut() {
            // The map's date line reads the renderer's calendar.
            let date = self
                .world
                .as_ref()
                .and_then(|w| w.calendar_date(Some(source.scene()), Instant::now()));
            ui.set_calendar(date);
            ui.feed(source.scene_mut());
            for minimize in ui.take_minimize_requests() {
                self.window.set_minimized(minimize);
            }
            // The 3-D viewports that show: drawn by the world renderer,
            // their textures handed to the UI before it draws.
            let (requests, controls) =
                ui.viewport_requests(source.scene(), scale.pixels_per_unit());
            let flashes = ui.take_figure_flashes();
            if let Some(world) = self.world.as_mut() {
                for (whole_figure, part_mask) in flashes {
                    world.flash_doll(whole_figure, part_mask);
                }
                let outcomes = world.prepare_viewports(
                    &self.device,
                    &self.queue,
                    Some(source.scene()),
                    &requests,
                    &controls,
                );
                for &(slot, outcome) in &outcomes {
                    if outcome == crate::viewport::ViewportOutcome::Drawn
                        && let Some((view, generation)) = world.viewport_texture(slot)
                    {
                        renderer.bind_view(&self.device, slot.texture(), view, generation);
                    }
                }
                ui.show_viewports(&outcomes);
            }
        }
        // The UI, its overlays included, is laid out and drawn in UI units.
        let size = scale.ui_size(framebuffer);
        // The overlays project with the camera the world was last drawn
        // with, onto the framebuffer in units.
        let (camera, target) = match (self.world.as_ref(), self.scene.as_deref()) {
            (Some(world), Some(source)) => crate::ui::overlays::world_view(
                world,
                source.scene(),
                ui.dats(),
                &ui.facts().objects,
                scale.overlay_viewport(framebuffer),
            ),
            _ => (None, None),
        };
        ui.set_world_view(camera, target);
        let navigation = match (self.world.as_ref(), self.scene.as_deref()) {
            (Some(world), Some(source)) => {
                crate::ui::overlays::navigation_lines(world, source.scene())
            }
            _ => Vec::new(),
        };
        ui.set_plugin_world(
            self.world.as_ref(),
            self.scene.as_deref().map(SceneSource::scene),
        );
        // The navigation view and the plugins' world lines, triangles and
        // ground shapes are drawn in the world pass, with its depth (lines
        // as in OpenAC `PluginWorldLineRenderer`).
        if let Some(world) = self.world.as_mut() {
            world.set_navigation_lines(&self.device, &self.queue, &navigation);
            world.set_plugin_lines(&self.device, &self.queue, ui.plugin_world_lines());
            world.set_plugin_triangles(
                &self.device,
                &self.queue,
                ui.plugin_world_triangles(),
                ui.plugin_world_shapes(),
            );
            world.set_icon_probes(&self.device, &self.queue, ui.plugin_icon_probes());
        }
        // The object under the pointer (the world tooltip and the cursor's
        // "found" forms); none while the window holds the cursor.
        let held = self.input.mouse_look() || self.gameplay.holds_cursor();
        let hover = match (self.cursor, self.world.as_mut()) {
            (Some((x, y)), Some(world)) if !held => {
                let scene = self.scene.as_deref().map(SceneSource::scene);
                pick_eligible(world, scene, x as f32, y as f32).map(|(guid, _)| {
                    let name = scene
                        .and_then(|s| s.object_by_guid(guid))
                        .and_then(|o| o.name.clone());
                    (guid, name)
                })
            }
            _ => None,
        };
        ui.set_world_hover(hover);
        if let Some(world) = self.world.as_ref() {
            let (fps, multiplier) = world.level_of_detail();
            ui.set_level_of_detail(fps, multiplier);
        }
        ui.set_pixels_per_unit(scale.pixels_per_unit());
        let list = ui.frame(Instant::now(), size);
        // Drawn in framebuffer pixels, on the whole-pixel grid.
        let batch = UiBatch::build_scaled(&list, scale.pixels_per_unit());
        renderer.prepare(
            &self.device,
            &self.queue,
            &batch,
            framebuffer,
            &mut ui.surfaces(),
        );
        for id in renderer.take_missing() {
            self.notices.push(format!(
                "ui: render surface {id:#010x} is not in the dats; drawn as magenta"
            ));
        }
        let commands = ui.drain();
        self.notices.extend(ui.take_notices());
        self.send_to_session(commands);
        self.follow_settings();
    }

    /// The cursor left the window: the UI's hover ends there (its tooltip
    /// goes, a plugin canvas hears its leave).
    pub(crate) fn cursor_left(&mut self) {
        let routed = match self.ui.as_mut() {
            Some(ui) if self.input.is_accepting() => ui_input::left::<KeyCode>(ui.host_mut()),
            _ => return,
        };
        let _ = self.after_ui(routed);
    }

    /// The cursor moved: the UI hovers and drags; over the world, dragging
    /// with the left button turns the orbit camera.
    pub(crate) fn cursor_moved(&mut self, x: f64, y: f64) {
        let previous = self.cursor.replace((x, y));
        let mouse_look = self.cursor_held();
        let (ui_x, ui_y) = self.ui_scale().ui_point(x, y);
        let routed = match self.ui.as_mut() {
            Some(ui) if self.input.is_accepting() => Some(ui_input::moved::<KeyCode>(
                ui.host_mut(),
                ui_x as i32,
                ui_y as i32,
                mouse_look,
            )),
            _ => None,
        };
        if let Some(routed) = routed
            && self.after_ui(routed).is_none()
        {
            return;
        }
        let Some((px, py)) = previous else {
            return;
        };
        let state = self.device_state();
        let now = self.gameplay_now();
        let cameras = self.world.as_mut().map(WorldRenderer::cameras_mut);
        self.gameplay
            .pointer_moved((x - px) as f32, (y - py) as f32, &state, cameras, now);
    }

    /// Raw mouse movement while the window holds the cursor: the fly
    /// camera's look, or mouse look's samples.
    pub(crate) fn mouse_motion(&mut self, dx: f64, dy: f64) {
        if !self.cursor_held() {
            return;
        }
        let cameras = self.world.as_mut().map(WorldRenderer::cameras_mut);
        self.gameplay.raw_motion(dx as f32, dy as f32, cameras);
    }
}

/// What the UI probe script and the world lifecycle automation reach
/// (`crate::automation`).
impl AttachedWindow {
    /// One frame of the UI probe script; what it says goes to the notices.
    fn tick_automation(&mut self, dt: f64) {
        let Some(mut runner) = self.ui_probe.take() else {
            return;
        };
        let mut log = Vec::new();
        {
            let mut target = crate::automation::WindowTarget {
                window: self,
                empty: ac_ui::root::UiRoot::new(),
            };
            runner.tick(dt, &mut target, &mut log);
        }
        self.notices
            .extend(log.into_iter().map(|line| format!("ui-probe: {line}")));
        if !runner.completed() {
            self.ui_probe = Some(runner);
        }
    }

    /// After a frame: the checkpoints asked for are written with what it
    /// drew.
    fn after_automation_frame(&mut self, outcome: &FrameOutcome) {
        let reveal = self.reveal_facts();
        let stats = self.world.as_ref().map(WorldRenderer::stats);
        if let Some(automation) = self.world_automation.as_mut() {
            let mut log = Vec::new();
            automation.after_frame(reveal, &outcome.world, stats.as_ref(), &mut log);
            self.notices.extend(log);
        }
    }

    /// The interface, if the window has one.
    pub(crate) fn ui_root(&self) -> Option<&ac_ui::root::UiRoot> {
        self.ui.as_ref().map(|ui| &ui.host().root)
    }

    /// The item table the interface shows.
    pub(crate) fn ui_objects(&self) -> Option<&ac_core::items::ClientObjectTable> {
        self.ui.as_ref().map(|ui| &ui.facts().objects)
    }

    /// A chat line or client command, as typed in the chat box.
    pub(crate) fn submit_line(&mut self, text: &str) -> Result<(), String> {
        if self.scene.is_none() {
            return Err("command submission needs a session".into());
        }
        self.send_to_session(vec![SessionCommand::Chat(text.to_owned())]);
        Ok(())
    }

    /// Automation presses (`hold`: `None`), holds or lets go of a key-map
    /// action.
    pub(crate) fn automation_action(
        &mut self,
        action: ac_ui::input::InputAction,
        hold: Option<bool>,
    ) -> Result<(), String> {
        let state = self.device_state();
        let now = self.gameplay_now();
        let mut out = Vec::new();
        let cameras = self.world.as_mut().map(WorldRenderer::cameras_mut);
        let accepted = self
            .gameplay
            .automation_action(action, hold, &state, cameras, now, &mut out);
        self.carry_out(out);
        if accepted {
            Ok(())
        } else {
            Err("the key map refused it (a capture or a text field has the keys)".into())
        }
    }

    /// Mouse look turned by (`dx`, `dy`) pixels.
    pub(crate) fn automation_mouse_look(&mut self, dx: f32, dy: f32) {
        let cameras = self.world.as_mut().map(WorldRenderer::cameras_mut);
        self.gameplay.raw_motion(dx, dy, cameras);
    }

    /// The world's lifecycle facts now.
    pub(crate) fn reveal_facts(&self) -> crate::automation::RevealFacts {
        let has_player = self
            .scene
            .as_deref()
            .is_some_and(|s| s.scene().player().is_some());
        let ready = self.world.as_ref().is_some_and(|w| {
            crate::automation::world_ready(
                has_player,
                w.has_revealed(),
                w.is_withholding(),
                w.portal_hides_world(),
            )
        });
        crate::automation::RevealFacts {
            ready,
            world_viewport_observed: crate::automation::world_viewport_observed(
                ready,
                self.last_world.normal_world_drawn,
            ),
            in_portal_space: self
                .world
                .as_ref()
                .is_some_and(WorldRenderer::portal_hides_world),
            materializations: self.portal_materializations,
        }
    }

    /// The framebuffer's size.
    pub(crate) fn framebuffer_size(&self) -> (u32, u32) {
        (self.config.width, self.config.height)
    }

    /// The world lifecycle automation, when an artifact folder was given.
    pub(crate) fn world_automation(&mut self) -> Option<&mut crate::automation::WorldAutomation> {
        self.world_automation.as_mut()
    }

    /// [`Self::world_automation`], to read.
    pub(crate) fn world_automation_ref(&self) -> Option<&crate::automation::WorldAutomation> {
        self.world_automation.as_ref()
    }

    /// Change a display setting as the options window would (applied and
    /// saved).
    pub(crate) fn automation_display(
        &mut self,
        change: impl FnOnce(&mut ac_host::settings::DisplaySettings),
    ) -> Result<(), String> {
        let Some(ui) = self.ui.as_mut() else {
            return Err("display automation needs the interface".into());
        };
        ui.change_display(change);
        self.follow_settings();
        Ok(())
    }

    /// Resize the window to `width` x `height` pixels.
    pub(crate) fn automation_resize(&mut self, width: u32, height: u32) -> Result<(), String> {
        let _ = self
            .window
            .request_inner_size(winit::dpi::PhysicalSize::new(width, height));
        Ok(())
    }

    /// Ask the host to close this window.
    pub(crate) fn request_close(&mut self) {
        self.close_requested = true;
    }

    /// Automation asked the window to close (once).
    pub(crate) fn take_close_request(&mut self) -> bool {
        std::mem::take(&mut self.close_requested)
    }
}

/// The object `world` drew under pixel (`x`, `y`), kept only while the
/// scene still holds that incarnation of it (a stale frame never picks a
/// newer object reusing the id) and it is not hidden from the UI: its id
/// and local id. A hidden object nearest the pointer finds nothing; it
/// does not let the pick through to what is behind it.
fn pick_eligible(
    world: &mut WorldRenderer,
    scene: Option<&crate::scene::Scene>,
    x: f32,
    y: f32,
) -> Option<(u32, u32)> {
    let hit = world.pick(x, y, 0)?;
    found_in_scene(scene?, hit.guid, hit.local_id)
}

/// What a pick that hit object `guid` drawn as `local_id` finds in `scene`:
/// that object, while the scene still holds that incarnation of it and it
/// is not hidden from the UI; else nothing.
fn found_in_scene(scene: &crate::scene::Scene, guid: u32, local_id: u32) -> Option<(u32, u32)> {
    scene
        .object_by_guid(guid)
        .filter(|o| o.key.local_id == local_id)
        .filter(|o| picks_as_found(o.radar.public_weenie_bitfield))
        .map(|_| (guid, local_id))
}

/// Whether an object with public description bits `bitfield` is found when
/// picked: not when its UI-hidden bit is set. The game marks traps and
/// similar scenery that way so the player cannot select, use or name them
/// under the pointer, and the pick then finds nothing at all.
fn picks_as_found(bitfield: u32) -> bool {
    bitfield & ac_core::items::PublicWeenieFlags::UI_HIDDEN.bits() == 0
}

/// A mouse button as a small number for the held-button set.
fn button_index(button: MouseButton) -> u16 {
    match button {
        MouseButton::Left => 0,
        MouseButton::Right => 1,
        MouseButton::Middle => 2,
        MouseButton::Back => 3,
        MouseButton::Forward => 4,
        MouseButton::Other(n) => n.saturating_add(5),
    }
}

/// What a mouse button change (`button` going down or up) does to the
/// world, given what the UI passed on (`world`) and whether the world holds
/// that button (`world_held`): (button, pressed, the UI took it). A release
/// the UI kept whose press went to the world (a drag lifted off the world
/// and let go over a window or the world) still lets the world's button
/// go, as one the UI took, so it is no click; otherwise the world would go
/// on thinking the button held (the orbit camera kept turning).
fn world_button<K>(
    world: Option<WorldInput<K>>,
    button: u16,
    pressed: bool,
    world_held: bool,
) -> Option<(u16, bool, bool)> {
    match world {
        Some(WorldInput::Button { button, pressed }) => Some((button, pressed, false)),
        _ if !pressed && world_held => Some((button, false, true)),
        _ => None,
    }
}

/// The drawable size for a framebuffer resize, or `None` for a size with no
/// area (minimized), which changes nothing.
#[must_use]
pub(crate) fn framebuffer_resize(width: u32, height: u32) -> Option<(u32, u32)> {
    (width > 0 && height > 0).then_some((width, height))
}

/// The refresh rate of the monitor the window is on, whole hertz.
fn monitor_refresh_hz(window: &Window) -> Option<u32> {
    let millihertz = window.current_monitor()?.refresh_rate_millihertz()?;
    Some((millihertz + 500) / 1000)
}

/// The multisample counts (above 1) the world can draw with: those the
/// adapter supports for both the frame's colour format and the depth
/// format. Without the adapter-specific format features only WebGPU's
/// guaranteed 4 is usable.
fn supported_sample_counts(
    adapter: &wgpu::Adapter,
    device: &wgpu::Device,
    color: wgpu::TextureFormat,
) -> Vec<u32> {
    if !device
        .features()
        .contains(wgpu::Features::TEXTURE_ADAPTER_SPECIFIC_FORMAT_FEATURES)
    {
        return vec![4];
    }
    let color = adapter.get_texture_format_features(color).flags;
    let depth = adapter
        .get_texture_format_features(crate::renderer::pipelines::DEPTH_FORMAT)
        .flags;
    [2, 4, 8, 16]
        .into_iter()
        .filter(|&n| color.sample_count_supported(n) && depth.sample_count_supported(n))
        .collect()
}

/// Pick the adapter: every adapter that can present to `surface`, ranked
/// (or the override), then checked against what the renderer needs. The
/// capability report is written to the diagnostics directory; an adapter
/// that falls short is refused with a message naming what is missing.
fn choose_adapter(
    instance: &wgpu::Instance,
    surface: &wgpu::Surface<'_>,
    options: &GraphicsOptions,
) -> Result<(wgpu::Adapter, AdapterSummary), RenderError> {
    let adapters: Vec<wgpu::Adapter> = block_on(instance.enumerate_adapters(wgpu::Backends::all()))
        .into_iter()
        .filter(|a| a.is_surface_supported(surface))
        .collect();
    let candidates: Vec<AdapterCandidate> = adapters
        .iter()
        .enumerate()
        .map(|(index, a)| {
            let info = a.get_info();
            AdapterCandidate {
                index,
                name: info.name,
                device_type: info.device_type,
                backend: info.backend,
            }
        })
        .collect();
    let lines: Vec<String> = candidates
        .iter()
        .map(|c| {
            format!(
                "{}: {} ({:?}, {:?})",
                c.index, c.name, c.device_type, c.backend
            )
        })
        .collect();
    let Some(choice) = gpu::choose_adapter(&candidates, options.gpu_adapter.as_deref()) else {
        return Err(RenderError::GpuUnsupported(format!(
            "no GPU adapter can present to the window (platform {}); update the graphics driver",
            gpu::platform_name()
        )));
    };
    let adapter = adapters
        .into_iter()
        .nth(choice.index)
        .ok_or(RenderError::SurfaceUnsupported)?;
    let info = adapter.get_info();
    let presents = !surface.get_capabilities(&adapter).formats.is_empty();
    let record = gpu::check_capabilities(
        &info,
        &adapter.limits(),
        adapter.features(),
        presents,
        options.gpu_force_unsupported.as_deref(),
    );
    let report = options.diagnostics_dir.as_ref().and_then(|dir| {
        let path = dir.join(gpu::REPORT_FILE_NAME);
        std::fs::create_dir_all(dir)
            .and_then(|()| std::fs::write(&path, gpu::report_json(&record, &choice.reason)))
            .ok()
            .map(|()| path)
    });
    if !record.is_supported() {
        let path = report
            .clone()
            .unwrap_or_else(|| PathBuf::from("(no diagnostics directory: report not written)"));
        return Err(RenderError::GpuUnsupported(gpu::unsupported_message(
            &record, &path,
        )));
    }
    Ok((
        adapter,
        AdapterSummary {
            backend: info.backend,
            name: info.name,
            selection: choice.reason,
            candidates: lines,
            report,
            timestamps: record.timestamps,
            driver: record.driver.clone(),
        },
    ))
}

/// Put `image` in the window's title bar and task bar. A platform without
/// window icons (Wayland) or an image the platform refuses leaves the
/// window as it was: an icon is never worth failing an attach.
fn set_icon(window: &Window, image: &ac_dat::texture::DecodedTexture) {
    match winit::window::Icon::from_rgba(image.rgba.clone(), image.width, image.height) {
        Ok(icon) => window.set_window_icon(Some(icon)),
        Err(error) => eprintln!("window icon: {error}"),
    }
}

/// What the window asks of the GPU device: `required_features`, and memory
/// blocks sized to what the client uses.
///
/// wgpu's default memory hint ("performance") takes GPU memory in blocks of
/// 128 to 256 MB, plus 64 to 128 MB for each kind of CPU-visible memory
/// (uploads, read-backs). The client fills them slowly or never: about
/// 290 MB used of 512 MB taken right after login, and 4 KB of timer
/// read-backs holding a whole 64 MB block. Each block counts in full in the
/// process's committed memory as soon as it is taken, so the slack showed up
/// as ~220 MB of commit nothing used. The "memory usage" hint takes blocks of
/// 8 to 64 MB (4 to 32 MB CPU-visible), so the slack stays within one block.
/// A resource larger than a block still gets a block of its own, and nothing
/// drawn changes: only where resources sit inside the GPU's memory.
pub(crate) fn device_descriptor(
    label: &'static str,
    required_features: wgpu::Features,
) -> wgpu::DeviceDescriptor<'static> {
    wgpu::DeviceDescriptor {
        label: Some(label),
        required_features,
        memory_hints: wgpu::MemoryHints::MemoryUsage,
        ..Default::default()
    }
}

/// Runs a future to completion on this thread.
///
/// wgpu's adapter and device requests are futures so they can run in a browser; on desktop they
/// complete immediately, so parking the thread until woken is all the executor needed.
pub(crate) fn block_on<F: Future>(future: F) -> F::Output {
    struct Unparker(Thread);
    impl Wake for Unparker {
        fn wake(self: Arc<Self>) {
            self.0.unpark();
        }
    }

    let waker = Waker::from(Arc::new(Unparker(thread::current())));
    let mut cx = Context::from_waker(&waker);
    let mut future = pin!(future);
    loop {
        match future.as_mut().poll(&mut cx) {
            Poll::Ready(output) => return output,
            Poll::Pending => thread::park(),
        }
    }
}

/// Screenshots asked for and not yet taken, and those taken and not yet
/// reported: a queue, one taken after each frame drawn, so every request
/// is answered in the order asked (two asked within one frame are taken
/// from it and the next).
#[derive(Debug)]
struct Screenshots<R> {
    pending: std::collections::VecDeque<PathBuf>,
    taken: Vec<(PathBuf, R)>,
}

impl<R> Default for Screenshots<R> {
    fn default() -> Self {
        Self {
            pending: std::collections::VecDeque::new(),
            taken: Vec::new(),
        }
    }
}

impl<R> Screenshots<R> {
    /// Take one after a frame.
    fn request(&mut self, path: PathBuf) {
        self.pending.push_back(path);
    }

    /// The oldest request, to take now.
    fn next(&mut self) -> Option<PathBuf> {
        self.pending.pop_front()
    }

    /// Requests wait.
    fn is_waiting(&self) -> bool {
        !self.pending.is_empty()
    }

    /// How the request for `path` went.
    fn answer(&mut self, path: PathBuf, result: R) {
        self.taken.push((path, result));
    }

    /// The answers since the last call, oldest first.
    fn take_results(&mut self) -> Vec<(PathBuf, R)> {
        std::mem::take(&mut self.taken)
    }
}

/// The selection a reveal that begins clears (OpenAC
/// `WorldGenerationQuiescence.CaptureBegin`): the selected object, when the
/// window's world shows it (`shown`: it stands in the resident world).
fn revealed_selection(selected: Option<u32>, shown: impl Fn(u32) -> bool) -> Option<u32> {
    selected.filter(|&guid| guid != 0 && shown(guid))
}

#[cfg(test)]
mod tests {
    use super::{
        NO_IMAGE_RETRY, SURFACE_DRAWING, SURFACE_OCCLUDED, SURFACE_TIMED_OUT, Screenshots,
        SurfaceNotes, block_on, found_in_scene, framebuffer_resize, picks_as_found, redraw_after,
        retail_screenshot_path, revealed_selection, world_button,
    };
    use crate::frame::FrameOutcome;
    use crate::ui::input::WorldInput;
    use std::time::Duration;

    #[test]
    fn an_object_hidden_from_the_ui_is_never_found_under_the_pointer() {
        use ac_core::items::PublicWeenieFlags;
        assert!(picks_as_found(PublicWeenieFlags::NONE.bits()));
        assert!(picks_as_found(
            (PublicWeenieFlags::STUCK | PublicWeenieFlags::ATTACKABLE).bits()
        ));
        assert!(!picks_as_found(PublicWeenieFlags::UI_HIDDEN.bits()));
        assert!(!picks_as_found(
            (PublicWeenieFlags::STUCK | PublicWeenieFlags::UI_HIDDEN).bits()
        ));
    }

    #[test]
    fn a_pick_on_an_object_hidden_from_the_ui_finds_nothing_in_the_scene() {
        use crate::scene::Scene;
        use ac_core::items::PublicWeenieFlags;
        use ac_runtime::session::PresentationEvent;
        use ac_runtime::world::{ObjectKey, ObjectView, RadarFacts, WorldSnapshot};
        let view = |local_id: u32, guid: u32, bitfield: PublicWeenieFlags| ObjectView {
            key: ObjectKey {
                local_id,
                incarnation: 1,
            },
            guid,
            name: None,
            setup_id: None,
            position: None,
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
                public_weenie_bitfield: bitfield.bits(),
                ..RadarFacts::default()
            },
            motion: None,
            effects: ac_runtime::world::EffectFacts::default(),
            step_blend: None,
        };
        const LIFESTONE: u32 = 0x7000_0001;
        const ACID: u32 = 0x7000_0002;
        let mut scene = Scene::default();
        scene.apply(PresentationEvent::Snapshot(WorldSnapshot {
            player_guid: None,
            objects: vec![
                view(1, LIFESTONE, PublicWeenieFlags::LIFESTONE),
                view(
                    2,
                    ACID,
                    PublicWeenieFlags::STUCK | PublicWeenieFlags::UI_HIDDEN,
                ),
            ],
        }));
        assert_eq!(found_in_scene(&scene, LIFESTONE, 1), Some((LIFESTONE, 1)));
        // A stale frame's hit on an id the scene now holds as another
        // incarnation finds nothing.
        assert_eq!(found_in_scene(&scene, LIFESTONE, 9), None);
        assert_eq!(found_in_scene(&scene, ACID, 2), None);
    }

    #[test]
    fn a_release_the_ui_kept_still_lets_go_of_a_button_the_world_holds() {
        // GitHub #45: a drag lifted off the world ends with the release on
        // the UI's side; the world went on thinking the left button held,
        // and the orbit camera kept turning until the next click.
        let passed = Some(WorldInput::<()>::Button {
            button: 0,
            pressed: false,
        });
        assert_eq!(
            world_button(passed, 0, false, true),
            Some((0, false, false))
        );
        assert_eq!(
            world_button::<()>(None, 0, false, true),
            Some((0, false, true))
        );
        // Nothing for the world when it did not hold it, or for a press.
        assert_eq!(world_button::<()>(None, 0, false, false), None);
        assert_eq!(world_button::<()>(None, 0, true, true), None);
        assert_eq!(
            world_button(Some(WorldInput::<()>::Moved), 0, true, true),
            None
        );
    }

    #[test]
    fn a_frame_without_an_image_is_tried_again_after_a_wait_never_at_once() {
        let drawn = FrameOutcome::default();
        let no_image = FrameOutcome {
            skipped_no_image: true,
            ..FrameOutcome::default()
        };
        let no_area = FrameOutcome {
            skipped_zero_area: true,
            ..FrameOutcome::default()
        };
        assert_eq!(redraw_after(&drawn), Some(Duration::ZERO));
        assert_eq!(redraw_after(&no_image), Some(NO_IMAGE_RETRY));
        assert!(NO_IMAGE_RETRY >= Duration::from_millis(100));
        assert_eq!(redraw_after(&no_area), None);
    }

    #[test]
    fn each_surface_change_is_reported_once() {
        let mut notes = SurfaceNotes::default();
        assert_eq!(notes.observe(SURFACE_DRAWING), None);
        assert_eq!(
            notes.observe(SURFACE_OCCLUDED).as_deref(),
            Some("surface occluded (was drawing)")
        );
        assert_eq!(notes.observe(SURFACE_OCCLUDED), None);
        assert_eq!(
            notes.observe(SURFACE_DRAWING).as_deref(),
            Some("surface drawing (was occluded)")
        );
        let mut fresh = SurfaceNotes::default();
        assert_eq!(
            fresh.observe(SURFACE_TIMED_OUT).as_deref(),
            Some("surface timed out")
        );
    }

    #[test]
    fn the_device_takes_gpu_memory_in_small_blocks() {
        // A 1 MB vertex buffer and a 1 MB upload buffer: each lands in the
        // first block of its kind, which must be small (8 MB for GPU memory,
        // 4 MB CPU-visible), not the 128 MB / 64 MB the default hint takes.
        let Some((device, _queue)) = crate::test_gpu::fresh_device() else {
            return;
        };
        const MB: u64 = 1 << 20;
        let _vertices = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("one MB of vertices"),
            size: MB,
            usage: wgpu::BufferUsages::VERTEX | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });
        let _upload = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("one MB to upload"),
            size: MB,
            usage: wgpu::BufferUsages::MAP_WRITE | wgpu::BufferUsages::COPY_SRC,
            mapped_at_creation: false,
        });
        // A backend without its own sub-allocator reports nothing to check.
        let Some(report) = device.generate_allocator_report() else {
            return;
        };
        assert!(!report.blocks.is_empty());
        for block in &report.blocks {
            assert!(
                block.size <= 8 * MB,
                "a {} MB block for {} MB used",
                block.size / MB,
                report.total_allocated_bytes / MB
            );
        }
    }

    #[test]
    fn a_reveal_clears_only_a_selection_the_world_shows() {
        // OpenAC `WorldGenerationQuiescence.CaptureBegin`: the selection
        // clears when the selected object is a live world object the
        // window shows; nothing selected, or not shown, stays.
        assert_eq!(revealed_selection(Some(7), |g| g == 7), Some(7));
        assert_eq!(revealed_selection(Some(8), |g| g == 7), None);
        assert_eq!(revealed_selection(None, |_| true), None);
    }
    use std::path::{Path, PathBuf};

    #[test]
    fn the_screenshot_key_takes_the_first_free_retail_name() {
        // OpenAC `FrameScreenshotController.TryRequestRetailScreenshot`.
        let dir = Path::new("shots");
        assert_eq!(
            retail_screenshot_path(dir, |_| false),
            Some(dir.join("ScreenShot00000.png"))
        );
        let taken = [
            dir.join("ScreenShot00000.png"),
            dir.join("ScreenShot00001.png"),
        ];
        assert_eq!(
            retail_screenshot_path(dir, |p| taken.iter().any(|t| t == p)),
            Some(dir.join("ScreenShot00002.png"))
        );
        assert_eq!(retail_screenshot_path(dir, |_| true), None);
    }

    #[test]
    fn two_screenshots_asked_in_one_frame_are_both_answered_in_order() {
        // `WindowHost::screenshot`: every request is answered once a frame
        // has been drawn; a second one before that frame must not replace
        // the first. OpenAC `FrameScreenshotController` queues them and
        // takes one per frame.
        let mut shots = Screenshots::<Result<(), String>>::default();
        shots.request(PathBuf::from("a.png"));
        shots.request(PathBuf::from("b.png"));
        // The first frame.
        let first = shots.next().unwrap();
        shots.answer(first, Ok(()));
        assert!(shots.is_waiting(), "the second waits for the next frame");
        // The next.
        let second = shots.next().unwrap();
        shots.answer(second, Err("disk full".to_owned()));
        assert!(!shots.is_waiting() && shots.next().is_none());
        assert_eq!(
            shots.take_results(),
            [
                (PathBuf::from("a.png"), Ok(())),
                (PathBuf::from("b.png"), Err("disk full".to_owned())),
            ]
        );
        assert!(shots.take_results().is_empty());
    }

    #[test]
    fn block_on_returns_a_ready_value() {
        assert_eq!(block_on(async { 7 }), 7);
    }

    #[test]
    fn block_on_waits_for_a_wake_from_another_thread() {
        use std::sync::mpsc;
        let (tx, rx) = mpsc::channel::<u32>();
        let handle = std::thread::spawn(move || {
            std::thread::sleep(std::time::Duration::from_millis(20));
            tx.send(42).unwrap();
        });
        // A future that is pending until the value arrives, re-polled on wake.
        let value = block_on(std::future::poll_fn(|cx| match rx.try_recv() {
            Ok(v) => std::task::Poll::Ready(v),
            Err(_) => {
                let waker = cx.waker().clone();
                std::thread::spawn(move || {
                    std::thread::sleep(std::time::Duration::from_millis(5));
                    waker.wake();
                });
                std::task::Poll::Pending
            }
        }));
        assert_eq!(value, 42);
        handle.join().unwrap();
    }

    /// From OpenAC `tests/AcDream.App.Tests/Rendering/FramebufferResizeControllerTests.cs`:
    /// a size without area is rejected and changes nothing.
    #[test]
    fn a_framebuffer_without_area_is_rejected() {
        assert_eq!(framebuffer_resize(1600, 900), Some((1600, 900)));
        for (w, h) in [(0, 720), (1280, 0), (0, 0)] {
            assert_eq!(framebuffer_resize(w, h), None);
        }
    }
}
