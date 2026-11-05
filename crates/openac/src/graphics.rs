//! The window host's startup options, read from the environment in one
//! place for both command forms, and how its reports are printed.

use crate::stdio::{errln, outln};
use std::path::PathBuf;

use ac_host::settings::{DisplaySettings, PlayerSettings, SettingsStore};
use ac_render::camera::OrbitOverrides;
use ac_render::platform::{DisplayProtocol, HostOs, resolve_display};
use ac_render::ui::window::{UiScript, parse_key};
use ac_render::{GraphicsOptions, HostEvent, HostOptions};

use crate::options::{OptionsError, parse_radius};

/// Everything the graphical side reads from the environment.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct GraphicsEnv {
    /// `OPENAC_DAT_DIR`.
    pub dat_dir: Option<PathBuf>,
    /// `OPENAC_AC_DIR`: the game's install folder (its
    /// `controlsntrols.ini`).
    pub ac_dir: Option<PathBuf>,
    /// `OPENAC_LANDBLOCK_RADIUS`: the near tier.
    pub landblock_radius: Option<u32>,
    /// `OPENAC_FAR_RADIUS`: the far (terrain-only) tier.
    pub far_radius: Option<u32>,
    /// `OPENAC_GPU_ADAPTER`: an adapter index or name substring.
    pub gpu_adapter: Option<String>,
    /// `OPENAC_GPU_FORCE_UNSUPPORTED`: a required capability to pretend is
    /// missing.
    pub gpu_force_unsupported: Option<String>,
    /// `OPENAC_UNCAPPED_RENDER=1`: no vsync and no frame limiter.
    pub uncapped: bool,
    /// `OPENAC_FRAME_PROF=1`: the frame profiler.
    pub frame_profiler: bool,
    /// `OPENAC_FRAME_HISTORY`: write every frame's timings here as CSV.
    pub frame_history: Option<PathBuf>,
    /// `OPENAC_NO_BLOCK_COMPRESSION=1`: decode block-compressed textures
    /// even when the GPU samples them.
    pub no_block_compression: bool,
    /// `OPENAC_ORBIT_DISTANCE_METERS`, `OPENAC_ORBIT_YAW_DEGREES`,
    /// `OPENAC_ORBIT_PITCH_DEGREES`: where the orbit camera starts.
    pub orbit: OrbitOverrides,
    /// `OPENAC_DISPLAY_PROTOCOL` and the session's display variables.
    pub display: DisplayProtocol,
    /// `OPENAC_PROBE_REVEAL_RADIUS`: landblocks the login reveal waits for.
    pub reveal_radius: Option<u32>,
    /// `OPENAC_WORLD_TIME`, `OPENAC_DAY_GROUP`: a pinned time of day and day
    /// group.
    pub sky: ac_render::environment::SkyOptions,
    /// `OPENAC_NO_AUDIO` and the sound probes.
    pub audio: ac_render::audio::AudioLaunchOptions,
    /// `OPENAC_MSAA_SAMPLES`, `OPENAC_ANISOTROPIC`, `OPENAC_A2C`,
    /// `OPENAC_MAX_COMPLETIONS_PER_FRAME`: quality preset overrides.
    pub quality: ac_render::quality::QualityOverrides,
    /// `OPENAC_RESIDENCY_*`: budgets for meshes and textures nothing draws.
    pub residency: ac_render::unowned_cache::ResidencyBudgets,
    /// `OPENAC_UI_PROBE_SCRIPT`: a UI probe script to play in the window.
    pub ui_probe_script: Option<PathBuf>,
    /// `OPENAC_UI_PROBE_DUMP=1`: dump the element tree when it starts.
    pub ui_probe_dump: bool,
    /// `OPENAC_AUTOMATION_ARTIFACT_DIR`: where named screenshots,
    /// checkpoints and signals go.
    pub automation_artifact_dir: Option<PathBuf>,
    /// `OPENAC_MOTION_LOG=1`: the window logs the motion states it takes
    /// up for the player and the selection.
    pub motion_log: bool,
}

