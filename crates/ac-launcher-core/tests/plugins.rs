//! Plugin manifest install rules, compatibility, content policy, `.sha256`
//! asset, icon, curated list, installed records, direct installs,
//! inventory, release client/feed/resolver and installer. Ported from
//! OpenAC `tests/AcDream.Launcher.Core.Tests/Plugins/` (one section per
//! file) and `PluginsNeverExecuteBoundaryTests.cs`, with the WebAssembly
//! plugin manifest (`entry`, `"MAJOR.MINOR"`
//! `apiVersion`, `permissions`; the parser and its own rules are tested in
//! `ac-plugin`) and the `wasm-v` release tags. HTTP fixtures are an in-memory
//! transport here.

mod common;

use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};
use std::time::SystemTime;

use ac_launcher_core::cancel::CancelToken;
use ac_launcher_core::integrity::sha256_hex;
use ac_launcher_core::plugins::{
    ApiVersion, CLIENT_NOT_INSTALLED, HostVersion, InstallRules, InstalledPluginInfo,
    InstalledPluginRecord, InstalledPluginRecordStore, InstalledPluginSource,
    PERMISSION_VOCABULARY_REFUSAL, PERMISSIONS_CHANGED_REFUSAL, PLUGIN_INDEX_ASSET,
    PLUGIN_INDEX_TAG, PendingPluginInstall, Permission, PermissionDeclaration, PluginCatalog,
    PluginIndex, PluginInstallSource, PluginInstaller, PluginInventory, PluginManifest,
    PluginReleaseCandidates, PluginReleaseChannel, PluginReleaseClient, PluginReleaseFetchResult,
    PluginReleaseResolution, PluginReleaseResolveResult, PluginReleaseResolver, PluginSha256File,
    SESSION_LEASE_REFUSAL, THIS_CLIENT, describe, direct_install_refusal,
    evaluate_version_compatibility, find_block_reason, github, highest_prerelease, highest_stable,
    is_built_for_csharp, plugin_release_tag, release_tag, validate_icon, validate_plugin_content,
    version_only_reason, without_client_version,
};
use ac_launcher_core::updates::{
    ClientVersionResolution, ClientVersionState, ExtractedFileRecord, LauncherVersion, UpdateError,
    UpdateSessionBarrier,
};
use ac_launcher_core::url::WebUrl;
use common::fixtures::{Compression, FakeServer, Route, create_zip};
use common::{TempDir, png};

fn version(text: &str) -> LauncherVersion {
    LauncherVersion::try_parse(text).unwrap()
}

fn none() -> CancelToken {
    CancelToken::none()
}

// --- LauncherPluginManifestTests (install rules) ----------------------------------

const VALID_JSON: &str = r#"
{
  "id": "edwards.hello",
  "displayName": "Hello",
  "version": "0.1.0",
  "entry": "hello.wasm",
  "apiVersion": "1.0",
  "minHostVersion": "0.1.7"
}
"#;

fn manifest_with(fields: &str) -> String {
    format!(
        r#"{{
  "id": "edwards.hello",
  "displayName": "Hello",
  "version": "0.1.0",
  "entry": "hello.wasm",
  "apiVersion": "1.0"{fields}
}}"#
    )
}

fn parse(text: &str) -> PluginManifest {
    PluginManifest::parse(text).unwrap()
}

// OpenAC LauncherPluginManifestTests.ParsesAllFields (the WebAssembly fields).
#[test]
fn parses_all_fields() {
    let manifest = parse(VALID_JSON);
    assert_eq!(manifest.id, "edwards.hello");
    assert_eq!(manifest.version, "0.1.0");
    assert_eq!(manifest.entry, "hello.wasm");
    assert_eq!(manifest.api_version, ApiVersion::new(1, 0));
    assert_eq!(manifest.min_host_version, Some(HostVersion::new(0, 1, 7)));
    manifest.validate_for_install().unwrap();
}

// OpenAC LauncherPluginManifestTests.UnknownFieldsAreIgnored.
#[test]
fn unknown_fields_are_ignored() {
    let manifest = parse(&manifest_with(r#", "somethingNew": "value""#));
    assert_eq!(manifest.id, "edwards.hello");
}

#[test]
fn an_openac_csharp_manifest_is_refused_as_built_for_openac() {
    let error = PluginManifest::parse(
        r#"{ "id": "edwards.hello", "displayName": "Hello", "version": "0.1.0",
            "entryDll": "Hello.dll", "apiVersion": 1, "minHostVersion": "0.1.7",
            "hosts": ["graphical", "headless"] }"#,
    )
    .unwrap_err();
    assert!(error.0.starts_with("built for OpenAC (C#)"), "{error}");
    assert!(is_built_for_csharp(&error));
}

// OpenAC LauncherPluginManifestTests.ValidateForInstallRejectsAnIdWithATrailingNewline.
#[test]
fn validate_for_install_rejects_an_id_with_a_trailing_newline() {
    let manifest = parse(&VALID_JSON.replace("\"edwards.hello\"", "\"edwards.hello\\n\""));
    assert!(manifest.validate_for_install().is_err());
}

// OpenAC LauncherPluginManifestTests.ValidateForInstallRejectsABadId.
#[test]
fn validate_for_install_rejects_a_bad_id() {
    for id in [
        "hello",
        "Edwards.Hello",
        "edwards.",
        ".hello",
        "edwards..hello",
    ] {
        let manifest = parse(&VALID_JSON.replace("\"edwards.hello\"", &format!("\"{id}\"")));
        assert!(manifest.validate_for_install().is_err(), "{id}");
    }
}

// OpenAC LauncherPluginManifestTests.ValidateForInstallRejectsANonSemVerVersion.
#[test]
fn validate_for_install_rejects_a_non_sem_ver_version() {
    for value in ["1.0", "v1.0.0", "1.0.0.0"] {
        let manifest = parse(&VALID_JSON.replace("\"0.1.0\"", &format!("\"{value}\"")));
        assert!(manifest.validate_for_install().is_err(), "{value}");
    }
}

// OpenAC LauncherPluginManifestTests.ValidateForInstallRequiresMinHostVersion.
#[test]
fn validate_for_install_requires_min_host_version() {
    let manifest = parse(&manifest_with(""));
    assert_eq!(
        manifest.validate_for_install().unwrap_err().0,
        "minHostVersion is required for launcher installs"
    );
}

#[test]
fn validate_for_install_needs_no_hosts_list() {
    // Every plugin runs in every session, windowed or not; `hosts` is retired.
    parse(&manifest_with(r#", "minHostVersion": "0.1.7""#))
        .validate_for_install()
        .unwrap();
}

// OpenAC LauncherPluginManifestTests.ValidateForInstallRejectsAnUnsupportedApiVersion.
#[test]
fn validate_for_install_rejects_an_unsupported_api_version() {
    let manifest = parse(&VALID_JSON.replace("\"apiVersion\": \"1.0\"", "\"apiVersion\": \"2.0\""));
    assert_eq!(
        manifest.validate_for_install().unwrap_err().0,
        "apiVersion 2.0 is not supported by this launcher (1.0 to 1.6)"
    );
}

// OpenAC LauncherPluginManifestTests.RejectsMinHostVersionAboveMaxHostVersion.
#[test]
fn rejects_min_host_version_above_max_host_version() {
    assert!(
        PluginManifest::parse(&manifest_with(
            r#", "minHostVersion": "0.2.0", "maxHostVersion": "0.1.0""#
        ))
        .is_err()
    );
}

// OpenAC LauncherPluginManifestTests.MatchesTagRequiresAVLeadingVersion, with
// this client's `wasm-v` tags.
#[test]
fn matches_tag_requires_a_wasm_v_leading_version() {
    let manifest = parse(VALID_JSON);
    assert_eq!(release_tag("0.1.0"), "wasm-v0.1.0");
    assert!(manifest.matches_tag("wasm-v0.1.0"));
    assert!(!manifest.matches_tag("wasm-v0.1.1"));
    assert!(!manifest.matches_tag("v0.1.0"));
    assert!(!manifest.matches_tag("0.1.0"));
}

// A repository that publishes several plugins tags each one
// `wasm-<plugin id>-v<version>`; the old `wasm-v` form still matches.
#[test]
fn matches_tag_accepts_the_plugin_id_form_for_its_own_id_only() {
    let manifest = parse(VALID_JSON);
    assert_eq!(
        plugin_release_tag("edwards.hello", "0.1.0"),
        "wasm-edwards.hello-v0.1.0"
    );
    assert!(manifest.matches_tag("wasm-edwards.hello-v0.1.0"));
    assert!(!manifest.matches_tag("wasm-edwards.hello-v0.1.1"));
    assert!(!manifest.matches_tag("wasm-edwards.other-v0.1.0"));
    assert!(!manifest.matches_tag("wasm-edwards.hell-v0.1.0"));
    assert!(!manifest.matches_tag("wasm-edwards.hello-0.1.0"));
}

// OpenAC LauncherPluginManifestTests.AbsentCapabilitiesParseToEmpty.
#[test]
fn absent_permissions_parse_to_empty() {
    let manifest = parse(VALID_JSON);
    assert_eq!(manifest.permissions_version, 0);
    assert!(manifest.permissions.is_empty());
    manifest.validate_for_install().unwrap();
}

const PERMITTED: &str = r#", "minHostVersion": "0.1.7", "permissionsVersion": "#;

fn declaration(permission: Permission, note: &str) -> PermissionDeclaration {
    PermissionDeclaration {
        permission,
        note: note.into(),
        hosts: Vec::new(),
    }
}

// OpenAC LauncherPluginManifestTests.ValidCapabilitiesRoundTripWithNotesIntact.
#[test]
fn valid_permissions_round_trip_with_notes_intact() {
    let manifest = parse(&manifest_with(&format!(
        r#"{PERMITTED}1, "permissions": [
            {{ "name": "network", "note": "Sends buff usage counts to my server." }},
            {{ "name": "chat", "note": "Reads chat to detect buff requests." }}
        ]"#
    )));
    assert_eq!(manifest.permissions_version, 1);
    assert_eq!(
        manifest.permissions,
        [
            declaration(Permission::Network, "Sends buff usage counts to my server."),
            declaration(Permission::Chat, "Reads chat to detect buff requests."),
        ]
    );
    manifest.validate_for_install().unwrap();
}

// OpenAC LauncherPluginManifestTests.ParseKeepsAnUnrecognizedCapability...: the
// vocabulary is enforced now, so an unknown name is refused when read.
#[test]
fn an_unrecognized_permission_is_refused() {
    let error = PluginManifest::parse(&manifest_with(&format!(
        r#"{PERMITTED}1, "permissions": [
            {{ "name": "teleportsPlayers", "note": "Moves the player around instantly." }}
        ]"#
    )))
    .unwrap_err();
    assert_eq!(error.0, "unknown permission: teleportsPlayers");
}

// OpenAC LauncherPluginManifestTests.ValidateForInstallRefusesACapabilitiesVersionAboveCurrent...
#[test]
fn a_permissions_version_above_current_refuses_on_the_version_rather_than_the_entry_rules() {
    let error = PluginManifest::parse(&manifest_with(&format!(
        r#"{PERMITTED}99, "permissions": [
            {{ "name": "notARealPermission", "note": "https://example.invalid/why" }}
        ]"#
    )))
    .unwrap_err();
    assert!(
        ac_launcher_core::plugins::is_newer_permission_vocabulary(&error),
        "{error}"
    );
}

fn permissions(entries: &str) -> String {
    manifest_with(&format!(
        r#", "permissionsVersion": 1, "permissions": {entries}"#
    ))
}

// OpenAC LauncherPluginManifestTests.CapabilityEntryRulesAreEnforced.
#[test]
fn permission_entry_rules_are_enforced() {
    for entries in [
        // A duplicate name (ignoring case).
        r#"[{ "name": "network", "note": "Sends usage counts." }, { "name": "Network", "note": "Also." }]"#,
        // A blank note.
        r#"[{ "name": "network", "note": "   " }]"#,
        // A newline in the note.
        r#"[{ "name": "network", "note": "Sends data.\nAlso this." }]"#,
        // A bidirectional override.
        "[{ \"name\": \"network\", \"note\": \"Sends data \u{202e}gnihton.tsod.\" }]",
        // A link.
        r#"[{ "name": "network", "note": "See https://example.com for details." }]"#,
    ] {
        assert!(
            PluginManifest::parse(&permissions(entries)).is_err(),
            "{entries}"
        );
    }
    let long = format!(
        r#"[{{ "name": "network", "note": "{}" }}]"#,
        "a".repeat(121)
    );
    assert!(PluginManifest::parse(&permissions(&long)).is_err());
}

// OpenAC LauncherPluginManifestTests.RejectsCapabilitiesWithNoCapabilitiesVersion.
#[test]
fn rejects_permissions_with_no_permissions_version() {
    assert!(
        PluginManifest::parse(&manifest_with(
            r#", "permissions": [{ "name": "network", "note": "Sends usage counts." }]"#
        ))
        .is_err()
    );
}

