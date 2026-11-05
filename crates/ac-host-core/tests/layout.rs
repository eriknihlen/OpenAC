//! The folder layout: moving an older root into it, session folders, and
//! the cleanup of old logs.

mod common;

use std::path::{Path, PathBuf};
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use ac_host_core::layout::{
    self, LauncherSessionFolder, MigrationError, clean_old_logs, create_session_folder, migrate,
    parse_old_session_id, parse_session_folder_name, session_folder_name,
};
use ac_host_core::local_time::LocalZone;
use ac_host_core::paths::file_name_safe;
use ac_host_core::{InstallSessionLease, PathSet, RootSource};
use common::TempDir;

/// 2026-11-02 12:00:00 UTC.
const NOW_MS: u64 = 1_793_620_800_000;

fn now() -> SystemTime {
    UNIX_EPOCH + Duration::from_millis(NOW_MS)
}

fn utc() -> LocalZone {
    LocalZone::fixed(0)
}

fn write(path: &Path, text: &str) {
    std::fs::create_dir_all(path.parent().unwrap()).unwrap();
    std::fs::write(path, text).unwrap();
}

fn read(path: &Path) -> String {
    std::fs::read_to_string(path).unwrap()
}

/// Sets a file's last write time to `seconds` after the epoch.
fn touch(path: &Path, seconds: u64) {
    let file = std::fs::OpenOptions::new().write(true).open(path).unwrap();
    file.set_modified(UNIX_EPOCH + Duration::from_secs(seconds))
        .unwrap();
}

fn root(name: &str) -> (TempDir, PathSet) {
    let dir = TempDir::new(name);
    let paths = PathSet::for_root(&dir.path, RootSource::Explicit);
    (dir, paths)
}

/// An old session folder name for a UTC start `ms` after the epoch.
fn old_session_name(ms: u64, hex: char) -> String {
    let seconds = i64::try_from(ms / 1000).unwrap();
    let (year, month, day) = ac_host_core::status::civil_from_days(seconds.div_euclid(86_400));
    let second = seconds.rem_euclid(86_400);
    format!(
        "{year:04}{month:02}{day:02}{:02}{:02}{:02}{:03}-{}",
        second / 3600,
        second / 60 % 60,
        second % 60,
        ms % 1000,
        hex.to_string().repeat(32)
    )
}

fn old_session_document(host: &str, port: u16, account: &str) -> String {
    format!(
        "{{ \"version\": 1, \"process\": {{}}, \"sessions\": [ {{ \"id\": \"x\", \
         \"endpoint\": {{ \"host\": \"{host}\", \"port\": {port} }}, \
         \"account\": \"{account}\" }} ] }}"
    )
}

fn profiles(paths: &PathSet) {
    write(
        &paths.launcher_profiles_file(),
        r#"{ "version": 2, "servers": [ { "name": "Coldeve", "host": "play.example.net", "port": 9000, "accounts": [] } ] }"#,
    );
}

/// An older root with something in every place the migration moves.
fn old_root(paths: &PathSet) -> PathBuf {
    let root = paths.root_directory().to_path_buf();
    write(&root.join("data").join("pak").join("acdream.pak"), "pak");
    let package = root.join("data").join("pak").join("acdream.pak");
    write(
        &root.join("data").join("install.json"),
        &format!(
            "{{\n  \"datDirectory\": {},\n  \"preparedAssetPath\": {},\n  \"version\": 1\n}}",
            quoted(Path::new("C:/Games/AC")),
            quoted(&package)
        ),
    );
    write(
        &root
            .join("plugins")
            .join("acdream.mosstank")
            .join("files")
            .join("settings.json"),
        "mine",
    );
    write(
        &root
            .join("plugins")
            .join("acdream.mosstank")
            .join("files")
            .join("debug")
            .join("macro-20261101-abc.log"),
        "macro",
    );
    write(
        &root
            .join("plugins")
            .join("acdream.mosstank")
            .join("plugin.json"),
        "{}",
    );
    write(
        &root
            .join("plugins")
            .join("acdream.mosswartmassacre")
            .join("files")
            .join("state.json"),
        "mm",
    );
    write(
        &root
            .join("vtank")
            .join("mosstank")
            .join("metas")
            .join("a.met"),
        "meta",
    );
    root
}

