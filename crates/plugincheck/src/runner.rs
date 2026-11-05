//! The checks. Every install rule is the launcher's own code
//! (`ac_launcher_core::plugins`), the same calls a direct install and the
//! installer make, so a rule change there changes this verdict too; the
//! module check is the session's own (`ac_plugin::engine`).

use std::path::{Path, PathBuf};

use ac_launcher_core::cancel::CancelToken;
use ac_launcher_core::plugins::{
    ICON_EXTENT, ICON_FILE_NAME, ICON_MAXIMUM_BYTES, InstallRules, PluginManifest,
    PluginSha256File, contract_limits, direct_install_refusal, entry_path, validate_icon,
    validate_plugin_content,
};
use ac_launcher_core::updates::{ExtractedFileRecord, SafeZipExtractor};
use ac_launcher_core::{fsutil, ids, integrity};
use ac_plugin::PluginEngine;
use ac_plugin::engine::{LAYOUT_REFUSAL, check_api_version, check_module};

use crate::report::{CheckItem, CheckMode, CheckReport};

/// Nothing to report: the path is missing, or is neither a folder nor a
/// `.zip` (exit code 2).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct UsageError(pub String);

impl std::fmt::Display for UsageError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.0)
    }
}

impl std::error::Error for UsageError {}

/// Checks the plugin folder or `.zip` at `path`.
///
/// # Errors
/// The path does not exist, or is a file that is not a `.zip`.
pub fn run_check(path: &Path) -> Result<CheckReport, UsageError> {
    let full = std::path::absolute(path).unwrap_or_else(|_| path.to_path_buf());
    if full.is_dir() {
        return Ok(run_directory(&full));
    }
    if full.is_file() {
        let is_zip = full
            .extension()
            .and_then(|e| e.to_str())
            .is_some_and(|e| e.eq_ignore_ascii_case("zip"));
        if !is_zip {
            return Err(UsageError(format!(
                "'{}' is a file but not a .zip. Pass an unzipped plugin folder or a plugin .zip.",
                path.display()
            )));
        }
        return Ok(run_zip(&full));
    }
    Err(UsageError(format!("'{}' does not exist.", path.display())))
}

fn display(path: &Path) -> String {
    path.display().to_string()
}

fn run_directory(directory: &Path) -> CheckReport {
    let mut checks = Vec::new();
    let manifest_path = directory.join("plugin.json");
    if !manifest_path.is_file() {
        checks.push(CheckItem::fail(
            "plugin.json present",
            "No 'plugin.json' was found at the root of the plugin folder.",
            "Add a plugin.json at the folder's root.",
        ));
        return CheckReport::finish(CheckMode::Directory, display(directory), checks);
    }
    checks.push(CheckItem::pass(
        "plugin.json present",
        "Found plugin.json at the folder root.",
    ));
    let Some(manifest) = parse_manifest(&manifest_path, &mut checks) else {
        return CheckReport::finish(CheckMode::Directory, display(directory), checks);
    };
    add_install_rules(&manifest, &mut checks);
    match direct_install_refusal(directory, &manifest) {
        None => checks.push(CheckItem::pass(
            "folder passes the direct-install checks",
            "Content, entry module, icon, and path/link safety all check out.",
        )),
        Some(refusal) => checks.push(CheckItem::fail(
            "folder passes the direct-install checks",
            &refusal,
            "This is the exact refusal a hand-unzipped install would show; fix it and re-run.",
        )),
    }
    checks.push(check_wasm_module(directory, &manifest));
    CheckReport::finish(CheckMode::Directory, display(directory), checks)
}

