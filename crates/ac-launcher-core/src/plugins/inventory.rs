//! What is installed: launcher-managed plugins, folders unzipped by hand
//! (direct installs, checked against every install rule each time) and the
//! plugins the client bundles, cross-referenced so a duplicated id is never
//! loaded. A hand-unzipped folder holding an OpenAC (C#) plugin is listed
//! as refused, so the player sees why it does nothing here.

use std::collections::{HashMap, HashSet, VecDeque};
use std::path::{Path, PathBuf};

use ac_host_core::PathSet;

use super::catalog::PluginCatalog;
use super::installer::contract_limits;
use super::manifest::{InstallRules, ManifestError, PluginManifest, is_built_for_csharp};
use super::record_store::{InstalledPluginRecordStore, PluginInstallSource};
use super::rules::{
    CLIENT_NOT_INSTALLED, ICON_FILE_NAME, ICON_MAXIMUM_BYTES, describe, validate_icon,
    validate_plugin_content,
};
use crate::fsutil;
use crate::updates::{
    ClientVersionResolution, ExtractedFileRecord, LauncherVersion, UpdateError,
    is_windows_device_name,
};

/// Where an installed plugin's files came from.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum InstalledPluginSource {
    /// Installed by the launcher.
    Managed,
    /// Unzipped by hand into the plugins folder.
    Direct,
    /// Shipped with the client.
    Bundled,
}

impl InstalledPluginSource {
    /// How a message names the source.
    pub fn describe(self) -> &'static str {
        match self {
            Self::Managed => "launcher-installed",
            Self::Direct => "directly installed",
            Self::Bundled => "client-bundled",
        }
    }
}

/// One row of the Installed view.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct InstalledPluginInfo {
    /// The plugin id.
    pub id: String,
    /// Its name.
    pub display_name: String,
    /// Its version.
    pub version: String,
    /// Where it came from.
    pub source: InstalledPluginSource,
    /// Its folder.
    pub directory: PathBuf,
    /// `owner/name`, for a managed plugin.
    pub repo: Option<String>,
    /// The compatibility line.
    pub compatibility: String,
    /// Whether that line is a warning.
    pub compatibility_is_warning: bool,
    /// The block reason, when the list blocks this version.
    pub blocked: Option<String>,
    /// A managed or direct copy of an id the client also bundles.
    pub conflict: bool,
    /// Listed or unlisted, for a managed plugin.
    pub listed_source: Option<PluginInstallSource>,
    /// Why a direct install is refused.
    pub refusal: Option<String>,
    /// Another copy of the same id is installed.
    pub has_duplicate: bool,
}

const DUPLICATE_REFUSAL: &str = "Another copy of this plugin is installed.";

/// The refusal shown for a folder holding an OpenAC (C#) plugin.
pub const BUILT_FOR_CSHARP_REFUSAL: &str =
    "Built for OpenAC (C#): this client loads WebAssembly plugins only.";

/// The version-only verdict against the installed client (min, max, skip).
pub fn evaluate_version_compatibility(
    manifest: &PluginManifest,
    client_version: Option<&LauncherVersion>,
) -> Option<String> {
    super::rules::version_only_reason(manifest, client_version)
}

/// The reason the list blocks this exact version of this client's build, if
/// it does.
pub fn find_block_reason(
    catalog: Option<&PluginCatalog>,
    id: &str,
    plugin_version: &LauncherVersion,
) -> Option<String> {
    catalog?
        .blocks_for_this_client()
        .find(|block| block.id.eq_ignore_ascii_case(id) && block.matches(plugin_version.value()))
        .map(|block| block.reason.clone())
}

/// What a plugin folder's `plugin.json` turned out to be.
enum Scanned {
    /// A manifest this client reads.
    Plugin(Box<PluginManifest>),
    /// An OpenAC (C#) plugin's manifest.
    CSharp,
}

fn scan_manifest(path: &Path) -> Option<Scanned> {
    let text = std::fs::read_to_string(path).ok()?;
    match PluginManifest::parse(&text) {
        Ok(manifest) => Some(Scanned::Plugin(Box::new(manifest))),
        Err(error) if is_built_for_csharp(&error) => Some(Scanned::CSharp),
        Err(_) => None,
    }
}

fn try_parse_manifest(path: &Path) -> Option<PluginManifest> {
    match scan_manifest(path)? {
        Scanned::Plugin(manifest) => Some(*manifest),
        Scanned::CSharp => None,
    }
}