fn quoted(path: &Path) -> String {
    let mut text = String::new();
    ac_host_core::json::write_string(&mut text, &path.to_string_lossy());
    text
}

#[test]
fn an_older_root_moves_into_the_current_layout() {
    let (_dir, paths) = root("layout-full");
    let root = old_root(&paths);
    profiles(&paths);
    let recent = old_session_name(NOW_MS - 3_600_000, 'a');
    let session = paths.launcher_log_directory().join(&recent);
    write(
        &session.join("session.json"),
        &old_session_document("play.example.net", 9000, "bot1"),
    );
    write(
        &session.join("status.jsonl"),
        "{\"v\":1,\"e\":\"started\",\"sessionId\":\"x\"}\n\
         {\"v\":1,\"e\":\"enteredWorld\",\"sessionId\":\"x\",\"characterId\":1,\"characterName\":\"Tester\"}\n",
    );
    write(&session.join("client.err.log"), "errors");

    let migration = migrate(&paths, now(), &utc()).unwrap();
    assert!(migration.moved);
    assert!(layout::is_current(&paths));

    // 1. data -> game, with its record naming the moved package.
    assert!(!root.join("data").exists());
    let game = paths.game_data_directory();
    assert_eq!(read(&game.join("pak").join("acdream.pak")), "pak");
    let record = read(&game.join("install.json"));
    let document = ac_host_core::json::parse(record.as_bytes()).unwrap();
    let members = ac_host_core::json::object_members(&document).unwrap();
    let value = |name: &str| {
        ac_host_core::json::string_or_null(&members.iter().find(|m| m.name == name).unwrap().value)
            .unwrap()
            .unwrap()
    };
    assert_eq!(
        PathBuf::from(value("preparedAssetPath")),
        game.join("pak").join("acdream.pak")
    );
    assert_eq!(value("datDirectory"), "C:/Games/AC");

    // 2. files -> data, for every plugin.
    assert_eq!(
        read(
            &paths
                .plugin_files_directory("acdream.mosstank")
                .join("settings.json")
        ),
        "mine"
    );
    assert_eq!(
        read(
            &paths
                .plugin_files_directory("acdream.mosswartmassacre")
                .join("state.json")
        ),
        "mm"
    );
    assert!(
        !root
            .join("plugins")
            .join("acdream.mosstank")
            .join("files")
            .exists()
    );

    // 3. vtank -> MossTank's folder.
    assert!(!root.join("vtank").exists());
    assert_eq!(
        read(
            &paths
                .vtank_profiles_directory()
                .join("mosstank")
                .join("metas")
                .join("a.met")
        ),
        "meta"
    );

    // 4. the macro debug logs leave the plugin's data.
    assert_eq!(
        read(
            &paths
                .old_plugin_logs_directory()
                .join("macro-20261101-abc.log")
        ),
        "macro"
    );
    assert!(
        !paths
            .plugin_files_directory("acdream.mosstank")
            .join("debug")
            .exists()
    );

    // 5. the session folder, under the profile's server name and the
    // character that entered the world, named by its local start.
    assert!(!session.exists());
    let moved = paths
        .session_logs_directory("Coldeve", "Tester")
        .join("2026-11-02 11-00-00");
    assert_eq!(read(&moved.join("client.err.log")), "errors");
    assert!(moved.join("session.json").is_file());
}

#[test]
fn a_current_root_is_left_alone_and_a_fresh_one_only_gets_its_marker() {
    let (_dir, paths) = root("layout-fresh");
    let migration = migrate(&paths, now(), &utc()).unwrap();
    assert!(migration.moved);
    assert!(layout::is_current(&paths));
    assert_eq!(
        read(&paths.layout_marker_file()).trim(),
        "{ \"version\": 2 }"
    );
    // Once marked, an old-looking folder is never looked at again.
    write(&paths.root_directory().join("vtank").join("x.txt"), "x");
    assert_eq!(
        migrate(&paths, now(), &utc()).unwrap(),
        layout::Migration::default()
    );
    assert!(paths.root_directory().join("vtank").join("x.txt").is_file());
}

