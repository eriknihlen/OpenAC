//! The plugin index: an optional file a repository that publishes several
//! plugins attaches to a fixed release, naming each plugin's latest stable
//! and beta release tags.
//!
//! A repository's releases feed lists only its latest ten releases, so one
//! plugin's releases can push another's off it. The index points at tags
//! and nothing more: the launcher still reads each tag's own `plugin.json`
//! and checks its id and version, so a stale index can at worst offer an
//! older release.

use std::collections::HashSet;

use ac_host_core::json::{self as host_json, Node, ObjectReader};

use super::manifest::is_well_formed_id;
use super::release::{PluginFeedRelease, plugin_release};
use crate::json::{self, ObjectBuilder, Out, reject_case_insensitive_duplicates};
use crate::updates::UpdateError;

/// The release whose asset is the index. It cannot be a plugin's tag: a
/// plugin id always contains a dot.
pub const PLUGIN_INDEX_TAG: &str = "wasm-index";

/// The index's asset name.
pub const PLUGIN_INDEX_ASSET: &str = "plugins-index.json";

/// The schema version this reader understands.
pub const CURRENT_INDEX_SCHEMA_VERSION: i32 = 1;

/// One plugin's latest releases.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PluginIndexEntry {
    /// The plugin id.
    pub id: String,
    /// Its latest stable release tag.
    pub stable: Option<String>,
    /// Its latest pre-release tag.
    pub beta: Option<String>,
}

/// What [`PluginIndex::set_release`] did.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum IndexReleaseUpdate {
    /// The entry now names the tag.
    Set,
    /// The entry already names this release of a higher version, kept.
    KeptNewer(String),
}

/// A repository's plugin index.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PluginIndex {
    /// 1.
    pub schema_version: i32,
    /// The plugins, in the order the file lists them.
    pub plugins: Vec<PluginIndexEntry>,
}

impl Default for PluginIndex {
    fn default() -> Self {
        Self::new()
    }
}

fn invalid(error: impl std::fmt::Display) -> UpdateError {
    UpdateError::failed(format!("The plugin index is invalid: {error}"))
}

/// Checks `tag` is a release of plugin `id`, a pre-release exactly when
/// `pre_release`.
fn check_tag(id: &str, tag: &str, pre_release: bool) -> Result<(), UpdateError> {
    let release = plugin_release(tag, id).ok_or_else(|| {
        invalid(format!(
            "'{tag}' is not a release tag of '{id}' (wasm-{id}-v<version> or wasm-v<version>)"
        ))
    })?;
    if release.version.is_pre_release() != pre_release {
        let field = if pre_release { "beta" } else { "stable" };
        let want = if pre_release {
            "a pre-release"
        } else {
            "not a pre-release"
        };
        return Err(invalid(format!("'{id}' {field} '{tag}' must be {want}")));
    }
    Ok(())
}

fn tag_member(object: &mut ObjectReader<'_>, name: &str) -> Result<Option<String>, UpdateError> {
    match object.take(name) {
        Some(node) => match host_json::string_or_null(node).map_err(invalid)? {
            Some(text) if text.is_empty() => Err(invalid(format!("'{name}' is blank"))),
            value => Ok(value),
        },
        None => Ok(None),
    }
}

fn entry_of(item: &Node) -> Result<PluginIndexEntry, UpdateError> {
    let mut object = ObjectReader::new(item).map_err(invalid)?;
    let id = match object.take("id") {
        Some(node) => host_json::string_or_null(node).map_err(invalid)?,
        None => None,
    };
    let stable = tag_member(&mut object, "stable")?;
    let beta = tag_member(&mut object, "beta")?;
    object.finish().map_err(invalid)?;
    let id = id.ok_or_else(|| invalid("an entry is missing 'id'"))?;
    if !is_well_formed_id(&id) {
        return Err(invalid(format!("'{id}' is not a plugin id")));
    }
    if stable.is_none() && beta.is_none() {
        return Err(invalid(format!("'{id}' names no release")));
    }
    if let Some(tag) = &stable {
        check_tag(&id, tag, false)?;
    }
    if let Some(tag) = &beta {
        check_tag(&id, tag, true)?;
    }
    Ok(PluginIndexEntry { id, stable, beta })
}

impl PluginIndex {
    /// An index naming no plugin yet.
    pub fn new() -> Self {
        Self {
            schema_version: CURRENT_INDEX_SCHEMA_VERSION,
            plugins: Vec::new(),
        }
    }