fn run_zip(zip_path: &Path) -> CheckReport {
    let mut checks = Vec::new();
    let length = std::fs::metadata(zip_path).map_or(0, |m| m.len());
    if length > contract_limits::MAXIMUM_ZIP_BYTES {
        checks.push(CheckItem::fail(
            "zip within size cap",
            &format!(
                "The archive is {length} bytes, over the launcher's {} MiB download cap.",
                contract_limits::MAXIMUM_ZIP_BYTES / (1024 * 1024)
            ),
            "Shrink the archive's contents.",
        ));
        return CheckReport::finish(CheckMode::Zip, display(zip_path), checks);
    }
    checks.push(CheckItem::pass(
        "zip within size cap",
        format!("{length} bytes."),
    ));

    let staging = std::env::temp_dir()
        .join("openac-plugincheck")
        .join(ids::unique_hex32());
    let report = check_extracted(zip_path, &staging, checks);
    fsutil::try_delete_tree(&staging);
    fsutil::try_delete_if_empty(&std::env::temp_dir().join("openac-plugincheck"));
    report
}

fn check_extracted(zip_path: &Path, staging: &Path, mut checks: Vec<CheckItem>) -> CheckReport {
    let finish = |checks| CheckReport::finish(CheckMode::Zip, display(zip_path), checks);
    let extractor = SafeZipExtractor::new(contract_limits::extraction(), true);
    let extracted = match extractor.extract(zip_path, staging, &[], &CancelToken::none()) {
        Ok(extracted) => extracted,
        Err(error) => {
            checks.push(CheckItem::fail(
                "zip extracts safely",
                &error.to_string(),
                "Rebuild the archive without links, path traversal, or entries over the \
                 launcher's size/count limits.",
            ));
            return finish(checks);
        }
    };
    checks.push(CheckItem::pass(
        "zip extracts safely",
        format!(
            "{} file(s) extracted within the launcher's limits.",
            extracted.len()
        ),
    ));

    let manifest_path = staging.join("plugin.json");
    if !manifest_path.is_file() {
        checks.push(CheckItem::fail(
            "plugin.json present",
            &missing_manifest_message(&extracted),
            "Add a plugin.json at the root of the archive.",
        ));
        return finish(checks);
    }
    checks.push(CheckItem::pass(
        "plugin.json present",
        "Found plugin.json at the archive root.",
    ));
    let Some(manifest) = parse_manifest(&manifest_path, &mut checks) else {
        return finish(checks);
    };
    add_install_rules(&manifest, &mut checks);
    match validate_plugin_content(&extracted, &manifest.entry) {
        Ok(()) => checks.push(CheckItem::pass(
            "plugin content policy",
            "Every file has an allowed extension (.wasm .json .xml .txt .md .png .ttf .otf) \
             and the declared entry module is present.",
        )),
        Err(error) => checks.push(CheckItem::fail(
            "plugin content policy",
            &error.to_string(),
            "Ship only the allowed file types (no native code) and the declared entry .wasm.",
        )),
    }
    checks.push(check_icon(staging, &extracted));
    checks.push(check_sha256_sidecar(zip_path));
    checks.push(check_asset_name(zip_path, &manifest));
    checks.push(check_wasm_module(staging, &manifest));
    finish(checks)
}

/// The content policy's own message for a missing manifest (it refuses that
/// before it looks at the entry, so the placeholder never shows).
fn missing_manifest_message(files: &[ExtractedFileRecord]) -> String {
    match validate_plugin_content(files, "unknown.wasm") {
        Ok(()) => "The plugin archive has no 'plugin.json' at its root.".into(),
        Err(error) => error.to_string(),
    }
}

fn parse_manifest(path: &Path, checks: &mut Vec<CheckItem>) -> Option<PluginManifest> {
    let text = match std::fs::read(path) {
        Ok(bytes) => {
            let bytes = bytes
                .strip_prefix(b"\xEF\xBB\xBF")
                .map(<[u8]>::to_vec)
                .unwrap_or(bytes);
            String::from_utf8_lossy(&bytes).into_owned()
        }
        Err(error) => {
            checks.push(CheckItem::fail(
                "plugin.json parses",
                &format!("plugin.json could not be read: {error}"),
                "Make plugin.json a readable UTF-8 file.",
            ));
            return None;
        }
    };
    match PluginManifest::parse(&text) {
        Ok(manifest) => {
            checks.push(CheckItem::pass(
                "plugin.json parses",
                format!(
                    "id={}, version={}, entry={}, apiVersion={}.",
                    manifest.id, manifest.version, manifest.entry, manifest.api_version
                ),
            ));
            Some(manifest)
        }
        Err(error) => {
            checks.push(CheckItem::fail(
                "plugin.json parses",
                &error.0,
                "Correct the problem named above in plugin.json; the field rules are in the \
                 plugin.json section of docs/plugin-development.md. Parsing stops at the first problem, so run this \
                 again after each fix.",
            ));
            None
        }
    }
}

