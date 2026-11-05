//! Profile store, migration, hardening, text projections, roster merge and
//! the checked accounts. Ported from OpenAC
//! `tests/AcDream.Launcher.Core.Tests/Profiles/` (LauncherProfileStoreTests,
//! LauncherProfileMigrationTests, LauncherProfileHardeningTests,
//! LauncherProfileTextTests, RosterMergeTests) and the store side of
//! `tests/AcDream.Launcher.Tests/LauncherWindowViewModel.CheckedAccounts.Tests.cs`.

mod common;

use std::path::PathBuf;

use ac_launcher_core::profiles::{
    AccountProfile, CharacterProfile, CharacterRosterEntry, LaunchMode, LauncherCheckedAccount,
    LauncherCheckedAccountStore, LauncherProfileDocument, LauncherProfileStore,
    LauncherTextEditorKind, OLDER_PROFILE_FILE_NAME, PROFILE_FILE_NAME, ProfileError,
    ServerProfile, VERSION2_PROFILE_FILE_NAME, apply_text, describe_account_removals, read_text,
};
use common::{TempDir, strings};

struct Fixture {
    root: TempDir,
    file_path: PathBuf,
}

fn fixture() -> Fixture {
    let root = TempDir::new("profiles");
    let file_path = root.join("launcher-profiles.json");
    Fixture { root, file_path }
}

fn store_at(path: &std::path::Path) -> LauncherProfileStore {
    LauncherProfileStore::new(path, None).unwrap()
}

fn loaded(fixture: &Fixture) -> LauncherProfileStore {
    let mut store = store_at(&fixture.file_path);
    store.load().unwrap();
    store
}

fn is_invalid<T: std::fmt::Debug>(result: Result<T, ProfileError>) -> bool {
    matches!(result, Err(ProfileError::Invalid(_)))
}

fn only_server(store: &LauncherProfileStore) -> &ServerProfile {
    assert_eq!(store.document().servers.len(), 1);
    &store.document().servers[0]
}

fn only_account(store: &LauncherProfileStore) -> &AccountProfile {
    let server = only_server(store);
    assert_eq!(server.accounts.len(), 1);
    &server.accounts[0]
}

fn only_character(store: &LauncherProfileStore) -> &CharacterProfile {
    let account = only_account(store);
    assert_eq!(account.characters.len(), 1);
    &account.characters[0]
}

fn account_named<'a>(
    store: &'a LauncherProfileStore,
    server: &str,
    account: &str,
) -> &'a AccountProfile {
    store
        .document()
        .servers
        .iter()
        .find(|item| item.name == server)
        .and_then(|server| server.accounts.iter().find(|item| item.account == account))
        .unwrap()
}

/// Removes a member from the JSON text of one server (by its position).
fn without_account_list_flags(text: &str) -> String {
    text.replace(",\n      \"accountListInitialized\": true", "")
}

// --- LauncherProfileStoreTests ---------------------------------------------

#[test]
fn load_on_missing_file_yields_empty_document_without_touching_disk() {
    let fixture = fixture();
    let mut store = store_at(&fixture.file_path);
    assert!(!store.load().unwrap());
    assert!(!fixture.file_path.exists());
    // Version 3 since logon commands per character.
    assert_eq!(store.document().version, 3);
    assert!(store.document().servers.is_empty());
}

#[test]
fn add_server_then_save_then_reload_round_trips() {
    let fixture = fixture();
    let mut store = loaded(&fixture);
    store.add_server("Local ACE", "127.0.0.1", 9000).unwrap();
    store.save().unwrap();
    assert!(fixture.file_path.exists());
    let reloaded = loaded(&fixture);
    let server = only_server(&reloaded);
    assert_eq!(server.name, "Local ACE");
    assert_eq!(server.host, "127.0.0.1");
    assert_eq!(server.port, 9000);
    assert!(server.accounts.is_empty());
}

#[test]
fn adding_a_server_copies_each_known_account_credential_once_without_characters() {
    let fixture = fixture();
    let mut store = loaded(&fixture);
    store.add_server("First", "first.example", 9000).unwrap();
    store.add_account("First", "shared", "secret").unwrap();
    store.add_server("Second", "second.example", 9000).unwrap();
    store
        .edit_account("Second", "shared", None, Some("second-secret"))
        .unwrap();
    store
        .add_account("Second", "second-only", "other-secret")
        .unwrap();

    store.add_server("Third", "third.example", 9000).unwrap();

    let third = &store.document().servers[2];
    assert_eq!(third.accounts.len(), 2);
    let password = |name: &str| {
        third
            .accounts
            .iter()
            .find(|account| account.account == name)
            .map(|account| account.password.clone())
    };
    assert_eq!(password("shared").as_deref(), Some("secret"));
    assert_eq!(password("second-only").as_deref(), Some("other-secret"));
    assert!(
        third
            .accounts
            .iter()
            .all(|account| account.characters.is_empty())
    );
    assert_eq!(
        account_named(&store, "Second", "shared").password,
        "second-secret"
    );
}

#[test]
fn loading_an_existing_empty_server_populates_it_with_known_accounts_and_persists_them() {
    let fixture = fixture();
    let mut store = loaded(&fixture);
    store.add_server("First", "first.example", 9000).unwrap();
    store.add_account("First", "shared", "secret").unwrap();
    store.add_server("Empty", "empty.example", 9000).unwrap();
    store.save().unwrap();
    // The second server as an older launcher left it: no accounts, never
    // reviewed.
    let mut document = store.document().clone();
    document.servers[1].accounts.clear();
    document.servers[1].account_list_initialized = false;
    std::fs::write(&fixture.file_path, document.to_json()).unwrap();

    let reloaded = loaded(&fixture);
    let second = &reloaded.document().servers[1];
    assert_eq!(second.accounts.len(), 1);
    assert_eq!(second.accounts[0].password, "secret");
    let persisted = loaded(&fixture);
    assert_eq!(persisted.document().servers[1].accounts.len(), 1);
}

#[test]
fn loading_older_partial_servers_adds_missing_accounts_without_changing_existing_passwords() {
    let fixture = fixture();
    let mut store = loaded(&fixture);
    store.add_server("First", "first.example", 9000).unwrap();
    store
        .add_account("First", "shared", "first-secret")
        .unwrap();
    store.add_server("Second", "second.example", 9000).unwrap();
    store
        .edit_account("Second", "shared", None, Some("second-secret"))
        .unwrap();
    store
        .add_account("Second", "second-only", "other-secret")
        .unwrap();
    store.save().unwrap();
    let text = std::fs::read_to_string(&fixture.file_path).unwrap();
    std::fs::write(&fixture.file_path, without_account_list_flags(&text)).unwrap();

    let reloaded = loaded(&fixture);

    let first = &reloaded.document().servers[0];
    let names: Vec<&str> = first.accounts.iter().map(|a| a.account.as_str()).collect();
    assert_eq!(names, ["shared", "second-only"]);
    assert_eq!(first.accounts[0].password, "first-secret");
    assert_eq!(first.accounts[1].password, "other-secret");
    assert_eq!(
        reloaded.document().servers[1].accounts[0].password,
        "second-secret"
    );
}

#[test]
fn intentionally_removing_the_last_account_stays_removed_after_restart() {
    let fixture = fixture();
    let mut store = loaded(&fixture);
    store.add_server("First", "first.example", 9000).unwrap();
    store.add_account("First", "shared", "secret").unwrap();
    store.add_server("Second", "second.example", 9000).unwrap();
    store.remove_account("Second", "shared").unwrap();
    store.save().unwrap();

    let reloaded = loaded(&fixture);
    assert!(reloaded.document().servers[1].accounts.is_empty());
    assert_eq!(reloaded.document().servers[0].accounts.len(), 1);
}

#[test]
fn save_never_writes_show_beta_plugins_when_false() {
    let fixture = fixture();
    let mut store = loaded(&fixture);
    store.add_server("Local ACE", "127.0.0.1", 9000).unwrap();
    store.save().unwrap();
    assert!(
        !std::fs::read_to_string(&fixture.file_path)
            .unwrap()
            .contains("showBetaPlugins")
    );
}

#[test]
fn save_never_writes_an_unset_account_row_selection() {
    let fixture = fixture();
    let mut store = loaded(&fixture);
    store.add_server("Local ACE", "127.0.0.1", 9000).unwrap();
    store.add_account("Local ACE", "testaccount", "pw").unwrap();
    store.save().unwrap();
    let saved = std::fs::read_to_string(&fixture.file_path).unwrap();
    assert!(!saved.contains("selectedCharacter"));
    assert!(!saved.contains("selectedLaunchMode"));
}

#[test]
fn show_beta_plugins_round_trips() {
    let fixture = fixture();
    let mut store = loaded(&fixture);
    store.set_show_beta_plugins(true);
    store.save().unwrap();
    assert!(
        std::fs::read_to_string(&fixture.file_path)
            .unwrap()
            .contains("\"showBetaPlugins\": true")
    );
    assert!(loaded(&fixture).document().show_beta_plugins);
}

#[test]
fn add_server_rejects_duplicate_name() {
    let fixture = fixture();
    let mut store = loaded(&fixture);
    store.add_server("Local ACE", "127.0.0.1", 9000).unwrap();
    let error = store
        .add_server("Local ACE", "127.0.0.1", 9001)
        .unwrap_err();
    assert!(error.to_string().contains("Local ACE"));
}

#[test]
fn add_server_rejects_out_of_range_port() {
    let fixture = fixture();
    let mut store = loaded(&fixture);
    for port in [0, 65536, -1] {
        assert!(is_invalid(store.add_server("Local ACE", "127.0.0.1", port)));
    }
}

#[test]
fn edit_server_renames_and_updates_host_and_port() {
    let fixture = fixture();
    let mut store = loaded(&fixture);
    store.add_server("Local ACE", "127.0.0.1", 9000).unwrap();
    store
        .edit_server("Local ACE", Some("Home ACE"), Some("10.0.0.5"), Some(9001))
        .unwrap();
    let server = only_server(&store);
    assert_eq!(server.name, "Home ACE");
    assert_eq!(server.host, "10.0.0.5");
    assert_eq!(server.port, 9001);
}

#[test]
fn edit_server_on_unknown_name_throws() {
    let fixture = fixture();
    let mut store = loaded(&fixture);
    assert!(is_invalid(store.edit_server(
        "Nope",
        None,
        Some("1.2.3.4"),
        None
    )));
}

#[test]
fn remove_server_removes_it() {
    let fixture = fixture();
    let mut store = loaded(&fixture);
    store.add_server("Local ACE", "127.0.0.1", 9000).unwrap();
    store.remove_server("Local ACE").unwrap();
    assert!(store.document().servers.is_empty());
}

#[test]
fn add_edit_remove_account_round_trip() {
    let fixture = fixture();
    let mut store = loaded(&fixture);
    store.add_server("Local ACE", "127.0.0.1", 9000).unwrap();
    store
        .add_account("Local ACE", "testaccount", "testpassword")
        .unwrap();
    assert_eq!(only_account(&store).account, "testaccount");
    assert_eq!(only_account(&store).password, "testpassword");
    store
        .edit_account("Local ACE", "testaccount", Some("renamed"), Some("newpass"))
        .unwrap();
    assert_eq!(only_account(&store).account, "renamed");
    assert_eq!(only_account(&store).password, "newpass");
    store.remove_account("Local ACE", "renamed").unwrap();
    assert!(only_server(&store).accounts.is_empty());
}