#[test]
fn a_running_client_holds_the_migration_off() {
    let (_dir, paths) = root("layout-busy");
    old_root(&paths);
    let lease = InstallSessionLease::try_acquire_shared(&paths)
        .unwrap()
        .unwrap();
    let error = migrate(&paths, now(), &utc()).unwrap_err();
    assert!(matches!(error, MigrationError::Busy));
    assert!(
        error
            .message(&paths)
            .starts_with("Could not move your files to the new layout: ")
    );
    assert!(
        error
            .message(&paths)
            .ends_with("is in use. Close all OpenAC clients and start the launcher again.")
    );
    assert!(paths.root_directory().join("data").is_dir());
    assert!(!layout::is_current(&paths));
    drop(lease);
    assert!(migrate(&paths, now(), &utc()).unwrap().moved);
}

#[test]
fn a_collision_keeps_the_newer_in_place_and_the_other_beside_it() {
    let (_dir, paths) = root("layout-collide");
    let root = paths.root_directory().to_path_buf();
    let old_newer = root.join("vtank").join("a.met");
    let old_older = root.join("vtank").join("b.met");
    write(&old_newer, "old newer");
    write(&old_older, "old older");
    touch(&old_newer, 2_000_000_000);
    touch(&old_older, 1_000_000_000);
    let new_a = paths.vtank_profiles_directory().join("a.met");
    let new_b = paths.vtank_profiles_directory().join("b.met");
    write(&new_a, "new older");
    write(&new_b, "new newer");
    touch(&new_a, 1_500_000_000);
    touch(&new_b, 1_500_000_000);

    let migration = migrate(&paths, now(), &utc()).unwrap();
    let vtank = paths.vtank_profiles_directory();
    assert_eq!(read(&vtank.join("a.met")), "old newer");
    assert_eq!(read(&vtank.join("a.old.met")), "new older");
    assert_eq!(read(&vtank.join("b.met")), "new newer");
    assert_eq!(read(&vtank.join("b.old.met")), "old older");
    assert_eq!(
        migration
            .notes
            .iter()
            .filter(|note| note.contains("were both there"))
            .count(),
        2
    );
}

#[test]
fn old_session_folders_are_dated_deleted_or_set_aside() {
    let (_dir, paths) = root("layout-sessions");
    let launcher = paths.launcher_log_directory();
    // Eight days old: deleted.
    let stale = launcher.join(old_session_name(NOW_MS - 8 * 86_400_000, 'b'));
    write(
        &stale.join("session.json"),
        &old_session_document("h", 1, "a"),
    );
    // No character ever entered: filed under the account, the host when no
    // profile names the server.
    let select = launcher.join(old_session_name(NOW_MS - 60_000, 'c'));
    write(
        &select.join("session.json"),
        &old_session_document("10.0.0.5", 9000, "player"),
    );
    // Two in the same second: the second gets " (2)".
    let first = launcher.join(old_session_name(NOW_MS - 120_000, 'd'));
    let second = launcher.join(old_session_name(NOW_MS - 120_000 + 1, 'e'));
    for folder in [&first, &second] {
        write(
            &folder.join("session.json"),
            &old_session_document("10.0.0.5", 9000, "x"),
        );
    }
    // Unreadable: unsorted.
    let broken_name = old_session_name(NOW_MS - 1000, 'f');
    write(&launcher.join(&broken_name).join("client.err.log"), "e");
    // Not a session folder at all: left alone.
    write(&launcher.join("notes").join("keep.txt"), "keep");
    write(&launcher.join("launcher.log"), "log");

    // Two hours east of UTC.
    let migration = migrate(&paths, now(), &LocalZone::fixed(7200)).unwrap();
    assert!(!stale.exists());
    assert!(migration.notes.iter().any(|note| note.contains("Deleted")));
    assert!(
        paths
            .session_logs_directory("10.0.0.5", "player")
            .join("2026-11-02 13-59-00")
            .join("session.json")
            .is_file()
    );
    let x = paths.session_logs_directory("10.0.0.5", "x");
    assert!(x.join("2026-11-02 13-58-00").is_dir());
    assert!(x.join("2026-11-02 13-58-00 (2)").is_dir());
    assert_eq!(
        read(
            &paths
                .unsorted_logs_directory()
                .join(&broken_name)
                .join("client.err.log")
        ),
        "e"
    );
    assert_eq!(read(&launcher.join("notes").join("keep.txt")), "keep");
    assert_eq!(read(&launcher.join("launcher.log")), "log");
}

