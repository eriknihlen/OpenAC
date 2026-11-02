//! The session-config composer, the capped error log and the process
//! supervisor. Ported from OpenAC
//! `tests/AcDream.Launcher.Core.Tests/Launching/` (SessionConfigComposerTests,
//! BoundedProcessOutputCaptureTests, LauncherProcessSupervisorTests). The
//! child-process fixtures (OpenAC's ConsoleSignalChild) are this test binary
//! re-run as a child: see `fixture_console_child`.

mod common;

use std::collections::VecDeque;
use std::io::Read;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::sync::{Arc, Condvar, Mutex};
use std::time::{Duration, Instant};

use ac_host::{CharacterSelector, ConfigReader, PathSet, SessionMode, load_config};
use ac_launcher_core::installation::LauncherInstallRecord;
use ac_launcher_core::launching::{
    BoundedProcessOutputCapture, ChildProcess, ChildProcessFactory, ComposeError,
    ComposedSessionConfig, DEFAULT_MAX_BYTES, DEFAULT_MAX_FILES, ExitCallback, LauncherProcessSpec,
    LauncherProcessSupervisor, LauncherSessionState, ProcessSupervisor, StopOutcome,
    SystemChildProcessFactory, compose, compose_probe, serialize, write,
};
use ac_launcher_core::plugins::{
    InstalledPluginRecord, InstalledPluginRecordStore, PluginCatalog, PluginInstallSource,
    PluginReleaseChannel,
};
use ac_launcher_core::profiles::{AccountProfile, CharacterProfile, LaunchMode, ServerProfile};
use common::{TempDir, fixture_args, fixture_command};

// OpenAC LauncherChildEnvironmentTests.EveryChildIsToldTheRootAndEachMember.
#[test]
fn every_child_is_told_the_root_and_each_member() {
    let root = std::env::temp_dir().join("openac-child-env");
    let mut paths = PathSet::for_root(&root, ac_host::RootSource::Explicit);
    paths.config_directory = root.join("elsewhere");

    let environment = ac_launcher_core::launching::child_environment(&paths);

    let value = |name: &str| {
        environment
            .iter()
            .find(|(key, _)| key == name)
            .map(|(_, value)| PathBuf::from(value))
    };
    assert_eq!(value("OPENAC_ROOT_DIR"), Some(root.clone()));
    assert_eq!(value("OPENAC_CONFIG_DIR"), Some(root.join("elsewhere")));
    assert_eq!(value("OPENAC_DATA_DIR"), Some(root.clone()));
    assert_eq!(value("OPENAC_CACHE_DIR"), Some(root.join("cache")));
    // An addition: the client writes heartbeats only when asked.
    assert_eq!(value("OPENAC_STATUS_HEARTBEAT"), Some(PathBuf::from("1")));
}

// --- SessionConfigComposerTests -------------------------------------------------

fn paths() -> PathSet {
    PathSet::from_members(
        "/cfg/acdream".into(),
        "/data/acdream".into(),
        "/cache/acdream".into(),
    )
}

fn install() -> LauncherInstallRecord {
    LauncherInstallRecord::new("/dats", "/data/acdream/pak/acdream.pak", "", 0, 0)
}

fn server() -> ServerProfile {
    ServerProfile::new("Local ACE", "127.0.0.1", 9000)
}

const PASSWORD: &str = "S3cretPassw0rd!";

fn account() -> AccountProfile {
    let mut account = AccountProfile::new("testaccount", PASSWORD);
    account.login_commands = vec!["/tell someone, hi".into()];
    account
}

fn character(mode: LaunchMode, id: Option<&str>) -> CharacterProfile {
    CharacterProfile {
        name: "+Acdream".into(),
        id: id.map(str::to_owned),
        launch_mode: mode,
        plugins: Some(vec!["ExamplePlugin".into()]),
    }
}

fn compose_simple(
    character: &CharacterProfile,
    session: &str,
    paths: &PathSet,
    catalog: Option<&PluginCatalog>,
) -> ComposedSessionConfig {
    compose(
        &server(),
        &account(),
        character,
        &install(),
        paths,
        session,
        None,
        catalog,
    )
    .unwrap()
}

/// The single session's members as (name, value) from the written JSON.
fn session_members(composed: &ComposedSessionConfig) -> Vec<(String, ac_host::json::Value)> {
    let json = serialize(&composed.document);
    let root = ac_host::json::parse(json.as_bytes()).unwrap();
    let members = ac_host::json::object_members(&root).unwrap();
    let sessions = members
        .iter()
        .find(|member| member.name == "sessions")
        .unwrap();
    let items = ac_host::json::array_items(&sessions.value).unwrap();
    assert_eq!(items.len(), 1);
    ac_host::json::object_members(&items[0])
        .unwrap()
        .iter()
        .map(|member| (member.name.clone(), member.value.value.clone()))
        .collect()
}

fn keys(composed: &ComposedSessionConfig) -> Vec<String> {
    let mut keys: Vec<String> = session_members(composed)
        .into_iter()
        .map(|(name, _)| name)
        .collect();
    keys.sort();
    keys
}

fn sorted(items: &[&str]) -> Vec<String> {
    let mut items: Vec<String> = items.iter().map(|item| (*item).to_owned()).collect();
    items.sort();
    items
}

fn session(composed: &ComposedSessionConfig) -> &ac_host::SessionDescriptor {
    &composed.document.sessions[0]
}

#[test]
fn pending_client_compatibility_cannot_compose_play_or_probe_session() {
    let mut pending = install();
    pending.requires_client_compatibility_confirmation = true;
    let play = compose(
        &server(),
        &account(),
        &character(LaunchMode::Gui, Some("0x5000000A")),
        &pending,
        &paths(),
        "pending-play",
        None,
        None,
    )
    .unwrap_err();
    let probe =
        compose_probe(&server(), &account(), &pending, &paths(), "pending-probe").unwrap_err();
    for error in [play, probe] {
        assert!(matches!(
            error,
            ComposeError::ClientCompatibilityUnconfirmed
        ));
        assert!(error.to_string().to_lowercase().contains("matching client"));
    }
}

#[test]
fn gui_mode_includes_character_selector_and_omits_policy() {
    let composed = compose_simple(
        &character(LaunchMode::Gui, Some("0x5000000A")),
        "session-gui",
        &paths(),
        None,
    );
    assert_eq!(
        keys(&composed),
        sorted(&[
            "id",
            "endpoint",
            "account",
            "character",
            "credential",
            "plugins",
            "loginCommands",
            "statusFile"
        ])
    );
    let session = session(&composed);
    assert_eq!(session.id, "session-gui");
    assert_eq!(session.account, "testaccount");
    assert_eq!(
        session.mode,
        SessionMode::Play {
            character: Some(CharacterSelector::Id(0x5000_000A)),
            policy: None
        }
    );
    assert_eq!(
        session.plugins.as_deref(),
        Some(&["ExamplePlugin".to_owned()][..])
    );
    assert_eq!(
        session.login_commands.as_deref(),
        Some(&["/tell someone, hi".to_owned()][..])
    );
    assert_eq!(
        session.status_file.as_deref(),
        Some(
            paths()
                .launcher_sessions_directory()
                .join("session-gui")
                .join("status.jsonl")
                .as_path()
        )
    );
    let json = serialize(&composed.document);
    assert!(json.contains("\"provider\": \"standardInput\""));
    assert!(json.contains("\"reference\": \"session\""));
}