#[test]
fn add_account_rejects_duplicate_account_on_same_server() {
    let fixture = fixture();
    let mut store = loaded(&fixture);
    store.add_server("Local ACE", "127.0.0.1", 9000).unwrap();
    store.add_account("Local ACE", "testaccount", "pw").unwrap();
    assert!(is_invalid(store.add_account(
        "Local ACE",
        "testaccount",
        "pw2"
    )));
}

#[test]
fn character_and_account_setters_change_only_what_they_name() {
    let fixture = fixture();
    let mut store = loaded(&fixture);
    store.add_server("Local ACE", "127.0.0.1", 9000).unwrap();
    store.add_account("Local ACE", "testaccount", "pw").unwrap();
    store
        .merge_roster(
            "Local ACE",
            "testaccount",
            &[CharacterRosterEntry::new(0x5000_000A, "+Acdream", 0)],
        )
        .unwrap();
    store
        .edit_character(
            "Local ACE",
            "testaccount",
            "+Acdream",
            Some(LaunchMode::Headless),
            None,
            None,
        )
        .unwrap();
    store
        .set_character_plugins(
            "Local ACE",
            "testaccount",
            "+Acdream",
            Some(&strings(&["ExamplePlugin"])),
        )
        .unwrap();
    store
        .set_account_login_commands("Local ACE", "testaccount", &strings(&["/tell someone, hi"]))
        .unwrap();
    let account = only_account(&store);
    let character = only_character(&store);
    assert_eq!(character.launch_mode, LaunchMode::Headless);
    assert_eq!(
        character.plugins.as_deref(),
        Some(&strings(&["ExamplePlugin"])[..])
    );
    assert_eq!(account.login_commands, ["/tell someone, hi"]);
    assert_eq!(character.id.as_deref(), Some("0x5000000A"));
}

#[test]
fn add_edit_and_remove_cached_character_round_trips_through_the_crud_surface() {
    let fixture = fixture();
    let mut store = loaded(&fixture);
    store.add_server("Local ACE", "127.0.0.1", 9000).unwrap();
    store.add_account("Local ACE", "testaccount", "pw").unwrap();
    store
        .add_character(
            "Local ACE",
            "testaccount",
            "+Manual",
            Some("0x5000000a"),
            LaunchMode::GuiSelect,
            None,
        )
        .unwrap();
    assert_eq!(only_character(&store).id.as_deref(), Some("0x5000000A"));
    assert_eq!(only_character(&store).launch_mode, LaunchMode::GuiSelect);
    store
        .edit_character(
            "Local ACE",
            "testaccount",
            "+Manual",
            Some(LaunchMode::Headless),
            Some("+Renamed"),
            Some(""),
        )
        .unwrap();
    let character = only_character(&store);
    assert_eq!(character.name, "+Renamed");
    assert_eq!(character.id, None);
    assert_eq!(character.launch_mode, LaunchMode::Headless);
    store
        .remove_character("Local ACE", "testaccount", "+Renamed")
        .unwrap();
    assert!(only_account(&store).characters.is_empty());
}

#[test]
fn add_character_rejects_an_ambiguous_or_invalid_id() {
    let fixture = fixture();
    let mut store = loaded(&fixture);
    store.add_server("Local ACE", "127.0.0.1", 9000).unwrap();
    store.add_account("Local ACE", "testaccount", "pw").unwrap();
    for id in ["5000000A", "0x00000000", "not-an-id"] {
        assert!(
            is_invalid(store.add_character(
                "Local ACE",
                "testaccount",
                "+Manual",
                Some(id),
                LaunchMode::GuiSelect,
                None,
            )),
            "{id}"
        );
    }
}

#[test]
fn full_profile_with_servers_accounts_and_characters_round_trips_through_disk() {
    let fixture = fixture();
    let mut store = loaded(&fixture);
    store.add_server("Local ACE", "127.0.0.1", 9000).unwrap();
    store
        .add_account("Local ACE", "testaccount", "testpassword")
        .unwrap();
    store
        .merge_roster(
            "Local ACE",
            "testaccount",
            &[CharacterRosterEntry::new(0x5000_000A, "+Acdream", 0)],
        )
        .unwrap();
    store
        .edit_character(
            "Local ACE",
            "testaccount",
            "+Acdream",
            Some(LaunchMode::Gui),
            None,
            None,
        )
        .unwrap();
    store
        .set_account_plugins("Local ACE", "testaccount", &strings(&["ExamplePlugin"]))
        .unwrap();
    store
        .set_account_profiles("Local ACE", "testaccount", &strings(&["Main", "Bots"]))
        .unwrap();
    store
        .set_account_login_commands("Local ACE", "testaccount", &strings(&["/vt start"]))
        .unwrap();
    store.save().unwrap();
    let text = std::fs::read_to_string(&fixture.file_path).unwrap();
    assert!(text.replace(' ', "").contains("\"launchMode\":\"gui\""));

    let reloaded = loaded(&fixture);
    let account = only_account(&reloaded);
    let character = only_character(&reloaded);
    assert_eq!(character.name, "+Acdream");
    assert_eq!(character.id.as_deref(), Some("0x5000000A"));
    assert_eq!(character.launch_mode, LaunchMode::Gui);
    assert_eq!(character.plugins, None);
    assert_eq!(account.plugins, ["ExamplePlugin"]);
    assert_eq!(account.profiles, ["Main", "Bots"]);
    assert_eq!(account.login_commands, ["/vt start"]);
}

#[test]
fn load_rejects_unsupported_version() {
    let fixture = fixture();
    // Version 4 is past the current one (3 since logon commands per character).
    std::fs::write(&fixture.file_path, r#"{"version":4,"servers":[]}"#).unwrap();
    assert!(is_invalid(store_at(&fixture.file_path).load()));
}

#[test]
fn load_rejects_unmapped_members_strictly() {
    let fixture = fixture();
    std::fs::write(
        &fixture.file_path,
        r#"{"version":1,"servers":[],"unexpectedField":true}"#,
    )
    .unwrap();
    assert!(is_invalid(store_at(&fixture.file_path).load()));
}

#[test]
fn save_writes_camel_case_json() {
    let fixture = fixture();
    let mut store = loaded(&fixture);
    store.add_server("Local ACE", "127.0.0.1", 9000).unwrap();
    store.save().unwrap();
    let text = std::fs::read_to_string(&fixture.file_path).unwrap();
    assert!(text.contains("\"version\""));
    assert!(text.contains("\"servers\""));
    assert!(text.contains("\"host\""));
    assert!(!text.contains("\"Version\""));
    assert!(!text.contains("\"Servers\""));
}

#[cfg(unix)]
#[test]
fn save_sets_owner_only_permissions_on_unix() {
    use std::os::unix::fs::PermissionsExt;
    let fixture = fixture();
    let mut store = loaded(&fixture);
    store.add_server("Local ACE", "127.0.0.1", 9000).unwrap();
    store
        .add_account("Local ACE", "testaccount", "testpassword")
        .unwrap();
    store.save().unwrap();
    let mode = std::fs::metadata(&fixture.file_path)
        .unwrap()
        .permissions()
        .mode();
    assert_eq!(mode & 0o777, 0o600);
}

#[test]
fn save_deletes_the_stale_temp_file_when_the_final_rename_fails() {
    let fixture = fixture();
    std::fs::create_dir_all(&fixture.file_path).unwrap();
    let mut store = store_at(&fixture.file_path);
    store.add_server("Local ACE", "127.0.0.1", 9000).unwrap();
    store
        .add_account("Local ACE", "testaccount", "testpassword")
        .unwrap();
    assert!(store.save().is_err());
    assert!(!fixture.file_path.with_extension("json.tmp").exists());
}

#[test]
fn load_deletes_a_stale_temp_file_left_behind_by_a_crashed_save() {
    let fixture = fixture();
    let temp = fixture.file_path.with_extension("json.tmp");
    std::fs::write(&temp, r#"{"version":1,"servers":[]}"#).unwrap();
    loaded(&fixture);
    assert!(!temp.exists());
}

// --- LauncherProfileMigrationTests --------------------------------------------

const VERSION1_FILE: &str = r#"{
  "version": 1,
  "servers": [
    {
      "name": "sawato", "host": "localhost", "port": 9000,
      "accounts": [
        {
          "account": "testaccount", "password": "pw1",
          "characters": [
            { "name": "+Mossy", "id": "0x50000027", "launchMode": "guiSelect", "plugins": ["acdream.mosstank", "openac.mosswartmassacre"], "loginCommands": [] },
            { "name": "+Acdream", "id": "0x5000000A", "launchMode": "guiSelect", "plugins": ["openac.mosswartmassacre", "acdream.mosstank"], "loginCommands": [] }
          ]
        },
        { "account": "notan3", "password": "pw3", "characters": [] }
      ]
    },
    {
      "name": "coldeve", "host": "play.coldeve.ac", "port": 9000,
      "accounts": [
        { "account": "testaccount", "password": "pw1", "characters": [] },
        {
          "account": "notan3", "password": "pw3",
          "characters": [
            { "name": "Festivus", "id": "0x5005FBB5", "launchMode": "gui", "plugins": ["acdream.mosstank", "none"], "loginCommands": ["/vt meta load bore", "/vt start"] },
            { "name": "Mule", "launchMode": "guiSelect", "plugins": ["example.arrow"], "loginCommands": ["/mm ws enable"] },
            { "name": "Quiet", "launchMode": "guiSelect", "plugins": ["acdream.mosstank", "example.arrow"], "loginCommands": [] }
          ],
          "selectedCharacter": "Festivus",
          "selectedLaunchMode": "headless"
        }
      ]
    }
  ],
  "users": [
    { "account": "testaccount", "password": "pw1" },
    { "account": "notan3", "password": "pw3" }
  ]
}"#;

struct Migration {
    root: TempDir,
}

impl Migration {
    fn new() -> Self {
        Self {
            root: TempDir::new("migration"),
        }
    }

    fn file_path(&self) -> PathBuf {
        self.root.join(PROFILE_FILE_NAME)
    }

    fn older_file_path(&self) -> PathBuf {
        self.root.join(OLDER_PROFILE_FILE_NAME)
    }

    fn new_store(&self) -> LauncherProfileStore {
        // The settings folder's chain: the version 2 file, then the version 1.
        LauncherProfileStore::with_older_files(
            self.file_path(),
            &[
                &self.root.join(VERSION2_PROFILE_FILE_NAME),
                &self.older_file_path(),
            ],
        )
        .unwrap()
    }

    fn load_version1(&self, json: &str) -> LauncherProfileStore {
        std::fs::write(self.older_file_path(), json).unwrap();
        let mut store = self.new_store();
        assert!(store.load().unwrap());
        store
    }
}

fn character_named<'a>(account: &'a AccountProfile, name: &str) -> &'a CharacterProfile {
    account
        .characters
        .iter()
        .find(|character| character.name == name)
        .unwrap()
}

#[test]
fn an_accounts_plugins_are_every_plugin_its_characters_had_in_first_seen_order() {
    let migration = Migration::new();
    let store = migration.load_version1(VERSION1_FILE);
    assert_eq!(
        account_named(&store, "sawato", "testaccount").plugins,
        ["acdream.mosstank", "openac.mosswartmassacre"]
    );
    assert_eq!(
        account_named(&store, "coldeve", "notan3").plugins,
        ["acdream.mosstank", "example.arrow"]
    );
}

