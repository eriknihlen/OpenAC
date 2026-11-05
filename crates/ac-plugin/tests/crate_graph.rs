//! Plugins are loaded by the session, never by the window , so headless and
//! graphical run plugins identically: the window's
//! crates do not depend on the plugin host, and the plugin host depends on
//! nothing of the window.

use std::path::Path;

fn manifest(krate: &str) -> String {
    let path = Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("..")
        .join(krate)
        .join("Cargo.toml");
    std::fs::read_to_string(&path).unwrap_or_else(|e| panic!("{}: {e}", path.display()))
}

/// The dependency names of a manifest's dependency tables (normal, build,
/// dev and target-specific).
fn dependencies(text: &str) -> Vec<String> {
    let mut names = Vec::new();
    let mut in_dependencies = false;
    for line in text.lines() {
        let line = line.trim();
        if line.starts_with('[') {
            in_dependencies = line.contains("dependencies");
            continue;
        }
        if in_dependencies
            && let Some((name, _)) = line.split_once(['=', '.'])
            && !line.starts_with('#')
        {
            names.push(name.trim().to_owned());
        }
    }
    names
}

#[test]
fn the_window_crates_do_not_reach_the_plugin_host() {
    for krate in ["ac-render", "ac-ui", "ac-audio"] {
        let names = dependencies(&manifest(krate));
        assert!(!names.is_empty(), "{krate}: no dependencies read");
        for plugin_crate in ["ac-plugin", "ac-plugin-abi", "wasmtime"] {
            assert!(
                !names.iter().any(|n| n == plugin_crate),
                "{krate} depends on {plugin_crate}"
            );
        }
    }
}

/// Plugin UI is data the session owns and a window draws (`ac-plugin-ui`):
/// the window reads it without reaching the plugin host, so it depends on
/// neither the host nor the window.
#[test]
fn the_plugin_ui_state_reaches_neither_the_host_nor_the_window() {
    let names = dependencies(&manifest("ac-plugin-ui"));
    assert!(names.iter().any(|n| n == "roxmltree"));
    for forbidden in [
        "ac-plugin",
        "ac-plugin-abi",
        "wasmtime",
        "ac-runtime",
        "ac-render",
        "ac-ui",
        "winit",
        "wgpu",
    ] {
        assert!(
            !names.iter().any(|n| n == forbidden),
            "ac-plugin-ui depends on {forbidden}"
        );
    }
}

#[test]
fn the_plugin_host_depends_on_nothing_of_the_window() {
    let names = dependencies(&manifest("ac-plugin"));
    assert!(names.iter().any(|n| n == "wasmtime"));
    for window_crate in [
        "ac-render",
        "ac-ui",
        "ac-audio",
        "winit",
        "wgpu",
        "ac-runtime",
    ] {
        assert!(
            !names.iter().any(|n| n == window_crate),
            "ac-plugin depends on {window_crate}"
        );
    }
}