#[test]
fn gui_select_mode_omits_character_field_entirely() {
    let composed = compose_simple(
        &character(LaunchMode::GuiSelect, Some("0x5000000A")),
        "session-guiselect",
        &paths(),
        None,
    );
    assert_eq!(
        keys(&composed),
        sorted(&[
            "id",
            "endpoint",
            "account",
            "credential",
            "plugins",
            "loginCommands",
            "statusFile"
        ])
    );
}

#[test]
fn headless_mode_includes_character_and_idle_policy() {
    let composed = compose(
        &server(),
        &account(),
        &character(LaunchMode::Headless, Some("0x5000000A")),
        &install(),
        &paths(),
        "session-headless",
        Some(750),
        None,
    )
    .unwrap();
    assert_eq!(
        keys(&composed),
        sorted(&[
            "id",
            "endpoint",
            "account",
            "character",
            "policy",
            "credential",
            "plugins",
            "loginCommands",
            "loginCommandDelayMs",
            "statusFile"
        ])
    );
    let SessionMode::Play { character, policy } = &session(&composed).mode else {
        panic!("play expected");
    };
    assert_eq!(character, &Some(CharacterSelector::Id(0x5000_000A)));
    assert_eq!(policy.as_ref().unwrap().id, "idle");
    assert_eq!(session(&composed).login_command_delay_ms, 750);
}

#[test]
fn gui_mode_falls_back_to_a_name_selector() {
    for id in [None, Some("12345678"), Some("0x00000000")] {
        let composed = compose_simple(
            &character(LaunchMode::Gui, id),
            "session-name",
            &paths(),
            None,
        );
        let SessionMode::Play { character, .. } = &session(&composed).mode else {
            panic!("play expected");
        };
        assert_eq!(
            character,
            &Some(CharacterSelector::Name("+Acdream".into())),
            "{id:?}"
        );
    }
}

#[test]
fn plugin_allow_list_rules() {
    let mut empty = character(LaunchMode::Gui, Some("0x5000000A"));
    // A blank list means none, not "every plugin loads".
    empty.plugins = Some(Vec::new());
    let mut no_commands = account();
    no_commands.login_commands.clear();
    let composed = compose(
        &server(),
        &no_commands,
        &empty,
        &install(),
        &paths(),
        "session-empty-lists",
        None,
        None,
    )
    .unwrap();
    assert!(keys(&composed).contains(&"plugins".to_owned()));
    assert_eq!(session(&composed).plugins.as_deref(), Some(&[][..]));
    assert!(!keys(&composed).contains(&"loginCommands".to_owned()));
    assert!(composed.plugin_status_lines.is_empty());

    let mut none = character(LaunchMode::Gui, Some("0x5000000A"));
    none.plugins = Some(vec!["none".into()]);
    let composed = compose_simple(&none, "session-no-plugins", &paths(), None);
    assert_eq!(session(&composed).plugins.as_deref(), Some(&[][..]));

    let mut one = character(LaunchMode::Gui, Some("0x5000000A"));
    one.plugins = Some(vec!["edwards.tank".into()]);
    let composed = compose_simple(&one, "session-one-plugin", &paths(), None);
    assert_eq!(
        session(&composed).plugins.as_deref(),
        Some(&["edwards.tank".to_owned()][..])
    );
}

const HELLO_LIST: &str = r#"{
  "schemaVersion": 1,
  "plugins": [
    { "id": "edwards.hello", "name": "Hello", "author": "Shane Edwards",
      "description": "Says hello.", "repo": "shaneedwards/openac-plugin-hello" }
  ],
  "blocked": [
    { "id": "edwards.hello", "versions": ["VERSION"], "reason": "test" }
  ]
}"#;

fn hello_list(version: &str) -> PluginCatalog {
    PluginCatalog::parse(&HELLO_LIST.replace("VERSION", version)).unwrap()
}

#[test]
fn a_blocked_configured_plugin_is_filtered_with_one_status_line() {
    let mut chosen = character(LaunchMode::Gui, Some("0x5000000A"));
    chosen.plugins = Some(vec!["edwards.hello".into(), "edwards.tank".into()]);
    let composed = compose_simple(
        &chosen,
        "session-blocked-plugin",
        &paths(),
        Some(&hello_list("*")),
    );
    assert_eq!(
        session(&composed).plugins.as_deref(),
        Some(&["edwards.tank".to_owned()][..])
    );
    assert_eq!(
        composed.plugin_status_lines,
        ["Plugin 'edwards.hello' is blocked and was not loaded."]
    );
}

struct PluginRoot {
    root: TempDir,
}

impl PluginRoot {
    fn new() -> Self {
        Self {
            root: TempDir::new("composer-plugins"),
        }
    }

    fn paths(&self) -> PathSet {
        self.root.path_set()
    }

    fn write_folder(&self, folder: &str, write_entry: bool, id: Option<&str>) {
        let directory = self.paths().plugins_directory().join(folder);
        std::fs::create_dir_all(&directory).unwrap();
        let id = id.unwrap_or(folder);
        std::fs::write(
            directory.join("plugin.json"),
            format!(
                r#"{{
  "id": "{id}",
  "displayName": "{id}",
  "version": "0.1.0",
  "entry": "{id}.wasm",
  "apiVersion": "1.0",
  "minHostVersion": "0.1.0"
}}"#
            ),
        )
        .unwrap();
        if write_entry {
            std::fs::write(directory.join(format!("{id}.wasm")), []).unwrap();
        }
    }

    fn add_record(&self, id: &str) {
        let mut store = InstalledPluginRecordStore::for_paths(&self.paths());
        store.load().unwrap();
        store.records.push(InstalledPluginRecord {
            id: id.into(),
            repo: "shaneedwards/openac-plugin-hello".into(),
            source: PluginInstallSource::Listed,
            version: Some("0.1.0".into()),
            tag: Some("v0.1.0".into()),
            zip_sha256: Some("a".repeat(64)),
            installed_at: std::time::SystemTime::now(),
            pending: None,
            channel: PluginReleaseChannel::Stable,
        });
        store.save().unwrap();
    }
}

#[test]
fn a_block_on_one_version_filters_only_that_installed_version() {
    for (blocked, filtered) in [("0.1.0", true), ("0.0.9", false)] {
        let plugins = PluginRoot::new();
        plugins.write_folder("edwards.hello", true, None);
        let mut chosen = character(LaunchMode::Gui, Some("0x5000000A"));
        chosen.plugins = Some(vec!["edwards.hello".into()]);
        let composed = compose_simple(
            &chosen,
            "session-version-block",
            &plugins.paths(),
            Some(&hello_list(blocked)),
        );
        let expected: &[String] = if filtered {
            &[]
        } else {
            &["edwards.hello".to_owned()]
        };
        assert_eq!(session(&composed).plugins.as_deref(), Some(expected));
        assert_eq!(composed.plugin_status_lines.len(), usize::from(filtered));
    }
}