#[test]
fn only_a_character_whose_plugins_differ_from_its_account_keeps_its_own_list() {
    let migration = Migration::new();
    let store = migration.load_version1(VERSION1_FILE);
    let testaccount = account_named(&store, "sawato", "testaccount");
    assert_eq!(character_named(testaccount, "+Mossy").plugins, None);
    let notan3 = account_named(&store, "coldeve", "notan3");
    assert_eq!(
        character_named(notan3, "Festivus").plugins.as_deref(),
        Some(&strings(&["acdream.mosstank"])[..])
    );
    assert_eq!(
        character_named(notan3, "Mule").plugins.as_deref(),
        Some(&strings(&["example.arrow"])[..])
    );
    assert_eq!(character_named(notan3, "Quiet").plugins, None);
}

#[test]
fn a_character_with_the_same_plugins_in_another_order_keeps_its_order() {
    let migration = Migration::new();
    let store = migration.load_version1(VERSION1_FILE);
    let account = account_named(&store, "sawato", "testaccount");
    assert_eq!(character_named(account, "+Mossy").plugins, None);
    assert_eq!(
        character_named(account, "+Acdream").plugins.as_deref(),
        Some(&strings(&["openac.mosswartmassacre", "acdream.mosstank"])[..])
    );
}

// Changed with logon commands per character (document version 3): a
// version 1 character keeps its own commands, so none are dropped and the
// account shares none. Before, the account kept the first character's and
// the notice listed the others.
#[test]
fn every_version1_character_keeps_its_own_commands_and_nothing_is_dropped() {
    let migration = Migration::new();
    let store = migration.load_version1(VERSION1_FILE);
    let notan3 = account_named(&store, "coldeve", "notan3");
    assert!(notan3.login_commands.is_empty());
    assert_eq!(
        character_named(notan3, "Festivus").login_commands,
        ["/vt meta load bore", "/vt start"]
    );
    assert_eq!(
        character_named(notan3, "Mule").login_commands,
        ["/mm ws enable"]
    );
    assert!(character_named(notan3, "Quiet").login_commands.is_empty());
    assert!(
        account_named(&store, "sawato", "testaccount")
            .login_commands
            .is_empty()
    );
    assert_eq!(store.migration_notice(), None);
}

#[test]
fn accounts_whose_characters_agree_raise_no_notice() {
    let migration = Migration::new();
    let json = VERSION1_FILE
        .replace(
            "\"loginCommands\": [\"/mm ws enable\"]",
            "\"loginCommands\": [\"/vt meta load bore\", \"/vt start\"]",
        )
        .replace(
            "\"plugins\": [\"acdream.mosstank\", \"example.arrow\"], \"loginCommands\": []",
            "\"plugins\": [\"acdream.mosstank\", \"example.arrow\"], \"loginCommands\": [\"/vt meta load bore\", \"/vt start\"]",
        );
    let store = migration.load_version1(&json);
    assert_eq!(store.migration_notice(), None);
}

#[test]
fn everything_else_carries_over() {
    let migration = Migration::new();
    let store = migration.load_version1(VERSION1_FILE);
    let notan3 = account_named(&store, "coldeve", "notan3");
    assert_eq!(notan3.password, "pw3");
    assert_eq!(notan3.selected_character.as_deref(), Some("Festivus"));
    assert_eq!(notan3.selected_launch_mode, Some(LaunchMode::Headless));
    let festivus = &notan3.characters[0];
    assert_eq!(
        (
            festivus.name.as_str(),
            festivus.id.as_deref(),
            festivus.launch_mode
        ),
        ("Festivus", Some("0x5005FBB5"), LaunchMode::Gui)
    );
    let names: Vec<&str> = store
        .document()
        .servers
        .iter()
        .map(|server| server.name.as_str())
        .collect();
    assert_eq!(names, ["sawato", "coldeve"]);
    assert_eq!(
        (
            store.document().servers[1].host.as_str(),
            store.document().servers[1].port
        ),
        ("play.coldeve.ac", 9000)
    );
    assert!(
        store
            .document()
            .servers
            .iter()
            .flat_map(|server| server.accounts.iter())
            .all(|account| account.profiles.is_empty())
    );
}

#[test]
fn the_shared_user_list_gives_every_server_its_accounts_as_before() {
    let migration = Migration::new();
    let json = r#"{"version":1,
     "servers":[
       {"name":"one","host":"h","port":9000,"accounts":[{"account":"b","password":"stale","characters":[{"name":"Hero","plugins":["p"],"loginCommands":[]}]}]},
       {"name":"two","host":"h","port":9001,"accounts":[]}],
     "users":[{"account":"a","password":"pa"},{"account":"b","password":"pb"}]}"#;
    let store = migration.load_version1(json);
    for server in &store.document().servers {
        let names: Vec<&str> = server.accounts.iter().map(|a| a.account.as_str()).collect();
        assert_eq!(names, ["a", "b"]);
    }
    let b = account_named(&store, "one", "b");
    assert_eq!(b.password, "pb");
    assert_eq!(b.characters[0].name, "Hero");
    assert_eq!(b.plugins, ["p"]);
    assert!(account_named(&store, "two", "b").characters.is_empty());
}

#[test]
fn the_old_file_is_kept_byte_for_byte_and_the_new_one_written_once() {
    let migration = Migration::new();
    let store = migration.load_version1(VERSION1_FILE);
    assert_eq!(
        std::fs::read_to_string(store.version1_backup_path()).unwrap(),
        VERSION1_FILE
    );
    let written = std::fs::read_to_string(migration.file_path()).unwrap();
    // The current version (3 since logon commands per character).
    assert!(written.contains("\"version\": 3"));
    assert!(!written.contains("\"users\""));

    let mut reloaded = migration.new_store();
    reloaded.load().unwrap();
    assert_eq!(reloaded.migration_notice(), None);
    assert_eq!(store.document(), reloaded.document());
    assert_eq!(
        std::fs::read_to_string(store.version1_backup_path()).unwrap(),
        VERSION1_FILE
    );
    assert_eq!(
        store.version1_backup_path(),
        migration.root.join("launcher-profiles.v1-backup.json")
    );
}

#[test]
fn the_older_launchers_file_is_never_written_so_it_keeps_working() {
    let migration = Migration::new();
    let mut store = migration.load_version1(VERSION1_FILE);
    store
        .execute_transaction(|store| store.add_server("Added later", "h", 9000))
        .unwrap();
    assert_eq!(
        std::fs::read_to_string(migration.older_file_path()).unwrap(),
        VERSION1_FILE
    );
    assert!(
        std::fs::read_to_string(migration.file_path())
            .unwrap()
            .contains("Added later")
    );
}

