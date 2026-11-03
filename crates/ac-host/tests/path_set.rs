//! Install root and folder resolution.
//!
//! Ported from OpenAC `tests/AcDream.Platform.Tests/ApplicationPathSetTests.cs`
//! and `tests/AcDream.Headless.Tests/HeadlessPathSetTests.cs`. The default
//! root's leaf is `openac-rust` here (OpenAC uses `OpenAC`, `openac` on
//! Linux) and the variables are `OPENAC_*` (OpenAC `ACDREAM_*`), so the two
//! never share an install.

mod common;

use std::path::{Path, PathBuf};

use ac_host::paths::{full_path, root_pointer};
use ac_host::{ConfigError, Os, PathOverrides, PathSet, Platform, RootSource, SystemPlatform};
use common::{FixturePlatform, TempDir};

fn base(name: &str) -> PathBuf {
    std::env::temp_dir().join(name)
}

fn windows_fixture(name: &str) -> FixturePlatform {
    let root = base(&format!("openac-resolve-{name}"));
    let mut platform = FixturePlatform::new(Os::Windows);
    platform.current_dir = root.clone();
    platform.application_data = Some(root.join("Roaming"));
    platform.local_application_data = Some(root.join("Local"));
    platform
}

fn pointer_path(platform: &FixturePlatform) -> PathBuf {
    platform
        .local_application_data
        .clone()
        .unwrap()
        .join("openac-rust")
        .join("root.json")
}

fn pointer_text(root: &Path) -> String {
    let mut text = String::from("{ \"version\": 1, \"root\": ");
    ac_host::json::write_string(&mut text, &root.to_string_lossy());
    text.push_str(" }");
    text
}

fn defaults(platform: &FixturePlatform) -> PathSet {
    PathSet::resolve(&PathOverrides::default(), platform).unwrap()
}

#[test]
fn windows_default_root_is_under_local_application_data() {
    let root = base("openac-runtime-windows");
    let mut platform = FixturePlatform::new(Os::Windows);
    platform.current_dir = root.join("work");
    platform.application_data = Some(root.join("AppData").join("Roaming"));
    platform.local_application_data = Some(root.join("AppData").join("Local"));

    let paths = defaults(&platform);

    let expected = root.join("AppData").join("Local").join("openac-rust");
    assert_eq!(paths.root_directory(), expected);
    assert_eq!(paths.data_directory, expected);
    assert_eq!(paths.config_directory, expected.join("settings"));
    assert_eq!(paths.cache_directory, expected.join("cache"));
    assert_eq!(paths.root_source, RootSource::Default);
}

#[test]
fn mac_default_root_is_under_application_support() {
    let home = base("mac-home");
    let mut platform = FixturePlatform::new(Os::MacOs)
        .with_var("XDG_DATA_HOME", &home.join("linux-data").to_string_lossy());
    platform.user_profile = Some(home.clone());

    let paths = defaults(&platform);

    assert_eq!(
        paths.root_directory(),
        home.join("Library")
            .join("Application Support")
            .join("openac-rust")
    );
    assert_eq!(paths.cache_directory, paths.data_directory.join("cache"));
}

#[test]
fn linux_default_root_follows_xdg_data_home() {
    let root = base("openac-runtime-xdg");
    let mut platform = FixturePlatform::new(Os::Linux)
        .with_var("XDG_CONFIG_HOME", &root.join("cfg").to_string_lossy())
        .with_var("XDG_DATA_HOME", &root.join("data").to_string_lossy())
        .with_var("XDG_CACHE_HOME", &root.join("cache").to_string_lossy());
    platform.current_dir = root.join("work");
    platform.user_profile = Some(root.join("home"));

    let paths = defaults(&platform);

    let expected = root.join("data").join("openac-rust");
    assert_eq!(paths.root_directory(), expected);
    assert_eq!(paths.config_directory, expected.join("settings"));
    assert_eq!(paths.cache_directory, expected.join("cache"));
}

