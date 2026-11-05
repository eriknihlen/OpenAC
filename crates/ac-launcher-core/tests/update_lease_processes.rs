//! The session barrier across processes: a second process holding the
//! update lease. Ported from OpenAC
//! `tests/AcDream.Launcher.Core.Tests/Updates/UpdateSessionBarrierTests`.
//!
//! These tests start child processes, so they live in a test binary of
//! their own. On Linux a child starts with a copy of every file its parent
//! has open, lock files included, until it runs its own program; a lock a
//! test has just released is still held through that copy, so another test
//! of the same binary taking it again at that moment is refused. The test
//! binaries whose tests take the launcher's locks start no process (checked
//! below).

mod common;

use std::path::Path;
use std::time::Duration;

use ac_launcher_core::updates::UpdateSessionBarrier;
use common::{TempDir, fixture_args, fixture_command, wait_for_file};

#[test]
fn fixture_hold_update_lease() {
    // A child process for the cross-process test below; does nothing in a
    // normal run.
    let Some(args) = fixture_args() else {
        return;
    };
    let (mode, data, ready, release) = (&args[0], &args[1], &args[2], &args[3]);
    let barrier = UpdateSessionBarrier::new(Path::new(data));
    let _session;
    let _exclusive;
    if mode == "session" {
        _session = barrier.acquire_session().unwrap();
    } else {
        _exclusive = barrier.acquire_exclusive().unwrap();
    }
    std::fs::write(ready, "ready").unwrap();
    let deadline = std::time::Instant::now() + Duration::from_secs(20);
    while !Path::new(release).exists() && std::time::Instant::now() < deadline {
        std::thread::sleep(Duration::from_millis(20));
    }
    std::process::exit(0);
}

#[test]
fn cross_process_lease_refuses_racing_launcher_and_releases_cleanly() {
    for mode in ["session", "exclusive"] {
        let root = TempDir::new("lease-cross");
        let ready = root.join(format!("{mode}.ready"));
        let release = root.join(format!("{mode}.release"));
        let data = root.path.to_string_lossy().into_owned();
        let mut holder = fixture_command(
            "fixture_hold_update_lease",
            &[
                mode,
                &data,
                &ready.to_string_lossy(),
                &release.to_string_lossy(),
            ],
        )
        .stdout(std::process::Stdio::null())
        .spawn()
        .unwrap();
        wait_for_file(&ready, &mut holder);
        let barrier = UpdateSessionBarrier::new(&root.path);
        assert!(barrier.acquire_exclusive().is_err(), "{mode}");
        if mode == "session" {
            let _peer = barrier.acquire_session().unwrap();
        } else {
            assert!(barrier.acquire_session().is_err());
        }
        std::fs::write(&release, "release").unwrap();
        assert!(holder.wait().unwrap().success());
        let _after = barrier.acquire_exclusive().unwrap();
    }
}

#[test]
fn the_test_binaries_that_take_launcher_locks_start_no_process() {
    // Spelled in pieces so this file does not match itself.
    let starts_a_process = [
        concat!("fixture_", "command("),
        concat!("Command", "::new("),
    ];
    let tests = Path::new(env!("CARGO_MANIFEST_DIR")).join("tests");
    for file in ["updates.rs", "plugins.rs"] {
        let source = std::fs::read_to_string(tests.join(file)).unwrap();
        // A process started only on Windows copies no lock file, and the
        // lock refusal this guards against is Linux's.
        let unix_source: String = source
            .split("#[cfg(windows)]")
            .enumerate()
            .map(|(index, part)| {
                if index == 0 {
                    part.to_owned()
                } else {
                    // Drop the item right after each Windows-only marker.
                    part.split_once("\n    }\n")
                        .map_or_else(String::new, |(_, rest)| rest.to_owned())
                }
            })
            .collect();
        for token in starts_a_process {
            assert!(
                !unix_source.contains(token),
                "tests/{file} starts a process ({token}); move that test to a binary of its own"
            );
        }
    }
}