#[test]
fn once_written_the_new_file_is_read_and_the_older_file_ignored() {
    let migration = Migration::new();
    migration.load_version1(VERSION1_FILE);
    // An older launcher run afterwards goes on using its own file.
    std::fs::write(migration.older_file_path(), r#"{"version":1,"servers":[]}"#).unwrap();
    let mut store = migration.new_store();
    store.load().unwrap();
    let names: Vec<&str> = store
        .document()
        .servers
        .iter()
        .map(|server| server.name.as_str())
        .collect();
    assert_eq!(names, ["sawato", "coldeve"]);
    assert_eq!(store.migration_notice(), None);
}

#[test]
fn with_neither_file_the_profiles_start_empty_and_nothing_is_written() {
    let migration = Migration::new();
    let mut store = migration.new_store();
    assert!(!store.load().unwrap());
    assert_eq!(std::fs::read_dir(&migration.root.path).unwrap().count(), 0);
}

#[test]
fn an_existing_backup_is_never_overwritten() {
    let migration = Migration::new();
    let store = migration.new_store();
    std::fs::write(store.version1_backup_path(), "earlier backup").unwrap();
    migration.load_version1(VERSION1_FILE);
    assert_eq!(
        std::fs::read_to_string(store.version1_backup_path()).unwrap(),
        "earlier backup"
    );
}

#[test]
fn an_invalid_old_file_is_refused_and_left_alone() {
    let migration = Migration::new();
    let json = r#"{"version":1,"servers":[{"name":"a","host":"h","port":0,"accounts":[]}]}"#;
    std::fs::write(migration.older_file_path(), json).unwrap();
    let mut store = migration.new_store();
    assert!(is_invalid(store.load()));
    assert_eq!(
        std::fs::read_to_string(migration.older_file_path()).unwrap(),
        json
    );
    assert!(!store.version1_backup_path().exists());
    assert!(!migration.file_path().exists());
}

// --- LauncherProfileHardeningTests ------------------------------------------

fn populated_store(fixture: &Fixture) -> LauncherProfileStore {
    let mut store = loaded(fixture);
    store.add_server("Local ACE", "127.0.0.1", 9000).unwrap();
    store
        .add_account("Local ACE", "testaccount", "password")
        .unwrap();
    store
        .add_character(
            "Local ACE",
            "testaccount",
            "+Acdream",
            Some("0x5000000A"),
            LaunchMode::Headless,
            Some(&strings(&["ExamplePlugin"])),
        )
        .unwrap();
    store
        .set_account_login_commands("Local ACE", "testaccount", &strings(&["/vt start"]))
        .unwrap();
    store.save().unwrap();
    store
}

#[test]
fn multi_field_edit_validates_everything_before_changing_anything() {
    let fixture = fixture();
    let mut store = populated_store(&fixture);
    assert!(is_invalid(store.edit_server(
        "Local ACE",
        Some("Partially renamed"),
        Some("changed.example.test"),
        Some(0),
    )));
    let server = only_server(&store);
    assert_eq!(server.name, "Local ACE");
    assert_eq!(server.host, "127.0.0.1");
    assert_eq!(server.port, 9000);

    assert!(is_invalid(store.edit_character(
        "Local ACE",
        "testaccount",
        "+Acdream",
        None,
        Some("+PartiallyRenamed"),
        Some("not-an-id"),
    )));
    let character = only_character(&store);
    assert_eq!(character.name, "+Acdream");
    assert_eq!(character.id.as_deref(), Some("0x5000000A"));
}

#[test]
fn transaction_restores_the_exact_document_when_persistence_fails() {
    let fixture = fixture();
    std::fs::create_dir_all(&fixture.file_path).unwrap();
    let mut store = store_at(&fixture.file_path);
    let result: Result<(), ProfileError> =
        store.execute_transaction(|store| store.add_server("Should roll back", "host", 9000));
    assert!(result.is_err());
    assert!(store.document().servers.is_empty());
    assert!(!fixture.file_path.with_extension("json.tmp").exists());
}

#[test]
fn transaction_restores_nested_credential_and_settings_on_mutation_failure() {
    let fixture = fixture();
    let mut store = populated_store(&fixture);
    let before = store.document().clone();
    let result: Result<(), ProfileError> = store.execute_transaction(|store| {
        let account = &mut store.document_mut().servers[0].accounts[0];
        account.password = "transient-secret".into();
        account.plugins.push("Transient.Plugin".into());
        Err(ProfileError::Invalid("simulated mutation failure".into()))
    });
    assert!(result.is_err());
    assert_eq!(store.document(), &before);
    assert_eq!(store.document().to_json(), before.to_json());
}

const INVALID_DOCUMENTS: &[&str] = &[
    r#"{"version":1,"servers":null}"#,
    r#"{"version":1,"servers":[null]}"#,
    r#"{"version":1,"servers":[{"name":" ","host":"h","port":9000,"accounts":[]}]}"#,
    r#"{"version":1,"servers":[{"name":"s","host":" ","port":9000,"accounts":[]}]}"#,
    r#"{"version":1,"servers":[{"name":"s","host":"h","port":0,"accounts":[]}]}"#,
    r#"{"version":1,"servers":[{"name":"s","host":"h","port":9000,"accounts":[]},{"name":"s","host":"h2","port":9001,"accounts":[]}]}"#,
    r#"{"version":1,"servers":[{"name":"s","host":"h","port":9000,"accounts":[{"account":"a","password":"p","characters":[]},{"account":"a","password":"p2","characters":[]}]}]}"#,
    r#"{"version":1,"servers":[{"name":"s","host":"h","port":9000,"accounts":[{"account":"a","password":null,"characters":[]}]}]}"#,
    r#"{"version":1,"servers":[{"name":"s","host":"h","port":9000,"accounts":[{"account":"a","password":"p","characters":null}]}]}"#,
    r#"{"version":1,"servers":[{"name":"s","host":"h","port":9000,"accounts":[{"account":"a","password":"p","characters":[{"name":"c","id":"0x00000000","launchMode":"gui","plugins":[],"loginCommands":[]}]}]}]}"#,
    r#"{"version":1,"servers":[{"name":"s","host":"h","port":9000,"accounts":[{"account":"a","password":"p","characters":[{"name":"c","id":"0x50000001","launchMode":"invalid","plugins":[],"loginCommands":[]}]}]}]}"#,
    r#"{"version":1,"servers":[{"name":"s","host":"h","port":9000,"accounts":[{"account":"a","password":"p","characters":[{"name":"c","id":"0x50000001","launchMode":"gui","plugins":null,"loginCommands":[]}]}]}]}"#,
    r#"{"version":1,"servers":[{"name":"s","host":"h","port":9000,"accounts":[{"account":"a","password":"p","characters":[{"name":"c","id":"0x50000001","launchMode":"gui","plugins":["P","P"],"loginCommands":[]}]}]}]}"#,
    r#"{"version":1,"servers":[{"name":"s","host":"h","port":9000,"accounts":[{"account":"a","password":"p","characters":[{"name":"c","id":"0x50000001","launchMode":"gui","plugins":[],"loginCommands":[" "]}]}]}]}"#,
    r#"{"version":1,"servers":[{"name":"s","host":"h","port":9000,"accounts":[{"account":"a","password":"p","characters":[{"name":"c","id":"0x50000001","launchMode":"gui","plugins":[],"loginCommands":[]},{"name":"c","id":"0x50000002","launchMode":"gui","plugins":[],"loginCommands":[]}]}]}]}"#,
    r#"{"version":1,"servers":[{"name":"s","host":"h","port":9000,"accounts":[{"account":"a","password":"p","characters":[{"name":"c1","id":"0x50000001","launchMode":"gui","plugins":[],"loginCommands":[]},{"name":"c2","id":"0x50000001","launchMode":"gui","plugins":[],"loginCommands":[]}]}]}]}"#,
];

#[test]
fn load_rejects_semantically_invalid_documents() {
    let fixture = fixture();
    for json in INVALID_DOCUMENTS {
        std::fs::write(&fixture.file_path, json).unwrap();
        let mut store = store_at(&fixture.file_path);
        assert!(is_invalid(store.load()), "{json}");
    }
}

#[test]
fn failed_load_does_not_replace_an_already_loaded_document() {
    let fixture = fixture();
    let mut store = populated_store(&fixture);
    std::fs::write(&fixture.file_path, r#"{"version":1,"servers":null}"#).unwrap();
    assert!(is_invalid(store.load()));
    assert_eq!(only_server(&store).name, "Local ACE");
}

#[cfg(unix)]
#[test]
fn unix_load_normalizes_an_existing_credential_file_to_0600_before_reading() {
    use std::os::unix::fs::PermissionsExt;
    let fixture = fixture();
    std::fs::write(&fixture.file_path, r#"{"version":1,"servers":[]}"#).unwrap();
    std::fs::set_permissions(&fixture.file_path, std::fs::Permissions::from_mode(0o644)).unwrap();
    let mut store = store_at(&fixture.file_path);
    assert!(store.load().unwrap());
    let mode = std::fs::metadata(&fixture.file_path)
        .unwrap()
        .permissions()
        .mode();
    assert_eq!(mode & 0o777, 0o600);
}

// --- LauncherProfileTextTests -----------------------------------------------

fn sample() -> LauncherProfileDocument {
    let mut notan3 = AccountProfile::new("notan3", "secret");
    notan3.profiles = strings(&["Bots"]);
    notan3.plugins = strings(&["acdream.mosstank"]);
    notan3.login_commands = strings(&["/vt start"]);
    notan3.selected_character = Some("Festivus".into());
    notan3.selected_launch_mode = Some(LaunchMode::Headless);
    let mut festivus = CharacterProfile::new("Festivus");
    festivus.id = Some("0x5005FBB5".into());
    festivus.launch_mode = LaunchMode::Gui;
    festivus.plugins = Some(strings(&["x.y"]));
    notan3.characters.push(festivus);
    let mut coldeve = ServerProfile::new("Coldeve", "play.coldeve.ac", 9000);
    coldeve.accounts = vec![notan3, AccountProfile::new("notan", "other")];
    let mut testaccount = AccountProfile::new("testaccount", "testpassword");
    testaccount.login_commands = strings(&["/vt nav load bore_circuit1"]);
    let mut sawato = ServerProfile::new("sawato", "localhost", 9000);
    sawato.accounts = vec![testaccount];
    LauncherProfileDocument {
        servers: vec![coldeve, sawato],
        ..LauncherProfileDocument::default()
    }
}

fn apply_error(
    document: &mut LauncherProfileDocument,
    kind: LauncherTextEditorKind,
    text: &str,
) -> String {
    match apply_text(document, kind, text) {
        Err(ProfileError::Invalid(message)) => message,
        other => panic!("expected an error, got {other:?}"),
    }
}

#[test]
fn accounts_read_as_server_sections_with_one_account_per_line() {
    assert_eq!(
        read_text(&sample(), LauncherTextEditorKind::Accounts),
        "#Coldeve\nName=notan3,Password=secret,Profiles=Bots\nName=notan,Password=other\n\n\
         #sawato\nName=testaccount,Password=testpassword\n"
    );
}

#[test]
fn accounts_round_trip_exactly() {
    let mut document = sample();
    document.servers[0].accounts[0].password = " a,b \"c\" \\d ".into();
    document.servers[0].accounts[0].profiles = strings(&["Main", "Bots and Mules"]);
    let before = document.clone();
    let text = read_text(&document, LauncherTextEditorKind::Accounts);
    apply_text(&mut document, LauncherTextEditorKind::Accounts, &text).unwrap();
    assert_eq!(document, before);
}

#[test]
fn accounts_keep_everything_else_about_an_account_that_stays() {
    let mut document = sample();
    apply_text(
        &mut document,
        LauncherTextEditorKind::Accounts,
        "#Coldeve\nName=notan3,Password=changed,Profiles=Main;Bots\nName=notan4,Password=new\n",
    )
    .unwrap();
    let kept = &document.servers[0].accounts[0];
    assert_eq!(kept.password, "changed");
    assert_eq!(kept.profiles, ["Main", "Bots"]);
    assert_eq!(kept.plugins, ["acdream.mosstank"]);
    assert_eq!(kept.login_commands, ["/vt start"]);
    assert_eq!(kept.characters[0].name, "Festivus");
    let names: Vec<&str> = document.servers[0]
        .accounts
        .iter()
        .map(|account| account.account.as_str())
        .collect();
    assert_eq!(names, ["notan3", "notan4"]);
    assert_eq!(document.servers[1].accounts[0].account, "testaccount");
}

#[test]
fn a_server_matches_ignoring_case_and_its_empty_section_removes_its_accounts() {
    let mut document = sample();
    apply_text(
        &mut document,
        LauncherTextEditorKind::Accounts,
        "#COLDEVE\n",
    )
    .unwrap();
    assert!(document.servers[0].accounts.is_empty());
    assert_eq!(document.servers[1].accounts.len(), 1);
}

#[test]
fn a_removed_or_renamed_account_is_described_with_what_is_saved_on_it() {
    let document = sample();
    let removals = describe_account_removals(
        &document,
        "#Coldeve\nName=notan3-renamed,Password=secret\n#sawato\n",
    );
    assert_eq!(
        removals,
        [
            "notan3 on Coldeve (1 character, 1 plugin, 1 logon command)",
            "testaccount on sawato (1 logon command)"
        ]
    );
    assert_eq!(document, sample());
    assert!(describe_account_removals(&document, "#Nowhere\n").is_empty());
}

#[test]
fn an_account_without_password_has_an_empty_one() {
    let mut document = sample();
    apply_text(
        &mut document,
        LauncherTextEditorKind::Accounts,
        "#sawato\r\nName=fresh\r\n",
    )
    .unwrap();
    let account = &document.servers[1].accounts[0];
    assert_eq!(account.account, "fresh");
    assert_eq!(account.password, "");
}

#[test]
fn account_errors_name_their_line_and_change_nothing() {
    let cases = [
        (
            "Name=a,Password=b",
            "Line 1: put a #Server line above the accounts on that server.",
        ),
        (
            "#Nowhere\nName=a,Password=b",
            "Line 1: there is no server named 'Nowhere'. Add it with Add server first.",
        ),
        (
            "#Coldeve\nName=a\nName=a",
            "Line 3: account 'a' appears more than once on this server.",
        ),
        (
            "#Coldeve\nName=a\nName=A",
            "Line 3: account 'A' appears more than once on this server.",
        ),
        (
            "#Coldeve\nName=notan\nName=NOTAN",
            "Line 3: account 'NOTAN' appears more than once on this server.",
        ),
        ("#Coldeve\nPassword=b", "Line 2: each account needs Name=."),
        (
            "#Coldeve\nName=a,Colour=red",
            "Line 2: 'Colour' is not Name, Password or Profiles, or appears twice.",
        ),
        (
            "#Coldeve\nName=a,Password=\"open",
            "Line 2: A quoted value is missing its closing double quote.",
        ),
        (
            "#Coldeve\n\n#coldeve",
            "Line 3: server 'Coldeve' appears more than once.",
        ),
        (
            "#Coldeve\nName=a,Profiles=Bots;bots",
            "Line 2: profile 'bots' is listed twice.",
        ),
        (
            "#Coldeve\njust words",
            "Line 2: expected Name=…,Password=… (a server line starts with #).",
        ),
    ];
    for (text, error) in cases {
        let mut document = sample();
        let message = apply_error(&mut document, LauncherTextEditorKind::Accounts, text);
        assert!(
            message.split('\n').any(|line| line == error),
            "{text}: {message}"
        );
        assert_eq!(document, sample());
    }
}

#[test]
fn every_account_error_is_reported_at_once_without_a_password() {
    let mut document = sample();
    let message = apply_error(
        &mut document,
        LauncherTextEditorKind::Accounts,
        "#Coldeve\nName=a,Password=hunter2,Bad=1\n#Nowhere\nName=b,Password=hunter3",
    );
    assert_eq!(
        message.split('\n').collect::<Vec<_>>(),
        [
            "Line 2: 'Bad' is not Name, Password or Profiles, or appears twice.",
            "Line 3: there is no server named 'Nowhere'. Add it with Add server first."
        ]
    );
    assert!(!message.contains("hunter"));
}

#[test]
fn logon_commands_read_every_server_and_account_with_their_commands() {
    // Every character has its ### header under its account (added with
    // logon commands per character).
    assert_eq!(
        read_text(&sample(), LauncherTextEditorKind::LogonCommands),
        "#Coldeve\n##notan3\n/vt start\n###Festivus\n##notan\n\n#sawato\n##testaccount\n/vt nav \
         load bore_circuit1\n"
    );
}

#[test]
fn logon_commands_round_trip_exactly() {
    let mut document = sample();
    let text = read_text(&document, LauncherTextEditorKind::LogonCommands);
    apply_text(&mut document, LauncherTextEditorKind::LogonCommands, &text).unwrap();
    assert_eq!(document, sample());
}

#[test]
fn logon_commands_replace_each_listed_accounts_commands_in_order() {
    let mut document = sample();
    apply_text(
        &mut document,
        LauncherTextEditorKind::LogonCommands,
        "#coldeve\n##notan\n/mm ws enable\n  /example set navNumber 1  \n\n/vt start\n",
    )
    .unwrap();
    assert_eq!(
        document.servers[0].accounts[1].login_commands,
        ["/mm ws enable", "/example set navNumber 1", "/vt start"]
    );
    // An account left out of a listed server's section runs nothing; a
    // server left out keeps its commands.
    assert!(document.servers[0].accounts[0].login_commands.is_empty());
    assert_eq!(
        document.servers[1].accounts[0].login_commands,
        ["/vt nav load bore_circuit1"]
    );
    assert_eq!(
        document.servers[0].accounts[0].plugins,
        ["acdream.mosstank"]
    );
}

#[test]
fn a_command_starting_with_a_hash_or_backslash_is_written_with_a_backslash_and_round_trips() {
    let mut document = sample();
    document.servers[1].accounts[0].login_commands = strings(&[
        "#not a server",
        "##not an account",
        "\\starts with a backslash",
        "/plain",
    ]);
    let before = document.clone();
    let text = read_text(&document, LauncherTextEditorKind::LogonCommands);
    apply_text(&mut document, LauncherTextEditorKind::LogonCommands, &text).unwrap();
    assert!(
        text.contains("\\#not a server\n\\##not an account\n\\\\starts with a backslash\n/plain")
    );
    assert_eq!(document, before);
}

#[test]
fn logon_command_errors_name_their_line_and_change_nothing() {
    let cases = [
        (
            "/vt start",
            "Line 1: a command needs an ##Account line above it.",
        ),
        ("##notan", "Line 1: put a #Server line above ##Account."),
        (
            "#Coldeve\n/vt start",
            "Line 2: a command needs an ##Account line above it.",
        ),
        (
            "#Coldeve\n##nobody\n/vt start",
            "Line 2: server 'Coldeve' has no account 'nobody'. Add it in Edit accounts first.",
        ),
        (
            "#Nowhere\n##notan\n/vt start",
            "Line 1: there is no server named 'Nowhere'. Add it with Add server first.",
        ),
        (
            "#Coldeve\n##notan\n##notan",
            "Line 3: account 'notan' appears more than once under 'Coldeve'.",
        ),
        (
            "#Coldeve\n#Coldeve",
            "Line 2: server 'Coldeve' appears more than once.",
        ),
    ];
    for (text, error) in cases {
        let mut document = sample();
        let message = apply_error(&mut document, LauncherTextEditorKind::LogonCommands, text);
        assert!(
            message.split('\n').any(|line| line == error),
            "{text}: {message}"
        );
        assert_eq!(document, sample());
    }
}

#[test]
fn names_with_edge_spaces_quotes_or_a_leading_hash_round_trip() {
    for kind in [
        LauncherTextEditorKind::Accounts,
        LauncherTextEditorKind::LogonCommands,
    ] {
        let mut document = sample();
        document.servers[0].name = " spaced \"server\" ".into();
        document.servers[1].name = "#hash".into();
        document.servers[0].accounts[0].account = "#notan3".into();
        document.servers[0].accounts[1].account = " a,b ".into();
        let before = document.clone();
        let text = read_text(&document, kind);
        apply_text(&mut document, kind, &text).unwrap();
        assert_eq!(document, before, "{kind:?}");
    }
}

#[test]
fn server_endpoint_edit_retains_accounts() {
    let mut document = sample();
    apply_text(
        &mut document,
        LauncherTextEditorKind::Servers,
        "Coldeve | new.example | 9010",
    )
    .unwrap();
    assert_eq!(document.servers.len(), 1);
    assert_eq!(document.servers[0].host, "new.example");
    assert_eq!(
        document.servers[0].accounts[0].characters[0].name,
        "Festivus"
    );
}

#[test]
fn adding_a_server_in_the_text_editor_copies_known_credentials() {
    let mut document = sample();
    apply_text(
        &mut document,
        LauncherTextEditorKind::Servers,
        "Coldeve | play.coldeve.ac | 9000\nsawato | localhost | 9000\nDreamWeave | game.example | \
         9000",
    )
    .unwrap();
    let added = &document.servers[2];
    let names: Vec<&str> = added.accounts.iter().map(|a| a.account.as_str()).collect();
    assert_eq!(names, ["notan3", "notan", "testaccount"]);
    assert_eq!(added.accounts[0].password, "secret");
    assert!(added.accounts[0].characters.is_empty());
    assert!(added.accounts[0].plugins.is_empty());
    assert_eq!(document.servers[0].accounts[0].characters.len(), 1);
}

#[test]
fn quoted_server_separators_round_trip() {
    let mut document = sample();
    document.servers[0].name = "Server, One | Test".into();
    let text = read_text(&document, LauncherTextEditorKind::Servers);
    apply_text(&mut document, LauncherTextEditorKind::Servers, &text).unwrap();
    assert_eq!(document.servers[0].name, "Server, One | Test");
}

#[test]
fn a_failed_disk_write_rolls_back_the_whole_account_edit() {
    let fixture = fixture();
    std::fs::create_dir_all(&fixture.file_path).unwrap();
    let mut store = store_at(&fixture.file_path);
    store.add_server("One", "localhost", 9000).unwrap();
    store
        .add_account("One", "Original", "original-password")
        .unwrap();
    let result: Result<(), ProfileError> = store.execute_transaction(|store| {
        apply_text(
            store.document_mut(),
            LauncherTextEditorKind::Accounts,
            "#One\nName=New,Password=new-password",
        )
    });
    assert!(matches!(result, Err(ProfileError::Io(_))));
    assert_eq!(store.document().servers[0].accounts[0].account, "Original");
    assert_eq!(
        store.document().servers[0].accounts[0].password,
        "original-password"
    );
}

// --- RosterMergeTests -------------------------------------------------------

fn store_with_server_and_account(fixture: &Fixture) -> LauncherProfileStore {
    let mut store = loaded(fixture);
    store.add_server("Local ACE", "127.0.0.1", 9000).unwrap();
    store
        .add_account("Local ACE", "testaccount", "testpassword")
        .unwrap();
    store
}

#[test]
fn first_merge_adds_new_characters_with_default_launch_mode_gui_select() {
    let fixture = fixture();
    let mut store = store_with_server_and_account(&fixture);
    store
        .merge_roster(
            "Local ACE",
            "testaccount",
            &[
                CharacterRosterEntry::new(0x5000_000A, "+Acdream", 0),
                CharacterRosterEntry::new(0x5000_000B, "+Second", 0),
            ],
        )
        .unwrap();
    let characters = &only_account(&store).characters;
    assert_eq!(characters.len(), 2);
    let first = characters.iter().find(|c| c.name == "+Acdream").unwrap();
    assert_eq!(first.id.as_deref(), Some("0x5000000A"));
    assert_eq!(first.launch_mode, LaunchMode::GuiSelect);
    assert_eq!(first.plugins, None);
    let second = characters.iter().find(|c| c.name == "+Second").unwrap();
    assert_eq!(second.id.as_deref(), Some("0x5000000B"));
    assert_eq!(second.launch_mode, LaunchMode::GuiSelect);
}

#[test]
fn second_merge_preserves_user_settings_on_an_existing_character() {
    let fixture = fixture();
    let mut store = store_with_server_and_account(&fixture);
    let roster = [CharacterRosterEntry::new(0x5000_000A, "+Acdream", 0)];
    store
        .merge_roster("Local ACE", "testaccount", &roster)
        .unwrap();
    store
        .edit_character(
            "Local ACE",
            "testaccount",
            "+Acdream",
            Some(LaunchMode::Headless),
            None,
            None,
        )
        .unwrap();
    store
        .set_character_plugins(
            "Local ACE",
            "testaccount",
            "+Acdream",
            Some(&strings(&["ExamplePlugin"])),
        )
        .unwrap();
    store
        .merge_roster("Local ACE", "testaccount", &roster)
        .unwrap();
    let character = only_character(&store);
    assert_eq!(character.launch_mode, LaunchMode::Headless);
    assert_eq!(
        character.plugins.as_deref(),
        Some(&strings(&["ExamplePlugin"])[..])
    );
}

#[test]
fn merge_updates_name_when_id_matches_but_display_name_changed() {
    let fixture = fixture();
    let mut store = store_with_server_and_account(&fixture);
    store
        .merge_roster(
            "Local ACE",
            "testaccount",
            &[CharacterRosterEntry::new(0x5000_000A, "+OldName", 0)],
        )
        .unwrap();
    store
        .merge_roster(
            "Local ACE",
            "testaccount",
            &[CharacterRosterEntry::new(0x5000_000A, "+NewName", 0)],
        )
        .unwrap();
    let character = only_character(&store);
    assert_eq!(character.name, "+NewName");
    assert_eq!(character.id.as_deref(), Some("0x5000000A"));
}

#[test]
fn character_absent_from_a_later_roster_snapshot_is_retained() {
    let fixture = fixture();
    let mut store = store_with_server_and_account(&fixture);
    store
        .merge_roster(
            "Local ACE",
            "testaccount",
            &[
                CharacterRosterEntry::new(0x5000_000A, "+Acdream", 0),
                CharacterRosterEntry::new(0x5000_000B, "+PendingDelete", 1),
            ],
        )
        .unwrap();
    store
        .merge_roster(
            "Local ACE",
            "testaccount",
            &[CharacterRosterEntry::new(0x5000_000A, "+Acdream", 0)],
        )
        .unwrap();
    let characters = &only_account(&store).characters;
    assert_eq!(characters.len(), 2);
    assert!(characters.iter().any(|c| c.name == "+Acdream"));
    assert!(characters.iter().any(|c| c.name == "+PendingDelete"));
}

#[test]
fn merge_normalizes_an_unprefixed_hex_id_instead_of_creating_a_duplicate_row() {
    let fixture = fixture();
    let mut store = store_with_server_and_account(&fixture);
    let mut legacy = CharacterProfile::new("+Acdream");
    legacy.id = Some("5000000A".into());
    store.document_mut().servers[0].accounts[0]
        .characters
        .push(legacy);
    store
        .merge_roster(
            "Local ACE",
            "testaccount",
            &[CharacterRosterEntry::new(0x5000_000A, "+Acdream", 0)],
        )
        .unwrap();
    let character = only_character(&store);
    assert_eq!(character.id.as_deref(), Some("0x5000000A"));
    assert_eq!(character.name, "+Acdream");
}

#[test]
fn same_name_roster_entry_corrects_a_wrong_valid_id_and_preserves_settings() {
    let fixture = fixture();
    let mut store = store_with_server_and_account(&fixture);
    store
        .add_character(
            "Local ACE",
            "testaccount",
            "+Acdream",
            Some("0x50000001"),
            LaunchMode::Headless,
            Some(&strings(&["ExamplePlugin"])),
        )
        .unwrap();
    store
        .merge_roster(
            "Local ACE",
            "testaccount",
            &[CharacterRosterEntry::new(0x5000_000A, "+Acdream", 0)],
        )
        .unwrap();
    let character = only_character(&store);
    assert_eq!(character.id.as_deref(), Some("0x5000000A"));
    assert_eq!(character.launch_mode, LaunchMode::Headless);
    assert_eq!(
        character.plugins.as_deref(),
        Some(&strings(&["ExamplePlugin"])[..])
    );
}

#[test]
fn authoritative_merge_collapses_correct_id_and_same_name_duplicates() {
    let fixture = fixture();
    let mut store = store_with_server_and_account(&fixture);
    {
        let account = &mut store.document_mut().servers[0].accounts[0];
        let mut canonical = CharacterProfile::new("+OldName");
        canonical.id = Some("0x5000000A".into());
        canonical.launch_mode = LaunchMode::Headless;
        canonical.plugins = Some(strings(&["Canonical.Plugin"]));
        let mut duplicate = CharacterProfile::new("+Acdream");
        duplicate.id = Some("0x50000001".into());
        duplicate.launch_mode = LaunchMode::Gui;
        duplicate.plugins = Some(strings(&["Duplicate.Plugin"]));
        account.characters.push(canonical);
        account.characters.push(duplicate);
    }
    store
        .merge_roster(
            "Local ACE",
            "testaccount",
            &[CharacterRosterEntry::new(0x5000_000A, "+Acdream", 0)],
        )
        .unwrap();
    let character = only_character(&store);
    assert_eq!(character.id.as_deref(), Some("0x5000000A"));
    assert_eq!(character.name, "+Acdream");
    assert_eq!(character.launch_mode, LaunchMode::Headless);
    assert_eq!(
        character.plugins.as_deref(),
        Some(&strings(&["Canonical.Plugin"])[..])
    );
}

#[test]
fn merge_throws_for_unknown_server_or_account() {
    let fixture = fixture();
    let mut store = store_with_server_and_account(&fixture);
    let roster = [CharacterRosterEntry::new(1, "x", 0)];
    assert!(is_invalid(store.merge_roster(
        "Nope",
        "testaccount",
        &roster
    )));
    assert!(is_invalid(store.merge_roster("Local ACE", "nope", &roster)));
}

// --- Checked accounts (OpenAC LauncherCheckedAccountStore) -------------------

#[test]
fn checked_accounts_round_trip_and_a_missing_file_is_none() {
    let fixture = fixture();
    let path = fixture.root.join("launcher-checked-accounts.json");
    let mut store = LauncherCheckedAccountStore::new(&path);
    store.load().unwrap();
    assert!(!store.contains("Two", "Alice"));
    store
        .save([
            LauncherCheckedAccount::new("Two", "Alice"),
            LauncherCheckedAccount::new("One", "Bob"),
        ])
        .unwrap();
    let mut reopened = LauncherCheckedAccountStore::new(&path);
    reopened.load().unwrap();
    assert!(reopened.contains("Two", "Alice"));
    assert!(reopened.contains("One", "Bob"));
    assert!(!reopened.contains("One", "Alice"));
    // Written in server, then account order.
    let text = std::fs::read_to_string(&path).unwrap();
    assert!(text.find("\"One\"").unwrap() < text.find("\"Two\"").unwrap());
}

// OpenAC LauncherWindowViewModel.CheckedAccounts.Tests
// InvalidSelectionFileIsReportedAndPreserved (the store's part).
#[test]
fn an_invalid_checked_accounts_file_is_an_error_and_left_alone() {
    let fixture = fixture();
    let path = fixture.root.join("launcher-checked-accounts.json");
    let text = r#"[{"server":null,"account":"Alice"}]"#;
    std::fs::write(&path, text).unwrap();
    let mut store = LauncherCheckedAccountStore::new(&path);
    assert!(store.load().is_err());
    assert_eq!(std::fs::read_to_string(&path).unwrap(), text);
}

// --- Account names ignore case ------------------------------------------------
//
// The server keeps one account per name whatever its case, so `Fefe` and
// `fefe` are the same account. Names stay as they were typed.

/// A document saved by a launcher that compared names exactly: `Fefe` and
/// `fefe` on one server, each with something of its own.
fn case_twins() -> LauncherProfileDocument {
    let mut upper = AccountProfile::new("Fefe", "upper-secret");
    upper.characters.push(CharacterProfile::new("Upper"));
    upper.login_commands = strings(&["/upper"]);
    let mut lower = AccountProfile::new("fefe", "lower-secret");
    lower.characters.push(CharacterProfile::new("Lower"));
    lower.login_commands = strings(&["/lower"]);
    let mut server = ServerProfile::new("Local ACE", "127.0.0.1", 9000);
    server.accounts = vec![upper, lower];
    server.account_list_initialized = true;
    LauncherProfileDocument {
        servers: vec![server],
        ..LauncherProfileDocument::default()
    }
}

// OpenAC LauncherProfileStoreTests.AccountNamesThatDifferOnlyInCaseAreOneAccount.
#[test]
fn add_account_refuses_a_name_that_differs_only_in_case() {
    let fixture = fixture();
    let mut store = loaded(&fixture);
    store.add_server("Local ACE", "127.0.0.1", 9000).unwrap();
    store.add_account("Local ACE", "Fefe", "pw").unwrap();
    assert!(is_invalid(store.add_account("Local ACE", "fefe", "pw2")));
    assert!(is_invalid(store.add_account("Local ACE", "FEFE", "pw2")));
    assert_eq!(only_account(&store).account, "Fefe");
    assert_eq!(only_account(&store).password, "pw");
}

// OpenAC LauncherProfileStoreTests.AccountNamesThatDifferOnlyInCaseAreOneAccount.
#[test]
fn an_account_is_found_by_any_case_and_renaming_it_to_another_case_keeps_the_new_spelling() {
    let fixture = fixture();
    let mut store = loaded(&fixture);
    store.add_server("Local ACE", "127.0.0.1", 9000).unwrap();
    store.add_account("Local ACE", "Fefe", "pw").unwrap();
    store.add_account("Local ACE", "other", "pw").unwrap();
    store
        .set_account_plugins("Local ACE", "fefe", &strings(&["x.y"]))
        .unwrap();
    store
        .edit_account("Local ACE", "FEFE", Some("fEfE"), None)
        .unwrap();
    let names: Vec<&str> = only_server(&store)
        .accounts
        .iter()
        .map(|account| account.account.as_str())
        .collect();
    assert_eq!(names, ["fEfE", "other"]);
    assert_eq!(only_server(&store).accounts[0].plugins, ["x.y"]);
    // Renaming another account onto it is a duplicate.
    assert!(is_invalid(store.edit_account(
        "Local ACE",
        "other",
        Some("FEFE"),
        None
    )));
}

// OpenAC LauncherProfileStoreTests.AFileWithTwoSpellingsOfOneAccountKeepsBothAndRefusesAThird.
#[test]
fn a_saved_file_with_two_cases_of_one_account_keeps_both_and_refuses_a_third() {
    let fixture = fixture();
    std::fs::write(&fixture.file_path, case_twins().to_json()).unwrap();
    let mut store = loaded(&fixture);
    assert_eq!(store.document(), &case_twins());
    assert!(is_invalid(store.add_account("Local ACE", "FEFE", "pw")));
    // Each spelling finds its own entry; a name spelled neither way finds
    // the first in the file.
    store
        .edit_account("Local ACE", "fefe", None, Some("lower-changed"))
        .unwrap();
    store
        .edit_account("Local ACE", "FEFE", None, Some("first-changed"))
        .unwrap();
    let server = only_server(&store);
    assert_eq!(server.accounts[0].password, "first-changed");
    assert_eq!(server.accounts[1].password, "lower-changed");
}

// OpenAC LauncherProfileStoreTests.AccountNamesThatDifferOnlyInCaseAreOneAccount (its new-server part).
#[test]
fn an_unreviewed_server_does_not_get_another_case_of_an_account_it_has() {
    let fixture = fixture();
    let mut document = case_twins();
    document.servers[0].accounts.truncate(1);
    let mut second = ServerProfile::new("Second", "second.example", 9000);
    second.accounts = vec![AccountProfile::new("FEFE", "second-secret")];
    document.servers.push(second);
    std::fs::write(
        &fixture.file_path,
        without_account_list_flags(&document.to_json()),
    )
    .unwrap();
    let mut store = loaded(&fixture);
    let names = |store: &LauncherProfileStore, index: usize| -> Vec<String> {
        store.document().servers[index]
            .accounts
            .iter()
            .map(|account| account.account.clone())
            .collect()
    };
    assert_eq!(names(&store, 0), ["Fefe"]);
    assert_eq!(names(&store, 1), ["FEFE"]);
    store.add_server("Third", "third.example", 9000).unwrap();
    assert_eq!(names(&store, 2), ["Fefe"]);
}

// OpenAC LauncherProfileTextTests (the case-variant duplicate rows of the account errors).
#[test]
fn text_import_refuses_a_second_case_of_one_account() {
    let mut document = sample();
    let before = document.clone();
    let message = apply_error(
        &mut document,
        LauncherTextEditorKind::Accounts,
        "#Coldeve\nName=notan3,Password=secret\nName=NOTAN3,Password=other\n",
    );
    assert!(message.contains("Line 3"), "{message}");
    assert!(message.contains("more than once"), "{message}");
    assert_eq!(document, before);
}

// OpenAC LauncherProfileTextTests.AnAccountRetypedInAnotherCaseIsTheSameAccountUnderTheNewSpelling.
#[test]
fn text_import_of_another_case_keeps_the_account_and_takes_the_new_spelling() {
    let mut document = sample();
    let text = "#Coldeve\nName=NOTAN3,Password=secret,Profiles=Bots\nName=notan,Password=other\n";
    assert!(describe_account_removals(&document, text).is_empty());
    apply_text(&mut document, LauncherTextEditorKind::Accounts, text).unwrap();
    let kept = &document.servers[0].accounts[0];
    assert_eq!(kept.account, "NOTAN3");
    assert_eq!(kept.characters[0].name, "Festivus");
    assert_eq!(kept.login_commands, ["/vt start"]);
}

// OpenAC LauncherProfileTextTests.TwoSavedSpellingsOfOneAccountRoundTripButATextCannotAddAThird.
#[test]
fn text_of_a_file_with_two_cases_of_one_account_round_trips_and_refuses_a_third() {
    let mut document = case_twins();
    for kind in [
        LauncherTextEditorKind::Accounts,
        LauncherTextEditorKind::LogonCommands,
    ] {
        let text = read_text(&document, kind);
        assert!(describe_account_removals(&document, &text).is_empty());
        apply_text(&mut document, kind, &text).unwrap();
        assert_eq!(document, case_twins(), "{kind:?}");
    }
    let message = apply_error(
        &mut document,
        LauncherTextEditorKind::Accounts,
        "#Local ACE\nName=Fefe,Password=a\nName=fefe,Password=b\nName=FEFE,Password=c\n",
    );
    assert!(message.contains("Line 4"), "{message}");
    assert_eq!(document, case_twins());
}

// OpenAC LauncherProfileMigrationTests.TheSharedUserListFindsAServersAccountWhateverTheCaseOfItsName.
#[test]
fn migration_keeps_an_accounts_characters_when_the_user_list_spells_it_in_another_case() {
    let migration = Migration::new();
    let json = r#"{"version":1,
     "servers":[
       {"name":"one","host":"h","port":9000,"accounts":[{"account":"b","password":"stale","characters":[{"name":"Hero","plugins":[],"loginCommands":[]}]}]}],
     "users":[{"account":"B","password":"pb"}]}"#;
    let store = migration.load_version1(json);
    let account = only_account(&store);
    assert_eq!(account.account, "B");
    assert_eq!(account.password, "pb");
    assert_eq!(account.characters[0].name, "Hero");
}

// OpenAC LauncherWindowViewModelTests.ACheckedAccountMatchesItsRowWhateverTheCaseOfItsName (the store side).
#[test]
fn a_checked_account_matches_its_row_in_any_case() {
    let fixture = fixture();
    let path = fixture.root.join("launcher-checked-accounts.json");
    let mut store = LauncherCheckedAccountStore::new(&path);
    store
        .save([LauncherCheckedAccount::new("Two", "alice")])
        .unwrap();
    let mut reopened = LauncherCheckedAccountStore::new(&path);
    reopened.load().unwrap();
    assert!(reopened.contains("Two", "Alice"));
    assert!(reopened.contains("Two", "ALICE"));
    assert!(!reopened.contains("two", "alice"));
    assert!(
        std::fs::read_to_string(&path)
            .unwrap()
            .contains("\"alice\"")
    );
}

// OpenAC LauncherProfileTextTests.LogonCommandsFindAnAccountNamedInAnotherCase.
#[test]
fn logon_commands_find_an_account_named_in_another_case() {
    let mut document = sample();
    apply_text(
        &mut document,
        LauncherTextEditorKind::LogonCommands,
        "#Coldeve\n##NOTAN\n/vt start\n",
    )
    .unwrap();
    assert_eq!(
        document.servers[0].accounts[1].login_commands,
        ["/vt start"]
    );
    assert!(document.servers[0].accounts[0].login_commands.is_empty());
}

// --- Profiles on characters, the profile list, commands per character ------

/// Coldeve with "alpha" (Ann, Bea) and "beta" (Cid).
fn character_store(fixture: &Fixture) -> LauncherProfileStore {
    let mut store = loaded(fixture);
    store
        .add_server("Coldeve", "play.example.test", 9000)
        .unwrap();
    for (account, characters) in [("alpha", &["Ann", "Bea"][..]), ("beta", &["Cid"][..])] {
        store.add_account("Coldeve", account, "pw").unwrap();
        for name in characters {
            store
                .add_character("Coldeve", account, name, None, LaunchMode::Gui, None)
                .unwrap();
        }
    }
    store.save().unwrap();
    store
}

fn tags<'a>(store: &'a LauncherProfileStore, account: &str, character: &str) -> &'a [String] {
    &character_named(account_named(store, "Coldeve", account), character).profiles
}

