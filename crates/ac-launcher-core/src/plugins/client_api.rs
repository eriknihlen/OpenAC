//! Which plugin API versions the installed client loads. The range belongs
//! to the client, not to the launcher: a released client carries it in
//! `plugin-api.json` at its folder's root (`openac plugin-api` prints it and
//! the release packages that output), and a client released before that
//! file is looked up in a frozen table of released versions. So a new plugin
//! API version changes the client alone, and the launcher stays as it is.

use std::io::Read as _;
use std::path::Path;

use ac_host_core::json::{self as host_json, ObjectReader};

use ac_plugin_version::ApiVersion;
pub use ac_plugin_version::{ApiRange, PLUGIN_API_FILE, PLUGIN_API_SCHEMA_VERSION};

use crate::updates::LauncherVersion;

/// The most a `plugin-api.json` may hold; anything larger is not one.
pub const PLUGIN_API_FILE_MAXIMUM_BYTES: u64 = 4096;

/// The plugin API every released client before `plugin-api.json` loaded
/// first: 1.0, unchanged in all of them.
const RELEASED_MINIMUM: ApiVersion = ApiVersion::new(1, 0);

/// Every client released without `plugin-api.json`, with the newest plugin
/// API it loads (each loads from 1.0). Frozen: every later client carries
/// the file, so this table never grows.
pub const CLIENTS_RELEASED_WITHOUT_THE_FILE: [(&str, ApiVersion); 17] = [
    ("0.1.0", ApiVersion::new(1, 0)),
    ("0.1.1", ApiVersion::new(1, 0)),
    ("0.1.2", ApiVersion::new(1, 0)),
    ("0.1.3", ApiVersion::new(1, 1)),
    ("0.1.4", ApiVersion::new(1, 1)),
    ("0.1.5", ApiVersion::new(1, 1)),
    ("0.1.6", ApiVersion::new(1, 2)),
    ("0.1.7", ApiVersion::new(1, 2)),
    ("0.1.8", ApiVersion::new(1, 2)),
    ("0.1.9", ApiVersion::new(1, 3)),
    ("0.1.10", ApiVersion::new(1, 5)),
    ("0.1.11", ApiVersion::new(1, 8)),
    ("0.1.12", ApiVersion::new(1, 8)),
    ("0.2.0", ApiVersion::new(1, 8)),
    ("0.2.1", ApiVersion::new(1, 8)),
    ("0.2.2", ApiVersion::new(1, 9)),
    ("0.2.3", ApiVersion::new(1, 11)),
];

/// The range a `plugin-api.json` says: schema version 1 with `minimum` and
/// `current` (`"MAJOR.MINOR"`, `minimum` not above `current`); members it
/// does not know are ignored. `None` for anything else.
pub fn parse_plugin_api_file(bytes: &[u8]) -> Option<ApiRange> {
    let root = host_json::parse(bytes).ok()?;
    let mut object = ObjectReader::new(&root).ok()?;
    let schema = host_json::uint32(object.required("schemaVersion").ok()?).ok()?;
    if schema != PLUGIN_API_SCHEMA_VERSION {
        return None;
    }
    let version = |node| {
        host_json::string_or_null(node)
            .ok()
            .flatten()
            .and_then(|text| ApiVersion::parse(&text))
    };
    let minimum = version(object.required("minimum").ok()?)?;
    let current = version(object.required("current").ok()?)?;
    minimum
        .is_at_most(current)
        .then(|| ApiRange::new(minimum, current))
}

/// The range in `<directory>/plugin-api.json`, if it holds one.
pub fn read_plugin_api_file(directory: &Path) -> Option<ApiRange> {
    let file = std::fs::File::open(directory.join(PLUGIN_API_FILE)).ok()?;
    let mut bytes = Vec::new();
    file.take(PLUGIN_API_FILE_MAXIMUM_BYTES + 1)
        .read_to_end(&mut bytes)
        .ok()?;
    if bytes.len() as u64 > PLUGIN_API_FILE_MAXIMUM_BYTES {
        return None;
    }
    parse_plugin_api_file(&bytes)
}

/// The range of a client released without `plugin-api.json`, by its
/// version (build metadata ignored; a pre-release is never in the table).
pub fn released_client_plugin_api(version: &LauncherVersion) -> Option<ApiRange> {
    if version.is_pre_release() {
        return None;
    }
    let release = version
        .value()
        .split_once('+')
        .map_or(version.value(), |(core, _)| core);
    CLIENTS_RELEASED_WITHOUT_THE_FILE
        .iter()
        .find(|(name, _)| *name == release)
        .map(|&(_, current)| ApiRange::new(RELEASED_MINIMUM, current))
}

/// The plugin API range of the client in `directory` at `version`: its
/// `plugin-api.json`, else the table of clients released without one, else
/// unknown (`None`).
pub fn client_plugin_api(
    directory: Option<&Path>,
    version: Option<&LauncherVersion>,
) -> Option<ApiRange> {
    directory
        .and_then(read_plugin_api_file)
        .or_else(|| version.and_then(released_client_plugin_api))
}

