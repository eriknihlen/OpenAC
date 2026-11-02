//! `openac-launcher`: the launcher window (OpenAC's `acdream-launcher`).
//! Accounts and servers, plugins, settings, first-run setup and updates,
//! and starting `openac` sessions. The logic is `ac-launcher-core`'s; this
//! crate composes it with the operating system's network and draws it with
//! egui.
//!
//! On Windows it is a windowed program with no console of its own, as
//! OpenAC's launcher is: started from Explorer no console window opens, and
//! the sessions it starts write their standard output to the hidden console
//! the launcher gives them, not into a console the launcher shares with
//! them. Started with its output redirected, the launcher and its sessions
//! write there, as OpenAC's do.
#![cfg_attr(windows, windows_subsystem = "windows")]

mod app;
mod mirror;
mod png;
mod script;
mod shell;
mod startup;
mod surface_watch;
mod theme;
mod ui;

use std::path::{Path, PathBuf};
use std::process::ExitCode;
use std::sync::Arc;
use std::time::SystemTime;

use ac_host::{PathOverrides, PathSet, RootSource, SystemPlatform};
use ac_launcher_core::control;
use ac_launcher_core::launcher_log::LauncherLog;
use ac_launcher_core::layout_gate::{self, LayoutGate, LayoutGateState};
use ac_launcher_core::net::HttpTransport;
use ac_launcher_core::updates::{
    ClientVersionStore, LauncherInstallationLayout, LauncherSelfUpdateManager, detect_rid,
    self_update_bootstrap,
};
use ac_launcher_core::window::Orchestration;
use ac_launcher_core::window::startup::{LauncherStartupMode, LauncherStartupOptions};
use ac_launcher_net::OsHttpTransport;

/// The exit code of a startup that failed (OpenAC's 74, `EX_IOERR`).
const STARTUP_FAILED: u8 = 74;

fn main() -> ExitCode {
    let arguments: Vec<String> = std::env::args().skip(1).collect();
    match run(&arguments) {
        Ok(code) => code,
        Err(error) => {
            eprintln!("Launcher startup failed safely: {error}");
            match write_crash_report(&arguments, &error) {
                Some(path) => eprintln!("Crash report: {}", path.display()),
                None => eprintln!("No crash report could be written."),
            }
            ExitCode::from(STARTUP_FAILED)
        }
    }
}

fn default_paths() -> Result<PathSet, String> {
    PathSet::resolve(&PathOverrides::default(), &SystemPlatform).map_err(|error| error.to_string())
}

