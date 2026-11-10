//! The small plugin rules: compatibility with the installed client, the
//! archive content policy, the `.sha256` asset, GitHub release addresses and
//! the icon.

use super::manifest::{HostVersion, PluginManifest};
use crate::updates::{ClientVersionResolution, ExtractedFileRecord, UpdateError, is_sha256};
use crate::url::{WebUrl, escape_data_string};

// --- Compatibility ------------------------------------------------------------

/// The reason text for a client that is not installed.
pub const CLIENT_NOT_INSTALLED: &str = "client not installed";

/// How a compatible note begins, so a card can leave it unsaid.
pub const COMPATIBLE_PREFIX: &str = "Compatible with OpenAC ";

/// A row's compatibility line.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CompatibilityDescription {
    /// The text.
    pub text: String,
    /// Whether it should read as a warning.
    pub is_warning: bool,
}

/// Why a plugin cannot run with the installed client (min, max, skip, and
/// the plugin API versions that client loads), or `None`. Every plugin runs
/// in every session, windowed or not, so there is no per-launch host gate.
/// The plugin API range is the client's own ([`ClientVersionResolution::plugin_api`]):
/// a client whose range is unknown is a reason too, never a silent yes.
pub fn version_only_reason(
    manifest: &PluginManifest,
    client: Option<&ClientVersionResolution>,
) -> Option<String> {
    let Some(installed) = client
        .and_then(|client| client.version.as_ref())
        .and_then(|version| HostVersion::from_informational(version.value()))
    else {
        return Some(CLIENT_NOT_INSTALLED.to_owned());
    };
    if let Some(min) = manifest.min_host_version
        && installed < min
    {
        return Some(format!(
            "requires OpenAC {min} or newer (this is {installed})"
        ));
    }
    if let Some(max) = manifest.max_host_version
        && installed > max
    {
        return Some(format!("supports OpenAC up to {max} (this is {installed})"));
    }
    if manifest.skip_host_versions.contains(&installed) {
        return Some(format!("is marked broken on OpenAC {installed}"));
    }
    let wanted = manifest.api_version;
    match client.and_then(|client| client.plugin_api) {
        Some(range) if range.contains(wanted) => None,
        Some(range) => Some(format!(
            "needs plugin API {wanted}; the installed client {installed} supports plugin API \
             {range}"
        )),
        None => Some(format!(
            "needs plugin API {wanted}; the installed client {installed} does not say which \
             plugin API versions it supports (update the client)"
        )),
    }
}

/// The Discover/Installed row's line: never blank; a version failure is a
/// warning, a missing client is not.
pub fn describe(
    manifest: &PluginManifest,
    client: Option<&ClientVersionResolution>,
) -> CompatibilityDescription {
    let Some(version) = client.and_then(|client| client.version.as_ref()) else {
        return CompatibilityDescription {
            text: "Client not installed".into(),
            is_warning: false,
        };
    };
    if let Some(reason) = version_only_reason(manifest, client) {
        return CompatibilityDescription {
            text: reason,
            is_warning: true,
        };
    }
    CompatibilityDescription {
        text: format!("{COMPATIBLE_PREFIX}{version}"),
        is_warning: false,
    }
}

/// A reason without its trailing `(this is x)`, for a card that already
/// shows versions.
pub fn without_client_version(reason: &str) -> String {
    match reason.rfind(" (this is ") {
        Some(cut) if cut > 0 && reason.ends_with(')') => reason[..cut].to_owned(),
        _ => reason.to_owned(),
    }
}

// --- Content policy ------------------------------------------------------------

/// The icon's file name at the plugin root.
pub use ac_plugin_icon::ICON_FILE_NAME;

/// The file types a plugin package may hold: its module, data, art and
/// fonts (the plugin package format). No native code of any kind.
pub const ALLOWED_EXTENSIONS: [&str; 8] = [
    ".wasm", ".json", ".xml", ".txt", ".md", ".png", ".ttf", ".otf",
];

