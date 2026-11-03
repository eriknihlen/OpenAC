//! The plugin host against real plugins built to wasm (`tests/fixtures/
//! plugins`). Session behaviour ported from OpenAC
//! `tests/AcDream.Core.Tests/Plugins/PluginSessionTests.cs` and
//! `PluginLoaderTests.cs`; the WebAssembly specifics (permissions, time limits,
//! memory caps, traps) are this host's own.

use std::path::{Path, PathBuf};
use std::sync::Arc;

use ac_plugin::abi::layout::{LAYOUT_SECTION_BYTES, core_shape, import_key, section_at};
use ac_plugin::abi::{
    API_CURRENT, ApiVersion, CharacterInfo, ChatLine, Event, Import, LAYOUT_SECTION, LogLevel, Wire,
};
use ac_plugin::engine::{LAYOUT_REFUSAL, check_layout};
use ac_plugin::fixtures::{fresh_root, install, with_custom_section};
use ac_plugin::{
    HostVersion, PluginEngine, PluginReport, PluginSession, PluginSessionOptions,
    PluginSessionSettings, SessionApi,
};

/// The session side, recorded.
#[derive(Debug, Default)]
struct FakeSession {
    window: bool,
    character: Option<CharacterInfo>,
    logs: Vec<(String, LogLevel, String)>,
    posts: Vec<String>,
    ui: ac_plugin_ui::PluginUi,
    /// Carries out submits at once (as the player speaking them) and
    /// hands the lines it writes back as new chat lines, as the runtime
    /// does; off, submits wait for the session.
    speaks: bool,
    /// Lines written and not yet asked for.
    unheard: Vec<ChatLine>,
    /// How many spells the fake spell table holds.
    spell_table: u32,
    /// How many spells each known-spell list holds.
    known: u32,
    /// The character is in a stay in the world.
    in_world: bool,
    /// The objects placed in the fake world.
    entities: Vec<ac_plugin::abi::world::EntitySnapshot>,
    /// Answers the clock with the computer's (off: the default reply, all
    /// zero, which keeps census lines the same on every run).
    clock: bool,
}

/// A spell record as long as the retail table's: three strings.
fn fake_spell(spell_id: u32) -> ac_plugin::abi::magic::SpellInfo {
    ac_plugin::abi::magic::SpellInfo {
        spell_id,
        name: format!("Fake Spell Of Testing {spell_id}"),
        description: "Increases the target's ability to test things by a good amount for \n                      a while, as the retail descriptions run on."
            .to_owned(),
        saying: "Zojak Tugeth".to_owned(),
        formula_component_ids: vec![1, 2, 3, 4, 5, 6, 7, 8],
        ..ac_plugin::abi::magic::SpellInfo::default()
    }
}

impl SessionApi for FakeSession {
    fn in_stay(&self) -> bool {
        self.in_world
    }
    fn entities(&self) -> Vec<ac_plugin::abi::world::EntitySnapshot> {
        self.entities.clone()
    }
    fn call(
        &mut self,
        _caller: ac_plugin::Caller<'_>,
        import: Import,
        request: &[u8],
    ) -> Result<Vec<u8>, String> {
        use ac_plugin::abi::{calls, magic::SpellPage, serve};
        let total = self.spell_table;
        match import {
            Import::SpellsPage => serve::<calls::SpellsPage>(request, |(first, count)| SpellPage {
                total,
                spells: (first..total.min(first.saturating_add(count.min(256))))
                    .map(fake_spell)
                    .collect(),
            })
            .map_err(|error| error.to_string()),
            Import::SpellsKnown => {
                serve::<calls::SpellsKnown>(request, |_| (1..=self.known).map(fake_spell).collect())
                    .map_err(|error| error.to_string())
            }
            Import::SpellsKnownIds => {
                serve::<calls::SpellsKnownIds>(request, |_| (1..=self.known).collect())
                    .map_err(|error| error.to_string())
            }
            Import::HostClock if self.clock => serve::<calls::HostClock>(request, |()| {
                let since_epoch = std::time::SystemTime::now()
                    .duration_since(std::time::UNIX_EPOCH)
                    .unwrap_or_default();
                ac_plugin::abi::host::Clock {
                    utc_unix_100ns: i64::try_from(since_epoch.as_nanos() / 100).unwrap_or(i64::MAX),
                    ..ac_plugin::abi::host::Clock::default()
                }
            })
            .map_err(|error| error.to_string()),
            // Each landblock asked for, its vertices at the landblock's
            // number in metres.
            Import::LandscapeTerrain => serve::<calls::LandscapeTerrain>(request, |landblocks| {
                landblocks
                    .into_iter()
                    .map(|landblock| ac_plugin::abi::world::LandblockTerrain {
                        landblock,
                        vertices: vec![
                            ac_plugin::abi::world::TerrainVertex {
                                terrain_type: 1,
                                road: 0,
                                height_meters: f32::from(landblock),
                            };
                            81
                        ],
                    })
                    .collect()
            })
            .map_err(|error| error.to_string()),
            Import::LandscapeTerrainPalette => {
                serve::<calls::LandscapeTerrainPalette>(request, |()| {
                    vec![ac_plugin::abi::world::TerrainColor {
                        terrain_type: 1,
                        rgb: 0x5A_5E30,
                        map_rgb: 0x64_9664,
                        name: Some("Grassland".into()),
                    }]
                })
                .map_err(|error| error.to_string())
            }
            other => Ok(other.default_reply()),
        }
    }
    fn log(&mut self, plugin_id: &str, level: LogLevel, message: &str) {
        self.logs
            .push((plugin_id.to_owned(), level, message.to_owned()));
    }
    fn window_attached(&self) -> bool {
        self.window
    }
    fn post_chat(&mut self, text: &str, _log_text_type: u32) {
        self.posts.push(text.to_owned());
        if self.speaks {
            self.unheard.push(ChatLine {
                text: text.to_owned(),
                display_text: text.to_owned(),
                ..ChatLine::default()
            });
        }
    }
    fn submit_now(&mut self, submits: &mut Vec<String>) {
        if !self.speaks {
            return;
        }
        for text in std::mem::take(submits) {
            self.unheard.push(ChatLine {
                sender: "Horan".into(),
                display_text: format!("Horan says, \"{text}\""),
                text,
                ..ChatLine::default()
            });
        }
    }
    fn new_chat_events(&mut self) -> Vec<Event> {
        std::mem::take(&mut self.unheard)
            .into_iter()
            .map(Event::ChatReceived)
            .collect()
    }
    fn character(&self) -> Option<CharacterInfo> {
        self.character.clone()
    }
    fn plugin_ui(&mut self) -> Option<&mut ac_plugin_ui::PluginUi> {
        Some(&mut self.ui)
    }
}

impl FakeSession {
    fn messages(&self, plugin: &str) -> Vec<String> {
        self.logs
            .iter()
            .filter(|(id, _, _)| id == plugin)
            .map(|(_, _, m)| m.clone())
            .collect()
    }
}

fn engine() -> Arc<PluginEngine> {
    Arc::new(PluginEngine::new())
}

/// The deadline of a tick, event, command or callback in the tests of a
/// stuck plugin, so they do not wait the real ten seconds.
const TEST_DEADLINE: std::time::Duration = std::time::Duration::from_secs(1);

/// An engine whose calls are stopped after [`TEST_DEADLINE`].
fn short_engine() -> Arc<PluginEngine> {
    Arc::new(PluginEngine::with_time_limits(ac_plugin::TimeLimits {
        call: TEST_DEADLINE,
        ..ac_plugin::TimeLimits::default()
    }))
}

fn options(roots: &[&Path], allow: Option<&[&str]>) -> PluginSessionOptions {
    PluginSessionOptions {
        roots: roots.iter().map(|r| r.to_path_buf()).collect(),
        allow_list: allow.map(|ids| ids.iter().map(|s| (*s).to_owned()).collect()),
        settings: PluginSessionSettings::empty(),
        host_version: HostVersion::new(0, 1, 0),
        storage: ac_plugin::storage::StorageRoots::default(),
        slow_log: None,
    }
}

fn session(roots: &[&Path], allow: Option<&[&str]>) -> PluginSession {
    PluginSession::new(engine(), options(roots, allow))
}

fn failed(reports: &[PluginReport], id: &str) -> Option<String> {
    reports.iter().find_map(|r| match r {
        PluginReport::Failed { id: failed, error } if failed == id => Some(error.clone()),
        _ => None,
    })
}

fn edit_manifest(folder: &Path, from: &str, to: &str) {
    let path = folder.join("plugin.json");
    let text = std::fs::read_to_string(&path).unwrap();
    assert!(text.contains(from), "{from}");
    std::fs::write(&path, text.replace(from, to)).unwrap();
}

struct Root(PathBuf);

impl Root {
    fn new(tag: &str) -> Self {
        Self(fresh_root(tag))
    }
}

impl Drop for Root {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}

/// OpenAC `PluginLootClassifierRegistryTests` across two real plugins:
/// `probe` registers `openac.probe/rules`, `hello` asks it through the
/// registry (ignoring case) and gets its verdict; once `probe` unloads, the
/// classifier is gone.
#[test]
fn a_plugin_asks_another_plugins_loot_classifier() {
    let root = Root::new("classifier");
    install(&root.0, "hello");
    install(&root.0, "probe");
    let mut api = FakeSession::default();
    let mut plugins = session(&[&root.0], Some(&["openac.hello", "openac.probe"]));
    plugins.start(&mut api);
    assert_eq!(plugins.loaded_ids(), ["openac.hello", "openac.probe"]);
    assert!(plugins.handle_line("/hello classify OPENAC.PROBE/rules", &mut api));
    assert_eq!(
        api.posts.last().unwrap(),
        "hello classify OPENAC.PROBE/rules: Keep by probe"
    );
    assert!(plugins.handle_line("/hello classify openac.probe/other", &mut api));
    assert_eq!(
        api.posts.last().unwrap(),
        "hello classify openac.probe/other: none"
    );
    plugins.request_reload("openac.probe");
    plugins.pump(0.0, None, &mut api);
    plugins.shutdown(&mut api);
    let mut plugins = session(&[&root.0], Some(&["openac.hello"]));
    plugins.start(&mut api);
    assert!(plugins.handle_line("/hello classify openac.probe/rules", &mut api));
    assert_eq!(
        api.posts.last().unwrap(),
        "hello classify openac.probe/rules: none"
    );
    plugins.shutdown(&mut api);
}

/// OpenAC `FilePluginStorage` through a real plugin: a write lands under the
/// plugin's own folder in its character scope (written atomically: no
/// temporary file is left), reads back, lists by key, and the profiles root
/// is refused without its permission.
#[test]
fn a_plugin_stores_text_in_its_own_folder() {
    let root = Root::new("storage-plugins");
    let data = Root::new("storage-data");
    install(&root.0, "hello");
    let mut options = options(&[&root.0], Some(&["openac.hello"]));
    options.storage = ac_plugin::storage::StorageRoots {
        plugin_data: Some(data.0.clone()),
        profiles: Some(data.0.join("vtank")),
    };
    let mut plugins = PluginSession::new(engine(), options);
    let mut api = FakeSession::default();
    plugins.start(&mut api);
    assert!(plugins.handle_line("/hello store kept words", &mut api));
    assert_eq!(
        api.posts.last().unwrap(),
        "hello store: Ok(()) Ok(Some(\"kept words\")) Ok([\"notes/hello.txt\"]) true"
    );
    let folder = data
        .0
        .join("openac.hello")
        .join(ac_host::paths::PLUGIN_FILES_FOLDER_NAME)
        .join("character")
        .join("+Horan");
    assert_eq!(
        std::fs::read_to_string(folder.join("notes").join("hello.txt")).unwrap(),
        "kept words"
    );
    let left: Vec<_> = std::fs::read_dir(folder.join("notes"))
        .unwrap()
        .map(|e| e.unwrap().file_name())
        .collect();
    assert_eq!(left, ["hello.txt"]);
    plugins.shutdown(&mut api);
}

/// Network hosts through a real plugin: a host its manifest lists is
/// reached at once and the reply comes back as its event; another host is
/// refused, the player is asked once, and `/plugin allow` lets it through.
#[test]
fn a_plugin_reaches_listed_hosts_and_the_player_approves_others() {
    use std::io::{Read, Write};
    let server = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
    let port = server.local_addr().unwrap().port();
    std::thread::spawn(move || {
        for stream in server.incoming().take(2) {
            let mut stream = stream.unwrap();
            let mut head = Vec::new();
            let mut byte = [0u8; 1];
            while !head.ends_with(b"\r\n\r\n") && stream.read(&mut byte).unwrap() == 1 {
                head.push(byte[0]);
            }
            stream
                .write_all(b"HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nhi")
                .unwrap();
        }
    });
    let root = Root::new("net");
    install(&root.0, "fetcher");
    let mut api = FakeSession::default();
    let mut plugins = session(&[&root.0], Some(&["openac.fetcher"]));
    plugins.start(&mut api);
    // Pumps until `text` has been posted `times` times.
    let wait_for =
        |plugins: &mut PluginSession, api: &mut FakeSession, text: &str, times: usize| {
            let count = |api: &FakeSession| api.posts.iter().filter(|p| *p == text).count();
            let start = std::time::Instant::now();
            while count(api) < times && start.elapsed() < std::time::Duration::from_secs(10) {
                plugins.pump(0.0, Some(1), api);
                std::thread::sleep(std::time::Duration::from_millis(5));
            }
            assert_eq!(count(api), times, "{:?}", api.posts);
        };
    assert!(plugins.handle_line(&format!("/fetch http://127.0.0.1:{port}/"), &mut api));
    wait_for(&mut plugins, &mut api, "fetched 200 None hi", 1);
    // localhost is not listed: refused, and the player is asked once.
    let other = format!("/fetch http://localhost:{port}/");
    assert!(plugins.handle_line(&other, &mut api));
    assert!(plugins.handle_line(&other, &mut api));
    let asks = api
        .posts
        .iter()
        .filter(|p| p.contains("/plugin allow openac.fetcher localhost"))
        .count();
    assert_eq!(asks, 3, "one ask and two refusals: {:?}", api.posts);
    assert!(plugins.handle_line("/plugin allow openac.fetcher localhost", &mut api));
    assert_eq!(
        api.posts.last().unwrap(),
        "Plugin openac.fetcher may reach localhost this session."
    );
    assert!(plugins.handle_line(&other, &mut api));
    wait_for(&mut plugins, &mut api, "fetched 200 None hi", 2);
    assert_eq!(
        api.posts
            .iter()
            .filter(|p| *p == "fetched 200 None hi")
            .count(),
        2
    );
    plugins.shutdown(&mut api);
}

/// OpenAC `IPluginChat.RegisterInputInterceptor` through a real plugin:
/// before it registers every line passes; then it drops, rewrites or passes
/// each line, and once it unloads its interceptor is gone.
#[test]
fn a_plugin_intercepts_typed_lines() {
    use ac_plugin_abi::callback::InterceptDecision;
    let root = Root::new("intercept");
    install(&root.0, "hello");
    let mut api = FakeSession::default();
    let mut plugins = session(&[&root.0], Some(&["openac.hello"]));
    plugins.start(&mut api);
    assert!(!plugins.intercepts_input());
    assert_eq!(
        plugins.intercept("a secret", &mut api),
        InterceptDecision::Pass
    );
    assert!(plugins.handle_line("/hello intercept", &mut api));
    assert_eq!(api.posts.last().unwrap(), "hello intercepts");
    assert!(plugins.intercepts_input());
    assert_eq!(
        plugins.intercept("a secret", &mut api),
        InterceptDecision::Suppress
    );
    assert_eq!(
        plugins.intercept("shout hi there", &mut api),
        InterceptDecision::Rewrite("HI THERE".into())
    );
    assert_eq!(
        plugins.intercept("hello", &mut api),
        InterceptDecision::Pass
    );
    plugins.shutdown(&mut api);
    assert!(!plugins.intercepts_input());
}