#[test]
fn the_staging_and_trash_copies_of_plugin_files_are_renamed_too() {
    let (_dir, paths) = root("layout-trash");
    let trash = paths
        .plugins_directory()
        .join(".trash")
        .join("acdream.mosstank-0123");
    write(&trash.join("files").join("x.json"), "x");
    migrate(&paths, now(), &utc()).unwrap();
    assert_eq!(read(&trash.join("data").join("x.json")), "x");
}

#[test]
fn a_session_folder_is_named_by_its_local_start_and_never_reused() {
    let (_dir, paths) = root("layout-create");
    let zone = LocalZone::fixed(3600);
    let first = create_session_folder(&paths, "Coldeve", "Tester", now(), &zone).unwrap();
    let second = create_session_folder(&paths, "Coldeve", "Tester", now(), &zone).unwrap();
    let third = create_session_folder(&paths, "Coldeve", "Tester", now(), &zone).unwrap();
    let parent = paths.logs_directory().join("Coldeve").join("Tester");
    assert_eq!(first, parent.join("2026-11-02 13-00-00"));
    assert_eq!(second, parent.join("2026-11-02 13-00-00 (2)"));
    assert_eq!(third, parent.join("2026-11-02 13-00-00 (3)"));
    for folder in [&first, &second, &third] {
        assert!(folder.is_dir());
    }
}

#[test]
fn session_folder_names_parse_back() {
    let local = i64::try_from(NOW_MS).unwrap();
    let name = session_folder_name(local);
    assert_eq!(name, "2026-11-02 12-00-00");
    assert_eq!(parse_session_folder_name(&name), Some(local));
    assert_eq!(
        parse_session_folder_name("2026-11-02 12-00-00 (12)"),
        Some(local)
    );
    for bad in [
        "2026-11-02 12-00-00 ()",
        "2026-11-02 12-00-00 (x)",
        "2026-13-02 12-00-00",
        "2026-11-02T12-00-00",
        "crash",
    ] {
        assert_eq!(parse_session_folder_name(bad), None, "{bad}");
    }
    assert_eq!(
        parse_old_session_id(&old_session_name(NOW_MS + 123, 'a')),
        Some(i64::try_from(NOW_MS + 123).unwrap())
    );
    assert_eq!(parse_old_session_id("20261102120000123-short"), None);
}

#[test]
fn names_are_made_file_name_safe() {
    assert_eq!(file_name_safe("Coldeve"), "Coldeve");
    assert_eq!(
        file_name_safe("a<b>c:d\"e/f\\g|h?i*j"),
        "a_b_c_d_e_f_g_h_i_j"
    );
    assert_eq!(file_name_safe("tab\there"), "tab_here");
    assert_eq!(file_name_safe("dots... "), "dots____");
    assert_eq!(file_name_safe(""), "_");
    assert_eq!(file_name_safe("."), "_");
    assert_eq!(file_name_safe(".."), "__");
    assert_eq!(file_name_safe("CON"), "CON_");
    assert_eq!(file_name_safe("con.txt"), "con_.txt");
    assert_eq!(file_name_safe("COM1"), "COM1_");
    assert_eq!(file_name_safe("LPT9"), "LPT9_");
    assert_eq!(file_name_safe("COM0"), "COM0");
    assert_eq!(file_name_safe("Console"), "Console");
    assert_eq!(file_name_safe("+Acdream"), "+Acdream");
    let (_dir, paths) = root("layout-names");
    assert_eq!(
        paths.session_logs_directory("crash", "NUL"),
        paths.logs_directory().join("crash_").join("NUL_")
    );
    assert_eq!(
        paths.session_logs_directory("Old Plugin Logs", "x"),
        paths.logs_directory().join("Old Plugin Logs_").join("x")
    );
}

