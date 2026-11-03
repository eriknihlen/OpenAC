//! The test plugins (`hello`, `probe`, `misbehave`, `uidemo`, `fetcher`, `parity`, `rawbytes`, `suite`), built to wasm for
//! tests and installed into a plugin root. Tests only (feature
//! `test-fixtures`).
//!
//! The plugins live in `crates/ac-plugin/tests/fixtures/plugins/`, their own
//! cargo workspace; they are built with the toolchain the tests run under,
//! for `wasm32-unknown-unknown`, into `target/plugin-fixtures/`.

use std::path::{Path, PathBuf};
use std::process::Command;
use std::sync::OnceLock;

/// The fixture workspace.
#[must_use]
pub fn source_directory() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("tests/fixtures/plugins")
}

/// Builds the test plugins once per test process and returns the folder
/// holding the `.wasm` files.
///
/// # Panics
/// When the build fails (the message has cargo's output): a test cannot run
/// without its plugins.
#[must_use]
pub fn wasm_directory() -> PathBuf {
    static BUILT: OnceLock<Result<PathBuf, String>> = OnceLock::new();
    BUILT
        .get_or_init(build)
        .clone()
        .unwrap_or_else(|error| panic!("the test plugins did not build: {error}"))
}

fn build() -> Result<PathBuf, String> {
    let source = source_directory();
    let target = Path::new(env!("CARGO_MANIFEST_DIR")).join("../../target/plugin-fixtures");
    let cargo = std::env::var("CARGO").unwrap_or_else(|_| "cargo".to_owned());
    let output = Command::new(cargo)
        .current_dir(&source)
        .args([
            "build",
            "--release",
            "--target",
            "wasm32-unknown-unknown",
            "--target-dir",
        ])
        .arg(&target)
        // The outer build's flags are for the host target, not wasm.
        .env_remove("RUSTFLAGS")
        .env_remove("CARGO_ENCODED_RUSTFLAGS")
        .output()
        .map_err(|error| format!("cargo could not be started: {error}"))?;
    if !output.status.success() {
        return Err(format!(
            "{}\n(is the wasm32-unknown-unknown target installed? rust-toolchain.toml lists \
             it)",
            String::from_utf8_lossy(&output.stderr)
        ));
    }
    Ok(target.join("wasm32-unknown-unknown/release"))
}

/// Installs the test plugin `name` (`hello`, `probe`, `misbehave`, `uidemo`, `fetcher`, `parity`, `rawbytes`, `suite`) into the
/// plugin root `root`: `<root>/<name>/plugin.json` and `<name>.wasm`.
/// Returns the plugin's folder.
///
/// # Panics
/// When the files cannot be copied.
pub fn install(root: &Path, name: &str) -> PathBuf {
    let folder = root.join(name);
    std::fs::create_dir_all(&folder).expect("plugin folder");
    std::fs::copy(
        source_directory().join(name).join("plugin.json"),
        folder.join("plugin.json"),
    )
    .expect("plugin.json");
    std::fs::copy(
        wasm_directory().join(format!("{name}.wasm")),
        folder.join(format!("{name}.wasm")),
    )
    .expect("plugin wasm");
    folder
}

/// A fresh, empty plugin root under the temp directory for one test.
///
/// # Panics
/// When the folder cannot be made.
#[must_use]
pub fn fresh_root(tag: &str) -> PathBuf {
    let root =
        std::env::temp_dir().join(format!("openac-plugin-root-{tag}-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&root);
    std::fs::create_dir_all(&root).expect("plugin root");
    root
}

/// `wasm` with its custom section `name` given the contents `data`, or
/// left out when `data` is `None`: a plugin as another SDK would have
/// built it. Every other section is kept as it is.
///
/// # Panics
/// When `wasm` is not a well-formed module or has no section `name`.
#[must_use]
pub fn with_custom_section(wasm: &[u8], name: &str, data: Option<&[u8]>) -> Vec<u8> {
    fn leb(bytes: &[u8], at: &mut usize) -> usize {
        let (mut value, mut shift) = (0usize, 0);
        loop {
            let byte = bytes[*at];
            *at += 1;
            value |= usize::from(byte & 0x7F) << shift;
            if byte & 0x80 == 0 {
                return value;
            }
            shift += 7;
        }
    }
    fn put_leb(out: &mut Vec<u8>, mut value: usize) {
        loop {
            let byte = (value & 0x7F) as u8;
            value >>= 7;
            if value == 0 {
                out.push(byte);
                return;
            }
            out.push(byte | 0x80);
        }
    }
    assert_eq!(&wasm[..4], b"\0asm", "not a wasm module");
    let mut out = wasm[..8].to_vec();
    let mut at = 8;
    let mut found = false;
    while at < wasm.len() {
        let id = wasm[at];
        at += 1;
        let size = leb(wasm, &mut at);
        let payload = &wasm[at..at + size];
        at += size;
        if id == 0 {
            let mut inner = 0;
            let length = leb(payload, &mut inner);
            if &payload[inner..inner + length] == name.as_bytes() {
                found = true;
                if let Some(data) = data {
                    let mut body = payload[..inner + length].to_vec();
                    body.extend_from_slice(data);
                    out.push(0);
                    put_leb(&mut out, body.len());
                    out.extend(body);
                }
                continue;
            }
        }
        out.push(id);
        put_leb(&mut out, size);
        out.extend_from_slice(payload);
    }
    assert!(found, "the module has no {name} section");
    out
}