#[test]
fn linux_default_root_falls_back_to_local_share() {
    let home = base("linux-home");
    let mut platform = FixturePlatform::new(Os::Linux);
    platform.user_profile = Some(home.clone());

    assert_eq!(
        defaults(&platform).root_directory(),
        home.join(".local").join("share").join("openac-rust")
    );
}

#[test]
fn every_member_lives_in_its_folder_under_the_root() {
    let root = base("openac-members");
    let paths = PathSet::for_root(&root, RootSource::Explicit);

    assert_eq!(paths.root_directory(), root);
    assert_eq!(paths.app_directory(), root.join("app"));
    assert_eq!(paths.game_data_directory(), root.join("game"));
    assert_eq!(paths.layout_marker_file(), root.join("layout.json"));
    assert_eq!(paths.config_directory, root.join("settings"));
    assert_eq!(
        paths.settings_file(),
        root.join("settings").join("settings.json")
    );
    assert_eq!(
        paths.key_bindings_file(),
        root.join("settings").join("keybinds.json")
    );
    assert_eq!(paths.plugins_directory(), root.join("plugins"));
    assert_eq!(paths.plugin_storage_directory(), root.join("plugins"));
    assert_eq!(
        paths.plugin_files_directory("acdream.mosstank"),
        root.join("plugins").join("acdream.mosstank").join("data")
    );
    assert_eq!(
        paths.vtank_profiles_directory(),
        root.join("plugins").join("acdream.mosstank").join("vtank")
    );
    assert_eq!(paths.logs_directory(), root.join("logs"));
    assert_eq!(
        paths.crash_reports_directory(),
        root.join("logs").join("crash")
    );
    assert_eq!(
        paths.launcher_log_file(),
        root.join("logs").join("launcher").join("launcher.log")
    );
    assert_eq!(
        paths.session_logs_directory("Coldeve", "Tester"),
        root.join("logs").join("Coldeve").join("Tester")
    );
    assert_eq!(
        paths.old_plugin_logs_directory(),
        root.join("logs").join("old plugin logs")
    );
    assert_eq!(paths.cache_directory, root.join("cache"));
    assert_eq!(
        paths.diagnostics_directory(),
        root.join("cache").join("diagnostics")
    );
    assert_eq!(
        paths.plugin_peers_directory(),
        root.join("cache").join("plugin-peers")
    );
}

#[test]
fn the_root_variable_wins_over_the_pointer() {
    let mut platform = windows_fixture("root-env");
    let at = platform.current_dir.clone();
    platform
        .files
        .insert(pointer_path(&platform), pointer_text(&at.join("pointed")));
    platform.variables.insert(
        "OPENAC_ROOT_DIR".into(),
        at.join("from-env").display().to_string(),
    );

    let paths = defaults(&platform);

    assert_eq!(paths.root_directory(), at.join("from-env"));
    assert_eq!(paths.root_source, RootSource::Environment);
}

#[test]
fn the_pointer_in_the_default_root_names_the_root() {
    let mut platform = windows_fixture("pointer");
    let pointed = platform.current_dir.join("Games").join("OpenAC");
    platform
        .files
        .insert(pointer_path(&platform), pointer_text(&pointed));

    let paths = defaults(&platform);

    assert_eq!(paths.root_directory(), pointed);
    assert_eq!(paths.config_directory, pointed.join("settings"));
    assert_eq!(paths.root_source, RootSource::Pointer);
}

#[test]
fn a_broken_pointer_is_reported_rather_than_ignored() {
    let mut platform = windows_fixture("broken-pointer");
    platform.files.insert(
        pointer_path(&platform),
        "{ \"version\": 1, \"root\": \"relative\" }".into(),
    );

    let error = PathSet::resolve(&PathOverrides::default(), &platform).unwrap_err();
    assert!(matches!(&error, ConfigError::Invalid(message) if message.contains("root.json")));
}

