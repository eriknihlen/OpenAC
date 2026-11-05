//! The dev profile compiles this crate optimized because of an LLVM bug, not
//! because of anything in it. These tests keep that override honest: it must be there while
//! the toolchain has the bug, and this file says when it can go.

use std::path::{Path, PathBuf};
#[cfg(target_arch = "x86_64")]
use std::process::Command;

fn workspace_root() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("../..")
}

/// The `opt-level` value of a `[section]` in a TOML text, as written.
fn opt_level_in_section(text: &str, section: &str) -> Option<String> {
    let mut inside = false;
    for line in text.lines() {
        let line = line.trim();
        if line.starts_with('[') {
            inside = line == section;
            continue;
        }
        if inside
            && let Some(value) = line.strip_prefix("opt-level")
            && let Some(value) = value.trim_start().strip_prefix('=')
        {
            return Some(value.trim().trim_matches('"').to_owned());
        }
    }
    None
}

#[test]
fn the_dev_profile_compiles_the_plugin_host_optimized() {
    let path = workspace_root().join("Cargo.toml");
    let text = std::fs::read_to_string(&path).unwrap_or_else(|e| panic!("{}: {e}", path.display()));
    let level = opt_level_in_section(&text, "[profile.dev.package.ac-plugin]");
    assert!(
        matches!(level.as_deref(), Some("1" | "2" | "3" | "s" | "z")),
        "the workspace Cargo.toml must compile ac-plugin optimized in the dev profile (found \
         {level:?}): at opt-level 0 LLVM miscompiles wasmi's Caller::get_export and every plugin \
         call crashes"
    );
}

/// Compiles the LLVM reduction at opt-level 0, with the machine verifier when
/// `verify`. Returns whether rustc succeeded and its stderr.
#[cfg(target_arch = "x86_64")]
fn compile_reduction(verify: bool) -> (bool, String) {
    let root = workspace_root();
    let source = root.join("crates/ac-plugin/tests/fixtures/wasmi-opt0-crash/repro_llvm.rs");
    let output = std::env::temp_dir().join(format!(
        "openac-opt0-guard-{}-{}.o",
        std::process::id(),
        verify
    ));
    let rustc = std::env::var("RUSTC").unwrap_or_else(|_| "rustc".to_owned());
    let mut command = Command::new(rustc);
    // From the workspace root, rustup picks the toolchain the workspace pins.
    command
        .current_dir(&root)
        .args(["--edition", "2024", "--emit=obj", "-C", "opt-level=0"])
        .args(["-C", "debug-assertions=off"]);
    if verify {
        command.args(["-C", "llvm-args=-verify-machineinstrs"]);
    }
    let result = command
        .arg(&source)
        .arg("-o")
        .arg(&output)
        .output()
        .expect("rustc could not be started");
    let _ = std::fs::remove_file(&output);
    (
        result.status.success(),
        String::from_utf8_lossy(&result.stderr).into_owned(),
    )
}

#[cfg(target_arch = "x86_64")]
#[test]
fn the_toolchain_still_has_the_llvm_bug_the_override_avoids() {
    let (plain, stderr) = compile_reduction(false);
    assert!(plain, "the reduction does not compile: {stderr}");
    let (verified, stderr) = compile_reduction(true);
    assert!(
        !verified && stderr.contains("Bad machine code"),
        "LLVM no longer emits the bad load for tests/fixtures/wasmi-opt0-crash/\
         repro_llvm.rs: the toolchain is fixed. Remove [profile.dev.package.ac-plugin] from the \
         workspace Cargo.toml and this test, after running the plugin \
         tests in the dev profile.\n{stderr}"
    );
}