#[test]
fn hello_runs_its_verb_chat_tick_and_window_events() {
    let root = Root::new("hello");
    install(&root.0, "hello");
    let mut api = FakeSession {
        character: Some(CharacterInfo {
            id: 0x5000_0001,
            name: "Horan".into(),
            world: "Local".into(),
            ..CharacterInfo::default()
        }),
        ..FakeSession::default()
    };
    let mut plugins = session(&[&root.0], None);
    plugins.start(&mut api);
    assert_eq!(
        plugins.take_reports(),
        vec![PluginReport::Loaded {
            id: "openac.hello".into()
        }]
    );
    assert_eq!(
        api.messages("openac.hello"),
        vec![
            "hello openac.hello enabled on API 1.6 (client 0.1.0), window attached: false",
            "plugin loaded: openac.hello (Hello)",
        ]
    );

    // A registered verb: the plugin answers at once.
    assert!(plugins.claims_line("/HELLO"));
    assert!(plugins.handle_line("/hello", &mut api));
    assert_eq!(api.posts, vec!["Hello, Horan! (no window is attached)"]);

    // A submit is queued for the session to route after the call.
    assert!(plugins.handle_line("/hello say ping hello everyone", &mut api));
    assert_eq!(plugins.take_submits(), vec!["ping hello everyone"]);

    // 150 ms of session time is ten ticks, and the session counts the
    // rounds it raised.
    assert_eq!(plugins.ticks_raised(), 0);
    plugins.pump(0.150, Some(1), &mut api);
    assert!(plugins.handle_line("/hello ticks", &mut api));
    assert_eq!(api.posts.last().unwrap(), "hello: 10 ticks");
    assert_eq!(plugins.ticks_raised(), 10);

    // Chat comes in as events; the plugin's own posted answer has no
    // speaker, so it does not answer itself.
    plugins.queue_chat(ChatLine {
        sender: "Horan".into(),
        text: "ping hello everyone".into(),
        display_text:
            "<Tell:IIDString:1342177281:Horan>Horan<\\Tell> says, \"ping hello everyone\"".into(),
        ..ChatLine::default()
    });
    plugins.set_window_attached(true);
    api.window = true;
    plugins.pump(0.0, Some(1), &mut api);
    assert_eq!(
        api.posts.last().unwrap(),
        "hello heard #1: <Tell:IIDString:1342177281:Horan>Horan<\\Tell> says, \"ping hello everyone\""
    );
    assert_eq!(
        api.messages("openac.hello").last().unwrap(),
        "hello: window attached: true"
    );
    assert!(plugins.handle_line("/hello", &mut api));
    assert_eq!(
        api.posts.last().unwrap(),
        "Hello, Horan! (a window is attached)"
    );

    plugins.shutdown(&mut api);
    assert_eq!(
        api.messages("openac.hello").last().unwrap(),
        "hello disabled after 10 ticks"
    );
    assert!(plugins.loaded_ids().is_empty());
    assert!(!plugins.claims_line("/hello"));
}