#[test]
fn member_variables_still_override_their_own_member_over_the_root_variable() {
    let platform = windows_fixture("member-env")
        .with_var("OPENAC_ROOT_DIR", "root")
        .with_var("OPENAC_CONFIG_DIR", "capture-config")
        .with_var("OPENAC_CACHE_DIR", "capture-cache");
    let at = platform.current_dir.clone();

    let paths = defaults(&platform);

    assert_eq!(paths.root_directory(), at.join("root"));
    assert_eq!(paths.config_directory, at.join("capture-config"));
    assert_eq!(paths.cache_directory, at.join("capture-cache"));
}

#[test]
fn member_variables_isolate_all_state_and_mark_the_set_as_not_default() {
    let platform = windows_fixture("isolated")
        .with_var("OPENAC_CONFIG_DIR", "capture-config")
        .with_var("OPENAC_DATA_DIR", "capture-data")
        .with_var("OPENAC_CACHE_DIR", "capture-cache");
    let at = platform.current_dir.clone();

    let paths = defaults(&platform);

    assert_eq!(paths.config_directory, at.join("capture-config"));
    assert_eq!(paths.data_directory, at.join("capture-data"));
    assert_eq!(paths.cache_directory, at.join("capture-cache"));
    assert_eq!(paths.root_source, RootSource::Environment);
}

#[test]
fn an_explicit_root_ignores_the_environment() {
    let platform = windows_fixture("explicit-root")
        .with_var("OPENAC_ROOT_DIR", "environment-root")
        .with_var("OPENAC_CONFIG_DIR", "environment-config");
    let at = platform.current_dir.clone();

    let paths = PathSet::resolve(
        &PathOverrides {
            root_directory: Some("explicit-root".into()),
            ..PathOverrides::default()
        },
        &platform,
    )
    .unwrap();

    let root = at.join("explicit-root");
    assert_eq!(paths.root_directory(), root);
    assert_eq!(paths.config_directory, root.join("settings"));
    assert_eq!(paths.root_source, RootSource::Explicit);
}

#[test]
fn an_explicit_member_wins_over_an_explicit_root() {
    let platform = windows_fixture("explicit-member");
    let at = platform.current_dir.clone();

    let paths = PathSet::resolve(
        &PathOverrides {
            root_directory: Some("explicit-root".into()),
            config_directory: Some("explicit-config".into()),
            ..PathOverrides::default()
        },
        &platform,
    )
    .unwrap();

    assert_eq!(paths.config_directory, at.join("explicit-config"));
    assert_eq!(paths.data_directory, at.join("explicit-root"));
}

#[test]
fn explicit_arguments_override_automation_environment_folders() {
    let root = base("openac-explicit-paths");
    let mut platform = FixturePlatform::new(Os::Linux)
        .with_var("OPENAC_CONFIG_DIR", "environment-config")
        .with_var("OPENAC_DATA_DIR", "environment-data")
        .with_var("OPENAC_CACHE_DIR", "environment-cache");
    platform.current_dir = root.clone();
    platform.user_profile = Some(root.join("home"));

    let paths = PathSet::resolve(
        &PathOverrides {
            config_directory: Some("explicit-config".into()),
            data_directory: Some("explicit-data".into()),
            cache_directory: Some("explicit-cache".into()),
            ..PathOverrides::default()
        },
        &platform,
    )
    .unwrap();

    assert_eq!(paths.config_directory, root.join("explicit-config"));
    assert_eq!(paths.data_directory, root.join("explicit-data"));
    assert_eq!(paths.cache_directory, root.join("explicit-cache"));
    assert_eq!(paths.root_source, RootSource::Explicit);
}

