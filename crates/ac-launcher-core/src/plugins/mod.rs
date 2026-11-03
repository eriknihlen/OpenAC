//! Plugin management: the curated list, `plugin.json` (read by the
//! client's own parser), compatibility with the installed client, the
//! installed inventory (managed, direct and bundled), release resolution on
//! GitHub (stable and beta `wasm-v` and `wasm-<plugin id>-v` releases), and the installer. Plugins
//! are downloaded and unzipped, never loaded or run, and never enabled for
//! any character by an install.

mod catalog;
mod index;
mod installer;
mod inventory;
mod manifest;
mod record_store;
mod release;
mod rules;

pub use catalog::{
    CURRENT_CATALOG_SCHEMA_VERSION, OPENAC_CLIENT, PLUGIN_LIST_ASSET, PLUGIN_LIST_REPO,
    PluginBlock, PluginCatalog, PluginCatalogEntry, THIS_CLIENT,
};
pub use index::{
    CURRENT_INDEX_SCHEMA_VERSION, PLUGIN_INDEX_ASSET, PLUGIN_INDEX_TAG, PluginIndex,
    PluginIndexEntry,
};
pub use installer::{
    AfterOldCodeSetAside, LocalPluginZipPreview, PACKAGED_FILES_REFUSAL,
    PERMISSION_VOCABULARY_REFUSAL, PERMISSIONS_CHANGED_REFUSAL, PLUGIN_WRITE_BUSY_REFUSAL,
    PLUGIN_WRITE_LOCK_FILE_NAME, PluginInstallResult, PluginInstaller, SESSION_LEASE_REFUSAL,
    UPDATE_IN_PROGRESS_REFUSAL, contract_limits, permissions_match,
};
pub use inventory::{
    InstalledPluginInfo, InstalledPluginSource, PluginInventory, direct_install_refusal,
    evaluate_version_compatibility, find_block_reason,
};
pub use manifest::{
    API_CURRENT, API_MINIMUM, ApiVersion, HostVersion, InstallRules, ManifestError,
    PERMISSIONS_VERSION, Permission, PermissionDeclaration, PluginManifest, WASM_TAG_PREFIX,
    has_valid_install_id, is_built_for_csharp, is_newer_permission_vocabulary, is_well_formed_id,
    plugin_release_tag, plugin_tag_prefix, release_tag,
};
pub use record_store::{
    InstalledPluginRecord, InstalledPluginRecordStore, PendingPluginInstall, PluginInstallSource,
    PluginReleaseChannel,
};
pub use release::{
    FEED_MAXIMUM_BYTES, MAXIMUM_DOCUMENT_BYTES, MAXIMUM_REFUSED_CANDIDATES,
    MAXIMUM_REPOSITORY_PLUGINS, PluginFeedRelease, PluginReleaseCandidates, PluginReleaseClient,
    PluginReleaseDocument, PluginReleaseFeedError, PluginReleaseFetchResult,
    PluginReleaseResolution, PluginReleaseResolveResult, PluginReleaseResolver,
    PluginRepositoryResolution, feed_tags, highest_prerelease, highest_stable, plugin_releases,
    wasm_releases,
};
pub use rules::{
    ALLOWED_EXTENSIONS, CLIENT_NOT_INSTALLED, COMPATIBLE_PREFIX, CompatibilityDescription,
    ICON_EXTENT, ICON_FILE_NAME, ICON_MAXIMUM_BYTES, PluginSha256File, describe, entry_path,
    github, validate_icon, validate_plugin_content, version_only_reason, without_client_version,
};