fn scan_manifests(root: &Path) -> Vec<(PathBuf, Scanned)> {
    let Ok(entries) = std::fs::read_dir(root) else {
        return Vec::new();
    };
    let mut directories: Vec<PathBuf> = entries
        .flatten()
        .map(|entry| entry.path())
        .filter(|path| path.is_dir())
        .collect();
    directories.sort();
    directories
        .into_iter()
        .filter(|directory| {
            !directory
                .file_name()
                .is_some_and(|name| name.to_string_lossy().starts_with('.'))
        })
        .filter_map(|directory| {
            let manifest_path = directory.join("plugin.json");
            if !manifest_path.is_file() {
                return None;
            }
            scan_manifest(&manifest_path).map(|scanned| (directory, scanned))
        })
        .collect()
}

/// The row of a folder holding an OpenAC (C#) plugin: named by its folder
/// (its manifest is not this client's to read), refused, never loaded.
fn csharp_info(directory: PathBuf) -> InstalledPluginInfo {
    let name = directory
        .file_name()
        .map(|name| name.to_string_lossy().into_owned())
        .unwrap_or_default();
    InstalledPluginInfo {
        id: name.clone(),
        display_name: name,
        version: String::new(),
        source: InstalledPluginSource::Direct,
        directory,
        repo: None,
        compatibility: BUILT_FOR_CSHARP_REFUSAL.into(),
        compatibility_is_warning: true,
        blocked: None,
        conflict: false,
        listed_source: None,
        refusal: Some(BUILT_FOR_CSHARP_REFUSAL.into()),
        has_duplicate: false,
    }
}

#[allow(clippy::too_many_arguments)]
fn build_info(
    manifest: &PluginManifest,
    directory: PathBuf,
    source: InstalledPluginSource,
    repo: Option<String>,
    listed_source: Option<PluginInstallSource>,
    catalog: Option<&PluginCatalog>,
    client_version: Option<&LauncherVersion>,
    refusal: Option<String>,
) -> InstalledPluginInfo {
    let blocked = LauncherVersion::try_parse(&manifest.version)
        .and_then(|version| find_block_reason(catalog, &manifest.id, &version));
    let compatibility = describe(manifest, client_version);
    InstalledPluginInfo {
        id: manifest.id.clone(),
        display_name: manifest.display_name.clone(),
        version: manifest.version.clone(),
        source,
        directory,
        repo,
        compatibility: compatibility.text,
        compatibility_is_warning: compatibility.is_warning,
        blocked,
        conflict: false,
        listed_source,
        refusal,
        has_duplicate: false,
    }
}

/// Builds the Installed view from `plugin.json` files.
#[derive(Debug, Clone)]
pub struct PluginInventory {
    plugins_directory: PathBuf,
}

impl PluginInventory {
    /// The inventory of `<data>/plugins`.
    pub fn new(paths: &PathSet) -> Self {
        Self {
            plugins_directory: paths.plugins_directory(),
        }
    }

    /// The plugins folder.
    pub fn plugins_directory(&self) -> &Path {
        &self.plugins_directory
    }