#[test]
fn the_cached_list_filters_blocked_ids_when_no_list_is_given() {
    let mut chosen = character(LaunchMode::Gui, Some("0x5000000A"));
    chosen.plugins = Some(vec!["edwards.hello".into()]);
    let composed = compose_simple(&chosen, "session-no-catalog-no-cache", &paths(), None);
    assert_eq!(
        session(&composed).plugins.as_deref(),
        Some(&["edwards.hello".to_owned()][..])
    );
    assert!(composed.plugin_status_lines.is_empty());

    let root = TempDir::new("composer-cache");
    let cached = PathSet {
        cache_directory: root.path.clone(),
        ..paths()
    };
    std::fs::write(
        root.join("plugins.json"),
        HELLO_LIST.replace("VERSION", "*"),
    )
    .unwrap();
    let composed = compose_simple(&chosen, "session-cached-catalog", &cached, None);
    assert_eq!(session(&composed).plugins.as_deref(), Some(&[][..]));
    assert_eq!(
        composed.plugin_status_lines,
        ["Plugin 'edwards.hello' is blocked and was not loaded."]
    );
}

#[test]
fn refused_and_duplicated_installs_are_excluded_with_a_status_line() {
    let plugins = PluginRoot::new();
    plugins.write_folder("edwards.broken", false, None);
    let mut chosen = character(LaunchMode::Gui, Some("0x5000000A"));
    chosen.plugins = Some(vec!["edwards.broken".into()]);
    let composed = compose_simple(&chosen, "session-refused-direct", &plugins.paths(), None);
    assert_eq!(session(&composed).plugins.as_deref(), Some(&[][..]));
    assert_eq!(
        composed.plugin_status_lines,
        ["Plugin 'edwards.broken' failed its install checks and was not loaded."]
    );

    let plugins = PluginRoot::new();
    plugins.write_folder("edwards.hello", true, None);
    plugins.add_record("edwards.hello");
    plugins.write_folder("aaa-stray", true, Some("edwards.hello"));
    let mut chosen = character(LaunchMode::Gui, Some("0x5000000A"));
    chosen.plugins = Some(vec!["edwards.hello".into()]);
    let composed = compose_simple(
        &chosen,
        "session-duplicated-install",
        &plugins.paths(),
        None,
    );
    assert_eq!(session(&composed).plugins.as_deref(), Some(&[][..]));
    assert_eq!(
        composed.plugin_status_lines,
        ["Plugin 'edwards.hello' has more than one copy installed and was not loaded."]
    );
}

#[test]
fn process_content_carries_the_install_record_and_omits_paths() {
    let composed = compose_simple(
        &character(LaunchMode::Gui, Some("0x5000000A")),
        "session-content",
        &paths(),
        None,
    );
    let json = serialize(&composed.document);
    assert!(json.contains("\"version\": 1"));
    assert!(!json.contains("\"paths\""));
    assert!(!json.contains("preparedAssetOverlayPath"));
    let content = composed.document.process.content.as_ref().unwrap();
    assert_eq!(content.dat_directory, Path::new("/dats"));
    assert_eq!(
        content.prepared_asset_path,
        Path::new("/data/acdream/pak/acdream.pak")
    );
}

#[test]
fn normal_play_sessions_omit_the_mode_field() {
    for mode in [LaunchMode::Gui, LaunchMode::GuiSelect, LaunchMode::Headless] {
        let composed = compose_simple(
            &character(mode, Some("0x5000000A")),
            "session-mode",
            &paths(),
            None,
        );
        assert!(!keys(&composed).contains(&"mode".to_owned()), "{mode:?}");
    }
}

#[test]
fn probe_mode_sets_mode_and_carries_an_explicit_load_none_allow_list() {
    let composed =
        compose_probe(&server(), &account(), &install(), &paths(), "session-probe").unwrap();
    assert_eq!(
        keys(&composed),
        sorted(&[
            "id",
            "mode",
            "endpoint",
            "account",
            "credential",
            "plugins",
            "statusFile"
        ])
    );
    let session = session(&composed);
    assert_eq!(session.mode, SessionMode::Probe);
    assert_eq!(session.endpoint.host, "127.0.0.1");
    assert_eq!(session.endpoint.port, 9000);
    assert_eq!(session.plugins.as_deref(), Some(&[][..]));
    assert!(session.login_commands.is_none());
    assert!(serialize(&composed.document).contains("\"mode\": \"probe\""));
}

#[test]
fn composed_documents_never_contain_the_password() {
    for mode in [LaunchMode::Gui, LaunchMode::GuiSelect, LaunchMode::Headless] {
        let composed = compose_simple(
            &character(mode, Some("0x5000000A")),
            "session-pw",
            &paths(),
            None,
        );
        assert!(!serialize(&composed.document).contains(PASSWORD));
    }
    let probe = compose_probe(
        &server(),
        &account(),
        &install(),
        &paths(),
        "session-probe-pw",
    )
    .unwrap();
    assert!(!serialize(&probe.document).contains(PASSWORD));
}

#[test]
fn compose_and_write_writes_the_document_under_the_expected_path() {
    let root = TempDir::new("composer-write");
    let paths = root.path_set();
    let composed = write(compose_simple(
        &character(LaunchMode::Gui, Some("0x5000000A")),
        "session-write",
        &paths,
        None,
    ))
    .unwrap();
    let expected = paths
        .launcher_sessions_directory()
        .join("session-write")
        .join("session.json");
    assert_eq!(composed.config_file_path, expected);
    let text = std::fs::read_to_string(&expected).unwrap();
    assert!(!text.contains(PASSWORD));

    let probe =
        write(compose_probe(&server(), &account(), &install(), &paths, "probe-write").unwrap())
            .unwrap();
    let json = std::fs::read_to_string(&probe.config_file_path).unwrap();
    assert!(json.contains("\"mode\": \"probe\""));
    assert!(!json.contains("\"character\""));
    assert!(!json.contains(PASSWORD));
}

#[test]
fn every_composed_document_round_trips_through_the_hosts_own_reader() {
    // OpenAC HostParity/LauncherSessionConfigRoundTripTests: what the
    // launcher writes is what the host reads. Graphical documents use the
    // window reader, windowless and probe documents the headless one.
    let mut overlay_install = install();
    overlay_install.prepared_asset_sha256 = "a".repeat(64);
    overlay_install.bake_tool_version = 9;
    overlay_install.prepared_asset_overlay_path = Some("/data/acdream/pak/overlay.pak".into());
    overlay_install.effective_bake_tool_version = 10;
    for (mode, reader) in [
        (LaunchMode::Gui, ConfigReader::Window),
        (LaunchMode::GuiSelect, ConfigReader::Window),
        (LaunchMode::Headless, ConfigReader::Headless),
    ] {
        for install in [install(), overlay_install.clone()] {
            let composed = compose(
                &server(),
                &account(),
                &character(mode, Some("0x5000000A")),
                &install,
                &paths(),
                "round-trip",
                Some(750),
                None,
            )
            .unwrap();
            let loaded = load_config(serialize(&composed.document).as_bytes(), reader)
                .unwrap_or_else(|error| panic!("{mode:?}: {error}"));
            assert_eq!(loaded, composed.document, "{mode:?}");
        }
    }
    let probe = compose_probe(
        &server(),
        &account(),
        &install(),
        &paths(),
        "round-trip-probe",
    )
    .unwrap();
    let loaded = load_config(
        serialize(&probe.document).as_bytes(),
        ConfigReader::Headless,
    )
    .unwrap();
    assert_eq!(loaded, probe.document);
}