fn extension(path: &str) -> String {
    let name = path.rsplit('/').next().unwrap_or(path);
    match name.rfind('.') {
        Some(dot) if dot + 1 < name.len() => name[dot..].to_lowercase(),
        _ => String::new(),
    }
}

/// The manifest's `entry` as an extracted file's path: `/`-separated, with
/// `.` segments dropped (the parser already refused anything leaving the
/// folder).
pub fn entry_path(entry: &str) -> String {
    entry
        .split(['/', '\\'])
        .filter(|segment| !segment.is_empty() && *segment != ".")
        .collect::<Vec<_>>()
        .join("/")
}

/// The release contract's content rules for an extracted plugin: allowed
/// file types only, `plugin.json` at the root, the declared entry module
/// present, and the icon named exactly `icon.png`.
pub fn validate_plugin_content(
    files: &[ExtractedFileRecord],
    entry: &str,
) -> Result<(), UpdateError> {
    let entry = entry_path(entry);
    let mut manifest_at_root = false;
    let mut entry_found = false;
    for file in files {
        if !ALLOWED_EXTENSIONS.contains(&extension(&file.path).as_str()) {
            return Err(UpdateError::failed(format!(
                "Plugin file '{}' has a disallowed extension.",
                file.path
            )));
        }
        if file.path == "plugin.json" {
            manifest_at_root = true;
        }
        if file.path == entry {
            entry_found = true;
        }
        if file.path.eq_ignore_ascii_case(ICON_FILE_NAME) && file.path != ICON_FILE_NAME {
            return Err(UpdateError::failed(format!(
                "Plugin file '{}' must be named '{ICON_FILE_NAME}' exactly.",
                file.path
            )));
        }
    }
    if !manifest_at_root {
        return Err(UpdateError::failed(
            "The plugin archive has no 'plugin.json' at its root.",
        ));
    }
    if !entry_found {
        return Err(UpdateError::failed(format!(
            "The plugin archive is missing its declared entry module '{entry}'."
        )));
    }
    Ok(())
}

// --- The .sha256 asset ------------------------------------------------------------

/// A release's `.sha256` asset: `shasum -a 256` output, hash first and an
/// optional file name (binary-mode `*` stripped).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PluginSha256File {
    /// The hash, lowercase.
    pub sha256: String,
    /// The file name it names, if any.
    pub file_name: Option<String>,
}

impl PluginSha256File {
    /// Reads the asset.
    pub fn parse(content: &str) -> Result<Self, UpdateError> {
        let tokens: Vec<&str> = content.split_whitespace().collect();
        match tokens.first() {
            Some(hash) if is_sha256(hash) => Ok(Self {
                sha256: hash.to_ascii_lowercase(),
                file_name: tokens
                    .get(1)
                    .map(|name| name.trim_start_matches('*').to_owned()),
            }),
            _ => Err(UpdateError::failed("The plugin SHA-256 file is malformed.")),
        }
    }

    /// Refuses a file name other than `expected_zip_name` (an absent name
    /// is accepted).
    pub fn require_matches(&self, expected_zip_name: &str) -> Result<(), UpdateError> {
        match &self.file_name {
            Some(name) if name != expected_zip_name => Err(UpdateError::failed(format!(
                "The plugin SHA-256 file names '{name}', expected '{expected_zip_name}'."
            ))),
            _ => Ok(()),
        }
    }
}

// --- GitHub release addresses ------------------------------------------------------

/// Builds and reads the `github.com/<owner>/<name>/releases/...` addresses
/// the plugin pipeline uses. No API host is ever contacted.
pub mod github {
    use super::{WebUrl, escape_data_string};

    const HOST: &str = "github.com";

    fn is_valid_owner(owner: &str) -> bool {
        (1..=39).contains(&owner.chars().count())
            && owner.split('-').all(|part| {
                !part.is_empty() && part.bytes().all(|byte| byte.is_ascii_alphanumeric())
            })
    }