fn non_empty(value: Option<String>) -> Option<String> {
    value.filter(|v| !v.trim().is_empty())
}

/// The largest far radius: the game's largest landscape draw distance.
const MAX_FAR_RADIUS: u32 = 25;

/// `OPENAC_FAR_RADIUS`: 0 to 25 landblocks.
fn parse_far_radius(text: &str) -> Result<u32, OptionsError> {
    text.trim()
        .parse::<u32>()
        .ok()
        .filter(|r| *r <= MAX_FAR_RADIUS)
        .ok_or_else(|| {
            OptionsError::Graphics(format!(
                "OPENAC_FAR_RADIUS: invalid value '{text}': expected 0 to {MAX_FAR_RADIUS}"
            ))
        })
}

impl GraphicsEnv {
    /// Read the variables through `env`.
    pub fn parse(env: impl Fn(&str) -> Option<String>) -> Result<Self, OptionsError> {
        let landblock_radius = non_empty(env("OPENAC_LANDBLOCK_RADIUS"))
            .as_deref()
            .map(parse_radius)
            .transpose()?;
        let far_radius = non_empty(env("OPENAC_FAR_RADIUS"))
            .as_deref()
            .map(parse_far_radius)
            .transpose()?;
        let display = resolve_display(HostOs::current(), &env)
            .map_err(OptionsError::Graphics)?
            .protocol;
        Ok(Self {
            dat_dir: non_empty(env("OPENAC_DAT_DIR")).map(PathBuf::from),
            ac_dir: env("OPENAC_AC_DIR")
                .filter(|v| !v.is_empty())
                .map(PathBuf::from),
            landblock_radius,
            far_radius,
            gpu_adapter: non_empty(env(ac_render::gpu::ADAPTER_OVERRIDE_VAR)),
            gpu_force_unsupported: non_empty(env(ac_render::gpu::FORCE_UNSUPPORTED_VAR)),
            uncapped: env("OPENAC_UNCAPPED_RENDER").as_deref() == Some("1"),
            frame_profiler: env("OPENAC_FRAME_PROF").as_deref() == Some("1"),
            frame_history: non_empty(env("OPENAC_FRAME_HISTORY")).map(PathBuf::from),
            no_block_compression: env("OPENAC_NO_BLOCK_COMPRESSION").as_deref() == Some("1"),
            orbit: OrbitOverrides::parse(
                env("OPENAC_ORBIT_DISTANCE_METERS").as_deref(),
                env("OPENAC_ORBIT_YAW_DEGREES").as_deref(),
                env("OPENAC_ORBIT_PITCH_DEGREES").as_deref(),
            ),
            display,
            reveal_radius: ac_render::reveal::parse_reveal_radius(
                env(ac_render::reveal::REVEAL_RADIUS_VAR).as_deref(),
            ),
            sky: ac_render::environment::SkyOptions::from_vars(&env),
            audio: ac_render::audio::launch_options(&env),
            quality: ac_render::quality::QualityOverrides::from_vars(&env),
            residency: ac_render::unowned_cache::ResidencyBudgets::from_vars(&env),
            ui_probe_script: non_empty(env("OPENAC_UI_PROBE_SCRIPT")).map(PathBuf::from),
            ui_probe_dump: env("OPENAC_UI_PROBE_DUMP").as_deref() == Some("1"),
            automation_artifact_dir: non_empty(env("OPENAC_AUTOMATION_ARTIFACT_DIR"))
                .map(PathBuf::from),
            motion_log: env(ac_runtime::session::diagnostics::MOTION_LOG_VAR).as_deref()
                == Some("1"),
        })
    }

