//! Session-config document checks.
//!
//! Ported from OpenAC `tests/AcDream.Headless.Tests/HeadlessConfigurationLoaderTests.cs`,
//! `SessionConfigurationSharedFixtureTests.cs`, and (for the window reader)
//! `tests/AcDream.App.Tests/Configuration/SessionConfigurationLoaderTests.cs`.
//! Golden documents: `OpenAC/tests/Fixtures/launcher-session/session-config-shared-fixture.json`
//! and the `bot.json` sample in `OpenAC/docs/building-and-running.md`.

mod common;

use std::path::PathBuf;

use ac_host_core::{
    CharacterOption, CharacterSelector, ConfigError, ConfigReader, CredentialProvider, HostConfig,
    PolicyRole, SessionDescriptor, SessionMode, load_config, load_config_file,
};
use common::{configuration_with, fixture, session};

fn load(json: &str) -> Result<HostConfig, ConfigError> {
    load_config(json.as_bytes(), ConfigReader::Headless)
}

fn load_window(json: &str) -> Result<HostConfig, ConfigError> {
    load_config(json.as_bytes(), ConfigReader::Window)
}

fn single(config: &HostConfig) -> &SessionDescriptor {
    assert_eq!(config.sessions.len(), 1);
    &config.sessions[0]
}

fn invalid_message(result: Result<HostConfig, ConfigError>) -> String {
    match result {
        Err(ConfigError::Invalid(message)) => message,
        other => panic!("expected a configuration error, got {other:?}"),
    }
}

fn is_json_error(result: &Result<HostConfig, ConfigError>) -> bool {
    matches!(result, Err(ConfigError::Json(_)))
}

fn with_bot(extra: &str) -> String {
    configuration_with(&[session("bot", "BOT_PASSWORD", Some(extra))])
}

// --- characterOptions ------------------------------------------------------

#[test]
fn valid_character_options_block_parses_exact_declared_names() {
    let config = load(&with_bot(
        r#""characterOptions":{
            "IgnoreAllegianceRequests":true,
            "ListenToTradeChat":false,
            "AutoTarget":true
        }"#,
    ))
    .unwrap();
    let declared = single(&config).character_options.as_ref().unwrap();
    assert_eq!(declared.len(), 3);
    assert!(declared[&CharacterOption::IgnoreAllegianceRequests]);
    assert!(!declared[&CharacterOption::ListenToTradeChat]);
    assert!(declared[&CharacterOption::AutoTarget]);
}

#[test]
fn unknown_option_name_fails_load_naming_the_offending_key() {
    let message = invalid_message(load(&with_bot(
        r#""characterOptions":{"NotARealOption":true}"#,
    )));
    assert!(message.contains("NotARealOption"));
}