    /// Reads an index strictly: unknown members, members repeated ignoring
    /// case, another schema version, a missing `plugins` array, an entry
    /// without a well-formed id or naming no release, an id repeated
    /// (ignoring case), and a tag that is not that plugin's release (a
    /// `stable` pre-release or a `beta` that is not one included) are all
    /// refused.
    pub fn parse(json_text: &str) -> Result<Self, UpdateError> {
        let root = host_json::parse(json_text.as_bytes()).map_err(invalid)?;
        if json::depth(&root) > 16 {
            return Err(invalid("nested too deeply"));
        }
        reject_case_insensitive_duplicates(&root).map_err(|_| invalid("a member is repeated"))?;
        if host_json::is_null(&root) {
            return Err(invalid("it is empty"));
        }
        let mut object = ObjectReader::new(&root).map_err(invalid)?;
        let schema_version = match object.take("schemaVersion") {
            Some(node) => host_json::int32(node).map_err(invalid)?,
            None => 0,
        };
        let plugin_nodes = object.take("plugins");
        object.finish().map_err(invalid)?;
        if schema_version != CURRENT_INDEX_SCHEMA_VERSION {
            return Err(invalid(format!(
                "schema version {schema_version} is not supported"
            )));
        }
        let items = match plugin_nodes {
            Some(node) if !host_json::is_null(node) => {
                host_json::array_items(node).map_err(invalid)?
            }
            _ => return Err(invalid("it is missing its plugins array")),
        };
        let mut plugins = Vec::with_capacity(items.len());
        let mut ids = HashSet::new();
        for item in items {
            let entry = entry_of(item)?;
            if !ids.insert(entry.id.to_lowercase()) {
                return Err(invalid(format!("'{}' is listed twice", entry.id)));
            }
            plugins.push(entry);
        }
        Ok(Self {
            schema_version,
            plugins,
        })
    }

    /// Reads an index file's bytes as [`Self::parse`] does, after a UTF-8
    /// byte order mark if there is one; bytes that are not UTF-8 are
    /// refused.
    pub fn parse_bytes(bytes: &[u8]) -> Result<Self, UpdateError> {
        let text = std::str::from_utf8(bytes.strip_prefix(b"\xEF\xBB\xBF").unwrap_or(bytes))
            .map_err(|_| invalid("it is not UTF-8"))?;
        Self::parse(text)
    }

    /// Plugin `id`'s entry (ignoring case).
    pub fn entry(&self, id: &str) -> Option<&PluginIndexEntry> {
        self.plugins
            .iter()
            .find(|entry| entry.id.eq_ignore_ascii_case(id))
    }

    /// The releases the index names for plugin `id`: stable, then beta.
    pub fn releases_for(&self, id: &str) -> Vec<PluginFeedRelease> {
        let Some(entry) = self.entry(id) else {
            return Vec::new();
        };
        [&entry.stable, &entry.beta]
            .into_iter()
            .flatten()
            .filter_map(|tag| plugin_release(tag, &entry.id))
            .collect()
    }

    /// Names `tag` as plugin `id`'s latest stable release, or its latest
    /// beta when `tag` is a pre-release, keeping the other; an id not yet
    /// listed is added at the end. A release of a higher version already
    /// named there is kept (a hotfix of an older line never moves the index
    /// back), and the result says so.
    pub fn set_release(&mut self, id: &str, tag: &str) -> Result<IndexReleaseUpdate, UpdateError> {
        if !is_well_formed_id(id) {
            return Err(invalid(format!("'{id}' is not a plugin id")));
        }
        let release = plugin_release(tag, id)
            .ok_or_else(|| invalid(format!("'{tag}' is not a release tag of '{id}'")))?;
        let index = match self
            .plugins
            .iter()
            .position(|entry| entry.id.eq_ignore_ascii_case(id))
        {
            Some(index) => index,
            None => {
                self.plugins.push(PluginIndexEntry {
                    id: id.to_owned(),
                    stable: None,
                    beta: None,
                });
                self.plugins.len() - 1
            }
        };
        let entry = &mut self.plugins[index];
        let listed_id = entry.id.clone();
        let named = if release.version.is_pre_release() {
            &mut entry.beta
        } else {
            &mut entry.stable
        };
        if let Some(current) = named.as_deref()
            && plugin_release(current, &listed_id)
                .is_some_and(|current| current.version > release.version)
        {
            return Ok(IndexReleaseUpdate::KeptNewer(current.to_owned()));
        }
        *named = Some(tag.to_owned());
        Ok(IndexReleaseUpdate::Set)
    }

    /// The index as the file to publish, indented by two spaces.
    pub fn to_json(&self) -> String {
        let plugins = self
            .plugins
            .iter()
            .map(|entry| {
                ObjectBuilder::new()
                    .member("id", Out::str(&entry.id))
                    .optional("stable", entry.stable.as_deref().map(Out::str))
                    .optional("beta", entry.beta.as_deref().map(Out::str))
                    .build()
            })
            .collect();
        let root = ObjectBuilder::new()
            .member("schemaVersion", Out::Int(i64::from(self.schema_version)))
            .member("plugins", Out::Array(plugins))
            .build();
        let mut text = json::to_pretty(&root);
        text.push('\n');
        text
    }
}
