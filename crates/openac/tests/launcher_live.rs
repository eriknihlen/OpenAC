//! End to end: the launcher core's orchestrator starts this `openac` binary
//! for a session against the local ACE server (`127.0.0.1:9000`), follows its
//! status file until the character is in the world, stops it the way the
//! launcher's Stop button does (console stop, then a kill after 30 s), and
//! checks that the client logged off and exited cleanly.
//!
//! Ignored by default; with `--include-ignored` it still does nothing unless
//! `OPENAC_LIVE_TESTS=1`. It logs in `testaccount2` as `+Horan` with the
//! password from `OPENAC_TEST_PASSWORD2` (never from a file in the
//! repository). One live session per account: make sure nothing else is
//! logged in on `testaccount2` first.
//!
//! `OPENAC_LIVE_WINDOW=1` makes it start the windowed form
//! (`openac --session-config`) instead of the headless one; a window opens
//! on this desktop for the length of the test.

use std::path::PathBuf;
use std::time::{Duration, Instant};

use ac_host::PathSet;
use ac_launcher_core::cancel::CancelToken;
use ac_launcher_core::installation::LauncherInstallRecord;
use ac_launcher_core::orchestration::{
    LauncherActivityState, LauncherExecutableSet, LauncherOrchestrator,
};
use ac_launcher_core::profiles::{LaunchMode, LauncherProfileStore};

const SERVER: &str = "Local ACE";
const ACCOUNT: &str = "testaccount2";
const CHARACTER: &str = "+Horan";

fn dat_directory() -> PathBuf {
    if let Ok(dir) = std::env::var("OPENAC_DAT_DIR")
        && !dir.trim().is_empty()
    {
        return PathBuf::from(dir);
    }
    let home = std::env::var("USERPROFILE")
        .or_else(|_| std::env::var("HOME"))
        .unwrap_or_default();
    PathBuf::from(home).join("Documents").join("Asheron's Call")
}

struct TempRoot(PathBuf);

impl Drop for TempRoot {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}

fn wait_for(
    orchestrator: &LauncherOrchestrator,
    session_id: &str,
    limit: Duration,
    done: impl Fn(LauncherActivityState) -> bool,
) -> ac_launcher_core::orchestration::LauncherSessionSnapshot {
    let started = Instant::now();
    loop {
        orchestrator.poll_status().unwrap();
        let snapshot = orchestrator.snapshot().unwrap();
        let session = snapshot
            .sessions
            .into_iter()
            .find(|session| session.session_id == session_id)
            .expect("the launched session");
        if done(session.state) || started.elapsed() > limit {
            return session;
        }
        std::thread::sleep(Duration::from_millis(250));
    }
}

#[test]
#[ignore = "local ACE server; needs OPENAC_LIVE_TESTS=1 and OPENAC_TEST_PASSWORD2"]
fn the_launcher_starts_a_session_into_the_world_and_stops_it_gracefully() {
    if std::env::var("OPENAC_LIVE_TESTS").as_deref() != Ok("1") {
        eprintln!("skipped: set OPENAC_LIVE_TESTS=1 to run live tests");
        return;
    }
    let password = std::env::var("OPENAC_TEST_PASSWORD2")
        .expect("OPENAC_TEST_PASSWORD2 holds testaccount2's password");
    let windowed = std::env::var("OPENAC_LIVE_WINDOW").as_deref() == Ok("1");
    let mode = if windowed {
        LaunchMode::Gui
    } else {
        LaunchMode::Headless
    };

    let root = TempRoot(std::env::temp_dir().join(format!(
        "openac-launcher-live-{}",
        ac_launcher_core::ids::unique_hex32()
    )));
    let paths = PathSet::from_members(
        root.0.join("config"),
        root.0.join("data"),
        root.0.join("cache"),
    );
    let mut store = LauncherProfileStore::for_paths(&paths);
    store.load().unwrap();
    store.add_server(SERVER, "127.0.0.1", 9000).unwrap();
    store.add_account(SERVER, ACCOUNT, &password).unwrap();
    store
        .add_character(SERVER, ACCOUNT, CHARACTER, None, mode, None)
        .unwrap();
    store.save().unwrap();

    let openac = PathBuf::from(env!("CARGO_BIN_EXE_openac"));
    let executables = LauncherExecutableSet::new(&openac, &openac, None);
    // The client reads the retail dats; the prepared package is required by
    // the record's format but not read by this client.
    let install = LauncherInstallRecord::new(
        dat_directory(),
        root.0.join("data").join("pak").join("acdream.pak"),
        "",
        0,
        0,
    );
    let orchestrator = LauncherOrchestrator::new(store, paths, executables, Some(install));
    orchestrator.load_profiles().unwrap();

    let launched = orchestrator
        .launch(SERVER, ACCOUNT, Some(CHARACTER), mode, &CancelToken::new())
        .unwrap();
    let id = launched.session_id.clone();
    println!("launched {id} ({mode:?})");

    let in_world = wait_for(&orchestrator, &id, Duration::from_secs(120), |state| {
        state == LauncherActivityState::InWorld || !state.is_active()
    });
    println!("{:?}: {}", in_world.state, in_world.status);
    if in_world.state != LauncherActivityState::InWorld {
        // Never leave a client we started behind.
        let _ = orchestrator.stop_session(&id, Duration::from_secs(30), &CancelToken::new());
        let log = orchestrator
            .session_log_path(&id)
            .and_then(|path| std::fs::read_to_string(path).ok())
            .unwrap_or_default();
        panic!("the session did not reach the world: {in_world:?}\nstandard error:\n{log}");
    }
    assert_eq!(in_world.character_name.as_deref(), Some(CHARACTER));

    // A few seconds in the world, then the launcher's Stop.
    std::thread::sleep(Duration::from_secs(5));
    let stopping = Instant::now();
    orchestrator
        .stop_session(&id, Duration::from_secs(30), &CancelToken::new())
        .unwrap();
    let ended = wait_for(&orchestrator, &id, Duration::from_secs(45), |state| {
        !state.is_active()
    });
    println!(
        "{:?} after {:.1} s: code {:?}, reason {:?}, graceful {}",
        ended.state,
        stopping.elapsed().as_secs_f64(),
        ended.exit_code,
        ended.exit_reason,
        ended.exited_gracefully
    );
    assert_eq!(ended.state, LauncherActivityState::Exited, "{ended:?}");
    assert_eq!(ended.exit_code, Some(0), "{ended:?}");
    assert!(ended.exited_gracefully, "{ended:?}");
    // Logged off, not killed: the 30 s kill never came.
    assert!(stopping.elapsed() < Duration::from_secs(30));
    orchestrator.dispose();
}