    /// The host options: these settings over the defaults, `dat_dir` and
    /// `radius` (from flags or a session document) winning over the
    /// environment, diagnostics in `diagnostics_dir`, and the player's
    /// display settings (vsync, fullscreen, field of view).
    pub fn host_options(
        &self,
        dat_dir: Option<PathBuf>,
        radius: Option<u32>,
        diagnostics_dir: Option<PathBuf>,
        display: &DisplaySettings,
    ) -> HostOptions {
        let mut options = HostOptions::with_overrides(
            dat_dir.or_else(|| self.dat_dir.clone()),
            radius.or(self.landblock_radius),
        );
        options.graphics = GraphicsOptions {
            gpu_adapter: self.gpu_adapter.clone(),
            gpu_force_unsupported: self.gpu_force_unsupported.clone(),
            diagnostics_dir,
            vsync: display.vsync,
            uncapped: self.uncapped,
            frame_profiler: self.frame_profiler,
            frame_history: self.frame_history.clone(),
            facts: ac_render::facts::SharedFacts::default(),
            fullscreen: display.fullscreen,
            field_of_view_degrees: Some(display.field_of_view),
            key_bindings_file: None,
            keymap_directory: None,
            ac_dir: self.ac_dir.clone(),
            screenshots_directory: None,
            no_block_compression: self.no_block_compression,
            ui_probe_script: self.ui_probe_script.clone(),
            ui_probe_dump: self.ui_probe_dump,
            automation_artifact_dir: self.automation_artifact_dir.clone(),
            motion_log: self.motion_log,
        };
        // The radii and the other quality values set at startup override
        // the preset the display settings choose; the far radius is raised
        // to the near when smaller.
        options.quality = ac_render::quality::QualityOverrides {
            near_radius: options.quality.near_radius,
            far_radius: self.far_radius,
            ..self.quality
        };
        options.residency = self.residency;
        options.orbit = self.orbit;
        options.display = self.display;
        options.reveal_radius = self.reveal_radius;
        options.sky = self.sky;
        options.audio.launch = self.audio;
        options
    }
}

/// The player's client settings and where they are kept. The window host
/// owns them once it runs: it applies what an options window or `/vsync`
/// changes and saves it.
#[derive(Debug, Clone)]
pub struct DisplayPrefs {
    /// The display settings at startup (the first window's size).
    pub settings: DisplaySettings,
    /// Every client section at startup.
    pub player: PlayerSettings,
    /// The settings file; `None` when the configuration directory is
    /// unknown (nothing is saved).
    pub store: Option<SettingsStore>,
}

impl DisplayPrefs {
    /// Read the client settings from the settings file in `paths`'
    /// configuration directory (the defaults when there is none).
    pub fn load(paths: Option<&ac_host::PathSet>) -> Self {
        let store = paths.map(|p| SettingsStore::new(p.settings_file()));
        let player = match &store {
            Some(store) => {
                let (player, warnings) = store.load_player();
                for warning in warnings {
                    errln!("openac: {warning}");
                }
                player
            }
            None => PlayerSettings::default(),
        };
        Self {
            settings: player.display.clone(),
            player,
            store,
        }
    }

    /// Hand the settings and their file to the window host, and where the
    /// window's key bindings come from: `keybinds.json` beside the settings
    /// file, and retail `.keymap` profiles in the game's folder (the dat
    /// directory).
    pub fn apply_to(&self, options: &mut HostOptions) {
        options.settings = self.player.clone();
        options.settings_file = self.store.as_ref().map(|s| s.path().to_path_buf());
        options.graphics.key_bindings_file = self
            .store
            .as_ref()
            .and_then(|s| s.path().parent())
            .map(|dir| dir.join("keybinds.json"));
        options.graphics.keymap_directory = options.dat_dir.clone();
        options.graphics.screenshots_directory = self
            .store
            .as_ref()
            .and_then(|s| s.path().parent())
            .map(|dir| dir.join("screenshots"));
        options.audio.settings = ac_render::audio::audio_settings_of(&self.player.audio);
        options.audio.mixer = ac_render::audio::mixer_options_of(&self.player.mixer);
    }