// --- BoundedProcessOutputCaptureTests ---------------------------------------------

fn temp_log(root: &TempDir) -> PathBuf {
    root.join(format!(
        "capture-{}.log",
        ac_launcher_core::ids::unique_hex32()
    ))
}

#[test]
fn capture_writes_lines_ignores_end_of_stream_and_concatenates_raw_bytes() {
    let root = TempDir::new("capture");
    let path = temp_log(&root);
    let capture = BoundedProcessOutputCapture::with_default_cap(&path).unwrap();
    capture.append_line(Some("first"));
    capture.append_line(None);
    capture.append_line(Some("second"));
    capture.close();
    assert_eq!(std::fs::read_to_string(&path).unwrap(), "first\nsecond\n");

    let path = temp_log(&root);
    let capture = BoundedProcessOutputCapture::with_default_cap(&path).unwrap();
    capture.append(b"abc");
    capture.append(b"def");
    capture.append(b"");
    capture.append_line(Some(""));
    capture.close();
    capture.append_line(Some("after close"));
    assert_eq!(std::fs::read_to_string(&path).unwrap(), "abcdef\n");
}

fn read(path: &Path) -> String {
    std::fs::read_to_string(path).unwrap()
}

fn generation(path: &Path, generation: u32) -> PathBuf {
    PathBuf::from(format!("{}.{generation}", path.display()))
}

// OpenAC BoundedProcessOutputCaptureTests.RotatesAtTheBoundaryAndKeepsWritingRecentLines.
#[test]
fn capture_rotates_at_the_boundary_and_keeps_writing_recent_lines() {
    let root = TempDir::new("capture-rotate");
    let path = temp_log(&root);
    let capture = BoundedProcessOutputCapture::new(&path, 12, DEFAULT_MAX_FILES).unwrap();
    capture.append_line(Some("first"));
    capture.append_line(Some("next!"));
    capture.append_line(Some("third"));
    assert!(!capture.is_done());
    assert_eq!(read(&generation(&path, 1)), "first\nnext!\n");
    assert_eq!(read(&path), "third\n");
}

// OpenAC BoundedProcessOutputCaptureTests.OversizedRawWritesKeepTheirTailAcrossGenerations.
#[test]
fn capture_oversized_raw_writes_keep_their_tail_across_generations() {
    let root = TempDir::new("capture-oversized");
    let path = temp_log(&root);
    let capture = BoundedProcessOutputCapture::new(&path, 5, DEFAULT_MAX_FILES).unwrap();
    capture.append(b"abcdefghijkl");
    capture.close();
    assert_eq!(read(&generation(&path, 2)), "abcde");
    assert_eq!(read(&generation(&path, 1)), "fghij");
    assert_eq!(read(&path), "kl");
}

// OpenAC BoundedProcessOutputCaptureTests.OldestGenerationIsDiscardedButTheLatestOutputSurvivesExit.
#[test]
fn capture_discards_the_oldest_generation_but_the_latest_output_survives_exit() {
    let root = TempDir::new("capture-oldest");
    let path = temp_log(&root);
    let capture = BoundedProcessOutputCapture::new(&path, 4, 3).unwrap();
    for index in 0..8 {
        capture.append_line(Some(&format!("{index:03}")));
    }
    capture.close();
    assert!(capture.is_done());
    assert_eq!(read(&path), "007\n");
    assert_eq!(read(&generation(&path, 1)), "006\n");
    assert_eq!(read(&generation(&path, 2)), "005\n");
    assert!(!generation(&path, 3).exists());
}

// OpenAC BoundedProcessOutputCaptureTests.RotatingOneSessionDoesNotTouchAnotherSessionsLog.
#[test]
fn capture_rotating_one_session_does_not_touch_another_sessions_log() {
    let root = TempDir::new("capture-two");
    let (first, second) = (temp_log(&root), temp_log(&root));
    let first_capture = BoundedProcessOutputCapture::new(&first, 4, DEFAULT_MAX_FILES).unwrap();
    let second_capture = BoundedProcessOutputCapture::new(&second, 4, DEFAULT_MAX_FILES).unwrap();
    second_capture.append_line(Some("ok"));
    first_capture.append_line(Some("one"));
    first_capture.append_line(Some("two"));
    assert_eq!(read(&generation(&first, 1)), "one\n");
    assert_eq!(read(&first), "two\n");
    assert!(!generation(&second, 1).exists());
    assert_eq!(read(&second), "ok\n");
}

// Not an OpenAC test: a log-spamming child keeps at most four files of
// 2 MiB.
#[test]
fn a_log_spamming_child_cannot_grow_the_logs_unboundedly() {
    let root = TempDir::new("capture-spam");
    let path = temp_log(&root);
    let capture = BoundedProcessOutputCapture::with_default_cap(&path).unwrap();
    let line = "x".repeat(4096);
    for _ in 0..4096 {
        capture.append_line(Some(&line));
    }
    assert!(!capture.is_done());
    assert!(std::fs::metadata(&path).unwrap().len() <= DEFAULT_MAX_BYTES);
    for index in 1..DEFAULT_MAX_FILES {
        assert!(std::fs::metadata(generation(&path, index)).unwrap().len() <= DEFAULT_MAX_BYTES);
    }
    assert!(!generation(&path, DEFAULT_MAX_FILES).exists());
}
#[test]
fn capture_creates_its_directory_and_refuses_bad_arguments() {
    let root = TempDir::new("capture-dir");
    let path = root.join("session").join("client.err.log");
    let capture = BoundedProcessOutputCapture::with_default_cap(&path).unwrap();
    capture.append_line(Some("hello"));
    assert!(path.is_file());
    assert!(BoundedProcessOutputCapture::new(&path, 0, DEFAULT_MAX_FILES).is_err());
    assert!(BoundedProcessOutputCapture::new(&path, 10, 0).is_err());
    assert!(BoundedProcessOutputCapture::new(Path::new("   "), 10, DEFAULT_MAX_FILES).is_err());
    assert!(BoundedProcessOutputCapture::new(Path::new(""), 10, DEFAULT_MAX_FILES).is_err());
}

// --- LauncherProcessSupervisorTests (fakes) -------------------------------------------

#[derive(Default)]
struct FakeState {
    started: bool,
    stdin: String,
    stdin_closed: bool,
    calls: Vec<&'static str>,
    kills: usize,
    exited: bool,
    code: Option<i32>,
    on_exit: Option<ExitCallback>,
}

/// What the fake's kill does.
#[derive(Clone, Copy, Default, PartialEq, Eq)]
enum KillBehavior {
    /// The child exits.
    #[default]
    Exit,
    /// The child stays until the test ends it.
    Delay,
    /// The kill fails.
    Throw,
}

struct FakeChild {
    exits_within_stop_timeout: bool,
    throw_on_stdin_write: bool,
    exit_during_start: bool,
    kill_behavior: KillBehavior,
    throw_after_spawn: bool,
    state: Mutex<FakeState>,
}

