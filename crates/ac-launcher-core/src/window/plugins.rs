//! The plugin backend the window uses: the release client, resolver,
//! installer, inventory and record store built once, recovery at start, and
//! the Check pipeline that the start-of-launcher check and Refresh list
//! share. Nothing here runs a plugin.

use std::collections::{HashMap, HashSet};
use std::path::PathBuf;
use std::sync::{Arc, Mutex, MutexGuard, PoisonError};
use std::time::SystemTime;

use ac_host::PathSet;

use crate::cancel::CancelToken;
use crate::launcher_log::LauncherLog;
use crate::net::HttpTransport;
use crate::plugins::{
    InstalledPluginInfo, InstalledPluginRecord, InstalledPluginRecordStore, MAXIMUM_DOCUMENT_BYTES,
    PermissionDeclaration, PluginCatalog, PluginInstaller, PluginInventory,
    PluginReleaseCandidates, PluginReleaseClient, PluginReleaseFetchResult,
    PluginReleaseResolveResult, PluginReleaseResolver, describe, version_only_reason,
};
use crate::updates::{ClientVersionResolution, LauncherVersion};
use crate::url::WebUrl;

/// One listed plugin not yet installed, as Discover shows it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PluginDiscoverEntry {
    /// The plugin id.
    pub id: String,
    /// Its name.
    pub name: String,
    /// Its author.
    pub author: String,
    /// Its description.
    pub description: String,
    /// `owner/name`.
    pub repo: String,
}

/// An installed plugin's newer release: its version and tag (pinned through
/// to the update dialog's install), its compatibility line, and the
/// permissions it declares.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PluginUpdateAvailability {
    /// The newer version.
    pub version: String,
    /// Its release tag.
    pub tag: String,
    /// Its compatibility line.
    pub compatibility_note: Option<String>,
    /// Whether that line is a warning.
    pub compatibility_is_warning: bool,
    /// What it declares.
    pub permissions: Vec<PermissionDeclaration>,
}

/// One plugin's re-check: an update, or why a newer release is withheld.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct PluginSingleCheckResult {
    /// The update, when offered.
    pub available: Option<PluginUpdateAvailability>,
    /// Why a newer release is not offered.
    pub withheld_reason: Option<String>,
}

/// One Check pass: the effective list, whether GitHub throttled it, the
/// cached list's age when the fetch failed, and the rows built from it.
#[derive(Debug, Clone, PartialEq)]
pub struct PluginCheckOutcome {
    /// The list in effect (fresh or cached), when there is one.
    pub catalog: Option<PluginCatalog>,
    /// GitHub answered 429.
    pub rate_limited: bool,
    /// When the cached list was written, if the cache was used.
    pub list_age: Option<SystemTime>,
    /// Installed plugins.
    pub installed: Vec<InstalledPluginInfo>,
    /// Listed plugins not installed and not blocked for every version.
    pub discover: Vec<PluginDiscoverEntry>,
    /// Updates by plugin id (lower case).
    pub updates_available: HashMap<String, PluginUpdateAvailability>,
    /// Withheld reasons by plugin id (lower case).
    pub update_withheld_reasons: HashMap<String, String>,
}

/// The plugin backend.
pub struct PluginComposition {
    paths: PathSet,
    list_uri: WebUrl,
    cache_file_path: PathBuf,
    /// Fetches release documents.
    pub release_client: PluginReleaseClient,
    /// Resolves repositories to releases.
    pub release_resolver: PluginReleaseResolver,
    /// The installer.
    pub installer: PluginInstaller,
    /// The installed view.
    pub inventory: PluginInventory,
    records: Arc<Mutex<InstalledPluginRecordStore>>,
    current_catalog: Mutex<Option<PluginCatalog>>,
    log: LauncherLog,
}

impl std::fmt::Debug for PluginComposition {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("PluginComposition")
            .field("list_uri", &self.list_uri)
            .finish_non_exhaustive()
    }
}

impl PluginComposition {
    /// Builds the backend for `paths` over `transport`. A record file that
    /// cannot be read only costs the launcher its memory of which plugins it
    /// manages; it never keeps the window from opening.
    pub fn new(paths: &PathSet, list_uri: WebUrl, transport: Arc<dyn HttpTransport>) -> Self {
        let mut store = InstalledPluginRecordStore::for_paths(paths);
        if store.load().is_err() {
            store.records.clear();
        }
        let records = Arc::new(Mutex::new(store));
        let release_client = PluginReleaseClient::new(Arc::clone(&transport));
        Self {
            paths: paths.clone(),
            list_uri,
            cache_file_path: paths.cache_directory.join("plugins.json"),
            release_resolver: PluginReleaseResolver::new(release_client.clone()),
            release_client,
            installer: PluginInstaller::new(paths, transport, Arc::clone(&records)),
            inventory: PluginInventory::new(paths),
            records,
            current_catalog: Mutex::new(None),
            log: LauncherLog::open(paths),
        }
    }