#[test]
fn a_repeated_member_is_refused_ignoring_case() {
    assert!(PluginManifest::parse(&manifest_with(r#", "ID": "other.id""#)).is_err());
}

// --- LauncherPluginCompatibilityTests -------------------------------------------

fn compat_manifest(min: &str, max: Option<&str>, skip: &[&str]) -> PluginManifest {
    let mut fields = format!(r#", "minHostVersion": "{min}""#);
    if let Some(max) = max {
        fields.push_str(&format!(r#", "maxHostVersion": "{max}""#));
    }
    if !skip.is_empty() {
        let list: Vec<String> = skip.iter().map(|v| format!("\"{v}\"")).collect();
        fields.push_str(&format!(r#", "skipHostVersions": [{}]"#, list.join(",")));
    }
    parse(&manifest_with(&fields))
}

// OpenAC LauncherPluginCompatibilityTests (the version reasons; the host
// reasons went with `hosts`).
#[test]
fn compatibility_reasons() {
    let client = version("0.1.7");
    assert_eq!(
        version_only_reason(&compat_manifest("0.1.0", None, &[]), Some(&client)),
        None
    );
    assert_eq!(
        version_only_reason(&compat_manifest("0.1.0", None, &[]), None).as_deref(),
        Some(CLIENT_NOT_INSTALLED)
    );
    assert_eq!(
        version_only_reason(&compat_manifest("0.2.0", None, &[]), Some(&client)).as_deref(),
        Some("requires OpenAC 0.2.0 or newer (this is 0.1.7)")
    );
    assert_eq!(
        version_only_reason(&compat_manifest("0.1.0", Some("0.1.0"), &[]), Some(&client))
            .as_deref(),
        Some("supports OpenAC up to 0.1.0 (this is 0.1.7)")
    );
    assert_eq!(
        version_only_reason(&compat_manifest("0.1.0", None, &["0.1.7"]), Some(&client)).as_deref(),
        Some("is marked broken on OpenAC 0.1.7")
    );
    // Build metadata and a pre-release are not part of the compared version.
    assert_eq!(
        version_only_reason(
            &compat_manifest("0.1.0", None, &["0.1.7"]),
            Some(&version("0.1.7+3a71d75"))
        )
        .as_deref(),
        Some("is marked broken on OpenAC 0.1.7")
    );
}

// OpenAC LauncherPluginCompatibilityTests (descriptions).
#[test]
fn compatibility_descriptions() {
    let client = version("0.1.7");
    let compatible = describe(&compat_manifest("0.1.0", None, &[]), Some(&client));
    assert_eq!(compatible.text, "Compatible with OpenAC 0.1.7");
    assert!(!compatible.is_warning);
    let missing = describe(&compat_manifest("0.1.0", None, &[]), None);
    assert_eq!(missing.text, "Client not installed");
    assert!(!missing.is_warning);
    let too_old = describe(&compat_manifest("0.2.0", None, &[]), Some(&client));
    assert_eq!(
        too_old.text,
        "requires OpenAC 0.2.0 or newer (this is 0.1.7)"
    );
    assert!(too_old.is_warning);
}

// OpenAC LauncherPluginCompatibilityTests.WithoutClientVersionDropsOnlyTheTrailingClientVersion.
#[test]
fn without_client_version_drops_only_the_trailing_client_version() {
    for (reason, expected) in [
        (
            "requires OpenAC 0.2.0 or newer (this is 0.1.8)",
            "requires OpenAC 0.2.0 or newer",
        ),
        (
            "supports OpenAC up to 0.1.5 (this is 0.1.8)",
            "supports OpenAC up to 0.1.5",
        ),
        (
            "is marked broken on OpenAC 0.1.8",
            "is marked broken on OpenAC 0.1.8",
        ),
        ("Headless only", "Headless only"),
    ] {
        assert_eq!(without_client_version(reason), expected);
    }
}

// --- PluginContentPolicyTests ---------------------------------------------------

fn file(path: &str) -> ExtractedFileRecord {
    ExtractedFileRecord::new(path, "a".repeat(64), 1, 0o644)
}

// OpenAC PluginContentPolicyTests.AcceptsAWellFormedTree, with the WebAssembly
// file types.
#[test]
fn content_policy_accepts_a_well_formed_tree() {
    validate_plugin_content(
        &[
            file("plugin.json"),
            file("hello.wasm"),
            file("README.md"),
            file("LICENSE.txt"),
            file("markup/panel.xml"),
            file("data/table.json"),
            file("fonts/a.ttf"),
            file("fonts/b.otf"),
            file("assets/icon.png"),
            file("icon.png"),
        ],
        "hello.wasm",
    )
    .unwrap();
    // The entry may sit in a folder and be written with `./` or `\`.
    for entry in ["bin/hello.wasm", "./bin/hello.wasm", "bin\\hello.wasm"] {
        validate_plugin_content(&[file("plugin.json"), file("bin/hello.wasm")], entry).unwrap();
    }
}

// OpenAC PluginContentPolicyTests.Refusals: native code of any kind, .NET
// assemblies and JPEGs among it.
#[test]
fn content_policy_refusals() {
    for extra in [
        "payload.exe",
        "lib.dylib",
        "install.sh",
        "hook.js",
        "shortcut.lnk",
        "Makefile",
        "Hello.dll",
        "Hello.pdb",
        "lib.so",
        "ICON.PNG",
        "Icon.png",
        "icon.jpg",
        "assets/photo.jpeg",
    ] {
        assert!(
            validate_plugin_content(
                &[file("plugin.json"), file("hello.wasm"), file(extra)],
                "hello.wasm"
            )
            .is_err(),
            "{extra}"
        );
    }
    assert_eq!(
        validate_plugin_content(&[file("plugin.json")], "hello.wasm")
            .unwrap_err()
            .to_string(),
        "The plugin archive is missing its declared entry module 'hello.wasm'."
    );
    assert!(
        validate_plugin_content(
            &[file("nested/plugin.json"), file("hello.wasm")],
            "hello.wasm"
        )
        .is_err()
    );
}

// --- PluginSha256FileTests ------------------------------------------------------

#[test]
fn sha256_file_rules() {
    let hash = "a".repeat(64);
    let plain = PluginSha256File::parse(&format!("{hash}\n")).unwrap();
    assert_eq!(plain.sha256, hash);
    assert_eq!(plain.file_name, None);
    let named = PluginSha256File::parse(&format!("{hash}  edwards.hello-0.1.0.zip\n")).unwrap();
    assert_eq!(named.file_name.as_deref(), Some("edwards.hello-0.1.0.zip"));
    let binary = PluginSha256File::parse(&format!("{hash} *edwards.hello-0.1.0.zip\n")).unwrap();
    assert_eq!(binary.file_name.as_deref(), Some("edwards.hello-0.1.0.zip"));
    for content in ["", "not-a-hash", "aaaa"] {
        assert!(PluginSha256File::parse(content).is_err(), "{content:?}");
    }
    named.require_matches("edwards.hello-0.1.0.zip").unwrap();
    let wrong = PluginSha256File::parse(&format!("{hash}  wrong-name.zip")).unwrap();
    assert!(wrong.require_matches("edwards.hello-0.1.0.zip").is_err());
    PluginSha256File::parse(&hash)
        .unwrap()
        .require_matches("edwards.hello-0.1.0.zip")
        .unwrap();
}

// --- LauncherPluginIconTests -----------------------------------------------------

#[test]
fn icon_rules() {
    validate_icon(&png::valid()).unwrap();
    for (name, bytes) in [
        ("over maximum", png::over_maximum_bytes()),
        ("no signature", png::no_signature()),
        ("not IHDR", png::first_chunk_wrong_type()),
        ("IHDR length", png::first_chunk_wrong_length()),
        ("dimensions", png::wrong_dimensions()),
        ("length overflow", png::chunk_length_overflow()),
        ("past the end", png::chunk_runs_past_end()),
        ("animated", png::animated_control()),
        ("no IEND", png::missing_iend()),
        ("after IEND", png::trailing_bytes_after_iend()),
        ("inflate bomb", png::inflate_bomb()),
    ] {
        assert!(validate_icon(&bytes).is_err(), "{name}");
    }
}

#[test]
fn icon_header_outside_the_png_specification_is_refused() {
    for (depth, color, compression, filter, interlace) in [
        (255, 6, 0, 0, 0),
        (16, 3, 0, 0, 0),
        (1, 2, 0, 0, 0),
        (8, 5, 0, 0, 0),
        (8, 6, 1, 0, 0),
        (8, 6, 0, 1, 0),
        (8, 6, 0, 0, 2),
    ] {
        assert!(
            validate_icon(&png::with_header(
                depth,
                color,
                16,
                compression,
                filter,
                interlace
            ))
            .is_err(),
            "{depth} {color}"
        );
    }
}

#[test]
fn icon_accepts_each_allowed_depth_at_its_exact_raw_size() {
    for (depth, color, raw) in [(1, 0, 64 * 9), (16, 6, 64 * 513), (8, 3, 64 * 65)] {
        validate_icon(&png::with_header(depth, color, raw, 0, 0, 0)).unwrap();
    }
    validate_icon(&png::with_header(8, 6, 16_504, 0, 0, 1)).unwrap();
    assert!(validate_icon(&png::with_header(8, 6, 16_505, 0, 0, 1)).is_err());
}

// --- PluginCatalogTests -----------------------------------------------------------

const HELLO_ENTRY: &str = r#"{ "id": "edwards.hello", "name": "Hello", "author": "Shane Edwards",
      "description": "Says hello.", "repo": "shaneedwards/openac-plugin-hello" }"#;

fn catalog_json(plugins: &str, blocked: Option<&str>) -> String {
    match blocked {
        Some(blocked) => {
            format!(r#"{{ "schemaVersion": 1, "plugins": [{plugins}], "blocked": [{blocked}] }}"#)
        }
        None => format!(r#"{{ "schemaVersion": 1, "plugins": [{plugins}] }}"#),
    }
}

#[test]
fn catalog_parses_plugins_and_blocked_entries() {
    let catalog = PluginCatalog::parse(&catalog_json(
        HELLO_ENTRY,
        Some(r#"{ "id": "someone.bad", "versions": ["*"], "reason": "Sends chat spam." }"#),
    ))
    .unwrap();
    assert_eq!(catalog.schema_version, 1);
    assert_eq!(catalog.plugins.len(), 1);
    assert_eq!(catalog.plugins[0].id, "edwards.hello");
    assert_eq!(catalog.plugins[0].repo, "shaneedwards/openac-plugin-hello");
    assert_eq!(catalog.blocked[0].id, "someone.bad");
    assert!(catalog.blocked[0].matches("9.9.9"));
    assert!(catalog.is_blocked("Someone.Bad", None));
}

#[test]
fn catalog_blocked_is_optional_and_an_empty_list_parses() {
    assert!(
        PluginCatalog::parse(&catalog_json(HELLO_ENTRY, None))
            .unwrap()
            .blocked
            .is_empty()
    );
    let empty = PluginCatalog::parse(&catalog_json("", Some(""))).unwrap();
    assert!(empty.plugins.is_empty() && empty.blocked.is_empty());
}

#[test]
fn catalog_refusals() {
    let cases = [
        r#"{ "schemaVersion": 1, "blocked": [] }"#.to_owned(),
        format!(r#"{{ "schemaVersion": 1, "plugins": [{HELLO_ENTRY}], "extra": true }}"#),
        format!(r#"{{ "schemaVersion": 1, "schemaVersion": 1, "plugins": [{HELLO_ENTRY}] }}"#),
        format!(r#"{{ "schemaVersion": 1, "SchemaVersion": 1, "plugins": [{HELLO_ENTRY}] }}"#),
        catalog_json(
            r#"{ "id": "edwards.hello", "Id": "edwards.other", "name": "Hello", "author": "A",
                 "description": "D", "repo": "shaneedwards/openac-plugin-hello" }"#,
            None,
        ),
        format!(r#"{{ "schemaVersion": 2, "plugins": [{HELLO_ENTRY}] }}"#),
        catalog_json(
            &format!(
                "{HELLO_ENTRY}, {}",
                HELLO_ENTRY.replace("openac-plugin-hello\"", "openac-plugin-hello-2\"")
            ),
            None,
        ),
        catalog_json(
            &format!(
                "{HELLO_ENTRY}, {}",
                HELLO_ENTRY
                    .replace("edwards.hello", "Edwards.Hello")
                    .replace("openac-plugin-hello\"", "openac-plugin-hello-2\"")
            ),
            None,
        ),
        catalog_json(
            &HELLO_ENTRY.replace("openac-plugin-hello\"", "openac-plugin-hello\\n\""),
            None,
        ),
    ];
    for json in cases {
        assert!(PluginCatalog::parse(&json).is_err(), "{json}");
    }
    for repo in [
        "openac-plugin-hello",
        "shaneedwards/",
        "/openac-plugin-hello",
        "shane edwards/openac-plugin-hello",
        "shaneedwards/openac plugin hello",
        "-shaneedwards/openac-plugin-hello",
        "shane--edwards/openac-plugin-hello",
        "shaneedwards/..",
        "shaneedwards/.",
    ] {
        let json = catalog_json(
            &HELLO_ENTRY.replace("shaneedwards/openac-plugin-hello", repo),
            None,
        );
        assert!(PluginCatalog::parse(&json).is_err(), "{repo}");
    }
}

#[test]
fn catalog_block_matching() {
    let catalog = PluginCatalog::parse(&catalog_json(
        HELLO_ENTRY,
        Some(r#"{ "id": "edwards.hello", "versions": ["0.1.0"], "reason": "test" }"#),
    ))
    .unwrap();
    assert!(catalog.is_blocked("edwards.hello", Some(&version("0.1.0"))));
    assert!(!catalog.is_blocked("edwards.hello", Some(&version("0.1.1"))));
    assert!(!catalog.is_blocked("someone.else", Some(&version("0.1.0"))));
    assert!(catalog.is_blocked("edwards.hello", None));
    let pre = PluginCatalog::parse(&catalog_json(
        HELLO_ENTRY,
        Some(r#"{ "id": "edwards.hello", "versions": ["1.3.0-beta.1"], "reason": "test" }"#),
    ))
    .unwrap();
    assert!(pre.is_blocked("edwards.hello", Some(&version("1.3.0-beta.1"))));
    assert!(!pre.is_blocked("edwards.hello", Some(&version("1.3.0-beta.2"))));
}

fn rust_entry(entry: &str) -> String {
    entry.replacen(" }", r#", "client": "openac-rust" }"#, 1)
}

// The list's `client` field: an entry without it is
// OpenAC's (C#); a Rust port keeps its C# id, listed once per client.
#[test]
fn catalog_entries_name_their_client_and_only_this_clients_are_offered() {
    let catalog = PluginCatalog::parse(&catalog_json(
        &format!("{HELLO_ENTRY}, {}", rust_entry(HELLO_ENTRY)),
        None,
    ))
    .unwrap();
    assert_eq!(catalog.plugins.len(), 2);
    assert_eq!(catalog.plugins[0].client, "openac");
    assert!(!catalog.plugins[0].is_for_this_client());
    assert_eq!(catalog.plugins[1].client, THIS_CLIENT);
    let ours: Vec<_> = catalog.entries_for_this_client().collect();
    assert_eq!(ours.len(), 1);
    assert_eq!(ours[0].id, "edwards.hello");

    let only_csharp = PluginCatalog::parse(&catalog_json(HELLO_ENTRY, None)).unwrap();
    assert_eq!(only_csharp.entries_for_this_client().count(), 0);

    // The same id twice for one client is still a duplicate; a blank client
    // is refused.
    for plugins in [
        format!("{}, {}", rust_entry(HELLO_ENTRY), rust_entry(HELLO_ENTRY)),
        HELLO_ENTRY.replacen(" }", r#", "client": " " }"#, 1),
        HELLO_ENTRY.replacen(" }", r#", "client": null }"#, 1),
    ] {
        assert!(
            PluginCatalog::parse(&catalog_json(&plugins, None)).is_err(),
            "{plugins}"
        );
    }
}

// This client reads a list asset of its
// own in the list repository's latest release, never OpenAC's
// `plugins.json` (whose reader refuses the `client` field).
#[test]
fn the_production_list_is_this_clients_own_asset_not_openacs() {
    assert_eq!(
        PluginCatalog::production_list_uri().to_string(),
        "https://github.com/eriknihlen/openac-plugins/releases/latest/download/plugins-rust.json"
    );
    assert_ne!(
        PluginCatalog::production_list_uri(),
        github::latest_asset("eriknihlen/openac-plugins", "plugins.json").unwrap()
    );
}

#[test]
fn a_block_naming_a_client_covers_only_that_clients_builds() {
    let catalog = PluginCatalog::parse(&catalog_json(
        HELLO_ENTRY,
        Some(
            r#"{ "id": "edwards.hello", "versions": ["*"], "reason": "C# only", "client": "openac" },
               { "id": "edwards.rust", "versions": ["*"], "reason": "Rust only", "client": "openac-rust" },
               { "id": "edwards.both", "versions": ["*"], "reason": "Every client" }"#,
        ),
    ))
    .unwrap();
    assert!(!catalog.is_blocked("edwards.hello", None));
    assert!(!catalog.is_blocked_for_all_versions("edwards.hello"));
    assert_eq!(
        catalog.block_reason("edwards.rust", None),
        Some("Rust only")
    );
    assert_eq!(
        catalog.block_reason("edwards.both", None),
        Some("Every client")
    );
    assert_eq!(
        find_block_reason(Some(&catalog), "edwards.hello", &version("1.0.0")),
        None
    );
    assert_eq!(
        find_block_reason(Some(&catalog), "edwards.both", &version("1.0.0")).as_deref(),
        Some("Every client")
    );
}

// --- InstalledPluginRecordStoreTests -------------------------------------------------

fn record(id: &str, repo: &str, version: Option<&str>) -> InstalledPluginRecord {
    InstalledPluginRecord {
        id: id.into(),
        repo: repo.into(),
        source: PluginInstallSource::Listed,
        version: version.map(str::to_owned),
        tag: version.map(|v| format!("v{v}")),
        zip_sha256: version.map(|_| "a".repeat(64)),
        installed_at: SystemTime::now(),
        pending: None,
        channel: PluginReleaseChannel::Stable,
    }
}

fn records_file(root: &TempDir) -> PathBuf {
    root.join("app").join("plugins-installed.json")
}

#[test]
fn record_store_file_path_is_under_app_under_data_directory() {
    let root = TempDir::new("records");
    let paths = root.path_set();
    let store = InstalledPluginRecordStore::for_paths(&paths);
    assert_eq!(
        store.file_path(),
        paths
            .data_directory
            .join("app")
            .join("plugins-installed.json")
    );
}

#[test]
fn record_store_round_trips_including_a_pending_record() {
    let root = TempDir::new("records");
    let mut store = InstalledPluginRecordStore::new(&records_file(&root));
    assert!(!store.load().unwrap());
    assert!(store.records.is_empty());
    store.records.push(record(
        "edwards.hello",
        "shaneedwards/openac-plugin-hello",
        Some("0.1.0"),
    ));
    let mut pending = record(
        "edwards.pending",
        "shaneedwards/openac-plugin-pending",
        None,
    );
    pending.source = PluginInstallSource::Unlisted;
    pending.pending = Some(PendingPluginInstall {
        version: "0.2.0".into(),
        tag: "wasm-v0.2.0".into(),
        zip_sha256: "b".repeat(64),
    });
    store.records.push(pending);
    store.save().unwrap();

    let mut reloaded = InstalledPluginRecordStore::new(store.file_path());
    reloaded.load().unwrap();
    assert_eq!(reloaded.records.len(), 2);
    let confirmed = reloaded.find("edwards.hello").unwrap();
    assert_eq!(confirmed.version.as_deref(), Some("0.1.0"));
    assert!(confirmed.pending.is_none());
    let pending = reloaded.find("edwards.pending").unwrap();
    assert!(pending.version.is_none());
    assert_eq!(pending.pending.as_ref().unwrap().version, "0.2.0");
    assert_eq!(pending.source, PluginInstallSource::Unlisted);
    assert!(reloaded.find("EDWARDS.HELLO").is_some());
    assert!(reloaded.find("edwards.other").is_none());
}

fn on_disk_record(id: &str, version: &str, extra: &str) -> String {
    format!(
        r#"{{ "id": "{id}", "repo": "shaneedwards/openac-plugin-hello",
              "source": "listed", "version": "{version}", "tag": "v{version}",
              "zipSha256": "{}",
              "installedAt": "2026-01-01T00:00:00Z"{extra}, "pending": null }}"#,
        "a".repeat(64)
    )
}

fn write_records(root: &TempDir, plugins: &[String]) -> PathBuf {
    let path = records_file(root);
    std::fs::create_dir_all(path.parent().unwrap()).unwrap();
    std::fs::write(
        &path,
        format!(
            r#"{{ "schemaVersion": 1, "plugins": [{}] }}"#,
            plugins.join(",")
        ),
    )
    .unwrap();
    path
}

#[test]
fn record_store_refusals() {
    for plugins in [
        vec![
            on_disk_record("edwards.hello", "0.1.0", ""),
            on_disk_record("edwards.hello", "0.1.1", ""),
        ],
        vec![
            on_disk_record("edwards.hello", "0.1.0", ""),
            on_disk_record("Edwards.Hello", "0.1.1", ""),
        ],
        vec![on_disk_record("../../app", "0.1.0", "")],
        vec![on_disk_record("C:/Users/someone/Documents", "0.1.0", "")],
        vec![on_disk_record("nul.hello", "0.1.0", "")],
        vec![on_disk_record("hello", "0.1.0", "")],
    ] {
        let root = TempDir::new("records-bad");
        let path = write_records(&root, &plugins);
        let mut store = InstalledPluginRecordStore::new(&path);
        assert!(store.load().is_err());
        assert!(store.records.is_empty());
    }
    let root = TempDir::new("records-schema");
    let path = records_file(&root);
    std::fs::create_dir_all(path.parent().unwrap()).unwrap();
    std::fs::write(&path, r#"{ "schemaVersion": 2, "plugins": [] }"#).unwrap();
    assert!(InstalledPluginRecordStore::new(&path).load().is_err());
}

#[test]
fn record_store_tolerates_and_drops_the_earlier_warning_accepted_at_field() {
    let root = TempDir::new("records-warning");
    let path = write_records(
        &root,
        &[on_disk_record(
            "edwards.hello",
            "0.1.0",
            r#", "warningAcceptedAt": "2026-01-01T00:00:00Z""#,
        )],
    );
    let mut store = InstalledPluginRecordStore::new(&path);
    assert!(store.load().unwrap());
    assert_eq!(store.records.len(), 1);
    store.save().unwrap();
    assert!(
        !std::fs::read_to_string(&path)
            .unwrap()
            .contains("warningAcceptedAt")
    );
}

#[test]
fn record_store_writes_channel_only_when_beta() {
    let root = TempDir::new("records-channel");
    let mut store = InstalledPluginRecordStore::new(&records_file(&root));
    store.records.push(record(
        "edwards.hello",
        "shaneedwards/openac-plugin-hello",
        Some("0.1.0"),
    ));
    store.save().unwrap();
    assert!(
        !std::fs::read_to_string(store.file_path())
            .unwrap()
            .contains("channel")
    );
    store.records[0].channel = PluginReleaseChannel::Beta;
    store.save().unwrap();
    assert!(
        std::fs::read_to_string(store.file_path())
            .unwrap()
            .contains("\"channel\": \"beta\"")
    );
    let mut reloaded = InstalledPluginRecordStore::new(store.file_path());
    reloaded.load().unwrap();
    assert_eq!(reloaded.records[0].channel, PluginReleaseChannel::Beta);
}

#[cfg(unix)]
#[test]
fn record_store_save_secures_the_file_to_owner_only_mode_on_unix() {
    use std::os::unix::fs::PermissionsExt;
    let root = TempDir::new("records-mode");
    let mut store = InstalledPluginRecordStore::new(&records_file(&root));
    store.records.push(record(
        "edwards.hello",
        "shaneedwards/openac-plugin-hello",
        Some("0.1.0"),
    ));
    store.save().unwrap();
    let mode = std::fs::metadata(store.file_path())
        .unwrap()
        .permissions()
        .mode();
    assert_eq!(mode & 0o777, 0o600);
}

// --- DirectInstallCheckTests ------------------------------------------------------------

const DIRECT_ID: &str = "acme.plugin";
const DIRECT_ENTRY: &str = "acme.plugin.wasm";

fn direct_manifest(min_host_version: Option<HostVersion>) -> PluginManifest {
    PluginManifest {
        id: DIRECT_ID.into(),
        display_name: DIRECT_ID.into(),
        version: "0.1.0".into(),
        entry: DIRECT_ENTRY.into(),
        api_version: ApiVersion::new(1, 0),
        dependencies: Vec::new(),
        min_host_version,
        max_host_version: None,
        skip_host_versions: Vec::new(),
        permissions_version: 0,
        permissions: Vec::new(),
        memory_mib: 256,
    }
}

fn valid_direct() -> PluginManifest {
    direct_manifest(Some(HostVersion::new(0, 1, 0)))
}

fn write_valid_plugin(root: &TempDir, folder: &str) -> PathBuf {
    let directory = root.join(folder);
    std::fs::create_dir_all(&directory).unwrap();
    std::fs::write(directory.join("plugin.json"), b"").unwrap();
    std::fs::write(directory.join(DIRECT_ENTRY), [1, 2, 3]).unwrap();
    directory
}

fn create_directory_link(link: &Path, target: &Path) {
    #[cfg(unix)]
    std::os::unix::fs::symlink(target, link).unwrap();
    #[cfg(windows)]
    {
        let status = std::process::Command::new("cmd.exe")
            .args(["/d", "/c", "mklink", "/J"])
            .arg(link)
            .arg(target)
            .stdout(std::process::Stdio::null())
            .status()
            .unwrap();
        assert!(status.success(), "could not create the test junction");
    }
}

#[test]
fn direct_install_passes_and_ignores_metadata_files() {
    let root = TempDir::new("direct");
    let directory = write_valid_plugin(&root, "valid");
    assert_eq!(direct_install_refusal(&directory, &valid_direct()), None);
    for name in [
        ".DS_Store",
        "._acme.plugin.wasm",
        "Thumbs.db",
        "desktop.ini",
    ] {
        std::fs::write(directory.join(name), [1, 2, 3]).unwrap();
    }
    assert_eq!(direct_install_refusal(&directory, &valid_direct()), None);
}

// OpenAC PluginInstallerTests.DirectInstallCheckIgnoresThePluginsOwnFiles.
#[test]
fn the_direct_install_check_ignores_the_plugins_own_files() {
    let root = TempDir::new("direct-files");
    let directory = write_valid_plugin(&root, "valid");
    std::fs::create_dir_all(directory.join("data")).unwrap();
    std::fs::write(directory.join("data").join("stray.exe"), [0x4D, 0x5A]).unwrap();
    assert_eq!(direct_install_refusal(&directory, &valid_direct()), None);
}

#[test]
fn direct_install_refuses_a_linked_root_and_a_linked_subfolder() {
    let root = TempDir::new("direct-links");
    let target = write_valid_plugin(&root, "real");
    let link = root.join("linked-root");
    create_directory_link(&link, &target);
    assert!(direct_install_refusal(&link, &valid_direct()).is_some());

    let directory = write_valid_plugin(&root, "with-linked-subfolder");
    let outside = root.join("outside-target");
    std::fs::create_dir_all(&outside).unwrap();
    create_directory_link(&directory.join("assets"), &outside);
    assert!(direct_install_refusal(&directory, &valid_direct()).is_some());
}

#[cfg(unix)]
#[test]
fn direct_install_refuses_a_file_symlink_and_a_fifo() {
    let root = TempDir::new("direct-unix");
    let directory = write_valid_plugin(&root, "with-file-symlink");
    let target = root.join("outside-file.txt");
    std::fs::write(&target, "not part of the plugin").unwrap();
    std::os::unix::fs::symlink(&target, directory.join("notes.txt")).unwrap();
    assert!(direct_install_refusal(&directory, &valid_direct()).is_some());

    let directory = write_valid_plugin(&root, "with-fifo");
    // Made in-process, not by the mkfifo program: a child process started
    // here keeps every lock file other tests hold open until it runs its
    // program, and their installs are refused as busy meanwhile.
    nix::unistd::mkfifo(
        &directory.join("icon.png"),
        nix::sys::stat::Mode::from_bits_truncate(0o644),
    )
    .unwrap();
    assert!(direct_install_refusal(&directory, &valid_direct()).is_some());
}

#[test]
fn direct_install_refuses_an_illegal_path_character() {
    let root = TempDir::new("direct-illegal");
    let directory = write_valid_plugin(&root, "illegal-character");
    // On Windows this names an alternate stream of a file called "notes",
    // which has no allowed extension; elsewhere ':' itself is refused.
    std::fs::write(directory.join("notes:txt"), [1]).unwrap();
    assert!(direct_install_refusal(&directory, &valid_direct()).is_some());
}

#[cfg(target_os = "linux")]
#[test]
fn direct_install_refuses_case_colliding_paths() {
    let root = TempDir::new("direct-case");
    let directory = write_valid_plugin(&root, "case-collision");
    for sub in ["sub", "SUB"] {
        std::fs::create_dir_all(directory.join(sub)).unwrap();
        std::fs::write(directory.join(sub).join("file.txt"), [1]).unwrap();
    }
    assert!(direct_install_refusal(&directory, &valid_direct()).is_some());
}

#[test]
fn direct_install_refuses_more_than_the_entry_limit() {
    let root = TempDir::new("direct-many");
    let directory = write_valid_plugin(&root, "too-many-files");
    for index in 0..2_001 {
        std::fs::write(directory.join(format!("extra-{index}.txt")), []).unwrap();
    }
    let refusal = direct_install_refusal(&directory, &valid_direct()).unwrap();
    assert!(refusal.contains("2000"), "{refusal}");
}

#[test]
fn direct_install_refuses_an_oversized_file() {
    let root = TempDir::new("direct-big");
    let directory = write_valid_plugin(&root, "oversized-file");
    let file = std::fs::File::create(directory.join("big.txt")).unwrap();
    file.set_len(64 * 1024 * 1024 + 1).unwrap();
    drop(file);
    assert!(direct_install_refusal(&directory, &valid_direct()).is_some());
}

#[test]
fn direct_install_content_and_manifest_refusals() {
    let root = TempDir::new("direct-content");
    // Native code of any kind (a .NET runtime folder's DLL among it).
    let directory = write_valid_plugin(&root, "native-library");
    std::fs::create_dir_all(directory.join("runtimes").join("win-x64")).unwrap();
    std::fs::write(
        directory
            .join("runtimes")
            .join("win-x64")
            .join("native.dll"),
        [1],
    )
    .unwrap();
    let refusal = direct_install_refusal(&directory, &valid_direct()).unwrap();
    assert!(refusal.contains("disallowed extension"), "{refusal}");

    let directory = write_valid_plugin(&root, "disallowed-extension");
    std::fs::write(directory.join("notes.exe"), [1]).unwrap();
    assert!(direct_install_refusal(&directory, &valid_direct()).is_some());

    let directory = root.join("missing-entry-module");
    std::fs::create_dir_all(&directory).unwrap();
    std::fs::write(directory.join("plugin.json"), b"").unwrap();
    assert!(direct_install_refusal(&directory, &valid_direct()).is_some());

    let directory = write_valid_plugin(&root, "no-min-host-version");
    let refusal = direct_install_refusal(&directory, &direct_manifest(None)).unwrap();
    assert!(refusal.contains("minHostVersion"), "{refusal}");

    let directory = write_valid_plugin(&root, "bad-icon");
    std::fs::write(directory.join("icon.png"), png::wrong_dimensions()).unwrap();
    assert!(direct_install_refusal(&directory, &valid_direct()).is_some());
}

#[cfg(unix)]
#[test]
fn direct_install_refuses_an_unreadable_subfolder() {
    use std::os::unix::fs::PermissionsExt;
    let root = TempDir::new("direct-locked");
    let directory = write_valid_plugin(&root, "unreadable-subfolder");
    let locked = directory.join("locked");
    std::fs::create_dir_all(&locked).unwrap();
    std::fs::write(locked.join("file.txt"), [1]).unwrap();
    std::fs::set_permissions(&locked, std::fs::Permissions::from_mode(0o000)).unwrap();
    let refusal = direct_install_refusal(&directory, &valid_direct());
    std::fs::set_permissions(&locked, std::fs::Permissions::from_mode(0o700)).unwrap();
    // Running as root can read anything; the rule only applies when it cannot.
    if std::fs::read_dir("/root").is_err() {
        assert!(refusal.is_some());
    }
}

// --- PluginInventoryTests ------------------------------------------------------------------

fn manifest_json(id: &str, version: &str) -> String {
    format!(
        r#"{{
  "id": "{id}",
  "displayName": "{id}",
  "version": "{version}",
  "entry": "{id}.wasm",
  "apiVersion": "1.0",
  "minHostVersion": "0.1.0"
}}"#
    )
}

fn write_manifest(directory: &Path, id: &str, version: &str) {
    std::fs::create_dir_all(directory).unwrap();
    std::fs::write(directory.join("plugin.json"), manifest_json(id, version)).unwrap();
    std::fs::write(directory.join(format!("{id}.wasm")), []).unwrap();
}

/// An OpenAC (C#) plugin unzipped into the plugins folder.
fn write_csharp_plugin(directory: &Path, id: &str) {
    std::fs::create_dir_all(directory).unwrap();
    std::fs::write(
        directory.join("plugin.json"),
        format!(
            r#"{{ "id": "{id}", "displayName": "{id}", "version": "0.5.1", "entryDll": "{id}.dll",
                "apiVersion": 1, "minHostVersion": "0.1.0", "hosts": ["headless"] }}"#
        ),
    )
    .unwrap();
    std::fs::write(directory.join(format!("{id}.dll")), []).unwrap();
}

#[test]
fn an_openac_csharp_plugin_folder_is_listed_as_refused_and_never_loaded() {
    let root = TempDir::new("inventory-csharp");
    let paths = root.path_set();
    write_csharp_plugin(
        &paths.plugins_directory().join("acdream.mosstank"),
        "acdream.mosstank",
    );
    write_manifest(&paths.plugins_directory().join(HELLO), HELLO, "0.1.0");
    let plugins = PluginInventory::new(&paths).build(
        &InstalledPluginRecordStore::for_paths(&paths),
        None,
        None,
    );
    assert_eq!(plugins.len(), 2);
    let csharp = plugins
        .iter()
        .find(|info| info.id == "acdream.mosstank")
        .unwrap();
    assert_eq!(csharp.source, InstalledPluginSource::Direct);
    assert_eq!(
        csharp.refusal.as_deref(),
        Some("Built for OpenAC (C#): this client loads WebAssembly plugins only.")
    );
    assert!(csharp.compatibility_is_warning);
    let hello = plugins.iter().find(|info| info.id == HELLO).unwrap();
    assert_eq!(hello.refusal, None);
    assert!(!hello.has_duplicate);
}

fn verified_client(directory: &Path) -> ClientVersionResolution {
    ClientVersionResolution {
        state: ClientVersionState::Verified,
        status: "ok".into(),
        version: Some(version("0.1.7")),
        directory: Some(directory.to_path_buf()),
        previous_version: None,
        record: None,
    }
}

const HELLO: &str = "edwards.hello";
const HELLO_REPO: &str = "shaneedwards/openac-plugin-hello";

#[test]
fn a_managed_plugin_is_distinguished_from_a_manual_one_by_the_record() {
    let root = TempDir::new("inventory");
    let paths = root.path_set();
    write_manifest(&paths.plugins_directory().join(HELLO), HELLO, "0.1.0");
    write_manifest(
        &paths.plugins_directory().join("someone.manual"),
        "someone.manual",
        "1.0.0",
    );
    let mut records = InstalledPluginRecordStore::for_paths(&paths);
    records
        .records
        .push(record(HELLO, HELLO_REPO, Some("0.1.0")));
    let plugins = PluginInventory::new(&paths).build(&records, None, None);
    let managed = plugins.iter().find(|info| info.id == HELLO).unwrap();
    assert_eq!(managed.source, InstalledPluginSource::Managed);
    assert_eq!(managed.repo.as_deref(), Some(HELLO_REPO));
    assert_eq!(managed.listed_source, Some(PluginInstallSource::Listed));
    let direct = plugins
        .iter()
        .find(|info| info.id == "someone.manual")
        .unwrap();
    assert_eq!(direct.source, InstalledPluginSource::Direct);
    assert_eq!(direct.repo, None);
    assert_eq!(direct.listed_source, None);
}

#[test]
fn a_bundled_id_that_is_also_managed_is_flagged_as_a_conflict_once() {
    let root = TempDir::new("inventory-bundled");
    let paths = root.path_set();
    let client = root.join("client");
    write_manifest(&paths.plugins_directory().join(HELLO), HELLO, "0.1.0");
    write_manifest(&client.join("plugins").join(HELLO), HELLO, "0.1.0");
    let mut records = InstalledPluginRecordStore::for_paths(&paths);
    records
        .records
        .push(record(HELLO, HELLO_REPO, Some("0.1.0")));
    let plugins =
        PluginInventory::new(&paths).build(&records, Some(&verified_client(&client)), None);
    assert_eq!(plugins.len(), 1);
    assert_eq!(plugins[0].source, InstalledPluginSource::Managed);
    assert!(plugins[0].conflict);
}

#[test]
fn a_record_whose_own_folder_manifest_id_differs_falls_through_to_the_direct_scan() {
    let root = TempDir::new("inventory-differs");
    let paths = root.path_set();
    write_manifest(
        &paths.plugins_directory().join(HELLO),
        "edwards.other",
        "0.1.0",
    );
    let mut records = InstalledPluginRecordStore::for_paths(&paths);
    records
        .records
        .push(record(HELLO, HELLO_REPO, Some("0.1.0")));
    let plugins = PluginInventory::new(&paths).build(&records, None, None);
    assert_eq!(plugins.len(), 1);
    assert_eq!(plugins[0].id, "edwards.other");
    assert_eq!(plugins[0].source, InstalledPluginSource::Direct);
}

#[test]
fn duplicate_ids_are_flagged_and_refused() {
    let root = TempDir::new("inventory-dup");
    let paths = root.path_set();
    write_manifest(&paths.plugins_directory().join(HELLO), HELLO, "0.1.0");
    write_manifest(&paths.plugins_directory().join("aaa-stray"), HELLO, "0.1.0");
    let mut records = InstalledPluginRecordStore::for_paths(&paths);
    records
        .records
        .push(record(HELLO, HELLO_REPO, Some("0.1.0")));
    let plugins = PluginInventory::new(&paths).build(&records, None, None);
    let managed = plugins
        .iter()
        .find(|info| info.source == InstalledPluginSource::Managed)
        .unwrap();
    assert!(managed.has_duplicate);
    assert_eq!(managed.refusal, None);
    let stray = plugins
        .iter()
        .find(|info| info.source == InstalledPluginSource::Direct)
        .unwrap();
    assert_eq!(
        stray.refusal.as_deref(),
        Some("Another copy of this plugin is installed.")
    );

    let root = TempDir::new("inventory-two-direct");
    let paths = root.path_set();
    write_manifest(&paths.plugins_directory().join("aaa-copy"), HELLO, "0.1.0");
    write_manifest(&paths.plugins_directory().join("zzz-copy"), HELLO, "0.1.0");
    let plugins = PluginInventory::new(&paths).build(
        &InstalledPluginRecordStore::for_paths(&paths),
        None,
        None,
    );
    assert_eq!(plugins.len(), 2);
    for info in &plugins {
        assert_eq!(info.source, InstalledPluginSource::Direct);
        assert_eq!(
            info.refusal.as_deref(),
            Some("Another copy of this plugin is installed.")
        );
    }

    let root = TempDir::new("inventory-direct-bundled");
    let paths = root.path_set();
    let client = root.join("client");
    write_manifest(&paths.plugins_directory().join(HELLO), HELLO, "0.1.0");
    write_manifest(&client.join("plugins").join(HELLO), HELLO, "0.1.0");
    let plugins = PluginInventory::new(&paths).build(
        &InstalledPluginRecordStore::for_paths(&paths),
        Some(&verified_client(&client)),
        None,
    );
    assert_eq!(plugins.len(), 1);
    assert_eq!(plugins[0].source, InstalledPluginSource::Direct);
    assert_eq!(
        plugins[0].refusal.as_deref(),
        Some("Another copy of this plugin is installed.")
    );
}

#[test]
fn inventory_find_and_version_rules() {
    let root = TempDir::new("inventory-find");
    let paths = root.path_set();
    let records = InstalledPluginRecordStore::for_paths(&paths);
    let inventory = PluginInventory::new(&paths);
    assert!(
        inventory
            .find("nobody.home", &records, None, None)
            .is_none()
    );
    write_manifest(&paths.plugins_directory().join(HELLO), HELLO, "0.1.0");
    assert!(
        inventory
            .find("Edwards.Hello", &records, None, None)
            .is_some()
    );

    let manifest = parse(&manifest_json(HELLO, "0.1.0"));
    assert_eq!(
        evaluate_version_compatibility(&manifest, None).as_deref(),
        Some("client not installed")
    );
    assert_eq!(
        evaluate_version_compatibility(&manifest, Some(&version("0.0.9"))).as_deref(),
        Some("requires OpenAC 0.1.0 or newer (this is 0.0.9)")
    );
    assert_eq!(
        evaluate_version_compatibility(&manifest, Some(&version("0.1.0"))),
        None
    );

    let catalog = PluginCatalog::parse(&catalog_json(
        HELLO_ENTRY,
        Some(r#"{ "id": "edwards.hello", "versions": ["0.1.0"], "reason": "test" }"#),
    ))
    .unwrap();
    assert_eq!(
        find_block_reason(Some(&catalog), HELLO, &version("0.1.0")).as_deref(),
        Some("test")
    );
    assert_eq!(
        find_block_reason(Some(&catalog), HELLO, &version("0.1.1")),
        None
    );
    assert_eq!(find_block_reason(None, HELLO, &version("0.1.0")), None);
    assert_eq!(
        find_block_reason(Some(&catalog), "Edwards.Hello", &version("0.1.0")).as_deref(),
        Some("test")
    );
}

// --- PluginReleaseClientTests -----------------------------------------------------------

fn latest(repo: &str, asset: &str) -> String {
    github::latest_asset(repo, asset).unwrap().to_string()
}

fn tagged(repo: &str, tag: &str, asset: &str) -> String {
    github::tagged_asset(repo, tag, asset).unwrap().to_string()
}

fn redirect(location: &str) -> Route {
    Route::Redirect {
        status: 302,
        location: location.into(),
    }
}

fn fetch(
    server: &FakeServer,
    url: &str,
    maximum: u64,
) -> Result<PluginReleaseFetchResult, UpdateError> {
    PluginReleaseClient::new(server.transport()).fetch_document(
        &WebUrl::parse(url).unwrap(),
        maximum,
        &none(),
    )
}

const MAX_DOC: u64 = ac_launcher_core::plugins::MAXIMUM_DOCUMENT_BYTES;

#[test]
fn captures_the_tag_from_the_latest_download_redirect() {
    let server = FakeServer::new();
    let body = br#"{"id":"edwards.hello"}"#.to_vec();
    server.add_route(
        &latest(HELLO_REPO, "plugin.json"),
        redirect(&tagged(HELLO_REPO, "v0.1.0", "plugin.json")),
    );
    server.add_route(
        &tagged(HELLO_REPO, "v0.1.0", "plugin.json"),
        redirect("https://release-assets.githubusercontent.com/plugin.json"),
    );
    server.add(
        "https://release-assets.githubusercontent.com/plugin.json",
        &body,
    );
    let PluginReleaseFetchResult::Success(document) =
        fetch(&server, &latest(HELLO_REPO, "plugin.json"), MAX_DOC).unwrap()
    else {
        panic!("expected success");
    };
    assert_eq!(document.tag.as_deref(), Some("v0.1.0"));
    assert_eq!(document.content, body);
}

#[test]
fn a_redirect_to_a_different_repo_is_unavailable() {
    let server = FakeServer::new();
    let impostor = tagged("attacker/evil-repo", "v9.9.9", "plugin.json");
    server.add_route(&latest(HELLO_REPO, "plugin.json"), redirect(&impostor));
    server.add(&impostor, b"{}");
    assert_eq!(
        fetch(&server, &latest(HELLO_REPO, "plugin.json"), MAX_DOC).unwrap(),
        PluginReleaseFetchResult::Unavailable
    );
}

#[test]
fn release_client_transport_and_status_outcomes() {
    let server = FakeServer::new();
    server.add_route("https://example.test/fail.json", Route::Fail);
    server.add_route("https://example.test/404.json", Route::Status(404));
    server.add_route("https://example.test/504.json", Route::Status(504));
    server.add_route("https://example.test/429.json", Route::Status(429));
    assert_eq!(
        fetch(&server, "https://example.test/fail.json", MAX_DOC).unwrap(),
        PluginReleaseFetchResult::Unavailable
    );
    assert_eq!(
        fetch(&server, "https://example.test/404.json", MAX_DOC).unwrap(),
        PluginReleaseFetchResult::Unavailable
    );
    assert_eq!(
        fetch(&server, "https://example.test/504.json", MAX_DOC).unwrap(),
        PluginReleaseFetchResult::Unavailable
    );
    assert_eq!(
        fetch(&server, "https://example.test/429.json", MAX_DOC).unwrap(),
        PluginReleaseFetchResult::RateLimited
    );
}

#[test]
fn non_https_hop_is_rejected_before_it_is_requested() {
    let server = FakeServer::new();
    let start = "https://example.test/plugin.json";
    server.add_route(start, redirect("http://example.test/plugin.json"));
    let error = fetch(&server, start, MAX_DOC).unwrap_err().to_string();
    assert!(error.contains("HTTPS"), "{error}");
    assert_eq!(server.requests(), [start]);
}

#[test]
fn oversized_bodies_are_rejected_and_the_cap_can_be_raised() {
    let server = FakeServer::new();
    let body = vec![b' '; MAX_DOC as usize + 1];
    server.add_declared("https://example.test/plugins.json", &body, None);
    assert!(fetch(&server, "https://example.test/plugins.json", MAX_DOC).is_err());
    server.add_declared(
        "https://example.test/declared.json",
        b"{}",
        Some(MAX_DOC + 1),
    );
    assert!(fetch(&server, "https://example.test/declared.json", MAX_DOC).is_err());
    server.add_declared("https://example.test/releases.atom", &body, None);
    let PluginReleaseFetchResult::Success(document) =
        fetch(&server, "https://example.test/releases.atom", MAX_DOC + 1).unwrap()
    else {
        panic!("expected success");
    };
    assert_eq!(document.content, body);
}

// --- PluginReleaseFeedTests ---------------------------------------------------------------

fn link(tag: &str) -> String {
    format!(r#"<link rel="alternate" href="https://github.com/{HELLO_REPO}/releases/tag/{tag}"/>"#)
}

fn entry(body: &str) -> String {
    format!("<entry>{body}</entry>")
}

fn feed(entries: &str) -> Vec<u8> {
    format!(
        r#"<?xml version="1.0" encoding="UTF-8"?>
<feed xmlns="http://www.w3.org/2005/Atom">{entries}</feed>"#
    )
    .into_bytes()
}

// OpenAC PluginReleaseFeedTests.ReleasesFeedIsTheRepoAtomFeedUrl.
#[test]
fn releases_feed_is_the_repo_atom_feed_url() {
    assert_eq!(
        github::releases_feed(HELLO_REPO).unwrap().to_string(),
        "https://github.com/shaneedwards/openac-plugin-hello/releases.atom"
    );
}

// OpenAC PluginReleaseFeedTests (the link rules), with `wasm-v` tags: an
// OpenAC (C#) release's `v` tag on the same feed is not this client's.
#[test]
fn feed_rules() {
    let found = highest_prerelease(&feed(&entry(&link("wasm-v1.3.0-beta.1"))), HELLO_REPO)
        .unwrap()
        .unwrap();
    assert_eq!(found.tag, "wasm-v1.3.0-beta.1");
    for entries in [
        entry(&link("wasm-v1.3.0")),
        entry(&link("v1.3.0-beta.1")),
        entry(&link("rust-v1.3.0-beta.1")),
        entry(
            r#"<link rel="alternate" href="https://github.com/someone-else/other-plugin/releases/tag/wasm-v1.3.0-beta.1"/>"#,
        ),
        entry(&format!(
            r#"<link rel="alternate" href="https://github.com/{HELLO_REPO}-renamed/releases/tag/wasm-v1.3.0-beta.1"/>"#
        )),
        entry(&format!(
            r#"<link rel="alternate" href="http://github.com/{HELLO_REPO}/releases/tag/wasm-v1.3.0-beta.1"/>"#
        )),
        entry(&format!(
            r#"<link rel="alternate" href="https://example.com/{HELLO_REPO}/releases/tag/wasm-v1.3.0-beta.1"/>"#
        )),
        entry(&format!(
            r#"<link rel="alternate" href="https://github.com/{HELLO_REPO}/releases/tag/wasm-v1.3.0-beta.1?foo=bar"/>"#
        )),
        entry(&format!(
            r#"<link rel="alternate" href="https://github.com/{HELLO_REPO}/releases/tag/wasm-v1.3.0-beta.1/extra"/>"#
        )),
        format!(
            r#"<link rel="self" href="https://github.com/{HELLO_REPO}/releases/tag/wasm-v1.3.0-beta.1"/>{}"#,
            entry("<title>no link here</title>")
        ),
        format!(
            r#"<entry><content type="html"><link href="https://github.com/{HELLO_REPO}/releases/tag/wasm-v1.3.0-beta.1"/></content></entry>"#
        ),
        String::new(),
        entry(&link("wasm-v1.3.0-beta.1+build.5")),
    ] {
        assert_eq!(
            highest_prerelease(&feed(&entries), HELLO_REPO).unwrap(),
            None,
            "{entries}"
        );
    }
    let highest = highest_prerelease(
        &feed(&(entry(&link("wasm-v1.3.0-beta.2")) + &entry(&link("wasm-v1.3.0-beta.10")))),
        HELLO_REPO,
    )
    .unwrap()
    .unwrap();
    assert_eq!(highest.tag, "wasm-v1.3.0-beta.10");
}

#[test]
fn the_highest_stable_wasm_release_ignores_csharp_releases_and_pre_releases() {
    let entries = [
        "v0.9.0",
        "wasm-v0.6.0",
        "wasm-v0.6.1",
        "wasm-v0.7.0-beta.1",
        "v0.5.1",
    ]
    .iter()
    .map(|tag| entry(&link(tag)))
    .collect::<String>();
    let stable = highest_stable(&feed(&entries), HELLO_REPO)
        .unwrap()
        .unwrap();
    assert_eq!(stable.tag, "wasm-v0.6.1");
    assert_eq!(stable.version.value(), "0.6.1");
    assert_eq!(
        highest_stable(&feed(&entry(&link("v1.0.0"))), HELLO_REPO).unwrap(),
        None
    );
}

// OpenAC PluginReleaseFeedTests.FeedRefusesADtdAndMalformedXml.
#[test]
fn feed_refuses_a_dtd_and_malformed_xml() {
    let dtd = br#"<?xml version="1.0"?>
<!DOCTYPE feed [<!ENTITY x "y">]>
<feed xmlns="http://www.w3.org/2005/Atom"></feed>"#;
    assert!(highest_prerelease(dtd, HELLO_REPO).is_err());
    assert!(
        highest_prerelease(br#"<feed xmlns="http://www.w3.org/2005/Atom">"#, HELLO_REPO).is_err()
    );
}

// --- PluginReleaseResolverTests --------------------------------------------------------------

fn resolver_manifest(version: &str) -> Vec<u8> {
    format!(
        r#"{{
  "id": "edwards.hello",
  "displayName": "Hello",
  "version": "{version}",
  "entry": "hello.wasm",
  "apiVersion": "1.0"
}}"#
    )
    .into_bytes()
}

fn resolver(server: &FakeServer) -> PluginReleaseResolver {
    PluginReleaseResolver::new(PluginReleaseClient::new(server.transport()))
}

fn feed_uri() -> String {
    github::releases_feed(HELLO_REPO).unwrap().to_string()
}

/// A repository whose feed lists `tags` (a C# `v` release among them is
/// ignored), each `wasm-v` tag serving a manifest of its own version.
fn release_server(tags: &[&str]) -> FakeServer {
    let server = FakeServer::new();
    let entries: String = tags.iter().map(|tag| entry(&link(tag))).collect();
    server.add(&feed_uri(), &feed(&entries));
    for tag in tags {
        if let Some(version) = tag.strip_prefix("wasm-v") {
            server.add(
                &tagged(HELLO_REPO, tag, "plugin.json"),
                &resolver_manifest(version),
            );
        }
    }
    server
}

fn tag_of(result: &PluginReleaseResolveResult) -> &str {
    &result.resolution().unwrap().tag
}

// OpenAC PluginReleaseResolverTests (stable), resolved from the feed's
// `wasm-v` releases instead of `releases/latest`.
#[test]
fn stable_resolution_outcomes() {
    let server = release_server(&["v0.9.0", "wasm-v0.2.0", "wasm-v0.1.0"]);
    let result = resolver(&server)
        .resolve(HELLO_REPO, PluginReleaseChannel::Stable, &none())
        .unwrap();
    let resolution = result.resolution().unwrap();
    assert_eq!(resolution.tag, "wasm-v0.2.0");
    assert_eq!(resolution.manifest.version, "0.2.0");
    assert_eq!(resolution.manifest_bytes, resolver_manifest("0.2.0"));
    // `releases/latest` (the C# launcher's) is never asked.
    assert_eq!(server.count(&latest(HELLO_REPO, "plugin.json")), 0);

    let server = release_server(&["wasm-v1.3.0-beta.1"]);
    assert!(matches!(
        resolver(&server)
            .resolve(HELLO_REPO, PluginReleaseChannel::Stable, &none())
            .unwrap(),
        PluginReleaseResolveResult::Prerelease(_)
    ));

    let server = release_server(&[]);
    server.add(
        &tagged(HELLO_REPO, "wasm-v0.2.0", "plugin.json"),
        &resolver_manifest("0.3.0"),
    );
    server.add(&feed_uri(), &feed(&entry(&link("wasm-v0.2.0"))));
    assert!(matches!(
        resolver(&server)
            .resolve(HELLO_REPO, PluginReleaseChannel::Stable, &none())
            .unwrap(),
        PluginReleaseResolveResult::Invalid(_)
    ));

    // A repository with only OpenAC (C#) releases has nothing for this client.
    let server = release_server(&["v0.5.1", "v0.5.0"]);
    assert_eq!(
        resolver(&server)
            .resolve(HELLO_REPO, PluginReleaseChannel::Stable, &none())
            .unwrap(),
        PluginReleaseResolveResult::Unavailable
    );

    // A `wasm-v` release whose manifest is OpenAC's is refused as built for it.
    let server = release_server(&[]);
    server.add(&feed_uri(), &feed(&entry(&link("wasm-v0.2.0"))));
    server.add(
        &tagged(HELLO_REPO, "wasm-v0.2.0", "plugin.json"),
        br#"{ "id": "edwards.hello", "displayName": "Hello", "version": "0.2.0",
              "entryDll": "Hello.dll", "apiVersion": 1 }"#,
    );
    let PluginReleaseResolveResult::Invalid(reason) = resolver(&server)
        .resolve(HELLO_REPO, PluginReleaseChannel::Stable, &none())
        .unwrap()
    else {
        panic!("expected invalid");
    };
    assert!(reason.starts_with("built for OpenAC (C#)"), "{reason}");

    let server = FakeServer::new();
    server.fallback(|_, _| Route::Status(429));
    assert_eq!(
        resolver(&server)
            .resolve(HELLO_REPO, PluginReleaseChannel::Stable, &none())
            .unwrap(),
        PluginReleaseResolveResult::RateLimited
    );
    let server = FakeServer::new();
    assert_eq!(
        resolver(&server)
            .resolve(HELLO_REPO, PluginReleaseChannel::Stable, &none())
            .unwrap(),
        PluginReleaseResolveResult::Unavailable
    );
}

#[derive(Default)]
struct BetaOptions {
    beta_manifest_missing: bool,
    beta_manifest_version: Option<&'static str>,
}

fn beta_server(stable: &str, beta: &str, options: BetaOptions) -> FakeServer {
    let stable_tag = format!("wasm-v{stable}");
    let beta_tag = format!("wasm-v{beta}");
    let server = FakeServer::new();
    server.add(
        &feed_uri(),
        &feed(&(entry(&link(&beta_tag)) + &entry(&link(&stable_tag)))),
    );
    server.add(
        &tagged(HELLO_REPO, &stable_tag, "plugin.json"),
        &resolver_manifest(stable),
    );
    if !options.beta_manifest_missing {
        server.add(
            &tagged(HELLO_REPO, &beta_tag, "plugin.json"),
            &resolver_manifest(options.beta_manifest_version.unwrap_or(beta)),
        );
    }
    server
}

fn resolve_beta(server: &FakeServer) -> PluginReleaseResolveResult {
    resolver(server)
        .resolve(HELLO_REPO, PluginReleaseChannel::Beta, &none())
        .unwrap()
}

// OpenAC PluginReleaseResolverTests (beta).
#[test]
fn beta_resolution_outcomes() {
    assert_eq!(
        tag_of(&resolve_beta(&beta_server(
            "1.3.0",
            "1.4.0-beta.1",
            BetaOptions::default()
        ))),
        "wasm-v1.4.0-beta.1"
    );
    assert_eq!(
        tag_of(&resolve_beta(&beta_server(
            "1.3.0",
            "1.3.0-beta.2",
            BetaOptions::default()
        ))),
        "wasm-v1.3.0"
    );
    for options in [
        BetaOptions {
            beta_manifest_missing: true,
            ..BetaOptions::default()
        },
        BetaOptions {
            beta_manifest_version: Some("1.5.0-beta.1"),
            ..BetaOptions::default()
        },
    ] {
        assert_eq!(
            tag_of(&resolve_beta(&beta_server(
                "1.3.0",
                "1.4.0-beta.1",
                options
            ))),
            "wasm-v1.3.0"
        );
    }
    // No stable release never hides a resolved beta.
    let server = release_server(&["wasm-v1.4.0-beta.1"]);
    assert_eq!(tag_of(&resolve_beta(&server)), "wasm-v1.4.0-beta.1");
}

#[test]
fn an_unreadable_feed_offers_nothing() {
    let server = FakeServer::new();
    server.add(
        &feed_uri(),
        br#"<feed xmlns="http://www.w3.org/2005/Atom">"#,
    );
    assert!(matches!(
        resolve_beta(&server),
        PluginReleaseResolveResult::Invalid(_)
    ));
    let server = FakeServer::new();
    server.add_declared(
        &feed_uri(),
        &feed(&entry(&link("wasm-v1.0.0"))),
        Some(2 * 1024 * 1024),
    );
    assert_eq!(
        resolve_beta(&server),
        PluginReleaseResolveResult::Unavailable
    );
}

fn resolution(version: &str) -> Box<PluginReleaseResolution> {
    Box::new(PluginReleaseResolution {
        tag: release_tag(version),
        manifest_bytes: resolver_manifest(version),
        manifest: parse(&String::from_utf8(resolver_manifest(version)).unwrap()),
    })
}

// OpenAC PluginReleaseResolverTests.CandidatesChooseByChannelAndPrecedence.
#[test]
fn candidates_choose_by_channel_and_precedence() {
    let stable = PluginReleaseResolveResult::Success(resolution("0.2.0"));
    let only_stable = PluginReleaseCandidates {
        stable: stable.clone(),
        beta: None,
        index_warning: None,
    };
    assert_eq!(only_stable.for_channel(PluginReleaseChannel::Beta), &stable);

    let failed_stable = PluginReleaseCandidates {
        stable: PluginReleaseResolveResult::Unavailable,
        beta: Some(PluginReleaseResolveResult::Success(resolution(
            "1.4.0-beta.1",
        ))),
        index_warning: None,
    };
    assert_eq!(
        tag_of(failed_stable.for_channel(PluginReleaseChannel::Beta)),
        "wasm-v1.4.0-beta.1"
    );

    let stable = PluginReleaseResolveResult::Success(resolution("1.3.0"));
    let higher = PluginReleaseCandidates {
        stable: stable.clone(),
        beta: Some(PluginReleaseResolveResult::Success(resolution(
            "1.4.0-beta.1",
        ))),
        index_warning: None,
    };
    let lower = PluginReleaseCandidates {
        stable,
        beta: Some(PluginReleaseResolveResult::Success(resolution(
            "1.3.0-beta.2",
        ))),
        index_warning: None,
    };
    assert_eq!(
        tag_of(higher.for_channel(PluginReleaseChannel::Beta)),
        "wasm-v1.4.0-beta.1"
    );
    assert_eq!(
        tag_of(lower.for_channel(PluginReleaseChannel::Beta)),
        "wasm-v1.3.0"
    );
}

// OpenAC PluginReleaseResolverTests.OnlyTheBetaChannelFetchesTheReleasesFeed: the
// feed now serves both channels (one fetch each pass); only beta fetches the
// pre-release's manifest.
#[test]
fn only_the_beta_channel_fetches_the_pre_release_manifest() {
    let beta_manifest = tagged(HELLO_REPO, "wasm-v1.4.0-beta.1", "plugin.json");
    let server = beta_server("1.3.0", "1.4.0-beta.1", BetaOptions::default());
    let candidates = resolver(&server)
        .resolve_candidates(HELLO_REPO, PluginReleaseChannel::Stable, &none())
        .unwrap();
    assert!(candidates.beta.is_none());
    assert_eq!(server.count(&feed_uri()), 1);
    assert_eq!(server.count(&beta_manifest), 0);
    let candidates = resolver(&server)
        .resolve_candidates(HELLO_REPO, PluginReleaseChannel::Beta, &none())
        .unwrap();
    assert!(candidates.beta.is_some());
    assert_eq!(server.count(&feed_uri()), 2);
    assert_eq!(server.count(&beta_manifest), 1);
}

// --- Several plugins in one repository ---------------------------------------------------------

fn plugin_manifest(id: &str, version: &str) -> Vec<u8> {
    format!(
        r#"{{ "id": "{id}", "displayName": "P", "version": "{version}", "entry": "p.wasm",
              "apiVersion": "1.0" }}"#
    )
    .into_bytes()
}

/// A repository whose feed lists each `(tag, id, version)` in order, the
/// tag serving a manifest naming `id` and `version`.
fn suite_server(releases: &[(&str, &str, &str)]) -> FakeServer {
    let server = FakeServer::new();
    let entries: String = releases.iter().map(|(tag, ..)| entry(&link(tag))).collect();
    server.add(&feed_uri(), &feed(&entries));
    for (tag, id, version) in releases {
        server.add(
            &tagged(HELLO_REPO, tag, "plugin.json"),
            &plugin_manifest(id, version),
        );
    }
    server
}

fn resolve_plugin(
    server: &FakeServer,
    id: &str,
    channel: PluginReleaseChannel,
) -> PluginReleaseResolveResult {
    resolver(server)
        .resolve_plugin(HELLO_REPO, id, channel, &none())
        .unwrap()
}

#[test]
fn a_plugin_resolves_from_its_own_tags_beside_another_plugins() {
    let server = suite_server(&[
        ("wasm-edwards.other-v2.0.0", "edwards.other", "2.0.0"),
        ("wasm-edwards.hello-v0.3.0", "edwards.hello", "0.3.0"),
        ("wasm-edwards.hello-v0.2.0", "edwards.hello", "0.2.0"),
    ]);
    let result = resolve_plugin(&server, "edwards.hello", PluginReleaseChannel::Stable);
    assert_eq!(tag_of(&result), "wasm-edwards.hello-v0.3.0");
    let result = resolve_plugin(&server, "edwards.other", PluginReleaseChannel::Stable);
    assert_eq!(tag_of(&result), "wasm-edwards.other-v2.0.0");
    // Another plugin's tag is never fetched.
    assert_eq!(
        server.count(&tagged(
            HELLO_REPO,
            "wasm-edwards.hello-v0.2.0",
            "plugin.json"
        )),
        0
    );
    // The id-less resolution reads only `wasm-v` tags, as before.
    assert_eq!(
        resolver(&server)
            .resolve(HELLO_REPO, PluginReleaseChannel::Stable, &none())
            .unwrap(),
        PluginReleaseResolveResult::Unavailable
    );
}

#[test]
fn old_wasm_v_tags_count_only_for_the_plugin_their_manifest_names() {
    // A new plugin in a repository whose older plugin used `wasm-v` tags.
    let server = suite_server(&[
        ("wasm-v0.9.0", "edwards.other", "0.9.0"),
        ("wasm-edwards.hello-v0.1.0", "edwards.hello", "0.1.0"),
    ]);
    let result = resolve_plugin(&server, "edwards.hello", PluginReleaseChannel::Stable);
    assert_eq!(tag_of(&result), "wasm-edwards.hello-v0.1.0");
    let result = resolve_plugin(&server, "edwards.other", PluginReleaseChannel::Stable);
    assert_eq!(tag_of(&result), "wasm-v0.9.0");
    // A plugin moving from `wasm-v` to its own tags keeps both.
    let server = suite_server(&[
        ("wasm-v0.9.0", "edwards.hello", "0.9.0"),
        ("wasm-edwards.hello-v0.8.0", "edwards.hello", "0.8.0"),
    ]);
    let result = resolve_plugin(&server, "edwards.hello", PluginReleaseChannel::Stable);
    assert_eq!(tag_of(&result), "wasm-v0.9.0");
}

#[test]
fn a_tag_another_id_parses_as_is_skipped_for_the_next_highest() {
    // `edwards.hello-v1.0.0` is a valid id, so its tag reads as a
    // pre-release of `edwards.hello`.
    let server = suite_server(&[
        (
            "wasm-edwards.hello-v1.0.0-v2.0.0",
            "edwards.hello-v1.0.0",
            "2.0.0",
        ),
        (
            "wasm-edwards.hello-v0.5.0-beta.1",
            "edwards.hello",
            "0.5.0-beta.1",
        ),
        ("wasm-edwards.hello-v0.4.0", "edwards.hello", "0.4.0"),
    ]);
    let result = resolve_plugin(&server, "edwards.hello", PluginReleaseChannel::Beta);
    assert_eq!(tag_of(&result), "wasm-edwards.hello-v0.5.0-beta.1");
}

#[test]
fn skipping_stops_after_three_refused_candidates() {
    let refused = |count: usize| -> Vec<(String, String)> {
        (0..count)
            .map(|index| {
                let version = format!("0.{}.0", 9 - index);
                (format!("wasm-v{version}"), version)
            })
            .collect()
    };
    for (count, found) in [(2, true), (3, false)] {
        let others = refused(count);
        let mut releases: Vec<(&str, &str, &str)> = others
            .iter()
            .map(|(tag, version)| (tag.as_str(), "edwards.other", version.as_str()))
            .collect();
        releases.push(("wasm-edwards.hello-v0.1.0", "edwards.hello", "0.1.0"));
        let server = suite_server(&releases);
        let result = resolve_plugin(&server, "edwards.hello", PluginReleaseChannel::Stable);
        if found {
            assert_eq!(tag_of(&result), "wasm-edwards.hello-v0.1.0");
        } else {
            assert_eq!(result, PluginReleaseResolveResult::Unavailable, "{count}");
            assert_eq!(
                server.count(&tagged(
                    HELLO_REPO,
                    "wasm-edwards.hello-v0.1.0",
                    "plugin.json"
                )),
                0
            );
        }
    }
}

#[test]
fn a_tag_naming_the_wrong_version_is_still_invalid() {
    let server = suite_server(&[("wasm-edwards.hello-v0.2.0", "edwards.hello", "0.3.0")]);
    assert!(matches!(
        resolve_plugin(&server, "edwards.hello", PluginReleaseChannel::Stable),
        PluginReleaseResolveResult::Invalid(_)
    ));
}

// --- The plugin index ------------------------------------------------------------------------------

const SUITE_INDEX: &str = r#"{
  "schemaVersion": 1,
  "plugins": [
    { "id": "aethrix.vision", "stable": "wasm-aethrix.vision-v1.2.0",
      "beta": "wasm-aethrix.vision-v1.3.0-beta.1" },
    { "id": "aethrix.navigation", "stable": "wasm-aethrix.navigation-v0.4.0" },
    { "id": "aethrix.macro", "beta": "wasm-v0.1.0-beta.2" }
  ]
}"#;

#[test]
fn the_index_names_each_plugins_releases() {
    let index = PluginIndex::parse(SUITE_INDEX).unwrap();
    assert_eq!(index.plugins.len(), 3);
    let vision = index.entry("aethrix.vision").unwrap();
    assert_eq!(vision.stable.as_deref(), Some("wasm-aethrix.vision-v1.2.0"));
    assert_eq!(
        vision.beta.as_deref(),
        Some("wasm-aethrix.vision-v1.3.0-beta.1")
    );
    let releases = index.releases_for("aethrix.vision");
    let tags: Vec<&str> = releases
        .iter()
        .map(|release| release.tag.as_str())
        .collect();
    assert_eq!(
        tags,
        [
            "wasm-aethrix.vision-v1.2.0",
            "wasm-aethrix.vision-v1.3.0-beta.1"
        ]
    );
    assert_eq!(releases[1].version.value(), "1.3.0-beta.1");
    assert_eq!(
        index.releases_for("aethrix.macro")[0].tag,
        "wasm-v0.1.0-beta.2"
    );
    assert!(index.entry("AETHRIX.Navigation").is_some());
    assert!(index.entry("aethrix.other").is_none());
    assert!(index.releases_for("aethrix.other").is_empty());
    assert_eq!(PLUGIN_INDEX_TAG, "wasm-index");
    assert_eq!(PLUGIN_INDEX_ASSET, "plugins-index.json");
    // The index is read only for this repository's own plugins: an empty one
    // is a repository that has published none yet.
    assert!(
        PluginIndex::parse(r#"{ "schemaVersion": 1, "plugins": [] }"#)
            .unwrap()
            .plugins
            .is_empty()
    );
}

#[test]
fn the_index_is_read_strictly() {
    let entry = |body: &str| format!(r#"{{ "schemaVersion": 1, "plugins": [ {body} ] }}"#);
    for text in [
        String::new(),
        "null".into(),
        "[]".into(),
        r#"{ "plugins": [] }"#.into(),
        r#"{ "schemaVersion": 2, "plugins": [] }"#.into(),
        r#"{ "schemaVersion": 1 }"#.into(),
        r#"{ "schemaVersion": 1, "plugins": [], "extra": 1 }"#.into(),
        r#"{ "schemaVersion": 1, "SchemaVersion": 1, "plugins": [] }"#.into(),
        entry(r#"{ "stable": "wasm-v1.0.0" }"#),
        entry(r#"{ "id": "nodot", "stable": "wasm-v1.0.0" }"#),
        entry(r#"{ "id": "a.b" }"#),
        entry(r#"{ "id": "a.b", "stable": "wasm-v1.0.0", "notes": "x" }"#),
        entry(r#"{ "id": "a.b", "stable": "wasm-a.c-v1.0.0" }"#),
        entry(r#"{ "id": "a.b", "stable": "v1.0.0" }"#),
        entry(r#"{ "id": "a.b", "stable": "wasm-a.b-v1.0" }"#),
        entry(r#"{ "id": "a.b", "stable": "wasm-a.b-v1.0.0+build.1" }"#),
        entry(r#"{ "id": "a.b", "stable": "wasm-a.b-v1.0.0-beta.1" }"#),
        entry(r#"{ "id": "a.b", "beta": "wasm-a.b-v1.0.0" }"#),
        entry(r#"{ "id": "a.b", "stable": "" }"#),
        entry(r#"{ "id": "a.b", "stable": 1 }"#),
        entry(
            r#"{ "id": "a.b", "stable": "wasm-v1.0.0" }, { "id": "A.B", "stable": "wasm-v1.1.0" }"#,
        ),
    ] {
        assert!(PluginIndex::parse(&text).is_err(), "{text}");
    }
    // A missing `beta` and an explicit null are the same.
    let index = PluginIndex::parse(&entry(
        r#"{ "id": "a.b", "stable": "wasm-v1.0.0", "beta": null }"#,
    ))
    .unwrap();
    assert_eq!(index.entry("a.b").unwrap().beta, None);
}

#[test]
fn the_index_round_trips_and_an_entry_can_be_replaced() {
    let mut index = PluginIndex::parse(SUITE_INDEX).unwrap();
    index
        .set_release("aethrix.vision", "wasm-aethrix.vision-v1.2.1")
        .unwrap();
    index
        .set_release("aethrix.macro", "wasm-aethrix.macro-v0.1.0")
        .unwrap();
    index
        .set_release("aethrix.new", "wasm-aethrix.new-v0.1.0-beta.1")
        .unwrap();
    assert!(
        index
            .set_release("aethrix.new", "wasm-aethrix.other-v0.2.0")
            .is_err()
    );
    assert!(index.set_release("bad", "wasm-v1.0.0").is_err());
    let again = PluginIndex::parse(&index.to_json()).unwrap();
    assert_eq!(again, index);
    let vision = again.entry("aethrix.vision").unwrap();
    assert_eq!(vision.stable.as_deref(), Some("wasm-aethrix.vision-v1.2.1"));
    assert_eq!(
        vision.beta.as_deref(),
        Some("wasm-aethrix.vision-v1.3.0-beta.1")
    );
    let macro_entry = again.entry("aethrix.macro").unwrap();
    assert_eq!(
        macro_entry.stable.as_deref(),
        Some("wasm-aethrix.macro-v0.1.0")
    );
    assert_eq!(macro_entry.beta.as_deref(), Some("wasm-v0.1.0-beta.2"));
    assert_eq!(
        again.entry("aethrix.new").unwrap().beta.as_deref(),
        Some("wasm-aethrix.new-v0.1.0-beta.1")
    );
    assert_eq!(
        PluginIndex::new().to_json(),
        "{\n  \"schemaVersion\": 1,\n  \"plugins\": []\n}\n"
    );
}

// --- Resolving from the feed and the index together -----------------------------------------------

fn index_uri() -> String {
    tagged(HELLO_REPO, PLUGIN_INDEX_TAG, PLUGIN_INDEX_ASSET)
}

fn serve_manifest(server: &FakeServer, tag: &str, id: &str, version: &str) {
    server.add(
        &tagged(HELLO_REPO, tag, "plugin.json"),
        &plugin_manifest(id, version),
    );
}

#[test]
fn a_plugin_pushed_off_the_feed_is_found_through_the_index() {
    let vision: Vec<(String, String)> = (0..10)
        .map(|patch| {
            let version = format!("1.0.{patch}");
            (format!("wasm-aethrix.vision-v{version}"), version)
        })
        .collect();
    let releases: Vec<(&str, &str, &str)> = vision
        .iter()
        .map(|(tag, version)| (tag.as_str(), "aethrix.vision", version.as_str()))
        .collect();
    let server = suite_server(&releases);
    serve_manifest(
        &server,
        "wasm-aethrix.navigation-v0.4.0",
        "aethrix.navigation",
        "0.4.0",
    );
    server.add(&index_uri(), SUITE_INDEX.as_bytes());
    let result = resolve_plugin(&server, "aethrix.navigation", PluginReleaseChannel::Stable);
    assert_eq!(tag_of(&result), "wasm-aethrix.navigation-v0.4.0");
    assert_eq!(server.count(&index_uri()), 1);
    // A plugin still on the feed takes the highest of both: the index's 1.2.0
    // over the feed's 1.0.9.
    serve_manifest(
        &server,
        "wasm-aethrix.vision-v1.2.0",
        "aethrix.vision",
        "1.2.0",
    );
    let result = resolve_plugin(&server, "aethrix.vision", PluginReleaseChannel::Stable);
    assert_eq!(tag_of(&result), "wasm-aethrix.vision-v1.2.0");
}

#[test]
fn a_stale_index_never_hides_a_newer_release_on_the_feed() {
    let server = suite_server(&[(
        "wasm-aethrix.navigation-v0.5.0",
        "aethrix.navigation",
        "0.5.0",
    )]);
    serve_manifest(
        &server,
        "wasm-aethrix.navigation-v0.4.0",
        "aethrix.navigation",
        "0.4.0",
    );
    server.add(&index_uri(), SUITE_INDEX.as_bytes());
    let result = resolve_plugin(&server, "aethrix.navigation", PluginReleaseChannel::Stable);
    assert_eq!(tag_of(&result), "wasm-aethrix.navigation-v0.5.0");
}

#[test]
fn a_single_plugin_repository_never_fetches_the_index() {
    let server = release_server(&["v0.9.0", "wasm-v0.2.0", "wasm-v0.1.0"]);
    let result = resolve_plugin(&server, "edwards.hello", PluginReleaseChannel::Beta);
    assert_eq!(tag_of(&result), "wasm-v0.2.0");
    resolver(&server)
        .resolve(HELLO_REPO, PluginReleaseChannel::Beta, &none())
        .unwrap();
    assert_eq!(server.count(&index_uri()), 0);
}

#[test]
fn the_index_is_fetched_when_the_feed_has_nothing_for_the_plugin() {
    // Only C# releases: one request for an index that is not there.
    let server = release_server(&["v0.5.1"]);
    assert_eq!(
        resolve_plugin(&server, "edwards.hello", PluginReleaseChannel::Stable),
        PluginReleaseResolveResult::Unavailable
    );
    assert_eq!(server.count(&index_uri()), 1);
    // The id-less resolution never reads it.
    resolver(&server)
        .resolve(HELLO_REPO, PluginReleaseChannel::Stable, &none())
        .unwrap();
    assert_eq!(server.count(&index_uri()), 1);
}

#[test]
fn a_rate_limited_index_is_reported_only_when_the_feed_had_nothing() {
    let server = release_server(&["v0.5.1"]);
    server.add_route(&index_uri(), Route::Status(429));
    assert_eq!(
        resolve_plugin(&server, "edwards.hello", PluginReleaseChannel::Stable),
        PluginReleaseResolveResult::RateLimited
    );
    let server = suite_server(&[("wasm-edwards.hello-v0.3.0", "edwards.hello", "0.3.0")]);
    server.add_route(&index_uri(), Route::Status(429));
    let result = resolve_plugin(&server, "edwards.hello", PluginReleaseChannel::Stable);
    assert_eq!(tag_of(&result), "wasm-edwards.hello-v0.3.0");
}

#[test]
fn a_malformed_index_is_ignored_with_a_warning() {
    let server = suite_server(&[("wasm-edwards.hello-v0.3.0", "edwards.hello", "0.3.0")]);
    server.add(
        &index_uri(),
        br#"{ "schemaVersion": 1, "plugins": [ { "id": "x" } ] }"#,
    );
    let candidates = resolver(&server)
        .resolve_plugin_candidates(
            HELLO_REPO,
            "edwards.hello",
            PluginReleaseChannel::Stable,
            &none(),
        )
        .unwrap();
    assert_eq!(tag_of(&candidates.stable), "wasm-edwards.hello-v0.3.0");
    let warning = candidates.index_warning.unwrap();
    assert!(warning.contains("plugin index is invalid"), "{warning}");
    // A good index gives no warning.
    let server = suite_server(&[("wasm-edwards.hello-v0.3.0", "edwards.hello", "0.3.0")]);
    server.add(&index_uri(), SUITE_INDEX.as_bytes());
    let candidates = resolver(&server)
        .resolve_plugin_candidates(
            HELLO_REPO,
            "edwards.hello",
            PluginReleaseChannel::Stable,
            &none(),
        )
        .unwrap();
    assert_eq!(candidates.index_warning, None);
}

#[test]
fn one_pass_reads_a_repositorys_feed_index_and_manifests_once() {
    let server = suite_server(&[
        ("wasm-aethrix.vision-v1.2.0", "aethrix.vision", "1.2.0"),
        ("wasm-v0.0.9", "aethrix.vision", "0.0.9"),
    ]);
    serve_manifest(
        &server,
        "wasm-aethrix.navigation-v0.4.0",
        "aethrix.navigation",
        "0.4.0",
    );
    server.add(&index_uri(), SUITE_INDEX.as_bytes());
    let pass = resolver(&server).pass();
    for _ in 0..2 {
        for id in ["aethrix.vision", "aethrix.navigation", "aethrix.macro"] {
            pass.resolve_plugin(HELLO_REPO, id, PluginReleaseChannel::Beta, &none())
                .unwrap();
        }
    }
    assert_eq!(server.count(&feed_uri()), 1);
    assert_eq!(server.count(&index_uri()), 1);
    // `aethrix.macro` tries the `wasm-v` tag (`aethrix.vision`'s) in both rounds.
    assert_eq!(
        server.count(&tagged(HELLO_REPO, "wasm-v0.0.9", "plugin.json")),
        1
    );
    // The same repository spelled in another case is the same repository.
    pass.resolve_plugin(
        &HELLO_REPO.to_uppercase(),
        "aethrix.vision",
        PluginReleaseChannel::Stable,
        &none(),
    )
    .unwrap();
    assert_eq!(server.count(&feed_uri()), 1);
    // A new pass reads afresh.
    let fresh = resolver(&server).pass();
    fresh
        .resolve_plugin(
            HELLO_REPO,
            "aethrix.vision",
            PluginReleaseChannel::Stable,
            &none(),
        )
        .unwrap();
    assert_eq!(server.count(&feed_uri()), 2);
}

#[test]
fn a_pass_keeps_no_rate_limited_fetch() {
    let server = FakeServer::new();
    server.add_route(&feed_uri(), Route::Status(429));
    let pass = resolver(&server).pass();
    assert_eq!(
        pass.resolve_plugin(
            HELLO_REPO,
            "edwards.hello",
            PluginReleaseChannel::Stable,
            &none()
        )
        .unwrap(),
        PluginReleaseResolveResult::RateLimited
    );
    server.add(&feed_uri(), &feed(&entry(&link("wasm-v0.2.0"))));
    server.add(
        &tagged(HELLO_REPO, "wasm-v0.2.0", "plugin.json"),
        &resolver_manifest("0.2.0"),
    );
    let result = pass
        .resolve_plugin(
            HELLO_REPO,
            "edwards.hello",
            PluginReleaseChannel::Stable,
            &none(),
        )
        .unwrap();
    assert_eq!(tag_of(&result), "wasm-v0.2.0");
}

// Erik's case: a new plugin in a repository whose older plugin used `wasm-v`
// tags, the new one's releases pushed off the feed.
#[test]
fn a_feed_of_another_plugins_wasm_v_tags_still_reads_the_index() {
    let server = suite_server(&[
        ("wasm-v0.9.2", "aethrix.vision", "0.9.2"),
        ("wasm-v0.9.1", "aethrix.vision", "0.9.1"),
    ]);
    serve_manifest(
        &server,
        "wasm-aethrix.navigation-v0.4.0",
        "aethrix.navigation",
        "0.4.0",
    );
    server.add(&index_uri(), SUITE_INDEX.as_bytes());
    let result = resolve_plugin(&server, "aethrix.navigation", PluginReleaseChannel::Stable);
    assert_eq!(tag_of(&result), "wasm-aethrix.navigation-v0.4.0");
    assert_eq!(server.count(&index_uri()), 1);
    // Each refused manifest was read once, not again after the index.
    assert_eq!(
        server.count(&tagged(HELLO_REPO, "wasm-v0.9.2", "plugin.json")),
        1
    );
    // The plugin those tags are is still found without the index.
    let server = suite_server(&[("wasm-v0.9.2", "aethrix.vision", "0.9.2")]);
    let result = resolve_plugin(&server, "aethrix.vision", PluginReleaseChannel::Stable);
    assert_eq!(tag_of(&result), "wasm-v0.9.2");
    assert_eq!(server.count(&index_uri()), 0);
}

#[test]
fn the_pre_release_message_names_only_this_plugins_pre_release() {
    // Another plugin's `wasm-v` beta is not this plugin's pre-release.
    let server = suite_server(&[("wasm-v1.0.0-beta.1", "aethrix.vision", "1.0.0-beta.1")]);
    assert_eq!(
        resolve_plugin(&server, "aethrix.navigation", PluginReleaseChannel::Stable),
        PluginReleaseResolveResult::Unavailable
    );
    let server = suite_server(&[(
        "wasm-aethrix.navigation-v0.5.0-beta.1",
        "aethrix.navigation",
        "0.5.0-beta.1",
    )]);
    let PluginReleaseResolveResult::Prerelease(message) =
        resolve_plugin(&server, "aethrix.navigation", PluginReleaseChannel::Stable)
    else {
        panic!("expected a pre-release");
    };
    assert!(message.contains("0.5.0-beta.1"), "{message}");
}

// --- Every plugin a repository publishes (Add from URL) ---------------------------------------

fn repository_ids(server: &FakeServer) -> Vec<(String, String)> {
    let found = resolver(server)
        .resolve_repository(HELLO_REPO, PluginReleaseChannel::Stable, &none())
        .unwrap();
    found
        .plugins
        .iter()
        .map(|plugin| (plugin.manifest.id.clone(), plugin.tag.clone()))
        .collect()
}

#[test]
fn a_repository_lists_every_plugin_from_its_tags_and_index() {
    let server = suite_server(&[
        ("wasm-v0.9.0", "aethrix.vision", "0.9.0"),
        ("wasm-aethrix.vision-v1.2.0", "aethrix.vision", "1.2.0"),
        ("wasm-aethrix.macro-v0.2.0", "aethrix.macro", "0.2.0"),
    ]);
    serve_manifest(
        &server,
        "wasm-aethrix.navigation-v0.4.0",
        "aethrix.navigation",
        "0.4.0",
    );
    server.add(&index_uri(), SUITE_INDEX.as_bytes());
    let mut found = repository_ids(&server);
    found.sort();
    assert_eq!(
        found,
        [
            (
                "aethrix.macro".to_owned(),
                "wasm-aethrix.macro-v0.2.0".to_owned()
            ),
            (
                "aethrix.navigation".to_owned(),
                "wasm-aethrix.navigation-v0.4.0".to_owned()
            ),
            // Its newest, not the old `wasm-v` release.
            (
                "aethrix.vision".to_owned(),
                "wasm-aethrix.vision-v1.2.0".to_owned()
            ),
        ]
    );
    assert_eq!(server.count(&feed_uri()), 1);
    assert_eq!(server.count(&index_uri()), 1);
}

#[test]
fn a_single_plugin_repository_lists_its_one_plugin_without_the_index() {
    let server = release_server(&["v0.9.0", "wasm-v0.2.0", "wasm-v0.1.0"]);
    assert_eq!(
        repository_ids(&server),
        [("edwards.hello".to_owned(), "wasm-v0.2.0".to_owned())]
    );
    assert_eq!(server.count(&index_uri()), 0);
    assert_eq!(
        server.count(&tagged(HELLO_REPO, "wasm-v0.2.0", "plugin.json")),
        1
    );
}

#[test]
fn a_tag_split_two_ways_lists_only_the_plugin_its_manifest_names() {
    // `wasm-edwards.hello-v1.0.0-v2.0.0` reads as `edwards.hello` 1.0.0-v2.0.0
    // or `edwards.hello-v1.0.0` 2.0.0; its manifest says which.
    let server = suite_server(&[(
        "wasm-edwards.hello-v1.0.0-v2.0.0",
        "edwards.hello-v1.0.0",
        "2.0.0",
    )]);
    assert_eq!(
        repository_ids(&server),
        [(
            "edwards.hello-v1.0.0".to_owned(),
            "wasm-edwards.hello-v1.0.0-v2.0.0".to_owned()
        )]
    );
}

#[test]
fn a_repository_with_nothing_for_this_client_says_why() {
    let found = |server: &FakeServer| {
        resolver(server)
            .resolve_repository(HELLO_REPO, PluginReleaseChannel::Stable, &none())
            .unwrap()
    };
    let server = release_server(&["v0.5.1"]);
    let result = found(&server);
    assert!(result.plugins.is_empty());
    assert_eq!(result.failure, PluginReleaseResolveResult::Unavailable);
    let server = release_server(&["wasm-v1.3.0-beta.1"]);
    assert!(matches!(
        found(&server).failure,
        PluginReleaseResolveResult::Prerelease(_)
    ));
    let server = FakeServer::new();
    server.add_route(&feed_uri(), Route::Status(429));
    assert_eq!(
        found(&server).failure,
        PluginReleaseResolveResult::RateLimited
    );
}

// --- PluginInstallerTests ---------------------------------------------------------------------

struct Release {
    version: String,
    manifest_bytes: Vec<u8>,
    zip_bytes: Vec<u8>,
    sha256: String,
    zip_name: String,
}

impl Release {
    fn tag(&self) -> String {
        release_tag(&self.version)
    }
}

struct InstallerFixture {
    root: TempDir,
    paths: ac_host::PathSet,
    server: FakeServer,
    records: Arc<Mutex<InstalledPluginRecordStore>>,
    installer: PluginInstaller,
}

fn installer_manifest(
    id: &str,
    version: &str,
    display_name: Option<&str>,
    permissions: Option<(u32, &str)>,
) -> String {
    let permission_fields = permissions.map_or(String::new(), |(version, json)| {
        format!(",\n  \"permissionsVersion\": {version},\n  \"permissions\": {json}")
    });
    format!(
        r#"{{
  "id": "{id}",
  "displayName": "{}",
  "version": "{version}",
  "entry": "{id}.wasm",
  "apiVersion": "1.0",
  "minHostVersion": "0.1.0"{permission_fields}
}}"#,
        display_name.unwrap_or(id)
    )
}

impl InstallerFixture {
    fn new() -> Self {
        let root = TempDir::new("plugin-installer");
        let paths = root.path_set();
        let server = FakeServer::new();
        let records = Arc::new(Mutex::new(InstalledPluginRecordStore::for_paths(&paths)));
        let installer = PluginInstaller::new(&paths, server.transport(), Arc::clone(&records));
        Self {
            root,
            paths,
            server,
            records,
            installer,
        }
    }

    fn release_from(
        &self,
        version: &str,
        manifest_bytes: Vec<u8>,
        zip_entries: Vec<(String, Vec<u8>)>,
    ) -> Release {
        let entries: Vec<(&str, &[u8], Option<u32>)> = zip_entries
            .iter()
            .map(|(name, content)| (name.as_str(), content.as_slice(), None))
            .collect();
        let zip_bytes = create_zip(&entries, Compression::Stored);
        Release {
            version: version.into(),
            sha256: sha256_hex(&zip_bytes),
            zip_name: format!("{HELLO}-{version}.zip"),
            manifest_bytes,
            zip_bytes,
        }
    }

    fn build_release(&self, version: &str, icon: Option<Vec<u8>>, bom: bool) -> Release {
        let mut manifest = installer_manifest(HELLO, version, None, None).into_bytes();
        if bom {
            manifest.splice(0..0, [0xEF, 0xBB, 0xBF]);
        }
        let mut entries = vec![
            ("plugin.json".to_owned(), manifest.clone()),
            (
                format!("{HELLO}.wasm"),
                format!("binary-{HELLO}").into_bytes(),
            ),
        ];
        if let Some(icon) = icon {
            entries.push(("icon.png".to_owned(), icon));
        }
        self.release_from(version, manifest, entries)
    }

    fn release(&self, version: &str) -> Release {
        self.build_release(version, None, false)
    }

    fn release_with_permissions(&self, version: &str, permissions: &str) -> Release {
        let manifest = installer_manifest(
            HELLO,
            version,
            None,
            Some((ac_launcher_core::plugins::PERMISSIONS_VERSION, permissions)),
        )
        .into_bytes();
        self.release_from(
            version,
            manifest.clone(),
            vec![
                ("plugin.json".to_owned(), manifest),
                (format!("{HELLO}.wasm"), b"binary".to_vec()),
            ],
        )
    }

    fn register(
        &self,
        repo: &str,
        release: &Release,
        sha_override: Option<&str>,
        icon: Option<Route>,
    ) {
        let tag = release.tag();
        self.server.add_route(
            &latest(repo, "plugin.json"),
            redirect(&tagged(repo, &tag, "plugin.json")),
        );
        self.server
            .add(&tagged(repo, &tag, "plugin.json"), &release.manifest_bytes);
        self.server.add(
            &tagged(repo, &tag, &format!("{}.sha256", release.zip_name)),
            format!(
                "{}  {}\n",
                sha_override.unwrap_or(&release.sha256),
                release.zip_name
            )
            .as_bytes(),
        );
        self.server
            .add(&tagged(repo, &tag, &release.zip_name), &release.zip_bytes);
        if let Some(icon) = icon {
            self.server.add_route(&tagged(repo, &tag, "icon.png"), icon);
        }
    }

    fn install(
        &self,
        repo: &str,
        release: &Release,
    ) -> Result<ac_launcher_core::plugins::PluginInstallResult, UpdateError> {
        self.installer
            .install_or_update(repo, &release.tag(), None, None, None, None, &none())
    }

    fn find(&self, id: &str) -> Option<InstalledPluginRecord> {
        self.records.lock().unwrap().find(id).cloned()
    }

    fn plugin_dir(&self) -> PathBuf {
        self.paths.plugins_directory()
    }
}

#[test]
fn install_rejects_a_zip_entry_over_the_shared_contract_cap() {
    let fixture = InstallerFixture::new();
    let manifest = installer_manifest(HELLO, "0.1.0", None, None).into_bytes();
    // 70 MiB: over the contract's 64 MiB per-entry and zip caps.
    let oversized = vec![7u8; 70 * 1024 * 1024];
    let release = fixture.release_from(
        "0.1.0",
        manifest.clone(),
        vec![
            ("plugin.json".into(), manifest),
            (format!("{HELLO}.wasm"), oversized),
        ],
    );
    fixture.register(HELLO_REPO, &release, None, None);
    assert!(fixture.install(HELLO_REPO, &release).is_err());
}

#[test]
fn install_succeeds_and_writes_a_confirmed_record() {
    let fixture = InstallerFixture::new();
    let release = fixture.release("0.1.0");
    fixture.register(HELLO_REPO, &release, None, None);
    let result = fixture.install(HELLO_REPO, &release).unwrap();
    assert_eq!(result.id, HELLO);
    assert_eq!(result.version, "0.1.0");
    assert!(!result.was_update);
    assert!(
        fixture
            .plugin_dir()
            .join(HELLO)
            .join("plugin.json")
            .is_file()
    );
    let record = fixture.find(HELLO).unwrap();
    assert_eq!(record.version.as_deref(), Some("0.1.0"));
    assert_eq!(record.tag.as_deref(), Some("wasm-v0.1.0"));
    assert!(record.pending.is_none());
    // Only the pinned tag is ever fetched, never latest.
    assert_eq!(fixture.server.count(&latest(HELLO_REPO, "plugin.json")), 0);
    // No empty staging or trash folder is left behind.
    assert!(!fixture.plugin_dir().join(".staging").exists());
    assert!(!fixture.plugin_dir().join(".trash").exists());
}

#[test]
fn update_and_remove_leave_no_empty_staging_or_trash_folder() {
    let fixture = InstallerFixture::new();
    let first = fixture.release("0.1.0");
    fixture.register(HELLO_REPO, &first, None, None);
    fixture.install(HELLO_REPO, &first).unwrap();
    let second = fixture.release("0.2.0");
    fixture.register(HELLO_REPO, &second, None, None);
    assert!(fixture.install(HELLO_REPO, &second).unwrap().was_update);
    assert!(!fixture.plugin_dir().join(".staging").exists());
    assert!(!fixture.plugin_dir().join(".trash").exists());
    fixture.installer.remove(HELLO, false).unwrap();
    assert!(!fixture.plugin_dir().join(".trash").exists());
}

#[test]
fn recover_reclaims_leftover_empty_staging_and_trash_folders() {
    let fixture = InstallerFixture::new();
    std::fs::create_dir_all(fixture.plugin_dir().join(".staging")).unwrap();
    std::fs::create_dir_all(fixture.plugin_dir().join(".trash")).unwrap();
    fixture.installer.recover().unwrap();
    assert!(!fixture.plugin_dir().join(".staging").exists());
    assert!(!fixture.plugin_dir().join(".trash").exists());
}

#[test]
fn install_never_deletes_a_staging_folder_something_else_is_still_using() {
    let fixture = InstallerFixture::new();
    let stray = fixture
        .plugin_dir()
        .join(".staging")
        .join("someone-elses-transaction");
    std::fs::create_dir_all(&stray).unwrap();
    std::fs::write(stray.join("in-progress.txt"), "still here").unwrap();
    let release = fixture.release("0.1.0");
    fixture.register(HELLO_REPO, &release, None, None);
    fixture.install(HELLO_REPO, &release).unwrap();
    assert!(stray.join("in-progress.txt").is_file());
}

#[test]
fn hash_mismatch_aborts_leaves_no_staging_and_a_retry_succeeds() {
    let fixture = InstallerFixture::new();
    let release = fixture.release("0.1.0");
    fixture.register(HELLO_REPO, &release, Some(&"f".repeat(64)), None);
    assert!(fixture.install(HELLO_REPO, &release).is_err());
    let staging = fixture.plugin_dir().join(".staging");
    assert!(!staging.exists() || ac_launcher_core::fsutil::is_empty_directory(&staging));
    assert!(!fixture.plugin_dir().join(HELLO).exists());
    assert!(fixture.find(HELLO).is_none());

    fixture.register(HELLO_REPO, &release, None, None);
    assert_eq!(fixture.install(HELLO_REPO, &release).unwrap().id, HELLO);
    assert!(
        fixture
            .plugin_dir()
            .join(HELLO)
            .join("plugin.json")
            .is_file()
    );
}

#[test]
fn zip_manifest_must_match_release_manifest() {
    let fixture = InstallerFixture::new();
    let published = installer_manifest(HELLO, "0.1.0", None, None).into_bytes();
    let zipped = installer_manifest(HELLO, "0.1.0", Some("Not The Same"), None).into_bytes();
    let release = fixture.release_from(
        "0.1.0",
        published,
        vec![
            ("plugin.json".into(), zipped),
            (format!("{HELLO}.wasm"), b"binary".to_vec()),
        ],
    );
    fixture.register(HELLO_REPO, &release, None, None);
    let error = fixture
        .install(HELLO_REPO, &release)
        .unwrap_err()
        .to_string();
    assert!(error.contains("plugin.json"), "{error}");
    assert!(!fixture.plugin_dir().join(HELLO).exists());
}

#[test]
fn icon_rules_during_install() {
    let icon_url = tagged(HELLO_REPO, "wasm-v0.1.0", "icon.png");

    let fixture = InstallerFixture::new();
    let release = fixture.build_release("0.1.0", Some(png::valid()), false);
    fixture.register(
        HELLO_REPO,
        &release,
        None,
        Some(Route::Body {
            body: png::valid(),
            declared_length: None,
            chunk_size: usize::MAX,
            chunk_delay: std::time::Duration::ZERO,
        }),
    );
    fixture.install(HELLO_REPO, &release).unwrap();
    assert!(fixture.plugin_dir().join(HELLO).join("icon.png").is_file());

    let fixture = InstallerFixture::new();
    let release = fixture.build_release("0.1.0", Some(png::valid()), false);
    fixture.register(HELLO_REPO, &release, None, None);
    let error = fixture
        .install(HELLO_REPO, &release)
        .unwrap_err()
        .to_string();
    assert!(error.contains("missing its icon.png asset"), "{error}");
    assert!(!fixture.plugin_dir().join(HELLO).exists());

    let fixture = InstallerFixture::new();
    let release = fixture.build_release("0.1.0", Some(png::valid()), false);
    fixture.register(
        HELLO_REPO,
        &release,
        None,
        Some(Route::Body {
            body: png::wrong_dimensions(),
            declared_length: None,
            chunk_size: usize::MAX,
            chunk_delay: std::time::Duration::ZERO,
        }),
    );
    let error = fixture
        .install(HELLO_REPO, &release)
        .unwrap_err()
        .to_string();
    assert!(error.contains("icon.png does not match"), "{error}");

    let fixture = InstallerFixture::new();
    let release = fixture.build_release("0.1.0", Some(png::valid()), false);
    fixture.register(HELLO_REPO, &release, None, Some(Route::Status(429)));
    assert_eq!(
        fixture
            .install(HELLO_REPO, &release)
            .unwrap_err()
            .to_string(),
        "GitHub is rate limiting; try later."
    );

    let fixture = InstallerFixture::new();
    let release = fixture.build_release("0.1.0", Some(png::wrong_dimensions()), false);
    fixture.register(HELLO_REPO, &release, None, None);
    assert!(fixture.install(HELLO_REPO, &release).is_err());
    assert_eq!(fixture.server.count(&icon_url), 0);

    let fixture = InstallerFixture::new();
    let release = fixture.release("0.1.0");
    fixture.register(HELLO_REPO, &release, None, None);
    fixture.install(HELLO_REPO, &release).unwrap();
    assert_eq!(fixture.server.count(&icon_url), 0);
}

#[test]
fn permission_refusals_during_install() {
    let fixture = InstallerFixture::new();
    let manifest = installer_manifest(
        HELLO,
        "0.1.0",
        None,
        Some((ac_launcher_core::plugins::PERMISSIONS_VERSION + 1, "[]")),
    )
    .into_bytes();
    let release = fixture.release_from("0.1.0", manifest, Vec::new());
    fixture.register(HELLO_REPO, &release, None, None);
    let error = fixture
        .install(HELLO_REPO, &release)
        .unwrap_err()
        .to_string();
    assert_eq!(error, PERMISSION_VOCABULARY_REFUSAL);
    assert!(!error.to_lowercase().contains("invalid"));

    let fixture = InstallerFixture::new();
    let manifest = installer_manifest(
        HELLO,
        "0.1.0",
        None,
        Some((
            ac_launcher_core::plugins::PERMISSIONS_VERSION,
            r#"[{ "name": "quantumTeleport", "note": "test" }]"#,
        )),
    )
    .into_bytes();
    let release = fixture.release_from("0.1.0", manifest, Vec::new());
    fixture.register(HELLO_REPO, &release, None, None);
    let error = fixture
        .install(HELLO_REPO, &release)
        .unwrap_err()
        .to_string();
    assert!(error.contains("The plugin manifest is invalid"), "{error}");

    let fixture = InstallerFixture::new();
    let release = fixture.release_with_permissions(
        "0.1.0",
        r#"[{ "name": "network", "note": "Sends usage counts." }]"#,
    );
    fixture.register(HELLO_REPO, &release, None, None);
    let displayed = [declaration(Permission::Chat, "Reads chat.")];
    let error = fixture
        .installer
        .install_or_update(
            HELLO_REPO,
            &release.tag(),
            None,
            None,
            Some(&displayed),
            None,
            &none(),
        )
        .unwrap_err()
        .to_string();
    assert_eq!(error, PERMISSIONS_CHANGED_REFUSAL);
    assert!(!fixture.plugin_dir().join(HELLO).exists());

    let fixture = InstallerFixture::new();
    let release = fixture.release_with_permissions(
        "0.1.0",
        r#"[{ "name": "chat", "note": "Reads chat." }, { "name": "network", "note": "Sends usage counts." }]"#,
    );
    fixture.register(HELLO_REPO, &release, None, None);
    let displayed = [
        declaration(Permission::Network, "Sends usage counts."),
        declaration(Permission::Chat, "Reads chat."),
    ];
    let result = fixture
        .installer
        .install_or_update(
            HELLO_REPO,
            &release.tag(),
            None,
            None,
            Some(&displayed),
            None,
            &none(),
        )
        .unwrap();
    assert_eq!(result.id, HELLO);
}

#[test]
fn unrecorded_folder_with_same_name_is_refused() {
    let fixture = InstallerFixture::new();
    let colliding = fixture.plugin_dir().join(HELLO);
    std::fs::create_dir_all(&colliding).unwrap();
    std::fs::write(colliding.join("leftover.txt"), "not a plugin").unwrap();
    let release = fixture.release("0.1.0");
    fixture.register(HELLO_REPO, &release, None, None);
    let error = fixture.install(HELLO_REPO, &release).unwrap_err();
    assert_eq!(
        error.to_string(),
        format!(
            "A folder named {HELLO} is already in your plugins folder, and the launcher didn't \
             install it. Move or delete that folder, then try again."
        )
    );
    assert!(
        !error
            .to_string()
            .contains(&*fixture.plugin_dir().to_string_lossy())
    );
    let UpdateError::Failed {
        io: Some(cause), ..
    } = &error
    else {
        panic!("expected a cause");
    };
    assert!(
        cause
            .to_string()
            .contains(&*fixture.plugin_dir().to_string_lossy())
    );
    assert!(colliding.join("leftover.txt").is_file());
}

#[test]
fn a_folder_that_appears_during_the_download_is_refused_and_left_alone() {
    let fixture = InstallerFixture::new();
    let release = fixture.release("0.1.0");
    fixture.register(HELLO_REPO, &release, None, None);
    let hand_placed = fixture.plugin_dir().join(HELLO);
    let zip_url = WebUrl::parse(&tagged(HELLO_REPO, &release.tag(), &release.zip_name))
        .unwrap()
        .to_string();
    let target = hand_placed.clone();
    fixture.server.on_request(move |url| {
        if url == zip_url {
            std::fs::create_dir_all(&target).unwrap();
            std::fs::write(target.join("mine.txt"), "placed by hand").unwrap();
        }
    });
    let error = fixture
        .install(HELLO_REPO, &release)
        .unwrap_err()
        .to_string();
    assert!(error.contains("launcher didn't install it"), "{error}");
    assert!(hand_placed.join("mine.txt").is_file());
    assert!(fixture.find(HELLO).is_none());
}

#[test]
fn a_manifest_saved_with_a_byte_order_mark_installs() {
    let fixture = InstallerFixture::new();
    let release = fixture.build_release("0.1.0", None, true);
    fixture.register(HELLO_REPO, &release, None, None);
    assert_eq!(
        fixture.install(HELLO_REPO, &release).unwrap().version,
        "0.1.0"
    );
}

#[test]
fn the_same_repository_spelled_in_another_case_is_an_update() {
    let fixture = InstallerFixture::new();
    let first = fixture.release("0.1.0");
    fixture.register(HELLO_REPO, &first, None, None);
    fixture.install(HELLO_REPO, &first).unwrap();
    let other_case = HELLO_REPO.to_uppercase();
    let second = fixture.release("0.2.0");
    fixture.register(&other_case, &second, None, None);
    let result = fixture.install(&other_case, &second).unwrap();
    assert!(result.was_update);
    assert_eq!(
        fixture.find(HELLO).unwrap().version.as_deref(),
        Some("0.2.0")
    );
}

#[test]
fn install_refused_when_id_collides_with_another_source() {
    for id_spelling in [HELLO, "Edwards.Hello"] {
        let fixture = InstallerFixture::new();
        let manual = fixture.plugin_dir().join("someones-copy");
        std::fs::create_dir_all(&manual).unwrap();
        std::fs::write(
            manual.join("plugin.json"),
            installer_manifest(id_spelling, "0.0.1", None, None),
        )
        .unwrap();
        let release = fixture.release("0.1.0");
        fixture.register(HELLO_REPO, &release, None, None);
        let error = fixture
            .install(HELLO_REPO, &release)
            .unwrap_err()
            .to_string();
        assert!(error.contains("directly installed"), "{error}");
    }

    let fixture = InstallerFixture::new();
    let client = fixture.root.join("client");
    let bundled = client.join("plugins").join(HELLO);
    std::fs::create_dir_all(&bundled).unwrap();
    std::fs::write(
        bundled.join("plugin.json"),
        installer_manifest(HELLO, "0.1.0", None, None),
    )
    .unwrap();
    let release = fixture.release("0.1.0");
    fixture.register(HELLO_REPO, &release, None, None);
    let resolution = verified_client(&client);
    let error = fixture
        .installer
        .install_or_update(
            HELLO_REPO,
            &release.tag(),
            None,
            Some(&resolution),
            None,
            None,
            &none(),
        )
        .unwrap_err()
        .to_string();
    assert!(error.contains("client-bundled"), "{error}");

    let fixture = InstallerFixture::new();
    {
        let mut records = fixture.records.lock().unwrap();
        let mut other = record(HELLO, "someoneElse/openac-plugin-hello", Some("0.1.0"));
        other.source = PluginInstallSource::Unlisted;
        records.records.push(other);
        records.save().unwrap();
    }
    let release = fixture.release("0.1.0");
    fixture.register(HELLO_REPO, &release, None, None);
    let error = fixture
        .install(HELLO_REPO, &release)
        .unwrap_err()
        .to_string();
    assert!(error.contains("someoneElse/openac-plugin-hello"), "{error}");
}

#[test]
fn update_refused_when_not_newer_than_installed() {
    let fixture = InstallerFixture::new();
    let first = fixture.release("0.2.0");
    fixture.register(HELLO_REPO, &first, None, None);
    fixture.install(HELLO_REPO, &first).unwrap();
    let downgrade = fixture.release("0.1.0");
    fixture.register(HELLO_REPO, &downgrade, None, None);
    let error = fixture
        .install(HELLO_REPO, &downgrade)
        .unwrap_err()
        .to_string();
    assert!(error.contains("not newer"), "{error}");
    assert_eq!(
        fixture.find(HELLO).unwrap().version.as_deref(),
        Some("0.2.0")
    );
}

#[test]
fn channels_follow_the_version_unless_chosen() {
    let fixture = InstallerFixture::new();
    let beta = fixture.release("0.2.0-beta.1");
    fixture.register(HELLO_REPO, &beta, None, None);
    fixture.install(HELLO_REPO, &beta).unwrap();
    assert_eq!(
        fixture.find(HELLO).unwrap().channel,
        PluginReleaseChannel::Beta
    );
    let stable = fixture.release("0.2.0");
    fixture.register(HELLO_REPO, &stable, None, None);
    fixture.install(HELLO_REPO, &stable).unwrap();
    assert_eq!(
        fixture.find(HELLO).unwrap().channel,
        PluginReleaseChannel::Beta
    );

    let fixture = InstallerFixture::new();
    let stable = fixture.release("0.1.0");
    fixture.register(HELLO_REPO, &stable, None, None);
    fixture.install(HELLO_REPO, &stable).unwrap();
    assert_eq!(
        fixture.find(HELLO).unwrap().channel,
        PluginReleaseChannel::Stable
    );

    let fixture = InstallerFixture::new();
    let stable = fixture.release("0.1.0");
    fixture.register(HELLO_REPO, &stable, None, None);
    fixture
        .installer
        .install_or_update(
            HELLO_REPO,
            &stable.tag(),
            None,
            None,
            None,
            Some(PluginReleaseChannel::Beta),
            &none(),
        )
        .unwrap();
    assert_eq!(
        fixture.find(HELLO).unwrap().channel,
        PluginReleaseChannel::Beta
    );
}

/// A release with `extra` files beside its manifest and module.
fn release_with_files(
    fixture: &InstallerFixture,
    version: &str,
    extra: &[(&str, &[u8])],
) -> Release {
    let manifest = installer_manifest(HELLO, version, None, None).into_bytes();
    let mut entries = vec![
        ("plugin.json".to_owned(), manifest.clone()),
        (
            format!("{HELLO}.wasm"),
            format!("binary-{version}").into_bytes(),
        ),
    ];
    entries.extend(
        extra
            .iter()
            .map(|(name, content)| ((*name).to_owned(), content.to_vec())),
    );
    fixture.release_from(version, manifest, entries)
}

// OpenAC PluginInstallerTests.InstallAndUpdateRunWhileASessionIsPlaying: a running client holds
// the session lock shared.
#[test]
fn install_and_update_run_while_a_session_is_playing() {
    let fixture = InstallerFixture::new();
    let first = fixture.release("0.1.0");
    fixture.register(HELLO_REPO, &first, None, None);
    let barrier = UpdateSessionBarrier::new(&fixture.paths.data_directory);
    let _session = barrier.acquire_session().unwrap();
    fixture.install(HELLO_REPO, &first).unwrap();
    let second = fixture.release("0.2.0");
    fixture.register(HELLO_REPO, &second, None, None);
    let result = fixture.install(HELLO_REPO, &second).unwrap();
    assert!(result.was_update);
    assert_eq!(
        fixture.find(HELLO).unwrap().version.as_deref(),
        Some("0.2.0")
    );
    assert!(
        std::fs::read_to_string(fixture.plugin_dir().join(HELLO).join("plugin.json"))
            .unwrap()
            .contains("\"0.2.0\"")
    );
}

// OpenAC PluginInstallerTests.InstallIsRefusedWhileAClientUpdateRuns.
#[test]
fn install_is_refused_while_a_client_update_runs() {
    let fixture = InstallerFixture::new();
    let release = fixture.release("0.1.0");
    fixture.register(HELLO_REPO, &release, None, None);
    let barrier = UpdateSessionBarrier::new(&fixture.paths.data_directory);
    {
        let _update = barrier.acquire_exclusive().unwrap();
        assert_eq!(
            fixture
                .install(HELLO_REPO, &release)
                .unwrap_err()
                .to_string(),
            ac_launcher_core::plugins::UPDATE_IN_PROGRESS_REFUSAL
        );
    }
    assert!(fixture.find(HELLO).is_none());
    assert!(!fixture.plugin_dir().join(HELLO).exists());
}

// OpenAC PluginInstallerTests.InstallIsRefusedWhileAnotherPluginWriteRuns.
#[test]
fn install_is_refused_while_another_plugin_write_runs() {
    let fixture = InstallerFixture::new();
    let release = fixture.release("0.1.0");
    fixture.register(HELLO_REPO, &release, None, None);
    let barrier = UpdateSessionBarrier::new(&fixture.paths.data_directory);
    let lock_path = barrier
        .lock_path()
        .parent()
        .unwrap()
        .join(ac_launcher_core::plugins::PLUGIN_WRITE_LOCK_FILE_NAME);
    std::fs::create_dir_all(lock_path.parent().unwrap()).unwrap();
    {
        let other = std::fs::OpenOptions::new()
            .read(true)
            .write(true)
            .create(true)
            .truncate(false)
            .open(&lock_path)
            .unwrap();
        other.lock().unwrap();
        assert_eq!(
            fixture
                .install(HELLO_REPO, &release)
                .unwrap_err()
                .to_string(),
            ac_launcher_core::plugins::PLUGIN_WRITE_BUSY_REFUSAL
        );
    }
    // The session lock is let go with the refusal: a client update is not
    // left blocked by a write that never happened.
    assert!(barrier.try_acquire_exclusive().unwrap().is_some());
}

// OpenAC PluginInstallerTests.AnUpdateLeavesThePlayersFilesFolderWhereItIs: a plugin with a file
// in its files folder open keeps it open through the update.
#[test]
fn an_update_leaves_the_players_files_folder_where_it_is() {
    use std::io::Write as _;

    let fixture = InstallerFixture::new();
    let first = fixture.release("0.1.0");
    fixture.register(HELLO_REPO, &first, None, None);
    fixture.install(HELLO_REPO, &first).unwrap();
    let files = fixture.paths.plugin_files_directory(HELLO);
    std::fs::create_dir_all(&files).unwrap();
    let log = files.join("plugin.log");
    let second = fixture.release("0.2.0");
    fixture.register(HELLO_REPO, &second, None, None);
    {
        let mut open = std::fs::File::create(&log).unwrap();
        open.write_all(b"before").unwrap();
        fixture.install(HELLO_REPO, &second).unwrap();
        open.write_all(b" after").unwrap();
    }
    assert_eq!(std::fs::read_to_string(&log).unwrap(), "before after");
    assert_eq!(
        fixture.find(HELLO).unwrap().version.as_deref(),
        Some("0.2.0")
    );
}

// OpenAC PluginInstallerTests.AnUpdateRemovesFilesTheNewVersionNoLongerShips.
#[test]
fn an_update_removes_files_the_new_version_no_longer_ships() {
    let fixture = InstallerFixture::new();
    let first = release_with_files(
        &fixture,
        "0.1.0",
        &[("lib/old.txt", b"old"), ("shared.xml", b"v1")],
    );
    fixture.register(HELLO_REPO, &first, None, None);
    fixture.install(HELLO_REPO, &first).unwrap();
    let second = release_with_files(&fixture, "0.2.0", &[("shared.xml", b"v2")]);
    fixture.register(HELLO_REPO, &second, None, None);
    fixture.install(HELLO_REPO, &second).unwrap();

    let plugin = fixture.plugin_dir().join(HELLO);
    assert!(!plugin.join("lib").join("old.txt").exists());
    assert!(!plugin.join("lib").exists());
    assert_eq!(
        std::fs::read_to_string(plugin.join("shared.xml")).unwrap(),
        "v2"
    );
    assert!(!fixture.plugin_dir().join(".staging").exists());
}
fn pending_record(version: Option<&str>, pending: &str) -> InstalledPluginRecord {
    let mut record = record(HELLO, HELLO_REPO, version);
    record.pending = Some(PendingPluginInstall {
        version: pending.into(),
        tag: release_tag(pending),
        zip_sha256: "b".repeat(64),
    });
    record
}

#[test]
fn recovery_reconciles_pending_entries() {
    let fixture = InstallerFixture::new();
    write_manifest(&fixture.plugin_dir().join(HELLO), HELLO, "0.2.0");
    {
        let mut records = fixture.records.lock().unwrap();
        records.records.push(pending_record(Some("0.1.0"), "0.2.0"));
        records.save().unwrap();
    }
    fixture.installer.recover().unwrap();
    let record = fixture.find(HELLO).unwrap();
    assert!(record.pending.is_none());
    assert_eq!(record.version.as_deref(), Some("0.2.0"));
    assert_eq!(record.tag.as_deref(), Some("wasm-v0.2.0"));

    let fixture = InstallerFixture::new();
    write_manifest(&fixture.plugin_dir().join(HELLO), HELLO, "0.1.0");
    {
        let mut records = fixture.records.lock().unwrap();
        records.records.push(pending_record(Some("0.1.0"), "0.2.0"));
        records.save().unwrap();
    }
    fixture.installer.recover().unwrap();
    let record = fixture.find(HELLO).unwrap();
    assert!(record.pending.is_none());
    assert_eq!(record.version.as_deref(), Some("0.1.0"));

    let fixture = InstallerFixture::new();
    {
        let mut records = fixture.records.lock().unwrap();
        records.records.push(pending_record(None, "0.1.0"));
        records.save().unwrap();
    }
    fixture.installer.recover().unwrap();
    assert!(fixture.find(HELLO).is_none());
}

#[test]
fn recovery_restores_or_discards_trash() {
    let fixture = InstallerFixture::new();
    {
        let mut records = fixture.records.lock().unwrap();
        records
            .records
            .push(record(HELLO, HELLO_REPO, Some("0.1.0")));
        records.save().unwrap();
    }
    let trash = fixture
        .plugin_dir()
        .join(".trash")
        .join(format!("{HELLO}-abc123"));
    write_manifest(&trash, HELLO, "0.1.0");
    fixture.installer.recover().unwrap();
    assert!(
        fixture
            .plugin_dir()
            .join(HELLO)
            .join("plugin.json")
            .is_file()
    );
    assert!(!trash.exists());

    let fixture = InstallerFixture::new();
    let trash = fixture
        .plugin_dir()
        .join(".trash")
        .join("someones-copy-abc123");
    write_manifest(&trash, HELLO, "0.1.0");
    fixture.installer.recover().unwrap();
    assert!(!fixture.plugin_dir().join("someones-copy").exists());
    assert!(!trash.exists());

    let fixture = InstallerFixture::new();
    write_manifest(&fixture.plugin_dir().join(HELLO), HELLO, "0.2.0");
    let trash = fixture
        .plugin_dir()
        .join(".trash")
        .join(format!("{HELLO}-abc123"));
    write_manifest(&trash, HELLO, "0.1.0");
    fixture.installer.recover().unwrap();
    assert!(
        fixture
            .plugin_dir()
            .join(HELLO)
            .join("plugin.json")
            .is_file()
    );
    assert!(!trash.exists());
}

#[test]
fn remove_keeps_storage_unless_asked_and_refuses_unmanaged_ids() {
    let fixture = InstallerFixture::new();
    assert!(fixture.installer.remove(HELLO, false).is_err());
    let release = fixture.release("0.1.0");
    fixture.register(HELLO_REPO, &release, None, None);
    fixture.install(HELLO_REPO, &release).unwrap();
    let storage = fixture.paths.plugin_files_directory(HELLO);
    std::fs::create_dir_all(&storage).unwrap();
    std::fs::write(storage.join("settings.json"), "{}").unwrap();
    fixture.installer.remove(HELLO, false).unwrap();
    // OpenAC RemoveKeepsStorageByDefault (plugins/<id>/files):
    // the code goes, the saved files stay.
    assert!(
        !fixture
            .plugin_dir()
            .join(HELLO)
            .join("plugin.json")
            .exists()
    );
    assert!(
        !fixture
            .plugin_dir()
            .join(HELLO)
            .join(format!("{HELLO}.wasm"))
            .exists()
    );
    assert!(fixture.find(HELLO).is_none());
    assert!(storage.join("settings.json").is_file());

    let fixture = InstallerFixture::new();
    let release = fixture.release("0.1.0");
    fixture.register(HELLO_REPO, &release, None, None);
    fixture.install(HELLO_REPO, &release).unwrap();
    let storage = fixture.paths.plugin_files_directory(HELLO);
    std::fs::create_dir_all(&storage).unwrap();
    fixture.installer.remove(HELLO, true).unwrap();
    assert!(!storage.exists());
}

// OpenAC PluginInstallerTests.UpdateCarriesThePluginsFilesIntoTheNewVersion.
#[test]
fn an_update_carries_the_plugins_files_into_the_new_version() {
    let fixture = InstallerFixture::new();
    let first = fixture.release("0.1.0");
    fixture.register(HELLO_REPO, &first, None, None);
    fixture.install(HELLO_REPO, &first).unwrap();
    let files = fixture.paths.plugin_files_directory(HELLO);
    std::fs::create_dir_all(files.join("profiles")).unwrap();
    std::fs::write(files.join("profiles").join("a.usd"), "mine").unwrap();

    let second = fixture.release("0.2.0");
    fixture.register(HELLO_REPO, &second, None, None);
    fixture.install(HELLO_REPO, &second).unwrap();

    assert_eq!(
        std::fs::read_to_string(files.join("profiles").join("a.usd")).unwrap(),
        "mine"
    );
    assert_eq!(
        fixture.find(HELLO).unwrap().version.as_deref(),
        Some("0.2.0")
    );
    assert!(!fixture.plugin_dir().join(".trash").exists());
}

// OpenAC PluginInstallerTests.ReinstallAfterRemoveFindsTheKeptFiles.
#[test]
fn a_reinstall_after_remove_finds_the_kept_files() {
    let fixture = InstallerFixture::new();
    let release = fixture.release("0.1.0");
    fixture.register(HELLO_REPO, &release, None, None);
    fixture.install(HELLO_REPO, &release).unwrap();
    let files = fixture.paths.plugin_files_directory(HELLO);
    std::fs::create_dir_all(&files).unwrap();
    std::fs::write(files.join("state.json"), "{}").unwrap();
    fixture.installer.remove(HELLO, false).unwrap();

    fixture.install(HELLO_REPO, &release).unwrap();

    assert!(
        fixture
            .plugin_dir()
            .join(HELLO)
            .join("plugin.json")
            .is_file()
    );
    assert!(files.join("state.json").is_file());
}

// OpenAC PluginInstallerTests.APackageThatShipsAFilesFolderIsRefused.
#[test]
fn a_package_that_ships_a_files_folder_is_refused() {
    // The player's folders are `data` and `vtank`; neither may come in a
    // package.
    for folder in ["data", "vtank", "Data"] {
        let fixture = InstallerFixture::new();
        let manifest = installer_manifest(HELLO, "0.1.0", None, None).into_bytes();
        let release = fixture.release_from(
            "0.1.0",
            manifest.clone(),
            vec![
                ("plugin.json".to_owned(), manifest),
                (format!("{HELLO}.wasm"), b"binary".to_vec()),
                (format!("{folder}/defaults.json"), b"{}".to_vec()),
            ],
        );
        fixture.register(HELLO_REPO, &release, None, None);

        let error = fixture.install(HELLO_REPO, &release).unwrap_err();

        assert_eq!(
            error.to_string(),
            ac_launcher_core::plugins::PACKAGED_FILES_REFUSAL,
            "{folder}"
        );
        assert!(!fixture.plugin_dir().join(HELLO).exists());
    }
}

/// The shared VTank folder inside a plugin's folder, as MossTank's holds it.
fn vtank_folder(fixture: &InstallerFixture) -> PathBuf {
    fixture
        .plugin_dir()
        .join(HELLO)
        .join(ac_host::paths::VTANK_FOLDER_NAME)
}

#[test]
fn an_update_and_a_reinstall_keep_the_data_and_vtank_folders() {
    let fixture = InstallerFixture::new();
    let first = fixture.release("0.1.0");
    fixture.register(HELLO_REPO, &first, None, None);
    fixture.install(HELLO_REPO, &first).unwrap();
    let data = fixture.paths.plugin_files_directory(HELLO);
    std::fs::create_dir_all(&data).unwrap();
    std::fs::write(data.join("settings.json"), "mine").unwrap();
    let vtank = vtank_folder(&fixture);
    std::fs::create_dir_all(vtank.join("mosstank").join("metas")).unwrap();
    std::fs::write(vtank.join("mosstank").join("metas").join("a.met"), "meta").unwrap();

    let second = fixture.release("0.2.0");
    fixture.register(HELLO_REPO, &second, None, None);
    fixture.install(HELLO_REPO, &second).unwrap();
    assert_eq!(
        fixture.find(HELLO).unwrap().version.as_deref(),
        Some("0.2.0")
    );
    assert_eq!(
        std::fs::read_to_string(data.join("settings.json")).unwrap(),
        "mine"
    );
    assert_eq!(
        std::fs::read_to_string(vtank.join("mosstank").join("metas").join("a.met")).unwrap(),
        "meta"
    );

    // Removed (its data kept), then installed again.
    fixture.installer.remove(HELLO, false).unwrap();
    assert!(
        !fixture
            .plugin_dir()
            .join(HELLO)
            .join("plugin.json")
            .exists()
    );
    assert!(data.join("settings.json").is_file());
    assert!(vtank.join("mosstank").join("metas").join("a.met").is_file());
    fixture.install(HELLO_REPO, &second).unwrap();
    assert!(
        fixture
            .plugin_dir()
            .join(HELLO)
            .join("plugin.json")
            .is_file()
    );
    assert!(data.join("settings.json").is_file());
    assert!(vtank.join("mosstank").join("metas").join("a.met").is_file());
}

#[test]
fn removing_with_its_storage_never_deletes_the_shared_vtank_folder() {
    let fixture = InstallerFixture::new();
    let release = fixture.release("0.1.0");
    fixture.register(HELLO_REPO, &release, None, None);
    fixture.install(HELLO_REPO, &release).unwrap();
    let data = fixture.paths.plugin_files_directory(HELLO);
    std::fs::create_dir_all(&data).unwrap();
    std::fs::write(data.join("settings.json"), "mine").unwrap();
    let vtank = vtank_folder(&fixture);
    std::fs::create_dir_all(&vtank).unwrap();
    std::fs::write(vtank.join("loot.utl"), "shared").unwrap();

    fixture.installer.remove(HELLO, true).unwrap();

    assert!(!data.exists());
    assert_eq!(
        std::fs::read_to_string(vtank.join("loot.utl")).unwrap(),
        "shared"
    );
    assert!(fixture.find(HELLO).is_none());
    // A folder holding only the shared VTank folder is not an install: the
    // plugin installs into it again.
    fixture.install(HELLO_REPO, &release).unwrap();
    assert!(
        fixture
            .plugin_dir()
            .join(HELLO)
            .join("plugin.json")
            .is_file()
    );
    assert!(vtank.join("loot.utl").is_file());
}

#[test]
fn a_vtank_folder_without_its_plugin_is_not_an_unmanaged_install() {
    // Another plugin writes the shared VTank folder while MossTank is not
    // installed; installing MossTank afterwards adopts the folder.
    let fixture = InstallerFixture::new();
    let vtank = vtank_folder(&fixture);
    std::fs::create_dir_all(&vtank).unwrap();
    std::fs::write(vtank.join("a.met"), "meta").unwrap();
    let release = fixture.release("0.1.0");
    fixture.register(HELLO_REPO, &release, None, None);
    fixture.install(HELLO_REPO, &release).unwrap();
    assert!(
        fixture
            .plugin_dir()
            .join(HELLO)
            .join("plugin.json")
            .is_file()
    );
    assert_eq!(
        std::fs::read_to_string(vtank.join("a.met")).unwrap(),
        "meta"
    );
}

// OpenAC PluginInstallerTests.RecoveryReturnsFilesAnInterruptedUpdateStaged.
#[test]
fn recovery_returns_files_an_interrupted_update_staged() {
    let fixture = InstallerFixture::new();
    write_manifest(&fixture.plugin_dir().join(HELLO), HELLO, "0.1.0");
    let staging = fixture.plugin_dir().join(".staging");
    let staged = staging.join(format!("{HELLO}-abc123")).join("data");
    std::fs::create_dir_all(&staged).unwrap();
    std::fs::write(staged.join("state.json"), "{}").unwrap();
    std::fs::write(staging.join(format!("{HELLO}-abc123.player-files")), HELLO).unwrap();

    fixture.installer.recover().unwrap();

    assert!(
        fixture
            .paths
            .plugin_files_directory(HELLO)
            .join("state.json")
            .is_file()
    );
    assert!(!staging.exists());
}

// OpenAC PluginInstallerTests.AnUpdateThatFailsPartWayPutsTheOldVersionBack.
#[test]
fn an_update_that_fails_part_way_puts_the_old_version_back() {
    let mut fixture = InstallerFixture::new();
    let first = release_with_files(&fixture, "0.1.0", &[("shared.xml", b"v1")]);
    fixture.register(HELLO_REPO, &first, None, None);
    fixture.install(HELLO_REPO, &first).unwrap();
    let files = fixture.paths.plugin_files_directory(HELLO);
    std::fs::create_dir_all(&files).unwrap();
    std::fs::write(files.join("state.json"), "mine").unwrap();
    fixture
        .installer
        .set_after_old_code_set_aside(Box::new(|| Err(std::io::Error::other("stopped here"))));

    let second = release_with_files(
        &fixture,
        "0.2.0",
        &[("shared.xml", b"v2"), ("new.xml", b"")],
    );
    fixture.register(HELLO_REPO, &second, None, None);
    let error = fixture
        .install(HELLO_REPO, &second)
        .unwrap_err()
        .to_string();

    let plugin = fixture.plugin_dir().join(HELLO);
    assert!(
        error.contains("The previous version is back in place."),
        "{error}"
    );
    assert!(
        std::fs::read_to_string(plugin.join("plugin.json"))
            .unwrap()
            .contains("\"0.1.0\"")
    );
    assert_eq!(
        std::fs::read_to_string(plugin.join("shared.xml")).unwrap(),
        "v1"
    );
    assert!(!plugin.join("new.xml").exists());
    assert_eq!(
        std::fs::read_to_string(files.join("state.json")).unwrap(),
        "mine"
    );
    assert!(!fixture.plugin_dir().join(".staging").exists());

    fixture.installer.recover().unwrap();
    let record = fixture.find(HELLO).unwrap();
    assert_eq!(record.version.as_deref(), Some("0.1.0"));
    assert!(record.pending.is_none());
}

/// Where an in-place update keeps the old version's files: `<staging>.old`.
fn set_aside_of(staging: &Path) -> PathBuf {
    let mut folder = staging.as_os_str().to_owned();
    folder.push(".old");
    PathBuf::from(folder)
}

/// A staging folder for an interrupted in-place update and its journal.
fn interrupted_update(fixture: &InstallerFixture, old_files: &[&str]) -> PathBuf {
    let staging = fixture
        .plugin_dir()
        .join(".staging")
        .join(format!("{HELLO}-abc123"));
    std::fs::create_dir_all(&staging).unwrap();
    std::fs::create_dir_all(set_aside_of(&staging)).unwrap();
    let mut journal = vec![HELLO.to_owned()];
    journal.extend(old_files.iter().map(|file| (*file).to_owned()));
    std::fs::write(
        fixture
            .plugin_dir()
            .join(".staging")
            .join(format!("{HELLO}-abc123.in-place")),
        journal.join("\n") + "\n",
    )
    .unwrap();
    {
        let mut records = fixture.records.lock().unwrap();
        records.records.push(pending_record(Some("0.1.0"), "0.2.0"));
        records.save().unwrap();
    }
    staging
}

// OpenAC PluginInstallerTests.RecoveryPutsBackAnInPlaceUpdateACrashInterrupted: the journal names
// the old version's files, some are set aside and some new files are in.
#[test]
fn recovery_puts_back_an_in_place_update_a_crash_interrupted() {
    let fixture = InstallerFixture::new();
    let plugin = fixture.plugin_dir().join(HELLO);
    std::fs::create_dir_all(plugin.join("data")).unwrap();
    std::fs::write(plugin.join("data").join("state.json"), "mine").unwrap();
    let staging = interrupted_update(&fixture, &[&format!("{HELLO}.wasm"), "plugin.json"]);
    let set_aside = set_aside_of(&staging);
    // Set aside: the old manifest; still in place: the old module; already
    // in: one new file; still staged: the new manifest.
    std::fs::write(set_aside.join("plugin.json"), manifest_json(HELLO, "0.1.0")).unwrap();
    std::fs::write(plugin.join(format!("{HELLO}.wasm")), "old").unwrap();
    std::fs::write(plugin.join("new.xml"), "new").unwrap();
    std::fs::write(staging.join("plugin.json"), manifest_json(HELLO, "0.2.0")).unwrap();

    fixture.installer.recover().unwrap();

    assert!(
        std::fs::read_to_string(plugin.join("plugin.json"))
            .unwrap()
            .contains("\"0.1.0\"")
    );
    assert_eq!(
        std::fs::read_to_string(plugin.join(format!("{HELLO}.wasm"))).unwrap(),
        "old"
    );
    assert!(!plugin.join("new.xml").exists());
    assert_eq!(
        std::fs::read_to_string(plugin.join("data").join("state.json")).unwrap(),
        "mine"
    );
    assert!(!fixture.plugin_dir().join(".staging").exists());
    let record = fixture.find(HELLO).unwrap();
    assert_eq!(record.version.as_deref(), Some("0.1.0"));
    assert!(record.pending.is_none());
}

// OpenAC PluginInstallerTests.RecoveryFinishesAnInPlaceUpdateWhoseManifestWasAlreadyIn.
#[test]
fn recovery_finishes_an_in_place_update_whose_manifest_was_already_in() {
    let fixture = InstallerFixture::new();
    let plugin = fixture.plugin_dir().join(HELLO);
    std::fs::create_dir_all(&plugin).unwrap();
    let staging = interrupted_update(&fixture, &["plugin.json"]);
    std::fs::write(
        set_aside_of(&staging).join("plugin.json"),
        manifest_json(HELLO, "0.1.0"),
    )
    .unwrap();
    std::fs::write(plugin.join("plugin.json"), manifest_json(HELLO, "0.2.0")).unwrap();

    fixture.installer.recover().unwrap();

    assert!(
        std::fs::read_to_string(plugin.join("plugin.json"))
            .unwrap()
            .contains("\"0.2.0\"")
    );
    assert!(!fixture.plugin_dir().join(".staging").exists());
    assert_eq!(
        fixture.find(HELLO).unwrap().version.as_deref(),
        Some("0.2.0")
    );
}
// OpenAC PluginInstallerTests.RecoveryNeverAdoptsAPackagesOwnFilesFolder.
#[test]
fn recovery_never_adopts_a_packages_own_files_folder() {
    let fixture = InstallerFixture::new();
    let staged = fixture
        .plugin_dir()
        .join(".staging")
        .join(format!("{HELLO}-abc123"))
        .join("data");
    std::fs::create_dir_all(&staged).unwrap();
    std::fs::write(staged.join("defaults.json"), "{}").unwrap();

    fixture.installer.recover().unwrap();

    assert!(!fixture.plugin_dir().join(HELLO).exists());
    assert!(!fixture.plugin_dir().join(".staging").exists());
}

// OpenAC PluginInstallerTests.RecoveryReturnsFilesFromRecordLessTrash.
#[test]
fn recovery_returns_files_from_record_less_trash() {
    let fixture = InstallerFixture::new();
    let trash = fixture
        .plugin_dir()
        .join(".trash")
        .join(format!("{HELLO}-abc123"));
    write_manifest(&trash, HELLO, "0.1.0");
    std::fs::create_dir_all(trash.join("data")).unwrap();
    std::fs::write(trash.join("data").join("state.json"), "{}").unwrap();

    fixture.installer.recover().unwrap();

    assert!(
        fixture
            .paths
            .plugin_files_directory(HELLO)
            .join("state.json")
            .is_file()
    );
    assert!(
        !fixture
            .plugin_dir()
            .join(HELLO)
            .join("plugin.json")
            .exists()
    );
    assert!(!trash.exists());
}

#[test]
fn set_channel_rules() {
    let fixture = InstallerFixture::new();
    assert!(
        fixture
            .installer
            .set_channel(HELLO, PluginReleaseChannel::Beta)
            .is_err()
    );
    let release = fixture.release("0.1.0");
    fixture.register(HELLO_REPO, &release, None, None);
    fixture.install(HELLO_REPO, &release).unwrap();
    let barrier = UpdateSessionBarrier::new(&fixture.paths.data_directory);
    {
        // OpenAC SetChannelIsRefusedWhileAClientUpdateRuns.
        let _lease = barrier.acquire_exclusive().unwrap();
        assert_eq!(
            fixture
                .installer
                .set_channel(HELLO, PluginReleaseChannel::Beta)
                .unwrap_err()
                .to_string(),
            ac_launcher_core::plugins::UPDATE_IN_PROGRESS_REFUSAL
        );
    }
    assert_eq!(
        fixture.find(HELLO).unwrap().channel,
        PluginReleaseChannel::Stable
    );
    // OpenAC SetChannelRunsWhileASessionIsPlaying.
    let session = barrier.acquire_session().unwrap();
    let updated = fixture
        .installer
        .set_channel(HELLO, PluginReleaseChannel::Beta)
        .unwrap();
    drop(session);
    assert_eq!(updated.channel, PluginReleaseChannel::Beta);
    assert_eq!(
        fixture.find(HELLO).unwrap().channel,
        PluginReleaseChannel::Beta
    );
}

#[test]
fn remove_direct_rules() {
    let fixture = InstallerFixture::new();
    let directory = fixture.plugin_dir().join("someones-copy");
    write_manifest(&directory, HELLO, "0.1.0");
    let storage = fixture.paths.plugin_files_directory(HELLO);
    std::fs::create_dir_all(&storage).unwrap();
    fixture.installer.remove_direct(&directory, true).unwrap();
    assert!(!directory.exists());
    assert!(!storage.exists());

    // A refused folder (no entry file) can still be removed.
    let directory = fixture.plugin_dir().join("broken-copy");
    std::fs::create_dir_all(&directory).unwrap();
    std::fs::write(
        directory.join("plugin.json"),
        installer_manifest(HELLO, "0.1.0", None, None),
    )
    .unwrap();
    fixture.installer.remove_direct(&directory, false).unwrap();
    assert!(!directory.exists());

    let outside = fixture.root.join("elsewhere").join("someones-copy");
    write_manifest(&outside, HELLO, "0.1.0");
    assert!(fixture.installer.remove_direct(&outside, false).is_err());
    assert!(outside.exists());

    let directory = fixture.plugin_dir().join("someones-copy");
    write_manifest(&directory, "not_a_valid_id", "0.1.0");
    let storage = fixture.paths.plugin_files_directory("not_a_valid_id");
    std::fs::create_dir_all(&storage).unwrap();
    fixture.installer.remove_direct(&directory, true).unwrap();
    assert!(!directory.exists());
    assert!(storage.exists());

    let directory = fixture.plugin_dir().join("someones-copy");
    write_manifest(&directory, HELLO, "0.1.0");
    let barrier = UpdateSessionBarrier::new(&fixture.paths.data_directory);
    {
        let _lease = barrier.acquire_exclusive().unwrap();
        assert_eq!(
            fixture
                .installer
                .remove_direct(&directory, false)
                .unwrap_err()
                .to_string(),
            SESSION_LEASE_REFUSAL
        );
    }
    assert!(directory.exists());
    ac_launcher_core::fsutil::try_delete_tree(&directory);

    let release = fixture.release("0.1.0");
    fixture.register(HELLO_REPO, &release, None, None);
    fixture.install(HELLO_REPO, &release).unwrap();
    assert!(
        fixture
            .installer
            .remove_direct(&fixture.plugin_dir().join(HELLO), false)
            .is_err()
    );
}

// --- PluginsNeverExecuteBoundaryTests and the dependency boundary ---------------------

// The client's plugin host (ac-plugin) is a dependency for its manifest reader
// only; the launcher never loads a module, so its engine is forbidden here too.
const FORBIDDEN: [&str; 17] = [
    "PluginEngine",
    "PluginSession",
    "check_module",
    "wasmtime",
    "Module::new",
    "std::process",
    "process::Command",
    "Command::new",
    "libloading",
    "dlopen",
    "LoadLibrary",
    "CreateProcess",
    "ShellExecute",
    "open::that",
    "execve",
    "posix_spawn",
    "ProcessExt",
];

fn contains_forbidden(text: &str) -> Option<&'static str> {
    let compact: String = text.chars().filter(|c| !c.is_whitespace()).collect();
    FORBIDDEN
        .into_iter()
        .find(|token| compact.contains(&token.replace(' ', "")))
}

#[test]
fn no_plugin_or_download_source_file_can_execute_or_load_code() {
    let crate_root = Path::new(env!("CARGO_MANIFEST_DIR"));
    let mut files: Vec<PathBuf> = std::fs::read_dir(crate_root.join("src").join("plugins"))
        .unwrap()
        .flatten()
        .map(|entry| entry.path())
        .collect();
    files.push(crate_root.join("src").join("updates").join("zip.rs"));
    files.push(crate_root.join("src").join("updates").join("downloader.rs"));
    files.push(crate_root.join("src").join("net.rs"));
    assert!(files.len() > 3);
    let offenders: Vec<String> = files
        .iter()
        .filter_map(|path| {
            let text = std::fs::read_to_string(path).unwrap();
            contains_forbidden(&text).map(|token| format!("{} uses '{token}'", path.display()))
        })
        .collect();
    assert!(offenders.is_empty(), "{offenders:?}");
}

#[test]
fn each_forbidden_token_is_detected_plain_and_whitespace_split() {
    for token in FORBIDDEN {
        assert_eq!(
            contains_forbidden(&format!("before {token} after")),
            Some(token)
        );
        let split: String = token.chars().map(|c| format!("{c} ")).collect();
        assert_eq!(
            contains_forbidden(&format!("before {split} after")),
            Some(token)
        );
    }
    assert_eq!(contains_forbidden("fn m() { let x = 1 + 1; }"), None);
}

#[test]
fn launcher_core_depends_only_on_pure_rust_crates_it_names() {
    let manifest =
        std::fs::read_to_string(Path::new(env!("CARGO_MANIFEST_DIR")).join("Cargo.toml")).unwrap();
    let mut section = String::new();
    let mut dependencies = Vec::new();
    for line in manifest.lines() {
        let line = line.trim();
        if line.starts_with('[') {
            section = line.to_owned();
            continue;
        }
        if section.ends_with("dependencies]")
            && !section.contains("dev-")
            && let Some((name, _)) = line.split_once('=')
        {
            dependencies.push(name.trim().split('.').next().unwrap().to_owned());
        }
    }
    dependencies.sort();
    assert_eq!(
        dependencies,
        [
            "ac-host",
            "ac-plugin",
            "miniz_oxide",
            "nix",
            "roxmltree",
            "sha2",
            "thiserror",
            "windows-sys"
        ]
    );
}

// --- PluginInstallerTests (local ZIPs) --------------------------------------------------------
// OpenAC `PluginInstallerTests.LocalZip*`: a local release archive installed as
// a direct install, without a release or any request.

/// A plugin release archive for `version`, written to `<root>/plugin.zip`.
fn write_local_zip(fixture: &InstallerFixture, version: &str) -> PathBuf {
    let zip = fixture.root.join("plugin.zip");
    std::fs::write(&zip, fixture.release(version).zip_bytes).unwrap();
    zip
}

fn local_inventory(
    fixture: &InstallerFixture,
    catalog: Option<&PluginCatalog>,
) -> Vec<InstalledPluginInfo> {
    let records = fixture.records.lock().unwrap();
    PluginInventory::new(&fixture.paths).build(&records, None, catalog)
}

fn install_local(
    fixture: &InstallerFixture,
    zip: &Path,
    preview: &ac_launcher_core::plugins::LocalPluginZipPreview,
    catalog: Option<&PluginCatalog>,
) -> Result<ac_launcher_core::plugins::PluginInstallResult, UpdateError> {
    fixture.installer.install_local_zip(
        zip,
        &preview.sha256,
        catalog,
        None,
        &preview.manifest.permissions,
        &none(),
    )
}

// OpenAC LocalZipInstallsAsDirectWithoutAReleaseOrNetworkRequest.
#[test]
fn local_zip_installs_as_direct_without_a_release_or_network_request() {
    let fixture = InstallerFixture::new();
    let zip = write_local_zip(&fixture, "0.1.0");
    let preview = fixture.installer.inspect_local_zip(&zip, &none()).unwrap();
    assert_eq!(preview.manifest.id, HELLO);
    let result = install_local(&fixture, &zip, &preview, None).unwrap();
    assert!(!result.was_update);
    assert_eq!(result.version, "0.1.0");
    assert!(fixture.find(HELLO).is_none());
    let inventory = local_inventory(&fixture, None);
    assert_eq!(inventory.len(), 1);
    assert_eq!(inventory[0].source, InstalledPluginSource::Direct);
    assert!(fixture.server.requests().is_empty());
}

// OpenAC LocalZipUpdatesDirectPluginAndPreservesItsSavedFiles.
#[test]
fn local_zip_updates_a_direct_plugin_and_preserves_its_saved_files() {
    let fixture = InstallerFixture::new();
    let zip = write_local_zip(&fixture, "0.1.0");
    let first = fixture.installer.inspect_local_zip(&zip, &none()).unwrap();
    install_local(&fixture, &zip, &first, None).unwrap();
    let saved = fixture
        .paths
        .plugin_files_directory(HELLO)
        .join("settings.json");
    std::fs::create_dir_all(saved.parent().unwrap()).unwrap();
    std::fs::write(&saved, "player settings").unwrap();

    let zip = write_local_zip(&fixture, "0.2.0");
    let second = fixture.installer.inspect_local_zip(&zip, &none()).unwrap();
    let result = install_local(&fixture, &zip, &second, None).unwrap();

    assert!(result.was_update);
    assert_eq!(std::fs::read_to_string(&saved).unwrap(), "player settings");
    let inventory = local_inventory(&fixture, None);
    assert_eq!(inventory.len(), 1);
    assert_eq!(inventory[0].version, "0.2.0");
    assert!(fixture.find(HELLO).is_none());
}

// OpenAC LocalZipFailedUpdateRestoresThePreviousCodeAndSavedFiles.
#[test]
fn local_zip_failed_update_restores_the_previous_code_and_saved_files() {
    let mut fixture = InstallerFixture::new();
    let zip = write_local_zip(&fixture, "0.1.0");
    let first = fixture.installer.inspect_local_zip(&zip, &none()).unwrap();
    install_local(&fixture, &zip, &first, None).unwrap();
    let saved = fixture
        .paths
        .plugin_files_directory(HELLO)
        .join("settings.json");
    std::fs::create_dir_all(saved.parent().unwrap()).unwrap();
    std::fs::write(&saved, "keep me").unwrap();

    let zip = write_local_zip(&fixture, "0.2.0");
    let second = fixture.installer.inspect_local_zip(&zip, &none()).unwrap();
    fixture
        .installer
        .set_after_old_code_set_aside(Box::new(|| Err(std::io::Error::other("test failure"))));
    assert!(install_local(&fixture, &zip, &second, None).is_err());

    let inventory = local_inventory(&fixture, None);
    assert_eq!(inventory.len(), 1);
    assert_eq!(inventory[0].version, "0.1.0");
    assert_eq!(std::fs::read_to_string(&saved).unwrap(), "keep me");
}

// OpenAC LocalZipRejectsUnsafeArchiveBeforeShowingAReview.
#[test]
fn local_zip_rejects_an_unsafe_archive_before_showing_a_review() {
    let fixture = InstallerFixture::new();
    let manifest = installer_manifest(HELLO, "0.1.0", None, None).into_bytes();
    let zip = fixture.root.join("plugin.zip");
    std::fs::write(
        &zip,
        create_zip(
            &[
                ("../outside.txt", b"escape".as_slice(), None),
                ("plugin.json", manifest.as_slice(), None),
                (&format!("{HELLO}.wasm"), b"binary".as_slice(), None),
            ],
            Compression::Stored,
        ),
    )
    .unwrap();
    assert!(fixture.installer.inspect_local_zip(&zip, &none()).is_err());
    assert!(!fixture.root.join("outside.txt").exists());
}

// OpenAC LocalZipRefusesAChangedArchiveBeforeReplacingAnInstalledPlugin.
#[test]
fn local_zip_refuses_a_changed_archive_before_replacing_an_installed_plugin() {
    let fixture = InstallerFixture::new();
    let zip = write_local_zip(&fixture, "0.1.0");
    let preview = fixture.installer.inspect_local_zip(&zip, &none()).unwrap();
    write_local_zip(&fixture, "0.2.0");
    let error = install_local(&fixture, &zip, &preview, None)
        .unwrap_err()
        .to_string();
    assert!(error.contains("changed since you reviewed"), "{error}");
    assert!(local_inventory(&fixture, None).is_empty());
}

// OpenAC LocalZipCannotReplaceALauncherManagedPlugin.
#[test]
fn local_zip_cannot_replace_a_launcher_managed_plugin() {
    let fixture = InstallerFixture::new();
    let first = fixture.release("0.1.0");
    fixture.register(HELLO_REPO, &first, None, None);
    fixture.install(HELLO_REPO, &first).unwrap();
    let zip = write_local_zip(&fixture, "0.2.0");
    let preview = fixture.installer.inspect_local_zip(&zip, &none()).unwrap();
    let error = install_local(&fixture, &zip, &preview, None)
        .unwrap_err()
        .to_string();
    assert!(error.contains("managed by the launcher"), "{error}");
    let inventory = local_inventory(&fixture, None);
    assert_eq!(inventory.len(), 1);
    assert_eq!(inventory[0].version, "0.1.0");
}

// OpenAC LocalZipRespectsTheCatalogBlocklist.
#[test]
fn local_zip_respects_the_catalog_blocklist() {
    let fixture = InstallerFixture::new();
    let zip = write_local_zip(&fixture, "0.1.0");
    let preview = fixture.installer.inspect_local_zip(&zip, &none()).unwrap();
    let catalog = PluginCatalog::parse(&catalog_json(
        "",
        Some(&format!(
            r#"{{ "id": "{HELLO}", "versions": ["*"], "reason": "blocked for test", "client": "openac-rust" }}"#
        )),
    ))
    .unwrap();
    let error = install_local(&fixture, &zip, &preview, Some(&catalog))
        .unwrap_err()
        .to_string();
    assert!(error.contains("blocked for test"), "{error}");
    assert!(local_inventory(&fixture, Some(&catalog)).is_empty());
}