impl FakeChild {
    fn exit_for_test(&self, code: i32) {
        let callback = {
            let mut state = self.state.lock().unwrap();
            state.exited = true;
            state.code = Some(code);
            state.on_exit.take()
        };
        if let Some(callback) = callback {
            callback(Some(code));
        }
    }

    fn state(&self) -> std::sync::MutexGuard<'_, FakeState> {
        self.state.lock().unwrap()
    }
}

impl ChildProcess for FakeChild {
    fn start(&self, on_exit: ExitCallback) -> std::io::Result<()> {
        {
            let mut state = self.state();
            state.started = true;
            state.on_exit = Some(on_exit);
        }
        if self.throw_after_spawn {
            return Err(std::io::Error::other("Fixture post-spawn setup failed."));
        }
        if self.exit_during_start {
            self.exit_for_test(0);
        }
        Ok(())
    }

    fn has_started(&self) -> bool {
        self.state().started
    }

    fn has_exited(&self) -> bool {
        self.state().exited
    }

    fn exit_code(&self) -> Option<i32> {
        self.state().code
    }

    fn write_input(&self, bytes: &[u8]) -> std::io::Result<()> {
        if self.throw_on_stdin_write {
            return Err(std::io::Error::other("stdin broke"));
        }
        let mut state = self.state();
        if state.stdin_closed {
            return Err(std::io::Error::other("closed"));
        }
        state.stdin.push_str(&String::from_utf8_lossy(bytes));
        Ok(())
    }

    fn close_input(&self) {
        self.state().stdin_closed = true;
    }

    fn try_request_graceful_stop(&self) -> bool {
        self.state().calls.push("gracefulStop");
        false
    }

    fn close_main_window(&self) -> bool {
        self.state().calls.push("closeMainWindow");
        if self.exits_within_stop_timeout {
            self.exit_for_test(0);
        }
        true
    }

    fn kill(&self) -> std::io::Result<()> {
        self.state().kills += 1;
        match self.kill_behavior {
            KillBehavior::Throw => return Err(std::io::Error::other("Fixture kill failed.")),
            KillBehavior::Exit => self.exit_for_test(-1),
            KillBehavior::Delay => {}
        }
        Ok(())
    }

    fn wait_for_exit(&self, _timeout: Option<Duration>) -> bool {
        self.state().exited
    }
}

#[derive(Default)]
struct FakeFactory {
    exits_within_stop_timeout: bool,
    throw_on_stdin_write: bool,
    exit_during_start: bool,
    kill_behavior: KillBehavior,
    throw_after_spawn: bool,
    last: Mutex<Option<Arc<FakeChild>>>,
}

impl FakeFactory {
    fn new(exits_within_stop_timeout: bool) -> Arc<Self> {
        Arc::new(Self {
            exits_within_stop_timeout,
            ..Self::default()
        })
    }

    fn last(&self) -> Arc<FakeChild> {
        self.last.lock().unwrap().clone().unwrap()
    }
}

impl ChildProcessFactory for FakeFactory {
    fn create(&self, _spec: &LauncherProcessSpec) -> Arc<dyn ChildProcess> {
        let child = Arc::new(FakeChild {
            exits_within_stop_timeout: self.exits_within_stop_timeout,
            throw_on_stdin_write: self.throw_on_stdin_write,
            exit_during_start: self.exit_during_start,
            kill_behavior: self.kill_behavior,
            throw_after_spawn: self.throw_after_spawn,
            state: Mutex::new(FakeState::default()),
        });
        *self.last.lock().unwrap() = Some(Arc::clone(&child));
        child
    }
}

fn spec() -> LauncherProcessSpec {
    LauncherProcessSpec::new(
        "fake-host",
        vec!["--session-config".into(), "session.json".into()],
    )
}

fn record_states(
    supervisor: &Arc<LauncherProcessSupervisor>,
) -> Arc<Mutex<Vec<LauncherSessionState>>> {
    let states = Arc::new(Mutex::new(Vec::new()));
    let sink = Arc::clone(&states);
    supervisor.subscribe(Arc::new(move |state| sink.lock().unwrap().push(state)));
    states
}

#[test]
fn start_writes_password_then_closes_stdin_and_transitions_to_running() {
    let factory = FakeFactory::new(true);
    let supervisor = LauncherProcessSupervisor::new(factory.clone());
    let states = record_states(&supervisor);
    assert!(!supervisor.has_live_process());
    supervisor.start(&spec(), Some(PASSWORD)).unwrap();
    let fake = factory.last();
    assert!(fake.state().started);
    assert!(supervisor.has_live_process());
    assert_eq!(fake.state().stdin, format!("{PASSWORD}\n"));
    assert!(fake.state().stdin_closed);
    assert_eq!(
        ProcessSupervisor::state(&*supervisor),
        LauncherSessionState::Running
    );
    assert_eq!(
        *states.lock().unwrap(),
        [
            LauncherSessionState::Starting,
            LauncherSessionState::Running
        ]
    );
    supervisor.stop(Duration::ZERO).unwrap();
    assert!(!supervisor.has_live_process());
}

// OpenAC LauncherProcessSupervisorTests.FailedStartupKeepsOwnershipUntilTheStartedChildActuallyExits.
#[test]
fn failed_startup_keeps_ownership_until_the_started_child_actually_exits() {
    for kill_behavior in [KillBehavior::Delay, KillBehavior::Throw] {
        for throw_after_spawn in [false, true] {
            let factory = Arc::new(FakeFactory {
                exits_within_stop_timeout: true,
                throw_on_stdin_write: !throw_after_spawn,
                kill_behavior,
                throw_after_spawn,
                ..FakeFactory::default()
            });
            let supervisor = LauncherProcessSupervisor::new(factory.clone());
            assert!(supervisor.start(&spec(), Some("pw")).is_err());
            let child = factory.last();
            assert!(supervisor.has_live_process());
            child.exit_for_test(-1);
            assert!(!supervisor.has_live_process());
            assert_eq!(
                ProcessSupervisor::state(&*supervisor),
                LauncherSessionState::Exited
            );
        }
    }
}

#[test]
fn a_console_session_keeps_its_input_open_and_takes_lines() {
    let factory = FakeFactory::new(true);
    let supervisor = LauncherProcessSupervisor::new(factory.clone());
    let console = LauncherProcessSpec {
        keep_standard_input_open: true,
        ..spec()
    };
    supervisor.start(&console, Some("pw")).unwrap();
    assert!(!factory.last().state().stdin_closed);
    assert!(supervisor.try_send_line("/vt start"));
    assert_eq!(factory.last().state().stdin, "pw\n/vt start\n");

    let factory = FakeFactory::new(true);
    let supervisor = LauncherProcessSupervisor::new(factory.clone());
    supervisor.start(&spec(), Some("pw")).unwrap();
    assert!(!supervisor.try_send_line("/vt start"));
    assert_eq!(factory.last().state().stdin, "pw\n");
}

#[test]
fn start_with_no_password_closes_stdin_without_writing_and_start_twice_fails() {
    let factory = FakeFactory::new(true);
    let supervisor = LauncherProcessSupervisor::new(factory.clone());
    supervisor.start(&spec(), None).unwrap();
    assert_eq!(factory.last().state().stdin, "");
    assert!(factory.last().state().stdin_closed);
    assert!(supervisor.start(&spec(), Some("pw")).is_err());
}