/// The plugin API range of the client beside the launcher (in the
/// launcher's own folder, at the launcher's version): its
/// `plugin-api.json` there, or, in a macOS bundle (`Contents/MacOS`), in
/// the bundle's `Contents/Resources`, which holds the bundle's data files;
/// else as [`client_plugin_api`].
pub fn beside_launcher_plugin_api(
    directory: &Path,
    version: Option<&LauncherVersion>,
) -> Option<ApiRange> {
    read_plugin_api_file(directory)
        .or_else(|| {
            directory
                .file_name()
                .is_some_and(|name| name == "MacOS")
                .then(|| directory.parent())
                .flatten()
                .and_then(|contents| read_plugin_api_file(&contents.join("Resources")))
        })
        .or_else(|| version.and_then(released_client_plugin_api))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn range(minimum: (u16, u16), current: (u16, u16)) -> ApiRange {
        ApiRange::new(
            ApiVersion::new(minimum.0, minimum.1),
            ApiVersion::new(current.0, current.1),
        )
    }

    #[test]
    fn the_file_is_read_as_the_client_writes_it() {
        let written = range((1, 0), (1, 11));
        assert_eq!(
            parse_plugin_api_file(written.to_file_text().as_bytes()),
            Some(written)
        );
        // A later schema-1 member is ignored; whitespace and order do not matter.
        assert_eq!(
            parse_plugin_api_file(
                br#"{ "current": "2.3", "extra": [1], "minimum": "1.4", "schemaVersion": 1 }"#
            ),
            Some(range((1, 4), (2, 3)))
        );
    }

    #[test]
    fn anything_else_is_no_range() {
        for text in [
            "",
            "[]",
            r#"{"minimum":"1.0","current":"1.11"}"#,
            r#"{"schemaVersion":2,"minimum":"1.0","current":"1.11"}"#,
            r#"{"schemaVersion":1,"current":"1.11"}"#,
            r#"{"schemaVersion":1,"minimum":"1.0"}"#,
            r#"{"schemaVersion":1,"minimum":"1","current":"1.11"}"#,
            r#"{"schemaVersion":1,"minimum":1.0,"current":"1.11"}"#,
            r#"{"schemaVersion":1,"minimum":"1.12","current":"1.11"}"#,
            r#"{"schemaVersion":1,"minimum":"1.0","current":"1.11","current":"1.12"}"#,
        ] {
            assert_eq!(parse_plugin_api_file(text.as_bytes()), None, "{text}");
        }
    }

    /// A fresh empty folder under the temporary directory.
    fn scratch(name: &str) -> std::path::PathBuf {
        let path = std::env::temp_dir().join(format!(
            "openac-client-api-{name}-{}",
            crate::ids::unique_hex32()
        ));
        let _ = std::fs::remove_dir_all(&path);
        std::fs::create_dir_all(&path).unwrap();
        path
    }

    #[test]
    fn beside_the_launcher_the_file_wins_over_the_released_table() {
        let folder = scratch("beside");
        let released = LauncherVersion::try_parse("0.2.2").unwrap();
        // No file: a released version is known by the table.
        assert_eq!(
            beside_launcher_plugin_api(&folder, Some(&released)),
            Some(range((1, 0), (1, 9)))
        );
        // The file beside the program says otherwise, and wins.
        std::fs::write(
            folder.join(PLUGIN_API_FILE),
            range((1, 0), (1, 12)).to_file_text(),
        )
        .unwrap();
        assert_eq!(
            beside_launcher_plugin_api(&folder, Some(&released)),
            Some(range((1, 0), (1, 12)))
        );
        assert_eq!(
            beside_launcher_plugin_api(&folder, None),
            Some(range((1, 0), (1, 12)))
        );
        std::fs::remove_dir_all(&folder).unwrap();
    }

    #[test]
    fn in_a_macos_bundle_the_file_is_read_from_its_resources() {
        let bundle = scratch("bundle").join("OpenAC.app");
        let programs = bundle.join("Contents").join("MacOS");
        let resources = bundle.join("Contents").join("Resources");
        std::fs::create_dir_all(&programs).unwrap();
        std::fs::create_dir_all(&resources).unwrap();
        let unreleased = LauncherVersion::try_parse("0.2.4").unwrap();
        assert_eq!(
            beside_launcher_plugin_api(&programs, Some(&unreleased)),
            None
        );
        std::fs::write(
            resources.join(PLUGIN_API_FILE),
            range((1, 0), (1, 12)).to_file_text(),
        )
        .unwrap();
        assert_eq!(
            beside_launcher_plugin_api(&programs, Some(&unreleased)),
            Some(range((1, 0), (1, 12)))
        );
        // Only a bundle's program folder looks there.
        let plain = bundle.join("Contents").join("Other");
        std::fs::create_dir_all(&plain).unwrap();
        assert_eq!(beside_launcher_plugin_api(&plain, Some(&unreleased)), None);
        std::fs::remove_dir_all(bundle.parent().unwrap()).unwrap();
    }

    // The released clients' `API_CURRENT`, read at each release tag
    // (`ac-plugin-abi`, from 0.1.12 `ac-plugin-version`); `API_MINIMUM` was
    // 1.0 in every one.
    #[test]
    fn released_clients_without_the_file_are_in_the_table() {
        let at =
            |text: &str| released_client_plugin_api(&LauncherVersion::try_parse(text).unwrap());
        assert_eq!(at("0.2.3"), Some(range((1, 0), (1, 11))));
        assert_eq!(at("0.2.2"), Some(range((1, 0), (1, 9))));
        assert_eq!(at("0.2.1"), Some(range((1, 0), (1, 8))));
        assert_eq!(at("0.1.10"), Some(range((1, 0), (1, 5))));
        assert_eq!(at("0.1.0"), Some(range((1, 0), (1, 0))));
        assert_eq!(at("0.2.3+abc"), Some(range((1, 0), (1, 11))));
        assert_eq!(at("0.2.3-beta.1"), None);
        assert_eq!(at("0.2.4"), None);
        assert_eq!(at("0.1.13"), None);
    }
}
