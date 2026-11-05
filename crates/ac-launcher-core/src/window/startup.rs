//! The launcher's command line: an isolated set of directories, another
//! update feed or plugin list, and the publish probe.

use std::path::{Path, PathBuf};

use ac_host_core::PathSet;

use crate::plugins::PluginCatalog;
use crate::updates::ReleaseManifestClient;
use crate::url::WebUrl;

/// What the process was started to do.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum LauncherStartupMode {
    /// Open the launcher window.
    Desktop,
    /// Exit 0 at once: a display-free check that the packaged program runs.
    VerifyPublish,
    /// The staged launcher applying a self-update (`--acdream-self-update-helper-v1`).
    SelfUpdateHelper,
    /// The updated launcher confirming it runs (`--acdream-self-update-confirm-v1`).
    SelfUpdateConfirmation,
}

/// The mode and where the launcher's own arguments start: after the
/// helper's three operands or the confirmation's one. A malformed internal
/// invocation is refused by the bootstrap; its operands are never read as
/// options.
fn read_mode(arguments: &[String]) -> (LauncherStartupMode, usize) {
    use crate::updates::self_update_bootstrap::{CONFIRM_ARGUMENT, HELPER_ARGUMENT};
    match arguments.first().map(String::as_str) {
        Some(HELPER_ARGUMENT) => (
            LauncherStartupMode::SelfUpdateHelper,
            arguments.len().min(4),
        ),
        Some(CONFIRM_ARGUMENT) => (
            LauncherStartupMode::SelfUpdateConfirmation,
            arguments.len().min(2),
        ),
        _ => (LauncherStartupMode::Desktop, 0),
    }
}

/// Why the command line was refused.
#[derive(Debug, Clone, PartialEq, Eq, thiserror::Error)]
#[error("{0}")]
pub struct StartupOptionsError(pub String);

fn refuse(message: impl Into<String>) -> StartupOptionsError {
    StartupOptionsError(message.into())
}

/// The parsed command line.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LauncherStartupOptions {
    /// What to do.
    pub mode: LauncherStartupMode,
    /// The configuration, data and cache directories.
    pub paths: PathSet,
    /// The release feed the update check reads (`--update-manifest-uri`,
    /// else production).
    pub update_manifest_uri: WebUrl,
    /// The curated plugin list (`--plugin-list-uri`, else production).
    pub plugin_list_uri: WebUrl,
    /// The launcher's own arguments (after any self-update prefix).
    pub public_arguments: Vec<String>,
    /// `--control-pipe`: the name of the optional control endpoint
    /// ([`crate::control`]), off when absent.
    pub control_pipe: Option<String>,
}