#[test]
fn stop_closes_gracefully_or_kills_after_the_timeout() {
    let factory = FakeFactory::new(true);
    let supervisor = LauncherProcessSupervisor::new(factory.clone());
    supervisor.start(&spec(), Some("pw")).unwrap();
    assert_eq!(
        supervisor.stop(Duration::from_millis(50)).unwrap(),
        StopOutcome::Exited
    );
    let fake = factory.last();
    assert_eq!(fake.state().calls, ["gracefulStop", "closeMainWindow"]);
    assert_eq!(fake.state().kills, 0);
    assert_eq!(
        ProcessSupervisor::state(&*supervisor),
        LauncherSessionState::Exited
    );
    assert_eq!(supervisor.exit_code(), Some(0));
    // A second stop after the exit does nothing.
    supervisor.stop(Duration::from_millis(50)).unwrap();
    assert_eq!(fake.state().calls.len(), 2);

    let factory = FakeFactory::new(false);
    let supervisor = LauncherProcessSupervisor::new(factory.clone());
    supervisor.start(&spec(), Some("pw")).unwrap();
    // A stop that had to kill says so (the launcher counts it as a stop,
    // not a crash).
    assert_eq!(
        supervisor.stop(Duration::from_millis(50)).unwrap(),
        StopOutcome::Killed
    );
    assert_eq!(factory.last().state().kills, 1);
    assert_eq!(
        ProcessSupervisor::state(&*supervisor),
        LauncherSessionState::Exited
    );
}

#[test]
fn stop_is_a_no_op_before_start() {
    let factory = FakeFactory::new(true);
    let supervisor = LauncherProcessSupervisor::new(factory.clone());
    supervisor.stop(Duration::from_millis(50)).unwrap();
    assert!(factory.last.lock().unwrap().is_none());
    assert_eq!(
        ProcessSupervisor::state(&*supervisor),
        LauncherSessionState::Starting
    );
}

#[test]
fn start_kills_the_child_when_feeding_stdin_fails_after_it_started() {
    let factory = Arc::new(FakeFactory {
        exits_within_stop_timeout: true,
        throw_on_stdin_write: true,
        ..FakeFactory::default()
    });
    let supervisor = LauncherProcessSupervisor::new(factory.clone());
    assert!(supervisor.start(&spec(), Some("pw")).is_err());
    assert!(factory.last().state().started);
    assert_eq!(factory.last().state().kills, 1);
}

#[test]
fn set_state_is_monotonic_and_ignores_a_transition_after_exited() {
    let factory = Arc::new(FakeFactory {
        exits_within_stop_timeout: true,
        exit_during_start: true,
        ..FakeFactory::default()
    });
    let supervisor = LauncherProcessSupervisor::new(factory);
    let states = record_states(&supervisor);
    supervisor.start(&spec(), Some("pw")).unwrap();
    assert_eq!(
        ProcessSupervisor::state(&*supervisor),
        LauncherSessionState::Exited
    );
    assert_eq!(
        *states.lock().unwrap(),
        [LauncherSessionState::Starting, LauncherSessionState::Exited]
    );
}

#[test]
fn concurrent_running_and_exited_publications_remain_monotonic_and_in_order() {
    let factory = FakeFactory::new(true);
    let supervisor = LauncherProcessSupervisor::new(factory.clone());
    let entered = Arc::new((Mutex::new(false), Condvar::new()));
    let release = Arc::new((Mutex::new(false), Condvar::new()));
    let states = Arc::new(Mutex::new(Vec::new()));
    {
        let entered = Arc::clone(&entered);
        let release = Arc::clone(&release);
        let states = Arc::clone(&states);
        supervisor.subscribe(Arc::new(move |state| {
            if state == LauncherSessionState::Running {
                *entered.0.lock().unwrap() = true;
                entered.1.notify_all();
                let mut released = release.0.lock().unwrap();
                while !*released {
                    released = release.1.wait(released).unwrap();
                }
            }
            states.lock().unwrap().push(state);
        }));
    }
    let starter = {
        let supervisor = Arc::clone(&supervisor);
        std::thread::spawn(move || supervisor.start(&spec(), Some("pw")).unwrap())
    };
    {
        let mut is_entered = entered.0.lock().unwrap();
        while !*is_entered {
            is_entered = entered.1.wait(is_entered).unwrap();
        }
    }
    factory.last().exit_for_test(17);
    assert_eq!(
        ProcessSupervisor::state(&*supervisor),
        LauncherSessionState::Exited
    );
    *release.0.lock().unwrap() = true;
    release.1.notify_all();
    starter.join().unwrap();
    assert_eq!(
        *states.lock().unwrap(),
        [
            LauncherSessionState::Starting,
            LauncherSessionState::Running,
            LauncherSessionState::Exited
        ]
    );
    assert_eq!(supervisor.exit_code(), Some(17));
}

#[test]
fn state_changed_publication_allows_cross_thread_reads_and_reentrant_exit() {
    let factory = FakeFactory::new(true);
    let supervisor = LauncherProcessSupervisor::new(factory.clone());
    let states = Arc::new(Mutex::new(Vec::new()));
    {
        let states = Arc::clone(&states);
        let reader = Arc::downgrade(&supervisor);
        let factory = Arc::clone(&factory);
        supervisor.subscribe(Arc::new(move |state| {
            states.lock().unwrap().push(state);
            if state != LauncherSessionState::Running {
                return;
            }
            let supervisor = reader.upgrade().unwrap();
            let read = std::thread::spawn(move || ProcessSupervisor::state(&*supervisor))
                .join()
                .unwrap();
            assert_eq!(read, LauncherSessionState::Running);
            factory.last().exit_for_test(23);
        }));
    }
    supervisor.start(&spec(), Some("pw")).unwrap();
    assert_eq!(
        *states.lock().unwrap(),
        [
            LauncherSessionState::Starting,
            LauncherSessionState::Running,
            LauncherSessionState::Exited
        ]
    );
    assert_eq!(supervisor.exit_code(), Some(23));
}

#[test]
fn dispose_stops_the_child_and_publishes_exited_once() {
    let factory = FakeFactory::new(true);
    let supervisor = LauncherProcessSupervisor::new(factory.clone());
    let states = record_states(&supervisor);
    supervisor.start(&spec(), None).unwrap();
    supervisor.dispose();
    supervisor.dispose();
    assert!(factory.last().state().exited);
    assert_eq!(
        states
            .lock()
            .unwrap()
            .iter()
            .filter(|state| **state == LauncherSessionState::Exited)
            .count(),
        1
    );
    assert!(!supervisor.try_send_line("after dispose"));
}

#[test]
fn the_process_spec_carries_no_credential_like_member() {
    let debug = format!("{:?}", LauncherProcessSpec::default()).to_lowercase();
    assert!(!debug.contains("password"));
    assert!(!debug.contains("credential"));
}

// --- Real children ------------------------------------------------------------------