#[test]
fn a_profile_is_on_one_character_per_account() {
    let fixture = fixture();
    let mut store = character_store(&fixture);
    store
        .set_character_profiles("Coldeve", "alpha", "Ann", &strings(&["Main"]))
        .unwrap();
    store
        .set_character_profiles("Coldeve", "beta", "Cid", &strings(&["Main"]))
        .unwrap();
    store
        .set_character_profiles("Coldeve", "alpha", "Bea", &strings(&["main", "Bots"]))
        .unwrap();
    // Main (in any case) leaves Ann; another account's character keeps it.
    assert!(tags(&store, "alpha", "Ann").is_empty());
    assert_eq!(tags(&store, "alpha", "Bea"), ["main", "Bots"]);
    assert_eq!(tags(&store, "beta", "Cid"), ["Main"]);
    assert!(is_invalid(store.set_character_profiles(
        "Coldeve",
        "alpha",
        "Ann",
        &strings(&["Bad,Name"])
    )));
}

#[test]
fn a_profile_on_two_characters_of_one_account_is_refused() {
    let fixture = fixture();
    let mut store = character_store(&fixture);
    let result = store.execute_transaction(|store| {
        let alpha = &mut store.document_mut().servers[0].accounts[0];
        alpha.characters[0].profiles = strings(&["Main"]);
        alpha.characters[1].profiles = strings(&["MAIN"]);
        Ok::<(), ProfileError>(())
    });
    match result {
        Err(ProfileError::Invalid(message)) => assert_eq!(
            message,
            "The profile 'MAIN' is on more than one character of account 'alpha' on server \
             'Coldeve'."
        ),
        other => panic!("expected a refusal, got {other:?}"),
    }
    assert!(tags(&store, "alpha", "Ann").is_empty());
}