#[test]
fn presentation_only_tier_three_option_name_fails_load() {
    let message = invalid_message(load(&with_bot(r#""characterOptions":{"ShowHelm":true}"#)));
    assert!(message.contains("ShowHelm"));
}

#[test]
fn numeric_key_fails_load_instead_of_aliasing_into_an_allowed_id() {
    let message = invalid_message(load(&with_bot(r#""characterOptions":{"15":true}"#)));
    assert!(message.contains("15"));
}

#[test]
fn comma_combined_key_fails_load_instead_of_or_combining_into_an_allowed_id() {
    let message = invalid_message(load(&with_bot(
        r#""characterOptions":{"ToggleRun,AutoTarget":true}"#,
    )));
    assert!(message.contains("ToggleRun,AutoTarget"));
}

#[test]
fn both_fellowship_exclusion_options_true_fails_load_naming_both_keys() {
    let message = invalid_message(load(&with_bot(
        r#""characterOptions":{"IgnoreFellowshipRequests":true,"FellowshipAutoAcceptRequests":true}"#,
    )));
    assert!(message.contains("IgnoreFellowshipRequests"));
    assert!(message.contains("FellowshipAutoAcceptRequests"));
}

#[test]
fn non_bool_value_fails_load() {
    let result = load(&with_bot(
        r#""characterOptions":{"IgnoreAllegianceRequests":"yes"}"#,
    ));
    assert!(result.is_err());
}

#[test]
fn absent_character_options_block_is_a_no_op() {
    let config = load(&configuration_with(&[session("bot", "BOT_PASSWORD", None)])).unwrap();
    assert!(single(&config).character_options.is_none());
}

#[test]
fn empty_character_options_block_is_a_no_op() {
    let config = load(&with_bot(r#""characterOptions":{}"#)).unwrap();
    assert!(
        single(&config)
            .character_options
            .as_ref()
            .unwrap()
            .is_empty()
    );
}

// --- mode, character, policy -----------------------------------------------

const PROBE: &str = r#"{
  "version": 1,
  "sessions": [
    {
      "id": "probe-session",
      "endpoint": { "host": "127.0.0.1", "port": 9000 },
      "account": "account",
      "mode": "probe",
      "credential": { "provider": "environment", "reference": "PROBE_PASSWORD" }
    }
  ]
}"#;

#[test]
fn probe_session_omitting_character_and_policy_loads() {
    let config = load(PROBE).unwrap();
    assert_eq!(single(&config).mode, SessionMode::Probe);
}

#[test]
fn session_mode_rejects_every_value_other_than_the_named_probe_mode() {
    for mode in ["\"play\"", "0"] {
        let json = PROBE.replace("\"probe\"", mode);
        assert!(is_json_error(&load(&json)), "mode {mode}");
    }
}

#[test]
fn conditional_session_fields_reject_explicit_json_null() {
    for (probe, null_field) in [
        (false, "mode"),
        (false, "character"),
        (false, "policy"),
        (true, "character"),
        (true, "policy"),
    ] {
        let mode = if probe {
            "\"mode\":\"probe\","
        } else if null_field == "mode" {
            "\"mode\":null,"
        } else {
            ""
        };
        let character = if null_field == "character" {
            "\"character\":null,"
        } else if probe {
            ""
        } else {
            "\"character\":{\"index\":0},"
        };
        let policy = if null_field == "policy" {
            "\"policy\":null,"
        } else if probe {
            ""
        } else {
            "\"policy\":{\"id\":\"idle\"},"
        };
        let json = format!(
            r#"{{"version":1,"sessions":[{{"id":"explicit-null",
            "endpoint":{{"host":"127.0.0.1","port":9000}},"account":"account",
            {mode}{character}{policy}
            "credential":{{"provider":"environment","reference":"PASSWORD"}}}}]}}"#
        );
        let message = invalid_message(load(&json));
        assert!(message.contains(&format!("'{null_field}'")), "{message}");
        assert!(message.contains("cannot be null"), "{message}");
    }
}

#[test]
fn presence_aware_validation_keeps_unmapped_member_rejection() {
    assert!(is_json_error(&load(&with_bot(
        "\"notAContractField\":true"
    ))));
}

#[test]
fn presence_aware_validation_keeps_typed_value_rejection() {
    let json = PROBE.replace("\"mode\": \"probe\"", "\"mode\": { \"value\": \"probe\" }");
    assert!(is_json_error(&load(&json)));
}

#[test]
fn probe_session_declaring_character_fails_load_naming_the_field() {
    let json = PROBE.replace(
        "\"mode\": \"probe\",",
        "\"mode\": \"probe\", \"character\": { \"index\": 0 },",
    );
    let message = invalid_message(load(&json));
    assert!(message.contains("probe"));
    assert!(message.contains("character"));
}

#[test]
fn probe_session_declaring_policy_fails_load_naming_the_field() {
    let json = PROBE.replace(
        "\"mode\": \"probe\",",
        "\"mode\": \"probe\", \"policy\": { \"id\": \"idle\" },",
    );
    let message = invalid_message(load(&json));
    assert!(message.contains("probe"));
    assert!(message.contains("policy"));
}

#[test]
fn play_session_missing_character_still_fails_load() {
    let json = r#"{"version":1,"sessions":[{"id":"play-missing-character",
        "endpoint":{"host":"127.0.0.1","port":9000},"account":"account",
        "policy":{"id":"idle"},
        "credential":{"provider":"environment","reference":"PLAY_PASSWORD"}}]}"#;
    assert!(invalid_message(load(json)).contains("requires a character selector"));
}

#[test]
fn play_session_missing_policy_still_fails_load() {
    let json = r#"{"version":1,"sessions":[{"id":"play-missing-policy",
        "endpoint":{"host":"127.0.0.1","port":9000},"account":"account",
        "character":{"index":0},
        "credential":{"provider":"environment","reference":"PLAY_PASSWORD"}}]}"#;
    assert!(invalid_message(load(json)).contains("requires a non-empty policy id"));
}