/// OpenAC's ConsoleSignalChild, as a test run in a child process:
/// `write-stderr <code> <count> <prefix>` writes lines to standard error and
/// exits with the code; `wait-for-break <ready> <break> <label> args...`
/// reads its input to the end, writes a ready file naming its arguments and
/// input size, and waits for a console stop, which it records and exits 0;
/// `echo-exit <code>` reads its input and exits with the code.
#[test]
fn fixture_console_child() {
    let Some(args) = fixture_args() else {
        return;
    };
    match args[0].as_str() {
        "write-stderr" => {
            let code: i32 = args[1].parse().unwrap();
            let count: usize = args[2].parse().unwrap();
            for index in 0..count {
                eprintln!("{}{index}", args[3]);
            }
            std::process::exit(code);
        }
        "echo-exit" => {
            let mut input = String::new();
            let _ = std::io::stdin().read_to_string(&mut input);
            std::process::exit(args[1].parse().unwrap());
        }
        "wait-for-break" => {
            let (ready, breaks, label) = (args[1].clone(), args[2].clone(), args[3].clone());
            let stopped = Arc::new(AtomicBool::new(false));
            let flag = Arc::clone(&stopped);
            let marker = breaks.clone();
            let name = label.clone();
            ctrlc::set_handler(move || {
                let _ = std::fs::write(&marker, &name);
                flag.store(true, Ordering::SeqCst);
            })
            .unwrap();
            let mut input = String::new();
            let _ = std::io::stdin().read_to_string(&mut input);
            let arguments: Vec<String> = args[4..]
                .iter()
                .map(|argument| ac_launcher_core::json::quote(argument))
                .collect();
            std::fs::write(
                &ready,
                format!(
                    r#"{{"label":{},"arguments":[{}],"stdinLength":{},"stdinLineCount":{}}}"#,
                    ac_launcher_core::json::quote(&label),
                    arguments.join(","),
                    input.len(),
                    input.matches('\n').count()
                ),
            )
            .unwrap();
            let deadline = Instant::now() + Duration::from_secs(30);
            while !stopped.load(Ordering::SeqCst) && Instant::now() < deadline {
                std::thread::sleep(Duration::from_millis(10));
            }
            std::process::exit(if stopped.load(Ordering::SeqCst) {
                0
            } else {
                75
            });
        }
        other => panic!("unknown fixture {other}"),
    }
}

fn fixture_spec(args: &[&str]) -> LauncherProcessSpec {
    let command = fixture_command("fixture_console_child", args);
    let mut spec = LauncherProcessSpec::new(
        command.get_program(),
        command
            .get_args()
            .map(|argument| argument.to_string_lossy().into_owned())
            .collect(),
    );
    spec.environment = command
        .get_envs()
        .filter_map(|(name, value)| {
            Some((
                name.to_string_lossy().into_owned(),
                value?.to_string_lossy().into_owned(),
            ))
        })
        .collect();
    spec
}

fn wait_for_exited(supervisor: &Arc<LauncherProcessSupervisor>, timeout: Duration) -> bool {
    let deadline = Instant::now() + timeout;
    while Instant::now() < deadline {
        if ProcessSupervisor::state(&**supervisor) == LauncherSessionState::Exited {
            return true;
        }
        std::thread::sleep(Duration::from_millis(10));
    }
    false
}

fn read_eventually_containing(path: &Path, fragment: &str) -> String {
    let deadline = Instant::now() + Duration::from_secs(5);
    let mut last = String::new();
    while Instant::now() < deadline {
        if let Ok(text) = std::fs::read_to_string(path) {
            if text.contains(fragment) {
                return text;
            }
            last = text;
        }
        std::thread::sleep(Duration::from_millis(20));
    }
    panic!(
        "'{}' never contained '{fragment}'; last: {last}",
        path.display()
    );
}

#[test]
fn a_real_process_is_fed_its_input_and_its_exit_code_captured() {
    let supervisor = LauncherProcessSupervisor::system();
    supervisor
        .start(&fixture_spec(&["echo-exit", "0"]), Some("unused-password"))
        .unwrap();
    assert!(wait_for_exited(&supervisor, Duration::from_secs(30)));
    assert_eq!(supervisor.exit_code(), Some(0));

    let supervisor = LauncherProcessSupervisor::system();
    supervisor
        .start(&fixture_spec(&["echo-exit", "5"]), None)
        .unwrap();
    assert!(wait_for_exited(&supervisor, Duration::from_secs(30)));
    assert_eq!(supervisor.exit_code(), Some(5));
}

#[test]
fn real_child_stderr_is_captured_for_both_child_kinds() {
    for (console_stop, code, prefix) in [(false, "7", "line-"), (true, "9", "native-line-")] {
        let root = TempDir::new("stderr-capture");
        let stderr = root.join("client.err.log");
        let mut spec = fixture_spec(&["write-stderr", code, "3", prefix]);
        spec.supports_console_graceful_stop = console_stop;
        spec.stderr_log_path = Some(stderr.clone());
        let supervisor = LauncherProcessSupervisor::system();
        supervisor.start(&spec, None).unwrap();
        assert!(wait_for_exited(&supervisor, Duration::from_secs(30)));
        assert_eq!(supervisor.exit_code(), Some(code.parse().unwrap()));
        let captured = read_eventually_containing(&stderr, &format!("{prefix}2"));
        assert!(captured.contains(&format!("{prefix}0")));
        assert!(captured.contains(&format!("{prefix}1")));
    }
}

/// A child process that counts kills, around a real one.
struct RecordingChild {
    inner: Arc<dyn ChildProcess>,
    kills: AtomicUsize,
}

impl ChildProcess for RecordingChild {
    fn start(&self, on_exit: ExitCallback) -> std::io::Result<()> {
        self.inner.start(on_exit)
    }
    fn has_exited(&self) -> bool {
        self.inner.has_exited()
    }
    fn exit_code(&self) -> Option<i32> {
        self.inner.exit_code()
    }
    fn write_input(&self, bytes: &[u8]) -> std::io::Result<()> {
        self.inner.write_input(bytes)
    }
    fn close_input(&self) {
        self.inner.close_input();
    }
    fn try_request_graceful_stop(&self) -> bool {
        self.inner.try_request_graceful_stop()
    }
    fn close_main_window(&self) -> bool {
        self.inner.close_main_window()
    }
    fn kill(&self) -> std::io::Result<()> {
        self.kills.fetch_add(1, Ordering::SeqCst);
        self.inner.kill()
    }
    fn wait_for_exit(&self, timeout: Option<Duration>) -> bool {
        self.inner.wait_for_exit(timeout)
    }
}

#[derive(Default)]
struct RecordingFactory {
    last: Mutex<VecDeque<Arc<RecordingChild>>>,
}

impl ChildProcessFactory for RecordingFactory {
    fn create(&self, spec: &LauncherProcessSpec) -> Arc<dyn ChildProcess> {
        let child = Arc::new(RecordingChild {
            inner: SystemChildProcessFactory.create(spec),
            kills: AtomicUsize::new(0),
        });
        self.last.lock().unwrap().push_back(Arc::clone(&child));
        child
    }
}

impl RecordingFactory {
    fn last(&self) -> Arc<RecordingChild> {
        self.last.lock().unwrap().back().cloned().unwrap()
    }
}