    /// Keeps a repository's index warning in the launcher log: a publisher's
    /// mistake the player never sees.
    pub fn log_index_warning(&self, repo: &str, candidates: &PluginReleaseCandidates) {
        self.log_index_warning_text(repo, candidates.index_warning.as_deref());
    }

    /// As [`Self::log_index_warning`], from the warning itself.
    pub fn log_index_warning_text(&self, repo: &str, warning: Option<&str>) {
        if let Some(warning) = warning {
            self.log
                .line(&format!("Plugin index of {repo} ignored: {warning}"));
        }
    }

    /// The directories.
    pub fn paths(&self) -> &PathSet {
        &self.paths
    }

    /// The curated list address.
    pub fn list_uri(&self) -> &WebUrl {
        &self.list_uri
    }

    /// The record store (shared with the installer).
    pub fn records(&self) -> MutexGuard<'_, InstalledPluginRecordStore> {
        self.records.lock().unwrap_or_else(PoisonError::into_inner)
    }

    /// The managed record for `id`.
    pub fn find_record(&self, id: &str) -> Option<InstalledPluginRecord> {
        self.records().find(id).cloned()
    }

    /// The list from the most recent Check, for filtering blocked ids out
    /// of the next launched session.
    pub fn current_catalog(&self) -> Option<PluginCatalog> {
        self.current_catalog
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .clone()
    }

    /// Every installed plugin as of now.
    pub fn installed(
        &self,
        client: Option<&ClientVersionResolution>,
        catalog: Option<&PluginCatalog>,
    ) -> Vec<InstalledPluginInfo> {
        let records = self.records();
        self.inventory.build(&records, client, catalog)
    }

    /// The installed row for `id`.
    pub fn find_installed(
        &self,
        id: &str,
        client: Option<&ClientVersionResolution>,
        catalog: Option<&PluginCatalog>,
    ) -> Option<InstalledPluginInfo> {
        let records = self.records();
        self.inventory.find(id, &records, client, catalog)
    }

    /// The launch-time recovery, run once before the inventory is first read.
    pub fn recover(&self) -> Result<(), crate::updates::UpdateError> {
        self.installer.recover()
    }

    /// The Check pipeline: fetches the list, falls back to the last cached
    /// copy on failure, writes a fresh cache on success, and builds the
    /// rows. Never fails; every failure is in the outcome.
    pub fn check(
        &self,
        client: Option<&ClientVersionResolution>,
        cancel: &CancelToken,
    ) -> PluginCheckOutcome {
        let mut rate_limited = false;
        let mut list_age = None;
        let catalog =
            match self
                .release_client
                .fetch_document(&self.list_uri, MAXIMUM_DOCUMENT_BYTES, cancel)
            {
                Ok(PluginReleaseFetchResult::Success(document)) => {
                    match PluginCatalog::parse(&String::from_utf8_lossy(&document.content)) {
                        Ok(catalog) => {
                            self.write_cache(&document.content);
                            Some(catalog)
                        }
                        Err(_) => self.try_read_cache(&mut list_age),
                    }
                }
                Ok(PluginReleaseFetchResult::RateLimited) => {
                    rate_limited = true;
                    self.try_read_cache(&mut list_age)
                }
                Ok(PluginReleaseFetchResult::Unavailable) | Err(_) => {
                    self.try_read_cache(&mut list_age)
                }
            };
        *self
            .current_catalog
            .lock()
            .unwrap_or_else(PoisonError::into_inner) = catalog.clone();

        let installed = self.installed(client, catalog.as_ref());
        let mut updates_available = HashMap::new();
        let mut update_withheld_reasons = HashMap::new();
        if !rate_limited {
            let resolver = self.release_resolver.pass();
            for info in &installed {
                let Some(record) = self.find_record(&info.id) else {
                    continue;
                };
                if record.pending.is_some() {
                    continue;
                }
                let check =
                    self.evaluate_update(&resolver, &record, catalog.as_ref(), client, cancel);
                if let Some(available) = check.available {
                    updates_available.insert(info.id.to_lowercase(), available);
                } else if let Some(reason) = check.withheld_reason {
                    update_withheld_reasons.insert(info.id.to_lowercase(), reason);
                }
            }
        }

        let installed_ids: HashSet<String> = installed
            .iter()
            .map(|info| info.id.to_lowercase())
            .collect();
        // A plugin blocked for every version is a dead end: left out before it costs Discover its
        // per-plugin request. A version-specific block needs that request to know the latest
        // version, so it stays listed and is judged afterwards.
        let discover = catalog
            .as_ref()
            .map(|catalog| {
                // Only this client's builds: an OpenAC (C#) entry in the list is not
                // offered here.
                catalog
                    .entries_for_this_client()
                    .filter(|entry| !installed_ids.contains(&entry.id.to_lowercase()))
                    .filter(|entry| !catalog.is_blocked_for_all_versions(&entry.id))
                    .map(|entry| PluginDiscoverEntry {
                        id: entry.id.clone(),
                        name: entry.name.clone(),
                        author: entry.author.clone(),
                        description: entry.description.clone(),
                        repo: entry.repo.clone(),
                    })
                    .collect()
            })
            .unwrap_or_default();

        PluginCheckOutcome {
            catalog,
            rate_limited,
            list_age,
            installed,
            discover,
            updates_available,
            update_withheld_reasons,
        }
    }

    /// Re-checks one installed plugin against the most recent Check's list
    /// (the beta toggle), not the whole pass.
    pub fn check_single(
        &self,
        id: &str,
        client: Option<&ClientVersionResolution>,
        cancel: &CancelToken,
    ) -> PluginSingleCheckResult {
        let Some(record) = self.find_record(id) else {
            return PluginSingleCheckResult::default();
        };
        if record.pending.is_some() {
            return PluginSingleCheckResult::default();
        }
        let catalog = self.current_catalog();
        self.evaluate_update(
            &self.release_resolver,
            &record,
            catalog.as_ref(),
            client,
            cancel,
        )
    }

    /// An advisory badge, not an enforcement decision (the installer refuses
    /// the update itself). Names a reason only when a newer release exists
    /// but is not offered; already current stays silent.
    fn evaluate_update(
        &self,
        resolver: &PluginReleaseResolver,
        record: &InstalledPluginRecord,
        catalog: Option<&PluginCatalog>,
        client: Option<&ClientVersionResolution>,
        cancel: &CancelToken,
    ) -> PluginSingleCheckResult {
        let Some(current) = record
            .version
            .as_deref()
            .and_then(LauncherVersion::try_parse)
        else {
            return PluginSingleCheckResult::default();
        };
        let Ok(candidates) =
            resolver.resolve_plugin_candidates(&record.repo, &record.id, record.channel, cancel)
        else {
            return PluginSingleCheckResult::default();
        };
        self.log_index_warning(&record.repo, &candidates);
        let PluginReleaseResolveResult::Success(resolution) =
            candidates.for_channel(record.channel)
        else {
            // Rate limited, unavailable, invalid (a newer permission vocabulary included: the
            // client's parser refuses it) and a pre-release latest are all silent: none is a
            // newer release known to be withheld.
            return PluginSingleCheckResult::default();
        };
        let manifest = &resolution.manifest;
        // Resolving by id already skips another plugin's release; this keeps
        // an update from ever replacing one plugin with another.
        if !manifest.id.eq_ignore_ascii_case(&record.id) {
            return PluginSingleCheckResult::default();
        }
        let Some(remote) = LauncherVersion::try_parse(&manifest.version) else {
            return PluginSingleCheckResult::default();
        };
        if remote <= current {
            return PluginSingleCheckResult::default();
        }
        let withheld = |reason: &str| PluginSingleCheckResult {
            available: None,
            withheld_reason: Some(reason.to_owned()),
        };
        if catalog.is_some_and(|catalog| catalog.is_blocked(&record.id, Some(&remote))) {
            return withheld("the plugin is blocked");
        }
        let client_version = client.and_then(|client| client.version.as_ref());
        if let Some(reason) = version_only_reason(manifest, client_version) {
            return withheld(&reason);
        }
        let compatibility = describe(manifest, client_version);
        PluginSingleCheckResult {
            available: Some(PluginUpdateAvailability {
                version: manifest.version.clone(),
                tag: resolution.tag.clone(),
                compatibility_note: Some(compatibility.text),
                compatibility_is_warning: compatibility.is_warning,
                permissions: manifest.permissions.clone(),
            }),
            withheld_reason: None,
        }
    }

    fn write_cache(&self, content: &[u8]) {
        // A cache that cannot be written only costs the next offline start its block list.
        let _ = std::fs::create_dir_all(&self.paths.cache_directory)
            .and_then(|()| crate::fsutil::write_via_tmp(&self.cache_file_path, content, false));
    }

    fn try_read_cache(&self, age: &mut Option<SystemTime>) -> Option<PluginCatalog> {
        if !self.cache_file_path.is_file() {
            return None;
        }
        *age = std::fs::metadata(&self.cache_file_path)
            .and_then(|metadata| metadata.modified())
            .ok();
        let text = std::fs::read_to_string(&self.cache_file_path).ok()?;
        PluginCatalog::parse(&text).ok()
    }
}