#[test]
fn a_new_profile_exists_empty_survives_a_restart_and_tags_in_use_join_the_list() {
    let fixture = fixture();
    let mut store = character_store(&fixture);
    store
        .execute_transaction(|store| store.create_profile("Spare"))
        .unwrap();
    store
        .execute_transaction(|store| {
            store.set_character_profiles("Coldeve", "alpha", "Ann", &strings(&["Main"]))
        })
        .unwrap();
    store
        .execute_transaction(|store| {
            store.set_account_profiles("Coldeve", "beta", &strings(&["Bots"]))
        })
        .unwrap();
    assert_eq!(store.document().profiles, ["Spare", "Main", "Bots"]);
    let reloaded = loaded(&fixture);
    assert_eq!(reloaded.document().profiles, ["Spare", "Main", "Bots"]);
    assert_eq!(tags(&reloaded, "alpha", "Ann"), ["Main"]);
}

#[test]
fn create_profile_refuses_an_empty_duplicate_or_unusable_name() {
    let fixture = fixture();
    let mut store = character_store(&fixture);
    store.create_profile("Main").unwrap();
    for name in ["", "   ", "MAIN", "main", "a,b", "a;b", "x#", " Main2"] {
        assert!(is_invalid(store.create_profile(name)), "{name:?}");
    }
    assert_eq!(store.document().profiles, ["Main"]);
    match store.create_profile("main") {
        Err(ProfileError::Invalid(message)) => {
            assert_eq!(message, "A profile named 'main' already exists.");
        }
        other => panic!("expected a refusal, got {other:?}"),
    }
}