#[test]
fn the_cleanup_deletes_what_is_older_than_a_week_but_never_a_running_session() {
    let (_dir, paths) = root("layout-cleanup");
    let zone = utc();
    let day = 86_400_000;
    let at = |ms: u64| UNIX_EPOCH + Duration::from_millis(ms);
    let old = create_session_folder(&paths, "S", "Old", at(NOW_MS - 8 * day), &zone).unwrap();
    let recent = create_session_folder(&paths, "S", "New", at(NOW_MS - 6 * day), &zone).unwrap();
    let running =
        create_session_folder(&paths, "S", "Running", at(NOW_MS - 9 * day), &zone).unwrap();
    write(&old.join("client.log"), "x");
    write(&running.join("session.json"), "{}");
    let other = paths.logs_directory().join("S").join("Old").join("notes");
    write(&other.join("keep.txt"), "keep");
    let claim = LauncherSessionFolder::claim(&paths, &running.join("session.json")).unwrap();
    assert_eq!(claim.directory(), running.as_path());

    let crash_old = paths.crash_reports_directory().join("crash-old.json");
    let crash_new = paths.crash_reports_directory().join("crash-new.json");
    let plugin_old = paths.old_plugin_logs_directory().join("macro-old.log");
    for file in [&crash_old, &crash_new, &plugin_old] {
        write(file, "x");
    }
    touch(&crash_old, (NOW_MS - 8 * day) / 1000);
    touch(&crash_new, (NOW_MS - day) / 1000);
    touch(&plugin_old, (NOW_MS - 30 * day) / 1000);
    // A server folder holding only an old session goes with it.
    let lonely = create_session_folder(&paths, "Gone", "C", at(NOW_MS - 10 * day), &zone).unwrap();

    let failures = clean_old_logs(&paths, now(), &zone);
    assert!(failures.is_empty(), "{failures:?}");
    assert!(!old.exists());
    assert!(other.join("keep.txt").is_file());
    assert!(recent.is_dir());
    assert!(running.is_dir(), "a running session's folder stays");
    assert!(!crash_old.exists());
    assert!(crash_new.exists());
    assert!(!plugin_old.exists());
    assert!(!lonely.exists());
    assert!(!paths.logs_directory().join("Gone").exists());

    drop(claim);
    clean_old_logs(&paths, now(), &zone);
    assert!(!running.exists());
}

#[test]
fn only_a_launcher_session_document_has_a_session_folder() {
    let (_dir, paths) = root("layout-claim");
    let folder = create_session_folder(&paths, "S", "C", now(), &utc()).unwrap();
    write(&folder.join("session.json"), "{}");
    assert!(LauncherSessionFolder::claim(&paths, &folder.join("session.json")).is_some());
    let by_hand = paths.root_directory().join("bot.json");
    write(&by_hand, "{}");
    assert!(LauncherSessionFolder::claim(&paths, &by_hand).is_none());
    let renamed = folder.join("other.json");
    write(&renamed, "{}");
    assert!(LauncherSessionFolder::claim(&paths, &renamed).is_none());
}

/// Makes `link` a link to the folder `target`: a directory symbolic link,
/// or on Windows a junction when symbolic links are not allowed. `false`
/// when the platform allows neither (the test then has nothing to check).
fn link_folder(link: &Path, target: &Path) -> bool {
    #[cfg(unix)]
    {
        std::os::unix::fs::symlink(target, link).is_ok()
    }
    #[cfg(windows)]
    {
        if std::os::windows::fs::symlink_dir(target, link).is_ok() {
            return true;
        }
        std::process::Command::new("cmd.exe")
            .args(["/d", "/c", "mklink", "/J"])
            .arg(link)
            .arg(target)
            .stdout(std::process::Stdio::null())
            .stderr(std::process::Stdio::null())
            .status()
            .is_ok_and(|status| status.success())
    }
    #[cfg(not(any(unix, windows)))]
    {
        false
    }
}