    fn is_valid_name(name: &str) -> bool {
        name != "."
            && name != ".."
            && (1..=100).contains(&name.len())
            && name
                .bytes()
                .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'.' | b'_' | b'-'))
    }

    /// `owner/name` with a valid GitHub user name and repository name.
    pub fn is_valid_repo(repo: &str) -> bool {
        let parts: Vec<&str> = repo.split('/').collect();
        parts.len() == 2 && is_valid_owner(parts[0]) && is_valid_name(parts[1])
    }

    fn build(repo: &str, path: &str) -> Option<WebUrl> {
        if !is_valid_repo(repo) {
            return None;
        }
        let (owner, name) = repo.split_once('/')?;
        WebUrl::parse(&format!(
            "https://{HOST}/{}/{}/{path}",
            escape_data_string(owner),
            escape_data_string(name)
        ))
    }

    /// The asset of the latest (non-pre-release) release.
    pub fn latest_asset(repo: &str, asset: &str) -> Option<WebUrl> {
        build(
            repo,
            &format!("releases/latest/download/{}", escape_data_string(asset)),
        )
    }

    /// The asset of the release tagged `tag`.
    pub fn tagged_asset(repo: &str, tag: &str, asset: &str) -> Option<WebUrl> {
        if tag.trim().is_empty() {
            return None;
        }
        build(
            repo,
            &format!(
                "releases/download/{}/{}",
                escape_data_string(tag),
                escape_data_string(asset)
            ),
        )
    }

    /// The repository's releases Atom feed (the only way to see
    /// pre-releases without the API).
    pub fn releases_feed(repo: &str) -> Option<WebUrl> {
        build(repo, "releases.atom")
    }

    /// Reads a typed `https://github.com/<owner>/<name>` address: nothing
    /// else (no query, fragment, credentials, other port or extra path).
    pub fn try_parse_repo_url(value: &str) -> Option<String> {
        if value.trim().is_empty() {
            return None;
        }
        let url = WebUrl::parse(value)?;
        if !url.is_https()
            || !url.host().eq_ignore_ascii_case(HOST)
            || url.port() != Some(443)
            || !url.query().is_empty()
            || !url.fragment().is_empty()
            || !url.user_info().is_empty()
        {
            return None;
        }
        let segments = url.segments();
        if segments.len() != 2 || !is_valid_owner(segments[0]) || !is_valid_name(segments[1]) {
            return None;
        }
        Some(format!("{}/{}", segments[0], segments[1]))
    }
}

// --- The icon ----------------------------------------------------------------------

pub use ac_plugin_icon::{ICON_EXTENT, ICON_MAXIMUM_BYTES};

/// Checks a plugin icon's PNG container (`ac_plugin_icon::validate_icon`,
/// the rules the client also applies before it shows the icon), as an
/// install failure.
pub fn validate_icon(bytes: &[u8]) -> Result<(), UpdateError> {
    ac_plugin_icon::validate_icon(bytes).map_err(|error| UpdateError::failed(error.to_string()))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn repo_shapes() {
        assert!(github::is_valid_repo("eriknihlen/openac-plugins"));
        assert!(!github::is_valid_repo("-bad/name"));
        assert!(!github::is_valid_repo("owner/.."));
        assert!(!github::is_valid_repo("owner/name/extra"));
        assert_eq!(
            github::try_parse_repo_url("https://github.com/owner/name").as_deref(),
            Some("owner/name")
        );
        assert_eq!(
            github::try_parse_repo_url("https://github.com/owner/name?x=1"),
            None
        );
        assert_eq!(
            github::try_parse_repo_url("http://github.com/owner/name"),
            None
        );
        assert_eq!(github::try_parse_repo_url("https://github.com/owner"), None);
        assert_eq!(
            github::tagged_asset("o/r", "v1.0.0", "plugin.json")
                .unwrap()
                .to_string(),
            "https://github.com/o/r/releases/download/v1.0.0/plugin.json"
        );
    }
}