#[test]
fn play_session_keeps_strict_character_selector_and_policy_validation() {
    let bad_selector = configuration_with(&[session("bad-selector", "A", None)
        .replace("{\"index\":0}", "{\"index\":0,\"name\":\"Two\"}")]);
    let blank_policy = configuration_with(&[
        session("blank-policy", "B", None).replace("{\"id\":\"idle\"}", "{\"id\":\"\"}")
    ]);
    invalid_message(load(&bad_selector));
    invalid_message(load(&blank_policy));
}

#[test]
fn character_selector_accepts_exactly_one_valid_way() {
    let selector = |json: &str| {
        let doc = configuration_with(&[session("s", "X", None).replace("{\"index\":0}", json)]);
        load(&doc)
    };
    let mode = |config: HostConfig| config.sessions[0].mode.clone();
    assert_eq!(
        mode(selector("{\"id\":1342177281}").unwrap()),
        SessionMode::Play {
            character: Some(CharacterSelector::Id(0x5000_0001)),
            policy: Some(ac_host_core::PolicyDescriptor {
                id: "idle".into(),
                role: None
            }),
        }
    );
    assert!(matches!(
        mode(selector("{\"name\":\"Toon\"}").unwrap()),
        SessionMode::Play {
            character: Some(CharacterSelector::Name(ref name)),
            ..
        } if name == "Toon"
    ));
    // A blank name does not count as a selector.
    assert!(matches!(
        mode(selector("{\"index\":2,\"name\":\"  \"}").unwrap()),
        SessionMode::Play {
            character: Some(CharacterSelector::Index(2)),
            ..
        }
    ));
    invalid_message(selector("{\"index\":-1}"));
    invalid_message(selector("{\"id\":0}"));
    invalid_message(selector("{}"));
    invalid_message(selector("{\"name\":\" \"}"));
    assert!(is_json_error(&selector("{\"id\":-1}")));
}

#[test]
fn policy_role_and_enum_names_ignore_case() {
    let doc = configuration_with(&[session("s", "X", None)
        .replace(
            "{\"id\":\"idle\"}",
            "{\"id\":\"idle\",\"role\":\"Recruit\"}",
        )
        .replace("\"environment\"", "\"StandardInput\"")]);
    let config = load(&doc).unwrap();
    let session = single(&config);
    assert_eq!(
        session.credential.provider,
        CredentialProvider::StandardInput
    );
    assert!(matches!(
        session.mode,
        SessionMode::Play {
            policy: Some(ac_host_core::PolicyDescriptor {
                role: Some(PolicyRole::Recruit),
                ..
            }),
            ..
        }
    ));
}

// --- pluginTags and pluginSettings (inert until plugins arrive) -------------