#[test]
fn delete_profile_takes_it_off_the_list_every_account_and_every_character() {
    let fixture = fixture();
    let mut store = character_store(&fixture);
    store
        .execute_transaction(|store| {
            store.set_character_profiles("Coldeve", "alpha", "Ann", &strings(&["Main", "Bots"]))?;
            store.set_character_profiles("Coldeve", "beta", "Cid", &strings(&["main"]))?;
            store.set_account_profiles("Coldeve", "alpha", &strings(&["MAIN"]))?;
            store.set_account_profiles("Coldeve", "beta", &strings(&["Bots"]))
        })
        .unwrap();
    store
        .execute_transaction(|store| store.delete_profile("Main"))
        .unwrap();
    assert_eq!(store.document().profiles, ["Bots"]);
    assert_eq!(tags(&store, "alpha", "Ann"), ["Bots"]);
    assert!(tags(&store, "beta", "Cid").is_empty());
    assert!(
        account_named(&store, "Coldeve", "alpha")
            .profiles
            .is_empty()
    );
    assert_eq!(account_named(&store, "Coldeve", "beta").profiles, ["Bots"]);
    assert!(is_invalid(store.delete_profile("Main")));
    // Gone after a restart too.
    let reloaded = loaded(&fixture);
    assert_eq!(reloaded.document().profiles, ["Bots"]);
}

#[test]
fn character_profiles_and_commands_round_trip_through_disk() {
    let fixture = fixture();
    let mut store = character_store(&fixture);
    store
        .execute_transaction(|store| {
            store.set_character_profiles("Coldeve", "alpha", "Bea", &strings(&["Main"]))?;
            store.set_character_login_commands(
                "Coldeve",
                "alpha",
                "Bea",
                &strings(&["/vt start", "/mm ws enable"]),
            )
        })
        .unwrap();
    let text = std::fs::read_to_string(&fixture.file_path).unwrap();
    assert!(text.contains("\"version\": 3"));
    // Left out while empty: Ann has neither.
    assert_eq!(text.matches("\"loginCommands\"").count(), 3);
    let reloaded = loaded(&fixture);
    assert_eq!(reloaded.document(), store.document());
    let bea = character_named(account_named(&reloaded, "Coldeve", "alpha"), "Bea");
    assert_eq!(bea.login_commands, ["/vt start", "/mm ws enable"]);
}

// The logon commands text with ### character headers.

fn character_sample() -> LauncherProfileDocument {
    let mut document = sample();
    let notan3 = &mut document.servers[0].accounts[0];
    notan3.characters[0].login_commands = strings(&["/vt meta load bore", "#hash command"]);
    notan3.characters.push(CharacterProfile::new("Mule"));
    document
}

#[test]
fn logon_commands_show_shared_lines_then_each_characters_own_and_round_trip_exactly() {
    let mut document = character_sample();
    let text = read_text(&document, LauncherTextEditorKind::LogonCommands);
    assert!(text.starts_with(
        "#Coldeve\n##notan3\n/vt start\n###Festivus\n/vt meta load bore\n\\#hash command\n\
         ###Mule\n##notan\n"
    ));
    apply_text(&mut document, LauncherTextEditorKind::LogonCommands, &text).unwrap();
    assert_eq!(document, character_sample());
    assert_eq!(
        read_text(&document, LauncherTextEditorKind::LogonCommands),
        text
    );
}

#[test]
fn logon_commands_put_lines_under_a_character_on_it_and_clear_a_character_left_out() {
    let mut document = character_sample();
    apply_text(
        &mut document,
        LauncherTextEditorKind::LogonCommands,
        "#Coldeve\n##notan3\n/fellowship quit\n###mule\n/mm ws enable\n/vt start\n##notan\n",
    )
    .unwrap();
    let notan3 = &document.servers[0].accounts[0];
    assert_eq!(notan3.login_commands, ["/fellowship quit"]);
    // Found ignoring case, as the one character of that name.
    assert_eq!(
        character_named(notan3, "Mule").login_commands,
        ["/mm ws enable", "/vt start"]
    );
    // Festivus is left out of the listed account: no commands of its own.
    assert!(
        character_named(notan3, "Festivus")
            .login_commands
            .is_empty()
    );
    // A server left out keeps everything.
    assert_eq!(
        document.servers[1].accounts[0].login_commands,
        ["/vt nav load bore_circuit1"]
    );
}

#[test]
fn a_bad_character_header_is_refused_with_its_line_and_changes_nothing() {
    let cases = [
        (
            "#Coldeve\n##notan3\n###Nobody\n/vt start",
            "Line 3: account 'notan3' on 'Coldeve' has no character 'Nobody'. Refresh its \
             characters, or add the character first.",
        ),
        (
            "#Coldeve\n##notan3\n###Festivus\n/a\n###Festivus\n/b",
            "Line 5: character 'Festivus' appears more than once under 'notan3'.",
        ),
        (
            "#Coldeve\n###Festivus\n/a",
            "Line 2: put an ##Account line above ###Character.",
        ),
        (
            "###Festivus",
            "Line 1: put an ##Account line above ###Character.",
        ),
    ];
    for (text, error) in cases {
        let mut document = character_sample();
        let message = apply_error(&mut document, LauncherTextEditorKind::LogonCommands, text);
        assert!(
            message.split('\n').any(|line| line == error),
            "{text}: {message}"
        );
        assert_eq!(document, character_sample());
    }
}