    /// A window spec sized from the resolution setting (1280 x 720 when it
    /// does not read as `WxH`). It opens titled with the program and its
    /// version; the window then names the character its session plays.
    pub fn window_spec(&self) -> ac_render::WindowSpec {
        let (width, height) = self.settings.resolution_size().unwrap_or((1280, 720));
        ac_render::WindowSpec {
            title: ac_render::title::product(),
            width,
            height,
            clear_color: ac_render::Color {
                r: 0.25,
                g: 0.35,
                b: 0.5,
                a: 1.0,
            },
        }
    }
}

/// The console's window commands that do not involve a session: `/fly`
/// (the fly camera), `/daytime` (step the time of day, as Ctrl+F7),
/// `/dumpnearby` (as Ctrl+F3), `/weather` (step the weather, as Ctrl+F10),
/// `/mute` (as Ctrl+M), `/mixer` (the sound mixer, as in the chat box) and
/// `/vsync on|off` (the host saves it to the settings file), `/uitype <line>` (type into
/// the chat entry), `/uitext <text>` (type into the focused field). True
/// when `line` was one; `usage` says how to use one used wrongly.
pub fn window_command(
    host: &ac_render::HostHandle,
    line: &str,
    prefs: &mut DisplayPrefs,
    usage: &dyn Fn(&str),
) -> bool {
    // `/uitype <line>`: type a line into the window's chat entry through
    // the UI's own key routing (a diagnostic for unattended live checks).
    if let Some(typed) = line.strip_prefix("/uitype ") {
        let _ = host.type_into_ui(typed.to_owned());
        return true;
    }
    // `/uitext <text>`: the characters only, into the focused field.
    if let Some(typed) = line.strip_prefix("/uitext ") {
        let _ = host.type_text_into_ui(typed.to_owned());
        return true;
    }
    // `/mixer [arguments]`: the window's sound mixer, as typed in the chat box.
    if let Some(arguments) = ac_render::audio::mixer_command_arguments(line) {
        let _ = host.mixer_command(arguments.to_owned());
        return true;
    }
    if let Some(step) = ui_script(line) {
        match step {
            Ok(step) => {
                let _ = host.script_ui(step);
            }
            Err(text) => usage(&format!("usage: {text}")),
        }
        return true;
    }
    let mut words = line.split_whitespace();
    match (words.next(), words.next()) {
        (Some("/fly"), None) => {
            let _ = host.toggle_fly();
            true
        }
        (Some("/daytime"), None) => {
            let _ = host.cycle_time_of_day();
            true
        }
        (Some("/dumpnearby"), None) => {
            let _ = host.dump_nearby();
            true
        }
        (Some("/weather"), None) => {
            let _ = host.cycle_weather();
            true
        }
        (Some("/mute"), None) => {
            let _ = host.toggle_mute();
            true
        }
        (Some("/vsync"), Some(state @ ("on" | "off"))) => {
            prefs.settings.vsync = state == "on";
            let _ = host.set_vsync(prefs.settings.vsync);
            true
        }
        (Some("/vsync"), _) => {
            usage("usage: /vsync on|off");
            true
        }
        _ => false,
    }
}

