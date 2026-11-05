//! `<data>/app/plugins-installed.json`: every plugin the launcher itself
//! installed, with an in-flight swap's target kept until it is reconciled.

use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};

use ac_host_core::PathSet;
use ac_host_core::json::{self as host_json, Node, ObjectReader};

use super::manifest::has_valid_install_id;
use crate::fsutil;
use crate::json::{self, ObjectBuilder, Out};
use crate::time::{format_utc, parse_iso8601};
use crate::updates::UpdateError;

/// Whether the plugin's repository was on the curated list at install time.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PluginInstallSource {
    /// On the list.
    Listed,
    /// Added from a URL.
    Unlisted,
}

/// Which releases a plugin is offered.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum PluginReleaseChannel {
    /// Stable releases only.
    #[default]
    Stable,
    /// Pre-releases too, when newer.
    Beta,
}

/// The target of an in-flight swap.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PendingPluginInstall {
    /// The version being installed.
    pub version: String,
    /// Its release tag.
    pub tag: String,
    /// Its archive's SHA-256.
    pub zip_sha256: String,
}

/// One launcher-managed plugin. `version`, `tag` and `zip_sha256` are
/// `None` only while `pending` names a first install not yet swapped in.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct InstalledPluginRecord {
    /// The plugin id.
    pub id: String,
    /// `owner/name` on GitHub.
    pub repo: String,
    /// Listed or unlisted.
    pub source: PluginInstallSource,
    /// The installed version.
    pub version: Option<String>,
    /// Its release tag.
    pub tag: Option<String>,
    /// Its archive's SHA-256.
    pub zip_sha256: Option<String>,
    /// When it was first installed.
    pub installed_at: SystemTime,
    /// A swap in flight.
    pub pending: Option<PendingPluginInstall>,
    /// The release channel (left out of the file while stable).
    pub channel: PluginReleaseChannel,
}

/// The installed-plugin record file.
#[derive(Debug, Clone)]
pub struct InstalledPluginRecordStore {
    file_path: PathBuf,
    /// The records.
    pub records: Vec<InstalledPluginRecord>,
}

const CURRENT_SCHEMA_VERSION: i32 = 1;

fn record_json(record: &InstalledPluginRecord) -> Out {
    let optional_text = |value: &Option<String>| value.as_deref().map_or(Out::Null, Out::str);
    let pending = record.pending.as_ref().map_or(Out::Null, |pending| {
        ObjectBuilder::new()
            .member("version", Out::str(&pending.version))
            .member("tag", Out::str(&pending.tag))
            .member("zipSha256", Out::str(&pending.zip_sha256))
            .build()
    });
    ObjectBuilder::new()
        .member("id", Out::str(&record.id))
        .member("repo", Out::str(&record.repo))
        .member(
            "source",
            Out::str(match record.source {
                PluginInstallSource::Listed => "listed",
                PluginInstallSource::Unlisted => "unlisted",
            }),
        )
        .member("version", optional_text(&record.version))
        .member("tag", optional_text(&record.tag))
        .member("zipSha256", optional_text(&record.zip_sha256))
        .member("installedAt", Out::str(format_utc(record.installed_at)))
        .member("pending", pending)
        .optional(
            "channel",
            (record.channel == PluginReleaseChannel::Beta).then(|| Out::str("beta")),
        )
        .build()
}

fn failed_record(path: &Path) -> impl Fn(host_json::JsonError) -> UpdateError + '_ {
    move |_| {
        UpdateError::failed(format!(
            "'{}' is not a valid installed-plugin record.",
            path.display()
        ))
    }
}

fn read_record(node: &Node, path: &Path) -> Result<InstalledPluginRecord, UpdateError> {
    let fail = failed_record(path);
    let mut object = ObjectReader::new(node).map_err(&fail)?;
    let mut text = |name: &str| -> Result<Option<String>, UpdateError> {
        match object.take(name) {
            Some(node) => host_json::string_or_null(node).map_err(&fail),
            None => Ok(None),
        }
    };
    let id = text("id")?.unwrap_or_default();
    let repo = text("repo")?.unwrap_or_default();
    let version = text("version")?;
    let tag = text("tag")?;
    let zip_sha256 = text("zipSha256")?;
    let installed_at = text("installedAt")?;
    // Written by an earlier build; tolerated and dropped.
    let _warning_accepted_at = text("warningAcceptedAt")?;
    let source = match object.take("source") {
        Some(node) => host_json::enumeration(
            node,
            &[
                ("listed", PluginInstallSource::Listed),
                ("unlisted", PluginInstallSource::Unlisted),
            ],
        )
        .map_err(&fail)?,
        None => PluginInstallSource::Listed,
    };
    let channel = match object.take("channel") {
        Some(node) => host_json::enumeration(
            node,
            &[
                ("stable", PluginReleaseChannel::Stable),
                ("beta", PluginReleaseChannel::Beta),
            ],
        )
        .map_err(&fail)?,
        None => PluginReleaseChannel::Stable,
    };
    let pending = match object.take("pending") {
        Some(node) if !host_json::is_null(node) => {
            let mut pending = ObjectReader::new(node).map_err(&fail)?;
            let mut field = |name: &str| -> Result<String, UpdateError> {
                match pending.take(name) {
                    Some(node) => Ok(host_json::string_or_null(node)
                        .map_err(&fail)?
                        .unwrap_or_default()),
                    None => Ok(String::new()),
                }
            };
            let version = field("version")?;
            let tag = field("tag")?;
            let zip_sha256 = field("zipSha256")?;
            pending.finish().map_err(&fail)?;
            Some(PendingPluginInstall {
                version,
                tag,
                zip_sha256,
            })
        }
        _ => None,
    };
    object.finish().map_err(&fail)?;
    let installed_at = match installed_at {
        Some(text) => parse_iso8601(&text)
            .map(|parsed| parsed.instant)
            .ok_or_else(|| {
                fail(host_json::JsonError {
                    kind: host_json::JsonErrorKind::WrongType("a date"),
                    at: node.at,
                })
            })?,
        None => UNIX_EPOCH,
    };
    Ok(InstalledPluginRecord {
        id,
        repo,
        source,
        version,
        tag,
        zip_sha256,
        installed_at,
        pending,
        channel,
    })
}

