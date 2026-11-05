//! The curated plugin list: the plugins Discover offers and the blocked
//! plugin versions.
//!
//! This client reads its own list, [`PLUGIN_LIST_ASSET`], published beside
//! OpenAC's `plugins.json` in the same release of the list repository.
//! OpenAC's list reader refuses any member it does not
//! know, so its list never carries a `client` field; this one does. Each
//! entry may name the client it is built for in `client` (`"openac"`,
//! OpenAC's C# client, when absent; `"openac-rust"`, this client), and
//! Discover offers only this client's entries. A Rust port keeps its C#
//! plugin id, so an id may be listed once per client. A block names a client
//! too, or covers every client when it names none.

use std::collections::HashSet;

use ac_host_core::json::{self as host_json, Node, ObjectReader};

use super::rules::github;
use crate::json::{self, reject_case_insensitive_duplicates};
use crate::updates::{LauncherVersion, UpdateError};
use crate::url::WebUrl;

/// The list repository.
pub const PLUGIN_LIST_REPO: &str = "eriknihlen/openac-plugins";

/// This client's list: a release asset of its own beside OpenAC's
/// `plugins.json`, which this client never reads.
pub const PLUGIN_LIST_ASSET: &str = "plugins-rust.json";

/// The schema version this reader understands.
pub const CURRENT_CATALOG_SCHEMA_VERSION: i32 = 1;

/// The `client` value naming OpenAC's C# client (also what an entry without
/// `client` is for).
pub const OPENAC_CLIENT: &str = "openac";

/// The `client` value naming this client.
pub const THIS_CLIENT: &str = "openac-rust";

/// A blocked plugin id and the versions it covers (`*` for all).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PluginBlock {
    /// The plugin id.
    pub id: String,
    /// The versions blocked.
    pub versions: Vec<String>,
    /// Why.
    pub reason: String,
    /// The client whose builds it blocks; `None` blocks every client's.
    pub client: Option<String>,
}

impl PluginBlock {
    /// Whether the block covers `version`.
    pub fn matches(&self, version: &str) -> bool {
        self.versions
            .iter()
            .any(|candidate| candidate == "*" || candidate == version)
    }

    /// Whether the block covers this client's builds.
    pub fn applies_to_this_client(&self) -> bool {
        self.client
            .as_deref()
            .is_none_or(|client| client == THIS_CLIENT)
    }
}

/// One listed plugin.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PluginCatalogEntry {
    /// The plugin id.
    pub id: String,
    /// Its name.
    pub name: String,
    /// Its author.
    pub author: String,
    /// One line about it.
    pub description: String,
    /// `owner/name` on GitHub.
    pub repo: String,
    /// The client it is built for ([`OPENAC_CLIENT`] when the entry names
    /// none).
    pub client: String,
}

impl PluginCatalogEntry {
    /// Whether it is built for this client.
    pub fn is_for_this_client(&self) -> bool {
        self.client == THIS_CLIENT
    }
}

/// The published list.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PluginCatalog {
    /// 1.
    pub schema_version: i32,
    /// The listed plugins.
    pub plugins: Vec<PluginCatalogEntry>,
    /// The blocked plugins.
    pub blocked: Vec<PluginBlock>,
}

fn invalid(error: impl std::fmt::Display) -> UpdateError {
    UpdateError::failed(format!("The plugin list is invalid: {error}"))
}

fn text(object: &mut ObjectReader<'_>, name: &str) -> Result<Option<String>, UpdateError> {
    match object.take(name) {
        Some(node) => text_node(node),
        None => Ok(None),
    }
}

fn text_node(node: &Node) -> Result<Option<String>, UpdateError> {
    host_json::string_or_null(node).map_err(invalid)
}

fn require_field(value: Option<String>, field: &str) -> Result<String, UpdateError> {
    match value {
        Some(text) if !text.trim().is_empty() => Ok(text),
        _ => Err(UpdateError::failed(format!(
            "The plugin list is missing '{field}'."
        ))),
    }
}

fn items(node: Option<&Node>) -> Result<Option<&[Node]>, UpdateError> {
    match node {
        Some(node) if !host_json::is_null(node) => {
            host_json::array_items(node).map(Some).map_err(invalid)
        }
        _ => Ok(None),
    }
}

impl PluginCatalog {
    /// The production list address: [`PLUGIN_LIST_ASSET`] of the list
    /// repository's latest release.
    pub fn production_list_uri() -> WebUrl {
        github::latest_asset(PLUGIN_LIST_REPO, PLUGIN_LIST_ASSET)
            .unwrap_or_else(|| unreachable!("the list repository name is valid"))
    }