impl LauncherStartupOptions {
    /// Parses `arguments` (without the program name). `resolve_default_paths`
    /// is called once, and only when no directory was given.
    pub fn parse(
        arguments: &[String],
        resolve_default_paths: impl FnOnce() -> Result<PathSet, String>,
    ) -> Result<Self, StartupOptionsError> {
        let (mode, public_start) = read_mode(arguments);
        let arguments = &arguments[public_start..];
        if arguments
            .iter()
            .any(|argument| argument == "--verify-publish")
        {
            if mode != LauncherStartupMode::Desktop || arguments.len() != 1 {
                return Err(refuse(
                    "--verify-publish must be the only launcher argument.",
                ));
            }
            // The probe exits before the paths are read; empty ones keep it free of the user's
            // profile.
            return Ok(Self {
                mode: LauncherStartupMode::VerifyPublish,
                paths: PathSet::from_members(PathBuf::new(), PathBuf::new(), PathBuf::new()),
                update_manifest_uri: ReleaseManifestClient::production_manifest_uri(),
                plugin_list_uri: PluginCatalog::production_list_uri(),
                public_arguments: arguments.to_vec(),
                control_pipe: None,
            });
        }

        let mut config: Option<PathBuf> = None;
        let mut data: Option<PathBuf> = None;
        let mut cache: Option<PathBuf> = None;
        let mut root: Option<PathBuf> = None;
        let mut update_manifest_uri: Option<WebUrl> = None;
        let mut plugin_list_uri: Option<WebUrl> = None;
        let mut control_pipe: Option<String> = None;
        let mut index = 0;
        while index < arguments.len() {
            let name = &arguments[index];
            let Some(value) = arguments
                .get(index + 1)
                .filter(|value| !value.starts_with("--"))
            else {
                return Err(refuse(format!(
                    "Launcher option '{name}' requires a value."
                )));
            };
            if value.trim().is_empty() {
                return Err(refuse(format!(
                    "Launcher option '{name}' requires a non-empty value."
                )));
            }
            match name.as_str() {
                "--config-dir" => set_directory_once(&mut config, value, name)?,
                "--data-dir" => set_directory_once(&mut data, value, name)?,
                "--cache-dir" => set_directory_once(&mut cache, value, name)?,
                "--root-dir" => set_directory_once(&mut root, value, name)?,
                "--update-manifest-uri" => {
                    if update_manifest_uri.is_some() {
                        return Err(refuse("Launcher options cannot be repeated."));
                    }
                    update_manifest_uri = Some(parse_feed_uri(name, value)?);
                }
                "--plugin-list-uri" => {
                    if plugin_list_uri.is_some() {
                        return Err(refuse("Launcher options cannot be repeated."));
                    }
                    plugin_list_uri = Some(parse_feed_uri(name, value)?);
                }
                "--control-pipe" => {
                    if control_pipe.is_some() || !crate::control::is_valid_pipe_name(value) {
                        return Err(refuse(
                            "Use one control pipe name of 1-64 ASCII letters, digits, hyphens \
                             or underscores.",
                        ));
                    }
                    control_pipe = Some(value.clone());
                }
                _ => return Err(refuse(format!("Unknown launcher option '{name}'."))),
            }
            index += 2;
        }

        // The three member flags name the whole set; a root alone names the
        // whole install. Either way the environment and the pointer file are
        // not consulted: the command line said where.
        let paths = match (config, data, cache) {
            (Some(config_directory), Some(data_directory), Some(cache_directory)) => {
                PathSet::from_members(config_directory, data_directory, cache_directory)
            }
            (None, None, None) => match root {
                Some(root) => PathSet::for_root(&root, ac_host_core::RootSource::Explicit),
                None => resolve_default_paths().map_err(StartupOptionsError)?,
            },
            _ => {
                return Err(refuse(
                    "--config-dir, --data-dir, and --cache-dir must be supplied together.",
                ));
            }
        };
        Ok(Self {
            mode,
            paths,
            update_manifest_uri: update_manifest_uri
                .unwrap_or_else(ReleaseManifestClient::production_manifest_uri),
            plugin_list_uri: plugin_list_uri.unwrap_or_else(PluginCatalog::production_list_uri),
            public_arguments: arguments.to_vec(),
            control_pipe,
        })
    }
}

/// An absolute HTTPS address, or HTTP to this machine (test feeds), with no
/// user information, query or fragment.
fn parse_feed_uri(option: &str, value: &str) -> Result<WebUrl, StartupOptionsError> {
    let parsed =
        WebUrl::parse(value).ok_or_else(|| refuse(format!("{option} must be an absolute URI.")))?;
    if !(parsed.is_https() || (parsed.is_http() && parsed.is_loopback())) {
        return Err(refuse(format!(
            "The {option} URI must use HTTPS (loopback HTTP is test-only)."
        )));
    }
    if !parsed.user_info().is_empty() {
        return Err(refuse(format!(
            "The {option} URI cannot contain user information."
        )));
    }
    if !parsed.query().is_empty() || !parsed.fragment().is_empty() {
        return Err(refuse(format!(
            "The {option} URI cannot contain a query or fragment."
        )));
    }
    Ok(parsed)
}