#[test]
fn a_crash_between_the_two_renames_of_a_collision_finishes_on_the_next_run() {
    let (_dir, paths) = root("layout-resume-collision");
    let root = paths.root_directory().to_path_buf();
    // The first run found both a.met files, set the older (already in
    // place) aside as a.old.met, and stopped before moving the newer in.
    write(&root.join("vtank").join("a.met"), "newer");
    write(&paths.vtank_profiles_directory().join("a.old.met"), "older");
    assert!(!layout::is_current(&paths));

    migrate(&paths, now(), &utc()).unwrap();

    let vtank = paths.vtank_profiles_directory();
    assert_eq!(read(&vtank.join("a.met")), "newer");
    assert_eq!(read(&vtank.join("a.old.met")), "older");
    assert!(!root.join("vtank").exists());
    assert!(layout::is_current(&paths));
}

#[test]
fn a_crash_after_the_plugin_step_finishes_on_the_next_run() {
    let (_dir, paths) = root("layout-resume-steps");
    let root = old_root(&paths);
    // Steps 1 and 2 ran their renames (data/ is game/, files/ is data/) and
    // the run stopped before rewriting the record and before step 3, so the
    // record, vtank/, the macro logs and the marker are as they were.
    std::fs::rename(root.join("data"), paths.game_data_directory()).unwrap();
    for plugin in ["acdream.mosstank", "acdream.mosswartmassacre"] {
        let folder = root.join("plugins").join(plugin);
        std::fs::rename(folder.join("files"), folder.join("data")).unwrap();
    }
    assert!(!layout::is_current(&paths));

    migrate(&paths, now(), &utc()).unwrap();

    assert!(layout::is_current(&paths));
    assert_eq!(
        read(&paths.game_data_directory().join("pak").join("acdream.pak")),
        "pak"
    );
    assert!(read(&paths.game_data_directory().join("install.json")).contains("game"));
    let data = paths.plugin_files_directory("acdream.mosstank");
    assert_eq!(read(&data.join("settings.json")), "mine");
    assert!(!data.join("debug").exists());
    assert_eq!(
        read(
            &paths
                .old_plugin_logs_directory()
                .join("macro-20261101-abc.log")
        ),
        "macro"
    );
    assert_eq!(
        read(
            &paths
                .vtank_profiles_directory()
                .join("mosstank")
                .join("metas")
                .join("a.met")
        ),
        "meta"
    );
    assert!(!root.join("vtank").exists());
}

#[test]
fn a_folder_wins_its_name_over_a_file_of_that_name() {
    let (_dir, paths) = root("layout-file-named-data");
    let plugin = paths.plugins_directory().join("acdream.mosstank");
    write(&plugin.join("files").join("settings.json"), "mine");
    // A file named `data`, written after the folder.
    write(&plugin.join("data"), "a stray file");
    touch(&plugin.join("files").join("settings.json"), 1_000_000_000);
    touch(&plugin.join("data"), 2_000_000_000);

    migrate(&paths, now(), &utc()).unwrap();

    assert_eq!(read(&plugin.join("data").join("settings.json")), "mine");
    assert_eq!(read(&plugin.join("data.old")), "a stray file");
    assert!(!plugin.join("files").exists());
}

#[test]
fn a_client_that_cannot_move_the_files_says_it_needs_the_new_launcher_at_once() {
    let (_dir, paths) = root("layout-client-busy");
    old_root(&paths);
    // An older launcher's session holds the install.
    let older = InstallSessionLease::try_acquire_shared(&paths)
        .unwrap()
        .unwrap();
    let started = std::time::Instant::now();
    let error = layout::migrate_waiting(&paths, Duration::from_secs(10)).unwrap_err();
    assert!(
        started.elapsed() < Duration::from_secs(2),
        "the client waited {:?}",
        started.elapsed()
    );
    assert!(
        error.starts_with(
            "This client needs the new launcher. Restart the launcher to finish the update."
        ),
        "{error}"
    );
    assert!(error.contains("in use"), "{error}");
    drop(older);
}