/// The UI diagnostics for unattended live checks: `/uikey <key>` (press
/// and release, e.g. `f12`, `alt+1`, `0`), `/uiclick <x> <y> [right|double]`,
/// `/uipress <x> <y>`, `/uimove <x> <y>`, `/uirelease <x> <y>` (a left-button
/// drag, one step per line so the panels answer it frame by frame) and
/// `/uiprobe` (where the shown windows,
/// item cells, spell cells, stat rows and buttons are). `None` when `line`
/// is none of them; `Err` carries the usage.
fn ui_script(line: &str) -> Option<Result<UiScript, &'static str>> {
    let mut words = line.split_whitespace();
    let verb = words.next()?;
    let args: Vec<&str> = words.collect();
    let number = |s: &str| s.parse::<i32>().ok();
    Some(match verb {
        "/uikey" => match args.as_slice() {
            [key] => parse_key(key)
                .map(|(key, modifiers)| UiScript::Key { key, modifiers })
                .ok_or(
                    "/uikey <key> (f1..f12, 0..9, grave, enter, escape, tab; alt+, ctrl+, shift+)",
                ),
            _ => Err("/uikey <key>"),
        },
        "/uiclick" => {
            let usage = "/uiclick <x> <y> [right|double]";
            match args.as_slice() {
                [x, y, rest @ ..] if rest.len() <= 1 => match (number(x), number(y)) {
                    (Some(x), Some(y)) => match rest.first().copied() {
                        None => Ok(UiScript::Click {
                            x,
                            y,
                            button: 0,
                            double: false,
                        }),
                        Some("right") => Ok(UiScript::Click {
                            x,
                            y,
                            button: 1,
                            double: false,
                        }),
                        Some("double") => Ok(UiScript::Click {
                            x,
                            y,
                            button: 0,
                            double: true,
                        }),
                        Some(_) => Err(usage),
                    },
                    _ => Err(usage),
                },
                _ => Err(usage),
            }
        }
        "/uipress" | "/uimove" | "/uirelease" => match args.as_slice() {
            [x, y] => match (number(x), number(y)) {
                (Some(x), Some(y)) => Ok(match verb {
                    "/uipress" => UiScript::Press { x, y },
                    "/uimove" => UiScript::Move { x, y },
                    _ => UiScript::Release { x, y },
                }),
                _ => Err("/uipress | /uimove | /uirelease <x> <y>"),
            },
            _ => Err("/uipress | /uimove | /uirelease <x> <y>"),
        },
        "/worldmove" => match args.as_slice() {
            [x, y] => match (number(x), number(y)) {
                (Some(x), Some(y)) => Ok(UiScript::Pointer { x, y }),
                _ => Err("/worldmove <x> <y>"),
            },
            _ => Err("/worldmove <x> <y>"),
        },
        "/worldclick" => {
            let usage = "/worldclick <x> <y> [right|double]";
            match args.as_slice() {
                [x, y, rest @ ..] if rest.len() <= 1 => match (number(x), number(y)) {
                    (Some(x), Some(y)) => {
                        let (button, double) = match rest.first().copied() {
                            None => (0, false),
                            Some("right") => (1, false),
                            Some("double") => (0, true),
                            Some(_) => return Some(Err(usage)),
                        };
                        Ok(UiScript::PointerClick {
                            x,
                            y,
                            button,
                            double,
                        })
                    }
                    _ => Err(usage),
                },
                _ => Err(usage),
            }
        }
        "/uiprobe" if args.is_empty() => Ok(UiScript::Probe),
        "/uiprobe" => Err("/uiprobe"),
        _ => return None,
    })
}