fn set_directory_once(
    destination: &mut Option<PathBuf>,
    value: &str,
    option: &str,
) -> Result<(), StartupOptionsError> {
    if destination.is_some() {
        return Err(refuse("Launcher options cannot be repeated."));
    }
    let path = Path::new(value);
    if !path.is_absolute() {
        return Err(refuse(format!(
            "Launcher option '{option}' must be an absolute path."
        )));
    }
    // Absolute already, so the base is never used.
    *destination = Some(ac_host_core::paths::full_path(path, path));
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn strings(values: &[&str]) -> Vec<String> {
        values.iter().map(|value| (*value).to_owned()).collect()
    }

    fn fixed() -> Result<PathSet, String> {
        Ok(PathSet::from_members("c".into(), "d".into(), "x".into()))
    }

    // OpenAC LauncherStartupOptions.Parse: a root alone names the whole
    // install without consulting the environment or the pointer.
    #[test]
    fn a_root_alone_names_the_whole_install() {
        let root = std::env::temp_dir().join("acdream-la11-root");
        let options = LauncherStartupOptions::parse(
            &strings(&["--root-dir", &root.to_string_lossy()]),
            untouched,
        )
        .unwrap();
        assert_eq!(options.paths.root_directory(), root);
        assert_eq!(options.paths.config_directory, root.join("settings"));
        assert_eq!(
            options.paths.root_source,
            ac_host_core::RootSource::Explicit
        );
        assert!(
            LauncherStartupOptions::parse(&strings(&["--root-dir", "relative"]), untouched)
                .is_err()
        );
    }

    fn untouched() -> Result<PathSet, String> {
        panic!("the default path resolver was touched")
    }

    // OpenAC LauncherControlTests.PipeIsOptInAndNamesAreValidated.
    #[test]
    fn the_control_pipe_is_opt_in_and_its_name_is_validated() {
        assert_eq!(
            LauncherStartupOptions::parse(&[], fixed)
                .unwrap()
                .control_pipe,
            None
        );
        assert_eq!(
            LauncherStartupOptions::parse(&strings(&["--control-pipe", "test-123"]), fixed)
                .unwrap()
                .control_pipe
                .as_deref(),
            Some("test-123")
        );
        let long = "a".repeat(65);
        for invalid in ["a/b", "a\\b", "a b", long.as_str(), "ä"] {
            assert!(
                LauncherStartupOptions::parse(&strings(&["--control-pipe", invalid]), fixed)
                    .is_err(),
                "{invalid}"
            );
        }
        assert!(
            LauncherStartupOptions::parse(
                &strings(&["--control-pipe", "one", "--control-pipe", "two"]),
                fixed
            )
            .is_err()
        );
    }

    fn absolute() -> String {
        std::env::temp_dir()
            .join("acdream-la11")
            .to_string_lossy()
            .into_owned()
    }

    // OpenAC LauncherStartupOptionsTests.ExplicitIsolationRootsAreNormalizedAndNeverResolveCanonicalPaths.
    #[test]
    fn explicit_isolation_roots_are_normalized_and_never_resolve_canonical_paths() {
        let root = std::env::temp_dir()
            .join("acdream-la11 options")
            .join("..")
            .join("isolation");
        let config = format!(
            "{}{}",
            root.join("config").display(),
            std::path::MAIN_SEPARATOR
        );
        let data = root.join("data").join(".").join("state");
        let cache = format!(
            "{}{}",
            root.join("cache").display(),
            std::path::MAIN_SEPARATOR
        );
        let options = LauncherStartupOptions::parse(
            &strings(&[
                "--config-dir",
                &config,
                "--data-dir",
                &data.to_string_lossy(),
                "--cache-dir",
                &cache,
                "--update-manifest-uri",
                "http://127.0.0.1:43119/manifest.json",
            ]),
            untouched,
        )
        .unwrap();
        let isolation = std::env::temp_dir().join("isolation");
        assert_eq!(options.paths.config_directory, isolation.join("config"));
        assert_eq!(
            options.paths.data_directory,
            isolation.join("data").join("state")
        );
        assert_eq!(options.paths.cache_directory, isolation.join("cache"));
        assert_eq!(
            options.update_manifest_uri.to_string(),
            "http://127.0.0.1:43119/manifest.json"
        );
        assert_eq!(options.mode, LauncherStartupMode::Desktop);
    }

    // OpenAC LauncherStartupOptionsTests.NoOverridesResolveDefaultsExactlyOnce.
    #[test]
    fn no_overrides_resolve_defaults_exactly_once() {
        let mut calls = 0;
        let options = LauncherStartupOptions::parse(&[], || {
            calls += 1;
            fixed()
        })
        .unwrap();
        assert_eq!(calls, 1);
        assert_eq!(options.paths, fixed().unwrap());
        assert_eq!(
            options.update_manifest_uri,
            ReleaseManifestClient::production_manifest_uri()
        );
        assert_eq!(
            options.plugin_list_uri,
            PluginCatalog::production_list_uri()
        );
    }

    // OpenAC LauncherStartupOptionsTests.VerifyPublishIsExclusiveAndDoesNotResolvePaths.
    #[test]
    fn verify_publish_is_exclusive_and_does_not_resolve_paths() {
        let options =
            LauncherStartupOptions::parse(&strings(&["--verify-publish"]), untouched).unwrap();
        assert_eq!(options.mode, LauncherStartupMode::VerifyPublish);
        assert!(
            LauncherStartupOptions::parse(
                &strings(&["--verify-publish", "--cache-dir", &absolute()]),
                fixed,
            )
            .is_err()
        );
    }

    // OpenAC LauncherStartupOptionsTests.RejectsInvalidPublicArguments (InvalidArguments).
    #[test]
    fn rejects_invalid_public_arguments() {
        let absolute = absolute();
        let cases: Vec<Vec<&str>> = vec![
            vec!["--unknown", "value"],
            vec!["--config-dir"],
            vec!["--config-dir", "relative"],
            vec!["--config-dir", &absolute],
            vec!["--config-dir", &absolute, "--data-dir", &absolute],
            vec![
                "--config-dir",
                &absolute,
                "--data-dir",
                &absolute,
                "--cache-dir",
                &absolute,
                "--cache-dir",
                &absolute,
            ],
            vec![
                "--update-manifest-uri",
                "http://updates.example.test/manifest.json",
            ],
            vec!["--update-manifest-uri", "file:///tmp/manifest.json"],
            vec![
                "--update-manifest-uri",
                "https://user:secret@example.test/manifest.json",
            ],
            vec![
                "--update-manifest-uri",
                "https://example.test/manifest.json?token=secret",
            ],
            vec![
                "--update-manifest-uri",
                "https://example.test/manifest.json#fragment",
            ],
            vec!["--update-manifest-uri", "not-a-uri"],
            vec![
                "--update-manifest-uri",
                "https://example.test/a",
                "--update-manifest-uri",
                "https://example.test/b",
            ],
            vec![
                "--plugin-list-uri",
                "http://plugins.example.test/plugins.json",
            ],
            vec!["--plugin-list-uri", "file:///tmp/plugins.json"],
            vec![
                "--plugin-list-uri",
                "https://user:secret@example.test/plugins.json",
            ],
            vec![
                "--plugin-list-uri",
                "https://example.test/plugins.json?token=secret",
            ],
            vec![
                "--plugin-list-uri",
                "https://example.test/plugins.json#fragment",
            ],
            vec!["--plugin-list-uri", "not-a-uri"],
            vec![
                "--plugin-list-uri",
                "https://example.test/a",
                "--plugin-list-uri",
                "https://example.test/b",
            ],
            // OpenAC LegacyDeferredSelfUpdatePrefixIsRejectedAsUntrustedInput: only the
            // helper and confirmation prefixes are modes.
            vec!["--acdream-self-update-deferred-v1", "x"],
        ];
        for case in cases {
            assert!(
                LauncherStartupOptions::parse(&strings(&case), fixed).is_err(),
                "{case:?} was accepted"
            );
        }
    }

    // OpenAC LauncherStartupOptionsTests.SelfUpdatePrefixesRetainOnlyTheValidatedPublicSuffix.
    #[test]
    fn self_update_prefixes_retain_only_the_validated_public_suffix() {
        use crate::updates::self_update_bootstrap::{CONFIRM_ARGUMENT, HELPER_ARGUMENT};
        let root = std::env::temp_dir().join("acdream-la11-self");
        let root_text = root.display().to_string();
        let suffix = vec![
            "--config-dir".to_owned(),
            root.join("config").display().to_string(),
            "--data-dir".to_owned(),
            root.join("data").display().to_string(),
            "--cache-dir".to_owned(),
            root.join("cache").display().to_string(),
            "--update-manifest-uri".to_owned(),
            "http://localhost:8123/manifest.json".to_owned(),
        ];
        let transaction = "0123456789abcdef0123456789abcdef";
        let helper: Vec<String> = [HELPER_ARGUMENT, "123", &root_text, transaction]
            .iter()
            .map(|a| (*a).to_owned())
            .chain(suffix.iter().cloned())
            .collect();
        let confirmation: Vec<String> = [CONFIRM_ARGUMENT, transaction]
            .iter()
            .map(|a| (*a).to_owned())
            .chain(suffix.iter().cloned())
            .collect();

        let helper_options = LauncherStartupOptions::parse(&helper, untouched).unwrap();
        let confirmation_options = LauncherStartupOptions::parse(&confirmation, untouched).unwrap();

        assert_eq!(helper_options.mode, LauncherStartupMode::SelfUpdateHelper);
        assert_eq!(
            confirmation_options.mode,
            LauncherStartupMode::SelfUpdateConfirmation
        );
        assert_eq!(helper_options.public_arguments, suffix);
        assert_eq!(confirmation_options.public_arguments, suffix);
        assert_eq!(helper_options.paths, confirmation_options.paths);
        assert_eq!(
            helper_options.update_manifest_uri,
            confirmation_options.update_manifest_uri
        );
    }

    // OpenAC LauncherStartupOptionsTests.AcceptsHttpsAndLoopbackHttpFeeds and
    // AcceptsHttpsAndLoopbackHttpPluginListFeeds.
    #[test]
    fn accepts_https_and_loopback_http_feeds() {
        for value in [
            "https://updates.example.test/manifest.json",
            "http://localhost:8123/manifest.json",
            "http://[::1]:8123/manifest.json",
        ] {
            let options =
                LauncherStartupOptions::parse(&strings(&["--update-manifest-uri", value]), fixed)
                    .unwrap();
            assert_eq!(Some(options.update_manifest_uri), WebUrl::parse(value));
        }
        for value in [
            "https://plugins.example.test/plugins.json",
            "http://localhost:8123/plugins.json",
        ] {
            let options =
                LauncherStartupOptions::parse(&strings(&["--plugin-list-uri", value]), fixed)
                    .unwrap();
            assert_eq!(Some(options.plugin_list_uri), WebUrl::parse(value));
        }
    }
}