#[test]
fn plugin_tags_parse_and_round_trip() {
    let config = load(&with_bot(r#""pluginTags":["tank","group-a"]"#)).unwrap();
    assert_eq!(
        single(&config).plugin_tags.as_deref(),
        Some(&["tank".to_owned(), "group-a".to_owned()][..])
    );
}

#[test]
fn a_blank_plugin_tag_is_rejected() {
    invalid_message(load(&with_bot(r#""pluginTags":["tank"," "]"#)));
}

#[test]
fn plugin_settings_with_all_six_keys_parses_and_round_trips() {
    let config = load(&with_bot(
        r#""pluginSettings":{
            "edwards.tank":{
                "settingsProfile":"myprofile",
                "metaProfile":"myMeta",
                "navProfile":"myNav",
                "lootProfile":"myLoot",
                "startMacro":"true",
                "enableMeta":"true"
            }
        }"#,
    ))
    .unwrap();
    let by_plugin = single(&config).plugin_settings.as_ref().unwrap();
    assert_eq!(by_plugin.len(), 1);
    let declared = &by_plugin["edwards.tank"];
    assert_eq!(declared.len(), 6);
    assert_eq!(declared["settingsProfile"], "myprofile");
    assert_eq!(declared["metaProfile"], "myMeta");
    assert_eq!(declared["navProfile"], "myNav");
    assert_eq!(declared["lootProfile"], "myLoot");
    assert_eq!(declared["startMacro"], "true");
    assert_eq!(declared["enableMeta"], "true");
}

#[test]
fn plugin_settings_with_some_keys_missing_parses_the_ones_present() {
    let config = load(&with_bot(
        r#""pluginSettings":{"edwards.tank":{"startMacro":"true"}}"#,
    ))
    .unwrap();
    let by_plugin = single(&config).plugin_settings.as_ref().unwrap();
    assert_eq!(by_plugin.len(), 1);
    let only = &by_plugin["edwards.tank"];
    assert_eq!(only.keys().collect::<Vec<_>>(), ["startMacro"]);
}

#[test]
fn plugin_settings_with_two_plugins_sharing_a_key_name_parses_each_separately() {
    let config = load(&with_bot(
        r#""pluginSettings":{
            "edwards.tank":{"startMacro":"true"},
            "acdream.other":{"startMacro":"false"}
        }"#,
    ))
    .unwrap();
    let by_plugin = single(&config).plugin_settings.as_ref().unwrap();
    assert_eq!(by_plugin.len(), 2);
    assert_eq!(by_plugin["edwards.tank"]["startMacro"], "true");
    assert_eq!(by_plugin["acdream.other"]["startMacro"], "false");
}

#[test]
fn absent_plugin_settings_block_parses_as_none() {
    let config = load(&configuration_with(&[session("bot", "BOT_PASSWORD", None)])).unwrap();
    assert!(single(&config).plugin_settings.is_none());
}

#[test]
fn misspelled_plugin_settings_property_name_fails_load() {
    let result = load(&with_bot(
        r#""pluginSetting":{"edwards.tank":{"startMacro":"true"}}"#,
    ));
    assert!(is_json_error(&result));
}

#[test]
fn null_plugin_settings_value_fails_load_naming_the_key() {
    let message = invalid_message(load(&with_bot(
        r#""pluginSettings":{"edwards.tank":{"startMacro":null}}"#,
    )));
    assert!(message.contains("startMacro"));
}

#[test]
fn null_plugin_settings_sub_map_fails_load_naming_the_plugin_id() {
    let message = invalid_message(load(&with_bot(r#""pluginSettings":{"edwards.tank":null}"#)));
    assert!(message.contains("edwards.tank"));
}

// --- Shared fixture and launch-contract fields -----------------------------

#[test]
fn reader_accepts_the_production_shaped_shared_fixture() {
    let config = load_config_file(
        &fixture("session-config-shared-fixture.json"),
        ConfigReader::Headless,
    )
    .unwrap();
    let content = config.process.content.as_ref().unwrap();
    assert_eq!(content.dat_directory, PathBuf::from("shared-fixture-dats"));
    assert_eq!(
        content.prepared_asset_path,
        PathBuf::from("shared-fixture-dats/acdream.pak")
    );
    let session = single(&config);
    assert_eq!(session.id, "shared-fixture");
    assert_eq!(session.endpoint.host, "127.0.0.1");
    assert_eq!(session.endpoint.port, 9000);
    assert_eq!(session.account, "sharedaccount");
    let SessionMode::Play {
        character: Some(CharacterSelector::Name(name)),
        policy: Some(policy),
    } = &session.mode
    else {
        panic!("play session expected");
    };
    assert_eq!(name, "SharedToon");
    assert_eq!(policy.id, "idle");
    assert_eq!(
        session.credential.provider,
        CredentialProvider::StandardInput
    );
    assert_eq!(session.credential.reference, "session");
    assert_eq!(
        session.plugins.as_deref(),
        Some(&["ExamplePlugin".to_owned(), "AnotherPlugin".to_owned()][..])
    );
    assert_eq!(
        session.login_commands.as_deref(),
        Some(&["/tell someone, hi".to_owned(), "/vt start".to_owned()][..])
    );
    assert_eq!(session.login_command_delay_ms, 750);
    assert_eq!(
        session.status_file,
        Some(PathBuf::from("shared-fixture-status.jsonl"))
    );
    assert_eq!(
        session.plugin_tags.as_deref(),
        Some(&["shared-fixture-group".to_owned(), "second-word".to_owned()][..])
    );
    let options = session.character_options.as_ref().unwrap();
    assert!(options[&CharacterOption::UseChargeAttack]);
    assert!(!options[&CharacterOption::AutoRepeatAttack]);
    let settings = session.plugin_settings.as_ref().unwrap();
    assert_eq!(settings["ExamplePlugin"]["mode"], "escort");
    assert_eq!(settings["AnotherPlugin"]["profile"], "other");
}

#[test]
fn reader_accepts_optional_layered_prepared_content() {
    let json = std::fs::read_to_string(fixture("session-config-shared-fixture.json"))
        .unwrap()
        .replace(
            "\"preparedAssetPath\": \"shared-fixture-dats/acdream.pak\"",
            "\"preparedAssetPath\": \"shared-fixture-dats/acdream.pak\",
      \"preparedAssetOverlayPath\": \"composer-dats/update.pak\",
      \"preparedAssetBaseRecipeVersion\": 4,
      \"preparedAssetEffectiveRecipeVersion\": 5",
        );
    let config = load(&json).unwrap();
    let overlay = config
        .process
        .content
        .unwrap()
        .prepared_asset_overlay
        .unwrap();
    assert_eq!(overlay.path, PathBuf::from("composer-dats/update.pak"));
    assert_eq!(overlay.base_recipe_version, 4);
    assert_eq!(overlay.effective_recipe_version, 5);
}

#[test]
fn partial_overlay_fails_load() {
    let json = std::fs::read_to_string(fixture("session-config-shared-fixture.json"))
        .unwrap()
        .replace(
            "\"preparedAssetPath\": \"shared-fixture-dats/acdream.pak\"",
            "\"preparedAssetPath\": \"x.pak\", \"preparedAssetBaseRecipeVersion\": 4",
        );
    assert!(invalid_message(load(&json)).contains("must be supplied together"));
}

#[test]
fn absent_launch_contract_fields_fall_back_to_pinned_defaults() {
    let config = load(&configuration_with(&[session(
        "no-launch-contract-fields",
        "X",
        None,
    )]))
    .unwrap();
    let session = single(&config);
    assert!(session.plugins.is_none());
    assert!(session.login_commands.is_none());
    assert_eq!(session.login_command_delay_ms, 500);
    assert!(session.status_file.is_none());
}

#[test]
fn empty_plugin_allow_list_is_kept_distinct_from_absent() {
    let config = load(&with_bot(r#""plugins":[]"#)).unwrap();
    assert_eq!(single(&config).plugins.as_deref(), Some(&[][..]));
}

#[test]
fn empty_plugins_entry_fails_load() {
    invalid_message(load(&with_bot(r#""plugins":["Ok","   "]"#)));
}

#[test]
fn negative_login_command_delay_fails_load() {
    invalid_message(load(&with_bot(r#""loginCommandDelayMs":-1"#)));
}

#[test]
fn blank_status_file_fails_load() {
    invalid_message(load(&with_bot(r#""statusFile":"   ""#)));
}

#[test]
fn null_login_command_entry_fails_load() {
    let message = invalid_message(load(&with_bot(r#""loginCommands":["/a",null]"#)));
    assert!(message.contains("loginCommands"));
}

// --- Document-level rules ---------------------------------------------------

#[test]
fn document_sample_from_building_and_running_loads() {
    let config = load_config_file(
        &fixture("building-and-running-bot.json"),
        ConfigReader::Headless,
    )
    .unwrap();
    assert_eq!(
        config.process.content.as_ref().unwrap().dat_directory,
        PathBuf::from("/opt/ac")
    );
    let session = single(&config);
    assert_eq!(session.id, "bot-1");
    assert_eq!(session.account, "youraccount");
    assert_eq!(session.credential.reference, "ACDREAM_BOT_PASSWORD");
}

#[test]
fn documented_plugin_fragments_load() {
    let config = load(&with_bot(
        r#""pluginTags": ["tank", "group-a"],
        "pluginSettings": {
          "acdream.example": { "startMacro": "true", "profile": "tank" }
        }"#,
    ))
    .unwrap();
    let session = single(&config);
    assert_eq!(session.plugin_tags.as_ref().unwrap().len(), 2);
    assert_eq!(
        session.plugin_settings.as_ref().unwrap()["acdream.example"]["profile"],
        "tank"
    );
}

#[test]
fn duplicate_members_are_refused_anywhere() {
    assert!(is_json_error(&load(
        r#"{"version":1,"version":1,"sessions":[]}"#
    )));
    assert!(is_json_error(&load(&with_bot(
        r#""characterOptions":{"AutoTarget":true,"AutoTarget":false}"#
    ))));
    assert!(is_json_error(&load(&with_bot(
        r#""pluginSettings":{"p":{"k":"a","k":"b"}}"#
    ))));
}

#[test]
fn member_names_are_case_sensitive() {
    assert!(is_json_error(&load(r#"{"Version":1,"sessions":[]}"#)));
}

#[test]
fn null_document_is_empty() {
    assert!(invalid_message(load("null")).contains("empty"));
}

#[test]
fn null_sessions_is_not_an_array() {
    assert!(invalid_message(load(r#"{"version":1,"sessions":null}"#)).contains("array"));
}

#[test]
fn endpoint_port_must_be_in_range() {
    for port in ["0", "65536", "-5"] {
        let doc = configuration_with(&[session("s", "X", None).replace("9000", port)]);
        assert!(invalid_message(load(&doc)).contains("port from 1 through 65535"));
    }
    let doc = configuration_with(&[session("s", "X", None).replace("9000", "9000.5")]);
    assert!(is_json_error(&load(&doc)));
}

#[test]
fn credential_reference_may_repeat_across_providers_only() {
    let other_provider = session("second", "BOT_PASSWORD", None).replace("environment", "file");
    let config = load(&configuration_with(&[
        session("bot", "BOT_PASSWORD", None),
        other_provider,
    ]))
    .unwrap();
    assert_eq!(config.sessions.len(), 2);
}

#[test]
fn json_errors_never_repeat_document_text() {
    let error = load(r#"{"version":1,"sessions":[],"password":"secret"}"#).unwrap_err();
    let text = error.to_string();
    assert!(text.contains("the JSON document is invalid"));
    assert!(!text.contains("secret"));
    assert!(!text.contains("password"));
}

// --- The window reader ------------------------------------------------------

const WINDOW_SESSION: &str = r#"{
  "version": 1,
  SESSIONS
}"#;

fn window_doc(session_members: &str) -> String {
    WINDOW_SESSION.replace(
        "SESSIONS",
        &format!(
            r#""sessions": [{{
              "id": "window",
              "endpoint": {{ "host": "127.0.0.1", "port": 9000 }},
              "account": "account",
              "credential": {{ "provider": "environment", "reference": "X" }}{session_members}
            }}]"#
        ),
    )
}

#[test]
fn window_reader_accepts_process_paths() {
    let json = r#"{
      "version": 1,
      "process": { "paths": {
        "configDirectory": "/config", "dataDirectory": "/data", "cacheDirectory": "/cache"
      } },
      "sessions": [ {
        "id": "paths-tolerant",
        "endpoint": { "host": "127.0.0.1", "port": 9000 },
        "account": "account",
        "credential": { "provider": "environment", "reference": "X" }
      } ]
    }"#;
    let config = load_window(json).unwrap();
    assert_eq!(single(&config).id, "paths-tolerant");
    let paths = &config.process.paths;
    assert_eq!(paths.config_directory.as_deref(), Some("/config"));
    assert_eq!(paths.data_directory.as_deref(), Some("/data"));
    assert_eq!(paths.cache_directory.as_deref(), Some("/cache"));
}

// OpenAC HeadlessPathSetTests.BotConfigurationCanNameTheRoot.
#[test]
fn a_bot_configuration_can_name_the_root() {
    let config =
        load(r#"{"version":1,"process":{"paths":{"rootDirectory":"D:/OpenAC"}},"sessions":[]}"#)
            .unwrap();
    assert_eq!(
        config.process.paths.root_directory.as_deref(),
        Some("D:/OpenAC")
    );
}

#[test]
fn window_reader_treats_absent_mode_as_play_with_optional_character() {
    let config = load_window(&window_doc("")).unwrap();
    assert_eq!(
        single(&config).mode,
        SessionMode::Play {
            character: None,
            policy: None
        }
    );
}

#[test]
fn window_reader_refuses_probe_mode_as_headless_only() {
    let message = invalid_message(load_window(&window_doc(r#", "mode": "probe""#)));
    assert!(message.contains("mode"));
    assert!(message.contains("probe"));
    assert!(message.contains("headless-only"));
}

#[test]
fn window_reader_refuses_unrecognized_mode() {
    invalid_message(load_window(&window_doc(r#", "mode": "bogus""#)));
}

#[test]
fn window_reader_ignores_policy_and_requires_exactly_one_session() {
    let config = load_window(&window_doc(
        r#", "policy": { "id": "idle", "role": "anything" }"#,
    ))
    .unwrap();
    assert_eq!(single(&config).id, "window");
    let empty = r#"{"version":1,"sessions":[]}"#;
    assert!(invalid_message(load_window(empty)).contains("exactly one"));
    let two = configuration_with(&[session("a", "A", None), session("b", "B", None)]);
    assert!(invalid_message(load_window(&two)).contains("exactly one"));
}

#[test]
fn the_headless_shared_fixture_also_starts_a_window() {
    let config = load_config_file(
        &fixture("session-config-shared-fixture.json"),
        ConfigReader::Window,
    )
    .unwrap();
    assert!(matches!(
        single(&config).mode,
        SessionMode::Play {
            character: Some(CharacterSelector::Name(_)),
            ..
        }
    ));
}