impl InstalledPluginRecordStore {
    /// A store for `file_path`, empty until loaded.
    pub fn new(file_path: &Path) -> Self {
        Self {
            file_path: fsutil::full_path(file_path),
            records: Vec::new(),
        }
    }

    /// The store at `<data>/app/plugins-installed.json`.
    pub fn for_paths(paths: &PathSet) -> Self {
        Self::new(
            &paths
                .data_directory
                .join("app")
                .join("plugins-installed.json"),
        )
    }

    /// The file.
    pub fn file_path(&self) -> &Path {
        &self.file_path
    }

    fn temp_path(&self) -> PathBuf {
        let mut temp = self.file_path.as_os_str().to_owned();
        temp.push(".tmp");
        PathBuf::from(temp)
    }

    /// Loads the file (a missing file gives no records and `false`). Records
    /// with a missing id or repo, an id that could not name an install
    /// folder, or a repeated id (ignoring case) make the whole file invalid.
    pub fn load(&mut self) -> Result<bool, UpdateError> {
        fsutil::try_delete_file(&self.temp_path());
        if !self.file_path.exists() {
            self.records.clear();
            return Ok(false);
        }
        fsutil::ensure_owner_only(&self.file_path).map_err(|error| {
            UpdateError::io(
                format!(
                    "'{}' could not be secured to owner-only mode 0600.",
                    self.file_path.display()
                ),
                error,
            )
        })?;
        let bytes = std::fs::read(&self.file_path).map_err(|error| {
            UpdateError::io(
                format!("'{}' could not be read: {error}", self.file_path.display()),
                error,
            )
        })?;
        let fail = failed_record(&self.file_path);
        let root = host_json::parse(&bytes).map_err(&fail)?;
        if host_json::is_null(&root) {
            return Err(UpdateError::failed(format!(
                "'{}' is empty.",
                self.file_path.display()
            )));
        }
        let mut object = ObjectReader::new(&root).map_err(&fail)?;
        let schema_version = match object.take("schemaVersion") {
            Some(node) => host_json::int32(node).map_err(&fail)?,
            None => 0,
        };
        let plugins = object.take("plugins");
        object.finish().map_err(&fail)?;
        if schema_version != CURRENT_SCHEMA_VERSION {
            return Err(UpdateError::failed(format!(
                "Unsupported installed-plugin record version {schema_version}; expected \
                 {CURRENT_SCHEMA_VERSION}."
            )));
        }
        let mut records = Vec::new();
        if let Some(plugins) = plugins.filter(|node| !host_json::is_null(node)) {
            for item in host_json::array_items(plugins).map_err(&fail)? {
                records.push(read_record(item, &self.file_path)?);
            }
        }
        let mut ids = std::collections::HashSet::new();
        for record in &records {
            if record.id.trim().is_empty() || record.repo.trim().is_empty() {
                return Err(UpdateError::failed(format!(
                    "'{}' has an installed-plugin record with a missing id or repo.",
                    self.file_path.display()
                )));
            }
            if !has_valid_install_id(&record.id) {
                return Err(UpdateError::failed(format!(
                    "'{}' has an installed-plugin record with an id that is not a plugin id.",
                    self.file_path.display()
                )));
            }
            if !ids.insert(record.id.to_lowercase()) {
                return Err(UpdateError::failed(format!(
                    "'{}' has more than one record for '{}'.",
                    self.file_path.display(),
                    record.id
                )));
            }
        }
        self.records = records;
        Ok(true)
    }

    /// Writes the records through a temp file (owner-only on Unix).
    pub fn save(&self) -> Result<(), UpdateError> {
        let document = ObjectBuilder::new()
            .member("schemaVersion", Out::Int(i64::from(CURRENT_SCHEMA_VERSION)))
            .member(
                "plugins",
                Out::Array(self.records.iter().map(record_json).collect()),
            )
            .build();
        fsutil::write_via_tmp(&self.file_path, json::to_pretty(&document).as_bytes(), true).map_err(
            |error| {
                UpdateError::io(
                    format!(
                        "'{}' could not be written: {error}",
                        self.file_path.display()
                    ),
                    error,
                )
            },
        )
    }

    /// The record for `id` (ignoring case).
    pub fn find(&self, id: &str) -> Option<&InstalledPluginRecord> {
        self.records
            .iter()
            .find(|record| record.id.eq_ignore_ascii_case(id))
    }

    /// Replaces the record with the same id, or adds it.
    pub fn upsert(&mut self, record: InstalledPluginRecord) {
        match self
            .records
            .iter_mut()
            .find(|candidate| candidate.id.eq_ignore_ascii_case(&record.id))
        {
            Some(existing) => *existing = record,
            None => self.records.push(record),
        }
    }
}