#[test]
fn a_client_waits_while_another_program_moves_the_files() {
    let (_dir, paths) = root("layout-client-waits");
    old_root(&paths);
    // Another program is moving the files: it holds the layout lock.
    let lock = std::fs::OpenOptions::new()
        .create(true)
        .truncate(false)
        .write(true)
        .open(paths.layout_lock_file())
        .unwrap();
    lock.lock().unwrap();
    assert!(matches!(
        migrate(&paths, now(), &utc()),
        Err(MigrationError::Moving)
    ));
    let finisher = {
        let paths = paths.clone();
        std::thread::spawn(move || {
            std::thread::sleep(Duration::from_millis(300));
            write(&paths.layout_marker_file(), "{ \"version\": 2 }");
            drop(lock);
        })
    };
    let migration = layout::migrate_waiting(&paths, Duration::from_secs(10)).unwrap();
    assert!(!migration.moved);
    finisher.join().unwrap();
}

#[test]
fn a_failed_step_says_what_went_wrong() {
    let (_dir, paths) = root("layout-messages");
    let path = paths.root_directory().join("data");
    let failed = |error: std::io::Error| MigrationError::Failed {
        path: path.clone(),
        error,
        notes: Vec::new(),
    };
    let denied = failed(std::io::Error::new(
        std::io::ErrorKind::PermissionDenied,
        "access denied by policy",
    ))
    .message(&paths);
    assert!(denied.contains("access denied by policy"), "{denied}");
    assert!(denied.contains(&path.display().to_string()), "{denied}");
    assert!(!denied.contains("is in use"), "{denied}");
    let busy = failed(std::io::Error::from(std::io::ErrorKind::ResourceBusy)).message(&paths);
    assert!(
        busy.contains("is in use. Close all OpenAC clients"),
        "{busy}"
    );
    #[cfg(windows)]
    {
        // A file another program holds open.
        let sharing = failed(std::io::Error::from_raw_os_error(32)).message(&paths);
        assert!(sharing.contains("is in use"), "{sharing}");
    }
    assert!(
        MigrationError::Busy
            .message(&paths)
            .contains("is in use. Close all OpenAC clients")
    );
}

#[test]
fn linked_plugin_and_session_folders_are_moved_through_the_link() {
    let (_dir, paths) = root("layout-links");
    let elsewhere = TempDir::new("layout-links-target");
    let target = elsewhere.path.join("mosstank-folder");
    write(&target.join("files").join("settings.json"), "mine");
    std::fs::create_dir_all(paths.plugins_directory()).unwrap();
    let plugin = paths.plugins_directory().join("acdream.mosstank");
    if !link_folder(&plugin, &target) {
        eprintln!("skipped: this platform would not make a folder link");
        return;
    }
    let session_target = elsewhere.path.join("old-session");
    write(
        &session_target.join("session.json"),
        &old_session_document("10.0.0.5", 9000, "player"),
    );
    std::fs::create_dir_all(paths.launcher_log_directory()).unwrap();
    let old_session = paths
        .launcher_log_directory()
        .join(old_session_name(NOW_MS - 60_000, 'a'));
    assert!(link_folder(&old_session, &session_target));

    migrate(&paths, now(), &utc()).unwrap();

    // The plugin's files were renamed inside the linked folder.
    assert_eq!(read(&target.join("data").join("settings.json")), "mine");
    assert!(!target.join("files").exists());
    // The linked session folder was filed like any other.
    assert!(std::fs::symlink_metadata(&old_session).is_err());
    assert!(
        paths
            .session_logs_directory("10.0.0.5", "player")
            .join("2026-11-02 11-59-00")
            .join("session.json")
            .is_file()
    );
}

#[test]
fn the_cleanup_never_deletes_through_a_link() {
    let (_dir, paths) = root("layout-cleanup-links");
    let elsewhere = TempDir::new("layout-cleanup-links-target");
    let zone = utc();
    let day = 86_400_000;
    let at = |ms: u64| UNIX_EPOCH + Duration::from_millis(ms);
    // An old session folder that is a link to a folder elsewhere.
    let session_target = elsewhere.path.join("kept-session");
    write(&session_target.join("client.log"), "keep");
    let character = paths.session_logs_directory("S", "C");
    std::fs::create_dir_all(&character).unwrap();
    let old_name = session_folder_name(i64::try_from(NOW_MS - 9 * day).unwrap());
    if !link_folder(&character.join(&old_name), &session_target) {
        eprintln!("skipped: this platform would not make a folder link");
        return;
    }
    // A server folder that is a link to a folder holding an old session.
    let other = PathSet::for_root(&elsewhere.path.join("other-root"), RootSource::Explicit);
    let kept = create_session_folder(&other, "x", "y", at(NOW_MS - 9 * day), &zone).unwrap();
    write(&kept.join("client.log"), "keep");
    assert!(link_folder(
        &paths.logs_directory().join("Linked"),
        &other.logs_directory().join("x")
    ));

    let failures = clean_old_logs(&paths, now(), &zone);
    assert!(failures.is_empty(), "{failures:?}");

    // The old linked session went, as a link: what it pointed at stays.
    assert!(std::fs::symlink_metadata(character.join(&old_name)).is_err());
    assert_eq!(read(&session_target.join("client.log")), "keep");
    // Nothing behind the linked server folder was touched.
    assert_eq!(read(&kept.join("client.log")), "keep");
}