// The upgrade from version 2: each account's commands move to a character.

const VERSION2_FILE: &str = r#"{
  "version": 2,
  "servers": [
    {
      "name": "Coldeve", "host": "play.example.test", "port": 9000,
      "accounts": [
        {
          "account": "picked", "password": "pw-picked", "profiles": ["Main"], "plugins": [],
          "loginCommands": ["/vt start"],
          "characters": [
            { "name": "Ann", "id": null, "launchMode": "gui" },
            { "name": "Bea", "id": "0x50000002", "launchMode": "gui" }
          ],
          "selectedCharacter": "Bea", "selectedLaunchMode": "headless"
        },
        {
          "account": "select", "password": "pw-select", "profiles": [], "plugins": [],
          "loginCommands": ["/a", "/b"],
          "characters": [
            { "name": "Cid", "id": null, "launchMode": "guiSelect" },
            { "name": "Dee", "id": null, "launchMode": "gui" }
          ],
          "selectedLaunchMode": "guiSelect"
        },
        {
          "account": "lonely", "password": "pw-lonely", "profiles": ["Bots"], "plugins": [],
          "loginCommands": ["/shared"], "characters": []
        },
        {
          "account": "quiet", "password": "pw-quiet", "profiles": [], "plugins": [],
          "loginCommands": [],
          "characters": [ { "name": "Eve", "id": null, "launchMode": "gui" } ]
        }
      ],
      "accountListInitialized": true
    }
  ]
}"#;

impl Migration {
    /// The version 2 launcher's own file.
    fn version2_file_path(&self) -> PathBuf {
        self.root.join(VERSION2_PROFILE_FILE_NAME)
    }

    fn load_version2(&self, json: &str) -> LauncherProfileStore {
        std::fs::write(self.version2_file_path(), json).unwrap();
        let mut store = self.new_store();
        assert!(store.load().unwrap());
        store
    }
}

#[test]
fn the_upgrade_moves_an_accounts_commands_to_the_character_it_launches() {
    let migration = Migration::new();
    let store = migration.load_version2(VERSION2_FILE);
    let picked = account_named(&store, "Coldeve", "picked");
    assert!(picked.login_commands.is_empty());
    assert_eq!(character_named(picked, "Bea").login_commands, ["/vt start"]);
    assert!(character_named(picked, "Ann").login_commands.is_empty());
    // Everything else stays as it was.
    assert_eq!(picked.selected_character.as_deref(), Some("Bea"));
    assert_eq!(picked.selected_launch_mode, Some(LaunchMode::Headless));
    assert_eq!(picked.profiles, ["Main"]);
    assert_eq!(store.document().profiles, ["Main", "Bots"]);
}

#[test]
fn the_upgrade_gives_a_character_select_accounts_commands_to_its_first_character() {
    let migration = Migration::new();
    let store = migration.load_version2(VERSION2_FILE);
    let select = account_named(&store, "Coldeve", "select");
    assert!(select.login_commands.is_empty());
    assert_eq!(character_named(select, "Cid").login_commands, ["/a", "/b"]);
    assert!(character_named(select, "Dee").login_commands.is_empty());
}

#[test]
fn the_upgrade_keeps_the_commands_of_an_account_without_characters_shared() {
    let migration = Migration::new();
    let store = migration.load_version2(VERSION2_FILE);
    assert_eq!(
        account_named(&store, "Coldeve", "lonely").login_commands,
        ["/shared"]
    );
    let quiet = account_named(&store, "Coldeve", "quiet");
    assert!(quiet.login_commands.is_empty());
    assert!(quiet.characters[0].login_commands.is_empty());
}

#[test]
fn the_upgrade_notice_lists_every_move_and_names_the_kept_version2_file() {
    let migration = Migration::new();
    let store = migration.load_version2(VERSION2_FILE);
    let notice = store.migration_notice().unwrap();
    assert_eq!(
        notice,
        format!(
            "Logon commands now belong to characters: a character runs its own, and commands \
             left on an account do not run. Each account's commands moved to the character it \
             launches (the first character when it starts at the character screen):\n\nColdeve \
             / picked: logon commands moved to Bea\nColdeve / select: logon commands moved to \
             Cid\nColdeve / lonely: logon commands stay on the account, where they do not run; \
             move them under a character (the account has no characters yet)\n\nThe old file is \
             kept at {}.",
            migration.version2_file_path().display()
        )
    );
    assert!(!notice.contains("pw-"));
}

// Changed with the version 3 file name: the version 2 file stays the older
// launcher's own (and so the backup); before, version 3 was written over it
// and a copy kept as launcher-profiles.v2-backup.json.
#[test]
fn the_upgrade_writes_the_version3_file_and_never_touches_the_version2_one() {
    let migration = Migration::new();
    let store = migration.load_version2(VERSION2_FILE);
    assert_eq!(
        std::fs::read_to_string(migration.version2_file_path()).unwrap(),
        VERSION2_FILE
    );
    assert_eq!(
        migration.file_path(),
        migration.root.join("launcher-profiles.v3.json")
    );
    let written = std::fs::read_to_string(migration.file_path()).unwrap();
    assert!(written.contains("\"version\": 3"));
    assert!(
        !migration
            .root
            .join("launcher-profiles.v2-backup.json")
            .exists()
    );

    // Read again: the version 3 file wins, nothing moves and nothing is said,
    // whatever an older launcher wrote to its own file meanwhile.
    std::fs::write(
        migration.version2_file_path(),
        VERSION2_FILE.replace("picked", "renamed"),
    )
    .unwrap();
    let mut reloaded = migration.new_store();
    reloaded.load().unwrap();
    assert_eq!(reloaded.migration_notice(), None);
    assert_eq!(reloaded.document(), store.document());

    // An edit writes only the version 3 file.
    reloaded
        .execute_transaction(|store| store.add_server("Added later", "h", 9000))
        .unwrap();
    assert!(
        !std::fs::read_to_string(migration.version2_file_path())
            .unwrap()
            .contains("Added later")
    );
}

#[test]
fn the_version2_file_wins_over_the_version1_file() {
    let migration = Migration::new();
    std::fs::write(migration.older_file_path(), VERSION1_FILE).unwrap();
    let store = migration.load_version2(VERSION2_FILE);
    let names: Vec<&str> = store
        .document()
        .servers
        .iter()
        .map(|server| server.name.as_str())
        .collect();
    assert_eq!(names, ["Coldeve"]);
    assert!(!store.version1_backup_path().exists());
}

#[test]
fn a_version2_file_without_commands_upgrades_without_a_notice() {
    let migration = Migration::new();
    let json = r#"{"version":2,"servers":[{"name":"a","host":"h","port":9000,"accounts":[]}]}"#;
    let store = migration.load_version2(json);
    assert_eq!(store.migration_notice(), None);
    assert_eq!(store.document().version, 3);
    assert_eq!(
        std::fs::read_to_string(migration.version2_file_path()).unwrap(),
        json
    );
}

// --- A failed load or migration write -----------------------------------

#[test]
fn after_a_failed_load_nothing_is_saved_until_a_load_succeeds() {
    let migration = Migration::new();
    // A file from a newer launcher this one cannot read.
    let newer = r#"{"version":4,"servers":[{"name":"keep","host":"h","port":9000}]}"#;
    std::fs::write(migration.file_path(), newer).unwrap();
    let mut store = migration.new_store();
    assert!(is_invalid(store.load()));
    assert!(store.load_failure().is_some());

    let refused = store.execute_transaction(|store| store.add_server("New", "h", 9000));
    match refused {
        Err(ProfileError::Invalid(message)) => assert!(
            message.starts_with(
                "The profiles could not be read, so nothing is saved until they load:"
            ),
            "{message}"
        ),
        other => panic!("expected a refusal, got {other:?}"),
    }
    assert!(is_invalid(store.save()));
    assert_eq!(
        std::fs::read_to_string(migration.file_path()).unwrap(),
        newer
    );
    assert!(store.document().servers.is_empty());

    // Once a load succeeds, edits save again.
    std::fs::write(migration.file_path(), r#"{"version":3,"servers":[]}"#).unwrap();
    store.load().unwrap();
    assert_eq!(store.load_failure(), None);
    store
        .execute_transaction(|store| store.add_server("New", "h", 9000))
        .unwrap();
}

#[test]
fn a_failed_migration_write_leaves_the_store_as_it_was() {
    let migration = Migration::new();
    std::fs::write(migration.version2_file_path(), VERSION2_FILE).unwrap();
    // The version 3 file's folder is a file, so writing it fails.
    let blocker = migration.root.join("blocker");
    std::fs::write(&blocker, "a file, not a folder").unwrap();
    let mut store = LauncherProfileStore::with_older_files(
        blocker.join(PROFILE_FILE_NAME),
        &[&migration.version2_file_path()],
    )
    .unwrap();
    assert!(store.load().is_err());
    assert!(store.document().servers.is_empty());
    assert_eq!(store.migration_notice(), None);
    assert!(store.load_failure().is_some());
    assert_eq!(
        std::fs::read_to_string(migration.version2_file_path()).unwrap(),
        VERSION2_FILE
    );
}

// --- A roster merge keeps a duplicate's profiles and commands -------------

#[test]
fn merging_a_duplicate_moves_its_profiles_and_its_commands_when_the_kept_has_none() {
    let fixture = fixture();
    let mut store = character_store(&fixture);
    {
        // Ann has the character's id; Ann2 its new name, added by hand.
        let alpha = &mut store.document_mut().servers[0].accounts[0];
        alpha.characters[0].id = Some("0x50000001".into());
        alpha.characters[0].profiles = vec!["Main".into()];
        alpha.characters[1].name = "Ann2".into();
        alpha.characters[1].profiles = vec!["Bots".into()];
        alpha.characters[1].login_commands = vec!["/vt start".into()];
    }
    // The roster names the id's character Ann2: both entries are one.
    let notes = store
        .merge_roster(
            "Coldeve",
            "alpha",
            &[CharacterRosterEntry::new(0x5000_0001, "Ann2", 0)],
        )
        .unwrap();
    assert!(notes.is_empty());
    let alpha = account_named(&store, "Coldeve", "alpha");
    assert_eq!(alpha.characters.len(), 1);
    let kept = &alpha.characters[0];
    assert_eq!(kept.name, "Ann2");
    assert_eq!(kept.profiles, ["Main", "Bots"]);
    assert_eq!(kept.login_commands, ["/vt start"]);
}

#[test]
fn merging_a_duplicate_keeps_the_kept_characters_commands_and_says_what_was_dropped() {
    let fixture = fixture();
    let mut store = character_store(&fixture);
    {
        let alpha = &mut store.document_mut().servers[0].accounts[0];
        alpha.characters[0].id = Some("0x50000001".into());
        alpha.characters[0].login_commands = vec!["/mine".into()];
        alpha.characters[1].name = "Ann2".into();
        alpha.characters[1].login_commands = vec!["/theirs".into(), "/more".into()];
    }
    let notes = store
        .merge_roster(
            "Coldeve",
            "alpha",
            &[CharacterRosterEntry::new(0x5000_0001, "Ann2", 0)],
        )
        .unwrap();
    assert_eq!(
        notes,
        [
            "Ann2 was listed twice on alpha; its logon commands were kept and the other entry's \
          were dropped: /theirs | /more"
        ]
    );
    let kept = &account_named(&store, "Coldeve", "alpha").characters[0];
    assert_eq!(kept.login_commands, ["/mine"]);
}