fn run(arguments: &[String]) -> Result<ExitCode, String> {
    let options = LauncherStartupOptions::parse(arguments, default_paths)
        .map_err(|error| error.to_string())?;
    if options.mode == LauncherStartupMode::VerifyPublish {
        // A display-free execution probe for the packaged program.
        return Ok(ExitCode::SUCCESS);
    }
    let paths = options.paths.clone();
    let rid = detect_rid().ok_or("This platform has no runtime identifier.")?;
    let transport: Arc<dyn HttpTransport> = mirror::GitHubMirror::wrap_from_environment(Arc::new(
        OsHttpTransport::new().map_err(|error| error.to_string())?,
    ));
    let executable = std::env::current_exe().map_err(|error| error.to_string())?;
    let launcher_directory = executable
        .parent()
        .map(Path::to_path_buf)
        .ok_or("The launcher's folder is unknown.")?;

    // Before anything else, as OpenAC's start: finish or recover what a previous self-update
    // left, or hand a staged one to its helper and exit; the helper and confirmation modes
    // end here too.
    let self_updates = Arc::new(LauncherSelfUpdateManager::new(
        &paths,
        Arc::clone(&transport),
    ));
    let layout = LauncherInstallationLayout::detect(&launcher_directory, &rid)
        .map_err(|error| error.to_string())?;
    let startup =
        self_update_bootstrap::handle_for_layout(arguments, &self_updates, &layout, &executable)
            .map_err(|error| error.to_string())?;
    if startup.should_exit {
        return Ok(ExitCode::from(
            u8::try_from(startup.exit_code).unwrap_or(STARTUP_FAILED),
        ));
    }
    if startup.remaining_arguments != options.public_arguments {
        return Err("The self-update bootstrap changed validated launcher arguments.".into());
    }

    // The install moves into the current folder layout before anything reads
    // its folders; while clients of the install run, the window waits for
    // them, and a move that fails starts nothing.
    let log = LauncherLog::open(&paths);
    log.line(&format!(
        "launcher {} started; install folder {}",
        env!("CARGO_PKG_VERSION"),
        paths.root_directory().display()
    ));
    let gate = LayoutGate::begin(&paths, &log, Arc::new(layout_gate::running_clients));
    let context = startup::StartContext {
        paths,
        options,
        rid,
        transport,
        executable,
        launcher_directory,
        self_updates,
        layout,
        log: log.clone(),
    };
    // Ready at once (the usual start): a part that cannot be made is a
    // startup failure, before any window opens.
    let prepared = if gate.state() == LayoutGateState::Ready {
        Some(startup::prepare(&context)?)
    } else {
        None
    };

    shell::run(
        shell::ShellOptions {
            title: "OpenAC".into(),
            size: (1120.0, 740.0),
            min_size: (760.0, 460.0),
            icon: window_icon(),
            log: Some(log),
        },
        move |_ctx, waker| startup::StartupApp::new(gate, context, prepared, waker),
    )
    .map_err(|error| error.to_string())?;
    Ok(ExitCode::SUCCESS)
}

/// The launcher's own image (OpenAC's 256-pixel launcher icon), in the
/// executable so the window never depends on a file beside it.
const WINDOW_ICON_PNG: &[u8] = include_bytes!("../../../assets/icons/launcher-256.png");

/// The window's title bar and task bar image, decoded by the client's own
/// PNG reader; none if it does not decode.
fn window_icon() -> Option<shell::WindowIcon> {
    let image = ac_launcher_core::window::icon::decode_png(WINDOW_ICON_PNG)?;
    Some(shell::WindowIcon {
        width: image.width,
        height: image.height,
        rgba: image.rgba.as_ref().clone(),
    })
}

/// Writes `launcher-crash-<time>.log` in the install's crash folder: the
/// one the command line names, else the root `--root-dir` or `--data-dir`
/// names, else the resolved one, else the default root's when resolving is
/// what failed (an unreadable pointer file, say).
/// Opens the control endpoint `name` over the launcher's orchestrator; a
/// name another process holds is a startup failure, as OpenAC's.
pub(crate) fn start_control(
    name: &str,
    launcher: &Arc<dyn Orchestration>,
    versions: &Arc<ClientVersionStore>,
    rid: &str,
    log: &LauncherLog,
) -> Result<control::ControlServer, String> {
    let log = log.clone();
    let handler = Arc::new(control::LauncherControlHandler::new(
        Arc::clone(launcher),
        Some(control::reload_client(Arc::clone(versions), rid.to_owned())),
        Some(control::client_version(Arc::clone(versions))),
    ));
    control::ControlServer::start(
        name,
        Arc::new(move |request, cancel| handler.handle(request, cancel)),
        Arc::new(move |message| log.say(message)),
        control::DEFAULT_REQUEST_TIMEOUT,
    )
    .map_err(|error| format!("The control pipe '{name}' could not be opened: {error}"))
}