/// Print one report of the window host.
pub fn print_event(event: &HostEvent) {
    match event {
        HostEvent::Attached(adapter) => {
            outln!(
                "window: attached on {:?} ({}); {}",
                adapter.backend,
                adapter.name,
                adapter.selection
            );
            for line in &adapter.candidates {
                outln!("window:   adapter {line}");
            }
            if let Some(report) = &adapter.report {
                outln!("window:   capability report {}", report.display());
            }
        }
        HostEvent::AttachFailed(error) => errln!("window: could not open: {error}"),
        HostEvent::RenderFailed(error) => errln!("window: closing after a GPU failure: {error}"),
        HostEvent::GpuError(error) => errln!("window: {error}"),
        HostEvent::FrameProfile(line) => outln!("{line}"),
        HostEvent::Notice(text) => outln!("window: {text}"),
        other => outln!("window: {other:?}"),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::HashMap;

    fn parse(vars: &[(&str, &str)]) -> Result<GraphicsEnv, OptionsError> {
        let map: HashMap<String, String> = vars
            .iter()
            .map(|(k, v)| ((*k).to_owned(), (*v).to_owned()))
            .collect();
        GraphicsEnv::parse(|k| map.get(k).cloned())
    }

    #[test]
    fn everything_is_off_by_default() {
        let env = parse(&[]).unwrap();
        // The display follows the platform; everything else is the default.
        assert_eq!(
            env,
            GraphicsEnv {
                display: env.display,
                ..GraphicsEnv::default()
            }
        );
        let options = env.host_options(None, None, None, &DisplaySettings::default());
        assert!(options.graphics.vsync);
        assert!(!options.graphics.uncapped && !options.graphics.frame_profiler);
    }

    #[test]
    fn diagnostics_switch_on_only_with_exactly_one() {
        let env = parse(&[
            ("OPENAC_UNCAPPED_RENDER", "1"),
            ("OPENAC_FRAME_PROF", "true"),
            ("OPENAC_FRAME_HISTORY", "logs/frames.csv"),
            ("OPENAC_GPU_ADAPTER", "radeon"),
            ("OPENAC_GPU_FORCE_UNSUPPORTED", " "),
            ("OPENAC_NO_BLOCK_COMPRESSION", "true"),
        ])
        .unwrap();
        assert!(!env.no_block_compression);
        assert!(env.uncapped);
        assert!(!env.frame_profiler);
        assert_eq!(env.frame_history, Some(PathBuf::from("logs/frames.csv")));
        assert_eq!(env.gpu_adapter.as_deref(), Some("radeon"));
        assert_eq!(env.gpu_force_unsupported, None);
    }

    #[test]
    fn the_ui_probe_and_automation_folder_come_from_the_environment() {
        let env = parse(&[
            ("OPENAC_UI_PROBE_SCRIPT", "logs/gate.txt"),
            ("OPENAC_UI_PROBE_DUMP", "1"),
            ("OPENAC_AUTOMATION_ARTIFACT_DIR", "logs/artifacts"),
        ])
        .unwrap();
        assert_eq!(env.ui_probe_script, Some(PathBuf::from("logs/gate.txt")));
        assert!(env.ui_probe_dump);
        assert_eq!(
            env.automation_artifact_dir,
            Some(PathBuf::from("logs/artifacts"))
        );
        let options = env.host_options(None, None, None, &DisplaySettings::default());
        assert_eq!(
            options.graphics.ui_probe_script,
            Some(PathBuf::from("logs/gate.txt"))
        );
        assert!(options.graphics.ui_probe_dump);
        let none = parse(&[("OPENAC_UI_PROBE_DUMP", "yes")]).unwrap();
        assert!(!none.ui_probe_dump);
        assert_eq!(none.ui_probe_script, None);
    }

    #[test]
    fn the_game_folder_comes_from_the_environment() {
        // OpenAC `RuntimeOptionsRetailUiTests.Parse_RetailUiIsDefaultOnAndReadsAcDir`
        // and `Parse_DefaultsRetailUiOnAndAcDirNull` (`ACDREAM_AC_DIR`, here
        // `OPENAC_AC_DIR`; empty is unset, as `NullIfEmpty`).
        let env = parse(&[("OPENAC_AC_DIR", r"C:\Turbine\Asheron's Call")]).unwrap();
        assert_eq!(
            env.ac_dir,
            Some(PathBuf::from(r"C:\Turbine\Asheron's Call"))
        );
        let options = env.host_options(None, None, None, &DisplaySettings::default());
        assert_eq!(
            options.graphics.ac_dir,
            Some(PathBuf::from(r"C:\Turbine\Asheron's Call"))
        );
        assert_eq!(parse(&[]).unwrap().ac_dir, None);
        assert_eq!(parse(&[("OPENAC_AC_DIR", "")]).unwrap().ac_dir, None);
    }

    #[test]
    fn the_motion_log_is_on_only_when_exactly_one() {
        let env = parse(&[("OPENAC_MOTION_LOG", "1")]).unwrap();
        assert!(env.motion_log);
        let options = env.host_options(None, None, None, &DisplaySettings::default());
        assert!(options.graphics.motion_log);
        assert!(!parse(&[("OPENAC_MOTION_LOG", "yes")]).unwrap().motion_log);
        assert!(!parse(&[]).unwrap().motion_log);
    }

    #[test]
    fn flags_win_over_the_environment() {
        let env = parse(&[
            ("OPENAC_DAT_DIR", "envdir"),
            ("OPENAC_LANDBLOCK_RADIUS", "3"),
        ])
        .unwrap();
        let options = env.host_options(
            Some(PathBuf::from("flagdir")),
            Some(1),
            None,
            &DisplaySettings {
                vsync: false,
                ..DisplaySettings::default()
            },
        );
        assert_eq!(options.dat_dir, Some(PathBuf::from("flagdir")));
        assert_eq!(options.quality.near_radius, Some(1));
        assert!(!options.graphics.vsync);
        let options = env.host_options(None, None, None, &DisplaySettings::default());
        assert_eq!(options.dat_dir, Some(PathBuf::from("envdir")));
        assert_eq!(options.quality.near_radius, Some(3));
        assert!(parse(&[("OPENAC_LANDBLOCK_RADIUS", "11")]).is_err());
    }

    #[test]
    fn the_radii_come_from_the_display_quality_unless_set_and_far_never_falls_below_near() {
        use ac_render::quality::{effective_display, resolve};
        let display = DisplaySettings::default();
        let defaults = parse(&[]).unwrap().host_options(None, None, None, &display);
        let q = resolve(&effective_display(&display), &defaults.quality);
        assert_eq!(q.radii(), (4, 8), "High at the default draw distance 8");
        let env = parse(&[
            ("OPENAC_FAR_RADIUS", "6"),
            ("OPENAC_LANDBLOCK_RADIUS", "8"),
            ("OPENAC_MSAA_SAMPLES", "2"),
        ])
        .unwrap();
        let options = env.host_options(None, None, None, &display);
        let q = resolve(&effective_display(&display), &options.quality);
        assert_eq!(q.radii(), (8, 8));
        assert_eq!(q.msaa_samples, 2);
        assert!(parse(&[("OPENAC_FAR_RADIUS", "26")]).is_err());
    }

    #[test]
    fn the_ui_diagnostics_parse_keys_clicks_drags_and_the_probe() {
        let (key, modifiers) = parse_key("alt+1").unwrap();
        assert_eq!(
            ui_script("/uikey alt+1"),
            Some(Ok(UiScript::Key { key, modifiers }))
        );
        assert_ne!(parse_key("1"), parse_key("alt+1"));
        assert!(matches!(
            ui_script("/uikey f12"),
            Some(Ok(UiScript::Key { .. }))
        ));
        assert_eq!(
            ui_script("/uiclick 10 20 double"),
            Some(Ok(UiScript::Click {
                x: 10,
                y: 20,
                button: 0,
                double: true
            }))
        );
        assert_eq!(
            ui_script("/uipress 1 2"),
            Some(Ok(UiScript::Press { x: 1, y: 2 }))
        );
        assert_eq!(
            ui_script("/worldclick 5 6 right"),
            Some(Ok(UiScript::PointerClick {
                x: 5,
                y: 6,
                button: 1,
                double: false
            }))
        );
        assert_eq!(
            ui_script("/worldmove 7 8"),
            Some(Ok(UiScript::Pointer { x: 7, y: 8 }))
        );
        assert!(matches!(ui_script("/worldclick 1"), Some(Err(_))));
        assert_eq!(
            ui_script("/uirelease 3 4"),
            Some(Ok(UiScript::Release { x: 3, y: 4 }))
        );
        assert_eq!(ui_script("/uiprobe"), Some(Ok(UiScript::Probe)));
        for bad in [
            "/uikey nope",
            "/uiclick 1",
            "/uimove 1",
            "/uiclick 1 2 left",
        ] {
            assert!(matches!(ui_script(bad), Some(Err(_))), "{bad}");
        }
        assert_eq!(ui_script("/fly"), None);
    }
}