fn add_install_rules(manifest: &PluginManifest, checks: &mut Vec<CheckItem>) {
    match manifest.validate_for_install() {
        Ok(()) => checks.push(CheckItem::pass(
            "manifest satisfies install rules",
            "id, version, minHostVersion, apiVersion and permissions are all valid.",
        )),
        Err(error) => checks.push(CheckItem::fail(
            "manifest satisfies install rules",
            &error.0,
            "Correct the field named above. These rules stop at the first problem, so run this \
             again after each fix.",
        )),
    }
}

fn check_icon(staging: &Path, extracted: &[ExtractedFileRecord]) -> CheckItem {
    if !extracted.iter().any(|file| file.path == ICON_FILE_NAME) {
        return CheckItem::skip(
            "plugin icon",
            "No icon.png at the archive root; it is optional.",
        );
    }
    let bytes = match fsutil::read_at_most(&staging.join(ICON_FILE_NAME), ICON_MAXIMUM_BYTES + 1) {
        Ok(bytes) => bytes,
        Err(error) => {
            return CheckItem::fail(
                "plugin icon",
                &format!("icon.png could not be read: {error}"),
                "Rebuild the archive.",
            );
        }
    };
    match validate_icon(&bytes) {
        Ok(()) => CheckItem::pass(
            "plugin icon",
            format!(
                "icon.png is a valid {ICON_EXTENT}x{ICON_EXTENT} PNG, {} bytes.",
                bytes.len()
            ),
        ),
        Err(error) => CheckItem::fail(
            "plugin icon",
            &error.to_string(),
            &format!(
                "Make icon.png a non-animated {ICON_EXTENT}x{ICON_EXTENT} PNG under {} KiB.",
                ICON_MAXIMUM_BYTES / 1024
            ),
        ),
    }
}

/// The `.sha256` sidecar is a release asset beside the zip, not part of it;
/// absent locally it only matters once a release is published.
fn check_sha256_sidecar(zip_path: &Path) -> CheckItem {
    let mut sidecar_path = zip_path.as_os_str().to_owned();
    sidecar_path.push(".sha256");
    let sidecar_path = PathBuf::from(sidecar_path);
    if !sidecar_path.is_file() {
        return CheckItem::skip(
            "sha256 sidecar",
            "No '<zip>.sha256' beside the archive; only required once this ships as a GitHub \
             release, not for a local check.",
        );
    }
    let regenerate = "Regenerate the sidecar with `shasum -a 256 <zip>` (or plugincheck pack).";
    let sidecar = match std::fs::read_to_string(&sidecar_path)
        .map_err(|error| error.to_string())
        .and_then(|text| PluginSha256File::parse(&text).map_err(|error| error.to_string()))
    {
        Ok(sidecar) => sidecar,
        Err(error) => return CheckItem::fail("sha256 sidecar", &error, regenerate),
    };
    let expected_name = zip_path
        .file_name()
        .map(|name| name.to_string_lossy().into_owned())
        .unwrap_or_default();
    if let Err(error) = sidecar.require_matches(&expected_name) {
        return CheckItem::fail(
            "sha256 sidecar",
            &error.to_string(),
            &format!(
                "Name the sidecar's target '{expected_name}', or regenerate it beside the renamed \
                 archive."
            ),
        );
    }
    let actual = match integrity::compute_sha256_hex(zip_path) {
        Ok(actual) => actual,
        Err(error) => {
            return CheckItem::fail(
                "sha256 sidecar",
                &format!("The archive could not be read: {error}"),
                regenerate,
            );
        }
    };
    if actual != sidecar.sha256 {
        return CheckItem::fail(
            "sha256 sidecar",
            &format!(
                "The sidecar declares {}, but the archive hashes to {actual}.",
                sidecar.sha256
            ),
            regenerate,
        );
    }
    CheckItem::pass(
        "sha256 sidecar",
        format!("Matches the archive's SHA-256 ({}).", sidecar.sha256),
    )
}

