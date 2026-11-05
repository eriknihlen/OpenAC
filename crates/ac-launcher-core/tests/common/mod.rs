//! Helpers shared by the integration tests.
#![allow(dead_code)]

use std::path::{Path, PathBuf};

use ac_host_core::PathSet;
use ac_launcher_core::ids::unique_hex32;

/// A directory removed with its contents when dropped.
pub struct TempDir {
    pub path: PathBuf,
}

impl TempDir {
    /// A fresh, existing directory under the temp directory.
    pub fn new(prefix: &str) -> Self {
        let path = std::env::temp_dir()
            .join("openac-launcher-tests")
            .join(format!("{prefix}-{}", unique_hex32()));
        std::fs::create_dir_all(&path).expect("create temp dir");
        Self { path }
    }

    /// A path inside the directory.
    pub fn join(&self, relative: impl AsRef<Path>) -> PathBuf {
        self.path.join(relative)
    }

    /// Config, data and cache directories inside this one.
    pub fn path_set(&self) -> PathSet {
        PathSet::from_members(self.join("config"), self.join("data"), self.join("cache"))
    }
}

impl Drop for TempDir {
    fn drop(&mut self) {
        ac_launcher_core::fsutil::try_delete_tree(&self.path);
    }
}

pub mod fixtures;
pub mod png;

/// The environment variable that turns a fixture test into a child process.
pub const FIXTURE_ENV: &str = "OPENAC_LAUNCHER_TEST_FIXTURE";

/// The fixture arguments when this test binary was started as a child.
pub fn fixture_args() -> Option<Vec<String>> {
    std::env::var(FIXTURE_ENV)
        .ok()
        .map(|joined| joined.split('\u{1f}').map(str::to_owned).collect())
}

/// A command that re-runs this test binary as a child, running only the
/// test `test_name` (a fixture that acts on `args`, then exits).
pub fn fixture_command(test_name: &str, args: &[&str]) -> std::process::Command {
    let exe = std::env::current_exe().expect("test binary path");
    let mut command = std::process::Command::new(exe);
    command
        .args([test_name, "--exact", "--nocapture", "--test-threads", "1"])
        .env(FIXTURE_ENV, args.join("\u{1f}"));
    command
}

/// Waits up to ten seconds for a file to appear.
pub fn wait_for_file(path: &Path, child: &mut std::process::Child) {
    let deadline = std::time::Instant::now() + std::time::Duration::from_secs(10);
    while !path.exists() {
        if let Ok(Some(status)) = child.try_wait() {
            panic!("fixture exited early with {status}");
        }
        assert!(
            std::time::Instant::now() < deadline,
            "fixture did not become ready"
        );
        std::thread::sleep(std::time::Duration::from_millis(20));
    }
}

/// Owned strings from string literals.
pub fn strings(items: &[&str]) -> Vec<String> {
    items.iter().map(|item| (*item).to_owned()).collect()
}