    /// Reads a list strictly: unknown members, members repeated ignoring
    /// case, another schema version, a missing `plugins` array, blank
    /// fields (a `client` that is present included), an invalid repository
    /// or a plugin id repeated for the same client (ignoring case) are
    /// refused. An empty `plugins` array is a list that has published
    /// nothing yet.
    pub fn parse(json_text: &str) -> Result<Self, UpdateError> {
        let root = host_json::parse(json_text.as_bytes()).map_err(invalid)?;
        if json::depth(&root) > 16 {
            return Err(invalid("nested too deeply"));
        }
        reject_case_insensitive_duplicates(&root).map_err(|_| {
            UpdateError::failed("Duplicate JSON property is not allowed in the plugin list.")
        })?;
        if host_json::is_null(&root) {
            return Err(UpdateError::failed("The plugin list is empty."));
        }
        let mut object = ObjectReader::new(&root).map_err(invalid)?;
        let schema_version = match object.take("schemaVersion") {
            Some(node) => host_json::int32(node).map_err(invalid)?,
            None => 0,
        };
        let plugin_nodes = object.take("plugins");
        let blocked_nodes = object.take("blocked");
        object.finish().map_err(invalid)?;
        if schema_version != CURRENT_CATALOG_SCHEMA_VERSION {
            return Err(UpdateError::failed(format!(
                "Plugin list schema version {schema_version} is not supported."
            )));
        }

        let plugin_items = items(plugin_nodes)?
            .ok_or_else(|| UpdateError::failed("The plugin list is missing its plugins array."))?;
        let mut plugins = Vec::with_capacity(plugin_items.len());
        let mut ids = HashSet::new();
        for item in plugin_items {
            let mut entry = ObjectReader::new(item).map_err(invalid)?;
            let id = text(&mut entry, "id")?;
            let name = text(&mut entry, "name")?;
            let author = text(&mut entry, "author")?;
            let description = text(&mut entry, "description")?;
            let repo = text(&mut entry, "repo")?;
            let client = entry.take("client").map(text_node);
            entry.finish().map_err(invalid)?;
            let id = require_field(id, "plugins[].id")?;
            let name = require_field(name, "plugins[].name")?;
            let author = require_field(author, "plugins[].author")?;
            let description = require_field(description, "plugins[].description")?;
            let repo = require_field(repo, "plugins[].repo")?;
            let client = match client {
                None => OPENAC_CLIENT.to_owned(),
                Some(value) => require_field(value?, "plugins[].client")?,
            };
            if !github::is_valid_repo(&repo) {
                return Err(UpdateError::failed(format!(
                    "Plugin list entry '{id}' has an invalid repo '{repo}'."
                )));
            }
            if !ids.insert((id.to_lowercase(), client.to_lowercase())) {
                return Err(UpdateError::failed(format!(
                    "Plugin list contains a duplicate id '{id}'."
                )));
            }
            plugins.push(PluginCatalogEntry {
                id,
                name,
                author,
                description,
                repo,
                client,
            });
        }

        let mut blocked = Vec::new();
        for item in items(blocked_nodes)?.unwrap_or_default() {
            let mut block = ObjectReader::new(item).map_err(invalid)?;
            let id = text(&mut block, "id")?;
            let reason = text(&mut block, "reason")?;
            let versions = match block.take("versions") {
                Some(node) if !host_json::is_null(node) => Some(
                    host_json::array_items(node)
                        .map_err(invalid)?
                        .iter()
                        .map(|version| host_json::string_or_null(version).map_err(invalid))
                        .collect::<Result<Vec<_>, _>>()?,
                ),
                _ => None,
            };
            let client = block.take("client").map(text_node);
            block.finish().map_err(invalid)?;
            let id = require_field(id, "blocked[].id")?;
            let reason = require_field(reason, "blocked[].reason")?;
            let client = match client {
                None => None,
                Some(value) => Some(require_field(value?, "blocked[].client")?),
            };
            let versions = versions
                .filter(|versions| !versions.is_empty())
                .ok_or_else(|| {
                    UpdateError::failed(format!("Blocked entry '{id}' has no versions."))
                })?;
            if versions
                .iter()
                .any(|version| version.as_deref().is_none_or(|v| v.trim().is_empty()))
            {
                return Err(UpdateError::failed(format!(
                    "Blocked entry '{id}' has a blank version."
                )));
            }
            blocked.push(PluginBlock {
                id,
                versions: versions.into_iter().flatten().collect(),
                reason,
                client,
            });
        }
        Ok(Self {
            schema_version,
            plugins,
            blocked,
        })
    }

    /// The entries built for this client, in list order.
    pub fn entries_for_this_client(&self) -> impl Iterator<Item = &PluginCatalogEntry> {
        self.plugins
            .iter()
            .filter(|entry| entry.is_for_this_client())
    }

    /// The blocks that cover this client's builds.
    pub fn blocks_for_this_client(&self) -> impl Iterator<Item = &PluginBlock> {
        self.blocked
            .iter()
            .filter(|block| block.applies_to_this_client())
    }

    /// The reason a plugin release of this client is blocked, or `None`.
    /// With no version, any block on the id applies.
    pub fn block_reason(&self, id: &str, version: Option<&LauncherVersion>) -> Option<&str> {
        self.blocks_for_this_client()
            .find(|block| {
                block.id.eq_ignore_ascii_case(id)
                    && version.is_none_or(|version| block.matches(version.value()))
            })
            .map(|block| block.reason.as_str())
    }

    /// Whether a plugin release is blocked.
    pub fn is_blocked(&self, id: &str, version: Option<&LauncherVersion>) -> bool {
        self.block_reason(id, version).is_some()
    }

    /// Whether every version of the id is blocked (`*`).
    pub fn is_blocked_for_all_versions(&self, id: &str) -> bool {
        self.blocks_for_this_client().any(|block| {
            block.id.eq_ignore_ascii_case(id) && block.versions.iter().any(|v| v == "*")
        })
    }
}