#[test]
fn relative_overrides_are_normalized_against_the_working_directory() {
    let root = base("openac runtime paths");
    let mut platform = FixturePlatform::new(Os::Linux);
    platform.current_dir = root.join("work");
    platform.user_profile = Some(root.join("home"));

    let paths = PathSet::resolve(
        &PathOverrides {
            config_directory: Some("relative config".into()),
            data_directory: Some("d\u{e5}ta".into()),
            cache_directory: Some("cache".into()),
            ..PathOverrides::default()
        },
        &platform,
    )
    .unwrap();

    assert_eq!(
        paths.config_directory,
        platform.current_dir.join("relative config")
    );
    assert_eq!(paths.data_directory, platform.current_dir.join("d\u{e5}ta"));
    assert_eq!(paths.cache_directory, platform.current_dir.join("cache"));
}

#[test]
fn a_blank_value_counts_as_unset_and_a_missing_platform_folder_is_an_error() {
    let mut platform = FixturePlatform::new(Os::Linux).with_var("OPENAC_DATA_DIR", "  ");
    platform.user_profile = Some(base("openac-blank-home"));
    let paths = PathSet::resolve(
        &PathOverrides {
            config_directory: Some(" ".into()),
            ..PathOverrides::default()
        },
        &platform,
    )
    .unwrap();
    let root = base("openac-blank-home")
        .join(".local")
        .join("share")
        .join("openac-rust");
    assert_eq!(paths.data_directory, root);
    assert_eq!(paths.config_directory, root.join("settings"));
    assert_eq!(paths.root_source, RootSource::Default);

    let mut platform = FixturePlatform::new(Os::Windows);
    platform.local_application_data = None;
    let error = PathSet::resolve(&PathOverrides::default(), &platform).unwrap_err();
    assert!(
        matches!(&error, ConfigError::Invalid(message) if message.contains("LocalApplicationData"))
    );
}

#[test]
fn a_pointer_write_round_trips_and_pointing_at_the_default_removes_it() {
    let scratch = TempDir::new("pointer");
    let default = scratch.path.join("default");
    let moved = scratch.path.join("moved");

    root_pointer::write(&default, &moved).unwrap();
    let written = std::fs::read_to_string(root_pointer::path_for(&default)).unwrap();
    assert_eq!(root_pointer::parse(&written), Some(moved));

    root_pointer::write(&default, &default).unwrap();
    assert!(!root_pointer::path_for(&default).exists());
}

#[test]
fn the_pointer_parse_rejects_anything_but_version_one_with_an_absolute_root() {
    let absolute = std::env::temp_dir().join("x");
    let mut versioned = String::from("{ \"version\": 2, \"root\": ");
    ac_host::json::write_string(&mut versioned, &absolute.to_string_lossy());
    versioned.push_str(" }");
    for text in [
        versioned.as_str(),
        "{ \"version\": 1, \"root\": \"x\" }",
        "{ \"version\": 1 }",
        "not json",
    ] {
        assert_eq!(root_pointer::parse(text), None, "{text}");
    }
}

#[test]
fn a_configured_root_wins_over_the_environment() {
    let root = base("openac-bot-root");
    let mut platform = FixturePlatform::new(Os::Windows)
        .with_var("OPENAC_ROOT_DIR", &root.join("from-env").to_string_lossy())
        .with_var(
            "OPENAC_CONFIG_DIR",
            &root.join("env-config").to_string_lossy(),
        );
    platform.current_dir = root.clone();
    platform.local_application_data = Some(root.join("Local"));

    let paths = PathSet::resolve(
        &PathOverrides {
            root_directory: Some("bot-root".into()),
            ..PathOverrides::default()
        },
        &platform,
    )
    .unwrap();

    assert_eq!(paths.data_directory, root.join("bot-root"));
    assert_eq!(
        paths.config_directory,
        root.join("bot-root").join("settings")
    );
}

#[test]
fn host_environment_reports_the_running_operating_system() {
    let os = SystemPlatform.os();
    assert_eq!(os == Os::Windows, cfg!(windows));
    assert_eq!(os == Os::MacOs, cfg!(target_os = "macos"));
}

#[test]
fn normalization_removes_dot_segments_and_trailing_separators() {
    let base = base("openac-norm");
    assert_eq!(
        full_path(&PathBuf::from("a/../b/./"), &base),
        base.join("b")
    );
}