#[test]
fn a_leftover_partial_copy_is_cleared_and_the_original_moved() {
    let (_dir, paths) = root("layout-partial-copy");
    let root = paths.root_directory().to_path_buf();
    // A run copying to another volume stopped half way through a.met: the
    // part it wrote sits beside the destination, newer than the original.
    let original = root.join("vtank").join("a.met");
    write(&original, "the good original");
    touch(&original, 1_000_000_000);
    let partial = paths.vtank_profiles_directory().join("a.met.copying");
    write(&partial, "the go");
    touch(&partial, 2_000_000_000);

    migrate(&paths, now(), &utc()).unwrap();

    let vtank = paths.vtank_profiles_directory();
    assert_eq!(read(&vtank.join("a.met")), "the good original");
    assert!(!partial.exists());
    assert!(!vtank.join("a.old.met").exists());
    assert!(!vtank.join("a.met.old.copying").exists());
}

#[test]
fn an_identical_file_already_there_is_not_kept_twice() {
    let (_dir, paths) = root("layout-identical");
    let root = paths.root_directory().to_path_buf();
    write(&root.join("vtank").join("a.met"), "same");
    touch(&root.join("vtank").join("a.met"), 2_000_000_000);
    write(&paths.vtank_profiles_directory().join("a.met"), "same");
    touch(
        &paths.vtank_profiles_directory().join("a.met"),
        1_000_000_000,
    );

    let migration = migrate(&paths, now(), &utc()).unwrap();

    let vtank = paths.vtank_profiles_directory();
    assert_eq!(read(&vtank.join("a.met")), "same");
    assert!(!vtank.join("a.old.met").exists());
    assert!(!root.join("vtank").exists());
    assert!(
        !migration
            .notes
            .iter()
            .any(|note| note.contains("were both there")),
        "{:?}",
        migration.notes
    );
}

#[test]
fn a_link_to_the_destination_itself_is_not_moved_onto_itself() {
    let (_dir, paths) = root("layout-same-folder");
    let vtank = paths.vtank_profiles_directory();
    write(&vtank.join("a.met"), "meta");
    // The old place is a link to the new one.
    if !link_folder(&paths.root_directory().join("vtank"), &vtank) {
        eprintln!("skipped: this platform would not make a folder link");
        return;
    }

    migrate(&paths, now(), &utc()).unwrap();

    assert_eq!(read(&vtank.join("a.met")), "meta");
    assert!(!vtank.join("a.old.met").exists());
    assert!(layout::is_current(&paths));
}

#[test]
fn a_link_back_into_the_folder_being_merged_is_not_followed_forever() {
    let (_dir, paths) = root("layout-merge-cycle");
    let root = paths.root_directory().to_path_buf();
    let old = root.join("vtank");
    write(&old.join("sub").join("a.met"), "meta");
    if !link_folder(&old.join("sub").join("loop"), &old) {
        eprintln!("skipped: this platform would not make a folder link");
        return;
    }
    // The destination already has the same folders, so the move merges
    // into them, link included.
    std::fs::create_dir_all(paths.vtank_profiles_directory().join("sub").join("loop")).unwrap();

    migrate(&paths, now(), &utc()).unwrap();

    assert_eq!(
        read(&paths.vtank_profiles_directory().join("sub").join("a.met")),
        "meta"
    );
    assert!(layout::is_current(&paths));
}