    /// Every installed plugin: each record's own folder first (so a stray
    /// folder with the same id cannot take the managed row), then every
    /// other folder as a direct install (checked), then the client's
    /// bundled plugins. A copy of an id the client bundles is marked as a
    /// conflict; any id present more than once is refused on its direct
    /// copies and flagged on the others.
    pub fn build(
        &self,
        records: &InstalledPluginRecordStore,
        client_resolution: Option<&ClientVersionResolution>,
        catalog: Option<&PluginCatalog>,
    ) -> Vec<InstalledPluginInfo> {
        let client_version = client_resolution.and_then(|resolution| resolution.version.as_ref());
        let bundled_directory = client_resolution
            .filter(|resolution| resolution.is_verified())
            .and_then(|resolution| resolution.directory.as_ref())
            .map(|directory| directory.join("plugins"));

        let mut results = Vec::new();
        let mut placed: HashSet<String> = HashSet::new();
        let mut claimed: HashSet<String> = HashSet::new();
        for record in &records.records {
            let own = self.plugins_directory.join(&record.id);
            let manifest_path = own.join("plugin.json");
            if !manifest_path.is_file() {
                continue;
            }
            let Some(manifest) = try_parse_manifest(&manifest_path) else {
                continue;
            };
            if !manifest.id.eq_ignore_ascii_case(&record.id) {
                continue;
            }
            claimed.insert(own.to_string_lossy().to_lowercase());
            placed.insert(manifest.id.to_lowercase());
            results.push(build_info(
                &manifest,
                own,
                InstalledPluginSource::Managed,
                Some(record.repo.clone()),
                Some(record.source),
                catalog,
                client_version,
                None,
            ));
        }
        // C# folders are listed after the duplicate pass: they never load, so
        // they cannot make a real plugin a duplicate.
        let mut csharp = Vec::new();
        for (directory, scanned) in scan_manifests(&self.plugins_directory) {
            if claimed.contains(&directory.to_string_lossy().to_lowercase()) {
                continue;
            }
            let manifest = match scanned {
                Scanned::Plugin(manifest) => manifest,
                Scanned::CSharp => {
                    csharp.push(csharp_info(directory));
                    continue;
                }
            };
            placed.insert(manifest.id.to_lowercase());
            let refusal = direct_install_refusal(&directory, &manifest);
            results.push(build_info(
                &manifest,
                directory,
                InstalledPluginSource::Direct,
                None,
                None,
                catalog,
                client_version,
                refusal,
            ));
        }

        let mut bundled_ids: HashSet<String> = HashSet::new();
        let mut unrepresented: HashMap<String, usize> = HashMap::new();
        if let Some(bundled) = &bundled_directory {
            for (directory, scanned) in scan_manifests(bundled) {
                let Scanned::Plugin(manifest) = scanned else {
                    continue;
                };
                let key = manifest.id.to_lowercase();
                bundled_ids.insert(key.clone());
                if placed.insert(key.clone()) {
                    results.push(build_info(
                        &manifest,
                        directory,
                        InstalledPluginSource::Bundled,
                        None,
                        None,
                        catalog,
                        client_version,
                        None,
                    ));
                } else {
                    *unrepresented.entry(key).or_default() += 1;
                }
            }
        }

        let mut occurrences: HashMap<String, usize> = HashMap::new();
        for info in &results {
            *occurrences.entry(info.id.to_lowercase()).or_default() += 1;
        }
        for (id, extra) in unrepresented {
            *occurrences.entry(id).or_default() += extra;
        }
        for info in &mut results {
            let key = info.id.to_lowercase();
            if info.source != InstalledPluginSource::Bundled && bundled_ids.contains(&key) {
                info.conflict = true;
            }
            if occurrences.get(&key).copied().unwrap_or_default() > 1 {
                if info.source == InstalledPluginSource::Direct {
                    info.refusal
                        .get_or_insert_with(|| DUPLICATE_REFUSAL.to_owned());
                } else {
                    info.has_duplicate = true;
                }
            }
        }
        results.extend(csharp);
        results
    }

    /// The row for `id` (ignoring case).
    pub fn find(
        &self,
        id: &str,
        records: &InstalledPluginRecordStore,
        client_resolution: Option<&ClientVersionResolution>,
        catalog: Option<&PluginCatalog>,
    ) -> Option<InstalledPluginInfo> {
        self.build(records, client_resolution, catalog)
            .into_iter()
            .find(|info| info.id.eq_ignore_ascii_case(id))
    }
}

/// Whether the client-not-installed reason is the only verdict.
pub fn is_client_not_installed(reason: &str) -> bool {
    reason == CLIENT_NOT_INSTALLED
}

// --- Direct installs -------------------------------------------------------------

fn is_ignored(name: &str) -> bool {
    name.eq_ignore_ascii_case(".DS_Store")
        || name.eq_ignore_ascii_case("Thumbs.db")
        || name.eq_ignore_ascii_case("desktop.ini")
        || name.starts_with("._")
}

fn segment_refusal(segment: &str, relative: &str) -> Option<String> {
    let bad = segment == "."
        || segment == ".."
        || segment.ends_with(' ')
        || segment.ends_with('.')
        || segment
            .chars()
            .any(|c| c.is_control() || matches!(c, '<' | '>' | '"' | '|' | '?' | '*' | ':'))
        || is_windows_device_name(segment);
    bad.then(|| format!("'{relative}' has a name the launcher will not accept."))
}

/// The first install rule a hand-unzipped plugin folder breaks, as a
/// player-facing reason, or `None` when it passes: no links or reparse
/// points, regular files and folders only, portable names, the extraction
/// limits, the content policy, the install manifest rules, and the icon.
/// Finder and Explorer metadata files are ignored. Advisory, not a security
/// boundary.
pub fn direct_install_refusal(directory: &Path, manifest: &PluginManifest) -> Option<String> {
    match walk(directory, manifest) {
        Ok(refusal) => refusal,
        Err(WalkError::Update(error)) => Some(error.to_string()),
        Err(WalkError::Manifest(error)) => Some(error.to_string()),
        Err(WalkError::Io) => Some("The plugin folder could not be read.".into()),
    }
}