fn write_crash_report(arguments: &[String], failure: &str) -> Option<PathBuf> {
    // The root named by `--root-dir` or, failing that, `--data-dir`, when it
    // is absolute.
    let requested_root = |arguments: &[String]| {
        ["--root-dir", "--data-dir"].iter().find_map(|option| {
            arguments
                .windows(2)
                .find(|pair| {
                    pair[0] == *option
                        && !pair[1].trim().is_empty()
                        && Path::new(&pair[1]).is_absolute()
                })
                .map(|pair| PathBuf::from(&pair[1]))
        })
    };
    let directory = match LauncherStartupOptions::parse(arguments, default_paths) {
        Ok(options) => options.paths.crash_reports_directory(),
        Err(_) => match requested_root(arguments) {
            Some(root) => PathSet::for_root(&root, RootSource::Explicit).crash_reports_directory(),
            None => match default_paths() {
                Ok(paths) => paths.crash_reports_directory(),
                Err(_) => PathSet::for_root(
                    &PathSet::default_root(&SystemPlatform).ok()?,
                    RootSource::Default,
                )
                .crash_reports_directory(),
            },
        },
    };
    std::fs::create_dir_all(&directory).ok()?;
    let now = SystemTime::now();
    let stamp = ac_launcher_core::time::format_utc(now)
        .chars()
        .filter(char::is_ascii_digit)
        .take(17)
        .collect::<String>();
    let path = directory.join(format!("launcher-crash-{stamp}.log"));
    let text = format!(
        "OpenAC launcher crash report\nutc: {}\nos: {}\nrid: {}\nversion: {}\n\n{failure}\n",
        ac_launcher_core::time::format_utc(now),
        std::env::consts::OS,
        detect_rid().unwrap_or_default(),
        env!("CARGO_PKG_VERSION"),
    );
    std::fs::write(&path, text).ok()?;
    Some(path)
}

#[cfg(test)]
mod icon_tests {
    // OpenAC `src/AcDream.Launcher/MainWindow.axaml` (`Icon`) with
    // `AcDream.Launcher.csproj` linking `assets/icons/acdream-launcher-256.png`
    // as the window icon: the same 256 x 256 image, decoded.
    #[test]
    fn the_window_icon_is_the_256_pixel_launcher_image() {
        let icon = super::window_icon().expect("the icon decodes");
        assert_eq!((icon.width, icon.height), (256, 256));
        assert_eq!(icon.rgba.len(), 256 * 256 * 4);
        assert!(icon.rgba.chunks_exact(4).any(|p| p[3] == 255));
    }
}

#[cfg(test)]
#[expect(
    dead_code,
    reason = "the build script's linking half is not called by the test"
)]
mod executable_icon_tests {
    include!("../windows_icon.rs");

    // OpenAC `AcDream.Launcher.csproj` `ApplicationIcon`
    // (`acdream-launcher.ico`, 7 images): the compiled resource file holds
    // the empty first resource, one icon resource per image with that
    // image's bytes, and the group directory numbering them 1..=7.
    #[test]
    fn the_icon_file_becomes_one_resource_per_image_and_a_group() {
        let ico = std::fs::read(concat!(
            env!("CARGO_MANIFEST_DIR"),
            "/../../assets/icons/launcher.ico"
        ))
        .unwrap();
        let res = icon_resource(&ico).unwrap();
        assert_eq!(&res[..8], &[0, 0, 0, 0, 32, 0, 0, 0]);
        let mut at = 32;
        let mut kinds = Vec::new();
        while at < res.len() {
            let size = u32_at(&res, at).unwrap() as usize;
            kinds.push((
                u16_at(&res, at + 10).unwrap(),
                u16_at(&res, at + 14).unwrap(),
            ));
            at += (32 + size).div_ceil(4) * 4;
        }
        assert_eq!(at, res.len());
        let mut expected: Vec<(u16, u16)> = (1..=7).map(|id| (3, id)).collect();
        expected.push((14, 1));
        assert_eq!(kinds, expected);
        // The first image's bytes, as the icon file has them.
        let size = u32_at(&ico, 6 + 8).unwrap() as usize;
        let offset = u32_at(&ico, 6 + 12).unwrap() as usize;
        assert_eq!(&res[64..64 + size], &ico[offset..offset + size]);
        assert!(icon_resource(b"not an icon").is_err());
    }
}