fn wait_for_ready(path: &Path, child: &RecordingChild) {
    let deadline = Instant::now() + Duration::from_secs(20);
    while !path.exists() {
        assert!(
            !child.has_exited(),
            "fixture exited early with {:?}",
            child.exit_code()
        );
        assert!(Instant::now() < deadline, "fixture did not become ready");
        std::thread::sleep(Duration::from_millis(20));
    }
    // The file is written in one call; wait until it parses.
    let deadline = Instant::now() + Duration::from_secs(5);
    while ac_host::json::parse(&std::fs::read(path).unwrap_or_default()).is_err() {
        assert!(Instant::now() < deadline, "ready file never completed");
        std::thread::sleep(Duration::from_millis(10));
    }
}

#[test]
fn a_console_stop_ends_only_the_target_process_group_without_a_kill() {
    // OpenAC WindowsCtrlBreakStopsOnlyTheTargetProcessGroupWithoutKill and
    // GracefulStopSignalSendsSigintToARealChildOnUnix in one test: Ctrl+Break
    // on Windows, SIGINT elsewhere.
    let root = TempDir::new("console-stop");
    let target_ready = root.join("target.ready.json");
    let target_break = root.join("target.break");
    let sibling_ready = root.join("sibling.ready.json");
    let sibling_break = root.join("sibling.break");
    let exact = [
        "plain",
        "contains spaces",
        "quoted-\"value",
        "ends-with-backslash\\",
        "",
    ];
    let target_factory = Arc::new(RecordingFactory::default());
    let sibling_factory = Arc::new(RecordingFactory::default());
    let target = LauncherProcessSupervisor::new(target_factory.clone());
    let sibling = LauncherProcessSupervisor::new(sibling_factory.clone());

    let target_ready_text = target_ready.to_string_lossy().into_owned();
    let target_break_text = target_break.to_string_lossy().into_owned();
    let mut target_args = vec![
        "wait-for-break",
        &target_ready_text,
        &target_break_text,
        "target",
    ];
    target_args.extend(exact);
    target
        .start(&fixture_spec(&target_args), Some("fixture-input"))
        .unwrap();
    let sibling_ready_text = sibling_ready.to_string_lossy().into_owned();
    let sibling_break_text = sibling_break.to_string_lossy().into_owned();
    sibling
        .start(
            &fixture_spec(&[
                "wait-for-break",
                &sibling_ready_text,
                &sibling_break_text,
                "sibling",
                "sibling",
            ]),
            None,
        )
        .unwrap();
    wait_for_ready(&target_ready, &target_factory.last());
    wait_for_ready(&sibling_ready, &sibling_factory.last());

    let ready = ac_host::json::parse(&std::fs::read(&target_ready).unwrap()).unwrap();
    let members = ac_host::json::object_members(&ready).unwrap();
    let member = |name: &str| &members.iter().find(|m| m.name == name).unwrap().value;
    let observed: Vec<String> = ac_host::json::array_items(member("arguments"))
        .unwrap()
        .iter()
        .map(|item| ac_host::json::string_or_null(item).unwrap().unwrap())
        .collect();
    assert_eq!(observed, exact);
    assert_eq!(
        ac_host::json::int32(member("stdinLength")).unwrap(),
        "fixture-input\n".len() as i32
    );
    assert_eq!(ac_host::json::int32(member("stdinLineCount")).unwrap(), 1);

    target.stop(Duration::from_secs(10)).unwrap();
    assert!(wait_for_exited(&target, Duration::from_secs(10)));
    assert_eq!(target.exit_code(), Some(0));
    assert!(
        target_break.exists(),
        "the target did not observe the console stop"
    );
    assert_eq!(target_factory.last().kills.load(Ordering::SeqCst), 0);
    assert!(!sibling_factory.last().has_exited());
    assert!(
        !sibling_break.exists(),
        "the console stop spilled into the sibling"
    );

    sibling.stop(Duration::from_secs(10)).unwrap();
    assert!(wait_for_exited(&sibling, Duration::from_secs(10)));
    assert_eq!(sibling.exit_code(), Some(0));
    assert!(sibling_break.exists());
    assert_eq!(sibling_factory.last().kills.load(Ordering::SeqCst), 0);
}

/// The release launcher is a windowed program with no console of its own,
/// so its Stop must attach to the child's console to send the Ctrl+Break
/// (a suspicion from a smoke test). Here a
/// launcher process started detached (no console) starts and stops a child;
/// the child must see the console stop and exit 0 without a kill.
#[cfg(windows)]
#[test]
fn a_launcher_without_a_console_stops_its_child_without_a_kill() {
    use std::os::windows::process::CommandExt;
    const DETACHED_PROCESS: u32 = 0x0000_0008;
    let root = TempDir::new("windowless-stop");
    let result = root.join("result.txt");
    let root_text = root.path.to_string_lossy().into_owned();
    let result_text = result.to_string_lossy().into_owned();
    let mut launcher = fixture_command("fixture_windowless_launcher", &[&root_text, &result_text]);
    launcher
        .creation_flags(DETACHED_PROCESS)
        .stdin(std::process::Stdio::null())
        .stdout(std::process::Stdio::null())
        .stderr(std::process::Stdio::null());
    let status = launcher.status().unwrap();
    let report = std::fs::read_to_string(&result).unwrap_or_default();
    assert!(status.success(), "launcher fixture failed: {report}");
    assert_eq!(
        report,
        "console: none; stopped: true; exit: Some(0); break seen: true; kills: 0"
    );
}

/// The launcher side of the test above, run detached: reports whether it
/// has a console, then starts a `wait-for-break` child through the real
/// supervisor and stops it.
#[cfg(windows)]
#[test]
#[allow(unsafe_code)]
fn fixture_windowless_launcher() {
    use windows_sys::Win32::System::Console::GetConsoleProcessList;
    let Some(args) = fixture_args() else {
        return;
    };
    let (root, result) = (PathBuf::from(&args[0]), PathBuf::from(&args[1]));
    let mut processes = [0u32; 1];
    // SAFETY: a writable one-element buffer and its length; the call only
    // reports how many processes share this process's console.
    let console = if unsafe { GetConsoleProcessList(processes.as_mut_ptr(), 1) } == 0 {
        "none"
    } else {
        "attached"
    };
    let ready = root.join("child.ready.json");
    let breaks = root.join("child.break");
    let (ready_text, break_text) = (
        ready.to_string_lossy().into_owned(),
        breaks.to_string_lossy().into_owned(),
    );
    let factory = Arc::new(RecordingFactory::default());
    let supervisor = LauncherProcessSupervisor::new(factory.clone());
    supervisor
        .start(
            &fixture_spec(&["wait-for-break", &ready_text, &break_text, "child", "x"]),
            None,
        )
        .unwrap();
    wait_for_ready(&ready, &factory.last());
    supervisor.stop(Duration::from_secs(10)).unwrap();
    let stopped = wait_for_exited(&supervisor, Duration::from_secs(10));
    std::fs::write(
        &result,
        format!(
            "console: {console}; stopped: {stopped}; exit: {:?}; break seen: {}; kills: {}",
            supervisor.exit_code(),
            breaks.exists(),
            factory.last().kills.load(Ordering::SeqCst)
        ),
    )
    .unwrap();
}