/// The installer downloads exactly `<id>-<version>.zip`; a local file may be
/// named anything, so another name is only noted.
fn check_asset_name(zip_path: &Path, manifest: &PluginManifest) -> CheckItem {
    let expected = format!("{}-{}.zip", manifest.id, manifest.version);
    let actual = zip_path
        .file_name()
        .map(|name| name.to_string_lossy().into_owned())
        .unwrap_or_default();
    if actual == expected {
        CheckItem::pass(
            "release asset name",
            format!("Named {expected}, as the launcher downloads it."),
        )
    } else {
        CheckItem::skip(
            "release asset name",
            format!(
                "The launcher downloads '{expected}'; rename '{actual}' to that before publishing."
            ),
        )
    }
}

/// Loads the entry module without running it and checks it as a session
/// does before instantiating it.
fn check_wasm_module(directory: &Path, manifest: &PluginManifest) -> CheckItem {
    const CHECK: &str = "module matches its manifest";
    let entry = directory.join(entry_path(&manifest.entry));
    if !entry.is_file() {
        return CheckItem::skip(
            CHECK,
            format!(
                "No entry module '{}' to load (refused above).",
                manifest.entry
            ),
        );
    }
    if let Err(error) = check_api_version(manifest) {
        return CheckItem::fail(
            CHECK,
            &error,
            "Build against an API version this client supports and declare it in plugin.json.",
        );
    }
    let engine = PluginEngine::new();
    let module = match engine.module(&entry) {
        Ok(module) => module,
        Err(error) => {
            return CheckItem::fail(
                CHECK,
                &error,
                "Build the plugin for wasm32-unknown-unknown with the openac-plugin SDK.",
            );
        }
    };
    match check_module(&module, manifest) {
        Ok(checked) => {
            let mut needed: Vec<&str> = checked
                .imports
                .iter()
                .filter_map(|import| import.permission())
                .map(|permission| permission.name())
                .collect();
            needed.sort_unstable();
            needed.dedup();
            let declared: Vec<&str> = manifest
                .permissions
                .iter()
                .map(|declaration| declaration.permission.name())
                .collect();
            // Only a permission some client function requires can be seen
            // in the imports. One the client checks per call instead (the
            // profiles folder, asked for by name in a storage call) cannot,
            // so it is never called unused.
            let judged: Vec<&str> = ac_plugin::abi::Import::ALL
                .iter()
                .filter_map(|import| import.permission())
                .map(|permission| permission.name())
                .collect();
            let unused: Vec<&str> = declared
                .iter()
                .copied()
                .filter(|name| judged.contains(name) && !needed.contains(name))
                .collect();
            let mut message = format!(
                "API {}; {} client function(s) imported, all allowed. Permissions used: {}; \
                 declared: {}.",
                checked.api_version,
                checked.imports.len(),
                list_or_none(&needed),
                list_or_none(&declared),
            );
            if !unused.is_empty() {
                message.push_str(&format!(
                    " Declared but not used: {} (the player is still asked for it).",
                    unused.join(", ")
                ));
            }
            CheckItem::pass(CHECK, message)
        }
        Err(error) if error.contains(LAYOUT_REFUSAL) => CheckItem::fail(
            CHECK,
            &error,
            "Rebuild the plugin against the openac-plugin SDK of the client it is for (update \
             the SDK crates in Cargo.lock, then build the .wasm again).",
        ),
        Err(error) => CheckItem::fail(
            CHECK,
            &error,
            "Declare the permission the import needs in plugin.json (with a note saying why), or \
             stop using that function; rebuild with the SDK for the declared apiVersion.",
        ),
    }
}

fn list_or_none(names: &[&str]) -> String {
    if names.is_empty() {
        "none".into()
    } else {
        names.join(", ")
    }
}