enum WalkError {
    Update(UpdateError),
    Manifest(ManifestError),
    Io,
}

impl From<std::io::Error> for WalkError {
    fn from(_: std::io::Error) -> Self {
        Self::Io
    }
}

fn walk(directory: &Path, manifest: &PluginManifest) -> Result<Option<String>, WalkError> {
    if fsutil::is_link(directory)? {
        return Ok(Some(
            "The plugin folder is a link, which the launcher will not follow.".into(),
        ));
    }
    let limits = contract_limits::extraction();
    let mut seen: HashSet<String> = HashSet::new();
    let mut files: Vec<ExtractedFileRecord> = Vec::new();
    let mut pending: VecDeque<String> = VecDeque::from([String::new()]);
    let mut file_count = 0usize;
    let mut total_bytes = 0u64;
    while let Some(relative_directory) = pending.pop_front() {
        let absolute = if relative_directory.is_empty() {
            directory.to_path_buf()
        } else {
            relative_directory
                .split('/')
                .fold(directory.to_path_buf(), |path, part| path.join(part))
        };
        let mut entries: Vec<_> = std::fs::read_dir(&absolute)?.collect::<Result<_, _>>()?;
        entries.sort_by_key(std::fs::DirEntry::file_name);
        for entry in entries {
            let name = entry.file_name().to_string_lossy().into_owned();
            if is_ignored(&name) {
                continue;
            }
            // The plugin's own saved files and the shared VTank folder are
            // the player's, not part of what was installed, and grow without
            // any install limit.
            if relative_directory.is_empty()
                && entry.file_type().is_ok_and(|kind| kind.is_dir())
                && ac_host_core::paths::is_player_folder(&name)
            {
                continue;
            }
            let relative = if relative_directory.is_empty() {
                name.clone()
            } else {
                format!("{relative_directory}/{name}")
            };
            let metadata = std::fs::symlink_metadata(entry.path())?;
            if fsutil::metadata_is_link(&metadata) {
                return Ok(Some(format!(
                    "'{relative}' is a link, which the launcher will not follow."
                )));
            }
            if let Some(refusal) = segment_refusal(&name, &relative) {
                return Ok(Some(refusal));
            }
            if relative.chars().count() > limits.maximum_relative_path_length {
                return Ok(Some(format!(
                    "'{relative}' is longer than {} characters.",
                    limits.maximum_relative_path_length
                )));
            }
            if !seen.insert(relative.to_lowercase()) {
                return Ok(Some(format!(
                    "'{relative}' collides with another entry that differs only in case."
                )));
            }
            if !metadata.is_file() && !metadata.is_dir() {
                return Ok(Some(format!(
                    "'{relative}' is not a regular file or folder."
                )));
            }
            if metadata.is_dir() {
                pending.push_back(relative);
                continue;
            }
            file_count += 1;
            if file_count > limits.maximum_entries {
                return Ok(Some(format!(
                    "The plugin folder has more than {} files.",
                    limits.maximum_entries
                )));
            }
            let length = metadata.len();
            if length > limits.maximum_entry_bytes {
                return Ok(Some(format!(
                    "'{relative}' is larger than {} MiB.",
                    limits.maximum_entry_bytes / (1024 * 1024)
                )));
            }
            total_bytes += length;
            if total_bytes > limits.maximum_total_bytes {
                return Ok(Some(format!(
                    "The plugin folder is larger than {} MiB.",
                    limits.maximum_total_bytes / (1024 * 1024)
                )));
            }
            files.push(ExtractedFileRecord::new(relative, "", length, 0));
        }
    }
    validate_plugin_content(&files, &manifest.entry).map_err(WalkError::Update)?;
    manifest
        .validate_for_install()
        .map_err(WalkError::Manifest)?;
    let icon = directory.join(ICON_FILE_NAME);
    if seen.contains(ICON_FILE_NAME) && icon.is_file() {
        let bytes = fsutil::read_at_most(&icon, ICON_MAXIMUM_BYTES + 1)?;
        validate_icon(&bytes).map_err(WalkError::Update)?;
    }
    Ok(None)
}