#[test]
fn a_plugin_that_imports_chat_without_the_permission_is_refused_at_load() {
    let root = Root::new("noperm");
    let folder = install(&root.0, "hello");
    edit_manifest(&folder, r#""permissionsVersion": 1,"#, "");
    let text = std::fs::read_to_string(folder.join("plugin.json")).unwrap();
    let start = text.find(r#""permissions""#).unwrap();
    // The list's own closing bracket (a host list inside it has one too).
    let end = text[start..].find("\n  ]").unwrap() + start + 4;
    std::fs::write(
        folder.join("plugin.json"),
        format!("{}\"x\": 0{}", &text[..start], &text[end..]),
    )
    .unwrap();

    let mut api = FakeSession::default();
    let mut plugins = session(&[&root.0], Some(&["openac.hello"]));
    plugins.start(&mut api);
    let error = failed(&plugins.take_reports(), "openac.hello").unwrap();
    assert!(
        error.contains("imports openac.chat_")
            && error.contains("needs the 'chat' permission plugin.json does not declare"),
        "{error}"
    );
    assert!(plugins.loaded_ids().is_empty());
}

#[test]
fn a_plugin_without_permissions_links_only_basic_imports() {
    let root = Root::new("probe-imports");
    let folder = install(&root.0, "probe");
    let manifest = ac_plugin::PluginManifest::parse(
        &std::fs::read_to_string(folder.join("plugin.json")).unwrap(),
    )
    .unwrap();
    let module = engine().module(&folder.join("probe.wasm")).unwrap();
    let checked = ac_plugin::engine::check_module(&module, &manifest).unwrap();
    assert!(checked.imports.contains(&Import::HostLog));
    assert!(checked.imports.contains(&Import::CharacterInfo));
    // The SDK's chat calls are never linked into a plugin that does not
    // make them.
    assert!(!checked.imports.contains(&Import::ChatSubmit));
    assert!(!checked.imports.contains(&Import::ChatPost));
}

#[test]
fn the_allow_list_picks_and_orders_and_shutdown_runs_in_reverse() {
    let root = Root::new("order");
    install(&root.0, "hello");
    install(&root.0, "probe");

    // Absent: everything, in discovery order (folder names).
    let mut api = FakeSession::default();
    let mut all = session(&[&root.0], None);
    all.start(&mut api);
    assert_eq!(all.loaded_ids(), vec!["openac.hello", "openac.probe"]);
    all.shutdown(&mut api);

    // Empty: nothing at all.
    let mut none = session(&[&root.0], Some(&[]));
    none.start(&mut api);
    assert!(none.loaded_ids().is_empty());
    assert!(none.take_reports().is_empty());

    // Listed: exactly those, in list order (ids match ignoring case).
    let mut api = FakeSession::default();
    let mut listed = session(&[&root.0], Some(&["OPENAC.PROBE", "openac.hello"]));
    listed.start(&mut api);
    assert_eq!(listed.loaded_ids(), vec!["openac.probe", "openac.hello"]);
    listed.shutdown(&mut api);
    let stops: Vec<&str> = api
        .logs
        .iter()
        .map(|(_, _, m)| m.as_str())
        .filter(|m| m.starts_with("hello disabled") || m.starts_with("probe stop"))
        .collect();
    assert_eq!(
        stops,
        vec!["hello disabled after 0 ticks", "probe stop: ticks=0"]
    );
}

#[test]
fn incompatible_plugins_fail_when_listed_and_are_skipped_when_not() {
    let root = Root::new("compat");
    let folder = install(&root.0, "hello");
    edit_manifest(
        &folder,
        r#""minHostVersion": "0.1.0""#,
        r#""minHostVersion": "9.0.0""#,
    );

    let mut api = FakeSession::default();
    let mut unlisted = session(&[&root.0], None);
    unlisted.start(&mut api);
    assert!(unlisted.take_reports().is_empty());

    let mut listed = session(&[&root.0], Some(&["openac.hello"]));
    listed.start(&mut api);
    assert_eq!(
        failed(&listed.take_reports(), "openac.hello").unwrap(),
        "plugin 'openac.hello' requires openAC 9.0.0 or newer (this is 0.1.0)."
    );
}

#[test]
fn a_duplicate_id_across_roots_loads_neither_copy() {
    let first = Root::new("dup-a");
    let second = Root::new("dup-b");
    install(&first.0, "hello");
    install(&second.0, "hello");
    let mut api = FakeSession::default();
    let mut plugins = session(&[&first.0, &second.0], None);
    plugins.start(&mut api);
    let error = failed(&plugins.take_reports(), "openac.hello").unwrap();
    assert!(
        error.starts_with("plugin 'openac.hello' is declared in more than one plugin folder: "),
        "{error}"
    );
    assert!(plugins.loaded_ids().is_empty());
}

#[test]
fn a_listed_plugin_that_is_missing_or_unreadable_is_reported() {
    let root = Root::new("missing");
    let csharp = root.0.join("acdream.old");
    std::fs::create_dir_all(&csharp).unwrap();
    std::fs::write(
        csharp.join("plugin.json"),
        r#"{ "id": "acdream.old", "displayName": "Old", "version": "1.0.0", "entryDll": "Old.dll", "apiVersion": 1 }"#,
    )
    .unwrap();
    let mut api = FakeSession::default();
    let mut plugins = session(&[&root.0], Some(&["openac.nowhere", "acdream.old"]));
    plugins.start(&mut api);
    let reports = plugins.take_reports();
    assert_eq!(
        failed(&reports, "openac.nowhere").unwrap(),
        "plugin 'openac.nowhere' was not found in the configured plugin roots."
    );
    // An unreadable manifest is reported under its folder's name.
    assert!(
        failed(&reports, "acdream.old")
            .unwrap()
            .starts_with("built for OpenAC (C#)")
    );
}

#[test]
fn an_api_version_this_client_does_not_support_is_refused() {
    let root = Root::new("api");
    let folder = install(&root.0, "probe");
    edit_manifest(&folder, r#""apiVersion": "1.6""#, r#""apiVersion": "1.7""#);
    let mut api = FakeSession::default();
    let mut plugins = session(&[&root.0], None);
    plugins.start(&mut api);
    assert_eq!(
        failed(&plugins.take_reports(), "openac.probe").unwrap(),
        "plugin 'openac.probe' declares apiVersion 1.7, but this build supports 1.0..1.6"
    );
}

/// Hands `hello` a chat line, a status notice and another line, in that
/// order; what it posted.
fn hello_hears_status_between_lines(root: &Path) -> (Vec<String>, Vec<String>) {
    let speech = |text: &str| ChatLine {
        sender: "Horan".into(),
        text: text.into(),
        display_text: text.into(),
        ..ChatLine::default()
    };
    let mut api = FakeSession::default();
    let mut plugins = session(&[root], Some(&["openac.hello"]));
    plugins.start(&mut api);
    plugins.queue_chat(speech("ping hello one"));
    plugins.queue_event(Event::StatusTextReceived(
        "The Corpse of War Armoredillo is already in use by Unkle Leo!".into(),
    ));
    plugins.queue_chat(speech("ping hello two"));
    plugins.pump(0.0, None, &mut api);
    let loaded = plugins.loaded_ids();
    plugins.shutdown(&mut api);
    (api.posts, loaded)
}

/// A status notice reaches a plugin built for API 1.1, in order with the
/// chat lines around it; the same plugin as an API 1.0 SDK built it (its
/// version section and core layout 1.0's) is never sent it, since that
/// SDK would stop the plugin on an event kind it cannot read, and still
/// hears the lines.
#[test]
fn a_status_notice_reaches_only_plugins_built_for_api_1_1() {
    let root = Root::new("status-1-1");
    install(&root.0, "hello");
    let (posts, loaded) = hello_hears_status_between_lines(&root.0);
    assert_eq!(loaded, ["openac.hello"]);
    assert_eq!(
        posts,
        [
            "hello heard #1: ping hello one",
            "hello status: The Corpse of War Armoredillo is already in use by Unkle Leo!",
            "hello heard #2: ping hello two",
        ]
    );

    let root = Root::new("status-1-0");
    let folder = install(&root.0, "hello");
    edit_manifest(&folder, r#""apiVersion": "1.6""#, r#""apiVersion": "1.0""#);
    let wasm = folder.join("hello.wasm");
    let layout = section_at(ApiVersion::new(1, 0));
    let built = std::fs::read(&wasm).unwrap();
    let built = with_custom_section(&built, "openac.api", Some(b"1.0"));
    let built = with_custom_section(&built, LAYOUT_SECTION, Some(&layout));
    std::fs::write(&wasm, built).unwrap();
    let (posts, loaded) = hello_hears_status_between_lines(&root.0);
    assert_eq!(loaded, ["openac.hello"]);
    assert_eq!(
        posts,
        [
            "hello heard #1: ping hello one",
            "hello heard #2: ping hello two",
        ]
    );
}

/// A minimal module importing `openac.<name>` with the convention's shape,
/// carrying the API section, exporting nothing.
fn module_importing(name: &str) -> Vec<u8> {
    fn section(id: u8, body: &[u8]) -> Vec<u8> {
        let mut out = vec![id, u8::try_from(body.len()).unwrap()];
        out.extend_from_slice(body);
        out
    }
    fn string(text: &str) -> Vec<u8> {
        let mut out = vec![u8::try_from(text.len()).unwrap()];
        out.extend_from_slice(text.as_bytes());
        out
    }
    let mut wasm = b"\0asm\x01\0\0\0".to_vec();
    // One type: (i32, i32) -> i32.
    wasm.extend(section(1, &[1, 0x60, 2, 0x7F, 0x7F, 1, 0x7F]));
    let mut import = vec![1];
    import.extend(string("openac"));
    import.extend(string(name));
    import.extend([0x00, 0x00]);
    wasm.extend(section(2, &import));
    let mut custom = string("openac.api");
    custom.extend_from_slice(b"1.0");
    wasm.extend(section(0, &custom));
    // A layout with no imports: the one import here is not the client's.
    // The core shape is API 1.0's, the version it says it was built for.
    let mut layout = string(LAYOUT_SECTION);
    layout.extend_from_slice(&core_shape(ApiVersion::new(1, 0)).to_le_bytes());
    wasm.extend(section(0, &layout));
    wasm
}

/// The client functions the test plugin `name` imports.
fn imported(name: &str) -> Vec<Import> {
    let path = ac_plugin::fixtures::wasm_directory().join(format!("{name}.wasm"));
    PluginEngine::new()
        .module(&path)
        .unwrap()
        .imports()
        .filter_map(|import| Import::from_name(import.name()))
        .collect()
}

/// This client's layout section, changed by `edit` (given the section and
/// the offset of each import's `(key, shape)` entry).
fn edited_layout(edit: impl FnOnce(&mut Vec<u8>, &dyn Fn(Import) -> usize)) -> Vec<u8> {
    let mut bytes = LAYOUT_SECTION_BYTES.to_vec();
    let entry = |import: Import| {
        let key = import_key(import.name()).to_le_bytes();
        (8..LAYOUT_SECTION_BYTES.len())
            .step_by(16)
            .find(|&at| LAYOUT_SECTION_BYTES[at..at + 8] == key)
            .unwrap()
    };
    edit(&mut bytes, &entry);
    bytes
}

/// The test plugin `name` installed into `root` with its layout section
/// replaced by `layout`, or without one.
fn install_with_layout(root: &Path, name: &str, layout: Option<&[u8]>) {
    let wasm = install(root, name).join(format!("{name}.wasm"));
    let built = std::fs::read(&wasm).unwrap();
    std::fs::write(&wasm, with_custom_section(&built, LAYOUT_SECTION, layout)).unwrap();
}

/// Loads `hello` with `layout` beside an untouched `probe`; hello's failure,
/// if any.
fn load_hello_with_layout(tag: &str, layout: Option<&[u8]>) -> (Option<String>, Vec<String>) {
    let root = Root::new(tag);
    install_with_layout(&root.0, "hello", layout);
    install(&root.0, "probe");
    let mut api = FakeSession::default();
    let mut plugins = session(&[&root.0], None);
    plugins.start(&mut api);
    let error = failed(&plugins.take_reports(), "openac.hello");
    (error, plugins.loaded_ids())
}

#[test]
fn every_test_plugin_is_built_with_this_clients_layout() {
    let engine = PluginEngine::new();
    for name in ["hello", "probe", "misbehave", "uidemo", "fetcher"] {
        let path = ac_plugin::fixtures::wasm_directory().join(format!("{name}.wasm"));
        let module = engine.module(&path).unwrap();
        assert_eq!(check_layout(&module, API_CURRENT), Ok(()), "{name}");
    }
}

/// `probe` as an SDK for API `version` built it, with `layout` as its
/// layout section; whether it loaded, and its failure if not.
fn load_probe_built_for(tag: &str, version: &str, layout: &[u8]) -> (Vec<String>, Option<String>) {
    let root = Root::new(tag);
    let folder = install(&root.0, "probe");
    edit_manifest(
        &folder,
        r#""apiVersion": "1.6""#,
        &format!(r#""apiVersion": "{version}""#),
    );
    let wasm = folder.join("probe.wasm");
    let built = std::fs::read(&wasm).unwrap();
    let built = with_custom_section(&built, "openac.api", Some(version.as_bytes()));
    let built = with_custom_section(&built, LAYOUT_SECTION, Some(layout));
    std::fs::write(&wasm, built).unwrap();
    let mut api = FakeSession::default();
    let mut plugins = session(&[&root.0], Some(&["openac.probe"]));
    plugins.start(&mut api);
    let error = failed(&plugins.take_reports(), "openac.probe");
    let loaded = plugins.loaded_ids();
    plugins.shutdown(&mut api);
    (loaded, error)
}

/// API 1.2 gave the inventory and world-object records a field (a house
/// hook's item types). A plugin an API 1.1 SDK built, which reads those
/// records (`probe` imports `items_owned` and `world_objects`), still
/// loads: its layout is compared with the shapes API 1.1 had, and the
/// session sends it the records as they were then. The same plugin
/// claiming 1.1 with this SDK's shapes is refused.
#[test]
fn a_plugin_built_for_api_1_1_loads_with_the_records_it_knew() {
    let used = imported("probe");
    assert!(used.contains(&Import::ItemsOwned) && used.contains(&Import::WorldObjects));
    let v1_1 = ApiVersion::new(1, 1);
    let (loaded, error) = load_probe_built_for("probe-1-1", "1.1", &section_at(v1_1));
    assert_eq!((loaded, error), (vec!["openac.probe".to_owned()], None));

    let (loaded, error) = load_probe_built_for("probe-1-1-new", "1.1", &LAYOUT_SECTION_BYTES);
    assert!(loaded.is_empty());
    assert!(error.unwrap().contains(LAYOUT_REFUSAL));
}

/// API 1.3 added the world-triangle and marker imports and nothing else: `probe` as
/// an API 1.2 SDK built it still loads.
#[test]
fn a_plugin_built_for_api_1_2_still_loads() {
    let v1_2 = ApiVersion::new(1, 2);
    let (loaded, error) = load_probe_built_for("probe-1-2", "1.2", &section_at(v1_2));
    assert_eq!((loaded, error), (vec!["openac.probe".to_owned()], None));
}

/// API 1.4 gave the theme's kind more variants: `probe` as an API 1.3 SDK
/// built it still loads.
#[test]
fn a_plugin_built_for_api_1_3_still_loads() {
    let v1_3 = ApiVersion::new(1, 3);
    let (loaded, error) = load_probe_built_for("probe-1-3", "1.3", &section_at(v1_3));
    assert_eq!((loaded, error), (vec!["openac.probe".to_owned()], None));
}

/// API 1.5 gave a path preview's plan two fields at its end: `probe` as an
/// API 1.4 SDK built it still loads.
#[test]
fn a_plugin_built_for_api_1_4_still_loads() {
    let v1_4 = ApiVersion::new(1, 4);
    let (loaded, error) = load_probe_built_for("probe-1-4", "1.4", &section_at(v1_4));
    assert_eq!((loaded, error), (vec!["openac.probe".to_owned()], None));
}

/// API 1.6 added only pointer kinds (hover, leave), no shape: `probe` as an
/// API 1.5 SDK built it still loads.
#[test]
fn a_plugin_built_for_api_1_5_still_loads() {
    let v1_5 = ApiVersion::new(1, 5);
    let (loaded, error) = load_probe_built_for("probe-1-5", "1.5", &section_at(v1_5));
    assert_eq!((loaded, error), (vec!["openac.probe".to_owned()], None));
}

/// A path preview's plan reaches a plugin exactly as its API version
/// encodes it, through a real module: `rawbytes` built for API 1.4 is
/// handed the plan without the grid it reused or the worker's time (byte
/// for byte the 1.4 encoding); built for this API it gets both.
#[test]
fn a_navigation_plan_reaches_each_plugin_in_its_api_versions_encoding() {
    use ac_plugin::abi::navigation::{NavigationPlan, NavigationPlanStatus};
    let event = Event::NavigationPlan(NavigationPlan {
        handle: 7,
        status: NavigationPlanStatus::Routed,
        reason: "routed".into(),
        length_meters: 12.5,
        grid_reused: true,
        took_ms: 3.25,
        ..NavigationPlan::default()
    });
    for version in [ApiVersion::new(1, 4), API_CURRENT] {
        let root = Root::new(&format!("raw-plan-{version}"));
        install_built_for(&root.0, "rawbytes", version);
        let mut api = FakeSession::default();
        let mut plugins = session(&[&root.0], Some(&["openac.rawbytes"]));
        plugins.start(&mut api);
        assert_eq!(plugins.loaded_ids(), ["openac.rawbytes"], "{version}");
        plugins.queue_event(event.clone());
        plugins.pump(0.0, None, &mut api);
        plugins.shutdown(&mut api);
        let handed = raw_handed(&api, "event");
        assert_eq!(handed, [event.to_bytes_at(version)], "{version}");
        let Ok(Event::NavigationPlan(read)) = Event::from_bytes_at(&handed[0], version) else {
            panic!("{version}: a plan");
        };
        assert_eq!(read.length_meters, 12.5, "{version}");
        if version == API_CURRENT {
            assert_eq!(handed[0], event.to_bytes());
            assert!(read.grid_reused);
            assert_eq!(read.took_ms, 3.25);
        } else {
            assert_eq!(handed[0].len() + 5, event.to_bytes().len(), "{version}");
            assert!(!read.grid_reused);
            assert_eq!(read.took_ms, 0.0);
        }
    }
}

/// A reach search's answer reaches a plugin built for API 1.5, through a
/// real module, byte for byte as this API encodes it; the same plugin as an
/// API 1.4 SDK built it is never sent it, since that SDK would stop the
/// plugin on an event kind it cannot read.
#[test]
fn a_reach_answer_reaches_only_plugins_built_for_api_1_5() {
    use ac_plugin::abi::navigation::{CellReach, NavigationPlanStatus, NavigationReach};
    let event = Event::NavigationReach(NavigationReach {
        handle: 3,
        status: NavigationPlanStatus::Routed,
        cells: vec![CellReach {
            cell_id: 0x0032_0100,
            route_meters: 4.5,
            by_drop: true,
            ..CellReach::default()
        }],
        reason: "1 cells reached".into(),
        grid_reused: true,
        took_ms: 9.0,
    });
    for version in [ApiVersion::new(1, 4), API_CURRENT] {
        let root = Root::new(&format!("raw-reach-{version}"));
        install_built_for(&root.0, "rawbytes", version);
        let mut api = FakeSession::default();
        let mut plugins = session(&[&root.0], Some(&["openac.rawbytes"]));
        plugins.start(&mut api);
        assert_eq!(plugins.loaded_ids(), ["openac.rawbytes"], "{version}");
        plugins.queue_event(event.clone());
        plugins.pump(0.0, None, &mut api);
        plugins.shutdown(&mut api);
        let handed = raw_handed(&api, "event");
        if version == API_CURRENT {
            assert_eq!(handed, [event.to_bytes()]);
        } else {
            assert!(handed.is_empty(), "{version}: {handed:?}");
        }
    }
}

/// Installs the test plugin `name` into `root` as an SDK for API `version`
/// built it: its manifest, version section and layout section that
/// version's ([`section_at`]). This SDK's own version installs it as built.
fn install_built_for(root: &Path, name: &str, version: ApiVersion) -> PathBuf {
    let folder = install(root, name);
    if version == API_CURRENT {
        return folder;
    }
    edit_manifest(
        &folder,
        &format!(r#""apiVersion": "{API_CURRENT}""#),
        &format!(r#""apiVersion": "{version}""#),
    );
    let wasm = folder.join(format!("{name}.wasm"));
    let built = std::fs::read(&wasm).unwrap();
    let built = with_custom_section(&built, "openac.api", Some(version.to_string().as_bytes()));
    let built = with_custom_section(&built, LAYOUT_SECTION, Some(&section_at(version)));
    std::fs::write(&wasm, built).unwrap();
    folder
}

/// The bytes `rawbytes` logged for its `kind` (`event` or `callback`)
/// handings, in order, exactly as the client handed them to it.
fn raw_handed(api: &FakeSession, kind: &str) -> Vec<Vec<u8>> {
    let prefix = format!("{kind} ");
    api.logs
        .iter()
        .filter(|(id, _, _)| id == "openac.rawbytes")
        .filter_map(|(_, _, line)| line.strip_prefix(&prefix))
        .map(|hex| {
            (0..hex.len())
                .step_by(2)
                .map(|at| u8::from_str_radix(&hex[at..at + 2], 16).unwrap())
                .collect()
        })
        .collect()
}

/// An object change, its world object a house hook with item-type bits
/// (a field API 1.2 added).
fn hook_object_changed() -> Event {
    use ac_plugin::abi::world::{ObjectChange, WorldObject};
    Event::ObjectChanged(ObjectChange {
        object_id: 0x7000_0001,
        revision: 3,
        current: Some(WorldObject {
            object_id: 0x7000_0001,
            name: "Wall Hook".into(),
            hook_item_types: 0x0000_0080,
            ..WorldObject::default()
        }),
        ..ObjectChange::default()
    })
}

/// An object change reaches a plugin exactly as its API version encodes
/// it: a plugin built for API 1.1 gets the world object without the house
/// hook's item types (byte for byte the 1.1 encoding), one built for 1.2
/// gets the field.
#[test]
fn an_object_change_reaches_each_plugin_in_its_api_versions_encoding() {
    let event = hook_object_changed();
    for version in [ApiVersion::new(1, 1), API_CURRENT] {
        let root = Root::new(&format!("raw-event-{version}"));
        install_built_for(&root.0, "rawbytes", version);
        let mut api = FakeSession::default();
        let mut plugins = session(&[&root.0], Some(&["openac.rawbytes"]));
        plugins.start(&mut api);
        assert_eq!(plugins.loaded_ids(), ["openac.rawbytes"], "{version}");
        plugins.queue_event(event.clone());
        plugins.pump(0.0, None, &mut api);
        plugins.shutdown(&mut api);
        let handed = raw_handed(&api, "event");
        assert_eq!(handed, [event.to_bytes_at(version)], "{version}");
        if version == API_CURRENT {
            assert_eq!(handed[0], event.to_bytes());
        } else {
            assert_ne!(handed[0], event.to_bytes(), "{version}");
            let Ok(Event::ObjectChanged(read)) = Event::from_bytes_at(&handed[0], version) else {
                panic!("{version}: an object change");
            };
            assert_eq!(read.current.map(|o| o.hook_item_types), Some(0));
        }
    }
}

/// A theme change reaches each plugin as its API version knows it, through
/// a real module: under Ember, `rawbytes` built for API 1.2 or 1.3 is handed
/// a kind it knows with Ember's name and colours, and reads the same from the
/// `ui_theme` import; built for this API it hears Ember itself.
#[test]
fn a_new_theme_reaches_each_plugin_as_its_api_version_knows_it() {
    use ac_plugin_ui::{ThemeKind, UiInput};
    for version in [ApiVersion::new(1, 2), ApiVersion::new(1, 3), API_CURRENT] {
        let root = Root::new(&format!("raw-theme-{version}"));
        install_built_for(&root.0, "rawbytes", version);
        let mut api = FakeSession::default();
        let mut plugins = session(&[&root.0], Some(&["openac.rawbytes"]));
        plugins.start(&mut api);
        assert_eq!(plugins.loaded_ids(), ["openac.rawbytes"], "{version}");
        api.ui.input(UiInput::Theme(ThemeKind::Ember));
        plugins.pump(0.0, None, &mut api);
        plugins.shutdown(&mut api);
        let info = ac_plugin::abi::ui_host::theme_info(ThemeKind::Ember);
        let event = Event::ThemeChanged(info.clone());
        let handed = raw_handed(&api, "event");
        assert_eq!(handed, [event.to_bytes_at(version)], "{version}");
        let Ok(Event::ThemeChanged(read)) = Event::from_bytes_at(&handed[0], version) else {
            panic!("{version}: a theme change");
        };
        let kind = if version == API_CURRENT {
            ac_plugin::abi::ui::ThemeKind::Ember
        } else {
            ac_plugin::abi::ui::ThemeKind::Moss
        };
        assert_eq!(read.kind, kind, "{version}");
        assert_eq!(read.display_name, "Ember", "{version}");
        assert_eq!(read.palette, info.palette, "{version}");
        assert!(
            api.logs.iter().any(|(id, _, line)| id == "openac.rawbytes"
                && *line == format!("ui_theme {kind:?} Ember")),
            "{version}: {:?}",
            api.logs
        );
    }
}

/// A plugin built for API 1.2 or 1.3 paints text without a font field, through a
/// real module, and the client reads it in the interface font; one built for
/// this API sends the field.
#[test]
fn an_older_plugins_canvas_text_reads_without_a_font() {
    use ac_plugin_ui::PaintCmd;
    for version in [ApiVersion::new(1, 2), ApiVersion::new(1, 3), API_CURRENT] {
        let root = Root::new(&format!("raw-paint-{version}"));
        install_built_for(&root.0, "rawbytes", version);
        let mut api = FakeSession::default();
        let mut plugins = session(&[&root.0], Some(&["openac.rawbytes"]));
        plugins.start(&mut api);
        assert!(plugins.handle_line(&format!("/rawpaint {version}"), &mut api));
        plugins.pump(0.0, None, &mut api);
        assert_eq!(
            plugins.loaded_ids(),
            ["openac.rawbytes"],
            "{version}: {:?}",
            api.logs
        );
        let canvas = api.ui.canvases().next().unwrap().clone();
        let [PaintCmd::Text { text, font, .. }] = canvas.paint.as_slice() else {
            panic!("{version}: {:?}", canvas.paint);
        };
        assert_eq!((text.as_str(), *font), ("raw", 0), "{version}");
        plugins.shutdown(&mut api);
    }
}

/// A plugin built for API 1.2 or 1.3 registers windows without a dock icon of
/// their own, through a real module, and the client reads the rest as sent; one
/// built for this API sends the path.
#[test]
fn an_older_plugins_window_reads_without_an_icon_file() {
    for version in [ApiVersion::new(1, 2), ApiVersion::new(1, 3), API_CURRENT] {
        let root = Root::new(&format!("raw-panel-{version}"));
        install_built_for(&root.0, "rawbytes", version);
        let mut api = FakeSession::default();
        let mut plugins = session(&[&root.0], Some(&["openac.rawbytes"]));
        plugins.start(&mut api);
        assert!(plugins.handle_line(&format!("/rawpanel {version}"), &mut api));
        plugins.pump(0.0, None, &mut api);
        assert_eq!(
            plugins.loaded_ids(),
            ["openac.rawbytes"],
            "{version}: {:?}",
            api.logs
        );
        let files: Vec<(String, Option<String>)> = api
            .ui
            .panels()
            .map(|p| (p.descriptor.title.clone(), p.descriptor.icon_file.clone()))
            .collect();
        let file = |path: &str| (version == API_CURRENT).then(|| path.to_owned());
        assert_eq!(
            files,
            [
                ("Raw".to_owned(), file("raw.png")),
                ("Raw Typed".to_owned(), file("rawtyped.png")),
            ],
            "{version}"
        );
        plugins.shutdown(&mut api);
    }
}

/// `uidemo` holds fonts through a real module: the bundled font in both
/// weights, a CFF OpenType file of its own, a file that is no font refused
/// without stopping it; a font held twice takes two releases; its text names
/// them; they go when it unloads.
#[test]
fn a_plugin_holds_fonts_through_a_real_module() {
    use ac_plugin_ui::PaintCmd;
    let root = Root::new("fonts");
    install(&root.0, "uidemo");
    let mut api = FakeSession::default();
    let mut plugins = session(&[&root.0], Some(&["openac.uidemo"]));
    plugins.start(&mut api);
    assert_eq!(plugins.loaded_ids(), ["openac.uidemo"]);
    let line = api
        .logs
        .iter()
        .map(|(_, _, line)| line.as_str())
        .find(|line| line.starts_with("uidemo fonts:"))
        .expect("it reported its fonts");
    assert!(
        line.starts_with(
            "uidemo fonts: heading=true body=true own=true bad=false released=true count=3 own_size="
        ),
        "{line}"
    );
    assert_eq!(api.ui.font_count("openac.uidemo"), 3);
    plugins.pump(0.0, None, &mut api);
    assert!(
        api.logs
            .iter()
            .any(|(_, _, line)| line.contains("font 'fonts/bad.ttf'")),
        "the bad file is reported: {:?}",
        api.logs
    );
    let canvas = api.ui.canvases().next().unwrap().clone();
    let fonts: Vec<u32> = canvas
        .paint
        .iter()
        .filter_map(|c| match c {
            PaintCmd::Text { font, .. } if *font != 0 => Some(*font),
            _ => None,
        })
        .collect();
    assert_eq!(fonts.len(), 3, "{:?}", canvas.paint);
    plugins.shutdown(&mut api);
    assert_eq!(api.ui.font_count("openac.uidemo"), 0);
}

/// A loot classifier question reaches the classifier's plugin exactly as
/// its API version encodes it: `hello` asks `rawbytes`'s classifier, and a
/// `rawbytes` built for API 1.1 is handed the item record without the house
/// hook's item types, one built for 1.2 with it.
#[test]
fn a_loot_question_reaches_each_classifier_in_its_api_versions_encoding() {
    use ac_plugin::abi::callback::Callback;
    for version in [ApiVersion::new(1, 1), API_CURRENT] {
        let root = Root::new(&format!("raw-callback-{version}"));
        install(&root.0, "hello");
        install_built_for(&root.0, "rawbytes", version);
        let mut api = FakeSession::default();
        let mut plugins = session(&[&root.0], Some(&["openac.hello", "openac.rawbytes"]));
        plugins.start(&mut api);
        assert_eq!(plugins.loaded_ids(), ["openac.hello", "openac.rawbytes"]);
        assert!(plugins.handle_line("/hello classify openac.rawbytes/rules", &mut api));
        plugins.shutdown(&mut api);
        let handed = raw_handed(&api, "callback");
        assert_eq!(handed.len(), 1, "{version}");
        let Ok(Callback::LootClassify(question)) = Callback::from_bytes_at(&handed[0], version)
        else {
            panic!("{version}: a loot question in that version's encoding");
        };
        let asked = Callback::LootClassify(question);
        assert_eq!(handed[0], asked.to_bytes_at(version), "{version}");
        assert_eq!(
            handed[0] == asked.to_bytes(),
            version == API_CURRENT,
            "{version}"
        );
    }
}

#[test]
fn a_plugin_built_with_another_shape_of_an_import_it_uses_is_refused_at_load() {
    // The MossTank fault: `loot_corpses`'s reply gained a field under the
    // same API version, and a plugin built before it decoded it wrongly.
    let used = imported("hello")[0];
    let layout = edited_layout(|bytes, entry| bytes[entry(used) + 8] ^= 1);
    let (error, loaded) = load_hello_with_layout("layout-import", Some(&layout));
    assert_eq!(
        error.unwrap(),
        format!(
            "the .wasm was built against a different plugin API layout (the request or reply \
             of openac.{} differs from this client's); rebuild it against this client's SDK",
            used.name()
        )
    );
    // Nothing of it ran; the other plugin is untouched.
    assert_eq!(loaded, vec!["openac.probe"]);
}

#[test]
fn a_plugin_built_with_other_events_is_refused_at_load() {
    let layout = edited_layout(|bytes, _| bytes[0] ^= 1);
    let (error, _) = load_hello_with_layout("layout-core", Some(&layout));
    assert_eq!(
        error.unwrap(),
        "the .wasm was built against a different plugin API layout (its start record, events or \
         callbacks differ from this client's); rebuild it against this client's SDK"
    );
}

#[test]
fn a_plugin_is_not_refused_for_an_import_it_does_not_use() {
    // Built before an import was added (its SDK's table lacks it), or with
    // another shape of one it never calls: it decodes nothing of it.
    let used = imported("hello");
    let unused: Vec<Import> = Import::ALL
        .iter()
        .copied()
        .filter(|import| !used.contains(import))
        .take(2)
        .collect();
    let layout = edited_layout(|bytes, entry| {
        bytes[entry(unused[0]) + 8] ^= 1;
        let missing = entry(unused[1]);
        bytes.drain(missing..missing + 16);
    });
    let (error, loaded) = load_hello_with_layout("layout-unused", Some(&layout));
    assert_eq!(error, None);
    assert_eq!(loaded, vec!["openac.hello", "openac.probe"]);
}

#[test]
fn a_plugin_without_a_layout_fingerprint_is_refused_at_load() {
    // Built by an SDK from before the fingerprint: refused while API 1.0 is
    // unfrozen, since its records may differ from this client's.
    let (error, loaded) = load_hello_with_layout("layout-none", None);
    assert_eq!(
        error.unwrap(),
        "the .wasm was built against a different plugin API layout (it has no openac.layout \
         section: an SDK from before layout fingerprints); rebuild it against this client's SDK"
    );
    assert_eq!(loaded, vec!["openac.probe"]);
}

#[test]
fn a_malformed_layout_fingerprint_is_refused_at_load() {
    let (error, _) = load_hello_with_layout("layout-bad", Some(b"1.0"));
    assert_eq!(
        error.unwrap(),
        "the .wasm was built against a different plugin API layout (its openac.layout section \
         is malformed); rebuild it against this client's SDK"
    );
}

#[test]
fn an_import_this_client_does_not_have_is_named_at_load() {
    let root = Root::new("newer");
    let folder = root.0.join("future");
    std::fs::create_dir_all(&folder).unwrap();
    std::fs::write(
        folder.join("future.wasm"),
        module_importing("teleport_anywhere"),
    )
    .unwrap();
    std::fs::write(
        folder.join("plugin.json"),
        r#"{ "id": "openac.future", "displayName": "Future", "version": "1.0.0", "entry": "future.wasm", "apiVersion": "1.0" }"#,
    )
    .unwrap();
    let mut api = FakeSession::default();
    let mut plugins = session(&[&root.0], None);
    plugins.start(&mut api);
    assert_eq!(
        failed(&plugins.take_reports(), "openac.future").unwrap(),
        "imports openac.teleport_anywhere, which API 1.6 of this client does not have (the plugin was \
         built for API 1.0)"
    );
}

fn misbehave_session(tag: &str, settings: &[(&str, &str)]) -> (Root, PluginSession, FakeSession) {
    let root = Root::new(tag);
    install(&root.0, "misbehave");
    install(&root.0, "hello");
    let mut declared = std::collections::BTreeMap::new();
    declared.insert(
        "openac.misbehave".to_owned(),
        settings
            .iter()
            .map(|(k, v)| ((*k).to_owned(), (*v).to_owned()))
            .collect(),
    );
    let mut options = options(&[&root.0], Some(&["openac.misbehave", "openac.hello"]));
    options.settings = PluginSessionSettings::from_map(&declared);
    let mut plugins = PluginSession::new(short_engine(), options);
    let mut api = FakeSession::default();
    plugins.start(&mut api);
    (root, plugins, api)
}

fn crash(order: &str) -> String {
    let (_root, mut plugins, mut api) = misbehave_session(&format!("crash-{order}"), &[]);
    plugins.take_reports();
    assert!(plugins.handle_line(&format!("/crash {order}"), &mut api));
    let error = failed(&plugins.take_reports(), "openac.misbehave").unwrap();
    // Unloaded, its verb released, the other plugin untouched and ticking.
    assert_eq!(plugins.loaded_ids(), vec!["openac.hello"]);
    assert!(!plugins.claims_line("/crash"));
    plugins.pump(0.015, None, &mut api);
    assert!(plugins.handle_line("/hello ticks", &mut api));
    assert_eq!(api.posts.last().unwrap(), "hello: 1 ticks");
    error
}

#[test]
fn a_panic_unloads_only_that_plugin_and_reports_the_message() {
    let error = crash("panic");
    assert!(
        error.starts_with("plugin 'openac.misbehave' stopped: panicked: ")
            && error.contains("misbehave was told to panic"),
        "{error}"
    );
}

/// An endless loop is stopped at the call's deadline,
/// with the entry call it was in.
#[test]
fn an_endless_loop_is_stopped_at_its_deadline() {
    let started = std::time::Instant::now();
    assert_eq!(
        crash("loop"),
        "plugin 'openac.misbehave' stopped: ran for more than 1 s in one call (it looks stuck) \
         in event command; last imports: none"
    );
    // Never before the deadline; soon after it (the epoch ticks every
    // 10 ms; the test machine may be busy).
    let took = started.elapsed();
    assert!(
        took >= TEST_DEADLINE && took < TEST_DEADLINE * 10,
        "{took:?}"
    );
}

/// A misbehaving plugin on `engine` with its slow log at `slow_log`, and a
/// client whose clock runs (the `slow` chat line works by it).
fn chat_misbehave(
    tag: &str,
    engine: Arc<PluginEngine>,
    slow_log: Option<std::time::Duration>,
) -> (Root, PluginSession, FakeSession) {
    let root = Root::new(tag);
    install(&root.0, "misbehave");
    let mut options = options(&[&root.0], Some(&["openac.misbehave"]));
    options.slow_log = slow_log;
    let mut plugins = PluginSession::new(engine, options);
    let mut api = FakeSession {
        clock: true,
        ..FakeSession::default()
    };
    plugins.start(&mut api);
    assert_eq!(plugins.loaded_ids(), ["openac.misbehave"]);
    plugins.take_reports();
    (root, plugins, api)
}

fn say_to(plugins: &mut PluginSession, api: &mut FakeSession, text: &str) {
    plugins.queue_chat(ChatLine {
        text: text.to_owned(),
        display_text: text.to_owned(),
        ..ChatLine::default()
    });
    plugins.deliver(api);
}

/// A call stopped at its deadline names the entry call and the client
/// functions it called last, with their counts, in the report (the status
/// file's `pluginFailed`) and in the log: a fault on a live server
/// otherwise says nothing of where to look.
#[test]
fn a_stuck_call_names_the_call_and_its_last_imports() {
    let (_root, mut plugins, mut api) = chat_misbehave("stuck-named", short_engine(), None);
    say_to(&mut plugins, &mut api, "burn");
    let expected = "plugin 'openac.misbehave' stopped: ran for more than 1 s in one call (it \
                    looks stuck) in event chat line; last imports: host_setting \u{d7}1, \
                    host_window_attached \u{d7}3, host_clock \u{d7}2";
    assert_eq!(
        failed(&plugins.take_reports(), "openac.misbehave").as_deref(),
        Some(expected)
    );
    assert!(
        api.logs
            .iter()
            .any(|(id, level, m)| id == "openac.misbehave"
                && *level == LogLevel::Error
                && m == expected),
        "{:?}",
        api.logs
    );
    assert!(plugins.loaded_ids().is_empty());
}

/// The imports of the `slow` chat line: the known pattern, then the clock
/// read as often as the work took (`host_clock ×N`).
const SLOW_IMPORTS: &str = "host_setting \u{d7}1, host_window_attached \u{d7}3, host_clock \u{d7}";

/// The slow log (`OPENAC_PLUGIN_SLOW_LOG`): a call over the threshold is
/// reported with its entry call, its time and its imports; calls under it,
/// and every call with the log off, are not.
#[test]
fn the_slow_log_reports_calls_over_its_threshold() {
    let slow_reports = |plugins: &mut PluginSession| -> Vec<(String, String)> {
        plugins
            .take_reports()
            .into_iter()
            .filter_map(|r| match r {
                PluginReport::SlowCall { id, message } => Some((id, message)),
                _ => None,
            })
            .collect()
    };

    let threshold = std::time::Duration::from_millis(50);
    let (_root, mut plugins, mut api) = chat_misbehave("slow-log", engine(), Some(threshold));
    say_to(&mut plugins, &mut api, "slow");
    let reports = slow_reports(&mut plugins);
    assert_eq!(reports.len(), 1, "{reports:?}");
    let (id, message) = &reports[0];
    assert_eq!(id, "openac.misbehave");
    let took = plugins.last_call_time("openac.misbehave").expect("loaded");
    assert!(took > threshold, "{took:?}");
    let expected = format!(
        "event chat line took {:.1} ms (client ",
        took.as_secs_f64() * 1000.0
    );
    assert!(message.starts_with(&expected), "{message}");
    // The client's share names the import it spent longest on; the
    // plugin's busy loop reads the clock most.
    assert!(
        message.contains(" ms, most in host_clock \u{d7}"),
        "{message}"
    );
    assert!(
        message.contains(&format!("); imports: {SLOW_IMPORTS}")),
        "{message}"
    );
    // A tick that does next to nothing stays under the threshold.
    plugins.pump(0.015, None, &mut api);
    assert!(slow_reports(&mut plugins).is_empty());

    let (_root, mut plugins, mut api) = chat_misbehave("slow-log-off", engine(), None);
    say_to(&mut plugins, &mut api, "slow");
    assert!(slow_reports(&mut plugins).is_empty());
}

/// A known-spell list of 730 self buffs (a high-level caster's) reaches the
/// plugin whole, as full records (`spells_known`) and as ids
/// (`spells_known_ids`). What each costs the plugin was measured with the
/// old fuel metering.
#[test]
fn the_known_spell_lists_reach_the_plugin_as_records_and_ids() {
    const KNOWN: u32 = 730;
    let root = Root::new("known-lists");
    install(&root.0, "probe");
    let mut api = FakeSession::default();
    let mut plugins = session(&[&root.0], None);
    plugins.start(&mut api);
    for (known, command) in [(KNOWN, "known"), (0, "known"), (KNOWN, "known-ids")] {
        api.known = known;
        assert!(plugins.handle_line(&format!("/probe {command} 1"), &mut api));
        assert!(
            api.messages("openac.probe")
                .contains(&format!("probe {command}: {known}")),
            "{:?}",
            api.messages("openac.probe")
        );
    }
    plugins.shutdown(&mut api);
}

/// The whole spell table (6266 records in the retail dats) in one command:
/// the SDK reads it page by page (the one-reply table once ran out of the
/// old fuel budget live).
#[test]
fn the_whole_spell_table_is_read_in_one_call_page_by_page() {
    let root = Root::new("spell-pages");
    install(&root.0, "probe");
    let mut api = FakeSession {
        spell_table: 6266,
        ..FakeSession::default()
    };
    let mut plugins = session(&[&root.0], None);
    plugins.start(&mut api);
    assert!(plugins.handle_line("/probe spells", &mut api));
    assert!(
        plugins
            .take_reports()
            .iter()
            .all(|r| !matches!(r, PluginReport::Failed { .. }))
    );
    assert!(
        api.messages("openac.probe")
            .contains(&"probe census spells_all: 6266 spells".to_owned()),
        "{:?}",
        api.messages("openac.probe")
    );
    plugins.shutdown(&mut api);
}

/// The landscape reads through a real module (the parity relay, which calls
/// a client function by name): the request reaches the client as the plugin
/// encoded it, and the client's answers reach the plugin whole.
#[test]
fn the_landscape_reads_reach_a_real_plugin() {
    use ac_plugin::abi::world::{LandblockTerrain, TerrainColor};
    let root = Root::new("landscape");
    install(&root.0, "parity");
    let mut api = FakeSession::default();
    let mut plugins = session(&[&root.0], None);
    plugins.start(&mut api);
    let hex = |bytes: &[u8]| -> String { bytes.iter().map(|b| format!("{b:02x}")).collect() };
    let unhex = |text: &str| -> Vec<u8> {
        (0..text.len())
            .step_by(2)
            .map(|at| u8::from_str_radix(&text[at..at + 2], 16).unwrap())
            .collect()
    };
    let mut reply = |seq: u32, import: Import, request: &[u8]| -> Vec<u8> {
        let line = format!("/parity call {seq} {} {}", import.name(), hex(request));
        assert!(plugins.handle_line(&line, &mut api));
        let prefix = format!("parity reply {seq} ");
        let messages = api.messages("openac.parity");
        let answer = messages
            .iter()
            .find_map(|m| m.strip_prefix(&prefix))
            .unwrap_or_else(|| panic!("no reply {seq}: {messages:?}"));
        unhex(answer)
    };
    let asked: Vec<u16> = vec![0xA9B4, 0x01B4];
    let terrain =
        Vec::<LandblockTerrain>::from_bytes(&reply(1, Import::LandscapeTerrain, &asked.to_bytes()))
            .unwrap();
    let answered: Vec<(u16, usize, f32)> = terrain
        .iter()
        .map(|t| {
            (
                t.landblock,
                t.vertices.len(),
                t.vertex(8, 8).unwrap().height_meters,
            )
        })
        .collect();
    assert_eq!(
        answered,
        [
            (0xA9B4, 81, f32::from(0xA9B4u16)),
            (0x01B4, 81, f32::from(0x01B4u16))
        ]
    );
    let palette =
        Vec::<TerrainColor>::from_bytes(&reply(2, Import::LandscapeTerrainPalette, &[])).unwrap();
    assert_eq!(palette.len(), 1);
    assert_eq!((palette[0].rgb, palette[0].map_rgb), (0x5A_5E30, 0x64_9664));
    assert_eq!(palette[0].name.as_deref(), Some("Grassland"));
    plugins.shutdown(&mut api);
}

/// OpenAC `ChatSuppressionFilterTests` across real plugins: filters run in
/// registration order across plugins and the first rejection decides; a
/// filter that faults suppresses nothing (here its plugin is unloaded)
/// and the next one is still asked; removing a filter
/// stops it suppressing; unloading takes a plugin's filters with it.
#[test]
fn chat_filters_drop_lines_in_registration_order_and_a_faulting_one_drops_nothing() {
    let (_root, mut plugins, mut api) = misbehave_session("filters", &[]);
    let line = |text: &str| ChatLine {
        kind: 4,
        text: text.to_owned(),
        ..ChatLine::default()
    };
    assert!(!plugins.filters_chat());
    assert!(plugins.handle_line("/crash filter", &mut api));
    assert!(plugins.handle_line("/hello filter noise", &mut api));
    assert!(plugins.filters_chat());
    plugins.take_reports();
    // The misbehaving filter is asked first, faults and keeps the line;
    // hello's, asked next, drops it.
    assert!(plugins.filter_chat(&line("noise to drop"), &mut api));
    let error = failed(&plugins.take_reports(), "openac.misbehave").unwrap();
    assert!(error.contains("misbehave's filter panicked"), "{error}");
    assert_eq!(plugins.loaded_ids(), ["openac.hello"]);
    assert!(!plugins.filter_chat(&line("signal to keep"), &mut api));
    // A status notice is offered under its own kind.
    let status = ChatLine {
        kind: ac_plugin::abi::types::STATUS_TEXT_KIND,
        ..line("noise in the status")
    };
    assert!(plugins.filter_chat(&status, &mut api));
    assert!(plugins.handle_line("/hello unfilter", &mut api));
    assert_eq!(api.posts.last().unwrap(), "hello unfiltered: true");
    assert!(!plugins.filters_chat());
    assert!(!plugins.filter_chat(&line("noise kept now"), &mut api));
    assert!(plugins.handle_line("/hello filter noise", &mut api));
    plugins.request_reload("openac.hello");
    plugins.pump(0.0, None, &mut api);
    assert!(
        !plugins.filters_chat(),
        "a reload starts without the filter"
    );
    plugins.shutdown(&mut api);
}

/// OpenAC `RuntimeWorldEntityProjectionTests` (a late subscriber gets every
/// entity replayed, each once): a plugin enabled into a populated world
/// hears `EntitySpawned` for each object already there; a spawn of one of
/// them queued in the same pass is not delivered again, a new one is.
#[test]
fn a_plugin_enabled_late_hears_every_entity_once() {
    use ac_plugin::abi::Event;
    use ac_plugin::abi::world::EntitySnapshot;
    let entity = |id: u32| EntitySnapshot {
        id,
        source_id: 0x7000_0000 | id,
        position: (1.0, 2.0, 3.0),
        rotation: (0.0, 0.0, 0.0, 1.0),
    };
    let root = Root::new("entities");
    install(&root.0, "probe");
    let mut api = FakeSession {
        entities: vec![entity(1), entity(2)],
        ..FakeSession::default()
    };
    let mut plugins = session(&[&root.0], None);
    plugins.start(&mut api);
    assert!(plugins.handle_line("/probe events", &mut api));
    assert!(
        api.messages("openac.probe")
            .contains(&"probe events: EntitySpawned, EntitySpawned".to_owned())
    );
    plugins.queue_event(Event::EntitySpawned(entity(2)));
    plugins.queue_event(Event::EntitySpawned(entity(3)));
    plugins.pump(0.0, None, &mut api);
    assert!(plugins.handle_line("/probe events", &mut api));
    assert_eq!(
        api.messages("openac.probe").last().unwrap(),
        "probe counted: {\"EntitySpawned\": 1}"
    );
    // Later passes deliver every spawn.
    plugins.queue_event(Event::EntitySpawned(entity(2)));
    plugins.pump(0.0, None, &mut api);
    assert!(plugins.handle_line("/probe events", &mut api));
    assert_eq!(
        api.messages("openac.probe").last().unwrap(),
        "probe counted: {\"EntitySpawned\": 1}"
    );
    plugins.shutdown(&mut api);
}

#[test]
fn memory_past_the_manifest_cap_stops_the_plugin() {
    assert_eq!(
        crash("oom"),
        "plugin 'openac.misbehave' stopped: ran out of memory (its limit is 16 MiB)"
    );
}

#[test]
fn a_refusal_or_panic_at_start_fails_the_load_and_releases_its_verbs() {
    let (_root, mut plugins, _api) = misbehave_session("refuse", &[("refuse", "no profile")]);
    assert_eq!(
        failed(&plugins.take_reports(), "openac.misbehave").unwrap(),
        "no profile"
    );
    assert!(!plugins.claims_line("/crash"));
    assert_eq!(plugins.loaded_ids(), vec!["openac.hello"]);

    let (_root, mut plugins, _api) = misbehave_session("enable-panic", &[("panic-on-enable", "")]);
    let error = failed(&plugins.take_reports(), "openac.misbehave").unwrap();
    assert!(
        error.starts_with("panicked: ") && error.contains("misbehave panicked in enable"),
        "{error}"
    );
    assert!(!plugins.claims_line("/crash"));
}

#[test]
fn a_trap_in_a_tick_unloads_and_plugin_reload_brings_it_back() {
    let (_root, mut plugins, mut api) = misbehave_session("tick", &[("panic-on-tick", "3")]);
    plugins.take_reports();
    plugins.pump(0.060, None, &mut api);
    let error = failed(&plugins.take_reports(), "openac.misbehave").unwrap();
    assert!(error.contains("misbehave panicked on tick 3"), "{error}");
    assert_eq!(plugins.loaded_ids(), vec!["openac.hello"]);

    // No automatic restart; /plugin reload loads it fresh at the next pump.
    assert!(plugins.claims_line("/plugin reload openac.misbehave"));
    assert!(plugins.handle_line("/plugin reload openac.misbehave", &mut api));
    assert!(plugins.take_reports().is_empty());
    plugins.pump(0.0, None, &mut api);
    assert_eq!(
        plugins.take_reports(),
        vec![PluginReport::Loaded {
            id: "openac.misbehave".into()
        }]
    );
    assert_eq!(api.posts.last().unwrap(), "Reloaded Misbehave 0.1.0.");
    // It was unloaded, so it comes back at the end of the load order.
    assert_eq!(
        plugins.loaded_ids(),
        vec!["openac.hello", "openac.misbehave"]
    );
    assert!(plugins.claims_line("/crash"));

    assert!(plugins.handle_line("/plugin", &mut api));
    assert_eq!(
        api.posts[api.posts.len() - 3..],
        [
            "Plugins:".to_owned(),
            "  openac.hello 0.1.0 (Hello): loaded".to_owned(),
            "  openac.misbehave 0.1.0 (Misbehave): loaded".to_owned(),
        ]
    );
    // A loaded plugin reloaded keeps its place.
    plugins.request_reload("openac.hello");
    plugins.pump(0.0, None, &mut api);
    assert_eq!(
        plugins.loaded_ids(),
        vec!["openac.hello", "openac.misbehave"]
    );
}

#[test]
fn reload_of_a_loaded_plugin_stops_it_first() {
    let root = Root::new("reload");
    install(&root.0, "hello");
    let mut api = FakeSession::default();
    let mut plugins = session(&[&root.0], None);
    plugins.start(&mut api);
    plugins.pump(0.045, None, &mut api);
    plugins.request_reload("openac.hello");
    plugins.pump(0.0, None, &mut api);
    let messages = api.messages("openac.hello");
    assert!(messages.contains(&"hello disabled after 3 ticks".to_owned()));
    assert_eq!(plugins.loaded_ids(), vec!["openac.hello"]);
    assert!(plugins.handle_line("/hello ticks", &mut api));
    assert_eq!(api.posts.last().unwrap(), "hello: 0 ticks");
}

#[test]
fn per_session_settings_reach_only_their_plugin() {
    let root = Root::new("settings");
    install(&root.0, "probe");
    install(&root.0, "hello");
    let declared = std::collections::BTreeMap::from([
        (
            "openac.probe".to_owned(),
            std::collections::BTreeMap::from([("label".to_owned(), "bot-1".to_owned())]),
        ),
        (
            "openac.hello".to_owned(),
            std::collections::BTreeMap::from([("greeting".to_owned(), "Well met".to_owned())]),
        ),
    ]);
    let mut options = options(&[&root.0], None);
    options.settings = PluginSessionSettings::from_map(&declared);
    let mut plugins = PluginSession::new(engine(), options);
    let mut api = FakeSession::default();
    plugins.start(&mut api);
    assert!(
        api.messages("openac.probe")
            .contains(&"probe enable: character=none label=bot-1 window=false".to_owned())
    );
    assert!(plugins.handle_line("/hello", &mut api));
    assert_eq!(
        api.posts.last().unwrap(),
        "Well met, stranger! (no window is attached)"
    );
}

/// The parity harness in small: the same scripted run of the
/// probe plugin with a window attached and without one; the transcripts
/// may differ only where an import's answer depends on the window.
#[test]
fn the_probe_transcript_is_the_same_with_and_without_a_window() {
    fn run(window: bool) -> Vec<String> {
        let root = Root::new(&format!("parity-{window}"));
        install(&root.0, "probe");
        let mut api = FakeSession {
            window,
            character: Some(CharacterInfo {
                id: 0x5000_0002,
                name: "Horan".into(),
                world: "Local".into(),
                ..CharacterInfo::default()
            }),
            ..FakeSession::default()
        };
        let mut plugins = session(&[&root.0], None);
        plugins.start(&mut api);
        for _ in 0..150 {
            plugins.pump(0.010, Some(1), &mut api);
        }
        assert!(plugins.handle_line("/probe now", &mut api));
        plugins.shutdown(&mut api);
        api.messages("openac.probe")
    }
    let windowed = run(true);
    let windowless = run(false);
    assert_eq!(windowed.len(), windowless.len());
    for (a, b) in windowed.iter().zip(&windowless) {
        // Allowed difference: host_window_attached's answer.
        assert_eq!(
            a.replace("window=true", "window=?"),
            b.replace("window=false", "window=?")
        );
    }
    assert!(windowless.contains(&"probe stop: ticks=100".to_owned()));
}

/// The census: every import, its permission (`-` for none) and
/// whether its answer depends on a window (`window`), in table order, as
/// `tests/import_census.txt` lists them. Adding an import, or making one
/// window-dependent, fails here until that list (and the probe harness's
/// allowed differences) is updated on purpose.
#[test]
fn import_census() {
    let census: Vec<String> = Import::ALL
        .iter()
        .map(|i| {
            let permission = i.permission().map_or("-", |p| p.name());
            let window = if i.needs_window() { " window" } else { "" };
            format!("{} {permission}{window}", i.name())
        })
        .collect();
    let expected: Vec<&str> = include_str!("import_census.txt")
        .lines()
        .map(str::trim)
        .filter(|l| !l.is_empty() && !l.starts_with('#'))
        .collect();
    assert_eq!(census, expected);
}

/// A window's typed bindings answer through a real module: a checkbox whose
/// state is unset draws from `OptionalBool` and sends a `BoolAction`, and a
/// colour picker sends its pick back as a `UintAction`.
#[test]
fn typed_bool_and_uint_actions_reach_a_real_plugin() {
    use ac_plugin_ui::{ActionValue, UiInput, UiValue};
    let root = Root::new("typed-actions");
    install(&root.0, "uidemo");
    let mut api = FakeSession::default();
    let mut plugins = session(&[&root.0], Some(&["openac.uidemo"]));
    plugins.start(&mut api);
    assert!(plugins.handle_line("/uidemo typed", &mut api));
    let panel = api
        .ui
        .panels()
        .find(|p| p.descriptor.window_id == "typed")
        .expect("the typed window")
        .clone();
    assert_eq!(
        panel.values.get("Maybe"),
        Some(&UiValue::OptionalBool(None))
    );
    assert_eq!(
        panel.values.get("Colour"),
        Some(&UiValue::Uint(0xFF33_6699))
    );
    for (handler, value) in [
        ("SetFlag", ActionValue::Bool(true)),
        ("SetFlag", ActionValue::Bool(false)),
        ("SetColour", ActionValue::Uint(0x8011_2233)),
    ] {
        api.ui.input(UiInput::Action {
            panel: panel.id,
            handler: handler.to_owned(),
            value,
        });
    }
    plugins.pump(0.0, None, &mut api);
    let heard: Vec<&str> = api
        .logs
        .iter()
        .map(|(_, _, line)| line.as_str())
        .filter(|line| line.starts_with("uidemo typed Set"))
        .collect();
    assert_eq!(
        heard,
        [
            "uidemo typed SetFlag Some(true)",
            "uidemo typed SetFlag Some(false)",
            "uidemo typed SetColour Some(\"80112233\")",
        ]
    );
    plugins.shutdown(&mut api);
}

/// A window's own dock icon, through a real module: a file in the plugin's
/// folder reaches the window's view, with its light version; one that breaks the
/// rules leaves the slot to the next icon and says why; both go with the plugin.
#[test]
fn a_windows_own_icon_file_reaches_its_view() {
    let pixels = vec![200_u8; 64 * 64 * 4];
    let good = ac_bits::png::encode_rgba(64, 64, &pixels);
    let wrong_size = ac_bits::png::encode_rgba(8, 8, &[200_u8; 8 * 8 * 4]);
    let root = Root::new("window-icon");
    let folder = install(&root.0, "rawbytes");
    std::fs::write(folder.join("raw.png"), &good).unwrap();
    std::fs::write(folder.join("raw.light.png"), &good).unwrap();
    std::fs::write(folder.join("rawtyped.png"), &wrong_size).unwrap();
    let mut api = FakeSession::default();
    api.ui.set_file_images(Some(std::sync::Arc::new(
        ac_plugin::file_image::PluginFileImageLoader::new(),
    )));
    let mut plugins = session(&[&root.0], Some(&["openac.rawbytes"]));
    plugins.start(&mut api);
    assert!(plugins.handle_line(&format!("/rawpanel {API_CURRENT}"), &mut api));
    plugins.pump(0.0, None, &mut api);
    let panels: Vec<_> = api.ui.panels().cloned().collect();
    let icon = panels[0].window_icon.as_ref().expect("the file was read");
    assert_eq!(icon.named.as_ref().map(|i| i.width), Some(64));
    assert!(icon.light.is_some() && icon.dark.is_none());
    assert_eq!(panels[1].window_icon, None);
    assert!(
        api.logs
            .iter()
            .any(|(_, _, line)| line.contains("plugin icon 'openac.rawbytes/rawtyped.png' ignored")),
        "{:?}",
        api.logs
    );
    plugins.shutdown(&mut api);
    assert_eq!(api.ui.panels().count(), 0);
}

/// A plugin installed with an `icon.png` has it on its windows. dock
/// buttons (OpenAC `RetailUiRuntime.ResolvePluginFileIcon`); one without,
/// or with a file the icon rules refuse, has none and the dock falls back
/// to the descriptor's icon. Unloading forgets it.
#[test]
fn a_plugins_icon_png_reaches_its_windows() {
    let pixels: Vec<u8> = (0..64 * 64)
        .flat_map(|i: u32| [(i % 64) as u8, (i / 64) as u8, 200, 255])
        .collect();
    let png = ac_bits::png::encode_rgba(64, 64, &pixels);
    let cases: [(&str, Option<Vec<u8>>, bool); 3] = [
        ("icon-valid", Some(png), true),
        ("icon-none", None, false),
        (
            "icon-small",
            Some(ac_bits::png::encode_rgba(2, 2, &[9; 16])),
            false,
        ),
    ];
    for (tag, file, shown) in cases {
        let root = Root::new(tag);
        let folder = install(&root.0, "uidemo");
        if let Some(bytes) = &file {
            std::fs::write(folder.join(ac_plugin::icon::ICON_FILE_NAME), bytes).unwrap();
        }
        let mut api = FakeSession::default();
        let mut plugins = session(&[&root.0], None);
        plugins.start(&mut api);
        let panel = api.ui.panels().next().unwrap().clone();
        assert_eq!(panel.file_icon.is_some(), shown, "{tag}");
        if let Some(icon) = &panel.file_icon {
            assert_eq!((icon.width, icon.height), (64, 64));
            assert_eq!(*icon.rgba, pixels);
        }
        plugins.shutdown(&mut api);
        assert!(api.ui.panels().next().is_none());
    }
}

/// `uidemo` (plugin UI): its window, canvas, image and label are the
/// session's state, the same with no window; a window that attaches
/// mid-run gets them from the snapshot; the player's input reaches the
/// plugin as UI events; unloading takes everything down.
#[test]
fn uidemo_keeps_its_ui_in_the_session_with_or_without_a_window() {
    use ac_plugin_ui::{
        ActionValue, ClientWindow, PluginUiView, PointerEvent, PointerKind, UiInput, UiValue,
    };
    let root = Root::new("uidemo");
    install(&root.0, "uidemo");
    let mut api = FakeSession::default();
    let mut plugins = session(&[&root.0], None);
    plugins.start(&mut api);
    assert_eq!(
        plugins.take_reports(),
        vec![PluginReport::Loaded {
            id: "openac.uidemo".into()
        }]
    );
    // Headless: the window exists and answers, the canvas is painted.
    assert!(
        api.messages("openac.uidemo").contains(
            &"uidemo enable: panel shown=true exists=true ping-control=true canvas=180x110 \
          visible=true listening=true measure=0x0 images=1 window=false"
                .to_owned()
        )
    );
    let panel = api.ui.panels().next().unwrap().clone();
    assert_eq!(panel.window_name(), "plugin:openac.uidemo:main");
    assert_eq!(panel.owner.display_name, "UI Demo");
    assert_eq!(
        panel.values.get("VolumeText"),
        Some(&UiValue::Text("Volume: 50".into()))
    );
    let canvas = api.ui.canvases().next().unwrap().clone();
    assert!(!canvas.paint.is_empty());
    assert!(canvas.takes_input());

    // In the world: the label goes up over the character.
    api.character = Some(CharacterInfo {
        id: 0x5000_000B,
        name: "+Horan".into(),
        world: "Local".into(),
        ..CharacterInfo::default()
    });
    plugins.pump(0.015, Some(1), &mut api);
    let labels = api.ui.merged_labels();
    assert_eq!(labels.len(), 1);
    assert_eq!(
        (labels[0].object_id, labels[0].text.as_str()),
        (0x5000_000B, "uidemo")
    );
    assert_eq!(api.ui.merged_triangles().len(), 1);
    let icons = api.ui.merged_icons();
    assert_eq!(icons.len(), 1);
    assert_eq!(icons[0].owner, "openac.uidemo");
    assert_eq!(
        icons[0].icon.anchor,
        ac_plugin_ui::MarkerAnchor::Object(0x5000_000B)
    );
    assert_eq!(api.ui.merged_shapes().len(), 1);

    // The player's input reaches the plugin with its next pump.
    api.ui.input(UiInput::Action {
        panel: panel.id,
        handler: "Ping".into(),
        value: ActionValue::None,
    });
    api.ui.input(UiInput::Action {
        panel: panel.id,
        handler: "SetVolume".into(),
        value: ActionValue::Float(80.0),
    });
    api.ui.input(UiInput::Pointer {
        canvas: canvas.id,
        event: PointerEvent {
            kind: PointerKind::Down,
            x: 40.0,
            y: 50.0,
            button: 1,
            modifiers: 0,
            wheel_delta: 0,
        },
        place: None,
    });
    plugins.pump(0.0, Some(1), &mut api);
    let messages = api.messages("openac.uidemo");
    assert!(messages.contains(
        &"uidemo ui SetVolume: pings=1 label=true volume=80 selected=-1 submitted=[]".to_owned()
    ));
    assert!(messages.contains(&"uidemo pointer kind=0 at 40,50 button=1".to_owned()));
    assert_eq!(
        api.ui.panels().next().unwrap().values.get("VolumeText"),
        Some(&UiValue::Text("Volume: 80".into()))
    );

    // No window: the client's windows are unavailable.
    assert!(plugins.handle_line("/uidemo inventory", &mut api));
    assert_eq!(
        api.messages("openac.uidemo").last().unwrap(),
        "uidemo inventory toggled: visible=false"
    );

    // A window attaches mid-run: it gets everything from the snapshot.
    api.ui.take_changes();
    let mut view = PluginUiView::new();
    view.apply_all(api.ui.snapshot());
    assert_eq!(view.panels.len(), 1);
    assert_eq!(view.canvases.len(), 1);
    assert_eq!(view.images.len(), 1);
    assert_eq!(view.labels.len(), 1);
    api.window = true;
    api.ui.set_window_attached(true);
    api.ui.input(UiInput::ClientWindows {
        mounted: ClientWindow::ALL.iter().fold(0, |m, w| m | w.bit()),
        visible: 0,
    });
    plugins.set_window_attached(true);
    plugins.pump(0.0, Some(1), &mut api);
    assert!(plugins.handle_line("/uidemo inventory", &mut api));
    assert_eq!(
        api.messages("openac.uidemo").last().unwrap(),
        "uidemo inventory toggled: visible=true"
    );
    // The window hears the request and the values since the snapshot.
    view.apply_all(api.ui.take_changes());
    assert_eq!(
        view.take_client_requests(),
        vec![(
            ClientWindow::Inventory,
            ac_plugin_ui::ClientWindowAction::Toggle
        )]
    );

    // Unloaded: its UI goes with it.
    plugins.shutdown(&mut api);
    assert_eq!(api.ui.panels().count(), 0);
    assert_eq!(api.ui.canvases().count(), 0);
    assert!(api.ui.merged_labels().is_empty());
    assert_eq!(api.ui.image_count("openac.uidemo"), 0);
}

/// A held pointer's release reaches the plugin through the session measured
/// from where the canvas is now, not from where the window still had it:
/// the window follows the plugin's moves a moment late. `uidemo`'s canvas
/// (180 x 110, top-right, offset -220, 40) is at (400, 40) on an 800 x 600
/// layer; the window still had it 30 to the left. A press stays as the
/// player saw it.
#[test]
fn the_session_hands_a_held_pointer_over_measured_from_the_canvass_latest_place() {
    use ac_plugin_ui::{CanvasPlace, PointerEvent, PointerKind, UiInput};
    let root = Root::new("uidemo-placed");
    install(&root.0, "uidemo");
    let mut api = FakeSession::default();
    let mut plugins = session(&[&root.0], None);
    plugins.start(&mut api);
    let canvas = api.ui.canvases().next().unwrap().id;
    let place = CanvasPlace {
        origin: (370.0, 40.0),
        layer: (800.0, 600.0),
    };
    for kind in [PointerKind::Down, PointerKind::Up] {
        api.ui.input(UiInput::Pointer {
            canvas,
            event: PointerEvent {
                kind,
                x: 40.0,
                y: 50.0,
                button: 1,
                modifiers: 0,
                wheel_delta: 0,
            },
            place: Some(place),
        });
    }
    plugins.pump(0.0, Some(1), &mut api);
    let heard: Vec<String> = api
        .messages("openac.uidemo")
        .into_iter()
        .filter(|m| m.starts_with("uidemo pointer"))
        .collect();
    assert_eq!(
        heard,
        vec![
            "uidemo pointer kind=0 at 40,50 button=1".to_owned(),
            "uidemo pointer kind=1 at 10,50 button=1".to_owned(),
        ]
    );
}

/// API 1.6 added hover and leave on a canvas. `uidemo` built for this API
/// hears a hover (two moves before its pump are one hover, at the later
/// place) and the leave; built for API 1.5 it hears neither, only the
/// press it heard before: an older plugin may read an unknown kind as a
/// drag.
#[test]
fn a_canvas_hover_and_leave_reach_only_plugins_built_for_api_1_6() {
    use ac_plugin_ui::{PointerEvent, PointerKind, UiInput};
    for version in [ApiVersion::new(1, 5), API_CURRENT] {
        let root = Root::new(&format!("uidemo-hover-{version}"));
        install_built_for(&root.0, "uidemo", version);
        let mut api = FakeSession::default();
        let mut plugins = session(&[&root.0], Some(&["openac.uidemo"]));
        plugins.start(&mut api);
        assert_eq!(plugins.loaded_ids(), ["openac.uidemo"], "{version}");
        let canvas = api.ui.canvases().next().unwrap().id;
        let mut pointer = |kind, x, button| {
            api.ui.input(UiInput::Pointer {
                canvas,
                event: PointerEvent {
                    kind,
                    x,
                    y: 50.0,
                    button,
                    modifiers: 0,
                    wheel_delta: 0,
                },
                place: None,
            });
        };
        pointer(PointerKind::Hover, 20.0, 0);
        pointer(PointerKind::Hover, 30.0, 0);
        pointer(PointerKind::Down, 30.0, 1);
        pointer(PointerKind::Up, 30.0, 1);
        pointer(PointerKind::Leave, 30.0, 0);
        plugins.pump(0.0, Some(1), &mut api);
        let heard: Vec<String> = api
            .messages("openac.uidemo")
            .into_iter()
            .filter(|m| m.starts_with("uidemo pointer"))
            .collect();
        let mut expected = vec![
            "uidemo pointer kind=0 at 30,50 button=1".to_owned(),
            "uidemo pointer kind=1 at 30,50 button=1".to_owned(),
        ];
        if version == API_CURRENT {
            expected.insert(0, "uidemo pointer kind=5 at 30,50 button=0".to_owned());
            expected.push("uidemo pointer kind=6 at 30,50 button=0".to_owned());
        }
        assert_eq!(heard, expected, "{version}");
        plugins.shutdown(&mut api);
    }
}

/// OpenAC hands a plugin's submit through the chat
/// pipeline inside the call, and the lines it writes reach the chat
/// subscribers there. Without re-entering the plugin, the submit is
/// carried out right after the call returns and the plugins hear its
/// lines before anything else; lines written while hearing them wait for
/// the next call, so a plugin answering its own lines cannot loop.
#[test]
fn a_plugins_submit_is_carried_out_and_heard_right_after_its_call() {
    let root = Root::new("hello-speaks");
    install(&root.0, "hello");
    let mut api = FakeSession {
        speaks: true,
        ..FakeSession::default()
    };
    let mut plugins = session(&[&root.0], None);
    plugins.start(&mut api);

    // The submit was spoken and heard within the one call that made it:
    // nothing is left for the session, no pass in between.
    assert!(plugins.handle_line("/hello say ping hello everyone", &mut api));
    assert!(plugins.take_submits().is_empty());
    assert_eq!(
        api.posts,
        ["hello heard #1: Horan says, \"ping hello everyone\""]
    );
    // The plugin's answer is a line too; it waits for the next call.
    assert_eq!(api.unheard.len(), 1);

    // A plugin's own post is heard right after the call that wrote it,
    // before the events queued behind it.
    api.unheard.clear();
    assert!(plugins.handle_line("/hello", &mut api));
    assert!(api.unheard.is_empty(), "heard at once: {:?}", api.unheard);
}

/// Hotkeys through a real plugin: registered with no window
/// they report unbound (OpenAC's headless registration); once a window
/// attaches and names its key map's chords, `ping` binds and `escape`, a
/// client chord, is refused; the window's report of a press reaches the
/// plugin; a rebind is stored as the player's override; unloading takes
/// them away.
#[test]
fn uidemo_hotkeys_bind_while_a_window_with_a_key_map_is_attached() {
    use ac_plugin::abi::keys::PluginKey;
    use ac_plugin_ui::UiInput;
    use ac_plugin_ui::hotkeys::Chord;
    let root = Root::new("hotkeys");
    install(&root.0, "uidemo");
    let mut api = FakeSession::default();
    let mut plugins = session(&[&root.0], None);
    plugins.start(&mut api);
    let said =
        |api: &FakeSession, line: &str| api.messages("openac.uidemo").iter().any(|m| m == line);
    assert!(said(
        &api,
        "uidemo hotkeys enable: ping bound=false key=0 escape bound=false"
    ));
    api.ui.set_window_attached(true);
    api.ui.input(UiInput::ClientChords(vec![Chord::key(
        PluginKey::Escape.code(),
    )]));
    assert!(plugins.handle_line("/uidemo hotkeys", &mut api));
    assert!(said(
        &api,
        "uidemo hotkeys now: ping bound=true key=16 escape bound=false"
    ));
    for id in ["ping", "escape"] {
        api.ui.input(UiInput::Hotkey {
            owner: "openac.uidemo".into(),
            id: id.into(),
        });
    }
    plugins.pump(0.0, None, &mut api);
    let presses: Vec<String> = api
        .messages("openac.uidemo")
        .into_iter()
        .filter(|m| m.starts_with("uidemo hotkey "))
        .collect();
    assert_eq!(
        presses,
        ["uidemo hotkey ping"],
        "the refused one never fires"
    );
    assert!(plugins.handle_line("/uidemo rebind", &mut api));
    assert!(said(
        &api,
        "uidemo hotkeys rebound: ping bound=true key=15 escape bound=false"
    ));
    assert!(api.ui.take_hotkey_overrides_changed());
    assert_eq!(
        api.ui.hotkeys().overrides().get("openac.uidemo:ping"),
        Some(&Chord {
            key: PluginKey::O.code(),
            ctrl: true,
            alt: false,
            shift: true,
        })
    );
    plugins.shutdown(&mut api);
    assert!(api.ui.hotkeys().bound().is_empty());
}

/// OpenAC `PluginCommandRegistryTests.DefinitionAliasesCompletionAndHelpAreSupported`
/// across two real plugins: `probe` reads the help list and the
/// definitions, and asks `hello` (through its callback) to complete a
/// partly typed `/hello`; a verb nobody holds completes to nothing, and a
/// plugin asking about its own verb is answered nothing.
#[test]
fn a_plugin_reads_the_help_list_and_another_plugins_completions() {
    let root = Root::new("completion");
    install(&root.0, "hello");
    install(&root.0, "probe");
    let mut api = FakeSession::default();
    let mut plugins = session(&[&root.0], Some(&["openac.hello", "openac.probe"]));
    plugins.start(&mut api);
    let said =
        |api: &FakeSession, line: &str| api.messages("openac.probe").iter().any(|m| m == line);
    assert!(plugins.handle_line("/probe help", &mut api));
    assert!(
        said(
            &api,
            "probe help: \"/hello - Greets you (hello say <text>, hello ticks)\\n/probe - Logs the \
             facts a plugin reads\" [\"hello[]\", \"probe[]\"]"
        ),
        "{:?}",
        api.messages("openac.probe")
    );
    assert!(plugins.handle_line("/probe complete hello S", &mut api));
    assert!(said(
        &api,
        "probe complete hello S: [\"say (say text aloud)\", \"store\"]"
    ));
    assert!(plugins.handle_line("/probe complete HELLO", &mut api));
    assert!(said(
        &api,
        "probe complete HELLO: [\"classify (ask another plugin's loot classifier)\", \"filter\", \
         \"intercept\", \"say (say text aloud)\", \"store\", \"ticks\", \"unfilter\"]"
    ));
    assert!(plugins.handle_line("/probe complete nobody x", &mut api));
    assert!(said(&api, "probe complete nobody x: []"));
    assert!(plugins.handle_line("/probe complete probe c", &mut api));
    assert!(said(&api, "probe complete probe c: []"));
    plugins.shutdown(&mut api);
}

/// Input actions (a gap in OpenAC): a plugin that listens hears the
/// client's input actions as the window's key map fires them, by name;
/// none arrive while no window is attached, and none after it stops
/// listening or is unloaded.
#[test]
fn uidemo_hears_the_input_actions_it_listens_for() {
    use ac_plugin_ui::UiInput;
    let root = Root::new("actions");
    install(&root.0, "uidemo");
    let mut api = FakeSession::default();
    let mut plugins = session(&[&root.0], None);
    plugins.start(&mut api);
    let fire = |api: &mut FakeSession, action: &str, activation: &str| {
        api.ui.input(UiInput::InputAction {
            action: action.into(),
            activation: activation.into(),
        });
    };
    let heard = |api: &FakeSession| -> Vec<String> {
        api.messages("openac.uidemo")
            .into_iter()
            .filter(|m| m.starts_with("uidemo action"))
            .collect()
    };
    assert!(plugins.handle_line("/uidemo actions", &mut api));
    fire(&mut api, "MovementForward", "Press");
    plugins.pump(0.0, None, &mut api);
    assert_eq!(heard(&api), ["uidemo actions true (was false)"]);

    api.ui.set_window_attached(true);
    fire(&mut api, "MovementForward", "Press");
    fire(&mut api, "MovementForward", "Release");
    plugins.pump(0.0, None, &mut api);
    assert!(plugins.handle_line("/uidemo actions off", &mut api));
    fire(&mut api, "UseQuickSlot_1", "Press");
    plugins.pump(0.0, None, &mut api);
    assert_eq!(
        heard(&api),
        [
            "uidemo actions true (was false)",
            "uidemo action MovementForward Press",
            "uidemo action MovementForward Release",
            "uidemo actions false (was true)",
        ]
    );
    assert!(plugins.handle_line("/uidemo actions", &mut api));
    plugins.shutdown(&mut api);
    assert!(!api.ui.hears_input_actions("openac.uidemo"));
}

// --- Reloading while the session runs ---------------------------------------------------------
// OpenAC `tests/AcDream.Core.Tests/Plugins/PluginReloadTests.cs`.

fn reloaded(api: &FakeSession) -> Vec<String> {
    api.posts
        .iter()
        .filter(|p| p.starts_with("Reloaded"))
        .cloned()
        .collect()
}

fn enabled_count(api: &FakeSession) -> usize {
    api.messages("openac.hello")
        .iter()
        .filter(|m| m.starts_with("hello openac.hello enabled"))
        .count()
}

/// Rewrites a plugin's `.wasm` with the test build's bytes, as a rebuild
/// does (a fresh write time).
fn rewrite_wasm(folder: &Path, name: &str) {
    let bytes =
        std::fs::read(ac_plugin::fixtures::wasm_directory().join(format!("{name}.wasm"))).unwrap();
    std::fs::write(folder.join(format!("{name}.wasm")), bytes).unwrap();
}

// OpenAC ARunningPluginsFilesCanBeOverwrittenAndDeleted.
#[test]
fn a_running_plugins_files_can_be_overwritten_and_deleted() {
    let root = Root::new("reload-files");
    let folder = install(&root.0, "hello");
    let mut api = FakeSession::default();
    let mut plugins = session(&[&root.0], None);
    plugins.start(&mut api);
    rewrite_wasm(&folder, "hello");
    std::fs::remove_file(folder.join("hello.wasm")).unwrap();
    assert!(!folder.join("hello.wasm").exists());
    assert!(plugins.handle_line("/hello ticks", &mut api));
    assert_eq!(api.posts.last().unwrap(), "hello: 0 ticks");
    plugins.shutdown(&mut api);
}

// OpenAC ReloadCommandStartsAFreshCopyOnTheNextTickAndTheOldCopyLeavesMemory.
#[test]
fn the_reload_command_starts_a_fresh_copy_at_the_next_pump() {
    let root = Root::new("reload-command");
    install(&root.0, "hello");
    let mut api = FakeSession::default();
    let mut plugins = session(&[&root.0], None);
    plugins.start(&mut api);
    plugins.pump(0.045, None, &mut api);

    assert!(plugins.handle_line("/plugin reload openac.hello", &mut api));
    assert_eq!(enabled_count(&api), 1);

    plugins.pump(0.0, None, &mut api);

    assert_eq!(enabled_count(&api), 2);
    let disabled: Vec<String> = api
        .messages("openac.hello")
        .into_iter()
        .filter(|m| m.starts_with("hello disabled"))
        .collect();
    assert_eq!(disabled, ["hello disabled after 3 ticks"]);
    assert_eq!(reloaded(&api), ["Reloaded Hello 0.1.0."]);
    assert!(
        api.messages(ac_plugin::session::MANAGER_ID)
            .contains(&"Reloaded Hello 0.1.0.".to_owned())
    );
    assert_eq!(plugins.loaded_ids(), ["openac.hello"]);
    assert!(plugins.handle_line("/hello ticks", &mut api));
    assert_eq!(api.posts.last().unwrap(), "hello: 0 ticks");
    plugins.shutdown(&mut api);
}

// OpenAC ReloadAllReloadsEveryRunningPlugin.
#[test]
fn reload_all_reloads_every_running_plugin() {
    let root = Root::new("reload-all");
    install(&root.0, "hello");
    install(&root.0, "probe");
    let mut api = FakeSession::default();
    let mut plugins = session(&[&root.0], None);
    plugins.start(&mut api);

    assert!(plugins.handle_line("/plugin reload all", &mut api));
    plugins.pump(0.0, None, &mut api);

    assert_eq!(
        api.posts,
        ["Reloaded Hello 0.1.0.", "Reloaded Probe 0.1.0."]
    );
    assert_eq!(plugins.loaded_ids(), ["openac.hello", "openac.probe"]);
    plugins.shutdown(&mut api);
}

// OpenAC ReloadingAPluginThatIsNotRunningSaysSo.
#[test]
fn reloading_a_plugin_that_is_not_running_says_so() {
    let root = Root::new("reload-absent");
    let mut api = FakeSession::default();
    let mut plugins = session(&[&root.0], None);
    plugins.start(&mut api);

    assert!(plugins.handle_line("/plugin reload openac.absent", &mut api));
    assert!(plugins.handle_line("/plugin reload", &mut api));
    plugins.pump(0.0, None, &mut api);

    assert_eq!(
        api.posts,
        [
            "Usage: /plugin reload <plugin id>, or /plugin reload all",
            "No plugin 'openac.absent' is running in this client.",
        ]
    );
}

// OpenAC ANewCopyThisClientCannotRunLeavesTheRunningCopyAlone (both cases).
#[test]
fn a_new_copy_this_client_cannot_run_leaves_the_running_copy_alone() {
    for (from, to, expected) in [
        (
            r#""minHostVersion": "0.1.0""#,
            r#""minHostVersion": "9.0.0""#,
            "requires openAC 9.0.0 or newer (this is 0.1.20)",
        ),
        (
            r#""apiVersion": "1.6""#,
            r#""apiVersion": "99.0""#,
            "needs plugin API version 99.0",
        ),
    ] {
        let root = Root::new("reload-refused");
        let folder = install(&root.0, "hello");
        let mut api = FakeSession::default();
        let mut options = options(&[&root.0], None);
        options.host_version = HostVersion::new(0, 1, 20);
        let mut plugins = PluginSession::new(engine(), options);
        plugins.start(&mut api);
        edit_manifest(&folder, r#""version": "0.1.0""#, r#""version": "2.0.0""#);
        edit_manifest(&folder, from, to);

        plugins.request_reload("openac.hello");
        plugins.pump(0.0, None, &mut api);

        assert_eq!(api.posts.len(), 1, "{:?}", api.posts);
        let line = &api.posts[0];
        assert!(line.contains("Hello 2.0.0 was not loaded"), "{line}");
        assert!(line.contains(expected), "{line}");
        assert!(line.contains("The running copy stays."), "{line}");
        assert!(
            !api.messages("openac.hello")
                .iter()
                .any(|m| m.starts_with("hello disabled"))
        );
        assert_eq!(enabled_count(&api), 1);
        assert_eq!(plugins.loaded_ids(), ["openac.hello"]);
        plugins.shutdown(&mut api);
    }
}

#[test]
fn a_new_copy_naming_a_different_plugin_leaves_the_running_copy_alone() {
    let root = Root::new("reload-other-id");
    let folder = install(&root.0, "hello");
    let mut api = FakeSession::default();
    let mut plugins = session(&[&root.0], None);
    plugins.start(&mut api);
    edit_manifest(&folder, "\"openac.hello\"", "\"openac.other\"");

    plugins.request_reload("openac.hello");
    plugins.pump(0.0, None, &mut api);

    assert_eq!(
        api.posts,
        [
            "Hello 0.1.0 was not loaded: its new plugin.json names a different plugin \
             ('openac.other'); restart the client to load it. The running copy stays."
        ]
    );
    assert_eq!(plugins.loaded_ids(), ["openac.hello"]);
    plugins.shutdown(&mut api);
}

// OpenAC APluginStartedWhileTheCharacterIsInTheWorldHearsTheLogin.
#[test]
fn a_plugin_started_while_the_character_is_in_the_world_hears_the_login() {
    let root = Root::new("reload-login");
    install(&root.0, "probe");
    let mut api = FakeSession::default();
    let mut plugins = session(&[&root.0], None);
    plugins.start(&mut api);
    let logins = |api: &FakeSession| {
        api.messages("openac.probe")
            .iter()
            .filter(|m| m.starts_with("probe event: login complete"))
            .count()
    };
    assert_eq!(logins(&api), 0);

    api.in_world = true;
    plugins.request_reload("openac.probe");
    plugins.pump(0.0, None, &mut api);

    assert_eq!(logins(&api), 1);
    plugins.shutdown(&mut api);
}

// OpenAC ChangingTheEntryAssemblyReloadsThePluginOnceItsFolderIsQuiet.
#[test]
fn changing_the_code_reloads_the_plugin_once_its_folder_is_quiet() {
    let root = Root::new("reload-quiet");
    let folder = install(&root.0, "hello");
    let mut api = FakeSession::default();
    let mut plugins = session(&[&root.0], None);
    plugins.start(&mut api);

    rewrite_wasm(&folder, "hello");
    plugins.pump(0.0, None, &mut api);
    assert!(plugins.has_pending_file_change("openac.hello"));
    plugins.pump(0.5, None, &mut api);
    assert!(api.posts.is_empty());

    plugins.pump(0.5, None, &mut api);
    assert_eq!(api.posts, ["Reloaded Hello 0.1.0."]);
    assert!(!plugins.has_pending_file_change("openac.hello"));
    plugins.pump(2.0, None, &mut api);
    assert_eq!(api.posts, ["Reloaded Hello 0.1.0."]);
    plugins.shutdown(&mut api);
}

// OpenAC WritingThePlayersFilesNeitherMarksNorHoldsBackAReload.
#[test]
fn writing_the_players_files_neither_marks_nor_holds_back_a_reload() {
    let root = Root::new("reload-players");
    let folder = install(&root.0, "hello");
    let mut api = FakeSession::default();
    let mut plugins = session(&[&root.0], None);
    plugins.start(&mut api);

    let files = folder.join(ac_host::paths::PLUGIN_FILES_FOLDER_NAME);
    std::fs::create_dir_all(&files).unwrap();
    std::fs::write(files.join("plugin.json"), "{}").unwrap();
    std::fs::write(files.join("hello.wasm"), "not the plugin").unwrap();
    std::fs::write(folder.join("plugin.log"), "a line").unwrap();
    plugins.pump(0.3, None, &mut api);
    assert!(!plugins.has_pending_file_change("openac.hello"));

    edit_manifest(&folder, r#""version": "0.1.0""#, r#""version": "0.1.1""#);
    plugins.pump(0.3, None, &mut api);
    assert!(plugins.has_pending_file_change("openac.hello"));
    plugins.pump(0.6, None, &mut api);
    std::fs::write(files.join("state.json"), "{\"saved\":true}").unwrap();
    std::fs::write(folder.join("plugin.log"), "another line").unwrap();
    plugins.pump(0.5, None, &mut api);

    assert_eq!(api.posts, ["Reloaded Hello 0.1.1."]);
    plugins.shutdown(&mut api);
}

/// Hello's folder made to run misbehave's code, which refuses to start
/// with the setting `refuse` it is given as `openac.hello`.
fn refusing_hello(tag: &str) -> (Root, PathBuf, PluginSession, FakeSession) {
    let root = Root::new(tag);
    let folder = install(&root.0, "hello");
    rewrite_wasm(&folder, "misbehave");
    let declared = std::collections::BTreeMap::from([(
        "openac.hello".to_owned(),
        std::collections::BTreeMap::from([("refuse".to_owned(), "no profile".to_owned())]),
    )]);
    let mut options = options(&[&root.0], None);
    options.settings = PluginSessionSettings::from_map(&declared);
    let plugins = PluginSession::new(engine(), options);
    (root, folder, plugins, FakeSession::default())
}

// OpenAC ANewCopyThatFailsToStartIsOffUntilTheNextReload.
#[test]
fn a_new_copy_that_fails_to_start_is_off_until_the_next_reload() {
    let (_root, folder, mut plugins, mut api) = refusing_hello("reload-fails");
    plugins.start(&mut api);
    assert_eq!(plugins.loaded_ids(), ["openac.hello"]);

    edit_manifest(&folder, "\"hello.wasm\"", "\"misbehave.wasm\"");
    plugins.request_reload("openac.hello");
    plugins.pump(0.0, None, &mut api);
    assert_eq!(api.posts.len(), 1, "{:?}", api.posts);
    assert_eq!(
        api.posts[0],
        "Hello 0.1.0 failed to start (no profile). It is off until its files change again or \
         /plugin reload openac.hello."
    );
    assert!(plugins.loaded_ids().is_empty());
    assert_eq!(
        failed(&plugins.take_reports(), "openac.hello").as_deref(),
        Some("no profile")
    );

    edit_manifest(&folder, "\"misbehave.wasm\"", "\"hello.wasm\"");
    plugins.request_reload("openac.hello");
    plugins.pump(0.0, None, &mut api);
    assert_eq!(api.posts.last().unwrap(), "Reloaded Hello 0.1.0.");
    assert_eq!(plugins.loaded_ids(), ["openac.hello"]);
    plugins.shutdown(&mut api);
}

// OpenAC ANewCopyThatCannotBeLoadedLeavesTheRunningCopy.
#[test]
fn a_new_copy_that_cannot_be_loaded_leaves_the_running_copy() {
    let root = Root::new("reload-unloadable");
    let folder = install(&root.0, "hello");
    let mut api = FakeSession::default();
    let mut plugins = session(&[&root.0], None);
    plugins.start(&mut api);

    edit_manifest(&folder, r#""version": "0.1.0""#, r#""version": "2.0.0""#);
    edit_manifest(&folder, "\"hello.wasm\"", "\"missing.wasm\"");
    plugins.request_reload("openac.hello");
    plugins.pump(0.0, None, &mut api);
    assert_eq!(api.posts.len(), 1, "{:?}", api.posts);
    assert!(
        api.posts[0].contains("could not be loaded"),
        "{}",
        api.posts[0]
    );
    assert!(api.posts[0].ends_with("The running copy stays."));
    assert_eq!(plugins.loaded_ids(), ["openac.hello"]);

    edit_manifest(&folder, r#""version": "2.0.0""#, r#""version": "2.0.1""#);
    edit_manifest(&folder, "\"missing.wasm\"", "\"hello.wasm\"");
    plugins.request_reload("openac.hello");
    plugins.pump(0.0, None, &mut api);
    assert_eq!(api.posts.last().unwrap(), "Reloaded Hello 2.0.1.");
    assert_eq!(plugins.loaded_ids(), ["openac.hello"]);
    plugins.shutdown(&mut api);
}

// OpenAC APluginThatFailedAtStartupIsTriedAgain.
#[test]
fn a_plugin_that_failed_at_startup_is_tried_again_when_its_files_change() {
    let root = Root::new("reload-startup");
    let folder = install(&root.0, "hello");
    edit_manifest(&folder, "\"hello.wasm\"", "\"missing.wasm\"");
    let mut api = FakeSession::default();
    let mut plugins = session(&[&root.0], None);
    plugins.start(&mut api);
    assert!(plugins.loaded_ids().is_empty());

    edit_manifest(&folder, "\"missing.wasm\"", "\"hello.wasm\"");
    plugins.pump(0.0, None, &mut api);
    assert!(plugins.has_pending_file_change("openac.hello"));
    plugins.pump(1.0, None, &mut api);
    assert_eq!(api.posts, ["Reloaded Hello 0.1.0."]);
    assert_eq!(plugins.loaded_ids(), ["openac.hello"]);
    plugins.shutdown(&mut api);
}

// OpenAC APluginWhoseManifestWasUnreadableAtStartupIsTriedAgainByName.
#[test]
fn a_plugin_whose_manifest_was_unreadable_at_startup_is_tried_again_by_name() {
    let root = Root::new("reload-unreadable");
    let folder = install(&root.0, "hello");
    let manifest = std::fs::read_to_string(folder.join("plugin.json")).unwrap();
    std::fs::write(folder.join("plugin.json"), "{ not json").unwrap();
    let mut api = FakeSession::default();
    let mut plugins = session(&[&root.0], Some(&["hello"]));
    plugins.start(&mut api);
    assert!(plugins.loaded_ids().is_empty());

    std::fs::write(
        folder.join("plugin.json"),
        manifest.replace("\"openac.hello\"", "\"hello\""),
    )
    .unwrap();
    assert!(plugins.handle_line("/plugin reload hello", &mut api));
    plugins.pump(0.0, None, &mut api);
    assert_eq!(api.posts, ["Reloaded Hello 0.1.0."]);
    plugins.shutdown(&mut api);
}

/// A plugin stopped by a fault is tried again when its code is
/// rebuilt, as a plugin that failed to start is.
#[test]
fn a_plugin_stopped_by_a_fault_is_tried_again_when_its_code_changes() {
    let (root, mut plugins, mut api) = misbehave_session("reload-fault", &[]);
    assert!(plugins.handle_line("/crash panic", &mut api));
    assert_eq!(plugins.loaded_ids(), ["openac.hello"]);

    rewrite_wasm(&root.0.join("misbehave"), "misbehave");
    plugins.pump(0.0, None, &mut api);
    plugins.pump(1.0, None, &mut api);
    assert_eq!(api.posts.last().unwrap(), "Reloaded Misbehave 0.1.0.");
    assert_eq!(plugins.loaded_ids(), ["openac.hello", "openac.misbehave"]);
    plugins.shutdown(&mut api);
}

// --- Time limits: slow calls warn, stuck calls stop ---

/// A call over the slow-call time (100 ms) finishes: the plugin keeps
/// running, the log warns (at most once a minute per plugin and entry call)
/// naming the call, its time and its imports, and chat says so once.
#[test]
fn a_slow_call_warns_and_goes_on() {
    let warnings = |api: &FakeSession| -> Vec<String> {
        api.logs
            .iter()
            .filter(|(id, level, _)| id == "openac.misbehave" && *level == LogLevel::Warn)
            .map(|(_, _, m)| m.clone())
            .collect()
    };
    let slow_posts = |api: &FakeSession| -> Vec<String> {
        api.posts
            .iter()
            .filter(|p| p.starts_with("[plugin] Misbehave was slow"))
            .cloned()
            .collect()
    };
    let (_root, mut plugins, mut api) = chat_misbehave("slow-warns", engine(), None);
    say_to(&mut plugins, &mut api, "slow");
    let took = plugins
        .last_call_time("openac.misbehave")
        .expect("still loaded");
    assert!(
        took > ac_plugin::SLOW_CALL && took < ac_plugin::CALL_DEADLINE,
        "{took:?}"
    );
    assert!(
        plugins
            .take_reports()
            .iter()
            .all(|r| !matches!(r, PluginReport::Failed { .. }))
    );
    assert_eq!(plugins.loaded_ids(), ["openac.misbehave"]);
    let logged = warnings(&api);
    assert_eq!(logged.len(), 1, "{logged:?}");
    let expected = format!(
        "plugin 'openac.misbehave' was slow in event chat line: {} ms (it keeps running; it is \
         stopped past 10 s); last imports: {SLOW_IMPORTS}",
        took.as_millis()
    );
    assert!(logged[0].starts_with(&expected), "{}", logged[0]);
    assert_eq!(
        slow_posts(&api),
        [format!(
            "[plugin] Misbehave was slow (event chat line, {} ms); it keeps running",
            took.as_millis()
        )]
    );

    // Again within the minute: neither the log nor chat repeats it.
    say_to(&mut plugins, &mut api, "slow");
    assert_eq!(warnings(&api).len(), 1);
    // A minute on, the log names it again; chat said it once.
    plugins.pump(
        ac_plugin::session::SLOW_CALL_LOG_INTERVAL_SECONDS,
        None,
        &mut api,
    );
    say_to(&mut plugins, &mut api, "slow");
    assert_eq!(warnings(&api).len(), 2);
    assert_eq!(slow_posts(&api).len(), 1);
    assert_eq!(plugins.loaded_ids(), ["openac.misbehave"]);
}

// --- Whose time an answer is: the plugin's that gave it ---

/// Two plugins of the suite on `engine`: `openac.suite` answers, and
/// `openac.asker` (the same module under another id) asks; the client's
/// clock runs (the suite works by it).
fn suite_pair(tag: &str, engine: Arc<PluginEngine>) -> (Root, PluginSession, FakeSession) {
    let root = Root::new(tag);
    let suite = install(&root.0, "suite");
    let asker = root.0.join("asker");
    std::fs::create_dir_all(&asker).unwrap();
    for file in ["plugin.json", "suite.wasm"] {
        std::fs::copy(suite.join(file), asker.join(file)).unwrap();
    }
    edit_manifest(&asker, "\"openac.suite\"", "\"openac.asker\"");
    edit_manifest(&asker, "\"Suite\"", "\"Asker\"");
    let mut plugins = PluginSession::new(
        engine,
        options(&[&root.0], Some(&["openac.suite", "openac.asker"])),
    );
    let mut api = FakeSession {
        clock: true,
        ..FakeSession::default()
    };
    plugins.start(&mut api);
    assert_eq!(plugins.loaded_ids(), ["openac.suite", "openac.asker"]);
    plugins.take_reports();
    (root, plugins, api)
}

/// A slow answer warns the plugin that gave it, in its callback, and
/// never the plugin that asked: the asking call's clock stops while the
/// other plugin answers.
#[test]
fn a_slow_answer_warns_the_plugin_that_gave_it_not_the_one_that_asked() {
    let (_root, mut plugins, mut api) = suite_pair("slow-answer", engine());
    assert!(plugins.handle_line("/asker ask openac.suite/rules work:150", &mut api));
    assert_eq!(
        api.messages("openac.asker").last().unwrap(),
        "asker ask: Keep"
    );
    let asked = plugins.last_call_time("openac.asker").unwrap();
    assert!(asked < ac_plugin::SLOW_CALL, "{asked:?}");
    let answered = plugins.last_call_time("openac.suite").unwrap();
    assert!(answered > ac_plugin::SLOW_CALL, "{answered:?}");
    let warned: Vec<&str> = api
        .logs
        .iter()
        .filter(|(_, level, _)| *level == LogLevel::Warn)
        .map(|(id, _, _)| id.as_str())
        .collect();
    assert_eq!(warned, ["openac.suite"]);
    assert!(
        api.messages("openac.suite")
            .last()
            .unwrap()
            .starts_with("plugin 'openac.suite' was slow in callback loot classify: "),
        "{:?}",
        api.logs
    );
    let slow: Vec<&String> = api
        .posts
        .iter()
        .filter(|p| p.contains("was slow"))
        .collect();
    assert_eq!(slow.len(), 1, "{slow:?}");
    assert!(
        slow[0].starts_with("[plugin] Suite was slow (callback loot classify, "),
        "{slow:?}"
    );
    assert_eq!(plugins.loaded_ids(), ["openac.suite", "openac.asker"]);
}

/// A stuck answer is stopped at the answering plugin's own deadline and
/// unloads that plugin; the plugin that asked hears no answer and runs on.
#[test]
fn a_stuck_answer_stops_the_plugin_that_gave_it_not_the_one_that_asked() {
    let (_root, mut plugins, mut api) = suite_pair("stuck-answer", short_engine());
    assert!(plugins.handle_line("/asker ask openac.suite/rules loop", &mut api));
    assert_eq!(
        api.messages("openac.asker").last().unwrap(),
        "asker ask: none"
    );
    let reports = plugins.take_reports();
    assert_eq!(
        failed(&reports, "openac.suite").as_deref(),
        Some(
            "plugin 'openac.suite' stopped: ran for more than 1 s in one call (it looks stuck) \
             in callback loot classify; last imports: none"
        )
    );
    assert_eq!(failed(&reports, "openac.asker"), None);
    assert_eq!(plugins.loaded_ids(), ["openac.asker"]);
}

/// The time another plugin takes to answer is left out of the asking
/// call's deadline: the asker works 800 ms of its own around an answer
/// that takes 700 ms, longer together than the 1 s deadline, and neither
/// is stopped.
#[test]
fn an_answer_does_not_count_against_the_deadline_of_the_call_that_asked() {
    let (_root, mut plugins, mut api) = suite_pair("lent-time", short_engine());
    let started = std::time::Instant::now();
    assert!(plugins.handle_line("/asker ask openac.suite/rules work:700 400", &mut api));
    assert!(started.elapsed() > TEST_DEADLINE, "{:?}", started.elapsed());
    assert!(
        api.messages("openac.asker")
            .contains(&"asker ask: Keep".to_owned())
    );
    assert!(
        plugins
            .take_reports()
            .iter()
            .all(|r| !matches!(r, PluginReport::Failed { .. }))
    );
    assert_eq!(plugins.loaded_ids(), ["openac.suite", "openac.asker"]);
    let asked = plugins.last_call_time("openac.asker").unwrap();
    assert!(
        asked >= std::time::Duration::from_millis(800) && asked < TEST_DEADLINE,
        "{asked:?}"
    );
}

// --- The compile cache (`ac_plugin::ModuleCache`) ---

/// The first load compiles and writes the cache file; the next engine
/// (the next start) loads the machine code from it and the plugin runs.
#[test]
fn a_compiled_plugin_is_kept_on_disk_and_loaded_from_there() {
    let root = Root::new("module-cache-hit");
    install(&root.0, "hello");
    let cache = root.0.join("cache");
    let wasm = root.0.join("hello").join("hello.wasm");
    let bytes = std::fs::read(&wasm).unwrap();

    let first = PluginEngine::with_module_cache(cache.clone());
    let file = first.module_cache().unwrap().path_of(&bytes);
    assert!(!file.exists());
    first.module(&wasm).unwrap();
    assert_eq!(first.module_cache_hits(), 0, "a miss compiles");
    assert!(file.exists(), "and writes {}", file.display());

    let second = Arc::new(PluginEngine::with_module_cache(cache));
    let mut plugins = PluginSession::new(Arc::clone(&second), options(&[&root.0], None));
    let mut api = FakeSession::default();
    plugins.start(&mut api);
    assert_eq!(second.module_cache_hits(), 1, "a hit loads");
    assert_eq!(plugins.loaded_ids(), ["openac.hello"]);
    plugins.pump(0.015, None, &mut api);
    assert!(plugins.handle_line("/hello ticks", &mut api));
    assert_eq!(api.posts.last().unwrap(), "hello: 1 ticks");
    plugins.shutdown(&mut api);
}

/// A damaged cache file, or one holding another module, is ignored: the
/// plugin is compiled again and the file rewritten.
#[test]
fn a_damaged_cache_file_is_ignored_and_rebuilt() {
    let root = Root::new("module-cache-damaged");
    install(&root.0, "hello");
    install(&root.0, "probe");
    let cache = root.0.join("cache");
    let hello = root.0.join("hello").join("hello.wasm");
    let probe = root.0.join("probe").join("probe.wasm");
    let engine = PluginEngine::with_module_cache(cache.clone());
    engine.module(&hello).unwrap();
    engine.module(&probe).unwrap();
    let disk = engine.module_cache().unwrap();
    let hello_file = disk.path_of(&std::fs::read(&hello).unwrap());
    let probe_file = disk.path_of(&std::fs::read(&probe).unwrap());

    // One byte of the machine code changed.
    let mut damaged = std::fs::read(&hello_file).unwrap();
    let last = damaged.len() - 1;
    damaged[last] ^= 0xFF;
    std::fs::write(&hello_file, &damaged).unwrap();
    let again = PluginEngine::with_module_cache(cache.clone());
    again.module(&hello).unwrap();
    assert_eq!(
        again.module_cache_hits(),
        0,
        "the damaged file is not loaded"
    );
    assert_ne!(
        std::fs::read(&hello_file).unwrap(),
        damaged,
        "it is rewritten"
    );
    let healed = PluginEngine::with_module_cache(cache.clone());
    healed.module(&hello).unwrap();
    assert_eq!(healed.module_cache_hits(), 1);

    // Another module's file under this module's name.
    std::fs::copy(&probe_file, &hello_file).unwrap();
    let swapped = PluginEngine::with_module_cache(cache.clone());
    let module = swapped.module(&hello).unwrap();
    assert_eq!(
        swapped.module_cache_hits(),
        0,
        "a key mismatch is not loaded"
    );
    assert_eq!(
        module.imports().len(),
        PluginEngine::new().module(&hello).unwrap().imports().len(),
        "hello's own module, not probe's"
    );

    // Cut short after its first bytes.
    std::fs::write(&hello_file, b"OACPMOD1").unwrap();
    let truncated = PluginEngine::with_module_cache(cache);
    truncated.module(&hello).unwrap();
    assert_eq!(truncated.module_cache_hits(), 0);
}
