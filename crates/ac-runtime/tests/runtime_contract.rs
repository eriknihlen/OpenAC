//! The runtime's contract with its hosts: one runtime owns the session's
//! state, a host without a window and a host with one drive it through the
//! same commands, and nothing in it depends on a window, a GPU or an audio
//! device (architecture invariants 1 and 3).
//!
//! Ported in spirit from OpenAC `tests/AcDream.Runtime.Tests/GameRuntimeContractTests.cs`
//! (`GameplayOwnersHaveNoStaticMutableSessionState`,
//! `GenerationAndTeardownAcknowledgementAreExact`) and
//! `Session/RuntimeLiveSessionNoWindowTests.cs` (no presentation assembly is
//! loaded by a runtime-only host): OpenAC checks by reflection over loaded
//! assemblies, here by reading the manifests and sources.

use std::collections::BTreeSet;
use std::path::{Path, PathBuf};

use ac_runtime::session::{Generation, Lifecycle, LifecycleState};

fn crates_dir() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .expect("crates/ac-runtime sits under crates/")
        .to_path_buf()
}

/// The names a manifest depends on outside its dev-dependencies.
fn dependencies(manifest: &str) -> Vec<String> {
    let mut names = Vec::new();
    let mut in_section = false;
    for line in manifest.lines() {
        let line = line.trim();
        if line.starts_with('[') {
            in_section = line == "[dependencies]"
                || (line.starts_with("[target.") && line.ends_with(".dependencies]"));
            continue;
        }
        if !in_section || line.is_empty() || line.starts_with('#') {
            continue;
        }
        let name: String = line
            .chars()
            .take_while(|c| c.is_ascii_alphanumeric() || *c == '-' || *c == '_')
            .collect();
        if !name.is_empty() {
            names.push(name);
        }
    }
    names
}

/// Every crate the runtime builds on, through the workspace's own crates.
fn dependency_closure(root: &str) -> BTreeSet<String> {
    let crates = crates_dir();
    let mut seen = BTreeSet::new();
    let mut queue = vec![root.to_owned()];
    while let Some(name) = queue.pop() {
        let manifest = crates.join(&name).join("Cargo.toml");
        let Ok(text) = std::fs::read_to_string(&manifest) else {
            continue;
        };
        for dependency in dependencies(&text) {
            if seen.insert(dependency.clone()) {
                queue.push(dependency);
            }
        }
    }
    seen
}

#[test]
fn the_runtime_builds_on_no_window_gpu_or_audio_crate() {
    let closure = dependency_closure("ac-runtime");
    assert!(
        closure.contains("ac-net") && closure.contains("ac-dat"),
        "the closure is being read: {closure:?}"
    );
    for presentation in [
        "ac-render",
        "ac-ui",
        "ac-audio",
        "openac",
        "winit",
        "wgpu",
        "egui",
        "cpal",
        "symphonia",
    ] {
        assert!(
            !closure.contains(presentation),
            "ac-runtime depends on {presentation}: {closure:?}"
        );
    }
}

fn rust_files(dir: &Path, out: &mut Vec<PathBuf>) {
    for entry in std::fs::read_dir(dir).into_iter().flatten().flatten() {
        let path = entry.path();
        if path.is_dir() {
            rust_files(&path, out);
        } else if path.extension().is_some_and(|e| e == "rs") {
            out.push(path);
        }
    }
}

/// The part of a source file that is not tests.
fn production_part(text: &str) -> &str {
    text.find("#[cfg(test)]").map_or(text, |at| &text[..at])
}

#[test]
fn the_runtime_owners_keep_no_static_mutable_state() {
    let mut files = Vec::new();
    rust_files(&crates_dir().join("ac-runtime").join("src"), &mut files);
    assert!(files.len() > 20, "the sources are being read");
    let mut found = Vec::new();
    for file in files {
        let name = file.file_name().unwrap().to_string_lossy().into_owned();
        if name == "tests.rs" || name.ends_with("_tests.rs") {
            continue;
        }
        let text = std::fs::read_to_string(&file).unwrap();
        for line in production_part(&text).lines() {
            let line = line.trim_start();
            let is_static = line.starts_with("static ") || line.starts_with("pub static ");
            let mutable = line.starts_with("static mut")
                || line.contains("thread_local!")
                || (is_static
                    && ["Mutex", "RwLock", "Cell<", "OnceLock", "LazyLock", "Atomic"]
                        .iter()
                        .any(|kind| line.contains(kind)));
            if mutable {
                found.push(format!("{}: {line}", file.display()));
            }
        }
    }
    assert!(found.is_empty(), "static mutable state: {found:#?}");
}

#[test]
fn every_host_drives_the_session_through_the_one_command_channel() {
    // OpenAC's hosts go through one command adapter
    // (`DirectGameRuntimeCommandAdapter`); here the window (ac-render) and
    // the console hosts (openac) hold the same `Sender<SessionCommand>` the
    // session hands out, and nothing else reaches into a session.
    for (krate, file) in [("ac-render", "host.rs"), ("openac", "main.rs")] {
        let text = std::fs::read_to_string(crates_dir().join(krate).join("src").join(file))
            .unwrap_or_default();
        assert!(
            text.contains("Sender<SessionCommand>"),
            "{krate} {file} drives the session through its command channel"
        );
        assert!(
            !text.contains("SessionState"),
            "{krate} {file} must not reach the session's owners directly"
        );
    }
}

#[test]
fn generations_are_exact_and_monotonic() {
    // GenerationAndTeardownAcknowledgementAreExact: the first generation
    // after "never started" is 1, each next is one more.
    let first = Generation::default().next();
    assert_eq!(first, Generation(1));
    assert_eq!(first.next(), Generation(2));
    assert!(first.next() > first);
    let stopped = Lifecycle::stopped(first);
    assert_eq!(stopped.state, LifecycleState::Stopped);
    assert_eq!(stopped.player_guid, 0);
    assert!(!stopped.has_transport);
}
